using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace WinForward.Probe;

// forwarded_blob.h v1 的测试侧构造器。只保证字节布局与 ABI 合法；
// 规则顺序、CIDR 语义等由用例自己控制（驱动侧是 first-match，不做顺序校验）。
internal sealed class ForwardedTestRule
{
    public uint Action { get; set; } = Protocol.RuleActionPermit;
    public uint IngressAdapterId { get; set; } = ForwardedBlobBuilder.AdapterAny;
    public string? ClientCidr { get; set; }
    public string? RemoteCidr { get; set; }
    public ushort PortStart { get; set; }
    public ushort PortEnd { get; set; } = 0xFFFF;
    public byte TransportProtocol { get; set; } = Protocol.RuleProtocolAny;
    public byte Family { get; set; }
    public uint UpstreamId { get; set; } = ForwardedBlobBuilder.UpstreamNone;

    public static ForwardedTestRule Permit(uint ingressAdapterId) => new()
    {
        Action = Protocol.RuleActionPermit,
        IngressAdapterId = ingressAdapterId,
    };

    public static ForwardedTestRule Block(uint ingressAdapterId) => new()
    {
        Action = Protocol.RuleActionBlock,
        IngressAdapterId = ingressAdapterId,
    };
}

internal static class ForwardedBlobBuilder
{
    public const uint Magic = 0x31424657; // 内存中 "WFB1"
    public const ushort Version = 1;
    public const int HeaderSize = 40;
    public const int RuleSize = 24;
    public const int NetworkPrefixSize = 20;
    public const uint PoolIndexAny = 0xFFFFFFFF;
    public const uint AdapterAny = 0xFFFFFFFF;
    public const uint UpstreamNone = 0xFFFFFFFF;

    public static byte[] Build(IReadOnlyList<ForwardedTestRule> rules, uint fallbackAction)
    {
        var networks = new List<byte[]>();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        byte[] entries = new byte[rules.Count * RuleSize];

        for (int index = 0; index < rules.Count; index++)
        {
            ForwardedTestRule rule = rules[index];
            uint clientIndex = PoolIndexAny;
            if (rule.ClientCidr is not null)
                clientIndex = AddNetwork(networks, offsets, rule.ClientCidr, rule.Family);
            uint remoteIndex = PoolIndexAny;
            if (rule.RemoteCidr is not null)
                remoteIndex = AddNetwork(networks, offsets, rule.RemoteCidr, rule.Family);

            Span<byte> entry = entries.AsSpan(index * RuleSize, RuleSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0..], rule.IngressAdapterId);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], clientIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], remoteIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[12..], rule.PortStart);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[14..], rule.PortEnd);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], rule.UpstreamId);
            entry[20] = rule.TransportProtocol;
            entry[21] = rule.Family;
            entry[22] = (byte)rule.Action;
            entry[23] = 0;
        }

        int rulesOffset = HeaderSize;
        int networkOffset = AlignUp(rulesOffset + entries.Length, 4);
        int networkSize = networks.Count * NetworkPrefixSize;
        int totalSize = networkOffset + networkSize;
        byte[] blob = new byte[totalSize];

        Span<byte> header = blob.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)rules.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], fallbackAction);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)rulesOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)networkOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)networkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], 0);

        entries.CopyTo(blob.AsSpan(rulesOffset));
        for (int index = 0; index < networks.Count; index++)
            networks[index].CopyTo(blob.AsSpan(networkOffset + (index * NetworkPrefixSize)));

        return blob;
    }

    public static byte[] BuildEmpty(uint fallbackAction) => Build(Array.Empty<ForwardedTestRule>(), fallbackAction);

    private static uint AddNetwork(List<byte[]> networks,
                                   Dictionary<string, uint> offsets,
                                   string cidr,
                                   byte ruleFamily)
    {
        byte[] entry = BuildNetwork(cidr, out byte family);
        if (ruleFamily != 0 && ruleFamily != family)
            throw new ArgumentException($"规则族 {ruleFamily} 与网段族 {family} 不一致：{cidr}");
        string key = Convert.ToHexString(entry);
        if (!offsets.TryGetValue(key, out uint offset))
        {
            offset = (uint)(networks.Count * NetworkPrefixSize);
            offsets.Add(key, offset);
            networks.Add(entry);
        }
        return offset;
    }

    private static byte[] BuildNetwork(string cidr, out byte family)
    {
        int slash = cidr.LastIndexOf('/');
        if (slash <= 0)
            throw new ArgumentException($"CIDR 应为 address/prefix：{cidr}");
        IPAddress address = IPAddress.Parse(cidr[..slash]);
        int prefixLength = int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture);
        byte[] bytes = address.GetAddressBytes();
        family = (byte)(bytes.Length == 4 ? 4 : 6);
        int maxBits = bytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxBits)
            throw new ArgumentException($"前缀长度越界：{cidr}");

        byte[] network = new byte[16];
        bytes.CopyTo(network, 0);
        for (int bit = prefixLength; bit < maxBits; bit++)
            network[bit / 8] &= (byte)~(0x80 >> (bit % 8));

        byte[] entry = new byte[NetworkPrefixSize];
        entry[0] = (byte)prefixLength;
        entry[1] = family;
        network.CopyTo(entry, 4);
        return entry;
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
