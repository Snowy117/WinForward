using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;

namespace WinForward.Probe;

// B4b 的 TCP DNAT / redirect 表 / 回程 SNAT 验证套件。
//
// 外部客户端（Linux）在约定端口上发起连接；套件在 VM 里下发 redirect 规则、启用 forwarded config，
// 监听 DNAT 后的端口，按 redirect 表的 upstream_id 做直连或 SOCKS5 中继，并核对计数器。
// 每个 phase 都是「安装 → 打印 READY → 等连接 → 断言 → 恢复」；恢复也由看门狗兜底。
internal static class ForwardedTcpSuite
{
    private const int DefaultListenerPort = 40400;
    private const int DefaultRemotePort = 40250;
    private const string DefaultRemoteAddress = "10.99.0.1";
    private const string DefaultBackend = "192.168.100.4:40250";

    public static async Task<int> RunAsync(Options options)
    {
        if (options.Has("restore-only"))
        {
            using var restoreDevice = new WfpDevice();
            Restore(restoreDevice, options.GetInt("listener-port", DefaultListenerPort));
            Console.WriteLine("[forwarded-tcp] restore-only rules=0 fallback=permit config=disabled");
            return 0;
        }

        string phase = options.Get("phase") ?? "core";
        string localIp = options.Get("local-ip") ?? "192.168.100.2";
        int listenerPort = options.GetInt("listener-port", DefaultListenerPort);
        string remoteAddress = options.Get("remote-address") ?? DefaultRemoteAddress;
        int remotePort = options.GetInt("remote-port", DefaultRemotePort);
        IPEndPoint backend = ParseEndPoint(options.Get("backend") ?? DefaultBackend);
        int waitMs = options.GetInt("wait-ms", 20000);
        int expectAccepts = options.GetInt("expect-accepts", phase == "upstreams" ? 2 : 1);
        int timeoutMs = options.GetInt("timeout-ms", 8000);
        int payloadBytes = options.GetInt("bytes", 65536);
        VerifyPlan? verify = options.Has("no-verify")
                                 ? null
                                 : new VerifyPlan
                                 {
                                     Port = options.GetInt("verify-port", listenerPort + 1000),
                                     Bytes = payloadBytes,
                                     Sha = ExpectedPayloadSha(payloadBytes),
                                     TimeoutMs = options.GetInt("verify-timeout-ms", 15000),
                                 };
        string? clientCidr = options.Get("client-cidr");
        uint ifIndex = options.Has("ifindex") ? options.GetUInt("ifindex", 0) : FindAdapterIndex(localIp) ?? 0;

        Check.True(ifIndex != 0, "解析入口 ifIndex", $"ifindex={ifIndex} local-ip={localIp}");
        IPAddress localAddress = IPAddress.Parse(localIp);
        byte family = (byte)(localAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 6);

        using var device = new WfpDevice();
        ProbeStats stats = device.GetStats();
        Check.Equal(Protocol.AbiVersion, (long)stats.AbiVersion, "驱动 ABI 版本与夹具一致");

        using OnLinkAlias alias = OnLinkAlias.Ensure(remoteAddress, localIp, ifIndex, !options.Has("no-alias"));
        if (!alias.Ready)
        {
            Console.WriteLine($"[forwarded-tcp] alias 不可用：{alias.Error}");
            return 7;
        }
        int watchdogSeconds = options.GetInt("watchdog-seconds", Math.Max(90, (waitMs / 1000) + 60));
        using var watchdog = StartRestoreWatchdog(watchdogSeconds, listenerPort);
        try
        {
            return phase switch
            {
                "core" => await RunCoreAsync(device, ifIndex, listenerPort, localIp, family,
                                             IPAddress.Parse(remoteAddress), remotePort, backend,
                                             clientCidr, expectAccepts, waitMs, timeoutMs, verify),
                "upstreams" => await RunUpstreamsAsync(device, ifIndex, listenerPort, localIp, family,
                                                       IPAddress.Parse(remoteAddress), remotePort, backend,
                                                       clientCidr, expectAccepts, waitMs, timeoutMs, verify),
                "fail" => await RunFailAsync(device, ifIndex, listenerPort, localIp, family,
                                             IPAddress.Parse(remoteAddress), remotePort, clientCidr, waitMs, timeoutMs),
                _ => throw new ArgumentException($"未知 phase：{phase}（core / upstreams / fail）"),
            };
        }
        finally
        {
            if (!options.Has("keep-blob"))
            {
                Restore(device, listenerPort);
            }
            if (watchdog is not null && !watchdog.HasExited)
            {
                watchdog.Kill(true);
            }
        }
    }

