# Traffic Policy & Lifecycle Contracts

> Which captured flows WinForward proxies: the two policy domains, the frames that bypass policy
> entirely, and how WinForward keeps its own traffic out of the proxy.
> The idle-expiry sweeper that recycles the state these rules create has its own document:
> [idle-expiry-sweep.md](./idle-expiry-sweep.md). Transport-level contracts live in the
> [redirect family hub](./tcp-local-redirect.md), the [UDP relay family hub](./udp-relay.md) and the
> [NDISAPI hub](./windows-ndisapi.md).

## Scope / Trigger

Read before touching `PolicySnapshot`, `RuleMatcher`, `FlowDispatcher`'s dispatch or admission path,
`CaptureAdapterScopeResolver`, `SelfTrafficRegistry`, or the `Host.Rules` / `Forwarded.Rules`
configuration schema.

---

## Two Policy Domains

**Policy eligibility is not capture scope.** Adapter-unqualified host rules may require every
MSTCP-bound adapter to stay captured, so forwarded eligibility is enforced *after* self-traffic, TCP
reverse handling and existing-flow resolution — immediately before a genuinely new flow is claimed.

**Admission has a second, earlier gate (F8, 2026-10-01).** Only the attribution-eligible shape is
deferred to the setup worker:

```
RequiresProcessAttribution ∧ attributor ≠ null ∧ Process is null ∧ Origin == Host
```

A forwarded miss, or a host miss with no process rule, is refused admission before the pipeline is
touched and keeps the inline path byte-for-byte. Both gates are the same method
(`FlowDispatcher.ShouldAttribute`), pinned by `AForwardedMissNeverCreatesAPendingEntry` and
`ANoProcessRuleMissNeverCreatesAPendingEntry`.

- **`Host` flows** evaluate `Host.Rules` in order (`PolicySnapshot.EvaluateHost`) and fall back to
  `Host.FallbackAction`. For an attribution-eligible miss, process attribution *and* policy
  evaluation run on a pooled setup worker before the flow-table claim rather than inside the claim's
  gate on the pump thread. The decision is still stored exactly once per logical flow
  (`FlowTable.TryClaimResolved`); a second entry for one transport tuple pays a second evaluation
  whose claim returns the existing decision, counted as `attributionReAdmission`.
- **`Forwarded` flows** evaluate `Forwarded.Rules` in order (`PolicySnapshot.EvaluateForwarded`) and
  fall back to `Forwarded.FallbackAction`, default `pass`.
- **Rule eligibility is positional (2026-10-04, task 10-04-host-forwarded-rule-split).**
  `adapterId`/`adapterName` on a forwarded rule narrow it to the adapter the packet arrived on; they
  no longer decide whether the rule belongs to the forwarded domain. A forwarded rule with no adapter
  selector is therefore valid and applies to every forwarded flow, and `EvaluateForwarded` no longer
  consults `RuleMatcher.IsAdapterQualified` (deleted). Rule order and every other matcher condition
  are preserved.
- **`Forwarded` is derived from NDIS `ON_RECEIVE`**, not from an authoritative Windows routing
  decision. It covers both traffic Windows may route across adapters and new inbound traffic addressed
  to a service on the host.
- The forwarded default pass is **cached in `FlowTable`** (`src/WinForward.Core/FlowTable.cs`); reverse
  and cross-adapter observations reuse it before origin-specific policy can run again. Flow-table
  capacity exhaustion still fails closed.

### No rule serves both domains (2026-10-04)

`PolicySnapshot` holds two lists — `HostRules` and `ForwardedRules` — and each evaluation reads only
its own. A rule intended for both domains must be written in both `Host.Rules` and `Forwarded.Rules`.

- `process` is **rejected inside `Forwarded.Rules` at validation**: a forwarded flow has no host
  process owner, so the matcher could never fire. This is why `PolicySnapshot.RequiresProcessAttribution`
  scans host rules only.
- Capture scope **unions both lists** (`CaptureAdapterScopeResolver.AccumulateScope`):
  adapter-constrained rules in either domain contribute their resolved adapters, an unconstrained rule
  in either domain widens scope to every MSTCP-bound adapter, and selector diagnostics are
  domain-qualified (`WinForward.Host.Rules[i]` / `WinForward.Forwarded.Rules[i]`).
- A list missed here reads as a policy bug but is a scope bug: "rule configured, adapter never
  captured".

Enforced by `EndpointAndPolicyTests` (domain separation and rule order),
`AdapterScopeAndFlowTableTests` (scope union and diagnostics), `FlowDispatcherExecutorTests`
(cross-origin fallback caching and capacity) and `FlowAttributionPipelineTests` (admission).

### Rule evaluation reads interned metadata (F4, 2026-09-30)

