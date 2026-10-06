# Design — MEL logging adoption

Companion to `prd.md`; requirement ids (R1–R9) refer to it. Facts marked **[spiked]** were
verified against `Microsoft.Extensions.Logging` 10.0.12 on this machine with the repo's own
compiler settings before planning was closed.

## 1. Why MEL and what it replaces

`IRuntimeLogger` exists to answer one question the BCL could not answer at the time: "check the
level before building the message". `[LoggerMessage]` answers it in generated code, so the
hand-written `if (logger.IsEnabled(...))` disappears without giving up the zero-cost disabled
path — and the project stops owning a logger interface, an enum, an event/field DSL and a
formatter.

| Piece today | After |
| --- | --- |
| `IRuntimeLogger` (interface, 7 members) | `ILogger` (BCL) |
| `RuntimeLogLevel` (enum, `WinForward.Configuration`) | `LogLevel` (BCL) |
| `NullRuntimeLogger.Instance` | `NullLogger.Instance` |
| `ConsoleRuntimeLogger` (sink + formatter + level filter) | `Microsoft.Extensions.Logging.Console`'s default `SimpleConsole` formatter; filtering is `LoggerFilterOptions` |
| `RuntimeLogField` + `Event(level, name, params fields)` | `[LoggerMessage]` method per event; fields are template placeholders |
| `UdpProxyLogging` / `TcpRedirectLogging` (static formatters) | `UdpProxyLog` / `TcpRedirectLog` (`static partial` `[LoggerMessage]` classes) |

Deleted outright: `RuntimeLogging.cs`'s four types, `RuntimeLogField`, and the `RuntimeLogLevel`
enum with its five-member ordering.

## 2. Package and factory

**[decision] Reference `Microsoft.Extensions.Logging`, and use the real `LoggerFactory`.**

Evidence:

- **[spiked]** A project with `PublishAot`, `EnableAotAnalyzer`, `EnableTrimAnalyzer`,
  `AnalysisLevel=latest` and `TreatWarningsAsErrors` builds **zero-warning** while calling
  `LoggerFactory.Create(b => { b.SetMinimumLevel(...); b.AddProvider(...); })` and a
  `[LoggerMessage]` method. No trim/AOT diagnostic is raised on our code.
- **[spiked]** `LoggerFactory`'s filter drives the generated short-circuit: with
  `SetMinimumLevel(LogLevel.Warning)` a `Level = LogLevel.Debug` method never reaches the
  provider (0 `Log<TState>` calls), and `logger.IsEnabled(LogLevel.None)` is false.
- Cost is a transitive closure of four Microsoft packages (`DependencyInjection`,
  `DependencyInjection.Abstractions`, `Options`, `Primitives`), constructed once at startup and
  trimmed by the AOT publish. That buys category caching, `LoggerFilterOptions`, disposal and
  `ISupportExternalScope` instead of a bespoke factory we would own forever.

`Microsoft.Extensions.Logging.Console` **is** referenced, and the console shape is Microsoft's
default `SimpleConsole` formatter — the project does not own a formatter any more. **[spiked]**
`AddConsole` + `AddSimpleConsole` builds clean under this repo's `PublishAot`,
`EnableAotAnalyzer`, `EnableTrimAnalyzer` and `TreatWarningsAsErrors`.

Exact sink configuration:

```csharp
LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(threshold);
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.AddSimpleConsole(options => options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ");
});
```

- `LogToStandardErrorThreshold` is **required**, not cosmetic: `WinForward adapters` prints its
  TSV table and `WinForward validate` prints its confirmation on **stdout**, so log lines must
  never share that stream.
- `TimestampFormat` is the one value set beyond MEL's defaults. It is an option *of* the default
  formatter, and the README/spec promise operators a wall-clock stamp for correlating a log with
  an external capture. Removing it is a one-line change if that judgement is wrong.
- `SingleLine` is deliberately **left at its default of `false`**. **[spiked]** `SingleLine = true`
  collapses an exception into the message line and destroys the stack trace, which is the single
  biggest diagnostic gain of the migration; the default multi-line shape renders it properly.
  The consequence is accepted: one event is no longer guaranteed to be one physical line, and the
  "one line per event" property retires with `ConsoleRuntimeLogger`.

Resulting shape, verbatim from the spike:

