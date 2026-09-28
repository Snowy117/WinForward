# Churn measurements — burst waves, sustained setup, retained footprint (task 09-21-session-creation-cost, design §4)

> **ERRATUM (2026-09-22, task `09-22-session-creation-cost-redo`).** Every real-dial allocation
> row here (the wave matrix and the sustained tables) is inflated by ~75.5 KB/session: the loopback SOCKS5 server ran in the same process as the
> measured client and allocates a 64 KiB relay-loop buffer plus per-connection socket/arrays for
> every accepted control connection. Clean values (server out of process): churn whole cycle
> **13,248.9–14,069.9 B/session** (wave matrix) and **13,720.2–13,868.6 B/session** (sustained);
> the recorded shapes still reproduce (in-process sanity cell 91,465 vs 90,829–91,088 B/session).
> The flatness / no-loss / unamortized-shape findings and the footprint rows stand (footprint
> uses fake transports; re-validated within the recorded band). Corrected report
> (repo-root-relative): `.trellis/tasks/09-22-session-creation-cost-redo/research/churn-measurements.md`.

Scope: UDP session churn at the design bounds (`udp.burstEstablishment` wave matrix, the new
`udp.churn` scenario in wave and sustained modes, `udp.sessionFootprint`) through the real SOCKS5
dial path against an in-process loopback server. Allocation bytes (`GC.GetTotalAllocatedBytes`) and
GC counts are the readable outputs; latency is reported as ordinals only — the 2026-09-21 A/B
established this dev box cannot resolve sub-2× latency deltas on short runs. Baseline reference
values for the same code (`research/framework-decomposition.md`): real-transport probe marginal
92,255 / 92,265 / 92,244 B/session; Noop probe marginal 5,737 / 5,737 / 5,727 B/session.

## Method and command lines

New benchmark-project scenario `UdpChurnScenario` (file
`benchmarks/WinForward.Benchmarks/Stability/UdpChurnScenario.cs`, CLI wired through
`SoakOptions`/`SoakRunner`: `--churn-waves K`). Each process fires one unmeasured warmup wave
(first-wave JIT/tiering is ~2× steady-state), then:

- **wave mode** (`--churn-waves 1`): fires one wave of `--burst-flows` short-lived sessions, waits
  for the first response per flow, then retires every session through the coordinator's own
  idle-expiry path (`RemoveExpiredAsync`, zero timeout) — allocation/GC sampled around the whole
  fire → first-responses → retire cycle, one JSONL row per wave;
- **sustained mode** (`--churn-waves 0`): cycles waves back-to-back for `--duration` seconds,
  emitting one aggregate row (realized session rate, allocation rate, GC counts, per-wave and
  per-first-response distributions).

The full campaign ran sequentially, one process at a time, from the repo root
(`/tmp/churn-campaign.sh`; timestamps + `/proc/loadavg` per invocation in
`research/raw/churn-campaign.progress`, observed load ≈ 1.9–5.6):

```text
# A: existing burst matrix (reference latency rows), N x D
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpBurst --flows 16 --pps 4000 --burst-flows N --dial-delay-ms D --output research/raw/udpburst-N{N}-D{D}.jsonl
# B: new churn wave matrix, 3 runs
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn --burst-flows N --dial-delay-ms D --churn-waves 1 --output research/raw/udpchurn-run{R}-N{N}-D{D}.jsonl
# C: sustained shape, 30 s window, 3 runs each
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn --burst-flows 48 --dial-delay-ms D --churn-waves 0 --duration 30 --output research/raw/udpchurn-sustained-D{D}-run{R}.jsonl
# D: retained footprint, 3 runs
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario footprint --output research/raw/footprint-run{R}.jsonl
```

(`{N}`/`{D}`/`{R}` expanded per cell; `research/raw/` abbreviates the full
`.trellis/tasks/09-21-session-creation-cost/research/raw/` output paths used by the campaign.)

## Burst matrix (udp.burstEstablishment; allocation not sampled by this scenario)

`--flows 16 --pps 4000` background traffic (0 loss in all cells, control/post ~4,000 pps), wave of
N sessions per cell. Latency numbers are ordinals (single run per cell; the serialization step
~ceil(N/8)·dial-delay is the visible shape). Raw:
`research/raw/udpburst-N{48,128,256}-D{0,5,20}.jsonl`.

