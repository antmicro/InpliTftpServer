// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    using System;

    public class TftpLogEventArgs : EventArgs
    {
        public DateTimeOffset TimeStamp { get; set; }
        public ETftpLogLevel LogLevel { get; set; }
        public string Message { get; set; }
    }
}
