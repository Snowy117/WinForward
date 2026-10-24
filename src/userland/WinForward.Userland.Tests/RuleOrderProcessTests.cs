using WinForward.Userland.Policy;
using static WinForward.Userland.Tests.RuleOrderSupport;

namespace WinForward.Userland.Tests;

internal static class RuleOrderProcessTests
{
    internal static void Register()
    {
        TestRunner.Add("rule-order-process/文件名与精确路径同段重叠被拒", FileNameVersusExactPath);
        TestRunner.Add("rule-order-process/文件名与不同精确路径被接受", FileNameVersusOtherPath);
        TestRunner.Add("rule-order-process/文件名与目录前缀重叠被拒", FileNameVersusDirectory);
        TestRunner.Add("rule-order-process/目录前缀遮蔽后代路径被拒", DirectoryPrefixShadowsChild);
        TestRunner.Add("rule-order-process/tools 不遮蔽 tools2", ToolsDoesNotShadowTools2);
        TestRunner.Add("rule-order-process/目录嵌套被拒", NestedDirectories);
        TestRunner.Add("rule-order-process/多端口任一相交即拒绝", MultiPortOverlap);
        TestRunner.Add("rule-order-process/多端口全部不相交被接受", MultiPortDisjoint);
        TestRunner.Add("rule-order-process/多网段任一相交即拒绝", MultiNetworkOverlap);
        TestRunner.Add("rule-order-process/多网段全部不相交被接受", MultiNetworkDisjoint);
        TestRunner.Add("rule-order-process/混合族数组按交集判定", MixedFamilies);
        TestRunner.Add("rule-order-process/网段族收窄有效族集", NetworkNarrowsFamily);
    }

    private static void FileNameVersusExactPath() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, processes: Processes("svchost.exe")),
            Rule(RuleAction.Block, processes: Processes(TestAppId.SvchostDos))), 0, 1);

    private static void FileNameVersusOtherPath() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, processes: Processes("other.exe")),
            Rule(RuleAction.Block, processes: Processes(TestAppId.SvchostDos))));

    private static void FileNameVersusDirectory()
    {
        PolicyCompileException? error = CaptureOrder(Permit(
            Rule(RuleAction.Permit, processes: Processes("svchost.exe")),
            Rule(RuleAction.Block, processes: Processes(TestAppId.System32Dos))));

        Check.True(error is not null, "文件名选择器可能命中目录下的同名文件");
        Check.True(error!.Message.Contains("#0", StringComparison.Ordinal), "消息要带更早的规则序号");
        Check.True(error.Message.Contains("#1", StringComparison.Ordinal), "消息要带 block 规则序号");
    }

    private static void DirectoryPrefixShadowsChild() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, processes: Processes(TestAppId.ToolsDos)),
            Rule(RuleAction.Block, processes: Processes(TestAppId.ToolsExeDos))), 0, 1);

    private static void ToolsDoesNotShadowTools2() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, processes: Processes(TestAppId.ToolsDos)),
            Rule(RuleAction.Block, processes: Processes(TestAppId.Tools2ExeDos))));

    private static void NestedDirectories() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, processes: Processes(TestAppId.MyAppDos)),
            Rule(RuleAction.Block, processes: Processes(TestAppId.MyAppBinExeDos))), 0, 1);

    private static void MultiPortOverlap() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, ports: Ports(80, 443)),
            Rule(RuleAction.Block, ports: Ports(443, 8080))), 0, 1);

    private static void MultiPortDisjoint() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, ports: Ports(80, 443)),
            Rule(RuleAction.Block, ports: Ports(8080, 8443))));

    private static void MultiNetworkOverlap() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, networks: Networks("10.0.0.0/8", "192.168.0.0/16")),
            Rule(RuleAction.Block, networks: Networks("10.1.0.0/16"))), 0, 1);

    private static void MultiNetworkDisjoint() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, networks: Networks("10.0.0.0/8", "192.168.0.0/16")),
            Rule(RuleAction.Block, networks: Networks("172.16.0.0/12"))));

    private static void MixedFamilies() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit,
                 families: new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 },
                 networks: Networks("10.0.0.0/8", "2001:db8::/32")),
            Rule(RuleAction.Block,
                 families: new[] { RuleAddressFamily.Ipv6 },
                 networks: Networks("2001:db8:1::/48"))), 0, 1);

    private static void NetworkNarrowsFamily() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, networks: Networks("10.0.0.0/8")),
            Rule(RuleAction.Block, families: new[] { RuleAddressFamily.Ipv6 })));
}