```
2026-10-06 09:02:16.579 dbug: WinForward.Runtime.UdpProxy.UdpProxyCoordinator[1518028212]
      UDP session created for 10.0.0.5:5353 -> 8.8.8.8:53 via socks5-a (uot), association 7.
2026-10-06 09:02:16.583 fail: WinForward.Runtime.UdpProxy.UdpProxyCoordinator[2001557941]
      Runtime failure.
      System.InvalidOperationException: boom
         at ConSpike.Program.Run(...)
```

`[1518028212]` is `EventId.Id`. The generator always emits an id, and its hash input is the
declared `EventName` when there is one and the method name otherwise — proven by the split
`generation.startup-fault` pair, where two differently-named methods that declared the same
`EventName` both compiled to `EventId(2024787733, "generation.startup-fault")`. Assigning a
hand-maintained numeric registry was considered and rejected — it is bespoke bookkeeping for a
number an operator cannot read anyway, and `EventId.Name` is the identifier machines use.

**Test consequence [measured]:** exactly **one** test file (`RuntimeLoggingTests.cs`) ever
constructed `ConsoleRuntimeLogger` with a `StringWriter`; every other test drives a recording
in-process logger. So no global `Console.SetError` capture is needed anywhere, and the formatter
assertions that existed to test *our* formatter are deleted rather than ported — Microsoft's
formatter is not our code to test. What stays ours, and stays tested, is the threshold contract
(`config.json` → `LogLevel` → `LoggerFactory` filter).

### Level mapping (R2)

`config.json` vocabulary is unchanged; only the internal enum moves.

| `config.json` | `LogLevel` | `SimpleConsole` renders |
| --- | --- | --- |
| `error` | `Error` | `fail: ` |
| `warn` | `Warning` | `warn: ` |
| `info` | `Information` | `info: ` |
| `debug` | `Debug` | `dbug: ` |
| `trace` | `Trace` | `trce: ` |

The abbreviations are MEL's own (`SimpleConsoleFormatter`), not ours; `Critical` maps to
`crit: ` and is never produced by the product. `None` is rejected as a `logLevel` value exactly as
an unknown string is today, and `IsEnabled(None)` is always false **[spiked]**.

`DefaultLogLevel` on `ValidatedConfiguration` becomes `LogLevel.Information`.

## 3. Console sink

There is no sink to write. `RuntimeLogging.CreateLoggerFactory(LogLevel threshold)` in
`src/WinForward.Runtime/Logging/RuntimeLogging.cs` is the whole of it:

```csharp
public static ILoggerFactory CreateLoggerFactory(LogLevel threshold) =>
    LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(threshold);
        builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.AddSimpleConsole(options => options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ");
    });
```

- Filtering lives in `LoggerFilterOptions`, one source of truth, which is where MEL puts it.
- The CLI holds the returned factory in a `using`, because `ConsoleLogger` writes through a
  background queue that `Dispose` drains. A hard kill loses queued lines; a clean shutdown does
  not.
- Colour follows MEL's default (`LoggerColorBehavior.Default`), so it appears only on a real
  terminal and never in a redirected log.

**What is given up, explicitly.** The structured `event.name key=value` rendering, the
hostile-value quoting, the null-omission rule, the guaranteed one-line record and the
never-throw-into-the-caller guarantee all lived in `ConsoleRuntimeLogger` and retire with it.
They are replaced by the guarantees of a widely-used logger, which are different ones. Two
concrete consequences worth naming: a value containing CR/LF can now split a rendered record, and
a console write failure is no longer swallowed by our code. Both were properties this project
chose to own; owning them is what the migration trades away.

**What is gained.** Exceptions now carry their stack trace instead of the call site folding
`exception.GetType().Name` into a message string, `[category]` gives every line a greppable
subsystem tag, and the format, the timestamp, the colour behaviour and (in a future Host-based
composition) the whole sink configuration become ordinary MEL options a user can change without
touching this repository. `Endpoint.ToString()` already renders `1.2.3.4:80` /
`[2001:db8::1]:443` (`src/WinForward.Core/Domain.cs:71`), so endpoint readability is unchanged.

## 4. `[LoggerMessage]` organisation

New `src/WinForward.Runtime/Logging/` (namespace `WinForward.Runtime.Logging`), one `internal
static partial class` per area, methods `public static partial void` with `ILogger` first:

| Class | Area | Replaces |
| --- | --- | --- |
| `FlowLog` | dispatcher, attribution pipeline, idle expiry | bare `_logger.Event` calls |
| `CaptureLog` | capture generations, adapter enumeration, packet processor, action executor | bare `_logger.Event` calls |
| `UdpProxyLog` | UDP coordinator/session/setup/reinjector | `UdpProxy/UdpProxyLogging.cs` |
| `TcpRedirectLog` | TCP redirect acceptor/relay/session store/coordinator | `TcpRedirect/TcpRedirectLogging.cs` |
| `RuntimeLog` | heartbeat, health monitor, adapter retry gate, generation faults | bare `_logger.Event` calls |

`src/WinForward.Cli/Logging/StartupLog.cs` holds the CLI-side methods (the CLI project sees
`WinForward.Runtime` internals through the existing `InternalsVisibleTo`, but its own log methods
belong to its own assembly).

Naming rules, applied mechanically:

1. One method per concrete event; **no `string eventName` parameter survives**. The five dynamic
   name sites (`FlowDispatcher.LogPacketStage`, `NdisPacketActionExecutor.LogPacket`,
   `UdpResponseReinjector.LogTrace`, `UdpProxyLogging.LogDebug/LogTrace`,
   `TcpRedirectLogging.LogDebug/LogTrace`) each enumerate a finite family and expand into one
   method per member.
2. `EventName` is the existing dotted string, verbatim — it is the machine-stable identifier (R4).
3. Method name is the PascalCase of the event name (`udp.session.created` → `UdpSessionCreated`),
   used as the `[LoggerMessage]` method identifier and as the fallback `EventId.Name`.
4. Every template placeholder is spelled exactly like the field key it replaces (`{flow}`,
   `{udpAssociation}`, `{targetKind}`, `{processPath}`, …), which is legal because C# parameters
   are camelCase anyway. This is what keeps `RecordingLogger`-based assertions and the stability
   census working with no translation layer.
5. The message body is an English sentence; the event name is not repeated inside it (R5).
6. `Exception` parameters are real `Exception` parameters, not `{exceptionType}` placeholders, so
   the sink renders them once.

Level discipline (R7): per-packet and per-session-per-event stays `Trace`/`Debug`;
`Information` is reserved for milestones an operator reads end to end. The added `Information`
surface is enumerated in `implement.md`.

## 5. Migration strategy

A strangler migration, because the change touches ~133 source files, ~46 test files and 8
benchmark files, and a big-bang rewrite would leave the tree unbuildable for hours:

1. **Foundation** — add the package, add the MEL sink and the `[LoggerMessage]` classes, add the
   `LogLevel` ↔ `config.json` bridge. `IRuntimeLogger` stays alive so the tree keeps building.
2. **Per-area conversion** — one area at a time (flow → capture → UDP proxy → TCP redirect →
   runtime/CLI), each area switching its own components and its own tests from `IRuntimeLogger`
   to `ILogger`. Build and targeted tests run after each area.
3. **Removal** — once no component takes `IRuntimeLogger`, delete `RuntimeLogging.cs`'s types,
   `RuntimeLogField` and `RuntimeLogLevel`, and finish the test/benchmark migration.

The compiler is the oracle at every step: no `dynamic`, `TreatWarningsAsErrors` on, and the
deleted types are leaves, so an unconverted site fails the build rather than silently keeping the
old path.

## 6. Test and benchmark migration

`tests/WinForward.TestSupport/RecordingLogger.cs` becomes `RecordingLogger : ILogger` +
`RecordingLoggerProvider : ILoggerProvider`, keeping the assertion surface the 35+
diagnostic assertions already use:

- `Events` — `(LogLevel Level, string Name, IReadOnlyList<KeyValuePair<string, object?>> Fields)`
  where `Name` is `eventId.Name` and `Fields` excludes `{OriginalFormat}` **[spiked]**: the
  generated `LogValues` state is `IReadOnlyList<KeyValuePair<string, object?>>` whose keys are the
  placeholder names and whose null values are preserved, and which carries one extra
  `{OriginalFormat}` pair holding the template.
- `Lines` — the `formatter(state, exception)` rendering, so message-text assertions read the same
  sentence an operator sees.
- `WarnCount`, and the optional `Func<LogLevel, bool>? isEnabled` predicate, unchanged in spirit
  (it stays unconditional-recording, it only models a threshold).

