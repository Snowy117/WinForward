# Research: audit-cli-integration

- **Query**: Read-only audit of `src/WinForward.Cli/Program.cs` and composition/startup/teardown consumers: command arguments and exit codes, configuration validation before driver use, logger redaction, disposal ordering, Native AOT/publish constraints, and Windows gating.
- **Scope**: internal
- **Date**: 2026-08-10

## Final Results

- **Status**: The composition-root shutdown-order defect described below is fixed in `src/WinForward.Cli/Program.cs`.
- **Regression**: `CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModes` and `CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModesOnCaptureFailure` compose `TransactionalCaptureRuntime` with the CLI-owned shutdown wrapper and deterministically record `sweeper -> capture -> UDP coordinator -> TCP coordinator -> adapter-mode restore` after normal completion and capture failure. `CoordinatorShutdownCompositionWaitsForCaptureRunBeforeClosingProxySessionsOnStop` observes cancellation on a blocked capture loop before proving none of those resources close. They run on Linux and do not require a driver or Windows adapter.
- **Validation**: `dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj -c Release --filter FullyQualifiedName~CaptureLifecycleTests --no-restore` passed 8/8; `dotnet build src/WinForward.Cli/WinForward.Cli.csproj -c Release --no-restore` and `dotnet build -c Release --no-restore` passed with 0 warnings / 0 errors; `dotnet test -c Release --no-restore` passed 215/215; `git diff --check` passed.
- **Windows gate remains**: The regression proves ownership order only. It does not replace elevated Windows x64 validation of actual adapter modes, driver I/O, Ctrl+C, published Native AOT output, proxy outage behavior, or the Hyper-V traffic matrix listed below.

## Findings

### Files Found

| File Path | Description |
|---|---|
| `src/WinForward.Cli/Program.cs` | CLI entry point, command dispatch, composition root, console logging, cancellation handler, exit-code mapping. |
| `src/WinForward.Configuration/ConfigurationModels.cs` | Source-generated JSON parsing and configuration validation. |
| `src/WinForward.Runtime/CaptureLifecycle.cs` | Transactional adapter-mode/capture lifecycle and restoration semantics. |
| `src/WinForward.Runtime/IdleExpirySweeper.cs` | Background expiry-loop lifecycle. |
| `src/WinForward.Runtime/NdisAdapterModeController.cs` | NDIS adapter-mode snapshot/apply/restore bridge. |
| `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs` | Multi-pump capture cancellation/disposal implementation. |
| `src/WinForward.Runtime/TcpProxyCoordinator.cs` | TCP session coordinator disposal. |
| `src/WinForward.Runtime/UdpProxyCoordinator.cs` | UDP session coordinator disposal. |
| `src/WinForward.NdisApi/NdisApiDriver.cs` | NDIS driver open/enumeration and safe-handle ownership. |
| `src/WinForward.NdisApi/NdisApiAbi.cs` | Native-library resolver and source-generated P/Invoke declarations. |
| `src/WinForward.Windows/Platform.cs` | Windows version, x64 OS, and elevation gate. |
| `src/WinForward.Cli/WinForward.Cli.csproj` | `win-x64`, Native AOT, invariant globalization, and single-file publish properties. |
| `Directory.Build.props` | Trim/AOT analyzers and warnings-as-errors configuration. |
| `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs` | Transactional lifecycle unit coverage. |
| `tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs` | Configuration parsing/validation and diagnostic-redaction unit coverage. |
| `tests/WinForward.Core.Tests/CapturePipelineTests.cs` | Capture-scope resolution unit coverage. |
| `README.md` | Documented build/test/publish commands and recorded Windows smoke/hardware matrix. |
| `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/design.md` | Recorded startup/shutdown ordering and Windows integration gate. |

### Command Arguments and Exit Codes

`Program.Main` records the exit-code contract at `src/WinForward.Cli/Program.cs:12-16`: `0` success, `1` configuration/selector/NDIS access error, `2` usage/unknown/unsupported platform, and `3` capture runtime failure.

