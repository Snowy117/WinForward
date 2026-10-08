# E4-a2 — the differ compares meaning (semantic mode)

Batch E4-a2 of `.trellis/tasks/10-07-e2e-e4-analyzer`: turn
`benchmarks/WinForward.E2E/scripts/oracle-diff.py` from a byte comparison into a **semantic** one.
Scope is the differ only — no analyzer, no frozen artifact, and no `BATCH_SECTIONS` change.

Authority: `design-decisions.md` **D21** (the C# rewrite exists for maintainability, not for
byte-identical output; `--mode semantic` by default, `--mode byte` kept for the structure surface),
**D20.2**/**D20.3** (slicing, batch ownership and the three exit states are *kept*; the frozen tree
and the `--raw` path are unchanged) and **D20.7/D20.8** (the batch table and `metrics` splitting),
plus the E4-a2 ruling that numeric cells are compared by **one unit of the printed last digit**
rather than by D21.1's relative ε.

**Result.** `<differ> --mode semantic` is the default; the golden against itself is **rc=0** in both
modes, `--batch 1a` and `--batch 1b` are **rc=0** in both modes, every other batch is still **rc=2**,
and the 43-case self-check matrix (43/43) covers the tolerance, the structural assertions and the
three exit states. `verification/**` was neither re-frozen nor regenerated: `git status` for that
directory is empty. The analyzer was not touched either, so all six gates are the ones the previous
batch already ran, re-run on this tree.

| file | sha256 | effective lines |
|---|---|---|
| `benchmarks/WinForward.E2E/scripts/oracle-diff.py` | `161fca21cbe6727e7771e4c6ee4f6410141e69248c5603581881a72641012927` | 1265 physical (Python, so outside the 400-line `.cs` rule) |
| the same file, after the check round's three exception-exit fixes | `a02819a66f3b470e113da7a587002258264f6f1b857783c1a569dd9b643c4bf8` | 1274 physical (same comparison semantics; see `E4a2-check.md` §3) |

The first hash is the state both the self-check matrix and the gates' oracle half were run against:
`/tmp/e4a2/gates/differ.sha256` holds the same digest, and the six oracle invocations were re-run
after the last edit to the file (a docstring sentence about the identifier rule) — all six rc=0. The
check round re-ran the matrix, the seven batches and the six gates against the second hash
(`E4a2-check.md` §1/§3/§5): 43/43 identical, the batch state unchanged. The
five C# gates do not read this file at all: build, test, format, inspectcode and `effective-lines.py`
walk `.cs` sources, and none of them was touched by this batch.

## 1. What is asserted and what is tolerated, item by item

`--mode semantic` (default) parses both sides and compares:

| what | semantic mode | byte mode |
|---|---|---|
| the 16 `## N.` headings: the number set, the order, each exactly once | **structure** (exact) | slice existence + slice text |
| a section's title text (`## 9. …`) | **structure**, whitespace/typography folded | slice text |
| a section's `### …` sub-headings | **structure** (block kind + folded text) | slice text |
| a section's paragraphs | **value**, wording token-for-token with the numbers inside under the numeric rule | slice text |
| the block sequence of a section (prose vs table, and how many) | **structure** | slice text |
| table header cell names | **structure** (folded, exact) | slice text |
| the row identity columns of a table | derived from the reference: the shortest unique leading run, capped by the leading run of dimension names | not used; position is everything |
| which rows exist | row **sets** must be equal: a row the produced lacks is **missing** (rc=2), a row it adds is **structure** (rc=1); order is not compared | order is compared |
| every row's cell count | **structure** — this is what keeps the reference's malformed 11-cell row (4 tables in §5) visible | slice text |
| every cell's category (`empty` / `n/a` / `n/a (reason)` / `< 3/n` / number / text) | **structure** — `n/a` ↔ number is rc=1, never a tolerated value | slice text |
| a numeric cell, composite cells included (`median [p25–p75] unit (n=K)`, `< 3/n = x %`) | **value**; the numbers are split out and compared by one unit of the reference's printed last digit; brackets, the en dash and the spacing are typography | slice text |
| an `n/a (reason)` or free-text cell | **value**; decoded, whitespace-dropped tokens compared exactly, the numbers inside under the numeric rule, markers through the equivalence table (§2.3) | slice text |
| `verdict.json` object structure | parsed; **key order ignored**; a missing member/key is **missing** (rc=2), an extra one is **structure** (rc=1) | canonical re-serialization of each slice (member order inside a slice counts; top-level order does not) |
| `verdict.json` numbers | parsed to values, then the same last-digit rule (`0.8000` ≡ `0.8`, `647.575` vs `648.0` fails) | text |
| `verdict.json` strings | decoded, then token-exact; the *wording* keys (`detail`, `status_reason`, `notes`, `reason`) tolerate the numbers inside the sentence | text |
| arrays | element-wise **in order**, so `passes`/`rows` are ordered arrays | text |
| the 14 top-level verdict keys and the 21 `metrics` members | **structure** when every batch is requested: missing → rc=2, extra → rc=1; `check_coverage` also refuses a `metrics` member no batch owns and a slice owned twice | the same two checks |
| `bootstrap` / `thresholds` | their own rule: the same member keys, `resamples`/`seed`/`min_passes` must be **integers** and equal, `thresholds` members must be strings and **token-exact** (`5 %` ≠ `6 %`, `5 %` = `5%`), `bootstrap`'s other strings tolerate the numbers inside | member order inside the slice counts |
| artifact form (produced side only) | **structure**: UTF-8 without BOM, no `\r`, exactly one trailing `\n` | the same check |
| `plots/SKIPPED.md` | must exist (else rc=2) and equal the frozen `verification/plots-SKIPPED.md` (else **structure** rc=1) | the same check |
| slice existence | unchanged D20.2 mechanism: an owned slice (or a whole file) that is not there is **rc=2** | unchanged |

## 2. The rules that needed a decision

### 2.1 The numeric rule

`unit = 10 ** (exponent of the reference's printed form)`, and the produced value passes when
`|expected − produced| <= --tolerance × unit` (`--tolerance` defaults to `1`; `0` means exact).
Integers are the exception that keeps the oracle honest: when **both** sides printed a whole number,
the comparison is exact — printing an integer loses no information, so an off-by-one datagram counter
must never pass. When the reference printed a whole number but the produced printed a fraction, the
reference's own rounding is the latitude (a median rounded to 0 decimals). When the produced printed
a whole number where the reference printed a fraction, the produced dropped information and only
equality passes.

| case | verdict | why |
|---|---|---|
| `453.8` vs `453.9` | equal | one unit of the last printed digit (1e-1) |
| `453.8` vs `455.0` | differs | 12 units |
| `6000` vs `6001` | differs | both printed whole |
| `6000` vs `6001.0` | differs | the produced's value is a whole number too |
| `20` vs `20.4` | equal | the reference rounded a fractional value to no decimals |
| `0.0004` vs `0` | differs | the produced dropped the fraction |
| `0.0625` vs `0.062` | equal | the produced printed fewer digits of the same value; the coarser print bounds the comparison |
| `1e-06` vs `0.000001` | equal | same value, different spelling |
| `8.000e-03` vs `0.008` | equal | same value, different spelling |
| `n=2 of 3` vs `n=2 of 4` | differs | both whole |
| `192.168.77.2:51234` vs `…:51235` | differs | a dotted run of two dots or more is an **identifier**, not a measurement (§2.1.1) |

`--tolerance 0` turns the only tolerance off: the `within` mutant (§3) is rc=0 at the default and
rc=1 at `--tolerance 0`, which is the negative control that the tolerance is what lets it pass.

#### 2.1.1 Identifiers are not measurements

A token with two dots or more (`192.168.77.2`, `1.0.0.0`) is compared as an identifier; a cell whose
numbers leave a `.` or a `:` behind (`192.168.77.2:51234`, `2026-10-06T08:34:30.000000+00:00`) is
classified as text rather than as a number. Both were added after measuring that the plain numeric
rule tolerated an endpoint whose last octet or port digit moved by one — the endpoint is a fact about
the campaign, not a reprinted measurement.

### 2.2 Row identity

The key is the **shortest leading run of columns that is unique in the table**, capped by the leading
run of *dimension* columns (`pass`, `row`, `arm`, `run`, `kind`, `scope`, `item`, `metric`, `file`,
`label`, `counter`, `assertion`, `dns port`, `target clean`). On the frozen golden that yields two
columns for most tables, one for the row-per-table ones, three for §14.2, and the whole row only for
the tables that genuinely repeat rows:

| table | rows | key width | why |
|---|---|---|---|
| §0.4 `All findings by severity` (harness-error) | 8 | 2 (`kind`, `scope`) | three `lane-witness-zero` rows share `kind` + `scope` and differ in `detail` |
| §2 `Per-run metadata` | 69 | 3 (`pass`, `run`, `label`) | the same run is listed three times, identically |
| §14.2 `Client vs target accounting, per (pass, run, arm)` | 219 | 3 (`pass`, `run`, `arm`) | three duplicated rows per pass |

Inside a key group the rows are paired exact-first and best-match-second, so:

* a value changed inside one of the duplicated §14.2 rows is reported as a **value** at that row's key
  (`tables.md:14.14.2 …[pass=pass1&run=control-post&arm=BASE].ledger datagrams: expected '10400.0' vs
  actual '10100.0'`, rc=1) rather than as a missing + extra pair;
* deleting a row is **missing** (rc=2), duplicating one is **structure** (rc=1);
* swapping two rows in a table — the duplicate-bearing ones included — is rc=0 in semantic mode and
  rc=1 in byte mode.

### 2.3 Markers

`FAIL`/`failed`, `n/a (not comparable)`, `not carried (UDP bypassed)`, `not measured in this row`, and
the em dash/hyphen placeholder all map to an explicit class. A cell that is a marker on one side and
not on the other is **always** a difference (measured: `—` → `n/a` is a `structure` difference),
which is the "not silently equal to arbitrary text" rule. Everything else is the token rule of §1:
the words must match, the numbers inside are reprinted values and follow §2.1.

### 2.4 Why `thresholds` is token-exact

The thresholds are declared constants, so `5 %` vs `6 %` must fail. They are compared with
`text_identical` (decoded, whitespace-dropped tokens, no numeric tolerance), which still lets `5 %`
and `5%` agree. `bootstrap`'s prose members (`method`, `resampling_unit`) use the tolerant text rule,
because they quote numbers.

## 3. Self-check matrix

43 mutants, each a copy of the frozen golden under `/tmp/e4a2/selfcheck/<case>` with one edit, run as
`python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --cs-out <case> [--mode …]`; the golden
itself is used as the produced side so every table and every key is exercised. Logs:
`/tmp/e4a2/selfcheck-logs/<case>.log`; the harness is `/tmp/e4a2/selfcheck.py`; the full transcript is
`/tmp/e4a2/selfcheck-matrix.txt`.

| case | mutation | semantic rc | byte rc |
|---|---|---|---|
| `baseline` | none (the golden against itself) | **0** | **0** |
| `within` | a `p50Us` cell `453.8` → `453.9` (one unit) | **0** | 1 |
| `within-zero` | the same mutant with `--tolerance 0` | 1 | — |
| `composite` | one number inside `[450.7–460.2]` +0.1 | **0** | 1 |
| `number-in-text` | `0 direct-path interceptions observed` → `0.0 …` | **0** | 1 |
| `float-within` | `proxied_latency_p50_us` 647.575 → 647.5755 | **0** | 1 |
| `marker` | a cell's `—` → `-` | **0** | — |
| `escape` | `Holm\u2013Bonferroni` → the literal en dash | **0** | **0** (see §7.4) |
| `beyond` | the same `p50Us` cell `453.8` → `455.0` | 1 (value) | 1 |
| `float-beyond` | `proxied_latency_p50_us` 647.575 → 648.0 | 1 (value) | — |
| `counter` | `< 3/6000` → `< 3/6001` | 1 (value) | — |
| `prose` | a paragraph's `6 correctness-failure` → `7` | 1 (value) | — |
| `reason` | `n/a (no denominator)` → `n/a (no denominator at all)` | 1 (value) | — |
| `header` | a §5 header column `count` → `samples` | 1 (structure) | 1 |
| `cells` | one cell dropped from the reference's 11-cell row | 1 (structure) | 1 |
| `na` | an `n/a` cell → `0` | 1 (structure) | — |
| `marker-wrong` | a placeholder `—` → `n/a` | 1 (structure) | — |
| `extra-row` | a table row duplicated | 1 (structure) | — |
| `title` | `## 9. DNS comparability detail` → `## 9. DNS results` | 1 (structure) | — |
| `subheading` | a `### ` table heading reworded | 1 (structure) | — |
| `threshold` | `"latency": "5 %"` → `"6 %"` | 1 (value) | — |
| `resamples` | `"resamples": 10000` → `10001` | 1 (value) | — |
| `float-resamples` | `"resamples": 10000` → `10000.0` | 1 (structure) | — |
| `extra-key` | an unknown top-level key added | 1 (structure) | — |
| `plots` | `plots/SKIPPED.md` reworded | 1 (structure) | — |
| `bom` / `crlf` / `two-newlines` | a BOM / CRLF / a second trailing newline | 1 (structure) each | — |
| `roworder` | two table rows swapped | **0** | 1 |
| `keyorder` | two `bootstrap` members swapped | **0** | 1 |
| `missing-key` | `row_profiles` deleted | **2** | — |
| `missing-metric` | one `metrics` member deleted | **2** | — |
| `missing-row` | a table row deleted | **2** | — |
| `missing-section` | a `## N.` heading deleted | **2** | — |

The matrix is checked by the harness itself (`expected` map) and ends in `SELF-CHECK OK`; the run
after the last code change is the one recorded in `/tmp/e4a2/selfcheck-matrix.txt`.

## 4. The batch state on the frozen tree

End to end (`--cs-out` omitted, so the tree is extracted and the analyzer is run), both modes:

| batch | semantic | byte |
|---|---|---|
| `1a` | **0** (7/7 slices) | **0** |
| `1b` | **0** (2/2) | **0** |
| `1c` | 2 (2 differ, 1 missing) | 2 |
| `2` | 2 (2 differ, 2 missing) | 2 |
| `3` | 2 (3 differ, 15 missing) | 2 |
| `4` | 2 (5 differ, 6 missing) | 2 |
| `5` | 2 (3 differ, 3 missing) | 2 |
| default (all) | 2 — 51 slices, 27 missing, 7 missing keys | 2 |

`--batch 1` (the alias for `1a,1b,1c`) is rc=2 as before, and the skeleton's TODO bodies show up as
`structure` (block counts) plus `value` (the placeholder text) in the batches that own them — the
same state b1a/b1b left behind, now with the categories spelled out.

The three-state mechanism itself is unchanged: every "nothing to compare" path (a bad tree, a missing
file, an analyzer that did not run, a JSON file that does not parse, a `BATCH_SECTIONS` table that
does not add up, a slice that should exist and does not) still leaves through `fail()` with rc=2 —
including the new ones (§5.3).

## 5. The report and the exit codes

### 5.1 Shape

Every difference is one line, `路径: 期望值 vs 实际值`, tagged with its category, and the run ends
with the per-category counts:

```text
-- differences (3)
   [structure] tables.md:headings: expected '0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15' vs actual '0 1 2 3 4 5 6 7 8 10 11 12 13 14 15'
   [structure] tables.md:8.blocks: expected '5 block(s)' vs actual '6 block(s)'
   [value] tables.md:8.prose[4]: expected '`MIX` has no `supplied` counter …' (840 chars) vs actual '…'
compared 51 slice(s): 1 differ(ent), 1 missing
differences: 2 structure, 1 value, 0 missing
rc=2: something that should exist does not (1 slice(s), 0 difference(s))
```

Short samples, one per category:

```text
   [value] tables.md:5.`tcp-connect`[row=wf-aot-opt&arm=LAT].p50Us: expected '453.8 [450.7–460.2] us (n=3)' vs actual '455.0 [450.7–460.2] us (n=3)'
   [value] verdict.json:dual_phase.rows[0].proxied_latency_p50_us: expected '647.575' vs actual '648.0'
   [structure] tables.md:5.`tcp-connect`.header: expected '| row | arm | count | … |' vs actual '| row | arm | samples | … |'
   [structure] tables.md:5.`tcp-connect`[row=wf-aot-opt&arm=IDLE].cells: expected '11 cell(s)' vs actual '10 cell(s)'
   [structure] tables.md:2.Product process sampling[pass=pass1&row=control-post].absent ticks: expected 'n/a: n/a' vs actual 'number: 0'
   [missing] verdict.json:row_profiles: expected '{9 key(s)}' vs actual <absent>
   [missing] tables.md:2.Declared loss threshold (`window`, ms) per arm[arm=any arm (client-side gate)]: expected 'any arm … | —' vs actual <absent>
```

### 5.2 Exit codes

* **0** — every owned slice exists and is equal, and no file-level difference was found.
* **1** — every owned slice exists, but a `structure` difference or an out-of-tolerance `value`
  difference was found. The tail line says `rc=1: every slice exists but N differ(s)`, or
  `rc=1: every slice is equal but the file's own structure differs` when the difference is in the
  global structure (a title, a top-level key, `plots/SKIPPED.md`, the artifact form).
* **2** — something that should exist does not, or a file is not parsable. Missing slices and
  missing rows (inside a slice) count, missing/extra top-level keys count, so rc=2 wins over rc=1.

### 5.3 Infrastructure controls (process level)

| what was broken | result |
|---|---|
| a `metrics` member removed from `BATCH_SECTIONS` | rc=2, `BATCH_SECTIONS does not own every metrics member: ['thru.goodputMbps']` |
| a `tables.md` section owned by two batches | rc=2, `tables.md:7 is owned by both batch 3 and 4` |
| a batch whose two lists are empty | rc=2, `batch 1b owns no slice; a batch that compares nothing would pass vacuously` |
| `--batch 9` | rc=2, `unknown batch '9'; known: 1a, 1b, 1c, 2, 3, 4, 5` |
| the produced directory without `tables.md` | rc=2, `MISSING (produced file absent)` |
| the produced `verdict.json` truncated (`{oops`) | rc=2, `not valid JSON: Expecting property name …` |
| the produced `verdict.json` holding `[]` | rc=2, `the top level is list, not an object` |
| the produced directory without `plots/SKIPPED.md` | rc=2 (1 missing difference) |
| the produced `tables.md` with no `## N.` heading | rc=2 (every table slice missing) |
| the frozen `plots-SKIPPED.md` missing | rc=2, `… the produced plots/SKIPPED.md cannot be judged` (this one was a real hole: it used to end in `FileNotFoundError`, which a process exits as 1 — the code that means "the content differs") |

The check round swept the rest of that hole's class and found three more members, all of which also
exited 1 through a traceback — the frozen `synthetic-tree.tar.gz` missing/corrupt, the frozen
`golden/py-verdict.json` unparsable (a read this batch's new `check_coverage` tooth introduced), and
the frozen `plots-SKIPPED.md` present but not decodable. All three now leave through `fail()` with
rc=2; the probes, their before/after outputs and the regression evidence are in `E4a2-check.md` §3.

## 6. Gates

Six gates, serially, on the final tree (`/tmp/e4a2/gates/`):

| gate | log | result |
|---|---|---|
| `dotnet build WinForward.slnx -c Release` | `build.log` | rc=0, **0 Warning(s) / 0 Error(s)** |
| `dotnet test WinForward.slnx -c Release -m:1` | `test.log` | rc=0, 14 assemblies, **1655 passed / 0 failed / 0 skipped**, no `error ` line |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | `format.log` | rc=0, **0 bytes of output** |
| `jb inspectcode -f=Xml -e=HINT -o=inspectcode.xml WinForward.slnx` | `inspectcode.xml` | rc=0; parsed with `ElementTree`: **`<Issue>` 0, `<IssueType>` 0**, no `CSharpErrors` |
| `effective-lines.py` over the four paths | `effective.log` | rc=0, **0 bytes of output** |
| semantic + byte on the golden and on the two landed batches | `oracle-*.log` | **rc=0** in all six invocations (§3, §4) |

`effective-lines.py` only ever walks `*.cs`, so this batch cannot move it: the only file it changes
is Python. It was run anyway, on the four paths the previous batches recorded
(`benchmarks/WinForward.E2E`, `benchmarks/WinForward.E2E.Contracts`,
`benchmarks/WinForward.E2E.Analysis`, `tests/WinForward.E2E.Tests`), and is silent.

## 7. Deviations, and things a check round should not have to rediscover

1. **`research/baseline/E4b1b-check.md` did not exist when this batch was built.** The task's "read
   first" list names `E4b1b-check.md` §5 as the minimal structure-vs-tolerance list to implement; there
   was no such file anywhere in the tree or in git history (`git log --all --diff-filter=D` and a
   filesystem-wide `rg --files -g '*E4b1b*'` find only `E4b1b-oracle.md`). The list implemented here is
   therefore the one the E4-a2 task message itself spells out, item by item, and §1 is that list
   written out. The check round has since written that file as a backfill (`E4b1b-check.md`); it is
   dated after this batch and is evidence about b1b, not a criteria source this batch could have read.
2. **A *deleted* `## N.` heading exits 2, not 1.** The self-check list puts "删/改标题" under
   rc=1 (structure), but deleting a heading deletes the slice, and the three-state mechanism is
   explicitly not to move ("删一个应存在切片 ⇒ rc=2"). A *reworded* heading is rc=1, which is the
   case the list is really about; a heading that loses its `## N.` form is rc=2, which is also what
   the b1a check round measured for the same mutation. The report names both (a `tables.md:headings`
   structure line *and* the missing slice) and rc=2 wins.
3. **D21.1's tolerance is superseded for the default.** D21.1 said "relative tolerance, default
   `1e-6`, `--tolerance` adjustable"; the E4-a2 ruling says one unit of the printed last digit and
   "no fixed relative ε". `--tolerance` survives as a multiplier of that unit (default `1`, `0` =
   exact), which keeps a knob without reintroducing a relative epsilon.
4. **Byte mode is unchanged, including its blind spots.** Its `verdict.json` slices are canonical
   re-serializations of the parsed subtree, so a *top-level* key reorder and an escape-vs-literal
   difference (`\u2013` vs `–`) are invisible to it — measured: `escape-byte` is rc=0 while
   `escape` semantic is also rc=0. What byte mode does catch, and semantic does not, is member order
   inside a slice, table row order, and every whitespace/digit difference in `tables.md`. Its
   genuinely byte-level parts are the prose, the rows and the artifact-form checks.
5. **Arrays are compared in order for every key**, not only `passes`/`rows` (the task pins those two).
   This is stricter than D21 requires — a reordered `findings` list would fail — and is registered
   rather than relaxed: no batch has hit it, and an order-insensitive rule is easy to add if one does.
6. **`plots/SKIPPED.md` missing is rc=2**, so a `--cs-out` directory has to carry `plots/SKIPPED.md`
   the way the analyzer writes it. Every real output does (it is written unconditionally); only
   hand-made self-check directories can trip on it, and the fix is to copy it.
7. **The artifact-form checks run on the produced side only.** The frozen reference is the contract's
   own bytes; reporting "the golden has a BOM" as a *difference* would name the wrong side.
8. **`check_coverage` grew two teeth**: a slice owned by two batches is now rc=2, and the set of
   `metrics/<key>` slices must equal the members the frozen reference publishes. The E4-a version
   checked only that the *container* name `metrics` appeared somewhere, so a member could be dropped
   from `BATCH_SECTIONS` and the check would still pass; it now cannot (negative control in §5.3).
9. **`verification/**` was not touched**, so no re-freeze and no re-diff from batch 1: the tarball,
   both golden files, `plots-SKIPPED.md`, `make_tree.py` and `check-fixture-drift.py` are byte-identical
   (`git status --short benchmarks/WinForward.E2E.Analysis/verification` prints nothing).
10. **The file tripled (337 → 1265 lines).** The comparison rules are the size: the module docstring
    is the criteria table, every function carries its contract, and nothing was moved into a second
    file because `scripts/` is a bare directory — importing a sibling would start writing
    `scripts/__pycache__/*.pyc`, and a tracked `.pyc` is already a registered debt item (D20.7,
    E4-d). If check prefers a package, that debt has to be settled first.

## 8. For check

1. **The integer rule (§2.1).** Confirm that "both sides printed a whole number ⇒ exact" is the right
   reading of "one unit of the last printed digit", and try to make a datagram counter or a pass count
   pass by one: `6000` vs `6001`, `n=2 of 3` vs `n=2 of 4`, and `< 3/6000` vs `< 3/6001` are the
   cases the rule exists for. Also confirm `--tolerance 0` really is exact end to end.
2. **Row identity (§2.2).** The key is derived, not declared. Confirm the three duplicate-bearing
   tables behave (a value inside a duplicated row is a value, not a missing+extra pair; a deleted row
   is rc=2; a duplicated row is rc=1), and that the `width > 4` / `width >= len(header)` fallback to
   `[#N]` changes only the *label*, never the comparison.
3. **The text split.** Verdict strings are token-exact except under the wording keys; `thresholds` is
   token-exact by construction. Confirm that is the right line — in particular that `5 %` vs `5%`
   passing while `5 %` vs `6 %` fails is what "阈值串精确文本" should mean, and that a number quoted
   inside a `detail` or an `n/a (reason)` may move by exactly one printed unit.
4. **§7.2 and §7.4**, the two places where the behaviour is deliberately *not* what a literal reading
   of the task's one-line entries would give (a deleted heading, and byte mode's canonical slices).
   Both are measured; if the intent is the literal reading, both are one-line changes.
5. **The full-run key-set assertion (§1).** It fires only when every batch is requested, so
   `--batch 1a` keeps passing on a tree where top-level keys are still absent — seven of the fourteen
   after b1b landed (`generated_by`/`raw`/`flat_mode`/`passes`/`rows`/`bootstrap`/`thresholds` are the
   ones present, and §4's full run reports exactly seven missing keys; the "eleven" an earlier draft of
   this note carried was the b1a-era count, when only three keys existed). Confirm
   that scoping is what the batch mechanism wants, and that the extra-key/extra-metric half
   (`structure`, rc=1) is not too strict for a port that lands ahead of its batch.
