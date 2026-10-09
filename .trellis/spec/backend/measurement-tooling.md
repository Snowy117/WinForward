# Measurement Harness Tooling

> Which script or gate checks what — the README contract table, the key-literal gate, the effective-line
> limit and the CLI snapshots — and the analyzer's contract and outputs. Read it when a gate goes red, or
> before running the analyzer or its differ. Family hub: [measurement-harness.md](./measurement-harness.md).

## The README contract table is a gate, not prose

`benchmarks/WinForward.E2E/README.md`'s "Which keys are contract" section (line 372) is what a reader
consults when a cell looks wrong, and it is the one place a rename can go stale without anything failing:
the analysis resolves most paths by arm kind, so a misspelt key renders an empty cell and the report still
builds. `check-readme-contract.py` reads the section — tables *and* prose — and for every backticked key
path it names, and for the five root names the table spells bare, it:

1. resolves it to a `const string` in `ArmKeys.*.cs`: the class chain spells the path
   (`ArmKeys.Common.Gates.ClientSendLoss` is `gates/clientSendLoss`), a `<name>` placeholder resolves to
   its container, and a `metrics/latency/…` / `metrics/loss/…` / `parameters/latency/…` /
   `parameters/loss/…` token resolves as the `BASE` phase key one level down;
2. requires that constant to be **referenced by a write site** — `Cli/`, `Client/`, `Target/`, `Wire/`,
   `Program.cs` or the `Contracts` records' own `WriteTo` methods; a test is not a write site, because a
   key only a test writes is a key no run publishes, and a line-commented call is not one either, because
   the scan drops line comments before it looks for the reference. This second check is also what catches
   a `BASE` phase key that a fresh constant spells correctly while the writer still uses the phase's own:
   the phase rule in (1) would resolve the copy, the write site would not;
3. fails on any token that is an `old_path` of `contract-rename.json` whose `new_path` differs, so a
   rename cannot leave the table describing a record that no longer exists.

