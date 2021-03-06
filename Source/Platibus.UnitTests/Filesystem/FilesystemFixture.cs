﻿using System;
using System.IO;
using Platibus.Filesystem;

namespace Platibus.UnitTests.Filesystem
{
    public class FilesystemFixture : IDisposable
    {
        private readonly DirectoryInfo _baseDirectory;
        private readonly FilesystemMessageQueueingService _messageQueueingService;
        private readonly FilesystemSubscriptionTrackingService _subscriptionTrackingService;

        private bool _disposed;

        public DirectoryInfo BaseDirectory
        {
            get { return _baseDirectory; }
        }
        
        public FilesystemMessageQueueingService MessageQueueingService
        {
            get { return _messageQueueingService; }
        }

        public FilesystemSubscriptionTrackingService SubscriptionTrackingService
        {
            get { return _subscriptionTrackingService; }
        }

        public FilesystemFixture()
        {
            _baseDirectory = GetTempDirectory();
            
            _messageQueueingService = new FilesystemMessageQueueingService(_baseDirectory);
            _messageQueueingService.Init();

            _subscriptionTrackingService = new FilesystemSubscriptionTrackingService(_baseDirectory);
            _subscriptionTrackingService.Init();
        }

        protected DirectoryInfo GetTempDirectory()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "Platibus.UnitTests", DateTime.Now.ToString("yyyyMMddHHmmss"));
            var tempDir = new DirectoryInfo(tempPath);
            if (!tempDir.Exists)
            {
                tempDir.Create();
            }
            return tempDir;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            Dispose(true);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _messageQueueingService.Dispose();
                _subscriptionTrackingService.Dispose();
            }
        }
    }
}
