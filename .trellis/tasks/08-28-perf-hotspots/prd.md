# PRD — Performance Hotspot Diagnosis & Optimization

## Background

WinForward intercepts every packet on captured adapters (NDIS). The managed
pipeline from capture to disposition runs per packet, so its per-packet CPU cost
and allocation rate directly cap throughput and add latency/jitter for all
proxied and passed traffic.

A full benchmark baseline was captured on the dev machine
(`bench-baseline-linux.json`, .NET 10, Release, managed-only pipeline):

| Scenario | Cost | Allocation | Signal |
|---|---|---|---|
| capturePump.endToEnd (1400B) | ~500 ns/pkt, ~2.0 M pps | **669 B/pkt** | 1.3 GB/s alloc at 2M pps; gen0 every ~28k pkts |
| dispatcher.warmPass.disabledTrace | 1268 ns/op | **672 B/op** | warm-path alloc confirmed in dispatcher alone |
| parser.ipv4Udp | 72–111 ns | **80 B/op** | parse path allocates (2× `IPAddress`) despite doc claiming zero-alloc |
| flowTable.resolveCrossAdapterHit @65k | 763 ns | 19.5 B/op | hash/equals over `IPAddress` objects; shared lock |
| socks5Udp.encode (1472B payload) | 293 ns | **1512 B/op** | output buffer allocated per datagram |
| ndisBuffer.reuseSet (1514B) | 186 ns | ~0 | per-frame copies at ABI boundary |

Code-level attribution of the ~670 B/packet (warm pass path):

1. `new PacketLease(frame)` — class, per packet (`CapturePacketProcessor`).
2. `ArrayPool.Rent` + `frameSpan.CopyTo` — 1 managed copy per packet before
   disposition is even known; only the async proxy path needs a detached copy.
3. `new CapturedFlowPacket(...)` record class + `packet with { FlowGeneration }`
   — **two** class allocations per warm packet (`FlowDispatcher.DispatchAsync`).
4. `CompleteAsync(..., () => executor.XXXAsync(packet, ct))` — closure + delegate
   per packet.
5. `new FlowContext(...)` record class per packet (`PacketFlowClassifier`).
6. `new IPAddress(bytes)` ×2 per packet in `IpTcpUdpPacket.TryParse` /
   `PacketFlowClassifier` (IPAddress is a class; `PacketView` carries them).
7. Pass path copies the frame again into a pooled native buffer
   (`NdisPacketActionExecutor.PassAsync` → `SetFrame`) even though the original
   native capture buffer still holds the unmodified frame and reinjection is
   synchronous within the pump's per-packet handler await.

## Goal

Reduce steady-state hot-path cost so the managed pipeline stops being the
bottleneck: fewer per-packet heap allocations, fewer frame copies, cheaper flow
lookups. The user explicitly allows C++-style code (spans, `unsafe`, raw
addresses, structs over classes) on the hot path.

## Requirements

- **R1 Zero/low-alloc warm path**: warm resolved pass/block packets allocate
  ≤ 64 B on average (vs ~670 B today), measured by the benchmark harness
  (`capturePump.endToEnd`, `dispatcher.warmPass`).
- **R2 Fewer copies for pass**: an unmodified pass packet performs no managed
  frame copy and no copy-back into a second native buffer (in-place reinjection
  from the capture buffer), with the pump's lifetime rules documented and
  enforced. Proxy paths may still copy (they rewrite/relay asynchronously).
- **R3 Cheap flow keys**: `Endpoint` stores the address as a fixed-size value
  (no `IPAddress` object) so parse, classify, hash, and equals are allocation
  free; flow table resolve at 65k flows ≤ 300 ns/op (vs 763 ns) on the bench.
- **R4 SOCKS5 UDP encode**: an allocation-free (span-writing) encode path for
  datagrams ≤ MTU; relay send path uses it.
- **R5 No behavior change**: policy decisions, reinjection directions, log
   semantics, fail-closed behavior, and public CLI behavior unchanged; all
   existing tests pass unchanged (test-side compatibility API kept, e.g.
   `Endpoint.From(IPAddress, port)`).
- **R6 Measured improvement**: full benchmark suite before/after with the
   harness's own counters; report ns/op, B/op, gen0 per million packets.

## Non-goals

- Native/kernel-side costs (NDISAPI transitions, driver copies) — out of scope
  except avoiding redundant native→managed→native round-trips we control.
- Configuration parsing, startup path, logging output format.
- TCP relay throughput (already ~7 GB/s loopback, ~0 alloc/op).

## Acceptance criteria

- [x] `dotnet test` green on all existing tests.
- [x] Benchmarks after optimization show: capturePump warm allocations ≤ 64 B/pkt
      (measured 10 B/pkt; the dispatcher.warmPass scenario's 160 B/op is the harness's own
      per-iteration `new PacketLease(new byte[64])` construction), ns/op improvement ≥ 20%
      on 1400B steady state (+44–53% pps), socks5Udp.encode 0 B/op for ≤1514 B datagrams
      (encodeSpan scenario: 4.2 B/op, archived in bench-encodespan-linux.json).
- [ ] flowTable 65k-hit ≤ 300 ns/op — NOT MET: 801 ns (baseline 763; briefly 1338 before
      hash tuning). Keys are allocation-free and hashing was tuned; the remaining cost is
      cache misses probing two dictionaries. Deferred: single-table redesign (report.md).
- [x] Safety argument for in-place reinjection written in design.md and encoded
      in tests (buffer reuse ordering enforced by the pump's handler-await
      contract).
- [x] Baseline and after benchmark JSONs stored in the task directory.
