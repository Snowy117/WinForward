# PRD: UDP stability — close Windows/Linux throughput gap and eliminate loss

## Goal

Make the UDP proxy pipeline's stability numbers on Windows no longer catastrophically behind Linux,
and drive soak loss rate to zero at target rates ("像 TCP 一样打到零" — UDP has no retransmission,
so zero loss means the in-process pipeline plus kernel buffers never drop a datagram at the tested
rates).

User value: WinForward runs on Windows in production; today the same soak that sustains 22.4k pps
with 0.19% loss on Linux only reaches 6.9k pps with 2.47% loss on Windows, the actual target
platform. Users of UDP-heavy workloads (DNS, QUIC-style flows) would see throughput collapse and
datagram loss through the proxy.

## Background — measured baseline (2026-08-29, seed 42, 60s, 256 flows, 512B payload)

Source data: `benchmarks/results/2026-08-29-windows-real-machine/` (stability-linux-full.jsonl,
stability-windows-full.jsonl). Same binary, same scenario, same seed.

| Metric | Linux (Ryzen 9 9955HX) | Windows (Win11 IoT LTSC 26100) |
|---|---|---|
| achieved pps (target 25000) | 22391 (89.6%) | 6914 (27.7%) |
| lossRate | 0.19% | 2.47% |
| sendLoopOverflows (of ~6000 ticks) | 18 | 1952 |
| outOfOrder / duplicates | 0 / 0 | 0 / 0 |
| Windows quick run (target 10000) | — | 6144 achieved, 0.33% loss |

Key observation: the Windows ceiling is ~6-7k pps **independent of target rate** (6144 @ 10k target
vs 6914 @ 25k target) ⇒ a per-datagram cost bottleneck saturates the pipeline, not a pacing-ratio
problem. Loss (2.47%) is downstream queue/kernel-buffer overflow once saturated.

## Confirmed facts (code evidence)

Per-datagram pipeline in the soak (benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs):

- Sender loop is fully serialized: `await coordinator.TrySendAsync(...)` per datagram
  (UdpLossScenario.cs:83), paced by 10ms `Task.Delay` ticks (UdpLossScenario.cs:17,90).
- Product send path steady state:
  - `UdpProxyCoordinator.TrySendAsync`: global `lock (_gate)` + dictionary lookup
    (src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:88-133)
  - `UdpProxySession.SendAsync`: `lock (_activityGate)` ×2 + `TouchActivity` (lock + GetUtcNow +
    association-table touch) (src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:89-108,255-264)
  - `Socks5UdpTransport.SendAsync`: `SemaphoreSlim` gate + SOCKS5 encode + `await
    Socket.SendToAsync` (src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:161-183)
- Product receive path: per-session `ReceiveLoopAsync` → `await ReceiveFromAsync` + decode +
  `sink.InjectAsync` + TouchActivity (UdpProxySession.cs:151-210).
- Harness echo chain adds ≥2 more async socket hops per datagram
  (benchmarks/.../LoopbackSocks5UdpServer.cs:195-256, UdpLossScenario.cs:138-199).
- Total per datagram: ≥4 async socket operations, ~10 lock acquisitions, 2 SOCKS5 codec ops.
- 256 flows ⇒ 256 SOCKS5 control connections + 256 harness relay loops + 256 session receive
  loops + EchoReceiver + sender ≈ 515 concurrent task loops.
- Kernel buffers already enlarged: product relay socket 512 KiB (Socks5UdpTransport.cs:82,127),
  harness relay 1 MiB (LoopbackSocks5UdpServer.cs:113), EchoReceiver 4 MiB (UdpLossScenario.cs:118).
- Linux perf baselines show the managed hot path is zero-allocation (BenchmarkDotNet.Artifacts
  2026-08-29); no GC pressure in the soak (gen0=0 in footprint on both OSes).

## Hypotheses (verified/refuted by the design's baseline step)

- H1 (primary): per-await socket I/O completion cost on Windows (IOCP + thread-pool dispatch per
  async op; Linux completes UDP sendto inline). ≥4 such hops per datagram.
- H2 (secondary): `Task.Delay` 10ms pacing vs Windows ~15.6ms default timer granularity.
- H3: ambient Windows per-datagram loopback cost (Defender, WFP layers, ndisrd present).
- H4: the Windows box hardware ceiling is unmeasured (possibly a VM) — quantified by the raw
  baseline measurement, not assumed away.

## Requirements

- R1 (diagnose): measure a bare-socket loopback UDP baseline (`udp.rawBaseline` stability
  scenario, same topology/pacing/metrics, no product code) on both OSes, so product overhead is
  separable from the environment ceiling.
- R2 (throughput, baseline-relative — user decision 2026-08-29): Windows `udp.lossRate` achieved
  pps at default parameters ≥ 70% of the Windows `udp.rawBaseline` achieved pps.
- R3 (loss): zero loss at non-saturated rates on both OSes: with post-fix ceiling `C_os` (achieved
  pps at the 25k default run), the run at target `T_zero = 25000 if C_os ≥ 25000 else ⌊0.8 × C_os⌋`
  reports `lossRate == 0`, `outOfOrder == 0`, `duplicates == 0`.
- R4 (no regression): Linux `C_linux ≥ 22000` and default-run lossRate ≤ 0.19%; TCP scenario
  unaffected; all existing unit tests green.
- R5 (allocation discipline, hot-path spec): the steady-state send path stays zero-allocation
  (perf benchmark allocation gates unchanged); no new per-datagram allocations on warm paths.

## Out of scope

- TCP `WSAEADDRINUSE` under adversarial churn (separate follow-up task).
- Real-NIC / driver-path throughput work (loopback soak only).
- SOCKS5 protocol or configuration schema changes.
- Long-duration (hours) soak, graceful-shutdown verification on real hardware (separate task).
- Receive-side batching (recvmmsg-class) — revisited only if acceptance fails (design.md D5).

## Acceptance criteria

- [x] A1: Windows `udp.lossRate` (default 60s/25k/seed 42) achieved pps ≥ 0.7 × Windows
      `udp.rawBaseline` achieved pps (same parameters).
      → post-fix: 4833 vs 4826 (100.1%), `benchmarks/results/2026-08-29-udp-fix/`.
- [x] A2: on both OSes, `udp.lossRate` at target `T_zero` (R3 formula) reports lossRate == 0,
      outOfOrder == 0, duplicates == 0.
      → Linux @20000 (T_zero) and @25000 (full target): exact 0/1.2M and 0/1.5M;
      Windows @3866 (T_zero): exact 0/231534.
- [x] A3: Linux default run achieved pps ≥ 22000 and lossRate ≤ 0.19%.
      → 24997 achieved, 0.063% (full-matrix run with concurrent publish), 0% dedicated.
- [x] A4: `dotnet test -c Release` green (386/386); affected perf suites' allocation columns
      unchanged (1/100-session points identical; steady-state send zero-alloc, reflection-locked).
- [x] Windows stability series discontinuity (pacing fix) documented in `benchmarks/README.md`.
- [x] Baseline + acceptance JSONL runs archived under `benchmarks/results/2026-08-29-udp-fix/`.

## Decisions

- D-acceptance: baseline-relative bars chosen over absolute-pps and relative-improvement bars
  (user, 2026-08-29): the Windows box's raw ceiling is unknown, so acceptance must not depend on
  hardware guesses; "product is not the bottleneck" is the defensible claim.
