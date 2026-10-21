namespace WinForward.Userland.Policy;

/// <summary>
/// 驱动 <c>rule_table.cpp</c> 里 <c>rule_order_valid</c> 的编译期镜像：对每条 block 规则，
/// 如果存在更早的非 block 规则可能与它命中同一条连接，就拒绝整份规则表。
/// </summary>
/// <remarks>
/// 驱动把 block 规则编译成独立的 <c>ALE_AUTH_CONNECT</c> 过滤器，这类过滤器只看自身条件、
/// 不参与 blob 的「首个命中」顺序。若一条更早的 permit/redirect 规则本应先命中，
/// 后面的 block 过滤器仍会拦掉这条连接 —— 语义与配置作者写的相反。
/// 驱动侧会整块拒绝（用户只看到 <c>STATUS_INVALID_PARAMETER</c>），所以这里用同样的判定
/// 提前给出带规则序号和条件摘要的错误。
///
/// 判定逐条对应 <c>rule_order_valid</c>：协议相容、地址族相容、端口区间相交、网段前缀相交、
/// 进程选择器任一侧为任意或字节相等。断言必须由 <see cref="RuleOrderValidator"/> 自己完成，
/// 不能指望驱动代劳。
/// </remarks>
internal static class RuleOrderValidator
{
    private const uint PoolIndexAny = RuleBlobLayout.PoolIndexAny;
    private const int NetworkPrefixSize = RuleBlobLayout.NetworkPrefixSize;

    /// <summary>
    /// 兜底动作与规则的组合检查：<c>fallback = block</c> 时驱动会创建一条无条件的
    /// <c>ALE_AUTH_CONNECT</c> block filter，同表里的 permit / redirect 规则全部变成死规则
    /// —— classify 仍会记 <c>permitted</c> / <c>matched_permit</c>，但连接已经被拒。
    /// 这种配置属于自相矛盾，编译期拒绝，别让用户对着「计数说放行、实际被拒」猜。
    /// </summary>
    internal static void ValidateFallback(RuleAction fallbackAction, RuleBlobRule[] rules)
    {
        if (fallbackAction != RuleAction.Block)
        {
            return;
        }

        for (int index = 0; index < rules.Length; index++)
        {
            RuleAction action = (RuleAction)rules[index].Action;
            if (action == RuleAction.Block)
            {
                continue;
            }

            throw new PolicyCompileException(
                $"兜底动作（fallbackAction）是 block，但规则 #{index} 是 {ActionName(action)}：" +
                "驱动会为兜底动作创建一条无条件的 AUTH_CONNECT block filter，把所有连接都拒掉，" +
                "这条规则永远不会生效（classify 却仍会记成放行，计数与真实结果不一致）。" +
                "请把兜底动作改成 permit/redirect，或把这条规则改成 block。");
        }
    }

    internal static void Validate(RuleBlobRule[] rules, byte[] processPool, RuleBlobNetworkPrefix[] networks)
    {
        for (int later = 0; later < rules.Length; later++)
        {
            if (rules[later].Action != (byte)RuleAction.Block)
            {
                continue;
            }

            for (int earlier = 0; earlier < later; earlier++)
            {
                if (rules[earlier].Action == (byte)RuleAction.Block)
                {
                    continue;
                }

                if (!Overlaps(rules[earlier], rules[later], processPool, networks))
                {
                    continue;
                }

                throw new PolicyCompileException(
                    $"规则 #{earlier}（{Describe(rules[earlier], processPool, networks)}）会遮蔽后面的 block 规则 " +
                    $"#{later}（{Describe(rules[later], processPool, networks)}）：两者在协议、地址族、端口区间、" +
                    "网段与进程选择器上都可能命中同一条连接。block 规则被编译成独立的 AUTH_CONNECT 过滤器、" +
                    "不参与规则顺序比较，驱动会整块拒绝这份规则表，请收窄条件或调整顺序。");
            }
        }
    }

    private static bool Overlaps(RuleBlobRule earlier,
                                 RuleBlobRule blocking,
                                 byte[] processPool,
                                 RuleBlobNetworkPrefix[] networks)
    {
        return ProtocolsOverlap(earlier, blocking) &&
               FamiliesOverlap(earlier, blocking, networks) &&
               PortsOverlap(earlier, blocking) &&
               NetworksOverlap(earlier, blocking, networks) &&
               ProcessesOverlap(earlier, blocking, processPool);
    }

    internal static bool ProtocolsOverlap(RuleBlobRule first, RuleBlobRule second) =>
        first.Protocol == (byte)RuleProtocol.Any || second.Protocol == (byte)RuleProtocol.Any ||
        first.Protocol == second.Protocol;

    private static bool FamiliesOverlap(RuleBlobRule first, RuleBlobRule second, RuleBlobNetworkPrefix[] networks)
    {
        byte firstFamily = EffectiveFamily(first, networks);
        byte secondFamily = EffectiveFamily(second, networks);
        return firstFamily == (byte)RuleAddressFamily.Any || secondFamily == (byte)RuleAddressFamily.Any ||
               firstFamily == secondFamily;
    }

    private static byte EffectiveFamily(RuleBlobRule rule, RuleBlobNetworkPrefix[] networks)
    {
        if (rule.Family != (byte)RuleAddressFamily.Any)
        {
            return rule.Family;
        }

        return rule.NetworkIndex == PoolIndexAny ? rule.Family : NetworkAt(rule.NetworkIndex, networks).Family;
    }

