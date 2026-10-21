using System.Globalization;
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
        IPEndPoint remote = ParseEndPoint(options.Require("remote"));
        string mode = options.Get("mode") ?? "unconnected";
        string payloadText = options.Get("payload") ?? "ping";
        int count = options.GetInt("count", 1);
        int timeoutMs = options.GetInt("timeout-ms", 1000);
        byte[] payload = Encoding.UTF8.GetBytes(payloadText);
        byte[] buffer = new byte[4096];

        using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs,
        };
        if (mode == "connected")
            socket.Connect(remote);

        for (int index = 0; index < count; index++)
        {
            string sent;
            try
            {
                int written = mode == "connected" ? socket.Send(payload) : socket.SendTo(payload, remote);
                sent = $"{written}B";
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                Console.WriteLine($"[udp-client] #{index} send-failed {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            try
            {
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                int received = socket.ReceiveFrom(buffer, ref source);
                Console.WriteLine($"[udp-client] #{index} sent={sent} recv={received}B from={source} " +
                                  $"payload={Encoding.UTF8.GetString(buffer, 0, received)}");
            }
            catch (SocketException exception)
            {
                Console.WriteLine($"[udp-client] #{index} sent={sent} recv-failed " +
                                  $"{exception.SocketErrorCode} ({exception.ErrorCode})");
            }
        }

        return 0;
    }

    private static int Run(Options options, bool relay)
    {
        string bind = options.Get("bind") ?? "0.0.0.0";
        int port = options.GetInt("port", 5555);
        int count = options.GetInt("count", 1);
        int timeoutMs = options.GetInt("timeout-ms", options.GetInt("linger-ms", 5000));

        using Socket socket = UdpSockets.CreateListener(new IPEndPoint(IPAddress.Parse(bind), port));
        int actualPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        Console.WriteLine($"LISTENER-PID={Environment.ProcessId}");
        Console.WriteLine($"LISTENER-PORT={actualPort}");

        for (int index = 0; index < count; index++)
        {
            ReceivedDatagram datagram = UdpSockets.Receive(socket, 4096, 1024, timeoutMs);
            string context = datagram.RedirectContext is { } contextBytes
                                 ? $"{contextBytes.Length}B {Convert.ToHexString(contextBytes)}"
                                 : "none";
            Console.WriteLine($"[udp] #{index} from={datagram.Source} bytes={datagram.Payload.Length} context={context}");
            Console.WriteLine($"[udp] #{index} payload={Encoding.UTF8.GetString(datagram.Payload)}");

            if (relay)
            {
                socket.SendTo(datagram.Payload, datagram.Source);
                Console.WriteLine($"[udp] #{index} echoed to {datagram.Source}");
            }
        }

        return 0;
    }

    private static IPEndPoint ParseEndPoint(string text)
    {
        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"地址应为 HOST:PORT：{text}");
        return new IPEndPoint(IPAddress.Parse(text[..separator]),
                              int.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture));
    }
}
