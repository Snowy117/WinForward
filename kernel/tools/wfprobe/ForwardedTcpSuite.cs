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

    private static WatchdogHandle? s_watchdog;
    private static int s_watchdogSeconds = 90;
    private static uint s_installedGeneration;

    public static async Task<int> RunAsync(Options options)
    {
        if (options.Has("restore-only"))
        {
            using var restoreDevice = new WfpDevice();
            if (Restore(restoreDevice, options.GetInt("listener-port", DefaultListenerPort),
                        options.GetInt("generation", -1)))
            {
                Console.WriteLine("[forwarded-tcp] restore-only rules=0 fallback=permit config=disabled " +
                                  $"generation={options.GetInt("generation", -1)}");
            }
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

        using OnLinkAlias? alias = SkipAlias(options, remoteAddress)
                                       ? null
                                       : OnLinkAlias.Ensure(remoteAddress, localIp, ifIndex, !options.Has("no-alias"));
        if (alias is not null && !alias.Ready)
        {
            Console.WriteLine($"[forwarded-tcp] alias 不可用：{alias.Error}");
            return 7;
        }
        int watchdogSeconds = options.GetInt("watchdog-seconds", Math.Max(90, (waitMs / 1000) + 60));
        s_watchdogSeconds = watchdogSeconds;
        s_watchdog = new WatchdogHandle();
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
                "multi" => await RunMultiAsync(device, options),
                "noingress" => await RunNoIngressAsync(device, options),

                "hotswap" => await RunHotSwapAsync(device, options),

                _ => throw new ArgumentException($"未知 phase：{phase}（core / upstreams / fail / multi / noingress / hotswap）"),


            };
        }
        finally
        {
            if (!options.Has("keep-blob"))
            {
                Restore(device, listenerPort, (int)s_installedGeneration);
            }

            s_watchdog?.Dispose();
            s_watchdog = null;
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

        using var listener = BindTcpAny(listenerPort, family);
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
            Check.True(SocketHelpers.SameAddress(session.ServerAddress, remoteAddress),
                       "redirect 表记录原始服务器地址", $"{session.ServerAddress}");
            Check.Equal(0, session.UpstreamId, "redirect 表记录 upstream_id");
            Check.True(session.BytesClientToServer > 0, "中继收到客户端数据", $"{session.BytesClientToServer}B");
            Check.True(session.BytesServerToClient > 0, "回程数据回到客户端", $"{session.BytesServerToClient}B");
            Check.True(session.CleanEof, "会话以半关闭/EOF 正常收尾", session.Detail);
        }

        if (sessions.Count > 0)
        {
            ProbeForwardedRedirect legacy = device.QueryForwardedRedirectLegacy(
                QueryFor(sessions[0].Local, sessions[0].Remote));
            Check.Equal(1, (long)legacy.Version, "core legacy query 返回 version=1");
            Check.True(legacy.ListenerIP is null, "core legacy query 不含 L", $"version={legacy.Version}");
            Check.Equal(remotePort, legacy.ServerPort, "core legacy query 仍能查回 server");
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
        await using var upstream0 = new Socks5FakeServer { DialOverride = backend };
        await using var upstream1 = new Socks5FakeServer { DialOverride = backend };
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

        using var listener = BindTcpAny(listenerPort, family);
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
        if (remoteAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Check.Equal(singleUpstream ? expectAccepts : 1, upstream0.V6Connections,
                        "upstream 0 收到 atyp=4 CONNECT");
            Check.Equal(singleUpstream ? 0 : 1, upstream1.V6Connections,
                        "upstream 1 收到 atyp=4 CONNECT");
        }

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

    private static TcpListener BindOn(string address, int port, uint ifIndex)
    {
        IPAddress parsed = SocketHelpers.WithScope(IPAddress.Parse(address), ifIndex);
        TcpListener listener = new(parsed, port);
        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            listener.Server.DualMode = false;
        }

        listener.Start();
        return listener;
    }

    private static TcpListener BindTcpAny(int port, byte family)
    {
        TcpListener listener = family == 6
                                   ? new TcpListener(IPAddress.IPv6Any, port)
                                   : new TcpListener(IPAddress.Any, port);
        if (family == 6)
        {
            listener.Server.DualMode = false;
        }

        return listener;
    }

    private static bool SkipAlias(Options options, string remoteAddress) =>
        options.Has("no-alias") && IPAddress.Parse(remoteAddress).AddressFamily == AddressFamily.InterNetworkV6;

    private static bool SameAddressSet(IEnumerable<IPAddress> actual, params IPAddress[] expected)
    {
        var remaining = new List<IPAddress>(expected);
        foreach (IPAddress item in actual)
        {
            int index = remaining.FindIndex(candidate => SocketHelpers.SameAddress(candidate, item));
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return remaining.Count == 0;
    }

    private static bool SameEndpointSet(IEnumerable<(IPAddress Address, int Port)> actual,
                                        params (IPAddress Address, int Port)[] expected)
    {
        var remaining = new List<(IPAddress Address, int Port)>(expected);
        foreach ((IPAddress address, int port) in actual)
        {
            int index = remaining.FindIndex(candidate => SocketHelpers.SameAddress(candidate.Address, address) &&
                                                         candidate.Port == port);
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return remaining.Count == 0;
    }

    private static async Task<int> RunMultiAsync(WfpDevice device, Options options)
    {
        string localIp = options.Get("local-ip") ?? "192.168.100.2";
        string secondLocalIp = options.Get("second-local-ip") ?? "192.168.100.98";
        string externalLocalIp = options.Get("external-local-ip") ?? "192.168.77.2";
        string remoteAddress = options.Get("remote-address") ?? "192.168.100.99";
        string externalRemoteAddress = options.Get("external-remote-address") ?? "192.168.77.99";
        int listenerPort = options.GetInt("listener-port", 40420);
        int remotePort = options.GetInt("remote-port", 40250);
        int secondRemotePort = options.GetInt("second-remote-port", remotePort + 1);
        IPEndPoint backend = ParseEndPoint(options.Get("backend") ?? DefaultBackend);
        int waitMs = options.GetInt("wait-ms", 30000);
        int timeoutMs = options.GetInt("timeout-ms", 15000);
        int sourcePortA = options.GetInt("source-port-a", 40500);
        int sourcePortB = options.GetInt("source-port-b", 40501);
        int payloadBytes = options.GetInt("bytes", 2048);

        uint internalIndex = options.Has("ifindex") ? options.GetUInt("ifindex", 0) : FindAdapterIndex(localIp) ?? 0;
        uint externalIndex = FindAdapterIndex(externalLocalIp) ?? 0;
        Check.True(internalIndex != 0 && externalIndex != 0, "multi 解析两个入口 ifIndex",
                   $"internal={internalIndex} external={externalIndex}");

        bool skipAlias = options.Has("no-alias") &&
                         IPAddress.Parse(remoteAddress).AddressFamily == AddressFamily.InterNetworkV6;
        using OnLinkAlias? secondL = skipAlias
                                         ? null
                                         : OnLinkAlias.Ensure(secondLocalIp, localIp, internalIndex, !options.Has("no-alias"));
        using OnLinkAlias? externalTarget = skipAlias
                                                ? null
                                                : OnLinkAlias.Ensure(
                                                    externalRemoteAddress, externalLocalIp, externalIndex,
                                                    !options.Has("no-alias"));
        if (secondL is not null && (!secondL.Ready || externalTarget is null || !externalTarget.Ready))
        {
            Console.WriteLine($"[forwarded-tcp] multi alias 不可用：{secondL.Error} / {externalTarget?.Error}");
            return 7;
        }

        ProbeForwardedIngress[] rows =
        {
            Ingress(internalIndex, IPAddress.Parse(localIp), (ushort)listenerPort),
            Ingress(internalIndex, IPAddress.Parse(secondLocalIp), (ushort)listenerPort),
            Ingress(externalIndex, IPAddress.Parse(externalLocalIp), (ushort)listenerPort),
        };
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                RedirectRule(internalIndex, null, IPAddress.Parse(remoteAddress), remotePort, secondRemotePort, 0),
                RedirectRule(externalIndex, null, IPAddress.Parse(externalRemoteAddress), remotePort, remotePort, 0),
            },
            Protocol.RuleActionPermit);
        device.SetForwardedIngressConfig(false, rows);
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo info = device.GetForwardedInfo();
        Check.Equal(1, info.Installed, "multi blob installed=1");
        Check.Equal(2, info.RuleCount, "multi 规则数回读一致");
        s_installedGeneration = info.Generation;
        device.SetForwardedIngressConfig(true, rows);
        device.ResetForwardedStats();

        var listeners = new List<TcpListener>();
        try
        {
            foreach (ProbeForwardedIngress row in rows)
            {
                listeners.Add(BindOn(row.ListenerIP!.ToString(), listenerPort, row.IfIndex));
            }

            Console.WriteLine($"[forwarded-tcp] READY phase=multi listener={listenerPort} " +
                              $"locals={localIp},{secondLocalIp},{externalLocalIp} " +
                              $"targets={remoteAddress}:{remotePort},{remoteAddress}:{secondRemotePort}," +
                              $"{externalRemoteAddress}:{remotePort} " +
                              $"source_ports={sourcePortA},{sourcePortB} bytes={payloadBytes}");
            Console.Out.Flush();


            var sessions = new List<SessionResult>();
            var relays = new List<Task<SessionResult>>();
            using var window = new CancellationTokenSource(waitMs);
            var acceptTasks = new Dictionary<Task<Socket>, TcpListener>();
            foreach (TcpListener listener in listeners)
            {
                acceptTasks[listener.AcceptSocketAsync(window.Token).AsTask()] = listener;
            }

            try
            {
                while (sessions.Count < 4)
                {
                    Task<Socket> ready = await Task.WhenAny(acceptTasks.Keys);
                    Socket client = await ready;
                    TcpListener listener = acceptTasks[ready];
                    acceptTasks.Remove(ready);
                    acceptTasks[listener.AcceptSocketAsync(window.Token).AsTask()] = listener;

                    var remote = (IPEndPoint)client.RemoteEndPoint!;
                    var local = (IPEndPoint)client.LocalEndPoint!;
                    sessions.Add(new SessionResult { Remote = remote, Local = local });
                    relays.Add(RelaySessionAsync(client, device, backend, null, window.Token, timeoutMs));
                }

                SessionResult[] results = await Task.WhenAll(relays);
                for (int index = 0; index < sessions.Count; index++)
                {
                    sessions[index].QueryOk = results[index].QueryOk;
                    sessions[index].ServerAddress = results[index].ServerAddress;
                    sessions[index].ServerPort = results[index].ServerPort;
                    sessions[index].IngressIfIndex = results[index].IngressIfIndex;
                    sessions[index].BytesClientToServer = results[index].BytesClientToServer;
                    sessions[index].BytesServerToClient = results[index].BytesServerToClient;
                    sessions[index].CleanEof = results[index].CleanEof;
                    sessions[index].Detail = results[index].Detail;
                }
            }
            catch (OperationCanceledException)
            {
                Check.True(false, "multi 在窗口内收到四条连接", $"accepted={sessions.Count}/4");
            }

            Thread.Sleep(200);
            ProbeForwardedStats stats = device.GetForwardedStats();
            PrintStats("multi", stats);

            Check.Equal(4, sessions.Count, "multi 收到四条连接");
            foreach (SessionResult session in sessions)
            {
                Check.True(session.QueryOk, "multi redirect 查询命中", session.Detail);
                Check.Equal(payloadBytes, session.BytesClientToServer, "multi 客户端字节数");
                Check.Equal(payloadBytes, session.BytesServerToClient, "multi 回程字节数");
            }

            SessionResult[] crossIngress = sessions.Where(session => session.Remote.Port == sourcePortA).ToArray();
            SessionResult[] sameIngress = sessions.Where(session => session.Remote.Port == sourcePortB).ToArray();
            Check.Equal(2, crossIngress.Length, "multi 跨入口组两条连接");
            Check.Equal(2, sameIngress.Length, "multi 同入口多 L 组两条连接");

            if (crossIngress.Length == 2)
            {
                Check.True(SameAddressSet(crossIngress.Select(session => session.Local.Address),
                                          IPAddress.Parse(localIp), IPAddress.Parse(externalLocalIp)),
                           "multi 跨入口使用两个 L",
                           string.Join(",", crossIngress.Select(session => session.Local.Address)));
                Check.True(SameEndpointSet(crossIngress.Select(session => (session.ServerAddress, session.ServerPort)),
                                           (IPAddress.Parse(remoteAddress), remotePort),
                                           (IPAddress.Parse(externalRemoteAddress), remotePort)),
                           "multi 跨入口回程记录两个原始目的",
                           string.Join(",", crossIngress.Select(session => $"{session.ServerAddress}:{session.ServerPort}")));
                var ingresses = crossIngress.Select(session => session.IngressIfIndex).ToHashSet();
                Check.True(ingresses.SetEquals(new[] { internalIndex, externalIndex }), "multi 跨入口 ingress 正确",
                           string.Join(",", ingresses));
            }

            if (sameIngress.Length == 2)
            {
                Check.True(SameAddressSet(sameIngress.Select(session => session.Local.Address),
                                          IPAddress.Parse(localIp), IPAddress.Parse(secondLocalIp)),
                           "multi 同入口并发使用两个 L",
                           string.Join(",", sameIngress.Select(session => session.Local.Address)));
                var ports = sameIngress.Select(session => session.ServerPort).ToHashSet();
                Check.True(ports.SetEquals(new[] { remotePort, secondRemotePort }),
                           "multi 同客户端端口两个目的并存", string.Join(",", ports));
                Check.True(sameIngress.All(session => SocketHelpers.SameAddress(session.ServerAddress,
                                                                                IPAddress.Parse(remoteAddress))),
                           "multi 同入口两个目的地址相同", remoteAddress);

                Check.True(sameIngress.All(session => session.IngressIfIndex == internalIndex),
                           "multi 同入口 ingress 相同", $"ifindex={internalIndex}");

            }

            Check.AtLeast(4, (long)stats.RedirectCreated, "multi 建表四次");
            Check.AtLeast(4, (long)stats.RedirectReturn, "multi 回程 SNAT");
            Check.AtLeast(1, (long)stats.RedirectMultiL, "multi 选中了非首个 L");
            Check.Equal(0, (long)stats.RedirectReturnAmbiguous, "multi 没有回程歧义");
            Check.Equal(0, (long)stats.RedirectNoIngress, "multi 没有未配置入口");

            // legacy/24B 风格的“任意 L”查询：同一客户端端口在两个 L 上各有一条表项，候选不唯一。
            // 控制查询必须返回 NOT_FOUND，但不能把 data-path 的 ambiguous 计数抬起来。
            if (sameIngress.Length == 2)
            {
                try
                {
                    _ = device.QueryForwardedRedirect(QueryWithoutListener(listenerPort, sameIngress[0].Remote));
                    Check.True(false, "multi 任意 L 查询应不唯一", $"client={sameIngress[0].Remote}");
                }
                catch (Win32Exception exception)
                {
                    Check.Equal(1168, exception.NativeErrorCode, "multi 任意 L 查询歧义返回 NOT_FOUND");
                }

                ProbeForwardedStats afterQuery = device.GetForwardedStats();
                Check.Equal(0, (long)afterQuery.RedirectReturnAmbiguous,
                            "multi 控制面查询不增加 data-path ambiguous");
            }

            CheckInvariants(stats, "multi");
            return Check.Failures == 0 ? 0 : 6;
        }
        finally
        {
            foreach (TcpListener listener in listeners)
            {
                listener.Stop();
            }
        }
    }
    private static async Task<int> RunNoIngressAsync(WfpDevice device, Options options)
    {
        string localIp = options.Get("local-ip") ?? "192.168.100.2";
        string externalLocalIp = options.Get("external-local-ip") ?? "192.168.77.2";
        string externalRemoteAddress = options.Get("external-remote-address") ?? "192.168.77.99";
        int listenerPort = options.GetInt("listener-port", 40430);
        int remotePort = options.GetInt("remote-port", 40250);
        int waitMs = options.GetInt("wait-ms", 8000);

        uint internalIndex = FindAdapterIndex(localIp) ?? 0;
        uint externalIndex = FindAdapterIndex(externalLocalIp) ?? 0;
        Check.True(internalIndex != 0 && externalIndex != 0, "noingress 解析两个入口 ifIndex",
                   $"internal={internalIndex} external={externalIndex}");

        using OnLinkAlias externalTarget = OnLinkAlias.Ensure(
            externalRemoteAddress, externalLocalIp, externalIndex, !options.Has("no-alias"));
        if (!externalTarget.Ready)
        {
            Console.WriteLine($"[forwarded-tcp] noingress alias 不可用：{externalTarget.Error}");
            return 7;
        }

        // 只配置 Internal 入口；规则只匹配 External 入口。External 帧必须 fail-closed，不能落 fallback permit。
        ProbeForwardedIngress[] rows =
        {
            Ingress(internalIndex, IPAddress.Parse(localIp), (ushort)listenerPort),
        };
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                RedirectRule(externalIndex, null, IPAddress.Parse(externalRemoteAddress), remotePort, remotePort, 0),
            },
            Protocol.RuleActionPermit);

        device.SetForwardedIngressConfig(false, rows);
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo info = device.GetForwardedInfo();
        Check.Equal(1, info.Installed, "noingress blob installed=1");
        Check.Equal(1, info.RuleCount, "noingress 规则数回读一致");
        s_installedGeneration = info.Generation;
        device.SetForwardedIngressConfig(true, rows);
        device.ResetForwardedStats();

        Console.WriteLine($"[forwarded-tcp] READY phase=noingress listener={listenerPort} " +
                          $"target={externalRemoteAddress}:{remotePort} wait={waitMs}");
        Console.Out.Flush();

        await Task.Delay(waitMs);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("noingress", stats);

        Check.AtLeast(1, (long)stats.RedirectNoIngress, "noingress 命中未配置入口");
        Check.AtLeast(1, (long)stats.Blocked, "noingress 原帧被 block");
        Check.Equal(0, (long)stats.RedirectCreated, "noingress 没有建表");
        Check.Equal(0, (long)stats.RedirectIngress, "noingress 没有 DNAT 注入");
        Check.Equal(0, (long)stats.RedirectReturnAmbiguous, "noingress 没有回程歧义");
        CheckInvariants(stats, "noingress");

        return Check.Failures == 0 ? 0 : 6;
    }


    private static async Task<int> RunHotSwapAsync(WfpDevice device, Options options)
    {
        string localIp = options.Get("local-ip") ?? "192.168.100.2";
        string remoteAddress = options.Get("remote-address") ?? "192.168.100.99";
        int listenerPort = options.GetInt("listener-port", 40421);
        int remotePort = options.GetInt("remote-port", 40250);
        IPEndPoint backend = ParseEndPoint(options.Get("backend") ?? DefaultBackend);
        int waitMs = options.GetInt("wait-ms", 30000);
        int timeoutMs = options.GetInt("timeout-ms", 15000);
        int payloadBytes = options.GetInt("bytes", 2048);
        int swapDelayMs = options.GetInt("swap-delay-ms", 1000);

        uint ifIndex = options.Has("ifindex") ? options.GetUInt("ifindex", 0) : FindAdapterIndex(localIp) ?? 0;
        IPAddress address = IPAddress.Parse(remoteAddress);
        ProbeForwardedIngress ingress = Ingress(ifIndex, IPAddress.Parse(localIp), (ushort)listenerPort);
        ForwardedTestRule rule = RedirectRule(ifIndex, null, address, remotePort, remotePort, 0);
        byte[] blob0 = ForwardedBlobBuilder.Build(new[] { rule }, Protocol.RuleActionPermit);
        device.SetForwardedIngressConfig(false, new[] { ingress });
        device.SetForwardedBlob(blob0);
        ProbeForwardedInfo before = device.GetForwardedInfo();
        s_installedGeneration = before.Generation;
        device.SetForwardedIngressConfig(true, new[] { ingress });
        device.ResetForwardedStats();

        await using var upstream0 = new Socks5FakeServer();
        await using var upstream1 = new Socks5FakeServer();
        var upstreams = new Dictionary<uint, Socks5FakeServer> { [0] = upstream0, [1] = upstream1 };

        using var listener = BindOn(localIp, listenerPort, ifIndex);
        Console.WriteLine($"[forwarded-tcp] READY phase=hotswap listener={listenerPort} " +
                          $"target={remoteAddress}:{remotePort} bytes={payloadBytes} swap_delay_ms={swapDelayMs}");
        Console.Out.Flush();

        using var window = new CancellationTokenSource(waitMs);
        try
        {
            Socket first = await listener.AcceptSocketAsync(window.Token);
            IPEndPoint firstLocal = (IPEndPoint)first.LocalEndPoint!;
            IPEndPoint firstRemote = (IPEndPoint)first.RemoteEndPoint!;
            ProbeForwardedRedirect firstEntry = device.QueryForwardedRedirect(QueryFor(firstLocal, firstRemote));
            Check.Equal(0, firstEntry.UpstreamId, "hotswap 旧连接保持 upstream 0");
            Check.Equal((long)before.Generation, firstEntry.Generation, "hotswap 旧表项 generation 不变");
            Task<SessionResult> firstRelay =
                RelaySessionAsync(first, device, backend, upstreams, window.Token, timeoutMs);

            await Task.Delay(swapDelayMs);

            byte[] blob1 = ForwardedBlobBuilder.Build(
                new[] { RedirectRule(ifIndex, null, address, remotePort, remotePort, 1) },
                Protocol.RuleActionPermit);
            device.SetForwardedBlob(blob1);
            ProbeForwardedInfo after = device.GetForwardedInfo();
            Check.True(after.Generation > before.Generation, "hotswap 规则 generation 增加",
                       $"{before.Generation} -> {after.Generation}");
            // 换 blob 之后外层 finally/看门狗必须用新 generation 恢复，否则会误判“过期”而跳过。
            s_installedGeneration = after.Generation;
            s_watchdog?.Dispose();
            s_watchdog = new WatchdogHandle();
            ArmWatchdog(device, listenerPort);

            Socket second = await listener.AcceptSocketAsync(window.Token);
            IPEndPoint secondLocal = (IPEndPoint)second.LocalEndPoint!;
            IPEndPoint secondRemote = (IPEndPoint)second.RemoteEndPoint!;
            ProbeForwardedRedirect secondEntry = device.QueryForwardedRedirect(QueryFor(secondLocal, secondRemote));
            Check.Equal(1, secondEntry.UpstreamId, "hotswap 新连接使用 upstream 1");
            Check.Equal((long)after.Generation, secondEntry.Generation, "hotswap 新表项 generation 是新一代");

            ProbeForwardedRedirect firstAgain = device.QueryForwardedRedirect(QueryFor(firstLocal, firstRemote));
            Check.Equal(0, firstAgain.UpstreamId, "hotswap 换表后旧连接仍映射 upstream 0");
            Check.Equal((long)before.Generation, firstAgain.Generation, "hotswap 换表后旧表项 generation 不变");

            Task<SessionResult> secondRelay =
                RelaySessionAsync(second, device, backend, upstreams, window.Token, timeoutMs);
            SessionResult[] results = await Task.WhenAll(firstRelay, secondRelay);

            Thread.Sleep(200);
            ProbeForwardedStats stats = device.GetForwardedStats();
            PrintStats("hotswap", stats);

            Check.True(results[0].QueryOk, "hotswap redirect 查询命中", results[0].Detail);
            Check.True(results[1].QueryOk, "hotswap redirect 查询命中", results[1].Detail);
            // 旧连接在换表后又发了一轮；旧表项/旧 upstream 仍应完整回程。
            Check.Equal(payloadBytes * 2, results[0].BytesClientToServer, "hotswap 旧连接换表后继续收发");
            Check.Equal(payloadBytes * 2, results[0].BytesServerToClient, "hotswap 旧连接换表后回程完整");
            Check.Equal(payloadBytes, results[1].BytesClientToServer, "hotswap 新连接字节数");
            Check.Equal(payloadBytes, results[1].BytesServerToClient, "hotswap 新连接回程字节数");


            Check.Equal(1, upstream0.Connections, "hotswap upstream 0 收到旧连接");
            Check.Equal(1, upstream1.Connections, "hotswap upstream 1 收到新连接");
            Check.AtLeast(1, (long)stats.TablesReplaced, "hotswap 至少替换一次规则表");
            Check.Equal(0, (long)stats.RedirectNoState, "hotswap 两条连接都命中表项");
            CheckInvariants(stats, "hotswap");
            return Check.Failures == 0 ? 0 : 6;
        }
        catch (OperationCanceledException)
        {
            Check.True(false, "hotswap 在窗口内收到两条连接", $"timeout_ms={waitMs}");
            return 6;
        }
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
        s_installedGeneration = info.Generation;
        device.SetForwardedIngressConfig(false, Array.Empty<ProbeForwardedIngress>());
        device.SetForwardedConfig(DisabledConfig(listenerPort));
        device.ResetForwardedStats();

        using var listener = BindTcpAny(listenerPort, family);
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
                    IPEndPoint requested = redirect.ServerIP.AddressFamily == AddressFamily.InterNetworkV6
                                               ? new IPEndPoint(redirect.ServerIP, redirect.ServerPort)
                                               : backend;
                    outbound = await Socks5TestClient.ConnectAsync(fake.EndPoint, requested, timeoutMs, token);
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
        if (!local.Address.Equals(IPAddress.Any) && !local.Address.Equals(IPAddress.IPv6Any))
        {
            byte[] listenerBytes = local.Address.GetAddressBytes();
            for (int index = 0; index < listenerBytes.Length; index++)
            {
                query.ListenerAddress[index] = listenerBytes[index];
            }
        }
        return query;

    }

    // 只带 (listener_port, client) 的 legacy 查询：ListenerAddress 留空，驱动按“任意 L”线性扫描。
    private static ProbeForwardedRedirectQuery QueryWithoutListener(int listenerPort, IPEndPoint client) =>
        QueryFor(new IPEndPoint(IPAddress.Any, listenerPort), client);


    private static unsafe void Install(WfpDevice device,
                                byte[] blob,
                                uint expectedRules,
                                int listenerPort,
                                string localIp,
                                byte family)
    {
        IPAddress address = IPAddress.Parse(localIp);
        uint ifIndex = FindAdapterIndex(localIp) ?? 0;
        Check.True(ifIndex != 0, "Install 解析入口 ifIndex", $"local-ip={localIp}");
        ProbeForwardedIngress ingress = Ingress(ifIndex, address, (ushort)listenerPort, family);
        device.SetForwardedIngressConfig(false, new[] { ingress });
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo info = device.GetForwardedInfo();
        Check.Equal(1, info.Installed, "forwarded blob installed=1");
        Check.Equal(expectedRules, info.RuleCount, "forwarded 规则数回读一致");
        s_installedGeneration = info.Generation;

        device.SetForwardedIngressConfig(true, new[] { ingress });
        (bool configEnabled, ProbeForwardedIngress[] ingresses) = device.GetForwardedIngressConfig();
        Check.True(configEnabled, "config 回读 enabled=1", $"count={ingresses.Length}");

        Check.Equal(1, ingresses.Length, "config 回读一条入口记录");
        Check.Equal((long)ifIndex, ingresses[0].IfIndex, "config 回读 ifindex");
        Check.Equal((long)listenerPort, ingresses[0].ListenerPort, "config 回读 listener_port");
        Check.Equal((long)family, ingresses[0].Family, "config 回读 family");
        ArmWatchdog(device, listenerPort);
    }


    private static unsafe ProbeForwardedIngress Ingress(uint ifIndex, IPAddress address, ushort port, byte family)
    {
        ProbeForwardedIngress ingress = default;
        ingress.IfIndex = ifIndex;
        ingress.ListenerPort = port;
        ingress.Family = family;
        byte[] bytes = address.GetAddressBytes();
        for (int index = 0; index < bytes.Length; index++)
        {
            ingress.ListenerAddress[index] = bytes[index];
        }
        return ingress;
    }

    private static unsafe ProbeForwardedIngress Ingress(uint ifIndex, IPAddress address, ushort port) =>
        Ingress(ifIndex, address, port,
                (byte)(address.AddressFamily == AddressFamily.InterNetwork ? 4 : 6));

    private static ProbeForwardedConfig DisabledConfig(int listenerPort) => new()

    {
        ListenerPort = (ushort)listenerPort,
    };

    private static bool Restore(WfpDevice device, int listenerPort, int expectedGeneration = -1)
    {
        bool restored = false;
        try
        {
            if (expectedGeneration >= 0)
            {
                ProbeForwardedInfo info = device.GetForwardedInfo();
                if (info.Installed != 1 || (long)info.Generation != expectedGeneration)
                {
                    Console.WriteLine($"[forwarded-tcp] 忽略过期的看门狗：expected_generation={expectedGeneration} " +
                                      $"installed={info.Installed} generation={info.Generation}");
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-tcp] 看门狗读取表状态失败：{exception.Message}");
            return false;
        }

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
            restored = true;
            Console.WriteLine("[forwarded-tcp] 已恢复 rules=0 fallback=permit config=disabled");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-tcp] 恢复空 blob 失败：{exception.Message}");
        }
        return restored;
    }

    private static void ArmWatchdog(WfpDevice device, int listenerPort)
    {
        s_watchdog?.Start(s_watchdogSeconds, listenerPort, device.GetForwardedInfo().Generation);
    }

    private sealed class WatchdogHandle : IDisposable
    {
        private Process? _process;

        public void Start(int seconds, int listenerPort, uint generation)
        {
            _process ??= StartRestoreWatchdog(seconds, listenerPort, generation);
        }

        public void Dispose()
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(true);
            }

            _process?.Dispose();
            _process = null;
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
                           stats.RedirectReturnFailed + stats.RedirectReturnAmbiguous + stats.UdpBlocked +
                           stats.UdpReturnFailed + stats.UdpReturnAmbiguous),
                    $"{name}：blocked == matched_block + fallback_block + redirect 失败 + 歧义 + udp 失败");

        Check.Equal((long)stats.Matched,
                    (long)(stats.MatchedPermit + stats.MatchedBlock + stats.MatchedRedirect),
                    $"{name}：matched == permit + block + redirect");
        Check.Equal((long)(stats.MatchedRedirect + stats.UdpFragmented),
                    (long)(stats.RedirectIngress + stats.RedirectBlocked),
                    $"{name}：matched_redirect + udp_fragmented == ingress + blocked");
        Check.Equal((long)stats.RedirectBlocked,
                    (long)(stats.RedirectNoConfig + stats.RedirectNoState + stats.RedirectNoIngress +
                           stats.RedirectCapacity + stats.RedirectNonTcp + stats.RedirectInjectFailed),

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
            // acked 来自客户端 TCP_INFO.tcpi_bytes_acked：本机内核把 SYN 也计入 1 字节，
            // 所以“全部发送数据被 ACK”的区间是 [payload, payload+1]；客户端用同一口径。
            Check.True(record.Acked >= plan.Bytes && record.Acked <= plan.Bytes + 1,
                       "客户端 TCP 已确认全部发送字节（tcpi_bytes_acked）",
                       $"expected={plan.Bytes}..{plan.Bytes + 1} actual={record.Acked}");
        }
    }

    private static Process? StartRestoreWatchdog(int seconds, int listenerPort, uint generation)
    {
        string? exe = Environment.ProcessPath;
        string? shell = Environment.GetEnvironmentVariable("COMSPEC");
        if (exe is null || shell is null)
        {
            return null;
        }

        string logPath = Path.Combine(Path.GetTempPath(), $"wfprobe-watchdog-{listenerPort}.log");
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/c ping -n {seconds + 1} 127.0.0.1 >nul & \"{exe}\" forwarded-tcp --restore-only " +
                        $"--listener-port {listenerPort} --generation {generation} >> \"{logPath}\" 2>&1",
        };
        try
        {
            Process? process = Process.Start(startInfo);
            Console.WriteLine($"[forwarded-tcp] 看门狗已启动：{seconds}s 后恢复空 permit 表与 disabled config " +
                              $"generation={generation} log={logPath}");
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
                if (!SocketHelpers.SameAddress(unicast.Address, wanted))
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
    private int _v6Connections;

    public Socks5FakeServer()
    {
        _listener.Start();
        EndPoint = (IPEndPoint)_listener.LocalEndpoint;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public IPEndPoint EndPoint { get; }

    public IPEndPoint? DialOverride { get; set; }

    public int Connections => Volatile.Read(ref _connections);

    public int V6Connections => Volatile.Read(ref _v6Connections);

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

    private async Task ServeAsync(Socket client)
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
                if (destination.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    Interlocked.Increment(ref _v6Connections);
                }

                await client.SendAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });

                IPEndPoint dial = DialOverride ?? new IPEndPoint(destination, port);
                using Socket outbound = new(dial.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                await outbound.ConnectAsync(dial, _stopToken);
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
