# UDP Datagram Path: the per-datagram send, the counters, and the recorded anchors

> The SOCKS5 UDP path end to end: encode and send, the disposal guard, the per-flow exchange
> evidence, and the anchors that keep the shape honest. Part of the [hot-path family](./hot-path.md);
> read it when you change the established-datagram path or a recorded UDP anchor. Ownership is
> [udp-association-ownership.md](./udp-association-ownership.md), "One flow, one association, one
> relay socket".

## The socket send

- The sending socket is non-blocking (`Blocking = false`; .NET 10 renamed `NonBlocking`), and the
  warm shape is an inline sync `SendTo(span)`. On Windows this removes a per-datagram
  IOCP→thread-pool hop; on Linux the sync send was already the fast path.
- Any `SocketException` falls back to the overlapped async send, which parks on a full kernel queue
  instead of busy-failing. Every gate/buffer-release path must release exactly once per call.
- Datagram encode uses `TryEncode(..., Span<byte>, out written)` into the transport's reusable
  `_sendBuffer`; allocating overloads exist for tests only.

## The disposal guard is outside the warm shape

- `Socks5UdpTransport.DisposeAsync` claims a one-shot `int` (`Interlocked.Exchange`) and sets it
  **before** `_socket.Dispose()`; `SendSpanAsync` refuses with `ObjectDisposedException` on a plain
  `Volatile.Read(ref _disposed) != 0` taken *before* `_sendGate.WaitAsync`. That is one volatile read
  plus one branch — 0 B, no CTS, no closure, no `Run`, no state machine — which is what keeps
  `WarmSyncSendAllocatesNoManagedBytes`, `SendSpanAsyncWarmPathRunsNoAsyncStateMachine` and
  `EstablishedUdpDatagramPathAllocatesNoManagedBytes` green.
- **The send gate is deliberately not disposed** (task 10-06-uot-per-flow-transport, 2026-10-06).
  `SemaphoreSlim.Dispose` frees only the lazily created `WaitHandle` (never requested here) and sets
  the disposed flag; disposing it while waiters are parked leaves those waits permanently incomplete,
  so a sender preempted between the guard read and `WaitAsync` would hang forever holding its caller's
  work lease, and the session's teardown drain would then wait on it.
- The guard's post-wait half closes the window this note used to accept: a sender that acquires the
  gate after teardown is refused there (`ObjectDisposedException`, or the transport's recorded typed
  fault) instead of writing to a closed socket. All three transports (`Socks5UdpTransport`,
  `Socks5UotTransport`, `LocalUdpTransport`) share the contract; each carries a racing-disposal test
  with a bounded wait (`…NeverObserveTheDisposedGate` for the two SOCKS5 transports,
  `…NeverStrandOnTheGate` for the local one) so a regression fails instead of hanging the run.

## The established-datagram path has three allocation-free operations

- (1) One volatile read of the association's fault state (a null check), so a flow whose control
  stream ended refuses the datagram before the send gate and before the socket (I4).
- (2) One `Interlocked.Increment` on the transport's own send counter, recorded only **after** the
  kernel accepted the datagram — every send shape (warm sync `SendTo`, the overlapped async fallback
  and the contended-gate tail) calls it exactly once.
- (3) On the receive side, a write-once response flag: the first successfully decoded relay datagram
  pays one `Interlocked.Exchange`, and a volatile read short-circuits every later response.
- The relay target is one `SocketAddress` the association serialized once at connect time and the
  transport cached at construction, so no send re-serializes an endpoint, compares a publication, or
  rebinds a registration. No allocation, no state machine. (The fresh-`EndPoint` regression that cost
  72 B per relay datagram is recorded in
  [native-lease-and-pool-lifetime.md](./native-lease-and-pool-lifetime.md).)
- **Nothing is recorded per skipped datagram.** `UnexpectedSource`, `Oversized`, `Malformed` and
  `ConnectionReset` return before `RecordResponseReceived()`, so a skip is an anomaly, never evidence
  that a server answered — the one-shot retention class depends on that distinction.

```csharp
// Wrong: record the send before the kernel accepted it — the one-shot retention class then counts
// datagrams that never left the host.
_ = _socket.SendTo(...);
RecordDatagramSent();

// Correct: one interlocked increment after acceptance; nothing at all on the skip paths.
_ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, _relaySocketAddress);
RecordDatagramSent();
```

## Counters and one-shot events

- `Socks5UdpTransport` implements `IUdpExchangeCounters` over its own two fields: `DatagramsSent`
  (one `Interlocked.Increment` per accepted send) and `SawResponse` (a `Volatile.Read` short-circuit
  plus a write-once `Interlocked.Exchange`); `SendSpanAsync`/`ReceiveAsync` call
  `RecordDatagramSent()`/`RecordResponseReceived()` — the transport's only per-datagram accounting
  calls.
