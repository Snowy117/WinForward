# Design — F3 expiry sweeps: bounded pause, zero allocation under locks

Scope: the four production sweep legs and the two test-only table sweeps — six sites, numbered
canonically exactly as the PRD numbers them (§1). Nothing on the packet *decision* path changes: no
rewrite, no injection, no policy evaluation, no table lookup semantics. The only packet-visible effect
is that a warm resolve stops queueing behind a full-table scan.

Deliberately not in scope: the A4 FlowTable rebuild (sharded slab + index, canonical key), the F2
lock architecture, F3.4's bucket storage (deferred to F2 — the seven-item representation contract F2
must land is in `research/implementation-notes.md` §8), and the F2/F4 work that would let the sweep run
without the table gate at all.

## 1. Chosen approach per site

| # | Site (canonical numbering, matching the PRD) | Mechanism | Gate-hold bound | Gated tick (PRD requirement 2) |
|---|---|---|---|---|
| 1 | `FlowTable.RemoveExpired` (`FlowTable.cs:90`) | **live-slot registry + chunked, resumable round**; hold predicate moved outside every table lock | one chunk of ≤ `ChunkEntries` examinations, or one single-entry removal | retiring (N expired, predicate present) |
| 2 | `TcpRedirectTable.RemoveExpired` (`TcpRedirectTable.cs:319`) | reused candidate scratch; scan in one hold, one short hold per removal; `Distinct()` deleted | one O(live-population) scan hold + O(1) removal holds | retiring |
| 3 | `TcpRedirectSessionStore.RemoveExpiredAsync` (`TcpRedirectSessionStore.cs:101`) + `TcpRedirectTombstoneTable.RemoveExpired` (`TcpRedirectTombstoneTable.cs:92`) | `SemaphoreSlim(1,1)` sweep gate + reused candidate/retire scratch; tombstone sweep reuses its own list; caller hoists the pending-SYN closure **and** the per-tick `HoldsFlow` delegate | one O(live-population) scan hold + one O(1) retire hold each | no-op over a populated store |
| 4 | `UdpProxyCoordinator.RemoveExpiredAsync` (`UdpProxyCoordinator.cs:341`) | `SemaphoreSlim(1,1)` + reused `(slot, session)` scratch; the existing `TryBeginExpiry` re-check stays the outside-gate verifier | one O(live-population) scan hold + one hold per candidate | no-op over a populated session set |
| 5 | `UdpAssociationPool.SweepIdleAssociationsAsync` (`UdpAssociationPool.cs:214`) | `SemaphoreSlim(1,1)` + reused association scratch; **one hold: scan + `CanRetire`/`CanRetireFaulted` + `set.Shared.Remove`**; disposal outside | one O(live-population) hold | no-op over a populated, fully-leased pool |
| 6 | `UdpAssociationTable.RemoveExpired` (`UdpAssociations.cs:126`) | same scratch fix as site 2 (no production caller; kept so "every sweep site allocates nothing" holds by grep) | one O(live-population) scan hold + O(1) removal holds | retiring |

The pause contract (PRD requirement 1) is scoped to site 1 — the only expiry gate in front of a
per-packet warm resolve at the 65,536-flow cardinality. The other sites (2–6) keep an O(their live
population) scan inside one hold: bounded by ≤16,384 entries per site, Σ over configured servers for
the pool (≤1,024 shared associations each, plus private associations bounded by the coordinator's
session capacity). At site 4 that hold reaches **~0.5–1.3 ms at the full 16,384-session capacity**, and
that gate is the warm datagram gate — accepted here, recorded honestly in the artifact README, with
`udp-ready-path-contention` named as the trigger to extend the live-slot cursor there. If a later
measurement asks for it at any of sites 2–6, the cursor of §2 extends mechanically (the same three
fields, keyed on the same kind of primary index).

Rejected alternatives, with reasons: `research/implementation-notes.md` §8 and §12.

## 2. `FlowTable`: live-slot registry + minimal-granularity chunked round

### 2.1 Data structure

New fields on `FlowTable` (everything else on the type is untouched):

```csharp
internal const int SweepChunkEntries = 256; // the scan hold's examination bound (see 2.2)
private readonly FlowState[] _liveStates;   // ctor: new FlowState[capacity]  (+8 B/slot)
private int _liveCount;                     // live prefix length of _liveStates
private readonly Lock _sweepGate = new();   // sweep-level single flight (outer; never under _gate)
internal SweepHoldProbe? HoldProbe { get; set; }   // diagnostics only; null in production
```

`_expiredScratch` (`FlowTable.cs:9`) is **deleted**, and the landed shape needs **no scratch at all**: a
scan hold hands its one candidate straight to the predicate and the removal hold, so the `_batch` /
`_approved` lists of the rejected batched variant (D-B, §2.2) do not exist. The registry is a plain
reference array — no intrusive links, no per-state field, no change to `FlowState`, `Reset`, `RentState`
or `ReturnState`.

Invariants (both maintained under `_gate`, the only place the registry is touched):

- `_liveStates[0.._liveCount)` is exactly the set of `FlowState` instances in `_states`; no holes, no
  duplicates. `internal int LiveStateCountForDiagnostics { get { lock (_gate) return _liveCount; } }`
  exists so a churn test can assert `LiveStateCountForDiagnostics == Count`; like `Count`, it takes
  `_gate` so the reading can never race the registry.
- Append in `TryClaimResolved` after `_states.Add` + `AddToTransportIndex` (`FlowTable.cs:75-76`):
  `_liveStates[_liveCount] = created; _liveCount++;`
- Remove only in the sweep, and always **before** `ReturnState` (a recycled state must never remain in
  the registry). Removal is the classic swap-remove **at the cursor**:
  `_liveStates[cursor] = _liveStates[--_liveCount];`.
- The cursor only passes an index that has been examined, or one whose state was removed — so no
  per-state index field and, unlike the batched variant, **no rewind rule**: the swap-remove happens at
  the cursor, so the tail element lands exactly where the cursor points and is examined next.

### 2.2 Algorithm — minimal hold granularity (landed; batched variant rejected by measurement)

