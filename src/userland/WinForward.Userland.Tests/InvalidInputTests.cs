using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class InvalidInputTests
{
    internal static void Register()
    {
        TestRunner.Add("invalid/空规则表为 null", NullTable);
        TestRunner.Add("invalid/规则为 null", NullRule);
        TestRunner.Add("invalid/兜底动作未知", UnknownFallback);
        TestRunner.Add("invalid/动作未知", UnknownAction);
        TestRunner.Add("invalid/协议未知", UnknownProtocol);
        TestRunner.Add("invalid/地址族未知", UnknownFamily);
        TestRunner.Add("invalid/端口区间颠倒", InvertedPorts);
        TestRunner.Add("invalid/规则族与网段族不一致", FamilyMismatch);
        TestRunner.Add("invalid/进程串为空", EmptyProcess);
        TestRunner.Add("invalid/进程串含控制字符", ControlCharacterProcess);
        TestRunner.Add("invalid/进程串含 NUL", NulProcess);
        TestRunner.Add("invalid/进程串含孤立代理项", LoneSurrogateProcess);
        TestRunner.Add("invalid/IPv4 前缀越界", Ipv4PrefixOutOfRange);
        TestRunner.Add("invalid/IPv6 前缀越界", Ipv6PrefixOutOfRange);
        TestRunner.Add("invalid/CIDR 缺少前缀", MissingPrefix);
        TestRunner.Add("invalid/CIDR 地址非法", BadAddress);
        TestRunner.Add("invalid/归一化结果非法", InvalidNormalizedPath);
    }

    private static void NullTable() =>
        Check.Throws<ArgumentNullException>(() => TestAppId.Compile(null!), "Compile(null)");

    private static void NullRule() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(new PolicyRule[] { null! }, RuleAction.Permit)),
            "规则列表里出现 null");

    private static void UnknownFallback() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(Array.Empty<PolicyRule>(), (RuleAction)9)),
            "兜底动作 9");

    private static void UnknownAction() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(new[] { new PolicyRule { Action = (RuleAction)7 } }, RuleAction.Permit)),
            "规则动作 7");

    private static void UnknownProtocol() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(new[] { new PolicyRule { Protocol = (RuleProtocol)17 } }, RuleAction.Permit)),
            "协议 17");

    private static void UnknownFamily() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(new[] { new PolicyRule { Family = (RuleAddressFamily)5 } }, RuleAction.Permit)),
            "地址族 5");

    private static void InvertedPorts() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(new[] { new PolicyRule { RemotePorts = new PortRange(500, 400) } }, RuleAction.Permit)),
            "端口 500..400");

    private static void FamilyMismatch() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(
                new[]
                {
                    new PolicyRule
                    {
                        Family = RuleAddressFamily.Ipv4,
                        RemoteNetwork = RemoteNetwork.Parse("2001:db8::/32"),
                    },
                },
                RuleAction.Permit)),
            "IPv4 规则配 IPv6 网段");

    private static void EmptyProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse(string.Empty), "空进程串");

    private static void ControlCharacterProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse("c:\\windows\\\tsvchost.exe"), "进程串含制表符");

    private static void NulProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse("c:\\windows\\svchost.exe\0x"), "进程串含 NUL");

    private static void LoneSurrogateProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse("c:\\\ud800\\svchost.exe"), "进程串含孤立代理项");

    private static void Ipv4PrefixOutOfRange() =>
        Check.Throws<ArgumentOutOfRangeException>(() => RemoteNetwork.Parse("10.1.2.3/33"), "IPv4 /33");

    private static void Ipv6PrefixOutOfRange() =>
        Check.Throws<ArgumentOutOfRangeException>(() => RemoteNetwork.Parse("2001:db8::/129"), "IPv6 /129");

    private static void MissingPrefix() =>
        Check.Throws<ArgumentException>(() => RemoteNetwork.Parse("10.1.2.3"), "缺少 /prefix");

    private static void BadAddress() =>
        Check.Throws<ArgumentException>(() => RemoteNetwork.Parse("10.1.2.999/24"), "地址非法");
    private static void InvalidNormalizedPath()
    {
        PolicyTable table = new(
            new[] { new PolicyRule { Process = ProcessSelector.Parse(@"c:\windows\system32\svchost.exe") } },
            RuleAction.Permit);

        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(table, NulNormalizer.Instance), "归一化结果含 NUL");
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(table, EmptyNormalizer.Instance), "归一化结果为空");
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(table, NullNormalizer.Instance), "归一化返回 null");
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(table, LoneSurrogateNormalizer.Instance), "归一化结果含孤立代理项");
        Check.Throws<ArgumentNullException>(
            () => RuleBlobCompiler.Compile(table, null!), "归一化器为 null");
    }

    private sealed class NulNormalizer : IProcessPathNormalizer
    {
        internal static NulNormalizer Instance { get; } = new();

        public string? Normalize(string path) => "a\0b";
    }

    private sealed class EmptyNormalizer : IProcessPathNormalizer
    {
        internal static EmptyNormalizer Instance { get; } = new();

        public string? Normalize(string path) => string.Empty;
    }

    private sealed class NullNormalizer : IProcessPathNormalizer
    {
        internal static NullNormalizer Instance { get; } = new();

        public string? Normalize(string path) => null;
    }

    private sealed class LoneSurrogateNormalizer : IProcessPathNormalizer
    {
        internal static LoneSurrogateNormalizer Instance { get; } = new();

        public string? Normalize(string path) => "c:\\\ud800\\svchost.exe";
    }

}
