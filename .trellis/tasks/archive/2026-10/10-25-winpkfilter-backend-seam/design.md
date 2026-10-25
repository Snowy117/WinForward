# Batch 4 design: WinpkFilter backend seam

## Interface

```csharp
internal interface IInterceptionBackend : IAsyncDisposable
{
    string ProviderName { get; }
    ValueTask<int> RunAsync(CancellationToken cancellationToken);
}
```

The backend returns the process exit code it owns (0 clean, 1 config/scope, 3 no adapters/runtime).
Exceptions that the current `Program` maps to specific diagnostics (`DllNotFoundException`,
`EntryPointNotFoundException`, `TypeLoadException`, `Win32Exception`) still escape to `Program`'s
existing catch blocks; the backend does not swallow them.

## WinpkFilterBackend

Move, without rewriting, the current blocks from `Program`:

- `RunInterceptionAsync`: driver open, adapter enumeration, no-adapter exit 3, scope resolution
  exit 1, health monitor creation.
- `RunCaptureLoopAsync`: timer scope, frame-pool diagnostics, `DurableCaptureBundle`, adapter
  watcher, `LayeredCaptureRunner`, heartbeat, bundle disposal, shared pool disposal.
- `CreateCaptureRunner` and `StartHeartbeat`.

The backend is constructed with `ValidatedConfiguration`, `ILoggerFactory` and the CLI logger. The
driver handle is opened inside `RunAsync` so the existing exception mapping around the call still
applies. The backend owns every IDisposable it creates; its `RunAsync` keeps the current
using/finally order:

1. driver opened and kept for the run;
2. timer scope first, so it outlives capture teardown;
3. bundle created and disposed in its own finally;
4. watcher/runner/heartbeat disposed before bundle disposal;
5. shared `NdisPacketBufferPool` drained after the capture runtime;
6. driver disposed last.

`DisposeAsync` is a no-op for this backend because `RunAsync` owns the whole lifetime; keep the
interface method for the Kernel backend later. Do not create a second timer/pool/driver.

File layout: `src/WinForward.Cli/Backends/IInterceptionBackend.cs`,
`src/WinForward.Cli/Backends/WinpkFilterBackend.cs`; split the heartbeat/composition helpers into
`WinpkFilterBackend.Heartbeat.cs` if the 400-effective-line ceiling requires it.

## Program

- Keep `Main`, `TryLoadConfiguration`, `TryCreateLoggerFactory`, `Validate`, `ListAdapters`,
  `ReportResolvedRun`, `DescribeSources`, `FindOption`, `PrintDiagnostics`.
- Keep the Batch 2 Kernel-provider refusal (exit 1) before any backend is created.
- `RunCaptureAsync` creates the WinpkFilter backend and calls a small
  `RunBackendUntilCancelledAsync(backend)` that owns only the console-cancel lifetime: it subscribes
  the handler, awaits `backend.RunAsync(shutdown.Token)`, removes the handler before the source is
  disposed, and returns the exit code the backend concluded. The exception-to-exit-code catches
  (`DllNotFoundException`, `EntryPointNotFoundException`, `TypeLoadException`, `Win32Exception` and
  the fallback) stay in `RunCaptureAsync`, around backend construction and the call; the backend
  never swallows them.
- The backend owns the interception lifecycle: it emits `StartupLog.InterceptionStarted`,
  `StoppedCleanly`, `ShutdownRequested` and `RuntimeFailed` from inside its own run, and returns 0
  for a clean or cancelled stop and 3 for a runtime fault. The pre-flight exits stay with the
  composition that decides them: the backend returns 3 for an empty adapter list and 1 for a
  scope-resolution failure; `Program` returns 1 for the Kernel refusal and the NDISAPI diagnostics
  and 2 for the platform and usage checks.
- Accepted host-layer difference: the console-cancel window now spans the whole backend run instead
  of only the runner call, so Ctrl+C during startup or teardown is no longer a hard process
  termination. The token is still observed only where it is awaited (the capture runner), so a
  Ctrl+C before that point lets the run reach the runner and stop cleanly; the 0/1/2/3 exit-code
  contract is unchanged.
- The `BackendSelected` log stays where it is; do not log it twice.
- No NDISAPI open or capture composition remains in `Program`.

## Tests and evidence

- The existing `WinForward.Integration.Tests` composition surface must still compile; update only
  references made necessary by the move, never the assertions.
- Root `dotnet build`, `dotnet test`, `dotnet format` and the Kernel fast gate are the batch gates.
- The Windows WinpkFilter E2E is not runnable on this Linux host; record that the equivalence is
  established by the unchanged code paths, the existing test suite and review, not by a new E2E.
- The `jb inspectcode` global tool remains broken (missing `System.Composition.AttributedModel
  10.0.0.12`); record it as an environment gap.

## Risks

- A moved `using`/`finally` changes teardown order and can leak the driver/pool. Keep the original
  order and review it line by line.
- The cancel handler must not capture a disposed CTS; the helper must remove the handler before the
  CTS scope ends, exactly as `RunUntilCancelledAsync` does today.
- The no-adapter/scope exits must still return 3/1, not exceptions that map to 0/3.
- The backend must not hold the logger factory after the run; no static state.
- The Kernel provider must not touch NDISAPI or fall back; the existing refusal is the safety net.
