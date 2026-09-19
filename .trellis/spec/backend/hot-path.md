# Hot-Path Conventions (Zero-Allocation Packet Pipeline)

> Established 2026-08-28 by task 08-28-perf-hotspots. Verified by the WinForward.Benchmarks
> harness (allocation/GC counters exact; ns/pps on the dev box carry ±50% noise).

## Scope / Trigger

Any code on the per-packet path: capture processing, parsing, classification, dispatch,
pass/block execution, flow-table probes, SOCKS5 UDP encode. Cold edges (config, process
attribution, socket setup, logging, tests) are exempt.

## Contracts

1. **Raw addresses only on the hot path.** `IPAddress` (class) never appears in
   parse/classify/flow-key code; use `IPAddressValue` (UInt128 bits, IPv4 in the low 32 bits,
   family + scope). Convert via `ToIPAddress()` / `From(IPAddress)` on cold edges only.
   `Endpoint` stores `IPAddressValue` by value. Since 2026-08-30 (task
   08-30-udp-alloc-jumbo) this extends to the SOCKS5 UDP datagram product type —
   `Socks5UdpDatagram.DestinationAddress` is `IPAddressValue?` and
   `IUdpProxyTransport.SendSpanAsync` takes an `Endpoint` — no framework addresses
   remain on any UDP datagram path.
2. **IPv4 masks stay inside the low 32 bits.** `IPPrefix.PrefixMask(prefixLength, family)`:
   IPv4 = `0xFFFFFFFF << (32 - len)` (/0→0, /32→0xFFFFFFFF); IPv6 = left-aligned 128-bit.
   A left-aligned mask over low-32 IPv4 bits matches everything — regression-locked by
   `IPv4PrefixesMatchOnlyTheirPrefix`.
3. **No async state machines on the steady-state path.** A fat async method (large struct
   locals hoisted into the state machine) heap-allocates per call even when it completes
   synchronously (~193 B/op measured). `FlowDispatcher.DispatchAsync` is a non-async entry
   that runs the synchronous warm shape (trace-off ∧ reverse-diversion declined ∧ not
   self-owned ∧ resolved ∧ (Pass ∨ Block ∨ Proxy-with-inline-server-hit)) and returns the
   executor's ValueTask directly; everything else falls into `DispatchSlowAsync`. The
   reverse diversion is decided by `ITcpReverseHandler.WantsPacket(in CapturedFlowPacket)`
   (task 08-30-hot-path-revival, X1): TCP ∧ src port ∈ active listener-port set
   (`TcpRedirectTable` `int[65536]` reference counts, Inc in `TryClaim` under the gate
   before SYN injection, Dec in `TryRemove`/`RemoveExpired`; query `Volatile.Read != 0`).
   `WantsPacket` is ONLY the warm-entry diversion precheck — the slow path's
   `TryHandleReverseAsync` always calls the full handler (protocol gate + full-tuple
   `IsReverseCandidate` + tombstone), so prefilter misses degrade to the slow path, never
   to wrong routing (listener-shaped tuples cannot resolve in any `FlowTable.TryResolve`
   mode; pinned by the tombstone-straggler test). Production-composition benchmarks
   (`WarmPassProductionAsync`/`WarmProxyProductionAsync`, real-predicate fake handler) gate
   the warm shape at 160 B — handler-less benchmarks alone proved nothing while X1 made
   the warm entry dead code in production. Proxy is the product's
   main path (task 08-29-socks5-perf-fullpath C2b), so a resolved proxy decision whose
   `ProxyServerName` hits `_servers` stays on the warm entry — measured 352 B → 160 B and
   596 ns → 258 ns per packet; an unresolved server name (fail-closed via slow path) and the
   UDP reverse-of-stored special case keep their slow-path behavior. `FlowAction` has exactly
   the values {Proxy, Pass, Block}, so no defensive "unknown action" gate is needed after the
   proxy branch (a removed always-false gate taught this lesson). Any new per-packet stage
   must follow the same split.
   **Socket sends follow it too** (task 08-29-udp-throughput-loss D2): the sending socket is
   non-blocking (`Blocking = false`; .NET 10 renamed `NonBlocking`), and the warm shape is an
   inline sync `SendTo(span)` — on Windows this removes a per-datagram IOCP→thread-pool hop
   (the mechanism behind a 3.5× loopback pps gap), on Linux the sync send was already the
   fast path. Any `SocketException` falls back to the overlapped async send, which parks on a
   full kernel queue instead of busy-failing; every gate/buffer-release path must release
   exactly once per call.
