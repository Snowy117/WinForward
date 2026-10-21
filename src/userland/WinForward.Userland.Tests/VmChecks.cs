using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WinForward.Userland.Device;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

/// <summary>
/// 需要真实驱动的检查：ABI 握手、结构尺寸（用真实 IOCTL 的缓冲区长度判据）、
/// 进程路径 511/512 边界，以及 C# 顺序冲突检查与驱动 <c>rule_order_valid</c> 的差分矩阵。
/// </summary>
/// <remarks>
/// 每一条都以「同一个输入，产品与驱动必须给出同一个结论」为准。产品拒绝而驱动接受、
/// 或反过来，都算失败；产品接受时驱动必须换表成功（generation +1），拒绝时 generation 不能变。
/// </remarks>
internal static class VmChecks
{
    private static int _failures;
    private static int _checks;

    internal static Task<int> RunAsync(string[] args)
    {
        _ = args;

        WfpDeviceClient device;
        try
        {
            device = WfpDeviceClient.Open();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[vm-check] FAIL 打不开驱动设备：{exception.GetType().Name}: {exception.Message}");
            Console.WriteLine("[vm-check] verdict=FAIL reason=device-unavailable");
            return Task.FromResult(2);
        }

        using (device)
        {
            try
            {
                CheckAbiHandshake(device);
                CheckStructureSizes(device);
                CheckProcessPathBoundary(device);
                CheckConfigRoundTrip(device);
                CheckRuleOrderDifferential(device);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                Fail("未捕获的驱动错误", $"{exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                Restore(device);
            }
        }

        Console.WriteLine($"[vm-check] checks={_checks} failures={_failures} verdict={(_failures == 0 ? "PASS" : "FAIL")}");
        return Task.FromResult(_failures == 0 ? 0 : 1);
    }

    private static void CheckAbiHandshake(WfpDeviceClient device)
    {
        ProbeStats stats = device.GetStats();
        Assert("ABI 握手", stats.AbiVersion == ProbeIoctl.AbiVersion,
               $"stats.abi_version={stats.AbiVersion} product={ProbeIoctl.AbiVersion}");

        ProbeConfig config = device.GetConfig();
        Assert("配置 ABI 版本", config.AbiVersion == ProbeIoctl.AbiVersion,
               $"config.abi_version={config.AbiVersion}");

        ProbeRuleInfo info = device.GetRuleInfo();
        Assert("规则信息 ABI 版本", info.AbiVersion == ProbeIoctl.AbiVersion,
               $"rule_info.abi_version={info.AbiVersion}");
    }

    private static void CheckStructureSizes(WfpDeviceClient device)
    {
        // 驱动的入口判据就是缓冲区长度：短 4 字节的 probe_config 必须被 STATUS_INVALID_PARAMETER 拒绝，
        // 完整 64 字节必须被接受。这条比 static_assert 更硬——它是运行中的内核在判。
        ExpectError("probe_config 60 字节被驱动拒绝", 87,
                    () => device.RawIoctl(ProbeIoctl.SetConfigCode, new byte[DeviceLayout.ProbeConfigSize - 4], 0));

        byte[] configBytes = new byte[DeviceLayout.ProbeConfigSize];
        ProbeConfig disabled = ProbeConfig.Disabled();
        MemoryMarshal.Write(configBytes, in disabled);
        byte[] accepted = device.RawIoctl(ProbeIoctl.SetConfigCode, configBytes, 0);
        Assert("probe_config 64 字节被驱动接受", accepted.Length == 0, $"返回 {accepted.Length} 字节");

        byte[] stats = device.RawIoctl(ProbeIoctl.GetStatsCode, Array.Empty<byte>(), DeviceLayout.ProbeStatsSize);
        Assert("probe_stats 输出 264 字节", stats.Length == DeviceLayout.ProbeStatsSize, $"实际 {stats.Length} 字节");

        ExpectError("probe_stats 输出短 8 字节被驱动拒绝", 122,
                    () => device.RawIoctl(ProbeIoctl.GetStatsCode, Array.Empty<byte>(), DeviceLayout.ProbeStatsSize - 8));

        byte[] ruleInfo = device.RawIoctl(ProbeIoctl.GetRuleInfoCode, Array.Empty<byte>(), DeviceLayout.ProbeRuleInfoSize);
        Assert("probe_rule_info 输出 32 字节", ruleInfo.Length == DeviceLayout.ProbeRuleInfoSize,
               $"实际 {ruleInfo.Length} 字节");

        byte[] events = device.RawIoctl(ProbeIoctl.GetEventsCode, Array.Empty<byte>(), DeviceLayout.ProbeEventSize);
        Assert("probe_event 每个 64 字节", events.Length % DeviceLayout.ProbeEventSize == 0,
               $"返回 {events.Length} 字节");
    }

    private static void CheckProcessPathBoundary(WfpDeviceClient device)
    {
        Assert("PolicyLimits 与内核共用 511 上限", PolicyLimits.MaxProcessPathBytes == 511,
               $"PolicyLimits.MaxProcessPathBytes={PolicyLimits.MaxProcessPathBytes}");

        string ascii511 = new('a', 511);
        (uint length511, string text511) = device.MeasureProcessPath(Encoding.Unicode.GetBytes(ascii511));
        Assert("UTF-8 511 字节通过", length511 == 511, $"内核返回 {length511}");
        Assert("511 字节内容一致", text511 == ascii511, $"文本长度 {text511.Length}");

        (uint length512, _) = device.MeasureProcessPath(Encoding.Unicode.GetBytes(new string('a', 512)));
        Assert("UTF-8 512 字节整条不匹配", length512 == 0, $"内核返回 {length512}");

        const string Chinese = @"\device\harddiskvolume1\users\张三\工具.exe";
        (uint chineseLength, string chineseText) = device.MeasureProcessPath(Encoding.Unicode.GetBytes(Chinese));
        Assert("中文路径按 UTF-8 返回", chineseLength == (uint)Encoding.UTF8.GetByteCount(Chinese) && chineseText == Chinese,
               $"长度 {chineseLength} 文本 {chineseText}");
    }

    private static void CheckConfigRoundTrip(WfpDeviceClient device)
    {
        uint pid = (uint)Environment.ProcessId;
        device.SetConfig(ProbeConfig.EnabledFor(pid, 1, new[] { pid }));
        ProbeConfig config = device.GetConfig();
        Assert("配置往返", config.Enabled == 1 && config.ListenerProcessId == pid && config.ListenerPort == 1 &&
                           config.SelfProcessCount == 1 && config.SelfProcessIds[0] == pid,
               $"enabled={config.Enabled} pid={config.ListenerProcessId} port={config.ListenerPort} " +
               $"self={config.SelfProcessCount}/{config.SelfProcessIds[0]}");
        device.SetConfig(ProbeConfig.Disabled());
    }

    private static void CheckRuleOrderDifferential(WfpDeviceClient device)
    {
        ApplyBlob(device, Benign());
        ulong rejectedBefore = device.GetStats().RejectedBlobs;

        foreach (DifferentialCase testCase in Cases())
        {
            string productVerdict;
            bool productAccepted;
            string message = string.Empty;
            try
            {
                _ = RuleBlobCompiler.Compile(testCase.Table, TestAppId.Normalizer);
                productAccepted = true;
                productVerdict = "accept";
            }
            catch (PolicyCompileException exception)
            {
                productAccepted = false;
                productVerdict = "reject";
                message = exception.Message;
            }

            byte[] blob = RuleBlobCompiler.CompileUnchecked(testCase.Table, TestAppId.Normalizer);
            uint generationBefore = device.GetRuleInfo().Generation;
            bool driverAccepted;
            int error = 0;
            try
            {
                device.SetRuleBlob(blob);
                driverAccepted = true;
            }
            catch (Win32Exception exception)
            {
                driverAccepted = false;
                error = exception.NativeErrorCode;
            }

            uint generationAfter = device.GetRuleInfo().Generation;
            string driverVerdict = driverAccepted ? "accept" : "reject";

            Assert($"rule-order {testCase.Name}：产品与驱动结论一致",
                   productAccepted == driverAccepted && productAccepted == testCase.ExpectedAccepted,
                   $"expected={(testCase.ExpectedAccepted ? "accept" : "reject")} product={productVerdict} " +
                   $"driver={driverVerdict} win32={error}");

            if (driverAccepted)
            {
                Assert($"rule-order {testCase.Name}：换表 generation +1", generationAfter == generationBefore + 1,
                       $"{generationBefore} -> {generationAfter}");
            }
            else
            {
                Assert($"rule-order {testCase.Name}：拒绝时 generation 不变", generationAfter == generationBefore,
                       $"{generationBefore} -> {generationAfter}");
                Assert($"rule-order {testCase.Name}：驱动用 STATUS_INVALID_PARAMETER 拒绝", error == 87,
                       $"Win32={error}");
            }

            if (!productAccepted)
            {
                bool messageOk = testCase.MessageFragment is { } fragment
                                     ? message.Contains(fragment, StringComparison.Ordinal)
                                     : message.Contains($"#{testCase.Earlier}", StringComparison.Ordinal) &&
                                       message.Contains($"#{testCase.Later}", StringComparison.Ordinal);
                Assert($"rule-order {testCase.Name}：报错可诊断", messageOk, message);

            }

            ApplyBlob(device, Benign());
        }

        ulong rejectedAfter = device.GetStats().RejectedBlobs;
        long rejectedDelta = (long)(rejectedAfter - rejectedBefore);
        int expectedRejects = Cases().Count(testCase => !testCase.ExpectedAccepted);
        Assert("rejected_blobs 精确增加", rejectedDelta == expectedRejects,
               $"delta={rejectedDelta} expect={expectedRejects}（每条被产品拒绝的用例让驱动再拒绝一次）");

    }

    private static void ApplyBlob(WfpDeviceClient device, PolicyTable table)
    {
        byte[] blob = RuleBlobCompiler.CompileUnchecked(table, TestAppId.Normalizer);
        device.SetRuleBlob(blob);
    }

    private static PolicyTable Benign() => new(Array.Empty<PolicyRule>(), RuleAction.Permit);

    private static void Restore(WfpDeviceClient device)
    {
        try
        {
            device.SetConfig(ProbeConfig.Disabled());
            ApplyBlob(device, Benign());
            Console.WriteLine("[vm-check] 已恢复：enabled=0、空规则表、fallback=permit");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Fail("恢复驱动状态", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ExpectError(string what, int expectedWin32, Action action)
    {
        try
        {
            action();
            Fail(what, "没有抛异常");
        }
        catch (Win32Exception exception)
        {
            Assert(what, exception.NativeErrorCode == expectedWin32, $"Win32={exception.NativeErrorCode}");
        }
    }

    private static void Assert(string what, bool condition, string detail)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"[vm-check] OK   {what}");
            return;
        }

        Fail(what, detail);
    }

    private static void Fail(string what, string detail)
    {
        _failures++;
        Console.WriteLine($"[vm-check] FAIL {what}：{detail}");
    }

    private sealed record DifferentialCase(string Name,
                                          PolicyTable Table,
                                          bool ExpectedAccepted,
                                          int Earlier = -1,
                                          int Later = -1,
                                          string? MessageFragment = null);

    private static IReadOnlyList<DifferentialCase> Cases() => new List<DifferentialCase>
    {
        new("只有 block 规则", Table(Rule(RuleAction.Block, ports: Single(80))), true),
        new("permit 全重叠后跟 block", Table(Rule(RuleAction.Permit, ports: Single(80)),
                                           Rule(RuleAction.Block, ports: Single(80))), false, 0, 1),
        new("端口区间相交", Table(Rule(RuleAction.Permit, ports: Ports(80, 90)),
                                 Rule(RuleAction.Block, ports: Ports(85, 95))), false, 0, 1),
        new("端口不相交", Table(Rule(RuleAction.Permit, ports: Single(80)),
                               Rule(RuleAction.Block, ports: Single(81))), true),
        new("地址族不相交", Table(Rule(RuleAction.Permit, family: RuleAddressFamily.Ipv4),
                                 Rule(RuleAction.Block, family: RuleAddressFamily.Ipv6)), true),
        new("地址族任一侧任意", Table(Rule(RuleAction.Permit),
                                     Rule(RuleAction.Block, family: RuleAddressFamily.Ipv6)), false, 0, 1),
        new("网段不相交", Table(Rule(RuleAction.Permit, network: Network("10.0.0.0/8")),
                               Rule(RuleAction.Block, network: Network("192.168.0.0/16"))), true),
        new("网段嵌套", Table(Rule(RuleAction.Permit, network: Network("10.0.0.0/8")),
                             Rule(RuleAction.Block, network: Network("10.1.0.0/16"))), false, 0, 1),
        new("网段任一侧任意", Table(Rule(RuleAction.Permit, network: Network("10.0.0.0/8")),
                                   Rule(RuleAction.Block)), false, 0, 1),
        new("进程选择器不同", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                   Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.OtherDos))), true),
        new("进程选择器不同（含非 ASCII 路径）", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                   Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.UnicodeDos))), true),
        new("进程选择器同一条路径", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                          Rule(RuleAction.Block,
                                               process: ProcessSelector.Parse(@"C:\PROGRAM FILES\APP\APP.EXE"))),
            false, 0, 1),
        new("进程任一侧任意", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                   Rule(RuleAction.Block)), false, 0, 1),
        new("两条 block 规则", Table(Rule(RuleAction.Block, ports: Single(80)),
                                    Rule(RuleAction.Block, ports: Single(80))), true),
        new("block 在前 permit 在后", Table(Rule(RuleAction.Block, ports: Single(80)),
                                           Rule(RuleAction.Permit, ports: Single(80))), true),
        new("redirect 遮蔽 block", Table(Rule(RuleAction.Redirect, ports: Single(80)),
                                        Rule(RuleAction.Block, ports: Single(80))), false, 0, 1),
        new("IPv6 网段推出地址族", Table(Rule(RuleAction.Permit, network: Network("2001:db8::/32")),
                                        Rule(RuleAction.Block, network: Network("2001:db8:1::/48"))), false, 0, 1),
        new("IPv6 与 IPv4 网段不相交", Table(Rule(RuleAction.Permit, network: Network("2001:db8::/32")),
                                             Rule(RuleAction.Block, network: Network("192.168.0.0/16"))), true),
        new("兜底动作为 block（无规则）", new PolicyTable(Array.Empty<PolicyRule>(), RuleAction.Block), true),
        new("兜底 block 与 permit 规则", new PolicyTable(new[] { Rule(RuleAction.Permit, ports: Single(80)) }, RuleAction.Block),
            false, 0, -1, "fallbackAction"),
        new("兜底 block 与全部 block 规则",
            new PolicyTable(new[] { Rule(RuleAction.Block, ports: Single(80)) }, RuleAction.Block), true),

    };

    private static PolicyTable Table(params PolicyRule[] rules) => new(rules, RuleAction.Permit);

    private static PolicyRule Rule(RuleAction action,
                                   RuleAddressFamily family = RuleAddressFamily.Any,
                                   PortRange? ports = null,
                                   RemoteNetwork? network = null,
                                   ProcessSelector? process = null) =>
        new()
        {
            Action = action,
            Family = family,
            RemotePorts = ports ?? PortRange.Any,
            RemoteNetwork = network,
            Process = process,
        };

    private static PortRange Single(ushort port) => PortRange.Single(port);

    private static PortRange Ports(ushort begin, ushort end) => new(begin, end);

    private static RemoteNetwork Network(string cidr) => RemoteNetwork.Parse(cidr);
}
