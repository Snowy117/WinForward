# Proxy Hardening Phase D (P1/P2) Benchmark Results

Date: 2026-08-29 · Linux Ubuntu 24.04.4, Ryzen 9 9955HX, .NET 10.0.10, BenchmarkDotNet 0.15.8
ShortRun job (`IterationCount=3, LaunchCount=1, WarmupCount=3`). Raw artifacts: `before/` /
`after/` (`*-report-github.md` + `*-report.csv`), `after/stability-quick.jsonl`.

Scope: task 08-29-proxy-stability-perf Phase D. BEFORE = HEAD `1e94849` (Phases A–C landed) +
benchmark infrastructure only (MemoryDiagnoser on the relay, the new endpoint-rewrite and
checksum micros, valid checksums on rewrite-benchmark frames — required so the AFTER
incremental rewrite measures the real captured-traffic shape). AFTER = D1 (relay CTS reuse) +
D2 (RFC 1624 incremental endpoint rewrite) + D3 (vectorized Internet checksum) landed.
Per hot-path.md contract 9, ns deltas under ~2× on this host are noise direction only;
allocation columns are the exact gates.

## D1 (P1) — relay pump CTS churn → `TcpRelayBenchmarks.OneWayAsync`

| Chunk | BEFORE alloc/op | AFTER alloc/op | BEFORE mean | AFTER mean |
|---|---:|---:|---:|---:|
| 1 (256 KiB) | 1,684.86 KB | 211.15 KB (-87%) | 179.52 ms | 168.89 ms |
| 1024 (16 MiB) | 845.88 KB | 181.56 KB (-79%) | 30.45 ms | 18.09 ms |
| 8192 (16 MiB) | 828.13 KB | 187.63 KB (-77%) | 17.06 ms | 14.73 ms |
| 65536 (16 MiB) | 820.26 KB | 178.97 KB (-78%) | 21.77 ms | 15.18 ms |

- AC8 gate "per-chunk managed allocation ≈ 0 on the warm path": the per-invocation floor is
  one-time socket/buffer scaffolding (~179 KB, identical shape across chunk sizes); the
  chunk-proportional component collapses from ~312 B/chunk (two linked CTS + timers, matching
  the 160 B/op probe) to ~5 B/chunk (slope between the 8192/65536 rows).
- Throughput: no regression at any chunk size (all rows equal or faster; ShortRun ns noise
  caveat applies).

## D2 (P2a) — incremental endpoint rewrite → `FrameRewriterBenchmarks`

| Method | @128 BEFORE → AFTER | @1400 BEFORE → AFTER |
|---|---|---|
| TryRewriteEndpointsDirect (checksum micro) | 73.2 → 37.0 ns | 626.2 → 33.4 ns (18.7×) |
| TryRewriteForwardLegHostShape | 81.6 → 29.5 ns | 658.4 → 49.7 ns (13.2×) |
| TryRewriteForwardLegForwardedShape | 74.0 → 33.4 ns | 727.6 → 34.8 ns (20.9×) |

- AC9: equivalence is pinned by `TcpEndpointRewriteIncrementalTests` — 512 randomized IPv4
  and 512 randomized IPv6 frames prove incremental === retained full-recompute oracle ===
  independently reimplemented checksum validation (odd lengths, option words, extension
  headers, address-only/port-only deltas, and a full 65 536-port sweep covering the
  computed-zero boundary where TCP stores 0x0000 verbatim).
- Precondition (documented in-code): the incremental update folds the incoming checksum in,
  so equivalence to full recompute holds for frames with valid checksums — the capture
  pipeline's contract. Benchmark frames now carry valid checksums to measure that shape.

## D3 (P2b) — vectorized Internet checksum → `ChecksumBenchmarks`

| FrameBytes | Scalar (BEFORE, production) | AFTER production (vectorized+fold guard) | Win |
|---|---:|---:|---:|
| 64 | 39.3 ns | 4.9 ns | 8.0× |
| 512 | 354.4 ns | 20.3 ns | 17.5× |
| 1514 | 989.0 ns | 61.7 ns | 16.0× |

- Landing rule was ">20% on the rebuild-path shape": measured 16× (1514 B) → landed in
  production (`PacketChecksums.Sum`: Vector256 pairwise u32-lane accumulate, byte-swap
  shuffle, scalar tail; lanes folded into the scalar sum every 4 096 blocks so any span
  length accumulates without uint wrap — uint wrap is NOT one's-complement-neutral since
  2^32 ≡ 1 mod 65 535, which is exactly what the pre-existing 131 076-byte all-0xFF audit
  test pins).
- In the AFTER run the benchmark's "Scalar" row *is* the production path (now vectorized);
  the unguarded `VectorCandidate` reference implementation runs ~1.5× faster still
  (61.7 → 41.0 ns @1514) but lacks the periodic fold and therefore only bounds spans by the
  64 KiB IP maximum — kept as decision data, not shipped.

## Validation

| Check | Result |
|---|---|
| `dotnet build -c Release` (solution) | 0 warnings, 0 errors |
| `dotnet test` | 431/431 passed (baseline 424 + 7 new: 5 incremental-checksum property tests, 1 vectorized-checksum equivalence, 1 relay re-arm cancellation regression) |
| Linux `--stability --quick` soak | see `after/stability-quick.jsonl` (zero UDP loss, tcp.unexpectedEof consistent with baseline mixes) |
