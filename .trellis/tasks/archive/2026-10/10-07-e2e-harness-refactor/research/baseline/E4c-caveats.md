# E4-c — the truncation caveat, the zero-denominator boundary tree and the two anchors

Final batch of `.trellis/tasks/10-07-e2e-e4-analyzer` (E4-c = E4-d). This document carries items 1–4 of
the batch: §14.7, `--zero-denominator`, the compensated-sum anchor and the §3 blank line. The deletions
are in `E4c-removal.md`; the registry entry is `research/semantic-fixes/index.jsonl`'s `E4-c`.

Authority: `design-decisions.md` **D21** (semantic comparison), **D21.1**, **D21.2 #1/#2**, **D21.3 #1**,
**D6.4/D6.6** (the reference's two exceptions and its retirement), **D19.3 C/D** (two mechanisms, one leaf
name), **D20.1** (a boundary tree is asserted on the C# side alone), **D20.7** (the `### 14.7` landing
spot and its negative control) and `implement.md`'s E4-c row. `research/baseline/E4b5-semantic.md` §7.3
and §10.6 are the two registration points this batch closes.

**Result: the §14.7 caveat renders in two tables of its own with a negative control that goes red, the
`--zero-denominator` tree is asserted with four pointed guards and three controls, the compensated sum is
pinned by a fact that a naive accumulator fails, byte mode's last pre-existing difference is gone, and the
whole oracle is green on the re-frozen tree (51/51 slices, every batch rc=0).**

## 1. The truncation caveat (§14.7)

### 1.1 What renders, and when

`Findings/LedgerTruncationTotals.cs` reads the counter off both levels of the ledger and keeps two
separate slots per target instance: the TCP echo listener's frames (`tcpSummary/truncatedFrames` and
`targetSummary/tcp/truncatedFrames`) and each DNS listener's short reads (`dnsSummary/truncatedFrames`
plus `targetSummary/dns|dnsAlt/truncatedFrames`), the latter keyed by the port the listener published.
The value is a running total, so the largest one seen wins — the rule `LedgerDecodeTotals` already reads
`undecodable` with.

`Tables/TableLedgerTruncated.cs` renders `### 14.7 Truncated frames (target side, unattributable)`
immediately after §14.6, and only when a counter is `> 0` — the §14.6 condition. Each mechanism is a
table of its own, because the two share one leaf name for two mechanisms (D19.3 C), and the rows are
(pass, ledger), never (pass, run, arm): the count is the target's.

```text
$ sed -n '/^### 14\.7/,/^## 15/p' <out>/tables.md            # --truncated-tcp 3
### 14.7 Truncated frames (target side, unattributable)

A frame the peer's close cut in half, and a DNS message a length prefix read short, are the same kind of …

| pass | ledger | truncated frames |
|---|---|---|
| pass1 | /tmp/wf-synth/ledger-main.jsonl | 3 |
| pass2 | /tmp/wf-synth/ledger-main.jsonl | 3 |
| pass3 | /tmp/wf-synth/ledger-main.jsonl | 3 |

**TCP — a frame cut off by the peer's close.** The echo listener's frame reader ends on a partial frame …

$ sed -n '/^### 14\.7/,/^## 15/p' <out>/tables.md            # --truncated-dns 2
| pass | ledger | dns port | truncated frames |
|---|---|---|---|
| pass1 | /tmp/wf-synth/ledger-main.jsonl | 40053 | 2 |
| pass1 | /tmp/wf-synth/ledger-main.jsonl | 53 | 2 |
| pass2 | /tmp/wf-synth/ledger-main.jsonl | 40053 | 2 |
| pass2 | /tmp/wf-synth/ledger-main.jsonl | 53 | 2 |
| pass3 | /tmp/wf-synth/ledger-main.jsonl | 40053 | 2 |
| pass3 | /tmp/wf-synth/ledger-main.jsonl | 53 | 2 |

**DNS — a length-prefix short read.** A DNS listener reads a two-byte length prefix and then that many …
```

