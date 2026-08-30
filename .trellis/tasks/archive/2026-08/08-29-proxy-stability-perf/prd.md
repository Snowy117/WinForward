# Proxy Stability and Performance Hardening

## Goal

Eliminate the stability defects and performance hotspots identified by the 2026-08-29 full-path
audit of the SOCKS5 TCP/UDP proxy data paths, without regressing fail-closed semantics, the
zero-allocation hot-path contracts, or the frozen configuration schema.

## Background

Audit sources: full code read of `src/WinForward.Runtime/TcpRedirect/`, `UdpProxy/`, `Socks5/`;
the 386-test suite; benchmark artifacts under `benchmarks/results/2026-08-29-*`. Every defect
below was verified against source at commit `46cc639` ("close the remove-tombstone two-lock
window"). The two-lock-window flaky from the same audit is already fixed at HEAD and out of
scope.

## Problem Catalog

### Stability

- **S1 (high) — IP fragments on an established proxied TCP flow produce broken, partly silent
  behavior.** The endpoint rewriter rejects any frame with fragment bits
  (`PacketChecksums.cs:104-105`), and the flow parsers reject fragments as non-flow
  (`IPTcpUdpPacket.cs:70-71`, `IPUdpPacket.cs:38`), so mid-flow fragments either (a) pass
  unproxied toward the real server — a policy leak that invites a stray RST from a server that
  never saw the connection — or (b) fail the rewrite on a resolved association and tear the live
  relay session down without a client-visible RST (`TcpRedirectSessionStore.cs:258-264`), after
  which the 60 s tombstone silently swallows retransmissions. Either way the client experience is
  undefined; PMTUD-less forwarded VM traffic is a realistic trigger.
- **S2 (high) — UDP relay socket does not disable `SIO_UDP_CONNRESET`.** No `IOControl` call
  exists anywhere in `src/` (verified by search). On Windows, an ICMP port-unreachable for a
  destination the relay socket wrote to surfaces as `SocketError.ConnectionReset` on the next
  receive; the receive loop exits through its generic catch (`UdpProxySession.cs:193-196`), the
  session is torn down without a tombstone, and the next outbound datagram re-ASSOCIATES. A noisy
  or hostile path can churn sessions (new TCP control connection + new UDP socket per cycle).
- **S3 (medium) — Unobserved relay task exceptions.** `TcpProxyRelay._completion`
  (`TcpProxyRelay.cs:73-86`) is awaited only by `ObserveRelayCompletionAsync`, which runs only
  when relay attach succeeds (`TcpRedirectAcceptor.cs:74,137-141`). On the attach-failure path
  and in `DisposeAsync`, a faulted completion is never observed → unobserved task exceptions.
- **S4 (medium) — Capacity rejection gives the client no feedback.** A SYN arriving at the
  session budget is silently dropped (`TcpProxyCoordinator.cs:118-123`); client stacks retransmit
  for ~20-60 s before failing. No RST is injected on this path.
- **S5 (medium, stability + perf) — Per-SYN `NetworkInterface.GetAllNetworkInterfaces()` on the
  capture pump thread.** `WindowsAdapterLocalAddressProvider.SelectLocalAddress` enumerates all
  interfaces on every new forwarded-flow SYN (`AdapterLocalAddressProvider.cs:46-49`); SYN bursts
  amplify into a system-call storm on the serial per-adapter pump.
- **S6 (low) — Observability gaps.** (a) Domain-typed relay responses are dropped silently
  without a counter (`UdpProxySession.cs:180`). (b) A dead UDP relay produces an unrate-limited
  warn per datagram (`NdisPacketActionExecutor.cs:158-161`). (c) The blocked-flow warn text
  claims "proxy relay support is not initialized in this build" for every `Blocked` reason
  including capacity/claim/rewrite (`NdisPacketActionExecutor.cs:182`) — misleading diagnostics.
  (d) The idle sweeper swallows sweep exceptions with no log at all (`IdleExpirySweeper.cs:86-91`).

### Performance

- **P1 — Relay pump CTS churn.** Each 8 KB chunk per direction allocates a linked CTS + stall
  timer on both the read and the write (`TcpProxyRelay.cs:131-132,149-150`); this is the dominant
  relay allocation hotspot at high throughput.
- **P2 — Scalar full checksum recomputation.** (a) The endpoint rewrite recomputes full IP+TCP
  checksums over the entire segment per redirected packet (`PacketChecksums.cs:97-138`) when an
  incremental update (RFC 1624) over the changed words would suffice. (b) The UDP response
  rebuild path computes the Internet checksum word-by-word with no vectorization
  (`PacketChecksums.cs:9-24,195-199`; `UdpFrameBuilder.cs`) — the dominant CPU cost of
  reinjection.

## Requirements

- R1 (S1): Fragments belonging to an associated proxied TCP flow must never be passed toward the
  real server, and the resulting teardown must be client-visible (RST when observed sequences
  permit) instead of a silent 60 s blackhole. A distinct trace reason (`fragment`) must be
  observable. Fragment reassembly itself remains out of scope.
- R2 (S2): An ICMP-driven `ConnectionReset` on a relay socket must not terminate the session:
  disable `SIO_UDP_CONNRESET` on relay sockets, and treat `ConnectionReset` in the receive path
  as a skip-class anomaly (counted, loop survives), consistent with the existing resilience
  pattern.
- R3 (S3): Every relay detach/dispose path must observe `Completion`; a faulted relay must never
  surface as an unobserved task exception.
- R4 (S4): A capacity-rejected SYN must elicit an in-window RST|ACK to the client so
  well-behaved clients fail fast with ECONNREFUSED. Injection must be once-per-tuple with a
  bounded cooldown to prevent reflection amplification from spoofed sources.
- R5 (S5): Steady-state forwarded SYN processing must not enumerate network interfaces per SYN;
  adapter local addresses must come from a cache invalidated by `NetworkChange` events (with a
  bounded safety TTL). Enumeration failure semantics stay fail-closed for forwarded flows with no
  local address.
- R6 (S6): All four observability gaps closed: domain-drop counter, rate-limited dead-relay
  warn, reason-accurate blocked-flow warn text, logged sweep failures. New events/fields must
  comply with the logging spec (metadata only, no payloads).
- R7 (P1): The relay warm path must not allocate a CTS+timer per chunk; use CTS reuse
  (`TryReset`) or an equivalent pattern. No throughput regression in `TcpRelayBenchmarks`.
- R8 (P2): Endpoint rewrite uses incremental checksum update with bit-exact equivalence to the
  current full recompute (property tests); the rebuild path uses a vectorized Internet checksum
  where benchmarks justify it, with the scalar path as fallback and identical 0↔0xFFFF semantics.

## Acceptance Criteria

- [ ] AC1: Existing 386-test suite passes, zero-warning build, and every fix adds at least one
  pinning test; S1 starts with characterization tests that document the current fragment behavior
  per shape (host/forwarded, forward/reverse leg) before semantics change.
- [ ] AC2 (S1): Test proves a mid-flow fragment on an associated flow is consumed (never passed
  toward the real server), triggers teardown with a client-visible RST when sequences are known,
  and emits a `reason=fragment`-style trace event.
- [ ] AC3 (S2): Test proves a `ConnectionReset` from the transport is classified as a skip and
  the session survives (send/receive continue); relay socket setup applies the
  `SIO_UDP_CONNRESET` IOControl (asserted via test seam).
- [ ] AC4 (S3): Test proves a faulted relay completion on the attach-failure and dispose paths is
  observed (no unobserved-exception path remains; asserted via seam, not GC timing).
- [ ] AC5 (S4): Test proves a capacity-rejected SYN produces exactly one RST|ACK per tuple within
  the cooldown window and none for subsequent retransmissions inside it.
- [ ] AC6 (S5): Test proves per-SYN processing performs zero interface enumerations in steady
  state (counting seam), and that cache invalidation refreshes addresses after a change event.
- [ ] AC7 (S6): Tests pin the new counter, the rate-limited warn, corrected warn text per blocked
  reason, and the sweep-failure log.
- [ ] AC8 (P1): `TcpRelayBenchmarks.OneWayAsync` shows per-chunk managed allocation reduced to
  ~0 on the warm path with no throughput regression (>5% loss blocks landing).
- [ ] AC9 (P2): Property tests prove incremental-rewrite checksums are bit-identical to full
  recompute across randomized payloads/addresses; checksum benchmarks show the expected
  improvement; `TcpEndpointRewriteTests` roundtrip and byte-exact tests pass unchanged.
- [ ] AC10: Linux quick soak (`--quick`) passes with zero loss and zero regressions vs the
  2026-08-29 baselines; full-suite + benchmark results recorded under
  `benchmarks/results/<date>-proxy-hardening/`.

## Constraints

- Fail-closed semantics are preserved: proxy-selected traffic is never silently passed.
- Configuration schema is frozen: no new user-facing config fields.
- Zero-allocation hot-path contracts (`spec/backend/hot-path.md`) apply to all data-path changes.
- Logging: single-line stderr records, metadata only, no payloads/credentials.
- Windows-only runtime (.NET 10); tests must remain Linux-runnable via seams.
- Documentation and spec updates in English; project comment style rules apply.

## Out of Scope

- IP fragment reassembly and SOCKS5 UDP FRAG support (policy unchanged: fail-closed).
- Windows BDN perf matrix run and hours-long soaks (recorded as follow-ups).
- Wildcard-listener spoof hardening beyond a documented decision (residual risk accepted; the
  listener binding is required by the WinpkFilter local-redirect design).
- The already-fixed tombstone two-lock-window flaky.
