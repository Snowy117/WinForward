# PRD — Adopt Microsoft.Extensions.Logging with source-generated log messages

## Goal

Replace the hand-rolled `IRuntimeLogger` / `ConsoleRuntimeLogger` stack with
`Microsoft.Extensions.Logging`. Every log site becomes a `[LoggerMessage]` source-generated
method, so the `IsEnabled` check the codebase writes by hand today moves into generated code.
The runtime regains missing `info`-level lifecycle output, and the console sink keeps a
single-line, timestamped, stderr shape so an operator can still grep one line per event.

## Context

- `src/WinForward.Runtime/RuntimeLogging.cs` owns `IRuntimeLogger`, `NullRuntimeLogger`,
  `ConsoleRuntimeLogger` and the `RuntimeLogField` record struct.
- `RuntimeLogLevel` (`src/WinForward.Configuration/ConfigurationModels.cs:127`) is the project's
  own level enum, parsed from `config.json`'s `logLevel` and stored on `ValidatedConfiguration`.
- 108 log call sites exist under `src/`; 50 of them are structured `Event(level, name, fields)`
  calls and only **6** are `info`.
- 32 sites hand-write an `IsEnabled` guard, several of them redundantly in front of a helper
  (`UdpProxyLogging.LogTrace`) that already checks the same level.
- `src/WinForward.Cli` publishes with `PublishAot=true`, `PublishSingleFile=true` and
  `EnableAotAnalyzer`/`EnableTrimAnalyzer` under `TreatWarningsAsErrors`.
- ~46 test files and 8 benchmark files construct the logger or assert on captured output.

## Requirements

**R1 — MEL is the logging abstraction.** `Microsoft.Extensions.Logging` (pinned centrally) is
referenced by the projects that log. `ILogger`/`ILoggerFactory` replace `IRuntimeLogger`;
`LogLevel` replaces `RuntimeLogLevel`; `NullLogger`/`NullLoggerFactory` replace
`NullRuntimeLogger`. `IRuntimeLogger`, `NullRuntimeLogger`, `ConsoleRuntimeLogger`,
`RuntimeLogField` and `RuntimeLogLevel` are deleted, not kept as compatibility shims.

**R2 — `config.json` vocabulary is unchanged.** `logLevel` still accepts exactly `error`, `warn`,
`info`, `debug`, `trace`, case-insensitively with surrounding whitespace ignored, defaulting to
`info`. `critical` and `none` stay rejected with the existing `logLevel` diagnostic. Threshold
ordering keeps MEL's `Trace < Debug < Information < Warning < Error`; the configured threshold
includes itself and every more-severe level.

**R3 — no hand-written level guards at call sites.** Every log statement is a `[LoggerMessage]`
method. A call site never writes `IsEnabled`. The disabled path allocates nothing, because the
generated method checks the level before constructing its state.

**R4 — structured identity survives.** Every event that carries an event name today keeps it as
`LoggerMessageAttribute.EventName`, and every field key today keeps its exact camelCase spelling
as the template placeholder name. So `EventId.Name` and the structured key/value state remain
what the stability census, the throttle/counter vocabulary, the health monitor keys and the
existing test assertions already read.

**R5 — messages become readable English sentences.** The message template is no longer
`"event.name key={value}"`. It reads as an operator sentence that embeds the placeholders, e.g.
`"UDP session created for {source} -> {destination} via {target} ({udpTransport}), association {udpAssociation}."`

**R6 — the console shape is MEL's, and the operator picks which one.** `Microsoft.Extensions.Logging.Console`'s default
`SimpleConsole` formatter produces the lines; the project owns no formatter. `AddConsole` is
configured only so that runtime logging goes to **stderr** at every level (`WinForward adapters`
writes a TSV table on stdout and `validate` writes its confirmation there; log lines must never
join that stream) and so the default formatter emits a local wall-clock timestamp
(`yyyy-MM-dd HH:mm:ss.fff`) for correlation with external events. `SingleLine` stays at MEL's
default `false`, so exceptions keep their stack traces.

