# E4-b1c2 — `row_profiles`, `findings`, the §0/§1/§2/§3 sections, and the gates behind them

Batch E4-b1c + E4-b2 of `.trellis/tasks/10-07-e2e-e4-analyzer`, landed together. Authority:
`design-decisions.md` **D21** (semantic comparison replaces byte equality; numbers by the reference's
printed precision, key order free, wording required to match) and **D21.1** (the tolerance direction),
D20.2 (slicing and batch ownership — `BATCH_SECTIONS`), D20.7 (the batch table), D20.8, and
`research/python-oracle-changes.md` (`analyze.py`'s change list and its rendering quirks).

Its criterion is `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic --batch 1c`
⇒ **rc=0** *and* `--batch 2` ⇒ **rc=0**, with `--batch 3/4/5` still **rc=2**.

**Result: `1c` rc=0 (3/3 slices), `2` rc=0 (4/4 slices), `1a` rc=0 (7/7), `1b` rc=0 (2/2), `3/4/5`
rc=2 (15/6/3 missing slices, and their own three sections still placeholders).** The frozen tree, the
frozen golden and the reference `analyze.py` were not touched.

## 1. What the batch is, and what it deliberately is not

- **§1 `What each row is and what it does with UDP`** — the campaign's own design table, rendered from
  `ROW_PROFILES`. It is the single place the design is recorded, and every `not measured in this row`,
  `not carried (UDP bypassed)` and comparability rule elsewhere in the document is derived from it.
- **§2 `Environment and provenance`** — the machine, the runtime, the plan hashes, the ledger
  attribution line, the declared loss window, arm durations, the sampled process, the per-row config
  digests and each run's metadata. It is the batch's own section, not a helper of §3 (§7).
- **§0 `Correctness findings`** — the severity counts, the three tables that carry a correctness
  failure rather than a performance result, and the whole finding list by severity.
- **§3 `Gate table`** — the two flow gates and the measurement-validity gates, one line per (pass, row),
  plus the per-row verdict summary.
- **`verdict.json`'s `row_profiles` / `findings` / `findings_by_severity`** — the design table as data,
  the finding list in collection order, and the same list counted by severity.

What the batch deliberately does **not** do: §12/§13/§14 and `verdict.json`'s `control_blocks` /
`dual_phase` / `ledger` keys (batch 5), the metrics (batches 3/4), and any rule the reference does not
have. Two computations the findings need — the control-block drift and the ledger views — are
implemented here because §0 cannot be rendered without them; their *sections* and *verdict keys* stay
in batch 5 (§10.1).

## 2. The module map

Every file carries the reference function named beside it. Effective lines are
`effective-lines.py`'s count (blank lines and whole-line comments excluded).

| file | effective lines | what it carries |
|---|---|---|
| `Tables/TableRowProfiles.cs` | 98 | `table_row_profiles` — §1 |
| `Tables/TableEnvironment.cs` | 321 | `table_environment` — §2 |
| `Tables/TableFindings.cs` | 258 | `table_findings` — §0.1 … §0.4 |
| `Tables/TableGates.cs` | 115 | `table_gates` — the section, its two sub-tables and the reference's caption |
| `Tables/GateFlow.cs` | 307 | `gate_flow_row`, `check_verdict`, `observed_carriage`, `expected_carriage` |
| `Tables/GateValidity.cs` | 248 | `gate_validity_row`, `base_loss_rates` |
| `Tables/FlowModel.cs` | 148 | `client_flow_model` and the terms it sums |
| `Checks/IdentityChecks.cs` | 275 | `udp_identity`, `dns_partition`, `reliability_invariants`, `identity_checks`, `identity_blocked_prefix`, `lane_witnesses`, `foreign_connection_sources` |
| `Checks/ControlDrift.cs` | 209 | `control_ordering`, `control_drift`, `control_statement` |
| `Findings/FindingsCollector.cs` | 292 | `collect_findings` (dual truth, foreign connections, identities, lane witnesses, latency gates, sampling) + `findings_by_severity` + the deduplication |
| `Findings/FindingsCollector.Structure.cs` | 201 | `collect_findings`' structural half (arms present, DNS ports, control blocks, design notes) |
| `Findings/DualFindings.cs` | 252 | `dual_records`, `dual_row_summary`, `dual_findings` — the path-interference findings |
| `Findings/LedgerViews.cs` | 286 | `ledger_views` — record attribution, arm views, the attribution sentence |
| `Findings/LedgerFindings.cs` | 210 | `ledger_findings` — the per-arm and per-pass findings |
| `Findings/LedgerClientCounts.cs` | 153 | `client_connection_count`, `client_datagram_count`, `declared_udp_path`, `endpoint_partition` |
| `Findings/LedgerDnsTotals.cs` | 99 | `ledger_dns_totals` |
| `Findings/Finding.cs` | 20 | `Finding`, `SEVERITY_ORDER`, the pre-declared thresholds |
| `Stats/DescriptiveStats.cs` | 112 | `quantile`, `median_iqr`, `fmt_stat`, `summarise_verdicts`, `holm_adjust`, `within_tolerance` |
| `Stats/BootstrapPair.cs` | 200 | `bootstrap_pair`, `decide` |
| `Model/ArmAccess.cs` | 117 | `arm_result`, `arm_number`, `arm_text`, `arm_flag`, `arm_ratio`, `arm_latency` |
| `Model/RunClocks.cs` | 85 | `tick_frequency`, `arm_window_seconds`, `arm_utc_window`, `parse_utc` |
| `Model/JsonValue.cs` / `JsonText.cs` / `CampaignQueries.cs` | 67 / 39 / 39 | `dig` / `dig_present`, Python's `str()`, the per-pass lookups |
| `Model/LedgerData.cs` + `Loading/LedgerLoader.cs` | 9 + 41 | `load_ledger`, read once per campaign |
| `Json/PythonExponential.cs` | 15 | the one `%.3e` gate cell |
| `Model/RowProfiles.cs` / `ClientRun.cs` / `CampaignModel.cs` / `CampaignLoader.cs` / `RunLoader.cs` | 144 / 28 / 47 / 205 / 195 | the carriage labels, the config digests, `order.txt`, `environment.json` |

The project is 55 files and 6190 effective lines; the largest file is `TableEnvironment.cs` at 321,
every one of them under the 400-line rule (`effective-lines.py` over the four paths is silent, §9).

## 3. `collect_findings`, rule by rule

The reference collects in a fixed order and the order is output: §0.4 lists each severity's findings in
collection order and `verdict.json` publishes the whole list in it. The C# side keeps the same order,
including the fact that `ledger_findings` runs *before* the design notes and `dual_findings` is
appended after everything else.

| # | reference rule | severity | where |
|---|---|---|---|
| 1 | `dual-truth-missing`, `direct-leak`, `direct-leak-unreadable`, `dual-lane-missing` | caveat / correctness | `FindingsCollector.DualTruth` |
| 2 | `foreign-connection` (per counter, then per MIX desktop) | correctness | `FindingsCollector.ForeignConnections` + `IdentityChecks.ForeignConnectionSources` |
| 3 | `udp-identity`, `dns-partition` (violation ⇒ harness error; uncheckable ⇒ caveat `*-uncheckable`), `scheduled-attempts` ⇒ caveat | harness / caveat | `FindingsCollector.Identities` |
| 4 | `lane-witness-zero` (per witness, `ARM_ORDER`), `idle-lanes` | harness | `FindingsCollector.LaneWitnesses` |
| 5 | `latency-backlog-drops`, `latency-schedule-truncated`, `latency-ceiling-reached` (LAT/LATLOAD/BASE, that order) | harness / caveat | `FindingsCollector.LatencyGates` |
| 6 | `sampler-error` (process names sorted, deduplicated), `sample-read-error` | harness | `FindingsCollector.Sampling` |
| 7 | `declared-arm-missing`, `undeclared-arm-present` | caveat | `FindingsCollector.Structure.Arms` |
| 8 | `dnsalt-on-53`, `dns-port-collision`, `dns-arm-port` | harness / informational | `FindingsCollector.Structure.DnsPorts` |
| 9 | `control-block-missing`, `control-bracketing` | caveat / correctness | `FindingsCollector.Structure.ControlBlocks` |
| 10 | `control-drift` / `control-drift-undecided` (per metric, when the bootstrap has no error) | correctness / caveat | `FindingsCollector.Structure.DriftFindings` + `Checks.ControlDrift` |
| 11 | `ledger_findings`: `no-target-ledger`, `empty-target-ledger`, `ledger-bad-lines`, `ledger-connection-mismatch`, `ledger-datagram-mismatch`, `ledger-window-ambiguous`, `ledger-source-overflow`, `ledger-endpoint-overlap`, `ledger-dns-udp-mismatch`, `ledger-dns-tcp-mismatch`, `ledger-write-errors` | caveat / harness / correctness | `Findings.LedgerFindings` |
| 12 | `udp53-direct`, `udp-not-carried` (per row id, in `row_ids` order) | informational | `FindingsCollector.Structure.Design` |
| 13 | `dual_findings`: `direct-lane-latency`, `direct-lane-loss`, `dual-shape-mismatch`, `direct-leak-unreadable` | path-interference / caveat | `Findings.DualFindings` |

Three behaviours are worth naming because they are rules rather than ports:

- **Deduplication is by `(severity, kind, scope, detail)` and keeps the first occurrence.** A rule that
  fires twice with the same sentence cannot inflate a count.
- **The wording is the rule.** The numbers inside a sentence are compared under the differ's tolerance
  (D21), the words are not: `directLeak=2: the product intercepted 2 application connection(s) it was
  configured to send direct` is the reference's sentence, singular/plural included.
- **`%d` on a float truncates toward zero**, and that is what several sentences quote (`60.0` never
  occurs, but `5999.999` would print `5999`). `Int()` is the one helper that does it.

## 4. The three keys, slice by slice

| slice | result | reference | notes |
|---|---|---|---|
| `verdict.json:row_profiles` | equal | `ROW_PROFILES` in declaration order | nine rows, ten members each; the member order is the reference's, the differ compares structure and ignores key order (D21) |
| `verdict.json:findings` | equal | `[finding.as_dict() for finding in ctx.findings]` | 23 findings, in collection order |
| `verdict.json:findings_by_severity` | equal | `{severity: count}` over `SEVERITY_ORDER` | the counts, not the lists: `6/2/8/1/6` |
| `tables.md:1` | equal | `table_row_profiles` | 9 rows + 3 prose blocks |
| `tables.md:2` | equal | `table_environment` | 13 blocks: the info table, the window table, the durations, the sampling table, the config hashes and the run metadata |
| `tables.md:0` | equal | `table_findings` | 4 tables + the severity headings |
| `tables.md:3` | equal | `table_gates` | 3 sub-tables; §3.1's 27 rows |

The differ's structural assertions are the ones that keep this honest: the 16 headings, each table's
column names, its row identity columns, every row's cell count and every cell's **category**
(number / `n/a` / `n/a (reason)` / empty / bound). `n/a` and `0` are different categories, which is
the rule that makes the "a rate the harness wrote null is not a zero" contract enforceable (§8).

## 5. `§0` and `§3`: the two sections a reader starts from

**§0** is four sub-sections: `0.1 directLeak` (every row that ran a dual phase, pass by pass),
`0.2 foreignConnection` (the five counters plus the per-desktop ones, or the "None observed" sentence
with its bound `3/readable`), `0.3 UDP accounting identities` (the ones that did not hold, plus the
held/uncheckable counts) and `0.4` the whole list by severity. The severity line is generated from the
same list, so the counts and the table cannot disagree.

**§3** is three sub-tables. §3.1's `notes` cell is built in the reference's own order — denominator
gaps, the `tcpAttempts` terms, the `udpArms` counted, the U-oT-control adjustment, "both carriages
observed", load errors, a failed run, bad JSONL lines, the control-block sentence, then
`failed:`/`undecidable:` — and the verdict is the checks' own: `FAIL` if any failed, `n/a` if any could
not be decided, else `PASS`. An `exempt` check (a control block, a product that cannot carry UDP) does
not fail the row, which is why `control-pre`/`control-post` are `PASS` in §3.1 while every row in a
pass with a dirty control block is `FAIL` in §3.2. That asymmetry is the reference's and is preserved.

## 6. `#17` / `#18` — the two disclosures, in the reference's own output form

E3 implemented both on the Python side; this batch only renders them. No new rule was invented.

**`#17` — a product that cannot carry UDP.** `proxifier`'s profile says
`udp = not-carried`, so:

- §1 carries the marker three times: in its opening prose ("Every `not measured in this row`,
  `not carried (UDP bypassed)` and comparability rule …"), in `proxifier`'s own cell in the last column,
  and in the caption under the table (*"`proxifier` cannot proxy UDP at all, so every UDP cell for that
  row reads `not carried (UDP bypassed)` and the row is excluded …"*);
- §3.1's `udpGate` is `exempt` with the detail *"this product cannot proxy UDP"*, and the caption
  exempts the row by name (1 occurrence in §3);
- `verdict.json` carries exactly one `udp-not-carried` finding:
  `informational / proxifier / every UDP cell for this row reads 'not carried (UDP bypassed)' and the
  row is excluded from the UDP-accuracy and DNS-latency comparisons; its TCP results are unaffected`.

**`#18` — the port-53 DNS path differs per row.** The `udp53_carriage` of each row's profile decides it:

- §1's `what it does with UDP/53` column prints the label per row (`relayed through the proxy`,
  `direct via the local DNS target (udp/53 forwarded verbatim, bypasses the proxy)`,
  `direct via the product's hardcoded port-53 pass-through`, `direct (the product cannot carry UDP at
  all)`, `n/a (no product loaded: the direct path is the only path)`);
- §3.1's `UDP/53 carriage` column repeats it per (pass, row), and a row whose port-53 path is direct
  gets the `udp53Carriage` check with the detail `direct-path measurement: …`;
- `verdict.json` carries five `udp53-direct` findings (every row whose `udp53` is not `relayed` and not
  `n/a`): `wf-aot-opt`, `wf-fdd-opt`, `wf-aot-nativeudp`, `proxifyre`, `proxifier`, each saying *"the
  port-53 DNS arm is a direct-path measurement on this row (…); the cross-product DNS comparison runs
  on DNSALT"*.

## 7. §2, and the one place the plan and the mechanism disagreed

**`implement.md`'s batch table used to list §2 twice**: in `E4-b1a` ("`Loading` + `Model` + §15 可用性 +
§2 环境") and in `E4-b1c` ("§1 + §2"). `oracle-diff.py`'s `BATCH_SECTIONS` — the mechanism's single
source of truth (D20.2) — owns §2 alone and gives it to **1c**:

```python
"1c": {"tables.md": ["1", "2"], "verdict.json": ["row_profiles"]},
```

`E4-b1a` therefore landed §15 and the preamble only, and §2 was never rendered: at the start of this
batch `--batch 1c` was rc=2 (`tables.md:2: DIFFERS`, `row_profiles: MISSING`). Since the batch's
criterion is `--batch 1c` rc=0, §2 had to land here. The table in `implement.md` now says so (the
`E4-b1a` row no longer claims §2), and this note is the record of the discrepancy.

§2's columns come from three batches' worth of machinery, and each column names where its data is
read:

| §2 column | where the data comes from | batch that owns it |
|---|---|---|
| row ids, passes, arms present, tick frequency, processors, OS, runtime, plan hashes, target | `run.json` through the b1a model (`CampaignModel`, `RunClocks`) | 1a |
| steady-state warmup, flat mode | the options and the campaign flag | 1a |
| run order (`order.txt`) | `CampaignLoader.ReadOrder` (this batch: the reference reads it in `discover()`) | 1a's loader, wired here |
| target ledger(s): paths, record and bad-line counts, attribution sentence | `LedgerLoader` + `ledger_views` | this batch |
| `environment.json` (sorted keys) | `CampaignLoader.ReadEnvironment` | this batch |
| declared loss window per arm, arm durations, sampling table, config digests, per-run metadata | `arm_number`, `arm_window_seconds`, `PythonGlob.ConfigFiles` + SHA-256 | this batch |

Nothing in §2 depends on a later batch: the window table reads published records, the durations read
the client's own ticks, and the config digests read the row's own files. §2 is therefore complete, not
a placeholder — verified by `--batch 1c` rc=0 with the section compared as 13 blocks.

## 8. Negative controls

Every control is one edit on the *produced* side (`--cs-out`, so the analyzer is not re-run) followed by
the differ. Each is red where it must be, and the base copy is green.

| control | edit | batch | rc | what the differ names |
|---|---|---|---|---|
| C1 | §1's header column `product` → `vendor` | 1c | **1** | `[structure] tables.md:1.table1.header` |
| C2 | §2's `analysed process` cell `n/a (no non-self samples)` → `0` | 1c | **1** | `[structure] …[pass=pass1&row=control-post].analysed process: expected 'n/a (reason): …' vs actual 'number: 0'` |
| C3 | §1 loses the `control-post` row | 1c | **2** | the row is a missing row, not a difference |
| C4 | §2's `Proxifier x260` → `x261` | 1c | **1** | `[value] tables.md:2.Product process sampling[…].all non-self processes` |
| D1 | §3.2's `LOSS clientSendLoss` cell `n/a` → `0` | 2 | **1** | `[structure] …LOSS clientSendLoss: expected 'n/a: n/a' vs actual 'number: 0'` |
| D2 | §3.1 loses a row | 2 | **2** | missing row |
| D3 | `findings[0].detail`'s `2` → `9` | 2 | **1** | `[value] verdict.json:findings[0].detail` |

C2 and D1 are the two directions of the category rule (a cell that is a marker on one side and a number
on the other is a *structure* difference, not a value one); C3/D2 are the row-set rule (a deleted row is
rc=2, never rc=0 with fewer rows); C1 is the header rule; C4/D3 show that the value path is still
exercised under the tolerance — a changed integer outside the band is red, not tolerated.

## 9. The gates

All six, on the final tree, in the repository's own order:

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed** in 14 assemblies |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **0 bytes of output**, rc=0 |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx` | **`<Issue>` 0**, no `CSharpErrors` |
| lines | `effective-lines.py` over the four paths | **0 bytes of output**, rc=0 (largest file 321) |
| oracle | `--batch 1a/1b/1c/2` | rc=0/0/0/0; `3/4/5` rc=2 |

The two long gates report here:

- **tests**: `dotnet test WinForward.slnx -c Release -m:1` — **14 assemblies, 1655 passed, 0 failed,
  0 skipped**, `TEST_RC=0` (`/tmp/e4b1c2/test-final2.log`).
- **the suite is allocation-sensitive and can flake under load.** One run, started while the oracle
  sweep was still running in another process, failed
  `WinForward.Performance.Tests.SweepAllocationGateTests.FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes`
  (`Expected: 0, Actual: 7824`) — an allocation gate in the core flow table, which no file this batch
  touched is reachable from. Re-run alone it passes (12/12), and the whole suite re-run undisturbed is
  green; the flake is recorded here so the next reader does not read it as a regression.
- **inspector**: `jb inspectcode -f=Xml -e=HINT` — the report parses with **`<Issue>` 0**,
  `<IssueType>` 0 and no `CSharpErrors` (`/tmp/e4b1c2/inspectcode-gate.xml`, after a first clean report in `/tmp/e4b1c2/inspectcode-final.xml`; re-confirmed on the frozen
  tree). The run before the last two fixes reported 85 findings, all in the files this batch wrote or
  extended: most were "this member can be narrower" and "this `using` is unused" and were fixed by
  narrowing or deleting the state (the b1a rule: state comes back with its consumer), the five
  intentional exact-float comparisons were expressed without `==` (`is 1.0`, `.Equals`, `is not 53.0`)
  rather than suppressed, and one inspection (`ConvertToExtensionBlock` on `Model/CampaignQueries.cs`)
  is turned off in `.editorconfig` for that one file, with the reason written beside it.

## 10. Deviations and observations

**10.1 Two batch-5 computations are implemented here, and only for the findings.** §0 cannot be
rendered without `control_drift` (finding 10) and `ledger_views` + `endpoint_partition` (finding 11):
one golden correctness finding is a control drift and another is a ledger endpoint overlap. Both are
implemented in full (`Checks/ControlDrift.cs`, `Findings/LedgerViews.cs`, `Findings/LedgerDnsTotals.cs`)
so the rules are not half-ported, but their **sections** (§13, §14) and **verdict keys**
(`control_blocks`, `ledger`, `dual_phase`) stay in batch 5, which is why `--batch 5` is still rc=2.
`dual_records` is in the same position: `dual_findings` needs it, §12 does not exist yet.

**10.2 `Collect` is the only entry point; the order is the contract.** `FindingsCollector.Collect`
composes thirteen rule groups in the reference's order and deduplicates at the end. A finding added in
the wrong group would move in `verdict.json` and in §0.4 without changing any count — the differ
compares the array in order, which is what makes this observable at all.

**10.3 `MIX`'s UDP block is prefix-blocked, and the gate table can see it.** When a MIX record violates
its UDP identity, `arm_number` refuses every `metrics/classes/udp/…`, `metrics/udp.sent`,
`metrics/udp.lossRate` and `metrics/clientSendLoss` reading of that arm with the identity's own detail.
That is why pass1/proxybridge's `MIX idleLanes` is `n/a` rather than a number even though the record
publishes the field: the reference excludes it, and the exclusion is visible in §3.2.

**10.4 `%d` truncation and the `n/a (reason)` category are load-bearing.** Two sentences quote a float
with `%d` (`foreignConnection`, `idleLanes`), and several cells are `n/a (<reason>)` rather than `n/a`.
Both are compared as structure, so a "cleaner" implementation that printed `5999.999` or a bare `n/a`
would be red, not tolerated.

**10.5 The observation the first run produced was a real bug, not a tolerance question.** The first
`--batch 2` run carried a 24th finding (`wf-aot-opt` at 5.4 % worse than the best direct lane) where the
reference has 23. The cause was the dual summary resetting its per-row accumulator on every record
(`summary.ContainsKey` guarding a dictionary that is only filled after the loop), so each row's median
was its last pass instead of the median of its passes. Fixed, not tolerated: a value difference inside
the tolerance would have been registered as an observation, but this one was a wrong rule.

**10.6 `Stats/BootstrapResampler.cs` and `Checks/InvariantChecks.cs` are gone.** Both were the
placeholder files that carried a "what this batch still owes" string; the bootstrap is now
`Stats/BootstrapPair.cs` (the pair and the verdict), and the invariant checks are `Checks/IdentityChecks.cs`
plus §3's own gates. Keeping a stub whose name promises the implemented work would have been a second
definition of the same thing, so the two stubs were deleted and the runner's run summary lost their
lines (`MetricCatalogue.Pending` stays — the metrics really are still missing).

**10.7 No test was added for this batch.** The criterion is the oracle, and the oracle compares 51
slices of which these seven are new; the project's own unit tests cover the primitives the tables are
built from (b1b's number/JSON vectors), not the tables. The negative controls in §8 are the batch's
regression evidence, and they are commands rather than test files because the differ is the only
consumer of the rendered text.

## 11. For check

1. **Re-run the criterion from a clean tree**: `--batch 1a,1b,1c,2` rc=0 and `--batch 3,4,5` rc=2 —
   in particular that `3/4/5` are rc=2 *because of missing slices*, not because a structure rule
   changed inside the sections this batch added.
2. **Test the two category controls against the differ itself** (C2, D1): they are the only evidence
   that the `n/a` / number distinction is enforced rather than declared, and they are one-line edits
   on a copy of `/tmp/wf-oracle/cs`.
3. **Check §2's batch claim** (§7): the section is rendered here because `BATCH_SECTIONS` gives §2 to
   1c; confirm that `implement.md`'s table no longer lists it twice, and that nothing in §2 reads a
   batch-3/4/5 key.
4. **Audit the `Collect` order** against `collect_findings`' own order (§3's table): the list is
   compared in order, so a group that moved is a red that no count would show.
