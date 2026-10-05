# 2026-10-01 UDP per-session footprint — before/after evidence (F6)

> **Removed feature, 2026-10-05 (task `10-05-remove-udp-association-sharing`, R6).** UDP association
> sharing no longer exists — the three configuration keys were removed, the pool / lease /
> control-association / capability machinery was deleted, and a proxy-decided flow now owns its own
> authenticated association. The `session-budget-*.jsonl` rows below therefore carry columns of the
> removed feature: `leasedFlows`, `associationsPerSession`, the `pooling` block,
> `verdict.poolingCovered` / `requirePooling`, and the recorded `udp.association.fallback` product
> event, while the prose cites the removed `FlowsPerAssociation` fan-out. Their `--require-pooling`
> commands are **historical**: the flag is gone from the harness, and a command line that still passes
> it fails at parse with the harness's unknown-argument error. **No number in this directory was
> edited or deleted** — this note is the only change.

Task `.trellis/tasks/10-01-udp-session-footprint` (finding F6 of the archived
`09-29-tcp-udp-path-structural-perf` research): the relay socket's per-session kernel receive
buffer (128 KiB → 64 KiB default), the shared native receive-window pool (default capacity 256 →
derived from the configured session capacity plus a retire allowance), and the idle retention
uniform 30 s → a 5 s completed-one-shot class with the sweep cadence derived from the effective
floor (15 s → 5 s).

**Host / runtime.** NixOS 26.11 (Linux 6.18.53, x86_64), AMD Ryzen 9 9955HX (16 cores / 32
threads), .NET SDK 10.0.401, runtime 10.0.12, Release. Base revision for the before-artifacts:
`91be11c7d89ba59dc85d26ccde375068d48aa486` (`git write-tree`
`e7fb73fd0089286d87c4d98ec07cb34eec5282b5`), with only the Step 1 instrument change on the tree.

**Attribution rules used throughout.**

- **Exact counts** (an assertion, a counter, a balance identity) prove everything about the pool,
  the buffer default and the retention boundaries; every such number cites the test that asserts it.
- **Series** (the footprint, soak, loss/burst/churn anchors) are compared against a recorded band;
  a movement inside one band is not a result, a movement across bands stops the step. Series are
  quoted as the median of the runs stated beside them.
- **The kernel receive buffer is arithmetic, never a measurement.** No managed instrument can
  observe `SO_RCVBUF`; every byte figure below is `live sessions × configured bytes` (a MiB/GiB
  product), and the only exact socket statement is the applied-value read-back recorded in
  §"Applied relay receive buffer".
- **The first populate is not a signal.** `NativeBufferPool.OverflowAllocations` counts every rent
  that missed the free list, so a first fill of N sessions reads N at any capacity; only the growth
  a second population cycle adds (`max(0, N − capacity)` plus free-list slack) discriminates
  (design §3, notes §9/D1).

## Commands

### Before (pre-change tree, Step 1 instrument only)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario footprint \
  --output benchmarks/results/2026-10-01-udp-session-footprint/footprint-before.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpSessionBudget \
  --rate 100 --churn-seconds 90 --drain-seconds 120 --socks5-external --require-pooling \
  --output benchmarks/results/2026-10-01-udp-session-footprint/session-budget-before.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp \
  --pps 25000 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-loss-before.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpBurst \
  --burst-flows 48 --dial-delay-ms 100 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-burst-before.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpChurn \
  --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-churn-before.jsonl