| N | dial delay | accepted/rejected | est. loss | first-response p50 | p95 | max | time-to-issue | time-to-last |
|---:|---:|---|---:|---:|---:|---:|---:|---:|
| 48 | 0 ms | 48 / 0 | 0 | 14.3 ms | 24.7 ms | 24.9 ms | 4.3 ms | 24.9 ms |
| 48 | 5 ms | 48 / 0 | 0 | 39.0 ms | 60.2 ms | 60.4 ms | 4.3 ms | 60.4 ms |
| 48 | 20 ms | 48 / 0 | 0 | 75.0 ms | 142.9 ms | 144.0 ms | 4.6 ms | 144.0 ms |
| 128 | 0 ms | 128 / 0 | 0 | 29.4 ms | 48.9 ms | 49.9 ms | 7.4 ms | 49.9 ms |
| 128 | 5 ms | 128 / 0 | 0 | 66.5 ms | 120.9 ms | 127.5 ms | 6.6 ms | 127.5 ms |
| 128 | 20 ms | 128 / 0 | 0 | 178.3 ms | 336.1 ms | 355.6 ms | 6.8 ms | 355.6 ms |
| 256 | 0 ms | 256 / 0 | 0 | 52.2 ms | 95.3 ms | 98.1 ms | 8.4 ms | 98.1 ms |
| 256 | 5 ms | 256 / 0 | 0 | 129.3 ms | 236.6 ms | 248.3 ms | 6.5 ms | 248.3 ms |
| 256 | 20 ms | 256 / 0 | 0 | 364.0 ms | 685.3 ms | 725.3 ms | 6.8 ms | 725.3 ms |

No wave lost a session or datagram at the design-bound loads; time-to-last tracks
ceil(N/8) × dial-delay plus a fixed loopback/pipe tail (24.9 ms at zero delay for N=48, 98.1 ms
for N=256). This scenario samples no allocation counters — the allocation side of churn comes
from the `udp.churn` rows below.

## Churn wave matrix (`udp.churn` wave mode; 3 runs per cell)

Whole-cycle allocation (fire → responses → retire) per session, B/session; GC counts of the wave
window; ordinals from the same rows. Raw: `research/raw/udpchurn-run{R}-N{N}-D{D}.jsonl`.

| N | dial delay | B/session run1 / run2 / run3 | spread | gen0/gen1/gen2 | retire | first-resp p50 / p95 |
|---:|---:|---|---:|---|---:|---:|
| 48 | 0 ms | 91,088 / 91,034 / 90,829 | 259 | 0 / 0 / 0 | 6.4–6.9 ms | 8.5–11.9 / 11.2–16.5 ms |
| 48 | 5 ms | 92,406 / 91,802 / 91,638 | 768 | 0 / 0 / 0 | 6.4–7.0 ms | 20.7–24.2 / 36.9–41.6 ms |
| 48 | 20 ms | 91,132 / 91,736 / 91,758 | 626 | 0 / 0 / 0 | 7.2–8.1 ms | 67.5–69.7 / 128.7–131.4 ms |
| 128 | 0 ms | 91,343 / 91,309 / 91,296 | 47 | 1 / 0 / 0 | 16.4–18.1 ms | 21.9–23.3 / 31.4–35.1 ms |
| 128 | 5 ms | 91,901 / 92,202 / 92,139 | 301 | 1 / 0 / 0 | 15.9–17.4 ms | 58.2–59.9 / 103.0–106.3 ms |
| 128 | 20 ms | 90,934 / 90,961 / 91,036 | 102 | 1 / 0 / 0 | 15.9–18.0 ms | 175.3–182.6 / 335.8–341.6 ms |
| 256 | 0 ms | 91,817 / 91,789 / 91,851 | 62 | 3 / 3 / 1 | 31.5–34.6 ms | 32.1–35.3 / 73.8–82.2 ms |
| 256 | 5 ms | 92,451 / 92,359 / 92,216 | 235 | 3 / 3 / 1 | 34.8–37.5 ms | 106.3–107.5 / 201.7–218.7 ms |
| 256 | 20 ms | 91,114 / 91,170 / 91,291 | 177 | 3 / 3 / 1 | 31.8–32.9 ms | 346.1–346.5 / 666.6–685.8 ms |

Every wave: accepted = N, rejected = 0, first responses = N, establishment loss = 0 (the flow keys
are re-offered each wave, so a cooldown-driven loss would show up here). Findings:

- **Allocation is flat at ~91 KB/session** (90.8–92.5 KB; spreads ≤ 0.9 %) across N and dial delay —
  per-session cost is not amortized by wave size, and delaying the dial does not change it.
- Dial delay moves allocation by ≤ 1.5 % and not monotonically (D5 rows sit ~0.4–1.5 % above D0
  within each N; D20 rows land between D0 and D5 only at N = 48 and below D0 at N = 128/256) —
  inside/adjacent to the run spread, no allocation trend with delay.
- **GC counts track allocated volume, not latency**: N=48 (~4.4 MB) → no collection; N=128
  (~11.7 MB) → one gen0; N=256 (~23.5 MB) → 3 gen0 / 3 gen1 / 1 gen2 — i.e. ~11.7 MB per gen0.
- Cross-check against the earlier records: 91 KB/session ≈ framework path (83.4 KB, measured
  create+dispose) + Noop bookkeeping stages (~5.8 KB product-shaped) + a ~1.6–3.3 KB unseparated
  residue from the real response/retire interaction (real socket receive path, response sink,
  per-wave retirement); the component split of that residue is a recorded gap, not a claim.

## Sustained shape (`udp.churn`, `--churn-waves 0 --duration 30`; 3 runs each)

