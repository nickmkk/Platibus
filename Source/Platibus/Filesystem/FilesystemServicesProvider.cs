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
using Platibus.Multicast;

namespace Platibus.Filesystem
{
    /// <summary>
    /// A provider that returns filesystem based message queueing, message journaling, and
    /// subscription trakcing services
    /// </summary>
    [Provider("Filesystem")]
    public class FilesystemServicesProvider : IMessageQueueingServiceProvider,
        ISubscriptionTrackingServiceProvider
    {
        /// <summary>
        /// Creates an initializes a <see cref="IMessageQueueingService"/>
        /// based on the provided <paramref name="configuration"/>
        /// </summary>
        /// <param name="configuration">The journaling configuration
        /// element</param>
        /// <returns>Returns a task whose result is an initialized
        /// <see cref="IMessageQueueingService"/></returns>
        public async Task<IMessageQueueingService> CreateMessageQueueingService(QueueingElement configuration)
        {
            var securityTokenServiceFactory = new SecurityTokenServiceFactory();
            var securitTokenConfig = configuration.SecurityTokens;
            var securityTokenService = await securityTokenServiceFactory.InitSecurityTokenService(securitTokenConfig);

            var path = configuration.GetString("path");
            var fsQueueingBaseDir = GetRootedDirectory(path);
            var fsQueueingService = new FilesystemMessageQueueingService(fsQueueingBaseDir, securityTokenService);
            fsQueueingService.Init();
            return fsQueueingService;
        }
        
        /// <summary>
        /// Creates an initializes a <see cref="ISubscriptionTrackingService"/>
        /// based on the provided <paramref name="configuration"/>.
        /// </summary>
        /// <param name="configuration">The journaling configuration
        /// element.</param>
        /// <returns>Returns a task whose result is an initialized
        /// <see cref="ISubscriptionTrackingService"/>.</returns>
        public Task<ISubscriptionTrackingService> CreateSubscriptionTrackingService(
            SubscriptionTrackingElement configuration)
        {
            var path = configuration.GetString("path");
            var fsTrackingBaseDir = GetRootedDirectory(path);
            var fsTrackingService = new FilesystemSubscriptionTrackingService(fsTrackingBaseDir);
            fsTrackingService.Init();

            var multicast = configuration.Multicast;
            if (multicast == null || !multicast.Enabled)
            {
                return Task.FromResult<ISubscriptionTrackingService>(fsTrackingService);
            }
             
            var multicastTrackingService = new MulticastSubscriptionTrackingService(
                fsTrackingService, multicast.Address, multicast.Port);

            return Task.FromResult<ISubscriptionTrackingService>(multicastTrackingService);
        }

        private static DirectoryInfo GetRootedDirectory(string path)
        {
            var rootedPath = GetRootedPath(path);
            return rootedPath == null ? null : new DirectoryInfo(rootedPath);
        }

        private static string GetRootedPath(string path)
        {
            var defaultRootedPath = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrWhiteSpace(path))
            {
                return defaultRootedPath;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.Combine(defaultRootedPath, path);
        }
    }
}