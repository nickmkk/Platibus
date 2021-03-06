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
using System.Threading.Tasks;
using Platibus.Config;
using Platibus.Config.Extensibility;
using Platibus.Journaling;
using Platibus.Multicast;

namespace Platibus.SQLite
{
    /// <summary>
    /// A provider for SQLite-based message queueing and subscription tracking services
    /// </summary>
    [Provider("SQLite")]
    public class SQLiteServicesProvider : IMessageQueueingServiceProvider, IMessageJournalProvider, ISubscriptionTrackingServiceProvider
    {
        /// <inheritdoc />
        public async Task<IMessageQueueingService> CreateMessageQueueingService(QueueingElement configuration)
        {
            var securityTokenServiceFactory = new SecurityTokenServiceFactory();
            var securitTokenConfig = configuration.SecurityTokens;
            var securityTokenService = await securityTokenServiceFactory.InitSecurityTokenService(securitTokenConfig);

            var path = configuration.GetString("path");
            var sqliteBaseDir = GetRootedDirectory(path);
            var sqliteMessageQueueingService = new SQLiteMessageQueueingService(sqliteBaseDir, securityTokenService);
            sqliteMessageQueueingService.Init();
            return sqliteMessageQueueingService;
        }

        /// <inheritdoc />
        public Task<IMessageJournal> CreateMessageJournal(JournalingElement configuration)
        {
            var path = configuration.GetString("path");
            var sqliteBaseDir = GetRootedDirectory(path);
            var sqliteMessageQueueingService = new SQLiteMessageJournal(sqliteBaseDir);
            sqliteMessageQueueingService.Init();
            return Task.FromResult<IMessageJournal>(sqliteMessageQueueingService);
        }

        /// <inheritdoc />
        public Task<ISubscriptionTrackingService> CreateSubscriptionTrackingService(
            SubscriptionTrackingElement configuration)
        {
            var path = configuration.GetString("path");
            var sqliteBaseDir = GetRootedDirectory(path);
            var sqliteSubscriptionTrackingService = new SQLiteSubscriptionTrackingService(sqliteBaseDir);
            sqliteSubscriptionTrackingService.Init();

            var multicast = configuration.Multicast;
            if (multicast == null || !multicast.Enabled)
            {
                return Task.FromResult<ISubscriptionTrackingService>(sqliteSubscriptionTrackingService);
            }

            var multicastTrackingService = new MulticastSubscriptionTrackingService(
                sqliteSubscriptionTrackingService, multicast.Address, multicast.Port);

            return Task.FromResult<ISubscriptionTrackingService>(multicastTrackingService);
        }

        private static DirectoryInfo GetRootedDirectory(string path)
        {
            var rootedPath = GetRootedPath(path);
            return rootedPath == null ? null : new DirectoryInfo(rootedPath);
        }

        private static string GetRootedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var defaultRootedPath = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrWhiteSpace(path))
            {
                return defaultRootedPath;
            }

            return Path.IsPathRooted(path) 
                ? path 
                : Path.Combine(defaultRootedPath, path);
        }
    }
}