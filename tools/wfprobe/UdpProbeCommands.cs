using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

internal static class UdpProbeCommands
{
    public static int Configure(Options options)
    {
        using var device = new WfpDevice();
        UdpProbeConfig config = default;
        config.Version = Protocol.UdpProbeConfigVersion;
        config.Enabled = options.Has("enable") ? options.GetUInt("enable", 1) : 1;
        config.ListenerPort = (ushort)options.GetUInt("port", 0);
        config.ListenerProcessId = options.GetUInt("pid", 0);
        config.ReturnMode = ParseReturnMode(options.Get("return-mode") ?? "off");
        config.ReturnLayer = (options.Get("layer") ?? "inbound") switch
        {
            "outbound" => (byte)1,
            "inbound" or "datagram" => (byte)0,
            _ => throw new ArgumentException($"未知 layer：{options.Get("layer")}"),
        };
        config.RemotePortFilter = (ushort)options.GetUInt("filter-port", 0);
        if (options.Get("target") is { } target)
        {
            config.RedirectTarget = 1;
            unsafe
            {
                byte[] bytes = IPAddress.Parse(target).GetAddressBytes();
                for (int index = 0; index < bytes.Length && index < 16; index++)
                    config.TargetAddress[index] = bytes[index];
            }
        }

        device.SetUdpProbeConfig(config);
        Console.WriteLine($"[udp-probe] enabled={config.Enabled} port={config.ListenerPort} pid={config.ListenerProcessId} " +
                          $"return-mode={config.ReturnMode} layer={config.ReturnLayer} " +
                          $"filter-port={config.RemotePortFilter} target={config.RedirectTarget}");
        PrintStats(device.GetUdpProbeStats());
        return 0;
    }

    public static int PrintStats()
    {
        using var device = new WfpDevice();
        PrintStats(device.GetUdpProbeStats());
        return 0;
    }

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
        bool inject = relay && options.Has("inject");
        using WfpDevice? device = inject ? new WfpDevice() : null;
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
            string records = datagram.RedirectRecords is { } recordBytes ? $"{recordBytes.Length}B" : "none";
            Console.WriteLine($"[udp] #{index} from={datagram.Source} bytes={datagram.Payload.Length} " +
                              $"context={context} records={records}");
            Console.WriteLine($"[udp] #{index} payload={Encoding.UTF8.GetString(datagram.Payload)}");

            if (relay)
            {
                if (inject)
                {
                    InjectReturn(device!, datagram, options);
                }
                else
                {
                    socket.SendTo(datagram.Payload, datagram.Source);
                    Console.WriteLine($"[udp] #{index} echoed to {datagram.Source}");
                }
            }
        }

        return 0;
}

    private static unsafe void InjectReturn(WfpDevice device, ReceivedDatagram datagram, Options options)
    {
        if (datagram.RedirectContext is not { Length: >= 36 } raw)
        {
            Console.WriteLine("[udp] inject skipped: no redirect context");
            return;
        }

        RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
        byte[] original = new byte[4];
        for (int index = 0; index < 4; index++)
            original[index] = context.OriginalAddress[index];

        UdpProbeReturnHeader header = new()
        {
            Version = 1,
            AppAddress = MemoryMarshal.Read<uint>(datagram.Source.Address.GetAddressBytes()),
            OriginalAddress = MemoryMarshal.Read<uint>(original),
            AppPort = (ushort)datagram.Source.Port,
            OriginalPort = context.OriginalPort,
            PayloadLength = (ushort)datagram.Payload.Length,
            Family = 4,
            InterfaceIndex = (uint)options.GetInt("interface", 1),
            CompartmentId = 0,
        };

        device.SendUdpReturn(header, datagram.Payload);
        Console.WriteLine($"[udp] injected original={new IPAddress(original)}:{context.OriginalPort} " +
                          $"app={datagram.Source} bytes={datagram.Payload.Length}");
    }

    private static byte ParseReturnMode(string text) => text switch
    {
        "off" => 0,
        "observe" => 1,
        "rewrite-inline" => 2,
        "rewrite-inject" => 3,
        _ => throw new ArgumentException($"未知 return-mode：{text}"),
    };

    private static IPEndPoint ParseEndPoint(string text)
    {
        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"地址应为 HOST:PORT：{text}");
        return new IPEndPoint(IPAddress.Parse(text[..separator]),
                              int.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture));
    }

    private static void PrintStats(UdpProbeStats stats) =>
        Console.WriteLine($"[udp-probe] seen={stats.Seen} redirected={stats.Redirected} " +
                          $"redirect-failures={stats.RedirectFailures} return-seen={stats.ReturnSeen} " +
                          $"return-matched={stats.ReturnMatched} return-rewritten={stats.ReturnRewritten} " +
                          $"return-failures={stats.ReturnFailures} ippacket-seen={stats.IppacketSeen} " +
                          $"ippacket-matched={stats.IppacketMatched} table={stats.TableEntries} " +
                          $"last-status=0x{stats.LastStatus:X8}");
}
