# Design — Local targets for UDP flows

Companion to `prd.md` (task `10-05-local-dns-transport`, parent R3). Written after the code
inspection recorded in the PRD's Background; every anchor below was read, not assumed.

## 1. Shape of the change

Three parts, in dependency order:

1. **A configuration surface** that names a local target and lets a rule select it — the rule's
   existing target field, renamed to `target`, now resolving against two declaration lists.
2. **A transport kind** behind the existing per-flow transport seam: one socket per flow, no
   association lease, payload forwarded verbatim, the flow's original destination synthesized as the
   reply's declared source.
3. **A measurement column** in the stability harness pricing the placement against the existing
   per-flow (`off`) and shared (`auto`) columns.

Nothing in the packet path learns what a flow carries: the transport never decodes, rewrites, or
caches the payload, and DNS is only the first workload that uses it.

## 2. Configuration surface

```json
{
  "socks5Servers": [{ "name": "remote", "host": "proxy.example.com", "port": 1080 }],
  "localTargets": [{ "name": "dns-in", "host": "127.0.0.1", "port": 5353 }],
  "host": {
    "fallbackAction": "pass",
    "rules": [
      { "protocol": ["udp"], "remotePort": ["53"], "action": "proxy", "target": "dns-in" }
    ]
  }
}
```

- `localTargets[]` entries carry `name`, `host` (an IP literal — a hostname here would need DNS to
  configure the DNS path), and `port`; they reuse the `socks5Servers` host/port validation style
  (`src/WinForward.Configuration/ConfigurationModels.cs:245-258`).
- Names are unique across `socks5Servers` and `localTargets`: one namespace, so a rule's `target`
  has exactly one meaning. Implemented next to the existing uniqueness check in
  `ValidateServers` (`ConfigurationModels.cs:221`).
- `RuleDto.ProxyServer` becomes `RuleDto.Target`; the loader rejects the old spelling with a
  diagnostic that names the replacement. Threaded through `FlowDecision.ProxyServerName` →
  `FlowDecision.TargetName` (`src/WinForward.Core/Domain.cs:300`), `FlowDispatcher`
  (`:203`, `:496`), `FlowAttributionPipeline` (`:339`, `:361`), examples, README, tests, benchmarks.
- A rule naming a local target must not be able to match TCP. `protocol` omitted means "any
  protocol", so the check is *not* "protocol list contains tcp": the rule is rejected unless its
  protocol selector is exactly UDP. Diagnostics follow the existing style (a path-qualified message
  in `ConfigurationRules.ParseRule`, `src/WinForward.Configuration/ConfigurationRules.cs:68`).
- A non-loopback `host` adds a `ValidatedConfiguration.Warnings` entry (the existing warning
  channel, `ConfigurationModels.cs:106`, produced by `ConfigurationLimits`), naming the address and
  both consequences: the payload leaves the host in the clear, and the endpoint's replies are
  attributed to the flow's original destination.

The rejected surface shapes are in §10.

## 3. Decision to transport

The resolved target travels as one value instead of a `Socks5Server`:

```csharp
public readonly record struct ProxyTarget(string Name, Socks5Server? Socks5, LocalTarget? Local)
{
    public bool IsLocal => Local is not null;
}
```

`ValidatedConfiguration.Servers` becomes `Targets`
(`IReadOnlyDictionary<string, ProxyTarget>`), and every resolution site keeps its current shape —
one dictionary probe per packet:

| Site | Today | After |
| --- | --- | --- |
| Warm dispatch (`FlowDispatcher.cs:203`) | `_servers.TryGetValue` → `Socks5Server` | `_targets.TryGetValue` → `ProxyTarget` |
| Slow dispatch (`FlowDispatcher.cs:496`) | same | same |
| Attribution (`FlowAttributionPipeline.cs:361`) | same | same |
| Executor (`NdisPacketActionExecutor.cs:356`) | `ProxyAsync(packet, Socks5Server, ct)` | `ProxyAsync(packet, ProxyTarget, ct)`; the TCP branch asserts `target.Socks5` (configuration guarantees it) |
| Coordinator (`UdpProxyCoordinator.Send.cs`) | `TrySendSpanAsync(flow, Socks5Server, …)` | `TrySendSpanAsync(flow, ProxyTarget, …)`; the target is read only on the admission path |

