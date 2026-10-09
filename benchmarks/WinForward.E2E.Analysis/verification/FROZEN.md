# Frozen oracle artifacts

The C# analysis is judged by **two-implementation oracle**: the same synthetic campaign tree is
analyzed twice — once by the frozen Python reference, once by `WinForward.E2E.Analysis` — and the
two full outputs are compared slice by slice with `oracle-diff.py`, beside this file.
This directory holds the frozen side of that comparison, so both runs start from the same bytes.

The **reference is retired**: `analyze.py` was deleted once all five batches passed (E4-c/D6.6), and
the golden files below are what remains of its answers. Everything the oracle compares is frozen
here; the reference itself is recoverable from git history (§2.1).

Everything below is *frozen*: changing it changes what the C# implementation is judged against.

## 1. The frozen set

| Artifact | What it is | sha256 |
|---|---|---|
| `synthetic-tree.tar.gz` | the campaign tree both implementations read; its root is the contents of `/tmp/wf-synth` (`raw/`, `ledger-main.jsonl`, `ledger-direct.jsonl`, `plan-1..3.json`) | `723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902` |
| `golden/py-tables.md` | the Python reference's `tables.md` on that tree (1245 lines) | `8011d05bdad5dd0b2232a1f9e0bd269a637ad7995299d351dae71a945104ebbc` |
| `golden/py-verdict.json` | the Python reference's `verdict.json` on that tree (14125 lines) | `2e0e64dada67524502cde8958dafdf6f90b1e54400015dacd89c921d8483c55d` |
| `plots-SKIPPED.md` | the fixed text the C# analysis writes to `<out>/plots/SKIPPED.md`, unconditionally | `39795a79df7ed8f33a875618553654153e3b1b85009161832052bf28d3144ad4` |
| `synthetic/make_tree.py` | the generator of the tree | `56a557f76af182c4d7088115acc2b0416647859b4174d3e5b22d117ca57f8e81` |
| `check-fixture-drift.py` | the key-set guard between the generator and the harness contract | `598e74460038a875c0e7fe557dcbfbe1596303ceeb665ae1936834a1abf5debc` |
| `check-boundary-trees.py` | the boundary trees' assertions and their negative controls (§4) | `ac83aa2a1372f37b0c474794ce2d86c6448837fc20c70853338a4b2d79ab5ee2` |
| `row-profiles.json` | the declared rows and carriage labels `check-fairness.py` asserts against | `f40002095052887a016e5f944fc2366a43e4ab59c2aaf7296ca2480366b884ce` |

Frozen on 2026-10-08 (E4-a). The tarball is built with sorted names, a fixed mtime
(`2026-10-01 00:00:00 UTC`) and zeroed ownership, so regenerating it from the same generator is
byte-identical; `bash freeze-tree.sh` does exactly that and prints the new hash.

**Re-frozen once, in E4-c** (the `--zero-denominator` flag was added to `synthetic/make_tree.py`):
the generator's own hash moved from `b3e946a7…` to `b946a38d…`, and regenerating the tree and the
golden from it reproduced `723b7378…`, `8011d05b…` and `2e0e64da…` **byte for byte**, so no golden
text moved and no slice had to be re-established. Every batch was re-run from batch 1 anyway
(`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E4c-caveats.md` §2).

The two script hashes above (`synthetic/make_tree.py`, `check-boundary-trees.py`) were re-measured on
the batch's final bytes during the E4-c check round. Both scripts were rewritten after this table was
first drafted, so the values it had carried for them (`60d96cf8…` and `8c370853…`) did not match the
files a checker would hash; everything else in the table was already exact. The functional claims
were re-established on the files as they now hash, which is what §1's rows are for.

