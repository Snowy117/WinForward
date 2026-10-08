# E4-c — independent check

Scope: the uncommitted E4-c work on top of HEAD `688d3b4` — §14.7 (the truncation caveat), the
`--zero-denominator` boundary tree and its re-freeze, the compensated-sum anchor, the §3 blank line,
and the retirement of `analyze.py` with its `.pyc`, its old outputs and the old `check-fairness.py`.
Everything below was re-run by the checker on the working tree; the checker's own harnesses live in
`/tmp/e4c-check/` (own slicer, own negative controls, patched no-op control, recipe regeneration).

**Verdict: E4-c may be committed.** Two frozen records carried stale hashes for two scripts (they were
rewritten after the tables were drafted); the check round re-measured both, re-established every claim
that depends on them, and corrected the three records plus the registry (§8). No functional defect was
found in the batch: every criterion it claims is reproducible from the frozen artifacts alone.

## 1. The frozen chain, re-run from scratch

The reference is still recoverable: the deletion is *staged*, not committed, so `HEAD` still carries
it — `git show 688d3b4:benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py` gives the
6165-line reference (`sha256 55067029555e48f3ed3ab171b447001e964a2b5837798fe7a6cc94f08d6b962a`), the
same file `FROZEN.md` §2.1 says to restore once the deletion is a commit.

| step | command | result |
|---|---|---|
| re-freeze the tree | `bash verification/freeze-tree.sh` | tarball `723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902` — **unchanged**; `raw/environment.json` `cb581a9c…` |
| regenerate the golden | `python3 /tmp/retired-reference/analyze.py --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out` | rc=0, 3 pass(es) / 9 row(s) / 21 metric(s) |
| compare A | `cmp out/tables.md golden/py-tables.md` | **empty** |
| compare B | `cmp out/verdict.json golden/py-verdict.json` | **empty** |
| the batch's own diff | `git status --short verification/` | only `FROZEN.md`, `synthetic/make_tree.py` (M) and `row-profiles.json`, `check-boundary-trees.py` (??) — tarball, golden and the three vector tables untouched |

Every other hash in `FROZEN.md` §1/§1.1 and `E4c-removal.md` §6 was re-measured as well:
`plots-SKIPPED.md` `39795a79…`, `check-fixture-drift.py` `acf41781…`, `row-profiles.json`
`f426b945…`, the three vector tables `b011711e…`/`3d256f59…`/`759c0834…`, `make_cp_vectors.py`
`3df6aea6…` and `scripts/analyze.sh` `4e1cbb13…` all match. The two that did not are §8's finding.

## 2. The full-batch criterion, from batch 1

| command | rc | slices |
|---|---|---|
| `oracle-diff.py --mode semantic --batch 1a / 1b / 1c / 2 / 3 / 4 / 5` | **0** each | 7 / 2 / 3 / 4 / 18 / 11 / 6 equal |
| `oracle-diff.py --mode semantic` | **0** | **51/51**, 0 structure / 0 value / 0 missing |
| `oracle-diff.py --mode semantic --tolerance 0` | **0** | 51 equal |
| `oracle-diff.py --mode byte --batch 1a / 1b / 1c / 2 / 3 / 4` | **0** each | 7 / 2 / 3 / 4 / 18 / 11 equal |
| `oracle-diff.py --mode byte --batch 5` | **1** | 6 slices, 1 differs |

The one byte difference is the registered one, and it is *only* member order: parsing both `ledger`
slices and re-serializing with sorted keys makes them **equal**, and the key orders differ at exactly
`ledger.passes.{pass1,pass2,pass3}.types` (reference `targetSummary, tcp, udpSummary, tcpSummary,
dnsSummary, error` vs produced `tcp, udpSummary, dnsSummary, tcpSummary, targetSummary, error`) — the
CPython `set` artefact `E4b5-semantic.md` §7.1 records. §3 of `tables.md` is byte-equal in batch 2,
which is D21.2 #1's blank line.

## 3. §14.7: "no arm-level cell consumes it", re-proved

`check-boundary-trees.py --workdir /tmp/e4c-check/boundary-final` → **rc=0, 17 checks** (12 guards +
5 controls), and independently of the script:

1. **My own slicer, my own loop.** Clean vs `--truncated-tcp 3`: **16 of 16** non-§14 slices
   byte-identical, §14 = clean §14 **+ 12 appended lines**; clean vs `--truncated-dns 2`: 16/16
   byte-identical, §14 + **15 lines**. Both appended blocks begin at `### 14.7 Truncated frames
   (target side, unattributable)`. The clean tree has no `truncated frames` match at all
   (`rg` rc=1). Both numbers are exactly what `E4c-caveats.md` §1.2 claims.
2. **The claim is a byte comparison, not a grep.** `guard_other_sections_unmoved` iterates
   `clean.sections` (preamble + §0–§13 + §15 = 16 slices) and requires byte equality, plus the
   `§14`-is-a-prefix property; the checker's own `rg` never appears.
