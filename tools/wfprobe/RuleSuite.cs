using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace WinForward.Probe;

internal static class RuleSuite
{
    // 一个必然不匹配的进程串（同样是 ALE_APP_ID 的形态），用来验证进程选择器真的参与比较。
    private const string OtherAppId = @"\device\harddiskvolume3\windows\system32\not-wfprobe.exe";

    public static async Task<int> RunAsync(Options options)
    {
        int replaceIterations = options.GetInt("iterations", 100);
        int timeoutMs = options.GetInt("timeout-ms", 5000);
        int settleMs = options.GetInt("settle-ms", 2000);
        string appId = AppId.ForCurrentProcess();
        int processId = Environment.ProcessId;
        Console.WriteLine($"[rules] pid={processId} app-id={appId}");

        using var device = new WfpDevice();
        device.ResetStats();
        PoolTagUsage? poolBefore = PoolTags.Find(Protocol.PoolTag);

        using var permitServer = new EchoServer(IPAddress.Loopback);
        using var blockServer = new EchoServer(IPAddress.Loopback);
        using var redirectTargetServer = new EchoServer(IPAddress.Loopback);
        using var fallbackServer = new EchoServer(IPAddress.Loopback);
        var redirectListener = new TcpListener(IPAddress.Loopback, 0);
        redirectListener.Start();
        int listenerPort = ((IPEndPoint)redirectListener.LocalEndpoint).Port;

        var config = new ProbeConfig
        {
            AbiVersion = Protocol.AbiVersion,
            Enabled = 1,
            ListenerProcessId = (uint)processId,
            ListenerPort = (ushort)listenerPort,
            SelfProcessCount = 0,
        };
        device.SetConfig(config);
        Program.PrintConfig("rules", device.GetConfig());

        // 顺序冲突：更早的 permit 与 block 可能命中同一批连接时，编译与下发都必须拒绝。
        {
            var conflicting = new[]
            {
                TestRule.PermitPort(50001, appId),
                TestRule.BlockPort(50001, appId),
            };
            bool builderRejected = false;
            string builderMessage = string.Empty;
            try
            {
                _ = BlobBuilder.Build(conflicting, Protocol.RuleActionPermit);
            }
            catch (InvalidOperationException ex)
            {
                builderRejected = true;
                builderMessage = ex.Message;
            }
            Check.True(builderRejected && builderMessage.Contains("#0", StringComparison.Ordinal) &&
                       builderMessage.Contains("#1", StringComparison.Ordinal),
                       "冲突规则在编译期被拒绝并指出两条规则", builderMessage);

            byte[] uncheckedBlob = BlobBuilder.BuildUnchecked(conflicting, Protocol.RuleActionPermit);
            ProbeRuleInfo before = device.GetRuleInfo();
            bool driverRejected = false;
            try
            {
                device.SetRuleBlob(uncheckedBlob);
            }
            catch (Win32Exception ex)
            {
                driverRejected = ex.NativeErrorCode == 87;
            }
            Check.True(driverRejected, "冲突 blob 被驱动独立拒绝", "STATUS_INVALID_PARAMETER");
            Check.Equal(before.Generation, device.GetRuleInfo().Generation, "冲突 blob 不改变 generation");
            Console.WriteLine($"[rules] conflict: builder-rejected={builderRejected} driver-rejected={driverRejected} " +
                              $"generation={before.Generation}->{device.GetRuleInfo().Generation}");
        }

        // fallback=block 与非 block 规则组合：编译期拒绝 + 驱动独立拒绝。
        {
            var conflictingFallback = new[] { TestRule.PermitPort(50003, appId) };
            bool builderRejected = false;
            string builderMessage = string.Empty;
            try
            {
                _ = BlobBuilder.Build(conflictingFallback, Protocol.RuleActionBlock);
            }
            catch (InvalidOperationException ex)
            {
                builderRejected = true;
                builderMessage = ex.Message;
            }
            Check.True(builderRejected && builderMessage.Contains("fallback", StringComparison.OrdinalIgnoreCase),
                       "fallback=block 冲突：编译期拒绝", builderMessage);

            byte[] uncheckedBlob = BlobBuilder.BuildUnchecked(conflictingFallback, Protocol.RuleActionBlock);
            ProbeRuleInfo beforeFallback = device.GetRuleInfo();
            bool driverRejected = false;
            try
            {
                device.SetRuleBlob(uncheckedBlob);
            }
            catch (Win32Exception ex)
            {
                driverRejected = ex.NativeErrorCode == 87;
            }
            Check.True(driverRejected, "fallback=block 冲突 blob 被驱动拒绝", "STATUS_INVALID_PARAMETER");
            Check.Equal(beforeFallback.Generation, device.GetRuleInfo().Generation, "fallback 冲突 blob 不改变 generation");
            Console.WriteLine($"[rules] fallback-conflict: builder-rejected={builderRejected} " +
                              $"driver-rejected={driverRejected} " +
                              $"generation={beforeFallback.Generation}->{device.GetRuleInfo().Generation}");
        }

        byte[] mainBlob = BlobBuilder.Build(new[]
        {
            TestRule.PermitPort(permitServer.Port, appId),
            TestRule.BlockPort(blockServer.Port),
            TestRule.RedirectPort(redirectTargetServer.Port),
        }, Protocol.RuleActionPermit);
        // fallback=block 只允许与 block 规则共存（任何非 block 规则都会被整块拒绝）。
        byte[] fallbackBlockBlob = BlobBuilder.Build(new[]
        {
            TestRule.BlockPort(blockServer.Port),
        }, Protocol.RuleActionBlock);
        byte[] wrongProcessBlob = BlobBuilder.Build(new[]
        {
            TestRule.PermitPort(permitServer.Port, AppId.ForPath(@"C:\Windows\System32\notepad.exe")),
        }, Protocol.RuleActionPermit);

        int redirectedConnections = 0;
        var permitTarget = new IPEndPoint(IPAddress.Loopback, permitServer.Port);
        var blockTarget = new IPEndPoint(IPAddress.Loopback, blockServer.Port);
        var redirectTarget = new IPEndPoint(IPAddress.Loopback, redirectTargetServer.Port);
        var fallbackTarget = new IPEndPoint(IPAddress.Loopback, fallbackServer.Port);

        device.SetRuleBlob(mainBlob);
        ProbeRuleInfo info = device.GetRuleInfo();
        Check.Equal(1, info.Installed, "规则表已安装");
        Check.Equal(3, info.RuleCount, "规则数与下发一致");
        Check.Equal(Protocol.RuleActionPermit, info.FallbackAction, "兜底动作与下发一致");
        Check.Equal(mainBlob.Length, info.TotalSize, "规则 blob 总长与下发一致");

        // A3：permit 规则命中 —— 连接到达真实目的地并完成回显。
        {
            ProbeStats before = device.GetStats();
            bool connected = SocketHelpers.TryConnect(permitTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(connected, "permit 规则允许连接", error);
            string detail = "not-connected";
            bool echo = connected && SocketHelpers.TryEcho(socket!, "permit", timeoutMs, out detail);
            Check.True(echo, "permit 规则连接到达真实目的地", detail);
            socket?.Dispose();
            ProbeStats after = device.GetStats();
            Check.Equal(1, (long)(after.MatchedPermit - before.MatchedPermit), "matched_permit +1");
            Check.Equal(0, (long)(after.FallbackPermit - before.FallbackPermit), "命中时 fallback 不计数");
            Check.Equal(0, (long)(after.Blocked - before.Blocked), "permit 不增加 blocked");
        }

        // A3：block 规则命中。redirect 层只把决定交给 auth 层（deferred），阻断发生在 AUTH_CONNECT。
        {
            int acceptedBefore = blockServer.Accepted;
            ProbeStats before = device.GetStats();
            bool connected = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(!connected, "block 规则拒绝连接（AUTH_CONNECT 层执行）", error);
            socket?.Dispose();
            await Task.Delay(200);
            Check.Equal(acceptedBefore, blockServer.Accepted, "block 规则下服务器没有 accept");
            ProbeStats after = device.GetStats();
            Check.Equal(1, (long)(after.MatchedBlock - before.MatchedBlock), "matched_block +1");
            Check.Equal(1, (long)(after.Blocked - before.Blocked), "blocked +1");
            Check.Equal(1, (long)(after.DeferredBlocks - before.DeferredBlocks), "deferred_blocks +1");
            ProbeEvent[] events = device.GetEvents(64);
            Check.True(events.Any(e => e.Decision == Protocol.DecisionBlockedRule &&
                                       e.RemotePort == blockServer.Port && e.MatchedRule == 1),
                       "block 事件带上命中规则序号", $"port={blockServer.Port}");
        }

        // 进程选择器在 block filter 上同样生效：同一端口，进程串匹配才拦。
        {
            byte[] processBlock = BlobBuilder.Build(
                new[] { TestRule.BlockPort(blockServer.Port, appId) }, Protocol.RuleActionPermit);
            device.SetRuleBlob(processBlock);
            bool connected = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(!connected, "进程串匹配的 block 规则拒绝连接", error);
            socket?.Dispose();

            byte[] otherProcessBlock = BlobBuilder.Build(
                new[] { TestRule.BlockPort(blockServer.Port, OtherAppId) },
                Protocol.RuleActionPermit);
            device.SetRuleBlob(otherProcessBlock);
            int acceptedBefore = blockServer.Accepted;
            bool allowed = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? allowedSocket, out string allowedError);
            Check.True(allowed, "进程串不匹配的 block 规则不拦连接", allowedError);
            allowedSocket?.Dispose();
            await Task.Delay(200);
            Check.Equal(acceptedBefore + 1, blockServer.Accepted, "进程串不匹配时连接到达目的地");

            device.SetRuleBlob(mainBlob);
        }

        // block filter 的网段条件（FWP_V4_ADDR_AND_MASK）。
        {
            byte[] networkBlock = BlobBuilder.Build(new[]
            {
                new TestRule
                {
                    Action = Protocol.RuleActionBlock,
                    PortStart = (ushort)blockServer.Port,
                    PortEnd = (ushort)blockServer.Port,
                    Network = "127.0.0.0/8",
                },
            }, Protocol.RuleActionPermit);
            device.SetRuleBlob(networkBlock);
            bool connected = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(!connected, "网段匹配的 block 规则拒绝连接", error);
            socket?.Dispose();

            byte[] otherNetworkBlock = BlobBuilder.Build(new[]
            {
                new TestRule
                {
                    Action = Protocol.RuleActionBlock,
                    PortStart = (ushort)blockServer.Port,
                    PortEnd = (ushort)blockServer.Port,
                    Network = "10.0.0.0/8",
                },
            }, Protocol.RuleActionPermit);
            device.SetRuleBlob(otherNetworkBlock);
            int acceptedBefore = blockServer.Accepted;
            bool allowed = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? allowedSocket, out string allowedError);
            Check.True(allowed, "网段不匹配的 block 规则不拦连接", allowedError);
            allowedSocket?.Dispose();
            await Task.Delay(200);
            Check.Equal(acceptedBefore + 1, blockServer.Accepted, "网段不匹配时连接到达目的地");

            device.SetRuleBlob(mainBlob);
        }

