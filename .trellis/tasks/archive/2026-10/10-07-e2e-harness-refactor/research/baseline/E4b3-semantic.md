# E4-b3 — §5 latency, §8 udp, §9 dns, and the fifteen metrics

Batch E4-b3 of `.trellis/tasks/10-07-e2e-e4-analyzer`: the three sections the batch table assigns to
§5/§8/§9 plus the fifteen `metrics/*` members `BATCH_SECTIONS` splits off to batch 3. Authority:
`design-decisions.md` **D21** (semantic comparison replaces byte equality; numbers by the reference's
printed precision, key order free, wording required to match), **D21.1** (the tolerance direction),
**D20.2** (slicing and batch ownership), **D20.7/D20.8** (§4 is batch 4, so the batch table's own
`BATCH_SECTIONS` is the criteria source for what this batch owns) and
`.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md` §5 (the rendering quirks
that must survive unchanged).

Its criterion is `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic --batch 3`
⇒ **rc=0**, with `--batch 1a/1b/1c/2` still **rc=0** and `--batch 4/5` still **rc=2**.

**Result: batch 3 rc=0 (18/18 slices), batch 1a/1b/1c/2 rc=0 (7/2/3/4 slices), batch 4/5 rc=2
(6/3 missing slices).** In `--mode byte` the same eighteen slices are **byte-identical** to the frozen
golden. The frozen tree, the frozen golden and the reference `analyze.py` were not touched
(`git status --short benchmarks/WinForward.E2E.Analysis/verification` prints nothing).

## 1. What the batch is, and what it deliberately is not

- **§5 `Latency detail`** — one sub-table per latency class (`tcp-connect`, `tcp-rtt`, `udp-rtt`,
  `dns-rtt`), one line per (row, arm) pair the campaign actually ran, every percentile read straight
  out of the harness's histogram and aggregated across passes. It is the only section that renders the
  deferred-window ceiling (`gates/windowOverflow`) and the only one whose rows may carry **eleven
  cells against a ten-column header**.
