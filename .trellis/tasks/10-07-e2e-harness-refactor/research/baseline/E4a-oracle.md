# E4-a — the oracle mechanism and the skeleton

Batch E4-a of `.trellis/tasks/10-07-e2e-e4-analyzer`. Authority: `design-decisions.md` **D20** (D20.1
boundary trees, D20.2 slicing, D20.3 tree layout, D20.5 verbatim defaults, D20.6 read route, D20.7 the
rest), D6, D19.3 H, and `research/python-oracle-changes.md`.

The batch delivers a mechanism, not a table: the frozen tree and golden the C# port will be judged
against, the differ that judges it, and a skeleton that compiles, runs and reports every batch it
still owes. Nothing of §0–§15's *content* is rendered yet except the sixteen headings.

## 1. What the fixture guard exposed (the drift list)

`verification/check-fixture-drift.py` compares the generator's key set, flattened with the shared
alphabet (`benchmarks/WinForward.E2E/scripts/jsonl_paths.py`), against the current contract in both
directions. The contract is read from **two** tables, because neither alone is the current one:
`contract-inventory.json` is the *frozen pre-migration* inventory of one real run (its `run` field
still points at `/tmp/b1c-run` and it still spells `sentOk`/`udpSent`), and `contract-rename.json` is
the registered delta E1–E3 landed on it — one row per path, `identical`/`renamed`/`added`, whose
`new_path` column is what a real run publishes today. The guard composes them and refuses to run if
the rename table names a baseline path the inventory does not have (a check that would catch a
regenerated table pointed at the wrong baseline).

Run on the *moved, unchanged* generator it reported 20 paths the fixture published that the contract
does not declare and **161 declared paths the fixture never published** — the tree had drifted from
E1–E3 by more than a rename. The 20 extras were 9 old spellings
(`metrics/{tcp,udp}.sentOk`, `metrics/{latency/tcp,latency/udp}.sentOk`, `metrics/udpSent`,
`metrics/udpLossRate`, `metrics/desktops/{udpSent,udpArrived,udpForeignConnection}`), 3 bogus
`parameters/*/window` keys (the contract spells them `inFlightWindow` and `lossWindowMs`), and 8 paths
in files that are not part of the record contract (`environment.json`, `proxy-truth.json`).

The 161 missing paths, grouped by where they belong:

| Area | Missing paths | What the fixture now publishes |
|---|---|---|
| LAT/LATLOAD | 6 | `tcp.sendWouldBlock`, `tcp.outstandingAtTeardown`, `udp.sendWouldBlock` (+ the 3 renames) |
| DNS/DNSALT | 5 | `queryTypes{A,AAAA,HTTPS,TXT}` + the `udp.sent` rename |
| LOSS/BASE | 8 | `clientSendLossRate`, `duplicateRate`, `outOfRangeSequences`, `sentOutOfRangeSequences` in both the arm and the control's loss phase |
| MIX | 10 | `classes.udp.{sendFailures,windowOverflow,outOfRangeSequences,sentOutOfRangeSequences}`, the desktop-lane renames, the arm-level renames |
| REL | ~75 | the whole `byMode` block (4 modes × 17 paths), the 14-key `attempt` record family, `echoedBytes`, `trailerBytes`, `attemptRecords`, `attemptRecordsOmitted` |
| THRU | 5 | `framesSent`, `framesEchoed`, `streamConnects`, `budgetBytes`, `budgetRemainingBytes` |
| PERSIST | 1 | `completionRate` (E3-e: the completion caliber, `achievedRate` re-read as requests sent per second) |
| IDLE | 1 | `parameters.traffic` |
| ledger | ~35 | the full `targetSummary.{tcp,udp,dns,dnsAlt}` blocks (`verdicts` ×8, the DNS counters, `udpReceivers`), `acceptErrors`/`truncatedFrames` at both levels, and the `error`/`detail` record |
| run.json | 1 | `planSource` |

Three of the additions are *conditional* keys no green run observes — `ArmKeys.Sample.ReadError`, the
error record's `message`, and `ArmKeys.Ledger.ErrorRecord.Detail` — so the guard declares them
explicitly (`UNOBSERVED_KEYS`) and requires them to be *present*: a declaration the fixture stops
exercising is itself reported. The frozen tree is the first fixture that covers them.

