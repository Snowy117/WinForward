# Benchmark coverage for the remaining structural findings (F2–F8)

Parent: `08-30-proxy-perf-stability`. Source finding set:
`.trellis/tasks/archive/2026-09/09-29-tcp-udp-path-structural-perf/research.md`
(F2 locks, F3 sweeps, F4 keys/parsing, F5 pump I/O, F6 UDP residency, F8 pump-thread attribution,
plus the A1/A4 memory items and roadmap 7–12).

## Goal

Make the remaining structural findings **falsifiable before they are optimized**: every one of them
names a measurement that today either does not exist, or exists only in a shape that cannot see the
effect (a fake collaborator). This task builds that instrumentation and records the "before" baselines,
and changes **no product behaviour**.

Why now: the first finding (F1) was implemented and verified with deterministic counting/allocation
gates, but the audit below shows three of the six remaining findings have *zero* existing coverage and
one has a measurement that reports a **constant zero delta** by construction. Optimizing against that
would produce unverifiable work.

## Background — coverage audit (verified against the tree, 2026-09-29)

| Finding | Measurement the research names | What exists today | Verdict |
|---|---|---|---|
| F2 lock chain / multi-adapter scaling | `FlowTableBenchmarks` + `DispatcherBenchmarks` A/B under 1/2/4 pump threads (research: "new multi-thread rows needed — current benchmarks are single-threaded"); `SelfTrafficBenchmarks` for the reorder | `FlowTableBenchmarks` has exactly two rows: `ResolveMissing` and `ResolveCrossAdapterHit`, both single-threaded (`Perf/FlowTableBenchmarks.cs`). `DispatcherBenchmarks` wires `NeverOwnedGuard` (a guard that never owns anything) + `CountingExecutor` (`Perf/DispatcherBenchmarks.cs:50,71-75`). No thread/contention row exists anywhere in `Perf/` | **zero coverage**, and the self-traffic reorder's gain is **structurally invisible** (the fake guard removes the code under test) |
| F3 O(N) stop-the-world sweeps | "sweep pause = max time a `TryResolve` blocks during a sweep tick at 65k seeded flows (assert < 0.5 ms)"; sweep allocation asserted zero | No flow-table sweep measurement at all; `RemoveExpired` is never exercised by any benchmark. `SessionSetupDecompositionBenchmarks` T3 covers the **UDP coordinator's** retire, not the flow table | **zero coverage** |
| F4 fat keys / repeated parsing | `FlowTableBenchmarks` probe ns; `ParserBenchmarks`; warm-path 160 B gate | `ParserBenchmarks` is IPv4-only. `FlowTableBenchmarks` has no `TryClaimResolved` (claim), no reverse-alias hit, no `Touch`/clock isolation. No composed per-packet TCP path (classify → sequence-track → rewrite) anywhere | **partial** |
| F5 pump I/O shape | "`CapturePumpBenchmarks` extended with an idle-CPU row and a wake-latency row"; `IdlePollIterationsAllocateNoManagedBytes` stays green | `CapturePumpBenchmarks` has one row (`EndToEndAsync`, 128/1400 B × batch 32/1, Pass config, fake reader). No idle row, no wake-latency row. The read side has **no IOCTL counting** (the driver exposes batched-*send* telemetry only) | **zero coverage** |
| F6 UDP per-session residency | `udp.sessionFootprint` / `udp.sessionBudget` | Adequate: footprint at 1/100/1000 sessions, and the sessionBudget soak with the kernel-buffer estimate, descriptor budget and drain-to-zero (1 h artifact recorded) | **adequate** (see R7 for the census half) |
| F8 pump-thread attribution | (research names no benchmark; the cost is 0.5–7 ms per new flow, MB/s of transient garbage) | No TCP new-flow churn scenario exists (`Stability/` has `tcp.unexpectedEof` and `tcp.throughput` only); process attribution is never enabled in any benchmark | **zero coverage** |
| A1/A4 memory (roadmap 7/9/10) | steady-state residency of the flow table, relay windows, UDP sessions; committed bytes | `SessionFootprintScenario` is UDP-only over fake transports; no flow-table residency, no TCP relay-window residency, no `TotalCommittedBytes`, no GC pause duration | **partial** |

