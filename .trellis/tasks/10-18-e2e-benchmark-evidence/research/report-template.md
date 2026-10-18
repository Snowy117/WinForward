# WinForward E2E release report template

<!--
Fill this template only after B8 (calibration) and B9 (release campaign). Values in angle brackets
are placeholders; delete the angle brackets and the comment when the value exists. Never fill a
release field from E5-b2: that campaign is legacy, mixed-caliber and uncalibrated (see the evidence
task's `research/reanalysis-notes.md`).
-->

| Field | Value |
|---|---|
| Campaign id | `<YYYY-MM-DD-name>` |
| Report date | `<YYYY-MM-DD>` |
| Repository commit | `<40-hex commit>` |
| Deploy record | `<deploy.json commit / dirty / dirtySummary / campaignEnvHash>` |
| Operator and session | `<name; machine; session start/end>` |
| Report status | `<draft / accepted>` |

## 1. Release caliber

The release caliber is the triple *(plan file, content hash, declared arm durations)*. Tables in this
report never mix the release caliber with a screening or legacy caliber.

| Item | Value |
|---|---|
| Plan | `benchmarks/WinForward.E2E/scripts/plans-release/full-plan.json` |
| Plan SHA-256 | `<64-hex>` |
| Arm seconds | IDLE `<30>` · LAT `<30>` · LATLOAD `<30>` · DNS `<30>` · DNSALT `<30>` · LOSS `<120>` · REL `<60>` · THRU `<30>` · MIX `<60>` · PERSIST `<60>` |
| Thresholds file | `<path>` |
| Thresholds SHA-256 | `<64-hex>` |
| Coverage file | `<coverage.json path; or "absent">` |
| Calibration record | `<three reference runs; spread formula; session>` |
| Deployment | `<commit; dirty flag; product versions; config hashes>` |

## 2. Passes, attempts and selection

| Item | Value |
|---|---|
| Campaign attempt | `<attempt ordinal; prior voided attempts and why>` |
| Pass directories | `<total>` |
| Discarded pass | `<1>` |
| Measured passes | `<2..7>` |
| Measured n | `<6>` |
| Required n | `<6>` |
| Insufficient | `<false>` |
| Rerun history | `<none / instrument-gate failure + archive path>` |

A pass may be rerun only for a pre-registered instrument gate. The failed pass is archived and
disclosed; "it looks slow" is not a rerun reason.

## 3. Primary family and decision rule

| Metric | Kind | Band (B) | Business (B_business) | MDD | Source |
|---|---|---:|---:|---:|---|
| `lat.tcp_rtt.p50` | ratio | `<0.18>` | `<0.05>` | `<0.18>` | calibration |
| `cpu.proxy.vcpuPct` | ratio | `<0.18>` | `<0.10>` | `<0.18>` | calibration |
| `mem.privateBytes.p50` | ratio | `<0.05>` | `<0.10>` | `<0.05>` | calibration |
| `loss.lossRate` | pp | `<0.50>` | `<0.50>` | `<0.50>` | calibration |

Reference row: `<wf-aot-nativeudp>`. Opponents: `<proxifyre, proxifier, proxybridge>`. Family size
per metric: `<3; loss 2 because Proxifier does not carry UDP>`. Adjustment: Bonferroni-adjusted
intervals, family-wise 95 %, `m` = the metric's comparable reference-vs-opponent pairs. Coverage for
the measured n: `<value from coverage.json; or "not simulated">`.

Decision vocabulary: `different` (adjusted interval excludes the band), `inconclusive` (interval
straddles the band or n is below `minPasses`), `unevaluable` (degenerate/zero-variance or missing
denominator), `report-only` (exploratory), `equivalent` only in a pre-declared TOST family. A
zero-event rate is `unevaluable`, never `same`.

## 4. Primary results

| Metric | Reference median | Opponent | Passes used | Estimate | Raw CI | Adjusted CI | State | Reason |
|---|---|---|---:|---|---|---|---|---|
| `<metric>` | `<median>` | `<row>` | `<6>` | `<ratio / pp>` | `<[low, high]>` | `<[low, high]>` | `<state>` | `<short reason>` |
|  |  |  |  |  |  |  |  |  |

Metric-level state and the decision pair (the pair whose interval is the published evidence) go in
the same table's continuation or in a per-metric note. Latency and CPU conclusions are phrased as
"not worse than X" or "difference at least Y", never as "faster by Z %".

## 5. REL capability gate

| Row | Events | Attempts | Measured rate | 3/n upper bound | Threshold | Status |
|---|---:|---:|---:|---:|---:|---|
| `<wf-aot-nativeudp>` | `<0>` | `<1200>` | `<0 pp>` | `<0.25 pp>` | `<0.25 pp>` | `<pass / fail / not-measured>` |
|  |  |  |  |  |  |  |

This is an upper-bound capability gate, not a reference-vs-opponent difference claim.

## 6. Clock, gates and findings

| Item | Value |
|---|---|
| Clock status | `<corrected / absent / partial>` |
| Pre/post offset and drift | `<per-pass medians; max drift; >100 ms?>` |
| Residual | `<max abs deviation from the pre/post line>` |
| Corrected ledger mismatches | `<connection / datagram / unattributable>` |
| Uncorrected ledger mismatches | `<negative control>` |
| Invalidated cells | `<count; gate / row / pass / arm>` |
| Findings by severity | `<correctness-failure / path-interference / harness-error / measurement-caveat / informational>` |

A failed gate invalidates the cell it names and removes the pass from that metric's family; it does
not silently rewrite the value.

## 7. Exploratory metrics and other rows

- Exploratory metrics: `<list or "all metrics outside the primary family and capability gate">`.
- Internal WinForward pairs (`wf-aot-opt` vs `wf-fdd-opt`, `wf-aot-nativeudp` vs `wf-aot-opt`):
  `<inFamily=false; report-only>`.
- `BASE-singbox` calibration control: `<record; not a product row>`.
- Values, raw intervals and n are reported; no difference or equivalence claim is made for these.

## 8. Limits

Write the measurement model's limits section verbatim where it applies:

- This is a solution comparison under one declared workload, not a product ranking; the seven rows
  are seven factor bundles.
- No absolute latency: no independent external reference; loopback plus one vSwitch hop is not a WAN.
- The interval describes short-term session noise over the measured passes, not machine-to-machine
  reproducibility.
- The release rules and this verdict are C#-only; the Python oracle covers the unchanged legacy
  `tables.md`/`verdict.json`, not the release documents.
- The clock correction uses one offset per pass; a drifted campaign is disclosed, not rescaled.
- QUIC/HTTP3, IPv6, kernel DPC/ISR, true blinding and connection-id-level attribution are out of
  scope.
- `<session-specific limits: driver versions, power plan, dynamic memory, NIC offload, targets, ...>`

## 9. Appendix: old E5-b2 data (legacy, not release evidence)

Do not merge this appendix into any release table. It records the re-analysis decision:

- E5-b2 is a 3-pass, mixed-caliber legacy campaign; it has no pre-registered calibration band and no
  `clock.jsonl`.
- Formal release re-analysis: stopped with `E5B2-NO-PREREGISTERED-BAND`,
  `E5B2-MIXED-CALIBER`, `E5B2-REFERENCE-ROW-NOT-ON-RELEASE-PLAN`, `E5B2-N=3<6`,
  `E5B2-NO-CLOCK-LOG` and `E5B2-PLAN-NOT-ARCHIVED`.
- A diagnostic state check on the old reference/old constants is recorded in
  `.trellis/tasks/10-18-e2e-benchmark-evidence/research/reanalysis-notes.md`; its `different` cells
  are not release claims.
- The old latency/CPU/memory conclusions keep their legacy meaning. The old loss `same` is the
  degenerate zero-variance case (now `unevaluable`), and REL is a capability gate (old data: fail),
  not a difference claim.

## 10. Provenance checklist

- [ ] `thresholds.json` and optional `coverage.json` archived beside the campaign.
- [ ] `release-inputs.json`, `verdict.release.json`, `tables.release.md`, `findings.release.json`
      present; legacy files unchanged.
- [ ] `sha256sum -c SHA256SUMS` passes in the archive root.
- [ ] Manifest matches the archive: campaign id, caliber path/hash/seconds, file hashes, archive path.
- [ ] Raw data is not committed; only the report/manifest are checked in.
- [ ] The report prints the release caliber, primary family, band/MDD, coverage, attempts and limits.
