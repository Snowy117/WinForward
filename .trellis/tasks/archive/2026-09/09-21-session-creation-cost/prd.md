# Session creation cost: allocation and performance under churn

## Goal

Decide — on measured evidence — whether the per-session cost of creating proxy sessions
(managed allocation, setup latency, live footprint) is a production problem under high-churn
workloads (DNS waves, short-lived connections), and what per-session budget/anchor and follow-up
work should result.

## Background (confirmed facts, 2026-09-21)

- **Trigger**: the before/after benchmark of the structured-concurrency program
  (`865228f` → `feb5955`) measured per session **+~296 B** (Noop-transport probe) and
  **+~715 B** (real-transport probe) of managed allocation. Raw logs + join script:
  `/home/paff/Projects/WinForward-bench-results/` (`before/after-perf-R3.log`, `compare.py`).
- **Pre-existing per-session totals** at both revisions (marginal vs the 1-session sweep):
  Noop ~5.3 KB/session, real-transport ~89.4 KB/session (`UdpSessionBenchmarks`, BDN ShortRun,
  1/100/1000 sweeps). Real decomposes as ~5.3 KB bookkeeping + ~84 KB framework, matching the
  spec's own split.
- **Existing contract**, `hot-path.md` §3 (task 08-29-socks5-perf-fullpath):
  "UDP session cold-path bookkeeping budget: **≤1 KB per session** (slot, setup queue, task
  machinery, MAC copy, tombstone/tracking structures, logging). Measured 2.1 KB → ~0.4 KB";
  framework socket cost (~84 KB/session: control TCP connect + UDP ASSOCIATE + sockets) is
  declared outside the budget; the Noop probe is named as the gate that "tracks the bookkeeping
  budget".
- **Unreconciled**: the 2026-08-30 record (task 08-30-udp-alloc-jumbo) put the Noop probe at
  **≈3.9 KB/session** ("within ≤~4 KB anchor"); today's Noop probe at `865228f` measures
  **~5.3 KB/session** — ~1.4 KB/session accumulated between 2026-08-30 and this program's base,
  while the spec still states ≤1 KB / ~0.4 KB. No gate currently fails on this drift.
- **Churn reality**: DNS flows carry 1-2 datagrams per session, so per-session cost is never
  amortized over packets; the repo models establishment waves (`udp.burstEstablishment`,
  8-wide setup limiter `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:39`, 5 s TTL,
  8 MiB global budget) but documents no churn-rate target to check against.
- The Noop probe's fake contributes a small per-session cost of its own
  (`BenchmarkUdpTransport` + two `IPEndPoint`s, `benchmarks/WinForward.Benchmarks/BenchmarkShared.cs:174-189`).
- The packet hot path is contractually zero-alloc and was re-verified byte-identical in the A/B
  (dispatcher 160/352 B, UDP datagram 0 B); this task covers the session tier only.

## Resolved decisions

- **Outcome (2026-09-21, user)**: evidence + decision only. The task produces the per-session
  allocation decomposition, churn-reality measurements, a reconciled/re-anchored budget, and a
  prioritized follow-up task list. No product-code changes; optimization is scheduled separately.

- **Scope tier (2026-09-21, user)**: UDP full stack. Both the bookkeeping layer (~5.3 KB/session
  Noop) and the framework layer (~84 KB/session: per-session SOCKS5 control connection + UDP
  ASSOCIATE + relay socket, confirmed per-session with no reuse at
  `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:161-208` and
  `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:40`) are in scope; the spec's "out of
  scope" note covers the ≤1 KB budget contract only. TCP sessions are an explicitly deferred
  layer (same per-session dial pattern; findings should transfer).

- **Envelope & solution space (2026-09-21, user)**: no numeric churn target — the guiding
  principle is "minimize allocation as much as possible"; `unsafe`/direct memory manipulation is
  sanctioned when it demonstrably reduces allocations and improves performance. Measurements use
  the design's existing bounds (8-wide limiter, 8 MiB/5 s burst queue, 16 384 capacity) as
  representative load rather than a user-defined target.

- **Runtime lever (2026-09-21, user)**: if the decomposition shows async machinery (state
  machines, continuations, timers) is a material contributor to session cost, recommending a
  .NET 11 SDK upgrade is sanctioned — its async-specific optimizations are the rationale.

## Requirements

- R1 — Decompose the Noop-probe per-session total (~5.3 KB at `865228f`, ~5.6 KB at `feb5955`)
  into named contributors with a repeatable method (the probe's own fake contributes
  `BenchmarkUdpTransport` + two `IPEndPoint`s/session, `BenchmarkShared.cs:174-189`).
- R2 — Decompose the real-transport per-session total (~89.4 KB) and separate framework
  (control connect / UDP ASSOCIATE / relay socket) from bookkeeping.
- R3 — Measure churn at representative design-bound loads (burst waves + sustained setup at the
  limiter rate; no user-defined target) and report allocation rate, GC, and footprint behavior.
- R4 — Reconcile measured reality with the documented budget (`hot-path.md` §3: ≤1 KB
  bookkeeping, measured 2.1→0.4 KB; 2026-08-30 anchor ≈3.9 KB Noop) and produce a re-anchored
  budget with an explicit proposed number, including where it lands (spec edit vs follow-up task).
- R5 — Produce an evidence-ranked follow-up task list for reduction work, explicitly including
  `unsafe`/direct-memory options where they materially cut allocations, each with a safety
  argument and fallback, and runtime-upgrade options (e.g. .NET 11 async optimizations) where the
  evidence points at async machinery.

## Acceptance Criteria

- [ ] `research/noop-decomposition.md` attributes ≥90% of the Noop per-session total to named
  stages, each with bytes/session and a ≥3-run spread; the method is reproducible from the
  documented command line.
- [ ] `research/framework-decomposition.md` splits the real-transport per-session total into
  control-connection, ASSOCIATE, and socket components (or explicitly merged components),
  cross-checked against the ~84 KB anchor plus `udp.sessionFootprint` retained numbers.
- [ ] `research/churn-measurements.md` reports allocation rate and GC behavior at the
  design-bound burst and sustained loads (design §4), with raw JSONL/logs archived.
- [ ] `research/reconciliation-and-decision.md` contains the contributor tables, an explicit
  proposed numeric budget with rationale and falsification condition, the proposed `hot-path.md`
  §3 diff (unapplied), and the ranked reduction list including `unsafe` variants with safety
  arguments where applicable.
- [ ] TCP-deferred note: findings that transfer to per-TCP-session dials are listed.
- [ ] No product-code changes: `src/**` untouched; build zero-warning, tests, format, and
  `jb inspectcode` gates green.

## Open Questions

- None blocking. The re-anchored budget and the reduction plan are produced by R4/R5 and
  approved at the final review.

## Likely out of scope

- Packet hot path (already gated byte-exact).
- Windows-side kernel/driver setup costs.
- TCP session creation (deferred layer; revisit after the UDP findings).
