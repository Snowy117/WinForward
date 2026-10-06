# Migration brief — `IRuntimeLogger` → `Microsoft.Extensions.Logging`

Read this in full before editing. You are converting **one area** of an in-flight migration.
Correctness rules below are not stylistic preferences; they exist to keep ~46 test files and a
stability benchmark working.

## State of the world

- `Microsoft.Extensions.Logging` 10.0.12 and `Microsoft.Extensions.Logging.Console` 10.0.12 are
  already referenced by `WinForward.Runtime` and `WinForward.Cli`.
- `src/WinForward.Runtime/Logging/RuntimeLogging.cs` (new) provides
  `RuntimeLogging.CreateLoggerFactory(LogLevel, LogFormat)` and `RuntimeLogCategories`
  (`Cli`, `Capture`, `Flow`, `Udp`, `Tcp`, `Runtime`).
- `tests/WinForward.TestSupport/RecordingLogger.cs` (new) provides `RecordingLogger : ILogger`,
  `RecordingLoggerProvider` and `RecordedEvent`.
- `LogLevel` (MEL) has **replaced** `RuntimeLogLevel` in `ValidatedConfiguration.LogLevel`.
- The **legacy** `IRuntimeLogger` / `RuntimeLogLevel` / `RuntimeLogField` / `ConsoleRuntimeLogger` /
  `RecordingRuntimeLogger` still exist and still compile, so that unconverted areas keep building.
  **They are deleted at the end of the migration.** Do not add new uses of them, and do not delete
  them — that is the integrator's job.

## The conversion

### 1. `ILogger` replaces `IRuntimeLogger` everywhere in your area

- Fields, constructor parameters, options-record members and `null` defaults change type from
  `IRuntimeLogger` to `ILogger`. A `null` default becomes `NullLogger.Instance`
  (namespace `Microsoft.Extensions.Logging.Abstractions`).
- The composition root, not the component, decides the category:
  `loggerFactory.CreateLogger(RuntimeLogCategories.Udp)`. Components just accept `ILogger`.
- Remove every hand-written `if (!_logger.IsEnabled(...))` / `if (_logger.IsEnabled(...))` guard.
  The generated `[LoggerMessage]` method performs that check itself. A guard that also does real
  work (a throttle check, a counter) stays — only the level test goes.

### 2. Every log statement becomes a `[LoggerMessage]` method

One `internal static partial class` per area under `src/WinForward.Runtime/Logging/`
(`UdpProxyLog`, `TcpRedirectLog`, `CaptureLog`, `FlowLog`, `RuntimeLog`) and
`src/WinForward.Cli/Logging/StartupLog.cs` for the CLI. Methods are
`public static partial void` with `ILogger logger` as the first parameter.

**Non-negotiable rules**, in priority order:

1. **`EventName` is the existing dotted event name, verbatim.** `logger.Event(RuntimeLogLevel.Warn,
   "udp.reinject.drop", ...)` becomes a method with `EventName = "udp.reinject.drop"`. Tests
   assert on `EventId.Name`, and `benchmarks/WinForward.Benchmarks/Stability/StabilityShared.cs`
   reads a fixed list of these names. Renaming or dropping one is a defect.
