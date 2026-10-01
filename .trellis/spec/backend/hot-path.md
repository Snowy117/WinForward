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
   that runs the synchronous warm shape (trace-off ∧ reverse-diversion declined ∧ no
   wildcard self-traffic tuple ∧ resolved ∧ (Pass ∨ Block ∨ Proxy-with-inline-server-hit))
   and returns the executor's ValueTask directly; everything else falls into
   `DispatchSlowAsync`. The reverse diversion is decided by
   `ITcpReverseHandler.WantsPacket(in CapturedFlowPacket)`
   (task 08-30-hot-path-revival, X1): TCP ∧ src port ∈ active listener-port set
   (`TcpRedirectTable` `int[65536]` reference counts, Inc in `TryClaim` under the gate
   before SYN injection, Dec in `TryRemove`/`RemoveExpired`; query `Volatile.Read != 0`).
   `WantsPacket` is ONLY the warm-entry diversion precheck — the slow path's
   `TryHandleReverseAsync` always calls the full handler (protocol gate + the single
   `TryResolveByReverse` fold probe + tombstone), so prefilter misses degrade to the slow
   path, never to wrong routing (listener-shaped tuples cannot resolve in any
   `FlowTable.TryResolve` mode; pinned by the tombstone-straggler test).
   **F2 (2026-09-30) moved the per-packet lookups off the global gates**: the self-traffic
   bypass on the warm entry is the **wildcard relay-socket half only**
   (`ISelfTrafficGuard.IsWildcardOwned`, two lock-free `ConcurrentDictionary` probes); the
   **exact-tuple half runs once per claim** (`TryHandleSelfTrafficAsync`), because a
   self-owned exact tuple never produces a flow-table state. Dropping the wildcard half
   instead is forbidden — `TcpProxyRelay` registers `(Tcp, Any:port, proxyEndpoint)` before
   its SYN, and a recycled ephemeral port would otherwise resolve a stale proxy state and
   redirect WinForward's own control connection into its own proxy
   (`ARelayWildcardTupleIsNeverProxiedOnAWarmHit`, red against the naive-deletion variant).
   The flow resolve is `FlowTable.TryResolveWarm` (one volatile slot read + the validated
   snapshot; no gate, no clock) and the TCP redirect pair is one `TryResolveByReverse`
   cache probe (the gated `_byReverse` authority on a miss), so a warm forward TCP packet
   takes **zero** redirect gate entries and a warm reverse packet exactly one probe; the
   UDP ready path resolves its session from a direct-mapped cache and takes **zero**
   coordinator gate entries. Per-lookup gate counts and the parked-gate facts live in
   `benchmarks/results/2026-09-30-warm-path-lock-chain/` (the artifact's spec-row → proof
   table). Production-composition benchmarks
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
4. **Packets are structs.** `FlowContext`, `CapturedFlowPacket`, `NativeFrameHandle` and
   `PacketLayout` are `readonly record struct` with `[StructLayout(LayoutKind.Auto)]`; completion is
   enum-driven (`PacketAction`), never closures/delegates. F4 (2026-09-30) sizes, asserted exactly by
   `FlowKeyShapeTests.StructSizesForDiagnostics`: `FlowKey` 64, `FlowContext` 80 (key + two interned
   metadata references), `CapturedFlowPacket` 152 (incl. the 16-byte layout), `FlowStateView` 96,
   `PacketView` 96, `Endpoint` 48; `PacketLayout` is 16 (`PacketLayoutTests.PacketLayoutFitsSixteenBytes`).
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
   **Since 2026-09-29 the TCP redirect data legs do the same** (task
   09-29-tcp-redirect-batched-injection): a pump-dispatched packet whose lease never materialized is
   rewritten on its capture slot and the slot is queued into a per-(adapter, direction) injection lane,
   so the slot is retained past the synchronous section until that iteration's flush — which runs before
   the next read and, at loop exit, before `ReleaseBatchBuffers()`. The rewrite is same-length (endpoint
   + checksum rewrite, MAC swap), the direction flag and enumeration handle are restamped, and the
   pre-rewrite bytes are read first by the sequence trackers. Never retain such a slot beyond the flush,
   and never treat the capture buffer as read-only after a redirect leg has handled it (the redirect
   contract in windows-ndisapi.md § "Redirect deferred-injection lanes" is the authority).
7. **Span-writing codecs.** Datagram encode uses `TryEncode(..., Span<byte>, out written)`
   into a reusable buffer (`Socks5UdpTransport._sendBuffer`); allocating overloads exist for
   tests only.
8. **Flow keys hash flat.** `FlowKey`/`TransportTuple` store packed address halves/ports/scopes and
   hash through one shared `HashCode.Combine` expression (`FlowHash.CombinePacked`; `Combine` and
   `CombineCanonical` delegate to it, so the packed and materialized forms are the same eight values
   by construction); `Equals` orders cheapest discriminators first and compares every field
   (including `OriginAdapterSlot`, `OriginAdapterGeneration` and both `ScopeId`s). **F4
   (2026-09-30):** `FlowKey` has no reference-typed field, so no string comparison can exist on any
   lookup path, and the struct is **exactly 64 B** (`FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields`
   asserts the size bound and the absence of reference-typed fields in one fact,
   `FlowKeyShapeTests.StructSizesForDiagnostics` asserts the exact 64,
   `FlowKeyPackedRoundTripsEndpoints`, `ReverseSwapsEndpointsAndScopes`,
   `PackedAndMaterializedHashesAgree`).
9. **Measurement discipline.** Benchmark gates are stated in allocation bytes and GC counts;
   treat ns/pps deltas under ~2× as noise on the dev box.
