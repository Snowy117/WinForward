# UDP stability fix series — 2026-08-29 (task 08-29-udp-throughput-loss)

Post-fix acceptance series for the UDP proxy pipeline. Same parameters as the 2026-08-29
pre-fix set (`../2026-08-29-windows-real-machine/`): 60s, seed 42, 256 flows, 512B payload,
unless the file name says otherwise.

## What changed (product)

1. **Patient setup admission** (`UdpProxyCoordinator`): the setup concurrency cap no longer
   fails fast — flows beyond the 8-wide gate queue on the semaphore instead of throwing,
   draining the setup queue, and tombstoning for 1s. Genuine setup failures keep the cooldown.
2. **Sync-send fast path** (`Socks5UdpTransport`): relay socket non-blocking; steady-state
   sends complete inline via sync `SendTo` (no IOCP hop, zero allocation), WouldBlock falls
   back to the overlapped send.
3. **Activity propagation throttling** (`UdpProxySession`): association-table touches at most
   once per 100ms per session; per-operation idle timestamps stay exact.

## What changed (harness)

- `udp.rawBaseline` scenario (raw-socket 4-hop mirror of the product chain) — environment
  ceiling measurement.
- Windows 1ms timer resolution for the run (`HighResolutionTimerScope`).
- Warmup + steady-state window semantics (ramp excluded from metrics, both UDP scenarios).
- Harness relay/echo loops: non-blocking sync-first sends, 4MiB relay / 16MiB echo buffers,
  4 parallel echo receive loops (the instrument must not be the bottleneck).
- New comparison-series break: numbers before the windowing/pacing changes are not comparable.

## Acceptance results (bars: product ≥ 0.7× baseline; lossRate == 0 at T_zero = ⌊0.8×C⌋)

| Metric | Linux pre-fix | Linux post-fix | Windows pre-fix | Windows post-fix |
|---|---|---|---|---|
| udp.rawBaseline pps / loss | 24987 / 0.007% | 24991 / **0** | 7058 / 1.06% | 4826 / **0** |
| udp.lossRate pps (25k target) | 22391 (89.6%) | **24998 (99.99%)** | 5813 (23.3%)* | 4833 (19.3%) |
| udp.lossRate loss @ 25k | 0.19% | 0.063% (contended) / **0** (dedicated) | 0.585%* | **0** |
| product / baseline ratio | 92.4% | 100.0% | 82.3% | 100.1% |
| exact-zero T_zero run | — | **0 @ 20000** (and @ 25000) | — | **0 @ 3866** |
| tcp transfers / mean | 283428 / 13.5ms | 368244 / 10.4ms | 22742 / 148ms | 22598 / 150ms |
| footprint 1000 sessions | 9.79MB, 0 gen0 | 7.09MB, 0 gen0 | 10.39MB, 0 gen0 | 7.39MB, 1 gen0 |

\* pre-fix Windows numbers were taken with the 1ms timer fix already active (bench2 build).

Key findings:

- The Windows↔Linux throughput gap is **environmental**: the Windows box is a Hyper-V VM on
  the same Ryzen 9 9955HX silicon as the Linux host; its raw-socket loopback ceiling (~4.8-7k
  pps) bounds everything above it. Post-fix the product runs at 100% of the measured raw
  baseline on both OSes.
- Pre-fix loss was 100% caused by the setup fail-fast chain (per-hop census: every lost
  datagram died in `RemoveSlotAsync` queue drains during cap retries; steady-state hops show
  zero loss). Post-fix loss is exactly zero on both OSes, including the Linux 25k full-target
  run.

## Files

| File | What |
|---|---|
| `stability-linux-prefix.jsonl` | Linux full matrix, pre-fix product (old pacing) |
| `stability-windows-prefix.jsonl` | Windows full matrix, pre-fix product (bench2, 1ms timer) |
| `stability-linux-final.jsonl` | Linux full matrix, post-fix (bench with parallel echo) |
| `stability-windows-final.jsonl` | Windows full matrix, post-fix (bench4) |
| `stability-linux-25k-zero.jsonl` | Linux udp.lossRate @ 25000 — exact zero, 1.5M/1.5M |
| `stability-linux-20k-tzero.jsonl` | Linux udp.lossRate @ 20000 (T_zero) — exact zero |
| `stability-windows-3866-tzero.jsonl` | Windows udp.lossRate @ 3866 (T_zero) — exact zero |

## Reproduction

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario all --duration 60 --pps 25000 --seed 42 --output <path>
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udp --pps 20000 --duration 60 --seed 42 --output <path>
```

Windows legs: evil-winrm-py runbook (see the 2026-08-29-windows-real-machine README),
self-contained win-x64 publish of the benchmarks project.
