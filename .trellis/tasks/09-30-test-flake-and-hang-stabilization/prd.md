# Stabilize the flaky allocation gate and the hanging test run

Parent: none (test-tree quality, not a performance backlog item). Evidence: the independent check of
`09-29-benchmark-coverage-remaining-findings`
(`.trellis/tasks/archive/2026-09/09-29-benchmark-coverage-remaining-findings/implement.md`, "Independent
check record"), the implement sub-agents' reports, and a code-level review of this plan
(`design.md` §0 records what the review corrected).

## Goal

Make `dotnet test WinForward.slnx -c Release` **deterministically** green by finding and fixing the two
defects that make it flaky — or, where a defect cannot be reproduced inside a bounded effort, by landing a
detector that makes the next occurrence name itself. Neither defect was introduced by the task that found
them.

## Evidence (as corrected by the plan review)

**Defect A — an exact allocation gate that fails roughly one run in six.**

> **Corrected after diagnosis (2026-09-30).** The failing test in this environment is
> `HotPathAllocationGateTests.DispatcherWarmFastPathAllocatesNoManagedBytes`
> (`Expected: 0 / Actual: 1880`), not the reverse gate; the archived **7520 is four such lumps summed**
> inside one window (7520 = 4 × 1880), and the reverse path itself is steady-state allocation-free. The
> cause is not on any product path: a loop whose **only** work is
> `GC.GetAllocatedBytesForCurrentThread()` shows the same single 7,336–7,360 B lump in 12/40 and 14/20
> process runs, and `DOTNET_TieredCompilation=0` removes it (0/20 on both the probe and the gate filter,
> against 17/20 and 12/20 baselines). **Tiered compilation performs a one-time managed allocation on the
> calling thread as hot code is published**, at an unpredictable iteration of whichever window is open.
> The evidence below is kept as recorded, with this correction on top.

- Test: `HotPathAllocationGateTests.ReverseRewriteAndInjectAllocatesNoManagedBytes`
  (`tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs:112`, file unchanged since `1a56eec`).
  It drives a **reverse SYN-ACK** (Ethernet + IP + TCP with `frame[47] = 0x12`), not a mid-flow packet —
  the mid-flow case is the sibling test at `:30`.
- Failure: `Assert.Equal() Failure: Expected: 0, Actual: 7520`.
- Rate: **1 of 25** runs of the 11-test `~HotPathAllocationGateTests` filter, **2 of 13** full-suite runs;
  pooled 3/38 = 7.9 %, Wilson 95 % CI [2.7 %, 20.8 %].
- **The byte count is a hypothesis, not a fact**: the archived record quotes 7520 once; the isolated
  failure's byte count was not recorded and no raw logs survive. Treat "7520 is deterministic" as
  something R1 must establish, not as a given.
- Shape: 8 warm-up calls + 64 measured calls on one thread, exact
  `Assert.Equal(0, allocated)` over `GC.GetAllocatedBytesForCurrentThread()`.

**Defect B — one full-suite run that hung for 41 minutes at ~0 % CPU.**

- Observed **once in roughly 18 full-suite runs** (the run itself plus ~17 later runs); the testhost had
  used 31 s of CPU in 41 minutes. A later run also reported 4 failures, 3 of which were a bug in a
  then-new test and 1 unidentified.
- **Correction to the obvious reading**: 60+ parked `wf-setup-*` threads is *the designed idle state*.
  `SetupExecutor` lazily starts `max(2 × ProcessorCount, 16)` workers — **64 on this 32-CPU host**
  (`SetupExecutor.cs:129,220-239`) — and they park on `_signal.Wait(token)` for the process lifetime
  (`:241-263`); the suite shares one executor through `TestPools.SetupExecutor` (`TestPools.cs:25`). The
  thread count therefore carries **no anomaly signal**, and the earlier assumption that it should return
  to a baseline was wrong.
- **The leading code-visible hypothesis**, to be tested before anything else: `SetupExecutor.TryEnqueue`
  checks `_disposed` (`:186`), enqueues (`:200`) and swallows the `ObjectDisposedException` from
  `_signal.Release()` (`:203-208`), while `Dispose` joins the workers, drains the ring and *then* disposes
  the semaphore (`:293-306`). An enqueue that lands after the drain is picked up by no worker and drained
  by nobody, so its `_completion` never completes — an unbounded await with 0 % CPU and no timeout, which
  is exactly the symptom. Both other pools in the repository close the equivalent window with a
  post-enqueue recheck (`NdisPacketBufferPool.cs:119-141`, `NativeBufferPool.cs:96-121`); `SetupExecutor`
  does not, and `SetupExecutorTests.cs:91-122` does not cover it.
- Mechanism constraints that the plan must respect: xunit 2.9.3 documents `Fact(Timeout)` as supported
  only with parallelization **disabled** (this suite neither disables it nor ships `xunit.runner.json`), so
  the usable timeout is VSTest's `--blame-hang --blame-hang-timeout <t>`, which dumps and terminates the
  testhost and writes `TestResults/<guid>/Sequence_<guid>.xml` whose last entry names the running test.
  The default timeout is one hour, which is why the 41-minute hang produced nothing.

**Repository constraints.**

- **CI runs no tests** (`.github/workflows/` covers formatting, `jb inspectcode` and AOT only), so the
  stability proof is a task-level procedure, not a CI gate. No CI change is in scope.
- `hot-path.md` already records a **negative result** for the tier-up family: on a sibling allocation gate,
  disabling tiered compilation/PGO did **not** remove the allocation burst (`hot-path.md:316-317`). This
  plan must not rediscover that the expensive way.

## Requirements

- **R1 Diagnose defect A to its root cause, in the right order.** First decide whether the delta is a
  **single object or a sum**: 7520 ÷ 40 = 188 exceeds the 72 calls the gate makes, so no per-call
  accumulation reaches it, and 7520 ÷ 64 = 117.5 is not an object size. Then attribute it by bisecting only over the
  sub-steps this gate actually drives — sequence tracking, the reverse endpoint rewrite, the injector call,
  the fake collaborator. The reset-injection failure tail is **out of the bisection**: the gate never
  enters it (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.Injections.cs:316-320` is a failure
  path). Before any of that, spend a
  ten-minute probe to settle whether a first-call/tier-up effect can land on the measured thread at all
  (call a trivial method ~200× on one thread with per-call deltas, with and without
  `DOTNET_TieredCompilation=0`); cite `hot-path.md:316-317` in the result.
- **R2 Fix defect A at its cause**, keeping the gate exact and discriminating, and adopt the gate shape the
  spec already landed (`hot-path.md:369-370,415-431`): assert the operation completed synchronously
  (`IsCompletedSuccessfully`), record `Environment.CurrentManagedThreadId` and assert it is unchanged
  across the measurement, keep the exact `Assert.Equal(0, allocated)`, and keep the thread-independent
  call-count backstop. Warm-up tuning is **not** a fix; if a readiness probe is needed it must require an
  exactly zero delta. A genuine rare-path product allocation is fixed in the product, with the spec
  updated — not by weakening the assertion.
- **R3 Prove defect A's stability with adequate power, and record it.** Use the demonstrated method:
  **≥100 consecutive** green runs of the affected filter and **≥40 consecutive** green full-suite runs
  (20 runs only reaches ~81 % power against the observed 7.9 %). Each run records its xunit summary
  including `Total: 11` for the filter, the git hash, and the exit status — never a discarded stream and
  never `--no-build` against a possibly stale binary. A failure anywhere in the loop stops the proof and
  returns to diagnosis; it is not re-run away. The same procedure run pre-fix (or the recorded 1/25 + 2/13
  with its CI) is the comparison baseline.
- **R4 Attack defect B through its leading hypothesis, then bound it.** First write the
  `SetupExecutor` enqueue-after-drain race test described in the evidence (a test that disposes while an
  enqueue is in flight and asserts the completion still arrives or is refused explicitly); if it fires,
  fix it with the repository's existing post-enqueue-recheck pattern. Then hunt with
  `--blame-hang --blame-hang-timeout 90s --blame-hang-dump-type mini --results-directory /tmp/wf-blame`
  over as many full-suite runs as the wall-clock budget allows (a healthy run is 6–9 s, so ~10 s/run; a
  firing run costs minutes and writes its dump under `/tmp`), and report "not reproduced in N runs" with the 95 %
  upper bound `p < 3/N` rather than an implied fix. Whatever lands must make the next occurrence
  self-identifying (the failing test's name from the sequence file, or an explicit refusal path).
- **R5 No gate weakening.** No exact gate's threshold may be relaxed, no test skipped or deleted, and the
  suite's case count may only grow. Any test that cannot guarantee what it asserts is replaced by a
  stronger deterministic assertion, with the reasoning recorded.
- **R6 Zero product behaviour change unless a defect requires one.** Any product change must be justified
  by R1/R4's evidence, carry a test that fails before it, and be reflected in the relevant
  `.trellis/spec/backend/` document.
- **R7 Land the lesson where it will be read.** The gate-shape rule (synchronous completion + thread
  identity + exact zero + call-count backstop) and the repeat-run procedure gain a named home in
  `.trellis/spec/backend/hot-path.md` (which also carries the stale "678 tests green" figure to correct),
  regardless of whether a product change happens.

## Acceptance Criteria

- [x] R1's diagnosis is recorded with the evidence that establishes it: the 200,000-iteration reproducer
      (one firing, at iteration 0, absorbed by the warm-up, so the reverse path is steady-state clean),
      the eight-variant bisection whose **counter-read-only control** carries the lump, the toggle matrices
      (tiering off 0/20 against 17/20 and 12/20 baselines; OSR-only off 2/20; raised call-count threshold
      2/20; forced collections and a diagnostics-off arm ruled out), and the arithmetic that makes the
      archived 7520 a **sum** of four 1,880 B lumps. The premise survived in a corrected form: a
      first-call/tier-up event *can* land on the calling thread's counter (the probe measured 136 B at call
      0 of a fresh `NoInlining` method), so `design.md` §0.1's mechanism claim was wrong and the fix is the
      host contract, not a warmer gate.
- [x] The reproducer's fate: **deleted** — the cause was reproduced on demand and fixed at the host, so
      keeping a second, slower instrument would have added maintenance without adding evidence. The spec
      now prescribes the technique (per-iteration deltas in a scratch reproducer) for the next occurrence.
- [x] The affected class is green in **100 consecutive filter runs** (rev `0cf25ba`, tree fingerprint
      `5be0c5afba18`, every run `rc=0` with the padded `Failed: 0, Passed: 11, Skipped: 0, Total: 11`),
      and produced **zero** full-suite failures after the fix; the pre-fix baseline (1/25 isolated,
      pooled 3/38 = 7.9 %, Wilson CI [2.7 %, 20.8 %]) is recorded.
- [ ] **Full suite in ≥40 consecutive runs: NOT MET, and not claimed.** 29 plain runs on the final
      configuration had 4 failures (longest green streak 12) and the 47-run hunt added 3 of the same two
      kinds: three are the same host-level lump landing in *other* exact gates
      (`CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` 168 B under
      `--blame-hang`, `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` 7,384 B, and one
      5,216 B pump lump), and one is an unrelated timing race in
      `LayeredCaptureRunnerHealthSignalTests.FailureThresholdForcesARefreshDespiteIdenticalEnumeration`
      (`consecutive` "0" versus the expected "1"). The residual rate (~10–14 %) is the same order as the
      pre-fix suite rate (2/13 = 15 %). This is a **different root cause family** — lumps that survive the
      tiering-off host contract, plus a non-allocation race — and it is carried by the follow-up task
      `09-30-exact-gate-residual-lumps` rather than being declared fixed here.
- [x] Discrimination re-proven: one `Volatile.Write(ref sink, new byte[64])` per measured iteration →
      reverse gate `Actual: 5632` (64 × 88 B), dispatcher gate `Actual: 22528` (256 × 88 B); restored →
      green. The experiment also exposed a trap now in the spec: `Assert.Equal` inside a window allocates,
      `Assert.True(condition, message)` does not.
- [x] Defect B: the race **fired naturally** (no seam needed) — `SetupExecutorTests.SetupExecutorDisposeRacingTheFirstEnqueueLeavesNoItemUnsettled`,
      512 attempts. Fail-before, 3/3 runs: the host **aborted** on an unhandled `ObjectDisposedException`
      in `WorkerLoop` inside the first 100 attempts, and with only the worker guard **1,997 of 1,998 /
      1,998 of 1,998 / 1,998 of 2,000** accepted items were stranded with never-settling completions.
      Fixed with the pool family's post-enqueue recheck plus the worker guard; after the fix the test is
      green and a 2,000-attempt run reports `stranded=0`.
- [x] The hunt: 47 full-suite runs under `--blame-hang --blame-hang-timeout 90s --blame-hang-dump-type mini
      --results-directory /tmp/wf-blame` → **0 hangs, 0 sequence files; not reproduced, 95 % upper bound
      p < 3/47 = 6.4 %**; the mechanism was demonstrated once by injecting a deliberate hang (30 s), whose
      `Sequence_*.xml` named the scratch test, then restored.
- [x] No exact gate relaxed (the injected-allocation check proves it), no `[Fact]`/`[Theory]` removed
      (`git diff -U0 tests/` has no such deletion) and the suite count grew by one (979 → 980).
- [x] Full gates green: Release build zero-warning, suite 980 + 18 green (`--blame-hang`), `dotnet format
      --verify-no-changes` exit 0 empty, and `jb inspectcode` zero `<Issue>`. The solution-wide run reported
      **exactly one** finding — `NotAccessedVariable` on the vestigial `probeKeepGoing` in
      `NdisCapturePumpTests.cs:314` — which was fixed by asserting the probe's continuation (strengthening
      the check rather than deleting the variable); the project-scoped re-run then reported **0 issues and
      0 `CSharpErrors`**, and no other file changed after the solution-wide run, so the gate holds.
- [x] `hot-path.md` carries the gate-shape rule, the repeat-run procedure (with commands and the power
      table), the tiering host contract and the cost of it (~7 s → ~10.5 s per suite run), the
      reconciliation of its older contradictory tiering note, the `Assert.Equal`-in-a-window trap, the
      corrected 998-test figure, the ODE half's missing-regression-test gap, and the two follow-up
      candidates.

## Out of Scope

- Performance work of any kind (the F2–F8 pipeline is a separate task sequence).
- Rewriting the test harness, replacing xUnit, or reclassifying gates.
- CI changes (CI runs no tests today; making it run them is a separate decision).
- Windows-only behaviour and the driver's own IOCTLs.

## Notes

- The two defects are independent and land as separate commits; A's stability proof runs **after** B's
  test-tree changes, because B changes suite-wide load and parallelism.
- Useful probes: for A, a loop over the measured body recording per-iteration deltas (excluding no
  iteration — the exclusions are what hid the predicted event in the first draft of this plan); for B, the
  race test first, then the blame-hang hunt.
- Budget guide: the tier-up probe ~10 min, the A reproducer ~30 min, the race test ~30 min, the hunt
  bounded by wall clock (~1 min per run), the two stability loops ~45 min.
