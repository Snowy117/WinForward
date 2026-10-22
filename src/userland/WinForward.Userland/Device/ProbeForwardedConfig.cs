using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_config</c> 的逐字段镜像：28 字节，Pack = 4。
/// enabled=1 时 listener_address/listener_port 是 DNAT 目标；关闭配置会清空驱动里的 redirect 表。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeForwardedConfig
{
    public uint Enabled;
    public ushort ListenerPort;
    public byte Family;
    public byte Reserved0;
    public AddressBytes ListenerAddress;
    public uint Reserved1;

    [InlineArray(16)]
    public struct AddressBytes
    {
        private byte _element0;
    }

    public static ProbeForwardedConfig Disabled(ushort listenerPort = 0, byte family = 0) => new()
    {
        ListenerPort = listenerPort,
        Family = family,
    };

    /// <summary>回读用：按 family 把 listener_address 还原成 IPAddress；未配置族时返回 null。</summary>
    public readonly IPAddress? ListenerIP
    {
        get
        {
            if (Family != 4 && Family != 6)
            {
                return null;
            }

            AddressBytes address = ListenerAddress;
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref address, 1));
            return Family == 4 ? new IPAddress(bytes[..4]) : new IPAddress(bytes);
        }
    }

    /// <summary>把 IPv4/IPv6 地址写进 16 字节存储；IPv4 的其余 12 字节保持 0。</summary>
    public static ProbeForwardedConfig EnabledFor(ushort listenerPort, IPAddress listenerAddress)
    {
        ArgumentNullException.ThrowIfNull(listenerAddress);
        if (listenerPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPort), listenerPort, "listener 端口不能为 0");
        }

        byte family = (byte)(listenerAddress.AddressFamily == AddressFamily.InterNetwork ? 4
                                 : listenerAddress.AddressFamily == AddressFamily.InterNetworkV6 ? 6
                                 : throw new ArgumentException("listener 地址只能是 IPv4/IPv6", nameof(listenerAddress)));
        ProbeForwardedConfig config = default;
        config.Enabled = 1;
        config.ListenerPort = listenerPort;
        config.Family = family;
        listenerAddress.GetAddressBytes().CopyTo(config.ListenerAddress);
        return config;
    }
}
