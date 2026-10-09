# 02 — Naming and idioms: the house convention, and the E2E trees audited against it

> Feeds subtask **C2 (`e2e-naming-and-idiom-pass`)** of `.trellis/tasks/10-09-e2e-csharp-native-cleanup/prd.md`.
> Read-only investigation at `master` `f992370`. Every `file:line` below was produced by `rg -n --heading`
> against that tree; the effective-line figures come from the read-only
> `benchmarks/WinForward.E2E/scripts/effective-lines.py`.
>
> **Sequencing assumption (PRD R3):** C1 deletes the Python emulation *first*. Anything C1 deletes is listed
> here only so C2 does not spend effort renaming a corpse — see §0.

Audit scope: `benchmarks/WinForward.E2E/**`, `benchmarks/WinForward.E2E.Contracts/**`,
`benchmarks/WinForward.E2E.Analysis/**`, `tests/WinForward.E2E.Tests/**` (`.cs`, `.csproj`, generated-looking
files). `bin/`/`obj/` and the ignored `__pycache__/` trees are excluded — the latter is already covered by
`.gitignore:28` and is untracked (`git ls-files | rg __pycache__` is empty).

---

## 0. What C1 removes, and why C2 must not touch it

These carry the Python-emulation *names*; C1 deletes or replaces them (PRD §任务地图 C1). No C2 rename may
land on them:

| file | evidence | why out of C2 scope |
|---|---|---|
| `benchmarks/WinForward.E2E.Analysis/Stats/CpRandom.cs` | 25 refs | replaced by `System.Random` (PRD 用户裁定 2026-10-09) |
| `benchmarks/WinForward.E2E.Analysis/Json/VerbatimNumber.cs` | 133 refs | replaced by `NumberFormat`/`ToString` |
| `benchmarks/WinForward.E2E.Analysis/Json/VerbatimJson.cs` | 176 refs | replaced by `Utf8JsonWriter`/`JsonSerializer` |
| `benchmarks/WinForward.E2E.Analysis/Json/PythonExponential.cs` | 2 refs | one `%.Ne` cell, deleted with the number emulation |
| `benchmarks/WinForward.E2E.Analysis/Loading/PythonGlob.cs` | 5 refs | replaced by `Directory.EnumerateFiles` |
| `benchmarks/WinForward.E2E.Analysis/Stats/DescriptiveStats.cs:119-…` | 81 refs | compensated (Neumaier) summation is dropped |
| `tests/WinForward.E2E.Tests/Analyzer{Random,Number,Json}GoldenTests.cs` | 3 files | the byte-identical golden vectors go with their producers |
| `benchmarks/WinForward.E2E.Analysis/verification/golden/{cp-random,py-number,py-json}-vectors.json` | 3 files | same |

**One file in that family is *not* on C1's list and is still a Python-era name:** `Model/PosixPathText.cs`
(16 refs) reproduces `pathlib.Path`'s `str()`. Its consumers are `Loading/CampaignLoader.cs:60,166,195-196,253`,
`Loading/LedgerLocator.cs:43,52`, `Loading/PythonGlob.cs:59`. The task.json description names "glob and path
spellings" as removable, but the C1 one-liner omits it. **Flag for the parent:** either fold `PosixPathText`
into C1 (its byte-exact separators are compared in `tables.md` §2, so this needs an oracle check) or accept
that it survives C1 and leave its name alone. Spelling-wise the name is *correct* — `Posix` is Pascal-case,
matching the BCL's own `PosixSignalRegistration` (`benchmarks/WinForward.E2E/Program.cs:162`).

---

## Part A — The house convention, derived from evidence

### A1. Acronym casing in identifiers

| rule | evidence |
|---|---|
| A **two**-letter acronym in an identifier is ALL CAPS | `src/WinForward.Core/IPPrefix.cs:13` (`IPPrefix`), `IPAddressValue` (named in `.trellis/spec/backend/directory-structure.md:33`), `IPEndPoint`/`IPAddress` throughout `src/` (e.g. `src/WinForward.Core/Domain.cs`), `SupportedOSPlatform` (`src/WinForward.Cli/Program.cs:120`) |
| A **three-or-more**-letter acronym is Pascal-case | `Tcp`/`Udp` (`src/WinForward.Runtime/TcpRedirect/*`), `Dns` (`src/WinForward.Runtime/Socks5/Socks5AddressCache.cs`), `Cpu` (`CpuSeconds`, `CpuDiagnostics`, `CpuDetail` — 14 identifiers in `src/`), `Json`, `Crc32C` |
| `Id` is **`Id`**, never `ID`, in identifiers | 40× `StableId`, 20× `ScopeId`, 16× `ProcessId`, 15× `LocalScopeId`, 12× `AdapterId` in `src/`. The 36 `ID`-shaped hits (`src/WinForward.Core/AdapterSlotTable.cs:5`, `src/WinForward.Windows/IProcessOwnerTableReader.cs:19,23,49,107,145`, `IPHelperAbi.cs:19,106`) are **prose inside comments** (`stable ID`, `owning PID`, `NET_LUID`) and ABI constant names, never managed identifiers |
| Prose spells the acronym out in caps: `CPU`, `DNS`, `IP`, `OS`, `VM` | `Cpu` vs `CPU`: 29 identifier hits (`CpuSeconds`…) vs 57 prose hits in `src/`; `Dns` vs `DNS`: 4 vs 16; `src/WinForward.NdisApi/NdisPacketArrivalSignal.cs:22` "on any OS"; `src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs:109` "reach the VM" |
| No managed identifier in `src/` contains an `Io` segment | `rg '\b\w*(Io\|IO)\w*\b' src/` yields only BCL/ABI names: `IOException`, `SIO_UDP_CONNRESET` (`src/WinForward.Runtime/TcpRedirect/TcpRedirectInterfaces.cs`), `IOCTL`/`IOCTLs`, `SIOUdpConnreset`, `IOControl`, `ERROR_OPERATION_ABORTED` |

Written rules that touch this: `.editorconfig:73-152` (`dotnet_naming_rule.*`, all `severity = suggestion`),
`.trellis/spec/backend/quality-guidelines.md:115-121` (§Field naming). There is **no custom naming analyzer**:
`analyzers/WinForward.Analyzers/DiagnosticDescriptors.cs:15,24,33,42` declares only WF0001–WF0004, and
`TaskRunAnalyzer.cs:9`, `ContinueWithAnalyzer.cs:9`, `BareAwaitableExpressionAnalyzer.cs:9`,
`UnawaitedAwaitableDiscardAnalyzer.cs:9` are all async-lifetime rules. So naming is convention + review, not
a gate — which is exactly why the two-letter cases survived the port.

### A2. Fields, locals, consts

`.editorconfig:65-71` states the rule in a comment and encodes it: `public`/`protected` fields PascalCase;
`internal`/`private` static fields `s_camelCase`; `internal`/`private` instance fields `_camelCase`;
`internal`/`private const` PascalCase; locals/parameters camelCase; local functions PascalCase.
`[ThreadStatic]` uses `t_` with a local `#pragma warning disable IDE1006`
(`.trellis/spec/backend/quality-guidelines.md:120`; the only two such fields are in
`src/WinForward.Runtime/PacketRuntime.cs` and `PacketPathProbe.cs`).