    private static async Task<int> RunCoreAsync(WfpDevice device,
                                                uint ifIndex,
                                                int listenerPort,
                                                string localIp,
                                                byte family,
                                                IPAddress remoteAddress,
                                                int remotePort,
                                                IPEndPoint backend,
                                                string? clientCidr,
                                                int expectAccepts,
                                                int waitMs,
                                                int timeoutMs,
                                                VerifyPlan? verify)
    {
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0),
            },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = new TcpListener(IPAddress.Any, listenerPort);
        listener.Start();
        using TcpListener? verifyListener = verify is null ? null : StartVerifyListener(verify);
        Console.WriteLine($"[forwarded-tcp] READY phase=core listener={listenerPort} " +
                          $"verify_port={verify?.Port.ToString() ?? "-"} " +
                          $"remote={remoteAddress}:{remotePort} backend={backend} accepts={expectAccepts}");
        Console.Out.Flush();

        var sessions = new List<SessionResult>();
        using var window = new CancellationTokenSource(waitMs);
        try
        {
            while (sessions.Count < expectAccepts)
            {
                Socket client = await listener.AcceptSocketAsync(window.Token);
                SessionResult result = await RelaySessionAsync(client, device, backend, null,
                                                               CancellationToken.None, timeoutMs);
                sessions.Add(result);
            }

            // 窗口尾部再等一小会：原帧若没被消费，可能额外冒出连接。
            using var tail = new CancellationTokenSource(500);
            try
            {
                Socket extra = await listener.AcceptSocketAsync(tail.Token);
                extra.Dispose();
                Check.True(false, "原帧被消费：没有额外 accept", "观察到了额外的连接");
            }
            catch (OperationCanceledException)
            {
                // 预期路径。
            }
            if (verify is not null && verifyListener is not null)
            {
                await CollectVerificationAsync(verifyListener, sessions, verify);
            }
        }
        catch (OperationCanceledException)
        {
            Check.True(false, "在等待窗口内收到客户端连接", $"accepted={sessions.Count} expect={expectAccepts}");
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("core", stats);

        Check.True(sessions.Count == expectAccepts, "accept 数符合预期", $"{sessions.Count}/{expectAccepts}");
        // 注入完成的 NBL 必须全部回调；失败意味着原帧被消费但副本没发出去。
        Check.AtLeast((long)(stats.RedirectIngress + stats.RedirectReturn), (long)stats.InjectCompleteCalls,
                      "每个成功注入都有完成回调");
        Check.Equal(0, (long)stats.InjectCompleteFailed, "注入完成状态全部成功");
        foreach (SessionResult session in sessions)
        {
            Check.True(session.QueryOk, "redirect 查询命中", session.Detail);
            Check.Equal(remotePort, session.ServerPort, "redirect 表记录原始服务器端口");
            Check.True(session.ServerAddress.Equals(remoteAddress), "redirect 表记录原始服务器地址",
                       $"{session.ServerAddress}");
            Check.Equal(0, session.UpstreamId, "redirect 表记录 upstream_id");
            Check.True(session.BytesClientToServer > 0, "中继收到客户端数据", $"{session.BytesClientToServer}B");
            Check.True(session.BytesServerToClient > 0, "回程数据回到客户端", $"{session.BytesServerToClient}B");
            Check.True(session.CleanEof, "会话以半关闭/EOF 正常收尾", session.Detail);
        }

        Check.AtLeast(1, (long)stats.RedirectCreated, "SYN 建表");
        Check.AtLeast(3, (long)stats.RedirectIngress, "入口 DNAT 注入（SYN + 数据 + FIN）");
        Check.AtLeast(3, (long)stats.RedirectReturn, "回程 SNAT 注入");
        Check.AtLeast(1, (long)stats.SelfInjected, "注入帧被防环闸识别");
        Check.Equal(0, (long)stats.RedirectNoConfig, "配置齐全：没有 no_config");
        Check.Equal(0, (long)stats.RedirectNoState, "表项命中：没有 no_state");
        Check.Equal(0, (long)stats.RedirectCapacity, "没有容量失败");
        Check.Equal(0, (long)stats.RedirectInjectFailed, "没有注入失败");
        CheckInvariants(stats, "core");

        return Check.Failures == 0 ? 0 : 6;
    }

    private static async Task<int> RunUpstreamsAsync(WfpDevice device,
                                                     uint ifIndex,
                                                     int listenerPort,
                                                     string localIp,
                                                     byte family,
                                                     IPAddress remoteAddress,
                                                     int remotePort,
                                                     IPEndPoint backend,
                                                     string? clientCidr,
                                                     int expectAccepts,
                                                     int waitMs,
                                                     int timeoutMs,
                                                     VerifyPlan? verify)
    {
        await using var upstream0 = new Socks5FakeServer();
        await using var upstream1 = new Socks5FakeServer();
        var upstreams = new Dictionary<uint, Socks5FakeServer>
        {
            [0] = upstream0,
            [1] = upstream1,
        };

        bool singleUpstream = expectAccepts != 2;
        byte[] blob = ForwardedBlobBuilder.Build(
            singleUpstream
                ? new[]
                {
                    RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0),
                }
                : new[]
                {
                    RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0),
                    RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort + 1, remotePort + 1, 1),
                },
            Protocol.RuleActionPermit);
        Install(device, blob, (uint)(singleUpstream ? 1 : 2), listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = new TcpListener(IPAddress.Any, listenerPort);
        listener.Start();
        using TcpListener? verifyListener = verify is null ? null : StartVerifyListener(verify);
        Console.WriteLine($"[forwarded-tcp] READY phase=upstreams listener={listenerPort} " +
                          $"verify_port={verify?.Port.ToString() ?? "-"} " +
                          $"ports={(singleUpstream ? remotePort.ToString() : $"{remotePort},{remotePort + 1}")} backend={backend} " +
                          $"socks5={upstream0.EndPoint},{upstream1.EndPoint}");
        Console.Out.Flush();

        var sessions = new List<SessionResult>();
        using var window = new CancellationTokenSource(waitMs);
        try
        {
            while (sessions.Count < expectAccepts)
            {
                Socket client = await listener.AcceptSocketAsync(window.Token);
                SessionResult result = await RelaySessionAsync(client, device, backend, upstreams,
                                                               CancellationToken.None, timeoutMs);
                sessions.Add(result);
            }
        }
        catch (OperationCanceledException)
        {
            Check.True(false, "在等待窗口内收到两个客户端连接", $"accepted={sessions.Count}");
        }

        if (verify is not null && verifyListener is not null)
        {
            await CollectVerificationAsync(verifyListener, sessions, verify);
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("upstreams", stats);

        Check.True(sessions.Count == expectAccepts, "客户端都触发 accept", $"{sessions.Count}/{expectAccepts}");
        if (singleUpstream)
        {
            Check.Equal(expectAccepts, upstream0.Connections, "单上游收到全部 CONNECT");
            Check.Equal(0, upstream1.Connections, "第二个上游没有 CONNECT");
            foreach (SessionResult session in sessions)
            {
                Check.Equal(0, session.UpstreamId, "所有会话映射到 upstream 0");
            }
        }
        else
        {
            Check.Equal(1, upstream0.Connections, "upstream 0 收到一次 CONNECT");
            Check.Equal(1, upstream1.Connections, "upstream 1 收到一次 CONNECT");
            var byPort = sessions.ToDictionary(session => session.ServerPort);
            Check.True(byPort.TryGetValue(remotePort, out SessionResult? first) && first is not null && first.UpstreamId == 0,
                       "端口映射到 upstream 0", $"upstream={first?.UpstreamId}");
            Check.True(byPort.TryGetValue(remotePort + 1, out SessionResult? second) && second is not null && second.UpstreamId == 1,
                       "端口映射到 upstream 1", $"upstream={second?.UpstreamId}");
        }
        Check.Equal(0, (long)stats.RedirectNoState, "两条流都命中 redirect 表");
        Check.AtLeast((long)(stats.RedirectIngress + stats.RedirectReturn), (long)stats.InjectCompleteCalls,
                      "每个成功注入都有完成回调");
        Check.Equal(0, (long)stats.InjectCompleteFailed, "注入完成状态全部成功");
        foreach (SessionResult session in sessions)
        {
            Check.True(session.BytesClientToServer > 0, "upstream 中继收到客户端数据",
                       $"{session.BytesClientToServer}B port={session.ServerPort}");
            Check.True(session.BytesServerToClient > 0, "upstream 回程数据回到客户端",
                       $"{session.BytesServerToClient}B port={session.ServerPort}");
        }
        CheckInvariants(stats, "upstreams");

        return Check.Failures == 0 ? 0 : 6;
    }

    private static async Task<int> RunFailAsync(WfpDevice device,
                                                uint ifIndex,
                                                int listenerPort,
                                                string localIp,
                                                byte family,
                                                IPAddress remoteAddress,
                                                int remotePort,
                                                string? clientCidr,
                                                int waitMs,
                                                int timeoutMs)
    {
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0),
            },
            Protocol.RuleActionPermit);
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo info = device.GetForwardedInfo();
        Check.Equal(1, info.Installed, "redirect 规则可以下发（B4b 不再整块拒绝）");
        device.SetForwardedConfig(DisabledConfig(listenerPort));
        device.ResetForwardedStats();

        using var listener = new TcpListener(IPAddress.Any, listenerPort);
        listener.Start();
        Console.WriteLine($"[forwarded-tcp] READY phase=fail listener={listenerPort} " +
                          $"remote={remoteAddress}:{remotePort} expect=no-accept timeout_ms={timeoutMs}");
        Console.Out.Flush();

        bool accepted = false;
        using (var window = new CancellationTokenSource(timeoutMs))
        {
            try
            {
                Socket extra = await listener.AcceptSocketAsync(window.Token);
                extra.Dispose();
                accepted = true;
            }
            catch (OperationCanceledException)
            {
                // 预期：没有配置时 SYN 被 block，listener 收不到连接。
            }
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("fail", stats);

        Check.True(!accepted, "无 forwarded config 时连接不会到达 listener", $"accepted={accepted}");
        Check.AtLeast(1, (long)stats.RedirectNoConfig, "redirect_no_config 增加");
        Check.AtLeast(1, (long)stats.RedirectBlocked, "redirect_blocked 增加");
        Check.AtLeast(1, (long)stats.Blocked, "原帧被 block");
        CheckInvariants(stats, "fail");
        return Check.Failures == 0 ? 0 : 6;
    }

    private static ForwardedTestRule RedirectRule(uint ifIndex,
                                                  string? clientCidr,
                                                  IPAddress remoteAddress,
                                                  int portStart,
                                                  int portEnd,
                                                  uint upstreamId) => new()
    {
        Action = Protocol.RuleActionRedirect,
        IngressAdapterId = ifIndex,
        ClientCidr = clientCidr,
        RemoteCidr = HostCidr(remoteAddress),
        PortStart = (ushort)portStart,
        PortEnd = (ushort)portEnd,
        TransportProtocol = Protocol.RuleProtocolTcp,
        Family = (byte)(remoteAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 6),
        UpstreamId = upstreamId,
    };

    private static async Task<SessionResult> RelaySessionAsync(Socket client,
                                                               WfpDevice device,
                                                               IPEndPoint backend,
                                                               Dictionary<uint, Socks5FakeServer>? upstreams,
                                                                CancellationToken token,
                                                               int timeoutMs)
    {
        using (client)
        {
            SessionResult result = new()
            {
                Remote = (IPEndPoint)client.RemoteEndPoint!,
                Local = (IPEndPoint)client.LocalEndPoint!,
            };
            try
            {
                ProbeForwardedRedirectQuery query = QueryFor((IPEndPoint)client.LocalEndPoint!,
                                                             (IPEndPoint)client.RemoteEndPoint!);
                ProbeForwardedRedirect redirect;
                try
                {
                    redirect = device.QueryForwardedRedirect(query);
                }
                catch (Win32Exception exception)
                {
                    result.Detail = $"查询失败 Win32={exception.NativeErrorCode} {exception.Message}";
                    return result;
                }

                result.QueryOk = redirect.Magic == ProbeForwardedRedirect.MagicValue &&
                                 redirect.Version == ProbeForwardedRedirect.VersionValue;
                result.ServerAddress = redirect.ServerIP;
                result.ServerPort = redirect.ServerPort;
                result.UpstreamId = redirect.UpstreamId;
                result.IngressIfIndex = redirect.IngressIfIndex;

                Socket outbound;
                if (upstreams is not null && upstreams.TryGetValue(redirect.UpstreamId, out Socks5FakeServer? fake))
                {
                    outbound = await Socks5TestClient.ConnectAsync(fake.EndPoint, backend, timeoutMs, token);
                }
                else
                {
                    outbound = await ConnectAsync(backend, timeoutMs, token);
                }

                using (outbound)
                {
                    (long up, long down, bool clean, string reason) =
                        await PumpPairAsync(client, outbound, timeoutMs, token);
                    result.BytesClientToServer = up;
                    result.BytesServerToClient = down;
                    result.CleanEof = clean;
                    result.Detail = reason;
                }
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                           or IOException or ObjectDisposedException or TimeoutException)
            {
                result.Detail = $"{exception.GetType().Name}: {exception.Message}";
            }

            return result;
        }
    }

    private static async Task<(long Up, long Down, bool Clean, string Reason)> PumpPairAsync(
        Socket client, Socket outbound, int timeoutMs, CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<(long Bytes, bool Clean)> forward = PumpAsync(client, outbound, session.Token);
        Task<(long Bytes, bool Clean)> backward = PumpAsync(outbound, client, session.Token);
        Task<(long Bytes, bool Clean)> first = await Task.WhenAny(forward, backward);
        (long _, bool firstClean) = await first;
        if (!firstClean)
        {
            session.Cancel();
        }

        Task<(long Bytes, bool Clean)> second = ReferenceEquals(first, forward) ? backward : forward;
        try
        {
            await second.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            session.Cancel();
            return (forward.IsCompletedSuccessfully ? forward.Result.Bytes : 0,
                    backward.IsCompletedSuccessfully ? backward.Result.Bytes : 0,
                    false,
                    "drain-timeout");
        }

        (long upBytes, bool upClean) = await forward;
        (long downBytes, bool downClean) = await backward;
        return (upBytes, downBytes, upClean && downClean, upClean && downClean ? "peer-closed" : "pump-failed");
    }

    private static async Task<(long Bytes, bool Clean)> PumpAsync(Socket from, Socket to, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        bool clean = false;
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, token);
                if (read == 0)
                {
                    clean = true;
                    break;
                }
                int offset = 0;
                while (offset < read)
                {
                    offset += await to.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, token);
                }
                total += read;
            }

            try
            {
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // 对端已经关了，半关闭没有意义。
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 异常关闭：字节数保留，clean=false。
        }
        return (total, clean);
    }

    private static async Task<Socket> ConnectAsync(IPEndPoint target, int timeoutMs, CancellationToken token)
    {
        Socket socket = new(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            await socket.ConnectAsync(target, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static unsafe ProbeForwardedRedirectQuery QueryFor(IPEndPoint local, IPEndPoint remote)
    {
        ProbeForwardedRedirectQuery query = default;
        query.ListenerPort = (ushort)local.Port;
        query.Family = (byte)(remote.AddressFamily == AddressFamily.InterNetwork ? 4 : 6);
        query.ClientPort = (ushort)remote.Port;
        query.Protocol = Protocol.RuleProtocolTcp;
        byte[] clientBytes = remote.Address.GetAddressBytes();
        for (int index = 0; index < clientBytes.Length; index++)
        {
            query.ClientAddress[index] = clientBytes[index];
        }
        return query;
    }

    private static unsafe void Install(WfpDevice device,
                                byte[] blob,
                                uint expectedRules,
                                int listenerPort,
                                string localIp,
                                byte family)
    {
        device.SetForwardedConfig(DisabledConfig(listenerPort));
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo info = device.GetForwardedInfo();
        Check.Equal(1, info.Installed, "forwarded blob installed=1");
        Check.Equal(expectedRules, info.RuleCount, "forwarded 规则数回读一致");

        ProbeForwardedConfig disabled = device.GetForwardedConfig();
        Check.Equal(0, disabled.Enabled, "启用前 config 回读 enabled=0");

        IPAddress address = IPAddress.Parse(localIp);
        ProbeForwardedConfig enabled = default;
        enabled.Enabled = 1;
        enabled.ListenerPort = (ushort)listenerPort;
        enabled.Family = family;
        byte[] listenerBytes = address.GetAddressBytes();
        for (int index = 0; index < listenerBytes.Length; index++)
        {
            enabled.ListenerAddress[index] = listenerBytes[index];
        }
        device.SetForwardedConfig(enabled);

        ProbeForwardedConfig readback = device.GetForwardedConfig();
        Check.Equal(1, readback.Enabled, "config 回读 enabled=1");
        Check.Equal(listenerPort, readback.ListenerPort, "config 回读 listener_port");
        Check.Equal(family, readback.Family, "config 回读 family");
    }

    private static ProbeForwardedConfig DisabledConfig(int listenerPort) => new()
    {
        ListenerPort = (ushort)listenerPort,
    };

    private static void Restore(WfpDevice device, int listenerPort)
    {
        try
        {
            device.SetForwardedConfig(DisabledConfig(listenerPort));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-tcp] 恢复 forwarded config 失败：{exception.Message}");
        }

        try
        {
            device.SetForwardedBlob(ForwardedBlobBuilder.BuildEmpty(Protocol.RuleActionPermit));
            Console.WriteLine("[forwarded-tcp] 已恢复 rules=0 fallback=permit config=disabled");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-tcp] 恢复空 blob 失败：{exception.Message}");
        }
    }

    private static void CheckInvariants(ProbeForwardedStats stats, string name)
    {
        Check.Equal((long)stats.ClassifyCalls,
                    (long)(stats.NoWriteRight + stats.InboundFrames + stats.OutboundFrames),
                    $"{name}：classify_calls == no_write_right + in + out");
        Check.Equal((long)(stats.InboundFrames + stats.OutboundFrames),
                    (long)(stats.SelfInjected + stats.NoTable + stats.Malformed + stats.NonIp + stats.Permitted +
                           stats.Blocked + stats.Redirected),
                    $"{name}：帧去向守恒（含 redirected）");
        Check.Equal((long)(stats.MatchedPermit + stats.FallbackPermit + stats.UdpReturnMiss),
                    (long)stats.Permitted,
                    $"{name}：permitted == matched_permit + fallback_permit + udp_return_miss");
        Check.Equal((long)stats.Blocked,
                    (long)(stats.MatchedBlock + stats.FallbackBlock + stats.RedirectBlocked +
                           stats.RedirectReturnFailed + stats.UdpBlocked + stats.UdpReturnFailed),
                    $"{name}：blocked == matched_block + fallback_block + redirect 失败 + udp 失败");
        Check.Equal((long)stats.Matched,
                    (long)(stats.MatchedPermit + stats.MatchedBlock + stats.MatchedRedirect),
                    $"{name}：matched == permit + block + redirect");
        Check.Equal((long)(stats.MatchedRedirect + stats.UdpFragmented),
                    (long)(stats.RedirectIngress + stats.RedirectBlocked),
                    $"{name}：matched_redirect + udp_fragmented == ingress + blocked");
        Check.Equal((long)stats.RedirectBlocked,
                    (long)(stats.RedirectNoConfig + stats.RedirectNoState + stats.RedirectCapacity +
                           stats.RedirectNonTcp + stats.RedirectInjectFailed),
                    $"{name}：redirect_blocked 拆分");
        Check.Equal((long)stats.Redirected, (long)(stats.RedirectIngress + stats.RedirectReturn),
                    $"{name}：redirected == ingress + return");
    }

    private static void PrintStats(string prefix, ProbeForwardedStats stats) =>
        Console.WriteLine($"[forwarded-tcp] {prefix} stats: calls={stats.ClassifyCalls} " +
                          $"in={stats.InboundFrames} out={stats.OutboundFrames} " +
                          $"redirected={stats.Redirected} (ingress={stats.RedirectIngress} return={stats.RedirectReturn}) " +
                          $"created={stats.RedirectCreated} active={stats.RedirectActive}/{stats.RedirectHighWater} " +
                          $"blocked={stats.Blocked} redirect_blocked={stats.RedirectBlocked} " +
                          $"no_config={stats.RedirectNoConfig} no_state={stats.RedirectNoState} " +
                          $"capacity={stats.RedirectCapacity} non_tcp={stats.RedirectNonTcp} " +
                          $"inject_failed={stats.RedirectInjectFailed} return_failed={stats.RedirectReturnFailed} " +
                           $"return_miss={stats.RedirectReturnMiss} expired={stats.RedirectExpired} " +
                           $"multi_nb={stats.RedirectMultiNb} complete={stats.InjectCompleteCalls}/" +
                           $"failed={stats.InjectCompleteFailed} self_injected={stats.SelfInjected}");

    private sealed class VerifyPlan
    {
        public int Port;
        public int Bytes;
        public string Sha = string.Empty;
        public int TimeoutMs;
    }

    private sealed class VerifyRecord
    {
        public int Port;
        public int Bytes;
        public long Acked;
        public string Sha = string.Empty;
        public bool Ok;
    }

    private static string ExpectedPayloadSha(int size)
    {
        byte[] payload = new byte[size];
        for (int index = 0; index < size; index++)
        {
            payload[index] = (byte)((index * 31 + 7) & 0xFF);
        }
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static TcpListener StartVerifyListener(VerifyPlan plan)
    {
        TcpListener listener = new(IPAddress.Any, plan.Port);
        listener.Start();
        return listener;
    }

    // 外部客户端在数据面结束后连 verify 端口，回报它实际收到的字节数 / 客户端侧 ACK 字节数 / SHA-256。
    // 没有这条回报就不能把「套件自己写完了 64KiB」当成 PASS：B4b 的丢包正是这样漏掉的。
    private static async Task CollectVerificationAsync(TcpListener listener,
                                                       List<SessionResult> sessions,
                                                       VerifyPlan plan)
    {
        var records = new List<VerifyRecord>();
        using var timeout = new CancellationTokenSource(plan.TimeoutMs);
        try
        {
            while (records.Count < sessions.Count)
            {
                using Socket socket = await listener.AcceptSocketAsync(timeout.Token);
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    continue;
                }

                Console.WriteLine($"[forwarded-tcp] verify {line}");
                VerifyRecord record = new();
                foreach (string field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = field.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }
                    string key = field[..equals];
                    string value = field[(equals + 1)..];
                    switch (key)
                    {
                        case "port":
                            record.Port = int.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "bytes":
                            record.Bytes = int.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "acked":
                            record.Acked = long.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "sha":
                            record.Sha = value;
                            break;
                        case "ok":
                            record.Ok = value == "1";
                            break;
                    }
                }
                records.Add(record);
            }
        }
        catch (OperationCanceledException)
        {
            // 缺回报时由下面的断言给出失败。
        }

        foreach (SessionResult session in sessions)
        {
            VerifyRecord? record = records.FirstOrDefault(candidate => candidate.Port == session.Remote.Port);
            Check.True(record is not null, "客户端回报了验证记录",
                       $"client_port={session.Remote.Port} records={records.Count}/{sessions.Count}");
            if (record is null)
            {
                continue;
            }
            Check.True(record.Ok, "客户端声明数据完整", $"client_port={record.Port}");
            Check.Equal(plan.Bytes, record.Bytes, "客户端实际收到完整字节数");
            Check.True(string.Equals(record.Sha, plan.Sha, StringComparison.Ordinal),
                       "客户端收到的字节内容 SHA-256 一致", $"expected={plan.Sha[..12]} actual={record.Sha}");
            Check.AtLeast(plan.Bytes, record.Acked, "客户端 TCP 已确认全部回程字节（ACK 前进）");
        }
    }

    private static Process? StartRestoreWatchdog(int seconds, int listenerPort)
    {
        string? exe = Environment.ProcessPath;
        string? shell = Environment.GetEnvironmentVariable("COMSPEC");
        if (exe is null || shell is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/c ping -n {seconds + 1} 127.0.0.1 >nul & \"{exe}\" forwarded-tcp --restore-only " +
                        $"--listener-port {listenerPort}",
        };
        try
        {
            Process? process = Process.Start(startInfo);
            Console.WriteLine($"[forwarded-tcp] 看门狗已启动：{seconds}s 后恢复空 permit 表与 disabled config");
            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Console.WriteLine($"[forwarded-tcp] 看门狗启动失败：{exception.Message}");
            return null;
        }
    }

    private static uint? FindAdapterIndex(string address)
    {
        IPAddress wanted = IPAddress.Parse(address);
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (!unicast.Address.Equals(wanted))
                {
                    continue;
                }
                int? index = adapter.GetIPProperties().GetIPv4Properties()?.Index;
                index ??= adapter.GetIPProperties().GetIPv6Properties()?.Index;
                return index is null ? null : (uint)index.Value;
            }
        }
        return null;
    }

    private static IPEndPoint ParseEndPoint(string text)
    {
        (string host, int port) = SocketHelpers.ParseEndPoint(text);
        return new IPEndPoint(IPAddress.Parse(host), port);
    }

    private static string HostCidr(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? $"{address}/32" : $"{address}/128";

    private sealed class SessionResult
    {
        public IPEndPoint Remote { get; init; } = new(IPAddress.Loopback, 0);
        public IPEndPoint Local { get; init; } = new(IPAddress.Loopback, 0);
        public bool QueryOk;
        public IPAddress ServerAddress = IPAddress.Any;
        public int ServerPort;
        public uint UpstreamId;
        public uint IngressIfIndex;
        public long BytesClientToServer;
        public long BytesServerToClient;
        public bool CleanEof;
        public string Detail = string.Empty;
    }
}

