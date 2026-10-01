# F6 UDP per-session footprint: relay receive buffer, pool sizing, adaptive idle TTL

Parent: `08-30-proxy-perf-stability`. Finding F6 of the archived research
`09-29-tcp-udp-path-structural-perf` (`research.md` §F6). This is step 7 of the operator's F2–F8 pipeline;
F3, F2, F4, F5 and F8 are archived and their contracts are the regression surface. The task that built the
footprint instruments (`09-29-benchmark-coverage-remaining-findings`) judged F6 **adequate** for its
measurement half — this task implements the change that half was built for.

## Problem

Every UDP session owns resources sized for a world it does not live in:

| # | Resource | Shape today |
|---|----------|-------------|
| 1 | Relay socket receive buffer | **128 KiB** per session by default (`Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`) — at the 16,384-session capacity that projects to ~2 GiB of **kernel** receive memory, held for the session's whole life regardless of whether it is an idle DNS one-shot or a saturated stream |
| 2 | Native receive-window lease | the pool's default capacity is 256, so every session beyond the 256th runs on a **tracked overflow allocation** (1,541 B per lease: 1,537 B buffer + 4 B state word) instead of the steady-state pool — visible as per-cycle overflow **churn**, because the free list can never fill |
| 3 | Parked receive loop | one pending IOCP read per session — irreducible given the design, but its retention is driven by the idle TTL, which today is uniform |

Recorded instruments: `udp.sessionBudget` under `benchmarks/results/2026-09-28-udp-reuse/` (kernel-buffer
estimate, descriptor budget, drain-to-zero; a 1 h artifact is on record). The newest `udp.sessionFootprint`
rows live under `benchmarks/results/2026-08-29-udp-fix/` and predate F2–F8; they are cited as **historical
shape** only, and this task captures its own before/after pair.

## Requirements

1. **The default per-session relay receive buffer drops to a documented 32–64 KiB**, chosen with the
   burst-absorption trade written down (the research's range), and the existing capacity×buffer validation
   still warns on an oversized product. A config override keeps today's value reachable for a deployment
   that wants it.
2. **The receive-window pool is sized from the session capacity at composition**, so steady state stops
   running on tracked overflow allocations. Proven by an exact overflow-count gate: at the configured
   session capacity with that many live sessions, the pool's overflow counter stays zero.
3. **Idle retention adapts to the exchange shape.** A one-shot exchange (the UDP majority — DNS) gets a
   short TTL (~5 s, the research's figure); a session with sustained exchange keeps the long one. The
   classification signal comes from the per-flow datagram counters the capability-evidence path already
   maintains, so the decision is nearly free, and the threshold is a documented constant, not a heuristic
   spread across call sites.
4. **Semantics preserved**: a sustained flow is never retired early; a one-shot flow's reply still lands
   inside its short TTL; the burst/loss scenarios keep their shape anchors (`UdpBurstScenario`,
   `UdpLossScenario`), the retention/teardown tests stay green, and the fail-closed admission behaviour is
   untouched.
5. **The footprint is measured, before and after**: `udp.sessionFootprint` at 1/100/1000 sessions and the
   `udp.sessionBudget` soak's kernel-buffer estimate and descriptor budget, recorded under
   `benchmarks/results/2026-10-01-udp-session-footprint/`; the adaptive TTL's effect on the resident set is
   shown by the one-shot vs sustained population, not asserted from the configuration alone.
6. **Existing gates stay green**: full suite, the F2/F3/F4/F5/F8 facts, gc-soak anchors, the UDP admission
   budgets, and the sweep gates (the idle-expiry legs this task touches are part of F3's matrix).
7. **The dead end stays dead**: a shared relay socket with destination-based demultiplexing is **not**
   revisited — the SOCKS5 UDP reply header carries only the peer address, so several client flows to the
   same destination cannot be disambiguated (a NAT requirement; per-flow sockets are irreducible).

## Acceptance Criteria

- [ ] **Buffer default (exact + recorded).** The composition's default per-session relay buffer is the chosen
      32–64 KiB value (asserted), an explicit override restores the old value (asserted), and the
      capacity×buffer validation still warns above its threshold; the trade is recorded in the artifact
      README with the projected kernel-memory change at the shipped capacity.
- [ ] **Pool sizing (exact).** With the pool sized from the session capacity, a **second** population cycle at
      that capacity produces **zero overflow growth** — the counter is cumulative ("rents that missed the
      free list"), so a first fill of N sessions reads N at any capacity and an absolute `== 0` would be
      unfalsifiable by construction. The red-before is the same cycle at the old capacity 256: **+44** new
      overflow allocations at N = 300, versus **+0** after, plus the balance identity
      (`Rented == Returned`, `Outstanding == 0`, `InPool == 300`). The per-lease cost in the arithmetic is
      **1,541 B** (1,537 B buffer + 4 B state word), not "~1.6 KiB".
- [ ] **Adaptive TTL (exact + series).** A one-shot flow is retired on the short TTL and a sustained flow
      keeps the long one (exact boundary facts at both edges), the classification uses the existing
      per-flow counters, and the footprint series shows the resident-set effect at a mixed population.
- [ ] **Footprint documented.** `udp.sessionFootprint` (1/100/1000) and the `sessionBudget` soak's kernel
      estimate, descriptor budget and drain-to-zero are re-recorded and cited.
- [ ] Release build zero-warning, full suite green, `dotnet format --severity info --verify-no-changes`
      empty output, `jb inspectcode` zero `<Issue>`.
- [ ] Benchmark data supporting each claim recorded and cited in the task record before archive.

## Notes

- **Sanctioned instrument re-base.** The `sessionBudget` soak's sweep cadence moves with the UDP tick
  (15 s → 5 s), so its steady-state ceiling and any row derived from it must be re-derived from the
  **measured resident set**, not from a hand constant, with the load's meaning restated and every moved row
  enumerated. A soak that silently stops testing what it names — because a ceiling and a load inverted — is
  not acceptable, and neither is keeping a ceiling that no longer follows from the effective retention.
- The research's three proposals are the input; the design may defer one with reasons (the pool sizing is
  the cheapest and most mechanical; the TTL change touches retention semantics and needs the boundary facts).
- The one-shot/sustained classification must not become a new per-datagram cost: it reads counters that are
  already maintained, and the design states exactly where the decision is made and how often.
- Out of scope: the shared-relay-socket dead end, the relay-socket-per-flow invariant, anything F2/F3/F8
  already landed, and the Windows-only measurement rows.
