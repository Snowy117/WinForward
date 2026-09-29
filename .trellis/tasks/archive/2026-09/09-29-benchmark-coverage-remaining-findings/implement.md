# Implementation plan — benchmark coverage for F2–F8

Instrumentation only: no product behaviour change. Each step lands as its own commit with its baseline
artifact; rollback for every step is deleting the files it added (plus reverting its `benchmarks/README.md`
rows).

## Step 0 — Preflight

- [x] Record the current baseline of every existing row the new work will sit beside
      (`FlowTableMiss/Hit`, `Dispatcher`, `CapturePump`, `FrameRewriter`, `Parser`, `GcSoak`,
      `tcp.throughput`, `udp.sessionFootprint`) into
      `benchmarks/results/2026-09-29-benchmark-coverage/before/`, with the exact command per row.
      This is the anchor for "did the instrumentation change anything it should not have".
- [x] Confirm the test baseline (`dotnet test -c Release` totals) and the ≤400 effective-line budget of
      every file the work will touch (`SoakOptions.cs`, `BenchmarkShared.cs`, `CapturePumpBenchmarks.cs`).
      Measured at task start: `SoakOptions.cs` 215, `SoakRunner.cs` 66, `StabilityContext.cs` 48,
      `BenchmarkShared.cs` 164 effective lines; test baseline 952 Core.Tests + 18 Analyzers.
- [x] Historical anchors already on disk for the "did the instrumentation perturb existing rows?" check:
      `benchmarks/results/2026-08-29-socks5-perf/baseline/` (`CapturePumpBenchmarks`,
      `DispatcherBenchmarks`, `FlowTableHitBenchmarks` reports) and
      `benchmarks/results/2026-08-29-proxy-hardening/{before,after}/` (`FrameRewriterBenchmarks`), plus
      the platform comparison in `benchmarks/results/2026-08-30-windows-vm/README.md`. The fresh capture
      below is compared against these.
- [x] Decide, per new row, the one-line "decision it informs" (PRD Notes) and write it into the class
      summary as it lands (every new scenario/benchmark class states the finding it instruments).

## Step 1 — R1 contention family + the fake-collaborator proof

- [x] `Stability/ScalingContentionScenario.cs` + `SoakOptions` arguments (`--threads`, `--flows`,
      `--shared-key-percent`): shared real `FlowTable` (seeded), real `SelfTrafficRegistry`, 1/2/4 worker
      loops; reports resolutions/s, per-worker spread, the scaling ratio, and both guard arms.
- [x] Same workload through `NeverOwnedGuard` vs the real registry, both recorded — at 1 thread the real
      guard costs ~2× the fake one (3.33M vs 6.77M resolutions/s), so the existing dispatcher rows
      report a constant zero delta for the self-traffic path.
- [x] Documented in `benchmarks/README.md`; artifact
      `benchmarks/results/2026-09-29-benchmark-coverage/scaling-contention.jsonl` (+ its README).
      The scaling curve also quantifies the F2 claim: ratio 0.158 (fake) / 0.203 (real) at four threads,
      i.e. aggregate throughput falls as workers are added.

Validation: ran at 1/2/4 threads on this host; two runs within ~5 %; JSONL verdict recorded.

## Step 1b — R1b UDP ready-path arm

- [x] `Perf/UdpReadyPathContentionBenchmarks.cs`: 1/2/4 pre-established flows driven on dedicated
      threads through the real coordinator send entry with a counting fake transport (4,096 datagrams per
      invocation, barrier-released, threads created once so start cost is outside the measurement).
      Reports ns/datagram and 0 B allocated; report-only.
- [x] Rationale recorded in the class summary and the README row: the existing `udp.lossRate` /
      `udp.rawBaseline` pair runs at the loopback ceiling, where F2.4's lock chain cannot show up.
      Measured: 350/406/499 ns and 361/404/523 ns across two runs — **+40–45 % per datagram at four
      workers**, so the gates do serialize the ready path, and the ready path itself allocates 0 B.

