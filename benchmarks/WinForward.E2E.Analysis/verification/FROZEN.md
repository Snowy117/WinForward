# Frozen oracle artifacts

The C# analysis is judged by **two-implementation oracle**: the same synthetic campaign tree is
analyzed twice — once by the frozen Python reference, once by `WinForward.E2E.Analysis` — and the
two full outputs are compared slice by slice with `benchmarks/WinForward.E2E/scripts/oracle-diff.py`.
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
| `synthetic/make_tree.py` | the generator of the tree | `b946a38dd061752f2e23f9da96aac7df9d1f0424e8d7a1dd7780ba11d3474e2d` |
| `check-fixture-drift.py` | the key-set guard between the generator and the harness contract | `acf4178105f911f7a7e3ef81611bf0dff069ae812ed5718f62086fd06f0dec2b` |
| `check-boundary-trees.py` | the boundary trees' assertions and their negative controls (§4) | `ac83aa2a1372f37b0c474794ce2d86c6448837fc20c70853338a4b2d79ab5ee2` |
| `row-profiles.json` | the declared rows and carriage labels `benchmarks/WinForward.E2E/scripts/check-fairness.py` asserts against | `f426b945114151df0a5075c4dc0b535b194705e11acef247e7cfc20e40228b7f` |

Frozen on 2026-10-08 (E4-a). The tarball is built with sorted names, a fixed mtime
(`2026-10-01 00:00:00 UTC`) and zeroed ownership, so regenerating it from the same generator is
byte-identical; `bash freeze-tree.sh` does exactly that and prints the new hash.

**Re-frozen once, in E4-c** (the `--zero-denominator` flag was added to `synthetic/make_tree.py`):
the generator's own hash moved from `b3e946a7…` to `b946a38d…`, and regenerating the tree and the
golden from it reproduced `723b7378…`, `8011d05b…` and `2e0e64da…` **byte for byte**, so no golden
text moved and no slice had to be re-established. Every batch was re-run from batch 1 anyway
(`.trellis/tasks/10-07-e2e-harness-refactor/research/baseline/E4c-caveats.md` §2).

The two script hashes above (`synthetic/make_tree.py`, `check-boundary-trees.py`) were re-measured on
the batch's final bytes during the E4-c check round. Both scripts were rewritten after this table was
first drafted, so the values it had carried for them (`60d96cf8…` and `8c370853…`) did not match the
files a checker would hash; everything else in the table was already exact. The functional claims
were re-established on the files as they now hash, which is what §1's rows are for.

### 1.1 The vector tables beside the golden (E4-b1b)

Three more files under `golden/` are frozen the same way but take **no part in the two-implementation
diff**: `oracle-diff.py` never reads them, and no change to `make_tree.py` invalidates them, because
the tree they describe is CPython's own behaviour rather than a campaign.

| Artifact | What it is | sha256 |
|---|---|---|
| `golden/cp-random-vectors.json` | `random.Random(int)` draws: 18 seeds × 8 raw words, 11 `getrandbits` widths and 28 `randrange` stops (702 draws) | `b011711e290a744827edb9ae7433304f29770619885cd7d4b953a0307bab2690` |
| `golden/py-number-vectors.json` | `%.*f` (230 values), `%.*g` (135) and `repr` (36) as CPython writes them, midpoints included | `3d256f5994adcd1f7ba44e4f63f06ac7e144b8bbbd56eaa2a9903e812ffcae4e` |
| `golden/py-json-vectors.json` | `json.dumps(value, indent=2)` for 12 documents: escapes, non-ASCII, astral pairs, empty and nested containers | `759c08345cb344901d13e799128ab17908ae3d71613b4ff36025f79cdc3bf49d` |
| `synthetic/make_cp_vectors.py` | the generator of all three, standard library only | `3df6aea6a6171167464a0d7bbdf6e8428eee899524142e93b7bb22ea78d52c60` |

They are the unit-test side of D20.5: the analysis's generator and its number formatting are judged
against the numbers CPython produced rather than against a restatement of them, and the vectors are
what make that auditable now that the reference is retired. Regenerating them rewrites all three
files in place and must leave the hashes above unchanged:

```bash
python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py
```

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
`.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md`. Both implementations
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
(`research/baseline/E4b5-semantic.md` §7.3). Boundary trees are therefore asserted against the **C#
output alone** — a pointed assertion per shape, each with a negative control — and never take part in
the two-implementation diff (D20.1).

Each boundary tree has one documented recipe. Regenerating it at `/tmp/wf-synth` and re-tarring it
with the §2 command must reproduce the hash; all five recipes were regenerated after E4-c's edit to
the generator and reproduced their hashes, so the table is a check rather than a record. The path is
part of the recipe, not a detail: every `run.json` embeds its own `outDirectory`, so a tree generated
in any other directory hashes differently (measured: all five reproduce only under `/tmp/wf-synth`).

| Flag | sha256 of the tree tarball | asserted by |
|---|---|---|
| `--window-overflow wf-aot-opt` | `d346f89d238102ea13108aae12cab679aeb3949638b179c71f4f6a905a155c19` | the §5 latency cells (`research/baseline/E4b3-semantic.md` §5) |
| `--undecodable 7` | `d3af3e07f934f7d13a6beb19aeebb4d9dbea23bd2a7b91418f3a507437f01039` | `check-fairness.py --tables` (`#11/arms`) |
| `--truncated-tcp 3` | `14ab9a6a25bc13656ad744e19054444b3f59baecc1ed13a757dcbf1d69402968` | `check-boundary-trees.py` (`#14.7/tcp`) |
| `--truncated-dns 2` | `f55f897dc801950e8854bbc41ce940c2e0ccadecaa9b94722996adbfb60d3ed9` | `check-boundary-trees.py` (`#14.7/dns`) |
| `--zero-denominator` | `e83268847ae24e2b17d2c7d348292c2977d913d9bc568f010dcf9d3e7ea56cb4` | `check-boundary-trees.py` (`#zero-denominator/*`) |

`python3 check-boundary-trees.py` builds each of the first, third, fourth and fifth trees at
`/tmp/wf-synth`, runs the built analysis over it and judges the produced documents; the second is
covered by `scripts/check-fairness.py`, whose `#11` guard reads the `undecodable` disclosure of any
document it is handed (`--tables`). Both carry their negative controls inside themselves, so
`--self-check`-style red is one command away.

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
