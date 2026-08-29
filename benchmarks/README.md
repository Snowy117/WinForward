# WinForward benchmarks

Two measurement modes in one project (`benchmarks/WinForward.Benchmarks`), both managed-only and
loopback — they never exercise WinpkFilter, NDISAPI, Windows IP Helper, or ETW, and run on any OS:

| Mode | Engine | What it answers |
|---|---|---|
| Perf | [BenchmarkDotNet](https://benchmarkdotnet.org) 0.15.8 | how fast / how allocating is the managed hot path |
| Stability | custom soak runner | how reliable is the proxy pipeline under sustained load |

Numbers produced before 2026-08-29 (the hand-rolled JSONL harness) are **not comparable** with
BenchmarkDotNet output — different methodology, different statistics. The old baselines stay
archived under `.trellis/tasks/08-17-performance-hotspots/research/`.

## Perf mode

Everything except a leading `--stability` is passed straight to BenchmarkDotNet. Quote the
`*` filter — an unquoted `*` is glob-expanded by the shell into directory names and BenchmarkDotNet
will silently select zero benchmarks:

```text
# full matrix, default job
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*'

# quick validation sweep
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --job short

# one family
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter *Parser* --job short
```

Scenario families (classes under `Perf/`, each with `[MemoryDiagnoser]` except the relay where
socket buffers would mislead the allocation columns):

| Class | Covers | Sweep |
|---|---|---|
| `ParserBenchmarks` | IPv4/UDP frame parse, payload parse, SOCKS5 UDP decode/encode/span-encode | frame bytes 64 / 512 / 1514 |
| `NdisBufferBenchmarks` | `NdisPacketBuffer` allocate+set+dispose vs reuse | frame bytes 64 / 512 / 1514 |
| `FlowTableMissBenchmarks` / `FlowTableHitBenchmarks` | flow-table resolve miss / cross-adapter hit | cardinality 0–65 535 |
| `SelfTrafficBenchmarks` | self-traffic registry wildcard miss | cardinality 0–65 535 |
| `DispatcherBenchmarks` | warm-path dispatch (pass, trace off) | — |
| `CapturePumpBenchmarks` | end-to-end pump round: 200 000 synthetic packets through dispatcher + processor | frame 128/1400 × batch 32/1 |
| `UdpSessionBenchmarks` | UDP session populate + dispose cost | 1 / 100 / 1000 sessions |
| `TcpRelayBenchmarks` | one-way relay transfer (256 KiB–16 MiB) | chunk 1 / 1024 / 8192 / 65536 |

Interpretation guidance from the hot-path conventions still applies: treat ns/pps deltas under
~2× as noise on a dev box; allocation bytes and GC counts are the exact gates. Use the same
command, machine, power settings, and runtime for before/after comparisons.

## Stability mode

Count-based reliability metrics under sustained load. One JSONL record per scenario execution
(schemaVersion 2, camelCase), written to the console and optionally `--output`:

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability [--scenario all|udp|tcp|footprint|baseline] [--duration 60] [--pps 25000] \
  [--payload-bytes 512] [--flows 256] [--tcp-concurrency 64] [--tcp-transfer-bytes 1048576] \
  [--abort-mix clean=25,clientRst=25,relayCancel=25,upstreamTruncate=25] [--seed 42] \
  [--output <path>] [--quick]
```

`--quick` = `--duration 15 --pps 10000 --tcp-concurrency 16 --flows 64`.

Stability runs hold the Windows system timer at 1 ms resolution (`timeBeginPeriod(1)` through the
production `HighResolutionTimerScope`) for the whole run so the 10 ms pacing ticks fire on time;
non-Windows hosts are unaffected. Windows stability numbers from before 2026-08-29 were paced at
the ~15.6 ms default granularity and are **not comparable** with current rows — the same series
break the BenchmarkDotNet rewrite introduced for perf numbers.

The UDP scenarios broke series once more with the 2026-08-29 windowing change: `udp.lossRate`
and `udp.rawBaseline` now count only the steady-state window (warmup establishes every flow
first, per-flow sequence markers snapshot at window start, and the post-window drain still
credits in-window stragglers), so earlier rows — which included the flow-establishment and
teardown tails — are **not comparable** with current rows either.

### Scenarios

- **`udp.lossRate`** — drives the real dial path (`UdpProxyCoordinator` + real
  `Socks5UdpTransportFactory` + `Socks5ControlConnection`) against a harness loopback SOCKS5 UDP
  server. Sequenced datagrams across `--flows` flows at target `--pps`; the destination echo
  receiver tracks loss, reordering, and duplicates (64-entry per-flow sequence window). Metrics
  count only the steady-state window: warmup sends one datagram per flow and waits (≤10 s) until
  the destination has observed every flow, sequence markers snapshot at window start, and the
  2 s drain before teardown still credits in-window stragglers — establishment and teardown-tail
  loss is excluded by design. Reports `sentDatagrams`, `destinationReceived`, `lossRate`,
  `outOfOrder`, `duplicates`, `responsesInjected` (response path through the counting sink),
  `achievedPps`, `sendLoopOverflows`.
- **`udp.rawBaseline`** — the bare OS + runtime loopback UDP ceiling, built from the exact
  `udp.lossRate` socket topology minus all WinForward product code (no coordinator, session, or
  SOCKS5 codec): one paced sender round-robining over per-flow client sockets (512 KiB), one
  dedicated forwarder socket per flow playing the SOCKS5 relay role (4 MiB, matching the harness
  relay), and a single echo destination (16 MiB) tracking loss/reordering/duplicates with the
  same 64-entry per-flow
  sequence window; it shares `udp.lossRate`'s warmup/window semantics so both rows count the
  same steady-state shape. Its `achievedPps` is the environment baseline (B_linux / B_windows)
  that acceptance comparisons compute product overhead from; `responsesInjected` counts the
  in-window datagrams that made it back to the client sockets.
- **`tcp.unexpectedEof`** — concurrent one-way transfers through `TcpProxyRelay` with an
  adversarial event fired mid-stream per transfer (weighted mix: clean / client RST / relay
  cancellation / upstream truncation at a random 20–80 % of the transfer). Receiver-side
  classification: `completed`, `unexpectedEof` (FIN before completion — the smoke-log failure
  shape), `resets`, `otherErrors`, plus `bytesCompleted` and per-transfer mean latency.
- **`udp.sessionFootprint`** — live-footprint measurement at 1 / 100 / 1000 UDP sessions using
  fake transports: `workingSetDeltaBytes`, `gen0Collections`, `allocatedBytes` captured with the
  session pool live (before disposal). This is the old `udpSessions.active` metric; a
  working-set level is not a time distribution, so it lives here rather than in BenchmarkDotNet.

A nonzero loss rate or an EOF count under an adversarial mix is an observation, not a harness
failure — the numbers become meaningful as a comparison series across builds. For UDP, loss
before the OS saturates (e.g. >0 at `--pps 50000`) indicates socket-buffer pressure worth
investigating.