**Three hash rows were re-measured after the C3 scripts triage (2026-10-09)**:
`synthetic/make_tree.py` (its docstring promised a per-pass `target-ledger.jsonl` the generator never
writes; it writes the two ledgers beside `raw/`), `check-fixture-drift.py` (its exit-code path for an
unreadable authority, and the directory the two contract tables are read from) and `row-profiles.json`
(the script path in its own note). No generator logic, rule, number or table moved: regenerating the
tree from the edited generator reproduced `723b7378…` byte for byte, so the golden below still stands.
The two contract tables (`contract-inventory.json`, `contract-rename.json`) and `contract-rename.md`
also live in this directory now rather than in an archived task's `research/`.

**The step IDs are provenance labels, not definitions.** `E4-a`, `E4-b1b`, `E4-c`, `E2-d`, `E3-d` and
`D6.6`/`D20.1`/`D21.1` name steps and decisions of the archived task
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/` (its `design-decisions.md` and
`research/baseline/`), which is where every one of them is defined. They are kept here because they
say *which* era a frozen byte or a snapshot tree belongs to; a reader who deletes one cannot tell two
records of the same artifact apart.

### 1.1 The retired vector tables (E4-b1b, retired 2026-10-09)

Three files under `golden/` and their generator used to be frozen beside the golden, taking no part in
the two-implementation diff: `cp-random-vectors.json` (`random.Random(int)` draws, 702 of them),
`py-number-vectors.json` (`%.*f` 230 values, `%.*g` 135, `repr` 36 finite and 3 named) and
`py-json-vectors.json` (`json.dumps(value, indent=2)` for 12 documents), generated by
`synthetic/make_cp_vectors.py`. They were the unit-test side of D20.5 — the port's generator and its
number formatting judged against the numbers CPython produced rather than against a restatement of
them.

They are **gone**, along with the port they judged: task `10-09-e2e-analysis-native-rng-and-format`
replaced the `CpRandom` MT19937 port with `System.Random` and the printf emulation with .NET's own
formatting, so the tables describe an implementation the analysis no longer has. Their hashes were
`b011711e…` (random), `3d256f59…` (number), `759c0834…` (json) and `3df6aea6…` (generator); they are
recoverable from git history at the last commit that carried them, and nothing reads them any more.
What still pins the formatting rules is inline in `AnalyzerNumberGoldenTests` /
`AnalyzerJsonGoldenTests`: the exact binary midpoints of the three-digit grid, the general form's
shape table, the cell fallback, the escape set, the nesting levels and the published float text.

## 2. How the frozen set was produced

```bash
# 1. the tree, at the one path both implementations are called with
rm -rf /tmp/wf-synth && mkdir -p /tmp/wf-synth
python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py /tmp/wf-synth/raw

# 2. the golden, from the Python reference on exactly that tree (see 2.1: the reference is retired)
rm -rf /tmp/wf-synth/out
python3 <the reference, restored per 2.1> \
    --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out
cp /tmp/wf-synth/out/tables.md  benchmarks/WinForward.E2E.Analysis/verification/golden/py-tables.md
cp /tmp/wf-synth/out/verdict.json benchmarks/WinForward.E2E.Analysis/verification/golden/py-verdict.json

# 3. the tarball (also `bash freeze-tree.sh`, which repeats 1 and 3)
cd /tmp/wf-synth && tar --sort=name --mtime='2026-10-01 00:00:00 UTC' \
    --owner=0 --group=0 --numeric-owner --format=gnu \
    -czf <this directory>/synthetic-tree.tar.gz \
    raw ledger-main.jsonl ledger-direct.jsonl plan-*.json

# 4. the guard between the generator and the contract
python3 benchmarks/WinForward.E2E.Analysis/verification/check-fixture-drift.py --tree /tmp/wf-synth
```

The reference implementation was the one commit of `analyze.py` whose only changes were the five
field spellings of `contract-rename.json` plus the two name neutralizations; the complete,
line-by-line list is in
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/python-oracle-changes.md`. Both implementations
are called with the hardcoded absolute path `/tmp/wf-synth/raw`, which is why the tree must be
extracted there and not somewhere else: the raw path, the two ledger paths and §2's path columns are
part of the compared bytes.

