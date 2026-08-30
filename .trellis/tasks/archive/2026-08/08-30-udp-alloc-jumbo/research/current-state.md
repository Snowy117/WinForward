# Current State: X6 + R5 re-validated at HEAD (2026-08-30)

Baseline of the original research: `e5667af`. Three children landed since
(fast-hardening, hot-path-revival, atomic-retire); this file re-validates the
udp-alloc-jumbo findings against HEAD `c91ab68`, 459/459 tests.

## Verdict summary

| Finding | Research claim | Verdict at HEAD | Notes |
|---|---|---|---|
| P1 (X6 fwd) | `UdpProxySession.cs:115` builds `IPEndPoint` per datagram | **CONFIRMED** | `UdpProxySession.cs:115` `new IPEndPoint(destination.Address.ToIPAddress(), destination.Port)` = 3 allocs (byte[] + IPAddress + IPEndPoint); `ToIPAddress` at `IPAddressValue.cs:97-105` |
| P1 (X6 round-trip) | `Socks5UdpTransport.cs:210` converts back | **CONFIRMED** | `Socks5UdpTransport.cs:210,242` `IPAddressValue.From(destination.Address)`; actual `SendTo` target is the fixed `RelayEndpoint` (`:217,247,259`); `From(IPAddress)` itself is stack-only (`IPAddressValue.cs:52-60`) |
| P2 (X6 rev) | `Socks5Udp.cs:108` `new IPAddress(bytes)` | **CONFIRMED** | `TryReadAddress` (`Socks5Udp.cs:104-116`) = 2 allocs/response; sole production consumer `UdpProxySession.cs:200` immediately converts to `Endpoint.From(IPAddress, port)` and discards it; null check at `:193` |
| P6 (X6 template) | `Socks5UdpTransport.cs:271` per-receive sender | **CONFIRMED** | `new IPEndPoint(IPAddress.Any/IPv6Any, 0)` per `ReceiveAsync` call, family fixed by `RelayEndpoint` |
| P3 (R5 buffer) | `_sendBuffer` hard-coded 1536 | **CONFIRMED, REFINED** | Now `Socks5UdpTransport.cs:267` `new byte[6 + 16 + UdpFrameBuilder.MaximumEthernetFrame]` (constant 1514, `UdpFrameBuilder.cs:22`) — no longer a literal, but still does not track the frame cap the rest of the pipeline uses |
| R5 cap plumbing | coordinator/reinjector accept `maximumFrameSize` | **CONFIRMED** | Coordinator receive buffer `= maximumFrameSize + 22 + 1` (`UdpProxyCoordinator.cs:100`, default `:67/:79`); reinjector cap `UdpResponseReinjector.cs:69,79,106`; production passes `NdisApiAbi.MaximumEthernetFrame` (1514) to both (`Program.cs:352,355`) |
| S3 (handshake storm) | encode failure → catch-all teardown | **CONFIRMED** | `SendOnReadySessionAsync` catch-all `RemoveSlotAsync(writeTombstone: false)` at `UdpProxyCoordinator.cs:201-205`; the IOException from `TryEncode` failure (`Socks5UdpTransport.cs:212,244`) lands there → teardown → next datagram re-runs full SOCKS5 handshake |
| S4 (oversize blackhole) | truncation skip near 1500B | **CONFIRMED, REFINED** | `IsPossiblyTruncated` (`Socks5UdpTransport.cs:301`) vs receive buffer `cap+22+1`; at the pinned 1514 ABI the deliverable payload ceiling is exactly 1472B (= 1514 − 14 − 20 − 8), so standard-MTU QUIC/WireGuard 1472B packets pass and only genuinely oversized ones drop (counted in the 5s skip summary `oversized=`). The drop policy follows the cap consistently; what is missing is documentation, not a new mechanism |

## Key facts about the frame-cap seam (refinement over research)

