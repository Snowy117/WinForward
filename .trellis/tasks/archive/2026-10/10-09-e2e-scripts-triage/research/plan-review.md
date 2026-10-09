# C3 adversarial plan review

Reviewer: adversarial pass over `.trellis/tasks/10-09-e2e-scripts-triage/{prd.md,implement.md}` against the
tree at `f992370`. Every claim below carries `file:line`. Read-only: only this file was written; no
`dotnet`/`jb`/analyzer/campaign script was run.

## Verdict

**Executable with fixes.** The deletion half (A group) is sound and I reproduced its two "broken today"
premises from the scripts themselves — both checkers fail exactly where the audit says, and neither deleted
script has a code caller. The move half (B group) is mechanically wrong in three places the plan does not
name: `check-fixture-drift.py`'s own `REPO_ROOT`/`SCRIPTS` constants (`check-fixture-drift.py:38-39`, not
just the table path at `:40`), the "recompute `parents[N]`" instruction for `oracle-diff.py` (which is a
no-op and, as worded, invites a wrong edit), and the cross-import of `jsonl_paths` that survives the move
only because the plan leaves that `sys.path` line alone. Two acceptance criteria (AC5's fixture-drift
criterion and AC7's `bash -n` on files nobody can commit) are not measurable as written. Nothing in the
plan is unrecoverable: all BLOCKER-class items are one-line amendments, listed below.

## BLOCKER

**B1 — the tables must move (or the lookup must be rewritten); the plan presents an untaken decision as an
optimisation and does not name the failing path constant.**
`.trellis/tasks/10-09-e2e-scripts-triage/implement.md:40-44` offers "move vs archive-aware lookup" and
decides neither, while `prd.md:28` (AC4) already asserts the tables *are* in `verification/`. That is a
scope decision the plan has not made. Confirmed state: both tables are tracked today at
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/{contract-inventory.json,contract-rename.json}`
(plus `contract-rename.md`), verified with `git ls-files`; the live path
`.trellis/tasks/10-07-e2e-harness-refactor/` does not exist. `check-readme-contract.py:47` and
`check-fixture-drift.py:40` both hard-code that dead path. **Amendment — pick the move and say so on
`implement.md:40`:** replace "二选一，选完把理由写进 commit message" with
`采用搬迁（archive-aware 查找需要两个脚本各复制一份 RepoPaths.cs:53-67 的逻辑，且把 ground truth 永久留在历史目录里）。`
Cheaper alternative, if the team prefers zero `git mv`: fix the two readers instead —
`check-readme-contract.py:47` →
`RENAME_TABLE = REPO_DIR / ".trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/contract-rename.json"`
and `check-fixture-drift.py:38-40` → the same archive prefix in `RESEARCH`. That is ~3 changed lines
against one `git mv` of 3 files plus 4 reader edits; the move is the better permanent fix, but it is a real
fork and must be stated, not left to the implementer.

**B2 — `check-fixture-drift.py`'s path constants are wrong for its *current* home, and the plan fixes only
the table path.**
`check-fixture-drift.py:38` `REPO_ROOT = Path(__file__).resolve().parents[3]` is correct for a file in
`.Analysis/verification/` (`verification`→`.Analysis`→`benchmarks`→repo, four levels), so if the tables
move to `verification/` the default `--root` (`:108`) must become that directory and not
`SCRIPTS`/`RESEARCH`. `check-fixture-drift.py:39` `SCRIPTS = REPO_ROOT / "benchmarks" / "WinForward.E2E" /
"scripts"` is fine today and is what makes `import jsonl_paths` (`:42-44`) resolve; moving the tables to
`verification/` does not break it, but the plan never mentions that this `sys.path.insert` is the only
thing keeping `jsonl_paths.py` importable from `.Analysis`. A naive "fix `:38-40` by deleting `SCRIPTS`"
breaks the import. Also `:12-14` (module docstring) still names
`.trellis/tasks/10-07-e2e-harness-refactor/research/` and `benchmarks/WinForward.E2E/scripts/jsonl_paths.py`
— the plan's B3 sync list (`implement.md:45-46`) omits this docstring. **Amendment:** add to
`implement.md:45` the line
`` check-fixture-drift.py:12-14 docstring 同步（表的新家 + jsonl_paths 仍在 harness scripts/ 的说明） ``,
and keep `:39` `SCRIPTS` as-is with the new default root at `:40`
(`VERIFICATION = REPO_ROOT / "benchmarks" / "WinForward.E2E.Analysis" / "verification"`).

**B3 — the `oracle-diff.py` `parents[N]` instruction is a no-op stated as a change.**
`oracle-diff.py:106` `REPO_ROOT = Path(__file__).resolve().parents[3]`. Current file is
`benchmarks/WinForward.E2E/scripts/oracle-diff.py` → `parents[3]` = repo root. Target is
`benchmarks/WinForward.E2E.Analysis/verification/oracle-diff.py` → `parents[3]` = repo root as well; the
directory depth is identical (`benchmarks/X/Y/file` vs `benchmarks/X/Y/file`). **`parents[3]` is correct
both before and after; no replacement line exists.** `implement.md:35-37` says
"重算成 `parents[3]`→`parents[3]`" and calls `verification/` "比 `scripts/` 深一层" — both false, and an
implementer who trusts the prose will change it to `parents[4]` and break the differ. Every other path
constant in the file is root-relative and move-invariant: `:107-114`
(`VERIFICATION`, `GOLDEN`, `ARCHIVE`, `PLOTS`, `ANALYZER`) all hang off `REPO_ROOT`. **Amendment:**
replace `implement.md:35-37` with
`oracle-diff.py:106 的 parents[3] 在 verification/ 下依然正确（两种位置都是 benchmarks/<项目>/<子目录>/<文件>，深度相同），不要改这一行；`:107-114` 全部挂在 REPO_ROOT 上，同样不动。`
`check-fairness.py` is genuinely move-invariant and needs no path edit: it resolves the root by walking up
for `benchmarks/WinForward.E2E` (`check-fairness.py:110-114`), builds `make_tree.py` and the analyzer from
that root (`:421-422`), and reads `row-profiles.json` from the same root (`:119-121`). `row-profiles.json:4`
is prose ("the rule source of benchmarks/WinForward.E2E/scripts/check-fairness.py") and is covered by
`implement.md:38`.

## SHOULD-FIX

**S1 — `check-fixture-drift.py` is not "broken today" in the way the plan tests it, and the plan's fix
loses the tree-side failure.** Reproduced twice: with `--tree /tmp/wf-synth` (exists, has `raw/`) the
script dies at `check-fixture-drift.py:65` on the missing `contract-inventory.json` and exits **1** with a
traceback; with `--tree benchmarks/WinForward.E2E.Analysis/verification/golden` it dies at the same line,
exit **1**, same traceback. The traceback never comes from `--tree`, so AC5's wording
(`prd.md:29`, `implement.md:56`) tests an input the plan does not repair. **Amendment:** replace that
criterion with two runs —
`python3 …/check-fixture-drift.py --tree /tmp/does-not-exist --root <new tables dir>` → rc=2 (no
traceback) **and** `--tree /tmp/wf-synth --root <new tables dir>` → rc=0 with
`fixture drift: none (both directions empty)` — and extend the plan's fix at `implement.md:56` to wrap the
`--tree` read as well (`:89-90` `raise SystemExit` for an empty tree is already the shape to copy).

**S2 — four named "fix" targets cannot be committed; the plan should say which of the seven repairs are
local-only, per site.** `git ls-files benchmarks/WinForward.E2E/scripts` lists 30 paths and does **not**
include `publish-campaign.sh`, `deploy-campaign.sh`, `start-targets.sh` or `wf.sh`; they are named by
`benchmarks/WinForward.E2E/.gitignore:4-7` and exist only on this machine. So C3's rows 3-5
(`implement.md:57-59`) are edits nobody can review, `bash -n` is the only gate they can ever pass, and
`prd.md:39` (risk 3) understates it as "改了但无法提交" without naming the files.
**Amendment:** append to `implement.md:64-65`:
`本任务 AC5 的 7 处修复中，第 3、4、5 处（publish-campaign.sh / deploy-campaign.sh / orchestrator.ps1 之外的 4 个 gitignored 脚本）仅本机生效、不进 commit；可提交的只有 check-readme-contract.py、check-fixture-drift.py、make_tree.py 与 FROZEN.md。`
`orchestrator.ps1` **is** tracked (`git ls-files` lists it) and so is committable.

**S3 — spec updates that the plan does not list.** `implement.md:84` names only
`measurement-tooling.md` and `directory-structure.md:103`. Grep of `.trellis/spec/` finds four more live
mentions that the plan's deletes and moves falsify:
`.trellis/spec/backend/measurement-tooling.md:71` (the `contract-inventory.py` row in "The scripts" table),
`:73` (`normalize-pattern-hits.py` row), `:75` and `:84` (`effective-lines.py` path + the four-path command
block), `:127-128` (`oracle-diff.py` and `check-fairness.py` paths), and
`.trellis/spec/backend/measurement-judgement.md:72,75,89` (three `contract-inventory.py` references),
`:103-104` (the archived-table path plus `normalize-pattern-hits.py`), and
`.trellis/spec/backend/measurement-record-contract.md:119` (`contract-inventory.py:53` as the DOTTED_LEAF
authority). **Amendment:** replace `implement.md:84` with
`spec 更新：measurement-tooling.md:71,73,75,78,80,84,127,128（删行/改路径/注明无 CI）、measurement-judgement.md:72,75,89,103-104（contract-inventory.py 已删 → 指向 C# 侧 DeclaredKeys/ContractShapeTests，归档表路径改新家）、measurement-record-contract.md:119（cite 改成归档表或删除）、directory-structure.md:103（新命令）。`

**S4 — the README table criterion is not achievable as written.** `implement.md:76` requires
`git ls-files benchmarks/WinForward.E2E/scripts` to agree "逐行" with the README table, but that table
deliberately also documents the four gitignored scripts (`benchmarks/WinForward.E2E/README.md:79-82`,
matching `.gitignore:4-7`) and the `plans*/` + `configs/` directories (`:58-61`), which `git ls-files`
renders as 30 individual paths. Literal equality is impossible; the audit already says so
(`research/03-scripts-audit.md:609-629`). **Amendment:** replace `implement.md:76` with
`判据：README 表（:58-72 跟踪行 + :74-83 本机行）中每个列出的脚本都能在 git ls-files 或 .gitignore:4-7 找到归属；git ls-files 的 12 个脚本无一缺失、无一行描述已删除/已搬走的脚本。`

**S5 — closing step has no observable effect.** `implement.md:82` runs
`dotnet build WinForward.slnx -c Release` to "validate" the new `tools/` directory. `WinForward.slnx`
enumerates its projects explicitly and `tools/` holds no `.csproj`; `.editorconfig` has no `tools/`
glob or file-length rule and `Directory.Build.props` holds no path discovery, so the build cannot see the
directory. **Amendment:** replace `implement.md:82` with
`无需为 tools/ 复跑 build：WinForward.slnx 显式列举项目、Directory.Build.props 与 .editorconfig 都无路径发现，tools/ 不在任何 gate 的扫描范围内（已核实 .github/workflows/{analyzer-gate,release-build}.yml 只跑 dotnet format / jb inspectcode / publish）。`

## NIT

**N1 — `publish-campaign.sh` line numbers are half wrong.** `implement.md:57` cites `:8,36`;
`:57`'s own criterion does not mention the ledger line. Verified: `:8` sets
`results="$repo/benchmarks/results/2026-10-06-e2e-competitors"` (deleted tree), `:36` is
`cd "$results/analysis"` (deleted directory, hard abort under `set -euo pipefail` at `:4`), and `:28` is
the `target-ledger.jsonl` test; `start-targets.sh:26` removes `ledger-main.jsonl`/`ledger-direct.jsonl`
and `:29`/`:33` write those two. `implement.md:58` does cover `:28`, so only the `:57` citation is loose.
**Amendment:** change `implement.md:57` to `publish-campaign.sh:8,36`.
**N2 — `deploy-campaign.sh` citation drift.** `implement.md:59` cites `:9,43-57`; the staging root is
`:9`, the config stages are `:43-49`, plans `:52`, orchestrator `:57`, and the duplicated
`C:\wfbench\e2e` is at `:30`. Read: all true. No amendment needed beyond noting `:52` never stages
`plans-windows/full-shape-plan.json`.
**N3 — `make_tree.py` docstring fix is real.** `implement.md:61` cites `:15-16`; the generator writes
exactly two ledgers at `:1523` (`ledger-main.jsonl`, `ledger-direct.jsonl`) and `:75-77` already says so,
so the fix is deleting the stale bullet. Not independently re-read beyond these lines (grep only).

## Verified true

- `python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py` → **rc=2**,
  stderr `cannot read …/.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json:
  [Errno 2] No such file or directory`; that directory does not exist. AC5's "rc=0" premise is real.
- `python3 benchmarks/WinForward.E2E.Analysis/verification/check-fixture-drift.py --tree /tmp/wf-synth`
  → **rc=1 + traceback** at `:65` on the missing `contract-inventory.json` (not on `--tree`); same result
  with a dummy existing tree. `/tmp/wf-synth/raw` does exist on this machine.
- AC5's quoted success string is exactly `:300-301` of `check-readme-contract.py`:
  `f"{len(tokens)} key(s) checked against {len(constants)} declared constant path(s): " f"{'ok' if not
  failures else …}"`. `tokens` comes from the README section, `constants` from `ArmKeys.*.cs`
  (`:35` glob, `:44-48` write roots); neither reads the contract tables, so **the counts cannot change
  when the table moves**. The `111`/`401` values themselves were not re-derived (that needs the fix).
- Delete list has no code callers. `rg -uu` over the tree (excluding `.git`, archives, `__pycache__`) for
  `contract-inventory|normalize-pattern-hits` finds only: the two scripts' own text, the two doc rows at
  `benchmarks/WinForward.E2E/README.md:65,67`, `measurement-judgement.md:72,75,89,103-104`,
  `measurement-tooling.md:71,73`, `measurement-record-contract.md:119`,
  `check-fixture-drift.py:12`, `make_tree.py:23` — no `import`, no `subprocess`, no test, no workflow.
- `.github/workflows/` holds exactly `analyzer-gate.yml` and `release-build.yml`; neither mentions E2E,
  Python or `tools/`. The "no CI runs these scripts" claim (`prd.md:30`) is true.
- `orchestrator.ps1`: `Write-Log` is `Write-Output` (`:62-64`); `Invoke-Client` logs `client … exit=…` at
  `:262` and returns `Test-ClientRun` at `:263`; both call sites discard the pipeline wholesale with
  `[void](…)` at `:358` and `:382`. `Invoke-Row`/`Invoke-ControlBlock` are called bare at `:448,451,454`,
  so their own logs survive and only `Invoke-Client`'s are swallowed — the audit's T3 is confirmed, and
  the ticket's `Write-Host` fix (`…/research/tickets.md:10`) is the two-line one.
- `tools/` is genuinely absent and unclaimed: no `tools/` directory, no `.github` glob, no
  `.editorconfig`/`Directory.Build.props`/`.gitignore` rule, no `WinForward.slnx` entry.
- `benchmarks/WinForward.E2E/README.md:79-82` + `.gitignore:4-7` confirm the four local-only scripts, so
  `prd.md:17` ("4 个 gitignored 本机脚本...就地修，不入库") matches the tree.

## Could not verify without running

- Whether `--root` at the new `verification/` home yields `fixture drift: none` (rc=0): needs the
  `git mv` first. I did not run `oracle-diff.py` or `check-fairness.py` (both execute the built Release
  analyzer and rewrite `/tmp/wf-synth`), so AC4's "全批次 rc=0" and "全 PASS" rest on the audit's word.
- The `111`/`401` numbers and their stability, and `check-readme-contract.py`'s pass after retargeting
  `:47` at the archive or the new home.
- `bash -n` cleanliness on the four edited shell scripts (AC7): the audit reports it clean today
  (`research/03-scripts-audit.md:330-333`); I did not run it.
- `orchestrator.ps1`'s syntax check under `pwsh` — no `pwsh` verified present, and AC7 already allows a
  manual review.
- `FROZEN.md:35,92` and the four `10-06-e2e-competitor-benchmark` pointers — taken from
  `research/03-scripts-audit.md:309-317,335-352`; not re-read line by line here.
- `compare-records.py:836`'s `--rename-table` is optional (`contract-rename.json, checked against the two
  path sets`), so its arg help needs a wording fix at most; not exercised.

## Acceptance criteria that are not measurable, with replacements

1. `prd.md:29` / `implement.md:56` — "`check-fixture-drift.py` 读不到输入时 rc=**2**". Not measurable:
   the input that is actually missing today is the table, never `--tree`. Replacement (S1): assert
   rc=2 for a nonexistent `--tree` **and** rc=0 plus `fixture drift: none (both directions empty)` for
   `/tmp/wf-synth` once the tables resolve.
2. `implement.md:76` — "`git ls-files` 与 README 表逐行一致". Impossible (S4). Replacement: every script
   row is accounted for by `git ls-files` or `.gitignore:4-7`, and no row names a deleted or moved script.
3. `implement.md:57-59` — `bash -n` + "人工走一遍路径" for three gitignored scripts. `bash -n` is
   measurable but proves nothing about the paths, and "人工走一遍" is not a criterion. Replacement:
   for each of the four local scripts, record in the commit message the exact `bash -n` result **and**
   the one concrete path fact the fix asserts (e.g. `start-targets.sh:26,29,33` writes
   `ledger-main.jsonl`/`ledger-direct.jsonl`; `deploy-campaign.sh:9` reads nothing under
   `/tmp/wf-bench/deploy/`), since the file itself cannot be reviewed in the repository.
4. `prd.md:29` — `orchestrator.ps1`'s four cited lines. Measurable only by reading; replacement:
   assert the campaign log after the fix contains one `client <label> exit=<code> in <n>s` line per
   `Invoke-Client` call (`orchestrator.ps1:262`), which is the observable the ticket's criterion 1 needs.

## Note on the archive

No archived task directory is edited by the plan: `implement.md:39` explicitly leaves
`10-07-e2e-harness-refactor` references alone, and B3's `git mv` **reads** from
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/` without rewriting it. This matches
the rule; the only caution is that `git mv` out of an archived task is itself a history-directory change
and should be named in the commit message as such.
