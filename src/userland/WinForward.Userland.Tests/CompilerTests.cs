using System.Text;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class CompilerTests
{
    private const string Svchost = TestAppId.SvchostDos;
    private const string App = TestAppId.AppDos;

    internal static void Register()
    {
        TestRunner.Add("compiler/空规则表只产出 56 字节头部", EmptyTable);
        TestRunner.Add("compiler/五个段起点 4 字节对齐且不重叠", Segments);
        TestRunner.Add("compiler/规则区间与字段原样写入", RuleFields);
        TestRunner.Add("compiler/协议与地址族写成位掩码", Masks);
        TestRunner.Add("compiler/进程串小写、去重、NUL 结尾", ProcessPool);
        TestRunner.Add("compiler/进程串偏移是池内字节偏移", ProcessIndexes);
        TestRunner.Add("compiler/网段按规则追加且主机位清零", NetworkPool);
        TestRunner.Add("compiler/IPv4 地址只占前 4 字节", NetworkIpv4Padding);
        TestRunner.Add("compiler/网段下标按记录数递增", NetworkIndexes);
        TestRunner.Add("compiler/任意选择器编码为计数 0 与首下标 0", AnySelectors);
        TestRunner.Add("compiler/兜底动作写入头部", FallbackAction);
        TestRunner.Add("compiler/内网 permit 后 block 其余被接受", PermitInternalThenBlockRest);
        TestRunner.Add("compiler/兜底 block 与 permit 规则共存被接受", FallbackBlockWithRules);
        TestRunner.Add("compiler/规则与选择器保留位为 0", ReservedIsZero);
        TestRunner.Add("compiler/端口排序合并相邻与重叠区间", PortMerge);
        TestRunner.Add("compiler/端口区间端点原样写入", PortEndpoints);
        TestRunner.Add("compiler/显式端口 0 被拒绝", PortZeroRejected);
        TestRunner.Add("compiler/PortRange.Any 仍表示任意端口", PortAnyAccepted);
        TestRunner.Add("compiler/进程串池末尾是 NUL", ProcessPoolTail);
        TestRunner.Add("compiler/进程串池按 UTF-8 字节计数", ProcessPoolUtf8Bytes);
        TestRunner.Add("compiler/一条配置规则只产出一条 blob 规则", OneRuleOneBlobRule);
    }

    private static void EmptyTable()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(Array.Empty<PolicyRule>(), RuleAction.Block));
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(56, blob.Length, "空表 blob 长度");
        Check.Equal(0x31425257U, header.Magic, "magic");
        Check.Equal((ushort)2, header.Version, "version");
        Check.Equal((ushort)56, header.HeaderSize, "header_size");
        Check.Equal(56U, header.TotalSize, "total_size");
        Check.Equal(0U, header.RuleCount, "rule_count");
        Check.Equal(1U, header.FallbackAction, "fallback_action");
        Check.Equal(56U, header.RulesOffset, "rules_offset");
        Check.Equal(56U, header.ProcessPoolOffset, "process_pool_offset");
        Check.Equal(0U, header.ProcessPoolSize, "process_pool_size");
        Check.Equal(56U, header.SelectorPoolOffset, "selector_pool_offset");
        Check.Equal(0U, header.SelectorPoolSize, "selector_pool_size");
        Check.Equal(56U, header.NetworkPoolOffset, "network_pool_offset");
        Check.Equal(0U, header.NetworkPoolSize, "network_pool_size");
        Check.Equal(56U, header.PortPoolOffset, "port_pool_offset");
        Check.Equal(0U, header.PortPoolSize, "port_pool_size");
    }

    private static void Segments()
    {
        byte[] blob = TestAppId.Compile(SampleTable());
        RuleBlobHeader header = BlobReader.Header(blob);
        int rulesEnd = (int)header.RulesOffset + ((int)header.RuleCount * RuleBlobLayout.RuleSize);
        int processEnd = (int)header.ProcessPoolOffset + (int)header.ProcessPoolSize;
        int selectorEnd = (int)header.SelectorPoolOffset + (int)header.SelectorPoolSize;
        int networkEnd = (int)header.NetworkPoolOffset + (int)header.NetworkPoolSize;
        int portEnd = (int)header.PortPoolOffset + (int)header.PortPoolSize;

        Check.Equal(blob.Length, (int)header.TotalSize, "total_size 等于实际长度");
        foreach ((string name, uint offset) in new[]
                 {
                     ("rules", header.RulesOffset),
                     ("process", header.ProcessPoolOffset),
                     ("selector", header.SelectorPoolOffset),
                     ("network", header.NetworkPoolOffset),
                     ("port", header.PortPoolOffset),
                 })
        {
            Check.Equal(0, (int)offset % 4, $"{name}_offset 4 字节对齐");
        }

        Check.Equal(56, (int)header.RulesOffset, "rules_offset 紧接头部");
        Check.True(rulesEnd <= (int)header.ProcessPoolOffset, "规则数组不越过进程池");
        Check.True(processEnd <= (int)header.SelectorPoolOffset, "进程池不越选择器池");
        Check.True(selectorEnd <= (int)header.NetworkPoolOffset, "选择器池不越网段池");
        Check.True(networkEnd <= (int)header.PortPoolOffset, "网段池不越端口池");
        Check.Equal(blob.Length, portEnd, "端口池结束于 blob 末尾");
    }

    private static void RuleFields()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule
                {
                    Name = "first",
                    Processes = new[] { ProcessSelector.Parse(Svchost) },
                    RemoteNetworks = new[] { RemoteNetwork.Parse("10.1.2.3/32") },
                    RemotePorts = new[] { PortRange.Single(443) },
                    Protocols = new[] { RuleProtocol.Tcp },
                    Families = new[] { RuleAddressFamily.Ipv4 },
                    Action = RuleAction.Redirect,
                },
                new PolicyRule
                {
                    Name = "second",
                    RemotePorts = new[] { new PortRange(1000, 2000) },
                    Action = RuleAction.Block,
                },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobRule first = BlobReader.Rule(blob, 0);
        RuleBlobRule second = BlobReader.Rule(blob, 1);

        Check.Equal(1U, first.ProcessCount, "规则 0 进程数");
        Check.Equal(1U, first.NetworkCount, "规则 0 网段数");
        Check.Equal(1U, first.PortCount, "规则 0 端口区间数");
        Check.Equal(0U, first.ProcessFirst, "规则 0 进程首下标");
        Check.Equal(0U, first.NetworkFirst, "规则 0 网段首下标");
        Check.Equal(0U, first.PortFirst, "规则 0 端口首下标");
        Check.Equal((byte)1, first.ProtocolMask, "规则 0 协议掩码 tcp");
        Check.Equal((byte)1, first.FamilyMask, "规则 0 地址族掩码 ipv4");
        Check.Equal((byte)2, first.Action, "规则 0 动作");
        Check.Equal(0U, second.ProcessCount, "规则 1 进程任意");
        Check.Equal(0U, second.ProcessFirst, "计数 0 时首下标必须是 0");
        Check.Equal(0U, second.NetworkCount, "规则 1 网段任意");
        Check.Equal(0U, second.NetworkFirst, "计数 0 时首下标必须是 0");
        Check.Equal(1U, second.PortCount, "规则 1 端口区间数");
        Check.Equal(1U, second.PortFirst, "规则 1 端口首下标接在规则 0 之后");
        Check.Equal((byte)0, second.ProtocolMask, "规则 1 协议默认任意");
        Check.Equal((byte)1, second.Action, "规则 1 动作");
    }

    private static void Masks()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[]
            {
                new PolicyRule { Protocols = new[] { RuleProtocol.Tcp } },
                new PolicyRule { Protocols = new[] { RuleProtocol.Udp } },
                new PolicyRule { Protocols = new[] { RuleProtocol.Tcp, RuleProtocol.Udp } },
                new PolicyRule { Protocols = new[] { RuleProtocol.Any, RuleProtocol.Tcp } },
                new PolicyRule { Families = new[] { RuleAddressFamily.Ipv4 } },
                new PolicyRule { Families = new[] { RuleAddressFamily.Ipv6 } },
                new PolicyRule { Families = new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 } },
                new PolicyRule { Families = new[] { RuleAddressFamily.Any, RuleAddressFamily.Ipv6 } },
            },
            RuleAction.Permit));

        Check.Equal((byte)1, BlobReader.Rule(blob, 0).ProtocolMask, "tcp 掩码");
        Check.Equal((byte)2, BlobReader.Rule(blob, 1).ProtocolMask, "udp 掩码");
        Check.Equal((byte)3, BlobReader.Rule(blob, 2).ProtocolMask, "tcp+udp 掩码");
        Check.Equal((byte)0, BlobReader.Rule(blob, 3).ProtocolMask, "any 优先于具体值");
        Check.Equal((byte)1, BlobReader.Rule(blob, 4).FamilyMask, "ipv4 掩码");
        Check.Equal((byte)2, BlobReader.Rule(blob, 5).FamilyMask, "ipv6 掩码");
        Check.Equal((byte)3, BlobReader.Rule(blob, 6).FamilyMask, "ipv4+ipv6 掩码");
        Check.Equal((byte)0, BlobReader.Rule(blob, 7).FamilyMask, "any 优先于具体值");
    }

    private static void ProcessPool()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(@"C:\Windows\SYSTEM32\SVCHOST.EXE") }, Action = RuleAction.Block },
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(Svchost) }, Action = RuleAction.Redirect },
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(App) }, Action = RuleAction.Permit },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);

        string expectedPool = TestAppId.SvchostDevice + "\0" + TestAppId.AppDevice + "\0";
        Check.Equal(Encoding.UTF8.GetByteCount(expectedPool), (int)header.ProcessPoolSize, "process_pool_size 是精确串字节数");
        Check.Equal(0, blob[(int)header.ProcessPoolOffset + (int)header.ProcessPoolSize - 1], "池尾是 NUL");
        Check.Equal(3U, header.SelectorPoolSize / RuleBlobLayout.ProcessSelectorSize, "每条规则各有一条 selector");

        Check.Equal(0U, BlobReader.Selector(blob, 0).StringOffset, "规则 0 指向池首");
        Check.Equal(0U, BlobReader.Selector(blob, 1).StringOffset, "大小写不同的同一路径复用同一条串");
        Check.Equal((uint)Encoding.UTF8.GetByteCount(TestAppId.SvchostDevice) + 1U,
                    BlobReader.Selector(blob, 2).StringOffset, "规则 2 指向 app 串首");
        Check.Equal(TestAppId.SvchostDevice, BlobReader.ProcessString(blob, 0), "池内是设备路径且已小写");
    }

    private static void ProcessIndexes()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(Svchost) } },
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(App) } },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        uint second = BlobReader.Selector(blob, 1).StringOffset;

        Check.Equal((uint)Encoding.UTF8.GetByteCount(TestAppId.SvchostDevice) + 1U, second, "第二条串的偏移是池内字节偏移");
        Check.Equal(TestAppId.AppDevice, BlobReader.ProcessString(blob, second), "偏移指向第二条串的串首");
    }

    private static void NetworkPool()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { RemoteNetworks = new[] { RemoteNetwork.Parse("10.1.9.77/24") } },
                new PolicyRule { RemoteNetworks = new[] { RemoteNetwork.Parse("10.1.9.1/24") } },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(40U, header.NetworkPoolSize, "v2 按规则追加记录，两条同网段各占 20 字节");
        Check.Equal(0U, BlobReader.Rule(blob, 0).NetworkFirst, "规则 0 指向池首");
        Check.Equal(1U, BlobReader.Rule(blob, 1).NetworkFirst, "规则 1 指向第二条记录");

        for (int index = 0; index < 2; index++)
        {
            RuleBlobNetworkPrefix prefix = BlobReader.NetworkPrefix(blob, index);
            Check.Equal((byte)24, prefix.PrefixLength, $"记录 {index} 的 prefix_length");
            Check.Equal((byte)4, prefix.Family, $"记录 {index} 的 family");
            Check.Equal((byte)0, prefix.Reserved0, $"记录 {index} 的 reserved[0]");
            Check.Equal((byte)0, prefix.Reserved1, $"记录 {index} 的 reserved[1]");
            Check.BytesEqual(new byte[] { 10, 1, 9, 0 }, BlobReader.NetworkAddress(prefix).AsSpan(0, 4), $"记录 {index} 主机位已清零");
        }
    }

    private static void NetworkIpv4Padding()
    {
        PolicyTable table = new(
            new[] { new PolicyRule { RemoteNetworks = new[] { RemoteNetwork.Parse("192.168.1.0/24") } } },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobNetworkPrefix prefix = BlobReader.NetworkPrefix(blob, 0);

        Check.BytesEqual(new byte[] { 192, 168, 1, 0 }, BlobReader.NetworkAddress(prefix).AsSpan(0, 4), "IPv4 地址");
        Check.BytesEqual(new byte[12], BlobReader.NetworkAddress(prefix).AsSpan(4, 12), "IPv4 的后 12 字节补零");
    }

    private static void NetworkIndexes()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { RemoteNetworks = new[] { RemoteNetwork.Parse("10.0.0.0/8") } },
                new PolicyRule { RemoteNetworks = new[] { RemoteNetwork.Parse("192.168.0.0/16") } },
                new PolicyRule { RemoteNetworks = new[] { RemoteNetwork.Parse("2001:db8::/32") } },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);

        Check.Equal(0U, BlobReader.Rule(blob, 0).NetworkFirst, "规则 0");
        Check.Equal(1U, BlobReader.Rule(blob, 1).NetworkFirst, "规则 1");
        Check.Equal(2U, BlobReader.Rule(blob, 2).NetworkFirst, "规则 2");
        Check.Equal(3U, BlobReader.Header(blob).NetworkPoolSize / RuleBlobLayout.NetworkPrefixSize, "网段池记录数");
    }

    private static void AnySelectors()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule() },
            RuleAction.Permit));
        RuleBlobRule rule = BlobReader.Rule(blob, 0);

        Check.Equal(0U, rule.ProcessCount, "process_count 任意");
        Check.Equal(0U, rule.ProcessFirst, "process_first 必须是 0");
        Check.Equal(0U, rule.NetworkCount, "network_count 任意");
        Check.Equal(0U, rule.NetworkFirst, "network_first 必须是 0");
        Check.Equal(0U, rule.PortCount, "port_count 任意");
        Check.Equal(0U, rule.PortFirst, "port_first 必须是 0");
        Check.Equal((byte)0, rule.ProtocolMask, "protocol_mask 任意");
        Check.Equal((byte)0, rule.FamilyMask, "family_mask 任意");
    }

    private static void FallbackAction()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule() },
            RuleAction.Redirect));

        Check.Equal(2U, BlobReader.Header(blob).FallbackAction, "fallback_action 写进头部");
    }

    private static void PermitInternalThenBlockRest()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule
                {
                    RemoteNetworks = new[] { RemoteNetwork.Parse("10.0.0.0/8") },
                    Action = RuleAction.Permit,
                },
                new PolicyRule { Action = RuleAction.Block },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);
        RuleBlobRule permit = BlobReader.Rule(blob, 0);
        RuleBlobRule block = BlobReader.Rule(blob, 1);

        Check.Equal(0U, header.FallbackAction, "兜底仍是 permit");
        Check.Equal(2U, header.RuleCount, "两条规则都保留");
        Check.Equal((byte)RuleAction.Permit, permit.Action, "规则 0 是 permit");
        Check.Equal(1U, permit.NetworkCount, "规则 0 保留 10.0.0.0/8");
        Check.Equal((byte)RuleAction.Block, block.Action, "规则 1 是 block");
        Check.Equal(0U, block.NetworkCount, "规则 1 不限网段");
    }

    private static void FallbackBlockWithRules()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule
                {
                    RemotePorts = new[] { PortRange.Single(443) },
                    Action = RuleAction.Permit,
                },
            },
            RuleAction.Block);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);
        RuleBlobRule permit = BlobReader.Rule(blob, 0);

        Check.Equal(1U, header.FallbackAction, "兜底动作是 block");
        Check.Equal(1U, header.RuleCount, "permit 规则保留");
        Check.Equal((byte)RuleAction.Permit, permit.Action, "规则 0 是 permit");
        Check.Equal(1U, permit.PortCount, "规则 0 保留 443 端口");
    }

    private static void ReservedIsZero()
    {
        byte[] blob = TestAppId.Compile(SampleTable());

        for (int index = 0; index < (int)BlobReader.Header(blob).RuleCount; index++)
        {
            Check.Equal((byte)0, BlobReader.Rule(blob, index).Reserved, $"规则 {index} 的 reserved");
            Check.Equal(0U, BlobReader.Rule(blob, index).Reserved2, $"规则 {index} 的 reserved2");
        }

        int selectorCount = (int)(BlobReader.Header(blob).SelectorPoolSize / RuleBlobLayout.ProcessSelectorSize);
        for (int index = 0; index < selectorCount; index++)
        {
            RuleBlobProcessSelector selector = BlobReader.Selector(blob, index);
            Check.Equal((byte)0, selector.Reserved0, $"selector {index} 的 reserved[0]");
            Check.Equal((byte)0, selector.Reserved1, $"selector {index} 的 reserved[1]");
        }
    }

    private static void PortMerge()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[]
            {
                new PolicyRule
                {
                    RemotePorts = new[]
                    {
                        new PortRange(443, 443),
                        new PortRange(80, 90),
                        new PortRange(85, 100),
                        new PortRange(1000, 1000),
                        new PortRange(1001, 1001),
                    },
                },
            },
            RuleAction.Permit));

        RuleBlobRule rule = BlobReader.Rule(blob, 0);
        Check.Equal(3U, rule.PortCount, "排序合并后的区间数");
        (ushort Start, ushort End)[] expected = { (80, 100), (443, 443), (1000, 1001) };
        for (int index = 0; index < expected.Length; index++)
        {
            RuleBlobPortRange range = BlobReader.PortRange(blob, (int)rule.PortFirst + index);
            Check.Equal(expected[index].Start, range.Start, $"区间 {index} 起点");
            Check.Equal(expected[index].End, range.End, $"区间 {index} 终点");
        }
    }

    private static void PortEndpoints()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule { RemotePorts = new[] { new PortRange(2, 65534) } } },
            RuleAction.Permit));
        RuleBlobRule rule = BlobReader.Rule(blob, 0);
        RuleBlobPortRange range = BlobReader.PortRange(blob, 0);

        Check.Equal(1U, rule.PortCount, "端口区间数");
        Check.Equal((ushort)2, range.Start, "端口起点");
        Check.Equal((ushort)65534, range.End, "端口终点");
    }

    private static void PortZeroRejected()
    {
        PolicyTable single = new(
            new[] { new PolicyRule { RemotePorts = new[] { PortRange.Single(0) } } },
            RuleAction.Permit);
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(single), "端口 0", "单端口 0 必须拒绝");

        PolicyTable range = new(
            new[] { new PolicyRule { RemotePorts = new[] { new PortRange(0, 80) } } },
            RuleAction.Permit);
        Check.ThrowsContaining<PolicyCompileException>(
            () => TestAppId.Compile(range), "端口 0", "以 0 开头的端口区间必须拒绝");
    }

    private static void PortAnyAccepted()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule { RemotePorts = new[] { PortRange.Any } } },
            RuleAction.Permit));
        RuleBlobRule rule = BlobReader.Rule(blob, 0);
        RuleBlobPortRange range = BlobReader.PortRange(blob, 0);

        Check.Equal(1U, rule.PortCount, "Any 写一条区间记录");
        Check.Equal((ushort)0, range.Start, "Any 起点 0");
        Check.Equal(ushort.MaxValue, range.End, "Any 终点 65535");
    }

    private static void ProcessPoolTail()
    {
        byte[] blob = TestAppId.Compile(SampleTable());
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.True(header.ProcessPoolSize > 0, "样本表应有进程串");
        Check.Equal(0, blob[(int)header.ProcessPoolOffset + (int)header.ProcessPoolSize - 1], "池的最后 1 字节是 NUL");
    }

    private static void ProcessPoolUtf8Bytes()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(Svchost) } },
                new PolicyRule { Processes = new[] { ProcessSelector.Parse(TestAppId.UnicodeDos) } },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);
        int firstSize = Encoding.UTF8.GetByteCount(TestAppId.SvchostDevice) + 1;
        int expectedSize = firstSize + Encoding.UTF8.GetByteCount(TestAppId.UnicodeDevice) + 1;

        Check.Equal(expectedSize, (int)header.ProcessPoolSize, "进程池按 UTF-8 字节计数");
        Check.Equal((uint)firstSize, BlobReader.Selector(blob, 1).StringOffset, "第二条串的偏移按 UTF-8 字节算");
        Check.Equal(TestAppId.UnicodeDevice, BlobReader.ProcessString(blob, (uint)firstSize), "中文设备路径原样落盘");
        Check.True(
            Encoding.UTF8.GetByteCount(TestAppId.UnicodeDevice) > TestAppId.UnicodeDevice.Length,
            "中文串的字节数必须大于 UTF-16 字符数，否则这条用例测不出差别");
    }

    private static void OneRuleOneBlobRule()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule
                {
                    Processes = new[] { ProcessSelector.Parse(Svchost), ProcessSelector.Parse(App) },
                    RemoteNetworks = new[]
                    {
                        RemoteNetwork.Parse("10.0.0.0/8"),
                        RemoteNetwork.Parse("2001:db8::/32"),
                    },
                    RemotePorts = new[] { PortRange.Single(80), PortRange.Single(443), new PortRange(8000, 8100) },
                    Protocols = new[] { RuleProtocol.Tcp, RuleProtocol.Udp },
                    Families = new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 },
                    Action = RuleAction.Redirect,
                },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobRule rule = BlobReader.Rule(blob, 0);

        Check.Equal(1, blob.Length > 0 ? (int)BlobReader.Header(blob).RuleCount : 0, "一条配置规则 = 一条 blob 规则");
        Check.Equal(2U, rule.ProcessCount, "进程数组");
        Check.Equal(2U, rule.NetworkCount, "网段数组");
        Check.Equal(3U, rule.PortCount, "端口数组");
        Check.Equal((byte)3, rule.ProtocolMask, "协议数组");
        Check.Equal((byte)3, rule.FamilyMask, "族数组");
    }

    private static PolicyTable SampleTable() => new(
        new[]
        {
            new PolicyRule
            {
                Processes = new[] { ProcessSelector.Parse(Svchost) },
                RemoteNetworks = new[] { RemoteNetwork.Parse("10.1.2.3/32") },
                RemotePorts = new[] { PortRange.Single(443) },
                Protocols = new[] { RuleProtocol.Tcp },
                Families = new[] { RuleAddressFamily.Ipv4 },
                Action = RuleAction.Redirect,
            },
            new PolicyRule
            {
                Processes = new[] { ProcessSelector.Parse(App) },
                Families = new[] { RuleAddressFamily.Ipv6 },
                Action = RuleAction.Block,
            },
        },
        RuleAction.Permit);
}