        // 非 ASCII 进程路径：ALE_APP_ID（UTF-16）→ 规则池（UTF-8）→ AUTH_CONNECT filter 的往返。
        {
            // apphost 把程序集名（wfprobe.dll）编进了自身，改 exe 文件名会找不到 dll；这里保留文件名，
            // 把整个程序放到中文目录下——路径里的非 ASCII 部分才是被验证的对象。
            string directory = @"C:\Users\张三";
            Directory.CreateDirectory(directory);
            string sourceDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string sourceBaseName = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
            string executable = Path.Combine(directory, "工具.exe");
            // apphost 把程序集名（wfprobe.dll / .runtimeconfig.json / .deps.json）编进了自身，
            // 只有 exe 能改名；把 exe 复制成中文名、依赖文件保持原名，路径里就同时有中文目录和中文文件名。
            File.Copy(Path.GetFullPath(Environment.ProcessPath!), executable, true);
            foreach (string extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            {
                File.Copy(Path.Combine(sourceDirectory, sourceBaseName + extension),
                          Path.Combine(directory, sourceBaseName + extension),
                          true);
            }
            try
            {
                string chineseAppId = AppId.ForPath(executable);
                Check.True(chineseAppId.Contains("张三", StringComparison.Ordinal) &&
                           chineseAppId.Contains("工具", StringComparison.Ordinal),
                           "中文进程路径归一化保留非 ASCII", chineseAppId);

                byte[] chineseBlob = BlobBuilder.Build(
                    new[] { TestRule.BlockPort(blockServer.Port, chineseAppId) }, Protocol.RuleActionPermit);
                device.SetRuleBlob(chineseBlob);
                ProbeStats beforeChinese = device.GetStats();
                int blockedExit = RunChild(executable,
                                           "client",
                                           "--remote",
                                           $"127.0.0.1:{blockServer.Port}",
                                           "--timeout-ms",
                                           "3000");
                ProbeStats afterChinese = device.GetStats();
                Check.True(blockedExit != 0, "中文路径的 block 规则命中", $"exit={blockedExit}");
                Check.AtLeast(1,
                              (long)(afterChinese.MatchedBlock - beforeChinese.MatchedBlock),
                              "中文路径在 redirect 层命中 block 规则（matcher 也看到了非 ASCII）");

                byte[] otherPortBlob = BlobBuilder.Build(
                    new[] { TestRule.BlockPort(65001, chineseAppId) }, Protocol.RuleActionPermit);
                device.SetRuleBlob(otherPortBlob);
                int allowedExit = RunChild(executable,
                                           "client",
                                           "--remote",
                                           $"127.0.0.1:{blockServer.Port}",
                                           "--timeout-ms",
                                           "3000");
                Check.Equal(0, allowedExit, "中文路径规则不误伤其他端口");
                Console.WriteLine($"[rules] chinese-path: app-id={chineseAppId} blocked-exit={blockedExit} " +
                                  $"allowed-exit={allowedExit}");

                device.SetRuleBlob(mainBlob);
            }
            finally
            {
                foreach (string name in new[] { "工具.exe", sourceBaseName + ".dll",
                                                sourceBaseName + ".runtimeconfig.json", sourceBaseName + ".deps.json" })
                {
                    try
                    {
                        File.Delete(Path.Combine(directory, name));
                    }
                    catch
                    {
                        // 子进程可能还持有镜像句柄，清理失败不影响断言。
                    }
                }
                try
                {
                    Directory.Delete(directory, false);
                }
                catch
                {
                    // 同上。
                }
            }
        }

        // 进程路径 UTF-8 边界：511 字节通过，512 字节整条置 0（不截断）。
        {
            byte[] path511 = Encoding.Unicode.GetBytes(new string('a', 511));
            byte[] path512 = Encoding.Unicode.GetBytes(new string('a', 512));
            (uint length511, string text511) = device.MeasureProcessPath(path511);
            Check.Equal(511, (long)length511, "511 字节路径完整通过");
            Check.Equal(511, text511.Length, "511 字节路径内容一致");
            (uint length512, _) = device.MeasureProcessPath(path512);
            Check.Equal(0, (long)length512, "512 字节路径整条不匹配（置 0，不截断）");

            (uint emojiLength, string emoji) = device.MeasureProcessPath(Encoding.Unicode.GetBytes("a😀b"));
            Check.Equal(6, (long)emojiLength, "代理对按 4 字节 UTF-8 展开");
            Check.True(emoji == "a😀b", "代理对内容正确", emoji);

            (uint brokenLength, _) = device.MeasureProcessPath(new byte[] { 0x00, 0xD8, 0x61, 0x00 });
            Check.Equal(0, (long)brokenLength, "孤立高代理项整条不匹配");

            // 三个非法代理项分支各自钉住：出现在路径开头之外时，必须整条置 0，不能留下前缀。
            (uint trailingHighLength, _) = device.MeasureProcessPath(new byte[] { 0x61, 0x00, 0x3D, 0xD8 });
            Check.Equal(0, (long)trailingHighLength, "结尾的孤立高代理项整条不匹配");
            (uint midHighLength, _) = device.MeasureProcessPath(new byte[] { 0x61, 0x00, 0x62, 0x00, 0x00, 0xD8, 0x63, 0x00 });
            Check.Equal(0, (long)midHighLength, "路径中间的孤立高代理项整条不匹配");
            (uint midLowLength, _) = device.MeasureProcessPath(new byte[] { 0x61, 0x00, 0x62, 0x00, 0x00, 0xDC, 0x63, 0x00 });
            Check.Equal(0, (long)midLowLength, "路径中间的孤立低代理项整条不匹配");

            // 3 字节序列的边界：508 ASCII + 一个 U+4E2D = 511 通过，509 + 3 = 512 拒绝。
            (uint threeByteOk, _) = device.MeasureProcessPath(
                Encoding.Unicode.GetBytes(new string('a', 508) + "\u4E2D"));
            Check.Equal(511, (long)threeByteOk, "3 字节序列的 511 边界通过");
            (uint threeByteOver, _) = device.MeasureProcessPath(
                Encoding.Unicode.GetBytes(new string('a', 509) + "\u4E2D"));
            Check.Equal(0, (long)threeByteOver, "3 字节序列的 512 边界被拒");
            Console.WriteLine($"[rules] boundary: utf8-511={length511} utf8-512={length512} " +
                              $"emoji={emojiLength} surrogate={brokenLength}");
        }

        // 驱动侧 selector 闸：超长/非法 UTF-8 的 permit blob 也必须整块拒绝（不能只靠上游编译器）。
        // 重叠判定的 ASCII 大小写折叠。
        {
            string longSelector = new string('a', 512);
            byte[] longBlob = BlobBuilder.BuildUnchecked(new[]
            {
                new TestRule
                {
                    Action = Protocol.RuleActionPermit,
                    PortStart = 50004,
                    PortEnd = 50004,
                    Processes = new[] { TestProcessSelector.FileName(longSelector) },
                },
            }, Protocol.RuleActionPermit);
            ProbeRuleInfo beforeLong = device.GetRuleInfo();
            bool longRejected = false;
            try
            {
                device.SetRuleBlob(longBlob);
            }
            catch (Win32Exception ex)
            {
                longRejected = ex.NativeErrorCode == 87;
            }
            Check.True(longRejected, "超长 selector 的 permit blob 被驱动拒绝", "STATUS_INVALID_PARAMETER");
            Check.Equal(beforeLong.Generation, device.GetRuleInfo().Generation, "超长 selector 不改变 generation");

            const string casePath = @"\device\harddiskvolume3\windows\system32\abc.exe";
            bool caseRejected = false;
            try
            {
                _ = BlobBuilder.Build(new[]
                {
                    TestRule.PermitPort(50005, casePath.ToUpperInvariant()),
                    TestRule.BlockPort(50005, casePath),
                }, Protocol.RuleActionPermit);
            }
            catch (InvalidOperationException)
            {
                caseRejected = true;
            }
            Check.True(caseRejected, "仅大小写不同的 selector 视为重叠（编译期）", "case-fold");
            Console.WriteLine($"[rules] selector-guard: long-rejected={longRejected} case-overlap={caseRejected}");
        }

        // A3：redirect 规则命中 —— 本地监听端接到连接并读回原始目的地。
        {
            int redirectsBefore = redirectedConnections;
            ProbeStats before = device.GetStats();
            Socket? server = await AcceptAsync(redirectListener, 0);
            bool connected = SocketHelpers.TryConnect(redirectTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(connected, "redirect 规则允许连接（目标被改写）", error);
            server ??= await AcceptAsync(redirectListener, timeoutMs);
            Check.True(server is not null, "监听端接到被重定向的连接", $"port={listenerPort}");
            if (server is not null)
            {
                using (server)
                {
                    byte[] raw = WfpSockets.QueryRedirectContext(server);
                    Check.Equal(36, raw.Length, "重定向 context 36B");
                    if (raw.Length == 36)
                    {
                        RedirectContext context = MemoryMarshal.Read<RedirectContext>(raw);
                        Check.Equal(Protocol.RedirectContextMagic, context.Magic, "context magic");
                        Check.Equal(2, context.RuleIndex, "context 记录命中规则序号");
                        Check.Equal(processId, (long)context.ProcessId, "context pid 是发起进程");
                        Check.Equal(redirectTarget.Port, context.OriginalPort, "context 原始端口");
                        Check.Equal(4, context.Family, "context 地址族");
                    }
                    byte[] payload = System.Text.Encoding.UTF8.GetBytes("redirect");
                    int received = 0;
                    try
                    {
                        server.ReceiveTimeout = timeoutMs;
                        socket!.ReceiveTimeout = timeoutMs;
                        server.Send(payload);
                        byte[] buffer = new byte[32];
                        received = socket.Receive(buffer);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[rules] redirect 数据通路异常：{ex.GetType().Name}: {ex.Message}");
                    }
                    Check.True(received == payload.Length, "重定向连接可以传输数据", $"received={received}B");
                }
                redirectedConnections++;
            }
            socket?.Dispose();
            ProbeStats after = device.GetStats();
            Check.Equal(1, (long)(after.MatchedRedirect - before.MatchedRedirect), "matched_redirect +1");
            Check.Equal(1, (long)(after.Redirected - before.Redirected), "redirected +1");
            Check.Equal(1, (long)(after.ContextsAllocated - before.ContextsAllocated), "contexts_allocated +1");
            Check.True(after.Redirected > before.Redirected && redirectedConnections > redirectsBefore,
                       "重定向计数与连接数同步", $"connections={redirectedConnections}");
        }

        // A4：未命中走 fallback_action（先 permit）。
        {
            ProbeStats before = device.GetStats();
            bool connected = SocketHelpers.TryConnect(fallbackTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(connected, "fallback=permit 时未命中连接放行", error);
            string detail = "not-connected";
            bool echo = connected && SocketHelpers.TryEcho(socket!, "fallback", timeoutMs, out detail);
            Check.True(echo, "fallback=permit 连接到达真实目的地", detail);
            socket?.Dispose();
            ProbeStats after = device.GetStats();
            Check.Equal(1, (long)(after.FallbackPermit - before.FallbackPermit), "fallback_permit +1");
            Check.Equal(0, (long)(after.MatchedPermit - before.MatchedPermit), "未命中时 matched_permit 不变");
        }

        // A4：fallback=block 下，未命中的连接被拒；命中的规则仍然先走 matched_block。
        device.SetRuleBlob(fallbackBlockBlob);
        {
            ProbeStats before = device.GetStats();
            int acceptedBefore = fallbackServer.Accepted;
            bool connected = SocketHelpers.TryConnect(fallbackTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(!connected, "fallback=block 时未命中连接被拒", error);
            socket?.Dispose();
            await Task.Delay(200);
            Check.Equal(acceptedBefore, fallbackServer.Accepted, "fallback=block 下服务器没有 accept");
            ProbeStats after = device.GetStats();
            Check.AtLeast(1, (long)(after.FallbackBlock - before.FallbackBlock), "fallback_block +1");
            Check.AtLeast(1, (long)(after.Blocked - before.Blocked), "blocked +1（fallback）");
            ProbeEvent[] events = device.GetEvents(64);
            Check.True(events.Any(e => e.Decision == Protocol.DecisionBlockedFallback &&
                                       e.RemotePort == fallbackServer.Port),
                       "fallback=block 事件", $"port={fallbackServer.Port}");

            // 关键回归：fallback=block 时命中的 block 规则仍要走 matched_block，且连接被拒。
            ProbeStats beforeHit = device.GetStats();
            bool hitConnected = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? hitSocket, out string hitError);
            Check.True(!hitConnected, "fallback=block 下命中规则仍被拒", hitError);
            hitSocket?.Dispose();
            ProbeStats afterHit = device.GetStats();
            Check.AtLeast(1, (long)(afterHit.MatchedBlock - beforeHit.MatchedBlock), "命中规则仍走 matched_block");
        }

        // 禁用开关必须真的撤掉 block filter（而不是只让 classify 早退）。
        device.SetRuleBlob(BlobBuilder.Build(new[] { TestRule.BlockPort(blockServer.Port) },
                                            Protocol.RuleActionPermit));
        {
            bool blocked = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? blockedSocket, out string blockedError);
            Check.True(!blocked, "block 规则生效（禁用前）", blockedError);
            blockedSocket?.Dispose();

            ProbeConfig disabled = config;
            disabled.Enabled = 0;
            device.SetConfig(disabled);
            int acceptedBefore = blockServer.Accepted;
            bool allowed = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? allowedSocket, out string allowedError);
            Check.True(allowed, "enabled=0 解除 block filter（连接放行）", allowedError);
            allowedSocket?.Dispose();
            await Task.Delay(200);
            Check.Equal(acceptedBefore + 1, blockServer.Accepted, "enabled=0 时连接到达目的地");

            device.SetConfig(config);
            bool blockedAgain = SocketHelpers.TryConnect(blockTarget, timeoutMs, out Socket? againSocket, out string againError);
            Check.True(!blockedAgain, "enabled=1 重新挂上 block filter", againError);
            againSocket?.Dispose();
            Console.WriteLine($"[rules] disabled: filter-off={allowed} filter-on={!blockedAgain}");
            device.SetRuleBlob(mainBlob);
        }

        // 进程选择器：换成不匹配的进程串，同端口规则不应命中。
        device.SetRuleBlob(wrongProcessBlob);
        {
            ProbeStats before = device.GetStats();
            bool connected = SocketHelpers.TryConnect(permitTarget, timeoutMs, out Socket? socket, out string error);
            Check.True(connected, "进程串不匹配时规则不命中（fallback=permit）", error);
            string detail = "not-connected";
            bool echo = connected && SocketHelpers.TryEcho(socket!, "wrong-process", timeoutMs, out detail);
            Check.True(echo, "进程串不匹配时连接按 fallback 放行", detail);
            socket?.Dispose();
            ProbeStats after = device.GetStats();
            Check.Equal(0, (long)(after.MatchedPermit - before.MatchedPermit), "进程串不匹配时 matched_permit 不变");
            Check.Equal(1, (long)(after.FallbackPermit - before.FallbackPermit), "进程串不匹配时 fallback_permit +1");
        }

        // A2：非法 blob 整体拒绝，且原规则表保持不变。
        device.SetRuleBlob(mainBlob);
        ProbeRuleInfo reference = device.GetRuleInfo();
        var invalidVariants = new (string Name, byte[] Blob)[]
        {
            ("magic 错", BlobBuilder.CorruptMagic(mainBlob)),
            ("版本错", BlobBuilder.CorruptVersion(mainBlob)),
            ("header_size 错", BlobBuilder.CorruptHeaderSize(mainBlob)),
            ("fallback 越界", BlobBuilder.CorruptFallback(mainBlob)),
            ("截断", BlobBuilder.Truncated(mainBlob)),
            ("短于头部", BlobBuilder.ShortBlob(mainBlob)),
            ("rules_offset 越界", BlobBuilder.RulesOffsetOutOfRange(mainBlob)),
            ("rules_offset 未对齐", BlobBuilder.RulesOffsetUnaligned(mainBlob)),
            ("process_pool 越界", BlobBuilder.ProcessPoolOutOfRange(mainBlob)),
            ("selector_pool 越界", BlobBuilder.SelectorPoolOutOfRange(mainBlob)),
            ("network_pool 越界", BlobBuilder.NetworkPoolOutOfRange(mainBlob)),
            ("port_pool 越界", BlobBuilder.PortPoolOutOfRange(mainBlob)),
            ("进程池末尾非 NUL", BlobBuilder.ProcessPoolUnterminated(mainBlob)),
        };
        foreach ((string name, byte[] bad) in invalidVariants)
        {
            ulong rejectedBefore = device.GetStats().RejectedBlobs;
            bool rejected = false;
            int errorCode = 0;
            try
            {
                device.SetRuleBlob(bad);
            }
            catch (Win32Exception ex)
            {
                rejected = true;
                errorCode = ex.NativeErrorCode;
            }
            Check.True(rejected && errorCode == 87, $"非法 blob 被拒绝：{name}", $"error={errorCode}");
            ProbeRuleInfo current = device.GetRuleInfo();
            Check.Equal(reference.Generation, current.Generation, $"拒绝后 generation 不变：{name}");
            Check.Equal(reference.RuleCount, current.RuleCount, $"拒绝后规则数不变：{name}");
            Check.Equal(reference.FallbackAction, current.FallbackAction, $"拒绝后兜底动作不变：{name}");
            Check.Equal(1, (long)(device.GetStats().RejectedBlobs - rejectedBefore), $"rejected_blobs +1：{name}");
        }
        {
            // 旧表仍然可用：permit 规则（进程串匹配）继续命中。
            ProbeStats before = device.GetStats();
            bool connected = SocketHelpers.TryConnect(permitTarget, timeoutMs, out Socket? socket, out string error);
            string detail = "not-connected";
            bool echo = connected && SocketHelpers.TryEcho(socket!, "after-reject", timeoutMs, out detail);
            Check.True(echo, "非法 blob 之后旧表仍然生效", $"{error} {detail}");
            socket?.Dispose();
            ProbeStats after = device.GetStats();
            Check.Equal(1, (long)(after.MatchedPermit - before.MatchedPermit), "旧表规则仍然命中");
        }

        // A6：连续替换 100 次，同时后台持续建立连接（读者/写者并发）。
        {
            int churnOk = 0;
            using var churn = new CancellationTokenSource();
            Task churnTask = Task.Run(async () =>
            {
                while (!churn.IsCancellationRequested)
                {
                    if (SocketHelpers.TryConnect(permitTarget, timeoutMs, out Socket? socket, out _))
                    {
                        if (SocketHelpers.TryEcho(socket!, "churn", timeoutMs, out _))
                            Interlocked.Increment(ref churnOk);
                        socket!.Dispose();
                    }
                    await Task.Delay(1);
                }
            });

            byte[] alternating = BlobBuilder.Build(new[]
            {
                TestRule.PermitPort(permitServer.Port, appId),
                TestRule.BlockPort(blockServer.Port),
                TestRule.RedirectPort(redirectTargetServer.Port),
                TestRule.BlockPort(65000),
            }, Protocol.RuleActionPermit);

            // 等后台循环真正跑起来：替换只要几十毫秒，线程池没调度到它就会得到假的 “0 次连接”。
            for (int waited = 0; waited < 200 && Volatile.Read(ref churnOk) < 3; waited++)
                await Task.Delay(10);
            Check.True(churnOk > 0, "替换开始前后台连接已经跑起来", $"echo={churnOk}");

            uint generationBefore = device.GetRuleInfo().Generation;
            bool allSucceeded = true;
            for (int index = 0; index < replaceIterations; index++)
            {
                try
                {
                    device.SetRuleBlob(index % 2 == 0 ? mainBlob : alternating);
                }
                catch
                {
                    allSucceeded = false;
                }
            }
            churn.Cancel();
            try
            {
                await churnTask;
            }
            catch
            {
                // 取消后台连接循环，异常不影响替换断言。
            }

            ProbeRuleInfo after = device.GetRuleInfo();
            Check.True(allSucceeded, "连续替换全部成功", $"iterations={replaceIterations}");
            Check.Equal(replaceIterations, after.Generation - generationBefore, "连续替换 generation +100");
            uint expectedRuleCount = (replaceIterations - 1) % 2 == 0 ? 3u : 4u;
            Check.Equal(expectedRuleCount, after.RuleCount, "替换后规则数与最后一块 blob 一致");
            Check.True(churnOk > 0, "替换期间 classify 持续放行", $"echo={churnOk}");
        }

        // 替换之后再走一次重定向，证明表仍然可用。
        {
            device.SetRuleBlob(mainBlob);
            Socket? server = await AcceptAsync(redirectListener, 0);
            bool connected = SocketHelpers.TryConnect(redirectTarget, timeoutMs, out Socket? socket, out _);
            server ??= await AcceptAsync(redirectListener, timeoutMs);
            Check.True(connected && server is not null, "替换压力之后重定向仍然工作", $"port={listenerPort}");
            if (server is not null)
            {
                using (server)
                {
                    byte[] raw = WfpSockets.QueryRedirectContext(server);
                    Check.Equal(36, raw.Length, "替换压力之后 context 仍然 36B");
                }
                redirectedConnections++;
            }
            socket?.Dispose();
        }

        HostRuleParitySuite.Run(device, timeoutMs);
        device.SetRuleBlob(mainBlob);

        config.Enabled = 0;
        device.SetConfig(config);
        await Task.Delay(settleMs);

        // 池标签：每次 TCP 重定向分配一块 36 字节 context，所有权交给 WFP 后由 WFP 释放。
        // 条目在第一次分配时创建，本次启动内计数归零后仍可读；刚开机/刚装驱动时基线为 null
        // 只表示「还没有任何分配」，按零参与差值。窗口结束时仍然读不到才是真的异常。
        // outstanding 只能和运行前基线比：connected UDP 的 redirect context 由 WFP 持有且本 boot
        // 不回收（udp-rules U12 实测 300s 仍 outstanding），这里不能假设全局归零。
        PoolTagUsage? poolAfter = PoolTags.Find(Protocol.PoolTag);
        Check.True(poolAfter is not null || redirectedConnections == 0,
                   "池标签在测量窗口内可读", poolAfter is null ? "PoolTags.Find 返回 null" : "ok");
        PoolTagUsage beforePool = poolBefore ?? PoolTagUsage.Zero(Protocol.PoolTag);
        PoolTagUsage afterPool = poolAfter ?? PoolTagUsage.Zero(Protocol.PoolTag);
        Check.Equal(redirectedConnections, (long)(afterPool.NonPagedAllocs - beforePool.NonPagedAllocs),
                    "context 池分配次数等于重定向次数");
        Check.Equal(redirectedConnections, (long)(afterPool.NonPagedFrees - beforePool.NonPagedFrees),
                    "context 池释放次数等于重定向次数（WFP 释放）");
        Check.Equal(beforePool.NonPagedOutstanding, afterPool.NonPagedOutstanding,
                    "context 池 outstanding 回到运行前基线");

        ProbeStats final = device.GetStats();
        Check.Equal(0, (long)final.ProcessPathMetadataSeen, "redirect 层没有 PROCESS_PATH metadata（实测）");
        Console.WriteLine($"[rules] stats: classify={final.ClassifyCalls} permitted={final.Permitted} blocked={final.Blocked} " +
                          $"redirected={final.Redirected} replaced={final.RulesReplaced} rejected={final.RejectedBlobs}");
        Console.WriteLine($"[rules] stats: matched permit/block/redirect={final.MatchedPermit}/{final.MatchedBlock}/{final.MatchedRedirect} " +
                          $"fallback permit/block/redirect={final.FallbackPermit}/{final.FallbackBlock}/{final.FallbackRedirect}");
        Console.WriteLine($"[rules] verdict={(Check.Failures == 0 ? "PASS" : "FAIL")} failures={Check.Failures}");
        return Check.Failures == 0 ? 0 : 6;
    }

    private static async Task<Socket?> AcceptAsync(TcpListener listener, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            return await listener.AcceptSocketAsync(cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static int RunChild(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动子进程");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

}