## Step 2 — R2 flow-table shapes

- [x] Additive rows in `Perf/FlowTableBenchmarks.cs`: same-orientation hit, reverse-alias hit, and a
      per-hit activity-clock reference row, all at `--params` cardinality 4,096 (the shipped
      `tcpFlowCapacity`). Existing rows untouched.
- [x] **Claim-path scope decision**: a "claim a new key" BDN row was **dropped as unsound** —
      BenchmarkDotNet invokes one method many times per iteration and `FlowTable` has no per-invocation
      reset (BDN 0.15.8 has no `[InvocationSetup]` here), so the row would silently degrade into the
      resolve path partway through an iteration and report a fabricated number. The claim cost is
      measured instead by a **controlled loop** in the R3 sweep scenario and the R4 churn path, where
      the harness owns the table's lifetime. Recorded here rather than silently replaced.
- [x] Record the row baselines (`--filter '*FlowTableProductionShape*' --job short`), three runs; the
      reverse-alias and clock rows are stable, the same-orientation hit row swings 26 % on a short job —
      recorded in the artifact README as a usage caveat rather than smoothed over.

## Step 3 — R3 sweep pause + zero-allocation gate

- [x] `Stability/SweepPauseScenario.cs` (+ `--scenario sweep`): a 65,536-entry table refilled with
      already-expired states and swept with the production call while `--tcp-concurrency` observer
      threads timestamp `TryResolve`; reports sweep mean/max wall time, expired-per-tick, allocation per
      sweep, and the observer **maximum** pause plus counts over 0.1/0.5/1/5 ms (an exact maximum, not a
      sampled percentile — a sample is what misses the one pause that matters). Verified live keys by
      holding TCP through the predicate and aborting if a resolve ever misses.
- [x] `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` (0 B per full sweep) plus
      `AllocationProbeSeesAKnownAllocation` (the same measurement path sees a known allocation).
      Discriminating power proven end to end: injecting a 16-byte allocation into the sweep turned the
      gate red with `Expected: 0, Actual: 40`, then the file was restored (no diff).
- [x] **R3b narrowed, with reasons**: only `FlowTable.RemoveExpired` is allocation-free today (reused
      scratch). `TcpRedirectTable.RemoveExpired`, `TcpRedirectSessionStore.RemoveExpiredAsync`,
      `UdpProxyCoordinator.RemoveExpiredAsync` and `UdpAssociationPool.SweepIdleAssociationsAsync` all
      build their retirement sets with LINQ and allocate, so a gate added now would be **red on an
      unmodified tree**. They are recorded as F3's targets in the artifact README with their code
      locations; their byte baselines need their own compositions (redirect sessions, live UDP
      associations) and land with the fix.
- [x] Sweep-expired row: **not** a BenchmarkDotNet row, for the same reason as the claim row — one
      method invoked many times per iteration against a table with no per-invocation reset would sweep an
      empty table after the first invocation. The sweep is measured by the scenario's controlled loop
      instead (146 sweeps of 65,536 entries in 15 s).
- [x] Baselines recorded: sweep 25.1 ms mean / 87.1 ms max; resolve pause up to **39.5 ms against the
      research's 0.5 ms target**; 14,022 resolves over 1 ms; **0 B per sweep**; claim 2,339.7 ns and
      192.02 B per claim.

## Step 4 — R4 TCP new-flow churn

- [x] `Stability/TcpChurnScenario.cs` (+ `--scenario tcpChurn`, `--attribution-delay-ms`,
      `--attribution-delay-percent`): the real relay path, one shared rate gate so `--rate` is the run's
      connection rate, first-byte latency per class, establishment outcomes, allocation per second and
      per connection, and the server's CONNECT/echo counts as cross-checks.
- [x] The clock covers dial → per-flow setup → handshake → first byte, so a stall is inside the measured
      window (the first version had it outside, and the delayed class showed no effect at all — caught by
      running the A/B rather than by reading it).
