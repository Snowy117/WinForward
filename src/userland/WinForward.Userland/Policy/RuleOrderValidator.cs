using System.Text;

namespace WinForward.Userland.Policy;

/// <summary>
/// 驱动 <c>rule_table.cpp</c> 里 <c>rule_order_valid</c> 的编译期镜像：对每条 block 规则，
/// 如果存在更早的非 block 规则可能与它命中同一条连接，就拒绝整份规则表。
/// </summary>
/// <remarks>
/// 驱动把 block 规则编译成独立的 <c>ALE_AUTH_CONNECT</c> 过滤器，这类过滤器只看自身条件、
/// 不参与 blob 的「首个命中」顺序。若一条更早的 permit/redirect 规则本应先命中，后面的
/// block 过滤器仍会拦掉这条连接 —— 语义与配置作者写的相反。
///
/// 判定逐条对应 <c>rule_order_valid</c>：协议掩码相交、有效族集相交、端口区间相交、
/// 网段前缀相交、进程选择器重叠。两侧必须保持同一算法；断言由本类型自己完成，
/// 不能指望驱动代劳。
/// </remarks>
internal static class RuleOrderValidator
{
    /// <summary>
    /// 兜底动作与规则的组合检查：<c>fallback = block</c> 时驱动会按 TCP(6) 与 UDP(17) 各创建
    /// 一条 <c>ALE_AUTH_CONNECT</c> block filter（IPv4 / IPv6 每层一对），同表里 permit / redirect
    /// 规则命中的 TCP/UDP 连接全部变成死规则 —— classify 仍会记 <c>permitted</c> /
    /// <c>matched_permit</c>，但连接已经被拒。
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
                "驱动会按 TCP(6) / UDP(17) 各创建一条 AUTH_CONNECT block filter（IPv4 / IPv6 每层一对），" +
                "把所有 TCP/UDP 连接都拒掉，这条规则永远不会生效（classify 却仍会记成放行，计数与真实结果不一致）。" +
                "请把兜底动作改成 permit/redirect，或把这条规则改成 block。");
        }
    }

    internal static void Validate(RuleBlobRule[] rules,
                                  byte[] processPool,
                                  RuleBlobProcessSelector[] selectors,
                                  RuleBlobNetworkPrefix[] networks,
                                  RuleBlobPortRange[] ports)
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

                if (!Overlaps(rules[earlier], rules[later], processPool, selectors, networks, ports))
                {
                    continue;
                }

                throw new PolicyCompileException(
                    $"规则 #{earlier}（{Describe(rules[earlier], processPool, selectors, networks, ports)}）" +
                    $"会遮蔽后面的 block 规则 #{later}（{Describe(rules[later], processPool, selectors, networks, ports)}）：" +
                    "两者在协议、地址族、端口、网段与进程选择器上都可能命中同一条连接。" +
                    "block 规则被编译成独立的 AUTH_CONNECT 过滤器、不参与规则顺序比较，" +
                    "驱动会整块拒绝这份规则表，请收窄条件或调整顺序。");
            }
        }
    }

    internal static bool ProtocolsOverlap(RuleBlobRule first, RuleBlobRule second) =>
        first.ProtocolMask == 0 || second.ProtocolMask == 0 ||
        (first.ProtocolMask & second.ProtocolMask) != 0;

    private static bool Overlaps(RuleBlobRule earlier,
                                 RuleBlobRule blocking,
                                 byte[] processPool,
                                 RuleBlobProcessSelector[] selectors,
                                 RuleBlobNetworkPrefix[] networks,
                                 RuleBlobPortRange[] ports)
    {
        return ProtocolsOverlap(earlier, blocking) &&
               FamiliesOverlap(earlier, blocking, networks) &&
               PortsOverlap(earlier, blocking, ports) &&
               NetworksOverlap(earlier, blocking, networks) &&
               ProcessesOverlap(earlier, blocking, processPool, selectors);
    }

    private static bool FamiliesOverlap(RuleBlobRule first, RuleBlobRule second, RuleBlobNetworkPrefix[] networks) =>
        (EffectiveFamilies(first, networks) & EffectiveFamilies(second, networks)) != 0;

    private static byte EffectiveFamilies(RuleBlobRule rule, RuleBlobNetworkPrefix[] networks)
    {
        byte families = rule.FamilyMask == 0 ? (byte)3 : rule.FamilyMask;
        if (rule.NetworkCount == 0)
        {
            return families;
        }

        byte networkFamilies = 0;
        for (uint index = 0; index < rule.NetworkCount; index++)
        {
            networkFamilies |= FamilyBit(networks[rule.NetworkFirst + index].Family);
        }

        return (byte)(families & networkFamilies);
    }

    private static byte FamilyBit(byte family) => family switch
    {
        (byte)RuleAddressFamily.Ipv4 => RuleBlobLayout.FamilyIpv4Bit,
        (byte)RuleAddressFamily.Ipv6 => RuleBlobLayout.FamilyIpv6Bit,
        _ => 0,
    };

    private static bool PortsOverlap(RuleBlobRule first, RuleBlobRule second, RuleBlobPortRange[] ports)
    {
        if (first.PortCount == 0 || second.PortCount == 0)
        {
            return true;
        }

        for (uint left = 0; left < first.PortCount; left++)
        {
            RuleBlobPortRange firstRange = ports[first.PortFirst + left];
            for (uint right = 0; right < second.PortCount; right++)
            {
                RuleBlobPortRange secondRange = ports[second.PortFirst + right];
                if (firstRange.Start <= secondRange.End && secondRange.Start <= firstRange.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool NetworksOverlap(RuleBlobRule first, RuleBlobRule second, RuleBlobNetworkPrefix[] networks)
    {
        if (first.NetworkCount == 0 || second.NetworkCount == 0)
        {
            return true;
        }

        for (uint left = 0; left < first.NetworkCount; left++)
        {
            RuleBlobNetworkPrefix firstPrefix = networks[first.NetworkFirst + left];
            for (uint right = 0; right < second.NetworkCount; right++)
            {
                RuleBlobNetworkPrefix secondPrefix = networks[second.NetworkFirst + right];
                if (firstPrefix.Family == secondPrefix.Family && PrefixesIntersect(firstPrefix, secondPrefix))
                {
                    return true;
                }
            }
        }

        return false;
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

    private static bool ProcessesOverlap(RuleBlobRule first,
                                         RuleBlobRule second,
                                         byte[] processPool,
                                         RuleBlobProcessSelector[] selectors)
    {
        if (first.ProcessCount == 0 || second.ProcessCount == 0)
        {
            return true;
        }

        for (uint left = 0; left < first.ProcessCount; left++)
        {
            RuleBlobProcessSelector firstSelector = selectors[first.ProcessFirst + left];
            for (uint right = 0; right < second.ProcessCount; right++)
            {
                RuleBlobProcessSelector secondSelector = selectors[second.ProcessFirst + right];
                if (SelectorsOverlap(firstSelector, secondSelector, processPool))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SelectorsOverlap(RuleBlobProcessSelector first,
                                         RuleBlobProcessSelector second,
                                         byte[] processPool)
    {
        ReadOnlySpan<byte> firstText = ProcessText(first.StringOffset, processPool);
        ReadOnlySpan<byte> secondText = ProcessText(second.StringOffset, processPool);

        if (first.Kind == RuleBlobProcessSelector.KindFileName &&
            second.Kind == RuleBlobProcessSelector.KindFileName)
        {
            return AsciiEquals(firstText, secondText);
        }

        if (first.Kind == RuleBlobProcessSelector.KindFileName)
        {
            return FileNameMatchesPath(firstText, second.IsDirectory, secondText);
        }

        if (second.Kind == RuleBlobProcessSelector.KindFileName)
        {
            return FileNameMatchesPath(secondText, first.IsDirectory, firstText);
        }

        return AsciiEquals(firstText, secondText) ||
               IsPathPrefix(firstText, secondText) ||
               IsPathPrefix(secondText, firstText);
    }

    private static bool FileNameMatchesPath(ReadOnlySpan<byte> fileName, byte isDirectory, ReadOnlySpan<byte> path)
    {
        if (isDirectory != 0)
        {
            return true;
        }

        return AsciiEquals(fileName, LastSegment(path));
    }

    private static ReadOnlySpan<byte> LastSegment(ReadOnlySpan<byte> path)
    {
        int separator = path.LastIndexOf((byte)'\\');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static bool IsPathPrefix(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> path)
    {
        if (candidate.Length >= path.Length)
        {
            return false;
        }

        for (int index = 0; index < candidate.Length; index++)
        {
            if (AsciiLower(candidate[index]) != AsciiLower(path[index]))
            {
                return false;
            }
        }

        return path[candidate.Length] == (byte)'\\';
    }

    private static bool AsciiEquals(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        for (int index = 0; index < first.Length; index++)
        {
            if (AsciiLower(first[index]) != AsciiLower(second[index]))
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

    private static ReadOnlySpan<byte> ProcessText(uint offset, byte[] processPool)
    {
        if (offset >= processPool.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        ReadOnlySpan<byte> tail = processPool.AsSpan((int)offset);
        int length = tail.IndexOf((byte)0);
        return length < 0 ? tail : tail[..length];
    }

    private static string Describe(RuleBlobRule rule,
                                   byte[] processPool,
                                   RuleBlobProcessSelector[] selectors,
                                   RuleBlobNetworkPrefix[] networks,
                                   RuleBlobPortRange[] ports)
    {
        string action = rule.Action switch
        {
            (byte)RuleAction.Permit => "permit",
            (byte)RuleAction.Block => "block",
            (byte)RuleAction.Redirect => "redirect",
            _ => $"动作 {rule.Action}",
        };

        return $"{action} {DescribeProtocols(rule.ProtocolMask)} {DescribeFamilies(rule.FamilyMask)} " +
               $"端口 {DescribePorts(rule, ports)} 网段 {DescribeNetworks(rule, networks)} " +
               $"进程 {DescribeProcesses(rule, processPool, selectors)}";
    }

    private static string DescribeProtocols(byte mask) => mask switch
    {
        0 => "任意协议",
        1 => "tcp",
        2 => "udp",
        3 => "tcp,udp",
        _ => $"协议掩码 {mask}",
    };

    private static string DescribeFamilies(byte mask) => mask switch
    {
        0 => "任意族",
        1 => "IPv4",
        2 => "IPv6",
        3 => "IPv4,IPv6",
        _ => $"族掩码 {mask}",
    };

    private static string DescribePorts(RuleBlobRule rule, RuleBlobPortRange[] ports)
    {
        if (rule.PortCount == 0)
        {
            return "任意端口";
        }

        List<string> parts = new((int)rule.PortCount);
        for (uint index = 0; index < rule.PortCount; index++)
        {
            RuleBlobPortRange range = ports[rule.PortFirst + index];
            parts.Add($"{range.Start}..{range.End}");
        }

        return string.Join(",", parts);
    }

    private static string DescribeNetworks(RuleBlobRule rule, RuleBlobNetworkPrefix[] networks)
    {
        if (rule.NetworkCount == 0)
        {
            return "任意网段";
        }

        List<string> parts = new((int)rule.NetworkCount);
        for (uint index = 0; index < rule.NetworkCount; index++)
        {
            parts.Add(DescribeNetwork(networks[rule.NetworkFirst + index]));
        }

        return string.Join(",", parts);
    }

    private static string DescribeProcesses(RuleBlobRule rule,
                                            byte[] processPool,
                                            RuleBlobProcessSelector[] selectors)
    {
        if (rule.ProcessCount == 0)
        {
            return "任意进程";
        }

        List<string> parts = new((int)rule.ProcessCount);
        for (uint index = 0; index < rule.ProcessCount; index++)
        {
            RuleBlobProcessSelector selector = selectors[rule.ProcessFirst + index];
            string text = Encoding.UTF8.GetString(ProcessText(selector.StringOffset, processPool));
            string prefix = selector.Kind == RuleBlobProcessSelector.KindFileName ? "文件名" : "路径";
            string directory = selector.IsDirectory != 0 ? "(目录)" : string.Empty;
            parts.Add($"{prefix}:{text}{directory}");
        }

        return string.Join(",", parts);
    }

    private static string DescribeNetwork(RuleBlobNetworkPrefix prefix)
    {
        int addressLength = prefix.Family == (byte)RuleAddressFamily.Ipv6 ? 16 : 4;
        byte[] bytes = new byte[addressLength];
        for (int index = 0; index < addressLength; index++)
        {
            bytes[index] = prefix.Network[index];
        }

        return $"{new System.Net.IPAddress(bytes)}/{prefix.PrefixLength}";
    }
}
