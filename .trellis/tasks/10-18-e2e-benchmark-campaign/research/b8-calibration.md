# B8 calibration record and B9 readiness

Status: calibration analysis complete on the valid three-pass tree. **No release verdict is claimed here.**
The B9 seven-pass campaign has not run. The main session owns the commit, the VM, the archive and the
release night; this record is the pre-registration evidence the B9 analysis must consume.

Analysis date: 2026-10-19 (host, Asia/Shanghai). Campaign session: 2026-10-18 19:19:56 to 20:03:45
(VM local, UTC-07:00; 2026-10-19 02:19:56 to 03:03:45 UTC).

## 1. What was measured, and from which bytes

| Item | Value |
| --- | --- |
| Valid calibration tree | `/tmp/wf-bench/b8-analysis/calibration2` (also `/tmp/wf-bench/b8-calibration2.zip`) |
| Passes / row | 3 passes (`pass1`..`pass3`), one product row `wf-aot-nativeudp`; `control-pre` and `control-post` BASE blocks in each pass |
| Plan | `benchmarks/WinForward.E2E/scripts/plans-release/full-plan.json` |
| Plan SHA-256 | `da148a1d99580475cdc75ea0b93130b55819aab1206074f9a21fd9ddd0a925d3` (verified by the analyzer) |
| `run.json.planHash` | `da148a1d99580475` (16-character prefix recorded by the client) |
| Declared arm seconds | IDLE 30, LAT 30, LATLOAD 30, DNS 30, DNSALT 30, LOSS 120, REL 60, THRU 30, MIX 60, PERSIST 60 (total 480 s) |
| Reference pass wall time | 482.601 s / 482.595 s / 482.570 s (passes 1/2/3) |
| `failures.json` | `[]` (empty; no failed row, arm or gate) |
| Deploy record | `/tmp/wf-bench/deploy.json`: commit `bbfe12c77027812cd7d4b25b8e07724500bbd16f`, branch `HEAD`, `dirty: false`, zero dirty entries, plan set `release`, SHA-256 `f1e8970503bcdfc88471feb7116b33e937e22a42f814f12aa4262307f42267ea` |
| Product source / clock probe | HEAD `458d5685a84bad8e9ad75bf523d26ce7d6f2000e` (`fix(e2e): 时钟探针容忍裸连接`); the campaign ran with this host probe |
| Validation attempt | `/tmp/wf-bench/b8-analysis/failed` (old AOT product; see §7) |
| Control tree | `/tmp/wf-bench/b8-analysis/control` (BASE-singbox; see §6) |

The deploy record and the product source are deliberately recorded as two facts. `deploy.json` names
`bbfe12c` and is clean; the only source change between `bbfe12c` and `458d568` is the host clock probe,
and the product was rebuilt from `458d568`. The plan and caliber bytes did not move between those two
commits. No single-commit provenance is claimed.

### 1.1 Analyzer run used for the per-pass values

The release-mode analyzer was run from the repository root at `458d568`:

```bash
bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh \
  --raw /tmp/wf-bench/b8-analysis/calibration2 \
  --out /tmp/wf-bench/b8-analysis/analysis-option-b2 \
  --release --thresholds benchmarks/WinForward.E2E/scripts/plans-release/thresholds.json \
  --min-passes 3 --discard-pass 0
```

Exit 0. Outputs: `release-inputs.json`, `verdict.release.json`, `tables.release.md`,
`findings.release.json`, and the legacy documents. The per-pass values are the analyzer's
`referenceValues` maps in `verdict.release.json`; they are read from
`calibration2/pass<N>/wf-aot-nativeudp/{LAT,LOSS,IDLE,...}.jsonl` and the 1 Hz sampler samples. The
`--min-passes 3 --discard-pass 0` override is a calibration diagnostic (all three passes measured);
it is not the release invocation. With no opponent rows in the tree, every comparison is
`unevaluable` for lack of a comparable declared opponent — that diagnostic state is not a release
verdict.

Final file hashes:

| File | SHA-256 |
| --- | --- |
| `benchmarks/WinForward.E2E/scripts/plans-release/thresholds.json` | `8ae8e31f576fd3791b9aba2267953063307819cd84a388c791c579029b51af3c` |
| `benchmarks/WinForward.E2E/scripts/plans-release/coverage.json` | `366e4523bf9a20aa183b2142678dc72351ce5b6f0da0cbe2ec95e60d68d5b84e` |
| `calibration2/clock.jsonl` | `e14d14bbf683282fdb57b5b8cc980791d59fb2f3fc5c1d7f403eb44b20468ebf` |
| `calibration2/environment.json` | `fc76ab2becd64052676e3e7a6d930d3d54334d55f8a56e0640cbb865f33af24a` |
| `calibration2/failures.json` | `4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945` |

## 2. Per-pass primary values

The release primary family under consideration is `lat.tcp_rtt.p50`, `cpu.proxy.vcpuPct`,
`mem.privateBytes.p50`, `loss.lossRate`. All four were extracted from the valid tree; the demotion
decision in §3 moves two of them out of the decision family.

| Metric | pass 1 | pass 2 | pass 3 | Median | Kind / unit |
| --- | ---: | ---: | ---: | ---: | --- |
| `lat.tcp_rtt.p50` | 3713.023 | 3205.119 | 3476.479 | 3476.479 | ratio; µs (LAT tcp-rtt p50) |
| `cpu.proxy.vcpuPct` | 26.12242049603247 | 24.08584640721144 | 24.26910582369307 | 24.26910582369307 | ratio; % of one vCPU |
| `mem.privateBytes.p50` | 37.62890625 | 39.60546875 | 37.435546875 | 37.62890625 | ratio; MiB (steady-state private bytes) |
| `loss.lossRate` | 0 | 0 | 0 | 0 | pp; LOSS loss rate |

## 3. Spread, band and MDD

Formula, pre-registered before the campaign:

```
spread = (max(pass values) - min(pass values)) / min(pass values)
B      = max(B_business, 1.5 * spread)
```

`B_business` is the smallest effect worth acting on, taken from the repository's existing declared
practical thresholds — `benchmarks/WinForward.E2E.Analysis/Metrics/MetricSpec.cs:60` (latency 5 %),
`:63` (CPU 10 %), `:66` (memory 10 %), `:69` (UDP loss 0.5 pp). The design review found no derivation
record for these constants (`design-stats-and-decisions.md` finding 3); they are used here only as the
pre-existing business floor, not as the decision band. Units: the first three are ratio fractions
(0.05 = 5 %), the loss value is percentage points (0.5 = 0.5 pp).

| Metric | Spread | 1.5 × spread | `B_business` (source / unit) | B | MDD (= B) | Role after calibration |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `lat.tcp_rtt.p50` | 0.15846650311579694 | 0.23769975467369542 | 0.05 (`MetricSpec.cs:60`; ratio) | **0.23769975467369542** | 0.23769975467369542 | exploratory / report-only (demoted) |
| `cpu.proxy.vcpuPct` | 0.08455480676864505 | 0.12683221015296758 | 0.10 (`MetricSpec.cs:63`; ratio) | **0.12683221015296758** | 0.12683221015296758 | exploratory / report-only (demoted) |
| `mem.privateBytes.p50` | 0.057964209318098814 | 0.08694631397714822 | 0.10 (`MetricSpec.cs:66`; ratio) | **0.10** (business-limited) | 0.10 | primary |
| `loss.lossRate` | undefined (0/0; min = max = 0) | undefined | 0.50 (`MetricSpec.cs:69`; pp) | **0.50** | 0.50 | primary, degenerate → `unevaluable` |

MDD is reported as the calibrated decision band B: it is the smallest effect the pre-registered rule
can distinguish from the band edge in the report's vocabulary. It is not a power calculation, and it
is not the business threshold when the business threshold is the smaller of the two.

### 3.1 Demotion decision (pre-registered rule)

The rule: if `B` exceeds the smallest effect worth claiming, the metric becomes report-only rather than
widening the band until it "works".

- `lat.tcp_rtt.p50`: B = 0.2377 > B_business = 0.05. A 5 % latency effect cannot be resolved; the
  metric is demoted to exploratory/report-only.
- `cpu.proxy.vcpuPct`: B = 0.1268 > B_business = 0.10. A 10 % CPU effect cannot be resolved; the
  metric is demoted to exploratory/report-only.