- [x] Acceptance proof recorded (three runs, final series): a 50 ms stall on 1 % of connections gives a
      delayed class at mean 52.3 ms while the stable class stays at 2.34 / 2.36 / 2.21 ms across the runs;
      pooled, the p99 moves 8.3 ms → ~52 ms (**≈6×**) for a mean of 2.34 → ~2.70 ms (**+15 %**).
- [x] F8's other half measured: **~91 KB of transient allocation per new flow**, 5 Gen0 collections and
      ~5.45 MB/s at 60 connections/s.
- [x] Recorded caveat: the baseline `maxMs` is ~99 ms against a p99 of 8.4 ms, so this row must be
      compared on p99, not max.

## Step 5 — R5 proxy data path micro

- [x] IPv6 frame builder in `BenchmarkShared` (`CreateIpv6TcpFrame`: Ethernet + IPv6(40) + TCP(20),
      TCP checksum over the IPv6 pseudo-header) plus `CreateIpv6UdpFrame` for the parser rows.
- [x] `Perf/TcpRedirectDataPathBenchmarks.cs`: host/forwarded × IPv4/IPv6 × 128/1400 B, composing
      sequence tracking → forward/reverse rewrite → MAC swap. The send is deliberately not in the row
      (a driver IOCTL; a counting fake would add noise, not information).
- [x] Independent oracle test (`BenchmarkFrameBuilderTests`): both builders checked against the test
      project's own `ChecksumMath`, plus "both legs accept the frame in both shapes and families".
- [x] **A harness bug the self-check caught**: the forwarded association's forward-local address was
      built as IPv4 while the IPv6 frame is IPv6, so the rewriter rejected it and setup failed — which
      turned all eight IPv6 rows `NA`. Without the setup proof those rows would have silently measured
      the *rejection* path, which is **faster** than the real rewrite, i.e. it would have looked like a
      finding. Fixed (family-matched forward-local) and covered by the new test.
- [x] Baselines recorded (`tcp-redirect-data-path.md`): IPv4 51–130 ns, IPv6 94–115 ns, **0 B allocated
      on every row**; frame size 128 vs 1400 barely moves the number (the rewrite is header-only work,
      which is itself the F4.2 parse-once argument).
- [x] Parser IPv6 rows (`Ipv6UdpTryParse`/`Ipv6UdpPayload` over a new `CreateIpv6UdpFrame`), 6 new test
      cases, both README sections and a parser archive — delivered by an implement sub-agent and
      re-verified in the parent session (968 + 18 green twice, format empty).
- [x] Refreshed baseline after that code change: IPv4 51–129 ns, IPv6 87–123 ns, 0 B on every row.
- [x] **A finding, not just a number**: `ReverseLegForwarded` is a reproducible **2.0×** slower for IPv6
      (103.5 vs 51.5 ns at 128 B, stable across runs and in the pre-change archive) while the other three
      legs stay within 1.4× and the two forward legs are 5–12 % *faster* on IPv6. Mechanism located in
      product code: `PacketChecksums.TryRewriteIpv6Tcp` folds a 16-word `stackalloc` loop where the IPv4
      path folds 4 words inline. Recorded as an F4 inlining candidate in design §3.1.
- [x] Gaps the independent check found here and their disposition: the results README's reverse-leg
      sentence claimed `RecordServerSynAck` (the row calls only `TrackServerSequence`), and R5's "0 B" was
      a BenchmarkDotNet column rather than a gate — both fixed by the follow-up implement sub-agent
      (`TcpRedirectDataPathAllocationGateTests` + the corrected sentence).

## Step 6 — R6 pump idle/wake + read-call counting

