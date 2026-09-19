# Design: GC-less zero-allocation hot paths

## 1. "GC fully off" — engineering definition

.NET (including Native AOT) has no supported "disable GC" switch. This design therefore realizes GC-off as:

1. **Steady-state zero managed allocation** on every application-controlled packet/flow/connection path (GC is allocation-driven; no allocation ⇒ no collection ever runs).
2. **Explicit configuration posture** so the intent is durable and regressions fail fast:
   - `WinForward.Cli.csproj`: `<ServerGarbageCollection>false</ServerGarbageCollection>`, `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` (workstation + blocking; irrelevant when idle, explicit for posture).
   - runtimeconfig fuse: `<System.GC.HeapHardLimitBytes>` (name finalized during implementation against .NET 10 naming) sized from the measured post-startup managed heap (measurement in M0; expected order 64–128 MiB). Overflow ⇒ forced GC then OOM (fail-fast) instead of silent collecting growth.
   - `<System.GC.RetainVM>false</System.GC.RetainVM>` so any startup heap shrink is returned to the OS.
3. **Observability**: `RuntimeHeartbeat` extends its payload with `GC.CollectionCount(0/1/2)`, `GC.GetTotalAllocatedBytes()`, aggregate native-pool occupancy; emits a warning-level event if any collection count is nonzero after the startup mark.

BCL infrastructure allocations (Socket/NetworkStream per connection, slow-path async state machines, exceptions) remain bounded and fenced by the fuse (user decision D1 boundary, PRD Out of Scope). Heartbeat exposes their drift; if soak shows they still trigger gen0, a follow-up native-socket task is the escalation path — not this task.

## 2. Buffer architecture: one native pool family

Generalize the proven `NdisPacketBufferPool` pattern (ConcurrentQueue + CAS idempotent return + Dispose drain) into a small family of size-class pools. All application buffers become native; `ArrayPool<byte>.Shared` usage is eliminated from steady-state paths.

```
NativeBufferPool (new, src/WinForward.Core or NdisApi — placement per directory-structure spec)
  ├── ctor(byte size, int capacity, PoolStats stats)   // NativeMemory.AllocZeroed / Free
  ├── Rent() -> NativeLease (struct: pool ref + void* + length; IDisposable; idempotent release)
  ├── ReturnAll/Dispose                                 // drain until empty (fixes L2 pattern)
  └── PoolStats { rented, returned, inPool, disposed }  // interlocked counters, test-assertable

Pool instances (wired in DurableCaptureBundle, disposed in existing finally order):
  ├── frame pool      — NdisPacketBufferPool (existing, keep; add L2 drain fix + stats)
  ├── relay pool      — 64 KiB x 2/direction per proxied TCP connection (replaces TcpProxyRelay ArrayPool, B11)
  ├── udp window pool — maxFrame + SOCKS5 header + oversize sentinel (replaces UdpProxySession/UdpSessionSetup ArrayPool windows, B11; single source of size truth already in DurableCaptureBundle.cs:103-106)
  ├── syn copy pool   — ≤ MaximumEthernetFrame slots for PendingSynSetup retained SYN frames (B1) and TcpRedirectAssociation templates (B2)
  └── setup slot pool — fixed-size setup work-item slots (B5, §4)
```

Rules:
- Every rent site is `try/finally` or `using`; ownership documented on the API (matches existing native-lease lifetime convention in hot-path.md).
- Exception paths must return buffers (L1 fix generalizes: no `Dispose`-after-rethrow patterns; audit rule goes to quality spec).
- Pools are sized to worst-case bounded load (capacity constants near existing ones); overflow behavior: frame/relay/window pools allocate-and-track (never fail mid-packet), with stats counter `overflowAllocations` surfaced in heartbeat so sizing errors are visible, not silent.

## 3. Per-packet path changes (R1)

### 3.1 A1 — capture pump pacing without allocation
`NdisCapturePump.RunAsync` (`NdisCapture.cs:116`) moves from "async loop + `Task.Delay` when idle" to a **dedicated long-running thread with a synchronous loop**:

- One dedicated thread per adapter pump (`new Thread(...) { IsBackground = true }` created once at pump start — a startup allocation).
- Loop body is synchronous: batch IOCTL read (already sync-shaped, `NdisApiDriver.ReadPacketsBatch`), then invoke the processing chain synchronously.
- Empty-batch pacing: `Thread.Sleep(1)` after `Thread.Yield()`-style backoff (zero allocation; preserves 1 ms poll cadence and the existing HighResolutionTimerScope).
- Downstream: the chain is already sync-completing on the fast path (`FlowDispatcher` sync entry). Where handlers are `async Task` (slow path: new-flow setup, proxy dial), the pump **enqueues a pooled work item** (§4) and continues; it never awaits them inline.
- Risk control: pump thread must not block on locks held by async continuations → setup queue is MPMC ring, no locks on the pump side. Rollback point: if a downstream handler cannot be de-asynced safely, keep the thread model but enqueue the whole packet batch to the existing async path via pooled slots (still zero-alloc; slightly higher latency).

### 3.2 A2 — SYN bit-test without materialization
`TcpProxyCoordinator.cs:421` `IsTcpSyn(packet.Lease.Frame.Span)` → `IsTcpSyn(packet.InspectionSpan)`. Pure span read, no copy. (The `InspectionSpan` accessor exists precisely for this; see FlowDispatcher.cs:50.)

### 3.3 A3/A4 — writable copies from native pools, not ArrayPool
- `PacketLease.Materialize` (managed ArrayPool copy) is retired from steady-state paths. `PacketLease` keeps `InspectionSpan` as the read-only view.
- Mid-flow reinjection (`TcpProxyCoordinator.cs:262`), reverse path (`:314`), and UDP proxy (`NdisPacketActionExecutor.cs:420-434`): rent from the frame pool (or reuse the already-rented lane buffers), run the existing in-place rewriters (`TcpFrameRewriter`, `UdpFrameBuilder.TryBuildInto` already write into native buffers), send, return in `finally`.
- `PacketLease.TryComplete`'s `_onCompleted?.Invoke(Frame)` materialization trap (audit L3) is removed/neutered: native leases carry no completion callback.

## 4. Per-flow / per-connection changes (R2)

- **B1/B2 SYN retention**: `PendingSynSetup` and `TcpRedirectAssociation.OriginalSynFrameCopy` hold a native lease (syn copy pool) instead of `byte[]`. Lifetime: setup completion / teardown / sweep → return (sweep paths already exist: TcpPendingSynSetup.cs:271 drain, TcpRedirectSessionStore.cs:138,199).
- **B3 MAC**: store 6 MAC bytes inline in the flow/session struct (`ulong` field or `fixed byte[6]` in a pool-owned state object); `clientMac.ToArray()` disappears.
- **B4 setup datagram queue**: `BoundedSetupQueue` slots hold native leases instead of `byte[]` copies; budget charge/credit pairing (audit L4) unchanged.
- **B5 setup scheduling**: replace per-flow `Task.Run` + closure with a **pooled setup executor**: N dedicated long-running setup worker threads (created at startup, count from config, default = 2× CPU) draining an MPMC ring of preallocated work-item slots. Slot carries a discriminated-union-style `enum SetupKind { TcpSyn, UdpNew }` + payload ref; slots rent/return from the setup slot pool. Workers may allocate freely only on failure paths (cold). Ring bounded by existing setup-queue budgets; overflow applies existing drop-oldest/deny policy (no growth, no leak).
- **B6 SOCKS5 constant messages**: `Socks5Messages` becomes static `ReadOnlyMemory<byte>` built once per process (constant greeting/auth frames). Variable request frames are written into a per-connection pooled buffer owned by `Socks5ControlConnection`.
- **B7 handshake replies**: same per-connection pooled scratch buffer (size = max handshake reply, small constant); `new byte[2]/[5]/[totalLength]` removed.
- **B8 RST frames**: `TcpResetBuilder` writes into a stackalloc span (`stackalloc byte[74]`, well under limits) or a rented frame for oversized options; consumed synchronously by the batch send path.
- **B9 DNS (user decision D2)**: `DurableCaptureBundle` startup resolves the configured SOCKS5 endpoint once → cached `IPAddressValue`. `Socks5ControlConnection.ConnectAsync` uses the cached value with zero DNS involvement. On connect failure, connection marks the cache dirty (interlocked flag); the next setup attempt re-resolves on the worker thread (cold path, allocation allowed) and refreshes the cache.
- **B10 FlowTable**: pre-size the Dictionary at startup to the configured max-flow bound (no rehash growth at runtime). `FlowState` objects come from a fixed-capacity object pool (array-based free list, Interlocked pop/push); flow expiry returns states (sweep paths exist). New-flow lookup path: Dictionary read → pool rent → init in place (no allocation).
- **B11 relay/UDP windows**: `TcpProxyRelay` (64 KiB × 2 per connection) and UDP receive windows rent from the relay/udp-window pools; return in existing `finally`/dispose paths.

