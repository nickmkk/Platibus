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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Transactions;
using Common.Logging;
using Platibus.Security;
using Platibus.SQL.Commands;

namespace Platibus.SQL
{
    /// <summary>
    /// A message queue based on a SQL database
    /// </summary>
    public class SQLMessageQueue : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(LoggingCategories.SQL);

        private readonly IDbConnectionProvider _connectionProvider;
        private readonly IMessageQueueingCommandBuilders _commandBuilders;
        private readonly QueueName _queueName;
        private readonly IQueueListener _listener;
        private readonly ISecurityTokenService _securityTokenService;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly bool _autoAcknowledge;
        private readonly int _maxAttempts;
        private readonly ActionBlock<SQLQueuedMessage> _queuedMessages;
        private readonly TimeSpan _retryDelay;

        private bool _disposed;
        private int _initialized;

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
        /// generating database commands for manipulating queued messages that conform to the SQL
        /// syntax required by the underlying connection provider</param>
        /// <param name="queueName">The name of the queue</param>
        /// <param name="listener">The object that will be notified when messages are
        /// added to the queue</param>
        /// <param name="securityTokenService"></param>
        /// <param name="options">(Optional) Settings that influence how the queue behaves</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="connectionProvider"/>,
        /// <paramref name="commandBuilders"/>, <paramref name="queueName"/>, or <paramref name="listener"/>
        /// are <c>null</c></exception>
        public SQLMessageQueue(IDbConnectionProvider connectionProvider, IMessageQueueingCommandBuilders commandBuilders, QueueName queueName,
            IQueueListener listener, ISecurityTokenService securityTokenService, QueueOptions options = null)
        {
            if (connectionProvider == null) throw new ArgumentNullException("connectionProvider");
            if (commandBuilders == null) throw new ArgumentNullException("commandBuilders");
            if (queueName == null) throw new ArgumentNullException("queueName");
            if (listener == null) throw new ArgumentNullException("listener");
            if (securityTokenService == null) throw new ArgumentNullException("securityTokenService");

            _connectionProvider = connectionProvider;
            _commandBuilders = commandBuilders;
            _queueName = queueName;
            _listener = listener;
            _securityTokenService = securityTokenService;

            var myOptions = options ?? new QueueOptions();

            _autoAcknowledge = myOptions.AutoAcknowledge;
            _maxAttempts = myOptions.MaxAttempts;
            _retryDelay = myOptions.RetryDelay;

            var concurrencyLimit = myOptions.ConcurrencyLimit;
            _cancellationTokenSource = new CancellationTokenSource();
            _queuedMessages = new ActionBlock<SQLQueuedMessage>(async msg =>
                await ProcessQueuedMessage(msg, _cancellationTokenSource.Token),
                new ExecutionDataflowBlockOptions
                {
                    CancellationToken = _cancellationTokenSource.Token,
                    MaxDegreeOfParallelism = concurrencyLimit
                });
        }

