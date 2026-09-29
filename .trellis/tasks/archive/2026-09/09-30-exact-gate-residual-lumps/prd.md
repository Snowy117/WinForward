# Diagnose the residual exact-gate host lumps and the health-signal race

Parent: none. Predecessor: `09-30-test-flake-and-hang-stabilization` (archived), which removed the tiering
family from the exact gates and fixed the `SetupExecutor` enqueue/dispose race. This task is **part 2 of the
operator's "fix all suspicious tests" item** — until it lands, no exact allocation gate can be proven stable
at suite level.

## Goal

Make the suite-level stability proof achievable: diagnose the lump that survives the tiering-off host
contract and lands in exact allocation gates, attribute it (or re-scope it to a family-level cause with the
product excluded), fix the health-signal race, and land **a proof criterion the evidence supports** — either
**≥40 consecutive green full-suite runs** (if the cause is fixed) or the operational per-gate procedure of
R4 with the measured residual rates and their confidence intervals (if it is an accepted host property).
The goal is a proof that means something, not a green streak that a known host event can break.

## Evidence

**Residual lump family (tiering OFF — the arm that matters here).**

| Failure | Window | Observed |
|---|---|---:|
| `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` | 1,000 idle `RunIterationForTests` calls | 5,216 B (`/tmp/wf-proof-suite-failure-6.txt`, after `stabilized`), 168 B (blame-hang arm) |
| `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` | one `RemoveExpired` over 4,096 seeded flows | 7,384 B (`/tmp/wf-proof-suite-failure-13.txt`) |
| hunt-only | — | 7,448 B |

**Sizes from other arms, which must not be quoted as this task's evidence**: 1,880 B is the documented
*tiering* lump (`WinForward.Core.Tests.csproj:11`, `hot-path.md:858-859`) and 7,336 / 7,360 / 8,008 B were
measured **with tiering on** (`hot-path.md:855-859`) — the archived `7520 = 4 × 1880` reading belongs to
that family. The residual's **multiplicity per process and per window is unmeasured** ("once per process"
is the tiering control's signature, never established for the residual), and it is exactly what any
tolerance decision would rest on: measuring it is requirement R1's first deliverable.

**Families already excluded analytically** (do not spend budget on them): first-execution JIT — both windows
drive a fixed branch set (`script = [static (_, _) => 0]` reaches only the `readCount == 0` path,
`NdisCapture.cs:286-297`; the sweep expires all 4,096 keys with `idleTimeout = TimeSpan.Zero`,
`FlowTable.cs:90-118`) and there is no ring or `TryDequeue` on the pump path at all (it preallocates
`_batchBuffers`, `NdisCapture.cs:143-144`); a fresh thread's first counter read — it happens inside the
preflight (`CapturePumpReadCallTests.cs:100`, `SweepAllocationGateTests.cs:41`,
`NdisCapturePumpTests.cs:319`) and its cost is absorbed into the value it returns; GC-triggered pool refill —
managed lists/arrays/dictionaries do not refill on GC, the pump preallocates, and forced Gen0/1/2 plus 668
sibling-triggered collections produced 0 jumps in the console control (`hot-path.md:882-883`).

**Health-signal race (product, not a racing test).**

The test asserts on the *log record's* `consecutive` field, which the product built at log time
(`LayeredCaptureRunner.cs:248-259` → `RecordingLogger.cs:50-53`), and it already waits past the refresh
before reading it. The only writer of `0` is `InterceptionHealthMonitor.NoteRefreshCompleted()`
(`:156-164`, called from `LayeredCaptureRunner.cs:230`) racing that field build — possible because
`_demandGate.Signal()` (`:245`) publishes the request **before** the record is built (`:251`). There is **no
refresh-completion signal to wait on**: `Generation(i).Started`/`InstalledScopes` complete earlier.
Observed failures: one in the 29-run proof (`/tmp/wf-proof-suite-failure-10.txt`) and two more across the
hunt (1+2 total).

## Requirements

- **R1 Attribute the residual lumps, with an instrument that can name an allocator.** Per-iteration deltas,
  `GC.CollectionCount` and thread id establish *when*, never *what*. Requirements: (a) measure the
  **multiplicity** (lumps per process and per window) before any tolerance decision; (b) run the window body
  on three thread contexts — the xunit thread, a dedicated `Thread`, and a pool thread **other than the
  test's** (the pool reuses threads, so this arm differs by thread history, not freshness) — because the
  observable is a per-thread counter event; (c) attempt type/stack attribution with a profiler installed for
  the purpose (the host has the SDK but no `dotnet-trace`/`gcdump`), time-boxed. If attribution cannot name
  an allocator inside the box, R1 is **re-scoped to a family-level cause with the product explicitly
  excluded** (the counter-read-only control is what excludes it), and that re-scope is recorded rather than
  smuggled past this requirement.
