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
using Microsoft.Owin;
using Platibus.Http;

namespace Platibus.Owin
{
    internal class OwinResponseAdapter : IHttpResourceResponse
    {
        private readonly IOwinResponse _response;
        private readonly ContentType _contentType;

        public int StatusCode
        {
            set { _response.StatusCode = value; }
        }

        public string StatusDescription
        {
            set { _response.ReasonPhrase = value; }
        }

        public string ContentType
        {
            get { return _response.ContentType; }
            set { _response.ContentType = value; }
        }

        public Encoding ContentEncoding
        {
            get { return _contentType.CharsetEncoding; }
            set
            {
                _contentType.CharsetEncoding = value;
                _response.ContentType = _contentType;
            }
        }

        public Stream OutputStream
        {
            get { return _response.Body; }
        }

        public OwinResponseAdapter(IOwinResponse response)
        {
            if (response == null) throw new ArgumentNullException("response");
            _response = response;
            _contentType = response.ContentType;
        }

        public void AddHeader(string header, string value)
        {
            _response.Headers.Append(header, value);
        }
    }
}