```

One run per before-file (the before-tree series is the baseline, not a claim about the change).
The after-series are three runs each, concatenated (the runner truncates `--output` per process).

### Red-before (a test, not a scenario)

```text
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~UdpReceiveWindowPoolTests"
```

Run **before** the composition change with the pre-change capacity (256) and a 300-session
population cycle: `first=300 second=344 growth=44 rented=600 returned=600 inPool=256
outstanding=0`. Captured transiently; the Step 3 fact asserts the green side of the same cycle.

### After (landed tree)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario footprint \
  --output benchmarks/results/2026-10-01-udp-session-footprint/footprint-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpSessionBudget \
  --rate 100 --churn-seconds 90 --drain-seconds 120 --socks5-external --require-pooling \
  --output benchmarks/results/2026-10-01-udp-session-footprint/session-budget-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp \
  --pps 25000 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-loss-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpBurst \
  --burst-flows 48 --dial-delay-ms 100 --duration 60 \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-burst-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpChurn \
  --burst-flows 48 --churn-waves 0 --duration 120 --socks5-external \
  --output benchmarks/results/2026-10-01-udp-session-footprint/udp-churn-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario retention \
  --output benchmarks/results/2026-10-01-udp-session-footprint/session-retention.jsonl
```

The retention arm has **no before-artifact by construction**: it measures a mechanism the
pre-change tree does not have, so it carries its own control (two identically built cohorts, one
shared clock, one sweep per cohort at the same instant: the control with equal TTLs, the treatment
with the 5 s class). Its two rows differ in nothing but the classification.

## Before / after tables

### `udp.sessionFootprint` (1 / 100 / 1000 sessions)

| Sessions | workingSetDeltaBytes | gen0Collections | allocatedBytes | Source |
|---|---|---|---|---|
| 1 | 0 | 0 | 62,624 | `footprint-before.jsonl` |
| 100 | 0 | 0 | 386,864 | `footprint-before.jsonl` |
| 1000 | 0 | 0 | 3,590,600 | `footprint-before.jsonl` |
| 1 | 0 | 0 | 62,632 | `footprint-after.jsonl` |
| 100 | 0 | 0 | 394,904 | `footprint-after.jsonl` |
| 1000 | 0 | 0 | 3,669,360 | `footprint-after.jsonl` |

`workingSetDeltaBytes` is expected to be 0 (the fake transports open no socket and the native pool
is small); the historical 2026-08-29 rows (`benchmarks/results/2026-08-29-udp-fix/`) are cited only
as a shape and predate five structural tasks.

### `udp.sessionFootprint.cycle` — the pool claim (red before, green after)

| Sessions | overflowFirstWave | overflowSecondWave | Growth | Source |
|---|---|---|---|---|
| 1 | 1 | 1 | 0 | `footprint-before.jsonl` |
| 100 | 100 | 100 | 0 | `footprint-before.jsonl` |
| 1000 | 1000 | 1742 | **+742** | `footprint-before.jsonl` (red) |
| 1 | 1 | 1 | 0 | `footprint-after.jsonl` |
| 100 | 100 | 100 | 0 | `footprint-after.jsonl` |
| 1000 | 1000 | 1000 | **0** | `footprint-after.jsonl` |