| Command path | Source behavior | Observed managed probe | Test mapping |
|---|---|---|---|
| no args / `--help` | Writes command synopsis and returns `0` (`Program.cs:20-24`). | `--help`: exit `0`. | No CLI-process test. |
| unknown command | Writes unknown-command diagnostic and returns `2` (`Program.cs:61-62`). | `bogus`: exit `2`. | No CLI-process test. |
| `validate` without `--config <path>` | Writes usage diagnostic and returns `2` (`Program.cs:328-333`). | `validate`: exit `2`. | No CLI-process test. |
| valid `validate --config` | Uses `TryLoadConfig`; writes success and returns `0` (`Program.cs:335-340`). | `examples/pass-fallback.json`: exit `0`. | Configuration parsing/validation and examples are covered by `FlowAndConfigurationTests.cs:927-940`; not by the CLI process. |
| invalid/unreadable `validate --config` | Returns `1` after parse/validation failure or selected I/O/authorization/JSON exception (`Program.cs:337`, `341-345`). | Not separately invoked in this audit. | Parser/validator cases are in `FlowAndConfigurationTests.cs`; no CLI-process exit assertion. |
| `run` without `--config <path>` | Writes usage diagnostic and returns `2` before configuration or driver activity (`Program.cs:36-43`). | `run`: exit `2`. | No CLI-process test. |
| valid `run --config` on non-Windows | Validates the configuration first, then returns `2` for the OS gate (`Program.cs:44-49`). | `run --config examples/pass-fallback.json`: exit `2` on this Linux host. | Configuration seam is covered; Windows branch is not executable here. |
| `adapters` on non-Windows | Returns `2` before `ListAdapters`/driver access (`Program.cs:27-35`). | `adapters`: exit `2` on this Linux host. | No CLI-process test. |
| `adapters` on supported Windows | `ListAdapters` applies `PlatformRequirements`, opens driver, emits stable/friendly/internal identity and returns `0`; known native availability failures return `1` (`Program.cs:90-111`). | Not executable on this host. | Adapter correlation has unit coverage; no CLI-process test. The Windows smoke contract is documented in `spec/backend/windows-ndisapi.md:43-44`. |

`FindOption` chooses the first case-insensitive occurrence of an option and does not reject unrecognized or trailing arguments (`Program.cs:348-352`). For example, `validate --config <valid-path> --unknown` follows the valid-config branch. No source-level command grammar beyond the required command/config option is present, and no test exercises this behavior.

Linux check probe: `dotnet exec src/WinForward.Cli/bin/Release/net10.0/win-x64/WinForward.dll` confirmed `--help` and valid `validate` exit `0`, while an unknown command, missing `--config`, `run --config examples/pass-fallback.json`, and `adapters` exit `2` with the documented diagnostics. `dotnet run` cannot launch this project on Linux because its fixed `win-x64` RID selects `WinForward.exe` (`Exec format error`); this managed-entrypoint probe does not substitute for the published Windows executable gate.

### Configuration and Driver Ordering

The `run` path calls `TryLoadConfig` before `OperatingSystem.IsWindows`, `PlatformRequirements.TryCheck`, and `RunCaptureAsync` (`Program.cs:38-58`). `TryLoadConfig` reads the file, parses it, and validates it before it can produce a `ValidatedConfiguration` (`Program.cs:65-86`). The driver first opens later in `RunInterceptionAsync` (`Program.cs:150-153`). This confirms configuration validation precedes driver creation and adapter-mode application for the `run` command.

The `adapters` command has no configuration input and opens the driver only after its platform gate (`Program.cs:90-101`). The `validate` command only calls `TryLoadConfig` and has no driver consumer (`Program.cs:326-345`).

Configuration parsing uses the source-generated `ConfigurationJsonContext` (`ConfigurationModels.cs:50`, `68-85`). Validation produces a `ValidatedConfiguration` only when diagnostics are empty (`ConfigurationModels.cs:89-135`). Unit tests cover valid/invalid configuration elements and published examples (`FlowAndConfigurationTests.cs:524-544`, `927-940`).

### Logger Redaction

Confirmed safe configuration-diagnostic path:

- `ConfigurationLoader.TryParse` turns malformed JSON into `ConfigDiagnostic` output instead of returning the serializer exception (`ConfigurationModels.cs:68-85`).
- `Program.PrintDiagnostics` writes those diagnostics (`Program.cs:354-357`).
- `ConfigurationParseDiagnosticsNameTheFailingFieldWithoutEchoingCredentials` verifies that an unknown JSON value containing credential-like text is absent from the diagnostic (`FlowAndConfigurationTests.cs:595-611`).
- `ConfigurationEnforcesUtf8CredentialLengthWithoutDisclosingPassword` verifies the password value is absent from a validation diagnostic (`FlowAndConfigurationTests.cs:670-700`).

