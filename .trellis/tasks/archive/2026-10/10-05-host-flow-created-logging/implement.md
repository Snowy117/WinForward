# Implementation plan: host flow creation logging in the deferred attribution path

Task: `.trellis/tasks/10-05-host-flow-created-logging` · Design: `design.md` · Requirements: `prd.md`

## Preconditions

- Task status is `in_progress` (`task.py start` after this plan is reviewed).
- Working tree holds only the untracked `test.log` capture; that file is not part of the change.
- Rollback point: commit `13f8a79` (current `master` HEAD). Every step below is revertible on its own.

## Steps

1. **Load the coding guidelines for the layers being touched** (`trellis-before-dev`): read
   `.trellis/spec/backend/` indexes and the quality-guidelines / logging docs before editing.
   Confirm the suppression and allocation rules the two gates enforce.

2. **Single-source the `flow.created` payload** — `src/WinForward.Runtime/FlowDispatcher.cs`
   - Rename `FlowFields(CapturedFlowPacket, int)` (`:534-545`) to
     `FlowCreatedFields(FlowContext context, long generation, int additionalFields)`.
   - Update the inline call site (`:269`) to pass `packet.Context, packet.FlowGeneration`.
   - Behavior must be identical: same field order, same null-omission semantics.
   - **Superseded by the review pass** (design.md §2, 2026-10-05): the implementer's rename left the
     three trailing slots duplicated at both call sites, so the reviewer folded the whole payload
     into `LogFlowCreated` and deleted the builder. The inline site is now the one-line call
     `LogFlowCreated(packet.Context, packet.FlowGeneration, claimed.Decision)`, and the tree was
     re-validated on that frozen shape.

3. **Add the host hook** — `src/WinForward.Runtime/FlowDispatcher.cs`
   - Add `private void LogFlowCreated(FlowContext, long, FlowDecision)` with
     `if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;` as the first statement.
   - Add the explicit `IFlowAttributionHost.LogFlowCreated` implementation beside the other
     explicit members (`:373-387`).

4. **Declare the hook** — `src/WinForward.Runtime/FlowAttributionPipeline.cs`
   - Add `void LogFlowCreated(FlowContext context, long generation, FlowDecision decision);` to
     `IFlowAttributionHost` (`:11-32`), documented as the deferred counterpart of the inline
     emission.

5. **Hand the generation back through the claim** —
   `src/WinForward.Runtime/FlowAttributionPendingIndex.cs`
   - `Claim(PendingFlowAttribution entry, out long generation)`; set the created
     `FlowState.Generation` on `Claimed`, `0` on `CapacityBlocked` / `AlreadyAttributed`.

6. **Emit at claim time** — `src/WinForward.Runtime/FlowAttributionPipeline.cs`
   - In `Deliver` (`:269-279`) capture `var claim = _index.Claim(entry, out var generation);`
     switch on the captured value, call `_host.LogFlowCreated(...)` in the `Claimed` arm, and let
     `default:` throw with the captured value instead of calling `Claim` a second time.

7. **Update the claim call sites in tests** —
   `tests/WinForward.Runtime.Flow.Tests/FlowAttributionPendingIndexTests.cs`
   - Four `index.Claim(...)` / `entry.Claim(...)` assertions gain the `out` argument; assertions
     themselves stay unchanged.

8. **Regression tests** — `tests/WinForward.Runtime.Flow.Tests/FlowAttributionPipelineTests.cs`
   - Let `Harness` accept an optional `RecordingRuntimeLogger` and pass it to the dispatcher.
   - Fact 1: dispatch three packets of one host flow; after the pipeline settles, assert exactly one
     recorded `flow.created` event whose fields carry `origin=host`, `process=dns.exe`, `rule=0`,
     and the flow's generation.
   - Fact 2: with `IncludeProcessPathInLogs: true` in the harness configuration, the same event
     carries `processPath`; with the default configuration it does not.
   - Fact 3 (regression): a forwarded packet through the same pipeline-composed dispatcher still
     emits exactly one `flow.created` from the inline path, with `origin=forwarded` and no
     `process`.

9. **Document the emission point** — `README.md`
   - In the logging paragraph (~`:169-173`), state that a host flow's `flow.created` line is written
     when the deferred attribution claim completes, so it can follow the proxy legs that flow
     produced.

## Validation

Run in order; each must pass before the next.

```bash
dotnet build WinForward.slnx -c Release                     # zero warnings
dotnet test tests/WinForward.Runtime.Flow.Tests -c Release  # focused: pipeline + logging facts
dotnet test WinForward.slnx -c Release                      # full suite, green
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0, empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue> entries
```

The format and inspectcode gates take minutes to tens of minutes; run them once the code is frozen,
never piped through `tail` (it hides the exit code).

## Review gates

- After step 6: re-read the `Deliver` diff against `design.md` §3 — the claim must remain the last
  state change of delivery, and no counter may move for the `Claimed` arm.
- After step 8: confirm Fact 1 fails if step 6 is reverted (the test must actually pin the gap, not
  pass vacuously).
- Before commit: the acceptance criteria in `prd.md` are all observably satisfied; `git status`
  shows only intended files (`test.log` stays untracked).

## Rollback

Single cohesive change: `git revert` of the task commit restores the previous logging behavior with
no configuration or data migration. If only the log volume turns out to be a problem in the field,
the hook can be dropped without touching the claim or the field builder.
