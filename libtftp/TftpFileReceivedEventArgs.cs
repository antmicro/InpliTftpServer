// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    using System;
    using System.IO;
    using System.Net;

    public class TftpFileReceivedEventArgs : EventArgs
    {
        public string Filename { get; set; }
        public DateTimeOffset TransferInitiated { get; set; }
        public DateTimeOffset TransferCompleted { get; set; }
        public IPEndPoint ReceivedFrom { get; set; }
        public MemoryStream Stream { get; set; }
    }
}
