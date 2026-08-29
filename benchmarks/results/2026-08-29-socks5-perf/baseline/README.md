# SOCKS5 Full-Path Baseline (Phase B)

Date: 2026-08-29 · Linux Ubuntu 24.04, Ryzen 9 9955HX · .NET 10.0.10 · BenchmarkDotNet 0.15.8 ShortRun
Full matrix: 54 benchmarks green (`PERF_EXIT=0`), stability quick all scenarios green (`STAB_EXIT=0`).
Raw artifacts: `*-report-github.md` / `*-report.csv` (perf) and `stability-quick.jsonl` (soak) in this directory.

## New SOCKS5-path baselines (the numbers this task exists for)

| Benchmark | Result | Note |
|---|---|---|
| FrameRewriter ClassifyTcpSyn | 39–41 ns, 0 B | size-independent |
| FrameRewriter SwapEthernetMacs | 7 ns, **0 B** | `new byte[6]` elided by JIT escape analysis; C1 reduced to source-level explicitness |
| FrameRewriter TryRewriteForwardLeg **HostShape** | 75.7 ns@128 / 616.7 ns@1400, 0 B | |
| FrameRewriter TryRewriteForwardLeg **ForwardedShape** | 250 ns@128 / **2,887.9 ns@1400**, 0 B | **4.7× slower than host shape @1400**; suspect `IPAddressValue.From(IPAddress)` per packet (`TcpRedirectTable.cs:55` stores `IPAddress?`, converted per call at `TcpFrameRewriter.cs:25`) |
| Socks5Handshake ConnectAndDestinationAsync | **1.131 ms, 70.61 KB** per connection | loopback, no-auth; baseline only (no hard optimization this round per Q1 decision) |
| Dispatcher WarmProxyDisabledTraceAsync | **596.4 ns, 352 B** | vs WarmPass 226.4 ns / 160 B → **+370 ns / +192 B per packet**; cause: Proxy falls into `DispatchSlowAsync` async state machine (sync fast path covers Pass/Block only, `FlowDispatcher.cs:247-277`) |
| UdpSession PopulateSessionsAsync (real transport, await-ready) | 1.797 ms/90 KB @1 · 28.8 ms/9.0 MB @100 · 310.4 ms/92.2 MB @1000 | **time scaling 10.8× @10× sessions = linear** (old 29× superlinearity was a Noop-queueing artifact, see below) |
| soak tcp.throughput (16 conc × 1 MiB, echo) | **137.8 MB/s one-way, 31,282 transfers, 0 failed**, handshake mean 1.41 ms | `bytesVerified == serverBytesEchoed` cross-check ok |
| soak udp.lossRate / udp.rawBaseline | lossRate 0 (150,014/150,014) | regression guard stays green |

## R1.4 finding: the old 29× superlinearity is gone — and was a measurement artifact

- Old Noop benchmark measured only *enqueue* (fire-and-forget `Task.Run` setup, payload copy into setup queue); at 1000 sessions GC pressure (Gen2 125) + scheduler backlog inflated time 29×.
- With the real `Socks5UdpTransport` + await-ready semantics, time scales 10.8× for 10× sessions (linear).
- Remaining cost: **~90 KB allocated per session** (real socket path: control TCP connect + UDP ASSOCIATE + transport sockets + slot/setup queue). The PRD's original "≤1 KB per session" target was defined against the Noop bookkeeping baseline (5.2 KB) and must be re-scoped: C3 splits the 90 KB into product-controllable bookkeeping (slot, setup queue, Task/TCS, MAC copy) vs framework socket cost, and drives the product share down; the framework share is out of scope.

## B3: the 70% throughput denominator is redefined

- Original anchor `TcpRelay OneWayAsync` ≈ 0.94 GB/s is a **single connection, 16 MiB one-way, no handshake** number; `tcp.throughput` is **16 concurrent echo transfers with a fresh SOCKS5 handshake per transfer** — the two are not comparable (soak is also CPU-saturated: 32 relay pumps + 16 echo handlers + 16 workers in one process).
- Handshake alone accounts for 1.41 ms of the 9.0 ms per-transfer cycle (~16%).
- **Decision**: Phase C4 adds a `bare` control mode to `TcpThroughputScenario` itself (same workers/echo/transfer size, relay leg wired with a plain socket pair instead of `EstablishAsync`). The 70% acceptance ratio is computed as `throughputMBs(socks5) / throughputMBs(bare)` — measuring exactly "what the SOCKS5 layer costs" as the user intended. `TcpRelay OneWayAsync` remains the theoretical single-connection ceiling reference.

## Quantified hot-spot list for Phase C

1. **C2** ForwardedShape rewrite 2.9 µs@1400 (host shape 0.62 µs): per-packet `IPAddressValue.From(IPAddress)` — convert once at table creation (`TcpRedirectSetup.cs:119` cold edge), store `IPAddressValue?`.
2. **C2b** WarmProxy +192 B/+370 ns per packet: bring `FlowAction.Proxy` into the dispatcher's sync fast path (main-path parity with Pass per product positioning).
3. **C1** `SwapEthernetMacs`: replace `new byte[6]` with explicit stack-local swap (already 0 B under JIT today; source-level guarantee against future regressions).
4. **C3** UDP session cold path: split 90 KB/session into product vs framework share; product share target ≤1 KB. Watch the ~5 captured exceptions per session (BDN Exceptions column 5/498/5000, exactly linear) — likely WouldBlock fallback or teardown race in the fake-server path; classify as harness artifact vs product defect.

## Environment note

High-priority process setting was denied (`Failed to set up high priority (Permission denied)`) — same posture as all previous runs on this host; ns-level comparisons stay within ±noise per hot-path.md contract 9.
