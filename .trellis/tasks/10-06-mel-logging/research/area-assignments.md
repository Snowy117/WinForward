# Area assignments and traps

Companion to `research/migration-brief.md`. The brief carries the rules; this file carries the
per-area partition and the traps specific to each area. Owners are listed in `implement.md` step 2.

Two files span several areas and are therefore owned by the integrator, not by an area owner, so
no two owners fight over a file that cannot compile until a third area lands:

- `tests/WinForward.Integration.Tests/LocalTargetDispatchTests.cs` — drives `UdpProxyComposer`
  (udp), `NdisPacketActionExecutor` (capture) and `FlowDispatcher` (flow).
- `tests/WinForward.Integration.Tests/DurableCaptureBundleTests.cs` — drives the CLI's
  `DurableCaptureBundle`.

## Cross-area rule

A production type consumed by `Program.cs` cannot flip to `ILogger` without breaking the CLI until
the CLI's own step. Area owners therefore verify with
`dotnet build src/WinForward.Runtime/WinForward.Runtime.csproj -c Release` plus their own test
project, never with a whole-solution build. When an area needs a type another area owns, the type
moves to the earlier area rather than being worked around.

The same coupling exists one level down: `tests/WinForward.TestSupport/` references production
types from every area, so it can only compile once all of them have converted. That is why
`RecordingRuntimeLogger` in that project implements both `IRuntimeLogger` and `ILogger` — it keeps
the shared fakes compiling against a mixed tree so every area's suite stays runnable. No area
owner needs to convert a fake another area owns.

It exists a second time in the benchmark project. `tests/WinForward.Runtime.UdpProxy.Tests`,
`tests/WinForward.Performance.Tests` and `tests/WinForward.NdisApi.Tests` all have a
`ProjectReference` to `benchmarks/WinForward.Benchmarks` — the udp suite reuses
`WinForward.Benchmarks.Stability`'s soak harness — so the benchmark project is a **build
dependency** of those three suites and cannot be deferred to the benchmark step. Its two
logger sinks (`ThresholdOnlyLogger` in `BenchmarkShared.cs`, `CountingRuntimeLogger` in
`Stability/StabilityShared.cs`) therefore get the same dual-interface shim, and the integrator
turns them into real `ILoggerProvider`s in step 5.

## capture

Files: `src/WinForward.Runtime/Capture/**`, `src/WinForward.Runtime/InterceptionHealthMonitor.cs`,
`tests/WinForward.Runtime.Capture.Tests/**`, `tests/WinForward.TestSupport/CaptureRunnerFakes.cs`.

Traps:

- `NdisPacketActionExecutor.LogPacket(eventName, packet, params additional)` takes a `string
  eventName`; enumerate its callers and emit one method per name, each with the four-field block
  it prepends (`packet`, `flow`, `protocol`, `stage` — `stage` carries the *old* event name, now
  redundant with `EventId.Name`; drop it unless a test asserts on it).
- `LayeredCaptureRunner` builds field arrays and spreads them (`[.. fields]`) for
  `runner.forcedRefresh` and `adapter.refresh`. Those become fixed parameter lists.
- `InterceptionHealthMonitor` is in this area because `LayeredCaptureRunner` constructs it; its own
  tests live in the flow test project and are not part of this area.
- `foreach (var error in scopeErrors) _logger.Error(error)` and the `warnings` loop emit dynamic
  text: they become single-placeholder methods.
- `LayeredCaptureRunner.cs` and `NdisPacketActionExecutor.cs` are the two largest files here, and
  both carry `Information`-level lines that must keep their level.

## udp

Files: `src/WinForward.Runtime/UdpProxy/**` (including the legacy `UdpProxyLogging.cs`, which the
new `Logging/UdpProxyLog.cs` replaces and which is deleted), `src/WinForward.Runtime/Socks5/**`,
`tests/WinForward.Runtime.UdpProxy.Tests/**`, `tests/WinForward.Runtime.Socks5.Tests/**`, and
`tests/WinForward.TestSupport/UdpTransportTestFactory.cs`.

Traps:

- `UdpProxyLogging.LogDebug(logger, eventName, ...)` carries **three** names
  (`udp.session.created`, `udp.session.closed`, `udp.session.expired`) and
  `UdpProxyLogging.LogTrace(logger, eventName, flow, params additional)` carries **four**
  (`udp.setupqueue.dropped`, `udp.setup.cooldown`, `udp.session.rejected`, `udp.packet.sent`).
  Each becomes its own method with the shared field block written out explicitly. Note the
  `additional` payloads differ in arity: `udp.packet.sent` supplies `packet`, `flow`, `bytes`,
  `udpAssociation` while the others supply a single `reason` or `dropped`.
- `UdpResponseReinjector.LogTrace(eventName, flow, params fields)` carries two names. All four
  `udp.response.dropped` call sites share one name and one field set, so they collapse into a
  single method taking `reason` (and `bytes`, null for the three fail-closed call sites) — the
  event name is constant and only the fields vary.
