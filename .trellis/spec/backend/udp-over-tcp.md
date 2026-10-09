# UDP over TCP

> The opt-in UoT v2 connect-mode carriage: one flow, one stream connection bound to one destination.
> Read it before touching `Socks5UotTransport`, `UotCodec`, the `UdpOverTcp` branch of
> `Socks5UdpTransportFactory.CreateAsync`, or `Socks5Messages`' domain `WriteRequest` overload.
> Family hub: [udp-relay.md](./udp-relay.md).

---

## Scope

Wired 2026-10-06 (task 10-06-uot-per-flow-transport). UoT is a **mode of the `socks5` target kind,
not a third kind**: one `udpOverTcp` field on a `Socks5Server`, off unless set, and the native relay
stays the default. `UdpTransportFactory`, `UdpProxySession`, the coordinator's send and admission
path, the response reinjector, the alias table and the two-class retention are all untouched;
`Socks5UdpTransportFactory.CreateAsync` branches on `server.UdpOverTcp` and every other server keeps
the association + relay-socket path of
[udp-association-ownership.md](./udp-association-ownership.md).

It is the extension of that ownership rule from one flow per association to **one flow per
connection**: a flow's datagrams ride a TCP stream bound to the flow's single destination, so its
descriptor floor drops from two to one, the setup round trip the native path serializes behind
`UDP ASSOCIATE` leaves the first datagram's critical path, and the reply-source question disappears
with the wire field that carried it.

## Signatures

- `Socks5UotTransport : IUdpProxyTransport, IUdpExchangeCounters`
  (`src/WinForward.Runtime/Socks5/Socks5UotTransport.cs`). `PeerEndpoint` is the SOCKS5 server
  endpoint and `LocalEndpoint` the connection's local socket endpoint; both are per-flow because the
  connection is, so `UdpSessionSetup`'s alias claim (`UdpSessionSetup.cs:104-118`) is unchanged and
  unique by construction.
- `Socks5UotTransport.DialAsync(server, context, ct)` dials
  `Socks5ControlConnection.ConnectDeferredHandshakeAsync` — the same attempt loop, address cache and
  before-the-SYN self-traffic registration as the native association, but the dial writes the
  greeting (and the RFC 1929 message when credentials are configured) and returns **without reading
  a reply**. `Create(control, maximumFrameSize)` takes the stream and socket, sets
  `socket.Blocking = false` for the warm send path, and sizes its send buffer
  `30 + UotCodec.MaximumRequestHeaderLength + UotCodec.FrameHeaderSize + maximumFrameSize` =
  `52 + maximumFrameSize`: the 30-byte magic `CONNECT`, the longest UoT request header (IPv6,
  20 bytes) and the 2-byte frame prefix, then the payload.
- `UotCodec` (`src/WinForward.Protocols/UotCodec.cs`): `MagicAddress = "sp.v2.udp-over-tcp.arpa"`,
  `Version = 2`, `RequestHeaderLength(AddressFamilyKind)`, `TryWriteRequestHeader(bool isConnect,
  IPAddressValue, ushort, Span<byte>, out int)`, `FrameHeaderSize = 2`, `TryWriteFrameHeader`. The
  request destination carries the **shared SOCKS address types**
  (`Socks5Messages.AddressTypeIPv4` / `AddressTypeIPv6`, with `Socks5Messages.AddressFieldLength` the
  one decode mapping the fixtures share); `isConnect: false` is refused, because the per-datagram
  `0x00`/`0x01`/`0x02` form of protocol version 1 is not implemented by this codec.
- `UdpTransportHandshakeRejectedException : IOException` beside `UdpAssociationLostException`
  (`UdpProxy/UdpTransportContracts.cs`). `UdpProxyCoordinator.TeardownReasonFor`
  (`UdpProxyCoordinator.Send.cs:215-221`) classifies both, and
  `RemoveReceiveFailedSessionCoreAsync` (`UdpProxyCoordinator.cs:568-583`) classifies the session's
  recorded fault through the same helper instead of hard-coding `Fault`.
- `UdpSessionSetup.UdpTransportOf(ProxyTarget)` (`UdpProxy/UdpSessionSetup.cs:186-190`) — a private
  static helper, not a logging type: `"uot"` for a `Socks5Server` with `UdpOverTcp`, `"native"` for
  any other SOCKS5 server, null for a local target. There is no `UdpProxyLogging` type.

## Contracts

