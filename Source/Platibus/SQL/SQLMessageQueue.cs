﻿// The MIT License (MIT)
// 
// Copyright (c) 2016 Jesse Sweetland
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Platibus.Diagnostics;
using Platibus.Queueing;
using Platibus.Security;
using Platibus.SQL.Commands;

namespace Platibus.SQL
{
    /// <summary>
    /// A message queue based on a SQL database
    /// </summary>
    public class SQLMessageQueue : AbstractMessageQueue
    {
        private readonly IDbConnectionProvider _connectionProvider;
        private readonly IMessageQueueingCommandBuilders _commandBuilders;
        private readonly ISecurityTokenService _securityTokenService;
        
        /// <summary>
        /// The connection provider
        /// </summary>
        public IDbConnectionProvider ConnectionProvider
        {
            get { return _connectionProvider; }
        }

        /// <summary>
        /// A collection of factories capable of  generating database commands for manipulating 
        /// queued messages that conform to the SQL syntax required by the underlying connection 
        /// provider
        /// </summary>
        public IMessageQueueingCommandBuilders CommandBuilders
        {
            get { return _commandBuilders; }
        }

        /// <summary>
        /// Initializes a new <see cref="SQLMessageQueue"/> with the specified values
        /// </summary>
        /// <param name="connectionProvider">The database connection provider</param>
        /// <param name="commandBuilders">A collection of factories capable of 
        ///     generating database commands for manipulating queued messages that conform to the SQL
        ///     syntax required by the underlying connection provider</param>
        /// <param name="queueName">The name of the queue</param>
        /// <param name="listener">The object that will be notified when messages are
        ///     added to the queue</param>
        /// <param name="securityTokenService">(Optional) The message security token
        ///     service to use to issue and validate security tokens for persisted messages.</param>
        /// <param name="options">(Optional) Settings that influence how the queue behaves</param>
        /// <param name="diagnosticService">(Optional) The service through which diagnostic events
        ///     are reported and processed</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="connectionProvider"/>,
        /// <paramref name="commandBuilders"/>, <paramref name="queueName"/>, or <paramref name="listener"/>
        /// are <c>null</c></exception>
        public SQLMessageQueue(IDbConnectionProvider connectionProvider, 
            IMessageQueueingCommandBuilders commandBuilders, QueueName queueName, 
            IQueueListener listener, ISecurityTokenService securityTokenService, 
            QueueOptions options = null, IDiagnosticService diagnosticService = null)
            : base(queueName, listener, options, diagnosticService)
        {
            if (connectionProvider == null) throw new ArgumentNullException("connectionProvider");
            if (commandBuilders == null) throw new ArgumentNullException("commandBuilders");
            if (queueName == null) throw new ArgumentNullException("queueName");
            if (listener == null) throw new ArgumentNullException("listener");
            if (securityTokenService == null) throw new ArgumentNullException("securityTokenService");

            _connectionProvider = connectionProvider;
            _commandBuilders = commandBuilders;
            _securityTokenService = securityTokenService;

            MessageEnqueued += OnMessageEnqueued;
            MessageAcknowledged += OnMessageAcknowledged;
            AcknowledgementFailure += OnAcknowledgementFailure;
            MaximumAttemptsExceeded += OnMaximumAttemptsExceeded;
        }

        private Task OnMessageEnqueued(object source, MessageQueueEventArgs args)
        {
            return InsertQueuedMessage(args.QueuedMessage);
        }

        private Task OnMessageAcknowledged(object source, MessageQueueEventArgs args)
        {
            return DeleteQueuedMessage(args.QueuedMessage);
        }

        private Task OnAcknowledgementFailure(object source, MessageQueueEventArgs args)
        {
            return UpdateQueuedMessage(args.QueuedMessage, null);
        }

