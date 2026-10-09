# Logging Guidelines

## Overview

Runtime logging uses `Microsoft.Extensions.Logging`. Every log statement is a `[LoggerMessage]`
source-generated method, so the level check lives in generated code and a call site hand-writes
`IsEnabled` only in the two exceptions the rules below name. The console sink is
`Microsoft.Extensions.Logging.Console`; the project registers no formatter and owns no logger
interface. Its configuration is the standard `Logging` section of the merged configuration — the
operator's `appsettings.json` beside the executable, then `--config` — and the project adds no
logging key of its own. Read this before adding a log statement or a `Logging` key.

Logging is observational: it must never participate in packet disposition, fail-closed, relay, or
shutdown decisions.

## Composition

- Build the one `ILoggerFactory` in `RuntimeLogging.CreateLoggerFactory`
  (`src/WinForward.Runtime/Logging/RuntimeLogging.cs`): `AddConfiguration(<Logging section>)`, then
  `AddConsole()`, then the `PostConfigure` actions. `IPostConfigureOptions<T>` runs after every
  `IConfigureOptions<T>`, so a project default applies only where the operator was silent and never
  depends on registration order.
- Force `LogToStandardErrorThreshold = LogLevel.Trace` unconditionally: the destination is an
  invariant rather than a setting. `WinForward adapters` writes a TSV table and `validate` writes
  its source list and confirmation on stdout, so no configuration value may move a record there.
- Supply the remaining defaults only where the configuration is silent:
  `PostConfigure<ConsoleLoggerOptions>` supplies the formatter name,
  `PostConfigure<SimpleConsoleFormatterOptions>` and `PostConfigure<JsonConsoleFormatterOptions>`
  supply the timestamp formats and the JSON encoder.
- Run `RuntimeLogging.TryValidate` as a pre-flight for both `run` and `validate`, before the factory
  is built. It reports the two values MEL leaves undiagnosed — an unparseable `LogLevel` and an
  unregistered `FormatterName` — as `ConfigDiagnostic`s, which the CLI prints to stderr with exit
  code 1.
- Build in `validate` the factory a run would build, so a formatter option MEL's binder throws on
  surfaces as that command's error rather than as an unhandled exception mid-run.
- Hold the factory in a `using` in the CLI: MEL's console provider writes through a background
  queue and drains it on dispose.
- Give each component a pre-categorised `ILogger` from the composition root, created with
  `loggerFactory.CreateLogger<Owner>()`. The category is the fully-qualified name of the type that
  owns the logger — MEL's documented convention, and what makes `Logging:LogLevel:<category>`
  overrides worth configuring.
- Use `CreateLogger(typeof(Program).FullName!)` for the CLI's own startup lines, because `Program`
  is static and cannot be a type argument.
- Keep one logger per module owner, not per class: the UDP and TCP coordinators take an `ILogger`
  through their options record (`UdpProxyOptions.Logger`, `TcpRedirectOptions.Logger`) and
  everything they construct shares that category. Giving every class its own category would mean
  threading an `ILoggerFactory` through those records instead — deliberately deferred, not
  forgotten.

## Log Levels