## 5. Leak fixes (R4)

- **L1**: `NdisPacketActionExecutor.AppendPass` overflow-send path: wrap send + buffer return in `try/finally` (mirror `FlushLane` :212-215).
- **L2**: `NdisPacketBufferPool.Dispose`/`NativeBufferPool.Dispose`: after CAS'd dispose, drain the queue in a loop until empty (returns race with dispose safely).
- **Pattern rule**: every new native rent site adds a balance test (rent N → return N → dispose → assert `inPool == N`, `rented == returned`). This goes into quality-guidelines via spec update in Phase 3.

## 6. Data flow after change (per-packet fast path)

```
[dedicated pump thread] NdisCapturePump sync loop
  → NdisApiDriver.ReadPacketsBatch (native IntermediateBuffer[], stackalloc/native request blocks)
  → CapturePacketProcessor.ProcessAsync (sync-completing)
  → IPTcpUdpPacket.TryParse / PacketFlowClassifier (span, zero-alloc)
  → FlowDispatcher.Dispatch (sync fast path)
      ├─ Pass/Block → lane accumulate → batch IOCTL (existing)
      ├─ TCP proxy  → InspectionSpan checks (A2) → in-place rewrite on rented native frame (A3) → inject → return
      │              → new SYN: enqueue pooled setup slot (B5) → worker: SOCKS5 (B6/B7, cached endpoint B9) → relay pump (B11 native buffers)
      └─ UDP proxy  → UdpFrameBuilder.TryBuildInto native buffer (A4) → SOCKS5 UDP transport send (already zero-alloc warm path)
                     → new flow: pooled setup slot (B5) → session with native receive window (B11)
```

## 7. Compatibility & migration

- Public API surface: internal changes only; CLI behavior, config schema, exit codes unchanged. DNS caching is the only observable behavior change (PRD D2).
- Tests: existing fakes (TrackingArrayPool, fakes for coordinators) keep compiling; TrackingArrayPool usage shrinks as ArrayPool leaves the paths. New fakes only if a pool seam needs injection (pools injected via DurableCaptureBundle like existing receiveBufferPool seam, UdpProxyCoordinator.cs:51 precedent).
- Benchmarks: CapturePumpBenchmarks / TcpRelayBenchmarks / UdpSessionBenchmarks / DispatcherBenchmarks must show 0 allocated bytes after the change (their MemoryDiagnoser columns are the regression gate, per hot-path.md §9).

## 8. Risks & trade-offs

| Risk | Mitigation |
|---|---|
| Pump thread model change (A1) breaks async assumptions downstream | Chain is sync-completing on fast path today; slow path enqueues, never awaits inline. Fallback: pooled-slot enqueue of whole batch keeps async path intact. Rollback = revert pump class only. |
| Dedicated threads added (pump per adapter + N setup workers) | Bounded count, created at startup; net threads may decrease (Task.Run pool churn removed). |
| HeapHardLimit too small → startup OOM | Fuse sized from M0 measurement (heartbeat heap stats), documented; only set in M5 after soak validation. |
| Native pool sizing wrong → overflowAllocations churn | Stats surfaced in heartbeat + soak gate on `overflowAllocations == 0` under soak load; capacity constants adjustable without design change. |
| BCL per-connection allocations still trigger gen0 eventually | Bounded + fenced by fuse; heartbeat visibility; escalation (native sockets) is explicitly a future task, per PRD Out of Scope. |
| Behavior change: DNS staleness for DDNS | User-accepted (D2): re-resolve on connect failure bounds staleness. |

## 9. Rollback shape

Milestones are independently revertable (see implement.md). The only cross-cutting artifacts (NativeBufferPool family, GC config) are additive; reverting M5 (GC fuse) alone returns to today's posture without code entanglement.