        private async Task OnMaximumAttemptsExceeded(object source, MessageQueueEventArgs args)
        {
            await UpdateQueuedMessage(args.QueuedMessage, DateTime.UtcNow);
            await DiagnosticService.EmitAsync(
                new SQLEventBuilder(this, DiagnosticEventType.DeadLetter)
                {
                    Detail = "Message abandoned",
                    Message = args.QueuedMessage.Message,
                    Queue = QueueName
                }.Build());
        }

        /// <inheritdoc />
        protected override async Task<IEnumerable<QueuedMessage>> GetPendingMessages(CancellationToken cancellationToken = default(CancellationToken))
        {
            var queuedMessages = new List<QueuedMessage>();
            var connection = _connectionProvider.GetConnection();
            try
            {
                var commandBuilder = _commandBuilders.NewSelectPendingMessagesCommandBuilder();
                commandBuilder.QueueName = QueueName;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                        {
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                var record = commandBuilder.BuildQueuedMessageRecord(reader);
                                var messageContent = record.Content;
                                var headers = DeserializeHeaders(record.Headers);
#pragma warning disable 612
                                var principal = await ResolvePrincipal(headers, record.SenderPrincipal);
#pragma warning restore 612
                                var message = new Message(headers, messageContent).WithoutSecurityToken();
                                var attempts = record.Attempts;
                                var queuedMessage = new QueuedMessage(message, principal, attempts);
                                queuedMessages.Add(queuedMessage);
                            }
                        }
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }

            // SQL calls are not async to avoid the need for TransactionAsyncFlowOption
            // and dependency on .NET 4.5.1 and later
            return queuedMessages.AsEnumerable();
        }