Coverage gap, not a confirmed credential disclosure: `ConsoleRuntimeLogger` is a prefix-only console sink (`Program.cs:359-364`), and several outer error paths interpolate `exception.Message` directly: configuration I/O (`Program.cs:82-85`, `341-345`), adapter availability (`107-110`), NDIS driver errors (`137-140`), startup (`142-145`), and runtime failures (`203-206`). The inspected configuration parser/validator does not expose SOCKS5 credential values through those known paths, but there is no CLI-process test that asserts console output excludes configured credentials for those outer exception branches.

### Startup and Teardown Ordering

Source-confirmed lifecycle behavior:

1. `RunInterceptionAsync` owns the `NdisApiDriver` with `using var`; it enumerates and resolves the capture scope before entering the capture composition (`Program.cs:150-171`).
2. `RunCaptureLoopAsync` creates the CLI-owned capture composition, adapter-mode controller, and `TransactionalCaptureRuntime`; `CreateCaptureCompositionAsync` owns TCP/UDP coordinators, dispatcher/processor, capture loop, and `IdleExpirySweeper` (`Program.cs:175-255`).
3. `Console.CancelKeyPress` cancels the run token; the handler is removed in `finally` (`Program.cs:184-211`).
4. `TransactionalCaptureRuntime.StartAsync` snapshots modes, applies them, runs capture, then always disposes capture before restoring the successfully applied modes in reverse order. `StopAsync` cancels an active run and awaits that run's cleanup rather than disposing its capture loop concurrently (`CaptureLifecycle.cs:51-119`). Mode restoration continues across per-adapter restore failures (`CaptureLifecycle.cs:123-139`).
5. `MultiAdapterCaptureLoop` propagates cancellation to every pump and disposes every pump (`MultiAdapterCaptureLoop.cs:29-67`).
6. `IdleExpirySweeper.DisposeAsync` cancels and awaits its loop before releasing its cancellation source (`IdleExpirySweeper.cs:79-90`).

#### Resolved: composition disposal order

- `RunCaptureLoopAsync` now passes `CoordinatorShutdownCaptureLoop` to `TransactionalCaptureRuntime` (`Program.cs:179-181`). The wrapper owns the active sweeper, capture loop, UDP coordinator, and TCP coordinator; `TransactionalCaptureRuntime.StartCoreAsync` disposes this capture-loop wrapper before `RestoreBestEffortAsync` restores modes (`CaptureLifecycle.cs:78-86`).
- `CoordinatorShutdownCaptureLoop.DisposeAsync` releases the sweeper, capture loop, UDP coordinator, and TCP coordinator in reverse acquisition order, continuing cleanup through every component when an earlier disposal throws (`Program.cs:398-430`). This places proxy-session closure before adapter-mode restoration for normal completion, cancellation, handled capture failure, and mode-application rollback.
- `CreateCaptureCompositionAsync` releases already-created coordinator(s) if later composition fails before wrapper ownership begins (`Program.cs:214-255`). The unchanged outer `await using` retains idempotent cleanup after runtime disposal.
- `CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModes` and `CoordinatorShutdownCompositionClosesProxySessionsBeforeRestoringModesOnCaptureFailure` in `CaptureLifecycleTests.cs` verify the observable order deterministically after normal completion and through `TransactionalCaptureRuntime`'s capture-failure `finally` path. `CoordinatorShutdownCompositionWaitsForCaptureRunBeforeClosingProxySessionsOnStop` adds the active-stop path. They are composition-order regressions, not hardware substitutes.

`CaptureLifecycleTests` verifies rollback of an already-applied adapter on apply failure (`CaptureLifecycleTests.cs:8-19`), cancellation restoration (`21-34`), an explicit active-stop wait before restoration (`52-70`), normal capture completion restoration/disposal (`38-49`), disposal before start (`74-84`), and CLI-owned composition ordering after normal completion, capture failure, and stop (`87-149`). It does not open a driver or alter real adapter modes.

### Native AOT, Publish, and Windows Gate

Publish-related source configuration is present:

- CLI project sets `PublishAot`, `InvariantGlobalization`, `RuntimeIdentifier` `win-x64`, and `PublishSingleFile` (`src/WinForward.Cli/WinForward.Cli.csproj:3-9`).
- Root build properties make warnings errors and enable both trim and AOT analyzers (`Directory.Build.props:3-12`).
- Configuration JSON uses source generation (`ConfigurationModels.cs:50`, `68-74`), and NDIS interop uses `[LibraryImport]` plus a resolver that loads `ndisapi.dll` only from `AppContext.BaseDirectory` (`NdisApiAbi.cs:114-162`).
- Runtime platform validation requires Windows, Windows 10 22H2+, a 64-bit OS, and an elevated token (`Platform.cs:10-18`); `run` and `adapters` invoke it on the Windows path (`Program.cs:50-57`, `92-96`).

