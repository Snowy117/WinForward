# Noop decomposition — per-session stage attribution (task 09-21-session-creation-cost, design §2)

Scope: the Noop probe's per-session total (`UdpSessionBenchmarks.PopulateSessionsNoopTransportAsync`,
fake transports) split into named setup stages S0–S5, the teardown delta T, two direct component
probes (C1 association, C2 session construct/start), the fake-transport baseline F, and the
harness's flow-key construction H. All numbers are managed-allocation bytes from BenchmarkDotNet's
`// GC:` line (`GC.GetTotalAllocatedBytes`, process-wide); ns/µs on this dev box are not
decision-grade (same-binary drift up to 2.8× per design §4) and are not used.

## Method and command lines

New benchmark class `SessionSetupDecompositionBenchmarks` (file
`benchmarks/WinForward.Benchmarks/Perf/SessionSetupDecompositionBenchmarks.cs`), each stage
stopping the real `UdpProxyCoordinator` pipeline at one boundary against in-memory fake
transports; every variant asserts its own readiness (slot counts, limiter saturation, flush
counters, receive-entry counters — see the source). Primary invocation, from the repo root, one
process at a time, with `/proc/loadavg` + ISO timestamps captured before/after each run:

```text
# primary batch: 3 runs, current code (decomposition-cur-run{1,2,3}.log/.load)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*SessionSetupDecomposition*' --job short
# independent confirmation batch: 3 runs, same code, S3/S4/S5 only (decomposition-stages345-run{1,2,3})
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*StageS3*' '*StageS4*' '*StageS5*' --job short
# matched-window cross-checks (InvocationCount=6, the window shape of the existing probe)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*PopulateSessionsNoopTransportAsync*' '*StageS5_ReadinessTeardownInsideAsync*' --unrollFactor 1 --invocationCount 6 --iterationCount 3 --warmupCount 3 --launchCount 1
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*StageS4_ReadinessTeardownOutsideAsync*' '*StageS5_ReadinessTeardownInsideAsync*' --unrollFactor 1 --invocationCount 6 --iterationCount 3 --warmupCount 3 --launchCount 1
# recorded baseline probe (baseline-UdpSession-run{1,2,3}.log)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpSession*' --job short
```

Per-run timestamps and `/proc/loadavg` snapshots are in the sibling `.load` files; the re-run
commands and exit codes are also logged in `research/raw/decomposition-cur.progress`.

Parsing convention (matches `research/framework-decomposition.md`): per case, the last `// GC:` line
of its `// Execute:` block is `GC: gen0 gen1 gen2 allocatedBytes totalOps`; the window is BDN's
extra allocation-only workload pass after the measured iterations, with `[IterationCleanup]`
excluded (decompiled `Engine.GetExtraStats`: GC counters are read *before* `IterationCleanupAction`).
`totalOps` is that pass's invocation count. For the stage cases BDN resolves `InvocationCount=1`
(the workloads take 30–85 ms), so `allocatedBytes` covers one full populate invocation of N
sessions. Per-session marginal of a sweep = `(alloc@1000 − alloc@1) / 999`; the run spread quoted
per stage is max−min across the batch. All stage cases emitted exactly 30 cases with no errors;
S0/S1/F/H/C1/C2 are byte-identical across all nine measured runs (deterministic loops), which is
also the strongest available check that those code paths did not change between batches.

## Version split (explicit evidence — no silent mixing)

Two benchmark revisions exist in the raw data:

- **V_A (pre-tightening)** — used by `decomposition-run{1,2,3}` (00:17–00:28). S3/S4 collapsed the
  measured window as soon as the fake transport had seen one flush per session, while background
  setup pipelines could still be returning: `while (factory.Sends < Sessions)`.
- **V_B (current)** — everything from `diag-s34-tighten-run{1,2}` (00:29) onward. The readiness
  waits now also require every setup pipeline to have fully returned (`executor.Pending == 0`) and
  S3/S4/S5 use `CountingSetupExecutor` (exact patch recovered from the previous session's
  transcript: SessionSetupDecompositionBenchmarks.cs hunks at 00:28:58, applied after
  `decomposition-run3` finished at 00:28:23 and before `diag-s34-tighten-run1`).

