# Merge the configuration into appsettings.json with MEL-standard logging config

## Goal

Give WinForward one configuration surface that a .NET developer already knows: a single
`appsettings.json` read through `Microsoft.Extensions.Configuration`, with PascalCase keys, the
standard `Logging` section feeding `Microsoft.Extensions.Logging`, and no project-specific knob for
selecting a log format. The operator value is that configuring WinForward stops requiring knowledge
of this repository — including for formatters this project does not ship.

## Background (repository evidence)

**The current configuration surface.**

- `--config <path>` is parsed by a hand-written scanner, `Program.FindOption`
  (`src/WinForward.Cli/Program.cs:432`), and is **required** by both `run` (`:43`) and `validate`
  (`:405`). `adapters` (`:32`) needs no configuration and constructs no logger factory.
- The file is read as raw JSON and deserialized with a source-generated context:
  `ConfigurationLoader.TryParse` (`src/WinForward.Configuration/ConfigurationModels.cs:160`) calls
  `JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.WinForwardConfigDto)` (`:165`).
  A `JsonException` becomes one `ConfigDiagnostic` at the exception's JSON path (`:186`).
- Validation is a hand-written walk over the DTO, `ConfigurationLoader.TryValidate` (`:193`), with
  `ConfigurationLimits.Parse` (`src/WinForward.Configuration/ConfigurationLimits.cs`) enforcing
  ranges and emitting warnings. It produces `ConfigDiagnostic(Path, Message)` values with
  operator-facing paths such as `tcpFlowCapacity` and `setupWorkerCount`
  (`ConfigurationLimits.cs:90,109,129`).
- `ValidatedConfiguration` is immutable for the lifetime of a run; hot reload is explicitly not
  supported (`README.md:275`).
- Fifteen distinct settings keys exist today, all camelCase: `fallbackAction`, `forwarded`, `host`,
  `localTargets`, `logFormat`, `logLevel`, `processingFailureAction`, `proxyUnavailableAction`,
  `rules`, `setupWorkerCount`, `target`, `tcpFlowCapacity`, `udpRelayReceiveBufferKb`,
  `udpSessionCapacity`, `udpSessionIdleSeconds`.
- **Only two keys are validated against their raw JSON type**: `logLevel` (`:170`) and `logFormat`
  (`:174`) must be strings. Everything else is deserialized straight into the DTO, so a misfiled type
  surfaces as a `JsonException` rather than as a targeted diagnostic.
- **The parser is deliberately strict in three ways, and tests pin all three.**
  `ConfigurationJsonContext` declares `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` and
  `UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow`
  (`src/WinForward.Configuration/ConfigurationModels.cs:83`). Therefore: an unknown key is a hard
  error (`ConfigurationRejectsUnknownJsonFields`,
  `tests/WinForward.Configuration.Tests/ConfigurationValidationTests.cs:269`); a **wrongly cased**
  key is an unknown key (`:82` documents that `udpOverTCP` is rejected where `udpOverTcp` is
  required); and a wrong value type fails deserialization. The diagnostic names the offending key's
  JSON path (`:315`).

**Deployment shape.** `README.md:370` records that the first release is a **foreground console
process**; native Windows Service installation is out of scope for it. An administrator launches it
from an arbitrary working directory, and `README.md:95` warns that the configuration file can hold
SOCKS5 credentials and that its permissions must be protected.

**Logging surface today** (built by the task that just landed): `logLevel` (five tokens) and
`logFormat` (`auto` | `simple` | `json`) feed `RuntimeLogging.CreateLoggerFactory`, which registers
`AddConsole` with a compiled-in timestamp format and one of MEL's stock formatters. Nothing about the
formatter is configurable by the operator, and `LogFormat`, `LogFormatNames` and
`RuntimeLogging.ResolveLogFormat` exist only to serve that knob.

**No `Microsoft.Extensions.Configuration` reference exists anywhere** in the tree.
`Directory.Packages.props` pins only `Microsoft.Extensions.Logging`, `.Abstractions` and `.Console`
at 10.0.12.

**The configuration stack is already most of the way here.** Because
`Microsoft.Extensions.Logging.Console` depends on `Microsoft.Extensions.Logging.Configuration`, which
depends on `Microsoft.Extensions.Configuration.Binder` and `Options.ConfigurationExtensions`, the
CLI's existing closure **already contains** `Configuration`, `Configuration.Abstractions`,
`Configuration.Binder`, `Logging.Configuration` and `Options.ConfigurationExtensions`. Adopting the
standard `Logging` section therefore needs no new package at all; only a JSON *file provider* is new
(`Configuration.Json` → `Configuration.FileExtensions`, `FileProviders.Abstractions`,
`FileProviders.Physical`, `FileSystemGlobbing`).

**Measured cost, on the artifact class that actually ships.** The release job
(`.github/workflows/release-build.yml:26`) publishes Native AOT for `win-x64` on `windows-latest`, so the
shipped artifact is one native executable (~5.19 MB). Cross-OS AOT is unsupported, so that exact
artifact cannot be produced on this Linux host; the numbers below come from Native AOT
(`linux-x64`, `StripSymbols=true`, clang 21.1.8) builds of four probes that differ **only** in their
logging/configuration stack, all four paying the same source-generated `System.Text.Json` cost:

