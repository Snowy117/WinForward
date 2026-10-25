using System.Globalization;
using System.Text.RegularExpressions;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 把 Host 规则 blob 的三份定义锁在一起：<c>src/common/rule_blob.h</c>（权威）、
/// 产品的 <see cref="RuleBlobLayout"/> / <see cref="PolicyLimits"/>、<c>tools/wfprobe/Protocol.cs</c>（夹具）。
/// </summary>
/// <remarks>
/// 产品与夹具都不引用彼此的工程：产品侧是 <c>internal</c> 常量，夹具是独立 exe。两边只能靠
/// 这组测试从源码文本里读常量来防漂移；结构体尺寸与字段不在这里重复，<c>LayoutTests</c> 负责
/// 产品布局，<c>AbiConsistencyTests</c> 负责控制面 ABI。
/// </remarks>
internal static class BlobAbiConsistencyTests
{
    private static readonly Regex CppConstantPattern = new(
        @"inline\s+constexpr\s+[\w:<>]+\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex FixtureConstantPattern = new(
        @"public\s+const\s+(?:uint|ushort|int|byte)\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex RuleAlignmentPattern = new(
        @"static_assert\s*\(\s*alignof\s*\(\s*rule\s*\)\s*==\s*(\d+)", RegexOptions.Compiled);

    private static readonly Lazy<string> Root = new(RepositoryRoot.Locate);

    private static readonly Lazy<string> RuleBlobHeaderText = new(() => Read("src", "common", "rule_blob.h"));

    private static readonly Lazy<Dictionary<string, long>> RuleBlobConstants = new(
        () => ParseCppConstants(RuleBlobHeaderText.Value));

    private static readonly Lazy<Dictionary<string, long>> IoctlConstants = new(
        () => ParseCppConstants(Read("src", "driver", "probe_ioctl.h")));

    private static readonly Lazy<Dictionary<string, long>> FixtureConstants = new(
        () => ParseFixtureConstants(Read("tools", "wfprobe", "Protocol.cs")));

    private static readonly Lazy<long> RuleAlignment = new(() =>
    {
        Match match = RuleAlignmentPattern.Match(RuleBlobHeaderText.Value);
        return match.Success
                   ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                   : throw new TestFailure("rule_blob.h 里读不到 static_assert(alignof(rule) == N)");
    });

    internal static void Register()
    {
        TestRunner.Add("abi/blob 头部与记录尺寸三处一致", Sizes);
        TestRunner.Add("abi/blob 魔数、版本与段对齐三处一致", Identity);
        TestRunner.Add("abi/blob 动作取值三处一致", Actions);
        TestRunner.Add("abi/blob 进程选择器 kind 三处一致", SelectorKinds);
        TestRunner.Add("abi/blob 协议与地址族掩码三处一致", Masks);
        TestRunner.Add("abi/blob 文本与容量上限三处一致", Limits);
    }

    private static void Sizes()
    {
        Check.Equal(CppConstant("rule_blob_header_size"), (long)RuleBlobLayout.HeaderSize, "rule_blob.h header 尺寸");
        Check.Equal(CppConstant("rule_blob_header_size"), FixtureConstant("RuleBlobHeaderSize"), "夹具 header 尺寸");
        Check.Equal(CppConstant("rule_size"), (long)RuleBlobLayout.RuleSize, "rule_blob.h rule 尺寸");
        Check.Equal(CppConstant("rule_size"), FixtureConstant("RuleBlobRuleSize"), "夹具 rule 尺寸");
        Check.Equal(CppConstant("process_selector_size"),
                    (long)RuleBlobLayout.ProcessSelectorSize, "rule_blob.h selector 尺寸");
        Check.Equal(CppConstant("process_selector_size"),
                    FixtureConstant("RuleBlobProcessSelectorSize"), "夹具 selector 尺寸");
        Check.Equal(CppConstant("port_range_size"), (long)RuleBlobLayout.PortRangeSize, "rule_blob.h port 尺寸");
        Check.Equal(CppConstant("port_range_size"), FixtureConstant("RuleBlobPortRangeSize"), "夹具 port 尺寸");
        Check.Equal(CppConstant("network_prefix_size"),
                    (long)RuleBlobLayout.NetworkPrefixSize, "rule_blob.h network 尺寸");
        Check.Equal(CppConstant("network_prefix_size"),
                    FixtureConstant("RuleBlobNetworkPrefixSize"), "夹具 network 尺寸");
        Check.Equal(CppConstant("network_address_size"),
                    (long)RuleBlobLayout.NetworkAddressSize, "rule_blob.h 地址字节数");
    }

