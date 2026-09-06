# UDP burst first-datagram TTL re-attribution

Parent: `08-30-proxy-perf-stability`. Source findings: `09-06-udp-burst-establishment`
(`benchmarks/results/2026-09-06-udp-burst/README.md`).

## Goal

Fix the measured burst-establishment loss mechanism: under a flash crowd of new UDP
flows with effective dial latency D, setups serialize on the 8-wide limiter in waves of
8, and wave k's triggering datagram is k×D old at flush — the 5 s setup datagram TTL
(enqueue-stamp) drops every wave with k×D > TTL. Measured corner: 128 flows × 4 s dial
→ only wave 1's 8 datagrams delivered, 93.75 % first-datagram loss. The datagram is not
client-stale; it merely waited on our own admission queue.

## Requirements

- R1 — TTL re-attribution: a datagram's staleness must not accrue while its flow's
  setup is still waiting on the `_setupLimiter` (age from the flow's dial start, or
  re-stamp queued datagrams when the limiter admits the setup). Client-side staleness
  still bounded: total retention ≤ dial start + TTL.
- R2 — Memory invariants preserved: 8 MiB global budget charge/credit exactly-once,
  per-slot 32-datagram/32 KiB bounds, bounded tombstones — all unchanged. The change
  alters age semantics only, never the budget accounting.
- R3 — Acceptance gate (required before landing): re-run the `udp.burstEstablishment`
  matrix at the 2026-09-06 points. The 128 × 4000 ms probe's
  `establishmentLossRate` must reach 0 (or a documented residual with mechanism);
  latency points within noise of the model; background windows still zero-loss,
  sub-0.1 ms send p95. This gate is also the spec contract recorded in
  `.trellis/spec/backend/udp-relay.md`.
- R4 — Tests: fake-`TimeProvider` unit coverage for the wave semantics (datagram
  enqueued at t0, dial starts late, flush at t0+long — delivered under re-attribution,
  dropped when older than dial-start + TTL). Existing `UdpSetupQueueTests` TTL test
  updated to the new semantics deliberately, not deleted.

## Constraints

- Product change confined to `UdpProxyCoordinator` / `BoundedSetupQueue` seam per
  `.trellis/spec/backend/udp-relay.md` §Bounded UDP setup memory.
- Full suite green, zero-warning build, BDN allocation gates unregressed.

## Non-Goals

- Limiter width changes (separate decision, see `09-06-local-mux-transport` for the
  structural alternative).
- Harness changes — the scenario exists and is the gate.

## Acceptance Criteria

- [x] R3 matrix re-run recorded under `benchmarks/results/` with before/after probe
      comparison (93.75 % → target 0 %). — `2026-09-06-udp-burst-ttl-fix/`:
      probe 8/128 → 128/128, loss 0.9375 → 0; spot points within noise.
- [x] Spec contract bullet updated with post-fix semantics.
- [x] Unit tests per R4; existing suite green (505/505; mutation-verified).
