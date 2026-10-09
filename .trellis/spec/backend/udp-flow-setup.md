# UDP Flow Setup

> What happens between a flow's first datagram and a live session: where datagrams wait, how much
> memory they may hold, how long they stay deliverable, and when a failed flow is cooled down. Read
> it before touching setup admission, the setup queue, or the setup cooldown. Family hub:
> [udp-relay.md](./udp-relay.md).

---

## First datagram to live session

- `UdpProxyCoordinator.TrySendSpanAsync` is **non-async and never blocks the capture pump**. A flow
  with no slot gets one, and the dial/claim/construct/flush pipeline runs as a background task off
  the pump **and off the coordinator gate** (`ScheduleSessionSetup` through the injected
  `ISetupExecutor`). Datagrams arriving while the slot is not yet ready are copied into the flow's
  bounded setup queue and answered later; none is downgraded to pass.
- `UdpSessionSetup` is that pipeline: the dial, the relay-alias claim, session construction, and the
  FIFO flush. It takes plain collaborators plus the single slot seam
  `IUdpSessionSlotHost host` — five operations (attach, re-stamp, flush dequeue, slot removal,
  receive-failure removal) whose gate-taking members keep the coordinator gate the single arbiter of
  slot state, so the pipeline is testable against a fake host and never observes the slot dictionary
  itself.
- **The setup limiter is 8 wide** (`UdpSessionSetup.MaximumConcurrentSetups`), and a flow beyond the
  cap waits on the semaphore rather than failing. Fail-fast was removed: dropping one already
  accepted datagram per cap failure turned a 256-flow flash crowd into tens of seconds of
  cooldown-retry loss.
- **A session table at capacity** refuses the new flow fail-closed with the 5 s-throttled
  `udp.session.capacity-block` warn and `udpCapacityRejections` — no cooldown, no teardown.
- The setup pipeline's failure sink is `UdpSessionSetup.HandleSetupFailureAsync`: an
  `OperationCanceledException` maps to `Shutdown`, an `UdpAssociationLostException` to
  `AssociationLost` (through `UdpProxyCoordinator.TeardownReasonFor`, the single classification
  point), and everything else to `SetupFailure` — counted `udpSetupFailures`, emitting
  `udp.setup.failed`, and the only reason that arms the cooldown.

## The setup queue and its global budget

Wired 2026-08-30 (task 08-30-atomic-retire). Per-flow bounds alone permitted
`capacity × 32 KiB` to accumulate while every setup parked on the 8-wide limiter.

- Per flow: `BoundedSetupQueue` in `src/WinForward.Core/BoundedSetupQueue.cs`, constructed as
  `new(32, 32_768)` (`UdpProxyCoordinator.SetupQueueMaximumPackets` /
  `SetupQueueMaximumBytes`), drop-oldest on overflow. Its entry is
  `Entry(NativeLease Lease, int Length, DateTimeOffset EnqueuedAt)` with an inline single-slot fast
  path plus a `Queue<Entry>`: `TryEnqueue(NativeLease lease, int length, DateTimeOffset
  enqueuedAt)` takes ownership of a native lease the caller filled (the queue leaves ownership with
  the caller when it refuses), and the dequeue surface is `TryDequeue(out NativeLease, out int)` /
  `TryDequeue(out NativeLease, out int, out DateTimeOffset)`. It is not thread-safe by design: one
  queue is owned by one flow's slot and every access is serialized by the coordinator gate.
- **Aggregate: one global byte budget, `UdpSetupQueueBudget.SetupQueueGlobalByteBudget` = 8 MiB**
  (`UdpProxy/UdpSetupQueueBudget.cs`), pure `Interlocked` accounting with no lock interaction with
  the coordinator gate. The test seam is `UdpProxyOptions.SetupQueueGlobalByteBudget` (internal
  `init` property), consumed by the coordinator at construction — there is no ctor parameter for it.
- **Charge/credit is exactly once**: the budget is charged at enqueue
  (`UdpSetupQueueBudget.TryCharge`), before the per-flow `TryEnqueue`, and credited back
  (`UdpSetupQueueBudget.Credit`) at whichever sink the datagram leaves the pending set through —
  flush send, flush TTL drop, drop-oldest eviction, per-flow-bounds rejection rollback, slot drain
  (setup failure), dispose drain. **Every `BoundedSetupQueue.TryDequeue` call site must credit**; a
  new dequeue sink without a credit is a budget leak, and a leaked charge permanently shrinks the
  budget.
