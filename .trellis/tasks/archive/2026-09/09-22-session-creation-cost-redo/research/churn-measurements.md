# Churn measurements (corrected) — real churn with the harness server out of process

Task `09-22-session-creation-cost-redo`, correcting
`archive/2026-09/09-21-session-creation-cost/research/churn-measurements.md`. Raw:
`research/raw/udpchurn-run{R}-N{N}-D{D}.jsonl` (wave matrix, 27 runs),
`research/raw/udpchurn-sustained-D{D}-run{R}.jsonl` (sustained, 6 runs),
`research/raw/udpchurn-incheck-N48-D0.jsonl` (in-process sanity cell),
`research/raw/footprint-run{R}.jsonl`.

Allocation (`GC.GetTotalAllocatedBytes`) and GC counts are the readable outputs; latency is
reported as ordinals only (this box cannot resolve sub-2× latency deltas). Every churn run
here uses `--socks5-external`, i.e. the loopback SOCKS5 server and its echo receiver run in a
child process; only the client, the coordinator and the response sink are measured.

## Method

```text
# wave matrix (3 runs per cell) — N in {48,128,256}, D in {0,5,20} ms
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn \
  --burst-flows N --dial-delay-ms D --churn-waves 1 --socks5-external \
  --output research/raw/udpchurn-run{R}-N{N}-D{D}.jsonl
# sustained shape (3 runs per D) — 30 s window, 48 flows per wave
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn \
  --burst-flows 48 --dial-delay-ms D --churn-waves 0 --duration 30 --socks5-external \
  --output research/raw/udpchurn-sustained-D{D}-run{R}.jsonl
# in-process sanity cell (the recorded shape; must still reproduce ~91 KB/session)
dotnet run ... --scenario udpchurn --burst-flows 48 --dial-delay-ms 0 --churn-waves 1 \
  --output research/raw/udpchurn-incheck-N48-D0.jsonl
# retained footprint (fake transports, unchanged path)
dotnet run ... --scenario footprint --output research/raw/footprint-run{R}.jsonl
```

Each wave process fires one unmeasured warmup wave, then measures the
fire → first-responses → retire cycle (retire through `RemoveExpiredAsync`, zero timeout).
Sequential runs, one process at a time; `/proc/loadavg` per run in the sibling `.load` files.

## Wave matrix (external; means of 3 runs, B/session)

| N | D = 0 ms | D = 5 ms | D = 20 ms |
|---:|---:|---:|---:|
| 48 | 13,248.9 (spread 210.5) | 13,181.9 (146.5) | 13,260.1 (219.8) |
| 128 | 14,069.9 (118.8) | 13,965.9 (87.9) | 13,938.6 (251.1) |
| 256 | 13,930.5 (130.8) | 13,954.1 (43.8) | 13,823.7 (118.2) |

Every cell: accepted = N, rejected = 0, first responses = N, establishment loss = 0,
**gen0/gen1/gen2 = 0/0/0** (the clean waves allocate 0.6–3.6 MB, below the gen0 budget).

Findings versus the recorded (contaminated) matrix:

- **Level**: 13.2–14.1 KB/session, versus 90.8–92.5 KB/session recorded — the difference is the
  in-process harness server (~75.5 KB/session, see `framework-decomposition.md` §3), not a
  client-side change.
- **Flatness holds**: the spread across N and dial delay is ≤1.9 % of the level (recorded
  ≤0.9 % of a 9× larger number); dial delay moves allocation by ≤1.0 % and not monotonically
  (D5 is −0.5 %/−0.7 % at N = 48/128 but +0.2 % at N = 256; D20 ranges −0.1 % to −0.9 %).
- **N-shape effect**: N = 48 lands ~0.7 KB/session (5 %) *below* N = 128/256 (13.2 vs 14.0 KB).
  The recorded matrix shows the same *direction* but much smaller relative size (91.1 → 91.8 KB,
  +0.8 %); with the harness removed the fixed-cost amortization of the small wave is now visible.
- **GC shape changes accordingly**: no collection at all in the clean waves (recorded N = 256
  rows collected 3× gen0 / 3× gen1 / 1× gen2 on ~23.5 MB); GC remains volume-driven.
- **Latency ordinals** keep the recorded shape (dial delay scales first-response p50/p95,
  loopback tail unchanged): e.g. N = 48 p50 9.3/23.1/68.7 ms at D = 0/5/20; N = 256 p50
  41.5/108.2/346.3 ms. Values track the recorded cells closely except the zero-delay large-wave
  rows (N = 256, D = 0: 41.5 ms vs 32.1–35.3 ms recorded), where the wave tail is
  scheduling-dominated; ordinals only, no claim.

## Sustained shape (external, 48 flows/wave, 30 s window)

| D | run | waves | sessions | allocated | B/session | MiB/s | sess/s | gen0/1/2 | MiB per gen0 | wall |
|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|
| 0 | 1 | 531 | 25,488 | 351,323,552 | 13,783.9 | 6.72 | 511.4 | 21/4/1 | 15.95 | 49.8 s |
| 0 | 2 | 407 | 19,536 | 269,746,184 | 13,807.6 | 5.79 | 439.7 | 16/7/1 | 16.08 | 44.4 s |
| 0 | 3 | 268 | 12,864 | 178,406,104 | 13,868.6 | 4.17 | 315.4 | 11/3/1 | 15.47 | 40.8 s |
| 5 | 1 | 348 | 16,704 | 229,622,736 | 13,746.6 | 4.13 | 314.8 | 14/5/1 | 15.64 | 53.1 s |
| 5 | 2 | 440 | 21,120 | 289,769,808 | 13,720.2 | 9.19 | 702.7 | 18/5/1 | 15.35 | 30.1 s |
| 5 | 3 | 409 | 19,632 | 269,865,088 | 13,746.2 | 4.33 | 330.5 | 16/7/1 | 16.09 | 59.4 s |

