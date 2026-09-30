# F3 expiry sweeps: bounded pause, zero allocation under locks

Parent: `08-30-proxy-perf-stability`. Finding F3 of the archived research
`09-29-tcp-udp-path-structural-perf` (`research.md` §F3, addendum §A4 item 5). This is step 2 of the
operator's F2–F8 pipeline (step 1, test stabilisation, is archived).

Site numbering below is canonical and matches `design.md` §1; requirements name files, not numbers.

## Problem

The expiry legs walk their whole population on every tick while holding a table gate, and all but one of
them allocate their retirement set inside that hold:

| # | Site | Shape today |
|---|------|-------------|
| 1 | `FlowTable.RemoveExpired` (`src/WinForward.Core/FlowTable.cs:90-118`) | **the pause-critical site**: enumerates all 65,536 states under the global gate, per-entry `DateTimeOffset` subtraction, and an `isHeld` predicate that acquires TCP store locks **inside** the flow gate. The one site that already allocates 0 B per sweep (`_expiredScratch`) |
| 2 | `TcpRedirectTable.RemoveExpired` (`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:319-335`) | `Where(...).Distinct().ToArray()` under the gate; no production caller (tests only), but its gate is shared with the warm reverse path |
| 3 | `TcpRedirectSessionStore.RemoveExpiredAsync` (`src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs:101`) + `TcpRedirectTombstoneTable.RemoveExpired` | the **live** TCP expiry leg: scans and retires under the store gate, building the retirement set with LINQ/collection expressions |
| 4 | `UdpProxyCoordinator.RemoveExpiredAsync` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:341-368`) | LINQ snapshot allocation under `_gate`, on a tick that runs every `min(mainInterval, max(5 s, idleTimeout/2))` — 15 s with the shipped defaults |
| 5 | `UdpAssociationPool.SweepIdleAssociationsAsync` (`src/WinForward.Runtime/UdpProxy/UdpAssociationPool.cs:214-230`) | `SelectMany/Where` list allocation under the pool gate |
| 6 | `UdpAssociationTable.RemoveExpired` (`src/WinForward.Runtime/UdpProxy/UdpAssociations.cs:126-138`) | scan-and-remove under the gate; no production caller, fixed with site 2 so "every sweep site allocates nothing" holds repo-wide |

The benchmark-coverage task measured the consequence at site 1
(`benchmarks/results/2026-09-29-benchmark-coverage/sweep-pause.jsonl`): a sweep over 65,536 seeded flows
blocks a concurrent `TryResolve` for up to **40.2 ms**, against the research's **0.5 ms** target, with
21,252 pauses over 500 µs in a 15 s window.

**Why the accepting metric is work, not wall-clock (established by measurement, not assumption).** After
the first implementation of the bounded-hold design, the warm path measurably stopped starving —
resolves completed *inside* sweep windows rose from 6,469–10,446 per 15 s window to 11.6–14.2 M — while
`maxSweepWindowPauseMs` still read 4.7–7.6 ms. Three controls in the task's artifact README pin the
cause: with a single observer thread the same code shows 21 in-window pauses > 0.5 ms out of 4.93 M
(0.0004 %); a control run with the sweep window armed for ~120 ms and **no product call at all** still
measures a 5.19 ms in-window max and 12,336 in-window pauses > 0.5 ms over 46.5 M resolves
(`--sweep-window-control-ms 120`, the shipped reproducible control); and out-of-window (refill, no
sweep hold) exceeds 0.5 ms 0.42 % of the time versus 0.14 % in-window. On a saturated host the wall-clock
pause series therefore measures scheduling and lock queueing, not hold length, so it cannot be an
acceptance gate. The property the PRD actually needs is bounded **expiry work** behind the gate, and that
is provable exactly by counting work per hold.

## Requirements

1. **Bounded expiry work per gate hold at site 1.** No flow-table gate hold inside
   `FlowTable.RemoveExpired` may examine more than `ChunkEntries` entries or remove more than
   `ChunkEntries` entries, so the expiry work a concurrent `FlowTable.TryResolve` can queue behind is
   bounded by construction. Proven by work counts (requirement 1's acceptance), not by a wall-clock
   pause number. The other sites keep an O(their live population) scan in one hold: bounded by ≤16,384
   entries per site (Σ over configured servers for the pool, ≤1,024 each), with the recorded estimate
   that the UDP coordinator's scan reaches ~0.5–1.3 ms at full capacity; `udp-ready-path-contention` is
   the trigger to extend the cursor there.
2. **No allocation on the gated tick**, per site, over a **populated** world (exact
   `GC.GetAllocatedBytesForCurrentThread` gate, not a threshold): site 1 and sites 2/6 are gated on a
   *retiring* tick; the three async sites (`TcpRedirectSessionStore`, `UdpProxyCoordinator`,
   `UdpAssociationPool`) are gated on a *no-op* tick, because their retiring ticks await disposal that is
   outside this gate's scope.
3. **No predicate under a table lock.** The holds-flow / `isHeld` predicate must not run while a flow-table
   lock is held; the lock-nesting edge it creates must be gone.
4. **No warm-path regression.** The warm resolve keeps its 0 B allocation contract and its current cost
   shape. The research's F3.4 (replace the per-hit `TimeProvider.GetUtcNow()` with an integer activity
   bucket) is **deferred to the F2 task**, which needs the bucket representation for its lock-free
   resolve; F3 lands only the once-per-tick integer cutoff comparison and records the representation
   contract F2 must honour (`research/implementation-notes.md` §8).
5. **Expiry semantics preserved.** A flow / UDP session / TCP association / pooled association is still
   retired no later than `idleTimeout` plus one sweep interval after its last activity; one
   `RemoveExpired` call still completes one full round (at 65,536 idle flows it still removes all
   65,536); `Count`, `Capacity` and the public table API keep their meaning; fail-closed behaviour is
   unchanged.
6. **Existing gates stay green**, including `HotPathAllocationGateTests`, the gc-soak shape anchors, and the
   UDP retention / burst scenarios. The new `_liveStates` registry (≈ +512 KiB at 65,536 capacity) is
   recorded as a measured delta in the artifact README.

## Acceptance Criteria

- [ ] **Work-per-hold bound (exact counts).** At 65,536 seeded idle flows, a Release-run probe test
      asserts that no `_gate` hold inside `FlowTable.RemoveExpired` performs more than `ChunkEntries`
      examinations or more than `ChunkEntries` removals, and records the round's hold count, max
      examinations and max removals; the same run proves one call still removes all 65,536.
- [ ] **Warm path stops starving (series).** In the instrumented sweep scenario at 65,536 flows, resolves
      completed inside sweep windows reach ≥ 1,000,000 per 15 s window (before-series: 6,469–10,446),
      3 runs. Measured with minimal hold granularity: 11.6–14.2 M.
- [ ] **Hold granularity is minimal and the duration cost is recorded, not hidden.** One gate hold removes
      at most one entry, and the scan hold examines at most `ChunkEntries`. The resulting cost is
      report-only: the pathological all-expired round measures ~120 ms against the before-series 26.2 ms,
      because the sweep deliberately trades its own duration for warm-path progress (measured trade:
      one removal per hold 11.6–14.2 M in-window at ~120 ms, 32 removals per hold ~1.1 M at ~55 ms,
      256 removals per hold ~0.1 M at ~35 ms). A production-shaped sweep (4,096 live / 32 idle-elapsed)
      is measured and recorded, and the sweeper's duty cycle at the shipped 60 s cadence stays ≤ 0.5 %.
- [ ] **Report-only calibration.** `maxPauseMs`, `pausesOver500us` and `maxSweepWindowPauseMs` are
      recorded together with the control window (armed, no sweep call) as the host's noise floor, and are
      explicitly not acceptance gates.
- [ ] An exact zero-allocation gate exists for each site (gated tick per requirement 2); site 1's existing
      gate stays green, and its predicate variant is added as a regression fact.
- [ ] `FlowTable.RemoveExpired` provably does not invoke the holds-flow predicate under the table gate
      (predicate-asserts-not-held test plus a parked-predicate concurrent-resolve test).
- [ ] Release build zero-warning, full suite green, `dotnet format --severity info --verify-no-changes`
      empty output, `jb inspectcode` zero `<Issue>`.
- [ ] The benchmark data that supports the claim is recorded and cited in the task record before archive.

## Notes

- The research's F3.1–F3.4 are the input; `design.md` decides which are adopted here and which are
  deferred. F3.1 (intrusive timing wheel) is rejected there with reasons.
- The site-1 sweep is a chunked round at **minimal hold granularity**: a scan hold examines at most
  `ChunkEntries` entries and stops at the first idle-elapsed candidate, the predicate runs with no gate
  held, and a one-entry removal hold then swap-removes that candidate (it removes at the cursor, so the
  element swapped in is examined next without a cursor rewind). Batching several removals into one hold
  was implemented and then **rejected by measurement** — it reduces the sweep's own duration but costs an
  order of magnitude of warm-path progress, which is the property this task exists to fix.
- Corrections recorded while planning (the first draft of this PRD carried them wrong): the UDP sites live
  in `src/WinForward.Runtime/UdpProxy/`, not `Udp/`; `TcpRedirectTable.RemoveExpired` and
  `UdpAssociationTable.RemoveExpired` have no production caller while
  `TcpRedirectSessionStore.RemoveExpiredAsync` is the live TCP leg; the UDP tick interval is capped by the
  main interval (`IdleExpirySweeper.DeriveUdpSweepInterval`).
- Known defect to carry into F2 (found while verifying the research's per-hit clock claim):
  `FlowState.Reset` stamps `DateTimeOffset.UtcNow` directly, bypassing the injected `TimeProvider`, so the
  bucket conversion must unify the clock source first.
- Deliberately dropped after review (record as a follow-up, not part of this task): the
  `SampleServerCapabilities` early-out — it is not an expiry sweep, no requirement names it, and it would
  add another exact window to a host-lump-exposed gate set.
- Dead ends recorded against re-exploration: in-window wall-clock pause as an acceptance figure; batched
  removal holds (`ChunkEntries`-sized holds plus a cursor rewind) — implemented, measured, and rejected
  because hold granularity, not acquisition count, is what decides how much warm-path progress survives
  a sweep.
- Out of scope: the full FlowTable rebuild of addendum §A4 (sharded slab + index), the F2 lock
  architecture, and the live-slot cursor at sites 2–6 (their scans stay O(live population) in one hold
  until a measurement asks for it).