- Use MEL's vocabulary: `Logging:LogLevel:Default` and `Logging:LogLevel:<fully-qualified
  category>` take `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`,
  matched case-insensitively; an omitted `Default` means MEL's own `Information`.
- Remember the ordering: `Trace < Debug < Information < Warning < Error < Critical`. A threshold
  includes itself and every more-severe level; `None` is not a severity in that chain, it disables
  the category it is set on. Filtering is MEL's `LoggerFilterOptions` job, not the call site's.
- Emit `Trace` through `Error`; `Critical` is the framework's level above `Error`.
- Write level names in full. `info`, `warn`, `INFO`, and `Verbose` are not level names in any
  position: MEL's filter parsing throws `InvalidOperationException` ("Configuration value 'info' is
  not supported.") instead of defaulting.
- Let `RuntimeLogging.TryValidate` pre-check every `Logging:LogLevel:<category>` value: an
  unparseable one becomes a `Logging.LogLevel.<category>` diagnostic, so `validate` and `run` fail
  with exit code 1 and an actionable message instead of a framework exception. The pre-check is the
  safety net for a carried-over `"logLevel": "info"`.
- Reserve `Information` for concise operational lifecycle output, `Debug` for logical flow and proxy
  lifecycle, and `Trace` for per-packet processing stages and terminal outcomes.
- Never raise a level to make a line more visible.

## Console Format

- Choose the formatter with `Logging:Console:FormatterName`, which names the three formatters this
  build registers: `simple`, `json`, and `systemd`. It is the whole surface — the project has no
  format key of its own — and configuring nothing selects the auto rule: `json` when stderr is
  redirected, `simple` when stderr is an interactive terminal.
- Treat an unregistered name as a `Logging.Console.FormatterName` diagnostic from
  `RuntimeLogging.TryValidate`, never as a fallback: given a name it cannot resolve, MEL selects
  `simple` in silence and reports nothing, which reads to an operator as their formatter setting
  being ignored. The accepted list is MEL's three stock names, so a build that adds a formatter must
  extend `RuntimeLogging.AcceptedFormatterNames`.
- Expect `simple` to write the abbreviated level (`trce`, `dbug`, `info`, `warn`, `fail`), the
  category, the numeric `EventId`, and the message, with an exception's stack trace on continuation
  lines.
- Expect `json` to write one JSON object per line carrying `Timestamp`, `EventId`, `LogLevel`,
  `Category`, `Message`, `Exception`, and `State` (the template's fields plus `{OriginalFormat}`).
  `FormatterOptions:JsonWriterOptions:Indented` deliberately breaks that one-record-per-line shape.
- Keep every default in code, in `RuntimeLogging.CreateLoggerFactory`: the auto rule, a local
  wall-clock timestamp carrying its UTC offset (`zzz yyyy-MM-dd HH:mm:ss.fff`, with a trailing space
  in `simple`), `SingleLine` at MEL's `false` so an exception keeps its stack trace, a relaxed JSON
  encoder (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, because MEL's default escapes every
  non-ASCII character), and the stderr destination.
- Apply that timestamp default to the two formatters whose records carry one: `systemd` renders
  journald's own shape, which has no timestamp unless `FormatterOptions:TimestampFormat` is set,
  because journald stamps the record itself.
- Treat `FormatterOptions` as the operator's surface — `TimestampFormat`, `SingleLine`,
  `ColorBehavior`, `IncludeScopes`, and `JsonWriterOptions:*` such as `Indented` — bound by the
  framework, with a code default applying only where the key is absent.
- Accept that MEL owns the line shape: a record may occupy several physical lines (`simple` writes a
  CR/LF-bearing value verbatim), and a console write failure is no longer swallowed by project code.
- Ship `src/WinForward.Cli/appsettings.example.json` as documentation, never as an implicit source —
  nothing loads it on its own, and an upgrade must not overwrite the operator's own
  `appsettings.json`. It shows the default posture — `LogLevel.Default: Information` and no
  `FormatterName` — and `AppSettingsExampleTests` keeps it in step with the constants it documents.

## Message Templates and Event Identity

- Write the message as a natural English sentence that embeds its placeholders, and do not restate
  the dotted event name in it: `tcp.redirect.unrelatedPeer` reads `"Accepted a peer that is not the
  redirect's expected client on listener {Listener}: expected {Expected}, actual {Actual}."`
- Declare `EventName = "area.event"` on every event a machine consumer reads — the stable dotted
  identifier it reads through `EventId.Name`. Renaming one is a breaking change: the stability
  census names a fixed list (`benchmarks/WinForward.Benchmarks/Stability/StabilityShared.cs`) and
  the diagnostic tests assert on these names.
- Take the method name as `EventId.Name` when a method declares no `EventName`; machine consumers
  ignore those events.
- Spell every placeholder exactly like the field key it carries: `{Flow}`, `{UdpAssociation}`,
  `{ProcessPath}`.