- `mem.privateBytes.p50`: B = 0.10 = B_business (the business floor dominates 1.5 × spread = 0.0869).
  The business effect is resolvable at the calibrated noise; retained as primary.
- `loss.lossRate`: B = 0.50 pp = B_business; the spread is undefined because all three values are zero.
  Retained as primary, but the release state is `unevaluable` (zero-width/zero-event interval), never
  `same`. The REL capability gate is the only place a `3/n` upper bound is reported; loss itself is a
  rate with no zero-event bound in the release path.

The release schema expresses report-only only through the non-primary set. The pre-registered file
therefore has `primary = [mem.privateBytes.p50, loss.lossRate]`, with `lat.tcp_rtt.p50` and
`cpu.proxy.vcpuPct` in `exploratory`; the analyzer publishes them with `role: exploratory`,
`state: report-only`, values and raw CI, and no adjusted interval or decision. The B9 report writer
must use that output and must not re-label a primary verdict in prose.

This is the pre-registered four-metric family with two demotions, not a silent change of question. The
B9 report must print all four values, state the demotion, and say that only memory can carry an
inferential claim in this release; loss is `unevaluable`; latency and CPU are report-only.

## 4. Coverage simulation (n = 6)

`coverage.json` beside the thresholds file is `calibration-coverage/1`. The release verdict embeds the
whole document under `coverage`; `tables.release.md` prints it in the Coverage section. It is not a
thresholds key.

Method:

- The release interval is a percentile bootstrap over passes: 10,000 resamples, paired log ratios for
a ratio metric, difference for a pp metric, type-7 linear-interpolation quantiles at `alpha/2` and
`1 - alpha/2`.
- For n = 6 there are only `6**6 = 46,656` possible resamples, so the simulation enumerates the exact
bootstrap distribution of the median instead of drawing 10,000 of them. The reported n = 6 coverage is
the population coverage of the method, not a Monte Carlo approximation of the bootstrap itself.
- The outer simulation uses 100,000 replicates, `random.Random` (Mersenne Twister), seed `20261006`.
  The sigma of the log-normal pass noise is the sample standard deviation of the three log calibration
  values (lat 0.073684, CPU 0.044836, memory 0.031151). The reference-vs-opponent pair is simulated as
  independent equal-sigma rows, so the log-ratio sigma is `sqrt(2) * sigma`; the calibration has no
  opponent row, so this is an assumption.
- Validation: the same exact-`n**n` method at n = 3 and n = 4 gives 0.751 and 0.8703, inside the
  design review's measured bands (71.3–75.7 % and 86.0–89.3 %). That is the evidence that the
  simulation is implementing the analyzer's percentile interval, not a different calculation.

| Metric | n | Family m | Raw 95 % interval coverage | Adjusted interval level / coverage | Use |
| --- | ---: | ---: | ---: | --- | --- |
| `mem.privateBytes.p50` | 6 | 3 | 0.9378 | 98.33 % / 0.9696 | primary decision |
| `loss.lossRate` | 6 | 2 | not simulated | 97.50 % / not simulated | primary, but degenerate |
| `lat.tcp_rtt.p50` | 6 | — | 0.9371 (context only) | — | exploratory; not used |
| `cpu.proxy.vcpuPct` | 6 | — | 0.9378 (context only) | — | exploratory; not used |

The n = 6 percentile interval is not a 95 % interval in practice: its simulated coverage is 93.78 %
for memory. The adjusted 98.33 % interval covers 96.96 %. The loss interval is zero-width at the
calibration values and is deliberately not simulated; its release state is `unevaluable`, never
`same`.

Limits: the simulation models pass-level log-normal noise; it is not a resampling of the three
calibration values; the ratio noise is an assumption because the calibration tree has no opponent row;
and the coverage is method-level, not a prediction for a particular B9 opponent.

## 5. Clock evidence

The calibration used the measure-and-correct policy. `clock.jsonl` has 30 samples (10 per pass, 5 pre
and 5 post), 0 bad lines. The release analyzer reports `clock.status = complete`, `applied = true`.

