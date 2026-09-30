# F2 warm-path lock chain — evidence artifact

Task: `.trellis/tasks/09-30-warm-path-lock-chain` (finding F2 of the archived
`09-29-tcp-udp-path-structural-perf`). Base revision: `c6f58b7` (post-F3 tree).

Host / runtime (every series below): AMD Ryzen 9 9955HX (16 physical / 32 logical), NixOS 26.11
(Zokor), .NET 10.0.12, Release. Allocation bytes and counts are gates; throughput and ns are series —
the recorded rule applies, so timing deltas under ~2× are noise on this box.

## Status

**Steps 1–7 are landed.** Step 1 (instrumented `scaling` warm arm, `GateHoldProbe`/`GateEntryCountForDiagnostics`/
`ActivityGateHoldProbe`), Step 2 (`ActivityBucket`/`ActivityBucketClock`, the barrier-correct `FlowState`
seqlock, bucket-space sweeps), Step 3 (the direct-mapped flow-table warm cache over the gated
`Dictionary` authority), Step 4 (self-traffic split: exact half at claim time, wildcard half lock-free
on the warm entry), Step 5 (TCP redirect: one fold probe, two warm caches, factored removal), Step 6
(UDP ready-first reorder, session cache, `_activityGate` off the send/touch paths) and Step 7 (one
shared clock wired through `DurableCaptureBundle` and ticked per pump iteration, evidence, specs).

**The PRD's scaling criterion is met after Steps 4–6**: the warm arm's self-normalised four-thread
ratio is **0.933 / 0.957 / 0.980** against the ≥ 0.6 line, its one-thread arm is 5.96–6.03 M/s
(≥ 3.00 M/s floor), and the recorded-baseline reading is **1.685 / 1.733 / 1.753** (line 0.6 ⇒
≥ 7,996,220/s at four threads). The four-thread absolute rate more than doubled (10.4–11.1 → 22.5–23.4
M/s) against a *fixed* denominator, which is the comparable statement; the self-normalised ratio is
quoted beside it because its denominator is the same run's one-thread arm.

## Command lines

```bash
# Scaling series (3 runs, one file per process — the runner truncates --output per process).
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario scaling --quick --flows 4096 --duration 63 --output /tmp/wf-warm-after46-$r.jsonl
done
cat /tmp/wf-warm-after46-{1,2,3}.jsonl > benchmarks/results/2026-09-30-warm-path-lock-chain/scaling-contention-after-steps4-6.jsonl

# UDP ready path (3 runs of the BDN short job, raw exports kept).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpReadyPathContention*' --job short

# Flow-table production shape (3 runs) and the must-not-move redirect rewrite rows.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRedirectDataPath*' '*Parser*' --job short

# Residency census (3 runs) and the gc-soak shape anchors.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario residency --quick --flows 100 --udp-flows 100 --output /tmp/wf-residency-after-$r.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --quick
```

`--duration 63` with `--quick --flows 4096`: the window divisor is `arms.Length * threads.Length`
(3 arms × 3 thread counts × 7 s), so each configuration keeps the recorded 7 s window. The table is
`capacity = flows + 1,024 = 5,120` ⇒ `clamp(5,120 × 64, 4,096, 262,144) = 262,144` slots (2 MB),
λ = 0.016. The before-series (same command, before Step 3) and the Step-3 after-series are
`scaling-contention-before.jsonl` / `scaling-contention-after.jsonl`; the post-Step-4–6 series is
`scaling-contention-after-steps4-6.jsonl`.

## The scaling series (warm arm, 3 runs each)

| series | 1 thread (M/s) | 2 threads | 4 threads | 4t self-normalised ratio | 4t vs recorded baseline | cache miss rate |
|---|---:|---:|---:|---:|---:|---:|
| before (locked guard + gated resolve) | 5.11 | 3.78 | 3.29 | 0.161 | 0.247 | 100 % (gated path) |
| before 2 | 5.16 | 3.73 | 3.19 | 0.154 | 0.239 | — |
| before 3 | 5.17 | 3.89 | 3.16 | 0.153 | 0.237 | — |
| after Step 3 (cache + **still gated** wildcard guard) 1 | 6.81 | 10.25 | 11.06 | 0.406 | 0.830 | 0.73 % |
| after Step 3 run 2 | 6.59 | 10.01 | 10.57 | 0.401 | 0.793 | 0.93 % |
| after Step 3 run 3 | 6.70 | 10.10 | 10.43 | 0.389 | 0.782 | 0.91 % |
| **after Steps 4–6 run 1** | **5.96** | **11.78** | **23.36** | **0.980** | **1.753** | 1.04 % |
| **after Steps 4–6 run 2** | **6.03** | **12.07** | **23.10** | **0.957** | **1.733** | 0.69 % |
| **after Steps 4–6 run 3** | **6.02** | **11.34** | **22.46** | **0.933** | **1.685** | 1.09 % |

