# UDP allocation zero-out + jumbo buffer sizing (X6 + R5)

Parent: `08-30-proxy-perf-stability` (backlog #5). Research sources:
`.trellis/tasks/archive/2026-08/08-30-proxy-perf-stability-research/research/udp-proxy-path.md`
(P1/P2/P3/P6, S3/S4) and `synthesis-and-backlog.md` (X6, R5). Re-validated at
HEAD `c91ab68`: see `research/current-state.md` (all findings CONFIRMED; P3/S4
refined).

## Goal

The UDP steady-state warm path is the last proxy path with per-datagram heap
allocations, and the transport send buffer is the only datagram-path buffer not
derived from the frame cap that every other component already honors. This task
zeroes the forward/reverse endpoint allocations and makes the send buffer
follow the same single source of truth, eliminating the jumbo-mismatch
handshake-storm failure mode and documenting the oversized-response boundary.

## Requirements

### R1 — Forward leg: no IPEndPoint round-trip (X6 / P1)

`UdpProxySession.SendAsync` already holds the destination as the struct
`Endpoint`; it currently materializes `new IPEndPoint(destination.Address
.ToIPAddress(), port)` (3 allocations per forwarded datagram) only for the
transport interface, which immediately converts back with
`IPAddressValue.From(...)` (stack-only) and sends to the fixed `RelayEndpoint`
anyway.

- `IUdpProxyTransport.SendAsync` takes `Endpoint destination` instead of
  `IPEndPoint`.
- The session forwards its `Endpoint` straight through; the transport encodes
  via the existing `Socks5UdpCodec.TryEncode(IPAddressValue, ...)` overload.
- After this change the forward warm path (uncontended gate, sync kernel send)
  allocates nothing.

### R2 — Reverse leg: decode straight to IPAddressValue (X6 / P2)

`Socks5UdpCodec.TryDecode` currently produces `IPAddress?` (2 allocations per
response: internal byte[] copy + IPAddress) whose only production consumer
immediately converts to `Endpoint` and discards it.

- `Socks5UdpDatagram.DestinationAddress` becomes `IPAddressValue?`
  (nullable struct; `null` still marks a domain-typed datagram).
- IPv6 scope propagation is preserved: the caller-supplied `scopeId` lands in
  `IPAddressValue.ScopeId` exactly as it landed in `IPAddress.ScopeId` before.
- The session's reverse leg constructs `Endpoint.From(IPAddressValue, port)`
  with no framework-address round-trip.

### R3 — Cached receive sender template (X6 / P6)

`Socks5UdpTransport.ReceiveAsync` allocates `new IPEndPoint(IPAddress.Any/IPv6Any, 0)`
per call although the family is fixed at construction.

- The transport caches one sender template per instance, computed from the
  relay address family at construction; `ReceiveAsync` reuses it.

### R4 — Send buffer follows the frame cap (R5 / P3 + S3)

The transport's `_sendBuffer` is sized `6 + 16 + UdpFrameBuilder.MaximumEthernetFrame`
(1536) regardless of the `maximumFrameSize` the coordinator and reinjector are
already constructed with. On a jumbo-capable ABI (cap 9014) every datagram with
payload > 1508 fails `TryEncode` → IOException → catch-all
`RemoveSlotAsync(writeTombstone: false)` → next datagram re-runs the full
SOCKS5 handshake (storm).

- `Socks5UdpTransportFactory` (and the internal `Socks5UdpTransport.CreateAsync`
  seam) accepts the pinned frame cap and sizes the send buffer
  `6 + 16 + cap`, matching how the coordinator sizes receive buffers
  (`cap + 22 + 1`) and the reinjector bounds rebuilt frames (`cap`).
- The production composition (`Program.cs`) passes the same
  `NdisApiAbi.MaximumEthernetFrame` constant it already passes to the
  coordinator and reinjector.
- The fail-closed IOException for a datagram that genuinely exceeds the buffer
  stays (it cannot happen at the pinned ABI since capture bounds payloads at
  `cap − 42`; it is the guard if that assumption ever breaks).

### R5 — Oversized-response policy documented (R5 / S4)

At the pinned 1514 ABI the deliverable response payload ceiling is exactly
1472B; larger relay responses skip with the existing `oversized=` 5s summary
counter and reinjector frame-build failures drop fail-closed with trace +
rate-limited warn. The behavior is correct and follows the cap consistently —
what is missing is an explicit statement of the boundary.

- The boundary (1472B at default ABI; `cap − 42` in general; a jumbo ABI lifts
  it end-to-end including the now-consistent send buffer) is documented in the
  transport and in `udp-relay.md`. No behavioral code change.

### Cross-cutting constraints

- No child may break (parent PRD): exactly-once pool returns, OCE token
  discipline, batch-slot stability, in-place read-then-write mutation order,
  teardown single-writer semantics. This task must not touch teardown paths,
  setup-queue charge/credit sites, or the `SendOnReadySessionAsync` OCE filter.
- Repo discipline: full test suite green, zero-warning build, benchmark
  allocation numbers recorded for the UDP paths (no regression; forward/reverse
  improvements expected).

## Acceptance Criteria

- [ ] Forward warm path: no per-datagram allocation from endpoint handling —
      `IUdpProxyTransport.SendAsync(Endpoint, ...)` in place; a test pins that
      the transport receives the session's `Endpoint` unchanged (address,
      port, family).
- [ ] Reverse leg: `Socks5UdpDatagram.DestinationAddress` is `IPAddressValue?`;
      IPv4, IPv6, and IPv6-with-scope decodes are pinned by tests
      (scope assertion migrated to `IPAddressValue.ScopeId`).
- [ ] Receive sender template is per-transport (no per-receive `IPEndPoint`
      allocation); source-validation behavior unchanged (port + family match).
- [ ] Send buffer derives from the factory-fed frame cap: a transport created
      with a jumbo-sized cap (e.g. 9014 via the internal seam) encodes a
      payload > 1508 without the buffer-size IOException.
- [ ] Production composition passes `NdisApiAbi.MaximumEthernetFrame` to
      `Socks5UdpTransportFactory` (same constant as coordinator + reinjector).
- [ ] All existing tests migrated and green (459+; new tests included),
      zero-warning build, benchmark run recorded with UDP session-populate
      and relevant perf numbers noted in the task notes.
- [ ] `udp-relay.md` updated: endpoint-zero-allocation contract (forward +
      reverse + sender template), frame-cap-derived buffer sizing rule, and
      the oversized-response boundary.
- [ ] Parent PRD backlog table row updated to completed for this child.

## Out of scope

- Raising the native frame cap (jumbo ABI recompile) — windows-reality-program.
- P4/P5/P7/P8/P9, S1/S2 (landed by atomic-retire), S5/S6/S7 — other backlog
  items or explicitly deferred by the research ranking.
- SOCKS5 handshake allocation diet (deferred in the research §5).