- **One flow, one connection, owned and disposed with it (R1).** The transport owns the control
  connection for the flow's whole life; nothing is pooled, leased or reused across flows, and
  `DisposeAsync` releases the connection (which owns the socket, the stream and the self-traffic
  tuple) exactly once through every path. The send gate is deliberately left undisposed so a parked
  sender is never stranded — the same rule and rationale as the native transport
  ([udp-datagram-path.md](./udp-datagram-path.md), "The disposal guard is outside the warm shape"). A
  connection whose construction failed is released by the
  factory's catch, mirroring the native association path.
- **Register the self-traffic tuple before the SYN**, through the dial's `onSocketReady` callback
  exactly as the native association does; registering it after the connection exists lets a
  catch-all proxy rule re-capture WinForward's own connection
  ([traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md)).
- **Pipelined establishment (R2).** The dial writes the greeting `[+ credential message]` and never
  reads; the first send writes one buffer — the domain-typed `CONNECT` to `UotCodec.MagicAddress`,
  the UoT request header with `isConnect = 1`, and the first `u16be length | payload` frame — and
  does not await the `CONNECT` reply. Reply validation moves to the receive path, whose first call
  consumes, in the order the server wrote them, the method selection, `[+ the credential reply]` and
  the `CONNECT` reply status before the first frame. No handshake reply is on the first datagram's
  critical path.
- **Framing (R3).** Datagrams are `u16be length | payload` on the stream. The receive loop reads the
  2-byte prefix and the payload separately (`ReadExactlyAsync`), so a frame split across segments is
  reassembled rather than dropped; a zero-length frame is a legal empty datagram; a frame longer than
  the caller's buffer is **consumed** to keep the stream aligned and reported
  `UdpTransportSkipReason.Oversized`. One writer gate per transport serializes frames — two
  concurrent sends must never interleave the 2-byte prefixes — and the payload is encoded into the
  transport's reusable send buffer before any await, so a capture buffer's span lifetime stays legal
  (only the contended-gate path copies).
- **Fail-closed guards.** A send whose destination differs from the one the first send captured
  (`_destination`), a payload above `ushort.MaxValue`, and a payload above the transport's frame
  ceiling all throw rather than mis-frame the stream. Connect mode binds the stream to one
  destination for its life; silently framing a second destination is the one unacceptable outcome.
- **Typed faults, and never a raw stream fault (R4).**

  | Observed | Recorded and thrown | Classified as |
  |---|---|---|
  | A non-success `CONNECT` reply, EOF before it, or the setup window (30 s) elapsing | `UdpTransportHandshakeRejectedException` | `UdpTeardownReason.SetupFailure` — the setup cooldown is armed (1 s), exactly as a refused `UDP ASSOCIATE` |
  | EOF / RST / any stream fault after establishment | `UdpAssociationLostException` | `UdpTeardownReason.AssociationLost` — counted `udpAssociationLost`, **no** cooldown, the flow re-establishes on its next datagram |

  The transport records the first fault and rethrows the same instance from every later send and
  receive, so a dead stream refuses datagrams before the socket. **A stream fault must never surface
  as the datagram-level `ConnectionReset` skip**: `UdpProxySession`'s receive loop treats that code
  as a one-datagram anomaly and continues (`UdpProxySession.cs:353-359`) — correct for an ICMP
  port-unreachable answering a UDP send, an infinite spin on a dead stream. Every `IOException` /
  `SocketException` observed on a live, non-cancelled transport is translated into one of the two
  types above before it leaves. The `CONNECT` reply read is additionally bounded by the transport's
  own setup window (`s_connectReplyTimeout`, 30 s), so a server that accepts the exchange and never
  answers fails the flow's setup instead of parking its receive loop until idle expiry.
- **The descriptor budget is one per live flow.** The stream connection replaces both native
  descriptors (the `UDP ASSOCIATE` control connection **and** the relay socket), so the floor is one
  local descriptor per live UoT flow against two per live native flow. The kernel receive-buffer
  estimate changes with it: `udpRelayReceiveBufferKb` is applied to a relay socket, and a UoT flow
  has none ([udp-session-lifecycle.md](./udp-session-lifecycle.md)).
- **Reply source is synthesized, and a foreign source is structurally impossible.** Connect mode
  carries no on-wire source, so `ReceiveAsync` declares the flow's captured destination as every
  reply's source — and therefore as the reinjected frame's source. The `udpResponseSourceMismatch`
  counter and its `udp.response.foreign_source` warn cannot fire on this path: the observation is
  impossible by structure, not suppressed by policy. A frame that no send ever framed (no captured
  destination) is `UdpTransportSkipReason.UnexpectedSource` instead of a default source the session
  would count as foreign.