- The native side pins the frame cap: `NdisApiAbi.MaximumEthernetFrame = 1514`
  (`NdisApiAbi.cs:16`) with `fixed byte Buffer[1514]` (`:93`); capture frames
  and reinjection frames are both bounded by it.
- Capture-side payloads therefore never exceed `cap − 42` (eth+IPv4+UDP) — the
  send buffer sized `6 + 16 + cap` can always encode anything the pipeline can
  capture. A datagram larger than that cannot exist in this process at the
  pinned ABI.
- The `maximumFrameSize` doc comment on the reinjector already anticipates a
  jumbo-capable ABI (`UdpResponseReinjector.cs:59-60`: "default 1514, or 9014
  for a jumbo-capable ABI"). If the ABI constant ever rises, coordinator
  receive buffers and reinjector caps follow automatically; the transport send
  buffer is the single buffer that would NOT follow today — that is the R5 bug.
- Consequence: fixing R5 = deriving the send buffer from the same factory-fed
  cap (or hard-wiring 65535). Deriving from the cap keeps one source of truth
  and matches how the receive buffer and reinjector are already sized.

## Call-site inventory for the signature/type changes

`IUdpProxyTransport.SendAsync(IPEndPoint destination, ...)`:

- Interface: `Socks5UdpTransport.cs:59`; production impl `:190` (+ private
  `SendAfterGateAsync :237`); sole production caller `UdpProxySession.cs:115`.
- Fakes/implementations to migrate: `tests/WinForward.Core.Tests/TestHelpers/UdpTransportFakes.cs`
  (`FakeTransport:62`, `ImmediateFaultTransport:125`),
  `UdpReceiveResilienceTests.cs` (`ConnectionResetOnceTransport:298`),
  `IdleExpirySweeperFailureTests.cs` (`ParkedTransport:92`),
  `benchmarks/.../Perf/BenchmarkShared.cs` (`BenchmarkUdpTransportFactory`'s
  transport), direct call `Socks5UdpAssociateTests.cs:229`.

`Socks5UdpDatagram.DestinationAddress` (`IPAddress?` → `IPAddressValue?`):

- Sole production consumer: `UdpProxySession.cs:193,200`.
- Benchmark loopback server: `LoopbackSocks5UdpServer.cs:124,278,296-297`
  (decodes requests, echoes the destination).
- Test assertions (~12): `Socks5UdpAssociateTests.cs:74,126`,
  `Socks5UdpTransportSendTests.cs:47`, `UdpRelayTests.cs:36,108,236,325`,
  `UdpReceiveResilienceTests.cs:127`, `Socks5ControlTimeoutTests.cs:160`,
  `UdpPacketParsingTests.cs:18,40,69-70` (scopeId assertion must survive as
  `IPAddressValue.ScopeId`).
- `UdpPacketView`/`IPTcpUdpPacket` already use `IPAddressValue` — this change
  aligns SOCKS5 datagrams with the rest of the parsing layer.
- `Endpoint.From(IPAddressValue, ushort)` exists (`Domain.cs:58`), so the
  session's reverse leg becomes `Endpoint.From(datagram.DestinationAddress!.Value, port)`.
- `Socks5UdpCodec.TryEncode` already has the `IPAddressValue` overload
  (`Socks5Udp.cs:16`) — the forward leg needs zero codec changes.

## Invariants this task must not break

- Charge/credit exactly-once on setup queues (atomic-retire contract,
  udp-relay.md §bounded-setup-memory) — untouched by design, but the checker
  re-verifies.
- OCE token discipline: `SendOnReadySessionAsync`'s
  `catch (OperationCanceledException) when (caller-token && !shutdown)` shape
  stays byte-identical (`UdpProxyCoordinator.cs:195-200`).
- Teardown single-writer / owns-slot removal — no teardown path is modified.
- SelfTrafficRegistry token lifecycle in `Socks5UdpTransport.CreateAsync`.
- The send gate serialization contract (`_sendGate`) around the shared
  `_sendBuffer` (comment block at `Socks5UdpTransport.cs:192-198`).