- **R2 Change what the diagnosis justifies, without weakening any gate.** Three dispositions:
  (a) an attributable cause with a product owner → fix the product under product-change discipline;
  (b) an attributable host/runtime event with no product owner → **no gate change**: replace the *proof*
  with a per-gate-process run (`dotnet test --filter` per gate) and record the suite-level residual rate
  with its CI as an accepted host property;
  (c) unattributable after the time box → (b), plus an explicit **sensitivity statement** in the spec naming
  which allocation rates the per-process gate would no longer catch.
  A tolerant gate shape is acceptable **only** if it survives the honest injected-allocation check: one
  allocation inside **one** window must still fail it. That test refutes min-of-K and "at most one of K
  nonzero" (both tolerate exactly a single-window allocation), which is why they are rejected in advance;
  relaxing a threshold to zero-nonzero, skipping a test or deleting an assertion is out of the question.
- **R3 Fix the health-signal race at its cause.** The cause is the product's ordering:
  `_demandGate.Signal()` precedes the state capture that builds the log record, so the monitor's
  `NoteRefreshCompleted()` can reset the streak in between. Fix it by capturing the streak and window
  snapshot under the monitor's gate and handing them to the trigger, or by moving `Signal()` after the
  record is built; add the bounded wait for the *post*-state assertions, which are racy in the opposite
  direction. Record the invariant in `.trellis/spec/backend/windows-ndisapi.md` (the spec that owns
  `NoteRefreshCompleted`/`runner.forcedRefresh`). **No deterministic fail-before test exists without a seam**
  — the pre-fix capture plus the ordering argument is the evidence, and a test-only seam is allowed if it
  makes the interleaving deterministic. An isolated "run it 200×" loop proves nothing about a
  thread-pool-starvation race and is not a deliverable.