- `UdpProxySession.MaybeLogSkipSummary` and `UdpSetupQueueBudget` currently emit
  `string.Create(...)` debug text. They are **kept**, not pruned, and become structured events
  with named fields (`unexpectedSource`, `oversized`, `malformed`, `connectionReset`,
  `domainDestination`; and the setup-queue drop total) rather than a `{detail}` blob: those
  counters have no other surface.
- Several throttled sites are `if (!_logger.IsEnabled(Warn)) return;` in front of a
  `RuntimeLogThrottle.ShouldEmit()` check. Only the level test goes; `ShouldEmit()` stays, and it
  must stay **first**, because the throttle contract is "suppressed occurrences cost nothing".
- `Socks5UdpAssociation` reaches its logger through `_context.Logger`; that context record's
  member type changes with everything else.

## tcp

Files: `src/WinForward.Runtime/TcpRedirect/**` (including the legacy `TcpRedirectLogging.cs`,
replaced by `Logging/TcpRedirectLog.cs` and deleted),
`tests/WinForward.Runtime.TcpRedirect.Tests/**`, `tests/WinForward.TestSupport/TcpCoordinatorFakes.cs`.

Traps:

- `TcpRedirectLogging.LogDebug` and `.LogTrace` take a `string eventName`; enumerate the callers
  and emit one method per name.
- `ClientResetInjector` and `TcpProxyCoordinator.Injections` build fields inline at the call site,
  including `tcp.redirect.relaySetupFailed`'s error type / socketError / nativeError / upstream /
  attempts set — the failure-path reason rule in the logging spec applies: keep every
  distinguisher.
- `TcpRedirectSessionStore` and `TcpProxyRelay` log disposal failures where the only payload is
  `exception.GetType().Name`. Those are genuinely unexpected faults, so they may take a real
  `Exception` parameter, but the existing structured field must survive whichever way you go.

## flow + runtime

Files: `src/WinForward.Runtime/FlowDispatcher.cs`, `FlowAttributionPipeline.cs`,
`IdleExpirySweeper.cs`, `RuntimeHeartbeat.cs`, `AdapterTransientRetryLogGate.cs`; `Logging/FlowLog.cs` and `Logging/RuntimeLog.cs`;
`tests/WinForward.Runtime.Flow.Tests/**`, `tests/WinForward.Performance.Tests/**`.

Traps:

- `FlowDispatcher.LogPacketStage(level, eventName, packet, params fields)` takes **both** a level
  and a name. Its `packet.completed` call passes `disposition`; the failure path passes
  `packet.failed`. `LogPacketStage` prepends an eight-field block (`packet`, `flow`, then the
  caller's fields, then `protocol`, `origin`, `source`, `destination`, `process`, `processPath`)
  — each resulting method needs that block explicitly, in the same field order.
- `_includeProcessPathInLogs` gates `processPath` to null. The parameter is `string?` and the
  expression is passed through unchanged; do not "simplify" it.
- `RuntimeHeartbeat` and `LayeredCaptureRunner` build their field lists as arrays and spread them
  (`[.. fields]`). Those become fixed parameter lists, and the **zero-delta-omitted, ordinal key
  order** contract of `runner.heartbeat` must survive.
- `RuntimeHeartbeat`'s heartbeat summary and `InterceptionHealthMonitor`'s degraded warning are
  the two places where `Information`/`Error` levels are load-bearing for the health story; keep
  them where they are.
- `tests/WinForward.Runtime.Flow.Tests/RuntimeLoggingTests.cs` is the one test file that used
  `ConsoleRuntimeLogger` with a `StringWriter`. Its formatter cases are deleted (the formatter is
  Microsoft's now), its threshold theory is re-expressed through `LoggerFactory` +
  `RecordingLoggerProvider`, and its two remaining cases (`DispatcherPropagates…`,
  `DispatcherIncludesProcessPathOnlyWhenPathPolicyRequiresIt`) assert on `RecordedEvent` fields
  instead of a rendered line.

## cli

Files: `src/WinForward.Cli/**` including `Program.cs`, `DurableCaptureBundle.cs`,
`UdpProxyComposer.cs`, `TcpRedirectComposer.cs`; `Logging/StartupLog.cs`;
`tests/WinForward.Integration.Tests/DurableCaptureBundleTests.cs`.

Traps:

- This is the composition root and therefore the **last** area. Until it lands, `Program.cs` must
  hold the MEL `ILoggerFactory` and the legacy `ConsoleRuntimeLogger` side by side, passing each
  to the components that still expect it.
- The factory must be disposed (`using`) so the console queue drains on shutdown.
- `adapters`, `validate` and the usage text write to **stdout**; the logger must never join that
  stream, which is what `LogToStandardErrorThreshold` guarantees.
- `foreach (var error in scopeErrors) logger.Error(error)` and the two
  `Console.Error.WriteLine` config-diagnostic paths are dynamic text: they become a
  single-placeholder `[LoggerMessage]` method.
- Configuration diagnostics printed outside the logger (`Console.Error.WriteLine`) are not part of
  this migration and stay as they are.
