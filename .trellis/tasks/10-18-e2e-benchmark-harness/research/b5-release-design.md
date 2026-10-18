# B5 release path (oracle strategy B)

Status: accepted 2026-10-18. Owner chose **B**: the legacy analyzer output stays byte-identical and
the new release semantics are C#-only. This document resolves the B5 plan-review blockers. It is
the contract for B5a–B5e.

## Why B

The Python reference is retired. `FROZEN.md §3` allows a re-freeze only for a generator change, a
reference restore, or a contract change — not for a verdict-semantics change. `oracle-diff.py`
pins `thresholds`, `bootstrap`, all 21 `metrics/*`, and the key surface. A hand-edited golden
would make the oracle C#-vs-itself. Option B keeps the legacy path and its oracle intact, and gives
the release rules their own contract, C# tests and negative controls. The report must say the
release rules are not covered by the Python oracle.

## Invocation and outputs

Legacy (unchanged, oracle-covered):

```bash
bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw raw --out analysis
# -> tables.md, verdict.json, plots/SKIPPED.md (byte-identical to today)
```

Release (new, C#-only):

```bash
bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh \
  --raw raw --out analysis \
  --release --thresholds thresholds.json --min-passes 6 --discard-pass 1
# -> release-inputs.json, tables.release.md, verdict.release.json, findings.release.json
#    (the legacy files may also be written for diagnostics; they are unchanged)
```

- `--release` without `--thresholds` is a usage error (exit 2); `--thresholds` without `--release`
  is a usage error. `--min-passes` / `--discard-pass` are release-only.
- The legacy output is never changed by the release flags: the oracle remains green.
- The release output is additive and has its own schema; it is not compared by `oracle-diff`.

## Thresholds file

```json
{
  "version": 1,
  "caliber": {
    "path": "benchmarks/WinForward.E2E/scripts/plans-release/full-plan.json",
    "sha256": "<64 hex>",
    "seconds": {"IDLE": 30, "LAT": 30, "LATLOAD": 30, "DNS": 30, "DNSALT": 30,
                "LOSS": 120, "REL": 60, "THRU": 30, "MIX": 60, "PERSIST": 60}
  },
  "referenceRow": "wf-aot-nativeudp",
  "opponents": ["proxifyre", "proxifier", "proxybridge"],
  "primary": [
    {"metric": "lat.tcp_rtt.p50",     "kind": "ratio", "band": 0.18, "business": 0.05, "mdd": 0.18},
    {"metric": "cpu.proxy.vcpuPct",   "kind": "ratio", "band": 0.18, "business": 0.10, "mdd": 0.18},
    {"metric": "mem.privateBytes.p50","kind": "ratio", "band": 0.05, "business": 0.10, "mdd": 0.05},
    {"metric": "loss.lossRate",       "kind": "pp",    "band": 0.50, "business": 0.50, "mdd": 0.50}
  ],
  "exploratory": ["lat.tcp_rtt.p99", "lat.udp_rtt.p50", "..."],
  "capability": {
    "rel.unexpectedEofRate": {"kind": "pp", "upperBound": 0.25, "nFrom": "connectAttempts"}
  },
  "equivalence": {"families": []},
  "minPasses": 6,
  "discardPass": 1,
  "warmupSeconds": 5,
  "clock": {"require": false, "maxDriftMs": 100}
}
```

Validation is fail-closed (exit 2, message names the field): unknown key, missing/duplicate/unknown
primary metric, `band <= 0`, `equivalence` family not in `primary`, reference/opponent not a
declared row, caliber hash mismatch or missing plan, `minPasses < 2`, `discardPass` not a pass in
the tree, `warmupSeconds` conflict with the CLI. The file's sha256 is recorded in the release
verdict and printed in the report.

Units: `ratio` bands are fractions (±0.18 = ±18 %); `pp` bands are percentage points. `business`
and `mdd` are report metadata; `band` is the decision band. `exploratory` defaults to every metric
not in `primary`/`capability` when omitted.

## Release verdict

`verdict.release.json` (schema `release-verdict/1`) contains:

- `caliber`: plan path/sha256/seconds, thresholds path/sha256, `release: true`.
- `passes`: `{total, discarded: [1], measured: [2..7], n: 6}`. `--discard-pass` removes the
  warm-up pass before any metric is computed; the legacy output still includes it.
- `referenceRow`, `opponents`, `primary` (metric/kind/band/mdd).
- Per primary metric: per-pass values, the metric-level median/raw CI/adjusted CI, `decisionPair`
  (the pair those intervals come from), `verdict`, reason, `familySize`, and per-opponent pairs
  with `inFamily` and their own intervals. The metric-level state is the most severe in-family pair
  state (`different` > `inconclusive` > `unevaluable` > `equivalent`); the decision pair is the
  first same-state in-family pair with a non-null adjusted interval; ties by row id ordinal, and if
  no same-state pair has an interval, the first same-state pair by row id. Internal WinForward pairs are exploratory and not in the adjusted family.
- `capability`: REL measured rate, n, 3/n upper bound, threshold (0.25 pp), `status` pass/fail/
  not-measured. No difference claim.
- `exploratory`: values and raw CI only; no decision.
- `clock`: status, pre/post offsets, drift, residual, corrected/uncorrected ledger mismatch counts,
  per-pass detail.
- `gates`: the invalidation matrix result (which gate invalidated which metric/pass/arm).
- `findings`: the existing findings plus the release-only clock/gate findings.
- `limits`: the claim-boundary text (solution comparison, no product ranking, no absolute latency,
  no WAN, C#-only release rules, clock correction assumptions).

`tables.release.md` is a compact report: caliber/thresholds hash, primary table with band/MDD/
CI/adjusted CI/verdict, REL capability, clock, gate invalidations, exploratory note, limits.

## Decision semantics

- **`different`**: a **Bonferroni-adjusted percentile interval** (family-wise 95 %) excludes the
  band. With `m` comparable reference-vs-opponent pairs for the metric, the interval level is
  `1 - 0.05/m`. This is a conservative Holm-equivalent decision; no p-value is defined or needed.
  The report says "Bonferroni-adjusted, family-wise 95 %" rather than "Holm".
- **`inconclusive`**: the adjusted interval straddles the band, or fewer than `minPasses` measured
  passes contribute.
- **`unevaluable`**: the interval is degenerate/zero-width, a denominator is missing, or every
  pass is null. A zero-variance zero-event rate is the expected clean state; it is `unevaluable`
  and the upper bound is reported, never `same`.
- **`equivalent`**: only inside a pre-registered TOST family in `equivalence.families`; TOST with
  the declared margin, unadjusted, declared. Empty by default, so `same`/`equivalent` is
  unreachable unless the owner pre-registers it.
- **`report-only`**: exploratory metrics and every metric not in `primary`. The release verdict does
  not make a `different`/`same` claim for them.
- REL is a capability gate, not a decision. `rel.fidelityRate` is exploratory.
- Coverage: the calibration writes the n=6 simulated coverage (percentile and Bonferroni-adjusted)
  to `coverage.json` beside the thresholds; the release verdict reads it if present and prints it.
  It is not a thresholds key. If absent, `coverage: null` and the report says so.

## Family and reference row

- The reference row is data (`referenceRow`), not a hardcoded id. `RowProfiles` exposes a
  `ReferenceRow` and the release code reads the thresholds value; a test asserts they agree.
- Per metric, the family is `reference vs each opponent that ran the metric`. Proxifier is excluded
  from `loss.lossRate` (and other UDP metrics) because it does not carry UDP; family size 2, not 3.
- Internal pairs (`wf-fdd-opt` vs `wf-aot-opt`, `wf-aot-nativeudp` vs `wf-aot-opt`) are exploratory:
  `inFamily=false`.
- The family size and the excluded pairs are printed.

## Gate invalidation matrix

Only gates that directly invalidate the measured quantity; a failed gate is `unevaluable` with a
reason, excluded from that metric's family. The matrix:

| Gate | Invalidates | Scope |
|---|---|---|
| `windowOverflow > 0` | latency cells | arm, pass |
| `clientSendLoss > 0` | latency and loss cells | arm, pass |
| UDP accounting identity mismatch (any prefix) | loss and throughput cells | arm, pass |
| `backlogDrops > 0` | arm's latency cells | arm, pass |
| `scheduleTruncated > 0` | arm's latency cells + row's throughput cell | arm, pass |
| `laneShortfall > 0` | every metric cell the arm serves | arm, pass |
| `samplerError > 0` or `readError`-rejected samples | row's CPU and memory cells | row, pass |
| `dns-partition` mismatch | DNS arm's latency and loss cells | arm, pass |
| `foreignConnection > 0`, `directLeak > 0` | no cell invalidation; campaign-level correctness finding | campaign |
| control block drift | control comparisons/caveat only | control |
| BASE floor FAIL | path-quality caveat only; **not** a global invalidation | campaign |

The frozen tree's `control-post` BASE loss 0.008 is exactly why the BASE floor must not invalidate
every row. `TableLatency`'s existing `windowOverflow` behavior stays for the legacy table; the
release matrix is a separate value object (`GateMatrix`) and does not change the legacy path.

A gate-invalidated pass is excluded from that metric's CI family; `InvalidatedPasses` counts only
values actually removed, so a gate targeting a pass with no value does not shrink `m`. A fully
invalidated pair leaves the family; a partial invalidation keeps it and draws over survivors.
REL/PERSIST `clientSendLoss` maps to no release cell because those arms have no latency/loss metric;
the legacy gate still reports it.

## Clock correction

- Parse `clock.jsonl` from the campaign root: `{pass:int, phase:"pre"|"post", offsetMs, rttMs,
  vmUtc, hostUtc}`. `pass` is the integer pass number; `passN` directories map directly.
- For each measured pass, use the pre and post medians. The window translation uses one offset per
  pass, the value of the linear pre/post line at the pass midpoint: `host = vm - offset`.
  `OffsetAt` still exposes the linear interpolation at any instant for diagnostics. The 100 ms
  drift gate bounds the per-arm-vs-pass difference to ≤50 ms, inside the ledger's 1 s summary
  band; the synthetic tree's records on arm-window edges make per-arm interpolation flip arbitrary
  ties, so pass-level is the stable rule. (Sign: `offsetMs = vm - host`; a VM that is behind has a
  negative offset and its window moves forward.)
- `residualMs`: the maximum absolute deviation of the 10 probe samples from the line through the
  pre/post medians. `driftMs = |post - pre|`.
- Run the ledger attribution twice: corrected and uncorrected. Report both mismatch counts. The
  corrected count is the release evidence; the uncorrected count is the negative control.
- Missing `clock.jsonl`: `clock.status = "absent"`, no shift, ledger attribution marked
  `uncorrected`. If `clock.require` is true, the release invocation exits 2. A pass with no post
  sample is `partial`: use the pre offset, mark the pass, do not silently interpolate.
- The clean synthetic tree has no `clock.jsonl`; do not add one to the frozen tree. Tests use a
  copied clean tree (see below).

## Test seam and negative controls

No `InternalsVisibleTo` (measurement-tooling.md). Add a **public** `WinForward.E2E.Analysis.Release`
namespace with pure types: `ThresholdsFile`, `ReleaseComparison`, `GateMatrix`, `ClockCorrection`,
`ReleaseOptions`. The CLI calls them; tests reference the analysis project and call the public API.
The existing internal `BootstrapPair`/`DescriptiveStats` are wrapped; the release writer stays
internal.

Required negative controls (each must fail when the violation is injected):

- **Band**: shift a synthetic ratio so the adjusted CI is just inside/outside the band; assert the
  verdict flips. The golden has 0 `different`, so this cannot come from `oracle-diff`.
- **`unevaluable`**: zero-variance primary and zero-event REL; assert `unevaluable`, not `same`.
- **Gate matrix**: inject `windowOverflow`, `clientSendLoss`, a UDP prefix mismatch; assert exactly
  the intended cells invalidate; assert control drift and BASE floor do not invalidate every row.
- **Clock**: copy the clean tree; shift every `run.json` `startedUtc` by +2000 ms; add
  `clock.jsonl` pre +1800 / post +2200 ms; corrected per-arm ledger counts equal the unshifted
  baseline, uncorrected differ. Negative controls: omit `clock.jsonl` (must differ), invert the
  sign (must differ). 2 s is above the 1 s ledger summary granularity; no timestamp ties.
- **REL**: 0 events at n=1200 → upper bound 0.25 pp, pass; 1 event → fail; 0/0 → not-measured.
- **Family**: Proxifier excluded from UDP loss; family size 2; internal pair `inFamily=false`.
- **CLI**: `--release` without thresholds, unknown thresholds key, band ≤ 0, caliber mismatch,
  `--thresholds` without `--release` → exit 2 with the field named.

## Slices

- **B5a-lite**: thresholds schema/parser, `--release`/`--thresholds`/`--min-passes`/`--discard-pass`
  parsing (release-only), reference row/opponents, caliber validation, public seam skeleton; no
  default output change. Tests for the parser/validation.
- **B5d-lite**: `clock.jsonl` parser, `-offset` window shift, interpolation/residual, no-op when
  absent; public `ClockCorrection`; copied-tree test. Does not touch the frozen tree.
- **B5b**: release comparison/state machine, Bonferroni-adjusted intervals, TOST family, REL
  capability, exploratory demotion, family/opponent logic; tests and negative controls.
- **B5c**: gate matrix, release verdict/tables writer, findings/limits; tests and negative controls.
- **B5e**: campaign invocation (`--release --thresholds ... --min-passes 6 --discard-pass 1`),
  `publish-campaign.sh`/B7 staging, README/report contract, `measurement-tooling.md` updates. The
  campaign must `cd` to the repo or use an absolute `caliber.path`, and analyze the full 7-pass
  tree with `--discard-pass 1`; do not stage only passes 2–7 (it loses the discarded-pass
  provenance).

Order: B5a-lite → B5d-lite → B5b → B5c → B5e. B4 must land before B5a-lite because B4 re-hashes
`caliber.json` (the analysis reads the hash at run time; no bake-in). B3b must land before B5d-lite
because the clock writer is B3a's file and B5d reads its output.
