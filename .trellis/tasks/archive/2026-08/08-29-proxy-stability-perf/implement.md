# Implement — Proxy Stability and Performance Hardening

Execution order mirrors `design.md` rollout. Every phase ends green on build+tests before the
next starts; commits are per-fix. Validation baseline: 386 tests, zero-warning build.

## Phase A — UDP quick wins (S2, S6)

- [ ] A1 (S2): `SIO_UDP_CONNRESET` IOControl on relay socket creation
      (`Socks5UdpTransport.CreateAsync`), input FALSE, before bind; internal seam for assertion.
- [ ] A2 (S2): `ConnectionReset` classified as skip in receive path; counter wired into the
      5 s skip summary; loop survives. Test: faulting-once fake transport → session alive.
- [ ] A3 (S6a): domain-destination drop counter.
- [ ] A4 (S6b): rate-limit the dead-relay per-datagram warn in `NdisPacketActionExecutor`.
- [ ] A5 (S6c): reason-accurate blocked-flow warn text.
- [ ] A6 (S6d): rate-limited warn in `IdleExpirySweeper` catch.
- [ ] Gate: `dotnet build -c Release` (0 warnings) + `dotnet test` + Linux
      `--stability --quick`. Commit.

## Phase B — TCP teardown correctness (S3, S4, S1)

- [ ] B1 (S3): observe relay `Completion` on dispose + attach-failure paths; faulting-fake
      tests on both paths.
- [ ] B2 (S4): SYN-template RST|ACK variant in `TcpResetBuilder` + capacity-path injection with
      per-tuple 1 s cooldown set (bounded, evict-oldest); tests: one RST per tuple per window,
      retransmissions inside window dropped silently, host/forwarded direction matrix.
- [ ] B3 (S1): characterization tests first (fragment on associated flow, both shapes, both
      legs — document current pass/teardown); then implement raw-tuple fragment match →
      consume + teardown-with-RST + `reason=fragment` trace; update characterization tests to
      the new pinned semantics.
- [ ] Gate: full `dotnet test` + focused review of RST semantics (client-visible behavior
      change — present the behavior table to the developer). Commit(s).

## Phase C — Pump-path enumeration cache (S5)

- [ ] C1: cache + `NetworkChange.NetworkAddressChanged` invalidation + 30 s TTL in
      `WindowsAdapterLocalAddressProvider`; enumeration seam; last-snapshot-on-failure policy.
- [ ] C2: tests — steady-state zero enumerations; refresh on event and on TTL expiry;
      empty-snapshot → forwarded flow still fail-closed.
- [ ] Gate: build + tests. Commit.

## Phase D — Performance (P1, P2)

- [ ] D1 (P1): relay CTS reuse via `TryReset` + `CancelAfter`; tests unchanged; run
      `TcpRelayBenchmarks` before/after — allocation ≈ 0 per chunk, throughput regression < 5%.
- [ ] D2 (P2a): incremental (RFC 1624) endpoint-rewrite checksums; property test vs full
      recompute; `TcpEndpointRewriteTests` byte-exact suite unchanged; add checksum micro
      benchmark; record numbers.
- [ ] D3 (P2b, conditional): vectorized Internet checksum for rebuild paths; land only on >20%
      micro win with identical semantics; otherwise record follow-up only.
- [ ] Gate: build + tests + benchmark artifacts under
      `benchmarks/results/<date>-proxy-hardening/`. Commit(s).

## Phase E — Full-scope verification and wrap-up

- [ ] E1: full `dotnet test`; Linux `--stability --quick` soak (zero loss, no regression vs
      2026-08-29 baselines).
- [ ] E2 (optional, if Windows host available): real-machine smoke — debug-log audit for 0
      warn/error, UDP DNS query, TCP TLS fetch; note results in README-style artifact.
- [ ] E3: spec update (Phase 3.3): error-handling (fragment policy), tcp-local-redirect (RST on
      teardown/capacity), udp-relay (connreset skip), hot-path (CTS reuse/incremental checksum
      contracts) — via trellis-update-spec flow.
- [ ] E4: commit remaining artifacts; finish-work (journal, archive).

## Validation commands

```bash
dotnet build -c Release                 # zero warnings required
dotnet test
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --quick
# BDN perf (adjust filters per phase):
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- -f '*TcpRelay*'
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- -f '*FrameRewriter*' --join
```

(Arg passthrough per `benchmarks/WinForward.Benchmarks/Program.cs`; verify before first run.)

## Review gates

1. After Phase B: client-visible RST semantics table → developer review before proceeding.
2. After Phase D: benchmark before/after numbers → developer review before spec update/commit.

## Rollback points

One commit per fix (A, B1, B2, B3, C, D1, D2, [D3]); revert individually. B3 depends on B2's
SYN-RST builder variant only for the capacity edge; core S1 path uses existing
tracked-sequence RST and is revert-isolated.
