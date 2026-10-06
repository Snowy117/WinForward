# Design — UoT v2 per-flow UDP transport

Companion to `prd.md`. Requirement ids (R1–R9) refer to it. Anchors were verified during planning;
`research/uot-v2-protocol.md` carries the external protocol facts and their sources.

## 1. Shape and boundaries

**[decision] UoT is a mode of the existing SOCKS5 target, not a new target kind.** The flag rides
`Socks5Server`, so `ProxyTarget`'s union and the composite dispatch
(`UdpTransportContracts.cs:135-143`) are untouched; `Socks5UdpTransportFactory.CreateAsync`
branches on the flag. This is why the task needs none of the shared-connection seam exceptions the
readiness note lists: one connection per flow keeps the endpoint pair, the alias claim, the
self-traffic registration, and the counters in their existing shapes.

| Piece | File | Change |
| --- | --- | --- |
| UoT wire codec | `src/WinForward.Protocols/UotCodec.cs` (new) | magic address, request header, frame prefix |
| Domain CONNECT request | `src/WinForward.Protocols/Socks5State.cs` | `RequestLength(string)` + `WriteRequest(Socks5Command, string, ushort, Span<byte>)` (ATYP 3; none exists today) |
| Deferred-handshake dial | `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs` | dial + write greeting [+ auth] without reading replies; a completion step that validates them |
| UoT transport | `src/WinForward.Runtime/Socks5/Socks5UotTransport.cs` (new) | the transport + its factory branch |
| Fault vocabulary | `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs` | `UdpTransportHandshakeRejectedException` beside `UdpAssociationLostException` |
| Classification | `UdpProxyCoordinator.Send.cs:205-210`, `UdpProxyCoordinator.cs:549-561` | classify the new exception → `SetupFailure`; stop hard-coding `Fault` on the receive path |
| Config | `ConfigurationModels.cs:46-53,82-87`, `ConfigurationTargets.cs:41-65` | `udpOverTcp` DTO member + record member + passthrough |
| Observability | `UdpProxyLogging.cs:18-33` | a `udpTransport` field derived from the target |

Not touched: `UdpProxySession`, the coordinator's send/admission path, the reinjector, the two-class
retention, the expiry sweep, the capacity accounting, `UdpTransportFactory`, the alias table.

## 2. Endpoints, alias, self-traffic

**[decision]** `PeerEndpoint` is the SOCKS5 server endpoint (the connection's remote);
`LocalEndpoint` is the connection's local socket endpoint. Both are per-flow because the connection
is, so `UdpSessionSetup.cs:102-110`'s alias claim is unchanged and unique by construction (a second
flow can never share a TCP connection, hence never share the pair).

**[decision]** Self-traffic registration reuses the existing dial callback
(`Socks5ControlConnection.ConnectAsync(onSocketReady:)`, the same path
`Socks5UdpAssociation.cs:146-157` uses to register *before* the SYN); the tuple is released by the
connection's disposal. No per-connection/per-flow lifetime question exists here — the connection and
the flow are the same lifetime.

## 3. Establishment, pipelined

