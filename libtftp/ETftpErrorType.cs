// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace libtftp
{
    public enum ETftpErrorType
    {
        NotDefined = 0,
        FileNotFound = 1,
        AccessViolation = 2,
        DiskFullOrAllocationExceeded = 3,
        IllegalOperation = 4,
        UnknownTransferId = 5,
        FileAlreadyExists = 6,
        NoSuchUser = 7
    }
}