Benchmarks: `BenchmarkShared`'s counting logger and `StabilityShared.CountingRuntimeLogger` become
`ILoggerProvider`s counting by `eventId.Name`; `BuildProductEvents`'s fixed name list is
unchanged, which is exactly why R4 exists.

## 7. Risks

| Risk | Mitigation |
| --- | --- |
| AOT publish regresses at link time even though the analyzer is clean **[spiked at analyzer level only; not closable on this machine]** | The obvious shortcut — `-r linux-x64 -p:PublishTrimmed=true` — is worthless here: ILLink folds `OperatingSystem.IsWindows()` to `false`, removes the whole Windows-only product path, and never inspects the logging closure, so its zero warnings are vacuous (verified: the published `WinForward.dll` references neither `LoggerFactory` nor `NdisApiDriver`). Verifying this needs a `win-x64` host, i.e. the release pipeline. The dependency is Microsoft's own AOT-supported logging stack, and the analyzer pass plus a standalone probe under this repository's exact compiler settings both come back clean. |
| The `IsEnabled`-free rule rots after this task | review criterion + PRD acceptance grep; a WF analyzer is *not* added here (out of scope, and the generated methods make the manual guard pointless rather than illegal) |
| Losing structured `key=value` in the console breaks an external consumer | the only consumers are in-repo (stability census is in-process; no test or script parses the rendered line); the structured state is still on `ILogger` for a future structured provider |
| Threshold tests silently weaken because filtering moved from the sink to `LoggerFactory` | the factory-level filter is asserted directly in the migrated `RuntimeLoggingTests` (threshold theory over all five levels) |
| Category names become a second vocabulary to maintain | the category is the fully-qualified name of the type that owns the logger, so it is derived from the type system rather than maintained by hand |

## 8. Decisions taken after the first spike round

The owner reviewed the plan and relaxed the output contract — the old `time [level] message` shape
is not sacred and MEL's own formatters are preferred over a hand-rolled one. Two further
requirements came with that: a manual override for the chosen shape, and unification of path
escaping.

### 8.1 The shape is chosen per destination, with a config override

- `Microsoft.Extensions.Logging.Console` **is** used, with Microsoft's `SimpleConsole` formatter.
  No project formatter exists any more, so §3's `RuntimeConsoleLoggerProvider` was never written.
- `config.json` gains `logFormat`: `auto` (default) | `simple` | `json`, validated exactly like
  `logLevel`, with the same diagnostic style and the same case/whitespace normalization.
  `auto` resolves to `json` when `Console.IsErrorRedirected` and to `simple` otherwise, so a
  watched terminal gets readable lines and a captured file keeps every structured field.
- The four all-MEL configuration options are: `SetMinimumLevel`, `AddConsole`'s
  `LogToStandardErrorThreshold = LogLevel.Trace` (hard requirement — stdout carries the `adapters`
  TSV and the `validate` confirmation), `AddSimpleConsole`'s `TimestampFormat`, and
  `AddJsonConsole`'s `TimestampFormat` + `JsonWriterOptions.Encoder =
  JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (without it every `+` and `>` in a rendered line
  becomes `\u002B` / `\u003E`).
- `SingleLine` stays at its default `false`. **[spiked]** `SingleLine = true` collapses an
  exception into the message line and destroys the stack trace (`... relay lane died    at
  ConSpike.Program.Run(...)`), which is the single biggest diagnostic gain of the migration.
- The timestamp is `zzz yyyy-MM-dd HH:mm:ss.fff`, which renders `+08:00 2026-10-06 09:04:47.282`.
  **[spiked]** .NET exposes only `z` / `zz` / `zzz` for an offset, so the colon-less `+0800` the
  owner originally sketched is not expressible in a format string; a colon-less offset would cost
  a full reimplementation of the formatter, including its colour, scope and multi-line exception
  handling.

### 8.2 Path escaping unifies by construction

The inconsistency was real and lived in `ConsoleRuntimeLogger.FormatValue`: `NeedsQuoting` only
quoted a value that contained whitespace, `"` or `=`, and `Quote` doubled backslashes. So
`C:\Users\test\browser.exe` rendered verbatim while `C:\Program Files\browser.exe` rendered as
`"C:\\Program Files\\browser.exe"` — the same path in two spellings, decided by whether it
happened to contain a space. `simple` now renders both verbatim and `json` escapes both as valid
JSON (`C:\\Program Files\\x.exe`, which a parser restores exactly). Within either format the
treatment is uniform, which is what the requirement asked for.

