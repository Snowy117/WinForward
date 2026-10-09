# Adversarial plan review — C2 `10-09-e2e-naming-and-idiom-pass`

> Reviewer: autonomous adversarial pass at `master` `f992370` (the same revision `research/02` was taken at;
> working tree has no source modification). Every claim below carries a command-verified `file:line`.
> **Hard stop was called mid-investigation**, so §6 (IsTestProject spike), §7 (further deviations) and part
> of §5 are listed as *not verified* rather than guessed. Nothing in this review was written from the plan
> text alone.

---

## Verdict

**Executable with fixes.** The rename core is sound and unusually well evidenced: the three (four) acronym
renames are real, the `.editorconfig` "do not touch" list matches the four globbed paths exactly, the
`OsDescription` **member** rename genuinely cannot move output, and `Reading<T>` can legally stay `internal`
in `WinForward.E2E.Analysis`. But the plan cannot be executed *as written* in two places: AC1's acceptance
gate (`check-readme-contract.py` rc=0) is **impossible today — the script exits 2 at HEAD**, and N2.1
misattaches the `.editorconfig` glob constraint to the wrong file, ordering a `LedgerViews.cs` split to
"keep the name `LedgerFindings.cs`" while `Analysis/Findings/LedgerFindings.cs:21` already *is* that class.
Two more counts in the plan are not reproducible from the code: "21 call sites" is a constructed count, not
a grep result, and "41 处行" does not match the 26 references the evidence base itself predicted. None of
these are architecture problems; all are one-paragraph amendments.

---

## BLOCKER

### B1 — AC1's `check-readme-contract.py` gate is red at HEAD; the criterion is unmeasurable
`prd.md:25` ("`check-readme-contract.py` 仍 rc=0 且 key 计数不缩水") and `implement.md:14`
("跑 `check-readme-contract.py` 确认 rc=0") both assume a green baseline. Measured:

```
$ python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py ; echo rc=$?
rc=2
cannot read .../.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json: [Errno 2] ...
```

The dead path is `benchmarks/WinForward.E2E/scripts/check-readme-contract.py:47`; the script reads it
unconditionally at `:272` before any check, and `.trellis/tasks/10-07-e2e-harness-refactor/` does not exist
(only `.trellis/tasks/archive/2026-10/10-07-*`). `research/02:424-426` notes the defect but the PRD/plan were
never amended, and the "key 计数" line (`:300`) never prints when the script dies at `:272`.

**Amendment.** Replace AC1's gate with the three contract checks that *do* run today, and record the baseline
explicitly:

| # | criterion |
|---|---|
| AC1a | `rg -n 'SocketIo\|IoError\|OsDescription\|AnIoFailure' benchmarks/ tests/ --type cs` returns 0 hits (verified baseline: 26 hits + `tests/WinForward.E2E.Tests/JsonlSinkTests.cs:35`); `README.md:53` updated |
| AC1b | `grep -n '"osDescription"' benchmarks/WinForward.E2E.Contracts/ArmKeys.Run.cs` still matches `ArmKeys.Run.cs:30` (value frozen) |
| AC1c | `dotnet test -c Release --filter FullyQualifiedName~JsonKeyLiteralGateTests\|FullyQualifiedName~ContractShapeTests\|FullyQualifiedName~CliSnapshotTests` green (these are the substitute contract checks named in `research/02:571-573`) |
| AC1d | if the checker must be cited, cite it as **baseline rc=2 / post-change rc=2**, and C3 owns the fix — never "rc=0" |

### B2 — N2.1 attaches the `.editorconfig` constraint to the wrong file
`implement.md:35-36`: "`Analysis/Findings/LedgerViews.cs`：385 有效行、4 个类型 → 按类型拆；**主文件保留
`LedgerFindings.cs` 的名字**（`.editorconfig` glob 约束）".