Three tokens are declared rather than inferred, in two sets (`check-readme-contract.py:123-131`), so that
dropping them is an edit and not a silent pass: `LEGACY_TOKENS = {"metrics/clientSendLoss"}` (the
analysis's legacy gate fallback, published by no current latency arm) and
`DOCUMENTED_NON_KEYS = {"parameters/window", "parameters/loss.lossWindowMs"}` (plan-key spellings the
analysis reads at neither path — it reads `parameters/inFlightWindow` and
`parameters/loss/lossWindowMs`). A key that is contract but that no table reads — the two out-of-range
counters and `completionRate` — belongs in the section's prose paragraph, where the same check covers it.

**The gate is red today, for one dead path.** The script hard-codes the rename table at
`.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json`
(`check-readme-contract.py:47`); task `10-07-e2e-harness-refactor` was archived by `aee1955`, so the
table now lives at
`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/contract-rename.json`. As shipped the
checker exits **2** and prints only `cannot read …/contract-rename.json: [Errno 2] No such file or
directory` — it never reaches its checks. Run against the archived table it prints
`111 key(s) checked against 401 declared constant path(s): ok` and exits 0, so those numbers are the
checker's real output and only the path is dead. The archive-aware lookup the .NET side already uses is
`tests/WinForward.E2E.Tests/RepoPaths.cs:49-71`; the fix belongs in the script (or in where the table
lives), not in this document. Exit codes: `0` every key resolved; `1` at least one key is stale,
unwritten or unspellable, one `FAIL: <token>: <reason>` line per key; `2` an input could not be read, the
`ArmKeys` shards declare nothing, or the section names no key.

## The key-literal gate

`JsonKeyLiteralGateTests` scans each gated file as **one text**, not line by line, so a call the formatter
wrapped still has its key position on it, and it fails when the scan root or the known-key set is empty —
the one failure mode a source-scanning gate cannot detect by scanning. The gated files are every file
under `Client/Arms/**` plus nine named writer files across both binaries: `Client/ClientRunner.cs`,
`Client/ArmRecordWriter.cs`, `Client/RunFileWriter.cs`, `Client/ResourceSampler.cs`,
`Client/ResourceSampleWriter.cs`, `Target/TcpTargetServer.cs`, `Target/UdpEchoServer.cs`,
`Target/DnsServer.cs`, `Target/TargetRunner.cs`. A literal is flagged only when it matches a **currently
declared** `ArmKeys` spelling (`JsonKeyLiteralGateTests.cs:132,184-200`), so a retired spelling passes
it: the gate catches a duplicated spelling, not a stale one
(→ [record contract](./measurement-record-contract.md)).

## The scripts

Each script is run by hand unless a test says otherwise; the README's script table (`README.md:58-72`)
lists the rest of the directory.

| Script | What it checks |
|---|---|
| `jsonl_paths.py` | the one canonical flattener (`join('/', member names)`); the inventory, `compare-records.py` and the rename table all import it |
| `contract-inventory.py` | `inventory --run DIR --out FILE` writes every canonical path of one run; `rename --baseline DIR --run DIR --out-json FILE --out-md FILE` writes the full `{kind, old_path, new_path, reason}` table and fails (exit 1) when the fresh run publishes a path the table does not declare |
| `compare-records.py` | the run-to-run comparison — classes, band and rename table; [judgement](./measurement-judgement.md) owns the manual procedure |
| `normalize-pattern-hits.py` | reports normalization-config patterns that match nothing in a tree |
| `check-readme-contract.py` | the README contract table against `ArmKeys` and the rename table (above) |
| `effective-lines.py` | the 400-effective-line limit over the `.cs` files it is given; exit 1 naming the file and its count, exit 2 when a path does not exist |
| `cli-snapshots.py` | records a CLI surface as exit code + stdout + stderr bytes: `cli-snapshots.py <harness-binary> <out-directory>` |
| `oracle-diff.py` | the differ the analysis is judged with (below) |
| `check-fairness.py` | asserts the fairness disclosures in `row-profiles.json` against the analysis's own `tables.md` |

`compare-records.py` and `effective-lines.py` are **not run by any test or CI workflow**. Run them by
hand — the comparison as [judgement](./measurement-judgement.md) shows it, the line gate as:

```
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts \
    benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests
```

It prints nothing and exits 0 on a clean tree; the counter strips blanks, `//` and `/* */` the way the
compiler sees them (a `//` inside a string is not a comment, a multi-line raw string counts as code).

## CLI snapshots

`CliSnapshotTests` replays the recorded commands through `Program.Main` in a non-parallel collection and
restores `Console.Out`/`Error`/cwd; a reworded message turns it red. The collector's `CASES` list holds
**31** commands (`cli-snapshots.py:42-76`) — the two helps, the three role forms and every rejection
either verb can print — while the frozen `before/` tree (the last binary published before E2-d) holds 28.
The test replays those 28 against `before/` and the 3 the E3-d receive-loop option added against
`after/`, the tree that recorded them (`tests/WinForward.E2E.Tests/CliSnapshotTests.cs:49-79`).

## The analyzer

`benchmarks/WinForward.E2E.Analysis` is C# and shares `Contracts`. It replaced the Python reference
(`analyze.py`), which is retired and survives only as the frozen `verification/golden/` output the differ
runs against (`verification/FROZEN.md:8`). Entry point
`benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh`: it builds once, then `exec`s the binary with the
caller's working directory and arguments untouched (`--raw --out --ledger --flat
--warmup-seconds --resamples --seed`; defaults `../raw`, `..`, `5.0`, `10000`, `20261006`; `--ledger` may
be repeated). `AnalysisOptions.DefaultMinPasses = 3` is a constant rather than a flag: fewer than three
passes is reported as `inconclusive`.

It reads JSONL with `JsonDocument`. The envelope-level names come from `ArmKeys` (a compile error on a
rename), while the paths inside `metrics`/`parameters` are literals addressed positionally on `/`, because
only the arm's kind knows which map a path belongs to — so a metric rename is a two-sided edit, and
`check-readme-contract.py` gates the documented half of it. No reflection, no source generation, no
`InternalsVisibleTo`. It writes three artifacts, unconditionally: `tables.md` (16 `## N.` sections,
`Tables/TablesWriter.cs:52-69`), `verdict.json` (14 top-level keys, `Verdict/VerdictWriter.cs:19-34`) and
`<out>/plots/SKIPPED.md` (`Cli/AnalysisRunner.cs:38-48`, `Cli/PlotsNotice.cs:41`); the frozen
byte-identical copy of the last one is `verification/plots-SKIPPED.md`.

