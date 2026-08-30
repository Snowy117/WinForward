# Hot dispatch path revival

Parent: `08-30-proxy-perf-stability`. Second implementation child — starts only after
`08-30-fast-hardening` completes (serial ordering, user-confirmed).

## Goal

Backlog #1 (research X1): make the optimized non-async warm dispatch entry execute in
the production composition again. Today `FlowDispatcher.DispatchAsync` diverts 100% of
production packets to `DispatchSlowAsync` because the reverse handler is wired
unconditionally (`Program.cs` run composition), so every landed warm-path optimization
(160 B / ~288 ns dispatch shape, measured 2.44M+ pps) is inert in real runs.

## Background (spot-verified in research)

- `FlowDispatcher.cs:129` (as-of `e5667af`): `if (_logger.IsEnabled(Trace) ||
  _reverseHandler is not null) return DispatchSlowAsync(...)` — the reverse handler is
  always non-null in production (`Program.cs:271` wires
  `tcpCoordinator.HandleReverseIfApplicableAsync`), so the warm entry never runs.
- The reverse handler is TCP-only by construction; UDP and Pass/Block traffic can never
  be reverse candidates but still pay the slow path.

## Requirements

1. **(a) TCP-only diversion gate**: divert to the reverse-handler slow path only for TCP
   packets; all other protocols take the warm entry. Small change, immediate effect.
2. **(b) Listener-port bitmap prefilter** (full fix): a 65536-bit bitmap of
   redirect-listener ports, atomically swapped on listener alloc/free, consulted on the
   warm path so non-candidate TCP packets (nothing listening on the destination port)
   also take the warm entry — only true candidates divert.
3. Correctness invariants: reverse-candidate TCP packets must reach the reverse handler
   with zero behavioral change (ordering, tombstone checks, logging parity); the warm
   entry's existing executor semantics (lease completion before executor runs, batch-slot
   contract) must be preserved.

## Acceptance Criteria

- [ ] **AC1**: test — production-shaped composition (reverse handler wired) executes the
      non-async warm entry for UDP and Pass/Block packets (assert slow-path not entered).
- [ ] **AC2(a)**: test — TCP packets in the production composition still divert to the
      reverse handler exactly as before (no regression in reverse routing behavior).
- [ ] **AC3(b)**: test — bitmap consult logic: candidate port → slow path; non-candidate
      port → warm path; listener alloc/free swaps the bitmap atomically (no torn reads,
      no missed candidates during swap).
- [ ] **AC4**: dispatcher benchmarks re-run on the production-shaped composition; warm
      path allocation stays at the measured 160 B/pkt baseline (no regression).
- [ ] **AC5**: full test suite green, zero-warning build.

## Out of Scope

- Zero-copy data path (backlog #6), batched IOCTLs (#7) — separate children sharing the
  same seams.
- `DispatchNonFlowAsync` non-async variant (deferred item).

## Notes

- Effort estimate from research: (a) S (hours), (b) M. If (a) lands and schedule
  pressures rise, (b) can slip to a later session without stranding (a).
- design.md + implement.md to be completed when this task becomes active (serial order).