The before-run's 1,000-session growth is `max(0, N − 256) = 744` less two leases: `Release` admits
a return when a racy `InPool < Capacity` read passes, so the free list held 258 idle leases when
the second cycle began (the pool's documented bounded-approximate shape, `NativeBufferPool.cs`).
The in-process red-before at exactly 300 sessions reads the arithmetically exact `+44`.

### Must-not-move anchors

| Scenario | Anchor | Before | After | Band (recorded 2026-09-28) |
|---|---|---|---|---|
| `udp --pps 25000 --duration 60` | loss / received | 0 loss; 1,499,750 sent = received; 24,994.4 pps | 0 loss in all 3 runs (1,499,539 / 1,499,589 / 1,499,536 sent = received; 24,989.7 pps median) | loss 0, ≈1,499,000 received |
| `udpBurst --burst-flows 48` | first responses / establishmentLossRate | 48/48, rate 0, p50 202.3 ms | 48/48, rate 0 in all 3 runs (p50 203.1 / 203.6 / 205.8 ms; background loss 0 in every arm; 0 unattributed) | 48/48, rate 0 |
| `udpChurn --churn-waves 0 --duration 120` | sessions / bytes per session / loss | 223,008 sessions, 7,430.2 B/session, loss 0, 0 rejected | 0 loss and 0 rejected in all 3 runs; 7,436.8 B/session median (7,436.7 / 7,436.8 / 7,454.9 — inside the ≤8,200 band); session counts 222,816 / 169,584 / 111,216 (median 169,584; the achieved rate varies run to run and is not the anchor) | 222,720 sessions, 7,577.5 B/session, ≤8,200 band, loss 0 |

### `udpSessionBudget` soak (acceptance load: `--rate 100 --churn-seconds 90 --drain-seconds 120`)

Both arms below were captured with the **fixed instrument**: the run now ticks the coordinator's
`ActivityBucketClock` once per driven iteration (`UdpSessionBudgetRun.TickActivityClock`), which is the
cadence production's capture pump gives it in `DurableCaptureBundle.FlushPendingInjections`. That mirror
is load-bearing, and its absence was a real defect in the instrument rather than in either product arm:
with a frozen clock every session carries the coordinator's construction bucket, so the first sweep past
the retention mass-retires the whole population and the run measures a sawtooth artifact. The recorded
frozen-clock artifacts are kept beside the fixed ones
(`session-budget-{before,after}-frozen-clock.jsonl`) so the difference is auditable — they read a steady
peak of 4,479 (before) and 500 (after), and the after one is the run whose `poolingCovered` verdict
failed.

| Metric | Before (pre-change worktree) | After (median of 3) | Source |
|---|---|---|---|
| `steadyStateSessions` (arrival-granular peak) | **4,539** | **1,014** (1,014 / 1,038 / 1,014) | `session-budget-{before,after}.jsonl` |
| churn / sampled peak sessions | 4,039 | 535 (533 / 559 / 533) | |
| **association high-water** (`steadyStatePeakAssociations`) | **284** (4,539 / 284 = 16.0) | **64–65** (1,014 / 64 = 15.8) | |
| estimated kernel receive buffer (sampled row) | 529,399,808 B = 504.9 MiB (4,039 × 128 KiB) | 34,930,688–36,634,624 B = 33.3–34.9 MiB (533–559 × 64 KiB) | |
| descriptors per session | 1.0696 | 1.118–1.122 | |
| drain to zero (elapsed since run start) | 135.4 s (≈30 s into the 120 s drain) | 100.26 s (≈10 s into the drain) | |
| final drain row | 0 sessions / 0 assoc / 0 leases, +3 descriptors | same (all 3 runs) | |
| loss / rejected / expired | 0 / 0 / 9,016 | 0 / 0 / 9,016 (all 3 runs) | |
| `verdict.retentionBounded` / `poolingCovered` / `pooling.saturated` | true / true / false | **true / true / false** (`passed: true`, all 3 runs) | |
| `sweepIntervalSeconds` / `oneShotIdleTimeoutSeconds` | 15 / — | 5 / 5 | |
| reported ceiling / `minimumDiscriminatingChurnSeconds` | 6,200 / 63 | 4,200 / 43 | |

The retention change removes `(4,539 − 1,014) / 4,539 = **77.7 %**` of the resident set at the same load,
and the association high-water falls with it (284 → 64) because the pool no longer opens control
connections for a population that lives 5 s instead of 30 s. Both arms keep the pooling fan-out at the
ideal 1/16, which is what the pooling half asserts.

**Stage-2 re-base (measured).** The PRD sanctions re-deriving the acceptance from the **measured**
resident set, and the measured median is 1,014, so `AcceptanceLoadSessions` moved 4,500 (pre-change
measurement) → 1,700 (stage 1's bound-derived intermediate, so no commit was red) → **1,014** (measured),
and every row derived from it moved with it:

| Moved literal | Stage 1 | Stage 2 (measured) |
|---|---|---|
| `AcceptanceLoadSessions` (doc names the artifact row and runs) | 1,700 | **1,014** |
| ideal fan-out `ceil(N/16)` | 107 | **64** |
| `SharedAssociationCeiling` at the shipped cap, and at the product-max cap | 123 | **80** |
| the small-cap row (`ceil + 4`) | 107 + 4 | **64 + 4** |
| tenth-held-privately: private / shared / total | 170 / 96 / 266 | **101 / 58 / 159** |
| the `above the shared-placement ceiling` sentence | 123 | **80** |
| `ThePostCapsAcceptanceShapePassesBothHalves` associations (sampled row and arrival peak) | 109 | **66** |
| expressions derived from the constant (`SessionsBeyondSharedBudget = N − 256`, the arrival-granular row's sampled/placed counts) | — | derive from the constant, unchanged in form |

The stage-1 rows stay as landed and still hold at the measured load: ceiling 6,200 → 4,200,
`MinimumChurnSeconds` 63 → 43, the refusal sentence "above 62" → "above 42", the default-rate ceiling
1,240 → 840, and the two `6_200L` budget literals → `4_200L`. What each verdict field tests at the
measured load: `retentionBounded` compares the arrival-granular peak (1,014) against the ceiling — a
ceiling derived from the *configured* retention at the *derived* cadence
(`rate × (idle 30 s + 2 × sweep 5 s) + margin 200 = 4,200`), so it bounds accumulation rather than
pinning the shipped one-shot band; see the discrimination limit in "Residual risks" below.
`poolingCovered` compares the association high-water (64) against `ceil(1,014/16) + 16 = 80`, the
descriptor budget sees 1.12 descriptors/session against the 1.25 slack budget, and the drain ends at
zero sessions/associations/leases. The discrimination precondition is re-checked at the new ceiling:
`100 × 90 = 9,000 > 4,200` (2.1×).
### `udp.sessionRetention` (control / treatment, no before-artifact)

| Arm | oneShot | sustained | shortTtlSeconds | longTtlSeconds | oneShotResident | sustainedResident |
|---|---|---|---|---|---|---|
| control | 32 | 32 | 30 | 30 | 32 | 32 |
| treatment | 32 | 32 | 5 | 30 | 0 | 32 |

All three runs of `session-retention.jsonl` read exactly these values (6 result rows): at the same
instant, with identically built cohorts and one shared clock, the control (both timeouts = the
configured retention) retires nothing while the treatment (the 5 s one-shot class) retires exactly
the one-shot class and keeps every sustained session. The only difference between the two rows is
the classification, so this series is exactly attributed — and the one-shot population at the
soak's `--rate 100` is where the resident-set effect lands end to end.

## The re-based soak cadence and ceilings

The soak's arithmetic is `rate × (idle + 2 × sweep) + margin`, with the sweep interval taken from
`IdleExpirySweeper.DeriveUdpSweepInterval` (a mirror of the production derivation). Moving the
effective retention floor to the 5 s one-shot class moves the rate-100 ceiling 6,200 → 4,200, and
every row derived from it moves with it. This is a **re-base with a reason, not a weakened
anchor**: the discrimination precondition (`rate × churnSeconds > ceiling`) is re-checked at the
new ceiling (9,000 > 4,200, 2.1× headroom; it was 1.45×).

| Moved row | Before | After |
|---|---|---|
| `UdpSessionBudgetAcceptanceTests.s_sweep` | 15 s | 5 s |
| rate-100 steady-state ceiling | 6,200 | 4,200 |
| `MinimumChurnSeconds` (rate 100) | 63 | 43 |
| refusal sentence | "above 62" | "above 42" |
| default-rate (20) ceiling | 1,240 | 840 |
| `TheRetentionHalfCoversTheEstimatedKernelReceiveBuffer` literals | `6_200L` | `4_200L` |
| `AcceptanceLoadSessions` | 4,500 (pre-change measured peak) | 1,700 (stage 1, bound-derived) → **1,014** (stage 2, measured median) |
| rows derived from the load | `ceil(N/16) + 16`, tenth-private, `N − 256` | moved with it (stage 1: 107/123, 266, 1,444; stage 2: 64/80, 159, 758) |
| `ThePoolingCheckReadsTheArrivalGranularPeakNotTheSampledRow` (not in the plan's list — its 4,478/282/4,000 sample literals were **above** the new 4,200 ceiling, so the row would have inverted) | fixed measured samples | re-expressed from `AcceptanceLoadSessions` and `FlowsPerAssociation`, keeping its discriminating assertion (the sampled shape alone still exceeds its own ceiling) |

Stage 1 used the bound-derived `rate × (short 5 s + 2 × sweep 5 s) + margin 200 = 1,700` so no commit
was red. **Stage 2 is applied from the fixed instrument's measurement**: the median after-arm
`steadyStateSessions` is **1,014** (1,014 / 1,038 / 1,014), so the constant and every row derived from
it moved to the values listed in the soak section. For the record, the frozen-clock instrument read
**500** at the same load and the same tree — the figure stage 2 would have encoded had the instrument
defect not been found and fixed first; both numbers are stated here so the difference is attributable.

## Projection table (arithmetic on a configured value — MiB/GiB, never decimal MB)

| Quantity | Before (128 KiB) | After (64 KiB) |
|---|---|---|
| at the shipped capacity (16,384 sessions) | 16,384 × 128 KiB = **2 GiB** | 16,384 × 64 KiB = **1 GiB** |
| the 1 h soak's measured peak (4,500 sessions) | 4,500 × 128 KiB = **563 MiB** | 4,500 × 64 KiB = **281 MiB** |
| the soak's sampled 4,000-session row | 4,000 × 128 KiB = **500 MiB** | 4,000 × 64 KiB = **250 MiB** |
| the post-TTL one-shot population (measured 1,014-session steady peak at `--rate 100`, median of 3) | 1,014 × 128 KiB = 126.75 MiB | 1,014 × 64 KiB = **63.4 MiB** |
| receive-window pool high-water at the shipped capacity | unbounded alloc/free churn beyond 256 leases | (16,384 + 1,024) × 1,541 B = **25.6 MiB** (24.1 MiB population + 1.5 MiB retire allowance) |

## Retention bounds (with their direction)

| Bound | Before | After | Direction |
|---|---|---|---|
| long class (configured 30 s), worst-case retirement age | 30 + 0.5 + 15 = 45.5 s | 30 + 0.5 + 5 = **35.5 s** | tightens |
| short class (5 s) | — | (5, **10.5**] s | new class, by design |
| F8 attribution pending-entry lifetime (same tick, no bucket term) | (5, 20] s | (5, **10**] s | tightens |
| TCP pending-SYN TTL | (5, 65] s (main leg) | unchanged | not affected (rides the main leg) |

## Gates on the landed tree

| Gate | Result | Artifact |
|---|---|---|
| `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** | — |
| `dotnet test WinForward.slnx -c Release` | **1,159 passed, 0 failed** (18 analyzers + 1,141 core; baseline 1,142 → +17 new facts) | — |
| Per-gate process proof (4 gate classes × 20 own-process runs, `git write-tree` fingerprint, totals assertion, injected-allocation signature predicate) | **80/80 green, 0 host-lump events** | `gate-stability.txt` |
| Independent per-gate re-derivation (109 own-process runs: the new gate ×20, the ten other recorded allocation-gate classes ×5–10 with the sweep arm re-run under the namespace-qualified filter, and the six non-gate classes this task grew ×3) | **every run exit 0, 0 gate failures, 0 host hits, test binary hash unchanged across the loop**; one arm exposed the filter defect below instead of failing a gate | `gate-stability-verify.txt` |
| Per-class fact totals (every class this task added or edited) | all green; totals quoted for the spec's refresh | `class-totals.txt` |
| New exact gate's discrimination | one `GC.KeepAlive(new byte[64])` inside the measured window failed `UdpProxyCoordinatorAdaptiveSweepAllocatesNoManagedBytes` at **`Actual: 88`**; the same form failed the pump gate at `Actual: 88000` (1,000 × 88 B); both restored → green. The probe form is load-bearing: a bare `_ = new byte[64]` is dead and the JIT removes it (measured 2/2 runs passing) | `gate-stability-verify.txt` |
| Red-before kept falsifiable | `UdpReceiveWindowPoolTests.TheRetiredDefaultCapacityGrowsByTheUnpooledPopulationAcrossACycle` asserts the retired capacity's `+44` cycle growth in-tree, so the green cycle gate cannot pass on a counter that stopped reporting growth | recorded here |
| `dotnet format --severity info --verify-no-changes` / `jb inspectcode` | **not run** — the operator runs both commit gates | — |

Report-only companions the sweep cadence and the evidence seam could touch (one `--quick` run each, all
exit 0): `attribution` — 64 flows admitted, `attributionsOnPumpThread: 0`,
`attributionsOnSetupWorker: 64`, 0 refused, `ownerTableScansPerBurst: 1`; `residency` — census only;
`gc-soak` — `workingSetSlopeBytesPerSecond: 0`, 0 Gen2. See `attribution-after.jsonl`,
`residency-after.jsonl`, `gc-soak-after.jsonl`.

## Independent verification (2026-10-01)

Re-run on the uncommitted working tree. Its fingerprint is `git stash create` tree
`456567417bf0b8214c2616a728b84a5d32ea2dbe` plus the sorted untracked-file hash `dbc5d5e784544804`; the
`e7fb73fd…` "tree fingerprint" quoted in the header and in `gate-stability.txt` is the **index** tree —
with nothing staged it is the base revision's tree, so it identifies the base, not the tree under test.
The bullets below were measured on that tree; the record fixes they produced were then applied and
re-verified on the final tree (tracked-tree fingerprint `6c580ac8062054b3e51c675f6933777716e0c684`,
untracked code+test hash `42094eaa44e30d4d` — the artifact and task directories are excluded so this
record can name its own tree without changing it): `dotnet build -c Release` → **0 warnings**,
full suite → **green (18 analyzers + 1,141 core)**, and `UdpAdaptiveSweepAllocationGateTests`,
`SweepAllocationGateTests`, `UdpReceiveWindowPoolTests`, `UdpSessionRetentionTests`,
`ConfigurationLimitsTests` and `Socks5UdpTransportLeaseTests` green in their own processes. The
operator's commit gates then reported 10 `dotnet format` info diagnostics (fixed, V11–V15) and 16
`jb inspectcode` issues (fixed, V16–V21) on this surface; both fix sets were followed by a green build,
the affected facts and the full suite on the fingerprint above. The dispositions are listed in
`implement.md` ("Verification dispositions").

- **Build**: `dotnet build WinForward.slnx -c Release --no-incremental` → 0 warnings, 0 errors.
- **Suite**: 9 full runs on the unchanged binary → 8 green (18 analyzers + 1,141 core) and one failure,
  `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes`, `Expected: 0 / Actual:
  8120`. That is the recorded suite-conditional host-lump family (`hot-path.md` "Allocation-gate
  stability"): a recorded victim gate whose `stabilized` preflight passed, `8,120 = 8,192 − 72` (the same
  `8,192 − k` shape as the counter-read probe), and 3/3 green in isolation. Rate 1/9 = 11 %, the recorded
  order. **8,120 B is not in the documented per-gate signature list** `168|5216|7384|7448`; that list was
  deliberately not extended here (the observation is in-suite, and the per-gate proof samples a different
  process shape), so the operator decides. A tenth suite run (after the commit-gate fixes below) hit the
  same family on a different recorded victim gate,
  `NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes` — green 14/14 in its own process and on
  the immediate re-run, so the family is host-conditional, not a product or fix regression.
- **Per-gate proof**: 109 own-process runs (the new gate ×20, the ten other recorded allocation-gate
  classes ×5–10, the six non-gate classes this task grew ×3), every run with its padded summary, totals
  assertion and the unchanged test-assembly hash — **every run exit 0, zero gate failures, zero host
  hits** (`gate-stability-verify.txt`). One procedure defect found and fixed: the spec's bare
  `FullyQualifiedName~SweepAllocationGateTests` filter now also matches `UdpAdaptiveSweepAllocationGateTests`
  (13, not 12, so the loop aborts on a false `VACUOUS MATCH`); the namespace-qualified filter is in the
  spec now.
- **Discrimination**: re-proven with an allocation the optimizer cannot sink — `GC.KeepAlive(new byte[64])`
  → adaptive gate `Actual: 88`, pump gate `Actual: 88000`. A bare `_ = new byte[64]` is removed by this JIT
  (2/2 runs passed with it), so the historical `new byte[64]` probe form must not be copied.

## Applied relay receive buffer (the seam's read-back)

`Socks5UdpTransport.AppliedRelayReceiveBufferSize` had never been read; the fact that closes it builds a
transport through the production `Socks5UdpTransportFactory` over a real loopback socket and records what
the OS settled on. On this host (Linux, where the kernel doubles `SO_RCVBUF`) the read-back is exactly
twice the request, so the exact statement stays the *configured* value and the socket fact asserts
`applied >= requested`:

```text
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~Socks5UdpTransportLeaseTests" --logger "console;verbosity=detailed"
# requested=65536  applied=131072   (the shipped 64 KiB default)
# requested=131072 applied=262144   (the udpRelayReceiveBufferKb: 128 override)
```

## Deliberately out of scope (recorded)

- **The shared relay socket with destination-based demultiplexing stays a dead end.** The SOCKS5 UDP
  reply header carries only the peer address, so several client flows to one destination cannot be
  disambiguated; per-flow relay sockets are a NAT requirement. Not revisited.
- **No config key for the one-shot TTL**: the constant plus the session's `min` covers the degenerate
  configuration, and a key would widen the validated surface for a policy with no measured need.
- **The 32 KiB buffer default** stays the documented one-line experiment, not the shipped value.
- **Windows-only measurement rows** (`gc-soak` on a real NIC, `TcpThroughputScenario`) are unchanged.
- **`async-lifetime.md` needs no edit** — checked: the receive loop's rent/release sites and the
  `UdpProxySession` scope contract are untouched by this task, and the spec names the session only in
  its per-owner table. Recorded as checked-and-not-needed rather than left unstated.

## Residual risks (recorded)

- **Sparse flows never promote.** Each of their datagrams starts a fresh session (evidence is per
  lease), so a keepalive in the `(5 s, 30 s]` band is re-established per datagram for the flow's
  whole life; the cost is bounded at one setup per sparse datagram, priced by the churn anchor
  (≈7.6 KB/session). A *slow first reply* is not at risk: the `SawResponse` conjunct keeps an
  unanswered exchange on the long TTL.
- **A fault storm larger than the retire allowance** still allocates fresh receive-window leases
  transiently (self-correcting, bounded by the storm, never a steady state).
- **Kernel drops are invisible.** WinForward has no counter for a response dropped by a full
  `SO_RCVBUF`; the loss/burst/churn anchors are the only instrument that can refute the 64 KiB
  default. The config key (`udpRelayReceiveBufferKb: 128`) restores the old value byte for byte.
- **The F8 attribution TTL leg rides the same tick** and therefore runs 3× as often; its pending
  -entry lifetime tightens with it (see the bounds table).
- **The soak's retention verdict is cadence-coupled and now carries 4.1× headroom.** The ceiling
  (4,200) is derived from the *configured* 30 s retention at the *derived* 5 s cadence, while the
  measured load is the one-shot band (1,014). It therefore proves "the population follows the
  configured retention at the derived cadence" — it catches unbounded accumulation and a reverted
  cadence (whose own ceiling moves back with it, so the pair stays consistent) — but a retention
  regression that stays under 4,200 would pass it. The one-shot class itself is proven by the exact
  boundary facts (`UdpSessionRetentionTests`) and by `udp.sessionRetention`'s control/treatment pair
  (32/32 + 32/32 resident vs 0 + 32/32), not by this soak; the soak's before/after series
  (4,539 → 1,014 at the identical command) is what attributes the end-to-end effect. Recorded rather
  than fixed: tightening the ceiling to the one-shot floor would change what the acceptance names and
  is a design decision.
