// The MIT License (MIT)
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
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Platibus.Filesystem;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Platibus.RabbitMQ
{
    /// <summary>
    /// A logical Platibus queue implemented with RabbitMQ queues and exchanges
    /// </summary>
    public class RabbitMQQueue : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(RabbitMQLoggingCategories.RabbitMQ);

        private readonly QueueName _queueName;
        private readonly string _queueExchange;
        private readonly string _deadLetterExchange;
        private readonly bool _autoAcknowledge;
        private readonly QueueName _retryQueueName;
        private readonly string _retryExchange;

        private readonly IQueueListener _listener;
        private readonly Encoding _encoding;

        private readonly TimeSpan _ttl;
        private readonly int _maxAttempts;
        private readonly TimeSpan _retryDelay;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly DurableConsumer[] _consumers;
        
        private readonly IConnection _connection;
        private bool _disposed;

        /// <summary>
        /// Initializes a new <see cref="RabbitMQQueue"/>
        /// </summary>
        /// <param name="queueName">The name of the queue</param>
        /// <param name="listener">The listener that will receive new messages off of the queue</param>
        /// <param name="connection">The connection to the RabbitMQ server</param>
        /// <param name="encoding">(Optional) The encoding to use when converting serialized message 
        /// content to byte streams</param>
        /// <param name="options">(Optional) Queueing options</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="queueName"/>, 
        /// <paramref name="listener"/>, or <paramref name="connection"/> is <c>null</c></exception>
        public RabbitMQQueue(QueueName queueName, IQueueListener listener, IConnection connection,
            Encoding encoding = null, QueueOptions options = default(QueueOptions))
        {
            if (queueName == null) throw new ArgumentNullException("queueName");
            if (listener == null) throw new ArgumentNullException("listener");
            if (connection == null) throw new ArgumentNullException("connection");

            _queueName = queueName;
            _queueExchange = _queueName.GetExchangeName();
            _retryQueueName = queueName.GetRetryQueueName();
            _retryExchange = _queueName.GetRetryExchangeName();
            _deadLetterExchange = _queueName.GetDeadLetterExchangeName();

            _listener = listener;
            _connection = connection;
            _encoding = encoding ?? Encoding.UTF8;
            _ttl = options.TTL;

            _autoAcknowledge = options.AutoAcknowledge;

            _maxAttempts = options.MaxAttempts <= 0
                ? QueueOptions.DefaultMaxAttempts
                : options.MaxAttempts;

            _retryDelay = options.RetryDelay <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(QueueOptions.DefaultRetryDelay)
                : options.RetryDelay;

            var concurrencyLimit = options.ConcurrencyLimit <= 0
                ? QueueOptions.DefaultConcurrencyLimit
                : options.ConcurrencyLimit;

            _cancellationTokenSource = new CancellationTokenSource();
            
            _consumers = new DurableConsumer[concurrencyLimit];
            for (var i = 0; i < _consumers.Length; i++)
            {
                var consumerTag = _queueName + "_" + i;
                _consumers[i] = new DurableConsumer(_connection, queueName, HandleDelivery, consumerTag, _autoAcknowledge);
            }
        }

        /// <summary>
        /// Initializes RabbitMQ queues and exchanges
        /// </summary>
        public void Init()
        {
            using (var channel = _connection.CreateModel())
            {
                var queueArgs = new Dictionary<string, object>
                {
                    {"x-dead-letter-exchange", _deadLetterExchange},
                };

                if (_ttl > TimeSpan.Zero)
                {
                    queueArgs["x-expires"] = _ttl;
                }

                channel.ExchangeDeclare(_queueExchange, "direct", true, false, null);
                channel.ExchangeDeclare(_deadLetterExchange, "direct", true, false, null);
                channel.QueueDeclare(_queueName, true, false, false, queueArgs);
                channel.QueueBind(_queueName, _queueExchange, "", null);

                var retryTtlMs = (int) _retryDelay.TotalMilliseconds;
                var retryQueueArgs = new Dictionary<string, object>
                {
                    {"x-dead-letter-exchange", _queueExchange},
                    {"x-message-ttl", retryTtlMs}
                };

                channel.ExchangeDeclare(_retryExchange, "direct", true, false, null);
                channel.QueueDeclare(_retryQueueName, true, false, false, retryQueueArgs);
                channel.QueueBind(_retryQueueName, _retryExchange, "", null);
            }

            foreach (var consumer in _consumers)
            {
                consumer.Init();
            }
        }

        /// <summary>
        /// Enqueues a message
        /// </summary>
        /// <param name="message">The message to enqueue</param>
        /// <param name="principal">The sender principal</param>
        /// <returns>Returns a task that completes when the message has been enqueued</returns>
        public Task Enqueue(Message message, IPrincipal principal)
        {
            CheckDisposed();
            return RabbitMQHelper.PublishMessage(message, principal, _connection, null, _queueExchange, _encoding);
        }
        
        /// <summary>
        /// Deletes the RabbitMQ queues and exchanges
        /// </summary>
        public void Delete()
        {
            CheckDisposed();
            Dispose(true);

            using (var channel = _connection.CreateModel())
            {
                channel.QueueDeleteNoWait(_queueName, false, false);
                channel.QueueDeleteNoWait(_retryQueueName, false, false);
                channel.ExchangeDeleteNoWait(_deadLetterExchange, false);
                channel.ExchangeDeleteNoWait(_queueExchange, false);
                channel.ExchangeDeleteNoWait(_retryExchange, false);
            }
        }

        private void HandleDelivery(IModel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
        {
            try
            {
                // Put on the thread pool to avoid deadlock
                var acknowleged = Task.Run(async () => await DispatchToListener(delivery, cancellationToken), cancellationToken).Result;
                if (acknowleged || _autoAcknowledge)
                {
                    Log.DebugFormat(
                        "Acknowledging message {0} from RabbitMQ queue \"{1}\" on channel {2}...",
                        delivery.DeliveryTag, _queueName, channel.ChannelNumber);

                    channel.BasicAck(delivery.DeliveryTag, false);
                }
                else
                {
                    Log.DebugFormat(
                        "Message {0} from RabbitMQ queue \"{1}\" received on channel {2} not acknowledged",
                        delivery.DeliveryTag, _queueName, channel.ChannelNumber);

                    // Add 1 to the header value because the number of attempts will be zero
                    // on the first try
                    var currentAttempt = delivery.BasicProperties.GetDeliveryAttempts() + 1;
                    if (currentAttempt < _maxAttempts)
                    {
                        Log.DebugFormat(
                            "Re-publishing message {0} to retry queue \"{1}\" for redelivery...",
                            delivery.DeliveryTag, _retryQueueName);

                        var retryProperties = delivery.BasicProperties;
                        retryProperties.IncrementDeliveryAttempts();
                        channel.BasicPublish(_retryExchange, "", retryProperties, delivery.Body);
                    }
                    else
                    {
                        Log.WarnFormat(
                            "Maximum delivery attempts for message {0} exceeded.  Sending NACK on channel {1}...",
                            delivery.DeliveryTag, channel.ChannelNumber);

                        channel.BasicNack(delivery.DeliveryTag, false, false);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorFormat("Error processing message {0} from RabbitMQ queue \"{1}\" on channel {2}", e,
                    delivery.DeliveryTag, _queueName, channel.ChannelNumber);

                // Due to the complexity of managing retry counts and delays in
                // RabbitMQ, retries are handled in the HandleDelivery method.
                // Therefore if we get to this point, we've done the best we
                // could.
                channel.BasicNack(delivery.DeliveryTag, false, false);
            }
        }

        private async Task<bool> DispatchToListener(BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
        {
            try
            {
                var messageBody = _encoding.GetString(delivery.Body);
                using (var reader = new StringReader(messageBody))
                using (var messageReader = new MessageReader(reader))
                {
                    var principal = await messageReader.ReadPrincipal();
                    var message = await messageReader.ReadMessage();

                    var context = new RabbitMQQueuedMessageContext(message.Headers, principal);
                    Thread.CurrentPrincipal = context.SenderPrincipal;
                    await _listener.MessageReceived(message, context, cancellationToken);
                    return context.Acknowledged;
                }
            }
            catch (MessageFileFormatException ex)
            {
                Log.ErrorFormat("Unable to read invalid or corrupt message", ex);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("Unhandled exception handling queued message", ex);
            }
            return false;
        }

        /// <summary>
        /// Throws an exception if this object has already been disposed
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if the object has been disposed</exception>
        protected void CheckDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        /// <summary>
        /// Finalizer that ensures all resources are released
        /// </summary>
        ~RabbitMQQueue()
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
        /// Called by the <see cref="Dispose()"/> method or finalizer to ensure that
        /// resources are released
        /// </summary>
        /// <param name="disposing">Indicates whether this method is called from the 
        /// <see cref="Dispose()"/> method (<c>true</c>) or the finalizer (<c>false</c>)</param>
        /// <remarks>
        /// This method will not be called more than once
        /// </remarks>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_cancellationTokenSource")]
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            _cancellationTokenSource.Cancel();
            if (disposing)
            {
                foreach (var consumer in _consumers)
                {
                    consumer.TryDispose();
                }
                _cancellationTokenSource.TryDispose();
            }
        }
    }
}