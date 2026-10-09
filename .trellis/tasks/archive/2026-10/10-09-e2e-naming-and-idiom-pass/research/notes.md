# C2 · durable findings

> Written by the implement dispatch of 2026-10-09. Measurements are against `HEAD` `0baf2c6`
> (+ the C2 diff). Anything here that contradicts `research/02` or the plan review is marked as such.

## Falsified by measurement: the four "unused" `using`s are used

`research/02` B3.8/B3.9 and `implement.md` N0.2 list four unused usings. Deleting them fails the build:

| file | line deleted | what breaks |
|---|---|---|
| `benchmarks/WinForward.E2E/Program.cs` | `using System.Runtime.InteropServices;` | `CS0246: PosixSignalRegistration` (`Program.cs:161`) |
| `Analysis/Checks/ControlDrift.cs` | `using System.Runtime.CompilerServices;` | `CS0246: ConditionalWeakTable<,>` (`:75`) |
| `Analysis/Findings/LedgerViews.cs` | same | `CS0246: ConditionalWeakTable<,>` (`:152`) |
| `Analysis/Loading/LedgerLoader.cs` | same | `CS0246: ConditionalWeakTable<,>` (`:18`) |

The audit had checked a *subset* of each namespace's types (`RuntimeInformation`/`Marshal`/
`LibraryImport` for `InteropServices`; `MethodImpl`/`Caller*`/`Unsafe` for `CompilerServices`) and missed
`PosixSignalRegistration` and `ConditionalWeakTable<,>`. The research itself names
`PosixSignalRegistration` at `research/02:40` without connecting it to the using it needs.
**Decision: the four usings stay.** N0.2 is void; AC6's "4 unused usings cleared" cannot be met as written.

## `Measured<T>` — the shape, and the two boundary rulings

`Analysis/Model/Measured.cs`: `internal readonly record struct Measured<T>(T Value, string? Reason)`.

The type argument is the *nullable* form (`Measured<double?>`, `Measured<JsonElement?>`,
`Measured<bool?>`, `Measured<Comparison?>`). An unconstrained `T?` does **not** mean `Nullable<T>` — it
is `T` with a nullable annotation — so the alternative (`where T : struct` + `T? Value`, giving
`Measured<double>`) would compile the *first* attempt into a non-nullable `double Value` and could not
carry `Comparison`. The review's own sketch (`Reading<T>(T Value, string? Reason)`, "used as
`Reading<string>`") is this shape.