48 flows/wave, waves back-to-back for 30 s wall (the in-flight wave completes, so wall time
overshoots: 30–58 s). Raw: `research/raw/udpchurn-sustained-D{0,5}-run{R}.jsonl` (+ `.log`).

| dial delay | run | waves | sessions | allocated | B/session | MB/s | sessions/s | gen0/gen1/gen2 | MB per gen0 | wall |
|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|
| 0 ms | 1 | 403 | 19,344 | 1,757 MB | 90,836 | 38.8 | 427.5 | 152 / 151 / 62 | 11.6 | 45.3 s |
| 0 ms | 2 | 289 | 13,872 | 1,264 MB | 91,094 | 32.1 | 352.5 | 110 / 109 / 44 | 11.5 | 39.4 s |
| 0 ms | 3 | 336 | 16,128 | 1,466 MB | 90,875 | 32.2 | 354.2 | 128 / 127 / 47 | 11.5 | 45.5 s |
| 5 ms | 1 | 448 | 21,504 | 1,959 MB | 91,083 | 65.2 | 715.6 | 166 / 165 / 68 | 11.8 | 30.1 s |
| 5 ms | 2 | 483 | 23,184 | 2,111 MB | 91,073 | 36.1 | 396.5 | 182 / 181 / 74 | 11.6 | 58.5 s |
| 5 ms | 3 | 252 | 12,096 | 1,104 MB | 91,248 | 24.1 | 264.3 | 93 / 92 / 40 | 11.9 | 45.8 s |

Per-wave distribution (B/session): p50 91.7–92.1 KB, p95 98.0–98.8 KB, max ≤ 100.7 KB in every run
(min 81.2–81.6 KB). First-response ordinals: D0 p50 8.1–13.2 / p95 18.5–25.6 / max 27.4–37.2 ms;
D5 p50 23.2–25.6 / p95 39.4–49.9 / max 55.0–66.7 ms. Findings:

- **Per-session allocation is invariant across the sustained shape too**: 90,836–91,248 B/session
  (spread 412 B, 0.45 %) over 12k–23k sessions per run, ~1.1–2.1 GB allocated per 30 s window.
- Realized setup rate is dominated by dev-box scheduling noise: 264–716 sessions/s, and the D0/D5
  ordering inverts between runs (D5 faster than D0 in run 1/2) — do not read rate differences;
  the per-wave floor (issue + response poll + retire) dominates when dials are instant.
- GCs are volume-driven: ~11.5–11.9 MB per gen0; gen1 ≈ gen0 − 1; gen2 ≈ 0.37–0.43 × gen0
  (workstation GC promoting most survivors). No gen2 count exceeded 74 in a 30 s window.

## Retained footprint (`udp.sessionFootprint`; 3 runs)

Existing scenario, fake transports; populated to `sessions` sessions, settled 100 ms, then sampled
(`GC.GetTotalAllocatedBytes(precise: true)` over the populate window; working-set delta of the
process). Raw: `research/raw/footprint-run{R}.jsonl`.

| sessions | workingSetDeltaBytes (r1/r2/r3) | gen0 (r1/r2/r3) | allocatedBytes (r1/r2/r3) | marginal vs N=1 |
|---:|---|---|---|---:|
| 1 | 0 / 0 / 0 | 0 / 0 / 0 | 51,680 / 51,736 / 51,736 | — |
| 100 | 0 / 0 / 0 | 0 / 0 / 0 | 432,072 / 440,544 / 459,048 | 3,961.7 B/session |
| 1000 | 0 / 0 / 0 | 0 / 0 / 0 | 4,202,592 / 4,184,448 / 4,019,880 | 4,087.8 B/session |

Notes: `workingSetDeltaBytes` is 0 in all nine rows — the managed working set does not move
measurably within the sampled window on this box (the scenario assigns the receive windows and
fake transports but the OS/GC does not grow the reported working set in 100 ms), so **retained**
footprint is reported as "not observable at this granularity", not as "no cost". The allocated
marginal (3.96–4.09 KB/session, spread ~180 B) is consistent with the Noop stage marginals for
setup-without-teardown shapes (S4 = 4.32 KB/session; the footprint scenario does not wait for the
flush/readiness flip and adds populate-loop re-offers, so the two are cross-checks, not equal
measurements). The same rows are mirrored in `research/framework-decomposition.md`
("Cross-checks against the anchors", item 3).

## Cross-cutting findings and gaps

- Allocation gate: **~91 KB/session real churn**, stable to <1 % across wave sizes 48/256, dial
  delays 0/20 ms, and sustained runs (≈ 83.4 KB framework + ≈ 5.8 KB bookkeeping + ≈ 1.6–3.3 KB
  response/retire residue); **~4.1 KB/session** for the Noop-style footprint shape.
- Establishment was lossless in every cell (waves and sustained); the 8-wide limiter serializes
  waves as designed; latency and achieved rate are reported as ordinals only.
- Not measured: absolute retained (post-GC) working set per session (workingSetDelta 0 at this
  granularity — would need a longer settle or a forced-GC measurement pass); the response/retire
  residue split; TCP churn (deferred layer, same per-session dial pattern).
