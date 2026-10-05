# Local DNS transport for hijacked UDP/53 flows

Child of `10-05-udp-association-sharing-correctness` (R3, policy level `L0`). Sibling children
`10-05-harness-response-ownership` (R1) and `10-05-reply-ownership-observability` (R2) are archived;
the sharing-policy child (R4/R5) is not created yet.

## Goal

Serve proxy-decided UDP/53 flows from a local transport bound for the local `dns-in` endpoint
instead of a SOCKS5 UDP association, presenting the flow's original destination as the reply source.

Three reasons, in the order the evidence supports them:

1. **Semantics.** A DNS flow sharing an association with a non-DNS flow inherits that flow's route
   decision (first-datagram routing, sniffing included) and, whenever it is not the last sender, its
   replies are written to a sibling (`sing` `protocol/socks/packet.go:92-100`). Hijacked DNS is the
   one flow class where a misdelivered reply is both a correctness failure and a security-relevant
   one (a resolver answer delivered to the wrong client, or a query answered from an unintended
   upstream).
2. **Latency.** Every hijacked query pays a control connect plus `UDP ASSOCIATE` (2.46 ms + 4.49 ms
   measured) against a 13.1 µs relay socket. A loopback send to a local DNS endpoint removes that
   cost entirely.
3. **Population.** Moving DNS off the SOCKS5 path removes exactly the traffic class that makes
   sharing unsafe, and leaves the remaining shared population (QUIC/WireGuard-like long flows) to
   the R4 policy decision with a clean traffic mix.

## Background — confirmed by inspection

- A proxy decision names a SOCKS5 server (`FlowDecision.ProxyServerName`,
  `src/WinForward.Core/Domain.cs:300`). `FlowDispatcher` resolves that name through
  `IReadOnlyDictionary<string, Socks5Server>` (`src/WinForward.Runtime/FlowDispatcher.cs:496`,
  `ValidatedConfiguration.Servers`, `src/WinForward.Configuration/ConfigurationModels.cs:93`) and
  hands the resolved `Socks5Server` to
  `NdisPacketActionExecutor.HandleUdpProxyAsync` (`.../Capture/NdisPacketActionExecutor.cs:429`) →
  `UdpProxyCoordinator.TrySendSpanAsync`.
- The transport seam is already per-flow. `UdpSessionSetup.CreateSessionAsync` calls
  `IUdpProxyTransportFactory.CreateAsync(Socks5Server, ct)` (`.../UdpProxy/UdpSessionSetup.cs:102`);
  `Socks5UdpTransportFactory.CreateAsync` (`.../Socks5/Socks5UdpTransport.cs:161`) rents an
  association lease from the per-server pool and wraps it in a transport that owns only its own
  relay socket, its self-traffic tuple, and the lease. The pooled object is the authenticated
  association, not the socket.
- Reply classification and reinjection are source-agnostic. `UdpProxySession.TryGetReceiveSource`
  (`.../UdpProxy/UdpProxySession.cs:403`) rebuilds the reply source from the decoded datagram and
  compares it to `Flow.Remote` (`:421`), counting mismatches through `RecordForeignSource` (`:491`);
  `InjectResponseAsync` (`:430`) passes that source to `IUdpResponseSink.InjectAsync`
  (`.../UdpProxy/UdpResponseReinjector.cs:16`), which rebuilds the frame with it. A transport that
  reports the flow's original destination as the reply source therefore needs no change downstream:
  the ownership check passes, and the reinjector spoofs the original destination for free.
- DNS is already the canonical one-shot flow: `OneShotDatagramThreshold = 1` plus a seen response
  selects `OneShotIdleTimeout` of 5 s (`.../UdpProxy/UdpProxySession.cs:64`, `:289`;
  `.../UdpProxy/UdpProxyCoordinator.cs:258`).
