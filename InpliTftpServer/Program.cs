// Telenor Inpli TFTP Server Module
//
// Copyright 2018 Telenor Inpli AS Norway

namespace InpliTftpServer
{
    using System;
    using System.IO;

    class Program
    {
        static void Main(string[] args)
        {
            libtftp.TftpServer.Instance.LogLevel = libtftp.ETftpLogSeverity.Debug;

            libtftp.TftpServer.Instance.FileReceived += 
                new EventHandler<libtftp.TftpTransferCompleteEventArgs>((sender, ev) => {
                    Console.WriteLine(
                        "Received file from " +
                        ev.RemoteHost.ToString() +
                        " called [" + ev.Filename + "] with " +
                        ev.Stream.Length.ToString() +
                        " bytes"
                    );
                }
            );

            libtftp.TftpServer.Instance.FileTransmitted +=
                new EventHandler<libtftp.TftpTransferCompleteEventArgs>((sender, ev) => {
                Console.WriteLine(
                    "Transmitted file to " +
                    ev.RemoteHost.ToString() +
                    " called [" + ev.Filename + "]"                    
                    );
                }
            );

            libtftp.TftpServer.Instance.Log +=
                new EventHandler<libtftp.TftpLogEventArgs>((sender, ev) =>
                {
                    switch (ev.LogLevel)
                    {
                        case libtftp.ETftpLogSeverity.Error:
                            Console.ForegroundColor = ConsoleColor.Red;
                            break;

                        case libtftp.ETftpLogSeverity.Debug:
                            Console.ForegroundColor = ConsoleColor.Gray;
                            break;

                        default:
                            Console.ForegroundColor = ConsoleColor.White;
                            break;
                    }

                    Console.Write("[" + ev.TimeStamp.ToString() + "]: ");

                    Console.ForegroundColor = ConsoleColor.White;

                    Console.WriteLine(ev.Message);
                }
            );

            libtftp.TftpServer.Instance.GetStream +=
                new EventHandler<libtftp.TftpGetStreamEventArgs>((sender, ev) =>
                {
                    ev.Result = new FileStream(@"C:\Temp\Teapot.Config", FileMode.Open, FileAccess.Read);
                }
            );

            libtftp.TftpServer.Instance.Start();

            Console.ReadKey();
        }
    }
}