`ratioVersusRecordedBaseline = rps(i) / (i × 3,331,758.3)`, the recorded pre-change one-thread baseline;
the acceptance line for that reading is 0.6 (= 7,996,220 /s at four threads). Both readings and the
measured hit/miss counts come from the scenario's own `warmResolve` verdict block
(`cacheHits`/`cacheMisses`/`cacheMissRate` per thread count; run 1: 41,287,187 hits / 435,309 misses at
one thread, 161,803,018 / 1,708,278 at four).

Per-lookup CPU cost (threads × wall seconds / resolution): after Steps 4–6 **167.8 / 169.8 / 171.3**,
165.8 / 165.7 / 173.2, 166.2 / 176.3 / 178.1 ns at 1/2/4 threads — i.e. flat across thread counts,
against the before-series' 362–384 ns at four threads. A miss costs the failed cache probe only; the
production fallback is the dispatcher's slow path and is outside this resolve-shaped unit.

### Decomposition (why the four-thread wall fell)

Companion arms in the same runs (run 1, M res/s at 1/2/4 threads): `fake` 7.57 / 4.98 / 4.17 (gated
resolve, no self-traffic guard), `real` 3.47 / 3.39 / 2.75 (populated registry, **full** `IsOwned` +
gated resolve), `warm` 5.96 / 11.78 / 23.36 (lock-free wildcard guard + cache probe).

- The four-thread ceiling was the **shared serialized term**, and Step 4 removed it: `warm` 11.06 → 23.36
  M/s with the cache unchanged, while `fake` (gated resolve, no guard) still collapses 7.57 → 4.17 M/s.
  The `real` arm (full `IsOwned` under the registry gate) is unchanged at 2.75–3.47 M/s and remains the
  pre-change comparator.
- **The one-thread arm is now ~22 % below its Step-3 value** (6.81 → 5.96–6.03 M/s): an uncontended
  `lock` + `Dictionary` probe was cheaper (~13 ns/lookup) than the two lock-free
  `ConcurrentDictionary` probes (~36 ns/lookup, `fake − warm` at one thread). The self-normalised ratio
  therefore also benefits from a *lower* denominator; the recorded-baseline reading (fixed denominator)
  is quoted beside it for that reason. The cache probe itself is not free either: the
  `FlowTableProductionShape` rows below measure `ResolveWarmHit` at 76–80 ns against the gated
  `ResolveSameOrientationHit` at 61–75 ns at one thread (the seqlock fences + canonical hash), while at
  four threads the gated resolve was 362–384 ns. Net: **a small single-thread cost for near-linear
  scaling and a 2.0–2.2× four-thread rate**.
- The miss rate is 0.69–1.10 %, below the design's ~1.6 % target, so the slot-cap/K-way levers are not
  the binding term and were not used.

## UDP ready path (PRD AC 5)

`udp-ready-path-contention` (`--job short`, 3 runs), `ReadySend` mean, 0 B every row:

| run | 1 worker | 2 workers | 4 workers |
|---|---:|---:|---:|
| recorded (pre-change) | 350.2–361.4 ns | 403.6–405.8 ns | 498.8–523.3 ns |
| after 1 / 2 / 3 | 128.0 / 129.0 / 127.6 ns | 116.0 / 119.4 / 117.7 ns | **186.1 / 187.8 / 186.8 ns** |

The PRD line is the four-worker mean ≤ 261.7 ns: met with 2.7–2.8× headroom. The 4/1-worker ratio is a
host metric (recorded 1.45) and is **not** a criterion; the one- and two-worker rows are recorded beside
it. Raw exports: `udp-ready-path-contention-{1,2,3}.csv`.

## Flow-table production shape (the retired clock row)

`--job short`, 3 runs, 0 B every row (mean):

| row | recorded (2026-09-29) | after Step 2 | after Steps 4–6 (runs 1/2/3) |
|---|---:|---:|---|
| `ResolveSameOrientationHit` | 117.29 ns | 63.17 ns | 75.40 / 61.66 / 61.29 ns |
| `ResolveReverseAliasHit` | 138.42 ns | 98.09 ns | 76.63 / 65.84 / 66.50 ns |
| `ResolveWarmHit` (new) | — | — | 78.95 / 79.65 / 76.23 ns |
| `ReadActivityClock` | 40.56 ns | 40.35 ns | **retired** (the row is gone; `ResolveWarmHit` replaces it) |