A ledger spanning three passes repeats its lifetime total under each of them rather than dividing it,
which the first paragraph of the subsection says out loud. `--truncated-tcp` prints the TCP half only and
`--truncated-dns` the DNS half only, so each mechanism's condition is observable on its own.

The clean tree prints nothing: `rg -c 'truncated frames' <clean>/tables.md` is rc=1, and every
`tables.md` slice is byte-identical to the frozen golden's — batch 5's one remaining byte difference is
`verdict.json:ledger`'s `types` member order (`E4b5-semantic.md` §7.1), not any table.

### 1.2 The negative control: no arm-level cell consumes it

The assertion is not "the caveat appears" but "**only** the caveat appears". `check-boundary-trees.py`
compares the boundary document with the clean one section by section:

```text
$ python3 benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py --workdir /tmp/e4c/boundary2
analyzer:  …/bin/Release/net10.0/WinForward.E2E.Analysis
tree:      /tmp/wf-synth
clean:     200366 byte(s) of tables.md
  PASS  #14.7/present: the caveat is printed inside §14
  PASS  #14.7/tcp: the TCP table carries the target's own count
  PASS  #14.7/tcp: no arm-level cell consumes the counter
  PASS  #14.7/clean: the clean tree prints no caveat
  PASS  control/truncated-tcp-arm-cell: the guard rejects the mutation
check-boundary-trees.py: --truncated-tcp 3: all 5 guard(s) held
  … the same five for --truncated-dns 2 …
check-boundary-trees.py: --truncated-dns 2: all 5 guard(s) held
  … the seven of --zero-denominator (§2.4) …
```

Measured on both trees: **16 of the 16 non-§14 slices are byte-identical to the clean run's**, §14 is the
clean §14 with the subsection appended (12 added lines for TCP, 15 for DNS), and the values are the
target's own. The control writes `3` (respectively `2`) into a §5 latency cell — an arm-level cell — and
`#14.7/<label>: no arm-level cell consumes the counter` goes red, which is D20.7's required negative
control for "no arm-level cell consumes it".

## 2. The zero-denominator boundary tree (`--zero-denominator`)

### 2.1 The flag, and why a flag was needed

`verification/synthetic/make_tree.py` gained a boolean flag (default off) that writes the three shapes a
**zero denominator** produces, none of which the clean tree can reach:

| shape | where | what the harness means by it |
|---|---|---|
| `pass1/wf-aot-opt`'s `LAT` arm publishes `udp.sent = 0` | §14.2 | the client-side datagram count is exactly zero, so the target's census has no band to be judged with |
| every pass's `wf-fdd-opt` `PERSIST` arm publishes `requests = 0` | §4, §10, `verdict.json` | `responseRate` is the explicit JSON `null` of a zero denominator, so the metric has no per-pass value |
| `pass1`'s `wf-aot-opt` `MIX` arm publishes `classes.udp.lossRate = null`, `pass2`'s drops `classes.udp` altogether | §4, §8, `verdict.json` | a null class field must **not** fall back to the arm-level field; a missing one must |

The reference cannot judge this tree: it exits **rc=1** with

```text
  File "…/analyze.py", line 5037, in table_ledger
    else ("ok (±%s)" % fmt_num(band, 0) if abs(arm["udp_datagrams"] - datagrams) <= band else "MISMATCH (±%s)" % fmt_num(band, 0))
TypeError: '<=' not supported between instances of 'float' and 'NoneType'
```

and writes **neither** file (`ls <out>` is empty) — the crash `E4b5-semantic.md` §7.3 and §10.6
registered. That is what makes this a boundary tree under D20.1: asserted against the C# output alone.

### 2.2 Re-freezing A, and the proof that nothing moved

Adding a flag edits the generator, so `FROZEN.md` §3's rule applied. Re-freeze performed in full:

```text
$ bash benchmarks/WinForward.E2E.Analysis/verification/freeze-tree.sh
wrote synthetic tree to /tmp/wf-synth/raw (ledgers in /tmp/wf-synth)
wrote …/verification/synthetic-tree.tar.gz
723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902  …/synthetic-tree.tar.gz
cb581a9c2c7d132a5fc168c673d9b23a61c12b37ed5042efff5175b37639e44d    raw/environment.json

$ python3 …/analysis/analyze.py --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out && \
  cmp /tmp/wf-synth/out/tables.md  …/verification/golden/py-tables.md && \
  cmp /tmp/wf-synth/out/verdict.json …/verification/golden/py-verdict.json
py-tables.md byte-identical
py-verdict.json byte-identical
```

| artifact | before | after |
|---|---|---|
| `synthetic-tree.tar.gz` | `723b7378…` | **`723b7378…`** (unchanged) |
| `golden/py-tables.md` | `8011d05b…` | **`8011d05b…`** (unchanged) |
| `golden/py-verdict.json` | `2e0e64da…` | **`2e0e64da…`** (unchanged) |
| `synthetic/make_tree.py` | `b3e946a7…` | **`b946a38d…`** (changed: the flag) |

The generator's row is the hash of the batch's final bytes: both `synthetic/make_tree.py` and
`check-boundary-trees.py` were rewritten after this table was drafted, so the values first recorded for
them (`60d96cf8…`, and `8c370853…` in `FROZEN.md` §1 and `E4c-removal.md` §6) were stale. The E4-c
check round re-measured both against the files as they now hash, re-ran every claim below on those
bytes, and corrected the three records; the tarball, both golden files and all five recipes were
already exact.

All five boundary recipes were regenerated from the new generator at `/tmp/wf-synth` and reproduced their
frozen hashes, which is the same statement measured five more ways:

| flag | sha256 | |
|---|---|---|
| `--window-overflow wf-aot-opt` | `d346f89d…` | unchanged |
| `--undecodable 7` | `d3af3e07…` | unchanged |
| `--truncated-tcp 3` | `14ab9a6a…` | unchanged |
| `--truncated-dns 2` | `f55f897d…` | unchanged |
| `--zero-denominator` | `e8326884…` | **new recipe** |

`check-fixture-drift.py` is rc=0 on the zero-denominator tree (603 contract paths + 3 declared-unobserved
shapes, both directions empty): the flag changes values and null-ness, never the key set. The new hashes
are recorded in `FROZEN.md` §1 and §4.

**Re-run from batch 1** (required after a re-freeze, even though nothing moved):

```text
$ for b in 1a 1b 1c 2 3 4 5; do python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic --batch $b; done
rc=0 rc=0 rc=0 rc=0 rc=0 rc=0 rc=0
$ python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic
compared 51 slice(s): 0 differ(ent), 0 missing → rc=0
```

### 2.3 The pointed assertions

`verification/check-boundary-trees.py` generates each boundary tree at `/tmp/wf-synth`, runs the built
analysis over it and judges the produced documents. On `--zero-denominator`:

```text
  PASS  #zero-denominator/empty-cell: a zero-denominator rate is an empty cell
  PASS  #zero-denominator/null-passes: the reason is the harness's own null
  PASS  #zero-denominator/mix-gate: a null class rate does not fall back
  PASS  #zero-denominator/datagram-band: a zero client count has no band
  PASS  control/empty-cell-to-zero: the guard rejects the mutation
  PASS  control/null-pass-to-number: the guard rejects the mutation
  PASS  control/fallback-through-a-null: the guard rejects the mutation
check-boundary-trees.py: --zero-denominator: all 7 guard(s) held
```

What each one reads, and why it is the shape D21.2 #2 asked for:

1. **The empty cell, not a zero.** §4's `wf-fdd-opt` × `PERSIST responseRate` cell is `''`:

   ```text
   $ python3 - <<…  # the cell, the metric's per-row entry
   wf-fdd-opt | PERSIST responseRate = '' | MIX udp.lossRate = '0.0000 [0.0000–0.0416] pp (n=3)'
   persist.responseRate wf-fdd-opt {'passes': 0, 'per_pass': {}, 'null_passes': 3, 'median': None,
                                    'status': 'ok', 'comparable': False}
   ```

   The reference's own rule (`fmt_stat`: "when every pass is null the cell is rendered **empty** rather
   than as a zero") is what the guard pins; a `0` there would be a number about nothing.