```
RemoveExpired(now, idleTimeout, isHeld):
  lock (_sweepGate)                                    // concurrent callers wait, as today's _gate did
    cutoffTicks = now.UtcTicks - idleTimeout.Ticks      // once per call (F3.4's compare shape)
    probe = HoldProbe                                   // diagnostics sink, read once; null in production
    lock (_gate) { roundLimit = _liveCount }            // one round covers the states present at entry
    cursor = 0; removed = 0
    while (cursor < roundLimit && cursor < _liveCount)  // round bound AND validity bound
      // ---- SCAN HOLD: ≤ SweepChunkEntries examinations, cursor past every live entry ----
      lock (_gate)
        examined = 0
        while (cursor < roundLimit && cursor < _liveCount && examined < SweepChunkEntries)
          s = _liveStates[cursor]
          if (s.LastActivityUtc.UtcTicks > cutoffTicks) { cursor++; examined++; continue }  // still live
          probe?.RecordScanHold(examined + 1)           // the candidate is this hold's last examination
          break out with candidate = s, cursor left ON it
        (chunk exhausted) probe?.RecordScanHold(examined); candidate = none
      if (candidate is none) continue
      // ---- NO table lock held — the caller's predicate decides ----
      if (isHeld is not null && isHeld(candidate.Key))
        { lock (_gate) cursor++; probe?.RecordRemovalHold(0); continue }  // skipped, activity untouched
      // ---- REMOVAL HOLD: exactly one entry, at the cursor ----
      lock (_gate)
        if (cursor < _liveCount && ReferenceEquals(_liveStates[cursor], candidate)
            && candidate.LastActivityUtc.UtcTicks <= cutoffTicks && _states.Remove(candidate.Key, out _))
          { _liveStates[cursor] = _liveStates[--_liveCount]; RemoveFromTransportIndex(candidate);
            ReturnState(candidate); removed++; probe?.RecordRemovalHold(1) }
        else { cursor++; probe?.RecordRemovalHold(0) }   // touched in between, or already gone
    return removed
```