## 2. The generator's changes

`benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_tree.py` (moved from
`benchmarks/results/2026-10-06-e2e-competitors/analysis/synthetic/`; the old copy is removed in E4-d
with `analyze.py`). Beyond the key set above:

* `--truncated-tcp N` / `--truncated-dns N` are new and default to 0, so the clean tree's bytes are
  unaffected; a counter is published either way. TCP truncation also books the cut-off connections
  under the target's existing `protocolError` verdict (D19.3 D), DNS truncation under
  `dnsSummary/truncatedFrames` — the two mechanisms share a spelling and are separate flags (D19.3 C).
* `--window-overflow` and `--undecodable` keep their behaviour; all four counters are values-only, so
  the boundary trees have the same key set as the clean one.
* The REL arm now writes its `attempt` records (stride 16, cap 4096, exactly the arm's own evidence
  budget) and the joint `byMode` block; the ledger writes the target's own `error` record.
* Three `plan-<pass>.json` files land beside the tree, which `run.json`'s `planPath` already pointed
  at (D20.3).
* The generator is deterministic: two runs into two different directories differ only in the
  `outDirectory` field, which embeds the tree path — the reason both implementations are called with
  the same hardcoded `/tmp/wf-synth/raw`.

Guard result on the frozen tree: **rc=0**, `contract: 603 path(s)`, `fixture: 605 path(s)`,
`declared unobserved shapes: 3`, `fixture drift: none (both directions empty)`.

## 3. The Python reference

`.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md` carries the full list;
the short form:

* **29 changed lines in `analyze.py`**: 27 field-name substitutions (five spellings) + `generated_by`
  → `"e2e-analysis v1"` + `tables.md` line 3's program name → `` `e2e-analysis v1` ``.
* The change is *mechanically reproducible*: applying the documented substitution script to
  `git show HEAD:…/analyze.py` reproduces the committed file byte for byte, so no statement, condition,
  default or layout moved with the names.
* §5 of that file lists the **eight rendering quirks** that look like defects and must survive:
  §5's 154 eleven-cell lines against a ten-column header (108 of them in tables whose header has ten),
  the `|---|---|` separator row, the empty cell for an all-null reading versus `n/a (<reason>)` (499
  occurrences), `fmt_stat`'s three tails and the en-dash median form (1085 unit-after-bracket cells),
  `%.3g`'s two renderings (`1e-06`, `8.000e-03`) against 27 literal `0.000` cells, no thousands
  separators/alignment/padding, UTF-8 without BOM with `\n` endings and one trailing newline, and
  `json.dumps`' two-space indent with `ensure_ascii` escapes (`\u2013` in `verdict.json` where
  `tables.md` carries the character).

## 4. The frozen artifacts

| Artifact | sha256 |
|---|---|
| `verification/synthetic-tree.tar.gz` | `723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902` |
| `verification/golden/py-tables.md` | `8011d05bdad5dd0b2232a1f9e0bd269a637ad7995299d351dae71a945104ebbc` |
| `verification/golden/py-verdict.json` | `2e0e64dada67524502cde8958dafdf6f90b1e54400015dacd89c921d8483c55d` |
| `verification/plots-SKIPPED.md` | `39795a79df7ed8f33a875618553654153e3b1b85009161832052bf28d3144ad4` |
| `verification/synthetic/make_tree.py` | `b3e946a7419f6a09622fac2b6a6915c7c563dcbec34cb35d4379d065c06482a1` |
| `verification/check-fixture-drift.py` | `acf4178105f911f7a7e3ef81611bf0dff069ae812ed5718f62086fd06f0dec2b` |

