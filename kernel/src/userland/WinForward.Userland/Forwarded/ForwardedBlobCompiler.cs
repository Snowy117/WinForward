using System.Runtime.InteropServices;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Forwarded;

/// <summary>
/// 把有序 forwarded 规则表编译成 forwarded_blob.h v1 的字节流。
/// </summary>
/// <remarks>
/// 段序为 header | rules | network_pool，段首 4 字节对齐。网段按 (族, 前缀, 地址) 去重、
/// 按首次出现顺序排布，索引是池内字节偏移。所有语义判断在编译期做完，内核侧只解码、不解释。
/// </remarks>
public static class ForwardedBlobCompiler
{
    public static byte[] Compile(ForwardedPolicyTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        ValidateFallback(table.FallbackAction);
        if (table.Rules.Count > ForwardedPolicyLimits.MaxRules)
        {
            throw new PolicyCompileException(
                $"规则数 {table.Rules.Count} 超过上限 {ForwardedPolicyLimits.MaxRules}");
        }

        NetworkPoolBuilder networkPool = new();
        ForwardedBlobRule[] entries = new ForwardedBlobRule[table.Rules.Count];

        for (int index = 0; index < table.Rules.Count; index++)
        {
            ForwardedPolicyRule rule = table.Rules[index] ?? throw Error(index, "规则为 null");
            entries[index] = CompileRule(index, rule, networkPool);
        }

        RuleBlobNetworkPrefix[] networks = networkPool.ToArray();
        if (networks.Length > ForwardedPolicyLimits.MaxNetworkPoolEntries)
        {
            throw new PolicyCompileException(
                $"网段池条目数 {networks.Length} 超过上限 {ForwardedPolicyLimits.MaxNetworkPoolEntries}");
        }

        int rulesOffset = ForwardedBlobLayout.HeaderSize;
        int networkOffset = ForwardedBlobLayout.AlignUp(
            rulesOffset + (entries.Length * ForwardedBlobLayout.RuleSize), ForwardedBlobLayout.SegmentAlignment);
        int networkSize = networks.Length * ForwardedBlobLayout.NetworkPrefixSize;
        int totalSize = networkOffset + networkSize;

        byte[] blob = new byte[totalSize];

        ForwardedBlobHeader header = default;
        header.Magic = ForwardedBlobLayout.Magic;
        header.Version = ForwardedBlobLayout.Version;
        header.HeaderSize = ForwardedBlobLayout.HeaderSize;
        header.TotalSize = (uint)totalSize;
        header.RuleCount = (uint)entries.Length;
        header.FallbackAction = (uint)table.FallbackAction;
        header.RulesOffset = (uint)rulesOffset;
        header.NetworkPoolOffset = (uint)networkOffset;
        header.NetworkPoolSize = (uint)networkSize;
        header.Reserved = 0;
        header.Reserved2 = 0;
        MemoryMarshal.Write(blob.AsSpan(0, ForwardedBlobLayout.HeaderSize), in header);

        for (int index = 0; index < entries.Length; index++)
        {
            MemoryMarshal.Write(
                blob.AsSpan(rulesOffset + (index * ForwardedBlobLayout.RuleSize), ForwardedBlobLayout.RuleSize),
                in entries[index]);
        }

        for (int index = 0; index < networks.Length; index++)
        {
            MemoryMarshal.Write(
                blob.AsSpan(networkOffset + (index * ForwardedBlobLayout.NetworkPrefixSize),
                            ForwardedBlobLayout.NetworkPrefixSize),
                in networks[index]);
        }

        return blob;
    }

