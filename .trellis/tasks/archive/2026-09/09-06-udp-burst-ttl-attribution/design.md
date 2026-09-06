# Design — UDP burst first-datagram TTL re-attribution

## Context (measured defect)

`UdpProxyCoordinator` setup path: `TrySendAsync` stamps each queued datagram with
`_timeProvider.GetUtcNow()` at enqueue (`EnqueueSetupDatagram`, `UdpProxyCoordinator.cs:230`);
`CreateSessionAsync` first awaits the 8-wide `_setupLimiter` (`:367`) — a flash crowd's
wave k waits (k−1)×D here — then dials, then `FlushSetupQueueAsync` drops entries whose
enqueue stamp is older than `SetupQueueDatagramTtl` (5 s) at `:447`. Measured
(`results/2026-09-06-udp-burst/`): 128 flows × 4 s dial → only wave 1's 8 triggering
datagrams survive; 93.75 % first-datagram loss. The limiter queue-wait is our own
admission delay, not client staleness.

## Fix

Re-stamp the slot's queued datagrams **when its setup leaves the limiter queue** (dial
start). Total retention semantics become: a datagram may wait on the limiter
indefinitely (bounded by the queue's 32-packet/32-KiB slot bound and the 8 MiB global
budget — both unchanged), then at most `SetupQueueDatagramTtl` of dial time.

### Changes

| File | Change |
|---|---|
| `src/WinForward.Core/PacketRuntime.cs` | `BoundedSetupQueue.RefreshEnqueuedStamps(DateTimeOffset)` — refresh `_pending` + every `_items` entry's `EnqueuedAt` (`record struct` `with`); rebuild `Queue<Entry>` in place (≤ 32 entries, cold path). `Bytes`/`Count` untouched. |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` | In `CreateSessionAsync`, right after `_setupLimiter.WaitAsync` returns (before dial): under `lock (_gate)`, if the slot is still the flow's live owner (`_sessions.TryGetValue` + `ReferenceEquals`, same check `FlushSetupQueueAsync` uses), `slot.SetupQueue.RefreshEnqueuedStamps(_timeProvider.GetUtcNow())` and `Interlocked.Add(ref _setupStampsRefreshedCount, entryCount)`. New `internal long SetupStampsRefreshedCount` diagnostic. Update `FlushSetupQueueAsync`'s doc comment to the new age semantics. |
| `tests/WinForward.Core.Tests/UdpSetupQueueTests.cs` | New tests + deliberate update of `FlushDropsSetupDatagramsOlderThanTheTtl` semantics note (below). |
| `.trellis/spec/backend/udp-relay.md` | Rewrite the 2026-09-06 measured-contract bullet to post-fix semantics; keep the matrix re-run gate. |
| `benchmarks/results/` | Post-fix matrix re-run artifacts + before/after README. |

### Why re-stamp at dial start (not other options)

- **Age-from-dial-start at flush (`now - max(stamp, dialStart)`)** — equivalent outcome but
  threads a new field through slot state and both flush/dequeue paths; re-stamping keeps
  the TTL check site a single unchanged expression and `BoundedSetupQueue` stays the only
  owner of stamps.
- **Widening the limiter** — changes admission semantics globally (server/OS pressure),
  out of scope; the mux research task owns the structural answer.
- **Raising the TTL** — re-introduces the stale-delivery problem the TTL was added to
  prevent (deliberately-expired retransmits replayed late); dial-start attribution
  removes exactly the false-staleness component and nothing else.

### Thread-safety note

`BoundedSetupQueue` is documented not-thread-safe, serialized by the coordinator gate —
the refresh call site takes `_gate` for exactly that reason, and concurrent enqueues
(`EnqueueSetupDatagram` under the same gate) are ordered against it. Entry count read
inside the same critical section feeds the counter; `Interlocked.Add` publishes.

## Test design

1. Queue-level (`BoundedSetupQueue`): empty no-op; single pending stamp refreshed; all
   `Queue<>` entries refreshed; `Count`/`Bytes` invariant.
2. Coordinator-level (fake `TimeProvider` + controllable factory, following the existing
   `UdpSetupQueueTests` fixtures):
   - **Limiter-wait refresh**: occupy all 8 setup slots with blocked transports; send
     flow #9's datagram (queued); advance fake clock 10 s (> TTL); release — flow #9's
     datagram must be delivered, `SetupTtlExpiredCount == 0`,
     `SetupStampsRefreshedCount >= 1`.
   - **Dial-time TTL still drops**: datagram enqueued, limiter free (refresh ≈ now), but
     the transport factory delays > 5 s inside `CreateAsync` → flush drops it,
     `SetupTtlExpiredCount == 1`. This is the deliberate evolution of
     `FlushDropsSetupDatagramsOlderThanTheTtl` (age now measured from dial start).
3. Existing budget/credit/cooldown tests must stay green unchanged (accounting untouched).

## Acceptance gate (from prd R3)

Re-run `udp.burstEstablishment`: the 128 × 4000 ms probe must report
`establishmentLossRate == 0` with `firstResponses == 128` (timeToLast ≈ 64 s — all 16
waves dial through); spot matrix points (8/48/128 × 100 ms) within noise of the
2026-09-06 baseline (latency shape is unchanged by design); background windows still
zero-loss / sub-0.1 ms p95. Artifacts under `benchmarks/results/<date>-udp-burst-ttl-fix/`
with a before/after table.

## Risks

| Risk | Mitigation |
|---|---|
| Gate hold time grows by the rebuild | ≤ 32 entries, once per setup, cold path |
| Stale deliveries re-introduced | Retention still capped at dial start + TTL; only limiter-wait is excused |
| Subtle interaction with slot teardown racing the refresh | Same-gate ownership check as flush; a lost owner skips refresh and flush returns anyway |
| Windows-side numbers diverge | Gate is a Linux matrix re-run per spec contract; Windows replication stays a separate backlog item |
