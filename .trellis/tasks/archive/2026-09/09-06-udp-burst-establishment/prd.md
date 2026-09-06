# UDP flow-establishment burst performance

Parent: `08-30-proxy-perf-stability`.

## Goal

Track and quantify the reported issue: when dozens of new UDP flows appear at the same
instant (the establishment burst shape typical of application startup, e.g. a wave of DNS
queries), the proxy shows performance and compatibility problems. The immediate deliverable
of this task is a reproducible benchmark covering that establishment-burst shape, plus a
recorded baseline that says where the time goes and whether pre-established traffic is
disturbed. Product fixes are follow-up children gated on these findings.

## Background

- Report: a burst of ~dozens of simultaneous new UDP flows (e.g. DNS) degrades performance
  and/or breaks compatibility during the establishment window.
- Current establishment path is fully asynchronous (fire-and-forget setup, bounded setup
  queue with 8 MiB global budget / 5 s datagram TTL, 8-wide setup concurrency limiter,
  1 s cooldown tombstones on failure) — `.trellis/spec/backend/udp-relay.md`,
  `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`.
- Existing benchmarks do not cover the burst shape:
  - `UdpLossScenario` warmup establishes flows one datagram at a time and explicitly
    excludes the establishment window from every metric (`benchmarks/README.md` §Scenarios).
  - `UdpSessionBenchmarks` measures ordered populate totals (1/100/1000 sessions); it has
    no per-flow latency distribution, no concurrent background traffic, and no way to
    model remote SOCKS5 setup latency (loopback dial is sub-millisecond, which hides the
    8-wide limiter's queuing effect).
- Known related backlog items in the parent: coordinator gate contention at high churn,
  per-flow DNS cache, mux-shaped local transport, per-response IOCTL cost under DNS load.

## Requirements

- R1 — Burst establishment scenario: a stability-mode scenario that fires N new UDP flows'
  first datagrams back-to-back at one instant and reports the per-flow first-response
  latency distribution (min/p50/p95/p99/max/mean), time-to-first and time-to-last response.
- R2 — Establishment loss accounting: count datagrams rejected by `TrySendAsync`, first
  responses never observed (setup-queue drop / TTL expiry / cooldown tombstone shapes),
  with the product-event census available as an opt-in diagnostic.
- R3 — Head-of-line measurement: pre-established background flows send at a steady rate
  through control, burst, and post windows; report per-window send latency (p95/max),
  loss, and achieved pps so burst-window degradation against the control window is
  quantified (the compatibility half of the report).
- R4 — Remote-dial realism: an optional artificial SOCKS5 UDP-ASSOCIATE delay on the
  harness server (`--dial-delay-ms`), default 0 so existing scenarios and their series
  are unchanged.
- R5 — Baseline data and analysis: a recorded matrix (burst size × dial delay) under
  `benchmarks/results/`, with a short analysis mapping observed latencies to the
  suspected mechanisms (setup-limiter serialization, coordinator gate, flush ordering),
  and parent-backlog sync.

## Constraints

- Managed-only loopback harness (runs on any OS, no WinpkFilter/NDIS), consistent with the
  existing benchmark project.
- No product-code changes; no changes to existing scenarios' metrics or series
  comparability.
- Repo invariants: full test suite green, zero-warning build.

## Non-Goals

- Fixing the product (follow-up child tasks, decided from this task's findings).
- Real-NIC / ETW measurement (parent backlog `windows-real-nic`).
- BenchmarkDotNet-side changes (existing `UdpSessionBenchmarks` stays as-is; its Windows
  real-transport [1000] NA is already tracked in the parent PRD).

## Acceptance Criteria

- [x] `--stability --scenario udpBurst` runs clean on the Linux dev box and emits one
      `udp.burstEstablishment` JSONL row with the R1–R3 metrics documented in
      `benchmarks/README.md`.
- [x] Sanity model holds: with `--dial-delay-ms D`, the last-flow first-response latency
      grows ~linearly as `ceil(N/8) × D` (verifying the 8-wide limiter is the dominant
      serialization at zero background load). Matrix D=100 ms fit: +2.9–4.3 % across
      N=8..128; D=25 ms fit: +10–16 % (fixed handshake overhead relatively larger).
- [x] Baseline matrix recorded under `benchmarks/results/2026-09-06-udp-burst/`
      (bursts 8/16/32/48/64/128 × dial delays 0/25/100 ms, fixed seed), README states
      machine/runtime and interpretation notes. Plus a TTL-loss corner probe
      (128 × 4000 ms → 8/128 first responses, 93.75 % establishment loss) replacing
      the census pass (compile-time opt-in; every matrix point showed zero drop counts,
      so the probe is the informative instrument).
- [x] Analysis note in the results README: establishment time goes to setup-limiter
      wave serialization (coordinator gate, flow table, allocations exonerated — zero
      background degradation everywhere); recommendation = TTL stamp re-attribution
      (small) or local-mux-transport (structural), NOT lock work. Recorded as spec
      contract in `.trellis/spec/backend/udp-relay.md`.
- [x] Existing scenario rows unchanged: `udp.lossRate`, `udp.rawBaseline`, BDN families
      untouched (no series break; verified by trellis-check via quick runs + diff).
- [x] Full test suite green (501/501), zero-warning build.
- [x] Parent PRD child-task map updated with this task and its outcome.

## Notes

- The burst scenario's background load must stay under the Windows loopback ~4.7k pps
  ceiling when comparing window metrics on a Windows guest; document recommended
  invocation (`--flows 16 --pps 4000`).