### 8.3 Custom value types survive both formatters

**[spiked]** `Endpoint`, `IPAddress`, `IPEndPoint` and enums all render through `ToString()` in
both `simple` and `json` — `"endpoint":"2001:db8::1:443"`, not a reflected nested object. So the
existing `Endpoint`-valued fields need no conversion at the call site, and JSON output introduces
no reflection-based serialization of project types. `Endpoint.ToString()` already brackets IPv6
(`src/WinForward.Core/Domain.cs:71`).

### 8.4 The migration stays incremental

The first attempt converted `ValidatedConfiguration.LogLevel` to `LogLevel` and deleted
`RuntimeLogLevel` up front. That produced one shared failure: `IRuntimeLogger`'s own declaration
became an error type, so Roslyn suppressed the body binding of all 25 files that used it and hid
the true blast radius. The corrected sequence keeps a **legacy-local** `RuntimeLogLevel` inside
`src/WinForward.Runtime/RuntimeLogging.cs`, so the legacy logger is self-contained and the tree
builds green while areas convert one at a time. `ConsoleRuntimeLogger` temporarily gained a
`LogLevel` constructor overload so the composition root needs no bridge. All of it is deleted in
the removal step.

`LogLevelNames` (`src/WinForward.Configuration/LogLevelNames.cs`) now owns the `config.json`
vocabulary in both directions — `TryParse` for validation and `ToConfigToken` for the startup line
that reports the active level — instead of the literals being retyped in `ParseLogLevel` and
`Program.cs`.

## 9. Decisions taken during area conversion

### 9.1 One name, one level

`SYSLIB1025` refuses two `[LoggerMessage]` methods that share an `EventName` in one class, and that
is the framework stating a design position: the level is part of an event's identity, which is how
ETW and the Windows event log read an `EventId`. The capture area's first cut worked around it with
a second partial class; the owner rejected that as carrying baggage, correctly. A whole-codebase
scan found exactly one same-name/different-level pair, so the error half of
`generation.startup-fault` was renamed `generation.startup-fault.exhausted` and the split class was
deleted. The warn half keeps the original name, so an existing filter still catches the frequent
case and a prefix search catches both. The rule is now in `logging-guidelines.md`, and the
`LayeredCaptureRunnerRefreshTests` sequence assertion pins both the level order and the name order.

### 9.2 The event-name anchor was wrong by one

`research/event-name-anchor.txt` was regenerated after an area owner noticed
`udp.response.foreign_source` missing. Cause: the extraction regex's character class excluded `_`,
so every event name containing an underscore was silently dropped. The corrected set is **67**
names, and that one was the only casualty. A verification tool that silently under-reports is worse
than no tool, which is why the count is asserted rather than eyeballed.

The stability census's nine fixed names were checked individually against both the anchor and the
converted sources; all nine survive in `UdpProxyLog.cs`.

### 9.3 Null rendering depends on the parameter count — and the first answer was wrong

An area owner reported that the source-generated path renders a null placeholder as empty rather
than `(null)`. A three-parameter probe appeared to disprove it, and the report was dismissed. The
owner was right and the dismissal was wrong: the behaviour **splits on the parameter count**.
Measured on `Microsoft.Extensions.Logging` 10.0.12:

| Parameters | Generated state type | A null placeholder renders as |
| --- | --- | --- |
| 1–6 | MEL's `LogValues<T0..T5>` | `(null)` |
| 7+ | the generator's own synthesised struct | nothing |

The first probe used three parameters and so only ever saw the first row; the product's events are
mostly over six parameters and live in the second. A smoke run of the shipping factory made the
real behaviour visible (`... (udp, flow ) via ...`, with `"flow":null` intact in JSON `State`), and
a parameter-count sweep confirmed the boundary.

Two things follow. The structured state is unaffected — a null is still a null. And the cosmetic
consequence is real: in the common case a nullable placeholder leaves a hole in the sentence, so
message templates should place one at the end of a clause rather than mid-sentence. The spec now
carries that guidance instead of the claim this section originally recorded.

The general lesson is worth more than the fact: a probe is only evidence about the regime it
exercises, and "I measured it" is not a rebuttal when the measurement covers a different input
shape than the one being disputed.

### 9.4 Accepted field-shape deviations (udp area)

