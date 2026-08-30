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
   `Endpoint` stores `IPAddressValue` by value.
2. **IPv4 masks stay inside the low 32 bits.** `IPPrefix.PrefixMask(prefixLength, family)`:
   IPv4 = `0xFFFFFFFF << (32 - len)` (/0→0, /32→0xFFFFFFFF); IPv6 = left-aligned 128-bit.
   A left-aligned mask over low-32 IPv4 bits matches everything — regression-locked by
   `IPv4PrefixesMatchOnlyTheirPrefix`.
3. **No async state machines on the steady-state path.** A fat async method (large struct
   locals hoisted into the state machine) heap-allocates per call even when it completes
   synchronously (~193 B/op measured). `FlowDispatcher.DispatchAsync` is a non-async entry
   that runs the synchronous warm shape (trace-off ∧ no reverse handler ∧ not self-owned ∧
   resolved ∧ (Pass ∨ Block ∨ Proxy-with-inline-server-hit)) and returns the executor's
   ValueTask directly; everything else falls into `DispatchSlowAsync`. Proxy is the product's
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
- `BoundedSetupQueue.TryEnqueue/TryDequeue` (`WinForward.Core/PacketRuntime.cs`) keeps a
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
