# Frozen oracle artifacts

The C# analysis is judged by **two-implementation oracle**: the same synthetic campaign tree is
analyzed twice — once by the frozen Python reference, once by `WinForward.E2E.Analysis` — and the
two full outputs are compared slice by slice with `benchmarks/WinForward.E2E/scripts/oracle-diff.py`.
This directory holds the frozen side of that comparison, so the reference keeps working after
`analyze.py` is retired (E4-d) and so both runs start from the same bytes.

Everything below is *frozen*: changing it changes what the C# implementation is judged against.

## 1. The frozen set

| Artifact | What it is | sha256 |
|---|---|---|
| `synthetic-tree.tar.gz` | the campaign tree both implementations read; its root is the contents of `/tmp/wf-synth` (`raw/`, `ledger-main.jsonl`, `ledger-direct.jsonl`, `plan-1..3.json`) | `723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902` |
| `golden/py-tables.md` | the Python reference's `tables.md` on that tree (1245 lines) | `8011d05bdad5dd0b2232a1f9e0bd269a637ad7995299d351dae71a945104ebbc` |
| `golden/py-verdict.json` | the Python reference's `verdict.json` on that tree (14125 lines) | `2e0e64dada67524502cde8958dafdf6f90b1e54400015dacd89c921d8483c55d` |
| `plots-SKIPPED.md` | the fixed text the C# analysis writes to `<out>/plots/SKIPPED.md`, unconditionally | `39795a79df7ed8f33a875618553654153e3b1b85009161832052bf28d3144ad4` |
| `synthetic/make_tree.py` | the generator of the tree | `b3e946a7419f6a09622fac2b6a6915c7c563dcbec34cb35d4379d065c06482a1` |
| `check-fixture-drift.py` | the key-set guard between the generator and the harness contract | `acf4178105f911f7a7e3ef81611bf0dff069ae812ed5718f62086fd06f0dec2b` |

Frozen on 2026-10-08 (E4-a). The tarball is built with sorted names, a fixed mtime
(`2026-10-01 00:00:00 UTC`) and zeroed ownership, so regenerating it from the same generator is
byte-identical; `bash freeze-tree.sh` does exactly that and prints the new hash.

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
what make that auditable after the reference retires (E4-d). Regenerating them rewrites all three
files in place and must leave the hashes above unchanged:

```bash
python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py
```

## 2. How the frozen set was produced

```bash
# 1. the tree, at the one path both implementations are called with
rm -rf /tmp/wf-synth && mkdir -p /tmp/wf-synth
python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py /tmp/wf-synth/raw

# 2. the golden, from the Python reference on exactly that tree
rm -rf /tmp/wf-synth/out
python3 benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py \
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

The reference implementation is the one commit of `analyze.py` whose only changes are the five field
spellings of `contract-rename.json` plus the two name neutralizations; the complete, line-by-line
list is in `.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md`. Both
implementations are called with the hardcoded absolute path `/tmp/wf-synth/raw`, which is why the
tree must be extracted there and not somewhere else: the raw path, the two ledger paths and §2's
path columns are part of the compared bytes.

## 3. Re-freezing

**A change to `synthetic/make_tree.py` invalidates the frozen golden.** The tree's key set and the
values behind every rendered cell both move, so there is no partial re-freeze: after any edit
here, regenerate the tree and the golden with §2 and record the new hashes in §1.

The same applies to the reference: `analyze.py` may only change in the two ways
`python-oracle-changes.md` lists. A further change is a change to the oracle itself — it needs its
own review and then a re-freeze, in the same commit.

After a re-freeze, the C# side is re-diffed from **batch 1**: every slice's golden text may have
moved, so every batch's earlier pass has to be re-established.

## 4. Boundary trees

The three counter flags move values, never the key set, so the default tree stays the clean campaign
the two implementations are diffed on. A tree built with a counter flag is a **boundary tree**: the
Python reference does not disclose window overflows in the latency cells, truncated frames or
undecodable datagrams beyond what it already renders, so boundary trees are asserted against the
**C# output alone** (a pointed assertion per counter, with a negative control) and never take part
in the two-implementation diff (D20.1).

Each boundary tree has one documented recipe. Regenerating it at `/tmp/wf-synth` and re-tarring it
with the §2 command must reproduce the hash; `--truncated-tcp 3` was regenerated after the freeze and
reproduced `14ab9a6a…`, so the table is a check rather than a record. The path is part of the recipe,
not a detail: every `run.json` embeds its own `outDirectory`, so a tree generated in any other
directory hashes differently (measured: all four recipes reproduce only under `/tmp/wf-synth`).

| Flag | sha256 of the tree tarball |
|---|---|
| `--window-overflow wf-aot-opt` | `d346f89d238102ea13108aae12cab679aeb3949638b179c71f4f6a905a155c19` |
| `--undecodable 7` | `d3af3e07f934f7d13a6beb19aeebb4d9dbea23bd2a7b91418f3a507437f01039` |
| `--truncated-tcp 3` | `14ab9a6a25bc13656ad744e19054444b3f59baecc1ed13a757dcbf1d69402968` |
| `--truncated-dns 2` | `f55f897dc801950e8854bbc41ce940c2e0ccadecaa9b94722996adbfb60d3ed9` |

## 5. What the clean tree does not exercise

The fixture promises the harness's **key set and the three states a key can be in** (present with a
value, present and JSON null, absent because the kind does not publish it). The clean tree exercises
the first and the third everywhere, and the second only on the container level (`verdict.json`
carries JSON null for a cell with no values at all).

A *reading* the harness wrote as JSON `null` — a rate whose denominator was zero — does not occur in
the clean tree: every arm in it sends something. The rendering rule for that state (an empty cell,
never a zero) therefore cannot be decided by the oracle and is asserted instead by the C# side's own
unit tests, where `ContractShapeTests.AnUnknownReadingIsNullAndNeverAMissingKey` already pins the
record side. The rule itself is preserved verbatim in the reference and listed in
`python-oracle-changes.md` §5.

`plots/` is not compared: `oracle-diff.py` reads `tables.md` and `verdict.json` only. The C# analysis
writes `<out>/plots/SKIPPED.md` from the fixed text in `plots-SKIPPED.md`, and the reference's own
`plots/SKIPPED.md` (which names the interpreter it would need) is deliberately different.
