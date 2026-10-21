using System.Runtime.InteropServices;
using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 把有序规则表编译成 rule_blob.h v1 的字节流。
/// </summary>
/// <remarks>
/// 段序为 header | rules | 进程串池 | 网段池，段首 4 字节对齐。进程串按归一化后的形式
/// 去重、按首次出现顺序排布，索引是池内字节偏移；网段记录 20 字节一条，索引是 20 的倍数。
/// 所有语义判断在编译期做完，内核侧只解码、不解释。
/// </remarks>
public static class RuleBlobCompiler
{
    public static byte[] Compile(PolicyTable table, IProcessPathNormalizer processPathNormalizer) =>
        Build(table, processPathNormalizer, validatePolicy: true);

    /// <summary>
    /// 测试专用入口：跳过顺序冲突检查，用来构造「驱动必须独立拒绝」的负向夹具。
    /// 产品路径只用 <see cref="Compile"/>；这个方法由 InternalsVisibleTo 只暴露给测试工程。
    /// </summary>
    internal static byte[] CompileUnchecked(PolicyTable table, IProcessPathNormalizer processPathNormalizer) =>
        Build(table, processPathNormalizer, validatePolicy: false);


    private static byte[] Build(PolicyTable table, IProcessPathNormalizer processPathNormalizer, bool validatePolicy)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(processPathNormalizer);

        ValidateAction(table.FallbackAction, "兜底动作", ruleIndex: null);

        ProcessPoolBuilder processPool = new();
        NetworkPoolBuilder networkPool = new();
        RuleBlobRule[] entries = new RuleBlobRule[table.Rules.Count];

        for (int index = 0; index < table.Rules.Count; index++)
        {
            PolicyRule rule = table.Rules[index]
                ?? throw Error(index, "规则为 null");
            entries[index] = CompileRule(index, rule, processPathNormalizer, processPool, networkPool);
        }

        byte[] processBytes = processPool.ToBytes();
        RuleBlobNetworkPrefix[] networks = networkPool.ToArray();

        // 与驱动 rule_order_valid 相同的判定：block 规则会被编译成独立的 AUTH_CONNECT 过滤器，
        // 若更早的非 block 规则可能与它命中同一条连接，语义就会与「首个命中」相矛盾。
        if (validatePolicy)
        {
            RuleOrderValidator.ValidateFallback(table.FallbackAction, entries);
            RuleOrderValidator.Validate(entries, processBytes, networks);
        }

        int rulesOffset = RuleBlobLayout.HeaderSize;
        int processOffset = rulesOffset + (entries.Length * RuleBlobLayout.RuleSize);
        int networkOffset = RuleBlobLayout.AlignUp(
            processOffset + processBytes.Length, RuleBlobLayout.SegmentAlignment);
        int networkSize = networks.Length * RuleBlobLayout.NetworkPrefixSize;
        int totalSize = networkOffset + networkSize;

        byte[] blob = new byte[totalSize];

        RuleBlobHeader header = default;
        header.Magic = RuleBlobLayout.Magic;
        header.Version = RuleBlobLayout.Version;
        header.HeaderSize = RuleBlobLayout.HeaderSize;
        header.TotalSize = (uint)totalSize;
        header.RuleCount = (uint)entries.Length;
        header.FallbackAction = (uint)table.FallbackAction;
        header.RulesOffset = (uint)rulesOffset;
        header.ProcessPoolOffset = (uint)processOffset;
        header.ProcessPoolSize = (uint)processBytes.Length;
        header.NetworkPoolOffset = (uint)networkOffset;
        header.NetworkPoolSize = (uint)networkSize;
        MemoryMarshal.Write(blob.AsSpan(0, RuleBlobLayout.HeaderSize), in header);

        for (int index = 0; index < entries.Length; index++)
        {
            MemoryMarshal.Write(
                blob.AsSpan(rulesOffset + (index * RuleBlobLayout.RuleSize), RuleBlobLayout.RuleSize),
                in entries[index]);
        }

        processBytes.CopyTo(blob, processOffset);

        for (int index = 0; index < networks.Length; index++)
        {
            MemoryMarshal.Write(
                blob.AsSpan(networkOffset + (index * RuleBlobLayout.NetworkPrefixSize), RuleBlobLayout.NetworkPrefixSize),
                in networks[index]);
        }

        return blob;
    }

    private static RuleBlobRule CompileRule(int ruleIndex,
                                              PolicyRule rule,
                                              IProcessPathNormalizer processPathNormalizer,
                                              ProcessPoolBuilder processPool,
                                              NetworkPoolBuilder networkPool)
    {
        ValidateAction(rule.Action, "动作", ruleIndex);
        ValidateProtocol(rule.Protocol, ruleIndex);
        ValidateFamily(rule.Family, ruleIndex);

        PortRange ports = rule.RemotePorts;
        if (ports.Begin > ports.End)
        {
            throw Error(ruleIndex, $"远端端口区间颠倒：{ports}");
        }

        uint networkIndex = RuleBlobLayout.PoolIndexAny;
        if (rule.RemoteNetwork is { } network)
        {
            if (rule.Family != RuleAddressFamily.Any && rule.Family != network.Family)
            {
                throw Error(
                    ruleIndex,
                    $"规则族 {(byte)rule.Family} 与网段族 {(byte)network.Family} 不一致，内核会整块拒绝");
            }

            networkIndex = networkPool.Add(network);
        }

        RuleBlobRule entry = default;
        entry.ProcessIndex = rule.Process is null
                                 ? RuleBlobLayout.PoolIndexAny
                                 : processPool.Add(NormalizeProcess(ruleIndex, rule.Process, processPathNormalizer));
        entry.NetworkIndex = networkIndex;
        entry.RemotePortStart = ports.Begin;
        entry.RemotePortEnd = ports.End;
        entry.Protocol = (byte)rule.Protocol;
        entry.Family = (byte)rule.Family;
        entry.Action = (byte)rule.Action;
        entry.Reserved = 0;
        return entry;
    }

    private static string NormalizeProcess(int ruleIndex, ProcessSelector selector, IProcessPathNormalizer processPathNormalizer)
    {
        string? normalized = processPathNormalizer.Normalize(selector.Path);
        if (string.IsNullOrEmpty(normalized))
        {
            throw Error(ruleIndex, $"进程路径无法归一化成内核会比较的形式：{selector.Path}");
        }

        try
        {
            ProcessSelector.EnsureRepresentable(normalized);
        }
        catch (ArgumentException exception)
        {
            throw Error(ruleIndex, $"归一化后的进程串不可用：{exception.Message}");
        }
        int byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > PolicyLimits.MaxProcessPathBytes)
        {
            throw Error(
                ruleIndex,
                $"进程路径超过 {PolicyLimits.MaxProcessPathBytes} 字节上限（UTF-8，不含结尾 NUL）：{byteCount} 字节");
        }

        return normalized;
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
}
