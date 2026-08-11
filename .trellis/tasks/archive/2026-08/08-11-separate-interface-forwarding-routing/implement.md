# Implementation Plan

## Checklist

- [x] Add an origin-specific policy evaluation path in
      `src/WinForward.Core/Policy.cs` that filters forwarded evaluation to
      adapter-qualified rules and defaults to pass while preserving rule
      indexes and matcher semantics.
- [x] Update `src/WinForward.Runtime/FlowDispatcher.cs` so only genuinely new
      forwarded flows use the forwarded policy path, after self-traffic,
      redirect handling, and existing-flow resolution.
- [x] Apply equivalent forwarded adapter-qualified-only/default-pass behavior
      to non-flow packet evaluation without changing host non-flow behavior.
- [x] Add focused policy and dispatcher tests covering adapter A selected,
      adapter B unselected, an unqualified catch-all proxy rule, block fallback,
      first-match ordering, host behavior preservation, cross-adapter decision
      reuse, and flow-table capacity failure.
- [x] Add non-flow tests for selected and unselected forwarded adapters and for
      unchanged host fallback behavior.
- [x] Update `README.md` configuration semantics and examples text so users know
      unqualified rules/fallback apply to host traffic, while forwarded traffic
      requires an adapter-qualified rule and otherwise passes.
- [x] Run formatting/static checks and the complete test suite.
- [x] Review the final diff against `prd.md` and `design.md`, including the
      direction-derived inbound-local limitation.

## Verification Result

- `dotnet build -c Release`: passed with 0 warnings and 0 errors.
- `dotnet test -c Release --no-build`: passed, 240/240 tests.
- Changed-file `dotnet format --verify-no-changes`: passed.
- `git diff --check`: passed.
- Repository-wide `dotnet format --verify-no-changes` remains blocked by ten
  pre-existing formatting issues in untouched files (nine missing final
  newlines and one import-order issue).

## Validation

```bash
dotnet build -c Release
dotnet test -c Release
dotnet format --verify-no-changes
```

## Review Gates

- No JSON schema change or new compatibility shim.
- No capture-scope narrowing as a substitute for dispatcher enforcement.
- Existing-flow lookup remains before origin-specific policy evaluation.
- No process attribution for forwarded traffic.
- Documentation explicitly states that the boundary is NDIS receive direction,
  including inbound traffic destined for the host.

## Rollback Points

- Policy evaluator and dispatcher changes can be reverted together without
  configuration migration.
- Test and documentation changes should be reverted with the behavior change;
  there are no persisted artifacts or rollout flags.
