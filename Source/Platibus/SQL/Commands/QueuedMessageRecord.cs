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

namespace Platibus.SQL.Commands
{
    /// <summary>
    /// A representation of a queued message stored in a message journal record
    /// </summary>
    public class QueuedMessageRecord
    {
        /// <summary>
        /// Message headers concatenated and formatted as a single string value
        /// </summary>
        public string Headers { get; set; }

        /// <summary>
        /// The message content
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// The number of attempts that have been made to process the message
        /// </summary>
        public int Attempts { get; set; }

        /// <summary>
        /// A serialized form of the principal in scope when the message was received
        /// </summary>
        [Obsolete]
        public string SenderPrincipal { get; set; }
    }
}
