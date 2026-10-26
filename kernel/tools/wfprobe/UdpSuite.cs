using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WinForward.Probe;

// UDP 正式能力的验证套件（U1–U13）。套件进程自己持有 listener，客户端跑在子进程里：
// listener 的 PID 进 self_process_ids；同 socket 回包不会重新 classify，self-PID 闸用独立 socket 验证。
internal static class UdpSuite
{
    private const string OtherAppId = @"\device\harddiskvolume3\windows\system32\not-wfprobe.exe";
    private const int MaxPayload = 65536;
    private const int MaxControl = 2048;
    private const int ContextSize = 36;

    public static async Task<int> RunAsync(Options options)
    {
        int timeoutMs = options.GetInt("timeout-ms", 5000);
        int connectedWindowMs = options.GetInt("connected-window-ms", 1000);
        int datagrams = options.GetInt("datagrams", 3);
        string pingHost = options.Get("ping-host") ?? "192.168.100.4";
        int colon = pingHost.LastIndexOf(':');
        if (colon > 0 && !pingHost.Contains(']'))
            pingHost = pingHost[..colon];

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到当前进程路径");
        string appId = AppId.ForCurrentProcess();
        int processId = Environment.ProcessId;
        Console.WriteLine($"[udp-rules] pid={processId} app-id={appId} exe={exe}");
        string? only = options.Get("only");
        string[] knownCases = { "U1", "U2", "U3", "U4", "U5", "U6", "U7", "U8", "U9", "U10", "U11", "U12", "U13", "clear-self" };
        string[] selected = only is null
            ? knownCases
            : knownCases.Where(id => only.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                        .Contains(id, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
        if (only is not null && selected.Length == 0)
        {
            Console.WriteLine($"[udp-rules] --only 没有匹配任何用例：{only}");
            return 9;
        }
        Console.WriteLine($"[udp-rules] 选中 {selected.Length} 个用例：{string.Join(",", selected)}");
        static bool Run(string[] cases, string id) => cases.Contains(id, StringComparer.Ordinal);

        using var device = new WfpDevice();
        device.SetConfig(new ProbeConfig { AbiVersion = Protocol.AbiVersion, Enabled = 0 });
        device.ResetStats();

        using var listener = new RedirectListener();
        using var blockSink = new BlockSinkBinding();
        var config = new ProbeConfig
        {
            AbiVersion = Protocol.AbiVersion,
            Enabled = 1,
            ListenerProcessId = (uint)processId,
            ListenerPort = (ushort)listener.Port,
            BlockSinkPort = (uint)blockSink.Port,
            SelfProcessCount = 1,
        };
        unsafe
        {
            config.SelfProcessIds[0] = (uint)processId;
        }

        device.SetConfig(config);
        Program.PrintConfig("udp-rules", device.GetConfig());
        Console.WriteLine($"[udp-rules] listener dual-mode port={listener.Port} block-sink-port={blockSink.Port}");



        using var context = new SuiteContext(device, listener, blockSink, exe, appId, processId, timeoutMs);
        try
        {
            if (Run(selected, "U1")) await U1(context);
            if (Run(selected, "U12")) await U12(context, connectedWindowMs);
            if (Run(selected, "U2")) await U2(context, datagrams);
            if (Run(selected, "U3")) await U3(context, datagrams);
            if (Run(selected, "U4")) await U4(context);
            if (Run(selected, "U5")) await U5(context);
            if (Run(selected, "U6")) await U6(context);
            if (Run(selected, "U7")) await U7(context, pingHost);
            if (Run(selected, "U8")) await U8(context);
            if (Run(selected, "U9")) await U9(context);
            if (Run(selected, "U10")) await U10(context);
            if (Run(selected, "U11")) await U11(context);
            if (Run(selected, "U13")) await U13(context);
            if (Run(selected, "clear-self")) ClearSelfRegression(device);

        }
        finally
        {
            context.SetBlob(Array.Empty<TestRule>(), Protocol.RuleActionPermit);
            config.Enabled = 0;
            device.SetConfig(config);
        }

        ProbeStats final = device.GetStats();
        Console.WriteLine($"[udp-rules] stats: classify={final.ClassifyCalls} permitted={final.Permitted} " +
                          $"blocked={final.Blocked} redirected={final.Redirected} contexts={final.ContextsAllocated}");
        Console.WriteLine($"[udp-rules] udp: calls={final.UdpClassifyCalls} permitted={final.UdpPermitted} " +
                          $"blocked={final.UdpBlocked} redirected={final.UdpRedirected}");
        Console.WriteLine($"[udp-rules] verdict={(Check.Failures == 0 ? "PASS" : "FAIL")} failures={Check.Failures}");
        return Check.Failures == 0 ? 0 : 8;
    }

    private static async Task U1(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U1 unconnected redirect + context =====");
        int port = FreeUdpPort();
        context.SetBlob(new[] { TestRule.RedirectPort(port, null, Protocol.RuleProtocolUdp) });
        ProbeStats before = context.Begin();

        using UdpClientProcess client = context.StartClient(
            "--remote", $"127.0.0.1:{port}", "--mode", "unconnected", "--payload", "u1",
            "--count", "1", "--timeout-ms", "2000");
        await client.WaitForReadyAsync(context.TimeoutMs);
        ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
        ProbeStats redirected = context.Device.GetStats();

        Check.Equal(1, (long)(redirected.UdpClassifyCalls - before.UdpClassifyCalls), "U1: udp_classify_calls +1");
        Check.Equal(1, (long)(redirected.UdpRedirected - before.UdpRedirected), "U1: udp_redirected +1");
        Check.Equal(0, (long)(redirected.UdpPermitted - before.UdpPermitted), "U1: 回包前 udp_permitted 不变");
        Check.Equal(1, (long)(redirected.ContextsAllocated - before.ContextsAllocated), "U1: contexts_allocated +1");
        Check.Equal(1, (long)(redirected.MatchedRedirect - before.MatchedRedirect), "U1: matched_redirect +1");

        RedirectContext redirect = ParseContext(datagram, "U1");
        Check.Equal(4, redirect.Family, "U1: context family=4");
        Check.Equal(17, redirect.Protocol, "U1: context protocol=17");
        Check.Equal(port, redirect.OriginalPort, "U1: context 原始端口");
        Check.Equal(client.Pid, (long)redirect.ProcessId, "U1: context PID 是客户端子进程");
        Check.Equal(0, redirect.RuleIndex, "U1: context rule=0");
        EqualText("127.0.0.1", ContextAddress(redirect), "U1: context 原始地址");
        EqualText("u1", Encoding.UTF8.GetString(datagram.Payload), "U1: listener 收到的 payload");

        ProbeEvent[] events = context.Device.GetEvents(64);
        Check.True(events.Any(e => e.Decision == Protocol.DecisionRedirected && e.Protocol == 17 &&
                                   e.RemotePort == port && e.MatchedRule == 0 && e.ProcessId == (uint)client.Pid),
                   "U1: 事件 decision=redirected protocol=17 rule=0 pid=客户端",
                   DescribeEvents(events));

        context.Listener.Reply(datagram);
        (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, exitCode, "U1: 客户端退出码");
        Check.Contains(output, "recv=2B", "U1: 客户端收到 listener 回包");

        ProbeStats replied = context.Device.GetStats();
        Check.Equal(1, (long)(replied.UdpClassifyCalls - before.UdpClassifyCalls),
                    "U1: listener 回包不重新 classify（同一 listener socket）");
        Check.Equal(0, (long)(replied.UdpPermitted - before.UdpPermitted), "U1: 回包不增加 udp_permitted");
        Check.Equal(0, context.Device.GetEvents(64).Count(e => e.Protocol == 17 &&
                                                               e.Decision == Protocol.DecisionPermittedSelfProcess),
                    "U1: 回包没有产生 self-PID 事件");
        CheckUdpInvariant(before, redirected, "U1 redirect");
        CheckUdpInvariant(before, replied, "U1 回包后");

        context.Device.ResetStats();
        using var selfSender = new UdpPeer();
        selfSender.Send(Encoding.UTF8.GetBytes("u1-self"), new IPEndPoint(IPAddress.Loopback, port));
        ProbeStats selfStats = context.Device.GetStats();
        Check.Equal(1, (long)selfStats.UdpClassifyCalls, "U1: self-PID 出站 classify 一次");
        Check.Equal(1, (long)selfStats.UdpPermitted, "U1: self-PID 出站被放行");
        Check.Equal(1, (long)selfStats.PermittedSelfProcess, "U1: 闸 4 self_process +1");
        Check.Equal(0, (long)selfStats.UdpRedirected, "U1: self-PID 出站没有被 redirect");
        CheckUdpInvariant(default, selfStats, "U1 self-PID");
        Check.True(context.Device.GetEvents(8).Any(e => e.Decision == Protocol.DecisionPermittedSelfProcess &&
                                                        e.Protocol == 17),
                   "U1: self-PID 事件 decision=permitted_self_process protocol=17", "");

    }

    private static async Task U2(SuiteContext context, int datagrams)
    {
        Console.WriteLine($"[udp-rules] ===== U2 connected redirect（{datagrams} 条同目的地）=====");
        int port = FreeUdpPort();
        context.SetBlob(new[] { TestRule.RedirectPort(port, null, Protocol.RuleProtocolUdp) });
        ProbeStats before = context.Begin();

        using UdpClientProcess client = context.StartClient(
            "--remote", $"127.0.0.1:{port}", "--mode", "connected", "--payload", "u2",
            "--count", datagrams.ToString(CultureInfo.InvariantCulture), "--timeout-ms", "2000");
        await client.WaitForReadyAsync(context.TimeoutMs);
        ReceivedDatagram first = context.Listener.Receive(context.TimeoutMs);
        ProbeStats redirected = context.Device.GetStats();

        Check.Equal(1, (long)(redirected.UdpClassifyCalls - before.UdpClassifyCalls),
                    $"U2: connected {datagrams} 条 datagram 只 classify 一次");
        Check.Equal(1, (long)(redirected.UdpRedirected - before.UdpRedirected), "U2: udp_redirected +1");
        Check.Equal(1, (long)(redirected.ContextsAllocated - before.ContextsAllocated), "U2: contexts_allocated +1");
        Check.Equal(0, (long)(redirected.UdpPermitted - before.UdpPermitted), "U2: 回包前 udp_permitted 不变");
        CheckUdpInvariant(before, redirected, "U2 redirect");

        RedirectContext redirect = ParseContext(first, "U2");
        Check.Equal(4, redirect.Family, "U2: connected v4 context family=4");
        Check.Equal(17, redirect.Protocol, "U2: context protocol=17");
        Check.Equal(port, redirect.OriginalPort, "U2: context 原始端口");
        Check.Equal(client.Pid, (long)redirect.ProcessId, "U2: context PID 是客户端子进程");
        Check.Equal(0, redirect.RuleIndex, "U2: context rule=0");
        EqualText("127.0.0.1", ContextAddress(redirect), "U2: context 原始地址");
        EqualText("u2", Encoding.UTF8.GetString(first.Payload), "U2: listener 收到的 payload");

        string firstContext = Convert.ToHexString(first.RedirectContext ?? Array.Empty<byte>());
        context.Listener.Reply(first);
        for (int index = 1; index < datagrams; index++)
        {
            ReceivedDatagram next = context.Listener.Receive(context.TimeoutMs);
            EqualText("u2", Encoding.UTF8.GetString(next.Payload), "U2: listener 收到的后续 payload");
            EqualText(firstContext, Convert.ToHexString(next.RedirectContext ?? Array.Empty<byte>()),
                      "U2: connected 同一关联 context 完全相同");
            context.Listener.Reply(next);
        }

        (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, exitCode, "U2: 客户端退出码");
        Check.Equal(datagrams, CountOccurrences(output, "recv=2B"), "U2: connected 客户端收到全部回包");

        ProbeStats replied = context.Device.GetStats();
        Check.Equal(1, (long)(replied.UdpClassifyCalls - before.UdpClassifyCalls),
                    "U2: listener 回包不重新 classify");
        Check.Equal(0, (long)(replied.UdpPermitted - before.UdpPermitted), "U2: 回包不增加 udp_permitted");
        CheckUdpInvariant(before, replied, "U2 回包后");
    }


    private static async Task U3(SuiteContext context, int datagrams)
    {
        Console.WriteLine($"[udp-rules] ===== U3 同一 socket/目的地 {datagrams} 条 datagram =====");
        int port = FreeUdpPort();
        context.SetBlob(new[] { TestRule.RedirectPort(port, null, Protocol.RuleProtocolUdp) });
        ProbeStats before = context.Begin();

        using UdpClientProcess client = context.StartClient(
            "--remote", $"127.0.0.1:{port}", "--mode", "unconnected", "--payload", "u3",
            "--count", datagrams.ToString(CultureInfo.InvariantCulture), "--timeout-ms", "2000");
        await client.WaitForReadyAsync(context.TimeoutMs);
        var received = new List<ReceivedDatagram>();
        ReceivedDatagram firstDatagram = context.Listener.Receive(context.TimeoutMs);
        received.Add(firstDatagram);

        ProbeStats redirected = context.Device.GetStats();
        CheckUdpInvariant(before, redirected, "U3 redirect");
        Check.Equal(1, (long)(redirected.UdpClassifyCalls - before.UdpClassifyCalls),
                    $"U3: {datagrams} 条 datagram 只 classify 一次（按关联）");
        Check.Equal(1, (long)(redirected.UdpRedirected - before.UdpRedirected), "U3: udp_redirected 只 +1");
        Check.Equal(1, (long)(redirected.ContextsAllocated - before.ContextsAllocated), "U3: contexts_allocated 只 +1");

        context.Listener.Reply(firstDatagram);
        for (int index = 1; index < datagrams; index++)
        {
            ReceivedDatagram next = context.Listener.Receive(context.TimeoutMs);
            received.Add(next);
            context.Listener.Reply(next);
        }

        Check.Equal(datagrams, received.Count, "U3: listener 收到全部 datagram");
        string firstContext = Convert.ToHexString(received[0].RedirectContext ?? Array.Empty<byte>());
        Check.True(received.All(item => Convert.ToHexString(item.RedirectContext ?? Array.Empty<byte>()) == firstContext),
                   "U3: 同一关联的 context 完全相同", $"datagrams={datagrams}");

        (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, exitCode, "U3: 客户端退出码");
        Check.Equal(datagrams, CountOccurrences(output, "recv=2B"), "U3: 客户端收到全部回包");

        ProbeStats replied = context.Device.GetStats();
        CheckUdpInvariant(before, replied, "U3 回包后");
        Check.Equal(1, (long)(replied.UdpClassifyCalls - before.UdpClassifyCalls),
                    "U3: 回包不重新 classify");
        Check.Equal(0, (long)(replied.UdpPermitted - before.UdpPermitted), "U3: 回包不增加 udp_permitted");
        Check.Equal(0, (long)(replied.UdpBlocked - before.UdpBlocked), "U3: udp_blocked 不变");
    }


    private static async Task U4(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U4 同一 socket 两个目的地 =====");
        int firstPort = FreeUdpPort();
        int secondPort = FreeUdpPort();
        context.SetBlob(new[]
        {
            TestRule.RedirectPort(firstPort, null, Protocol.RuleProtocolUdp),
            TestRule.RedirectPort(secondPort, null, Protocol.RuleProtocolUdp),
        });
        ProbeStats before = context.Begin();

        using UdpClientProcess client = context.StartInteractive(
            "--remote", $"127.0.0.1:{firstPort}", "--mode", "unconnected", "--timeout-ms", "2000");
        await client.WaitForReadyAsync(context.TimeoutMs);

        client.SendCommand($"sendto 127.0.0.1:{firstPort} u4-a");
        ReceivedDatagram firstDatagram = context.Listener.Receive(context.TimeoutMs);
        context.Listener.Reply(firstDatagram);
        await client.WaitForLineAsync("send#0", context.TimeoutMs);

        client.SendCommand($"sendto 127.0.0.1:{secondPort} u4-b");
        ReceivedDatagram secondDatagram = context.Listener.Receive(context.TimeoutMs);
        context.Listener.Reply(secondDatagram);
        await client.WaitForLineAsync("send#1", context.TimeoutMs);

        ProbeStats redirected = context.Device.GetStats();
        CheckUdpInvariant(before, redirected, "U4");
        Check.Equal(2, (long)(redirected.UdpClassifyCalls - before.UdpClassifyCalls), "U4: 两个目的地 classify 两次");
        Check.Equal(2, (long)(redirected.UdpRedirected - before.UdpRedirected), "U4: udp_redirected +2");
        Check.Equal(2, (long)(redirected.ContextsAllocated - before.ContextsAllocated), "U4: contexts_allocated +2");

        RedirectContext first = ParseContext(firstDatagram, "U4-a");
        RedirectContext second = ParseContext(secondDatagram, "U4-b");
        Check.Equal(firstPort, first.OriginalPort, "U4: 第一条 context 原始端口");
        Check.Equal(secondPort, second.OriginalPort, "U4: 第二条 context 原始端口");
        Check.True(first.OriginalPort != second.OriginalPort, "U4: 两条 context 不串", "");

        client.SendCommand("quit");
        (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, exitCode, "U4: 客户端退出码");
        Check.Equal(2, CountOccurrences(output, "recv=4B"), "U4: 客户端收到两条回包");

    }

    private static async Task U5(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U5 permit =====");
        using var sink = new UdpPeer();
        context.SetBlob(new[] { TestRule.PermitPort(sink.Port, null, Protocol.RuleProtocolUdp) });
        ProbeStats before = context.Begin();

        using UdpClientProcess client = context.StartClient(
            "--remote", $"127.0.0.1:{sink.Port}", "--mode", "unconnected", "--payload", "u5",
            "--count", "1", "--timeout-ms", "400");
        await client.WaitForReadyAsync(context.TimeoutMs);

        Check.True(sink.TryReceive(context.TimeoutMs, out PeerDatagram datagram), "U5: 目标端口收到 datagram", "");
        EqualText("u5", Encoding.UTF8.GetString(datagram.Payload), "U5: permit payload");
        ProbeStats after = context.Device.GetStats();
        CheckUdpInvariant(before, after, "U5");
        Check.True(!context.Listener.TryReceive(300, out _), "U5: listener 没有收到包", "permit 直连");

        (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, exitCode, "U5: 客户端退出码");
        Check.Contains(output, "recv-failed", "U5: 目标端口不回包，客户端超时");

        Check.Equal(1, (long)(after.UdpClassifyCalls - before.UdpClassifyCalls), "U5: udp_classify_calls +1");
        Check.Equal(1, (long)(after.UdpPermitted - before.UdpPermitted), "U5: udp_permitted +1");
        Check.Equal(0, (long)(after.UdpRedirected - before.UdpRedirected), "U5: 没有 redirect");
        Check.Equal(0, (long)(after.ContextsAllocated - before.ContextsAllocated), "U5: 没有 context 分配");
        Check.Equal(1, (long)(after.MatchedPermit - before.MatchedPermit), "U5: matched_permit +1");
        Check.True(context.Device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionPermittedRule &&
                                                         e.Protocol == 17 && e.MatchedRule == 0),
                   "U5: 事件 decision=permitted_rule protocol=17", "");
    }

    private static async Task U6(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U6 UDP block =====");
        using var blockedSink = new UdpPeer();
        using var controlSink = new UdpPeer();
        long sinkBefore = context.BlockSink.Received;
        context.SetBlob(new[] { TestRule.BlockPort(blockedSink.Port, null, Protocol.RuleProtocolUdp) });
        ProbeStats before = context.Begin();

        using UdpClientProcess blocked = context.StartClient(
            "--remote", $"127.0.0.1:{blockedSink.Port}", "--mode", "unconnected", "--payload", "u6-block",
            "--count", "1", "--timeout-ms", "400");
        await blocked.WaitForReadyAsync(context.TimeoutMs);
        (int blockedExit, string blockedOutput) = await blocked.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, blockedExit, "U6: block 下客户端 send 成功（退出码 0）");
        Check.Contains(blockedOutput, "sent=8B", "U6: send 返回成功");
        Check.Contains(blockedOutput, "recv-failed", "U6: 应用侧 recv 超时（静默丢包）");
        Check.True(!blockedSink.TryReceive(300, out _), "U6: 被 block 的端口没有收到 datagram", "auth 层丢弃");
        Check.True(!context.Listener.TryReceive(200, out _), "U6: listener 没有收到包", "");
        Check.Equal(sinkBefore, context.BlockSink.Received, "U6: 固定 auth filter 命中（sink 没有收到 datagram）");

        ProbeStats afterBlock = context.Device.GetStats();
        CheckUdpInvariant(before, afterBlock, "U6 block");
        Check.Equal(1, (long)(afterBlock.UdpClassifyCalls - before.UdpClassifyCalls), "U6: udp_classify_calls +1");
        Check.Equal(1, (long)(afterBlock.UdpBlocked - before.UdpBlocked), "U6: udp_blocked +1");
        Check.Equal(0, (long)(afterBlock.UdpPermitted - before.UdpPermitted), "U6: udp_permitted 不变");
        Check.Equal(0, (long)(afterBlock.UdpRedirected - before.UdpRedirected), "U6: 没有 redirect");
        Check.Equal(1, (long)(afterBlock.DeferredBlocks - before.DeferredBlocks), "U6: deferred_blocks +1");
        Check.Equal(1, (long)(afterBlock.MatchedBlock - before.MatchedBlock), "U6: matched_block +1");
        Check.True(context.Device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionBlockedRule &&
                                                         e.Protocol == 17 && e.Action == Protocol.ActionBlock),
                   "U6: 事件 decision=blocked_rule protocol=17 action=block", "");

