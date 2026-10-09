# UDP Session Lifecycle

> One UDP flow's session: its state vocabulary, scope-owned lifetime, teardown reasons, fail-closed
> send drop, the receive-failure signal, and its two-class retention with the sweep cadence derived
> from it. Read it before touching session admission, disposal, expiry, or retention. Family hub:
> [udp-relay.md](./udp-relay.md).

---

## State vocabulary and teardown reasons

- `UdpSessionState { SettingUp, Active, Expiring, Faulted, Disposed }`
  (`UdpProxy/UdpSessionState.cs`) and `UdpTeardownReason { SetupFailure, Expiry, Fault,
  AssociationLost, Shutdown }` (`UdpProxy/UdpTeardownReason.cs`). Transitions are one-way
  (`SettingUp → Active → Expiring → Disposed`, `Active → Faulted → Disposed`) except that
  `CancelExpiry` returns an `Expiring` session to `Active` when the sweep loses a removal race; the
  winning owner then disposes it.
- Two levels feed the state under their own lock: the **slot** level (the coordinator's `_gate`)
  while the relay dials, then the **session** level (`_activityGate`) once attached.
- `UdpProxySession.State` is computed under `_activityGate` (lifecycle only — the send and touch
  paths no longer take it). `LastActivityUtc` is derived from the internal activity bucket, the same
  representation the coordinator's expiry scan and `TryBeginExpiry` compare.
- **Teardown reason is data**: every slot removal is `IUdpSessionSlotHost.RemoveSlotAsync(flow,
  slot, UdpTeardownReason)`, and **only `SetupFailure` arms the 1 s setup cooldown** — a dead SOCKS5
  server must not be hammered at datagram rate. `Expiry`, `Fault`, `Shutdown` and `AssociationLost`
  leave the flow free to set up again immediately. Teardown logging carries the reason.

## Scope-owned lifetime

Wired 2026-09-21 (task 09-20-lifecycle-migration-cluster). `async-lifetime.md` is the primitive's
contract and carries the per-owner table.

- `UdpProxySession` owns `_scope = new QuiescenceScope(context.Shutdown)`, which owns the CTS; the
  receive loop and every injection route through `_scope.Token`. The coordinator owns its own
  `QuiescenceScope` and session scopes nest under `_scope.Token`.
- **One failure representation**: `_scope.Fault` is the only failure state — there is no parallel
  `_receiveFailure` field. The receive loop records the fault
  (`_scope.RecordFault(exception, "udp.receive")`) and then signals. Send admission is a `WorkLease`
  taken from the scope, so the scope's own drain joins every outstanding sender, and
  `_scope.IsSealed` is the sealed fact.
- **`UdpSessionSetup`'s setup-failure `RemoveSlotAsync` stays a direct call** — a gate-taking
  decision, not a fire-and-forget teardown, and it must still work while the coordinator's scope is
  sealed.
- **`_expiring` is retained** as the owner's own admission policy: the scope never unseals, so
  expiry-versus-send cannot live in it.
- **Owner teardown single-flight (D11)**: `UdpProxySession.DisposeAsync` and
  `UdpProxyCoordinator.DisposeAsync` each keep an explicit one-shot claim; a later caller joins
  `_scope.DrainAsync()`.

## The send path takes no `_activityGate`

Fixed 2026-09-30 (task 09-30-warm-path-lock-chain). `SendSpanAsync`'s admission is

```csharp
if (Volatile.Read(ref _expiring) || _scope.Fault is not null || !_scope.TryEnter(out var workLease)) return false;
```

- The scope's lock-free CAS is the admission authority and its drain joins outstanding leases, so
  disposal still cannot free the transport under a sender. `_activityGate` survives only for the
  lifecycle transitions (`State`, `TryBeginExpiry`, `CancelExpiry`), which are off the packet path;
  `_expiring` is read and written volatile there.
- **The ready path is stronger still**: a cache-resident, flow-validated ready session takes zero
  coordinator-gate entries, zero activity-gate entries and zero clock reads — the cooldown probe and
  the clock read live on the admission path, and a flow in cooldown has no slot by construction (the
  cooldown write and the slot removal share one `_gate` hold). Pinned by
  `UdpWarmPathGateTests.UdpReadySendTakesZeroActivityGateEntries` /
  `UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads`.
- Recorded consequence: a sender admitted between the sweeper's idle re-check and its `_expiring`
  store goes **out** instead of becoming a counted `UdpFailClosedDrop` — the benign direction.

## Fail-closed send drop

- A session that refuses a datagram because it is expiring or faulted **returns `false`; it does not
  throw**. The coordinator counts `RuntimeCounters.UdpFailClosedDrop` and emits the 5 s-throttled
  `udp.send.dropped reason=sessionUnavailable`, and **leaves the slot in place**: the sweeper owns an
  expiring session's removal and the receive-failure handler a faulted one's. Throwing here would
  leak internal state into the packet path and tear down a slot its own state owner is responsible
  for.
- **A genuine transport exception is different**: the coordinator removes the slot with
  `UdpProxyCoordinator.TeardownReasonFor(exception)` and rethrows the original exception (via
  `ExceptionDispatchInfo`), preserving the existing remove-slot-then-throw semantics on both the
  inline and the awaited send shape.
- **Caller cancellation propagates untouched** while the coordinator's scope is not sealed: a
  single caller's cancellation is not evidence that the shared transport failed.

## The receive-failure signal and the join split

- `IUdpSessionSlotHost.RemoveReceiveFailedSession(UdpProxySession session)` is **`void`**, not
  `Task`: VSTHRD200 forbids an `Async` suffix on a non-awaitable, and the signal must not be
  awaitable. It runs inside the failing loop's own frame and **must return promptly** — awaiting or
  blocking on `session.DisposeAsync()` there deadlocks against the session's own disposal, which
  awaits the receive loop.
- The coordinator maps it to `_scope.Run(_ => RemoveReceiveFailedSessionAsync(session),
  "udp.receive-failure")`, which returns immediately and makes the teardown a tracked child.
- **The per-teardown warning lives inside the `Run` body**, with a rethrow so `Run` records the
  fault: `Run` swallows the child's fault into `scope.Fault` and cannot log the owner's
  domain-specific warning itself.
- `RemoveReceiveFailedSessionCoreAsync` is also the classification's ownership check: a fault whose
  slot is already gone (a send-path failure removed it first) returns before the classifier, so one
  fault is never classified or counted twice across the two paths.
- **The coordinator seals at a defined point.** `DisposeCoreAsync` cancels the scope, clears
  sessions and cooldowns, awaits each `slot.Completion` and `session.DisposeAsync()`, and only
  **then** drains the scope (sealing and joining in-flight `Run` teardowns) before releasing the
  setup limiter. Sealing earlier would refuse teardowns that in-flight sessions still need; sealing
  later would race the drain.

| Condition | Required result |
|---|---|
| Send admitted while the session is active | lease taken with no `_activityGate` (scope CAS only), released exactly once in the send tail |
| Ready session hit (cache-validated, `Session.Flow` matches) | zero coordinator-gate entries, zero activity-gate entries, zero clock reads; the transport send runs inline |
| Session expiring/faulted at admission | `false` returned, counted `UdpFailClosedDrop` by the caller, no lease taken, slot not removed |
| Flow inside its 1 s setup cooldown | rejected on the admission path before any slot; the ready path cannot observe this state |
| Send transport throws | slot removed with `TeardownReasonFor(exception)`, the original exception rethrown |
| Caller cancellation (coordinator scope not sealed) | propagates untouched; no slot removal |
| `DisposeAsync` with an outstanding send lease | does not complete until the lease is released; then `State == Disposed` |
| Idle expiry | normal teardown; no fault recorded; the handler never fires |
| Genuine receive fault | `scope.Fault` set; `State == Faulted`; a later send fails closed (`false`, counted drop); the teardown runs via `_scope.Run` |
| Receive fault racing coordinator shutdown | the in-flight teardown still completes (joined by the drain) |
| Faulting teardown | warning logged inside the `Run` body; no unobserved task exception; coordinator disposal still completes |
| Setup failure while the coordinator scope is sealed | slot removal still happens (direct call) |
| Two concurrent coordinator `DisposeAsync` calls | owner teardown runs once; the second joins the drain |

Tests: `UdpProxySessionTests` (`DisposeAsyncWaitsForAnOutstandingSendLease`,
`FaultedSessionStateHasNoParallelReceiveFailureField`, the idle-expiry and genuine-fault families);
`UdpProxyCoordinatorLifecycleTests`
(`ReceiveFailureTeardownInFlightAcrossDisposalIsStillJoined`,
`FaultingReceiveFailureTeardownLogsTheWarningAndNeverEscapes`); `UdpProxyCoordinatorTests`
single-flight disposal and `SessionStateReportsSettingUpWhileDialingThenActiveWhenReady`;
`UdpSessionSetupTests` setup-failure and cancelled-dial reasons. `HotPathAllocationGateTests`
(`EstablishedUdpDatagramPathAllocatesNoManagedBytes`, exactly 0 B and passing in isolation),
`UdpWarmPathGateTests` and the sweep gates stay green.

## Activity accounting and expiry

- **The activity stamp is a 500 ms bucket** (`ActivityBucket`, `src/WinForward.Core/ActivityBucket.cs`),
  quantised **down**, with one `ActivityBucketClock` per composition advanced by the pump, each
  claim and each sweep — a warm hit writes one integer and never reads a clock. `UdpProxySession`
  updates its bucket on *every* send and receive (`Volatile.Write`, not `Interlocked`), and expiry
  compares bucket cutoffs, so an idle sweep can retire a session **up to one bucket late and never
  early**.
- Propagation to the association-table observer (`UdpAssociationTable.TryTouch`) is throttled per
  session to `ActivityPropagationBuckets = 1` bucket (500 ms) via a CAS on
  `_lastActivityPropagationBucket`; the first activity after creation or after a bucket propagates
  immediately. The table serves reverse-leg classification on seconds-scale timeouts, so 500 ms
  granularity is unobservable there and per-datagram touches were pure overhead. **Do not throttle
  the stamp itself** — that would delay expiry.
- `UdpAssociationTable.RemoveExpired` has **no production caller** (its own doc-comment says tests
  only); the sweeper's UDP leg calls only `UdpProxyCoordinator.RemoveExpiredAsync`. Sweep pruning of
  associations is not a live production path.
- `TryBeginExpiry` admits expiry under `_activityGate` and then cancels the per-session scope
  **outside** the lock — the lock is non-reentrant, and a cancellation callback must not
  self-deadlock.

## Two-class retention and the sweep cadence

- **Two-class session retention.** UDP sessions keep `udpSessionIdleSeconds` (default 30 s) **except
  a completed one-shot exchange** — at most one datagram sent (`OneShotDatagramThreshold` inside
  `UdpProxySession`) and already answered — which is retained for
  `UdpProxyCoordinator.OneShotIdleTimeout = 5 s`. The class is read from the per-flow exchange
  evidence `IUdpExchangeCounters` (implemented by both `Socks5UdpTransport` and `LocalUdpTransport`
  over their own counters, which is what makes a completed local one-shot retire on the short class
  too; a transport that does not implement the interface is classified *sustained*), once per idle
  candidate per tick, and the classification is monotone — the counters only grow and the response
  flag is write-once.
- **The sweep cadence derives from the retention floor.**
  `IdleExpirySweeper.EffectiveUdpRetentionFloor` is the shorter of the configured retention and the
  one-shot class, and the UDP leg runs at `min(mainInterval, max(5 s, floor / 2))` — **5 s at the
  defaults**, 15 s under uniform retention. Both classes' TTLs and the never-early rule are
  unchanged: the long class retires in `(30, 35.5] s` and the short class in `(5, 10.5] s`.
  The F8 attribution pending entry rides the same tick and tightens `(5, 20] s → (5, 10] s` (see
  [error-handling.md](./error-handling.md)).
- **The relay receive buffer is per session.** Every relay socket applies `udpRelayReceiveBufferKb`
  (default **64 KiB**, range 16..1024; the owner of the default is
  `Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`; `udpRelayReceiveBufferKb: 128` restores
  the historical value) **before bind**, and the kernel aggregate estimate stays `live sessions ×
  the configured value` — one relay socket per live flow. The UoT carriage has none; see
  [udp-over-tcp.md](./udp-over-tcp.md).

Tests: `UdpSessionRetentionTests` (class selection, `EffectiveUdpRetentionFloor`, sweep-cadence
derivation), `LocalUdpTransportRetentionTests` (the local transport's own counters feed the same
class), `UdpUotRetentionTests` (the UoT stream's counters do too), `UdpSetupCooldownTests`
(1 s cooldown, bounded table), and `HotPathAllocationGateTests` (the counters add no allocation to
the datagram path). Sweep gates: `SweepAllocationGateTests.UdpProxyCoordinatorSweepAllocatesNoManagedBytes`
(the no-op tick that repeats every 5 s) and `…UdpAssociationTableSweepAllocatesNoManagedBytes` (the
retiring tick over 64 idle associations), plus
`UdpAdaptiveSweepAllocationGateTests.UdpProxyCoordinatorAdaptiveSweepAllocatesNoManagedBytes` for
the two-class tick. Their before/after figures live in
`benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/`.