- `RuntimeCounters`: `UdpCapacityRejections`, `UdpSetupFailures`, `UdpAssociationLost`, alongside the
  pre-existing `UdpSetupRejections`/`UdpSetupBudgetRejections`.
  - `udpCapacityRejections` counts each datagram refused at the session-capacity gate; the
    accompanying `udp.session.capacity-block` warn is rate-limited to 5 s.
  - `udpSetupFailures` counts each genuine setup failure — dial, ASSOCIATE, or session construction —
    and is exactly the set that arms the 1 s setup cooldown (shutdown cancellation and
    `UdpAssociationLostException` are deliberately not counted here).
  - `udpAssociationLost` counts each send failure classified as `UdpTeardownReason.AssociationLost` —
    with one flow per association that is one increment for the flow whose next send found its
    association dead (both the ready path and the setup-queue flush map through
    `UdpProxyCoordinator.TeardownReasonFor`), while the rate-limited `udp.association.lost` warn
    reports the death itself. An association that dies while its flow is idle moves no counter:
    nothing sends, so nothing is classified.
- **The counters are observational only** and never influence packet disposition, fail-closed,
  recovery or shutdown decisions (the `RuntimeCounters` contract).

## Transport defaults and the setup queue

- `Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize = 64 * 1024`;
  `ConfigurationLoader.DefaultUdpRelayReceiveBufferKb = 64`;
  `ConfigurationLoader.DefaultUdpSessionIdleTimeout = 30 s`, with
  `UdpProxyCoordinator.OneShotIdleTimeout = 5 s` as the retention of a completed one-shot exchange and
  the UDP tick derived from the effective floor (5 s at the defaults).
- `BoundedSetupQueue.TryEnqueue`/`TryDequeue` (`WinForward.Core/BoundedSetupQueue.cs`) keeps a
  single-slot fast path (≤1 buffered datagram skips the `Queue<>` object + array). Overflow is
  drop-oldest, freshest-wins.

## The recorded setup anchors (per-flow shape)

- **Noop probe budget: `UdpSessionBenchmarks` Noop probe ≤5,400 B/session marginal** (1→1000 sweep;
  measured 5,161.8 B, spread 14.9 B; re-anchored 2026-09-22 by 09-22-udp-teardown-session-tier-alloc
  after −565.2 B/session of measured reductions, was 5,727.0; headroom to the band 238 B). The probe's
  window contains its own fake transport + flow-key harness (460.8 B/session), so product-shaped cost
  is ≤4,950 B/session (measured 4,701.0 B; was 5,266.2); the N=100 row is ≈5,900 B/session.
- **UDP session setup bookkeeping budget: ≤1,500 B/session** — capacity pre-seed, slot claim, setup
  queue + payload copy, task machinery, tombstone/tracking structures. Measured 1,433 B/session,
  spread ≤172 B (task 09-21-session-creation-cost, 2026-09-22). The 2026-08-29 design guarantees
  stand: single-slot setup queue fast path, no `registered` TCS, inlined setup-failure handling,
  method-group delegates cached in the constructor, dictionary pre-sizing clamped to
  `min(capacity, 1024)`. The `≤1 KB` and "measured 2.1 KB → ~0.4 KB" figures predate the session-tier
  and teardown attribution and are **superseded — re-derive, never relax the band**.
- **Where a move comes from**: `SessionSetupDecompositionBenchmarks` localizes it — capacity 489 /
  admission 829 / setup start ≤172 / session tier 2,503 / teardown 1,459 (matched shape, not
  re-measured) – 1,885 (single-pass) B per session. Of the admission 829, 356.8 is the flow-key
  harness and 272.0 is the cold `SetupWorkItem` rent production amortizes (the executor's
  `OverflowAllocations` diagnostic is zero at steady state), so steady-state admission is ≈200 B
  per session (slot + queue + completion cell); the capacity 489 is the probe's `capacity = N`
  pre-seed (one-time at the production 1,024 clamp), and live flows beyond the clamp pay ≈80 B per
  session of amortized dictionary growth per dictionary.
- **Framework socket cost stays outside this budget** (control TCP connect + SOCKS5 handshake, UDP
  ASSOCIATE, relay socket) but is anchored instead of untracked, and is measured with the loopback
  server out of process — see [benchmark-methodology.md](./benchmark-methodology.md). Isolated
  create+dispose path **7,952 B/session** (control connect + greeting 3,792 = 47.7 %; ASSOCIATE 959;
  relay socket 576; self-traffic 160; transport ctor + wiring 2,465). Churn whole cycle
  **≤14,500 B/session** wave shape / **≤14,300** sustained (measured 13,249–14,070 / 13,720–13,869;
  recorded in `benchmarks/results/2026-09-28-udp-reuse/README.md`). Real probe marginal
  **≤17,500 B/session** (measured 17,021 B, echo-fed shape). Re-anchored 2026-09-22 (task
  09-22-session-creation-cost-redo); the superseded 83,442 / 77,448 / ≤95,000 figures were inflated by
  the in-process harness server's per-connection 64 KiB relay buffer and are not comparable. The
  N=48/D=0 churn wave cell was re-measured at 12,560.3 B/session after the
  09-22-udp-teardown-session-tier-alloc reductions (−688.6); the full wave matrix, the sustained shape
  and the real probe were not re-run, so those bands stand with additional headroom.
