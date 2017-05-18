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
using Platibus.Journaling;
using Platibus.Serialization;

namespace Platibus.Config
{
    /// <summary>
    /// Concrete mutable implementation of <see cref="IPlatibusConfiguration" /> used for
    /// programmatically configuring the bus.
    /// </summary>
    /// <remarks>
    /// This class is not threadsafe.  The caller must provide synchronization if there is
    /// a possibility for multiple threads to make conccurrent updates to instances of this
    /// class.
    /// </remarks>
    public class PlatibusConfiguration : IPlatibusConfiguration
    {
        private readonly EndpointCollection _endpoints = new EndpointCollection();
        private readonly IList<IHandlingRule> _handlingRules = new List<IHandlingRule>();
        private readonly IList<ISendRule> _sendRules = new List<ISendRule>();
        private readonly IList<ISubscription> _subscriptions = new List<ISubscription>();
        private readonly IList<TopicName> _topics = new List<TopicName>();

        /// <inheritdoc />
        public TimeSpan ReplyTimeout { get; set; }

        /// <inheritdoc />
        public IMessageNamingService MessageNamingService { get; set; }

        /// <inheritdoc />
        public ISerializationService SerializationService { get; set; }

        /// <inheritdoc />
        public IMessageJournal MessageJournal { get; set; }

        /// <inheritdoc />
        public IEndpointCollection Endpoints
        {
            get { return _endpoints; }
        }

        /// <inheritdoc />
        public IEnumerable<TopicName> Topics
        {
            get { return _topics; }
        }

        /// <inheritdoc />
        public IEnumerable<ISendRule> SendRules
        {
            get { return _sendRules; }
        }

        /// <inheritdoc />
        public IEnumerable<IHandlingRule> HandlingRules
        {
            get { return _handlingRules; }
        }

        /// <inheritdoc />
        public IEnumerable<ISubscription> Subscriptions
        {
            get { return _subscriptions; }
        }

        /// <inheritdoc />
        public string DefaultContentType { get; set; }

        /// <summary>
        /// Initializes a new <see cref="PlatibusConfiguration"/> instance with
        /// the default message naming and serialization services.
        /// </summary>
        /// <seealso cref="DefaultMessageNamingService"/>
        /// <seealso cref="DefaultSerializationService"/>
        public PlatibusConfiguration()
        {
            MessageNamingService = new DefaultMessageNamingService();
            SerializationService = new DefaultSerializationService();
		    DefaultContentType = "application/json";
        }

        /// <summary>
        /// Adds a named endpoint to the configuration
        /// </summary>
        /// <param name="name">The name of the endpoint</param>
        /// <param name="endpoint">The endpoint</param>
        /// <remarks>
        /// Named endpoints must be added before messages can be sent or
        /// subscriptions can be created
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown if either argument
        /// is <c>null</c></exception>
        public virtual void AddEndpoint(EndpointName name, IEndpoint endpoint)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (endpoint == null) throw new ArgumentNullException("endpoint");
            _endpoints.Add(name, endpoint);
        }

        /// <summary>
        /// Adds a topic to the configuration
        /// </summary>
        /// <param name="topic">The name of the topic</param>
        /// <remarks>
        /// Topics must be explicitly added in order to publish messages to them
        /// </remarks>
        public virtual void AddTopic(TopicName topic)
        {
            if (topic == null) throw new ArgumentNullException("topic");
            if (_topics.Contains(topic)) throw new TopicAlreadyExistsException(topic);
            _topics.Add(topic);
        }

        /// <summary>
        /// Adds a rule governing to which endpoints messages will be sent
        /// </summary>
        /// <param name="sendRule">The send rule</param>
        public virtual void AddSendRule(ISendRule sendRule)
        {
            if (sendRule == null) throw new ArgumentNullException("sendRule");
            _sendRules.Add(sendRule);
        }

        /// <summary>
        /// Adds a rule governing the handlers and queues to which incoming
        /// messages will be routed
        /// </summary>
        /// <param name="handlingRule">The handling rule</param>
        public virtual void AddHandlingRule(IHandlingRule handlingRule)
        {
            if (handlingRule == null) throw new ArgumentNullException("handlingRule");
            _handlingRules.Add(handlingRule);
        }

        /// <summary>
        /// Adds a subscription to a local or remote topic
        /// </summary>
        /// <param name="subscription">The subscription</param>
        public virtual void AddSubscription(ISubscription subscription)
        {
            if (subscription == null) throw new ArgumentNullException("subscription");
            _subscriptions.Add(subscription);
        }
    }
}