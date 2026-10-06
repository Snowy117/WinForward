# Merge the configuration into appsettings.json with MEL-standard logging config

## Goal

Give WinForward one configuration surface a .NET developer already knows: a single
`appsettings.json` read through `Microsoft.Extensions.Configuration`, with PascalCase keys, the
standard `Logging` section feeding `Microsoft.Extensions.Logging`, and no project-specific knob for
selecting a log format. The operator value is that configuring WinForward stops requiring knowledge
of this repository — including for formatters this project does not ship, which become selectable by
name — with one honest limit recorded below.

### What "selectable by name" does and does not buy

MEL resolves a formatter from what the process **registered**; there is no configuration-driven plugin
mechanism. This project registers exactly the three stock formatters, so `simple`, `json` and
`systemd` are selectable by configuration with no code change, and that is the whole of the gain over
the retired `logFormat` enum. Adding a formatter this project does not ship — a CSV formatter, which
is what motivated the standard section in the first place — still takes its package and one
registration line, because nothing else can put a formatter into the process.

Two consequences the plan carries:

- The pre-flight validates a configured `FormatterName` against the names this build registers
  (`RuntimeLogging.AcceptedFormatterNames`) and rejects anything else with a message listing them,
  because MEL's own behaviour for an unknown name is a **silent fallback to `simple`** — the operator
  would see "my formatter setting is ignored" rather than an error.
- That list and the registrations must not drift apart. The names are taken from the framework's
  `ConsoleFormatterNames` constants rather than retyped, and a test asserts that each accepted name
  renders that formatter's shape, so a name in the list that nothing registered fails the suite.

An extension point that would let an operator drop in a formatter assembly is deliberately **not**
part of this task; it is a separate decision with its own loading and AOT consequences.

## Background

### How it is actually deployed

An operator runs WinForward as a Windows service through **WinSW**, with the executable and the
configuration in the same directory:

```xml
<workingdirectory>%BASE%</workingdirectory>
<executable>%BASE%\WinForward.exe</executable>
<arguments>run --config "%BASE%\config.json"</arguments>
<serviceaccount><username>LocalSystem</username></serviceaccount>
<logpath>%BASE%\logs</logpath>
<log mode="roll-by-time"><pattern>yyyy-MM-dd</pattern>...</log>
```

Three consequences shape this task, and the first contradicts an assumption an earlier draft of this
plan made:

- `README.md:370` says the first release is a **foreground console process** and that native Windows
  Service installation is out of scope. WinSW wraps it anyway. So although the repository does not
  ship service support, the *service* is the real deployment, and the plan must not assume an
  administrator launching from an arbitrary working directory.
- Because `workingdirectory` and the executable both point at `%BASE%`, resolving `appsettings.json`
  from the executable's directory and from the working directory find the **same file**. The
  `--config` argument is therefore redundant in this deployment — the operator's own read is correct.
- WinSW captures the child's stdout and stderr into **files**, so `Console.IsErrorRedirected` is
  true and today's `auto` rule selects **JSON** for the service log. This is existing behaviour, not
  something this task introduces, and it has a desirable property: the service gets machine-readable
  lines while an operator running `WinForward validate` in a terminal gets the human format.

### The configuration surface today

- `--config <path>` is parsed by a hand-written scanner, `Program.FindOption`
  (`src/WinForward.Cli/Program.cs:432`), and is **required** by both `run` (`:43`) and `validate`
  (`:405`). `adapters` (`:32`) needs no configuration and builds no logger factory.
- The file is read as raw JSON and deserialized with a source-generated context:
  `ConfigurationLoader.TryParse` (`src/WinForward.Configuration/ConfigurationModels.cs:160`) calls
  `JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.WinForwardConfigDto)` (`:165`).
  A `JsonException` becomes one `ConfigDiagnostic` at the exception's JSON path (`:186`).
- Validation is a hand-written walk over the DTO, `ConfigurationLoader.TryValidate` (`:193`), with
  `ConfigurationLimits.Parse` (`src/WinForward.Configuration/ConfigurationLimits.cs`) enforcing
  ranges and emitting warnings. Paths are built by concatenation, producing JSON-shaped locations
  such as `host.rules[6].remoteCidr[0]` (`ConfigurationRules.cs:143`) and `tcpFlowCapacity`
  (`ConfigurationLimits.cs:90,109,129`).
- `ValidatedConfiguration` is immutable for the lifetime of a run; hot reload is explicitly not
  supported (`README.md:275`).