The hit rows include the write-through and no longer read a clock; `ReadActivityClock` measured a call
the product no longer makes on any packet path, so it is recorded as obsolete rather than re-measured.
Raw exports: `flow-table-production-shape-{1,2,3}.csv`.

## Memory: the 2 MB warm array, the residency census and gc-soak

`residency-census-after.jsonl` (3 runs, `--quick --flows 100 --udp-flows 100`): the `flowTable` stage's
cumulative `managedBytes` is **35,446,160 / 35,458,448 / 35,446,160 B** against the recorded
same-invocation 2026-09-29 values **32,834,456 / 32,834,456 / 32,822,168 B** — **+2.61–2.62 MB (+7.9–8.0 %)**.
The `_warm` array is 262,144 slots × 8 B = **2,097,152 B** at the shipped 65,536 capacity; the remaining
~0.51 MB is process/GC accounting drift between the two trees (the zero-flow baseline stage differs by
only ~2.4 KB, so the drift is on the larger object graph, not the array). The design predicted +2 MB /
+6 %; the measurement confirms the array and the order of magnitude. The array is allocated once in the
`FlowTable` constructor, never grown, and holds only references — it adds no per-claim allocation (the
0 B claim/expire gate is green).

`gc-soak-after.jsonl` (`--quick`): `workingSetSlopeBytesPerSecond` 0, `poolOutstandingDelta` 0,
`poolOverflowDelta` 0, `udpSenderThreadAllocatedBytes` 0, gen0/1/2 = 5/2/0 — the F3 shape anchors hold
with the 2 MB LOH array resident. The array is a single long-lived LOH object allocated on the cold
composition path (never per packet, never reclaimed), which is why the soak's slope/growth anchors are
unaffected.

## Exact evidence

`warm-path-gate-counts.txt` carries every countable claim with its command, revision and exact
assertion text: the before counts per warm packet, the Step 2 clock/bucket facts, the Step 3 cache facts
and the reverted concurrent-index comparator (488 B/claim+expire, `Expected: 0, Actual: 124,928`), the
Step 4 naive-deletion red for the wildcard fact, the Step 5/6 red-before variants and the post-change
counts. `step3-blocked.patch` is the reverted concurrent-index implementation, kept as the measured
dead end.

Recorded red-before evidence for the structural facts (temporary variants, restored immediately —
exact text in `warm-path-gate-counts.txt`):

| fact | red variant | failure text |
|---|---|---|
| `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` | warm-entry check deleted outright (the naive variant requirement 2 first read) | `Expected: 1 / Actual: 2` on the proxy count |
| `WarmHitTakesZeroExactTupleGuardProbes` | pre-split tree (warm entry ran the full `IsOwned`) | `Expected: 1 / Actual: 2` |
| `EveryClaimCallsTheFullGuardExactlyOnce` | pre-split tree | `Expected: 8 / Actual: 16` |
| `ReverseResolveCompletesWhileRedirectGateIsHeld` | `TryResolveByReverse` forced through the gate | "the warm reverse resolve queued behind the parked redirect-table gate" (10 s) |
| `UdpReadySendCompletesWhileCoordinatorGateIsHeld` | ready probe removed (all sends via admission) | "the ready send queued behind the parked coordinator gate" (10 s) |
| `UdpReadySendTakesZeroActivityGateEntries` | `_activityGate` restored on the send admission | `Expected: 0 / Actual: 32` |

Must-not-move series: `tcp-redirect-data-path-after.csv` (16 rows, `--job short`) — 51.13–120.37 ns per
rewrite row, 0 B every row, against the recorded 51–129 ns; the rewrite rows do not include the table
lookups, so the redirect change's own proof is the probe/gate count above, not this series.

Repetition proof: `gate-stability.txt` — 20/20 consecutive green runs of `HotPathAllocationGateTests`
(Total: 11 each) and 20/20 of every structural class matching `WarmPathGateTests` (Total: 25 each),
one process per run with the padded summary and a non-vacuous totals assertion recorded.

