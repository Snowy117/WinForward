using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinForward.Probe;

// 四道防循环闸的运行时验证。四道闸各自需要的场景：
//   1. 监听端口闸：客户端直接连监听端口，连接不应再被重定向；
//   4. self-PID 闸：代理进程（独立 PID）的出站连接不应被重定向；
//   3. redirect state 闸：代理在出站 socket 上带回 redirect records，查询状态应放行；
//   2. 条件标志闸：与 3 共用同一场景，哪一道先命中由事件里的 decision 反映。
internal static class GateSuite
{
    public static async Task<int> RunAsync(Options options)
    {
        int timeoutMs = options.GetInt("timeout-ms", 20000);
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到当前进程路径");
        string appId = AppId.ForCurrentProcess();
        int processId = Environment.ProcessId;
        Console.WriteLine($"[gates] pid={processId} app-id={appId}");

        using var device = new WfpDevice();
        device.ResetStats();
        using var echo = new EchoServer(IPAddress.Loopback);
        var echoTarget = new IPEndPoint(IPAddress.Loopback, echo.Port);

        try
        {
            // 闸 1：监听端口
            {
                using RelayHandle relay = await RelayHandle.StartAsync(exe, 1, null, false, timeoutMs);
                try
                {
                    byte[] blob = BlobBuilder.Build(new[]
                    {
                        new TestRule
                        {
                            Action = Protocol.RuleActionRedirect,
                            PortStart = 1,
                            PortEnd = 65535,
                            Process = appId,
                        },
                    }, Protocol.RuleActionPermit);
                    device.SetRuleBlob(blob);
                    SetConfig(device, relay.Port, relay.Pid, Array.Empty<uint>());

                    bool connected = SocketHelpers.TryConnect(new IPEndPoint(IPAddress.Loopback, relay.Port), timeoutMs,
                                                             out Socket? socket, out string error);
                    Check.True(connected, "闸 1：连接监听端口成功", error);
                    socket?.Dispose();
                    (int exitCode, string output) = await relay.WaitAsync(timeoutMs);
                    Check.Equal(0, exitCode, "闸 1：relay 正常退出");
                    Check.Contains(output, "accept idx=0 context=0B", "闸 1：连接没有被再次重定向（无 redirect context）");
                    Check.True(relay.Events(device).Any(e => e.Decision == Protocol.DecisionPermittedListenerPort &&
                                                             e.RemotePort == relay.Port),
                               "闸 1：事件 decision=permitted_listener_port", $"port={relay.Port}");
                }
                finally
                {
                    await relay.StopAsync();
                }
            }

            // 闸 4：self-PID
            {
                using RelayHandle relay = await RelayHandle.StartAsync(exe, 1, echoTarget.ToString(), false, timeoutMs);
                try
                {
                    byte[] blob = BlobBuilder.Build(new[] { TestRule.RedirectPort(echo.Port) }, Protocol.RuleActionPermit);
                    device.SetRuleBlob(blob);
                    SetConfig(device, relay.Port, relay.Pid, new[] { (uint)relay.Pid });

                    bool connected = SocketHelpers.TryConnect(echoTarget, timeoutMs, out Socket? socket, out string error);
                    Check.True(connected, "闸 4：原始连接被重定向后完成", error);
                    socket?.Dispose();
                    (int exitCode, string output) = await relay.WaitAsync(timeoutMs);
                    Check.Contains(output, "accept idx=0 context=36B", "闸 4：代理接到带 context 的连接");
                    Check.Contains(output, "outbound=echo", "闸 4：代理出站连接直达真实目的地");
                    Check.True(!output.Contains("accept idx=1", StringComparison.Ordinal),
                               "闸 4：没有第二次 accept（没有成环）", "accepts=1");
                    Check.Equal(0, exitCode, "闸 4：relay 退出码");
                    Check.True(relay.Events(device).Any(e => e.Decision == Protocol.DecisionPermittedSelfProcess &&
                                                             e.ProcessId == (uint)relay.Pid),
                               "闸 4：事件 decision=permitted_self_process", $"pid={relay.Pid}");
                }
                finally
                {
                    await relay.StopAsync();
                }
            }

            // 闸 3 / 闸 2：redirect records + 条件标志（self-PID 集合为空，排除闸 4 的干扰）
            {
                using RelayHandle relay = await RelayHandle.StartAsync(exe, 1, echoTarget.ToString(), true, timeoutMs);
                try
                {
                    byte[] blob = BlobBuilder.Build(new[] { TestRule.RedirectPort(echo.Port) }, Protocol.RuleActionPermit);
                    device.SetRuleBlob(blob);
                    SetConfig(device, relay.Port, relay.Pid, Array.Empty<uint>());

                    bool connected = SocketHelpers.TryConnect(echoTarget, timeoutMs, out Socket? socket, out string error);
                    Check.True(connected, "闸 3：原始连接被重定向后完成", error);
                    socket?.Dispose();
                    (int exitCode, string output) = await relay.WaitAsync(timeoutMs);
                    Check.Contains(output, "relay records=", "闸 3：代理拿到 redirect records");
                    Check.Contains(output, "outbound=echo", "闸 3：带 records 的出站连接直达目的地");
                    Check.True(!output.Contains("accept idx=1", StringComparison.Ordinal),
                               "闸 3：没有第二次 accept（没有成环）", "accepts=1");
                    Check.Equal(0, exitCode, "闸 3：relay 退出码");
                    ProbeEvent[] events = relay.Events(device);
                    Check.True(events.Any(e => e.Decision == Protocol.DecisionPermittedRedirectState &&
                                               e.ProcessId == (uint)relay.Pid),
                               "闸 3：事件 decision=permitted_redirect_state", $"pid={relay.Pid}");
                    Console.WriteLine($"[gates] 闸 3 场景观察到的 decision：" +
                                      string.Join(",", events.Select(e => Program.DecisionName(e.Decision)).Distinct()));
                }
                finally
                {
                    await relay.StopAsync();
                }
            }
        }
        finally
        {
            var config = device.GetConfig();
            config.Enabled = 0;
            device.SetConfig(config);
        }

        Console.WriteLine($"[gates] verdict={(Check.Failures == 0 ? "PASS" : "FAIL")} failures={Check.Failures}");
        return Check.Failures == 0 ? 0 : 7;
    }