- Loop prevention is per-transport: `(Udp, local socket, relay endpoint)` is registered in
  `SelfTrafficRegistry` before the first datagram (`.../Socks5/Socks5UdpTransport.cs:309`), and the
  `SIO_UDP_CONNRESET` posture is applied before bind (`:519`). Source validation is port plus
  address family, never exact address (`.trellis/spec/backend/udp-relay.md`).
- Configuration today: `socks5Servers` entries (name/host/port/username/password) become
  `ValidatedConfiguration.Servers` (`ConfigurationModels.cs:221`, `:235`); rules reference them by
  name through `proxyServer` (`examples/process-proxy.json`). README's "No implicit rules" section
  states that WinForward adds no user-visible policy rules for DNS and that DNS policy must be
  explicit — a rule-referenced target keeps that property, an implicit port-53 rewrite does not.
- The parent PRD records that `09-06-local-mux-transport` R4 asks how a pooled/mux transport slots
  into `IUdpProxyTransportFactory` composition, and that whichever of the two lands first owns the
  seam. R3 lands first, so R3 owns the seam and must shape it so a second transport kind (and a
  multiplexed local transport) can slot in behind the same decision point.

## Requirements

- **R1 — Selection surface.** A local target is declared in `localTargets[]` (`name`, `host` as an IP
  literal, `port`) and selected by a rule's `target` field — the same field that selects a SOCKS5
  server, renamed from `proxyServer` with a diagnostic naming the replacement and applied everywhere
  the name is threaded (`RuleDto`, `FlowDecision`, `FlowDispatcher`, `FlowAttributionPipeline`,
  examples, README, tests, benchmarks). Names are unique across `localTargets` and `socks5Servers`
  (one namespace), while `socks5Servers` keeps its name and meaning: its entries serve TCP `CONNECT`
  and UDP `UDP ASSOCIATE` and carry credentials. A rule that could match TCP is rejected when it
  names a local target — including a rule with no `protocol` selector, which matches every protocol.
  The packet path carries no port-53 special case.
- **R2 — Transport behaviour.** A local transport sends the flow's payload verbatim to the
  configured endpoint (no parsing, no rewriting, no caching) and reports the reply to the session
  with the flow's original destination as its source, so `TryGetReceiveSource`,
  `RecordForeignSource`, and `UdpResponseReinjector` are unchanged. It registers its self-traffic
  tuple before the first datagram, applies the `SIO_UDP_CONNRESET` posture before bind, follows the
  `udpRelayReceiveBufferKb` receive-buffer budget, accepts replies only from the configured endpoint
  (address, port, and family), and emits a startup warning when that endpoint is not a loopback
  address (the payload leaves the host in the clear and the endpoint answers as the original
  destination).
- **R3 — No association, no sharing.** The transport never rents a lease, never enters the
  association pool or the capability sampler, and never shares one socket between flows: the local
  hop must not reproduce the reply-ownership defect this task exists to remove. The flow's session,
  slot, expiry, and teardown lifecycle is otherwise the existing one.
- **R4 — Fail-closed failure semantics.** An unreachable endpoint, a refused send, or an absent
  reply fails closed without a pass downgrade, consistent with
  `proxyUnavailableAction`/`processingFailureAction`; the failure is counted and rate-limited like
  the existing UDP failure events. A client retry inside the one-shot idle window reuses the
  session; after expiry it re-establishes.
- **R5 — Observability.** A counter and a structured event distinguish local-DNS-served flows (and
  their failures) from SOCKS5-relayed ones, so the R4 policy decision can price the DNS population
  instead of assuming it.
- **R6 — Documentation.** README gains the configuration key(s), the event, and an explicit
  statement of what the transport does with the client's original destination (it is discarded as
  routing input; the local endpoint's own DNS policy answers, and the destination is restored only
  as the reply's source address). At least one example config demonstrates the opt-in.

## Decisions

