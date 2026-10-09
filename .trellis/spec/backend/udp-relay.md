# UDP Relay Contracts

> How WinForward proxies UDP flows through a SOCKS5 relay or a local UDP endpoint: what one
> datagram's path costs, which anomalies are skips rather than failures, and the invariants every
> document in this family obeys. Read it before touching anything UDP-proxy shaped, then open the
> child that owns the rule you need. NDISAPI transport basics are in
> [windows-ndisapi.md](./windows-ndisapi.md); the TCP counterpart is
> [hot-path.md](./hot-path.md).

---

## Scope

The family owns the whole UDP proxy path: capture-side handoff, the per-flow transport (SOCKS5
relay, local endpoint, or the opt-in UoT stream), flow setup and its memory bounds, session
lifetime and retention, relay response reinjection, and association ownership.

Touch points: `src/WinForward.Runtime/UdpProxy/**`, `src/WinForward.Runtime/Socks5/Socks5Udp*.cs`,
the UDP halves of `src/WinForward.Cli/UdpProxyComposer.cs` /
`src/WinForward.Cli/DurableCaptureBundle.cs`, `UdpFrameBuilder` / `Socks5UdpCodec` / `UotCodec` in
`src/WinForward.Protocols/`, and the UDP leg of `IdleExpirySweeper`.

---

## The datagram path

One proxy-decided UDP datagram, in order:

1. `NdisPacketActionExecutor` parses the frame with `IPUdpPacket.TryParseSpan`
   (`src/WinForward.Protocols/IPUdpPacket.cs`), reads the Ethernet source MAC from bytes 6..11 of
   **each** proxied frame, and hands the payload span to `UdpProxyCoordinator.TrySendSpanAsync`
   (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Send.cs`). The original frame is
   **consumed, never reinjected** — the transport owns forwarding.
2. The send is admitted on one of two shapes. A cache-resident **ready** session
   (`_sessionCache`, direct-mapped, validated by the attached session's own flow key) is sent
   inline with **zero coordinator-gate entries and zero clock reads**; a cache collision, an
   unattached slot or a stale entry falls back to the gated admission path, which probes the setup
   cooldown and may create a slot.
3. The transport consumes the span synchronously — SOCKS5/UoT encode into its reusable send buffer
   before any await — so the native capture buffer may recycle when the entry returns. Only the
   contended-gate send shape copies (cold path).
4. A relay reply re-enters through the session's receive loop, is validated as **skip or datagram**,
   and is rebuilt into an Ethernet frame and injected toward the right adapter. See
   [udp-response-reinjection.md](./udp-response-reinjection.md).

**Accepting a reply.** Relay-source validation is port + address family, not exact `IPEndPoint`:
`Socks5UdpTransport.IsAcceptableRelaySource` accepts a datagram whose source port equals the relay
port and whose family matches the relay, even from a different IP — RFC 1928 does not pin the reply
source, and a multi-homed or anycast relay may answer from another address. IPv6 scope is
deliberately not compared (the receive interface's scope legitimately differs from the relay's
advertised one). A **local** target is the opposite case and validates exactly:
`LocalUdpTransport.IsAcceptableLocalSource` requires the configured endpoint's address bits, port
and family, because that endpoint is one specific host the configuration chose. Anything else is
the `UnexpectedSource` skip.

**Skip classes.** `UdpTransportSkipReason` (`UdpProxy/UdpTransportContracts.cs`) names every
per-datagram anomaly — `UnexpectedSource`, `Oversized`, `Malformed`, `ConnectionReset` — and a skip
ends nothing: the loop keeps receiving and the shape is counted in the session's rate-limited 5 s
summary (`connectionReset=` counts there too). A domain-typed relay response
(`DestinationAddress == null`) has the same posture, counted as `domainDestination=`.
**The deliverable payload ceiling is `cap − 42`** (1472 B at the pinned 1514
`UdpFrameBuilder.DefaultMaximumEthernetFrame`): the session's receive window is `cap + 22 + 1`, so a
larger reply skips as `Oversized` rather than being truncated. A jumbo-capable ABI lifts the
ceiling end to end, because the send buffer follows the same cap.
**Only socket-level faults are fatal**; `ConnectionReset` is classified as a skip at both the
transport (`UdpTransportReceiveClassifier.ClassifyFault`) and the session loop, because on Windows
an ICMP port-unreachable answering one of the flow's own sends surfaces that way.

**Loop prevention.** The flow's own `(Udp, local relay endpoint, relay endpoint)` tuple is
registered in `SelfTrafficRegistry` **before the first datagram** and released on dispose, so a
catch-all proxy rule never re-captures WinForward's own relay traffic. The registry must
wildcard-match a socket bound to Any/IPv6Any by port + remote: an outgoing datagram's source IP is
chosen by routing, not by the bind address (the relay socket binds `0.0.0.0`). Exact tuple matching
silently misses it and the relay traffic recurses. A UDP **reverse** datagram (server → client, the
reinjected relay response) is detected as the reverse of a stored key on a proxied flow and passed,
not re-proxied; without that, the response loops forever creating new associations.

---

## Invariants that hold across the family

- **One flow, one carriage, owned end to end.** A flow's association, relay socket, local-endpoint
  socket, or UoT stream is dialed for that flow alone and disposed with it; nothing is pooled,
  leased, refcounted or re-associated. See
  [udp-association-ownership.md](./udp-association-ownership.md) and
  [udp-over-tcp.md](./udp-over-tcp.md).
- **Fail-closed, never a silent downgrade.** A response that cannot be delivered to the right
  adapter, a datagram that cannot be framed, a setup that fails — each is dropped or refused and
  counted, never sent out a path it does not belong to.
- **Counters never decide disposition.** `RuntimeCounters` values (including
  `udpResponseSourceMismatch`) are observations; no counter influences packet disposition,
  fail-closed, relay or shutdown decisions.
- **The warm path stays warm.** The established datagram shape does not allocate managed memory,
  take the coordinator gate, or read a clock; the allocation gates
  (`HotPathAllocationGateTests`, `SweepAllocationGateTests`,
  `UdpAdaptiveSweepAllocationGateTests`) pin it.
- **A threshold, default or allowlist names its owning type.** Numbers transcribed here are for
  orientation; the constant is authoritative.

---

## Topic map

| Child | Owns | Read it when |
|---|---|---|
| [udp-relay-transport.md](./udp-relay-transport.md) | The SOCKS5 UDP relay transport: ASSOCIATE across families, relay socket setup, endpoint and buffer sizing | You change `IUdpProxyTransport`, the send/decode shape, or the frame-cap-derived buffers |
| [udp-flow-setup.md](./udp-flow-setup.md) | What happens between a flow's first datagram and a live session: the 8-wide dial limiter, the bounded setup queue, the 8 MiB global budget, the 5 s TTL, the 1 s cooldown | You touch setup admission, queue budgeting, or the setup cooldown |
| [udp-session-lifecycle.md](./udp-session-lifecycle.md) | One flow's session: state vocabulary, scope-owned lifetime, teardown reasons, fail-closed send drop, receive-failure signal, two-class retention and the sweep cadence | You touch session admission, disposal, expiry, or retention |
| [udp-association-ownership.md](./udp-association-ownership.md) | One flow, one authenticated association: what the dial produced, the watchdog, disposal order, exchange evidence | You touch `Socks5UdpAssociation`, the relay-socket contract, or association death |
| [udp-response-reinjection.md](./udp-response-reinjection.md) | How a reply is validated, owned, rebuilt and injected toward the right adapter — or dropped fail-closed; the foreign-source counter | You touch response rebuilding, adapter targeting, client-MAC handling, or reply observability |
| [udp-over-tcp.md](./udp-over-tcp.md) | The opt-in UoT v2 connect-mode carriage: framing, pipelined establishment, typed faults, descriptor budget, synthesized reply source | You touch `Socks5UotTransport`, `UotCodec`, or the `UdpOverTcp` branch |

---

## Where things moved

This document was one file until 2026-10-09. Dated evidence under `benchmarks/results/**` cites its
section numbers and must never be rewritten, so every old section is mapped here.

| Old section (this file, 2026-10-09 line range) | Now lives in |
|---|---|
| "UDP relay wiring" (L7-41) | [udp-response-reinjection.md](./udp-response-reinjection.md) (reinjection, client MAC, host-flow adapter binding); [udp-association-ownership.md](./udp-association-ownership.md) (the per-flow factory and its self-traffic tuple); [udp-flow-setup.md](./udp-flow-setup.md) (setup admission and queueing); this hub (the datagram path, skip classes, self-traffic and reverse-datagram rules) |
| "UDP relay hardware findings (Win11, 2026-08-09)" (L44-48) | This hub — the reverse-datagram pass-through and the self-traffic wildcard rule. The local-SOCKS5-server re-capture was a test-harness artifact, not a relay contract, and is deleted |
| "Cross-family SOCKS5 UDP relay setup" §1-§7 (L52-147) | [udp-relay-transport.md](./udp-relay-transport.md) |
| "Bounded UDP setup memory: global budget + datagram TTL + cooldown bound" §1-§6 (L151-265) | [udp-flow-setup.md](./udp-flow-setup.md) |
| "UDP endpoint zero-allocation + frame-cap buffer sizing" §1-§6 (L268-322) | [udp-relay-transport.md](./udp-relay-transport.md), including its xUnit-inference note — that gotcha is bound to this migration, so it stays beside it rather than in `test-stability.md` |
| "UDP session lifetime, teardown reason, and fail-closed send drop" (2026-09-20) §1-§7 (L326-431) | **Deleted** — superseded 2026-09-21; the surviving rules are in [udp-session-lifecycle.md](./udp-session-lifecycle.md) |
| "Scope-owned UDP lifetime and the receive-failure signal/join split" (2026-09-21) **§3 / §4** (L434-554) | [udp-session-lifecycle.md](./udp-session-lifecycle.md) — its "Contracts" and "Validation & Error Matrix". This is where a frozen `udp-relay.md §3/§4` citation (e.g. `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md:201`, the no-`_activityGate` send path and the zero-gate ready hit) resolves |
| "UDP association ownership: one flow, one authenticated association" §1-§7 (L557-648) | [udp-association-ownership.md](./udp-association-ownership.md) |
| "UDP session retention and the relay receive buffer (two-class retention)" §1-§3 (L651-687) | [udp-session-lifecycle.md](./udp-session-lifecycle.md) |
| "Reply-ownership observability: the foreign-source counter" §1-§6 (L690-808) | [udp-response-reinjection.md](./udp-response-reinjection.md) |
| "UDP over TCP per flow: one flow, one stream connection" §1-§7 (L811-1029) | [udp-over-tcp.md](./udp-over-tcp.md) |

A frozen citation that names only "the host-flow response adapter binding" (e.g.
`benchmarks/results/2026-09-30-flow-key-parse-once/README.md:180`) resolves to that heading in
[udp-response-reinjection.md](./udp-response-reinjection.md).
