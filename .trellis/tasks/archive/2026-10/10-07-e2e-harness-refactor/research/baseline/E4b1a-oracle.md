# E4-b1a — `Loading` + `Model` + §15 + the preamble

Batch E4-b1a of `.trellis/tasks/10-07-e2e-e4-analyzer`. Authority: `design-decisions.md` **D20**
(D20.2 slicing and batch ownership, D20.3 the tree both sides are called with, D20.5 the verbatim
defaults, D20.6 the read route, D20.7 the batch table), D6.4 (the two neutralizations), and
`research/python-oracle-changes.md` (the reference's change list and its rendering quirks).

The batch turns the skeleton's census into the reference's model and renders the first slice it owns.
Its criterion is `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a` ⇒ **rc=0**:
`preamble` + `## 15.` + `verdict.json`'s `generated_by`/`raw`/`flat_mode`/`passes`/`rows`, equal.

**Result: rc=0, 7 of 7 slices equal** (`/tmp/e4b1a/checks/A-1a.log`), and every other batch still
rc=2 (§5). The frozen artifacts under `verification/` and the reference `analyze.py` were not touched
(`git status` in §7).

## 1. The module split

`Loading/RecordScanner.cs` and `Model/CampaignInventory.cs` are gone: the first counted records
without keeping them and the second was a census rather than a model. Everything else is either new
or carries the same name with the reference's own content behind it.

| file | effective lines | the reference function(s) it carries |
|---|---|---|
| `Loading/CampaignLoader.cs` | 160 | `discover()` — pass/row discovery, `--flat`, the pass-level dual offer, ledger attachment |
| `Loading/RunLoader.cs` | 172 | `load_run()`, `load_dual()`, `announce_dual_owner()` — the roster, `proxy-truth.json`, configs, and the dual-owner rule |
| `Loading/ArmLoader.cs` | 48 | `load_arm()` — one file's records, classified by their own `type` |
| `Loading/JsonReader.cs` | 86 | the read route (D20.6): `JsonDocument` + `ArmKeys`, no serializer |
| `Loading/LedgerLocator.cs` | 43 | `find_ledger_paths()` — four shipped names, then the glob, first-seen order, `--ledger` verbatim |
| `Loading/PythonGlob.cs` | 36 | the glob half of `Path.glob()`: hidden names are not matched, results are path-sorted |
| `Model/CampaignModel.cs` | 40 | `Context` — and `row_ids`/`pass_ids`, the two orderings the whole document follows |
| `Model/ClientRun.cs` | 27 | `RunData` — the run, its document, its arms, its lanes, its load errors |
| `Model/ArmRecords.cs` | 60 | `ArmData` and the arm set's load order |
| `Model/RunSamples.cs` | 51 | `self_samples`, `product_samples`, `sampler_errors`, `sample_is_readable` |
| `Model/RowProfiles.cs` | 117 | `ROW_PROFILES`, `PLAN_ARMS`, `ROW_ORDER` |
| `Model/NaturalKey.cs` | 62 | `natural_key` |
| `Model/PosixPathText.cs` | 55 | `str(Path(...))`, `Path.parent`, `Path / name` |
| `Model/JsonValue.cs` | 39 | `dict.get` semantics, including the `is True` vs truthiness distinction |
| `Tables/TablesWriter.cs` | 67 | `build_tables_md()` — the preamble, the section join and the closing `rstrip() + "\n"` |
| `Tables/TableAvailability.cs` | 86 | `table_availability()` — §15 |
| `Tables/MarkdownTable.cs` | 19 | `md_table()` — no padding, no alignment, no arity check |
| `Verdict/VerdictWriter.cs` | 38 | the reference's key order and object text |
| `Verdict/VerdictSections.cs` | 18 | the five keys this batch owns |
| `Verdict/VerbatimJson.cs` | 71 | the scalar/array half of `json.dumps` (b1b completes the writer) |
| `Cli/AnalysisRunner.cs` | 68 | `main()`'s write order and its summary lines |

The twenty-one files above are what this batch wrote or rewrote; the project as a whole is thirty-one
files and 1586 effective lines, and its largest file is `RunLoader.cs` at 172 — every one of them
under the 400-line rule (`effective-lines.py` over the four paths is silent, §7).

## 2. `RowCount`: the U3 correction

The skeleton's `CampaignInventory.RowCount` counted **loaded runs** and printed 27; the reference
prints `len(ctx.row_ids)` = **9**. `CampaignModel` now carries both, and the distinction is stated
where it is made (`Model/CampaignModel.cs`):

* `Rows` — every loaded run, pass by pass (`ctx.rows`, 27 in the frozen tree); §15 prints one line
  per entry.
* `RowIds` — the distinct row ids, first-occurrence order re-sorted by `ROW_ORDER` rank and then
  `natural_key` (`ctx.row_ids`, 9); this is `verdict.json`'s `rows` and the run summary's count.
* `RowCount` is `RowIds.Count`.

The run summary follows the reference: `analyze.py: 3 pass(es), 9 row(s), 2 ledger(s), 27 loaded
run(s)` — the last number is deliberately new and is stdout-only, so the two counts can be told apart
in a log without re-deriving them (`checks/A-1a.log`).

## 3. The preamble and §15, byte for byte

`tables.md:1-8` (the title, the program-name line with the `--raw` value, the aggregation policy and
the "read section 1 first" pointer) and everything from `## 15.` to the end of the file are compared
as raw text by `oracle-diff.py`. Measured on the produced file against
`verification/golden/py-tables.md`:

| slice | bytes (both sides) | lines | `cmp` |
|---|---|---|---|
| preamble (`## 0.` 之前) | 948 | 8 | identical |
| §15 (`## 15.` … EOF) | 4282 | 33 | identical |

`checks/G-bytes.log`. The two neutralized lines are both inside the preamble: `tables.md:3` names
`e2e-analysis v1` and neither implementation (A2-11), and `verdict.json`'s `generated_by` is the same
string (§4). The document's tail is produced the reference's way — parts joined with `\n`, the join
`rstrip()`ed, one `\n` appended — so §15 ends at the last table row with exactly one newline.

§15's own content is the model's own content: `pass`, `row`, the ordinal-sorted arm names, the
`with-result/total` ratio, the plan's missing arms, the undeclared extras, the self/product/rejected
sample counts, the `samplerError` count, the `proxy-truth.json` yes/no, the dual lanes, the config
count and the notes column. The dual column is where the frozen tree is interesting: `proxifier`,
`proxifyre`, `wf-aot-opt` and `wf-fdd-opt` carry their own `<row>/dual`; `control-post` and
`control-pre` hold a copy of the pass-level dual directory because the offer walks the rows that have
no lanes until it reaches the row the lanes name; `proxybridge` is that row and stops the walk; and
`wf-aot-dnsrelay`/`wf-aot-nativeudp` — which come after it — keep `n/a`. All four outcomes appear in
the golden and in the produced file, so the rule is exercised rather than asserted.

## 4. The five verdict keys, byte for byte

`verdict.json` is sliced by top-level key, and the comparison is over the canonical re-serialization
of each key's parsed value. As *text* the produced file is the five keys in the reference's
declaration order:

```text
{
  "generated_by": "e2e-analysis v1",
  "raw": "/tmp/wf-synth/raw",
  "flat_mode": false,
  "passes": [ "pass1", "pass2", "pass3" ],   (one element per line, as json.dumps indent=2)
  "rows":   [ 9 element rows ]
}
```

| check | result |
|---|---|
| the five keys' values equal as JSON (`json.loads` both sides) | yes |
| key order equal to the reference's declaration order | yes (`generated_by`, `raw`, `flat_mode`, `passes`, `rows`) |
| produced text vs the reference's prefix up to its first unowned key | equal after dropping the one separator comma the reference needs because eleven more keys follow (`339 → 338` bytes) |
| the six keys this and later batches do not own yet | absent, so `oracle-diff.py` reports them as missing slices (rc=2) rather than comparing them as empty |

`checks/H-verdict.log`. The absence is the point: `1b`/`1c`/`2`/`3`/`4`/`5` all still exit 2 (§5).

## 5. The three exit codes, re-measured on batch 1a

| what was compared | command | result |
|---|---|---|
| the port against the frozen golden | `oracle-diff.py --batch 1a` | **rc=0**, 7/7 equal |
| one digit changed in §15 (`260 → 261`) | `--batch 1a --cs-out .../mutated` | **rc=1**, `tables.md:15` named with its line |
| the §15 heading renamed so the pattern no longer matches | `--batch 1a --cs-out .../nosection` | **rc=2**, `0 differ, 1 missing` |
| one of the five keys deleted | `--batch 1a --cs-out .../nokey` | **rc=2**, `0 differ, 1 missing` |
| every other batch | `--batch 1b/1c/2/3/4/5` | **rc=2** each: 2/2, 1/3, 2/4, 15/18, 6/11, 3/6 |

`checks/A-1a.log`, `B-mutated.log`, `C-nosection.log`, `D-nokey.log`, and `gates/oracle-*.log`. Batch
3 in full: `compared 18 slice(s): 3 differ(ent), 15 missing` — the three §5/§8/§9 sections exist as
placeholders and the fifteen metric slices are absent, so no other batch was let through by this one.

## 6. Parity checks the batch's own criterion does not cover

The criterion fixes one tree at one path; three shapes the port must still get right are checked
against the reference directly.

**`--flat`.** Running both sides on `/tmp/wf-synth/raw/pass1/wf-aot-opt --flat` (a single row
directory read as one implicit pass) and comparing the same batch: rc=0, including the preamble's
flat-mode suffix, `flat_mode: true`, `passes: ["flat"]`, `rows: ["wf-aot-opt"]` and §15's single line
(`checks/E-flat.log`).

**A relative `--raw`.** Called as `--raw raw/` from `/tmp/wf-synth`, both sides print
`"raw": "raw"` and discover the same ledgers — `ledger-main.jsonl, ledger-direct.jsonl` (no `./`
prefix, because `Path("raw/").parent` is `.` and `Path(".") / name` drops the dot). rc=0
(`checks/F-rel.log`, `F-rel-cs.stdout`).

**Ledger discovery.** On a tree with all four shipped names, an extra `*ledger*.jsonl`, a
non-matching file and a pass-local ledger, the port's per-pass order is the reference's `discover()`
output, path for path (`checks/L-ledger-order.log`): pass directory first, then `--raw`, then
`--raw`'s parent; inside each, `target-ledger.jsonl`, `ledger.jsonl`, `ledger-main.jsonl`,
`ledger-direct.jsonl`, then the glob sorted — so pass1 lists its own `ledger.jsonl` in front and every
pass then lists `…/target-ledger.jsonl, …/ledger.jsonl, …/ledger-main.jsonl, …/ledger-direct.jsonl,
…/zz-ledger.jsonl`, while `other.jsonl` is never a candidate. With
`--ledger /tmp/nope.jsonl --ledger …/ledger-direct.jsonl --ledger …/ledger-order-2` both sides keep
exactly `…/ledger-direct.jsonl` for every pass: non-existent paths and directories are filtered, and
the override replaces the search rather than extending it.

## 7. The gates

Six gates plus the batch criterion, all on the frozen revision, serially:

| gate | result |
|---|---|
| `dotnet build WinForward.slnx -c Release` | rc=0, **0 Warning(s) / 0 Error(s)**, and `WinForward.E2E.Analysis -> …/WinForward.E2E.Analysis.dll` is in the log |
| `dotnet test WinForward.slnx -c Release -m:1` | rc=0, 14 assemblies, **1621 passed, 0 failed, 0 skipped**, no `error ` line |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0, **0 bytes of output** |
| `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx` | rc=0, XML parsed: **`<Issue>` 0, `<IssueType>` 0** |
| `effective-lines.py` over the four paths | rc=0, **0 bytes of output** |
| the batch's own criterion | `oracle-diff.py --batch 1a` rc=0; `--batch 3` rc=2 |

Logs: `/tmp/e4b1a/gates/{build,test,format,inspectcode,effective-lines,oracle-1a,oracle-3}.log`.

The first inspectcode run on this batch's tree reported **33** findings, all in the new files and all
fixed rather than suppressed: `RedundantEmptySwitchSection`, `RedundantUsingDirective`,
`MemberCanBePrivate.Global` (10 constants and one property that only their own file reads),
`CanSimplifyDictionaryLookupWithTryAdd`, `CanSimplifyDictionaryTryGetValueWithGetValueOrDefault`,
`ReplaceWithFieldKeyword` (C# 14's `field`), `ArrangeObjectCreationWhenTypeEvident` (9 target-typed
`new`), `UseVerbatimString` (2 — written as two `Append` calls instead) and four unused-member
findings that were resolved by **deleting** what no batch consumes yet: `CampaignModel` no longer
carries `WarmupSeconds`/`Resamples`/`Seed`/`MinPasses`. They are parsed and printed from
`AnalysisOptions` today and return to the model in b1b/b4 with the code that reads them; carrying
them unused would have been state no test could reach.
`verification/` and `benchmarks/results/…/analyze.py` are untouched; the tracked
`analysis/__pycache__/analyze.cpython-314.pyc`, rewritten by the provenance checks that imported the
reference, was restored with `git checkout --`.

## 8. Deviations and deferrals

1. **`NaN`/`Infinity` are not readable.** `json.loads` accepts the named floating-point literals;
   `System.Text.Json` has no reader option for them (its `AllowNamedFloatingPointLiterals` lives on
   `JsonNumberHandling`, which is a *writer* setting), so such a line counts as malformed here. The
   harness's own writer refuses to serialize those values, so a tree this harness wrote cannot
   contain one; the divergence is documented in `JsonReader`'s remarks rather than papered over.
2. **Invalid UTF-8 in a JSON file is an error, not a crash.** The reference reads `run.json` with
   `errors` unset, so a decode error escapes its `except (OSError, JSONDecodeError)` and takes the
   whole analysis down; the port continues instead, and what it records depends on where the bad bytes
   are: inside a string value `System.Text.Json` substitutes U+FFFD and the file parses **without a
   note**, while bad bytes between tokens (or a file that cannot be decoded at all) are recorded as
   `run.json unreadable (JSONDecodeError)` — a load error, so it lands in §15's `notes`. Measured by
   the check round (`E4b1a-check.md` §6, §9.1); this paragraph replaces the earlier claim that every
   variant records a load error.
3. **`order.txt` is not read yet.** `discover()` collects it and §3's gate table consumes it in b2;
   the parsed field would be unused state until then, so it lands with its reader.
4. **`--warmup-seconds` / `--resamples` / `--seed` affect nothing this batch renders.** They are
   parsed to the reference's defaults (`5.0`, `10000`, `20261006`) and printed in the summary; the
   model does not carry them until b1b/b4 consume them (see the gate note in §7).
5. **`VerbatimJson` is deliberately minimal.** It writes strings and string arrays with
   `json.dumps`' escaping and indentation; the whole-document writer, the value encoder and the unit
   tests are b1b's, and the class still prints what it owes on every run.

## 9. For check

1. **The dual-owner walk (`RunLoader.AttachDual` + `CampaignLoader.AttachPassDual`).** It is the one
   place the port reproduces a rule that looks like a defect: the pass-level dual directory is
   attached to *every* row that has no lanes of its own until the row the labels name is reached, so
   `control-post` and `control-pre` hold a copy in the golden. §15's `dual lanes` column carries the
   evidence for all four outcomes; a "fix" that attached the directory only to its owner would turn
   the golden red on three lines.
2. **`RowCount`** (§2) — confirm 9 everywhere a row count is published and 27 only where loaded runs
   are meant (the summary's last number, §15's line count).
3. **The five verdict keys' order** (§4). The comparison is value-wise, so a wrong *order* would still
   pass the differ while breaking the byte contract; it is checked here explicitly.
4. **The deferrals in §8.3-8.4** — `order.txt` and the statistics parameters are the only pieces
   of `Context` the reference builds that the model does not carry. Confirm that is the right boundary
   (b2 for `order`, b1b/b4 for the parameters) rather than something b1a should already have.
5. **`JsonReader`'s two documented divergences** (§8.1-8.2) — sanity-check that both are unreachable
   from a tree the harness can write, and that recording them as input-class notes is preferable to
   reproducing a crash.
