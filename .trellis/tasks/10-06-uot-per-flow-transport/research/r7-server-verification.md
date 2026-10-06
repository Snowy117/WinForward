# R7 — server verification: UoT v2 connect mode against the pinned sing-box build

Collected 2026-10-06 for `10-06-uot-per-flow-transport` (R7; design §3, §4, §6). This memo pins the
server build, records the server-side facts the client design rests on, carries the on-box probe the
operator can run against the running box, and lists what stays unverified.

Two hard boundaries bound this note, and both are stated rather than papered over:

- **This host cannot reach the operator's box.** Everything below is *source* read at a pinned
  commit, fetched from this host, plus the probe that would settle the runtime half on the box.
  Nothing here is a measurement of a running sing-box.
- **The pin is not the release the earlier experiments ran on.** The SOCKS5 reply-path experiments
  ran on sing-box 1.14.1 (sing v0.9.4); the operator's box runs the testing channel. §1 pins both,
  and §3 states which facts were verified at which pin.

**How to read this note.** Markers, the convention of `research/uot-v2-protocol.md` and
`10-05-remove-udp-association-sharing/research/l2-readiness.md`:

- **[recorded]** — sourced from an artifact in this repository, cited inline.
- **[external→recorded-at-<pin>]** — a public artifact fetched from this host and pinned at the named
  tag/commit (`-at-collection` where the fact is about the artifact set itself, such as a tag list),
  cited `file:line` plus its URL. The planning memo's `[external]` becomes this marker once the
  artifact is pinned; an unpinned `[external]` claim stays `[to confirm]`.
