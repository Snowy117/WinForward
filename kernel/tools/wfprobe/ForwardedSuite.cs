using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinForward.Probe;

// MAC 帧层 forwarded 规则的验证套件（B4a 的 F1–F7）。
//
// 与 host 的 rules 套件一样按「下发 → 制造流量 → 读计数」断言；不同的是 forwarded 规则的
// client/remote 是方向化的：入向 client=源、remote=目的，出向 client=目的、remote=源。
// 因此 VM 主动连 Linux 时，client 侧 CIDR 写的是 Linux 地址。
//
// block 用例（F2/F4）都用固定源端口的客户端 + 端口收窄的规则：出向帧的 remote_port 就是源端口，
// 规则因此只命中测试流，不会碰到 WinRM 的 5985/5986。整个套件外层再挂一个看门狗：进程被杀时
// 超时后把 forwarded 表恢复成空 permit 表，避免把 L2 留在拦截状态。
internal static class ForwardedSuite
{
    private const int DefaultInboundPort = 40260;
    private const int DefaultSourcePortBase = 40310;

    public static async Task<int> RunAsync(Options options)
    {
        if (options.Has("restore-only"))
        {
            using var restoreDevice = new WfpDevice();
            restoreDevice.SetForwardedBlob(ForwardedBlobBuilder.BuildEmpty(Protocol.RuleActionPermit));
            Console.WriteLine("[forwarded] restore-only rules=0 fallback=permit");
            return 0;
        }

        int timeoutMs = options.GetInt("timeout-ms", 3000);
        int inboundWaitMs = options.GetInt("inbound-wait-ms", 0);
        int inboundPort = options.GetInt("inbound-port", DefaultInboundPort);
        int sourcePortBase = options.GetInt("source-port", DefaultSourcePortBase);
        string localIp = options.Get("local-ip") ?? "192.168.100.2";
        string gateway = options.Get("gateway") ?? "192.168.100.1";
        string? peerText = options.Get("peer");
        string? peer6 = options.Get("peer6");
        HashSet<string> only = ParseOnly(options.Get("only"));
        List<string> skipped = new();

        using var device = new WfpDevice();
        ProbeForwardedInfo info = device.GetForwardedInfo();
        ProbeStats stats = device.GetStats();
        Console.WriteLine($"[forwarded] abi={stats.AbiVersion} info_abi={info.AbiVersion} " +
                          $"installed={info.Installed} generation={info.Generation}");
        Check.Equal(Protocol.AbiVersion, (long)stats.AbiVersion, "驱动 ABI 版本与夹具一致");
        Check.Equal(Protocol.AbiVersion, (long)info.AbiVersion, "forwarded_info ABI 版本与夹具一致");

        uint ifIndex = options.Has("ifindex") ? options.GetUInt("ifindex", 0) : FindAdapterIndex(localIp) ?? 0;
        Check.True(ifIndex != 0, "解析出入口 ifIndex", $"ifindex={ifIndex} local-ip={localIp}");
        Check.True(sourcePortBase >= 1024 && sourcePortBase + 8 <= 65535,
                   "测试源端口基数合法", $"base={sourcePortBase}（需要 base+8 ≤ 65535）");
        Check.True(sourcePortBase + 8 < 5985 || sourcePortBase > 5986,
                   "测试源端口段与 WinRM 管理端口不重叠", $"base={sourcePortBase}");

        IPEndPoint? peer = peerText is null ? null : ParseEndPoint(peerText);
        Console.WriteLine($"[forwarded] ifindex={ifIndex} local={localIp} peer={peerText ?? "-"} " +
                          $"inbound_port={inboundPort} inbound_wait_ms={inboundWaitMs} " +
                          $"source_port_base={sourcePortBase}");

        string clientCidr = HostCidr(peer?.Address.ToString() ?? localIp);
        string remoteCidr = HostCidr(localIp);

        // 固定源端口是 block 用例的收窄条件，必须真的能用：上一轮同 4-tuple 的连接可能还在
        // TIME_WAIT（或端口被别的进程占着），所以每个端口都 bind + 探路连接一次。
        int probeTimeoutMs = Math.Min(timeoutMs, 1000);
        HashSet<int> usedSourcePorts = new();
        int passPort = ResolveSourcePort(sourcePortBase, peer, probeTimeoutMs, usedSourcePorts);
        int blockPort = ResolveSourcePort(sourcePortBase + 1, peer, probeTimeoutMs, usedSourcePorts);
        int orderBlockPort = ResolveSourcePort(sourcePortBase + 2, peer, probeTimeoutMs, usedSourcePorts);
        int orderPermitPort = ResolveSourcePort(sourcePortBase + 3, peer, probeTimeoutMs, usedSourcePorts);
        int cidrPort = ResolveSourcePort(sourcePortBase + 4, peer, probeTimeoutMs, usedSourcePorts);
        int cidrMissPort = ResolveSourcePort(sourcePortBase + 5, peer, probeTimeoutMs, usedSourcePorts);
        int portMissPort = ResolveSourcePort(sourcePortBase + 6, peer, probeTimeoutMs, usedSourcePorts);
        Console.WriteLine($"[forwarded] source_ports pass={passPort} block={blockPort} " +
                          $"order={orderBlockPort}/{orderPermitPort} f5={cidrPort}/{cidrMissPort}/{portMissPort}");

        int watchdogSeconds = options.GetInt("watchdog-seconds", Math.Max(90, (inboundWaitMs / 1000) + 60));
        using Process? watchdog = StartRestoreWatchdog(watchdogSeconds);
        try
        {
            if (ShouldRun(only, "F1") && !RunPassCase(device, ifIndex, peer, clientCidr, remoteCidr,
                                                      passPort, timeoutMs, "F1"))
                skipped.Add("F1");

            if (ShouldRun(only, "F2") && !RunBlockCase(device, ifIndex, peer, clientCidr, remoteCidr,
                                                       blockPort, timeoutMs, "F2"))
                skipped.Add("F2");

            if (ShouldRun(only, "F3") && !RunFallbackCases(device, ifIndex, peer, gateway, timeoutMs, localIp))
                skipped.Add("F3");

            if (ShouldRun(only, "F4") && !RunOrderCase(device, ifIndex, peer, clientCidr, remoteCidr,
                                                       orderBlockPort, orderPermitPort, timeoutMs))
                skipped.Add("F4");

            if (ShouldRun(only, "F5") && !RunCidrAndPortCases(device, ifIndex, peer, clientCidr, localIp,
                                                              cidrPort, cidrMissPort, portMissPort, timeoutMs))
                skipped.Add("F5");

            if (ShouldRun(only, "F6"))
                RunInvalidBlobCase(device, ifIndex, clientCidr, remoteCidr);

            if (ShouldRun(only, "F7") && !RunIpv6Case(device, ifIndex, peer6, timeoutMs))
                skipped.Add("F7");

            if (ShouldRun(only, "F1i"))
            {
                if (inboundWaitMs > 0)
                    await RunInboundCaseAsync(device, ifIndex, localIp, inboundPort, inboundWaitMs, timeoutMs,
                                              block: false);
                else
                {
                    Console.WriteLine("[forwarded] F1i SKIP：入向用例需要 --inbound-wait-ms（外部客户端在窗口内连接）");
                    skipped.Add("F1i");
                }
            }

            if (ShouldRun(only, "F2i"))
            {
                if (inboundWaitMs > 0)
                    await RunInboundCaseAsync(device, ifIndex, localIp, inboundPort, inboundWaitMs, timeoutMs,
                                              block: true);
                else
                {
                    Console.WriteLine("[forwarded] F2i SKIP：入向用例需要 --inbound-wait-ms");
                    skipped.Add("F2i");
                }
            }
        }
        finally
        {
            if (!options.Has("keep-blob"))
                Restore(device);
            if (watchdog is not null && !watchdog.HasExited)
                watchdog.Kill(true);
        }

        ProbeForwardedStats final = device.GetForwardedStats();
        PrintStats("final", final);
        CheckInvariantsStable(device, "最终");

        string[] required = { "F1", "F2", "F3", "F6" };
        bool requiredSkipped = skipped.Any(required.Contains);
        bool requestedSkipped = only.Count > 0 && skipped.Any(only.Contains);
        bool failed = Check.Failures > 0;
        bool ok = !failed && !requiredSkipped && !requestedSkipped;

        if (skipped.Count > 0)
        {
            Console.WriteLine($"[forwarded] skipped={string.Join(",", skipped)}" +
                              "（F1/F2/F3/F6 是必需用例；F7 属环境未验证；F1i/F2i 需要外部客户端）");
        }

        string verdict = failed ? "FAIL" : (ok ? "PASS" : "SKIPPED");
        Console.WriteLine($"[forwarded] verdict={verdict} failures={Check.Failures} " +
                          $"skipped={skipped.Count} required_skipped={requiredSkipped}");
        return ok ? 0 : 6;
    }

