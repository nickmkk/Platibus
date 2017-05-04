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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Platibus.SQL
{
    /// <summary>
    /// A <see cref="ISubscriptionTrackingService"/> implementation that uses a SQL database to
    /// store queued messages
    /// </summary>
    public class SQLSubscriptionTrackingService : ISubscriptionTrackingService, IDisposable
    {
        private readonly IDbConnectionProvider _connectionProvider;
        private readonly ISQLDialect _dialect;

        private readonly ConcurrentDictionary<TopicName, IEnumerable<SQLSubscription>> _subscriptions =
            new ConcurrentDictionary<TopicName, IEnumerable<SQLSubscription>>();

        private bool _disposed;

        /// <summary>
        /// The connection provider used to obtain connections to the SQL database
        /// </summary>
        public IDbConnectionProvider ConnectionProvider
        {
            get { return _connectionProvider; }
        }

        /// <summary>
        /// The SQL dialect
        /// </summary>
        public ISQLDialect Dialect
        {
            get { return _dialect; }
        }

        /// <summary>
        /// Initializes a new <see cref="SQLSubscriptionTrackingService"/> with the specified connection
        /// string settings and dialect
        /// </summary>
        /// <param name="connectionStringSettings">The connection string settings to use to connect to
        /// the SQL database</param>
        /// <param name="dialect">(Optional) The SQL dialect to use</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="connectionStringSettings"/>
        /// is <c>null</c></exception>
        /// <remarks>
        /// If a SQL dialect is not specified, then one will be selected based on the supplied
        /// connection string settings
        /// </remarks>
        /// <seealso cref="DbExtensions.GetSQLDialect"/>
        /// <seealso cref="ISQLDialectProvider"/>
        public SQLSubscriptionTrackingService(ConnectionStringSettings connectionStringSettings,
            ISQLDialect dialect = null)
        {
            if (connectionStringSettings == null) throw new ArgumentNullException("connectionStringSettings");
            _connectionProvider = new DefaultConnectionProvider(connectionStringSettings);
            _dialect = dialect ?? connectionStringSettings.GetSQLDialect();
        }

        /// <summary>
        /// Initializes a new <see cref="SQLSubscriptionTrackingService"/> with the specified connection
        /// provider and dialect
        /// </summary>
        /// <param name="connectionProvider">The connection provider to use to connect to
        /// the SQL database</param>
        /// <param name="dialect">The SQL dialect to use</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="connectionProvider"/>
        /// or <paramref name="dialect"/> is <c>null</c></exception>
        public SQLSubscriptionTrackingService(IDbConnectionProvider connectionProvider, ISQLDialect dialect)
        {
            if (connectionProvider == null) throw new ArgumentNullException("connectionProvider");
            if (dialect == null) throw new ArgumentNullException("dialect");
            _connectionProvider = connectionProvider;
            _dialect = dialect;
        }

        /// <summary>
        /// Initializes the subscription tracking service by creating the necessary objects in the
        /// SQL database
        /// </summary>
        public void Init()
        {
            var connection = _connectionProvider.GetConnection();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandType = CommandType.Text;
                    command.CommandText = _dialect.CreateSubscriptionTrackingServiceObjectsCommand;
                    command.ExecuteNonQuery();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }

            var subscriptionsByTopicName = SelectSubscriptions()
                .GroupBy(subscription => subscription.TopicName);

            foreach (var grouping in subscriptionsByTopicName)
            {
                var topicName = grouping.Key;
                var subscriptions = grouping.ToList();
                _subscriptions.AddOrUpdate(topicName, subscriptions, (t, s) => s.Union(subscriptions).ToList());
            }
        }

        /// <summary>
        /// Adds or updates a subscription
        /// </summary>
        /// <param name="topic">The topic to which the <paramref name="subscriber"/> is
        /// subscribing</param>
        /// <param name="subscriber">The base URI of the subscribing Platibus instance</param>
        /// <param name="ttl">(Optional) The maximum Time To Live (TTL) for the subscription</param>
        /// <param name="cancellationToken">(Optional) A cancellation token that can be used by
        /// the caller to cancel the addition of the subscription</param>
        /// <returns>Returns a task that will complete when the subscription has been added or
        /// updated</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="topic"/> or
        /// <paramref name="subscriber"/> is <c>null</c></exception>
        /// <exception cref="ObjectDisposedException">Thrown if the object is already disposed</exception>
        public async Task AddSubscription(TopicName topic, Uri subscriber, TimeSpan ttl = default(TimeSpan),
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (topic == null) throw new ArgumentNullException("topic");
            if (subscriber == null) throw new ArgumentNullException("subscriber");

            CheckDisposed();
            var expires = ttl <= TimeSpan.Zero ? DateTime.MaxValue : (DateTime.UtcNow + ttl);
            cancellationToken.ThrowIfCancellationRequested();
            var subscription = await InsertOrUpdateSubscription(topic, subscriber, expires);
            _subscriptions.AddOrUpdate(topic, new[] {subscription},
                (t, existing) => new[] {subscription}.Union(existing).ToList());
        }

        /// <summary>
        /// Removes a subscription
        /// </summary>
        /// <param name="topic">The topic to which the <paramref name="subscriber"/> is
        /// subscribing</param>
        /// <param name="subscriber">The base URI of the subscribing Platibus instance</param>
        /// <param name="cancellationToken">(Optional) A cancellation token that can be used by
        /// the caller to cancel the subscription removal</param>
        /// <returns>Returns a task that will complete when the subscription has been removed</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="topic"/> or
        /// <paramref name="subscriber"/> is <c>null</c></exception>
        /// <exception cref="ObjectDisposedException">Thrown if the object is already disposed</exception>
        public async Task RemoveSubscription(TopicName topic, Uri subscriber,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (topic == null) throw new ArgumentNullException("topic");
            if (subscriber == null) throw new ArgumentNullException("subscriber");

            CheckDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteSubscription(topic, subscriber);

            _subscriptions.AddOrUpdate(topic, new SQLSubscription[0],
                (t, existing) => existing.Where(se => se.Subscriber != subscriber).ToList());
        }

        /// <summary>
        /// Returns a list of the current, non-expired subscriber URIs for a topic
        /// </summary>
        /// <param name="topic">The topic</param>
        /// <param name="cancellationToken">A cancellation token that can be used by the caller
        /// to cancel the query</param>
        /// <returns>Returns a task whose result is the distinct set of base URIs of all Platibus
        /// instances subscribed to the specified local topic</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="topic"/> is <c>null</c>
        /// </exception>
        public Task<IEnumerable<Uri>> GetSubscribers(TopicName topic,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (topic == null) throw new ArgumentNullException("topic");
            
            CheckDisposed();
            IEnumerable<SQLSubscription> subscriptions;
            _subscriptions.TryGetValue(topic, out subscriptions);
            var activeSubscribers = (subscriptions ?? Enumerable.Empty<SQLSubscription>())
                .Where(s => s.Expires > DateTime.UtcNow)
                .Select(s => s.Subscriber);

            return Task.FromResult(activeSubscribers);
        }

        /// <summary>
        /// Inserts or updates a subscription record in the SQL database
        /// </summary>
        /// <param name="topic">The topic to which the <paramref name="subscriber"/> is subscribing</param>
        /// <param name="subscriber">The base URI of the subscribing Platibus instance</param>
        /// <param name="expires">The date and time at which the subscription will expire</param>
        /// <returns>Returns a task that will complete when the subscription record has been inserted 
        /// or updated and whose result will be the an immutable representation of the inserted 
        /// subscription record</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual Task<SQLSubscription> InsertOrUpdateSubscription(TopicName topic, Uri subscriber,
            DateTime expires)
        {
            SQLSubscription subscription;
            var connection = _connectionProvider.GetConnection();
            try
            {
                using (var scope = new TransactionScope(TransactionScopeOption.Required))
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandType = CommandType.Text;
                        command.CommandText = _dialect.InsertSubscriptionCommand;

                        command.SetParameter(_dialect.TopicNameParameterName, (string) topic);
                        command.SetParameter(_dialect.SubscriberParameterName, subscriber.ToString());
                        command.SetParameter(_dialect.ExpiresParameterName, expires);

                        var rowsAffected = command.ExecuteNonQuery();
                        if (rowsAffected == 0)
                        {
                            command.CommandText = _dialect.UpdateSubscriptionCommand;
                            command.ExecuteNonQuery();
                        }
                    }
                    subscription = new SQLSubscription(topic, subscriber, expires);
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
            return Task.FromResult(subscription);
        }

        /// <summary>
        /// Selects all of the non-expired subscription records from the SQL database
        /// </summary>
        /// <returns>Returns a task that will complete when the subscription records have been
        /// selected and whose result will be the records that were selected</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual IEnumerable<SQLSubscription> SelectSubscriptions()
        {
            var subscriptions = new List<SQLSubscription>();
            var connection = _connectionProvider.GetConnection();
            try
            {
                using (var scope = new TransactionScope(TransactionScopeOption.Required))
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandType = CommandType.Text;
                        command.CommandText = _dialect.SelectSubscriptionsCommand;
                        command.SetParameter(_dialect.CurrentDateParameterName, DateTime.UtcNow);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var topicName = (TopicName) reader.GetString("TopicName");
                                var subscriber = new Uri(reader.GetString("Subscriber"));
                                var expires = reader.GetDateTime("Expires").GetValueOrDefault(DateTime.MaxValue);
                                var subscription = new SQLSubscription(topicName, subscriber, expires);
                                subscriptions.Add(subscription);
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
            return subscriptions;
        }

        /// <summary>
        /// Deletes a subscription record from the SQL database
        /// </summary>
        /// <param name="topic">The name of the topic</param>
        /// <param name="subscriber">The base URI of the subscribing Platibus instance</param>
        /// <returns>Returns a task that will complete when the subscription record
        /// has been deleted</returns>
        [SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        protected virtual Task DeleteSubscription(TopicName topic, Uri subscriber)
        {
            bool deleted;
            var connection = _connectionProvider.GetConnection();
            try
            {
                using (var scope = new TransactionScope(TransactionScopeOption.Required))
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandType = CommandType.Text;
                        command.CommandText = _dialect.DeleteSubscriptionCommand;

                        command.SetParameter(_dialect.TopicNameParameterName, (string) topic);
                        command.SetParameter(_dialect.SubscriberParameterName, subscriber.ToString());

                        var rowsAffected = command.ExecuteNonQuery();
                        deleted = rowsAffected > 0;
                    }
                    scope.Complete();
                }
            }
            finally
            {
                _connectionProvider.ReleaseConnection(connection);
            }
            return Task.FromResult(deleted);
        }

        /// <summary>
        /// Finalizer method that ensures resources are freed
        /// </summary>
        ~SQLSubscriptionTrackingService()
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
            GC.SuppressFinalize(this);
            _disposed = true;
        }

        /// <summary>
        /// Called by the <see cref="Dispose()"/> method or by the finalizer to free held resources
        /// </summary>
        /// <param name="disposing">Indicates whether this method is called from the 
        /// <see cref="Dispose()"/> method (<c>true</c>) or from the finalizer (<c>false</c>)</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _connectionProvider.TryDispose();
            }
        }

        /// <summary>
        /// Throws an <see cref="ObjectDisposedException"/> if the subscription tracking service has
        /// been disposed
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if the object is already disposed</exception>
        protected void CheckDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        }
    }
}