- Expect a null placeholder to render by parameter count: up to six parameters the generator uses
  MEL's `LogValues<T0..T5>`, which renders `(null)`, while more than six it synthesises its own
  state struct, which renders nothing at all. Most events here are over six parameters, so the
  common case is a silently empty slot; the structured state keeps the null either way (measured on
  MEL 10.0.12, task 10-06-mel-logging).
- Put a nullable placeholder where an empty slot still reads acceptably — at the end of a clause
  rather than mid-sentence, or behind its own label — and never rely on `(null)` appearing for a
  human reader.
- Keep one name to one level. `SYSLIB1025` rejects two `[LoggerMessage]` methods that share an
  `EventName` inside one class, and that is the framework stating a design position rather than an
  obstacle to route around: the level is part of an event's identity, which is how ETW and the
  Windows event log read an `EventId`. Two levels therefore mean two events with two names — never
  one name split across two partial classes.
- Split a name rather than share it, as `generation.startup-fault` did: the absorbed-and-retrying
  half kept the name at `Warning`, and the budget-exhausted fail-closed half became
  `generation.startup-fault.exhausted` at `Error`, so an existing filter still catches the frequent
  case and a prefix search catches both.
- Attach a real `Exception` only where the fault is genuinely unexpected. Routinely handled I/O
  failures (socket reset, timeout) keep their structured `{Error}` / `{SocketError}` /
  `{NativeError}` fields and no stack trace, so an expected fault never costs a stack trace in the
  log.
- Write a plain operator line as a `[LoggerMessage]` method too, with no placeholders when the text
  is constant; `ILogger.LogInformation` / `LogWarning` / `LogError` extension calls are not used in
  `src/`.
- Make every parameter appear in the message template: `SYSLIB1015` rejects a parameter that no
  placeholder references, so there is no such thing as a "structured-only" field. Two call shapes
  that cannot share one template are two events, not one event with a union of nullable fields.
- **Guard a log call with a call-site `IsEnabled` for exactly one purpose: skipping expensive
  argument construction on a timer-driven path.** The generated method's own level check keeps the
  disabled path free but cannot un-evaluate arguments the caller already built; guarding a *cheap*
  argument is not worth it and makes behaviour depend on the logging configuration, while guarding
  an expensive one is MEL's own idiom. `RuntimeHeartbeat.Emit` is the only site that qualifies: its
  tick is timer-driven rather than event-driven, so without the guard it would invoke the usage
  delegate and build the pool and delta blocks on every tick even at `logLevel=warn`. The guard
  covers only the heartbeat; the warn-level GC alarm in the same tick is independent of it. Two
  further sites build arguments the level may discard — `LayeredCaptureRunner`'s `DescribeWindows`
  and `UdpResponseReinjector`'s adapter-id join — and are **accepted as is**: both run at most once
  per occurrence of the event they describe, never more often than the record itself would have been
  written.
- **One exception to that rule.** `FlowDispatcher`'s warm-path bypass
  (`if (_logger.IsEnabled(LogLevel.Trace)) return DispatchSlowAsync(...)`) reads a level but is a
  dispatch decision rather than a log guard: with trace enabled every packet takes the observable
  slow path. Deleting it as a "leftover guard" would route the steady state through that path and
  break the allocation gates.

## What to Log

- Log lifecycle events, recoverable and fail-closed faults, policy/action decisions, classification,
  proxy setup/relay/teardown, reinjection, drops, and terminal packet outcomes.
- Let `Information` carry a run end to end: the resolved configuration summary, the capture scope,
  each target coming up, interception start, the heartbeat, the shutdown request, and the clean stop.
- Correlate packet diagnostics on the `Packet` sequence and flow diagnostics on the `Flow`
  generation; a TCP or UDP association generation is an additional field (`TcpAssociation`,
  `UdpAssociation`) where the record has one.
- Emit a `flow.created` record from both flow-creation paths (2026-10-05, task
  10-05-host-flow-created-logging): the inline dispatcher and the deferred attribution pipeline both
  call one shared builder, so the field set is identical. The deferred path can only log at claim
  time — the claim is the last step of delivery — so a host flow's record may follow the proxy legs
  it produced, and the flow's `Process` / `ProcessPath` / `Rule` fields stay absent until the
  worker's verdict lands.