    private static bool RunPassCase(WfpDevice device,
                                    uint ifIndex,
                                    IPEndPoint? peer,
                                    string clientCidr,
                                    string remoteCidr,
                                    int sourcePort,
                                    int timeoutMs,
                                    string name)
    {
        if (peer is null)
        {
            Console.WriteLine($"[forwarded] {name} SKIP：没有 --peer（VM 主动连接需要一台外部监听）");
            return false;
        }

        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                new ForwardedTestRule
                {
                    Action = Protocol.RuleActionPermit,
                    IngressAdapterId = ifIndex,
                    ClientCidr = clientCidr,
                    RemoteCidr = remoteCidr,
                    PortStart = (ushort)sourcePort,
                    PortEnd = (ushort)sourcePort,
                    TransportProtocol = Protocol.RuleProtocolTcp,
                },
            },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, Protocol.RuleActionPermit, name);
        device.ResetForwardedStats();

        bool connected = TryConnect(peer, sourcePort, timeoutMs, out string error);
        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats(name, stats);

        Check.True(connected, $"{name} 命中 permit：连接成功", error);
        Check.AtLeast(1, (long)stats.MatchedPermit, $"{name} 命中 permit：matched_permit 增加");
        Check.AtLeast(1, (long)stats.Permitted, $"{name} 命中 permit：permitted 增加");
        Check.Equal(0, (long)stats.MatchedBlock, $"{name} 命中 permit：没有 block");
        CheckInvariantsStable(device, name);
        return true;
    }

    private static bool RunBlockCase(WfpDevice device,
                                     uint ifIndex,
                                     IPEndPoint? peer,
                                     string clientCidr,
                                     string remoteCidr,
                                     int sourcePort,
                                     int timeoutMs,
                                     string name)
    {
        if (peer is null)
        {
            Console.WriteLine($"[forwarded] {name} SKIP：没有 --peer");
            return false;
        }

        // 只拦「源端口 = sourcePort」的这一条流；WinRM 的 5985/5986 走 fallback permit。
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                new ForwardedTestRule
                {
                    Action = Protocol.RuleActionBlock,
                    IngressAdapterId = ifIndex,
                    ClientCidr = clientCidr,
                    RemoteCidr = remoteCidr,
                    PortStart = (ushort)sourcePort,
                    PortEnd = (ushort)sourcePort,
                    TransportProtocol = Protocol.RuleProtocolTcp,
                },
            },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, Protocol.RuleActionPermit, name);
        device.ResetForwardedStats();

        bool connected = TryConnect(peer, sourcePort, Math.Min(timeoutMs, 2000), out string error);
        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats(name, stats);

        // block = 丢帧，不注入 RST：客户端表现为超时/连接失败，不承诺「立即被拒」。
        Check.True(!connected, $"{name} 命中 block：连接失败或超时", error);
        Check.AtLeast(1, (long)stats.MatchedBlock, $"{name} 命中 block：matched_block 增加");
        Check.AtLeast(1, (long)stats.Blocked, $"{name} 命中 block：blocked 增加");
        Check.Equal(0, (long)stats.MatchedPermit, $"{name} 命中 block：没有 permit");
        CheckInvariantsStable(device, name);
        return true;
    }

    private static bool RunFallbackCases(WfpDevice device,
                                         uint ifIndex,
                                         IPEndPoint? peer,
                                         string gateway,
                                         int timeoutMs,
                                         string localIp)
    {
        if (peer is not null)
        {
            Install(device, ForwardedBlobBuilder.BuildEmpty(Protocol.RuleActionPermit), 0,
                    Protocol.RuleActionPermit, "F3");
            device.ResetForwardedStats();
            bool connected = TryConnect(peer, 0, timeoutMs, out string error);
            Thread.Sleep(200);
            ProbeForwardedStats permitStats = device.GetForwardedStats();
            PrintStats("F3 fallback=permit", permitStats);
            Check.True(connected, "F3 fallback=permit：未命中规则仍然放行", error);
            Check.Equal(0, (long)permitStats.Matched, "F3 fallback=permit：matched=0");
            Check.AtLeast(1, (long)permitStats.FallbackPermit, "F3 fallback=permit：fallback_permit 增加");
            CheckInvariantsStable(device, "F3 fallback=permit");
        }
        else
        {
            Console.WriteLine("[forwarded] F3 fallback=permit SKIP：没有 --peer（block 部分照常执行）");
        }

        // fallback=block 影响所有未命中帧。为不切断管理通道，先放行 WinRM 的 5985/5986；
        // 非 IP 帧（ARP）在驱动里从不参与 block。套件级看门狗负责进程被杀后的恢复。
        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                new ForwardedTestRule
                {
                    Action = Protocol.RuleActionPermit,
                    IngressAdapterId = ifIndex,
                    PortStart = 5985,
                    PortEnd = 5986,
                    TransportProtocol = Protocol.RuleProtocolTcp,
                },
            },
            Protocol.RuleActionBlock);
        Install(device, blob, 1, Protocol.RuleActionBlock, "F3 fallback=block");
        device.ResetForwardedStats();

        PingReply reply = PingOnce(gateway, Math.Min(timeoutMs, 1500));
        Thread.Sleep(200);
        ProbeForwardedStats blockStats = device.GetForwardedStats();
        PrintStats("F3 fallback=block", blockStats);
        Check.True(reply.Status != IPStatus.Success, "F3 fallback=block：未命中的 ICMP 被丢弃", reply.Status.ToString());
        Check.AtLeast(1, (long)blockStats.FallbackBlock, "F3 fallback=block：fallback_block 增加");
        Check.AtLeast(1, (long)blockStats.Blocked, "F3 fallback=block：blocked 增加");
        Check.Equal((long)blockStats.Blocked, (long)blockStats.FallbackBlock,
                    "F3 fallback=block：这次的 block 全部来自兜底");
        Check.Equal((long)blockStats.Matched, (long)blockStats.MatchedPermit,
                    "F3 fallback=block：命中的只有 permit 白名单规则，没有规则级 block");
        CheckInvariantsStable(device, "F3 fallback=block");

        // 立刻恢复，别等看门狗。
        Restore(device);
        Console.WriteLine($"[forwarded] F3 fallback=block 已恢复（local={localIp}）");
        // fallback=permit 需要 --peer；只跑 block 半边时把 F3 标为 skipped，
        // 否则必需用例会在前置条件缺失时仍然报 PASS。
        return peer is not null;
    }

    private static bool RunOrderCase(WfpDevice device,
                                     uint ifIndex,
                                     IPEndPoint? peer,
                                     string clientCidr,
                                     string remoteCidr,
                                     int blockFirstPort,
                                     int permitFirstPort,
                                     int timeoutMs)
    {
        if (peer is null)
        {
            Console.WriteLine("[forwarded] F4 SKIP：没有 --peer");
            return false;
        }

        ForwardedTestRule Block(int port) => new()
        {
            Action = Protocol.RuleActionBlock,
            IngressAdapterId = ifIndex,
            ClientCidr = clientCidr,
            RemoteCidr = remoteCidr,
            PortStart = (ushort)port,
            PortEnd = (ushort)port,
            TransportProtocol = Protocol.RuleProtocolTcp,
        };
        ForwardedTestRule Permit(int port) => new()
        {
            Action = Protocol.RuleActionPermit,
            IngressAdapterId = ifIndex,
            ClientCidr = clientCidr,
            RemoteCidr = remoteCidr,
            PortStart = (ushort)port,
            PortEnd = (ushort)port,
            TransportProtocol = Protocol.RuleProtocolTcp,
        };

        Install(device, ForwardedBlobBuilder.Build(new[] { Block(blockFirstPort), Permit(blockFirstPort) },
                                                   Protocol.RuleActionPermit),
                2, Protocol.RuleActionPermit, "F4 block-first");
        device.ResetForwardedStats();
        bool blocked = TryConnect(peer, blockFirstPort, Math.Min(timeoutMs, 2000), out string blockedError);
        Thread.Sleep(200);
        ProbeForwardedStats blockedStats = device.GetForwardedStats();
        PrintStats("F4 block-first", blockedStats);
        Check.True(!blocked, "F4 顺序：block 在前的 blob 里 block 生效", blockedError);
        Check.AtLeast(1, (long)blockedStats.MatchedBlock, "F4 顺序：命中第一条 block");
        Check.Equal(0, (long)blockedStats.MatchedPermit, "F4 顺序：后面的 permit 不参与");

        Install(device, ForwardedBlobBuilder.Build(new[] { Permit(permitFirstPort), Block(permitFirstPort) },
                                                   Protocol.RuleActionPermit),
                2, Protocol.RuleActionPermit, "F4 permit-first");
        device.ResetForwardedStats();
        bool permitted = TryConnect(peer, permitFirstPort, timeoutMs, out string permittedError);
        Thread.Sleep(200);
        ProbeForwardedStats permittedStats = device.GetForwardedStats();
        PrintStats("F4 permit-first", permittedStats);
        Check.True(permitted, "F4 顺序：permit 在前的 blob 里 permit 生效", permittedError);
        Check.AtLeast(1, (long)permittedStats.MatchedPermit, "F4 顺序：命中第一条 permit");
        Check.Equal(0, (long)permittedStats.MatchedBlock, "F4 顺序：后面的 block 不参与");
        CheckInvariantsStable(device, "F4");
        return true;
    }

    private static bool RunCidrAndPortCases(WfpDevice device,
                                            uint ifIndex,
                                            IPEndPoint? peer,
                                            string clientCidr,
                                            string localIp,
                                            int cidrPort,
                                            int cidrMissPort,
                                            int portMissPort,
                                            int timeoutMs)
    {
        if (peer is null)
        {
            Console.WriteLine("[forwarded] F5 SKIP：没有 --peer");
            return false;
        }

        string subnet24 = Subnet24(localIp);
        Install(device,
                ForwardedBlobBuilder.Build(
                    new[]
                    {
                        new ForwardedTestRule
                        {
                            Action = Protocol.RuleActionPermit,
                            IngressAdapterId = ifIndex,
                            ClientCidr = clientCidr,
                            RemoteCidr = subnet24,
                            TransportProtocol = Protocol.RuleProtocolTcp,
                        },
                    },
                    Protocol.RuleActionPermit),
                1, Protocol.RuleActionPermit, "F5 CIDR");
        device.ResetForwardedStats();
        bool connected = TryConnect(peer, cidrPort, timeoutMs, out string error);
        Thread.Sleep(200);
        ProbeForwardedStats cidrStats = device.GetForwardedStats();
        PrintStats("F5 CIDR", cidrStats);
        Check.True(connected, "F5 client/remote CIDR：/24 远端网段命中", error);
        Check.AtLeast(1, (long)cidrStats.MatchedPermit, "F5 client/remote CIDR：matched_permit 增加");

        Install(device,
                ForwardedBlobBuilder.Build(
                    new[]
                    {
                        new ForwardedTestRule
                        {
                            Action = Protocol.RuleActionPermit,
                            IngressAdapterId = ifIndex,
                            ClientCidr = "10.0.0.0/8",
                        },
                    },
                    Protocol.RuleActionPermit),
                1, Protocol.RuleActionPermit, "F5 CIDR miss");
        device.ResetForwardedStats();
        bool missConnected = TryConnect(peer, cidrMissPort, timeoutMs, out string missError);
        Thread.Sleep(200);
        ProbeForwardedStats missStats = device.GetForwardedStats();
        PrintStats("F5 CIDR miss", missStats);
        Check.True(missConnected, "F5 不匹配的 CIDR 走 fallback（仍然放行）", missError);
        Check.Equal(0, (long)missStats.Matched, "F5 不匹配的 CIDR：matched=0");
        Check.AtLeast(1, (long)missStats.FallbackPermit, "F5 不匹配的 CIDR：fallback_permit 增加");

        Install(device,
                ForwardedBlobBuilder.Build(
                    new[]
                    {
                        new ForwardedTestRule
                        {
                            Action = Protocol.RuleActionPermit,
                            IngressAdapterId = ifIndex,
                            PortStart = 1,
                            PortEnd = 2,
                            TransportProtocol = Protocol.RuleProtocolTcp,
                        },
                    },
                    Protocol.RuleActionPermit),
                1, Protocol.RuleActionPermit, "F5 port miss");
        device.ResetForwardedStats();
        bool portConnected = TryConnect(peer, portMissPort, timeoutMs, out string portError);
        Thread.Sleep(200);
        ProbeForwardedStats portStats = device.GetForwardedStats();
        PrintStats("F5 port miss", portStats);
        Check.True(portConnected, "F5 端口不命中走 fallback（仍然放行）", portError);
        Check.Equal(0, (long)portStats.Matched, "F5 端口不命中：matched=0");
        Check.AtLeast(1, (long)portStats.FallbackPermit, "F5 端口不命中：fallback_permit 增加");
        CheckInvariantsStable(device, "F5");
        return true;
    }

    private static void RunInvalidBlobCase(WfpDevice device, uint ifIndex, string clientCidr, string remoteCidr)
    {
        byte[] valid = ForwardedBlobBuilder.Build(
            new[]
            {
                new ForwardedTestRule
                {
                    Action = Protocol.RuleActionPermit,
                    IngressAdapterId = ifIndex,
                    ClientCidr = clientCidr,
                    RemoteCidr = remoteCidr,
                    TransportProtocol = Protocol.RuleProtocolTcp,
                },
            },
            Protocol.RuleActionPermit);
        Install(device, valid, 1, Protocol.RuleActionPermit, "F6 baseline");
        ProbeForwardedInfo before = device.GetForwardedInfo();
        ProbeForwardedStats statsBefore = device.GetForwardedStats();

        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 0xFF;
        CheckRejected(device, badMagic, 87, "F6 非法 magic 被拒（STATUS_INVALID_PARAMETER）");

        byte[] truncated = valid[..(valid.Length - 4)];
        CheckRejected(device, truncated, 87, "F6 截断的 blob 被拒");

        byte[] permitWithUpstream = ForwardedBlobBuilder.Build(
            new[]
            {
                new ForwardedTestRule
                {
                    Action = Protocol.RuleActionPermit,
                    IngressAdapterId = ifIndex,
                    UpstreamId = 0,
                },
            },
            Protocol.RuleActionPermit);
        CheckRejected(device, permitWithUpstream, 87, "F6 非 redirect 带 upstream 被拒（upstream 组合非法）");

        ProbeForwardedInfo after = device.GetForwardedInfo();
        ProbeForwardedStats statsAfter = device.GetForwardedStats();
        Check.Equal(before.Generation, after.Generation, "F6 非法 blob 不推进 generation");
        Check.Equal(before.RuleCount, after.RuleCount, "F6 非法 blob 不改规则数");
        Check.Equal(before.TotalSize, after.TotalSize, "F6 非法 blob 不改表长度");
        Check.Equal((long)(statsBefore.RejectedBlobs + 3), (long)statsAfter.RejectedBlobs, "F6 rejected_blobs 增加 3");
    }

    private static void CheckRejected(WfpDevice device, byte[] blob, int expectedError, string name)
    {
        bool rejected = false;
        int code = 0;
        try
        {
            device.SetForwardedBlob(blob);
        }
        catch (Win32Exception ex)
        {
            rejected = true;
            code = ex.NativeErrorCode;
        }

        Check.True(rejected && code == expectedError, name, $"win32={code}");
    }

    private static bool RunIpv6Case(WfpDevice device, uint ifIndex, string? peer6, int timeoutMs)
    {
        if (peer6 is null)
        {
            Console.WriteLine("[forwarded] F7 SKIP：没有 --peer6（本 VM 默认关闭 Internal IPv6 绑定，IPv6 路径在本环境未验证）");
            return false;
        }

        if (!PingSucceeds(peer6, timeoutMs))
        {
            Console.WriteLine($"[forwarded] F7 SKIP：{peer6} 在规则表之外就不可 ping 通（环境未验证）");
            return false;
        }

        byte[] blob = ForwardedBlobBuilder.Build(
            new[]
            {
                new ForwardedTestRule
                {
                    Action = Protocol.RuleActionPermit,
                    IngressAdapterId = ifIndex,
                    Family = 6,
                },
            },
            Protocol.RuleActionPermit);
        Install(device, blob, 1, Protocol.RuleActionPermit, "F7 ipv6");
        device.ResetForwardedStats();
        bool reachable = PingSucceeds(peer6, timeoutMs);
        Thread.Sleep(200);
        ProbeForwardedStats stats = device.GetForwardedStats();
        PrintStats("F7 ipv6", stats);
        Check.True(reachable, "F7 IPv6：family=ipv6 规则命中后仍然可达", peer6);
        Check.AtLeast(1, (long)stats.MatchedPermit, "F7 IPv6：matched_permit 增加");
        CheckInvariantsStable(device, "F7");
        return true;
    }

    private static async Task RunInboundCaseAsync(WfpDevice device,
                                                  uint ifIndex,
                                                  string localIp,
                                                  int inboundPort,
                                                  int waitMs,
                                                  int timeoutMs,
                                                  bool block)
    {
        string name = block ? "F2i" : "F1i";
        var listener = new TcpListener(IPAddress.Any, inboundPort);
        listener.Start();
        try
        {
            byte[] blob = ForwardedBlobBuilder.Build(
                new[]
                {
                    new ForwardedTestRule
                    {
                        Action = block ? Protocol.RuleActionBlock : Protocol.RuleActionPermit,
                        IngressAdapterId = ifIndex,
                        ClientCidr = "0.0.0.0/0",
                        RemoteCidr = HostCidr(localIp),
                        PortStart = (ushort)inboundPort,
                        PortEnd = (ushort)inboundPort,
                        TransportProtocol = Protocol.RuleProtocolTcp,
                    },
                },
                Protocol.RuleActionPermit);
            Install(device, blob, 1, Protocol.RuleActionPermit, name);
            device.ResetForwardedStats();

            Console.WriteLine($"[forwarded] FORWARDED-READY case={name} port={inboundPort} wait_ms={waitMs} block={block}");
            Console.Out.Flush();

            Socket? accepted = await AcceptAsync(listener, waitMs);
            Thread.Sleep(200);
            ProbeForwardedStats stats = device.GetForwardedStats();
            PrintStats(name, stats);
            if (block)
            {
                Check.True(accepted is null, "F2i 入向 block：VM 监听端没有收到连接", $"port={inboundPort}");
                Check.AtLeast(1, (long)stats.MatchedBlock, "F2i 入向 block：matched_block 增加");
                Check.AtLeast(1, (long)stats.Blocked, "F2i 入向 block：blocked 增加");
            }
            else
            {
                Check.True(accepted is not null, "F1i 入向 permit：VM 监听端收到连接", $"port={inboundPort}");
                Check.AtLeast(1, (long)stats.MatchedPermit, "F1i 入向 permit：matched_permit 增加");
            }
            accepted?.Dispose();
            CheckInvariantsStable(device, name);
        }
        finally
        {
            listener.Stop();
            Restore(device);
        }
    }

    private static void Install(WfpDevice device, byte[] blob, uint expectedRules, uint expectedFallback, string name)
    {
        ProbeForwardedInfo before = device.GetForwardedInfo();
        device.SetForwardedBlob(blob);
        ProbeForwardedInfo after = device.GetForwardedInfo();
        Check.Equal(1, after.Installed, $"{name}：下发后 installed=1");
        Check.Equal(expectedRules, after.RuleCount, $"{name}：规则数回读一致");
        Check.Equal(expectedFallback, after.FallbackAction, $"{name}：fallback 回读一致");
        Check.Equal(blob.Length, after.TotalSize, $"{name}：blob 长度回读一致");
        Check.Equal(before.Generation + 1, after.Generation, $"{name}：generation 递增 1");
    }

    private static void Restore(WfpDevice device) =>
        device.SetForwardedBlob(ForwardedBlobBuilder.BuildEmpty(Protocol.RuleActionPermit));

    // 选一个真的能用的源端口：先 bind，再用同一个 4-tuple 真连一次 peer 探路。Windows 在旧连接
    // 还处于 TIME_WAIT 时会用 WSAEADDRINUSE 拒绝复用同一个 (源端口, 目的端口) 组合，仅靠
    // SO_REUSEADDR 探测 bind 发现不了；探路连接以 linger=0 中止（RST），不产生新的 TIME_WAIT。
    // 规则与实际连接都用返回值，保证「端口收窄」在端口不可用时仍然成立。
    private static int ResolveSourcePort(int preferred, IPEndPoint? peer, int timeoutMs, HashSet<int> used)
    {
        for (int candidate = preferred; candidate < preferred + 16 && candidate <= 65535; candidate++)
        {
            if ((candidate is 5985 or 5986) || used.Contains(candidate))
                continue;

            Socket? probe = null;
            try
            {
                probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.LingerState = new LingerOption(true, 0);
                probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                probe.Bind(new IPEndPoint(IPAddress.Any, candidate));
                if (peer is not null)
                {
                    using var timeout = new CancellationTokenSource(timeoutMs);
                    probe.ConnectAsync(peer, timeout.Token).AsTask().GetAwaiter().GetResult();
                }
                used.Add(candidate);
                return candidate;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                continue;
            }
            catch (Exception)
            {
                // peer 不可达等非端口问题：交给用例自己报错，不在这里换端口掩盖。
                used.Add(candidate);
                return candidate;
            }
            finally
            {
                probe?.Dispose();
            }
        }
        return preferred;
    }
    // 固定源端口的连接：出向帧的 remote_port 就是源端口，block 规则因此只命中这条测试流。
    private static bool TryConnect(IPEndPoint target, int sourcePort, int timeoutMs, out string error)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // 测试连接用完就中止（RST）：避免客户端先关留下的 TIME_WAIT 占住固定源端口，
            // 让套件可以反复跑。计数只关心帧，RST 与 FIN 都是普通控制帧。
            socket.LingerState = new LingerOption(true, 0);
            if (sourcePort != 0)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, sourcePort));
            }

            using var timeout = new CancellationTokenSource(timeoutMs);
            socket.ConnectAsync(target, timeout.Token).AsTask().GetAwaiter().GetResult();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static PingReply PingOnce(string address, int timeoutMs)
    {
        using var ping = new Ping();
        return ping.Send(address, timeoutMs);
    }

    private static bool PingSucceeds(string address, int timeoutMs)
    {
        try
        {
            return PingOnce(address, timeoutMs).Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }

    private static async Task<Socket?> AcceptAsync(TcpListener listener, int timeoutMs)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeoutMs);
            return await listener.AcceptSocketAsync(cancellation.Token);
        }
        catch
        {
            return null;
        }
    }

    private static Process? StartRestoreWatchdog(int seconds)
    {
        string? exe = Environment.ProcessPath;
        string? shell = Environment.GetEnvironmentVariable("COMSPEC");
        if (exe is null || shell is null)
            return null;
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/c ping -n {seconds + 1} 127.0.0.1 >nul & \"{exe}\" forwarded-rules --restore-only",
        };
        try
        {
            Process? process = Process.Start(startInfo);
            Console.WriteLine($"[forwarded] 看门狗已启动：{seconds}s 后恢复空 permit 表（进程被杀时的兜底）");
            return process;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Console.WriteLine($"[forwarded] 看门狗启动失败：{ex.Message}");
            return null;
        }
    }

    private static void PrintStats(string prefix, ProbeForwardedStats stats) =>
        Console.WriteLine($"[forwarded] {prefix} stats: calls={stats.ClassifyCalls} in={stats.InboundFrames} " +
                          $"out={stats.OutboundFrames} permitted={stats.Permitted} blocked={stats.Blocked} " +
                          $"matched={stats.Matched} (permit={stats.MatchedPermit} block={stats.MatchedBlock}) " +
                          $"fallback={stats.Fallback} (permit={stats.FallbackPermit} block={stats.FallbackBlock}) " +
                          $"self_injected={stats.SelfInjected} no_table={stats.NoTable} malformed={stats.Malformed} " +
                          $"non_ip={stats.NonIp}");

    private static void CheckInvariantsStable(WfpDevice device, string name)
    {
        ProbeForwardedStats stats = device.GetForwardedStats();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (InvariantsHold(stats))
                break;
            Thread.Sleep(25);
            stats = device.GetForwardedStats();
        }

        Check.Equal((long)stats.ClassifyCalls, (long)(stats.NoWriteRight + stats.InboundFrames + stats.OutboundFrames),
                    $"{name}：classify_calls == no_write_right + in + out");
        Check.Equal((long)(stats.InboundFrames + stats.OutboundFrames),
                    (long)(stats.SelfInjected + stats.NoTable + stats.Malformed + stats.NonIp + stats.Permitted +
                           stats.Blocked + stats.Redirected),
                    $"{name}：帧去向守恒");
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
        Check.Equal((long)stats.Fallback, (long)(stats.FallbackPermit + stats.FallbackBlock),
                    $"{name}：fallback == fallback_permit + fallback_block");
    }

    private static bool InvariantsHold(ProbeForwardedStats stats) =>
        stats.ClassifyCalls == stats.NoWriteRight + stats.InboundFrames + stats.OutboundFrames &&
        stats.InboundFrames + stats.OutboundFrames ==
        stats.SelfInjected + stats.NoTable + stats.Malformed + stats.NonIp + stats.Permitted + stats.Blocked +
            stats.Redirected &&
        stats.Permitted == stats.MatchedPermit + stats.FallbackPermit + stats.UdpReturnMiss &&
        stats.Blocked ==
            stats.MatchedBlock + stats.FallbackBlock + stats.RedirectBlocked + stats.RedirectReturnFailed +
            stats.UdpBlocked + stats.UdpReturnFailed &&
        stats.Matched == stats.MatchedPermit + stats.MatchedBlock + stats.MatchedRedirect &&
        stats.MatchedRedirect + stats.UdpFragmented ==
            stats.RedirectIngress + stats.RedirectBlocked + stats.UdpIngress + stats.UdpBlocked &&
        stats.Fallback == stats.FallbackPermit + stats.FallbackBlock;

    private static uint? FindAdapterIndex(string address)
    {
        IPAddress wanted = IPAddress.Parse(address);
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (!unicast.Address.Equals(wanted))
                    continue;
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

    private static string HostCidr(string address) => address.Contains(':') ? $"{address}/128" : $"{address}/32";

    private static string Subnet24(string address)
    {
        string[] parts = address.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}.0/24" : address;
    }

    private static HashSet<string> ParseOnly(string? text) =>
        text is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                  StringComparer.OrdinalIgnoreCase);

    private static bool ShouldRun(HashSet<string> only, string name) => only.Count == 0 || only.Contains(name);
}
