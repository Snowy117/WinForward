using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using WinForward.Userland.Device;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

/// <summary>
/// 需要真实驱动的检查：ABI 握手、结构尺寸（用真实 IOCTL 的缓冲区长度判据）、
/// 进程路径 511/512 边界，以及 C# 规则表与驱动的换表差分矩阵。
/// </summary>
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
                CheckForwardedLegacySizes(device);

                CheckProcessPathBoundary(device);
                CheckProcessPathResolution(device);
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
    private static void CheckForwardedLegacySizes(WfpDeviceClient device)
    {
        ProbeForwardedRedirectQuery query = default;
        query.ListenerPort = 40000;
        query.Family = 4;
        query.ClientPort = 43210;
        query.Protocol = 6;
        byte[] full = MemoryMarshal.AsBytes(new ReadOnlySpan<ProbeForwardedRedirectQuery>(in query)).ToArray();
        full[4] = 127;
        full[5] = 0;
        full[6] = 0;
        full[7] = 1;

        ExpectError("forwarded query 20 字节被拒绝", 87,
                    () => device.RawIoctl(ProbeIoctl.QueryForwardedRedirectCode, full[..20], 64));
        ExpectError("forwarded query 24 字节（legacy）被接受，无表项返回 NOT_FOUND", 1168,
                    () => device.RawIoctl(ProbeIoctl.QueryForwardedRedirectCode, full[..24], 64));
        ExpectError("forwarded query 输出 56 字节被拒绝", 87,
                    () => device.RawIoctl(ProbeIoctl.QueryForwardedRedirectCode, full[..24], 56));
        ExpectError("forwarded query 40 字节（v7）被接受，无表项返回 NOT_FOUND", 1168,
                    () => device.RawIoctl(ProbeIoctl.QueryForwardedRedirectCode, full, 88));

        byte[] stats400 = device.RawIoctl(ProbeIoctl.GetForwardedStatsCode, Array.Empty<byte>(), 400);
        Assert("forwarded stats 400 字节（v6）兼容", stats400.Length == 400, $"实际 {stats400.Length} 字节");
        byte[] stats448 = device.RawIoctl(ProbeIoctl.GetForwardedStatsCode, Array.Empty<byte>(), 448);
        Assert("forwarded stats 448 字节（v7）兼容", stats448.Length == 448, $"实际 {stats448.Length} 字节");
        byte[] stats456 = device.RawIoctl(ProbeIoctl.GetForwardedStatsCode, Array.Empty<byte>(),
                                          DeviceLayout.ProbeForwardedStatsSize);
        Assert("forwarded stats 456 字节（v7+）", stats456.Length == DeviceLayout.ProbeForwardedStatsSize,
               $"实际 {stats456.Length} 字节");
        ExpectError("forwarded stats 392 字节被拒绝", 122,
                    () => device.RawIoctl(ProbeIoctl.GetForwardedStatsCode, Array.Empty<byte>(), 392));
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

    private static void CheckProcessPathResolution(WfpDeviceClient device)
    {
        if (!OperatingSystem.IsWindows())
        {
            Fail("路径归一化真实实现在 Windows 上可用", "当前平台不是 Windows");
            return;
        }

        WindowsProcessPathNormalizer normalizer = WindowsProcessPathNormalizer.ForWindows();
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到当前进程路径");
        string directory = Path.GetDirectoryName(executable) ?? throw new InvalidOperationException("拿不到当前进程目录");

        ProcessPathResolution? file = normalizer.Normalize(executable);
        ProcessPathResolution? folder = normalizer.Normalize(directory);
        Assert("当前进程路径解析成设备路径", file is not null, executable);
        Assert("当前进程目录解析成设备路径", folder is not null, directory);
        if (file is null || folder is null)
        {
            return;
        }

        int lastSeparator = file.DevicePath.LastIndexOf('\\');
        Assert("进程设备路径以 \\device\\ 开头",
               file.DevicePath.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase), file.DevicePath);
        Assert("进程设备路径 IsDirectory=false", !file.IsDirectory, $"IsDirectory={file.IsDirectory}");
        Assert("进程设备路径最后一段是 exe 文件名",
               file.DevicePath[(lastSeparator + 1)..]
                   .Equals(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase),
               file.DevicePath);
        Assert("目录设备路径 IsDirectory=true", folder.IsDirectory, $"IsDirectory={folder.IsDirectory}");
        Assert("目录设备路径以 \\device\\ 开头",
               folder.DevicePath.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase), folder.DevicePath);
        Assert("进程设备路径位于目录之下",
               file.DevicePath.StartsWith(folder.DevicePath + "\\", StringComparison.OrdinalIgnoreCase),
               $"{file.DevicePath} via {folder.DevicePath}");

        ApplyBlob(device, Benign());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        Task echoLoop = EchoLoopAsync(listener, stop.Token);
        try
        {
            CheckSelectorHit(device, normalizer, directory, "目录选择器", port);
            CheckSelectorHit(device, normalizer, executable, "精确路径选择器", port);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                echoLoop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static void CheckSelectorHit(WfpDeviceClient device,
                                         WindowsProcessPathNormalizer normalizer,
                                         string selector,
                                         string label,
                                         int port)
    {
        var rule = new PolicyRule
        {
            Action = RuleAction.Permit,
            Processes = new[] { ProcessSelector.Parse(selector) },
            RemotePorts = new[] { PortRange.Single((ushort)port) },
        };
        byte[] blob = RuleBlobCompiler.Compile(new PolicyTable(new[] { rule }, RuleAction.Permit), normalizer);

        device.SetConfig(ProbeConfig.EnabledFor((uint)Environment.ProcessId, 1, Array.Empty<uint>()));
        try
        {
            device.SetRuleBlob(blob);
            ProbeStats before = device.GetStats();
            bool echoed = TryEcho(port, "path", 5000, out string detail);
            ProbeStats after = device.GetStats();
            Assert($"{label}命中原进程连接", echoed, detail);
            Assert($"{label}使 matched_permit +1", after.MatchedPermit == before.MatchedPermit + 1,
                   $"before={before.MatchedPermit} after={after.MatchedPermit}");
            Assert($"{label}不落到 fallback", after.FallbackPermit == before.FallbackPermit,
                   $"before={before.FallbackPermit} after={after.FallbackPermit}");
        }
        finally
        {
            device.SetConfig(ProbeConfig.Disabled());
        }
    }

    private static async Task EchoLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stop);
                using NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[64];
                int read = await stream.ReadAsync(buffer, stop);
                if (read > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), stop);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (stop.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TryEcho(int port, string payload, int timeoutMs, out string detail)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = timeoutMs;
            using NetworkStream stream = client.GetStream();
            byte[] data = Encoding.UTF8.GetBytes(payload);
            stream.Write(data);
            byte[] buffer = new byte[data.Length];
            int read = stream.Read(buffer, 0, buffer.Length);
            detail = $"echo={read}B";
            return read == data.Length;
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
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

        device.SetConfig(ProbeConfig.EnabledFor(
            (uint)Environment.ProcessId, 1, Array.Empty<uint>(), blockSinkPort: 2));
        try
        {
            foreach (DifferentialCase testCase in Cases())
            {
                byte[] blob;
                try
                {
                    blob = RuleBlobCompiler.Compile(testCase.Table, TestAppId.Normalizer);
                }
                catch (PolicyCompileException exception)
                {
                    Assert($"rule-order {testCase.Name}：产品编译成功", false, exception.Message);
                    continue;
                }

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
                Assert($"rule-order {testCase.Name}：产品编译成功、驱动接受", driverAccepted,
                       $"product=accept driver={(driverAccepted ? "accept" : "reject")} win32={error}");
                Assert($"rule-order {testCase.Name}：换表 generation +1", generationAfter == generationBefore + 1,
                       $"{generationBefore} -> {generationAfter}");

                ApplyBlob(device, Benign());
            }
        }
        finally
        {
            device.SetConfig(ProbeConfig.Disabled());
        }

        ulong rejectedAfter = device.GetStats().RejectedBlobs;
        Assert("rejected_blobs 不增", rejectedAfter == rejectedBefore,
               $"before={rejectedBefore} after={rejectedAfter}");
    }

    private static void ApplyBlob(WfpDeviceClient device, PolicyTable table)
    {
        device.SetRuleBlob(RuleBlobCompiler.Compile(table, TestAppId.Normalizer));
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

    private sealed record DifferentialCase(string Name, PolicyTable Table);

    private static IReadOnlyList<DifferentialCase> Cases() => new List<DifferentialCase>
    {
        new("只有 block 规则", Table(Rule(RuleAction.Block, ports: Single(80)))),
        new("permit 全重叠后跟 block", Table(Rule(RuleAction.Permit, ports: Single(80)),
                                           Rule(RuleAction.Block, ports: Single(80)))),
        new("端口区间相交", Table(Rule(RuleAction.Permit, ports: Ports(80, 90)),
                                 Rule(RuleAction.Block, ports: Ports(85, 95)))),
        new("端口不相交", Table(Rule(RuleAction.Permit, ports: Single(80)),
                               Rule(RuleAction.Block, ports: Single(81)))),
        new("地址族不相交", Table(Rule(RuleAction.Permit, family: RuleAddressFamily.Ipv4),
                                 Rule(RuleAction.Block, family: RuleAddressFamily.Ipv6))),
        new("地址族任一侧任意", Table(Rule(RuleAction.Permit),
                                     Rule(RuleAction.Block, family: RuleAddressFamily.Ipv6))),
        new("网段不相交", Table(Rule(RuleAction.Permit, network: Network("10.0.0.0/8")),
                               Rule(RuleAction.Block, network: Network("192.168.0.0/16")))),
        new("网段嵌套", Table(Rule(RuleAction.Permit, network: Network("10.0.0.0/8")),
                             Rule(RuleAction.Block, network: Network("10.1.0.0/16")))),
        new("网段任一侧任意", Table(Rule(RuleAction.Permit, network: Network("10.0.0.0/8")),
                                   Rule(RuleAction.Block))),
        new("进程选择器不同", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                   Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.OtherDos)))),
        new("进程选择器不同（含非 ASCII 路径）", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                   Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.UnicodeDos)))),
        new("进程选择器同一条路径", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                          Rule(RuleAction.Block,
                                               process: ProcessSelector.Parse(@"C:\PROGRAM FILES\APP\APP.EXE")))),
        new("进程任一侧任意", Table(Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.AppDos)),
                                   Rule(RuleAction.Block))),
        new("两条 block 规则", Table(Rule(RuleAction.Block, ports: Single(80)),
                                    Rule(RuleAction.Block, ports: Single(80)))),
        new("block 在前 permit 在后", Table(Rule(RuleAction.Block, ports: Single(80)),
                                           Rule(RuleAction.Permit, ports: Single(80)))),
        new("redirect 后跟 block", Table(Rule(RuleAction.Redirect, ports: Single(80)),
                                        Rule(RuleAction.Block, ports: Single(80)))),
        new("IPv6 网段推出地址族", Table(Rule(RuleAction.Permit, network: Network("2001:db8::/32")),
                                        Rule(RuleAction.Block, network: Network("2001:db8:1::/48")))),
        new("IPv6 与 IPv4 网段不相交", Table(Rule(RuleAction.Permit, network: Network("2001:db8::/32")),
                                             Rule(RuleAction.Block, network: Network("192.168.0.0/16")))),
        new("兜底动作为 block（无规则）", new PolicyTable(Array.Empty<PolicyRule>(), RuleAction.Block)),
        new("兜底 block 与 permit 规则", new PolicyTable(new[] { Rule(RuleAction.Permit, ports: Single(80)) }, RuleAction.Block)),
        new("兜底 block 与全部 block 规则",
            new PolicyTable(new[] { Rule(RuleAction.Block, ports: Single(80)) }, RuleAction.Block)),
        new("block 文件名选择器", Table(Rule(RuleAction.Block, process: ProcessSelector.Parse("svchost.exe")))),
        new("block 目录前缀选择器", Table(Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.ToolsDos)))),
        new("目录前缀 tools 不遮蔽 tools2", Table(
                Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.ToolsDos)),
                Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.Tools2ExeDos)))),
        new("目录前缀后跟后代路径", Table(
                Rule(RuleAction.Permit, process: ProcessSelector.Parse(TestAppId.ToolsDos)),
                Rule(RuleAction.Block, process: ProcessSelector.Parse(TestAppId.ToolsExeDos)))),
        new("多端口数组任一区间相交", Table(
                Rule(RuleAction.Permit, portArray: PortArray(80, 443)),
                Rule(RuleAction.Block, portArray: PortArray(443, 8080)))),
        new("多端口数组全部不相交", Table(
                Rule(RuleAction.Permit, portArray: PortArray(80, 443)),
                Rule(RuleAction.Block, portArray: PortArray(8080, 8443)))),
        new("混合族有效族集相交", Table(
                Rule(RuleAction.Permit,
                     familyArray: new[] { RuleAddressFamily.Ipv4, RuleAddressFamily.Ipv6 },
                     networkArray: new[] { Network("10.0.0.0/8"), Network("2001:db8::/32") }),
                Rule(RuleAction.Block,
                     familyArray: new[] { RuleAddressFamily.Ipv6 },
                     networkArray: new[] { Network("2001:db8:1::/48") }))),
        new("混合族网段收窄后不相交", Table(
                Rule(RuleAction.Permit, networkArray: new[] { Network("10.0.0.0/8") }),
                Rule(RuleAction.Block, familyArray: new[] { RuleAddressFamily.Ipv6 }))),

    };

    private static PolicyTable Table(params PolicyRule[] rules) => new(rules, RuleAction.Permit);

    private static PolicyRule Rule(RuleAction action,
                                   RuleAddressFamily? family = null,
                                   PortRange? ports = null,
                                   RemoteNetwork? network = null,
                                   ProcessSelector? process = null,
                                   RuleAddressFamily[]? familyArray = null,
                                   PortRange[]? portArray = null,
                                   RemoteNetwork[]? networkArray = null,
                                   ProcessSelector[]? processArray = null) =>
        new()
        {
            Action = action,
            Families = familyArray ?? (family is null ? null : new[] { family.Value }),
            RemotePorts = portArray ?? (ports is null ? null : new[] { ports.Value }),
            RemoteNetworks = networkArray ?? (network is null ? null : new[] { network }),
            Processes = processArray ?? (process is null ? null : new[] { process }),
        };

    private static PortRange Single(ushort port) => PortRange.Single(port);

    private static PortRange Ports(ushort begin, ushort end) => new(begin, end);

    private static PortRange[] PortArray(params ushort[] ports) => ports.Select(PortRange.Single).ToArray();

    private static RemoteNetwork Network(string cidr) => RemoteNetwork.Parse(cidr);
}
