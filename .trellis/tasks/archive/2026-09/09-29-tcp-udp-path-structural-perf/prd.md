# TCP/UDP data-path structural performance research

Parent: `08-30-proxy-perf-stability`.

## Goal

Explain, with file/line evidence, why the TCP redirect and UDP proxy data paths can hit a
performance/resource wall even though every per-packet allocation contract in `hot-path.md` is
already honored — and record the algorithmic proposals that remove those structural causes, in a
sequenced, benchmark-anchored roadmap.

The full findings live in [research.md](./research.md): F1 single-IOCTL redirect injection,
F2 global lock chain, F3 O(N) stop-the-world sweeps, F4 fat flow keys and repeated parsing,
F5 pump I/O shape, F6 UDP per-session footprint, F7 the WFP ceiling lever.

## Requirements

- Review the whole packet path: capture pump → dispatcher → core tables → TCP redirect → UDP proxy
  → SOCKS5 transports → NDISAPI driver layer.
- Every finding must cite concrete file/line evidence and a cost model, propose at least one
  algorithm/structure that removes it, name its semantic risks, and name the benchmark that proves
  the gain.
- Respect the existing hot-path contracts: proposals must keep the steady-state 0 B allocation
  shape and the fail-closed semantics.
- Record dead-end ideas so they are not re-explored.

## Acceptance Criteria

- [x] `research.md` records F1–F7 with evidence, proposals, risks, and measurement hooks.
- [x] Roadmap orders the work by benefit/risk and names the validation gates for each step.
- [ ] Implementation tasks for the accepted proposals are created as children when picked up
  (out of scope for this research task).

## Notes

- Research-only task; no code changes. Implementation of any accepted proposal gets its own
  child task with design/implement docs.
