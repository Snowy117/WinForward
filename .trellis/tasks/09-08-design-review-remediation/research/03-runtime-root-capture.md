# Design Review — WinForward.Runtime root + Capture/

> Sub-agent report (explore), 2026-09-08. Read-only review; no files modified.
> FlowDispatcher.cs already reviewed by the lead agent — skipped here.
> All 16 target files ≤322 effective lines, total 1,619 effective lines, all within the ≤400 gate.

## 1. Composition shape (who constructs whom)

```
Program.RunCaptureLoopAsync  (src/WinForward.Cli/Program.cs:179 — composition root)
 ├─ SelfTrafficRegistry                                  [durable]
 ├─ NdisPacketReinjector(driver) ─────────────────────────────┐
 ├─ NdisAdapterListWatcher(driver)                            │ IPacketReinjector
 └─ DurableCaptureBundle.CreateAsync (DurableCaptureBundle.cs:63) │
     ├─ TcpProxyCoordinator                                   │
     │   ├─ TcpRedirectInjector(reinjector) ◄─────────────────┤ TcpRedirect→Capture
     │   └─ ListenerFactory / RelayFactory / redirectTable     │
     ├─ UdpProxyCoordinator                                   │
     │   ├─ Socks5UdpTransportFactory(selfTraffic)             │
     │   └─ UdpResponseReinjector(reinjector, udpTargets) ◄────┘ UdpProxy→Capture
     ├─ NdisPacketActionExecutor(reinjector, tcp, udp)         [durable]
     ├─ FlowDispatcher(executor, reverseHandler: tcp, fragmentHandler: tcp)
     ├─ IdleExpirySweeper(dispatcher, tcp, udp).Start()
     └─ UdpAdapterTargetSource  ← swapped fresh per scope install via UpdateUdpTargets

LayeredCaptureRunner  (assembled Program.cs:207; surface = RunAsync(ct) + SignalDegraded)
 ├─ NdisAdapterEnumerationProvider(driver)
 ├─ NdisCaptureGenerationFactory(driver, CapturePacketProcessor(Dispatcher, logger,
 │     onBatchCompleted: Executor.FlushPendingPasses), logger, onAdapterDegraded→SignalDegraded)
 ├─ watcher / policy / disposeDurable=bundle.DisposeAsync / onScopeInstalled=UpdateUdpTargets
 └─ per generation: factory.Create(scope)                            [generation-scoped]
      ├─ NdisAdapterModeController(driver, adapters)
      ├─ MultiAdapterCaptureLoop(driver, adapters, processor) → NdisCapturePump × N
      └─ TransactionalCaptureRuntime(modes, loop) →RuntimeCaptureGeneration→ ICaptureGeneration

Teardown ordering (structurally enforced, see §3 strength 3)
```

## 2. Seam inventory (production adapters / test fakes)

| Seam (definition site) | Production impl | Test doubles |
|---|---|---|
| `IPacketReinjector` (NdisPacketReinjector.cs:14) | 1 (NdisPacketReinjector) | 2 dedicated fakes + 2 using files |
| `IPacketActionExecutor` (FlowDispatcher.cs:57) | 1 (NdisPacketActionExecutor) | 6 (Fake×2/ThrowingPass/Recording/Noop/Counting) |
| `ICaptureGenerationFactory` / `ICaptureGeneration` (NdisCaptureGeneration.cs:29/17) | 1 / 1 | CaptureRunnerFakes 1+ each |
| `IAdapterEnumerationProvider` (AdapterEnumeration.cs:26) | 1 | 1 (CaptureRunnerFakes) |
| `IAdapterListChangeSource` (AdapterListWatcher.cs:13) | 1 (plus internal driver-less test ctor :50) | 1 (FakeAdapterListChangeSource) |
| `IAdapterModeController` / `IPacketCaptureLoop` (CaptureLifecycle.cs:17/24) | 1 each | 2 test files each providing their own |
| `IRuntimeLogger` (RuntimeLogging.cs:11) | 2 (Null/Console) | 1 (RecordingRuntimeLogger) |
| `ISelfTrafficGuard` (root) | 1 | 4 test files |

**The reinjection seam is real**: the batching contract has dedicated tests (NdisPacketActionExecutorBatchingTests), fakes snapshot inside the call to defeat aliasing (PacketReinjectorFakes.cs:62-72), and `DebugAssertNoPendingPasses` (DEBUG-only) pins the "every pump iteration must flush" convention.

## 3. Top 3 strengths

1. **LayeredCaptureRunner is a true deep module**: surface is only `RunAsync` + `SignalDegraded` (LayeredCaptureRunner.cs:84,92), internally encapsulating the generation factory, enumeration provider, change source, storm-guard, no-op diff skip, durable single-flight teardown, fail-closed fault propagation. `RefreshDemandGate` (:362-393) is a clean coalescing async demand gate.
2. **Real seams everywhere, contracts guarded**: Windows details all hidden behind `[SupportedOSPlatform]` adapters; runner/lifecycle unit-testable on any OS; executor batching contract double-locked by DEBUG assertion + snapshot fakes.
3. **Teardown/state discipline**: single-flight disposal (CaptureLifecycle.cs:183-190 `_cleanupTask ??=`; DurableCaptureBundle.cs:157-164), reverse-per-adapter mode restoration (CaptureLifecycle.cs:168), degradation-recovery vs shutdown-recovery concurrency overlap handled with snapshot + idempotent fallback (:160-166 comments).

