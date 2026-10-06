# Implementation plan — appsettings.json as the single configuration surface

Ordered so the riskiest unknown retires first: if the new packages cannot stay clean under the
trim/AOT analyzers, the rest of the plan changes shape, and that is cheaper to learn in step 1 than
in step 8.

## Step 1 — Retire the AOT/trim risk before writing the implementation

1.1 Add `Microsoft.Extensions.Configuration.Json` and `Microsoft.Extensions.Logging.Configuration` to
`Directory.Packages.props` (10.0.12, centrally pinned like the logging packages).

1.2 Wire the smallest working version of `RuntimeLogging.CreateLoggerFactory(IConfiguration)` in
`src/WinForward.Runtime/Logging/RuntimeLogging.cs`: `AddConfiguration(section)` + `AddConsole()` + the
single `PostConfigure<ConsoleLoggerOptions>` action from `design.md` §2.3.

1.3 **Checkpoint — go/no-go.** `dotnet build WinForward.slnx -c Release` must stay zero-warning with
`EnableAotAnalyzer` / `EnableTrimAnalyzer` / `TreatWarningsAsErrors` on. If it does not, stop and
record the analyzer output here before going further. The recorded fallback is to bind
`Logging:LogLevel` and `Logging:Console:FormatterName` by hand and keep `AddConsole(configure)`,
trading some framework fidelity for analyzability.

## Step 2 — The configuration model

2.1 New loader in `src/WinForward.Configuration/`: build the two layers per `design.md` §2.1 —
`AppContext.BaseDirectory/appsettings.json` (optional), then `--config` (optional) — require at least
one, and expose the loaded sources in order as data the CLI can print. **Nothing else may be
registered as a source** (risk R-3).

2.2 Materialise the `WinForward` section back to JSON (`IConfiguration.GetChildren()` →
`Utf8JsonWriter`, no binder reflection) and feed the result to the existing
`ConfigurationLoader.TryParse` / `TryValidate`. Handle and test the three edge cases in `design.md`
§4: an empty section, a sparse array, and a numeric-looking value that must stay a JSON string.

2.3 Rename the DTO's 27 keys to PascalCase: `[JsonPropertyName]` on **every** property in all five
shapes, and remove `PropertyNamingPolicy` from `JsonSourceGenerationOptions` so nothing is inferred.

2.4 Re-case the validator's literal path segments and root them at `WinForward`
(`ConfigurationModels.cs`, `ConfigurationRules.cs`, `ConfigurationTargets.cs`,
`ConfigurationLimits.cs`), producing paths of the form `WinForward.Host.Rules[6].RemoteCidr[0]`.

## Step 3 — Logging integration

3.1 Delete `LogFormat`, `LogFormatNames`, `logFormat` parsing and `RuntimeLogging.ResolveLogFormat`.
Keep the auto rule, the timestamp format and the stderr threshold as the post-configure defaults from
step 1.2.

3.2 Add `appsettings.example.json`: the operator-facing example from `design.md` §2.2, with
`CopyToOutputDirectory`, plus the two tests from risks R-2 (it parses under the strict validator; its
`Logging` values match the code constants).

3.3 Update the run summary to report the effective formatter and level, and to list the configuration
sources.

## Step 4 — CLI surface

4.1 `Program.cs`: `--config` optional for `run` and `validate`; a usage error when no source exists;
`validate` prints the loaded sources before its existing confirmation line and warnings.

4.2 Keep every exit code and every stdout line's shape (`README.md:43`): `adapters`' TSV, the
`validate` confirmation and the usage text are contracts from the previous task.

## Step 5 — Documentation

5.1 Rewrite the README's configuration section around the new file, the two layers, the full
27-key table, the `Logging` keys this project honours, and the explicit note that
`LogToStandardErrorThreshold` is not configurable, with the reason.

5.2 Add the WinSW service section (`design.md` §6): the service XML, where the configuration lives,
the `.err.log` stream, and the JSON-by-default consequence with the one-line formatter change.

5.3 Record in `.trellis/spec/backend/logging-guidelines.md` that the factory takes configuration,
that the destination is an invariant, that an absent `FormatterName` means auto, and that defaults
live in code with `appsettings.example.json` as their documentation. Retire the `logFormat`
paragraphs.

## Step 6 — Tests

6.1 Configuration tests: the PascalCase schema, the new diagnostic paths, and the three strictness
properties still failing loudly. The existing 118 tests are the baseline and each must still mean
something.

6.2 Layering: a partial `--config` file merges over the exe-directory file rather than replacing it.

6.3 Hostile configuration: `LogToStandardErrorThreshold` cannot move a record to stdout, and an
absent `FormatterName` still means auto. With no configuration at all, the timestamp is still present.