| probe | AOT exe | delta |
| --- | --- | --- |
| hand-written logger, no `Microsoft.Extensions.*` | 2.43 MB | — |
| MEL with the format fixed in code (what ships today) | 3.22 MB | **+0.79 MB for MEL** |
| MEL + our own parser flattened into an in-memory configuration source | 3.24 MB | +0.02 MB |
| MEL + `Configuration.Json` (the standard `appsettings.json` file provider) | 3.71 MB | +0.49 MB |

Cross-check against the 5.19 MB CI artifact: a ~0.79 MB MEL cost implies a pre-migration executable
of roughly 4.4 MB, which is consistent. The standard file-provider route would take the shipped exe
to roughly 5.7 MB (+9%); the in-memory route to roughly 5.2 MB (+0.5%). The difference between the
two routes is **+458 KiB**.

## Requirements (draft)

- **R1 — one file.** A single `appsettings.json` carries both the logging configuration and
  WinForward's own settings. `config.json` stops existing as a separate format and schema.
- **R2 — PascalCase keys.** Settings keys follow the .NET convention (`TcpFlowCapacity`,
  `UdpSessionCapacity`, `Targets`, `Rules`, …), because that is what the ecosystem and every
  `appsettings.json` example use.
- **R3 — standard `Logging` section.** `Logging:LogLevel:*` and `Logging:Console:*` are consumed by
  MEL itself, so `logFormat`, `LogFormat`, `LogFormatNames` and `RuntimeLogging.ResolveLogFormat` are
  deleted. Which formatter to use becomes `Logging:Console:FormatterName`.
- **R4 — `validate` is retained.** It stays a driver-free command that parses and validates a
  configuration and reports diagnostics with the existing exit codes (`README.md:43`).
- **R5 — diagnostics stay actionable.** Validation errors and warnings keep naming the setting that
  is wrong, at whatever path the new model defines.
- **R6 — `validate`'s detection power is an explicit decision, not a side effect.** The three
  strictness properties above are either preserved or consciously dropped, and the choice is stated
  in the README so an operator knows what `validate` does and does not catch.

## Resolved decisions

- **Q1 — the configuration layering model (resolved: default + explicit overlay).** Load
  `appsettings.json` from the executable's directory when present (optional), then apply
  `--config <path>` as one more JSON layer when given (optional). `--config` therefore survives, but
  its meaning narrows from "the configuration file" to "one more layer on top of the defaults",
  which is the standard `AddJsonFile` model. The load order is reported by `validate` and by the run
  summary so an operator can always see which sources were read.
- **Q2 — the key layout (resolved by the owner's "follow the local customs" instruction).**
  WinForward's settings live under one top-level section beside `Logging:`, because a named section
  per component is the .NET convention and it keeps this project's keys out of the framework's
  namespace. The section name is a design detail to confirm at design review.
- **Q4 — the formatter surface (resolved by measurement: the whole surface is free).**
  A probe against `Microsoft.Extensions.Logging.Configuration` 10.0.12 confirms that
  `LoggerFactory.Create(b => { b.AddConfiguration(configuration.GetSection("Logging")); b.AddConsole(); })`
  needs **no Generic Host** and that `Logging:Console:FormatterName`,
  `Logging:Console:FormatterOptions:{TimestampFormat,SingleLine,ColorBehavior}` and
  `Logging:LogLevel:<category>` all take effect. Nothing has to be built for R3; what remains is to
  ship a default `appsettings.json`, because `AddConsole()`'s default `TimestampFormat` is empty and
  the timestamp this project currently compiles in would otherwise disappear.
  Two packages are required: `Microsoft.Extensions.Configuration.Json` and
  `Microsoft.Extensions.Logging.Configuration` (which owns `ILoggingBuilder.AddConfiguration`).
  `Microsoft.Extensions.Hosting` is not required.
- **Q3 — the PascalCase compatibility posture (moot).** Because a mis-cased key is *rejected* today
  rather than accepted, and because the file must be re-authored anyway (its name and the location
  of the logging settings change), there is no silent-compatibility cohort to protect: old files fail
  loudly and the README rewrite is the migration.

## Open questions (blocking)

- **Q5 — how much of `validate`'s strictness survives the move to layered configuration.** See
  "The strictness decision" below.

## The strictness decision (Q5)

`IConfiguration` is a case-**insensitive** store of **strings** whose binder **ignores** keys it does
not recognize. All three of the strictness properties recorded above are therefore lost by default
the moment WinForward's settings are bound through it. The options:

- **(a) Validate the effective configuration strictly (recommended).** Materialise the merged
  configuration's own section back to JSON and run the *existing* strict deserializer and validator
  over it. Preserves all three properties — unknown keys, mis-cased keys and wrong types all stay
  hard errors with a path — keeps the 118 existing configuration tests meaningful, and composes
  correctly with layering, because it validates the *merged* result rather than one file. What it
  does not do is let the framework's binder be the sole authority.
- **(b) Strict only inside the project's section, via a new schema walk.** Walk the section's children
  against the known key set and reject what does not match, leaving `Logging:` to the framework.
  Equivalent outcome for this project's keys; more new machinery than (a), and it has to be written
  AOT-safely (no reflection over the DTO).
- **(c) Accept the loosening.** `validate` checks the values it recognises and silently ignores
  everything else, which is how a stock .NET app behaves. Cheapest, and it gives up typo and casing
  detection in a file that holds SOCKS5 credentials and proxy rules.

## Out of scope (draft)

- Any sink beyond the console: no file sink, no Serilog/OpenTelemetry, no rotation.
- Windows Service installation.
- Configuration hot reload (`README.md:275`).
- The `adapters` command and the packet path.