The tarball is deterministic (sorted names, `--mtime='2026-10-01 00:00:00 UTC'`, owner/group 0): two
builds produced the same digest, and extracting it reproduces the tree except for the analysis output
directory, which the freeze recipe deliberately leaves out of the archive. `FROZEN.md` records the
recipes, the re-freeze rule ("a change to `make_tree.py` invalidates the golden; the C# side is then
re-diffed from batch 1") and the four boundary trees' hashes — one of which (`--truncated-tcp 3`) was
regenerated after the freeze and reproduced its hash, so the four are a check rather than a record.

## 5. The differ

`benchmarks/WinForward.E2E/scripts/oracle-diff.py`:

* `BATCH_SECTIONS = {batch: {"tables.md": [...], "verdict.json": [...]}}`, every slice owned exactly
  once; a startup coverage check refuses a table that drops a section or one of the 14 top-level keys
  (negative control: deleting `ledger` from batch 5 exited **2** with
  `rc=2: BATCH_SECTIONS does not own every slice: tables [], verdict keys ['ledger']`).
* `metrics` is sliced **by member** (`metrics/<key>`, 21 members) so its extractors can land in two
  batches: 15 with §5/§8/§9 (batch 3), 6 with §4 and CPU/memory/PERSIST/reliability (batch 4).
* The `preamble` slice (everything before `## 0.`, including `tables.md:3` and the `--raw` value)
  belongs to batch 1a; `--batch 1` expands to `1a,1b,1c`.
* Ownership of the **14 top-level keys** is complete, each exactly once:

  | batch | `tables.md` sections | `verdict.json` keys |
  |---|---|---|
  | 1a | `preamble`, §15 | `generated_by`, `raw`, `flat_mode`, `passes`, `rows` |
  | 1b | — | `bootstrap`, `thresholds` |
  | 1c | §1, §2 | `row_profiles` |
  | 2 | §0, §3 | `findings`, `findings_by_severity` |
  | 3 | §5, §8, §9 | 15 of the 21 `metrics/<key>` members: latency, UDP-loss, DNS, MIX loss, THRU goodput |
  | 4 | §4, §6, §7, §10, §11 | the other 6 `metrics/<key>` members: reliability, PERSIST, memory, CPU |
  | 5 | §12, §13, §14 | `control_blocks`, `dual_phase`, `ledger` |

  §4 is batch 4 because the headline matrix prints all twenty-one metric columns (D20.8): with it
  there, every batch's slices depend only on batches that already passed, and the differ carries no
  cross-batch annotation.

  The two `metrics` rows sum to the container, which is how the coverage check treats it: `metrics` is
  owned once every one of its members is.
* Extraction: `rm -rf /tmp/wf-synth`, unpack, then **assert** `*ledger*.jsonl` ≥ 2 before comparing;
  both sides are called with `/tmp/wf-synth/raw` written into the script.
* Three exit codes, and "nothing to compare" is never a pass. Every path that has nothing to compare
  leaves through one `fail()` helper that prints `rc=2: <reason>` and exits **2** — a broken tree, a
  missing file, an analyzer that did not run, a batch table that does not add up. (Before the review
  these were `raise SystemExit(<str>)`, which a process exits as **1**, the code that means "the
  content differs"; the measured exit codes are in the self-check table below.)

The three-state self-check, each run against the frozen golden:

| what was compared | command | result |
|---|---|---|
| the skeleton (16 headings with `<!-- TODO(batch N) -->`, `verdict.json` = `{}`) | `--batch 1a --cs-out /tmp/selfcheck/skeleton` | **rc=2**, §15 and the preamble differ, the 5 batch-1a verdict keys missing |
| the same, end to end (extract + assert + run the C# side) | `--batch 1a` | **rc=2**, identical consequence through the real invocation |
| the C# placeholder for a core table | `--batch 3 --cs-out /tmp/selfcheck/skeleton` | **rc=2**, §5/§8/§9 differ (they exist as TODO bodies) and all 15 metric slices missing |
| one digit changed (`preamble` line 3, `flat_mode`) | `--batch 1 --cs-out /tmp/selfcheck/mutated` | **rc=1**, both differences named with their line; the other 10 slices equal |
| the golden against itself | `--cs-out /tmp/selfcheck/as-golden` | **rc=0**, 51 slices equal (the slicing is total: 17 table slices + 13 verdict keys + 21 metrics) |
| a tarball with one ledger | the script under a patched `ARCHIVE`, as a process | **rc=2** before any comparison: `expected the tree beside the raw directory to hold at least two *ledger*.jsonl, found 1` |
| an analyzer that is not built | the script under a patched `ANALYZER`, as a process | **rc=2**: `…WinForward.E2E.Analysis: the analyzer is not built; run \`dotnet build WinForward.slnx -c Release\`` |
| an unknown batch name | `--batch 9` | **rc=2**: `unknown batch '9'; known: 1a, 1b, 1c, 2, 3, 4, 5` |
| a batch table missing a key | `check_coverage()` under a patched `BATCH_SECTIONS` | **rc=2**: `BATCH_SECTIONS does not own every slice: tables [], verdict keys ['ledger']` |

**The freeze chain reproduces the golden.** Extracting `synthetic-tree.tar.gz` at `/tmp/wf-synth` and
running the reference on it (`--raw /tmp/wf-synth/raw`) reproduces `golden/py-tables.md` and
`golden/py-verdict.json` byte for byte (`cmp` silent for both), so the hashes in §4 pin the whole
chain and not just the archive.

## 6. The skeleton

`benchmarks/WinForward.E2E.Analysis`, in `WinForward.slnx` under `/benchmarks/`, referencing
`Contracts`; `OutputType=Exe`, `AssemblyName=RootNamespace=WinForward.E2E.Analysis`; no
`InternalsVisibleTo`, no source generator, no `resources/`.

* **Read route (D20.6)**: `Loading/RecordScanner.cs` parses JSONL with `JsonDocument` and reads a
  record's family through `ArmKeys.Common.Record.Type`. Compile-level counter-proof: a temporary
  `JsonSerializer.Deserialize<T>` in that file failed the build with **2 errors, 0 warnings** — exactly
  `IL2026` and `IL3050` — and was removed again, after which the build is back to 0 warnings.
* **CLI**: `--raw --out --ledger --flat --warmup-seconds --resamples --seed` with the reference's
  defaults (`../raw`, `..`, `5.0`, `10000`, `20261006`) and a repeatable `--ledger`; the
  `DefaultMinPasses = 3` constant is carried too. An unknown argument or a missing `--raw` exits 2,
  like the reference.
* **Outputs**: `tables.md` carries all sixteen `## N.` headings, each body `<!-- TODO(batch N) -->`
  with its owning batch, and the preamble a `<!-- TODO(batch 1a) -->`; `verdict.json` is `{}` because
  no top-level key has been rendered — `Verdict/VerdictWriter.cs` holds the 14 keys in the reference's
  order and writes only those a batch has produced, so an unimplemented key is *absent* rather than
  empty. `<out>/plots/SKIPPED.md` is written unconditionally and is byte-identical to
  `verification/plots-SKIPPED.md` (`diff` empty; the text names no interpreter, virtual environment or
  package manager).
* **`scripts/analyze.sh`**: builds with `-c Release --no-restore`, then `exec`s the produced binary;
  it changes no working directory and parses no argument. Verified from `/tmp` (a different CWD) with
  identical output bytes to a direct invocation.
* **E4-b1b's queue** is explicit in the tree: `Stats/CpRandom.cs`, `Stats/BootstrapResampler.cs`,
  `Tables/VerbatimNumber.cs`, `Verdict/VerbatimJson.cs` each carry what they owe and why, and the
  runner prints the list on every run.
* Skeleton run on the frozen tree:
  `analyze.py: 3 pass(es), 27 row(s), 2 ledger(s), 7306 ledger record(s)` — the ledger count agrees
  with the golden's §14 row, and the ledger *order* (`ledger-main.jsonl` before `ledger-direct.jsonl`)
  is the reference's own discovery order, not a sorted one.

## 7. The gates

| Gate | Result |
|---|---|
| `dotnet build WinForward.slnx -c Release` | 0 warnings, 0 errors; the analyzer's line appears in the output |
| `dotnet test WinForward.slnx -c Release -m:1` | 14 test projects, `Failed: 0` in every one, exit 0 |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, **empty output** (0 bytes) |
| `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | XML parsed: **0** `<Issue>` entries |
| `effective-lines.py` over the four paths | no output, exit 0 |
| `bash benchmarks/WinForward.E2E/scripts/publish.sh` | exit 0 — but it does **not** compile the new project, see below |
| `selftest.sh` | not applicable: see below |

### 7.1 `publish.sh` does not cover the new project (A2-10, measured)

D20.7 assumed `publish.sh`'s bare build is the whole solution. It is not, and the reason is one line:
`repo=$(cd "$(dirname "$0")/.." && pwd)` resolves from `benchmarks/WinForward.E2E/scripts/` to
`benchmarks/WinForward.E2E`, not to the repository root. So

```bash
$ bash -c 'echo "$(cd "$(dirname benchmarks/WinForward.E2E/scripts/publish.sh)/.." && pwd)"'
/home/paff/Projects/WinForward/benchmarks/WinForward.E2E
$ (cd benchmarks/WinForward.E2E && dotnet msbuild -getProperty:MSBuildProjectFullPath)
/home/paff/Projects/WinForward/benchmarks/WinForward.E2E/WinForward.E2E.csproj
$ rg -c WinForward.E2E.Analysis benchmarks/WinForward.E2E/WinForward.E2E.csproj   # no reference
```

measured on the frozen tree, then: `publish.sh` exits 0 and publishes exactly the harness
(`WinForward.E2E[.exe]` plus `Contracts`), while `dotnet build WinForward.slnx -c Release` from the
repository root is the gate that does compile the new project. This is *not* a defect the new project
introduced — a bare `dotnet publish` in the repository root publishes the whole solution and fails on
`src/WinForward.Cli` with `NETSDK1102` (pre-existing) — but it does mean the six-gate list's claim
needs the solution build as its own line, which it has.

### 7.2 `selftest.sh` is not applicable

`benchmarks/WinForward.E2E/scripts/selftest.sh` runs the harness against itself on Linux (`client`
and `target` in one process tree) and then analyzes the result with the *Python* analysis. It has
nothing to assert about a project that reads records and renders tables: the analyzer's own end-to-end
exercise is the oracle itself (the differ extracts the frozen tree, runs the analyzer and compares 51
slices), which is strictly stronger than "it ran without crashing". The script is left untouched.

## 8. Deviations from the plan

1. **§4 is a batch-4 section, as D20.8 rules.** `table_headline` prints one column per metric (all 21
   of them), so with §4 in batch 3 the batch's criterion could never be met on its own. BATCH_SECTIONS
   and `TablesWriter` both put it in batch 4, the milestone is b1a + b3 (§5/§8/§9) + b4 (§4/§6/§7/§10/§11),
   and the differ needs no cross-batch annotation — it carried one only while the assignment was the
   older one.
2. **The clean tree does not exercise the JSON-`null` reading state.** Every arm in it sends something,
   so no cell is rendered as the empty cell the reference uses for a `null` reading. The rule is
   preserved and documented (`FROZEN.md` §5, `python-oracle-changes.md` §5.3) and must be asserted by
   the C# side's own unit tests in batch 1b; adding it to the fixture would have meant inventing a
   zero-denominator arm, which the batch plan puts on the *boundary* trees (D20.1).
3. **`--ledger` is a repeatable option, not a single value**, matching the reference; the skeleton
   already honours it (an explicit list wins over the search).
4. **The boundary trees are frozen by recipe and hash, not by storing four more tarballs** (D20.1 says
   "各自冻结"). `FROZEN.md` §4 records each recipe and the sha256 a regeneration must reproduce.
5. **The arm-level `error` record's shape** (`{type, arm, kind, label, error, message, detail,
   startedTicks, endedTicks}`) is not exercised as a *failed arm*; the `detail` path comes from the
   ledger's own error record, whose key set is the same one path. Driving a failed arm would re-shape
   many cells for a key that is already covered, so it is left to a later batch if the analysis's
   failure rendering ever needs it.
6. **`scripts/analyze.sh` lives in the analyzer project** (`benchmarks/WinForward.E2E.Analysis/scripts/`),
   read as `scripts/…` relative to the project the batch creates; the harness's own `scripts/` keeps
   `oracle-diff.py`, which the parent named with its full path.