**The E2E trees already comply** — `s_utf8` (`Analysis/Loading/JsonReader.cs:42`), `s_options` (:36),
`s_flags` (`Analysis/Cli/AnalysisOptions.cs:29`), `_buffer`, `_state`, `_index`. No finding.

### A3. Filename = primary type; partial shards are `Type.Topic.cs`

`.trellis/spec/backend/directory-structure.md:125` — "**Filename = primary type name**, one primary type per
file", with co-location only for a tight cluster (`:127-131`). `.editorconfig:191-196` disables MA0048
repo-wide, so the compiler does **not** enforce it — the rule is judged by cluster, not by count.

The partial-shard convention is **`<Type>.<Topic>.cs`**, and it is well attested:

| precedent | location |
|---|---|
| `ConfigurationModels.cs` + `ConfigurationModels.Udp.cs` | `src/WinForward.Configuration/` |
| `TcpProxyCoordinator.cs` + `.Diagnostics.cs` + `.Injections.cs` | `src/WinForward.Runtime/TcpRedirect/` |
| `UdpProxyCoordinator.cs` + `.Send.cs` + `.Sweep.cs` | `src/WinForward.Runtime/UdpProxy/` |
| `ArmKeys.Common.cs` … `ArmKeys.Throughput.cs` (13 shards) | `benchmarks/WinForward.E2E.Contracts/` |
| `LedgerShapeTests.cs` + `LedgerShapeTests.Accounting.cs` | `tests/WinForward.E2E.Tests/` |

### A4. Multi-type files are fine only when the cluster is tight

`directory-structure.md:127-131` names the three allowed clusters: an interface beside its single
implementation, interop declarations for one ABI directory, a record plus the static class that operates on
it. `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs` is the reference shape (interface + result
vocabulary in one file, cited at `directory-structure.md:86`). `src/WinForward.Runtime/TcpRedirect/TcpRedirectInterfaces.cs`
is the high-water mark for doc density on such a file (206.8 XML-doc lines per 100 effective lines).

### A5. Multi-value returns are **records**, not tuples

Measured with `rg --pcre2 '^\s*(?:internal|public|private)\s+(?:static\s+)?(?:async\s+)?\([A-Za-z?<>,\s\[\]\.]+\)\s+[A-Z]\w*\('`:

| tree | named-tuple-returning methods |
|---|---|
| `src/` | **0** |
| `tests/WinForward.Core.Tests`, `tests/WinForward.Integration.Tests`, `benchmarks/WinForward.Benchmarks` | 0 each |
| `tests/WinForward.Runtime.TcpRedirect.Tests` | 1 |
| `benchmarks/WinForward.E2E` | 1 |
| `benchmarks/WinForward.E2E.Contracts` | 0 |
| `tests/WinForward.E2E.Tests` | 2 |
| **`benchmarks/WinForward.E2E.Analysis`** | **39** |

The house shape is a record: `src/WinForward.Core/NativeBufferPool.cs:227`
(`internal readonly record struct NativeBufferPoolStats(`), `src/WinForward.Core/Domain.cs:300,314`,
`src/WinForward.Core/Policy.cs:3,25`, `src/WinForward.Core/ProcessMetadata.cs:9`,
`src/WinForward.Windows/AdapterLocalAddressProvider.cs:14,16`,
`src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs:44,52`.

### A6. Static class of pure functions is the house shape; extension methods are not

`directory-structure.md:110-112` treats "a cluster of static pure functions" as a *natural split seam*, and
`src/` is full of them (`PacketChecksums`, `FlowHash`, `ChecksumMath`, `ProcessSelectorMatcher`). Extension
methods, by contrast, appear **4 times in the whole repository**, all in one file:
`benchmarks/WinForward.E2E.Analysis/Model/CampaignQueries.cs:10,19,28,40`. `.editorconfig:236-239` keeps a
rule-level suppression there (`resharper_convert_to_extension_block_highlighting = none`) with the explicit
reason "The project keeps the classic static-class form". **So extension methods are the exception, not the
target.** Any proposal to "make `ArmAccess` extension methods" would move *away* from the house style.

### A7. Multi-line returns / `out` parameters / `Tuple`

`ValueTuple` and `System.Tuple` types are never named explicitly; `object`/`dynamic` appear **zero** times in
`benchmarks/WinForward.E2E*` except one delegate parameter (`benchmarks/WinForward.E2E/Program.cs:50`,
`void CancelHandler(object? _, ConsoleCancelEventArgs eventArgs)` — a `ConsoleCancelEventHandler` signature).
`dynamic` appears 0 times repo-wide.

### A8. The 400-effective-line ceiling, and the "split before you get there" rule

`directory-structure.md:98-121`: ≤400 effective lines, gate =
`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py <paths>`, and — the part that matters here —
"**Meeting the ceiling is not a licence to stop.** A file near 400 lines splits along its existing seams
before it goes over", with the precedent `ConfigurationModels.cs` **split at 375**.

### A9. XML docs: `<summary>` + prose. One `<param>` per record property is *not* the house style.

`src/` contains exactly **1** `<param name="…">` element in 138 files
(`src/WinForward.Runtime/QuiescenceScope.cs:102`). Its records document positional properties **in prose
inside the record's `<summary>`** — `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs:36-44`,
`src/WinForward.Core/Policy.cs:3`, `src/WinForward.Core/ProcessMetadata.cs:9`.

The four E2E trees contain **157** `<param name="…">` elements, of which the overwhelming majority document
record positional properties. Top files: `tests/WinForward.E2E.Tests/ContractRegistry.cs` 21,
`Analysis/Checks/ControlDrift.cs` 21, `Analysis/Findings/DualFindings.cs` 17,
`Analysis/Metrics/MetricSpec.cs` 13, `Analysis/Model/LedgerData.cs` 11,
`benchmarks/WinForward.E2E/Client/Lanes/LaneCounts.cs` 8.