3. **My own negative controls** (none of them the author's five), run through the same guard:
   writing `3` into a **§14.2 row cell** (inside §14) → **red**; writing it into a **§4 headline
   arm-metric cell** (`MIX udp.lossRate (pp)` of `wf-aot-opt`) → **red**. The author's control writes
   into §5; the two sections I used are untouched by it.
4. **The controls are not vacuous.** A patched copy of the checker in which
   `control/empty-cell-to-zero`'s mutation is a no-op makes exactly that control **FAIL** (rc=1) and
   nothing else move — a control that cannot fail would not be evidence.
5. **Measured extra, beyond the guard:** the whole `verdict.json` of each `--truncated-*` run is
   *canonically identical* (sorted-key re-serialization) to the clean run's. The counter reaches no
   verdict entry either, so "not spread to arms" holds on the machine-readable side too.

Observation, not a defect: the in-tree guard compares `tables.md` only, so a mutation that wrote the
counter into a `verdict.json` metric entry would not turn it red. The claim the docs make is about
cells, the guard proves it over all 16 sections, and my stronger measurement above shows the property
holds anyway; strengthening the frozen checker would move its hash for a shape no producer can reach
(the counter is consumed only by `Findings/LedgerTruncationTotals.cs` and `Tables/TableLedgerTruncated.cs`
— verified by `rg TruncatedFrames|truncatedFrames` over the analysis, which has no third consumer).

## 4. `--zero-denominator`

| claim | how it was measured | result |
|---|---|---|
| the flag is **off by default** | the generator from HEAD (index blob, `b3e946a7…`) and the current one, each writing the default tree into `/tmp/wf-synth` and tarred with `FROZEN.md` §2's command | both `723b7378…`, `cmp` equal — the default path did not move |
| the recipe hashes did not move | all five flags regenerated from the current generator | `d346f89d…`, `d3af3e07…`, `14ab9a6a…`, `f55f897d…` reproduce §4's table; `--zero-denominator` = **`e8326884…`**, the new row |
| C# survives the tree | `WinForward.E2E.Analysis --raw /tmp/wf-synth/raw --out …` on the zero tree | rc=0, both documents written |
| the four pointed assertions | guards `#zero-denominator/{empty-cell,null-passes,mix-gate,datagram-band}` | green, and the produced values re-read by hand: §4 `wf-fdd-opt × PERSIST responseRate` is `''`; `persist.responseRate` has `passes 0 / per_pass {} / null_passes 3 / median null` with the harness's own null-rate reason on all three passes; `mix.udp.lossRate` has `null_passes 1`, `per_pass {pass2 0.0417, pass3 0.0}` (pass2's arm-level value ×100); §14.2's row is `33 / 32 / ok / 600.0 / 0 / n/a` |
| the reference cannot judge it | restored reference on the same tree | **rc=1**, `TypeError: '<=' not supported between instances of 'float' and 'NoneType'` at `analyze.py:5037`, and **0 files** in the output directory — D20.1's basis, reproduced |
| the key set did not move | `check-fixture-drift.py` on the zero tree | rc=0, both directions empty (603 contract paths + 3 declared shapes) |

## 5. The removal is complete, and the rule source is live

