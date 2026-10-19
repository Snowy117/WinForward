# WinForward E2E release campaign — 2026-10-19

| Field | Value |
|---|---|
| Campaign id | `2026-10-19-release` (attempt 2; attempt 1 voided, see §2) |
| Report date | `2026-10-20` (host local; the campaign ran 2026-10-19 16:01–23:49 host / 01:01–08:49 VM −07:00) |
| Repository commit | `692b5446c122d11984b3e92b9970806a718d19c3` |
| Deploy record | `raw/deploy.json`: commit `692b544`, `dirty: false` (0 entries), `campaignEnvHash: 0f16bcae33b75a8f79b351a9057dbc6c8f2cbddff028544a94afa0d9d9d5fb42`, plan set `release` |
| Operator and session | Main remediation session on the Linux host, `wfbench-restore` VM session; campaign started 2026-10-19 16:01 host / 01:01 VM, orchestrator complete 23:49 host / 08:49 VM |
| Report status | `accepted` as the campaign record; the primary inferential claim is **withheld** because the memory environment left the calibrated band (§4, §8) |

## 1. Release caliber

The release caliber is the triple *(plan file, content hash, declared arm durations)*. The calibration
band was written and hashed before this campaign ran; the analysis read that file and did not derive a
band from campaign data.

| Item | Value |
|---|---|
| Plan | `benchmarks/WinForward.E2E/scripts/plans-release/full-plan.json` |
| Plan SHA-256 | `da148a1d99580475cdc75ea0b93130b55819aab1206074f9a21fd9ddd0a925d3` |
| Arm seconds | IDLE 30 · LAT 30 · LATLOAD 30 · DNS 30 · DNSALT 30 · LOSS 120 · REL 60 · THRU 30 · MIX 60 · PERSIST 60 |
| Thresholds file | `~/bench-archive/2026-10-18-calibration/thresholds.json` |
| Thresholds SHA-256 | `8ae8e31f576fd3791b9aba2267953063307819cd84a388c791c579029b51af3c` |
| Coverage file | `~/bench-archive/2026-10-18-calibration/coverage.json`, SHA-256 `366e4523bf9a20aa183b2142678dc72351ce5b6f0da0cbe2ec95e60d68d5b84e` |
| Calibration record | `.trellis/tasks/archive/2026-10/10-18-e2e-benchmark-campaign/research/b8-calibration.md` (three reference runs, 2026-10-18, same VM and binary) |
| Warm-up trimmed per arm | 5 s (`--warmup-seconds 5`) |

### Calibration in one table

`spread = (max − min) / min` over the three reference passes; `B = max(B_business, 1.5 × spread)`.

| Metric | Runs | Spread | B | B_business | Role |
|---|---|---:|---:|---:|---|
| `lat.tcp_rtt.p50` | 3713.023 / 3205.119 / 3476.479 µs | 0.15847 | 0.23770 | 0.05 | demoted: report-only |
| `cpu.proxy.vcpuPct` | 26.12242 / 24.08585 / 24.26911 % | 0.08455 | 0.12683 | 0.10 | demoted: report-only |
| `mem.privateBytes.p50` | 37.628906 / 39.605469 / 37.435547 MB | 0.05796 | **0.10** | 0.10 | primary |
| `loss.lossRate` | 0 / 0 / 0 pp | undefined | **0.50 pp** | 0.50 | primary, degenerate |

The calibration session's `environment.json` recorded 7,383 MB visible at its start; the VM's
dynamic memory then settled and the attempt-1 preflight (20:27 VM) read 6,114 MB visible. The band
is a short-term session-noise estimate, not a machine-to-machine constant.

### Deployment