- **R4 Prove it, operationally.** Under a fixed cause: **≥40 consecutive green full-suite runs**. Under
  disposition (b)/(c): **N per-gate process runs per exact gate** (one gate per process), each recorded with
  the padded `Failed/Passed/Skipped/Total` line, the totals assertion (981 + 18), the git hash, the tree
  fingerprint and the exit status, reporting the **measured per-gate residual rate** and the suite-level
  rate with its CI — a failure is counted as the accepted host event **only** when its nonzero pattern
  matches the recorded signature; any other pattern stops the work as an incomplete diagnosis. Per-gate
  isolation changes attribution, not incidence, so the run count N is chosen for the rate's precision
  (the predecessor's power table: 20 runs = 80.7 %), not for the hope of a post-event window. No
  `--no-build`, no discarded output, and the break-on-failure rule is qualified by the disposition instead
  of treating every nonzero as a diagnosis failure. Full gates (Release build, format, `jb inspectcode`) run
  **before** the proof and the tree is frozen for it.
- **R5 No gate weakening, no test skipped or deleted**, the suite count only grows, and any spec statement
  the work invalidates (including the predecessor's follow-up candidates in `hot-path.md`) is corrected.
- **R6 Record the decision in the specs** so the next gate author inherits it: `hot-path.md` gets the
  outcome, the accepted residual rate, any tolerance **and its sensitivity floor**, the reconciliation of
  the `:940` "forbidden" row and the `:878-881` per-window-control follow-up, and the negative results not
  to re-test; `windows-ndisapi.md` gets the health-signal invariant. The scratch reproducer lives under
  `/tmp`, runs **never concurrently with a suite proof** (a co-resident reproducer absorbs the
  once-per-process event it hunts), and is deleted when the cause is fixed or recorded.

## Acceptance Criteria

- [x] The residual lump is **re-scoped**: the profiler was installed with its observation guarantee
      pre-registered, proved able to name a continuously allocating type, and proved **blind** to the
      one-shot class under diagnosis (0 samples for a single 8 KB allocation whose 8,280 B the counter
      read). Tiering, cold JIT, first-counter-read and GC-refill are excluded with evidence; the
      counter-read-only control **of the residual** could not be run, so the product is explicitly **not**
      claimed excluded. Multiplicity: 0/27 processes (≤12.5 %) and 0/249,881 windows (≤1.6e-5).
- [x] Disposition **(c)** executed and stated: no product fix for the lump, no gate change; the
      per-gate-process proof (procedure in `hot-path.md`) with the accepted rates and CIs, plus the
      sensitivity floor (≈1/50,000 per window no longer reliably caught).
- [x] The health-signal race is fixed at its ordering cause (see the criterion above), the invariant is in
      `windows-ndisapi.md`, and the recorded pre-fix capture stands in for the fail-before test that cannot
      exist without a seam.
- [x] **Per-gate criterion met** (disposition (c)): 80/80 green process runs (20 × 4 gates), 0
      signature-matched host hits, 0 unexplained failures, class totals asserted every run.
- [x] **Suite-level rate measured and recorded** rather than claimed: 13 runs → 2 failures (~15 %), one a
      **signature match** (`SweepAllocationGateTests`, `Actual: 7448`) and therefore the accepted host
      event, one **not** (`UdpAssociationHeadTests.TheDefaultHeadKeepsTheAcceptanceLoadShared`,
      `Expected: 282, Actual: 281`) and therefore carried by the follow-up task
      `09-30-udp-association-head-count-flake`.
- [x] The lump is **not** claimed fixed and the product is **not** claimed excluded by a control of the
      residual (the counter-read-only control of the residual cannot be run without masking the gates) —
      both stated in the spec and in this record.
- [x] The health-signal race is fixed at its ordering cause with the trigger-time snapshot, the invariant is
      in `windows-ndisapi.md`, and the bounded post-state assertion plus the recorded pre-fix capture stand
      in for the fail-before test that cannot exist without a seam.
- **(superseded by the four criteria above)** The proof criterion under the chosen disposition: **≥40 consecutive green full-suite runs**, or
      R4's operational per-gate procedure (N process runs per exact gate, each recorded with the padded
      summary, the totals assertion 981 + 18, the git hash and the tree fingerprint) with the measured
      per-gate residual rate — a failure counted as the accepted host event only when the **checkable
      signature predicates** hold (a recorded victim gate, a delta equal to one of 168 / 5,216 / 7,384 /
      7,448 B, past the gate's own `Assert.True(stabilized)` preflight, and the injected-allocation check
      still failing when re-run) — plus the suite-level rate with its CI and the sensitivity statement. This
      criterion claims **no rate threshold**: the rate is measured and recorded as the accepted host
      property, so the finished record is not a stability guarantee.
      Full gates run before the proof on a frozen tree.
- [x] No gate relaxed (the four gate files are byte-unchanged), no test skipped or deleted, and the suite
      count grew by one (980 → 981).
- [x] Full gates green: Release build zero-warning, suite 981 + 18 with 0 failed, `dotnet format
      --verify-no-changes` exit 0 empty, and `jb inspectcode`'s two genuine findings in the new test code
      fixed (pattern merged; the self-reference suppressed with its reason) → test project 0 issues.
- [x] `hot-path.md` carries the outcome and the negative results (census, suite-conditional finding, tool
      blindness with its positive control, the priced-and-rejected per-window control, accepted rates with
      CIs, the sensitivity floor, the do-not-re-test list, the per-gate procedure); `windows-ndisapi.md`
      carries the trigger-snapshot invariant.

## Out of Scope

- The F2–F8 performance pipeline (this task must finish first; the operator's order is explicit).
- Windows-only behaviour, the driver's IOCTLs, and CI changes.

## Notes

- The predecessor's artifacts are the starting evidence: `tests/` proof logs under `/tmp/wf-proof-*.log` and
  `/tmp/wf-proof-suite-failure-*.txt`, its `hot-path.md` section, and its task record at
  `.trellis/tasks/archive/2026-09/09-30-test-flake-and-hang-stabilization/`.
- **Where the reproducer lives**: `NdisCapturePump.RunIterationForTests` is `internal` with
  `InternalsVisibleTo` only to `WinForward.Core.Tests` and `WinForward.Benchmarks` (`NdisApiAbi.cs:6-7`), so a
  `/tmp` project cannot drive the pump or sweep window bodies. The window reproducers are **temporary files
  inside an IVT'd assembly**, removed before the frozen proof; the `/tmp` host can carry only the public
  counter-read control.
- Two candidate directions worth pricing before choosing: (i) pair every measured window with an
  identically shaped control window that performs the same counter reads but no product call, and assert on
  the **difference** — priced and rejected in design §2, because the random lump lands in exactly one of the
  two windows and the difference therefore cannot separate it from a product allocation; (ii) accept the
  lumps as an accepted host property and make the proof operational (R4): per-gate process runs with the
  measured per-gate rate and the signature-matched failure classification. Note that per-gate isolation
  changes **attribution, not incidence** — it does not place the window after the event, and the
  plan's own capture shows a lump landing after the probe reported `stabilized`.
- Neither direction may be adopted without the injected-allocation check: a shape that cannot fail is not a
  gate.