### The key inventory (corrected)

**29 distinct key names across five DTO shapes** — not the fifteen an earlier draft of this plan
claimed. That draft grepped for explicit `[JsonPropertyName]` attributes and missed every key that
`PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` infers from the property name. The full set:

| Shape | Keys |
| --- | --- |
| `WinForwardConfigDto` (13) | `logLevel`, `logFormat`, `socks5Servers`, `localTargets`, `host`, `forwarded`, `proxyUnavailableAction`, `processingFailureAction`, `tcpFlowCapacity`, `setupWorkerCount`, `udpSessionCapacity`, `udpRelayReceiveBufferKb`, `udpSessionIdleSeconds` |
| `RuleDomainDto` (2) | `fallbackAction`, `rules` |
| `Socks5ServerDto` (6) | `name`, `host`, `port`, `username`, `password`, `udpOverTcp` |
| `LocalTargetDto` (3) | `name`, `host`, `port` |
| `RuleDto` (9) | `process`, `adapterId`, `adapterName`, `protocol`, `addressFamily`, `remoteCidr`, `remotePort`, `action`, `target` |

`logLevel` and `logFormat` are deleted by R3, so **27 key names are renamed to PascalCase**, and
because the naming policy that currently infers them is dropped, every property must carry its own
name. The target list is `socks5Servers`, which rules reference by `name`; there is no separate
`targets` key.

### The parser is deliberately strict, and tests pin all three properties

`ConfigurationJsonContext` declares the camelCase policy and
`UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow`
(`src/WinForward.Configuration/ConfigurationModels.cs:83`). Therefore an unknown key is a hard error
(`ConfigurationRejectsUnknownJsonFields`,
`tests/WinForward.Configuration.Tests/ConfigurationValidationTests.cs:269`); a **wrongly cased** key
is an unknown key (`:82` documents that `udpOverTCP` is rejected where `udpOverTcp` is required); and
a wrong value type fails deserialization. `IConfiguration` removes all three by default: it is a
case-**insensitive** store of **strings** whose binder **ignores** keys it does not recognise.

### The logging surface the previous task left behind

`logLevel` (five tokens: `error`, `warn`, `info`, `debug`, `trace`) and `logFormat`
(`auto` | `simple` | `json`) feed `RuntimeLogging.CreateLoggerFactory`, which registers `AddConsole`
with a compiled-in timestamp format and one of MEL's stock formatters. Nothing about the formatter is
operator-configurable, and `LogFormat`, `LogFormatNames` and `RuntimeLogging.ResolveLogFormat` exist
only to serve that knob. Runtime log lines go to stderr and never to stdout, which is a contract of
that task.

## Requirements

- **R1 — one file.** The operator's `appsettings.json` carries both the logging configuration and
  WinForward's own settings. `config.json` stops existing as a separate format and schema.
- **R2 — PascalCase keys.** The 27 surviving key names follow the .NET convention. **Values are not
  renamed**: `action`, `protocol` and `fallbackAction` already parse case-insensitively
  (`ConfigurationRules.cs:103` uses `ToLowerInvariant`, `:214` uses `OrdinalIgnoreCase`), so `"pass"`,
  `"proxy"`, `"udp"` and the CIDR/process literals stay exactly as they are.
- **R3 — standard `Logging` section.** `Logging:LogLevel:*` and `Logging:Console:*` are consumed by
  MEL itself, so `logFormat`, `LogFormat`, `LogFormatNames` and `RuntimeLogging.ResolveLogFormat` are
  deleted. Choosing a formatter becomes `Logging:Console:FormatterName`; leaving it unset preserves
  today's automatic selection.
- **R4 — `validate` is retained** as a driver-free command that reports diagnostics with the existing
  exit codes (`README.md:43`).
- **R5 — diagnostics stay actionable.** A validation error or warning still names the setting that is
  wrong, and the three strictness properties above are preserved.
- **R6 — `validate`'s detection power is an explicit decision.** What `validate` does and does not
  catch is stated in the README, because the move to layered configuration could silently have
  weakened it.
- **R7 — no shipped file may ever be overwritten by an upgrade.** WinForward ships
  `appsettings.example.json`, which is **never loaded**, and defines every default in code. The
  operator's `appsettings.json` is theirs alone.
- **R8 — the service deployment is documented.** The README gains a WinSW section covering where the
  configuration lives, which stream the runtime log occupies, and how to change the formatter.