Suite/build at the frozen tree: `dotnet build WinForward.slnx -c Release` 0 warnings / 0 errors;
`dotnet test WinForward.slnx -c Release` **1021 + 18 = 1039 passed, 0 failed**;
`HotPathAllocationGateTests` 11/11, `SweepAllocationGateTests` 12/12, `WarmPathGateTests` 12/12,
`SelfTrafficWarmPathGateTests` 4/4, `TcpRedirectWarmPathGateTests` 2/2, `UdpWarmPathGateTests` 7/7,
`DurableCaptureBundleTests` 11/11 (the tick fact included; corrected by the check pass — the earlier 15 did not
match the class's test count). `dotnet format` / `jb inspectcode` are the operator's gates (not run here by
instruction).

## Spec row → proof map

Every binding contract row this task touches, its restatement and the proof that pins it:

| spec row | after this task | proof |
|---|---|---|
| `hot-path.md` §3 (warm shape) | wildcard-only self-traffic guard; `TryResolveWarm`; one redirect fold probe; gate-free UDP ready path | `SelfTrafficWarmPathGateTests` (4), `WarmPathGateTests` (12), `TcpRedirectWarmPathGateTests` (2), `UdpWarmPathGateTests` (7); the parked-gate facts |
| `hot-path.md` §"FlowTable pooling" | the warm entry reads the validated view; a caller never holds the instance past the gate | `WarmCacheHitServesTheValidatedView`, `FlowTableRemovedFlowIsNeverServedFromItsOldSlot`, `FlowTableActivitySurvivesARecycledState` |
| `hot-path.md` §"FlowTable live-slot registry + chunked sweep" | expired is `ActivityBucket < cutoffBucket`, strict `<`, cutoff from the argument, `long.MaxValue` for ≤0 | `FlowTableRetainsAStateUntilTheBucketAfterItsIdleWindow`, `ZeroIdleTimeoutStillRetiresEveryStateOnTheCall`, `SweepAllocationGateTests` 12/12 |
| `hot-path.md` §"FlowTable warm cache" (new) | direct-mapped cache over the gated authority, false-miss-only | `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` (0 B), `FlowTableWarmResolveAllocatesNoManagedBytes`, `FlowTableCollidingFlowsFallBackToTheGatedPath`, `FlowTableTransportTupleIsUniqueAcrossOrigins`, `WarmResolveTakesNoFlowTableGateEntries` (0/256) |
| `hot-path.md` §"activity bucket" (new) | 500 ms, one clock, three tick sources, no warm clock read | `ActivityBucketClockReadsTheClockExactlyOncePerTickAndNeverOnAWarmResolve`, `ActivityBucketClockTicksExactlyOncePerPumpIteration`, `UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads`, `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` |
| `tcp-local-redirect.md` X1 row + §3 | `WantsPacket` unchanged; slow path full check; one `TryResolveByReverse` probe, no gate on a cache-resident warm packet (a collision costs the one gated probe; a forward flow whose source port is itself a live listener port costs one gated probe per packet — the prefilter is a candidate filter) | `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` (0 gates, 1 probe), `ReverseResolveCompletesWhileRedirectGateIsHeld`, `TcpReversePrefilterTests` |
| `traffic-policy-lifecycle.md` UDP activity row | bucket-derived stamp; coordinator scan pre-filter + `TryBeginExpiry` re-check | `UdpSessionCountMatchesTheLiveSessionsAcrossChurn`, `UdpProxyCoordinatorLifecycleTests` expiry family, `UdpProxySessionTests` |
| `traffic-policy-lifecycle.md` self-traffic row (new) | wildcard half on the warm entry, exact half at claim time; the accepted delta and its bound | `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` (naive-deletion red recorded), `ASelfOwnedTupleIsNeverClaimed`, `WarmHitTakesZeroExactTupleGuardProbes`, `EveryClaimCallsTheFullGuardExactlyOnce` |
| `udp-relay.md` §3/§4 | no `_activityGate` on the send path; ready hit = 0 coordinator gates / 0 activity gates / 0 clock reads; expired-but-admitted datagram is delivered (delta 5) | `UdpReadySendTakesZeroActivityGateEntries`, `UdpReadySendCompletesWhileCoordinatorGateIsHeld`, `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate`, `UdpSessionCacheMissesOnATornDownSlot`, `UdpSessionCacheNeverServesACollidingFlowsSession` |
| `async-lifetime.md` §"Lock order" | the UDP session no longer holds a gate across `TryEnter`; the lock-order property is unchanged | `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate`, the scope's own tests, `HotPathAllocationGateTests` 11/11 |
| `quality-guidelines.md` coordinator-gate row | the ready path reads the session cache; every mutation/removal and the slot dictionary stay gated | `UdpReadySendCompletesWhileCoordinatorGateIsHeld`, `UdpSessionCountMatchesTheLiveSessionsAcrossChurn` |
| `quality-guidelines.md` association-table row (narrow exception) | validated cache reads before `_gate`; misses consult the authority; every mutation gated and cache-clearing | `ReverseResolveCompletesWhileRedirectGateIsHeld`, the alias/exactly-once suite, `TcpProxyCoordinatorRewriteTests` |
| `quality-guidelines.md` `Touch` row | a *validated* resolution refreshes (bucket store); a rejected snapshot defers to the slow path | `WarmPathGateTests`, `TcpProxyCoordinatorRewriteTests.NonExactReverseProbeDoesNotRefreshActivity` |
| `quality-guidelines.md` `_count` rule | `Count`/`Capacity`/`SessionCount` stay exact with the gated authorities — no maintained counter | `TcpProxyCoordinatorAliasTests`/`TcpReversePrefilterTests`, `UdpSessionCountMatchesTheLiveSessionsAcrossChurn`, `FlowTableRegistryMirrorsCountAcrossChurn` |

## Residuals recorded, not fixed

- The dispatcher's **slow** path still reads the pooled instance after the gate is released (the
  pre-existing claim-path ABA); `TryResolve`/`TryClaimResolved` keep their signatures per the design, so
  the fix stays a follow-up (design §2.7 / D12).