- **Retention is the transport's own evidence (I3/R5).** `IUdpExchangeCounters` is implemented from
  the transport's own observations: one increment after the kernel accepted a whole frame, and a
  write-once flag on the first successfully decoded frame (a skip records nothing), so the completed
  one-shot 5 s retirement class is preserved on this path exactly as it is for the native transport.
- **Observability.** `targetKind` stays `local|socks5` — UoT is a mode, not a third kind — and the
  `udp.session.created` event gains `udpTransport=uot|native` beside it, so a column or a product
  event can attribute the carriage without a new event name and without changing `targetKind`.

## Carried residuals, documented rather than fixed

1. **The magic `CONNECT` request is 30 bytes**: `4 (VER 5 | CMD 1 | RSV 0 | ATYP 3) + 1 (length) +
   23 (the FQDN `sp.v2.udp-over-tcp.arpa`) + 2 (port)`. The send buffer follows from that
   (`52 + maximumFrameSize`), so the prefix length is not a round number to "tidy".
2. **A rejection discovered on the receive path arms the cooldown without incrementing
   `udpSetupFailures` and without emitting `udp.setup.failed`.** Those two live in the setup
   pipeline's failure sink (`UdpSessionSetup.HandleSetupFailureAsync`, `UdpSessionSetup.cs:156`),
   which the receive path never calls: `RemoveReceiveFailedSessionCoreAsync`
   (`UdpProxyCoordinator.cs:568-583`) classifies the fault and removes the slot, and only
   `RemoveSlotAsync`'s `SetupFailure` branch arms the cooldown (`UdpProxyCoordinator.cs:518-521`).
   The flow's next datagram still traces `udp.setup.cooldown` (`UdpProxyCoordinator.Send.cs:65-67`)
   and re-establishes after 1 s. **Consequence for diagnosis: a systematically rejecting UoT server
   shows as cooldown churn on the trace, not as `udpSetupFailures` growth or `udp.setup.failed`
   events — read the cooldown trace, not the counter.** The setup/flush window is the one path that
   does count it (the same send path, reached through the setup pipeline).

**Interop status.** The wire facts here are source-pinned against sing-box `testing` @`2ff3985c`
(task 10-06's `research/r7-server-verification.md`), not verified interop: both loopback fixtures
decode the encoding with the server's own mapping (refusing an undefined family as
`unknown address family: <byte>`), but the runtime on-box probe has not run and the fixture is not
the pinned server.

**Measured evidence.** `benchmarks/results/2026-10-06-uot-per-flow/` carries the mode's column
beside the native per-flow column on one binary, one target field apart: descriptors per session,
burst-establishment percentiles, zero misdelivery and zero loss in both columns with exactly one
connection per flow. That directory also states the limits of its own evidence (loopback RTT and
zero loss make the TCP-carriage cost invisible), which is why the native relay stays the
recommendation on lossy legs and the mode remains off by default whichever way the numbers read.

## Tests

- `Socks5UotTransportTests`: the frame-before-`CONNECT`-reply observation (a scripted server that
  reads the datagram before writing the reply), echo round trip, split-frame reassembly, oversized
  frame consumed and reported, zero-length frame, destination mismatch and oversized payload both
  failing closed, concurrent sends never interleaving, zero managed allocations on the warm send,
  dispose releasing the connection exactly once, and the self-traffic tuple registered before the
  SYN.
- `UdpUotFlowLifecycleTests`: `CONNECT` refusal → `SetupFailure` with the cooldown armed; mid-flow
  connection death → `AssociationLost` with no cooldown and a successful re-establishment on the
  next datagram; the flow's own `IUdpExchangeCounters` putting a completed one-shot in the 5 s
  class.
- `UdpProxyLoggingTests`: `udpTransport` is `uot` for a `UdpOverTcp` server, `native` for every other
  SOCKS5 server, null elsewhere, with `targetKind` unchanged beside it.
- The native suites (`Socks5UdpAssociationOwnershipTests`, `UdpAssociationLossTests`,
  `UdpReceiveResilienceTests`, `Socks5UdpConnresetTests`, the retention and sweep gates) stay green
  unchanged — the mode's branch must not move the native path's behaviour.
