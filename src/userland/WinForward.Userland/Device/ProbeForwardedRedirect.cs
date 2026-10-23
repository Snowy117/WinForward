using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_redirect_query</c> 的逐字段镜像：24 字节，Pack = 4。
/// 按 accepted socket 的本地端口（listener）与对端（client）查询 redirect 表。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeForwardedRedirectQuery
{
    public ushort ListenerPort;
    public byte Family;
    public byte Reserved0;
    public AddressBytes ClientAddress;
    public ushort ClientPort;
    public byte Protocol; // 0/6 = TCP（0 是 B4b 兼容值），17 = UDP
    public byte Reserved1;

    [InlineArray(16)]
    public struct AddressBytes
    {
        private byte _element0;
    }

    public static ProbeForwardedRedirectQuery ForClient(ushort listenerPort, IPEndPoint client)
    {
        return ForClient(listenerPort, client, RedirectContext.ProtocolTcp);
    }

    /// <summary>UDP 用：同一个 listener 端口上的 TCP/UDP 表项按 protocol 区分。</summary>
    public static ProbeForwardedRedirectQuery ForClientUdp(ushort listenerPort, IPEndPoint client) =>
        ForClient(listenerPort, client, RedirectContext.ProtocolUdp);

    private static ProbeForwardedRedirectQuery ForClient(ushort listenerPort, IPEndPoint client, byte protocol)
    {
        ArgumentNullException.ThrowIfNull(client);
        byte family = (byte)(client.AddressFamily == AddressFamily.InterNetwork ? 4
                               : client.AddressFamily == AddressFamily.InterNetworkV6 ? 6
                               : throw new ArgumentException("client 地址只能是 IPv4/IPv6", nameof(client)));
        ProbeForwardedRedirectQuery query = default;
        query.ListenerPort = listenerPort;
        query.Family = family;
        query.Protocol = protocol;
        query.ClientPort = (ushort)client.Port;
        client.Address.GetAddressBytes().CopyTo(query.ClientAddress);
        return query;
    }
}

/// <summary>
/// <c>probe_forwarded_redirect</c> 的逐字段镜像：64 字节，Pack = 4。
/// magic/version 用于在解析字段前先认版本；地址按原样保存（IPv4 只用前 4 字节）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeForwardedRedirect
{
    public const uint MagicValue = 0x31524657; // 内存中 "WFR1"
    public const ushort VersionValue = 1;

    public const uint StateSyn = 1;
    public const uint StateEstablished = 2;
    public const uint StateClosing = 3;

    public uint Magic;
    public ushort Version;
    public byte Family;
    public byte Protocol;
    public AddressBytes ClientAddress;
    public ushort ClientPort;
    public AddressBytes ServerAddress;
    public ushort ServerPort;
    public ushort ListenerPort;
    public ushort Reserved0;
    public uint IngressIfIndex;
    public uint UpstreamId;
    public uint RuleIndex;
    public uint State;

    [InlineArray(16)]
    public struct AddressBytes
    {
        private byte _element0;
    }

    public readonly IPAddress ClientIP => AddressOf(ClientAddress, Family);

    public readonly IPAddress ServerIP => AddressOf(ServerAddress, Family);

    public readonly string StateText => State switch
    {
        StateSyn => "syn",
        StateEstablished => "established",
        StateClosing => "closing",
        _ => $"state-{State}",
    };

    public override readonly string ToString() =>
        $"client={ClientIP}:{ClientPort} server={ServerIP}:{ServerPort} listener={ListenerPort} " +
        $"upstream={UpstreamId} rule={RuleIndex} ingress={IngressIfIndex} state={StateText}";

    private static IPAddress AddressOf(AddressBytes address, byte family)
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref address, 1));
        return family == 4 ? new IPAddress(bytes[..4]) : new IPAddress(bytes);
    }
}