| Pass | Pre offset (ms) | Post offset (ms) | Drift (ms) | Residual (ms) | Applied offset (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 1104.630 | 1119.539 | 14.909 | 1.039 | 1112.0845 |
| 2 | 1119.702 | 1133.164 | 13.462 | 0.066 | 1126.4330 |
| 3 | 1133.302 | 1146.737 | 13.435 | 0.072 | 1140.0195 |

Offset sign is `vm - host`; the VM was about +1.10 to +1.15 s ahead of the host and drifting ~13–15 ms
per pass. The 100 ms drift gate passed, and the residual is at most 1.04 ms. The calibration tree has
no target ledger, so the corrected/uncorrected ledger counts are unavailable (`available: false`);
this is a diagnostic gap, not a zero. The B9 run must include the target ledgers so the corrected read
can be checked against the uncorrected negative control.

## 6. BASE-singbox control

Tree: `/tmp/wf-bench/b8-analysis/control/pass1/BASE-singbox` (also `/tmp/wf-bench/b8-control.zip`).
The control is TCP-only through the sing-box SOCKS5 inbound, no product.

| Item | Value |
| --- | --- |
| `control.json` | row `BASE-singbox`, kind `calibration-control`, plan `base-singbox-plan.json`, SOCKS5 `127.0.0.1:1080`, `tcpOnly: true`, UDP `not run: SOCKS5 CONNECT carries TCP only` |
| `proxy-truth.json` | `tcp: 61`, `udp: 0`, `utcp: 0`, `total: 61`, `directLeak: 0` |
| LAT `tcp-rtt` | n = 1201, min 1162.3 µs, p50 **4126.719 µs**, p90 8982.527 µs, p99 10829.823 µs, max 124613.4 µs |
| LAT `tcp-connect` | n = 60, p50 5101.567 µs; `tcp.connectAttempts = 61` |
| Clock | median +1075.0 ms (VM − host), drift 1.3 ms; measure-and-correct warning, as in calibration |

The LAT hop against the direct BASE blocks in the calibration session:

| Direct BASE block | pass 1 | pass 2 | pass 3 |
| --- | ---: | ---: | ---: |
| `control-pre` tcp-rtt p50 (µs) | 2286.591 | 4347.903 | 4239.359 |
| `control-post` tcp-rtt p50 (µs) | 4020.223 | 3869.695 | 3750.911 |

Median of the six direct p50 values: **3944.959 µs**. Control p50: **4126.719 µs**. Point estimate of
the sing-box hop: **+181.76 µs (+4.61 %)**. The direct blocks themselves span 2286.591–4347.903 µs, so
the hop is inside the direct control's run-to-run variation and is not resolved by this single control
pass. The control quantifies the path; it does not support a precise hop claim. For context only, the
reference row's LAT p50 (3476.479 µs) is lower than the sing-box-only control; that difference includes
the product's own latency and is not a hop estimate.

Control block drift is a measurement caveat in this calibration: the analyzer's findings report
post/pre ratios 0.8848 and 0.8900 in passes 2 and 3 (CI straddles the ±0.05 band). It is a caveat, not
a cell invalidation (`gates.invalidatedCells = 0`).

## 7. Failed old-product attempt (discarded)

Tree: `/tmp/wf-bench/b8-analysis/failed` (also `/tmp/wf-bench/b8-calibration-failed.zip`).

- `failures.json` has three entries, one per pass: `stage: "product-start"`,
  `error: "wf-aot-nativeudp did not start"`, `cleanSlate: false`, no arm.
- Each `pass<N>/wf-aot-nativeudp/run.json` is a failed skeleton:
  `failed: true`, `stage: "product-start"`, `error: "wf-aot-nativeudp did not start"`, `arms: []`.
- The old AOT product was `WinForward.exe` size 6,633,472, SHA-256
  `29B0A34E1CD139BF138614521FC7D3214A8D492EEB054B75909643E5B72201A5`; the rebuilt product is size
  6,836,736, SHA-256 `DBEFFF756AFAA04634A0B6F2300B7940A03604AE80EB48C1C6D62DF73E472A4F` (from the
  valid tree's `environment.json`).
- The old AOT product could not parse the new configuration (campaign context; the failure record
  itself says only "did not start"). The orchestrator correctly recorded a product-start failure and
  wrote the skeleton rather than measuring an empty row.
- The attempt is kept as the failed-attempt record only. Its control-pre/post blocks are not
  calibration data, and no metric from this tree is used in this record or in B9. The valid attempt
  was rerun after rebuilding the product from HEAD `458d568`.

## 8. Sensitivity proof (B4, not rerun)

Reference: commit `a27b82d5289a856d2b2472774a73d6848c97a49d` (`feat(e2e): 加入正对照注入与 REL stall`),
whose message records the B4 run: sensitivity plan + 1000 ms injection, measured p99.9 **1072 ms TCP /
1069 ms UDP**, control **83 ms / 79 ms**. The plan is
`benchmarks/WinForward.E2E/scripts/plans/sensitivity-plan.json` (LAT 30 s at 20/s, LOSS 120 s at 500/s),
and the contract is pinned by `tests/WinForward.E2E.Tests/TargetDelayInjectionTests.cs` (1 s injection
must put p99.9 in 900–5000 ms; zero injection must stay below 500 ms). The target writes the effective
delay to `<ledger-base>.injection.json`, so a calibration run is auditable from its own sidecar. The B4
run's sidecar and ledger are not present in the B8 analysis trees; this record cites the commit and the
test and does not rerun the proof.

## 9. Gates and findings

- `gates.invalidatedCells = 0`; no gate invalidated a metric cell in the calibration tree.
- `findings.release.json`: 0 correctness-failure, 0 path-interference, 0 harness-error, 5
  measurement-caveat, 1 informational. The caveats are the control-block drift (two ratios), the
  missing target ledger (two lines), and the missing-ledger clock caveats; the informational line is
  the port-53 direct-path note. These are caveats, not invalidations.
- Clean slate: all three passes `passed: true`, `leftovers: []`; passes 2 and 3 stopped a stale
  sing-box process before the pass.
- Proxy truth for each product pass: `tcp=1430 udp=8 uot=0`; dual: `tcp=95 udp=4 directLeak=0`.
- Calibration had no target ledger (`pass<N>` has no `target-ledger.jsonl`); the B9 run must include
  the two ledgers and the corrected/uncorrected clock reads.

### 9.1 REL capability baseline (not a primary metric)

The calibration's `rel.unexpectedEofRate` capability gate fails on the reference row:

| Pass | connectAttempts | unexpectedEof | fidelityMismatch |
| --- | ---: | ---: | ---: |
| pass1 | 1201 | 229 | 240 |
| pass2 | 1201 | 233 | 240 |
| pass3 | 1201 | 235 | 240 |
| pooled | 3603 | 697 | 720 |

Pooled rate: **697 / 3603 = 19.345 pp** against the pre-registered 0.25 pp upper bound; because events > 0,
the `3/n` upper bound is not available and the release status is **fail**. `rel.fidelityRate` is 720/3603
= 19.98 pp (exploratory). This is a capability gate, not a difference claim, and it does not enter the
The attempt records carry the signature of a reset converted to FIN: every failing attempt is
`mode: resetAfterN`, `expected: reset`, `observed: unexpectedEof`, `eof: true`, `reset: false`, while
the `halfClose` attempts are clean. That is consistent with the known sing-box RST-to-FIN behavior, not
with a generic product failure. However, the calibration has no direct-path REL control, so the evidence
cannot separate sing-box from a product REL path; the attribution remains **unresolved: known sing-box
RST-to-FIN versus product REL path**. The release report reports `capability: fail` and makes no REL
difference claim. The B9 run should record whether the failure reproduces and preserve the REL per-pass
counts.

## 10. Raw archive paths

These are the extracted orchestrator outputs, exactly as downloaded:

| Tree | Path | Zip |
| --- | --- | --- |
| Valid 3-pass calibration | `/tmp/wf-bench/b8-analysis/calibration2` | `/tmp/wf-bench/b8-calibration2.zip` |
| Failed old-AOT attempt | `/tmp/wf-bench/b8-analysis/failed` | `/tmp/wf-bench/b8-calibration-failed.zip` |
| BASE-singbox control | `/tmp/wf-bench/b8-analysis/control` | `/tmp/wf-bench/b8-control.zip` |
| Deploy record | `/tmp/wf-bench/deploy.json` | (inside neither campaign tree) |
| Analysis outputs | `/tmp/wf-bench/b8-analysis/analysis-option-b2` (per-pass values, clock, coverage embedding); `/tmp/wf-bench/b8-analysis/analysis-calib` (first diagnostic run) | — |
| Coverage simulation | `/tmp/wf-bench/b8-analysis/coverage-sim.py`, `coverage-input.json`, `coverage-output.json` | — |

Proposed archive root (main session to create and verify; not created by this analysis):

```bash
mkdir -p ~/bench-archive/2026-10-18-calibration/raw
cp -a /tmp/wf-bench/b8-analysis/calibration2/. ~/bench-archive/2026-10-18-calibration/raw/
cp -a /tmp/wf-bench/b8-analysis/control ~/bench-archive/2026-10-18-calibration/control
cp -a /tmp/wf-bench/b8-analysis/analysis-option-b2 ~/bench-archive/2026-10-18-calibration/analysis
cp /tmp/wf-bench/b8-analysis/coverage-sim.py /tmp/wf-bench/b8-analysis/coverage-input.json \
   ~/bench-archive/2026-10-18-calibration/
cp -a /tmp/wf-bench/b8-analysis/failed ~/bench-archive/2026-10-18-calibration/failed-attempt
cp /tmp/wf-bench/deploy.json ~/bench-archive/2026-10-18-calibration/
cp benchmarks/WinForward.E2E/scripts/plans-release/thresholds.json \
   benchmarks/WinForward.E2E/scripts/plans-release/coverage.json \
   ~/bench-archive/2026-10-18-calibration/
(cd ~/bench-archive/2026-10-18-calibration && \
  find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > /tmp/calibration-SHA256SUMS && \
  mv /tmp/calibration-SHA256SUMS SHA256SUMS && \
  sha256sum -c SHA256SUMS)

```

Raw data stays out of git. The checked-in artifacts are `thresholds.json`, `coverage.json` and this
record; the raw trees are archive-only. The main session should generate `SHA256SUMS` over the archive
and keep the `sha256sum -c` output with the B9 report.

## 11. B9 deploy and analysis runbook

### 11.1 Preconditions to upload and verify

1. **Commit the pre-registration.** The main session commits this record plus
   `plans-release/thresholds.json` and `plans-release/coverage.json`. The deploy record must name that
   commit and be clean. The current worktree has unrelated modifications/untracked files
   (`.trellis/tasks/08-30-proxy-perf-stability/task.json`, `10-10-loh-footprint/`,
   `benchmarks/results/2026-10-18-cpu-usage-analysis/`); `deploy-campaign.sh` will refuse a dirty tree
   unless they are committed, stashed or explicitly deployed with `--allow-dirty` and disclosed.
2. **Product build provenance.** Rebuild/publish the product from the same commit the deploy record will
   name, or record the split explicitly. The calibration used the `458d568` AOT rebuild (SHA-256
   `DBEFFF...`); the deploy record was `bbfe12c` clean. The B9 `environment.json` must carry the
   product SHA-256, `fileVersion` and `ndisapi.dll` hash. The clock-probe fix `458d568` must be the
   probe the campaign runs.
3. **Archive and thresholds.** Copy the calibration raw tree, `thresholds.json` and `coverage.json` to
   `~/bench-archive/2026-10-18-calibration/`; verify `sha256sum -c SHA256SUMS`; set
   `WF_THRESHOLDS=~/bench-archive/2026-10-18-calibration/thresholds.json`. Do not regenerate the
   thresholds or coverage from B9 data.
4. **Plans and deploy.** From the repository root, with `WF_PLAN_SET=release` and a fresh campaign
   root:

   ```bash
   scripts/deploy-campaign.sh --dry-run
   scripts/deploy-campaign.sh
   ```

   The dry run names the five `plans-release` plans, `caliber.json`, the published clients and the
   Proxifier profile. The real run writes `deploy.json` into the campaign root and stages
   `C:\wfbench\e2e\{full,udp,dns,dual,base}-plan.json` + `caliber.json`. Verify `deploy.json` commit,
   `dirty`, `campaignEnvHash`, plan hashes and `caliber.sha256`.
5. **Targets and VM.** Start both Linux targets with `scripts/start-targets.sh`; verify labels
   `target:40010` and `target:40011` and remove/fresh the two ledgers. Run
   `scripts/wf.sh file scripts/verify-vm.ps1 600 -TargetAddress "$WF_HOST_IP"`; check ProxiFyre 2.6.1,
   Proxifier 4.14, ProxyBridge, `ndisrd`/`ProxifierDrv`/`WinDivert`, sing-box `1.14.2-singfix`,
   `bench.ppx`, `ndisapi.dll` and the `C:\wfbench` layout. Firewall profiles must be off; the clock
   probe must be reachable.
6. **Ledgers.** The calibration tree has no host `ledger-main.jsonl`/`ledger-direct.jsonl`; B9 must
   collect both. Keep `WF_LEDGER_DIR` pointing at the host directory that holds them, remove any stale
   copy before the run, and leave both files in place after the run. `publish-campaign.sh` copies them
   into `raw/`; the release verdict must show nonzero arm/file counts under `clock.correctedLedger`
   and `clock.uncorrectedLedger`, not `no ledger found`. This is the first campaign with the
   corrected/uncorrected ledger evidence.
7. **Preflight only.** Run the orchestrator once with `-PreflightOnly` under a scratch out root (not the
   campaign root), check `environment.json`, `clockOffsetMs`, the firewall state, target reachability
   and `productVersions`, then discard the preflight tree. The calibration's clock was ~+1.10 s and
   drifted ~13–15 ms/pass; measure-and-correct is expected, not a synchronized clock.

### 11.2 The exact B9 campaign invocation

Use a fresh out root and set `WF_RESULTS_ROOT` to the same directory so `deploy.json` and the `passN`
directories are siblings at the campaign root that `publish-campaign.sh` zips:

```bash
export WF_RESULTS_ROOT='C:\wfbench\results\b9-release'
```

On the VM, with PowerShell 7:

```powershell
$a = @('-NoProfile','-ExecutionPolicy','Bypass','-File','C:\wfbench\orchestrator.ps1',
       '-Passes','7','-OutRoot','C:\wfbench\results\b9-release',
       '-Seed','20261006','-OrderSeed','0')
$p = Start-Process 'C:\Program Files\PowerShell\7\pwsh.exe' -ArgumentList $a -PassThru -WindowStyle Hidden `
     -RedirectStandardOutput C:\wfbench\orch.log -RedirectStandardError C:\wfbench\orch.err
```

- `-Passes 7`, full matrix (no `-Rows`), `-Seed 20261006`, `-OrderSeed 0`; default `-PlanRoot
  C:\wfbench\e2e`, default target `192.168.100.4` and ports, default `-RowTimeoutSeconds 1800`.
- Enable the watchdog task before the run; the heartbeat must stay fresh.
- Expected root: `environment.json`, `clock.jsonl`, `failures.jsonl`/`failures.json`,
  `deploy.json`, `pass1`..`pass7`, each pass with `order.txt`, `order.json`, `clean-slate.json`,
  `control-pre`, `control-post`, and the seven product rows.
- A pass may be rerun only for a pre-registered instrument gate (clock, product start, ledger identity);
  archive the failed pass and disclose it. A second rerun in the campaign reruns the whole campaign.
  "It looks slow" is not a rerun reason.

### 11.3 Analysis after the run

From the repository root:

```bash
bash benchmarks/WinForward.E2E/scripts/publish-campaign.sh \
  /tmp/wf-bench/b9-campaign \
  /home/paff/bench-archive/2026-10-18-calibration/thresholds.json
```

`publish-campaign.sh` zips/downloads the campaign root, copies `ledger-main.jsonl` and
`ledger-direct.jsonl` into `raw/`, checks `raw/deploy.json`, requires `pass1`..`pass7`, and runs the
analyzer from the repository root with `--release --thresholds <abs> --min-passes 6 --discard-pass 1`.
It writes the four release docs beside the legacy docs. The thresholds and coverage were written and
hashed before the B9 run; the analysis must read them, never derive a band from B9 data. If the archived
`thresholds.json` does not hash to `8ae8e31f...`, stop. Verify:

- `release-inputs.json`: `total: 7`, `discarded: [1]`, `measured: [2,3,4,5,6,7]`, `n: 6`,
  `required: 6`, `insufficient: false`; the archived file digests are
  `thresholds.sha256 = 8ae8e31f576fd3791b9aba2267953063307819cd84a388c791c579029b51af3c` and
  `caliber.sha256 = da148a1d99580475cdc75ea0b93130b55819aab1206074f9a21fd9ddd0a925d3`;
  `warmupSeconds: 5`.
- `verdict.release.json`: `primary` has exactly `mem.privateBytes.p50` and `loss.lossRate`; `lat.tcp_rtt.p50`
  and `cpu.proxy.vcpuPct` are in `exploratory` with `role: exploratory`; REL is the capability gate;
  `coverage` is the embedded `coverage.json`; clock status is `complete` and both
  `clock.correctedLedger` and `clock.uncorrectedLedger` show the two ledgers; gates/findings are printed.
- The B9 report prints all four values, the demotion, the band/MDD, n=6 measured and the simulated
  coverage. It does not make a product ranking, an absolute-latency claim or a WAN claim. It uses the
  analyzer's exploratory/report-only output for latency and CPU, not a prose override.

### 11.4 Archive and report

Copy the downloaded campaign tree, the four release docs, `thresholds.json`, `coverage.json` and
`raw/deploy.json` to `~/bench-archive/<date>-release/` (proposed `2026-10-19-release`; use the local
start date), generate `SHA256SUMS`, verify `sha256sum -c`, and check in only the report and manifest
under `benchmarks/results/`. Raw data stays out of git.

## 12. Limits

- Three calibration passes, one row, one session, one binary. The band describes short-term session
  noise, not machine-to-machine reproducibility; the n=6 coverage is method-level, not a B9 guarantee.
- The calibration tree is the reference row only. It has no opponent rows, so the family comparisons
  are `unevaluable` there; the band is a reference-noise estimate.
- Latency and CPU are too noisy for their pre-registered business effects at this calibration and are
  demoted to report-only. Only memory can carry an inferential claim; loss is retained but its
  zero-variance calibration makes the release state `unevaluable`, never `same`.
- REL capability fails at 19.345 pp (697/3603) on the reference row; this is a capability finding,
  not a band input. The release report reports `capability: fail` and makes no REL difference claim.
- Clock offset is ~+1.10 s and is measured-and-corrected per pass; drift 13.4–14.9 ms, residual
  ≤1.04 ms. The calibration tree has no target ledger, so the corrected/uncorrected ledger evidence
  exists only in B9.
- Product build provenance is split: `deploy.json` `bbfe12c` (clean), product source/clock probe
  `458d568`. The old AOT product failed to start; the valid product is the `458d568` rebuild.
- The BASE-singbox control is one 60 s TCP pass; the hop point estimate (+181.8 µs) is inside the
  direct control's run-to-run variation and is not resolved.
- The 3-pass calibration is not a release campaign; no `different`/`same`/`equivalent` state in this
  record is a release claim. The B9 analysis must use `--min-passes 6 --discard-pass 1` and the
  pre-registered file.

## 13. What the main session must resolve before B9

1. Commit the B8 record, `thresholds.json` and `coverage.json`; clean the worktree or deploy with a
   disclosed `--allow-dirty`.
2. Decide the deploy commit and product build source; record both in `deploy.json` and
   `environment.json`. Do not claim one commit if the deploy and product differ.
3. Update the README's `plans-release/` description if the two calibration files stay beside the five
   plans; it currently says "5 plans plus `caliber.json`". (Or move the files and point
   `WF_THRESHOLDS` at the archive copy.)
4. Create and checksum the calibration archive; set `WF_THRESHOLDS` to the archived copy.
5. Run `deploy-campaign.sh`, `verify-vm.ps1`, `start-targets.sh`, the preflight and the 7-pass
   invocation exactly as in §11; verify `release-inputs.json` before the report.
6. Keep the B9 report inside the limits: only memory is inferential; loss is `unevaluable`; latency and
   CPU are exploratory/report-only; REL is a capability gate; no product ranking, absolute latency or
   WAN claim.
