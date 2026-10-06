# Implement — MEL logging adoption

Execution plan for `prd.md` / `design.md`. Each step ends with a build or test command that must
pass before the next step starts. Steps 1–5 keep the tree green; step 6 is the removal step and
is the only one that deliberately breaks stale call sites.

## Step 0 — Baseline

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release --no-build
```

Record the baseline test count. Everything after this must not lose a test.

## Step 1 — Foundation (additive, tree stays green)

1. `Directory.Packages.props`: pin `Microsoft.Extensions.Logging` and
   `Microsoft.Extensions.Logging.Console` at `10.0.12`.
2. Package references in `src/WinForward.Configuration` (Abstractions is enough for `LogLevel`,
   but keep one version — reference `Microsoft.Extensions.Logging`), `src/WinForward.Runtime`,
   `src/WinForward.Cli`. Test projects inherit transitively through `WinForward.TestSupport`;
   `benchmarks/WinForward.Benchmarks` references it explicitly.
3. New `src/WinForward.Runtime/Logging/RuntimeLogCategories.cs` — the six category constants
   (`cli`, `capture`, `flow`, `udp`, `tcp`, `runtime`).
4. New `src/WinForward.Runtime/Logging/RuntimeLogging.cs` — `CreateLoggerFactory(LogLevel)` with
   the exact configuration in `design.md` §3 (stderr routing + timestamp, nothing else).
5. `src/WinForward.Configuration`: add the `logLevel` string ↔ `LogLevel` bridge next to the
   existing validation, keeping the accepted vocabulary and diagnostics byte-identical.
   `ValidatedConfiguration.LogLevel` type changes `RuntimeLogLevel` → `LogLevel`. Delete
   `RuntimeLogLevel` only in step 6; until then keep the old enum alive if anything still needs it.
6. `tests/WinForward.TestSupport/RecordingLogger.cs` gains the `ILogger`-based recorder and a
   `RecordingLoggerProvider`, beside the existing `RecordingRuntimeLogger` so converted and
   unconverted areas can coexist.
7. New cases in `tests/WinForward.Runtime.Flow.Tests/RuntimeLoggingTests.cs` asserting the
   threshold contract at the `LoggerFactory` level (one theory over all five levels), **beside**
   the existing ones.

Validate:

```bash
dotnet build WinForward.slnx -c Release
```

## Step 2 — `[LoggerMessage]` classes, per area

Each area is converted **as a unit** — its `[LoggerMessage]` class, its call sites and its tests
together — because all Runtime areas share one project: two half-converted areas in flight would
leave the project uncompilable and neither owner able to verify. The conversion brief every area
follows is `.trellis/tasks/10-06-mel-logging/research/migration-brief.md`.

Area partition, in the order the areas are converted:

| Order | Area | Owns | Produces |
| --- | --- | --- | --- |
| 1 | capture | `src/WinForward.Runtime/Capture/**`, `tests/WinForward.Runtime.Capture.Tests/**` | `Logging/CaptureLog.cs` |
| 2 | udp | `src/WinForward.Runtime/UdpProxy/**`, `src/WinForward.Runtime/Socks5/**` | `Logging/UdpProxyLog.cs` |
| 3 | tcp | `src/WinForward.Runtime/TcpRedirect/**` | `Logging/TcpRedirectLog.cs` |
| 4 | flow + runtime | `FlowDispatcher.cs`, `FlowAttributionPipeline.cs`, `IdleExpirySweeper.cs`, `RuntimeHeartbeat.cs`, `InterceptionHealthMonitor.cs`, `AdapterTransientRetryLogGate.cs` | `Logging/FlowLog.cs`, `Logging/RuntimeLog.cs` |
| 5 | cli | `src/WinForward.Cli/**` | `Logging/StartupLog.cs` |

The CLI is last on purpose: it is the composition root, so it must hold the legacy logger and the
MEL factory side by side until every Runtime area has converted.

**Where the categories are decided.** The pre-migration CLI threads one `IRuntimeLogger` instance
from the root into every component, so keeping that shape would stamp a single category on every
line and make the six-category vocabulary in the README and the spec a fiction. `DurableCaptureBundle`
is a composer rather than a leaf — it constructs the flow dispatcher, the packet executor, the idle
sweeper and both proxy composers — so it takes the `ILoggerFactory` and creates each area's logger
itself. Leaf components keep receiving one pre-categorised `ILogger`, and `Program.cs` creates only
the categories it uses directly (`cli`, `capture`, `runtime`).

**Where the missing `info` surface lives.** The capture area added two milestones; the udp, tcp and
flow areas correctly added none, because every candidate line in those areas is per-flow or
per-sweep and belongs at `debug`. The operator-visible gap is therefore the startup narrative, which
only the composition root can tell, and the CLI step owns it:

- one line for the resolved run: configuration file, active log level and format, target count,
  host/forwarded rule counts and fallback actions, flow and session capacities;
- one line per configured proxy target as it is armed (name, kind, endpoint, UDP carriage, whether
  authentication is configured) — once at startup, never per flow;
- the existing interception-started, heartbeat, shutdown-requested and clean-stop lines stay as they
  are.

The event-name and field-key inventory is the source of truth: every `EventName` and every
placeholder must match an existing `Event(...)` name and an existing `RuntimeLogField` key.

Validate after each area:

```bash
dotnet build WinForward.slnx -c Release
dotnet test tests/<that area's test project> -c Release
```

## Step 4 — `info` surface

Add the milestones (R7), each as an `Information` `[LoggerMessage]`:

- resolved configuration summary at startup (target count, rule count, flow capacities, log level;
  never raw configuration text);
- capture scope resolved (adapter count, tunnel mode) and each capture generation started;
- each TCP redirect listener bound and each UDP target armed (once per target, not per flow);
- process-attribution / policy summary when a policy with rules loads;
- interception started, shutdown requested, clean stop, and the heartbeat summary that already
  exists;
- idle-expiry sweep expiring at least one flow: **reviewed and deliberately left at `Debug`**, since `runtime.expired` is already a gated per-sweep summary and raising it would add a line every sweep on a busy host.

Prune in the same pass: any guard left double-checking a level the `[LoggerMessage]` method now
checks, and any field whose value is the constant event name (the capture area's four packet-trace
events dropped their `stage` field for exactly that reason).

The two `string.Create` window summaries (`UdpSetupQueueBudget`, `UdpProxySession`) are **kept**
rather than pruned, contrary to the first draft of this plan: those counters have no other
surface, and as structured events with named fields they are strictly more useful than the single
formatted string they replace.

Validate: the existing diagnostic-logging tests, plus a `RecordingLogger` assertion that each new
milestone emits at `Information` and not below.

## Step 5 — Benchmarks

`BenchmarkShared` and `StabilityShared` move to `ILoggerProvider`-based counting. Confirm
`BuildProductEvents` still reports the same nine names (the array was never twelve).

```bash
dotnet build benchmarks/WinForward.Benchmarks -c Release
```

## Step 6 — Removal

1. Delete `src/WinForward.Runtime/RuntimeLogging.cs` (the legacy `IRuntimeLogger`,
   `RuntimeLogLevel`, `RuntimeLogField`, `NullRuntimeLogger`, `ConsoleRuntimeLogger`) and
   `tests/WinForward.TestSupport/RecordingRuntimeLogger.cs`.
2. Fix every remaining compile error.
3. Delete the superseded cases from `RuntimeLoggingTests.cs` (the `ConsoleRuntimeLogger` theory and
   the `RuntimeLogField`-shaped formatter test) — their replacements landed in step 1.

### Event-name anchor

`research/event-name-anchor.txt` lists the 66 structured event names that existed at `git HEAD`.
Renaming or dropping one is a silent breaking change to the machine vocabulary: the stability
census looks up a fixed list, and ~35 diagnostic assertions select events by name. Step 6 must
prove every anchor name survived:

```bash
rg -o -N 'EventName = "[^"]+"' -g '*.cs' src | sed -E 's/.*EventName = "([^"]+)"/\1/' | sort -u > /tmp/event-names-now.txt
comm -23 <(rg -v '^#' .trellis/tasks/10-06-mel-logging/research/event-name-anchor.txt | sort -u) /tmp/event-names-now.txt
```

Empty output is the pass condition. A non-empty result is either a rename to revert or a deliberate
vocabulary change to justify in the task record.

Validate:

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
rg -n 'RuntimeLogLevel|IRuntimeLogger|RuntimeLogField|ConsoleRuntimeLogger' src tests benchmarks
```

The last command must print nothing.

## Step 7 — Full quality gate (Phase 2.2 / 3.4)

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx   # must show zero <Issue>
```

`dotnet format` must exit 0 with empty output; the JetBrains report must contain zero `<Issue>`
entries. Then a manual smoke check of the CLI's new line shape and of stdout being untouched.

### AOT verification

The Roslyn trim/AOT analyzers are clean on the new dependency (verified in a standalone project
with this repo's compiler settings before implementation started), but an analyzer pass is not a
link pass. This machine has no `clang`, which `PublishAot` needs, and the publish target is
`win-x64` from Linux, so a true native publish is not reachable here. In descending order of
strength, do whichever is available:

1. `dotnet publish src/WinForward.Cli -c Release -r linux-x64 -p:PublishAot=true` after
   installing clang (`nix run nixpkgs#clang nixpkgs#lld`) — the only check that exercises the
   native compilation of the MEL closure.
2. `dotnet publish src/WinForward.Cli -c Release -r linux-x64 -p:PublishTrimmed=true` — runs the
   real ILLink trimmer over the published closure even without a native toolchain, which is where
   a reflection-driven trim warning in `Microsoft.Extensions.Logging` would surface.
3. The analyzer pass alone, recorded as the residual risk.

Whichever runs, its warnings must be zero.

## Rollback points

- After step 1 or 2: `git checkout` the added files; nothing else changed.
- After a single area in step 3: revert that area's files only; other areas and the old logger are
  untouched by construction.
- Step 6 is the first irreversible step; it is deliberately last, and `git revert` of that one
  commit restores `RuntimeLogging.cs` without touching the converted call sites' history.

## Outcome

All six steps ran. Final state on this machine:

| Gate | Result |
| --- | --- |
| `dotnet build WinForward.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test WinForward.slnx -c Release` | 13 projects, 1265 tests, 0 failures |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, empty output |
| `jb inspectcode -f=Xml -e=HINT` | 0 issues |
| R1 grep (`RuntimeLogLevel\|IRuntimeLogger\|RuntimeLogField\|ConsoleRuntimeLogger`) over `src`/`tests`/`benchmarks` | clean |
| R4 event-name anchor (71 names) | every one survives as an `EventName` literal |
| No `ILogger.Log*` extension calls in `src/` | clean |

Delivered: 134 `[LoggerMessage]` declarations (133 unique method names; `NoCaptureAdapters` exists in both `CaptureLog` and `StartupLog`) across `CaptureLog`, `UdpProxyLog`, `TcpRedirectLog`,
`FlowLog`, `RuntimeLog` and the CLI's `StartupLog`; `Microsoft.Extensions.Logging` 10.0.12 (+
`.Console`) replacing a hand-rolled logger, enum, field DSL and formatter; `logFormat` as a new
`config.json` setting with an `auto` rule that follows whether stderr is redirected; eight new
event names (six information milestones plus the two same-name/different-level and
same-name/different-shape splits); and the four deleted types.

Two gates needed a second pass. The format gate first reported 42 JetBrains issues that
`dotnet format` cannot see (27 redundant `using WinForward.Configuration;` left behind by the
enum's removal, five redundant `(long)` casts the areas added when the parameter was still
`object?`, and a handful of style hints), then the two enum-switch inspections turned out to be
mutually unsatisfiable for `JsonValueKind` and the parser was reshaped so neither applies. A BOM
regression the removal script introduced (`utf-8-sig` on write) was caught by the `CHARSET`
diagnostic and reverted across 30 files.

Not done here: a real Native AOT publish. This machine has no `clang` and the publish target is
`win-x64` from Linux, so only the Roslyn trim/AOT analyzer pass was exercised, plus a standalone
project probe with the repository's exact compiler settings. That remains the one unverified risk
recorded in `design.md` §7.

**Do not try to close that gap with `-r linux-x64 -p:PublishTrimmed=true`.** It was attempted and
it produces a *vacuous* pass. ILLink folds `OperatingSystem.IsWindows()` to `false` for a linux-x64
target and removes the entire Windows-only product path — the published `WinForward.dll` references
neither `LoggerFactory`/`AddConsole` nor `NdisApiDriver`/`LayeredCaptureRunner`, and the logging
closure is not even present in the output (`Microsoft.Extensions.Logging.dll` and
`Microsoft.Extensions.Logging.Console.dll` are gone, leaving only `Abstractions`). The run reported
zero warnings because it never inspected the code in question. A trimmed or AOT publish for this
CLI is only meaningful on a `win-x64` host, which means the release pipeline.

## Follow-up deferred by the owner (not part of this task)

Recorded here so it is not lost. The owner reviewed the `logFormat` knob and decided the project
should **adopt MEL's own configuration surface instead of extending `config.json`**:

1. Delete `logFormat` (and `LogFormatNames`, `LogFormat`, `RuntimeLogging.ResolveLogFormat`, and
   the `auto` rule that follows `Console.IsErrorRedirected`).
2. Bind the standard `Logging` section instead — `builder.AddConfiguration(section)` plus
   `AddConsole()`, which exposes `Logging:Console:FormatterName` (so the formatter is chosen by
   name, including any third-party or project-registered one) and
   `Logging:Console:FormatterOptions:{TimestampFormat,UseUtcTimestamp,SingleLine,IncludeScopes,ColorBehavior,JsonWriterOptions}`.
3. Consider folding the project's own `config.json` into `appsettings.json`, so an operator has one
   file and the logging knobs are the ones every .NET developer already knows.

The appeal is that this costs a user nothing to learn and makes an exotic sink trivial — the owner's
example was being able to configure a CSV formatter without touching this repository. The reason it
is deferred rather than folded in: our `config.json` carries a hand-written validator with its own
diagnostic paths (R2 of this task pins those diagnostics), so adopting `IConfiguration` means
deciding which surface owns `logLevel` and how the two validation stories compose. That is a design
decision in its own right, not a rider on this migration.

Two things this task leaves in a better position for it: the `Logging:LogLevel:<category>` overrides
only become worth having once categories are fully qualified (see the FQCN change below), and
`logging-guidelines.md` now names `RuntimeLogging.CreateLoggerFactory` as the single composition
point that would change.

### Pre-existing test failures (not caused by this task)

`WinForward.Runtime.UdpProxy.Tests.UdpUotFlowLifecycleTests.TwoConcurrentUotFlowsOwnTwoConnectionsAndBothAliasClaimsSucceed`
fails **5 out of 5 runs in isolation on the pre-migration baseline** (`git stash` of the whole
task, `dotnet test --filter`, verified) and passes inside a full-suite run. It asserts
`server.FrameCount == 2` immediately after waiting only for the two sessions to become ready, so
the frame flush races session readiness. Nothing in this task changes that path; it was confirmed
against HEAD rather than assumed. The same caution applies to
`Socks5ControlConnectionDeferredHandshakeTests.DisposeJoinsAParkedCompletion`, which failed once on
the clean baseline and once mid-migration and passes consistently in isolation.

Both are recorded here so that a later reader does not attribute them to the logging migration.

## Owner-requested follow-up changes (done in this task)

Two changes the owner asked for after reading the MEL best-practice research:

**1. PascalCase placeholders** (MEL's documented recommendation: "We recommend Pascal casing for
placeholder names", and Serilog does the same). Verified first that MEL matches a placeholder to a
parameter **case-insensitively** and that the state key takes the *placeholder's* casing, so the
C# parameters stay camelCase — the rename touches `Message` templates only and needs no suppression
and no naming-convention violation. 467 placeholder occurrences across the 134 logging methods, plus
the field-key literals the tests read back.

**2. Fully-qualified category names** (MEL's convention, which OTel phrases as dot-separated
UpperCamelCase). `RuntimeLogCategories` and its six short names (`cli`, `capture`, `flow`, `udp`,
`tcp`, `runtime`) are deleted; the composition root now creates one logger per **module owner** via
`loggerFactory.CreateLogger<Owner>()`, giving categories such as
`WinForward.Runtime.UdpProxy.UdpProxyCoordinator` and `WinForward.Cli.Program`. The CLI's own lines
use `CreateLogger(typeof(Program).FullName!)` because `Program` is static and cannot be a type
argument. Correcting `logFormat` reporting came along for free: the run summary now reports the
**effective** shape rather than the literal `auto`, via `RuntimeLogging.ResolveLogFormat`.

Granularity is per module owner, not per class: a subsystem that receives its `ILogger` through an
options record (the UDP and TCP coordinators and everything they construct) shares the owner's
category. Per-class categories would mean threading an `ILoggerFactory` through those options
records instead of an `ILogger` — a real refactor, deliberately deferred and recorded in
`logging-guidelines.md` so the current granularity reads as a decision rather than an oversight.

### A note on how these two changes were made

Both were mechanical renames performed with scripts, and the first attempts were wrong in two
independent ways that are worth recording, because the *verification* is what caught them:

- The test-side literal rename was too broad: it rewrote **every** string literal matching a
  placeholder name, so JSON config keys (`"udpSessionCapacity"`), asserted values (`"adapters"`),
  counter keys (`"udpSessions"`) and even `[SupportedOSPlatform("Windows")]` were corrupted. The
  fix was to restrict renames to field-key positions and to restore everything else from HEAD by
  matching each line's brace-normalised shape. Only `[SupportedOSPlatform]` was silently wrong;
  every other case failed loudly.
- A line-restoration helper appended `re.split`'s captured group *and* the rebuilt brace, turning
  `$"{stableId}=..."` into `$"stableId{stableId}=..."`. The signature is a duplicated identifier
  before a brace (`X{X}`), and a case-insensitive backreference scan found all four sites. One of
  them compiled and silently changed a rendered value; the test caught it.

Neither class of error survived the gates, but both were introduced by the tooling rather than by
reasoning about the code, and the lesson is the one this task keeps relearning: a script that edits
source needs a detector for the exact shape of its own failure, and `build + test` is that detector.