**Selection surface (2026-10-05).** A named local target is declared in its own configuration list
and selected by rules through the same field that selects a SOCKS5 server. The two rejected
alternatives were one global endpoint selected implicitly for whatever port the local service serves
(it turns a rule's declared target into a partial lie and gives up per-process/per-adapter
granularity) and a global endpoint plus a per-rule opt-out flag (two knobs for one decision).

**Vocabulary (2026-10-05).** The list is `localTargets`, the rule field is `target`. The word
"server" is not part of the concept, and neither is DNS: WinForward's packet path never learns what
protocol a flow carries, so a local target is an endpoint on this host that terminates a selected
flow and whose replies are attributed to the flow's original destination — DNS is the first user of
that mechanism, not its definition.

```json
{
  "socks5Servers": [{ "name": "remote", "host": "proxy.example.com", "port": 1080 }],
  "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
  "host": {
    "fallbackAction": "pass",
    "rules": [
      { "protocol": ["udp"], "remotePort": ["53"], "action": "proxy", "target": "dns-in" },
      { "process": ["tunnelled.exe"], "protocol": ["udp"], "remotePort": ["53"], "action": "proxy", "target": "remote" }
    ]
  }
}
```

**Endpoint locality (2026-10-05).** A local target may name any address, loopback or not. A
non-loopback target is accepted and emits a startup warning: the flow's payload leaves this host in
the clear, and the endpoint's replies are attributed to the flow's original destination. Loopback is
therefore not enforced — a resolver service hosted on a trusted LAN stays configurable — and the
warning is the operator's signal rather than a guard. The alternative (rejecting non-loopback hosts)
was declined for that flexibility.

## Out of scope

- DNS payload parsing, caching, EDNS handling, or any DNS-aware rewrite: the payload crosses the
  local hop unchanged.
- DNS over TCP, DoH/DoT/DoQ, and non-53 resolver ports.
- Any change to sing-box or the user's server configuration; R3 consumes an endpoint that already
  exists.
- The R4 sharing policy itself, and any change to `udpAssociationReuse` semantics.
- The R2 counter's drop-versus-inject decision.

## Acceptance criteria

- [ ] A proxy-decided UDP/53 flow selected onto the local target is answered without any SOCKS5
      control connection or `UDP ASSOCIATE`, and the client observes the reply with its original
      destination as the source address and port.
- [ ] The local transport shares no socket between flows and never appears in the association pool
      or the capability sampler; a per-flow test asserts both.
- [ ] Reply classification is unchanged for this transport. The session's classification path is
      untouched, so the R2 counter stays armed: the local transport declares the destination it was
      last given as the reply's source, and a drift between that and the flow's own destination is
      counted by `udpResponseSourceMismatch` instead of passing as the flow's own
      (`AReplyWhoseDeclaredSourceIsNotTheFlowsDestinationIsCountedByTheR2Counter`). A genuinely
      foreign sender is filtered before the classifier ever sees it — counted as an
      `UnexpectedSource` skip in the session's skip summary — so the local hop is strictly tighter
      than the shared one rather than merely equal.
- [ ] Fail-closed on an unreachable endpoint, with a counted, rate-limited event and no pass
      downgrade.
- [ ] The DNS population served locally is visible in a counter and in the trace log.
- [ ] A non-loopback target starts with a warning naming the address and both consequences
      (payload in the clear, replies attributed to the original destination).
- [ ] The harness local-target column records zero SOCKS5 control connections and zero
      `UDP ASSOCIATE` handshakes for its flows, with first-response latency and bytes/session from
      the corrected per-flow accounting; the result set is committed under `benchmarks/results/`
      with its commands, host line, and any superseded-number notes.
- [ ] README and at least one example config document the opt-in, the endpoint, and the fate of the
      client's original destination.
- [ ] Repository quality gate passes: `dotnet format --verify-no-changes` (empty),
      `dotnet build -c Release` (zero warnings), `dotnet test -c Release` (green), `jb inspectcode`
      (zero `<Issue>`).
