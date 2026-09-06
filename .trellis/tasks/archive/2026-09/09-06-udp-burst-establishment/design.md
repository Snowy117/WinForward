# Design — UDP flow-establishment burst benchmark

## Context

The establishment path under test (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`):
first datagram of a new flow → session slot + bounded setup queue allocated under the
coordinator gate → fire-and-forget `CreateSessionAsync` behind an 8-wide
`SemaphoreSlim` (`_setupLimiter`) → full SOCKS5 dial (TCP control connect + UDP ASSOCIATE)
→ setup-queue flush → slot ready. Failure lands a 1 s cooldown tombstone and drains the
setup queue (dropping the triggering datagram). Full semantics: `.trellis/spec/backend/udp-relay.md`.

What a burst of N simultaneous flows exercises that no current benchmark does:

1. Setup-limiter serialization — N setups queue 8-at-a-time; per-flow first-response
   latency is dominated by wave position (`ceil(k/8) × dial` for the k-th flow).
2. Coordinator-gate pressure — N slot allocations + N enqueue charges + background
   sends + flushes all take the same `Lock _gate`.
3. Setup-queue pressure — the triggering datagrams (and any retries) buffer for the
   whole dial duration; with a slow remote server they approach the 5 s TTL / 8 MiB
   budget / per-slot 32-datagram bounds.
4. Compatibility — pre-established flows must keep flowing while the burst establishes.

On loopback the dial is sub-millisecond, which hides (1) and (3); hence the delay knob (R4).

## Deliverable shape

One new stability scenario. No BDN changes (ordered populate is already covered; burst
metrics are latency distributions + loss, which is the stability runner's shape).

### New files / touched files

| File | Change |
|---|---|
| `benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs` | new — scenario implementation |
| `Stability/SoakOptions.cs` | add `SoakScenario.Burst` (`"udpBurst"`), options `--burst-flows` (default 48, ≥1), `--dial-delay-ms` (default 0, ≥0) |
| `Stability/SoakRunner.cs` | wire `("udpBurst", UdpBurstScenario.RunAsync)`; add to `All` |
| `Stability/LoopbackSocks5UdpServer.cs` | optional `TimeSpan associateDelay` ctor param (default `default`): `Task.Delay` before writing the UDP-ASSOCIATE reply in `HandleControlAsync`; existing callers unchanged |
| `benchmarks/README.md` | document scenario, options, invocation, interpretation |
| `benchmarks/results/2026-09-06-udp-burst/` | baseline matrix artifacts |

## Scenario protocol

Topology: exactly `UdpLossScenario`'s — `EchoReceiver` destination, `LoopbackSocks5UdpServer`
(+ delay knob), real `UdpProxyCoordinator` + `Socks5UdpTransportFactory`, counting response
sink. Payloads carry `DatagramHeader` (sequence, flowId) for attribution.

Phases (fixed window lengths; deterministic, self-terminating):

1. **Setup & warmup** — establish `--flows` background flows (one datagram each, wait ≤10 s
   for all observed, same as `UdpLossScenario.WarmupAsync`).
2. **Control window** (5 s) — background paced sender only (reuses the 10 ms tick pacing
   loop pattern, round-robin over background flows).
3. **Burst window** — at `t0`, fire `--burst-flows` distinct new flows' first datagrams
   back-to-back in a tight sequential loop (the pump delivering a flash crowd; sequential
   is correct — the product pump is serialized per adapter). Record per-flow `t0` via
   `Stopwatch.GetTimestamp()`. Background keeps pacing. Window ends when all burst
   first-responses are observed or 30 s timeout.
4. **Post window** (5 s) — background only.
5. **Drain** (2 s, still counts attributed responses) → dispose → one JSONL row.

### Measurement mechanics

- **Burst first-response latency** — the sink (`IUdpResponseSink`) reads the
  `DatagramHeader`, maps flowId → burst flow index, CAS-records the response timestamp
  into a `long[]`. Latency set = deltas vs per-flow `t0`. Buckets computed once after
  drain (sort N values, pick percentiles). `TrySendAsync` returning `false` for a burst
  datagram marks that flow rejected (no latency sample).
- **Background attribution** — sender stamps (window, flow, seq, send-timestamp) into a
  bounded in-flight ring (capacity ≥ pps × 12 s); the sink matches (flow, seq) → window.
  Ring overflow degrades to an `unattributed` counter (never blocks the send loop).
  Per-window metrics: sent, injected, lossRate, send-accept latency p95/max (latencies
  collected into per-window `List<double>`), achievedPps. Head-of-line verdict =
  burst window vs control/post windows.
- **Product-event census** — same opt-in pattern as `UdpLossScenario`
  (`CaptureProductEvents = false` by default; setup-phase events
  `udp.setupqueue.dropped`, `udp.setup.cooldown`, `udp.setup.failed` are the interesting
  ones for this scenario). One instrumented diagnostic pass may be run per baseline
  matrix point at a reduced background rate.

### JSONL row

Scenario `udp.burstEstablishment`, parameters
`{ burstFlows, dialDelayMs, backgroundFlows, backgroundPps, payloadBytes, seed }`, metrics:

```json
{
  "burstAccepted": 48, "burstRejected": 0, "firstResponses": 48,
  "establishmentLossRate": 0.0,
  "firstResponseMs": { "min": 1.2, "p50": 45.0, "p95": 310.5, "p99": 355.1, "max": 360.0, "mean": 130.4 },
  "timeToFirstMs": 1.2, "timeToLastMs": 360.0,
  "background": {
    "control": { "sent": 20000, "injected": 20000, "lossRate": 0.0, "sendP95Ms": 0.02, "sendMaxMs": 0.4, "achievedPps": 3998 },
    "burst":   { "sent": 21000, "injected": 20995, "lossRate": 0.00024, "sendP95Ms": 3.1, "sendMaxMs": 41.0, "achievedPps": 3890 },
    "post":    { "sent": 20000, "injected": 20000, "lossRate": 0.0, "sendP95Ms": 0.02, "sendMaxMs": 0.3, "achievedPps": 4000 }
  },
  "unattributedResponses": 0
}
```

(`productEvents` omitted unless the census is enabled — `StabilityContext` already
ignores nulls.)

## Series comparability

New scenario + new metric names ⇒ a new series; nothing existing changes. The loopback
server's default delay stays 0 so `udp.lossRate`, `udp.rawBaseline`, and
`UdpSessionBenchmarks` behavior is bit-identical. Timer discipline unchanged (the runner
already holds `timeBeginPeriod(1)` on Windows).

## Risks / mitigations

| Risk | Mitigation |
|---|---|
| Windows loopback ~4.7k pps ceiling makes background pps defaults (25k) saturate | Document `--flows 16 --pps 4000` for this scenario; achievedPps is reported, window-vs-window comparisons stay within-row |
| Sequential burst issue loop is itself the bottleneck (hides product latency) | The loop only does `TrySendAsync` (miss path: gate + slot alloc + enqueue; micro-scale) — measure issue-loop duration and report it (`timeToIssueMs`) so the harness can be exonerated |
| 30 s timeout too tight for large bursts × big delays | Timeout scales: `max(30 s, ceil(N/8) × delay × 3)` |
| Delay knob changes shared server for other scenarios | Default parameter; only the burst scenario passes a non-zero value |

## Alternatives considered

- **Extend `UdpLossScenario` with a burst warmup** — rejected: its series semantics
  ("steady-state window only") are load-bearing; mixing establishment in breaks them.
- **BDN `[Benchmark]` for concurrent establish** — rejected: distributions and loss
  belong to the soak runner; BDN already covers ordered populate
  (`PopulateSessionsAsync`).
- **Chaos dial-failure modes in the harness server** — deferred: the failure shapes
  (tombstone/cooldown) are already unit-tested in product tests; adding server-side
  faults would grow this task without changing the latency story. Can follow up if the
  baseline matrix shows failure-path artifacts.