- **[analysis]** — this repository's own reasoning on top of recorded and pinned facts.
- **[to confirm]** — not established here: either it needs the running box (§4's probe), or it is not
  reachable from this host at all (§5).
- **[recorded on-box]** — a claim the operator's probe transcript establishes. **No such claim exists
  in this memo**; §4 is the procedure that produces them.

Nothing is asserted on this memo's authority. Where a claim is a *prediction* about the box rather
than a fact about pinned source, it says so.

## 1. The pins

| Pin | What | Commit | Fetched/served |
| --- | --- | --- | --- |
| **A — the operator's box** | sing-box `testing` branch | `2ff3985c0a8fd628ab10b9ec9cd3d37512909c57` (2026-10-03) | `git ls-remote`/shallow clone; `go.mod:46` pins `github.com/sagernet/sing v0.9.7-0.20260929150544-6f21f2425a95` |
| **A — the operator's library** | `sing` at the commit that pseudo-version derives from | `6f21f2425a959912c37d2ef43d61e2a663315dea` (committer `2026-09-29T23:05:44+08:00` = UTC `20260929150544`) | shallow clone of `SagerNet/sing` `dev` |
| **B — the release the earlier experiments ran on** | sing-box v1.14.1 | `1ac1a339cb1223e9c70eae14c44411c75033c02d` | shallow clone of the tag; `go.mod:48` pins `github.com/sagernet/sing v0.9.4` |
| **B — its library** | sing v0.9.4 | `ef37987fa0bee9702bd086f6ca728c3d61d26eac` | shallow clone of the tag |
| for comparison | sing-box v1.14.2 (newest release at collection) | `af6e64c3b69e6132ebaee0e1a3d24e93903f6709` | `go.mod:48` pins `github.com/sagernet/sing v0.9.6-0.20260922013354-87c33f17688f` |
| for comparison | sing v0.9.6 (newest release tag) | `5f9aad7def20103b626459b006fcc907382f8373` | fetched per-file |

**[analysis] Why pin A is the operator's build and not just a guess.** `sing-box`'s `testing` branch
is the testing channel; its `go.mod` requires exactly `v0.9.7-0.20260929150544-6f21f2425a95`, and
that pseudo-version's timestamp (`20260929150544`) and hash suffix (`6f21f2425a95`) both derive from
commit `6f21f2425a959912c37d2ef43d61e2a663315dea` of the `sing` repository — whose *committer* time
(UTC `2026-09-29 15:05:44`) is the pseudo-version's stamp. The archived build-pinning lesson
(`10-05-udp-association-sharing-correctness/prd.md`, Notes) named exactly this string, so the
library half of the operator's build is pinned to a single commit rather than to a version range.

**[to confirm] The sing-box half.** The `testing` branch tip moves. The pin above is the tip at
collection time; the operator's binary may be an earlier testing-channel build of the same channel.
The *sing* commit is the airtight half (it is what the client's wire behaviour depends on); the
sing-box half matters only for the inbound/router code, which §3 checks at both its v1.14.1 release
and the testing tip and finds identical in the places this design depends on.

**[external→recorded-at-collection] A public tag does not exist for `v0.9.7-0.20260929150544`.**
It is a Go pseudo-version (derived from a commit on `dev`), not a tag: the `sing` repository's newest
tag at collection is `v0.9.6`. That is why the pin above is a commit, and why the probe records the
running `sing-box version` output (§4).

## 2. The finding that changes the picture: the request header's address types

**This is the one place where the pinned source contradicts this repository's implementation, and it
is a stop-the-line item for the mode.** It is recorded here and reported upward; no code was changed
by this memo (batch 6a is documentation-only). **Batch 6b then made the fix — see "Resolution"
below** — so the client's encoding now matches the pinned parser; only the on-box half stays open.

**[external→recorded-at-A and -B] The v2 request destination uses the ordinary SOCKS address types.**
sing-box's own protocol documentation, identical at both pins
(`docs/configuration/shared/udp-over-tcp.md`, v1.14.1 lines 60-78), separates the two encodings:

- *Protocol version 2 → Request format* (lines 64-72): `isConnect u8 | ATYP u8 | address | port u16be`,
  and line 72: "**ATYP / address / port**: Request destination, uses the SOCKS address format."
- *Protocol version 1 → Stream format* (lines 46-58) — the table the planning memo quoted — carries
  the *different* address types `0x00` IPv4, `0x01` IPv6, `0x02` domain, and version 2's
  *non-connect* stream format (lines 80-82) is "the same as the stream format in protocol version 1".

The implementation agrees, at every pin checked:

- `common/uot/protocol.go:41-52` (`sing` @A; byte-identical at `v0.9.4`):
  `ReadRequest` reads `request.Destination` with **`M.SocksaddrSerializer`** (line 47), i.e. the
  ordinary SOCKS mapping.
- `common/metadata/socksaddr.go:3-7`: `SocksaddrSerializer` maps `0x01` IPv4, `0x04` IPv6,
  `0x03` FQDN. `common/metadata/family.go:5-10` defines those constants; `AddressFamilyEmpty` is
  `0xff`, **not** `0x00`.
- `common/uot/protocol.go:19-23`: the `AddrParser` with `0x00`/`0x01`/`0x02` exists in the same file,
  and `common/uot/conn.go:82`, `:91`, `:135`, `:144` use it **only** on the `!isConnect` branches —
  the non-connect/v1 stream format, which this product never emits.
- `common/metadata/serializer.go:131-163` (`ReadAddress`): an unknown family byte falls to
  `default` and returns `unknown address family: <byte>` (lines 161-162).
- Unchanged at `sing` `v0.9.0`…`v0.9.6`, at the pinned commit (`dev`) and at
  `main` (`0ad23b637bd49e7c2982640137df7598dde93ff9`).

**[recorded] What this repository wrote at the time of the finding** (pre-fix anchors; corrected in
batch 6b, see the Resolution below). `UotCodec.TryWriteRequestHeader`
(`src/WinForward.Protocols/UotCodec.cs:53-70`) wrote `destination[1] = isIpv4 ? 0x00 : 0x01`
(constants at `:31`, `:34`) — the *per-datagram* UoT values — into the **request header**, and
`Socks5UotTransport.EncodeFrame` (`src/WinForward.Runtime/Socks5/Socks5UotTransport.cs:271-272`)
writes that header after the magic `CONNECT`. The comment at `UotCodec.cs:9-11` and the test
`Socks5ProtocolTests.UotRequestHeaderCarriesItsOwnAddressTypesForBothIpFamilies`
(`tests/WinForward.Runtime.Socks5.Tests/Socks5ProtocolTests.cs:87-116`, pinning `{ 1, 0, 192, … }`)
both encoded the same reading of the version 1 table applied to the version 2 request; both are gone
from the corrected tree.

**[analysis] What the pinned server does with those bytes** (traced through the pinned source; a
prediction the probe in §4 settles on the box):

| Client destination | Bytes sent (after `isConnect=1`) | Server `ReadRequest` outcome | Client observes |
| --- | --- | --- | --- |
| IPv4 | `0x00` + 4 addr + 2 port | `familyMap[0x00]` misses; the zero value `0x00` is no `Family` constant, so `ReadAddress` returns `unknown address family: 0` | `common/uot/router.go:58-63`: the error is logged and `N.CloseOnHandshakeFailure` runs; the wrapped `LazyConn` writes the SOCKS5 failure reply `05 01 00 01 00000000 0000` (`protocol/socks/lazy.go:49-69`, `protocol/socks/socks5/protocol.go:253-273`), then the connection closes |
| IPv6 | `0x01` + 16 addr + 2 port | `familyMap[0x01]` is **IPv4**: the server reads 4 of the 16 address bytes as the address and the next 2 as the port, and returns a bogus but *valid* destination — no error | the router logs `inbound UoT connect connection to <first 4 bytes of the IPv6 address>:<bytes 5-6 as a port>`; the remaining 10 address bytes and the real port stay in the stream, so the frame length is read at the wrong offset |

So the failure shape differs by family and neither is the intended one: **IPv4 destinations fail
closed** (a proper `REP != 0` reply → the existing `UdpTransportHandshakeRejectedException` →
`SetupFailure` + cooldown), while **IPv6 destinations are silently mis-parsed** and the server
directs its upstream UDP traffic at an address assembled from the low bits of the client's IPv6
destination. On the pinned build, every flow of the mode's target population (IPv4 destinations)
would fail setup and be re-dialed once per 1 s setup cooldown, forever.

**[analysis] Correcting it is a one-constant change plus its fixtures**, and batch 6b made it: the
request-header types are `0x01` IPv4 / `0x04` IPv6 (`0x03` FQDN, never emitted in
connect mode because destinations are captured IPs), while the `0x00`/`0x01`/`0x02` values stay
where they belong — the non-connect/v1 per-datagram form this product does not emit. The three
artifacts that pinned the wrong assumption moved with it: `UotCodec`, the protocol test above, and the
two loopback fixtures (`benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UotServer.cs:378-388`,
`tests/WinForward.TestSupport/ScriptedSocks5UotServer.cs`) — at the time of the finding the fixtures
parsed the request header with the client's own mapping, which is why the R8 harness measured a
working mode: it was the client's mirror, not an independent server. **The R8 columns are therefore
internal-consistency evidence, not interop evidence**, and the mode cannot be called verified until
the probe below passes against the real build; the header half of that precondition is now closed
(Resolution below).

**[analysis] The planning memo's source for the wrong reading.** `research/uot-v2-protocol.md` §1
cites "the documentation fix in SagerNet/sing-box commit 0970bbc" for the `0x00`/`0x01`/`0x02`
values. That commit (sha `0970bbc9d8b880a0ff4d1dc828976670ad867626`, 2025-02-27,
<https://github.com/SagerNet/sing-box/commit/0970bbc9d8b880a0ff4d1dc828976670ad867626>) added the
address-type table under *Protocol version 1 → Stream format* — exactly where the pinned
documentation still has it — and left line 72's "Request destination, uses the SOCKS address format"
for version 2's request untouched. The table was applied to the wrong message.

**[recorded] Resolution — batch 6b (this tree, 2026-10-06).** The client now writes the address types
the pinned parser reads, and the artifacts that carried the misreading were corrected with it:

| File | What changed |
| --- | --- |
| `src/WinForward.Protocols/Socks5State.cs` | the RFC 1928 set is named once: `AddressTypeIPv4 = 0x01`, `AddressTypeIPv6 = 0x04`, `AddressTypeDomain = 0x03`, plus `AddressFieldLength(byte)` (4 / 16 / 0 for the length-prefixed domain form, negative for a family no SOCKS implementation defines) — the mapping the SOCKS5 writers, the UoT codec and every loopback server share |
| `src/WinForward.Protocols/UotCodec.cs` | `TryWriteRequestHeader` writes those shared constants (the private `0x00`/`0x01` pair is gone) and rejects `isConnect: false`, since that mode's per-datagram types are a format this codec does not implement |
| `tests/WinForward.Runtime.Socks5.Tests/Socks5ProtocolTests.cs` | `UotRequestHeaderUsesTheSocksAddressTypesForBothIpFamilies` pins the new bytes; `UotRequestHeaderDestinationDecodesThroughTheSocksReplyParser` decodes them with `Socks5Messages.TryParseReply` — an independent SOCKS5 decoder — so the per-datagram types fail in the unit suite |
| `tests/WinForward.TestSupport/ScriptedSocks5UotServer.cs`, `benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UotServer.cs` | both decode the request destination through the shared SOCKS mapping and refuse an undefined family as `unknown address family: <byte>`, mirroring `common/metadata/serializer.go:161-162`; the scripted fixture's two behaviours are pinned by `TheFixtureRefusesARequestDestinationTheSocksMappingDoesNotDefine` and `TheFixtureDecodesAnIpv6RequestDestinationThroughTheSocksAddressType` |
| `research/uot-v2-protocol.md` §1, `design.md` §4 | carry the corrected reading (the `0x00`/`0x01`/`0x02` table belongs to the v1/non-connect stream format) |

Both halves of the guard were checked by mutation on this tree: reverting the writer to
`0x00`/`0x01` fails the two protocol tests, and reverting the shared mapping to the old
`0x00 => 4` / `0x01 => 16` mirror fails
`TheFixtureDecodesAnIpv6RequestDestinationThroughTheSocksAddressType` (the `0x00` case then stops
being refused as an unknown family too). **The R8 columns remain
internal-consistency evidence** — the loopback fixture is not the pinned server — but they now
decode with the server's mapping instead of the client's mirror, and they were re-measured after the
fix (`benchmarks/results/2026-10-06-uot-per-flow/`).

**[to confirm] Still open: the on-box probe.** The resolution above is a source-level conclusion.
The probe in §4 (cells 1 and 2, `--encoding socks`) is still the observation that turns it into a
`[recorded on-box]` claim, and §5's residual R4 stays open until the operator runs it. With the
corrected client, cell 3 is a counterfactual (the pre-fix build's bytes), not a description of the
shipped encoding.

## 3. Pinned source verification of the five facts the design rests on

Each fact is stated once, marked with the pin it was verified at. Line anchors are the ones read at
that pin; where a fact holds at both pins the v1.14.1 anchors are given in parentheses only when they
differ. URLs use the commit permalink form
`https://github.com/SagerNet/sing-box/blob/<sha>/<path>#L<line>`.

### (a) The SOCKS inbound wraps `uot.NewRouter` unconditionally

**[external→recorded-at-A and -B] Verified.**

- `protocol/socks/inbound.go:39-61` (`NewInbound`) constructs `router: uot.NewRouter(router, logger)`
  at line 48, with no option consulted.
- `option/simple.go:14-18` (`SocksInboundOptions`) carries only `ListenOptions`, `Users` and
  `DomainResolver`: **no inbound field enables or disables UoT**. The UoT option exists only on the
  outbound side, `option/simple.go:73-81` (`SOCKSOutboundOptions.UDPOverTCP`, line 80) — i.e. in
  sing-box's own client, which is what makes a UoT client an expected peer rather than an exploit.
- The **`mixed` inbound wraps the same router** (`protocol/mixed/inbound.go:53`), so an operator
  running `mixed` instead of `socks` gets UoT too.
- Verified identically at pin B (`protocol/socks/inbound.go:48`; the inbound options are
  `option/simple.go:14-18` at pin A and `option/simple.go:8-12` at pin B — neither carries a UoT
  field; the outbound's `udp_over_tcp` is `option/simple.go:80` at pin A and `:29` at pin B).

*URLs:*
<https://github.com/SagerNet/sing-box/blob/2ff3985c0a8fd628ab10b9ec9cd3d37512909c57/protocol/socks/inbound.go#L48>,
<https://github.com/SagerNet/sing-box/blob/2ff3985c0a8fd628ab10b9ec9cd3d37512909c57/option/simple.go#L14-L18>,
<https://github.com/SagerNet/sing-box/blob/2ff3985c0a8fd628ab10b9ec9cd3d37512909c57/protocol/mixed/inbound.go#L53>.

### (b) The UoT router intercepts the magic FQDN, reads the header, and wraps the stream as a packet connection

**[external→recorded-at-A and -B] Verified.** `common/uot/router.go` (sing-box):

- `:27-49` (`RouteConnection`) and `:55-82` (`RouteConnectionEx`) switch on
  `metadata.Destination.Fqdn`; the `case uot.MagicAddress` branch calls `uot.ReadRequest(conn)`,
  logs `inbound UoT connect connection to <destination>` when `request.IsConnect` (`:66`; `:35` on
  the non-Ex path), replaces the metadata destination with the request's (`:70-71`) and hands
  `uot.NewConn(conn, *request)` to the router's packet-connection path (`:72`).
- `uot.ReadRequest`/`uot.NewConn` are the **`sing` library's** `common/uot` package:
  `protocol.go:41-52` and `conn.go:31-39` (byte-identical at `v0.9.4`).
- A read failure takes `:58-63`: the error is logged as `process connection from <source>: <err>` and
  `N.CloseOnHandshakeFailure` runs (see fact (c) for what the client then reads).
- The magic string and version live at `common/uot/protocol.go:13-17` (`Version = 2`,
  `MagicAddress = "sp.v2.udp-over-tcp.arpa"`, `LegacyMagicAddress` for v1), and the FQDN comparison
  is exact.

*URLs:*
<https://github.com/SagerNet/sing-box/blob/2ff3985c0a8fd628ab10b9ec9cd3d37512909c57/common/uot/router.go#L55-L82>,
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/common/uot/protocol.go#L41-L52>.

### (c) Connect mode is bound to the single request destination; what the reply write path actually is

**[external→recorded-at-A and -B] Verified.** `sing` `common/uot/conn.go` (byte-identical at
`v0.9.4`), the `N.NetPacketConn` the router hands to the packet-connection machinery:

- `:22-39`: the connection stores `isConnect` and `destination` from the request, for the
  connection's whole life; there is no per-read or per-write field to refresh.
- `:50-74` (`ReadFrom`) and `:109-128` (`ReadPacket`): in connect mode the *source* of every frame is
  `c.destination` (`:52-53`, `:110-111`) — the wire carries no source at all.
- `:76-107` (`WriteTo`), `:130-156` (`WritePacket`) and `:46-48` (`Write` → `WriteTo(p, c.destination)`):
  in connect mode the destination argument is **ignored**; the only bytes written are
  `u16be length | payload` (`:135-155` guard every address write behind `if !c.isConnect`).
  Write serialization is one `sync.Mutex` (`:28`, locked at `:77`, `:131`).
- The documentation states the same framing: `docs/configuration/shared/udp-over-tcp.md:74-78`
  (connect stream format = `length u16be | data`), and `:80-82` (non-connect = the v1 format with
  per-packet addressing).

**[recorded] The defect this shape cannot have.** The archived measurement
(`10-05-udp-association-sharing-correctness/prd.md`, Background: two live flows per association →
45–50 % of replies reach the flow that asked, eight flows → 9 %, zero packet loss) has a mechanism in
the pinned source, and that mechanism is what sharing removed. Line numbers are pin A's; `v0.9.4`
differs only where noted.

- `sing` `common/bufio/bind.go`, `serverPacketConn`: the relay socket keeps **one** peer address,
  refreshed on every read (`:140`, `:149`) and every reply is written to it (`:153-159`). At
  `v0.9.4` the same three behaviours sit at `:143-171` without the mutex — the only change between
  the pins is the `sync.RWMutex` around the field.
- `sing` `protocol/socks/packet.go`, `AssociatePacketConn`: the client-facing half of the same
  shape. `ReadFrom`/`ReadPacket` store `remoteAddr` (`:52`, `:88`) and `Write` sends to it
  (`:108-109`; `v0.9.4`: `:107-109`). `WritePacket` does write the address it is handed, so the
  misdelivery is a property of *which* address the caller hands over, not of the framing.

**Both are still present at the operator's pin** (`:129-176` for the first; the second unchanged
apart from a buffer-release fix) — the correctness program's removal of sharing remains right — and
neither has **any analogue in connect mode**: the UoT connection has no stored peer, no caller-chosen
address reaches the wire, and the destination a reply is written with is discarded before framing.

**[analysis] Consequences the design already relies on, now pinned:**

1. **A foreign reply source cannot be expressed over connect mode.** The frame has no source field,
   so the client must synthesize one; `Socks5UotTransport.ReceiveAsync`
   (`src/WinForward.Runtime/Socks5/Socks5UotTransport.cs:345-352`) declares the captured flow
   destination, which is exactly the value `ReadPacket` returns server-side. The
   `udpResponseSourceMismatch` counter therefore cannot fire on this path — the observation is
   structurally impossible, not suppressed (design §6; `udp-relay.md`, "Reply-ownership
   observability").
2. **One connection carries one route decision**, taken from the request destination at
   `common/uot/router.go:70-71` — the same one-destination property the native association has, but
   now per flow rather than per shared association.
3. **The reply path is the same router machinery, but the defect's site is not on it.**
   `route/conn.go:177-292` (pin A; `:156-271` at pin B) dials the outbound and copies replies back
   toward the inbound connection (`:290-291`). On the native path that inbound connection is the
   `AssociatePacketConn` chain whose stored peer is the defect above; on the UoT path it is the
   destination-bound `uot.Conn`, which discards the destination. The machinery is shared; the object
   that made a shared association unsafe is not.

*URLs:*
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/common/uot/conn.go#L130-L156>,
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/common/bufio/bind.go#L129-L176>,
<https://github.com/SagerNet/sing-box/blob/2ff3985c0a8fd628ab10b9ec9cd3d37512909c57/route/conn.go#L177-L292>.

### (d) The SOCKS5 server reads the flight sequentially and tolerates a fully pipelined client

**[external→recorded-at-A and -B] Verified, with a credential-shape precondition.**

`sing` `protocol/socks/handshake.go`, `HandleConnectionEx` — one `*std_bufio.Reader` over the
connection (`sing-box` `protocol/socks/inbound.go:76` creates it once per connection) and a strictly
sequential consumption of it:

| Line (pin A) | Read/write |
| --- | --- |
| `:136` | `reader.ReadByte()` — the version byte |
| `:176` | `socks5.ReadAuthRequest0(reader)` — the greeting |
| `:182-188` | `WriteAuthResponse(Method: AuthTypeNoAcceptedMethods)` when the client did not offer username/password and the inbound has users |
| `:194-199` | `WriteAuthResponse(Method: authMethod)` |
| `:202-205` | `socks5.ReadUsernamePasswordAuthRequest(reader)` — **only when `authMethod == AuthTypeUsernamePassword`** |
| `:213-216` | `WriteUsernamePasswordAuthResponse` |
| `:222-225` | `socks5.ReadRequest(reader)` — the `CONNECT` |
| `:226-229` | `handler.NewConnectionEx(conn, …)`; the reply is **not** written here |

**[analysis] Why that makes the pipelined flight legal.** The server never waits for the client to
wait: each read is a blocking read on a buffered reader over the same TCP stream, and bytes the
client wrote early are simply already there (in the socket or the reader's buffer) when the server
asks for them. TCP ordering delivers greeting → auth message → request → UoT header → frames in
exactly the order the server consumes them (design §3). The reply-writing steps are interleaved but
never gate a read: `WriteAuthResponse` happens *before* the auth message is read, and the client's
decision not to read it is a choice, not a requirement.

**[external→recorded-at-A] The `CONNECT` reply is written lazily, so the client's read order still
holds.** The socks handshake hands the router a `LazyConn` (`protocol/socks/handshake.go:228`), and
`protocol/socks/lazy.go:26-89` writes the SOCKS5 success reply on the first `Read`/`Write` — for a
UoT connection that is the first *reply frame* written back. A failure path writes the failure reply
through `LazyConn.HandshakeFailure` (`:49-69`) via `common/network/handshake.go:36-61`
(`CloseOnHandshakeFailure`). So the client sees, in order: method selection, `[+ credential reply]`,
then the `CONNECT` reply — and, before the first frame — *exactly* the sequence
`Socks5UotTransport.ReceiveAsync` → `EstablishAsync` → `ReadConnectReplyCoreAsync` expects
(`src/WinForward.Runtime/Socks5/Socks5UotTransport.cs:306-421`), whatever the delay between them.

**[analysis] The precondition: the credential shape must match on both sides.**

- Client offers username/password ⇔ `server.Username is not null`: the greeting is `05 02 00 02`
  (`Socks5Messages.Greeting`, `src/WinForward.Protocols/Socks5State.cs:33-34`), and the RFC 1929
  message is written when the password is set too (`Socks5ControlConnection.WriteDeferredGreetingAsync`,
  `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs:553-561`) — both **before** reading
  anything.
- The server reads the auth message only when its inbound configured `users` — `auth.NewAuthenticator`
  returns nil for an empty list (`sing` `common/auth/auth.go:14-17`), and `authenticator != nil` is
  what selects method `0x02` (`protocol/socks/handshake.go:189-193`). If the client writes credentials
  against an inbound **without** `users`, the server answers `05 00` and immediately reads a SOCKS5
  request — it gets the auth message's leading `0x01` and fails the request (`socks5.ReadRequest`
  rejects a non-`0x05` version). The client's completion accepts `method == 0` without complaint, then
  reads the failure reply the `LazyConn` writes and throws — the flow fails closed with
  `SetupFailure`/cooldown, not a hang.
  The reverse mismatch (server has `users`, client offers only `0x00`) answers `05 ff` first
  (`handshake.go:182-188`), which the client's completion rejects as a refused method.
  **So: credentials present on both sides or absent on both sides; a mixed configuration is a
  fail-closed setup loop, not a crash.**

**[to confirm] That the operator's inbound has the shape the client's configuration assumes** — the
probe in §4 records the method byte and the credential reply, which is the direct observation.

*URLs:*
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/protocol/socks/handshake.go#L126-L230>,
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/protocol/socks/lazy.go#L26-L89>,
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/common/network/handshake.go#L36-L61>.

### (e) UoT v2 exists at the pinned version

**[external→recorded-at-A and -B] Verified.**

- `common/uot/protocol.go:13-17`: `Version = 2` and `MagicAddress = "sp.v2.udp-over-tcp.arpa"` are
  present and unchanged from `v0.9.0` through the operator's commit; `EncodeRequest`/`WriteRequest`
  (`:54-72`) and the client (`common/uot/client.go:15-48`) default to version 2 for
  `version == 0 or Version`.
- The pinned documentation's support table (`docs/configuration/shared/udp-over-tcp.md:34-38`) lists
  sing-box for UoT v2 since **v1.2-beta9**, and `:28-30` records that version 2 is the default.
- The inbound path is unconditional (fact (a)), so no server configuration enables it: the operator's
  socks/mixed inbound terminates UoT v2 as built.
- Verified identically at pin B.

**[analysis] What "availability" does *not* mean here:** the version number is not an on-wire field.
It is carried by which magic FQDN the client connects to (`sp.v2.udp-over-tcp.arpa` selects v2,
`sp.udp-over-tcp.arpa` selects v1 at `common/uot/router.go:42`/`:74`). A server that predates v2
would fall through to a normal TCP `CONNECT` to the `.arpa` name and fail at DNS, which is why §4's
probe asserts the router's `inbound UoT connect connection to` line rather than trusting the reply.

*URLs:*
<https://github.com/SagerNet/sing/blob/6f21f2425a959912c37d2ef43d61e2a663315dea/common/uot/protocol.go#L13-L23>,
<https://github.com/SagerNet/sing-box/blob/2ff3985c0a8fd628ab10b9ec9cd3d37512909c57/docs/configuration/shared/udp-over-tcp.md#L34-L38>.

## 4. The on-box probe

The probe exercises the same facts end to end against the running build, records a transcript, and is
the only thing that can turn the `[to confirm]` items of §5 into `[recorded on-box]` claims. Run it
from a host that can reach the inbound, with a UDP echo destination the box can reach.

### 4.1 Prerequisites

1. **An echo destination.** Any UDP socket that answers from the address the client sent to works.
   The simplest is a five-line echo on the probe host, on a port the box's route does not hijack
   (avoid 53 if DNS is hijacked):

   ```bash
   python3 - <<'PY'
   import socket
   s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.bind(("0.0.0.0", 40000))
   print("echo on 0.0.0.0:40000", flush=True)
   while True:
       data, addr = s.recvfrom(65535)
       s.sendto(b"echo:" + data, addr)
   PY
   ```

   If the box runs on a different host, pass the probe host's reachable address as `--target` and
   make sure the box's route sends that UDP flow through a UDP-capable outbound.
2. **The inbound's log at `info` (default) and a file to read it from.** The router's line (below) is
   info level; the failure path is error level. Run with `"log": {"level": "debug"}` if you also want
   the `connection closed:` lines.
3. **Record the build**: `sing-box version` (and, if it is a custom build, the `go.mod` sing pin).
   Write both into the transcript — §1 pins a commit, and this line is what ties the run to it.
4. **A throwaway credential pair** if the inbound has `users`: the server logs the attempted password
   on an auth failure (`protocol/socks/handshake.go:218`), so never probe with the real one.

### 4.2 The probe

Save as `r7-uot-probe.py` and run `python3 r7-uot-probe.py --server BOX:1080 --target ECHO:40000
[--username U --password P] [--encoding socks|uot] [--family v4|v6] [--shape product|one]`. It
performs **no read until every flight segment has been written** — that is the property under test,
not a convenience.

```python
#!/usr/bin/env python3
"""R7 probe for sing-box UoT v2 connect mode (task 10-06-uot-per-flow-transport).

Writes the whole flight before reading a byte, then prints a timestamped hex transcript:
two encodings of the v2 request destination (--encoding), both IP families, both flight shapes.
"""

import argparse
import socket
import struct
import sys
import time

MAGIC = b"sp.v2.udp-over-tcp.arpa"

# The v2 request destination's ATYP. "socks" is the ordinary RFC 1928 set the pinned server reads
# (Sing common/metadata/socksaddr.go: 0x01 IPv4, 0x04 IPv6, 0x03 FQDN), and it is what the corrected
# UotCodec writes (see the resolution note in §2). "uot" is the v1/non-connect stream-format set
# (Sing common/uot/protocol.go AddrParser: 0x00/0x01/0x02) that the pre-fix client wrote into the
# request header; cell 3 keeps it as the counterfactual §2 pins.
ENCODINGS = {"socks": {4: 0x01, 6: 0x04}, "uot": {4: 0x00, 6: 0x01}}


def show(direction, what, data, t0):
    print(f"T+{time.monotonic() - t0:7.3f}  {direction}  {what:<24} {data.hex(' ')}", flush=True)


def read_exact(sock, count, what, t0, timeout):
    sock.settimeout(timeout)
    buf = b""
    while len(buf) < count:
        try:
            chunk = sock.recv(count - len(buf))
        except socket.timeout:
            show("RX", what + " TIMEOUT", buf, t0)
            return None
        if not chunk:
            show("RX", what + " EOF", buf, t0)
            return None
        buf += chunk
    show("RX", what, buf, t0)
    return buf


def read_reply(sock, t0, timeout):
    """One SOCKS5 reply: VER REP RSV ATYP + bound address + port."""
    head = read_exact(sock, 4, "connect reply head", t0, timeout)
    if head is None:
        return None
    body_length = {0x01: 4, 0x04: 16, 0x03: None}.get(head[3])
    if body_length is None:  # domain-typed bound address: one length byte decides
        length = read_exact(sock, 1, "connect reply bound length", t0, timeout)
        if length is None:
            return None
        head += length
        body_length = length[0]
    body = read_exact(sock, body_length + 2, "connect reply body", t0, timeout)
    return None if body is None else head + body


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", required=True, help="host:port of the sing-box socks/mixed inbound")
    parser.add_argument("--target", required=True, help="host:port of the UDP echo destination")
    parser.add_argument("--username")
    parser.add_argument("--password")
    parser.add_argument("--encoding", choices=sorted(ENCODINGS), default="socks")
    parser.add_argument("--family", choices=("v4", "v6"), default="v4")
    parser.add_argument("--shape", choices=("product", "one"), default="product",
                        help="product: greeting[+auth] then CONNECT+header+frame; one: a single write")
    parser.add_argument("--datagram", default="r7-probe")
    parser.add_argument("--timeout", type=float, default=3.0)
    args = parser.parse_args()

    credentials = args.username is not None and args.password is not None
    server_host, server_port = args.server.rsplit(":", 1)
    target_host, target_port = args.target.rsplit(":", 1)
    target_port = int(target_port)
    family = socket.AF_INET if args.family == "v4" else socket.AF_INET6
    address = socket.getaddrinfo(target_host, target_port, family, socket.SOCK_DGRAM)[0][4][0]
    atyp = ENCODINGS[args.encoding][4 if args.family == "v4" else 6]

    greeting = bytes([5, 2, 0, 2]) if credentials else bytes([5, 1, 0])
    auth = b""
    if credentials:
        user, password = args.username.encode(), args.password.encode()
        auth = bytes([1, len(user)]) + user + bytes([len(password)]) + password
    connect = bytes([5, 1, 0, 3, len(MAGIC)]) + MAGIC + struct.pack(">H", 0)
    header = bytes([1, atyp]) + socket.inet_pton(family, address) + struct.pack(">H", target_port)
    payload = args.datagram.encode()
    frame = struct.pack(">H", len(payload)) + payload

    print(f"# probe -> {args.server}  target {address}:{target_port}  encoding={args.encoding} "
          f"family={args.family} shape={args.shape} credentials={credentials}", flush=True)
    sock = socket.create_connection((server_host, int(server_port)), timeout=args.timeout)
    t0 = time.monotonic()
    if args.shape == "one":
        flight = greeting + auth + connect + header + frame
        show("TX", "flight (one write)", flight, t0)
        sock.sendall(flight)
    else:
        show("TX", "greeting[+auth]", greeting + auth, t0)
        sock.sendall(greeting + auth)
        show("TX", "CONNECT+header+frame", connect + header + frame, t0)
        sock.sendall(connect + header + frame)

    method = read_exact(sock, 2, "method selection", t0, args.timeout)
    if method is None:
        print("VERDICT: the server closed before the method reply (see the box log).", flush=True)
        return
    if method[1] == 0x02 and credentials:
        read_exact(sock, 2, "credential reply", t0, args.timeout)
    reply = read_reply(sock, t0, args.timeout)
    if reply is None:
        print("VERDICT: no CONNECT reply — treated as a late-discovered rejection.", flush=True)
        return
    if reply[1] != 0x00:
        print(f"VERDICT: CONNECT refused, REP=0x{reply[1]:02x} — exactly the rejection the client "
              f"maps to SetupFailure + cooldown.", flush=True)
        return
    length = read_exact(sock, 2, "frame prefix", t0, args.timeout)
    if length is None:
        print("VERDICT: CONNECT accepted, no reply frame (the echo never answered?).", flush=True)
        return
    body = read_exact(sock, struct.unpack(">H", length)[0], "frame payload", t0, args.timeout)
    print(f"VERDICT: {'echo received: ' + repr(body) if body else 'short frame'}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
```

**The probe's own verification (this session, not the box's).** The script was smoke-tested from this
host against a loopback mock that implements the pinned reply sequence (method selection → `CONNECT`
reply → framed echo) and the pinned rejection shape: cell 1 printed the expected transcript and cell 3
printed `05 01 00 01 00 00 00 00 00 00`. That exercises the probe's plumbing only; its correctness
against the real build is the operator's run.

### 4.3 What to run, what to expect, and what each observation falsifies

Run each cell below against the same box and the same echo, from the same probe host. Record the
whole hex transcript plus the box's log lines for the connection.

| # | Command | Expected observation | What a different observation falsifies |
| --- | --- | --- | --- |
| 1 | `--encoding socks --family v4` | `05 00` (or `05 02` + `01 00` with credentials), then `05 00 00 01 <bnd> <port>`, then the framed echo. The box logs `inbound connection to sp.v2.udp-over-tcp.arpa`, then `inbound UoT connect connection to <echo ip>:<echo port>`. | If no echo arrives although all five segments were written first, the **pipelined flight is not tolerated** → design §3's fallback (await the method/auth replies at create) applies. If the method reply is `05 ff` or `05 02` without `users` on the inbound, the credential shapes do not correspond (fact (d) precondition). |
| 2 | `--encoding socks --family v6` | The same, with an IPv6 request destination and a `05 00 00 04 …` bound address. | A wrong destination in the box's log line falsifies the "standard SOCKS ATYP" half of §2 — it would instead support the pre-fix `UotCodec` mapping. |
| 3 | `--encoding uot --family v4` (the pre-fix encoding; kept as §2's counterfactual) | **Predicted:** `05 01 00 01 00 00 00 00 00 00`, then EOF; the box logs at error level `process connection from <src>: UoT read request: unknown address family: 0`. | If instead the echo arrives, §2 is wrong and the per-datagram encoding is interoperable. This cell is the on-box adjudication of §2 — run it before believing any of it. |
| 4 | `--encoding uot --family v6` | **Predicted:** the CONNECT "succeeds" and the box's log line names a *bogus* IPv4 destination assembled from the IPv6 address's leading bytes (`<byte0-3>:<(byte4<<8)|byte5>`), followed by misframed or missing echo. | If the log names the real IPv6 destination, §2's IPv6 branch is wrong. |
| 5 | `--shape one --encoding socks --family v4` | Identical to cell 1: sending the whole flight in one `sendall` changes nothing (the server reads from a buffer). | A difference between cells 1 and 5 would mean a byte-arrival dependency the source does not show. |
| 6 | `--encoding socks --family v4` against a config with a temporary `{"action":"reject","port":40000}` route rule | a failure `CONNECT` reply (`REP != 0`; generic failure `0x01` unless the rejection carries an errno), then close; the packet path logs `connection closed:` at debug. This is the **CONNECT-rejection path**: the client reads the failure status on the receive path and maps it to `SetupFailure` + cooldown. | A *success* reply under a reject rule would mean the rule did not apply to this packet connection. |
| 7 | `--encoding socks --family v4` with a wrong password on an inbound with `users` | `05 02`, then `01 01` (credential failure). The client's completion rejects it before the `CONNECT` reply. | A `05 00` first reply means the inbound has no `users`: the client must then be configured without credentials (fact (d)). |

**Log lines to look for** (all from the pins in §1; the box's `log.level` gates them):

| Line | Level | Source | What it proves |
| --- | --- | --- | --- |
| `inbound connection to sp.v2.udp-over-tcp.arpa` | info | `protocol/socks/inbound.go:92`/`:97` at pin A (`:91`/`:96` at pin B; user-prefixed when authenticated) | the socks handshake completed and the FQDN was parsed |
| `inbound UoT connect connection to <destination>` | info | `common/uot/router.go:66` | the UoT router intercepted **and parsed the request destination** — the value in this line *is* the ATYP verdict |
| `process connection from <source>: UoT read request: unknown address family: 0` | error | `common/uot/router.go:61` + `common/metadata/serializer.go:162` | the request header's ATYP was not the SOCKS set (cell 3) |
| `open packet connection to <destination> using outbound/<type>[<tag>]` | error | `route/conn.go:213` | the outbound refused the request destination |
| `connection closed: <error>` | debug | `route/route.go:223`/`:240` | the packet connection ended by rejection rather than by the client |

### 4.4 The product-side check to run in the same session

With the probe passing on cells 1/2 and the product's encoding corrected, run WinForward against the
same box with `udpOverTcp: true` on that server entry and confirm, from the debug events:

- `udp.session.created` carries `udpTransport=uot` beside `targetKind=socks5`;
- a flow served over UoT never emits `udp.response.foreign_source` (structurally impossible in
  connect mode, design §6);
- a completed one-shot flow retires at the 5 s class (the transport's own `IUdpExchangeCounters`).

On a build with the encoding *uncorrected* (the pre-fix revision; kept here because it is the
cheapest end-to-end demonstration of the two carried residuals in §5), this same run shows: every UoT
flow fails on the receive path as `SetupFailure`, the next
datagram traces `udp.setup.cooldown` (`UdpProxyCoordinator.Send.cs:66`), and **no** `udp.setup.failed`
event and **no** `udpSetupFailures` increment appear — because those live in the setup pipeline
(`UdpSessionSetup.cs:152-167`), not on the receive path (`UdpProxyCoordinator.cs:563-575`).

## 5. Residual list: what is still `[to confirm]`, and what the client does if the answer is negative

| # | Residual | Why it is open here | If it turns out negative |
| --- | --- | --- | --- |
| R1 | The operator's build really is pinned A (sing-box `testing`, sing `6f21f24`), and its socks/mixed inbound terminates UoT v2 | the running binary is not reachable from this host; a moved branch tip or a custom build would change the answer | nothing to fall back to at the client: v2 is identified by the magic FQDN, and a build without it fails the probe's cell 1 at the DNS/routing step. Re-pin and re-run §4 |
| R2 | The fully pipelined flight (greeting → `[auth]` → `CONNECT` → UoT header → first datagram) is accepted at runtime | verified from source (fact (d)) but not exercised; runtime behaviour is the box's | **design §3's fallback**: await the method/auth replies at create (reuse the existing synchronous `ConnectAsync`), keeping the `CONNECT` + UoT header + first-datagram pipeline. Every external behaviour and acceptance criterion is unchanged; ≈2 RTT return to the first datagram |
| R3 | The credential shapes correspond (`users` on the inbound ⇔ credentials in the WinForward server entry) | a configuration fact of the box, not of the source | fail-closed already: the client throws and the flow arms the cooldown (fact (d)'s precondition table). Fix the configuration; no code change |
| R4 | **The request-header ATYP conflict of §2 — fixed in the code (batch 6b); the on-box half stays open** — the shipped `UotCodec` now writes `0x01`/`0x04`, the set the pinned server reads, and both loopback fixtures decode with that mapping | the code half is closed on this tree (§2's Resolution table; both guard halves were checked by mutation); the probe's cells 1–3 are still what settles the runtime half on the box | not a fallback question: with the pre-fix encoding the mode was non-functional against the pinned build for IPv4 destinations (a permanent `SetupFailure` loop) and silently mis-addressed for IPv6. The remaining action is the probe: run §4's cells 1/2, and cell 3 as the counterfactual, and record the transcript |
| R5 | **Carried residual — the magic `CONNECT` is 30 bytes** | not a residual question in the end: `4 (VER/CMD/RSV/ATYP) + 1 (length) + 23 (FQDN) + 2 (port) = 30`, and the FQDN is 23 bytes (`sp.v2.udp-over-tcp.arpa`). Recorded here rather than fixed | n/a — confirmed by construction against the pinned server, which parses the FQDN form (`0x03`, length `23`) at `protocol/socks/handshake.go:222` |
| R6 | **Carried residual — a receive-path rejection arms the cooldown without `udpSetupFailures` or `udp.setup.failed`** | recorded as designed: the receive-path teardown classifies through `TeardownReasonFor` and removes the slot (`UdpProxyCoordinator.cs:563-575`), and only `RemoveSlotAsync`'s `SetupFailure` branch arms the cooldown (`UdpProxyCoordinator.cs:515-518`); the counter, the event and the warn live in `UdpSessionSetup.HandleSetupFailureAsync` (`UdpSessionSetup.cs:152-167`), which the receive path never calls | n/a — it is a deliberate documentation item. The flow still traces `udp.setup.cooldown` on its next datagram and re-establishes after 1 s. Only the setup/flush window counts and logs the failure, so a systematically rejecting server shows as cooldown churn, not as `udpSetupFailures` growth — read the cooldown trace, not the counter, when diagnosing a UoT rejection |
| R7 | The UoT path's reply frames are what the client's `ReadConnectReplyCoreAsync` expects when the box's route *defers* the reply (a slow outbound) | source shows the reply is written immediately before the first frame, whatever the delay, but only a run proves it for the box's own outbound | if the server can emit a frame before the `CONNECT` reply, the client's strict read order would misparse. The probe's cell 1 with a deliberately delayed echo destination is the cheap check (point `--target` at an echo that sleeps before answering) |
| R8 | Whether the box's real traffic population is IPv4 or IPv6 destinations | the box's rules/adapters decide it; not a source fact | with the corrected encoding, neither family is special. With the *uncorrected* encoding, IPv6 is the worse failure (R4) |

## 6. What this memo does not cover

- **Sing-box beyond the socks/mixed inbound.** Other inbounds also wrap `uot.NewRouter`
  (`protocol/vless`, `vmess`, `shadowsocks`, … at pin A), but the client's design reaches the
  server through a SOCKS5 `CONNECT`, so only the socks and mixed inbounds are in scope.
- **UoT v1 / non-connect mode.** Out of scope by PRD; noted only to keep the version's two address
  encodings from being confused again (§2).
- **Anything measured.** This note contains no timing, no throughput, and no descriptor number; the
  mode's measured evidence is `benchmarks/results/2026-10-06-uot-per-flow/` and its internal
  validation limits are recorded there.
