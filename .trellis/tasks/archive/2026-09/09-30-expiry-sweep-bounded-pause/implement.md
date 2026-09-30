# Implementation plan — F3 expiry sweeps: bounded pause, zero allocation under locks

Five independently revertible code steps (one per site group), one instrumentation step that must land
first, and evidence/spec work. Each site step lands its gate **and** its fix in the same commit, so every
commit leaves a green, shippable tree; the red byte numbers are captured transiently during the step and
recorded in the artifact README. Run the full gate set at every rollback point.

Prerequisites already true: `dotnet` on PATH via direnv, `rg`, and the archived baselines under
`benchmarks/results/2026-09-29-benchmark-coverage/`.

**Step status (archive reconciliation, 2026-09-30).** Steps 0–7 all landed and were independently
verified (check report in dispositions 25–33; full suite 1,013 green, per-gate proof 80/80). Every
deviation from the text below — the D-B batching reversal, the reverted algorithm, the site-2/6
treatment, the dropped sampler early-out, the artifact renames — is recorded in the **Review
dispositions** table at the end; read it beside the checkboxes. The two hand-off items (the F2
representation-contract note and the parent backlog row) are discharged by the operator when this task
is archived: the parent's child map gains the F3 row and the F2 row carries the pointer to
`research/implementation-notes.md` §8.

## Step 0 — Preflight

- [x] Re-verify the anchors in `research/implementation-notes.md` §2–§6 against the current source
      (they were read at planning time; confirm no drift in `FlowTable.cs`, `TcpRedirectTable.cs`,
      `TcpRedirectSessionStore.cs`, `TcpRedirectTombstoneTable.cs`, `TcpProxyCoordinator.cs`,
      `UdpProxyCoordinator.cs`, `UdpAssociationPool.cs`, `UdpAssociations.cs`,
      `IdleExpirySweeper.cs`).
- [x] Record the F3.4 baseline for the deferred work:
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short`
      (expect `ReadActivityClock` ≈ 40 ns; `ResolveSameOrientationHit` has a ±26 % short-job spread —
      quote three runs or a full job).
- [x] Baseline the suite and record the current test count (`dotnet test WinForward.slnx -c Release`),
      so a green tree is proven before the first edit.

## Step 1 — Sweep-scenario phase instrumentation (lands first), before-artifact, gate scaffolding

The raw `maxPauseMs` / `pausesOver500us` series cannot show this win: the scenario's own 65,536
individual `TryClaimResolved` refill calls per round contend on the same `_gate`
(`SweepPauseScenario.cs:165`), which accounts for ≥96 % of the recorded 21,252 pauses over 500 µs
(research/implementation-notes.md §11/D10). The instrumented scenario must therefore exist **before**
the before-artifact is captured, so before and after are the same instrument.

- [x] `benchmarks/WinForward.Benchmarks/Stability/SweepPauseScenario.cs`: add a volatile in-sweep flag
      armed immediately around the `RemoveExpired` call (`:172`, cleared right after) and have each
      observer record, for the resolves it starts while the flag is set, an in-window maximum and the
      in-window counts over 0.1 / 0.5 / 1 / 5 ms. New row metrics: `maxSweepWindowPauseMs`,
      `pausesInWindowOver500us`, `sweepWindowResolves` (must be non-zero and is reported so a vacuous
      window is visible), alongside the existing report-only raw fields. Keep the raw fields and the
      scenario's existing tripwires (`removed != sweptKeys.Length`) untouched.
- [x] Update the scenario's XML doc + the row's `note` to state the attribution rule (in-window =
      resolve started while the sweep held the gate) and that `maxSweepWindowPauseMs` is a
      **phase-scoped diagnostic** while the raw series is report-only and normalized per sweep. (The
      measurement later showed even the in-window maximum is host-dominated — finding 14 — so no timing
      field is an acceptance figure; the note must not claim otherwise.)
- [x] `mkdir -p benchmarks/results/2026-09-30-expiry-sweep-bounded-pause`, then capture the
      **instrumented before** series at unmodified product HEAD:
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario sweep --quick --tcp-concurrency 4 --output benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/sweep-pause-before.jsonl`
      (15 s per run, 3 runs appended; expect `maxSweepWindowPauseMs` ≈ 39–41 ms,
      `sweepWindowResolves` in the thousands, `sweepAllocatedBytesPerSweep` 0). Quote the 09-29 raw
      values beside it as the pre-instrumentation reference.
- [x] Start `benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md` with the host/runtime
      header, the command lines, the attribution arithmetic, and the empty per-site byte table.
- [x] Test scaffolding for the gates: extend `SweepAllocationGateTests`'s class doc comment (the flow
      table is no longer the only site that satisfies F3.3), and re-read `hot-path.md`
      §"Allocation-gate stability" (`:838-1181`) to fix the shape every fact will use — four
      window-contract properties (synchronous completion via `IsCompletedSuccessfully` on the
      `ValueTask<int>` sites, unchanged managed thread id, the exact `Assert.Equal(0, allocated)`,
      a thread-independent call-count backstop), bounded exactly-zero probe batches before the window,
      and every assertion outside it.
- [x] Run the pre-existing gates alone to pin the baseline:
      `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"`
      and `...~FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`.

Validation for the step: the instrumented scenario builds and both old and new metrics appear in the
row; the before-artifact exists with a non-vacuous `sweepWindowResolves`; the pre-existing gates green.
The scenario change is instrumentation only — no product code is touched in this step.

Rollback: revert the scenario and delete the before-artifact (nothing else depends on it yet).

## Step 2 — Site 1, `FlowTable`: registry + minimal-granularity chunked round + the work-per-hold probes — **rollback point A**  [LANDED]

Three passes were measured at this site before the shape was settled (all runs in
`benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/`):

1. registry + one-removal-per-hold chunked round: fixed the warm-path starvation
   (`sweepWindowResolves` 6.5–10.4 k → 11.6–14.2 M) but the in-window wall-clock line was still missed;