- **§8 `UDP accuracy detail`** — the two arms that carry the full classification (`LOSS` whole,
  `MIX`'s `classes.udp`), the fifteen classifier fields, the rule-of-three bound behind each count, and
  the denominator table those bounds are quoted from.
- **§9 `DNS comparability detail`** — the port-53 arm beside the arm on a port no product
  special-cases, the carriage §1 declares for each row, and how much of each arm rode the UDP path.
- **The fifteen metrics** — `lat.*`, `latload.*`, `loss.*`, `dns*`, `dnsalt*`, `thru.goodputMbps` and
  `mix.udp.lossRate`: each one's per-row per-pass values, medians and IQRs, and the bootstrap
  comparison of every pair of rows that can be compared at all.

What it deliberately does **not** do: §4 headline, §6 cpu, §7 memory, §10 persist, §11 tcp (§4 is
batch 4 by D20.8 #2, and it is the one section that prints all twenty-one metric columns), §12/§13/§14
and `verdict.json`'s `control_blocks`/`dual_phase`/`ledger` (batch 5), the six batch-4 metrics
(`rel.*`, `persist.*`, `mem.*`, `cpu.*`), and the truncation caveat (§14.7, which is E4-c). No rule the
reference does not have was added.

## 2. The criterion

Command and output on the final tree (`--raw /tmp/wf-synth/raw`, the tree the differ extracts from
`verification/synthetic-tree.tar.gz`):

| batch | semantic | byte | slices |
|---|---|---|---|
| `1a` | **0** | **0** | 7 |
| `1b` | **0** | **0** | 2 |
| `1c` | **0** | **0** | 3 |
| `2` | **0** | 1 (pre-existing, §8) | 4 |
| `3` | **0** | **0** | 18 |
| `4` | 2 (6 missing) | 2 | 11 |
| `5` | 2 (3 missing) | 2 | 6 |

`--mode semantic --batch 3 --tolerance 0` is **rc=0 as well**: the tolerance is not what lets this batch
pass. Every number in the eighteen slices — the bootstrap's `estimate`, its `ci95`, both p-values, both
Holm-adjusted p-values — is reproduced **exactly**, not merely inside one unit of the reference's printed
last digit. That is the `CpRandom` port of b1b (CPython's `random.Random(int)`, `_randbelow`'s rejection
sampling), the same seed derivation, the same draw order and the same float operations, and it is worth
re-running whenever the bootstrap is touched: a changed random stream would show up here first.

```text
-- batch 3
   tables.md:5: equal
   tables.md:8: equal
   tables.md:9: equal
   verdict.json:metrics/lat.tcp_rtt.p50: equal
   … (the other fourteen metrics: equal)
compared 18 slice(s): 0 differ(ent), 0 missing
differences: 0 structure, 0 value, 0 missing
rc=0: every slice of this batch is equal
```

§4 and §5 are rc=2 *because of missing slices* — §4/§6/§7/§10/§11 are still `<!-- TODO(batch 4) -->`
and the six batch-4 `metrics/*` members are absent by design (§7.1) — not because a structure rule
moved inside the sections this batch added.

**Byte mode's batch 2 result is pre-existing and not this batch's.** `--mode byte --batch 2` reports one
difference: the produced §3 has **one extra blank line** between §3.1's caption and `### 3.2`
(`tables.md:3` line 60: reference `### 3.2 Measurement validity gates` vs produced `''`). Semantic mode
folds that block boundary, which is why batch 2's criterion (rc=0) is unaffected. `Tables/TableGates.cs`
is not among this batch's modified files and byte mode was never batch 2's criterion; the difference is
registered here so the next reader does not read it as a regression.

## 3. The module map

Every file carries the reference function beside it. Effective lines are
`benchmarks/WinForward.E2E/scripts/effective-lines.py`'s own count.

| file | effective lines | what it carries |
|---|---|---|
| `Metrics/MetricCell.cs` | 70 | `MetricValue` + `metric_row_status` (`MetricStatus.Resolve`) |
| `Metrics/MetricSpec.cs` | 31 | `METRIC_SPECS`' row shape + `THRESHOLDS` |
| `Metrics/MetricCatalogue.cs` | 125 | `METRIC_SPECS`/`METRIC_DEFINITIONS` (the fifteen), `per_pass_values`, `cell_text` |
| `Metrics/MetricComparisons.cs` | 247 | `build_verdict`'s metrics half: `per_row`, `pair_comparability`, `bootstrap_pair`, `holm_adjust`, `decide`, the `pairs` array |
| `Tables/TableLatency.cs` | 134 | `table_latency` — §5 |
| `Tables/TableUdp.cs` | 196 | `table_udp` + `UDP_ACCURACY_FIELDS` — §8 |
| `Tables/TableDns.cs` | 186 | `table_dns`, `_port`, `_dns_udp_share`, `_dns_rtt_cell` — §9 |
| `Checks/WindowOverflow.cs` | 25 | `window_overflow_reached` |
| `Stats/DescriptiveStats.cs` | 114 (was 100) | `+ holm_adjust` |
| `Tables/TablesWriter.cs` | 82 | §5/§8/§9 wired to their bodies |
| `Verdict/VerdictSections.cs` | 88 | `+ metrics` |
| `Model/ArmAccess.cs` | 78 | `NullRateReason` made internal (the MIX fallback's guard compares against it) |

The project is 61 files and 7216 effective lines by that counter; the largest file is
`Tables/TableEnvironment.cs` at 321, and the largest this batch wrote is `MetricComparisons.cs` at 247 —
every one under the 400-line rule (`effective-lines.py` over the four paths is silent, §9).

Three implementation notes, because each is a rule rather than a port:

1. **The metric family is the *tested* pairs, not the candidate pairs.** `holm_adjust` runs over one
   p-value per comparable pair; feeding it the whole candidate list (36 pairs for a metric with 10
   comparable ones) makes every adjusted value wrong, and the differ sees it as a value difference in
   `holm_p_equivalence` (measured before the fix: 7 differences across five metrics). The family size is
   `candidates.Count(comparable)` and is published as `holm_family_size`.
2. **A pair that makes no claim is listed, never dropped.** `pair_comparability`'s four reasons are the
   reference's sentences, in its order (design absence, then not-carried, then the port-53 carriage,
   then "one side has no per-pass value"), and the *doubled* `not measured in this row:` prefix the
   reference produces for a not-in-plan row is reproduced verbatim.
3. **The MIX loss rate's fallback is guarded by the reason.** The arm-level `metrics/udp.lossRate` is
   read only when the class field is *absent*; a JSON `null` there is the harness saying the
   denominator was zero, and falling back would replace that statement with a number about a different
   counter. The `zeros-and-nulls` mutant exercises the guard; the `mix-class-missing` mutant exercises
   the positive path.

## 4. §5's 154 eleven-cell lines

`md_table()` joins whatever cells it is handed and never pads them against the header.
`table_latency` hands eleven cells to a row whose histogram is missing or not carried (the row id, the
arm, the count column, eight more cells and a trailing `note`-shaped cell) while the header names ten.
The golden therefore carries 236 row lines in §5, of which **154 have eleven cells** and 82 have ten.

Measured on the produced document and the frozen golden, per section, as `cells → lines`:

| section | produced | golden |
|---|---|---|
| §5 | `{10: 82, 11: 154}` | `{10: 82, 11: 154}` |
| §8 | `{21: 20, 5: 11}` | `{21: 20, 5: 11}` |
| §9 | `{11: 11}` | `{11: 11}` |

and the 154 eleven-cell lines of §5 are **byte-identical**, in order, to the golden's. The differ's
`cells` structure assertion is what keeps them visible (a row's cell count is structure, never a
tolerated value), and the `cells` negative control (§6) drops one cell from one such row and the differ
names it: `expected '11 cell(s)' vs actual '10 cell(s)'`.

The eleven-cell shapes are produced in four places, each with its own reason: a design absence
(`n/a (reason)` + eight `n/a`), a row that cannot carry UDP (eleven not-carried markers), a histogram
nobody published (`n/a (arm has no class histogram)` + eight `n/a`), and the deferred-window ceiling
(eleven overflow cells). In every one of them the *rendering* is the reference's
`["a", "b", "c"] + [x] * 8` against a ten-column header — reproduced, not repaired.

## 5. The fifteen metrics, and the three cell tails the clean tree never reaches

The clean tree always has three passes per cell, so it exercises `(n=K)` and the null/unavailable tail
only in a few places. The mutation suite covers the rest:

| tail | reference form | reached by |
|---|---|---|
| `(n=1)` | `6000 (n=1)` | `under-three-passes` (§8 `sent` of wf-aot-opt LOSS) |
| `(n=1 of 3; 2 null)` | `… (n=1 of 3; 2 null)` | `zeros-and-nulls` (`thru.goodputMbps` in the metrics slice) |
| `(K pass(es) without a value)` | `< 3/2400 = 0.1250 % (2 pass(es) without a value)` | `zeros-and-nulls` (§8 `lossRate`) |
| the **empty cell** | `\|  \|` — a rate the harness wrote `null` | `zeros-and-nulls` (wf-fdd-opt LOSS `lossRate`, all three passes null) |
| the rule of three overridden by one non-zero pass | `0 [0–50] (n=3)` | `udp-loss-changed` |
| `harness error: UDP identity violated (…)` | the reason inside `n/a (…)` | `udp-identity-violated` |

The empty cell and `n/a (reason)` are different *categories* in the differ, and the category is what a
table cell is judged on: the `zeros-and-nulls` mutant is the only place the empty cell appears at all
(the frozen golden has none — `python-oracle-changes.md` §5.3 records that, which is why the rule is
also covered by b1b's table-driven unit tests).

## 6. The mutation trees

Nine mutants, each one edit on a fresh extraction of the frozen tarball at the hardcoded
`/tmp/wf-synth`. Both implementations are run on the mutated tree (`analyze.py --raw /tmp/wf-synth/raw`
and `WinForward.E2E.Analysis --raw /tmp/wf-synth/raw`) and the batch-3 slices are compared **byte for
byte** by the differ itself: `oracle-diff.py --mode byte --batch 3 --golden <mutant ref> --cs-out
<mutant cs>`. Each mutant is also compared against the **frozen golden**, which is how "this mutation
moved the compared surface at all" is told from "this mutation was vacuous".

Recipe: `/tmp/e4b3/run_mutants.py` (extract → mutate → run both → differ twice), verification of each
mutation's shape against the reference's own output: `/tmp/e4b3/verify_mutants.py`, summary
`/tmp/e4b3/mutants/summary.tsv`. **Both suites were re-run against the final build** after the gates'
last refactors, and the table below is that run.

| mutant | the edit | ref vs C# | vs frozen golden |
|---|---|---|---|
| `histogram-empty` | wf-aot-opt's LAT `latency.tcp-rtt` emptied in all three passes | **rc=0** | rc=1 |
| `zeros-and-nulls` | a whole-cell null rate (wf-fdd-opt LOSS `lossRate`/`corruptRate`), a two-null tail over a zero (wf-aot-opt MIX `classes.udp.lossRate`), and a two-null tail over a value (`thru.goodputMbps`) | **rc=0** | rc=1 |
| `under-three-passes` | wf-aot-opt's LOSS `result` deleted in pass2 and pass3 (the samples stay) | **rc=0** | rc=1 |
| `udp-loss-changed` | `late = 100` in one LOSS pass, `corruptDatagrams = 40` in one MIX pass, `udp.lossRate = 0.025` in one LAT pass — every UDP identity kept | **rc=0** | rc=1 |
| `udp-identity-violated` | wf-aot-opt's LOSS `late += 7` without adjusting `arrived`, so the identity no longer holds | **rc=0** | rc=1 |
| `dns-timeout` | the port-53 DNS arm times out 50 queries (`answered=950`, `answerRate=0.95`) and loses its pass-3 `dns-rtt` histogram | **rc=0** | rc=1 |
| `window-overflow` | proxifyre's LAT `gates.windowOverflow = 7` in one pass | **rc=0** | rc=1 |
| `mix-class-missing` | wf-fdd-opt's MIX `metrics.classes` removed, so the arm-level loss rate has to answer | **rc=0** | rc=1 |
| `truncated-frames` | both target ledgers report `truncatedFrames = 3` (on `tcpSummary`, `dnsSummary` and `targetSummary`) | **rc=0** | **rc=0** |

**Nine of nine byte-equal, and no unexplained difference.** Each mutant was also verified to have moved
the campaign *in the shape it claims*, reading the reference's own output
(`/tmp/e4b3/verify_mutants.py`, one assertion per mutant over §5/§8/§9 and the metric slice):

- `histogram-empty` → §5 says `n/a (LAT has no tcp-rtt histogram)`; `lat.tcp_rtt.p50`'s
  `rows.wf-aot-opt.passes` is 0.
- `zeros-and-nulls` → §8 carries a genuinely empty cell and `(2 pass(es) without a value)`;
  `thru.goodputMbps`'s `null_passes` is 2 and `loss.lossRate`'s is 3.
- `under-three-passes` → §8 renders `(n=1)`; `loss.lossRate`'s `passes` is 1.
- `udp-loss-changed` → §8 renders `0 [0–50] (n=3)` (the "median zero with a non-zero pass" rule) and
  `0 [0–20] (n=3)`; `lat.udp_lossRate`'s pass2 is 2.5 and `mix.udp.lossRate`'s is 0.0417.
- `udp-identity-violated` → §8's wf-aot-opt LOSS line reads `n/a (no sent metric)` in every cell and
  `loss.lossRate`'s `passes` is 0 with the identity reason in `unavailable_passes`.
- `dns-timeout` → §9 renders `95.0000` and `(n=2)`; `dns.rtt.p50`'s `passes` is 2.
- `window-overflow` → §5's whole proxifyre LAT row is `n/a (windowOverflow > 0)`, and the **metric**
  keeps all three passes — the reference consults the ceiling only where it renders §5, and the port
  matches that asymmetry rather than extending it.
- `mix-class-missing` → §8 says `n/a (MIX does not publish sent)`; `mix.udp.lossRate` keeps three
  passes, i.e. the arm-level fallback answers.
- `truncated-frames` → **the batch-3 surface does not move on either side.** Neither implementation
  reads the counter: the reference's only `truncat…` hits are `gates/scheduleTruncated`, a different
  key, and the C# side does not consume `truncatedFrames` at all (`rg` over the analyzer prints none).
  This is the machine-checked half of AC "no arm-level cell consumes it": with the ledgers reporting
  three truncated frames, all eighteen batch-3 slices are byte-identical to the **clean** golden.

**No mutant made the reference crash or become unreachable**, so no D20.1 exception was needed. The
reference ran to completion on every one of the nine trees; the only edit that changed the *number of
implementations able to answer* was `udp-identity-violated`, where the reference and the port both
report the violation as a reason (and both keep the arm's non-UDP cells).

## 7. Negative controls

Nine controls, each one edit on a copy of the **clean produced** directory, compared with
`oracle-diff.py --mode semantic --batch 3 --cs-out <copy>` (`/tmp/e4b3/negative_controls.py`). Every
one is red where it must be; the base copy is green.

| control | edit | rc | what the differ names |
|---|---|---|---|
| `header` | §5's header column `count` → `samples` | 1 | `[structure] tables.md:5.\`tcp-connect\`.header` |
| `cells` | one cell dropped from an eleven-cell §5 row | 1 | `[structure] …[row=wf-aot-opt&arm=IDLE].cells: expected '11 cell(s)' vs actual '10 cell(s)'` |
| `reason-to-number` | §8's `n/a (reason)` sent cell → `0` | 1 | `[structure] …[row=control-pre&arm=LOSS].sent: expected 'n/a (reason): …' vs actual 'number: 0'` |
| `number-to-reason` | §8's `sent` number → `n/a (reason)` | 1 | `[structure] …[row=wf-aot-opt&arm=LOSS].sent: expected 'number: 6000 …' vs actual 'n/a (reason): …'` |
| `bare-reason` | §9's bare `n/a` port cell → `0` | 1 | `[structure] tables.md:9.table1[row=control-pre].DNS arm port: expected 'n/a: n/a' vs actual 'number: 0'` |
| `marker-to-number` | §8's `not carried (UDP bypassed)` → `0` | 1 | `[structure] …[row=proxifier&arm=MIX].sent: expected 'text: not carried (UDP bypassed)' vs actual 'number: 0'` |
| `counter-off-by-one` | a whole-number counter `6000` → `6001` | 1 | `[value] tables.md:5.\`tcp-rtt\`[row=wf-aot-opt&arm=LATLOAD].count` |
| `metric-number` | a metric's per-row `median` `648.258` → `700.0` | 1 | `[value] verdict.json:metrics/lat.tcp_rtt.p50.rows.wf-aot-opt.median` |
| `metric-missing-member` | one metric's `threshold` member deleted | **2** | `[missing] verdict.json:metrics/lat.tcp_rtt.p50.threshold` |

The six structural controls cover the four category directions the task names — header, cell count,
`n/a` ↔ number, marker ↔ number — plus the bare-`n/a`-versus-`n/a (reason)` distinction; the two value
controls show the tolerance is not swallowing a datagram counter (both sides printed the integer, so
the comparison is exact) nor a moved median; the missing-member control shows a deleted JSON member is
a missing slice (rc=2), not a tolerated value.

## 8. Deviations and observations

**8.1 The `metrics` container publishes fifteen members, not twenty-one.** The six batch-4 metrics
(`rel.unexpectedEofRate`, `rel.fidelityRate`, `persist.responseRate`, `persist.reconnects`,
`mem.privateBytes.p50`, `cpu.proxy.vcpuPct`) are **absent** rather than present with a placeholder
extraction, so `--batch 4` reports them as missing slices (rc=2) instead of comparing a wrong number.
`Metrics/MetricCatalogue.cs` names all six in `Pending`'s remarks with a `<!-- TODO(batch 4) -->`
marker. Nothing in §5/§8/§9 or the fifteen reads a batch-4 key: §5 reads the harness's own histograms,
§8 reads the LOSS arm and the MIX UDP class, §9 reads the two DNS arms' own counters.

**8.2 §5's ceiling replaces the row; the metric does not consult it.** The reference calls
`window_overflow_reached` only inside `table_latency`, so a row whose window deferred work prints the
overflow cell in §5 while its metric keeps the pass. The `window-overflow` mutant is the evidence that
the port reproduces that asymmetry deliberately.

**8.3 `MetricCatalogue.Pending` moved from "3/4 …" to "4 …".** It is printed on stdout only
(`stdout is not part of the contract`) and now says the six batch-4 extractors are what the run still
owes.

**8.4 `DescriptiveStats.HolmAdjust` is new, and `control_blocks` will reuse it.** The metric pairs need
the step-down adjustment; batch 5's `control_blocks` needs the same function, so it lives in the
statistics module rather than beside the metrics.

**8.5 `ArmAccess.NullRateReason` went from `private` to `internal`.** The MIX loss rate's fallback guard
compares a reading's reason against it, and the constant is the only definition of that sentence.

**8.6 `MetricStatus.Resolve` takes the declaration's three parts, not a `MetricSpec`.** §5 and §9 ask
"is this arm measured here / does this row carry UDP / is the port-53 path direct?" about arms that are
not metrics at all; passing a whole metric spec would have forced them to fabricate one.

**8.7 `MetricSpec` has no `Digits`, and the spec-based `cell_text` overload is gone.** Every cell this
batch renders goes through `MetricCatalogue.CellText(cell, digits, unit, …)`, which is what the
reference's own `cell_text` is: a rounding, a unit and the four category rules. The recorded digit count
and the overload that reads it belong to §4, the only section that prints all twenty-one metrics with
their own rounding; the inspector found the overload unused, and the rule the b1a batch set is that state
comes back with its consumer, so both were removed rather than kept as a placeholder. §9's answer-rate
cell passes `digits: 4, unit: "pp"` where it is used, which is the same rendering path.

**8.8 Byte-mode `--batch 2` is rc=1 and stays that way.** One extra blank line in §3 before
`### 3.2` (§2 above). Out of this batch's scope; registered for check.

## 9. The gates

All six, on the final tree, in the repository's own order (`/tmp/e4b3/gates/`):

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** (`build-final.log`) |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed, 0 skipped** in 14 assemblies, rc=0 (`test-final.log`) |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **0 bytes of output**, rc=0 (`format-final.log`) |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`, on a **cleared** `InspectCode` cache | 630 files inspected; **`<Issue>` 0**, `<IssueType>` 0, no `CSharpErrors` (`inspectcode-cold.xml`) |
| lines | `effective-lines.py` over the four paths | **0 bytes of output**, rc=0 (largest 321; project 61 files / 7216 effective lines) |
| oracle | `--mode semantic --batch 1a/1b/1c/2/3` (+`--tolerance 0`), `--mode byte --batch 3` | rc=0/0/0/0/**0** (**0** at tolerance 0, **0** in byte mode); `4/5` rc=2 |

Across two rounds the inspector reported **17** findings, every one of them in this batch's new files
and every one of them real. The first round (13) was one redundant `using`, one unused method, one
unused auto-property accessor (an id the cell stored and never read), two collections that could be
`init`-only, six threshold families that could be private, one `if` that wanted to be a `return`, and one
straight-line list that wanted to be a collection expression. The cold-cache round after those fixes (4)
was the cascade they exposed: the two collections were then `get`-only, and the cell's `status` and
`status_reason` — only ever written by the object initializer — were `init`-only. Each was fixed rather
than suppressed (the `if` was already an early-exit shape, so collapsing its two remaining `return`s into
one conditional expression is what the code wanted). **No `.editorconfig` entry and no `#pragma` was
added by this batch.**

## 10. For check

1. **The integer rule against the bootstrap, confirmed.** `--mode semantic --batch 3 --tolerance 0`
   is rc=0 (§2), so `p_value`, `holm_p_value`, `p_equivalence` and `holm_p_equivalence` — counts over
   `--resamples` printed by `repr` — are reproduced exactly. Worth keeping as the regression the
   bootstrap is judged by: if a later change makes it red, it is a changed random stream, not a
   formatting question.
2. **The family is the tested pairs.** Confirm `holm_family_size` equals the number of `pairs[]` entries
   with `"in_holm_family": true` and that `holm_adjust` runs over exactly those, in tested order
   (§3.1). The pre-fix symptom was 7 value differences in `holm_p_equivalence` on five metrics.
3. **`--batch 4` is rc=2 because of missing slices.** Check the six absent `metrics/*` members and the
   five `<!-- TODO(batch 4) -->` sections; the batch-3 sections must not be named in batch 4's report.
   The one *byte-mode* red on an earlier batch — `--batch 2`, one extra blank line before `### 3.2`
   (§8.8) — is pre-existing and outside this batch; decide whether it is worth a one-line fix in
   `TableGates` or an entry in the task's observations.
4. **The 154 eleven-cell lines** (§4) are held by the differ's `cells` assertion, not by the renderer:
   `MarkdownTable` never pads against the header on purpose, and the `cells` control is the proof.
5. **The truncation counter is consumed nowhere in this batch** — the `truncated-frames` mutant shows
   both implementations' batch-3 slices are byte-identical to the clean golden with the ledgers
   reporting three truncated frames. E4-c's §14.7 disclosure has to be the only consumer, and its
   negative control ("write the count into an arm cell ⇒ the assertion must go red") is still owed.
