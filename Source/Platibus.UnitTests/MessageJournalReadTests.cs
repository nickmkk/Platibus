﻿// The MIT License (MIT)
// 
// Copyright (c) 2017 Jesse Sweetland
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
using System.Linq;
using System.Threading.Tasks;
using Platibus.Journaling;
using Xunit;

namespace Platibus.UnitTests
{
    [Trait("Category", "UnitTests")]
    public abstract class MessageJournalReadTests
    {
        protected IMessageJournal MessageJournal;
        protected MessageJournalPosition Start;
        protected int Count;
        protected MessageJournalFilter Filter;

        protected IList<MessageJournalReadResult> Pages = new List<MessageJournalReadResult>();

        protected MessageJournalReadTests(IMessageJournal messageJournal)
        {
            MessageJournal = messageJournal;
        }

        [Fact]
        protected async Task ReadResultsCanBePaged()
        {
            await GivenSampleSet();
            await GivenAtBeginningOfJournal();
            GivenPageSize(10);
            await WhenReadingToEndOfJournal();

            // Expect all 32 messages to be read in 4 pages
            Assert.Equal(4, Pages.Count);
            Assert.Equal(Count, Pages[0].Entries.Count());
            Assert.Equal(Count, Pages[1].Entries.Count());
            Assert.Equal(Count, Pages[2].Entries.Count());
            Assert.Equal(2, Pages[3].Entries.Count());
        }

        [Fact]
        protected async Task ReadsAreRepeatable()
        {
            await GivenSampleSet();
            await GivenAtBeginningOfJournal();
            GivenPageSize(10);
            await WhenReadingToEndOfJournal();

            var firstRun = new List<MessageJournalReadResult>(Pages);
            Pages.Clear();

            await GivenAtBeginningOfJournal();
            await WhenReadingToEndOfJournal();

            var secondRun = new List<MessageJournalReadResult>(Pages);

            AssertSameResults(firstRun, secondRun);
        }

        [Theory]
        [InlineData("Sent", 8)]
        [InlineData("Received", 16)]
        [InlineData("Published", 8)]
        protected async Task ReadResultsCanBeFilteredByCategory(string category, int expectedCount)
        {
            await GivenSampleSet();
            await GivenAtBeginningOfJournal();
            GivenPageSize(10);
            GivenCategoryFilter(category);
            await WhenReadingToEndOfJournal();

            var actual = Pages.SelectMany(p => p.Entries).ToList();
            Assert.Equal(expectedCount, actual.Count);
           
            AssertCategory(actual, category);
        }

        [Theory]
        [InlineData("FooEvents", 4)]
        [InlineData("BarEvents", 4)]
        [InlineData("BazEvents", 8)]
        protected async Task ReadResultsCanBeFilteredByTopic(string topic, int expectedCount)
        {
            await GivenSampleSet();
            await GivenAtBeginningOfJournal();
            GivenPageSize(10);
            GivenTopicFilter(topic);
            await WhenReadingToEndOfJournal();

            var actual = Pages.SelectMany(p => p.Entries).ToList();
            Assert.Equal(expectedCount, actual.Count);

            AssertTopic(actual, topic);
        }

        [Theory]
        [InlineData("Received", "FooEvents", 2)]
        [InlineData("Published", "BazEvents", 4)]
        protected async Task ReadResultsCanBeFilteredByCategoryAndTopic(string category, string topic, int expectedCount)
        {
            await GivenSampleSet();
            await GivenAtBeginningOfJournal();
            GivenPageSize(10);
            GivenCategoryFilter(category);
            GivenTopicFilter(topic);
            await WhenReadingToEndOfJournal();

            var actual = Pages.SelectMany(p => p.Entries).ToList();
            Assert.Equal(expectedCount, actual.Count);

            AssertTopic(actual, topic);
        }

        protected async Task GivenSampleSet()
        {
            // Sample Set
            // ----------
            // 32 Messages
            //  8 Sent
            // 16 Received (16 direct, 8 publications)
            //  8 Published
            //  4 Messages with topic "FooEvents"
            //  4 Messages with topic "BarEvents"
            //  8 Messages with topic "BazEvents"
        
            await GivenJournaledSentMessage("FooRequest:1");
            await GivenJournaledReceivedMessage("FooRequest:1");
            await GivenJournaledPublication("FooStarted:1", "FooEvents");
            await GivenJournaledReceivedPublication("FooStarted:1", "FooEvents");
            await GivenJournaledPublication("FooCompleted:1", "FooEvents");
            await GivenJournaledReceivedPublication("FooCompleted:1", "FooEvents");
            await GivenJournaledSentMessage("FooResponse:1");
            await GivenJournaledReceivedMessage("FooResponse:1");
            await GivenJournaledSentMessage("BarRequest:1");
            await GivenJournaledReceivedMessage("BarRequest:1");
            await GivenJournaledPublication("BarStarted:1", "BarEvents");
            await GivenJournaledPublication("BarCompleted:1", "BarEvents");
            await GivenJournaledSentMessage("BarResponse:1");
            await GivenJournaledReceivedPublication("BarStarted:1", "BarEvents");
            await GivenJournaledSentMessage("BazRequest:1");
            await GivenJournaledReceivedMessage("BarResponse:1");
            await GivenJournaledReceivedMessage("BazRequest:1");
            await GivenJournaledReceivedPublication("BarCompleted:1", "BarEvents");
            await GivenJournaledPublication("BazStarted:1", "BazEvents");
            await GivenJournaledReceivedPublication("BazStarted:1", "BazEvents");
            await GivenJournaledPublication("BazFailed:1", "BazEvents");
            await GivenJournaledSentMessage("BazResponse:1");
            await GivenJournaledReceivedMessage("BazResponse:1");
            await GivenJournaledReceivedPublication("BazFailed:1", "BazEvents");
            await GivenJournaledSentMessage("BazRequest:2");
            await GivenJournaledReceivedMessage("BazRequest:2");
            await GivenJournaledPublication("BazStarted:2", "BazEvents");
            await GivenJournaledReceivedPublication("BazStarted:2", "BazEvents");
            await GivenJournaledPublication("BazCompleted:2", "BazEvents");
            await GivenJournaledReceivedPublication("BazCompleted:2", "BazEvents");
            await GivenJournaledSentMessage("BazResponse:2");
            await GivenJournaledReceivedMessage("BazResponse:2");
        }

