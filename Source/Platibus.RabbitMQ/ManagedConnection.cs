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
using Platibus.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Platibus.RabbitMQ
{
    /// <summary>
    /// Maintains an open connection to a RabbitMQ server and attempts to reconnect
    /// whenever there is a failure.
    /// </summary>
    internal class ManagedConnection : IConnection
    {
        private readonly object _syncRoot = new object();
        private readonly string _managedConnectionId;
        private readonly IConnectionFactory _connectionFactory;
        private readonly IDiagnosticService _diagnosticService;

        private volatile IConnection _connection;

        private bool _closed;

        public string ManagedConnectionId { get { return _managedConnectionId; } }

        public ManagedConnection(Uri uri, IDiagnosticService diagnosticService)
        {
            if (uri == null) throw new ArgumentNullException("uri");
            if (diagnosticService == null) throw new ArgumentNullException("diagnosticService");

            // Trailing slashes causes errors when connecting to RabbitMQ
            uri = uri.WithoutTrailingSlash();

            var managedConnectionIdBuilder = new UriBuilder(uri)
            {
                // Sanitize credentials in managed connection ID.  This value
                // is output in log messages for diagnostics, so we don't want
                // or need the credentials displayed.
                UserName = "",
                Password = ""
            };

            _managedConnectionId = managedConnectionIdBuilder.Uri.ToString();
            _connectionFactory = new ConnectionFactory {Uri = uri.ToString()};
            _diagnosticService = diagnosticService;
            _connection = _connectionFactory.CreateConnection();
        }

        private IConnection Connection
        {
            get
            {
                CheckClosed();
                var myConnection = _connection;
                if (myConnection != null && myConnection.IsOpen) return myConnection;
                lock (_syncRoot)
                {
                    myConnection = _connection;
                    if (myConnection != null && myConnection.IsOpen) return myConnection;

                    if (myConnection != null)
                    {
                        _diagnosticService.Emit(new RabbitMQEventBuilder(this, RabbitMQEventType.RabbitMQReconnect)
                        {
                            Detail = "Reconnecting managed connection ID " + _managedConnectionId
                        }.Build());
                    }

                    myConnection = _connectionFactory.CreateConnection();
                    myConnection.CallbackException += CallbackException;
                    myConnection.ConnectionShutdown += ConnectionShutdown;
                    myConnection.ConnectionShutdown += (sender, args) => _connection = null;
                    myConnection.ConnectionBlocked += ConnectionBlocked;
                    myConnection.ConnectionUnblocked += ConnectionUnblocked;

                    _connection = myConnection;

                    _diagnosticService.Emit(new RabbitMQEventBuilder(this, RabbitMQEventType.RabbitMQConnectionOpened)
                    {
                        Detail = "Opened managed connection ID " + _managedConnectionId
                    }.Build());
                }
                return _connection;
            }
        }

        public string ClientProvidedName
        {
            get { return Connection.ClientProvidedName; }
        }

        public IDictionary<string, object> ClientProperties
        {
            get { return Connection.ClientProperties; }
        }
        
        public int LocalPort
        {
            get { return Connection.LocalPort; }
        }
        
        public int RemotePort
        {
            get { return Connection.RemotePort; }
        }

        public void Dispose()
        {
        }

        private void CheckClosed()
        {
            if (_closed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        ~ManagedConnection()
        {
            CloseManagedConnection(false);
        }

        public void CloseManagedConnection(bool disposing)
        {
            if (_closed) return;

            _closed = true;
            if (disposing)
            {
                _connection.Close();
                _connection = null;
                
                _diagnosticService.Emit(new RabbitMQEventBuilder(this, RabbitMQEventType.RabbitMQConnectionClosed)
                {
                    Detail = "Managed connection ID " + _managedConnectionId + " successfully closed"
                }.Build());
            }
            GC.SuppressFinalize(this);
        }

        public void Abort()
        {
            if (_connection == null) return;
            lock (_syncRoot)
            {
                if (_connection == null) return;
                _connection.Abort();
                _connection = null;
                _diagnosticService.Emit(new RabbitMQEventBuilder(this, RabbitMQEventType.RabbitMQConnectionAborted)
                {
                    Detail = "Managed connection ID " + _managedConnectionId + " aborted"
                }.Build());
            }
        }

        public void Abort(ushort reasonCode, string reasonText)
        {
            if (_connection == null) return;
            lock (_syncRoot)
            {
                if (_connection == null) return;
                _connection.Abort(reasonCode, reasonText);
                _connection = null;
            }
        }

        public void Abort(int timeout)
        {
            if (_connection == null) return;
            lock (_syncRoot)
            {
                if (_connection == null) return;
                _connection.Abort(timeout);
                _connection = null;
            }
        }

        public void Abort(ushort reasonCode, string reasonText, int timeout)
        {
            if (_connection == null) return;
            lock (_syncRoot)
            {
                if (_connection == null) return;
                _connection.Abort(reasonCode, reasonText, timeout);
                _connection = null;
            }
        }

        public void Close()
        {
        }

        public void Close(ushort reasonCode, string reasonText)
        {
        }

        public void Close(int timeout)
        {
        }

        public void Close(ushort reasonCode, string reasonText, int timeout)
        {
        }

        public IModel CreateModel()
        {
            return Connection.CreateModel();
        }

        public void HandleConnectionBlocked(string reason)
        {
            Connection.HandleConnectionBlocked(reason);
        }

        public void HandleConnectionUnblocked()
        {
            Connection.HandleConnectionUnblocked();
        }

        public bool AutoClose
        {
            get { return false; }
            set { }
        }

        public ushort ChannelMax
        {
            get { return Connection.ChannelMax; }
        }

        public ShutdownEventArgs CloseReason
        {
            get { return Connection.CloseReason; }
        }

        public AmqpTcpEndpoint Endpoint
        {
            get { return Connection.Endpoint; }
        }

        public uint FrameMax
        {
            get { return Connection.FrameMax; }
        }

        public ushort Heartbeat
        {
            get { return Connection.Heartbeat; }
        }

        public bool IsOpen
        {
            get { return Connection.IsOpen; }
        }

        public AmqpTcpEndpoint[] KnownHosts
        {
            get { return Connection.KnownHosts; }
        }

        public IProtocol Protocol
        {
            get { return Connection.Protocol; }
        }

        public IDictionary<string, object> ServerProperties
        {
            get { return Connection.ServerProperties; }
        }

        public IList<ShutdownReportEntry> ShutdownReport
        {
            get { return Connection.ShutdownReport; }
        }

        public ConsumerWorkService ConsumerWorkService
        {
            get { return Connection.ConsumerWorkService; }
        }

        public event EventHandler<CallbackExceptionEventArgs> CallbackException;
        public event EventHandler<ConnectionBlockedEventArgs> ConnectionBlocked;
        public event EventHandler<ShutdownEventArgs> ConnectionShutdown;
        public event EventHandler<EventArgs> ConnectionUnblocked;

    }
}