Measured XML-doc density (`///` lines ÷ effective lines, via the repo's own counter):

| tree | files | effective | `///` | doc per 100 eff |
|---|---|---|---|---|
| `src/` | 138 | 16 331 | 5 583 | 34.2 |
| `benchmarks/WinForward.Benchmarks` | 52 | 9 673 | 1 906 | 19.7 |
| `tests/WinForward.Runtime.TcpRedirect.Tests` | 20 | 3 826 | 162 | 4.2 |
| `benchmarks/WinForward.E2E` | 75 | 8 696 | 1 440 | 16.6 |
| `benchmarks/WinForward.E2E.Analysis` | 76 | 9 875 | 2 172 | 22.0 |
| `tests/WinForward.E2E.Tests` | 68 | 8 249 | 1 301 | 15.8 |
| **`benchmarks/WinForward.E2E.Contracts`** | 30 | 1 787 | 1 509 | **84.4** |
| — of which the `ArmKeys.*.cs` shards | 13 | 823 | 934 | **113.5** |

The `ArmKeys` density is defensible: `measurement-tooling.md:7-48` makes the README contract table a gate
resolved *through* `ArmKeys.*.cs`, so those files are the contract's documentation. `src/`'s own most
doc-dense file (`TcpRedirectInterfaces.cs`, 206.8 %) beats it. **Not a rule violation** — but the ratio is
14 files' worth, not one file's, which is why it reads as a translation artefact. Flagged, ranked low.

### A10. Comment language and shape

- **English only.** CJK appears in **1 line** across the four trees — a test *string literal*,
  `tests/WinForward.E2E.Tests/AnalyzerJsonGoldenTests.cs:58` (`VerbatimJson.String("中文")`), which is test
  data, not prose. `src/` has 0 CJK lines in `.cs`. The Chinese in `.editorconfig` is process commentary,
  not code documentation. **No finding.**
- `///` for anything a reader needs; `//` for the rationale behind a non-obvious line. `/* */` blocks appear
  nowhere in the four trees.
- Design-decision references `(D20.5)`, `(R8)`, `design §8` are **house style**: `src/` has 17 `(D…)`-shaped
  references and 76 files' worth of `(D…)/(R…)`. But **batch ids** of the migration (`E2-d`, `E3-d`, `E3-e`)
  are not: `rg '\bE\d+-[a-z]\b' src/` returns **0**, while the four trees contain 11 lines of them, all in
  `tests/` (see B2.7).

### A11. Where the repo is itself inconsistent — and which side is more common

| concept | side A | side B | more common |
|---|---|---|---|
| multi-value return | named tuple (39 in `Analysis`) | record | **record** — 1 named tuple in all of `src/`, and 42 across the four E2E trees |
| partial-shard file name | `Type.Topic.cs` (5 precedents incl. `src/`) | `Topic.cs` declaring `Type` (2 files) | **`Type.Topic.cs`** |
| extension methods | static class (repo-wide) | `CampaignQueries.cs` only | **static class** |
| record property docs | prose in `<summary>` (138 files, 1 `<param>`) | one `<param>` per property (157) | **prose** |
| `ArmDenominatorTable` vs `Table*` prefix | `Table*` (17 files) | `ArmDenominatorTable.cs` (1) | **`Table*`** |
| `IsTestProject` in a test csproj | implicit via `Microsoft.NET.Test.Sdk` (14 projects) | explicit `true` (`E2E.Tests.csproj:4`) | **implicit** |

---

## Part B — The audit

Call-site counts are `rg -c` over `benchmarks/` + `tests/` (whole repo where a type could leak into other
trees). "Contract-visible?" distinguishes **output-visible** (a byte someone compares or greps) from
**cross-assembly** (a rename costs edits in the test project but is mechanical).

### B1. Two-letter / acronym identifiers

The hunt was exhaustive, not sampled. Filtering all 7 462 distinct identifiers in the four trees with a
case-boundary regex (`[a-z](Io|Os|Ip|Id|Vm|Pc|Db|Msg|Cfg|Num|Idx|Cnt|Buf|Len|Req|Resp|Src|Dst|Tx|Rx|Ts|Tbl|Ctx|Opts|Args|Svc|Obj|Ptr|Vec|Elem|Chr|Str)(?=[A-Z0-9]|$)`)
returns **only four hits**, three of which are real:

| # | `file:line` | current | proposed | call sites | contract-visible? | confidence |
|---|---|---|---|---|---|---|
| 1 | `benchmarks/WinForward.E2E/Target/SocketIo.cs:27` (+ filename) | `SocketIo` | `SocketIO`, or better `SocketStreams` | **7** refs (`DnsServer.cs:321,322,334`; `TcpConnectionProtocol.cs:159,222`; declaration; `tests/…/TruncatedConnectionTests.cs:97`) + `README.md:53` | **Documentation-visible**: `README.md:53` names the path in the Layout table. Not oracle-visible | high |
| 2 | `benchmarks/WinForward.E2E/Client/Lanes/LaneTransportContracts.cs:39` (and `:29,46,103` in `<see cref>`) | `LaneReceiveKind.IoError` | `LaneReceiveKind.IOError` | **16** refs across 6 files | internal; only `tests/WinForward.E2E.Tests/Lanes/{LatencyPolicyTests,LaneTransportTests}.cs` reach it | high |
| 3 | `benchmarks/WinForward.E2E.Contracts/ArmKeys.Run.cs:30` | `Run.OsDescription` (member) — value stays `"osDescription"` | `Run.OSDescription` | **3** refs (`RunFileWriter.cs:68`, `Analysis/Tables/TableEnvironment.cs:89`, declaration) | **The member name is not output-visible**: `check-readme-contract.py:204` derives the path from the const's **value**, and `:205` + `:294` build the write-site probe from `ArmKeys.{chain}.{member}` — which a mechanical rename keeps consistent. Renaming the *value* `"osDescription"` **is** output-visible: **do not do it** | high |
| 4 | `tests/WinForward.E2E.Tests/JsonlSinkTests.cs:35` | `AnIoFailureIsPropagatedUnderTheClientPolicy` | `AnIOFailureIsPropagatedUnderTheClientPolicy` | 1 | none | high |

**Negative results worth recording** (the PRD hypothesis named them; they are already correct):

- **`Ip`** — every `Ip`/`IP` identifier in the four trees is a BCL name (`IPEndPoint`, `IPAddress`,
  `IPAddress.Loopback`): `tests/…/LedgerShapeTests.cs:374-377,443-446,486`, `Lanes/LaneTransportTests.cs:44,63,80,235-248`.
  `src/` spells the concept `IP` all-caps (`IPPrefix`, `IPAddressValue`). **No deviation.**
- **`Id`** — 36 distinct `Id`-suffixed identifiers (`ConnectionId`, `rowId`, `RunId`, `PassId`, `transactionId`,
  `ControlId`, …). All correct per A1. `MixUdpBook.cs:107,110` (`OfferThreadId`, `SettleThreadId`) are
  correctly cased and **not** dead — consumed by `tests/…/MixUdpBookConcurrencyTests.cs:185-187`.
- **`Cpu` / `Dns` / `Tcp` / `Udp` / `Json` / `Crc32C` / `Posix`** — all Pascal-case, matching both the
  .NET rule and `src/` (`CpuSeconds`, `Dns`, `Socks5UdpTransport`, `UdpTransportContracts`). **No deviation.**
- **`Vm`, `Pc`, `Db`, `Msg`, `Cfg`, `Idx`, `Cnt`, `Buf`, `Len`, `Req`, `Resp`, `Src`, `Dst`, `Tx`, `Rx`, `Ts`,
  `Num`, `Tbl`, `Ctx`, `Opts`, `Args`, `Svc`, `Obj`, `Str`** — **zero** occurrences as identifier segments.
  `No*` (33 hits) is the English word (`NoData`, `NoDelay`, `NoDenominator`); `Func` is `System.Func`;
  `eventArgs` is the `ConsoleCancelEventArgs` parameter. **The two-letter problem is three identifiers, not
  the sixty the brief anticipated.**

### B2. Python-flavoured shape

| # | `file:line` | current | proposed | call sites | contract-visible? | confidence |
|---|---|---|---|---|---|---|
| 1 | **39 sites** in `benchmarks/WinForward.E2E.Analysis/**` — full list below | named-tuple returns | one record per shape | 39 declarations; every call site is a deconstruction in the same assembly + `Analysis/Model/ArmAccess.cs` reached from `Tables/`/`Checks/` | internal | high |
| 2 | `benchmarks/WinForward.E2E/Client/ArmContext.cs:87` | `internal static class Dedicated` (an adjective as a type name) holding `RunOnOwnThreadAsync` | `DedicatedThread` / `OwnThreadRunner` | **18** refs (7 arm files, 14 call sites) | internal, but named as a concept in `tests/…/LaneEngineAllocationGateTests.cs:10` | high |
| 3 | `benchmarks/WinForward.E2E.Analysis/Cli/AnalysisOptions.cs:6` | `…so switching a campaign's invocation between the two implementations is not part of the migration.` | delete the clause (the reference is retired) | 1 | none | high |
| 4 | `tests/WinForward.E2E.Tests/ContractRegistry.cs:113,136` | "One migrated kind…", "The migrated kinds, one shape file each." | "One registered kind…" / "The registered kinds…" | 2 comments | none | high |
| 5 | `tests/WinForward.E2E.Tests/ContractShapeTests.cs:15` and `:30` | `The shape contract of a migrated kind…`; test method `EveryMigratedKindWritesExactlyTheMetricPathsItsKeysDeclare` | drop `Migrated` / `Registered` | 1 declaration + 1 doc line | none (xunit method name) | high |
| 6 | `Analysis/Tables/GateFlow.cs:390-391`, `Analysis/Checks/IdentityChecks.cs:343-345`, `Analysis/Findings/FindingsCollector.cs:333-334` | the same 2-line `private static string Int(double)` copied **3×** with the same doc sentence | one shared helper | 3 | none | high |
| 7 | `Analysis/Tables/TableDns.cs:232`, `TableLatency.cs:182`, `TableUdp.cs:251` | identical `private static string[] Repeated(int count, string value)` **3×** | one shared helper | 3 | none | high |
| 8 | `Analysis/Findings/LedgerDecodeTotals.cs:79`, `LedgerTruncationTotals.cs:126` | identical `Larger(double?, double?)` **2×** | one shared helper | 2 | none | medium |
| 9 | `Analysis/Metrics/MetricComparisons.cs:314,317` and `Analysis/Verdict/VerdictSections.cs:224,227` | identical `Number(double?)` / `Text(string?)` **2× each** | one shared JSON-null helper | 4 | none | medium |
| 10 | `tests/WinForward.E2E.Tests/{CliSnapshotTests.cs:8,20,22,26,73,120; RepoPaths.cs:43; TargetOptionsTests.cs:68; ObjectDisposedCatchGateTests.cs:17; ClientOptionsTests.cs:71}` | batch ids `E2-d`, `E3-d`, `E3-e` in comments | describe the behaviour, not the migration batch | 11 lines | none | medium |
| 11 | 157 `<param>` record-property docs (A9) | per-property `<param>` | prose inside the record `<summary>` | 157 | none | medium |

**B2.1 in full — the 39 named-tuple returns.** 17 are the single dominant shape
`(T? Value, string? Reason)`; 4 more are the same shape under other names (`Ok`/`Detail`, `Prefix`/`Detail`).
Collapsing **those 21** behind one `internal readonly record struct Reading<T>(T Value, string? Reason)` is
the single largest readability change available in the tree and touches one new file plus 21 signatures:

| file | tuples |
|---|---|
| `Analysis/Metrics/MetricCatalogue.cs:229,233,250,279` | 4× `(double? Value, string? Reason)` |
| `Analysis/Findings/LedgerClientCounts.cs:121,147,159,179` | 4× `(double? Value, string? Reason)` |
| `Analysis/Model/ArmAccess.cs:23,38,71,101,134,160` | `(JsonElement? Result, string? Reason)`, 3× `(double? Value, string? Reason)`, `(JsonElement? Value, …)`, `(bool? Value, …)`, + 1 more |
| `Analysis/Checks/IdentityChecks.cs:93,257,284,309` | 3× `(bool? Ok, string? Detail)`, `(string? Prefix, string? Detail)` |
| `Analysis/Tables/GateValidity.cs:141,187,240,275` | 4 further shapes |
| `Analysis/Tables/GateFlow.cs:133,168,365` | 3 further shapes |
| `Analysis/Stats/BootstrapPair.cs:41,108,155,195` | 4 further shapes |
| `Analysis/Stats/CpuDetail.cs:58,101,119` | 3 further shapes |
| `Analysis/Loading/CampaignLoader.cs:74,112` | 2× a 3-tuple of dictionaries |
| `Analysis/Metrics/MetricCell.cs:42` | `(string Status, string? Reason)` |
| `Analysis/Model/JsonValue.cs:74`, `Model/RunClocks.cs`, `Model/*` | 3 further shapes |
| `Analysis/Json/VerbatimNumber.cs:189,286` | **C1 territory — skip** |
| `Analysis/Tables/{TableLatency.cs:135, TableEnvironment.cs:320, TableMemory.cs}` | 3 further shapes |
| `Analysis/Stats/DescriptiveStats.cs:54` | **C1 territory — skip** |
| `benchmarks/WinForward.E2E/Client/Arms/MixMetricsWriter.cs:59` | `(MixMetrics Metrics, long ClientSendLoss, int IdleLanes)` — the one non-Analysis case |
| `tests/WinForward.E2E.Tests/LedgerShapeTests.cs:443`, `SourceCensusTests.cs:159` | 2 test helpers |

**Explicitly checked and *not* findings** (the brief hypothesised them; the evidence clears them):

- **Manual parsing where `System.Text.Json` fits** — `Analysis/Loading/JsonReader.cs:34-134` is
  `JsonDocument`-based, and `:17-21` documents *why* (IL2026/IL3050 under `TreatWarningsAsErrors`). Compliant.
- **`catch (Exception)`** — 19 sites, **all** filtered: 14× `when (exception is not OperationCanceledException)`,
  3× `when (exception is IOException or UnauthorizedAccessException)`. `src/` does the same
  (`src/WinForward.Configuration/ConfigurationLayering.cs:147,159`, `src/WinForward.Cli/Program.cs:138,175,412`,
  `src/WinForward.NdisApi/NdisCapture.cs:245,257,266`). The bare `catch { }` form appears **0** times in both.
  Compliant.
- **`Array.Resize`** — 4 sites (`Wire/FrameStreamReader.cs:123`, `Client/UdpReliability.cs:30,467,468`).
  `src/` uses it in its own growable lanes (`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:142-143`,
  `src/WinForward.Runtime/TcpRedirect/RedirectInjectionLanes.cs:219-221`). Compliant.
- **`List<T>` in hot loops / `StringBuilder` misuse** — the lane seam (`Client/Lanes/LaneEngine.cs`,
  `TcpLaneTransport.cs`, `UdpLaneTransport.cs`) has **zero** `List<` and is pinned by
  `tests/…/Lanes/LaneEngineAllocationGateTests.cs` and `LaneTransportAllocationGateTests.cs`. The 5
  `StringBuilder` sites (`Analysis/Json/VerbatimJson.cs:43,140,168,189`, `Client/PlanFile.cs:269`) all pass a
  capacity or write into a reused instance. Compliant.
- **`object` / `dynamic` / `Tuple` / `Dictionary<string, object>`** — 0 / 0 / 0 / 0.
- **`internal static` grab-bags** — the tree's static-class ratio is high (`Analysis` 71 of 109 types) but
  each is a cohesion-checked module (`ArmAccess` = 6 record readers; `JsonValue` = member accessors;
  `Tables/Table*.cs` = one report table each). The genuinely multi-topic files are in B3, not here.

### B3. Structure

| # | `file:line` | current | proposed | call sites | contract-visible? | confidence |
|---|---|---|---|---|---|---|
| 1 | `Analysis/Findings/LedgerViews.cs` (385 eff, 504 raw, **4 types**: `LedgerArmView:10`, `LedgerPassView:59`, `LedgerViews:90`, `LedgerViewsBuilder:120`) | one file, four types, 27 `Dictionary<string,…>` | split on the existing type seams (`LedgerArmView.cs`, `LedgerPassView.cs`, `LedgerViews.cs`, `LedgerViewsBuilder.cs`) | 0 (type names unchanged) | no | high — `directory-structure.md:116-121` names this exact situation at **375** eff |
| 2 | `Analysis/Findings/LedgerClientCounts.cs` and `Analysis/Findings/LedgerDnsTotals.cs` | both declare `internal static partial class LedgerFindings` (`:9` and `:10`) but are not named after it | `LedgerFindings.ClientCounts.cs`, `LedgerFindings.DnsTotals.cs` | 0 (type & member names unchanged) | no | high — A3 |
| 3 | `benchmarks/WinForward.E2E/Client/ArmContext.cs` (7 types: `Clock:12`, `Pacer:26`, `Dedicated:87`, `LatencySet:96`, `ArmOutcome:125`, `EmptyMetrics:157`, `ArmContext:169`) | a grab-bag; `README.md:37` describes the file as "the shared clock, the pacer, and the per-run latency histograms" — i.e. the README already disagrees with the contents | split: `Clock.cs`, `DedicatedThread.cs` (see B2.2), `LatencySet.cs`, `ArmOutcome.cs` + `EmptyMetrics.cs` beside `ArmContext.cs` | 0 (type names unchanged) | `README.md:37` needs a line | high |
| 4 | `benchmarks/WinForward.E2E/Client/PlanFile.cs` (375 eff, 504 raw, one static class, 16 members) | a static class of plan-schema readers named after the artefact | split the schema (`PlanSchema`/plan records) from the loader (`PlanFileSerializer`), or at minimum stop at the validate-vs-read seam | 0 if names are kept | no | medium — the 375 precedent, but a split needs a real seam |
| 5 | `tests/WinForward.E2E.Tests/Lanes/LaneTestDoubles.cs` (359 eff, 547 raw, **10 types**) | one file for 4 enums, 3 record structs, 2 fakes and a log | `LaneEvent.log`/`LanePolicyFake.cs`/`LaneTransportFake.cs`/… | 0 (names unchanged) | no | medium |
| 6 | `Analysis/Tables/ArmDenominatorTable.cs` | prefix order differs from all 17 siblings (`TableAvailability.cs` … `TableUdp.cs`) | `TableArmDenominator.cs` | **2** refs (`rg ArmDenominatorTable`) | no | medium |
| 7 | `Contracts/Json/Rate.cs:11` and `Contracts/Json/PerSecond.cs:11` | file ≠ type (`JsonRate`, `JsonPerSecond`) — the only two such in `Contracts` | rename the **files** to `JsonRate.cs` / `JsonPerSecond.cs` (0 call-site change) | 18 + 14 refs if the **type** is renamed instead; `README.md:344,345` quotes `JsonRate.Rate` / `JsonPerSecond.PerSecond` | file rename: no. Type rename: README-visible | high |
| 8 | `benchmarks/WinForward.E2E/Program.cs:2` | `using System.Runtime.InteropServices;` — no `RuntimeInformation`/`Marshal`/`LibraryImport` in the file | delete | 1 | no | high |
| 9 | `Analysis/Checks/ControlDrift.cs:1`, `Analysis/Findings/LedgerViews.cs:2`, `Analysis/Loading/LedgerLoader.cs:1` | `using System.Runtime.CompilerServices;` — no `[MethodImpl]`, `Caller*`, `Unsafe` in any of the three | delete 3 usings | 3 | no | high |
| 10 | `benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py:7,12` | comments name `Stats/CpRandom.cs` and **`Tables/VerbatimNumber.cs`** | `Json/VerbatimNumber.cs` (already moved); the file is C1's to delete | 2 comment lines | no | high (drifted) |
| 11 | `benchmarks/WinForward.E2E.Analysis/verification/row-profiles.json:8` | prose names `Model/RowProfiles.cs` | correct today; keep in sync if the file moves | 1 | no | high |

**Clean, with evidence — do not spend C2 budget here:**
`#region`/`#endregion` **0** occurrences; TODO/FIXME/HACK **0** (only the analysis's own intentional
`<!-- TODO(batch N) -->` placeholders at `Analysis/Tables/TablesWriter.cs:14,127`, which are the
not-yet-rendered sections of `tables.md` — deliberate); commented-out code **0** (a targeted regex over
`//`-lines found only prose); namespace-vs-folder mismatches **0** across all 249 `.cs` files;
`effective-lines.py` over all four paths exits **0**.

### B4. Project files

The MSBuild chain is: root `Directory.Build.props` (net10.0, `LangVersion 14.0`, `Nullable enable`,
`ImplicitUsings enable`, `TreatWarningsAsErrors true`, `Deterministic`, `AnalysisLevel latest`,
`EnableTrimAnalyzer`/`EnableAotAnalyzer`, `AllowUnsafeBlocks`, and the four analyzer packages) → `src/`
adds the WF0001–WF0004 analyzer reference, `tests/` adds `TieredCompilation=false`. **There is no
`benchmarks/Directory.Build.props`** (`rg --files -g 'Directory.Build.*'`), so all four E2E csprojs inherit
the root props by MSBuild's directory walk-up. Verified against `src/WinForward.Core/WinForward.Core.csproj`
and `tests/WinForward.Runtime.TcpRedirect.Tests/…csproj`.

| # | `file:line` | current | proposed | call sites | contract-visible? | confidence |
|---|---|---|---|---|---|---|
| 1 | `benchmarks/WinForward.E2E.Contracts/WinForward.E2E.Contracts.csproj:1` | `<Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="AssemblyName">` — a project-level attribute whose only job is to defeat the solution-wide `-p:AssemblyName=WinForward.E2E.Direct` that `scripts/publish.sh:20` passes | replace with a named opt-in: `<AssemblyName Condition="'$(DirectLaneImage)' == 'true'">WinForward.E2E.Direct</AssemblyName>` + `-p:DirectLaneImage=true` in `publish.sh:20`. **Requires a real `dotnet publish` verification** | 1 project + 1 script line | **the AssemblyName *is* output-visible** — the products identify applications by image name (`publish.sh:27-28`) | medium — the workaround is documented in a 4-line comment and works; this is a taste call, not a defect |
| 2 | `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj:4` | `<IsTestProject>true</IsTestProject>` | delete — `Microsoft.NET.Test.Sdk` sets it; no other test csproj declares it (`rg IsTestProject tests/ benchmarks/ --glob '*.csproj'` finds only this and `TestSupport`'s deliberate `false`) | 1 | no | medium |
| 3 | `benchmarks/WinForward.E2E.Analysis/WinForward.E2E.Analysis.csproj:2,8,12` | a blank line at the top and bottom of `<Project>` | match the sibling files (no padding) | 1 | no | low |
| — | all four | `AssemblyName` + `RootNamespace` declared explicitly | **keep** — every project in the repo does this (`src/*`, `benchmarks/WinForward.Benchmarks`) | — | — | — |
| — | all four | no `InvariantGlobalization`, no `PublishAot` | **keep** — those are `WinForward.Cli`-specific (`src/WinForward.Cli/WinForward.Cli.csproj:6,24`); `Benchmarks` disables the trim/AOT analyzers (`benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj:6-7`) and E2E needs neither | — | — | — |
| — | `WinForward.slnx:33-35`, `.gitignore:28`, `benchmarks/WinForward.E2E/.gitignore` | all four projects listed; `__pycache__/` already ignored | **nothing to move** | — | — | — |

### B5. Doc comments and comment language

Covered under A9/A10. Summary of the two real findings:

1. **B2.11** — 157 `<param>` elements documenting record positional properties where `src/` uses prose
   (157 vs 1). `Analysis/Checks/ControlDrift.cs` (21) and `tests/…/ContractRegistry.cs` (21) are the worst.
   *Caveat:* this is genuinely useful IntelliSense, and deleting it is information loss. Recommend converting
   only the biggest three or four records, or leaving it — see the ranking.
2. **B2.3–B2.5, B2.10** — the migration vocabulary (`the migration`, `migrated kind`, `E2-d`/`E3-d`)
   survives in 5 comment sites plus 11 batch-id lines. `src/` has 0 batch ids.

The 108 "the reference" lines across 50 of `Analysis`'s 76 files are **not** a finding per se: they document
the frozen semantics C1 is removing. They must be rewritten **by C1**, not by C2.

### B6. Dead members

**No dead `internal`/`public` type or member exists in the four trees.** Method: extract every
`internal`/`public` type declaration and every `internal`/`public` member declaration from the three
production E2E trees, then count whole-word occurrences across the four trees *including the test project*
(a friend-assembly-safe reference scope). Result: **0 candidates**.

The first pass (production trees only) flagged `MixUdpBook.OfferThreadId` (`Client/Arms/MixUdpBook.cs:107`)
and `MixUdpBook.SettleThreadId` (`:110`) — both are consumed by
`tests/WinForward.E2E.Tests/MixUdpBookConcurrencyTests.cs:185-187`. `.editorconfig:250-276` (`[tests/**.cs]`)
suppresses `S1144`/`MA0182` for exactly this fixture-mirroring shape, and
`.trellis/spec/backend/quality-guidelines.md:143-148` lists `MemberCanBePrivate` on friend-assembly members
as a jb false positive that must not be "fixed". **Not dead.**

This is consistent with the gates: MA0182 (unused internal types) is suppressed only under `[tests/**.cs]`
(`.editorconfig:269`), and `S1144` likewise only under `[tests/**.cs]` (`.editorconfig:251`), so an
unused internal member under `benchmarks/` would already have turned the format gate red.

---

## Contract / deliberate — the "do not touch" list

| thing | where | why |
|---|---|---|
| Every `ArmKeys.*` const **value** | `benchmarks/WinForward.E2E.Contracts/ArmKeys.*.cs` (13 shards) | PRD 契约不变量 #1. The strings *are* the JSON keys; `check-readme-contract.py:204` derives every documented path from them |
| Every CLI flag: `--flat --raw --out --ledger --warmup-seconds --resamples --seed` | `Analysis/Cli/AnalysisOptions.cs:29,32,79,130,133,136,139,142,151,160` | PRD #3; `cli-snapshots.py` records exit code + stdout/stderr bytes and `CliSnapshotTests` replays 28 of them |
| Every CLI flag: `--bind --tcp-port --udp-port --udp-receivers --dns-port --dns-alt-port --label --ledger` | `benchmarks/WinForward.E2E/Cli/TargetOptions.cs:50-57` | same |
| `tables.md` section headings, column names, `n/a (reason)` / `not carried` spellings; `verdict.json`'s 14 top-level keys | `Analysis/Tables/TablesWriter.cs:52-69`, `Analysis/Verdict/VerdictWriter.cs:19-34`, `Analysis/Tables/MarkdownTable.cs` | PRD #2; the oracle differ compares headings, column names and cell kinds exactly (`measurement-tooling.md:132-138`) |
| `run.json`, `result`/`armSummary`/`error`, `ledger.jsonl`, `<out>/plots/SKIPPED.md` | `Client/RunFileWriter.cs`, `Client/ArmRecordWriter.cs`, `Analysis/Cli/AnalysisRunner.cs:38-48` | PRD #4 |
| `verification/**` — `golden/*`, `synthetic-tree.tar.gz`, `row-profiles.json`, `FROZEN.md` | `benchmarks/WinForward.E2E.Analysis/verification/` | the frozen oracle; `FROZEN.md` states that any change means refreezing and re-diffing from batch 1 |
| `public` on `CpRandom`, `VerbatimNumber`, `VerbatimJson` | `Analysis/Stats/CpRandom.cs:30`, `Analysis/Json/VerbatimNumber.cs:26`, `Analysis/Json/VerbatimJson.cs:27` | design decision **D20.6** (`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md:837`): the analysis project deliberately carries **no** `InternalsVisibleTo`. Superseded by C1 for `CpRandom`/`Verbatim*` — but do not "fix" it as a visibility bug in the meantime |
| `Findings/FindingsCollector.cs` + `FindingsCollector.Structure.cs` | `Analysis/Findings/` | already the house `Type.Topic.cs` partial shape (A3). **Not** a file-per-type violation |
| `Client/Lanes/LaneTransportContracts.cs` (5 types) | `benchmarks/WinForward.E2E/Client/Lanes/` | mirrors `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`, the shape `directory-structure.md:86` cites as correct |
| The two `ExitCodes` types with different member names for the same value | `benchmarks/WinForward.E2E/Cli/ExitCodes.cs` (`UsageError = 2`) vs `Analysis/Cli/ExitCodes.cs` (`InputError = 2`) | not a duplicate: the analysis's `2` means "input unreadable" and the oracle reads it (`:4-6`). Two contracts, two names |
| `catch (Exception)` with a `when` filter | 19 sites | `src/` does the same (see B2, "explicitly checked") |
| `Array.Resize`, `Dictionary<string, …>` keyed by run/pass/label id | see B2 | `src/` does the same |
| `Jsonl` spelling | `Contracts/Json/Jsonl{Sink,Policy}.cs`, `tests/…/JsonlSink*Tests.cs` | the artefact is `.jsonl`; no `JsonLines` precedent exists anywhere in the repo |
| `PosixPathText`, `PythonGlob`, `CpRandom`, `VerbatimJson`, `VerbatimNumber`, `PythonExponential` **names** | see §0 | C1 owns their fate. Renaming them is a *semantic* decision (delete vs rename), not a C2 rename |
| `#region`-free, TODO-free, no commented-out code | whole tree | already clean — do not add regions while splitting |

---

## Proposed execution order

Each step is behaviour-zero and mechanical. "build" means the step changes compiled output and therefore
needs `dotnet build WinForward.slnx -c Release` (zero-warning) before the next step is trusted.

### S0 — pre-flight (no edits)

```bash
# the baseline the whole pass is measured against (PRD AC5/AC6)
dotnet test WinForward.slnx -c Release
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts \
    benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests
git grep -n 'IoError\|SocketIo\|OsDescription' -- '*.cs' | wc -l   # expect 26
```

### S1 — the three acronym renames (mechanical; build after)

1. `SocketIo` → `SocketIO` (7 refs) **and** `git mv SocketIo.cs SocketIO.cs`; update `README.md:53`.
2. `LaneReceiveKind.IoError` → `IOError` (16 refs).
3. `ArmKeys.Run.OsDescription` → `OSDescription` (**member only**; the const value stays `"osDescription"`).
4. `AnIoFailureIsPropagatedUnderTheClientPolicy` → `AnIOFailureIsPropagatedUnderTheClientPolicy`.

```bash
# after: nothing may remain, and the contract gate must still resolve every key
rg -n 'SocketIo|IoError|OsDescription|AnIoFailure' benchmarks/ tests/ --type cs   # expect no hits
python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py               # expects rc=0
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py <four paths>        # rc=0
```
> **Known pre-existing blocker for that checker:** `check-readme-contract.py:47` hard-codes the rename table
> at the *live* task path; task `10-07` is archived, so the script exits **2** today
> (`measurement-tooling.md:36-48`). Treat rc=2 as the baseline defect, not as a step-S1 regression. C3 owns it.

### S2 — file renames with zero call-site churn (build after)

5. `Analysis/Findings/LedgerClientCounts.cs` → `LedgerFindings.ClientCounts.cs`;
   `Analysis/Findings/LedgerDnsTotals.cs` → `LedgerFindings.DnsTotals.cs`.
6. `Analysis/Tables/ArmDenominatorTable.cs` → `TableArmDenominator.cs`.
7. `Contracts/Json/Rate.cs` → `JsonRate.cs`; `Contracts/Json/PerSecond.cs` → `JsonPerSecond.cs`.

```bash
rg -n 'ArmDenominatorTable|Json/Rate\.cs|Json/PerSecond\.cs' benchmarks/ tests/ --type cs
rg -n 'LedgerFindings(ClientCounts|DnsTotals)' benchmarks/ tests/   # README/editorconfig references
grep -rn 'LedgerClientCounts\|LedgerDnsTotals\|ArmDenominatorTable' .editorconfig   # must be empty
git status --porcelain    # renamed files, no content diff
```

### S3 — unused usings and dead-comment vocabulary (build after; cheapest possible diff)

8. Delete `benchmarks/WinForward.E2E/Program.cs:2`,
   `Analysis/Checks/ControlDrift.cs:1`, `Analysis/Findings/LedgerViews.cs:2`,
   `Analysis/Loading/LedgerLoader.cs:1`.
9. Rewrite `Analysis/Cli/AnalysisOptions.cs:6`; `tests/…/ContractRegistry.cs:113,136`;
   `tests/…/ContractShapeTests.cs:15,30`.
10. Delete the 11 batch-id lines in `tests/` (B2.10).

```bash
dotnet build WinForward.slnx -c Release    # CS8019/IDE0005-adjacent breakage shows here
```

### S4 — the two `Type.Topic.cs` / grab-bag splits (build after; no semantic edit)

11. Add `LedgerArmView.cs`, `LedgerPassView.cs`, `LedgerViewsBuilder.cs`; move
    `Analysis/Findings/LedgerViews.cs:10-119` and `:120-…` **verbatim**.
12. Split `Client/ArmContext.cs` into `Clock.cs`, `DedicatedThread.cs`, `LatencySet.cs`,
    `ArmOutcome.cs`, `EmptyMessages.cs`(with `ArmContext.cs`); update `README.md:37`.
13. Split `tests/…/Lanes/LaneTestDoubles.cs` by fake.

```bash
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E.Analysis/Findings benchmarks/WinForward.E2E/Client tests/WinForward.E2E.Tests/Lanes
git diff --stat -M    # a pure move reports rename + 0 changed lines per hunk
```

### S5 — the record-return refactor (build + tests; the only step that touches signatures)

14. Add `internal readonly record struct Reading<T>(T Value, string? Reason)` (name TBD at design time) and
    convert the **21** `(X? value, string? reason)` sites listed in B2.1. Leave the 18 structural tuples alone.
15. Deduplicate `Int` (3×), `Repeated` (3×), `Larger` (2×), `Number`/`Text` (2× each) — move each to the
    module that already owns it (`Tables/MarkdownTable.cs` is the natural home for the two table helpers).

```bash
dotnet test WinForward.slnx -c Release        # 1,660-test baseline (quality-guidelines.md:91)
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic    # behaviour-zero proof
```

### S6 — csproj hygiene (build + publish; last because it touches the release path)

16. `tests/WinForward.E2E.Tests.csproj:4` — delete `<IsTestProject>true</IsTestProject>`.
17. **Optional / needs a publish run:** replace `TreatAsLocalProperty="AssemblyName"`
    (`Contracts.csproj:1`) with a named condition and change `scripts/publish.sh:20`.

```bash
dotnet test tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj -c Release
scripts/publish.sh && ls -1 "${WF_PUB:-/tmp/wf-bench/pub}"/win/*.exe "${WF_PUB:-/tmp/wf-bench/pub}"/win-direct/*.exe
# the two Windows images must still differ by file name (publish.sh:27-28)
```

### Global gate after every step that builds

```bash
dotnet build WinForward.slnx -c Release                                        # zero warnings
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore  # empty output
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts \
    benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests
```
Steps **S1, S2, S4, S6** also need a `jb inspectcode` re-run (file moves invalidate the four
`.editorconfig` globs and the cache); steps **S3, S5** need `dotnet test -c Release` because they change
signatures/text the tests read.

### Cross-cutting hazard: the four `.editorconfig` globs

`.editorconfig:238,241,244,247` keys four suppressions to four *exact paths*:

```
[benchmarks/WinForward.E2E.Analysis/Model/CampaignQueries.cs]   resharper_convert_to_extension_block_highlighting = none
[benchmarks/WinForward.E2E.Analysis/Tables/GateValidity.cs]     dotnet_diagnostic.S1244.severity = none
[benchmarks/WinForward.E2E.Analysis/Tables/GateFlow.cs]         dotnet_diagnostic.S1244.severity = none
[benchmarks/WinForward.E2E.Analysis/Findings/LedgerFindings.cs] dotnet_diagnostic.S1244.severity = none
```

**None of the C2 renames may touch those four file names**, or the glob silently stops matching and the
format gate goes red on the next `dotnet format`. (Two of them — `GateValidity.cs`, `GateFlow.cs` — must be
*re-verified* by C1: S1244 is float-equality, and C1 removes the Python-exact float comparisons.)

### Cross-cutting hazard: paths written into tests and docs as strings

| literal | where | rename that breaks it |
|---|---|---|
| `benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs` | `tests/…/ClientSendLossGateTests.cs:165-166` | any move of `ReliabilityArm.cs` |
| `benchmarks/WinForward.E2E/Target/DnsServer.cs` | `tests/…/TruncatedConnectionTests.cs:93` | any move of `DnsServer.cs` |
| `benchmarks/WinForward.E2E/Client/Arms/ReliabilityExchange.cs` | `tests/…/ReliabilityTruncationTests.cs:60-61` | any move of `ReliabilityExchange.cs` |
| `.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json` | `benchmarks/WinForward.E2E/scripts/check-readme-contract.py:47` (dead — archived) | C3, not C2 |
| the same task path | `benchmarks/WinForward.E2E.Analysis/verification/check-fixture-drift.py:40` (dead) | C3 |
| `.trellis/tasks/…/cli-snapshots` (archive-aware) | `tests/…/RepoPaths.cs:49-71` | nothing in C2 |
| ~28 `.cs` path rows + a symbol table | `benchmarks/WinForward.E2E/README.md:30-56`, `:506-514` | every rename in S1/S2/S4, plus the `s_stallDelay` symbol row |
| `JsonRate.Rate`, `JsonPerSecond.PerSecond` | `benchmarks/WinForward.E2E/README.md:344-345` | the *type* rename option in B3.7 |
| `Stats/CpRandom.cs`, `Tables/VerbatimNumber.cs` (already stale) | `verification/synthetic/make_cp_vectors.py:7,12` | C1 deletes this script's subjects |

---

## Top 10, ranked by (readability gained) ÷ (risk × effort)

| # | item | gain | risk | effort | why this rank |
|---|---|---|---|---|---|
| 1 | **B2.1 — collapse the 21 `(value, reason)` tuples behind one `Reading<T>` record** | very high: 21 signatures across 12 files stop being positional and self-documenting | low-medium: mechanical, but it is a signature change and the analysis has no compiler-visible friend boundary | medium | the single largest readability delta; `src/` has **0** named-tuple returns, `Analysis` has 39 |
| 2 | **B3.1 — split `Findings/LedgerViews.cs` (385 eff, 4 types)** | high: the house rule names this exact file at exactly this size | very low: physical move, verbatim bodies | low | `directory-structure.md:116-121` split `ConfigurationModels.cs` at **375**; this is 385 |
| 3 | **B3.3 — split `Client/ArmContext.cs` (7 unrelated types)** | high: a reader looking for the pacer finds `EmptyMetrics` next to it, and the README's description is already wrong | very low: names unchanged | low-medium | second-largest cluster in the tree; also fixes `README.md:37` drift |
| 4 | **B1.1–B1.4 — the three acronym renames + the test method** | medium-high: the user's own complaint, and "遗留缩写清零" is AC2 | very low: 26 refs total, all mechanical | very low | do it first; it is the visible half of AC2 |
| 5 | **B3.2 — `LedgerFindings.{ClientCounts,DnsTotals}.cs`** | medium: makes the partial shard discoverable by name, consistent with `ArmKeys.*`/`TcpProxyCoordinator.*` | very low: 2 `git mv`, no content change | very low | best ratio in the whole list |
| 6 | **B2.3–B2.6 + B2.10 — purge the migration vocabulary and dedupe `Int`/`Repeated`** | medium: removes the exact "scaffolding and Python-era traits" the user named | very low: comments and 5 helper bodies | low | the diff is tiny and the message is exactly the complaint |
| 7 | **B3.6–B3.7 — naming-order fixes (`TableArmDenominator.cs`, `JsonRate.cs`, `JsonPerSecond.cs`)** | medium-low: restores the `Table*` and filename=type rules the rest of the tree obeys | very low: 3 `git mv` | very low | zero-risk tidying while in the neighbourhood |
| 8 | **B2.2 — `Dedicated` → `DedicatedThread`** | medium: an adjective is not a type name; 18 refs read better | very low: one rename | very low | cheap and it is the kind of name a reader stumbles on |
| 9 | **B3.5 — split `LaneTestDoubles.cs` (10 types, 359 eff)** | medium: per-fake files make the lane suite navigable | low: verbatim moves, test-only | medium | more mechanical than #2 but a bigger pile |
| 10 | **B4.2 + B3.8/B3.9 — redundant `IsTestProject`, 4 unused usings** | low-medium: gate-invisible dead weight | very low | very low | `IDE0005` does not fire without `GenerateDocumentationFile`, so nothing else will ever find these |

**Deliberately below the top 10** (real, but worse ratio): B2.11 (157 `<param>` → prose — *deletes*
information for a style point; recommend converting only `ControlDrift.cs`/`ContractRegistry.cs` or skipping),
B3.4 (`PlanFile.cs` at 375 eff — needs a real seam, not a line count), B4.1
(`TreatAsLocalProperty` — works, documented, and the fix touches the publish path), B3.10–B3.11 (stale
comments in the C1 territory).

---

## Residual uncertainty

- **`jb inspectcode` was not run** (20-minute gate, forbidden by the brief). If any E2E member is only
  reachable through reflection — the xunit `[Fact]` classes are the obvious candidates — my B6 scan would
  still have counted the *name* in the test assembly, so the risk is confined to members referenced only via
  `ArmKeys` reflection in `tests/…/DeclaredKeys.cs`/`ContractRegistry.cs`. Re-run `jb inspectcode` after S1.
- **`effective-lines.py` counts were taken with the repo's own counter**, and the four-file exit code is 0.
  Two files sit inside 25 lines of the ceiling (`LedgerViews.cs` 385, `PlanFile.cs` 375); C1's deletions and
  C2's splits will move both numbers, so re-measure after C1.
- **`README.md` line numbers** (30-56, 344-345, 372, 506-514) shift with every README edit; the anchors were
  correct at `f992370`.
- The `check-readme-contract.py` rename-table path is **already dead** (`measurement-tooling.md:36-48`), so
  that gate cannot be used as a C2 regression check until C3 fixes it. `ContractShapeTests`,
  `JsonKeyLiteralGateTests` and `CliSnapshotTests` are the substitute contract checks.