- The accepted UDP delta (`design.md` §7 delta 5): a send admitted between the sweeper's idle re-check
  and its `_expiring` store is delivered instead of becoming a counted `UdpFailClosedDrop`. The window
  is a few instructions wide inside `TryBeginExpiry` under `_activityGate` and has no test seam; the
  landed fact (`SendIsAdmittedWhileTheSweeperHoldsTheActivityGate`) pins the ordering that makes it
  benign — the send does not wait on the gate and its datagram is delivered — not the counter flip
  itself. **This is the one acceptance gap of the task and is reported as such.**
- The self-traffic accepted delta: an **exact** registration made after a claim no longer diverts the
  next packet (the flow keeps proxying while it receives traffic); both registration sites and the bound
  are named in `design.md` §4/§7. The wildcard half is unchanged and pinned.
- The four-thread scaling ceiling is no longer lock-bound, but the one-thread arm pays ~36 ns/lookup for
  the lock-free wildcard guard where the uncontended gate cost ~13 ns. A copy-on-write wildcard snapshot
  (cold writes, one volatile read + immutable-set probe per hit) is the named lever if the operator
  wants the single-thread number back; it was not used because the design specifies the
  `ConcurrentDictionary` and the PRD criterion is met.
- **The cache is a fixed 262,144 slots at the shipped capacity, so AC 1's ratio is demonstrated for the
  measured working set, not for a full table.** At `--flows 4096` λ = 0.016 and the measured miss rate is
  0.69–1.10 %; design §2.1's own row already records that a *fully populated* 65,536-flow table has
  λ = 0.25 and a modelled ~22 % miss rate, which §2.5's arithmetic puts below the 0.6 line. The levers
  (raise the 262,144 cap; K-way probing, which needs an explicit nod because it changes the specified
  direct-mapped shape) were not used because the acceptance run's miss rate is far below the ~1.6 %
  target. The operator's archive record should carry this envelope: the ratio is a property of the
  measured working set, and the shipped capacity allows a working set four times larger than the one
  measured.
- **The zero-gate claim for the TCP forward direction is conditional on the listener-port prefilter.**
  `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` drives a forward packet whose source port
  is not a listener port, which is the common case; the prefilter only proves absence on a *miss*, so a
  forward flow whose source port number coincides with a live listener port takes one gated
  `TryResolveByReverse` probe per packet for as long as that listener exists (both ports are OS-assigned
  ephemerals). That is still one gate entry fewer than the pre-change candidate+resolve pair, and it is
  the honest boundary of the "zero redirect gate entries" figure.
- **A warm probe can serve an entry that a concurrent removal has just retired** — the one window the
  lock-free caches do not close. For the flow table the reader's snapshot must complete before the
  recycling `Reset` (a reader whose snapshot lands after it is a false miss); for the two TCP caches the
  association is never recycled, so a probe that loaded the slot before `RemoveUnderGate`'s guarded
  clear returns it and the coordinator handles one packet on a retired association instead of taking the
  tombstone grace drop. Both are the ordinary "reader linearized just before the removal" race and are
  bounded by one in-flight probe; closing them would need a per-slot version or a gate read, i.e. a
  design change. `hot-path.md` and `quality-guidelines.md` state the window; the code comments no longer
  claim the caches are miss-only.