        using UdpClientProcess control = context.StartClient(
            "--remote", $"127.0.0.1:{controlSink.Port}", "--mode", "unconnected", "--payload", "u6-allow",
            "--count", "1", "--timeout-ms", "400");
        await control.WaitForReadyAsync(context.TimeoutMs);
        Check.True(controlSink.TryReceive(context.TimeoutMs, out PeerDatagram allowed), "U6: 未 block 端口可达", "");
        EqualText("u6-allow", Encoding.UTF8.GetString(allowed.Payload), "U6: 未 block 端口 payload");
        ProbeStats afterControl = context.Device.GetStats();
        CheckUdpInvariant(before, afterControl, "U6 控制组");
        await control.WaitForExitAsync(context.TimeoutMs);

        Check.AtLeast(1, (long)(afterControl.UdpPermitted - before.UdpPermitted),
                      "U6: 未 block 端口走 permit");
        ProbeEvent[] controlEvents = context.Device.GetEvents(64);
        Check.True(controlEvents.Any(e => e.Decision == Protocol.DecisionPermittedFallback &&
                                          e.Protocol == 17 && e.RemotePort == controlSink.Port &&
                                          e.ProcessId == (uint)control.Pid),
                   "U6: 控制组 datagram 记录为 permitted_fallback",
                   $"port={controlSink.Port} pid={control.Pid}");
        // fallback_permit 是跨协议全局计数，后台 TCP 也会 +1；UDP 控制组决策已由上面的 (pid, port) 事件断言。
        Check.AtLeast(1, (long)(afterControl.FallbackPermit - before.FallbackPermit), "U6: fallback_permit 至少 +1");