## Acceptance Criteria

- [ ] `appsettings.json` beside the executable is loaded when present; `--config <path>` is loaded as
      one further JSON layer when given; `run` and `validate` fail with a usage error when neither
      exists.
- [ ] **Nothing ever loads `appsettings.example.json`**, and upgrading WinForward cannot overwrite a
      file the operator configured: the example file is the only configuration-shaped file the build
      copies to the output directory.
- [ ] `appsettings.example.json` **parses under the strict validator**, so the documented example
      cannot rot, and its `Logging` values match the code defaults they document.
- [ ] Every file in `examples/` is migrated to the new shape and still passes
      `ExampleConfigurationsAllValidate`
      (`tests/WinForward.Configuration.Tests/ConfigurationLimitsTests.cs:313`), which loads each of
      them through the real loader.
- [ ] A document with no `WinForward` object fails with a diagnostic that names the convention rather
      than an incidental missing-field error, because that is the first thing a pre-migration
      `config.json` will produce.
- [ ] `Logging:Console:FormatterName` selects the formatter; an unset `FormatterName` still selects
      `json` when stderr is redirected and `simple` otherwise; `Logging:LogLevel:Default` and
      `Logging:LogLevel:<fully-qualified category>` both filter.
- [ ] `Logging:Console:FormatterOptions:TimestampFormat` reaches the rendered line, and a
      configuration that is silent about the formatter still renders the timestamp the previous task
      compiled in (today's behaviour must survive on a config that never mentions it).
- [ ] No configuration value can move a runtime log record to **stdout**, including a configuration
      that sets `Logging:Console:LogToStandardErrorThreshold`.
- [ ] `logFormat`, `LogFormat`, `LogFormatNames` and `RuntimeLogging.ResolveLogFormat` no longer
      exist, and no code reads or writes a `logFormat` setting. The README and the spec **may name
      the retired key** when describing the migration — naming what replaced what is what makes a
      breaking change migratable — but must not present it as a supported setting.
- [ ] An unknown key, a wrongly cased key and a wrongly typed value are each still hard errors from
      `validate`, and each diagnostic names the offending key.
- [ ] All 27 renamed keys are documented in the README, and a `--config` file supplying only some
      settings merges over the defaults rather than replacing them.
- [ ] `validate` reports the configuration sources it loaded, in order, and `run`'s summary reports
      the effective formatter and level.
- [ ] `adapters`' TSV, `validate`'s confirmation line and the usage text keep their exact shapes and
      stdout destination; exit codes stay `0` clean, `1` configuration/driver error, `2` usage error.
- [ ] `dotnet build WinForward.slnx -c Release` is zero-warning with the AOT and trim analyzers on;
      `dotnet test WinForward.slnx -c Release` is green; both quality gates are clean.

## Technical Notes

### Layering model

Sources apply in order, later winning: the executable's `appsettings.json` (optional), then
`--config <path>` (optional), with at least one required. The executable's directory is used rather
than the working directory because that is where the file lives for a service install and because a
Host's content-root convention would resolve against wherever the process happened to start.
`--config` stays supported and documented even though the primary deployment does not need it: it is
one `AddJsonFile` line and it serves running two instances with different settings from one install.
`appsettings.{Environment}.json`, user secrets and an environment-variable layer are deliberately
excluded; each implicit source is another place an operator must look when a rule they thought they
set is not applying. The load order is printed by both commands.

### Key layout, and the wrapper every file carries

WinForward's settings live under one top-level section, `WinForward`, beside `Logging`. A named
section per component is the .NET convention and keeps these keys out of the framework's namespace.
`host` and `forwarded` are the two rule domains, so they read `WinForward:Host:Rules` and
`WinForward:Forwarded:Rules`.

**Every** configuration file is an appsettings-format document and therefore carries the wrapper:
the operator's `appsettings.json`, any `--config` file, and the seven files in `examples/`, which
become documents an operator can copy into place whole. The wrapper is required rather than optional
because accepting a bare section as well would make a misspelled `Winforward` object silently valid
as a flat configuration; requiring it also gives the migration its diagnostic, since a legacy file
has no `WinForward` object at all.

### Diagnosis paths

Because validation re-runs the existing validator over the materialised section (§ below), the path
root is a single string. It becomes `WinForward`, and the validator's existing concatenation keeps
producing JSON-shaped locations with PascalCase segments:
`WinForward.Host.Rules[6].RemoteCidr[0]`, `WinForward.TcpFlowCapacity`. The dot-and-bracket form is
kept rather than `IConfiguration`'s colon form because the operator reads it to find an entry in a
JSON file; the colon form is internal plumbing they never type.

### Why validate the effective configuration, and why the merge is on JSON

`validate` merges the source documents **as JSON** (objects merge key by key; arrays and scalars
replace; an explicit `null` overrides) and runs the **existing** strict path over the merged
`WinForward` object. Merging through `IConfiguration` instead was measured to be incapable of binding
a valid configuration at all: the configuration system stores every scalar as a string, so a valid
`"SetupWorkerCount": 8` came back as `"8"` and failed to bind, a boolean came back as `"True"`, and
an array collapsed to `null`. The JSON merge preserves the operator's types exactly. This preserves all three strictness properties and
validates the *merged* result, which is the only coherent object once `--config` may supply part of
the settings. It also keeps the existing configuration tests meaningful. Cost: one materialisation
step, a small `Utf8JsonWriter` walk over `IConfiguration.GetChildren()` that needs no
`ConfigurationBinder` reflection, and one mechanical rename pass over the validator's path literals.
Two consequences are accepted: paths gain the `WinForward` root, and the old raw-JSON-type
diagnostics for `logLevel`/`logFormat` disappear with those keys — type strictness survives through
the round trip, since a quoted number re-materialises as a JSON string and still fails to bind to an
`int`.

### The formatter surface is free, and its defaults are ours

A probe against `Microsoft.Extensions.Logging.Configuration` 10.0.12 (recorded in
`research/mel-config-probe.md`) shows that
`LoggerFactory.Create(b => { b.AddConfiguration(section); b.AddConsole(); })` needs **no Generic Host**
and that `FormatterName`, `FormatterOptions:{TimestampFormat,SingleLine,ColorBehavior}` and
per-category `LogLevel` all take effect. So R3 requires no new code, and a formatter this project
does not ship becomes selectable by name. A second probe fed a hostile configuration that set
`LogToStandardErrorThreshold = None`: stdout stayed at **0 bytes**, because
`IPostConfigureOptions<T>` runs after every `IConfigureOptions<T>`. That is what makes the stderr
invariant, the timestamp default and the auto-selection default **order-independent**, and it is why
`logFormat` can be deleted with no behavioural loss — "auto" becomes "no `FormatterName` was
configured".

Two packages are added: `Microsoft.Extensions.Configuration.Json` and
`Microsoft.Extensions.Logging.Configuration` (which owns `ILoggingBuilder.AddConfiguration`).
`Microsoft.Extensions.Hosting` is not added; it would bring a DI container, a host lifetime and an
environment abstraction that a service with a hand-written composition root does not want.

### The log level vocabulary changes

`logLevel`'s five lowercase tokens are replaced by MEL's `Trace`/`Debug`/`Information`/`Warning`/`Error`
under `Logging:LogLevel`. MEL's parsing is case-insensitive, so an operator may write either casing;
the README documents the canonical one. This is the only user-visible vocabulary change beyond the
key names.

### Compatibility

No migration shim. The repository has no release tag and no changelog, and because a mis-cased or
unknown key is rejected loudly today, a stale `config.json` cannot half-work. Pointing `--config` at
one produces the missing-`WinForward` diagnostic described above, which names the convention rather
than the stale keys — so the loader must emit that targeted message rather than letting the absence
surface as an incidental missing-field error. The README rewrite is the migration path and
`validate`'s diagnostics are the migration tool. The PascalCase rename is therefore a loud break
rather than a silent one, which is what makes it safe to do now.

### AOT/trim

The new packages extend the closure that must stay clean under `EnableAotAnalyzer`,
`EnableTrimAnalyzer` and `TreatWarningsAsErrors`. `Configuration.Binder` is reflection-based and
arrives transitively, so this is the one unverified area; implementation step 1.3 checks it before
any other work, and the fallback is to bind the few keys by hand and keep `AddConsole(configure)`.

## Out of scope

- Any sink beyond the console: no file sink, no Serilog or OpenTelemetry, no rotation. WinSW already
  owns log rolling.
- `appsettings.{Environment}.json`, user secrets and environment-variable layering.
- Native Windows Service installation, and configuration hot reload (`README.md:275`).
- A compatibility shim for the old `config.json` shape.
- The `adapters` command and the packet path.