The V_A→V_B diff touches only S3/S4/S5, `PopulateAndAwaitReadyAsync`, `AwaitAllReadyAsync`, and the
shared `Fixture`/`CountingSetupExecutor` plumbing (exposing the existing pending count; no behavior
change for S0–S2/F/H/C1/C2). Confirmed empirically: their values are identical in every run that
measured them — the three pre-tightening V_A runs and the three current runs (S0 @1000 = 496,680 B;
S1 = 1,323,552; F = 104,032; H = 356,800; C1 = 836,016; C2 = 2,701,864 — a single distinct value
across all six runs; the S3/S4/S5-only batches do not cover these cases).

**Data used in this doc**: the primary batch `decomposition-cur-run{1,2,3}` (current code, full
stage set, 2026-09-22 00:51–00:54) plus the same-code confirmation batch
`decomposition-stages345-run{1,2,3}` for S3/S4/S5. V_A is quoted only in the T reconciliation below
as a window-shape comparison, and is never mixed into the primary tables.

## Per-stage sweep (N = 1 / 100 / 1000; primary batch)

`alloc` columns are the three runs' GC-line bytes (B). `marginal` is
`(alloc@1000 − alloc@1) / 999` in B/session; `spread` is max−min across the three runs.

| Stage | alloc N=1 (r1/r2/r3) | alloc N=100 (r1/r2/r3) | alloc N=1000 (r1/r2/r3) | marginal B/session | spread |
|---|---:|---:|---:|---:|---:|
| S0 coordinator only (no traffic) | 8,280 / 8,280 / 8,280 | 54,456 ×3 | 496,680 ×3 | **488.9** | 0 |
| S1 + admission (slot + enqueue) | 7,176 ×3 | 134,928 ×3 | 1,323,552 ×3 | **1,317.7** | 0 |
| S2 + background setup start | 34,824 / 34,824 / 33,120 | 221,104 / 221,104 / 220,936 | 1,522,200 / 1,352,664 / 1,522,200 | **1,432.9** | 171.4 |
| S3 + pipeline, no payload (probe readiness) | 36,784 / 36,992 / 36,992 | 509,088 / 488,112 / 516,464 | 4,410,592 / 5,021,672 / 4,706,080 | **4,680.5** | 611.5 |
| S4 + full readiness (teardown outside) | 35,072 / 33,368 / 33,368 | 464,880 / 486,568 / 468,224 | 4,265,168 / 4,388,536 / 4,406,160 | **4,323.7** | 142.8 |
| S5 + teardown inside (today's probe shape) | 100,592 ×3 | 736,040 / 753,568 / 748,168 | 6,500,304 / 6,325,240 / 6,331,976 | **6,291.5** | 175.2 |
| F fake transport only | 136 ×3 | 10,432 ×3 | 104,032 ×3 | **104.0** | 0 |
| H flow-key construction | 352 ×3 | 35,200 ×3 | 356,800 ×3 | **356.8** | 0 |
| C1 association claim/release | 2,120 ×3 | 82,800 ×3 | 836,016 ×3 | **834.7** | 0 |
| C2 session construct/start | 5,832 ×3 | 271,048 ×3 | 2,701,864 ×3 | **2,698.7** | 0 |

Confirmation batch (same code, S3/S4/S5 only): S3 = 4,713 / 4,833 / 4,805 (mean 4,784); S4 =
4,211 / 4,188 / 4,392 (mean 4,264); S5 = 6,240 / 6,394 / 6,247 (mean 6,293) B/session. Both
batches agree within their own spreads (S4/S5); S3's spread is the noisiest case of the set.

## Stage deltas and attribution (mean B/session, and % of the S5 total)

| Component | B/session | runs (primary batch) | spread | share of S5 |
|---|---:|---|---:|---:|
| S0: coordinator capacity (pre-seeded slot table + association table + cooldown table) | 488.9 | 489 / 489 / 489 | 0 | 7.8 % |
| admission = S1−S0 (slot claim, setup-queue enqueue + payload copy + charge/lease) | 828.8 | 829 / 829 / 829 | 0 | 13.2 % |
| setup start = S2−S1 (executor enqueue/worker, setup state machine, limiter queue, dial re-stamp) | 115.2 | 171 / 1 / 173 | 171.4 | 1.8 % |
| session tier = S4−S2 (association claim, `UdpProxySession` + context + quiescence scope/CTS, attach, receive-loop start + receive-window rent, flush) | 2,890.8 | 2,745 / 3,040 / 2,887 | 294.9 | 45.9 % |
| S3 variant of the pipeline leg = S3−S2 (same construction, probe-driven readiness) | 3,247.7 | 2,889 / 3,671 / 3,183 | 781.2 | (51.6 %) |
| teardown T = S5−S4 (compensation inside the measured window) | 1,967.9 | 2,172 / 1,871 / 1,860 | 311.3 | 31.3 % |
| **S5 total (teardown inside, today's probe shape)** | **6,291.5** | 6,406 / 6,231 / 6,238 | 175.2 | 100 % |

Named components sum to exactly the S5 total (no residual bucket), so attribution coverage of the
S5-measured per-session cost is **100 %**. Notes on the two legs that are not clean deltas:

- **S2−S1 is quantized** (1 vs ~172 B/session): the stage freezes while eight dials sit on the
  8-wide limiter, so the parked setups' remaining work flips between two states. Read it as
  "≤0.2 KB/session for background setup start".
- **S3 is a variant probe, not an extra multiplicative stage.** S3 waits by re-probing every flow
  with an over-budget datagram until each slot admits directly, so its window pays repeated
  admission attempts; S4 waits on the single accepted flush per session. The result is a small
  inversion (S3−S4 = +356.9 B/session, spread 486): the probe-round overhead of S3 slightly exceeds
  the flush work that S4 adds. Use S4−S2 for the session tier and treat S3 as its bracket.

Component probes (direct measurements against the same product types):

- **C1 = 834.7 B/session**: relay-alias + `FlowKey.Create` construction, `UdpAssociationTable`
  claim → ownership query → remove; the probe builds its own capacity-sized table, so its storage
  overlap with S0's association-table share (~0.5 KB/session) is double-counted against the stage
  table — C1 is an upper bound for the claim tier.
- **C2 = 2,698.7 B/session**: context record + `UdpProxySession` + shutdown CTS + quiescence scope +
  `Start` with the receive-window lease, readiness asserted through per-session receive entries.
  C2 alone covers **93.4 % of the S4−S2 leg**; the remainder is the association claim, the flush,
  and the attach/ready flip.
- **F = 104.0 B/session** (`BenchmarkUdpTransport` + two `IPEndPoint`s) and **H = 356.8 B/session**
  (flow-key construction) are harness elements present identically in the existing probe's window;
  together 460.8 B/session. Product-shaped S5 = 6,291.5 − 460.8 = **5,830.7 B/session** at this
  window shape.

## T (teardown) reconciliation against the documented shape

The class header documents that every stage except S5 defers disposal to `[IterationCleanup]`, and
BDN reads the GC counters before iteration cleanup (verified in the decompiled engine), so S0–S4
windows price setup only while S5 (and the existing probe, whose `await using var coordinator`
disposes inside its workload) prices teardown too. Measured T = S5−S4:

| Shape | T (B/session) | runs | spread |
|---|---:|---|---:|
| Primary batch, `InvocationCount=1` | **1,967.9** | 2,172 / 1,871 / 1,860 | 311.3 |
| Confirmation batch (same code, stages345) | 2,029.7 | 2,028 / 2,206 / 1,855 | 351.0 |
| Pre-tightening V_A (window shape only) | 2,126.8 | 2,258 / 2,031 / 2,091 | 227.2 |
| Matched shape, `InvocationCount=6` (matched-s45-run{1,2}) | **1,458.5** | 1,472.6 / 1,444.3 | 28.3 |

Reconciliation with `matched-window-run{1,2,3}.log`: at equal window shape the probe and S5 agree
to **+0.09 % on average** (per session @1000: probe 5,798.1 / 5,808.1 / 5,800.2; S5 5,821.9 /
5,796.2 / 5,804.1), so the probe's window contains the same teardown work S5 measures. The
matched-shape S4 (4,333.0 / 4,353.2) then splits the probe total as setup 4,343.1 + teardown
1,458.5 = 5,801.6 = the matched S5, closing the loop: teardown is ~25 % of the probe's per-session
total at the probe's own shape, and the larger `InvocationCount=1` T (1,968) is the same
first-pass-in-window effect that lifts the whole S5 single-pass number to 6,291 (see below).

BDN's `// Exceptions:` counter adds one teardown-side observation: the teardown-inside-window cases
report 130 / 328 / 2,128 first-chance exceptions at N = 1/100/1000 (≈2/session marginal,
byte-identical in every batch and in both revisions; S0–S4 windows report none) — the disposal path
runs on exception-shaped control flow. Source attribution of those exceptions is not part of this
benchmark; it is recorded as a follow-up lead for the teardown reduction work.

## Cross-checks against the Noop record (attribution ≥ 90 %)

Recorded Noop-probe marginals from `research/raw/baseline-UdpSession-run{1,2,3}.log`:
**5,737 / 5,737 / 5,727 B/session** (framework-decomposition.md convention; recomputation from the
same GC lines gives 5,739 / 5,737 / 5,728 — ≤2 B apart).

1. **Shape equivalence.** `matched-window-run{1,2,3}` (probe and S5 at `InvocationCount=6`):
   probe = 5,802.1 (spread 10), S5 = 5,807.4 (spread 26) → ratio 1.0041 / 0.9980 / 1.0007. The
   stage S5 is the existing probe's shape.
2. **Coverage of the record.** 6,291.5 / 5,737 = **109.7 %**; at the matched shape the S5 total
   (5,807.4) vs 5,737 = **101.2 %**. Either way the named stages cover ≥ 90 % of the Noop
   per-session total; the single-shape surplus is quantified, not concealed.
3. **Where the surplus comes from.** All stage cases run at BDN `InvocationCount=1`, so their
   allocation pass contains exactly one populate invocation, while the baseline probe accumulates
   5–12 invocations per pass and reports their average. The per-invocation first-pass effect
   (~0.4–0.6 KB/session) is visible directly: S5@1000 per session is 6,231–6,406 single-pass vs
   5,796–5,822 averaged over six passes, while S4@1000 is 4,265–4,406 single-pass (4,333.0 /
   4,353.2 matched) — i.e. the effect sits almost entirely in the teardown phase, not in setup. It
   is not a code-path difference (matched-shape probe ≡ matched-shape S5).
4. **Harness closure.** Subtracting F + H (460.8 B/session, present in both probes) leaves
   5,830.7 B/session of product-shaped cost at the single-pass shape and 5,346.6 at the matched
   shape; the existing ≤1 KB bookkeeping contract is a sub-budget of this, not of the whole probe.

## Interpretation (allocation first)

- The Noop per-session total is **not** dominated by one reducible stage: the session tier
  (construct + attach + receive start) is 45.9 %, teardown 31.3 %, admission 13.2 %, and the
  coordinator's capacity pre-seed 7.8 %. Background setup start is ≤2 %.
- The **teardown quarter** (1.5–2.0 KB/session depending on shape) is the least visible cost in the
  existing contract — no current gate isolates it. It is a plausible first reduction target
  (disposal-path allocations: drain/join cells, tombstone/slot removal, transport dispose), but the
  split inside T is not attributed by this benchmark (only C2-shaped setup probes exist); a
  teardown-side component probe is a follow-up, not a result of this task.
- The **session tier** (2.9 KB/session) is the largest single leg; C2 shows 93 % of it is the
  session object + context + quiescence scope/CTS + receive-loop start, i.e. exactly the async and
  lifetime machinery, matching the design's D7 contract rather than protocol work.
- The **admission** leg (0.83 KB/session) plus S0's capacity pre-seed are the closest match to the
  spec's original bookkeeping list (slot, setup queue, task machinery, MAC copy); the measured
  product-shaped total is ~5.3–5.8 KB/session, i.e. the ≤1 KB ledger drifted well before this task
  — the re-anchor is the decision doc's job (R4), but the stage shares above are the input.
- `unsafe`/direct-memory levers have no visible target in this layer: the stage allocations are
  managed control objects (CTS/scope/state machines/slots), not buffer materialization; the only
  per-session buffer materialization (payload copy in the setup queue) is priced inside S1.

## Gaps / ambiguities

- S2's limiter-freeze quantization (1 vs ~172 B/session) cannot be refined without a seam that
  stops the pipeline between executor dequeue and dial start; merged, as design §2 allows.
- S3's probe-driven readiness adds an unseparated admission-probe overhead (+357 B/session vs S4,
  spread 486); the S4 leg is the clean one and S3 is reported as its bracket.
- T's internal split (scope drain vs slot/tombstone removal vs transport dispose) is not measured;
  only its total is. The single-pass vs matched-shape T difference (1,968 vs 1,459) is a window
  effect whose micro-cause was not isolated further (it reproduces across all batches).
- The 109.7 % vs 101.2 % coverage gap vs the record is a BDN window-shape artifact by construction
  (matched-window evidence); it is not reproducible as a code difference.
