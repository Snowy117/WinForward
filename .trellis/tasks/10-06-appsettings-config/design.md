# Design — appsettings.json as the single configuration surface

## 1. Approach

Adopt `Microsoft.Extensions.Configuration` as the source of the merged settings, adopt the standard
`Logging` section for logging, and keep the existing strict validator by validating the **effective
merged** configuration. No Generic Host, and **no configuration file is ever shipped as a loaded
default** — every default lives in code and `appsettings.example.json` exists only to document them.

Three measurements drove this design, all against `Microsoft.Extensions.*` 10.0.12:

| Measurement | Result |
| --- | --- |
| `LoggerFactory.Create(b => { b.AddConfiguration(section); b.AddConsole(); })` with `Logging:Console:FormatterName` / `FormatterOptions:*` / `LogLevel:<category>` | all take effect; **no Host required** |
| `builder.Services.PostConfigure<ConsoleLoggerOptions>(...)` against a configuration that sets `LogToStandardErrorThreshold = None` | stdout **0 bytes**; the code default beat the configuration |
| The same post-configure with no `FormatterName` configured | the auto rule still selected the formatter |

## 2. Configuration model

### 2.1 Sources and order

Applied in order, later wins, merged **as JSON documents** rather than through `IConfiguration`'s
flattened key view (§4 records the measurement that ruled the alternative out):

1. `appsettings.json` beside the executable (`AppContext.BaseDirectory`), **optional**, and **never
   shipped by this project** — it belongs to the operator.
2. `--config <path>` when given, **optional**.
3. **At least one source must exist**; `run` and `validate` otherwise fail with a usage error,
   preserving today's "you must configure me" behaviour now that both files are optional.

**Merge rules.** A recursive object merge: objects merge key by key, **arrays and scalars replace
wholesale**, and an explicit JSON `null` overrides a value exactly as it would have before. Array
replacement is deliberate — a `--config` file that lists three rules should produce three rules, not
the first three of the default file's list, which is what index-wise key merging would do.

**Every configuration file is an appsettings-format document**, so WinForward's settings sit under a
`WinForward` object in each of them, including the `examples/*.json` files, which become files an
operator can copy into place whole. A document with no `WinForward` object produces a diagnostic that
names the convention, because that is exactly what a pre-migration `config.json` looks like and it is
the first thing a migrating operator will meet.

The same merged document is fed to `IConfiguration` through `AddJsonStream`, so MEL's `Logging`
section and the strict domain schema are two views of one effective configuration and cannot
disagree. Nothing else is ever registered as a configuration source.

The executable's directory is used rather than the working directory because that is where the file
lives in the service install this project is actually deployed as — WinSW sets `workingdirectory` to
the same `%BASE%` that holds the executable, so both resolutions agree there and only the
executable-relative one survives a differently-started process.

`--config` is **not** required by that deployment and the README says so; it stays because it is one
`AddJsonFile` line and it covers running two instances with different settings from one install.

Deliberately excluded: `appsettings.{Environment}.json`, user secrets, environment variables. Each
implicit source is another place an operator has to look when a rule they thought they set is not
applying. The load order is printed by `validate` and by the run summary.

### 2.2 Schema

