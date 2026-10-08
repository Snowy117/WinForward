# E4-c — retiring the Python analyzer

Items 5 and 6 of E4-c in `.trellis/tasks/10-07-e2e-e4-analyzer`: the deletion list, what replaced the
deleted program, the remaining `rg analyze.py` references with the reason each one stays, and the frozen
artifacts that are deliberately kept. The caveats, the boundary tree and the anchors are in
`E4c-caveats.md`.

Authority: `design-decisions.md` **D6.6** (the reference is deleted once the oracle passes, and depends on
git history thereafter), **D20.4** (the fairness rules are externalized and the checker asserts on the C#
product alone), **D20.7** (README ownership, the pyc, the old verification outputs) and `implement.md`'s
E4-c row ("删除 `analyze.py`/旧 `check-fairness.py` + `git rm --cached` 那枚 `.pyc` + README 指向
`analyze.sh`"). B-4 is the same decision seen from the batch table: E4-c and E4-d are one batch.

## 1. Pre-deletion confirmation

The deletion is authorized by D6.6 ("oracle 全 5 批通过后才删") and by `implement.md`'s batch table. The
evidence was on disk **before** the deletion, in the parent task's baseline directory
(`.trellis/tasks/10-07-e2e-harness-refactor/research/baseline/`):

| batch | file | what it establishes |
|---|---|---|
| E4-a | `E4a-oracle.md` | the mechanism: slicing, three exit codes, the skeleton reports rc=2, a changed digit rc=1 |
| E4-a2 | `E4a2-semantic-mode.md`, `E4a2-check.md` | semantic comparison replaces byte equality (D21), with a 43-case self-check |
| E4-b1a | `E4b1a-oracle.md`, `E4b1a-check.md` | `--batch 1a` rc=0 (7 slices) |
| E4-b1b | `E4b1b-oracle.md`, `E4b1b-check.md` | `--batch 1b` rc=0 (2 slices) |
| E4-b1c + b2 | `E4b1c2-semantic.md` | `--batch 1c` rc=0 (3) and `--batch 2` rc=0 (4) |
| E4-b3 | `E4b3-semantic.md`, `E4b3-check.md` | `--batch 3` rc=0 (18) |
| E4-b4 | `E4b4-semantic.md` | `--batch 4` rc=0 (11) |
| E4-b5 | `E4b5-semantic.md` | `--batch 5` rc=0 (6) and **the full run rc=0, 51/51 slices** (§2.2, §10.1) |

Every one of those runs compared the C# product against **`analyze.py`'s own output**, so the reference
had already judged the port before it was deleted. This batch re-ran the same criterion once more on the
re-frozen tree before removing anything (`E4c-caveats.md` §2.2, §5).

## 2. The deletion list

```text
$ git status --short benchmarks/results/
R  benchmarks/results/2026-10-06-e2e-competitors/analysis/README.md -> benchmarks/WinForward.E2E.Analysis/README.md
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/__pycache__/analyze.cpython-314.pyc
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/synthetic/README.md
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/synthetic/make_tree.py
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/verification/selftest-tables.md
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/verification/selftest-verdict.json
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/verification/synthetic-tables.md
D  benchmarks/results/2026-10-06-e2e-competitors/analysis/verification/synthetic-verdict.json
```

| path | size at HEAD | why it goes |
|---|---|---|
| `analysis/analyze.py` | 6165 lines / 271 503 B | the program the port replaces (D6.6); recoverable from git history (§3.1) |
| `analysis/__pycache__/analyze.cpython-314.pyc` | 326 533 B | a build artefact of that program, tracked by accident; `git rm --cached` plus the file itself, per D20.7/C12 |
| `analysis/verification/synthetic-tables.md`, `synthetic-verdict.json` | 203 743 B + 470 065 B | outputs of the *pre-E3* fixture that no recipe regenerates — D20.7's "旧的 `verification/synthetic-*` 退役" |
| `analysis/verification/selftest-tables.md`, `selftest-verdict.json` | 41 105 B + 27 169 B | the same for a one-off self-test run: no tree, no hash, no recipe — D20.7's "不可复现的 `selftest-*`" |
| `analysis/synthetic/make_tree.py`, `synthetic/README.md` | 1269 lines + 16 lines | the generator's pre-move copy. D6.3 **moved** it to `verification/synthetic/`; this one still writes the pre-E3 key set (`completionRate`, `udpReceivers`, `acceptErrors`, `truncatedFrames` are all absent) and its README instructs the reader to run the deleted program, so keeping it would leave a second, wrong generator beside the frozen one |
| `analysis/` itself (the now-empty directories) | — | git does not track empty directories; `benchmarks/results/2026-10-06-e2e-competitors/` disappears with its last file, and `benchmarks/results/` keeps its twenty campaign directories, which are data rather than tooling |

The one file that **moved** instead of going is `analysis/README.md` (22 521 B, 357 lines): it is the
analysis's own document and D20.7/A2-6 gives it to this batch, so it now lives where the analysis does —
`benchmarks/WinForward.E2E.Analysis/README.md` — and `E5-readme-rows.md` §3's two edits were applied to it
(the `#19`-related gate paragraph at its `:164` and the `measurement-caveat` example row at its `:180`).
Because the program it documents changed, the mechanical references were retargeted in the same move
(§4.2) and the harness README's three links to it were repointed.

## 3. The old `check-fairness.py`

D20.4 says the fairness assertions move to an external rule source and a checker that reads **C# output
only**, and that the old script retires with `analyze.py` ("不留 exit 2 的死脚本"). The path survives —
D20.4 names `scripts/check-fairness.py` as the new script's home — but its contents do not: the old file
imported `analyze.py` (`load_analysis`, `resolve_analysis_dir`), generated fixtures with the **old**
`analysis/synthetic/make_tree.py` and ran `python3 analyze.py`. All of that is gone.

`benchmarks/WinForward.E2E/scripts/check-fairness.py` is a rewrite of 554 lines into 566:

| old | new |
|---|---|
| `import analyze` and read `ROW_PROFILES`, `UDP_INCAPABLE_ROWS`, `NOT_CARRIED_CELL`, `UDP53_LABEL` off the module | read `benchmarks/WinForward.E2E.Analysis/verification/row-profiles.json` (`--rules` overrides it) |
| ran `analyze.py` on a fixture tree it generated with the old generator | runs the built `WinForward.E2E.Analysis` binary on the frozen `verification/synthetic/make_tree.py` tree |
| `--analysis-dir`, `--undecodable` | `--rules`, `--tree-dir` |
| `--tables PATH` for mutation controls | kept, unchanged in meaning |
| four guards (#17/#18/#19/#11) | the same four, plus `#17/designed-rows`: **an empty `designed_rows` is a FAIL, not a NOTE** (D20.4) |
| controls run by hand | `--self-check`: six mutations, one per guard, each of which the guard must reject |

The rule source is duplicated in the analysis's own `Model/RowProfiles.cs` by design (D20.4): the JSON is
the *expected* answer and the produced tables the *actual*, so a drifting rule and a drifting renderer
disagree loudly instead of quietly. `row-profiles.json`'s own `note` says so.

Verified:
```text
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --workdir /tmp/e4c/fairness2
check-fairness.py: rules …/verification/row-profiles.json (9 row(s))
  … 14 guards …
check-fairness.py: the frozen tree, analyzed by the C# analysis: all 14 guard(s) held     # rc=0
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --self-check --workdir /tmp/e4c/fairness2
  PASS  control/not-carried-marker-to-number: #17/profile rejects the mutation
  PASS  control/not-carried-udp-cell-to-zero: #17/section 4 rejects the mutation
  PASS  control/udp53-carriage-label-shortened: #18/section 9 rejects the mutation
  PASS  control/cpu-scope-marker-dropped: #19/scope rejects the mutation
  PASS  control/undecodable-moved-out-of-14: #11/arms rejects the mutation
  PASS  control/designed-rows-renamed: #17/designed-rows rejects the mutation
check-fairness.py: …: all 7 guard(s) held                                                         # rc=0
```

The `#11` guard is also the carrier for the `--undecodable 7` boundary tree (`check-fairness.py --tables`
on its `tables.md`), which is how E3's undecodable evidence survives without `analyze.py`
(`FROZEN.md` §4's table names it).

## 4. What replaces the deleted program

| question the old script answered | the artifact that answers it now |
|---|---|
| "what does the analysis produce?" | `WinForward.E2E.Analysis` itself, run through `scripts/analyze.sh` — the only entry point |
| "which output is correct?" | `verification/golden/py-tables.md` and `py-verdict.json`: the reference's answers, frozen with their sha256 (`FROZEN.md` §1) |
| "is this digit right?" | `golden/cp-random-vectors.json`, `py-number-vectors.json`, `py-json-vectors.json` — CPython's own numbers, replayed by `Analyzer*GoldenTests` (D20.5) |
| "was the tree ever right?" | `verification/synthetic/make_tree.py` + `check-fixture-drift.py` (603 contract paths, both directions) |
| "does the report disclose what it must?" | `check-fairness.py` + `verification/row-profiles.json` (§3) |
| "what happens where the reference had no answer?" | `verification/check-boundary-trees.py`: the `--truncated-*` and `--zero-denominator` trees, 12 guards and 5 controls |
| "how do I run any of it again?" | `scripts/analyze.sh`, `FROZEN.md` §2/§2.1/§4, and git history for the reference itself (§3.1) |

### 4.1 Restoring the reference

```bash
git log --diff-filter=D -- benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py   # the E4-c commit
git show <that commit>^:benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py > /tmp/retired-reference/analyze.py
```

The same recipe is in `FROZEN.md` §2.1, next to the golden it would have to reproduce.

### 4.2 The documents the deletion touched

| file | change |
|---|---|
| `analysis/README.md` → `benchmarks/WinForward.E2E.Analysis/README.md` | moved; §3's two edits; the "Running it" block retargeted to `scripts/analyze.sh`; the `Plots` and `Verification` sections corrected (no PNG is written, `plots/SKIPPED.md` is) |
| `benchmarks/WinForward.E2E/README.md` | three links to the moved README repointed, and the "Running the analysis" commands retargeted to `scripts/analyze.sh` |
| `benchmarks/WinForward.E2E.Analysis/Cli/*`, `Program.cs` | the CLI's own prefixes and usage line said `analyze.py:`; they now say `e2e-analysis:`, the neutral program name the report already publishes as `generated_by` |
| `tests/WinForward.E2E.Tests/LogHistogramTests.cs` | one comment named `analyze.py` as the reader of the histogram's published fields; it now names the analysis |
| `verification/FROZEN.md` | §1 hashes refreshed (the generator's own moved in E4-c), §1.1 split out, §2.1 added (restoring the reference), §3 rewritten for a world without it, §4's table extended to five recipes and given the assertion that covers each one |

## 5. Remaining `rg analyze.py` references

```text
$ rg -n 'analyze\.py' --glob '!.git' --glob '!*.pyc' --glob '!.trellis/**' .
benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py:17
benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py:48
benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py:25
benchmarks/WinForward.E2E.Analysis/verification/FROZEN.md:8,83,93,94,100,101,102
```

| reference | why it stays |
|---|---|
| `make_cp_vectors.py:17` — "This script imports nothing from `analyze.py`" | a **frozen** artifact (`FROZEN.md` §1.1); the sentence states the vectors stand on the standard library alone, which stays true and stays useful. Editing it would move a frozen hash for prose. Listed here as history, not as a live reference |
| `make_tree.py:48` — "the retired reference raises `TypeError` on (`analyze.py:5037`)" | the shape's provenance: the line number is why `udp.sent = 0` is a boundary shape at all. It says "retired" |
| `check-boundary-trees.py:25` — the same crash, in the docstring that explains why the reference is not invoked | same reason; the guard exists because the reference had no answer |
| `FROZEN.md` §2.1 and §3 | the frozen set's own documentation: it has to name the file it is the frozen answer of, and how to get it back |

Two references are **not** in the tree but are left for their owners:

- `benchmarks/WinForward.E2E/README.md` — clean (`rg analyze.py` is rc=1); its only remaining mention of
  the old layout is gone.
- `.trellis/spec/backend/measurement-harness.md:210` — "`sum(outcomes) == connectAttempts == scheduledAttempts`
  is a live analyzer identity (`analyze.py reliability_invariants`)". The identity is live (the C# port
  implements it); the parenthetical names a deleted file. **Registered for E5** rather than edited here:
  `.trellis/spec/` is not this batch's surface, and the fix is a one-word swap.
- `.trellis/tasks/**` — the task's own research and the parent's design decisions quote the file by
  necessity; they are the historical record this batch's evidence cites.

## 6. What is deliberately kept frozen

| artifact | sha256 | why it is not deleted |
|---|---|---|
| `verification/synthetic-tree.tar.gz` | `723b7378…` | the one tree the oracle is defined on |
| `verification/golden/py-tables.md` | `8011d05b…` | the reference's answers, the "A" of the oracle |
| `verification/golden/py-verdict.json` | `2e0e64da…` | the same for the machine-readable document |
| `verification/golden/cp-random-vectors.json` | `b011711e…` | CPython's own draws (D20.5) |
| `verification/golden/py-number-vectors.json` | `3d256f59…` | CPython's own `%.*f`/`%.*g`/`repr` |
| `verification/golden/py-json-vectors.json` | `759c0834…` | CPython's own `json.dumps` escapes |
| `verification/synthetic/make_tree.py` | `b946a38d…` | the generator; explicitly outside V3's deletion authorization (D6.3) |
| `verification/synthetic/make_cp_vectors.py` | `3df6aea6…` | the vectors' generator; regenerating must reproduce their hashes |
| `verification/plots-SKIPPED.md` | `39795a79…` | the fixed text `plots/SKIPPED.md` is compared against |
| `verification/check-fixture-drift.py` | `acf41781…` | the key-set guard between generator and contract |
| `verification/check-boundary-trees.py` | `ac83aa2a…` | this batch's boundary-tree assertions |
| `verification/row-profiles.json` | `f426b945…` | the fairness checker's rule source |
| `verification/freeze-tree.sh`, `FROZEN.md` | (docs) | the recipe and the record |
| `scripts/analyze.sh` | `4e1cbb13…` | the analysis's only entry point |

`git status --short benchmarks/WinForward.E2E.Analysis/verification` after the batch lists only
`synthetic/make_tree.py` (modified, re-frozen above), `row-profiles.json` and `check-boundary-trees.py`
(both new): the tarball, both golden documents and the three vector tables are untouched, which is the
machine statement of §1's table.

Two rows of that table were stale when the E4-c check round hashed the tree: `make_tree.py` and
`check-boundary-trees.py` were both rewritten after the table was drafted, so the values it carried for
them (`60d96cf8…`, `8c370853…`) did not match the files. The round re-measured both, re-ran every claim
that depends on them on the bytes as they now hash, and corrected the records here, in `FROZEN.md` §1
and in `E4c-caveats.md` §2.2. Every other row of §1's table was already exact.

## 7. Deviations and observations

1. **The old `check-fairness.py` was rewritten at its own path, not deleted outright.** D20.4 gives the
   new checker that path, and D6.6/D20.9 retire the old behaviour with `analyze.py`. A `git rm` followed
   by a new file at the same path is what a rename looks like to git; the report states the substance
   (no more `import analyze`, no more fixture generation, no more `analyze.py` invocation) rather than
   the plumbing.
2. **`analysis/synthetic/` went with the four registered outputs.** D20.7 names
   `verification/synthetic-*` and `selftest-*`; the pre-move generator's copy and its README are the same
   class of artefact (superseded, unreproducible, and pointing at the deleted program) and leaving them
   would leave a wrong generator next to the frozen one. Recorded here because it is one step beyond the
   letter of the registration.
3. **The moved README needed more than §3's two lines.** §3's edits are the two it lists; on top of them
   the moved document had to stop instructing its reader to run a deleted program (§4.2). The prose is
   otherwise the reference's, and E5 still owns the campaign-level documentation sweep.
4. **The analyzer's own stdout prefix changed** (`analyze.py:` → `e2e-analysis:`). stdout is explicitly
   not part of the oracle's contract (D20.7/A2-9), no test asserts it, and leaving a deleted program's
   name on every line of a live tool's output would make `rg analyze.py` a false positive forever.
5. **`.trellis/spec/backend/measurement-harness.md:210` is registered, not fixed** (§5), and
   `make_cp_vectors.py`'s docstring is left frozen on purpose (§5). Both are named so a later `rg` finds a
   decision rather than an oversight.

## 8. For check

1. **Confirm the deletion is *staged*, not committed** — this batch must not create a commit. `git status`
   shows `D` (index) for the eight paths and `R` for the README.
2. **Re-run the pre-deletion criterion from the frozen artifacts alone** (no reference): `oracle-diff.py
   --mode semantic` must be rc=0, 51/51. Nothing in it needs `analyze.py`.
3. **Re-run both checkers and confirm the counts**: `check-fairness.py` 14 guards, `--self-check` 6
   controls; `check-boundary-trees.py` 17 checks. Then break one rule in `row-profiles.json` (e.g. flip
   `proxifier`'s `udp` to `proxied-native`) and confirm `check-fairness.py` goes red — that is the proof
   the JSON is the rule source and not decoration.
4. **Check that no live file names the deleted program** (§5): the four surviving references must be the
   four listed, and each must be documentation or a frozen artifact.
5. **`scripts/analyze.sh` is the only entry point**: confirm nothing else in the tree invokes a Python
   analyzer (`rg -n 'python3 .*analy' --glob '!.trellis/**'`), and that the wrapper still runs from a
   foreign CWD with identical output.
