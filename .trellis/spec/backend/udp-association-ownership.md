# UDP Association Ownership

> One flow, one authenticated SOCKS5 UDP association: what the dial produced, who owns it, how the
> watchdog declares it dead, how disposal is ordered, and what exchange evidence the transport
> keeps. Read it before touching `Socks5UdpAssociation`, the relay-socket contract, or association
> death. Family hub: [udp-relay.md](./udp-relay.md).

---

## One flow, one association, one relay socket

Rewritten 2026-10-05 (task 10-05-remove-udp-association-sharing). The rule exists because the
alternative was provably unsound: at concurrency a shared association misdelivers replies (4–7 of 48
churn flows received another flow's reply; 3 of 48 in the burst scenario), and when two flows share a
destination the misdelivery is address-indistinguishable, so no detector could ever demote a server
on that evidence.

- **Nothing is pooled, leased, refcounted, or re-associated.** Every `CreateAsync(ProxyTarget, ct)`
  dials a fresh association for the one flow it serves; the factory owns no association between
  calls. It does own the `udp.association.lost` log throttle that every association it creates
  reports through — one line per window across all of them, because a server dropping a whole
  population at once is one incident.
- The local relay port still identifies the flow, so `RelayAlias` uniqueness, the per-flow
  self-traffic tuple, and reverse routing are unchanged. The relay-alias collision guard lives in
  [idle-expiry-sweep.md](./idle-expiry-sweep.md), which also states that association retention is
  the session's, not the sweeper's.
- **The descriptor floor is two per live flow** (the control connection and the relay socket), and
  the kernel receive-buffer estimate stays `live sessions × udpRelayReceiveBufferKb` — one relay
  socket per flow. The UoT carriage changes both; see [udp-over-tcp.md](./udp-over-tcp.md).
- **Removed with the sharing feature**: `UdpAssociationPool`, `UdpControlAssociation`,
  `UdpAssociationLease`, `UdpAssociationEvidence`, `UdpServerCapability` +
  `UdpAssociationCapabilitySampler`, `UdpAssociationReuseMode`, the in-place re-association, and the
  `udpAssociationReuse` / `udpAssociationMaxPerServer` / `udpAssociationFlowsPerAssociation` keys.
  `UdpAssociationTable` / `UdpAssociation` stay, and are now only the **alias registry**: the
  original key ↔ `(local relay endpoint, relay endpoint)` pair that makes a relay reply classifiable
  as the reverse of a stored flow, plus the per-flow `Generation` the log fields print (the flow's
  correlation id, not a re-association epoch).
- `UdpProxyCoordinator.ReceiveWindowRetireFloor = 64` is the one surviving number from the pooling
  configuration: the fixed floor of the receive-window pool's retire allowance,
  `max(ReceiveWindowRetireFloor, sessionCapacity / 16)`.

## The association owns the control connection, its relay publication, and the watchdog

- `Socks5UdpAssociation.ConnectAsync(Socks5Server, Socks5UdpAssociationContext, CancellationToken)`
  dials the authenticated `Socks5ControlConnection`, sends `UDP ASSOCIATE`, publishes the relay
  endpoint and its serialized form, and starts the control-stream watchdog. Its internal surface is
  `RelayEndpoint`, `RelaySocketAddress`, `RelayAddressFamily`, `Fault`
  (`UdpAssociationLostException?`), and `DisposeAsync`; the class, the context and `Server` are
  internal/private.
- **The relay endpoint is published once**, before the transport is handed the association, and is
  never replaced. `Socks5UdpTransport.PeerEndpoint` is therefore fixed for the transport's life, and
  the send path uses the `SocketAddress` the association serialized once at connect time — no
  per-send rebind, no reference compare.
- **The watchdog blocks on the control stream.** A server-side close (a 0-byte read), a stream
  fault, or an `IOException`/`SocketException` on that read is the association's death. RFC 1928
  defines no control-connection traffic after `UDP ASSOCIATE`, so any received byte is not death and
  the read continues; the codec's own cancellation and disposal are a normal exit.
- **Death is fail-closed and explicit.** The watchdog records one `UdpAssociationLostException`
  carrying the control-stream death as its inner exception, emits the rate-limited
  `udp.association.lost` warn (`proxy` / `relay` / `reason`), and closes the dead control
  connection. The transport refuses every later datagram with that stored exception **before its
  socket and before its send gate**, so a dead association can never write to a relay no one is
  watching. There is no in-place recovery and no address-family follow: the flow's next datagram
  establishes a new association.
- **Association loss is not a setup failure (I5).** `UdpProxyCoordinator.TeardownReasonFor` maps the
  exception to `UdpTeardownReason.AssociationLost`, counts `udpAssociationLost` once per send failure
  so classified, and arms **no** setup cooldown, so the flow re-establishes on its next datagram.
  The same mapping covers the setup-queue flush window, which rides the same send path. A failed
  dial or ASSOCIATE is the opposite case: `SetupFailure`, counted `udpSetupFailures`, 1 s cooldown
  armed — see [udp-flow-setup.md](./udp-flow-setup.md).
- `Socks5UdpTransport.Create(association, selfTraffic, socketFactory, disableUdpConnectionReset,
  maximumFrameSize, relayReceiveBufferBytes)` binds the relay socket in the association's relay
  family and returns a transport owning **both** the association and the socket.

## Disposal ordering

- Each association is an owner: one `QuiescenceScope` whose only child is the watchdog, plus an
  explicit one-shot teardown. `Socks5UdpTransport.DisposeAsync` releases, in order, the relay
  socket → its self-traffic token → the association, and continues into the later releases when an
  earlier one throws (one-shot flag first, so a repeat dispose returns). Sealing the association's
  scope **before** closing the control connection is what makes the watchdog's faulted read a
  teardown rather than a death.
- **The send gate is deliberately left undisposed**: disposing a `SemaphoreSlim` with waiters parked
  strands those waits forever, and a stranded sender would hold its caller's work lease. The guard
  refuses new senders, and a parked sender is released by the disposed socket's faulted send and
  refused by the post-wait re-check (see [udp-datagram-path.md](./udp-datagram-path.md), "The
  disposal guard is outside the warm shape").
- **Setup failure releases what it acquired.** A relay socket that cannot be created, an
  unappliable receive buffer, or a failed bind releases the association the factory just dialed
  (closing its control connection); a control dial or ASSOCIATE failure releases the half-built
  association. No path leaves a control connection behind.
- `DurableCaptureBundle` disposes sweeper → UDP coordinator → UDP native pools → TCP. The
  coordinator's drain disposes every session's transport, and with it every flow's own association,
  so nothing association-shaped is released separately.

## Exchange evidence stays on the transport

- `IUdpExchangeCounters` is implemented by `Socks5UdpTransport` over its own two fields: one
  `Interlocked` increment after the kernel accepted a send, and a write-once flag for the first
  successfully decoded relay response. Skipped relay datagrams (unexpected source, oversized,
  malformed, connection reset) record nothing. The one-shot retirement class and the sweep
  allocation gates are unchanged; a transport that does not implement the interface is classified
  *sustained*, which is the retention-safe direction.
- **The hot path is unchanged.** No association interaction per datagram beyond one volatile fault
  read for fail-closed: the only additions on the established path are the send counter, the
  write-once response flag, and the cached `SocketAddress` handed to `SendTo`.

| Condition | Required result |
|---|---|
| A flow sets up | one control connection, one `UDP ASSOCIATE`, one relay socket, one alias claim — all owned by the flow's transport |
| Two concurrent flows | two control connections, two ASSOCIATE replies, two relay sockets (the 1:1 shape) |
| Control stream ends (0-byte read) or faults | watchdog records `UdpAssociationLostException`, emits the rate-limited `udp.association.lost` warn, closes the control connection |
| Send after that death | transport throws the stored exception before its socket; `AssociationLost` slot removal, `udpAssociationLost`++, **no** setup cooldown |
| Flow's next datagram after the loss | a fresh setup: new dial, new ASSOCIATE, new relay socket, served normally |
| Failed dial/ASSOCIATE during setup | `SetupFailure`, `udpSetupFailures`++, 1 s setup cooldown armed |
| Relay socket create / apply / bind failure after a successful ASSOCIATE | the association is released (control connection closed) and the setup fails closed |
| Transport disposed | relay socket closed, self-traffic token released, association disposed (watchdog joined, control connection closed), once |
| Association death while the setup queue flushes | the same `AssociationLost` mapping as the ready path (no cooldown, not a setup failure) |

Tests: `Socks5UdpAssociationOwnershipTests` (two concurrent flows ⇒ two control connections, two
ASSOCIATE replies, two relay sockets; both halves released on each transport's dispose);
`UdpAssociationLossTests` (a control connection dying mid-flow fails the flow closed with the
`AssociationLost` removal, no cooldown and the warn, the next datagram re-establishes and is served,
no unobserved task fault, and the setup-flush window maps to the same reason);
`Socks5UdpTransportFactoryTests` (the association is released on every relay-socket
construction-failure path, dispose is idempotent, the configured relay receive buffer reaches a real
socket); `UdpReceiveResilienceTests` / `Socks5UdpConnresetTests` / `Socks5UdpTransportSendTests` /
`Socks5UdpAssociateTests` (loop-prevention tuple, pre-bind `SIO_UDP_CONNRESET`, allocation-free warm
send, cross-family ASSOCIATE, skip-class receive); `UdpProxyCompositionTests` (the
session-capacity/relay-buffer transposition guard at the composition seam); `SweepAllocationGateTests`
(the coordinator and alias-table sweep gates).