One top-level section beside `Logging:`, every key PascalCase, values untouched:

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information" },
    "Console": { "FormatterName": "json" }
  },
  "WinForward": {
    "ProxyUnavailableAction": "block",
    "ProcessingFailureAction": "block",
    "Socks5Servers": [ { "Name": "main", "Host": "::1", "Port": 30890, "UdpOverTcp": true } ],
    "LocalTargets": [ { "Name": "dns-in", "Host": "::1", "Port": 53 } ],
    "Host": {
      "FallbackAction": "pass",
      "Rules": [
        { "Protocol": ["udp"], "RemotePort": ["67", "68"], "Action": "pass" },
        { "Process": ["D:\\SteamLibrary\\..."], "Action": "pass" },
        { "Action": "proxy", "Target": "main" }
      ]
    },
    "Forwarded": {
      "FallbackAction": "pass",
      "Rules": [ { "AdapterId": ["{xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx}"], "Action": "proxy", "Target": "main" } ]
    }
  }
}
```

**27 key names across five DTO shapes are renamed** (`prd.md` has the full table). Two mechanical
rules make this safe rather than delicate:

- `PropertyNamingPolicy` is removed from `JsonSourceGenerationOptions` and **every** property carries
  its own `[JsonPropertyName]`. Without the policy nothing is inferred, so a forgotten property fails
  loudly at the validator instead of silently changing a key name.
- Rule values are **not** renamed. `ParseAction` lowercases before matching
  (`ConfigurationRules.cs:103`) and `ParseProtocol` uses `OrdinalIgnoreCase` (`:214`), so `"pass"`,
  `"proxy"`, `"udp"` and every CIDR, port and process literal stay exactly as they are.

### 2.3 Logging

`logFormat`, `LogFormat`, `LogFormatNames` and `RuntimeLogging.ResolveLogFormat` are **deleted**. The
standard section replaces them with no loss of behaviour:

| Today | After | Mechanism |
| --- | --- | --- |
| `"logFormat": "auto"` | omit `Logging:Console:FormatterName` | post-configure applies the auto rule |
| `"logFormat": "json"` / `"simple"` | `"FormatterName": "json"` / `"simple"` | framework binding |
| `logLevel` tokens (`error`…`trace`) | `Logging:LogLevel:Default` (`Error`…`Trace`) | framework binding |
| compiled-in timestamp | code default, overridable by `FormatterOptions:TimestampFormat` | post-configure default + binding |
| logs always on stderr | **not configurable** | post-configure forces `Trace` |
| — | `Logging:LogLevel:<category>` per-category overrides | framework binding; worth having only because categories are fully-qualified type names |
| — | `SingleLine`, `ColorBehavior`, `JsonWriterOptions:*`, `IncludeScopes` | framework binding, free |

`RuntimeLogging.CreateLoggerFactory` keeps its role as the single composition point and changes
shape: it takes the `Logging` section, calls `AddConfiguration(section)` then `AddConsole()`, and
registers post-configure actions that force `LogToStandardErrorThreshold = Trace` and supply the
timestamp format, the JSON writer's relaxed encoder and the formatter name **only when the operator
did not configure them**.

**It takes three actions, not one, and the first draft of this section was wrong.** The timestamp
cannot ride along with the console logger's own options: `ConsoleLoggerOptions.TimestampFormat` is
`[Obsolete]`, which under `TreatWarningsAsErrors` is a build error. The measured alternatives are
`PostConfigure<SimpleConsoleFormatterOptions>` and `PostConfigure<JsonConsoleFormatterOptions>`, one
per formatter whose options carry the value the operator may set, plus the
`PostConfigure<ConsoleLoggerOptions>` that owns the stderr invariant and the auto formatter name.
Order-independence is preserved for all three for the reason probe 2 gives, and the operator's
`FormatterOptions:TimestampFormat` still wins.

**The timestamp default covers `simple` and `json`, not `systemd`**, and that is deliberate rather
than an omission. An independent check found the earlier claim here — that the timestamp this project
compiles in is universal — contradicted the shipped behaviour and four documentation sentences;
the documentation was corrected rather than the behaviour. `systemd` renders journald's own shape and
carries no timestamp because journald stamps every record itself, so adding one would duplicate it and
would change the rendered shape of a formatter that was never selectable before this task (the retired
`logFormat` could only choose `simple` or `json`). The encoder default is not cosmetic: the pre-migration
code wrote with `UnsafeRelaxedJsonEscaping`, and without it a non-ASCII process path or adapter name
renders as `\uXXXX` escapes — a regression an operator would notice before any log line was read. `IPostConfigureOptions<T>` runs after every `IConfigureOptions<T>`, so the three defaults are
order-independent and the framework's own binding still wins wherever the operator was explicit.

The stderr destination stays an invariant rather than a setting because the previous task's contract
is that a runtime log line never shares stdout with the `adapters` TSV or the `validate`
confirmation. The README must say so, with the reason, since a user who sets that key would
otherwise expect it to work.

### 2.4 `appsettings.example.json`

Shipped, copied to the output directory, and **loaded by nothing**. It is the documentation surface
for defaults that now live in code, and it is a test fixture: a test feeds it to the strict validator
so the example cannot rot, and a second test compares the `Logging` values it shows against the code
constants they document.

This is the shape that makes R7 hold. The deployment keeps its configuration in the same directory as
the executable, so a shipped-and-loaded default file would be overwritten by the next upgrade. An
example that is never read cannot overwrite anything, and the operator's `appsettings.json` is
theirs alone.

## 3. Logging categories

Unchanged from the previous task: the category is the fully-qualified name of the type that owns the
logger, created at the composition root. The payoff arrives now —
`Logging:LogLevel:WinForward.Runtime.UdpProxy.UdpProxyCoordinator` becomes an operator control, which
is the supported answer to "this subsystem is noisy".

## 4. Validation

`validate` becomes "validate the effective configuration": build the merged document (§2.1) and run
the **existing** strict path over its `WinForward` object — `ConfigurationLoader.TryParse` (still
owning `UnmappedMemberHandling.Disallow`, the per-property names and the path diagnostics), then
`ConfigurationLoader.TryValidate` and `ConfigurationLimits.Parse`.

### Why the merge is done on JSON, not on `IConfiguration`

The first draft of this design materialised the `WinForward` section back to JSON out of
`IConfiguration`. A probe proved that incapable of binding **any valid configuration**, because the
configuration system stores every scalar as a string:

```
IConfiguration view: TcpFlowCapacity='4096'  Port='1080'  UdpOverTcp='True'
materialised -> {"SetupWorkerCount":"8","Socks5Servers":null,"TcpFlowCapacity":"4096"}
    deserialize: FAILS -> The JSON value could not be converted to Nullable<Int32>.
                          Path: $.SetupWorkerCount
