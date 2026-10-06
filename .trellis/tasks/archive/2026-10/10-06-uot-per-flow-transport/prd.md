# UoT v2 per-flow UDP transport for SOCKS5 targets

Parent: `08-30-proxy-perf-stability`. Sibling: `09-06-local-mux-transport` (the mux research
track — unaffected by this task; see Out of scope).

## Goal

Cut the per-flow establishment cost and descriptor footprint of proxy-decided **non-DNS** UDP
flows by adding an opt-in UDP-over-TCP v2 (connect mode) transport mode to the existing SOCKS5
target, measured against today's native per-flow SOCKS5 UDP path.

The DNS-shaped population that motivated association sharing already has its strictly better
placement (local targets, `10-05-local-dns-transport`). What remains on the SOCKS5 UDP path is
the population the correctness program recorded as sharing's remaining sweet spot — short non-DNS
request/response flows — and every one of those flows still pays a serialized ≈7 ms
connect + `UDP ASSOCIATE` and holds two local descriptors before its first datagram can leave.

User value: the first datagram rides the handshake flight instead of waiting out three serialized
round trips, and each live flow costs one descriptor instead of two — without giving up the
shipped one-flow-per-association correctness guarantee.

## Background and confirmed facts

- **[recorded] Today's serialized cost.** Per flow: control connect 2.46 ms + ASSOCIATE total
  4.49 ms (≈7 ms), then the relay socket; two descriptors per live flow (control connection +
  relay socket); 96/144/192 handshakes per 48-flow churn wave
  (`10-05-udp-association-sharing-correctness/prd.md` Background;
  `benchmarks/results/2026-10-05-no-association-sharing/`).
- **[recorded] The serialization is a data dependency, not a message count.** The first datagram
  cannot leave until the ASSOCIATE reply's `BND.ADDR` names the relay endpoint
  (`Socks5ControlConnection.cs:219-226`, `Socks5UdpAssociation.cs:64-75`).
- **[recorded] The correctness baseline is not negotiable.** One flow per association; the
  failure modes that banned sharing (concurrent misdelivery, same-destination undetectability,
  stale-reply window) are recorded in
  `10-05-remove-udp-association-sharing/research/decision-record.md`. UoT per-flow extends the
  same guarantee: one flow per connection.
- **[recorded] The seam is ready.** Transport-neutral `IUdpProxyTransport` /
  `IUdpProxyTransportFactory` and the composite dispatch (`UdpTransportContracts.cs:92-143`);
  the L2 obligations are mapped in
  `10-05-remove-udp-association-sharing/research/l2-readiness.md`. For the per-flow shape, its
  shared-connection exceptions (§2.3 alias key, §2.5 per-connection self-traffic, §2.8 ownership)
  do not apply.
- **[external] Server side needs no change.** sing-box's socks inbound wraps `uot.NewRouter`
  unconditionally; UoT v2 connect mode is a `CONNECT` to `sp.v2.udp-over-tcp.arpa` followed by
  `u16be`-length-prefixed datagrams on the same stream. Facts and sources:
  `research/uot-v2-protocol.md`. Confirmation against the exact build is R7.

## Requirements

- **R1 — UoT v2 connect-mode transport, one TCP connection per flow.** Dialed, owned, and
  disposed with the flow, exactly the ownership shape of today's association. No sharing, no
  pooling, no warm reuse across flows.
- **R2 — Pipelined establishment.** No handshake reply is awaited before the first datagram: the
  dial writes the greeting (and the auth message when credentials are configured) without reading a
  reply, and the first send writes the CONNECT request, the UoT request header, and the flow's queued
  datagrams in one flight. Reply validation moves to the receive loop (R4). The client must not be
  able to hammer a server that refuses the exchange.
- **R3 — Stream framing and receive loop.** `u16be` length-prefixed datagrams over the stream;
  the receive loop is partial-read-safe (a datagram split across segments is reassembled, not
  dropped); the oversized rule keeps its meaning at the existing receive-window constant — the
  2-byte framing is smaller than the SOCKS5 UDP header it replaces, so no seam constant changes.
- **R4 — Fault vocabulary.** Two classes, each mapped to an existing teardown reason: a rejection
  discovered late (a non-success CONNECT reply, or EOF before it) is
  `UdpTransportHandshakeRejectedException` → `UdpTeardownReason.SetupFailure`, arming the setup
  cooldown exactly as a refused `UDP ASSOCIATE` does today; a connection death after establishment is
  `UdpAssociationLostException` → `UdpTeardownReason.AssociationLost` (counted, no cooldown, the flow
  re-establishes on its next datagram). No raw `ConnectionReset` may escape the transport: the
  session's receive loop treats that code as a per-datagram skip and would spin on a dead stream.
- **R5 — Seam obligations.** The transport implements `IUdpExchangeCounters` from its own
  observations (the completed-one-shot 5 s retirement class is preserved); registers its TCP
  tuple in `SelfTrafficRegistry` before the dial; exposes per-flow `PeerEndpoint`/`LocalEndpoint`
  from the connection so the alias claim works unchanged; and the UoT path is distinguishable
  from the native path in logs/counters (a `targetKind` token or an equivalent field — the choice
  belongs to design).