| Item | Value |
|---|---|
| Commit | `692b544` clean (`src/` and `tests/` identical to `458d568`) |
| WinForward AOT | `WinForward.exe` SHA-256 `DBEFFF756AFAA04634A0B6F2300B7940A03604AE80EB48C1C6D62DF73E472A4F` |
| WinForward FDD | `WinForward.exe` SHA-256 `3FAED55A4F727AB52A9B8733F5CBF1BF1DAC459924A987B9793106910D567E37` |
| NDISAPI sidecar | `ndisapi.dll` 3.6.1.1, SHA-256 `C9ECE4A79E89F8B7F681E4E142B23CDBB2FDE497EA20D4742F0E7843E79E391F` |
| Sing-box relay | `sing-box 1.14.2-singfix`, SHA-256 `09B0E9AE296E461281714D76E0554FBB5BFAC24343E5AB23297707D2F943F403` |
| Opponents | ProxiFyre 2.6.1 `B317381D…`; Proxifier 4.14.0.1 `8C06F7CB…`; ProxyBridge CLI `A049E3D4…` |
| Platform | Windows 11 IoT Enterprise LTSC 10.0.26100, 16 logical CPUs, Hyper-V VM `WinLTSC`; power plan Balanced |
| Drivers | `ndisrd`, `ProxifierDrv`, `WinDivert` all Running |
| Targets | Host `192.168.100.4`; TCP/UDP 40010, direct TCP 40011, DNS 53 and 40053, direct DNS 40054 |
| NIC | Both adapters: checksum offload Rx+Tx, LSO v2, RSC, RSS enabled (same state as calibration; not changed) |

## 2. Passes, attempts and selection

| Item | Value |
|---|---|
| Attempt | `2` of 2. Attempt 1 is voided: a planned Windows Update restart killed pass 4 at 00:33 VM on 2026-10-19; archived at `~/bench-archive/2026-10-19-b9-attempt1-aborted/` |
| Pass directories | 7 (`pass1` … `pass7`), 9 rows each, 63 loaded runs |
| Discarded pass | `1` (warm-up) |
| Measured passes | `2, 3, 4, 5, 6, 7` |
| Measured n | 6 |
| Required n | 6 |
| Insufficient | false |
| Rerun history | No instrument-gate rerun inside attempt 2. Attempt 1 was voided as a whole, not repaired |

Pass 1's `proxifier` row ran with the Proxifier service holding no global profile: the row completed
every arm but reported `proxy truth: tcp=0 udp=0`. The profile was installed as
`C:\ProgramData\Proxifier\Default.ppx` (SHA-256 `E2E3B10E…`, 1457 B) and pass 2 verified
`tcp=1430, udp=0, directLeak=0`. Pass 1 is the discarded warm-up, so the zero-truth row is not a
measured result; it is still an instrument incident and is disclosed. During the fix the ProxiFyre
`app-config.json` was accidentally overwritten and restored byte-identically (408 B, SHA-256
`FE69DA07C5027916FB8B49E084897021F7150ACA27A343D2D63CA4DE483984F6`).

## 3. Primary family and decision rule

| Metric | Kind | Band (B) | Business (B_business) | MDD | Source |
|---|---|---:|---:|---:|---|
| `mem.privateBytes.p50` | ratio | 0.10 | 0.10 | 0.10 | calibration |
| `loss.lossRate` | pp | 0.50 | 0.50 | 0.50 | calibration |
| `lat.tcp_rtt.p50` | ratio | 0.23770 | 0.05 | 0.23770 | calibration (demoted) |
| `cpu.proxy.vcpuPct` | ratio | 0.12683 | 0.10 | 0.12683 | calibration (demoted) |

Reference row `wf-aot-nativeudp`; declared opponents `proxifyre`, `proxifier`, `proxybridge`.
Family size `m = 3` for memory, latency and CPU; `m = 2` for loss because Proxifier does not carry
UDP and is excluded from that family. Adjustment is Bonferroni, family-wise 95 %; each adjusted
interval covers `1 − 0.05/m`. Coverage for n = 6 measured passes is embedded verbatim from
`coverage.json`: method-level 93.78 % for the memory 95 % interval and 96.96 % for its adjusted
interval; the loss interval is not simulated because its calibration values are degenerate.

Decision vocabulary: `different` (adjusted interval excludes the band), `inconclusive` (interval
straddles the band or n is below `minPasses`), `unevaluable` (degenerate/zero-variance or missing
denominator), `report-only` (exploratory), `equivalent` only in a pre-declared TOST family. No TOST
family is declared in this thresholds file. A zero-event rate is `unevaluable`, never `same`.

Only memory and loss are primary. Latency p50 and CPU are demoted to exploratory because their
calibrated bands exceed their business effects; the demotion is a pre-registered calibration rule,
not a post-hoc choice.

## 4. Primary results

### Memory — the analyzer returns `different`; this report withholds the claim