2. **D-B batched removals** (`SweepChunkEntries`-sized holds, descending order, cursor rewind): implemented,
   probed, and **rejected by measurement** — 256 removals per hold measured 89 k–120 k in-window resolves at
   35–37 ms `sweepMeanMs`, i.e. ~1/120 of pass 1's warm-path progress, with a worse report-only pause tail
   (49.8–58.7 ms vs 4.4–6.8 ms); 32 removals per hold measured 1.06–1.10 M at 55–56 ms;
3. **landed: minimal hold granularity** (one removal per hold, at the cursor, no rewind). Measured
   13.1–14.1 M in-window resolves per 15 s window at 110.0–114.7 ms `sweepMeanMs` (report-only, D-C).

- [x] `src/WinForward.Core/FlowTable.cs`:
      - [x] `_liveStates` (pre-sized `new FlowState[capacity]` in the ctor), `_liveCount`, `_sweepGate`;
            `_expiredScratch` deleted; append in `TryClaimResolved` after `_states.Add` +
            `AddToTransportIndex`; `LiveStateCountForDiagnostics` (gate-taking) and
            `HoldsGateForDiagnostics`; no batch scratch (the D-B `_batch`/`_approved` were removed with the
            reverted variant);
      - [x] `RemoveExpired` per `design.md` §2.2: scan hold (≤ `SweepChunkEntries` examinations, cursor past
            every still-live entry, stopping **on** the first idle-elapsed candidate), the predicate with no
            table lock held, then **one removal hold that removes exactly one entry at the cursor** with the
            per-removal re-check (`cursor < _liveCount`, `ReferenceEquals(_liveStates[cursor], candidate)`,
            still `<= cutoffTicks`, `_states.Remove(key)` success). Both loop bounds kept
            (`cursor < roundLimit && cursor < _liveCount`); removing at the cursor means the swapped-in tail
            is examined next, so **no rewind exists**;
      - [x] `internal const int SweepChunkEntries = 256;` (the probes read it) with the scan-bound doc, and
            the boundary compare in the implemented, test-pinned form: skip when `UtcTicks > cutoffTicks`,
            expired when `<= cutoffTicks` (`CoreFlowStructuresTests.cs:96-97`, `:116-117` pin the semantics);
      - [x] `ScanChunk`/`RemoveCandidateAt` extracted (MA0051): `ScanChunk` is the scan hold and reports its
            examination count **including the candidate it stops on**; `RemoveCandidateAt` is the one-entry
            removal hold at the cursor;
      - [x] diagnostics sink: `internal sealed class SweepHoldProbe` (per-round `ScanHolds`, `RemovalHolds`,
            `MaxExaminations`, `MaxRemovals`, `int[SweepChunkEntries + 1]` examination and removal
            histograms, `Reset()`) plus `internal SweepHoldProbe? HoldProbe { get; set; }`, read once per
            round into a local and null-checked **per hold** (a local null check, no allocation, no
            `[Conditional("DEBUG")]` — the probes run in Release);
      - [x] XML doc on `RemoveExpired`: the predicate runs **without the table gate** and is still consulted
            only for idle-elapsed candidates; one call completes one round; minimal hold granularity and the
            probe contract.
- [x] `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs`: **`FlowTableSweepHoldWorkIsBoundedByChunkEntries`**
      — the acceptance fact (D-A). 65,536 seeded idle-elapsed flows plus a 4,096-flow held observer set, the
      predicate a single hoisted instance, a `HoldProbe` attached; assert `MaxExaminations <=
      SweepChunkEntries`, **`MaxRemovals <= 1`**, `removed == 65,536`, `Count == 4,096` with
      `LiveStateCountForDiagnostics` equal to it, and record `ScanHolds` / `RemovalHolds` / both histograms
      (measured: 69,632 scan holds × 1 examination, 65,536 single-entry removal holds, 4,096 zero-removal
      held holds); a second released call takes `Count` to 0. No timing assertion anywhere in the fact.
- [x] `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs`:
      **`FlowTableProductionShapeSweepRecordsItsHoldShape`** — the PRD's production-shape measurement: 4,096
      live / 32 idle-elapsed flows, a probe attached, counts asserted (48 scan holds, 32 removal holds, max
      256 examinations / 1 removal), duration **recorded in the artifact README, not asserted**
      (0.033–0.036 ms; duty cycle ≈ 0.00006 % at the 60 s cadence).
- [x] `tests/WinForward.Core.Tests/CoreFlowStructuresTests.cs`: `FlowTableSweepCompletesOneRoundWithClaimsInterleaved`
      (claim mid-round via a predicate callback; note for the record: on the **unmodified** tree this shape
      fails with `InvalidOperationException` — the pre-fix failure mode, not a registry-invariant assertion);
      and `FlowTableSweepRemovesEveryIdleFlowInOneRound` (65,536 all idle-elapsed, one call, `removed == N`,
      `Count == 0`) — the shape that caught the rejected batched variant's missing rewind.
- [x] `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs`: `FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes`
      (**regression-only, not red-first**: an unmodified `FlowTable` already swept at exactly 0 B once
      `_expiredScratch` had stabilised), `FlowTableSweepPreservesLiveRegistryInvariant`, and
      `SweepHoldPredicateRunsOutsideTheTableGate` (`!table.HoldsGateForDiagnostics`).
- [x] Parked-predicate concurrent-resolve test in `CoreFlowStructuresTests` (dedicated threads, not
      `Task.Run`: the xunit thread is a pool thread and an inlined task would park the test thread inside its
      own predicate).