- **Budget exhaustion rejects the new datagram** — charge rolled back, `RejectionCount` and
  `UdpSetupBudgetRejections` incremented, the existing drop path taken. No setup cooldown is armed
  and no teardown happens; the flow retries on its next datagram.
- Diagnostics are one record, `UdpProxyDiagnostics` (`UdpProxy/UdpProxyDiagnostics.cs`), read off
  `UdpProxyCoordinator.Diagnostics`: `PendingSetupBytes`, `SetupBudgetRejectionCount`,
  `SetupCooldownCount`, `SetupTtlExpiredCount`, `SetupStampsRefreshedCount`. The TTL and re-stamp
  counters are owned by `UdpSessionSetup` (`TtlExpiredCount`, `StampsRefreshedCount`), not by the
  coordinator.

## The flush TTL ages from dial start

- `UdpSessionSetup.s_setupQueueDatagramTtl` is **5 s**, and it is measured from the flow's **dial
  start**, not from the datagram's arrival: `CreateSessionAsync` calls
  `RefreshSetupStampsAtDialStart` → `BoundedSetupQueue.RefreshEnqueuedStamps` right after the setup
  leaves the limiter, under the same slot-ownership check the flush uses. Time spent queued on the
  limiter is WinForward's admission delay, not client staleness, and the flush must deliver fresh
  state.
- **Age filtering belongs to the flush only**: flush drops entries whose own enqueue stamp is older
  than the TTL; drop-oldest eviction credits regardless of age. Stamps come from the coordinator's
  `_timeProvider`, so fake-time tests drive them.
- Consequence: total retention is bounded by dial start + TTL (the per-flow bounds and the 8 MiB
  budget cap the limiter-wait component), and `SetupStampsRefreshedCount` is the diagnostic.
  Established sessions are immune either way. **Any change that re-attributes the stamp or widens
  the limiter must re-run the `udp.burstEstablishment` acceptance matrix**, whose recorded figures
  live in `benchmarks/results/2026-09-06-udp-burst-ttl-fix/`.
- A 5 s TTL is safe because a normal setup completes in well under a second; a datagram still queued
  after that has already been retransmitted or abandoned at the application layer.

## The setup cooldown is bounded

- `UdpSetupCooldownTable` (`UdpProxy/UdpSetupCooldownTable.cs`) is a `TryHit` / `Write` /
  `PruneExpired` deadline index whose **capacity is the coordinator's session capacity**. A write at
  capacity evicts the oldest entry: refusing the write would degrade the cooldown into an
  immediate-retry storm against a dead server.
- Only `UdpTeardownReason.SetupFailure` arms the 1 s cooldown. Lazy prune on touch plus the
  sweeper's UDP leg — at its own derived cadence, see
  [udp-session-lifecycle.md](./udp-session-lifecycle.md) — keep the index bounded between ticks.
- A flow inside its cooldown is rejected on the admission path before any slot exists and traced as
  `udp.setup.cooldown`.

| Condition | Required result |
|---|---|
| Aggregate pending bytes + new datagram > 8 MiB | enqueue rejected, charge rolled back, counter++ |
| Queued entry stamped older than 5 s at flush | dropped (not delivered), credited, counter++ |
| Setup failure drains the slot queue | every drained byte credited |
| Dispose drains pending queues | every drained byte credited |
| Setup cooldown table at capacity on a new failure | oldest entry evicted, the write proceeds |

Tests: `UdpSetupQueueBudgetTests` owns
`GlobalSetupBudgetRejectsBeyondTheAggregateAndCreditsBackOnFlush`,
`SetupFailureCreditsBackThePendingBudget`, `DisposeCreditsBackDatagramsStillQueuedForSetup`,
`SetupQueueLeasesReturnToThePoolAcrossDropOldestFlushAndTtlDrop` and
`SetupQueueLeaseIsReleasedWhenTheDatagramExceedsTheFrameCap`; `UdpSetupQueueTests` owns
`FlushDropsSetupDatagramsOlderThanTheTtl` (fake `TimeProvider`: a late-arriving fresh entry on an
old slot is still delivered — entry stamp, not slot stamp) with the FIFO / drop-oldest / no-bypass /
dispose-drain family; `UdpSetupCooldownTests` owns
`SetupCooldownsAreBoundedAndEvictTheOldestAtCapacity` plus the 1 s-cooldown, 8-way-cap and
flash-crowd facts.
