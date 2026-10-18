# E2E benchmark measurement model

This is the one-page measurement model the review asked for. It states what is estimated, what
decision each estimate supports, and what the device cannot support.

## Questions

1. **Capability / correctness.** Does a solution carry the declared UDP workload, and does it leak
   or mix flows? Answered by counts and identity checks: `directLeak`, `foreignConnection`,
   UDP accounting identity, declared arms present. No confidence interval is needed.
2. **Cost comparison under a declared workload.** For each primary metric, what is the ratio (or
   percentage-point difference) between the reference solution and each opponent, under the release
   caliber? Answered only when the interval excludes the pre-registered band.
3. **Regression.** For the same row and same release caliber, did a later build move a primary
   metric beyond the calibration band? This is the most defensible use of the device.

## Estimand and analysis unit

- Per row, per metric, per pass: the metric's own within-pass summary (median or quantile as the
  metric defines it).
- The analysis unit is the **pass**. The headline is the median across measured passes; the interval
  is a percentile bootstrap over passes, 10,000 resamples, seeded.
- A campaign has 7 passes: 1 discarded warm-up + 6 measured. Fewer than 6 measured passes means the
  interval degenerates; the verdict says `inconclusive` or `unevaluable`, not `same`.
- Passes share one session; the interval describes short-term session noise, not machine-to-machine
  reproducibility. The report says so.

## Primary family and decision rule

| Family | Metrics | Decision |
| --- | --- | --- |
| Primary (inferential) | `lat.tcp_rtt.p50`, `cpu.proxy.vcpuPct`, `mem.privateBytes.p50`, `loss.lossRate` | `different` / `inconclusive` / `unevaluable`; `equivalent` only via a separately declared TOST family. |
| Capability gate | `rel.unexpectedEofRate` | Report the measured rate and the 3/n upper bound; compare the bound with 0.25 pp. No difference claim. |
| Exploratory | the remaining headline metrics | Report values, CI and n; no decision. |

- Band: `B = max(B_business, 1.5 × calibration spread)` per metric, pre-registered before the
  release campaign. `B_business` is the smallest effect worth acting on for that metric.
- `different`: the bootstrap interval excludes the band; the test's null is "the ratio is inside the
  band". Holm is applied within each metric's reference-vs-opponent family (three opponents).
- `same`/`equivalent`: only inside a pre-declared TOST equivalence family, unadjusted and declared
  as such. A zero-variance or zero-event interval is `unevaluable`, never `same`.
- Coverage: the n=6 simulated coverage of the percentile interval is printed next to `n`; it is not
  assumed to be 95 %.
- Latency and CPU conclusions are phrased as "not worse than X" or "difference at least Y"; they are
  never phrased as "faster by 5 %".

## Calibration

- 3 back-to-back release runs on the reference row (`wf-aot-nativeudp`), same binary, same session,
  ~30–40 min.
- Compute per-metric `spread = (max − min) / min` across the 3 runs; set `B`.
- If `B` is so wide that no effect worth acting on can be resolved, demote the metric to report-only
  rather than widening the band until it "works".
- `BASE-singbox` runs once in the same calibration session to quantify the sing-box hop.

## Release campaign

- Release plan: `plans-release/full-plan.json`, rate-preserving, declared arm durations.
- 7 passes: 1 discarded warm-up + 6 measured. Every attempt archived; `attempt` recorded.
- Per-run warm-up 30 s; per-arm trim 5 s. The deviation from the design's per-arm 30/60 s is in the
  report's limits section.
- Rerun only for a pre-registered instrument gate; the failed pass is archived and disclosed.

## What the device cannot support

- A product ranking. The rows are solution bundles. Two single-factor comparisons exist—build
  mode (`wf-aot-opt` vs `wf-fdd-opt`) and UDP carriage (`wf-aot-nativeudp` vs `wf-aot-opt`)—but only
  the build-mode pair is a product-level claim, and even it is exploratory.
- Absolute latency. There is no independent external reference and the sing-box hop is common to the
  measured rows.
- A default-transport claim for a row that does not run the default full plan.
- Any extrapolation to a real WAN. The path is loopback plus one vSwitch hop.
- QUIC/HTTP3, IPv6, kernel DPC/ISR and true blinding: out of scope, declared in the limits section.

## Report contract

Every report prints: release caliber (plan file, hash, arm durations), primary family, per-metric
band/MDD, measured n and simulated coverage, attempt/pass provenance, the capability-gate upper
bounds, and the limits section above. Tables never mix release and screening calibers.
