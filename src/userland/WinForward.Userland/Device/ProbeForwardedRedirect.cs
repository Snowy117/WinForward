using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_redirect_query</c> 的逐字段镜像：40 字节，Pack = 4。
/// 按 accepted socket 的本地 (L, listener 端口) 与对端（client）查询 redirect 表。
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
    public AddressBytes ListenerAddress;

    [InlineArray(16)]
    public struct AddressBytes
    {
        private byte _element0;
    }

    public static ProbeForwardedRedirectQuery ForClient(IPEndPoint listener, IPEndPoint client) =>
        ForClient(listener, client, RedirectContext.ProtocolTcp);

    /// <summary>UDP 用：同一个 listener 端口上的 TCP/UDP 表项按 protocol 区分。</summary>
    public static ProbeForwardedRedirectQuery ForClientUdp(IPEndPoint listener, IPEndPoint client) =>
        ForClient(listener, client, RedirectContext.ProtocolUdp);

    private static ProbeForwardedRedirectQuery ForClient(IPEndPoint listener, IPEndPoint client, byte protocol)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(client);
        byte family = (byte)(client.AddressFamily == AddressFamily.InterNetwork ? 4
                               : client.AddressFamily == AddressFamily.InterNetworkV6 ? 6
                               : throw new ArgumentException("client 地址只能是 IPv4/IPv6", nameof(client)));

        ProbeForwardedRedirectQuery query = default;
        query.ListenerPort = (ushort)listener.Port;
        query.Family = family;
        query.Protocol = protocol;
        query.ClientPort = (ushort)client.Port;
        client.Address.GetAddressBytes().CopyTo(query.ClientAddress);
        // 通配 listener（0.0.0.0/[::]）没有可用的 L，保持全 0：驱动按“任意 L”查询。
        if (!IsWildcard(listener.Address))
        {
            if (ProbeForwardedIngress.FamilyOf(listener.Address) != family)
            {
                throw new ArgumentException("listener 与 client 的地址族不一致", nameof(listener));
            }

            listener.Address.GetAddressBytes().CopyTo(query.ListenerAddress);
        }
        return query;
    }

    private static bool IsWildcard(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
}

/// <summary>
/// <c>probe_forwarded_redirect</c> 的逐字段镜像：88 字节，Pack = 4。
/// magic/version 用于在解析字段前先认版本；v2 追加 listener 地址 L 与建表 generation。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeForwardedRedirect
{
    public const uint MagicValue = 0x31524657; // 内存中 "WFR1"
    public const ushort VersionValue = 2;
    public const ushort LegacyVersionValue = 1;

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
    public AddressBytes ListenerAddress;
    public uint Generation;
    public uint Reserved1;

    [InlineArray(16)]
    public struct AddressBytes
    {
        private byte _element0;
    }

    public readonly IPAddress ClientIP => AddressOf(ClientAddress, Family);

    public readonly IPAddress ServerIP => AddressOf(ServerAddress, Family);

    public readonly IPAddress? ListenerIP =>
        Version >= VersionValue && (Family == 4 || Family == 6) ? AddressOf(ListenerAddress, Family) : null;

    public readonly string StateText => State switch
    {
        StateSyn => "syn",
        StateEstablished => "established",
        StateClosing => "closing",
        _ => $"state-{State}",
    };

    public override readonly string ToString() =>
        $"client={ClientIP}:{ClientPort} server={ServerIP}:{ServerPort} listener={ListenerIP}:{ListenerPort} " +
        $"upstream={UpstreamId} rule={RuleIndex} ingress={IngressIfIndex} generation={Generation} state={StateText}";

    internal static IPAddress AddressOf(AddressBytes address, byte family)
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref address, 1));
        return family == 4 ? new IPAddress(bytes[..4]) : new IPAddress(bytes);
    }
}