2. **`null_passes` and its reason.** All three passes are counted as null passes, `per_pass` is empty,
   `median` is `null`, and each pass's reason is the harness's own
   `null rate: the harness wrote null because the denominator was zero` — the string `ArmAccess`
   carries and the reference's `NULL_RATE_REASON`. The reason is what makes the empty cell actionable
   rather than merely absent.
3. **The MIX fallback's gate, in both directions.** `wf-aot-opt`'s `mix.udp.lossRate` entry reads
   `passes: 2`, `null_passes: 1`, `unavailable_passes: {"pass1": <null-rate reason>}` and
   `per_pass: {"pass2": 0.0417, "pass3": 0.0}`. pass1's class field is a JSON `null`, so the arm-level
   fallback must **not** fire (the pass contributes no value and its reason is the null-rate one, not a
   missing-field one); pass2's `classes.udp` is absent, so the fallback **must** fire, and the guard
   recomputes the arm's own `metrics/udp.lossRate` from `/tmp/wf-synth/raw/pass2/wf-aot-opt/MIX.jsonl`
   and checks the cell equals it ×100 (`0.0004167 × 100 = 0.0417`).
4. **§14.2's zero count.** The row that used to crash the reference now reads a defined `n/a`:

   ```text
   | pass  | run       | arm | ledger TCP connections | client connections | TCP check | ledger datagrams | client datagrams | datagram check | …
   | pass1 | wf-aot-opt | LAT | 33                     | 32                 | ok        | 600.0            | 0                | n/a            | …
   ```

   The client count prints `0` (an integer, as `sum([])` does for an empty census), the ledger's census
   is `600.0`, and the check says `n/a` — no band can be derived from a rate that does not exist.

### 2.4 The negative controls

Each control edits a copy of the produced document and re-runs the guard it belongs to:

| control | the edit | the guard that must reject it | rc |
|---|---|---|---|
| `empty-cell-to-zero` | §4's `wf-fdd-opt` `PERSIST responseRate` cell `''` → `0` | `#zero-denominator/empty-cell` | red |
| `null-pass-to-number` | `persist.responseRate`'s `null_passes` 3 → 2 | `#zero-denominator/null-passes` | red |
| `fallback-through-a-null` | pass1's `unavailable_passes` reason → `MIX metrics.classes.udp.lossRate missing` | `#zero-denominator/mix-gate` | red |

The first is the one the batch's scope names explicitly ("把空 cell 写成 `0` 必须红"); the third is the
gate's other direction, and it fails precisely because a null rate that *did* fall back would have to
report the missing-field reason rather than the null-rate one.

### 2.5 What §7.3 and §10.6 leave behind