- [x] `Stability/PumpIdleWakeScenario.cs` (`--scenario pump`, excluded from `--scenario all`): the real
      pump over two fake readers. **Idle**: 15 s window at the production 1 ms poll delay → 13,165/13,284/
      13,288 polls, `cpuSecondsPerIdleSecond` **0.0178/0.0185/0.0178** (~1.8 % of one core, i.e. ~20 µs of
      timer/sleep cost per poll — recorded as `Thread.Sleep` resolution and context switching, not packet
      work), `allocatedBytes` **0/0/0** with the row failing the run otherwise, `readCallsPerPoll` 1.0.
      **Wake**: 5,000 wakes → p50 **0.078 ms**, p95 0.124 ms, p99 0.166–0.189 ms, max 0.58–0.82 ms,
      `readsPerWake` = `readCallsPerPacket` = 1.0. That ~0.08 ms median against the up-to-1 ms poll delay
      is the F5.2 number.
- [x] `tests/WinForward.Core.Tests/CapturePumpReadCallTests.cs` (113 eff lines): exact read-call gate (one
      `TryReadPackets` per empty poll, one per non-empty batch), the idle-path zero-allocation invariant,
      and a scenario-selectability test so the documented invocation is itself falsifiable.
- [x] **Two measurement traps the sub-agent caught in its own first version**, both kept in the artifact
      README as evidence: the wake row first measured a *hot handoff* (p50 0.52 µs — the reader took the
      semaphore fast path and never parked), fixed with a confirmed-park handshake (reader announces it is
      entering its blocking wait, harness waits, then arms); and the idle row first failed its own gate with
      exactly **40 B** — the harness allocated its `Stopwatch` after the baseline snapshot. Both fixed.
