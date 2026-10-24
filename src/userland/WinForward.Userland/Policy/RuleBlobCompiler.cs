using System.Runtime.InteropServices;
using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 把有序规则表编译成 rule_blob.h v2 的字节流。
/// </summary>
/// <remarks>
/// 段序为 header | rules | 进程串池 | selector 池 | 网段池 | 端口池，段首 4 字节对齐。
/// 一条配置规则对应一条 blob 规则：进程选择器、网段与端口区间都按规则连续追加，
/// 规则只持有下标与数量。进程串按归一化后的形式去重；网段不做跨规则去重。
/// 所有语义判断在编译期做完，内核侧只解码、不解释。
/// </remarks>
public static class RuleBlobCompiler
{
    public static byte[] Compile(PolicyTable table, IProcessPathNormalizer processPathNormalizer)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(processPathNormalizer);
        ValidateAction(table.FallbackAction, "兜底动作", ruleIndex: null);

        BlobPools pools = new();
        RuleBlobRule[] entries = new RuleBlobRule[table.Rules.Count];

        for (int index = 0; index < table.Rules.Count; index++)
        {
            PolicyRule rule = table.Rules[index]
                ?? throw Error(index, "规则为 null");
            entries[index] = CompileRule(index, rule, processPathNormalizer, pools);
        }

        byte[] processBytes = pools.ProcessPool.ToBytes();
        RuleBlobProcessSelector[] selectors = pools.Selectors.ToArray();
        RuleBlobNetworkPrefix[] networks = pools.Networks.ToArray();
        RuleBlobPortRange[] ports = pools.Ports.ToArray();