- [x] Re-ran the instrumented sweep series and the calibration runs:
      - `sweep-pause-after.jsonl`: 3 runs of `... --stability --scenario sweep --quick --tcp-concurrency 4`;
        measured `sweepAllocatedBytesPerSweep == 0`, `removedPerSweep == 65536`, `sweepWindowResolves`
        13,251,906 / 14,070,168 / 13,145,035 (above the ≥1 M line), `sweepMeanMs` 111.7 / 110.0 / 114.7 —
        report-only, deliberately above the before-series' 26.2 ms (D-C), with the duty cycle ≤ 0.5 %;
      - `sweep-pause-control.jsonl`: the same scenario with the in-window flag armed for ~120 ms and **no
        product call**, now reproducible through `--sweep-window-control-ms 120` (5.19 ms in-window max,
        12,336 overshoots);
      - `sweep-pause-1observer.jsonl`: `--tcp-concurrency 1`, the scheduling cross-check (21 overshoots of
        4.93 M);
      - the rejected variants' runs kept as `sweep-pause-k{32,64}-diagnostic.jsonl`;
      - the artifact README states that `maxSweepWindowPauseMs` / `maxPauseMs` / `pausesOver500us` are
        report-only diagnostics quoted against the control, that the acceptance evidence is the `HoldProbe`
        counts, and that the runner's `--output` truncates per run so multi-run series are concatenations of
        temp files.

Validation (this round): Release build zero-warning, the targeted filters (class + flow-table consumers),
and both probe facts alone in Release — all green. `dotnet format` / `jb inspectcode` / the full suite stay
with the main session and Step 7.

Rollback: revert `FlowTable.cs` + the new flow-table facts; the other sites are untouched. Reverting to a
batched shape is **not** a supported rollback: it was measured and rejected (see above).


## Step 3 — Site 2, `TcpRedirectTable` scratch — **rollback point B**

- [x] Site 2, `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs`: add `_expiredScratch` + a
      `Lock` `_sweepGate` (this method is synchronous — no `await` in the critical section); rewrite
      `RemoveExpired` as `design.md` §3 (scan into the scratch under one hold; per-candidate re-check +
      removal hold; `Distinct()` deleted); comment that the method has no production caller (tests only).
- [x] `tests/WinForward.Core.Tests/SweepAllocationGateTests.cs`: add
      `TcpRedirectTableSweepAllocatesNoManagedBytes` (4,096 claimed associations, all idle-elapsed).
      Run it red first on the unmodified table, record the byte count, then fix and re-run to green.
- [x] Keep `TcpReversePrefilterTests.PortIsReferenceCountedAcrossDistinctListenerTuples` and
      `TcpProxyCoordinatorConcurrencyTests`' expiry test green (they pin the removal side effects).

Validation: the step's gate fact in isolation + those two tests + full gate set.

Rollback: revert the file.

## Step 4 — Site 3: TCP session store, tombstones, both delegate hoists — **rollback point C**

- [x] `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs`: add `_expiredScratch`,
      `_retiredScratch` (`List<(TcpRedirectSession Session, ITcpRelay? Relay)>`) and
      `SemaphoreSlim(1,1) _sweepGate` — **not a `Lock`**: this method awaits the disposal tail, and a
      lock must not be held across an `await`. Acquire with
      `await _sweepGate.WaitAsync().ConfigureAwait(false)`, release in `finally`; the uncontended fast
      path returns a cached completed task, so the no-op tick neither suspends nor allocates. Split the
      current single expression into scan-under-gate + per-candidate `RetireSessionUnderGate` hold +
      outside-gate disposal tail; add the allocation-free capture overload
      `RetireSessionUnderGate(session, sink)` for the sweep and keep the `RetiredSession` record plus
      its record-returning overload for the cold callers (`DisposeCoreAsync:128`,
      `TearDownSessionAsync:161`, `TryRetireSessionUnderGate:172`); change the hook parameter to
      `Action<DateTimeOffset>? prunePending`.
- [x] Scratch lifetime at this site, exactly: `_expiredScratch.Clear()` at the **start** of the critical
      section (candidates are still in `_sessions`, so an abort merely re-discovers them); and
      `_retiredScratch.Clear()` **only after the disposal loop has attempted every entry** — never at
      the start of the next tick, or an aborted tick's already-retired-but-undisposed sessions lose
      their listeners/relays. Contain each entry's disposal failure (log + continue) so the loop always
      reaches the clear; a leftover list from a catastrophic abort is self-healing, because the next
      tick's disposal loop walks the **whole** list — the leftovers sit at its lowest indices, ahead of
      that tick's own appends — and clears it only at the end (`TcpRedirectSessionStore.cs:179-194`).
- [x] `src/WinForward.Runtime/TcpRedirect/TcpRedirectTombstoneTable.cs`: add
      `List<TombstoneEntry> _expiredScratch`; fill under `_gate`, remove in the same hold, then the
      existing `DrainStaleQueueHeadUnderGate`.
- [x] `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs`: cache the pending-SYN hook in a
      readonly field built in the ctor (`_prunePendingSyn = _pendingSyn.RemoveExpired;`, `:457`), with
      the store's hook parameter taking the timestamp so no closure captures `now`.
- [x] `src/WinForward.Runtime/IdleExpirySweeper.cs`: hoist the other per-tick delegate —
      `_holdsFlow = _tcp?.HoldsFlow` as a readonly field built in the ctor, passed at `:155` instead of
      the per-tick instance-method-group conversion (same allocation class as the closure above).
- [x] Tests: `TcpRedirectTombstoneTableTests` unchanged-green; add
      `TcpRedirectSessionStoreSweepAllocatesNoManagedBytes` (populated store, nothing idle; tombstones
      populated and unexpired) — red first with the byte count recorded, then green after the fix;
      keep `TcpProxyCoordinatorCapacityTests.HoldsFlowTracksSessionAndGraceWindow` green.

Validation: the step's gate fact in isolation + `FullyQualifiedName~TcpRedirect` + full gate set.

Rollback: revert the three files.

## Step 5 — Site 4: UDP coordinator scratch — **rollback point D**