        Check.AtLeast(2, (long)(afterControl.UdpClassifyCalls - before.UdpClassifyCalls),
                      "U6: block 与控制组至少各 classify 一次（背景 UDP 关联可能增加 calls）");
    }

    private static async Task U7(SuiteContext context, string pingHost)
    {
        Console.WriteLine("[udp-rules] ===== U7 any block + TCP 对照 + ICMP =====");
        using var blockedSink = new UdpPeer();
        using var blockedTcp = new EchoServer(IPAddress.Loopback, blockedSink.Port);
        using var controlTcp = new EchoServer(IPAddress.Loopback);
        using var controlSink = new UdpPeer();
        long sinkBefore = context.BlockSink.Received;

        context.SetBlob(new[] { TestRule.BlockPort(blockedSink.Port, null, Protocol.RuleProtocolAny) });
        ProbeStats before = context.Begin();

        using UdpClientProcess udp = context.StartClient(
            "--remote", $"127.0.0.1:{blockedSink.Port}", "--mode", "unconnected", "--payload", "u7-udp",
            "--count", "1", "--timeout-ms", "400");
        await udp.WaitForReadyAsync(context.TimeoutMs);
        (int udpExit, string udpOutput) = await udp.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, udpExit, "U7: any block 下 UDP send 成功");
        Check.Contains(udpOutput, "sent=6B", "U7: UDP send 返回成功");
        Check.Contains(udpOutput, "recv-failed", "U7: UDP 应用侧超时");
        Check.True(!blockedSink.TryReceive(300, out _), "U7: any block 覆盖 UDP", "对端收不到");
        Check.Equal(sinkBefore, context.BlockSink.Received,
                    "U7: any block 的固定 auth filter 命中（sink 没有收到 datagram）");

        (bool tcpConnected, SocketError tcpError, string tcpDetail) =
            TryTcpConnectAsNonSelf(context, new IPEndPoint(IPAddress.Loopback, blockedSink.Port), context.TimeoutMs);
        Check.True(!tcpConnected, "U7: any block 覆盖 TCP（连接阶段失败）", tcpDetail);
        Console.WriteLine($"[udp-rules] U7 any-block tcp: error={tcpError} detail={tcpDetail}");

        bool controlConnected = SocketHelpers.TryConnect(new IPEndPoint(IPAddress.Loopback, controlTcp.Port),
                                                         context.TimeoutMs, out Socket? controlSocket, out string controlError);
        bool controlEcho = controlConnected &&
                           SocketHelpers.TryEcho(controlSocket!, "u7-tcp", context.TimeoutMs, out _);
        Check.True(controlEcho, "U7: 未被 any block 命中的 TCP 正常", controlError);
        controlSocket?.Dispose();

        using UdpClientProcess controlUdp = context.StartClient(
            "--remote", $"127.0.0.1:{controlSink.Port}", "--mode", "unconnected", "--payload", "u7-ok",
            "--count", "1", "--timeout-ms", "400");
        await controlUdp.WaitForReadyAsync(context.TimeoutMs);
        Check.True(controlSink.TryReceive(context.TimeoutMs, out PeerDatagram controlDatagram),
                   "U7: 未被 any block 命中的 UDP 正常", "");
        EqualText("u7-ok", Encoding.UTF8.GetString(controlDatagram.Payload), "U7: UDP 控制组 payload");
        ProbeStats afterBlock = context.Device.GetStats();
        CheckUdpInvariant(before, afterBlock, "U7 block/控制组");
        await controlUdp.WaitForExitAsync(context.TimeoutMs);

        Check.Equal(1, (long)(afterBlock.UdpBlocked - before.UdpBlocked), "U7: udp_blocked +1");
        Check.AtLeast(2, (long)(afterBlock.DeferredBlocks - before.DeferredBlocks),
                      "U7: UDP 关联与 TCP 对照各改道一次（deferred_blocks +2）");
        Check.AtLeast(1, (long)(afterBlock.UdpPermitted - before.UdpPermitted),
                      "U7: 控制组 UDP permit 至少 +1（背景 UDP 关联可能增加）");
        Check.True(context.Device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionPermittedFallback &&
                                                         e.Protocol == 17 &&
                                                         e.ProcessId == (uint)controlUdp.Pid &&
                                                         e.RemotePort == controlSink.Port),
                   "U7: 控制组 datagram 记录为 permitted_fallback",
                   $"port={controlSink.Port} pid={controlUdp.Pid}");
        // fallback_permit 是跨协议全局计数，后台 TCP 也会 +1；UDP 决策已由事件精确断言。
        Check.AtLeast(1, (long)(afterBlock.FallbackPermit - before.FallbackPermit), "U7: fallback_permit 至少 +1");
        Check.AtLeast(2, (long)(afterBlock.UdpClassifyCalls - before.UdpClassifyCalls),
                      "U7: block 与控制组至少各 classify 一次（背景 UDP 关联可能增加 calls）");


        CheckPing($"U7: any block（端口规则）下 ICMP ping 仍通（{pingHost}）", pingHost);
        ProbeStats afterPing = context.Device.GetStats();
        CheckUdpInvariant(afterBlock, afterPing, "U7 ICMP 后");
        Check.Equal(0, (long)(afterPing.UdpClassifyCalls - afterBlock.UdpClassifyCalls),
                    "U7: ICMP 不进入 UDP classify");


        // 全端口 any block：规则覆盖 1..65535 的所有 TCP/UDP，但 auth filter 仍只写协议 6/17；
        // ICMP 必须继续通，同时 TCP/UDP 都必须被拦。
        context.SetBlob(new[]
        {
            new TestRule
            {
                Action = Protocol.RuleActionBlock,
                PortStart = 0,
                PortEnd = 0xFFFF,
                TransportProtocol = Protocol.RuleProtocolAny,
            },
        });
        CheckPing($"U7: 全端口 any block 下 ICMP ping 仍通（{pingHost}）", pingHost);

        using (UdpClientProcess fullUdp = context.StartClient(
                   "--remote", $"127.0.0.1:{controlSink.Port}", "--mode", "unconnected", "--payload", "u7-full",
                   "--count", "1", "--timeout-ms", "400"))
        {
            await fullUdp.WaitForReadyAsync(context.TimeoutMs);
            (int fullExit, string fullOutput) = await fullUdp.WaitForExitAsync(context.TimeoutMs);
            Check.Equal(0, fullExit, "U7: 全端口 any block 下 UDP send 成功");
            Check.Contains(fullOutput, "recv-failed", "U7: 全端口 any block 下 UDP 应用侧超时");
            Check.True(!controlSink.TryReceive(300, out _), "U7: 全端口 any block 覆盖 UDP", "对端收不到");
            Check.Equal(sinkBefore, context.BlockSink.Received,
                        "U7: 全端口 any block 的固定 auth filter 命中（sink 没有收到 datagram）");
        }

        (bool fullTcp, SocketError fullTcpError, string fullTcpDetail) =
            TryTcpConnectAsNonSelf(context, new IPEndPoint(IPAddress.Loopback, controlTcp.Port), context.TimeoutMs);
        Check.True(!fullTcp, "U7: 全端口 any block 覆盖 TCP（连接阶段失败）", fullTcpDetail);
        Console.WriteLine($"[udp-rules] U7 full-block tcp: error={fullTcpError} detail={fullTcpDetail}");

        // fallback=block：固定 auth filter 只写协议 6/17，ICMP 不被误拦。
        context.SetBlob(Array.Empty<TestRule>(), Protocol.RuleActionBlock);
        CheckPing($"U7: fallback block 下 ICMP ping 仍通（{pingHost}）", pingHost);
        context.SetBlob(Array.Empty<TestRule>(), Protocol.RuleActionPermit);

    }

    private static async Task U8(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U8 进程选择器 =====");
        int matchedPort = FreeUdpPort();
        using var otherSink = new UdpPeer();
        context.SetBlob(new[]
        {
            TestRule.RedirectPort(matchedPort, context.AppId, Protocol.RuleProtocolUdp),
            TestRule.RedirectPort(otherSink.Port, OtherAppId, Protocol.RuleProtocolUdp),
        });
        ProbeStats before = context.Begin();

        using UdpClientProcess matched = context.StartClient(
            "--remote", $"127.0.0.1:{matchedPort}", "--mode", "unconnected", "--payload", "u8-match",
            "--count", "1", "--timeout-ms", "2000");
        await matched.WaitForReadyAsync(context.TimeoutMs);
        ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
        RedirectContext redirect = ParseContext(datagram, "U8-match");
        Check.True(context.AppId.Contains("wfprobe", StringComparison.OrdinalIgnoreCase),
                   "U8: 当前进程串是 wfprobe 的 ALE_APP_ID", context.AppId);
        Check.Equal(0, redirect.RuleIndex, "U8: 命中第一条（进程串匹配）规则");
        Check.Equal(matched.Pid, (long)redirect.ProcessId, "U8: 匹配规则 context PID");

        ProbeStats afterMatched = context.Device.GetStats();
        CheckUdpInvariant(before, afterMatched, "U8 matched");
        Check.Equal(1, (long)(afterMatched.MatchedRedirect - before.MatchedRedirect), "U8: matched_redirect +1");
        Console.WriteLine($"[udp-rules] U8 matched: redirect+{afterMatched.MatchedRedirect - before.MatchedRedirect} " +
                          $"fallback+{afterMatched.FallbackPermit - before.FallbackPermit} " +
                          $"calls+{afterMatched.UdpClassifyCalls - before.UdpClassifyCalls} " +
                          $"events={DescribeEvents(context.Device.GetEvents(32))}");
        context.Listener.Reply(datagram);
        await matched.WaitForExitAsync(context.TimeoutMs);
        ProbeStats afterReply = context.Device.GetStats();
        CheckUdpInvariant(afterMatched, afterReply, "U8 回包后");
        Console.WriteLine($"[udp-rules] U8 reply: fallback+{afterReply.FallbackPermit - afterMatched.FallbackPermit} " +
                          $"self+{afterReply.PermittedSelfProcess - afterMatched.PermittedSelfProcess} " +
                          $"calls+{afterReply.UdpClassifyCalls - afterMatched.UdpClassifyCalls} " +
                          $"events={DescribeEvents(context.Device.GetEvents(32))}");

        using UdpClientProcess other = context.StartClient(
            "--remote", $"127.0.0.1:{otherSink.Port}", "--mode", "unconnected", "--payload", "u8-other",
            "--count", "1", "--timeout-ms", "400");
        await other.WaitForReadyAsync(context.TimeoutMs);
        Check.True(otherSink.TryReceive(context.TimeoutMs, out PeerDatagram otherDatagram),
                   "U8: 进程串不匹配时按 fallback permit 直连", "");
        EqualText("u8-other", Encoding.UTF8.GetString(otherDatagram.Payload), "U8: fallback 直连 payload");
        ProbeStats afterOther = context.Device.GetStats();
        CheckUdpInvariant(afterReply, afterOther, "U8 控制组");
        ProbeEvent[] otherEvents = context.Device.GetEvents(128);
        Check.True(otherEvents.Any(e => e.Decision == Protocol.DecisionPermittedFallback &&
                                        e.Protocol == 17 && e.RemotePort == otherSink.Port &&
                                        e.ProcessId == (uint)other.Pid),
                   "U8: 不匹配进程串的 datagram 记录为 permitted_fallback",
                   $"port={otherSink.Port} pid={other.Pid}");
        // fallback_permit 是跨协议全局计数，后台 TCP 也会 +1；UDP 决策已由上面的 permitted_fallback 事件精确断言。
        Check.AtLeast(1, (long)(afterOther.FallbackPermit - afterMatched.FallbackPermit),
                      "U8: fallback_permit 至少 +1");

        Check.True(!context.Listener.TryReceive(200, out _), "U8: 不匹配的规则没有触发 redirect", "");
        await other.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, (long)(afterOther.MatchedRedirect - afterMatched.MatchedRedirect),
                    "U8: 不匹配的规则 matched_redirect 不变");
        Console.WriteLine($"[udp-rules] U8 other: fallback+{afterOther.FallbackPermit - afterReply.FallbackPermit} " +
                          $"self+{afterOther.PermittedSelfProcess - afterReply.PermittedSelfProcess} " +
                          $"calls+{afterOther.UdpClassifyCalls - afterReply.UdpClassifyCalls} " +
                          $"events={DescribeEvents(otherEvents)}");

    }

    private static async Task U9(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U9 fallback redirect / permit =====");
        int unmatchedPort = FreeUdpPort();
        int rulePort = FreeUdpPort();
        context.SetBlob(new[] { TestRule.RedirectPort(rulePort, null, Protocol.RuleProtocolUdp) },
                        Protocol.RuleActionRedirect);
        ProbeStats before = context.Begin();

        using UdpClientProcess fallbackRedirect = context.StartClient(
            "--remote", $"127.0.0.1:{unmatchedPort}", "--mode", "unconnected", "--payload", "u9-redirect",
            "--count", "1", "--timeout-ms", "2000");
        await fallbackRedirect.WaitForReadyAsync(context.TimeoutMs);
        ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
        RedirectContext redirect = ParseContext(datagram, "U9-redirect");
        Check.Equal(Protocol.InvalidRuleIndex, redirect.RuleIndex, "U9: fallback redirect rule=0xFFFFFFFF");
        Check.True(context.Device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionRedirected &&
                                                         e.Protocol == 17 &&
                                                         e.RemotePort == unmatchedPort &&
                                                         e.MatchedRule == Protocol.InvalidRuleIndex),
                   "U9: fallback redirect 事件 protocol=17 rule=0xFFFFFFFF",
                   $"port={unmatchedPort}");
        ProbeStats afterRedirect = context.Device.GetStats();
        CheckUdpInvariant(before, afterRedirect, "U9 fallback redirect");
        context.Listener.Reply(datagram);
        await fallbackRedirect.WaitForExitAsync(context.TimeoutMs);

        // 计数器是全局的：后台 UDP 只要落在窗口里也会 +1，所以这里只保证至少 1 次，
        // 具体归属由上面的 (protocol, remote_port, rule=0xFFFFFFFF) 事件断言。
        Check.AtLeast(1, (long)(afterRedirect.FallbackRedirect - before.FallbackRedirect), "U9: fallback_redirect +1");
        Check.AtLeast(1, (long)(afterRedirect.UdpRedirected - before.UdpRedirected), "U9: udp_redirected +1");

        using var sink = new UdpPeer();
        context.SetBlob(new[] { TestRule.RedirectPort(rulePort, null, Protocol.RuleProtocolUdp) },
                        Protocol.RuleActionPermit);
        ProbeStats beforePermit = context.Begin();
        using UdpClientProcess fallbackPermit = context.StartClient(
            "--remote", $"127.0.0.1:{sink.Port}", "--mode", "unconnected", "--payload", "u9-permit",
            "--count", "1", "--timeout-ms", "400");
        await fallbackPermit.WaitForReadyAsync(context.TimeoutMs);
        Check.True(sink.TryReceive(context.TimeoutMs, out PeerDatagram permitted), "U9: fallback permit 直连可达", "");
        EqualText("u9-permit", Encoding.UTF8.GetString(permitted.Payload), "U9: fallback permit payload");
        ProbeStats afterPermit = context.Device.GetStats();
        CheckUdpInvariant(beforePermit, afterPermit, "U9 fallback permit");
        Check.True(!context.Listener.TryReceive(200, out _), "U9: fallback permit 不 redirect", "");
        await fallbackPermit.WaitForExitAsync(context.TimeoutMs);

        Check.True(context.Device.GetEvents(64).Any(e => e.Decision == Protocol.DecisionPermittedFallback &&
                                                         e.Protocol == 17 &&
                                                         e.ProcessId == (uint)fallbackPermit.Pid &&
                                                         e.RemotePort == sink.Port),
                   "U9: fallback permit datagram 记录为 permitted_fallback",
                   $"port={sink.Port} pid={fallbackPermit.Pid}");
        // fallback_permit 是跨协议全局计数，后台 TCP 也会 +1；UDP 决策已由事件与 udp_permitted 精确断言。
        Check.AtLeast(1, (long)(afterPermit.FallbackPermit - beforePermit.FallbackPermit),
                      "U9: fallback_permit 至少 +1");
        Check.AtLeast(1, (long)(afterPermit.UdpPermitted - beforePermit.UdpPermitted), "U9: udp_permitted +1");
    }

    private static bool IsPermittedDecision(byte decision) =>
        decision is >= Protocol.DecisionPermittedDisabled and <= Protocol.DecisionPermittedRule ||
        decision == Protocol.DecisionPermittedDeferredBlock;

    private static async Task U10(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U10 IPv6 / dual-mode =====");
        int mappedUnconnectedPort = FreeUdpPort();
        int mappedConnectedPort = FreeUdpPort();
        int nativeUnconnectedPort = FreeUdpPort();
        int nativeConnectedPort = FreeUdpPort();
        context.SetBlob(new[]
        {
            TestRule.RedirectPort(mappedUnconnectedPort, null, Protocol.RuleProtocolUdp),
            TestRule.RedirectPort(mappedConnectedPort, null, Protocol.RuleProtocolUdp),
            TestRule.RedirectPort(nativeUnconnectedPort, null, Protocol.RuleProtocolUdp),
            TestRule.RedirectPort(nativeConnectedPort, null, Protocol.RuleProtocolUdp),
        });

        ProbeStats beforeMappedUnconnected = context.Begin();
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"[::ffff:127.0.0.1]:{mappedUnconnectedPort}", "--mode", "unconnected",
                   "--payload", "u10a", "--count", "1", "--timeout-ms", "2000"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
            RedirectContext redirect = ParseContext(datagram, "U10 v4-mapped unconnected");
            Check.Equal(4, redirect.Family, "U10: v4-mapped unconnected family=4");
            Check.Equal(mappedUnconnectedPort, redirect.OriginalPort, "U10: v4-mapped unconnected 原始端口");
            EqualText("127.0.0.1", ContextAddress(redirect), "U10: v4-mapped unconnected 原始地址是纯 v4");
            Check.Equal(client.Pid, (long)redirect.ProcessId, "U10: v4-mapped unconnected context PID");
            context.Listener.Reply(datagram);
            (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
            Check.Equal(0, exitCode, "U10: v4-mapped unconnected 客户端退出码");
            Check.Contains(output, "recv=4B", "U10: v4-mapped unconnected 收到回包");
        }
        ProbeStats afterMappedUnconnected = context.Device.GetStats();
        CheckUdpInvariant(beforeMappedUnconnected, afterMappedUnconnected, "U10 v4-mapped unconnected");
        Check.Equal(1, (long)(afterMappedUnconnected.UdpRedirected - beforeMappedUnconnected.UdpRedirected),
                    "U10: v4-mapped unconnected redirect +1");

        ProbeStats beforeMappedConnected = context.Begin();
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"[::ffff:127.0.0.1]:{mappedConnectedPort}", "--mode", "connected",
                   "--payload", "u10b", "--count", "1", "--timeout-ms", "300", "--recv", "0"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
            RedirectContext redirect = ParseContext(datagram, "U10 v4-mapped connected");
            Check.Equal(4, redirect.Family, "U10: v4-mapped connected family=4");
            EqualText("127.0.0.1", ContextAddress(redirect), "U10: v4-mapped connected 原始地址是纯 v4");
            await client.WaitForExitAsync(context.TimeoutMs);
        }
        ProbeStats afterMappedConnected = context.Device.GetStats();
        CheckUdpInvariant(beforeMappedConnected, afterMappedConnected, "U10 v4-mapped connected");
        Check.Equal(1, (long)(afterMappedConnected.UdpRedirected - beforeMappedConnected.UdpRedirected),
                    "U10: v4-mapped connected redirect +1");

        ProbeStats beforeNativeUnconnected = context.Begin();
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"[::1]:{nativeUnconnectedPort}", "--mode", "unconnected",
                   "--payload", "u10c", "--count", "1", "--timeout-ms", "2000"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
            RedirectContext redirect = ParseContext(datagram, "U10 native v6 unconnected");
            Check.Equal(6, redirect.Family, "U10: native v6 unconnected family=6");
            Check.Equal(nativeUnconnectedPort, redirect.OriginalPort, "U10: native v6 unconnected 原始端口");
            EqualText("::1", ContextAddress(redirect), "U10: native v6 unconnected 原始地址");
            context.Listener.Reply(datagram);
            (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
            Check.Equal(0, exitCode, "U10: native v6 unconnected 客户端退出码");
            Check.Contains(output, "recv=4B", "U10: native v6 unconnected 收到回包");
        }
        ProbeStats afterNativeUnconnected = context.Device.GetStats();
        CheckUdpInvariant(beforeNativeUnconnected, afterNativeUnconnected, "U10 native v6 unconnected");
        Check.Equal(1, (long)(afterNativeUnconnected.UdpRedirected - beforeNativeUnconnected.UdpRedirected),
                    "U10: native v6 unconnected redirect +1");

        ProbeStats beforeNativeConnected = context.Begin();
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"[::1]:{nativeConnectedPort}", "--mode", "connected",
                   "--payload", "u10d", "--count", "1", "--timeout-ms", "300", "--recv", "0"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
            RedirectContext redirect = ParseContext(datagram, "U10 native v6 connected");
            Check.Equal(6, redirect.Family, "U10: native v6 connected family=6");
            EqualText("::1", ContextAddress(redirect), "U10: native v6 connected 原始地址");
            await client.WaitForExitAsync(context.TimeoutMs);
        }
        ProbeStats afterNativeConnected = context.Device.GetStats();
        CheckUdpInvariant(beforeNativeConnected, afterNativeConnected, "U10 native v6 connected");
        Check.Equal(1, (long)(afterNativeConnected.UdpRedirected - beforeNativeConnected.UdpRedirected),
                    "U10: native v6 connected redirect +1");
    }

    private static async Task U11(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U11 enabled 0↔1 =====");
        using var sink = new UdpPeer();
        int port = sink.Port;
        context.SetBlob(new[] { TestRule.RedirectPort(port, null, Protocol.RuleProtocolUdp) });

        context.SetEnabled(false);
        ProbeStats beforeDisabled = context.Begin();
        ProbeStats disabled;
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"127.0.0.1:{port}", "--mode", "unconnected", "--payload", "u11-off",
                   "--count", "1", "--timeout-ms", "400"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            Check.True(sink.TryReceive(context.TimeoutMs, out PeerDatagram direct), "U11: enabled=0 时直连", "");
            EqualText("u11-off", Encoding.UTF8.GetString(direct.Payload), "U11: enabled=0 直连 payload");
            disabled = context.Device.GetStats();
            CheckUdpInvariant(beforeDisabled, disabled, "U11 enabled=0");
            Check.True(!context.Listener.TryReceive(200, out _), "U11: enabled=0 不 redirect", "");
            await client.WaitForExitAsync(context.TimeoutMs);
        }

        Check.AtLeast(1, (long)(disabled.UdpClassifyCalls - beforeDisabled.UdpClassifyCalls),
                      "U11: enabled=0 仍 classify 并计数");
        Check.AtLeast(1, (long)(disabled.UdpPermitted - beforeDisabled.UdpPermitted), "U11: enabled=0 记 permitted");
        Check.Equal(0, (long)(disabled.UdpRedirected - beforeDisabled.UdpRedirected), "U11: enabled=0 无 redirect");
        Check.Equal(0, (long)(disabled.ContextsAllocated - beforeDisabled.ContextsAllocated), "U11: enabled=0 无 context");

        context.SetEnabled(true);
        ProbeStats beforeEnabled = context.Begin();
        ProbeStats enabled;
        int enabledClientPid = 0;
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"127.0.0.1:{port}", "--mode", "unconnected", "--payload", "u11-on",
                   "--count", "1", "--timeout-ms", "2000"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            enabledClientPid = client.Pid;
            ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
            EqualText("u11-on", Encoding.UTF8.GetString(datagram.Payload), "U11: enabled=1 恢复 redirect");
            Check.True(!sink.TryReceive(200, out _), "U11: enabled=1 时没有直连包", "");
            enabled = context.Device.GetStats();
            CheckUdpInvariant(beforeEnabled, enabled, "U11 enabled=1");
            context.Listener.Reply(datagram);
            (int exitCode, string output) = await client.WaitForExitAsync(context.TimeoutMs);
            Check.Equal(0, exitCode, "U11: enabled=1 客户端退出码");
            Check.Contains(output, "recv=6B", "U11: enabled=1 收到回包");
        }

        Check.AtLeast(1, (long)(enabled.UdpRedirected - beforeEnabled.UdpRedirected), "U11: enabled=1 redirect +1");
        // 先证明事件窗口里确实有这一条 redirected 事件，否则下面的 !Any 会因为“根本没有事件”而静默通过。
        ProbeEvent[] enabledEvents = context.Device.GetEvents(64);
        Check.True(enabledEvents.Any(e => e.Decision == Protocol.DecisionRedirected &&
                                          e.Protocol == 17 &&
                                          e.ProcessId == (uint)enabledClientPid &&
                                          e.RemotePort == port),
                   "U11: enabled=1 存在这条连接的 redirected 事件（事件窗口有效）",
                   DescribeEvents(enabledEvents));
        // enabled=1 的窗口里只允许这条连接被 redirect；全局 udp_permitted 会被后台流量干扰，
        // 因此用 (pid, remote_port) 过滤事件，证明这个客户端没有走 permit 直连。
        Check.True(!enabledEvents.Any(e => IsPermittedDecision(e.Decision) &&
                                           e.Protocol == 17 &&
                                           e.ProcessId == (uint)enabledClientPid &&
                                           e.RemotePort == port),
                   "U11: enabled=1 首包没有走 permit（回包另计）",
                   $"port={port} pid={enabledClientPid}");

    }

    private static async Task U12(SuiteContext context, int connectedWindowMs)
    {
        Console.WriteLine("[udp-rules] ===== U12 生命周期 / WfPr 池 =====");

        // unconnected 先跑：只有干净启动（基线 outstanding==0）才严格断言关闭后归还。
        PoolTagUsage unconnectedBaseline = WaitForPool(PoolTagUsage.Zero(Protocol.PoolTag),
                                                    TimeSpan.FromMilliseconds(2000));
        int unconnectedPort = FreeUdpPort();
        context.SetBlob(new[] { TestRule.RedirectPort(unconnectedPort, null, Protocol.RuleProtocolUdp) });
        ProbeStats unconnectedBefore = context.Begin();
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"127.0.0.1:{unconnectedPort}", "--mode", "unconnected", "--payload", "u12-sock",
                   "--count", "1", "--timeout-ms", "2000"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            ReceivedDatagram datagram = context.Listener.Receive(context.TimeoutMs);
            context.Listener.Reply(datagram);
            (int exitCode, _) = await client.WaitForExitAsync(context.TimeoutMs);
            Check.Equal(0, exitCode, "U12: unconnected 客户端退出码");
        }
        ProbeStats unconnectedAfter = context.Device.GetStats();
        long unconnectedRedirects = (long)(unconnectedAfter.UdpRedirected - unconnectedBefore.UdpRedirected);
        Check.Equal(1, unconnectedRedirects, "U12: unconnected redirect +1");
        CheckUdpInvariant(unconnectedBefore, unconnectedAfter, "U12 unconnected");

        PoolTagUsage releasedPool = WaitForPool(unconnectedBaseline, TimeSpan.FromMilliseconds(2000));
        long releasedAllocs = (long)releasedPool.NonPagedAllocs - unconnectedBaseline.NonPagedAllocs;
        long releasedFrees = (long)releasedPool.NonPagedFrees - unconnectedBaseline.NonPagedFrees;
        Check.Equal(unconnectedRedirects, releasedAllocs, "U12: unconnected context 分配数等于 redirect 数");
        Console.WriteLine($"[udp-rules] U12 unconnected: baseline={unconnectedBaseline.NonPagedOutstanding} " +
                          $"allocs=+{releasedAllocs} frees=+{releasedFrees} " +
                          $"outstanding={unconnectedBaseline.NonPagedOutstanding}->{releasedPool.NonPagedOutstanding}");
        if (unconnectedBaseline.NonPagedOutstanding == 0)
        {
            Check.Equal(unconnectedRedirects, releasedFrees, "U12: unconnected context 关闭后释放");
            Check.Equal(0, releasedPool.NonPagedOutstanding, "U12: unconnected outstanding 回到 0 基线");
        }
        else
        {
            Console.WriteLine($"[udp-rules] U12 LIMIT: unconnected 基线 outstanding=" +
                              $"{unconnectedBaseline.NonPagedOutstanding}（非干净启动），不严格断言释放");
        }

        // connected：WFP 在本 boot 内不回收该 context，只记录窗口，不做任何释放断言。
        // 26100 的根因是每包 AleRedirectRecordReference 不归还；详见
        // .trellis/spec/backend/kernel/wfp-connected-udp-record-refcount-leak.md。
        PoolTagUsage connectedBaseline = Pool();
        int connectedPort = FreeUdpPort();
        context.SetBlob(new[] { TestRule.RedirectPort(connectedPort, null, Protocol.RuleProtocolUdp) });
        ProbeStats connectedBefore = context.Begin();
        using (UdpClientProcess client = context.StartClient(
                   "--remote", $"127.0.0.1:{connectedPort}", "--mode", "connected", "--payload", "u12-conn",
                   "--count", "1", "--timeout-ms", "300", "--recv", "0"))
        {
            await client.WaitForReadyAsync(context.TimeoutMs);
            context.Listener.Receive(context.TimeoutMs);
            await client.WaitForExitAsync(context.TimeoutMs);
        }
        ProbeStats connectedAfter = context.Device.GetStats();
        long connectedRedirects = (long)(connectedAfter.UdpRedirected - connectedBefore.UdpRedirected);
        Check.Equal(1, connectedRedirects, "U12: connected redirect +1");
        CheckUdpInvariant(connectedBefore, connectedAfter, "U12 connected");

        var watch = Stopwatch.StartNew();
        PoolTagUsage afterClose = connectedBaseline;
        long allocDelta = 0;
        long freeDelta = 0;
        foreach (int milestone in new[] { 1000, 10000, 60000, 180000, 300000 })
        {
            if (milestone > connectedWindowMs)
                break;
            int waitMs = (int)Math.Max(0, milestone - watch.ElapsedMilliseconds);
            afterClose = WaitForPool(connectedBaseline, TimeSpan.FromMilliseconds(waitMs));
            allocDelta = (long)afterClose.NonPagedAllocs - connectedBaseline.NonPagedAllocs;
            freeDelta = (long)afterClose.NonPagedFrees - connectedBaseline.NonPagedFrees;
            Console.WriteLine($"[udp-rules] U12 connected @{watch.ElapsedMilliseconds}ms (milestone={milestone}ms): " +
                              $"allocs=+{allocDelta} frees=+{freeDelta} " +
                              $"outstanding={connectedBaseline.NonPagedOutstanding}->{afterClose.NonPagedOutstanding}");
            if (afterClose.NonPagedOutstanding <= connectedBaseline.NonPagedOutstanding)
                break;
        }
        Check.Equal(connectedRedirects, allocDelta, "U12: connected context 分配数等于 redirect 数");
        Console.WriteLine("[udp-rules] U12 LIMIT: connected UDP context 由 WFP 持有，本 boot 不做释放断言" +
                          $"（观察窗口 {connectedWindowMs}ms）");
    }


    private static async Task U13(SuiteContext context)
    {
        Console.WriteLine("[udp-rules] ===== U13 规则热替换 =====");
        using var permitPeer = new UdpPeer();
        int port = permitPeer.Port;
        context.SetBlob(new[] { TestRule.PermitPort(port, null, Protocol.RuleProtocolUdp) });

        using UdpClientProcess client = context.StartInteractive(
            "--remote", $"127.0.0.1:{port}", "--mode", "unconnected", "--timeout-ms", "500");
        await client.WaitForReadyAsync(context.TimeoutMs);

        client.SendCommand("send hot-1");
        Check.True(permitPeer.TryReceive(context.TimeoutMs, out PeerDatagram firstDatagram), "U13: permit 阶段对端收到包", "");
        EqualText("hot-1", Encoding.UTF8.GetString(firstDatagram.Payload), "U13: permit 阶段 payload");
        permitPeer.Send(firstDatagram.Payload, firstDatagram.Source);
        string first = await client.WaitForLineAsync("send#0", context.TimeoutMs);
        Check.Contains(first, "recv=5B", "U13: 旧关联首包 permit 并收到回包");

        context.SetBlob(new[] { TestRule.BlockPort(port, null, Protocol.RuleProtocolUdp) });
        context.Device.ResetStats();
        client.SendCommand("send hot-2");
        Check.True(permitPeer.TryReceive(context.TimeoutMs, out PeerDatagram secondDatagram),
                   "U13: 已存在的 UDP 关联保持旧 permit 决策", "");
        EqualText("hot-2", Encoding.UTF8.GetString(secondDatagram.Payload), "U13: 旧关联 payload");
        permitPeer.Send(secondDatagram.Payload, secondDatagram.Source);
        string second = await client.WaitForLineAsync("send#1", context.TimeoutMs);
        Check.Contains(second, "recv=5B", "U13: 旧关联换表后仍收到回包");

        ProbeStats oldAssociation = context.Device.GetStats();
        CheckUdpInvariant(default, oldAssociation, "U13 旧关联");
        // 全局 UdpClassifyCalls 会包含后台 permitted 流量；旧关联是否被重分类由 pid/port 事件与
        // udp_blocked/deferred 归属，不能再用全局 calls==0 断言。
        Check.Equal(0, (long)oldAssociation.UdpBlocked, "U13: 旧关联不增加 udp_blocked");
        Check.Equal(0, (long)oldAssociation.DeferredBlocks, "U13: 旧关联不增加 deferred_blocks");
        ProbeEvent[] oldEvents = context.Device.GetEvents(32);
        Check.Equal(0, oldEvents.Count(e => e.Protocol == 17 && e.ProcessId == (uint)client.Pid &&
                                            e.RemotePort == port),
                    "U13: 旧关联没有新 classify 事件");
        Console.WriteLine($"[udp-rules] U13 old-association events: {DescribeEvents(oldEvents)}");

        client.SendCommand("new");
        await client.WaitForLineAsync("new", context.TimeoutMs);
        context.Device.ResetStats();
        long sinkBefore = context.BlockSink.Received;
        client.SendCommand("send hot-3");
        string third = await client.WaitForLineAsync("send#2", context.TimeoutMs);
        Check.Contains(third, "recv-failed", "U13: 新关联按新 blob 被 block");
        Check.True(!permitPeer.TryReceive(400, out _), "U13: 新关联 datagram 对端收不到", "");
        Check.Equal(sinkBefore, context.BlockSink.Received,
                    "U13: 新关联的固定 auth filter 命中（sink 没有收到 datagram）");

        ProbeStats newAssociation = context.Device.GetStats();
        CheckUdpInvariant(default, newAssociation, "U13 新关联");
        Check.Equal(1, (long)newAssociation.UdpClassifyCalls, "U13: 新关联 classify 一次");
        Check.Equal(1, (long)newAssociation.UdpBlocked, "U13: 新关联 udp_blocked +1");
        Check.Equal(1, (long)newAssociation.DeferredBlocks, "U13: 新关联 deferred_blocks +1");
        Check.True(context.Device.GetEvents(16).Any(e => e.Decision == Protocol.DecisionBlockedRule &&
                                                         e.Protocol == 17),
                   "U13: 新关联事件 decision=blocked_rule protocol=17", "");

        client.SendCommand("quit");
        (int exitCode, _) = await client.WaitForExitAsync(context.TimeoutMs);
        Check.Equal(0, exitCode, "U13: 客户端退出码");
    }

    private static unsafe void ClearSelfRegression(WfpDevice device)
    {
        Console.WriteLine("[udp-rules] ===== control --clear-self 工具回归 =====");
        int code = Program.Control(new Options(new[] { "--clear-self" }));
        Check.Equal(0, code, "control --clear-self: 退出码");
        ProbeConfig cleared = device.GetConfig();
        Check.Equal(0, (long)cleared.SelfProcessCount, "control --clear-self: self_process_count=0");
        int remaining = 0;
        for (int index = 0; index < Protocol.MaxSelfProcessIds; index++)
        {
            if (cleared.SelfProcessIds[index] != 0)
                remaining++;
        }
        Check.Equal(0, remaining, "control --clear-self: self_process_ids 全部清零");
    }

    private static void CheckUdpInvariant(ProbeStats before, ProbeStats after, string label)
    {
        long calls = (long)(after.UdpClassifyCalls - before.UdpClassifyCalls);
        long decisions = (long)(after.UdpPermitted - before.UdpPermitted)
                       + (long)(after.UdpBlocked - before.UdpBlocked)
                       + (long)(after.UdpRedirected - before.UdpRedirected);
        Check.Equal(calls, decisions, $"{label}: UDP 不变式 calls=permitted+blocked+redirected");
    }

    private static RedirectContext ParseContext(ReceivedDatagram datagram, string label)

    {
        Check.Equal(ContextSize, datagram.RedirectContext?.Length ?? 0, $"{label}: context 长度 36B");
        Check.True(datagram.ContextLevel is UdpSockets.IpProtoIp or UdpSockets.IpProtoIpv6,
                   $"{label}: cmsg level 是 IPPROTO_IP/IPPROTO_IPV6",
                   $"level={datagram.ContextLevel}");
        if (datagram.RedirectContext is not { Length: ContextSize } raw)
            return default;

        RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
        Check.Equal(Protocol.RedirectContextMagic, context.Magic, $"{label}: context magic");
        Check.Equal(Protocol.RedirectContextVersion, context.Version, $"{label}: context version");
        return context;
    }

    private static void EqualText(string expected, string actual, string name) =>
        Check.True(expected == actual, name, $"expected={expected} actual={actual}");

    private static unsafe string ContextAddress(RedirectContext context)
    {
        return Program.AddressText(context.OriginalAddress, context.Family);
    }

    private static string DescribeEvents(IEnumerable<ProbeEvent> events) =>
        string.Join(", ", events.Select(e => $"{Program.DecisionName(e.Decision)}/proto={e.Protocol}" +
                                              $"/rule={(e.MatchedRule == Protocol.InvalidRuleIndex ? "-" : e.MatchedRule.ToString(CultureInfo.InvariantCulture))}" +
                                              $"/remote-port={e.RemotePort}/pid={e.ProcessId}"));

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static (bool Available, bool Ok, string Detail) RunPing(string host)
    {
        var startInfo = new ProcessStartInfo("ping")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("1000");
        startInfo.ArgumentList.Add(host);

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ping");
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            int replies = Regex.Matches(output, "TTL=", RegexOptions.IgnoreCase).Count;
            return (true, process.ExitCode == 0 && replies >= 2, $"exit={process.ExitCode} replies={replies}");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return (false, false, $"ping 不可用：{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void CheckPing(string name, string host)
    {
        (bool available, bool ok, string detail) = RunPing(host);
        if (available)
            Check.True(ok, name, detail);
        else
            Console.WriteLine($"[udp-rules] ENV-GAP: {name} 跳过：{detail}");
    }


    // 套件进程自己登记在 self_process_ids 里（listener 回包闸）；验证 block 规则时先临时
    // 取消 self-PID，让这条连接真正进入规则匹配，结束后恢复原配置。
    private static unsafe (bool Connected, SocketError Error, string Detail) TryTcpConnectAsNonSelf(
        SuiteContext context, IPEndPoint target, int timeoutMs)
    {
        ProbeConfig saved = context.Device.GetConfig();
        ProbeConfig withoutSelf = saved;
        withoutSelf.SelfProcessCount = 0;
        for (int index = 0; index < Protocol.MaxSelfProcessIds; index++)
            withoutSelf.SelfProcessIds[index] = 0;
        context.Device.SetConfig(withoutSelf);
        try
        {
            return TryTcpConnect(target, timeoutMs);
        }
        finally
        {
            context.Device.SetConfig(saved);
        }
    }

    private static (bool Connected, SocketError Error, string Detail) TryTcpConnect(IPEndPoint target, int timeoutMs)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            socket.ConnectAsync(target, timeout.Token).AsTask().GetAwaiter().GetResult();
            return (true, SocketError.Success, "connected");
        }
        catch (SocketException exception)
        {
            return (false, exception.SocketErrorCode, $"{exception.SocketErrorCode} ({exception.ErrorCode})");
        }
        catch (Exception exception)
        {
            return (false, SocketError.SocketError, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static PoolTagUsage Pool() => PoolTags.Find(Protocol.PoolTag) ?? PoolTagUsage.Zero(Protocol.PoolTag);

    private static PoolTagUsage WaitForPool(PoolTagUsage baseline, TimeSpan window)
    {
        PoolTagUsage current = Pool();
        var watch = Stopwatch.StartNew();
        while (current.NonPagedOutstanding > baseline.NonPagedOutstanding && watch.Elapsed < window)
        {
            Thread.Sleep(50);
            current = Pool();
        }
        return current;
    }

    private sealed class SuiteContext : IDisposable
    {
        private readonly RedirectListener listener;
        private readonly int timeoutMs;

        public SuiteContext(WfpDevice device,
                            RedirectListener listener,
                            BlockSinkBinding blockSink,
                            string exe,
                            string appId,
                            int processId,
                            int timeoutMs)
        {
            Device = device;
            this.listener = listener;
            BlockSink = blockSink;
            Exe = exe;
            AppId = appId;
            ProcessId = processId;
            this.timeoutMs = timeoutMs;
        }

        public WfpDevice Device { get; }

        public RedirectListener Listener => listener;

        public BlockSinkBinding BlockSink { get; }

        public string Exe { get; }

        public string AppId { get; }

        public int ProcessId { get; }

        public int TimeoutMs => timeoutMs;

        public ProbeStats Begin()
        {
            Device.ResetStats();
            return Device.GetStats();
        }

        public void SetBlob(IReadOnlyList<TestRule> rules, uint fallback = Protocol.RuleActionPermit) =>
            Device.SetRuleBlob(BlobBuilder.Build(rules, fallback));

        public void SetEnabled(bool enabled)
        {
            ProbeConfig config = Device.GetConfig();
            config.Enabled = enabled ? 1u : 0u;
            Device.SetConfig(config);
        }

        public UdpClientProcess StartClient(params string[] arguments) =>
            UdpClientProcess.Start(Exe, interactive: false, arguments);

        public UdpClientProcess StartInteractive(params string[] arguments) =>
            UdpClientProcess.Start(Exe, interactive: true,
                                   new[] { "--control-stdin" }.Concat(arguments).ToArray());

        public void Dispose() => listener.Dispose();
    }

    private sealed class RedirectListener : IDisposable
    {
        private readonly Socket socket;

        public RedirectListener()
        {
            socket = UdpSockets.CreateListener(new IPEndPoint(IPAddress.IPv6Any, 0), dualMode: true);
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        public int Port { get; }

        public ReceivedDatagram Receive(int timeoutMs) =>
            UdpSockets.Receive(socket, MaxPayload, MaxControl, timeoutMs);

        public bool TryReceive(int timeoutMs, out ReceivedDatagram datagram)
        {
            try
            {
                datagram = Receive(timeoutMs);
                return true;
            }
            catch (TimeoutException)
            {
                datagram = null!;
                return false;
            }
        }

        public void Reply(ReceivedDatagram datagram) => socket.SendTo(datagram.Payload, datagram.Source);

        public void Dispose() => socket.Dispose();
    }

    private sealed class UdpPeer : IDisposable
    {
        private readonly Socket socket;

        public UdpPeer()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        public int Port { get; }

        public bool TryReceive(int timeoutMs, out PeerDatagram datagram)
        {
            datagram = null!;
            if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
                return false;

            byte[] buffer = new byte[MaxPayload];
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);
            int received = socket.ReceiveFrom(buffer, ref source);
            datagram = new PeerDatagram(buffer[..received], (IPEndPoint)source);
            return true;
        }

        public void Send(byte[] payload, IPEndPoint target) => socket.SendTo(payload, target);

        public void Dispose() => socket.Dispose();
    }

    private sealed record PeerDatagram(byte[] Payload, IPEndPoint Source);

    private sealed class UdpClientProcess : IDisposable
    {
        private readonly Process process;
        private readonly List<string> lines = new();
        private readonly object gate = new();
        private int cursor;

        private UdpClientProcess(Process process)
        {
            this.process = process;
        }

        public int Pid { get; private set; }

        public string Output
        {
            get
            {
                lock (gate)
                    return string.Join("\n", lines);
            }
        }

        public static UdpClientProcess Start(string exe, bool interactive, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = interactive,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("udp-client");
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            var process = new Process { StartInfo = startInfo };
            var handle = new UdpClientProcess(process);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    handle.Add(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    handle.Add(args.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return handle;
        }

        private void Add(string line)
        {
            lock (gate)
            {
                lines.Add(line);
                if (Pid == 0 && TryReadPid(line, out int pid))
                    Pid = pid;
            }
        }

        private static bool TryReadPid(string line, out int pid)
        {
            pid = 0;
            int index = line.IndexOf("pid=", StringComparison.Ordinal);
            if (index < 0)
                return false;
            index += 4;
            int end = index;
            while (end < line.Length && char.IsAsciiDigit(line[end]))
                end++;
            return end > index && int.TryParse(line[index..end], NumberStyles.Integer,
                                               CultureInfo.InvariantCulture, out pid);
        }

        public async Task WaitForReadyAsync(int timeoutMs)
        {
            await WaitForLineAsync("ready ", timeoutMs);
            if (Pid == 0)
                throw new InvalidOperationException($"客户端没有输出 pid：\n{Output}");
        }

        public async Task<string> WaitForLineAsync(string needle, int timeoutMs)
        {
            for (int waited = 0; waited <= timeoutMs; waited += 20)
            {
                lock (gate)
                {
                    for (int index = cursor; index < lines.Count; index++)
                    {
                        if (!lines[index].Contains(needle, StringComparison.Ordinal))
                            continue;
                        cursor = index + 1;
                        return lines[index];
                    }
                }
                await Task.Delay(20);
            }
            throw new TimeoutException($"等待客户端输出“{needle}”超时（{timeoutMs} ms）：\n{Output}");
        }

        public void SendCommand(string command)
        {
            try
            {
                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                throw new InvalidOperationException($"客户端已退出，无法发送：{command}\n{Output}", exception);
            }
        }

        public async Task<(int ExitCode, string Output)> WaitForExitAsync(int timeoutMs)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"客户端超时未退出（{timeoutMs} ms）：\n{Output}");
            }
            process.WaitForExit();
            return (process.ExitCode, Output);
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
            process.Dispose();
        }
    }
}
