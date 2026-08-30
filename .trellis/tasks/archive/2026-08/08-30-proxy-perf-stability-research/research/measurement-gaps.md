# Research: Measurement Infrastructure & Known Gaps (2026-08-30)

- **Query**: Read-only review of the benchmark/stability infrastructure, result artifacts, and residual/future-work items recorded in archived task docs.
- **Scope**: all files under `benchmarks/WinForward.Benchmarks/` (Perf + Stability), `smoke/`, `benchmarks/results/*` summaries, archived task docs under `.trellis/tasks/archive/2026-08/`.
- **Date**: 2026-08-30.

## A) Measurement Inventory

### A.1 Perf benchmarks (BenchmarkDotNet 0.15.8, `benchmarks/WinForward.Benchmarks/Perf/`; `Program.cs` passes non-`--stability` args through to BDN)

| Class | Measures | Included/excluded | Key params |
|---|---|---|---|
| `ParserBenchmarks.cs` | IPv4/UDP frame parsing (`IPTcpUdpPacket.TryParse`, `IPUdpPacket.TryParse`), SOCKS5 UDP decode/encode/TryEncodeSpan | IPv4 only; pure managed parsing, no sockets | 64/512/1514 B |
| `NdisBufferBenchmarks.cs` | `NdisPacketBuffer` new+set+dispose vs reuse | Managed struct only, no native NDIS calls | 64/512/1514 B |
| `FlowTableBenchmarks.cs` (Miss + Hit) | Flow-table TryResolve miss / cross-adapter hit | Table structure only, no session entities | cardinality 0–65,535 |
| `SelfTrafficBenchmarks.cs` | Self-traffic registry wildcard miss | Table only | 0–65,535 |
| `DispatcherBenchmarks.cs` | `WarmPassDisabledTraceAsync` + `WarmProxyDisabledTraceAsync` | Executor is a `CountingExecutor` no-op — dispatch bookkeeping only, **no real Proxy execution** | — |
| `CapturePumpBenchmarks.cs` | `EndToEndAsync`: 200k synthetic frames through NdisCapturePump→FlowDispatcher→CapturePacketProcessor | `FiniteCaptureReader` fake read side; **Pass config** (not proxy); no NDIS hardware, no reinjection, no real sockets | 128/1400 B × batch 32/1 |
| `TcpRelayBenchmarks.cs` | `OneWayAsync` bare socket pair through `TcpProxyRelay` one-way | No SOCKS5 handshake (pre-connected), one-way, loopback | chunk 1/1024/8192/65536, 256 KiB–16 MiB |
| `Socks5HandshakeBenchmarks.cs` | `ConnectAsync`+`ConnectDestinationAsync` full handshake against a loopback fake server | no-auth only, loopback RTT≈0; baseline-only (no optimization target) | — |
| `UdpSessionBenchmarks.cs` | `PopulateSessionsAsync` (real UDP ASSOCIATE + await-ready) + `PopulateSessionsNoopTransportAsync` (bookkeeping probe) | Cold-path session creation; no steady-state data plane | 1/100/1000 sessions |
| `FrameRewriterBenchmarks.cs` | ClassifyTcpSyn, SwapEthernetMacs, TryRewriteForwardLeg (host/forwarded shapes), TryRewriteEndpointsDirect | Pure-function rewrites, frames carry valid checksums (incremental-rewrite precondition) | 128/1400 B |
| `ChecksumBenchmarks.cs` | Scalar vs Vector256 internet checksum | Pure function | 64/512/1514 B |

### A.2 Stability scenarios (custom soak runner, `Stability/SoakRunner.cs`, JSONL schemaVersion 2; Windows holds a 1ms timer via `HighResolutionTimerScope` throughout)

| Scenario | Adversarial condition | Metrics (pass criteria) |
|---|---|---|
| `udp.lossRate` | Real dial path (UdpProxyCoordinator+Socks5UdpTransport+Socks5ControlConnection vs `LoopbackSocks5UdpServer`), 10ms tick pacing, steady-state window (warmup/teardown tails excluded) | lossRate==0 (>0 pre-saturation requires investigation), outOfOrder/duplicates, achievedPps, sendLoopOverflows, per-hop counts |
| `udp.rawBaseline` | Same topology minus all product code | Environment ceiling B_linux/B_windows; product/B ratio ≥0.7×, loss==0 @ T_zero |
| `tcp.unexpectedEof` | Random adversarial events mid-transfer (clean/clientRst/relayCancel/upstreamTruncate at 20–80% weighted mix) | Receiver-side classification completed/unexpectedEof/resets/otherErrors (otherErrors==0 is the implicit pass line) |
| `tcp.throughput` | socks5 vs bare dual mode, 16 concurrent × 1 MiB echo, fresh SOCKS5 handshake per transfer | socks5/bare ≥ 0.70; 0 failed; throughputMBs, meanHandshakeMicroseconds |
| `udp.sessionFootprint` | Fake-transport populate 1/100/1000 sessions, measured while pools alive | workingSetDeltaBytes, gen0Collections, allocatedBytes |

