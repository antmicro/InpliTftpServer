// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    using System;
    using System.IO;
    using System.Net;

    public class TftpGetStreamEventArgs : EventArgs
    {
        public IPEndPoint RemoteHost { get; set; }
        public string Filename { get; set; }
        public Stream Result { get; set; }
    }
}