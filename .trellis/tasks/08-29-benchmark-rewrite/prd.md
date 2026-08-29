# Rewrite benchmarks on BenchmarkDotNet with stability scenarios

## Goal

Replace the hand-rolled benchmark statistics harness (`benchmarks/WinForward.Benchmarks`, a single
840-line `Program.cs` with manual Stopwatch/GC sampling and self-computed ns/op) with a
scientifically grounded measurement stack, and add a new stability benchmark dimension (UDP packet
loss rate, TCP unexpected EOF, etc.) that the current harness cannot express.

## Background

- The current harness computes elapsed/CPU/allocations/working-set deltas by hand with
  `Stopwatch` + `GC` + `Process` sampling. It has no outlier rejection, no pilot runs, no
  confidence intervals, and no process isolation — results are not scientifically defensible.
- Production smoke testing (see `smoke/*.log`) surfaces reliability failures (TCP streams dying
  with unexpected EOF, UDP datagrams going missing) that no current benchmark measures.
- New project constraint (user decision, 2026-08-29): benchmark code is now subject to the same
  **400 effective lines per .cs file** limit as the rest of the codebase (previously exempt —
  spec update required in Phase 3.3).

## Requirements

### R1 — Migrate performance benchmarks to BenchmarkDotNet

- Use **BenchmarkDotNet 0.15.8** (first version line with .NET 10 support) added via central
  package management (`Directory.Packages.props`).
- Migrate ALL existing scenario families from the current matrix, preserving their parameter
  sweeps:
  - `parser.ipv4Udp`, `parser.ipv4UdpPayload` (frame sizes 64/512/1514)
  - `socks5Udp.decode`, `socks5Udp.encode`, `socks5Udp.encodeSpan`
  - `ndisBuffer.allocateSetDispose`, `ndisBuffer.reuseSet` (frame sizes 64/512/1514)
  - `flowTable.resolveMissing`, `flowTable.resolveCrossAdapterHit` (cardinality 0/1k/16k/65k)
  - `selfTraffic.wildcardMiss` (cardinality 0/1k/16k/65k)
  - `dispatcher.warmPass.disabledTrace`
  - `capturePump.endToEnd` + steady-state aggregate (frame 128/1400 × batch 32/1)
  - `udpSessions.active` (1/100/1000 sessions)
  - `tcpRelay.oneWay` (chunk 1/1024/8192/65536)
- Statistics come from BenchmarkDotNet (MemoryDiagnoser for allocations; median/percentiles/
  outliers via its engine). No hand-rolled timing remains for perf scenarios.

### R2 — Add stability benchmarks (soak runner)

- A separate scenario runner (not BenchmarkDotNet) for count-based reliability metrics under
  sustained load, emitting JSONL records (schema in `design.md`):
  - **UDP loss rate**: real-socket loopback path through `UdpProxyCoordinator` + a harness
    SOCKS5 UDP associate server; send sequenced datagrams at a target pps for a duration;
    report sent/received/lossRate/reordered/duplicated.
  - **TCP unexpected EOF**: concurrent one-way transfers through `TcpProxyRelay` with
    adversarial mid-stream events (client abort, relay cancellation, upstream truncation);
    report clean/complete, unexpected-eof (FIN before transfer completion), reset counts.
- Both scenarios must be runnable in a short smoke mode (e.g. 15 s) and a full mode.
- Stability scenarios stay managed-only/loopback (no WinpkFilter/NDISAPI dependency), runnable
  on any OS like the current harness.

### R3 — Respect the 400 effective line limit

- Every .cs file under `benchmarks/` ≤ 400 effective lines (non-blank, non-comment).
- Split along natural seams (scenario families, shared fixture builders, fakes).

### R4 — Documentation

- Rewrite `benchmarks/README.md` for the new commands (BDN filter/job args for perf; soak args
  for stability), what each measures, and how to compare before/after runs.

## Non-Goals

- No benchmarks that require the WinpkFilter driver, Windows elevation, or real network
  interfaces (consistent with today's `managedOnly: true` stance).
- No CI pipeline changes (benchmark runs stay manual, as today).
- No changes to `src/` production code. If a stability scenario exposes a production defect,
  report it; fixing it is a separate task.

## Acceptance Criteria

- [x] `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*'` runs the
      full BenchmarkDotNet matrix covering all R1 scenario families without error. (The `*` must be
      quoted: unquoted, the shell glob-expands it to directory names and BDN silently selects zero
      benchmarks — check-agent finding, 2026-08-29.)
- [x] `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --quick`
      completes both stability scenarios and emits JSONL records with loss rates and EOF counts.
- [x] `dotnet build -c Release` and `dotnet test -c Release` pass (analyzers included).
- [x] Every .cs file under `benchmarks/` has ≤ 400 effective lines.
- [x] The old hand-rolled `BenchmarkContext` timing/statistics code is deleted (no dual paths).
- [x] `benchmarks/README.md` documents the new workflows.
- [ ] `.trellis/spec/backend/directory-structure.md` no longer exempts benchmarks from the
      line-count convention (spec updated in Phase 3.3).

## Notes

- User decisions (2026-08-29): BenchmarkDotNet for perf; independent soak runner for stability;
  full scenario-parity migration.
