# 2026-09-06 UDP burst TTL re-attribution — acceptance matrix (post-fix)

Task `09-06-udp-burst-ttl-attribution` (parent `08-30-proxy-perf-stability`). Compares
against the pre-fix baseline in `../2026-09-06-udp-burst/` (same machine, seed, and
invocation: `--flows 16 --pps 4000 --payload-bytes 512 --seed 42`).

Fix under test: `BoundedSetupQueue.RefreshEnqueuedStamps` re-stamps a slot's queued
datagrams when its setup leaves the 8-wide `_setupLimiter` (dial start), so the 5 s
setup datagram TTL measures dial age instead of enqueue age. Budget accounting
(charge/credit exactly-once), slot bounds, and tombstones are untouched.

## Before / after

| Point (N × delay) | firstResponses | lossRate | p50 (ms) | max (ms) | Background loss (after) |
|---|---|---|---|---|---|
| 8 × 100 | 8 → 8 | 0 → 0 | 103.1 → 102.5 | 104.2 → 102.7 | 0 |
| 48 × 100 | 48 → 48 | 0 → 0 | 309.0 → 325.0 | 624.7 → 628.4 | 0 |
| 128 × 100 | 128 → 128 | 0 → 0 | 827.1 → 828.8 | 1646.7 → 1651.2 | 0 |
| **128 × 4000 (probe)** | **8 → 128** | **0.9375 → 0** | 4002.4 → 32024.0 | 4002.7 → 64037.5 | 0 |

## Interpretation

- **Probe (the acceptance gate)**: every wave's triggering datagram now survives its
  limiter queue-wait and is delivered when its dial completes; `timeToLast` = 64 037 ms
  matches the 16-wave × 4 s model exactly (each flow responds ~4 s after its own dial
  start). Pre-fix only wave 1 (8 flows) survived.
- **Latency shape unchanged**: 8/48/128 × 100 ms points are within run-to-run noise of
  the baseline (establishment still serializes `ceil(N/8) × D` — the re-attribution
  changes *delivery*, not *timing*; wave collapse is the mux-transport task's goal).
- **No collateral**: background windows zero loss in all rows (burst window of the
  probe paced 256 239 datagrams over 64 s at ~4 000 pps, sub-0.05 ms send p95);
  `unattributedResponses` 0 everywhere.
- Trade (intended): first datagrams may now be held up to limiter-wait + 5 s instead of
  5 s absolute. Slot bounds (32 pkt / 32 KiB) and the 8 MiB global budget bound that
  retention; `SetupStampsRefreshedCount` diagnostics observe the re-stamps.

## Repro

```text
for n in 8 48 128; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario udpBurst --burst-flows $n --dial-delay-ms 100 \
    --flows 16 --pps 4000 --payload-bytes 512 --seed 42 \
    --output benchmarks/results/2026-09-06-udp-burst-ttl-fix/burst${n}-delay100.jsonl
done
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpBurst --burst-flows 128 --dial-delay-ms 4000 \
  --flows 16 --pps 4000 --payload-bytes 512 --seed 42 \
  --output benchmarks/results/2026-09-06-udp-burst-ttl-fix/probe-ttl-burst128-delay4000.jsonl
```

Unit coverage lives in `tests/WinForward.Core.Tests/UdpSetupQueueTests.cs`:
`LimiterQueueWaitDoesNotExpireTheTriggeringDatagram` (pins the new semantics,
mutation-verified) and the evolved `FlushDropsSetupDatagramsOlderThanTheTtl`
(dial-delay > TTL still drops).
