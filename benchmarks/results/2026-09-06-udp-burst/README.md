# 2026-09-06 UDP flow-establishment burst baseline

First baseline matrix for the `udp.burstEstablishment` scenario (task
`09-06-udp-burst-establishment`, parent `08-30-proxy-perf-stability`), measuring the
reported issue shape: dozens of new UDP flows appearing at the same instant (application
startup / DNS wave) while pre-established flows keep running.

## Environment

| | |
|---|---|
| System | Ubuntu 24.04.4 LTS (dev VM) |
| Runtime | .NET 10.0.11, X64 |
| Command | `--stability --scenario udpBurst --flows 16 --pps 4000 --payload-bytes 512 --seed 42` |
| Matrix | bursts 8/16/32/48/64/128 × dial delays 0/25/100 ms + TTL probe 128 × 4000 ms |

`--dial-delay-ms` delays the harness SOCKS5 server's UDP-ASSOCIATE reply, modeling the
remote dial latency that loopback's sub-millisecond dial hides. Background load
(16 flows × 4k pps) stays under the Windows ~4.7k pps loopback ceiling so the same
invocation ports to a Windows guest.

## Burst first-response latency (all flows admitted, zero establishment loss except probe)

| N \ delay | 0 ms p50 → max | 25 ms p50 → max | 100 ms p50 → max | model 100 ms |
|---|---|---|---|---|
| 8 | 3.0 → 3.7 | 27.6 → 27.9 | 103.1 → 104.2 | 100 (+4.2%) |
| 16 | 3.1 → 4.4 | 29.0 → 55.0 | 106.2 → 208.0 | 200 (+4.0%) |
| 32 | 8.1 → 17.1 | 53.4 → 116.4 | 208.9 → 417.3 | 400 (+4.3%) |
| 48 | 8.8 → 29.2 | 81.7 → 167.2 | 309.0 → 624.7 | 600 (+4.1%) |
| 64 | 19.6 → 23.6 | 116.0 → 221.7 | 423.3 → 828.7 | 800 (+3.6%) |
| 128 | 27.8 → 44.3 | 222.1 → 444.9 | 827.1 → 1646.7 | 1600 (+2.9%) |

Model: last flow's first response ≈ `ceil(N/8) × dial + ε` where the 8 is the
coordinator's setup-limiter width. At 100 ms the fit is +2.9–4.3% across the whole
matrix (ε = per-flow handshake + pacing); at 25 ms the fixed ε is relatively larger
(+10–16%). Harness cost is negligible: `timeToIssueMs` ≤ 0.5 ms even at 128 flows
(the sequential issue loop — the serialized pump shape — is not the bottleneck).

**Conclusion 1 (performance):** establishment latency is serialized by the 8-wide
setup limiter, linear in burst size and dial latency. A 48-flow DNS wave against a
100 ms-dial proxy waits up to ~625 ms for the last answer; against a 250 ms dial
(extrapolated) ~1.5 s — DNS-client timeout territory. The coordinator gate, flow
table, and allocation paths contribute nothing measurable at this scale.

## Background (head-of-line) immunity

Every matrix point: background loss 0 in all three windows (control / burst / post),
`sendP95Ms` < 0.1 ms in the burst window (vs < 0.1 ms control), achievedPps ≈ 4000 in
all windows. Even the 128 × 4000 ms probe — 192 s of serialized dialing with 768k
background datagrams in its burst window — shows zero background loss and
`sendP95Ms` 0.053 ms.

**Conclusion 2 (compatibility, positive):** the establishment burst does not disturb
established flows at all. No measurable coordinator-gate contention, no head-of-line
blocking, no pacing degradation. The P4 "single global gate" concern from the
08-30 research is exonerated at this scale/shape.

## TTL probe: triggering-datagram loss against a slow server (128 × 4000 ms)

`burstAccepted` 128, `firstResponses` **8**, `establishmentLossRate` **93.75%**, the
8 survivors all at ~4002 ms. Mechanism: wave k's dial starts at (k−1)×D and completes
at k×D; its triggering datagram (enqueued at t≈0) is k×D old at flush. The 5 s setup
datagram TTL drops every wave with k×D > 5 s — at D=4 s only wave 1 (k=1, 4 s) fits.
All 128 sessions still establish (the setups themselves complete and later datagrams
would flow), but the first query of every flow beyond wave 1 is silently dropped.

Generalized loss threshold: first-datagram loss begins at wave `floor(TTL / D) + 1`,
i.e. a burst loses triggering datagrams when `N > 8 × floor(5 s / D)`. Real-world
shapes: D=500 ms → N > 80; D=1 s → N > 40 (a 48-flow burst loses 8); D=250 ms →
N > 160 (safe). Slow-ish remote proxies + app-startup bursts do cross the line.

**Conclusion 3 (compatibility, negative):** this is the mechanism behind the reported
issue — the serialization × TTL interaction, not lock contention or queue overflow
(the 8 MiB budget was never approached; `unattributedResponses` 0 everywhere).

## Verdict and follow-up recommendations

No product defect in the fast-server regime (delay 0: 128 flows in 44 ms, zero loss,
zero disturbance). The failure mode is structural: per-flow SOCKS5 dialing serializes
8-wide, and the TTL correctly refuses to deliver datagrams that waited out the
serialization. Ranked follow-up candidates for the parent backlog:

1. **TTL attribution change (small, targeted)**: age the triggering datagram from its
   dial start (or reset its stamp when its flow's setup leaves the limiter queue)
   rather than from enqueue — queue-wait under the limiter is not client-side
   staleness. Trades a bounded amount of held memory for first-datagram delivery.
2. **`local-mux-transport` (existing backlog, structural fix)**: fixed connection
   pool + multiplexing removes the per-flow dial entirely, collapsing the wave
   latency from `ceil(N/8)×D` to ~`D` and eliminating the TTL interaction.
3. **Adaptive limiter width (medium)**: widen setup concurrency when the pending-setup
   population approaches the TTL horizon. Protects the OS/server less simply than 1/2.
4. Windows-guest replication of this matrix (recommended invocation ports directly);
   extends the windows-reality program.

Do **not** chase the coordinator gate or allocation paths for this issue — the matrix
exonerates both.

## Files

- `burst{8,16,32,48,64,128}-delay{0,25,100}.jsonl` — matrix rows (one result record each)
- `probe-ttl-burst128-delay4000.jsonl` — the TTL-loss corner probe

## Repro

```text
for d in 0 25 100; do for n in 8 16 32 48 64 128; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario udpBurst --burst-flows $n --dial-delay-ms $d \
    --flows 16 --pps 4000 --payload-bytes 512 --seed 42 \
    --output benchmarks/results/2026-09-06-udp-burst/burst${n}-delay${d}.jsonl
done; done
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 128 --dial-delay-ms 4000 \
  --flows 16 --pps 4000 --payload-bytes 512 --seed 42 \
  --output benchmarks/results/2026-09-06-udp-burst/probe-ttl-burst128-delay4000.jsonl
```
