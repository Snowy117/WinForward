# F8 — process attribution off the pump thread, with an owner-table snapshot cache

Task: `.trellis/tasks/10-01-attribution-off-pump`. Finding F8 of the archived research
`09-29-tcp-udp-path-structural-perf` (addendum §A3).

## Host / runtime

| | |
|---|---|
| Host | NixOS 26.11, Linux |
| Runtime | .NET 10.0.12 (`RuntimeInformation.FrameworkDescription` in each row's metadata) |
| Base revision (before arm) | `ba51205` |
| Landed tree | the F8 tree (see `class-totals.txt` for the revision line) |
| Scenario mode | `--quick` (Flows = 64, 4 packets per flow) |

## Commands

```bash
D=benchmarks/results/2026-10-01-attribution-off-pump
# BEFORE (worktree at ba51205 + the pre-change scenario), 3 runs, one file per run:
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario attribution --quick --attribution-cost-ms 7 --output /tmp/wf-f8-before-$r.jsonl
# AFTER (landed tree), same three runs:
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario attribution --quick --attribution-cost-ms 7 --output /tmp/wf-f8-after-$r.jsonl
# the control arm (no modelled cost), each tree:
... --attribution-cost-ms 0 --output $D/attribution-control.jsonl
# tcpChurn calibration + must-not-move (5 % arm authoritative; the 1 % arm is recorded as invalid):
... --stability --scenario tcpChurn --quick --rate 60 --tcp-concurrency 8 --payload-bytes 4096 \
  --output $D/tcp-churn-baseline.jsonl
... --attribution-delay-ms 50 --attribution-delay-percent 5 --output $D/tcp-churn-delay-5pct.jsonl
... --attribution-delay-ms 50 --attribution-delay-percent 1 --output $D/tcp-churn-delay-1pct.jsonl
```

The runner truncates `--output` per process, so each arm was run once per file and the three files
were concatenated (`cat /tmp/wf-f8-after-{1,2,3}.jsonl > $D/attribution-off-pump-after.jsonl`).

## The headline: exact counts and the stall series

| Metric | BEFORE (`ba51205`) | AFTER (landed) |
|---|---:|---:|
| `attributionsOnPumpThread` | **64** | **0** |
| `attributionsOnSetupWorker` | 0 | **64** |
| `admittedEntries` | 0 | 64 |
| `retainedPackets` | 0 | 256 |
| `deliveredPackets` | 256 | 256 |
| `reAdmissions` | 0 | 0 |
| `pumpBlockedMs` p50 | 0.0039–0.0055 | 0.0038–0.0053 |
| `pumpBlockedMs` p95 | **7.519–7.561** | **0.019–0.028** |
| `pumpBlockedMs` p99 | 7.678–7.915 | 0.628–0.727 |
| `pumpBlockedMs` max | 31.5–33.7 | 17.1–18.0 |

The p95 moves by ~380×. The p50 does not move because only a flow's first packet pays attribution
(192 of the run's 256 packets are warm), which is exactly why the p95 — not the p50 — is the
acceptance statistic. The raw runs are `attribution-off-pump-before.jsonl` /
`attribution-off-pump-after.jsonl`; the exact-count detail is in `attribution-before-counts.txt`.

## The coalescing series (AC-4)

`attribution.ownerBurst` drives the **real** `WindowsProcessAttributor` over a scripted
`IProcessOwnerTableReader`: 16 concurrent new flows whose rows are scripted cost **1** system-wide
scan (`scansPerFlow = 0.0625`) in all three after runs. The before arm has no such row — the reader
seam does not exist at `ba51205` — so this is recorded as an after-only series, with the exact
statements carried by `ProcessOwnerTableCacheTests` (eleven facts — the nine the plan named plus the
unavailable-reader and out-of-range-window edges — including the forced rescan for a socket that
bound after the read, `window = 0`, and UDP never reusing a snapshot).

## tcpChurn calibration + must-not-move

| Metric | reproduce (`ba51205`) | F8 tree |
|---|---:|---:|
| `allocatedBytesPerConnection` (baseline / 5 % / 1 %) | 91,088 / 91,066 / 91,031 | 91,046 / 91,069 / 91,057 |
| attempts (baseline / 5 % / 1 %) | 918 / 935 / 920 | 918 / 934 / 921 |
| stable count (5 % arm) | 889 | 888 |
| delayed count (5 % arm) | 46 | 46 |
| delayed p50 (5 % arm) | 51.803 | 51.556 |
| `gen0Collections` | 5 | 5 |

Allocation is within 0.06 % of the reproduce series — the `must-not-move` row holds. `tcpChurn` never
runs the dispatcher, the pump or the attributor (its `--attribution-delay-ms` is a client-side
`Task.Delay`), so it is **calibration only**, exactly as the amended PRD states.

Pooled percentile arithmetic (the artifact's own nearest-rank rule, `StabilityShared.Percentile`):

- **baseline**: the stable class *is* the pooled set → pooled p99 = **6.951 ms**.
- **5 % arm** (authoritative): attempts 934, stable 888, delayed 46; pooled p99 rank
  `ceil(0.99 × 934) = 925`, inside the delayed block (ranks 889–934) → pooled p99 ≈ **52.8 ms**.
- **1 % arm** (recorded as **invalid**): attempts 921, stable 912, delayed 9; pooled p99 rank
  `ceil(0.99 × 921) = 912` = the largest *stable* sample → **90.036 ms**, a host-scheduling outlier,
  not the stall. Re-pooling cannot be derived from the per-class summaries, which is why the rank is
  computed from the counts here.

## Budgets

| Bound | Value | Basis |
|---|---|---|
| Pending entries | 1,024 | `FlowAttributionPendingIndex.DefaultCapacity` (the TCP pending index's cap) |
| Global retained bytes | 8 MiB | `FlowAttributionPendingIndex.DefaultGlobalByteBudget` (the UDP setup queue's budget) |
| Per-flow ring | 32 packets | the UDP setup queue's per-flow shape |
| Entry TTL | 5 s | the TCP pending index; a backstop, swept on the sweeper's fast tick |
| Failure / claim-failure cooldown | 1 s | the TCP pending index |
| Setup ring | 2,048 | **the finite relation**: `DefaultRingCapacity ≥ TcpPendingSynSetupIndex.DefaultCapacity (1024) + FlowAttributionPendingIndex.DefaultCapacity (1024)` |

**A UDP setup item can still be refused when the ring is full.** `UdpProxyCoordinator.Send.cs`
enqueues one item per admitted session against a 16,384 session capacity, so no inequality of the
form `ring ≥ tcpCap + flowCap + udpHeadroom` is satisfiable. That refusal is already counted
(`RuntimeCounters.UdpSetupRejections`) and fail-closed, and it is **explicitly accepted**; the
load-bearing form of the relation is the test
`SetupExecutorTests.DefaultRingCapacityCoversBothFiniteProducerCaps`.

## Exact vs series

- **Exact** (gates/assertions): the attribution thread counts, the admission/delivery counts, the
  ring and budget bounds, the refusal counters, the scan/snapshot equality, the allocation gates.
- **Series** (report-only): `pumpBlockedMs`, `firstPacketDelayMs`, `ownerTableScansPerBurst`,
  `allocatedBytesPerNewFlow`, and every `tcpChurn` timing.
- The `attribution` row measures the **pipeline**, never `iphlpapi`. The real system-wide enumeration
  cannot run on this host, so its cost is the `--attribution-cost-ms` model and its table is
  scripted. `allocatedBytesPerNewFlow` is process-wide and includes the harness, so it compares only
  between runs of this row.

## Instrument hashes

| Arm | File | sha256 |
|---|---|---|
| after (recorded series) | `benchmarks/WinForward.Benchmarks/Stability/AttributionOffPumpScenario.cs` | `039b7cda9b6f6c5c7de8efe1f1ad3081bc4b6e7f82647f375f4efaa20b26f124` |
| after (check round) | same path, `refusedPackets` added | `feed625b6fabcce1b8fe23c34ef46e6a107fd65a72442f457c37fc732406ef0e` |
| before | (worktree `ba51205`) same path | `3302141a3999b264c56992a22b10df4c5078f47d3f78eccf5b500c2028a3245b` |

The recorded after series predates the check-round field: the scenario now also reports
`refusedPackets` (`design.md` §8 / `implement.md` Step 2 list it), which the recorded runs lack. The
field's emission and value were verified by one post-fix diagnostic run into `/tmp` (not into this
directory, so the recorded series stays the one the tables above quote):
`--scenario attribution --quick --attribution-cost-ms 7` → `refusedPackets = 0`, with the exact counts
unchanged (`attributionsOnPumpThread = 0`, `attributionsOnSetupWorker = 64`, `admittedEntries = 64`).
Nothing else in the row changed, so the tables remain comparable.

The workload, packet script, counters and metrics are identical between the arms. The **only**
differences are the arm wiring — the after arm passes `attributionPool:` / `setupExecutor:` to the
dispatcher and calls `pipeline.DeliverDecided(handle)` between packets, and it carries the extra
`attribution.ownerBurst` row — because the pre-change product API has no pipeline to wire. That
difference is the change under test, not a measurement change.

## Windows open items (design §9) — recorded verbatim, none closed on this host

| # | Item | Experiment | Expected | Fallback |
|---|---|---|---|---|
| W1 | Real per-flow attribution cost / table size | the `attribution` scenario on Windows with a process rule and the real attributor: `pumpBlockedMs`, `attributionsOnSetupWorker`, `ownerTableScansPerBurst` | scan 0.5–7 ms off-pump; one scan per epoch | if attribution is cheaper than the pipeline, the coalescer still removes the burst's redundant scans |
| W2 | Snapshot hit rate over a browsing burst | production `ownerTableScansPerBurst` in the heartbeat | ≪ 1 scan per flow (series, never a threshold) | window `0` keeps coalescing only |
| W3 | UDP local-port recycling inside 300 ms | compare UDP snapshot lookups with fresh scans | rare | UDP stays cache-off until an `OWNER_MODULE` creation-timestamp check exists |
| W4 | Arrival-signal availability (F5's open item) | F5's `SetPacketEvent` experiment | available on modern NICs | no wake: delivery latency bounded by the poll delay or the idle timeout |
| W5 | `OpenProcess` failure rate | `flow.attribution-miss` (`afterRetry=true`) vs today's baseline | unchanged | today's miss path |
| W6 | F8 + R8 double window | a run with a process rule *and* a TCP redirect rule; both pending counters | independent windows; the SYN is deferred by F8 then retained by R8 | the F8 drain hands the packet to the executor, which is the R8 entry |

## Residue the operator carries

- **The deferred UDP snapshot** (§3.3): needs the `*_TABLE_OWNER_MODULE` creation timestamp, which is
  part of the pinned Windows API surface the PRD puts out of scope.
- **Accepted UDP ring refusals**: counted (`UdpSetupRejections`) and fail-closed; no satisfiable
  inequality covers UDP.
- **The 60 s TTL sweep discrepancy** (implementation-notes D6): the TCP pending index's documented
  5 s TTL rides the 60 s main leg; F8's own TTL has a dedicated leg on the fast tick for that reason.
- **The bounded slow-path re-entry** (§5.4.5): a pending flow's packets re-enter the full slow path,
  including the gated exact-tuple self-traffic check and the reverse probe, once per packet instead
  of once per claim — bounded by the ring (32) and the TTL (5 s), and restated in `hot-path.md`.
- **The TTL sweep is a backstop**: the delivery path, not the sweep, normally ends an entry's life.

## Check round (independent verification, 2026-10-01)

An independent verification round re-derived every PRD criterion from the landed code and these
artifacts, attacked the interleavings the implementer listed, and closed the recorded gaps. Verdicts:

| PRD criterion | Verdict | Basis |
|---|---|---|
| AC-1 off-pump, per admitted entry | **PASS** | `attribution-off-pump-{before,after}.jsonl`: 64 → 0 pump-thread, 0 → 64 setup-worker, `admittedEntries` 0 → 64, reproduced by `FlowAttributionPipelineTests`; re-admissions and failed claims counted (`ASecondKeyOfOneTupleIsCountedAsAReAdmission`, `AFailedClaimIsCountedAndDoesNotReAttributePerPacket`) |
| AC-2 order and exactly-once | **PASS** | `PacketsAreDeliveredInArrivalOrderAfterTheDecision`, `NoPacketIsDeliveredTwice`, `AClaimIsPublishedOnlyAfterTheLastPendingPacket`, `AnEnqueueDuringADrainDoesNotDisturbIt`, `AFailingSetupBlocksEveryPendingPacketAndNothingIsStranded`, `AThrowingExecutorBlocksTheBatchRemainderAndNothingIsDetached`, `AShutdownCancellationArmsNoCooldown` |
| AC-3 bounded and fail-closed | **PARTIAL** | the caps, budget, ring, TTL, seal and cooldown facts are green, and the conservation identity now also holds under the ring-full, claim-failed and duplicate-claim sinks (`TheGlobalBudgetIsConservedUnderTheRefusalAndDuplicateClaimSinks`). The partial half is the reporting channel: seven `RuntimeCounters` keys the design promised were **not incremented at all** until this round (fixed; see `implement.md` C1). The counters themselves were never un-bounded |
| AC-4 snapshot exact + series | **PASS** | `ProcessOwnerTableCacheTests` 11/11 (one read per coalesced epoch, the later-epoch retry, one forced rescan per late socket, TCP-only reuse, UDP never reusing, window expiry, scan/snapshot equality); `attribution.ownerBurst` = 1 scan per 16 concurrent flows in all three runs |
| AC-4 movement / calibration | **PASS** | the `attribution` series above; `tcpChurn` 5 % arm as calibration + must-not-move (allocation within 0.06 %), pooled percentile recorded explicitly, the 1 % arm recorded invalid |
| Allocation | **PASS** | the four pump-loop gates green; `PendingAdmitAllocatesOnlyTheDocumentedColdBudget` green and its discrimination re-proven; 91,046/91,069/91,057 B against the reproduce 91,088/91,066/91,031 |
| F1 regression surface | **PASS** | `NdisPacketActionExecutorBatchingTests` 14/14, `TcpRedirectInjectionBatchingTests` 14/14, `WarmPathGateTests` 25/25, dispatcher classes 28/28 |
| Semantics preserved | **PASS** | the shared `ShouldAttribute`/`AttributeProcessAsync`; the eligibility negative facts; the predicates moved verbatim into `OwnerTable` (checked line by line against `ba51205`); the lifecycle suites green |
| Existing gates | **PASS** | full suite 1,141 green; the per-gate loop below |
| Windows-only paths behind the seam | **PASS (recorded)** | the W1–W6 table above, `attribution-before-counts.txt` §4, and the residual risks below — every number in this directory is seam-level |

Gaps this round closed, and its own fixes, are itemised in `implement.md`'s "Check round" table (C1
counter wiring, C2 the wake-registry event leak, C3 TTL packet accounting, C4 the refusal-path wake,
C5 `refusedPackets`, C6 the spec totals string, C7 the fact count, C8 the flaky coalescing fact). What
the round could **not** verify on this host — and what the operator must carry as a residual — is in
`implement.md`'s A1–A6 table; the load-bearing one is A1: the per-adapter ownership invariant rests on a
1:1 handle→pump map that only a Windows run can falsify.

**Suite-level reading (not a guarantee).** Sixteen full-suite runs on the frozen tree: **three
consecutive fully green (1,124/1,124 each)** on the final tree, two more green runs before the last
flake fix (run 12-13), and five earlier runs with one failure each plus one with two (one of them
diagnosed and fixed — the intermittent worker-race fact — the other lost to a pipe and unreproduced)
— a recorded victim allocation gate whose `Actual` was lost to a pipe, the C8 flake (fixed), a 280 B
lump in `HotPathAllocationGateTests` that matches none of the recorded signature sizes (incomplete
diagnosis, itemised), and an unrelated UDP-association teardown race. Every F8 class was green in every
run and 5/5 in its own per-gate process runs; the two allocation-gate classes re-ran 10/10 green in
isolation. That is the host property `hot-path.md`'s residual-lump section already records, and it is
why `gate-stability.txt` (not the suite loop) is the per-gate acceptance evidence. **"Full suite green"
here means a green run, not a stable suite.**