- `rg -n 'analyze\.py' --glob '!.git' .` → **four files, ten lines**, exactly the set
  `E4c-removal.md` §5 lists: `FROZEN.md` (7: the frozen record and the restore recipe),
  `synthetic/make_cp_vectors.py:17` (a frozen docstring), `synthetic/make_tree.py:48` and
  `check-boundary-trees.py:25` (the crash's provenance). No live code names it; the CLI's own output
  prefix is `e2e-analysis:`.
- No live import: `rg 'import analyze|from analyze|load_analysis|resolve_analysis_dir'` → none outside
  documentation; `git ls-files '*.pyc'` is **empty** (the tracked byte-code file is gone).
- Each entry point was run once, on the final tree: `analyze.sh` rc=0 (from the repo root **and** from
  a foreign CWD, output byte-identical including `plots/SKIPPED.md`), `check-fairness.py` rc=0
  (14 guards), `--self-check` rc=0 (6 controls), `check-fixture-drift.py` rc=0,
  `check-boundary-trees.py` rc=0 (17 checks).
- `row-profiles.json` is the rule source, not decoration: flipping `proxifier`'s `udp` from
  `not-carried` to `proxied-native` **in place** turned `check-fairness.py` **rc=1**
  (`#17/rule`, `#18/section 8`); restoring the file returned its hash to `f426b945…` and the checker
  to rc=0.
- Deletions are staged, not committed: `git status` shows the eight documented `D` paths and the
  `R100` README move, and nothing else. No file still in use was deleted (every script that reads the
  tree, the contract and the analysis ran green afterwards).

## 6. Gates, on the final tree

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | rc=0, **0 Warning(s), 0 Error(s)** |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | rc=0, 14 assemblies, **1656 passed / 0 failed / 0 skipped** |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0, **0 bytes** of tool output |
| inspector | caches (`InspectCode`, `Transient`, `/tmp/JB`) cleared, `jb inspectcode -f=Xml -e=HINT … WinForward.slnx` | rc=0, `<Issues />` **empty** (0 `<Issue>`, 0 `<IssueType>`), no `CSharpErrors`, 1713 entries analyzed/inspected (1067 analyzed + 646 inspected) |
| lines | `effective-lines.py` over the four paths | rc=0, **0 bytes** of output |
| oracle | §2 | semantic all batches + full 51/51 + tolerance 0 green; byte 1a–4 green, 5 on the registered order |
| boundary / fairness / drift | §3–§5 | 17/17, 14/14 + 6 controls, rc=0 |

## 7. Conventions

- The three files this batch wrote are all far under the limit: `Tables/TableLedgerTruncated.cs` **97**,
  `Findings/LedgerTruncationTotals.cs` **91**, `tests/…/AnalyzerStatsAnchorTests.cs` **18** effective
  lines; the split left `Tables/TableLedgerCross.cs` at **312**. The largest file in the four-path run
  is the pre-existing `PlanFile.cs` (375).
- No changelog-style comments: a scan of the batch's touched files for `previously|used to|no longer|
  this batch|E4-*|formerly|had been|changed to|TODO|FIXME` finds only two pre-existing sentences that
  mean something else (`AnalysisRunner.cs:15` "this batch is not done", `TableLedger.cs:246` "the
  tolerances used to judge").
- `DescriptiveStats` is `public static class` with **`Sum` the only public member**; the analysis
  assembly has **no `InternalsVisibleTo`** (its csproj has none, and the four textual hits in the
  analysis are doc comments saying so) — D20.6 holds.

## 8. What the check round fixed

1. **Two stale frozen hashes.** `FROZEN.md` §1 recorded `60d96cf8…` for `synthetic/make_tree.py` and
   `8c370853…` for `check-boundary-trees.py`; the files as delivered hash `b946a38d…` and
   `ac83aa2a…`. Both files were rewritten after those tables were drafted (mtimes 7 s apart, before
   the evidence docs), and every functional claim in §2–§5 was re-established on the bytes as they now
   hash, so the records were corrected — not the artifacts. Corrected in `FROZEN.md` §1 and its
   re-freeze note, `E4c-caveats.md` §2.2, `E4c-removal.md` §6, and the `E4-c` registry entry in
   `research/semantic-fixes/index.jsonl`; each place now says why.
2. **A mislabelled line count.** `E4c-caveats.md` §5 quoted "76 files / 9752 effective lines" as the
   four-path run's own numbers; they are the analysis project's (the four paths hold 248 files /
   28326 effective lines, and the gate prints nothing on success). The row now says both.
3. **A stray blank line** at the end of `Tables/TableLedgerCross.cs`, left by the §14.7 split, was
   removed. It is output-inert and every gate was re-run afterwards (build, tests, format, cold
   inspector, oracle, boundary, fairness, drift, lines, `analyze.sh`).

## 9. Deviations and observations left registered

1. **`benchmarks/WinForward.E2E/scripts/publish-campaign.sh` still runs the deleted program** — its
   step 4 does `cd "$results/analysis"` and `python3 analyze.py …`. The file is **gitignored** and the
   PRD explicitly excludes it from this task, so it is not touched; the next real campaign will have to
   retarget it to `scripts/analyze.sh`. Untracked, so it cannot affect the commit.
2. **The `verdict.json` side of "not spread to arms" has no in-tree guard** (§3): measured identical,
   but only the `tables.md` claim is asserted. Strengthening the frozen checker is a hash-moving change
   with no reachable producer, so it is registered rather than done here.
3. **`FROZEN.md` §2.1's `git log --diff-filter=D` recipe** cannot name the commit until this batch is
   committed; until then the reference is `git show 688d3b4:…` (which is what §1 used). The recipe is
   correct for the world the deletion is in.
4. **`.trellis/spec/backend/measurement-harness.md:210`** still names `analyze.py` in parentheses; the
   batch registered it for E5 and the check round confirms it is still there, unedited.
5. **`E4b5-semantic.md` §10.6's `dns-clientless` shape** stays an observation (a tree mutation, not a
   flag), as `E4c-caveats.md` §2.5 and §6.4 say.

## 10. For the parent

Commit it. The staged deletions, the re-frozen tree, the corrected records and the whitespace-only code
edit all sit in one consistent state, and the tree the gates ran on is the tree that would be
committed: build 0 warnings, 1656 tests, empty format, empty inspector report, silent line gate,
semantic oracle 51/51. The only follow-ups are the gitignored `publish-campaign.sh` (§9.1) and the E5
doc sweep (§9.4), neither of which this batch owns.