1. `udp.session.closed` / `.expired` omit the three target fields (`target`, `targetKind`,
   `udpTransport`) that the old shared helper emitted as null on every teardown call. Accepted: the
   values were always null on those two events, and embedding them renders
   `via (null) ((null)/(null))` in a lifecycle line. `udp.session.created` still carries all eight.
2. `udp.session.created`'s message adds `{protocol}` and `{flow}` to the sentence sketched in §4.
   Accepted: the archived 08-11 design names flow/association correlation as that event's purpose,
   and §4's sentence was illustrative rather than contractual.

### 9.5 Two conversion traps worth their own line

- `CA1873` is gated at info severity: a `[LoggerMessage]` argument that is itself a call is flagged,
  and the fix cascades into `CA1859`. The remedy is to hoist into a local — the same eager
  evaluation, still no call-site `IsEnabled`.
- The legacy threshold predicate **inverts** under MEL: `level <= RuntimeLogLevel.Info` (legacy enum
  `Error = 0 … Trace = 4`) becomes `level >= LogLevel.Information`. Only tests that take the
  recording logger's `isEnabled` argument can hit it, and the udp area lost two assertions to it.

## 10. Decisions taken during the CLI and removal steps

### 10.1 Categories are decided by the composer, not the leaf

The pre-migration CLI threaded one `IRuntimeLogger` instance from the root into everything, so
keeping that shape would have stamped a single category on every line and made the per-owner
vocabulary in the README and the spec a fiction. `DurableCaptureBundle` constructs the flow
dispatcher, the packet executor, the idle sweeper and both proxy composers, so it now takes the
`ILoggerFactory` and creates each area's logger itself; leaf components still receive exactly one
pre-categorised `ILogger`. `Program.cs` creates only the categories it uses directly.

### 10.2 `SYSLIB1015` retired the union-field idea

The two `udp.targets.noMac` call shapes (a zero-MAC host-fallback adapter vs. a set of scope
adapters) looked like one event with a `kind` discriminator and a union of nullable fields. The
generator refuses that: **every `[LoggerMessage]` parameter must be referenced by a placeholder**,
so there is no such thing as a field that exists only in the structured state. Since the shapes
cannot share a template, they are two events — `udp.targets.noMac` keeps the host-fallback half
(the anchor name survives) and the group half became `udp.targets.noMac.adapters`. This is the same
conclusion the owner reached for `generation.startup-fault`, reached from the opposite direction:
the framework keeps pushing "one template, one event, one name".

The integration test's `NoMacEvents` helper now matches the name prefix and selects the variant by
exact name, which is strictly stronger than the old `kind`-field filter because the name is the
machine identity.

### 10.3 The `IsEnabled` acceptance criterion needed an exception

`rg 'IsEnabled' src` after the migration finds two things, neither of them a log guard:
`HighResolutionTimerScope.IsEnabled` (an unrelated timer-resolution flag) and
`FlowDispatcher`'s `if (_logger.IsEnabled(LogLevel.Trace)) return DispatchSlowAsync(...)`. The
second reads a log level but is a dispatch decision: with trace enabled every packet takes the
observable slow path, and deleting it would route the steady state through that path and break the
allocation gates. The PRD criterion was narrowed and the exception documented in the spec, because
a future reader following "no hand-written IsEnabled" literally would otherwise delete it.

### 10.4 One narrow suppression, per the repository's own policy

`CA1859` appeared on `LayeredCaptureRunner.InstallGenerationAsync`'s `IReadOnlyList<T>` parameter
only because the capture area hoisted a call argument into a local to satisfy `CA1873` for its new
milestone. The suggestion asks to narrow the parameter to `List<T>`; the generation factory it
feeds takes `IReadOnlyList<T>`, and the method runs once per capture generation, so following it
would trade the method's contract for an unmeasurable devirtualization on a cold path. Suppressed
narrowly with that reason, which is the option `AGENTS.md` prescribes when a diagnostic does not
genuinely improve the code.

### 10.5 What the migration cost, measured

- 132 `[LoggerMessage]` methods across five log classes plus the CLI's `StartupLog`.
- 8 new event names, all documented in `research/event-name-anchor.txt`; all 71 pre-migration names
  survived, verified mechanically.
- 13 test projects, 1265 tests, 0 failures; the four earlier areas were re-run green after each
  later area landed.
- Zero legacy logger references remain anywhere in `src/`, `tests/` or `benchmarks/`, and the
  legacy file is deleted rather than deprecated.
