# Design — Benchmark rewrite (BenchmarkDotNet + stability soak runner)

## 1. Overall shape

One project, two modes, dispatched in `Program.cs`:

| Mode | Trigger | Engine | Output |
|---|---|---|---|
| Perf | no `--stability` prefix (all other args) | BenchmarkDotNet `BenchmarkSwitcher` | BDN artifacts (`BenchmarkDotNet.Artifacts/`, console tables) |
| Stability | first arg `--stability` | custom soak runner | JSONL (console + optional `--output` file) |

```csharp
// Program.cs (shape)
return args is ["--stability", .. var rest]
    ? await SoakRunner.RunAsync(SoakOptions.Parse(rest))
    : new BenchmarkSwitcher(typeof(Program).Assembly).Run(args) == SummaryValidator.ExitCodeOk ? 0 : 1;
```

No shared statistics code between the two modes. BDN owns timing/allocation science for perf;
the soak runner owns count-based reliability science for stability.

## 2. Perf mode (BenchmarkDotNet)

### 2.1 Conventions

- Package: `BenchmarkDotNet` 0.15.8 via `Directory.Packages.props` (CPM). First line with
  .NET 10 toolchain support (RuntimeMoniker.Net10).
- Every benchmark class: public, `[MemoryDiagnoser]` (except `TcpRelayBenchmarks`, where socket
  buffers dominate and allocation columns would mislead).
- Parameter sweeps via `[Params]`; defaults inherited from the old harness:
  frame sizes `{64, 512, 1514}`, cardinalities `{0, 1_000, 16_384, 65_535}`,
  relay chunks `{1, 1024, 8192, 65536}`, pump frames `{128, 1400}`, pump batches `{32, 1}`,
  UDP sessions `{1, 100, 1000}`.
- Quick validation: BDN's own `--job short` CLI flag replaces the old `--quick`.
- Dead-code elimination guards: per-class `private static long s_sink` + `Volatile.Write`
  (same pattern as the old harness).

### 2.2 Scenario mapping (old → new)

| Old scenario | New benchmark | Notes |
|---|---|---|
| `parser.ipv4Udp` / `parser.ipv4UdpPayload` | `ParserBenchmarks` `[Params] frameBytes` | frames pre-built in `GlobalSetup` |
| `socks5Udp.decode` / `.encode` / `.encodeSpan` | same class | encode caps (old `Min(count, 100k)`) dropped — BDN controls iteration count |
| `ndisBuffer.allocateSetDispose` / `.reuseSet` | `NdisBufferBenchmarks` | |
| `flowTable.resolveMissing` | `FlowTableMissBenchmarks` `[Params] cardinality {0,1k,16k,65k}` | table populated in `GlobalSetup` |
| `flowTable.resolveCrossAdapterHit` | `FlowTableHitBenchmarks` `[Params] {1k,16k,65k}` | separate class because cardinality 0 has no hit case |
| `selfTraffic.wildcardMiss` | `SelfTrafficBenchmarks` `[Params] cardinality` | registration tokens held for class lifetime |
| `dispatcher.warmPass.disabledTrace` | `DispatcherBenchmarks` | dispatcher built in `GlobalSetup` |
| `capturePump.endToEnd` | `CapturePumpBenchmarks` `[Params] frameBytes × batchCapacity` | 1 op = one round of 200,000 packets; fresh pipeline (dispatcher/pump/`FiniteCaptureReader`) per op **inside** the method, matching old per-invocation semantics |
| `capturePump.steadyState` (max-of-5-rounds + gen0/M derived record) | **deleted** | BDN's percentile/outlier statistics over many measured rounds supersede "max of 5 rounds"; Gen0 per op is a native BDN column |
| `udpSessions.active` (time + workingSetDelta) | `UdpSessionBenchmarks.PopulateSessions` measures populate cost (1 op = N sessions); the **live-footprint workingSetDelta measurement moves to the soak runner** (`udp.sessionFootprint`), because a working-set *level* under live rentals is not a time distribution and BDN has no honest column for it | |
| `tcpRelay.oneWay` | `TcpRelayBenchmarks` `[Params] chunkBytes` | 1 op = full transfer (256 KiB when chunk=1, else 16 MiB); socket pairs created per op, as before |

The old warmup/CPU-ramp floor (≥100k warmup packets for the pump) is dropped: BDN's
auto-scaling warmup phase serves the same purpose.

### 2.3 Deleted hand-rolled machinery

`BenchmarkContext` (Stopwatch sampling, manual GC.CollectionCount deltas, forced
`GC.Collect` baseline, `workingSetDeltaBytes` sampling, JSONL writer), `BenchmarkOptions`,
and the `S1215` pragma go away entirely. No dual paths.

