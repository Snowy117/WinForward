# Diagnose the residual exact-gate host lumps and the health-signal race

Parent: none. Predecessor: `09-30-test-flake-and-hang-stabilization` (archived), which removed the tiering
family from the exact gates and fixed the `SetupExecutor` enqueue/dispose race. This task is **part 2 of the
operator's "fix all suspicious tests" item** — until it lands, no exact allocation gate can be proven stable
at suite level.

## Goal

Make the suite-level stability proof achievable: diagnose the once-per-process lump that survives the
tiering-off host contract and lands in exact allocation gates, decide whether the gate shape or the host is
what needs to change, and fix the health-signal race. Success is **≥40 consecutive green full-suite runs**
recorded with per-run summaries, git hash and exit status — the criterion the predecessor could not meet.

## Evidence

**Residual lump family (three failures in 29 plain full-suite runs + the 47-run hunt).**

| Failure | Observed | Where |
|---|---:|---|
| `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes` | 168 B (under `--blame-hang`), 5,216 B | pump idle window |
| `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` | 7,384 B | sweep window |
| observed lump sizes in the predecessor's probes | 168 / 1,880 / 5,216 / 7,336 / 7,360 / 7,384 / 7,448 / 8,008 B | once per process, random iteration |

- The predecessor's decisive control — a loop whose **only** work is
  `GC.GetAllocatedBytesForCurrentThread()` — carried the lump under default tiering (12/40, 14/20 runs) and
  was clean with `DOTNET_TieredCompilation=0` (0/20). The gates now run with tiering disabled, yet the
  lumps still appear, so this is **not** the same cause and must not be assumed to be.
- The predecessor's bounded exactly-zero probe preflight did **not** protect the window (a 5,216 B lump
  landed after eight clean probe batches), and `DOTNET_gcConcurrent=0` looked promising (0/20) then failed
  to replicate (1 failure in the next 10 runs) and was reverted — recorded in `hot-path.md` as a negative
  result rather than a fix.
- Leading hypothesis to test first: with tiering off, JIT still compiles a code path the first time it runs
  — so a path reached for the first time *inside* a measured window (a rare branch of the gate, a library
  helper, the assertion machinery) allocates on the measuring thread. The measured body is warmed; the
  surrounding machinery may not be.

**Health-signal race.**

- `LayeredCaptureRunnerHealthSignalTests.FailureThresholdForcesARefreshDespiteIdenticalEnumeration` failed
  with `consecutive` "0" against the expected "1" (twice in the predecessor's runs). It is not an allocation
  gate: it looks like a forced-refresh versus success-hook ordering race.

## Requirements

- **R1 Diagnose the residual lumps to a named cause**, with the same discipline the predecessor used:
  reproduce on demand (the predecessor's per-iteration-delta technique, in a scratch reproducer), then
  attribute — window body, surrounding machinery, runtime/host event, or accounting. A cause must be
  established by evidence that distinguishes it from the tiering family already ruled out; "host noise" is
  not a diagnosis.
- **R2 Change what the diagnosis justifies, without weakening any gate.** Two outcomes are acceptable:
  (a) a fix that removes the cause (e.g. warming a code path, or a host/build setting with replicated
  evidence — the predecessor's reverted `gcConcurrent` attempt shows what "replicated" means: two
  independent arms, not one favourable run); or (b) a **gate shape that separates a host lump from a
  product allocation** and is proven not to hide a real regression — any such shape must be shown to still
  fail, with the exact bytes, when an allocation is injected into the measured region. Relaxing a threshold
  to zero-nonzero, skipping a test, or deleting an assertion is out of the question.
- **R3 Fix the health-signal race at its cause**, with a regression test that fails before the fix, or — if
  the race cannot be made deterministic — a bounded assertion that makes the ordering explicit and a
  recorded explanation of why the previous expectation could not hold under load.
- **R4 Prove the suite**: **≥40 consecutive green full-suite runs**, each recording the padded
  `Failed/Passed/Skipped/Total` line, the git hash and the process exit status (no `--no-build`, no
  discarded output, break on any failure). The predecessor's baseline for comparison: 29 runs, longest green
  streak 12, ~10–14 % failure rate after the tiering fix.
- **R5 No gate weakening, no test skipped or deleted**, the suite count only grows, and any spec statement
  the work invalidates (including the predecessor's follow-up candidates in `hot-path.md`) is corrected.
- **R6 Record the gate-shape decision in `.trellis/spec/backend/hot-path.md`** so the next gate author
  inherits it, including the negative results (which knob did not replicate, and on what evidence).

## Acceptance Criteria

- [ ] The residual lump has a named cause with discriminating evidence, and the tiering family is
      explicitly excluded as its explanation.
- [ ] Either the cause is fixed, or the gate shape that tolerates it is landed **and** proven to still fail
      on an injected allocation with the exact byte count.
- [ ] The health-signal race is fixed with a regression test (or explicitly bounded with the recorded
      reason it cannot be deterministic).
- [ ] **≥40 consecutive green full-suite runs** recorded with summaries, git hash and exit status.
- [ ] No gate relaxed, no test skipped or deleted, the suite count only grew.
- [ ] Full gates green: Release build zero-warning, `dotnet format --verify-no-changes` empty,
      `jb inspectcode` zero `<Issue>`.
- [ ] `hot-path.md` carries the outcome and the negative results.

## Out of Scope

- The F2–F8 performance pipeline (this task must finish first; the operator's order is explicit).
- Windows-only behaviour, the driver's IOCTLs, and CI changes.

## Notes

- The predecessor's artifacts are the starting evidence: `tests/` proof logs under `/tmp/wf-proof-*.log` and
  `/tmp/wf-proof-suite-failure-*.txt`, its `hot-path.md` section, and its task record at
  `.trellis/tasks/archive/2026-09/09-30-test-flake-and-hang-stabilization/`.
- Two candidate directions worth pricing before choosing: (i) pair every measured window with an
  identically shaped control window that performs the same counter reads but no product call, and assert on
  the **difference** (a product allocation inflates only the measured side; whether a random host lump can
  be told apart this way is exactly what R2 must prove or refute); (ii) accept the lumps as a host property
  and move the suite-level proof to a per-gate process (`dotnet test --filter` per gate) where a fresh
  process has one lump at most and the measured window can be placed after it.
- Neither direction may be adopted without the injected-allocation check: a shape that cannot fail is not a
  gate.