| Metric | Reference median | Opponent | Passes | Median ratio | Raw CI (95 %) | Adjusted CI (98.33 %) | Analyzer state |
|---|---|---:|---:|---:|---|---|---|
| `mem.privateBytes.p50` | 37.307 MB | proxifyre | 6 | 0.86294 | [0.85763, 0.86486] | [0.85763, 0.86655] | different |
| `mem.privateBytes.p50` | 37.307 MB | proxifier | 6 | 4.4630 | [4.4217, 4.5266] | [4.4216, 4.5266] | different |
| `mem.privateBytes.p50` | 37.307 MB | proxybridge | 6 | 14.695 | [14.035, 15.465] | [13.962, 15.476] | different |

These are the pre-registered analysis's numbers, and they are **not adopted as a release claim**.
The campaign record fixed the memory-comparability floor before any measured pass completed
(`.trellis/tasks/archive/2026-10/10-18-e2e-benchmark-campaign/research/b9-attempt2-environment.md`): the memory family is not comparable when a measured pass
boundary's guest-visible RAM falls below 5,500 MB, 10 % under the 6,114 MB attempt-1 preflight the
release was planned against. Measured pass starts fell through that floor: pass 2 started at
6,367 MB (+4.1 %), pass 6 at 5,310 MB (−13.1 %), and pass 7 at 4,075 MB (−33.3 %); the 3 GB + 2 GB
artificial occupancy did not survive the host's dynamic-memory reclaim. Under the campaign's own
rule the memory family is **not comparable to calibration**, so the campaign carries **no primary
inferential claim**. The verdict document prints `different` because the release model has no
environment gate and must publish what it computed; the claim boundary is applied here, not
retro-fitted into the analyzer. A publishable memory family needs a rerun with guest-visible RAM
pinned, or proved constant, across all measured passes.

### Loss — `unevaluable`

| Metric | Opponent | Passes | Median | Raw CI | Adjusted CI | State |
|---|---|---:|---:|---|---|---|
| `loss.lossRate` | proxifyre | 6 | 0 pp | [0, 0] | [0, 0] | unevaluable |
| `loss.lossRate` | proxybridge | 6 | 0 pp | [0, 0] | [0, 0] | unevaluable |
| `loss.lossRate` | proxifier | 0 | n/a | n/a | n/a | not carried (UDP bypassed) |

Every measured loss rate is zero with no spread. The zero-width interval is `unevaluable`, never
`same`; the family carries no difference or equivalence claim. This is the same degeneracy the
calibration recorded (0/0/0 pp).

### Latency and CPU — the other two family values, exploratory

| Metric | Reference median | proxifyre | proxifier | proxybridge |
|---|---:|---|---|---|
| `lat.tcp_rtt.p50` | 3919.359 µs | 1.073 [0.919, 1.145] | 0.929 [0.847, 1.035] | 0.935 [0.720, 1.000] |
| `cpu.proxy.vcpuPct` | 23.582 % | 0.487 [0.473, 0.499] | 2.627 [2.585, 2.701] | 0.592 [0.565, 0.636] |

Values are median ratios to the reference row with raw 95 % intervals over the six measured passes.
Both metrics are report-only: no difference, equivalence or "faster by" statement is made. The full
per-pass values and every exploratory interval are in `verdict.release.json`.

## 5. REL capability gate

| Row | Events / attempts | Rate (pp) | 3/n upper bound | Threshold | Status |
|---|---:|---:|---|---:|---|
| `wf-aot-nativeudp` | 1399 / 7206 | 19.414 | n/a (events > 0) | 0.25 pp | fail |
| `proxifyre` | 1413 / 7206 | 19.609 | n/a | 0.25 pp | fail |
| `proxifier` | 1356 / 7206 | 18.818 | n/a | 0.25 pp | fail |
| `proxybridge` | 1364 / 7206 | 18.929 | n/a | 0.25 pp | fail |
| `wf-aot-opt` | 1396 / 7206 | 19.373 | n/a | 0.25 pp | fail |
| `wf-fdd-opt` | 1405 / 7206 | 19.498 | n/a | 0.25 pp | fail |
| `wf-aot-dnsrelay` | not measured (dns-only plan) | — | — | 0.25 pp | not-measured |
| control blocks | not measured (BASE-only plan) | — | — | 0.25 pp | not-measured |

The reference row itself fails: 19.414 pp against a 0.25 pp bound, consistent with the calibration's
19.345 pp (697/3603). This is a capability bound, not a reference-vs-opponent difference claim, and
it does not become a band input.

## 6. Clock, gates and findings