## 3. Stability mode (soak runner)

### 3.1 CLI

```
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability [--scenario all|udp|tcp|footprint] [--duration 60] [--pps 25000] \
  [--payload-bytes 512] [--flows 256] [--tcp-concurrency 64] [--tcp-transfer-bytes 1048576] \
  [--abort-mix clean=25,clientRst=25,relayCancel=25,upstreamTruncate=25] [--seed 42] \
  [--output <path>] [--quick]
```

`--quick` = `--duration 15 --pps 10000 --tcp-concurrency 16 --flows 64`.

### 3.2 JSONL schema (stability, schemaVersion 2)

```jsonc
// one metadata record first
{ "type": "metadata", "schemaVersion": 2, "mode": "stability", "managedOnly": true,
  "timestampUtc": "...", "runtime": "...", "os": "...", "architecture": "...",
  "options": { /* full SoakOptions echo */ } }
// then one record per scenario execution
{ "type": "result", "scenario": "udp.lossRate",
  "parameters": { "pps": 25000, "durationSeconds": 60, "payloadBytes": 512, "flows": 256 },
  "metrics": { "sentDatagrams": 0, "destinationReceived": 0, "lossRate": 0.0,
               "outOfOrder": 0, "duplicates": 0, "responsesInjected": 0,
               "achievedPps": 0.0, "sendLoopOverflows": 0 } }
{ "type": "result", "scenario": "tcp.unexpectedEof",
  "parameters": { "concurrency": 64, "transferBytes": 1048576, "durationSeconds": 60, "abortMix": {} },
  "metrics": { "transfers": 0, "completed": 0, "unexpectedEof": 0, "resets": 0, "otherErrors": 0,
               "bytesCompleted": 0, "unexpectedEofRate": 0.0, "meanTransferMilliseconds": 0.0 } }
{ "type": "result", "scenario": "udp.sessionFootprint",
  "parameters": { "sessions": 100 },
  "metrics": { "workingSetDeltaBytes": 0, "gen0Collections": 0, "allocatedBytes": 0 } }
```

Writer: `StabilityContext` (metadata + `WriteResult(scenario, parameters, metrics)`), camelCase
JSON, console + optional file — mirrors the old console/file duality.

### 3.3 `LoopbackSocks5UdpServer` (harness-only, real protocol)

Drives the **real** dial path: `UdpProxyCoordinator` + `Socks5UdpTransportFactory` +
`Socks5ControlConnection`. The harness server implements the minimal server side:

- TCP control listener: read greeting (`VER NMETHODS METHODS…`) → reply `05 00` (no-auth);
  read UDP ASSOCIATE request → reply `05 00 00 01 127.0.0.1 <relayPort>` where `relayPort` is
  a per-connection UDP relay socket bound to loopback.
