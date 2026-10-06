# Logging Guidelines

## Overview

Runtime logging uses `Microsoft.Extensions.Logging`. Every log statement is a `[LoggerMessage]`
source-generated method, so the level check lives in generated code and no call site hand-writes
`IsEnabled`. The console sink is `Microsoft.Extensions.Logging.Console`; the project owns no
formatter and no logger interface. Its configuration is the standard `Logging` section of the
operator's `appsettings.json`, and the project adds no logging key of its own.

Logging is observational and must never participate in packet disposition, fail-closed, relay, or
shutdown decisions.

## Composition

- `src/WinForward.Runtime/Logging/RuntimeLogging.cs` builds the one `ILoggerFactory` from the
  `Logging` section the composition root hands it: `AddConfiguration(section)` then `AddConsole()`,
  plus **one** `PostConfigure<ConsoleLoggerOptions>` action that owns every project default. It
  forces `LogToStandardErrorThreshold = LogLevel.Trace` unconditionally and supplies the timestamp
  format and the formatter name **only where the configuration is silent**.
  `IPostConfigureOptions<T>` runs after every `IConfigureOptions<T>`, so the invariant and the
  defaults are order-independent and an explicit operator value still wins.
- The same type owns `RuntimeLogging.TryValidate`, the pre-flight **both `run` and `validate`** run
  over the merged `Logging` section before the factory is built. It reports the two values MEL
  leaves undiagnosed — an unparseable `LogLevel` entry and an unregistered `FormatterName` — as
  `ConfigDiagnostic`s, which the CLI prints to stderr with exit code 1. `validate` also builds the
  factory a run would build, so a malformed formatter option (a value MEL's binder throws on)
  surfaces as that command's error rather than as an unhandled exception mid-run.
- Runtime logging goes to **stderr only**, and that destination is an **invariant rather than a
  setting**. `WinForward adapters` writes a TSV table on stdout, and `validate` writes its source
  list and its confirmation there; a log line must never share that stream. No configuration value
  can move a record to stdout — the post-configure action above fixes the threshold after the
  framework has read the file.
- The CLI holds the factory in a `using`, because `ConsoleLogger` writes through a background
  queue that `Dispose` drains.
- Components receive a pre-categorised `ILogger` from the composition root, and the category is
  the **fully-qualified name of the type that owns the logger**, which is MEL's documented
  convention (OTel phrases it as dot-separated UpperCamelCase, usually satisfied by the FQCN) and
  what makes `Logging:LogLevel:<category>` overrides worth configuring. Create it with
  `loggerFactory.CreateLogger<Owner>()`; the CLI's own startup lines use
  `CreateLogger(typeof(Program).FullName!)` because `Program` is static and cannot be a type
  argument. One logger per **module owner**, not per class: a subsystem that receives an `ILogger`
  through its options record (the UDP and TCP coordinators and everything they construct) shares
  that owner's category. Giving every class its own category means threading an `ILoggerFactory`
  through those options records instead of an `ILogger` — deliberately deferred, not forgotten.

## Log Levels

The vocabulary is MEL's and lives in the standard section: `Logging:LogLevel:Default` and
`Logging:LogLevel:<fully-qualified category>` take `Trace`, `Debug`, `Information`, `Warning`,
`Error`, `Critical`, or `None`. The names are matched case-insensitively, so an operator may write
either casing; the canonical spelling is the one listed, and an omitted `Default` means MEL's own
`Information`. Threshold ordering is `Trace < Debug < Information < Warning < Error < Critical`, and
the configured threshold includes itself and every more-severe level; `None` is not a severity in
that chain, it disables the category it is set on. The project emits `Trace` through `Error`;
`Critical` is the framework's level above `Error`.

**The names are full names, and the retired short tokens are not level names.** `info`, `warn`,
`INFO`, and `Verbose` are not accepted in any position: MEL's own filter parsing throws
`InvalidOperationException` ("Configuration value 'info' is not supported.") rather than defaulting,
so the mistake would surface as a framework exception instead of a diagnostic.
`RuntimeLogging.TryValidate` therefore pre-checks every `Logging:LogLevel:<category>` value and
reports an unparseable one as a `Logging.LogLevel.<category>` diagnostic, so `validate` and `run`
fail with exit code 1 and an actionable message instead. The pre-check is the migration's safety net,
because `"logLevel": "info"` is the value most likely to be carried across.

`Information` is concise operational lifecycle output; `Debug` records logical flow and proxy
lifecycle; `Trace` records per-packet processing stages and terminal outcomes. Never raise a level
to make a line more visible.

## Console Format

Choosing a formatter is `Logging:Console:FormatterName`, which names the formatters MEL registers:
`simple`, `json`, and `systemd`. The project has no format key of its own: the old `logFormat` key,
its `LogFormat` enumeration and the resolver that mapped them were deleted with the move to the
standard section, so `FormatterName` plus the auto rule below is the whole surface. When the operator
configures nothing, the post-configure action supplies the auto rule: `json` when stderr is
redirected, and `simple` when stderr is an interactive terminal.

An unregistered name is a `Logging.Console.FormatterName` diagnostic from the same
`RuntimeLogging.TryValidate` pre-check, not a fallback: given a name it cannot resolve, MEL selects
`simple` in silence and reports nothing, which reads to an operator as their formatter setting being
ignored. The accepted list is MEL's three stock names, so a formatter this project does not register
is rejected rather than selected — a build that adds one must extend that list with it.

- `simple` abbreviates the level (`trce`, `dbug`, `info`, `warn`, `fail`), then the category, the
  numeric `EventId`, and the message; an exception's stack trace follows on continuation lines.
- `json` writes one JSON object per line: `Timestamp`, `EventId`, `LogLevel`, `Category`,
  `Message`, `Exception`, and `State` (the message template's fields plus `{OriginalFormat}`).
  `FormatterOptions:JsonWriterOptions:Indented` breaks that one-record-per-line shape deliberately.

Every default lives in code: the auto rule above, a local wall-clock timestamp carrying its UTC
offset (`zzz yyyy-MM-dd HH:mm:ss.fff`, with a trailing space in `simple`), `SingleLine` at MEL's
`false` so an exception keeps its stack trace, and the stderr destination from Composition. The
timestamp default reaches the two formatters whose records carry one; `systemd` renders journald's
own shape, which has no timestamp unless `FormatterOptions:TimestampFormat` is set, because journald
stamps the record itself. The
shipped `appsettings.example.json` documents those defaults and is **never loaded** — an upgrade
must not be able to overwrite the operator's own `appsettings.json` — and a test keeps the example's
`Logging` values in sync with the code constants they document.

`FormatterOptions` is the operator's surface here: `TimestampFormat` (the code default above applies
when it is absent and the selected formatter has one), `SingleLine`, `ColorBehavior`,
`IncludeScopes`, and `JsonWriterOptions:*` such as `Indented`. They are MEL's own options, bound by
the framework.

**Consequences of owning no formatter.** A record is no longer guaranteed to be one physical line,
a value containing CR/LF is no longer escaped, and a console write failure is no longer swallowed
by project code. Those guarantees died with `ConsoleRuntimeLogger`.

## Message Templates and Event Identity

- The message is a natural English sentence that embeds its placeholders. It does not repeat the
  event name: `"UDP session created for {source} -> {destination} via {target} ({udpTransport}), association {udpAssociation}."`
- Every structured event declares `EventName = "area.event"`, the stable dotted identifier machine
  consumers read through `EventId.Name`. Renaming one is a breaking change: the stability census in
  `benchmarks/WinForward.Benchmarks/Stability/StabilityShared.cs` looks up a fixed list, and the
  diagnostic tests assert on these names.
- Every placeholder is spelled exactly like the field key it carries (`{flow}`, `{udpAssociation}`,
  `{processPath}`). A null value always stays null in the structured state, but **how it renders in
  the message depends on the parameter count**: up to six parameters the generator uses MEL's
  `LogValues<T0..T5>`, which renders `(null)`, while more than six it synthesises its own state
  struct, which renders nothing at all. Most events here are over six parameters, so the common
  case is a silently empty slot. **Put a nullable placeholder where an empty slot still reads
  acceptably** — at the end of a clause rather than mid-sentence, or behind its own label — and
  never rely on `(null)` appearing for a human reader. This was measured, not assumed; see the
  probe results recorded in the task's `design.md` §9.3.
- A method without `EventName` takes its method name as `EventId.Name`; machine consumers ignore it.
- **One name, one level.** `SYSLIB1025` rejects two `[LoggerMessage]` methods that share an
  `EventName` inside one class, and that is the framework stating a design position rather than an
  obstacle to route around: the level is part of an event's identity, which is exactly how ETW and
  the Windows event log read an `EventId`. Two levels therefore mean two events with two names —
  never one name split across two partial classes. The one occurrence in this codebase was
  `generation.startup-fault`: the absorbed-and-retrying half kept the name at `Warning`, and the
  budget-exhausted fail-closed half became `generation.startup-fault.exhausted` at `Error`, so an
  existing filter still catches the frequent case and a prefix search catches both.
- Attach a real `Exception` only where the fault is genuinely unexpected. Routinely handled I/O
  failures (socket reset, timeout) keep their structured `error` / `socketError` / `nativeError`
  fields and no stack trace, so an expected fault never costs a stack trace in the log.
- A plain operator line with no structured fields is still a `[LoggerMessage]` method, with no
  placeholders when the text is constant. `ILogger.LogInformation` / `LogWarning` / `LogError`
  extension calls are not used in `src/`.
- **Every parameter must appear in the message template.** `SYSLIB1015` rejects a parameter that no
  placeholder references, so there is no such thing as a "structured-only" field. Two call shapes
  that cannot share one template are two events, not one event with a union of nullable fields.
- **A call-site `IsEnabled` is legitimate for exactly one purpose: skipping expensive argument
  construction on a timer-driven path.** The generated method's own level check keeps the disabled
  path free, but it cannot un-evaluate the arguments the caller already built. Guarding a *cheap*
  argument is not worth it (and makes behaviour depend on the logging configuration). Guarding an
  expensive one is MEL's own idiom. `RuntimeHeartbeat.Emit` is the only site that qualifies: its
  tick is driven by a timer rather than by the event, so without the guard it would invoke the
  usage delegate and build the pool and delta blocks on every tick even at `logLevel=warn`. The
  guard covers only the heartbeat; the warn-level GC alarm in the same tick is independent of it.
  Two further sites build arguments the level may discard — `LayeredCaptureRunner`'s
  `DescribeWindows` and `UdpResponseReinjector`'s adapter-id join — and are **accepted as is**,
  because both run at most once per occurrence of the event they describe, never more often than
  the record itself would have been written.
- **One exception to the no-hand-written-`IsEnabled` rule.** `FlowDispatcher`'s warm-path bypass
  (`if (_logger.IsEnabled(LogLevel.Trace)) return DispatchSlowAsync(...)`) reads a level but is a
  dispatch decision rather than a log guard: with trace enabled every packet takes the observable
  slow path. Deleting it as a "leftover guard" would route the steady state through that path and
  break the allocation gates.

## What to Log

Log lifecycle events, recoverable and fail-closed faults, policy/action decisions, classification,
proxy setup/relay/teardown, reinjection, drops, and terminal packet outcomes. Metadata may include
transport and endpoints, adapter/process identity, rule index, proxy server name, stage, reason,
and byte counts.

`info` must let an operator read a run end to end: the resolved configuration summary, the capture
scope, each TCP listener and UDP target coming up, interception start, the heartbeat, shutdown
request, and clean stop.

## What NOT to Log

Never log SOCKS5 usernames or passwords, authentication frames, raw packet bytes, payloads, relay
buffers, or raw configuration text. Full process paths are emitted only when validated policy
configuration contains a path-based process selector; process names remain permitted.

## Executable Contract

### Scope / Signatures

This contract applies to the `Logging` configuration section, the `ILoggerFactory` built by
`RuntimeLogging.CreateLoggerFactory` from that section, every `[LoggerMessage]` method, packet
dispatch, and proxy coordinators. The level vocabulary is MEL's, so no project type owns it and
`ValidatedConfiguration` carries no logging fields; the configuration loader merges the sources at
the JSON level and validates the merged `WinForward` object alone, while the `Logging` section
reaches MEL unchanged. The process-path privacy flag is still derived from the validated policy
rules (`ConfigurationRules.AnyProcessSelectorIsAPath`).

### Boundary Contracts

- Accepted levels are MEL's full names `Trace`, `Debug`, `Information`, `Warning`, `Error`,
  `Critical`, and `None`, parsed case-insensitively; an omitted `Logging:LogLevel:Default` means
  `Information`. The retired short tokens (`info`, `warn`, …) are not level names, and
  `RuntimeLogging.TryValidate` rejects them with a diagnostic rather than letting MEL throw.
- The console formatter is `Logging:Console:FormatterName`; absent means the auto rule from Console
  Format. Only MEL's three registered names are accepted (`simple`, `json`, `systemd`); any other
  name is a diagnostic, because MEL would otherwise fall back to `simple` in silence.
  `Logging:Console:LogToStandardErrorThreshold` is not part of the surface: the post-configure action
  forces it, so configuring it has no effect.
- Threshold ordering is `Trace < Debug < Information < Warning < Error < Critical`; a threshold
  includes itself and all more-severe levels, and `None` sits outside that chain as the value that
  disables the category it is set on. Filtering is `LoggerFilterOptions`' job; the disabled path is
  short-circuited by the generated method before it builds any state.
- Records use `yyyy-MM-dd HH:mm:ss.fff` with a `zzz` UTC offset on stderr.
- Packet diagnostics use the runtime `packet` sequence; flow diagnostics use the `FlowTable`
  generation. TCP/UDP association generations may be additional fields.
- **Both flow-creation paths owe a `flow.created` record** (2026-10-05, task
  10-05-host-flow-created-logging). A flow-table entry is created either by the inline dispatcher
  or by the deferred attribution pipeline, and each one emits the event with the same field set
  from one shared builder. The deferred path can only log at claim time — the claim is the last
  step of delivery — so a host flow's record may follow the proxy legs it produced, and the flow's
  `process`/`processPath`/`rule` fields are still absent until the worker's verdict lands. An
  entry created with no record at all is a defect, not a log level to raise: before 2026-10-05 a
  host flow whose entry the pipeline claimed was invisible at every level, which is exactly the
  "I configured a process rule and my own traffic disappeared from the log" report.
- Allowed metadata includes endpoints, adapters, process identity, rule/action, stage, reason, and
  byte counts. Full process paths require a path-based process selector.

### Validation and Error Matrix

| Condition | Required result |
| --- | --- |
| `Logging:LogLevel:Default` omitted | MEL's own `Information` |
| An accepted level, formatter, or formatter-option value | Bound by MEL and in effect; the code default applies only where the key is absent |
| A `LogLevel` value that is not a level name (`info`, `warn`, `Verbose`) | Fail with the `Logging.LogLevel.<category>` diagnostic and exit code 1 — never a silent fallback to `Information` |
| A `FormatterName` that is not a registered formatter | Fail with the `Logging.Console.FormatterName` diagnostic and exit code 1 — MEL alone would fall back to `simple` in silence |
| A formatter option MEL's binder cannot apply | Reported by `run`/`validate` through the factory build, not as an unhandled exception |
| An unknown key under `Logging` (any other name) | Ignored by the framework; not a WinForward diagnostic |
| A `WinForward` key that is missing, unknown, wrongly cased, or wrongly typed — a quoted number where a number belongs included | Fail with the path diagnostic (`WinForward.Host.Rules[6].RemoteCidr[0]`-shaped) without echoing raw input |
| `Logging:Console:LogToStandardErrorThreshold` configured | Ignored: the post-configure action forces `Trace`, so no record reaches stdout |
| Entry below the threshold | The generated method returns before constructing state; no formatting, no output |
| Console writer failure | Not swallowed by project code (MEL owns the sink) |
| Classification/dispatch failure | Emit trace `packet.failed` when possible, dispose the lease, and rethrow |
| Proxy or reinjection failure | Preserve existing fail-closed behavior and log metadata/reason only |

### Good / Base / Bad Cases

- Good: `"Logging": { "LogLevel": { "Default": "Trace" }, "Console": { "FormatterName": "json" } }`
  produces one JSON object per record whose `Message` reads
  `UDP session created for 10.0.0.5:5353 -> 8.8.8.8:53 via main (socks5/uot), association 7.`
  and whose `State` carries `source`, `destination`, `target`, `udpTransport`, `udpAssociation`.
- Base: an omitted `Logging` section preserves concise lifecycle output on a terminal and
  structured JSON in a redirected log, with the code's timestamp default and no per-packet work.
- Bad: passing a SOCKS5 password, packet span, UDP payload, authentication frame, relay buffer, or
  raw configuration JSON to a `[LoggerMessage]` method.

### Required Tests

- Configuration tests cover the two layers, the PascalCase schema, the three strictness properties
  (unknown, wrongly cased, wrongly typed), and the new `WinForward`-rooted diagnostic paths.
- Logging-configuration tests cover the level filter at `Default` and per category, an explicit
  `FormatterName`, and the auto rule when it is absent — including a hostile configuration that sets
  `LogToStandardErrorThreshold` and still writes nothing to stdout.
- `RuntimeLogging.TryValidate` tests cover both of its checks: every retired short token and every
  other unparseable level is reported per category, an unregistered `FormatterName` is reported with
  its key, and absent values are not errors (absence selects the defaults).
- A test keeps `appsettings.example.json` in sync with the code defaults: it parses under the strict
  validator, and its `Logging` values match the constants they document.
- Logger tests cover the threshold contract through `LoggerFactory` for every level: an entry at
  or above the threshold reaches the provider, an entry below it does not.
- Runtime tests cover packet/flow correlation, terminal completion/failure, process-path privacy,
  proxy lifecycle metadata, and unchanged packet dispositions.
- Static review/search confirms credentials, payloads, raw frames, and relay buffers never reach
  logging calls.

### Wrong vs Correct

Wrong:

```csharp
if (_logger.IsEnabled(LogLevel.Trace)) UdpLog.UdpPacketReceived(_logger, packet, payload.Span);
```

Correct:

```csharp
UdpLog.UdpPacketReceived(_logger, flowGeneration, associationGeneration, source, destination, payloadLength);
```

The generated method performs the level check; the call site passes only typed values.

## Runtime Diagnostics Conventions (wired 2026-09-17, task 09-17-adapter-staleness-logging)

> Root cause of the 2026-09-17 outage post-mortem: every decisive failure path was silent (Trace-only or swallowed) while harmless warns flooded. These conventions gate every new failure site.

- **`RuntimeLogThrottle`** (src/WinForward.Runtime/RuntimeLogThrottle.cs): per-event-site key + window throttle. First occurrence always emits; suppressed occurrences cost nothing — checks are **check-first** (`ShouldEmit()` before any field construction). With `[LoggerMessage]` the level check is free, so a throttled site is now only a `ShouldEmit()` check.
- **`RuntimeCounters`** (src/WinForward.Runtime/RuntimeCounters.cs): observation-only `Interlocked` long counters (zero-alloc after key creation), shared instance + `Snapshot()`. The stable key vocabulary lives as constants (`relaySetupFailed`, `udpOriginUnresolved`, `udpFailClosedDrop`, `flowCapacityBlock`, `attributionMiss`, `passReinjectFailed`, …) and is shared by the health monitor thresholds and heartbeat deltas — never retype a key literal. Counters never influence behavior.
- **Heartbeat**: `runner.heartbeat` (info, 60 s default, `RuntimeHeartbeat`) aggregates usage (flows/flowCapacity, tcp/udp sessions+capacity, pumps running/degraded), health state (degraded, consecutiveForced, cooldownRemainingSeconds), and the counter movement since the previous tick. **The counter deltas are one `deltas` field**, not one field per counter: the key set is open (`RuntimeCounters.Increment` accepts any key, and the native pools register `pool.<name>.rented|returned` at runtime), and `SYSLIB1015` requires every `[LoggerMessage]` parameter to appear in its message template, so a runtime key set cannot be a set of placeholders. The field carries `key=delta` tokens in ordinal key order with zero deltas omitted and is null when nothing moved; the same constraint shaped `runner.forcedRefresh`'s single `windows` field. A consumer that used to read `Fields["attributionMiss"]` must parse the token list instead. Observational loop: a faulty usage provider warns and retries next tick, never dies.
- **Change-gated group warns**: recurring state-shaped warnings (e.g. `udp.targets.noMac`) emit on first occurrence and only when the member SET changes — not on every refresh. Full recovery (all MACs valid) or empty scope resets the memory so the next occurrence re-emits.
- **Failure-path events carry their reason**: never log a fixed-text failure line again. `tcp.redirect.relaySetupFailed` carries error type + socketError/nativeError + upstream + attempts; `tcp.redirect.unrelatedPeer` carries listener/expected/actual; `udp.reinject.*` carry flow key + map summary. A failure log without its distinguisher is a defect.
- **Incident triad rule** (acceptance gate for future diagnostics work): for any incident, the log must let an operator read out the three essentials — 失效环节 (which leg failed), 触发信号 (which signal fired), 恢复动作 (what recovery ran). The 2026-09-17 replay against the new events satisfies this; keep it that way.