// 最小 SOCKS5 测试上游：握手后 CONNECT 到客户端请求的地址，然后原样转发。
internal sealed class Socks5FakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _connections;

    public Socks5FakeServer()
    {
        _listener.Start();
        EndPoint = (IPEndPoint)_listener.LocalEndpoint;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public IPEndPoint EndPoint { get; }

    public int Connections => Volatile.Read(ref _connections);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private static async Task ServeAsync(Socket client)
    {
        using (client)
        {
            try
            {
                byte[] header = new byte[2];
                await ReadExactlyAsync(client, header);
                int methods = header[1];
                byte[] methodsBuffer = new byte[methods];
                await ReadExactlyAsync(client, methodsBuffer);
                await client.SendAsync(new byte[] { 0x05, 0x00 });

                byte[] request = new byte[4];
                await ReadExactlyAsync(client, request);
                IPAddress destination;
                switch (request[3])
                {
                    case 0x01:
                        byte[] v4 = new byte[4];
                        await ReadExactlyAsync(client, v4);
                        destination = new IPAddress(v4);
                        break;
                    case 0x04:
                        byte[] v6 = new byte[16];
                        await ReadExactlyAsync(client, v6);
                        destination = new IPAddress(v6);
                        break;
                    case 0x03:
                        byte[] length = new byte[1];
                        await ReadExactlyAsync(client, length);
                        byte[] domain = new byte[length[0]];
                        await ReadExactlyAsync(client, domain);
                        destination = Dns.GetHostAddresses(Encoding.ASCII.GetString(domain))[0];
                        break;
                    default:
                        return;
                }

                byte[] portBytes = new byte[2];
                await ReadExactlyAsync(client, portBytes);
                int port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

                await client.SendAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });

                using Socket outbound = new(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                await outbound.ConnectAsync(new IPEndPoint(destination, port), _stopToken);
                await PumpBound(client, outbound);
            }
            catch (Exception)
            {
                // 测试上游尽力转发；异常让会话自然结束。
            }
        }
    }

    private static CancellationToken _stopToken => CancellationToken.None;

    private static async Task PumpBound(Socket client, Socket outbound)
    {
        using var session = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task up = CopyAsync(client, outbound, session.Token);
        Task down = CopyAsync(outbound, client, session.Token);
        Task first = await Task.WhenAny(up, down);
        Task second = ReferenceEquals(first, up) ? down : up;
        try
        {
            await second.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            session.Cancel();
        }

        try
        {
            await Task.WhenAll(up, down);
        }
        catch
        {
            // 关闭路径。
        }
    }

    private static async Task CopyAsync(Socket from, Socket to, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, token);
                if (read == 0)
                {
                    try
                    {
                        to.Shutdown(SocketShutdown.Send);
                    }
                    catch
                    {
                        // 对端已关。
                    }
                    return;
                }
                int offset = 0;
                while (offset < read)
                {
                    offset += await to.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, token);
                }
            }
        }
        catch
        {
            // 取消/关闭。
        }
    }

    private static async Task ReadExactlyAsync(Socket socket, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None);
            if (read == 0)
            {
                throw new IOException("SOCKS5 对端提前关闭");
            }
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
            // 关闭路径。
        }
        _stop.Dispose();
    }
}

