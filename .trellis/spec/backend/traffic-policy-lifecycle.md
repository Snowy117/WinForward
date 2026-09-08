# Traffic Policy & Lifecycle Contracts

> How WinForward decides which captured flows are proxied (host vs forwarded policy domains), which frames bypass policy entirely, how WinForward's own traffic is exempted (loop prevention), and how idle state is swept. Split 2026-08-29 from the former monolithic NDISAPI file; transport-level contracts live in [windows-ndisapi.md](./windows-ndisapi.md), [tcp-local-redirect.md](./tcp-local-redirect.md), [udp-relay.md](./udp-relay.md).

---

## Host vs forwarded policy domains (fixed 2026-08-11)

- Policy eligibility is not the same as capture scope. Adapter-unqualified host rules may require all MSTCP-bound adapters to remain captured, so forwarded eligibility is enforced after self-traffic, TCP reverse handling, and existing-flow resolution, immediately before a genuinely new flow is claimed.
- New `Host` flows evaluate the complete ordered rule list (`_policy.Evaluate`) and use the configured `fallbackAction`.
- New `Forwarded` flows evaluate only rules containing `adapterId` and/or `adapterName` (`_policy.EvaluateForwarded`, both invoked from `FlowDispatcher`), preserving original rule order and every additional matcher condition. If none match, they pass independently of adapter-unqualified rules and `fallbackAction`.
- The same adapter-qualified-only/default-pass contract applies to forwarded packets that cannot be classified as TCP/UDP flows. Host non-flow behavior remains unchanged. **Superseded 2026-08-14**: non-flow frames (non-IP, non-TCP/UDP, unparseable, fragmented) now always pass on both origins without any policy evaluation — blocking them broke ARP/ICMPv6-ND and silently severed L2 (see "Non-flow frames always pass").
- The implicit forwarded pass is cached in `FlowTable` (`src/WinForward.Core/FlowTable.cs`); reverse and cross-adapter observations reuse it before origin-specific policy can run again. Flow-table capacity exhaustion still fails closed.
- `Forwarded` is derived from NDIS `ON_RECEIVE`, not an authoritative Windows routing decision. It includes both traffic Windows may route across adapters and new inbound traffic addressed to a service on the host.
- Regression coverage: `ForwardedPolicySkipsUnqualifiedRulesAndConfiguredFallback`, `DispatcherSeparatesForwardedAdaptersFromHostCatchAllPolicy`, `DispatcherCachesForwardedImplicitPassAcrossOriginsAndFailsClosedAtCapacity`; non-flow pass coverage moved to `DispatcherForwardedNonFlowAlwaysPassesRegardlessOfRules` / `DispatcherHostNonFlowAlwaysPassesRegardlessOfFallbackAndRules` (see "Non-flow frames always pass").

---

## Non-flow frames always pass (fixed 2026-08-14)

Frames that cannot be classified as TCP/UDP flows (ARP, ICMPv6 ND, other L2/L3, unparseable, fragments) are never policy-evaluated: `FlowDispatcher.DispatchNonFlowAsync` passes them unconditionally after the self-traffic check. Policy exists to govern proxyable flows; blocking non-flow frames under a catch-all proxy/block rule silently broke ARP resolution and produced total L2 failure on the gateway (26 `packet.dropped reason=policy`, all nonFlow, 23 of them 42-byte ARP frames, while test traffic never even reached the capture layer). Locked by `DispatcherForwardedNonFlowAlwaysPassesRegardlessOfRules` / `DispatcherHostNonFlowAlwaysPassesRegardlessOfFallbackAndRules`.

---

## Policy authoring for gateway LANs (hardware-verified 2026-08-15)

Because forwarded flows evaluate only adapter-qualified rules (see "Host vs forwarded policy domains"), a plain `remoteCidr`-only pass rule can never exempt LAN traffic that arrives on a bridged adapter — and a host flow egressing the bridge adapter DOES match `adapterId` rules, so rule ORDER decides whether LAN traffic is proxied (a remote upstream cannot reach 192.168.x and the connection blackholes after the redirect handshake). The working pattern is a combined rule placed before the adapter-proxy rule; matchers are AND-combined and the rule stays adapter-qualified: `{ "adapterId": [...], "remoteCidr": [LAN prefixes], "action": "pass" }`. It exempts both host-origin and forwarded LAN traffic without any `EvaluateForwarded` change.

---

## Loop prevention: control-connection registration happens BEFORE the SYN (fixed 2026-08-12)

- **Both TCP `CONNECT` and UDP `ASSOCIATE` control connections are SOCKS5 traffic to the proxy endpoint and must be registered in `SelfTrafficRegistry` before their SYN leaves the host.** A catch-all proxy rule would otherwise capture WinForward's own control SYN and recurse until the bounded session capacity is exhausted.
- `Socks5ControlConnection.ConnectAsync` (`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`) binds the socket to a wildcard local endpoint first, invokes an `onSocketReady(local, remote)` callback (which returns the loop-prevention token) BEFORE `socket.ConnectAsync`, and owns/disposes the token with the connection. Registering after connect leaves a race where the SYN is already observable.
- The registered tuple is `(Tcp, Any:bound_port, proxy_ip:socks_port)`: the socket is bound to wildcard, so the local address is Any and the wildcard matcher covers the routing-chosen source IP, exactly like the UDP relay socket. Registering `(proxy, proxy)` is a bug — it can never match the observed `(host:ephemeral, proxy:socks)` and silently disables loop prevention.
- A failed connection attempt (DNS multi-address fallback) disposes the registration of that attempt before trying the next address.
- Locked by `SelfTrafficUpstreamTcpTupleIsOwnedWhenObservedAsHostEphemeralToProxy` (forward + reverse owned; an unrelated app sharing the proxy endpoint with its own source port is NOT exempted).

---

## Idle expiry sweep (wired 2026-08-12)

- `FlowTable.RemoveExpired`, `TcpRedirectTable.RemoveExpired`, and `UdpAssociationTable.RemoveExpired` (`src/WinForward.Runtime/UdpProxy/UdpAssociations.cs`) implement idle expiry but had no caller; UDP sessions accumulated to the bounded capacity and then failed closed.
- `IdleExpirySweeper` (`src/WinForward.Runtime/IdleExpirySweeper.cs`) is the single wiring point: started in `Program.RunCaptureLoopAsync`, it periodically sweeps `TcpProxyCoordinator.RemoveExpiredAsync`, `FlowDispatcher.RemoveExpiredFlows`, and `UdpProxyCoordinator.RemoveExpiredAsync` in that fixed order (tcp → flows → udp, so redirect tombstones/sessions are recycled before flows are evaluated; see the flow-hold contract in [tcp-local-redirect.md](./tcp-local-redirect.md)). It is disposed (ordered disposal in `CoordinatorShutdownCaptureLoop`) after the capture runtime stops and before the coordinators are released.
- `UdpProxySession` tracks `LastActivityUtc` on send and receive; `UdpProxyCoordinator.RemoveExpiredAsync` disposes idle sessions and releases their `UdpAssociationTable` entries. The relay-alias collision guard (`UdpAssociationTable` claim per flow, design §8) is now live: a second flow claiming the same relay alias is rejected fail-closed.
- Sweep failures are isolated (RCS1075 suppression, same pattern as `CaptureLifecycle.cs` in `src/WinForward.Runtime/Capture/`) so a transient teardown error cannot stop the capture loop.