| Artifact | Purpose |
|---|---|
| `verification/synthetic-tree.tar.gz` | the campaign every regression runs on (deterministic tar: sorted entries, fixed mtime/owner) |
| `verification/golden/{py-tables.md,py-verdict.json}` | the reference output for that tree |
| `verification/synthetic/make_tree.py` + `FROZEN.md` | how the tree is built, and how to refreeze (any change means refreezing and re-diffing from batch 1) |
| `benchmarks/WinForward.E2E/scripts/oracle-diff.py` | the differ — the harness's `scripts/`, not this project's: `--mode semantic` (default) or `--mode byte`, `--batch N`, `--tolerance` |
| `verification/row-profiles.json` + `benchmarks/WinForward.E2E/scripts/check-fairness.py` | the fairness rules as data, asserted against the C# output |
| `verification/check-boundary-trees.py` | the knob trees (window overflow, undecodable, truncated, zero denominator) |
| `verification/check-fixture-drift.py` | fixture paths no green run observes, against the contract constants; it resolves its fixtures from the same archived task path (`check-fixture-drift.py:40`) and needs the same fix |

Differ contract: exit `0` equal, `1` different, `2` **something that should exist does not** — a missing
slice or an unreadable artifact is never a pass. Semantic mode keeps headings, column names, row identity,
cell counts and cell kinds exact, compares numbers within one unit of the reference's printed precision,
decodes strings before comparing, and ignores object key order and row order. Byte mode is a
structure-surface regression, not a batch criterion. Every section and every verdict key belongs to
exactly one batch in `BATCH_SECTIONS`; a new section must be added there, and a key without a batch is a
usage error.

**Six leaves are exempt from the printed-precision rule**, because they are resampled rather than
published: inside a `metrics/<member>.pairs[i]` entry, the four p-values (`p_value`, `holm_p_value`,
`p_equivalence`, `holm_p_equivalence`) are compared within an absolute `5e-2` and the two interval edges
(`ci95[0]`, `ci95[1]`) within `max(1e-2, 1e-2 · abs(expected))` (`oracle-diff.py`:
`STATISTICAL_PATH`/`statistical_tolerance`). The bound is measured, not chosen: each of the four is a
`--resamples`-draw estimate of a probability (the direct pair doubles the one-sided count, the equivalence
pair takes one edge), so two generator sequences differ by SD 6.4e-3 and 3.5e-2 at worst on the frozen
tree — wider than one printed unit, narrower than any real change. The path must match exactly, so no
table cell, `estimate`/`median`/`iqr`, verdict string or key set is relaxed, and widening the pattern or
the bound is an edit that owes a new measurement.

**Serialize differ runs.** `oracle-diff.py` re-extracts `/tmp/wf-synth` and both sides write
`/tmp/wf-oracle/cs`, so two concurrent runs truncate each other's tree: a concurrent run was measured
reporting `28 structure, 751 value, 118 missing` with a partially read ledger (`6376/0/0` records, zero
unparsable), while the identical worktree, binary and script re-ran rc=0 and 20 serial runs on a static
tree were stable. The failure looks like a real regression, so it is worth knowing before chasing one.

A boundary state the reference cannot render (a zero denominator makes it raise) is asserted from the C#
side alone: build the knob tree, assert the C# output, and add a negative control that must turn red.
Records whose numbers are summed for the report follow CPython's compensated summation when the reference
does (`Stats/DescriptiveStats.cs`); that rule has its own anchor test rather than relying on a comparison.
