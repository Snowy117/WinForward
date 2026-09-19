# IDE1006 站点清单（base c8a5453，共 230）与命名约定处置

用户裁定约定（2026-09-19，含两轮澄清）：
- public/protected（可能公开暴露的）字段 → PascalCase，无前缀；公开字段出于性能允许保留，命名同 PascalCase
- internal 与 private 同级：static → s_camelCase（[ThreadStatic] → t_camelCase），实例字段 → _camelCase
- private/internal const → PascalCase（MaxBufferSize）；局部 const → camelCase
- editorconfig 按声明的可访问性匹配（无法区分 internal 类型上的 public 字段，这类字段保持 PascalCase）

## A1 — private static 缺 s_：67 处（66 改名 + 1 特例）
清单一（改名 s_camelCase；PacketRuntime.cs:28 t_recycleCache 除外）：
- benchmarks/WinForward.Benchmarks/Stability/SessionFootprintScenario.cs:12  `private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);`
- benchmarks/WinForward.Benchmarks/Stability/SessionFootprintScenario.cs:13  `private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);`
- benchmarks/WinForward.Benchmarks/Stability/SessionFootprintScenario.cs:11  `private static readonly byte[] PopulatePayload = [1];`
- benchmarks/WinForward.Benchmarks/Perf/UdpSessionBenchmarks.cs:26  `private static readonly byte[] Payload = [1];`
- benchmarks/WinForward.Benchmarks/Perf/UdpSessionBenchmarks.cs:25  `private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);`
- benchmarks/WinForward.Benchmarks/Stability/StabilityShared.cs:30  `private static readonly string[] ProductEventNames =`
- benchmarks/WinForward.Benchmarks/Perf/ChecksumBenchmarks.cs:48  `private static readonly Vector256<byte> SwapAdjacentBytes = Vector256.Create(`
- benchmarks/WinForward.Benchmarks/Stability/TcpEofScenario.cs:12  `private static readonly TimeSpan RelaySettleTimeout = TimeSpan.FromSeconds(30);`
- benchmarks/WinForward.Benchmarks/Stability/TcpEofScenario.cs:13  `private static readonly TimeSpan ReceiverTimeout = TimeSpan.FromSeconds(10);`
- benchmarks/WinForward.Benchmarks/Stability/TcpThroughputScenario.cs:26  `private static readonly TimeSpan RelaySettleTimeout = TimeSpan.FromSeconds(30);`
- benchmarks/WinForward.Benchmarks/Stability/StabilityContext.cs:9  `private static readonly JsonSerializerOptions Serialization = new()`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:22  `private static readonly TimeSpan ControlWindow = TimeSpan.FromSeconds(5);`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:23  `private static readonly TimeSpan PostWindow = TimeSpan.FromSeconds(5);`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:24  `private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:25  `private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:26  `private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromMilliseconds(50);`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:27  `private static readonly TimeSpan BurstPollInterval = TimeSpan.FromMilliseconds(20);`
- benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:28  `private static readonly TimeSpan MinimumBurstWindow = TimeSpan.FromSeconds(30);`
- benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs:19  `private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);`
- benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs:20  `private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);`
- benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs:21  `private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromMilliseconds(50);`
- benchmarks/WinForward.Benchmarks/Stability/UdpRawBaselineScenario.cs:19  `private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);`
- benchmarks/WinForward.Benchmarks/Stability/UdpRawBaselineScenario.cs:20  `private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);`
- benchmarks/WinForward.Benchmarks/Stability/UdpRawBaselineScenario.cs:21  `private static readonly TimeSpan WarmupPollInterval = TimeSpan.FromMilliseconds(50);`
- benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5TcpServer.cs:104  `private static readonly byte[] NoAuthMethodReply = [5, 0];`
- benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5TcpServer.cs:105  `private static readonly byte[] ConnectSuccessReply = [5, 0, 0, 1, 0, 0, 0, 0, 0, 0];`
- benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5TcpServer.cs:106  `private static readonly byte[] RequestFailureReply = [5, 1, 0, 1, 0, 0, 0, 0, 0, 0];`
- benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:41  `private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(250);`
- benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:42  `private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);`
- benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:43  `private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);`
- benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:40  `private static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(2);`
- benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:705  `private static readonly Endpoint Destination = Endpoint.From(IPAddress.Parse("192.0.2.80`
- benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UdpServer.cs:119  `private static readonly byte[] RequestFailureReply = [5, 1, 0, 1, 0, 0, 0, 0, 0, 0];`
- benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UdpServer.cs:118  `private static readonly byte[] NoAuthMethodReply = [5, 0];`
- src/WinForward.Core/PacketRuntime.cs:28  `private static PacketLease? t_recycleCache;`  ← [ThreadStatic] t_ 特例，保持 t_（见下）
- src/WinForward.NdisApi/NdisCapture.cs:108  `private static readonly TimeSpan TransientRetryDelayCap = TimeSpan.FromMilliseconds(1600`
- src/WinForward.NdisApi/NdisCapture.cs:107  `private static readonly TimeSpan DefaultTransientRetryBaseDelay = TimeSpan.FromMilliseco`
- src/WinForward.Protocols/PacketChecksums.cs:17  `private static readonly Vector256<byte> SwapAdjacentBytes = Vector256.Create(`
- src/WinForward.Runtime/Capture/AdapterEnumeration.cs:49  `private static readonly IReadOnlyDictionary<string, string> EmptyFingerprints = new Dict`
- src/WinForward.Runtime/IdleExpirySweeper.cs:16  `private static readonly TimeSpan SweepFailureLogInterval = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/Socks5/Socks5AddressCache.cs:16  `private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> DefaultR`
- src/WinForward.Runtime/TcpRedirect/TcpPendingSynSetup.cs:70  `private static readonly TimeSpan SetupFailureCooldown = TimeSpan.FromSeconds(1);`
- src/WinForward.Runtime/TcpRedirect/TcpPendingSynSetup.cs:62  `private static readonly TimeSpan RetentionTtl = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs:21  `private static readonly TimeSpan BoundedAcceptRetryDelay = TimeSpan.FromMilliseconds(100`
- src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:32  `private static readonly TimeSpan RateLimitedWarnInterval = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs:16  `private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(30);`
- src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs:26  `private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> DefaultA`
- src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:96  `private static readonly NativeBufferPool SharedPumpBufferPool = new(PumpBufferSize, capa`
- src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs:40  `private static readonly TimeSpan StructuredReinjectLogInterval = TimeSpan.FromSeconds(30`
- src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs:35  `private static readonly TimeSpan MissingOriginLogInterval = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:30  `private static readonly TimeSpan SetupQueueDatagramTtl = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/UdpProxy/UdpSetupQueueBudget.cs:25  `private static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs:47  `private static readonly TimeSpan TombstoneGracePeriod = TimeSpan.FromSeconds(60);`
- src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:123  `private static readonly Action<Socket> DisableUdpConnectionResetAction = DisableUdpConne`
- src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:120  `private static readonly byte[] DisableValue = { 0, 0, 0, 0 };`
- src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:34  `private static readonly TimeSpan RateLimitedLogInterval = TimeSpan.FromSeconds(5);`
- src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:43  `private static readonly TimeSpan ActivityPropagationInterval = TimeSpan.FromMilliseconds`
- src/WinForward.Runtime/UdpProxy/UdpSetupCooldownTable.cs:19  `private static readonly TimeSpan SetupFailureCooldown = TimeSpan.FromSeconds(1);`
- src/WinForward.Windows/AdapterLocalAddressProvider.cs:48  `private static readonly TimeSpan SnapshotSafetyTimeToLive = TimeSpan.FromSeconds(30);`
- src/WinForward.Windows/ProcessAttribution.cs:15  `private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(2);`
- tests/WinForward.Core.Tests/AdapterTransientRetryLogGateTests.cs:15  `private static readonly long FiveSeconds = TimeSpan.FromSeconds(5).Ticks;`
- tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs:23  `private static readonly IPAddress ClientIpv4 = IPAddress.Parse("192.0.2.10");`
- tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs:24  `private static readonly IPAddress DestIpv4 = IPAddress.Parse("192.0.2.53");`
- tests/WinForward.Core.Tests/UdpProxySessionTests.cs:18  `private static readonly NativeBufferPool ReceiveWindowPool = new(1537);`
- tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs:28  `private static readonly Socks5Server Server = new("primary", "127.0.0.1", 1080, null, nu`
- tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs:26  `private static readonly IPAddress ClientIpv4 = IPAddress.Parse("192.0.2.10");`
- tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs:27  `private static readonly IPAddress DestIpv4 = IPAddress.Parse("192.0.2.53");`

## A2 — internal static 现为 PascalCase：9 处（改名 s_camelCase）
- src/WinForward.Runtime/InterceptionHealthMonitor.cs:37  DefaultWindow
- src/WinForward.Runtime/InterceptionHealthMonitor.cs:38  DefaultTriggerCooldown
- src/WinForward.Runtime/InterceptionHealthMonitor.cs:39  DegradedTriggerSpacing
- src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:33  DefaultMinimumRefreshInterval
- src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:36  DefaultPeriodicRefreshInterval
- src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:20  RelayConnectAttemptTimeout
- src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:103  StallTimeout
- src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:107  ArmThrottleTicks
- src/WinForward.Runtime/TcpRedirect/ClientResetInjector.cs:28  CapacityResetCooldownWindow

## A3 — internal 实例字段现为 PascalCase：12 处（改名 _camelCase）
- src/WinForward.Runtime/SetupExecutor.cs:23  Handler
- src/WinForward.Runtime/SetupExecutor.cs:25  Completion
- src/WinForward.Runtime/SetupExecutor.cs:26  Flow
- src/WinForward.Runtime/SetupExecutor.cs:27  Server
- src/WinForward.Runtime/SetupExecutor.cs:28  CancellationToken
- src/WinForward.Runtime/SetupExecutor.cs:31  Tcp
- src/WinForward.Runtime/SetupExecutor.cs:34  Udp
- src/WinForward.Runtime/SetupExecutor.cs:55  Entry
- src/WinForward.Runtime/SetupExecutor.cs:56  Frame
- src/WinForward.Runtime/SetupExecutor.cs:72  FlowGeneration
- src/WinForward.Runtime/SetupExecutor.cs:73  ClientMac
- src/WinForward.Runtime/SetupExecutor.cs:74  Slot

## B — 非私有实例字段被判缺 _：76 处 → 全部声明为 public，保持 PascalCase，零改名
- src/WinForward.Windows/IPHelperAbi.cs × 33（public ABI 结构/internal 类型上的 public 字段）
- src/WinForward.NdisApi/NdisApiAbi.cs × 24（public ABI 结构/internal 类型上的 public 字段）
- src/WinForward.Runtime/SetupExecutor.cs × 10（public ABI 结构/internal 类型上的 public 字段）
- src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs × 3（public ABI 结构/internal 类型上的 public 字段）
- src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs × 3（public ABI 结构/internal 类型上的 public 字段）
- tests/WinForward.Core.Tests/UdpSessionSetupTests.cs × 2（public ABI 结构/internal 类型上的 public 字段）
- tests/WinForward.Core.Tests/RuntimeHeartbeatTests.cs × 1（public ABI 结构/internal 类型上的 public 字段）

## C — 'Prefix _ not expected'（NativeLease）：2 处 → 配置修正后合规（internal 实例 → _camelCase），零改动
- src/WinForward.Core/NativeBufferPool.cs:165  `internal readonly NativeBufferPool? _pool;`
- src/WinForward.Core/NativeBufferPool.cs:166  `internal readonly void* _pointer;`

## D — 局部 const 被判 PascalCase：85 处 → constants 规则去掉 local，零改名

## E — [ThreadStatic] t_ 特例（全仓唯一）
- src/WinForward.Core/PacketRuntime.cs:27-28 `[ThreadStatic] private static PacketLease? t_recycleCache;`
- 方案1：editorconfig 为 internal+private static 增加 t_camelCase 规则（实证多规则'任一满足'语义）；方案2（兜底）：单站点 `#pragma warning disable IDE1006` + 理由。

## 执行流（用户指示：先改 editorconfig，再跑 dotnet format）
1. 改 editorconfig（5 处）：static_fields→{private,private_protected,internal}+s_；instance_fields→同可见性+_；
   non_private_static_fields / non_private_readonly_fields 去掉 internal、private_protected；constants 去掉 local；(可选)t_ 规则。
2. `dotnet format style WinForward.slnx --no-restore --severity info --diagnostics IDE1006` 应用 87 处改名（跨项目引用若未传播，按构建错误补改）。
3. diff 审查 + build + test；残余 IDE1006 逐点处置。