    private static void SetConfig(WfpDevice device, int listenerPort, int listenerPid, uint[] selfPids)
    {
        if (selfPids.Length > Protocol.MaxSelfProcessIds)
            throw new ArgumentException("self pids 过多");
        var config = new ProbeConfig
        {
            AbiVersion = Protocol.AbiVersion,
            Enabled = 1,
            ListenerProcessId = (uint)listenerPid,
            ListenerPort = (ushort)listenerPort,
            SelfProcessCount = (uint)selfPids.Length,
        };
        unsafe
        {
            for (int index = 0; index < selfPids.Length; index++)
                config.SelfProcessIds[index] = selfPids[index];
        }
        device.SetConfig(config);
        Program.PrintConfig("gates", device.GetConfig());
    }

    private sealed class RelayHandle : IDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new();
        private readonly object gate = new();
        private readonly List<ProbeEvent> events = new();

        private RelayHandle(Process process, int pid, int port)
        {
            this.process = process;
            Pid = pid;
            Port = port;
        }

        public int Pid { get; private set; }

        public int Port { get; private set; }

        public string Output
        {
            get
            {
                lock (gate)
                    return output.ToString();
            }
        }

        public static async Task<RelayHandle> StartAsync(string exe,
                                                         int expectAccepts,
                                                         string? outbound,
                                                         bool records,
                                                         int timeoutMs)
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("relay");
            startInfo.ArgumentList.Add("--bind");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("--expect-accepts");
            startInfo.ArgumentList.Add(expectAccepts.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--accept-window-ms");
            startInfo.ArgumentList.Add("2500");
            startInfo.ArgumentList.Add("--outbound-records");
            startInfo.ArgumentList.Add(records ? "1" : "0");
            if (outbound is not null)
            {
                startInfo.ArgumentList.Add("--outbound");
                startInfo.ArgumentList.Add(outbound);
            }

            var process = new Process { StartInfo = startInfo };
            var handle = new RelayHandle(process, 0, 0);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                    return;
                lock (handle.gate)
                {
                    handle.output.AppendLine(args.Data);
                    RelayHandle? self = handle;
                    if (self.Pid == 0 && args.Data.StartsWith("relay-ready ", StringComparison.Ordinal))
                    {
                        foreach (string part in args.Data.Split(' '))
                        {
                            if (part.StartsWith("pid=", StringComparison.Ordinal))
                                self.Pid = int.Parse(part[4..], CultureInfo.InvariantCulture);
                            if (part.StartsWith("port=", StringComparison.Ordinal))
                                self.Port = int.Parse(part[5..], CultureInfo.InvariantCulture);
                        }
                    }
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null)
                    return;
                lock (handle.gate)
                    handle.output.AppendLine(args.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            int waited = 0;
            while (handle.Pid == 0 && waited < timeoutMs)
            {
                await Task.Delay(50);
                waited += 50;
            }
            if (handle.Pid == 0)
            {
                process.Kill();
                throw new InvalidOperationException("relay 没有在预期时间内就绪");
            }
            return handle;
        }

        public ProbeEvent[] Events(WfpDevice device)
        {
            ProbeEvent[] snapshot = device.GetEvents(64);
            lock (gate)
            {
                events.Clear();
                events.AddRange(snapshot);
                return events.ToArray();
            }
        }

        public async Task<(int ExitCode, string Output)> WaitAsync(int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw new InvalidOperationException("relay 超时未退出");
            }
            process.WaitForExit();
            return (process.ExitCode, Output);
        }

        public async Task StopAsync()
        {
            if (!process.HasExited)
            {
                process.Kill();
                try
                {
                    await process.WaitForExitAsync();
                }
                catch
                {
                    // 测试清理路径，进程可能已经先退出。
                }
            }
        }

        public void Dispose()
        {
            process.Dispose();
        }
    }
}