Today's serialization is a data dependency: the first datagram cannot leave until the `UDP
ASSOCIATE` reply names the relay endpoint (`Socks5UdpAssociation.cs:64-75`), so a flow pays
connect + greeting/method + auth + ASSOCIATE round trips before its queued first datagram is
flushed (`UdpSessionSetup.cs:101-122`).

**[decision] UoT connect mode removes every one of those waits from the first datagram's path.**

| Step | Actor | Waits for a reply? |
| --- | --- | --- |
| dial TCP (self-traffic registered before the SYN) | create | connect only |
| write greeting `[+ username/password message]` | create | **no** |
| publish endpoints, return; setup flushes the queued datagram | create | — |
| write CONNECT(magic) + UoT request header + `u16be length \| payload` in one buffer | first send | **no** |
| write `u16be length \| payload` | later sends | no |
| read method-selection reply, `[+ auth reply]`, then CONNECT reply status | receive loop | — |
| read frames → payloads | receive loop | — |

Why this is legal: TCP ordering already gives the server the bytes in the order it reads them
(greeting → auth → request → UoT header → frames); SOCKS5's request/reply *sequencing* is a
client-side convention that exists to let the client react to a rejection, not to make the server
able to parse. The cost of ignoring it is discovered late and handled by §6's vocabulary. The gain
is the removal of ≈2 round trips from the first-response path, on top of the ASSOCIATE round trip.

**[risk + fallback]** Writing the auth message before the method-selection reply is
protocol-rude. R7's verification covers it against the pinned build; if the pinned server mishandles
the pipelined flight, the fallback is to await the method/auth replies at create (reusing today's
`ConnectAsync` unchanged) and keep the CONNECT+header+datagram pipeline — the transport's external
behaviour and every acceptance criterion stay the same, only ≈2 RTT return.

## 4. Wire formats

- **CONNECT request** (written by the transport into its own send buffer): `VER 5 | CMD 1 |
  RSV 0 | ATYP 3 | len 23 | "sp.v2.udp-over-tcp.arpa" | port 0` (the port is ignored by the
  interceptor; the request carries no relay dependency).
- **UoT request header**: `isConnect u8 = 1 | ATYP u8 | address | port u16be`. The request
  destination is an ordinary **SOCKS** address — the intercepting server reads it with its SOCKS
  address serializer — so ATYP is `0x01` IPv4 / `0x04` IPv6 (`0x03` domain is never emitted: the
  flow destination is always a captured IP) and the constants are shared from
  `Socks5Messages.AddressTypeIPv4`/`AddressTypeIPv6` rather than restated, so the two encodings
  cannot diverge. `UotCodec` carries no non-connect address types: the `0x00`/`0x01`/`0x02` set
  belongs to protocol version 1's per-datagram stream format (`isConnect = 0`), which the codec does
  not implement and refuses to write (`isConnect: false` fails closed).
  **Corrected 2026-10-06** after `research/r7-server-verification.md` §2 pinned the server's request
  parser; the earlier revision here applied the version 1 stream-format table to the request.
- **Frame**: `u16be length | payload`. The payload ceiling is the product's frame ceiling (far
  below 65535); a payload above `ushort.MaxValue` fails closed rather than truncating.
- **Buffer sizing**: the transport's send buffer is `CONNECT = 30` (`VER/CMD/RSV/ATYP3 | len 23 |
  "sp.v2.udp-over-tcp.arpa" | port`, corrected from an earlier 29-byte estimate — the FQDN is 23
  bytes, pinned by a batch-1 test) `+ UoT header ≤ 20 + 2 +
  maximumFrameSize` = `52 + maximumFrameSize`. The session's receive window is **unchanged**: it already carries
  `MaximumSocks5UdpHeaderSize` of slack (`UdpProxyCoordinator.cs:215`), and UoT's 2-byte prefix is
  smaller than the header it replaces, so the `cap - 42` ceiling and the oversized rule keep their
  meaning.

## 5. Send path (packet-path discipline)

- **One writer gate per transport.** A stream cannot interleave frames: two datagrams written
  concurrently would corrupt the 2-byte prefix. The gate mirrors `Socks5UdpTransport`'s shape
  (contended fast path, copy-and-await slow path) because that shape is what keeps a capture
  buffer's span lifetime legal: the payload is encoded into the transport's reusable send buffer
  before any await.
- **Socket shape**: after the handshake the transport takes `GetUpstreamStream()` (which resets the
  socket timeouts to infinite, `Socks5ControlConnection.cs:284-292`), then sets `Blocking = false`
  so the warm path is a synchronous non-blocking send; a would-block copies `_sendBuffer[..written]`
  into a pooled buffer and awaits with the gate held.
- **First send** prepends the CONNECT request and the UoT request header, capturing the destination
  as the flow's destination.
- **Guards**: a later send whose destination differs from the captured one, or a payload above
  `ushort.MaxValue`, fails closed (throws) — connect mode cannot honour either, and silently
  mis-framing is the one unacceptable outcome.
- `IUdpExchangeCounters.DatagramsSent` is incremented once the kernel accepted the write (native
  parity: `Socks5UdpTransport.cs:291`); the warm path stays allocation-free, pinned by the same
  kind of gate test the native transport has.

## 6. Receive path

- **First reads** consume the deferred replies: method selection, `[+ auth reply]`, then the CONNECT
  reply status. Order matters and is exactly the order the server wrote them.
- **Frames**: read the 2-byte prefix, then the payload into the caller's buffer; a frame whose
  length exceeds the buffer is consumed (keeping the stream aligned) and reported `Oversized`;
  a zero-length frame is a legal empty datagram.
- **Source synthesis.** Connect mode carries no on-wire source. The transport sets
  `UdpTransportDatagram.SourceAddress` from the captured flow destination; without it
  `UdpProxySession.TryGetReceiveSource` classifies every reply as an S6a domain-typed skip
  (`UdpProxySession.cs:399-414`). Consequence to record: the foreign-source counter cannot fire on
  this path — the observation is structurally impossible over connect mode, not suppressed.
- **Faults are typed and never raw.**

| Observed | Recorded + thrown | Classified as |
| --- | --- | --- |
| CONNECT reply status ≠ success, or EOF before it | `UdpTransportHandshakeRejectedException` | `SetupFailure` (cooldown) |
| EOF / RST / any stream fault after establishment | `UdpAssociationLostException` | `AssociationLost` (counted, no cooldown) |

  **[decision] This is why the transport must never let a raw `SocketException(ConnectionReset)`
  escape**: `UdpProxySession`'s receive loop treats that code as a *skip* and continues
  (`UdpProxySession.cs:343-349`) — correct for an ICMP unreachable answering one UDP send, an
  infinite spin on a dead stream. Every stream fault is translated into the typed fault above.
  The send path repeats the fail-closed check (`_fault is { } f → throw f`), mirroring
  `Socks5UdpTransport.cs:262`.

## 7. Fault classification (the one shared-path change)

`UdpProxyCoordinator.TeardownReasonFor` (`Send.cs:205-210`) gains one branch, and
`RemoveReceiveFailedSessionCoreAsync` (`UdpProxyCoordinator.cs:549-561`) stops hard-coding
`Fault`: it reads the session's recorded fault and classifies it through the same helper.

- `UdpTransportHandshakeRejectedException` → `SetupFailure` — semantically exact ("the relay setup
  failed against a reachable-looking server: arm the setup cooldown"), and it is what the native
  path already does when `UDP ASSOCIATE` is refused at setup. Without it, a systematically
  refusing server would be re-dialed once per client retransmit instead of once per cooldown.
- `UdpAssociationLostException` → `AssociationLost` (counted, no cooldown).
- Everything else → `Fault`, unchanged.

**[recorded] Native-behaviour delta: none.** The native transport only throws the association-lost
exception from the *send* path (`Socks5UdpTransport.cs:262`); its receive path classifies socket
faults through `ClassifyReceiveFault` and never surfaces either typed exception. The check phase
must prove this with the existing suite rather than by argument.

The session must expose the recorded exception (the scope stores it at `UdpProxySession.cs:369` and
tests it at `:376`); an internal accessor is the smallest seam.

## 8. Configuration and observability

- `Socks5ServerDto` gains `bool? UdpOverTcp` → JSON `udpOverTcp`. The loader uses camelCase with
  `UnmappedMemberHandling.Disallow` (`ConfigurationModels.cs:78`), so the DTO member is mandatory
  for the key to parse at all and an unknown spelling keeps failing closed.
- `Socks5Server` gains `bool UdpOverTcp = false` as its last positional member, defaulted so the
  existing construction sites (tests, fixtures, harness) compile unchanged; `ValidateServer`
  (`ConfigurationTargets.cs:41-65`) passes it through. A bool implies no new validation warning.
- **Observability**: `targetKind` stays `local|socks5` — UoT is a mode, not a third kind, and
  letting `IsLocal`-style derivation carry it would hide that. `UdpProxyLogging` gains a
  `udpTransport` field (`uot` | `native` | null) derived from the resolved target next to the
  existing fields (`:25-33`), so the harness columns and product events can attribute the mode
  without a new event.

## 9. Harness and evidence

- `--target` gains `uot` (`SoakTargetKind`); the three scenarios start a UoT fixture and build the
  *same* `Socks5UdpTransportFactory` with `UdpOverTcp = true` — the mode rides the target, so the
  harness needs no new factory composition. `udp.sessionBudget`'s `--target` refusal
  (`SoakOptions.cs:242-245`) is lifted for the new token, because per-session descriptor cost is one
  of the two things this change claims.
- **New benchmark fixture** `LoopbackSocks5UotServer` (beside `LoopbackSocks5UdpServer`): greeting
  [+ auth] → CONNECT-to-magic → UoT header → framed datagrams, bridged to a per-connection upstream
  UDP socket toward `EchoReceiver`. It deliberately **defers the CONNECT reply until after it has
  read the first frame**, which makes the pipelining an observation rather than a construction. No
  last-sender field exists here: connect mode is destination-bound by construction.
  Counters: `{ connections, connectReplies, frames }` — the direct analogue of
  `socks5Handshakes { controlConnections, associateReplies }`.
- **New TestSupport fixture** `ScriptedSocks5UotServer` with explicit hooks (reply gate, received
  frames, injected replies, drop-connection) for the unit-level pipelining and fault tests. The
  existing `ScriptedSocks5UdpServer` stays as the native fixture.
- Columns: `udp.churn` and `udp.burstEstablishment` for `--target socks5` vs `--target uot`;
  `udp.sessionBudget --target uot` for descriptors/session. Results land in
  `benchmarks/results/2026-MM-DD-uot-per-flow/` with the hand-authored README convention (commands
  block, accounting identity, `## Superseded / Still standing`).

