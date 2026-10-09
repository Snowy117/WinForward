# C3 notes — durable findings from the scripts triage

Facts a later task should not have to re-derive. Every one was measured on this tree, not read.

## Moves did not need path-code changes, and that is now proven

`benchmarks/X/Y/file.py` and `benchmarks/X/Y'/file.py` are the same depth, so
`Path(__file__).resolve().parents[3]` in `oracle-diff.py` is the repository root **before and after**
the move to `verification/`. `check-fairness.py` never used a depth at all: its `repository_root()`
walks up looking for `benchmarks/WinForward.E2E`. Running both from the new home passes unchanged
(`oracle-diff.py` 51 slices, 0 differences, rc=0; `check-fairness.py` 14/14 guards, rc=0), which is
the cheap proof that "recompute `parents[N]`" is a no-op here and that editing it to `parents[4]`
would have broken the differ.

The one constant the move *did* touch is `check-fixture-drift.py`'s `SCRIPTS`
(`benchmarks/WinForward.E2E/scripts`): it survives because it is what puts `jsonl_paths.py` on
`sys.path` for the `import jsonl_paths` at the top of the file. Deleting it as "unused after the
tables moved" breaks the import. The contract tables moved to `verification/`; `jsonl_paths.py`
stayed with the harness it describes.

## `check-fixture-drift.py` had two invisible failure modes

Both its authorities were read with a bare `Path.read_text()` and both errors left the process as
exit **1** — the code this project reserves for "the content differs". A checker that reports an
unreadable input as drift is the defect `oracle-diff.py:74-75` names. It now exits **2** for: a
missing table, a `--tree` that is not a directory, a `--tree` with no records, and a rename table
naming baseline paths the inventory lacks (that last one is an inconsistency *between* the two
authorities — the contract is undecidable, not drifted). Nothing reads these tables' `generatedBy`
fields, which is why their stale generator path could be reworded safely.

## The frozen set is hash-verified, so touching a listed file costs a re-measure

`FROZEN.md` §1 lists sha256 per artifact, so three rows had to be re-measured even though no
behaviour moved: `synthetic/make_tree.py` (docstring only), `check-fixture-drift.py` (exit-code path)
and `row-profiles.json` (a path in its own note). Regenerating the tree with
`bash verification/freeze-tree.sh` from the edited generator reproduced
`723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902` **byte for byte** (`git status`
clean on `synthetic-tree.tar.gz`), so the golden still stands and no re-freeze was owed. The two
contract tables are *not* in that hash table, so their content edits need no re-measure.

## `orchestrator.ps1`'s swallowed log line: the root cause, not the symptom

`Write-Log` writes to the success stream, and `Invoke-Client` both logged and returned a value on that
same stream. Both call sites discarded it with `[void](…)`, so the `client … exit=…` line went with
it. The ticket's `Write-Host` fix works (measured: `Write-Host` does reach the launcher's
`-RedirectStandardOutput C:\wfbench\orch.log` under `pwsh -File`), but the smaller fix is to stop
`Invoke-Client` publishing a return value at all — `Test-ClientRun` already records the verdict in
`$script:Failures`, so nothing needs it back. A reproduction of the stream shape now emits exactly one
`client <label> exit=<code> in <n>s` line per call and no stray booleans.

## Things this task deliberately did not do

- **`start-targets.sh:14` ignores `$WF_PUB`** (`publish.sh:8` honours it). Audit §2.8, outside this
  task's AC list; not edited.
- **`wf.sh:47-61` T5** (`repl_cmd`'s "last line is a prompt" test) and `start-targets.sh` are
  unrunnable-from-here machine glue with no AC in this task.
- **`orchestrator.ps1`'s `-Pass` parameters** (`:343`, `:378` after the T3 fix) are used only to
  build the client label; `research/03-scripts-audit.md:281` calls `Invoke-ControlBlock`'s binding
  never used, which is wrong — it feeds `'-p' + $Pass` at `:385`. Cosmetic either way.
- **`compare-records.py` / `jsonl_paths.py` survival** is `research/03-scripts-audit.md` §7's open
  question, owned by the parent task.
- **`selftest.sh`'s Python heredoc** (§4.4) — a follow-up, not a repair.
- **Nothing was committed.** `publish-campaign.sh`, `deploy-campaign.sh`, `start-targets.sh` and
  `wf.sh` are not in `git ls-files`; the two fixed in this task are local-only by construction.

## Check round (2026-10-09, re-measured on `0d1edda`)

- **All eight `FROZEN.md` §1 sha256 rows match the files on disk**, not just the three C3
  re-measured: `make_tree.py` `56a557f7…`, `check-fixture-drift.py` `598e7446…`, `row-profiles.json`
  `f4000209…`, tarball `723b7378…` (plus the five untouched rows). The tarball was not regenerated
  here — the recorded hash is the observable.
- **The four repaired readers behave as claimed**, run one at a time: `check-readme-contract.py`
  rc=0 with `111 key(s) checked against 401 declared constant path(s): ok`; `check-fixture-drift.py`
  rc=2 on a non-directory/short tree, a missing table, an empty tree and an unknown baseline path
  (all four, no traceback), rc=0 `fixture drift: none (both directions empty)` on `/tmp/wf-synth`;
  `oracle-diff.py` full batch 51 slices, 0 differences, rc=0; `check-fairness.py` 14/14 rc=0.
- **The `scripts/__pycache__` tree returns on the first checker run.** `check-fixture-drift.py`
  imports `jsonl_paths` from `benchmarks/WinForward.E2E/scripts/`, so CPython rewrites
  `scripts/__pycache__/jsonl_paths.cpython-314.pyc`; the other six files of the old tree stay gone and
  `verification/__pycache__` stays gone. Deleting it is one-shot by nature and `.gitignore:28` keeps it
  invisible to git. AC2's "deleted" is a state, not a property of the commit.
- **`deploy-campaign.sh` stages the ProxiFyre config to `C:\wfbench\stage\…`, which its own layout
  loop (`:30`) never creates** — pre-existing (the `/tmp/wf-bench/deploy-campaign.sh` copy from
  2026-10-06 has the same destination and the same absent directory), untouched by C3, and only
  observable against the VM. A first `stage()` call would report `remote absent`.
- **`measurement-tooling.md`'s `check-boundary-trees.py` row overstates it**: the script builds the
  truncated-TCP, truncated-DNS and zero-denominator trees; `--window-overflow` is asserted by no live
  checker and `--undecodable` belongs to `check-fairness.py --tables` (`FROZEN.md` §4 owns this
  split). Pre-existing row, not introduced by C3; left for the docs task.
- **`benchmarks/WinForward.E2E/README.md:85` says "none of these four" under a five-row table**
  (`AGENTS.local.md` + the four scripts) and `:95` says "Nothing in this document depends on those
  files" while `:209`/`:298` name `start-targets.sh`/`deploy-campaign.sh` as the machine-specific
  launchers. Both sentences are pre-existing prose; read as "the four scripts" they are defensible,
  so they were not rewritten.
- **`10-06-e2e-competitor-benchmark/prd.md:118` was reworded by this check round**: it had said "the
  Python `analyze.py` there replaced the original script", but no `analyze.py` exists anywhere
  (`rg -uuu --files -g 'analyze.py'` → nothing) — the C# analysis replaced it. `design.md:17`'s
  architecture sketch still draws `analysis/analyze.py`; that whole diagram (ports 30010/30053,
  192.168.77.x) is the original design, so it was left as the design-as-written.