    private static bool PortsOverlap(RuleBlobRule first, RuleBlobRule second) =>
        first.RemotePortStart <= second.RemotePortEnd && second.RemotePortStart <= first.RemotePortEnd;

    private static bool NetworksOverlap(RuleBlobRule first, RuleBlobRule second, RuleBlobNetworkPrefix[] networks)
    {
        if (first.NetworkIndex == PoolIndexAny || second.NetworkIndex == PoolIndexAny)
        {
            return true;
        }

        return PrefixesIntersect(NetworkAt(first.NetworkIndex, networks), NetworkAt(second.NetworkIndex, networks));
    }

    private static RuleBlobNetworkPrefix NetworkAt(uint index, RuleBlobNetworkPrefix[] networks)
    {
        int slot = (int)(index / NetworkPrefixSize);
        return slot < networks.Length ? networks[slot] : default;
    }

    private static bool PrefixesIntersect(RuleBlobNetworkPrefix first, RuleBlobNetworkPrefix second)
    {
        RuleBlobNetworkPrefix shorter = first.PrefixLength <= second.PrefixLength ? first : second;
        RuleBlobNetworkPrefix longer = first.PrefixLength <= second.PrefixLength ? second : first;

        int fullBytes = shorter.PrefixLength / 8;
        int remainingBits = shorter.PrefixLength % 8;
        for (int index = 0; index < fullBytes; index++)
        {
            if (longer.Network[index] != shorter.Network[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        int mask = 0xFF << (8 - remainingBits);
        return ((longer.Network[fullBytes] ^ shorter.Network[fullBytes]) & mask) == 0;
    }

    private static bool ProcessesOverlap(RuleBlobRule first, RuleBlobRule second, byte[] processPool)
    {
        if (first.ProcessIndex == PoolIndexAny || second.ProcessIndex == PoolIndexAny)
        {
            return true;
        }

        ReadOnlySpan<byte> firstText = ProcessText(first.ProcessIndex, processPool);
        ReadOnlySpan<byte> secondText = ProcessText(second.ProcessIndex, processPool);

        // 规则池在正常编译路径上已经是 ASCII 小写；这里仍然按「只折叠 ASCII」比较，
        // 因为 matcher 在比较连接侧路径时就是这么折叠的：只有大小写不同的两个选择器
        // 会命中同一条连接，顺序冲突必须按同一个语义判定。不要用 OrdinalIgnoreCase——
        // 那是全 Unicode 折叠，与内核的行为不一致。
        if (firstText.Length != secondText.Length)
        {
            return false;
        }

        for (int index = 0; index < firstText.Length; index++)
        {
            if (AsciiLower(firstText[index]) != AsciiLower(secondText[index]))
            {
                return false;
            }
        }

        return true;

    }

    private static byte AsciiLower(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static string ActionName(RuleAction action) => action switch
    {
        RuleAction.Permit => "permit",
        RuleAction.Block => "block",
        RuleAction.Redirect => "redirect",
        _ => $"动作 {(byte)action}",
    };

    private static ReadOnlySpan<byte> ProcessText(uint index, byte[] processPool)
    {
        if (index >= processPool.Length)
        {
            // 索引越界只可能来自编译器自身的内存错误；保守地当成「重叠」，宁可拒绝也不静默放行。
            return ReadOnlySpan<byte>.Empty;
        }

        ReadOnlySpan<byte> tail = processPool.AsSpan((int)index);
        int length = tail.IndexOf((byte)0);
        return length < 0 ? tail : tail[..length];
    }

    private static string Describe(RuleBlobRule rule, byte[] processPool, RuleBlobNetworkPrefix[] networks)
    {
        string action = rule.Action switch
        {
            (byte)RuleAction.Permit => "permit",
            (byte)RuleAction.Block => "block",
            (byte)RuleAction.Redirect => "redirect",
            _ => $"动作 {rule.Action}",
        };
        string protocol = rule.Protocol switch
        {
            (byte)RuleProtocol.Tcp => "tcp",
            (byte)RuleProtocol.Udp => "udp",
            (byte)RuleProtocol.Any => "任意协议",
            _ => $"协议 {rule.Protocol}",
        };
        string family = rule.Family switch
        {
            (byte)RuleAddressFamily.Ipv4 => "IPv4",
            (byte)RuleAddressFamily.Ipv6 => "IPv6",
            _ => rule.NetworkIndex == PoolIndexAny ? "任意族" : $"网段族 {(byte)NetworkAt(rule.NetworkIndex, networks).Family}",
        };
        string ports = $"{rule.RemotePortStart}..{rule.RemotePortEnd}";
        string network = rule.NetworkIndex == PoolIndexAny
                             ? "任意网段"
                             : DescribeNetwork(NetworkAt(rule.NetworkIndex, networks));
        string process = rule.ProcessIndex == PoolIndexAny
                             ? "任意进程"
                             : System.Text.Encoding.UTF8.GetString(ProcessText(rule.ProcessIndex, processPool));
        return $"{action} {protocol} {family} 端口 {ports} 网段 {network} 进程 {process}";
    }

    private static string DescribeNetwork(RuleBlobNetworkPrefix prefix)
    {
        int addressLength = prefix.Family == (byte)RuleAddressFamily.Ipv6 ? 16 : 4;
        byte[] bytes = new byte[addressLength];
        for (int index = 0; index < addressLength; index++)
        {
            bytes[index] = prefix.Network[index];
        }

        System.Net.IPAddress address = new(bytes);

        return $"{address}/{prefix.PrefixLength}";
    }
}