- [x] `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`: add
      `List<(UdpSessionSlot Slot, UdpProxySession Session)> _idleScratch` and
      `SemaphoreSlim(1,1) _sweepGate` (this method awaits; `RemoveExpiredAsync` is public, so two
      callers sharing the scratch is reachable); replace the collection expression at `:347-349` with
      the scan under one `_gate` hold; index the scratch by count in the existing outside-gate loop
      (`_beforeExpiryRecheck`, `TryBeginExpiry`, `RemoveSlotAsync` — all unchanged);
      `_idleScratch.Clear()` at the start of the critical section (candidates stay live in `_sessions`,
      so an aborted tick is simply re-discovered); release the semaphore in `finally`.
- [x] Tests: add `UdpProxyCoordinatorSweepAllocatesNoManagedBytes` (a populated session set over fake
      transports, nothing idle) — red first with the byte count recorded, then green; keep
      `UdpProxyCoordinatorLifecycleTests`, `IdleExpirySweeperCadenceTests` and the UDP retention tests
      green; confirm `_cooldowns.PruneExpired` still runs under the gate.

Validation: the step's gate fact in isolation + `FullyQualifiedName~UdpProxyCoordinator` + full gate
set, plus a short `udp`/`udpChurn` scenario run for the retention shape (the `gc-soak` and
Windows-only rows stay with `windows-real-nic`).

Rollback: revert the file.

## Step 6 — Sites 5 and 6: association pool and association table — **rollback point E**

- [x] Site 5, `src/WinForward.Runtime/UdpProxy/UdpAssociationPool.cs`: add
      `List<UdpControlAssociation> _retireScratch` and `SemaphoreSlim(1,1) _sweepGate` (the disposal
      loop awaits); replace the `SelectMany/Where` collection at `:220-221` with **one `_gate` hold
      covering the scan, the `CanRetire || CanRetireFaulted` test and every `set.Shared.Remove`** (that
      atomicity is the design, not a per-candidate re-check — `design.md` §6); clear the scratch at the
      start of the critical section (an association removed from `Shared` but not yet disposed is still
      in `set.All` and still retirable, so an abort is re-discovered); disposal loop outside, unchanged.
- [x] **Not in scope** (review disposition): the `SampleServerCapabilities` early-out. It is not an
      expiry sweep, no requirement names it, and it would add another exact window to a
      host-lump-exposed gate set — recorded as a follow-up in the task record, not implemented.
- [x] `src/WinForward.Runtime/UdpProxy/UdpAssociations.cs`: same one-line scratch fix for
      `UdpAssociationTable.RemoveExpired` (`:126-138`) plus a comment that it has no production caller,
      so "every sweep site allocates nothing" holds by grep.
- [x] Tests: add `UdpAssociationPoolSweepAllocatesNoManagedBytes` (shared associations with
      outstanding leases, nothing retirable) — red first with the byte count recorded, then green; the
      optional `UdpAssociationTableSweepAllocatesNoManagedBytes` fact; keep
      `UdpAssociationPoolTests.IdleRetirementWaitsOutTheRetentionWindowAndNeverClosesALeasedAssociation`
      and `UdpAssociationCapabilityTests` green.

Validation: the step's gate facts in isolation + `FullyQualifiedName~UdpAssociation` + full gate set.

Rollback: revert the two files.

## Step 7 — Evidence, spec, and record