### 2.1 Restoring the retired reference

`analyze.py` was deleted in E4-c (D6.6) and lives only in git history, at the last commit that still
carried it (`git log --diff-filter=D -- benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py`
names it). A re-freeze does not need the reference — §1's golden hashes are the frozen answers — but
if the golden ever has to be produced again, restore the file from that commit:

```bash
mkdir -p /tmp/retired-reference
git show <commit>^:benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py \
    > /tmp/retired-reference/analyze.py
python3 /tmp/retired-reference/analyze.py --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out
```

It must be the reference `python-oracle-changes.md` describes, and the run must reproduce the hashes
in §1; a different file is a different oracle.

## 3. Re-freezing

**A change to `synthetic/make_tree.py` invalidates the frozen golden.** The tree's key set and the
values behind every rendered cell both move, so there is no partial re-freeze: after any edit
here, regenerate the tree and the golden with §2 and record the new hashes in §1.

The reference is gone from the tree (§2.1), so a re-freeze now has three possible triggers: a change
to `synthetic/make_tree.py`, a restore of the reference from git history, or a change to
`check-fixture-drift.py`'s contract composition. All three regenerate the golden with §2 and record
the new hashes in §1; a golden produced from a reference other than the one
`python-oracle-changes.md` describes is a different oracle and needs its own review.

After a re-freeze, the C# side is re-diffed from **batch 1**: every slice's golden text may have
moved, so every batch's earlier pass has to be re-established.

## 4. Boundary trees

The counter flags and `--zero-denominator` move values, never the key set, so the default tree stays
the clean campaign the two implementations are diffed on. A tree built with one of them is a
**boundary tree**: the Python reference does not disclose window overflows in the latency cells,
truncated frames or undecodable datagrams beyond what it already renders, it has no answer for a
denominator of exactly zero, and on that last tree it raises `TypeError`
(`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E4b5-semantic.md` §7.3).
Boundary trees are therefore asserted against the **C#
output alone** — a pointed assertion per shape, each with a negative control — and never take part in
the two-implementation diff (D20.1).

Each boundary tree has one documented recipe. Regenerating it at `/tmp/wf-synth` and re-tarring it
with the §2 command must reproduce the hash; all five recipes were regenerated after E4-c's edit to
the generator and reproduced their hashes, so the table is a check rather than a record. The path is
part of the recipe, not a detail: every `run.json` embeds its own `outDirectory`, so a tree generated
in any other directory hashes differently (measured: all five reproduce only under `/tmp/wf-synth`).

| Flag | sha256 of the tree tarball | asserted by |
|---|---|---|
| `--window-overflow wf-aot-opt` | `d346f89d238102ea13108aae12cab679aeb3949638b179c71f4f6a905a155c19` | the §5 latency cells, recorded in `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/baseline/E4b3-semantic.md` §5 — **no live checker asserts this tree** |
| `--undecodable 7` | `d3af3e07f934f7d13a6beb19aeebb4d9dbea23bd2a7b91418f3a507437f01039` | `check-fairness.py --tables` (`#11/arms`) |
| `--truncated-tcp 3` | `14ab9a6a25bc13656ad744e19054444b3f59baecc1ed13a757dcbf1d69402968` | `check-boundary-trees.py` (`#14.7/tcp`) |
| `--truncated-dns 2` | `f55f897dc801950e8854bbc41ce940c2e0ccadecaa9b94722996adbfb60d3ed9` | `check-boundary-trees.py` (`#14.7/dns`) |
| `--zero-denominator` | `e83268847ae24e2b17d2c7d348292c2977d913d9bc568f010dcf9d3e7ea56cb4` | `check-boundary-trees.py` (`#zero-denominator/*`) |