Gate execution in this audit:

| Gate | Result | Evidence |
|---|---|---|
| `dotnet build src/WinForward.Cli/WinForward.Cli.csproj -c Release` | Passed, 0 warnings / 0 errors. | Executed on Ubuntu 26.04, .NET SDK 10.0.109. |
| `dotnet test -c Release --no-restore` | Passed, 215/215. | Executed on this host after the normal-completion, capture-failure, and active-stop shutdown-order regressions were added. |
| `dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64` | Not runnable on this Linux host. | Native AOT build reached IL compilation, then SDK emitted: `Cross-OS native compilation is not supported.` |

Windows-only gate steps documented by the repository:

1. On an elevated supported Windows x64 host with compatible WinpkFilter driver and `ndisapi.dll` sidecar, execute `dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64` (`README.md:162-164`).
2. Run the published executable's `validate --config <example>` and invalid-config cases, requiring expected `0`/`1` exits and field-path diagnostics; run `adapters`, requiring `0` and stable/friendly/internal adapter output; repeat without the sidecar, requiring `1`. The recorded AOT smoke matrix is at `README.md:24-33` and the adapter-specific contract is at `.trellis/spec/backend/windows-ndisapi.md:43-44`.
3. Start `run --config <example>`, send Ctrl+C, and verify clean exit plus exact adapter-mode restoration. This is recorded as a Windows hardware requirement in `README.md:33` and in the design integration matrix (`design.md:313-323`).
4. Run proxy-selected TCP and UDP traffic with an unavailable SOCKS5 endpoint. Each selected flow must fail closed, leave no listener/association/session after teardown, and must never fall back to pass.
5. Run the host and Hyper-V matrix through a real SOCKS5 server: IPv4 and IPv6 TCP redirect, UDP ASSOCIATE/response reinjection, retransmitted SYN, FIN/RST/half-close, reverse injection toward MSTCP versus the forwarded origin adapter, and idle expiry. Hyper-V requires a real guest/virtual-switch topology; it remains unexecuted on this host.

### Resolved Defects

| Anchor | Evidence / reproduction | Test mapping |
|---|---|---|
| `src/WinForward.Cli/Program.cs:179-181`, `214-255`, `371-431`; `src/WinForward.Runtime/CaptureLifecycle.cs:78-119` | Previously, `runtime.StartAsync` restored adapter modes before outer scope unwinding disposed TCP/UDP coordinators. The CLI now wraps capture and both coordinators in the runtime-owned `CoordinatorShutdownCaptureLoop`, which completes coordinator disposal before runtime restoration. `StopAsync` now waits for an active capture run's own `finally` cleanup rather than disposing that wrapper concurrently. | Normal-completion and capture-failure regressions assert `sweeper -> capture -> UDP -> TCP -> restore`; the active-stop regression asserts this cleanup cannot start until the cancelled capture run completes. |

### Coverage Gaps

- `WinForward.Core.Tests` now references `WinForward.Cli` solely for the friend-visible composition-order seam. It does not launch `Program.Main`, so command parsing, stdout/stderr text, process exit codes, and `Console.CancelKeyPress` registration/removal still have no direct automated test.
- CLI grammar beyond finding `--config` is untested: duplicate options, missing option value when followed by another token, and extra/unknown trailing arguments all lack a process-level expectation.
- Console redaction is tested for `ConfigurationLoader` diagnostics but not for outer exception-message logging paths in `Program.cs:82-85`, `107-110`, `137-145`, `203-206`, and `341-345`.
- The current Linux test host cannot execute the Windows platform/elevation/driver branch, native AOT `win-x64` publish, driver-open failure handling, adapter enumeration, or Ctrl+C restoration against real adapter modes. The local publish failure is an SDK host constraint, not a source compile/analyzer failure.

## Related Specs

- `.trellis/spec/backend/error-handling.md:25-26` — malformed configuration diagnostics must not append raw `JsonException.Message` or raw JSON values.
- `.trellis/spec/backend/windows-ndisapi.md:43-44` — adapter unit and Windows CLI smoke contract.
- `.trellis/tasks/archive/2026-08/08-07-winforward-proxy/design.md:277-290` — declared transactional startup/shutdown sequence.

## Caveats / Not Found

- This audit did not execute an NDIS driver, alter adapter modes, or run the published native executable because the host is Linux (`linux-x64`).
- README hardware/AOT statements are recorded project documentation, not re-executed Windows evidence from this audit.
- No GitHub Actions or other repository CI workflow files were found to provide an independent Windows gate.
