# Implementation plan — Benchmark rewrite

Working directory: repo root. All commands from repo root unless noted. No `src/` changes.

## Pre-flight

- [ ] `dotnet --version` reports 10.0.109+ (SDK pin via `global.json`).
- [ ] Working tree clean (`git status`).

## Step 1 — Package wiring

- [ ] `Directory.Packages.props`: add
      `<PackageVersion Include="BenchmarkDotNet" Version="0.15.8" />`.
- [ ] `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj`: add
      `<PackageReference Include="BenchmarkDotNet" />` (CPM supplies the version).
- [ ] Validation: `dotnet restore` exits 0.

## Step 2 — Skeleton + shared fixtures

- [ ] Delete the old single-file harness content of `Program.cs` (keep the project compiling
      from here on — every step ends green).
- [ ] Create `Perf/BenchmarkShared.cs` first: `CreateIpv4UdpFrame`, `CreateIpv4TcpFrame`,
      `CreateFlowKey`, `CreateContext`, `CountingExecutor`, `ThresholdOnlyLogger`,
      `NeverOwnedGuard`, `NoopAsyncDisposable`, `BenchmarkUdpTransportFactory` +
      `BenchmarkUdpTransport`, `NoopUdpResponseSink` (moved verbatim from the old Program.cs;
      visibility `internal`).
- [ ] `Program.cs`: mode dispatch (`--stability` → soak runner, else `BenchmarkSwitcher`).
      Soak runner referenced but minimal at this point.
- [ ] Validation: `dotnet build -c Release` exits 0.

## Step 3 — Perf benchmarks (BDN), one commit-sized unit

- [ ] `ParserBenchmarks.cs` — ipv4Udp, ipv4UdpPayload, socks5Udp decode/encode/encodeSpan,
      `[Params(64, 512, 1514)] FrameBytes`.
- [ ] `NdisBufferBenchmarks.cs` — allocateSetDispose, reuseSet.
- [ ] `FlowTableBenchmarks.cs` — `FlowTableMissBenchmarks` (cardinality 0/1k/16k/65k) +
      `FlowTableHitBenchmarks` (1k/16k/65k); `GlobalSetup` populates via `TryClaimResolved`.
- [ ] `SelfTrafficBenchmarks.cs` — wildcardMiss, cardinality sweep, tokens held per class
      instance.
- [ ] `DispatcherBenchmarks.cs` — warmPass.disabledTrace.
- [ ] `CapturePumpBenchmarks.cs` — endToEnd (1 op = 200k-packet round, fresh pipeline per op,
      `FiniteCaptureReader` moved in, CA1416 pragma + justification).
- [ ] `UdpSessionBenchmarks.cs` — PopulateSessions (1 op = N sessions, fake transports).
- [ ] `TcpRelayBenchmarks.cs` — oneWay, `[Params(1, 1024, 8192, 65536)] ChunkBytes`, transfer
      size 256 KiB / 16 MiB, per-op socket pairs, CA1416 pragma.
- [ ] Validation (smoke, one class at a time is fine):
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter *Parser* --job short`
      exits 0 and prints a results table.
- [ ] Validation: full quick sweep
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --job short`
      exits 0.

## Step 4 — Stability runner

- [ ] `Stability/SoakOptions.cs` — parse per design §3.1 (`--scenario`, `--duration`,
      `--pps`, `--payload-bytes`, `--flows`, `--tcp-concurrency`, `--tcp-transfer-bytes`,
      `--abort-mix`, `--seed`, `--output`, `--quick`).
- [ ] `Stability/StabilityContext.cs` — metadata record (schemaVersion 2) + `WriteResult`,
      console + optional file.
- [ ] `Stability/LoopbackSocks5UdpServer.cs` — no-auth greeting + UDP ASSOCIATE reply, per-
      connection UDP relay socket, `Socks5UdpCodec` decode/encode, forward-to-destination +
      echo-back.
- [ ] `Stability/UdpLossScenario.cs` — paced multi-flow sender, sequence/out-of-order/
      duplicate tracking, counting response sink, drain, JSONL result.
- [ ] `Stability/TcpEofScenario.cs` — concurrent transfers, abort mix (clean/clientRst/
      relayCancel/upstreamTruncate), receiver-side classification, JSONL result; CA1416
      pragma.
- [ ] `Stability/SessionFootprintScenario.cs` — N ∈ {1, 100, 1000}, workingSetDelta/Gen0/
      allocated captured before disposal.
- [ ] `Stability/SoakRunner.cs` — orchestration + exit code (0 ok; 1 if any scenario throws).
- [ ] Validation:
      `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --quick`
      exits 0, emits metadata + `udp.lossRate` + `tcp.unexpectedEof` + `udp.sessionFootprint`
      records with sane metrics (lossRate ∈ [0,1], transfer counts > 0, footprint grows with
      sessions).
- [ ] Validation (manual, once): a 30 s `--scenario udp` run at `--pps 50000` on this machine
      to confirm the scenario saturates before the OS (lossRate > 0 under pressure is a valid
      observation, not a failure).

## Step 5 — Docs + line budget

- [ ] Rewrite `benchmarks/README.md`: perf commands (BDN filter/job/exporters), stability
      commands, JSONL schema summary, before/after comparability warning.
- [ ] Line-budget check over every .cs file under `benchmarks/` (effective = non-blank,
      non-comment):
      ```bash
      python3 - <<'EOF'
      import pathlib, sys
      bad = []
      for p in pathlib.Path("benchmarks").rglob("*.cs"):
          eff = sum(1 for l in p.read_text().splitlines()
                    if l.strip() and not l.lstrip().startswith("//"))
          if eff > 400: bad.append((str(p), eff))
      print("OK" if not bad else bad); sys.exit(bool(bad))
      EOF
      ```

## Step 6 — Full gate (review gate before commit)

- [ ] `dotnet build -c Release` exits 0.
- [ ] `dotnet test -c Release` exits 0 (benchmarks must not break the solution build/tests).
- [ ] `dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter * --job short`
      exits 0 (full matrix, short job).
- [ ] `--stability --quick` exits 0.
- [ ] Line-budget script from Step 5 exits OK.

## Step 7 — Spec update + commit (Phase 3)

- [ ] `.trellis/spec/backend/directory-structure.md`: benchmarks no longer exempt from the
      400-line rule — update the tree annotation
      (`benchmarks/ # throwaway 基准宿主（不适用文件行数约定）` → subject to the same limit,
      note the 2026-08-29 decision).
- [ ] Commit: single commit `benchmarks: rewrite on BenchmarkDotNet with stability soak runner`
      (ask user first per house rules; never commit unprompted).

## Rollback points

- After any failing step: `git checkout -- benchmarks/ Directory.Packages.props` restores the
  previous harness (no src/ files are touched, so rollback is always cheap).
- Post-commit rollback: `git revert <commit>`.