internal static class Socks5TestClient
{
    public static async Task<Socket> ConnectAsync(IPEndPoint proxy, IPEndPoint destination, int timeoutMs,
                                                  CancellationToken token)
    {
        Socket socket = new(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            await socket.ConnectAsync(proxy, timeout.Token);

            await socket.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, timeout.Token);
            byte[] response = new byte[2];
            await ReadExactlyAsync(socket, response, timeout.Token);
            if (response[0] != 0x05 || response[1] != 0x00)
            {
                throw new IOException($"SOCKS5 协商失败：{response[0]:X2} {response[1]:X2}");
            }

            byte[] address = destination.Address.GetAddressBytes();
            byte[] request = new byte[4 + address.Length + 2];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = (byte)(address.Length == 4 ? 0x01 : 0x04);
            address.CopyTo(request, 4);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + address.Length), (ushort)destination.Port);
            await socket.SendAsync(request, timeout.Token);

            byte[] reply = new byte[4];
            await ReadExactlyAsync(socket, reply, timeout.Token);
            if (reply[1] != 0x00)
            {
                throw new IOException($"SOCKS5 CONNECT 被拒绝：rep={reply[1]}");
            }
            int skip = reply[3] switch
            {
                0x01 => 4 + 2,
                0x04 => 16 + 2,
                0x03 => 1 + (await ReadByteAsync(socket, timeout.Token)) + 2,
                _ => throw new IOException($"SOCKS5 未知地址类型 {reply[3]}"),
            };
            if (reply[3] == 0x03)
            {
                skip -= 1; // 长度字节已经读过
            }
            byte[] rest = new byte[skip];
            await ReadExactlyAsync(socket, rest, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<byte> ReadByteAsync(Socket socket, CancellationToken token)
    {
        byte[] one = new byte[1];
        await ReadExactlyAsync(socket, one, token);
        return one[0];
    }

    private static async Task ReadExactlyAsync(Socket socket, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, token);
            if (read == 0)
            {
                throw new IOException("SOCKS5 对端提前关闭");
            }
            offset += read;
        }
    }
}
