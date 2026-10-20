using System.Text;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class CompilerTests
{
    private const string Svchost = @"c:\windows\system32\svchost.exe";
    private const string App = @"c:\program files\app\app.exe";

    internal static void Register()
    {
        TestRunner.Add("compiler/空规则表只产出 40 字节头部", EmptyTable);
        TestRunner.Add("compiler/各段起点 4 字节对齐且不重叠", Segments);
        TestRunner.Add("compiler/规则顺序与字段原样写入", RuleFields);
        TestRunner.Add("compiler/进程串小写、去重、NUL 结尾", ProcessPool);
        TestRunner.Add("compiler/进程索引是池内字节偏移", ProcessIndexes);
        TestRunner.Add("compiler/网段主机位清零并去重", NetworkPool);
        TestRunner.Add("compiler/IPv4 地址只占前 4 字节", NetworkIpv4Padding);
        TestRunner.Add("compiler/网段索引是 20 的倍数", NetworkIndexes);
        TestRunner.Add("compiler/任意选择器写成 0xFFFFFFFF", AnySelectors);
        TestRunner.Add("compiler/兜底动作写入头部", FallbackAction);
        TestRunner.Add("compiler/规则保留位为 0", ReservedIsZero);
        TestRunner.Add("compiler/端口区间端点原样写入", PortEndpoints);
        TestRunner.Add("compiler/进程串池末尾是 NUL", ProcessPoolTail);
        TestRunner.Add("compiler/进程串池按 UTF-8 字节计数", ProcessPoolUtf8Bytes);
        TestRunner.Add("compiler/进程路径归一化可替换", ProcessNormalizerIsReplaceable);
        TestRunner.Add("compiler/解析不到的 DOS 路径拒绝编译", UnresolvableProcessPathIsRejected);
        TestRunner.Add("compiler/真实解析器只能在 Windows 上用", WindowsResolverRequiresWindows);
    }

    private static void EmptyTable()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(Array.Empty<PolicyRule>(), RuleAction.Block));
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(40, blob.Length, "空表 blob 长度");
        Check.Equal(0x31425257U, header.Magic, "magic");
        Check.Equal((ushort)1, header.Version, "version");
        Check.Equal((ushort)40, header.HeaderSize, "header_size");
        Check.Equal(40U, header.TotalSize, "total_size");
        Check.Equal(0U, header.RuleCount, "rule_count");
        Check.Equal(1U, header.FallbackAction, "fallback_action");
        Check.Equal(40U, header.RulesOffset, "rules_offset");
        Check.Equal(40U, header.ProcessPoolOffset, "process_pool_offset");
        Check.Equal(0U, header.ProcessPoolSize, "process_pool_size");
        Check.Equal(40U, header.NetworkPoolOffset, "network_pool_offset");
        Check.Equal(0U, header.NetworkPoolSize, "network_pool_size");

        Check.Equal(40U, header.NetworkPoolOffset, "空表的零长度段落在 total_size 端点");
    }

    private static void Segments()
    {
        byte[] blob = TestAppId.Compile(SampleTable());
        RuleBlobHeader header = BlobReader.Header(blob);
        int rulesEnd = (int)header.RulesOffset + ((int)header.RuleCount * 16);
        int processEnd = (int)header.ProcessPoolOffset + (int)header.ProcessPoolSize;
        int networkEnd = (int)header.NetworkPoolOffset + (int)header.NetworkPoolSize;

        Check.Equal(blob.Length, (int)header.TotalSize, "total_size 等于实际长度");
        Check.Equal(0, (int)header.RulesOffset % 4, "rules_offset 对齐");
        Check.Equal(0, (int)header.ProcessPoolOffset % 4, "process_pool_offset 对齐");
        Check.Equal(0, (int)header.NetworkPoolOffset % 4, "network_pool_offset 对齐");
        Check.Equal(40, (int)header.RulesOffset, "rules_offset 紧接头部");
        Check.True(rulesEnd <= (int)header.ProcessPoolOffset, "规则数组不越过进程池");
        Check.True(processEnd <= (int)header.NetworkPoolOffset, "进程池不越过网段池");
        Check.Equal(blob.Length, networkEnd, "网段池结束于 blob 末尾");
        Check.True(
            (int)header.NetworkPoolOffset - processEnd < 4,
            "进程池与网段池之间只有对齐填充");
    }

    private static void RuleFields()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule
                {
                    Name = "first",
                    Process = ProcessSelector.Parse(Svchost),
                    RemoteNetwork = RemoteNetwork.Parse("10.1.2.3/32"),
                    RemotePorts = PortRange.Single(443),
                    Protocol = RuleProtocol.Tcp,
                    Family = RuleAddressFamily.Ipv4,
                    Action = RuleAction.Redirect,
                },
                new PolicyRule
                {
                    Name = "second",
                    RemotePorts = new PortRange(1000, 2000),
                    Action = RuleAction.Block,
                },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobRule first = BlobReader.Rule(blob, 0);
        RuleBlobRule second = BlobReader.Rule(blob, 1);

        Check.Equal(443, (int)first.RemotePortStart, "规则 0 端口起点");
        Check.Equal(443, (int)first.RemotePortEnd, "规则 0 端口终点");
        Check.Equal((byte)6, first.Protocol, "规则 0 协议");
        Check.Equal((byte)4, first.Family, "规则 0 地址族");
        Check.Equal((byte)2, first.Action, "规则 0 动作");
        Check.Equal(0U, first.ProcessIndex, "规则 0 的 process_index 指向池首");
        Check.Equal(0U, first.NetworkIndex, "规则 0 的 network_index 指向池首");
        Check.Equal(0xFFFFFFFFU, second.NetworkIndex, "规则 1 的 network_index 是任意");
        Check.Equal(0xFFFFFFFFU, second.ProcessIndex, "规则 1 的 process_index 是任意");
        Check.Equal(1000, (int)second.RemotePortStart, "规则 1 端口起点");
        Check.Equal(2000, (int)second.RemotePortEnd, "规则 1 端口终点");
        Check.Equal((byte)0, second.Protocol, "规则 1 协议默认任意");
        Check.Equal((byte)1, second.Action, "规则 1 动作");
    }

    private static void ProcessPool()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Process = ProcessSelector.Parse(@"C:\Windows\SYSTEM32\SVCHOST.EXE"), Action = RuleAction.Block },
                new PolicyRule { Process = ProcessSelector.Parse(Svchost), Action = RuleAction.Redirect },
                new PolicyRule { Process = ProcessSelector.Parse(App), Action = RuleAction.Permit },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);

        string expectedPool = TestAppId.SvchostDevice + "\0" + TestAppId.AppDevice + "\0";
        Check.Equal(Encoding.UTF8.GetByteCount(expectedPool), (int)header.ProcessPoolSize, "process_pool_size 是精确串字节数");
        Check.Equal(0, blob[(int)header.ProcessPoolOffset + (int)header.ProcessPoolSize - 1], "池尾是 NUL");

        Check.Equal(0U, BlobReader.Rule(blob, 0).ProcessIndex, "规则 0 指向池首");
        Check.Equal(0U, BlobReader.Rule(blob, 1).ProcessIndex, "大小写不同的同一路径复用同一条串");
        Check.Equal((uint)Encoding.UTF8.GetByteCount(TestAppId.SvchostDevice) + 1U, BlobReader.Rule(blob, 2).ProcessIndex, "规则 2 指向 app 串首");
        Check.Equal(TestAppId.SvchostDevice, BlobReader.ProcessString(blob, 0), "池内是设备路径且已小写");
    }

    private static void ProcessIndexes()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Process = ProcessSelector.Parse(Svchost), Action = RuleAction.Block },
                new PolicyRule { Process = ProcessSelector.Parse(App), Action = RuleAction.Block },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        uint second = BlobReader.Rule(blob, 1).ProcessIndex;

        Check.Equal((uint)Encoding.UTF8.GetByteCount(TestAppId.SvchostDevice) + 1U, second, "第二条串的索引是池内字节偏移");
        Check.Equal(TestAppId.AppDevice, BlobReader.ProcessString(blob, second), "索引指向第二条串的串首");
    }

    private static void NetworkPool()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { RemoteNetwork = RemoteNetwork.Parse("10.1.9.77/24"), Action = RuleAction.Block },
                new PolicyRule { RemoteNetwork = RemoteNetwork.Parse("10.1.9.1/24"), Action = RuleAction.Redirect },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);

        Check.Equal(20U, header.NetworkPoolSize, "两条同网段记录去重后只剩 20 字节");
        Check.Equal(0U, BlobReader.Rule(blob, 0).NetworkIndex, "规则 0 指向池首");
        Check.Equal(0U, BlobReader.Rule(blob, 1).NetworkIndex, "规则 1 复用同一条记录");

        RuleBlobNetworkPrefix prefix = BlobReader.NetworkPrefix(blob, 0);
        Check.Equal((byte)24, prefix.PrefixLength, "prefix_length");
        Check.Equal((byte)4, prefix.Family, "family");
        Check.Equal((byte)0, prefix.Reserved0, "reserved[0]");
        Check.Equal((byte)0, prefix.Reserved1, "reserved[1]");
        Check.BytesEqual(new byte[] { 10, 1, 9, 0 }, BlobReader.NetworkAddress(prefix).AsSpan(0, 4), "主机位已清零");
    }

    private static void NetworkIpv4Padding()
    {
        PolicyTable table = new(
            new[] { new PolicyRule { RemoteNetwork = RemoteNetwork.Parse("192.168.1.0/24") } },
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
                new PolicyRule { RemoteNetwork = RemoteNetwork.Parse("10.0.0.0/8") },
                new PolicyRule { RemoteNetwork = RemoteNetwork.Parse("192.168.0.0/16") },
                new PolicyRule { RemoteNetwork = RemoteNetwork.Parse("2001:db8::/32"), Family = RuleAddressFamily.Ipv6 },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);

        Check.Equal(0U, BlobReader.Rule(blob, 0).NetworkIndex, "规则 0");
        Check.Equal(20U, BlobReader.Rule(blob, 1).NetworkIndex, "规则 1");
        Check.Equal(40U, BlobReader.Rule(blob, 2).NetworkIndex, "规则 2");
        Check.Equal(0, (int)BlobReader.Rule(blob, 2).NetworkIndex % 20, "网段索引是 20 的倍数");
    }

    private static void AnySelectors()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule() },
            RuleAction.Permit));
        RuleBlobRule rule = BlobReader.Rule(blob, 0);

        Check.Equal(0xFFFFFFFFU, rule.ProcessIndex, "process_index 任意");
        Check.Equal(0xFFFFFFFFU, rule.NetworkIndex, "network_index 任意");
        Check.Equal(0, (int)rule.RemotePortStart, "端口起点 0");
        Check.Equal(0xFFFF, (int)rule.RemotePortEnd, "端口终点 65535");
    }

    private static void FallbackAction()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule() },
            RuleAction.Redirect));

        Check.Equal(2U, BlobReader.Header(blob).FallbackAction, "fallback_action 写进头部");
    }

    private static void ReservedIsZero()
    {
        byte[] blob = TestAppId.Compile(SampleTable());

        for (int index = 0; index < (int)BlobReader.Header(blob).RuleCount; index++)
        {
            Check.Equal((byte)0, BlobReader.Rule(blob, index).Reserved, $"规则 {index} 的 reserved");
        }
    }

    private static void PortEndpoints()
    {
        byte[] blob = TestAppId.Compile(new PolicyTable(
            new[] { new PolicyRule { RemotePorts = new PortRange(2, 65534) } },
            RuleAction.Permit));
        RuleBlobRule rule = BlobReader.Rule(blob, 0);

        Check.Equal(2, (int)rule.RemotePortStart, "端口起点");
        Check.Equal(65534, (int)rule.RemotePortEnd, "端口终点");
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
                new PolicyRule { Process = ProcessSelector.Parse(Svchost), Action = RuleAction.Block },
                new PolicyRule { Process = ProcessSelector.Parse(TestAppId.UnicodeDos), Action = RuleAction.Redirect },
            },
            RuleAction.Permit);

        byte[] blob = TestAppId.Compile(table);
        RuleBlobHeader header = BlobReader.Header(blob);
        int firstSize = Encoding.UTF8.GetByteCount(TestAppId.SvchostDevice) + 1;
        int expectedSize = firstSize + Encoding.UTF8.GetByteCount(TestAppId.UnicodeDevice) + 1;

        Check.Equal(expectedSize, (int)header.ProcessPoolSize, "进程池按 UTF-8 字节计数");
        Check.Equal((uint)firstSize, BlobReader.Rule(blob, 1).ProcessIndex, "第二条串的索引按 UTF-8 字节算");
        Check.Equal(TestAppId.UnicodeDevice, BlobReader.ProcessString(blob, (uint)firstSize), "中文设备路径原样落盘");
        Check.True(
            Encoding.UTF8.GetByteCount(TestAppId.UnicodeDevice) > TestAppId.UnicodeDevice.Length,
            "中文串的字节数必须大于 UTF-16 字符数，否则这条用例测不出差别");
    }


    private static void ProcessNormalizerIsReplaceable()
    {
        PolicyTable table = new(
            new[]
            {
                new PolicyRule { Process = ProcessSelector.Parse(Svchost), Action = RuleAction.Block },
                new PolicyRule { Process = ProcessSelector.Parse(@"C:\Windows\SYSTEM32\SVCHOST.EXE"), Action = RuleAction.Redirect },
            },
            RuleAction.Permit);

        // 注入一个把前缀换成 \??\ 的解析器：路径表示只由 normalizer 决定，编译器不写死。
        FwpmAppIdNormalizer normalizer = new(path => @"\??\" + path);
        byte[] blob = RuleBlobCompiler.Compile(table, normalizer);
        RuleBlobHeader header = BlobReader.Header(blob);
        string expected = @"\??\" + Svchost;

        Check.Equal(Encoding.UTF8.GetByteCount(expected) + 1, (int)header.ProcessPoolSize, "自定义解析的池长度");
        Check.Equal(0U, BlobReader.Rule(blob, 0).ProcessIndex, "第一条指向池首");
        Check.Equal(0U, BlobReader.Rule(blob, 1).ProcessIndex, "大小写不同的同一路径仍去重");
        Check.Equal(expected, BlobReader.ProcessString(blob, 0), "池内是注入解析器的输出");
    }

    private static void UnresolvableProcessPathIsRejected()
    {
        PolicyTable table = new(
            new[] { new PolicyRule { Process = ProcessSelector.Parse(@"c:\nope\missing.exe") } },
            RuleAction.Permit);

        Check.Throws<PolicyCompileException>(
            () => TestAppId.Compile(table), "解析器映射不到时拒绝编译");
    }

    private static void WindowsResolverRequiresWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            // 真实解析只能在 Windows 上验；这条用例的价值在非 Windows 分支。
            return;
        }

        Check.Throws<PlatformNotSupportedException>(
            () => FwpmAppIdNormalizer.ForWindows(), "非 Windows 上不允许构造真实解析器");
    }


    private static PolicyTable SampleTable() => new(
        new[]
        {
            new PolicyRule
            {
                Process = ProcessSelector.Parse(Svchost),
                RemoteNetwork = RemoteNetwork.Parse("10.1.2.3/32"),
                RemotePorts = PortRange.Single(443),
                Action = RuleAction.Redirect,
            },
            new PolicyRule
            {
                Process = ProcessSelector.Parse(App),
                Family = RuleAddressFamily.Ipv6,
                Action = RuleAction.Block,
            },
        },
        RuleAction.Permit);
}
