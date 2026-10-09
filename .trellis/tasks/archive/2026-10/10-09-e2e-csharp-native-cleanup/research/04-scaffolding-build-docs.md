# 04 — Non-code scaffolding: build plumbing, data/assets, documentation, archaeology

> Research for `.trellis/tasks/10-09-e2e-csharp-native-cleanup` (deliverable C4, `e2e-docs-and-scaffolding`).
> Read-only investigation. Nothing was modified, created or deleted; the only file written is this one.
> HEAD at investigation time: `f992370` (`chore: record journal`). No `dotnet build`/`test`/`format` and no
> `jb inspectcode` was run. Two dependency-free Python gates **were** executed (they read files only and
> write nothing): `effective-lines.py` and `check-readme-contract.py` — their results are marked
> "**executed**" wherever they appear. Every other "the gate is red" statement is derived from source,
> `ls` and `git`, and is marked as such.

## 0. Method, and one correction to the task brief

Evidence is `rg -n --heading`, `git ls-files`, `git check-ignore -v`, `git status --porcelain`,
`git log`, `du`, `wc`, `tar -tzf` (listing only), and `python3` for structural JSON comparison.

**Correction:** the plan key is `seconds`, not `durationSeconds`.
`rg -n --heading -F 'durationSeconds' benchmarks/WinForward.E2E` → **zero hits**; the only occurrences in
the repository are in the *other* benchmark project
(`benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs:75`). Plan schema:
`benchmarks/WinForward.E2E/Client/PlanFile.cs:13`, `:323`; default `Seconds = 60` at
`benchmarks/WinForward.E2E/Client/ArmSpec.cs:28`; identity keys `name`/`kind`/`seconds` at
`benchmarks/WinForward.E2E/Client/Arms/ArmKind.cs:13`.

Two facts that condition several findings:

- There is **no `benchmarks/Directory.Build.props`** (`fd -H 'Directory\.(Build|Packages)\.' .` returns only
  the root, `src/`, `tests/`). MSBuild therefore walks `benchmarks/WinForward.E2E/` → `benchmarks/` →
  repository root and finds the root props. All four benchmark projects inherit it.
- **CI never runs `dotnet build` or `dotnet test`.** `.github/workflows/analyzer-gate.yml` runs
  `dotnet format` (`:27`) and `jb inspectcode` (`:49`); `.github/workflows/release-build.yml` runs
  `dotnet restore`/`dotnet publish` only (`:24,27,40,54,64`). `rg -n --heading -i 'e2e|benchmarks/'
  .github/` → **zero hits**: no workflow names an E2E project at all.

---