2. **Every placeholder is spelled exactly like the `RuntimeLogField` key it replaces.** The old
   field `new("udpAssociation", ...)` becomes the placeholder `{udpAssociation}` with a C#
   parameter named `udpAssociation`. This is legal (C# parameters are camelCase) and it is what
   keeps every existing field assertion working.
3. **Preserve null-ness.** Old code wrote `new("flow", generation == 0 ? null : generation)`. The
   generated method takes `long? flow` and the call passes the same expression. Never substitute
   `0` for `null` and never drop a null.
4. **Method name is the PascalCase of the event name** — `udp.session.created` →
   `UdpSessionCreated`, `tcp.redirect.relaySetupFailed` → `TcpRedirectRelaySetupFailed`.
5. **No `string eventName` parameter survives.** The five dynamic-name helpers
   (`UdpProxyLogging.LogDebug/LogTrace`, `TcpRedirectLogging.LogDebug/LogTrace`,
   `UdpResponseReinjector.LogTrace`, `NdisPacketActionExecutor.LogPacket`,
   `FlowDispatcher.LogPacketStage`) each cover a finite set of names. Enumerate the set and emit
   one method per name. If a helper prepends a shared field block to caller-supplied fields, give
   each resulting method that same field block explicitly.
6. **The message is a natural English sentence** that embeds the placeholders; the event name is
   never repeated inside it. Old:
   `"udp.setup.failed protocol={protocol} source={source} destination={destination} reason={reason}"`.
   New:
   `"UDP session setup failed for {source} -> {destination} ({protocol}): {reason}."`
7. **`Exception` is a real `Exception` parameter**, not a `{exceptionType}` placeholder, wherever
   the old code only extracted `exception.GetType().Name`. Keep the old `reason` field too when
   tests assert on it. Do **not** attach an exception to an expected, routinely-handled I/O
   failure (socket reset, timeout) — those keep their structured `error`/`socketError` fields and
   no stack trace. Attach it only where the old code called `exception.ToString()` or where the
   fault is genuinely unexpected.
8. **`LogLevel` policy.** Per-packet and per-session-per-event stay `Trace`/`Debug`. `Information`
   is for milestones an operator reads end to end. Never raise a level to make a line more
   visible; if a line was `Trace` it stays `Trace` unless this brief says otherwise.

### 3. Same event name at two levels

`SYSLIB1025` is an **error**, not a warning: the generator refuses two `[LoggerMessage]` methods in
one class that declare the same `EventName`. `generation.startup-fault` is exactly that case — it
is logged at `Warning` when the fault is absorbed and at `Error` when the recovery budget is
exhausted, and both must keep `EventName = "generation.startup-fault"` because tests select the
event by name. The workaround is to declare one of the pair in a second `internal static partial
class` in the same file (see `CaptureStartupFaultLog`). Both methods still reach
`EventId.Name == "generation.startup-fault"`; only `EventId.Id` differs. Do not "fix" the
collision by renaming one event — that is a breaking change to the machine vocabulary.

Run `rg -n 'EventName = "' <your area's log class>` before you finish and confirm no name appears
twice inside one class.

### 4. `CA1873` is a gate hazard

`dotnet format --severity info` flags `CA1873` when a `[LoggerMessage]` method's argument is
itself a method call, property access or other non-trivial expression, and the fix cascades into
`CA1859`. The capture area hit 21 of them on the first cut. The remedy is to hoist the expression
into a local before the call:

```csharp
// CA1873: string.Join is a call in an argument position
CaptureLog.CaptureScopeResolved(_logger, scope.Count, string.Join("; ", scope.Select(Describe)));

// fine
var description = string.Join("; ", scope.Select(Describe));
CaptureLog.CaptureScopeResolved(_logger, scope.Count, description);
```

Evaluation stays eager and identical, and it is still no call-site `IsEnabled`. Do not "fix" this
by adding a guard — the analyzer is asking for the value to be materialised first, not for the
call to be skipped.

Run the format gate over just your files before you finish:

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore --include <your files>
```

### 5. The legacy threshold predicate inverts

The legacy `RuntimeLogLevel` runs `Error = 0 … Trace = 4`, so a recording logger built as
`new RecordingRuntimeLogger(level => level <= RuntimeLogLevel.Info)` means "info and above". MEL's
`LogLevel` runs the other way (`Trace = 0 … Critical = 5`), so the migrated predicate is
`level >= LogLevel.Information` — the comparison flips. Tests that take the `isEnabled` constructor
argument are the ones that hit this; the udp area lost two assertions to it before catching on.

### 6. Plain message lines

`logger.Warn("...")` / `.Error(...)` / `.Info(...)` / `.Debug(...)` become `[LoggerMessage]`
methods too, with a natural sentence and no placeholders when the text is constant.

Where the text came from a variable (`_logger.Error(error)`, `logger.Warn(warning)`), use a
single-placeholder method:

```csharp
[LoggerMessage(Level = LogLevel.Error, Message = "{detail}")]
public static partial void ConfigurationError(ILogger logger, string detail);
```

Give it an `EventName` only when the old code had one; a method without `EventName` takes the
method name as `EventId.Name`, which machine consumers ignore.

### 7. `IsEnabled` in hot paths

Nothing to do. The generated method checks before it builds its state, so the disabled path stays
allocation-free without a call-site guard.

## Forbidden

- No `ILogger.LogInformation(...)` / `LogWarning(...)` / `LogError(...)` extension calls in `src/`.
  Everything goes through `[LoggerMessage]` (this also keeps CA1848 silent).
- No `RuntimeLogField`, `RuntimeLogLevel`, `IRuntimeLogger`, `ConsoleRuntimeLogger`,
  `RecordingRuntimeLogger` in converted code.
- No new prose comments explaining what you changed or why the migration looks like this. Comments
  in this repository state contracts or non-obvious invariants, nothing else.

## Verification for your area

```bash
dotnet build src/WinForward.Runtime/WinForward.Runtime.csproj -c Release
dotnet test tests/<your area test project> -c Release
```

**Do not run a whole-solution build while your area is in flight.** The CLI is the composition
root and the last area to convert, so any component type it constructs stops compiling as soon as
that type flips to `ILogger`. That is expected and is the integrator's problem, not yours. `dotnet
test tests/<your area>` does not build `WinForward.Cli`, so it stays a valid oracle.

The build must be zero-warning (`TreatWarningsAsErrors` is on) and the tests green. If a test
asserted on the old `key=value` rendered line, port the assertion to the structured
`RecordedEvent.Fields` / `.Message` rather than to a new string match.

## Shared test support

`tests/WinForward.TestSupport/` references production types from every area, so it is the last
thing to compile. To keep every area's suite runnable throughout, `RecordingRuntimeLogger` in that
project implements **both** `IRuntimeLogger` and `ILogger`: one instance is valid at a legacy
constructor and at a converted one. You never need to convert a fake or a helper that another area
owns in order to unblock yourself — pass the recorder through.

Converted tests use `RecordingLogger` (MEL only). `RecordingRuntimeLogger` exists only so
unconverted areas and shared fakes keep compiling, and disappears with the legacy logger.

## Test migration pattern

```csharp
// before
var logger = new RecordingRuntimeLogger();
...
var (level, _, fields) = Assert.Single(logger.Events, e => e.Name == "udp.reinject.drop");
Assert.Equal("missingHostTarget", fields.Single(f => f.Key == "reason").Value);

// after
var logger = new RecordingLogger();
...
var (level, _, fields) = Assert.Single(logger.Events, e => e.Name == "udp.reinject.drop");
Assert.Equal("missingHostTarget", fields.Single(f => f.Key == "reason").Value);
```

`fields.Single(f => f.Key == "...").Value` keeps working unchanged — `KeyValuePair` has the same
member names `RuntimeLogField` had, and the values are boxed exactly as before.
`logger.Lines` still carries `(LogLevel Level, string Message)`; `logger.WarnCount` is unchanged.