        /// <summary>
        /// Inserts a new queued message into the database
        /// </summary>
        /// <param name="queuedMessage">The queued message to insert</param>
        /// <param name="cancellationToken">(Optional) A cancellation token through which the
        /// caller can request cancellation of the insert operation</param>
        /// <returns>Returns a task that completes when the insert operation completes</returns>
        protected virtual async Task InsertQueuedMessage(QueuedMessage queuedMessage, CancellationToken cancellationToken = default(CancellationToken))
        {
            var message = queuedMessage.Message;
            var principal = queuedMessage.Principal;
            var expires = message.Headers.Expires;
            var connection = _connectionProvider.GetConnection();
            var securityToken = await _securityTokenService.NullSafeIssue(principal, expires);
            var messageWithSecurityToken = message.WithSecurityToken(securityToken);
            try
            {
                var headers = messageWithSecurityToken.Headers;
                var commandBuilder = _commandBuilders.NewInsertQueuedMessageCommandBuilder();
                commandBuilder.MessageId = headers.MessageId;
                commandBuilder.QueueName = QueueName;
                commandBuilder.Origination = headers.Origination == null ? null : headers.Origination.ToString();
                commandBuilder.Destination = headers.Destination == null ? null : headers.Destination.ToString();
                commandBuilder.ReplyTo = headers.ReplyTo == null ? null : headers.ReplyTo.ToString();
                commandBuilder.Expires = headers.Expires;
                commandBuilder.ContentType = headers.ContentType;
                commandBuilder.Headers = SerializeHeaders(headers);
                commandBuilder.Content = messageWithSecurityToken.Content;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
        }

        /// <summary>
        /// Deletes a queued message from the database i.e. in response to message acknowledgement
        /// </summary>
        /// <param name="queuedMessage">The queued message to delete</param>
        /// <param name="cancellationToken">(Optional) A cancellation token through which the
        /// caller can request cancellation of the delete operation</param>
        /// <returns>Returns a task that completes when the delete operation completes</returns>
        protected virtual async Task DeleteQueuedMessage(QueuedMessage queuedMessage, CancellationToken cancellationToken = default(CancellationToken))
        {
            var connection = _connectionProvider.GetConnection();
            try
            {
                var message = queuedMessage.Message;
                var headers = message.Headers;
                var commandBuilder = _commandBuilders.NewDeleteQueuedMessageCommandBuilder();
                commandBuilder.MessageId = headers.MessageId;
                commandBuilder.QueueName = QueueName;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
        }
        
        /// <summary>
        /// Selects all dead messages from the SQL database
        /// </summary>
        /// <returns>Returns a task that completes when all records have been selected and whose
        /// result is the enumerable sequence of the selected records</returns>
        protected virtual async Task<IEnumerable<QueuedMessage>> GetDeadMessages(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = new CancellationToken())
        {
            var queuedMessages = new List<QueuedMessage>();
            var connection = _connectionProvider.GetConnection();
            try
            {
                var commandBuilder = _commandBuilders.NewSelectDeadMessagesCommandBuilder();
                commandBuilder.QueueName = QueueName;
                commandBuilder.StartDate = startDate;
                commandBuilder.EndDate = endDate;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                        {
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                var record = commandBuilder.BuildQueuedMessageRecord(reader);
                                var messageContent = record.Content;
                                var headers = DeserializeHeaders(record.Headers);
                                var message = new Message(headers, messageContent);
#pragma warning disable 612
                                var principal = await ResolvePrincipal(headers, record.SenderPrincipal);
#pragma warning restore 612
                                var attempts = record.Attempts;
                                var queuedMessage = new QueuedMessage(message, principal, attempts);
                                queuedMessages.Add(queuedMessage);
                            }
                        }
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
            return queuedMessages.AsEnumerable();
        }

        /// <summary>
        /// Updates a queued message in the database i.e. in response to an acknowledgement failure
        /// </summary>
        /// <param name="queuedMessage">The queued message to delete</param>
        /// <param name="abandoned">The date/time the message was abandoned (if applicable)</param>
        /// <param name="cancellationToken">(Optional) A cancellation token through which the
        ///     caller can request cancellation of the update operation</param>
        /// <returns>Returns a task that completes when the update operation completes</returns>
        protected virtual async Task UpdateQueuedMessage(QueuedMessage queuedMessage, DateTime? abandoned, CancellationToken cancellationToken = default(CancellationToken))
        {
            var connection = _connectionProvider.GetConnection();
            try
            {
                var message = queuedMessage.Message;
                var headers = message.Headers;
                var commandBuilder = _commandBuilders.NewUpdateQueuedMessageCommandBuilder();
                commandBuilder.MessageId = headers.MessageId;
                commandBuilder.QueueName = QueueName;
                commandBuilder.Abandoned = abandoned;
                commandBuilder.Attempts = queuedMessage.Attempts;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
        }
        
        /// <summary>
        /// A helper method to serialize message headers so that they can be inserted into a
        /// single column in the SQL database
        /// </summary>
        /// <param name="headers">The message headers to serialize</param>
        /// <returns>Returns the serialized message headers</returns>
        protected virtual string SerializeHeaders(IMessageHeaders headers)
        {
            if (headers == null) return null;

            using (var writer = new StringWriter())
            {
                foreach (var header in headers)
                {
                    var headerName = header.Key;
                    var headerValue = header.Value;
                    writer.Write("{0}: ", headerName);
                    using (var headerValueReader = new StringReader(headerValue))
                    {
                        var multilineContinuation = false;
                        string line;
                        while ((line = headerValueReader.ReadLine()) != null)
                        {
                            if (multilineContinuation)
                            {
                                // Prefix continuation with whitespace so that subsequent
                                // lines are not confused with different headers.
                                line = "    " + line;
                            }
                            writer.WriteLine(line);
                            multilineContinuation = true;
                        }
                    }
                }
                return writer.ToString();
            }
        }

#pragma warning disable 618
        /// <summary>
        /// Determines the principal stored with the queued message
        /// </summary>
        /// <param name="headers">The message headers</param>
        /// <param name="senderPrincipal">The serialized sender principal</param>
        /// <returns></returns>
        /// <remarks>
        /// <para>This method first looks for a security token stored in the 
        /// <see cref="IMessageHeaders.SecurityToken"/> header.  If present, the 
        /// <see cref="ISecurityTokenService"/> provided in the constructor is used to validate it
        /// and reconstruct the <see cref="IPrincipal"/>.</para>
        /// <para>If a security token is not present in the message headers then the legacy
        /// SenderPrincipal column is read.  If the value of the SenderPrincipal is not <c>null</c>
        /// then it will be deserialized into a <see cref="SenderPrincipal"/>.
        /// </para>
        /// </remarks>
        protected async Task<IPrincipal> ResolvePrincipal(IMessageHeaders headers, string senderPrincipal)
        {
            if (!string.IsNullOrWhiteSpace(headers.SecurityToken))
            {
                return await _securityTokenService.Validate(headers.SecurityToken);
            }

            try
            {
                return string.IsNullOrWhiteSpace(senderPrincipal)
                    ? null 
                    : DeserializePrincipal(senderPrincipal);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }
        
        /// <summary>
        /// Helper method to deserialize the sender principal from a record in the SQL database
        /// </summary>
        /// <param name="str">The serialized principal string</param>
        /// <returns>Returns the deserialized principal</returns>
        [Obsolete("For backward compatibility only")]
        protected virtual IPrincipal DeserializePrincipal(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return null;

            var bytes = Convert.FromBase64String(str);
            using (var memoryStream = new MemoryStream(bytes))
            {
                var formatter = new BinaryFormatter();
                return (SenderPrincipal)formatter.Deserialize(memoryStream);
            }
        }
#pragma warning restore 618

        /// <summary>
        /// Helper method to deserialize message headers read from a record in the SQL database
        /// </summary>
        /// <param name="headerString">The serialized header string</param>
        /// <returns>Returns the deserialized headers</returns>
        /// <remarks>
        /// This method performs the inverse of <see cref="SerializeHeaders"/>
        /// </remarks>
        protected virtual MessageHeaders DeserializeHeaders(string headerString)
        {
            var headers = new MessageHeaders();
            if (string.IsNullOrWhiteSpace(headerString)) return headers;

            var currentHeaderName = (HeaderName)null;
            var currentHeaderValue = new StringWriter();
            var finishedReadingHeaders = false;
            var lineNumber = 0;

            using (var reader = new StringReader(headerString))
            {
                string currentLine;
                while (!finishedReadingHeaders && (currentLine = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(currentLine))
                    {
                        if (currentHeaderName != null)
                        {
                            headers[currentHeaderName] = currentHeaderValue.ToString();
                            currentHeaderName = null;
                            currentHeaderValue = new StringWriter();
                        }

                        finishedReadingHeaders = true;
                        continue;
                    }

                    if (currentLine.StartsWith(" ") && currentHeaderName != null)
                    {
                        // Continuation of previous header value
                        currentHeaderValue.WriteLine();
                        currentHeaderValue.Write(currentLine.Trim());
                        continue;
                    }

                    // New header.  Finish up with the header we were just working on.
                    if (currentHeaderName != null)
                    {
                        headers[currentHeaderName] = currentHeaderValue.ToString();
                        currentHeaderValue = new StringWriter();
                    }

                    if (currentLine.StartsWith("#"))
                    {
                        // Special line. Ignore.
                        continue;
                    }

                    var separatorPos = currentLine.IndexOf(':');
                    if (separatorPos < 0)
                    {
                        throw new FormatException(string.Format("Invalid header on line {0}:  Character ':' expected",
                            lineNumber));
                    }

                    if (separatorPos == 0)
                    {
                        throw new FormatException(
                            string.Format(
                                "Invalid header on line {0}:  Character ':' found at position 0 (missing header name)",
                                lineNumber));
                    }

                    currentHeaderName = currentLine.Substring(0, separatorPos);
                    currentHeaderValue.Write(currentLine.Substring(separatorPos + 1).Trim());
                }

                // Make sure we set the last header we were working on, if there is one
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString();
                }
            }
            return headers;
        }
    }
}