// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

using System;

internal static class BufferPrimatives
{
    public static int Get16BE(this byte [] buffer, long offset)
    {
        if ((offset + 2) >= buffer.Length)
            throw new IndexOutOfRangeException();

        return ((int)(buffer[offset]) << 8) | (int)buffer[offset+1];
    }

    public static void Write16BE(this byte [] buffer, long offset, int value)
    {
        if ((offset + 2) >= buffer.Length)
            throw new IndexOutOfRangeException();

        buffer[offset] = (byte)((value >> 16) & 0xff);
        buffer[offset + 1] = (byte)(value & 0xff);
    }
}