## 4. Top issues (ranked)

1. **Durable executor's pass lane leaks slots across generations → silently degraded batching** (NdisPacketActionExecutor.cs:39 `PendingLaneCapacity=8`; :108-109 "lanes … are never removed"; DurableCaptureBundle.cs:96 executor built once per run; AdapterEnumeration.cs:11-12 handles rebuilt fresh per refresh). Every adapter-list refresh mints brand-new (handle, direction) keys: after ~4 refreshes all 8 slots are occupied by dead lanes, after which all passes permanently take the :83-90 immediate single-send degraded path — **no log, no telemetry** (`PendingPassCount` is internal and nobody publishes it). The batching IOCTL contract silently rots on long-running gateways. Fix direction: clear lanes on generation stop, or notify the executor of scope changes like `UpdateUdpTargets` does.
2. **ClassifyNonFlow uses the `IPAddress` class on the classify path** (PacketFlowClassifier.cs:40 `Endpoint.From(IPAddress.Any, 0)`, going through the class-conversion overload at Domain.cs:51). hot-path.md contract 1 states verbatim "IPAddress (class) never appears in parse/classify/flow-key code". Non-flow frames (ARP/ND) are persistently high-frequency on a gateway LAN — exactly the ARP incident shape the spec records. Should use an `IPAddressValue` IPv4-any constant.
3. **Capacity degradation fully silent** (NdisPacketActionExecutor.cs:29-31 comment admits "degrades those passes to immediate single sends"): machines with >4 NICs never batch from generation 0 onward, no warn nor event, violating the spirit of the logging spec "recoverable … faults must be observable"; and the 8-lane ceiling has no configuration surface.
4. **`PassAsync` null-reference exception names the wrong parameter** (NdisPacketActionExecutor.cs:55: `packet.Lease is null` but `throw new ArgumentNullException(nameof(packet))`) — misleading diagnostics, one-minute fix.
5. **`TransactionalCaptureRuntime.StartAsync` naming trap** (CaptureLifecycle.cs:57-66): named Start, but the returned ValueTask completes only when the whole run ends; `RuntimeCaptureGeneration.RunAsync` (NdisCaptureGeneration.cs:110-113) relies on that semantic. Correct but easy to misuse (ValueTask must not be awaited twice).

## 5. Cross-group edge audit

All `using WinForward.Runtime.*` within src:

| Edge | Evidence | Verdict |
|---|---|---|
| root→TcpRedirect (only `TcpRedirectOutcome`, via fragmentHandler delegate) | FlowDispatcher.cs:6,228-280 | ✓ sanctioned |
| root→Coordinators (Tcp+Udp) | IdleExpirySweeper.cs:2-3 | ✓ sanctioned |
| Capture→Coordinators | NdisPacketActionExecutor.cs:6-7 | ✓ sanctioned |
| TcpRedirect→Capture (`IPacketReinjector`) | TcpRedirectInjector.cs:3 | ✓ sanctioned |
| UdpProxy→Capture (`IPacketReinjector`) | UdpResponseReinjector.cs:6 | ✓ sanctioned |
| TcpRedirect→Socks5 | TcpProxyRelay.cs:8 | ✓ sanctioned |
| **UdpProxy→Socks5** | UdpProxyCoordinator.cs:5, UdpProxySession.cs:7 | **✗ beyond sanctioned set** |

The only out-of-bounds edge is UdpProxy→Socks5, and what it uses is not `Socks5Server` (which lives in WinForward.Configuration/ConfigurationModels.cs — no violation there) but **SOCKS5 datagram codec types** (`Socks5UdpDatagram`/`Socks5UdpHeaderSize`/`Socks5UdpReceiveResult`/`Socks5UdpReceiveSkipReason`, defined in Socks5/Socks5UdpTransport.cs). Semantically "UdpProxy reuses the SOCKS5 datagram wire codec" — these types look more like a shared protocol layer; suggest either adding this edge to the sanctioned set with rationale, or moving the datagram codec into Protocols/Core.

## 6. Spec violation list

- **hot-path #1**: PacketFlowClassifier.cs:40 (see issue 2) — the only substantive violation.
- Everything else verified clean: in-place reinjection ✓ (executor :57-75, materialized frames take the pooled-copy path); lease `Release()` in processor finally ✓ (CapturePacketProcessor.cs:99-102); all trace `IsEnabled`-guarded ✓ (incl. `LogPacket` double-check); idle sweep fixed order tcp→flows→udp ✓ (IdleExpirySweeper.cs:73-76); sweep failure isolation + rate-limited logging ✓; bundle teardown sweeper→udp→tcp ✓ (DurableCaptureBundle.cs:166-183); self-traffic registration before SYN ✓ (TcpProxyRelay.cs:39, Socks5UdpTransport.cs:172,193); coordinators torn down before mode restoration — structurally enforced by `TeardownAsync` ordering (LayeredCaptureRunner.cs:275-298: first `StopGenerationAsync` awaits generation cleanup (pump stop + mode restore), then `disposeDurableAsync`), not merely by convention.

**Overall**: deep-module discipline and seam design here are textbook-grade; the only genuinely urgent item is the lane leak (issue 1) — recommend scheduling it into a fix plan first.