4. **Packets are structs.** `FlowContext`, `CapturedFlowPacket`, `NativeFrameHandle` are
   `readonly record struct` with `[StructLayout(LayoutKind.Auto)]`; completion is
   enum-driven (`PacketAction`), never closures/delegates.
5. **Native-frame lease lifetime.** `PacketLease.TakeNative(IFrameSource)` recycles
   per-thread. The native frame stays valid for the whole dispatch (the pump awaits each
   handler, so batch slots cannot be reused earlier — locked by
   `PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight`). Consumers that keep frame bytes past
   the synchronous section must read `Lease.Frame` (lazy ArrayPool materialization;
   `Release()` in the processor's finally returns it). Parse/classify run directly on the
   native span.
6. **In-place pass reinjection.** An unmodified pass frame reinjects its own capture buffer:
   `PrepareForReinjection(enumerationHandle)` retargets only the adapter handle (+ zeroes
   UnionPadding); DeviceFlags/Flags/Length/payload stay as captured. Materialized or
   buffer-less packets take the pooled-copy fallback. Enumeration handle ≠ captured
   `m_hAdapter` (see windows-ndisapi.md).
7. **Span-writing codecs.** Datagram encode uses `TryEncode(..., Span<byte>, out written)`
   into a reusable buffer (`Socks5UdpTransport._sendBuffer`); allocating overloads exist for
   tests only.
8. **Flow keys hash flat.** `FlowKey`/`TransportTuple` use manual `HashCode.Combine` over
   both endpoints' bits/ports/family/proto; `Equals` orders cheapest discriminators first
   and compares every field (including `OriginAdapterId` and `ScopeId`).
9. **Measurement discipline.** Benchmark gates are stated in allocation bytes and GC counts;
   treat ns/pps deltas under ~2× as noise on the dev box.

## SOCKS5 Path Contracts (task 08-29-socks5-perf-fullpath)

### 1. Scope / Trigger

Trigger: any change to the TCP redirect forward-leg rewrite, the dispatcher proxy branch,
UDP session setup, or the SOCKS5 benchmark/soak suite.

### 2. Signatures

- `TcpRedirectAssociation.ForwardLocalAddress` is `IPAddressValue?` (`TcpRedirectTable.cs`);
  the only `From(IPAddress)` conversion runs at `TcpRedirectSetup.ResolveForwardLocalAddress`
  (association creation, once per flow). `TcpFrameRewriter.TryRewriteForwardLeg` consumes the
  stored value with zero conversions per packet.
- `TcpFrameRewriter.SwapEthernetMacs` swaps via byte-index pairs (`(frame[i], frame[i+6])`),
  constructively allocation-free — do not reintroduce a heap temp (`new byte[6]` measured 0 B
  only under JIT escape analysis; the indexed swap is a source-level guarantee).
- `BoundedSetupQueue.TryEnqueue/TryDequeue` (`WinForward.Core/BoundedSetupQueue.cs`) keeps a
  single-slot fast path (≤1 buffered datagram skips the `Queue<>` object + array).
- `TcpThroughputScenario` soak modes: `--tcp-relay-mode socks5|bare`.

### 3. Contracts

- **Forwarded-shape rewrite cost is O(frame), not O(conversions)**: before C2 the per-packet
  `IPAddressValue.From(IPAddress)` made forwarded DNAT 4.8× slower than host shape
  (2,887.9 ns vs 616.7 ns @1400 B); storing the raw value at creation closed the gap
  (626 ns ≈ host shape). Any per-packet stage that needs an address from a cold-edge object
  must cache `IPAddressValue` at the cold edge.
- **UDP session cold-path bookkeeping budget: ≤1 KB per session** (slot, setup queue,
  task machinery, MAC copy, tombstone/tracking structures, logging). Measured 2.1 KB →
  ~0.4 KB via: single-slot setup queue fast path, no `registered` TCS (slot add under the
  coordinator gate already orders before any handler can run), inlined setup-failure handling
  (tombstone written iff the exception is not an OCE), method-group delegates cached in the
  constructor, dictionary pre-sizing clamped to `min(capacity, 1024)`. Framework socket cost
  (~84 KB/session: control TCP connect + UDP ASSOCIATE + sockets) is outside this budget and
  out of scope.
- **Throughput acceptance anchor**: `tcp.throughput` socks5 mode must stay ≥70% of bare mode
  (same workers/echo/transfer-size, relay leg without SOCKS5 establishment); measured 93.9%
  (141.4 vs 150.6 MB/s, 16 conc × 1 MiB quick). `TcpRelay OneWayAsync` (~0.94 GB/s) is the
  single-connection theoretical ceiling, not the ratio denominator.
- **Benchmark gate**: `FrameRewriterBenchmarks` (all methods 0 B), `WarmProxyDisabledTraceAsync`
  = 160 B (equal to `WarmPassDisabledTraceAsync`), `UdpSessionBenchmarks` Noop probe tracks
  the bookkeeping budget, linear time scaling at 10× sessions.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Proxy decision, server name resolves inline, not UDP reverse | warm sync entry, 160 B, executor `ProxyAsync` ValueTask returned directly |
| Proxy decision, server name unresolved | `DispatchSlowAsync` (fail-closed block semantics unchanged) |
| UDP datagram reverse-of-stored proxied flow key | `DispatchSlowAsync` (pass-to-client semantics unchanged) |
| Association created without local address candidate | `ForwardLocalAddress = null` → forwarded fail-closed `Blocked` (unchanged) |
| Setup queue overflow during session setup | drop-oldest, freshest-wins (unchanged; single-slot path preserves bounds) |
| Setup task throws non-OCE | tombstone written, slot removed (inlined handler ⟺ old `IsFaulted` semantics) |

### 5. Good/Base/Bad Cases

- Good: a forwarded mid-flow data packet rewrites in ~0.6 µs @1400 B with 0 B allocated.
- Base: a proxy decision whose server was removed from config falls into the slow path and
  blocks fail-closed — behavior identical to pre-C2b.
- Bad: adding `IPAddressValue.From(IPAddress)` inside a per-packet rewrite; adding a
  defensive "unknown FlowAction" gate (the enum is closed); re-adding a heap temp in
  `SwapEthernetMacs`.

### 6. Tests Required

- Existing 386-test baseline (rewrite byte-for-byte round-trip, coordinator admission,
  cooldown, capacity, single-flight dispose) must hold behavior-zero.
- Benchmark re-runs: FrameRewriter 0 B, Dispatcher WarmProxy = WarmPass allocation, UdpSession
  Noop probe ≤ ~4 KB @100 sessions, soak ratio ≥70%.

### 7. Wrong vs Correct

```csharp
// Wrong: per-packet conversion from a cold-edge class address (4.8× rewrite cost).
var raw = IPAddressValue.From(association.ForwardLocalAddress);

// Correct: the association stores the raw value once at creation; the rewrite uses it.
var raw = association.ForwardLocalAddress; // IPAddressValue?
```

## Relay pump and checksum contracts (task 08-29-proxy-stability-perf, 2026-08-29)

### 1. Scope / Trigger

Trigger: any change to `TcpProxyRelay`'s pump loop, `PacketChecksums`, or the checksum/relay benchmark suite.

### 2. Signatures

- `TcpProxyRelay` owns one reusable `StallWindow` (linked CTS) **per direction per session**; `Arm()` runs before every `ReadAsync`/`WriteAsync`, throttled to at most once per second (task 08-30-fast-hardening X8a): the first arm is unconditional, subsequent arms only when >1 s (`Stopwatch` ticks, internal static `StallWindow.IsRearmDue(lastArmTicks, nowTicks)`, strictly greater) has elapsed since the last arm.
- Each pump direction rents one 64 KiB buffer from `ArrayPool<byte>.Shared` (`PumpBufferSize = 64 * 1024`, task 08-30-fast-hardening X5) for its whole lifetime — one rent per direction (half-close gives independent lifetimes), returned exactly once in `finally` on every exit path; loop bounds use `buffer.Length` (the pool may return a larger array).
- `PacketChecksums.TryRewriteIpv4Tcp/TryRewriteIpv6Tcp` use RFC 1624 incremental update; the internal full-recompute oracle is kept for property tests (`InternalsVisibleTo("WinForward.Core.Tests")`).
- `PacketChecksums.Sum` is `Vector256`-vectorized when hardware-accelerated, scalar fold-while-adding otherwise.

### 3. Contracts

- **Stall-window CTS reuse (P1)**: `Arm()` = `TryReset()` + `CancelAfter(StallTimeout)`; only when `TryReset` returns false (raced with the timer firing) is the CTS recreated — re-linked to the lifetime token, so lifetime/peer cancellation still lands instantly. `TryReset` clears any pending `CancelAfter` timer (verified on .NET 10), so each window arms from its own `Arm()` with no residual-timer leakage. Measured per-op pump allocation 160 B → 0 B; `TcpRelayBenchmarks.OneWayAsync` chunk-8192 allocation −77% (828→188 KB/op) with no throughput regression. Do not reintroduce per-chunk `CreateLinkedTokenSource` + `CancelAfter`.
- **Re-arm throttle (X8a, 2026-08-30)**: the throttle only skips `Arm()` re-invocations within 1 s — it never disarms, never recreates the CTS, and never breaks the lifetime-token link; the previously armed timer simply stays armed, so the 30-min stall window drifts by at most 1 s. Motivation: unconditional `Arm()` cost ~100–200 ns per timer op, ≈5–10% of a core at 10 Gbps single-flow. Kept the P1 contract intact (when `Arm()` does run, it is still `TryReset` + `CancelAfter`, never per-chunk CTS creation). Both relay legs set `NoDelay = true` (listener accept + upstream `ConnectAsync` success) — Nagle has no upside for a byte-pipe relay and produces 40–200 ms delayed-ACK cliffs on interactive traffic.
- **Incremental endpoint rewrite (P2a)**: `HC' = ~(~HC + Σ(~m + m'))` folded over only the changed words (IPv4: address words counted in header + pseudo-header, ports; IPv6: 16 address words + ports, no header checksum). New words are read back **from the frame after the write** (single encoding source). **Precondition: the input checksum is valid** — that is the capture-pipeline contract; on invalid-checksum input the result differs from a full recompute (garbage-in-garbage-out, documented in-code). Bit-identical to full recompute on valid inputs: property-pinned (512+512 random frames, 65,536-port sweep incl. computed-zero, three-way vs independent test helpers). EndpointsDirect micro: 626→33 ns @1400 B (0 B).
- **Vectorized `Sum` (P2b) — fold invariants are load-bearing**: `uint` wraparound is NOT one's-complement neutral (`2^32 ≡ 1 mod 65535`), so any deferred-fold scheme must keep partial sums strictly bounded. The landed version folds to ≤0xFFFF after every accumulation step (vector lanes folded fully per block; bound exactly 0xFFFF_FFFF, never overflows). The scalar fallback must stay fold-while-adding (bare accumulation overflows ≥131,076 B of 0xFF — the `ProtocolAuditTests` shape — and broke host-independence on non-AVX hosts). Packet paths never reach the risky sizes (IP ≤64 KiB), but the function is size-public; keep the invariants for all inputs. Measured 989→61.7 ns @1514 B (16×).
- **Benchmark frames must carry valid checksums**: incremental rewrite's precondition means `BenchmarkShared` frames with zeroed checksums measure an invalid-input shape — benchmark frames are built with correct checksums before/after so the delta is real.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Pump op completes within window | CTS reused via `TryReset`, 0 B allocated per op |
| Arm invoked within 1 s of the previous arm | skipped (`IsRearmDue` false); previously armed timer stays armed |
| Arm invoked >1 s after the previous arm / first arm | `Arm()` runs (`IsRearmDue` true, first arm unconditional) |
| Stall timer fires between ops / `TryReset` returns false | CTS recreated linked to lifetime token; semantics unchanged |
| Pump exits on any path (normal, fault, dispose, stall) | pooled 64 KiB direction buffer returned exactly once |
| Rewrite with valid input checksum | incremental ≡ full recompute, byte-identical frame |
| Rewrite with invalid input checksum | garbage-in-garbage-out vs full recompute (documented precondition) |
| `Sum` on any size/fill incl. 131,076 B 0xFF, ≥393,216 B adversarial | bit-identical scalar vs vector, no overflow |

### 5. Tests Required

- Relay: existing half-close/sibling-cancel/fast-fail suite unchanged + multi-rearm mid-stream failure regression (`MidStreamFailureCancelsSiblingPumpAfterRepeatedStallWindowRearms`).
- Checksums: `TcpEndpointRewriteIncrementalTests` (random-frame equivalence, port sweep, three-way vs independent helpers); `ProtocolAuditTests` 131,076 B audit shape stays green on all hosts; baseline 431 tests behavior-zero.

### 6. Wrong vs Correct

```csharp
// Wrong: bare uint accumulation "folded at the end" — overflows on ≥131k B of 0xFF.
uint acc = 0;
foreach (var w in words) acc += w; // 65,538 × 65,535 > 2^32 — result ≠ one's-complement sum

// Correct: fold-while-adding keeps the partial sum ≤ 0xFFFF forever.
uint acc = 0;
foreach (var w in words) { acc += w; while (acc > 0xFFFF) acc = (acc & 0xFFFF) + (acc >> 16); }
```

## Closure-hoisting and allocation-gate contracts (task 09-18-gc-less-zero-alloc, 2026-09-18)

### 1. Scope / Trigger

Trigger: any change to a warm zero-alloc entry that also contains a cold `Task.Run`/lambda
branch, and any allocation-gate test that protects a span-vs-memory overload choice.

### 2. Signatures

- `UdpProxyCoordinator.TrySendSpanAsync` is the **only** (and **non-async**) warm send entry
  (task 09-19-compat-api-cleanup removed the memory `TrySendAsync`); the cold new-flow work is
  delegated to `ScheduleSessionSetup(FlowKey, Socks5Server, long, byte[]?, UdpSessionSlot)`
  (`UdpProxyCoordinator.Send.cs`), which is the only place a `Task.Run(() => ...)` lambda lives.
- `UdpProxyCoordinator` is a `partial class` split into `UdpProxyCoordinator.cs` (admission /
  lifecycle) and `UdpProxyCoordinator.Send.cs` (the span send bridge), keeping each file
  ≤400 effective lines.

### 3. Contracts

- **Closure hoisting is per-call, not per-branch.** Roslyn hoists a captured lambda's closure
  display class to **method entry** (`IL_0000: newobj '<>c__DisplayClass…'`), so an entry that
  *contains* a capturing lambda pays its allocation on **every** call even when the warm path
  returns before the lambda's branch. Measured 184 B/op deterministically on a coordinator whose
  warm path never entered the new-flow branch. Fix: extract the capturing lambda into a separate
  cold helper method; both warm entries then contain no lambda and no display class is hoisted.
  This is a source-level guarantee — do not rely on escape analysis.
- **Allocation gates must discriminate the exact seam.** History: the interface once carried both
  a memory `SendAsync` and a span `SendSpanAsync`; a fake that incremented one shared `_sends`
  counter for both could not detect a regression that reverted the caller to the memory overload
  (self-fulfilling gate), so the gate counted them separately (`MemorySends`/`SpanSends`). Task
  09-19-compat-api-cleanup deleted the memory overload and made `IUdpProxyTransport.SendSpanAsync`
  the only send seam, so the gate now measures the span path directly: it asserts
  `GC.GetAllocatedBytesForCurrentThread()` delta `== 0` across real dispatches and that the fake's
  `SpanSends` advanced by exactly the expected count. A future re-materialization on the send path
  (a new memory overload, `ToArray()`, or `new byte[]`) must make that 0-B assertion fail.
- **Approved cold-path materialization.** `Socks5UdpTransport.SendSpanAsync` copies with
  `payload.ToArray()` only on the contended-gate branch: the span views native capture memory
  that recycles once dispatch returns, so it cannot cross the gate `await`. This is a documented
  cold-path exemption; the warm uncontended shape stays zero-alloc.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Warm entry contains a capturing lambda anywhere in its body | 0 B gate fails by design; extract the lambda to a cold helper |
| Warm entry returns before the cold branch | still 0 B — no display class is hoisted |
| A materializing overload/copy is re-added on the send seam | 0 B gate fails (`SpanSends` count and/or allocation delta) |
| Span reaches `Socks5UdpTransport` with the send gate uncontended | zero-alloc sync `SendTo` |
| Span reaches `Socks5UdpTransport` with the gate contended | `payload.ToArray()` cold copy (documented exemption) |

### 5. Tests Required

- `HotPathAllocationGateTests`: `MidFlowRewriteAndInjectAllocatesNoManagedBytes`,
  `ReverseRewriteAndInjectAllocatesNoManagedBytes`, `EstablishedUdpDatagramPathAllocatesNoManagedBytes`
  — the UDP gate asserts 0 allocated bytes and that the fake's `SpanSends` advances by exactly the expected count after the measurement (span is the only send seam).
- `NativeBufferPoolTests` / `NdisPacketBufferPoolTests`: balance identity + dispose-drain races
  (`ReturnsRacingDisposeNeverStrandBuffers`).
- Baseline must stay behavior-zero (678 tests green on this task).

### 6. Wrong vs Correct

```csharp
// Wrong: the capturing lambda lives inside the warm entry — Roslyn hoists its display
// class to method entry, so EVERY call pays ~184 B even when this branch is not taken.
public ValueTask<bool> TrySendSpanAsync(...)
{
    lock (_gate) { if (warm) return ValueTask.FromResult(true); }
    var completion = Task.Run(() => _setup.CreateSessionAsync(flow, server, gen, mac, token, slot));
    ...
}

// Correct: the lambda lives only in a cold helper; the warm entry contains no lambda,
// so no display class is hoisted and the warm path measures 0 B.
private void ScheduleSessionSetup(FlowKey flow, Socks5Server server, long flowGeneration, byte[]? capturedClientMac, UdpSessionSlot slot)
    => slot.Completion = Task.Run(() => _setup.CreateSessionAsync(flow, server, flowGeneration, capturedClientMac, _shutdown.Token, slot));
```

## Native pool family, pooled flow/setup state, and GC-off posture (task 09-18, 2026-09-18)

### 1. Scope / Trigger

Trigger: any new native rent site, any change to `NativeBufferPool`/`NativeLease`, `FlowTable`
pooling, `SetupExecutor`, the native relay/UDP receive windows, the allocation gates, or the CLI
GC configuration. This is the contract for the full-path zeroing milestone (M1-M5).

### 2. Signatures

- `NativeBufferPool(int byteSize, int capacity = 256, Action<bool>? accountingSink = null)`
  (`src/WinForward.Core/NativeBufferPool.cs`): `Rent() -> NativeLease`, `Dispose()`, and
  `Stats` with interlocked `Rented` / `Returned` / `InPool` / `DisposedCount` /
  `OverflowAllocations` / `Outstanding`. Overflow allocates-and-tracks instead of failing.
- `NativeLease` (struct): `Span<byte>`, `Memory<byte>` (backed by `NativeMemoryManager :
  MemoryManager<byte>`, one manager per fresh native allocation, never per rent), and an
  idempotent `Dispose()`. `MemoryManager.Pin` returns the raw pointer, `Unpin` is a no-op
  (native memory never moves), so `NetworkStream`/`Socket` async IO can consume `lease.Memory`.
- `SetupExecutor` (`src/WinForward.Runtime/SetupExecutor.cs`): MPMC `ConcurrentQueue` ring +
  `SemaphoreSlim` signal + dedicated `Thread` workers (`DefaultWorkerCount = max(2 × CPU, 16)`,
  `DefaultRingCapacity = 1024`); `StartPendingSetup`/`LaunchSetup` replace per-flow `Task.Run`.
- `FlowTable(capacity)`: `_states`/`_transportIndex` pre-sized dictionaries plus a
  `FlowState[]` free list; `FlowState.Reset(FlowKey, FlowDecision, long)` re-initializes in
  place; `RemoveExpired` returns states.
- `UdpProxyCoordinator.ReceiveWindowSize(int maximumFrameSize) = frame + 22 + 1` — the single
  source of truth shared by composition and the receiver.
- `Socks5UdpTransport` ctor caches `_relaySocketAddress = relayEndpoint.Serialize()`.
- `RuntimeHeartbeat`: `RuntimeGcSnapshot(Gen0Collections, Gen1Collections, Gen2Collections,
  AllocatedBytes)`, a startup mark, an injectable snapshot provider, and the
  `WarnGcCollected` warn-on-new-collection event.

### 3. Contracts

- **Native lease ownership.** Every rent is `using`/`try-finally`; release exactly once per
  rental. Release is idempotent across copies of a lease (in-band `int` state cell,
  `StateRented → StateIdle`), and a return racing `Dispose` is freed by exactly one drainer
  (no stranding, no double free). A release from a *stale* copy after the buffer was re-rented
  is a **rental-contract violation** (no in-tree violator); never double-release or retain a
  lease past its owning scope.
- **`lease.Memory` is valid only until release.** It is the documented bridge for async IO over
  native memory; do not hold it across a release. The pool's own size-class pools are the relay
  pump (64 KiB), the UDP receive window (`ReceiveWindowSize`), the SYN copy, and the setup slot.
- **FlowTable pooling.** `RemoveExpired` returns states under the gate; every successful
  `TryResolve` calls `Touch` before returning, so a live state cannot be idle-expired. Reuse
  means a `FlowState` reference held *past* expiry could observe the next flow's fields —
  callers must read state within the gate-held / `Touch`-refreshed operation (no production path
  retains a `FlowState` across an await).
- **SetupExecutor.** Per-item exception containment (`TrySetException`), balanced
  pending/enqueued/completed/rejected counters, lazy worker start, and `Dispose` joins workers,
  drains queued items (canceling their completions), and refuses new work. A worker that
  dequeues after `_disposed` drains that item instead of executing it.
- **GC posture (CLI).** `WinForward.Cli.csproj` sets `ServerGarbageCollection=false`,
  `ConcurrentGarbageCollection=false`, `RetainVMGarbageCollection=false`, and the fuse
  `System.GC.HeapHardLimit` (bytes) via `<RuntimeHostConfigurationOption>` — there is **no**
  MSBuild GC property for this key, and the explicit item is what guarantees the
  runtimeconfig.json entry in both JIT and AOT publishes. The fuse value is documented in-csproj
  with its measurement basis.
- **`gc-soak` asserted contract.** Steady state = established flows only (no new setup inside the
  measured window, since per-connection BCL allocations are Out-of-Scope). The scenario asserts
  the application-level guarantee — UDP forward lanes stay within the leak allowance
  (`max(64 KiB, sends/512)`, far below any per-datagram leak), pools perform **no fresh overflow
  allocation** during the window (summed `OverflowAllocations` unchanged), the working-set
  slope/growth stay flat — and, **after teardown**, every pool drains to `Outstanding == 0` with
  the conservation identity `OverflowAllocations == DisposedCount + InPool + Outstanding` — and
  *reports* process-wide `gen0/1/2` and allocated bytes. Cumulative `Outstanding` is deliberately
  **not** asserted inside the window: established relays legitimately finish and return their
  leases mid-soak (a return is not a leak). Process-wide "gen counts unchanged" is not assertable
  on the loopback harness; the heartbeat `gc.collected` alarm is the field signal.
- **Allocation gates must exercise the real production collaborator.** A gate built on a fake
  that implements the same interface cannot observe an allocation trap inside the real
  collaborator. `Socks5UdpTransport` handed a fresh `EndPoint` to `Socket.SendTo`/`SendToAsync`,
  which serializes it to a `SocketAddress` — **72 B per relay datagram** on the product's main
  path — while every fake-transport gate stayed green. When a path's real collaborator can
  allocate, add at least one gate against the real one (loopback) or a dedicated regression test.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Rent N → return N → dispose | `Stats.InPool == N`, `Rented == Returned`, `Outstanding == 0` |
| Return racing `Dispose` | freed by the drain or the return's post-enqueue recheck, exactly once |
| Second release of a lease copy | no-op (in-band state already idle) |
| Release of a stale copy after re-rent | contract violation — would enqueue a buffer another renter holds; do not write such code |
| Pool at capacity, all in use | overflow allocates and increments `OverflowAllocations`; never fails mid-packet |
| `NativeLease.Memory` used after release | invalid — memory may be re-rented (documented "valid until release") |
| Warm entry hands the real relay socket an `EndPoint` | 72 B/datagram (forbidden); use the cached `SocketAddress` |
| Established relay finishes mid-window and returns its leases | `Outstanding` legitimately drops — do not assert in-window `Outstanding` equality; gate on overflow growth |
| Any lease still held after full teardown | post-teardown `Outstanding != 0` fails the soak (bounded drain wait, not a fixed sleep) |
| A pool overflows during the window | summed `OverflowAllocations` grows — soak fails |
| `SetupExecutor.TryEnqueue` after dispose | refuses; item completed/rejected, no `ObjectDisposedException` escapes |
| Post-dispose worker dequeues an item | drains it (canceled completion + recycled) instead of executing |
| `FlowState` reused after expiry | caller must not observe it past the gate-held operation |
| Fuse key misspelled / MSBuild property used | runtimeconfig.json has no `System.GC.HeapHardLimit`; fuse silently absent |

### 5. Good/Base/Bad Cases

- Good: a warm UDP relay datagram is encoded into the transport's buffer and handed to the
  kernel via the cached `SocketAddress` — 0 B on the calling thread.
- Base: a native pool at capacity under a burst allocates-and-tracks overflow; the heartbeat
  surfaces `poolOccupancy` and the soak fails if `OverflowAllocations` moved.
- Bad: `new byte[6]`/`ToArray()`/`EndPoint` serialization on a steady-state path; holding a
  lease's `Memory` past release; asserting process-wide zero-GC on a harness that allocates
  BCL infrastructure.

### 6. Tests Required

- `NativeBufferPoolTests`: rent/return balance, dispose-drain races, idempotent release,
  `Memory` round-trip, 0 managed bytes on warm re-rent.
- `NdisPacketBufferPoolTests`: L1 (overflow-throw still returns the buffer) + L2 dispose-drain.
- `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` / `FlowTableRecyclesExpiredStatesThroughItsPool`.
- `SetupExecutorTests`: balance, reject-beyond-capacity, fault-keeps-draining, dispose joins/
  drains/refuses.
- `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes`: real relay socket, warm
  synchronous `SendSpanAsync` completes on the calling thread with 0 B (the EndPoint-trap regression).
- `HotPathAllocationGateTests`: per-packet (TCP mid-flow/reverse, UDP, socks5, RST, SYN
  retention, FlowTable, dispatcher warm path) 0 B gates.
- `GcSoakScenarioTests`: token parsing/defaults/selection, leak allowance, slope helper.

### 7. Wrong vs Correct

```csharp
// Wrong: a fresh EndPoint reaches the kernel every datagram — SocketAddress serialization
// allocates 72 B/op, invisible to any fake-transport gate.
_socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, relayEndpoint);

// Correct: serialize once in the ctor; the send sites stay allocation-free.
_relaySocketAddress = relayEndpoint.Serialize();
_socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, _relaySocketAddress);
```

```csharp
// Wrong: assert process-wide zero-GC on a loopback harness whose BCL async receives and lock
// infrastructure allocate on park — the gate can never hold and hides nothing useful.

// Correct: assert the application-level guarantee per sending thread (leak-bounded) and report
// process-wide gen counts as observability; the field alarm is the heartbeat warn event.
```

```csharp
// Wrong: assert full pool-state equality (including cumulative Outstanding) across the window —
// established TCP relays finish and return their leases mid-soak, so Outstanding legitimately
// drifts (a return, not a leak) and a 30-min run fails on correct behavior.

// Correct: gate the window on fresh overflow growth only, then — after the relays/coordinator are
// torn down — wait (bounded) for every pool to drain to Outstanding == 0 and assert the
// conservation identity OverflowAllocations == DisposedCount + InPool + Outstanding.
```
