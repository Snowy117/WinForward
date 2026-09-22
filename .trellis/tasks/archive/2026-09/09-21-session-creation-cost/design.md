# Session creation cost — measurement & decision design

Companion to `prd.md` (R1–R5). This task is evidence + decision only: no product-code changes;
everything below happens under `benchmarks/**` and the task's `research/**`.

## 1. Deliverable map

| Output | Path | Requirement |
|---|---|---|
| Noop staged attribution | `research/noop-decomposition.md` | R1 |
| Framework split (real transport) | `research/framework-decomposition.md` | R2 |
| Churn measurements | `research/churn-measurements.md` | R3 |
| Reconciliation + re-anchored budget + ranked reductions | `research/reconciliation-and-decision.md` | R4/R5 |
| Proposed `hot-path.md` §3 re-anchor text (diff, not applied) | attached in the decision doc | R4 |

Seam preference order (no product-code edits): (1) existing internals reachable via
`InternalsVisibleTo` from the benchmark/test projects; (2) existing injectable factories
(`createControl`, `socketFactory`, `disableUdpConnectionReset` in
`Socks5UdpTransport.CreateAsync`); (3) benchmark-local wrappers/fakes. If attribution is
impossible without a product seam, record the gap and fall back to a source-level audit instead
of editing `src/**`.

## 2. Noop decomposition (alloc-probe bisect — repo prior art)

Staged variants of `PopulateSessionsNoopTransportAsync`, each cumulative over the previous,
measured with BDN `[MemoryDiagnoser]` at 1/100/1000 sweeps. The marginal delta between stages
prices one per-session stage each:

- **S0** — coordinator constructed, no traffic (fixed cost baseline).
- **S1** — + `TrySendSpanAsync` admission (slot + setup-queue enqueue).
- **S2** — + background setup start (task machinery, up to just before session attach). If no
  seam can stop the pipeline at this boundary, merge S2 into S3 and say so in the doc.
- **S3** — + session construction (`UdpProxySession` + quiescence scope/CTS + transport).
- **S4** — + attach + receive-loop start (state machine, receive-window lease rent).
- **S5** — + full readiness (flush through the fake transport; equals today's probe shape).
- **T** — teardown delta: S5 measured with the coordinator disposal inside vs outside the
  measured window.
- **F** — fake-only baseline (`BenchmarkUdpTransport` + two `IPEndPoint`s per session,
  `BenchmarkShared.cs:174-189`), reported and subtracted from every stage.

Rules: allocation bytes are the gate (BDN `Allocated`), never ns; ≥3 runs per variant and report
byte spreads (tiering wobble). Each stage variant must assert its own readiness (non-vacuous).

## 3. Framework decomposition (real transport)

Use the existing seams to isolate, per session:

- control-connection connect + handshake (real `Socks5ControlConnection` vs a fake control),
- UDP ASSOCIATE (part of the real-control variant; split exists only if the control connection
  exposes the associate step separately — otherwise report it as one component),
- relay socket create/bind + self-traffic registration (real socket vs fake).

Cross-check components against the ~84 KB/session anchor and the real-transport total
(~89.4 KB). Retained (post-GC) live footprint comes from the existing `udp.sessionFootprint`
scenario at 1/100/1000 — reuse it rather than building new instrumentation.

## 4. Churn measurement

- **Burst waves** — the existing `udp.burstEstablishment` matrix at the design bounds: burst
  sizes 48/128/256 × dial delays 0/5/20 ms (loopback; `--dial-delay-ms` makes the 8-wide limiter
  serialization visible). Report per-wave allocation and latency from the JSONL rows.
- **Sustained shape** — a benchmark-local loop cycling short-lived sessions at the limiter's max
  rate (8 / dial-delay), sampling `GC.GetTotalAllocatedBytes` and GC counts over a fixed window.
  Preferred shape: an optional repeated-wave/soak mode on the burst scenario; a new stability
  scenario is the fallback if extending the burst scenario distorts it. Either way the change is
  benchmark-project-only.
- Quality bar: bytes/session with spread across ≥3 runs; latency reported as ordinals only —
  the 2026-09-21 A/B established that this dev box cannot resolve sub-2× latency deltas on short
  runs (same-binary drift up to 2.8×). That lesson goes into the decision doc.

## 5. Decision outputs (R4/R5)

The decision doc must contain:

1. contributor table: stage → bytes/session → % of Noop total → reducibility verdict;
2. framework table: component → bytes/session → reuse/pool feasibility;
3. proposed re-anchored budget: an explicit number + rationale + the condition that would
   falsify it (and the ≤1 KB spec text's fate);
4. ranked reduction opportunities — expected savings, blast radius, compat risk, and, per the
   user directive, explicit `unsafe`/direct-memory variants where they materially cut allocation,
   each with a safety argument and a fallback;
5. TCP-transfer notes: which findings apply to per-TCP-session dials (TCP is the deferred layer).

Ranking criterion (user directive): allocation minimization first, performance second; solutions
that merely move cost elsewhere (e.g., to kernel memory) must say so explicitly. Where the data
shows async machinery (state machines, continuations, timers) is a material contributor, a .NET 11
SDK upgrade may be recommended as a lever — state its expected effect and migration risk.

## 6. Constraints & non-goals

- No product-code or behavior changes; packet hot path untouched.
- Windows-side kernel/driver costs out of scope.
- Baseline numbers for reconciliation: ~5.3 KB (Noop) / ~89.4 KB (real) per session,
  `865228f`→`feb5955` delta +296 B / +715 B.

## 7. Risks

- **Fake distortion** in S-stages (the fake adds its own cost and may change continuation/tiering
  behavior) → subtract F and cross-check totals against `QuiescenceScopeAllocationGateTests` and
  `HotPathAllocationGateTests`.
- **Attribution ambiguity** at stage boundaries (S2) → merge stages and document it.
- **Churn measurement noise** on the dev box → allocations/GC are the readable outputs; latency
  only ordinal, per §4.
