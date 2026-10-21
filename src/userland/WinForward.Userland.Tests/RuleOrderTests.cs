using WinForward.Userland.Policy;

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
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void OverlappingPorts() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, ports: new PortRange(80, 90)),
                Rule(RuleAction.Block, ports: new PortRange(85, 95)),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void DisjointPorts() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(81)),
            },
            RuleAction.Permit));

    private static void DisjointFamilies() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, family: RuleAddressFamily.Ipv4, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, family: RuleAddressFamily.Ipv6, ports: PortRange.Single(80)),
            },
            RuleAction.Permit));

    private static void FamilyAnyOverlaps() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, family: RuleAddressFamily.Ipv6, ports: PortRange.Single(80)),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void DisjointNetworks() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, network: RemoteNetwork.Parse("10.0.0.0/8")),
                Rule(RuleAction.Block, network: RemoteNetwork.Parse("192.168.0.0/16")),
            },
            RuleAction.Permit));

    private static void NestedNetworks() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, network: RemoteNetwork.Parse("10.0.0.0/8")),
                Rule(RuleAction.Block, network: RemoteNetwork.Parse("10.1.0.0/16")),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void NetworkAnyOverlaps() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, network: RemoteNetwork.Parse("10.0.0.0/8")),
                Rule(RuleAction.Block),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void DisjointProcesses() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.OtherDos)),
            },
            RuleAction.Permit));

    private static void SameProcess() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                Rule(RuleAction.Block, process: ProcessSelector.Parse(@"C:\PROGRAM FILES\APP\APP.EXE")),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void ProcessAnyOverlaps() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                Rule(RuleAction.Block),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void TwoBlockRules() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
            },
            RuleAction.Permit));

    private static void BlockBeforePermit() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
            },
            RuleAction.Permit));

    private static void RedirectShadowsBlock() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Redirect, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void FallbackBlockOnly() =>
        ExpectAccepted(new PolicyTable(Array.Empty<PolicyRule>(), RuleAction.Block));

    private static void Ipv6FamilyFromNetwork() =>
        ExpectRejected(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, network: RemoteNetwork.Parse("2001:db8::/32")),
                Rule(RuleAction.Block, network: RemoteNetwork.Parse("2001:db8:1::/48")),
            },
            RuleAction.Permit), earlier: 0, later: 1);

    private static void MessageMentionsBothRules()
    {
        PolicyTable table = new(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
            },
            RuleAction.Permit);

        PolicyCompileException? error = Capture(table);
        Check.True(error is not null, "重叠规则必须拒绝编译");
        Check.True(error!.Message.Contains("#0", StringComparison.Ordinal), "消息要带更早的规则序号 #0");
        Check.True(error.Message.Contains("#1", StringComparison.Ordinal), "消息要带 block 规则序号 #1");
        Check.True(error.Message.Contains("block", StringComparison.Ordinal), "消息要说明后面的动作是 block");
        Check.True(error.Message.Contains("permit", StringComparison.Ordinal), "消息要说明更早的规则是 permit");
    }

    private static void ProtocolPredicate()
    {
        // 协议判定是数值比较：任一侧为 Any(0) 即相容，具体协议必须相等。这里直接用
        // 编译后的 blob 条目喂谓词，钉住 UDP(17) 加入后这条判定没有退化。
        RuleBlobRule permitUdp = new()
        {
            Action = (byte)RuleAction.Permit,
            Protocol = 17,
            RemotePortStart = 1,
            RemotePortEnd = 65535,
        };
        RuleBlobRule blockTcp = new()
        {
            Action = (byte)RuleAction.Block,
            Protocol = 6,
            RemotePortStart = 1,
            RemotePortEnd = 65535,
        };
        RuleBlobRule blockUdp = blockTcp;
        blockUdp.Protocol = 17;

        Check.True(!RuleOrderValidator.ProtocolsOverlap(permitUdp, blockTcp), "UDP 与 TCP 不相容");
        Check.True(RuleOrderValidator.ProtocolsOverlap(permitUdp, blockUdp), "同为 UDP 相容");
        permitUdp.Protocol = (byte)RuleProtocol.Any;
        Check.True(RuleOrderValidator.ProtocolsOverlap(permitUdp, blockTcp), "任意协议与 TCP 相容");
    }

    private static void TcpAndUdpDoNotOverlap() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, protocol: RuleProtocol.Tcp, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, protocol: RuleProtocol.Udp, ports: PortRange.Single(80)),
            },
            RuleAction.Permit));

    private static void UdpProtocolIsDescribed()
    {
        PolicyTable table = new(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, protocol: RuleProtocol.Udp, ports: PortRange.Single(80)),
            },
            RuleAction.Permit);

        PolicyCompileException? error = Capture(table);
        Check.True(error is not null, "任意协议 permit 会遮蔽 UDP block 规则");
        Check.True(
            error!.Message.Contains(" udp ", StringComparison.Ordinal),
            "消息里的 block 规则条件要显示 udp");
    }

    private static void UncheckedCompileProducesBlob()
    {
        PolicyTable table = new(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
            },
            RuleAction.Permit);

        Check.True(Capture(table) is not null, "受检查的编译必须拒绝");
        byte[] blob = RuleBlobCompiler.CompileUnchecked(table, TestAppId.Normalizer);
        Check.Equal(72, blob.Length, "绕过检查后确实产出 40+16*2 字节的 blob");
    }

    private static PolicyRule Rule(RuleAction action,
                                   RuleAddressFamily family = RuleAddressFamily.Any,
                                   RuleProtocol protocol = RuleProtocol.Any,
                                   PortRange? ports = null,
                                   RemoteNetwork? network = null,
                                   ProcessSelector? process = null) =>
        new()
        {
            Action = action,
            Family = family,
            Protocol = protocol,
            RemotePorts = ports ?? PortRange.Any,
            RemoteNetwork = network,
            Process = process,
        };

    private static void ExpectAccepted(PolicyTable table)
    {
        byte[] blob = RuleBlobCompiler.Compile(table, TestAppId.Normalizer);
        Check.True(blob.Length >= 40, "编译成功且产出 blob");
        Check.True(Capture(table) is null, "这条规则表不应触发顺序冲突");
    }

    private static void ExpectRejected(PolicyTable table, int earlier, int later)
    {
        PolicyCompileException? error = Capture(table);
        Check.True(error is not null, $"规则 #.{earlier} 应遮蔽 #.{later}，必须拒绝");
        Check.True(error!.Message.Contains($"#{earlier}", StringComparison.Ordinal), $"消息缺更早的规则序号 #{earlier}");
        Check.True(error.Message.Contains($"#{later}", StringComparison.Ordinal), $"消息缺 block 规则序号 #{later}");
    }

    private static PolicyCompileException? Capture(PolicyTable table, IProcessPathNormalizer? normalizer = null)
    {
        try
        {
            RuleBlobCompiler.Compile(table, normalizer ?? TestAppId.Normalizer);
            return null;
        }
        catch (PolicyCompileException exception)
        {
            return exception;
        }
    }

    private static void FallbackBlockWithPermit()
    {
        PolicyTable table = new(
            new[] { Rule(RuleAction.Permit, ports: PortRange.Single(80)) },
            RuleAction.Block);

        PolicyCompileException? error = Capture(table);
        Check.True(error is not null, "兜底 block 与 permit 规则共存必须拒绝");
        Check.True(error!.Message.Contains("兜底动作", StringComparison.Ordinal), "消息要指向兜底动作");
        Check.True(error.Message.Contains("#0", StringComparison.Ordinal), "消息要指出被废掉的规则序号");
    }

    private static void FallbackBlockWithOnlyBlocks() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Block, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(81)),
            },
            RuleAction.Block));

    private static void FallbackPermitWithMixedRules() =>
        ExpectAccepted(new PolicyTable(
            new[]
            {
                Rule(RuleAction.Permit, ports: PortRange.Single(80)),
                Rule(RuleAction.Block, ports: PortRange.Single(81)),
            },
            RuleAction.Permit));

    private static void AsciiFoldedProcessComparison()
    {
        // 故意注入不折叠大小写的解析器：规则池里会出现大小写不同的两条串。
        // matcher 比较连接侧路径时会折叠 ASCII，所以这两条规则会命中同一条连接，
        // 顺序冲突必须按同一语义判定——只有大小写不同也算重叠。
        PolicyTable table = new(
            new[]
            {
                Rule(RuleAction.Permit, process: ProcessSelector.Parse(@"C:\APP.EXE")),
                Rule(RuleAction.Block, process: ProcessSelector.Parse(@"c:\app.exe")),
            },
            RuleAction.Permit);

        PolicyCompileException? error = Capture(table, IdentityNormalizer.Instance);
        Check.True(error is not null, "只有大小写不同的进程选择器必须按重叠处理");
        Check.True(error!.Message.Contains("#0", StringComparison.Ordinal), "消息要带更早的规则序号");
    }

    private sealed class IdentityNormalizer : IProcessPathNormalizer
    {
        internal static IdentityNormalizer Instance { get; } = new();

        public string? Normalize(string path) => path;
    }
}

