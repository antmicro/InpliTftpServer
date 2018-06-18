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
    using System.Net.Sockets;
    using System.Threading.Tasks;
    using System.Timers;

    public class TftpServer : IDisposable
    {
        /// <summary>
        /// A singleton instance of the TFTP server object
        /// </summary>
        public static TftpServer Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new TftpServer();
                return _instance;
            }
        }

        private static TftpServer _instance;

        /// <summary>
        /// An event handler for when a file is received
        /// </summary>
        public EventHandler<TftpTransferCompleteEventArgs> FileReceived;

        /// <summary>
        /// An async event handler for when a file is received
        /// </summary>
        public event Func<object, TftpTransferCompleteEventArgs, Task> FileReceivedAsync;

        /// <summary>
        /// An event handler for when a file is finished transmitting
        /// </summary>
        public EventHandler<TftpTransferCompleteEventArgs> FileTransmitted;

        /// <summary>
        /// An async event handler for when a file is finished transmitting
        /// </summary>
        public event Func<object, TftpTransferCompleteEventArgs, Task> FileTransmittedAsync;

        /// <summary>
        /// An event called to log a message
        /// </summary>
        public EventHandler<TftpLogEventArgs> Log;

        /// <summary>
        /// When a read request comes in, this event is a callback to provide the stream to be transferred
        /// </summary>
        public event Func<object, TftpGetStreamEventArgs, Task> GetStream;

        /// <summary>
        /// The maximum period of idle time to retain a session in memory before cleaning it up
        /// </summary>
        public TimeSpan MaximumIdleSession { get; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The maximum time to wait before retransmitting an unacknowledged packet
        /// </summary>
        public TimeSpan RetransmissionTimeout { get; } = TimeSpan.FromMilliseconds(1000);

        /// <summary>
        /// The log level for the server to reach before logging.
        /// </summary>
        public ETftpLogSeverity LogSeverity { get; set; } = ETftpLogSeverity.Informational;

        private UdpClient Socket { get; set; }

        private UdpClient Socket6 { get; set; }

        private Timer PeriodicTimer;

        private ConcurrentDictionary<IPEndPoint, TftpSession> Sessions { get; set; } = new ConcurrentDictionary<IPEndPoint, TftpSession>();

        /// <summary>
        /// Constructor
        /// </summary>
        public TftpServer()
        {
        }

        /// <summary>
        /// Start the server
        /// </summary>
        /// <param name="port">The port to start it on</param>
        public void Start(int port = 69)
        {
            if (Socket != null)
                throw new InvalidOperationException("Cannot start the server, it's already started");

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

            var handlerTasks = retransmitSessions.Select(x => Task.Factory.StartNew(() => x.Value.RetransmitAsync()));

            Task.WhenAll(handlerTasks);
        }

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

                var receiveTask =
                    Task.Factory.StartNew(
                        async () => {
                            try
                            {
                                await session.OnReceiveAsync(messageData);
                            }
                            catch (Exception e)
                            {
                                LogError("Internal error: " + e.Message);
                            }
                        }
                    );
            }
            catch (Exception e)
            {
                LogError("Internal error: " + e.Message);
            }

            socket.BeginReceive(new AsyncCallback(OnUdpData), socket);
        }

        /// <summary>
        /// Called by sessions to request a transfer stream from the host application
        /// </summary>
        /// <param name="remoteHost">The remote host requesting the transfer</param>
        /// <param name="filename">The filename requested by the remote host</param>
        /// <returns></returns>
        internal async Task<Stream> GetReadStreamAsync(IPEndPoint remoteHost, string filename)
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

            Delegate[] invocationList = GetStream.GetInvocationList();
            Task[] handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
            {
                handlerTasks[i] = ((Func<object, TftpGetStreamEventArgs, Task>)invocationList[i])(this, eventArgs);
            }

            await Task.WhenAll(handlerTasks);

            if(eventArgs.Result == null)
            {
                LogError("Unknown file");
                return null;
            }

            return eventArgs.Result;
        }

        /// <summary>
        /// Called by a session to remove itself when it's no longer needed
        /// </summary>
        /// <param name="tftpSession">The session to remove</param>
        internal void UnregisterSession(TftpSession tftpSession)
        {
            if (!Sessions.TryRemove(tftpSession.RemoteHost, out TftpSession removedSession))
                throw new Exception("Could not remove session " + tftpSession.RemoteHost.ToString() + " from known sessions");
        }

        /// <summary>
        /// Called by a session to signal that it's complete
        /// </summary>
        /// <param name="session">The transfer which is complete</param>
        internal async Task TransferCompleteAsync(TftpSession session)
        {
            UnregisterSession(session);

            EventHandler<TftpTransferCompleteEventArgs> handler =
                (session.Operation == ETftpOperationType.WriteOperation) ?
                    FileReceived :
                    FileTransmitted
                    ;

            var eventArgs = new TftpTransferCompleteEventArgs
            {
                Operation = session.Operation,
                Filename = session.Filename,
                RemoteHost = session.RemoteHost,
                Stream = (session.Operation == ETftpOperationType.WriteOperation) ? (MemoryStream)session.TransferStream : null,
                TransferInitiated = session.TransferRequestInitiated,
                TransferCompleted = DateTimeOffset.Now
            };

            handler?.Invoke(
                this,
                eventArgs
            );

            var invocationList =
                (session.Operation == ETftpOperationType.WriteOperation) ?
                    (FileReceivedAsync?.GetInvocationList()) :
                    (FileTransmittedAsync?.GetInvocationList())
                    ;

            if (invocationList == null)
                return;

            var handlerTasks = new Task[invocationList.Length];

            for (int i = 0; i < invocationList.Length; i++)
                handlerTasks[i] = ((Func<object, TftpTransferCompleteEventArgs, Task>)invocationList[i])(this, eventArgs);

            await Task.WhenAll(handlerTasks);
        }

        /// <summary>
        /// Used to send a packet to a host
        /// </summary>
        /// <param name="destination">The endpoint to transfer to</param>
        /// <param name="buffer">The data to transmit</param>
        internal void Transmit(IPEndPoint destination, byte[] buffer)
        {
            Transmit(destination, buffer, buffer.Length);
        }

        /// <summary>
        /// Transmit a packet to a remote host
        /// </summary>
        /// <param name="destination">The host to transmit to</param>
        /// <param name="buffer">The buffer to transmit</param>
        /// <param name="length">The length of the data to transfer</param>
        internal void Transmit(IPEndPoint destination, byte[] buffer, int length)
        {
            if (destination.AddressFamily == AddressFamily.InterNetwork)
                Socket.Send(buffer, length, destination);
            else if (destination.AddressFamily == AddressFamily.InterNetworkV6)
                Socket6.Send(buffer, length, destination);
            else
                throw new NotImplementedException("Protocol not implemented");
        }

        /// <summary>
        /// Implementation of the IDisposable interface
        /// </summary>
        public void Dispose()
        {
            PeriodicTimer.Stop();
            Socket.Close();
            Socket6.Close();
        }

        /// <summary>
        /// Syslog an error level message
        /// </summary>
        /// <param name="message">The message to log</param>
        internal void LogError(string message)
        {
            if (Log != null && LogSeverity >= ETftpLogSeverity.Error)
            {
                Log.Invoke(
                    this,
                    new TftpLogEventArgs
                    {
                        Severity = ETftpLogSeverity.Error,
                        TimeStamp = DateTimeOffset.Now,
                        Message = message
                    }
                );
            }
        }

        /// <summary>
        /// Syslog an infortmational level message
        /// </summary>
        /// <param name="message">The message to log</param>
        internal void LogInfo(string message)
        {
            if (Log != null && LogSeverity >= ETftpLogSeverity.Informational)
            {
                Log.Invoke(
                    this,
                    new TftpLogEventArgs
                    {
                        Severity = ETftpLogSeverity.Informational,
                        TimeStamp = DateTimeOffset.Now,
                        Message = message
                    }
                );
            }
        }

        /// <summary>
        /// Syslog a debug level message
        /// </summary>
        /// <param name="message">The message to log</param>
        internal void LogDebug(string message)
        {
            if (Log != null && LogSeverity >= ETftpLogSeverity.Debug)
            {
                Log.Invoke(
                    this,
                    new TftpLogEventArgs
                    {
                        Severity = ETftpLogSeverity.Debug,
                        TimeStamp = DateTimeOffset.Now,
                        Message = message
                    }
                );
            }
        }
    }
}