```

`JsonNumberHandling.AllowReadingFromString` repairs numbers but not booleans, whose provider value is
`"True"` with a capital T, and the array collapsed to `null` because a section node with children and
no direct value reports `Value == null`. Merging the JSON documents themselves avoids all three
problems and is strictly better than any repair:

```
json-merge -> {"TcpFlowCapacity":4096,"Socks5Servers":[{"Name":"main","Port":1080,"UdpOverTcp":true}],"SetupWorkerCount":8}
    bound OK: tcp=4096 workers=8 port=1080 uot=True
quoted number "4096": FAILS -> Path: $.TcpFlowCapacity
explicit null survives the merge: True
```

So all three strictness properties survive **exactly**, including a quoted number where a number
belongs, and the merged result is still the object under validation.

**Path shape.** The materialisation feeds the section as the validator's root, so the root string is
the only new thing: `WinForward`. The validator's existing concatenation then yields
`WinForward.Host.Rules[6].RemoteCidr[0]` and `WinForward.TcpFlowCapacity`. The dot-and-bracket form is
kept over `IConfiguration`'s colon form because an operator reads it to find an entry in a JSON file,
and the colon form is plumbing they never type. The mechanical pass is to re-case the validator's
literal path segments and prefix the root.

**Accepted consequences.** Paths gain a root segment. The old raw-JSON-type diagnostics for
`logLevel`/`logFormat` disappear with those keys; type strictness survives the round trip, because a
quoted number re-materialises as a JSON string and still fails to bind to an `int`.

**Merge implementation.** `JsonNode.Parse` per source, then a recursive object merge as described in
§2.1, then `ToJsonString()` of the `WinForward` object into the existing deserializer. No
`ConfigurationBinder` reflection is involved, which matters for §7. Edge cases to handle and test:
a key present in both layers, an array in the override replacing the default's array entirely, an
explicit `null` overriding a value, and a document with no `WinForward` object at all.

## 5. CLI surface

| Command | Today | After |
| --- | --- | --- |
| `run --config <path>` | `--config` required | both sources optional, at least one required |
| `validate --config <path>` | `--config` required | same; and `validate` alone now works beside the executable |
| `adapters` | no configuration | unchanged |

Both commands print the sources they loaded, in order. The run summary reports the effective
formatter and level, so "why is my setting not applying" is answerable from the output alone.

## 6. Documentation

- The README's configuration section is rewritten around the new file, the two layers, the
  `WinForward` section with all 27 keys, the `Logging` keys this project honours, and an explicit
  note that `LogToStandardErrorThreshold` is not configurable and why.
- The README gains a **WinSW service section**, because that is how the tool is actually run: the
  service XML as a starting point, the configuration's location beside the executable, the fact that
  WinSW writes stdout and stderr to different files so the runtime log is the `.err.log` family, that
  such a log is JSON by default because a redirected stream selects it, and the one-line change for an
  operator who prefers the human format there.
- `README.md:370`'s claim that service installation is out of scope stays true of *native* service
  support and gains the WinSW pointer.

## 7. Dependencies and AOT

Added: `Microsoft.Extensions.Configuration.Json` and `Microsoft.Extensions.Logging.Configuration`
(which owns `ILoggingBuilder.AddConfiguration`), pinned centrally at 10.0.12.
`Microsoft.Extensions.Configuration` and `Configuration.Binder` arrive transitively.
`Microsoft.Extensions.Hosting` is **not** added.

**This is the one unverified area.** `Configuration.Binder` is reflection-based, and the new packages
extend the closure that must stay clean under `EnableAotAnalyzer` / `EnableTrimAnalyzer` /
`TreatWarningsAsErrors`. The binding path used here is a fixed generic
(`LoggerProviderOptions.RegisterProviderOptions<ConsoleLoggerOptions, ConsoleLoggerProvider>`), so it
is expected to be statically analyzable, but the build gate is the evidence, and implementation step
1.3 checks it before any other work. A real AOT publish stays out of reach on this machine for the
reason recorded in the archived `10-06-mel-logging` task.

## 8. Trade-offs recorded

| Decision | Cost accepted |
| --- | --- |
| Named `WinForward:` section + dot/bracket paths | every path gains a root segment; the validator's literals need re-casing |
| Example file instead of a loaded default | defaults are invisible in the file tree; bought back by `validate` printing effective values, the README table, and a test that the example parses |
| No environment-variable or environment-specific layers | less flexible; every source stays explicit and printable |
| Validate by materialising back to JSON | one extra pipeline step and a hand-written writer, in exchange for keeping the existing strict validator and its tests |
| stderr destination is not configurable | deviates from "configuration is king"; required by the stdout contract, documented with the reason |
| `--config` kept though the primary deployment needs no flag | one redundant affordance; pays for the two-instances case |
| No Generic Host | no DI/`IOptions` conveniences; the hand-written composition root stays |
| Values not renamed | key names and value spellings use different casing conventions in one file — which is also what stock .NET looks like (`"LogLevel": "Information"` is a value, not a key) |

## 9. Risks

- **R-1 — the new packages break the trim/AOT analyzer pass.** Mitigation: step 1.3 runs before any
  other work; the recorded fallback is to bind the few keys by hand and keep `AddConsole(configure)`.
- **R-2 — the example file drifts from the schema.** Mitigation: a test feeds it to the strict
  validator and compares its `Logging` values against the code constants. The seven files in
  `examples/` are covered by the existing `ExampleConfigurationsAllValidate`
  (`tests/WinForward.Configuration.Tests/ConfigurationLimitsTests.cs:313`), which fails until they are
  migrated — a useful forcing function rather than an oversight.
- **R-3 — something accidentally loads the example file.** Mitigation: an acceptance test asserts the
  only path ever registered is `appsettings.json` or the operator's `--config`; the implementation
  covers it with a decoy `appsettings.example.json` written into the search directory.
- **R-7 (closed in both directions) — the accepted-formatter list drifts from what the build
  registers.** The list is built from
  the framework's `ConsoleFormatterNames` constants, which removes typos but not the drift: a name
  could be listed that nothing registered, and MEL's silent fallback would hide it. Mitigation: a
  test per accepted name asserting the rendered record has **that formatter's shape**, plus a test
  that resolves the console formatters the registration actually provides and pins the list to that
  set. A test that merely checks a name is a member of the list is a tautology and does not count.

  The direction that was nearly missed is worth recording: the shape theory alone catches a name that
  nothing registered, but **not** a registration that the list omits — removing `systemd` from the
  list left the whole suite green until the second test existed. Two directions, two tests.
- **R-4 — a partial `--config` file merges surprisingly.** Inherent to layering; mitigated by printing
  the sources and their order in both commands.
- **R-5 — the path-literal rename touches many validator messages.** Mitigation: one mechanical pass,
  then the existing configuration tests, whose paths change in exactly two dimensions (root and
  casing) and are therefore reviewable as a diff.
- **R-6 — losing the raw-JSON-type diagnostics for two deleted keys.** Accepted, documented in §4.
