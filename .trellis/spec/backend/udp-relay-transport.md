# UDP Relay Transport

> The SOCKS5 UDP relay transport itself: the transport seam, ASSOCIATE across address families,
> relay socket setup, and the endpoint/buffer sizing that keeps the send path allocation-free. Read
> it before changing `IUdpProxyTransport`, the send/decode shape, or the buffers derived from the
> frame cap. Family hub: [udp-relay.md](./udp-relay.md).

---

## The transport seam

- `IUdpProxyTransport`, `IUdpProxyTransportFactory`, `IUdpExchangeCounters`,
  `UdpAssociationLostException`, and the neutral receive vocabulary `UdpTransportReceiveResult` /
  `UdpTransportSkipReason` / `UdpTransportDatagram` live in
  `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`, beside the coordinator and session
  that own them. One transport instance per flow, owned by `UdpProxySession`.
- Two implementations sit behind the seam: `Socks5UdpTransport` (the relayed path, which maps its
  decoded wire datagram into `UdpTransportDatagram` field for field — a readonly-record-struct
  copy, no allocation on the receive path) and `LocalUdpTransport` (`UdpProxy/LocalUdpTransport.cs`:
  one socket per flow pointed at a `localTargets` endpoint, payload forwarded verbatim, replies
  accepted only from that endpoint and declared to have come from the flow's own destination).
- `IUdpProxyTransportFactory.CreateAsync` takes the resolved `Configuration.ProxyTarget` — the
  SOCKS5-server / local-endpoint union. The composite `UdpTransportFactory` dispatches by kind and
  **refuses** a target that carries neither, so a further implementation slots in behind the same
  decision point without touching the coordinator, the session, or the reinjector. The dependency
  for the contract therefore runs Socks5→UdpProxy, and the two concrete factories are chosen by the
  composition root (`src/WinForward.Cli/UdpProxyComposer.cs`). The datagram wire codec itself
  (`Socks5UdpDatagram`, `Socks5UdpCodec`) stays in `src/WinForward.Protocols/Socks5Udp.cs`.

## Cross-family ASSOCIATE (fixed 2026-08-11)

- `Socks5ControlConnection.UdpAssociateAsync(CancellationToken)` sends the UDP ASSOCIATE request
  with **no caller-provided local endpoint**: `0.0.0.0:0` for an IPv4 control socket, `[::]:0` for
  an IPv6 one, after control authentication and before UDP socket allocation. It returns the
  relay's `IPEndPoint`.
- The returned BND/relay endpoint is **authoritative for the UDP socket's address family**.
  Allocate and bind the UDP socket in that family, and build the relay alias only after the local
  and relay endpoints are same-family — the alias pairs the *transport's* local endpoint with the
  relay endpoint, never the flow's endpoints, so a relay whose family differs from the original
  flow's cannot make `FlowKey.Create`'s same-family invariant throw (`ArgumentException`,
  `src/WinForward.Core/Domain.cs:116`).
- `Socks5UdpCodec.TryEncode` preserves the original destination's IPv4 / domain / IPv6 ATYP
  independently of the relay socket family, so a valid IPv4 relay can carry an IPv6 destination
  ATYP. `TryEncode` is the only encode seam — the `IPAddress`-taking `Encode` overloads were removed
  (task 09-19-compat-api-cleanup); loopback callers materialize a `byte[]` themselves.
- Register the `(Udp, local UDP endpoint, relay endpoint)` tuple in `SelfTrafficRegistry` before the
  first relay datagram. The TCP control tuple remains registered before its SYN through the control
  connection's `onSocketReady` contract (see
  [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md)).
- Proxy setup stays fail-closed: the socket, the UDP self-traffic token, and the flow's association
  are each released when owned, and disposal continues through later resources if an earlier
  disposal throws.
