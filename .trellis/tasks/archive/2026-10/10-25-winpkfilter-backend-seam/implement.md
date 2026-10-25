# Batch 4 implementation plan

## Steps

1. **Baseline.** Record `Program.cs` line count and the current root test total (1958) and
   `dotnet format`/build status. Read `Program.cs`, `DurableCaptureBundle.cs`, `LayeredCaptureRunner.cs`,
   `StartupLog.cs`, the integration tests and the CLI csproj's `InternalsVisibleTo`.
2. **Seam.** Add `src/WinForward.Cli/Backends/IInterceptionBackend.cs` with
   `ProviderName`, `ValueTask<int> RunAsync(CancellationToken)` and `ValueTask DisposeAsync()`.
   Keep it internal.
3. **Move the WinpkFilter composition.** Add `WinpkFilterBackend` and move the existing
   `RunInterceptionAsync`, `RunCaptureLoopAsync`, `CreateCaptureRunner`, `StartHeartbeat` and
   `WireFramePoolDiagnostics` into it without changing the body order. Preserve:
   - driver open before preflight, driver disposed last;
   - timer scope before capture teardown;
   - bundle disposal in its finally;
   - watcher/runner/heartbeat disposal before bundle disposal;
   - shared `NdisPacketBufferPool` drained after the runtime;
   - adapter-degraded/watcher wiring and heartbeat fields.
   If a file exceeds the 400-effective-line ceiling, split heartbeat/composition into a partial
   file; do not compress/rewrite logic.
4. **Update Program.** Keep configuration/logging/platform/Kernel refusal/report/validate/adapters/
   console cancellation. `RunCaptureAsync` constructs the backend and calls it via a small
   `RunBackendUntilCancelledAsync` that preserves the current console-cancel handler and the
   started/stopped/shutdown/runtime log events and exception-to-exit-code catches. Remove the
   NDISAPI open and capture composition from `Program`. Do not log `BackendSelected` twice.
5. **Exit codes and exceptions.** Keep no-adapters → 3, scope/config → 1, clean → 0, runtime → 3.
   `DllNotFound`/`EntryPoint`/`TypeLoad`/`Win32` still escape to `Program`'s existing catches.
   Verify the backend's `RunAsync` result is returned, not ignored.
6. **Tests.** Update only references required by the move; keep assertions. Build/test/format.
7. **Do not touch** Runtime/NdisApi/Kernel contracts/driver, BackendDriver behaviour, host TCP,
   UDP, forwarded, or unrelated dirty files. No commit.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --fast'
```

The Windows WinpkFilter E2E is not runnable on this Linux host; state that clearly and rely on the
unchanged code paths plus the test suite. `jb inspectcode` remains unavailable (global tool missing
`System.Composition.AttributedModel 10.0.0.12`); record the environment gap.

## Risky files / rollback

- `Program.cs`: the Kernel refusal must stay before backend creation; the console-cancel handler
  must be removed before the CTS scope ends.
- `WinpkFilterBackend.cs`: teardown order and single ownership; a double dispose or a missing
  `finally` is a blocking defect.
- `WinForward.Cli.csproj` / integration tests: only add references if the move requires; do not
  widen `InternalsVisibleTo`.
- Rollback: revert the new backend files and restore the `Program` methods; no runtime state.

## Report

Changed files, the exact moved methods, the observed startup/teardown order, test/format/build
results, and any unavoidable behaviour difference (should be none).