### A.3 smoke/ directory

`smoke/` contains only `ndisapi.dll` (git-ignored WinpkFilter user-mode sidecar DLL for real-machine smoke). The smoke *evidence* lives in results dirs: `benchmarks/results/2026-08-29-windows-real-machine/` (validate/adapters CLI, 30s all-pass run + ping zero loss + hard-kill recovery, real curl TLS/nslookup, 10MB×3 throughput, two product run logs) and `2026-08-29-socks5-perf/optimized/windows/` (udp-quick zero loss + `winforward-run.log` with full tcp.redirect lifecycle and an IPv6 TCP flow).

## B) Headline Numbers (post latest optimizations)

**Perf (Linux 9955HX, .NET 10.0.10, ShortRun)**
- Capture pump end-to-end: 200k packets in 65.1–69.6 ms ≈ **2.9–3.1M pps**, constant 1.86 MB/200k ≈ **9.3 B/packet** (Pass config)
- Dispatcher: WarmPass 222.3 ns / 160 B; WarmProxy 287.7 ns / **160 B** (baseline 596.4 ns / 352 B; C2b closed the gap)
- Frame rewrite: HostShape 29.5/49.7 ns, ForwardedShape 33.4/34.8 ns (@128/1400; baseline forwarded@1400 was 2,887.9 ns — 4.8×); EndpointsDirect 626→33 ns; all **0 B**
- Checksums: 1514 B 989→**61.7 ns** (16×); 64 B 39.3→4.9 ns
- TCP relay: chunk 8192 @16 MiB **14.73 ms ≈ 1.09 GB/s**; per-invocation 828→188 KB, per chunk ~312 B→**~5 B** (D1 CTS reuse)
- SOCKS5 handshake: **1.131–1.185 ms / 70.6 KB per connection** (loopback no-auth; unoptimized, baseline only)
- UDP sessions (real transport): 1→1.93 ms/89.6 KB, 100→26.3 ms/8.81 MB, 1000→**305.8 ms/89.0 MB (11.6×@10×, linear)**; bookkeeping probe 3.84 KB@100 (product share ~0.4 KB/session; framework sockets ~84 KB out of budget)

**Stability (quick=15s/10k pps/64 flows/16 concurrent, Linux)**
- `udp.lossRate`: **loss 0** (150,100/150,100, 0 duplicates); Linux 60s full-parameter: 24,998 pps (99.99%), dedicated machine loss **0**, 25k full target 1.5M/1.5M exact zero; Windows (Hyper-V VM): 4,833 pps = **100.1%** of raw baseline (4,826), loss 0, T_zero=3,866
- `tcp.unexpectedEof`: 90,523 transfers, completed 25.05% (exactly the clean weight), unexpectedEof 52.7%, resets 22.2%, **otherErrors 0**
- `tcp.throughput`: socks5 **130.9 vs bare 152.1 MB/s = 86.1%** (C4 round 93.9%), 30,755 transfers 0 failed, mean handshake 1.53–1.84 ms
- `udp.sessionFootprint`@1000: **5.34 MB** allocated (baseline 7.66 MB), 0–1 gen0, workingSetDelta **0**
- Windows real-machine proxy chain: LAN 10MB×3 **25–30 MB/s** (first run 11 MB/s cold)

## C) Measurement Gaps (not covered, but consequential for a SOCKS5 transparent proxy)

1. **Windows real-NDIS-path zero benchmarks**: CapturePump uses a fake reader (no WinpkFilter/reinjection); real machines only get functional smoke; the only Windows soak numbers come from a Hyper-V VM whose loopback ceiling (~4.8k pps) caps everything — pps and reinjection cost on real NIC/driver are unknown.
2. **End-to-end per-connection added latency**: no "via proxy vs direct" latency delta anywhere; handshake benchmark is loopback RTT≈0; stability reports means only, no p50/p99/p999 distributions.
3. **Concurrent-flow scale**: TCP concurrency tops out at 64 (quick 16), UDP flows ≤256; `tcpFlowCapacity` 4096 has never been stressed (FlowTable microbench to 65,535 is an empty structure, not live sessions).
4. **Real network conditions**: all loopback, zero RTT, zero loss, no reorder/retransmission pressure; the fake server always succeeds (no auth failure, no CONNECT refusal, no server-side delay/degradation injection).
5. **Upstream failure recovery**: rebuild/backoff latency after mid-run SOCKS5 server death is unmeasured (SIO_UDP_CONNRESET fixed behavior; recovery time has no number); `RelayConnectMaxAttempts` retry path uncovered.
6. **Long soaks**: longest run is 60s (quick 15s); hours-scale memory drift, handle leaks, tombstone accumulation under churn cycles unmeasured (footprint covers one-shot populate only).
7. **IPv6 data plane**: benchmark frames are all IPv4; IPv6 incremental checksum has property-test equivalence only, no perf benchmark; functional coverage exists via real-machine smoke only (`winforward-run.log` fd00→2001:2::379 flow).
8. **DNS-shaped traffic**: small packets, high churn, few datagrams per flow (DNS is the canonical UDP-proxy load) has no dedicated scenario (payload floor 12 B, flows ≤256).
9. **MTU/fragmentation/PMTUD**: after S1 changed to consume+RST, real-world trigger frequency and client impact are unquantified; rewrite correctness under retransmission has no benchmark.
10. **Reverse-leg reinjection**: `IUdpResponseSink` is a no-op in benchmarks; TCP reverse (client-direction) reinjection latency is in no soak.
11. **GC pauses**: allocations are precisely metered, but GC pause time under load is not measured.
12. **Handshake auth variants**: no-auth only; username/password round trip has no benchmark.

