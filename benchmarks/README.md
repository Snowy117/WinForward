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

Scenario families (classes under `Perf/`, each with `[MemoryDiagnoser]` — for the relay the
absolute allocation includes one-time socket-buffer scaffolding per invocation, so gate on the
before/after delta at equal chunk sizes rather than the raw number):

| Class | Covers | Sweep |
|---|---|---|
| `ParserBenchmarks` | IPv4/UDP frame parse, payload parse, SOCKS5 UDP decode/encode/span-encode | frame bytes 64 / 512 / 1514 |
| `ParserBenchmarks` (IPv6 rows) | IPv6/UDP frame parse and payload parse (`Ipv6UdpTryParse` / `Ipv6UdpPayload`), mirroring the IPv4 pair against `BenchmarkShared.CreateIpv6UdpFrame` | frame bytes 64 / 512 / 1514 |
| `NdisBufferBenchmarks` | `NdisPacketBuffer` allocate+set+dispose vs reuse | frame bytes 64 / 512 / 1514 |
| `FlowTableMissBenchmarks` / `FlowTableHitBenchmarks` | flow-table resolve miss / cross-adapter hit | cardinality 0–65 535 |
| `FlowTableProductionShapeBenchmarks` | the shipped cardinality's real orientations: same-orientation hit, reverse-alias hit (every proxied reply), and the per-hit activity-clock reference row | cardinality 4 096 |
| `SelfTrafficBenchmarks` | self-traffic registry wildcard miss | cardinality 0–65 535 |
| `DispatcherBenchmarks` | warm-path dispatch (pass, trace off) | — |
| `UdpReadyPathContentionBenchmarks` | the UDP ready path's per-datagram cost under 1/2/4 worker threads (counting fake transport, so the number is the coordinator gate + session lookup, not sockets) | workers 1 / 2 / 4 |
| `CapturePumpBenchmarks` | end-to-end pump round: 200 000 synthetic packets through dispatcher + processor | frame 128/1400 × batch 32/1 |
| `UdpSessionBenchmarks` | UDP session populate + dispose cost | 1 / 100 / 1000 sessions |
| `SessionSetupDecompositionBenchmarks` | staged Noop session-setup decomposition (capacity → admission → setup start → session tier → teardown) plus component probes and the fake baseline | 1 / 100 / 1000 sessions |
| `FrameworkSetupBenchmarks` | per-session framework dial split (control connect + handshake, UDP ASSOCIATE, relay socket, self-traffic, transport ctor) | 1 000 create+dispose per variant |
| `TcpRelayBenchmarks` | one-way relay transfer (256 KiB–16 MiB) | chunk 1 / 1024 / 8192 / 65536 |
| `TcpRedirectDataPathBenchmarks` | the composed per-packet proxy cost: sequence tracking → forward/reverse rewrite → MAC swap, in host and forwarded association shapes, IPv4 and IPv6 | frame bytes 128 / 1400 × IPv4/IPv6 |
| `ChecksumBenchmarks` | scalar vs vectorized Internet checksum (P2b decision data; scalar is the baseline) | frame bytes 64 / 512 / 1514 |

`TcpRedirectDataPathBenchmarks` is the composed row for the redirect data path: one invocation performs
the real per-packet work a proxied TCP frame pays — sequence tracking, the leg's endpoint rewrite, and
the MAC swap — in both association shapes and both families, and `[GlobalSetup]` proves every row's
rewrite succeeds on the pristine frame before anything is timed. The driver send is deliberately **out
of scope**: it is an IOCTL, so a counting fake would measure the fake rather than the driver, and the
Windows driver's cost is not observable from a Linux micro row. The two new `ParserBenchmarks` IPv6
rows are parse-only shapes (Ethernet + IPv6 + UDP, zero UDP checksum — neither production parser reads
the checksum field), so like the IPv4 pair they measure header classification plus payload length.

Interpretation guidance from the hot-path conventions still applies: treat ns/pps deltas under
~2× as noise on a dev box; allocation bytes and GC counts are the exact gates. Use the same
command, machine, power settings, and runtime for before/after comparisons.