`RuleMatcher` reads `context.ProcessName`, `ProcessPath`, `AdapterId`, `AdapterName` and `RemotePort`,
but the values come from the two interned metadata references the packet carries — `AdapterMetadata`
from the slot table's own instance, `ProcessMetadata` created once at claim — plus the packed
`FlowKey.RemotePort`. No string is copied per packet, and a warm packet still carries the adapter
identity with a null process identity
(`FlowContextMetadataTests.ClaimedFlowCarriesTheInternedAdapterAndProcessMetadata`,
`WarmHitKeepsTheAdapterMetadataAndCarriesNoProcessMetadata`). An adapter-qualified rule on an adapter
the slot table does not know sees `AdapterId == null`, the same shape as an adapter-less key.

---


---

## Process Attribution: The Owner-Table Cache

A process-attribution cache on the setup-worker path: it awaits, it never runs on the packet path,
and it is entered once per new flow rather than once per packet — so it carries an allocation budget
instead of the packet path's exemption: one reusable slot per kind, no row array and no framework
address per scan (see `hot-path.md`'s scope note and
[native-lease-and-pool-lifetime.md](./native-lease-and-pool-lifetime.md#owner-table-slots)). It is
what turns a policy rule's `process` selector into a decided owner.

- **One reusable slot and one single-flight refresh gate per `OwnerTableKind`** (Tcp4, Tcp6, Udp4,
  Udp6). The reader refills that slot in place and the cache searches it under the same gate, which is
  what keeps a scan free of managed allocation; the slot's rows are `IPAddressValue` values and its
  storage grows once to the widest table seen. Concurrent missers join one in-flight fill whose
  contents are published after the last of them asked, so a burst's own newly-bound sockets are
  visible to the shared read — a window alone cannot deliver that, because a socket's row appears at
  bind, microseconds before the packet that triggers the lookup. Recorded series: the `attribution.ownerBurst` row costs **1** read for 16 concurrent
  flows over one scripted table, and the exact form is
  `ProcessOwnerTableCacheTests.NConcurrentMissesInsideTheWindowReadTheTableExactlyOnce`. The scan
  count is a **series, never a threshold**.
- **The freshness rule is the request instant, not a flag.** A slot filled before the caller asked may
  only answer a **positive TCP row**; a miss always falls through to a read, so a flow whose socket
  bound after the last fill still gets a real scan. A fill published at or after the request is
  authoritative both ways, which is what lets `WindowsProcessAttributor.FindAsync`'s 2 ms retry —
  a lookup with a later instant — answer from the first attempt's fill instead of forcing a second.
- **Positive caching is per kind, and the bound is the predicate's.** A TCP row matches all four tuple
  fields, so a row that survives into a later request describes the same connection; an
  exact-4-tuple reuse inside the 300 ms window is effectively impossible under TIME_WAIT, so TCP
  reuse is accepted. A UDP row matches the **local port alone**, so a recycled port inside the window
  would attribute a flow to the previous process (fail-open) — **UDP never answers from a slot filled
  before the request**, only from a fill published at or after it. Closing the UDP half
  needs the `*_TABLE_OWNER_MODULE` creation timestamp, a recorded on-Windows follow-up.
- **The seam is the platform boundary.** `IPHelperOwnerTableReader` is the single
  `[SupportedOSPlatform("windows")]` type and owns the size probe, the table call and the
  `NativeMemory` release; `IPHelperOwnerTableParser` owns the fail-closed row-count validation and the
  row decode, which is what lets the decode's zero-allocation gate run on a host without the native
  tables. The default reader off-Windows is `UnavailableOwnerTableReader`, so the observable result
  there stays `null` while the managed cache logic is exercisable on any host through an injected
  reader. The predicates live in `OwnerTable` unchanged and the `Where/Select/Distinct/ToArray` chain
  became one predicate pass, so a slot hit and a fresh scan answer identically for identical rows.
- **The wake signal is latency, never correctness, and its ownership is split three ways.**
  `CompositePacketArrivalSignal` composes the **borrowed** driver signal with one event the
  `FlowAttributionWakeRegistry` owns: the composite disposes the driver signal in place of the list
  owner (so the driver registration is still released exactly once), the registry never touches a
  composite, and the registry keeps **one event per adapter handle**, reused across generations,
  which `DurableCaptureBundle` disposes after the pumps have stopped and the pipeline is sealed (a
  disposed event is reported as "not signalled", so a racing `Wait`/`Signal` cannot throw out of the
  idle path). A refused driver registration registers nothing and the pipeline's signal becomes a
  no-op: delivery then waits at most the poll delay or the idle bound
  (`CompositePacketArrivalSignalTests`,
  `FlowAttributionPipelineTests.ThePipelineSignalsOnlyItsOwnAdaptersEvent`).

```csharp
// Wrong: serve a UDP answer from a slot filled before the request — the predicate is the local port
// alone, so a recycled port attributes the flow to the previous process (fail-open).
return snapshot.Table.Lookup(key);

// Correct (all under the kind's gate): a fill published at or after the request answers positively or
// negatively; anything older may only confirm a positive TCP row; a miss reads.
if (snapshot is not null && snapshot.IsUsable)
{
    if (snapshot.TakenUtc >= requestInstant) return snapshot.Table.Lookup(key);
    if (ReusesSnapshot(kind) && IsFresh(snapshot, _clock()) && snapshot.Table.Lookup(key) is { } reused) return reused;
}

var table = _reader.Read(kind);
return table.IsAvailable ? table.Lookup(key) : null;
```

Tests: `ProcessOwnerTableCacheTests` — coalescing, the retry joining a later fill, a post-read bind
forcing exactly one more read, a cached TCP hit, UDP never reusing, window expiry, scan/snapshot
agreement, the recycled-port asymmetry, `window = 0`, and the unavailable reader.
`IPHelperOwnerTableParserTests` locks the decode and the fill protocol: the four row images
(TCP4/TCP6/UDP4/UDP6, including the IPv6 scope ids), a begun fill being unsearchable, a rejected row
count leaving the slot's contents answering, the non-fillable `Unavailable` constant, and the 0 B
allocation gate.

---

## Non-flow Frames Always Pass (fixed 2026-08-14)

Frames that cannot be classified as TCP/UDP flows — ARP, ICMPv6 ND, other L2/L3, unparseable,
fragments — are **never policy-evaluated**: `FlowDispatcher.DispatchNonFlowAsync` passes them
unconditionally after the self-traffic check.

Policy exists to govern proxyable flows. Blocking non-flow frames under a catch-all proxy/block rule
silently broke ARP resolution and produced total L2 failure on the gateway: 26
`packet.dropped reason=policy` records, all nonFlow, 23 of them 42-byte ARP frames, while test traffic
never reached the capture layer. Locked by `DispatcherForwardedNonFlowAlwaysPassesRegardlessOfRules`
and `DispatcherHostNonFlowAlwaysPassesRegardlessOfFallbackAndRules`.

---

## Loop Prevention: Control Connections Register Before Their SYN (fixed 2026-08-12)

- **Both the TCP `CONNECT` and the UDP `ASSOCIATE` control connection are SOCKS5 traffic to the proxy
  endpoint and must be registered in `SelfTrafficRegistry` before their SYN leaves the host.** A
  catch-all proxy rule would otherwise capture WinForward's own control SYN and recurse until the
  bounded session capacity is exhausted.
- `Socks5ControlConnection.ConnectAsync`
  (`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`) binds the socket to a wildcard local
  endpoint first, invokes an `onSocketReady(local, remote)` callback — which returns the
  loop-prevention token — **before** `socket.ConnectAsync`, and owns and disposes that token with the
  connection. Registering after connect leaves a race in which the SYN is already observable.
- The registered tuple is `(Tcp, Any:bound_port, proxy_ip:socks_port)`. The socket is bound to
  wildcard, so the local address is `Any` and the wildcard matcher covers the routing-chosen source
  IP, exactly like the UDP relay socket. **Registering `(proxy, proxy)` is a bug**: it can never match
  the observed `(host:ephemeral, proxy:socks)` and silently disables loop prevention.
- A failed connection attempt (DNS multi-address fallback) disposes that attempt's registration before
  trying the next address.
- **The guard is split: the wildcard half answers on the warm path, the exact half once per claim.**
  The wildcard half is the load-bearing one for loop prevention — `TcpProxyRelay` registers its
  upstream control connection before its SYN, so a host-flow ephemeral port recycled to that socket
  must never resolve stale proxy state on the warm path
  (`ARelayWildcardTupleIsNeverProxiedOnAWarmHit`). The lock-free shape of that half, the exact-tuple
  half, and the accepted delta for an exact registration made after a flow was claimed are in
  [warm-path-dispatch.md](./warm-path-dispatch.md), "the self-traffic split".
- `SelfTrafficUpstreamTcpTupleIsOwnedWhenObservedAsHostEphemeralToProxy` locks the tuple shape in
  both directions; an unrelated application sharing the proxy endpoint with its own source port is
  **not** exempted.

---

## Authoring Policy For A Gateway LAN (hardware-verified 2026-08-15)

Because `Forwarded.Rules` is its own domain, a LAN exemption is written once in that list instead of
being smuggled in as an adapter-qualified rule ordered ahead of the proxy rule. Positional eligibility
(2026-10-04) makes a bare `RemoteCidr`-only pass rule eligible on its own, so the working pattern is

```json
{ "RemoteCidr": ["<LAN prefixes>"], "Action": "pass" }
```

placed **before** the adapter proxy rule in `Forwarded.Rules`. If host egress on the bridge adapter
also needs the exemption, the same rule goes into `Host.Rules` as well — no rule serves both domains,
and rule order only decides within one domain. Without the exemption a remote upstream cannot reach
192.168.x and the connection blackholes after the redirect handshake.

**Configuration key names are PascalCase, and a wrongly-cased key fails the load.** The document goes
through `JsonSerializer.Deserialize` with the source-generated context, which is case-sensitive:
`"remoteCidr"` is not a selector with a default, it is an unusable setting. The loader's own
no-section diagnostic states the rule — settings live under a `WinForward` object and key names are
PascalCase, for example `WinForward.TcpFlowCapacity` (`ConfigurationLayering.cs`), and every other
diagnostic names the rule's full path (`WinForward.Host.Rules[0].RemoteCidr`).
