using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

internal static class SelfTest
{
    public static async Task<int> RunAsync(Options options)
    {
        int iterations = options.GetInt("iterations", 5);
        int port = options.GetInt("port", 18080);
        int timeoutMs = options.GetInt("timeout-ms", 5000);
        int settleMs = options.GetInt("settle-ms", 1500);
        uint mode = Program.ParseMode(options.Get("mode") ?? "documented");
        string bind = options.Get("bind") ?? "127.0.0.1";
        string? source = options.Get("source");
        string? expectAddress = options.Get("expect-address");
        string? expectBranch = options.Get("expect-branch");
        IPEndPoint remote = Program.ParseEndPoint(options.Require("remote"));
        uint overrideAddress = options.Get("local-address") is { } localText ? Program.IpToNetworkUInt32(localText) : 0u;
        int processId = Environment.ProcessId;

        using var device = new WfpDevice();
        device.ResetStats();
        PoolTagUsage? poolBefore = PoolTags.Find(Protocol.PoolTag);

        var config = new ProbeConfig
        {
            AbiVersion = Protocol.AbiVersion,
            Enabled = 1,
            Mode = mode,
            TargetProcessId = (uint)processId,
            ListenerProcessId = (uint)processId,
            ListenerPort = (ushort)port,
            LocalAddressOverride = overrideAddress,
        };
        device.SetConfig(config);
        Program.PrintConfig("selftest", device.GetConfig());

        var listener = new TcpListener(IPAddress.Parse(bind), port);
        listener.Start();

        var endpointCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int failed = 0;
        int contextsOk = 0;
        int contextsFailed = 0;
        int echoOk = 0;
        byte[] expectedAddressBytes = remote.Address.GetAddressBytes();

        try
        {
            for (int index = 0; index < iterations; index++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                client.NoDelay = true;
                if (source is not null)
                    client.Bind(new IPEndPoint(IPAddress.Parse(source), 0));

                ValueTask<Socket> acceptTask = listener.AcceptSocketAsync(timeout.Token);
                try
                {
                    await client.ConnectAsync(remote, timeout.Token);
                }
                catch (Exception ex)
                {
                    failed++;
                    _ = acceptTask.AsTask().ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    Console.WriteLine($"[selftest] #{index} connect 失败：{ex.GetType().Name}: {ex.Message}");
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
                    continue;
                }

                using (server)
                {
                    if (server.LocalEndPoint is IPEndPoint local)
                    {
                        string key = $"{local.Address}:{local.Port}";
                        endpointCounts[key] = endpointCounts.TryGetValue(key, out int seen) ? seen + 1 : 1;
                    }

                    if (TryReadContext(server, remote, processId, expectedAddressBytes, out string contextText, out string contextHex))
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
                        client.ReceiveTimeout = timeoutMs;
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
        PoolTagUsage? poolAfter = PoolTags.Find(Protocol.PoolTag);

        int eventRedirected = events.Count(item => item.Decision == 7);
        int eventLoopback = events.Count(item => item.Branch == 1);
        int eventLocal = events.Count(item => item.Branch == 2);

        Console.WriteLine($"[selftest] iterations={iterations} completed={iterations - failed} failures={failed} " +
                          $"contexts_ok={contextsOk} contexts_failed={contextsFailed} echo_ok={echoOk}");
        Console.WriteLine($"[selftest] endpoints: {string.Join(", ", endpointCounts.Select(pair => $"{pair.Key}x{pair.Value}"))}");
        Console.WriteLine($"[selftest] stats: classify={stats.ClassifyCalls} redirected={stats.Redirected} " +
                          $"contexts_allocated={stats.ContextsAllocated} alloc_failures={stats.ContextAllocFailures} " +
                          $"apply_failures={stats.ApplyFailures}");
        Console.WriteLine($"[selftest] events(recent {events.Length}): redirected={eventRedirected} " +
                          $"any_to_loopback={eventLoopback} local_address={eventLocal} " +
                          $"wrong_pid={events.Count(item => item.Decision == 1)} " +
                          $"listener_port={events.Count(item => item.Decision == 2)}");
        Console.WriteLine($"[selftest] pool tag {Protocol.PoolTag}: " + FormatPool(poolBefore, poolAfter));

        bool addressOk = true;
        if (expectAddress is not null)
            addressOk = endpointCounts.Keys.All(key => key.StartsWith(expectAddress + ":", StringComparison.Ordinal));

        bool branchOk = true;
        if (expectBranch is not null)
        {
            branchOk = expectBranch switch
            {
                "loopback" => eventLoopback > 0 && eventLocal == 0,
                "local" => eventLocal > 0 && eventLoopback == 0,
                _ => false,
            };
        }

        bool pass = failed == 0 && contextsFailed == 0 && contextsOk == iterations && echoOk == iterations &&
                   addressOk && branchOk;
        Console.WriteLine($"[selftest] verdict={(pass ? "PASS" : "FAIL")} address_ok={addressOk} branch_ok={branchOk}");
        return pass ? 0 : 5;
    }

    private static bool TryReadContext(Socket socket,
                                       IPEndPoint remote,
                                       int processId,
                                       byte[] expectedAddress,
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
                context.Family != 4 ||
                context.Protocol != 6 ||
                context.ProcessId != (uint)processId ||
                context.OriginalPort != (ushort)remote.Port)
                return false;

            unsafe
            {
                for (int index = 0; index < 4; index++)
                {
                    if (context.OriginalAddress[index] != expectedAddress[index])
                        return false;
                }

                text = $"pid={context.ProcessId} original={Program.AddressText(context.OriginalAddress)}:{context.OriginalPort}";
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPool(PoolTagUsage? before, PoolTagUsage? after)
    {
        static string One(PoolTagUsage? usage) => usage is { } value
            ? $"allocs={value.NonPagedAllocs} frees={value.NonPagedFrees} " +
              $"outstanding={value.NonPagedOutstanding} bytes={value.NonPagedUsed}"
            : "missing";

        return $"before[{One(before)}] after[{One(after)}]";
    }
}
