# Local mux transport research for UDP burst and port churn

Parent: `08-30-proxy-perf-stability`. Elevated from the parent's "new candidates"
backlog by the `09-06-udp-burst-establishment` findings.

## Goal

Decide (research, no product code) whether a fixed-connection-pool + stream-multiplexed
local transport to the SOCKS5 server should replace per-flow dialing. Three measured
problems share this root: burst establishment wave latency (`ceil(N/8) × D`),
first-datagram TTL loss under slow dials (93.75 % at the 128 × 4 s corner), and Windows
dynamic-port exhaustion under churn (96.65 % throughput failures at default pool,
`results/2026-08-30-windows-vm/`).

## Requirements

- R1 — Server-side mux verification: confirm the target SOCKS5 server ecosystem
  (sing-box VLESS inbound + multiplex; no UDS inbound, issue #733 closed) supports the
  client-side shape we would implement; document protocol/limits (stream caps,
  UDP-over-mux semantics — does UDP still need per-flow ASSOCIATE or can relays share
  a pooled control connection?).
- R2 — Loopback mux throughput measurement: prototype-level measurement of mux vs
  per-flow dial on loopback (both Linux and the Windows guest process where feasible);
  compare against the `udp.burstEstablishment` matrix shape.
- R3 — Elimination estimates: expected burst first-response latency (target: wave
  collapse to ~1×D), port consumption per flow, memory per session, failure modes
  (head-of-line blocking *inside* the mux — the new risk this introduces).
- R4 — Rollout shape: how a pooled/mux transport slots into
  `IUdpProxyTransportFactory` / `Socks5UdpTransportFactory` composition
  (`Program.cs` `CreateUdpCoordinator`) as an opt-in server-type; config surface;
  rollback path.
- R5 — Decision memo: go/no-go with ranked evidence; if go, seed the implementation
  child task's PRD; if no-go, record why and re-rank the parent backlog.

## Constraints

- Research artifacts live under this task's `research/` directory; no product changes.
- UDP relay semantics must stay spec-conformant (`.trellis/spec/backend/udp-relay.md`)
  — any mux shape that breaks source validation or self-traffic registration is out.

## Acceptance Criteria

- [ ] Research notes covering R1–R4 with sources (issue links, measured numbers).
- [ ] Decision memo (R5) committed to the task dir; parent backlog re-ranked or a
      child implementation task created accordingly.

## Notes

- Ordering: independent of `09-06-udp-burst-ttl-attribution` (the small fix can land
  first; if mux lands later, the TTL re-attribution remains correct and harmless).
