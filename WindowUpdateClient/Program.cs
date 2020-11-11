using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace WindowUpdateClient
{
    class Program
    {
        static void Main(string[] args)
        {
            string ip = args[0];
            int port = int.Parse(args[1]);

            Stopwatch s = Stopwatch.StartNew();
            Console.WriteLine("init_window_size\twindow_update\tdownload_time_ms");

            int maxIinitialWindowSize = 8 * 1024 * 1024;
            int minIinitialWindowSize = 16 * 1024;

            int maxIter = (int)Math.Log2(maxIinitialWindowSize / minIinitialWindowSize) + 1;
            int maxSizeIter = 100;

            int initialWindowSize = maxIinitialWindowSize;
            for (int iter = 0; iter < maxIter; ++iter, initialWindowSize /= 2)
            {
                int windowUpdateThreshold = initialWindowSize / 8;

                for (int i = 0; i < maxSizeIter; ++i)
                {
                    byte[] buffer = new byte[initialWindowSize];
                    MemoryStream drainage = new MemoryStream();

                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        socket.Connect(new IPEndPoint(IPAddress.Parse(ip), port));
                        //Console.Error.WriteLine($"\n[{DateTime.Now.TimeOfDay}] Socket connected to {socket.RemoteEndPoint}: {socket.Connected}");

                        byte[] windowUpdateMsg = BitConverter.GetBytes(windowUpdateThreshold);
                        int totalRead = 0;
                        int pendingWindowUpdate = 0;

                        int bytesAvailable = 0;
                        ManualResetEventSlim bytesAddedEvent = new ManualResetEventSlim();
                        object syncObject = new object();

                        CancellationTokenSource cts = new CancellationTokenSource();
                        CancellationToken token = cts.Token;

                        s.Restart();

                        socket.Send(BitConverter.GetBytes(initialWindowSize)); // init

                        // reading data from server
                        var readThread = new Thread(() => 
                            {
                                while (true)
                                {
                                    int bytesReceived = socket.Receive(buffer); // ugh... doing rubbish in bytes, need spanning and circling the buffer
                                    totalRead += bytesReceived;

                                    if (bytesReceived <= 0)
                                    {
                                        cts.Cancel();
                                        break;
                                    }

                                    lock (syncObject)
                                    {
                                        bytesAvailable += bytesReceived;
                                        if (bytesAvailable > buffer.Length)
                                        {
                                            throw new Exception("Buffer overflow");
                                        }
                                        bytesAddedEvent.Set();
                                    }
                                }
                            });
                        readThread.Start();

                        // draining data to memory stream and sending window updates
                        while (!token.IsCancellationRequested)
                        {
                            if (Volatile.Read(ref bytesAvailable) == 0)
                            {
                                try
                                {
                                    bytesAddedEvent.Wait(token);
                                }
                                catch (OperationCanceledException)
                                {
                                    s.Stop();
                                    //Console.Error.WriteLine("Done");
                                    break;
                                }
                            }

                            int writeSize = bytesAvailable;
                            drainage.Write(buffer[0..writeSize]); // sorry byte rubbish again

                            lock (syncObject)
                            {
                                bytesAvailable -= writeSize;
                                bytesAddedEvent.Reset();
                            }

                            pendingWindowUpdate += writeSize;
                            if (pendingWindowUpdate >= windowUpdateThreshold)
                            {
                                pendingWindowUpdate = 0;
                                socket.Send(windowUpdateMsg);
                            }
                        }

                        //Console.WriteLine($"WINDOW_UPDATE {windowUpdateThreshold/1024} Kb: Elapsed {s.ElapsedMilliseconds} ms -- {(totalRead * 1000.0 / s.ElapsedMilliseconds / 1024 / 1024)} Mb/s");
                        Console.WriteLine($"{initialWindowSize}\t{windowUpdateThreshold}\t{s.ElapsedMilliseconds}");

                        int finishedIter = iter * maxSizeIter + i + 1;
                        Console.Error.WriteLine($"[{DateTime.Now.TimeOfDay}] {finishedIter * 100 / maxIter / maxSizeIter}%");
                    }
                }
            }
        }
    }
}
