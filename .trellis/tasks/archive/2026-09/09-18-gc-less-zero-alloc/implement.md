# Implementation Plan: GC-less zero-allocation hot paths

Ordered milestones; each is independently verifiable and revertable. Validation commands at repo root (Linux, nix shell provides dotnet-sdk_10).

## M0 — Baseline & observability (no behavior change)

- [x] Run full baseline: `dotnet test -c Release` (expect 589 pass), `dotnet build -c Release` zero-warning.
- [x] Extend `RuntimeHeartbeat` payload: `GC.CollectionCount(0/1/2)`, `GC.GetTotalAllocatedBytes(precise: false)`, startup mark (collections at "running" state), aggregate native pool occupancy placeholder.
- [x] Record measured post-startup managed heap (basis for HeapHardLimit fuse in M5) from a loopback soak run.

Validation: `dotnet test -c Release`; heartbeat fields visible in `--stability --scenario footprint` JSONL.

## M1 — Leak fixes (L1, L2) + pool stats

- [x] `NdisPacketActionExecutor.AppendPass` overflow path: try/finally around send + buffer return (design §5).
- [x] `NdisPacketBufferPool.Dispose`: post-dispose drain-until-empty loop (L2).
- [x] Add rent/return/inPool/disposed interlocked stats to `NdisPacketBufferPool`; balance tests: rent N → return N → dispose → assert balanced; overflow-throw regression test for L1 (send throws ⇒ buffer still returned).

Validation: `dotnet test -c Release --filter "NdisPacketBufferPool|NdisPacketActionExecutor"`; full suite green.

## M2 — NativeBufferPool family + per-packet paths (A1–A4)

- [x] New `NativeBufferPool` (size-class pool: rent lease struct, idempotent return, dispose-drain, stats) + unit tests mirroring NdisPacketBufferPoolTests.
- [x] A2: `TcpProxyCoordinator.cs:421` `IsTcpSyn` over `InspectionSpan`.
- [x] A3: mid-flow/reverse TCP paths rent native frame copies; `TcpFrameRewriter` in place; `finally` return; remove `Lease.Frame` from these paths.
- [x] A4: UDP proxy datagram path onto native buffers via `UdpFrameBuilder.TryBuildInto`.
- [x] Neuter `PacketLease.TryComplete` completion-callback materialization (design §3.3).
- [x] Allocation-gate tests (pattern: `GC.GetAllocatedBytesForCurrentThread()` deltas, precedent `UdpRelayTests.cs:420`): TCP mid-flow packet, TCP reverse packet, UDP datagram, RST build.

Validation: `dotnet test -c Release`; `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRelay*|*UdpSession*|*Dispatcher*' --job short` → 0 B allocated columns.

## M3 — Per-flow/per-connection zeroing (B1–B11)

- [x] B1/B2: SYN retention + templates on syn-copy pool leases; sweep/drain paths return (TcpPendingSynSetup, TcpRedirectSessionStore, TcpRedirectTable teardown).
- [x] B3: MAC stored inline in flow/session state; drop `ToArray()`.
- [x] B4: `BoundedSetupQueue` slots carry native leases.
- [x] B5: pooled setup executor (dedicated workers + MPMC ring + pooled slots) replacing per-flow `Task.Run`; enqueue sites: TcpProxyCoordinator.cs:191, UdpProxyCoordinator.cs:150.
- [x] B6/B7: static constant SOCKS5 messages; per-connection pooled scratch buffer in `Socks5ControlConnection`; remove `new byte[]` in Socks5State/Socks5ControlConnection.
- [x] B8: `TcpResetBuilder` stackalloc path + batch-send integration.
- [x] B9: startup endpoint resolve + cached `IPAddressValue`; failure-marked re-resolve on setup worker (cold path); `Dns.GetHostAddressesAsync` leaves the per-connection path.
- [x] B10: FlowTable pre-sized Dictionary + pooled FlowState (free-list); expiry returns states.
- [x] B11: `TcpProxyRelay` 64 KiB ×2 and UDP receive windows onto relay/window native pools; dispose paths return.
- [x] Allocation-gate tests: SYN flow setup entry (fast-path side), SOCKS5 message production, setup-slot rent/return balance.

Validation: `dotnet test -c Release`; `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*' --job short` — covered Perf benchmarks report 0 B / 0 GC.

## M4 — A1 pump threading (after data paths are alloc-free)

- [x] `NdisCapturePump`: dedicated thread + sync loop; `Thread.Sleep(1)`/yield empty pacing; slow-path handlers enqueue to pooled executor instead of inline await (design §3.1).
- [x] Fallback if a handler cannot de-async: batch enqueue via pooled slots (documented in code).
- [x] Tests: pump lifecycle (start/stop/dispose idles cleanly, no leaked thread), empty-poll zero-alloc gate on pump loop (N iterations ⇒ 0 B), backoff/error path regression tests intact.

Validation: `dotnet test -c Release`; `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*CapturePump*' --job short` → 0 B per packet, idle iterations 0 B.

## M5 — GC posture + soak gate

- [x] csproj/runtimeconfig: workstation GC (`ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`), `RetainVMGarbageCollection=false`, `System.GC.HeapHardLimit=134217728` (128 MiB) fuse via `RuntimeHostConfigurationOption` in `WinForward.Cli.csproj`; basis documented in-csproj (gc-soak harness ≈12–13 MiB post-full-GC, revisited on hardware before final).
- [x] Stability scenario `gc-soak`: mixed TCP-relay + UDP-forward loopback load (default 30 min via `GcSoakDefaultDurationSeconds=1800`, `--duration`/`--quick` overridable): asserts the application UDP forward lanes stay within a leak ceiling (absolute 64 KiB or `sends/512`, i.e. far below any per-datagram leak), pool `Outstanding`+`OverflowAllocations` return to baseline, and working-set slope/growth stay flat; `overflowAllocations == 0` under soak load.
- [x] Heartbeat warning event when collections occur after startup mark (`RuntimeHeartbeat.WarnGcCollected`, done in M0).

**AC3 methodology note (deviation, deliberate):** process-wide `gen0/1/2` counts are **reported**
(observability), not asserted unchanged. The mixed loopback harness and the product's parked
receive loops allocate BCL async state machines / lock infrastructure on park — explicitly
Out-of-Scope BCL infrastructure per the PRD. The asserted contract is therefore the
application-level guarantee: the steady-state UDP forward lanes allocate no meaningful managed
bytes on their calling threads over the window, pools balance, and the working set stays flat.
The real production daemon (Windows/AOT) is the only place process-wide zero-GC can ultimately be
observed; the heartbeat `gc.collected` alarm is the field signal for it.

Validation: `dotnet test -c Release`; `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --duration 1800`; re-run footprint scenario for comparison.

## Risky files / rollback points

- `src/WinForward.NdisApi/NdisCapture.cs` (thread model) — highest risk; M4 isolated on purpose; revert = restore async pump.
- `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs`, `UdpProxyCoordinator.cs` (ownership changes) — M2/M3 split limits blast radius.
- `DurableCaptureBundle.cs` (pool wiring) — additive; each pool additive until its consumers switch.
- GC config (M5) — config-only, revertible independently.

## Pre-start checklist

- [x] Spec review via trellis-before-dev (hot-path, windows-ndisapi, udp-relay, tcp-local-redirect, quality-guidelines).
- [x] Fresh `dotnet test -c Release` baseline recorded in task notes.
- [x] PRD/design approved (Phase 1.4 gate).
