using System.Net;

namespace WinForward.Probe;

internal sealed class FixtureRule
{
    public byte Action { get; init; }
    public byte ProtocolMask { get; init; }
    public byte FamilyMask { get; init; }
    public List<FixtureNetwork> Networks { get; init; } = new();
    public List<TestPortRange> Ports { get; init; } = new();
    public List<TestProcessSelector> Processes { get; init; } = new();
    public uint ProcessFirst;
    public uint ProcessCount;
    public uint NetworkFirst;
    public uint NetworkCount;
    public uint PortFirst;
    public uint PortCount;
}

internal readonly record struct FixtureNetwork(byte Family, byte PrefixLength, byte[] Address)
{
    public string Text =>
        $"{new IPAddress(Address.AsSpan(0, Family == Protocol.RuleFamilyIpv4 ? 4 : 16))}/{PrefixLength}";
}

internal static class RuleBlobOrder
{
    public static void Validate(IReadOnlyList<FixtureRule> rules, uint fallbackAction)
    {
        ValidateFallback(rules, fallbackAction);
        ValidateBlockSelectors(rules);
        ValidateBlockBudget(rules, fallbackAction);
        ValidateRuleOrder(rules);
    }

    private static void ValidateFallback(IReadOnlyList<FixtureRule> rules, uint fallbackAction)
    {
        if (fallbackAction != Protocol.RuleActionBlock)
            return;
        for (int index = 0; index < rules.Count; index++)
        {
            if (rules[index].Action == Protocol.RuleActionBlock)
                continue;
            throw new InvalidOperationException(
                $"兜底动作（fallbackAction）是 block，与规则 #{index}（{Describe(rules[index])}）冲突：" +
                "那条规则永远不会生效。请去掉非 block 规则，或把兜底动作改回 permit。");
        }
    }

    private static void ValidateBlockSelectors(IReadOnlyList<FixtureRule> rules)
    {
        for (int index = 0; index < rules.Count; index++)
        {
            FixtureRule rule = rules[index];
            if (rule.Action != Protocol.RuleActionBlock)
                continue;
            foreach (TestProcessSelector selector in rule.Processes)
            {
                if (selector.Kind == Protocol.ProcessSelectorKindPath && !selector.IsDirectory)
                    continue;
                string kind = selector.Kind == Protocol.ProcessSelectorKindFileName ? "文件名" : "目录前缀";
                throw new InvalidOperationException(
                    $"规则 #{index}：block 规则不支持{kind}进程选择器「{selector.Value}」；" +
                    "WFP auth filter 只能精确匹配 ALE_APP_ID。请改用精确文件路径，或把该规则改成 permit/redirect。");
            }
        }
    }

    private static void ValidateBlockBudget(IReadOnlyList<FixtureRule> rules, uint fallbackAction)
    {
        long total = fallbackAction == Protocol.RuleActionBlock ? 4 : 0;
        for (int index = 0; index < rules.Count; index++)
        {
            FixtureRule rule = rules[index];
            if (rule.Action != Protocol.RuleActionBlock)
                continue;
            total += BlockFilterCount(rule);
            if (total > Protocol.MaxBlockFilters)
            {
                throw new InvalidOperationException(
                    $"规则 #{index}：block filter 预算超限：算上本条规则共需要 {total} 条 AUTH_CONNECT filter，" +
                    $"上限是 {Protocol.MaxBlockFilters}（数组会在驱动侧展开成多条 filter）");
            }
        }
    }

    private static long BlockFilterCount(FixtureRule rule)
    {
        long protocols = rule.ProtocolMask is 0 or 3 ? 2 : 1;
        long processes = Math.Max(1, rule.Processes.Count);
        long ports = Math.Max(1, rule.Ports.Count);
        long layers = 0;
        byte families = rule.FamilyMask == 0 ? (byte)3 : rule.FamilyMask;
        foreach ((byte bit, byte family) in new[] { (Protocol.RuleFamilyMaskIpv4, Protocol.RuleFamilyIpv4),
                                                    (Protocol.RuleFamilyMaskIpv6, Protocol.RuleFamilyIpv6) })
        {
            if ((families & bit) == 0)
                continue;
            layers += rule.Networks.Count == 0
                          ? 1
                          : rule.Networks.Count(network => network.Family == family);
        }

        return layers * protocols * processes * ports;
    }

    private static void ValidateRuleOrder(IReadOnlyList<FixtureRule> rules)
    {
        for (int later = 0; later < rules.Count; later++)
        {
            if (rules[later].Action != Protocol.RuleActionBlock)
                continue;
            for (int earlier = 0; earlier < later; earlier++)
            {
                if (rules[earlier].Action == Protocol.RuleActionBlock)
                    continue;
                if (!Overlaps(rules[earlier], rules[later]))
                    continue;
                throw new InvalidOperationException(
                    $"规则 #{earlier}（{Describe(rules[earlier])}）会先于 block 规则 #{later}" +
                    $"（{Describe(rules[later])}）命中同一批连接；block 在驱动里编译成独立 filter，" +
                    "无法保持“首个命中”的语义。请收窄条件或调整顺序。");
            }
        }
    }

    private static bool Overlaps(FixtureRule first, FixtureRule second) =>
        ProtocolsOverlap(first, second) && FamiliesOverlap(first, second) && PortsOverlap(first, second) &&
        NetworksOverlap(first, second) && ProcessesOverlap(first, second);

