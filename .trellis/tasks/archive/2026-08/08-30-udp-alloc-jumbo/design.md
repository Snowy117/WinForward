# Design: UDP allocation zero-out + jumbo buffer sizing

Task: `08-30-udp-alloc-jumbo`. Evidence: `research/current-state.md` (all
file:line references below verified at HEAD `c91ab68`).

## Shape of the change

Four independent seams, ordered so the tree compiles after each step:

```
Protocols (Socks5Udp.cs)          Runtime (Socks5/UdpProxy)              Cli (Program.cs)
  Socks5UdpDatagram.IPAddress?  →  IUdpProxyTransport.SendAsync(Endpoint)  Socks5UdpTransportFactory(
  TryReadAddress → IPAddressValue  Socks5UdpTransport: cap ctor param,         selfTraffic,
                                     cached sender template                     NdisApiAbi.MaximumEthernetFrame)
                                   UdpProxySession: pass-through + HasValue
```

## D1 — `IUdpProxyTransport.SendAsync(Endpoint destination, ...)` (forward)

**Signature**: `ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)`.

- `Endpoint` (Core, `Domain.cs:32`) is already the session's input type and
  carries `IPAddressValue Address` + `ushort Port`.
- Transport body: `Socks5UdpCodec.TryEncode(destination.Address, destination.Port, payload.Span, _sendBuffer, out var written)`
  — the `IPAddressValue` overload (`Socks5Udp.cs:16`) exists; both the sync
  entry and `SendAfterGateAsync` drop their `IPAddressValue.From(...)` call.
- `UdpProxySession.SendAsync` (`UdpProxySession.cs:115`) forwards `destination`
  unchanged — the `new IPEndPoint(ToIPAddress(), port)` line and its 3
  allocations disappear.
- Rationale for `Endpoint` over `(IPAddressValue, ushort)`: the sole production
  caller already holds an `Endpoint`; one struct parameter keeps the interface
  stable for any future endpoint-scoped data.
- Migration set (mechanical): fakes in `UdpTransportFakes.cs`,
  `UdpReceiveResilienceTests.cs`, `IdleExpirySweeperFailureTests.cs`,
  `BenchmarkShared.cs`; direct call `Socks5UdpAssociateTests.cs:229` becomes
  `SendAsync(Endpoint.From(IPAddress.Loopback, 53), ...)`.
- The `IPAddress`-taking `TryEncode`/`Encode` codec overloads stay (tests and
  the loopback server still use them); only the transport's internal call
  changes.

## D2 — `Socks5UdpDatagram` decodes to `IPAddressValue?` (reverse)

**Type**: `public readonly record struct Socks5UdpDatagram(IPAddressValue? DestinationAddress, string? DestinationDomain, ushort DestinationPort, ReadOnlyMemory<byte> Payload);`

- `TryReadAddress` (`Socks5Udp.cs:104-116`) writes `IPAddressValue` via
  `FromIPv4(bytes)` / `FromIPv6(bytes, checked((uint)scopeId))` — the
  `ArgumentException` catch disappears (the value-type constructors only throw
  on the IPv4 upper-bits invariant, which a 4-byte `FromIPv4` cannot violate;
  length checks already gate the slices). Keep the `type`/`length` cross-check
  return value semantics.
- Scope propagation: `TryDecode`'s `long scopeId` parameter stays; IPv6 with
  non-zero scope produces `ScopeId != 0` exactly as `new IPAddress(bytes, scopeId)`
  did (`UdpPacketParsingTests.cs:69-70` migrates to `.ScopeId`).
- Consumer `UdpProxySession.cs:193,200`:
  `if (response.DestinationAddress is not { } address) …skip-domain…` else
  `Endpoint.From(address, response.DestinationPort)`.
- Loopback server (`LoopbackSocks5UdpServer.cs:124,278,296-297`): store
  `IPAddressValue?`, echo via the `IPAddressValue` encode path.
- Test assertions compare against `(IPAddressValue)IPAddress.Parse(...)` or
  `IPAddressValue.FromIPv4/6` literals; the existing implicit conversion from
  `IPAddress` (`IPAddressValue.cs:64`) does not apply to generic
  `Assert.Equal<T>` — assertions are rewritten explicitly.