- **R6 — Configuration.** An opt-in field on the existing SOCKS5 server DTO (working name
  `udpOverTcp`); default off — the native UDP relay path is unchanged and remains the default.
  The loader's unknown-property fail-closed behaviour is preserved, and the field collects
  whatever validation warnings it implies (the non-loopback warning precedent).
- **R7 — Server verification before behaviour locks.** Pin the exact sing-box build in use and
  confirm, against it and its documentation: socks inbound UoT support, connect-mode semantics
  (one fixed destination per connection), the reply write path for that connection (the same
  `routePacketConnection` machinery as the recorded SOCKS5 defect — confirm, don't assume), and
  UoT v2 availability at that version — **including the pipelined flight itself** (greeting, auth
  when configured, CONNECT, UoT request header, and the first datagram written without awaiting any
  reply). If the pinned build mishandles the flight, the implementation falls back to awaiting the
  method/auth replies at create (design §3) without changing the transport's external behaviour.
  Findings land in `research/`.
- **R8 — Harness evidence.** A UoT column beside the native per-flow column in `udp.churn`,
  `udp.burstEstablishment`, and `udp.sessionBudget`, reporting first-response p50, handshake
  counts, bytes/session, and the burst wave shape. The mode is justified by the numbers it
  produces, in this task's `benchmarks/results/` directory.
- **R9 — Documentation.** README gains the mode's description including the TCP-carrying caveat
  (per-flow head-of-line, QUIC-over-TCP congestion stacking; native relay stays the recommendation
  on lossy legs); `.trellis/spec/backend/udp-relay.md` gains the transport's ownership section,
  extending the one-flow-per-association guarantee to one-flow-per-connection.

## Out of scope

- **UoT non-connect mode** (per-packet addressing, cross-destination sharing): two live flows to
  one destination are indistinguishable on the wire — the structurally undetectable
  same-destination ambiguity the correctness program banned (decision record §2).
- **Pooling or warm reuse of UoT connections across flows**: the stale-reply window of the
  rejected exclusive-lease pool (decision record §4).
- **VLESS / XUDP / mux**: remains `09-06-local-mux-transport`'s research question. This task's
  UoT mode landing does not close that track; the burst wave shape (`ceil(N/8) × D`) keeps its N
  under UoT.
- Changes to the TCP redirect path, to sing-box, or anything smelling of payload inspection
  (the permanently rejected clause).
- Relay-socket sharing in any form.

## Constraints

- Repository quality gates: `dotnet format` empty output, Release zero-warning build, green
  tests, `jb inspectcode` zero `<Issue>` entries.
- Managed, native-AOT-published Windows executable; no new native dependency. The benchmark and
  stability harness stays managed-only and keeps running on Linux.
- Never commit or publish the user's real configuration; `__RUNTIME_*__` placeholder secrets and
  redaction rules apply to every artifact, harness configs included.
- The shipped guarantee ("one flow per association, so the reply-ownership ambiguity a shared
  association has cannot occur") must remain literally true after this task, extended to the UoT
  mode as one flow per connection.

## Acceptance criteria

- [ ] A SOCKS5 target with the UoT field on serves `udp.churn` / `udp.burstEstablishment` /
      `udp.sessionBudget` flows with zero misdelivery and exactly one TCP connection per live
      flow, disposed with the flow (harness-observed, like the 96/144/192 handshake accounting).
- [ ] A flow's first datagram is written before any handshake reply is read — observable in a
      scripted-server test that reads the datagram before writing the CONNECT reply — a late-discovered
      rejection tears the flow down as `SetupFailure` (cooldown armed), and a mid-flow connection death
      is an `AssociationLost` re-establishment with no cooldown.
- [ ] The UoT harness column shows the first-response p50 and burst-wave improvement against the
      native per-flow column, or the task's decision memo records why the measured delta does not
      justify the mode (in which case it still ships off-by-default).
- [ ] A completed one-shot flow over UoT keeps the 5 s retirement class (the transport implements
      `IUdpExchangeCounters`).
- [ ] The R7 server-verification memo, pinned to the exact build, is committed under `research/`.
- [ ] Quality gates green; README and `udp-relay.md` updated per R9.

## Notes

- Predecessor context: this is the L2 readiness note's "UoT v2 connect" candidate
  (`10-05-remove-udp-association-sharing/research/l2-readiness.md`), narrowed to the per-flow
  shape so that none of its shared-connection seam exceptions apply.
- `09-06-local-mux-transport` R1–R4 stay valid research for the mux question; nothing here
  reuses or supersedes them.
- Design decisions taken with the user at planning time (all recorded in `design.md`): UoT is a mode
  of the existing SOCKS5 target (`udpOverTcp` on the server entry, default off, native unchanged),
  one connection per flow with no sharing or pooling, the whole handshake pipeline of R2, the fault
  classes of R4, a `udpTransport` log field beside an unchanged `targetKind`, and `--target uot` as
  the harness knob. The harness numbers decide whether the mode is ever *recommended*; they do not
  decide whether it ships (it ships off by default).
