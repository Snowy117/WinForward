# PRD — Recover generation startup from stale adapter handles (native 87)

## Goal

Eliminate the intermittent fatal exit:

```
[error] Runtime failure: Unable to read NDISAPI adapter mode (native error 87, 0x00000057).
```

An NDISRD bound-adapter-list rebuild that lands **between the runner's enumeration and the new
generation's mode snapshot** invalidates the just-acquired "fresh" handles before the pumps ever
start. `GetAdapterMode` then fails with `ERROR_INVALID_PARAMETER` (87), the `Win32Exception`
escapes the generation fail-closed, and the process exits. Today three 87 surfaces are handled —
pump reads (degrade → refresh, task 09-07 R3), teardown restores (swallowed best-effort), and
enum-then-rebuild storms (R4's "loop is the reconciler") — but the **generation install/startup
phase** has no recovery: the fault kills the run before the loop can reconcile. This task closes
that gap: the same event becomes a storm-guarded, bounded refresh retry instead of a process exit.

Production shape of the race: enumerate → (storm-guard wait, old-generation teardown incl. pump
drain) → install generation → snapshot modes. Every millisecond of that window is exposure; the
window is largest on refresh rebuilds (old-generation teardown is in the middle of it) and exists
at gen-0 startup too.

## Requirements

- R1 (classification): a generation fault is classified as a *recoverable startup stale-handle
  fault* iff it is a `Win32Exception` with `NativeErrorCode == 87` AND the generation had not yet
  started its pump run (a phase the generation must expose). Every other escape — non-87 startup
  faults, 87 after pumps started, anything else — keeps today's fail-closed propagation.
- R2 (recovery semantics): a classified fault is absorbed; the runner enters the existing refresh
  pipeline — storm-guard interval, fresh enumeration, scope diff, new generation install — and the
  recovery demand is *forced*: an empty diff must still install a replacement generation (the
  current one is dead; the empty-diff no-op skip would strand the run with zero pumps). The forced
  rebuild is logged honestly, distinguishable from a normal no-op skip.
- R3 (bounded recovery): consecutive forced recoveries are counted; beyond a small fixed limit the
  original fault is rethrown fail-closed (genuine-defect guard, same spirit as the 09-07 storm
  guard). The streak resets once a generation successfully reaches its pump run.
- R4 (no regression): refresh-pipeline behavior for every existing demand source (change event,
  degraded pump 87) is unchanged; all existing tests stay green.
- R5 (telemetry): each absorbed startup fault logs a structured warn event with the native error
  and the attempt-vs-limit count, following `IRuntimeLogger` conventions; the resulting forced
  rebuild is visible in the existing `adapter.refresh` event.

## Acceptance Criteria

- [ ] AC1: Unit test — a generation whose mode snapshot faults with 87 before pumps start is
      recovered: the runner re-enumerates, installs a new generation, the run continues, and the
      fault does not propagate out of `RunAsync`.
- [ ] AC2: Unit test — recovery with an unchanged enumeration (empty diff) still installs a
      replacement generation and logs the rebuild as forced (not as a no-op skip).
- [ ] AC3: Unit test — consecutive startup 87 faults beyond the limit propagate the original
      exception out of `RunAsync` (fail-closed) after a warn.
- [ ] AC4: Unit test — a startup fault with a native error other than 87, and a 87 fault that
      occurs only after the pumps started, both propagate fail-closed (no recovery attempt).
- [ ] AC5: Unit test — a refresh demand racing a startup-87 fault (demand wins `WhenAny`) still
      ends with a live generation: the demand processing absorbs the classified fault instead of
      rethrowing it.
- [ ] AC6: The recovery streak resets after a generation reaches its pump run (a later isolated
      startup fault is recovered even after an earlier recovered fault).
- [ ] AC7: `dotnet build` + full `dotnet test` + repo lint/format checks pass; interface changes
      are additive (`ReachedPumpRun` on `ICaptureGeneration`, runtime latch), fakes updated.

## Constraints

- Windows-only code paths carry `[SupportedOSPlatform("windows")]` where the file does not already.
- No ndisrd driver changes; recovery stays in the managed runner.
- `NdisNativeCallStatus.IsTransientReadError` is NOT extended with 87 (09-07 constraint stands).
- Documentation (prd/design/implement) in English, per `.trellis/spec/backend/index.md`.

## Out of scope / notes

- `NdisAdapterModeController` still has no direct unit tests (it is a thin hardware-bound wrapper
  over `NdisApiDriver`; faking requires a seam this task does not introduce). Its flag semantics
  remain covered indirectly via `CaptureLifecycleTests` fakes. Noted as future hardening.
- Enumeration (`GetAdapters`) failures during a refresh demand stay fatal; only the mode
  snapshot/apply phase gains recovery (the observed production fault).