**R7 — more `info`, less noise.** `info` gains the operational milestones an operator needs to
read a session end to end (startup configuration summary, capture scope, listener/target
bring-up, shutdown) while per-packet and per-session detail stays `debug`/`trace`. Redundant
nested guards and duplicated window-summary lines are removed.

**R8 — tests and benchmarks migrate with the product.** No test keeps asserting the old
`event.name key=value` line shape. The recording logger used by tests keeps exposing event name,
level and structured fields so the existing diagnostic assertions stay meaningful.

**R9 — documentation follows.** `.trellis/spec/backend/logging-guidelines.md` is rewritten around
the new contract, and the README's `logLevel` section is updated.

## Acceptance Criteria

- [ ] `rg 'RuntimeLogLevel|IRuntimeLogger|RuntimeLogField|ConsoleRuntimeLogger'` over `src/`,
      `tests/` and `benchmarks/` returns nothing.
- [ ] `rg 'IsEnabled'` over `src/**/*.cs` returns no call site that guards a **log statement**. One
      deliberate exception survives and must not be "fixed": `FlowDispatcher`'s warm-path bypass
      selects the trace-only dispatch path with `_logger.IsEnabled(LogLevel.Trace)`. That is a
      dispatch decision, not a log guard — removing it would force every packet through the slow
      path and break the allocation gates.
- [ ] Every structured event name that exists today still appears as an `EventName` literal
      (`research/event-name-anchor.txt`, 71 names).
- [ ] Runtime log lines appear on **stderr** only — never on stdout, which carries the `adapters`
      TSV and the `validate` confirmation — in whichever shape `logFormat` resolves to
      (`simple` or `json`). Every record's first line starts with the local wall-clock timestamp
      **prefixed by its UTC offset**, i.e. `+08:00 2026-10-06 11:24:41.570 …`, and the shape
      matches the destination: `json` when stderr is redirected, `simple` on an interactive
      terminal, unless the operator forces one.
- [ ] An exception passed to a log method renders with its stack trace.
- [ ] stdout output (`adapters` TSV, `validate` confirmation, usage text) is unchanged.
- [ ] `info` covers: resolved configuration summary, adapter/capture scope, each proxy
      listener/target brought up, interception start, shutdown request, clean stop.
- [ ] `dotnet build WinForward.slnx -c Release` is zero-warning.
- [ ] `dotnet test WinForward.slnx -c Release` is green.
- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` exits 0
      with empty output.
- [ ] `jb inspectcode -f=Xml -e=HINT` reports zero `<Issue>` entries.

## Constraints

- `PublishAot` + `EnableAotAnalyzer` + `EnableTrimAnalyzer` + `TreatWarningsAsErrors` must stay
  clean: no new trim/AOT warning from the logging dependency or from generated code.
- The hot path stays allocation-free on the disabled path and adds no allocation to the enabled
  debug/trace path beyond the message rendering the sink already performs.
- Logging remains observational: it never participates in packet disposition, fail-closed,
  relay, or shutdown decisions.
- The privacy rules are unchanged: never log SOCKS5 credentials, authentication frames, raw
  packet bytes, payloads, relay buffers, or raw configuration text; full process paths only when
  a path-based process selector is configured.

## Out of scope

- No file sink, no JSON sink, no OpenTelemetry exporter, no log rotation.
- No structured-text (key=value) rendering in the console sink; the structured state stays
  available to any future `ILoggerProvider`.
- Exactly one new configuration knob, `logFormat` (`auto` | `simple` | `json`, default `auto`),
  approved by the owner during implementation. `logLevel` keeps its five values and its single
  global threshold; the formatter's other options (timestamp format, single line, colour) stay
  compiled in rather than exposed.
- No change to `RuntimeLogThrottle`, `RuntimeCounters`, `RuntimeHeartbeat` cadence or the
  health-monitor thresholds beyond what the message migration forces.
