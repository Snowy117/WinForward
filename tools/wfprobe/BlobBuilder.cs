using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace WinForward.Probe;

internal readonly record struct TestProcessSelector(string Value, byte Kind, bool IsDirectory)
{
    public static TestProcessSelector FileName(string value) =>
        new(value, Protocol.ProcessSelectorKindFileName, false);

    public static TestProcessSelector Path(string value) =>
        new(value, Protocol.ProcessSelectorKindPath, false);

    public static TestProcessSelector Directory(string value) =>
        new(value, Protocol.ProcessSelectorKindPath, true);
}

internal readonly record struct TestPortRange(ushort Start, ushort End)
{
    public static TestPortRange Single(int port) => new((ushort)port, (ushort)port);

    public static TestPortRange Between(int start, int end) => new((ushort)start, (ushort)end);
}

internal sealed class TestRule
{
    public uint Action { get; set; } = Protocol.RuleActionPermit;
    public ushort PortStart { get; set; }
    public ushort PortEnd { get; set; } = 0xFFFF;
    public byte TransportProtocol { get; set; } = Protocol.RuleProtocolTcp;
    public byte Family { get; set; }
    public string? Process { get; set; }
    public string? Network { get; set; }

    public IReadOnlyList<TestProcessSelector>? Processes { get; set; }
    public IReadOnlyList<string>? Networks { get; set; }
    public IReadOnlyList<TestPortRange>? Ports { get; set; }
    public IReadOnlyList<byte>? Protocols { get; set; }
    public IReadOnlyList<byte>? Families { get; set; }

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
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] Build(IReadOnlyList<TestRule> rules, uint fallbackAction)
    {
        List<FixtureRule> compiled = Compile(rules, validateSelectors: true);
        RuleBlobOrder.Validate(compiled, fallbackAction);
        return Write(compiled, fallbackAction);
    }

    public static byte[] BuildUnchecked(IReadOnlyList<TestRule> rules, uint fallbackAction) =>
        Write(Compile(rules, validateSelectors: false), fallbackAction);

    private static List<FixtureRule> Compile(IReadOnlyList<TestRule> rules, bool validateSelectors)
    {
        var compiled = new List<FixtureRule>(rules.Count);
        for (int index = 0; index < rules.Count; index++)
            compiled.Add(CompileRule(rules[index], index, validateSelectors));
        return compiled;
    }

    private static FixtureRule CompileRule(TestRule rule, int index, bool validateSelectors)
    {
        if (rule.Action > Protocol.RuleActionRedirect)
            throw Error(index, $"动作 {rule.Action} 不是已知取值（0 permit / 1 block / 2 redirect）");

        byte protocolMask = ProtocolMask(rule, index);
        byte familyMask = FamilyMask(rule, index);
        return new FixtureRule
        {
            Action = (byte)rule.Action,
            ProtocolMask = protocolMask,
            FamilyMask = familyMask,
            Networks = Networks(rule, index, familyMask),
            Ports = Ports(rule, index),
            Processes = Processes(rule, index, validateSelectors),
        };
    }

    private static byte ProtocolMask(TestRule rule, int index)
    {
        if (rule.Protocols is null)
        {
            return rule.TransportProtocol switch
            {
                Protocol.RuleProtocolAny => 0,
                Protocol.RuleProtocolTcp => Protocol.RuleProtocolMaskTcp,
                Protocol.RuleProtocolUdp => Protocol.RuleProtocolMaskUdp,
                _ => throw Error(index, $"协议 {rule.TransportProtocol} 不是已知取值（0 任意 / 6 TCP / 17 UDP）"),
            };
        }

        if (rule.Protocols.Count == 0)
            throw Error(index, "protocol 数组不能为空；不限制时请省略该字段");
        byte mask = 0;
        bool any = false;
        foreach (byte protocol in rule.Protocols)
        {
            any |= protocol == Protocol.RuleProtocolAny;
            mask |= protocol switch
            {
                Protocol.RuleProtocolAny => (byte)0,
                Protocol.RuleProtocolTcp => Protocol.RuleProtocolMaskTcp,
                Protocol.RuleProtocolUdp => Protocol.RuleProtocolMaskUdp,
                _ => throw Error(index, $"协议 {protocol} 不是已知取值（0 任意 / 6 TCP / 17 UDP）"),
            };
        }

        return any ? (byte)0 : mask;
    }

    private static byte FamilyMask(TestRule rule, int index)
    {
        if (rule.Families is null)
        {
            return rule.Family switch
            {
                Protocol.RuleFamilyAny => 0,
                Protocol.RuleFamilyIpv4 => Protocol.RuleFamilyMaskIpv4,
                Protocol.RuleFamilyIpv6 => Protocol.RuleFamilyMaskIpv6,
                _ => throw Error(index, $"地址族 {rule.Family} 不是已知取值（0 任意 / 4 IPv4 / 6 IPv6）"),
            };
        }

        if (rule.Families.Count == 0)
            throw Error(index, "family 数组不能为空；不限制时请省略该字段");
        byte mask = 0;
        bool any = false;
        foreach (byte family in rule.Families)
        {
            any |= family == Protocol.RuleFamilyAny;
            mask |= family switch
            {
                Protocol.RuleFamilyAny => (byte)0,
                Protocol.RuleFamilyIpv4 => Protocol.RuleFamilyMaskIpv4,
                Protocol.RuleFamilyIpv6 => Protocol.RuleFamilyMaskIpv6,
                _ => throw Error(index, $"地址族 {family} 不是已知取值（0 任意 / 4 IPv4 / 6 IPv6）"),
            };
        }

        return any ? (byte)0 : mask;
    }

    private static List<FixtureNetwork> Networks(TestRule rule, int index, byte familyMask)
    {
        var sources = new List<string>();
        if (rule.Networks is { } explicitNetworks)
        {
            if (explicitNetworks.Count == 0)
                throw Error(index, "remoteNetwork 数组不能为空；不限制时请省略该字段");
            sources.AddRange(explicitNetworks);
        }
        else if (rule.Network is not null)
        {
            sources.Add(rule.Network);
        }

        var entries = new List<FixtureNetwork>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string cidr in sources)
        {
            if (string.IsNullOrWhiteSpace(cidr))
                throw Error(index, "remoteNetwork 包含空值");
            FixtureNetwork entry = ParseNetwork(cidr, index);
            if (familyMask is Protocol.RuleFamilyMaskIpv4 or Protocol.RuleFamilyMaskIpv6)
            {
                byte expected = familyMask == Protocol.RuleFamilyMaskIpv4
                                    ? Protocol.RuleFamilyIpv4
                                    : Protocol.RuleFamilyIpv6;
                if (entry.Family != expected)
                    throw Error(index, $"规则地址族 {expected} 与网段 {entry.Text} 的族 {entry.Family} 不一致，内核会整块拒绝");
            }

            if (seen.Add(entry.Text))
                entries.Add(entry);
        }

        return entries;
    }

    private static List<TestPortRange> Ports(TestRule rule, int index)
    {
        var ranges = new List<TestPortRange>();
        if (rule.Ports is { } explicitPorts)
        {
            if (explicitPorts.Count == 0)
                throw Error(index, "remotePorts 数组不能为空；不限制时请省略该字段");
            ranges.AddRange(explicitPorts);
        }
        else if (rule.PortStart != 0 || rule.PortEnd != 0xFFFF)
        {
            ranges.Add(new TestPortRange(rule.PortStart, rule.PortEnd));
        }

        foreach (TestPortRange range in ranges)
        {
            if (range.Start > range.End)
                throw Error(index, $"远端端口区间颠倒：{range.Start}-{range.End}");
        }

        ranges.Sort(static (left, right) =>
                        left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.End.CompareTo(right.End));
        var merged = new List<TestPortRange>(ranges.Count);
        foreach (TestPortRange range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
            {
                TestPortRange last = merged[^1];
                merged[^1] = new TestPortRange(last.Start, (ushort)Math.Max(last.End, range.End));
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    private static List<TestProcessSelector> Processes(TestRule rule, int index, bool validateSelectors)
    {
        var selectors = new List<TestProcessSelector>();
        if (rule.Processes is { } explicitSelectors)
        {
            if (explicitSelectors.Count == 0)
                throw Error(index, "process 数组不能为空；不限制时请省略该字段");
            selectors.AddRange(explicitSelectors);
        }
        else if (rule.Process is not null)
        {
            selectors.Add(TestProcessSelector.Path(rule.Process));
        }

        var unique = new List<TestProcessSelector>(selectors.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TestProcessSelector selector in selectors)
        {
            ValidateSelector(selector, index, validateSelectors);
            if (seen.Add($"{selector.Kind}/{selector.Value}"))
                unique.Add(selector);
        }

        return unique;
    }

    private static void ValidateSelector(TestProcessSelector selector, int index, bool validateSelectors)
    {
        if (selector.Value.Length == 0)
            throw Error(index, "进程选择器不能为空");
        if (selector.Kind == Protocol.ProcessSelectorKindFileName)
        {
            if (selector.IsDirectory)
                throw Error(index, $"文件名选择器不能标记为目录：{selector.Value}");
            if (selector.Value.Contains('/') || selector.Value.Contains('\\'))
                throw Error(index, $"文件名选择器不能包含路径分隔符：{selector.Value}");
        }
        else if (selector.Kind == Protocol.ProcessSelectorKindPath)
        {
            if (selector.Value[^1] == '\\' ||
                !selector.Value.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase))
                throw Error(index, $"路径型进程选择器必须是 \\device\\ 开头、不以 \\ 结尾的设备路径：{selector.Value}");
        }
        else
        {
            throw Error(index, $"进程选择器 kind {selector.Kind} 不是已知取值（0 文件名 / 1 路径）");
        }

        foreach (char character in selector.Value)
        {
            if (character < '\u0020' || character is >= '\u007F' and <= '\u009F')
                throw Error(index, $"进程选择器含控制字符 U+{(int)character:X4}：{selector.Value}");
        }

        if (!validateSelectors)
            return;

        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(selector.Value);
        }
        catch (EncoderFallbackException)
        {
            throw Error(index, $"进程选择器含非法 UTF-16（孤立代理项）：{selector.Value}");
        }

        if (bytes > Protocol.RuleBlobProcessSelectorTextMaxSize)
            throw Error(index,
                        $"进程选择器 UTF-8 长度 {bytes} 超过上限 {Protocol.RuleBlobProcessSelectorTextMaxSize} 字节" +
                        $"（不含结尾 NUL）：{selector.Value}。内核会整条拒绝这份 blob。");
    }

    private static FixtureNetwork ParseNetwork(string cidr, int index)
    {
        int slash = cidr.LastIndexOf('/');
        if (slash <= 0 || slash == cidr.Length - 1)
            throw Error(index, $"网段应为 CIDR（address/prefix）：{cidr}");
        if (!IPAddress.TryParse(cidr[..slash], out IPAddress? address))
            throw Error(index, $"网段地址无法解析：{cidr}");
        if (!int.TryParse(cidr[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefixLength))
            throw Error(index, $"网段前缀不是十进制整数：{cidr}");
        byte family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                          ? Protocol.RuleFamilyIpv4
                          : Protocol.RuleFamilyIpv6;
        int limit = family == Protocol.RuleFamilyIpv4 ? 32 : 128;
        if (prefixLength < 0 || prefixLength > limit)
            throw Error(index, $"网段 {cidr} 的前缀长度必须在 0..{limit}");

        byte[] bytes = new byte[16];
        address.GetAddressBytes().CopyTo(bytes, 0);
        int fullBytes = prefixLength / 8;
        int remaining = prefixLength % 8;
        if (remaining != 0)
        {
            bytes[fullBytes] &= (byte)(0xFF << (8 - remaining));
            fullBytes++;
        }

        for (int offset = fullBytes; offset < bytes.Length; offset++)
            bytes[offset] = 0;
        return new FixtureNetwork(family, (byte)prefixLength, bytes);
    }

    private static byte[] Write(IReadOnlyList<FixtureRule> rules, uint fallbackAction)
    {
        if (fallbackAction > Protocol.RuleActionRedirect)
            throw new InvalidOperationException($"兜底动作 {fallbackAction} 不是已知取值（0 permit / 1 block / 2 redirect）");

        var processOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var processBytes = new List<byte>();
        var selectors = new List<SelectorRecord>();
        var networks = new List<FixtureNetwork>();
        var ports = new List<TestPortRange>();

        foreach (FixtureRule rule in rules)
        {
            uint processFirst = (uint)selectors.Count;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (TestProcessSelector selector in rule.Processes)
            {
                string key = $"{selector.Kind}/{selector.Value}";
                if (!seen.Add(key))
                    continue;
                if (!processOffsets.TryGetValue(selector.Value, out uint offset))
                {
                    offset = (uint)processBytes.Count;
                    processOffsets.Add(selector.Value, offset);
                    processBytes.AddRange(Encoding.UTF8.GetBytes(selector.Value));
                    processBytes.Add(0);
                }

                selectors.Add(new SelectorRecord(offset, selector.Kind, selector.IsDirectory));
            }

            // ABI 约定（rule_blob.h::is_valid_rule_range）：count == 0 即“任意”，first 必须为 0。
            rule.ProcessCount = (uint)selectors.Count - processFirst;
            rule.ProcessFirst = rule.ProcessCount == 0 ? 0 : processFirst;

            uint networkFirst = (uint)networks.Count;
            networks.AddRange(rule.Networks);
            rule.NetworkCount = (uint)rule.Networks.Count;
            rule.NetworkFirst = rule.NetworkCount == 0 ? 0 : networkFirst;

            uint portFirst = (uint)ports.Count;
            ports.AddRange(rule.Ports);
            rule.PortCount = (uint)rule.Ports.Count;
            rule.PortFirst = rule.PortCount == 0 ? 0 : portFirst;
        }

        int rulesOffset = Protocol.RuleBlobHeaderSize;
        int processOffset = rulesOffset + (rules.Count * Protocol.RuleBlobRuleSize);
        int selectorOffset = AlignUp(processOffset + processBytes.Count, Protocol.RuleBlobSegmentAlignment);
        int selectorSize = selectors.Count * Protocol.RuleBlobProcessSelectorSize;
        int networkOffset = AlignUp(selectorOffset + selectorSize, Protocol.RuleBlobSegmentAlignment);
        int networkSize = networks.Count * Protocol.RuleBlobNetworkPrefixSize;
        int portOffset = AlignUp(networkOffset + networkSize, Protocol.RuleBlobSegmentAlignment);
        int portSize = ports.Count * Protocol.RuleBlobPortRangeSize;
        int totalSize = portOffset + portSize;
        if (totalSize > Protocol.RuleBlobMaxSize)
        {
            throw new InvalidOperationException(
                $"规则 blob 总大小 {totalSize} 字节超过上限 {Protocol.RuleBlobMaxSize} 字节（64 KiB）");
        }

        byte[] blob = new byte[totalSize];
        WriteUInt32(blob, 0, Protocol.RuleBlobMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(4), Protocol.RuleBlobVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(6), Protocol.RuleBlobHeaderSize);
        WriteUInt32(blob, 8, (uint)totalSize);
        WriteUInt32(blob, 12, (uint)rules.Count);
        WriteUInt32(blob, 16, fallbackAction);
        WriteUInt32(blob, 20, (uint)rulesOffset);
        WriteUInt32(blob, 24, (uint)processOffset);
        WriteUInt32(blob, 28, (uint)processBytes.Count);
        WriteUInt32(blob, 32, (uint)selectorOffset);
        WriteUInt32(blob, 36, (uint)selectorSize);
        WriteUInt32(blob, 40, (uint)networkOffset);
        WriteUInt32(blob, 44, (uint)networkSize);
        WriteUInt32(blob, 48, (uint)portOffset);
        WriteUInt32(blob, 52, (uint)portSize);

        for (int index = 0; index < rules.Count; index++)
        {
            FixtureRule rule = rules[index];
            Span<byte> span = blob.AsSpan(rulesOffset + (index * Protocol.RuleBlobRuleSize),
                                          Protocol.RuleBlobRuleSize);
            WriteUInt32(span, 0, rule.ProcessFirst);
            WriteUInt32(span, 4, rule.ProcessCount);
            WriteUInt32(span, 8, rule.NetworkFirst);
            WriteUInt32(span, 12, rule.NetworkCount);
            WriteUInt32(span, 16, rule.PortFirst);
            WriteUInt32(span, 20, rule.PortCount);
            span[24] = rule.ProtocolMask;
            span[25] = rule.FamilyMask;
            span[26] = rule.Action;
            span[27] = 0;
            WriteUInt32(span, 28, 0);
        }

        processBytes.CopyTo(blob, processOffset);
        for (int index = 0; index < selectors.Count; index++)
        {
            SelectorRecord selector = selectors[index];
            Span<byte> span = blob.AsSpan(selectorOffset + (index * Protocol.RuleBlobProcessSelectorSize),
                                          Protocol.RuleBlobProcessSelectorSize);
            WriteUInt32(span, 0, selector.StringOffset);
            span[4] = selector.Kind;
            span[5] = selector.IsDirectory ? (byte)1 : (byte)0;
            span[6] = 0;
            span[7] = 0;
        }

        for (int index = 0; index < networks.Count; index++)
        {
            FixtureNetwork network = networks[index];
            Span<byte> span = blob.AsSpan(networkOffset + (index * Protocol.RuleBlobNetworkPrefixSize),
                                          Protocol.RuleBlobNetworkPrefixSize);
            span[0] = network.PrefixLength;
            span[1] = network.Family;
            span[2] = 0;
            span[3] = 0;
            network.Address.CopyTo(span[4..]);
        }

        for (int index = 0; index < ports.Count; index++)
        {
            Span<byte> span = blob.AsSpan(portOffset + (index * Protocol.RuleBlobPortRangeSize),
                                          Protocol.RuleBlobPortRangeSize);
            BinaryPrimitives.WriteUInt16LittleEndian(span, ports[index].Start);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], ports[index].End);
        }

        return blob;
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static void WriteUInt32(byte[] blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offset), value);

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);

    private static InvalidOperationException Error(int index, string message) =>
        new($"规则 #{index}：{message}");

    private readonly record struct SelectorRecord(uint StringOffset, byte Kind, bool IsDirectory);

    public static byte[] CorruptMagic(byte[] blob) => Mutate(blob, copy => copy[0] ^= 0xFF);

    public static byte[] CorruptVersion(byte[] blob) =>
        Mutate(blob, copy => BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(4), 3));

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
        Mutate(blob, copy => WriteUInt32(copy, 40, (uint)blob.Length + 4));

    public static byte[] SelectorPoolOutOfRange(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 32, (uint)blob.Length + 4));

    public static byte[] PortPoolOutOfRange(byte[] blob) =>
        Mutate(blob, copy => WriteUInt32(copy, 48, (uint)blob.Length + 4));

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