10. **A frame is parsed once; the packet carries the parse's proofs.** `IPHeaderLength`,
    `TransportLength` and the TCP flags byte are produced by the capture processor's one
    `IPTcpUdpPacket.TryParse` (the view `PacketFlowClassifier` then consumes) and derived into a 16-byte `PacketLayout`
    (`WinForward.Protocols`) carried on `CapturedFlowPacket`. `TcpFrameRewriter.IsTcpSyn`, both
    `TcpSequenceObservation` reads and the layout overload of
    `PacketChecksums.TryRewriteTcpEndpoints` consume it and never re-walk the headers; the
    span-taking entry points stay as the independent oracle. The layout carries the address family
    (the rewriter's write geometry depends on it) and keeps only what the parse cannot prove:
    `IsTcp`, `frame.Length >= TransportEnd`, and the argument-address family equality. Facts:
    `LayoutSynTestMatchesTheSpanTest`, `RedirectedForwardPacketWalksHeadersExactlyOnce`,
    `RedirectedReversePacketWalksHeadersExactlyOnce`, `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects`,
    `SequenceAdvanceIgnoresEthernetPadding`, `ExtensionHeaderFramesProduceTheSameAdvance`,
    `PacketLayoutFitsSixteenBytes`.
11. **A defaulted layout is refused, not applied.** `PacketTransport.Tcp` is `0`, so
    `default(PacketLayout)` — what a hand-built `CapturedFlowPacket` without a layout carries —
    reads as a valid TCP layout whose transport header sits at the IP header's offset with an IPv4
    family. The layout therefore carries an explicit validity stamp that only
    `PacketLayout.From(in PacketView)` writes; every consumer gates on `IsTcp`/`IsValid`, and a
    defaulted layout is refused **byte-identically** (fail-closed) instead of being rewritten at
    wrong offsets. A packet without a layout can also never be routed into SYN handling, because
    `TcpProxyCoordinator.HandlePacketAsync` reads the layout-driven `IsTcpSyn`; it takes the data
    path, whose rewrite refuses and blocks the association.
    `CapturePacketProcessor.ProcessAsync` stamps every flow packet it dispatches
    (facts: `OnlyAParsedFrameYieldsAValidLayout`,
    `DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten`, `DefaultedLayoutObservesNoSequence`,
    `RedirectLegRefusesADefaultedLayoutByteIdentically`,
    `EveryDispatchedFlowPacketCarriesAParsedLayout`). The non-flow arm carries `default` by design and
    no consumer on that path reads a layout.
12. **Sequence trackers are atomic, not locked.** `TcpRedirectAssociation`'s two trackers are `long`
    (−1 = unobserved) written by a CAS-max loop over the unchanged wrap-aware `IsSequenceAhead`
    predicate and read with `Volatile.Read`; the association holds no reference-typed instance field
    (no `Lock`), so a redirected forward+reverse packet pair takes **zero** gate entries and no
    per-association lock allocation. The cold RST readers tolerate a weakly consistent value by
    construction. Facts: `RedirectPacketTakesZeroSequenceGateEntries`,
    `TcpRedirectAssociationHoldsNoLockField`, `ConcurrentSequenceObservationsKeepTheLargerValue`,
    `UnobservedTrackerReadsNullAndObservedZeroReadsZero`.

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
- **UDP session setup bookkeeping budget: ≤1,500 B per session** (capacity pre-seed, slot claim,
  setup queue + payload copy, task machinery, tombstone/tracking structures). Measured
  1,433 B/session, spread ≤172 B (task 09-21-session-creation-cost, 2026-09-22). The design
  guarantees from the 2026-08-29 contract stand (single-slot setup queue fast path, no
  `registered` TCS, inlined setup-failure handling, method-group delegates cached in the
  constructor, dictionary pre-sizing clamped to `min(capacity, 1024)`); its ≤1 KB and
  "measured 2.1 KB → ~0.4 KB" figures predate the session-tier and teardown attribution and are
  superseded.
- **Noop probe budget: `UdpSessionBenchmarks` Noop probe ≤5,400 B/session marginal** (1→1000
  sweep; measured 5,161.8 B, spread 14.9 B; re-anchored 2026-09-22 by
  09-22-udp-teardown-session-tier-alloc after −565.2 B/session of measured reductions, was
  5,727.0; headroom to the band 238 B). The probe's window contains its own fake transport +
  flow-key harness (460.8 B/session), so product-shaped cost is ≤4,950 B/session (measured
  4,701.0 B; was 5,266.2). When the probe moves, `SessionSetupDecompositionBenchmarks` localizes
  the move: capacity 489 / admission 829 / setup start ≤172 / session tier 2,503 / teardown 1,459
  (matched shape, not re-measured) – 1,885 (single-pass) B per session. Split note
  (09-22-udp-admission-capacity-alloc): of the admission 829, 356.8 is the flow-key harness (H)
  and 272.0 is the cold `SetupWorkItem` rent production amortizes (the executor's
  `OverflowAllocations` diagnostic is zero at steady state), so steady-state admission is ≈200
  B/session (slot + queue + completion cell); the capacity 489 is the probe's `capacity = N`
  pre-seed (one-time at the production 1,024 clamp), and live flows beyond the clamp pay ≈80
  B/session of amortized dictionary growth per dictionary. Anchor falsification: a
  documented ≥3-run batch above the band on an unmodified tree is product drift to fix (never to
  relax the band).
- **Framework socket cost stays outside this budget** (control TCP connect + SOCKS5 handshake,
  UDP ASSOCIATE, relay socket) but is anchored instead of untracked, and it must be measured with
  the loopback SOCKS5 server **out of process** (`--socks5-external`,
  `WINFORWARD_BENCH_EXTERNAL_SERVER=1`; an in-process run is a diagnostic whose number carries a
  ~75,500 B/session harness share): isolated create+dispose path **7,952 B/session** (control
  connect + greeting 3,792 = 47.7 %; ASSOCIATE 959; relay socket 576; self-traffic 160; transport
  ctor + wiring 2,465), churn whole cycle **≤14,500 B/session** wave shape / **≤14,300**
  sustained (measured 13,249–14,070 / 13,720–13,869 B), real probe marginal **≤17,500 B/session**
  (measured 17,021 B, echo-fed shape) — re-anchored 2026-09-22 (task
  09-22-session-creation-cost-redo); the superseded 83,442 / 77,448 / ≤95,000 figures were
  inflated by the in-process harness server's per-connection 64 KiB relay buffer and are not
  comparable. The N=48/D=0 churn wave cell was re-measured at 12,560.3 B/session after the
  09-22-udp-teardown-session-tier-alloc reductions (−688.6; the full wave matrix, sustained shape
  and real probe were not re-run, so those bands stand with additional headroom). Reusing control
  connections was the only structural lever there — a product/protocol
  decision whose allocatable share is the ~3.8 KB/session per-flow dial; that lever is now landed
  (default-on association pooling, see the section below), and the recorded pooled `udp.churn`
  shape is **7,577.5 B/session** (`auto`) / 7,556.7–7,564.7 (`always`, check–implementer) against
  the 13,066.5 B/session per-flow Step 1 run in `benchmarks/results/2026-09-28-udp-reuse/`. The
  anchors in this bullet stay per-flow on purpose: `UdpSessionBenchmarks` and
  `FrameworkSetupBenchmarks` still construct the pool with `UdpAssociationReuseMode.Off`, so each
  recorded number keeps comparing against the shape it was measured on instead of silently
  re-basing. Anchor falsification: a
  documented ≥3-run batch above the framework/churn anchors on an unmodified tree, with the
  in-process ratio still ≈10.5×, is product drift to fix (never to relax); if the in-process
  value moves with it, the harness changed and the anchor is re-derived.
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
  Noop probe ≤5,400 B/session marginal (1→1000 sweep; N=100 row ≈5,900 B/session), real-dial runs
  against the out-of-process server (framework ladder ≤8,200 B/session, churn ≤14,500 B/session),
  soak ratio ≥70%.

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
- **An allocation gate must open only after its path is ready, and must verify it stayed on one
  thread.** `GC.GetAllocatedBytesForCurrentThread()` is a *per-thread* counter, so a gate over a window
  that spans `await`s is only meaningful when (a) the driven path is actually **ready** — it takes the
  direct/warm shape rather than a cold setup path — and (b) the reading thread did not change.
  `EstablishedUdpDatagramPathAllocatesNoManagedBytes`
  (`tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs`, measured window `:148-155`, asserts
  through `:171`) measures 64 dispatches across 64 `await`s; run alone it failed repeatedly
  (`Expected: 0, Actual: 600`, and later `3688`, `4328`, `5352`).

  **The cause is a not-yet-ready window, not thread migration** (corrected 2026-09-21 after the fix was
  measured; the earlier "with a cold pool the continuation migrates to another pool thread and the
  before/after readings land on different threads" explanation was **wrong**). Evidence: the managed
  thread id was constant across 40 instrumented 64-dispatch loops (8 runs × 5 loops) — no continuation
  migration ever occurred, consistent with the source, whose whole chain
  (`NdisPacketActionExecutor.ProxyAsync` → `UdpProxyCoordinator.TrySendSpanAsync` →
  `UdpProxySession.SendSpanAsync`) completes inline against the fake transport; and disabling tiered
  compilation/PGO did **not** remove the burst — an early window riding the cold setup path is not a
  tiering event, so that negative result stands (reconciled 2026-09-30: tiering *does* add a separate
  one-time lump to the counter, which `TieredCompilation=0` removes on every shape measured since; see
  "Allocation-gate stability" below. Read the 2026-09-21 result as "readiness was the cause *of that
  burst*", not as "tiering cannot perturb a per-thread window"). What the measurements do show is
  coupling to readiness:
  the per-thread delta was non-zero **only in the first batch**, and every non-zero first batch
  coincided with a first-batch send-count shortfall (`SpanSends` delta of 17/40/67 where 64 was
  expected). While a session is not yet `Ready`, datagrams take the bounded drop-oldest setup queue and
  are sent later by the background flush pipeline — so an early window rides the cold setup path, which
  allocates once and leaks sends into the window. Once the path is ready the window is allocation-free
  and the counter is exact (every later batch measured 0 B).

  So: **fix the window, not the threshold.** Open the measured window only after (1) a probe send proves
  the session admits directly — the counter advances by exactly one and
  `Diagnostics.PendingSetupBytes == 0` — and (2) an allocation-stable probe batch shows the exact 0-byte
  reading (bounded retries; the landed gate allows 8). Keep the exact `Assert.Equal(0, allocated)`,
  assert the managed thread id did not change across the window, and always pair the byte assertion with
  the thread-independent call counter (`SpanSends`), which catches a regression regardless. Readiness is
  **not** a licence to relax the threshold: a real per-packet allocation never stabilizes, so the
  bounded loop fails rather than passes (injecting `new byte[1]` on the warm path makes the gate fail
  with "the UDP warm path never became allocation-stable").
- **A single-threaded `SynchronizationContext` is not a substitute for the readiness precondition.**
  This gate's chain awaits with `ConfigureAwait(false)` throughout (`NdisPacketActionExecutor.cs:360,450`
  and the send tails), so a context cannot capture the continuations at all. (Migration was not the
  observed failure — see the bullet above — but the thread-id assertion is kept as cheap insurance,
  since the counter is per-thread either way.)
- **Sibling gate, same shape.** `DispatcherWarmFastPathAllocatesNoManagedBytes` carries the same
  readiness transient; it was left untouched as outside that task's scope, and the same
  ready-then-measure shape applies when it is next touched. Its reported 1-of-5 class-alone failure
  (`Actual: 1880`) could not be reproduced by a later independent run, so its frequency is unconfirmed.
- **Approved cold-path materialization.** `Socks5UdpTransport.SendSpanAsync` copies with
  `payload.ToArray()` only on the contended-gate branch: the span views native capture memory
  that recycles once dispatch returns, so it cannot cross the gate `await`. This is a documented
  cold-path exemption; the warm uncontended shape stays zero-alloc.
- **The UDP transport's disposal guard is outside the warm shape** (C4, 2026-09-21).
  `Socks5UdpTransport.DisposeAsync` claims a one-shot `int` (`Interlocked.Exchange`) and sets it
  **before** `_socket.Dispose()`; `SendSpanAsync` refuses with `ObjectDisposedException` on a plain
  `Volatile.Read(ref _disposed) != 0` taken *before* `_sendGate.WaitAsync`. That is one volatile read
  plus one branch — 0 B, no CTS, no closure, no `Run`, no state machine — and the "`_sendGate` disposed
  last" order is unchanged, which is what keeps `WarmSyncSendAllocatesNoManagedBytes`,
  `SendSpanAsyncWarmPathRunsNoAsyncStateMachine` and `EstablishedUdpDatagramPathAllocatesNoManagedBytes`
  green. Its residual window (a sender preempted between the read and `WaitAsync` can still reach a
  disposed gate) is accepted: closing it absolutely would require a lease on the per-datagram path.
- **The capture refresh and demand machinery is off the packet path** (C4, 2026-09-21).
  `LayeredCaptureRunner`'s monitor thread, its periodic tick, and the extracted
  `CaptureRefreshWorkers` type allocate (a `Run` child's delegate + state machine, one OS thread) per
  *generation or tick*, never per packet; the capture pump's own zero-allocation gate
  (`NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes`) is unaffected.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Warm entry contains a capturing lambda anywhere in its body | 0 B gate fails by design; extract the lambda to a cold helper |
| Warm entry returns before the cold branch | still 0 B — no display class is hoisted |
| A materializing overload/copy is re-added on the send seam | 0 B gate fails (`SpanSends` count and/or allocation delta) |
| A gate's measured window opens before the driven path is ready (or spans `await`s that resume on another thread) | per-thread delta is invalid — the gate fails spuriously *or* can mask a small regression; prove direct admission and allocation stability first, then assert the thread is unchanged and keep the exact zero |
| A gate's readiness/stable-warm-up phase is implemented as a relaxed threshold | forbidden — the stabilization loop must require an exactly-0 delta, so a genuine per-call allocation makes it fail instead of pass |
| Span reaches `Socks5UdpTransport` with the send gate uncontended | zero-alloc sync `SendTo` |
| Span reaches `Socks5UdpTransport` with the gate contended | `payload.ToArray()` cold copy (documented exemption) |

### 5. Tests Required

- `HotPathAllocationGateTests`: `MidFlowRewriteAndInjectAllocatesNoManagedBytes`,
  `ReverseRewriteAndInjectAllocatesNoManagedBytes`, `EstablishedUdpDatagramPathAllocatesNoManagedBytes`
  — the UDP gate asserts 0 allocated bytes and that the fake's `SpanSends` advances by exactly the expected count after the measurement (span is the only send seam).
- `NativeBufferPoolTests` / `NdisPacketBufferPoolTests`: balance identity + dispose-drain races
  (`ReturnsRacingDisposeNeverStrandBuffers`).
- Every allocation gate must be verifiable in isolation, not only inside the full suite: run it with
  `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~<GateName>"`. A gate that only
  passes when the pool is warm is a weak gate — fix it before trusting it to protect a path you are
  about to change (see "An allocation gate must open only after its path is ready").
- Baseline must stay behavior-zero (678 tests green on this task; the suite is 980 Core.Tests + 18 Analyzers = 998 as of 2026-09-30).

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

```csharp
// Wrong: the measured window is opened before the driven path is ready (and without checking the
// thread), so the first batch rides the cold setup path — `before` and `after` then disagree.
// The same code measured 600 B alone and 0 B inside the full suite (2026-09-21); the thread id was
// constant across the window every time, so continuation migration was NOT the cause — readiness was.
var before = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < count; index++)
    await executor.ProxyAsync(packet, server, cancellationToken);
Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

// Correct: prove the path is ready and allocation-stable first, then open a thread-checked,
// exactly-zero window. A single-threaded SynchronizationContext is NOT a substitute: this chain
// awaits with ConfigureAwait(false) throughout (measured 2026-09-21), so a context cannot capture it.
Assert.True(await ProbeAdmitsDirectlyAsync(), "the gate relies on direct admission, not the setup queue");
Assert.True(await WaitForAllocationStableBatchAsync(), "the UDP warm path never became allocation-stable");

var threadId = Environment.CurrentManagedThreadId;
var before = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < count; index++)
{
    var pending = executor.ProxyAsync(packet, server, cancellationToken);
    Assert.True(pending.IsCompletedSuccessfully, "the gate relies on the synchronous fast path");
    await pending;
}
Assert.Equal(threadId, Environment.CurrentManagedThreadId);
Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
Assert.Equal(count, factory.Transport!.SpanSends - spanSendsBeforeMeasure);

// The documented-bound form is only for a path that genuinely must yield; the readiness and
// allocation-stability precondition still applies, and the reason must be recorded above.
Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, documentedBound);
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
  `SemaphoreSlim` signal + dedicated `Thread` workers (`s_defaultWorkerCount = max(2 × CPU, 16)`,
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
  retains a `FlowState` across an await). **Since F2 (2026-09-30) the warm entry reads the
  validated view instead of the instance**: `FlowTable.TryResolveWarm` returns a `FlowStateView`
  produced by the state's barrier-bracketed seqlock (`TrySnapshot`) plus a transport-tuple
  corroboration, so a recycled/torn state is a false miss, never a wrong decision. The gated
  `TryResolve`/`TryClaimResolved` still hand out the pooled instance (the pre-existing post-gate
  ABA on the slow/claim path is a recorded follow-up, design §2.7).
- **FlowTable warm cache (task 09-30-warm-path-lock-chain).** `FlowState?[] _warm` is a
  pre-allocated direct-mapped cache over the gated `Dictionary` authorities (capacity × 64 slots,
  clamped to [4,096, 262,144]; 2 MB at the shipped 65,536 default, allocated once in the ctor and
  never grown). A hit is served only after the seqlock snapshot and the exact tuple corroboration,
  so a collision, an unpopulated slot or a torn read is a **false miss** (the gated path, unchanged)
  and never a wrong decision. One window is inherent to the shape and is stated rather than smoothed
  over: a probe that loaded the slot reference before a concurrent sweep's `ReferenceEquals`-guarded
  clear can still return the triple it validated, i.e. serve the decision the flow had at the moment
  the slot was read; the reader whose snapshot lands after the recycling `Reset` gets a false miss.
  Population at claim, write-through on a gated hit and the `ReferenceEquals`-guarded clear in the
  sweep's removal hold are the only writes, all under `_gate`; the authorities' `Count`/`Capacity`
  stay exact (no maintained counter) and no per-claim allocation is added —
  `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` is the acceptance gate (the reverted
  `ConcurrentDictionary` variant measured 488 B/claim+expire and is the recorded comparator).
  **F4 (2026-09-30) re-proved this cache under the packed key**: `FlowTable.SlotOf` reads
  `FlowHash.CombineCanonicalPacked` over the key's packed halves, `TransportTuple` is a packed 48 B
  value with the same field set, and `Matches` still corroborates by tuple equality — so packed-hash
  drift can only cost a warm slot, never a wrong answer
  (`PackedAndMaterializedCanonicalHashesAgree`, `CanonicalSlotIsOrderIndependent`,
  `FlowTableTransportTupleIsUniqueAcrossOrigins` and `WarmCacheHitServesTheValidatedView` for the
  forward and reverse corroboration, `FlowTableWarmResolveAllocatesNoManagedBytes` re-run unchanged).
  The tuple's field-set equality is a source-level property of a private nested type with no
  dedicated discrimination fact: the packed rewrite's coverage is the observable corroboration
  above, so a field dropped from `TransportTuple.Equals` would only be caught if it also changed an
  existing fact's outcome (recorded residual, `implement.md`).
- **FlowTable live-slot registry + chunked sweep (task 09-30-expiry-sweep-bounded-pause).** The table
  keeps `_liveStates[0.._liveCount)` as a hole-free mirror of `_states` (append in `TryClaimResolved`
  under `_gate`; removal is a swap-remove **at the cursor** that happens strictly **before**
  `ReturnState`, so a recycled state can never keep a registry slot; `LiveStateCountForDiagnostics`
  takes `_gate` so a churn test can assert it equals `Count`). `RemoveExpired` walks that registry at
  **minimal hold granularity**: a scan hold examines ≤ `SweepChunkEntries` (256) entries and stops on
  the first idle-elapsed candidate, the `isHeld` predicate runs with **no table lock held** (the nested
  store/tombstone-lock edge is gone — a held entry keeps its original idle point), and a removal hold
  re-checks identity/idleness/key-presence and removes exactly one entry, so the removal lands at the
  cursor and the swapped-in tail is examined next (no rewind, no batch). Deadline semantics: **expired
  is `ActivityBucket < cutoffBucket`** (task 09-30-warm-path-lock-chain; supersedes the
  `LastActivityUtc.UtcTicks <= cutoffTicks` integer form), where `cutoffBucket =
  ActivityBucket.Cutoff(now, idleTimeout)` is computed once per call from the argument and
  `idleTimeout <= TimeSpan.Zero` yields `long.MaxValue` (the drain call sites keep "retire everything
  on this call"). The strict `<` is load-bearing: a stamp is quantised **down** to its bucket, so
  retirement lands in `(idleTimeout, idleTimeout + 500 ms]` — never early, at most one bucket late —
  while `<=` would retire up to one bucket early. The comparison always reads the internal integer
  bucket, never `LastActivityUtc`.
  `SweepChunkEntries` is a scan bound only — one entry per removal hold whatever it is — and the sweep's
  own duration is report-only (every removal takes its own gate hold).
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

## Real-Dial Measurement Harness (task 09-22-session-creation-cost-redo)

### 1. Scope / Trigger

Trigger: any change to a benchmark instrument that dials the loopback SOCKS5 server **and** reads
allocation counters (`FrameworkSetupBenchmarks`, the real-transport `UdpSessionBenchmarks` rows,
`udp.churn`). The server must not share the measured process: it allocates a 64 KiB relay-loop
buffer plus a per-connection socket/arrays per accepted control connection, and
`GC.GetTotalAllocatedBytes` is process-wide — the 2026-09-21/22 "framework path
83,442 B/session" was ~75,500 B/session of harness (2026-09-22 correction).

### 2. Signatures

- Child server mode: `--stability --serve-socks5-udp --flows <N> [--dial-delay-ms <D>]`
- Churn opt-in: `--socks5-external`; benchmark opt-in: `WINFORWARD_BENCH_EXTERNAL_SERVER=1`
- Helper: `ExternalLoopbackSocks5UdpServer.StartAsync(int flows, TimeSpan associateDelay, CancellationToken)` → `ControlEndpoint`, `IsEnabled`, `ThrowIfExited()`

### 3. Contracts

- Handshake: the child prints exactly one stdout line `{"controlPort":<P>,"echoPort":<E>}`;
  everything else it writes goes to stderr.
- Lifetime: the child exits on stdin EOF (parent death or disposal) or SIGTERM/SIGINT; the parent
  closes stdin, waits 5 s, then `Kill(entireProcessTree: true)`.
- Child content: `EchoReceiver(N)` + `LoopbackSocks5UdpServer(receiver.Endpoint, associateDelay)`
  — the external probe is **echo-fed** (one response per session); the historical in-process probe
  was discard-fed. Quote the shape with any probe number.
- Default: no flag and no env var = the historical in-process harness, kept byte-identical so
  recorded command lines keep reproducing their (harness-inflated) numbers.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| no handshake line within 15 s | `StartAsync` throws with the child's drained stderr attached |
| child exits mid-instrument | next `ThrowIfExited()` fails the run (probe flush wait, churn wave top and response wait) — never a silent zero-response row |
| double dispose | the latch guard makes the second call a no-op; stdin is closed once |
| child outlives a crashed parent | stdin EOF ends it; the 5 s wait + process-tree kill covers the rest |

### 5. Good/Base/Bad Cases

- Good: real-dial instruments read only client allocations; the framework ladder's measured
  harness share is ~75,500 B/session (in-process 83,442 → out-of-process 7,952).
- Base: in-process runs stay valid as diagnostics when the harness share is stated or subtracted.
- Bad: adding a new real-dial allocation instrument against the in-process server, or quoting an
  in-process number without its harness share.

### 6. Tests Required

- Smoke: `--serve-socks5-udp --flows 8 < /dev/null` prints the handshake line and exits 0.
- A/B anchor: the framework ladder reproduces in-process ≈83,442 / out-of-process ≈7,952 B/session
  (≥3 runs, `--job short`); the budget anchors are in §3 above.

### 7. Wrong vs Correct

```csharp
// Wrong: measure a real dial with the loopback server in the measured process — its
// per-connection 64 KiB relay buffer + 4 MiB relay socket + control arrays land in the client's
// GC.GetTotalAllocatedBytes (a ~9× inflation on the framework ladder).
_server = new LoopbackSocks5UdpServer(discardEndpoint);

// Correct: host the server in a child process and dial its control endpoint; the instrument
// reads only client-side allocations and the topology matches production.
await using var server = await ExternalLoopbackSocks5UdpServer.StartAsync(flows, TimeSpan.Zero, token);
_socks = new Socks5Server("benchmark", "127.0.0.1", (ushort)server.ControlEndpoint.Port, null, null);
```

## UDP association pooling: counters, capability-evidence cost, and the re-based churn shape (task 09-28-udp-association-reuse, 2026-09-28)

### 1. Scope / Trigger

Trigger: any change to `Socks5UdpTransport`'s established-datagram path, the per-lease capability
evidence, the UDP association counters, or a recorded UDP framework/churn anchor. The pooling
contract itself (placement, capability verdict, recovery, retention) is in
[udp-relay.md](./udp-relay.md).

### 2. Signatures

- `UdpAssociationEvidence` (`UdpProxy/UdpAssociationCapability.cs`): `DatagramsSent` and
  `SawResponse` (reads), `RecordDatagramSent()` (one `Interlocked.Increment`),
  `RecordResponseReceived()` (a `Volatile.Read` short-circuit plus a write-once
  `Interlocked.Exchange`).
- `UdpAssociationLease.RecordDatagramSent()` / `RecordResponseReceived()` — the transport's only
  per-datagram calls, and the whole hot-path addition of the pooling change.
- `RuntimeCounters`: `UdpCapacityRejections`, `UdpSetupFailures`, `UdpAssociationLost`,
  `UdpAssociationRecovered`, `UdpAssociationFallbacks` (alongside the pre-existing
  `UdpSetupRejections` / `UdpSetupBudgetRejections`).
- Transport defaults: `Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize = 64 * 1024`;
  `ConfigurationLoader.DefaultUdpRelayReceiveBufferKb = 64`;
  `ConfigurationLoader.DefaultUdpSessionIdleTimeout = 30 s`, with
  `UdpProxyCoordinator.OneShotIdleTimeout = 5 s` as the retention of a completed one-shot exchange and
  the UDP tick derived from the effective floor (5 s at the defaults).

### 3. Contracts

- **The established-datagram path gains four allocation-free operations.** (1) One volatile read
  of the lease's fault state (a null check), so an association that died without recovery refuses
  the datagram before the send gate and before the socket (I4). (2) One reference compare
  of the cached relay publication against the lease's current one, so an in-place re-association
  redirects the next send without re-serializing an endpoint or touching the socket. (3) One
  `Interlocked.Increment` on the lease's evidence, recorded only after the kernel accepted the
  datagram — every send shape (warm sync `SendTo`, the overlapped async fallback, and the
  contended-gate tail) calls it exactly once. (4) On the receive side, a write-once response flag:
  the first successfully decoded relay datagram pays one `Interlocked.Exchange`, and a volatile
  read short-circuits every later response. No pool interaction, no allocation, no state machine.
- **Nothing is recorded per skipped datagram.** `UnexpectedSource`, `Oversized`, `Malformed`, and
  `ConnectionReset` return before `RecordResponseReceived()`, so a skip is an anomaly, never
  evidence that a server answered — the capability rule depends on that distinction.
- **Counter semantics and one-shot events.** `udpCapacityRejections` counts each datagram refused
  at the session-capacity gate (the accompanying `udp.session.capacity-block` warn is rate-limited
  to 5 s); `udpSetupFailures` counts each genuine setup failure — dial, ASSOCIATE, or session
  construction — and is exactly the set that arms the 1 s setup cooldown (shutdown cancellation
  and `UdpAssociationLostException` are deliberately not counted here); `udpAssociationLost`
  counts each send failure classified as `UdpTeardownReason.AssociationLost`, i.e. once per attached
  flow that failed its next send after the association faulted (both the ready path and the
  setup-queue flush map through `UdpProxyCoordinator.TeardownReasonFor`), so one association death
  increments it up to `udpAssociationFlowsPerAssociation` times; `udpAssociationRecovered` counts each
  successful in-place re-association (debug `udp.association.recovered`); `udpAssociationFallbacks`
  counts each server flipped to per-flow associations for the run, at most once per server (the
  gate-guarded sticky verdict is what makes the warn and the counter one-shot together).
- **The recorded framework/churn anchors are per-flow shapes and stay comparable.** The default
  relay receive buffer is **64 KiB** per socket (128 KiB through the F6 task's Step 1, a hard-coded
  512 KiB before that) and the session retention is the **two-class** shape: the configured
  `udpSessionIdleSeconds` (30 s) for every session except a completed one-shot exchange, which
  retires at `UdpProxyCoordinator.OneShotIdleTimeout` = 5 s, swept on the cadence the *effective*
  retention floor derives (`IdleExpirySweeper.EffectiveUdpRetentionFloor` → 5 s, was 15 s under
  uniform retention). Kernel receive-buffer totals and any pre-F6 measurement must therefore be read
  with the shape they were recorded on; the 2026-10-01 artifact
  (`benchmarks/results/2026-10-01-udp-session-footprint/`) re-ran the loss/burst/churn anchors on the
  shipped shape and they held (loss 0, burst 48/48 with `establishmentLossRate` 0, churn inside its
  ≤8,200 B/session band), so the numbers below keep their meaning without a re-base. `UdpSessionBenchmarks` and
  `FrameworkSetupBenchmarks` still construct the pool with `UdpAssociationReuseMode.Off`, so the
  ≤5,400 B/session Noop probe, the ≤8,200 B/session framework ladder, and the
  ≤14,500/≤14,300/≤17,500 churn anchors continue to describe the per-flow shape they were recorded
  on.
- **Pooling re-bases the control-connection half, not the relay-socket half.** One authenticated
  control connection plus one ASSOCIATE now serve up to `udpAssociationFlowsPerAssociation` flows,
  so the per-flow control connect + greeting (~3.8 KB/session of the framework path, plus the
  per-flow ASSOCIATE) is amortized while each flow keeps its relay socket and its per-flow
  bookkeeping. Recorded
  (`benchmarks/results/2026-09-28-udp-reuse/`, 48-flow × 120 s sustained churn, out-of-process
  SOCKS5 server): **13,066.5 B/session** per-flow at Step 1 → **7,556.7 / 7,564.7** (`always`,
  check / implementer runs) / **7,577.5** (`auto`, Step 3). The same directory's `udp.sessionBudget` soak records the
  descriptor ratio: 1.94–1.95 descriptors per live session at 100 new flows/s while the pooled head
  was saturated at the pool's original 256 flows per server, and 1.06 in the pooled regime (rate 5)
  — the descriptor floor stays ≈1 per live flow plus one control connection per association.
- **The counters are observational only** and never influence packet disposition, fail-closed,
  recovery, or shutdown decisions (the `RuntimeCounters` contract).

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Warm sync send accepted by the kernel | one evidence increment, 0 B, no pool interaction |
| Contended gate / overlapped async send accepted | same one increment on the send tail, still 0 B on the fast path |
| Relay datagram skipped (source/oversize/malformed/reset) | no evidence recorded |
| First decoded relay datagram for a lease | write-once response flag set (one `Interlocked.Exchange` per lease) |
| Session capacity full | datagram refused, `udpCapacityRejections`++, rate-limited `udp.session.capacity-block` warn |
| Genuine setup failure | `udpSetupFailures`++, 1 s setup cooldown armed, rate-limited warn |
| Association faulted, attached flow fails its next send | `udpAssociationLost`++, `AssociationLost` removal, no cooldown |
| In-place re-association succeeds | `udpAssociationRecovered`++, debug `udp.association.recovered`, no session touched |
| Server detected as source-port-pinning | `udpAssociationFallbacks`++ and `udp.association.fallback` warn once per server per run |
| Framework/churn instrument run on the pooled default | forbidden — the instruments stay on `off`, so each anchor keeps its recorded per-flow shape |

### 5. Good/Base/Bad Cases

- Good: a warm pooled datagram is encoded into the transport buffer, sent to the lease's current
  relay, and costs one interlocked increment; the recorded churn drop to 7,577.5 B/session is the
  control-connection half of the framework cost, amortized 16 flows to one association.
- Base: a faulted association's next datagram per attached flow is dropped fail-closed, counted,
  and re-established through the pool without a cooldown; the relay-socket half of the cost is
  unchanged from the per-flow shape.
- Bad: adding a pool interaction, an allocation, or an endpoint materialization to the per-datagram
  path; recording a response for a skipped datagram (it would let a pinning server look
  shareable); routing the framework/churn instruments through `auto` and re-basing their anchors.

### 6. Tests Required

- `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` — 0 B, and it must
  still pass in isolation (the readiness/thread rules above apply).
- `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` — real relay socket, 0 B on the
  warm shape.
- `UdpAssociationEvidenceLifetimeTests` / `UdpAssociationCapabilityTests` — evidence is recorded
  only on successful sends and decoded responses, leaves the live set on release, and the >cap
  lease case cannot mis-index it.
- `UdpProxyCoordinatorTests` / `UdpSessionSetupTests` — `AssociationLost` counting and the absence
  of a setup cooldown on both the ready path and the flush window, versus a genuine setup failure.

### 7. Wrong vs Correct

```csharp
// Wrong: record the send before the kernel accepted it — the sampler's send-side evidence then
// counts datagrams that never left the host, and a failing relay looks like a pinning server.
_ = _socket.SendTo(...);
_lease.RecordDatagramSent();
```

```csharp
// Correct: one interlocked increment after acceptance; nothing at all on the skip paths.
_ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, CurrentRelaySocketAddress());
_lease.RecordDatagramSent();
```

```csharp
// Wrong: re-basing a recorded per-flow anchor by pointing the framework instrument at the pooled
// default — the number moves for a harness reason, not a product one.
await using var associations = new UdpAssociationPool(registry, UdpAssociationReuseMode.Auto);

// Correct: the instrument keeps the shape its anchor was measured on.
await using var associations = new UdpAssociationPool(registry, UdpAssociationReuseMode.Off);
```

## Measurement self-checks (task 09-29-benchmark-coverage-remaining-findings, 2026-09-29)

### 1. Scope / Trigger

Any new benchmark row, stability scenario or allocation/timing gate. A measurement that silently measures
the wrong path is worse than no measurement: it produces a plausible number that later work optimizes
against. Every one of the traps below was hit while building this task's coverage, so each check exists
because something real slipped through without it.

### 2. Contracts

- **Prove the measured path is taken, in setup.** A frame that the rewriter rejects is *faster* to process
  than a frame it accepts, so a row measuring the rejection path looks like an improvement. Rows must
  assert their operation succeeds on the pristine input before anything is timed
  (`TcpRedirectDataPathBenchmarks.ProveRowsSucceed`, the parser rows throwing on `TryParse == false`), and
  setup must fail loudly otherwise.
- **Prove the population is live before sampling.** A census or churn row whose population silently failed
  to build reports per-flow costs for flows that do not exist. Assert the population against an
  independent counter (the server's CONNECT-reply count, the factory's created count, the table's own
  `Count`) and abort the run on mismatch — the population proof is the only thing allowed to abort a
  report-only row.
- **Model the wait being measured.** A wake-latency row must confirm the reader actually parked before it
  is armed; arming first measures a hot handoff (0.5 µs) and calls it a wake (78 µs). The same rule
  applies to any latency whose claim is "the cost of waiting".
- **Keep the harness out of its own window.** Take allocation baselines after the harness has created its
  own clocks, buffers and lists, or the gate reports the harness's allocations as the product's (the
  pump-idle gate failed with exactly 40 B of `Stopwatch` before this was fixed).
- **Never use a fake collaborator for the thing under test.** A guard/executor/transport that answers
  before touching the code under test makes the measured delta structurally zero: `NeverOwnedGuard` hides
  the whole self-traffic path, and the same workload through the real registry costs 2× at one thread.
- **Mirror every production wiring point the measurement depends on, not only the one under test.**
  A scenario that drives a coordinator's sweep directly has no capture pump, so it must tick the
  composition's `ActivityBucketClock` itself at the pump's cadence
  (`DurableCaptureBundle.FlushPendingInjections`; `UdpSessionBudgetRun.TickActivityClock` is the mirror).
  A frozen clock leaves every session stamped at the coordinator's construction bucket, so the first
  sweep past the retention **mass-retires the whole population** and the run measures a sawtooth
  artifact instead of the retention it names. Measured 2026-10-01 (F6): the frozen-clock
  `udp.sessionBudget` read a 4,479-session steady peak before the change and 500 after, and the after
  run's `poolingCovered` verdict failed because the association high-water mark (60 s warm retention)
  still reflected the pre-steady transient while the session peak was measured only post-steady; the
  same tree with the tick mirrored read 4,539 before and 1,014 after, both verdicts green. A verdict
  that changes when an *instrument* mirror is added is an instrument defect — fix the mirror, never the
  ceiling or the load.
- **A wall-clock maximum is not an acceptance figure when the host queueing dominates it.**
  `flowTable.sweepPause`'s phase-scoped `maxSweepWindowPauseMs` fixed *attribution* (in-window resolves
  no longer include the scenario's own refill contention) but not *attributability to the hold*: the
  calibration control — the same in-window flag armed ~120 ms with **no product call at all**, now
  reproducible with `--sweep-window-control-ms <n>` — measured 5.19 ms with 12,336 in-window overshoots,
  the same order as the code under test, and the raw series stays refill-dominated (≥96 %). Sweep
  acceptance is therefore proven by **countable work-per-hold probes**
  (`SweepAllocationGateTests.FlowTableSweepHoldWorkIsBoundedByChunkEntries` and
  `…FlowTableProductionShapeSweepRecordsItsHoldShape`), with every timing field classified report-only
  and quoted beside the control.
- **Prove the gate can fail.** For every exact gate, inject the violation once (a 16-byte allocation, a
  one-short population) and record the exact failure message before restoring the file; a gate whose
  failure mode has never been observed is an assumption.
- **Report-only unless the metric is exact.** Timing, throughput, percentiles and residency are series
  comparisons; allocation bytes, call counts and GC counts are gates. The research targets (e.g. "max
  resolve pause < 0.5 ms") are recorded as target lines in the verdict row, never as pass/fail lines.

### 3. Tests Required

- One gate test per exact claim, in a file of its own when the existing gate file is near the line budget.
- One self-check test for any new frame or key builder, checked against an implementation independent of
  the builder (the test project's own checksum code, the production parser).
- A scenario-selection test when a scenario is added or deliberately excluded from `--scenario all`, so the
  documented invocation stays falsifiable.

## Allocation-gate stability: the tiering host contract, the gate shape, and the repeat-run proof (task 09-30-test-flake-and-hang-stabilization, 2026-09-30)

### 1. Scope / Trigger

- **Trigger**: an exact 0-byte gate over `GC.GetAllocatedBytesForCurrentThread()` failed roughly one run
  in six (`HotPathAllocationGateTests.DispatcherWarmFastPathAllocatesNoManagedBytes`,
  `Expected: 0 / Actual: 1880`), in isolation as well as under the full suite. The recorded failure was
  once attributed to `ReverseRewriteAndInjectAllocatesNoManagedBytes` with `Actual: 7520`.
- **Scope**: every exact per-thread allocation window in the test tree, the environment its host runs in,
  and the run-count procedure that proves a fix. The product path was never at fault here.

### 2. Contracts

- **The per-thread counter is not sound on its own across a window.** `GC.GetAllocatedBytesForCurrentThread()`
  returns the current thread's cumulative allocated bytes. In a host with tiered compilation enabled the
  runtime performs **one-time managed allocations on the calling thread as hot code is published**
  (call counting and on-stack replacement both do it), and such a lump can land inside a measured window
  whose driven code allocates nothing. Measured 2026-09-30 on this host:
  - a loop whose only work was reading the counter saw a single **7,336–7,360 B** jump in **12/20** and
    **17/20** process runs, always in one batch, at a random iteration;
  - `HotPathAllocationGateTests.DispatcherWarmFastPathAllocatesNoManagedBytes` failed **2/20** runs with
    **1,880 B**, and an instrumented run located the lump at iteration 65 of 256 with **8,008 B**;
  - one-time first-use costs are visible the same way and are absorbed by the warm-up (the first call of
    a fresh `NoInlining` method measured **136 B**; the dispatcher path's first batch measured **824 B**).
- **The measurement environment is part of the gate.** `WinForward.Core.Tests` therefore sets
  `<TieredCompilation>false</TieredCompilation>`: every method is compiled by the optimizing JIT on first
  use, so no tiering event can land in a window, and the gates measure the optimized steady state that
  production reaches after warm-up anyway. Evidence (20 runs per arm, class filter): OSR-only off
  (`TieredCompilationQuickJitForLoops=false`) **2/20** fails, a raised call-count threshold
  (`TC_CallCountThreshold=1000000`) **2/20** fails, `TieredCompilation=0` **0/20** and
  `TieredCompilation=0 DOTNET_gcConcurrent=0` **0/20**. The same toggle on the counter-read probe:
  17/20 and 12/20 baseline, 0/20 with `TieredCompilation=0`.
- **A residual host lump survives every host setting tried, and it is not the product.** With tiering
  disabled the full suite still caught lumps inside exact windows —
  `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` 168 B and 5,216 B,
  `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` 7,384 B — on paths whose only work is
  polling, with the sweep figure carrying the same "8,192 − k" signature as the counter-read probe.
  Rate: 3 failures in the 30 full-suite runs that carried the tiering fix, and 1 more in the 10 runs
  after it. `DOTNET_gcConcurrent=0` looked like a second fix (0/20 full-suite runs against 3/30 without
  it) but **did not replicate** (1 failure in the next 10 runs), so that setting was reverted and the
  arm is recorded as a negative result. Disposition: the family is bounded, self-naming (the gate names
  itself and its exact bytes) and explicitly *not* product code; a future task that wants a
  deterministic full suite must build a per-window control that can tell a host lump from a driven
  allocation, because no preflight, host toggle or assertion shape tested here does it.
- **Ruled out, do not re-derive**: the counter being unfaithful on an *idle* thread (0 jumps under forced
  Gen0/Gen1/Gen2 collections and 668 sibling-triggered collections in a console host), the VSTest
  diagnostics server (`DOTNET_EnableDiagnostics=0` → 15/20), and warm-up tuning. Tiering and concurrent
  GC are the two families; both fixes are host configuration, never a relaxed threshold, and an
  exactly-zero probe batch before the window does **not** protect the window itself against either —
  the second pump failure (5,216 B) landed after eight clean probe batches.
  One probe arm (tiering *and* diagnostics disabled together) still showed 3/20, so the family is
  **suppressed, not proven impossible** — the acceptance evidence is the run-count proof below, not the
  configuration alone.
- **The host change costs ~3 s per suite run.** `TieredCompilation=false` makes the test host compile
  every method with the optimizing JIT on first use, so the suite went from ~7 s to ~10.5 s per
  full-suite run (+50 %). That is the recorded price of determinism; it buys gates whose failure means a
  product allocation, and it is not a regression to rediscover.
- **The repeat-run proof is the only accepted stability evidence.** A single green run says nothing about
  a ~8 % flake. Use the procedure in §4; a failure anywhere in the loop stops the proof and returns to
  diagnosis. That loop proves a *product/allocation-regression* claim; the host-residual family is proven by
  the per-gate process run in the section below, and the two procedures are not interchangeable.
- **Gate the tick shape the site actually repeats.** A retiring tick whose teardown `await`s disposal
  outside the gate cannot be byte-exact inside the window (the disposal legitimately allocates and
  suspends), so the three async sweep legs are gated on a **no-op tick over a populated world** —
  `TcpRedirectSessionStoreSweepAllocatesNoManagedBytes` (64 registered `Redirecting` sessions + unexpired
  tombstones), `UdpProxyCoordinatorSweepAllocatesNoManagedBytes` (16 fake-transport sessions) and
  `UdpAssociationPoolSweepAllocatesNoManagedBytes` (shared associations with outstanding leases, so
  `CanRetire` is false). The population is the proof that the window is not vacuous: an empty store would
  green a "skip the scan when empty" fast path. The synchronous tables are gated on their real retiring
  tick instead. A gate whose driven call can strand a lease must release it **before** asserting (a
  failing assert that skips the release hangs the pool's drain instead of failing the test).
- **The landed shape extends to every exact window in the suite**, not only the two that flaked:
  `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes`,
  `NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes`,
  `NdisCapturePumpIdleWaitTests.IdleWaitIterationsAllocateNoManagedBytes` (F5, 2026-10-01: the
  production `NdisPacketArrivalSignal.Wait` entry point over an unsignaled event, timeout zero),
  `NdisApiReadShapeTests.DriverReadPathAllocatesNoManagedBytes` (F5: the driver's per-drain read
  path through `CreateForTests`),
  `FlowAttributionPipelineTests.PendingAdmitAllocatesOnlyTheDocumentedColdBudget` (F8, 2026-10-01: the
  deferred-attribution admission — the cold budget is one entry object + one 32-slot ring + the first
  retained slot, and every later packet of an already-pending flow is an exactly-zero ring append) and
  `CompositePacketArrivalSignalTests.CompositeArrivalWaitAllocatesNoManagedBytes` (F8: the production
  two-handle park, `CompositePacketArrivalSignal.Wait` over the borrowed driver signal and the
  pipeline-owned event, both unsignaled at a zero timeout) and
  `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` now open their windows only after a
  bounded run of probe batches that each read an **exactly-zero** delta on an unchanged thread (the UDP
  gate's landed shape). The probe batches are part of the contract, not decoration: the same host lump
  that failed the dispatcher gate was caught in the pump gate (168 B) and in the sweep gate (7,384 B —
  the same "8,192 − k" signature as the counter-read probe) during full-suite runs.
- **Harness assertions inside a measured region are pollution.** `Assert.Equal` allocates (measured
  ~200–300 B per call; a sweep gate's probe loop that asserted inside its own window reported "never
  became allocation-stable" on *every* batch until the assertion moved out), while
  `Assert.True(condition, message)` does not (the UDP gate's measured loop has carried it at 0 B for 100
  runs). Keep every assertion outside the window, or use the boolean form.
- **Every gate states its window contract in code**: assert the driven operation completed synchronously
  (`IsCompletedSuccessfully`) so no continuation can migrate, capture `Environment.CurrentManagedThreadId`
  before the window and assert it unchanged after, keep the exact `Assert.Equal(0, allocated)`, and keep
  the thread-independent call-count backstop. `DispatcherWarmFastPathAllocatesNoManagedBytes` and
  `ReverseRewriteAndInjectAllocatesNoManagedBytes` carry all four; the UDP gate's landed
  readiness/stability preflight plus the same four is the reference shape.
- **The gate's discrimination must be re-proven after any change to its window.** Inject one allocation
  inside the measured region and record the exact failure before restoring: on 2026-09-30 a single
  `new byte[64]` per iteration failed the reverse gate with `Actual: 5632` (64 × 88 B) and the dispatcher
  gate with `Actual: 22528` (256 × 88 B), and both were green again after restoring. **Keep the injected
  allocation alive** (`GC.KeepAlive(new byte[64])`, or a use the optimizer cannot sink): a bare
  `_ = new byte[64]` is dead and was measured *eliminated* on .NET 10.0.401 (2026-10-01 F6 check — two
  runs of `UdpAdaptiveSweepAllocationGateTests` passed with the bare form and failed at `Actual: 88` with
  the `GC.KeepAlive` form; the pump gate read `Actual: 88000`, 1,000 × 88 B). A probe that the JIT
  removes proves nothing about the window.
- **Setup-executor shutdown races settle on both sides** (same window family as the pool drains in §"Native
  pool family"): `SetupExecutor.TryEnqueue` rechecks `_disposed` *after* the ring append and drains on the
  enqueuer's side, and a worker whose semaphore or shutdown source was disposed under it exits like a
  cancelled one instead of faulting its thread unhandled. Measured 2026-09-30 on
  `SetupExecutorDisposeRacingTheFirstEnqueueLeavesNoItemUnsettled` (512 natural attempts, no seam): without
  the worker guard the test host aborted inside the first hundred attempts on 3/3 runs, without the
  post-enqueue recheck 1,997 of 1,998 accepted items were stranded with their completions never settled,
  and with both the test is green over 2,000 attempts.

### 3. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Exact allocation gate run in a host with tiered compilation enabled | report-only: a tiering lump can fail a gate at random; disable tiering for the gate host |
| Gate window opened without the synchronous-completion assertion | forbidden — an incomplete `ValueTask` can resume on another thread and invalidate the per-thread reading |
| Measured thread id changed across the window | gate fails; the reading is invalid, not the path |
| Gate failure message reports bytes but no iteration | acceptable only after a bisected cause; prefer locating the lump per iteration before changing anything |
| Shutdown begins between an enqueue's disposed check and its ring append | the enqueuer's post-enqueue recheck drains the item; the completion settles cancelled, never hangs |
| A worker starts after its shutdown source or semaphore was disposed | the worker exits like a cancelled one; it must not fault its thread unhandled |
| A stability fix that relaxes the byte assertion or skips an iteration | forbidden — the window keeps its exact zero and its call-count backstop |

### 4. Tests Required — the repeat-run procedure

Recorded outcome of this task's own proof runs (tree `0cf25ba` + the fix set, 2026-09-30), so a reader
knows what was actually achieved rather than what was planned:

- **Class filter (`FullyQualifiedName~HotPathAllocationGateTests`): 100/100 consecutive green runs**, each
  with `Total: 11` and exit status 0 — against the recorded pre-fix baseline of 1/25 isolated runs
  (pooled 3/38 = 7.9 %, Wilson 95 % CI [2.7 %, 20.8 %]).
- **Full suite: not fully green in this environment.** On the final host config this task measured 29
  full-suite runs and 4 failed (13.8 %): 3 host lumps inside exact gates —
  `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` 168 B (that one under
  `--blame-hang`, whose collector runs in-host) and 5,216 B, and
  `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` 7,384 B — plus 1 unrelated timing
  flake in `LayeredCaptureRunnerHealthSignalTests` (a refresh-counter assertion, not an allocation
  gate). The affected class produced **zero** full-suite failures in those runs, and the longest
  consecutive green streak observed was 12. The rate is the same order as the pre-fix full-suite rate
  the PRD recorded (2/13 = 15 %), so it is a pre-existing host condition rather than a regression — but
  the **≥40-consecutive-green-suite-run criterion is not met** and must not be reported as met. The
  settled half of the fix is the tiering host contract above; the unsettled half is a per-window control
  that can tell a host lump from a driven allocation — priced and rejected by
  `09-30-exact-gate-residual-lumps` (see its section below), whose replacement is the per-gate process
  proof.
- **Hang hunt (defect B's second half): not reproduced in 47 full-suite runs** under
  `--blame-hang --blame-hang-timeout 90s --blame-hang-dump-type mini --results-directory /tmp/wf-blame`
  — zero hang-shaped runs and zero `Sequence_*.xml` files, so the 95 % upper bound is **p < 3/47 = 6.4 %**
  for a 41-minute-class hang per run (a healthy run is ~15 s under blame). The three non-hang failures in
  those runs were one more host lump (`SweepAllocationGateTests` 7,448 B) and two runs of
  `LayeredCaptureRunnerHealthSignalTests.FailureThresholdForcesARefreshDespiteIdenticalEnumeration`
  (`consecutive` read "0" where the test expects "1", a race between the forced-refresh logging and the
  success hook, not an allocation gate and outside this task's scope).


- `HotPathAllocationGateTests.DispatcherWarmFastPathAllocatesNoManagedBytes` and
  `ReverseRewriteAndInjectAllocatesNoManagedBytes` assert all four window-contract properties from §2.
- `SetupExecutorTests.SetupExecutorDisposeRacingTheFirstEnqueueLeavesNoItemUnsettled` drives the natural
  dispose/enqueue race and asserts every accepted item settles and the refusal path stays explicit.
- **Known gap, no assertion-based regression test for the worker-abort half.** Before the worker guard,
  the observable behaviour is an *unhandled* `ObjectDisposedException` on a `wf-setup-*` thread, which
  aborts the test host instead of failing an assertion — measured on 3/3 runs of the natural race, each
  inside the first hundred attempts. A test cannot assert on a process abort, so the landed test covers
  only the settlement half (which does fail an assertion: 1,997 of 1,998 accepted items stranded without
  the recheck). The guard is therefore covered by its recorded pre-fix evidence, not by a test that
  turns red on removal; treat that as an accepted gap until a host-level observation exists.
- **Stability proof (≥100 consecutive green filter runs, ≥40 consecutive green full-suite runs).** Run
  from the repository root with the tree frozen; every run records its padded summary line, the git
  revision and the process exit status; never `--no-build` (a stale binary greens vacuously) and never a
  discarded stream; a failure stops the loop and returns to diagnosis:
  ```bash
  summary='Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+'
  for i in $(seq 1 100); do
    out=$(dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~HotPathAllocationGateTests' 2>&1); rc=$?
    echo "filter $i $(git rev-parse --short HEAD) rc=$rc $(echo "$out" | rg -o "$summary" | tail -1)"
    [ "$rc" -eq 0 ] || break
    echo "$out" | rg -q 'Total: *11' || break          # a vacuous filter match must not pass
    echo "$out" | rg -q 'Failed: *0,' || break
  done
  ```
  Run the same loop without `--filter` for the ≥40 full-suite runs. The power behind the counts: against
  the recorded 7.9 % (3/38) failure rate, 100 green filter runs and 40 green suite runs are 99.97 % and
  96.28 %; 20 suite runs would be only 80.7 % and are not accepted. The baseline for comparison is the
  pre-fix record (1/25 isolated runs, 2/13 suite runs, pooled 3/38 = 7.9 %, Wilson 95 % CI
  [2.7 %, 20.8 %]).

### 5. Wrong vs Correct

```csharp
// Wrong: an exact window in a tiering host, with no completion or thread check. A tiering lump
// (measured 1,880 B on this gate) or a continuation resuming elsewhere both read as a product leak.
var before = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < count; index++) await dispatcher.DispatchAsync(packets[index], token);
Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

// Correct: the host runs TieredCompilation=false, the window asserts the synchronous fast path and
// the thread identity, and the exact zero and the call-count backstop stay in place.
var measuredThreadId = Environment.CurrentManagedThreadId;
var before = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < count; index++)
{
    var pending = dispatcher.DispatchAsync(packets[index], token);
    Assert.True(pending.IsCompletedSuccessfully, "the allocation gate relies on the synchronous fast path");
    await pending;
}
var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Assert.Equal(measuredThreadId, Environment.CurrentManagedThreadId);
Assert.Equal(0, allocated);
Assert.Equal(1 + 8 + count, executor.PassCount);
```

## The residual exact-gate lump: multiplicity, the attribution limit, and the per-gate proof (diagnosed 2026-09-30, task 09-30-exact-gate-residual-lumps)

### 1. Scope / Trigger

- **Trigger**: with the tiering host contract in place (§"Allocation-gate stability"), the full suite still
  failed about one run in ten with a one-shot lump inside one exact allocation window —
  `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` 168 B / 5,216 B and
  `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` 7,384 B (plus 7,448 B in the hunt) — on
  paths whose only work is polling. The predecessor's follow-up — "a per-window control that can tell a host
  lump from a driven allocation" — is answered here (priced and rejected), and the proof procedure is
  replaced by per-gate process runs.
- **Scope**: the two victim windows, every exact window in the tree by inheritance, the attribution
  instruments, the proof procedure, and the health-signal race that shared the same failure family. **No
  gate threshold, window shape, assertion or warm-up changed.**

### 2. Contracts

- **The residual is not a property of the window bodies (measured 2026-09-30).** A temporary in-assembly
  reproducer (`ResidualLumpProbe`, deleted after the measurement; `NdisCapturePump.RunIterationForTests` is
  `internal`, so only an `InternalsVisibleTo` assembly can drive the window bodies) re-ran the exact window
  bodies with **per-iteration** deltas, no excluded iterations, recording iteration index, bytes, thread id
  and Gen0/1/2 — one process per arm, never concurrently with a suite run:

  | Window body | Thread / co-resident load | Measured | Window lumps |
  |---|---|---:|---:|
  | pump idle iteration | xunit thread | 17,053,000 iterations / 17,053 windows | 0 |
  | pump idle iteration | dedicated `Thread` | 17,000,000 / 17,000 | 0 |
  | pump idle iteration | pool thread, not the test's | 12,693,000 / 12,693 | 0 |
  | pump idle iteration | + 8 allocation loaders | 9,202,000 / 9,202 (9,595 Gen0, 2 Gen1) | 0 |
  | pump idle iteration | + 8 blocking full-GC loaders | 1,026,000 / 1,026 (4,476 Gen0/1/2) | 0 |
  | pump idle iteration | + 4 thread-churn loaders | 16,535,000 / 16,535 | 0 |
  | pump idle iteration with a live on-thread allocation context (8 KB per window) | + 8 blocking full-GC loaders | 466,000 / 466 (3,551 Gen0/1/2) | 0 |
  | flow-table sweep | xunit thread / dedicated `Thread` / pool thread | 53,107 windows (screen + census) | 0 |
  | counter-read-only control (same loop, no product call) | xunit thread | 3,223,621,000 / 3,223,621 windows | 0 |
  | counter-read-only control with a live allocation context | + 8 blocking full-GC loaders | 2,394,000 / 2,394 | 0 |

  Each no-load arm shows exactly **one** nonzero delta at iteration 0 of the census loop (152–272 B) — the
  counter-read-only control reproduces it identically while driving no product code, so it is the census
  loop's own first-transition overhead, not the residual. The arms that re-open the window per iteration
  (`Mark()`) show none at all. A bare console host (`TieredCompilation=false`, public counter read only)
  measured **0 lumps in 148,525,260 counter-read iterations**. The thread-identity, GC-count and
  per-window fields are in the raw census lines.
- **Multiplicity census (27 processes, one arm per process, 12 s each).** 6,014,442,625 iterations over
  6,061,021 windows; the only nonzero deltas in the whole run are the fifteen iteration-0 transition
  artifacts above (one per pump/control process, all 152 B, all control-reproduced). Per arm: pump window
  6 × (10.2 M iterations / 10.2 k windows), dedicated 3 × (10.2 M), pool 3 × (10.3 M), sweep 6 + 3 + 3
  processes (46,625 windows), counter-read-only control 3 × (1.96–1.97 billion iterations / 1.96 M
  windows).
  Combined with the screen above: **0 lumps in 196,774 pump windows and 53,107 sweep windows; 0 in
  11,509,218 control windows.**
- **The pool arm needs `ThreadPool.UnsafeQueueUserWorkItem`, not `Task.Run(...).GetAwaiter().GetResult()`.**
  The xunit test thread is itself a pool thread, so the pool may execute the task inline on it — the first
  run of that arm silently measured the test thread again (`thread == testThread`). Check the thread id in
  any future arm.
- **Multiplicity.** In isolation the residual's measured count is **0 lumps per process** (27 arm
  processes, >200,000 window-body windows; Wilson 95 % upper bound 12.5 % per process) and **0 per window**
  (0 in 249,881 window-body windows; Wilson 95 % upper bound 1.6 × 10⁻⁵ per window). In the suite's own
  captures every lump failure names **one gate and one size** — 168 / 5,216 / 7,384 / 7,448 B in the 29-run
  proof and the 47-run hunt — and no run shows two failing gates. "Once per process, random iteration" is
  the **tiering** control's signature and stays in that family.
- **The event is suite-process-conditional, not window-conditional.** Spread over the gates' exact
  windows, the recorded ~10 % per-run lump rate implies a per-window rate of order 10⁻², at which the
  isolated census would have fired hundreds to thousands of times; zero were observed. Whatever triggers the
  residual needs the co-resident suite process (≈980 sibling tests, their threads, their JIT/loader
  activity), not the window body — which is why per-gate isolation changes *attribution, not incidence*, and
  cannot place a window after the event.
- **No allocator can be named with the available instrument (recorded, not asserted).** `dotnet-trace
  collect --profile gc-verbose` was installed for the purpose, and **what it guarantees to observe was
  written down before it ran**: allocation sampling is **budget-based** (a thread's allocation context is
  sampled roughly once per ~100 KB that thread allocates), so a sparse ≤8 KB one-shot on a thread that
  allocates nothing else is never sampled. Measured: the positive control (an 8 KB class allocated every
  iteration) produced **1,559 samples naming the type**, while the same class allocated **once** produced
  **zero** samples — although the per-thread counter read its 8,280 B lump. A null capture therefore means
  "the instrument could not see it", never "nothing allocated". `dotnet-gcdump` is weaker still: it forces a
  collection, so a transient lump is gone before the dump.
- **The per-window control is priced and rejected — the `:878-881` follow-up is closed.** A control window
  shaped like a gate window (same counter reads, no product call) cannot separate a random host lump from a
  driven allocation: the lump lands in exactly one of the two windows, so their difference is nonzero
  whichever cause it had. A **co-resident** control is worse — the residual fires at most once per suite
  process, so the control would absorb the event and mask the gates it is meant to calibrate. Every control
  above therefore runs in its own process, never with a suite run.
- **The `:940` forbidden row stands, and the honest injected-allocation check is what keeps it.** No
  tolerant shape was adopted — the gates keep `Assert.Equal(0, allocated)` and their call-count backstop.
  min-of-K and "at most one of K windows nonzero" tolerate exactly the class under diagnosis (a lump in one
  window) and are refuted by that check; min-of-K additionally loses three to four orders of magnitude of
  power for a 1-in-5,000-iteration regression (0.02 % against 18 %). Re-proven on this task's tree: one
  `new byte[64]` per measured iteration → pump gate `Actual: 88000` (1,000 × 88 B), one `new byte[64]`
  inside the single sweep window → sweep gate `Actual: 88`; both restored → green. (F6 check,
  2026-10-01: the injected allocation must be kept alive — see the probe-form contract in §2; the
  figures above reproduce with `GC.KeepAlive(new byte[64])`.) A gate shape that cannot
  fail that check is not a gate.
- **Disposition: no product fix, no gate change, per-gate proof.** The counter-read-only control **of the
  residual itself** (tiering off, suite process) could not be run — the event never appears in an isolated
  process, and a co-resident control masks the gates — so **the product is not excluded by a control of the
  residual**; that limitation is recorded rather than smoothed over. What the evidence supports is the
  operational proof: run each exact gate in its own process, record the measured per-gate residual rate, and
  keep the **suite-level rate as an accepted host property** — allocation lumps 3/29 = 10.3 %, Wilson 95 %
  [3.6 %, 26.4 %]; 4/29 = 13.8 % [5.5 %, 30.6 %] including the health-signal race this task fixed; the
  47-run `--blame-hang` hunt adds 1/47 = 2.1 % [0.4 %, 11.1 %]; pooled 4/76 = 5.3 % [2.1 %, 12.8 %].
- **Sensitivity statement.** The disposition does not lower regression sensitivity — same exact window, same
  N — but it changes **what is proven**: per-gate process runs sample a process running one gate instead of
  a co-resident suite, so the suite-conditional trigger above is not exercised by the proof. The regression
  floor of an N-run proof over a 1,000-iteration window is `1 − (1 − 1,000/K)^N`: N = 20 gives
  K = 5,000 → 98.8 %, K = 10,000 → 87.8 %, K = 20,000 → 64.2 %, K = 50,000 → 33.2 %, and a regression
  rarer than ~1 allocation per 50,000 iterations is no longer reliably caught. That floor is intrinsic to
  the run count, not a consequence of per-gate isolation.
- **An accepted host property, not a stability guarantee.** The finished record claims **no rate threshold**.
  A per-gate failure counts as the accepted host event only when the checkable signature holds: the gate is
  a recorded victim, the delta equals one of 168 / 5,216 / 7,384 / 7,448 B, the run passed the gate's own
  `Assert.True(stabilized)` preflight, and the injected-allocation check still fails when re-run. Any other
  failure is an incomplete diagnosis, not an accepted event.
- **Do not re-test** (this task's negative results): thread context (xunit thread / dedicated `Thread` / a
  genuinely other pool thread), allocation pressure, blocking full collections, thread churn, a live
  allocation context on the measured thread, the counter-read-only control in the suite host and in a bare
  console host, and profiler-based attribution of a ≤8 KB one-shot. The tiering, `gcConcurrent`, OSR-only,
  forced-collection, diagnostics-server and warm-up families stay excluded by the predecessor's section.

### 3. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Exact gate fails in the suite with a nonzero delta | classify: recorded victim gate + the gate's clean `stabilized` preflight + the injected check still failing → accepted host event; anything else stops the work |
| Exact gate fails in its own process (per-gate proof) | same classification; the **measured** per-gate rate is recorded, never a threshold |
| A proposed gate shape tolerates one nonzero window | forbidden — one injected allocation inside one window must still fail it |
| A reproducer runs concurrently with a suite run | forbidden — it absorbs the once-per-suite-process event and masks the gates |
| A profiler null capture over a window body | not evidence of "nothing allocated": budget-based sampling cannot see a ≤8 KB one-shot on a non-allocating thread |
| A gate failure reports bytes with no per-iteration location | acceptable only for the recorded signature sizes; prefer per-iteration deltas before changing anything |

### 4. Tests Required — the per-gate proof procedure

The suite-level loop is **not** the criterion. Run each exact gate in its own process, N runs per gate,
record every run's padded summary, its totals assertion (the suite total is `1,141 + 18` on the
2026-10-01 F6 tree, up from `995 + 18` at the 09-30-expiry-sweep-bounded-pause tree), the git hash, the
tree fingerprint and the exit status; a failure is accepted only under the signature predicate above.
**Qualify the filter with the namespace** (`FullyQualifiedName~WinForward.Core.Tests.$gate`): a bare
class-name substring is no longer unique — `~SweepAllocationGateTests` also matches
`UdpAdaptiveSweepAllocationGateTests` and reads `Total: 13` instead of 12, so the loop below stops on a
false `VACUOUS MATCH` (measured 2026-10-01 during the F6 check; the namespace-qualified filter reads 12):

```bash
log=/tmp/wf-lumps-proof.txt; : > "$log"; rev=$(git rev-parse --short HEAD); tree=$(git write-tree)
summary='Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+'
totals='HotPathAllocationGateTests:11 CapturePumpReadCallTests:4 SweepAllocationGateTests:12 NdisCapturePumpTests:14 NdisCapturePumpIdleWaitTests:5 FlowAttributionPipelineTests:19 FlowAttributionPendingIndexTests:10 ProcessOwnerTableCacheTests:11 CompositePacketArrivalSignalTests:3 UdpAdaptiveSweepAllocationGateTests:1'
signature='^(168|5216|7384|7448)$'
for entry in $totals; do
  gate=${entry%%:*}; expected=${entry##*:}
  for i in $(seq 1 20); do
    out=$(dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WinForward.Core.Tests.$gate" 2>&1); rc=$?
    line=$(echo "$out" | rg -o "$summary" | tail -1)
    actual=$(echo "$out" | rg -o 'Actual: *[0-9]+' | tail -1 | rg -o '[0-9]+')
    echo "gate $gate run $i $rev $tree rc=$rc total=$expected $line actual=${actual:-none}" >> "$log"
    echo "$out" | rg -q "Total: *$expected" || { echo "VACUOUS MATCH: $gate run $i"; break; }
    if [ "$rc" -ne 0 ]; then
      if echo "${actual:-x}" | rg -q "$signature"; then echo "accepted host hit: $gate run $i ($actual B)"; else echo "UNEXPLAINED FAILURE: $gate run $i"; break; fi
    fi
  done
done
```

### 5. Wrong vs Correct

```csharp
// Wrong: "prove" stability by re-running the suite until it is green. Each run carries the
// suite-conditional host event, the green streak is luck, and a failure is re-run away.
// Wrong: tolerate one nonzero window (min-of-K, "at most one nonzero") — that is exactly the
// class under diagnosis, and the injected-allocation check fails this shape by construction.
// Wrong: co-resident control windows — they absorb the once-per-suite-process event.

// Correct: no gate change; classify by the checkable signature and measure the rate.
//   per-gate process run + recorded padded summary + totals + hash + fingerprint
//   + injected-allocation check re-run on the frozen tree
// The gates keep the exact zero; only the proof procedure and what it proves changed.
```

## Warm-path lock-free resolve, the activity bucket and the self-traffic split (task 09-30-warm-path-lock-chain, 2026-09-30)

### 1. Scope / Trigger

Trigger: any change to the warm resolve of a flow decision, the TCP reverse/original probes, the UDP
ready path, the activity stamp/expiry representation, the self-traffic guard, or the composition's
per-iteration callback. This is finding F2 of the archived `09-29-tcp-udp-path-structural-perf`
research, landed after F3's bounded-hold sweeps.

### 2. Contracts

- **One activity bucket, 500 ms, one clock per composition.** `ActivityBucket.TicksPerBucket` is 500 ms
  (the binding half of F3 item 4 is "≥8 buckets per retention window"; the 5 s session floor makes the
  old "≥1 s" half unsatisfiable). `ActivityBucketClock` is created **once** in `DurableCaptureBundle`,
  threaded into the `FlowDispatcher`'s `FlowTable`, the `TcpProxyCoordinator` and the
  `UdpProxyCoordinator`/`UdpProxySession`, and advanced by three sources: the pump's per-iteration
  callback (`DurableCaptureBundle.FlushPendingInjections`, the only `OnBatchCompleted` chain point),
  every claim (`FlowState.Reset` via `Tick()`) and every sweep (`RemoveExpired` publishes the instant
  it was handed, so the cutoff and the stamps cannot drift inside a call). A missed wiring freezes the
  bucket and expires active flows — pinned by
  `DurableCaptureBundleTests.ActivityBucketClockTicksExactlyOncePerPumpIteration` (one tick per pump
  iteration, empty polls included, zero per packet) and by the throwing-clock facts.
- **No clock read on the warm path.** A hit stores/derives the bucket: `FlowState.TouchBucket`,
  `TcpRedirectAssociation.Touch` and `UdpProxySession.TouchActivity` write one integer each; the TCP
  coordinator's per-packet instant is `ActivityBucket.ToUtc(clock.Current)` and the UDP ready path reads
  none. The tombstone / setup-cooldown / pending-SYN / setup-queue stamps keep the real clock (their
  windows are not bucket comparisons).
- **Never early.** Every sweep compares the **internal integer bucket** with a strict `<` cutoff
  (`ActivityBucket.Cutoff(now, idleTimeout)`), so retirement lands in `(idleTimeout, idleTimeout + w]`;
  `idleTimeout <= TimeSpan.Zero` keeps "retire everything on this call". `LastActivityUtc` is derived
  and only for logs/tests.
- **Warm resolves take no global gate.** `FlowTable.TryResolveWarm` (one volatile slot read + the
  seqlock snapshot + tuple corroboration), `TcpRedirectTable.TryResolveByReverse`/`TryResolveByOriginal`
  (direct-mapped caches validated by immutable association fields, falling back to the gated authority),
  `ISelfTrafficGuard.IsWildcardOwned` (`ConcurrentDictionary`) and the UDP ready path's session cache are
  lock-free; a cache miss is always a fall-back to the unchanged gated path, never an approximation.
  **F4 (2026-09-30)** keeps this contract while the key and the corroboration tuple became packed: the
  slot function and `Matches` read packed fields (no `Endpoint` materialization), and the adapter slot
  the key now carries is interned at classification, never resolved on a warm probe — the packed
  re-proof is the warm-cache bullet above, with `FlowKey.IsReverseOf` serving the dispatcher's
  endpoint-swap test without materializing four `Endpoint`s.
- **TCP pays one reverse probe and no redirect gate on a warm packet.** `IsReverseCandidate` is gone:
  the coordinator resolves once via `TryResolveByReverse` and passes the association into
  `HandleReverseAsync`, which no longer probes. `HandlePacketAsync` keeps the listener-port prefilter in
  front of the probe (a reverse tuple's source port is always a live listener port, so a miss proves
  absence) — that is what makes a cache-resident warm forward packet cost zero redirect gate entries.
  The prefilter is a candidate filter, not a forward-direction absence proof: client source ports and
  listener ports are both OS-assigned ephemerals, so a forward flow whose source port number is itself a
  live listener port pays one gated reverse probe per packet for as long as that listener exists (one
  gate entry fewer than the pre-change candidate+resolve pair) and then falls through to the original
  index. A cache collision costs the same one gated probe on either direction.
- **UDP ready path is wait-free and gate-free.** `TrySendSpanAsync` resolves the ready session from the
  pre-allocated cache (validated by `Session.Flow`), else takes the admission path, which keeps the
  cooldown probe, the clock read, the capacity check and the re-check of `slot.Ready` under `_gate`.
  `UdpProxySession.SendSpanAsync`/`TouchActivity` no longer take `_activityGate`: `_scope.TryEnter` is
  the admission authority and the scope's drain joins outstanding leases. The propagation sentinel is
  `long.MinValue` (bucket 0 is a real bucket), and `TryBeginExpiry` re-checks in bucket space under the
  gate it still holds.
- **The self-traffic wildcard half stays on the warm entry.** Loop prevention is fail-closed: a relay
  control socket registers `(protocol, Any:port, remote)` before its SYN, so the warm entry must answer
  that half. The exact-tuple half runs once per claim and a claimed state is its "proven not self"
  record; the accepted delta (an exact registration after the claim keeps proxying while traffic flows)
  is recorded in the task's design §4/§7. **F8 (2026-10-01) restates the half's frequency**: a flow whose
  attribution is pending has no claimed state yet, so each of its packets re-enters the full slow path —
  including the gated exact-tuple check — once per packet instead of once per claim. That is bounded by
  the pending ring (32 packets) and the retention TTL (5 s), counted by the pipeline's delivered-packet
  counter, and it is the recorded price of keeping the claim on the pump; an admission-side
  short-circuit is a recorded non-goal (`FlowAttributionPipelineTests`).
- **Counts are the proof.** Per warm hit: registry gate 1 → 0, exact-tuple guard probes 1 → 0, flow-table
  gate 1 → 0, redirect gate 2 → 0 (one reverse probe), UDP coordinator gate 1 → 0, session activity-gate
  entries 2 → 0, clock reads 2 → 0. The parked-gate facts
  (`WarmResolveCompletesWhileFlowTableGateIsHeld`, `ReverseResolveCompletesWhileRedirectGateIsHeld`,
  `UdpReadySendCompletesWhileCoordinatorGateIsHeld`) prove the removal structurally; every count and its
  before/after text is in `benchmarks/results/2026-09-30-warm-path-lock-chain/warm-path-gate-counts.txt`.

### 3. Wrong vs Correct

```csharp
// Wrong: read the flow table's pooled instance outside the gate (a recycled state can be observed),
// or leave the whole self-traffic check off the warm entry (a recycled ephemeral port then resolves a
// stale proxy state and redirects WinForward's own relay connection into its own proxy).
if (_selfTraffic.IsOwned(packet.Context)) return DispatchSlowAsync(packet, cancellationToken);
if (!_flows.TryResolve(packet.Context.Key, out var state)) return DispatchSlowAsync(packet, cancellationToken);

// Correct: the warm entry answers the wildcard half lock-free and reads a validated view; the exact
// half and the full check run once per claim.
if (_selfTraffic.IsWildcardOwned(packet.Context)) return DispatchSlowAsync(packet, cancellationToken);
if (!_flows.TryResolveWarm(packet.Context.Key, out var existing)) return DispatchSlowAsync(packet, cancellationToken);

// Wrong: compare activity in DateTimeOffset space on a per-entry basis, or with `<=`
// (retires up to one bucket early).
if (now - association.LastActivityUtc >= idleTimeout) Remove(association);

// Correct: one bucket cutoff per call, strict `<` on the internal integer bucket.
var cutoffBucket = ActivityBucket.Cutoff(now, idleTimeout);
if (association.BucketForDiagnostics < cutoffBucket) RemoveUnderGate(association);
```

## F4 flow identity, parse-once and slim context — spec-row → proof map (task 09-30-flow-key-parse-once, 2026-09-30)

Task record: `.trellis/tasks/09-30-flow-key-parse-once/`; evidence:
`benchmarks/results/2026-09-30-flow-key-parse-once/` (`README.md`, `walk-and-gate-counts.txt`,
`defaulted-layout-hardening.txt`, `step6-ipv6-delta-verdict.txt`, `class-totals.txt`,
`gate-stability.txt`). Every row below is a binding row in this spec directory that F4 changed,
with the fact that discharges it. All claims are exact counts/sizes except the AC-4 series, which is
a **recorded reading** (per-leg no-regression; the improvement figure and the IPv6:IPv4 ratio are
readings, never thresholds — contract 9).

| Spec row | After F4 | Proof |
|---|---|---|
| this file, contract 4 | `PacketLayout` on the packet; sizes `FlowKey` 64 / `FlowContext` 80 / `CapturedFlowPacket` 152 / `FlowStateView` 96 | `FlowKeyShapeTests.StructSizesForDiagnostics` |
| this file, contract 8 | packed key with no reference-typed field; shared `FlowHash.CombinePacked` | `FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields`, `FlowKeyShapeTests.StructSizesForDiagnostics` (exact 64), `FlowKeyPackedRoundTripsEndpoints`, `ReverseSwapsEndpointsAndScopes`, `PackedAndMaterializedHashesAgree`, `PackedAndMaterializedCanonicalHashesAgree`, `CanonicalSlotIsOrderIndependent` |
| this file, contract 10 | one parse per redirected packet; every layout consumer reads the layout | `RedirectedForwardPacketWalksHeadersExactlyOnce`, `RedirectedReversePacketWalksHeadersExactlyOnce` (red-before 4/4 per leg), `LayoutSynTestMatchesTheSpanTest`, `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects`, `SequenceAdvanceIgnoresEthernetPadding`, `ExtensionHeaderFramesProduceTheSameAdvance`, `PacketLayoutFitsSixteenBytes` |
| this file, contract 11 | a defaulted layout is refused byte-identically by every consumer | `OnlyAParsedFrameYieldsAValidLayout`, `DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten`, `DefaultedLayoutObservesNoSequence`, `RedirectLegRefusesADefaultedLayoutByteIdentically`, `EveryDispatchedFlowPacketCarriesAParsedLayout` |
| this file, contract 12 | CAS-max `long` trackers, no gate and no lock on the association | `RedirectPacketTakesZeroSequenceGateEntries` (red 2), `TcpRedirectAssociationHoldsNoLockField`, `ConcurrentSequenceObservationsKeepTheLargerValue`, `UnobservedTrackerReadsNullAndObservedZeroReadsZero` |
| this file, F2 warm cache + "warm resolves take no global gate" | packed slot function and packed 48 B `TransportTuple`; no `Endpoint` materialization on a warm probe | the two agreement facts + `FlowTableTransportTupleIsUniqueAcrossOrigins` / `WarmCacheHitServesTheValidatedView` (forward and reverse corroboration) + the F2 facts re-run unchanged (`FlowTableWarmResolveAllocatesNoManagedBytes` among them). The planned `TransportTupleEqualsTheKeysTransportFields` was not written: `TransportTuple` is a private nested type, so a direct field-set fact would need a visibility change — recorded residual |
| this file, "Closure-hoisting and allocation-gate" → Tests Required | per-packet 0 B gates unchanged; the layout refactor surfaced one gate that had been passing on a mis-rewrite | `HotPathAllocationGateTests` 11/11 (unchanged), `class-totals.txt`, `defaulted-layout-hardening.txt` |
| `quality-guidelines.md` § Current Conventions (rewrite primitive) | layout overload keeps `IsTcp`, `TransportEnd` and family equality only; span entry point is the oracle | `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects`, `DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten` |
| `quality-guidelines.md` (hard invariant linkages) | one `FlowHash.CombinePacked` behind both entry points and both packed callers | `PackedAndMaterializedHashesAgree`, `PackedAndMaterializedCanonicalHashesAgree` |
| `quality-guidelines.md` § Testing Requirements (rewrite tests) | the oracle/mutable-offset discipline extends to the layout overload and the defaulted layout | the two rewriter facts above |
| `tcp-local-redirect.md` (TFO SYN / sequence tracking) | layout-driven SYN and advance predicates; CAS-max trackers | the contract-10/12 rows above + `SynWithPayloadIsRedirectedLikeBareSyn`, `RetransmittedSynWithPayloadReusesAssociation` (unchanged) |
| `windows-ndisapi.md` (adapter identity) | interned monotone slot table; an adapter that cannot be interned is refused, never keyed | `AdapterSlotTableRoundTripsStableId`, `TryInternIsIdempotentPerStableIdAndSlotsAreNeverReused`, `AdapterSlotSurvivesARefresh`, `AnAdapterThatCannotBeInternedIsRefusedNotAliased`, `FlowKeyEqualityKeepsTheGeneration` |
| `udp-relay.md` (host-flow response adapter binding + Required tests) | `Resolve(ushort slot)` over a slot-indexed array; `AdapterIds` resolves through the slot table | `DurableCaptureBundleTests.ScopeHeadBecomesHostFallbackAndEachAdapterResolves` (slot → target over the production `UpdateUdpTargets` path), `UdpAdapterTargetSourceTests.AdapterIdsResolveThroughTheSlotTable`, `ResolveReadsTheLatestSnapshotAfterUpdateWithoutReconstruction`, and the three unchanged response-target tests |
| `traffic-policy-lifecycle.md` (policy domains) | the same five properties, backed by interned metadata + the packed key | policy suites unchanged; `FlowContextMetadataTests` |

**Accepted semantic deltas** (recorded, not smoothed over): `FlowContext`/`CapturedFlowPacket` are
record structs whose generated equality now compares metadata references instead of four strings
(nothing in `src/` compares either by equality; the metadata shapes are records, so equal triples
still compare equal); `FlowContext.RemotePort` is the key's remote port, so the three construction
sites that used to pass it explicitly no longer can disagree with the key;
`FlowKey.Create(…, AdapterSlotTable, …)` on an unregistered adapter yields `NoSlot`, which only cold
edges and tests reach — capture-scope adapters are interned at generation build and refused there.

**Residuals** (verified, not smoothed over): the non-flow arm carries `default(PacketLayout)` by design
and no consumer reads it; the Windows composition-level slot refusal has no Linux harness — only the
table-level refusal is pinned (`AnAdapterThatCannotBeInternedIsRefusedNotAliased`), while the factory's
scope-exclusion/log loop is untested; `ProcessMetadata` is allocated once per attributed claim and no
0 B gate drives that path; the design's by-slot `AdapterSlotTable.Observe` seam was removed as unused (the refresh
runs through `TryIntern`); `TransportTuple`'s per-field equality has no direct fact (it is a private nested type), so
the warm corroboration is pinned observably; `PacketView`'s constructor is public, so `PacketLayout.From`
stamps a hand-built view too — the validity stamp excludes `default`, it does not authenticate a parse;
the companion benchmark series are shape witnesses (their exact siblings are the gate classes, green in
every run).

## The owner-table epoch coalescer (F8, 2026-10-01)

### 1. Scope / Trigger

Any change to `ProcessOwnerTableCache`, `IProcessOwnerTableReader`/`IPHelperOwnerTableReader`,
`WindowsProcessAttributor.FindAsync`, or the deferred-attribution pipeline's owner-table reads.

### 2. Contracts

- **One snapshot slot and one single-flight refresh gate per `OwnerTableKind`** (Tcp4, Tcp6, Udp4,
  Udp6). Concurrent missers join one in-flight read whose snapshot is published after the last of
  them asked, so a burst's own newly-bound sockets are visible to the shared read. A window alone
  cannot deliver that: a socket's row appears at bind, microseconds before the packet that triggers
  the lookup, so it can never be in a snapshot taken before the burst. The recorded series is the
  `attribution.ownerBurst` row — 16 concurrent flows over one scripted table cost **1** read — and
  the exact form is `ProcessOwnerTableCacheTests.NConcurrentMissesInsideTheWindowReadTheTableExactlyOnce`.
  The scan count is a **series, never a threshold**.
- **The freshness rule is the request instant, not a flag.** A snapshot taken before the caller asked
  may only answer a **positive TCP row**; a miss always falls through to a read, so a flow whose
  socket bound after the last read still gets a real scan (`WinForwardProcessAttributor.FindAsync`'s
  2 ms retry is just a lookup with a later instant, which is what lets it coalesce onto an epoch that
  started after it asked instead of forcing a second scan).
- **Positive caching is per kind, and the bound is the predicate's.** A TCP row matches all four tuple
  fields, so a row that survives into a later request describes the same connection; an exact-4-tuple
  reuse inside the 300 ms window is effectively impossible under TIME_WAIT, so TCP reuse is accepted.
  A UDP row matches the **local port alone**, so a recycled port inside the window would attribute a
  flow to the previous process (fail-open) — **UDP never reuses a snapshot older than the request**,
  only coalescing onto an epoch published at or after it. Closing the UDP half needs the
  `*_TABLE_OWNER_MODULE` creation timestamp, which is a recorded on-Windows follow-up.
- **The seam is the platform boundary.** `IPHelperOwnerTableReader` is the single
  `[SupportedOSPlatform("windows")]` type and owns the size probe, the fail-closed row-count
  validation and the `FreeHGlobal`. The default reader off-Windows is
  `UnavailableOwnerTableReader`, so the observable result there stays `null` while the managed cache
  logic is exercisable on any host through an injected reader. The predicates moved into
  `OwnerTable` unchanged and the `Where/Select/Distinct/ToArray` became one predicate pass, so a
  snapshot hit and a fresh scan answer identically for identical rows.
- **The wake signal is latency, never correctness, and its ownership is split three ways.**
  `CompositePacketArrivalSignal` composes the **borrowed** driver signal with one event the
  `FlowAttributionWakeRegistry` owns: the composite disposes the driver signal in place of the list
  owner (so the driver registration is still released exactly once), the registry never touches a
  composite, and the registry keeps **one event per adapter handle** — reused across generations —
  which `DurableCaptureBundle` disposes after the pumps have stopped and the pipeline is sealed
  (a disposed event is reported as "not signalled", so a racing `Wait`/`Signal` cannot throw out of
  the idle path). A refused driver registration registers nothing and the pipeline's signal becomes
  a no-op: delivery then waits at most the poll delay or the idle bound, which the facts pin
  (`CompositePacketArrivalSignalTests`, `FlowAttributionPipelineTests.ThePipelineSignalsOnlyItsOwnAdaptersEvent`).

### 3. Tests Required

`ProcessOwnerTableCacheTests`: coalescing, the retry joining a later epoch, a post-read bind forcing
exactly one more read, a cached TCP hit, UDP never reusing, window expiry, scan/snapshot agreement,
the recycled-port asymmetry, `window = 0`, and the unavailable reader.

### 4. Wrong vs Correct

```csharp
// Wrong: serve a UDP answer from a snapshot taken before the request — the predicate is the local
// port alone, so a recycled port attributes the flow to the previous process (fail-open).
return snapshot.Table.Lookup(key);

// Correct: only a positive TCP row may be reused; a UDP lookup coalesces onto an epoch published at
// or after its request instant and otherwise reads.
if (snapshot is not null && ReusesSnapshot(kind) && snapshot.IsUsable && IsFresh(snapshot, now) && snapshot.TakenUtc <= requestInstant
    && snapshot.Table.Lookup(key) is { } cached)
{
    return cached;
}
```
