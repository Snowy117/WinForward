# Batch 4: extract the WinpkFilter backend seam

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Introduce an internal `IInterceptionBackend` and move the existing WinpkFilter capture/redirect
composition out of `Program` into `WinpkFilterBackend`. `Program` keeps configuration, logging,
platform checks, console cancellation and exit-code mapping. The Kernel provider still refuses with
the Batch 2 diagnostic; no Kernel data path is added. Behaviour of the WinpkFilter provider must be
byte-for-byte unchanged.

## Requirements

- **D4-R1 Seam.** Add an internal `IInterceptionBackend` at the CLI composition boundary:
  `ValueTask<int> RunAsync(CancellationToken)` plus `ValueTask DisposeAsync()`, with a provider name
  for logs. Keep it narrow; do not abstract the packet path or the coordinators.
- **D4-R2 WinpkFilter backend.** `WinpkFilterBackend` owns the NDISAPI driver handle, adapter
  pre-flight, capture-scope resolution, `DurableCaptureBundle`, `LayeredCaptureRunner`, the
  health monitor, the high-resolution timer scope, the heartbeat, frame-pool diagnostics, adapter
  watcher and the exact teardown order currently in `Program`. It returns the same exit codes
  (1 scope/config, 3 no adapters/runtime) and emits the same `StartupLog` events.
- **D4-R3 Program.** `Program` keeps argument parsing, configuration load/validation, logger
  creation, platform checks, the Kernel-provider refusal, `validate`, `adapters`, the resolved-run
  report, console cancellation and the exception-to-exit-code mapping. It creates the backend for
  WinpkFilter and calls it; it must not open NDISAPI itself after the extraction.
- **D4-R4 No behaviour change.** The WinpkFilter data path, capture composition, teardown ordering,
  heartbeat, log events, exit codes and exception mapping stay equivalent. No Runtime/NdisApi/
  Kernel contracts/driver changes; no Kernel backend or raw-socket relay seam in this batch.
- **D4-R5 Quality.** Root build/test/format and the Kernel fast gate pass; the existing integration
  tests are updated only if the seam requires it. The missing `jb inspectcode` global tool remains
  an environment gap; the primary gates are the evidence.

## Acceptance Criteria

- [x] D4-AC1: `WinForward.slnx` builds; `dotnet test` is green with the same total (1958) and
      `dotnet format` exits 0 with empty output.
- [x] D4-AC2: a WinpkFilter run follows the same startup/teardown order and returns the same exit
      codes for no-adapters (3), scope/config failure (1), clean shutdown (0) and runtime failure
      (3); no resource is disposed twice or leaked.
- [x] D4-AC3: a Kernel-provider `run` still returns 1 before any NDISAPI open and never falls back
      silently.
- [x] D4-AC4: the heartbeat, resolved-run report and startup/shutdown logs are emitted from the
      same points as before; the provider name is logged once.
- [x] D4-AC5: no Runtime/NdisApi/Kernel contracts/driver/forwarded/host-TCP changes; no unrelated
      dirty files.

## Out of Scope

- The Kernel backend data path, raw-socket relay overload, host TCP/UDP, forwarded, ARM64,
  production packaging.
- Changing WinpkFilter capture/redirect/relay behaviour or the CLI public commands.

## Known follow-ups

- `src/WinForward.Runtime/InterceptionHealthMonitor.cs:127` and
  `src/WinForward.Runtime/AdapterTransientRetryLogGate.cs:15` still name `Program` as the owner that
  creates and wires these objects. The extraction moved that wiring into `WinpkFilterBackend`; this
  batch's no-Runtime rule leaves the comments for the change that next touches
  `WinForward.Runtime`.
- `IInterceptionBackend.ProviderName` has no reader yet: the resolved-run report keeps logging
  `BackendDriver.ProviderName` from configuration. The property is meant to be consumed when the
  Kernel backend lands; if `jb inspectcode` becomes available again and reports it unused, decide
  then whether to consume it or remove it.
- The seam has no fake-backend test. A test pinning the exit-code passthrough and the
  handler-removal contract of `RunBackendUntilCancelledAsync` is optional follow-up hardening; it
  was not added in this batch.
