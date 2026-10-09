# Benchmark Methodology: rows that measure the path they name

> How to build a benchmark row, stability scenario or gate that measures the path it claims to
> measure, and which numbers may be quoted as gates. Part of the [hot-path family](./hot-path.md);
> read it when you add a row, a scenario, an allocation or timing gate, or a real-dial instrument.

## Measurement discipline

- A benchmark gate is stated in **allocation bytes and GC counts**, never in time; ns/pps deltas
  under ~2× are noise on the dev box. Timing, throughput, percentiles and residency are **series
  comparisons**, reported beside their control; allocation bytes, call counts and GC counts are
  gates. Research targets (e.g. "max resolve pause < 0.5 ms") are recorded as target lines, never as
  pass/fail lines.
- Before a gate is trusted, **prove it can fail**: inject the violation once (a 16-byte allocation, a
  one-short population), record the exact failure message, then restore the file. A gate whose
  failure mode has never been observed is an assumption. See
  [allocation-gates.md](./allocation-gates.md) for the injection form that survives the JIT.

## Rows must measure the path they name

Each of these checks exists because something real slipped through without it.

- **Prove the measured path is taken, in setup.** A frame the rewriter rejects is *faster* to process
  than one it accepts, so a row measuring the rejection path looks like an improvement. Rows assert
  their operation succeeds on the pristine input before anything is timed
  (`TcpRedirectDataPathBenchmarks.ProveRowsSucceed`; the parser rows throw on `TryParse == false`),
  and setup fails loudly otherwise.
- **Prove the population is live before sampling.** A census or churn row whose population silently
  failed to build reports per-flow costs for flows that do not exist. Assert the population against
  an independent counter — the server's CONNECT-reply count, the factory's created count, the table's
  own `Count` — and abort the run on a mismatch. The population proof is the only thing allowed to
  abort a report-only row.
- **Model the wait being measured.** A wake-latency row must confirm the reader actually parked
  before it is armed; arming first measures a hot handoff (0.5 µs) and calls it a wake (78 µs). The
  same rule applies to any latency whose claim is "the cost of waiting".
- **Keep the harness out of its own window.** Take allocation baselines after the harness has created
  its own clocks, buffers and lists, or the gate reports the harness's allocations as the product's
  (the pump-idle gate failed with exactly 40 B of `Stopwatch` before this was fixed).
- **Never use a fake collaborator for the thing under test.** A guard/executor/transport that answers
  before touching the code under test makes the measured delta structurally zero:
  `NeverOwnedGuard` hides the whole self-traffic path, while the same workload through the real
  registry costs 2× at one thread.
- **Mirror every production wiring point the measurement depends on, not only the one under test.**
  A scenario that drives a coordinator's sweep directly has no capture pump, so it must tick the
  composition's `ActivityBucketClock` itself at the pump's cadence
  (`DurableCaptureBundle.FlushPendingInjections` is the chain point; `UdpSessionBudgetRun.TickActivityClock`
  is the mirror). A frozen clock leaves every session stamped at the coordinator's construction
  bucket, so the first sweep past the retention **mass-retires the whole population** and the run
  measures a sawtooth artifact instead of the retention it names: the frozen-clock `udp.sessionBudget`
  read a 4,479-session steady peak before the change and 500 after (the run's association term — the
  pooling-coverage verdict the tree then carried, removed with association sharing on 2026-10-05 —
  failed because the association high-water mark still reflected the pre-steady transient), while the
  same tree with the tick mirrored read 4,539 before and 1,014 after and the surviving
  `verdict.retentionBounded` stayed green on both arms. A verdict that changes when an *instrument*
  mirror is added is an instrument defect — fix the mirror, never the ceiling or the load.
- **A wall-clock maximum is not an acceptance figure when the host queueing dominates it.**
  `flowTable.sweepPause`'s phase-scoped `maxSweepWindowPauseMs` fixed *attribution* (in-window
  resolves no longer include the scenario's own refill contention) but not *attributability to the
  hold*: the calibration control — the same in-window flag armed ~120 ms with **no product call at
  all**, reproducible with `--sweep-window-control-ms <n>` — measured 5.19 ms with 12,336 in-window
  overshoots, the same order as the code under test, and the raw series stays refill-dominated
  (≥96 %). Sweep acceptance is therefore proven by **countable work-per-hold probes**
  (`SweepAllocationGateTests.FlowTableSweepHoldWorkIsBoundedByChunkEntries` and
  `…FlowTableProductionShapeSweepRecordsItsHoldShape`), with every timing field classified
  report-only and quoted beside the control.

## The real-dial harness: the server must not share the measured process

The loopback SOCKS5 server allocates a 64 KiB relay-loop buffer plus per-connection sockets and
arrays per accepted control connection, and `GC.GetTotalAllocatedBytes` is process-wide — the
2026-09-21/22 "framework path 83,442 B/session" was ~75,500 B/session of harness. So every
instrument that dials the loopback server **and** reads allocation counters
(`FrameworkSetupBenchmarks`, the real-transport `UdpSessionBenchmarks` rows, `udp.churn`) runs the
server out of process:

- Child mode: `--stability --serve-socks5-udp --flows <N> [--dial-delay-ms <D>]`; churn opt-in
  `--socks5-external`; benchmark opt-in `WINFORWARD_BENCH_EXTERNAL_SERVER=1`.
- Helper: `ExternalLoopbackSocks5UdpServer.StartAsync(int flows, TimeSpan associateDelay,
  CancellationToken)` → `ControlEndpoint`, `IsEnabled`, `ThrowIfExited()`.
- Handshake: the child prints exactly one stdout line `{"controlPort":<P>,"echoPort":<E>}`;
  everything else it writes goes to stderr.
- Lifetime: the child exits on stdin EOF (parent death or disposal) or SIGTERM/SIGINT; the parent
  closes stdin, waits 5 s, then `Kill(entireProcessTree: true)`.
- Child content: `EchoReceiver(N)` + `LoopbackSocks5UdpServer(receiver.Endpoint, associateDelay)`.
  The external probe is **echo-fed** (one response per session); the historical in-process probe was
  discard-fed. Quote the shape with any probe number.
- Default: no flag and no env var means the historical in-process harness, kept byte-identical so
  recorded command lines keep reproducing their (harness-inflated) numbers.

Failure handling: no handshake line within 15 s makes `StartAsync` throw with the child's drained
stderr attached; a child that exits mid-instrument fails the next `ThrowIfExited()` (probe flush
wait, churn wave top and response wait) rather than producing a silent zero-response row; double
dispose is a no-op behind a latch and closes stdin once; a child outliving a crashed parent ends on
stdin EOF, with the 5 s wait plus process-tree kill as the backstop.

Evidence for the switch: the framework ladder reproduces at in-process ≈83,442 B/session versus
out-of-process ≈7,952 B/session (≥3 runs, `--job short`). The recorded anchors themselves are owned by
[udp-datagram-path.md](./udp-datagram-path.md). In-process runs stay valid as *diagnostics* when the
harness share is stated or subtracted; adding a new real-dial allocation instrument against the
in-process server, or quoting an in-process number without its harness share, is the defect this
section exists to prevent.

## Tests required

- One gate test per exact claim, in a file of its own when the existing gate file is near the line
  budget.
- One self-check test for any new frame or key builder, checked against an implementation independent
  of the builder (the test project's own checksum code, the production parser).
- A scenario-selection test when a scenario is added or deliberately excluded from `--scenario all`,
  so the documented invocation stays falsifiable.
- Smoke: `--serve-socks5-udp --flows 8 < /dev/null` prints the handshake line and exits 0.