    private static void Identity()
    {
        Check.Equal(CppConstant("rule_blob_magic"), (long)RuleBlobLayout.Magic, "RuleBlobLayout.Magic");
        Check.Equal(CppConstant("rule_blob_magic"), FixtureConstant("RuleBlobMagic"), "夹具 RuleBlobMagic");
        Check.Equal(CppConstant("rule_blob_version"), (long)RuleBlobLayout.Version, "RuleBlobLayout.Version");
        Check.Equal(CppConstant("rule_blob_version"), FixtureConstant("RuleBlobVersion"), "夹具 RuleBlobVersion");
        Check.Equal(RuleAlignment.Value, (long)RuleBlobLayout.SegmentAlignment, "RuleBlobLayout.SegmentAlignment");
        Check.Equal(RuleAlignment.Value, FixtureConstant("RuleBlobSegmentAlignment"), "夹具 RuleBlobSegmentAlignment");
    }

    private static void Actions()
    {
        Dictionary<string, long> actions = ParseEnum("rule_action");
        Check.Equal(EnumValue(actions, "permit"), (long)RuleAction.Permit, "RuleAction.Permit");
        Check.Equal(EnumValue(actions, "block"), (long)RuleAction.Block, "RuleAction.Block");
        Check.Equal(EnumValue(actions, "redirect"), (long)RuleAction.Redirect, "RuleAction.Redirect");
        Check.Equal(EnumValue(actions, "permit"), FixtureConstant("RuleActionPermit"), "夹具 RuleActionPermit");
        Check.Equal(EnumValue(actions, "block"), FixtureConstant("RuleActionBlock"), "夹具 RuleActionBlock");
        Check.Equal(EnumValue(actions, "redirect"), FixtureConstant("RuleActionRedirect"), "夹具 RuleActionRedirect");
    }

    private static void SelectorKinds()
    {
        Dictionary<string, long> kinds = ParseEnum("process_selector_kind");
        Check.Equal(EnumValue(kinds, "file_name"),
                    (long)RuleBlobProcessSelector.KindFileName, "RuleBlobProcessSelector.KindFileName");
        Check.Equal(EnumValue(kinds, "path"),
                    (long)RuleBlobProcessSelector.KindPath, "RuleBlobProcessSelector.KindPath");
        Check.Equal(EnumValue(kinds, "file_name"), (long)ProcessSelectorKind.FileName, "ProcessSelectorKind.FileName");
        Check.Equal(EnumValue(kinds, "path"), (long)ProcessSelectorKind.Path, "ProcessSelectorKind.Path");
        Check.Equal(EnumValue(kinds, "file_name"),
                    FixtureConstant("ProcessSelectorKindFileName"), "夹具 ProcessSelectorKindFileName");
        Check.Equal(EnumValue(kinds, "path"),
                    FixtureConstant("ProcessSelectorKindPath"), "夹具 ProcessSelectorKindPath");
    }

