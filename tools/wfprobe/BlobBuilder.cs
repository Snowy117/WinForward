using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace WinForward.Probe;

// 测试用的规则描述。内核侧只认 blob，不解释语义；这里负责把语义编译成 rule_blob v1。
internal sealed class TestRule
{
    public uint Action { get; set; } = Protocol.RuleActionPermit;
    public ushort PortStart { get; set; }
    public ushort PortEnd { get; set; } = 0xFFFF;
    public byte TransportProtocol { get; set; } = 6;
    public byte Family { get; set; }
    public string? Process { get; set; }
    public string? Network { get; set; }

    public static TestRule Port(uint action, int port, string? process = null, byte transportProtocol = Protocol.RuleProtocolTcp) => new()
    {
        Action = action,
        PortStart = (ushort)port,
        PortEnd = (ushort)port,
        Process = process,
        TransportProtocol = transportProtocol,
    };

    public static TestRule RedirectPort(int port, string? process = null, byte transportProtocol = Protocol.RuleProtocolTcp) =>
        Port(Protocol.RuleActionRedirect, port, process, transportProtocol);

    public static TestRule BlockPort(int port, string? process = null, byte transportProtocol = Protocol.RuleProtocolTcp) =>
        Port(Protocol.RuleActionBlock, port, process, transportProtocol);

    public static TestRule PermitPort(int port, string? process = null, byte transportProtocol = Protocol.RuleProtocolTcp) =>
        Port(Protocol.RuleActionPermit, port, process, transportProtocol);

}

internal static class BlobBuilder
{
    public const uint Magic = 0x31425257; // 内存中 "WRB1"
    public const ushort Version = 1;
    public const int HeaderSize = 40;
    public const int RuleSize = 16;
    public const int NetworkPrefixSize = 20;
    public const uint PoolIndexAny = 0xFFFFFFFF;

    public static byte[] Build(IReadOnlyList<TestRule> rules, uint fallbackAction)
    {
        ValidateProcessSelectors(rules);
        ValidateFallback(rules, fallbackAction);
        ValidateRuleOrder(rules);
        return BuildUnchecked(rules, fallbackAction);
    }