- [x] Scope narrowing carried out as written below (the driver's query+read pair is below the seam).

### Repository observation recorded by Step 6 (not this task's defect)

The sub-agent's first full `dotnet test WinForward.slnx` **hung for 41 minutes at ~0 % CPU**: the testhost
had 83 threads parked on `futex` and 60+ `wf-setup-*` (`SetupExecutor`) threads waiting, which is the
shape of a test never receiving a completion signal rather than a slow suite. It did not reproduce in
three later runs (two of them under `--blame-hang --blame-hang-timeout 120s`, which reported nothing), and
the suite normally finishes in 6–9 s. A fourth run in the parent session was green as well. Left as a
flagged environment/test-tree flake for a dedicated investigation (hang dump + per-test timeout census)
— it is not caused by, and cannot be fixed inside, this measurement task.

## Step 7 — R7 residency census

- [x] `Stability/ResidencyCensusScenario.cs` (`--scenario residency`, `--udp-flows`): baseline(0) → flow
      table → real loopback relays held open → coordinator sessions over fake transports, a forced full GC
      before every sample, one row per stage plus a report-only verdict.
- [x] Population proofs are the only aborts: `table.Count == --flows`, the loopback server's
      `ConnectReplies == --flows` (awaited, then asserted) with the relay count, and the transport
      factory's `Created == --udp-flows` with `coordinator.SessionCount`. No byte or timing threshold can
      fail the run.
- [x] Findings: **the flow table costs ~32.5 MB managed at the production default capacity whether it
      holds 1, 100 or 1000 live states** (≈251 B per live state — the "325 KB/live flow" reading overstates
      by ~1,300×; A1's ~20 MB estimate measured at 32.5 MB, which is the A4 target). TCP relays cost
      146–160 KB working set and **4.22 descriptors each** (exact across runs) — A4 item 9's ~10 MB @100
      connections confirmed at ~15 MB. Fake UDP sessions: 4.2 KB managed, 41–44 KB working set, 0
      descriptors by construction (the real 1.070 descriptors/live session stays the 09-28 series).
- [x] **A trap avoided and documented**: on Linux `Process.PrivateMemorySize64` is `/proc` `VmData`, which
      carries glibc per-thread arenas (100 fake UDP sessions read +614 MB ≈ 6.1 MB/session; verified with a
      standalone probe that Private == VmData). It is reported as required but marked non-attributable
      rather than presented as per-flow cost.
- [x] Spread stated rather than hidden: managed 0.04–0.09 %, descriptors exact, working set ±3–4.6 % —
      so **a working-set difference under ~10 % is not a result on this row**.

## Step 8 — Documentation, artifacts, record

- [ ] `benchmarks/README.md`: the new scenario/row families, their commands, and the gate/report
      classification table from the design.
- [x] Baselines recorded in `benchmarks/results/2026-09-29-benchmark-coverage/` with a README naming the
      host/runtime, the command per artifact, and the noise caveats. **Superseded note**: the `before/`
      directory this plan originally called for was replaced by the historical anchors already in the repo
      (`results/2026-08-29-socks5-perf/baseline/`, `results/2026-08-29-proxy-hardening/{before,after}/`,
      `results/2026-08-30-windows-vm/README.md`) plus the fresh per-family baselines; the per-run files
      behind each multi-run table are labelled in the README rather than archived separately.
- [x] **Commit plan (pre-authorized: the operator is AFK and has approved this task's operations, so it is
      executed without a confirmation round)**, in the repository's order — work commits first, then the
      task record, the archive and the journal; every work message ends with the task slug:
      1. `perf(bench): F2-F8 measurement coverage: scenarios, rows and harness plumbing (benchmark-coverage-remaining-findings)`
         — the new scenarios, the new Perf rows, `BenchmarkShared`, `SoakOptions`, `SoakRunner`.
      2. `test(bench): sweep, frame-builder, read-call and data-path allocation gates (benchmark-coverage-remaining-findings)`
         — the four new test files.
      3. `docs(bench): document the new scenarios, artifacts and the inspection evidence (benchmark-coverage-remaining-findings)`
         — `benchmarks/README.md`, the results README and every recorded baseline under
         `benchmarks/results/2026-09-29-benchmark-coverage/`.
      4. `chore(task): record the benchmark-coverage task and sync the parent backlog (benchmark-coverage-remaining-findings)`
         — the task directory and `.trellis/tasks/08-30-proxy-perf-stability/` (child row).
      5. `chore(task): archive 09-29-benchmark-coverage-remaining-findings`
      6. `chore: record journal`
- [ ] The spec note (only if the task taught something the specs do not carry) — decided at archive time.
- [ ] Archive and journal entry.
- [ ] Task record + spec note: if a measurement forced a seam or a convention worth keeping, capture it
      in `.trellis/spec/backend/` (Phase 3.3), and add the "how each F2–F8 will be proved" mapping to the
      task's record so the optimization tasks can cite it.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                  # zero-warning
dotnet test WinForward.slnx -c Release                                   # baseline + the new tests only
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx # zero <Issue>

dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTable*' '*Redirect*' '*CapturePump*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario scaling --threads 1
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario sweepPause --flows 65536 --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario tcpChurn --rate 20 --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario residency --flows 100 --quick
```

## Risky areas / rollback

| Area | Risk | Rollback |
|---|---|---|
| `Stability/SoakOptions.cs` (argument plumbing) | growing past the line budget or breaking existing argument parsing | keep new arguments in a small partial/companion file; existing arguments untouched |
| `BenchmarkShared.cs` | the IPv6 builder touches a file every Perf class uses | builder additive; existing IPv4 builders unchanged; the file's budget re-checked |
| R4's delay seam | a synthetic delay could be mistaken for the real attribution cost | the scenario's verdict row names the mode explicitly, and the README states the Windows split |
| Timing rows | flaky gates would block every future commit | only exact metrics gate (design §3) |

## Independent check record (trellis-check sub-agent)

Verdict per checklist item: no product change **PASS** · artifact numbers **PARTIAL** (every checkable
number matched exactly; six multi-run columns were unverifiable because the export path overwrites) ·
gate/report discipline **PASS** for the rows, **PARTIAL** for R5's allocation claim · tests **PARTIAL**
(one pre-existing flaky allocation gate) · build/format **PASS** · file budget **PASS** (max 319 effective
lines) · docs parse **PARTIAL** (synopsis drift) · cross-layer mapping **PARTIAL** (stale citations only) ·
duplication **PASS** (acceptable) · comments **PASS**.

Two blockers it raised, and what happened to them:

1. **The JetBrains gate was not met and the task's own account of it was wrong.** The checker's run on the
   same tree reported **13 genuine `<Issue>` entries and zero `CSharpErrors`** — the 57 `CSharpErrors` the
   parent session saw came from its *cold-cache* run (caches cleared first), which is why they sat on
   compiling code. Three of the 13 were `RedundantUsingDirective` findings the checker proved genuine by
   deleting them (the build stayed clean), so calling them false positives was itself false. Disposition:
   the follow-up implement sub-agent fixes or narrowly suppresses all 13, adds the missing R5 allocation
   gate, and rewrites the results README's note to the reproducible picture. The lesson for the repo:
   **cache state changes what `jb inspectcode` reports**, so a cold run's findings must be confirmed by a
   second run before being dismissed.
2. **`dotnet test` is not deterministically green on this tree.** In 13 full-suite runs the checker saw 2
   failures, both the **pre-existing** `HotPathAllocationGateTests.ReverseRewriteAndInjectAllocatesNoManagedBytes`
   (`Expected: 0 / Actual: 7520`), which also failed once in 25 isolated runs of that filter — so it is
   inherent to that test and not a side effect of the 19 tests this task adds. Left as a flagged
   test-tree defect for a dedicated investigation; this task must not "fix" it by loosening an exact gate.

Everything else it verified: ≥8 numbers per family matched their artifacts exactly; five fault injections
proved the self-checks can fail (restored byte-identically, md5-checked); 19 scenario names and 25
documented flags all parse; every §3.1 mapping row is backed by a landed artifact; the new enum members
serialize by name so the added scenarios are comparability-safe; `--scenario all` still expands to exactly
the same six scenarios as before.


## Follow-up pipeline (operator directive, recorded here so it survives the archive)

The operator (AFK) directed that after this task completes, the following run **sequentially**, each as its
own Trellis task with the full flow — create task → PRD (a research sub-agent first where the finding is
complex) → **a review sub-agent that plays the operator's review of the PRD/design** → implement → check →
**a benchmark run that proves the change and records its data** → commit → archive. Only one agent may edit
files at a time.

1. **Flaky/hang test stabilisation.** Evidence already collected: `HotPathAllocationGateTests.ReverseRewriteAndInjectAllocatesNoManagedBytes`
   fails in roughly 1 run in 6 (`Expected: 0 / Actual: 7520`, also in isolation), and one full-suite run
   hung 41 minutes at ~0 % CPU with 83 threads parked on `futex` and 60+ `wf-setup-*` threads waiting.
2. **F3 sweeps** — measured: a 65,536-entry sweep blocks a resolve for up to **40 ms against the research's
   0.5 ms target**, and four other sweep sites allocate their retirement sets.
3. **F2 locks** — measured: throughput *falls* as workers are added (scaling ratio 0.158–0.203 at four
   threads) and the real self-traffic guard costs 2× the fake one the existing rows use.
4. **F4 keys and parsing** — measured: the IPv6 reverse forwarded leg is a reproducible 2.0× the IPv4 leg,
   and the composed data-path rows give the per-packet baseline to move.
5. **F5 pump I/O** — measured: ~1.8 % of a core per idle second and a 0.078 ms median wake; the driver's
   internal query+read IOCTL pair is below the seam and needs Windows to gate.
6. **F8 pump-thread attribution** — measured: a 50 ms per-flow stall moves the pooled p99 from 8.3 ms to
   ~52 ms while the stable class mean does not move, and a new flow allocates ~91 KB.
7. **F6 UDP footprint** — partly covered already; its residual (adaptive idle TTL) needs the one-shot vs
   sustained classification signal that belongs to the change itself.
8. **F7 WFP** — to be scoped by its own research step first; if it is not implementable in this repository,
   the PRD and its review record that outcome instead of forcing a change.

Order rationale: the measured user-visible severity first (sweep pauses and the scaling collapse), then the
per-packet costs, then the two items whose proof is partly Windows-bound, and last the item whose
measurement half already exists.
