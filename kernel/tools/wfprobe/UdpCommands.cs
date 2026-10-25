using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Probe;

internal static class UdpCommands
{
    public static int Listen(Options options) => Run(options, relay: false);

    public static int Relay(Options options) => Run(options, relay: true);

    public static int Client(Options options)
    {
        IPEndPoint remote = UdpSockets.ParseEndPoint(options.Require("remote"));
        string mode = options.Get("mode") ?? "unconnected";
        if (mode is not ("connected" or "unconnected"))
            throw new ArgumentException($"--mode 只接受 connected / unconnected：{mode}");
        bool connected = mode == "connected";
        string defaultPayload = options.Get("payload") ?? "ping";
        int count = options.GetInt("count", 1);
        int timeoutMs = options.GetInt("timeout-ms", 1000);
        bool receive = options.GetInt("recv", 1) != 0;
        int holdMs = options.GetInt("hold-ms", 0);
        bool control = options.Has("control-stdin");
        byte[] buffer = new byte[65536];

        Socket socket = CreateClient(remote, connected, options.Get("bind"));
        try
        {
            Console.WriteLine($"[udp-client] ready pid={Environment.ProcessId} local={socket.LocalEndPoint} " +
                              $"remote={remote} mode={mode}");
            Console.Out.Flush();

            int failures = 0;
            if (control)
            {
                failures += RunControlLoop(socket, remote, connected, defaultPayload, timeoutMs, receive, buffer);
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(defaultPayload);
                    if (!Send(socket, remote, connected, payload, index, out int written))
                    {
                        failures++;
                        continue;
                    }

                    Console.WriteLine($"[udp-client] send#{index} sent={written}B {ReceiveText(socket, receive, timeoutMs, buffer)}");
                    Console.Out.Flush();
                }

                Console.WriteLine($"[udp-client] done pid={Environment.ProcessId} count={count} failures={failures}");
                Console.Out.Flush();
                if (holdMs > 0)
                    Thread.Sleep(holdMs);
            }

            return failures == 0 ? 0 : 4;
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static int RunControlLoop(Socket initial,
                                      IPEndPoint remote,
                                      bool connected,
                                      string defaultPayload,
                                      int timeoutMs,
                                      bool receive,
                                      byte[] buffer)
    {
        Socket socket = initial;
        int failures = 0;
        int index = 0;
        while (true)
        {
            string? line = Console.In.ReadLine();
            if (line is null)
                break;
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (line == "quit")
                break;
            if (line == "new")
            {
                socket.Dispose();
                socket = CreateClient(remote, connected, null);
                Console.WriteLine($"[udp-client] new pid={Environment.ProcessId} local={socket.LocalEndPoint}");
                Console.Out.Flush();
                continue;
            }
            IPEndPoint target = remote;
            string payloadText = defaultPayload;
            if (line.StartsWith("sendto ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    Console.WriteLine($"[udp-client] unknown-command {line}");
                    continue;
                }

                target = UdpSockets.ParseEndPoint(parts[1]);
                if (parts.Length >= 3)
                    payloadText = parts[2];
            }
            else if (line.StartsWith("send ", StringComparison.Ordinal))
                payloadText = line[5..];
            else if (line != "send")
            {
                Console.WriteLine($"[udp-client] unknown-command {line}");
                continue;
            }

            byte[] payload = Encoding.UTF8.GetBytes(payloadText);
            if (!Send(socket, target, connected, payload, index, out int written))
            {
                failures++;
                index++;
                continue;
            }

            Console.WriteLine($"[udp-client] send#{index} sent={written}B {ReceiveText(socket, receive, timeoutMs, buffer)}");
            Console.Out.Flush();
            index++;
        }

        socket.Dispose();
        Console.WriteLine($"[udp-client] done pid={Environment.ProcessId} count={index} failures={failures}");
        Console.Out.Flush();
        return failures;
    }

    private static Socket CreateClient(IPEndPoint remote, bool connected, string? bind)
    {
        var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (remote.AddressFamily == AddressFamily.InterNetworkV6)
            {
                try
                {
                    socket.DualMode = true;
                }
                catch (SocketException)
                {
                }
            }

            if (bind is not null)
                socket.Bind(new IPEndPoint(IPAddress.Parse(bind), 0));

            if (connected)
                socket.Connect(remote);

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool Send(Socket socket,
                             IPEndPoint remote,
                             bool connected,
                             byte[] payload,
                             int index,
                             out int written)
    {
        written = 0;
        try
        {
            written = connected ? socket.Send(payload) : socket.SendTo(payload, remote);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            Console.WriteLine($"[udp-client] send#{index} send-failed {exception.GetType().Name}: {exception.Message}");
            Console.Out.Flush();
            return false;
        }
    }

    private static string ReceiveText(Socket socket, bool receive, int timeoutMs, byte[] buffer)
    {
        if (!receive)
            return "recv=skipped";
        socket.ReceiveTimeout = timeoutMs;

        EndPoint source = socket.AddressFamily == AddressFamily.InterNetworkV6
                              ? new IPEndPoint(IPAddress.IPv6Any, 0)
                              : new IPEndPoint(IPAddress.Any, 0);
        try
        {
            int received = socket.ReceiveFrom(buffer, ref source);
            return $"recv={received}B from={source} payload={Encoding.UTF8.GetString(buffer, 0, received)}";
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            string detail = exception is SocketException socketException
                                ? $"{socketException.SocketErrorCode} ({socketException.ErrorCode})"
                                : exception.GetType().Name;
            return $"recv-failed {detail}";
        }
    }

    private static int Run(Options options, bool relay)
    {
        IPAddress bind = IPAddress.Parse(options.Get("bind") ?? "0.0.0.0");
        int port = options.GetInt("port", 5555);
        int count = options.GetInt("count", 1);
        int timeoutMs = options.GetInt("timeout-ms", 5000);
        int lingerMs = options.GetInt("linger-ms", 0);

        using Socket socket = UdpSockets.CreateListener(new IPEndPoint(bind, port),
                                                       bind.AddressFamily == AddressFamily.InterNetworkV6);
        int actualPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Console.WriteLine($"LISTENER-PID={Environment.ProcessId}");
        Console.WriteLine($"LISTENER-PORT={actualPort}");
        Console.WriteLine($"[udp] ready pid={Environment.ProcessId} port={actualPort} bind={bind}");
        Console.Out.Flush();

        for (int index = 0; index < count; index++)
        {
            ReceivedDatagram datagram;
            try
            {
                datagram = UdpSockets.Receive(socket, 4096, 2048, timeoutMs);
            }
            catch (TimeoutException exception)
            {
                Console.WriteLine($"[udp] #{index} {exception.Message}");
                return 1;
            }

            string context = datagram.RedirectContext is { } contextBytes
                                 ? $"{contextBytes.Length}B level={datagram.ContextLevel} " +
                                   Convert.ToHexString(contextBytes)
                                 : "none";
            Console.WriteLine($"[udp] #{index} from={datagram.Source} bytes={datagram.Payload.Length} " +
                              $"context={context}");
            Console.WriteLine($"[udp] #{index} payload={Encoding.UTF8.GetString(datagram.Payload)}");
            Console.Out.Flush();

            if (relay)
            {
                socket.SendTo(datagram.Payload, datagram.Source);
                Console.WriteLine($"[udp] #{index} echoed to {datagram.Source}");
            }
        }

        if (lingerMs > 0)
            Thread.Sleep(lingerMs);

        return 0;
    }
}
