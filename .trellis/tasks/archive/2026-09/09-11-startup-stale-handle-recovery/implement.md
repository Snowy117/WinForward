# Implement — startup stale-handle recovery

Ordered checklist. Each step compiles and keeps the suite green before the next.

## Step 1 — Phase latch in `TransactionalCaptureRuntime`

- [ ] Add `public bool ReachedPumpRun { get { lock (_gate) return _reachedPumpRun; } }` (private
      bool field) to `src/WinForward.Runtime/Capture/CaptureLifecycle.cs`; set it under the gate
      in `StartCoreAsync` immediately before `await _capture.RunAsync(runtimeCancellation)`
      (together with the `Running` state transition).
- [ ] `RuntimeCaptureGeneration` (in `NdisCaptureGeneration.cs`): implement
      `ICaptureGeneration.ReachedPumpRun` by delegating.
- [ ] Unit tests in `CaptureLifecycleTests`: latch stays false when `SnapshotAsync` /
      `ApplyCaptureModeAsync` throws; latch true once the loop run started.

Validation: `dotnet build && dotnet test --filter CaptureLifecycleTests`

## Step 2 — Runner classification + recovery

- [ ] `LayeredCaptureRunner`: add `MaxConsecutiveStartupRecoveries` const, `_startupFaultStreak`,
      `_forceRebuild` fields and `IsRecoverableStartupFault(generation, fault)` helper.
- [ ] `ObserveGenerationExitAsync`: catch-classify; recoverable →
      `RecoverFromStartupFaultAsync` (streak/cap, warn `generation.startup-fault`, dispose dead
      generation, clear `_generation`/`_runTask`, set `_forceRebuild`, signal demand) and return
      to the loop; else rethrow (today's behavior).
- [ ] `StopGenerationAsync`: in the fault-capture branch, classify; recoverable → absorb with the
      same streak/cap + warn + `_forceRebuild = true` (no extra signal: the demand processing
      installs next); over-cap → rethrow.
- [ ] `ProcessRefreshDemandAsync`: consume `_forceRebuild` at entry; forced demand skips the
      empty-diff early return; `LogRefresh` adds `forced=true` on forced installs.
- [ ] Streak reset wherever a generation with `ReachedPumpRun == true` completes
      (natural exit, refresh stop, teardown drain).
- [ ] Keep `[SupportedOSPlatform]` discipline; no comments beyond the repo's doc-comment style.

Validation: `dotnet build && dotnet test --filter LayeredCaptureRunner`

## Step 3 — Fakes + recovery tests

- [ ] `CaptureRunnerFakes`: startup-fault knob on the fake generation (exception thrown before
      pumps-started latch; configurable per-creation via the factory script).
- [ ] New test cases in `LayeredCaptureRunnerRefreshTests` per design §7 (AC1-AC6): recovery
      success; forced install on empty diff; cap exceeded → original fault propagates; non-87
      startup fault and post-pump 87 propagate; demand-races-fault lands a live generation;
      streak resets after a successful generation.

Validation: `dotnet test --filter LayeredCaptureRunnerRefreshTests`

## Step 4 — Full gate

- [ ] `dotnet build` (whole solution, warnings-as-errors state preserved).
- [ ] `dotnet test` full suite.
- [ ] Repo lint/format checks (verify command from AGENTS.md / quality-guidelines).
- [ ] Review diff against prd ACs; confirm no behavior change outside the fault path.

## Review gates / rollback

- Gate after Step 2 (runner semantics) and after Step 4 (full scope check via trellis-check).
- Rollback point: single-commit revert; no persistent state, no config surface.