`E4b5-semantic.md` §7.3 (the reference's `TypeError`) and §10.6 (the C# answer to it) are closed by
§2.3's fourth guard and §2.1's crash reproduction: the tree is now generated, not hand-mutated, so the
shape is reproducible with one command, and the C# answer (`n/a`) is asserted rather than described.
`E4b5-semantic.md` §10.6's closing nuance — "§7.4 says 'not as a C#-side question', but the C# side does
choose a rendering there" — is **not** taken up as an assertion in this batch: the `dns-clientless`
shape (a ledger-only port) is a tree mutation, not a flag, and it belongs with the mutants `E4b5-semantic.md`
§5/§10.5 already carries. Registered as an observation rather than silently dropped.

## 3. The compensated-sum anchor (D21.3 #1)

`Stats/DescriptiveStats` is now a `public static class` with `Sum` public and every other member still
internal, which is the visibility decision b1b made for `CpRandom`/`VerbatimNumber`/`VerbatimJson`:
the test project drives the member directly and D20.6 keeps `InternalsVisibleTo` out of the assembly.
The class remark says so; no other surface moved and no `.editorconfig` entry was added.

`tests/WinForward.E2E.Tests/AnalyzerStatsAnchorTests.cs` carries one fact:

```csharp
double[] values = [1e16, 1.0, -1e16];
Assert.Equal(1.0, DescriptiveStats.Sum(values));

var naive = 0.0;
foreach (var value in values) { naive += value; }
Assert.Equal(0.0, naive);
```

The second half is what keeps the first from being vacuous: it is the accumulator the anchor exists to
rule out. Verified by mutation — `Sum` rewritten to `total += value`, rebuilt, the fact then fails:

```text
$ dotnet test … --filter FullyQualifiedName~AnalyzerStatsAnchorTests   # with a naive Sum
Expected: 1
Actual:   0
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1
```

and the file was restored byte-for-byte (`sha256 0be0c3fb29337038ec8448e9fc40883554a4775da7a5d4101f0cd7344ffdb5fb`
before and after). Why it matters: §7's `ols_slope` and the identity CPU sum are differences of large
sums, so a plain accumulator moves their last printed digit — and `E4b4-semantic.md` records that a
naive sum passes every **other** gate in this batch (byte and semantic both rc=0), which is exactly why
the value is pinned on its own instead of through a cell. The suite is 1655 → **1656** facts.

## 4. The §3 blank line (D21.2 #1)

`TableGates.RenderBody` had a `string.Empty` between the §3.1 caption and the `### 3.2` heading, and the
reference appends the caption and the next heading back to back. The blank line is gone:

```text
$ python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode byte --batch 2
   tables.md:0: equal (107 line(s))
   tables.md:3: equal (107 line(s))
   verdict.json:findings: equal (142 line(s))
   verdict.json:findings_by_severity: equal (9 line(s))
compared 4 slice(s): 0 differ(ent), 0 missing → rc=0
```

Byte mode over every batch: `1a/1b/1c/2/3/4` rc=0 and `5` rc=1 on the one slice `ledger` at the one
documented difference (the CPython `set` member order of `ledger.passes[*].types`), which is the file's
only remaining byte difference anywhere. Semantic mode is rc=0 for every batch, the full run and
`--tolerance 0`, unchanged by the edit.

**Deviation from D21.2 #1's wording.** The registration says the fix is "去掉 `Caption()` 的前导换行"
and warns that deleting a `string.Empty` "会把空行从表格前挪到段落后". Measured, `Caption()` has no
leading newline at all (a C# raw string literal starts at the first content line): the extra line was the
`string.Empty` element **after** `Caption()` in `RenderBody`'s list. Deleting *that* element removes
exactly one line, at exactly the place the reference does not have one, and the line that separates the
§3.1 table from the caption is untouched — verified by `diff` of the §3 slices being empty, which is what
the batch's criterion asks for. D21.2 #1's *concern* (do not move the blank line out of the table-to-caption
gap) is what the applied fix satisfies; its *diagnosis* of where the line lived was off by one element.

## 5. The oracle and the gates

Every gate on the final tree, in the repository's own order (logs in `/tmp/e4c/gates/`):

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1656 passed, 0 failed, 0 skipped**, 14 assemblies, rc=0 |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **0 bytes of output**, rc=0 |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`, `InspectCode`/`Daemon`/`Transient` caches and `/tmp/JB` cleared | **`<Issue>` 0, `<IssueType>` 0**, no `CSharpErrors` |
| lines | `effective-lines.py` over the four paths | **0 bytes of output**, rc=0 (silent over the 248 files / 28326 effective lines those four paths hold); the analysis project alone is 76 files / 9752 effective lines, largest `TableEnvironment.cs` 321, largest this batch `TableLedgerTruncated.cs` **97** |
| oracle | `oracle-diff.py` | semantic: `1a/1b/1c/2/3/4/5` rc=0 and the full run rc=0 (**51/51 slices**, 0 structure / 0 value / 0 missing); `--tolerance 0` rc=0; byte: `1a/1b/1c/2/3/4` rc=0, `5` rc=1 on the `types` order alone |
| boundary trees | `check-boundary-trees.py` | **17 checks, all green** — 12 guards (4 per tree) and 5 negative controls (§1.2, §2.3, §2.4) |
| fairness | `check-fairness.py` (and `--self-check`) | 14 guards rc=0; 6 controls rc=0 (§`E4c-removal.md` §4) |
| fixture drift | `check-fixture-drift.py --tree /tmp/wf-synth` | rc=0 (603 contract paths + 3 declared shapes) |
| entry point | `bash …/scripts/analyze.sh …` from `/tmp/e4c/fromroot` | rc=0, output byte-identical to the repo-root run, `plots/SKIPPED.md` written |

The first cold inspector round of this batch was started *before* §5.1's split and raced with those edits:
it reported 11 findings (`LedgerDecodeTotals is never used`, `MemberCanBePrivate` on members the section
uses), every one of which is impossible in the final tree and none of which the second cold round
reproduces — the mid-run edit, not the tree. The table's row is the **second** round, run from a cleared
cache on the frozen tree.

The run above `--batch 5`'s byte difference is the accepted CPython `set` artefact
(`E4b5-semantic.md` §7.1/§10.2), and the reorder control still turns it green; the batch's criterion is
semantic mode (D21), which is rc=0 at zero tolerance too.

### 5.1 The split that the line gate forced

`TableLedgerCross.cs` reached **401** effective lines when §14.7 landed in it, one over the limit. §14.7
moved to `Tables/TableLedgerTruncated.cs` (97 effective lines) rather than being squeezed: the split is
the same subsection boundary b5 used for §14 itself, and it is output-inert — `cmp` on both produced
documents before and after the split is empty.

## 6. Deviations and observations

1. **D21.2 #1's diagnosis was off by one element** (§4). The criterion it asks for is met; the change is
   one deleted `string.Empty` after `Caption()`, not a newline removed from inside it.
2. **`TableLedgerCross.cs` had to be split** (§5.1), which the batch plan did not anticipate; it is a pure
   move and left the produced bytes untouched.
3. **The zero-denominator tree's DNS counter is stamped on both listeners.** `stamp_truncated` (E4-a's
   generator) repeats the DNS count in `targetSummary/dns` and `targetSummary/dnsAlt`, so `--truncated-dns 2`
   is a tree in which both listeners short-read two messages. The rendering reports what the ledger says,
   per listener and per port; nothing in the analysis asserts that only one listener can truncate.
4. **`E4b5-semantic.md` §10.6's "§7.4 wants its own assertion" is registered, not implemented** (§2.5):
   that shape needs a tree *mutation* (a ledger-only DNS port), not a flag, so it stays with the mutant
   evidence rather than growing this batch's surface.
5. **`--zero-denominator` is one flag for three shapes.** Splitting it into three flags would have meant
   three more recipes and three more hashes to keep; one flag keeps `FROZEN.md` §4's table a check rather
   than a wall of hashes. If a future batch needs one shape without the others, the injections are three
   independent keys in `make_tree.py` and splitting is mechanical.

## 7. For check

1. **The re-freeze changed no compared byte** (§2.2). Re-run `freeze-tree.sh` and the golden regeneration
   yourself: the tarball must come back `723b7378…` and both golden files must be byte-identical. If they
   are not, the generator's default path moved and the whole oracle has to be re-established from batch 1.
2. **The boundary assertions are the negative controls' other half** (§1.2, §2.4). Confirm
   `check-boundary-trees.py` reports 17 checks and that deleting the `mutate` call in any control turns
   that control red — a control that cannot fail is not evidence.
3. **`DescriptiveStats`'s new visibility is the minimum**: the class is public, `Sum` is public, the other
   six members are internal. Confirm no inspection fires on the internal members of a public type and that
   no `InternalsVisibleTo` appeared anywhere (D20.6).
4. **§14.7 rows are (pass, ledger), not (pass, row, arm)** — the batch's "不摊到臂" requirement. The guard
   proves it by comparing 16 sections byte for byte, not by grepping for a column name; check that the
   comparison is over all sixteen and not over a subset.
5. **`--zero-denominator`'s three shapes are independent injections** in `make_tree.py`
   (`zero_udp_sent`, `zero_requests`, `null_udp_class_rate`/`drop_udp_class`); if a future change makes one
   of them vacuous, the corresponding guard in `check-boundary-trees.py` stops being a statement about the
   analysis and the flag's docstring has to say so.