        /// <summary>
        /// Reads previously queued messages from the database and initiates message processing
        /// </summary>
        /// <returns>Returns a task that completes when initialization is complete</returns>
        public virtual async Task Init()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                await EnqueueExistingMessages();
            }
        }

        /// <summary>
        /// Adds a message to the queue
        /// </summary>
        /// <param name="message">The message to add</param>
        /// <param name="senderPrincipal">The principal that sent the message or from whom
        /// the message was received</param>
        /// <returns>Returns a task that completes when the message has been added to the queue</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="message"/> is
        /// <c>null</c></exception>
        /// <exception cref="ObjectDisposedException">Thrown if this SQL message queue instance
        /// has already been disposed</exception>
        public async Task Enqueue(Message message, IPrincipal senderPrincipal)
        {
            if (message == null) throw new ArgumentNullException("message");
            CheckDisposed();

            var queuedMessage = await InsertQueuedMessage(message, senderPrincipal);

            await _queuedMessages.SendAsync(queuedMessage);
            // TODO: handle accepted == false
        }

        /// <summary>
        /// Called by the message processing loop to process an individual message
        /// </summary>
        /// <param name="queuedMessage">The queued message to process</param>
        /// <param name="cancellationToken">A cancellation token that can be used by the caller
        /// to cancel message processing</param>
        /// <returns>Returns a task that completes when the queued message is processed</returns>
        protected async Task ProcessQueuedMessage(SQLQueuedMessage queuedMessage, CancellationToken cancellationToken)
        {
            var messageId = queuedMessage.Message.Headers.MessageId;
            var attemptCount = queuedMessage.Attempts;
            var abandoned = false;
            var message = queuedMessage.Message;
            var principal = queuedMessage.Principal;
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            while (!abandoned && attemptCount < _maxAttempts)
            {
                attemptCount++;

                Log.DebugFormat("Processing queued message {0} (attempt {1} of {2})...", messageId, attemptCount,
                    _maxAttempts);

                var context = new SQLQueuedMessageContext(message.Headers, principal);
                Thread.CurrentPrincipal = context.Principal;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _listener.MessageReceived(message, context, cancellationToken);
                    if (_autoAcknowledge && !context.Acknowledged)
                    {
                        await context.Acknowledge();
                    }
                }
                catch (Exception ex)
                {
                    Log.WarnFormat("Unhandled exception handling queued message {0}", ex, messageId);
                }

                if (context.Acknowledged)
                {
                    Log.DebugFormat("Message acknowledged.  Marking message {0} as acknowledged...", messageId);
                    await UpdateQueuedMessage(queuedMessage, DateTime.UtcNow, null, attemptCount);
                    Log.DebugFormat("Message {0} acknowledged successfully", messageId);
                    return;
                }

                if (attemptCount >= _maxAttempts)
                {
                    Log.WarnFormat("Maximum attempts to process message {0} exceeded", messageId);
                    abandoned = true;
                }

                if (abandoned)
                {
                    await UpdateQueuedMessage(queuedMessage, null, DateTime.UtcNow, attemptCount);
                    return;
                }

                await UpdateQueuedMessage(queuedMessage, null, null, attemptCount);

                Log.DebugFormat("Message not acknowledged.  Retrying in {0}...", _retryDelay);
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        /// <summary>
        /// Inserts a message into the SQL database
        /// </summary>
        /// <param name="message">The message to enqueue</param>
        /// <param name="principal">The principal that sent the message or from whom the
        /// message was received</param>
        /// <returns>Returns a task that completes when the message has been inserted into the SQL
        /// database and whose result is a copy of the inserted record</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual async Task<SQLQueuedMessage> InsertQueuedMessage(Message message, IPrincipal principal)
        {
            SQLQueuedMessage queuedMessage;
            var expires = message.Headers.Expires;
            var connection = _connectionProvider.GetConnection();
            var securityToken = await _securityTokenService.NullSafeIssue(principal, expires);
            var messageWithSecurityToken = message.WithSecurityToken(securityToken);
            try
            {
                var headers = messageWithSecurityToken.Headers;
                var commandBuilder = _commandBuilders.NewInsertQueuedMessageCommandBuilder();
                commandBuilder.MessageId = headers.MessageId;
                commandBuilder.QueueName = _queueName;
                commandBuilder.MessageId = headers.MessageId;
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
                        await command.ExecuteNonQueryAsync();
                    }
                    scope.Complete();
                }
                queuedMessage = new SQLQueuedMessage(messageWithSecurityToken, principal);
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }

            return queuedMessage;
        }
        
        /// <summary>
        /// Selects all queued messages from the SQL database
        /// </summary>
        /// <returns>Returns a task that completes when all records have been selected and whose
        /// result is the enumerable sequence of the selected records</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual async Task<IEnumerable<SQLQueuedMessage>> SelectQueuedMessages()
        {
            var queuedMessages = new List<SQLQueuedMessage>();
            var connection = _connectionProvider.GetConnection();
            try
            {
                var commandBuilder = _commandBuilders.NewSelectQueuedMessagesCommandBuilder();
                commandBuilder.QueueName = _queueName;
                
                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var record = commandBuilder.BuildQueuedMessageRecord(reader);
                                var messageContent = record.Content;
                                var headers = DeserializeHeaders(record.Headers);
                                var message = new Message(headers, messageContent);
#pragma warning disable 612
                                var principal = await ResolvePrincipal(headers, record.SenderPrincipal);
#pragma warning restore 612
                                var attempts = record.Attempts;
                                var queuedMessage = new SQLQueuedMessage(message, principal, attempts);
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
        /// Selects all abandoned messages from the SQL database
        /// </summary>
        /// <returns>Returns a task that completes when all records have been selected and whose
        /// result is the enumerable sequence of the selected records</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual async Task<IEnumerable<SQLQueuedMessage>> SelectAbandonedMessages(DateTime startDate, DateTime endDate)
        {
            var queuedMessages = new List<SQLQueuedMessage>();
            var connection = _connectionProvider.GetConnection();
            try
            {
                var commandBuilder = _commandBuilders.NewSelectAbandonedMessagesCommandBuilder();
                commandBuilder.QueueName = _queueName;
                commandBuilder.StartDate = startDate;
                commandBuilder.EndDate = endDate;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var record = commandBuilder.BuildQueuedMessageRecord(reader);
                                var messageContent = record.Content;
                                var headers = DeserializeHeaders(record.Headers);
                                var message = new Message(headers, messageContent);
#pragma warning disable 612
                                var principal = await ResolvePrincipal(headers, record.SenderPrincipal);
#pragma warning restore 612
                                var attempts = record.Attempts;
                                var queuedMessage = new SQLQueuedMessage(message, principal, attempts);
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
        /// Updates an existing queued message in the SQL database
        /// </summary>
        /// <param name="queuedMessage">The queued message to update</param>
        /// <param name="acknowledged">The date and time the message was acknowledged, if applicable</param>
        /// <param name="abandoned">The date and time the message was abandoned, if applicable</param>
        /// <param name="attempts">The number of attempts to process the message so far</param>
        /// <returns>Returns a task that completes when the record has been updated</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual async Task UpdateQueuedMessage(SQLQueuedMessage queuedMessage, DateTime? acknowledged,
            DateTime? abandoned, int attempts)
        {
            var connection = _connectionProvider.GetConnection();
            try
            {
                var message = queuedMessage.Message;
                var headers = message.Headers;
                var commandBuilder = _commandBuilders.NewUpdateQueuedMessageCommandBuilder();
                commandBuilder.MessageId = headers.MessageId;
                commandBuilder.QueueName = _queueName;
                commandBuilder.Acknowledged = acknowledged;
                commandBuilder.Abandoned = abandoned;
                commandBuilder.Attempts = attempts;

                using (var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
                {
                    using (var command = commandBuilder.BuildDbCommand(connection))
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
        }

        private async Task EnqueueExistingMessages()
        {
            var queuedMessages = await SelectQueuedMessages();
            foreach (var queuedMessage in queuedMessages)
            {
                Log.DebugFormat("Enqueueing existing message ID {0}...", queuedMessage.Message.Headers.MessageId);
                await _queuedMessages.SendAsync(queuedMessage);
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
        protected virtual IMessageHeaders DeserializeHeaders(string headerString)
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

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> if this object has been disposed
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if this object has been disposed</exception>
        protected virtual void CheckDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        /// <summary>
        /// Finalizer to ensure that all resources are released
        /// </summary>
        ~SQLMessageQueue()
        {
            Dispose(false);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            if (_disposed) return;
            Dispose(true);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Called by the <see cref="Dispose()"/> method or by the finalizer to free held resources
        /// </summary>
        /// <param name="disposing">Indicates whether this method is called from the 
        /// <see cref="Dispose()"/> method (<c>true</c>) or from the finalizer (<c>false</c>)</param>
        [SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_cancellationTokenSource")]
        protected virtual void Dispose(bool disposing)
        {
            _cancellationTokenSource.Cancel();
            if (disposing)
            {
                _cancellationTokenSource.TryDispose();
            }
        }
    }
}