    private static void Masks()
    {
        Check.Equal(CppConstant("rule_protocol_mask_tcp"),
                    (long)RuleBlobLayout.ProtocolTcpBit, "RuleBlobLayout.ProtocolTcpBit");
        Check.Equal(CppConstant("rule_protocol_mask_tcp"),
                    FixtureConstant("RuleProtocolMaskTcp"), "夹具 RuleProtocolMaskTcp");
        Check.Equal(CppConstant("rule_protocol_mask_udp"),
                    (long)RuleBlobLayout.ProtocolUdpBit, "RuleBlobLayout.ProtocolUdpBit");
        Check.Equal(CppConstant("rule_protocol_mask_udp"),
                    FixtureConstant("RuleProtocolMaskUdp"), "夹具 RuleProtocolMaskUdp");
        Check.Equal(CppConstant("rule_protocol_mask_all"),
                    (long)(RuleBlobLayout.ProtocolTcpBit | RuleBlobLayout.ProtocolUdpBit), "protocol_mask_all = tcp|udp");
        Check.Equal(CppConstant("rule_family_mask_ipv4"),
                    (long)RuleBlobLayout.FamilyIpv4Bit, "RuleBlobLayout.FamilyIpv4Bit");
        Check.Equal(CppConstant("rule_family_mask_ipv4"),
                    FixtureConstant("RuleFamilyMaskIpv4"), "夹具 RuleFamilyMaskIpv4");
        Check.Equal(CppConstant("rule_family_mask_ipv6"),
                    (long)RuleBlobLayout.FamilyIpv6Bit, "RuleBlobLayout.FamilyIpv6Bit");
        Check.Equal(CppConstant("rule_family_mask_ipv6"),
                    FixtureConstant("RuleFamilyMaskIpv6"), "夹具 RuleFamilyMaskIpv6");
        Check.Equal(CppConstant("rule_family_mask_all"),
                    (long)(RuleBlobLayout.FamilyIpv4Bit | RuleBlobLayout.FamilyIpv6Bit), "family_mask_all = ipv4|ipv6");
    }

    private static void Limits()
    {
        Check.Equal(CppConstant("process_selector_text_max_size"),
                    (long)PolicyLimits.MaxProcessPathBytes, "PolicyLimits.MaxProcessPathBytes");
        Check.Equal(CppConstant("process_selector_text_max_size"),
                    FixtureConstant("RuleBlobProcessSelectorTextMaxSize"), "夹具 selector 文本上限");
        Check.Equal(IoctlConstant("rule_blob_max_size"),
                    (long)PolicyLimits.RuleBlobMaxSize, "PolicyLimits.RuleBlobMaxSize");
        Check.Equal(IoctlConstant("rule_blob_max_size"), FixtureConstant("RuleBlobMaxSize"), "夹具 RuleBlobMaxSize");
    }

    private static Dictionary<string, long> ParseCppConstants(string text)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (Match match in CppConstantPattern.Matches(text))
        {
            try
            {
                values[match.Groups[1].Value] = IntegerExpression.Evaluate(
                    match.Groups[2].Value,
                    name => values.TryGetValue(name, out long value) ? value : null);
            }
            catch (FormatException)
            {
                // ioctl 码引用 make_ioctl(...)，不属于 blob 常量表；需要时用 AbiConsistencyTests 查。
            }
        }

        return values;
    }

    private static Dictionary<string, long> ParseFixtureConstants(string text)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (Match match in FixtureConstantPattern.Matches(text))
        {
            values[match.Groups[1].Value] = IntegerExpression.Evaluate(match.Groups[2].Value, _ => null);
        }

        return values;
    }

    private static Dictionary<string, long> ParseEnum(string enumName)
    {
        Regex pattern = new($@"enum\s+class\s+{Regex.Escape(enumName)}\s*:[^{{]*\{{(.*?)\}};",
                           RegexOptions.Singleline);
        Match match = pattern.Match(RuleBlobHeaderText.Value);
        if (!match.Success)
        {
            throw new TestFailure($"rule_blob.h 里没有 enum class {enumName}");
        }

        Dictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (Match item in Regex.Matches(match.Groups[1].Value, @"(\w+)\s*=\s*(\d+)"))
        {
            values[item.Groups[1].Value] = long.Parse(item.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static long EnumValue(Dictionary<string, long> values, string name) =>
        values.TryGetValue(name, out long value) ? value : throw new TestFailure($"rule_blob.h 的枚举里没有 {name}");

    private static long CppConstant(string name) => Lookup(RuleBlobConstants.Value, name, "src/common/rule_blob.h");

    private static long IoctlConstant(string name) => Lookup(IoctlConstants.Value, name, "src/driver/probe_ioctl.h");

    private static long FixtureConstant(string name) => Lookup(FixtureConstants.Value, name, "tools/wfprobe/Protocol.cs");

    private static long Lookup(Dictionary<string, long> values, string name, string file) =>
        values.TryGetValue(name, out long value) ? value : throw new TestFailure($"{file} 里读不到常量 {name}");

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root.Value }.Concat(parts).ToArray()));
}