- UDP relay socket: receive SOCKS5 UDP datagrams (`Socks5UdpCodec.TryDecode` — reuse the
  production codec, benchmarks already reference `WinForward.Protocols`), forward payload to
  the decoded destination (the scenario's echo receiver), and echo responses back to the
  client's last source endpoint with `Socks5UdpCodec.Encode`.
- Keep-connection-count bounded (relay sockets disposed when the control connection drops).

Rationale for not referencing `tests/…/TestHelpers`: keeps the benchmark harness decoupled from
the xunit project; the server here needs different behavior (echo + counters) anyway.

### 3.4 `udp.lossRate` scenario

1. Start `LoopbackSocks5UdpServer` + one UDP echo receiver socket (the "destination").
2. Create `UdpProxyCoordinator` with the real factory and a counting `IUdpResponseSink`
   (`responsesInjected`).
3. `flows` distinct `FlowKey`s; paced send loop targets `pps` aggregate across flows
   (burst-of-N + `Task.Delay` pacing; `sendLoopOverflows` counts rounds that missed their
   deadline). Payload = 8-byte big-endian sequence number + flow id + filler to
   `payloadBytes`.
4. Echo receiver tracks per-flow `lastSeenMax`; `seq < lastSeenMax` → out-of-order, duplicate
   detected via 64-entry ring buffer of recent sequence numbers per flow.
5. Duration elapsed → stop sender, drain 2 s, dispose, emit record.
   `lossRate = 1 - destinationReceived / sentDatagrams`.

### 3.5 `tcp.unexpectedEof` scenario

Concurrent transfer loop until deadline. Each transfer (all loopback, `TcpProxyRelay` under
`#pragma warning disable CA1416`, same precedent as the old relay benchmark):

1. Two socket pairs (local↔relayLocal, upstream↔relayUpstream); relay started.
2. Sender writes `transferBytes`; receiver reads until EOF/exception.
3. One adversarial event per transfer, chosen by weighted mix, fired at a random fraction
   (20–80 %) of the byte count:
   - `clean` — no event (expect: complete).
   - `clientRst` — `localPeer.Close()` with `LingerState(0, true)` → RST into the relay.
   - `relayCancel` — cancel the relay's `CancellationTokenSource` mid-stream.
   - `upstreamTruncate` — `upstreamPeer.Shutdown(Send)` early.
4. Receiver-side classification:
   - `completed` — received == expected.
   - `unexpectedEof` — `ReceiveAsync` returned 0 with `0 < received < expected`
     (the FIN-before-completion the smoke logs show).
   - `resets` — `SocketException` (ConnectionReset/ConnectionAborted).
   - `otherErrors` — anything else (counts, does not fail the run).

### 3.6 `udp.sessionFootprint` scenario

Old `udpSessions.active` semantics preserved: fake `BenchmarkUdpTransport` (no sockets),
coordinator created with capacity N ∈ {1, 100, 1000}, populate, snapshot
`workingSetDeltaBytes` / Gen0 / allocated **before disposal**, then dispose, then emit record
per N.

## 4. File layout (all ≤ 400 effective lines)

```
benchmarks/WinForward.Benchmarks/
├─ WinForward.Benchmarks.csproj          (+ BenchmarkDotNet PackageReference)
├─ Program.cs                            ~50   (mode dispatch)
├─ Perf/
│  ├─ BenchmarkShared.cs                 ~210  (frame builders, CreateFlowKey/CreateContext,
│  │                                            fakes: CountingExecutor, ThresholdOnlyLogger,
│  │                                            NeverOwnedGuard, NoopAsyncDisposable,
│  │                                            BenchmarkUdpTransportFactory, NoopUdpResponseSink)
│  ├─ ParserBenchmarks.cs                ~160
│  ├─ NdisBufferBenchmarks.cs            ~70
│  ├─ FlowTableBenchmarks.cs             ~130  (Miss + Hit classes)
│  ├─ SelfTrafficBenchmarks.cs           ~90
│  ├─ DispatcherBenchmarks.cs            ~90
│  ├─ CapturePumpBenchmarks.cs           ~190  (incl. FiniteCaptureReader)
│  ├─ UdpSessionBenchmarks.cs            ~75
│  └─ TcpRelayBenchmarks.cs              ~130
└─ Stability/
   ├─ SoakOptions.cs                     ~90
   ├─ StabilityContext.cs                ~90   (JSONL metadata + result writer)
   ├─ SoakRunner.cs                      ~90   (parse → run scenarios → summary exit code)
   ├─ LoopbackSocks5UdpServer.cs         ~180
   ├─ UdpLossScenario.cs                 ~200
   ├─ TcpEofScenario.cs                  ~200
   └─ SessionFootprintScenario.cs        ~100
```

Shared fakes live once in `Perf/BenchmarkShared.cs` (`internal`); `SessionFootprintScenario`
reuses `BenchmarkUdpTransportFactory` from there.

## 5. Analyzer compatibility

- `CA1416` pragmas (with justification comments) only where Windows-attributed types are
  touched: `CapturePumpBenchmarks` (pump/processor), `TcpEofScenario` + `TcpRelayBenchmarks`
  (`TcpProxyRelay`) — identical precedent to the current harness.
- `MA0051` pragma no longer needed (classes split by family).
- `S1215` (forced GC.Collect) disappears with `BenchmarkContext`.
- Benchmark classes/methods public (BDN requirement); fakes stay `internal`/private nested, so
  `S1104`-style mutable-field warnings keep their current benign status.

## 6. Trade-offs / alternatives rejected

- **BDN custom `IMetricDescriptor` for loss rate / working set** — rejected: forces
  reliability counters into a time-distribution model they don't fit; separate runner is
  honest about what is being measured.
- **Referencing `tests/WinForward.Core.Tests` for `Socks5TestServer`** — rejected: test-project
  dependency in a benchmark host; server needs echo+counters behavior anyway.
- **Keeping the old JSONL perf pipeline alongside BDN** — rejected: dual statistic paths
  invite before/after comparisons across incompatible methodologies (PRD: no dual paths).
- **Soak runner on real NIC / driver** — out of scope (Non-Goal), loopback loss still exercises
  buffer pressure, coordinator queuing, and codec correctness.

## 7. Rollout / rollback

- Rollout: single PR; `src/` untouched (zero production blast radius).
- Rollback: `git revert` of the benchmark commit restores the old harness.
- Historical comparability note for `README.md`: numbers before/after this change are **not**
  comparable (different measurement methodology); the old baseline JSONL stays archived under
  `.trellis/tasks/08-17-performance-hotspots/research/`.