- Treat an entry created with no `flow.created` record as a defect, not as a level to raise: before
  2026-10-05 a host flow whose entry the pipeline claimed was invisible at every level, which is
  exactly the "I configured a process rule and my own traffic disappeared from the log" report.
- Allowed metadata: transport and endpoints, adapter and process identity, rule index, proxy server
  name, stage, reason, and byte counts.

## What NOT to Log

- Never log SOCKS5 usernames or passwords, authentication frames, raw packet bytes or spans,
  payloads, relay buffers, or raw configuration text.
- Emit full process paths only when the validated policy contains a path-based process selector
  (`ConfigurationRules.AnyProcessSelectorIsAPath`, surfaced as `ValidatedConfiguration`
  `IncludeProcessPathInLogs`); process names stay permitted.

## Executable Contract

Applies to the `Logging` configuration section, the `ILoggerFactory` that
`RuntimeLogging.CreateLoggerFactory` builds from it, every `[LoggerMessage]` method, packet dispatch,
and the proxy coordinators. The level vocabulary and the formatter surface are MEL's, so no project
type owns them and `ValidatedConfiguration` carries no level or formatter field — its only
logging-shaped member is the derived `IncludeProcessPathInLogs` privacy flag. The loader merges its
sources at the JSON level, validates the merged `WinForward` object alone, and serves the merged
document from memory (`ConfigurationLoader.TryLoad`, `ConfigurationLayering.cs`), so the `Logging`
section reaches MEL unchanged and always belongs to the same document as `WinForward`.

### Validation and Error Matrix

| Condition | Required result |
| --- | --- |
| `Logging:LogLevel:Default` omitted | MEL's own `Information` |
| An accepted level, formatter, or formatter-option value | Bound by MEL and in effect; a code default applies only where the key is absent |
| A `LogLevel` value that is not a level name (`info`, `warn`, `Verbose`) | Fail with the `Logging.LogLevel.<category>` diagnostic and exit code 1 — never a silent fallback to `Information` |
| A `FormatterName` that is not a registered formatter | Fail with the `Logging.Console.FormatterName` diagnostic and exit code 1 — MEL alone would fall back to `simple` in silence |
| A formatter option MEL's binder cannot apply | Reported by `run`/`validate` through the factory build, not as an unhandled exception |
| An unknown key under `Logging` outside `LogLevel` | Ignored by MEL's binder; not a WinForward diagnostic |
| A `WinForward` key that is missing, unknown, wrongly cased, or wrongly typed — a quoted number where a number belongs included | Fail with the path diagnostic (`WinForward.Host.Rules[6].RemoteCidr[0]`-shaped) without echoing raw input |
| `Logging:Console:LogToStandardErrorThreshold` configured | Ignored: the post-configure action forces `Trace`, so no record reaches stdout |
| An entry below the threshold | The generated method returns before constructing state; no formatting, no output |
| A console write failure | Not swallowed by project code (MEL owns the sink) |
| A classification or dispatch failure | Emit trace `packet.failed` when possible, dispose the lease, and rethrow |
| A proxy or reinjection failure | Preserve the existing fail-closed behavior and log metadata/reason only |

### Required Tests

- Configuration tests (`ConfigurationLayeringTests`, `ConfigurationValidationTests`) cover the two
  layers, the PascalCase schema, the strict reader's unknown-member and wrong-casing rejections, and
  the `WinForward`-rooted diagnostic paths.
- `RuntimeLoggingTests` covers the level filter at `Default` and per category, an explicit
  `FormatterName`, the auto rule when it is absent, and a hostile configuration that sets
  `LogToStandardErrorThreshold` and still writes nothing to stdout.
- `RuntimeLoggingTests` covers both `RuntimeLogging.TryValidate` checks: every retired short token
  and every other unparseable level is reported per category, an unregistered `FormatterName` is
  reported with its key, and absent values are not errors.