- **The framework-ladder band ≤8,200 B/session is deliberately not carried here.** It had two
  claimants — a "framework ladder" band in this document and the `udpChurn --churn-waves 0` row
  (7,436.8 B/session, "inside the ≤8,200 band") in
  `benchmarks/results/2026-10-01-udp-session-footprint/README.md` — and no artifact that records which
  shape it was derived from. **Measure against the band the owning artifact README records, and
  re-derive the ladder band from the archived numbers before assigning it an owner.**
- **The anchors are per-flow shapes and stay comparable.** `UdpSessionBenchmarks` and
  `FrameworkSetupBenchmarks` build one association per session through the per-flow
  `Socks5UdpTransportFactory`, so the Noop probe and the churn anchors keep describing the shape they
  were recorded on. Anchor falsification: a documented ≥3-run batch above the framework/churn anchors
  on an unmodified tree, with the in-process ratio still ≈10.5×, is product drift to fix (never to
  relax the band); if the in-process value moves with it, the harness changed and the anchor is
  re-derived.
- **The pooling series describes a removed feature.** The 2026-09-28 churn series measured
  **13,066.5 B/session** per-flow at Step 1 → **7,556.7 / 7,564.7** (`always`) / **7,577.5** (`auto`)
  with up to sixteen flows sharing one authenticated control connection, and the `udp.sessionBudget`
  soak recorded 1.06–1.95 descriptors per live flow against that shared head. Sharing was removed (it
  misdelivers replies at concurrency, and the misdelivery is undetectable when two flows share a
  destination), so those numbers are history: no recorded anchor may be re-based onto them. The
  shipped shape is the recorded `off`/per-flow column — two descriptors per live flow (control
  connection + relay socket), which `UdpSessionBudgetRun` now reports from the live session count
  itself.
- **Read every kernel-buffer and retention number with its shape.** The default relay receive buffer
  is 64 KiB per socket (128 KiB through the F6 Step 1, a hard-coded 512 KiB before that) and the
  session retention is the two-class shape: the configured `udpSessionIdleSeconds` (30 s) for every
  session except a completed one-shot exchange, which retires at
  `UdpProxyCoordinator.OneShotIdleTimeout` = 5 s, swept on the cadence the *effective* retention floor
  derives (`IdleExpirySweeper.EffectiveUdpRetentionFloor` → 5 s, was 15 s under uniform retention).
  The 2026-10-01 artifact (`benchmarks/results/2026-10-01-udp-session-footprint/`) re-ran the
  loss/burst/churn anchors on the shipped shape and they held (loss 0, burst 48/48 with
  `establishmentLossRate` 0), so the numbers keep their meaning without a re-base.
- Benchmark gate: the Noop probe tracks the bookkeeping budget, with linear time scaling at 10×
  sessions.

## Outcomes a reader must be able to predict

| Condition | Required result |
|---|---|
| Warm sync send accepted by the kernel | one counter increment, 0 B, no association interaction beyond the fault read |
| Contended gate / overlapped async send accepted | the same one increment on the send tail, still 0 B on the fast path |
| Relay datagram skipped (source/oversize/malformed/reset) | no evidence recorded |
| First decoded relay datagram for a flow | write-once response flag set (one `Interlocked.Exchange` per flow) |
| Session capacity full | datagram refused, `udpCapacityRejections` ++, rate-limited `udp.session.capacity-block` warn |
| Genuine setup failure | `udpSetupFailures` ++, 1 s setup cooldown armed, rate-limited warn |
| Flow's association dead, its next send fails | `udpAssociationLost` ++, `AssociationLost` removal, no cooldown, one rate-limited `udp.association.lost` warn for the death |
| Setup queue overflow during session setup | drop-oldest, freshest-wins |
| Setup task throws non-OCE | tombstone written, slot removed (inlined handler ⟺ old `IsFaulted` semantics) |
| Framework/churn instrument run | one association per session through the per-flow factory, so each anchor keeps its recorded per-flow shape |

## Tests required

- `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` — 0 B, and it must
  still pass in isolation (the readiness/thread rules in
  [allocation-gates.md](./allocation-gates.md) apply).
- `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` — real relay socket, 0 B on the
  warm shape.
- `UdpAssociationLossTests` — the counters move once per flow on a real control-stream death, and no
  response is recorded for a skipped datagram.
- `UdpProxyCoordinatorTests` / `UdpSessionSetupTests` — `AssociationLost` counting and the absence of
  a setup cooldown on both the ready path and the flush window, versus a genuine setup failure.
- Real-dial re-run: Noop probe ≤5,400 B/session marginal (1→1000 sweep), churn anchors as above.
