# Measured behaviour of the Microsoft.Extensions.Logging configuration surface

Three probes were run against `Microsoft.Extensions.*` 10.0.12 before this task's design was
written, because each one decides a design choice rather than merely informing it. They were run as
a self-contained project (`Microsoft.Extensions.Configuration`, `.Logging`, `.Logging.Console`,
`.Logging.Configuration`), not against the product.

Run them again rather than trusting this file if the package versions move.

## 1. The standard `Logging` section needs no Generic Host

```csharp
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:WinForward.Runtime.FlowDispatcher"] = "Debug",
        ["Logging:Console:FormatterName"] = "simple",
        ["Logging:Console:FormatterOptions:TimestampFormat"] = "zzz yyyy-MM-dd HH:mm:ss.fff ",
        ["Logging:Console:FormatterOptions:SingleLine"] = "true",
        ["Logging:Console:FormatterOptions:ColorBehavior"] = "Disabled",
    })
    .Build();

using var factory = LoggerFactory.Create(b =>
{
    b.AddConfiguration(configuration.GetSection("Logging"));
    b.AddConsole();
});
```

Observed: `+08:00 2026-10-06 12:09:44.513 info: WinForward.Runtime.FlowDispatcher[1498124477] info line from flow`

Every key took effect, and a second logger at category `WinForward.Runtime.SomeOtherOwner` produced
nothing because `Default` was `Warning` while the `FlowDispatcher` category had its own override.
**Consequences:** the whole formatter surface is free — nothing has to be built for it, including the
ability to select a formatter this project does not ship. Per-category level overrides work and are
only worth having because categories are fully-qualified type names. `Microsoft.Extensions.Hosting`
is not needed.

**Two things this probe corrected:** `ILoggingBuilder.AddConfiguration` lives in
`Microsoft.Extensions.Logging.Configuration`, not in `Microsoft.Extensions.Logging`; and with no
`TimestampFormat` configured the output has **no timestamp at all**, because `AddConsole()`'s default
is empty while this project currently compiles the timestamp in.

## 2. A code-level invariant beats the configuration

The product invariant is that a runtime log line never shares stdout with the `adapters` TSV or the
`validate` confirmation. `ConsoleLoggerOptions.LogToStandardErrorThreshold` defaults to `None`, so a
configuration that sets it — or a missing default file — would move logs to stdout. The probe fed a
**hostile** configuration:

```csharp
["Logging:Console:LogToStandardErrorThreshold"] = "None",   // tries to move logs to stdout
// and no FormatterName, so the auto rule must apply

using var factory = LoggerFactory.Create(builder =>
{
    builder.AddConfiguration(section);
    builder.AddConsole();
    builder.Services.PostConfigure<ConsoleLoggerOptions>(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
        if (section.GetSection("Console")["FormatterName"] is null)
        {
            options.FormatterName = Console.IsErrorRedirected ? ConsoleFormatterNames.Json : ConsoleFormatterNames.Simple;
        }
    });
});
```

Observed with stdout and stderr captured separately: `stdout bytes=[0] stderr bytes=[218]`, and the
record was JSON because stderr was redirected.

**Consequences:** `IPostConfigureOptions<T>` runs after every `IConfigureOptions<T>`, so the
invariant and the auto default are **order-independent** — there is no need to reason about whether
`AddConsole(configure)` was registered before or after `AddConfiguration`. This is what lets
`logFormat` be deleted with no loss of behaviour: "auto" becomes "the operator configured no
`FormatterName`", and the auto rule stays as the default.

## 3. `IConfiguration` cannot round-trip scalar types

Found while writing the documentation, then reproduced here, because it invalidated this task's first
design. `IConfiguration` stores **every** JSON scalar as a string, so materialising a section back to
JSON destroys the types the strict validator depends on:

```
IConfiguration view: TcpFlowCapacity='4096'  Port='1080'  UdpOverTcp='True'
materialised -> {"SetupWorkerCount":"8","Socks5Servers":null,"TcpFlowCapacity":"4096"}
    deserialize: FAILS -> The JSON value could not be converted to Nullable<Int32>.
                          Path: $.SetupWorkerCount
```

Three separate failures in that one line: a valid `"SetupWorkerCount": 8` cannot bind once it is the
string `"8"`; a boolean comes back as `"True"`, which `JsonNumberHandling.AllowReadingFromString`
does not repair because the gap is boolean-from-string, not number-from-string; and the
`Socks5Servers` **array collapsed to `null`**, because a section node that has children and no direct
value reports `Value == null` with `Exists() == false`, so a naive `GetChildren()` walk drops it.

**The resolution is to merge the JSON documents instead of the flattened key view**, which preserves
types exactly and needs no repair:

```
json-merge -> {"TcpFlowCapacity":4096,"Socks5Servers":[{"Name":"main","Port":1080,"UdpOverTcp":true}],"SetupWorkerCount":8}
    bound OK: tcp=4096 workers=8 port=1080 uot=True
quoted number "4096": FAILS -> Path: $.TcpFlowCapacity
explicit null survives the merge: True
```

Merge rules: objects merge key by key, **arrays and scalars replace wholesale**, an explicit `null`
overrides. Array replacement is deliberate — a `--config` file that lists three rules should produce
three rules rather than the first three of the default file's list, which is what index-wise key
merging does. Note also that `GetSection("WinForward")` finds a section written `winforward` while
child keys keep their original casing, which is the property that makes a mis-cased key inside the
section a hard error once the section is validated strictly.

The same merged document still feeds MEL through `AddJsonStream`, so the framework's `Logging` section
and the strict domain schema are two views of one effective configuration.

## 4. Not verified here

- Whether the new packages stay clean under `EnableAotAnalyzer` / `EnableTrimAnalyzer` with
  `TreatWarningsAsErrors`. `Configuration.Binder` is reflection-based and arrives transitively, so
  the binding path
  (`LoggerProviderOptions.RegisterProviderOptions<ConsoleLoggerOptions, ConsoleLoggerProvider>`) has
  to be shown statically analyzable by the build. This is implementation step 1.3, deliberately the
  first thing to check.
- A real Native AOT publish remains out of reach on this machine, for the reason recorded in the
  archived `10-06-mel-logging` task (no `clang`, `win-x64` target).