## D) Recorded Residuals in Task Docs (quotes)

- `.trellis/tasks/archive/2026-08/08-29-proxy-stability-perf/prd.md` (Out of Scope): "Windows BDN perf matrix run and hours-long soaks (recorded as follow-ups)"; "IP fragment reassembly and SOCKS5 UDP FRAG support (policy unchanged: fail-closed)"; "Wildcard-listener spoof hardening beyond a documented decision (residual risk accepted…)". S1–S6/P1–P2 bodies are all fixed and landed (`2026-08-29-proxy-hardening/README.md`: D1/D2/D3 landed, 431/431 tests).
- `.trellis/tasks/archive/2026-08/08-29-udp-throughput-loss/prd.md:83`: "TCP `WSAEADDRINUSE` under adversarial churn (separate follow-up task)".
- `benchmarks/results/2026-08-29-windows-real-machine/README.md` (open issues): "TCP 高频换联撞端口池：64 并发持续换联在 Windows 上产生 8.2% `WSAEADDRINUSE`"; "未验证项：真实控制台 Ctrl+C 优雅停机…；小时级长浸泡；AOT 发布产物真机验证".
- `.trellis/tasks/archive/2026-08/08-29-socks5-perf-fullpath/prd.md` (Out of Scope): Windows real-machine NDIS reinjection-path benchmarking (real machine got functional smoke only); the connect path was "benchmarked to a baseline only, no hard optimization this round (user decision 2026-08-29)" → the 70.6 KB/connection handshake allocation remains a known unoptimized item.
- Same task's `optimized/README.md` methodology finding: an upstream fake-IP router invalidates "egress IP" proofs — future smoke must use the SOCKS5-server-side connection table.
- `benchmarks/README.md`: three comparability breaks recorded (pre-2026-08-29 hand-rolled JSONL, the Windows 15.6ms timer era, the UDP windowing change).

## E) Risk Ranking Assessment

1. **Windows real-machine data-plane performance (highest risk)**: the product is a Windows transparent proxy, but all perf numbers are Linux managed-only or Windows numbers capped by a VM loopback ceiling (4.8k pps); the known Windows send-path 3.5× divergence (recorded in `hot-path.md` contract 3) shows platform behavior splits sharply — NDIS batching, reinjection cost, and behavior under real-NIC interrupt moderation are all extrapolated. This is the single layer that could turn "3M pps on Linux" into a completely different story on a real machine.
2. **Concurrency scale & port budget**: 8.2% WSAEADDRINUSE at only 64 concurrent; real desktop browsing easily exceeds that; the 4096 capacity ceiling has never been pushed. User-perceivable connection-failure risk, already named as a follow-up in two task docs and still not planned.
3. **Upstream realism & failure-recovery latency**: the fake server's zero-RTT always-succeed behavior hides handshake retry/backoff, auth failure, and server-death recovery behavior; per-connection added latency (the most user-visible metric) has zero numbers.
4. **Hours-scale soaks**: 60 seconds cannot demonstrate memory drift, handle leaks, or tombstone accumulation under churn cycles — exactly the class of issues S-3/S-2-type findings describe; the footprint's 0 workingSetDelta covers static populate only.
5. **IPv6 data-plane performance**: functionality exists, numbers do not — if the IPv6 rewrite path has the same hotspots IPv4 had fixed (e.g. the former per-packet conversion), the current benchmark net cannot catch it.
6. **Missing tail-latency distributions**: all stability metrics are means/counts; interactive proxy traffic (web first byte) is p99-sensitive, and means systematically hide it.
7. **Fragmentation/PMTUD trigger rate**: behavior is fail-closed and test-pinned, but real-network frequency is unknown — medium risk since correctness is guaranteed and only experience remains.

**Overall impression**: the Linux managed sub-chain has excellent measurement discipline (allocation-as-gate, baselines on disk, honest comparability breaks), but the whole measurement system stops at the comfortable origin on the "target platform × real network × scale × time" axes — and those four axes are exactly where transparent proxies actually fail in production.