| Item | Value |
|---|---|
| Clock status | `complete`, applied per pass; policy measure-and-correct (`requireSync: false`) |
| Probes | 70 samples, 0 bad lines; preflight median offset +1456.5 ms (VM − host) |
| Per-pass offset | pre +1456.7 → +1887.4 ms; post +1530.8 → +1955.1 ms |
| Drift | 67.6–74.1 ms per pass, all below the 100 ms limit; residual ≤ 0.97 ms |
| Corrected ledger | 700 arm windows in 2 files: 58 connection mismatches, 1 datagram mismatch, 0 unattributable |
| Uncorrected ledger (negative control) | 700 arm windows in 2 files: 293 connection mismatches, 265 datagram mismatches, 0 unattributable |
| Invalidated cells | 20 cells, all `proxybridge`: LATLOAD p50/p99 (passes 3, 4, 6, 7), DNS and DNSALT rate/p50 (passes 3, 6, 7) — `windowOverflow` / `clientSendLoss` gates |
| Findings by severity | correctness-failure 7 · path-interference 4 · harness-error 28 · measurement-caveat 570 · informational 8 |

The offset exceeded the optional ±200 ms sync bound in every pass, which is why the campaign ran
measure-and-correct rather than `-RequireClockSync`; the drift stayed inside the gate and the
correction removes 235 of 293 connection mismatches and 264 of 265 datagram mismatches.

Campaign caveats (no product cell is invalidated):

- `foreign-connection` (correctness failure): ProxyBridge delivered datagrams of one flow into
  another in the MIX arm in all 7 passes — 1715, 3998, 1997, 1920, 4209, 2041, 3617 datagrams. This
  is a ProxyBridge correctness finding, reported separately from the family comparisons.
- `control-drift-undecided` (measurement caveat): post/pre control-block ratios 1.0223
  (CI 1.0024–1.1293) and 1.0261 (CI 0.9620–1.0990) straddle the ±5 % agreement band, so the two
  control blocks are not shown to agree.
- `direct-lane-latency` (path interference): the direct lane's LAT p50 is 2501.6 µs on the
  WinForward rows, 3175.4 µs proxifyre, 2906.1 µs proxybridge — the lane is a property of each
  path, not a product effect.
- `udp53-direct` / `udp-not-carried` (informational): the port-53 DNS arm is a direct path on every
  row; Proxifier carries no UDP and is excluded from UDP comparisons.

## 7. Exploratory metrics and other rows

- Exploratory metrics: the 18 metrics outside the primary family and the capability gate, including
  `lat.tcp_rtt.p50`, `lat.tcp_rtt.p99`, `lat.udp_rtt.p50`, `latload.*`, `dns.*`, `dnsalt.*`,
  `thru.goodputMbps`, `mix.udp.lossRate`, `persist.*` and `cpu.proxy.vcpuPct`. Values and raw
  intervals only; no difference or equivalence claim.
- Internal WinForward pairs (`wf-aot-opt`, `wf-fdd-opt`, `wf-aot-dnsrelay` against the reference):
  `inFamily = false`, report-only. They are configuration variants, not declared opponents.
