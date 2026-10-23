using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinForward.Probe;

// B5 的 forwarded UDP 验证套件。驱动负责入口 DNAT 与回程 SNAT；本工具负责 listener、redirect 表查询、
// 直连或 SOCKS5 UDP 中继，并让外部 Linux 客户端通过 verify 端口回报实际收到的字节数、SHA 与源地址。
internal static class ForwardedUdpSuite
{
    private const int DefaultListenerPort = 40400;
    private const int DefaultRemotePort = 40250;
    private const string DefaultRemoteAddress = "192.168.100.99";
    private const string DefaultBackend = "192.168.100.4:40250";

    private static WatchdogHandle? s_watchdog;
    private static int s_watchdogSeconds = 120;
    private static uint s_installedGeneration;

    public static async Task<int> RunAsync(Options options)
    {
        if (options.Has("restore-only"))
        {
            using var restoreDevice = new WfpDevice();
            if (Restore(restoreDevice, options.GetInt("listener-port", DefaultListenerPort),
                        options.GetInt("generation", -1)))
            {
                Console.WriteLine("[forwarded-udp] restore-only rules=0 fallback=permit config=disabled " +
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
        int expectDatagrams = options.GetInt("expect-datagrams", 10);
        int expectCreated = options.GetInt("expect-created", 1);
        int verifyTimeoutMs = options.GetInt("verify-timeout-ms", 20000);
        int capacity = options.GetInt("capacity", 4096);
        int sweepBase = options.GetInt("sweep-base", 41001);
        string? clientCidr = options.Get("client-cidr");
        int verifyPort = options.GetInt("verify-port", listenerPort + 1000);
        uint ifIndex = options.Has("ifindex") ? options.GetUInt("ifindex", 0) : FindAdapterIndex(localIp) ?? 0;

        Check.True(ifIndex != 0, "解析入口 ifIndex", $"ifindex={ifIndex} local-ip={localIp}");
        IPAddress localAddress = IPAddress.Parse(localIp);
        byte family = (byte)(localAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 6);

        using var device = new WfpDevice();
        ProbeStats stats = device.GetStats();
        Check.Equal(Protocol.AbiVersion, (long)stats.AbiVersion, "驱动 ABI 版本与夹具一致");

        // OnLinkAlias 只支持 IPv4；IPv6 分片用链路本地地址，不需要别名（调用方需显式 --no-alias）。
        bool skipAlias = IPAddress.Parse(remoteAddress).AddressFamily == AddressFamily.InterNetworkV6 &&
                         options.Has("no-alias");
        using OnLinkAlias? alias = skipAlias
                                       ? null
                                       : OnLinkAlias.Ensure(remoteAddress, localIp, ifIndex, !options.Has("no-alias"));
        if (alias is not null && !alias.Ready)
        {
            Console.WriteLine($"[forwarded-udp] alias 不可用：{alias.Error}");
            return 7;
        }

        int watchdogSeconds = options.GetInt("watchdog-seconds", Math.Max(120, (waitMs / 1000) + 60));
        s_watchdogSeconds = watchdogSeconds;
        s_watchdog = new WatchdogHandle();
        try
        {
            return phase switch
            {
                "core" => await RunCoreAsync(device, ifIndex, listenerPort, localIp, family, IPAddress.Parse(remoteAddress),
                                             remotePort, backend, clientCidr, waitMs, expectDatagrams, expectCreated,
                                             verifyPort, verifyTimeoutMs, options.Has("no-verify")),
                "socks5" => await RunSocks5Async(device, ifIndex, listenerPort, localIp, family,
                                                 IPAddress.Parse(remoteAddress), remotePort, clientCidr, waitMs,
                                                 expectDatagrams, verifyPort, verifyTimeoutMs),
                "fail" => await RunFailAsync(device, ifIndex, listenerPort, localIp, family,
                                             IPAddress.Parse(remoteAddress), remotePort, clientCidr, waitMs,
                                             verifyTimeoutMs),
                "fragment" => await RunFragmentAsync(device, ifIndex, listenerPort, localIp, family,
                                                     IPAddress.Parse(remoteAddress), remotePort, clientCidr,
                                                     waitMs, options.GetInt("expect-fragments", 9),
                                                     options.GetInt("fragment-timeout-ms", 8000),
                                                     options.GetInt("fragment-permit-first-port", 0)),
                "fragment-host" => await RunFragmentHostAsync(device, ifIndex, listenerPort, localIp, family,
                                                              IPAddress.Parse(remoteAddress), remotePort, clientCidr,
                                                              waitMs, IPAddress.Parse(options.Get("host-fragment-address") ?? localIp),
                                                              options.GetInt("host-fragment-port", 40600),
                                                              options.GetInt("fragment-timeout-ms", 8000)),
                "capacity" => await RunCapacityAsync(device, ifIndex, listenerPort, localIp, family,
                                                     IPAddress.Parse(remoteAddress), remotePort, clientCidr,
                                                     waitMs, capacity, sweepBase),
                "reject" => await RunRejectAsync(device, ifIndex, listenerPort, localIp, family,
                                                 IPAddress.Parse(remoteAddress), remotePort, clientCidr,
                                                 waitMs),
                _ => throw new ArgumentException($"未知 phase：{phase}（core / socks5 / fail / fragment / fragment-host / capacity / reject）"),
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

    private static async Task<int> RunCoreAsync(WfpDevice device, uint ifIndex, int listenerPort, string localIp,
                                                byte family, IPAddress remoteAddress, int remotePort,
                                                IPEndPoint backend, string? clientCidr, int waitMs,
                                                int expectDatagrams, int expectCreated, int verifyPort,
                                                int verifyTimeoutMs, bool noVerify)
    {
        byte[] blob = ForwardedBlobBuilder.Build(
            new[] { RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0) },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = BindUdp(listenerPort);
        using var backendSocket = BindEphemeralUdp();
        using Socket? decoy = TryBindDecoy(remoteAddress, remotePort);
        using TcpListener? verifyListener = noVerify ? null : StartVerifyListener(verifyPort);
        Console.WriteLine($"[forwarded-udp] READY phase=core listener={listenerPort} " +
                          $"verify_port={(noVerify ? "skipped" : verifyPort.ToString())} " +
                          $"remote={remoteAddress}:{remotePort} backend={backend} datagrams={expectDatagrams} " +
                          $"created={expectCreated} decoy={(decoy is null ? "unbound" : "bound")}");
        Console.Out.Flush();

        var records = new List<DatagramRecord>();
        using var window = new CancellationTokenSource(waitMs);
        try
        {
            byte[] buffer = new byte[65535];
            byte[] echoBuffer = new byte[65535];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (records.Count < expectDatagrams)
            {
                SocketReceiveFromResult received =
                    await listener.ReceiveFromAsync(buffer, SocketFlags.None, any, window.Token);
                if (received.RemoteEndPoint is not IPEndPoint client || received.ReceivedBytes <= 0)
                {
                    continue;
                }

                DatagramRecord record = QueryRecord(device, new IPEndPoint(IPAddress.Parse(localIp), listenerPort), client);
                records.Add(record);
                byte[] payload = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                await backendSocket.SendToAsync(payload, SocketFlags.None, backend, window.Token);
                SocketReceiveFromResult echo =
                    await backendSocket.ReceiveFromAsync(echoBuffer, SocketFlags.None, any, window.Token);
                await listener.SendToAsync(echoBuffer.AsMemory(0, echo.ReceivedBytes), SocketFlags.None, client,
                                           window.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Check.True(false, "在等待窗口内收到全部 datagram", $"received={records.Count}/{expectDatagrams}");
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("core", stats);

        foreach (DatagramRecord record in records)
        {
            Check.True(record.QueryOk, "UDP redirect 查询命中", record.Detail);
            Check.Equal(remotePort, record.ServerPort, "redirect 表记录原始服务器端口");
            Check.True(record.ServerAddress.Equals(remoteAddress), "redirect 表记录原始服务器地址",
                       $"{record.ServerAddress}");
            Check.Equal(0, record.UpstreamId, "redirect 表记录 upstream_id");
            Check.Equal((byte)Protocol.RuleProtocolUdp, record.Protocol, "redirect 表 protocol=17");
        }

        Check.AtLeast(expectCreated, (long)stats.UdpCreated, "datagram 建表次数");
        if (expectCreated > 1)
        {
            Check.AtLeast(expectCreated - 1, (long)stats.UdpExpired, "超过空闲阈值的表项被回收");
        }
        else
        {
            Check.Equal(0, (long)stats.UdpExpired, "未跨越超时阈值时没有 expired");
        }
        Check.AtLeast(expectDatagrams, (long)stats.UdpIngress, "入口 DNAT 注入全部 datagram");
        Check.AtLeast(expectDatagrams, (long)stats.UdpReturn, "回程 SNAT 注入全部 datagram");
        Check.AtLeast(1, (long)stats.SelfInjected, "注入帧被防环闸识别");
        Check.Equal(0, (long)stats.UdpNoConfig, "配置齐全：没有 udp_no_config");
        Check.Equal(0, (long)stats.UdpCapacity, "没有容量失败");
        Check.Equal(0, (long)stats.UdpInjectFailed, "没有注入失败");
        Check.Equal(0, (long)stats.UdpReturnMissBlocked, "没有混合 NBL 丢弃");

        CheckInvariants(stats, "core");

        Check.True(decoy is not null, "U7：decoy 绑定原始目的地",
                   decoy is null ? $"绑定 {remoteAddress}:{remotePort} 失败" : "bound");
        int decoyDatagrams = 0;
        long decoyBytesTotal = 0;
        string decoyDetail = "无数据";
        if (decoy is not null && decoy.Poll(200_000, SelectMode.SelectRead))
        {
            byte[] decoyBuffer = new byte[65535];
            while (decoy.Available > 0)
            {
                EndPoint decoyPeer = new IPEndPoint(IPAddress.Any, 0);
                int decoyBytes = decoy.ReceiveFrom(decoyBuffer, ref decoyPeer);
                if (decoyBytes <= 0)
                {
                    break;
                }

                ++decoyDatagrams;
                decoyBytesTotal += decoyBytes;
                string preview = Convert.ToHexString(decoyBuffer.AsSpan(0, Math.Min(decoyBytes, 16)));
                decoyDetail = $"datagrams={decoyDatagrams} bytes={decoyBytesTotal} from={decoyPeer} head={preview}";
            }
        }

        // 断言前确认窗口仍然有效：规则表被外部恢复（典型是上一轮被 kill 的套件遗留的看门狗）时，
        // 原帧会按 fallback permit 落到本机栈并被 decoy 收到。这必须报成窗口失效，不能当成驱动回归。
        ProbeForwardedInfo windowInfo = device.GetForwardedInfo();
        ProbeForwardedConfig windowConfig = device.GetForwardedConfig();
        Check.True(windowInfo.Installed == 1 && windowInfo.RuleCount == 1 && windowConfig.Enabled == 1,
                   "U7：断言窗口内 forwarded 表与 config 仍然生效（没有被动过）",
                   $"installed={windowInfo.Installed} rules={windowInfo.RuleCount} " +
                   $"config_enabled={windowConfig.Enabled} generation={windowInfo.Generation} " +
                   $"fallback_permit={stats.FallbackPermit} matched_redirect={stats.MatchedRedirect}");
        Check.True(decoyDatagrams == 0,
                   "U7：原始目的地没有收到未改写帧（decoy 无数据）", decoyDetail);


        if (!noVerify)
        {
            string expectedSha = ExpectedPayloadSha(expectDatagrams);
            List<VerifyRecord> verified = await CollectVerificationAsync(verifyListener!, 1, expectDatagrams,
                                                                         expectedSha, verifyTimeoutMs);
            Check.Equal(1, verified.Count, "客户端回报了一条验证记录");
            if (verified.Count == 1)
            {
                VerifyRecord verifiedRecord = verified[0];
                Check.Equal(expectDatagrams, verifiedRecord.Count, "客户端收到全部回包");
                Check.Equal(expectDatagrams * PayloadSize, verifiedRecord.Bytes, "客户端实收字节数");
                Check.Equal(remotePort, verifiedRecord.TargetPort, "客户端看到的源端口是原始服务器端口");
                Check.True(verifiedRecord.SourceOk, "客户端看到的源地址是原始服务器地址", $"{verifiedRecord.Source}");
                Check.True(string.Equals(verifiedRecord.Sha, expectedSha, StringComparison.Ordinal),
                           "客户端收到的字节 SHA 一致", $"expected={expectedSha[..12]} actual={verifiedRecord.Sha}");
            }
        }

        return Check.Failures == 0 ? 0 : 6;
    }

    private static async Task<int> RunFragmentAsync(WfpDevice device, uint ifIndex, int listenerPort, string localIp,
                                                    byte family, IPAddress remoteAddress, int remotePort,
                                                    string? clientCidr, int waitMs, int expectFragments,
                                                    int fragmentTimeoutMs, int permitFirstPort)
    {
        ForwardedTestRule redirect = RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0);
        ForwardedTestRule[] rules = permitFirstPort > 0
            ? new[] { PermitRule(ifIndex, clientCidr, remoteAddress, permitFirstPort, permitFirstPort), redirect }
            : new[] { redirect };
        byte[] blob = ForwardedBlobBuilder.Build(rules, Protocol.RuleActionPermit);
        Install(device, blob, permitFirstPort > 0 ? 2u : 1u, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = BindUdpFor(listenerPort, remoteAddress.AddressFamily);
        Console.WriteLine($"[forwarded-udp] READY phase=fragment listener={listenerPort} " +
                          $"remote={remoteAddress}:{remotePort} expect_fragments={expectFragments} " +
                          $"permit_first_port={permitFirstPort} expect=no-datagram");

        Console.Out.Flush();

        bool received = false;
        EndPoint fragmentAny = remoteAddress.AddressFamily == AddressFamily.InterNetworkV6
                                   ? new IPEndPoint(IPAddress.IPv6Any, 0)
                                   : new IPEndPoint(IPAddress.Any, 0);
        using (var window = new CancellationTokenSource(Math.Min(waitMs, fragmentTimeoutMs)))
        {
            try
            {
                _ = await listener.ReceiveFromAsync(new byte[65535], SocketFlags.None,
                                                    fragmentAny, window.Token);
                received = true;
            }
            catch (OperationCanceledException)
            {
                // 预期：分片在规则匹配之前被 block，listener 收不到任何 datagram。
            }
        }

        Thread.Sleep(300);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("fragment", stats);

        Check.True(!received, "U6/U1：分片 datagram 不会到达 listener", $"received={received}");
        Check.Equal(expectFragments, (long)stats.UdpFragmented, "每个分片都精确计入 udp_fragmented");
        Check.Equal(expectFragments, (long)stats.UdpBlocked, "分片全部精确计入 udp_blocked");
        Check.Equal(0, (long)stats.UdpCreated, "分片不建表");
        Check.Equal(0, (long)stats.UdpIngress, "分片没有 DNAT 注入");
        Check.Equal(0, (long)stats.UdpReturn, "分片没有回程注入");
        Check.Equal((long)stats.UdpFragmented, (long)stats.UdpBlocked, "分片：udp_blocked == udp_fragmented");
        CheckInvariants(stats, "fragment");
        return Check.Failures == 0 ? 0 : 6;
    }

    private static async Task<int> RunFragmentHostAsync(WfpDevice device, uint ifIndex, int listenerPort,
                                                        string localIp, byte family, IPAddress remoteAddress,
                                                        int remotePort, string? clientCidr, int waitMs,
                                                        IPAddress hostFragmentAddress,
                                                        int hostFragmentPort, int fragmentTimeoutMs)
    {
        // 同一 remote IP 上，未命中 redirect 端口的 host 分片必须按原路径重组并送达。
        byte[] blob = ForwardedBlobBuilder.Build(
            new[] { RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0) },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var host = BindUdpOn(hostFragmentPort, hostFragmentAddress);
        Console.WriteLine($"[forwarded-udp] READY phase=fragment-host host={hostFragmentAddress}:{hostFragmentPort} " +
                          $"remote={remoteAddress}:{remotePort} expect=host-datagram");

        Console.Out.Flush();

        bool received = false;
        int receivedBytes = 0;
        using (var window = new CancellationTokenSource(Math.Min(waitMs, fragmentTimeoutMs)))
        {
            try
            {
                EndPoint hostAny = hostFragmentAddress.AddressFamily == AddressFamily.InterNetworkV6
                                       ? new IPEndPoint(IPAddress.IPv6Any, 0)
                                       : new IPEndPoint(IPAddress.Any, 0);
                var result = await host.ReceiveFromAsync(new byte[65535], SocketFlags.None,
                                                         hostAny, window.Token);
                received = result.ReceivedBytes > 0;
                receivedBytes = result.ReceivedBytes;
            }
            catch (OperationCanceledException)
            {
                // 下面断言失败并打印 host 收不到。
            }
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("fragment-host", stats);

        Check.True(received, "U6/范围：host UDP 分片重组后到达本机 socket", $"received={received}");
        Check.AtLeast(1, receivedBytes, "host UDP 分片载荷非空");
        Check.Equal(0, (long)stats.UdpFragmented, "host 分片没有被 forwarded 分片闸计数");
        Check.Equal(0, (long)stats.UdpBlocked, "host 分片没有被 forwarded 分片闸 block");
        Check.Equal(0, (long)stats.UdpCreated, "host 分片不建 forwarded 表项");
        CheckInvariants(stats, "fragment-host");
        return Check.Failures == 0 ? 0 : 6;
    }


    private static async Task<int> RunCapacityAsync(WfpDevice device, uint ifIndex, int listenerPort, string localIp,
                                                    byte family, IPAddress remoteAddress, int remotePort,
                                                    string? clientCidr, int waitMs, int capacity, int sweepBase)
    {
        if (capacity <= 0 || capacity > 65536)
        {
            Console.WriteLine($"[forwarded-udp] capacity 参数非法：{capacity}");
            return 2;
        }

        // 生产容量 4096 可以直接用 4096 个不同源端口压满：回程 key 含 client_port，不依赖多个客户端 IP。
        byte[] blob = ForwardedBlobBuilder.Build(
            new[] { RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0) },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = BindUdp(listenerPort);
        Console.WriteLine($"[forwarded-udp] READY phase=capacity listener={listenerPort} " +
                          $"remote={remoteAddress}:{remotePort} capacity={capacity} sweep={capacity + 1} base={sweepBase}");
        Console.Out.Flush();

        int received = 0;
        using (var window = new CancellationTokenSource(Math.Max(waitMs, 180000)))
        {
            byte[] buffer = new byte[65535];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (received < capacity)
            {
                using var slice = CancellationTokenSource.CreateLinkedTokenSource(window.Token);
                slice.CancelAfter(5000);
                try
                {
                    SocketReceiveFromResult result =
                        await listener.ReceiveFromAsync(buffer, SocketFlags.None, any, slice.Token);
                    if (result.ReceivedBytes > 0)
                    {
                        ++received;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Thread.Sleep(500);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("capacity", stats);

        Check.Equal(capacity, received, "capacity：listener 收到全部 DNAT datagram");
        Check.Equal(capacity, (long)stats.UdpCreated, "capacity：表项数等于容量");
        Check.Equal(1, (long)stats.UdpCapacity, "capacity：第 N+1 条触发 udp_capacity");
        Check.Equal(1, (long)stats.UdpBlocked, "capacity：第 N+1 条被 block");
        Check.Equal(capacity, (long)stats.UdpIngress, "capacity：容量内的 datagram 全部注入");
        Check.Equal(capacity + 1, (long)(stats.UdpIngress + stats.UdpBlocked),
                    "capacity：每条 datagram 都成功注入或被 block");
        CheckInvariants(stats, "capacity");
        return Check.Failures == 0 ? 0 : 6;
    }


    // 夹具级 reject：Socks5UdpFakeServer 是进程内假上游，只验证“关联失败不回退直连”的驱动行为。
    // 产品级 reject（WinForward.Controller + ForwardedUdpRelay）由 tools/windows/forwarded-udp-reject-e2e.ps1 验证。
    private static async Task<int> RunRejectAsync(WfpDevice device, uint ifIndex, int listenerPort, string localIp,
                                                  byte family, IPAddress remoteAddress, int remotePort,
                                                  string? clientCidr, int waitMs)
    {
        await using var upstream = new Socks5UdpFakeServer(rejectAssociate: true);
        byte[] blob = ForwardedBlobBuilder.Build(
            new[] { RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0) },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = BindUdp(listenerPort);
        Console.WriteLine($"[forwarded-udp] READY phase=reject listener={listenerPort} " +
                          $"remote={remoteAddress}:{remotePort} upstream={upstream.EndPoint} expect=no-response");
        Console.Out.Flush();

        int received = 0;
        int failed = 0;
        using (var window = new CancellationTokenSource(Math.Min(waitMs, 10000)))
        {
            byte[] buffer = new byte[65535];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (received < 3)
            {
                SocketReceiveFromResult result =
                    await listener.ReceiveFromAsync(buffer, SocketFlags.None, any, window.Token);
                if (result.RemoteEndPoint is not IPEndPoint client || result.ReceivedBytes <= 0)
                {
                    continue;
                }

                ++received;
                DatagramRecord record = QueryRecord(device, new IPEndPoint(IPAddress.Parse(localIp), listenerPort), client);
                Check.True(record.QueryOk, "reject：UDP redirect 查询命中", record.Detail);
                try
                {
                    await Socks5UdpProbeClient.ConnectAsync(upstream.EndPoint, window.Token);
                    Check.True(false, "reject：上游应拒绝 UDP ASSOCIATE", "ASSOCIATE 意外成功");
                }
                catch (Exception exception) when (exception is IOException or SocketException or System.ComponentModel.Win32Exception)
                {
                    ++failed;
                    Console.WriteLine($"[forwarded-udp] reject：UDP ASSOCIATE 失败（预期）{exception.GetType().Name}: {exception.Message}");
                }
            }
        }

        Thread.Sleep(300);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("reject", stats);

        Check.Equal(3, received, "reject：listener 收到全部 datagram");
        Check.Equal(3, failed, "reject：每条 datagram 都尝试上游且失败");
        Check.Equal(0, upstream.Associations, "reject：没有成功建立 SOCKS5 UDP 关联");
        Check.AtLeast(3, upstream.AssociationFailures, "reject：上游记录到 ASSOCIATE 失败");
        Check.Equal(0, (long)stats.UdpReturn, "reject：没有回程注入（不直连）");
        CheckInvariants(stats, "reject");
        return Check.Failures == 0 ? 0 : 6;
    }


    private static async Task<int> RunSocks5Async(WfpDevice device, uint ifIndex, int listenerPort, string localIp,
                                                  byte family, IPAddress remoteAddress, int remotePort,
                                                  string? clientCidr, int waitMs, int expectDatagrams,
                                                  int verifyPort, int verifyTimeoutMs)
    {
        await using var upstream0 = new Socks5UdpFakeServer();
        await using var upstream1 = new Socks5UdpFakeServer();
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0),
                RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort + 1, remotePort + 1, 1),
            },
            Protocol.RuleActionPermit);
        Install(device, blob, 2, listenerPort, localIp, family);
        device.ResetForwardedStats();

        using var listener = BindUdp(listenerPort);
        using var verifyListener = StartVerifyListener(verifyPort);
        var associations = new Dictionary<AssociationKey, Socks5UdpProbeClient>();
        Console.WriteLine($"[forwarded-udp] READY phase=socks5 listener={listenerPort} verify_port={verifyPort} " +
                          $"remote={remoteAddress}:{remotePort},{remotePort + 1} datagrams={expectDatagrams} " +
                          $"socks5={upstream0.EndPoint},{upstream1.EndPoint}");
        Console.Out.Flush();

        var records = new List<DatagramRecord>();
        using var window = new CancellationTokenSource(waitMs);
        try
        {
            while (records.Count < expectDatagrams * 2)
            {
                byte[] buffer = new byte[65535];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                SocketReceiveFromResult received =
                    await listener.ReceiveFromAsync(buffer, SocketFlags.None, any, window.Token);
                if (received.RemoteEndPoint is not IPEndPoint client || received.ReceivedBytes <= 0)
                {
                    continue;
                }

                DatagramRecord record = QueryRecord(device, new IPEndPoint(IPAddress.Parse(localIp), listenerPort), client);
                records.Add(record);
                AssociationKey key = new(client, record.ServerAddress, record.ServerPort, record.UpstreamId);
                if (!associations.TryGetValue(key, out Socks5UdpProbeClient? association))
                {
                    IPEndPoint proxy = record.UpstreamId == 0 ? upstream0.EndPoint : upstream1.EndPoint;
                    association = await Socks5UdpProbeClient.ConnectAsync(proxy, window.Token);
                    associations[key] = association;
                }

                byte[] payload = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                await association.SendAsync(record.ServerAddress, record.ServerPort, payload, window.Token);
                int length = await association.ReceiveAsync(window.Token);
                int payloadOffset = Socks5UdpProbeClient.PayloadOffset(association.LastReceived);
                await listener.SendToAsync(association.LastReceived.AsMemory(payloadOffset, length - payloadOffset),
                                           SocketFlags.None, client, window.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Check.True(false, "在等待窗口内收到全部 datagram", $"received={records.Count}/{expectDatagrams * 2}");
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("socks5", stats);

        Check.Equal(1, upstream0.Associations, "upstream 0 只收到一次 UDP ASSOCIATE（关联复用）");
        Check.Equal(1, upstream1.Associations, "upstream 1 只收到一次 UDP ASSOCIATE（关联复用）");
        Check.Equal(0, upstream0.AssociationFailures, "upstream 0 没有失败");
        Check.Equal(0, upstream1.AssociationFailures, "upstream 1 没有失败");
        Check.AtLeast(2, (long)stats.UdpCreated, "两个目的地各建一条表项");
        Check.AtLeast(expectDatagrams * 2, (long)stats.UdpIngress, "入口 DNAT 注入全部 datagram");
        Check.AtLeast(expectDatagrams * 2, (long)stats.UdpReturn, "回程 SNAT 注入全部 datagram");
        Check.Equal(0, (long)stats.UdpNoConfig, "配置齐全：没有 udp_no_config");
        CheckInvariants(stats, "socks5");

        foreach (AssociationKey key in associations.Keys)
        {
            Check.True(key.UpstreamId is 0 or 1, "关联映射到 upstream 0/1", $"upstream={key.UpstreamId}");
        }

        string expectedSha = ExpectedPayloadSha(expectDatagrams);
        List<VerifyRecord> verified = await CollectVerificationAsync(verifyListener, 2, expectDatagrams, expectedSha,
                                                                     verifyTimeoutMs);
        Check.Equal(2, verified.Count, "两个客户端都回报了验证记录");
        foreach (VerifyRecord verifiedRecord in verified)
        {
            Check.Equal(expectDatagrams, verifiedRecord.Count, $"客户端 {verifiedRecord.ClientPort} 收到全部回包");
            Check.True(verifiedRecord.SourceOk, $"客户端 {verifiedRecord.ClientPort} 看到的源地址是原始服务器",
                       $"{verifiedRecord.Source}");
            Check.True(string.Equals(verifiedRecord.Sha, expectedSha, StringComparison.Ordinal),
                       $"客户端 {verifiedRecord.ClientPort} 收到的字节 SHA 一致",
                       $"expected={expectedSha[..12]} actual={verifiedRecord.Sha}");
        }

        foreach (Socks5UdpProbeClient association in associations.Values)
        {
            await association.DisposeAsync();
        }

        return Check.Failures == 0 ? 0 : 6;
    }

    private static async Task<int> RunFailAsync(WfpDevice device, uint ifIndex, int listenerPort, string localIp,
                                                byte family, IPAddress remoteAddress, int remotePort,
                                                string? clientCidr, int waitMs, int verifyTimeoutMs)
    {
        byte[] blob = ForwardedBlobBuilder.Build(
            new[] { RedirectRule(ifIndex, clientCidr, remoteAddress, remotePort, remotePort, 0) },
            Protocol.RuleActionPermit);
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo info = device.GetForwardedInfo();
        Check.Equal(1, info.Installed, "redirect 规则可以下发");
        s_installedGeneration = info.Generation;
        device.SetForwardedConfig(DisabledConfig(listenerPort));
        device.ResetForwardedStats();

        using var listener = BindUdp(listenerPort);
        Console.WriteLine($"[forwarded-udp] READY phase=fail listener={listenerPort} " +
                          $"verify_port={listenerPort + 1000} remote={remoteAddress}:{remotePort} expect=no-datagram");
        Console.Out.Flush();

        bool received = false;
        using (var window = new CancellationTokenSource(Math.Min(waitMs, 5000)))
        {
            try
            {
                _ = await listener.ReceiveFromAsync(new byte[65535], SocketFlags.None,
                                                    new IPEndPoint(IPAddress.Any, 0), window.Token);
                received = true;
            }
            catch (OperationCanceledException)
            {
                // 预期：没有 forwarded config 时入口帧被 block，listener 收不到 datagram。
            }
        }

        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("fail", stats);

        Check.True(!received, "无 forwarded config 时 datagram 不会到达 listener", $"received={received}");
        Check.AtLeast(1, (long)stats.UdpNoConfig, "udp_no_config 增加");
        Check.AtLeast(1, (long)stats.UdpBlocked, "udp_blocked 增加");
        Check.AtLeast(1, (long)stats.Blocked, "原帧被 block");
        CheckInvariants(stats, "fail");

        return Check.Failures == 0 ? 0 : 6;
    }

    private static DatagramRecord QueryRecord(WfpDevice device, IPEndPoint listener, IPEndPoint client)
    {
        DatagramRecord record = new() { Client = client, Detail = string.Empty };
        ProbeForwardedRedirectQuery query = QueryFor(listener, client);
        try
        {
            ProbeForwardedRedirect redirect = device.QueryForwardedRedirect(query);
            record.QueryOk = redirect.Magic == ProbeForwardedRedirect.MagicValue &&
                             redirect.Version == ProbeForwardedRedirect.VersionValue;
            record.ServerAddress = redirect.ServerIP;
            record.ServerPort = redirect.ServerPort;
            record.UpstreamId = redirect.UpstreamId;
            record.IngressIfIndex = redirect.IngressIfIndex;
            record.Protocol = redirect.Protocol;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            record.Detail = $"Win32={exception.NativeErrorCode} {exception.Message}";
        }

        return record;
    }

    private static unsafe ProbeForwardedRedirectQuery QueryFor(IPEndPoint listener, IPEndPoint client)
    {
        ProbeForwardedRedirectQuery query = default;
        query.ListenerPort = (ushort)listener.Port;
        query.Family = (byte)(client.AddressFamily == AddressFamily.InterNetwork ? 4 : 6);
        query.Protocol = Protocol.RuleProtocolUdp;
        query.ClientPort = (ushort)client.Port;
        byte[] clientBytes = client.Address.GetAddressBytes();
        for (int index = 0; index < clientBytes.Length; index++)
        {
            query.ClientAddress[index] = clientBytes[index];
        }
        if (!listener.Address.Equals(IPAddress.Any) && !listener.Address.Equals(IPAddress.IPv6Any))
        {
            byte[] listenerBytes = listener.Address.GetAddressBytes();
            for (int index = 0; index < listenerBytes.Length; index++)
            {
                query.ListenerAddress[index] = listenerBytes[index];
            }
        }
        return query;
    }

    private static Socket BindUdp(int port) => BindUdpFor(port, AddressFamily.InterNetwork);
    private static Socket BindUdpOn(int port, IPAddress address)
    {
        Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
        }

        socket.Bind(new IPEndPoint(address, port));
        return socket;
    }

    private static Socket BindUdpFor(int port, AddressFamily family)
    {
        Socket socket = new(family, SocketType.Dgram, ProtocolType.Udp);
        if (family == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        else
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }

        return socket;
    }


    private static Socket? TryBindDecoy(IPAddress remoteAddress, int remotePort)
    {
        for (int attempt = 0; attempt < 30; ++attempt)
        {
            try
            {
                Socket decoy = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                decoy.Bind(new IPEndPoint(remoteAddress, remotePort));
                return decoy;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressNotAvailable)
            {
                Thread.Sleep(100);
            }
            catch (SocketException exception)
            {
                Console.WriteLine($"[forwarded-udp] decoy 绑定 {remoteAddress}:{remotePort} 失败：{exception.Message}");
                return null;
            }
        }

        Console.WriteLine($"[forwarded-udp] decoy 绑定 {remoteAddress}:{remotePort} 超时（地址未就绪）");
        return null;
    }

    private static Socket BindEphemeralUdp()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket;
    }

    private static ForwardedTestRule PermitRule(uint ifIndex, string? clientCidr, IPAddress remoteAddress,
                                                int portStart, int portEnd) => new()
    {
        Action = Protocol.RuleActionPermit,
        IngressAdapterId = ifIndex,
        ClientCidr = clientCidr,
        RemoteCidr = HostCidr(remoteAddress),
        PortStart = (ushort)portStart,
        PortEnd = (ushort)portEnd,
        TransportProtocol = Protocol.RuleProtocolUdp,
        Family = (byte)(remoteAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 6),
    };

    private static ForwardedTestRule RedirectRule(uint ifIndex, string? clientCidr, IPAddress remoteAddress,
                                                  int portStart, int portEnd, uint upstreamId) => new()
    {
        Action = Protocol.RuleActionRedirect,
        IngressAdapterId = ifIndex,
        ClientCidr = clientCidr,
        RemoteCidr = HostCidr(remoteAddress),
        PortStart = (ushort)portStart,
        PortEnd = (ushort)portEnd,
        TransportProtocol = Protocol.RuleProtocolUdp,
        Family = (byte)(remoteAddress.AddressFamily == AddressFamily.InterNetwork ? 4 : 6),
        UpstreamId = upstreamId,
    };

    private static unsafe void Install(WfpDevice device, byte[] blob, uint expectedRules, int listenerPort,
                                       string localIp, byte family)
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
        (bool enabled, ProbeForwardedIngress[] ingresses) = device.GetForwardedIngressConfig();
        Check.True(enabled, "config 回读 enabled=1", $"count={ingresses.Length}");

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

    private static ProbeForwardedConfig DisabledConfig(int listenerPort) => new() { ListenerPort = (ushort)listenerPort };


    private static bool Restore(WfpDevice device, int listenerPort, int expectedGeneration = -1)
    {
        try
        {
            if (expectedGeneration >= 0)
            {
                ProbeForwardedInfo info = device.GetForwardedInfo();
                if (info.Installed != 1 || (long)info.Generation != expectedGeneration)
                {
                    Console.WriteLine($"[forwarded-udp] 忽略过期的看门狗：expected_generation={expectedGeneration} " +
                                      $"installed={info.Installed} generation={info.Generation}");
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-udp] 看门狗读取表状态失败：{exception.Message}");
            return false;
        }

        bool restored = false;
        try
        {
            device.SetForwardedConfig(DisabledConfig(listenerPort));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-udp] 恢复 forwarded config 失败：{exception.Message}");
        }

        try
        {
            device.SetForwardedBlob(ForwardedBlobBuilder.BuildEmpty(Protocol.RuleActionPermit));
            restored = true;
            Console.WriteLine("[forwarded-udp] 已恢复 rules=0 fallback=permit config=disabled");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[forwarded-udp] 恢复空 blob 失败：{exception.Message}");
        }
        return restored;
    }

    private static void ArmWatchdog(WfpDevice device, int listenerPort)
    {
        if (s_watchdog is null)
        {
            return;
        }

        s_watchdog.Start(s_watchdogSeconds, listenerPort, device.GetForwardedInfo().Generation);
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
        Check.Equal((long)stats.ClassifyCalls, (long)(stats.NoWriteRight + stats.InboundFrames + stats.OutboundFrames),
                    $"{name}：classify_calls == no_write_right + in + out");
        Check.Equal((long)(stats.InboundFrames + stats.OutboundFrames),
                    (long)(stats.SelfInjected + stats.NoTable + stats.Malformed + stats.NonIp + stats.Permitted +
                           stats.Blocked + stats.Redirected),
                    $"{name}：帧去向守恒");
        Check.Equal((long)(stats.MatchedPermit + stats.FallbackPermit + stats.UdpReturnMiss),
                    (long)stats.Permitted,
                    $"{name}：permitted == matched_permit + fallback_permit + udp_return_miss");
        Check.Equal((long)(stats.RedirectIngress + stats.RedirectBlocked + stats.UdpIngress + stats.UdpBlocked),
                    (long)(stats.MatchedRedirect + stats.UdpFragmented),
                    $"{name}：matched_redirect + udp_fragmented == tcp/udp ingress + blocked");
         Check.Equal((long)(stats.MatchedBlock + stats.FallbackBlock + stats.RedirectBlocked + stats.RedirectReturnFailed +
                            stats.RedirectReturnAmbiguous + stats.UdpBlocked + stats.UdpReturnFailed +
                            stats.UdpReturnAmbiguous),
                     (long)stats.Blocked,
                     $"{name}：blocked 拆分含 UDP 与歧义");

        Check.Equal((long)(stats.RedirectIngress + stats.RedirectReturn + stats.UdpIngress + stats.UdpReturn),
                    (long)stats.Redirected,
                    $"{name}：redirected == ingress + return（TCP+UDP）");
         Check.Equal((long)(stats.UdpNoConfig + stats.UdpNoState + stats.UdpNoIngress + stats.UdpCapacity +
                            stats.UdpFragmented + stats.UdpInjectFailed),
                     (long)stats.UdpBlocked,
                     $"{name}：udp_blocked 拆分");

    }

    private static void PrintStats(string prefix, ProbeForwardedStats stats) =>
        Console.WriteLine($"[forwarded-udp] {prefix} stats: calls={stats.ClassifyCalls} in={stats.InboundFrames} " +
                          $"out={stats.OutboundFrames} redirected={stats.Redirected} " +
                          $"udp_created={stats.UdpCreated} udp_ingress={stats.UdpIngress} udp_return={stats.UdpReturn} " +
                          $"udp_blocked={stats.UdpBlocked} no_config={stats.UdpNoConfig} capacity={stats.UdpCapacity} " +
                          $"fragmented={stats.UdpFragmented} inject_failed={stats.UdpInjectFailed} " +
                          $"return_failed={stats.UdpReturnFailed} return_miss={stats.UdpReturnMiss} miss_blocked={stats.UdpReturnMissBlocked} " +
                          $"expired={stats.UdpExpired} multi_nb={stats.UdpMultiNb} " +
                          $"complete={stats.InjectCompleteCalls}/{stats.InjectCompleteFailed}");

    private static TcpListener StartVerifyListener(int port)
    {
        TcpListener listener = new(IPAddress.Any, port);
        listener.Start();
        return listener;
    }

    private const int PayloadSize = 64;

    private static string ExpectedPayloadSha(int count)
    {
        byte[] payload = new byte[count * PayloadSize];
        for (int datagram = 0; datagram < count; datagram++)
        {
            for (int index = 0; index < PayloadSize; index++)
            {
                payload[datagram * PayloadSize + index] = (byte)((datagram * 31 + index * 7 + 3) & 0xFF);
            }
        }

        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static async Task<List<VerifyRecord>> CollectVerificationAsync(TcpListener listener, int expected,
                                                                           int expectedCount, string expectedSha,
                                                                           int timeoutMs)
    {
        List<VerifyRecord> records = new();
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            while (records.Count < expected)
            {
                using Socket socket = await listener.AcceptSocketAsync(timeout.Token);
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    continue;
                }

                Console.WriteLine($"[forwarded-udp] verify {line}");
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
                        case "client_port":
                            record.ClientPort = int.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "target_port":
                            record.TargetPort = int.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "bytes":
                            record.Bytes = int.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "count":
                            record.Count = int.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "sha":
                            record.Sha = value;
                            break;
                        case "source":
                            record.Source = value;
                            break;
                        case "source_ok":
                            record.SourceOk = value == "1";
                            break;
                        case "ok":
                            record.Ok = value == "1";
                            break;
                    }
                }

                records.Add(record);
                _ = expectedCount;
                _ = expectedSha;
            }
        }
        catch (OperationCanceledException)
        {
            // 缺回报由调用方断言。
        }

        return records;
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
            Arguments = $"/c ping -n {seconds + 1} 127.0.0.1 >nul & \"{exe}\" forwarded-udp --restore-only " +
                        $"--listener-port {listenerPort} --generation {generation} >> \"{logPath}\" 2>&1",
        };
        try
        {
            Process? process = Process.Start(startInfo);
            Console.WriteLine($"[forwarded-udp] 看门狗已启动：{seconds}s 后恢复空 permit 表与 disabled config " +
                              $"generation={generation} log={logPath}");
            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"[forwarded-udp] 看门狗启动失败：{exception.Message}");
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

    private sealed class DatagramRecord
    {
        public IPEndPoint Client { get; init; } = new(IPAddress.Loopback, 0);
        public bool QueryOk;
        public IPAddress ServerAddress = IPAddress.Any;
        public int ServerPort;
        public uint UpstreamId;
        public uint IngressIfIndex;
        public byte Protocol;
        public string Detail = string.Empty;
    }

    private sealed class VerifyRecord
    {
        public int ClientPort;
        public int TargetPort;
        public int Bytes;
        public int Count;
        public string Sha = string.Empty;
        public string Source = string.Empty;
        public bool SourceOk;
        public bool Ok;
    }

    private readonly record struct AssociationKey(IPEndPoint Client, IPAddress ServerAddress, int ServerPort,
                                                 uint UpstreamId);
}

/// <summary>wfprobe 进程内的最小 SOCKS5 UDP 上游：UDP ASSOCIATE + 原样回显 payload。</summary>
internal sealed class Socks5UdpFakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly bool _rejectAssociate;
    private int _associations;
    private int _associationFailures;
    private int _datagrams;

    public Socks5UdpFakeServer(bool rejectAssociate = false)
    {
        _rejectAssociate = rejectAssociate;
        _listener.Start();
        EndPoint = (IPEndPoint)_listener.LocalEndpoint;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public IPEndPoint EndPoint { get; }

    public int Associations => Volatile.Read(ref _associations);

    public int AssociationFailures => Volatile.Read(ref _associationFailures);

    public int Datagrams => Volatile.Read(ref _datagrams);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket control;
            try
            {
                control = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(control));
        }
    }

    private async Task ServeAsync(Socket control)
    {
        using (control)
        {
            try
            {
                byte[] greeting = new byte[2];
                await ReadExactlyAsync(control, greeting);
                byte[] methods = new byte[greeting[1]];
                await ReadExactlyAsync(control, methods);
                await control.SendAsync(new byte[] { 0x05, 0x00 });

                byte[] request = new byte[4];
                await ReadExactlyAsync(control, request);
                if (_rejectAssociate || request[1] != 0x03)
                {
                    Interlocked.Increment(ref _associationFailures);
                    await control.SendAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                using Socket data = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                data.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                if (data.LocalEndPoint is not IPEndPoint relay)
                {
                    return;
                }
                byte[] reply = new byte[10];
                reply[0] = 0x05;
                reply[1] = 0x00;
                reply[3] = 0x01;
                relay.Address.GetAddressBytes().CopyTo(reply, 4);
                reply[8] = (byte)(relay.Port >> 8);
                reply[9] = (byte)(relay.Port & 0xFF);
                await control.SendAsync(reply);
                Interlocked.Increment(ref _associations);

                using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[] one = new byte[1];
                        while (await control.ReceiveAsync(one, session.Token) > 0)
                        {
                        }
                    }
                    catch
                    {
                        // 控制连接关闭就是关联结束。
                    }
                    finally
                    {
                        await session.CancelAsync();
                    }
                });

                byte[] buffer = new byte[65535];
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                while (!session.IsCancellationRequested)
                {
                    SocketReceiveFromResult received =
                        await data.ReceiveFromAsync(buffer, SocketFlags.None, remote, session.Token);
                    if (received.ReceivedBytes <= 0)
                    {
                        continue;
                    }

                    if (Socks5UdpWire.HeaderLength(buffer.AsSpan(0, received.ReceivedBytes)) < 0)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref _datagrams);
                    await data.SendToAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None,
                                           received.RemoteEndPoint, session.Token);
                }
            }
            catch (Exception)
            {
                // 关闭/畸形握手：测试上游直接结束会话。
            }
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

/// <summary>wfprobe 侧的 SOCKS5 UDP 客户端：控制连接 + 已 connect 到中继的数据 socket。</summary>
internal sealed class Socks5UdpProbeClient : IAsyncDisposable
{
    private readonly Socket _control;
    private readonly Socket _data;

    private Socks5UdpProbeClient(Socket control, Socket data)
    {
        _control = control;
        _data = data;
    }

    public byte[] LastReceived { get; } = new byte[65535];

    public static async Task<Socks5UdpProbeClient> ConnectAsync(IPEndPoint proxy, CancellationToken token)
    {
        Socket control = new(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Socket? data = null;
        try
        {
            await control.ConnectAsync(proxy, token);
            await control.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, token);
            byte[] greeting = new byte[2];
            await ReadExactlyAsync(control, greeting, token);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
            {
                throw new IOException($"UDP ASSOCIATE 协商失败：{greeting[0]:X2} {greeting[1]:X2}");
            }

            await control.SendAsync(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, token);
            byte[] reply = new byte[4];
            await ReadExactlyAsync(control, reply, token);
            if (reply[1] != 0x00)
            {
                throw new IOException($"UDP ASSOCIATE 被拒绝：rep={reply[1]}");
            }

            IPAddress address;
            switch (reply[3])
            {
                case 0x01:
                    byte[] v4 = new byte[4];
                    await ReadExactlyAsync(control, v4, token);
                    address = new IPAddress(v4);
                    break;
                case 0x04:
                    byte[] v6 = new byte[16];
                    await ReadExactlyAsync(control, v6, token);
                    address = new IPAddress(v6);
                    break;
                case 0x03:
                    byte[] length = new byte[1];
                    await ReadExactlyAsync(control, length, token);
                    byte[] domain = new byte[length[0]];
                    await ReadExactlyAsync(control, domain, token);
                    address = Dns.GetHostAddresses(Encoding.ASCII.GetString(domain))[0];
                    break;
                default:
                    throw new IOException($"UDP ASSOCIATE 回复地址类型 {reply[3]} 未知");
            }

            byte[] portBytes = new byte[2];
            await ReadExactlyAsync(control, portBytes, token);
            int port = (portBytes[0] << 8) | portBytes[1];
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                address = proxy.Address;
            }

            data = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            data.Connect(new IPEndPoint(address, port));
            Socks5UdpProbeClient client = new(control, data);
            control = null!;
            data = null;
            return client;
        }
        finally
        {
            control?.Dispose();
            data?.Dispose();
        }
    }

    public async Task SendAsync(IPAddress server, int port, byte[] payload, CancellationToken token)
    {
        byte[] datagram = Socks5UdpWire.Encode(new IPEndPoint(server, port), payload);
        await _data.SendAsync(datagram, SocketFlags.None, token);
    }

    public async Task<int> ReceiveAsync(CancellationToken token) =>
        await _data.ReceiveAsync(LastReceived, SocketFlags.None, token);

    public static int PayloadOffset(byte[] datagram) => Socks5UdpWire.HeaderLength(datagram);

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

    public ValueTask DisposeAsync()
    {
        _data.Dispose();
        _control.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>SOCKS5 UDP 线格式：wfprobe 不引用产品程序集，这里保留一份最小实现。</summary>
internal static class Socks5UdpWire
{
    public static byte[] Encode(IPEndPoint destination, ReadOnlySpan<byte> payload)
    {
        byte[] address = destination.Address.GetAddressBytes();
        int header = 4 + address.Length + 2;
        byte[] datagram = new byte[header + payload.Length];
        datagram[3] = address.Length == 4 ? (byte)0x01 : (byte)0x04;
        address.CopyTo(datagram, 4);
        datagram[header - 2] = (byte)(destination.Port >> 8);
        datagram[header - 1] = (byte)(destination.Port & 0xFF);
        payload.CopyTo(datagram.AsSpan(header));
        return datagram;
    }

    public static int HeaderLength(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 4 || datagram[0] != 0 || datagram[1] != 0 || datagram[2] != 0)
        {
            return -1;
        }

        switch (datagram[3])
        {
            case 0x01:
                return datagram.Length >= 10 ? 10 : -1;
            case 0x04:
                return datagram.Length >= 22 ? 22 : -1;
            case 0x03:
                if (datagram.Length < 5)
                {
                    return -1;
                }

                int length = 4 + 1 + datagram[4] + 2;
                return datagram.Length >= length ? length : -1;
            default:
                return -1;
        }
    }
}