    private static ForwardedBlobRule CompileRule(int ruleIndex, ForwardedPolicyRule rule, NetworkPoolBuilder networkPool)
    {
        ValidateAction(rule.Action, "动作", ruleIndex);
        ValidateProtocol(rule.Protocol, ruleIndex);
        ValidateFamily(rule.Family, ruleIndex);

        PortRange ports = rule.RemotePorts;
        if (ports.Begin > ports.End)
        {
            throw Error(ruleIndex, $"远端端口区间颠倒：{ports}");
        }

        if (rule.IngressAdapterId == ForwardedBlobLayout.AdapterAny)
        {
            throw Error(ruleIndex, $"IngressAdapterId {ForwardedBlobLayout.AdapterAny} 是“任意入口”哨兵，不能作为具体 id");
        }

        ValidateUpstream(ruleIndex, rule.Action, rule.UpstreamId);

        RemoteNetwork? clientNetwork = rule.ClientNetwork;
        RemoteNetwork? remoteNetwork = rule.RemoteNetwork;
        ValidateNetworkFamily(ruleIndex, rule.Family, "clientCidr", clientNetwork);
        ValidateNetworkFamily(ruleIndex, rule.Family, "remoteCidr", remoteNetwork);
        if (clientNetwork is not null && remoteNetwork is not null && clientNetwork.Family != remoteNetwork.Family)
        {
            throw Error(ruleIndex,
                        $"clientCidr 族 {(byte)clientNetwork.Family} 与 remoteCidr 族 " +
                        $"{(byte)remoteNetwork.Family} 不一致，内核会整块拒绝");
        }

        ForwardedBlobRule entry = default;
        entry.IngressAdapterId = rule.IngressAdapterId ?? ForwardedBlobLayout.AdapterAny;
        entry.ClientNetworkIndex = clientNetwork is null
                                       ? ForwardedBlobLayout.PoolIndexAny
                                       : networkPool.Add(clientNetwork);
        entry.RemoteNetworkIndex = remoteNetwork is null
                                       ? ForwardedBlobLayout.PoolIndexAny
                                       : networkPool.Add(remoteNetwork);
        entry.RemotePortStart = ports.Begin;
        entry.RemotePortEnd = ports.End;
        entry.UpstreamId = rule.UpstreamId ?? ForwardedBlobLayout.UpstreamNone;
        entry.Protocol = (byte)rule.Protocol;
        entry.Family = (byte)rule.Family;
        entry.Action = (byte)rule.Action;
        entry.Reserved = 0;
        return entry;
    }

    private static void ValidateFallback(RuleAction action)
    {
        if (action is not (RuleAction.Permit or RuleAction.Block))
        {
            throw new PolicyCompileException(
                $"兜底动作 {(byte)action} 不是已知取值（只允许 0 permit / 1 block）");
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

    private static void ValidateUpstream(int ruleIndex, RuleAction action, uint? upstreamId)
    {
        if (action == RuleAction.Redirect)
        {
            if (upstreamId is null)
            {
                throw Error(ruleIndex, "redirect 规则必须指定 UpstreamId");
            }

            if (upstreamId == ForwardedBlobLayout.UpstreamNone)
            {
                throw Error(ruleIndex,
                            $"UpstreamId {ForwardedBlobLayout.UpstreamNone} 是“无上游”哨兵，不能用于 redirect");
            }

            return;
        }

        if (upstreamId is not null)
        {
            throw Error(ruleIndex, $"非 redirect 规则不得设置 UpstreamId（当前动作 {(byte)action}）");
        }
    }

    private static void ValidateNetworkFamily(int ruleIndex,
                                              RuleAddressFamily family,
                                              string what,
                                              RemoteNetwork? network)
    {
        if (network is null)
        {
            return;
        }

        if (family != RuleAddressFamily.Any && family != network.Family)
        {
            throw Error(ruleIndex,
                        $"规则族 {(byte)family} 与 {what} 的网段族 {(byte)network.Family} 不一致，内核会整块拒绝");
        }
    }

    private static PolicyCompileException Error(int? ruleIndex, string message) =>
        new(ruleIndex is null ? message : $"规则 #{ruleIndex}：{message}");
}
