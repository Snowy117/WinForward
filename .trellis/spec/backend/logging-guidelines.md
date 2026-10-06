# Logging Guidelines

## Overview

Runtime logging uses `Microsoft.Extensions.Logging`. Every log statement is a `[LoggerMessage]`
source-generated method, so the level check lives in generated code and no call site hand-writes
`IsEnabled`. The console sink is `Microsoft.Extensions.Logging.Console`; the project owns no
formatter and no logger interface.

Logging is observational and must never participate in packet disposition, fail-closed, relay, or
shutdown decisions.

## Composition

- `src/WinForward.Runtime/Logging/RuntimeLogging.cs` builds the one `ILoggerFactory`:
  the configured threshold, `AddConsole` with `LogToStandardErrorThreshold = LogLevel.Trace`, and
  either `AddSimpleConsole` or `AddJsonConsole`.
- Runtime logging goes to **stderr only**. `WinForward adapters` writes a TSV table on stdout and
  `validate` writes its confirmation there; a log line must never share that stream.
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

The `config.json` vocabulary is unchanged and ordered from most severe to most verbose:
`error`, `warn`, `info`, `debug`, `trace`, case-insensitive with surrounding whitespace ignored,
defaulting to `info`. `ParseLogLevel` maps them onto `LogLevel.Error`, `.Warning`,
`.Information`, `.Debug`, `.Trace`; `critical` and `none` stay rejected with the `logLevel`
diagnostic. Threshold ordering is MEL's `Trace < Debug < Information < Warning < Error`, and the
configured threshold includes itself and every more-severe level.

`info` is concise operational lifecycle output; `debug` records logical flow and proxy lifecycle;
`trace` records per-packet processing stages and terminal outcomes. Never raise a level to make a
line more visible.

## Log Format

`config.json`'s `logFormat` accepts `auto`, `simple`, or `json`, case-insensitive with surrounding
whitespace ignored, defaulting to `auto`.

- `auto` resolves to `json` when stderr is redirected and to `simple` when stderr is an
  interactive terminal.
- `simple` abbreviates the level (`trce`, `dbug`, `info`, `warn`, `fail`), then the category, the
  numeric `EventId`, and the message; an exception's stack trace follows on continuation lines.
- `json` writes one JSON object per line: `Timestamp`, `EventId`, `LogLevel`, `Category`,
  `Message`, `Exception`, and `State` (the message template's fields plus `{OriginalFormat}`).

Both formats are configured with a local wall-clock timestamp carrying its UTC offset
(`zzz yyyy-MM-dd HH:mm:ss.fff`). `singleLine` stays at MEL's default of `false` so an exception
keeps its stack trace.

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

This contract applies to `config.json` logging, the `ILoggerFactory` built by
`RuntimeLogging.CreateLoggerFactory`, every `[LoggerMessage]` method, packet dispatch, and proxy
coordinators. `ConfigurationLoader.TryParse` rejects a non-string `logLevel` at path `logLevel`
and a non-string `logFormat` at path `logFormat`; `ConfigurationLoader.TryValidate` normalizes
both into `ValidatedConfiguration.LogLevel` and `ValidatedConfiguration.LogFormat` and derives the
process-path privacy flag. `LogLevelNames` owns the string vocabulary in both directions.

### Boundary Contracts

- Accepted levels are `error`, `warn`, `info`, `debug`, and `trace`; accepted formats are `auto`,
  `simple`, and `json`. Case-insensitive, surrounding whitespace ignored. Omission means `info`
  and `auto`.
- Threshold ordering is `error < warn < info < debug < trace`; a threshold includes itself and all
  more-severe levels. Filtering is `LoggerFilterOptions`' job; the disabled path is short-circuited
  by the generated method before it builds any state.
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
| `logLevel` omitted | Validate successfully with `info` |
| `logFormat` omitted | Validate successfully with `auto` |
| Valid level or format string | Normalize and store the matching enum |
| Null, blank, unknown, or wrong type | Fail with a `logLevel` / `logFormat` diagnostic without echoing raw input |
| Entry below the threshold | The generated method returns before constructing state; no formatting, no output |
| Console writer failure | Not swallowed by project code (MEL owns the sink) |
| Classification/dispatch failure | Emit trace `packet.failed` when possible, dispose the lease, and rethrow |
| Proxy or reinjection failure | Preserve existing fail-closed behavior and log metadata/reason only |

### Good / Base / Bad Cases

- Good: `"logLevel": " Trace ", "logFormat": "json"` produces one JSON object per record whose
  `Message` reads
  `UDP session created for 10.0.0.5:5353 -> 8.8.8.8:53 via main (socks5/uot), association 7.`
  and whose `State` carries `source`, `destination`, `target`, `udpTransport`, `udpAssociation`.
- Base: omitted `logLevel` and `logFormat` preserve concise lifecycle output on a terminal and
  structured JSON in a redirected log, without per-packet work.
- Bad: passing a SOCKS5 password, packet span, UDP payload, authentication frame, relay buffer, or
  raw configuration JSON to a `[LoggerMessage]` method.

### Required Tests

- Configuration tests cover defaulting, all five levels and all three formats, case/whitespace
  normalization, wrong types, blank/unknown values, and both diagnostic paths.
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
