# Design — benchmark coverage for F2–F8

No product behaviour changes. The only production-side edits allowed are injected measurement seams
(constructor/options parameters that already exist or are added as *optional* seams, never hot-path
branches).

## 1. Harness placement

| Requirement | Harness | Why |
|---|---|---|
| R1 contention/scaling, R3 concurrent sweep observer, R4 TCP churn, R7 residency census | stability runner (`Stability/`, one scenario class per file + `SoakOptions` arguments) | needs several threads, sustained load, live sockets and a JSONL verdict row; BenchmarkDotNet runs one method at a time and cannot express contention honestly |
| R2 flow-table shapes, R5 proxy-path micro, R6 row shapes | BenchmarkDotNet (`Perf/`, one class per file) | micro rows with allocation diagnostics; the contention family (R1) is *also* expressible as a BDN method that fans out internally — but the scenario form is chosen for R1 because it must report a scaling curve and a verdict, not a single mean |

Both harnesses are already wired (`Program.cs` dispatches `--stability` to the soak runner, everything
else to BDN), and both are under the repository's ≤400 effective-line rule, so new work lands as new
files rather than growth of existing ones.

## 2. Requirement designs

### R1 — contention and scaling family (`Stability/ScalingContentionScenario.cs`)

- **Shape**: one shared `FlowTable` (capacity 4,096) seeded with `--flows` live states, one shared
  `SelfTrafficRegistry`, and `--threads` (1/2/4) independent worker loops, each resolving keys that are
  mostly its own (so the measurement is the *table's* contention, not a hot single key) plus a small
  shared-key fraction to model the reverse-leg traffic that every adapter hits.
- **Metrics**: aggregate resolutions/s, per-worker throughput spread (max/min), and the *scaling ratio*
  `throughput(N) / (N × throughput(1))` — the F2 claim is that this drops below 1 as N grows because the
  single-instance locks ping-pong.
- **Fake-collaborator A/B (acceptance)**: the same workload runs once with `NeverOwnedGuard` and once
  with the real `SelfTrafficRegistry` populated with a realistic self-traffic tuple count, and both rows
  are recorded. This is the artifact that proves the existing `DispatcherBenchmarks` rows cannot see the
  self-traffic work.
- **Report-only**: thread-count scaling is inherently noisy; no gate and no in-band median. The scenario
  emits one row per configuration and the operator repeats the run, as the repo does for soak batches.

### R2 — flow-table shapes (`Perf/FlowTableBenchmarks.cs`, additive rows)

New rows, existing rows untouched: `ClaimNewFlow` (a genuinely new cold key per invocation, exercising
`TryClaimResolved` + insert), `ResolveReverseAlias` (the reverse orientation that every proxied download
packet takes), `ResolveHitAtLiveCardinality` (a table seeded with 4,096 live flows, i.e. the production
`tcpFlowCapacity` default rather than an empty table), and `ResolveHitMissNoTouch` if a clock-free
variant is added later. Serves F2/F4 as the probe-cost baseline.

### R3 — sweep pause (`Stability/SweepPauseScenario.cs` + an exact allocation gate)

- Seeded `--flows 65536` live states; a sweeper thread calls `RemoveExpired` on the production cadence
  while `--threads` observer threads call `TryResolve` in a tight loop, timestamping each call.
- **Reported**: sweep wall time per tick (median/max), observer `TryResolve` latency p50/p99/**max**
  (the research's "max time a `TryResolve` blocks during a sweep tick"), and the number expired per tick.
  The research's `max < 0.5 ms` is recorded as the *target line* in the verdict row, not a hard gate
  (a hard timing gate would be load-flaky); the **zero-allocation** half becomes the gate.
- **Gate (exact)**: `HotPathAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` — `GC.GetAllocatedBytesForCurrentThread`
  around one full 65k sweep must be 0 B. The existing `FlowTable.RemoveExpired` already reuses
  `_expiredScratch`; the gate exists to keep it that way (and to fail loudly if a future sweep adds LINQ).
- **Discriminating by construction**: the acceptance check injects a temporary allocation (e.g. a
  `ToArray` in the sweep under test) and requires the gate to fail.

### R4 — TCP new-flow churn (`Stability/TcpChurnScenario.cs`)

- **Shape**: the `tcp.throughput` composition (loopback SOCKS5 TCP server + the real redirect path) driven
  as a *connection* workload: `--rate` new connections/s for `--duration`, each connection fetching a
  small object and closing, `--tcp-concurrency` capping in-flight flows. Reports per-connection
  first-byte latency p50/p95/p99/max, establishment success/failure, and per-5 s allocation deltas.
- **Attribution shape**: `--attribution-delay-ms` injects a synthetic delay into the harness's process
  attributor seam (`IProcessAttributor` is already injectable), modelling F8's on-pump attribution cost
  without Windows. The acceptance check runs 0 ms vs a delay and requires **p99 to move while the mean
  barely does** — the property that makes this scenario the F8 instrument. On Windows the same flag is
  unnecessary; the real attributor provides the delay.
- **Report-only**: the result row carries the load, the attribution settings and `gated: false` — no separate verdict row landed, because the parameters make the mode self-describing.

### R5 — proxy data-path micro (`Perf/TcpRedirectDataPathBenchmarks.cs`)

Composes the real per-packet work of a proxied TCP frame — `TcpSequenceObservation.TrackClientSequence`
(forward) / `RecordServerSynAck` + `TrackServerSequence` (reverse), `TcpFrameRewriter.TryRewriteForwardLeg`
or `PacketChecksums.TryRewriteTcpEndpoints` + `SwapEthernetMacs`, then a counting `ITcpRedirectInjector`
— for **host and forwarded** association shapes × **IPv4 and IPv6** × 128/1400 B. `[MemoryDiagnoser]`
must report 0 B for the rewrite legs (the injector fake records into a pre-sized slot, not a list).

- Needs an **IPv6 frame builder** in `BenchmarkShared` (today all builders are IPv4) — the same helper
  also closes the "IPv6 has no perf rows" item for `ParserBenchmarks`/`FrameRewriterBenchmarks`, which
  gain an IPv6 variant row each.
- Serves as the F1 follow-up number and as F4's parse-once delta target.

### R6 — pump idle and wake (`Stability/PumpIdleWakeScenario.cs` + a call-count gate)

- **Idle row**: a pump with an always-empty fake reader for a fixed wall interval; reports CPU time per
  idle second (`Process.TotalProcessorTime` delta, sampled inside the benchmark method and surfaced as a
  custom metric) plus allocated bytes (must stay 0, the existing `IdlePollIterationsAllocateNoManagedBytes`
  invariant extended to the new shape).
- **Wake row**: `SignallingCaptureReader` — a fake reader whose `TryReadPackets` blocks on a
  `ManualResetEventSlim` until the harness arms it with one frame — measures arrival→dispatch latency
  (arm timestamp to handler entry) as a distribution (p50/p95/p99/max), which is exactly the F5.2
  `SetPacketEvent` shape.
- **Call-count gate (exact)**: a counting `INdisPacketReader` records (query, read) invocations per poll,
  so F5.1's "speculative read halves IOCTLs under load" becomes assertable as call counts without a
  driver: `CapturePumpReadCallTests` asserts the current query-then-read pair and will assert the
  post-change single read. The driver's own IOCTL count stays a Windows concern (`windows-real-nic`).

### R7 — live-residency census (`Stability/ResidencyCensusScenario.cs`)

- Builds a real composition at `--flows` live TCP connections (real relay sockets through the loopback
  server so relay windows are actually allocated), `--udp-flows` live UDP sessions (fake transports are
  acceptable — the measured part is the session/slot/lease residency), then forces a GC and reports:
  `GC.GetTotalMemory(forceFullCollection: true)`, `GC.GetGCMemoryInfo().TotalCommittedBytes`,
  `Process.WorkingSet64`, `Process.PrivateMemorySize64`, per-generation collection counts, and the flow
  table's own `Count`/`Capacity`.
- The census is reported as a per-flow delta against a zero-flow baseline run in the same process, so the
  number is attributable to the live population rather than to the AOT/runtime base. This is the artifact
  the A4 FlowTable rebuild (−19 MB steady state) and the relay-window item (−10 MB @100 conns) are judged
  against.

## 3. Gate vs report

| Metric | Class | Rationale |
|---|---|---|
| Sweep managed allocation (R3) | **gate** | exact, deterministic, and the F3 proposal's own contract ("no allocation under any table lock") |
| Pump idle-poll allocation (R6) | **gate** | existing invariant extended to the new shape |
| Read-side call counts (R6) | **gate** | exact counts; the F5.1 change is defined by them |
| Proxy-path allocation bytes (R5) | **gate** | the `[MemoryDiagnoser]` 0 B column **and** the xunit gate over the four composed legs (`TcpRedirectDataPathAllocationGateTests`), so the claim fails the build rather than only the report |
| Latency percentiles (R3, R4, R6) | report | timing; recorded baselines + the "≥2× is noise" discipline |
| Thread-scaling ratio (R1) | report | inherently noisy; the artifact is the curve, not a pass line |
| Residency bytes (R7) | report (with a documented expectation per item) | depends on GC state and platform allocator; the A4/relay items will change it by design |

## 3.1 How each remaining finding will be proved

Written while planning; it found two holes in the requirement set above (R1b, R3b) plus one honest
residual. Every optimization task should cite its row from this table.

| Finding (research) | Proved by | Status |
|---|---|---|
| F1 batched redirect injection (shipped) | `TcpRedirectInjectionBatchingTests` counting gates (32 frames → 1 call) plus the 0 B gates in `HotPathAllocationGateTests` (`MidFlowRewriteAndInjectAllocatesNoManagedBytes`, `DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes`) — already landed | covered |
| F1 follow-up: per-packet cost of the product's main path | **R5** (`TcpRedirectDataPathBenchmarks`: host/forwarded × IPv4/IPv6, ns + 0 B) | R5 |
| F2.1 self-traffic check off the warm path | **R1**'s fake-vs-real guard A/B (the delta the existing rows cannot see) + existing `SelfTrafficBenchmarks` for the registry's own cost | R1 |
| F2.2 lock-free resolve over immutable decisions | **R1**'s thread-scaling ratio `throughput(N) / (N × throughput(1))` over a shared real table | R1 |
| F2.3 sharded table with a direction-normalized hash | **R1** scaling + **R2**'s live-cardinality and reverse-alias hits (sharding changes both) | R1 + R2 |
| F2.4 UDP ready-path lock-free | **R1b (added by this mapping)**: the UDP ready-path per-datagram cost under 1/2/4 threads over a fake *ready* session+transport — the existing `udp.lossRate`/`udp.rawBaseline` pair sits at the ~25k pps loopback ceiling, where lock overhead is invisible | **new** |
| F3.1 timing wheel / F3.2 incremental cursor sweep | **R3** sweep-pause probe (`--scenario sweep`: 65,536 expired states per tick, exact observer **maximum** plus counts over 0.1/0.5/1/5 ms — no p99 by design, a sampled percentile is what misses the one pause that matters; the BDN sweep row was dropped, see implement.md Step 3) | R3 |
| F3.3 no allocation under any table lock | **R3b, narrowed during implementation**: only `FlowTable.RemoveExpired` is allocation-free today (reused scratch) so only it can carry a *green* gate; `TcpRedirectTable.RemoveExpired`, `TcpRedirectSessionStore.RemoveExpiredAsync`, `UdpProxyCoordinator.RemoveExpiredAsync` and `UdpAssociationPool.SweepIdleAssociationsAsync` build retirement sets with LINQ and are recorded as F3's **targets** (their gates land with the fix) | **gate + 4 targets** |
| F3.4 bucketed activity time | **R2**'s `Touch`/clock-isolation row (the per-hit clock call is what the bucket removes) | R2 |
| F4.1 interned adapter identity (fat `FlowKey`) | **R2** probe cost at live cardinality (hash + `Equals` is where the string compare shows); the **claim** half is a controlled loop in R3/R4, not a BDN row (BenchmarkDotNet cannot reset the table per invocation, so a "new key" row would silently become a resolve row) | R2 + R3/R4 |
| F4.2 parse once / carry the view (four header walks) | **R5**'s composed row (the redundant walks are inside it) + its IPv6 rows. R5 already produced one target: the IPv6 reverse forwarded leg is a reproducible **2.0×** the IPv4 leg (`PacketChecksums.TryRewriteIpv6Tcp` folds a 16-word `stackalloc` loop where IPv4 folds 4 words inline) | R5 + F4 target |
| F4.3 atomic sequence trackers | **R5** (sequence tracking is part of the composed row) | R5 |
| F4.4 slim `FlowContext` (struct copies) | **R5** + `CapturePumpBenchmarks` ns + the dispatcher 160 B warm gate | R5 (indirect) |
| F5.1 speculative read | **R6**'s read-call count gate (query+read per poll → one read) | R6 |
| F5.2 event-driven wake | **R6**'s idle-cost row + wake-latency distribution | R6 |
| F6 resident footprint (buffer default, pool sizing) | existing `udp.sessionFootprint` / `udp.sessionBudget` (adequate) | covered |
| F6 adaptive idle TTL | **Residual**: proving it needs a workload whose sessions are *classified* one-shot vs sustained, and that classification is part of F6's own product change; the census (R7) can then report residency per class. Not buildable before the product signal exists — recorded, not silently claimed | **residual** |
| F8 attribution off the pump thread | **R4** (TCP churn p99 with a synthetic attribution delay; the real number stays Windows) + **R7**'s allocation/GC columns for the transient-garbage half | R4 + R7 |
| A1/A4 steady-state memory (flow table, relay windows, UDP sessions, committed headroom) | **R7** residency census (per-flow deltas + `TotalCommittedBytes`) | R7 |
| A5 lossy fingerprint caches (tombstones/cooldowns) | **R7**'s census at capacity (the cached structures are part of the measured residency) | R7 |

## 4. Risks

| Risk | Mitigation |
|---|---|
| Timing rows flake under CI load | only exact metrics gate; timing rows are recorded artifacts with 3-run medians, and the soak runner's verdict never fails on a percentile |
| New rows silently change existing series | additive only: no existing row name, `[Params]` value or threshold is touched |
| R4's synthetic attribution delay is not the real attributor | it is explicitly a *shape* instrument (does the tail react?); the Windows real number stays with `windows-real-nic` |
| R1's fan-out measures the harness, not the product | workers resolve mostly-disjoint keys through the real `FlowTable`; the fake-vs-real guard A/B anchors the interpretation |
| IPv6 builder mistakes | the builder gets its own checksum oracle assertion (the repo already has an independent `Sum`/`Finish` test helper) |
| Harness line budget | new scenario/benchmark files, not growth of `SoakOptions.cs`/`BenchmarkShared.cs` beyond the limit |

## 5. Rollout

Each requirement lands as its own commit with its baseline artifact recorded under
`benchmarks/results/2026-09-29-benchmark-coverage/`; `benchmarks/README.md` gains the row/scenario
documentation and the gate/report classification. Nothing in this task changes product behaviour, so
rollback is deleting the added files.
