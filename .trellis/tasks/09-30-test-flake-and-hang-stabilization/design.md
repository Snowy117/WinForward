# Design — stabilize the flaky allocation gate and the hanging run

Diagnosis first, fix second, proof last. Both defects are rare, so each one must **fire on demand** (or be
ruled out by a cheap probe) before anything is changed: a fix that cannot be shown to address a reproduced
cause is not a fix, and one that merely lowers the failure rate is worse than the flake (R5).

## 0. What the plan review corrected

Three load-bearing assumptions in the first draft were wrong, and the design is built around not repeating
them:

1. **The tier-up premise is unproven at the mechanism level.** Tier-1 compilation runs on the runtime's
   tiering worker, so its allocations are not expected to appear in a *per-thread* counter
   (`GC.GetAllocatedBytesForCurrentThread`, `hot-path.md:302-304`) — and the repository already has a
   negative result for this family: disabling tiered compilation/PGO did not remove the allocation burst
   on a sibling gate (`hot-path.md:316-317`). The first draft also excluded the first 100 iterations of
   its reproducer, which is exactly where a call-count effect would fire. A ten-minute probe decides this
   before the diagnosis budget is spent.
2. **7520 bytes is not a plausible single product allocation on this path.** The only product-side managed
   allocation the measured body can reach is `new NdisPacketBuffer` (~40 B on a shared-pool miss,
   `NdisPacketBufferPool.cs:79`); the table lookup, sequence tracking, endpoint rewrite,
   `IPAddressValue.From` (stack-only), `ValueTask.FromResult` and the null logger are 0 B, and the
   reset-injection tail is never entered by this gate. `7520 = 64 × 117.5` also rules out a uniform
   per-iteration allocation. So the first question is **single object or sum**, and allocation tracing is
   useless here (its ~100 KB sampling budget will not catch one 7520-byte object).
3. **The hang's "anomaly" was a false invariant.** 60+ parked `wf-setup-*` threads is the designed idle
   state: `SetupExecutor` starts `max(2 × ProcessorCount, 16)` = 64 workers on this 32-CPU host
   (`SetupExecutor.cs:129,220-239`) that park for the process lifetime (`:241-263`), shared suite-wide via
   `TestPools.SetupExecutor` (`TestPools.cs:25`). The count cannot move and proves nothing. xunit 2.9.3's
   `Fact(Timeout)` also requires parallelization to be disabled, so the usable per-test timeout is VSTest's
   `--blame-hang --blame-hang-timeout` (default one hour — which is why the 41-minute hang produced no
   dump).

## 1. Defect A — the 7520-byte allocation

### 1.1 Probe the premise (10 minutes, before anything else)

A trivial loop: one method called ~200 times on one thread, per-call
`GC.GetAllocatedBytesForCurrentThread()` deltas, run with and without `DOTNET_TieredCompilation=0` and
`DOTNET_TieredPGO=0`. Outcome either confirms that a first-call/tier-up effect can land on the calling
thread's counter or kills the premise outright (the expected outcome, given `hot-path.md:316-317`). The
result is recorded in the task record either way; if it is killed, the reproducer goes straight to steady
state with **no excluded iterations**.

### 1.2 Single object or sum

Before bisecting, establish the arithmetic: 7520 ÷ 40 = 188 (not 64), 7520 ÷ 64 = 117.5, 7520 ÷ 8 = 940 —
none of which matches a per-iteration object. So either one 7520-byte object is created once, or several
smaller ones sum. The reproducer answers this by reporting, per firing iteration, the delta and the number
of firings: one firing with the whole delta means a single object; repeated firings of equal small deltas
mean a sum. Only then does bisection mean anything.

### 1.3 Bisect only what the gate drives

The reproducer loops the exact measured body with per-iteration deltas (no warm-up exclusion, no
threshold), then bisects in this order:

1. sequence tracking alone, 2. the reverse endpoint rewrite alone, 3. the injector call alone,
4. the fake collaborator alone, 5. the whole body.

The reset-injection path is **not** in the list: it is a failure tail this gate never enters. Each step's
reproducer records the first firing iteration and the delta, so the allocation lands on a named sub-step.
Environment toggles (`DOTNET_TieredCompilation`, `DOTNET_TieredPGO`, a forced `GC.Collect()` before the
window) are used to separate one-time from steady-state causes.

### 1.4 Fix and harden the gate

Two outcomes, two fixes:

- **Product rare-path allocation** (a first-use cache, a pooled rent that is not returned before the
  snapshot, an asynchronous completion that only happens when the pool is warm): fix the product, add the
  regression test, update `hot-path.md`.
