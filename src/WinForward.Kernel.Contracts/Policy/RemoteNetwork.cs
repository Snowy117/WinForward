using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 远端网段（CIDR）：地址族、前缀长度与 16 字节规范地址。主机位在构造时清零，
/// 因此同一网段的不同写法会归一成同一条 20 字节记录。
/// </summary>
public sealed class RemoteNetwork
{
    internal const int AddressSize = 16;
    internal const int Ipv4PrefixLimit = 32;
    internal const int Ipv6PrefixLimit = 128;

    private readonly byte[] _address;

    private RemoteNetwork(RuleAddressFamily family, int prefixLength, byte[] address)
    {
        Family = family;
        PrefixLength = prefixLength;
        _address = address;
    }

    public RuleAddressFamily Family { get; }

    public int PrefixLength { get; }

    /// <summary>16 字节规范地址，IPv4 只用前 4 字节，其余补零。</summary>
    public ReadOnlySpan<byte> Address => _address;

    public static RemoteNetwork Parse(string cidr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);

        var slash = cidr.LastIndexOf('/');
        if (slash <= 0 || slash == cidr.Length - 1)
        {
            throw new ArgumentException($"CIDR 应写成 <address>/<prefix>：{cidr}", nameof(cidr));
        }

        if (!IPAddress.TryParse(cidr[..slash], out var address))
        {
            throw new ArgumentException($"CIDR 的地址部分无法解析：{cidr}", nameof(cidr));
        }

        if (!int.TryParse(cidr[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength))
        {
            throw new ArgumentException($"CIDR 的前缀长度不是十进制整数：{cidr}", nameof(cidr));
        }

        return Create(address, prefixLength);
    }

    public static RemoteNetwork Create(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        var family = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => RuleAddressFamily.Ipv4,
            AddressFamily.InterNetworkV6 => RuleAddressFamily.Ipv6,
            _ => throw new ArgumentException($"不支持的地址族 {address.AddressFamily}", nameof(address)),
        };

        var limit = family == RuleAddressFamily.Ipv4 ? Ipv4PrefixLimit : Ipv6PrefixLimit;
        if (prefixLength < 0 || prefixLength > limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(prefixLength),
                prefixLength,
                string.Create(CultureInfo.InvariantCulture,
                              $"{(byte)family} 族的前缀长度必须在 0..{limit}"));
        }

        var storage = new byte[AddressSize];
        address.GetAddressBytes().CopyTo(storage, 0);
        MaskHostBits(storage, prefixLength);
        return new RemoteNetwork(family, prefixLength, storage);
    }


    private static void MaskHostBits(byte[] address, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            address[fullBytes] &= (byte)(0xFF << (8 - remainingBits));
            fullBytes++;
        }

        for (var index = fullBytes; index < address.Length; index++)
        {
            address[index] = 0;
        }
    }

    public override string ToString() => $"{FormatAddress()}/{PrefixLength}";

    private string FormatAddress()
    {
        // IPv4 存在前 4 字节：直接把 16 字节交给 IPAddress 会被当成 IPv6。
        ReadOnlySpan<byte> slice = _address.AsSpan(0, Family == RuleAddressFamily.Ipv4 ? 4 : AddressSize);
        return new IPAddress(slice).ToString();
    }
}