`python3 check-boundary-trees.py` builds the truncated-TCP, truncated-DNS and zero-denominator trees
(rows 3–5) at `/tmp/wf-synth`, runs the built analysis over each and judges the produced documents. It
does **not** build rows 1–2: the `--undecodable` tree is covered by `check-fairness.py`, whose `#11`
guard reads the `undecodable` disclosure of any document it is handed (`--tables`), and the
`--window-overflow` tree has no live consumer — its assertions are the §5 latency cells recorded in
the archived E4-b3 research note. Both live checkers carry their negative controls inside themselves,
so `--self-check`-style red is one command away.

## 5. What the clean tree does not exercise

The fixture promises the harness's **key set and the three states a key can be in** (present with a
value, present and JSON null, absent because the kind does not publish it). The clean tree exercises
the first and the third everywhere, and the second only on the container level (`verdict.json`
carries JSON null for a cell with no values at all).

A *reading* the harness wrote as JSON `null` — a rate whose denominator was zero — does not occur in
the clean tree: every arm in it sends something. The rendering rule for that state (an empty cell,
never a zero) therefore cannot be decided by the oracle; the `--zero-denominator` boundary tree in §4
is what exercises it, and `ContractShapeTests.AnUnknownReadingIsNullAndNeverAMissingKey` pins the
record side. The rule itself was preserved verbatim in the reference and is listed in
`python-oracle-changes.md` §5.

`plots/` is not compared: `oracle-diff.py` reads `tables.md`, `verdict.json` and the fixed
`plots/SKIPPED.md` (D21.1) only. The C# analysis writes `<out>/plots/SKIPPED.md` from the frozen text
in `plots-SKIPPED.md`; the deleted reference wrote its own, which named the interpreter it needed.

## 6. The declared statistical tolerance (2026-10-09)

The semantic differ compares numbers within one unit of the **reference's** last printed digit, which
is the right rule for a published statistic and the wrong one for a resampled one. Six leaves inside a
`metrics/<member>.pairs[i]` entry are therefore compared under a declared tolerance of their own
(`oracle-diff.py`, `STATISTICAL_PATH` / `statistical_tolerance`):

| Leaves | Rule | Why |
|---|---|---|
| `p_value`, `holm_p_value`, `p_equivalence`, `holm_p_equivalence` | absolute `5e-2` | each is a `--resamples`-draw Monte-Carlo estimate of a probability: the direct pair doubles the one-sided count, the equivalence pair takes one edge. Two sequences estimate the same probability with a difference of SD 6.4e-3 and 3.5e-2 at worst on this tree, so a print-precision bound cannot hold. Conservative for the equivalence pair, whose worst measured move is 1.4e-2 |
| `ci95[0]`, `ci95[1]` | `max(1e-2, 1e-2 · abs(expected))` | the edges are on the metric's own comparison scale (a ratio, or a difference in the metric's units, spanning 0.09 to 0.8 here), not on the p-value's |

Measured on this tree with `10-09-e2e-analysis-native-rng-and-format`: with `CpRandom` replaced by
`System.Random`, 75 leaves move (58 `p_value`, 10 `p_equivalence`, 7 `holm_p_equivalence`), no table
cell and no verdict among them; re-running the **same binary** with `--seed 20261006` versus
`20261007` moves 77 leaves with p_value SD 6.4e-3 and worst 3.0e-2, which is what fixes the width. An
`1e-2` p-value bound — the first draft, taken from the field's 1e-4 print granularity — left 27 of the
75 unexplained and is a level the analysis cannot reproduce against itself. `holm_p_value` (212
leaves) and both `ci95` edges move nowhere on this tree: every `holm_p_value` leaf sits at 0 or 1 (41
zeros, 171 ones), and 3 passes leave the 2.5 % quantile of `ci95` on the smallest atom. The path has to
match exactly, so the relaxation cannot reach a table cell, an `estimate`/`median`/`iqr`, a verdict
string or a key set.

