# E4-b5 — §12 dual, §13 control, §14 ledger, and the last three verdict keys

Batch E4-b5 of `.trellis/tasks/10-07-e2e-e4-analyzer`: the three sections the batch table assigns to
§12/§13/§14 plus `verdict.json`'s `control_blocks`, `dual_phase` and `ledger`. Authority:
`design-decisions.md` **D21** (semantic comparison replaces byte equality), **D21.1** (the tolerance
direction and the identifier rule for dotted endpoints), **D21.2**/**D21.3** (the registrations that are
E4-c's, not this batch's), **D20.1** (a boundary tree is asserted on the C# side alone), **D20.2**
(slicing and batch ownership) and **D20.7** (the truncation caveat belongs to a *new* `### 14.7` in
E4-c, not here).

Its criterion is `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic --batch 5`
⇒ **rc=0**, with `--batch 1a/1b/1c/2/3/4` still **rc=0** and the **full** run (no `--batch`) ⇒ **rc=0**.

**Result: batch 5 rc=0 (6/6 slices), the full run rc=0 (51/51 slices: 17 `tables.md` slices and 34
`verdict.json` slices), and `1a/1b/1c/2/3/4` rc=0.** `--mode semantic --tolerance 0 --batch 5` is
**rc=0** as well. `--mode byte --batch 5` is **rc=1 on one slice**, for a reason that is not a value:
the reference builds `ledger.passes[*].types` by iterating a python **set**, so its member order is a
hash-table artefact (§7.1). After reordering that one object on the produced side — nothing else — byte
mode is **rc=0 for all six slices**.

With this batch every one of the sixteen `## N.` sections and all fourteen top-level verdict keys is
rendered, and no `<!-- TODO(batch N) -->` remains in the produced `tables.md` (§2.3).

## 1. What the batch is, and what it deliberately is not

- **§12 `Dual-phase detail`** — one row per campaign row: the pass count, the largest `directLeak`, both
  lanes' LAT p50 and LOSS loss rate, the direct lane's excess over the campaign's *best* direct lane,
  the shape column and the one-sentence verdict.
- **§13 `Control block comparison`** — the five `BASE` metrics as a bootstrap over passes with the same
  thresholds the findings use, then the per-pass table of which block each pass ran and whether the
  product block was bracketed.
- **§14 `Target-ledger cross-check`** — six subsections: provenance and attribution (14.1), the two
  sides' accounting per (pass, run, arm) (14.2), the UDP source-endpoint partition (14.3), the DNS
  query totals (14.4), the TCP verdicts against the client's expectations (14.5) and — only when there
  is something to disclose — the target-side decode total (14.6).
- **The three keys** — `control_blocks` (availability, one entry per metric with both blocks' per-pass
  values and the comparison, and the per-pass presence/ordering) , `dual_phase` (one entry per pass and
  row with both lanes' readings) and `ledger` (availability, one entry per pass with paths, records,
  types, labels and attribution, and the DNS port totals).

What it deliberately does **not** do: the truncation caveat of **§14.7** (E4-c, D20.7/C23 — §14.6 is
the reference's own subsection and is rendered exactly as the reference renders it), the
`--zero-denominator` edge flag and the empty-cell / MIX-fallback / compensated-sum assertions E4-c owes
(D21.2 #2 and D21.3 #1), and the byte-mode §3 blank line (D21.2 #1, still the produced file's only
byte difference anywhere: `tables.md` is 1246 lines against the golden's 1245, one extra line at 388).
No rule the reference does not have was added.

## 2. The criterion

### 2.1 The batch, in three modes

| batch | semantic | semantic `--tolerance 0` | byte | slices |
|---|---|---|---|---|
| `1a` | **0** | – | **0** | 7 |
| `1b` | **0** | – | **0** | 2 |
| `1c` | **0** | – | **0** | 3 |
| `2` | **0** | – | 1 (pre-existing, §3 blank line) | 4 |
| `3` | **0** | – | **0** | 18 |
| `4` | **0** | **0** | **0** | 11 |
| `5` | **0** | **0** | 1 (the `types` member order, §7.1) | 6 |

```text
-- batch 5
   tables.md:12: equal
   tables.md:13: equal
   tables.md:14: equal
   verdict.json:control_blocks: equal
   verdict.json:dual_phase: equal
   verdict.json:ledger: equal
compared 6 slice(s): 0 differ(ent), 0 missing
differences: 0 structure, 0 value, 0 missing
rc=0: every slice of this batch is equal
```

The §12 slice is 16 lines, §13 is 20 and §14 is 286 — the subsection count §14 prints on the clean tree
is five, because `undecodable` is zero there (§7.7).

### 2.2 The full run

```text
batches:   1a, 1b, 1c, 2, 3, 4, 5
-- batch 1a … -- batch 5
compared 51 slice(s): 0 differ(ent), 0 missing
differences: 0 structure, 0 value, 0 missing
rc=0: every slice of this batch is equal
```

51 = the 17 `tables.md` slices (the `preamble` plus the sixteen `## N.` sections) + the 34
`verdict.json` slices (the 14 top-level keys, with `metrics` split into its 21 members). The run also
performs `check_full_key_sets`, which is what makes "the 14 keys and the 21 metrics are all there and
the produced file has no key the reference does not" a criterion rather than a hope.

### 2.3 No placeholder survives

```text
$ rg -c 'TODO' /tmp/e4b5/out/tables.md      # rc=1: no match
$ rg -c '^## ' /tmp/e4b5/out/tables.md
16
```

The runner's own stdout no longer reports anything as unrendered: the `Pending` constant and the
summary line that printed it are gone (§7.6).

## 3. The module map

Every file carries the reference function beside it. Effective lines are
`benchmarks/WinForward.E2E/scripts/effective-lines.py`'s own count.

| file | effective lines | what it carries |
|---|---|---|
| `Tables/TableDual.cs` | 129 | §12 |
| `Tables/TableControl.cs` | 88 | §13 |
| `Tables/TableLedger.cs` | 187 | §14 intro, 14.1, 14.2, and the section's shared wording |
| `Tables/TableLedgerCross.cs` | 312 | §14.3, 14.4, 14.5, 14.6 |
| `Findings/LedgerDecodeTotals.cs` | 60 | `ledger_decode_totals` — the decode counters, keyed by ledger path |
| `Findings/LedgerViews.cs` | 261 (was 245) | `+` `Paths`/`Types`/`Labels` on the pass view |
| `Findings/LedgerDnsTotals.cs` | 101 (was 76) | `+` `LedgerPaths`; `DnsTotals` reachable for §14.4 and the verdict |
| `Findings/LedgerClientCounts.cs` | 161 (was 142) | `+` the partition's arm labels; `EndpointPartition` reachable |
| `Findings/DualFindings.cs` | 255 (was 245) | `+` `Passes` on the row summary; `Records`/`Summarise` reachable |
| `Checks/ControlDrift.cs` | 214 (was 211) | `+` one cached result per campaign |
| `Verdict/VerdictSections.cs` | 206 (was 125) | `+` `control_blocks`, `dual_phase`, `ledger` |
| `Tables/TablesWriter.cs` | 90 | §12/§13/§14 wired to their bodies |

The project is 74 files and 9562 effective lines by that counter; the largest is
`Tables/TableEnvironment.cs` at 321 and the largest this batch wrote is `TableLedgerCross.cs` at 312 —
every one under the 400-line rule (`effective-lines.py` over the four paths is silent, §8). §14 was one
file until it measured **478** effective lines: the split is at the subsection boundary, with the
section's entry point keeping the intro, the two tables it renders itself and the wording both halves
print.

## 4. §12, §13, §14 and the three keys

1. **§12 and the interference findings share one reading of the phase.** `TableDual` renders from
   `DualFindings.Records`/`Summarise` — the same records the `direct-lane-latency` finding is built
   from — so the table's verdict and the finding cannot disagree about which lane was slowest.
   `DualRowSummary.Passes` came back with its consumer (the section's `passes` column); the finding
   never read it.
2. **§13 prints the same statement the finding quotes.** Both read `ControlDrift.Compute`, which now
   caches its result per campaign in the `ConditionalWeakTable` shape `LedgerViewsBuilder` already
   uses: the section, the control-drift findings and `control_blocks` are one computation, so the five
   bootstraps are drawn once and the three cannot drift apart (§7.5).
3. **§13's two tables spell presence two ways, and both are the reference's.** The per-pass table says
   `present`/`MISSING`; the sentence a tree with no usable pair gets says `control-pre present` /
   `no control-pre`. The first run of the differ reported exactly that difference and the port follows
   the reference rather than unifying the wording.
4. **§14's two halves are one section.** `TableLedger` renders 14.1 and 14.2 and hands the campaign and
   the ledger views to `TableLedgerCross` for 14.3–14.6. The subsections are separate methods with the
   paragraph each one prints next to it, the way the reference's single `table_ledger` reads.
5. **The three keys are rendered from the same views the sections are.** `control_blocks` from
   `ControlDrift.Compute`, `dual_phase` from `DualFindings.Records`, `ledger` from
   `LedgerViewsBuilder.For` and `LedgerFindings.DnsTotals` — no second reading of any of them, so a
   section and its key cannot say different things.
6. **`ledger.dns_ports` publishes where each port's numbers came from.** `LedgerFindings.DnsTotals`
   gained the `ledger_paths` set and `summaries` count the verdict member carries, and a port the
   client alone knows stays a five-member entry exactly as the reference's `setdefault` leaves it.

## 5. The mutation trees

Ten mutants, each one edit on a fresh extraction of the frozen tarball at the hardcoded
`/tmp/wf-synth`. Both implementations are run on the mutated tree (`analyze.py --raw /tmp/wf-synth/raw`
and `WinForward.E2E.Analysis --raw /tmp/wf-synth/raw`) and the batch-5 slices are compared **against
their own mutant reference**: semantic mode (the criterion), semantic mode at `--tolerance 0`, and byte
mode. Each mutant is also compared against the **frozen golden**, which is how "this mutation moved the
compared surface at all" is told from "this mutation was vacuous".

Recipe: `/tmp/e4b5/run_mutants.py` (extract → mutate → run both → differ four times); shape
verification of each mutation against the reference's own output: `/tmp/e4b5/verify_mutants.py`;
summary `/tmp/e4b5/mutants/summary.tsv`.

| mutant | the edit | semantic | tol 0 | byte | vs golden |
|---|---|---|---|---|---|
| `ledger-missing` | both ledger files are gone | **0** | **0** | **0** | 2 |
| `ledger-truncated-bad-lines` | the main ledger gains two unparsable lines, the direct ledger loses its last line's tail | **0** | **0** | 1 → 0 (§7.1) | 1 |
| `ledger-endpoint-overlap` | every direct-lane census names the proxied lane's endpoint | **0** | **0** | 1 → 0 | 1 |
| `labels-collapsed-window-overlap` | one label for every record, and one row's window moved onto another's | **0** | **0** | 1 → 0 | 2 |
| `control-blocks-missing` | no pass ran either control block | **0** | **0** | 1 → 0 | 2 |
| `control-post-missing` | pass2 ran no control-post block | **0** | **0** | 1 → 0 | 2 |
| `control-order-broken` | pass1's control-post starts before every product row | **0** | **0** | 1 → 0 | 1 |
| `dual-directory-missing` | pass2 kept no `dual/` at the pass level or under its rows | **0** | **0** | 1 → 0 | 2 |
| `undecodable-present` | the main ledger could not decode datagrams | **0** | **0** | 1 → 0 | 1 |
| `dns-clientless` | the ledger reports a DNS listener on a port no client arm targeted | **0** | **0** | 1 → 0 | 1 |

**Ten of ten are rc=0 in the criterion and at zero tolerance**, and in nine of them byte mode's only
difference is the `types` member order — the same one-object reorder that makes the clean tree rc=0
(§7.1) makes every one of them rc=0 too. `ledger-missing` is byte rc=0 outright, because a tree with no
ledger renders no `types` object at all. Every mutant moved the surface (`vs golden` is 1 or 2; 2 is a
tree that *removed* something the golden has — a lane reading, a block, a row — which the differ reports
as a missing slice or row rather than as a differing one).

Each was also verified to have moved the campaign **in the shape it claims**, reading the reference's
own output (`/tmp/e4b5/verify_mutants.py`, one assertion per mutant):

- `ledger-missing` → §14 renders the no-ledger paragraph and `verdict.ledger.available` is `false`.
- `ledger-truncated-bad-lines` → §14.1's pass1 row reports **3** unparsable lines and **7305** records:
  the two garbage lines are counted, and the cut last line is not a record.
- `ledger-endpoint-overlap` → §14.3 names `192.168.77.2:51234` as a shared endpoint and marks the row
  `CORRECTNESS FAILURE: an endpoint served both a proxied and a direct-path window`.
- `labels-collapsed-window-overlap` → §14.1 says "because the ledger carries one label", the pass's
  label map is one key with 7306 records, and **39** §14.2 rows read a different ledger count than the
  frozen tree (the window-only selection is a different selection).
- `control-blocks-missing` → §13 renders the one sentence a tree with no usable pair gets, naming what
  each pass held, and `control_blocks` reports `available: false` with no comparisons and three per-pass
  entries.
- `control-post-missing` → §13's pass2 row is `| pass2 | present | MISSING | … |` and every comparison's
  `unavailable` map names that pass.
- `control-order-broken` → §13's pass1 bracketing cell reports the product row that "started after
  control-post".
- `dual-directory-missing` → pass2's five `dual_phase` rows carry no lane reading and §12's latency cells
  are built from 2 passes (`(n=2)`).
- `undecodable-present` → §14.6 appears, with `ledger-main.jsonl` at 174400 received / 11 undecodable
  (the running total's maximum, not the 7 the per-second record carries) and `all ledgers 338400 / 11`.
- `dns-clientless` → §14.4 prints the ledger-only port 40054 with `no client counterpart` in both checks
  and `ledger.dns_ports` carries it.

**No mutant made the reference crash** on the mutation as shipped, so no D20.1 exception was needed.
One *rejected* form did crash it and is registered in §7.4.

The tenth mutant is not decoration: it is the tree that found the batch's one real defect (§7.9).

## 6. Negative controls

Eleven controls, each one edit on a copy of the **clean produced** directory, compared with
`oracle-diff.py --mode semantic --batch 5 --cs-out <copy>` (`/tmp/e4b5/negative_controls.py`). Every one
is red where it must be; the base copy is green (rc=0).

| control | edit | rc | what the differ names |
|---|---|---|---|
| `dual-reason-to-number` | §12's `n/a (no dual phase: …)` → `0` | 1 | `[structure] …[row=control-pre].directLeak: expected "n/a (reason): …" vs actual 'number: 0'` |
| `dual-verdict-reworded` | §12's `CORRECTNESS FAILURE: directLeak=2` → the no-leak sentence | 1 | `[value] …[row=wf-fdd-opt].verdict` |
| `dual-passes-number` | §12's wf-aot-opt pass count `3` → `2` | 1 | `[value] …[row=wf-aot-opt].passes` |
| `control-present-to-missing` | §13's pass1 `present` → `MISSING` | 1 | `[value] tables.md:13.Per-pass control blocks and bracketing[pass=pass1].control-pre` |
| `control-unavailable-text` | §13's loss statement `0.8000` → `0.9000` | 1 | `[value] …[metric (BASE arm)=BASE loss lossRate].post vs pre` |
| `control-block-member` | `control_blocks.comparisons[0].statement` deleted | **2** | `[missing] verdict.json:control_blocks.comparisons[0].statement` |
| `ledger-check-to-marker` | §14.2's TCP check `ok` → `n/a` | 1 | `[structure] …[pass=pass1&run=control-post&arm=BASE].TCP check: expected 'text: ok' vs actual 'n/a: n/a'` |
| `ledger-census-number` | §14.2's ledger census `10400.0` → `104000.0` | 1 | `[value] …[pass=pass1&run=control-post&arm=BASE].ledger datagrams` |
| `ledger-attribution-dropped` | §14.1's attribution sentence replaced | 1 | `[value] tables.md:14.14.1 Provenance and attribution[pass=pass1].attribution used` |
| `ledger-pass-member` | `ledger.passes.pass1.attribution` deleted | **2** | `[missing] verdict.json:ledger.passes.pass1.attribution` |
| `dual-phase-row-dropped` | `dual_phase.rows` loses one element | 1 | `[structure] verdict.json:dual_phase.rows: expected '15 element(s)' vs actual '14 element(s)'` |

The structural directions the task names are covered on the batch's own surface: a **reason** turned
into a **number** (§12, §13-table), a **marker** turned into a **number** (§14.2's `ok` → `n/a`), a
**value** moved inside a cell (§12 passes, §13 statement, §14 census), a **record at the JSON layer**
turned into a different **kind** (a row dropped from an array), and two **deleted members** (rc=2, not a
tolerated value). Three of the controls run against text the *reference* also prints in a section batch
5 does not own (§0's findings quote §13's statement, §2 prints the same attribution sentence): those
edits are applied everywhere the text occurs, so the difference is guaranteed to be inside the compared
slice rather than only outside it — a first version of two of them edited only the §0/§2 copy and the
differ correctly stayed green, which is how the omission was found.

## 7. Deviations and observations

**7.1 `verdict.json`'s `ledger.passes[*].types` member order is a CPython set artefact.** The reference
builds the map as `{kind: count for kind in {record.get("type") for record in records}}`, so its member
order is the iteration order of a python `set` — a function of CPython's string hash and table probing,
not of the records. This port keeps first-occurrence order. Every reader sorts the keys (§14.1 sorts
them into the `record types` cell) and the semantic mode ignores object member order, so the two orders
are the same answer everywhere except a byte comparison of that one object: reordering *only* the
`types` objects of the produced file (no other change) turns byte mode's one remaining difference into
rc=0, which is the control the claim rests on. Reproducing the reference's order would mean porting
CPython's hash and set layout — machinery for a JSON object's key order in a mode D21 explicitly does
not use as a criterion.

**7.2 An empty datagram census prints `0`, not `0.0`.** `arm["udp_datagrams"]` is
`sum(endpoints.values())`, and python's `sum` over an empty mapping is the *integer* zero, so a window
whose census named no source prints `0` while a window that named one prints a float
(`10400.0`). `TableLedger.DatagramCount` is that distinction; byte mode found it (`| pass1 | proxifier |
IDLE | 1 | n/a | n/a | 0 | … |` against a produced `0.0`).

**7.3 A client-side datagram count of exactly zero has no band, and the reference crashes there.** §14.2
computes `band = datagram_tolerance(...) if datagrams else None` and then compares
`abs(observed - datagrams) <= band`: a `0.0` count is falsy, so `band` is `None` and the comparison
raises `TypeError`. Such a row is reachable — the row filter only needs *one* of the four counters to be
truthy, so an arm with client connections and a zero datagram count enters it. This port prints `n/a`
for that row rather than raising: a defined answer where the reference has none, which D20.1 sends to a
C#-side assertion. Registered for **E4-c** with D21.2 #2's empty-cell and zero-denominator work; no tree
in §5 reaches it, because reaching it two-sidedly would mean extending the reference.

**7.4 The inverse of §14.4's clientless row crashes the reference's findings.** A first form of the
`dns-clientless` mutant moved a listener's port so that a port *with* client counters had no summary in
the ledger; `analyze.py` then raises `KeyError: 'summaries'` in `ledger_findings`
(`analyze.py:2460`, reached from `collect_findings`) and writes neither file. The *shipped* mutant adds
a ledger-only port instead, which §14.4 renders as `no client counterpart` and the findings correctly
skip. Registered as a reference crash, not as a C#-side question.

**7.5 `ControlDrift.Compute` is cached per campaign.** The control-drift findings, §13 and
`verdict.json`'s `control_blocks` all want the same five bootstraps over the same per-pass values. The
cache is the `ConditionalWeakTable<CampaignModel, ControlDriftResult>` shape `LedgerViewsBuilder`
established, so the result is computed once and no two readers can disagree.

**7.6 The "not yet rendered" summary is gone.** `MetricCatalogue.Pending` and the runner's summary line
existed to say which batch still owed what; with all sixteen sections and fourteen keys rendered there
is nothing to say and the constant could only rot. The `<!-- TODO(batch N) -->` arm stays in
`TablesWriter`: it is the placeholder a declared-but-unimplemented section renders, and with every
section wired no output contains it (§2.3).

**7.7 §14.6 is the reference's own subsection, and the clean tree prints none of it.** `undecodable` is
zero on the frozen tree, so the golden's §14 has five subsections; the sixth appears only when the
target could not decode something, which is why the `undecodable-present` mutant exists (§5). This is
**not** E4-c's `### 14.7` truncation caveat (D20.7/C23): that subsection is a C#-side disclosure with
its own negative control and is not implemented here.

**7.8 §14.5's client expectation is read out of the reliability arm's own `expected` block.** The
comparison is the reference's: the target's verdict counts against what the client said it expected, and
a metric the arm does not publish is `n/a` rather than a zero — the frozen tree's `partialFin` and
`halfClose` columns are `n/a` on the client side for exactly that reason.

**7.9 The §13 unavailable branch printed a type name, and only a mutant could see it.** `TableControl`'s
first form ended with `string.Join('\n', Unavailable(drift.PerPass), string.Empty)` where `Unavailable`
returned a `List<string>`: the call binds to `params object?[]`, so the section rendered
`System.Collections.Generic.List\`1[System.String]`. The frozen tree always has both blocks, so every
criterion this batch has — the batch's three modes, the full run, all nine original mutants — stayed
green while the branch was wrong. The `control-blocks-missing` mutant (no pass ran either block) was
built for exactly that branch and turned it red; the method now returns the sentence as a string and the
mutant is rc=0 in all three modes. It is the batch's answer to "the clean tree cannot reach a rule" —
the same role the empty cell and the zero denominator play for E4-c (D21.2 #2).

## 8. The gates

All six, on the final tree, in the repository's own order (`/tmp/e4b5/gates/`):

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** (`build.log`) |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed, 0 skipped** in 14 assemblies, rc=0 (`test-final.log`) |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **0 bytes of output**, rc=0 |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`, on a **cleared** `InspectCode`/`Daemon`/`Transient` cache | **`<Issue>` 0, `<IssueType>` 0**, no `CSharpErrors` (`inspectcode-final.xml`) |
| lines | `effective-lines.py` over the four paths | **0 bytes of output**, rc=0 (74 files / 9562 effective lines; largest 321, largest of this batch 312) |
| oracle | §2 | batch 5 rc=0 (6/6), tolerance 0 rc=0, full run rc=0 (51/51), `1a/1b/1c/2/3/4` rc=0 |

The format gate needed **no** fix in this batch's files: `dotnet format --verify-no-changes` was empty
on its first run over the final tree. The first **cold** inspector round of the batch reported **5**
findings, all in this batch's new files and all real: two `MergeIntoPattern` on §12's two lifted
comparisons (`excess is { } x && x > Thresholds.Latency` wants to be the lifted comparison itself,
which is also what the reference's `is not None and … > …` means for a `double?`) and three
`MemberCanBePrivate.Global` (`Unavailable`, `NoAccounting` and `NonZero` are read only inside
`TableLedger`; the three constants its sibling `TableLedgerCross` does read stay `internal`). All five
were fixed rather than suppressed, and the **second cold run reports `<Issue>` 0**. The tenth
mutant's defect (§7.9) was found after that round, so the inspector was run **a third time** from a
cleared cache on the final tree — `inspectcode-final.xml` — and the test suite was re-run with it.

Every gate was re-run on the **final** tree after those five fixes and again after the tenth mutant's
fix (§7.9) — the whole criterion in its three modes, the ten mutation trees with their shape
assertions, the eleven controls, the test suite and a third cold inspector round — so the table above is
the final tree's own.

Frozen artifacts were not touched: `git status --short benchmarks/WinForward.E2E.Analysis/verification`
and `git diff HEAD -- …/verification` are both empty (tarball sha256
`723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902`, `py-tables.md`
`8011d05bdad5dd0b2232a1f9e0bd269a637ad7995299d351dae71a945104ebbc`, `py-verdict.json`
`2e0e64dada67524502cde8958dafdf6f90b1e54400015dacd89c921d8483c55d`), and `.editorconfig` is unchanged
(this batch adds no suppression of its own). Every mutation ran on a fresh extraction of the tarball at
`/tmp/wf-synth`.

## 9. For check

1. **`--tolerance 0` is rc=0 for batch 5** (§2.1), so a later change that turns it red is an arithmetic
   change rather than a formatting question. The three values that make it work are the control
   bootstrap (unchanged since batch 2), the §12 medians over per-pass lists, and the §14 counts, which
   are integers in both implementations.
2. **`types` is the one byte-mode difference** (§7.1) and the reorder control is the evidence. Confirm
   the semantic mode stays the criterion and that no one "fixes" the order by hard-coding the golden's.
3. **§14.2's `0` vs `0.0`** (§7.2) is a python `sum([])` artefact rather than a formatting choice;
   the same distinction will matter for any future count that can be empty.
4. **The two unreachable-in-practice rows** (§7.3 the zero datagram count, §7.4 the reference's
   `KeyError`) belong with D21.2 #2 in E4-c: the zero-denominator batch is where an empty cell, a MIX
   fallback and a compensated sum get their assertions, and both of these are the same class of
   "the reference has no answer here, so the C# side must state one".
5. **The `Pending` mechanism is gone on purpose** (§7.6). If E4-c wants the batch boundary visible in
   the output again, the `<!-- TODO(batch N) -->` arm is still there; the stdout line is not coming
   back, because no batch is outstanding.

## 10. Independent check (`trellis-check`)

Every claim above was re-derived from the tree, not read off this file. The criterion, the gates and
the mutants were re-run; the two reference crashes were reproduced; and the check built **eleven
mutants of its own** (a different set from §5's ten). One real defect came out of that and is fixed.

### 10.1 The criterion on the check's own runs

| run | result |
|---|---|
| `--mode semantic` (no `--batch`) | **rc=0**, 51/51 slices (17 `tables.md` + 34 `verdict.json`) |
| `--mode semantic --tolerance 0` (no `--batch`) | **rc=0**, 51/51 |
| `--mode semantic --batch 1a,1b,1c,2,3,4` | **rc=0**, 45/45 |
| `--mode byte --batch 5` | **rc=1**, one slice (`verdict.json:ledger`), first difference line 12 inside `types`; **rc=0 for all six slices** once *only* the 12 lines of the three `types` objects are reordered (the re-serialization was checked byte-faithful first, so no other byte moved) |
| `--mode byte --batch 1a,1b,1c,3,4` | **rc=0**; `--batch 2` rc=1 on the pre-existing §3 blank line (D21.2 #1, E4-c) |

No `TODO` survives (`rg -c TODO` over the produced `tables.md` and `verdict.json` is rc=1, `rg -c '^## '`
is 16), the analyzer's stdout carries no "not yet rendered" line, and the produced artifacts are
byte-identical across two independent runs (`tables.md` `f6fd30d1…`, `verdict.json` `aef4683a…`).

### 10.2 `types` is a CPython set artefact, and it is accepted

`analyze.py:2229` builds the map as `{kind: sum(...) for kind in {record.get("type") for record in records}}`.
The check recomputed that set from the frozen tree's 7306 ledger records on CPython 3.14.7: the
iteration order is `targetSummary, tcp, udpSummary, tcpSummary, dnsSummary, error`, **exactly** the
golden's order in all three passes, and the counts are equal as maps. The port's
`LedgerViews.TypesOf` is a `GroupBy` (first-occurrence order) with no literal order anywhere.
Semantic mode is rc=0 with either order. **Accepted**: D21 makes semantic the criterion, byte mode is
explicitly not one, and reproducing the order would mean porting CPython's string hash and set probing
for an object whose member order no reader depends on (§14.1 sorts the keys).

### 10.3 The defect this check found and fixed: `ledger_paths` was published unsorted

**Symptom.** `verdict.json`'s `ledger.dns_ports[port].ledger_paths` came out in *discovery* order while
the reference publishes it sorted (`sorted(value) if isinstance(value, set)`, `analyze.py:5721–5722`). The
frozen tree hides it because every DNS port is reported by exactly one ledger.

**Probe.** A fresh extraction with the six `dnsSummary` records copied into `ledger-direct.jsonl` (so
ports 53 and 40053 are credited to two ledgers, discovered main→direct):

```text
reference ledger_paths = ['/tmp/wf-synth/ledger-direct.jsonl', '/tmp/wf-synth/ledger-main.jsonl']
produced  ledger_paths = ['/tmp/wf-synth/ledger-main.jsonl', '/tmp/wf-synth/ledger-direct.jsonl']
differ --mode semantic rc=1  →  [value] verdict.json:ledger.dns_ports.53.ledger_paths[0]: expected
                               '/tmp/wf-synth/ledger-direct.jsonl' vs actual '/tmp/wf-synth/ledger-main.jsonl'
```

**Fix.** `Findings/LedgerDnsTotals.cs` now keeps `LedgerPaths` deduplicated *and ordinal-sorted* as it
accumulates (a `BinarySearch`/`Insert` instead of `Contains`/`Add`), which is the reference's own
contract for that member; the property's doc comment says so. Nothing else reads the list.

**Re-checked.** The probe is semantic rc=0 and tolerance-0 rc=0 after the fix (byte mode still shows
only the `types` order). The fix is **inert on the frozen tree**: `tables.md` `f6fd30d1…` and
`verdict.json` `aef4683a…` are unchanged, and the whole §10.1 criterion was re-run on the new tree.
`dns-summaries-two-ledgers` is now one of the eleven mutants (§10.5), so the shape is a standing guard.

### 10.4 §13's unavailable branch, reproduced both ways

A tree with every pass's `control-pre` and `control-post` removed (post-fix) makes both sides print

```text
n/a (both control blocks are needed in at least one pass; the tree holds pass1: no control-pre, no control-post, …)
```

with `control_blocks.available=false`, no comparisons and three `per_pass` entries; semantic and
tolerance-0 are rc=0. Restoring the pre-fix shape (a `List<string>`-returning helper passed to
`string.Join('\n', …, string.Empty)`) in a temporary edit made §13 print
``System.Collections.Generic.List`1[System.String]`` and turned semantic rc=1 — the documented
mechanism, reproduced end to end. The file was restored byte-for-byte (`sha256 2815435b…` before and
after, `git status` unchanged) and the tree rebuilt.

**Sibling scan.** The whole solution has exactly three `string.Join` calls with three or more arguments:
`TableControl.cs:57` (the fixed one — the helper returns a `string`), `TableLedger.cs:74` (`Unavailable`
is a `const string`) and `TableDns.cs:61` (four string-typed arguments). Every other `Join` is the
two-argument `IEnumerable<string>` overload, and no list-returning helper in this batch is interpolated
or passed to `Join` anywhere else. No other branch of that class exists.

### 10.5 Eleven mutants of the check's own

Each is one edit on a fresh extraction of the frozen tarball; both sides run on the mutated tree and
the six batch-5 slices are compared against **their own mutant reference**. All eleven are rc=0 in the
criterion and at `--tolerance 0`; in all eleven byte mode's only difference is the `types` order (rc=0
after reordering it), and all eleven moved the compared surface against the frozen golden (rc=1, or
rc=2 where the mutation removes a record).

| mutant | the edit | vs frozen golden |
|---|---|---|
| `control-blocks-swapped` | every pass's two control directories exchanged | 1 |
| `dual-lanes-swapped` | every pass's and every row's `dual/direct` ↔ `dual/proxied` exchanged | 1 |
| `ledger-tcp-records-dropped` | the main ledger keeps no per-connection `tcp` record | 2 |
| `ledger-labels-prefixed` | every ledger label renamed, so no label matches a run label | 2 |
| `one-row-dual-missing-everywhere` | `wf-fdd-opt` lost its `dual/` in all three passes | 2 |
| `dns-summaries-duplicated` | every `dnsSummary` record appears twice | 1 |
| `endpoint-census-overflow` | a window's census emptied and `sourceOverflow=2` per record | 1 |
| `tcp-verdicts-shifted` | every `tcp` record's verdict rewritten to `reset` | 1 |
| `decode-totals-conflict` | per-second records raise both decode counters, the closing block lowers them | 1 |
| `dns-summaries-two-ledgers` | the direct ledger also reports the main ledger's DNS ports | 1 |
| `dual-pass-level-offer` | `pass3/proxifier` lost its own `dual/` while the pass-level `dual/` stays | 2 |

Each mutation was asserted to have moved the campaign in the shape it claims, read out of the
*reference's* own output: the loss comparison inverts (`pre=0.8`, `post=0.0`); `pass1/wf-aot-opt`'s
lanes exchange (`651.373/646.39`); `pass1`'s `types` keeps only the direct ledger's 480 `tcp` records;
the attribution falls back to the window ("the ledger carries no label"); the two rows lose both lane
readings in all three passes and no other row does; port 53 reports `summaries=6`, `ledger_udp=29280`;
four `ledger-source-overflow` findings name `pass1/proxifier …` with counts 30/40 and §14.2's cell
reads `30`; §14.5's pass1 row reads target `reset=5161` against client-expected `185`; §14.6 appears
with the per-key maximum (`999999` received, not the closing block's `100000`); both DNS ports credit
two ledgers in sorted order; and `pass3/proxifier` inherits the pass-level (proxybridge) lanes
(`667.463/665.786`) while the rows after the owner keep their own.

§9.3's `0` vs `0.0` is covered too: the emptied census prints `0` (not `0.0`) with `—` for the
endpoints on **both** sides.

**Restoration hashes.** The extraction root's manifest (`path sha256` over all files, sorted) is
`36307b467b873544462b2a11bf61b07bbadd7d9ad6213f3dbe1bf7e540ad9a26` (396 files) pristine; each mutant's
post-edit manifest is distinct (e.g. `dns-summaries-two-ledgers` `6a543594…`, `dual-pass-level-offer`
`de93670b…`, `control-blocks-swapped` `cf2fe1f1…`), and re-extracting returns the pristine hash. No
mutant can leak into the next, and the frozen tarball is untouched.

### 10.6 The two reference crashes, reproduced

| input | reference | the C# side |
|---|---|---|
| `pass1/wf-aot-opt`'s LAT arm publishes `udp.sent = 0` | rc=1, `TypeError: '<=' not supported between instances of 'float' and 'NoneType'` at `analyze.py:5037` (`table_ledger`); **neither file written** | rc=0; the §14.2 row reads `… | 600.0 | 0 | n/a | 1 | …` — a defined `n/a` where the reference has no answer |
| the ledger's `dnsSummary` on 40053 moved to 40059 (clients still target 40053) | rc=1, `KeyError: 'summaries'` at `analyze.py:2460` (`ledger_findings`); **neither file written** | rc=0; §14.4 prints `| all passes | 40053 | 0 | 14400 | MISMATCH | 0 | 3600 | MISMATCH |` and `| all passes | 40059 | 14400 | 0 | no client counterpart | …`, `dns_ports["40053"]` stays the five-member entry, and the mismatch findings carry `0 shutdown summaries` |

Both C# answers are defensible: a client-only port is a real mismatch and a zero datagram count is
`n/a`, and neither invents a number the reference would have printed. **Registered for E4-c**: §7.3 and
§7.4 of this file, and the tracked `research/semantic-fixes/index.jsonl`'s `E4-b5` entry (the only line
that entry added is its own). One wording nuance for E4-c: §7.4 says "not as a C#-side question", but the
C# side *does* choose a rendering there (`summaries=0`, a `MISMATCH` row) — E4-c should keep an assertion
on it rather than treat it as pure reference history.

### 10.7 The gates, re-run on the checked tree

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed, 0 skipped**, 14 assemblies, rc=0 |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0, **0 bytes** |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`, `InspectCode`/`Transient`/`Daemon` caches and `/tmp/JB` cleared | 1703 entries analyzed/inspected, **`<Issues />` empty (0 issues)**, `<IssueTypes />` empty, no `CSharpErrors` |
| lines | `effective-lines.py` over the four paths | **0 bytes**, rc=0; 74 files, largest `TableEnvironment.cs` 321, largest of this batch `TableLedgerCross.cs` 312 (`TableLedger.cs` 187, `TableDual.cs` 129, `LedgerDnsTotals.cs` **102** after §10.3's fix, `TableControl.cs` 88, `LedgerDecodeTotals.cs` 60) |
| oracle | §10.1 | as tabulated |

No suppression was added: `.editorconfig` is unchanged, and this batch's files carry no `#pragma` and
no `ReSharper`/`SuppressMessage` attribute (the four `S1244` pragmas in the tree are the committed
batch-4 ones). Frozen artifacts are untouched (`git status --short …/verification` empty; tarball
`723b7378…`, `py-tables.md` `8011d05b…`, `py-verdict.json` `2e0e64da…`).

### 10.8 What is left for E4-c

1. **§14.7** the truncation caveat (D20.7/C23) — not implemented here, as the batch says.
2. **§7.3's zero datagram count** as a C#-side assertion, with D21.2 #2's empty cell and zero
   denominator; **§10.6's second row** wants its own assertion too.
3. **D21.2 #1** the byte-mode §3 blank line (batch 2), still the produced file's only other byte
   difference anywhere.
4. **The `types` member order** stays accepted (§10.2); if E4-c ever wants byte-mode batch 5 green, that
   is the one object to reorder and nothing else.