The warm path is load-bearing: a DNS flow's retransmit that resolved to a local target must not fall
into `DispatchSlowAsync` on every datagram, so local targets resolve in the same single dictionary
probe as SOCKS5 servers.

## 4. The transport seam

The seam (`IUdpProxyTransport`, `IUdpProxyTransportFactory`, `IUdpExchangeCounters`, the receive
result vocabulary) currently lives in `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` and is
documented as "UdpProxy's sanctioned cross-group edge into Socks5"
(`.trellis/spec/backend/udp-relay.md`). A second, non-SOCKS5 implementation makes that framing
wrong, and the parent PRD gives R3 ownership of this seam because `09-06-local-mux-transport` R4
plugs into the same decision point. The design therefore:

- moves the contract into the `WinForward.Runtime.UdpProxy` namespace (a new
  `UdpProxy/UdpTransportContracts.cs`), leaving `Socks5UdpTransport.cs` holding only the SOCKS5
  implementation. The dependency flips from `UdpProxy → Socks5` to `Socks5 → UdpProxy`, with no
  cycle: after the move the only Socks5 reference left inside UdpProxy is the relay receive-buffer
  default in `UdpProxyOptions` (`UdpProxyOptions.cs:29`), which is a configuration default rather
  than a contract (`implement.md` C5 decides whether it moves); the concrete factory is constructed
  by the composition root (`src/WinForward.Cli/UdpProxyComposer.cs:78`);
- renames the seam vocabulary away from SOCKS5: `Socks5UdpReceiveResult` →
  `UdpTransportReceiveResult`, `Socks5UdpReceiveSkipReason` → `UdpTransportSkipReason`, and the
  seam's datagram carrier becomes a neutral `UdpTransportDatagram(source, payload)` rather than the
  wire codec's `Socks5UdpDatagram` (`src/WinForward.Protocols/Socks5Udp.cs:11`). The SOCKS5
  transport maps its decoded datagram into the seam type; both are `readonly record struct`s, so the
  mapping is a struct copy and adds no allocation to the receive path;
- changes `IUdpProxyTransportFactory.CreateAsync` to take the `ProxyTarget`, and adds a composite
  that dispatches by target kind:

```csharp
internal sealed class UdpTransportFactory(IUdpProxyTransportFactory socks5, IUdpProxyTransportFactory local)
    : IUdpProxyTransportFactory
{
    public ValueTask<IUdpProxyTransport> CreateAsync(ProxyTarget target, CancellationToken cancellationToken)
        => target.Local is not null
            ? local.CreateAsync(target, cancellationToken)
            : socks5.CreateAsync(target, cancellationToken);
}
```

A future transport kind (the mux shape) adds one list entry, one factory, and one branch here — the
coordinator, session, and reinjector stay untouched.

The seam deliberately gains **no** sharing vocabulary: a local target has no association to lease,
pool, or detect capability on. Sharing remains a property of the SOCKS5 transport.

## 5. Local transport

`LocalUdpTransportFactory` / `LocalUdpTransport` mirror the SOCKS5 transport's per-flow socket shape
(`Socks5UdpTransport.Create`, `Socks5UdpTransport.cs:280-330`) and drop everything association-shaped:

- **One socket per flow.** Flow identity is the socket, so nothing has to demultiplex replies: the
  defect class R3 removes (a reply delivered to the wrong flow) cannot exist at this hop. Sharing one
  local socket across flows would need DNS-transaction-id demultiplexing — payload inspection, which
  R3 rejects — and would not be reliable anyway.
