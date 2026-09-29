# 2026-09-29 benchmark coverage for the remaining structural findings (F2–F8)

Task `09-29-benchmark-coverage-remaining-findings` (parent `08-30-proxy-perf-stability`). This
directory holds the **before** baselines the remaining research findings
(`archive/2026-09/09-29-tcp-udp-path-structural-perf/research.md`: F2 locks/scaling, F3 sweeps,
F4 keys/parsing, F5 pump I/O, F6 UDP residency, F8 pump-thread attribution) are judged against.

No product behaviour changed in this task: everything here is instrumentation plus recorded numbers.

**Reading convention for the multi-run tables**: each artifact file holds the runs it names — the JSONL
series contain every run taken, while the BenchmarkDotNet `.md`/`.csv` pairs hold the **latest** run of
their family (BDN's export path overwrites). Where a table quotes an earlier run whose report was
superseded by a re-export, the column is labelled by run number and cannot be re-derived from the archive;
the values were exact when recorded, and every later run reproduced them within the stated noise. Rows
whose earlier run is quoted *and* still on disk are labelled accordingly.

## Host / runtime

NixOS 26.11 (Zokor), .NET 10.0.12, X64. Numbers are not comparable across hosts; the repository's
standing rule applies — allocation bytes and counts are exact gates, timing and throughput are series
comparisons (treat <2× as noise).

## Artifacts

### `scaling-contention.jsonl` — R1, the lock chain's scaling curve (research F2)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario scaling --quick --flows 4096 --duration 45 \
  --output benchmarks/results/2026-09-29-benchmark-coverage/scaling-contention.jsonl
```

Shape: one shared `FlowTable` seeded with 4,096 live states plus one self-traffic registry; 1/2/4
dedicated worker threads released from a common gate, each walking its own key partition with every
10th lookup aimed at a 409-key shared pool. The measured unit is the pair a warm packet pays —
`ISelfTrafficGuard.IsOwned` then `FlowTable.TryResolve`. Each configuration is measured twice: with
`NeverOwnedGuard` (the guard every existing dispatcher row uses) and with a real populated
`SelfTrafficRegistry`.

| arm | 1 thread | 2 threads | 4 threads | scaling ratio (1 / 2 / 4) |
|---|---:|---:|---:|---|
| `NeverOwnedGuard` (fake) | 6,766,983 /s | 4,641,784 /s | 4,273,210 /s | 1 / 0.343 / 0.158 |
| real `SelfTrafficRegistry` | 3,331,758 /s | 3,398,317 /s | 2,699,595 /s | 1 / 0.510 / 0.203 |

Two readings, both load-bearing for the F2 work:

1. **The fake-collaborator distortion is real and large.** At one thread the real guard costs ~2× the
   fake one (3.33M vs 6.77M resolutions/s ≈ +150 ns per lookup). Any row built on `NeverOwnedGuard`
   therefore reports a **constant zero delta** for the self-traffic reorder (F2.1) — which is exactly
   what `Perf/DispatcherBenchmarks.cs` does today.
2. **The scaling claim is now falsifiable.** Aggregate throughput *falls* as workers are added (ratio
   0.158–0.203 at four threads): the shared flow-table and registry locks serialize every worker, which
   is research F2's core claim, measured instead of assumed.

Report-only: the verdict row carries `gated: false`. Run 3× and quote the median when comparing before
and after an optimization; a single row on a loaded host is not a result.

### `flow-table-production-shape.md` — R2, the flow table at the shipped cardinality (research F4)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short
```

`FlowTableProductionShapeBenchmarks`, cardinality 4,096 (the default `tcpFlowCapacity`), 0 B allocated
on every row. Two `--job short` runs (3 iterations each; the spread is the point — a short job is not a
precise instrument, and the repository's own rule is that sub-2× timing deltas are noise):

| Row | run 1 | run 2 | run 3 |
|---|---:|---:|---:|
| `ResolveSameOrientationHit` | 105.10 ns | 92.65 ns | 117.29 ns |
| `ResolveReverseAliasHit` | 137.86 ns | 134.80 ns | 138.42 ns |
| `ReadActivityClock` | 40.25 ns | 40.74 ns | 40.56 ns |

Three `--job short` runs (3 iterations each), and the spread is itself a finding: the **clock row is the
stable one** (40.3–40.7 ns) and the reverse-alias row is nearly as stable (134.8–138.4 ns), while the
**same-orientation hit moves 92.7–117.3 ns — a 26 % swing** on a shared host. So:

- the per-hit activity clock is **40 ns, ~34–43 % of a same-orientation hit** — the headroom the bucketed
  activity time (F3.4) is aiming at, and measurable with confidence;
- the reverse orientation — the shape every proxied reply packet takes — costs **+21–42 ns** over the
  stored orientation (the second index lookup), the baseline for the direction-normalized hash
  (F2.3/F4.1);
- **do not use the same-orientation hit row for a delta without a full (non-`short`) job or 3× medians** —
  on a short job its own run-to-run spread is larger than most of the deltas this task exists to measure. The **claim** path is not a row here on purpose:
BenchmarkDotNet invokes one method many times per iteration and the table has no per-invocation reset, so
a "new key" row would silently turn into a resolve row; the claim cost is measured by the controlled loop
in the sweep/churn scenarios instead.

### `udp-ready-path-contention.md` — R1b, the UDP ready path under contention (research F2.4)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpReadyPathContention*' --job short
```

`UdpReadyPathContentionBenchmarks`, 4,096 datagrams per invocation split across pre-established flows
driven on dedicated threads (a counting fake transport, so no socket work is in the number), 0 B
allocated on every row:

| Workers | run 1 | run 2 |
|---|---:|---:|
| 1 | 350.2 ns | 361.4 ns |
| 2 | 405.8 ns | 403.6 ns |
| 4 | 498.8 ns | 523.3 ns |

Reading: per-datagram cost grows **+40–45 % at four workers** — the coordinator gate and the session lookup
serialize ready-path sends, exactly the contention F2.4 describes. A path that scaled cleanly would hold
~350 ns. The existing UDP scenarios cannot show this: `udp.lossRate` / `udp.rawBaseline` run at the
~25k pps loopback ceiling, where socket work hides the lock cost. The transport's own send gate is out of
scope here (a fake transport replaces it) and remains covered on the wire by the soak rows.

### `sweep-pause.jsonl` — R3, the sweep's stop-the-world pause (research F3)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario sweep --quick --tcp-concurrency 4 \
  --output benchmarks/results/2026-09-29-benchmark-coverage/sweep-pause.jsonl
```

`flowTable.sweepPause` + `flowTable.claim`: a 65,536-entry table is refilled with already-expired states
and swept with the production call (`FlowTable.RemoveExpired` plus the coordinator's "still held?"
predicate) while four observer threads resolve live keys, 15 s:

| Metric | run 1 | run 2 | Research target |
|---|---:|---:|---:|
| Sweeps of 65,536 expired flows | 146 | 141 | — |
| Sweep wall time, mean / max | 25.1 / 87.1 ms | 26.3 / 86.8 ms | — |
| **Resolve pause, max** | **39.5 ms** | **40.2 ms** | **0.5 ms** |
| Resolves over 100 µs / 500 µs | 41,704 / 25,284 | 30,457 / 21,252 | — |
| Resolves over 1 ms / 5 ms | 14,022 / 828 | 13,949 / 1,311 | — |
| Sweep allocation per sweep | **0 B** | **0 B** | 0 B |
| Claim cost (fresh table) | 2,339.7 ns / **192.02 B** | 1,993.1 ns / **192.02 B** | — |

The two runs agree on everything that matters: the pause is 40 ms against a 0.5 ms target, the sweep
allocates nothing, and the per-claim allocation is byte-identical (192.02 B) while the per-claim time
moves 15 % — the same "exact counts are gates, timing is a series" split the rest of the repository uses.

Readings, all of them load-bearing for F3:

- the pause target is missed by **~80×**: a single sweep blocks a `TryResolve` for up to **40 ms**, and
  ~14,000 resolves crossed 1 ms in 15 s. The stop-the-world scan is not a theoretical concern;
- the sweep itself is **already allocation-free** (0 B/sweep over 8 samples) — `FlowTable` collects
  expired keys into a reused scratch list, so the xunit gate
  (`SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes`) holds this exactly, and its
  discriminating power is proven two ways: the companion probe test shows the same measurement path sees
  a known allocation, and injecting a 16-byte allocation into the sweep turns the gate red with
  `Expected: 0, Actual: 40`;
- the claim path costs **192 B and 2.3 µs per new flow** on a table whose state pool is empty — the
  per-claim allocation the A4 memory work is about, measured at the production shape;
- **the other four sweep sites allocate today** and are therefore F3's targets rather than gates:
  `TcpRedirectTable.RemoveExpired` (`Where/Distinct/ToArray`), `TcpRedirectSessionStore.RemoveExpiredAsync`
  (`Where/Select`), `UdpProxyCoordinator.RemoveExpiredAsync` (`Where/Select` + tuple array),
  `UdpAssociationPool.SweepIdleAssociationsAsync` (`SelectMany/Where` + list). Their byte baselines need
  their own compositions (redirect sessions, live UDP associations), so they land with the F3 fix, which
  is also when their gates can be green — a gate added now would be red on an unmodified tree.

### `tcp-churn-baseline.jsonl`, `tcp-churn-attribution-delay.jsonl`, `tcp-churn-attribution-delay-1pct.jsonl` — R4, TCP new-flow churn and the attribution tail (research F8)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario tcpChurn --quick --rate 60 --tcp-concurrency 8 --payload-bytes 4096 \
  --output benchmarks/results/2026-09-29-benchmark-coverage/tcp-churn-baseline.jsonl

# the same load with a 50 ms synthetic per-flow stall on 5 % / 1 % of connections
  ... --attribution-delay-ms 50 --attribution-delay-percent 5 \
  --output benchmarks/results/2026-09-29-benchmark-coverage/tcp-churn-attribution-delay.jsonl
  ... --attribution-delay-ms 50 --attribution-delay-percent 1 \
  --output benchmarks/results/2026-09-29-benchmark-coverage/tcp-churn-attribution-delay-1pct.jsonl
```

Each connection is dialled, SOCKS5-established, sent 4,096 B and closed; the clock covers everything the
client waits for (dial → per-flow setup → handshake → first byte), which is where pump-side attribution
would sit. Two series of `--quick` runs at 60 connections/s. **Series 2 is the one to read** (it is the
final code; series 1 agreed within noise):

| Metric | baseline | 50 ms on 5 % | 50 ms on 1 % |
|---|---:|---:|---:|
| Attempts / successes / failures | 921 / 921 / 0 | 937 / 937 / 0 | 922 / 922 / 0 |
| Stable class: count, mean, p50 | 921, **2.336**, 1.204 ms | 891, **2.365**, 1.224 ms | 913, **2.207**, 1.132 ms |
| Stable class: p95 / p99 | 3.445 / 8.308 ms | 2.261 / 10.068 ms | 2.676 / 7.254 ms |
| Delayed class: count, mean, p50 | — | 46, **51.749**, 51.708 ms | 9, **52.301**, 52.300 ms |
| Delayed class: max | — | 54.489 ms | 52.647 ms |
| Allocated per connection | ~91 KB | ~91 KB | ~91 KB |

Readings:

- **the tail-only signature is reproduced**: the stable-class mean is **2.34 / 2.36 / 2.21 ms across the
  three runs** — indistinguishable — while the delayed class sits a full 50 ms above it. Pooled over all
  connections the 1 % run moves the p99 from **8.3 ms to ~52 ms (≈6×)** for a mean of **2.34 → ~2.70 ms
  (+15 %)**. That is precisely the "p99 moves, the mean barely does" property the F8 instrument needs, and
  it is why the classes are reported separately: the stable class is the control that proves the load
  itself did not change;
- **the allocation column is the other half of F8**: **~91 KB of transient garbage per new flow**, 5 Gen0
  collections and ~5.45 MB/s at 60 connections/s — the MB/s-scale churn garbage the finding describes, now
  measured rather than estimated;
- **the scenario cross-checks itself**: `serverConnectReplies == attempts` and
  `serverBytesEchoed == receivedBytes` byte-for-byte in every run, so "922 connections" is corroborated by
  the server rather than only counted by the client;
- caveat worth carrying forward: the baseline `maxMs` is ~100 ms while its p99 is 8.3 ms — one outlier
  (host scheduling or a GC pause) dominates the maximum, so **compare p99, not max**, on this row.

### `tcp-redirect-data-path.md` / `parser.md` — R5, the composed per-packet redirect cost and the IPv6 parser rows (research F1 follow-up / F4)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRedirectDataPath*' '*Parser*' --job short
```

`TcpRedirectDataPathBenchmarks` composes the real per-packet work of a proxied TCP frame —
`TcpSequenceObservation.TrackClientSequence` (forward) or `TrackServerSequence` (reverse; `RecordServerSynAck` runs only on the SYN-ACK edge, which a mid-flow row does not model)
(reverse), then `TcpFrameRewriter.TryRewriteForwardLeg` or `PacketChecksums.TryRewriteTcpEndpoints` +
`SwapEthernetMacs` — for host/forwarded association shapes × IPv4/IPv6 × 128/1400 B. `[GlobalSetup]`
proves every row's rewrite **succeeds** on a pristine frame before anything is timed, so no row can
report the (faster) rejection path as a result. The driver send is deliberately out of scope: it is an
IOCTL, and a counting fake would measure the fake. `parser.md` carries the parser family with its new
IPv6 UDP rows (`Ipv6UdpTryParse` / `Ipv6UdpPayload`) built by `BenchmarkShared.CreateIpv6UdpFrame`;
both UDP builders are parse-only shapes, which is why the UDP checksum stays zero — neither production
parser reads it.

Three `--job short` runs of that command (3 iterations each; r1 / r2 / r3 below, the archived files are
r3). The pre-change archive this replaces (same class, before the parser rows landed) read IPv4 51–130 ns
and IPv6 94–113 ns — the same bands, so the refresh did not move the series:

| Leg | IPv4 128 B (r1 / r2 / r3) | IPv4 1400 B | IPv6 128 B | IPv6 1400 B |
|---|---:|---:|---:|---:|
| `ForwardLegHost` | 119.10 / 119.46 / 116.01 ns | 129.19 / 126.22 / 118.60 ns | 105.87 / 105.41 / 106.21 ns | 111.47 / 122.31 / 111.20 ns |
| `ForwardLegForwarded` | 94.32 / 96.50 / 92.43 ns | 101.38 / 101.35 / 102.90 ns | 88.06 / 88.34 / 87.48 ns | 93.44 / 93.79 / 91.52 ns |
| `ReverseLegHost` | 63.93 / 63.38 / 62.24 ns | 67.91 / 70.56 / 68.63 ns | 89.52 / 88.67 / 87.69 ns | 99.54 / 98.48 / 96.22 ns |
| `ReverseLegForwarded` | 51.49 / 52.19 / 51.12 ns | 59.26 / 57.96 / 58.03 ns | 103.42 / 103.54 / 103.50 ns | 112.84 / 122.78 / 112.02 ns |

| Parser row | 64 B (r1 / r2 / r3) | 512 B | 1514 B |
|---|---:|---:|---:|
| `Ipv4UdpTryParse` | 12.47 / 11.47 / 11.49 ns | 11.63 / 11.41 / 11.41 ns | 12.25 / 18.56 / 12.36 ns |
| `Ipv4UdpPayload` | 12.76 / 11.82 / 10.64 ns | 12.74 / 10.67 / 10.72 ns | 10.80 / 10.80 / 10.70 ns |
| `Ipv6UdpTryParse` | 12.57 / 10.59 / 10.58 ns | 10.47 / 10.44 / 11.59 ns | 11.44 / **44.91** / 10.66 ns |
| `Ipv6UdpPayload` | 11.45 / 9.88 / 9.87 ns | 10.10 / 9.87 / 10.50 ns | 12.89 / 9.89 / 9.77 ns |

Readings:

- **Every case allocates 0 B** — the archived CSVs' `Allocated` column is `0 B` on all 16 data-path
  cases and all 18 parser cases, in both families, every shape and every frame size. The composed path
  is CPU-only, so F4's parse-once work has to be judged on ns: the byte column has no headroom left to
  move. Design §3 makes that zero a **gate** rather than a report — `TcpRedirectDataPathAllocationGateTests`
  drives each of the four composed legs once per address family at 1400 B, after warm-up, against
  `GC.GetAllocatedBytesForCurrentThread()`, so a leg that starts allocating fails the build.
- **The frame size is irrelevant to the number.** Growing the frame 128 → 1400 B (11×) raises every
  leg's median by 2–14 % (largest absolute gap 10.5 ns, e.g. `ReverseLegHost` IPv6 88.7 → 98.5 ns), and the parser
  rows are flatter still (9.9–11.6 ns across 64/512/1514 B, some cells lower at the larger size) —
  both rewrites and both parsers work on headers and never read the payload. That is F4.2's argument in
  one row: what parse-once can remove is the repeated header walks, not payload work. (A
  payload-proportional effect would have to show ~10× at this size ratio.)
- **IPv6 costs roughly the same as IPv4 on three of four legs, and exactly 2× on the fourth — and the
  fourth is real.** `ForwardLegHost`, `ForwardLegForwarded` and `ReverseLegHost` sit within 1.4× of
  their IPv4 twins (IPv6 even *wins* both forward rows by ~7–11 %), but `ReverseLegForwarded` is a
  reproducible **2.0×**: IPv4 51.5 / 52.2 / 51.1 ns against IPv6 103.4 / 103.5 / 103.5 ns at 128 B, the
  same ratio at 1400 B, stable to ~1 % at 128 B across three runs plus the pre-change archive. The code
  explains it: `PacketChecksums.TryRewriteIpv6Tcp` copies the 32 pseudo-header address bytes into a
  `stackalloc ushort[16]` via `FillAddressWords` and folds 16 word deltas in a loop, while
  `TryRewriteIpv4Tcp` keeps four address words in locals and folds them inline. At the 2× line, stable,
  and mechanism-backed, this is a finding for the F4 work (the IPv6 fold is an inlining candidate), not
  harness noise.
- **The parser rows mirror the composed picture**: IPv6 UDP parse/payload is 9.9–11.4 ns against IPv4's
  10.7–12.4 ns (medians), and constant across 64/512/1514 B — a fixed header walk in both families,
  every row 0 B. The new rows are also the harness half of the "IPv6 has no perf rows" gap: the same
  builder feeds them and the data-path rows, and `BenchmarkFrameBuilderTests` pins both parsers
  accepting it structurally.
- **Caveat, sharper than the usual ±20 %**: `--job short` is 3 iterations per row (3 warmups, 1
  launch), and r2's `Ipv6UdpTryParse` at 1514 B measured **44.91 ns** where r1 and r3 measured 11.44
  and 10.66 ns — a 4× swing on a row whose siblings did not move, i.e. a scheduling artifact of a
  3-iteration job. Single short-job rows are not results: quote medians, and only believe a ≥2× delta
  that reproduces across runs (the `ReverseLegForwarded` gap above is quoted precisely because it does).

### `pump-idle-wake.jsonl` — R6, pump idle cost, wake latency and the read-call seam (research F5)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario pump --quick \
  --output benchmarks/results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl
```

Three consecutive `--quick` runs in one file (each run carries its own metadata row). Both rows drive the
real `NdisCapturePump` through a fake reader: the idle row polls an always-empty queue for 15 s with the
production 1 ms `PollDelay`, and the wake row parks `TryReadPackets` on a semaphore until the harness arms
exactly one frame — 5,000 measured wakes after 500 unrecorded warmups. Each wake's arrival is the
harness's timestamp immediately before arming, its dispatch is the handler's entry timestamp on the pump
thread, and the harness confirms the reader is parked before arming, so every sample is a real
blocked→ready transition rather than a hot semaphore pass (the hot shape measures sub-microsecond and
would say nothing about F5.2).

| Metric | run 1 | run 2 | run 3 |
|---|---:|---:|---:|
| Idle window / `polls` / `pollsPerSecond` | 15.001 s / 13,165 / 877.6 | 15.001 s / 13,284 / 885.6 | 15.001 s / 13,288 / 885.8 |
| `cpuSeconds` / `cpuSecondsPerIdleSecond` | 0.267 / **0.01780** | 0.2775 / **0.01850** | 0.2674 / **0.01783** |
| `allocatedBytes` (the row's own gate) | **0** | **0** | **0** |
| Idle `readCallsPerPoll` / `readCallsPerPacket` | **1.0** / 0 (no packets) | **1.0** / 0 | **1.0** / 0 |
| Wake p50 / p95 | 0.0779 / 0.1268 ms | 0.0783 / 0.1239 ms | 0.0791 / 0.1221 ms |
| Wake p99 / max / mean | 0.1890 / 0.5844 / 0.0840 ms | 0.1877 / 0.8211 / 0.0836 ms | 0.1662 / 0.7125 / 0.0835 ms |
| Wake `readsPerWake` / `readCallsPerPacket` / `emptyReads` | **1.0** / **1.0** / 1 | **1.0** / **1.0** / 1 | **1.0** / **1.0** / 1 |

Readings:

- **The idle row's gate is exact and it holds: 0 B allocated in all three windows.** A pump polled at its
  production cadence for 15 s touches the managed heap zero times, measured with
  `GC.GetTotalAllocatedBytes(precise: true)` around the window and enforced in the row — a non-zero delta
  throws instead of being reported as a number. That carries the existing
  `IdlePollIterationsAllocateNoManagedBytes` invariant from the test seam to the real run loop. (The
  first version of this row failed its own gate with exactly 40 B: the harness started its `Stopwatch`
  *after* the allocation snapshot. The tool caught its own instrumentation, which is what the gate is
  for.)
- **Idle CPU is ~1.8 % of one core, and it is dominated by `Thread.Sleep` resolution, not by pump work.**
  ~13,200 polls in 15 s is ~880 polls/s — 1.13 ms per poll, i.e. the 1 ms `Thread.Sleep` request resolving
  long — and the 0.267–0.278 s of CPU that comes with it is ~20 µs per poll: the timer arming plus the
  context switch in and out of the sleep. The pump's own loop body (one read, one callback, one sleep) is
  the same code the 0 B gate covers, and it allocates nothing; treat the CPU figure as the cost of the
  **polling cadence**, not of a packet. On a Windows host at 1 ms timer resolution the cadence is closer
  to the requested delay, so this row is a cross-host series and not a portable constant.
- **A blocked-read wake costs ~0.08 ms at the median and ~0.19 ms at p99** (0.078/0.124/0.188 ms for
  p50/p95/p99; max 0.58–0.82 ms on a shared host). That is the number F5.2 is arguing about: with the
  current shape an arriving packet waits up to one full poll delay (nominally 1 ms, ~1.13 ms here) before
  the next read, while the event-driven wake shape costs an order of magnitude less. The max column is a
  host-scheduling artifact (a single outlier per 5,000 wakes), so compare p99, not max.
- **The read-call accounting is exactly one call per poll and one per packet, on both rows.** Idle:
  13,165–13,288 read calls for 13,165–13,288 polls (`readCallsPerPoll` 1.0, `packets` 0); wake: 5,000 read
  calls, 5,000 packets and 5,000 wake dispatches for 5,000 wakes (`readsPerWake` and `readCallsPerPacket`
  1.0), with the run's single terminating empty poll reported as `emptyReads: 1`. `CapturePumpReadCallTests`
  gates the same pattern deterministically in xunit (exact counts over a scripted reader, plus the 0 B idle
  invariant for the counting shape), so an F5.1 change that starts issuing a second read per poll turns the
  test red rather than this row.
- **The caveat the F5.1 half inherits: the driver's own IOCTL pair is below this seam.** The pump's only
  read seam is `INdisPacketReader.TryReadPackets`; `NdisApiDriver.TryReadPackets` then performs
  `GetAdapterPacketQueueSize` and, only for a non-empty queue, `ReadPacketsBatch` — a query+read pair
  inside the driver that no harness-side reader can count. So "speculative read halves the IOCTLs under
  load" is only assertable on Windows against the real driver; what is established here is that the pump
  adds no read of its own beyond one per poll, which makes the driver-internal pair the entire remaining
  target.
- **Timing here is a series, not a gate** (design §3): the three runs agree closely (idle CPU within 4 %,
  wake p50 within 2 %), but latency and CPU on a shared host are comparison numbers. The exact
  quantities are the 0 B row gate and the xunit call counts. Total runtime is ~23 s for `--quick`
  (15 s idle window + 6.1 s of wake round trips).

### `residency-census.jsonl` — R7, the live-residency census (research A1/A4, F6's TCP half)

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario residency --quick --flows 100 --udp-flows 100 \
  --output benchmarks/results/2026-09-29-benchmark-coverage/residency-census.jsonl
```

Three consecutive `--quick` runs in one file (each run carries its own metadata row). Four samples in
order — a zero-flow **baseline**, the **flow table** at its production 65,536-state capacity seeded
with 100 live states through the real claim call, 100 real **loopback SOCKS5 relays held open** (two
64 KiB native pump windows each), and 100 **UDP coordinator sessions over fake transports** — with
`GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();` immediately before every sample. The
populations are cumulative, so the row to read is `vsPreviousStage` (the delta attributable to that
stage's own population), not `vsBaseline` (which still contains every earlier stage). Every stage
proves its population before it samples and aborts the run otherwise: `table.Count == --flows`, the
loopback server's CONNECT-reply counter (awaited, because its connection task increments it after the
establishing call returns), and the fake transport factory's created count cross-checked against the
coordinator's own `SessionCount`. Nothing else can fail the run — `gated: false` in the verdict row.

Absolute levels, run 1 / run 2 / run 3 (bytes):

| Stage | managed | GC committed | working set | private (`VmData`) | descriptors | gen 0/1/2 |
|---|---:|---:|---:|---:|---:|---:|
| baseline (population 0) | 315,104 / 308,624 / 308,624 | 1,269,760 / 1,204,224 / 1,204,224 | 41.89 / 41.93 / 41.96 M | 85.6 / 85.5 / 85.5 M | 64 | 3 / 3 / 3 |
| flow table (100 live states) | 32,834,456 / 32,834,456 / 32,822,168 | 33.6 / 33.7 / 33.6 M | 46.22 / 46.35 / 46.26 M | 127.4 / 127.4 / 127.4 M | 66 | 7 / 7 / 7 |
| + 100 TCP relays | 40,381,872 / 40,381,320 / 40,375,696 | 45.0 / 45.0 / 45.0 M | 62.2 / 60.9 / 61.1 M | 212.8 / 203.8 / 212.4 M | 488 | 10 / 10 / 10 |
| + 100 UDP sessions | 40,807,408 / 40,794,280 / 40,801,608 | 42.8 / 42.7 / 42.8 M | 66.4 / 65.0 / 65.5 M | 835.6 / 809.4 / 826.6 M | 488 | 13 / 13 / 13 |

Marginal per live flow (`vsPreviousStage.perFlow`), run 1 / run 2 / run 3:

| Stage (its own population) | managed B/flow | working set B/flow | Δ descriptors | descriptors/flow |
|---|---:|---:|---:|---:|
| flow table, 100 live states (capacity-dominated) | 325,193.5 / 325,258.3 / 325,135.4 | 43,294.7 / 44,236.8 / 43,049.0 | +2 total | 0.02 |
| TCP relays, 100 | 75,474.2 / 75,468.6 / 75,535.3 | **159,866.9 / 145,858.6 / 148,807.7** | +422 | **4.22** |
| UDP sessions, 100 (fake transports) | **4,255.4 / 4,129.6 / 4,259.1** | 42,311.7 / 41,082.9 / 43,786.2 | 0 | 0 |

Population scaling check (one run each at `--flows 1 / 100 / 1000`, same command with the population
swapped) — this is what separates a population's cost from a capacity's:

| `--flows` | flow-table managed delta | relay marginal working set | relay marginal managed | relay Δ descriptors |
|---:|---:|---:|---:|---:|
| 1 | 32,484,096 | 4,730,880 | 130,896 | +26 |
| 100 | 32,519,352 | 15,986,688 | 7,547,416 | +422 |
| 1000 | 32,734,632 | 104,112,128 | 78,002,752 | +4,022 |

Readings:

- **The flow table's residency is its capacity, not its population — and A1 item 1a's estimate was
  low.** Creating the table costs **32.5 MB of managed heap (+32.4 MB of GC-committed bytes)** whether
  it holds 1, 100 or 1000 live states: the 999 extra states between the extremes add 250,536 B, i.e.
  **≈251 B per live state**. At `--flows 100` the naive `(sample − baseline) / population` figure is
  therefore **325 KB per live flow, ~1,300× the states' own cost** — exactly the reading the row's own
  note warns against, and the reason the census reports `flowTableCount`/`flowTableCapacity` beside it.
  A1's ~20 MB estimate for `_states` + `_transportIndex` + `_freeStates` prices the table at ~32.5 MB
  measured; this is the number the A4 rebuild has to move, and the 251 B/state is what a live-state
  pool costs on the current design (against the sweep scenario's 192 B per *claim*).
- **The relay-window claim is confirmed, in the working-set column.** A held-open relay adds
  **146–160 KB of working set** — the two 64 KiB pump windows plus its resident share of the ~75.5 KB
  of managed async state a pending pump pair holds — and **4.22 descriptors**. The A4 item-9 target
  (~128 KiB per connection, "an idle SSH connection costs the same as a saturated one") is measured
  here rather than assumed, and the relay stage moves GC-committed bytes by only ~11.3 MB per 100
  relays: the windows are native and are **not** on the managed heap, which is why the stage's
  `managedBytes` figure (~75 KB/relay) must not be read as the window cost. The descriptor count is
  ~4.2 per relay at 100 relays (4.02 at 1000), not 1: two are the harness's own client/accepted socket
  pair, one is the upstream SOCKS5 control socket, one is the loopback server's accepted side, and the
  small excess is the runtime's own descriptors that appear with the first relay.
- **The first relay is not a relay, and the hundredth is cheaper than the first.** At `--flows 1` the
  relay stage moves +4.73 MB of working set and +26 descriptors (thread pool, allocator arenas, the
  SOCKS5 stack); at 1000 relays the marginal is 104 KB and 4.02 descriptors per relay, and the
  100 → 1000 marginal is **97.9 KB / 4.0 descriptors per relay**. So a census at one or two live
  objects measures startup, not the object — the honest per-relay figure is the 1000-point's 104 KB,
  and the 100-point's 146–160 KB carries ~4.7 MB of one-off cost. The same shape applies to the UDP
  stage (43 KB/session at 100 sessions, 11.5 KB/session at 1000): these are the "dominated by
  something other than the population" cases the census exists to expose.
- **The spread, quoted against the effect size.** The managed column is stable to **0.04–0.09 %**
  (flow table 325,135–325,258 B/flow; relays 75,469–75,535 B/flow) and the descriptor deltas are
  **exact** (422 and 0 in all three runs, as counts always are). The working-set column moves
  **±4.6 %** run to run for relays (145.9–159.9 KB/flow) and **±3 %** for UDP sessions
  (41.1–43.8 KB/flow), so on this host **a working-set difference under ~10 % is not a result** —
  the same "exact counts are gates, bytes on the heap are a series" split the rest of the directory
  uses, with the added caveat that the native column is the noisy one.
- **`PrivateMemorySize64` is reported, but it is not a per-population column on Linux.** It reads
  `/proc/self/status`'s `VmData`, which also carries glibc's per-thread arena reservations: the
  100-session UDP stage shows **+614 MB (+6.1 MB/session)** while its working set moves +4.3 MB.
  Verified separately (a probe printing both counters at each step): `PrivateMemorySize64` equalled
  `VmData` exactly (72 MB vs 73,728 kB at start), 200 × 64 KiB native allocations on one thread moved
  `VmData` +21 MB and RSS +10 MB, and 32 threads allocating 4 × 64 KiB each moved `VmData` **+266 MB**
  while RSS moved **+1.7 MB**. The column is in the row because R7 asks for it; every per-flow reading
  above comes from `managedBytes`, `workingSetBytes` and the descriptor delta instead.
- **The UDP half is F6's session residency, not its descriptor number.** 100 fake-transport sessions
  cost **4.2 KB managed and 41–44 KB of working set each** (session/slot/association/lease objects
  plus the per-session setup-queue and receive-window buffers) and **0 descriptors** — a fake
  transport opens no socket, so this row cannot carry the real dial path's descriptor cost; the
  09-28 `udp.sessionBudget` series measured **1.070 descriptors per live session**, which is the
  number to compare against TCP's 4.22. GC-committed bytes can even move *negative* between stages
  (the UDP stage reads −2.2 MB): committed bytes are segment-granular and the runtime decommits on
  its own schedule, so only the managed-heap and working-set columns are interpretable at this
  granularity.

## A note on the JetBrains inspection gate for the harness projects

On this tree, `jb inspectcode -f=Xml -e=HINT WinForward.slnx` reports **zero `<Issue>` entries and zero
`CSharpErrors`** — the whole solution, harness and test projects included. That state is reproducible
from a **warm** cache and took one round of fixes: such a run reported exactly **13 genuine findings,
none of them in a product project**.

- **3 redundant directives** — `WinForward.Runtime.Socks5` in `TcpChurnScenario.cs`,
  `WinForward.Core` in `BenchmarkFrameBuilderTests.cs` and `SweepAllocationGateTests.cs`. Each was
  verified by deleting it and rebuilding (Release stays at 0 warnings), not by trusting the analyzer.
- **1 logical-pattern merge** (`BenchmarkShared.CreateIpv6TcpFrame`'s range guard now matches its three
  sibling builders), **2 redundant casts** (`(uint)tcpLength` in that IPv6 builder,
  `(uint)payloadLength` in the checksum test), and **2 `ConvertToAutoPropertyWhenPossible`** in the
  read-call probe.
- **1 `MemberCanBePrivate.Local`** (`UdpCensusStage.Sessions`, used only inside its own nested class)
  and **1 wrong nullable annotation**: `ChurnCounters._failureReasons` is `string?[]` because its
  unused slots are null by construction, so the snapshot's null filter is live and the analyzer's
  "always true" proof rested on a contract the field did not actually have.
- **3 narrow suppressions with reasons**, where the analyzer cannot see the invariant:
  `DisposeOnUsingVariable` ×2 in `PumpIdleWakeScenario` (the stop must be initiated before the run is
  awaited or the parked poll never ends; `NdisCapturePump.DisposeAsync` is idempotent, so the
  `await using` dispose is the exception-path net) and `AccessToDisposedClosure` in
  `ResidencyCensusScenario` (the awaited poll returns before the `await using` scope disposes the
  server) — the same treatment `ScalingContentionScenario` and `SweepPauseScenario` already carry.
  `TcpRedirectDataPathBenchmarks`' two `[Params]` properties carry a `PropertyCanBeMadeInitOnly.Global`
  disable/restore pair for the same reason `.editorconfig` suppresses the `[Params]` accessor
  inspection under `Perf/**`: BenchmarkDotNet writes them through reflection, so the setters must stay
  settable (the R5 gate below reuses that class, which is what makes the static analysis see a write).

The earlier **57-`CSharpErrors`** reading for these projects came from a **cold** run: with
`~/.local/share/JetBrains/` and `/tmp/JB` cleared, the analysis lands on pre-existing, compiling code
(`new NeverOwnedGuard()`, `ThresholdOnlyLogger` "has no constructors defined", a required
`using Xunit;` called redundant) and cascades unused-member findings from there. It was the empty cache
warming up, not evidence that the harness cannot be analyzed — the same tree with a warm cache reports
no `CSharpErrors`, and the 13 findings above were genuine. The run's log does still reproduce a
third-party Roslyn provider load failure (`System.Composition.AttributedModel`
`FileNotFoundException`); it does not stop the analysis or change the report.