- Alignment argument: `UdpPacketView` and `IPTcpUdpPacket` already expose
  `IPAddressValue`; SOCKS5 datagrams joining them removes the last framework
  address on any parsing product type.

## D3 — Cached receive sender template

- `private readonly IPEndPoint _receiveSenderTemplate` computed in the private
  constructor from `relayEndpoint.AddressFamily`
  (`InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0)`).
- `ReceiveAsync` (`Socks5UdpTransport.cs:271`) uses the field. `ReceiveFromAsync`
  does not mutate the passed endpoint (it reports the actual remote in
  `SocketReceiveFromResult.RemoteEndPoint`), so sharing one instance across
  calls is safe; the field is readonly and never handed out.

## D4 — Frame-cap-derived send buffer

- `Socks5UdpTransportFactory(SelfTrafficRegistry selfTraffic, int maximumFrameSize)`
  — required parameter (one production composition site; the benchmarks'
  parameterless convenience stays via a `= UdpFrameBuilder.DefaultMaximumEthernetFrame`
  default on the internal seam only, so existing benchmark/test construction
  compiles while `Program.cs` passes the ABI constant explicitly).
- Plumb: factory `CreateAsync` → internal `Socks5UdpTransport.CreateAsync(...,
  int maximumFrameSize = UdpFrameBuilder.DefaultMaximumEthernetFrame)` →
  private ctor → `_sendBuffer = new byte[6 + 16 + _maximumFrameSize]`
  (guard `> 0`, consistent with the coordinator's `UdpProxyCoordinator.cs:87`).
- Sizing proof: capture bounds the payload at `cap − 42` (eth 14 + IPv4 20 +
  UDP 8), so `6 + 16 + cap` always covers `6 + addrLen + payload`; the IPv6
  worst case (`6 + 16 + cap − 42 < 6 + 16 + cap`) holds with the same margin
  the receive buffer uses.
- `Program.cs` `CreateUdpCoordinator` (`Program.cs:345-355`): hoist
  `var maximumFrameSize = NdisApiAbi.MaximumEthernetFrame;` and pass it to all
  three components — the composition comment states the single-source-of-truth
  rule (send buffer, receive buffer, reinjector cap, and the native ABI must
  agree; only the ABI constant should ever change).
- Alternatives considered: hard-wiring `6 + 16 + 65535` (research option) —
  rejected because it re-introduces a second, unrelated sizing policy and
  64 KiB per session of committed-on-demand heap vs. the cap-derived 1544 B
  (or 9036 B on jumbo) that every sibling buffer already uses.

## D5 — Oversized-response boundary documentation

- XML-doc note on `ReceiveAsync` / `IsPossiblyTruncated`: the receive buffer is
  `cap + 22 + 1`, so the deliverable payload ceiling is `cap − 42` (1472 B at
  the pinned 1514 ABI); larger relay responses skip as `Oversized` and surface
  in the 5s summary. A jumbo-capable ABI lifts the ceiling end-to-end because
  D4 makes the send buffer follow the same cap.
- `udp-relay.md` gains the boundary statement (see implement.md step 7). No
  behavior change.

## Tradeoffs / risks

| Risk | Mitigation |
|---|---|
| Public-type breaking change (`Socks5UdpDatagram`, `IUdpProxyTransport`) | Single-repo app, no external consumers; full-suite migration in one task; call-site inventory in current-state.md |
| `Assert.Equal` type mismatches across ~12 test sites | Mechanical `(IPAddressValue)` rewrites listed in current-state.md; compiler finds every site |
| Hidden dependence on `IPAddress` reference identity/equality in tests | `IPAddressValue` equality is value-based (bits+family+scope); tests compare literals, not identities |
| Send-buffer resize changes `_sendGate` contract | Buffer stays a single per-transport field created once in the ctor; the gate/comment block (`Socks5UdpTransport.cs:192-198`) is untouched |
| jumbo encode test needs a >1508B payload without a real jumbo network | Use the internal `CreateAsync` seam with a fake `createControl` + loopback relay (existing `Socks5UdpAssociateTests` pattern) and a large in-memory payload; asserts encode success, not network delivery |

## Compatibility / rollback

No wire-format, config-schema, or native-ABI change. All edits are reversible
in one `git revert`; no data migrations. The plan lands as a single commit on
`master` after the check pass, mirroring prior children.