    private static bool ProtocolsOverlap(FixtureRule first, FixtureRule second) =>
        first.ProtocolMask == 0 || second.ProtocolMask == 0 ||
        (first.ProtocolMask & second.ProtocolMask) != 0;

    private static bool FamiliesOverlap(FixtureRule first, FixtureRule second) =>
        (EffectiveFamilies(first) & EffectiveFamilies(second)) != 0;

    private static byte EffectiveFamilies(FixtureRule rule)
    {
        byte families = rule.FamilyMask == 0 ? (byte)3 : rule.FamilyMask;
        if (rule.Networks.Count == 0)
            return families;
        byte networkFamilies = 0;
        foreach (FixtureNetwork network in rule.Networks)
            networkFamilies |= network.Family == Protocol.RuleFamilyIpv4 ? Protocol.RuleFamilyMaskIpv4 : Protocol.RuleFamilyMaskIpv6;
        return (byte)(families & networkFamilies);
    }

    private static bool PortsOverlap(FixtureRule first, FixtureRule second)
    {
        if (first.Ports.Count == 0 || second.Ports.Count == 0)
            return true;
        foreach (TestPortRange left in first.Ports)
        {
            foreach (TestPortRange right in second.Ports)
            {
                if (left.Start <= right.End && right.Start <= left.End)
                    return true;
            }
        }

        return false;
    }

    private static bool NetworksOverlap(FixtureRule first, FixtureRule second)
    {
        if (first.Networks.Count == 0 || second.Networks.Count == 0)
            return true;
        foreach (FixtureNetwork left in first.Networks)
        {
            foreach (FixtureNetwork right in second.Networks)
            {
                if (left.Family == right.Family && PrefixesIntersect(left, right))
                    return true;
            }
        }

        return false;
    }

    private static bool PrefixesIntersect(FixtureNetwork first, FixtureNetwork second)
    {
        int prefix = Math.Min(first.PrefixLength, second.PrefixLength);
        int fullBytes = prefix / 8;
        for (int index = 0; index < fullBytes; index++)
        {
            if (first.Address[index] != second.Address[index])
                return false;
        }

        int remaining = prefix % 8;
        if (remaining == 0)
            return true;
        int mask = 0xFF << (8 - remaining);
        return ((first.Address[fullBytes] ^ second.Address[fullBytes]) & mask) == 0;
    }

    private static bool ProcessesOverlap(FixtureRule first, FixtureRule second)
    {
        if (first.Processes.Count == 0 || second.Processes.Count == 0)
            return true;
        foreach (TestProcessSelector left in first.Processes)
        {
            foreach (TestProcessSelector right in second.Processes)
            {
                if (SelectorsOverlap(left, right))
                    return true;
            }
        }

        return false;
    }

    private static bool SelectorsOverlap(TestProcessSelector first, TestProcessSelector second)
    {
        if (first.Kind == Protocol.ProcessSelectorKindFileName &&
            second.Kind == Protocol.ProcessSelectorKindFileName)
            return AsciiEquals(first.Value, second.Value);
        if (first.Kind == Protocol.ProcessSelectorKindFileName)
            return FileNameMatchesPath(first.Value, second);
        if (second.Kind == Protocol.ProcessSelectorKindFileName)
            return FileNameMatchesPath(second.Value, first);
        return AsciiEquals(first.Value, second.Value) || IsPathPrefix(first.Value, second.Value) ||
               IsPathPrefix(second.Value, first.Value);
    }

    private static bool FileNameMatchesPath(string fileName, TestProcessSelector path)
    {
        if (path.IsDirectory)
            return true;
        int separator = path.Value.LastIndexOf('\\');
        string lastSegment = separator < 0 ? path.Value : path.Value[(separator + 1)..];
        return AsciiEquals(fileName, lastSegment);
    }

    private static bool IsPathPrefix(string candidate, string path) =>
        candidate.Length < path.Length && path[candidate.Length] == '\\' &&
        AsciiEquals(candidate, path[..candidate.Length]);

    private static bool AsciiEquals(string first, string second)
    {
        if (first.Length != second.Length)
            return false;
        for (int index = 0; index < first.Length; index++)
        {
            if (AsciiLower(first[index]) != AsciiLower(second[index]))
                return false;
        }

        return true;
    }

    private static char AsciiLower(char character) =>
        character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;

    private static string Describe(FixtureRule rule) =>
        $"action={rule.Action} protocol-mask={rule.ProtocolMask} family-mask={rule.FamilyMask} " +
        $"ports={DescribePorts(rule)} networks={DescribeNetworks(rule)} processes={DescribeProcesses(rule)}";

    private static string DescribePorts(FixtureRule rule) =>
        rule.Ports.Count == 0 ? "any" : string.Join(",", rule.Ports.Select(range => $"{range.Start}-{range.End}"));

    private static string DescribeNetworks(FixtureRule rule) =>
        rule.Networks.Count == 0 ? "any" : string.Join(",", rule.Networks.Select(network => network.Text));

    private static string DescribeProcesses(FixtureRule rule) =>
        rule.Processes.Count == 0
            ? "any"
            : string.Join(",", rule.Processes.Select(selector =>
                                 $"{selector.Kind}/{selector.Value}{(selector.IsDirectory ? "(dir)" : string.Empty)}"));
}