**Why minimal granularity won.** Hold *granularity*, not acquisition count, is what preserves warm-path
progress: a shorter hold is a shorter queue a concurrent resolve can be stuck behind. Measured on the
instrumented sweep scenario (`sweepWindowResolves` per 15 s window / `sweepMeanMs`): one removal per hold
**13.1–14.1 M / 110–115 ms** (3 runs), 32 removals per hold 1.06–1.10 M / 55–56 ms (2 runs), 256 removals
per hold 89 k–120 k / 35–37 ms (3 runs). The batched variant was implemented, measured with the full
probe/round-completeness work, and rejected: it buys the sweep's own duration, which this task does not
need, with an order of magnitude of the property the task exists to fix — and its longer holds also made
the report-only in-window pause tail *worse* (49.8–58.7 ms against 4.4–6.8 ms). Numbers and artifacts:
`benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md`; the rejected-variant runs are kept
as `sweep-pause-k{32,64}-diagnostic.jsonl` (the 256-removal default-chunk runs themselves were **not**
retained — their figures are the working-log numbers carried in `implement.md` finding 20 and in the
artifact README's trade table, which marks that provenance).

**Why the batched variant needed a rewind (kept as the rejected alternative's counterexample).** With
`SweepChunkEntries` removals per hold in descending slot order (descending so an earlier removal cannot
displace a still-pending candidate), each removal pulls the current tail into its own slot; a tail element
from an unexamined index lands *below* the cursor, so without
`from != slot && from >= cursor → cursor = min(cursor, slot)` it is never examined in that round.
Simulated and then reproduced in code: N = 65,536 all idle-elapsed, K = 256 → the batched round returned
**32,768 removals with 32,768 idle-elapsed states still live** (the scenario's tripwire would throw);
the landed minimal-granularity loop has no such failure mode because it removes at the cursor.

Boundary semantics (the implemented, test-pinned form): **expired is `LastActivityUtc.UtcTicks <=
cutoffTicks`**, so the skip is `> cutoffTicks`. The original code's `now - last >= idleTimeout` and the
exact-boundary assertions in `CoreFlowStructuresTests.cs:96-97`, `:116-117` are equivalent to `<=`, not
`<`; the earlier draft's `>= cutoffTicks` skip was off by one tick at the boundary.

**Why the constant is a scan bound only.** A scan hold performs ≤ `SweepChunkEntries` examinations; a
removal hold removes exactly one entry. `SweepChunkEntries` is therefore not a granularity lever at all in
the landed shape — every idle entry costs its own removal hold whatever the constant is — and raising or
lowering it cannot reduce the round's total work (every entry is examined once and every idle entry
removed once). The acceptance evidence is the countable `SweepHoldProbe`, never a wall-clock reading.

Properties:

- **One call still completes one round.** Invariant: no unexamined element ever sits below the cursor.
  A scan hold advances the cursor only past still-live entries, and a removal swap-writes the tail *into*
  the cursor's own slot, so the element that lands there is examined next. The round ends only when the
  cursor has passed every live slot (or `roundLimit`, for states appended mid-round). So "retired no later
  than `idleTimeout` plus one sweep interval" (PRD requirement 5) is unchanged and `SweepPauseScenario`'s
  `removedPerSweep == 65,536` tripwire keeps holding.
- **Work per hold is bounded and countable.** Scan hold ≤ `SweepChunkEntries` examinations, removal hold
  ≤ 1 removal, so the expiry work a concurrent `TryResolve` queues behind is bounded by construction.
- **The predicate sees only idle-elapsed candidates**, at most once per candidate per hold, and is
  invoked **without any table lock** — the store/tombstone locks it acquires internally
  (`TcpProxyCoordinator.HoldsFlow:468`) are no longer nested inside the flow table's `_gate`. The nested
  edge disappears; the documented repo-wide order (store → redirect table → tombstone,
  `TcpRedirectSessionStore.cs:25-27`) is no longer extended by an outer frame.
- **A hold never re-arms activity** (unchanged): a held entry keeps its original idle point and is
  re-examined next round.
- **No double removal.** The removal hold re-checks identity at the cursor, still-idle-elapsed, and
  `_states.Remove(key, out _)` success before touching the registry.
- **Concurrent sweeps are serialized** by `_sweepGate` (exactly-once `ReturnState`); lock order
  `_sweepGate` → `_gate`, acyclic.
- **Mid-round appends** (a claim while the gate is released) land at indices ≥ `_liveCount` > cursor and
  are examined next round; a state claimed mid-round cannot be idle-elapsed.
- **Exceptions** behave as today: a throwing `isHeld` aborts the round with no lock held and no partial
  bookkeeping; the next call starts a fresh round (`IdleExpirySweeper` isolates and rate-limits the
  failure, `IdleExpirySweeper.cs:115-120`, `:130-133`).
- **Its own duration is the accepted cost.** The pathological all-expired round measures ~110–115 ms
  against the before-series' 26.2 ms, because every removal takes its own gate hold. That is report-only
  and deliberate; the duty cycle at the shipped 60 s main cadence is 0.18–0.19 %, inside the PRD's 0.5 %
  line, and the production-shaped round (4,096 live / 32 idle-elapsed) measures 0.033–0.036 ms
  (≈ 0.00006 %).

### 2.3 Acceptance: work per hold, not wall-clock (D-A), and the honest trade (D-C)

The first implemented pass (minimal granularity) moved `maxSweepWindowPauseMs` from 92.6/36.4/44.8 ms to
4.4–6.8 ms and the warm-path throughput (`sweepWindowResolves`) from 6.5–10.4 k to **13.1–14.1 M** per
15 s window — but the 0.5 ms line was still missed, and a **control run with the window armed ~120 ms and
no product call at all** measured `maxSweepWindowPauseMs` 5.19 ms with 12,336 in-window overshoots: the
in-window maximum is host scheduling and lock queueing, not hold length (the same code with one observer
measured 21 in-window overshoots of 4.93 M resolves). Wall-clock therefore cannot be the acceptance
figure.

The acceptance property is **countable**: a scan hold inside `FlowTable.RemoveExpired` examines at most
`SweepChunkEntries` entries, and a removal hold removes **exactly one** entry. It is pinned by the
`HoldProbe` sink + two Release-mode probe facts (§6.1) — the 65,536-flow acceptance fact and the
production-shaped round — and every timing number stays a series.

The honest trade, recorded in the artifact README (D-C): the sweep's **own duration** is report-only and
deliberately poor in the pathological shape — ~110–115 ms for an all-expired 65,536-flow round against
the before-series' 26.2 ms, because every removal takes its own gate hold. The alternatives were measured
and rejected: 32 removals per hold ~1.06–1.10 M in-window at ~55 ms, 256 removals per hold 89 k–120 k
in-window at ~35 ms. Hold granularity, not acquisition count, is what keeps warm-path progress, so the
task buys `sweepWindowResolves` 6.5–10.4 k → 13.1–14.1 M per 15 s window with the duration it does not
need. The duty cycle at the shipped 60 s main cadence stays inside the PRD's 0.5 % line: 0.18–0.19 % for
the pathological round, 0.00006 % for the production-shaped one (4,096 live / 32 idle-elapsed,
0.033–0.036 ms).

### 2.4 Implemented shape and deviations (for the archive)

Landed: the registry + the **minimal-granularity** chunked round, with these recorded deviations and
resolution notes:

- `ScanChunk` and `RemoveCandidateAt` are extracted private methods (MA0051 method-length limit):
  `ScanChunk` is the scan hold and reports its examination count (including the candidate it stops on);
  `RemoveCandidateAt` is the one-entry removal hold at the cursor.
- The **D-B batched variant was implemented and then reverted by measurement** (§2.2): `_batch`/
  `_approved`, descending-order removal, the cursor rewind and its round-completeness counterexample are
  gone; the rejected variant's runs survive as `sweep-pause-k{32,64}-diagnostic.jsonl` and its
  counterexample as a paragraph in §2.2.
- The parked-predicate concurrent-resolve test lives in `CoreFlowStructuresTests`, not
  `TcpProxyCoordinatorCapacityTests`: that harness cannot reach the dispatcher's private `FlowTable`, so
  the test composes the table directly.
- The probe tests run in **Release** (`dotnet test -c Release`): the `HoldProbe` sink is a plain
  `internal` member with a local null check, never a `[Conditional("DEBUG")]` hook.
- The control-window calibration is a documented benchmark option (`--sweep-window-control-ms <n>`,
  `benchmarks/README.md`), not a reverted temp patch.
- Measurement-side deviation: the stability runner's `--output` truncates per process, so the recorded
  before/after/control series were concatenated from per-run temp files (noted in the artifact README).

## 3. `TcpRedirectTable` (site 2)

```csharp
private readonly List<TcpRedirectAssociation> _expiredScratch = [];   // sweep-owned, reused
```

`RemoveExpired` becomes: `lock (_sweepGate)` → `_expiredScratch.Clear()` → under `_gate`, one walk of
`_byOriginal.Values` collecting idle-elapsed associations (no predicates, no removals) → outside the
gate, for each candidate, one `_gate` hold that re-checks presence + idleness and then runs exactly
today's removal body (`:326-331`, including the candidate-port decrement). `Distinct()` is deleted
(provably a no-op, `research/implementation-notes.md` §3). Zero allocation once the scratch has grown.
This method is synchronous, so its `_sweepGate` is a plain `Lock` with no `await` inside the critical
section; the async sites use `SemaphoreSlim` instead (§4–§6). The table is not on a production sweep
path; the fix exists because the PRD names the site and because the table's gate is a warm packet-path
gate.

## 4. TCP session store + tombstones (site 3)

`TcpRedirectSessionStore`:

- `private readonly SemaphoreSlim _sweepGate = new(1, 1);` — a `Lock` **cannot** be used: this method
  awaits (the disposal tail), and a `Lock`/`Monitor` must not be held across an `await`. The semaphore is
  acquired with `await _sweepGate.WaitAsync().ConfigureAwait(false)` at the top and released in a
  `finally`; uncontended, `WaitAsync()` returns a cached completed task, so the no-op tick neither
  suspends nor allocates and `IsCompletedSuccessfully` still holds (that is exactly what the site's gate
  asserts). In-repo corroboration that this shape is allocation-free: `Socks5UdpTransport` awaits its
  send gate the same way (`Socks5UdpTransport.cs:321`) under the exact 0 B gate
  `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes`.
- `private readonly List<TcpRedirectSession> _expiredScratch = [];`
- `private readonly List<(TcpRedirectSession Session, ITcpRelay? Relay)> _retiredScratch = [];`
  The `RetiredSession` record (`:11`) is **kept** — the cold callers (`DisposeCoreAsync:128`,
  `TearDownSessionAsync:161`, `TryRetireSessionUnderGate:172`) still use it — and the sweep gets an
  allocation-free capture overload `RetireSessionUnderGate(session, sink)` that writes
  (session, relay) pairs into the reused scratch; the existing record-returning overload becomes a
  thin wrapper for the cold callers. Net effect on the sweep tick: no array, no iterator, no closure,
  no record — the retirement set itself allocates nothing.
- `RemoveExpiredAsync` body, all inside the semaphore:
  - `prunePending?.Invoke(now)` first, unchanged;
  - `_expiredScratch.Clear()` at the **start of the critical section** — safe, because the semaphore
    guarantees no other caller is mid-sweep, and these are candidates that are still registered in
    `_sessions` (an aborted tick simply re-discovers them next tick);
  - under one `_gate` hold, scan `_sessions.Values` into `_expiredScratch` (today the same hold also
    *retires*, `:108-110`);
  - outside the gate, per candidate, one `_gate` hold calling the allocation-free
    `RetireSessionUnderGate(session, _retiredScratch)` (unchanged body: `Phase = Closing`, `Retire()`,
    table alias removal + tombstone inside the nested table/tombstone gates);
  - **then** the `ReleaseRetiredAsync` disposal loop (outside the gate, per entry), with per-entry
    exception containment so one failed disposal cannot skip its siblings;
  - `_retiredScratch.Clear()` **only after that loop has attempted every entry** — never at the start of
    the next tick, or an aborted tick's already-retired-but-undisposed sessions would be dropped and
    their listeners/relays stranded. `_retiredScratch` is cleared inside the same critical section that
    filled it.
- Why the semaphore and not "the store gate already serializes it": the scan and the disposal loop run
  *outside* `_gate`, so `_gate` no longer covers the scratch's lifetime, and
  `TcpProxyCoordinator.RemoveExpiredAsync` is **public** — concurrent callers are reachable, and two
  callers sharing one scratch (one clearing it while the other drains it) is a relay/listener leak.

`TcpRedirectTombstoneTable`:

- `private readonly List<TombstoneEntry> _expiredScratch = [];`; `RemoveExpired` fills it under
  `_gate`, then removes each entry and runs the existing `DrainStaleQueueHeadUnderGate` under the same
  hold (the drain is O(1) amortized and needs no chunking at a 60 s cadence and a bounded capacity).
- No sweep gate is needed here: the scratch is only ever touched under `_gate` (filled and drained in
  one hold), so the existing serialization already covers it.

`TcpProxyCoordinator` (`:457`): two per-tick delegate allocations are hoisted into readonly fields built
in the constructor:

- `_prunePendingSyn = _pendingSyn.RemoveExpired;` with the store's hook parameter changed from
  `Action?` to `Action<DateTimeOffset>?` (one caller) so the tick no longer captures `now`;
- `IdleExpirySweeper.cs:155` builds a fresh `_tcp.HoldsFlow` delegate on every tick — cache it as
  `_holdsFlow = _tcp?.HoldsFlow` in the sweeper's constructor and pass the field instead.

## 5. UDP coordinator (site 4)

- `private readonly SemaphoreSlim _sweepGate = new(1, 1);` (this method awaits the teardown, so a `Lock`
  is illegal) and `private readonly List<(UdpSessionSlot Slot, UdpProxySession Session)> _idleScratch = [];`.
- `RemoveExpiredAsync`: `await _sweepGate.WaitAsync().ConfigureAwait(false)` → `_idleScratch.Clear()` →
  `_cooldowns.PruneExpired(now)` and the scan into `_idleScratch` under one `_gate` hold (the
  `Where`/`Select`/array at `:347-349` go away) → the scratch is then indexed by count, not enumerated as
  an array → `finally { _sweepGate.Release(); }`.
- The outside-gate loop is unchanged in substance: `_beforeExpiryRecheck`, then per candidate
  `session.TryBeginExpiry(now, idleTimeout)` (the existing re-verifier, `:357`) and
  `_slotHost.RemoveSlotAsync(...)`.
- Scratch lifetime: `_idleScratch` holds **candidates that are still live in `_sessions`**, so
  clearing it at the start of the critical section is safe — an aborted tick's entries are simply
  re-discovered by the next scan, which is today's behaviour. What is *not* safe without the semaphore
  is two callers sharing the list (`RemoveExpiredAsync` is public); the semaphore, not `_gate`, is what
  makes the clear site correct.
- Uncontended, `WaitAsync()` returns a cached completed task, so a no-op tick completes synchronously
  with no allocation — the site's gate asserts exactly that via `IsCompletedSuccessfully`.

## 6. Association pool (site 5) and site 6

- `private readonly SemaphoreSlim _sweepGate = new(1, 1);` (the disposal loop awaits) and
  `private readonly List<UdpControlAssociation> _retireScratch = [];`.
- `SweepIdleAssociationsAsync` shape, stated as it will be: **one `_gate` hold covers the scan, the
  `CanRetire(now, s_idleRetireTimeout) || CanRetireFaulted` test, and every `set.Shared.Remove`** — the
  decision and the removal stay atomic, and the test reads the association's own volatiles, not another
  lock, so there is no nesting edge. Then `_retireScratch.Clear()` at the start of the critical section
  (safe: an association removed from `set.Shared` but not yet disposed is still in `set.All` and still
  retirable, so the next scan re-discovers it), and the disposal loop outside the gate, unchanged under
  the semaphore.
- Population bound for that single hold: `_servers.Values.SelectMany(set => set.All)` — `All` holds the
  shared associations (≤ `MaxAssociationsPerServerLimit`, 1,024 by default, per server) **plus** the
  private associations created when the ceiling is hit (bounded by the coordinator's session
  capacity), summed over configured servers.
- **Dropped after review (recorded as a follow-up, not part of this task):** the
  `SampleServerCapabilities` early-out. It is not an expiry sweep, no requirement names it, and it would
  add another exact window to a host-lump-exposed gate set.
- `UdpAssociationTable.RemoveExpired` (site 6): same synchronous scratch fix as site 2
  (`research/implementation-notes.md` §3), with a comment that the method has no production caller.

### 6.1 Gate shapes (exact, deterministic)

| Site | Gate (in `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs` unless noted) | Gated tick / shape | Before the fix |
|---|---|---|---|
| 1 | `FlowTableSweepAllocatesNoManagedBytes` (exists) | retiring: 4,096 expired, no predicate | green (0 B) — regression-only |
| 1 | `FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes` (new) | retiring: 4,096 expired, ~half held by **one hoisted** predicate instance, plus 1,024 live non-expired entries | **green today** (the scratch is already reused and the predicate is the caller's) — regression-only, not red-first |
| 1 | `FlowTableSweepPreservesLiveRegistryInvariant` (new, not an allocation gate) | churn; `LiveStateCountForDiagnostics == Count`; every expired state removed within one call | n/a |
| 1 | **`FlowTableSweepHoldWorkIsBoundedByChunkEntries`** (new, **the acceptance fact**, D-A) | 65,536 seeded idle-elapsed flows, 4,096 observer-equivalent held flows, a `HoldProbe` attached: assert `MaxExaminations <= SweepChunkEntries`, **`MaxRemovals <= 1`**, `removed == 65,536`, `Count == 4,096` (the held set survives; a second released call takes it to 0), and record `ScanHolds` / `RemovalHolds` / both histograms — 69,632 scan holds × 1 examination, 65,536 single-entry removal holds + 4,096 zero-removal held holds. Counts only — no timing. Works in Release (a plain `internal` sink + a local null check, no `[Conditional("DEBUG")]`) | n/a (new property) |
| 1 | **`FlowTableProductionShapeSweepRecordsItsHoldShape`** (new, PRD AC-3) | 4,096 live / 32 idle-elapsed flows, a `HoldProbe` attached: 48 scan holds (16 full `SweepChunkEntries`-entry live holds, then 1 per idle entry), 32 single-entry removal holds, `MaxExaminations == SweepChunkEntries`, `MaxRemovals == 1`, `removed == 32`, `Count == 4,096`. The round's duration (0.033–0.036 ms measured, duty cycle ≈ 0.00006 % at the 60 s cadence) is recorded in the artifact README and **not** asserted | n/a (new property) |
| 2 | `TcpRedirectTableSweepAllocatesNoManagedBytes` (new) | retiring: 4,096 claimed associations, all idle-elapsed | red today |
| 3 | `TcpRedirectSessionStoreSweepAllocatesNoManagedBytes` (new) | no-op over a populated store (registered, `Redirecting`) with unexpired tombstones | red today |
| 4 | `UdpProxyCoordinatorSweepAllocatesNoManagedBytes` (new) | no-op over a populated session set (fake transports) | red today |
| 5 | `UdpAssociationPoolSweepAllocatesNoManagedBytes` (new) | no-op over a populated pool whose shared associations have outstanding leases (`CanRetire == false`) | red today |
| 6 | `UdpAssociationTableSweepAllocatesNoManagedBytes` (new) | retiring: 64 claimed associations, all idle-elapsed | red today |

The predicate fact mandates **exactly one delegate instance**, hoisted out of the measured window and
shared by the probe and measured calls — the same discipline `HotPathAllocationGateTests.cs:435-436`
documents ("a per-call-site lambda would allocate its cached delegate on its own first invocation,
inside the measured window"), not a comment in `SweepAllocationGateTests.cs`.

Every gate follows the established shape (`hot-path.md` §"Allocation-gate stability", `:838-1005`): a
bounded probe loop that must observe an **exactly zero** per-thread delta first, then one measured
window on the same managed thread, then `Assert.Equal(0, allocated)`. Each fact additionally carries
the four-property window contract from that section (`:912-917`):

1. the driven operation **completed synchronously** — `IsCompletedSuccessfully` on the `ValueTask<int>`
   of the three async sites. For a no-op tick this doubles as the proof that the sweep never awaited
   (a future change that makes the empty tick suspend turns the gate red, not silently green);
2. `Environment.CurrentManagedThreadId` captured before the window and asserted unchanged after it;
3. the exact `Assert.Equal(0, allocated)` (never a threshold);
4. a thread-independent call-count backstop — the sweep's returned removed count for a retiring tick,
   the populated-world count (`AssociationCount`, session count, store count) for a no-op tick.

Assertions stay outside the window (`Assert.Equal` allocates ~200–300 B; the boolean `Assert.True`
form does not). Each fact is re-discriminated once after its window changes: inject one allocation
inside the window, record the exact failing byte count, restore. The gate host runs with
`TieredCompilation=false` (`WinForward.Core.Tests`), and a residual host lump can still fail an exact
gate spuriously at a low rate (`hot-path.md:870-881`). The accepted stability evidence is therefore the
spec's **per-gate process run** (`hot-path.md:1157-1181`): 20 runs per gate, one gate per filter, the
residual signature predicate `168|5216|7384|7448`, recording revision, tree fingerprint, expected
totals and every run's padded summary — the suite-level loop is explicitly *not* the criterion.

**Timing facts are reclassified (D-A).** `maxSweepWindowPauseMs`, `maxPauseMs`, `pausesOver500us` and
`sweepMeanMs` are series/diagnostics, not acceptance figures: the control run that armed the in-window
flag for ~120 ms with **no product call at all** still measured 5.19 ms and 12,336 in-window overshoots —
the same order as the fixed code — so the in-window maximum measures host scheduling and lock queueing
rather than hold length. **The sweep's own duration is deliberately spent**: minimal hold granularity
measured `sweepMeanMs` 110.0–114.7 ms (against the before-series' 26.2 ms) and was chosen over the
batched variants precisely because hold granularity decides warm-path progress (§2.2/§2.3). The
user-visible series is `sweepWindowResolves` (6.5–10.4 k → 13.1–14.1 M per 15 s window, above the PRD's
≥1 M line); `removedPerSweep == 65,536` stays an exact scenario tripwire. The two `HoldProbe` facts above
are the acceptance evidence.

The existing `AllocationProbeSeesAKnownAllocation` companion proves the probe can see an allocation;
the new gates reuse it, and each red-first gate records its exact red byte count when first run (its
site step in `implement.md`).

`FlowTableSweepAllocatesNoManagedBytes` will allocate nothing on the *first* probe sweep after the
change (the scratch it stabilised is gone), so its loop converges immediately; it and the predicate
variant stay as regression gates for site 1 — neither can be red-first, because an unmodified
`FlowTable` already sweeps at exactly 0 B once `_expiredScratch` has stabilised.

## 7. Contracts

### Public — unchanged

- `FlowTable.RemoveExpired(DateTimeOffset, TimeSpan, Func<FlowKey,bool>?) → int`,
  `FlowTable.TryResolve/TryClaimResolved/Count/Capacity`, `FlowState.Touch/LastActivityUtc/Reset`.
- `TcpRedirectTable.RemoveExpired`, `TcpRedirectSessionStore.RemoveExpiredAsync`,
  `UdpProxyCoordinator.RemoveExpiredAsync`, `UdpAssociationPool.SweepIdleAssociationsAsync`,
  `UdpAssociationTable.RemoveExpired` — same names, parameters, return meanings.
- Driver-facing semantics: nothing on the wire changes. No injection, rewrite, checksum, route,
  disposition, capacity or fail-closed behaviour is touched; the sweep still runs on the same
  `IdleExpirySweeper` cadence (`DeriveUdpSweepInterval` is untouched).

### New internal members

| Member | Purpose |
|---|---|
| `FlowTable._liveStates/_liveCount` | the registry (§2.1) |
| `FlowTable._sweepGate` (`Lock`) | sweep-level single flight at site 1 and the two synchronous table sites |
| `FlowTable.LiveStateCountForDiagnostics` | test/diagnostic read of `_liveCount` under `_gate`, precedent `TcpRedirectTombstoneTable.QueueCountForDiagnostics:48` |
| `FlowTable.HoldsGateForDiagnostics => _gate.IsHeldByCurrentThread` | the exact proof that no predicate runs under the table lock (§8) |
| `FlowTable.SweepChunkEntries` (`internal const 256`) | the one constant that bounds both hold kinds; read by the probe test |
| `FlowTable.HoldProbe { get; set; }` + `FlowTable.SweepHoldProbe` | D-A's diagnostics sink: per-round hold counts, max examinations/removals and both histograms; settable, read once per round into a local, null in production (precedent: `QueueCountForDiagnostics`, `HoldsGateForDiagnostics`) |
| `SemaphoreSlim(1,1)` sweep gates at sites 3, 4, 5 | async-safe single flight across each whole method; uncontended `WaitAsync()` is allocation-free and non-suspending |
| `TcpRedirectSessionStore.RemoveExpiredAsync`'s hook parameter `Action<DateTimeOffset>? prunePending` | lets the coordinator cache the delegate instead of allocating a closure per tick |
| `IdleExpirySweeper._holdsFlow` | cached `_tcp?.HoldsFlow` instead of a fresh delegate per tick (`IdleExpirySweeper.cs:155`) |
| scratch fields on sites 2–6 | reused retirement sets |

### Semantics preserved, explicitly

- A flow / session / association is still retired no later than `idleTimeout` plus one sweep interval
  after its last activity (each call completes one round; each tick calls once).
- A hold still skips the entry **without** touching its activity, so it expires at its original idle
  point once the hold lapses.
- The predicate is still consulted only for idle-elapsed candidates.
- `Count`/`Capacity` keep their meaning; the `_liveStates` registry is a mirror of `_states`, never an
  authority.
- Fail-closed behaviour (capacity refusal, alias-collision refusal, teardown reason bookkeeping) is
  untouched.

### Accepted semantic deltas

1. The hold predicate is evaluated without the table gate, so between "predicate said not held" and
   "remove under the gate" a hold can appear. That window already exists today (the store's `Holds`
   uses the store gate, not the flow gate, so its answer is stale by the time the removal runs); the
   change widens it from sub-microsecond to at most one chunk. Consequence is bounded: the flow table
   holds a *decision cache*, the redirect table remains authoritative for redirect routing, and a
   re-claimed tuple arrives as a new generation, which the hold contract already documents
   (`TcpProxyCoordinator.cs:465-466`).
2. The predicate can be called more than once for the same entry across ticks, and (with a swap) an
   entry can be examined twice in one round — a call-count change only; there is no observable effect
   because the predicate is required to be pure (it is: `HoldsFlow` is two dictionary probes).
3. `FlowTable.RemoveExpired` takes longer in total wall time: every removal is its own gate hold, so the
   pathological all-expired round measures ~110–115 ms against the before-series' 26.2 ms. Accepted and
   deliberate (D-C): it buys 13.1–14.1 M in-window resolves per 15 s window, the duty cycle stays inside
   0.5 % at the shipped cadence, and the round's *result* is unchanged.

## 8. Proving the requirements

| PRD requirement | Proof | Class |
|---|---|---|
| 1 — the expiry work a warm resolve can queue behind is bounded | **counts, not wall-clock (D-A)**: `FlowTableSweepHoldWorkIsBoundedByChunkEntries` asserts every hold inside `RemoveExpired` at 65,536 idle flows exams ≤ `SweepChunkEntries` and removes ≤ **1** entry, records the hold counts and both histograms (69,632 scan holds × 1 examination, 65,536 single-entry removal holds + 4,096 zero-removal holds for the held set), and proves round completeness (`removed == 65,536`); `FlowTableProductionShapeSweepRecordsItsHoldShape` records the production shape (4,096 live / 32 idle → 48 scan holds, 32 removal holds, max 256 examinations / 1 removal, 0.033–0.036 ms report-only) | exact gate (counts) + one report-only duration |
| 1 (diagnostics) — user-visible effect and the honest trade | instrumented `--scenario sweep` series: `sweepWindowResolves` 6.5–10.4 k → **13.1–14.1 M** per 15 s window (3 runs); `maxSweepWindowPauseMs` 92.6/36.4/44.8 ms → 6.78/4.40/6.23 ms; `sweepMeanMs` 26.2 → 110.0–114.7 ms (report-only, D-C); the rejected batched variants' runs (`sweep-pause-k{32,64}-diagnostic.jsonl`) and the **control run with no product call** (5.19 ms, 12,336 in-window overshoots) are quoted beside every timing figure as the calibration limit; `maxPauseMs`/`pausesOver500us` stay report-only, normalized per sweep, with the ≥96 % refill attribution (`SweepPauseScenario.cs:165`) | series (report-only) |
| 2 — no allocation on the gated tick per site | the gate matrix in §6.1 — retiring tick for sites 1, 2, 6; no-op tick over a populated world for sites 3, 4, 5; exact `GC.GetAllocatedBytesForCurrentThread` deltas, `IsCompletedSuccessfully`, pinned thread id, call-count backstop | exact gate |
| 3 — no holds-flow predicate under a table lock | `SweepHoldPredicateRunsOutsideTheTableGate`: a predicate that asserts `!table.HoldsGateForDiagnostics` (new internal read of `Lock.IsHeldByCurrentThread`) is passed to a sweep over a table with idle-elapsed entries, and the returned count proves the predicate actually ran; plus a two-thread test where a resolve completes while the predicate is parked on a `ManualResetEventSlim` | exact gate |
| 4 — no warm-path regression | `HotPathAllocationGateTests.FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` (0 B claim+touch+expire) unchanged; `FlowTableProductionShapeBenchmarks` re-run for `ResolveSameOrientationHit` / `ResolveReverseAliasHit` / `ReadActivityClock` (unchanged code path — `Touch` is byte-identical); the only warm-path edit is the registry append in `TryClaimResolved`, and `flowTable.claim` (192.02 B / 1,993 ns) is the recorded series. F3.4's bucket stays with F2 (contract in `research/implementation-notes.md` §8) | exact gate + series |
| 5 — expiry semantics | existing boundary tests (`CoreFlowStructuresTests.cs:82-140`, `:189-200`; `HotPathAllocationGateTests.cs:463-478`; `TcpProxyCoordinatorCapacityTests.cs:359-401`; `UdpAssociationPoolTests.cs:169-191`; `TcpRedirectTombstoneTableTests.cs`; `FlowDispatcherTests.cs:100-116`) stay green, plus new round-completeness/churn tests | exact |
| 6 — existing gates green + the `_liveStates` delta recorded | full suite + format + inspectcode + the gc-soak/udp scenarios (Windows-only rows stay with `windows-real-nic`); the registry's construction cost measured and written into the artifact README (§9) | exact/series as today |

## 9. Measurement

Artifacts land in `benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/`:

- `sweep-pause-before.jsonl` — unmodified **product** HEAD with the **instrumented** scenario already in
  the tree (Step 1 of `implement.md`): the instrumentation must land first, or before and after are not
  comparable and the raw series cannot show the win at all.
- `sweep-pause-after.jsonl` — the same instrumented scenario, 3 runs, with the landed minimal-granularity
  round. The acceptance figure is **not** here: `maxSweepWindowPauseMs` and the raw series are diagnostics
  (the 0.5 ms line is not attainable on this host — see the control below). Measured:
  `sweepWindowResolves` 13,251,906 / 14,070,168 / 13,145,035; `sweepMeanMs` 111.7 / 110.0 / 114.7
  (report-only, D-C); `removedPerSweep` 65,536; `sweepAllocatedBytesPerSweep` 0.
- `sweep-pause-control.jsonl` — the calibration run, reproducible with `--sweep-window-control-ms 120`:
  the in-window flag armed for ~120 ms with **no product call at all** (4 observers), which measured
  `maxSweepWindowPauseMs` 5.19 ms and 12,336 in-window overshoots. Every timing figure is quoted against
  this floor.
- `sweep-pause-1observer.jsonl` — the same code with one observer: 21 in-window overshoots of 4.93 M
  resolves (0.0004 %), the evidence that the 4-observer overshoots are scheduling, not hold length.
- `sweep-pause-k64-diagnostic.jsonl`, `sweep-pause-k32-diagnostic.jsonl` — the **rejected batched
  variants** (temporary `SweepChunkEntries` overrides, reverted): 32 removals per hold 1.06–1.10 M
  in-window at 55–56 ms, 64 removals per hold 393 k at 47 ms. Kept so the reversal is auditable.
- `README.md` — host/runtime header, command lines, the **measured before/after/control/1-observer table**,
  the per-hold histogram (`ScanHolds` / `RemovalHolds` / `MaxExaminations` / `MaxRemovals` + both
  histograms) from the acceptance probe fact at 65,536 flows, the **production-shape row** (4,096 live /
  32 idle: 48 scan / 32 removal holds, 0.033–0.036 ms, duty cycle ≈ 0.00006 %), the **trade table**
  (granularity 1 / 32 / 256 → in-window resolves, `sweepMeanMs`) naming the two rejected values as measured
  dead ends, the honest trade statement (D-C: the sweep's own duration is report-only and deliberately
  ~110–115 ms for the pathological round; the user-visible win is `sweepWindowResolves` 6.5–10.4 k →
  13.1–14.1 M), the per-site gate byte table (red → 0; site 1's facts marked regression-only), the raw
  series quoted normalized per sweep with the attribution arithmetic and the control floor, the
  `_liveStates` memory delta (measured with `GC.GetTotalAllocatedBytes(precise: true)` around
  `new FlowTable(65_536)`, ≈ +512 KiB, optionally corroborated by the residency census's flow-table
  stage), the honest note that the site-4 scan hold reaches ~0.5–1.3 ms at 16,384 sessions on the warm
  datagram gate (`udp-ready-path-contention` as the trigger), and the F3.4 `ReadActivityClock` baseline.

  **Note on the runner:** `--output` truncates per run, so multi-run series are concatenations of
  per-run temp files (how the recorded 3-run artifacts were built); state that in the README rather than
  implying a single invocation appended them.

## 10. Risks

| Risk | Attack surface | Mitigation / disposition |
|---|---|---|
| A `FlowState` returned to `_freeStates` while still in `_liveStates` → two flows share one state | the one invariant added to a hot, pooled type | single removal funnel (sweep only) + swap-remove strictly before `ReturnState` + `[Conditional("DEBUG")]` slot assertion + `LiveStateCountForDiagnostics == Count` churn test |
| Two concurrent `RemoveExpired` callers interleave rounds → double `ReturnState`, wrong counts | `_sweepGate` is new; nothing else enforces single flight once the round releases `_gate` | `_sweepGate` wraps the whole round (lock order `_sweepGate` → `_gate`, acyclic); a concurrency test asserts `removed(A) + removed(B) == N` and `Count == 0` |
| **Batched removal holds cost warm-path progress** (the D-B variant) | it was implemented and measured: 32 removals per hold 1.06–1.10 M in-window at ~55 ms, 256 removals per hold 89 k–120 k at ~35 ms, against 13.1–14.1 M for one removal per hold | reverted to minimal granularity: hold granularity, not acquisition count, decides how much warm-path progress survives a sweep; the rejected runs are kept as `sweep-pause-k{32,64}-diagnostic.jsonl` |
| **Batched phase 3 without the cursor rewind loses states** (rejected variant only) — reproduced in code: N=65,536 all idle-elapsed, K=256 → `removed = 32,768`, 32,768 idle-elapsed states still live | the D-B variant's phase 3; the scenario tripwire would throw, a production sweep would halve its retirement rate | the landed shape removes at the cursor, so no rewind exists to drop; the round-completeness test (`FlowTableSweepRemovesEveryIdleFlowInOneRound`) and the scenario tripwire stay green. The rejected variant's rewind rule is recorded in §2.2 |
| An unexamined element is left below the cursor by a removal (retirement slips a round) | the cursor must always advance past examined work | removes at the cursor and swap-writes the tail onto it, so the swapped-in element is examined next; pinned by `FlowTableSweepRemovesEveryIdleFlowInOneRound` (65,536 in one call) and the churn test |
| The removed `_batch`/`_approved` scratch would have grown inside the sweep (allocation under the gate) — no longer applicable | the landed shape has no batch scratch | nothing to grow: the landed round records no per-chunk collections, and the 0 B sweep gate covers it |
| The round re-enters the scan after the last removal and re-reads the stale slot at `cursor == _liveCount` → the same state returned to `_freeStates` twice | the loop bound | the loop carries **both** bounds (`cursor < roundLimit && cursor < _liveCount`); the removal branch additionally requires `_states.Remove(...)` to succeed before returning the state; pinned by an all-expired sweep followed by a claim/expire cycle test |
| The predicate-outside-gate window admits a hold that appears between predicate and removal | PRD requirement 3 forces the predicate out of the lock | pre-existing race class (store gate ≠ flow gate today); re-verify idleness under the gate; a re-claimed tuple is a new generation by contract (`TcpProxyCoordinator.cs:465-466`); document at the call site |
| `SweepChunkEntries = 256` turns out too coarse | it bounds the scan hold's examinations | the count bound is independent of host speed; with one removal per hold the constant is a scan bound only and cannot change the round's total work or its removal granularity (§2.2) |
| The in-window timing reading is mistaken for a hold measurement again | the measured 4.4–6.8 ms exceeded the 0.5 ms line while the control (no product call) measured 5.19 ms | timing facts are classified as series/diagnostics in §6.1 and §8; the acceptance evidence is the `HoldProbe` counts; every timing table in the artifact README carries the control run beside it |
| The async sweep gates (`SemaphoreSlim`) suspend on a contended tick, so a gate could measure a non-synchronous path | sites 3–5 are single-flight in production; the gate drives the uncontended shape and asserts `IsCompletedSuccessfully` | the gate's `IsCompletedSuccessfully` + pinned thread id fail loudly if a change ever makes the uncontended tick await; the fast path returns a cached completed task (0 B), corroborated in-repo by `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` over `Socks5UdpTransport.cs:321` |
| Sweep wall time grows and the 60 s/15 s cadence overlaps | **measured and accepted**: minimal hold granularity costs 26.2 → 110.0–114.7 ms mean for the pathological all-expired round (80–83 sweeps per window vs 139/147/139), bought deliberately for 13.1–14.1 M in-window resolves | the duration is report-only (D-C); the duty cycle at the shipped 60 s main cadence is 0.18–0.19 % for that round and ≈ 0.00006 % for the production shape (4,096 live / 32 idle, 0.033–0.036 ms), inside the PRD's 0.5 % line. Main legs are gated to ~60 s of a 15 s tick and the `PeriodicTimer` coalesces, so a slower sweep delays only the next tick. Batching the removals to shorten it was measured and rejected (§2.2) |
| Sites 2–6 scan holds stay O(live population) | the bounded-hold contract is scoped to the 65,536-flow table (the 0.5 ms line is historical, §2.3), but site 4's gate is the warm datagram gate | the ~0.5–1.3 ms estimate at 16,384 sessions is recorded in the artifact; the extension (live-slot cursor over the primary index) is mechanical if `udp-ready-path-contention` or a later measurement asks for it |
| The async sites' no-op gate is vacuous (nothing populated) | the gate could pass because there is nothing to iterate | gate shapes require a **populated** world (§6.1) and the existing discriminating probe proves the instrument sees allocations |
| The `_liveStates` registry costs ≈ +512 KiB at 65,536 capacity | site 1's memory shape is capacity-dominated and the A4 task will re-measure it | measured as a construction delta and recorded in the artifact README (PRD requirement 6); it replaces a scratch list that grows to ≈6 MB of `FlowKey` slots on the first large sweep, so the net at capacity is negative |
| `Dictionary` enumeration cost is a high-water mark, not the live count | could silently make the "small population" sites expensive | the registry at site 1 removes the dependence there; sites 2–6 keep their bounded populations and are report-only |
| Test-only sweep APIs (`TcpRedirectTable`, `UdpAssociationTable`) drift from the production shape | their fixes are not exercised by any production path | gates land with the fix (same commit); a comment on each method states it has no production caller |

## 11. Rollback shape

Rollback is per site, and each step leaves a green tree (see `implement.md`):

| Step | Revert restores |
|---|---|
| FlowTable minimal-granularity chunked sweep | the O(N) single-hold sweep with the predicate nested under `_gate` (`removedPerSweep`/`Count` semantics unchanged; the warm-path starvation returns). Reverting to a *batched* removal shape is not a rollback option — it was measured and rejected (§2.2) |
| TCP redirect table scratch | `Where/Distinct/ToArray` under `_gate` |
| TCP session store + tombstones + both delegate hoists | LINQ collection expressions + the per-tick closure and per-tick `HoldsFlow` delegate |
| UDP coordinator scratch | LINQ collection expression + array under `_gate` |
| Pool scratch | `SelectMany/Where` list under `_gate` |

No configuration, no schema, no wire change: reverting is a source revert plus dropping the
corresponding gate facts (or leaving them red-capable only for the reverted site, which is why gates
land in the same commit as their fix).
