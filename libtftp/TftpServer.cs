// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.NetworkInformation;
    using System.Net.Sockets;
    using System.Timers;

    public class TftpServer : IDisposable
    {
        private static TftpServer _instance;
        public static TftpServer Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new TftpServer();
                return _instance;
            }
        }

        public EventHandler<TftpFileReceivedEventArgs> FileReceived;

        public EventHandler<TftpFileReceivedEventArgs> FileTransmitted;

        public EventHandler<TftpLogEventArgs> Log;

        public EventHandler<TftpGetStreamEventArgs> GetStream;

        public ETftpLogLevel LogLevel { get; set; } = ETftpLogLevel.Informational;

        private UdpClient Socket { get; set; }
        private UdpClient Socket6 { get; set; }

        private string LocalDomainName { get; set; }
        private string LocalHostName { get; set; }
        private string LocalFQDN { get; set; }

        private Timer PeriodicTimer;
        public TimeSpan MaximumIdleSession { get; } = TimeSpan.FromSeconds(5);
        public TimeSpan RetransmissionTimeout { get; } = TimeSpan.FromMilliseconds(1000);

        public TftpServer()
        {
            LocalDomainName = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            LocalHostName = Dns.GetHostName();

            LocalFQDN = LocalHostName + (string.IsNullOrWhiteSpace(LocalDomainName) ? "" : ("." + LocalDomainName));
        }

        public void Start(int port = 69)
        {
            Socket = new UdpClient(port);
            Socket.BeginReceive(new AsyncCallback(OnUdpData), Socket);

            Socket6 = new UdpClient(port, AddressFamily.InterNetworkV6);
            Socket6.BeginReceive(new AsyncCallback(OnUdpData), Socket6);

            PeriodicTimer = new Timer(500);
            PeriodicTimer.Elapsed += PeriodicTimer_Elapsed;
            PeriodicTimer.Start();
        }

        private void PeriodicTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            var now = DateTimeOffset.Now;
            var idledOutSessions = Sessions.Where(x => now.Subtract(x.Value.IdleSince) > MaximumIdleSession);

            foreach (var session in idledOutSessions)
                UnregisterSession(session.Value);

            var retransmitSessions = Sessions
                .Where(x => 
                    x.Value.Operation == ETftpOperationType.ReadOperation &&
                    now.Subtract(x.Value.IdleSince) > RetransmissionTimeout
                 );

            foreach (var session in retransmitSessions)
                session.Value.Retransmit();
        }

        internal ConcurrentDictionary<IPEndPoint, TftpSession> Sessions { get; set; } = new ConcurrentDictionary<IPEndPoint, TftpSession>();

        private void OnUdpData(IAsyncResult result)
        {
            var timeReceived = DateTimeOffset.Now;
            var socket = result.AsyncState as UdpClient;

            IPEndPoint source = new IPEndPoint(0, 0);
            byte[] messageData = socket.EndReceive(result, ref source);

            try
            {
                if(!Sessions.TryGetValue(source, out TftpSession session))
                {
                    session = new TftpSession(this, source);
                    Sessions[source] = session;
                }

                session.OnReceive(messageData);
            }
            catch (Exception e)
            {
                //UnhandledMessageReceived?.Invoke(this,
                //    new SyslogParsingErrorEvent
                //    {
                //        MessageSource = source,
                //        ExceptionMessage = e.Message,
                //        MessageData = messageData
                //    }
                //);
            }

            socket.BeginReceive(new AsyncCallback(OnUdpData), socket);
        }

        internal Stream GetReadStream(IPEndPoint remoteHost, string filename)
        {
            if(GetStream == null)
            {
                LogError("No file system available");
                return null;
            }

            var eventArgs = new TftpGetStreamEventArgs
            {
                Filename = filename,
                RemoteHost = remoteHost
            };

            GetStream.Invoke(
                this,
                eventArgs
            );

            if(eventArgs.Result == null)
            {
                LogError("Unknown file");
                return null;
            }

            return eventArgs.Result;
        }

        internal void UnregisterSession(TftpSession tftpSession)
        {
            if (!Sessions.TryRemove(tftpSession.RemoteHost, out TftpSession removedSession))
                throw new Exception("Could not remove session " + tftpSession.RemoteHost.ToString() + " from known sessions");
        }

        internal void TransferComplete(TftpSession session)
        {
            UnregisterSession(session);
            FileReceived?.Invoke(
                this,
                new TftpFileReceivedEventArgs
                {
                    Filename = session.Filename,
                    ReceivedFrom = session.RemoteHost,
                    Stream = session.ReceiveStream,
                    TransferInitiated = session.TransferRequestInitiated,
                    TransferCompleted = DateTimeOffset.Now
                }
            );
        }

        internal void ReadRequestComplete(TftpSession session)
        {
            UnregisterSession(session);
            FileTransmitted?.Invoke(
                this,
                new TftpFileReceivedEventArgs
                {
                    Filename = session.Filename,
                    ReceivedFrom = session.RemoteHost,
                    Stream = session.ReceiveStream,
                    TransferInitiated = session.TransferRequestInitiated,
                    TransferCompleted = DateTimeOffset.Now
                }
            );
        }

        internal void Transmit(IPEndPoint destination, byte[] buffer)
        {
            if (destination.AddressFamily == AddressFamily.InterNetwork)
                Socket.Send(buffer, buffer.Length, destination);
            else if (destination.AddressFamily == AddressFamily.InterNetworkV6)
                Socket6.Send(buffer, buffer.Length, destination);
            else
                throw new NotImplementedException("Protocol not implemented");
        }

        internal void Transmit(IPEndPoint destination, byte[] buffer, int length)
        {
            if (destination.AddressFamily == AddressFamily.InterNetwork)
                Socket.Send(buffer, length, destination);
            else if (destination.AddressFamily == AddressFamily.InterNetworkV6)
                Socket6.Send(buffer, length, destination);
            else
                throw new NotImplementedException("Protocol not implemented");
        }

        public void Dispose()
        {
            PeriodicTimer.Stop();
            Socket.Close();
            Socket6.Close();
        }

        internal void LogError(string message)
        {
            if (Log != null && LogLevel >= ETftpLogLevel.Error)
            {
                Log.Invoke(
                    this,
                    new TftpLogEventArgs
                    {
                        LogLevel = ETftpLogLevel.Error,
                        TimeStamp = DateTimeOffset.Now,
                        Message = message
                    }
                );
            }
        }

        internal void LogInfo(string message)
        {
            if (Log != null && LogLevel >= ETftpLogLevel.Informational)
            {
                Log.Invoke(
                    this,
                    new TftpLogEventArgs
                    {
                        LogLevel = ETftpLogLevel.Informational,
                        TimeStamp = DateTimeOffset.Now,
                        Message = message
                    }
                );
            }
        }

        internal void LogDebug(string message)
        {
            if (Log != null && LogLevel >= ETftpLogLevel.Debug)
            {
                Log.Invoke(
                    this,
                    new TftpLogEventArgs
                    {
                        LogLevel = ETftpLogLevel.Debug,
                        TimeStamp = DateTimeOffset.Now,
                        Message = message
                    }
                );
            }
        }
    }
}
