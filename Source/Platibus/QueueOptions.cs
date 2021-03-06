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

namespace Platibus
{
    /// <summary>
    /// Options for setting behavior on queues
    /// </summary>
    public class QueueOptions : IEquatable<QueueOptions>
    {
        /// <summary>
        /// The default maximum number of attempts
        /// </summary>
        public const int DefaultMaxAttempts = 10;

        /// <summary>
        /// The default concurrency limit, i.e. the maximum number of concurrent worker
        /// processes that read from the same queue
        /// </summary>
        public const int DefaultConcurrencyLimit = 4;

        /// <summary>
        /// The default retry delay expressed in milliseconds
        /// </summary>
        public const int DefaultRetryDelay = 1000;

        private int _concurrencyLimit = DefaultConcurrencyLimit;
        private int _maxAttempts = DefaultMaxAttempts;
        private TimeSpan _retryDelay = TimeSpan.FromMilliseconds(DefaultRetryDelay);
        private bool _isDurable = true;

        /// <summary>
        ///     The maximum number of messages that will be processed concurrently.
        /// </summary>
        /// <remarks>
        ///     If this value is less than or equal to <c>0</c> the <see cref="DefaultConcurrencyLimit" />
        ///     will be applied.
        /// </remarks>
        /// <seealso cref="DefaultConcurrencyLimit" />
        public int ConcurrencyLimit
        {
            get { return _concurrencyLimit; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Concurrency limit must be greater than zero");
                }
                _concurrencyLimit = value;
            }
        }

        /// <summary>
        ///     Whether the messages should be acknowledged automatically.  If auto-acknowledge
        ///     is set to <code>false</code> the <see cref="IQueueListener" /> must explicitly
        ///     acknowledge each message.  Otherwise the message will be acknowledged automatically
        ///     provided an exception is not thrown from the <see cref="IQueueListener.MessageReceived" />
        ///     method.
        /// </summary>
        public bool AutoAcknowledge { get; set; }

        /// <summary>
        ///     The maximum number of attempts to process the message.  If the <see cref="IQueueListener" />
        ///     does not acknowledge the message, or the queue is set to <see cref="AutoAcknowledge" /> and
        ///     an exception is thrown from the <see cref="IQueueListener.MessageReceived" /> method, the
        ///     message will be put back on the queue and retried up to this maximum number of attempts.
        /// </summary>
        public int MaxAttempts
        {
            get { return _maxAttempts; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Maximum number of attempts must be greater than zero");
                }
                _maxAttempts = value;
            }
        }

        /// <summary>
        ///     The amount of time to wait before putting an unacknowledged message back on the queue.
        /// </summary>
        public TimeSpan RetryDelay
        {
            get { return _retryDelay; }
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Retry delay must be greater than zero");
                }
                _retryDelay = value;
            }
        }

        /// <summary>
        /// The maximum amount of time the queue should live.
        /// </summary>
        public TimeSpan TTL { get; set; }

        /// <summary>
        /// Indicates whether the queue should be durable or transient (if supported)
        /// </summary>
        public bool IsDurable
        {
            get { return _isDurable; }
            set { _isDurable = value; }
        }
        
        /// <inheritdoc />
        public bool Equals(QueueOptions other)
        {
            if (ReferenceEquals(null, other)) return false;
            return ConcurrencyLimit == other.ConcurrencyLimit
                   && AutoAcknowledge == other.AutoAcknowledge
                   && MaxAttempts == other.MaxAttempts
                   && RetryDelay.Equals(other.RetryDelay)
                   && TTL.Equals(other.TTL)
                   && IsDurable == other.IsDurable;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            var other = obj as QueueOptions;
            return Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = ConcurrencyLimit;
                hashCode = (hashCode * 397) ^ AutoAcknowledge.GetHashCode();
                hashCode = (hashCode * 397) ^ MaxAttempts;
                hashCode = (hashCode * 397) ^ RetryDelay.GetHashCode();
                hashCode = (hashCode * 397) ^ TTL.GetHashCode();
                hashCode = (hashCode * 397) ^ IsDurable.GetHashCode();
                return hashCode;
            }
        }

        /// <summary>
        /// Determines whether two sets of queue options are equivalent based on their respective
        /// values
        /// </summary>
        /// <param name="left">The left operand</param>
        /// <param name="right">The right operand</param>
        /// <returns>Returns <c>true</c> if the <paramref name="left"/> queue options are
        /// equivalent to the <paramref name="right"/> queue options; <c>false</c> otherwise.
        /// </returns>
        public static bool operator ==(QueueOptions left, QueueOptions right)
        {
            return Equals(left, right);
        }

        /// <summary>
        /// Determines whether two sets of queue options are not equivalent based on their 
        /// respective values
        /// </summary>
        /// <param name="left">The left operand</param>
        /// <param name="right">The right operand</param>
        /// <returns>Returns <c>true</c> if the <paramref name="left"/> queue options are
        /// not equivalent to the <paramref name="right"/> queue options; <c>false</c> otherwise.
        /// </returns>
        public static bool operator !=(QueueOptions left, QueueOptions right)
        {
            return !Equals(left, right);
        }
    }
}