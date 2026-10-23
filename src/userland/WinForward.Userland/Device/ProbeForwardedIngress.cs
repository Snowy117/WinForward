using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_forwarded_ingress</c> 的逐字段镜像：28 字节，Pack = 4。ifindex 可以是
/// <see cref="ProbeIoctl.ForwardedIngressAnyIfIndex"/>，表示没有精确入口记录时使用的通配映射。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeForwardedIngress
{
    public uint IfIndex;
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

    public static ProbeForwardedIngress For(IPAddress listenerAddress, ushort listenerPort, uint ifIndex)
    {
        ArgumentNullException.ThrowIfNull(listenerAddress);
        if (listenerPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPort), listenerPort, "listener 端口不能为 0");
        }

        ProbeForwardedIngress ingress = default;
        ingress.IfIndex = ifIndex;
        ingress.ListenerPort = listenerPort;
        ingress.Family = FamilyOf(listenerAddress);
        listenerAddress.GetAddressBytes().CopyTo(ingress.ListenerAddress);
        return ingress;
    }

    internal static byte FamilyOf(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? (byte)4
        : address.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)6
        : throw new ArgumentException("listener 地址只能是 IPv4/IPv6", nameof(address));
}

/// <summary><c>probe_forwarded_ingress_config</c> 的 16 字节头，Pack = 4。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeForwardedIngressConfigHeader
{
    public uint AbiVersion;
    public uint Enabled;
    public uint IngressCount;
    public uint Reserved0;
}

/// <summary>
/// <c>probe_forwarded_ingress_config</c> 的编解码：16 字节头 + 0..16 条 28 字节记录。
/// 记录不是结构体数组字段，而是紧跟头部连续存放，因此收发都走显式序列化。
/// </summary>
public sealed class ProbeForwardedIngressConfig

{
    public const int HeaderSize = 16;
    public const int IngressSize = 28;
    public const int MaxIngresses = (int)ProbeIoctl.ForwardedIngressCapacity;
    public const int MaxBytes = HeaderSize + (MaxIngresses * IngressSize);

    public bool Enabled { get; init; }

    public IReadOnlyList<ProbeForwardedIngress> Ingresses { get; init; } = Array.Empty<ProbeForwardedIngress>();

    public uint AbiVersion { get; init; } = ProbeIoctl.AbiVersion;

    public static ProbeForwardedIngressConfig Disabled() => new() { Enabled = false };

    public static ProbeForwardedIngressConfig EnabledFor(IReadOnlyList<ProbeForwardedIngress> ingresses)
    {
        ArgumentNullException.ThrowIfNull(ingresses);
        if (ingresses.Count > MaxIngresses)
        {
            throw new ArgumentOutOfRangeException(nameof(ingresses), ingresses.Count,
                                                  $"入口记录不能超过 {MaxIngresses} 条");
        }

        return new ProbeForwardedIngressConfig { Enabled = true, Ingresses = ingresses.ToArray() };
    }

    public byte[] ToBytes()
    {
        if (Ingresses.Count > MaxIngresses)
        {
            throw new InvalidOperationException($"入口记录不能超过 {MaxIngresses} 条");
        }

        byte[] bytes = new byte[HeaderSize + (Ingresses.Count * IngressSize)];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), AbiVersion);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), Enabled ? 1u : 0u);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), (uint)Ingresses.Count);
        BitConverter.TryWriteBytes(bytes.AsSpan(12, 4), 0u);
        for (int index = 0; index < Ingresses.Count; index++)
        {
            ProbeForwardedIngress ingress = Ingresses[index];
            MemoryMarshal.Write(bytes.AsSpan(HeaderSize + (index * IngressSize), IngressSize), in ingress);

        }
        return bytes;
    }

    public static ProbeForwardedIngressConfig FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            throw new InvalidOperationException($"forwarded 入口配置只有 {bytes.Length} 字节，期望至少 {HeaderSize}");
        }

        uint abiVersion = BitConverter.ToUInt32(bytes[..4]);
        uint enabled = BitConverter.ToUInt32(bytes.Slice(4, 4));
        uint count = BitConverter.ToUInt32(bytes.Slice(8, 4));
        if (count > MaxIngresses)
        {
            throw new InvalidOperationException($"驱动回读入口记录数 {count} 超过上限 {MaxIngresses}");
        }

        int expected = HeaderSize + ((int)count * IngressSize);
        if (bytes.Length < expected)
        {
            throw new InvalidOperationException(
                $"forwarded 入口配置只有 {bytes.Length} 字节，{count} 条记录需要 {expected}");
        }

        var ingresses = new ProbeForwardedIngress[count];
        for (int index = 0; index < count; index++)
        {
            ingresses[index] = MemoryMarshal.Read<ProbeForwardedIngress>(
                bytes.Slice(HeaderSize + (index * IngressSize), IngressSize));
        }

        return new ProbeForwardedIngressConfig
        {
            AbiVersion = abiVersion,
            Enabled = enabled != 0,
            Ingresses = ingresses,
        };
    }
}