- **Harness allocation** (the test's own fakes, a rented buffer, a list): fix the test and explain the
  missing precondition.

Either way the gate adopts the shape the spec already landed (`hot-path.md:369-370,415-431`), which the
reverse gate currently lacks: assert the operation completed synchronously (`IsCompletedSuccessfully`),
record `Environment.CurrentManagedThreadId` and assert it is unchanged, keep the exact
`Assert.Equal(0, allocated)`, and keep the thread-independent call-count backstop
(`Assert.Equal(8 + count, injector.Calls)`). Warm-up tuning is explicitly rejected as a fix; any readiness
probe must require an exactly zero delta.

## 2. Defect B — the 41-minute hang

### 2.1 The leading hypothesis, tested first

`SetupExecutor.TryEnqueue` checks `_disposed` (`:186`), enqueues (`:200`) and swallows the
`ObjectDisposedException` from `_signal.Release()` (`:203-208`); `Dispose` joins the workers, drains the
ring and *then* disposes the semaphore (`:293-306`). An enqueue that lands after the drain is picked up by
no worker and drained by nobody, so its `_completion` never completes: an unbounded await with 0 % CPU and
no timeout — the observed symptom, and the only window of its kind found in the runtime's pool/executor
family (both
pools close the equivalent window with a post-enqueue recheck: `NdisPacketBufferPool.cs:119-141`,
`NativeBufferPool.cs:96-121`).

The test writes the race directly: `Dispose()` on a background thread racing an enqueue, then require either a
completed completion or an explicit refusal. If it fires, the fix is the repository's existing
post-enqueue-recheck pattern, with that test as the regression test.

### 2.2 Hunt with the right tool

`--blame-hang --blame-hang-timeout 90s --blame-hang-dump-type full` over as many full-suite runs as the
wall clock allows (a healthy run is 6–9 s). Blame writes `TestResults/<guid>/Sequence_<guid>.xml`, whose
last entry names the running test, so a recurrence is self-identifying. If the hunt does not reproduce the
hang, the report states **"not reproduced in N runs, 95 % upper bound p < 3/N"** rather than implying a
fix — and the landed detection is the race fix (if it fired) plus the short timeout, not a claim.

### 2.3 What must not be built

- No assertion on the setup thread count (it is constant by design).
- No per-test `Fact(Timeout)` (undefined without disabled parallelization).
- No dump-per-run on healthy runs (cost), and no watchdog that can hang the suite itself.

## 3. Statistics of the proof

Recorded rate: 3/38 pooled = 7.9 %, Wilson 95 % CI [2.7 %, 20.8 %].

| Proof | Power against 7.9 % | Power against 4 % | Decision |
|---|---:|---:|---|
| 100 filter runs green | 99.97 % | 98.31 % | keep |
| 20 suite runs green | 80.72 % | 55.8 % | too weak |
| **40 suite runs green** | **96.28 %** | 80.46 % | required |

The pre-fix baseline is the recorded 1/25 + 2/13 with its CI (a fresh ≥25/≥10 baseline cannot establish a
4 % rate and is not repeated). Every run in both loops records its xunit summary — the whitespace-padded `Failed/Passed/Skipped/Total`
line, with `Total: 11` for the class filter that reproduces the recorded baseline — plus the git hash and
**the process exit status** (an aborted testhost can print `Failed: 0` and still exit non-zero); **no `--no-build`** (a stale binary would green
vacuously) and **no discarded output**. A single failure stops the loop and returns to diagnosis.

## 4. Ordering

B changes suite-wide load and parallelism (timeouts, hunt configuration), so it lands **before** A's
stability proof; if any test-tree change lands after that proof, the proof is re-run. Commits:
A's fix, B's fix/detector, the spec update, then the task record, archive and journal.

## 5. Risks

| Risk | Mitigation |
|---|---|
| The flake stops reproducing and a fix is invented | R1 requires the reproduced cause or an explicit arithmetic/probe-based ruling; an unreproduced change is recorded as such, never as a fix |
| The reproducer's own allocations pollute the measurement | it asserts its own steady state first and reports the excluded-iteration policy explicitly (none) |
| A fix hides a regression | the gate keeps an exact zero assertion plus the thread-identity and call-count backstops; warm-up tuning is rejected |
| The hunt burns wall clock without reproducing | bounded by the wall-clock budget with an explicit upper bound; the race test is the primary instrument |
| Detectors add flakiness of their own | the only landed detector is the short blame-hang timeout plus (if it fires) the race fix; no new timing-sensitive assertions in the suite |

## 6. Rollout and the lesson's home

Two independent commits (A, B), one spec commit, then the task record, archive and journal.
`.trellis/spec/backend/hot-path.md` gains the gate-shape rule (synchronous completion, thread identity,
exact zero, call-count backstop), the repeat-run procedure as a named section, and a correction of its
stale "678 tests green" figure — regardless of whether any product code changes. The PRD's "no CI change"
note is explicit: CI runs formatting, `jb inspectcode` and AOT only, so the stability proof lives with the
spec, not in CI.
