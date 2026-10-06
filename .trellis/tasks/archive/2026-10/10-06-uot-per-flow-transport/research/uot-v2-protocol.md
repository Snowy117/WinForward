# UoT v2 protocol facts — external sources, collected 2026-10-06

Collected during planning for `10-06-uot-per-flow-transport`. **Nothing here is verified against
the exact sing-box build in use** — R7 of the PRD owns that confirmation. Sources are public
sing-box documentation and source mirrors; each fact names its origin. §1's request-header address
types were corrected on 2026-10-06 after `research/r7-server-verification.md` §2 pinned them against
the sing and sing-box sources; the on-box half of that confirmation is still open.

Two markers, same convention as `10-05-remove-udp-association-sharing/research/l2-readiness.md`:

- **[external]** — sourced from the public artifact cited inline; not yet confirmed against the
  operator's build.
- **[analysis]** — this repository's own reasoning on top of recorded + external facts.

## 1. Wire format (UoT v2)

**[external]** sing-box "UDP over TCP" shared-option documentation
(<https://sing-box.sagernet.org/configuration/shared/udp-over-tcp/>):

- UoT is a SagerNet proprietary protocol; version 2 is the default and is supported by sing-box
  since v1.2-beta9.
- The client requests the magic address `sp.v2.udp-over-tcp.arpa` from the upper-layer proxy
  protocol (e.g. a SOCKS5 `CONNECT` to that FQDN).
- Request format: `isConnect u8 | ATYP u8 | address variable | port u16be`. The request destination
  is an **ordinary SOCKS address**: ATYP is `0x01` IPv4 / `0x04` IPv6 / `0x03` FQDN, and the
  documentation says so in as many words ("Request destination, uses the SOCKS address format").
- `isConnect = 1` — connect mode: the stream is bound to the single request destination; stream
  format is `length u16be | data` per datagram.
- `isConnect = 0` — non-connect mode: same stream format as protocol v1, i.e. each datagram
  carries its own `ATYP | address | port | length | payload` header. **The `0x00` IPv4 / `0x01`
  IPv6 / `0x02` domain types belong to that per-datagram stream format**, not to the request: commit
  `0970bbc` added the table under *Protocol version 1 → Stream format*, where the documentation
  still has it, and left the version 2 request line untouched.

**[external] The request-header address types were corrected after this memo's first revision**,
which had applied the version 1 stream-format table to the version 2 request.
`research/r7-server-verification.md` §2 pins the request's real set against the pinned sing and
sing-box sources (`common/uot/protocol.go` reads the request destination with
`M.SocksaddrSerializer`; the `AddrParser` with `0x00`/`0x01`/`0x02` is used only on the
`!isConnect` branches) and names the commit whose table was misread. The bullets above state it.

**[analysis]** Per-datagram framing in connect mode is 2 bytes, against the SOCKS5 UDP header's
10 (IPv4) / 22 (IPv6) bytes plus the UDP relay socket's own encapsulation. The existing
receive-window constant (`maximumFrameSize + MaximumSocks5UdpHeaderSize + OversizeSentinelSize`,
`UdpProxyCoordinator.cs:209-215`) therefore covers UoT framing with headroom; §2.7 of the
readiness note is a non-issue for connect mode.

## 2. Server-side support surface

**[external]** sing-box `protocol/socks/inbound.go` (SagerNet/sing-box, rev 9da0746d): the socks
inbound constructor wraps its router unconditionally — `router: uot.NewRouter(router, logger)`.
No inbound option gates UoT; a socks inbound accepts UoT connections by construction.

**[external]** sing-box internals documentation
(<https://singbox-internals.hidandelion.com/transport/uot.html>): the UoT router intercepts
connections whose destination FQDN is the magic address, reads the request header, wraps the TCP
connection as an `N.PacketConn` (`uot.NewConn`), and routes it via `RoutePacketConnectionEx`.
Connect mode behaves as a connected UDP socket to a single destination; non-connect mode carries
per-packet destinations.

**[analysis]** Two consequences:

1. **Zero server-side change** for the recorded deployment: the operator's sing-box socks
   inbound already terminates UoT. The credential surface is the existing SOCKS5 username/password;
   no new config keys on the server.
2. The UoT inbound path enters the **same `routePacketConnection` machinery** whose
   last-peer-refreshed write path caused the SOCKS5 association-sharing misdelivery
   (`10-05-udp-association-sharing-correctness/prd.md` Background). For connect mode the packet
   connection is bound to one destination for its life, so the cross-flow class does not apply —
   but the reply write path must still be confirmed on the exact build (PRD R7), per the version
   pinning lesson recorded in that PRD's Notes (1.14.1 / sing v0.9.4 vs the testing channel
   sing v0.9.7-0.20260929150544).

## 3. The pipelining property (the reason this task exists)

**[recorded]** Today's per-flow establishment is serialized by a *data dependency*: the client
cannot send its first datagram until the `UDP ASSOCIATE` reply arrives, because the reply's
`BND.ADDR` is the relay endpoint every send targets
(`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs:219-226`
`UdpAssociateAsync` → `ReadEndpointReplyAsync`;
`src/WinForward.Runtime/Socks5/Socks5UdpAssociation.cs:64-75` — `RelayEndpoint` throws while
unnegotiated). Measured cost: connect 2.46 ms + ASSOCIATE total 4.49 ms ≈ 7 ms per flow
(`10-05-udp-association-sharing-correctness/prd.md` Background; priced per flow in
`benchmarks/results/2026-10-05-no-association-sharing/`).

**[analysis]** The UoT exchange carries the same message *count* (greeting, auth, CONNECT) but
the CONNECT reply contains nothing the client needs — datagrams ride the same ordered stream as
the handshake itself. The client may therefore write greeting + auth + CONNECT(magic) + UoT
request header + first datagram in one flight and learn of failure from the reply afterwards.
First-datagram send latency collapses from ≈7 ms to ≈ the TCP connect. This is protocol-sanctioned,
unlike guessing the SOCKS5 relay address ahead of the ASSOCIATE reply.

**[analysis]** What UoT does *not* change: one dial per flow remains one dial per flow. The burst
establishment wave shape (`ceil(N/8) × D`, 8-wide setup limiter;
`benchmarks/results/2026-10-05-no-association-sharing/README.md:117`) keeps its N and its limiter;
only D shrinks. Collapsing the wave itself remains the mux track's question
(`09-06-local-mux-transport`, unaffected by this task).

## 4. Rejected sibling shapes (recorded here so they stay rejected)

- **UoT non-connect mode as a sharing shape.** Per-packet addressing lets one connection carry
  many destinations, but two live flows to the *same* destination are indistinguishable on the
  wire — the structurally undetectable same-destination misdelivery recorded in
  `10-05-remove-udp-association-sharing/research/decision-record.md` §2, moved onto a stream.
  Out of scope (PRD).
- **Pooling / warm reuse of UoT connections across flows.** A late segment for a dead flow can
  arrive after the connection is reassigned — the stale-reply window of the rejected
  exclusive-lease pool (same decision record, §4). One connection per flow, dies with the flow.
- **VLESS + XUDP + mux.** The only shape that delivers true many-flows-over-one-connection with
  per-flow identity; stays with `09-06-local-mux-transport`'s research. External facts collected
  for it (sing-mux = smux/yamux/h2mux; XUDP = VLESS default packet encoding with per-session ids;
  Mux.Cool is built into the VLESS protocol and cannot be disabled, sing-box issue #2415) are
  deliberately *not* expanded here — they belong to that task's research.

## 5. Client-side implementation shape anchors

**[recorded]** Reusable machinery in this tree:

- `Socks5Messages.WriteRequest(Socks5Command, …)` already parameterizes the command
  (`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs:224`) — a `Connect` request to the
  magic address is a small variant, not a new handshake stack.
- `Socks5ControlConnection.AuthenticateAsync` (`:349-352`) is unchanged for UoT.
- The transport seam is transport-neutral (`UdpTransportContracts.cs:92-143`); a UoT transport is
  one more factory branch, and per-flow endpoints fall out of the TCP connection naturally —
  none of the shared-connection exceptions in `l2-readiness.md` §2.3/§2.5/§2.8 apply.
- Fault mapping: a stream read fault is flow-fatal and maps onto the existing
  `UdpAssociationLostException` / `UdpTeardownReason.AssociationLost` path (no setup cooldown,
  flow re-establishes on its next datagram; `.trellis/spec/backend/udp-relay.md` R5). The
  datagram-scoped `ConnectionReset` skip class does not exist on a stream
  (`l2-readiness.md` §2.6 answers itself for the per-flow shape).
