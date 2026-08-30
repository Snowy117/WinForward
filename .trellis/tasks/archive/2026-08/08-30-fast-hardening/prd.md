# Fast hardening: client RST + NoDelay + pooled pump buffers

Parent: `08-30-proxy-perf-stability`. First implementation child (serial before
`08-30-hot-path-revival`).

## Goal

Land the four highest user-visible-impact-per-effort proxy hardening items from the
2026-08-30 research (backlog #2 + #3): make relay faults/stalls client-visible instantly,
remove Nagle latency cliffs, raise the per-flow throughput ceiling, and cut timer-op
overhead — all small, independently verifiable changes on the TCP relay path.

## Background (from research, baseline e5667af)

- **R1 (HIGH)**: mid-flow relay fault/stall leaves the client blackholed for minutes
  (ETIMEDOUT) — `ObserveRelayCompletionAsync` swallows and tears down without a client
  reset; the stall path returns normally so even the catch is skipped.
  `ClientResetInjector.TryInjectClientResetAsync` already builds the in-window RST|ACK;
  only the relay-*setup* failure path calls it today.
- **X4 (HIGH, latency)**: no `NoDelay` anywhere in `src/` — Nagle × delayed-ACK gives
  40–200ms stalls on interactive traffic through the proxy.
- **X5 (MED-HIGH)**: 8 KiB fixed pump buffers — syscall/memcpy per-byte cost ~8× a
  64 KiB pooled buffer.
- **X8a (MED-LOW)**: `StallWindow.Arm()` twice per 8KiB chunk ≈ 5–10% of a core at
  10 Gbps single flow.

Full evidence with file:line in
`archive/2026-08/08-30-proxy-perf-stability-research/research/tcp-redirect-path.md` (S1,
P1, P2, P3) and `research/synthesis-and-backlog.md` §2–§3.

## Requirements

1. **R1 — client-visible reset on relay end**: when a relay ends `Stalled` or `Faulted`,
   inject a client RST via the existing `ClientResetInjector` before session teardown, so
   the client's original-destination connection aborts immediately instead of
   retransmitting into a tombstone. Clean (FIN-propagated) ends must NOT send RST.
2. **R1 — end-kind surface**: `TcpProxyRelay` exposes why it ended (internal
   `RelayEndKind { CleanEnded, Stalled, Faulted }`); the acceptor consumes it.
3. **X4 — `NoDelay = true`** on the accepted socket (listener side) and the upstream
   socket (after `ConnectAsync` succeeds).
4. **X5 — pump buffers**: 64 KiB rented from `ArrayPool<byte>.Shared` per direction,
   returned in `finally`; steady-state memory must stay bounded (pool reuse).
5. **X8a — stall re-arm throttle**: only re-`Arm()` when >1s elapsed since the last arm
   (Stopwatch); 30-min window drift is irrelevant; lifetime-token link semantics
   unchanged (never disarmed between operations).

## Constraints

- Do not alter teardown single-writer semantics, pool exactly-once return, or OCE token
  discipline (parent-task invariants).
- Follow `.trellis/spec/backend/hot-path.md` conventions for anything on the pump path.
- No new configuration surface unless a requirement is impossible without it (research
  suggests none is needed for these four items).

## Acceptance Criteria

- [ ] **AC1 (R1-fault)**: test — a relay that faults mid-flow (upstream reset) results in
      a client-directed RST|ACK being injected (observable via the existing injector seam
      / fake) before teardown; the association's trackers are used for in-window seq.
- [ ] **AC2 (R1-stall)**: test — a relay that ends via the stall path triggers the same
      client reset.
- [ ] **AC3 (R1-clean)**: test — a clean relay end (FINs propagated) does NOT inject a
      client reset.
- [ ] **AC4 (X4)**: accepted and upstream sockets have `NoDelay = true` (assert in
      existing relay/listener tests or add one).
- [ ] **AC5 (X5)**: pump loop rents 64 KiB pooled buffers and returns them on all paths
      (normal end, fault, dispose); no per-chunk allocation added (existing relay
      benchmark allocation gate must not regress).
- [ ] **AC6 (X8a)**: re-arm is throttled; stall-window behavior otherwise unchanged
      (existing stall tests stay green).
- [ ] **AC7**: full test suite green, zero-warning build; `TcpRelayBenchmarks` and
      `tcp.*` stability scenarios run clean.

## Out of Scope

- TCP keepalive + configurable/lower stall default (backlog #8 hardening bundle).
- Atomic retire+remove+tombstone (backlog #4, own child).
- Reverse-lookup/lock cleanups (X7 family).
- Any dispatch-path change (hot-path-revival child, runs after this task).