    // 跳过顺序校验：只给“驱动必须独立拒绝冲突 blob”的负向用例使用。
    public static byte[] BuildUnchecked(IReadOnlyList<TestRule> rules, uint fallbackAction)
    {
        var processOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var processBytes = new List<byte>();
        var networkEntries = new List<byte[]>();
        var networkOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        byte[] entries = new byte[rules.Count * RuleSize];

        for (int index = 0; index < rules.Count; index++)
        {
            TestRule rule = rules[index];
            uint processIndex = PoolIndexAny;
            if (rule.Process is not null)
            {
                if (!processOffsets.TryGetValue(rule.Process, out processIndex))
                {
                    processIndex = (uint)processBytes.Count;
                    processOffsets.Add(rule.Process, processIndex);
                    processBytes.AddRange(Encoding.UTF8.GetBytes(rule.Process));
                    processBytes.Add(0);
                }
            }

            uint networkIndex = PoolIndexAny;
            if (rule.Network is not null)
            {
                byte[] entry = BuildNetwork(rule.Network, out byte family);
                if (rule.Family == 0)
                    rule.Family = family;
                if (rule.Family != family)
                    throw new ArgumentException($"规则族 {rule.Family} 与网段族 {family} 不一致");
                string key = Convert.ToHexString(entry);
                if (!networkOffsets.TryGetValue(key, out networkIndex))
                {
                    networkIndex = (uint)(networkEntries.Count * NetworkPrefixSize);
                    networkOffsets.Add(key, networkIndex);
                    networkEntries.Add(entry);
                }
            }

            Span<byte> span = entries.AsSpan(index * RuleSize, RuleSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span, processIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], networkIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], rule.PortStart);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], rule.PortEnd);
            span[12] = rule.TransportProtocol;
            span[13] = rule.Family;
            span[14] = (byte)rule.Action;
            span[15] = 0;
        }

        int rulesOffset = HeaderSize;
        int processOffset = rulesOffset + entries.Length;
        int networkOffset = AlignUp(processOffset + processBytes.Count, 4);
        int networkSize = networkEntries.Count * NetworkPrefixSize;
        int totalSize = networkOffset + networkSize;

        byte[] blob = new byte[totalSize];
        WriteUInt32(blob, 0, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(6), HeaderSize);
        WriteUInt32(blob, 8, (uint)totalSize);
        WriteUInt32(blob, 12, (uint)rules.Count);
        WriteUInt32(blob, 16, fallbackAction);
        WriteUInt32(blob, 20, (uint)rulesOffset);
        WriteUInt32(blob, 24, (uint)processOffset);
        WriteUInt32(blob, 28, (uint)processBytes.Count);
        WriteUInt32(blob, 32, (uint)networkOffset);
        WriteUInt32(blob, 36, (uint)networkSize);

        entries.CopyTo(blob, rulesOffset);
        processBytes.CopyTo(blob, processOffset);
        for (int index = 0; index < networkEntries.Count; index++)
            networkEntries[index].CopyTo(blob, networkOffset + (index * NetworkPrefixSize));
        return blob;
    }

    private static void ValidateProcessSelectors(IReadOnlyList<TestRule> rules)
    {
        foreach (TestRule rule in rules)
        {
            if (rule.Process is null)
                continue;
            int bytes = Encoding.UTF8.GetByteCount(rule.Process);
            if (bytes > 511)
            {
                throw new InvalidOperationException(
                    $"进程选择器 UTF-8 长度 {bytes} 超过上限 511 字节（不含结尾 NUL）：{rule.Process}。" +
                    "内核会把它整条按不匹配处理，规则会永久静默不生效。");
            }
        }
    }

    private static void ValidateFallback(IReadOnlyList<TestRule> rules, uint fallbackAction)
    {
        if (fallbackAction != Protocol.RuleActionBlock)
            return;
        for (int index = 0; index < rules.Count; index++)
        {
            if (rules[index].Action == Protocol.RuleActionBlock)
                continue;
            throw new InvalidOperationException(
                $"fallback=block 会生成无条件的 block filter，与规则 #{index}（{Describe(rules[index])}）冲突：" +
                "那条规则永远不会生效。请去掉非 block 规则，或把 fallback 改回 permit。");
        }
    }

    private static void ValidateRuleOrder(IReadOnlyList<TestRule> rules)
    {
        for (int later = 0; later < rules.Count; later++)
        {
            if (rules[later].Action != Protocol.RuleActionBlock)
                continue;
            for (int earlier = 0; earlier < later; earlier++)
            {
                if (rules[earlier].Action == Protocol.RuleActionBlock)
                    continue;
                if (RulesOverlap(rules[earlier], rules[later]))
                {
                    throw new InvalidOperationException(
                        $"规则 #{earlier}（{Describe(rules[earlier])}）会先于 block 规则 #{later}" +
                        $"（{Describe(rules[later])}）命中同一批连接；block 在驱动里编译成独立 filter，" +
                        "无法保持“首个命中”的语义。请收窄条件或调整顺序。");
                }
            }
        }
    }

    private static string Describe(TestRule rule) =>
        $"action={rule.Action} port={rule.PortStart}-{rule.PortEnd} protocol={rule.TransportProtocol} " +
        $"family={rule.Family} process={rule.Process ?? "any"} network={rule.Network ?? "any"}";

    private static bool RulesOverlap(TestRule first, TestRule second) =>
        ProtocolsOverlap(first, second) && FamiliesOverlap(first, second) && PortsOverlap(first, second) &&
        NetworksOverlap(first, second) && ProcessesOverlap(first, second);

    private static bool ProtocolsOverlap(TestRule first, TestRule second) =>
        first.TransportProtocol == 0 || second.TransportProtocol == 0 ||
        first.TransportProtocol == second.TransportProtocol;

    private static byte EffectiveFamily(TestRule rule)
    {
        if (rule.Family != 0)
            return rule.Family;
        if (rule.Network is null)
            return 0;
        return ParseNetwork(rule.Network).Family;
    }

    private static bool FamiliesOverlap(TestRule first, TestRule second)
    {
        byte firstFamily = EffectiveFamily(first);
        byte secondFamily = EffectiveFamily(second);
        return firstFamily == 0 || secondFamily == 0 || firstFamily == secondFamily;
    }

    private static bool PortsOverlap(TestRule first, TestRule second) =>
        first.PortStart <= second.PortEnd && second.PortStart <= first.PortEnd;

    private static bool NetworksOverlap(TestRule first, TestRule second)
    {
        if (first.Network is null || second.Network is null)
            return true;
        (byte firstFamily, int firstPrefix, byte[] firstBytes) = ParseNetwork(first.Network);
        (byte secondFamily, int secondPrefix, byte[] secondBytes) = ParseNetwork(second.Network);
        if (firstFamily != secondFamily)
            return false;
        (int shorterPrefix, byte[] shorterBytes, byte[] longerBytes) =
            firstPrefix <= secondPrefix ? (firstPrefix, firstBytes, secondBytes) : (secondPrefix, secondBytes, firstBytes);
        int fullBytes = shorterPrefix / 8;
        for (int index = 0; index < fullBytes; index++)
        {
            if (shorterBytes[index] != longerBytes[index])
                return false;
        }
        int remaining = shorterPrefix % 8;
        if (remaining == 0)
            return true;
        int mask = 0xFF << (8 - remaining);
        return ((shorterBytes[fullBytes] ^ longerBytes[fullBytes]) & mask) == 0;
    }

    private static bool ProcessesOverlap(TestRule first, TestRule second)
    {
        if (first.Process is null || second.Process is null)
            return true;
        // 与内核 matcher 一致：只折叠 ASCII 大小写。
        return string.Equals(AsciiLower(first.Process), AsciiLower(second.Process), StringComparison.Ordinal);
    }

    private static string AsciiLower(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char character in text)
            builder.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        return builder.ToString();
    }

    private static (byte Family, int Prefix, byte[] Bytes) ParseNetwork(string cidr)
    {
        int slash = cidr.IndexOf('/');
        if (slash <= 0)
            throw new ArgumentException($"网段应为 CIDR：{cidr}");
        IPAddress address = IPAddress.Parse(cidr[..slash]);
        int prefix = int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture);
        byte[] bytes = address.GetAddressBytes();
        return (bytes.Length == 4 ? (byte)4 : (byte)6, prefix, bytes);
    }

    private static byte[] BuildNetwork(string cidr, out byte family)
    {
        int slash = cidr.IndexOf('/');
        if (slash <= 0)
            throw new ArgumentException($"网段应为 CIDR：{cidr}");
        IPAddress address = IPAddress.Parse(cidr[..slash]);
        int prefixLength = int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture);
        byte[] bytes = address.GetAddressBytes();
        family = bytes.Length == 4 ? (byte)4 : (byte)6;
        if (prefixLength < 0 || prefixLength > (bytes.Length * 8))
            throw new ArgumentException($"前缀长度越界：{cidr}");
        byte[] entry = new byte[NetworkPrefixSize];
        entry[0] = (byte)prefixLength;
        entry[1] = family;
        bytes.CopyTo(entry, 4);
        return entry;
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void WriteUInt32(byte[] blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offset), value);

    // A2 的非法变体：每个只违反一条约束。
    public static byte[] CorruptMagic(byte[] blob) => Mutate(blob, copy => copy[0] ^= 0xFF);

    public static byte[] CorruptVersion(byte[] blob) =>
        Mutate(blob, copy => BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(4), 2));

    public static byte[] CorruptHeaderSize(byte[] blob) =>
        Mutate(blob, copy => BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(6), 44));

    public static byte[] CorruptFallback(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 16, 3));

    public static byte[] Truncated(byte[] blob) => blob[..^4];

    public static byte[] ShortBlob(byte[] blob) => blob[..20];

    public static byte[] RulesOffsetOutOfRange(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 20, (uint)blob.Length + 8));

    public static byte[] RulesOffsetUnaligned(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 20, 42));

    public static byte[] ProcessPoolOutOfRange(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 24, (uint)blob.Length + 4));

    public static byte[] NetworkPoolOutOfRange(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 32, (uint)blob.Length + 4));

    public static byte[] ProcessPoolUnterminated(byte[] blob) =>
        Mutate(blob, copy =>
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(24));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(28));
            if (size > 0)
                copy[offset + size - 1] = (byte)'x';
        });

    private static byte[] Mutate(byte[] blob, Action<byte[]> mutation)
    {
        byte[] copy = (byte[])blob.Clone();
        mutation(copy);
        return copy;
    }
}
