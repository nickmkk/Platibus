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
using System.Text;
using System.Threading.Tasks;

namespace Platibus.Filesystem
{
    internal class MessageFileWriter : IDisposable
    {
        private readonly bool _leaveOpen;
        private readonly TextWriter _writer;

        private bool _disposed;

        public MessageFileWriter(TextWriter writer, bool leaveOpen = false)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            _writer = writer;
            _leaveOpen = leaveOpen;
        }

        public MessageFileWriter(Stream stream, Encoding encoding = null, bool leaveOpen = false)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            _writer = new StreamWriter(stream, encoding ?? Encoding.UTF8);
            _leaveOpen = leaveOpen;
        }

        public async Task WriteMessage(Message message)
        {
            if (message == null) throw new ArgumentNullException("message");

            // Write a blank line to preserve backward compatibility with older messages that
            // have a binary formatted SenderPrincipal.  The MessageFileReader will continue to
            // support older messages for the forseeable future to ensure that older messages can
            // still be processed following an update.
            await _writer.WriteLineAsync();

            var headers = message.Headers;
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    var headerName = header.Key;
                    var headerValue = header.Value;
                    await _writer.WriteAsync(string.Format("{0}: ", headerName));
                    using (var headerValueReader = new StringReader(headerValue))
                    {
                        var multilineContinuation = false;
                        string line;
                        while ((line = await headerValueReader.ReadLineAsync()) != null)
                        {
                            if (multilineContinuation)
                            {
                                // Prefix continuation with whitespace so that subsequent
                                // lines are not confused with different headers.
                                line = "    " + line;
                            }
                            await _writer.WriteLineAsync(line);
                            multilineContinuation = true;
                        }
                    }
                }
            }

            // Blank line separates headers from content
            await _writer.WriteLineAsync();

            var content = message.Content;
            if (!string.IsNullOrWhiteSpace(content))
            {
                // No special formatting required for content.  Content is defined as
                // everything following the blank line.
                await _writer.WriteAsync(content);
            }
        }

        ~MessageFileWriter()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Dispose(true);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_writer")]
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _writer.Dispose();
            }
        }
    }
}