Converted: the 14 literal `(X? Value, string? Reason)` **declarations** (`ArmAccess` ×5,
`MetricCatalogue` ×4, `LedgerClientCounts` ×4, `GateValidity` ×1) + the 4 non-return positions
(`MetricSpec.cs:40`, `ControlDrift.cs:286`, `ArmDenominatorTable.cs:100` → `TableArmDenominator.cs:100`,
`MetricCatalogue.cs:229`'s parameter) + `BootstrapPair.Draw`'s return.

**Ruling 1 — `BootstrapPair.cs:41` is converted** (`(Comparison? Comparison, string? Error)` →
`Measured<Comparison?>`). The dispatch names it in scope; the review (AC2b) left it to the implementer.
Cost: `Comparison` → `Value` and `Error` → `Reason` are member-name changes. They are not observable:
no `nameof`, no string key and no reflection reads either name, and the oracle is unchanged. Its two
consumers deconstruct positionally (`MetricComparisons.cs:219`, `ControlDrift.cs:198`) and needed no edit.

**Ruling 2 — `CpuDetail.cs:58` keeps its 3-member tuple**
`(double? Value, string? Reason, CpuDiagnostics Diagnostics)`. A `Measured<T>` cannot carry the third
member without a second record type or a nested tuple, and the review's S3 says "do not extend the
record". `MetricCatalogue.cs:291` still deconstructs `var (value, why, _)`. Recorded as *not this
pattern*, not as an oversight.

Not converted, deliberately: `ArmAccess.ArmResult` (`(JsonElement? Result, string? Reason)` — first
member renamed), `IdentityChecks.cs:93,257,284,309` (`Detail`), `MetricCell.cs:42` (non-nullable
`Status`) — all are the review's AC2b list, and converting them renames `Result`/`Detail`/`Status`,
which the plan forbids as a semantic change.

Deconstruction sites needed **zero** edits: a positional `record struct` generates `Deconstruct`, so
`var (value, why) = …` and `.Value` / `.Reason` reads kept compiling. The 22-site churn the review
expected did not materialise.

## N3 — `ArmDenominatorTable` needed a *type* rename, not only a file rename

`git mv ArmDenominatorTable.cs TableArmDenominator.cs` alone would have left the type `ArmDenominatorTable`
in a file named `TableArmDenominator.cs`, violating `directory-structure.md:125` (filename = primary
type) — the very rule the rename exists to restore. All 17 sibling files declare `internal static class
Table*`, so the type was renamed too: declaration + `TableCpu.cs:234`, 2 references. `ArmDenominators`
(the record the class operates on) stays where it is: a record plus the class that operates on it is one
of the three allowed co-location clusters.

The other four moves are content-free renames of a partial shard (`LedgerFindings.*.cs`) or of a file
whose type already matched (`JsonRate.cs`, `JsonPerSecond.cs`).

## Path literals that the splits broke (and were updated)

- `.trellis/spec/backend/measurement-record-contract.md:115` pointed at `Client/ArmContext.cs:157` for
  `EmptyMetrics`; now `Client/EmptyMetrics.cs:11`.
- `benchmarks/WinForward.E2E/README.md:37` described `Client/ArmContext.cs` as "the shared clock, the
  pacer, and the per-run latency histograms" — three of the seven types that lived there. Replaced by
  one row per new file, plus corrected rows for `Target/SocketIO.cs` (N0).
- The analysis README and `.editorconfig` contain **no** reference to any renamed path; the four
  exact-path `.editorconfig` globs (`CampaignQueries.cs`, `GateValidity.cs`, `GateFlow.cs`,
  `LedgerFindings.cs`) all still resolve to existing files (re-checked after N3).

## N4 — `AnalysisOptions.cs:6` was already clean

The migration sentence the plan quotes ("…so switching a campaign's invocation between the two
implementations is not part of the migration") is gone at `HEAD`; C1 rewrote it. The surviving migration
vocabulary was the 2 `migrated kind` doc lines in `ContractRegistry.cs`, the same in
`ContractShapeTests.cs` (+ its test method name), and 10 batch-id lines. All rewritten as statements
about current behaviour; `D*` references (`D14.7`, `D14.21`, `D18.5`, `D20.5`) untouched.
`JsonPaths.cs:20` still says "The Python side stays the reference for…": that is a statement about the
frozen oracle under `verification/**`, which still exists, not migration vocabulary — left alone.

## D4 · `IsTestProject` spike — measured, and the line stays

**Baseline (mechanical proof, `tests/*/…/obj/project.assets.json` → `project.frameworks.*.dependencies`):**
14 of the 15 test projects resolve the four analyzer packages (`Meziantou.Analyzer`,
`Microsoft.VisualStudio.Threading.Analyzers`, `Roslynator.Analyzers`, `SonarAnalyzer.CSharp`);
`WinForward.E2E.Tests` resolves **0**.

**Spike:** deleted `<IsTestProject>true</IsTestProject>` (`tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj:4`),
`dotnet restore` → the four packages appear as direct dependencies (verified), then
`dotnet build -c Release -p:TreatWarningsAsErrors=false`.

**Result: 31 analyzer findings**, none in a lane or packet path:

| rule | count | where |
|---|---|---|
| `MA0006` (use `string.Equals`) | 14 | `ClientRunnerTests`, `CliSnapshotTests`, `ContractShapeTests` ×5, `LedgerShapeTests`, `ObjectDisposedCatchGateTests`, `PlanFileTests` ×3, `TruncatedConnectionTests`, `UdpReceiverOptionTests` |
| `S3358` (nested ternary) | 5 | `ContractRegistry.cs:184`, `Shapes/LatencyShape.cs:104,107,128,129` |
| `MA0002` (`IEqualityComparer` on a dictionary) | 5 | `ContractShapeTests` ×3, `DeclaredKeys.cs:70,71` |
| `S127` (loop variable modified in body) | 2 | `ObjectDisposedCatchGateTests.cs:262,268` |
| `MA0009` (regex without a timeout) | 2 | `JsonKeyLiteralGateTests.cs:64,68` |
| `S2344` (enum name should be plural/`Flags`) | 1 | `ContractRegistry.cs:9` |
| `S1118` (static-only class) | 1 | `CliSnapshotTests.cs:200` |
| `MA0008` (missing `StructLayout`) | 1 | `TruncatedConnectionTests.cs:209` |

**Decision (the pre-authorised rule, `> ~20`):** restore the line and document the exemption in the
csproj. The comment states the mechanism (restore-time `ExcludeRestorePackageImports=true` means xunit's
props never run, so this explicit property is the only thing keeping the four packages unresolved), the
measured count, and the revisit trigger (clear the list, then drop the line). `dotnet restore` after the
restore re-confirms 0 analyzer dependencies.

## Per-batch summary (implement dispatch, 2026-10-09)

| batch | what landed | gate |
|---|---|---|
| N0 | 4 acronym renames, 27 references + 2 spec lines + `README.md:53`; `Target/SocketIo.cs` → `SocketIO.cs`; `Dedicated` → `DedicatedThread` (15 lines); `Repeated` ×3 → `MarkdownTable.Repeated` (+7 qualified call sites); **the 4 "unused" usings were restored** (see above) | build 0 warnings, 364 tests, oracle rc=0 |
| N1 | `Analysis/Model/Measured.cs` + 14 declarations, 4 non-return positions and `BootstrapPair.Draw`; deconstruction sites unchanged | build 0 warnings, 364 tests, oracle rc=0 |
| N2 | `Findings/LedgerViews.cs` 385 → 352/19/10/7 eff (`LedgerViewsBuilder`, `LedgerArmView`, `LedgerPassView`, `LedgerViews`); `Client/ArmContext.cs` 137 → `Clock` 10 / `Pacer` 52 / `DedicatedThread` 8 / `LatencySet` 26 / `ArmOutcome` 11 / `EmptyMetrics` 12 / `ArmContext` 30 eff; `README.md:37` and `measurement-record-contract.md:115` updated | build 0 warnings, 364 tests, oracle rc=0, effective-lines no output |
| N3 | 5 `git mv`; `ArmDenominatorTable` type → `TableArmDenominator` (2 refs) | build 0 warnings, 364 tests |
| N4 | 3 doc lines + 1 test method name + 10 batch-id lines rewritten; 0 hits for `\b(E2-[a-z]\|E3-[a-z]\|E4-[a-z0-9]+\|b1b\|b1c)\b` in the four trees | build 0 warnings, 364 tests |
| N5 | `LaneTestDoubles.cs` 359 → `LaneEventLog` 34 / `LanePolicyFake` 195 / `LaneTransportFake` 137 eff; `IsTestProject` spike (31 findings → line restored with a comment) | build 0 warnings, 364 tests, oracle rc=0 |

Final evidence pass, in the parent's order and with nothing else running:
`dotnet build WinForward.slnx -c Release` → **0 warnings, 0 errors**;
`oracle-diff.py` → **`compared 51 slice(s): 0 differ(ent), 0 missing` / `differences: 0 structure, 0 value, 0 missing` / rc=0**;
`dotnet test tests/WinForward.E2E.Tests/… -c Release` → **364 passed, 0 failed**;
`effective-lines.py <four paths>` → **no output, rc=0**.

## Gate follow-up: MA0003 from the `Measured<T>` conversion

`Measured<T>` turned tuple literals into constructor calls, and MA0003 ("name the parameter") fires on
every construction whose argument is a literal. Fixed by naming both arguments at **all 50**
constructions in the five files it reported (`ArmAccess.cs` 22, `LedgerFindings.ClientCounts.cs` 18,
`MetricCatalogue.cs` 6, `BootstrapPair.cs` 3, `GateValidity.cs` 1) — the 41 the gate flagged plus the 9
that pass only non-literals, so each file has a single construction shape. No factory was added: naming
is what the rule is for, and `Measured<T>.Absent(...)`/`Of(...)` would have added a second way to build
a reading to save a few characters.

Verified without running the parent's gate: MA0003's severity was raised temporarily in
`.editorconfig`, `dotnet build WinForward.slnx -c Release -p:TreatWarningsAsErrors=false` reported **0
MA0003**, and `.editorconfig` was restored byte-identically (`git diff --stat .editorconfig` empty).

Two unused usings from the N2/N5 splits removed: `System.Text.Json` in
`Client/ArmOutcome.cs` (only `IJsonWritable` was referenced, from `Contracts.Json`) and
`WinForward.E2E.Client` in `Lanes/LanePolicyFake.cs` (`Clock` is used by `LaneEventLog`, not by the
fake). Nothing suppressed.

## Residual items handed to the parent

- **`Findings/LedgerFindings.ClientCounts.cs:192`**: a `<summary>One DNS port's ledger totals and the
  client totals they are read against.</summary>` sits directly on `internal sealed class EndpointSlot`,
  which it does not describe. C2 is renames-and-moves only, so it was left; it looks like a doc line
  orphaned by an earlier split and belongs to C4's documentation pass.
- **`research/02` B3.8/B3.9 (the four usings)** — see above; AC6's usings clause is unreachable.
