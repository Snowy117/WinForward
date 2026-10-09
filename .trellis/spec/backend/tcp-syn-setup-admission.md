# TCP SYN Setup Admission

> What happens to a genuinely new SYN before any session exists: the pump-side fast paths, the capacity
> gate and its client-visible refusal, the bounded pending-SYN index, the pooled setup executor, and the
> setup-failure cooldown. Read it when you change `TcpProxyCoordinator.HandleSynAsync` /
> `StartPendingSetup` / `LaunchSetup` / `SetupPendingAsync`, `TcpPendingSynSetupIndex`, the redirect
> capacity, or the setup executor's ring. Part of the TCP local-redirect family; the hub
> [tcp-local-redirect.md](./tcp-local-redirect.md) has the pipeline overview, the cross-cutting invariants
> and the topic map. The frame the setup eventually injects is
> [tcp-redirect-transform.md](./tcp-redirect-transform.md)'s business; the RST|ACK the gate elicits is
> [tcp-client-close-injection.md](./tcp-client-close-injection.md)'s; the setup lease it runs under is
> [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md)'s; the pump-ordering relaxation it is allowed to
> make is [ndis-batched-capture.md](./ndis-batched-capture.md#pump-batching-and-idle-pacing).

## The pump side never waits (R8, wired 2026-08-30)

`TcpProxyCoordinator.HandleSynAsync` runs on the capture pump's strictly-ordered handler chain, so every
path through it must return without waiting on a listener bind. The synchronous fast paths are unchanged
and stay instantaneous:

1. **Existing association re-inject** (`TryResolveByOriginal`): policy is evaluated exactly once, and the
   retransmitted SYN is rewritten and **deferred to that iteration's lane flush** (2026-09-29) — the handler
   still returns before touching the driver.
2. **TIME_WAIT tombstone hit**: consumed as `Dropped` — the grace contract lives in
   [tcp-redirect-teardown-grace.md](./tcp-redirect-teardown-grace.md).
3. **Setup-failure cooldown hit** (1 s per flow): consumed as `Dropped`, so a retransmitting client cannot
   hammer a failing setup path at SYN rate.
4. **Capacity gate**: see below.
5. **Genuinely new flow**: retain and return — see below.

A new SYN takes one path only: copy the frame synchronously into a pooled native lease
(`_synCopyPool.Rent()` + `source.CopyTo(lease.Span)` — the pump's native batch slot is recycled the moment
the handler returns), hand it to the coordinator-owned `TcpPendingSynSetupIndex`, launch the background
setup on the pooled `ISetupExecutor`, and return `TcpRedirectOutcome.SetupPending` (the executor consumes
it silently, trace `packet.dropped reason=setupPending`). Only a **newly created** entry pays one bounded
managed copy (`source.ToArray()`), which the background pipeline needs because a `NativeLease.Span` must
not cross an `await`; a retransmission inside the pending window overwrites the retained lease and starts
no second task.

## The capacity gate

```csharp
if (_store.SessionCount + _pendingSyn.ActiveCount >= Capacity) { /* reject */ }
```

The gate counts pending SYN setups **alongside** live sessions, because each retained entry becomes at
most one session — so the budget holds even while setups are in flight and the RST fast-fail never depends
on background registration timing. `Capacity` is `TcpRedirectOptions.Capacity`, default 16 384. The
rejection traces `tcp.redirect.rejected reason=capacity` and returns `TcpRedirectOutcome.Blocked` after
`ClientResetInjector.InjectCapacityRejectedResetAsync` — the abort shape, the claim-then-inject cooldown and
the direction matrix are in [tcp-client-close-injection.md](./tcp-client-close-injection.md). A refusal by
the pending index reports the same outcome with its own trace (`tcp.setup.pending.dropped
reason=pendingBudget`), which is the same explicit-backpressure posture: the client retries on its next
retransmission once the window clears.

## The bounded pending index (`TcpPendingSynSetupIndex`)

The gate is a **leaf lock**, never taken while holding the store, table or tombstone gates. Bounds mirror
the UDP bounded-setup pattern:

- **1024 entry cap** (distinct original flow keys). A refusal traces `tcp.setup.pending.dropped` and
  returns `Blocked` — the same posture as the capacity gate.
- **1 MiB global byte budget**, `Interlocked`-charged on retain and credited **exactly once at every
  sink**: a retransmission's overwrite, the completing setup's removal, TTL expiry, and the dispose drain.
  The budget exists for symmetry with the UDP pattern and for future jumbo frames; the entry cap binds
  first under standard MTUs (a frame is at most ~1514 B, and the pool buffer is sized to the pinned
  maximum Ethernet frame while `RetainedLength` is what the budget charges).
- **5 s retention TTL**, enforced by the idle sweep. The still-running setup task is unaffected — it
  captured its own frame reference at launch, and a replacement generation meets the table claim as an
  ordinary concurrent loser.
- **1 s per-flow setup-failure cooldown**, bounded and evict-oldest, written only on genuine failure, never
  on shutdown cancellation.

## The pooled setup worker

`LaunchSetup` rents a work item from `ISetupExecutor` (`RentItem(_setupHandler)`) and enqueues it
(`TryEnqueue`). A full ring releases the retained entry and fails the flow closed
(`TcpSetupExecutorRingFull` warn, `Blocked`) — the same backpressure posture as the pending-index cap.
`SetupPendingAsync` then runs off the pump thread:

- `_store.TryEnterSetup(out var lease)` admits the work into the store's quiescence scope. A `false`
  return means disposal already began: `_pendingSyn.Complete(..., writeCooldown: false, ...)` runs and the
  task unwinds without a cooldown. Admission is a bool, not an exception.
- The pipeline is the existing `SetupNewRedirectAsync` fed a `CapturedFlowPacket` built over the retained
  managed copy. Claim exactly-once and the concurrent-loser release are unchanged; on a claim success the
  rewrite/injection/session-registration/accept-loop tail runs from the retained copy, and a loser
  re-injects against the existing association.
- A genuine failure (null return or exception) warns and arms the cooldown; shutdown cancellation unwinds
  without one.
- **Ordering that matters**: the entry removal and its cooldown write complete *before* the lease is
  released (`lease.Dispose()` in the `finally`, after `_pendingSyn.Complete`). A later release would let a
  post-drain `RemoveAll` run in between and re-arm a cooldown on a disposed index.

**Store-gate audit.** The pump side holds no setup lease at all, so the retire drain
(`RetireSessionUnderGate`) covers background setups through the same inflight counter as the historical
inline ones. Lock order is **pending lock (leaf) → store gate → table gate → tombstone gate**, verified
acyclic; do not add a path that takes them in another order.

## Tests Required

- `TcpPendingSynSetupTests`: index bounds, exactly-once byte-budget credit, TTL, cooldown, cap trace,
  dispatch-does-not-wait-for-bind, sweep expiry while in flight, and the three dispose-race facts
  (`DisposeClearsTheCooldownAnEarlierFailureArmed`, `DisposeRacingAnInFlightSetupLeavesNoCooldownOrCharge`,
  `LaunchedSetupItemCarriesTheShutdownTokenSoParkedSetupsUnwindOnDispose`).
- Coordinator suites: absorption burst, pre-claim re-inject, dispose drain of a parked setup, capacity
  gate + RST direction matrix.