## Running on a Windows guest (no .NET SDK)

Learned in the 2026-08-30 VM program (`results/2026-08-30-windows-vm/README.md`):

- BDN's default process-isolated toolchain needs the `dotnet` CLI; on an SDK-less guest
  it prints "requires dotnet SDK" and executes 0 benchmarks. Add `--inProcess` there —
  and use the same toolchain on the Linux side when comparing platforms.
- Multi-family selection uses one `--filter` with several glob values:
  `-f '*CapturePump*' '*Dispatcher*' ...`. A repeated `--filter` is silently ignored.
- Launch BDN from the repo root on every OS; from another cwd the generated boilerplate
  fails with "Unable to find WinForward.Benchmarks".
- Self-contained single-file publish (`-r win-x64 --self-contained
  -p:PublishSingleFile=true`) is the transportable shape; the stability runner works
  anywhere, elevated or not.
- Switch the guest to the High Performance power plan before measuring, and compare
  Windows numbers only against Linux runs taken the same way.

## Stability mode

Count-based reliability metrics under sustained load. One JSONL record per scenario execution
(schemaVersion 2, camelCase), written to the console and optionally `--output`:

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability [--scenario all|udp|udpBurst|udpChurn|udpSessionBudget|tcp|tcpChurn|tcpthroughput|footprint|baseline|residency|retention|scaling|sweep|pump] [--duration 60] [--pps 25000] \
  [--payload-bytes 512] [--flows 256] [--udp-flows 100] [--burst-flows 48] [--dial-delay-ms 0] [--churn-waves 1] \
  [--rate 20] [--capacity 16384] [--churn-seconds 90] [--drain-seconds 120] \
  [--target socks5|local] \
  [--tcp-concurrency 64] [--tcp-transfer-bytes 1048576] [--socks5-external] \
  [--attribution-delay-ms 0] [--attribution-delay-percent 5] [--threads 0] [--shared-key-percent 10] \
  [--abort-mix clean=25,clientRst=25,relayCancel=25,upstreamTruncate=25] [--seed 42] \
  [--output <path>] [--quick]

