using WinForward.Userland.Policy;
using static WinForward.Userland.Tests.RuleOrderSupport;

namespace WinForward.Userland.Tests;

/// <summary>
/// 编译期顺序冲突检查的行为矩阵。每一条都对应驱动 <c>rule_order_valid</c> 的一个判定条件，
/// 删掉被测条件会让对应的「被接受」用例变红（被误拒）。
/// </summary>
internal static class RuleOrderTests
{
    internal static void Register()
    {
        TestRunner.Add("rule-order/完全重叠的 permit 使 block 被拒", FullyOverlappingPermit);
        TestRunner.Add("rule-order/端口区间相交被拒", OverlappingPorts);
        TestRunner.Add("rule-order/端口区间不相交被接受", DisjointPorts);
        TestRunner.Add("rule-order/地址族不相交被接受", DisjointFamilies);
        TestRunner.Add("rule-order/任一侧地址族为任意即重叠被拒", FamilyAnyOverlaps);
        TestRunner.Add("rule-order/网段不相交被接受", DisjointNetworks);
        TestRunner.Add("rule-order/网段嵌套被拒", NestedNetworks);
        TestRunner.Add("rule-order/任一侧网段为任意即重叠被拒", NetworkAnyOverlaps);
        TestRunner.Add("rule-order/进程选择器不同被接受", DisjointProcesses);
        TestRunner.Add("rule-order/进程选择器相同被拒", SameProcess);
        TestRunner.Add("rule-order/任一侧进程为任意即重叠被拒", ProcessAnyOverlaps);
        TestRunner.Add("rule-order/两条 block 规则互不检查", TwoBlockRules);
        TestRunner.Add("rule-order/block 在前的 permit 不影响", BlockBeforePermit);
        TestRunner.Add("rule-order/redirect 也会遮蔽 block", RedirectShadowsBlock);
        TestRunner.Add("rule-order/兜底动作为 block 且无规则被接受", FallbackBlockOnly);
        TestRunner.Add("rule-order/IPv6 网段推出地址族", Ipv6FamilyFromNetwork);
        TestRunner.Add("rule-order/报错消息带两条规则的条件摘要", MessageMentionsBothRules);
        TestRunner.Add("rule-order/协议谓词对非 TCP 也生效", ProtocolPredicate);
        TestRunner.Add("rule-order/TCP 与 UDP 规则不重叠", TcpAndUdpDoNotOverlap);
        TestRunner.Add("rule-order/协议文案会显示 udp", UdpProtocolIsDescribed);
        TestRunner.Add("rule-order/绕过检查可以产出驱动会拒绝的 blob", UncheckedCompileProducesBlob);
        TestRunner.Add("rule-order/兜底 block 与 permit 规则共存被拒", FallbackBlockWithPermit);
        TestRunner.Add("rule-order/兜底 block 与全部 block 规则被接受", FallbackBlockWithOnlyBlocks);
        TestRunner.Add("rule-order/兜底 permit 与混合规则被接受", FallbackPermitWithMixedRules);
        TestRunner.Add("rule-order/进程选择器按 ASCII 折叠比较", AsciiFoldedProcessComparison);
    }

