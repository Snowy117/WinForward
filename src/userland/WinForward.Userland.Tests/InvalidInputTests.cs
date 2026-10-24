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
        TestRunner.Add("invalid/五个选择器空数组都拒绝", EmptyArrays);
        TestRunner.Add("invalid/数组里的 null 元素拒绝", NullElements);
        TestRunner.Add("invalid/进程串为空", EmptyProcess);
        TestRunner.Add("invalid/进程串含 C0 控制字符", ControlCharacterProcess);
        TestRunner.Add("invalid/进程串含 DEL", DelProcess);
        TestRunner.Add("invalid/进程串含 C1 控制字符", C1ControlProcess);
        TestRunner.Add("invalid/进程串含 NUL", NulProcess);
        TestRunner.Add("invalid/进程串含孤立代理项", LoneSurrogateProcess);
        TestRunner.Add("invalid/IPv4 前缀越界", Ipv4PrefixOutOfRange);
        TestRunner.Add("invalid/IPv6 前缀越界", Ipv6PrefixOutOfRange);
        TestRunner.Add("invalid/CIDR 缺少前缀", MissingPrefix);
        TestRunner.Add("invalid/CIDR 地址非法", BadAddress);
        TestRunner.Add("invalid/归一化结果含 NUL", NulNormalizedPath);
        TestRunner.Add("invalid/归一化返回 null", NullNormalizedPath);
        TestRunner.Add("invalid/归一化结果含孤立代理项", LoneSurrogateNormalizedPath);
        TestRunner.Add("invalid/归一化器为 null", NullNormalizer);
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
            () => TestAppId.Compile(new PolicyTable(
                new[] { new PolicyRule { Protocols = new[] { (RuleProtocol)18 } } },
                RuleAction.Permit)),
            "协议 18");

    private static void UnknownFamily() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(
                new[] { new PolicyRule { Families = new[] { (RuleAddressFamily)5 } } },
                RuleAction.Permit)),
            "地址族 5");

    private static void InvertedPorts() =>
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(
                new[] { new PolicyRule { RemotePorts = new[] { new PortRange(500, 400) } } },
                RuleAction.Permit)),
            "#0", "端口 500..400");

    private static void FamilyMismatch() =>
        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(new PolicyTable(
                new[]
                {
                    new PolicyRule
                    {
                        Families = new[] { RuleAddressFamily.Ipv4 },
                        RemoteNetworks = new[] { RemoteNetwork.Parse("2001:db8::/32") },
                    },
                },
                RuleAction.Permit)),
            "IPv4 规则配 IPv6 网段");

    private static void EmptyArrays()
    {
        (string Field, PolicyRule Rule)[] cases =
        {
            ("process", new PolicyRule { Processes = Array.Empty<ProcessSelector>() }),
            ("remoteNetwork", new PolicyRule { RemoteNetworks = Array.Empty<RemoteNetwork>() }),
            ("remotePorts", new PolicyRule { RemotePorts = Array.Empty<PortRange>() }),
            ("protocol", new PolicyRule { Protocols = Array.Empty<RuleProtocol>() }),
            ("family", new PolicyRule { Families = Array.Empty<RuleAddressFamily>() }),
        };

        foreach ((string field, PolicyRule rule) in cases)
        {
            Check.ThrowsContaining<PolicyCompileException>(
                () => TestAppId.Compile(new PolicyTable(new[] { rule }, RuleAction.Permit)),
                field, $"{field} 空数组必须拒绝");
        }
    }

    private static void NullElements()
    {
        (string Field, PolicyRule Rule)[] cases =
        {
            ("process", new PolicyRule { Processes = new ProcessSelector[] { null! } }),
            ("remoteNetwork", new PolicyRule { RemoteNetworks = new RemoteNetwork[] { null! } }),
        };

        foreach ((string field, PolicyRule rule) in cases)
        {
            Check.ThrowsContaining<PolicyCompileException>(
                () => TestAppId.Compile(new PolicyTable(new[] { rule }, RuleAction.Permit)),
                field, $"{field} 的 null 元素必须拒绝");
        }
    }

    private static void EmptyProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse(string.Empty), "空进程串");

    private static void ControlCharacterProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse("c:\\windows\\\tsvchost.exe"), "进程串含制表符");

    private static void DelProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse("c:\\windows\\\u007Fsvchost.exe"), "进程串含 DEL");

    private static void C1ControlProcess() =>
        Check.Throws<ArgumentException>(() => ProcessSelector.Parse("c:\\windows\\\u0085svchost.exe"), "进程串含 C1");

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

    private static void NulNormalizedPath()
    {
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), new FixedNormalizer(new ProcessPathResolution("\\device\\a\0b", false))),
            "归一化结果含 NUL");
    }

    private static void NullNormalizedPath()
    {
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), new FixedNormalizer(null)),
            "归一化返回 null");
    }

    private static void LoneSurrogateNormalizedPath()
    {
        Check.Throws<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(Table(), new FixedNormalizer(new ProcessPathResolution("\\device\\c:\\\ud800\\svchost.exe", false))),
            "归一化结果含孤立代理项");
    }

    private static void NullNormalizer()
    {
        Check.Throws<ArgumentNullException>(
            () => RuleBlobCompiler.Compile(Table(), null!), "归一化器为 null");
    }

    private static PolicyTable Table() => new(
        new[] { new PolicyRule { Processes = new[] { ProcessSelector.Parse(@"c:\windows\system32\svchost.exe") } } },
        RuleAction.Permit);

    private sealed class FixedNormalizer : IProcessPathNormalizer
    {
        private readonly ProcessPathResolution? _resolution;

        internal FixedNormalizer(ProcessPathResolution? resolution) => _resolution = resolution;

        public ProcessPathResolution? Normalize(string path) => _resolution;
    }
}