- **Send**: forward `payload` verbatim to the configured endpoint, from a reusable send buffer sized
  by the pinned frame cap (same contract as the relay: the caller's span must not cross an await).
- **Receive**: accept only datagrams whose source equals the configured endpoint (address, port, and
  family — stricter than the relay's port+family rule, because a local target is a specific host we
  chose rather than an anycast relay), classify an oversized datagram as a skip, and report the
  result with the **flow's original destination** as the declared source. That single decision keeps
  `UdpProxySession.TryGetReceiveSource` (`UdpProxySession.cs:403`), `RecordForeignSource` (`:491`),
  and `UdpResponseReinjector` unchanged, including the R2 counter's meaning. The destination is the
  one the coordinator passes on every send (`FlowKey.Remote`); the transport records it on the first
  send and synthesizes the source from it.
- **Loop prevention**: register `(Udp, local socket endpoint, target endpoint)` in
  `SelfTrafficRegistry` before the first datagram, and apply the `SIO_UDP_CONNRESET` posture before
  bind — both as the relay does (`Socks5UdpTransport.cs:309`, `:519`). For a loopback target this is
  hygiene; for a non-loopback target it is load-bearing, because WinForward's own datagrams then
  traverse a captured adapter.
- **Association table**: the flow claims a per-flow alias (its own local endpoint + the target
  endpoint) and the row is never shared. The claim is what makes the target's reply leg classifiable
  as the reverse of a stored flow (`FlowDispatcher.cs:243`), which matters for the non-loopback
  case; the pool, the capability sampler, and `udpAssociationReuse` are never consulted.
- **Retention**: implement `IUdpExchangeCounters` (`Socks5UdpTransport.cs:105`). A transport without
  it is classified as *sustained*, which would keep every DNS session for the configured 30 s
  instead of retiring a completed one-shot at 5 s (`UdpProxySession.cs:64`, `:289`).
- **Failures**: an unreachable endpoint, a refused send, or a malformed/oversized reply follows the
  existing UDP failure semantics — fail closed, counted, rate-limited, no pass downgrade. There is no
  dial to fail, so `proxyUnavailableAction` is not consulted for this transport; datagram-level
  drops match the relay's send-failure path.

## 6. Observability

- Counters: `udpLocalTargetFlows` (one per local transport created) and `udpLocalTargetFailures` (a
  send the socket refused, plus a fatal receive fault), beside the existing UDP counters
  (`src/WinForward.Runtime/RuntimeCounters.cs`), so R4 can price the DNS population. Both ride the
  free-form counter registry whose deltas the heartbeat already reports.
- Event: the existing `udp.session.created` trace line gains the target kind and name
  (`target=<name>`, `targetKind=local|socks5`), which is what separates the two placements in the
  trace log. No new event name is introduced: a local-target send or receive failure surfaces
  through the executor's existing rate-limited UDP failure warn, which does not name the target kind
  — the counter is the measurement there.
- README documents the two counters and the trace fields in the UDP prose beside
  `udp.response.foreign_source` rather than adding event-table rows, because no new event exists.

## 7. Compatibility and migration

- The configuration schema changes: `proxyServer` → `target`, plus the new `localTargets` list. The
  loader fails closed on the old key with a diagnostic naming the replacement; no dual spelling is
  accepted (one decision, one name).
- Existing configurations keep working after the rename; a configuration that never declares a local
  target behaves exactly as today.
- `configuration is immutable for the lifetime of a run` (README) is unchanged.
- The transport uses no Windows-only API, so the Linux-run stability harness exercises the same code
  path as production.

## 8. Measurement

The harness bypasses the policy layer entirely: each scenario hand-builds the coordinator
(`UdpChurnScenario.cs:46-66`) and its own server entry, then calls
`TrySendSpanAsync(flow, server, …)`; every row records its column in both `metadata.options` (via the
options record, `StabilityContext.cs:33`) and its own `parameters` (the self-description rule R1's
README states at `:50-55`). R3 adds a local-target column on that machinery:

- **Host scenarios**: `udp.churn` and `udp.burstEstablishment`, which carry the R1 accounting and are
  association-agnostic. `udp.sessionBudget` is excluded: its pooling verdict and warmup expectation
  are keyed on the association pool, which a local target never touches.
- **Knob**: `--target <socks5|local>` (default `socks5`), refused for unknown values like
  `--reuse` (`SoakOptions.cs:379-390`), recorded in `metadata.options` and in every row's
  `parameters`.
- **Server side**: a benchmark-side `LoopbackLocalUdpResponder` — a loopback UDP socket that answers
  each datagram verbatim and counts what it received, with no TCP control channel, no ASSOCIATE, and
  no last-sender write path. `LoopbackSocks5UdpServer` gains a control-connection counter so the
  "zero handshakes" claim is observed instead of asserted by construction.
- **Client side**: the **product** `LocalUdpTransportFactory`, so the column prices shipped code; the
  scenario passes a `ProxyTarget` whose `Local` is set. A hand-written fake transport would measure a
  shape nobody ships.
- **Recorded columns**: control connections and ASSOCIATE handshakes (zero for the local column),
  first-response latency, bytes/session, and the existing per-flow accounting — the local column is
  expected to read `firstResponses == accepted`, `misdelivered == 0`,
  `noResponse == flows − accepted`.
- **Constraint the harness cannot dodge**: `UdpSessionSetup.CreateSessionAsync` always claims an
  association alias built from the transport's local and relay endpoints
  (`UdpSessionSetup.cs:103-116`), so the local transport must report a distinct pair per flow or a
  second flow fails with "UDP relay alias collision". The product transport satisfies this by
  construction (one socket per flow).
- **Results**: `benchmarks/results/<date>-local-target/` with the exact commands, host line, tables,
  and explicit supersession/series-break notes; older directories gain the supersession blockquote
  rather than losing their numbers.

## 9. Risks and rollback

| Risk | Mitigation |
| --- | --- |
| The warm path stops resolving local targets, so DNS flows pay the slow path per datagram | the single-dictionary-probe design in §3; the harness's `timeToIssueMs`/latency columns and a unit test on the warm dispatch |
| Rename churn (`proxyServer` → `target`, seam type renames) drifts a test or benchmark config silently | mechanical rename across the tree; the full `dotnet build -c Release` / `dotnet test -c Release` gates |
| A non-loopback target's own traffic recurses through the capture path | self-traffic registration plus the per-flow alias (§5); a test asserting the registry tuple and the reverse-classification outcome |
| The seam move breaks the documented SOCKS5↔UdpProxy boundary | ship the move and the spec update (`udp-relay.md`) in the same change; SOCKS5 tests must pass unmodified except for the renames |
| Local-target failures surface as silent client timeouts | counted, rate-limited warn events (§6) |

Rollback: the feature is opt-in, so a configuration that declares no local target is unaffected and
reverting the change is a plain revert of one change set — no data migration, no persisted state.

## 10. Rejected alternatives

- **One global endpoint, port-53 implicit selection.** Turns a rule's declared target into a partial
  lie for the port it serves, gives up per-process/per-adapter granularity, and puts a protocol check
  into the packet path.
- **Global endpoint plus a per-rule opt-out flag.** Two knobs for one decision.
- **Reusing `Socks5Server` for a local endpoint.** A type lie in a codebase whose names carry
  contracts; it would also let a local endpoint be handed to the TCP `CONNECT` path.
- **Sharing one local socket across DNS flows, demultiplexed by transaction id.** Payload inspection
  in a transport-layer component (the same reason L1's QUIC clause was rejected by the parent PRD),
  and unreliable when two clients reuse a transaction id.
- **DNS-aware handling (parsing, caching, EDNS rewriting).** WinForward's packet path does not know
  what a flow carries, and a cache would invent an answer the endpoint never gave.
- **Loopback-only validation.** Declined by decision (PRD "Decided: endpoint locality"): a resolver
  on a trusted LAN stays configurable, with a startup warning instead of a guard.
- **A separate rule action.** `FlowAction` is a closed set with pass/block/proxy semantics threaded
  through both rule domains; a local target is a *where*, not a *what*, so it belongs in the target,
  not the action.
