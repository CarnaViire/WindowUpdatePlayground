using System;
using System.Threading;
using System.Net;
using System.Net.Sockets;

namespace WindowUpdateServer
{
    class Program
    {
        const int maxFrameSize = 16384;
        const int fileSize = 25 * 1024 * 1024;

        static void Main(string[] args)
        {
            string ip = args[0];
            int port = int.Parse(args[1]);

            Random random = new Random(fileSize);
            byte[] file = new byte[fileSize];
            random.NextBytes(file);

            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.Bind(new IPEndPoint(IPAddress.Parse(ip), port));
                Console.WriteLine($"[{DateTime.Now.TimeOfDay}] Socket bound to {server.LocalEndPoint}");

                server.Listen();

                while (true)
                {
                    Console.WriteLine("\nWaiting for client to connect...");
                    Socket accept = server.Accept();
                    Console.WriteLine($"[{DateTime.Now.TimeOfDay}] Socket connected to {accept.RemoteEndPoint}");

                    long bytesAvailable = 0;
                    ManualResetEventSlim bytesAddedEvent = new ManualResetEventSlim();
                    object syncObject = new object();

                    CancellationTokenSource cts = new CancellationTokenSource();
                    CancellationToken token = cts.Token;

                    // reading window updates from client
                    var readThread = new Thread(() => 
                        {
                            byte[] windowSizeMsg = new byte[4];
                            while (!token.IsCancellationRequested)
                            {
                                accept.Receive(windowSizeMsg);
                                int windowSize = BitConverter.ToInt32(windowSizeMsg);
                                lock (syncObject)
                                {
                                    if (windowSize < 0)
                                    {
                                        throw new Exception("negative windowSize");
                                    }
                                    bytesAvailable = checked(bytesAvailable + windowSize);
                                    bytesAddedEvent.Set();
                                }
                            }
                        });
                    readThread.Start();

                    // sending the data
                    int totalSent = 0;
                    int currentPercent = 0;
                    while (true)
                    {
                        if (Volatile.Read(ref bytesAvailable) == 0)
                        {
                            bytesAddedEvent.Wait();
                        }
                        int windowSize;
                        lock (syncObject)
                        {
                            if (bytesAvailable == 0)
                            {
                                throw new Exception("Bytes not available");
                            }
                            windowSize = (int)Math.Min(bytesAvailable, maxFrameSize);
                            bytesAvailable -= windowSize;
                            if (bytesAvailable < 0)
                            {
                                throw new Exception("negative bytes");
                            }
                            bytesAddedEvent.Reset();
                        }

                        Span<byte> buffer = file[totalSent..Math.Min(totalSent+windowSize, file.Length)];

                        int sent = accept.Send(buffer);
                        if (sent != buffer.Length)
                        {
                            throw new Exception("unexpected sent bytes " + sent);
                        }
                        totalSent += sent;
                        
                        int newPercent = (int)(totalSent * 100L / fileSize);
                        if (newPercent > currentPercent)
                        {
                            Console.WriteLine($"[{DateTime.Now.TimeOfDay}] {newPercent}%");
                            currentPercent = newPercent;
                        }

                        if (totalSent == fileSize) 
                        {
                            Console.WriteLine($"[{DateTime.Now.TimeOfDay}] Done");
                            break;
                        }
                    }

                    cts.Cancel(); // cancel read thread

                    accept.Disconnect(true);
                }
            }
        }
    }
}