- `BASE-singbox`: a calibration-day control only (one 60 s TCP pass; hop point estimate +181.8 µs,
  inside the direct control's run-to-run variation). It is not a row in this campaign and carries no
  release claim.
- Control blocks `control-pre` / `control-post` ran fixed and are used for the drift check, not for
  product comparisons.

## 8. Limits

The measurement model's limits, verbatim:

- This is a solution comparison under one declared workload, not a product ranking; the seven rows
  are seven factor bundles.
- No absolute latency is claimed: there is no independent external reference, and the loopback path
  plus one vSwitch hop is not a WAN.
- The interval describes short-term session noise over the measured passes, not
  machine-to-machine reproducibility.
- The release rules and this verdict are C#-only; the Python oracle covers the unchanged legacy
  `tables.md` and `verdict.json`, not these files.
- The clock correction uses one offset per pass (the pre/post line at the pass midpoint); a
  campaign whose drift exceeds the declared limit is disclosed, not rescaled.
- Latency and CPU conclusions are phrased as "not worse than X" or "difference at least Y", never
  as a speed-up percentage.
- Internal WinForward row pairs and every metric outside the pre-registered primary family are
  exploratory: values and raw intervals only, no difference or equivalence claim.
- QUIC/HTTP3, IPv6, kernel DPC/ISR, true blinding and connection-id-level ledger attribution are
  outside this campaign.

Session-specific limits:

- **Attempt 1 was destroyed by Windows Update.** A planned restart (MoUsoCoreWorker 00:30:03,
  TrustedInstaller 00:34:39 VM) killed pass 4. Attempt 1 is voided and archived; attempt 2 is this
  campaign. Windows Update was paused/disabled before attempt 2.
- **The memory family is not comparable.** Preflight 1,831 MB; pass 2 start 6,367 MB; pass 6 start
  5,310 MB; pass 7 start 4,075 MB. The 5 GB artificial occupancy was reclaimed by the host's
  dynamic-memory balloon despite the allocation processes remaining alive. The analyzer's memory
  states are printed but not claimed (§4).
- **Proxifier needed a service-wide profile.** Pass 1's proxifier row had zero proxy truth because
  the service, running as LocalSystem, reads a global profile rather than the per-campaign copy.
  The profile was installed for the service and pass 2 onward measured `tcp=1430`. The ProxiFyre
  config was restored byte-identically after an accidental overwrite (hashes in §2).
- **WinDivert had to be re-registered after the restart.** The kernel driver was absent after the
  reboot; it was re-created against `C:\Program Files\ProxyBridge\WinDivert64.sys` and verified
  Running before attempt 2 (driver table in §1).
- **Clock offset is ~1.5–2.0 s and is corrected per pass.** The VM runs ahead of the host by
  1456.7–1955.1 ms; drift 67.6–74.1 ms; residual ≤ 0.97 ms. The optional ±0.2 s sync gate was not
  used, by agreement (measure-and-correct).
- **Product provenance is split.** The deployed commit is `692b544`; the AOT product binary was
  built at `458d568`, whose `src/` and `tests/` are identical to `692b544`. `deploy.json` records
  the deployed commit; the product hashes are in `environment.json` and §1.
- **The VM's dynamic-memory setting and snapshot id are not readable from the guest** ("Invalid
  namespace"); the recorded `vmId` is `6D606EF1-F6F2-433C-89E4-4682116C8A3D`. NIC offloads were
  enabled on both adapters for every pass and were not varied.
- **Publish recovery.** The automated publish downloaded the campaign zip only to 51 % and `unzip`
  failed; the downloaded copy was complete (4,617,909 bytes; `unzip -t` clean). Steps 2–5 of
  `publish-campaign.sh` were run by hand against that verified zip: extract, copy both ledgers, and
  the same release invocation. The analysis ran from the main checkout at `692b544` because the
  clean publish worktree had no restored `obj/` (`NETSDK1004`). No input differs from the scripted
  path.
- **Loss is zero-event degenerate**, and **REL is a capability gate**, not a difference claim; both
  statements hold for the reference row as well as the opponents.

## 9. Appendix: old E5-b2 data (legacy, not release evidence)

Do not merge this appendix into any release table. It records the re-analysis decision:

- E5-b2 is a 3-pass, mixed-caliber legacy campaign; it has no pre-registered calibration band and no
  `clock.jsonl`.
- Formal release re-analysis is stopped with `E5B2-NO-PREREGISTERED-BAND`, `E5B2-MIXED-CALIBER`,
  `E5B2-REFERENCE-ROW-NOT-ON-RELEASE-PLAN`, `E5B2-N=3<6`, `E5B2-NO-CLOCK-LOG` and
  `E5B2-PLAN-NOT-ARCHIVED`.
- A diagnostic state check on the old reference and old constants is recorded in
  `.trellis/tasks/archive/2026-10/10-18-e2e-benchmark-evidence/research/reanalysis-notes.md`; its `different` cells
  are not release claims.
- The old latency/CPU/memory conclusions keep their legacy meaning. The old loss `same` is the
  degenerate zero-variance case (now `unevaluable`), and REL is a capability gate (old data: fail),
  not a difference claim.

## 10. Provenance checklist

- [x] `thresholds.json` and `coverage.json` archived beside the campaign (`8ae8e31f…`, `366e4523…`).
- [x] `release-inputs.json`, `verdict.release.json`, `tables.release.md`, `findings.release.json`
      present; the legacy `tables.md`/`verdict.json` are the release run's legacy outputs and were
      not edited by hand.
- [x] `sha256sum -c SHA256SUMS` passes in `~/bench-archive/2026-10-19-release/`.
- [x] `benchmarks/results/2026-10-19-release.manifest.json` matches the archive: campaign id,
      caliber path/hash/seconds, file hashes and archive path.
- [x] Raw data is not committed; only this report and the manifest are checked in.
- [x] The report prints the release caliber, primary family, band/MDD, coverage, attempts and limits.
