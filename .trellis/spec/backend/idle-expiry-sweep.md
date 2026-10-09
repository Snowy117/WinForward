# Idle Expiry Sweep

> The single timer that recycles WinForward's idle state — flow decisions, TCP redirect sessions,
> UDP sessions and pending attributions — and the bounds that keep a sweep from stalling the packet
> path. Split out of [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md), which owns which
> flows are proxied in the first place.

## Scope / Trigger

Read before touching `IdleExpirySweeper`, any `RemoveExpired*` site, `UdpProxySession`'s activity
stamp, `ActivityBucket`, or `DurableCaptureBundle`'s teardown order.

---

## Wiring

- `FlowTable.RemoveExpired`, `UdpAssociationTable.RemoveExpired` and friends existed but had no
  caller: UDP sessions accumulated to the bounded capacity and then failed closed.
- **`IdleExpirySweeper` (`src/WinForward.Runtime/IdleExpirySweeper.cs`) is the single wiring point.**
  `DurableCaptureBundle.BuildWithUdpAsync` constructs it with the configured retentions and starts it;
  nothing else sweeps on a timer.
- It is **disposed first** in the bundle's single-flight ordered teardown, ahead of the attribution
  pipeline and the coordinators, so no TTL tick can fire into a half-dismantled graph.
- The legs it drives: `TcpRedirectSessionStore.RemoveExpiredAsync` (the TCP leg),
  `FlowDispatcher.RemoveExpiredFlows` (the flow table), `UdpProxyCoordinator.RemoveExpiredAsync` (the
  UDP leg), and `FlowAttributionPipeline.RemoveExpired` (the pending-attribution TTL leg, wired only
  when the pipeline exists).

## One Tick Loop, Two Cadences

`Start()` runs the loop inside the owner's `QuiescenceScope` (`"idle-expiry.loop"`); cancellation
while the scope is sealed is the normal exit, not a failure.

- **The tick period is the UDP leg's cadence.** `DeriveUdpSweepInterval` asks for
  `max(5 s, EffectiveUdpRetentionFloor / 2)` and caps the result at the main interval
  (`min(mainInterval, requested)`); the 5 s floor is applied *before* the cap. The effective floor is
  the **shorter** of the configured retention and the 5 s one-shot class
  (`EffectiveUdpRetentionFloor`), so a short class is swept on its own scale. At the default 30 s
  retention that is a 5 s tick; under a uniform retention it is 15 s.
- **The main leg keeps its one-minute cadence** by gating inside each tick on the injected clock
  (`now - lastMainSweepUtc >= _interval`), and the first main sweep happens a full main interval after
  start — the historical `PeriodicTimer(_interval)` behaviour. The attribution leg is synchronous and
  allocation-free when nothing expires, so it rides every tick with no cadence of its own.
- **A failing main sweep still advances the cadence stamp**, which is stamped before the legs run, so
  a failure can never shorten or extend the group's cadence.
- The per-tick `runtime.expired` debug aggregate is emitted only when a leg expired something, and
  reports flows, TCP redirects, UDP sessions and pending attributions together.

## Sweep Order

Within the main leg, **TCP redirects sweep before the flow table**: expiring a half-open session
releases its hold before the flow sweep runs, so a dead session's flow decision expires on its own
idle instead of being held a round longer by a session that no longer exists. The capacity summary
rides the TCP leg so it keeps the one-minute cadence rather than following the faster UDP tick. Flows
held by a live relaying session or a grace tombstone are skipped by the predicate (`HoldsFlow`, cached
per tick) and keep their original idle point.

## Chunked Retirement (task 09-30-expiry-sweep-bounded-pause)

Every sweep site scans in a bounded hold, re-checks each candidate under its own gate before removing
it, and allocates nothing on a tick that expires nothing (`SweepAllocationGateTests`).

- **`FlowTable.RemoveExpired`** uses minimal hold granularity: a scan hold examines at most
  `SweepChunkEntries` (256) entries and stops at the first idle-elapsed candidate, which is then
  reported as held with **no lock held**; each removal gets its own hold at the cursor. The live-slot
  registry invariant behind this lives in [warm-path-dispatch.md](./warm-path-dispatch.md),
  "FlowTable's warm cache and chunked sweep".
- **`TcpRedirectSessionStore.RemoveExpiredAsync`** (with the tombstone sweep riding its tick) runs its
  scan and its disposal loop outside the store gate, so the reused candidate and retire scratches are
  protected by a `SemaphoreSlim(1,1)` sweep gate instead. **A `Lock` cannot be used there: the
  disposal tail awaits.** The synchronous tables use a plain `Lock` sweep gate *outer* to the table
  gate — the order is sweep gate → table gate, and it is acyclic.
- **`UdpProxyCoordinator.RemoveExpiredAsync`** clears its scratch at the start of the critical
  section, collects candidates under `_gate` with a bucket pre-filter, then runs the disposal loop
  outside the gate with `TryBeginExpiry` as the per-candidate re-verifier. A slot removal that loses
  the race calls `CancelExpiry` and leaves the session alone.
- Scratches are cleared at the **start** of the critical section, except the store's retired list,
  which is cleared only after every disposal has been attempted: an aborted tick must not lose
  already-retired, not-yet-disposed sessions — the next tick drains the leftovers whole.
- The per-tick `HoldProbe` counts (`SweepChunkEntries` as the examination bound, one removal per hold)
  are the sweep's **acceptance evidence**. The wall-clock series is report-only.

## Failure Isolation

- **Leg groups are isolated (S6d).** The main, UDP and attribution legs each own a `try`/`catch`
  inside the tick: a failing group is surfaced through one rate-limited (5 s,
  `Interlocked`-guarded) warn and retried on the next tick, without skipping or starving the others.
  A transient teardown error must never stop the capture loop.

## Activity Stamps And Retention Bounds

- **`UdpProxySession` tracks activity as a bucket-derived stamp.** `LastActivityUtc` is derived from
  the internal integer bucket; a send or a receive writes one integer from the shared
  `ActivityBucketClock` and never reads a clock.
- The coordinator's scan compares
  `ActivityBucketForDiagnostics < ActivityBucket.Cutoff(now, min(idleTimeout, oneShotIdleTimeout))` —
  the **later** cutoff, so the candidate set is a superset of both classes — and `TryBeginExpiry`
  re-checks the candidate's **own class cutoff**, so retirement is never early and at most one 500 ms
  bucket late.
- The never-early bound is therefore **class-relative**: a session stamped at `t0` retires in
  `(τ_class, τ_class + 500 ms + sweep]` — short class `(5, 10.5] s`, long class `(30, 35.5] s`. The
  tighter tick means no session is retired later than before; only completed one-shots retire earlier.
- The relay-alias collision guard is live here: a second flow claiming the same relay alias is
  rejected fail-closed.

## What The Sweeper Does Not Own

**Association retention is the flow's, not the sweeper's.** A flow's association lives exactly as long
as its session: `UdpProxyCoordinator.RemoveExpiredAsync` disposes the idle session, and that disposal
releases the transport's own relay socket and control connection with it. There is no warm retention,
no association-maintenance child, and nothing association-shaped for the sweeper to retire
independently — see [udp-association-ownership.md](./udp-association-ownership.md).

The table-level helpers that the coordinator sweeps superseded (`UdpAssociationTable.RemoveExpired`)
survive as a test-only surface; no production path calls them.
