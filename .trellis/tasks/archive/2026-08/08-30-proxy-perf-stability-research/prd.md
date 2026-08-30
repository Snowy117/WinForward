# Preserve 2026-08-30 Proxy Perf/Stability Deep-Dive Research

## Goal

Persist the 2026-08-30 four-agent deep analysis of the SOCKS5 proxy forwarding paths
(TCP redirect/relay, UDP relay, SOCKS5 control + capture/dispatch front-end, and the
measurement infrastructure) into durable task research documents, so that the findings,
file:line evidence, verified spot-checks, and the ranked improvement backlog can be
reused without re-running the investigation.

## Background

- Session date: 2026-08-30. Trigger: user request to analyze project performance and
  stability with focus on the SOCKS5 proxy series (the product's core value), explicitly
  not the pass/block path.
- Method: four parallel read-only review agents (TCP path, UDP path, SOCKS5 control +
  capture/dispatch, benchmarks/measurement gaps) plus main-agent spot-check verification
  of the three highest-impact claims.
- Baseline at analysis time: commit `e5667af` (master, clean). All previously completed
  optimizations (batched capture reads, pooling, incremental/vectorized checksums, CTS
  reuse, UDP zero-loss admission work, S1–S6/P1–P2 proxy hardening) are already landed
  and are excluded from the backlog.

## Scope

- Documentation only. No source, test, benchmark, or spec changes.
- Deliverables: `research/` files listed below. Findings carry `file:line` references so
  future tasks can be planned directly from them.

## Deliverables

| File | Content |
|------|---------|
| `research/tcp-redirect-path.md` | TCP redirect/relay path review: architecture, perf findings P1–P10, stability findings S1–S7, verified non-issues, ranked top-5 |
| `research/udp-proxy-path.md` | UDP proxy path review: architecture, perf findings P1–P9, stability findings S1–S8, ranked top-5 |
| `research/socks5-capture-dispatch.md` | SOCKS5 control connection + capture/dispatch front-end review: perf findings B.1–B.6, stability findings C.1–C.9, ranked top-5 |
| `research/measurement-gaps.md` | Benchmark/stability-harness inventory, headline numbers, measurement gaps, recorded residuals in task docs, risk ranking |
| `research/synthesis-and-backlog.md` | Cross-report synthesis, spot-check verification evidence, deduplicated ranked improvement backlog with effort estimates |

## Acceptance Criteria

1. All five research files exist under the task's `research/` directory.
2. Every finding retains its `file:line` reference and enough context to act on without
   re-reading the full source tree.
3. The three spot-checked claims (production warm-path dead entry, missing client RST on
   mid-flow relay end, missing `NoDelay`) are recorded with their verification evidence.
4. No files outside `.trellis/tasks/08-30-proxy-perf-stability-research/` are modified.

## Out of Scope

- Implementing any of the backlog items (each deserves its own task).
- Windows-hardware benchmarking or soak runs.
- Spec updates (no new coding contracts were learned; the findings are task research).