### Cross-cutting gaps (these are what make the above hard, not just missing)

1. **Single pump / single thread everywhere.** No `Perf/` row exercises concurrency (the only threads
   are `TcpRelayBenchmarks`' sender task and `UdpSessionBenchmarks`' spin wait). The research's central
   F2 claim — throughput does not scale with adapter count because every pump serializes on the same
   lock lines — is currently unfalsifiable.
2. **The proxy data path has no ns-level benchmark.** `CapturePumpBenchmarks` runs a **Pass** config,
   `DispatcherBenchmarks` runs a **no-op executor**, and `TcpProxyCoordinator`'s rewrite+inject has never
   been benchmarked (F1 substituted unit-level counting gates). The product's main path still has no
   per-packet cost number.
3. **No CPU-time or contention metric.** Every row is wall-clock ns or pps; there is no process CPU time,
   no lock-contention counter, no context-switch evidence. F2 and F5 are both *CPU-cost* claims.
4. **Fake-collaborator distortion** (the repo's own rule: "an allocation gate that uses a fake
   collaborator cannot see a trap inside the real one" — `quality-guidelines.md`). `NeverOwnedGuard`,
   `CountingExecutor` and `NoopUdpResponseSink` mean any new row built on them would report "no change"
   for work that changed exactly that collaborator.

Closed since the 2026-08-30 gap list
(`archive/2026-08/08-30-proxy-perf-stability-research/research/measurement-gaps.md`) and therefore
**not** re-opened here: hours-scale soak (1 h `udp.sessionBudget` recorded), DNS-shaped bursts
(`udp.burstEstablishment`), UDP tail-latency distributions (`firstResponseMs` percentiles). Still open
and deliberately left out: Windows real-NDIS numbers (`windows-real-nic`), real-network conditions,
upstream-failure recovery, IPv6 *functional* coverage, fragmentation trigger rates.

## Requirements

- **R1 Contention and scaling family.** A runnable measurement that drives **1 / 2 / 4** concurrent
  pumps (or dispatcher chains) over **one shared** flow table + self-traffic registry and reports
  per-configuration aggregate throughput and per-thread spread, plus a contention signal (CPU time per
  packet and/or per-thread throughput skew). It must use the **real** `SelfTrafficRegistry` and a
  **real** flow table seeded with live flows. Serves F2, and gives the F2.1 reorder a visible delta.
- **R1b UDP ready-path arm (added while writing design §3.1).** The same 1/2/4-thread shape over the
  **UDP ready path** (`UdpProxyCoordinator` send → session activity gate → transport send, with a fake
  *ready* session/transport so sockets do not dominate): the existing `udp.lossRate`/`udp.rawBaseline`
  pair runs at the ~25k pps loopback ceiling, where F2.4's lock chain is invisible. Serves F2.4.
- **R2 Flow-table probe/shape coverage.** `FlowTableBenchmarks` gains the missing shapes: claim
  (`TryClaimResolved`) on a cold table, reverse-alias hit, `Touch`/clock-call isolation, and a hit at a
  realistic live cardinality. Serves F2/F4.
- **R3 Sweep-pause probe and zero-allocation gate.** Seed 65k live flows, run `RemoveExpired`, and
  report (a) the sweep's own wall time and (b) the maximum and p99 `TryResolve` latency observed while a
  sweeper thread runs — the research's "max time a `TryResolve` blocks during a sweep tick" metric.
  Assert **zero managed allocation per sweep** (exact gate) where the unmodified tree can satisfy it:
  `FlowTable.RemoveExpired` (reused scratch). The other sites the research named —
  `TcpRedirectTable.RemoveExpired`, `TcpRedirectSessionStore.RemoveExpiredAsync`,
  `UdpProxyCoordinator.RemoveExpiredAsync` and `UdpAssociationPool.SweepIdleAssociationsAsync` — all
  allocate today (LINQ retirement sets) and are therefore recorded as F3's **targets**: a gate added now
  would be red on an unmodified tree, which is not a gate but a broken build. Their byte baselines land
  with the fix. Serves F3.
- **R4 TCP new-flow churn scenario.** A stability scenario that creates TCP flows through the real
  redirect path at a configurable rate for a configurable window and reports per-flow first-response /
  first-byte **p50/p95/p99** plus establishment success and per-wave allocation. It must be runnable
  with process rules configured, so pump-thread attribution cost shows up as a latency tail. Serves F8
  and the F2 claim path.
- **R5 Proxy data-path micro.** A BenchmarkDotNet row that composes the real per-packet redirect work —
  sequence tracking → forward-leg rewrite → reverse-leg rewrite → (counting) injection — for the host
  and forwarded association shapes, in IPv4 **and** IPv6, reporting ns and allocation bytes per frame.
  Serves the F1 follow-up and F4.
- **R6 Pump idle/wake measurement.** Extend `CapturePumpBenchmarks` (or a sibling class) with an
  **idle-cost row** (CPU time per idle second at the poll cadence) and a **wake-latency row**
  (arrival → dispatch distribution through a signalling fake reader). Add a read-side call-count gate
  (queue query + batch read per poll) so F5's "halves IOCTLs under load" is assertable without a driver.
- **R7 Live-residency census.** A stability scenario reporting, at a configurable live-flow count, the
  resident cost of: the flow table (states + transport index), TCP relay windows per connection, UDP
  sessions, plus process `TotalCommittedBytes` / working set / GC collection counts. Serves the A1/A4
  memory items and F6's TCP half.
- **R8 No behavioural change and no comparability break.** No product code changes except where a
  measurement seam is required (and then only as an injected seam, never a hot-path branch). Existing
  benchmark row names, parameters and gate thresholds are unchanged; new rows are additive.

## Acceptance Criteria

- [ ] Each of R1–R7 exists as a runnable, documented measurement with its command in
      `benchmarks/README.md`, and each is classified in the design's **gate vs report** table: only
      exact metrics (allocation bytes, GC counts, call counts, event counts) become gates; timing,
      throughput and scaling rows are report-only with recorded baselines, per the repo's existing
      discipline (`benchmarks/README.md`: "allocation bytes are exact gates; treat ns/pps deltas under
      ~2× as noise").
- [ ] A "before" baseline artifact is recorded under `benchmarks/results/2026-09-29-benchmark-coverage/`
      for every new row, with the command that produced it, so each later optimization has a diff target.
- [ ] R1 demonstrates the fake-collaborator fix: the same workload measured through `NeverOwnedGuard`
      and through the real registry, with both numbers recorded (proving the distortion was real).
- [ ] R3's zero-allocation sweep gate fails when a sweep allocation is artificially introduced, and
      passes on the unmodified tree (the gate is discriminating, not tautological).
- [ ] R4 reports a tail-latency distribution that reacts to a deliberately induced pump stall (e.g. a
      synthetic per-flow delay): the p99 must move while the mean does not — the property that makes it
      a usable F8 instrument.
- [ ] R5 covers host + forwarded × IPv4 + IPv6 and reports 0 B per frame for the rewrite legs.
- [ ] Full gates green: Release build zero-warning; `dotnet test -c Release` at the recorded baseline
      plus only the tests this task adds; `dotnet format --verify-no-changes` empty; `jb inspectcode`
      zero `<Issue>`; benchmark-host effective-line budget ≤ 400 per file.
- [ ] Each of the 09-29 research findings has a "how it will be proved" line in this task's design
      citing the new row/scenario (the instrumentation half of the roadmap's `Measurement` sections).

## Out of Scope

- The optimizations themselves (F2–F8) — this task only makes them measurable; each keeps its own task.
- Windows real-NDIS / real-NIC numbers (the `windows-real-nic` candidate), real-network loss/reorder
  conditions, upstream-failure recovery latency, IPv6 functional coverage, fragmentation trigger rates.
- Changing any existing gate threshold or benchmark parameter (comparability).
- New product configuration surface (a scenario argument is not a product key).

## Notes

- Harness split: BenchmarkDotNet for micro rows (R2, R5, part of R6), the custom stability runner for
  everything needing concurrency or sustained load (R1, R3's concurrent observer, R4, R7) —
  BenchmarkDotNet runs one method at a time and cannot express contention.
- Every new measurement must name, in one line, the decision it informs; a row nobody would act on is
  not worth its maintenance weight.