Verified facts:
- `.editorconfig:247` is `[benchmarks/WinForward.E2E.Analysis/Findings/LedgerFindings.cs]` — a path glob on a
  file that **already exists**: `benchmarks/WinForward.E2E.Analysis/Findings/LedgerFindings.cs:21`
  `internal static partial class LedgerFindings` (11 591 bytes, present in the directory listing).
- `LedgerViews.cs` (23 321 bytes) is a different file; the four types in it are `LedgerArmView`,
  `LedgerPassView`, `LedgerViews`, `LedgerViewsBuilder` (`research/02:301`) — none is `LedgerFindings`.

So the sentence, read literally, orders the implementer to rename `LedgerViews.cs` to `LedgerFindings.cs` —
a name collision with a live partial class, and it would also orphan the S1244 suppression at
`.editorconfig:247`/`:248`. The clause belongs to **N3.1** (`LedgerFindings.{ClientCounts,DnsTotals}.cs`).

**Amendment.** N2.1 becomes: "split `Analysis/Findings/LedgerViews.cs` on its four type seams into
`LedgerArmView.cs` / `LedgerPassView.cs` / `LedgerViews.cs` / `LedgerViewsBuilder.cs`; **no `.editorconfig`
glob covers `LedgerViews.cs`, so all four names are free**." Move the `LedgerFindings.cs`-name-freeze clause
to N3.1, and add the positive statement "the renamed shards are `LedgerFindings.ClientCounts.cs` /
`LedgerFindings.DnsTotals.cs`, which the glob at `.editorconfig:247` does **not** match (its pattern has no
wildcard), so the S1244 suppression stays exactly where it is."

---

## SHOULD-FIX

### S1 — "21 call sites" is a constructed count; AC2's grep cannot see 7 of them
`implement.md:24-31` and `prd.md:26` claim 21 sites of one shape and propose
`rg -c '\(.*\? Value, string\? Reason\)'` → 0 as the criterion. Ground truth (regex from `research/02:101`,
plus a direct hunt for `string? Reason|Detail|Error)`):

| site kind | `file:line` | count |
|---|---|---|
| literal `(X? Value, string? Reason)` **returns** | `Analysis/Metrics/MetricCatalogue.cs:229,233,250,279`; `Analysis/Tables/GateValidity.cs:275`; `Analysis/Model/ArmAccess.cs:38,71,101,134,160`; `Analysis/Findings/LedgerClientCounts.cs:121,147,159,179` | **14** |
| same shape, first member renamed | `Analysis/Model/ArmAccess.cs:23` `(JsonElement? Result, string? Reason)` | 1 |
| same shape, both members renamed | `Analysis/Checks/IdentityChecks.cs:93` `(string? Prefix, string? Detail)`, `:257,:284,:309` `(bool? Ok, string? Detail)` | 4 |
| same shape, second member renamed | `Analysis/Stats/BootstrapPair.cs:41` `(Comparison? Comparison, string? Error)` | 1 |
| same shape **but first member not nullable** | `Analysis/Metrics/MetricCell.cs:42` `(string Status, string? Reason)` | 1 |
| **total** | | **21** |