6.4 The previous task's console-shape test keeps passing unchanged — the regression guard for the
stderr contract.

## Validation commands

```
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-appsettings.xml WinForward.slnx
```

## Risky files and rollback points

- `src/WinForward.Runtime/Logging/RuntimeLogging.cs` — the composition point; step 1.3 is the
  go/no-go.
- `src/WinForward.Configuration/ConfigurationModels.cs` — the strict deserializer R5 protects.
- `src/WinForward.Configuration/ConfigurationRules.cs` — the validator whose 27 key literals and path
  segments both move.
- `src/WinForward.Cli/Program.cs` — the stdout contracts from the previous task.
- Rollback: a single revert of the task's commits; the previous task's commit is the known-good tree.

## Known pre-existing failures (do not re-investigate)

Recorded in the archived `10-06-mel-logging` task: the UDP UoT two-flow lifecycle test fails in
isolation on the pre-migration baseline too, and the Socks5 deferred-handshake test is intermittently
flaky. Both pass inside a full-suite run.

## Check phase outcome

An independent `trellis-check` agent verified the frozen tree, then the main session re-ran the gates
itself. Final state: build `0 Warning(s) 0 Error(s)`; `dotnet test WinForward.slnx -c Release` green
across 13 projects with **1297 tests**; `dotnet format --severity info --verify-no-changes` exit 0
with empty output; `jb inspectcode -e=HINT` **0 `<Issue>` elements** on 393 files. The test baseline
was measured twice and independently — `git archive HEAD` into a scratch directory gives 1268 — so
the change adds 29 cases net.

All fourteen acceptance criteria passed, each with a command or a captured output rather than an
assurance. The check agent ran four targeted falsification attempts rather than reading the code:

- **The formatter shape theory was half falsified.** Adding a name nothing registers fails it, but
  **removing `systemd` from the list left the entire suite green**: the shape theory iterates the
  list, so a missing name simply loses a case. The implementation agent's claim of having
  mutation-checked "both directions" was therefore wrong in one of them. Closed by a new test that
  resolves the formatters the console registration actually provides and pins the list to that set.
  R-7 in `design.md` now names both directions.
- The missing-`--config` classification, `validate` building the logger factory, and the seven
  `examples/*.json` going through the real loader were all confirmed, the last two **non-vacuously**:
  a deliberately mis-cased key in `dns-policy.json` makes the example test fail, and pointing the
  first layer at `appsettings.example.json` makes the decoy test fail.

Two defects the check agent found and fixed, both real: **a documentation/behaviour mismatch** where
four sentences claimed every record carries a timestamp while `systemd` — newly selectable, and
documented as such — renders journald's shape with none (the documentation was corrected, not the
behaviour; see `design.md` §2.3), and **a lost test case**: the retired suite pinned a blank
`logLevel` and the replacement did not.

### Judgment calls, ruled

- **`systemd` has no timestamp** — accepted as documented. journald stamps every record itself, so
  adding one would duplicate it and change an accepted formatter's render.
- **Keep the registration-set test** — it is the second direction of R-7, not scope creep.
- **A numeric `LogLevel` (`"5"`) is accepted** — `Enum.TryParse` is exactly MEL's own acceptance set,
  and `validate` must never reject a configuration `run` would accept.
- **Keep the generic `Invalid JSON configuration.` message.** It is pre-existing (HEAD's `TryParse`
  catch used the same template) and the path is what names the key. The framework's own reason was
  measured and rejected on evidence: it never echoes the offending value, so privacy would allow it,
  but it names only the root DTO type and its line/byte positions now point into the re-serialised
  section rather than the operator's file — appending it would be actively misleading.

### Pre-existing problems recorded, not fixed here

- **A flaky allocation gate.** `SweepAllocationGateTests.FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes`
  failed once in roughly six full-solution runs with 64 bytes against an expected 0, and passes 6/6
  in isolation and 3/3 as a whole project. The file is untouched by this task (its last change is the
  previous task's commit). A thread-local allocation measurement is only sound while nothing else
  runs on the measuring thread, and xUnit schedules tests on thread-pool threads, so the likely
  explanation is another test's allocation landing inside the window — a hypothesis, not a finding.
  It matters because a zero-allocation gate that can cry wolf under load can equally mask a real
  regression; it deserves its own investigation.
- **`SetupExecutorTests.SetupExecutorRejectsBeyondRingCapacityAndRecyclesTheRejectedItem`** threw a
  `NullReferenceException` once in four full-suite runs and never in eight isolated runs; untouched by
  this task. Recorded alongside the two failures already noted in the archived `10-06-mel-logging`.
- **`ConfigurationValidationTests.cs` is 491 effective lines against the 400 limit**
  (`directory-structure.md:81`). This task reduced it from 595 and introduced no new violation.