- **The relay socket disables `SIO_UDP_CONNRESET` before bind** through the vendor IOCTL
  `0x9800000C` with a 4-byte `FALSE` (`Socks5UdpTransport`'s disable action): while the
  Windows-default TRUE stands, an ICMP port-unreachable answering one of the socket's sends surfaces
  as `SocketError.ConnectionReset` on the next receive, and the session was torn down and
  re-ASSOCIATEed per cycle under a noisy path. The default implementation is guarded by
  `OperatingSystem.IsWindows()` because Linux rejects vendor IOCTLs, and an internal seam lets tests
  assert the call on any OS. The receive-side half of the same posture is the `ConnectionReset` skip
  class (see the hub's "Skip classes").

| Condition | Required result |
|---|---|
| IPv4 control + IPv4 relay | bind an IPv4 UDP socket, send IPv4 ATYP as requested |
| IPv6 control + IPv6 relay | bind an IPv6 UDP socket, send IPv6 ATYP as requested |
| IPv4 relay + IPv6 destination | bind an IPv4 UDP socket; send the IPv6 ATYP unchanged |
| IPv6 relay + IPv4 destination | bind an IPv6 UDP socket; send the IPv4 ATYP unchanged |
| Relay family differs from the original flow's family | alias uses the transport's own same-family endpoints; the flow key is untouched |
| Control / associate / socket / registration failure | release what was acquired and block the proxy-selected flow |

Tests: exact all-zero IPv4 and IPv6 ASSOCIATE request bytes; a loopback IPv4 relay receiving an IPv6
destination ATYP/address/payload with self-traffic ownership existing before send and removed after
disposal; an injected socket-disposal exception still releasing the UDP token and control
connection; the matching-family integration suites staying green.

## Zero-allocation endpoints and frame-cap buffers

Wired 2026-08-30. Before the fix a forwarded datagram cost three heap allocations on the send path
(`IPEndPoint` round-trips), a relay response cost two more, a receive one to two, and the transport
send buffer was hard-wired to the Protocols constant instead of the frame cap the coordinator and
reinjector already honor — on a jumbo-capable ABI every payload over 1508 failed `TryEncode`,
surfaced as an `IOException`, and re-ran a full SOCKS5 handshake per datagram.

- `IUdpProxyTransport.SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload,
  CancellationToken)` takes the Core `Endpoint` struct, not `IPEndPoint`, and is span-only (the
  memory overload was removed by task 09-19-compat-api-cleanup).
- `Socks5UdpDatagram(IPAddressValue? DestinationAddress, string? DestinationDomain,
  ushort DestinationPort, ReadOnlyMemory<byte> Payload)` — a null address (nullable struct) marks a
  domain-typed datagram.
- **Forward leg passes `Endpoint` straight through** (`UdpProxySession.SendSpanAsync` → transport):
  no `ToIPAddress()` / `new IPEndPoint` materialization on the send path. The transport encodes via
  `Socks5UdpCodec.TryEncode(IPAddressValue, …)` and the actual `SendTo` target is one `SocketAddress`
  the association serialized once at connect time and the transport cached at construction.
- **Decode (`Socks5UdpCodec.TryDecode`) produces `IPAddressValue?` directly** (`IPAddressValue.FromIPv4` /
  `FromIPv6(bytes, scopeId)`): the caller-supplied relay scope lands in `IPAddressValue.ScopeId`
  exactly as it previously landed in `IPAddress.ScopeId`, and the session's reverse leg builds
  `Endpoint.From(address, port)` with zero framework-address round-trips. A negative `scopeId` into
  the decode is an `OverflowException` from the `checked` cast, unreachable because the scope comes
  from `IPAddress.ScopeId` and is always ≥ 0.
- **Test-authoring note for that migration**: xUnit `Assert.Equal` generic inference does not apply
  the `IPAddress` → `IPAddressValue` implicit conversion, so migrated assertions cast explicitly
  (`(IPAddressValue?)expected`); `Assert.Equal(IPAddress, IPAddressValue)` still compiles elsewhere
  (for example the `UdpPacketSpanView` assertions in `UdpPacketParsingTests`) through inference
  participation — those sites are not unmigrated ones.
- **One sender template per transport**: `ReceiveFromAsync` does not mutate the passed endpoint (the
  observed remote arrives in `SocketReceiveFromResult.RemoteEndPoint`), so a readonly
  ctor-computed template is shared across receives.
- **The send buffer derives from the same frame cap as every sibling**: `new byte[6 + 16 +
  maximumFrameSize]` (guard `> 0`), mirroring the coordinator's receive sizing (`cap + 22 + 1`) and
  the reinjector's frame bound (`cap`). Capture bounds payloads at `cap − 42`, so the send buffer
  always encodes anything the pipeline can capture; the fail-closed `IOException` for a genuinely
  larger datagram stays as the assumption guard.
- **Composition has one source of truth**: `DurableCaptureBundle` hoists
  `const int maximumFrameSize = NdisApiAbi.MaximumEthernetFrame;`
  (`src/WinForward.Cli/DurableCaptureBundle.cs`) and fans it out to `Socks5UdpTransportFactory`,
  `UdpResponseReinjector`, and `UdpProxyCoordinator`; the composer consumes
  `composition.MaximumFrameSize`. Send buffer, receive buffer, reinjector cap, and the native ABI
  must agree — only the ABI constant should ever change.
- Factory shape: `Socks5UdpTransportFactory(SelfTrafficRegistry selfTraffic, int maximumFrameSize,
  int relayReceiveBufferBytes = Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize,
  Socks5AddressCache? addressCache = null, ILogger? logger = null, Func<Socks5Server,
  CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null,
  Func<AddressFamily, Socket>? socketFactory = null, Action<Socket>? disableUdpConnectionReset =
  null)` — internal, one factory per composition, dialing one association per flow and owning none
  itself. The frame cap stays required; the internal `Socks5UdpTransport.Create(association, …)`
  seam mirrors the cap and buffer with defaults (`UdpFrameBuilder.DefaultMaximumEthernetFrame`,
  `DefaultRelaySocketReceiveBufferSize`). The relay receive-buffer policy is owned by
  [udp-session-lifecycle.md](./udp-session-lifecycle.md).

Tests: `SendSpanAsyncForwardsTheSessionEndpointToTheTransportUnchanged` (endpoint fidelity through
the session); `SocksUdpIPv4RoundTripDecodesToTheRawAddressValue` and
`Socks5UdpDecodeCarriesRelayScopeForIPv6Address` (the raw value and its propagated scope);
`JumboCapSendBufferEncodesPayloadsBeyondTheDefaultCap` (9014 cap, 2000 B payload, end to end through
a loopback relay) and `DefaultCapSendBufferFailsClosedOnOversizedPayloads`; the skip-class,
connection-reset and reinjection suites stay green.