So 21 is reachable only by relaxing two things the plan never states: `MetricCell.cs:42`'s `Status` is a
non-nullable `string` (fine for `Reading<T>(T Value, string? Reason)` used as `Reading<string>`, but not
"the same shape"), and three different second-member names (`Reason`/`Detail`/`Error`) collapse into one
`Reason` — **a documentation/semantics change**, which `prd.md:48` (风险 2) explicitly forbids ("它是纯
record 替换，行为必须逐点等价；不加新语义"). Renaming `Error`→`Reason` and `Detail`→`Reason` is exactly the
kind of change the plan says it is not making.

Meanwhile AC2's grep is satisfied by converting **only the 17 textual `Value, string? Reason` occurrences**
(14 returns + `MetricSpec.cs:40` + `ControlDrift.cs:286` + `MetricCatalogue.cs:229`'s parameter +
`ArmDenominatorTable.cs:100`'s parameter). It can never match `Result`/`Ok`/`Prefix`/`Comparison`/`Status`,
so AC2 and the prose claim measure different sets.

**Amendment.** Replace AC2 with an explicit site list:

- AC2a: the 14 literal `(double?/JsonElement?/bool? Value, string? Reason)` **declarations** listed above are
  gone, plus `Analysis/Metrics/MetricSpec.cs:40`, `Analysis/Checks/ControlDrift.cs:286`,
  `Analysis/Tables/ArmDenominatorTable.cs:100`, `Analysis/Metrics/MetricCatalogue.cs:229` (parameter position).
- AC2b: decide and record explicitly whether `ArmAccess.cs:23`, `IdentityChecks.cs:93,257,284,309`,
  `BootstrapPair.cs:41`, `MetricCell.cs:42` are converted; if yes, the plan must state that
  `Detail`/`Error`/`Prefix` become `Reason` and that this is an accepted naming change (`MetricCell.cs:42`'s
  non-nullable `Status` stays non-nullable, i.e. `Reading<string>`).
- AC2c: `rg -n 'string\? (Reason|Detail|Error)\)' benchmarks/WinForward.E2E.Analysis --type cs` returns only
  the sites AC2b deliberately kept.

### S2 — N1's effort estimate is an undercount: the type also lives in `Func<>` and parameter positions, and ~22 deconstructions move
Non-return positions that AC2's grep *does* match and that must be rewritten:
`Analysis/Metrics/MetricSpec.cs:40` `Func<CampaignModel, ClientRun, (double? Value, string? Reason)> Extract`,
`Analysis/Checks/ControlDrift.cs:286` `Func<ClientRun, (double? Value, string? Reason)> Extract`,
`Analysis/Tables/ArmDenominatorTable.cs:100` parameter `((double? Value, string? Reason) reading, string label)`,
`Analysis/Metrics/MetricCatalogue.cs:229` parameter `((double? Value, string? Reason) reading)`.

Consumer churn (approximate `rg -c`, named + generic deconstructions ≈ 22):
`MetricCatalogue.cs:159` `var (value, why) = spec.Extract(...)`, `:143`, `:291`;
`MetricComparisons.cs:219` `var (comparison, error) = BootstrapPair.Draw(...)`;
`ControlDrift.cs:198`; `IdentityChecks.cs:51,57,66,72,82,119` `var (result, _) / (ok, detail)`;
`LedgerClientCounts.cs:123,161`; `TableDns.cs:148,174,221`; `TableLatency.cs:89,154`;
`TableLedgerCross.cs:288`; `FindingsCollector.cs:238`; `WindowOverflow.cs:40`.

**Amendment.** State the real edit set (~4 signatures + 14 declarations + ~22 deconstruction sites), and add
to N1's judgement `dotnet build` before the tests, because `Func<...>` type-argument mismatches are the
compile errors a partial conversion produces first.

### S3 — the 3-member sibling is undecided
`Analysis/Stats/CpuDetail.cs:58` returns `(double? Value, string? Reason, CpuDiagnostics Diagnostics)` —
inside the same family, outside the 21, and `MetricCatalogue.cs:291` deconstructs it as
`var (value, why, _)`. An implementer converting `MetricCatalogue.cs:229`'s parameter will meet it on the
same screen. **Amendment:** one line in N1 — "`CpuDetail.cs:58` keeps its tuple (a `Reading<T>` cannot carry
the third member); do not extend the record."

### S4 — the 4th acronym rename is missing from the PRD
`prd.md:18` states "只有 3 处越界"; the plan's own N0.1 (`implement.md:11`) adds a 4th:
`tests/WinForward.E2E.Tests/JsonlSinkTests.cs:35`
`AnIoFailureIsPropagatedUnderTheClientPolicy` (verified present). **Amendment:** make AC1 say "4
identifiers", and fix "41 处行" — the reference count is **26** (verified: `IoError` 16 in
`LaneTransportContracts.cs:29,39,46,103`, `TcpLaneTransport.cs:125,143,150,155`, `UdpLaneTransport.cs:106`,
`LaneEngine.cs:253`, `LatencyTcpPolicy.cs:197`, `LatencyPolicyTests.cs:134,266`,
`LaneTransportTests.cs:156,199,222`; `SocketIo` 7 in `SocketIo.cs:27`, `DnsServer.cs:321,322,334`,
`TcpConnectionProtocol.cs:159,222`, `TruncatedConnectionTests.cs:97`, `README.md:53`; `OsDescription` 3 in
`ArmKeys.Run.cs:30`, `RunFileWriter.cs:68`, `TableEnvironment.cs:89`) — matching `research/02:408`'s
`git grep … | wc -l # expect 26`. Where 41 came from is not reproducible; drop it or replace with 26.