- `AppSettingsExampleTests` keeps `appsettings.example.json` parsing under the strict validator, with
  `Logging` values that match the constants they document.
- `RuntimeLoggingTests` covers the threshold contract through `LoggerFactory` for every level: an
  entry at or above the threshold reaches the provider, an entry below it does not.
- Runtime tests (`RuntimeDiagnosticLoggingTests`, `UdpProxyLoggingTests`, `CaptureMilestoneLoggingTests`,
  `NdisPacketActionExecutorLoggingTests`) cover packet/flow correlation, terminal completion and
  failure, process-path privacy, proxy lifecycle metadata, and unchanged packet dispositions.
- Confirm by search that credentials, payloads, raw frames, and relay buffers never reach a logging
  call.

## Runtime Diagnostics Conventions

The 2026-09-17 outage post-mortem found every decisive failure path silent (Trace-only or swallowed)
while harmless warns flooded; these conventions gate every new failure site (task
09-17-adapter-staleness-logging).

- Throttle a recurring failure line with one `RuntimeLogThrottle`
  (`src/WinForward.Runtime/RuntimeLogThrottle.cs`) per event site: the first occurrence always
  emits, later ones collapse into its window, and the check is **check-first** (`ShouldEmit()`
  before any field construction), so a suppressed occurrence costs nothing. With `[LoggerMessage]`
  the level check is already free, so a throttled site costs only that one call.
- Aggregate failure and decision counts in `RuntimeCounters`
  (`src/WinForward.Runtime/RuntimeCounters.cs`): observation-only `Interlocked` counters, zero-alloc
  once the key's box exists, with a `Shared` instance and a lock-free `Snapshot()`. Take key names
  from its constants (`RelaySetupFailed`, `UdpOriginUnresolved`, `UdpFailClosedDrop`,
  `FlowCapacityBlock`, `AttributionMiss`, `PassReinjectFailed`, …) — never retype a key literal —
  because the health monitor's thresholds and the heartbeat's deltas both read them. Counters never
  influence behavior.
- Emit the periodic `runner.heartbeat` (`RuntimeHeartbeat`, `Information`, 60 s default)
  aggregating usage (flows and flow capacity, TCP/UDP sessions against capacity, pumps
  running/degraded), health state (`Degraded`, `ConsecutiveForced`, `CooldownSeconds`), the GC
  posture, and the counter movement since the previous tick.
- Carry that movement in one `Deltas` field, not one field per counter: the key set is open
  (`RuntimeCounters.Increment` accepts any key, and the native pools register
  `pool.<name>.rented|returned` at runtime) and `SYSLIB1015` requires every `[LoggerMessage]`
  parameter to appear in its message template, so a runtime key set cannot be a set of placeholders.
  The field carries `key=delta` tokens in ordinal key order with zero deltas omitted and is null
  when nothing moved; the same constraint shaped `runner.forcedRefresh`'s single `Windows` field.
  Parse that token list rather than reading a field per counter.
- Never let diagnostics take the runtime down: a faulty usage provider warns
  (`HeartbeatSummaryFailed`) and retries on the next tick.
- Gate recurring state-shaped warns on change (`DurableCaptureBundle.WarnNoMacAdaptersOnChange`):
  `udp.targets.noMac.adapters` emits on first occurrence and only when its member SET changes, and
  `udp.targets.noMac` on first occurrence and only when its host changes — never on every refresh.
  A full recovery (all MACs valid) or an empty scope resets the memory so the next occurrence
  re-emits.
- Carry the distinguisher on every failure event — never log a fixed-text failure line again.
  `tcp.redirect.relaySetupFailed` carries `Error`, `SocketError`, `NativeError`, `Upstream`, and
  `Attempts`; `tcp.redirect.unrelatedPeer` carries `Listener`, `Expected`, and `Actual`;
  `udp.reinject.*` carry the flow key and the map summary. A failure log without its distinguisher
  is a defect.
- Apply the incident triad as the acceptance gate for new diagnostics work (2026-09-17 replay): for
  any incident the log must let an operator read out which leg failed, which signal fired, and what
  recovery ran.
