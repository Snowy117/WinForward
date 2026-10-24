using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;

namespace WinForward.Probe;

internal static class HostRuleParitySuite
{
    private const string OtherFileName = "not-wfprobe.exe";
    private const string OtherAppId = @"\device\harddiskvolume3\windows\system32\not-wfprobe.exe";
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameNt = 0x00000002;

    public static void Run(WfpDevice device, int timeoutMs)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到当前进程路径");
        string exeName = Path.GetFileName(exe);
        string appId = AppId.ForCurrentProcess();
        int processId = Environment.ProcessId;
        string exeDirectory = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("拿不到当前进程目录");
        string directoryAppId = DevicePathForDirectory(exeDirectory);

        using var echo = new EchoServer(IPAddress.Loopback);
        var target = new IPEndPoint(IPAddress.Loopback, echo.Port);
        using var sandbox = new ProbeSandbox();
        Console.WriteLine($"[rules] parity: exe={exe} app-id={appId} dir-app-id={directoryAppId} port={echo.Port}");

        RunFileNameCases(device, echo, target, exeName, timeoutMs);
        RunExactPathCases(device, echo, target, appId, sandbox, timeoutMs);
        RunDirectoryCases(device, echo, target, directoryAppId, sandbox, timeoutMs);
        RunArrayCases(device, echo, target, exeName, processId, timeoutMs);
        RunBlockCases(device, echo, target, exeName, directoryAppId, appId, processId, sandbox, timeoutMs);
    }

    private static void RunFileNameCases(WfpDevice device, EchoServer echo, IPEndPoint target, string exeName, int timeoutMs)
    {
        TestRule matched = PermitAt(echo.Port);
        matched.Processes = new[] { TestProcessSelector.FileName(exeName.ToUpperInvariant()) };
        ExpectPermit("文件名选择器命中 wfprobe.exe（ASCII 大小写不敏感）", device, target, timeoutMs, matched);

        TestRule otherName = PermitAt(echo.Port);
        otherName.Processes = new[] { TestProcessSelector.FileName(OtherFileName) };
        ExpectFallback("文件名选择器不命中最后一段不同的路径", device, target, timeoutMs, otherName);

        TestRule withoutExtension = PermitAt(echo.Port);
        withoutExtension.Processes = new[] { TestProcessSelector.FileName(Path.GetFileNameWithoutExtension(exeName)) };
        ExpectFallback("文件名选择器不做扩展名补全（wfprobe 不匹配 wfprobe.exe）", device, target, timeoutMs,
                       withoutExtension);
    }

    private static void RunExactPathCases(WfpDevice device,
                                          EchoServer echo,
                                          IPEndPoint target,
                                          string appId,
                                          ProbeSandbox sandbox,
                                          int timeoutMs)
    {
        string copy = sandbox.Copy("same-name");
        string copyAppId = AppId.ForPath(copy);
        Check.True(!string.Equals(appId, copyAppId, StringComparison.OrdinalIgnoreCase),
                   "同名副本的设备路径与原文件不同", $"{appId} vs {copyAppId}");

        TestRule original = PermitAt(echo.Port);
        original.Processes = new[] { TestProcessSelector.Path(appId) };
        ExpectPermit("精确路径只命中该文件（原文件）", device, target, timeoutMs, original);
        ExpectChildFallback("精确路径不命中其他目录的同名文件", device, echo.Port, copy, timeoutMs,
                            "精确路径规则命中原文件、不命中同名副本");

        TestRule copied = PermitAt(echo.Port);
        copied.Processes = new[] { TestProcessSelector.Path(copyAppId) };
        device.SetRuleBlob(BlobBuilder.Build(new[] { copied }, Protocol.RuleActionPermit));
        ExpectChildPermit("精确路径命中副本自身", device, echo.Port, copy, timeoutMs);
        ExpectFallback("精确路径不命中其他同名文件（副本规则）", device, target, timeoutMs, copied);

        TestRule other = PermitAt(echo.Port);
        other.Processes = new[] { TestProcessSelector.Path(OtherAppId) };
        ExpectFallback("精确路径不命中不同的设备路径", device, target, timeoutMs, other);
    }

    private static void RunDirectoryCases(WfpDevice device,
                                          EchoServer echo,
                                          IPEndPoint target,
                                          string directoryAppId,
                                          ProbeSandbox sandbox,
                                          int timeoutMs)
    {
        Check.True(directoryAppId.StartsWith(@"\device\", StringComparison.Ordinal),
                   "目录路径归一化成设备路径", directoryAppId);

        TestRule direct = PermitAt(echo.Port);
        direct.Processes = new[] { TestProcessSelector.Directory(directoryAppId) };
        ExpectPermit("目录选择器命中该目录的直接子文件", device, target, timeoutMs, direct);

        TestRule sibling = PermitAt(echo.Port);
        sibling.Processes = new[] { TestProcessSelector.Directory(directoryAppId + "2") };
        ExpectFallback("目录选择器不误伤同名前缀目录", device, target, timeoutMs, sibling);

        string deepRoot = Path.Combine(sandbox.Root, "deep");
        string deepExe = sandbox.Copy("deep/sub");
        string deepAppId = DevicePathForDirectory(deepRoot);

        TestRule descendant = PermitAt(echo.Port);
        descendant.Processes = new[] { TestProcessSelector.Directory(deepAppId) };
        device.SetRuleBlob(BlobBuilder.Build(new[] { descendant }, Protocol.RuleActionPermit));
        ExpectChildPermit("目录选择器命中任意深度的后代", device, echo.Port, deepExe, timeoutMs);
        ExpectFallback("目录选择器不命中目录之外的进程", device, target, timeoutMs, descendant);
    }

    private static void RunArrayCases(WfpDevice device,
                                      EchoServer echo,
                                      IPEndPoint target,
                                      string exeName,
                                      int processId,
                                      int timeoutMs)
    {
        int spare = echo.Port is 65000 or 65001 ? 64000 : 65000;

        TestRule multiPort = PermitAt(echo.Port);
        multiPort.Ports = new[] { TestPortRange.Single(echo.Port), TestPortRange.Single(spare) };
        ExpectPermit("端口数组 OR：命中第二个端口区间", device, target, timeoutMs, multiPort);

        TestRule otherPorts = PermitAt(echo.Port);
        otherPorts.Ports = new[] { TestPortRange.Single(spare), TestPortRange.Single(spare + 1) };
        ExpectFallback("端口数组 OR：所有区间都不含远端端口时不命中", device, target, timeoutMs, otherPorts);

        TestRule multiProcess = PermitAt(echo.Port);
        multiProcess.Processes = new[]
        {
            TestProcessSelector.FileName(OtherFileName),
            TestProcessSelector.FileName(exeName),
        };
        ExpectPermit("进程数组 OR：第二个选择器命中", device, target, timeoutMs, multiProcess);

        TestRule otherProcesses = PermitAt(echo.Port);
        otherProcesses.Processes = new[]
        {
            TestProcessSelector.FileName(OtherFileName),
            TestProcessSelector.FileName("another-not-wfprobe.exe"),
        };
        ExpectFallback("进程数组 OR：所有选择器都不命中时回退", device, target, timeoutMs, otherProcesses);

        TestRule multiNetwork = PermitAt(echo.Port);
        multiNetwork.Networks = new[] { "10.0.0.0/8", "127.0.0.0/8" };
        ExpectPermit("网段数组 OR：命中第二个网段", device, target, timeoutMs, multiNetwork);

        TestRule otherNetworks = PermitAt(echo.Port);
        otherNetworks.Networks = new[] { "10.0.0.0/8", "192.168.0.0/16" };
        ExpectFallback("网段数组 OR：所有网段都不含远端地址时不命中", device, target, timeoutMs, otherNetworks);

        TestRule bothProtocols = PermitAt(echo.Port);
        bothProtocols.Protocols = new[] { Protocol.RuleProtocolUdp, Protocol.RuleProtocolTcp };
        ExpectPermit("协议数组 OR：TCP 连接命中 tcp 位", device, target, timeoutMs, bothProtocols);

        TestRule udpOnly = PermitAt(echo.Port);
        udpOnly.Protocols = new[] { Protocol.RuleProtocolUdp };
        ExpectFallback("协议数组 OR：只有 udp 时 TCP 连接不命中", device, target, timeoutMs, udpOnly);

        TestRule bothFamilies = PermitAt(echo.Port);
        bothFamilies.Families = new[] { Protocol.RuleFamilyIpv6, Protocol.RuleFamilyIpv4 };
        ExpectPermit("族数组 OR：IPv4 连接命中 v4 位", device, target, timeoutMs, bothFamilies);

        TestRule ipv6Only = PermitAt(echo.Port);
        ipv6Only.Families = new[] { Protocol.RuleFamilyIpv6 };
        ExpectFallback("族数组 OR：只有 IPv6 时 IPv4 连接不命中", device, target, timeoutMs, ipv6Only);

        TestRule matrix = PermitAt(echo.Port);
        matrix.Ports = new[] { TestPortRange.Single(echo.Port), TestPortRange.Single(spare) };
        matrix.Processes = new[]
        {
            TestProcessSelector.FileName(OtherFileName),
            TestProcessSelector.FileName(exeName),
        };
        matrix.Networks = new[] { "10.0.0.0/8", "127.0.0.0/8" };
        matrix.Protocols = new[] { Protocol.RuleProtocolUdp, Protocol.RuleProtocolTcp };
        matrix.Families = new[] { Protocol.RuleFamilyIpv6, Protocol.RuleFamilyIpv4 };
        device.ResetStats();
        ExpectPermit("数组 AND：一条规则写满五个数组仍只生成一条 blob 规则", device, target, timeoutMs, matrix);
        Check.True(device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionPermittedRule &&
                                                 e.MatchedRule == 0 &&
                                                 e.RemotePort == echo.Port &&
                                                 e.ProcessId == (uint)processId &&
                                                 e.Protocol == Protocol.RuleProtocolTcp),
                   "多值规则的事件 rule_index=0（配置规则序号一致）", $"port={echo.Port}");

        TestRule matrixWrongFamily = PermitAt(echo.Port);
        matrixWrongFamily.Ports = new[] { TestPortRange.Single(echo.Port), TestPortRange.Single(spare) };
        matrixWrongFamily.Processes = new[]
        {
            TestProcessSelector.FileName(OtherFileName),
            TestProcessSelector.FileName(exeName),
        };
        matrixWrongFamily.Protocols = new[] { Protocol.RuleProtocolUdp, Protocol.RuleProtocolTcp };
        matrixWrongFamily.Families = new[] { Protocol.RuleFamilyIpv6 };
        ExpectFallback("数组 AND：任一字段不满足（族）即整条不命中", device, target, timeoutMs, matrixWrongFamily);

        TestRule matrixWrongProtocol = PermitAt(echo.Port);
        matrixWrongProtocol.Ports = new[] { TestPortRange.Single(echo.Port), TestPortRange.Single(spare) };
        matrixWrongProtocol.Processes = new[]
        {
            TestProcessSelector.FileName(OtherFileName),
            TestProcessSelector.FileName(exeName),
        };
        matrixWrongProtocol.Networks = new[] { "10.0.0.0/8", "127.0.0.0/8" };
        matrixWrongProtocol.Protocols = new[] { Protocol.RuleProtocolUdp };
        matrixWrongProtocol.Families = new[] { Protocol.RuleFamilyIpv6, Protocol.RuleFamilyIpv4 };
        ExpectFallback("数组 AND：任一字段不满足（协议）即整条不命中", device, target, timeoutMs,
                       matrixWrongProtocol);
    }

    private static void RunBlockCases(WfpDevice device,
                                      EchoServer echo,
                                      IPEndPoint target,
                                      string exeName,
                                      string directoryAppId,
                                      string appId,
                                      int processId,
                                      ProbeSandbox sandbox,
                                      int timeoutMs)
    {
        string copy = sandbox.Copy("block");
        string copyAppId = AppId.ForPath(copy);

        RunBudgetCase(device, echo.Port);

        TestRule block = new()
        {
            Action = Protocol.RuleActionBlock,
            PortStart = (ushort)echo.Port,
            PortEnd = (ushort)echo.Port,
            Protocols = new[] { Protocol.RuleProtocolTcp },
            Processes = new[] { TestProcessSelector.Path(appId), TestProcessSelector.Path(copyAppId) },
        };
        device.SetRuleBlob(BlobBuilder.Build(new[] { block }, Protocol.RuleActionPermit));
        try
        {
            RunSelfBlockChecks(device, echo, target, copy, exeName, directoryAppId, processId, timeoutMs);
        }
        finally
        {
            ClearRules(device);
        }
    }

    private static void RunBudgetCase(WfpDevice device, int port)
    {
        var selectors = new List<TestProcessSelector>(65);
        for (int index = 0; index < 65; index++)
        {
            selectors.Add(TestProcessSelector.Path($@"\device\harddiskvolume3\budget\app{index}.exe"));
        }

        var overBudget = new TestRule
        {
            Action = Protocol.RuleActionBlock,
            PortStart = (ushort)port,
            PortEnd = (ushort)port,
            Protocols = new[] { Protocol.RuleProtocolTcp, Protocol.RuleProtocolUdp },
            Processes = selectors,
        };

        string message = string.Empty;
        bool buildRejected = false;
        try
        {
            _ = BlobBuilder.Build(new[] { overBudget }, Protocol.RuleActionPermit);
        }
        catch (InvalidOperationException exception)
        {
            buildRejected = true;
            message = exception.Message;
        }
        Check.True(buildRejected && message.Contains("预算", StringComparison.Ordinal),
                   "block filter 预算：65 个精确路径 × 2 层 × 2 协议 = 260 > 128，产品编译期拒绝", message);

        ProbeConfig config = device.GetConfig();
        ProbeConfig disabled = config;
        disabled.Enabled = 0;
        device.SetConfig(disabled);
        try
        {
            ExpectDriverRejected(device, "block filter 预算：enabled=0 时驱动也整块拒绝",
                                 BlobBuilder.BuildUnchecked(new[] { overBudget }, Protocol.RuleActionPermit));
        }
        finally
        {
            device.SetConfig(config);
        }
    }

    private static void RunSelfBlockChecks(WfpDevice device,
                                           EchoServer echo,
                                           IPEndPoint target,
                                           string copy,
                                           string exeName,
                                           string directoryAppId,
                                           int processId,
                                           int timeoutMs)
    {
        Check.Equal(1, device.GetRuleInfo().RuleCount, "block 精确路径数组：规则数=1");

        ProbeStats beforeBlock = device.GetStats();
        int acceptedBefore = echo.Accepted;
        bool parentConnected = SocketHelpers.TryConnect(target, timeoutMs, out Socket? socket, out string error);
        socket?.Dispose();
        Check.True(!parentConnected, "block 精确路径数组：原进程连接被拒", error);

        int childExit = RunClient(copy, echo.Port, timeoutMs);
        Check.True(childExit != 0, "block 精确路径数组：副本连接被拒", $"exit={childExit}");
        Check.Equal(acceptedBefore, echo.Accepted, "block 精确路径数组：服务器没有 accept");

        ProbeStats afterBlock = device.GetStats();
        Check.Equal(2, (long)(afterBlock.MatchedBlock - beforeBlock.MatchedBlock),
                    "block 精确路径数组：两个路径各命中一次 matched_block");
        Check.Equal(2, (long)(afterBlock.Blocked - beforeBlock.Blocked), "block 精确路径数组：blocked +2");
        Check.Equal(2, (long)(afterBlock.DeferredBlocks - beforeBlock.DeferredBlocks),
                    "block 精确路径数组：deferred_blocks +2");
        Check.True(device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionBlockedRule &&
                                                 e.MatchedRule == 0 &&
                                                 e.RemotePort == echo.Port &&
                                                 e.ProcessId == (uint)processId),
                   "block 事件带上唯一规则序号 0", $"port={echo.Port}");

        TestRule blockFileName = new()
        {
            Action = Protocol.RuleActionBlock,
            PortStart = (ushort)echo.Port,
            PortEnd = (ushort)echo.Port,
            Processes = new[] { TestProcessSelector.FileName(exeName) },
        };
        ExpectBuildRejected("block + 文件名：编译期拒绝", blockFileName, "文件名");
        ExpectDriverRejected(device, "block + 文件名：驱动整块拒绝",
                             BlobBuilder.BuildUnchecked(new[] { blockFileName }, Protocol.RuleActionPermit));

        TestRule blockDirectory = new()
        {
            Action = Protocol.RuleActionBlock,
            PortStart = (ushort)echo.Port,
            PortEnd = (ushort)echo.Port,
            Processes = new[] { TestProcessSelector.Directory(directoryAppId) },
        };
        ExpectBuildRejected("block + 目录前缀：编译期拒绝", blockDirectory, "目录前缀");
        ExpectDriverRejected(device, "block + 目录前缀：驱动整块拒绝",
                             BlobBuilder.BuildUnchecked(new[] { blockDirectory }, Protocol.RuleActionPermit));
    }

    private static void ClearRules(WfpDevice device) =>
        device.SetRuleBlob(BlobBuilder.Build(Array.Empty<TestRule>(), Protocol.RuleActionPermit));

    private static void ExpectBuildRejected(string name, TestRule rule, string kind)
    {
        string message = string.Empty;
        bool rejected = false;
        try
        {
            _ = BlobBuilder.Build(new[] { rule }, Protocol.RuleActionPermit);
        }
        catch (InvalidOperationException exception)
        {
            rejected = true;
            message = exception.Message;
        }

        Check.True(rejected && message.Contains("#0", StringComparison.Ordinal) &&
                   message.Contains(kind, StringComparison.Ordinal),
                   name, message);
    }

    private static void ExpectDriverRejected(WfpDevice device, string name, byte[] blob)
    {
        ProbeRuleInfo before = device.GetRuleInfo();
        ulong rejectedBefore = device.GetStats().RejectedBlobs;
        bool rejected = false;
        int error = 0;
        try
        {
            device.SetRuleBlob(blob);
        }
        catch (Win32Exception exception)
        {
            rejected = true;
            error = exception.NativeErrorCode;
        }

        Check.True(rejected && error == 87, name, $"error={error}");
        ProbeRuleInfo after = device.GetRuleInfo();
        Check.Equal(before.Generation, after.Generation, $"{name}：generation 不变");
        Check.Equal(before.RuleCount, after.RuleCount, $"{name}：规则数不变");
        Check.Equal(1, (long)(device.GetStats().RejectedBlobs - rejectedBefore), $"{name}：rejected_blobs +1");
    }

    private static TestRule PermitAt(int port) => new()
    {
        Action = Protocol.RuleActionPermit,
        PortStart = (ushort)port,
        PortEnd = (ushort)port,
        TransportProtocol = Protocol.RuleProtocolAny,
    };

    private static void ExpectPermit(string name,
                                     WfpDevice device,
                                     IPEndPoint target,
                                     int timeoutMs,
                                     params TestRule[] rules)
    {
        device.SetRuleBlob(BlobBuilder.Build(rules, Protocol.RuleActionPermit));
        Check.Equal(1, device.GetRuleInfo().RuleCount, $"{name}：blob 规则数=1");
        ProbeStats before = device.GetStats();
        bool echoed = TryEcho(target, timeoutMs, out string detail);
        ProbeStats after = device.GetStats();
        Check.True(echoed, name, detail);
        Check.Equal(1, (long)(after.MatchedPermit - before.MatchedPermit), $"{name}：matched_permit +1");
        Check.Equal(0, (long)(after.FallbackPermit - before.FallbackPermit), $"{name}：fallback_permit +0");
    }

    private static void ExpectFallback(string name,
                                       WfpDevice device,
                                       IPEndPoint target,
                                       int timeoutMs,
                                       params TestRule[] rules)
    {
        device.SetRuleBlob(BlobBuilder.Build(rules, Protocol.RuleActionPermit));
        ProbeStats before = device.GetStats();
        bool echoed = TryEcho(target, timeoutMs, out string detail);
        ProbeStats after = device.GetStats();
        Check.True(echoed, name, detail);
        Check.Equal(0, (long)(after.MatchedPermit - before.MatchedPermit), $"{name}：matched_permit +0");
        Check.Equal(1, (long)(after.FallbackPermit - before.FallbackPermit), $"{name}：fallback_permit +1");
    }

    private static void ExpectChildPermit(string name,
                                          WfpDevice device,
                                          int port,
                                          string executable,
                                          int timeoutMs)
    {
        ProbeStats before = device.GetStats();
        int exitCode = RunClient(executable, port, timeoutMs);
        ProbeStats after = device.GetStats();
        Check.Equal(0, exitCode, $"{name}：子进程回显成功");
        Check.Equal(1, (long)(after.MatchedPermit - before.MatchedPermit), $"{name}：matched_permit +1");
        Check.Equal(0, (long)(after.FallbackPermit - before.FallbackPermit), $"{name}：fallback_permit +0");
    }

    private static void ExpectChildFallback(string name,
                                            WfpDevice device,
                                            int port,
                                            string executable,
                                            int timeoutMs,
                                            string detail)
    {
        ProbeStats before = device.GetStats();
        int exitCode = RunClient(executable, port, timeoutMs);
        ProbeStats after = device.GetStats();
        Check.Equal(0, exitCode, $"{name}：子进程按 fallback 放行并回显");
        Check.Equal(0, (long)(after.MatchedPermit - before.MatchedPermit), $"{name}：matched_permit +0");
        Check.Equal(1, (long)(after.FallbackPermit - before.FallbackPermit), $"{name}：fallback_permit +1");
        Console.WriteLine($"[rules] parity: {detail} exit={exitCode}");
    }

    private static bool TryEcho(IPEndPoint target, int timeoutMs, out string detail)
    {
        if (!SocketHelpers.TryConnect(target, timeoutMs, out Socket? socket, out string error))
        {
            detail = error;
            return false;
        }

        using (socket)
            return SocketHelpers.TryEcho(socket!, "parity", timeoutMs, out detail);
    }

    private static int RunClient(string executable, int port, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("client");
        startInfo.ArgumentList.Add("--remote");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        startInfo.ArgumentList.Add("--timeout-ms");
        startInfo.ArgumentList.Add(timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动子进程");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string DevicePathForDirectory(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting,
                                                 FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                                     $"打开目录失败：{path}");

        var buffer = new System.Text.StringBuilder(1024);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameNt);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                                     $"GetFinalPathNameByHandleW 失败：{path}");

        string text = buffer.ToString();
        if (!text.StartsWith('\\'))
            throw new InvalidOperationException($"目录没有归一成设备路径：{text}");
        var folded = new System.Text.StringBuilder(text.Length);
        foreach (char character in text)
            folded.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        return folded.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                                              SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName,
                                                     uint desiredAccess,
                                                     uint shareMode,
                                                     IntPtr securityAttributes,
                                                     uint creationDisposition,
                                                     uint flagsAndAttributes,
                                                     IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                                              SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,
                                                         System.Text.StringBuilder path,
                                                         uint pathLength,
                                                         uint flags);

    private sealed class ProbeSandbox : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
                                                    $"wfprobe-parity-{Guid.NewGuid():N}");

        public string Root => root;

        public string Copy(string relativeDirectory)
        {
            string directory = Path.Combine(root, relativeDirectory);
            Directory.CreateDirectory(directory);
            string sourceExe = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到当前进程路径");
            string sourceDirectory = Path.GetDirectoryName(sourceExe) ??
                                     throw new InvalidOperationException("拿不到当前进程目录");
            string baseName = Path.GetFileNameWithoutExtension(sourceExe);
            string destination = Path.Combine(directory, Path.GetFileName(sourceExe));
            File.Copy(sourceExe, destination, true);
            foreach (string extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            {
                File.Copy(Path.Combine(sourceDirectory, baseName + extension),
                          Path.Combine(directory, baseName + extension),
                          true);
            }

            return destination;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }
}
