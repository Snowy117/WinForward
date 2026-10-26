using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

internal static class SelfTest
{
    public static async Task<int> RunAsync(Options options)
    {
        int iterations = options.GetInt("iterations", 5);
        int timeoutMs = options.GetInt("timeout-ms", 5000);
        int settleMs = options.GetInt("settle-ms", 1000);
        bool dual = options.Has("dual") && options.GetInt("dual", 1) != 0;
        IPEndPoint remote = Program.ParseEndPoint(options.Require("remote"));
        int processId = Environment.ProcessId;

        using var device = new WfpDevice();
        device.ResetStats();

        string bindText = options.Get("bind") ?? (dual ? "::1" : "127.0.0.1");
        TcpListener listener = dual ? new TcpListener(IPAddress.IPv6Any, 0) : new TcpListener(IPAddress.Parse(bindText), 0);
        if (dual)
            listener.Server.DualMode = true;
        listener.Start();
        int listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var config = new ProbeConfig
        {
            AbiVersion = Protocol.AbiVersion,
            Enabled = 1,
            ListenerProcessId = (uint)processId,
            ListenerPort = (ushort)listenerPort,
            SelfProcessCount = 0,
        };
        device.SetConfig(config);
        Program.PrintConfig("selftest", device.GetConfig());

        byte[] blob = BlobBuilder.Build(new[] { TestRule.RedirectPort(remote.Port) }, Protocol.RuleActionPermit);
        device.SetRuleBlob(blob);
        ProbeRuleInfo ruleInfo = device.GetRuleInfo();
        Check.Equal(1, ruleInfo.RuleCount, "selftest 规则数");
        Check.Equal(Protocol.RuleActionPermit, ruleInfo.FallbackAction, "selftest 兜底动作");

        int failed = 0;
        int contextsOk = 0;
        int contextsFailed = 0;
        int echoOk = 0;
        var endpointCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        try
        {
            for (int index = 0; index < iterations; index++)
            {
                using var timeout = new CancellationTokenSource(timeoutMs);
                ValueTask<Socket> acceptTask = listener.AcceptSocketAsync(timeout.Token);
                Socket? client = null;
                TcpClient? tcp = null;
                try
                {
                    if (dual)
                    {
                        tcp = new TcpClient();
                        await Task.Run(() => tcp.Connect(remote.Address.ToString(), remote.Port));
                        client = tcp.Client;
                    }
                    else
                    {
                        client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        await client.ConnectAsync(remote, timeout.Token);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _ = acceptTask.AsTask().ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    Console.WriteLine($"[selftest] #{index} connect 失败：{ex.GetType().Name}: {ex.Message}");
                    client?.Dispose();
                    tcp?.Dispose();
                    continue;
                }

                Socket server;
                try
                {
                    server = await acceptTask;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"[selftest] #{index} accept 失败：{ex.GetType().Name}: {ex.Message}");
                    client?.Dispose();
                    tcp?.Dispose();
                    continue;
                }

                using (server)
                {
                    if (server.LocalEndPoint is IPEndPoint local)
                    {
                        string key = $"{local.Address}:{local.Port}";
                        endpointCounts[key] = endpointCounts.TryGetValue(key, out int seen) ? seen + 1 : 1;
                    }

                    if (TryCheckContext(server, remote, processId, out string contextText, out string contextHex))
                    {
                        contextsOk++;
                        if (index == 0)
                            Console.WriteLine($"[selftest] #{index} context raw={contextHex} {contextText}");
                    }
                    else
                    {
                        contextsFailed++;
                        Console.WriteLine($"[selftest] #{index} context 校验失败");
                    }

                    try
                    {
                        byte[] payload = { 0x5A };
                        client!.ReceiveTimeout = timeoutMs;
                        server.ReceiveTimeout = timeoutMs;
                        client.Send(payload);
                        byte[] buffer = new byte[16];
                        int received = server.Receive(buffer);
                        if (received > 0)
                        {
                            server.Send(buffer, received, SocketFlags.None);
                            int back = client.Receive(buffer);
                            if (back == received)
                                echoOk++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[selftest] #{index} echo 失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }

                client?.Dispose();
                tcp?.Dispose();
            }
        }
        finally
        {
            listener.Stop();
        }

        config.Enabled = 0;
        device.SetConfig(config);
        await Task.Delay(settleMs);

        ProbeStats stats = device.GetStats();
        ProbeEvent[] events = device.GetEvents(64);

        Console.WriteLine($"[selftest] iterations={iterations} completed={iterations - failed} failures={failed} " +
                          $"contexts_ok={contextsOk} contexts_failed={contextsFailed} echo_ok={echoOk}");
        Console.WriteLine($"[selftest] endpoints: {string.Join(", ", endpointCounts.Select(pair => $"{pair.Key}x{pair.Value}"))}");
        Console.WriteLine($"[selftest] stats: classify={stats.ClassifyCalls} redirected={stats.Redirected} " +
                          $"contexts_allocated={stats.ContextsAllocated} alloc_failures={stats.ContextAllocFailures} " +
                          $"apply_failures={stats.ApplyFailures}");
        Console.WriteLine($"[selftest] events(recent {events.Length}): redirected={events.Count(e => e.Decision == Protocol.DecisionRedirected)} " +
                          $"any_to_loopback={events.Count(e => e.Branch == 1)} local_address={events.Count(e => e.Branch == 2)}");

        Check.Equal(iterations, iterations - failed, "连接数");
        Check.Equal(iterations, contextsOk, "context 全部读回并通过校验");
        Check.Equal(iterations, echoOk, "echo 全部成功");
        Check.Equal(iterations, (long)stats.Redirected, "redirected 计数与连接数一致");
        Check.Equal(iterations, (long)stats.ContextsAllocated, "contexts_allocated 与连接数一致");
        bool addressOk = endpointCounts.Keys.All(key => dual
                                                             ? key.Contains("::", StringComparison.Ordinal)
                                                             : key.StartsWith("127.0.0.1:", StringComparison.Ordinal));
        Check.True(addressOk, "accepted 端点在预期地址族", string.Join(",", endpointCounts.Keys));

        bool semanticIpv4 = remote.AddressFamily == AddressFamily.InterNetwork ||
                            remote.Address.IsIPv4MappedToIPv6;
        Console.WriteLine($"[selftest] verdict={(Check.Failures == 0 ? "PASS" : "FAIL")} dual={dual} " +
                          $"family={(semanticIpv4 ? 4 : 6)}");
        return Check.Failures == 0 ? 0 : 5;
    }

    private static bool TryCheckContext(Socket socket,
                                        IPEndPoint remote,
                                        int processId,
                                        out string text,
                                        out string hex)
    {
        text = string.Empty;
        hex = string.Empty;
        try
        {
            byte[] raw = WfpSockets.QueryRedirectContext(socket);
            if (raw.Length != 36)
                return false;
            hex = Convert.ToHexString(raw);

            RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
            if (context.Magic != Protocol.RedirectContextMagic ||
                context.Version != Protocol.RedirectContextVersion ||
                context.ProcessId != (uint)processId ||
                context.OriginalPort != (ushort)remote.Port)
                return false;

            // 双模客户端连 IPv4 字面量时 WFP 在 V6 层给出 ::ffff:a.b.c.d；host classify 把它归一成
            // IPv4 才写 redirect context。mapped 地址的前 10 个字节是零，v4 字节只能从 MapToIPv4 取，
            // 直接读原始 16 字节的前四个会得到 0.0.0.0。
            byte[] remoteBytes = remote.Address.GetAddressBytes();
            bool expectIpv4 = remote.AddressFamily == AddressFamily.InterNetwork ||
                              remote.Address.IsIPv4MappedToIPv6;
            if (remote.Address.IsIPv4MappedToIPv6)
                remoteBytes = remote.Address.MapToIPv4().GetAddressBytes();
            byte expectedFamily = expectIpv4 ? (byte)4 : (byte)6;
            if (context.Family != expectedFamily)
                return false;

            unsafe
            {
                if (expectIpv4)
                {
                    for (int index = 0; index < 4; index++)
                    {
                        if (context.OriginalAddress[index] != remoteBytes[index])
                            return false;
                    }
                    for (int index = 4; index < 16; index++)
                    {
                        if (context.OriginalAddress[index] != 0)
                            return false;
                    }
                }
                else
                {
                    for (int index = 0; index < 16; index++)
                    {
                        if (context.OriginalAddress[index] != remoteBytes[index])
                            return false;
                    }
                }

                text = $"pid={context.ProcessId} family={context.Family} " +
                       $"original={Program.AddressText(context.OriginalAddress, context.Family)}:{context.OriginalPort}";
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
