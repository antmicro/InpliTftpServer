// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;

    internal class TftpSession
    {
        public IPEndPoint RemoteHost { get; private set; }

        public TftpServer Parent { get; private set; }

        private int CurrentBlock { get; set; } = 0;

        public ETftpOperationType Operation { get; set; } = ETftpOperationType.Unspecified;

        public string Filename { get; set; }

        internal MemoryStream ReceiveStream { get; set; }

        internal Stream TransmitStream { get; set; }

        public DateTimeOffset TransferRequestInitiated { get; set; }

        public DateTimeOffset IdleSince { get; set; }

        private DateTimeOffset LastMessageTime { get; set; } = DateTimeOffset.MinValue;

        private long BytesReceived { get; set; }

        internal TftpSession(TftpServer parent, IPEndPoint remoteHost)
        {
            Parent = parent;
            RemoteHost = remoteHost;
            IdleSince = DateTimeOffset.Now;
        }

        private void LogError(string message)
        {
            Parent.LogError(RemoteHost.ToString() + ": " + message);
        }

        private void LogInfo(string message)
        {
            Parent.LogInfo(RemoteHost.ToString() + ": " + message);
        }

        private void LogDebug(string message)
        {
            Parent.LogDebug(RemoteHost.ToString() + ": " + message);
        }

        internal void OnReceive(byte[] messageData)
        {
            IdleSince = DateTimeOffset.Now;

            // No reason to throw if the message is empty
            if (messageData.Length < 2)
                return;

            var messageType = messageData.Get16BE(0);
            switch((ETftpPacketType)messageType)
            {
                case ETftpPacketType.WriteRequest:
                    OnWriteRequest(messageData);
                    break;

                case ETftpPacketType.ReadRequest:
                    OnReadRequest(messageData);
                    break;

                case ETftpPacketType.Data:
                    OnDataReceived(messageData);
                    break;

                case ETftpPacketType.Acknowledgement:
                    OnAcknowledge(messageData);
                    break;

                case ETftpPacketType.Error:
                    OnError(messageData);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }

        private void OnError(byte[] messageData)
        {
            if(messageData.Length >= 5)
            {
                var errorCode = (ETftpErrorType)messageData.Get16BE(2);
                var start = 4;
                var index = start;
                while (index < messageData.Length && messageData[index] != 0)
                    index++;

                if(messageData[index] == 0)
                {
                    var message = Encoding.UTF8.GetString(messageData, start, index - start);
                    LogError("Client error: " + errorCode.ToString() + ": " + message);
                }
            }

            Parent.UnregisterSession(this);
        }

        private byte[] TransmitBuffer = new byte[516];
        private int TransmitBufferLength = 0;
        private int BlockTransmitCount = 0;

        private void OnAcknowledge(byte[] messageData)
        {
            if (messageData.Length < 4)
            {
                Retransmit();
                return;
            }

            if(TransmitBufferLength < 516)
            {
                Parent.ReadRequestComplete(this);
                return;
            }

            TransmitBufferLength = 0;
            Retransmit();
        }

        internal void Retransmit()
        {
            if(TransmitBufferLength == 0)
            {
                int bytesRead = TransmitStream.Read(TransmitBuffer, 4, 512);
                TransmitBufferLength = bytesRead + 4;
                CurrentBlock++;
                BlockTransmitCount = 0;
                TransmitBuffer.Write16BE(0, (int)ETftpPacketType.Data);
                TransmitBuffer.Write16BE(2, CurrentBlock);
            }
            else
            {
                BlockTransmitCount++;
            }

            Parent.Transmit(RemoteHost, TransmitBuffer, TransmitBufferLength);

        }

        private void OnReadRequest(byte[] messageData)
        {
            LogDebug("Received read request");

            var request = ProcessRequestHeader(messageData);
            if (request == null)
                return;

            if (Operation == ETftpOperationType.WriteOperation)
            {
                TransmitError(ETftpErrorType.IllegalOperation, "Already processing WriteRequest");
            }
            else if (TransmitStream != null)
            {
                TransmitError(ETftpErrorType.IllegalOperation, "Read request already in progress");
            }
            else if (!string.IsNullOrEmpty(Filename) && Filename != request.Filename)
            {
                TransmitError(ETftpErrorType.IllegalOperation, "Read request conflicts with previous read request");
            }
            else
            {
                TransmitStream = Parent.GetReadStream(RemoteHost, request.Filename);
                if(TransmitStream == null)
                {
                    TransmitError(ETftpErrorType.FileNotFound, "File not found");
                    return;
                }

                Operation = ETftpOperationType.ReadOperation;
                Filename = request.Filename;
                TransferRequestInitiated = IdleSince;
                Retransmit();
            }
        }

        private void OnDataReceived(byte[] messageData)
        {
            if (messageData.Length < 4)
            {
                LogDebug("Packet ended prematurely on receive");
                TransmitError(ETftpErrorType.IllegalOperation, "Packet ended prematurely");
                Parent.UnregisterSession(this);

                return;
            }

            var blockNumber = messageData.Get16BE(2);

            if(blockNumber != ((CurrentBlock + 1) & 0xFFFF))
            {
                LogDebug("Block received out of sequence");
                TransmitError(ETftpErrorType.IllegalOperation, "Block received out of sequence");
                Parent.UnregisterSession(this);
            }

            BytesReceived += messageData.Length - 4;

            CurrentBlock++;
            TransmitAck(blockNumber);

            if(ReceiveStream == null)
            {
                if(CurrentBlock != 1)
                {
                    LogDebug("ReceiveStream not created yet but not on first packet. Ending transfer");
                    TransmitError(ETftpErrorType.NotDefined, "Server error");
                    Parent.UnregisterSession(this);
                }

                ReceiveStream = new MemoryStream();
            }

            ReceiveStream.Write(messageData, 4, messageData.Length - 4);

            if (messageData.Length != 516)
            {
                LogDebug("Last block received, transfer complete");
                Parent.TransferComplete(this);
            }
            else
            {
                if (IdleSince.Subtract(LastMessageTime) > TimeSpan.FromSeconds(1))
                {
                    LogDebug("Received " + BytesReceived.ToString() + " bytes so far");
                    LastMessageTime = IdleSince;
                }
            }
        }

        private TftpRequest ProcessRequestHeader(byte [] messageData)
        {
            var index = 2;

            var startOfFileName = index;
            while (index < messageData.Length && messageData[index] != 0)
                index++;

            if (index >= messageData.Length || messageData[index] != 0)
            {
                LogDebug("Message ends prematurely while reading filename");
                TransmitError(ETftpErrorType.IllegalOperation, "Filename not specified");

                return null;
            }

            var fileName = Encoding.UTF8.GetString(messageData.Skip(startOfFileName).Take(index - startOfFileName).ToArray());
            if (string.IsNullOrWhiteSpace(fileName))
            {
                LogDebug("Message contains null or empty filename");
                TransmitError(ETftpErrorType.IllegalOperation, "Filename not specified");

                return null;
            }

            LogDebug("Request for filename: " + fileName);

            index++;
            var startOfModeString = index;
            while (index < messageData.Length && messageData[index] != 0)
                index++;

            if (index >= messageData.Length || messageData[index] != 0)
            {
                LogDebug("Message ends prematurely while reading mode");
                TransmitError(ETftpErrorType.IllegalOperation, "Transfer mode not specified");

                return null;
            }

            var mode = Encoding.UTF8.GetString(messageData.Skip(startOfModeString).Take(index - startOfModeString).ToArray());
            if (string.IsNullOrWhiteSpace(mode))
            {
                LogDebug("Message contains null or empty mode");
                TransmitError(ETftpErrorType.IllegalOperation, "Transfer mode not specified");

                return null;
            }

            LogDebug("Request mode: " + mode);

            if (mode != "octet")
            {
                LogDebug("Unhandled TFTP mode " + mode);
                TransmitError(ETftpErrorType.IllegalOperation, "Unhandled TFTP transfer mode");

                return null;
            }

            return new TftpRequest
            {
                Filename = fileName,
                Mode = mode
            };
        }

        private void OnWriteRequest(byte[] messageData)
        {
            LogDebug("Received write request");

            var request = ProcessRequestHeader(messageData);
            if (request == null)
                return;

            if (Operation == ETftpOperationType.ReadOperation)
            {
                TransmitError(ETftpErrorType.IllegalOperation, "Already processing ReadRequest");
            }
            else if (ReceiveStream != null)
            {
                TransmitError(ETftpErrorType.IllegalOperation, "Write request already in progress");
            }
            else if(!string.IsNullOrEmpty(Filename) && Filename != request.Filename)
            {
                TransmitError(ETftpErrorType.IllegalOperation, "Write request conflicts with previous write request");
            }
            else
            {
                TransmitAck(CurrentBlock);

                Operation = ETftpOperationType.WriteOperation;
                Filename = request.Filename;
                TransferRequestInitiated = IdleSince;
            }
        }

        private void TransmitAck(int blockNumber)
        {
            Parent.Transmit(
                RemoteHost,
                new byte[]
                {
                    00, (byte)ETftpPacketType.Acknowledgement,
                    (byte)((blockNumber >> 8) & 0xff),
                    (byte)(blockNumber & 0xff)
                }
            );
        }

        private void TransmitError(ETftpErrorType errorNumber, string message)
        {
            Parent.Transmit(
                RemoteHost,
                new byte[]
                {
                    00, (byte)ETftpPacketType.Error,
                    (byte)(((int)errorNumber >> 8) & 0xff),
                    (byte)((int)errorNumber & 0xff)
                }
                .Concat(Encoding.UTF8.GetBytes(message))
                .Concat(new byte[] { 0 })
                .ToArray()
            );
        }
    }
}