- [x] Artifact README at `benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/README.md`:
      host/runtime header, command lines, the **phase-scoped** before/after table
      (`maxSweepWindowPauseMs` + `sweepWindowResolves` + in-window counts), the raw series quoted
      **normalized per sweep** with the attribution arithmetic (`research/implementation-notes.md`
      §11/D10: ≥96 % refill contention, ≤564 of 21,252 sweep-attributable), the per-site gate byte
      table (red → 0, site 1's two facts marked regression-only), the `_liveStates` memory delta
      (measured with `GC.GetTotalAllocatedBytes(precise: true)` around `new FlowTable(65_536)`,
      ≈ +512 KiB; corroborate with the residency census's flow-table stage if a run is taken), the
      honest note that site 4's scan hold reaches ~0.5–1.3 ms at 16,384 sessions on the warm datagram
      gate with `udp-ready-path-contention` named as the trigger to extend the cursor, the F3.4
      `ReadActivityClock` baseline, and the caveats (max-pause host noise; "exact counts are gates,
      timing is a series").
- [x] After-series: three instrumented `--scenario sweep --quick` runs into
      `sweep-pause-after.jsonl`, quoted against `sweep-pause-control.jsonl`; the acceptance figure is
      the `HoldProbe` histogram from Step 2, and the series carries the D-C claims (≥1 M
      `sweepWindowResolves` per window; `sweepMeanMs` ≤ 2× the 26.2 ms before-series).
- [x] Gate stability evidence — the spec's **per-gate process run** is the criterion, not a
      suite-level loop (`hot-path.md:1157-1181`, landed in `ac98dbc`): for each exact gate, 20 runs of
      `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~<one gate>"`, recording the
      revision, `git write-tree` fingerprint, expected totals and every run's padded summary; a failure
      is accepted only under the residual signature predicate `168|5216|7384|7448`, anything else stops
      the loop. Use the spec's own script rather than a hand-rolled one.
- [x] Update the spec's recorded `totals` string at `hot-path.md:1166` so
      `SweepAllocationGateTests:<n>` matches the class's actual fact count after the new gates land
      (2 existing + 6 new allocation facts + the registry-invariant fact = 10 if all land) — the
      per-gate loop asserts that total, so a stale number makes every run a vacuous match.
- [x] Spec updates via `trellis-update-spec`:
      - `hot-path.md`: the FlowTable pooling section gains the live-slot registry invariant (a state
        leaves `_liveStates` before `ReturnState`; the sweep's holds are chunk-bounded; the predicate
        runs outside the table gate); the allocation-gate section gains the "gate the no-op tick over a
        populated world" shape.
      - `traffic-policy-lifecycle.md`: the idle-expiry sweep section gains the per-site chunked
        retirement shape, the async-safe `SemaphoreSlim` sweep gate (a `Lock` cannot span an `await`),
        and the corrected `DeriveUdpSweepInterval` cadence note (D4).
      - `hot-path.md`'s measurement section gains one line: the sweep scenario's raw pause series is
        refill-dominated **and** its phase-scoped in-window maximum is host-dominated (the no-product-call
        control reproduces it), so sweep acceptance is proven by countable work-per-hold probes, not by
        wall-clock.
      - `tcp-local-redirect.md` / `udp-relay.md`: one matrix row each for the new gates.
- [x] Record the F3.4 deferral and the representation contract (`design.md` §7 /
      `research/implementation-notes.md` §8) in the F2 task's notes so the bucket work inherits it,
      including the `FlowState.Reset` clock-source defect (D9).
- [x] Correct the parent `08-30-proxy-perf-stability` backlog's F3 row if it repeats the PRD's site
      list (D1/D3).

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                     # zero-warning
dotnet test WinForward.slnx -c Release                                      # full suite green
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"
# per-gate stability proof: the spec's loop, one gate per filter, 20 runs each (hot-path.md:1157-1181)
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx    # zero <Issue>

# The proving benchmark (before/after; --quick is a 15 s observation window)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario sweep --quick --tcp-concurrency 4 \
  --output benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/sweep-pause-after.jsonl

# Warm-path series (must not move)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short

# Retention shapes (host-agnostic halves)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn --quick
```

## Acceptance-criteria mapping

| AC (as finally decided by the operator: minimal hold granularity, D-A acceptance, D-C trade) | Where it is discharged |
|---|---|
| **Work per hold bounded (the acceptance fact)**: a scan hold examines ≤ `SweepChunkEntries` entries and **a removal hold removes exactly one** at 65,536 idle flows | Step 2 `FlowTableSweepHoldWorkIsBoundedByChunkEntries` + the `HoldProbe` histogram in the Step 7 artifact README |
| **Production-shaped sweep measured** (4,096 live / 32 idle-elapsed; duration report-only) and the duty cycle ≤ 0.5 % at the 60 s cadence | Step 2 `FlowTableProductionShapeSweepRecordsItsHoldShape` (counts) + the artifact README's production-shape row (0.033–0.036 ms; ≈ 0.00006 %) |
| Round completeness (`removed == 65,536`, `Count == 0` with no holds left) | Step 2's all-idle-elapsed case + the scenario tripwire; the rejected batched variant's rewind counterexample is recorded in `design.md` §2.2 |
| Exact zero-allocation gate per site, gated tick per requirement 2 | each site's gate fact, landed with its fix (Steps 2–6; shapes in `design.md` §6.1) |
| `FlowTable.RemoveExpired` does not invoke the predicate under the table gate | Step 2 (`SweepHoldPredicateRunsOutsideTheTableGate` + the parked-predicate test in `CoreFlowStructuresTests`) |
| The honest trade recorded (D-C: the sweep's own duration is report-only and deliberately ~110–115 ms in the pathological shape; `sweepWindowResolves` 6.5–10.4 k → 13.1–14.1 M; both rejected batched variants named as measured dead ends) | Step 2's after/control/1-observer runs, the `sweep-pause-k{32,64}-diagnostic.jsonl` runs and the trade table in the Step 7 artifact README |
| Timing figures never presented as acceptance (control run beside them) | Steps 2 and 7; classification in `design.md` §6.1/§8 |
| `_liveStates` delta recorded; regression-only predicate fact green | Step 2 + Step 7 artifact README |
| Release zero-warning, suite green, format empty, inspectcode zero | Validation commands, every step's rollback point |
| Benchmark data recorded and cited before archive | Steps 1, 2 and 7 artifact README |

## Risky files / rollback points

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Core/FlowTable.cs` | The hot resolve gate and the pooled-state contract; a registry bug corrupts state identity | Step 2 (rollback point A) |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs` | Teardown ordering (R2: alias removal + tombstone are atomic under the store gate) | Step 4 (rollback point C) |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` | The warm datagram gate and the session teardown protocol (`TryBeginExpiry` owns the slot) | Step 5 (rollback point D) |
| `src/WinForward.Runtime/UdpProxy/UdpAssociationPool.cs` | Lease/evidence bookkeeping; disposing outside the gate must not change ownership | Step 6 (rollback point E) |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs` | Warm reverse-path gate; removal side effects are pinned by the port-refcount tests | Step 3 (rollback point B) |
| `benchmarks/WinForward.Benchmarks/Stability/SweepPauseScenario.cs` | The acceptance instrument itself: a flag misplaced (armed outside the `RemoveExpired` call, or read after the resolve instead of before it) silently voids AC-1 | Step 1 revert; the row reports `sweepWindowResolves` so a vacuous window is visible |
| `src/WinForward.Runtime/IdleExpirySweeper.cs`, `TcpProxyCoordinator.cs` | The two delegate hoists touch the tick's call shape (cadence is pinned by `IdleExpirySweeperCadenceTests`) | Step 4 revert |
| `benchmarks/**`, `tests/**` expectations | `SweepPauseScenario` throws unless one call removes all 65,536 expired states — a partial-round design fails it by design | n/a (tripwire) |

## Commit plan skeleton

```text
0  test(bench): phase-scoped in-window pause metrics for the sweep scenario
   + the instrumented before-artifact                                           [Step 1]
1  fix(tcp): allocation-free TcpRedirectTable expiry sweep + its gate fact      [Step 3, rollback point B]
2  perf(flow): chunked, resumable FlowTable expiry sweep; hold predicate off the table lock
   + the flow-table gate/predicate/registry facts                               [Step 2, rollback point A]
3  perf(tcp): chunked session/tombstone expiry retirement + both delegate hoists
   + the store gate fact                                                        [Step 4, rollback point C]
4  perf(udp): reusable idle scratch in UdpProxyCoordinator.RemoveExpiredAsync
   + the coordinator gate fact                                                  [Step 5, rollback point D]
5  perf(udp): reusable retire scratch in the association pool and association table
   + the pool/table gate facts                                                  [Step 6, rollback point E]
6  docs(evidence): sweep-pause before/after artifacts + spec updates for F3     [Step 7]
```

Each commit carries its site's gate fact, so no commit is red; each is checked with
`dotnet format … --verify-no-changes` (empty output), `jb inspectcode` (zero `<Issue>`),
`dotnet test -c Release` (green) and `dotnet build -c Release` (zero-warning).

## Deferred / explicitly out of scope

- F3.4's bucket **storage** and the per-iteration bucket plumbing: F2
  (`research/implementation-notes.md` §8, the seven-item representation contract). F3 lands only the
  once-per-tick integer cutoff comparison.
- F3.1's timing wheel: rejected for this table (`research/implementation-notes.md` §8).
- A4's FlowTable rebuild, the F2 lock architecture, and sharding the sweep so the scan needs no gate.
- The live-slot cursor at sites 2–6 (their scans stay O(live population) in one hold until a
  measurement asks for it; the recorded site-4 estimate and its `udp-ready-path-contention` trigger are
  in the artifact README).
- The `SampleServerCapabilities` early-out (dropped in review; follow-up, not an expiry sweep).
- Windows-only validation rows (`gc-soak` TCP/UDP soak on a real NIC, `TcpThroughputScenario` ratio):
  unchanged assignment to the `windows-real-nic` program.

## Review dispositions (2026-09-30, for the archive)

| # | Severity | Disposition |
|---|---|---|
| 1 | BLOCKER | Fixed: Step 1 lands the phase-scoped `SweepPauseScenario` metric first and captures the instrumented before-artifact with it; raw `maxPauseMs`/`pausesOver500us` are report-only, normalized per sweep, with the ≥96 % refill attribution stated (`design.md` §8–§9, `research/implementation-notes.md` §11, D10). **Superseded by finding 14**: the measurement showed even the in-window maximum is host-dominated, so the acceptance figure is now the Step 2 `HoldProbe` counts. |
| 2 | MAJOR | Fixed: `ChunkEntries = 256`, derived against the recorded worst sweep (1.32 µs/state → ≤ ~338 µs) instead of the mean; the wrong 10–80 µs figure is called out and the unmeasured scan/removal split is stated (`design.md` §2.2, D12). |
| 3 | MAJOR | Fixed: sites 3/4/5 use `SemaphoreSlim(1,1)` + `WaitAsync()` held across the whole async method (a `Lock` cannot span the awaits), with the exact scratch lifetime and clear site per site — `_retiredScratch` is cleared only after every entry's disposal was attempted (`design.md` §4–§6). |
| 4 | MAJOR | Fixed: Step 7 now uses the spec's per-gate process run (`hot-path.md:1157-1181`, 20 runs per gate, signature `168\|5216\|7384\|7448`, revision/tree/totals recorded) and updates the spec's `totals` string at `hot-path.md:1166`. |
| 5 | MAJOR | Fixed: the predicate fact is regression-only (an unmodified `FlowTable` already sweeps at 0 B), mandates exactly one hoisted delegate instance, and cites `HotPathAllocationGateTests.cs:435-436` (verified line; the review's 433-434 is two lines early). |
| 6 | MAJOR | Fixed: canonical numbering throughout, files named instead of numbers, and the population bounds restated (≤16,384 per site, Σ over servers for the pool). |
| 7 | MAJOR | Fixed: the pool's shape is stated as **one hold covering scan + `CanRetire` + `set.Shared.Remove`**, disposal outside; the population bound now says Σ over configured servers (≤1,024 shared each, plus private associations bounded by the session capacity). |
| 8 | MAJOR | Fixed: `design.md` §6.1 has a per-site gated-tick column matching PRD requirement 2 — retiring for sites 1/2/6, no-op over a populated world for sites 3/4/5. |
| 9 | MINOR | Fixed: the `_liveStates` cost (≈ +512 KiB at 65,536) is recorded in `design.md` §10 and measured into the artifact README in Step 7. |
| 10 | MINOR | Fixed: `DisposeCoreAsync:128`, `TearDownSessionAsync:161`, the per-gate procedure at `hot-path.md:1157-1181`, and addendum §A4 item 5 (`research.md:448`). |
| 11 | MINOR | Fixed: the Step 2 churn test records that the pre-fix failure is `InvalidOperationException` (enumeration mutation under `_gate`), and `LiveStateCountForDiagnostics` is specified as gate-taking. |
| 12 | MINOR | Fixed: `IdleExpirySweeper._holdsFlow` is hoisted in Step 4 alongside the pending-SYN hook (same per-tick allocation class). |
| 13 | MINOR | Folded in: the `SampleServerCapabilities` early-out is dropped and recorded as a follow-up; the site-4 scan-hold estimate (~0.5–1.3 ms at 16,384 sessions on the warm datagram gate) and the `udp-ready-path-contention` trigger are stated in `design.md` §1/§9 and in the artifact README. |
| 14 | BLOCKER (measured, post-implementation) | The 0.5 ms in-window line is not attainable on this host: the **control run with no product call at all** measured `maxSweepWindowPauseMs` 10.0064 ms with 13,181 in-window overshoots, and the 1-observer run measured 63 overshoots of 5.30 M (0.0012 %). Timing is reclassified as series/diagnostics; the control ships as `sweep-pause-control.jsonl` and is quoted beside every timing figure. |
| 15 | Decision (D-A) | Acceptance moved to **counts**: `FlowTableSweepHoldWorkIsBoundedByChunkEntries` + the `HoldProbe` sink (per-round hold counts, max examinations/removals, both histograms), Release-mode, no `[Conditional("DEBUG")]`. Implemented in Step 2 and mapped in the AC table. |
| 16 | Decision (D-B) — **superseded by 20** | Batched removals land: phase 1 scan hold → phase 2 predicate outside the gate → phase 3 one removal hold in descending slot order, with the **cursor rewind the sketch omitted** (finding 17). Expected ~512 holds/round against ~135 k. Implemented and measured; **reversed by measurement** (finding 20). |
| 17 | BLOCKER (found while reviewing D-B; applies to the rejected variant only) | The sketch's "swap-removing without moving the cursor" loses states: simulated N=65,536 all idle-elapsed with K=256 → `removed = 32,768`, 32,768 expired states still live, round ends at `cursor == liveCount == 32,768`. Fix folded into the design: a removal that pulls an unexamined tail (`from != slot && from >= cursor`) rewinds the cursor to its slot; the same input then removes all 65,536 in 512 holds. Pinned by the all-idle-elapsed round test and the scenario tripwire. |
| 18 | Measure (regression) — **guard dropped by 20** | The per-removal draft cost `sweepMeanMs` 26.2 → ~120 ms (73/69/79 sweeps per window vs 139/147/139). D-B was proposed as the fix with a `sweepMeanMs` ≤ 2× guard; the reversal keeps the duration and drops the guard (report-only, D-C). Note for the record: "lower `ChunkEntries` later" is **provably a no-op** for the all-expired hand-off cost — every entry is still examined once and removed once, so `K` only changes hold size/count, never total work (and the timing reading it was meant to fix was host scheduling, per finding 14). |
| 19 | Deviations (record) | `ScanChunk`/`RemoveCandidateAt` extracted for MA0051; the parked-predicate test lives in `CoreFlowStructuresTests` (the capacity harness cannot reach the dispatcher's private `FlowTable`); the stability runner's `--output` truncates per run, so the 3-run series were concatenated from temp files — recorded in `design.md` §2.4 and the artifact README. |
| 20 | **REVERSAL (measured)** | Batched removals **rejected**; the landed shape is minimal hold granularity (one removal per hold, at the cursor). Measured on the instrumented scenario, in-window resolves per 15 s window / `sweepMeanMs`: **one removal per hold 13,145,035–14,070,168 / 110.0–114.7 ms** (3 runs, the landed shape), 32 removals per hold 1,104,001 / 1,063,213 / 55.2 / 56.2 ms (2 runs), 256 removals per hold 89,062–119,690 / 35.2–36.6 ms (3 runs). Hold **granularity**, not acquisition count, is what preserves warm-path progress — a shorter hold is a shorter queue a concurrent resolve can be stuck behind — and batching's longer holds also made the report-only in-window tail worse (49.8–58.7 ms against 4.4–6.8 ms). The `sweepMeanMs` ≤ 2× guard is dropped: the sweep's own duration is report-only (D-C), and the duty cycle stays inside the PRD's 0.5 % line (pathological round 0.18–0.19 %, production shape ≈ 0.00006 %). Rejected runs kept as `sweep-pause-k{32,64}-diagnostic.jsonl`; the batched variant's rewind counterexample stays in `design.md` §2.2 as the rejected alternative. |
| 21 | Decision (landed shape, PRD-aligned) | `RemoveExpired` restored to the minimal-granularity loop: scan hold ≤ `SweepChunkEntries` examinations stopping **on** the first idle-elapsed candidate, predicate with no lock held, one removal hold at the cursor via `RemoveCandidateAt`; no `_batch`/`_approved`, no rewind. Acceptance fact restated (`MaxRemovals <= 1`, histogram recorded: 69,632 scan holds × 1 examination, 65,536 single-entry removal holds, 4,096 zero-removal held holds) and the PRD's production-shape probe added (4,096 live / 32 idle → 48 scan / 32 removal holds, 0.033–0.036 ms, duration recorded not asserted). Series re-run: after ×3 13.1–14.1 M in-window at 110.0–114.7 ms, 1 observer 4,933,142 at 54.0 ms, control unchanged. |
| 22 | Measure (control refresh) | The control is now reproducible via `--sweep-window-control-ms 120` (documented in `benchmarks/README.md`) instead of a reverted temp patch, and re-measured **5.1941 ms** in-window max with 12,336 in-window overshoots (supersedes finding 14's temp-patch 10.0064 ms / 13,181, same order); the 1-observer cross-check re-measured 21 overshoots of 4.93 M resolves. |
| 23 | Deviations (Steps 3–6, record) | Site 2 and site 6 got the **full** site-2 treatment (reused scratch + `Lock` sweep gate outer to the table gate + per-candidate re-check hold), not a literal one-line scratch: the design says "same synchronous scratch fix as site 2", and both tables' gates are packet-path gates. `TcpRedirectSessionStore.ReleaseRetiredAsync` now takes the `(session, relay)` tuple so the sweep scratch feeds it without allocating a record per retirement (cold callers pass the tuple from their `RetiredSession`), with `ReleaseRetiredScratchAsync` extracted for MA0051. `IdleExpirySweeper._holdsFlow` is built as `tcp is null ? null : tcp.HoldsFlow` because a `?.` method group does not compile (CS8978). The pool gate needed a **release-before-assert** discipline: the pool's drain joins every outstanding lease by design, so an assertion that fires while the 16 probe leases are still held hangs the drain instead of failing the test — the gate now disposes its leases in a `finally` (recorded in `hot-path.md`'s gate-shape contracts). The same lesson applied to the temporary red probes. |
| 24 | Correction (Step 7 totals) | `SweepAllocationGateTests` now holds **12** facts (2 pre-existing + the site-1 predicate/production/work-count facts + 5 site gates + the discriminator), not the 10 the earlier plan arithmetic predicted; the recorded `totals=` string at `hot-path.md` was updated to 12 so the per-gate loop's expected total cannot match vacuously. |
| 25 | Check (independent verification) | Clean `dotnet build WinForward.slnx -c Release --no-incremental`: **0 warnings / 0 errors**. Full suite: Analyzers 18/18 + Core.Tests 995/995, 0 failed/skipped, exit 0. Per-gate proof with the spec's own loop (log: `benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/gate-stability.txt`): **20/20 green for each of the four gate classes, pooled 0/80**, every run's totals assertion matched (11 / 3 / 12 / 14), `actual=none` in all 80 (no lump appeared, nothing to classify). AC verdicts: **AC-1 PASS** — the acceptance fact asserts and records exactly the stated shape (69,632 scan holds × 1 examination, 65,536 single-entry removal holds, 4,096 zero-removal held holds, `removed == 65,536`, `Count == 4,096`, and a second released call empties the table); **AC-2 PASS** — artifact rows re-read from the raw jsonl match the README exactly (13,251,906 / 14,070,168 / 13,145,035 in-window against the before-series 10,446 / 8,657 / 6,469); **AC-3 PASS** — production shape 48 scan / 32 removal holds, max 256 examinations / 1 removal asserted, 0.033–0.036 ms and the ≤ 0.5 % duty cycle recorded report-only; **AC-4 PASS** — every timing field `gated: false` with the control quoted beside each figure. Allocation gates for sites 2–6, site 1's gates, the predicate/registry facts and the warm-path gate all exist, are non-vacuous and exact-zero. Extra cross-checks: `--scenario udpchurn --quick` retires 48/48 sessions through the *retiring* tick with 0 setup failures/cooldowns/fallbacks (`udpchurn-check-quick.jsonl`), and a post-change `--filter '*FlowTableProductionShape*' --job short` run shows `Allocated` empty on all three methods. |
| 26 | Check — fix (spec ↔ code) | `traffic-policy-lifecycle.md`'s corrected cadence rendered the formula in the wrong order: `max(5 s, min(mainInterval, retention / 2))` returns 5 s exactly where the code returns the main interval. The code (and `research/implementation-notes.md` D4) is `min(mainInterval, max(5 s, retention / 2))` — the 5 s floor applies to the *request* before the main-interval cap, so a main interval shorter than 5 s wins. Reworded to name the request and the cap separately. |
| 27 | Check — fix (stale claim) | `SweepPauseScenario`'s class XML doc still called `maxSweepWindowPauseMs` / `pausesInWindow*` "the acceptance figures (the research's 0.5 ms line)" — the exact claim finding 14/22 reversed in the row `note`, `benchmarks/README.md` and `hot-path.md`. The class doc now classifies them as phase-scoped diagnostics and points acceptance at the countable probe. |
| 28 | Check — fix (artifact honesty) | The trade table's K = 256 row quoted runs whose raw jsonl was never retained, while the kept `sweep-pause-k64-diagnostic.jsonl` (K = 64: 393,388 in-window resolves, 47.230 ms, 122 sweeps) had no row at all — so finding 20's "Rejected runs kept as `sweep-pause-k{32,64}-diagnostic.jsonl`" overstated what the files back (they back K = 32 and K = 64). The README now carries the K = 64 row and marks the K = 256 row's provenance; `design.md` §2.2 records the same. |
| 29 | Check — fix (artifact wording) | README: the control row's 88 B/sweep is `GC.GetTotalAllocatedBytes(precise: true)` — process-wide, so "the control loop's own" was wrong; relabelled as background allocation while the control slept. Added the observer-count note separating the contended `sweepMeanMs` (110–115 ms, 4 observers) from the single-threaded throwaway-probe round (30.3–41.7 ms), and a note that the `before` artifact's embedded `note` predates the reclassification (the artifact is kept byte-for-byte, not re-edited). |
| 30 | Check — fix (stale totals) | `hot-path.md` §4's prose suite total `981 + 18` was stale; the verified tree is `995 + 18`. The per-class `totals=` string itself is correct (checked against each class's `[Fact]` / `[Theory]`+`[InlineData]` count). |
| 31 | Check — fix (test failure mode) | `CoreFlowStructuresTests.FlowTableSweepPredicateParkDoesNotBlockConcurrentResolve` released the parked predicate only on the success path, so the assertion it exists to raise (a resolve queueing behind the parked predicate) unwound into `using` disposal of a `ManualResetEventSlim` while the sweeper thread was still parked inside `Wait` — the crash/hang-instead-of-a-red-assertion class finding 23 already recorded for the pool gate. The release now sits in a `finally`; the test still passes. |
| 32 | Check — recorded, not fixed | Site 3's gate passes `prunePending: null`, so the gated tick is the store's call shape, not `TcpProxyCoordinator.RemoveExpiredAsync`'s (which always passes the cached hook): a re-created per-tick closure there, or an allocating hook body, is caught by no exact gate. Not changed here — making the store gate pass a real hoisted `TcpPendingSynSetupIndex.RemoveExpired` would alter the class the per-gate proof had just run 20× on, and gating the coordinator's own tick is a new fact (fact count + `totals=` string + gate matrix all move). Recorded in the artifact README's check-session residual risks. |
| 33 | Check — recorded, not fixed | Two duration figures have no committed harness (throwaway-probe figures, disclosed as such in the README): the single-threaded 65,536-flow round (30.3–41.7 ms) and the production-shape round (0.033–0.036 ms). The PRD's quoted control figures (10.0 ms / 13,181 overshoots; 63 of 5.30 M) are the superseded temp-patch readings (finding 22 supersedes them with 5.1941 ms / 12,336). `implement.md`'s Step 3–7 checkboxes are unticked although Steps 3–6 landed and are verified green — the checklist should be reconciled before archive; Step 5's "short `udp`/`udpChurn` scenario run" item is discharged by the check session's run (`udpchurn-check-quick.jsonl`). |

