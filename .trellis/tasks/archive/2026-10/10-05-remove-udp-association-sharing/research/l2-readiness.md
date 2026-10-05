# L2 readiness — what the UoT v2 / VLESS transport will need

Written for the task that implements the L2 UDP transport, not for
`10-05-remove-udp-association-sharing`. It maps the seam the transport plugs into, the obligations it
must meet, the two candidate protocols and their recorded cost shapes, the configuration-shape
options for a third target kind, the server-side surface, and the questions the L2 task must answer
itself. It constrains nothing: every choice below is the L2 task's to make.

**How to read this note.** Two markers are used, and they are the important part:

- **[recorded]** — the statement is sourced from an artifact in this repository, cited inline. It can
  be relied on as the state of *this tree*.
- **[to confirm]** — the statement is *not* established by anything in this repository (typically a
  protocol or server behaviour). It is written as a question the L2 task must answer against the
  server implementation and its documentation before designing around it. Nothing in this note
  asserts protocol internals on its own authority.

## 1. The seam

**[recorded]** The transport contract was made transport-neutral by R3
(`10-05-local-dns-transport`), and a new transport kind is a new union member, a new factory, and one
dispatch branch:

| Piece | Anchor |
| --- | --- |
| `IUdpProxyTransport` — `PeerEndpoint`, `LocalEndpoint`, `SendSpanAsync`, `ReceiveAsync` | `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs:92` |
| `IUdpProxyTransportFactory` — `ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget, CancellationToken)` | `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs:122` |
| `UdpTransportFactory` composite — dispatch on `{ Local: not null }` / `{ Socks5: not null }`, refusing an unshaped target | `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs:135` |
| `ProxyTarget` — `(Name, Socks5Server?, LocalTarget?)`, `IsLocal`, `FromServer` | `src/WinForward.Configuration/ConfigurationModels.cs:103` |
| Composition — the composite is built once with the SOCKS5 and local factories | `src/WinForward.Cli/UdpProxyComposer.cs:69` |
| Session setup — the coordinator calls `transportFactory.CreateAsync(target, ct)` and never sees the kind | `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs:101` |
| Lifecycle log — `target=<name>` and `targetKind=local|socks5` | `src/WinForward.Runtime/UdpProxy/UdpProxyLogging.cs:25` |

**[recorded]** R3 is the precedent for adding a kind: the local target arrived as one more
`ProxyTarget` member, one more factory, and one more dispatch branch, and the session, the
coordinator, the alias table, and the reinjector were not touched (`.trellis/spec/backend/udp-relay.md`,
"UDP association ownership"). The L2 task should expect the same shape — with the exceptions listed
in §2, which are the parts of the seam that currently assume a socket per flow.

**[recorded]** What the pipeline does with the transport, so the obligations are concrete:

1. `UdpSessionSetup` creates the transport, then claims a `RelayAlias` from
   `(transport.LocalEndpoint, transport.PeerEndpoint, TransportProtocol.Udp, flow.Origin)`
   (`UdpSessionSetup.cs:102-110`; uniqueness enforced in `UdpAssociations.cs:60-103`).
2. The session owns the transport for the flow's life and calls `SendSpanAsync` on the flow's
   datagrams and `ReceiveAsync` in its receive loop.
3. The receive loop re-injects a decoded datagram toward the client with its declared source; the
   reply-ownership counter compares that source with `Flow.Remote`
   (`UdpProxySession.cs:401`, counter `RuntimeCounters.UdpResponseSourceMismatch`).
4. Teardown disposes the transport; the coordinator releases the alias.

## 2. What an L2 transport must implement

### 2.1 The transport contract itself

**[recorded]** One transport instance per flow; `PeerEndpoint` and `LocalEndpoint` are documented as
fixed for the transport's life; `SendSpanAsync` must consume its span synchronously (the caller's
native capture buffer recycles when the call returns — only the contended-gate shape may copy and
await); `ReceiveAsync` returns one decoded datagram or one `UdpTransportSkipReason`
(`UdpTransportContracts.cs:92-115`, `:14-34`).

### 2.2 Per-flow framing — above all

**[recorded]** The reason L2 can carry many flows over one server connection at all is framing: a
per-flow identity on the wire (a session/stream id) so a reply is demultiplexed by **identity**
rather than by the socket it arrived on (design §7; parent PRD R5). This is the property the removed
SOCKS5 sharing lacked, and it is the top obligation:

- The transport must be able to tell which frames on the shared connection belong to its flow, and
  deliver only those from `ReceiveAsync`.
- Frames belonging to other flows on the same connection are consumed (and, if the transport's
  receive loop is the connection's only reader, routed to their sessions) — they are not
  `UdpTransportSkipReason` anomalies of this flow.
- If the L2 task instead chooses one connection per flow or per destination (§3), framing still has
  to carry the flow's destination and the reply's source; what changes is only how much identity the
  demultiplexing needs.

**[to confirm]** Whether the candidate protocols really provide a per-flow identity that survives a
shared connection, and what its lifetime and scope are — see §5.

### 2.3 The two endpoints, and the alias claim that keys on them

**[recorded]** The alias uniqueness guard is live and endpoint-keyed: `RelayAlias` is
`(LocalEndpoint, PeerEndpoint, Udp, origin)`, and a second flow resolving to the same triple is
rejected fail-closed ("UDP relay alias collision with another flow", `UdpSessionSetup.cs:107-110`;
the guard is `UdpAssociations.cs:72-87`). Two flows through one shared TCP connection would report
the same local socket endpoint and the same server endpoint, so a naive shared-connection transport
collides with itself on the second flow.

**[to decide]** How the L2 transport supplies a *per-flow* `LocalEndpoint`/`PeerEndpoint` pair — or
whether the seam and the alias claim need a per-flow discriminator (a session id) alongside the
endpoints. This is the one part of §1's "no pipeline changes" expectation that the shared-connection
shape may break, and it should be settled in the L2 design, not discovered during implementation.
Note that `TryFindRelay` currently has no production caller (the reverse-classification half is
test-only); the live half is the uniqueness claim, so a seam change here is smaller than it looks.
Anchors: `UdpAssociations.cs:25-49`, `:105-107`.

### 2.4 `IUdpExchangeCounters`

**[recorded]** The retention class reads the transport's own counters through the internal
`IUdpExchangeCounters` (`UdpTransportContracts.cs:168`): `DatagramsSent` (incremented after the
kernel/connection accepted the send) and `SawResponse` (set by the first successfully decoded
response; skips do not count). A transport that does not implement it is classified *sustained*
(`UdpTransportContracts.cs:160-167`), which loses the completed-one-shot 5 s retirement class
(`UdpProxyCoordinator.OneShotIdleTimeout`; `.trellis/spec/backend/udp-relay.md`, "two-class
retention"). Implementing it from the L2 transport's own send/response observations is required for
the DNS-shaped population to keep the retention it has today.

### 2.5 Self-traffic registration before the first datagram

**[recorded]** The transport registers its own traffic tuple in `SelfTrafficRegistry` before the
first datagram, so catch-all proxy rules never re-intercept the proxy's own connection
(`SelfTrafficRegistry.cs:7`; SOCKS5 control tuple registered before the SYN,
`Socks5UdpAssociation.cs:150-157`; relay tuple `Socks5UdpTransport.cs:224-227`; local tuple
`LocalUdpTransport.cs:197-200`). The registry is the only built-in loop-prevention safeguard
(README, "No implicit rules").

**[to decide]** For a shared connection the registration is **per connection, not per flow** — one
TCP (or protocol) tuple that must be registered before it is dialed and released when the connection
dies, not when an individual flow ends. The lifecycle of that registration is a design decision the
L2 task owns.

### 2.6 Fault classification: skip-class vs fatal

**[recorded]** The shared vocabulary is `UdpTransportReceiveClassifier.ClassifyFault`
(`UdpTransportContracts.cs:151-158`): a `SocketError.ConnectionReset` is skip-class
(`UdpTransportSkipReason.ConnectionReset`, S2 — an ICMP port-unreachable answering a send must not
end a session's receive loop), every other socket fault is fatal and tears the session down. The
per-datagram anomalies (`UnexpectedSource`, `Oversized`, `Malformed`) are results, not exceptions.

**[to decide]** For a stream-based L2 transport, a read fault on the shared connection is a
*connection* fault, not a per-datagram fault: it kills every flow on that connection. Mapping a
connection death to each affected flow's failure — and choosing whether the existing
`UdpAssociationLostException` / `UdpTeardownReason.AssociationLost` path (no setup cooldown, flow
re-establishes on its next datagram; `.trellis/spec/backend/udp-relay.md`, R5) is the right
vocabulary or whether L2 needs its own — is an L2 decision. The same question applies to the
`ConnectionReset` rule: over a stream, a reset is connection-scoped, not datagram-scoped.

### 2.7 Receive window and payload ceiling

**[recorded]** The coordinator owns one receive-window size for every transport:
`ReceiveWindowSize = maximumFrameSize + MaximumSocks5UdpHeaderSize + OversizeSentinelSize`
(`UdpProxyCoordinator.cs:209-215`), and the deliverable payload ceiling is `cap - 42` at the pinned
1514 ABI; a datagram that fills the window is reported `Oversized`
(`Socks5UdpTransport.cs:399-407`). The local transport reuses the window with a smaller frame.

**[to confirm / to decide]** An L2 transport whose per-datagram framing is larger than the SOCKS5 UDP
header needs either a per-kind receive-window size or proof that the framing fits the existing
`+22` allowance; and the oversized rule must keep meaning what it says. This is a seam constant, so
changing it is a composition decision.

### 2.8 Ownership, disposal, and lifecycle

**[recorded]** The transport is an owner: it disposes in a fixed order (socket/connection →
self-traffic token → association/connection state → send gate) and continues through later releases
when an earlier one throws (`Socks5UdpTransport.DisposeAsync`, `async-lifetime.md` ownership
section). Setup failures release whatever was acquired (`Socks5UdpTransportFactory.CreateAsync`
catch path). A shared connection must define what happens when one flow's transport is disposed
while siblings keep using it — the connection outlives the flow, so its owner is not the flow
transport.

### 2.9 Small observability obligations

**[recorded]** `targetKind` is derived by `UdpProxyLogging.LocalOrSocks5` as `IsLocal ? "local" :
"socks5"` (`UdpProxyLogging.cs:29-33`), so a third kind needs a third token (and `IsLocal` must not
silently become the union's only discriminator). The `udpAssociation=<generation>` log field comes
from the alias table, so it is free.

**[to decide]** Which counters the L2 path needs beyond the existing ones (the local target carries
`udpLocalTargetFlows` / `udpLocalTargetFailures`; the SOCKS5 path counts setup failures and
association-lost). Any new counter belongs in `RuntimeCounters` with the same
"never influences disposition" contract.

## 3. The two candidate protocols and what each costs

Both candidates are named by the parent PRD R5 (`10-05-udp-association-sharing-correctness/prd.md`:
"UoT v2 connect, or VLESS + Mux.Cool + XUDP") and priced in shape by this task's design §7. That is
the extent of what this repository records about them; every mechanism below is marked accordingly.

| | UoT v2 (**[recorded]** shape) | VLESS + XUDP (**[recorded]** shape) |
| --- | --- | --- |
| Recorded cost statement (design §7) | "a `CONNECT` per destination with length-prefixed datagrams over TCP — per-flow correctness, TCP head-of-line coupling, one connection per destination" | "per-flow session ids over one muxed connection — correctness and sharing, at the cost of a mux implementation and a protocol-specific framing" |
| Per-flow identity | the connection itself (one destination per `CONNECT`) | the XUDP session id |
| Sharing many flows | only flows that share a destination share the connection | many flows over one muxed connection (the thing the removed mechanism was for) |
| New machinery | length-prefixed framing over TCP | a mux client plus XUDP framing |
| Head-of-line exposure | per destination: one stalled destination's connection delays only its own flows | shared: one stalled stream can delay the others — the risk the 09-06 PRD already named ("head-of-line blocking *inside* the mux") |

**[to confirm]** For UoT v2: the exact framing (is the length prefix the whole story? are there
control/keepalive frames?), whether `CONNECT` carries the destination and how the reply's declared
source is expressed, whether the connection is torn down with the flow or pooled, what authentication
the server requires, and whether the server's UDP-over-TCP path delivers replies per flow or per
connection.

**[to confirm]** For VLESS + XUDP: what an XUDP session id is and how it is allocated and retired,
whether one muxed connection really can carry many UDP flows with reply demultiplexing, which mux
(Mux.Cool per the parent PRD, or sing-box's own), stream caps and flow-control behaviour, how a
destination and a reply source are encoded per datagram, and what happens to in-flight flows when
one mux stream or the whole connection fails.

**[to confirm]** For both: whether the client-side implementation fits this repository's constraints
(managed, native-AOT-published Windows executable, no new native dependency), and what the
credentials look like (VLESS UUID / UoT auth) so the configuration shape in §4 can be designed.

## 4. Configuration shape for a third target kind

**[recorded]** Today's shape (R3's precedent: "the kind lives in the declaration, and the rule field
keeps naming a target"):

- Two sibling top-level lists, `socks5Servers` and `localTargets`
  (`ConfigurationModels.cs:13`, `:16`), each with its own DTO (`Socks5ServerDto` `:46`,
  `LocalTargetDto` `:55`) and its own validator (`ConfigurationTargets.Validate`, `:21`).
- Both are parsed into **one name-keyed `ProxyTarget` table**; names share one namespace
  (case-insensitive), and a duplicate in the second list is diagnosed against that list
  (`ConfigurationTargets.cs:17-25`, `:94`).
- A rule's `target` resolves by name (`ConfigurationRules.cs:77`), and a rule naming a
  local-kind target must select exactly `udp` (`ConfigurationRules.cs:88-92`).

Two viable shapes, both keeping `ProxyTarget` as the union:

| Option | Shape | Cost |
| --- | --- | --- |
| **A — sibling list** (follows R3) | a third list (e.g. `l2Targets` / `muxServers`) with its own DTO, validator, and `ProxyTarget` member; rules resolve names as today | another top-level key, another participant in the one namespace, and a third `IsLocal`-style predicate; nothing existing changes |
| **B — `type` discriminator** | one target list where each entry carries `type: "socks5" \| "local" \| "l2"`; per-kind fields are validated against the declared type | a schema decision (which fields are legal per type) and a rework of the two existing lists; names and rule resolution stay as they are |

**[recorded]** Neither shape needs a migration surface: the product has never been released, so a
removed or newly introduced key is just a schema change and an old configuration fails closed at
parse through the loader's unknown-property rule (design §3 for this task; the same reasoning
rejected R3's legacy `proxyServer` spelling).

**[to decide]** Whichever shape wins: whether an L2 target carries credentials (and of what kind),
whether it exposes protocol knobs (mux on/off, packet encoding) or hardcodes one shape, whether the
UDP-only rule guard (`ConfigurationRules.cs:88-92`) covers it, and what the `targetKind` token is.
The L2 target should also collect the validation warnings its fields imply (the local target's
non-loopback warning in `ConfigurationTargets.WarnOnNonLoopbackLocalTarget` is the precedent).

## 5. Server-side support surface

**[recorded] The 09-06 premise.** `09-06-local-mux-transport` R1 records the server-side fact this
repository has: the target ecosystem is **sing-box VLESS inbound + multiplex**, there is **no UDS
inbound** (issue #733 closed), and the task's stated purpose is to confirm that ecosystem supports
the client-side shape and to document protocol/limits — stream caps, and "UDP-over-mux semantics —
does UDP still need per-flow ASSOCIATE or can relays share a pooled control connection?"
(`.trellis/tasks/09-06-local-mux-transport/prd.md` R1). That task is still at PRD stage with **no
research artifacts** in this repository, so its premise is a planning claim, not a verified finding.
Its mux was also premised on a *local mux to the SOCKS5 server*, not on UDP framing.

**[to confirm] The expected surface named by this task's design §7** — sing-box's `udp_over_tcp`
(v2) and VLESS `packet_encoding: xudp` + `multiplex` — is recorded nowhere else in this repository.
The L2 task must confirm, against the exact server build in use and its own documentation:

1. Which inbounds support which candidate at all, and at what version (UoT v2 in a `socks`/`mixed`-
   style inbound? XUDP in a VLESS inbound?).
2. Whether UDP-over-mux is supported, and whether one muxed connection can carry many UDP flows with
   per-flow reply demultiplexing (the property §2.2 depends on).
3. What the server's write path does with replies under the chosen encoding — specifically, whether
   it keeps the SOCKS5 path's "one peer address per association, refreshed on every read" behaviour
   (the field measurement in
   `.trellis/tasks/10-05-udp-association-sharing-correctness/prd.md` Background), or whether XUDP's
   session identity replaces it. If the same last-peer write path survives under a different name,
   the framing must be relied on for correctness, not assumed.
4. Stream caps, flow-control behaviour, keepalives, and what a stalled stream does to its siblings
   (the head-of-line question in §6.1).
5. The authentication and credential surface per candidate.
6. Whether the candidate interoperates with the SOCKS5 UDP path this product already ships, so an
   operator can mix kinds across rules.

**[recorded] Version pinning.** The SOCKS5 reply-path experiments ran on sing-box 1.14.1
(sing v0.9.4), while the operator's box runs the testing channel
(sing v0.9.7-0.20260929150544); the SOCKS5 reply path was line-for-line unchanged between them, but
the result was not reproduced on the exact build in use, and the parent task recorded that its child
"should confirm it" (`10-05-udp-association-sharing-correctness/prd.md`, Notes). The L2 task should
pin the exact build it targets and re-confirm anything it relies on.

## 6. Open questions the L2 task must answer

The four the design names first, then the ones this read of the tree adds.

1. **Head-of-line cost on the local leg.** A stalled or lost mux stream delays its siblings; measure
   it on loopback and, where feasible, the Windows guest — the 09-06 PRD already names "head-of-line
   blocking *inside* the mux" as the new risk.
2. **Framing overhead per datagram** (protocol headers, length prefixes, session-id fields) and its
   effect on the deliverable payload ceiling (`cap - 42` today) and on the receive-window constant
   (§2.7).
3. **One muxed connection or one connection per destination.** UoT v2's per-destination `CONNECT`
   versus VLESS + XUDP's muxed sessions is the cost-model decision; the answer determines whether the
   product keeps one-connection-per-flow's descriptor shape or returns to a shared-connection shape
   (with per-flow identity, this time).
4. **How the 09-06 mux research is repurposed.** Its R1 verification, R2 loopback measurement, R3
   elimination estimates, and R4 rollout shape are reusable; its premise — a local mux carrying
   per-flow SOCKS5 `UDP ASSOCIATE`s — is superseded, because per-flow correctness now needs per-flow
   framing rather than per-flow ASSOCIATE. The parent PRD also records that the seam question belongs
   to whichever task lands first, and R3 landed: L2 plugs into the existing composite
   (`10-05-udp-association-sharing-correctness/prd.md`, Notes; design §7).
5. **The alias claim over a shared connection** (§2.3): per-flow endpoint pair, or a session-id
   discriminator in the alias key?
6. **What a shared connection's death means per flow** (§2.6): reuse the `AssociationLost` path, or
   introduce an L2-specific teardown reason? What does a `ConnectionReset` on a stream mean for the
   datagram-level skip vocabulary?
7. **The self-traffic registration's lifetime when it is per connection, not per flow** (§2.5), and
   who owns the connection when the first flow using it is disposed.
8. **What the reply-ownership counter should mean at the L2 hop.** Today it compares a reply's
   declared source with the receiving flow's destination. With per-flow framing the "wrong flow"
   case is impossible by identity; the remaining observation is a server answering from an unexpected
   endpoint. Keep the same counter and wording, or define an L2-specific one?
9. **Credentials and the configuration shape** (§4): which fields an L2 target needs, and whether the
   sibling-list or the `type`-discriminator option carries them better.
10. **What the harness needs** to record an L2 column (a new `--target` value? a new server fixture?),
    and which of the surviving churn/burst/session-budget columns it can reuse.
11. **AOT and dependency constraints**: whether the candidate's client can be implemented in the
    managed, native-AOT-published client without new native dependencies.

## 7. What the L2 task should not have to touch

**[recorded]** If §2.2 (identity-based demultiplexing), §2.4 (exchange counters), §2.5 (self-traffic),
and §2.3 (the alias key) are satisfied inside the transport, then the session, the coordinator's
send/admission path, the response reinjector, the two-class retention, the expiry sweep, and the
capacity accounting all stay as they are — that is the point of the R3 seam
(`.trellis/spec/backend/udp-relay.md`, "UDP association ownership", and the local-target section).
The exceptions are exactly the seam constants and guards named above: the receive-window size, the
`targetKind` token, the UDP-only rule guard, and the alias claim.
