# E4-b3 — independent check

Scope: the uncommitted E4-b3 work on top of HEAD `268180b` — §5 latency, §8 udp, §9 dns and the fifteen
`metrics/*` members (`Metrics/{MetricCatalogue,MetricCell,MetricComparisons,MetricSpec}.cs`,
`Tables/{TableLatency,TableUdp,TableDns}.cs`, `Checks/WindowOverflow.cs`, plus `+HolmAdjust`,
`+metrics`, `+§5/§8/§9` in the files already tracked). Everything below was run by the checker on the
working tree; the checker's own harnesses live in `/tmp/e4b3check/` (`run_mutants.py`, `controls.py`).

**Verdict: E4-b3 may be committed.** One byte-mode difference on an *earlier* batch is registered, not
fixed (§5); one test is owed for a rule the oracle cannot reach (non-blocking, §7).

## 1. The criterion, re-run

| command | rc | slices |
|---|---|---|
| `oracle-diff.py --mode semantic --batch 3` | **0** | 18 equal |
| `oracle-diff.py --mode semantic --batch 3 --tolerance 0` | **0** | 18 equal |
| `oracle-diff.py --mode byte --batch 3` | **0** | 18 equal |
| `--mode semantic --batch 1a / 1b / 1c / 2` | **0** | 7 / 2 / 3 / 4 equal |
| `--mode semantic --batch 4` | **2** | 5 differ + **6 missing** |
| `--mode semantic --batch 5` | **2** | 3 differ + **3 missing** |
| `--mode byte --batch 1a,1b,1c,2` | **1** | §3, one extra blank line (§5) |

`--tolerance 0` is rc=0: every bootstrap `estimate`, both `ci95` endpoints, both raw p-values and both
Holm-adjusted p-values are reproduced **exactly**, so the `CpRandom` stream, the seed derivation, the
draw order and the float sequence are unchanged. This is the strongest regression signal in the batch
and it is green.

## 2. Holm family — the tested pairs, verified three ways

1. **Invariant on the published document.** Over all 21 `metrics/*` members of the frozen golden:
   `holm_family_size == count(pairs[] where in_holm_family)`; the `in_holm_family` entries are exactly
   the `comparable` subsequence, in order; every comparable pair carries both adjusted p-values.
   0 violations. The spread is real: 10 comparable of 36 candidates for the latency/loss metrics, 2 for
   `dns.answerRate`/`dns.rtt.p50` (the port-53 carriage rule), 6 for `mix.udp.lossRate`, 21 for the two
   batch-4 resource metrics.
2. **A family that moves, moving on both sides.** New mutant `dnsalt-value-missing`: `proxybridge`'s
   DNSALT publishes `metrics.answerRate = null` in all three passes. Candidates stay 36, the family
   shrinks 10 → 6, and the eight pairs involving that row become non-comparable with the reference's own
   sentence (`one side has no per-pass value for this metric`, plus the *doubled*
   `not measured in this row: not measured in this row: …` prefix on the not-in-plan rows, reproduced
   verbatim). `--mode byte --batch 3` between the two implementations: **rc=0**; against the frozen
   golden: rc=1 (the mutation moved the surface).
3. **The "candidate pairs" error mode really does go red.** `MetricComparisons.Pairs` was temporarily
   changed to size the family by `candidates.Count` and index by candidate position (file backed up,
   restored, sha256 verified — see §4). Result: `--mode semantic --batch 3 --tolerance 0` ⇒ **rc=1**
   with **7 value differences across five metrics**, all `holm_p_equivalence`
   (`lat.tcp_rtt.p50/p99.pairs[8|12]`, `lat.udp_rtt.p50.pairs[8]`, `latload.tcp_rtt.p50/p99.pairs[18]`),
   expected `0.5198/0.5094/0.2594/0.2621/0.2551` vs actual `1.0` — the injected `1.0` of the
   non-comparable pairs saturates the running maximum. This reproduces the symptom the batch's own
   evidence records, so the fix is load-bearing rather than cosmetic.

## 3. The 154 eleven-cell lines

Cell counts, produced vs frozen golden, per section (`cells → lines`, rows *and* headers counted, the
separator row excluded):

| section | produced | golden |
|---|---|---|
| §5 | `{10: 78, 11: 154}` | `{10: 78, 11: 154}` |
| §8 | `{21: 19, 5: 10}` | `{21: 19, 5: 10}` |
| §9 | `{11: 10}` | `{11: 10}` |

(§5's 228 data rows are 154 eleven-cell and 74 ten-cell; adding the four 10-column headers gives the
batch evidence's `{10: 82, 11: 154}`, and §8/§9 gain one header each → `{21: 20, 5: 11}` and
`{11: 11}`. The counts reconcile exactly.)