## 10. Risks, rollback, and what would falsify the change

| Risk | Handling |
| --- | --- |
| Pipelined greeting/auth is mishandled by the pinned server | R7 verification before implementation locks behaviour; fallback in §3 keeps every acceptance criterion |
| A stream fault surfacing as a raw socket exception spins the session | §6's typed translation, with a test that a mid-flow death tears the flow down rather than looping |
| Frame interleaving under concurrent sends | §5's gate, with a test that concurrent sends never interleave (the native suite has the precedent) |
| `Blocking = false` conflicting with the socket's timeout settings | `GetUpstreamStream()` resets them to infinite first; a test pins the resulting mode |
| TCP carriage degrades lossy paths | Documented caveat; native stays the default and the mode is per-server opt-in |

**Rollback**: the mode is one config field, off by default. Reverting deletes the UoT transport +
codec, the factory branch, the config/record members, and the log field; the one shared-path change
(§7) is additive and can stay or go independently.

## 11. Requirement → evidence map

| Requirement | Evidence |
| --- | --- |
| R1 one connection per flow, disposed with it | harness accounting (connections per wave) + ownership test |
| R2 pipelined establishment | scripted-server test: the frame is read before the CONNECT reply is written |
| R3 framing, partial reads, oversized | codec unit tests + transport receive tests (split frame, oversized frame, zero-length frame) |
| R4 fault vocabulary | tests: CONNECT refusal → `SetupFailure` + cooldown; mid-flow death → `AssociationLost`; no skip-class spin |
| R5 seam obligations | counters test (one-shot retention), self-traffic test, alias/endpoint test, log-field test |
| R6 configuration | DTO parse test (key present/absent, unknown spelling still fails closed) + validator test |
| R7 server verification | `research/` memo pinned to the exact build, incl. the pipelined flight |
| R8 harness evidence | `benchmarks/results/2026-MM-DD-uot-per-flow/` README + jsonl columns |
| R9 documentation | README + `.trellis/spec/backend/udp-relay.md` |