### S5 — the spec doc that spells `IoError` is not in the rename list
`rg -n 'IoError' .trellis/spec` →
`.trellis/spec/backend/measurement-lane-seam.md:19` ("`LaneReceiveKind ∈ {Payload, EndOfStream, Malformed,
IoError}`") and `:25` ("→ `IoError`"). The plan's禁区 (`prd.md:35-41`) names `README.md` and `ArmKeys`, never
`.trellis/spec`. Leaving these makes the spec contradict the code the same task renames.
**Amendment:** add both lines to N0.1's edit list; add ".trellis/spec" to the AC1a grep root.

### S6 — the rule the plan wants to enshrine is false for `src/` (`Ipv4`/`Ipv6`)
`prd.md:17` claims "**已实测**：`IP`/`IPv4`/`IPv6` 与 `Id` 已经合规". True for the four E2E trees
(`rg -o '\b\w*Ip\w*\b'` over the four trees → **0 hits**; the only `IPv` spellings are BCL
`IPAddress.IPv6Any` at `tests/WinForward.E2E.Tests/SocketsTests.cs:95-96`,
`benchmarks/WinForward.E2E/Target/Sockets.cs:95`, and prose at
`benchmarks/WinForward.E2E/Target/SourceCensus.cs:169-170`, `ArmKeys.Ledger.cs:166`). But it is **false for
`src/`, which is where Part A claims to derive the rule from**: `rg -c 'Ipv[46]' src/` = **108 hits in 12
files**, led by `src/WinForward.Protocols/PacketChecksums.cs` (30: `TryRewriteIpv6Tcp`, `RewriteIpv4TcpFull`),
`src/WinForward.Protocols/IPFragment.cs` (18), `src/WinForward.Protocols/UdpFrameBuilder.cs:isIpv4` (16),
`src/WinForward.Protocols/TcpResetBuilder.cs:Ipv6HeaderLength` (16),
`src/WinForward.Windows/AdapterLocalAddressProvider.cs:16:Ipv4Mask`, `src/WinForward.Protocols/UotCodec.cs:74:isIpv4`.
`research/02:50` cites only `IPPrefix`/`IPAddress`/`SupportedOSPlatform` and never mentions these.

Consequence: the 收尾 step (`implement.md:74-76`) would write "IPv4/IPv6 keep `IP` caps" into
`.trellis/spec/backend/directory-structure.md` as a standing rule while `src/` violates it ~108 times — the
next agent will either "fix" the packet path (behaviour-adjacent, out of scope) or distrust the spec.
**Amendment:** state the rule with its scope — "new identifiers use `IPv4`/`IPv6` (matching the BCL's
`IPAddress.IPv6Any`); `src/WinForward.Protocols/*` and `src/WinForward.Windows/*`'s existing `Ipv4`/`Ipv6`
(108 occurrences, 12 files) are grandfathered and out of C2" — and register the `src/` cleanup as a separate
follow-up task for the parent, not as C2 work.

### S7 — the D4/`IsTestProject` spike is under-specified (finding itself *not verified* — see §6)
`implement.md:61-67`. The experiment ("delete, restore, build, count findings") has no decision threshold,
no baseline, and no way to distinguish "fixable" from "not". Whatever survives the removal is *not*
unconstrained: `.editorconfig:250-273` and `:430` already suppress 15+ rules for `[tests/**.cs]` (including
`S1144`, `MA0182`, `MA0040`, `MA0041`, `xUnit1012/1030/1031/2005…`), so the spike measures only the residue.
**Amendment:** before/after `rg -c '"type": "package"' obj/project.assets.json` per test project as the
mechanical proof; run the enumeration build with `-p:TreatWarningsAsErrors=false` so one run yields the whole
list instead of the first error; pre-commit to a threshold (e.g. ≤20 diagnostics and none in a packet/lane
path → fix; more → keep the property) **and pre-commit to the exemption comment text**. Most likely
categories (expectation, not measured): culture/formatting rules on test data (`MA0002`/`MA0011`), string
comparison rules, `MA0051` long test methods, Sonar `S2699`-family assertion rules, `VSTHRD` async rules not
covered by the test glob, and `CA/IDE` naming on fixtures.

### S8 — wrong tool path in AC3
`prd.md:27` says `tools/effective-lines.py <四路径>`; the script lives at
`benchmarks/WinForward.E2E/scripts/effective-lines.py` (`research/02:139`, `:405`). Also note the tool's exit
status is the gate (rc=1 when over the ceiling) — "无输出" alone is the weaker half of the criterion; state
`rc=0`.

---

## NIT

- N1: `Reading<T>`'s "放 `Analysis/Model/Reading.cs` 或最贴近使用者的位置" (`implement.md:28`) is a coin flip
  in an otherwise mechanical plan. Pick `Analysis/Model/Reading.cs` — it is referenced by
  `Metrics/`, `Checks/`, `Findings/`, `Stats/` and `Tables/`, and `Model/` is already the shared vocabulary
  directory (`Model/ArmAccess.cs`, `Model/JsonValue.cs`, `Model/LedgerData.cs`).
- N3: state that the shard `git mv`s must happen **without** an accompanying content edit, so
  `git status` shows `R` — otherwise the rename/history check in `implement.md:48` is not evidence.
- N0.2's four unused `using`s include `Analysis/Findings/LedgerViews.cs:2`, a file N2 splits. Do the using
  deletion first (N0 before N2) or the line moves — the plan's order is already right, but say why.
- `prd.md:40` mentions "3 个把源码路径当字符串读的测试"; the research lists exactly three
  (`ClientSendLossGateTests.cs:165-166`, `TruncatedConnectionTests.cs:93`,
  `ReliabilityTruncationTests.cs:60-61`) — none of C2's renames touches those files' subjects
  (`ReliabilityArm.cs`, `DnsServer.cs`, `ReliabilityExchange.cs` stay put), so this constraint is inert for
  C2. Confirm it stays inert rather than copying it as risk.

---

## Verified true

1. `.editorconfig` has **exactly four** E2E file-exact globs and no wildcard covering the trees:
   `:238` `CampaignQueries.cs`, `:241` `GateValidity.cs`, `:244` `GateFlow.cs`, `:247` `LedgerFindings.cs`;
   everything else is `[*.cs]` (`:295`), `[tests/**.cs]` (`:250`, `:430`), `[src/**.cs]` (`:461`) or the
   Benchmarks path glob (`:437`). `SocketIo.cs`, `ArmContext.cs`, `LedgerViews.cs`, `ArmDenominatorTable.cs`,
   `Json/Rate.cs`, `Json/PerSecond.cs`, `LedgerClientCounts.cs` are **not** globbed → the plan's禁区 list is
   correct and its renames are glob-free.
2. `ArmKeys.Run.OsDescription`'s **member** rename cannot change output:
   (a) `check-readme-contract.py:204` computes the JSON path from the const's **value**, `:205` builds the
   write-site probe as `ArmKeys.{chain}.{member}`, and `:294` greps that probe in the concatenated
   write-site text — a mechanical rename keeps the pair consistent; `:215` skips `ArmKeys.*` files from the
   write sites, so the declaration shard cannot self-satisfy the probe;
   (b) **no** `nameof(ArmKeys.Run.*)` or member-name-derived string exists anywhere —
   `rg -n 'nameof\(ArmKeys'` finds only `tests/WinForward.E2E.Tests/ClientSendLossGateTests.cs:170`
   (`Common.Gates.ClientSendLoss`) and `tests/WinForward.E2E.Tests/TruncatedConnectionTests.cs:100-101`
   (`Ledger.TcpSummary/DnsSummary.TruncatedFrames`); `DeclaredKeys.Under`
   (`tests/WinForward.E2E.Tests/DeclaredKeys.cs:32-48`) reads
   `field.GetRawConstantValue()` — the value, never the name;
   (c) the member is `public` (`benchmarks/WinForward.E2E.Contracts/ArmKeys.Run.cs:30`) but is referenced
   **only** twice outside its declaration: `benchmarks/WinForward.E2E/Client/RunFileWriter.cs:68` and
   `benchmarks/WinForward.E2E.Analysis/Tables/TableEnvironment.cs:89` — **not from the test project**.
   Residual risk: the frozen *value* `"osDescription"` also appears at
   `benchmarks/WinForward.E2E/README.md:408` (doc) and
   `benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py:1360,1435` (synthetic fixture) —
   renaming the value would be output-visible, which is why freezing it is right.
   `JsonKeyLiteralGateTests` collects the known keys from `ArmKeys`'s values and scans for *literals* in key
   positions, so it is blind to a member rename.
3. `Ip`/`Id` in the four trees: zero `Ip`-cased identifiers; the acronym work in the four trees really is the
   4 identifiers, not 60 (`research/02:209-233`'s negative results reproduce).
4. The 400-line gate is currently green over the five relevant files:
   `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py
   benchmarks/WinForward.E2E.Analysis/Findings/LedgerViews.cs benchmarks/WinForward.E2E/Client/ArmContext.cs
   benchmarks/WinForward.E2E.Analysis/Findings/LedgerFindings.cs benchmarks/WinForward.E2E/Client/PlanFile.cs
   tests/WinForward.E2E.Tests/Lanes/LaneTestDoubles.cs` → **rc=0, no output**. (`LedgerViews.cs` and
   `PlanFile.cs` are therefore *under* 400 right now — the plan's 385/375 numbers are plausible but the
   script prints nothing for compliant files, so they were not independently confirmed.)
5. `Reading<T>` can stay `internal` in `WinForward.E2E.Analysis`: all 21 candidate sites live in `internal`
   types (`Analysis/Metrics/MetricCatalogue.cs:33` `internal static class`, `Analysis/Model/ArmAccess.cs:17`,
   `Analysis/Checks/IdentityChecks.cs:27`, `Analysis/Stats/BootstrapPair.cs:38`,
   `Analysis/Tables/GateValidity.cs:20`, `Analysis/Findings/LedgerFindings.cs:21`,
   `Analysis/Metrics/MetricCell.cs:97`), and the Analysis project has **no** `InternalsVisibleTo` (only
   `benchmarks/WinForward.E2E/WinForward.E2E.csproj:8` and
   `benchmarks/WinForward.E2E.Contracts/WinForward.E2E.Contracts.csproj:11` grant it, both to
   `WinForward.E2E.Tests`). The two test-project tuple returns are different shapes
   (`tests/…/LedgerShapeTests.cs:443` `(TcpAcceptLoop Loop, IPEndPoint EndPoint)`,
   `tests/…/SourceCensusTests.cs:159` `(Dictionary<int,long> Placed, long Overflow)`), and the one E2E-side
   tuple (`benchmarks/WinForward.E2E/Client/Arms/MixMetricsWriter.cs:59`) is a 3-tuple — so no cross-assembly
   placement question arises.
6. `LedgerFindings.cs` exists as the partial main file; `LedgerClientCounts.cs`/`LedgerDnsTotals.cs` sit
   beside it as siblings (directory listing verified) — the N3.1 rename direction is right.

## Could not verify without building / was not reached

1. **§6 — the `IsTestProject` restore-time mechanism: NOT VERIFIED.** I did not read `Directory.Build.props`,
   `tests/Directory.Build.props`, `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj` or an
   `obj/project.assets.json`. The claim ("`ExcludeRestorePackageImports=true` at restore ⇒ package props not
   imported ⇒ the explicit `<IsTestProject>true</IsTestProject>` is the only thing turning the root props'
   analyzer `ItemGroup` condition false ⇒ 0 analyzer packages vs 4 for the other 14") is *consistent* with
   `.editorconfig` never having been tuned for this project, but it is unconfirmed. Chevk list for whoever
   resumes: (a) the analyzer `ItemGroup` condition in `Directory.Build.props`; (b) whether
   `Microsoft.NET.Test.Sdk`'s props set `IsTestProject` only during *build* evaluation and therefore arrive
   too late; (c) `rg -c '"type": "package"' tests/*/obj/project.assets.json` for the 15 test projects, and
   `rg -o 'meziantou|sonar|roslynator|Threading' <assets>` per project. The plan must not present this as
   measured until (a)-(c) are run.
2. `.editorconfig`'s glob engine was not executed (needs `dotnet format`, forbidden here). The statement that
   `[benchmarks/WinForward.E2E.Analysis/Findings/LedgerFindings.cs]` does not match
   `LedgerFindings.ClientCounts.cs` is from the pattern's shape (no `*`), not from a run.
3. I did not read C1's plan text. C1↔C2 overlaps below are derived from `research/02:18-41` and `:518-519`,
   not from C1's own `implement.md`.
4. `ArmContext.cs`'s 7 types / `PlanFile.cs` / `LaneTestDoubles.cs`'s 10 types and README line anchors were
   not re-inspected; `README.md:37` was not read.
5. No `rg` was run for the six renamed *file names* across `.trellis/spec`, `README.md`, `.editorconfig` and
   `tests/**` — so **forgotten references to `LedgerClientCounts.cs`, `LedgerDnsTotals.cs`,
   `ArmDenominatorTable.cs`, `Json/Rate.cs`, `Json/PerSecond.cs` may still exist and are the first thing to
   grep before N3.** The research claims only 2 refs for `ArmDenominatorTable` (`research/02:306`) without
   listing them.
6. Part B's "0 dead members", `PosixPathText`'s fate, and any further house-convention deviations
   (file/type mismatch beyond the three renames, `partial` shard naming, extra tuple returns, namespace
   mismatches) were not scanned beyond the tuple census — the tuple census *did* independently reproduce the
   42-declaration total (`39 Analysis + 1 E2E + 2 tests`), which is a good sign for Part B's arithmetic.

---

## Corrected rename list

Acronym renames (code + docs), the complete set:

| # | old | new | sites |
|---|---|---|---|
| 1 | `SocketIo` (type + file) | `SocketIO` (file `Target/SocketIO.cs`) | `Target/SocketIo.cs:27`; `Target/DnsServer.cs:321,322,334`; `Target/TcpConnectionProtocol.cs:159,222`; `tests/…/TruncatedConnectionTests.cs:97`; `README.md:53` |
| 2 | `LaneReceiveKind.IoError` | `LaneReceiveKind.IOError` | 16 lines: `Client/Lanes/LaneTransportContracts.cs:29,39,46,103`; `Client/Lanes/TcpLaneTransport.cs:125,143,150,155`; `Client/Lanes/UdpLaneTransport.cs:106`; `Client/Lanes/LaneEngine.cs:253`; `Client/Arms/LatencyTcpPolicy.cs:197`; `tests/…/Lanes/LatencyPolicyTests.cs:134,266`; `tests/…/Lanes/LaneTransportTests.cs:156,199,222` |
| 3 | `ArmKeys.Run.OsDescription` | `ArmKeys.Run.OSDescription` (value `"osDescription"` frozen) | `Contracts/ArmKeys.Run.cs:30`; `Client/RunFileWriter.cs:68`; `Analysis/Tables/TableEnvironment.cs:89` |
| 4 | `AnIoFailureIsPropagatedUnderTheClientPolicy` | `AnIOFailureIsPropagatedUnderTheClientPolicy` | `tests/WinForward.E2E.Tests/JsonlSinkTests.cs:35` |
| 5 | **`IoError` in prose** (added by this review) | `IOError` | `.trellis/spec/backend/measurement-lane-seam.md:19,25` |

File renames: `LedgerClientCounts.cs → LedgerFindings.ClientCounts.cs`, `LedgerDnsTotals.cs →
LedgerFindings.DnsTotals.cs`, `Tables/ArmDenominatorTable.cs → Tables/TableArmDenominator.cs`,
`Contracts/Json/Rate.cs → Contracts/Json/JsonRate.cs`, `Contracts/Json/PerSecond.cs →
Contracts/Json/JsonPerSecond.cs` (types `JsonRate`/`JsonPerSecond` unchanged; `README.md:344-345` quotes the
*types*, not the files, so it stays valid — `research/02:307`).

**Not renamed, and why (keep):** `Ipv4`/`Ipv6` in `src/` — out of C2 scope, needs its own task (S6);
`PosixPathText`, `CpRandom`, `Verbatim*` — C1 owns their fate (`research/02:34-40`, `:390`).

---

## C1 serialisation (derived from `research/02`, not re-verified against C1's plan)

C2 must not start N1/N4 on these paths until C1 has archived:

| overlap | C1 side | C2 side |
|---|---|---|
| `benchmarks/WinForward.E2E.Analysis/Tables/GateValidity.cs` | `GateValidity.cs` S1244 float-equality re-verify (`research/02:518-519`) | N1's `(double? Value, string? Reason)` at `:275` |
| `benchmarks/WinForward.E2E.Analysis/Tables/GateFlow.cs` | same S1244 re-verify | N4's batch-id/vocabulary sweep could touch it |
| `benchmarks/WinForward.E2E.Analysis/Stats/DescriptiveStats.cs` | Neumaier summation dropped (`research/02:30`) | N1 explicitly skips `:54` — keep skipping |
| `benchmarks/WinForward.E2E.Analysis/Json/VerbatimNumber.cs:189,286` | file deleted | N1 skips it (`research/02:269`) — correct |
| `benchmarks/WinForward.E2E.Analysis/Loading/PythonGlob.cs`, `Model/PosixPathText.cs` | deleted / undecided | N0.2 touches `Loading/LedgerLoader.cs:1` — different file, but re-run `rg` after C1 |
| `benchmarks/WinForward.E2E.Analysis/Stats/CpRandom.cs`, `Json/VerbatimJson.cs`, `Json/PythonExponential.cs`, `tests/…/Analyzer{Random,Number,Json}GoldenTests.cs`, `Analysis/verification/golden/*.json` | deleted | no C2 item names them — safe, but N5's `IsTestProject` spike changes the *analyzer* set over the surviving test files, so it must come after C1's deletions or the findings count is measured against corpses |

Additional serialisation: C1's `Int(double)` dedup is C2's N0.3 boundary (`implement.md:17`) — C2 must
re-grep for `private static string Int(double)` after C1 archives, because C1 may have moved it and C2's
`Repeated`×3 dedup would otherwise create a second home for the same helper.