        return WriteBlob(table.FallbackAction, entries, processBytes, selectors, networks, ports);
    }

    private static RuleBlobRule CompileRule(int ruleIndex,
                                            PolicyRule rule,
                                            IProcessPathNormalizer processPathNormalizer,
                                            BlobPools pools)
    {
        ValidateAction(rule.Action, "动作", ruleIndex);
        byte protocolMask = BuildProtocolMask(ruleIndex, rule.Protocols);
        byte familyMask = BuildFamilyMask(ruleIndex, rule.Families);
        (uint first, uint count) networks = AddNetworks(ruleIndex, rule.RemoteNetworks, familyMask, pools);
        (uint first, uint count) ports = AddPorts(ruleIndex, rule.RemotePorts, pools);
        (uint first, uint count) processes = AddProcesses(
            ruleIndex, rule.Processes, processPathNormalizer, pools);

        RuleBlobRule entry = default;
        entry.ProcessFirst = processes.count == 0 ? 0 : processes.first;
        entry.ProcessCount = processes.count;
        entry.NetworkFirst = networks.count == 0 ? 0 : networks.first;
        entry.NetworkCount = networks.count;
        entry.PortFirst = ports.count == 0 ? 0 : ports.first;
        entry.PortCount = ports.count;
        entry.ProtocolMask = protocolMask;
        entry.FamilyMask = familyMask;
        entry.Action = (byte)rule.Action;
        return entry;
    }

    private static byte BuildProtocolMask(int ruleIndex, IReadOnlyList<RuleProtocol>? protocols)
    {
        if (protocols is null)
        {
            return 0;
        }

        EnsureNotEmpty(ruleIndex, protocols, "protocol");
        byte mask = 0;
        bool any = false;
        foreach (RuleProtocol protocol in protocols)
        {
            ValidateProtocol(protocol, ruleIndex);
            any |= protocol == RuleProtocol.Any;
            mask |= protocol switch
            {
                RuleProtocol.Tcp => RuleBlobLayout.ProtocolTcpBit,
                RuleProtocol.Udp => RuleBlobLayout.ProtocolUdpBit,
                _ => 0,
            };
        }

        return any ? (byte)0 : mask;
    }

    private static byte BuildFamilyMask(int ruleIndex, IReadOnlyList<RuleAddressFamily>? families)
    {
        if (families is null)
        {
            return 0;
        }

        EnsureNotEmpty(ruleIndex, families, "family");
        byte mask = 0;
        bool any = false;
        foreach (RuleAddressFamily family in families)
        {
            ValidateFamily(family, ruleIndex);
            any |= family == RuleAddressFamily.Any;
            mask |= family switch
            {
                RuleAddressFamily.Ipv4 => RuleBlobLayout.FamilyIpv4Bit,
                RuleAddressFamily.Ipv6 => RuleBlobLayout.FamilyIpv6Bit,
                _ => 0,
            };
        }

        return any ? (byte)0 : mask;
    }

    private static (uint first, uint count) AddNetworks(int ruleIndex,
                                                        IReadOnlyList<RemoteNetwork>? networks,
                                                        byte familyMask,
                                                        BlobPools pools)
    {
        if (networks is null)
        {
            return (0, 0);
        }

        EnsureNotEmpty(ruleIndex, networks, "remoteNetwork");
        uint first = (uint)pools.Networks.Count;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RemoteNetwork? network in networks)
        {
            if (network is null)
            {
                throw Error(ruleIndex, "remoteNetwork 数组包含 null");
            }

            byte networkBit = NetworkBit(ruleIndex, network);
            if (familyMask != 0 && (familyMask & networkBit) == 0)
            {
                throw Error(ruleIndex,
                            $"规则地址族与网段 {network} 的族 {(byte)network.Family} 不一致，内核会整块拒绝");
            }

            if (seen.Add(network.ToString()))
            {
                pools.Networks.Add(network);
            }
        }

        return (first, (uint)pools.Networks.Count - first);
    }

    private static (uint first, uint count) AddPorts(int ruleIndex,
                                                     IReadOnlyList<PortRange>? remotePorts,
                                                     BlobPools pools)
    {
        if (remotePorts is null)
        {
            return (0, 0);
        }

        EnsureNotEmpty(ruleIndex, remotePorts, "remotePorts");
        List<PortRange> sorted = new(remotePorts.Count);
        foreach (PortRange range in remotePorts)
        {
            if (range.Begin > range.End)
            {
                throw Error(ruleIndex, $"远端端口区间颠倒：{range}");
            }

            if (range.Begin == 0 && !range.IsAny)
            {
                throw Error(ruleIndex,
                            $"远端端口 0 不是合法端口（只有 PortRange.Any 的 0..65535 表示任意）：{range}");
            }

            sorted.Add(range);
        }

        sorted.Sort(static (left, right) =>
                        left.Begin != right.Begin ? left.Begin.CompareTo(right.Begin) : left.End.CompareTo(right.End));

        uint first = (uint)pools.Ports.Count;
        int index = 0;
        while (index < sorted.Count)
        {
            int begin = sorted[index].Begin;
            int end = sorted[index].End;
            index++;
            while (index < sorted.Count && sorted[index].Begin <= end + 1)
            {
                end = Math.Max(end, sorted[index].End);
                index++;
            }

            pools.Ports.Add(new RuleBlobPortRange { Start = (ushort)begin, End = (ushort)end });
        }

        return (first, (uint)pools.Ports.Count - first);
    }

    private static (uint first, uint count) AddProcesses(int ruleIndex,
                                                          IReadOnlyList<ProcessSelector>? processes,
                                                          IProcessPathNormalizer processPathNormalizer,
                                                          BlobPools pools)
    {
        if (processes is null)
        {
            return (0, 0);
        }

        EnsureNotEmpty(ruleIndex, processes, "process");
        uint first = (uint)pools.Selectors.Count;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ProcessSelector? selector in processes)
        {
            if (selector is null)
            {
                throw Error(ruleIndex, "process 数组包含 null");
            }

            CompiledProcessSelector compiled = NormalizeSelector(ruleIndex, selector, processPathNormalizer);

            string key = $"{(byte)compiled.Kind}/{compiled.Text}";
            if (!seen.Add(key))
            {
                continue;
            }

            RuleBlobProcessSelector record = default;
            record.StringOffset = pools.ProcessPool.Add(compiled.Text);
            record.Kind = compiled.Kind;
            record.IsDirectory = compiled.IsDirectory ? (byte)1 : (byte)0;
            pools.Selectors.Add(record);
        }

        return (first, (uint)pools.Selectors.Count - first);
    }

    private static CompiledProcessSelector NormalizeSelector(int ruleIndex,
                                                              ProcessSelector selector,
                                                              IProcessPathNormalizer processPathNormalizer)
    {
        byte kind;
        bool isDirectory = false;
        string text;
        if (selector.Kind == ProcessSelectorKind.FileName)
        {
            if (selector.Value.Contains('/') || selector.Value.Contains('\\'))
            {
                throw Error(ruleIndex, $"文件名选择器不能包含路径分隔符：{selector.Value}");
            }

            kind = RuleBlobProcessSelector.KindFileName;
            text = selector.Value;
        }
        else
        {
            ProcessPathResolution? resolution = processPathNormalizer.Normalize(selector.Value);
            if (resolution is null || string.IsNullOrEmpty(resolution.DevicePath))
            {
                throw Error(ruleIndex, $"路径型进程选择器无法解析成设备路径：{selector.Value}");
            }

            kind = RuleBlobProcessSelector.KindPath;
            isDirectory = resolution.IsDirectory;
            text = resolution.DevicePath;
            if (!text.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase) || text[^1] == '\\')
            {
                throw Error(ruleIndex,
                            $"路径型进程选择器必须归一成以 \\device\\ 开头、不以 \\ 结尾的设备路径：" +
                            $"{text}（选择器：{selector.Value}）");
            }
        }

        text = AsciiLowercaseNormalizer.Fold(text);
        try
        {
            ProcessSelector.EnsureRepresentable(text);
        }
        catch (ArgumentException exception)
        {
            throw Error(ruleIndex, $"归一化后的进程串不可用：{exception.Message}");
        }

        int byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > PolicyLimits.MaxProcessPathBytes)
        {
            throw Error(ruleIndex,
                        $"进程选择器超过 {PolicyLimits.MaxProcessPathBytes} 字节上限（UTF-8，不含结尾 NUL）：" +
                        $"{byteCount} 字节");
        }

        return new CompiledProcessSelector(text, kind, isDirectory);
    }

    private static byte NetworkBit(int ruleIndex, RemoteNetwork network) => network.Family switch
    {
        RuleAddressFamily.Ipv4 => RuleBlobLayout.FamilyIpv4Bit,
        RuleAddressFamily.Ipv6 => RuleBlobLayout.FamilyIpv6Bit,
        _ => throw Error(ruleIndex, $"网段 {network} 的地址族 {(byte)network.Family} 不是已知取值"),
    };

    private static void EnsureNotEmpty<T>(int ruleIndex, IReadOnlyList<T> values, string field)
    {
        if (values.Count == 0)
        {
            throw Error(ruleIndex, $"{field} 数组不能为空；不限制时请省略该字段（null）");
        }
    }

    private static byte[] WriteBlob(RuleAction fallbackAction,
                                    RuleBlobRule[] entries,
                                    byte[] processBytes,
                                    RuleBlobProcessSelector[] selectors,
                                    RuleBlobNetworkPrefix[] networks,
                                    RuleBlobPortRange[] ports)
    {
        int rulesOffset = RuleBlobLayout.HeaderSize;
        int processOffset = rulesOffset + (entries.Length * RuleBlobLayout.RuleSize);
        int selectorOffset = RuleBlobLayout.AlignUp(
            processOffset + processBytes.Length, RuleBlobLayout.SegmentAlignment);
        int selectorSize = selectors.Length * RuleBlobLayout.ProcessSelectorSize;
        int networkOffset = RuleBlobLayout.AlignUp(
            selectorOffset + selectorSize, RuleBlobLayout.SegmentAlignment);
        int networkSize = networks.Length * RuleBlobLayout.NetworkPrefixSize;
        int portOffset = RuleBlobLayout.AlignUp(
            networkOffset + networkSize, RuleBlobLayout.SegmentAlignment);
        int portSize = ports.Length * RuleBlobLayout.PortRangeSize;
        int totalSize = portOffset + portSize;

        if (totalSize > PolicyLimits.RuleBlobMaxSize)
        {
            throw new PolicyCompileException(
                $"规则 blob 总大小 {totalSize} 字节超过上限 {PolicyLimits.RuleBlobMaxSize} 字节（64 KiB）" +
                "（进程串、网段与端口数组都计入）；请减少规则或选择器数量。");
        }

        byte[] blob = new byte[totalSize];

        RuleBlobHeader header = default;
        header.Magic = RuleBlobLayout.Magic;
        header.Version = RuleBlobLayout.Version;
        header.HeaderSize = RuleBlobLayout.HeaderSize;
        header.TotalSize = (uint)totalSize;
        header.RuleCount = (uint)entries.Length;
        header.FallbackAction = (uint)fallbackAction;
        header.RulesOffset = (uint)rulesOffset;
        header.ProcessPoolOffset = (uint)processOffset;
        header.ProcessPoolSize = (uint)processBytes.Length;
        header.SelectorPoolOffset = (uint)selectorOffset;
        header.SelectorPoolSize = (uint)selectorSize;
        header.NetworkPoolOffset = (uint)networkOffset;
        header.NetworkPoolSize = (uint)networkSize;
        header.PortPoolOffset = (uint)portOffset;
        header.PortPoolSize = (uint)portSize;
        MemoryMarshal.Write(blob.AsSpan(0, RuleBlobLayout.HeaderSize), in header);

        WriteRecords(blob, rulesOffset, entries, RuleBlobLayout.RuleSize);
        processBytes.CopyTo(blob, processOffset);
        WriteRecords(blob, selectorOffset, selectors, RuleBlobLayout.ProcessSelectorSize);
        WriteRecords(blob, networkOffset, networks, RuleBlobLayout.NetworkPrefixSize);
        WriteRecords(blob, portOffset, ports, RuleBlobLayout.PortRangeSize);
        return blob;
    }

    private static void WriteRecords<T>(byte[] blob, int offset, T[] values, int recordSize)
        where T : struct
    {
        for (int index = 0; index < values.Length; index++)
        {
            MemoryMarshal.Write(
                blob.AsSpan(offset + (index * recordSize), recordSize), in values[index]);
        }
    }

    private static void ValidateAction(RuleAction action, string what, int? ruleIndex)
    {
        if (action is not (RuleAction.Permit or RuleAction.Block or RuleAction.Redirect))
        {
            throw Error(ruleIndex, $"{what} {(byte)action} 不是已知取值（0 permit / 1 block / 2 redirect）");
        }
    }

    private static void ValidateProtocol(RuleProtocol protocol, int ruleIndex)
    {
        if (protocol is not (RuleProtocol.Any or RuleProtocol.Tcp or RuleProtocol.Udp))
        {
            throw Error(ruleIndex, $"协议 {(byte)protocol} 不是已知取值（0 任意 / 6 TCP / 17 UDP）");
        }
    }

    private static void ValidateFamily(RuleAddressFamily family, int ruleIndex)
    {
        if (family is not (RuleAddressFamily.Any or RuleAddressFamily.Ipv4 or RuleAddressFamily.Ipv6))
        {
            throw Error(ruleIndex, $"地址族 {(byte)family} 不是已知取值（0 任意 / 4 IPv4 / 6 IPv6）");
        }
    }

    private static PolicyCompileException Error(int? ruleIndex, string message) =>
        new(ruleIndex is null ? message : $"规则 #{ruleIndex}：{message}");

    private sealed class BlobPools
    {
        internal ProcessPoolBuilder ProcessPool { get; } = new();

        internal List<RuleBlobProcessSelector> Selectors { get; } = new();

        internal NetworkPoolBuilder Networks { get; } = new(deduplicate: false);

        internal List<RuleBlobPortRange> Ports { get; } = new();
    }

    private readonly struct CompiledProcessSelector
    {
        internal CompiledProcessSelector(string text, byte kind, bool isDirectory)
        {
            Text = text;
            Kind = kind;
            IsDirectory = isDirectory;
        }

        internal string Text { get; }

        internal byte Kind { get; }

        internal bool IsDirectory { get; }
    }
}