## 1. Build and repo plumbing

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `Directory.Build.props:1-19` vs `benchmarks/WinForward.E2E/WinForward.E2E.csproj:1-13`, `benchmarks/WinForward.E2E.Contracts/WinForward.E2E.Contracts.csproj:1-13`, `benchmarks/WinForward.E2E.Analysis/WinForward.E2E.Analysis.csproj:1-13` | **No property gap.** Because `benchmarks/` has no `Directory.Build.props`, all three inherit `TargetFramework`, `LangVersion`, `Nullable=enable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel=latest`, trim/AOT analyzers and the four analyzer packages. Evidence: `benchmarks/WinForward.E2E/obj/project.assets.json` resolves exactly the same four analyzer packages as `src/` (`Meziantou`, `Roslynator`, `SonarAnalyzer`, `VisualStudio.Threading`). Comparison baseline `src/WinForward.Core/WinForward.Core.csproj:1-15` also sets nothing local. | Leave alone; add nothing to `benchmarks/`. | None — but see the next row: this only holds *because* no `benchmarks/Directory.Build.props` exists. Adding one later without the explicit `Import` would silently strip every property from all four projects (`tests/Directory.Build.props:2-5` is the precedent comment). |
| `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj:4` | **`<IsTestProject>true</IsTestProject>` is set by exactly one project in the repository** (`rg -n --heading 'IsTestProject' -g '*.csproj' .` → this line, plus `tests/WinForward.TestSupport/WinForward.TestSupport.csproj:11` which sets `false`). It is not cosmetic: `Directory.Build.props:14` conditions the analyzer `ItemGroup` on `'$(IsTestProject)' != 'true'`, so this one line is the **only** thing that turns the four analyzers off. Measured: `tests/WinForward.E2E.Tests/obj/project.assets.json` resolves 0 analyzer packages, while `tests/WinForward.Core.Tests`, `…Protocols.Tests`, `…Integration.Tests` and even `…TestSupport` all resolve 4. Assets mtime `2026-10-08 07:09:57`, csproj mtime `2026-10-08 07:08:51` — the restore post-dates the edit, so the reading is current. | Decide deliberately, and write the decision down. Option A (matches the other 14 test projects): delete line 4 — E2E.Tests then gets the analyzers like every other test project. Option B (matches the file's apparent intent): keep it *and* state in a comment that this project deliberately opts out of the four analyzers, and note that the other 14 never honoured the condition because `IsTestProject` is set too late for it. | **High for Option A.** Deleting the line turns on 4 analyzers under `TreatWarningsAsErrors=true` across ~90 test files. `.editorconfig:217-273` documents measured hit counts for the `tests/**.cs` tree (MA0040 30 hits, xUnit1030 94, xUnit1031 2, MA0051 2, VSTHRD002 4, S1144 1, S2699 3, S927 4 …), which suggests the suppressions were measured with the analyzers **on** — so much of the surface is already covered — but the size of the remaining delta is unknown without a Release build. Do not do this blind. |
| `WinForward.slnx:31-36` | Folder placement is **consistent**: all four benchmark projects sit under `<Folder Name="/benchmarks/">`, alongside `/src/`, `/tests/`, `/analyzers/`. `<Folder Name="/src/">` at `:3-5` carries a 2-space indent where the rest of the file uses 4 — cosmetic, pre-existing, unrelated to E2E. | No move. Optionally normalise the `:3-5` indent in a separate whitespace-only commit. | None. |
| `WinForward.slnx:33-35` vs `.github/workflows/analyzer-gate.yml:27,49` | **Do not exclude the E2E projects from `dotnet build WinForward.slnx -c Release`.** The repository's two enforced analyzer gates operate on the *solution* file: `analyzer-gate.yml:27` runs `dotnet format WinForward.slnx --severity info --verify-no-changes` and `:49` runs `jb inspectcode … WinForward.slnx`. Those two commands are the only automated quality signal the four benchmark projects have. The on-demand path does not need slnx membership — `benchmarks/WinForward.E2E/scripts/publish.sh:13` runs its own `dotnet build -c Release` inside `benchmarks/WinForward.E2E/` — so removing them from the slnx buys nothing and silently removes 4 projects + 1 test project from both gates. | Keep them in `WinForward.slnx`. | Excluding them would silently drop the only automated checks on `benchmarks/WinForward.E2E*` and `tests/WinForward.E2E.Tests`. |
| `.github/workflows/analyzer-gate.yml:23-27,45-49`; `.github/workflows/release-build.yml:23-24,26-71` | **Gap: CI runs neither `dotnet build WinForward.slnx -c Release` nor `dotnet test WinForward.slnx -c Release`.** `analyzer-gate.yml` = restore → `dotnet format` → (second job) restore → `jb inspectcode` → assert zero issues. `release-build.yml` = restore → four `dotnet publish` of `src/WinForward.Cli`. `AGENTS.md` names both commands as commit gates, and `.trellis/spec/backend/quality-guidelines.md:125` cites `.github/workflows/analyzer-gate.yml` as their home ("alongside the `-c Release` zero-warning build and a green `-c Release` test run (`AGENTS.md`; `.github/workflows/analyzer-gate.yml`)") — that file runs neither. `quality-guidelines.md:94` repeats the Release requirement. | Either add `dotnet build WinForward.slnx -c Release` + `dotnet test WinForward.slnx -c Release` as a CI job, or correct `quality-guidelines.md:125` to say the two commands are local-only. | The 1,660-test suite (`quality-guidelines.md:91`, measured 2026-10-09) has **no CI enforcement point at all**. Any test-only regression — including every gate the E2E work leans on (`CliSnapshotTests`, `JsonKeyLiteralGateTests`, `ContractShapeTests`, `ObjectDisposedCatchGateTests`) — merges green. |
| `.github/workflows/**` | **Gap: no Python gate runs in CI.** `rg -n --heading -i 'analyze\.sh\|check-fairness\|check-readme-contract\|cli-snapshots\|compare-records\|contract-inventory\|effective-lines\|jsonl_paths\|normalize-pattern-hits\|oracle-diff\|\.py' .github/` → exit 1, zero hits. `.trellis/spec/backend/measurement-tooling.md:80` already concedes `compare-records.py` and `effective-lines.py` are manual-only, but `directory-structure.md:100-103` names `effective-lines.py` as **the** 400-effective-line gate ("The gate is `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py <paths>` (exit 1 when a file is over)"). | Either wire `effective-lines.py` (and `check-readme-contract.py`) into a CI job, or demote the wording in `directory-structure.md:103` from "the gate" to "the check, run by hand". | The 400-line ceiling — which `benchmarks/README.md:426-442` uses to register three known over-limit files — has **no enforcement point**, so it silently rots; `check-readme-contract.py` is already red (§3.2). |
| **executed** `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests` | **Measured: exit 0, no output.** The whole E2E tree — harness, contracts, analyzer and the four-project test suite — is under the 400-effective-line ceiling today. The counter works from a plain checkout (no build needed). For contrast, the same script over `benchmarks/WinForward.Benchmarks` returns exit 1 with exactly the three figures `benchmarks/README.md:432-437` publishes (909 / 684 / 410), so the README's registered-debt section is **accurate and reproducible**. | Nothing to fix in the tree. If the ceiling is to have a gate, this is the command to wire — it is fast and dependency-free. | None. |
| `.gitignore:2-8,28`; `benchmarks/WinForward.E2E/.gitignore:4-11` vs disk | **Nothing generated is tracked by mistake.** `git ls-files \| rg '(^\|/)(bin\|obj)/'` → 0 hits; `git ls-files BenchmarkDotNet.Artifacts` → 0; `git ls-files '*.pyc'` → 0; `git ls-files \| rg '\.(dll\|exe\|pdb\|nupkg\|tgz\|zip)$'` → 0 outside `.trellis/tasks/archive/**`; `git status --porcelain benchmarks/ tests/WinForward.E2E.Tests/` → empty. The 13 MB `BenchmarkDotNet.Artifacts/` at the repository root is ignored by `.gitignore:8`; both `__pycache__` directories are ignored by `.gitignore:28` (`git check-ignore -v` confirms for `benchmarks/WinForward.E2E/scripts/__pycache__` and `benchmarks/WinForward.E2E.Analysis/verification/__pycache__`). | Nothing to untrack. Optionally delete the two ignored output trees (§6). | None. |
| `.gitignore:18` | `AGENTS.local.md` (no slash → matches at **any** depth) is what keeps `benchmarks/WinForward.E2E/AGENTS.local.md` out of the repository, while the tracked root `AGENTS.local.md:1` tells every agent to go read it. The consequence is that the **only** description of how a real campaign is launched is unreviewable and has drifted (§3.4; §7 row 10). | Either track a sanitised copy (addresses/credentials redacted into placeholders) as `benchmarks/WinForward.E2E/docs/runbook.md`, or add a tracked, machine-independent "how a campaign is launched" section to the harness README and shrink `AGENTS.local.md` to the machine facts only. | Keeping it untracked means the drift can never be caught by review; the runbook is cited by `.trellis/spec` nowhere. |
| `.editorconfig:236-239` | E2E-specific suppression 1/4: `[benchmarks/WinForward.E2E.Analysis/Model/CampaignQueries.cs]` → `resharper_convert_to_extension_block_highlighting = none`, with its reason immediately above at `:236-237` ("almost all extension methods … the project keeps the classic static-class form"). | Keep as is — the reason is adjacent and repo-verifiable. | None. |
| `.editorconfig:233-235` vs `:241-248` | **E2E-specific suppression 2–4/4 with a misplaced rationale.** Three sections pin `dotnet_diagnostic.S1244.severity = none` (float equality) for `Tables/GateValidity.cs`, `Tables/GateFlow.cs` and `Findings/LedgerFindings.cs`. The comment that justifies them ("The analysis reproduces the reference's own float comparisons: an accounting identity that must hold exactly, a denominator that is exactly zero, a wall time that is exactly zero.") sits at `:233-235`, directly above the **CampaignQueries** header at `:238`, not above the three S1244 headers at `:241`, `:244`, `:247`. A reader auditing the S1244 entries finds the CampaignQueries reason instead. | Move `:233-235` down so it heads the `:241-248` block, and leave `:236-237` on the CampaignQueries header. | None; pure auditability. But `quality-guidelines.md:126` requires every suppression's reason to be verifiable against this repository, and today these three are not adjacent to theirs. |
| `.editorconfig:241-248` vs `benchmarks/WinForward.E2E.Analysis/Stats/OlsSlope.cs:73-75`, `Model/RunClocks.cs:29-31`, `Model/ArmAccess.cs:152-154` | **Two mechanisms for one rule.** Three files suppress S1244 by glob; three sites suppress it by local `#pragma warning disable S1244 // <reason>` (`OlsSlope.cs:73`, `RunClocks.cs:29`, `ArmAccess.cs:152`). `quality-guidelines.md:126` states the preference order ("a localized `#pragma warning disable <RULE> // <reason>`, or a glob-scoped `.editorconfig` `severity = none` entry"). The globs cover `GateValidity.cs:101,119,148`, `GateFlow.cs:213`, `LedgerFindings.cs:235,249`. | Convert the three globs to inline pragmas (7 sites, each gets its own one-line reason), or state in the `.editorconfig` block why these three files are glob- not site-scoped. | None beyond churn; the pragma form is what the rest of the file-length-adjacent code already uses. |
| `.editorconfig:437` | The only `benchmarks/`-scoped glob in the whole file is for the **other** benchmark project (`[benchmarks/WinForward.Benchmarks/Perf/**.cs]` → `resharper_unused_auto_property_accessor_global_highlighting = none`). The E2E tree has exactly the four sections above and no blanket suppression. | Nothing to remove; record the count (4) so a later "why does E2E have special cases?" question has an answer. | None. |
| `src/WinForward.Cli/WinForward.Cli.csproj:24` (only `InvariantGlobalization` in the repository) | **Checked, not a gap.** `WinForward.E2E.Analysis` formats numbers to match a frozen golden but pins `CultureInfo.InvariantCulture` explicitly at 30 sites (`rg -c --no-heading 'CultureInfo\.InvariantCulture' benchmarks/WinForward.E2E.Analysis` → 30 files), and `CliSnapshotTests.cs:180` parses with `NumberStyles.Integer, CultureInfo.InvariantCulture`. So the absence of `InvariantGlobalization` on the analyzer is not a latent culture bug today. | Leave alone; note it as the one property the analyzer does *not* share with the only other output-comparing binary. | If a future edit drops an `InvariantCulture`, `AnalyzerNumberGoldenTests` would catch it only on a host whose culture differs — i.e. not on this one. `InvariantGlobalization=true` on `WinForward.E2E.Analysis` would make that class of mistake fail everywhere. Low priority, cheap. |
| `benchmarks/WinForward.E2E.Analysis/WinForward.E2E.Analysis.csproj:1-13` | Its comments are gone but the file carries two blank-line-delimited `PropertyGroup`/`ItemGroup` blocks and a trailing blank line before `</Project>` (`:12`), unlike the terse `benchmarks/WinForward.E2E/WinForward.E2E.csproj`. Purely cosmetic. | Fold into C2 (naming/idiom) if it is touched at all. | None. |

---

## 2. Data and asset files

### 2.1 `benchmarks/WinForward.E2E/scripts/plans*/`

Twelve files in three directories. `plans/` = 6 (base, dns, dual, full, selftest, udp); `plans-short/` = 5
(the same minus `selftest-plan`); `plans-windows/` = 1 (`full-shape-plan.json`). Every file has exactly one
top-level key, `arms`.

**Quantified duplication** (leaf-level comparison over JSON pointers; the numbers below were produced by
flattening each document to `(pointer, scalar)` pairs and diffing the maps):

| Pair | Union leaves | Identical | Differing | What differs |
|---|---|---|---|---|
| `plans/base-plan.json` ↔ `plans-short/base-plan.json` | 5 | 4 | 1 | `/arms[0]/seconds` 60 → 12 |
| `plans/dns-plan.json` ↔ `plans-short/dns-plan.json` | 12 | 8 | 4 | both `seconds` 90 → 18; both `ratePerSecond` 200 → 100 |
| `plans/dual-plan.json` ↔ `plans-short/dual-plan.json` | 18 | 13 | 5 | three `seconds` 40 → 8; `LAT ratePerSecond` 40 → 20; `LOSS ratePerSecond` 300 → 150 |
| `plans/full-plan.json` ↔ `plans-short/full-plan.json` | 56 | 39 | **17** | 10 `seconds`; `LATLOAD`/`DNS`/`DNSALT`/`LOSS` `ratePerSecond` ÷2; `REL connectionsPerSecond` 20 → 10; `THRU targetBytesPerSecond` 60 000 000 → 15 000 000; `PERSIST idleSeconds` 25 → 6 |
| `plans/udp-plan.json` ↔ `plans-short/udp-plan.json` | 20 | 15 | 5 | three `seconds`; `LATLOAD`/`LOSS` `ratePerSecond` ÷2 |
| **`plans/full-plan.json` ↔ `plans-windows/full-shape-plan.json`** | **56** | **47** | **9** | **all nine are `seconds`** (IDLE 60→12, LAT 60→12, LATLOAD 60→24, DNS 90→24, DNSALT 90→24, LOSS 120→120 (unchanged), REL 120→24, THRU 60→12, MIX 120→24, PERSIST 90→45); **zero structural differences** |

Arm order, arm names, per-arm key sets and per-arm key order are identical across all three `full-plan`
variants. So:

- `plans-windows/full-shape-plan.json` is **exactly** `plans/full-plan.json` with nine `seconds` values
  overridden — 47 of 56 leaves are verbatim identical, 0 leaves differ in any key other than `seconds`.
  This matches the description in `benchmarks/WinForward.E2E/Client/RepoPaths.cs:15-19` and
  `benchmarks/WinForward.E2E/README.md:60`.
- `plans-short/*` is **not** a pure duration compression: even after ignoring every `seconds` leaf,
  `plans-short/full-plan.json` still differs from `plans/full-plan.json` in **7** leaves
  (`ratePerSecond` ×5, `connectionsPerSecond`, `targetBytesPerSecond`, `idleSeconds`). The README is
  accurate ("the same arm shapes at short durations **and lower rates**", `README.md:59`).

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `benchmarks/WinForward.E2E/Client/PlanFile.cs:97-131`, `:397-400`; `Cli/ClientOptions.cs:34,48,107-115`; `Cli/CommandLine.cs:41-71`; `Client/ClientRunner.cs:14` | **The loader supports no override mechanism.** The root must be an object with an `arms` array (`:121-127`); every key an arm declares must be in its kind's whitelist or loading fails (`:397-400`); `--plan` takes exactly one path, and a repeated flag is a last-one-wins overwrite, not a merge (`CommandLine.cs:41-71` calls `apply` per occurrence; `ClientOptions.cs:114` assigns `Path`); there is no `extends`/`include`/`patch`/`override` token anywhere in `PlanFile.cs`; no environment override exists (`rg -n --heading -F 'GetEnvironmentVariable' benchmarks/WinForward.E2E/` → zero hits). The only defaults are the per-field `ArmSpec` initializers (`ArmSpec.cs:24-71`, `Seconds = 60` at `:28`) and the built-in 8-arm plan used when no path is given (`PlanFile.cs:48-61`, selected `:186-190`). | If the plan sets are to be deduplicated, the override seam has to be **built first** — it does not exist. Two shapes are cheap and fit the loader: (a) a top-level `overrides: [{name, seconds, …}]` applied after `arms` is read, or (b) a `--plan` repeat where later files patch earlier ones by arm name. Either way it is new loader behaviour plus new validation tests, not a pure data edit. | Medium. `PlanFileValidationTests` is a large existing suite (19 fixtures under `tests/WinForward.E2E.Tests/Fixtures/plans/`) and `PlanFileTests.cs:27` asserts `Assert.Equal(12, plans.Length)` over the three directories — a merge mechanism plus a plan-set change touches both. Also `D14.14` semantics ("zero means not declared", `PlanFile.cs:72-75`) make an override of `0` ambiguous with "do not override" unless the patch layer distinguishes absent from zero. |
| `benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json:1-80` | **The single cleanest dedup target in the whole task**: 9 of 56 leaves differ from `plans/full-plan.json`, all `seconds`. Today it is also the least anchored file in the tree — `rg -n --heading -F 'full-shape-plan' .` → **zero hits**; the only consumer of the file is the directory glob in `tests/WinForward.E2E.Tests/PlanFileTests.cs:88-92` plus the count at `:27` (`Assert.Equal(12, plans.Length)`). No script, doc, workflow or spec names it. | Two options. (a) If the override seam is built: delete `plans-windows/` and express it as `--plan plans/full-plan.json --overrides plans-windows.json` (or equivalent); the test glob shrinks to 11 and `RepoPaths.WindowsPlansDirectory` (`RepoPaths.cs:20`) goes with it. (b) If no seam is built: keep the file but make it *referenced* — name it in `README.md:60`'s row as the actual filename (the row currently describes it without naming it) and add it to `orchestrator.ps1`'s plan vocabulary so it stops being an orphan. | Low for (b). Medium for (a): `PlanFileTests.cs:27`'s hard count and `:79-92` must move in the same commit, and `selftest.sh:17`'s usage string enumerates all three directories. |
| `benchmarks/WinForward.E2E/scripts/plans-short/**` (5 files, 4.6 KB total) | **Also orphaned, but with one live consumer**: `tests/WinForward.E2E.Tests/PlanFileTests.cs:79` loads `plans-short/base-plan.json` by name ("a given path overrides the built-in plan"), and `:90` globs the directory. Referenced in prose by `README.md:59,142` and `selftest.sh:17`. **No campaign path ever runs a short plan**: `deploy-campaign.sh:52-54` stages exactly `full udp dns dual base`; `orchestrator.ps1:80,85,90,95,99,103,109,301,383` uses `full`/`udp`/`dns`/`dual`/`base` only. | Keep `plans-short/base-plan.json` (a test depends on it by name); the other four are exercised only by the glob at `PlanFileTests.cs:88-92`. If the override seam lands, keep exactly one short variant as the seam's own test fixture and delete the rest. | Low. The 12-file count at `PlanFileTests.cs:27` is the only hard coupling. |
| `benchmarks/WinForward.E2E/scripts/plans/selftest-plan.json:1-86` | Its 11 arms are the full-plan shape at 5–10 s. It is named only in prose (`README.md:58`, `README.md:128`) — `selftest.sh:11` takes the path as `$1`. | Keep: it is the only plan whose purpose (a whole-harness smoke in ~90 s, `README.md:124-143`) is not reproducible by compression alone (it adds `BASE`, which `full-plan` does not have). | None. |
| `benchmarks/WinForward.E2E/scripts/plans/full-plan.json:1-80` vs `selftest-plan.json` | The two overlap in 10 of 11 arms. `selftest-plan` is `full-plan` plus `BASE` minus nothing, at 1/12 durations and lower rates — i.e. the same kind of derivation as `plans-short/full-plan.json`, which already exists. | If the override seam lands, `selftest-plan` collapses to "`plans/full-plan.json` + short durations + `BASE`", and `plans-short/` and `plans-windows/` are two named override files rather than eleven copies. | See the loader row. |

**Total duplication if an override seam existed:** 11 of the 12 plan files are derivable from the 5 in
`plans/` plus two override tables (short ≈ 17+20 leaves, windows = 9 leaves); only `plans/` itself is
irreducible, and `plans/selftest-plan.json` differs from `plans/full-plan.json` by adding one arm.

### 2.2 `benchmarks/WinForward.E2E/scripts/configs/`

Six files, 4 933 bytes total, all tracked. They are complete and consistent with the product set, but **no
repository file names any of them**.

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `scripts/configs/wf-aot-opt.json`, `wf-aot-nativeudp.json`, `wf-aot-dnsrelay.json`, `wf-fdd-opt.json`, `proxifyre-app-config.json`, `proxybridge.pbprofile` | **Zero name-based references anywhere in the repository.** The sweep `rg -n --heading -F '<name>' .` returns exit 1 for each of the six. They are wired by *deployed* path and by size-checked upload: `benchmarks/WinForward.E2E/scripts/deploy-campaign.sh:43-49` (untracked/local) copies them to `C:\wfbench\wf-aot\config.json`, `…\config-nativeudp.json`, `…\config-dnsrelay.json`, `C:\wfbench\wf-fdd\config.json`, `C:\wfbench\proxybridge\bench.pbprofile` and `C:\wfbench\stage\proxifyre-app-config.json`; `benchmarks/WinForward.E2E/scripts/orchestrator.ps1:79,84,89,94,99,108-109` then reads those deployed paths as each row's `ConfigSource`. | Add a `configs/` table to the harness README mapping **repo filename → product row → deployed path**, so the mapping lives in a tracked file rather than only in an ignored script. The README already promises the set (`README.md:61`) but never lists it. | Low, documentation only — but the mapping is the only thing that makes the six files reviewable. Today a reader cannot tell which of the six belongs to which of the seven orchestrator rows. |
| `scripts/configs/wf-fdd-opt.json` vs `scripts/configs/wf-aot-opt.json` | **Byte-identical duplicate** (`diff` is empty; both 972 bytes, 60 lines). `orchestrator.ps1:83-85` runs `wf-fdd-opt` from `C:\wfbench\wf-fdd\config.json` — a framework-dependent build of the *same* configuration. | Keep the deploy-time copy but stop storing two files: have `deploy-campaign.sh` upload `wf-aot-opt.json` twice, or make `wf-fdd-opt.json` a one-line note. | Low, **but** the duplicate exists because the two rows are deliberately compared as a build-vs-build delta (`orchestrator.ps1:10-12`: "`wf-aot-opt` is the reference, `wf-fdd-opt` changes only the build"). If the two configs ever diverge, the delta stops being attributable — so the dedup must preserve byte-identity, not merely equivalence. |
| `scripts/configs/wf-aot-opt.json:7,29-38,53-59` vs `wf-aot-nativeudp.json:7,29-38,53-59` | The two differ only in `udpOverTcp` (`true` vs `false`) — 1 line of 60 — and both carry the same `remotePort:["53"]` rule and `localTargets[dns]`. This is the "changes only the UDP carriage" row pair (`orchestrator.ps1:10-12`). | Keep both; they are the product-delta design, not duplication to remove. | None. |
| `orchestrator.ps1:103,365` | **`bench.ppx` (Proxifier) exists in no repository path.** `rg -n --heading -F 'bench.ppx' .` hits only those two orchestrator lines; `deploy-campaign.sh:49` stages it from `/tmp/wf-bench/bench.ppx`. The Proxifier row is therefore configured from a file the repository does not ship — while `README.md:74-82` lists only `AGENTS.local.md` as machine-local and says "Nothing in this document depends on those files" (`README.md:84`). | Add `bench.ppx` to `README.md:74-82`'s machine-local table, or commit a sanitised Proxifier profile next to `proxybridge.pbprofile`. | The comparison's Proxifier arm is not reproducible from a fresh checkout, and the README says it is. |
| `orchestrator.ps1:99` + `deploy-campaign.sh:48,59-60` | `proxifyre-app-config.json` is staged into `C:\wfbench\stage\` and then `Copy-Item`-ed over `C:\Program Files\ProxiFyre\app-config.json`. The indirection is deliberate but is documented nowhere tracked. | Same table as above; one line. | Low. |
| `benchmarks/WinForward.E2E/scripts/publish-campaign.sh:8,36-37` (untracked/local) | **The local campaign collector still points at the retired Python-era tree.** `results="$repo/benchmarks/results/2026-10-06-e2e-competitors"` (`:8`) — that directory does not exist (`ls` → no such file; `benchmarks/results/` has no such entry). `:36` then does `cd "$results/analysis"` — the `analysis/` subdirectory of the deleted tree — and `:37` runs `analyze.sh --raw "$raw" --out "$results"`. The script cannot get past `cd`, so **the documented end-to-end campaign loop is broken today**. | Repoint it at a live location, e.g. `benchmarks/results/<campaign-date>/` with `cd "$results"` and `--out "$results"`. Fix in the same change as the runbook (§3). | Medium: this is the only script that closes the loop "run on the VM → pull results → analyse", and `AGENTS.local.md:163` instructs the reader to use it. Both files are gitignored, so nothing in review catches the rot. |

### 2.3 `benchmarks/WinForward.E2E.Analysis/verification/`

1.6 MB, 14 tracked files + 1 gitignored `.pyc`. `verification/FROZEN.md` (176 lines) is the record of the
frozen set; **all twelve sha256 it publishes were re-measured and match byte-for-byte**, and the two line
counts it states (`FROZEN.md:19` "1245 lines", `:20` "14125 lines") are exact.

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `verification/golden/py-tables.md` (1245 lines, 203 KB), `verification/golden/py-verdict.json` (14125 lines, 470 KB) | **Consumed by exactly one thing: `benchmarks/WinForward.E2E/scripts/oracle-diff.py`** (`:108` `GOLDEN`, `:127` `GOLDEN_NAMES`, `:263` reads `py-verdict.json` for the metrics member list, `:1099` `load_artifact`, `:1171` `--golden` default). **No test reads them** and no CI job runs the differ. What breaks if they are dropped: every `oracle-diff.py` invocation exits **2** ("something that should exist does not" — `:264-265` returns `None`, `:255-258` fails, `:1092-1102` fails on the missing slice). What keeps them honest: `FROZEN.md:114-127` ("A change to `synthetic/make_tree.py` invalidates the frozen golden … there is no partial re-freeze"; after any edit, re-diff from batch 1) plus the sha256 in `FROZEN.md:19-20`. | Keep. These two are the entire point of the frozen oracle: they are the only record of what the retired Python implementation answered (`FROZEN.md:8-10`), and the D21 relaxation is measured *against* them. Note that they are inert unless someone runs `oracle-diff.py` — see the CI-gap row in §1 and the C1 plan for the RNG swap, whose widened tolerance must be recorded in `FROZEN.md`. | Deleting either removes the only cross-implementation reference; nothing else in the tree can substitute, because `analyze.py` is gone (`FROZEN.md:97-112` restores it from `git show b3b4aa4c^:…`, verified to resolve). |
| `verification/golden/cp-random-vectors.json` (3121 lines), `py-number-vectors.json` (1996 lines), `py-json-vectors.json` (119 lines) | **Live, hard dependencies of three test classes**, via `RepoPaths.AnalyzerGolden` (`tests/WinForward.E2E.Tests/RepoPaths.cs:28-29`): `AnalyzerRandomGoldenTests.cs:22`, `AnalyzerNumberGoldenTests.cs:21,40,60`, `AnalyzerJsonGoldenTests.cs:19`. **There is no skip logic anywhere in the project** — `rg -n --heading 'Skip\|SkippableFact' tests/WinForward.E2E.Tests/` finds none, so deletion is a hard `FileNotFoundException`, not a skip. Each test also has an anti-emptying tooth: `AnalyzerJsonGoldenTests.cs:29` (≥10 cases), `AnalyzerNumberGoldenTests.cs:33,53,77` (≥200 / ≥120 / ≥36), `AnalyzerRandomGoldenTests.cs:16,55-56` (≥18 seeds, ≥600 draws). | **These are the C1 collateral.** If `CpRandom`, `VerbatimNumber` and `VerbatimJson` are replaced with `System.Random` and ordinary .NET formatting (the user's 2026-10-09 ruling), all three vector files and all three test classes go with them. That is a deliberate deletion, not a cleanup: make it part of C1's commit and record it in `FROZEN.md` §1.1 (which currently presents them as frozen) **and** in `.trellis/spec/backend/measurement-tooling.md:125`. | Medium. `FROZEN.md:43-63` and `measurement-tooling.md:125` both describe these as frozen; deleting them without updating both leaves two live documents asserting a set that no longer exists. `RepoPaths.cs:22-29`'s doc comment also goes. |
| `verification/plots-SKIPPED.md` (13 lines, 899 B) | Consumed by `oracle-diff.py:110`, `:1068-1069` (missing ⇒ exit 2) and `:1074-1079` (text differs ⇒ exit 1). The C# side does **not** read it: `Cli/PlotsNotice.cs:17-32` embeds its own copy of the text and writes it at `:35-46`. So the frozen file is the only mechanical check that the embedded copy still says what it said. | Keep. It is 899 bytes guarding a user-visible output the analysis writes unconditionally. | Deleting it makes `oracle-diff.py` exit 2 on every run and removes the only cross-check on `PlotsNotice`'s embedded text. |
| `verification/row-profiles.json` (118 lines) | Consumed by **one** caller, `benchmarks/WinForward.E2E/scripts/check-fairness.py:120` (validated `:122-130`; the four guards are documented at `check-fairness.py:9-18`). The C# analyzer does **not** read it — `Model/RowProfiles.cs:55-62,68-79,112-128,148-214` hardcodes the whole table — and both sides say so (`Tables/TableRowProfiles.cs:34-39`; `row-profiles.json:8-9`: "The same declarations live in the analysis's own source (`Model/RowProfiles.cs`); the checker is what keeps the two honest"). | Keep, and say in `FROZEN.md:25` that its only purpose is the cross-check — otherwise a future reader sees a data file with no code consumer and deletes it. | Deleting it makes `check-fairness.py` exit 2 and removes the only expectation source independent of the hardcoded C# table. This is the single most delete-tempting file in `verification/` and the one with the least obvious consumer. |
| `verification/synthetic-tree.tar.gz` (527 622 B, 472 entries, 7 284 KiB uncompressed) | **Consumed by exactly one thing**: `oracle-diff.py:109` (`ARCHIVE`), extracted at `:280-281`, layout-asserted at `:285-292`. Empty-grep proof (excluding the `verification/` tree itself and `.jsonl` dumps): `rg -n --heading -g '!**/*.jsonl' -g '!benchmarks/WinForward.E2E.Analysis/verification/**' 'synthetic-tree' .` → **1 hit**. Regenerable: `verification/freeze-tree.sh:19` runs `make_tree.py`, `:22-27` re-tars with `--sort=name --mtime='2026-10-01 00:00:00 UTC' --owner=0 --group=0 --numeric-owner --format=gnu`; `tar -tvzf` shows every entry `0/0` at that mtime, so the recipe is deterministic. Its sha256 matches `FROZEN.md:18`. | Keep. It is the input both the golden and the C# output are produced from; `oracle-diff.py` exits 2 without it. | It is the largest tracked binary inside `benchmarks/` (515 KiB) and the only committed tarball in the tree. It is not "tracked by mistake" — `FROZEN.md:16-29` is its justification — but it is worth noting it is the only artifact whose integrity depends on a recipe that rewrites the file in place (`freeze-tree.sh:15,26`), so a re-freeze is a repository write, never a verification step. |
| `verification/check-fixture-drift.py:40` | **Live but broken: a dead hard-coded path.** `RESEARCH = REPO_ROOT / ".trellis" / "tasks" / "10-07-e2e-harness-refactor" / "research"`, made the default `--root` at `:108`. That directory does not exist — the task was archived by `aee1955` — so `contract_paths()` (`:65-66`) raises `FileNotFoundError` before any check runs. `.trellis/spec/backend/measurement-tooling.md:130` already records this ("it resolves its fixtures from the same archived task path (`check-fixture-drift.py:40`) and needs the same fix"), but `FROZEN.md:23,86,121` still presents it as a live re-freeze step (§2 of that recipe cannot run). | Fix the path — `tests/WinForward.E2E.Tests/RepoPaths.cs:49-67` is the archive-aware lookup the .NET side already uses, and `check-fixture-drift.py` should mirror it. Either way, decide whether the file stays a frozen-set member (`FROZEN.md:23`) or is retired. | Low fix, but **do not delete it silently**: it is the only mechanical guard that the synthetic fixture's key set equals the harness's (`FROZEN.md:116` makes it a re-freeze trigger). Deleting it without a replacement drops a documented invariant. |
| `verification/check-boundary-trees.py` (20 KB) | **No automated consumer** — the sweep `rg -n --heading -g '*.sh' -g '*.yml' -g '*.yaml' -g '*.ps1' -g '*.csproj' -g '*.slnx' 'check-boundary-trees\|check-fixture-drift\|freeze-tree\|oracle-diff\|make_cp_vectors\|synthetic-tree\|row-profiles\|plots-SKIPPED' .` returns **exactly one hit — `freeze-tree.sh:15`, its own `ARCHIVE=` line**. Documented only by `WinForward.E2E.Analysis/README.md:499`, `measurement-tooling.md:129` and `FROZEN.md:24,150-152,154`. It carries the five boundary-tree assertions and their negative controls. | Keep. | None; but it is dead weight in the same sense as the golden: nothing runs it unless a human does. |
| `verification/synthetic/make_tree.py`, `make_cp_vectors.py`, `freeze-tree.sh` | `make_tree.py` is invoked by `freeze-tree.sh:19`, `check-fairness.py:421-430` and `check-boundary-trees.py:399-404,419,426,442`. `make_cp_vectors.py` is invoked by **nothing** — only doc comments name it (`RepoPaths.cs:25`, `Json/VerbatimNumber.cs:23`, `Stats/CpRandom.cs:20`), so it is manual-only per `FROZEN.md:61-63`. `freeze-tree.sh` likewise has no caller. | `make_tree.py` keep. `make_cp_vectors.py` + `freeze-tree.sh` keep **only if** the C1 RNG swap leaves vector tables behind; if the three vector JSONs go (§ above), `make_cp_vectors.py` has no reason to exist and its hash row `FROZEN.md:54` goes with it. | Low, and entirely contingent on C1's outcome — do this row *after* C1, not before. |
| `verification/__pycache__/check-boundary-trees.cpython-314.pyc` | Ignored output: untracked (`git ls-files …` = 0, `git ls-files '*.pyc'` = 0 repo-wide) and matched by `.gitignore:28`. Not stale (the pyc records source mtime `1791445036` / size `20208`, equal to the current `check-boundary-trees.py`). Same for `benchmarks/WinForward.E2E/scripts/__pycache__/` (7 `.pyc` files, 232 KB, mtime `2026-10-09 09:48`). | Delete both on sight; they regenerate. See §6. | None. |
| `verification/FROZEN.md:35,92,137,148,172` | **Five stale paths inside the frozen-set record itself.** All five cite `.trellis/tasks/10-07-e2e-harness-refactor/…` — the *pre-archive* location; all five are missing there and present under `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/…`. Specifically `:35` `research/baseline/E4c-caveats.md`, `:92` `research/python-oracle-changes.md`, `:137` `research/baseline/E4b5-semantic.md`, `:148` `research/baseline/E4b3-semantic.md`, `:172` `python-oracle-changes.md §5`. Also `.trellis/tasks/10-07-e2e-harness-refactor/research/baseline/E4c-caveats.md` at `:35` is the only place `FROZEN.md` records the re-freeze evidence. While a re-freeze is already hypothetical, `FROZEN.md:114-127` presents these as the steps to follow. | Rewrite the five to the archive-aware form (or to "the archived task `10-07-e2e-harness-refactor`, `research/…`"). | Low; but the whole point of `FROZEN.md` is that a future reader can reproduce the frozen set, and §3's first step currently dead-ends. |

### 2.4 `benchmarks/results/**` and the root `BenchmarkDotNet.Artifacts/`

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `benchmarks/results/` | **Meant to be tracked, and it is.** `git ls-files benchmarks/results \| wc -l` = 282; files on disk = 282 — the two sorted lists are identical, so nothing under it is untracked and nothing in it is ignored. `git check-ignore -v` on real samples exits 1. `git status --porcelain benchmarks/results` is empty. 20 directories, 3.4 MB; file kinds 129 `.jsonl`, 70 `.csv`, 57 `.md`, 20 `.txt`, 3 `.log`, 1 `.patch`, 1 `.html`, 1 extensionless (`2026-08-29-proxy-hardening/stability-final`). **No generated junk**: no `bin/`, no `obj/`, no `BenchmarkDotNet.Artifacts/` anywhere under it, and the `bdn-*` sub-trees (`2026-08-30-windows-vm/bdn-linux-inproc/`, `…/bdn-win/`) and every `.csv`/`.md`/`.html` report are tracked. | Keep as is. §47 of the task PRD already excludes it ("`benchmarks/results/**` 里的历史 campaign 数据"). | None. |
| `benchmarks/README.md` vs `benchmarks/results/` | **The 10 results directories the README names all exist** (references at `benchmarks/README.md:69,122,123,124,180,204,211,214,279,280,282,310,324,347,374,375,395,414`). **Ten directories on disk are never named there**: `2026-08-29-proxy-hardening`, `2026-08-29-socks5-perf`, `2026-08-29-udp-fix`, `2026-08-29-windows-real-machine`, `2026-09-06-udp-burst-ttl-fix`, `2026-09-30-expiry-sweep-bounded-pause`, `2026-09-30-flow-key-parse-once`, `2026-09-30-warm-path-lock-chain`, `2026-10-01-attribution-off-pump`, `2026-10-06-aot-instruction-set` (several are cited from other docs' `README.md`). There is **no dangling reference**: nothing the README names is missing. | Add a one-line index of the 20 directories to `benchmarks/README.md`, or state that the directory listing is the index. | Low. Without an index, half the evidence base is reachable only by `ls`. |
| `benchmarks/results/2026-10-06-e2e-competitors/` | **Absent, and correctly so** — it became `benchmarks/WinForward.E2E.Analysis/` in `b3b4aa4` (`git log --diff-filter=R --name-status -M -- 'benchmarks/**'` shows `R088 benchmarks/results/2026-10-06-e2e-competitors/analysis/README.md → benchmarks/WinForward.E2E.Analysis/README.md`; `analyze.py` was deleted in the same commit). `FROZEN.md:100,106` reference the old path **on purpose**, as a git-history recipe, and both were verified to resolve (`git cat-file -e b3b4aa4c^:benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py` → ok). | Leave `FROZEN.md` alone on this point. | None — this is the one place the old path must survive. Contrast `publish-campaign.sh:8,36` (§2.2), where the same old path is a live bug. |
| `BenchmarkDotNet.Artifacts/` (repository root) | **Not tracked.** 13 MB (12 365 093 B), 170 files (114 `.log`, 20 `.md`, 18 `.html`, 18 `.csv`; `results/` holds 56 files). `git ls-files BenchmarkDotNet.Artifacts` → 0; `git check-ignore -v BenchmarkDotNet.Artifacts` → `.gitignore:8:BenchmarkDotNet.Artifacts/`. It is the documented output of `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*'` (`benchmarks/README.md:17-30`). Its only textual mention in the repository is `benchmarks/results/2026-08-29-windows-real-machine/README.md:59`. | Safe to delete (§6), with the caveat that the raw logs are not provably summarised in that one README. | Low. It is ignored output; the risk is only that someone wanted the raw logs. |

---

## 3. Documentation

Three documents, 145 KB / 1 645 lines: `benchmarks/README.md` (442 lines, 36 737 B),
`benchmarks/WinForward.E2E/README.md` (704 lines, 62 966 B),
`benchmarks/WinForward.E2E.Analysis/README.md` (499 lines, 35 083 B), plus the gitignored
`benchmarks/WinForward.E2E/AGENTS.local.md` (206 lines, 10 775 B). Detailed claims are in §7; what
follows is the structural picture, the gate-load-bearing surface, and the duplication measurement.

### 3.1 Duplication is *topical*, not textual

Measured with a line-level set intersection over normalised lines ≥40 characters: `benchmarks/README.md`
∩ `WinForward.E2E/README.md` = **0** shared lines; `benchmarks/README.md` ∩ `Analysis/README.md` = **0**;
`WinForward.E2E/README.md` ∩ `Analysis/README.md` = **2** (both are the same two `analyze.sh` command
examples). So there are no copy-pasted paragraphs to delete. What there *is* is a set of subjects each
document covers from its own side:

| Subject | Copy 1 | Copy 2 | Overlap shape |
|---|---|---|---|
| Running the analyzer | `WinForward.E2E/README.md:586-606` | `Analysis/README.md:24-52` | Both give the `analyze.sh` invocation and explain that it never changes the working directory. Copy 1 is 21 lines of narrative; copy 2 is 8 lines of narrative + a 7-row flag table (`:41-49`). Copy 1 ends by delegating ("The flags, the inputs, the aggregation policy and every table are described in [README.md](../WinForward.E2E.Analysis/README.md)", `:603-604`) — the delegation is correct, the duplicated invocation is not needed twice. |
| The analysis's outputs | `WinForward.E2E/README.md:605-606` | `Analysis/README.md:6-10` | `plots/SKIPPED.md` and "writes `tables.md` and `verdict.json`" appear in both. Copy 2 owns it (a 5-row table); copy 1's sentence is a pointer. |
| The key contract | `WinForward.E2E/README.md:372-447` (canonical, gate-read) | `Analysis/README.md:12-22` | Copy 2 restates *why* the shared `Contracts` project makes a rename a compile error, and names the checker. Not duplication — but it uses different words for the same invariant, so the two must be edited together. |
| The gate semantics | `WinForward.E2E/README.md:448-500` ("Gates and validity" — the **record's** gate keys) | `Analysis/README.md:150-189` ("The gates" — section 3.1/3.2 of `tables.md`) | Different objects with the same name. A reader who greps `gates` lands in either. `Analysis/README.md:152` even says "Section 3.1 is the flow gate table" without saying these are *not* the `gates/*` record keys. |
| The verification recipe | `WinForward.E2E/README.md:672-704` | `Analysis/README.md:475-499` | Both end the document with "here is what a healthy run looks like". Copy 1's is a 16-row table of record-level checks; copy 2's is a 3-step recipe. Neither references the other. |
| `check-readme-contract.py` | `WinForward.E2E/README.md:70,390-393,431,694` | `Analysis/README.md:20` | Copy 1 owns it; copy 2 links to it. Fine. |
| The row set | `Analysis/README.md:98-107` (8-row table) | `WinForward.E2E/README.md:7` (5 products) + `scripts/orchestrator.ps1:76-111` (7 product rows) | Three descriptions of the same object at three granularities. The orchestrator is the only one that is executable. |

### 3.2 What is load-bearing — "edit with care, a gate reads this"

Only **one** machine reads `benchmarks/WinForward.E2E/README.md`, and it reads exactly one section.
`rg -n --heading -i 'readme' tests/WinForward.E2E.Tests/` → **zero hits**: no test reads any README.

| README part | `file:line` | Machine that reads it | What happens on a careless edit |
|---|---|---|---|
| The `## Which keys are contract` **heading** | `WinForward.E2E/README.md:372` | `scripts/check-readme-contract.py:51` (`SECTION_HEADING = "## Which keys are contract"`, matched by `text.find` at `:160`) | Renaming or re-wording the heading makes the checker exit **2** with `no '## Which keys are contract' heading` (`:161-162`). It is a literal, not a regex. |
| Everything from that heading to the **next** `\n## ` — today lines **372–447**, ending at `## Gates and validity` (`:448`) | `WinForward.E2E/README.md:372-447` | `check-readme-contract.py:157-165` (the section "up to the next top-level heading") | The extraction is *positional*: `rest.find("\n## ")` at `:164`. Moving the section, inserting a `## ` heading inside it, or promoting a subsection to `## ` **shrinks the scanned region silently** — the checker fails on tokens it sees, never on tokens that disappeared, so a key documented outside the region is simply no longer covered. Tables **and prose** are read (`:11-12`). |
| Every backticked key path in 372–447, plus the five bare root names the table spells | `WinForward.E2E/README.md:372-447` | `check-readme-contract.py:53-60` (write roots), `:118-132` (`KEY_RE`, `LEGACY_TOKENS`, `DOCUMENTED_NON_KEYS`), `:279` (fails when the section names no key) | Each token must resolve to a `const string` in `ArmKeys.*.cs` **and** be referenced by a write site in `Cli/`, `Client/`, `Target/`, `Wire/`, `Program.cs` or `Contracts` (`:53-60`). Three tokens are declared exceptions and dropping them is an edit, not a silent pass: `LEGACY_TOKENS = {"metrics/clientSendLoss"}` (`:127`) and `DOCUMENTED_NON_KEYS = {"parameters/window", "parameters/loss.lossWindowMs"}` (`:132`). |
| The script table (lines 58–72) | `WinForward.E2E/README.md:58-72` | nobody mechanically — but `.trellis/spec/backend/measurement-tooling.md:65` cites it **by line number** | Adding or removing a row above line 72 invalidates the spec's `README.md:58-72` citation. |
| The whole document's line numbering | `WinForward.E2E/README.md:1-704` | `.trellis/spec/backend/measurement-tooling.md:9` cites `"Which keys are contract" section (line 372)` | Any insertion or deletion above line 372 makes that citation wrong. **This is the practical constraint on every README edit in C4**: either keep the line count above 372 stable, or update `measurement-tooling.md:9` in the same commit. |

Everything else in the README is prose a human reads. The `README.md:694-697` claim that three gates
"run the same way" is a *claim*, not a parsed surface — and it is wrong (§7 row 2).

**The gate is red right now, measured.** `python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py`
was executed and exited **2** with

```
cannot read /home/paff/Projects/WinForward/.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json:
[Errno 2] No such file or directory
```

which is exactly what `.trellis/spec/backend/measurement-tooling.md:41-43` predicts ("As shipped the checker
exits **2** and prints only `cannot read …/contract-rename.json: [Errno 2] No such file or directory` — it
never reaches its checks"). So the section *is* gate-load-bearing, and the gate that reads it is currently
inert: no key in `README.md:372-447` is being checked at all. Fixing `check-readme-contract.py:47` is a
prerequisite for trusting any contract-table edit in C4, not a follow-up.

### 3.3 What is *not* broken (checked, negative results)

These were the specific suspicions worth settling, and none of them holds:

- **No stale `analysis/` path in any README.** `rg -n --heading 'analysis/|/analysis|analyze\.py|e2e-competitors' benchmarks/README.md benchmarks/WinForward.E2E/README.md benchmarks/WinForward.E2E.Analysis/README.md benchmarks/WinForward.E2E/AGENTS.local.md` → **zero hits in all four**. The only surviving `benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py` reference is `verification/FROZEN.md:100,106`, and there it is a deliberate `git show` recipe (verified to resolve against `b3b4aa4c^`).
- **The batch/section table agrees with the code.** `.trellis/spec/backend/measurement-tooling.md:116` claims 16 `## N.` sections; `Tables/TablesWriter.cs:51-69` declares exactly 16 headings (`## 0.` … `## 15.`), and `scripts/oracle-diff.py:129-181`'s `BATCH_SECTIONS` owns exactly those 16 plus the verdict keys, with `:250-258` failing if any slice is unowned. The README's own `Section N` cross-references all resolve: `Analysis/README.md:95` → `## 1.` (`TablesWriter.cs:54`), `:127` → `## 9.` (`:62`), `:152` → `### 3.1`/`### 3.2` (`Tables/TableGates.cs:36,46`; the frozen reference emits them at `golden/py-tables.md:333,388`), `:183` → `## 5.` (`TablesWriter.cs:58`), `:192` → `## 0.` (`:53`), `:215` → `## 13.` (`:66`), `:225` → `## 12.` (`:65`), `:234` → `## 10.` (`:63`), `:241` → `## 14.` (`:67`). **No batch/section contradiction exists.**
- **The orchestrator parameter table is exactly right.** `WinForward.E2E/README.md:269-281` lists 13 parameters with defaults; `scripts/orchestrator.ps1:22-43` declares the same 13 with the same defaults, one for one (including `-Seed = 20261006`, `-DnsPortAlt = 40053`, `-TcpPortDirect = 40011`, `-DnsPortDirect = 40054`). This is the one large table in the READMEs that needs no fixing — worth keeping as the model for a generated table.
- **The row table's *content* is right; only its shape is off.** `Analysis/README.md:98-107` presents 8 rows (7 products + one merged `control-pre, control-post` row) while `Model/RowProfiles.cs:68-79` declares **9** ids in `Order` (`control-pre` first, `control-post` last) and `verification/row-profiles.json` declares the same 9 under `order` and `rows`. Merging the two control blocks into one table row is a legitimate presentation choice, but the merged row's plan column reads "BASE only" (`:107`) whereas `PlanArms["BASE only"] = ["BASE"]` (`RowProfiles.cs:61`) — correct — and the *order* the code asserts (control-pre **first**, control-post **last**, `:70,78`) is not visible in the table, which lists the merged row last. One extra line in the table (split the two ids) would make the README's row set and order match the code's exactly.
- **`benchmarks/README.md` references 10 results directories and all 10 exist**; no dangling path except `:13` (the archived `08-17-performance-hotspots` task dir, §7 row 4).
- **The arm count claim holds.** `WinForward.E2E/README.md:356` says "Ten arms" and the table at `:361-370` holds ten rows. Note the asymmetry that `LATLOAD` gets its own row while `DNSALT` is prose-only (`:356-357`), even though both are second instances of a kind (`ArmKind.cs:19-21` for `latency`, `:33` for `dns`) — and `plans/full-plan.json` contains **10** arm entries of which `DNSALT` is one, while the README's ten rows swap `DNSALT` for `BASE`, an arm no campaign plan's `full` set runs. Cosmetic, but it is why the table cannot be generated from `ArmKind`.

### 3.4 `AGENTS.local.md`

`benchmarks/WinForward.E2E/AGENTS.local.md` (206 lines, untracked via `.gitignore:18`, pointed at by the
tracked root `AGENTS.local.md:1`) is **mostly current**. Checked section by section:

| Section | Verdict |
|---|---|
| §1–§3 machines, firewall, port table (`:7-45`) | Consistent with `scripts/start-targets.sh:28-33` (40010/40011/53/40053/40054) and `orchestrator.ps1:22-43`'s defaults. Machine facts; not verifiable from the repository by design. |
| §4 `wf.sh` wrapper (`:47-78`) | Describes the gitignored `scripts/wf.sh`, which exists. |
| §5 VM install state (`:80-120`) | `:95` lists `full-plan.json / udp-plan.json / dns-plan.json / dual-plan.json / base-plan.json` in `C:\wfbench\e2e` — matches `deploy-campaign.sh:52-54` and `orchestrator.ps1:25`. `:91-92` names the **deployed** config names (`config.json`, `config-nativeudp.json`, `config-dnsrelay.json`) which match `orchestrator.ps1:79,89,94`; the repo-side names differ (§2.2). `:93` `proxifier\ bench.ppx` — the file has no repository source (§2.2). |
| §7 targets (`:128-141`) | `:141` cites "`README.md` 的 'Run the target'" — that heading exists at `WinForward.E2E/README.md:177`. Correct. |
| §8 running a campaign (`:143-164`) | `:146-148` match `publish.sh`, `deploy-campaign.sh`, `start-targets.sh`. **`:163` `scripts/publish-campaign.sh` "…再跑分析" is broken** — the script `cd`s into a directory that no longer exists (§7 row 10). |
| §9 self-test (`:166-174`) | `:170` `scripts/selftest.sh scripts/plans/selftest-plan.json` matches `README.md:128`. Correct. |
| §10 judging criteria (`:176-194`) | Record-level checks; each one corresponds to a key the analysis or the README names (`gates.clientSendLoss`, `foreignConnection`, the UDP identity, `tcp.laneSupplied[]`, `directLeak`). Consistent. |
| §11 known-product-defect pointers (`:196-206`) | All three `.trellis/tasks/10-06-e2e-competitor-benchmark/research/…` paths **exist** (that task is still live, `ls -d` confirms). Correct. |

So the runbook is not stale except for the one broken script it instructs the reader to run. Its real
problem is architectural, not factual: the machine-independent half (the product set, the config
mapping, the campaign steps, the judging criteria) is unreviewable because the file is ignored. That is
the `benchmarks/WinForward.E2E/docs/runbook.md` proposal in §8.

---

## 4. Migration archaeology in comments

Two different kinds of Python-era residue, and they should be treated differently.

### 4.1 Decision IDs

`rg -o --no-heading -g '*.cs' 'D[0-9]{1,2}(\.[0-9]+)?' benchmarks/WinForward.E2E{,Contracts,.Analysis} tests/WinForward.E2E.Tests`,
tallied:

| ID | Occurrences | Where it points today |
|---|---|---|
| `D18.5` | 43 | `LaneEngine.cs:25,127,235,250`, `LaneCounts.cs:16`, `ILanePolicy.cs:4,33,53,60,68,71`, `LaneEngineOrderTests.cs:9`, `LatencyPolicyTests.cs:16,193,392`, `LaneEngineConcurrencyTests.cs:82`, `LaneCountsDisjointnessTests.cs:10`, `LaneTestDoubles.cs:182,303` … |
| `D19.3` | 18 | ledger/listener accounting; e.g. `LaneTransportTests.cs:181`, `TruncatedConnectionTests.cs:87`, `LedgerShapeTests.cs:193`, `ArmKeys.Ledger.cs:32,218` |
| `D19.2` | 17 | teardown vocabulary; `ObjectDisposedTeardownTests.cs:17`, `measurement-run-lifecycle.md:87` |
| `D18.6` | 16 | lane send/receive contract; `LaneEngineSendTests.cs:147,250`, `LatencyPolicyTests.cs:17,141,276`, `LaneTransportTests.cs:13,142` |
| `D14.17` | 11 | `ArmKeys.*` shard layout; `ArmKeys.Run.cs:11`, `ArmKeys.Reliability.cs:34,172`, `ArmKeys.Ledger.cs:24,43,402` |
| `D18.1`, `D18.2`, `D18.3`, `D18.4` | 14 | same family |
| `D14.6`, `D14.7`, `D14.16`, `D14.23`, `D20.5`, `D20.6`, `E2-d`, `E3-d` | 3–6 each | mixed |
| `D12`, `D14.4`, `D14.12`, `D14.20`, `D19.x` remainders | 1–3 each | mixed |

The definitions all live in **one archived file**:
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md` (1000 lines), with
`D18.` at `:572-704`, `D19.` at `:705-762`, `D20.` at `:797-880`, `D21.` at `:881-937`, `D22.` at `:938-969`,
`D23.` at `:970+`.

**Which IDs a live document still cites** — measured with word boundaries over `.trellis/spec/`
(`rg -n --heading -w 'D20|D21|D22|D23' .trellis/spec/`, `rg -n --heading 'E4-[a-z0-9]|b1b|B1b' .trellis/spec/`):

- `D20`, `D21`, `D22`, `D23`, `E4-*`, `b1b` → **zero hits.** The task brief's suspicion that a spec still
  cites `D20.x`/`E4-b1b` is **not** borne out. Those IDs appear only in the archived design records and in
  `verification/FROZEN.md:8,31,38,43,56,99` (`E4-a`, `E4-b1b`, `E4-c`, `D6.6`, `D20.1`, `D20.5`, `D21.1`).
- `D14.7` → `.trellis/spec/backend/measurement-run-lifecycle.md:25`
- `D19.2` → `.trellis/spec/backend/measurement-run-lifecycle.md:87` (inside a code block that quotes the
  comment verbatim)
- `D19.3` → `.trellis/spec/backend/measurement-judgement.md:73`

So exactly **three** decision IDs are load-bearing for a live document, and `D18.*` — by far the densest
family, 73 occurrences — is cited by **nothing** outside the archived task.

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `Client/Lanes/ILanePolicy.cs:4,33,53,60,68,71`; `Client/Lanes/LaneEngine.cs:25,127,235,250`; `Client/Lanes/LaneCounts.cs:16`; `Client/Arms/UdpLatencyPolicy.cs` (13 ID hits, the densest file); `Client/Arms/LatencyTcpPolicy.cs` (8) | **`D18.5`/`D18.6` are the single largest archaeology block: 59 occurrences across 14 files, all in the lane seam**, and the definitions live at `design-decisions.md:650-704` inside an archived task. A reader following `D18.5 #7` today has to know to look in `.trellis/tasks/archive/2026-10/`. The comments themselves are *behavioural* — e.g. `LaneEngine.cs:127` "The offer loop's one ordering rule (D18.5 #3): the previous slot's settlements are …" — so the sentence is useful and only the parenthetical is dead weight. | Rewrite as plain statements of current behaviour, dropping the ID: `LaneEngine.cs:127` → "The offer loop's one ordering rule: the previous slot's settlements are …". Where the ID is the *reason* and the reason is not otherwise stated (e.g. `ILanePolicy.cs:33` "Thread contract (D18.2, D18.5 #2/#4)"), inline the rule as a sentence instead. This is exactly the C2 "naming and idiom" pass, and it is mechanical. | Low per site, but it is 59 edits. **Do not delete a D18.x mention that carries a rule the spec does not restate** — check `measurement-lane-seam.md` first for coverage. |
| `Client/Arms/UdpLatencyPolicy.cs` (13 hits), `Client/Arms/LatencyTcpPolicy.cs` (8) | The two densest files are *policy* files whose comments are mostly the rule text plus an ID. Same treatment as above. | Fold into C2. | Low. |
| `tests/WinForward.E2E.Tests/TruncatedConnectionTests.cs:15,87`; `Lanes/LaneEngineSendTests.cs:147,250`; `Lanes/LatencyPolicyTests.cs:16,17,141,193,276,392`; `Lanes/LaneEngineOrderTests.cs:9`; `Lanes/LaneEngineConcurrencyTests.cs:82`; `Lanes/LaneCountsDisjointnessTests.cs:10,37`; `Lanes/LaneTestDoubles.cs:182,303`; `ObjectDisposedTeardownTests.cs:17`; `LedgerShapeTests.cs:40,193` | Test-side counterparts of the same IDs. In a test the ID often *is* the whole rationale ("this asserts D18.5 #6"). | Rewrite as the assertion's reason ("settlement is called on success and on failure alike"), or — cheaper and defensible — keep the ID **and** add one line naming what it asserts. Prefer the rewrite; the test name usually already says it. | Low. |
| `.trellis/spec/backend/measurement-run-lifecycle.md:25,87`; `measurement-judgement.md:73` | **These three citations are the reason `D14.7`/`D19.2`/`D19.3` must not be scrubbed.** `measurement-run-lifecycle.md:87` literally reproduces a source comment (`/* teardown closed the socket first: … (D19.2 ⑨) */`) — so removing the ID from that source comment breaks the spec's quotation. | Keep these three IDs in the code. Before any rewrite pass, run the spec-grep and produce an allow-list; treat it as the input to the C2 sweep. | Medium if ignored: a blanket `D18.*`/`D19.*` scrub silently invalidates `measurement-run-lifecycle.md:87`. |
| `verification/FROZEN.md:8,31,38,43,56,99`; `scripts/oracle-diff.py:183`; `scripts/contract-inventory.py:60,76,77`; `scripts/check-fairness.py:7`; `verification/check-boundary-trees.py:27`; `tests/WinForward.E2E.Tests/RepoPaths.cs:43`; `CliSnapshotTests.cs:8,20,22,26,73,120`; `ClientOptionsTests.cs:71`; `TargetOptionsTests.cs:68`; `Tables/TablesWriter.cs:18` | **The `E2-d`/`E3-d`/`E4-*`/`D6.6`/`D20.5`/`D21.1` layer.** These are not stray: `E2-d` is *the identity of the frozen CLI snapshot trees* (`CliSnapshotTests.cs:20-29` — "The before tree is what a binary that predates E2-d printed"), and `E4-a`/`E4-b1b`/`E4-c` are *the provenance stamps of the frozen artifacts* (`FROZEN.md:27,31,43`). | **Keep, with one clarifying clause.** These IDs are load-bearing as provenance/version labels even though no spec defines them: "predates `E2-d`" names a specific commit era, and a reader who deletes it cannot tell which snapshot tree is which. Add a one-line legend to `FROZEN.md` §1 and to the `CliSnapshotTests` class summary naming the parent task, so the ID has a resolvable referent. | Deleting them destroys the provenance of the frozen fixtures — the one thing `FROZEN.md` exists to preserve. |
| `scripts/oracle-diff.py:183` | "`--batch 1` is the whole first batch; the sub-batches exist so the port can be landed in halves." A comment about how the *port* was sequenced, not about how the differ behaves today. | Rewrite to what the flag does now ("`--batch 1` selects the whole first batch; `1a/1b/1c` select its slices"). | Low. |
| `scripts/normalize-pattern-hits.py` | Feeds only `compare-records.py`, which is itself uncalled by anything (§1 CI gap). "Normalization patterns" is Python-era vocabulary. | Decide with `compare-records.py` in C3: keep the pair as a documented manual tool, or retire both. Do not retire one without the other. | Low. |

### 4.2 Reference-implementation prose

`rg -c --no-heading 'the reference|reference implementation|the Python|CPython|ported from|port of'` over the
four E2E projects, ranked:

| File | Hits | Character |
|---|---|---|
| `benchmarks/WinForward.E2E.Analysis/Verdict/VerdictSections.cs` | 9 | mostly "in the reference's own words / declaration order" — i.e. the *frozen golden dictates this order*. Load-bearing: `:25` "(D20.5). The thresholds text is the reference's own prose, down to its en dash and its asterisks". |
| `Stats/CpRandom.cs` | 6 | the file is the Python RNG port; C1 deletes it. |
| `Loading/RunLoader.cs` | 6 | reference-compatible loading order. |
| `Metrics/MetricCell.cs` (4), `MetricComparisons.cs` (3), `Model/RunClocks.cs` (3), `Loading/JsonReader.cs` (3), `Loading/PythonGlob.cs` (3), `Stats/DescriptiveStats.cs` (3) | 3–4 each | mixed: `PythonGlob.cs` is a port of `glob.glob` (C1 candidate); `DescriptiveStats.cs` is compensated summation, which `measurement-tooling.md:142-143` says is required **because the reference does it** and has its own anchor test. |
| 40+ further files | 1–2 each | one-line "the reference's own ordering/format" notes. |

| `file:line` | Finding | Proposal | Risk |
|---|---|---|---|
| `Analysis/Stats/CpRandom.cs` (whole file), `Json/VerbatimNumber.cs:103`, `Json/VerbatimJson.cs`, `Json/PythonExponential.cs`, `Loading/PythonGlob.cs`, `Stats/DescriptiveStats.cs` | **The "reference" mentions here are the file's reason to exist** — these six are the CPython-emulation layer the user's 2026-10-09 ruling retires ("不要为了复刻 RNG 而保留 RNG", `prd.md:96-103`). Rewriting their comments is pointless; they are deleted by C1. | Let C1 delete them and their language with them. Do not spend a C2 pass here. | Sequencing: C2 must not rewrite comments in files C1 deletes (the PRD's own R3 says C1 first). |
| `Analysis/Verdict/VerdictSections.cs:10,22,25,34,37,40,51,226,229` | **These "reference's own words" comments are load-bearing and must survive.** The frozen `py-verdict.json` (`verification/golden/py-verdict.json`, 14125 lines) *is* the reference's output; the strings at `:25` must match it byte-for-byte or `oracle-diff.py` fails. "In the reference's own declaration order" is the only statement of why the key order is what it is. | Keep, but make the referent concrete the first time: "in the retired reference's own declaration order (`verification/golden/py-verdict.json`)". | Deleting or softening these invites a reordering that the oracle then rejects — with no hint as to why. |
| `Analysis/Stats/DescriptiveStats.cs` and `.trellis/spec/backend/measurement-tooling.md:142-143` | The compensated-summation rule is required *because the reference does it*: "Records whose numbers are summed for the report follow CPython's compensated summation when the reference does (`Stats/DescriptiveStats.cs`); that rule has its own anchor test rather than relying on a comparison." | Keep both; this is precisely the "a decision ID / rationale a live spec still cites" category the brief asks to preserve. | Removing it would re-introduce a summation difference that the spec explicitly calls out as needing its own anchor test. |
| 40+ files with 1–2 "the reference's own …" mentions | Each is a one-line rationale for an ordering, a rounding, or a null convention. Individually trivial; collectively they are the reason a reader believes the analyzer is a transcription. | Rewrite the ones that describe **current behaviour** ("the reference publishes a design absence as null" → "a design absence is published as JSON null") and keep the ones that name the **frozen artifact** the value must match. A per-file decision is needed; a blanket `sed` is not appropriate. | Low individually; the mechanical sweep in the PRD's AC1 (`rg -i 'cpython' …` 只剩 `FROZEN.md` 等文档) will surface them, and each needs a yes/no. |
| `tests/WinForward.E2E.Tests/Analyzer{Json,Number,Random}GoldenTests.cs`, `AnalyzerStatsAnchorTests.cs`, `RepoPaths.cs:22-29` | Their class summaries and fixture doc-comments explain why the golden files exist. Those files are the C1 deletion set. | Delete with C1; do not rewrite. | Sequencing only. |

**Density summary for the plan:** decision-ID archaeology is concentrated (59 hits in 14 lane-seam files +
a long tail of 1–2-hit sites), the reference prose is diffuse (≈100 mentions over ≈55 files, of which
6 files are pure CPython emulation and disappear with C1), and **three IDs (`D14.7`, `D19.2`, `D19.3`) plus
the `E2-d`/`E4-*` provenance stamps must be preserved**.

---

## 5. "Tracked by mistake"

Measured with `git ls-files` ∩ the on-disk listing. **The list is empty.**

| Candidate | Verdict | Proof |
|---|---|---|
| `bin/`, `obj/` under any E2E project | **Not tracked.** | `git ls-files \| rg '(^\|/)(bin\|obj)/'` → 0 hits, while **683** such files exist on disk across the four E2E projects (`WinForward.E2E` 199, `…Contracts` 58, `…Analysis` 82, `tests/WinForward.E2E.Tests` 344) — all ignored by `.gitignore:2-3`. |
| `BenchmarkDotNet.Artifacts/` (root) | **Not tracked.** | `git ls-files BenchmarkDotNet.Artifacts \| wc -l` → 0; `git check-ignore -v BenchmarkDotNet.Artifacts` → `.gitignore:8`. 13 MB / 170 files on disk. |
| `__pycache__/`, `*.pyc` | **Not tracked.** | `git ls-files '*.pyc'` → 0; `git check-ignore -v benchmarks/WinForward.E2E/scripts/__pycache__` → `.gitignore:28:__pycache__/`; same for `verification/__pycache__`. |
| Published trees (`publish/`, `dist/`) | **Not tracked.** | `git ls-files \| rg '(publish\|dist)/'` → 0; `.gitignore:16` ignores `dist/`. |
| `.trellis/tasks/archive/**` (285 files, 3.6 MB, incl. the CLI snapshot fixtures) | **Tracked deliberately**, not a mistake — but see §8 and the row below. | `git ls-files .trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/` → 285 files, of which 181 are `research/cli-snapshots/**`. `git check-ignore -v` on `research/contract-rename.json` → exit 1 (not ignored). |
| Any other binary under `benchmarks/` | **One, justified.** | `git ls-files -z \| xargs -0 du -b \| sort -rn \| head -30` shows `benchmarks/WinForward.E2E.Analysis/verification/synthetic-tree.tar.gz` at 527 622 B as the only archive inside `benchmarks/`; `FROZEN.md:16-29` is its justification and `oracle-diff.py:109` its consumer. |

**The near-miss worth naming:** the CLI snapshot fixtures live at
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/cli-snapshots/` (181 tracked files:
`before/` 28 cases ×3 + `index.json` = 85 entries; `after/` 31 ×3 + `index.json` = 94) and
`tests/WinForward.E2E.Tests/RepoPaths.cs:49-67` resolves them by walking
`.trellis/tasks/archive/*/10-07-e2e-harness-refactor/research/cli-snapshots`, throwing
`InvalidOperationException` at `:66` if neither the live nor the archived path exists. This is **a gate
fixture living inside an archived task directory** — the only such arrangement in the repository. It is
tracked and therefore safe from archive pruning *by git*, but any task-archive cleanup, `.trellis/`
relocation, or Trellis upgrade that moves archived tasks breaks `CliSnapshotTests` with a message that
points at `.trellis/tasks/archive`. Move the two trees under
`tests/WinForward.E2E.Tests/Fixtures/cli-snapshots/` (they are 676 KB) and delete `RepoPaths.FindCliSnapshots`.

---

## 6. "Safe to delete"

| # | Target | Proof | Risk of deleting |
|---|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/__pycache__/` (7 `.pyc`, 232 KB) | Untracked (`git ls-files` → 0), ignored (`git check-ignore -v` → `.gitignore:28`), regenerated by any `python3` import. | **None.** |
| 2 | `benchmarks/WinForward.E2E.Analysis/verification/__pycache__/` (1 `.pyc`, 31 KB) | Untracked, ignored (`.gitignore:28`), and the pyc's recorded source mtime/size equal the current `check-boundary-trees.py`. | **None.** |
| 3 | `BenchmarkDotNet.Artifacts/` at the repository root (13 MB, 170 files) | Untracked (`git ls-files` → 0), ignored (`.gitignore:8`), wholly regenerated by `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*'` (`benchmarks/README.md:17-30`). | **Low, not zero.** Its only pointer is `benchmarks/results/2026-08-29-windows-real-machine/README.md:59` ("详见根目录 BenchmarkDotNet.Artifacts/"), and nobody has proved the raw logs are fully summarised in that README. Delete only with the owner's assent, or archive the 20 `.md` summaries first. |
| 4 | `benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json` + `RepoPaths.WindowsPlansDirectory` | Only consumer is the directory glob `tests/WinForward.E2E.Tests/PlanFileTests.cs:88-92` and the count at `:27`; `rg -F 'full-shape-plan' .` → 0 hits. Deleting it is a 9-leaf override's worth of data (`plans/full-plan.json` + nine `seconds`). | **Medium — conditional.** Only after the override seam exists; and `PlanFileTests.cs:27`'s `Assert.Equal(12, plans.Length)`, `:88-92` and `selftest.sh:17` must change in the same commit. |
| 5 | `benchmarks/WinForward.E2E/scripts/plans-short/` (5 files, 4.6 KB) | No campaign path uses them (`deploy-campaign.sh:52-54`; `orchestrator.ps1:80-109,301,383`). | **Medium.** `PlanFileTests.cs:79` loads `plans-short/base-plan.json` **by name** and `:90` globs the directory; `README.md:59,142` and `selftest.sh:17` name the directory. Keep `base-plan.json`; the other four are glob-only. |
| 6 | `verification/golden/{cp-random-vectors,py-number-vectors,py-json-vectors}.json` + `synthetic/make_cp_vectors.py` + the three `Analyzer*GoldenTests` classes | Deleted *by design* under the 2026-10-09 ruling (`prd.md:96-103`): `System.Random(seed)` replaces the MT19937 port and ordinary .NET formatting replaces `%.*f`/`%.*g`/`repr`. | **High if done alone.** `FROZEN.md:43-63` and `.trellis/spec/backend/measurement-tooling.md:125` both assert these are frozen; both must be updated in the same change. This is C1's scope, not C4's. |
| 7 | `scripts/normalize-pattern-hits.py` and `scripts/compare-records.py` | `compare-records.py` (45 KB) is named by **no README at all** — `rg -n -F 'compare-records' benchmarks/*/README.md benchmarks/README.md` → 0 hits — only by `.trellis/spec/backend/measurement-tooling.md:72,80` and `measurement-judgement.md:96`. `normalize-pattern-hits.py` is named only by `WinForward.E2E/README.md:67` and feeds only `compare-records.py`. | **Medium.** They are the manual run-to-run comparison the judgement spec describes; deleting them without retiring `measurement-judgement.md`'s procedure would leave a spec describing a missing tool. Decide as a pair, in C3. |
| 8 | `scripts/cli-snapshots.py` | Effectively dead as a *runner*: nothing shells out to it, and `CliSnapshotTests` replays the recorded trees in-process. Referenced only by `WinForward.E2E/README.md:72` and itself. | **Medium.** It is the documented way to *record new* snapshot cases; deleting it makes the frozen trees unmaintainable. If kept, fix the count in `README.md:72` (§7). |

**Explicitly NOT safe to delete** (they look like dead weight and are not): `synthetic-tree.tar.gz`,
`golden/py-tables.md`, `golden/py-verdict.json`, `plots-SKIPPED.md`, `row-profiles.json`,
`check-fixture-drift.py` (fix, don't delete), `synthetic/make_tree.py`, `freeze-tree.sh`,
`check-boundary-trees.py`, `benchmarks/results/**`.

---

## 7. Stale document claims

| # | Claim | Claim `file:line` | Contradicting source `file:line` |
|---|---|---|---|
| 1 | "record the **28** CLI commands" | `benchmarks/WinForward.E2E/README.md:72` | `benchmarks/WinForward.E2E/scripts/cli-snapshots.py:42-76` — the `CASES` list holds **31** entries (verified by AST parse: `len(CASES) == 31`); the `before/` tree holds 28 (`index.json` length 28) and the `after/` tree 31. `.trellis/spec/backend/measurement-tooling.md:96` states the correct pair ("**31** commands … while the frozen `before/` tree … holds 28"). |
| 2 | "The harness's own **three gates run the same way**: `check-readme-contract.py` … `effective-lines.py` … and `CliSnapshotTests`" — implying all three are runnable gates | `benchmarks/WinForward.E2E/README.md:694-697` | `.trellis/spec/backend/measurement-tooling.md:80-81` — "`compare-records.py` and `effective-lines.py` are **not run by any test or CI workflow**. Run them by hand". And `check-readme-contract.py` cannot run at all: `scripts/check-readme-contract.py:47` points at `.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json`, which does not exist, so the checker exits 2 before its checks (`.trellis/spec/backend/measurement-tooling.md:36-45` records exactly this). Only `CliSnapshotTests` actually runs. |
| 3 | "'the `-c Release` zero-warning build and a green `-c Release` test run (`AGENTS.md`; `.github/workflows/analyzer-gate.yml`)" — citing the workflow as their home | `.trellis/spec/backend/quality-guidelines.md:125` | `.github/workflows/analyzer-gate.yml:14-67` contains restore → `dotnet format` → restore → `jb inspectcode` → assert → upload; **no `dotnet build` and no `dotnet test` step**. `.github/workflows/release-build.yml:14-71` publishes only `src/WinForward.Cli`. |
| 4 | "archived under `.trellis/tasks/08-17-performance-hotspots/research/`" | `benchmarks/README.md:13` | The live path does not exist (`ls` → no such file); the archive is `.trellis/tasks/archive/2026-08/08-17-performance-hotspots/`. Every other archived-task reference in that README was updated when the task was archived; this one was not. |
| 5 | "`scripts/contract-inventory.py` \| publish `research/contract-inventory.json` and `research/contract-rename.{json,md}`" | `benchmarks/WinForward.E2E/README.md:65` | `ls benchmarks/WinForward.E2E/research` → no such directory; the files exist only at `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/{contract-inventory.json,contract-rename.json,contract-rename.md}`. `contract-inventory.py:306,312-313` makes `--out`/`--out-json`/`--out-md` **required**, so the script has no default `research/` at all; the README's path is aspirational prose from the pre-archive era. |
| 6 | "…or when the spelling is an `old_path` of `research/contract-rename.json`" | `benchmarks/WinForward.E2E/README.md:393` | The checker reads `RENAME_TABLE = REPO_DIR / ".trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json"` (`check-readme-contract.py:47`) — never `research/`. The real file is in the archived task. |
| 7 | "`scripts/check-fairness.py` and `verification/check-boundary-trees.py` assert the disclosures this document describes" — the first path written as if relative to the analysis project | `benchmarks/WinForward.E2E.Analysis/README.md:498` | `ls benchmarks/WinForward.E2E.Analysis/scripts/` → only `analyze.sh`. The query script is `benchmarks/WinForward.E2E/scripts/check-fairness.py` (`benchmarks/WinForward.E2E/README.md:69`; `verification/FROZEN.md:25`; `scripts/check-fairness.py` itself). Contrast `:20` of the same README, which writes the path in full. |
| 8 | "`bash freeze-tree.sh` does exactly that and prints the new hash" / "record the new hashes in §1" as a live procedure | `benchmarks/WinForward.E2E.Analysis/verification/FROZEN.md:29`, `:114-127` | `FROZEN.md:35,92,137,148,172` cite `.trellis/tasks/10-07-e2e-harness-refactor/research/…`, which does not exist (archived). §3's first trigger and §2's recorded evidence both dead-end. §2 step 4 (`:86`) additionally invokes `check-fixture-drift.py`, which exits on `FileNotFoundError` at `check-fixture-drift.py:40,108`. |
| 9 | "`verification/FROZEN.md` … the **reference is retired**: `analyze.py` was deleted once all five batches passed (E4-c/D6.6)" but `E4-c`/`D6.6` are defined nowhere reachable | `FROZEN.md:8` | The definitions are in `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md` (D6.6 is not a heading; `D20.`–`D23.` are at `:797-969`). `rg -n --heading -w 'D6\.6' .trellis/spec/` → 0 hits. Provenance ID with no resolvable referent outside the archive. |
| 10 | The runbook "结果拉回来: `scripts/publish-campaign.sh` 打包、下载、解压到 raw/，再跑分析" | `benchmarks/WinForward.E2E/AGENTS.local.md:163` | `benchmarks/WinForward.E2E/scripts/publish-campaign.sh:8` sets `results="$repo/benchmarks/results/2026-10-06-e2e-competitors"` (directory absent) and `:36` does `cd "$results/analysis"`; the script cannot reach `:37`. The analyzer moved to `benchmarks/WinForward.E2E.Analysis` in `b3b4aa4` (`git log --diff-filter=R -M --name-status` shows the README rename). |
| 11 | `README.md:74-84` lists the machine-specific files as only `AGENTS.local.md`, `scripts/wf.sh`, `scripts/deploy-campaign.sh`, `scripts/start-targets.sh`, `scripts/publish-campaign.sh`, and says "Nothing in this document depends on those files" | `benchmarks/WinForward.E2E/README.md:74-84` | `benchmarks/WinForward.E2E/scripts/orchestrator.ps1:103,365` and `deploy-campaign.sh:49` use `bench.ppx`, which exists **nowhere** in the repository (`rg -n --heading -F 'bench.ppx' .` → those two orchestrator lines only). The Proxifier row therefore does depend on a file that is neither listed nor shipped. |
| 12 | "`scripts/plans-windows/` \| `full-plan`'s load shape — `LOSS 120s × 500/s`, `LATLOAD 500 rps`, the `PERSIST` idle window, `DNS 200 rps` — at compressed arm durations" | `benchmarks/WinForward.E2E/README.md:60` | Accurate in substance, but it describes the file without ever naming it, and `rg -F 'full-shape-plan' .` → 0 hits, so a reader cannot get from the table to the file. Compare `README.md:58`, which lists `full-plan`, `udp-plan`, `dns-plan`, `dual-plan`, `base-plan`, `selftest-plan` by stem. Also note `LOSS` is listed as "120s" and is the **only** arm whose duration is *not* compressed in that file (verified: `/arms[5]/seconds` 120 → 120). |
| 13 | "`scripts/configs/` \| the product configurations each row is measured with" | `benchmarks/WinForward.E2E/README.md:61` | No repository file names any of the six files (`rg -F '<name>' .` → exit 1 for each); the only mapper is the gitignored `deploy-campaign.sh:43-49`, and `wf-fdd-opt.json` is a byte-identical duplicate of `wf-aot-opt.json`, so "the configurations" is 6 files for 7 rows and the repo path → row mapping is recorded nowhere tracked. |
| 14 | `README.md:72` and the Layout table present `scripts/` as complete | `benchmarks/WinForward.E2E/README.md:58-72` | The table lists 15 entries and omits `scripts/compare-records.py` (45 678 bytes — the largest script in the directory), which `.trellis/spec/backend/measurement-tooling.md:72` and `measurement-judgement.md:96` describe as the run-to-run comparator. `rg -n -F 'compare-records' benchmarks/README.md benchmarks/WinForward.E2E/README.md benchmarks/WinForward.E2E.Analysis/README.md` → 0 hits in all three. |
| 15 | Plan-schema table: `` `payloadBytes` `` \| latency, loss, persistent, both BASE phases \| 120 / 200 / 120 / 120 and 200 \| **0 or more** | `benchmarks/WinForward.E2E/README.md:227` | The key's domain is capped at the frame codec's own limit: `Client/PlanFile.cs:81` declares `new("payloadBytes", 0, (int)FrameCodec.MaxPayloadLength, …)`, `Wire/FrameCodec.cs:45` sets `MaxPayloadLength = 4u * 1024u * 1024u`, and `PlanFile.cs:422-427` refuses anything above `key.Maximum` with `'payloadBytes' is <v>, which is outside 0..4194304`. The same README states the ceiling correctly elsewhere ("the 4 MiB ceiling \| `Wire/FrameCodec.cs`", `:509`). The code even carries the rationale at `PlanFile.cs:79-80`. |
| 16 | Plan-schema paragraph: "Every numeric key is an integer, and `0` means 'not declared' … The one exception to '0 = not declared' is `dnsPort`" | `benchmarks/WinForward.E2E/README.md:241-245` | Two exceptions the paragraph does not carry. (a) `seconds` is not an integer key: `ArmSpec.cs:28` declares `double Seconds`, `PlanFile.cs:323-331` reads it through `TryReadNumber` (`:497-503`, a plain `TryGetDouble`) and rejects only `<= 0`, so a fractional `seconds` loads while a fractional `window` does not (`TryReadInt`, `:453-495`, with `IntegerTolerance` at `:46`). (b) `seconds = 0` is a **load error**, not "not declared": `PlanFile.cs:325-329` emits `'seconds' is 0, which is not a positive number`, and the table's own `seconds` row already says "greater than 0" (`:225`). |

**One plan-schema check that came back clean:** the `tcpPercent` / `cnameEvery` / `dnsPort` row's accepted values ("0..100 / 0 or more / 0..65535", `:237`) match `PlanFile.cs:84,85,89` exactly, and `seconds`' "greater than 0" (`:225`) matches `PlanFile.cs:325`.

---

## 8. Proposed end state for `benchmarks/`

At the level of the E2E projects. **Bold** = change from today.

```
benchmarks/
├── README.md
│     Index for the *whole* benchmarks tree: the two BenchmarkDotNet modes as today, plus
│     **a short "The three E2E projects" section** pointing at each project's own README, and
│     **a one-line index of the 20 directories under results/**. Today this file names no E2E
│     project at all (`rg -n -i 'e2e' benchmarks/README.md` → 1 hit, line 433, and it is about
│     effective-lines.py).
├── WinForward.Benchmarks/            # unchanged: BDN Perf/ + Stability/ hosts
├── WinForward.E2E/                   # the harness binary
│   ├── WinForward.E2E.csproj         # unchanged
│   ├── Cli/  Client/  Target/  Wire/ # unchanged
│   ├── README.md
│   │     Keeps the "Which keys are contract" section exactly where it is (**a gate reads it**,
│   │     `check-readme-contract.py:51,158`), fixes the 28→31 count, adds the configs table and
│   │     the compare-records.py row, and adds a "how a campaign is launched" section so the
│   │     machine-independent half of AGENTS.local.md is reviewable.
│   ├── **docs/runbook.md**           # NEW, tracked, sanitised: the machine-independent half of
│   │                                 # AGENTS.local.md (paths, ports, product set, the campaign
│   │                                 # steps, the judging criteria). Addresses/credentials stay
│   │                                 # in the (still ignored) AGENTS.local.md.
│   ├── AGENTS.local.md               # (ignored) shrinks to host addresses, credentials and
│   │                                 # installed-product state; points at docs/runbook.md.
│   └── scripts/
│       ├── plans/                    # the only irreducible plan set:
│       │                             #   base, dns, dual, full, udp, selftest
│       ├── **plans-variants/**       # NEW, only if the loader grows an override seam:
│       │                             #   short.json + windows.json, two small override tables
│       │                             #   replacing plans-short/ (5 files) + plans-windows/ (1).
│       │                             #   If no seam is built, keep plans-short/ (5) and
│       │                             #   plans-windows/ (1) as they are and name full-shape-plan
│       │                             #   in README.md:60.
│       ├── configs/                  # 6 files; **wf-fdd-opt.json becomes a deploy-time second
│       │                             # copy of wf-aot-opt.json** (they are byte-identical), and the
│       │                             # repo-path → product-row → deployed-path mapping moves into
│       │                             # README.md.
│       ├── orchestrator.ps1  publish.sh  selftest.sh
│       ├── jsonl_paths.py  contract-inventory.py  check-readme-contract.py
│       ├── check-fairness.py  oracle-diff.py  effective-lines.py  cli-snapshots.py
│       ├── **compare-records.py  normalize-pattern-hits.py**
│       │                             # keep both, or retire both together with the
│       │                             # judgement-spec procedure they implement; either way the
│       │                             # README table must list what survives.
│       └── (ignored: wf.sh, deploy-campaign.sh, start-targets.sh, publish-campaign.sh)
│                                     # publish-campaign.sh repointed off the deleted
│                                     # results/2026-10-06-e2e-competitors/analysis path.
├── WinForward.E2E.Contracts/         # unchanged
├── WinForward.E2E.Analysis/          # the analyzer
│   ├── WinForward.E2E.Analysis.csproj  Cli/ Tables/ Verdict/ Stats/ Loading/ Model/ …
│   ├── README.md                     # keeps its `analyze.sh` paths; fixes `scripts/check-fairness.py`
│   │                                 # at :498 to the full harness-relative path
│   ├── scripts/analyze.sh            # unchanged (the entry point; a build+exec wrapper)
│   └── verification/
│       ├── FROZEN.md                 # keeps §1/§1.1 hashes; **five live-task paths rewritten to the
│       │                             # archive**; §1.1 updated if C1 removes the vector tables
│       ├── synthetic-tree.tar.gz  plots-SKIPPED.md  row-profiles.json
│       ├── golden/{py-tables.md,py-verdict.json}
│       │                             # the two files the oracle diffs against
│       ├── **golden/**{cp-random-vectors,py-number-vectors,py-json-vectors}.json
│       │                             # DELETED WITH C1 (System.Random + .NET formatting)
│       ├── synthetic/make_tree.py    # keep
│       ├── synthetic/make_cp_vectors.py  # deleted with C1
│       ├── check-boundary-trees.py   # keep
│       ├── check-fixture-drift.py    # keep, **path fixed** (archive-aware, mirroring
│       │                             # tests/…/RepoPaths.cs:49-67)
│       └── freeze-tree.sh            # keep; note in FROZEN.md that it writes into the repo
└── results/                          # unchanged, all 282 files tracked; add the index to README.md
```

Adjacent, outside `benchmarks/`:

```
tests/WinForward.E2E.Tests/
├── Fixtures/plans/                   # unchanged (19 files)
└── **Fixtures/cli-snapshots/{before,after}/**   # MOVED here from
                                      # .trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/
                                      # (181 files, 676 KB); RepoPaths.FindCliSnapshots
                                      # (RepoPaths.cs:49-67) replaced by a plain path constant, and
                                      # the InvalidOperationException at :66 goes with it.
```

---

## 9. Open questions

1. **Which of the three self-suppressions was measured with the analyzers on?** `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj:4` turns the four analyzer packages off for that project only; every other test project gets them. `.editorconfig:217-273` publishes hit counts (MA0040 30, xUnit1030 94, S1144 1, …) that can only have been measured with analyzers enabled — but not necessarily *in E2E.Tests*. Settling whether the property is a deliberate opt-out or a porting leftover requires one Release build with the line removed (forbidden here).
2. **Is an override/merge mechanism in `PlanFile` in scope at all?** The dedup case is strong (11 of 12 plan files derivable; `full-shape-plan.json` differs in 9 leaves, all `seconds`), but it is *new loader behaviour* with new validation and new tests, not a data cleanup. If the answer is "no new loader behaviour", the correct C4 action is only to name `full-shape-plan.json` in `README.md:60` and to document the three plan sets' relationship — not to delete anything.
3. **What does `check-readme-contract.py`'s gate actually protect after C1?** The checker resolves README key paths to `ArmKeys` constants and requires a write site (`check-readme-contract.py:53-60`). The C1 RNG/format work does not touch `ArmKeys`, so the checker should stay green once `:47` is fixed — but that should be re-run, not assumed. Related: `.trellis/spec/backend/measurement-tooling.md:28-34` declares `LEGACY_TOKENS = {"metrics/clientSendLoss"}` and `DOCUMENTED_NON_KEYS = {…}` at `check-readme-contract.py:123-131`; those declarations are the "edit, not silent pass" mechanism and must be re-justified after any README rewrite.
4. **`compare-records.py` and `normalize-pattern-hits.py`: retire or keep?** They are the run-to-run comparison procedure `.trellis/spec/backend/measurement-judgement.md` describes but no README lists. If they are retired, does the judgement spec lose its only tool? If they are kept, does the README table gain two rows? Neither answer can be reached from the code alone.
5. **Does `synthetic-tree.tar.gz` still reproduce byte-for-byte?** `freeze-tree.sh:15,26` rewrites the in-repo tarball, so a read-only audit cannot run it. `FROZEN.md:31-34` claims a re-freeze reproduced `723b7378…` byte for byte in E4-c; the tar listing (uniform `0/0` owners, fixed mtime, GNU format, sorted names) is consistent with that, but the claim is unverified today. Same for `make_cp_vectors.py`'s three hashes (`FROZEN.md:51-53`).
6. **Was `publish-campaign.sh` the only local glue broken by the analyzer move?** `wf.sh` and `start-targets.sh` were read and are consistent with `AGENTS.local.md` (ports 40010/40011/53/40053/40054 at `start-targets.sh:28-33` match `AGENTS.local.md:41-45`). `deploy-campaign.sh` reads correctly but stages `/tmp/wf-bench/bench.ppx` from outside the repo. `/tmp/wf-bench/deploy/**` — the staging tree both scripts consume — has **no generator anywhere in the repository**, a conclusion the archived research already reached (`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E5b-windows.md:1036`: "仓库里没有任何脚本生成它"). How that tree is populated is undetermined.
7. **Should `row-profiles`-style duplicate-with-hardcoded-source files get a machine check?** `verification/row-profiles.json` and `Model/RowProfiles.cs:55-214` are the same table twice, held together only by `check-fairness.py` — which nothing runs. If `check-fairness.py` is not wired into a gate, is the duplicate still worth its 118 lines? (Contrast `verification/golden/*`, where the duplicate is the *point*.)
8. **The `.editorconfig` globs vs pragmas question, as a policy:** the repo has both mechanisms for S1244, and `quality-guidelines.md:126` states a preference. Should C4 add a one-line rule ("new suppressions are inline pragmas unless the same rule fires in ≥3 sites of one file") so the next audit does not have to re-litigate it?

---

## 10. Could not determine

- Whether `dotnet build WinForward.slnx -c Release` / `dotnet test WinForward.slnx -c Release` are green —
  forbidden to run. Every "the gate is red" claim above is either an **executed** Python gate
  (`check-readme-contract.py` exit 2; `effective-lines.py` exit 0 over the E2E tree, exit 1 over
  `WinForward.Benchmarks`) or a path/existence proof (`check-fixture-drift.py:40`, `publish-campaign.sh:8,36`).
- Whether `check-fairness.py`, `check-boundary-trees.py` and `oracle-diff.py` currently pass — they need a
  Release build and generated trees under `/tmp`. `effective-lines.py` and `check-readme-contract.py` are
  the only two of the six that run from a plain checkout.
- Whether removing `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj:4` is a small or a large edit — needs a Release build (open question 1).
- Whether `freeze-tree.sh` and `make_cp_vectors.py` still reproduce their recorded hashes — both rewrite repository files.
- The provenance of `/tmp/wf-bench/deploy/**` and the contents of the ignored `wf.sh` — local filesystem facts, not repository facts.

---

## Appendix — re-running the load-bearing claims

```bash
# nothing generated is tracked, and the E2E trees are clean
git ls-files | rg '(^|/)(bin|obj)/' ; git ls-files BenchmarkDotNet.Artifacts
git ls-files '*.pyc' ; git status --porcelain benchmarks/ tests/WinForward.E2E.Tests/

# the one analyzer-suppression asymmetry
python3 - <<'PY'
import json
for p in ('tests/WinForward.Core.Tests','tests/WinForward.E2E.Tests'):
    d=json.load(open(p+'/obj/project.assets.json'))
    print(p, len([l for l in d['libraries'] if any(k in l for k in
        ('Meziantou','Roslynator','SonarAnalyzer','VisualStudio.Threading'))]))
PY

# the plan duplication numbers in §2.1 (9 of 56 leaves differ, all 'seconds')
diff -u benchmarks/WinForward.E2E/scripts/plans/full-plan.json \
        benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json | rg '^[-+] ' | wc -l

# the two dead gate paths
ls .trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json   # ENOENT
ls .trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/contract-rename.json

# the CLI case count (31 in the collector, 28 in the before tree, 31 in after)
python3 -c "import ast;t=ast.parse(open('benchmarks/WinForward.E2E/scripts/cli-snapshots.py').read());\
print([len(n.value.elts) for n in t.body if getattr(getattr(n,'targets',[None])[0],'id',None)=='CASES'])"
python3 -c "import json;print([len(json.load(open('.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/cli-snapshots/%s/index.json'%t))) for t in ('before','after')])"

# the README section a gate reads, and its bounds
rg -n '^## ' benchmarks/WinForward.E2E/README.md | sed -n '1,12p'
rg -n 'SECTION_HEADING =|rest.find' benchmarks/WinForward.E2E/scripts/check-readme-contract.py

# where the decision IDs are defined vs where a live spec cites them
rg -n '^### D(18|19|20)\.' .trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md
rg -n -w 'D14\.7|D19\.2|D19\.3' .trellis/spec/
```