`MarkdownTable.Render` joins whatever cells it is handed and never pads against `headers.Count` — read
in the source, and stated in its own remarks as deliberate. The negative control that drops one cell
from an eleven-cell row makes the differ name it:

```
[structure] tables.md:5.`tcp-connect`[row=wf-aot-opt&arm=IDLE].cells: expected '11 cell(s)' vs actual '10 cell(s)'
```

Padding the row in the other direction (11 → 12 cells) is red as well, and the whole §5 slice is
byte-identical to the golden (`--mode byte --batch 3` rc=0), so the 154 lines are reproduced in order,
not merely counted.

## 4. Mutation trees — seven, none of them the author's

Each mutant is one coordinated edit on a fresh extraction of the frozen tarball at `/tmp/wf-synth`;
both implementations are run on the mutated tree and compared with
`oracle-diff.py --mode byte --batch 3`. "vs frozen" = the same comparison with the frozen golden as the
reference, which is how "did this mutation move the compared surface at all" is told from "was it
vacuous".

| mutant | the edit | ref vs cs | vs frozen |
|---|---|---|---|
| `zero-denominator` | `proxybridge` MIX `classes.udp.sent/arrived/late/never/abandoned/corruptDatagrams = 0` and `lossRate = null`, arm-level `udp.sent = 0`/`udp.lossRate = null`, all three passes | **reference crashes** | rc=1 (C# alone) |
| `null-rate` | `classes.udp.lossRate = null` in pass2 with `sent` left at 2400 (guard path); the field **deleted** in pass3 (arm-level fallback path) | **0** | rc=1 |
| `single-pass` | `proxifier` LAT and THRU `result` records removed in pass2/pass3 (samples kept) | **0** | rc=1 |
| `histogram-overflow` | `proxifyre` pass1 LAT `tcp-rtt.maxUs = 4 × windowCeilingMs`, `p999Us = 8 × windowCeilingMs` (p999 > max) | **0** | rc=1 |
| `window-and-schedule` | `proxifyre` pass2 LAT `gates.windowOverflow = 5` **and** `gates.scheduleTruncated = 4` | **0** | rc=1 |
| `dns-port-conflict` | `wf-aot-dnsrelay` DNSALT declares `dnsPort = 53` (both arms claim the port-53 path) in pass2/pass3, and pass2 also has `udp.sent = tcpSent = 0` so no share exists | **0** | rc=1 |
| `truncated-frames` | **21** `truncatedFrames` counters in `ledger-main.jsonl` set to 3 (all occurrences, nested `dns`/`dnsAlt`/`tcp` blocks included) | **0** | **0** |

Six of seven are byte-equal between the implementations and moved the batch-3 surface. The seventh is
the E4-c prerequisite: with the ledgers reporting three truncated frames, **the whole document is
byte-identical on both sides** — not just batch 3. `diff` of the mutant's C# `tables.md` and
`verdict.json` against the clean run is empty, the mutant reference's two files are byte-identical to
the frozen golden, `rg truncatedFrames` over the analyzer prints nothing, and the reference's only
`truncat*` hits are `gates/scheduleTruncated` (a different key). Nothing in this batch consumes the
counter.

### 4.1 The reference crashes on a zero denominator (new, and it matters)

`zero-denominator` is a **C#-only boundary tree**: `analyze.py` exits 1 with

```
File "analyze.py", line 5037, in table_ledger
    else ("ok (±%s)" % fmt_num(band, 0) if abs(arm["udp_datagrams"] - datagrams) <= band else …)
TypeError: '<=' not supported between instances of 'float' and 'NoneType'
```

Mechanism, instrumented on a copy of the reference: `client_datagram_count` returns `0.0` for that arm,
the band assignment is `datagram_tolerance(...) if datagrams else None` — and `0.0` is falsy, so the
band is `None` — while the guard one line earlier is `if datagrams is None`, which a zero slips past.
It is a latent reference defect reachable whenever an arm reports zero UDP datagrams, i.e. exactly the
"zero denominator" state this batch's null-rate rules exist for. Consequences: (a) the state can only
ever be asserted against the C# output (D20.1), and (b) after E4-d deletes the reference there is
nothing left to compare it against — see §7.

The C# side on that tree, asserted pointwise (5/5): `metrics/mix.udp.lossRate.rows.proxybridge` has
`passes == 0`, `null_passes == 3`, all three passes carrying
`null rate: the harness wrote null because the denominator was zero`; §8's MIX `lossRate` cell is
**empty**, and the row carries no zero rate anywhere.

### 4.2 The null-rate guard and the fallback, on one tree

In `null-rate`, pass2 (present `null`) reads `unavailable_passes.pass2 = "null rate: …"` with
`metrics/udp.lossRate = 0.0` sitting right there unread — the guard; pass3 (field absent) reads
`per_pass.pass3 = 0.2083`, the arm-level fallback answering. Both sides byte-identical, so both
branches are pinned by the oracle on a tree where they disagree with each other.

## 5. Registered, not fixed: byte-mode `--batch 2`'s blank line

`TableGates.cs:41` (`string.Empty,`, between `Caption()` and `"### 3.2 …"`) emits one blank line the
reference does not. The produced §3 slice is 21050 chars against the golden's 21049 and a unified diff
shows the single extra `''` line; nothing else in the whole `1a,1b,1c,2` byte run differs.

* Byte mode is not a batch criterion (D21; `oracle-diff.py`'s own docstring calls it "the
  structure-surface regression mode, not a batch criterion"), and `--mode semantic --batch 2` is rc=0.
* §3 belongs to batch 2, whose file is committed at `HEAD` and untouched by this batch; the fix is one
  line — delete `TableGates.cs:41` — and it is **semantically inert**: `section_blocks` merges adjacent
  paragraphs across a blank line (the blank line only calls `flush_table()`, a no-op between
  paragraphs, and `flush_text()` runs at the `###` heading), so §3's block sequence and every cell stay
  the same and semantic batch 2 stays rc=0, while the two slice texts become identical and byte batch 2
  turns rc=0.
* Recommendation: apply it in whichever change next touches §3, or as a one-line hygiene commit. It does
  not block E4-b3, and folding a batch-2 rendering edit into a batch-3 commit is the reviewer's call,
  not the checker's.

## 6. The gates, on the tree that would be committed

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed, 0 skipped**, rc=0, 14 assemblies |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **0 bytes of output**, rc=0 |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx` on a **cleared** cache | **630 files**, `<Issues />` empty, `<IssueTypes />` empty, no `CSharpErrors` |
| lines | `effective-lines.py` over the four paths | **0 bytes of output**, rc=0 |
| oracle | see §1 | |

New suppressions: **none.** `.editorconfig` has no diff, no entry names any file this batch touched, and
`rg '#pragma|ReSharper disable'` over all nine files this batch wrote or changed prints nothing. No
reflection either — not one `typeof`/`nameof`/`GetType` in the new files, so the `typeof(...)`-literals
rule is satisfied vacuously.

Effective lines, this batch's largest files: `MetricComparisons.cs` 247, `TableUdp.cs` 196,
`TableDns.cs` 186, `TableLatency.cs` 134, `MetricCatalogue.cs` 125 (was 100),
`DescriptiveStats.cs` 114 (was 100), `MetricCell.cs` 70, `MetricSpec.cs` 31,
`WindowOverflow.cs` 25. All ≤ 400.

Frozen artifacts: `git status --short benchmarks/WinForward.E2E.Analysis/verification` is empty. No
`src/` file and no `.trellis` schema file is touched; the uncommitted set is exactly the twelve
analyzer files.

## 7. Owed, and not blocking: a test for the rule the oracle cannot reach

The batch's rendering rules are judged by the oracle, which is a stronger criterion than a unit test for
everything the clean tree exercises. Two rules are exceptions, because the frozen tree cannot reach them
and now **nothing committed pins them**:

* a JSON-`null` rate renders as an **empty cell** (§8 `lossRate`, `MetricCatalogue.CellText`'s
  `NullPasses > 0 → ""`), and
* the MIX arm-level fallback is **guarded by the reason**, so a null does not fall back.

`FROZEN.md` §5 says the empty-cell rule "is asserted instead by the C# side's own unit tests", but the
test it names (`ContractShapeTests.AnUnknownReadingIsNullAndNeverAMissingKey`) pins the *record* side —
the harness writing `null` — not the analyzer's rendering. Today the only evidence for either rule is
untracked `/tmp` mutants (the batch's `zeros-and-nulls`, this check's §4.2). §4.1 sharpens the point:
the reference **cannot** produce the state at all, so no oracle run will ever cover it, and after E4-d
retires `analyze.py` there is nothing left to diff against.

Judgement: **owed, non-blocking.** The port's behaviour was verified here (5/5 assertions, §4.1), no
committed artifact depends on the state, and the risk is a future silent regression in a rule that is
currently evidence-only. The natural home is the mechanism `FROZEN.md` §4 already defines for exactly
this class of state: a `--zero-denominator` boundary flag on `verification/synthetic/make_tree.py`,
asserted against the C# output alone with a pointed assertion and its own negative control. That route
needs no change to the analyzer's visibility — only three types are `public`, and D20.6 forbids an
`InternalsVisibleTo` — so it is the one that does not force a design decision.
