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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Platibus.Queueing;
using Platibus.Security;

namespace Platibus.SQLite
{
    /// <summary>
    /// An <see cref="IMessageQueueingService"/> implementation that stores queued
    /// messages in a SQLite database
    /// </summary>
    public class SQLiteMessageQueueingService : AbstractMessageQueueingService<SQLiteMessageQueue>
    {
        private readonly DirectoryInfo _baseDirectory;
        private readonly ISecurityTokenService _securityTokenService;
        
        /// <summary>
        /// Initializes a new <see cref="SQLiteMessageQueueingService"/>
        /// </summary>
        /// <param name="baseDirectory">The directory in which the SQLite database files will
        /// be created</param>
        /// <param name="securityTokenService">(Optional) The message security token
        /// service to use to issue and validate security tokens for persisted messages.</param>
        /// <remarks>
        /// <para>If a base directory is not specified then the base directory will default to a
        /// directory named <c>platibus\queues</c> beneath the current app domain base 
        /// directory.  If the base directory does not exist it will be created.</para>
        /// <para>If a <paramref name="securityTokenService"/> is not specified then a
        /// default implementation based on unsigned JWTs will be used.</para>
        /// </remarks>
        public SQLiteMessageQueueingService(DirectoryInfo baseDirectory, ISecurityTokenService securityTokenService = null)
        {
            if (baseDirectory == null)
            {
                var appdomainDirectory = AppDomain.CurrentDomain.BaseDirectory;
                baseDirectory = new DirectoryInfo(Path.Combine(appdomainDirectory, "platibus", "queues"));
            }
            _baseDirectory = baseDirectory;
            _securityTokenService = securityTokenService ?? new JwtSecurityTokenService();
        }

        /// <summary>
        /// Initializes the SQLite message queueing service
        /// </summary>
        /// <remarks>
        /// Creates directories if they do not exist
        /// </remarks>
        public void Init()
        {
            _baseDirectory.Refresh();
            if (!_baseDirectory.Exists)
            {
                _baseDirectory.Create();
                _baseDirectory.Refresh();
            }
        }
        
        /// <inheritdoc />
        protected override Task<SQLiteMessageQueue> InternalCreateQueue(QueueName queueName, IQueueListener listener, QueueOptions options = null,
            CancellationToken cancellationToken = new CancellationToken())
        {
            var queue = new SQLiteMessageQueue(_baseDirectory, queueName, listener, _securityTokenService, options);
            return Task.FromResult(queue);
        }
    }
}