﻿using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Platibus.Filesystem;

namespace Platibus.UnitTests.Filesystem
{
    public class FilesystemMessageJournalingServiceTests : MessageJournalingServiceTests
    {
        private readonly FilesystemMessageJournalInspector _inspector;

        public FilesystemMessageJournalingServiceTests() 
            : this(FilesystemCollectionFixture.Instance)
        {
        }

        public FilesystemMessageJournalingServiceTests(FilesystemCollectionFixture fixture)
            : base(InitMessageJournalingService(fixture))
        {
            _inspector = new FilesystemMessageJournalInspector(fixture.BaseDirectory);
        }

        private static FilesystemMessageJournalingService InitMessageJournalingService(FilesystemCollectionFixture fixture)
        {
            var messageJournalingService = new FilesystemMessageJournalingService(fixture.BaseDirectory);
            messageJournalingService.Init();
            return messageJournalingService;
        }

        protected override async Task AssertSentMessageIsWrittenToJournal()
        {
            var journaledMessages = await _inspector.EnumerateSentMessages();
            var messageIsJournaled = journaledMessages
                .Any(m => m.Headers.MessageId == Message.Headers.MessageId);
            Assert.That(messageIsJournaled, Is.True);
        }

        protected override async Task AssertReceivedMessageIsWrittenToJournal()
        {
            var journaledMessages = await _inspector.EnumerateReceivedMessages();
            var messageIsJournaled = journaledMessages
                .Any(m => m.Headers.MessageId == Message.Headers.MessageId);
            Assert.That(messageIsJournaled, Is.True);
        }

        protected override async Task AssertPublishedMessageIsWrittenToJournal()
        {
            var journaledMessages = await _inspector.EnumeratePublishedMessages();
            var messageIsJournaled = journaledMessages
                .Any(m => m.Headers.MessageId == Message.Headers.MessageId);
            Assert.That(messageIsJournaled, Is.True);
        }
    }
}