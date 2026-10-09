# E2E scripts audit — per-script disposition and reorganisation proposal

Read-only investigation for task `10-09-e2e-csharp-native-cleanup`. Every claim below carries a
`file:line`. Nothing was modified outside this `research/` directory; no gate was run; the only
commands executed were `rg`, `git`, `bash -n`, `python3 -m py_compile` and two of the project's own
read-only Python checkers (`check-readme-contract.py`, `check-fixture-drift.py` — their exit codes
are cited as proof below).

Counting: **22 scripts** in scope (12 tracked under `benchmarks/WinForward.E2E/scripts/`, 4
gitignored-but-present there, 6 under `benchmarks/WinForward.E2E.Analysis/`), plus **27 tracked
script-adjacent assets** (18 plan/config JSON under `scripts/`, 9 under
`WinForward.E2E.Analysis/verification/`) and **8 untracked `__pycache__` files**.

---

## 0. Two facts that frame everything

**Nothing runs any of these scripts automatically.** `.github/workflows/` holds exactly two
workflows — `analyzer-gate.yml` (`dotnet format` + `jb inspectcode`) and `release-build.yml` (the
product CLI's AOT/fdd publishes) — and `rg -n "E2E|e2e|benchmark" .github/workflows/` exits 1
(no match). No test invokes a script either: `rg -n "python3|python\b" tests/` has no hit, and
`.trellis/spec/backend/measurement-tooling.md:65` states the rule plainly — "Each script is run by
hand unless a test says otherwise" — while `:80` names the two it believes nobody runs
(`compare-records.py`, `effective-lines.py`). So **every caller in the tables below is a document,
another script, or a test that reads an artifact the script produced.** "Live" therefore means
"reachable and still correct for a human who follows the documentation", not "invoked by CI".

**The repository has no root-level `tools/` or `scripts/` directory.** `ls -d */` at the root yields
`analyzers/ benchmarks/ examples/ src/ tests/`. Any "move it to a root-level tools/" proposal below
therefore means *creating* that directory; the alternative homes that do exist are `analyzers/` (a
Roslyn analyzer project) and `tests/WinForward.Analyzers.Tests`.

---

## 1. Per-script disposition table

### 1.1 `benchmarks/WinForward.E2E/scripts/` — tracked

| Script | What it does | Callers (`file:line`) | Status | Scope | Proposal |
|---|---|---|---|---|---|
| `check-fairness.py` (566 L) | Asserts the four fairness guards (`#11` `undecodable` confinement, `#17` `not carried (UDP bypassed)`, `#18` port-53 carriage labels, `#19` user-mode CPU scope) against the **C#** analysis's `tables.md`, reading its rules from `verification/row-profiles.json`; by default it regenerates the frozen tree with `make_tree.py` and runs the built analyzer first | `benchmarks/WinForward.E2E/README.md:69`; `benchmarks/WinForward.E2E.Analysis/README.md:498`; `verification/FROZEN.md:25,149,156`; `verification/row-profiles.json:4`; `.trellis/spec/backend/measurement-tooling.md:129`; own `--self-check` at `check-fairness.py:435,519,550-557` | live (manual) | E2E-**analysis** specific — it drives `WinForward.E2E.Analysis/bin/...` (`check-fairness.py:421-431`) and never touches the harness | **Move** to `benchmarks/WinForward.E2E.Analysis/verification/` |
| `check-readme-contract.py` (306 L) | Gate: every backticked key path in `README.md`'s "Which keys are contract" section must resolve to an `ArmKeys.*` `const string` that a write site references, and must not be an `old_path` of the rename table | `benchmarks/WinForward.E2E/README.md:70,390,431,694`; `benchmarks/WinForward.E2E.Analysis/README.md:20`; `measurement-tooling.md:9,33-52` | **broken** (exit 2 — see §2.1) | E2E harness (its README + its `Contracts` project) | **Keep**, fix the one path; see §2.1 for the two fix options |
| `cli-snapshots.py` (127 L) | Records the CLI's whole user-visible surface (31 cases: both helps, the three role forms, every rejection) as `<nn>-<name>.{exit,stdout,stderr}` plus `index.json` from a *published* binary | `benchmarks/WinForward.E2E/README.md:72`; its output is read by `tests/WinForward.E2E.Tests/CliSnapshotTests.cs:44` via `RepoPaths.CliSnapshotsDirectory` (`tests/WinForward.E2E.Tests/RepoPaths.cs:49-67`, archive-aware) | live, rarely run (only when CLI text changes on purpose — it happened twice, `CliSnapshotTests.cs:20-29`) | E2E harness | **Keep as-is.** Verified not drifted: its `CASES` list (`cli-snapshots.py:42-76`, 31 entries) matches `research/cli-snapshots/after/index.json` exactly in both names and argv (checked mechanically) |
| `compare-records.py` (984 L) | Run-to-run comparison of two artifact trees in D15's five classes (structural / conditional / identity / contract / reading) with a per-key jitter band and a rename-table check | `contract-inventory.py:93,100,284`; `jsonl_paths.py:11`; `normalize-pattern-hits.py:15,25,47`; `.trellis/spec/backend/measurement-judgement.md:20,44,73,90,96`; doc `benchmarks/WinForward.E2E/README.md:68` | live (manual gate, declared as such at `measurement-judgement.md:91-108`) — but it requires `--normalize record-normalize.json`, which lives in the *archive* (`measurement-judgement.md:101-103`) | E2E campaign results | **Keep.** Its fixtures being archived is already documented; the only improvement is a default that points at the archive (see §4.3) |
| `contract-inventory.py` (321 L) | `inventory` writes every canonical path of one run; `rename` writes the full `{kind, old_path, new_path, reason}` migration table by diffing a frozen pre-migration baseline against a fresh run | `benchmarks/WinForward.E2E/README.md:65` (its only tracked reference); its *outputs* are consumed by `check-readme-contract.py:47` and `check-fixture-drift.py:65-66` | **spent** (still runnable, nothing consumes it — see §3.1) | E2E migration (D7/D14.6/D15) | **Delete** the script; **move** its two output tables to a live home |
| `effective-lines.py` (230 L) | Reports `.cs` files above a 400-**effective**-line limit, counting the way the compiler does (`blank_comments` at `effective-lines.py:131-173` strips comments but not string contents) | `.trellis/spec/backend/directory-structure.md:103`; `.trellis/spec/backend/measurement-tooling.md:84`; `benchmarks/README.md:433` (run against `benchmarks/WinForward.Benchmarks` — a **different project**); `benchmarks/WinForward.E2E/README.md:71,695` | live | **Solution-wide**, not E2E — see §4.1 | **Move** to a new root-level `tools/` |
| `jsonl_paths.py` (194 L) | The one canonical flattener: `"/".join(member names)`, arrays contribute their own path once with an arity, keys keep their dots | `contract-inventory.py:33`; `compare-records.py:89`; `normalize-pattern-hits.py:38`; `check-fixture-drift.py:44`; doc `benchmarks/WinForward.E2E/README.md:66`; mirrored in C# at `tests/WinForward.E2E.Tests/JsonPaths.cs:13` | live while its consumers live | **E2E-only** — verified: all four importers are E2E/analysis tools; the C# mirror `JsonPaths.cs` is in `WinForward.E2E.Tests` | **Keep.** The README's claim ("shared by the inventory, the comparator and the rename table") is true and the "is it solution-scoped?" suspicion is **false** |
| `normalize-pattern-hits.py` (106 L) | Reports `readingPathPatterns` / `volatileArityPatterns` in a normalize config that match no observed path in a run tree (D16.2's dead-pattern check) | `benchmarks/WinForward.E2E/README.md:67`; `.trellis/spec/backend/measurement-judgement.md:104` | **spent** (still runnable, question closed — see §3.2) | E2E migration | **Delete** |
| `oracle-diff.py` (1274 L) | The two-implementation differ: slices both `tables.md` and `verdict.json`, owns each slice by batch (`BATCH_SECTIONS`, `oracle-diff.py:129-181`), and compares in `--mode semantic` (default) or `--mode byte` with a printed-precision tolerance and three-state exit codes | `benchmarks/WinForward.E2E/README.md:68`; `verification/FROZEN.md:5,46,174`; `measurement-tooling.md:127`; cited in code docs at `benchmarks/WinForward.E2E.Analysis/Metrics/MetricCatalogue.cs:13` and `Json/VerbatimJson.cs:21` | live — it is the **only** tie between the C# output and the frozen reference; `check-fairness.py` and `check-boundary-trees.py` assert disclosures, not equality | E2E-**analysis** specific (all its inputs are `WinForward.E2E.Analysis/verification/**`, `oracle-diff.py:106-120`) | **Move** to the analysis project; `measurement-tooling.md:127` already calls it "the harness's `scripts/`, not this project's" |
| `orchestrator.ps1` (471 L) | The campaign driver on the machine under test: seven product rows, a per-pass randomised order, `control-pre`/`control-post` blocks, a per-row dual phase, `proxy-truth.json` from the sing-box log, and `environment.json` | `benchmarks/WinForward.E2E/README.md:62,262`; `benchmarks/WinForward.E2E/AGENTS.local.md:8`; staged by `deploy-campaign.sh:57`; `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design.md:531` | live, but carries **defect T3** (§2.5) | E2E, machine-coupled (defaults `192.168.100.4` at `:26`, `C:\wfbench\*` at `:24-41`) | **Keep tracked**; fix T3. Note the split inconsistency argued in §6.2 |
| `publish.sh` (28 L) | Builds Release and publishes three framework-dependent trees (linux / win / win-direct) under `$WF_PUB` | `benchmarks/WinForward.E2E/README.md:63,92,127`; `AGENTS.local.md:8`; `benchmarks/WinForward.E2E.Contracts/WinForward.E2E.Contracts.csproj:2` | live | E2E | **Keep as-is** |
| `selftest.sh` (78 L) | Publishes the Linux build if missing, starts one target on `127.0.0.1`, runs the client against a plan, then prints every `result` record arm by arm | `benchmarks/WinForward.E2E/README.md:64,127-131`; `AGENTS.local.md:9` | live | E2E | **Keep**, but see §4.4: its embedded Python heredoc (`selftest.sh:52-78`) is the one non-gate Python dependency in a tracked harness script |

### 1.2 `benchmarks/WinForward.E2E/scripts/` — gitignored but present on disk

All four are named by `benchmarks/WinForward.E2E/.gitignore:4-7` and by
`benchmarks/WinForward.E2E/README.md:79-82`.

| Script | What it does | Callers (`file:line`) | Status | Scope | Proposal |
|---|---|---|---|---|---|
| `wf.sh` (75 L) | Drives the Windows VM through a persistent `evil-winrm-py` tmux session: `run` / `file` / `up` / `down` / `cap` / `key` / `raw` / `idle`, delimited by random markers | `benchmarks/WinForward.E2E/README.md:79`; `AGENTS.local.md:4`; used by `deploy-campaign.sh:7` and `publish-campaign.sh:6` | live (machine glue); carries **T5** (`repl_cmd`'s "last line is a prompt" test at `wf.sh:47-61`) | one particular machine pair — correctly not in the repo | **Leave untracked.** Out of scope for a repository change; recorded here only because the README indexes it |
| `deploy-campaign.sh` (60 L) | Stages the two client images, the product configurations, the plans and the orchestrator onto the VM, verifying each transfer **by remote file size** (`stage()`, `deploy-campaign.sh:14-27`) | `benchmarks/WinForward.E2E/README.md:80,288`; `AGENTS.local.md:8` | live but **drifted** — sources everything from a hand-maintained `/tmp/wf-bench/deploy` (§2.4) | machine glue | **Fix in place** (it is gitignored; the fix is local) |
| `start-targets.sh` (44 L) | Starts the two long-lived target instances (proxied `:40010`/dns `53`, direct `:40011`/dns `40054`) with separate ledgers | `benchmarks/WinForward.E2E/README.md:81,199`; `AGENTS.local.md:7`; `make_tree.py:75` describes it | live; **T1 is fixed** — `:29` and `:33` both carry `--label target:<port>` | machine glue | **Fix in place**: `:14` hardcodes `/tmp/wf-bench/pub/linux/...` while `publish.sh:8` honours `$WF_PUB` |
| `publish-campaign.sh` (40 L) | Zips the VM's results directory, downloads it, extracts into `raw/`, copies the target ledger, runs the analysis | `benchmarks/WinForward.E2E/README.md:82`; `AGENTS.local.md:8` | **broken in three places** (§2.3) | machine glue | **Fix in place** |

### 1.3 `benchmarks/WinForward.E2E.Analysis/`

| Script | What it does | Callers (`file:line`) | Status | Scope | Proposal |
|---|---|---|---|---|---|
| `scripts/analyze.sh` (20 L) | Builds the analyzer (`dotnet build … -v quiet`) then `exec`s the binary with the caller's cwd and arguments untouched | `benchmarks/WinForward.E2E/README.md:48,593,600`; `benchmarks/WinForward.E2E.Analysis/README.md:28,31,34,37,485,491`; `measurement-tooling.md:117-118`; `publish-campaign.sh:37` | live | E2E analysis | **Keep as-is** — the model for what a thin wrapper should be |
| `verification/freeze-tree.sh` (30 L) | Regenerates `/tmp/wf-synth` with `make_tree.py` and rebuilds `synthetic-tree.tar.gz` reproducibly (sorted names, fixed mtime, zeroed ownership) | `verification/FROZEN.md:29,79` | live (the re-freeze procedure) | analysis oracle | **Keep as-is** |
| `verification/check-fixture-drift.py` (141 L) | Guards the synthetic fixture's key set against the current contract, in both directions, from `contract-inventory.json` composed with `contract-rename.json` | `verification/FROZEN.md:23,86,122`; `verification/synthetic/make_tree.py:21`; docstring `check-fixture-drift.py:12` | **broken** (uncaught `FileNotFoundError`, exit 1 — §2.2) | analysis oracle | **Fix the one path now**; see §2.2 for the retire-later argument |
| `verification/check-boundary-trees.py` (481 L) | Runs `make_tree.py` with `--truncated-tcp|dns` / `--zero-denominator` and asserts the C#-only boundary shapes, each guard paired with a negative control | `verification/FROZEN.md:24,150-154`; `benchmarks/WinForward.E2E.Analysis/README.md:499` | live | analysis oracle | **Keep as-is** (paths resolve via `parents[3]` at `:398` — no stale reference) |
| `verification/synthetic/make_tree.py` (1532 L) | Generates the frozen synthetic campaign tree: seven rows, control blocks, dual phases, a deliberate `foreignConnection`, lane-witness and control-drift faults, and two ledgers | `verification/FROZEN.md:22,116,121`; `check-fairness.py:421,430`; `check-boundary-trees.py:399`; `freeze-tree.sh:19` | live; carries **T6** (docstring contradicts the generator — §2.6) | analysis oracle | **Keep**; fix the docstring |
| `verification/synthetic/make_cp_vectors.py` (352 L) | Regenerates the three CPython vector tables (`cp-random-vectors`, `py-number-vectors`, `py-json-vectors`) from CPython itself, importing nothing from the retired `analyze.py` (`:17-19`) | `verification/FROZEN.md:54,62`; its outputs are read by `tests/WinForward.E2E.Tests/AnalyzerJsonGoldenTests.cs:19`, `AnalyzerNumberGoldenTests.cs:21,40,60`, `AnalyzerRandomGoldenTests.cs:22` | live, rarely run (only on a CPython-version change) | analysis oracle | **Keep as-is** — Python is *required* here: the file's whole point is "produced by CPython itself", so a C# rewrite would destroy it |

### 1.4 Script-adjacent assets

| Asset | What it is | Callers (`file:line`) | Status | Proposal |
|---|---|---|---|---|
| `scripts/plans/{full,udp,dns,dual,base,selftest}-plan.json` (6) | the committed campaign plans | `tests/WinForward.E2E.Tests/RepoPaths.cs:11` → `PlanFileTests.cs:89-91`; `selftest.sh:17`; `deploy-campaign.sh:52`; `orchestrator.ps1:80,85,90,95,99,103,109` (via `-PlanRoot`) | live | keep |
| `scripts/plans-short/*.json` (5) | the same arm shapes at short durations | `RepoPaths.cs:13`; `PlanFileTests.cs:79,90`; `selftest.sh:17` | live | keep |
| `scripts/plans-windows/full-shape-plan.json` (1) | `full-plan`'s load shape at compressed durations, for the Windows lightweight validation | `RepoPaths.cs:15-20`; `PlanFileTests.cs:91`; `benchmarks/WinForward.E2E/README.md:60`; used by the E5-b2 rerun with a manual `-PlanRoot C:\wfbench\e2e-win2` (`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/tickets.md:94`) | live | keep — but note **no deploy script ships it** (`deploy-campaign.sh:52` loops only the five `plans/` names) |
| `scripts/configs/{wf-aot-opt,wf-aot-nativeudp,wf-aot-dnsrelay,wf-fdd-opt}.json`, `proxifyre-app-config.json`, `proxybridge.pbprofile` (6) | the product configuration each row is measured with | `benchmarks/WinForward.E2E/README.md:61`; **only** `deploy-campaign.sh:43-48` reads them, and through the `/tmp` staging copy. In tracked content there are **zero** references: `rg` over the repo finds only that gitignored script and the archived evidence `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E5b2-windows-rerun.md:193-194`, which records a manual `cmp` proving the staging copies were still byte-identical | live | keep (they are campaign inputs); fix `deploy-campaign.sh` to read them from here |
| `verification/golden/{py-tables.md,py-verdict.json}` | the retired reference's answers on the frozen tree | `oracle-diff.py:106-110,127`; `FROZEN.md:19-20` | live | keep |
| `verification/golden/{cp-random-vectors,py-number-vectors,py-json-vectors}.json` | CPython's own arithmetic/JSON vectors | the three `Analyzer*GoldenTests.cs` above, via `RepoPaths.AnalyzerGolden` (`RepoPaths.cs:28-29`) | live | keep |
| `verification/synthetic-tree.tar.gz` | the frozen campaign every regression runs on | `oracle-diff.py:109`; `freeze-tree.sh:15,26`; `FROZEN.md:18` | live | keep |
| `verification/plots-SKIPPED.md` | the fixed text the analysis writes to `<out>/plots/SKIPPED.md`, byte-compared | `oracle-diff.py:110`; mirrored in code at `benchmarks/WinForward.E2E.Analysis/Cli/PlotsNotice.cs:9,41` | live | keep |
| `verification/row-profiles.json` | the fairness rules as data | `check-fairness.py:119-120`; `FROZEN.md:25` | live | keep; update its self-reference at `:4` if `check-fairness.py` moves |
| `verification/FROZEN.md`, `verification/plots-SKIPPED.md` | the frozen set's manifest and the fixed notice | docs | live, but two dead links (§2.7) | fix the two links |
| `scripts/__pycache__/*.pyc` (7) and `verification/__pycache__/*.pyc` (1) | CPython bytecode caches | none | **dead** | delete (§5) |

---

## 2. Broken today

### 2.1 `check-readme-contract.py:47` — the rename table's path is stale (exit 2)

```
47:RENAME_TABLE = REPO_DIR / ".trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json"
```

Task `10-07-e2e-harness-refactor` was archived by `aee1955`
(`git log --oneline -1 aee1955` → `chore(task): archive 10-07-e2e-harness-refactor`), so the file now
lives at `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/contract-rename.json`
(verified present, 127 354 B). The old directory does not exist. **Proved by running it:**

```
$ python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py
cannot read …/.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json: [Errno 2] No such file or directory
rc=2
```

`.trellis/spec/backend/measurement-tooling.md:36-44` already documents this exact failure and states
the real numbers a fixed run prints (`111 key(s) checked against 401 declared constant path(s): ok`).

**Fix (two options).**
- *Primary:* move `contract-rename.json` and `contract-inventory.json` out of the archived task into a
  live home — `benchmarks/WinForward.E2E.Analysis/verification/` is the right one, because
  `row-profiles.json` there is the same kind of artifact (frozen rules/data a checker asserts
  against). Then `check-readme-contract.py:47` becomes
  `REPO_DIR / "benchmarks/WinForward.E2E.Analysis/verification/contract-rename.json"` and
  `check-fixture-drift.py:40` becomes the same directory. This removes the whole
  "archived-task-path" failure class permanently.
- *Minimal-diff alternative:* make the lookup archive-aware, copying
  `tests/WinForward.E2E.Tests/RepoPaths.cs:53-67` (`FindCliSnapshots`: try the live path, else walk
  `.trellis/tasks/archive/` for the task name). That is the pattern the C# side already uses. It
  leaves the fixture in the archive, which means
  `.trellis/spec/backend/measurement-judgement.md:101-103` stays correct as written.

Either way, `check-fixture-drift.py:12` and the docstring at `check-readme-contract.py:16` should be
re-worded to match.

### 2.2 `check-fixture-drift.py:38-40` — the same stale path, worse failure mode

```
38:REPO_ROOT = Path(__file__).resolve().parents[3]
39:SCRIPTS = REPO_ROOT / "benchmarks" / "WinForward.E2E" / "scripts"
40:RESEARCH = REPO_ROOT / ".trellis" / "tasks" / "10-07-e2e-harness-refactor" / "research"
```

`RESEARCH` is the default for `--root` (`:108`) and is read at `:65-66` by a bare
`Path.read_text()` with no `try`. **Proved by running it** (with a dummy `--tree`, since `--root` is
resolved first at `:111`):

```
$ python3 …/check-fixture-drift.py --tree …/verification/golden
FileNotFoundError: [Errno 2] No such file or directory: '…/.trellis/tasks/10-07-e2e-harness-refactor/research/contract-inventory.json'
rc=1
```

Note the exit code: **1**, a traceback — not the controlled `2` that this project's own convention
demands for "an input could not be read" (`oracle-diff.py:68-75`). A checker that reports
"infrastructure failure" as "content differs" is the exact defect `oracle-diff.py:74-75` calls out.
`.trellis/spec/backend/measurement-tooling.md:130` already flags it: "it resolves its fixtures from
the same archived task path (`check-fixture-drift.py:40`) and needs the same fix".

**Fix:** as §2.1, plus wrap the two `read_text()` calls (`:65-66`) in the `unusable(...)`/`return 2`
path the file already has for its other inputs, so a missing authority exits 2 rather than 1.

**Also worth deciding now:** this checker's two authorities are a *pre-migration snapshot*
(`contract-inventory.json`) composed with a *migration delta* (`contract-rename.json`). Once
`contract-inventory.py` is deleted (§3.1) neither authority has a generator left, and both were
produced from trees the repository does not own — the archived `baseline/run1` and the `/tmp` scratch
runs (`contract-rename.md:7-8`, `contract-inventory.json`'s `run` field). The guard still catches a
`make_tree.py` edit that invents a key, so it is worth one line to fix — but the durable replacement
is C#-side and already half-built:
`ContractShapeTests` + `DeclaredKeys` (`tests/WinForward.E2E.Tests/ContractShapeTests.cs:14-19`,
`DeclaredKeys.cs:6-9`) assert declared-vs-written paths in-process, and `System.Formats.Tar` could
read the *tracked* `synthetic-tree.tar.gz` and compare its canonical paths to `ArmKeys` with no
Python and no archived table. Register that as a follow-up, not this task's work.

### 2.3 `publish-campaign.sh` — three independent breaks

```
 8:results="$repo/benchmarks/results/2026-10-06-e2e-competitors"
```

That directory does not exist: `ls -d benchmarks/results/*/` lists 20 campaign directories and
`2026-10-06-e2e-competitors` is not among them. Commit `b3b4aa4`
(`feat(analysis): disclose truncated frames and retire the Python analyzer`) deleted the whole
`benchmarks/results/2026-10-06-e2e-competitors/` tree; `git ls-files | rg '2026-10-06-e2e-competitors'`
returns 0 paths and `rg -uuu --files -g 'analyze.py' .` returns nothing.

```
36:cd "$results/analysis"
```

`analysis/` was the deleted Python analyzer's directory. With `set -euo pipefail` (`:4`) this line is
a hard abort before the analysis runs — the "reproduce the campaign is a hard blocker" of D22/T4
(`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md:951`).

```
28:if [ -f /tmp/wf-bench/target-ledger.jsonl ]; then
```

No launcher writes that name any more. `start-targets.sh:15` sets `ledgers=/tmp/wf-bench`, `:26`
removes `ledger-main.jsonl`/`ledger-direct.jsonl`, and `:29`/`:33` write exactly those two.
`target-ledger.jsonl` is only the target's *default* `--ledger` value
(`benchmarks/WinForward.E2E/README.md:195`), which the campaign overrides. So §3 of the script always
takes the `WARNING` branch (`:32`) and the analysis is handed a tree with **no ledger at all** —
silently, since it is a `WARNING` and not a failure. This break is *not* in the ticket list; it is new
in this audit.

**Fix:** derive `results` from an argument/default that exists (e.g. `$WF_RESULTS` or
`benchmarks/results/<date>-<slug>`), drop line 36's `cd` (the analyzer never needed it —
`analyze.sh:9-11` guarantees the working directory is not changed), and copy **both**
`ledger-main.jsonl` and `ledger-direct.jsonl` into `raw/` (or pass them with repeated `--ledger`,
which `measurement-tooling.md:107-108` says the analysis accepts).

### 2.4 `deploy-campaign.sh` — sources a staging directory no script creates

```
 7:wf=/tmp/wf-bench/wf.sh
 8:pub=/tmp/wf-bench/pub
 9:deploy=/tmp/wf-bench/deploy
…
43:stage "$deploy/configs/wf-aot-opt.json"       'C:\wfbench\wf-aot\config.json'
44:stage "$deploy/configs/wf-aot-nativeudp.json" 'C:\wfbench\wf-aot\config-nativeudp.json'
45:stage "$deploy/configs/wf-aot-dnsrelay.json"  'C:\wfbench\wf-aot\config-dnsrelay.json'
46:stage "$deploy/configs/wf-fdd-opt.json"       'C:\wfbench\wf-fdd\config.json'
47:stage "$deploy/configs/proxybridge.pbprofile" 'C:\wfbench\proxybridge\bench.pbprofile'
48:stage "$deploy/configs/proxifyre-app-config.json" 'C:\wfbench\stage\proxifyre-app-config.json'
52:for f in full-plan udp-plan dns-plan dual-plan base-plan; do
53:    stage "$deploy/e2e/$f.json" "C:\\wfbench\\e2e\\$f.json"
57:stage "$deploy/orchestrator.ps1" 'C:\wfbench\orchestrator.ps1'
```

`/tmp/wf-bench/deploy/` exists on this machine but **nothing in the repository produces it** — it is
a manual copy, `ls -la --time-style` dates its contents to 2026-10-06 22:23 while the repo's
`orchestrator.ps1` was last committed by `59c3a09`. The repository already holds every one of these
files at a canonical path: `benchmarks/WinForward.E2E/scripts/configs/*` (6),
`benchmarks/WinForward.E2E/scripts/plans/*.json`, `benchmarks/WinForward.E2E/scripts/orchestrator.ps1`.
At the moment of this audit all seven staged files are byte-identical to their repo counterparts
(checked with `diff -q`), so the drift is latent, not active — and the archived evidence shows it was
caught by hand once already:

```
.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E5b2-windows-rerun.md:193:
$ for f in wf-aot-opt.json … ; do cmp -s /tmp/wf-bench/deploy/configs/$f scripts/configs/$f && echo "same  $f"; done
:194: same  wf-aot-opt.json / … / same  proxifyre-app-config.json
```

**Fix:** set `repo=$(cd "$(dirname "$0")/.." && pwd)` and read `$repo/scripts/configs/…`,
`$repo/scripts/plans/…`, `$repo/scripts/orchestrator.ps1`. Only `bench.ppx` (`:49`) has no repo
home — it is a Proxifier GUI export and stays a `/tmp` staging file; that one line should say so in a
comment. Two smaller defects in the same file: `:30` lists `C:\wfbench\e2e` **twice** in the
directory array, and the loop at `:52` never ships `scripts/plans-windows/full-shape-plan.json`, so a
Windows lightweight run needs a hand-made `-PlanRoot` (as E5-b2 did,
`…/research/tickets.md:94`).

### 2.5 `orchestrator.ps1` — T3, the swallowed exit-code log (still open)

```
 62:function Write-Log {
 64:    Write-Output ('{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Message)
…

262:    Write-Log ('  client {0} exit={1} in {2:n0}s' -f $Label, $exit, $stopwatch.Elapsed.TotalSeconds)
263:    return (Test-ClientRun -OutDir $OutDir -Label $Label)
…
358:    [void](Invoke-Client -Label ($Product.Id + '-p' + $Pass) -OutDir $productDir `
…
382:    [void](Invoke-Client -Label ('control-' + $Tag + '-p' + $Pass) -OutDir $dir `
```

`Write-Log` writes to the **success stream** (`Write-Output`). `Invoke-Client`'s own output is its
return value (`:263`), and both of its call sites discard that wholesale with `[void](…)`. So every
line `Invoke-Client` logs — including the `client … exit=…` line at `:262` — is collected into the
pipeline and thrown away. The log lines emitted from `Invoke-Row` (`:345-346`, `:349`, `:364`),
`Invoke-DualPhase` (`:326`, `:330`) and `Invoke-ControlBlock` (`:379`) survive, because those
functions are called bare (`:368`, `:451`, `:448`). Net effect: the campaign log has **zero** client
exit-code lines, which is why AC18's first criterion had to be inferred from `failed` instead of read
(`…/research/tickets.md:10`, `design-decisions.md:950`).

**Fix:** make `Write-Log` write to the host rather than the pipeline (`Write-Host`), or have
`Invoke-Client` return a small object and log the exit code at the call site, e.g.
`$run = Invoke-Client …; Write-Log ('  {0} exit={1}' -f $run.Label, $run.Exit)`. The `Write-Host`
variant is the one the ticket names (`tickets.md:10`) and is a two-line change; the object variant is
strictly better PowerShell and keeps `Write-Log`'s output redirectable. Two cosmetic extras in the
same file: `Invoke-ControlBlock`'s `-Pass` parameter (`:375`) is bound but never used, and
`Invoke-Row`'s `-Pass` (`:340`) is only used to build the label.

### 2.6 `make_tree.py:15-16` — T6, docstring contradicts the generator (still open)

```
15: * a `target-ledger.jsonl` per pass whose source census, connection records and per-port
16:   DNS summaries agree with the client records (except where a mismatch is intentional);
…
75: Two ledgers are generated, as the shipped `start-targets.sh` starts two target instances: the
76: proxied rows' traffic goes to `ledger-main.jsonl` (port 40010, dns 53) and the dual phase's
77: direct lane to `ledger-direct.jsonl` (port 40011, dns 40054), both beside the raw directory
78: rather than inside it.
…
1523:    for path, records in ((out.parent / "ledger-main.jsonl", main_records), (out.parent / "ledger-direct.jsonl", direct_records)):
```

The generator writes exactly two ledgers, beside the raw directory — never a per-pass
`target-ledger.jsonl`. Confirmed independently: `tar tzf …/synthetic-tree.tar.gz | rg ledger` yields
only `ledger-main.jsonl` and `ledger-direct.jsonl` across all 472 entries. Following the docstring
double-counts (the ticket records 74094 → 148188, `tickets.md:13`).

**Fix:** replace `:15-16` with the two-ledger sentence that already exists at `:75-78` — the file is
internally inconsistent rather than ignorant of the truth, so this is a pure deletion of the stale
bullet. (Editing `make_tree.py` invalidates the frozen golden per `FROZEN.md:116-118`; a
docstring-only edit does not change the tree's bytes, but the re-freeze rule is written without
carve-outs, so the hash row `FROZEN.md:22` should be re-measured and re-recorded in the same commit.)

### 2.7 `FROZEN.md` — two dead documentation links

```
35:(`.trellis/tasks/10-07-e2e-harness-refactor/research/baseline/E4c-caveats.md` §2).
92:`.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md`. Both implementations
```

Neither path exists. Both are prose attributions rather than live inputs, so nothing fails — but a
reader following the re-freeze recipe cannot reach them. Fix: insert `archive/2026-10/` in both.

### 2.8 `start-targets.sh:14` — `$WF_PUB` is ignored

```
14:binary=/tmp/wf-bench/pub/linux/WinForward.E2E
```

`publish.sh:8` writes to `${WF_PUB:-${TMPDIR:-/tmp}/wf-bench/pub}`. On this machine `TMPDIR` and
`WF_PUB` are both unset so the two agree, but a `WF_PUB` override silently leaves the launcher
pointing at a stale tree (and `$TMPDIR` set to anything but `/tmp` does the same). Fix:
`pub=${WF_PUB:-${TMPDIR:-/tmp}/wf-bench/pub}; binary=$pub/linux/WinForward.E2E`.

### 2.9 Syntax is clean everywhere

For completeness: `bash -n` passes on all 8 shell scripts and `python3 -m py_compile` passes on all
13 Python scripts. Every defect above is a path or a logic bug, not a syntax error.

### 2.10 Same deletion, four stale doc pointers in a **live** task

`publish-campaign.sh` is not the only thing still pointing at the deleted campaign tree. The task
`10-06-e2e-competitor-benchmark` is **not** archived, and four of its documents name the directory:

```
.trellis/tasks/10-06-e2e-competitor-benchmark/implement.md:101:  `benchmarks/results/2026-10-06-e2e-competitors/README.md`: the headline matrix, the ratio-to-control
.trellis/tasks/10-06-e2e-competitor-benchmark/prd.md:118:        `benchmarks/results/2026-10-06-e2e-competitors/`.
.trellis/tasks/10-06-e2e-competitor-benchmark/design.md:210:  `benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py` (Linux, Python) reads the JSONL
.trellis/tasks/10-06-e2e-competitor-benchmark/design.md:234:  - `benchmarks/results/2026-10-06-e2e-competitors/` — raw JSONL, effective product configurations,
```

`design.md:210` is the worst of the four: it presents the retired Python analyzer as the live
reader of the campaign records. `design.md:234` and `prd.md:118` point at evidence that no longer
exists. **Fix:** re-target them at `benchmarks/WinForward.E2E.Analysis/` (the analysis that replaced
it) and at wherever the campaign's evidence actually lives now — or mark the documents as historical
if that task is itself finished. Out of this audit's write scope (it is a live task directory), so it
is registered here rather than fixed.

---

## 3. Delete list

### 3.1 `benchmarks/WinForward.E2E/scripts/contract-inventory.py`

**Verdict: spent, not broken.** I checked the strong claim first and it is *false*: both of its inputs
still exist. The rename baseline named at
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/contract-rename.md:7` is
`…/research/baseline/run1`, which survived archiving intact (`run1/out/*.jsonl`, `ledger.jsonl`,
`target.out`), and the fresh run named at `:8` (`/tmp/e3e/post1`) is still on disk. So the tool would
run today and reproduce roughly the table it already produced. The case for deletion is consumption,
not reachability:

1. **Its only tracked reference is a documentation row.** `benchmarks/WinForward.E2E/README.md:65` —
   "publish `research/contract-inventory.json` and `research/contract-rename.{json,md}` from a record
   tree" — pointing at a `research/` directory that no longer exists in either project. No test, no
   workflow and no other script invokes it.
2. **Every input it reads is migration-era, and the repository does not own them.**
   `contract-inventory.json`'s own `run` field is `/tmp/b1c-run`; the rename table's inputs are a tree
   inside an archived task directory and a `/tmp` scratch tree (`contract-rename.md:7-8`). Nothing in
   the live tree can reproduce either. Regenerating the tables would therefore depend on `/tmp`
   surviving — the exact fragility this audit is about.
3. **Its logic is a record of a closed migration.** The four D5 convergence families are hardcoded
   (`:46-54`, `:84-90`) and the whole `ADDITIONS` table (`:65-82`) is keyed to named batches
   (`D7`, `D14.23`, `D19.3`, `E3-b1/c/d/e`). This is the E1–E3 migration's ledger, not a general tool.
4. **Its outputs' consumers need the frozen bytes, never a fresh run.** `check-readme-contract.py:47`
   and `check-fixture-drift.py:65-66` read the *archived* tables as fixtures. A regenerated table
   would silently move both gates' ground truth.
5. **The contract it inventories is now asserted natively in C#.** `ContractShapeTests` +
   `DeclaredKeys` (`tests/WinForward.E2E.Tests/ContractShapeTests.cs:14-19`, `DeclaredKeys.cs:6-9`)
   compare each kind's published bytes against `ArmKeys` in-process, which is the same two-way diff
   this tool computed offline.

**Its two output tables must survive the deletion** — they are still live inputs. See §4.3.

### 3.2 `benchmarks/WinForward.E2E/scripts/normalize-pattern-hits.py`

**Verdict: spent, not broken** — its `--normalize` subject survives in the archive
(`record-normalize.json`) and `--run` accepts any tree, so it too would still execute. It is dead
because its question is closed and answered:

1. **Only documentation references it:** `benchmarks/WinForward.E2E/README.md:67` and
   `.trellis/spec/backend/measurement-judgement.md:104`. No test, workflow or script calls it.
2. **Its subject is the D15 migration's classification table.** `record-normalize.json` is the
   pre-migration artifact that sorted the old record paths into `readingPathPatterns` /
   `volatileArityPatterns` / `identityPathPatterns` / `contractPathPatterns` so the *port* could be
   judged. D16.2's question — "is every declared pattern a shape a run actually publishes?" — was
   answered for that contract and the answer is what the file now holds.
3. **The C# side classifies by declared keys, not by pattern.** `DeclaredKeys.Under` reads the
   `ArmKeys` constants and `ContractShapeTests` compares them to the published paths, so a stale
   classification is now a compile-or-test failure rather than a pattern that matches nothing.

There is no second consumer to preserve, and no artifact of its own to keep: its output is a report on
stdout (`normalize-pattern-hits.py:88-102`), nothing on disk.

### 3.3 The `__pycache__` trees

`benchmarks/WinForward.E2E/scripts/__pycache__/` (7 `.pyc`) and
`benchmarks/WinForward.E2E.Analysis/verification/__pycache__/check-boundary-trees.cpython-314.pyc`.
Neither is tracked (`git ls-files | rg '__pycache__|\.pyc$'` exits 1) and both are ignored by
`.gitignore:28`. They are pure local bytecode caches; the one inside `verification/` is additionally
awkward because every *other* artifact in that directory is hash-frozen in `FROZEN.md:16-25` and this
one is not listed, so a reader auditing the frozen set meets an unexplained file. Delete them and add
nothing to `.gitignore` — the rule already covers them.

---

## 4. Out-of-place list (moves, with the references each implies)

### 4.1 `effective-lines.py` → a new root-level `tools/` — the one genuine scope error

The 400-effective-line rule is a **solution-wide** quality gate, not an E2E gate:

- `.trellis/spec/backend/directory-structure.md:98` — "## File Length Ceiling (2026-08-28; **extended
  to benchmarks/** 2026-08-29)";
- `:100` — "**Every `.cs` file stays at or under 400 effective lines**";
- `:103` — the gate *is* `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py <paths>`;
- `benchmarks/README.md:433` runs it against `benchmarks/WinForward.Benchmarks`, a project with no
  relationship to the E2E harness at all, and registers three exceptions there;
- `.trellis/spec/backend/measurement-tooling.md:80-90` runs it over four paths spanning
  `WinForward.E2E`, `WinForward.E2E.Contracts`, `WinForward.E2E.Analysis` **and**
  `tests/WinForward.E2E.Tests`.

Meanwhile `benchmarks/WinForward.E2E/README.md:694-697` calls it one of "**the harness's own three
gates**", alongside `check-readme-contract.py` and `CliSnapshotTests`. That sentence is the concrete
symptom: the README has adopted a repository policy because the tool happens to live in its folder.

**Destination:** `tools/effective-lines.py` (creating `tools/`). It is the only proposal here that
needs a new top-level directory, and the alternative homes are worse: `analyzers/` is a Roslyn
analyzer and this is a file counter, and `tests/` is for xunit projects.

**References to update (all of them):**
- `.trellis/spec/backend/directory-structure.md:103`
- `.trellis/spec/backend/measurement-tooling.md:80-90` (the command block and the sentence
  describing the counter's rule)
- `benchmarks/README.md:433`
- `benchmarks/WinForward.E2E/README.md:71` (drop the row from the harness script table) and `:695`
  (rewrite "the harness's own three gates" so it no longer claims a repo-wide rule)
- `effective-lines.py`'s own usage docstring `:20-22`

**Language:** **keep Python.** It is a text tool with no dependencies and its comment/string scanner
(`:35-173`) is already written and correct. But record the honest counter-argument for a later task:
"strip comments the way the compiler sees them" is exactly what Roslyn gives for free, so a
`Microsoft.CodeAnalysis.CSharp` implementation living in `analyzers/` would be *more* correct than the
hand-rolled scanner (raw strings, interpolated strings with nested braces, `#if` regions) and could
report per-file inside the IDE. That is a real justification, not an aesthetic one — but it is a
rewrite with a test burden, and it does not belong in a cleanup task.

### 4.2 `check-fairness.py` and `oracle-diff.py` → `WinForward.E2E.Analysis/verification/`

Both are instruments of the **analysis** project, not the harness:

- `check-fairness.py:421-431` resolves `benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py`
  and `.../bin/Release/net10.0/WinForward.E2E.Analysis`, then runs them; it reads
  `verification/row-profiles.json` (`:119-120`). It never touches `benchmarks/WinForward.E2E/`.
- `oracle-diff.py:106-120` resolves `verification/golden`, `verification/synthetic-tree.tar.gz`,
  `verification/plots-SKIPPED.md` and the analysis binary from the same project. It has no
  harness-side input at all — `REPO_ROOT` (`:106`) is used only to reach `WinForward.E2E.Analysis/`.
- The spec already sees the misplacement: `.trellis/spec/backend/measurement-tooling.md:127` calls
  `oracle-diff.py` "the differ — **the harness's `scripts/`, not this project's**".
- The analysis project already owns four checkers of its own
  (`verification/check-boundary-trees.py`, `verification/check-fixture-drift.py`,
  `verification/synthetic/make_tree.py`, `verification/synthetic/make_cp_vectors.py`) and the frozen
  data all six operate on. Splitting two of the six across a project boundary is arbitrary.

**Destination:** `benchmarks/WinForward.E2E.Analysis/verification/`.

**References to update:**
- `benchmarks/WinForward.E2E/README.md:68` (drop the `oracle-diff.py` row) and `:69` (drop the
  `check-fairness.py` row) — or keep them as cross-references, but they no longer describe this
  directory
- `benchmarks/WinForward.E2E.Analysis/verification/FROZEN.md:5,25,46,149,156,174`
- `benchmarks/WinForward.E2E.Analysis/verification/row-profiles.json:4` (names
  `benchmarks/WinForward.E2E/scripts/check-fairness.py` verbatim)
- `benchmarks/WinForward.E2E.Analysis/README.md:498`
- `.trellis/spec/backend/measurement-tooling.md:127-129`
- `.trellis/spec/backend/measurement-judgement.md` (if it names either path)
- `check-fairness.py`'s own usage docstring `:26-28`

**Counter-argument to weigh, and my call:** a *judge* should not live inside the *judged* project.
But the project has already accepted that trade-off — the frozen golden, the tree and the boundary
checkers are all inside `WinForward.E2E.Analysis/verification/`, and `FROZEN.md:12` states outright
that "changing it changes what the C# implementation is judged against". Given that, keeping two of
the six instruments outside is the inconsistency, not the independence. Move them.

### 4.3 The two contract tables → `WinForward.E2E.Analysis/verification/` (or make the lookup archive-aware)

`contract-inventory.json` and `contract-rename.json` are currently
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/` — an archived task directory.
They have two live consumers (`check-readme-contract.py:47`, `check-fixture-drift.py:65-66`) and,
once `contract-inventory.py` is deleted (§3.1), no generator. Their natural neighbours are
`verification/row-profiles.json` (same kind of artifact: frozen data a checker asserts against) and
`verification/golden/*`. Moving them makes both checkers' default paths stable forever and lets §2.1
and §2.2 be fixed as one-line path edits instead of an archive-walking helper.

If the team prefers the archive as their permanent home, the alternative in §2.1 (archive-aware
lookup mirroring `RepoPaths.cs:53-67`) is equally valid — but then
`measurement-judgement.md:101-103` must be understood as the authority, and both checkers need the
lookup duplicated.

### 4.4 `selftest.sh`'s embedded Python heredoc — a small, optional de-Pythoning

```
52:python3 - "$work/out" <<'PY'
…
78:PY
```

26 lines of Python that walk `$work/out/*.jsonl`, keep the `result` records, and pretty-print each
arm's metrics, latency histogram and notes. This is the **only** place where a tracked harness script
needs Python for something that is not a gate — and it duplicates what the analysis already prints
(`analyze.sh --raw /tmp/wf-bench/selftest/out --flat --out /tmp/selftest-analysis`,
`benchmarks/WinForward.E2E/README.md:600`). Two non-rewrite options, both smaller than the heredoc:
add a `--summary` verb to the harness binary (it already owns the record model and `ArmKeys`), or
delete the heredoc and point the operator at `analyze.sh`. **Not urgent; note it as a follow-up.**

### 4.5 What is *not* out of place — the explicit negative results

- **`jsonl_paths.py` is E2E-only, not solution-scoped.** All four of its importers are E2E tools
  (`contract-inventory.py:33`, `compare-records.py:89`, `normalize-pattern-hits.py:38`,
  `check-fixture-drift.py:44`); the only non-E2E mention is the C# mirror's own comment
  (`tests/WinForward.E2E.Tests/JsonPaths.cs:13`), which points at it as the definition of the
  alphabet. The README's claim at `benchmarks/WinForward.E2E/README.md:66` is accurate. It does not
  belong at the repository root.
- **The plans and configurations belong where they are.** `plans*/*.json` are consumed by
  `tests/WinForward.E2E.Tests/RepoPaths.cs:11-20`, `PlanFileTests.cs:79-91` and
  `selftest.sh:17`; `configs/*` are the campaign's measurement inputs
  (`benchmarks/WinForward.E2E/README.md:61`). They are E2E assets under an E2E project. The
  `plans-windows` directory is deliberately a third directory rather than a file inside the other two
  (`RepoPaths.cs:15-20`).
- **`make_cp_vectors.py` must stay Python** — its stated purpose is that the vectors come from
  CPython itself (`make_cp_vectors.py:16-19`). A C# reimplementation would defeat it.
- **`make_tree.py` should stay Python** in the near term: it is a 1532-line fixture generator whose
  bytes are hash-frozen (`FROZEN.md:18-22`), and a port would invalidate the golden, the tarball and
  all five batches' evidence (`FROZEN.md:116-127`). The cost/benefit is bad until the frozen oracle
  itself is retired.

---

## 5. Keep-as-is list, with one-line justifications

| Kept | Why |
|---|---|
| `scripts/publish.sh` | Live, referenced from the Contracts csproj and both READMEs; builds the four artifacts the campaign's whole attribution story depends on |
| `scripts/selftest.sh` | The one-host end-to-end smoke test; live in both READMEs and `AGENTS.local.md:9` (see §4.4 for its heredoc) |
| `scripts/orchestrator.ps1` | The campaign driver; only needs the T3 fix (§2.5) |
| `scripts/cli-snapshots.py` | Verified byte-consistent with the recorded `after/` tree (31 cases, names and argv identical to `after/index.json`); it is the only way to extend the CLI record |
| `scripts/compare-records.py` | Declared a live manual gate by `measurement-judgement.md:91-108`; its fixtures survive in the archive and the spec already says so |
| `scripts/jsonl_paths.py` | The alphabet's single definition, imported by two surviving tools (`compare-records.py`, `check-fixture-drift.py`) and mirrored by `JsonPaths.cs` |
| `scripts/check-readme-contract.py` | The only gate that reads the README contract table; keep after the §2.1 path fix |
| `WinForward.E2E.Analysis/scripts/analyze.sh` | A model thin wrapper: builds, `exec`s, changes nothing (see its own header `:2-11`) |
| `verification/freeze-tree.sh` | The reproducible re-freeze procedure named by `FROZEN.md:29,79` |
| `verification/check-boundary-trees.py` | The only home for the C#-only boundary shapes and their negative controls (`FROZEN.md:150-154`) |
| `verification/synthetic/make_tree.py` | The fixture generator every regression runs on (fix T6's docstring, §2.6) |
| `verification/synthetic/make_cp_vectors.py` | Must be CPython; its outputs are read by three xunit facts |
| `verification/golden/*`, `synthetic-tree.tar.gz`, `plots-SKIPPED.md`, `row-profiles.json` | Every one has a verified live consumer (table §1.4) |
| `scripts/plans*/*.json`, `scripts/configs/*` | Consumed by `RepoPaths`/`PlanFileTests` and by the (to-be-fixed) deploy script |

---

## 6. Proposed end state

### 6.1 `benchmarks/WinForward.E2E/scripts/` — the campaign kit and the harness's own gates

```
benchmarks/WinForward.E2E/scripts/
├── plans/                    # 6 committed campaign plans (unchanged; read by RepoPaths + PlanFileTests)
├── plans-short/              # 5 short-duration plans for validating a change (unchanged)
├── plans-windows/            # 1 compressed full-plan for the Windows lightweight validation (unchanged)
├── configs/                  # 6 product configurations each row is measured with (unchanged)
├── publish.sh                # build + publish the four artifacts (unchanged)
├── selftest.sh               # one-host end-to-end smoke test (unchanged; heredoc logged as follow-up)
├── orchestrator.ps1          # the campaign driver for the machine under test (T3 fixed)
├── check-readme-contract.py  # the README contract-table gate (path fixed, §2.1)
├── cli-snapshots.py          # the CLI recorder whose trees CliSnapshotTests replays (unchanged)
├── compare-records.py        # the run-to-run comparator, a declared manual gate (unchanged)
└── jsonl_paths.py            # the shared path alphabet, imported by compare-records (unchanged)
```

Changes relative to today: **+0 files, −2 files, 3 files out.**

| Leaves `WinForward.E2E/scripts/` | Goes to | Why |
|---|---|---|
| `check-fairness.py` | `WinForward.E2E.Analysis/verification/` | drives the analysis binary and `verification/row-profiles.json`; never touches the harness (§4.2) |
| `oracle-diff.py` | `WinForward.E2E.Analysis/verification/` | every input is `verification/**`; the spec already calls it foreign (§4.2) |
| `effective-lines.py` | new root `tools/` | enforces a repository-wide rule over four projects (§4.1) |
| `contract-inventory.py` | deleted | spent migration tool, no consumer (§3.1) |
| `normalize-pattern-hits.py` | deleted | spent migration check, no consumer (§3.2) |

The four gitignored machine scripts (`wf.sh`, `deploy-campaign.sh`, `start-targets.sh`,
`publish-campaign.sh`) stay untracked and in place; `.gitignore:4-7` continues to exclude them.
`deploy-campaign.sh` and `publish-campaign.sh` still need their local fixes (§2.3, §2.4), which are
edits to files the repository does not carry.

### 6.2 The shell/`ps1` split versus the README's gitignore claim

`git ls-files benchmarks/WinForward.E2E/scripts` returns 30 paths: the 6 `configs/*` (5 JSON + the
`proxybridge.pbprofile`), the 12 `plans*/*.json`, and **12 scripts** — `check-fairness.py`,
`check-readme-contract.py`, `cli-snapshots.py`, `compare-records.py`, `contract-inventory.py`,
`effective-lines.py`, `jsonl_paths.py`, `normalize-pattern-hits.py`, `oracle-diff.py`,
**`orchestrator.ps1`**, `publish.sh`, `selftest.sh`.

Of the five scripts the question names:

| Script | Tracked? | README says | `.gitignore` says | Verdict |
|---|---|---|---|---|
| `orchestrator.ps1` | **yes** (`git ls-files` lists it) | committed — in the Layout table at `README.md:62`, and the enclosing comment says "The plans, the product configurations and the orchestrator are committed" | not listed | **consistent** |
| `wf.sh` | no | absent from a fresh checkout (`README.md:79`) | `:4` | consistent |
| `deploy-campaign.sh` | no | `README.md:80` | `:5` | consistent |
| `start-targets.sh` | no | `README.md:81` | `:6` | consistent |
| `publish-campaign.sh` | no | `README.md:82` | `:7` | consistent |

**There is no mismatch between the README's claim and the tracked set** — of the five scripts the
question names, exactly one (`orchestrator.ps1`) is tracked and four are not, and that is precisely
what `benchmarks/WinForward.E2E/.gitignore:4-7` and `README.md:74-83` say.

Two observations worth registering anyway:

1. **The split's stated rationale does not fully hold.** `benchmarks/WinForward.E2E/.gitignore:1-3`
   justifies the exclusion with "they describe one particular pair of machines rather than the
   experiment" — but the committed `orchestrator.ps1` is *more* machine-coupled than any of the four
   excluded scripts: it hardcodes `192.168.100.4` (`:26`), **fourteen** `C:\wfbench\…` literals
   (`:24-25`, `:36-41`, `:73-75`, `:103`, `:108-109`), `C:\Program Files\ProxyBridge\ProxyBridge_CLI.exe`
   (`:107`) and the service names `ProxiFyreService` / `Proxifier` (`:98`, `:102`). The difference is
   that the orchestrator's
   couplings are all overridable default parameters with a documented table
   (`README.md:269-281`), while the four excluded scripts have none. That is a defensible line — it
   just is not the line the comment describes. Reword the comment to "these four embed no parameter
   for the machine, so a reader cannot retarget them" and the claim becomes true.
2. **`AGENTS.local.md` — read as instructed, and it does not contradict the tracked scripts.**
   §7 (`AGENTS.local.md`) says both instances "都必须带 `--label target:<自己的端口>`", and
   `start-targets.sh:29,33` do exactly that (T1 is fixed). §8's run order (`publish.sh` →
   `deploy-campaign.sh` → `start-targets.sh` → orchestrator under pwsh7 → `publish-campaign.sh`)
   matches the README's. §5's inventory of `C:\wfbench\e2e\` plan files matches
   `deploy-campaign.sh:52` one-for-one. Three small asymmetries, none a contradiction:
   - §4's `wf.sh` command list omits `cap` and `raw`, which `wf.sh:70,72` implements;
   - §5 lists the plan set but not `plans-windows/full-shape-plan.json`, consistent with
     `deploy-campaign.sh:52` never shipping it — so the E5-b2 run's `-PlanRoot C:\wfbench\e2e-win2`
     (`…/research/tickets.md:94`) was hand-made and remains unreproducible from the scripts;
   - §5's directory listing includes `watchdog.ps1` and `heartbeat.txt`, which no tracked script
     stages (`deploy-campaign.sh` stages only `orchestrator.ps1`, `:57`).

   Also note the header comment at `wf.sh:5` advertises `wf.sh cap [n] | key '<text>' | raw '<text>'`
   while `AGENTS.local.md` §4 lists only `run`/`file`/`up`/`down`/`idle`/`key` — the doc is the
   incomplete one.

### 6.3 Hygiene — clean, with one recommendation

| Question | Answer | Evidence |
|---|---|---|
| Are `__pycache__`/`.pyc` git-tracked? | **No** | `git ls-files \| rg '__pycache__\|\.pyc$'` exits 1 (no match) |
| Are they gitignored? | **Yes** | `.gitignore:28` `__pycache__/`; verified with `git check-ignore -v` on both trees |
| Are `bin/`/`obj/` in the relevant `.gitignore`? | **Yes** | `.gitignore:2-3` (`**/bin/`, `**/obj/`) plus `benchmarks/WinForward.E2E/.gitignore:9-11`; `git check-ignore -v` confirms both |
| Are published trees tracked? | **No** | only `$WF_PUB` (`/tmp`) and `bin/obj` — `git ls-files` shows no publish output |
| Any generated file committed by accident? | **No** | `git status --porcelain --ignored=no benchmarks/` is empty; the only ignored-but-present things under the E2E projects are `bin/`, `obj/`, the two `__pycache__` trees, `AGENTS.local.md`, and the four machine scripts |
| Intentional generated-but-tracked files? | Yes, and correctly so | `verification/golden/*` (5), `verification/synthetic-tree.tar.gz`, `verification/plots-SKIPPED.md` are all hash-frozen in `FROZEN.md:16-25` and have live consumers (table §1.4) |

**Recommendation:** delete the two `__pycache__` trees (§3.3) and nothing else. The
`verification/__pycache__/` one deserves the deletion most, because `FROZEN.md`'s table enumerates
every other file in that directory and this one is not in it — an auditor comparing the directory to
the manifest currently finds one unexplained file.

---

## 7. Open questions

1. **Where should the two contract tables live?** §4.3 recommends
   `WinForward.E2E.Analysis/verification/`; §2.1's alternative keeps them in the archive behind an
   archive-aware lookup. This is a genuine either/or and needs a decision, because it fixes both
   broken checkers at once.
2. **Is `check-fixture-drift.py` worth keeping past the migration?** Its two authorities are frozen
   migration artifacts with no generator left once `contract-inventory.py` goes (§3.1), and neither is
   reproducible from the live tree — the baseline sits inside an archived task directory and the fresh
   run only in `/tmp` (`contract-rename.md:7-8`). It still
   catches a `make_tree.py` edit that invents a key. The C#-native replacement sketched in §2.2
   (read the tracked tarball's canonical paths against `ArmKeys`) has not been prototyped, so the
   cost of retiring it is unmeasured.
3. **Should `compare-records.py` and `jsonl_paths.py` survive?** I recommend yes — the spec declares
   `compare-records.py` a live manual gate (`measurement-judgement.md:91`) and its fixtures are
   documented as archived. But the honest caveat is that its required `--normalize` fixture is
   migration-era, and if the team wants a run-to-run comparator for future campaigns it should be a
   `compare` verb in the analysis project where the record model already lives. I could not determine
   from the tree whether run-to-run comparison is still on the roadmap. **Interaction with §3:** the
   two deletions there take `jsonl_paths.py` from four importers down to two
   (`compare-records.py:89`, `check-fixture-drift.py:44`); if `compare-records.py` goes too, it is
   down to one, and the pair becomes a joint deletion candidate.
4. **Is `orchestrator.ps1` in the right place at all?** §6.2 argues the tracked/gitignored split is
   defensible but mis-described. The deeper question — whether a script whose row table names
   `C:\Program Files\ProxyBridge\ProxyBridge_CLI.exe` and one particular service should be tracked at
   all — is a product decision, not an audit finding.
5. **The `plans-windows` deployment gap.** `deploy-campaign.sh:52` ships five plans; the tracked
   README documents three plan directories. I could not find any script that ships the sixth
   (`plans-windows/full-shape-plan.json`), and the E5-b2 evidence shows it was staged by hand. Whether
   that is intentional (a one-off validation run) or an oversight in a gitignored script is not
   determinable from the repository.
6. **`selftest.sh`'s heredoc** (§4.4). I recommend retiring it in favour of `analyze.sh` or a
   `--summary` verb, but I did not verify that `analyze.sh --flat` prints every field the heredoc
   does (`notes` in particular) — a five-minute check before deciding.
7. **Scratch state outside the repository.** `/tmp/wf-bench/` holds a dozen hand-written tools not
   under version control (`clock-probe.py`, `clock-probe.ps1`, `clock-offset.py`, `e5b2-*.py`,
   `e5b2-gates.sh`, `watchdog.ps1`, `bench.ppx`, `deploy/`). They are cited as evidence in the
   archived tickets (`tickets.md:123`) and are outside this audit's scope by construction, but they
   are part of how the harness is actually operated and will be lost with `/tmp`.