- **Per-session allocation is invariant**: 13,720–13,869 B/session (spread 148 B, 1.1 %) over
  12.9k–25.5k sessions per run, 4.2–9.2 MiB/s allocated (recorded: 90,836–91,248 B/session at
  24–65 decimal MB/s ≈ 23–62 MiB/s).
- Realized rate is dev-box scheduling noise as before (314.8–702.7 sessions/s; the D0/D5
  ordering inverts between runs) — not a dial-delay effect.
- GC is volume-driven: ~15.4–16.1 MiB per gen0 (recorded 11.5–11.9 decimal MB ≈ 11.0–11.3 MiB —
  the clean workload promotes *less* absolute volume, and with far fewer survivors: gen2 = 1 per
  run, recorded up to 74).

## In-process sanity cell (recorded shape must reproduce)

`udpchurn-incheck-N48-D0`: **91,465.2 B/session** (allocated 4,390,328 B, 48/0 accepted,
gen0 = 0) versus the recorded 90,829 / 91,034 / 91,088. That is +0.4 % and just outside the
recorded 3-run spread (259 B). The framework ladder A/B reproduced the archived values to
≤1 B in the same session, so the code path is identical; the small positive offset is read as
box state at the end of a 40-minute campaign, not as a behavior difference. Recorded as a
one-cell sanity check, not a re-measurement.

Provenance confirmation on the reviewed code: after the `trellis-check` pass applied its
behavior-preserving gate fixes, one further external cell (N = 48, D = 0) reproduced
**13,185.2 B/session** (campaign mean 13,248.9; range 13,130–13,341) —
`research/raw/udpchurn-confirm-N48-D0.jsonl`.

## Retained footprint (`udp.sessionFootprint`, fake transports)

| sessions | allocated, this redo (r1/r2/r3) | recorded | marginal vs N=1 (redo/recorded) |
|---:|---|---|---:|
| 1 | 51,616 / 51,680 / 51,616 | 51,680 / 51,736 / 51,736 | — |
| 100 | 437,648 / 430,600 / 436,992 | 432,072 / 440,544 / 459,048 | 3,874.2 / 3,961.7 |
| 1000 | 4,044,488 / 4,131,080 / 4,069,592 | 4,202,592 / 4,184,448 / 4,019,880 | 4,034.6 / 4,087.8 |

`workingSetDeltaBytes` is 0 in all nine redo rows as well: **retained** working-set growth
stays unobservable at this scenario's granularity (100 ms settle, no forced GC) — carried
gap, not a claim. The allocated figures reproduce the recorded band (the 100-session row's
lowest redo value, 430,600, is 0.3 % under the recorded minimum).

## Composition of the clean whole cycle and the residue

Clean components measured this redo (all allocation-based, same box):

| Component | B/session | Source |
|---|---:|---|
| framework path (dial + ASSOCIATE + relay socket + registration + transport ctor) | 7,952.0 | `framework-decomposition.md` §4 (isolated create+dispose) |
| session-setup bookkeeping (Noop probe, incl. its 460.8 B harness) | 5,727.0 | `udpsession-{ext,in}-run*` Noop rows (5,727.0 external / 5,734.0 in-process, within run spread; the Noop shape never touches the server) |
| **sum of named components** | **13,679.0** | |
| churn whole cycle, wave shape | 13,248.9 (N=48) – 14,069.9 (N=128) | this doc, §wave matrix |
| implied residue | **−0.4 KB (N=48) … +0.4 KB (N=128)** | churn − named components |
| real probe marginal (one populate of N, wholesale drain) | 17,021.2 | `udpsession-ext-run*` |
| implied residue of that shape | **3,342.2** | probe − named components |

The two shapes disagree on the residue because their retire and response profiles differ: the
churn retires each session mid-wave through `RemoveExpiredAsync` while the coordinator is
live, whereas the probe populates all N sessions and tears them all down in one wholesale
dispose at the end of the invocation, and it waits for all N echoed responses before that.
This is the same cost class the recorded docs carried as an unattributed
"1.6–3.3 KB/session response/retire residue"; the clean redo puts a measured value on both
ends of it (≈0.4 KB in the per-wave-retire shape, ≈3.3 KB in the populate-then-drain shape)
but does not split it internally — carried gap with a concrete follow-up (a component probe
for response receive + drain vs per-session retire).

## Cross-cutting findings

- **Whole-cycle churn is ~13.2–14.1 KB/session** (wave) and **13.72–13.87 KB/session**
  (sustained), spread ≤1.9 %, lossless in every cell, with the 8-wide limiter serializing waves
  as designed. Recorded levels (90.8–92.5 KB) are ~6.7× the clean ones; the difference is the
  harness server, quantified in `framework-decomposition.md` §3.
- The recorded shape still reproduces (in-process sanity cell, +0.4 %).
- Not measured (unchanged): absolute retained working set; the internal split of the
  response/retire residue; TCP churn (deferred layer; `LoopbackSocks5TcpServer` carries the
  same in-process artifact class).