Every scenario not listed in the `all` arm of the parser is excluded there deliberately; the runner's
own summary enumerates the reason per exclusion (`gc-soak`, `udpSessionBudget`, `scaling`, `sweep`,
`pump`, `residency`, `retention`, `tcpChurn`).
```

`--quick` = `--duration 15 --pps 10000 --tcp-concurrency 16 --flows 64`.

The harness no longer has a UDP association-placement knob: a proxy-decided flow owns its own
authenticated association (task `10-05-remove-udp-association-sharing`, R6), so there is one shape to
measure. The `--reuse` flag is gone and an old command line meets the harness's unknown-argument
error; the historical `off`/`auto`/`always` columns under `results/` are annotated as measurements of
a removed feature and their commands are historical.

`--target socks5|local` selects the transport the two flow-establishment scenarios send through:
`socks5` (the default) relays each flow through the loopback SOCKS5 server, `local` points the same
flows at a loopback endpoint through the **product** `UdpTransportFactory` composite — both factories
composed as the CLI composes them — which forwards the payload verbatim and attributes the answer to
the flow's original destination. It is the placement column R7 of `10-05-local-dns-transport` asks
for; its series is `results/2026-10-05-local-target/`, and the surviving columns' confirmation series
is `results/2026-10-05-no-association-sharing/`. Only `udp.churn` and `udp.burstEstablishment`
host the column; every other scenario leaves its relay path unchanged and is refused at parse time
rather than recording a relay-measured row stamped `local` (a session-budget run, for instance, is a
retention/descriptor soak whose row has no local-target meaning). The two
scenarios that do host it keep the SOCKS5 server running in every column. Both servers' own
counters land in every row — `socks5Handshakes` (`controlConnections` / `associateReplies`) and
`localResponder`
(`datagramsReceived` / `datagramsReplied`), cumulative from the start of the run's load (the
unmeasured warmup wave included) — so the local column's zeros are read beside the relay columns'
non-zeros in the same artifact rather than asserted by construction. Cumulative rather than per
window is deliberate: each wave's handshakes are only readable as the difference between two rows,
and the last row of a run is its total. An unknown value is
refused at parse time, and `--target local` refuses `--socks5-external` because an
out-of-process server's counters never reach this parent.

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
  `achievedPps`, `sendLoopOverflows`. `lossRate` is a **forward-direction** figure —
  `destinationReceived / sentDatagrams`, measured at the echo destination — so it does not depend on
  which flow a relay reply came back on and no response-ownership filter applies to it;
  `responsesInjected` is a window total for the same reason. Per-flow response ownership is measured
  by `udp.churn`, `udp.burstEstablishment` and `udp.sessionBudget` (see
  `results/2026-10-05-udp-reuse-ownership/`).
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
- **`udp.burstEstablishment`** — the flow-establishment burst shape (DNS-wave startup):
  `--burst-flows` new flows fire their first datagrams back-to-back while `--flows`
  pre-established background flows keep pacing through control / burst / post windows. Reports
  per-flow first-response latency distribution (`firstResponseMs` min/p50/p95/p99/max/mean,
  `timeToFirstMs` / `timeToLastMs`), admission (`burstAccepted` / `burstRejected` /
  `establishmentLossRate`), harness cost (`timeToIssueMs`), and per-window background metrics
  (`sent`, `injected`, `lossRate`, `sendP95Ms`, `sendMaxMs`, `achievedPps`) so burst-window
  degradation against the control window is a within-row comparison. `--dial-delay-ms`
  delays the harness SOCKS5 server's UDP-ASSOCIATE reply to model a remote dial — on loopback
  the sub-millisecond dial hides the 8-wide setup limiter's queuing, so nonzero delays are how
  the serialization wave (`ceil(N/8) × delay`) becomes visible. Recommended invocation
  `--flows 16 --pps 4000` (stays under the Windows ~4.7k pps loopback ceiling);
  `--duration`/`--quick` do not apply — the scenario has fixed window lengths. Series started
  2026-09-06; baseline matrix under `results/2026-09-06-udp-burst/`. The `firstResponses`,
  `establishmentLossRate` and `firstResponseMs` fields were re-based on 2026-10-05 to per-flow
  response ownership — a reply counts only when it arrived on the flow that asked — so rows recorded
  before that date report a success they did not measure (see
  `results/2026-10-05-udp-reuse-ownership/`). Follows `--target`: under `local` the burst and the
  background flows go through the product transport composite to a loopback responder, and the row's
  `socks5Handshakes` / `localResponder` counters are the run's totals on both sides of the comparison
  (`results/2026-10-05-local-target/`). Read the burst window's duration with its `injected / sent`
  figures: the window closes when the burst's own first responses arrive or the adaptive timeout
  fires. Both surviving columns answer the burst's flows, so both close it in tens of milliseconds;
  the historical `auto` column in `results/2026-10-05-udp-reuse-ownership/` ran it to the 30 s floor
  because only 3 of 48 flows saw their own reply, which is a reading of a removed shape.
- **`udp.churn`** — session-creation churn: waves of `--burst-flows` short-lived sessions through
  the real dial path, each wave retired through the coordinator's own idle-expiry path
  (`RemoveExpiredAsync` with a zero timeout, i.e. the per-session teardown the periodic sweeper
  would drive), with per-wave `allocatedBytes` / `bytesPerSession` / `gen0`–`gen2` deltas sampled
  around the full create→respond→retire cycle. Every process first fires one unmeasured warmup
  wave (first-call JIT, worker-thread creation, and socket-stack warmup would otherwise dominate a
  short wave row). `--churn-waves K` (K ≥ 1) fires K consecutive
  waves and emits one row per wave (parameters carry the wave index); `--churn-waves 0` cycles
  waves back-to-back for `--duration` seconds and emits one aggregate row (`bytesPerSession`,
  `bytesPerSecond`, `achievedSessionsPerSecond`, per-wave bytes/session min/p50/p95/max, and the
  pooled first-response latency distribution). `--dial-delay-ms` delays the harness SOCKS5
  server's UDP-ASSOCIATE reply, so the realized session rate approaches the 8-wide setup limiter's
  bound (≈ 8 / delay). The same flow keys are re-offered every wave (a client tuple re-querying
  after its session expired), and a setup-failure cooldown surfaces as that wave's establishment
  loss. Allocation sampling uses `GC.GetTotalAllocatedBytes(precise: false)`; latency is
  reported as ordinals only. Each row carries the wave's per-flow accounting — `firstResponses`
  (own), `misdelivered` and `noResponse`, the clamped remainder
  `max(0, flows − own − misdelivered)` — so the wave's flows are accounted for from the row itself:
  `own + misdelivered + noResponse == flows` against `--burst-flows` (or the sustained row's
  `sessions`), which is exact unless a flow both received a foreign reply and was later answered by
  its own and is therefore counted in `own` and `misdelivered` both. Series re-based
  2026-10-05 on per-flow response ownership (see `results/2026-10-05-udp-reuse-ownership/`). Follows
  `--target`: under `local` the same waves go through the product transport composite to a loopback
  responder, and every row carries the two servers' own `socks5Handshakes` / `localResponder`
  counters cumulative up to that row — so the local column's zero handshakes sit beside the relay
  columns' non-zeros, and the per-flow accounting says which transport answered which flow
  (`results/2026-10-05-local-target/`).
- **`udp.sessionBudget`** — the session-budget soak (PRD acceptance 1): `--rate` new flows/s for
  `--churn-seconds`, then `--drain-seconds` with no new flows, sampling the live sessions, the
  harness SOCKS5 server's live control connections (the rows' `associations` column), the process's
  own descriptors, and the estimated kernel receive buffer
  every 5 s. It asserts no loss or rejection, the retention ceiling
  `rate × (idle + 2 × sweep) + margin` (and the receive-buffer estimate as its byte form), the
  per-session descriptor budget, and drain-to-zero (sessions, the harness server's live control
  connections, and descriptors). A churn
  window whose cumulative flow
  count does not exceed the ceiling is refused before the load — it could not discriminate retention
  from accumulation. The verdict row names the surviving term (`verdict.retentionBounded`) and the
  failures behind it. Each row carries `misdelivered` and `noResponse` beside `datagramsReceived`
  (the own term), so
  `own + misdelivered + noResponse == accepted` is checkable from the row, and its `associations`
  column is the **harness SOCKS5 server's own live control-connection count** — accepted minus
  closed, read on the peer side of the dial: the shipped one-association-per-flow shape (task
  `10-05-remove-udp-association-sharing`, R6) seen from the server, so a control connection that
  outlives its session is visible in the row and the drain's association term is not the sessions
  term restated. Under `--socks5-external` the out-of-process server's count never reaches this
  process, so the column is **omitted** (the writer drops an unobserved field rather than reporting a
  number nobody read): the drain's association term is then not evaluated, and the descriptor budget
  charges the shipped one-control-connection-per-session allowance in place of an observation. Note
  the shipped 20 flows/s rate leaves only one
  flow in flight at
  a time; `--rate 500 --churn-seconds 60` is the overlapping-arrival variant. Series and commands:
  `results/2026-09-28-udp-reuse/` (per-flow success columns
  superseded 2026-10-05) and `results/2026-10-05-udp-reuse-ownership/`; both measured association
  placement, which no longer exists.
- **`tcp.unexpectedEof`** — concurrent one-way transfers through `TcpProxyRelay` with an
  adversarial event fired mid-stream per transfer (weighted mix: clean / client RST / relay
  cancellation / upstream truncation at a random 20–80 % of the transfer). Receiver-side
  classification: `completed`, `unexpectedEof` (FIN before completion — the smoke-log failure
  shape), `resets`, `otherErrors`, plus `bytesCompleted` and per-transfer mean latency.
- **`udp.sessionFootprint`** — live-footprint measurement at 1 / 100 / 1000 UDP sessions using
  fake transports: `workingSetDeltaBytes`, `gen0Collections`, `allocatedBytes` captured with the
  session pool live (before disposal). This is the old `udpSessions.active` metric; a
  working-set level is not a time distribution, so it lives here rather than in BenchmarkDotNet.
  Each population also emits `udp.sessionFootprint.cycle` with `overflowFirstWave` /
  `overflowSecondWave` — the receive-window pool's cumulative overflow counter after a first
  population and after a second one over the same pool. The pool is built through
  `UdpProxyCoordinator.ReceiveWindowPoolCapacity(sessions)`, so the row mirrors composition: a first
  fill reads N at any capacity, and only the second wave's growth discriminates a pool sized from the
  session capacity from one left at the type's default.
- **`udp.sessionRetention`** — the mixed one-shot/sustained retention arm (F6): two identically built
  cohorts of 32 one-shot (one datagram, answered) and 32 sustained (two datagrams) sessions over fake
  transports that carry the exchange evidence, one shared mutable clock, and **one sweep per cohort at
  the same instant** — the control with both timeouts equal to the configured retention (nothing is
  past it, so both classes stay resident) and the treatment with the 5 s one-shot class (exactly the
  one-shot class retires). One coordinator per cohort is load-bearing: a coordinator sweep is
  population-wide. The row carries `oneShot`/`sustained`, the two TTLs it drove and the resident count
  per class; the run aborts unless each cohort's population is proven live before its sweep (the shared
  factory's created count and every transport's flush, cross-checked against the coordinator's slot
  count), the treatment's two TTLs differ, and the arms come out in the predicted shape. The control is
  the attribution: the two rows share every input except the classification. Excluded from
  `--scenario all` (a controlled probe, not a soak); series:
  `results/2026-10-01-udp-session-footprint/session-retention.jsonl`.
- **`scaling.contention`** — the lock chain's contention and scaling curve (research F2): one shared
  `FlowTable` seeded with `--flows` live states plus one self-traffic registry, driven by 1/2/4
  dedicated worker threads (released from a common gate, so the window excludes start skew) that each
  walk their own key partition with every `--shared-key-percent`-th lookup aimed at a shared pool (the
  reverse-leg traffic every adapter sees). The measured unit is the pair a warm packet pays —
  `ISelfTrafficGuard.IsOwned` then `FlowTable.TryResolve`. Every configuration is measured **twice**:
  once with `NeverOwnedGuard` (the guard shape every existing dispatcher row uses) and once with a real
  populated `SelfTrafficRegistry`; the fake arm cannot see the self-traffic work at all, which is the
  point of the A/B. Reports resolutions/s, per-worker rates, worker spread, and a verdict row carrying
  the scaling ratio `throughput(N) / (N × throughput(1))` per arm. **Report-only** (`gated: false`) —
  contention curves are noisy, so the artifact is the curve, not a pass line; run it 3× and quote the
  median. `--threads N` pins one configuration instead of the sweep. Excluded from `--scenario all`
  (a micro probe, and the A/B doubles the run). First series:
  `results/2026-09-29-benchmark-coverage/scaling-contention.jsonl` — at `--flows 4096` the real arm
  measured 3.33M / 3.40M / 2.70M resolutions/s for 1/2/4 threads (ratio 1 / 0.51 / 0.203) against the
  fake arm's 6.77M / 4.64M / 4.27M (ratio 1 / 0.343 / 0.158): the self-traffic check alone halves the
  single-thread rate, and adding workers makes aggregate throughput *worse* — the F2 claim, quantified.

- **`flowTable.sweepPause`** — the sweep's stop-the-world pause (research F3): a 65,536-entry table is
  refilled with already-expired states and swept with the production call (`FlowTable.RemoveExpired` plus
  the coordinator's "still held?" predicate) while `--tcp-concurrency` observer threads resolve live TCP
  keys. Live keys are held by protocol through the hold predicate, so the observers never lose their
  working set, and a resolve that misses aborts the run instead of reporting a pause for the wrong work.
  Reports the sweep's mean/max wall time, its allocation per sweep, and the observer-side **maximum**
  pause plus counts over 0.1 / 0.5 / 1 / 5 ms — an exact maximum rather than a sampled percentile,
  because the one pause that matters is the one a sample misses. Every timing field is **report-only**
  (`gated: false`) and is quoted against the calibration control below; the sweep's hold bound is proven
  by counts in `SweepAllocationGateTests.FlowTableSweepHoldWorkIsBoundedByChunkEntries`, and its zero
  allocation by `SweepAllocationGateTests`. `--flows` can raise but not lower the 65,536 floor.
  `--sweep-window-control-ms <n>` runs the **calibration control**: the sweeper thread arms the same
  in-window flag and then waits *n* ms instead of calling the sweep (no refill, no removal, no tripwire;
  `removedPerSweep` reports 0 and the row's `note` says it is a control). A control run at `120` measured
  a 5.19 ms in-window maximum and 12,336 in-window pauses over 0.5 ms with **zero product work inside the
  window**, which is why no in-window timing maximum is an acceptance figure on a shared host: the same
  order of pauses appears with no sweep at all. Excluded from `--scenario all` (it seeds a 65k table and
  loops: a probe, not a soak). First series:
  `results/2026-09-29-benchmark-coverage/sweep-pause.jsonl` — sweep 25.1 ms mean / 87.1 ms max, resolve
  pause up to **39.5 ms against the 0.5 ms target**, 14,022 resolves over 1 ms in 15 s, 0 B per sweep.
- **`pump.idleWake`** — the capture pump's idle cost and its wake→dispatch latency (research F5), driven
  through fake readers so no hardware is needed. Four rows. The idle row (`pump.idle`, no arrival signal)
  runs an always-empty reader for `--duration` (capped at 15 s) and reports `cpuSecondsPerIdleSecond` (a
  `Process.TotalProcessorTime` delta taken over the window on the scenario thread),
  `polls`/`pollsPerSecond`, and the window's managed allocation — which must be **0 B**, enforced in the
  row itself (a non-zero delta fails the run). `pump.idleEvent` (F5, 2026-10-01) runs the same window with
  the production `NdisPacketArrivalSignal` installed over an event the harness never sets, so every wait
  runs to its 100 ms bound: it reports `waits`/`waitsPerSecond` and `emptyReadsPerSecond` beside the CPU
  number, and its own 0 B throw is what covers ~150 **real** timeouts (the xunit gates can only exercise
  `WaitOne(0)`'s fast path). `pump.idleWake` parks `TryReadPackets` on a semaphore until the harness arms
  exactly one frame — the reader-side proxy for the arrival shape — while `pump.idleWakeEvent` parks the
  **pump** in the arrival wait and has the harness arm the frame and set the event, confirming the park
  (one more `Wait` entry than the previous wake) before every measured arrival so a hot handoff cannot
  masquerade as a wake; a missing park entry, a timeout-driven return, or a lost dispatch fails the run
  instead of being silently dropped. Both wake rows report arrival→dispatch p50/p95/p99/max over 5,000
  wakes (plus 500 unrecorded warmups). All four carry the F5.1 seam-level read-call accounting
  (`readCallsPerPoll`, `readCallsPerPacket`), and `CapturePumpReadCallTests` gates that pattern exactly
  (one `TryReadPackets` call per poll and one per batch, never one per packet; one handler call per
  packet; one read + one wait for a signal-installed idle iteration). **F5.1's driver-internal read shape
  is gated at its own seam** (`NdisApiReadShapeTests`, over `NdisApiDriver.CreateForTests` and the
  `INdisReadPacketCalls` interface): one batched read and no queue query per non-empty drain, the query
  only on the non-success path. What the stability rows add is the pump's own cadence, which is what makes
  the idle-IOCTL-rate claim (`~886 polls/s → ~10 waits/s`) measurable. Report-only timing
  (`gated: false`): the exact numbers are the rows' 0 B throws, the exact wait/read counts and the xunit
  call counts. Excluded from `--scenario all` (a micro probe, not a soak). Series:
  `results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl` (before) and
  `results/2026-10-01-pump-io-shape/pump-idle-wake-after.jsonl` (after) — **0.0178–0.0185 CPU seconds per
  idle second at ~880 polls/s and 0 B allocated** (the 1 ms pacing resolves at ~1.13 ms on this host, so
  the cadence is `Thread.Sleep` resolution and the ~20 µs of CPU per poll is the timer/wake cost, not
  packet work), the idle wait at **~10 waits/s and ~0.0005 CPU seconds per idle second**, and wake
  **p50/p95/p99 of ~0.07 / ~0.11 / ~0.15 ms** on both the reader-side proxy and the pump-side wait.

- **`flowTable.claim`** — the insert path's cost on a fresh table (2,339.7 ns and 192.02 B per claim at
  the production cardinality), the half of the probe-cost question BenchmarkDotNet cannot express: a
  "new key" row would silently become a resolve row once the table filled mid-iteration.

- **`tcp.churn`** — new-flow churn through the real TCP redirect relay path (research F8):
  `--rate` connections/s, `--tcp-concurrency` workers, each connection dialled, SOCKS5-established, sent
  `--payload-bytes` and closed. Reports the first-byte distribution per class (p50/p95/p99/max and mean,
  never a mean alone), establishment outcomes with failure reasons, transient allocation per second and
  per connection, and the server's own CONNECT-reply and echoed-byte counts as cross-checks.
  `--attribution-delay-ms` with `--attribution-delay-percent` injects a synthetic per-flow stall on the
  selected share of connections, which is the shape of the pump-thread process attribution that only runs
  on Windows: the delayed class is reported separately so "the tail moved and the middle did not" is
  readable from one artifact. Report-only (`gated: false`); on Windows run it with the delay at 0 and the
  real attributor supplies the stall. First series:
  `results/2026-09-29-benchmark-coverage/tcp-churn-*.jsonl` — ~91 KB allocated per new flow, and a 50 ms
  stall on 1 % of connections moves the pooled p99 from 8.3 ms to ~52 ms (≈6×) while the stable-class mean
  stays at 2.34 / 2.36 / 2.21 ms across the three runs.

- **`residency.census`** — the live-residency census (research A1/A4 memory items plus F6's TCP half):
  a zero-flow baseline taken in-process, then three populations built in order — the flow table seeded
  to `--flows` live states at its production 65,536-state capacity, `--flows` real loopback SOCKS5
  relays held open (two 64 KiB native pump windows each), and `--udp-flows` coordinator sessions over
  fake transports — with `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();` immediately
  before every sample. Each row reports `GC.GetTotalMemory(forceFullCollection: true)`,
  `TotalCommittedBytes`, working set, `PrivateMemorySize64`, descriptors, per-generation collection
  counts and the flow table's `Count`/`Capacity` as absolute values, as deltas against the baseline,
  and as deltas against the previous stage — read `vsPreviousStage`, because the populations are
  cumulative and its delta is the one attributable to that stage's own population. **Report-only**
  (`gated: false`): no byte or timing threshold can fail the run. The only aborts are the population
  proofs (`--flows` flow states, the loopback server's CONNECT-reply count, the transport factory's
  created count cross-checked against the coordinator's `SessionCount`) — a census of a population
  that silently failed to build would be a fabricated number. Excluded from `--scenario all` (a
  census, not a soak). `--flows` sizes the TCP populations, `--udp-flows` the UDP one. First series:
  `results/2026-09-29-benchmark-coverage/residency-census.jsonl` — the flow table costs **32.5 MB
  whether it holds 1, 100 or 1000 live states** (A1 item 1a's pre-allocation, ≈251 B per live
  state), a held-open relay adds **146–160 KB** of working set and **4.22 descriptors**, and a
  fake-transport UDP session adds **≈4.2 KB managed / 41–44 KB working set**. `PrivateMemorySize64`
  is reported but is not a per-population column on Linux (it is `VmData`, which carries the
  allocator's per-thread arenas): see the artifact's readings.

A nonzero loss rate or an EOF count under an adversarial mix is an observation, not a harness
failure — the numbers become meaningful as a comparison series across builds. For UDP, loss
before the OS saturates (e.g. >0 at `--pps 50000`) indicates socket-buffer pressure worth
investigating.
