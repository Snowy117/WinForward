# E5-b2 old-data re-analysis (B10)

Status: local analysis complete on 2026-10-19. The release campaign (B8/B9) has not run and no VM was
provisioned; this note makes no campaign claim. The archive's own `reanalysis-notes.md` remains the
B1 baseline note; this checked-in note is the B10 re-analysis record.

## 1. What was compared

Archive: `~/bench-archive/2026-10-08-e5b2`.

| Item | Value |
|---|---|
| Campaign | `2026-10-08-e5b2` |
| Passes | 3 (`pass1`..`pass3`), all measured; no discarded warm-up |
| Rows | 9 (2 control blocks, 7 product rows) |
| Archive baseline | `analysis/` (B1, generated at `3f4d5ab`) |
| Older baseline | `original-analysis/` (2026-10-09, the campaign's own output) |
| New legacy output | `/tmp/wf-b10/legacy-analysis/` |
| New release diagnostic | `/tmp/wf-b10/old-data/release-diagnostic/` (diagnostic only, not a release claim) |
| Repository HEAD | `5b0083d` |

The legacy run was:

```bash
cd ~/bench-archive/2026-10-08-e5b2
bash /home/paff/Projects/WinForward/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh \
  --raw raw --ledger ledger-main.jsonl --ledger ledger-direct.jsonl \
  --out /tmp/wf-b10/legacy-analysis
```

Exit 0; 3 passes, 9 rows, 2 ledgers, 27 loaded runs.

## 2. Legacy path on the old tree

### 2.1 Result: not byte-identical to the B1 baseline, byte-identical to B2

| File | Size (bytes) | SHA-256 |
|---|---:|---|
| Archived `analysis/tables.md` | 209,144 | `863180beb2689c674e805683c3124dcd5c2ca00e7d518632c4e8f86613b168d7` |
| Archived `analysis/verdict.json` | 476,329 | `becc3f253829bdc716f0a2a69e54de81ef806eb68ec010b1aa5fb46e541811ea` |
| HEAD legacy `tables.md` | 211,786 | `6540afd03d8ef40928abdea692384e89909fee2d8f947b358d39bcfb9dcfcb99` |
| HEAD legacy `verdict.json` | 480,590 | `d00363223f7185ce237ce7d5e1afe10bbae496443b5ba3eb15fb096e12196aa3` |

The difference is fully attributable to B2 (`8e01d51`, "land the release caliber and the default
configuration reference row"). The proof is a build/run at the two commits:

- analyzer from `3f4d5ab` on the archive -> the two files are byte-identical to `analysis/`;
- analyzer from `8e01d51` (B2) on the archive -> the two files are byte-identical to the HEAD run.

The B1 commit `2c306f3` is a child of B2 in this history, so the archived baseline was produced before
B2 even though its commit sits after it. B2 deliberately changed the compiled row table:
`wf-aot-nativeudp` moved from `udp only`/dual `false` to `full`/dual `true` and became the reference
row; `wf-aot-opt` and `wf-fdd-opt` were relabelled as the opt-in UoT variant. The synthetic tree and
golden were re-frozen in the same commit. No analyzer code path was changed by B3-B7 in a way that
moves the legacy documents: B2's output equals HEAD's byte-for-byte.

### 2.2 What moved

`tables.md`: 1,302 -> 1,313 lines, 19 changed hunks, all in the row-profile/findings/availability
sections:

- the §1 row-profile labels and the "WinForward rows are a deliberate design" paragraph;
- the three `dual-truth-missing` rows and three `declared-arm-missing` rows for `wf-aot-nativeudp`,
  plus the two `dual-shape-mismatch` and one `direct-leak-unreadable` rows, and the §0 finding counts;
- the §4/§5/§8/§9/§10/§14 availability cells that changed from `not measured in this row: ... not in
  the udp only plan` to `declared-absent` / `declared in the full plan but no arm file or result was
  found`, and the §14.2 datagram cells;
- the inserted `wf-aot-nativeudp` dual-phase row (now that the profile declares a dual phase).

`verdict.json`: 291 JSON leaf changes, classified as:

| Area | Leaves | Nature |
|---|---:|---|
| `metrics/*` | 90 | `status` / `status_reason` / pair `reason` strings for `wf-aot-nativeudp`; **zero** numeric, CI, p-value or verdict changes |
| `dual_phase` | 48 | the `wf-aot-nativeudp` dual row inserted; later rows shift |
| `row_profiles` | 9 | the B2 profile/reference labels |
| `findings` and finding counts | 143 | the added dual/declared-arm findings and the measurement-caveat count (40 -> 48) |
| `findings_by_severity` | 1 | same count change |

There are no changed numeric leaves under `metrics`; the only changed numeric leaves are the
`dual_phase` row array and the caveat count. No old metric estimate, interval, p-value or verdict
changes.

### 2.3 Frozen-path gates

- `oracle-diff.py --mode semantic`: 51 slices, 0 differ, exit 0.
- `oracle-diff.py --mode byte`: 25 value differences, exit 1. These are the known non-byte identity of
  the C# port (integer-valued floats printed without `.0`, the runtime-dependent resampling
  sequence), not a B10 regression. Byte mode is the structure-surface regression, not the batch
  criterion (D21); the frozen baseline is semantic.
- `check-fairness.py`: all 14 guards held, exit 0.
- `check-fixture-drift.py`: 603 contract paths, 605 fixture paths, 3 declared unobserved shapes,
  no drift in either direction, exit 0.
- `check-boundary-trees.py`: truncated-TCP 5/5, truncated-DNS 5/5, zero-denominator 7/7, all
  negative controls rejected the mutation, exit 0.
- `check-readme-contract.py`: 111 keys against 403 declared constants, ok, exit 0.

The old archive is not a byte-identical input to the *current* row table, but the legacy path itself
is stable: reproduce a baseline at the commit that produced it, and do not compare two profile
generations as if one were a regression.

## 3. Release-rule re-analysis

### 3.1 What the release path requires

`--release --thresholds <file>` requires a complete pre-registered file: a single `caliber` plan with
its SHA-256 and all ten full-plan arm durations, `referenceRow`, `opponents`, the four primary bands,
the REL capability bound, `minPasses`/`discardPass`, and `warmupSeconds`/`clock`. A release verdict is
only meaningful when the band was written before the campaign (the calibration's
`B = max(B_business, 1.5 x spread)`).

### 3.2 What the old campaign has

| Requirement | E5-b2 |
|---|---|
| Single release caliber | No: 5 full rows on `plans-windows/full-shape-plan.json` (`ed0a3477...`), both controls on `plans/base-plan.json` (`6c28a3d0...`), `wf-aot-nativeudp` on `plans/udp-plan.json` (`b0292c18...`), `wf-aot-dnsrelay` on `plans/dns-plan.json` (`4888274b...`) |
| Old full-plan bytes in the archive | No. The archive has the raw tree, ledgers, environment and logs, but not the plan. The repository path now hashes to `d1a20255...` because B4 added `stall=25` to `REL.modeMix`; the manifest still records `ed0a3477...`. The exact old bytes are recoverable from `3f4d5ab` and hash to `ed0a3477...` (verified in this analysis). |
| New reference row on the full plan | No. The design's reference `wf-aot-nativeudp` ran the 3-arm udp plan; it has no full-plan `LAT` TCP histogram and no `REL`. The release path also requires all ten `caliber.seconds` arms, so a partial plan cannot be the release caliber. |
| Pre-registered calibration band | No. The old analyzer's thresholds are business constants (5 % latency, 10 % CPU, 10 % memory, 0.5 pp loss, 0.1 pp TCP). No `thresholds.json`, no spread, no `coverage.json`. Using the constants as a post-hoc band is not a re-analysis under D7. |
| Release pass selection | No. Three measured passes, no discarded warm-up; the release default is six measured passes after one discarded pass. |
| Per-pass clock log | No. There is a host-side `clock-probe.out` and a shift study (`e5b2-shift.out`), but no `clock.jsonl`; both ledger reads are uncorrected (35 connection mismatches, 1 datagram mismatch). |

### 3.3 Decision: stop, no release verdict

Named stop reasons:

1. `E5B2-NO-PREREGISTERED-BAND` - the calibration band and coverage file do not exist. A release
   verdict would need a band that was declared before the campaign.
2. `E5B2-MIXED-CALIBER` - the tree is four calibers in one; no single `caliber` describes it.
3. `E5B2-REFERENCE-ROW-NOT-ON-RELEASE-PLAN` - the new reference row ran the udp plan; the old
   reference (`wf-aot-opt`) is available, but it is not the release reference.
4. `E5B2-N=3<6` - three measured passes, no discarded pass; below the release floor. Under
   `minPasses: 6` every primary family would be `inconclusive` (underpowered), not `different` or
   `same`.
5. `E5B2-NO-CLOCK-LOG` - no per-pass clock correction; the old ledger shift cannot be corrected
   under the release rule.
6. `E5B2-PLAN-NOT-ARCHIVED` - the archived manifest and the repository path disagree; the old plan
   bytes are only in git history.

This is a re-analysis, not a new campaign. The old data remains a legacy reading and cannot carry a
release claim.

The stale-plan refusal is fail-closed and was observed: pointing `caliber.path` at the repository's
`plans-windows/full-shape-plan.json` while declaring `ed0a3477...` exits 2 and writes no output
directory:

```text
e2e-analysis: thresholds: 'caliber.sha256' is 'ed0a3477...', but
'benchmarks/WinForward.E2E/scripts/plans-windows/full-shape-plan.json' hashes to 'd1a20255...'
```

### 3.4 Diagnostic state check (not a release claim)

To exercise the new state machine on the old tree, a diagnostic thresholds file was built from the
old campaign's own declarations:

- caliber: the recovered `full-shape-plan.json`, SHA-256 `ed0a3477...`, seconds
  12/12/24/24/24/120/24/12/24/45;
- reference row: `wf-aot-opt` (the old campaign's reference configuration and a full-plan row);
- opponents: `proxifyre`, `proxifier`, `proxybridge`;
- primary bands: the old published constants (0.05, 0.10, 0.10, 0.5 pp), explicitly **not** a
  calibration band;
- capability: `rel.unexpectedEofRate <= 0.25 pp`, `nFrom: connectAttempts`;
- `minPasses: 3`, `discardPass: 0`, warmup 5 s, no clock log.

The run was:

```bash
cd /tmp/wf-b10/old-data
bash /home/paff/Projects/WinForward/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh \
  --raw /home/paff/bench-archive/2026-10-08-e5b2/raw \
  --ledger /home/paff/bench-archive/2026-10-08-e5b2/ledger-main.jsonl \
  --ledger /home/paff/bench-archive/2026-10-08-e5b2/ledger-direct.jsonl \
  --out release-diagnostic \
  --release --thresholds thresholds.old-diagnostic.json
```

Resulting states (diagnostic only; do not quote as release results):

| Metric | State | Detail |
|---|---|---|
| `lat.tcp_rtt.p50` | `different` | vs `proxifier` and `proxybridge`; `inconclusive` vs `proxifyre`; family 3 |
| `cpu.proxy.vcpuPct` | `different` | all three opponents; family 3 |
| `mem.privateBytes.p50` | `different` | all three opponents; family 3 |
| `loss.lossRate` | `unevaluable` | zero variance across passes; `proxifyre` and `proxybridge` in family, `proxifier` outside it (UDP not carried) |
| `rel.unexpectedEofRate` | capability `fail` | `wf-aot-opt`, `wf-fdd-opt`, `proxifyre`, `proxifier`, `proxybridge`: 345-357 events in 1443 attempts (23.9-24.7 pp) against 0.25 pp; `wf-aot-nativeudp`, `wf-aot-dnsrelay` and the controls `not-measured` |
| exploratory 16 | `report-only` | values and raw intervals only |

Other diagnostic facts: clock status `absent`; 12 invalidated cells in 16 gate invalidations (all
`proxybridge` LATLOAD/DNS/DNSALT in pass 1 and pass 3); findings by severity: 3 correctness-failure,
3 path-interference, 16 harness-error (the gate invalidations), 48 measurement-caveat, 9
informational. The old `same` results for loss are exactly the degenerate zero-width case D7
replaces: the new state is `unevaluable`, never `same`. The old REL difference claims become a
capability `fail`; the old latency/CPU/memory states for the old reference family do not change.

Why this is not a release claim: the bands are post hoc, the reference is the old one, n=3 is below
the release floor, the tree mixes calibers and there is no clock log. The diagnostic shows the state
machine, not new evidence.

If the new reference (`wf-aot-nativeudp`) and the release floor (`minPasses: 6`) were imposed instead,
the rule gives `inconclusive` for every primary metric (three measured passes) and `not-measured` for
the reference row's REL capability (opponents that ran REL still fail); that configuration is also
cross-caliber and is not run as a verdict.

### 3.5 Defect found in the release output

`ReleaseComparison.EvaluatePrimary` passes `equivalence.GetValueOrDefault(primary.Metric)` to the pair
decision (`benchmarks/WinForward.E2E.Analysis/Release/ReleaseComparison.cs:84`). The dictionary holds
`double` margins, so a metric with no `equivalence.families` entry gets `0.0`, not `null`. The
diagnostic verdict therefore publishes `"equivalenceMargin": 0` and an inconclusive reason that says
"outside the pre-registered ±0 TOST margin" even though no TOST family was declared. The state is not
changed in this data, but the document asserts a pre-registration that does not exist. This is a
B5 release-path defect, not a B10 code change: it needs a `TryGetValue`-style null and a test for an
absent family.

## 4. Limits and implications for the final report

- The old tree is legacy evidence. It can be cited only as "E5-b2, legacy caliber (mixed), 3 passes,
  no calibration band, no clock log", never as a release table or a release claim.
- The final report must leave the B8/B9 values as placeholders until they exist: release caliber and
  hash, calibration band and MDD, coverage, attempts/discarded pass, per-metric release verdicts, REL
  capability, clock residual, gate invalidations, and the limits section in the measurement model.
- Do not import old rows into a release table. The release caliber is `plans-release/full-plan.json`;
  the old five full rows ran the shorter `plans-windows/full-shape-plan.json`, and two old rows ran
  partial plans.
- No old conclusion is retracted: the old latency/CPU/memory comparisons keep their legacy meaning.
  The new rules change the loss `same` to `unevaluable`, turn REL into a capability gate, and demote
  every non-family pair to `report-only`.
- Before final acceptance, resolve the manifest/plan mismatch: either archive the old full-shape plan
  (and record its SHA-256 in `SHA256SUMS`/the manifest) or update the manifest to point at the
  recovered bytes with their provenance. The current manifest's `caliber.sha256` no longer hashes the
  path it names.
- The archive's `SHA256SUMS` verifies (exit 0), but it does not cover the plan; the manifest mismatch is
  therefore invisible to that checksum gate.
- The archive's B1 note says to regenerate `SHA256SUMS` after appending to the note or overwriting
  `analysis/`. B10 did not modify the archive; if the archive note is extended for B10, regenerate
  the sums then.

## Appendix A - diagnostic thresholds file

`/tmp/wf-b10/old-data/thresholds.old-diagnostic.json` (SHA-256
`dcdee2139a46012d4833148bf91bc1063a8ece90b4ca4b3edb59e0fbc2329079`) uses:

```json
{
  "version": 1,
  "caliber": {
    "path": "full-shape-plan.json",
    "sha256": "ed0a3477b74dfa973227c80e732592076220fdfa54e29aeb69094f805779d92e",
    "seconds": {"IDLE": 12, "LAT": 12, "LATLOAD": 24, "DNS": 24, "DNSALT": 24,
                "LOSS": 120, "REL": 24, "THRU": 12, "MIX": 24, "PERSIST": 45}
  },
  "referenceRow": "wf-aot-opt",
  "opponents": ["proxifyre", "proxifier", "proxybridge"],
  "primary": [
    {"metric": "lat.tcp_rtt.p50", "kind": "ratio", "band": 0.05},
    {"metric": "cpu.proxy.vcpuPct", "kind": "ratio", "band": 0.10},
    {"metric": "mem.privateBytes.p50", "kind": "ratio", "band": 0.10},
    {"metric": "loss.lossRate", "kind": "pp", "band": 0.5}
  ],
  "capability": {"rel.unexpectedEofRate": {"kind": "pp", "upperBound": 0.25, "nFrom": "connectAttempts"}},
  "equivalence": {"families": []},
  "minPasses": 3,
  "discardPass": 0,
  "warmupSeconds": 5,
  "clock": {"require": false, "maxDriftMs": 100}
}
```

This file is a diagnostic fixture, not a pre-registered release file and not for commit.

## Appendix B - reproduction commands

```bash
# Archived baseline (byte-identical at its own commit)
git archive 3f4d5ab | tar -x -C /tmp/wf-b10/baseline-src
bash /tmp/wf-b10/baseline-src/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh \
  --raw raw --ledger ledger-main.jsonl --ledger ledger-direct.jsonl \
  --out /tmp/wf-b10/baseline-repro

# B2 output (byte-identical to the HEAD legacy run)
git archive 8e01d51 | tar -x -C /tmp/wf-b10/b2-src
bash /tmp/wf-b10/b2-src/benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh \
  --raw raw --ledger ledger-main.jsonl --ledger ledger-direct.jsonl \
  --out /tmp/wf-b10/b2-repro
```
