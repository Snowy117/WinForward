# Proxy perf/stability improvement program

## Goal

Parent task for the proxy performance/stability improvement program. It owns the ranked
backlog produced by the 2026-08-30 deep-dive research, the child-task map, cross-child
acceptance criteria, and the final integration review. The product's core value is SOCKS5
proxy forwarding (pass/block are byproducts), so backlog ordering follows proxy-path impact.

This parent is not the implementation target unless it gains direct work; implementation
happens in child tasks.

## Source Material

- Research: `.trellis/tasks/archive/2026-08/08-30-proxy-perf-stability-research/research/`
  - `synthesis-and-backlog.md` — deduplicated ranked backlog (authoritative for ordering)
  - `tcp-redirect-path.md`, `udp-proxy-path.md`, `socks5-capture-dispatch.md`,
    `measurement-gaps.md` — per-path findings with file:line evidence
- Baseline at research time: commit `e5667af`, 431/431 tests, zero-warning build.
- Spot-verified top claims (trust without re-verification): dead production hot dispatch
  entry, missing client RST on mid-flow relay end, missing `NoDelay`.

## Child Task Map

| Child | Backlog items | Status |
|-------|---------------|--------|
| `08-30-fast-hardening` | #2 client RST (R1) + #3 NoDelay/64KiB buffers/stall re-arm throttle (X4, X5, X8a) | completed 2026-08-30 |
| `08-30-hot-path-revival` | #1 revive hot dispatch path (X1): (a) TCP-gate reverse diversion, (b) port bitmap prefilter | completed 2026-08-30 |
| `08-30-atomic-retire` | #4 atomic retire+remove+tombstone + bounded queues (R2, R3, R4): global 8 MiB setup budget + 5 s datagram TTL + tombstone bounds; SYN-path grace check | completed 2026-08-30 |
| `08-30-udp-alloc-jumbo` | #5 UDP allocation zero-out + buffer sizing consistency (X6, R5) | completed 2026-08-30 |
| `08-30-windows-reality` | #9 Windows measurement program (executed on Win11 IoT LTSC VM per user decision): stability matrix, BDN subset, 1h soak, port-pool attribution | completed 2026-08-30 |
| later: zero-copy-datapath | #6 zero-copy proxy data path (X2) — deprioritized by 08-30 VM data: managed path is platform-equivalent, not the Windows bottleneck | on demand |
| later: batched-ioctls | #7 batched reinjection IOCTLs (X3) — elevated by 08-30 VM data: Windows per-IO cost is the structural gap (batching amortizes it) | on demand |
| later: hardening-bundle | #8 keepalive + lock cleanup + ServerGC (R6, X7, X8, R11) | on demand |
| later: driver-resilience | #10 transient driver-error retry + offload listener alloc (R7, R8) | on demand |

New candidates from 08-30-windows-reality (see
`benchmarks/results/2026-08-30-windows-vm/README.md`):

- `port-budget-windows` — high-churn port-pool capacity: deployment guidance (widen
  dynamic range / TcpTimedWaitDelay), loopback-aware upstream error-path RST close
  (symmetric to #2's client RST), harness-side port accounting. Attribution: 96.65 % →
  0.40 % failures on widened pool, pure OS capacity, not a product defect.
- `local-mux-transport` (research first) — fixed connection pool + stream multiplexing
  to the local SOCKS5 server (e.g. VLESS inbound + multiplex) eliminating port churn,
  per-flow handshake cost, and loopback connects; sing-box has no UDS inbound.
- `windows-real-nic` — real-NIC/ETW measurement past the ~4.7k pps loopback ceiling.
- small: UdpSession real-transport populate[1000] NA on Windows.

Deferred (from research §5): SOCKS5 handshake allocation diet, tighter 150s setup budget
(X9), TCP socket buffer sizing docs (R10), per-flow DNS cache, IPv6 perf (fold into
windows-reality-program).

## Requirements

- Each child task must carry its own PRD and acceptance criteria testable in isolation.
- Ordering constraints live in the child PRDs (e.g. hot-path-revival runs after
  fast-hardening).
- Every child must keep the repo at: full test suite green, zero-warning build, and
  allocation-gate benchmarks not regressed (existing benchmark discipline).
- Cross-cutting invariants that no child may break: exactly-once pool returns, OCE token
  discipline, batch-slot stability contract, in-place read-then-write mutation order,
  teardown single-writer semantics.

## Acceptance Criteria

- [x] fast-hardening landed: relay fault/stall produces a client-visible RST; NoDelay on
      both relay legs; pooled 64KiB pump buffers; stall re-arm throttled.
- [x] hot-path-revival landed: production composition executes the non-async warm dispatch
      entry (verified by test or benchmark on the production composition shape).
- [x] atomic-retire landed: retire+alias-removal+tombstone atomic under the store gate;
      TCP tombstone queue drains on sweep; UDP setup tombstones bounded; global 8 MiB
      setup budget + 5 s TTL with charge/credit exactly-once.
- [ ] Each landed child has its research finding re-validated against current source
      (file:line references may have drifted since e5667af).
- [ ] Integration review: after each child, full test suite + build clean; benchmarks
      recorded for affected paths.
- [ ] Backlog table above kept in sync (children created/completed update this file).

## Notes

- Research evidence program (windows-reality-program) is explicitly not product code; it
  should be scheduled early enough to inform whether zero-copy/batched-IOCTL effort is
  worth it (research: "determines where the next optimization dollar goes").
