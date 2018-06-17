// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    public enum ETftpPacketType
    {
        ReadRequest = 1,
        WriteRequest = 2,
        Data = 3,
        Acknowledgement = 4,
        Error = 5
    }
}
