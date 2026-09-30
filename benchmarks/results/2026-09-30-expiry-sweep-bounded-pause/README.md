# F3 expiry sweeps: bounded pause, zero allocation under locks — evidence

Task: `.trellis/tasks/09-30-expiry-sweep-bounded-pause` (finding F3 of the archived research
`09-29-tcp-udp-path-structural-perf`).

**Acceptance is countable, not wall-clock.** The landed shape is the sweep at **minimal hold
granularity**: a scan hold examines at most `SweepChunkEntries` entries and removes nothing, the predicate
runs with no gate held, and a removal hold removes **exactly one** entry at the cursor. That is proven
below by exact counts from the `FlowTable.SweepHoldProbe` sink. Every timing number in this document is a
**report-only diagnostic**, quoted against the calibration control; the sweep's own duration is
deliberately spent to keep warm-path progress (the trade table below).

Host / runtime (from the artifacts' metadata rows):

| Field | Value |
|---|---|
| OS | NixOS 26.11 (Zokor) |
| Runtime | .NET 10.0.12 |
| Architecture | X64 |
| SDK | 10.0.401 |
| Logical processors | 32 |

## Command lines

The pause series (15 s observation per run). The stability runner **truncates** its `--output` path per
process (`SoakRunner.OpenOutput` opens with `append: false`), so each run wrote its own temp file and the
runs were concatenated into the artifact files — a single invocation does not append:

```bash
series=after   # also: control, 1observer
for run in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario sweep --quick --tcp-concurrency 4 \
    [--sweep-window-control-ms 120] \           # control series only
    --output /tmp/wf-sweep-$series-$run.jsonl
done
cat /tmp/wf-sweep-$series-{1,2,3}.jsonl > benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/sweep-pause-$series.jsonl
# the 1-observer cross-check uses --tcp-concurrency 1 and a single run
```

## Acceptance: work per hold (exact counts)

`SweepAllocationGateTests.FlowTableSweepHoldWorkIsBoundedByChunkEntries` seeds **65,536 idle-elapsed
UDP flows plus 4,096 held TCP flows** (the scenario's observer-equivalent set, claimed first), attaches a
`SweepHoldProbe`, and runs one `RemoveExpired` with a single hoisted predicate. The same fixture was run
independently by a throwaway probe (Release, `SweepChunkEntries = 256`), which also recorded the round's
duration:

| Measurement | Value |
|---|---:|
| `removed` (one call, one round) | 65,536 |
| `Count` / `LiveStateCountForDiagnostics` after the round | 4,096 / 4,096 (the held set survives by contract) |
| `ScanHolds` / `RemovalHolds` | 69,632 / 69,632 (one of each per entry in this all-idle-elapsed fixture) |
| `MaxExaminations` | **1** (≤ `SweepChunkEntries` = 256) |
| `MaxRemovals` | **1** — one entry per removal hold |
| Examination histogram | index 1 → 69,632 holds |
| Removal histogram | index 0 → 4,096 holds (the held region), index 1 → 65,536 holds |
| Round duration, single-threaded (report-only) | 30.3 / 30.6 / 41.7 ms over three probe runs (the same round under the benchmark's 4 observers is 110–115 ms — see the trade table's observer-count note) |

The held set is then released (`isHeld: null`) and a second call removes exactly it, leaving
`Count == 0` — the "one round ends empty" shape for a table with no holds left.

### Production-shaped round (PRD AC-3)

`SweepAllocationGateTests.FlowTableProductionShapeSweepRecordsItsHoldShape` drives **4,096 live flows and
32 idle-elapsed flows** with a probe attached and asserts the counts (no duration assertion). The same
fixture measured by the throwaway probe:

| Measurement | Value |
|---|---:|
| `removed` / `Count` after | 32 / 4,096 |
| `ScanHolds` / `RemovalHolds` | 48 / 32 (16 full 256-entry live scan holds, then one scan hold + one single-entry removal hold per idle flow) |
| `MaxExaminations` / `MaxRemovals` | 256 / 1 |
| Examination histogram | index 256 → 16 holds, index 1 → 32 holds |
| Removal histogram | index 1 → 32 holds |
| Round duration (report-only) | **0.033 / 0.034 / 0.036 ms** over three probe runs |

**Sweeper duty cycle at the shipped 60 s main cadence** (PRD AC-3, ≤ 0.5 %): the production-shaped round is
0.036 ms / 60 s ≈ **0.00006 %**; even the pathological all-expired round measured by the benchmark (110–115
ms per call, below) is **0.18–0.19 %**. Both are inside the line.

## Warm-path series (report-only): before / after / control / 1 observer

| Run | `sweeps` | `sweepMeanMs` | `sweepWindowResolves` | `maxSweepWindowPauseMs` | `maxPauseMs` | `pausesOver500us` | `removedPerSweep` | `sweepAllocatedBytesPerSweep` |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| before 1 | 139 | 26.173 | 10,446 | 92.5733 | 92.5733 | 25,625 | 65,536 | 0 |
| before 2 | 147 | 25.649 | 8,657 | 36.4400 | 56.6825 | 24,623 | 65,536 | 0 |
| before 3 | 139 | 26.478 | 6,469 | 44.7933 | 44.7933 | 23,440 | 65,536 | 0 |
| after 1 (minimal granularity) | 80 | 111.732 | 13,251,906 | 6.7753 | 12.1082 | 29,822 | 65,536 | 0 |
| after 2 | 83 | 110.007 | 14,070,168 | 4.3962 | 11.0228 | 28,897 | 65,536 | 0 |
| after 3 | 80 | 114.746 | 13,145,035 | 6.2328 | 13.7399 | 29,600 | 65,536 | 0 |
| control (armed 120 ms, **no product call**) | 125 | 120.143 | 46,503,855 | 5.1941 | 5.1941 | 12,371 | 0 | 88 (process-wide background allocation while the control slept; no product call is made) |
| after, 1 observer | 117 | 54.009 | 4,933,142 | 1.5851 | 6.8683 | 149 | 65,536 | 0 |

The `before` rows are kept **byte-for-byte as recorded**: their embedded `note` still calls
`maxSweepWindowPauseMs` an acceptance figure, because that artifact was captured before finding 14/22
(the host-dominated in-window maximum). The classification below supersedes that note — no timing field
in any row, including the in-window ones, is an acceptance figure. The `after`/`control`/`1observer` rows
were written by the reclassified scenario and carry the corrected note.

Verdicts against the PRD's acceptance criteria:

- **AC-1 work-per-hold: met** — `MaxExaminations` 1 (≤ 256), `MaxRemovals` 1, one round, 65,536 removed.
- **AC-2 warm path stops starving: met** — 13.1–14.1 M resolves inside sweep windows per 15 s window
  against the ≥ 1,000,000 line and the before-series' 6,469–10,446 (3 runs).
- **AC-3 minimal granularity + recorded duration: met** — one removal per hold; the production-shaped
  round is 0.033–0.036 ms (duty cycle ≈ 0.00006 %), and the pathological all-expired round's ~110–115 ms
  is recorded here and in the trade table rather than hidden.

## The trade: hold granularity decides warm-path progress (the rejected variants)

Batching removals into chunk-sized holds was implemented, measured and **rejected**. Hold
granularity — not acquisition count — is what preserves warm-path progress, because a shorter hold is a
shorter queue a concurrent resolve can be stuck behind. The rejected values are measured dead ends; the
raw runs are kept for `k32` and `k64` (`sweep-pause-k32-diagnostic.jsonl`,
`sweep-pause-k64-diagnostic.jsonl`, produced by a temporary removals-per-hold override in a scratch
build; the override was reverted). The **K = 256** row is the D-B draft's default-chunk variant: its
three runs' raw jsonl was not retained, so those figures are the working-log numbers also recorded in
`implement.md` finding 20 and `design.md` §2.2, not a re-derivable artifact — the kept `k32`/`k64` runs
carry the same monotone trend on their own:

| Hold granularity (removals per hold) | `sweepMeanMs` | `sweeps` | `sweepWindowResolves` | `maxSweepWindowPauseMs` | Decision |
|---|---:|---:|---:|---:|---|
| **1 (landed)** | 110.0–114.7 | 80–83 | **13,145,035–14,070,168** | 4.4–6.8 | **adopted** — warm-path progress first; duration report-only |
| 32 (rejected) | 55.154 / 56.214 | 113 / 108 | 1,104,001 / 1,063,213 | 9.6523 / 9.6609 | rejected: ~1/13 the in-window resolves for a duration the task does not need |
| 64 (rejected) | 47.230 | 122 | 393,388 | 18.9879 | rejected: ~1/33 the in-window resolves |
| 256 (rejected, the D-B draft — runs not retained; log figures) | 35.2–36.6 | 131–136 | 89,062–119,690 | 49.8–58.7 | rejected: ~1/120 the in-window resolves, and its longer holds made the report-only pause tail *worse* |

Read the `sweepMeanMs` column with its observer count: the 1/32/64/256 rows are the benchmark's
**4-observer** `sweepMeanMs`, so they are comparable with each other and with the before-series' 26.2 ms,
not with the single-threaded throwaway-probe duration in the acceptance table above (30.3–41.7 ms for
the same 65,536-flow landed round). The contended round is slower than the uncontended one because every
removal hold re-acquires a gate four observer threads are queueing on; that is the shape the landed
granularity deliberately buys warm-path progress with, and both figures are report-only.

The 256-removal variant also needed a **cursor rewind** to stay round-complete (its phase 3 pulled
unexamined tails below the cursor; without the rewind the all-idle-elapsed round returned 32,768 of
65,536). The landed minimal-granularity loop removes at the cursor, so the swapped-in tail is examined
next and no rewind exists to get wrong.

## Attribution and the calibration floor

The raw counts include the scenario's own 65,536 individual `TryClaimResolved` refill calls per round,
which contend on the same table gate; at most 4 observers × the round count can be sweep-sourced, so
≥96 % of `pausesOver500us` is refill contention this task does not touch. The control run arms the same
in-window flag for 120 ms per iteration with **no product call at all** (reproducible with
`--sweep-window-control-ms 120`) and still measures a 5.19 ms in-window maximum and 12,336 in-window
pauses over 0.5 ms — the same order as a real run's in-window population, with zero sweep work. No
in-window timing maximum can be an acceptance figure on this host; the 1-observer run (21 in-window
pauses over 0.5 ms of 4.93 M) is the scheduling cross-check.

## Per-site allocation-gate byte table (red → 0)

Gates land with their site's fix. Site 1's two allocation facts are **regression-only** (an unmodified
`FlowTable` already swept at exactly 0 B once its deleted scratch list had stabilised); its two
work-count facts are new properties. Sites 2–6 were run **red first** on the unmodified product and their
exact per-tick byte counts recorded below. **Every** allocation window in the table — seven of the nine
rows; the two site-1 count facts have no byte window — was then re-discriminated by injecting one
`new byte[64]` inside it: all seven reported `Expected: 0, Actual: 88` and were green again after
restoring.

| # | Site | Gate fact | Before | After |
|---|---|---|---|---|
| 1 | `FlowTable.RemoveExpired` | `FlowTableSweepAllocatesNoManagedBytes` | 0 B (regression-only) | 0 B (regression-only; the probe converges on its first sweep) |
| 1 | `FlowTable.RemoveExpired` + hold predicate | `FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes` | 0 B (regression-only) | 0 B |
| 1 | `FlowTable.RemoveExpired` — work per hold | `FlowTableSweepHoldWorkIsBoundedByChunkEntries` | n/a (new property) | 1 examination / 1 removal max, 65,536 removed in one round (counts, not bytes) |
| 1 | `FlowTable.RemoveExpired` — production shape | `FlowTableProductionShapeSweepRecordsItsHoldShape` | n/a (new property) | 48 scan / 32 removal holds, max 256 examinations / 1 removal |
| 2 | `TcpRedirectTable.RemoveExpired` (retiring: 4,096 idle associations) | `TcpRedirectTableSweepAllocatesNoManagedBytes` | **355,672 B** | 0 B |
| 3 | `TcpRedirectSessionStore.RemoveExpiredAsync` + tombstones (no-op: 64 Redirecting sessions + 64 unexpired tombstones) | `TcpRedirectSessionStoreSweepAllocatesNoManagedBytes` | **520 B** | 0 B |
| 4 | `UdpProxyCoordinator.RemoveExpiredAsync` (no-op: 16 fake-transport sessions) | `UdpProxyCoordinatorSweepAllocatesNoManagedBytes` | **272 B** | 0 B |
| 5 | `UdpAssociationPool.SweepIdleAssociationsAsync` (no-op: 4 shared associations, 16 outstanding leases) | `UdpAssociationPoolSweepAllocatesNoManagedBytes` | **328 B** | 0 B |
| 6 | `UdpAssociationTable.RemoveExpired` (retiring: 64 idle associations) | `UdpAssociationTableSweepAllocatesNoManagedBytes` | **4,184 B** | 0 B |

## Per-gate stability proof (`gate-stability.txt`)

The suite-level loop is explicitly **not** the criterion (`hot-path.md` §4). Each exact gate class was run
in its own process, 20 runs per class, with the spec's own loop — per-run padded summary, expected totals
assertion, `git rev-parse --short HEAD`, `git write-tree` fingerprint, exit status, and the residual
signature predicate `^(168|5216|7384|7448)$` as the only accepted failure classification:

| Gate class | Runs | Green | Failures | Host lumps (`actual=`) |
|---|---:|---:|---:|---:|
| `HotPathAllocationGateTests` | 20 | 20 | 0 | 0 (`actual=none` in all 20) |
| `CapturePumpReadCallTests` | 20 | 20 | 0 | 0 |
| `SweepAllocationGateTests` | 20 | 20 | 0 | 0 |
| `NdisCapturePumpTests` | 20 | 20 | 0 | 0 |

**Per-gate failure rate 0/20 for every class; pooled 0/80** (Wilson 95 % upper bound ≈ 3.4 %,
Clopper-Pearson one-sided ≈ 3.7 %). Every run's `Total:` matched its class's expected count
(11 / 3 / 12 / 14) — no run was a vacuous filter match — and no failure of any size occurred, so nothing
had to be classified: the recorded lump family (168 / 5,216 / 7,384 / 7,448 B) never appeared.

Two log-reading caveats. (1) `git write-tree` fingerprints the **index**, and every change of this task is
worktree-only (unstaged edits plus untracked task/artifact files), so the recorded fingerprint is HEAD's
tree and does **not** identify the tested bits; the log's addendum records the working-tree identity
(`git diff HEAD | sha256sum`). (2) The four gate classes were untouched after the loop; the check session's
later edits were prose, one benchmark XML doc comment (`SweepPauseScenario`) and the release-ordering fix
inside `CoreFlowStructuresTests.FlowTableSweepPredicateParkDoesNotBlockConcurrentResolve`, none of which is
in a filtered class — the edited tree was then re-verified with a clean build, the full suite and one run
per gate filter (all green, same totals).

## Check-session cross-checks (2026-09-30)

- **Build and suite.** `dotnet build WinForward.slnx -c Release --no-incremental`: 0 warnings / 0 errors.
  `dotnet test WinForward.slnx -c Release`: `WinForward.Analyzers.Tests` 18/18 and
  `WinForward.Core.Tests` 995/995, 0 failed, 0 skipped, exit 0 (`995 + 18` — the spec's stale `981 + 18`
  prose was corrected in `hot-path.md` §4).
- **UDP retirement under churn — the retiring tick no gate covers** (`udpchurn-check-quick.jsonl`,
  `--stability --scenario udpchurn --quick`): 48/48 sessions accepted with a first response,
  `removed` 48 through `UdpProxyCoordinator.RemoveExpiredAsync`'s zero-timeout retiring path, `retireMs`
  3.25, establishment loss 0, and 0 setup failures / cooldowns / association fallbacks. Site 4's new
  scan → re-verify → teardown loop is therefore exercised end-to-end with real retirements; the xunit
  gate can only cover its no-op tick.
- **Warm path after the change** (`dotnet run -c Release --project benchmarks/WinForward.Benchmarks --
  --filter '*FlowTableProductionShape*' --job short`, one run, post-change): `ReadActivityClock`
  **40.25 ns** (pre-change baseline 40.524 / 40.273 / 40.284), `ResolveReverseAliasHit` **133.90 ns**
  (baseline 142.316 / 143.817 / 143.548), `ResolveSameOrientationHit` **106.34 ns** (baseline
  91.659 / 104.896 / 90.178 — within the ±26 % short-job spread the implementation plan records for this
  method). **`Allocated` is empty (`-`) for all three** — the warm resolve's zero-allocation contract
  holds after the change; the F3.4 numbers above stay the *pre-change* baseline for the deferred bucket
  work.

## Check-session residual risks (record before archive)

- **Site 3's gate passes `prunePending: null`.** The gated tick is therefore the store's own call shape,
  not `TcpProxyCoordinator.RemoveExpiredAsync`'s, which always passes the cached hook. A future edit that
  re-created a per-tick closure there, or made the hook's body allocate, would not be caught by any exact
  gate. Closing it means either passing a real hoisted `TcpPendingSynSetupIndex.RemoveExpired` in the store
  gate or gating the coordinator's own tick (a new fact: fact count, `totals=` string and gate matrix all
  move with it).
- **The no-op gates do not prove the scan ran.** They prove a *populated* world is untouched byte-exactly;
  a future "skip the scan when the last scan found no candidate" shortcut would keep them green while
  covering no scan at all, and would need its own gate on a scanning tick.
- **Two duration figures have no committed harness**: the single-threaded 65,536-flow round
  (30.3–41.7 ms) and the production-shape round (0.033–0.036 ms) came from a throwaway probe, disclosed as
  such above but not reproducible from the tree. The benchmark's contended `sweepMeanMs` rows are.
- **The K = 256 rejected-variant runs were not retained** (see the trade table); only K = 32 and K = 64
  have raw artifacts.
- **The PRD's quoted control figures** (`maxSweepWindowPauseMs` 10.0 ms / 13,181 overshoots; 63 of 5.30 M)
  are the superseded temp-patch readings; the shipped control is 5.1941 ms / 12,336 (finding 22), same
  order. The PRD is the planning record and was left as written.

## `_liveStates` memory delta

Measured with `GC.GetTotalAllocatedBytes(precise: true)` around the allocation (paths warmed first):
`new FlowState[65_536]` = **524,312 B = 512.0 KiB = 8.00 B/slot**, exactly the +8 B/slot the design
records. The same instrument around `new FlowTable(65_536)` reports **32,870,264 B** (~31.4 MiB) for the
whole construction — dominated by the two pre-sized dictionaries plus the pre-existing `_freeStates`
pool. The registry replaces `_expiredScratch`, which grew to ≈ 6 MB of `FlowKey` slots at 65,536 expired
flows, so the net at that cardinality is negative. Minimal granularity keeps no batch scratch at all.

## F3.4 baseline for the deferred bucket work

`ReadActivityClock` (the per-hit clock the deferred bucket removes), three `--job short` runs:
**40.524 / 40.273 / 40.284 ns**, against `ResolveSameOrientationHit` 91.659 / 104.896 / 90.178 ns and
`ResolveReverseAliasHit` 142.316 / 143.817 / 143.548 ns. Taken at unmodified product HEAD.

## Known limits, recorded honestly

- **Site 4's scan hold reaches ~0.5–1.3 ms at the full 16,384-session capacity** on the warm datagram
  gate. That is accepted here (the bounded-hold contract is scoped to the 65,536-flow table);
  `udp-ready-path-contention` is the trigger to extend the live-slot cursor there. Sites 2–6 keep an
  O(live population) scan in one hold, bounded by ≤16,384 entries per site.
- **The sweep's own duration is report-only and deliberately poor in the pathological shape**: ~110–115 ms
  for an all-expired 65,536-flow round against the before-series' 26.2 ms, because every removal takes its
  own gate hold. That is the accepted price of 13–14 M in-window resolves; the duty cycle stays ≤ 0.5 % at
  the shipped 60 s cadence.
- **`SweepChunkEntries` bounds work per hold, not total work**: every entry is examined once and each idle
  entry removed once in its own hold, so the round's total work is independent of the scan chunk size.
- **Exact counts are gates, timing is a series.** The allocation facts and the two work-per-hold facts
  assert exact counts; every timing number here is an observation from a 15 s window on a shared host.

## Follow-ups recorded, not implemented

- **`SampleServerCapabilities` early-out** (dropped after review): it is not an expiry sweep, no
  requirement names it, and a fast path there would add another exact window to the host-lump-exposed
  gate set. Recorded here so the next reader knows it was seen and deliberately left alone.
- **F3.4 activity bucket** (deferred to F2): the per-hit `TimeProvider.GetUtcNow()` stays; F3 landed only
  the once-per-tick integer cutoff. `ReadActivityClock` baseline above; the representation contract is in
  `research/implementation-notes.md` §8, including the `FlowState.Reset` clock-source defect (D9).
- **Site 4's warm datagram gate** holds an O(live population) scan (~0.5–1.3 ms at 16,384 sessions);
  `udp-ready-path-contention` is the measured trigger to extend the live-slot cursor there.