        protected async Task GivenAtBeginningOfJournal()
        {
            Start = await MessageJournal.GetBeginningOfJournal();
        }

        protected void GivenPageSize(int count)
        {
            Count = count;
        }

        protected async Task GivenJournaledSentMessage(string content)
        {
            var messageHeaders = new MessageHeaders
            {
                MessageId = MessageId.Generate(),
                Sent = DateTime.UtcNow,
                Origination = new Uri("urn:localhost/platibus0"),
                Destination = new Uri("urn:localhost/platibus1"),
                MessageName = "Sent",
                ContentType = "text/plain"
            };
            var message = new Message(messageHeaders, content);
            await MessageJournal.Append(message, MessageJournalCategory.Sent);
        }

        protected async Task GivenJournaledReceivedMessage(string content)
        {
            var messageHeaders = new MessageHeaders
            {
                MessageId = MessageId.Generate(),
                Sent = DateTime.UtcNow.AddSeconds(-1),
                Received = DateTime.UtcNow,
                Origination = new Uri("urn:localhost/platibus0"),
                Destination = new Uri("urn:localhost/platibus1"),
                MessageName = "Received",
                ContentType = "text/plain"
            };
            var message = new Message(messageHeaders, content);
            await MessageJournal.Append(message, MessageJournalCategory.Received);
        }

        protected async Task GivenJournaledReceivedPublication(string content, TopicName topic)
        {
            var published = DateTime.UtcNow.AddSeconds(-1);
            var messageHeaders = new MessageHeaders
            {
                MessageId = MessageId.Generate(),
                Sent = published,
                Published = published,
                Received = DateTime.UtcNow,
                Origination = new Uri("urn:localhost/platibus0"),
                Destination = new Uri("urn:localhost/platibus1"),
                Topic = topic,
                MessageName = "Received",
                ContentType = "text/plain"
            };
            var message = new Message(messageHeaders, content);
            await MessageJournal.Append(message, MessageJournalCategory.Received);
        }

        protected async Task GivenJournaledPublication(string content, TopicName topic)
        {
            var messageHeaders = new MessageHeaders
            {
                MessageId = MessageId.Generate(),
                Published = DateTime.UtcNow,
                Origination = new Uri("urn:localhost/platibus0"),
                MessageName = "Published",
                ContentType = "text/plain",
                Topic = topic
            };
            var message = new Message(messageHeaders, content);
            await MessageJournal.Append(message, MessageJournalCategory.Published);
        }

        protected void GivenCategoryFilter(MessageJournalCategory category)
        {
            if (Filter == null)
            {
                Filter = new MessageJournalFilter();
            }
            Filter.Categories = new[] {category};
        }

        protected void GivenTopicFilter(TopicName topic)
        {
            if (Filter == null)
            {
                Filter = new MessageJournalFilter();
            }
            Filter.Topics = new[] { topic };
        }

        protected async Task WhenReadingToEndOfJournal()
        {
            MessageJournalReadResult result;
            do
            {
                result = await MessageJournal.Read(Start, Count, Filter);
                Pages.Add(result);
                Start = result.Next;
            } while (!result.EndOfJournal);
        }

        protected void AssertCategory(IEnumerable<MessageJournalEntry> journaledMessages,
            MessageJournalCategory expectedCategory)
        {
            foreach (var journaledMessage in journaledMessages)
            {
                Assert.Equal(expectedCategory, journaledMessage.Category);
            }
        }

        protected void AssertTopic(IEnumerable<MessageJournalEntry> journaledMessages,
            TopicName expectedTopic)
        {
            foreach (var journaledMessage in journaledMessages)
            {
                Assert.Equal(expectedTopic, journaledMessage.Data.Headers.Topic);
            }
        }

        protected void AssertSameResults(IList<MessageJournalReadResult> firstRun, IList<MessageJournalReadResult> secondRun)
        {
            Assert.Equal(firstRun.Count, secondRun.Count);
            for (var page = 0; page < firstRun.Count; page++)
            {
                var firstRunPage = firstRun[page];
                var secondRunPage = secondRun[page];

                Assert.Equal(firstRunPage.Start, secondRunPage.Start);
                Assert.Equal(firstRunPage.Next, secondRunPage.Next);
                Assert.Equal(firstRunPage.EndOfJournal, secondRunPage.EndOfJournal);
                Assert.Equal(firstRunPage.Entries.Count(), secondRunPage.Entries.Count());

                using (var firstRunMessages = firstRunPage.Entries.GetEnumerator())
                using (var secondRunMessages = secondRunPage.Entries.GetEnumerator())
                {
                    while (firstRunMessages.MoveNext() && secondRunMessages.MoveNext())
                    {
                        var firstRunMessage = firstRunMessages.Current;
                        var secondRunMessage = secondRunMessages.Current;

                        Assert.Equal(firstRunMessage.Position, secondRunMessage.Position);
                        Assert.Equal(firstRunMessage.Category, secondRunMessage.Category);
                        Assert.Equal(firstRunMessage.Data, secondRunMessage.Data, new MessageEqualityComparer());
                    }
                }
            }
        }
    }
}