    private static void FullyOverlappingPermit() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(80))), earlier: 0, later: 1);

    private static void OverlappingPorts() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, ports: Ports(80, 90)),
            Rule(RuleAction.Block, ports: Ports(85, 95))), earlier: 0, later: 1);

    private static void DisjointPorts() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(81))));

    private static void DisjointFamilies() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, families: new[] { RuleAddressFamily.Ipv4 }, ports: Ports(80)),
            Rule(RuleAction.Block, families: new[] { RuleAddressFamily.Ipv6 }, ports: Ports(80))));

    private static void FamilyAnyOverlaps() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, families: new[] { RuleAddressFamily.Ipv6 }, ports: Ports(80))), 0, 1);

    private static void DisjointNetworks() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, networks: Networks("10.0.0.0/8")),
            Rule(RuleAction.Block, networks: Networks("192.168.0.0/16"))));

    private static void NestedNetworks() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, networks: Networks("10.0.0.0/8")),
            Rule(RuleAction.Block, networks: Networks("10.1.0.0/16"))), 0, 1);

    private static void NetworkAnyOverlaps() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, networks: Networks("10.0.0.0/8")),
            Rule(RuleAction.Block)), 0, 1);

    private static void DisjointProcesses() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, processes: Processes(TestAppId.AppDos)),
            Rule(RuleAction.Block, processes: Processes(TestAppId.OtherDos))));

    private static void SameProcess() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, processes: Processes(TestAppId.AppDos)),
            Rule(RuleAction.Block, processes: Processes(@"C:\PROGRAM FILES\APP\APP.EXE"))), 0, 1);

    private static void ProcessAnyOverlaps() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, processes: Processes(TestAppId.AppDos)),
            Rule(RuleAction.Block)), 0, 1);

    private static void TwoBlockRules() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Block, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(80))));

    private static void BlockBeforePermit() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Block, ports: Ports(80)),
            Rule(RuleAction.Permit, ports: Ports(80))));

    private static void RedirectShadowsBlock() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Redirect, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(80))), 0, 1);

    private static void FallbackBlockOnly() =>
        ExpectAccepted(Table(RuleAction.Block));

    private static void Ipv6FamilyFromNetwork() =>
        ExpectRejected(Permit(
            Rule(RuleAction.Permit, networks: Networks("2001:db8::/32")),
            Rule(RuleAction.Block, networks: Networks("2001:db8:1::/48"))), 0, 1);

    private static void MessageMentionsBothRules()
    {
        PolicyCompileException? error = Capture(Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(80))));

        Check.True(error is not null, "重叠规则必须拒绝编译");
        Check.True(error!.Message.Contains("#0", StringComparison.Ordinal), "消息要带更早的规则序号 #0");
        Check.True(error.Message.Contains("#1", StringComparison.Ordinal), "消息要带 block 规则序号 #1");
        Check.True(error.Message.Contains("block", StringComparison.Ordinal), "消息要说明后面的动作是 block");
        Check.True(error.Message.Contains("permit", StringComparison.Ordinal), "消息要说明更早的规则是 permit");
    }

    private static void ProtocolPredicate()
    {
        RuleBlobRule permitUdp = new() { ProtocolMask = RuleBlobLayout.ProtocolUdpBit };
        RuleBlobRule blockTcp = new() { ProtocolMask = RuleBlobLayout.ProtocolTcpBit };
        RuleBlobRule blockUdp = blockTcp;
        blockUdp.ProtocolMask = RuleBlobLayout.ProtocolUdpBit;

        Check.True(!RuleOrderValidator.ProtocolsOverlap(permitUdp, blockTcp), "UDP 与 TCP 不相容");
        Check.True(RuleOrderValidator.ProtocolsOverlap(permitUdp, blockUdp), "同为 UDP 相容");
        permitUdp.ProtocolMask = 0;
        Check.True(RuleOrderValidator.ProtocolsOverlap(permitUdp, blockTcp), "任意协议与 TCP 相容");
    }

    private static void TcpAndUdpDoNotOverlap() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, protocols: new[] { RuleProtocol.Tcp }, ports: Ports(80)),
            Rule(RuleAction.Block, protocols: new[] { RuleProtocol.Udp }, ports: Ports(80))));

    private static void UdpProtocolIsDescribed()
    {
        PolicyCompileException? error = Capture(Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, protocols: new[] { RuleProtocol.Udp }, ports: Ports(80))));

        Check.True(error is not null, "任意协议 permit 会遮蔽 UDP block 规则");
        Check.True(error!.Message.Contains(" udp ", StringComparison.Ordinal), "消息里的 block 规则条件要显示 udp");
    }

    private static void UncheckedCompileProducesBlob()
    {
        PolicyTable table = Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(80)));

        Check.True(Capture(table) is not null, "受检查的编译必须拒绝");
        byte[] blob = RuleBlobCompiler.CompileUnchecked(table, TestAppId.Normalizer);
        Check.Equal(128, blob.Length, "绕过检查后确实产出 56+32*2+4*2 字节的 blob");
    }

    private static void FallbackBlockWithPermit()
    {
        PolicyTable table = Table(
            RuleAction.Block,
            Rule(RuleAction.Permit, ports: Ports(80)));

        PolicyCompileException? error = Capture(table);
        Check.True(error is not null, "兜底 block 与 permit 规则共存必须拒绝");
        Check.True(error!.Message.Contains("兜底动作", StringComparison.Ordinal), "消息要指向兜底动作");
        Check.True(error.Message.Contains("#0", StringComparison.Ordinal), "消息要指出被废掉的规则序号");
    }

    private static void FallbackBlockWithOnlyBlocks() =>
        ExpectAccepted(Table(
            RuleAction.Block,
            Rule(RuleAction.Block, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(81))));

    private static void FallbackPermitWithMixedRules() =>
        ExpectAccepted(Permit(
            Rule(RuleAction.Permit, ports: Ports(80)),
            Rule(RuleAction.Block, ports: Ports(81))));

    private static void AsciiFoldedProcessComparison()
    {
        PolicyCompileException? error = Capture(
            Permit(
                Rule(RuleAction.Permit, processes: Processes(@"C:\APP.EXE")),
                Rule(RuleAction.Block, processes: Processes(@"c:\app.exe"))),
            IdentityNormalizer.Instance);

        Check.True(error is not null, "只有大小写不同的进程选择器必须按重叠处理");
        Check.True(error!.Message.Contains("#0", StringComparison.Ordinal), "消息要带更早的规则序号");
    }

    private sealed class IdentityNormalizer : IProcessPathNormalizer
    {
        internal static IdentityNormalizer Instance { get; } = new();

        public ProcessPathResolution? Normalize(string path) =>
            new(@"\device\harddiskvolume1" + path[2..], isDirectory: false);
    }
}
