# Windows NDISAPI Adapter Identity Contract

> How WinForward correlates NDISAPI runtime adapters with stable Windows adapter identities. Verified on a real Windows 11 host with WinpkFilter 3.6.2.1 during the 08-07-winforward-proxy check phase.

---

## 1. Scope / Trigger

- Trigger: any code that enumerates adapters (`adapters` CLI command), resolves `adapterId`/`adapterName` config selectors, or maps an `INTERMEDIATE_BUFFER` adapter handle back to a configured adapter.
- This is an infra integration contract: NDISAPI (kernel driver handles) and Windows IP Helper (`NetworkInterface`) are two separate enumeration planes that must be joined correctly.

## 2. Signatures

- `WindowsAdapterInventory(Func<IReadOnlyList<(string InternalName, nint Handle, byte[] Mac, ushort Mtu)>> ndisAdapters, Func<IReadOnlyList<IpAdapterInfo>>? ipAdapters = null)` — the second provider is injectable so correlation is unit-testable without Windows hardware.
- `IReadOnlyList<WindowsAdapter> GetCurrentAdapters()` — `WindowsAdapter(StableId, FriendlyName, InternalName, RuntimeHandle, Generation)`.
- `IpAdapterInfo(string Id, string Name, byte[] Mac)` — projection of `NetworkInterface.Id` / `.Name` / `.GetPhysicalAddress()`.

## 3. Contracts

- NDISAPI `GetTcpipBoundAdaptersInfo` internal name is typically `\DEVICE\{GUID}`; stripping the `\DEVICE\` prefix yields the adapter's NetCfgInstanceId, which equals `NetworkInterface.Id` (`{GUID}`, same casing on observed systems — compare case-insensitively and brace-insensitively via `Guid.TryParse` normalization).
- **Correlation is GUID-primary.** MAC is only a sanity-check fallback when the internal name is not a GUID.
- `RuntimeHandle` is process-lifetime state: never persist it, never print it as identity, rebuild it on adapter-list change.
- Ambiguity rule: a correlation key must match **exactly one** IP Helper adapter; zero or multiple matches fall back (see matrix).
- IP Helper owner-PID IPv6 `ScopeId` fields are host-order DWORDs and must be preserved when constructing `IPAddress`; only owner-row port fields use network byte order and require conversion.

## 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| GUID extracted from internal name, exactly one `NetworkInterface.Id` match | correlated (`StableId` = `Id`, `FriendlyName` = `Name`) |
| GUID match count 0 or >1 | try MAC fallback |
| MAC fallback matches exactly one | correlated |
| MAC fallback matches 0 or >1 | uncorrelated: `StableId`/`FriendlyName` fall back to the internal name (never guess) |
| Internal name not a GUID and MAC is all-zero (hidden/virtual adapters report `000000000000`) | uncorrelated fallback |

## 5. Good/Base/Bad Cases

- Good: `\DEVICE\{DD8CD9A1-...}` ↔ `Id = {DD8CD9A1-...}` → friendly name `Ethernet`. Verified for physical and hidden `Local Area Connection* N` adapters alike (the hidden ones have no MAC at all and only resolve via GUID).
- Base: adapter whose internal name is not a GUID → MAC fallback resolves it when the MAC is unique.
- Bad: MAC-only correlation. NDIS filter drivers (WFP Native/802.3 MAC Layer LWF, WinpkFilter's own NDIS LWF, Npcap, QoS Packet Scheduler) each clone the physical MAC, so one MAC appears on ~6 `NetworkInterface` entries and the ambiguity check always fails. **MAC-only correlation never works on a real Windows host.**

## 6. Tests Required

- Unit (hardware-independent, via injected `IpAdapterInfo` provider): GUID correlation success; brace/case-insensitive GUID compare; MAC fallback when internal name is not a GUID; bare-GUID internal name; duplicate-MAC interfaces must NOT corrupt GUID correlation; zero-MAC and ambiguous adapters fall back to the internal name. IP Helper projection tests preserve a nonzero IPv6 scope ID while converting network-order ports.
- Windows smoke: `WinForward.exe adapters` prints stable GUID + friendly name + internal name for every MSTCP-bound adapter (exit 0); without `ndisapi.dll` it exits 1 with an actionable diagnostic.

## 7. Wrong vs Correct

### Wrong

```csharp
// MAC-only correlation: always ambiguous once any NDIS filter driver
// (including WinpkFilter itself) clones the MAC onto filter interfaces.
var matches = ipAdapters.Where(ip => ip.Mac.SequenceEqual(adapter.Mac)).Take(2).ToArray();
var matched = matches.Length == 1 ? matches[0] : null; // always null on real hosts
```

### Correct

```csharp
// GUID primary (internal name GUID == NetworkInterface.Id), MAC as sanity fallback.
if (TryExtractGuid(adapter.InternalName, out var guid))
{
    var guidMatches = ipAdapters.Where(ip => TryExtractGuid(ip.Id, out var id) && EqualsOrdinalIgnoreCase(id, guid)).ToArray();
    if (guidMatches.Length == 1) return guidMatches[0];
}
// ... then MAC fallback, then internal-name fallback. See AdapterIdentity.cs.
```

**Related**: `.trellis/tasks/08-07-winforward-proxy/research/winpkfilter-ndisapi-design-constraints.md` (direction semantics, handle lifetime, IP Helper correlation evidence); `src/WinForward.Windows/AdapterIdentity.cs` (reference implementation).

---

## Adapter Handles: Enumeration vs Captured (hardware-verified 2026-08-07)

> **Warning**: `GetTcpipBoundAdaptersInfo` returns per-adapter handles that are the ONLY valid values for request-level `hAdapterHandle` fields. The `INTERMEDIATE_BUFFER.m_hAdapter` seen in captured packets is a DIFFERENT kernel pointer (observed: list handle `0xFFFFAD8A2B30B010` vs captured `0xFFFFAD8A2B30B2D0` on the same adapter). Passing the captured `m_hAdapter` as the request handle makes `SendPacketToAdapter`/`SendPacketToMstcp` fail with `ERROR_INVALID_PARAMETER` (87) on every packet.

- `NdisCapturePump` must stamp captured packets with the pump's enumeration handle (`NdisCapture.cs`), never with `NdisPacketBuffer.CapturedAdapterHandle`.
- A captured packet carries two distinct flag values: `DeviceFlags` selects MSTCP-relative direction, while `INTERMEDIATE_BUFFER.m_Flags` is NDIS packet metadata. Preserve both through the managed capture record and ordinary pass reinjection; a fresh synthetic frame intentionally starts with metadata flags zero.
- **Native-call gate topology (superseded 2026-08-28, task 08-28-udp-loss-design-flaws D3)**: `NdisApiDriver` splits serialization into a **control gate** (open/close/enumeration/mode snapshot+set+restore — cold path) and a **per-adapter-handle gate map** (`NdisAdapterGateMap`: leaf `Lock` + `Dictionary<nint, NdisNativeCallGate>`, grow-only) used by `TryReadPackets`, `SendPacketToMstcp`, `SendPacketToAdapter`, keyed by the enumeration handle the request carries. Within one adapter handle every native call stays serialized (preserves per-adapter read/reinject ordering and the historical OVERLAPPED concern — each request struct is method-local); **across adapters calls proceed in parallel**, so a slow IOCTL on adapter A can no longer stall adapter B's pump reads. The queue query + batch read pair keeps sharing ONE lease of the adapter's gate. Lock order is fixed: the map lock is a leaf (never held across `gate.Enter()`), and the control gate is never nested inside an adapter gate or vice versa. This replaces the earlier "serialize every operation on one driver instance" contract: that single-Monitor design coupled all adapters through one lock and was a confirmed driver-queue-overflow (silent loss) amplifier under multi-adapter/high-pps load. Gate contention telemetry (`MaxConcurrentCalls`) is preserved per gate. Rollback shape if driver-level coupling ever shows up on hardware: a single send-gate + per-adapter read-gates.
- This matches the official samples: `ETH_M_REQUEST.hAdapterHandle` is set once from the adapter list and reused for read/write requests.
- All NDISAPI `[LibraryImport]` declarations use `SetLastError = true`; send-path exceptions must include `Marshal.GetLastWin32Error()` — driver-side rejections are otherwise undiagnosable.
- Diagnostics context worth logging on send failure: native error, frame length, device flags, adapter handle.

**Symptom / Cause / Fix** (recorded as a verified bug class):

- Symptom: capture works, tunnel mode applies, but every pass reinjection fails with native error 87 and the runtime shuts down fail-closed.
- Cause: request built with the captured buffer's `m_hAdapter` instead of the enumeration handle.
- Fix: `NdisCapturedPacket(buffer, _adapterHandle, buffer.DeviceFlags)` in the pump.
- Verification: scratch harness `sendtest` (read -> reinject captured buffer with enumeration handle: 97/97 OK, both directions). Product re-verified: 100/100 ICMP pass-through with 0% loss and no duplicates.

**Performance note**: the pump now reads in batches (`ReadPackets`, batch capacity 32, one gate lease per batch; wired 2026-08-27 — see "Batched capture reads and buffer pooling" below). The 1 ms poll delay remains only on empty batches; since 2026-08-28 the capture run is wrapped in `WinForward.Windows.HighResolutionTimerScope` (winmm `timeBeginPeriod(1)`, fail-open with a one-shot warn), so the 1 ms delay resolves to ~1–2 ms instead of the ~15.6 ms default timer tick; `SetPacketEvent` event-driven reads are the optional next upgrade if empty-to-first-packet latency still matters.

---

## TCP Local Redirect: WinpkFilter local_redirect transform (hardware-verified 2026-08-08, Win11)

The official WinpkFilter transparent-TCP-redirect pattern (`ndisapi::local_redirector`, used by socksify/ProxiFyre) is NOT "rewrite dst to loopback + SendToMstcp". It is:

1. Swap Ethernet src/dst MACs.
2. Swap IP src/dst.
3. Rewrite `th_dport` to the local proxy port. **The client's source port (`th_sport`) MUST be preserved** — the redirector only rewrites th_dport. The local proxy server's accepted connection then has peer = server_ip:client_orig_port, which the per-flow mapping resolves by client source port.
4. Recompute IP + TCP checksums (the pseudo-header uses the swapped addresses).
5. The listener binds `0.0.0.0:proxy_port` (all interfaces), not loopback — the rewritten packet's destination is the client's own IP address + proxy port.

Attempting dst=loopback + SendToMstcp produced a byte-correct frame (verified checksums) that MSTCP silently ignored — the local-redirect contract requires the IP-swap form.

### Reverse path (the subtle part)

The SYN-ACK that MSTCP emits in response to an injected (`SendPacketsToMstcp`) SYN is a reverse packet (source port = proxy port). It must be recognized and reversed BEFORE flow-table lookup and policy evaluation:

- A dispatcher-level reverse hook (`TcpProxyCoordinator.HandleReverseIfApplicableAsync`, wired as `FlowDispatcher._reverseHandler`) runs right after the self-traffic check. If the packet's local/remote port matches a proxy listener port, it is reversed (`src -> original server:port, dst -> original client:port`, MACs swapped) and injected toward MSTCP. This prevents the reverse packet from being re-evaluated as a new client flow (which policy would silently `pass`, killing the handshake).
- Without the hook, the reverse packet is evaluated by policy (process attribution can't match the injected tuple) and passed straight to the wire — the client never receives its SYN-ACK and the connection times out. This was the dominant failure mode during bring-up.
- **LoopbackFilter (0x20) is NOT required** for this reverse path: the hook catches the reverse packet on the normal capture path. (Loopback filtering was investigated; enabling it caused the injected SYN's loopback reflection to be re-captured, which then had to be drained.)

### Mid-flow data

After the handshake, client -> listener data on the original flow must also be rewritten to the proxy tuple and reinjected (`ReinjectExistingFlowDataAsync`: same swap, dst -> proxy port). A flow with an active redirect association is recognized by `TryResolveByOriginal`; anything else is not ours.

### Redundant accepts

A retransmitted SYN can make MSTCP open a second connection on the same listener. After the first relay is established, further accepts must be drained and closed immediately (`DrainRedundantConnectionsAsync`) rather than starting a second relay — otherwise every extra accept fails with SocketException and the log floods.

**Reference**: `src/WinForward.Runtime/TcpProxyCoordinator.cs` (HandleSynAsync/HandleReverseAsync/ReinjectExistingFlowDataAsync/HandleReverseIfApplicableAsync/DrainRedundantConnectionsAsync), `TcpRedirectListener.cs` (0.0.0.0 bind), `FlowDispatcher.cs` (`_reverseHandler`).

### Forwards-direction reverse injection (hardware-verified complement)

Reverse-packet injection direction follows the flow origin:

- Host-originated flow (client on this host): the reversed packet goes to MSTCP (`SendToMstcp`).
- Forwarded flow (client behind a VM/remote adapter): the reversed packet must go back to the origin adapter (`SendToAdapter`), not MSTCP.

`TcpRedirectInjector.InjectAsync(frame, towardMstcp, adapterHandle, ct)` selects the direction; the coordinator passes `association.OriginalKey.Origin == FlowOriginKind.Host`. Locked by `ForwardedFlowReverseInjectsTowardOriginAdapter` / `HostFlowReverseInjectsTowardMstcp` in `TcpProxyCoordinatorTests`.

> **Note**: the current Win11 test host has no Hyper-V VM stack (the "Microsoft Hyper-V Network Adapter" interfaces exist but no vSwitch/VM is present), so guest-originated forwarded traffic cannot be exercised end-to-end here. The forwarded code path is unit-locked; a host with a real guest VM is required for the hardware matrix.

### Forwarded flows use the DNAT-to-local transform (fixed 2026-08-14)

The WinpkFilter IP-swap transform above is only valid for **host-originated** flows. Applied to a forwarded SYN it produces dst = client-ip:listener-port, which is not a local address — MSTCP routes the frame back out to the client, the listener never sees a SYN, and the flow hangs (observed on the 192.168.77.x gateway: `tcp.relay.started` stayed 0 while mangled frames were passed back to the client). Forwarded flows therefore use a different shape, selected per association by `TcpRedirectAssociation.ForwardLocalAddress`:

- Forward leg (SYN and mid-flow data, `TryRewriteForwardLeg`): **src stays client:client-port; only dst moves to (adapter-local address L, listener port)**. L is resolved per origin adapter via `IAdapterLocalAddressProvider` (wired as `WindowsAdapterLocalAddressProvider`): IPv4 prefers a same-subnet address, IPv6 skips link-local and prefers a /64 prefix match. **Never 127.0.0.1** — the reverse reply from a loopback destination would carry a martian source and can be dropped by the stack before reaching the capture layer for rewriting. No L candidate → fail-closed `Blocked` (`tcp.redirect.rejected reason=localAddress`).
- No MAC swap on the forward leg: the arrival frame's dst MAC already addresses this host. The MAC swap is now gated on `towardMstcp` everywhere (host shape swaps; forwarded never does).
- Table endpoints follow the shape: forwarded `ReverseSource = (L, listener-port)`, `ReverseDestination = AcceptedPeer = (client, client-port)`, so the accept-loop peer validation and `TryResolveByReverse` see the real client tuple.
- Reverse leg is unchanged textually (`src -> original server:port, dst -> original client:port`) and still injects to the origin adapter; the association's origin-shaped endpoints make the same rewrite call correct for both shapes.
- The wildcard self-traffic registration `(Tcp, 0.0.0.0:P, 0.0.0.0:P)` cannot match the forwarded SYN-ACK `(L:P -> client:port)` because the remote leg must equal the observed remote exactly — the reverse hook still owns that packet.
- Locked by `ForwardedFlowSynRewritesTowardAdapterLocalListener`, `ForwardedFlowWithoutLocalAddressFailsClosed`, `ForwardedFlowAcceptsClientTuplePeer`, and the rewritten `ForwardedFlowReverseInjectsTowardOriginAdapter`.

### Relay setup failure resets the client (fixed 2026-08-14)

When the SOCKS5 relay cannot be established after a successful redirect (proxy down, auth failure, upstream unreachable), the client's connection is already established through the redirect leg and would otherwise hang. The coordinator records the client ISN (+ a bounded copy of the original SYN) at setup and the server ISN when the reverse SYN-ACK passes the hook; on relay failure it crafts a standalone RST|ACK (`TcpResetBuilder`, fresh checksums, MACs mirrored from the SYN template) with seq = server-ISN + 1 — in-window for the client's established state — and injects it toward MSTCP (host) or the origin adapter (forwarded) before tearing the session down. Missing sequence numbers degrade to plain teardown. Locked by `RelaySetupFailureInjectsClientResetWhenSequencesKnown` / `ForwardedRelayFailureInjectsClientResetTowardOriginAdapter` / `RelaySetupFailureBlocksAndReleasesAlias` (degradation) and `TcpResetBuilderTests`.

### Non-flow frames always pass (fixed 2026-08-14)

Frames that cannot be classified as TCP/UDP flows (ARP, ICMPv6 ND, other L2/L3, unparseable, fragments) are never policy-evaluated: `FlowDispatcher.DispatchNonFlowAsync` passes them unconditionally after the self-traffic check. Policy exists to govern proxyable flows; blocking non-flow frames under a catch-all proxy/block rule silently broke ARP resolution and produced total L2 failure on the gateway (26 `packet.dropped reason=policy`, all nonFlow, 23 of them 42-byte ARP frames, while test traffic never even reached the capture layer). Locked by `DispatcherForwardedNonFlowAlwaysPassesRegardlessOfRules` / `DispatcherHostNonFlowAlwaysPassesRegardlessOfFallbackAndRules`.

### Deployment: Windows Firewall inbound rule is required (hardware-verified 2026-08-15)

Every redirect path terminates at a local listener socket, and the injected SYN is an unsolicited inbound TCP connection from the stack's perspective. On adapters whose network profile applies the default inbound block (typically Public), the Windows Firewall silently drops that SYN before it reaches TCP: `tcp.redirect.created` appears, no SYN-ACK ever leaves, and the flow hangs. Forwarded DNAT injections onto Private/unidentified-profile virtual adapters passed by default, which is why the failure only showed on the WLAN (Public) host path. Symptom trio: `tcp.redirect.created` present, `tcp.relay.started` absent, zero captures for the listener port. Diagnose with `Set-NetFirewallProfile -All -LogBlocked True` + `pfirewall.log` (DROP to the listener port) and `Get-NetTCPConnection -State SynReceived` (empty). Remedy is a deployment rule, not code: `New-NetFirewallRule -Direction Inbound -Action Allow -Program "<path>\WinForward.exe" -Profile Any`.

### Policy authoring for gateway LANs (hardware-verified 2026-08-15)

Because forwarded flows evaluate only adapter-qualified rules (see "Host vs forwarded policy domains"), a plain `remoteCidr`-only pass rule can never exempt LAN traffic that arrives on a bridged adapter — and a host flow egressing the bridge adapter DOES match `adapterId` rules, so rule ORDER decides whether LAN traffic is proxied (a remote upstream cannot reach 192.168.x and the connection blackholes after the redirect handshake). The working pattern is a combined rule placed before the adapter-proxy rule; matchers are AND-combined and the rule stays adapter-qualified: `{ "adapterId": [...], "remoteCidr": [LAN prefixes], "action": "pass" }`. It exempts both host-origin and forwarded LAN traffic without any `EvaluateForwarded` change.

### UDP relay wiring (wired 2026-08-09, hardware pass pending)

- A proxy-decided UDP datagram is parsed for its payload (`IpUdpPacket.TryParse`) and handed to `UdpProxyCoordinator.TrySendAsync`; the original frame is consumed (never reinjected) — the SOCKS5 UDP relay transport owns forwarding.
- SOCKS5 UDP responses arrive from the dynamic relay endpoint; `UdpResponseReinjector` (an `IUdpResponseSink`) rebuilds a complete Ethernet II + IPv4/IPv6 + UDP frame (`UdpFrameBuilder`, RFC 768 0→0xFFFF checksum inversion) with the real server as source and `originalFlow.Local` as destination, then injects toward MSTCP (host flow) or the origin adapter (forwarded flow).
- Relay-source validation is port + address-family (not exact `IPEndPoint`): `Socks5UdpTransport.ReceiveAsync` accepts a datagram whose source port equals the relay port and whose family matches the relay, even from a different IP (multi-homed/anycast relay); a different port or family is still rejected. IPv6 scope is deliberately not compared (the receive interface's scope legitimately differs from the relay's advertised scope). Re-tightening to exact-address equality would break multi-homed relays (RFC 1928 does not pin the reply source).
- The response reinjector needs the NDISAPI enumeration handle + the host adapter MAC (from NDISAPI `CurrentAddress`); the MAC is used for both src and dst on host flows.
- **Forwarded flow destination MAC** (fixed 2026-08-15): a forwarded (Hyper-V/VM) flow's response must NOT use the adapter's own MAC as destination — the vSwitch would deliver it to the host, never the VM. The client (VM) MAC is recorded at session creation (`NdisPacketActionExecutor` extracts the Ethernet src MAC from bytes 6..11 of the first proxied frame, before `IpUdpPacket.TryParse` is known-good; `UdpProxySession.ClientMac` is set once, get-only) and plumbed through `IUdpResponseSink.InjectAsync(..., byte[]? clientMac, ...)` to `UdpResponseReinjector`, which uses it as the destination MAC (src = origin adapter MAC) for forwarded flows only. Missing/≠6-byte client MAC on a forwarded flow → fail-closed drop with rate-limited warn (`LogMissingClientMac`, 5s interval, same pattern as missing-origin-adapter). TCP reverse legs are unaffected — they reuse captured frames whose MACs are already correct. Locked by `ForwardedFlowResponseInjectsTowardOriginAdapter` (dst == client MAC) / `ForwardedFlowResponseWithoutValidClientMacIsDroppedFailClosed` / `RelayResponseCarriesRecordedClientMac`.
- **Host-flow response adapter binding** (fixed 2026-08-27, hardware-verified): `SendPacketToMstcp` is adapter-bound. A host UDP response must resolve `FlowKey.OriginAdapterId` through the capture-scope `UdpAdapterTarget` map and use that target's enumeration handle and MAC for both Ethernet header slots. The startup-selected `scope[0]` target is only a compatibility fallback when the origin adapter is absent from the current map; that fallback emits a rate-limited warning. Forwarded behavior is unchanged: an unresolved origin still drops fail-closed and a resolved origin still injects toward the adapter with the recorded client MAC as destination.

  Hardware proof (Win11 guest with two Hyper-V adapters, default route on External while GUID sort made Internal the `scope[0]`): the pre-fix build reinjected every host UDP response with Internal's MAC on both slots and the frame was dropped by tcpip with `DropReason "Not locally destined"` (strong-host receive validation — the destination IP 192.168.77.2 belongs to External, not the indicating interface). pktmon captured the drop location `0xE0004136` while the WinForward trace showed `udp.response.reinjected target=mstcp` completing — reinjection executes; delivery dies in the IP layer. 0/20 queries succeeded pre-fix; the origin-adapter fix delivers 20/20 plus 5/5 on a 1-second-settle restart, first attempt included, with zero warnings and one UDP session per query.

  | Flow/adapter condition | Required response action |
  |---|---|
  | Host + origin stable ID resolves | `SendToMstcp(origin.Handle)`; src/dst MAC = `origin.Mac` |
  | Host + origin stable ID missing/unresolved | warn (rate-limited), then `SendToMstcp(fallback.Handle)` |
  | Forwarded + origin resolves + valid client MAC | `SendToAdapter(origin.Handle)`; dst MAC = client MAC |
  | Forwarded + origin unresolved or client MAC invalid | drop fail-closed |

  Good: a WLAN-originated host query is reinjected to MSTCP on the WLAN enumeration handle even when another adapter sorts first in capture scope. Base: a host adapter disappears mid-flow, so the response uses the startup fallback and records a warning. Bad: choosing `scope[0]` for every host response; the indication is attached to an unrelated interface and can fail Windows adapter/source-address validation.

  Required tests: `HostFlowResponseInjectsTowardItsOriginAdapter` asserts handle, ON_RECEIVE flag, and both MAC slots; `HostFlowWithUnresolvedOriginAdapterUsesFallbackAndWarns` asserts fallback injection and warning; forwarded response tests continue to assert `SendToAdapter`, ON_SEND, and client destination MAC.

  ```csharp
  // Wrong: startup ordering is unrelated to the flow's capture adapter.
  reinjector.SendToMstcp(scope[0].RuntimeHandle, buffer);

  // Correct: resolve the stable adapter identity carried by the flow.
  var target = adaptersByStableId[flow.OriginAdapterId!];
  reinjector.SendToMstcp(target.Handle, buffer);
  ```
- Loop prevention: `Socks5UdpTransport` registers `(Udp, localSocketEndpoint, relayEndpoint)` in `SelfTrafficRegistry` when `UDP ASSOCIATE` returns the dynamic relay endpoint, and releases the token on dispose — catch-all proxy rules never recursively intercept WinForward's own UDP relay traffic. The factory takes the registry.

### SOCKS5 control-socket timeout lifecycle (fixed 2026-08-15)

- `Socks5ControlConnection.ConnectOnceAsync` sets `socket.ReceiveTimeout = socket.SendTimeout = 30s` as the per-attempt connect/authenticate timeout. In .NET, async socket reads/writes honor these timeouts, so any socket handed to a long-lived consumer keeps that 30s ceiling.
- `GetUpstreamStream()` (the `TcpProxyRelay` handoff point) MUST reset both to `Timeout.Infinite` before returning the stream — otherwise an idle relay connection dies at 30s via `SocketException(TimedOut)`, defeating the relay's own 30-minute stall window (M4). The CONNECT command (`ConnectDestinationAsync`) runs BEFORE `GetUpstreamStream()`, so the per-attempt window still governs setup. The UDP control socket never goes through `GetUpstreamStream()` and has no post-associate operations, so it is intentionally left unchanged.
- Relay-side idle protection is owned by `TcpProxyRelay.PumpAsync`'s per-operation write/read timeout CTS (30 minutes); the socket-level timeouts are only for the bounded setup phase. Locked by `UpstreamStreamClearsPerAttemptSocketTimeouts` (asserts `ReceiveTimeout == -1 && SendTimeout == -1` after handoff; note .NET reads a disabled timeout back as 0 on Linux and -1 on Windows, so assert `<= 0`).

### UDP relay hardware findings (Win11, 2026-08-09)

- A UDP proxy flow's reverse datagram (server -> client, the reinjected relay response) MUST be delivered to the local client, not re-proxied back to the relay. The dispatcher detects a reverse-of-stored-key packet on a proxied UDP flow and passes it; without this the response loops forever creating new UDP ASSOCIATEs.
- Self-traffic registry must wildcard-match a socket bound to Any/IPv6Any by port + remote, because an outgoing datagram's source IP is chosen by routing, not the bind address (the UDP relay socket binds 0.0.0.0). Exact tuple matching silently misses it and the relay traffic recurses.
- Known test-harness artifact: a local SOCKS5 server's own forwarded queries (its forwarder target sockets) are re-caught and re-proxied by WinForward, causing an ASSOCIATE storm. Self-traffic protects WinForward's own sockets, not the SOCKS5 server's. A remote SOCKS5 server avoids this entirely; the clean hardware proof on this host is short single queries (nslookup) that are answered before re-interception matters.

### Loop prevention: control-connection registration happens BEFORE the SYN (fixed 2026-08-12)

- **Both TCP `CONNECT` and UDP `ASSOCIATE` control connections are SOCKS5 traffic to the proxy endpoint and must be registered in `SelfTrafficRegistry` before their SYN leaves the host.** A catch-all proxy rule would otherwise capture WinForward's own control SYN and recurse until the bounded session capacity is exhausted.
- `Socks5ControlConnection.ConnectAsync` binds the socket to a wildcard local endpoint first, invokes an `onSocketReady(local, remote)` callback (which returns the loop-prevention token) BEFORE `socket.ConnectAsync`, and owns/disposes the token with the connection. Registering after connect leaves a race where the SYN is already observable.
- The registered tuple is `(Tcp, Any:bound_port, proxy_ip:socks_port)`: the socket is bound to wildcard, so the local address is Any and the wildcard matcher covers the routing-chosen source IP, exactly like the UDP relay socket. Registering `(proxy, proxy)` is a bug — it can never match the observed `(host:ephemeral, proxy:socks)` and silently disables loop prevention.
- A failed connection attempt (DNS multi-address fallback) disposes the registration of that attempt before trying the next address.
- Locked by `SelfTrafficUpstreamTcpTupleIsOwnedWhenObservedAsHostEphemeralToProxy` (forward + reverse owned; an unrelated app sharing the proxy endpoint with its own source port is NOT exempted).

### Idle expiry sweep (wired 2026-08-12)

- `FlowTable.RemoveExpired`, `TcpRedirectTable.RemoveExpired`, and `UdpAssociationTable.RemoveExpired` implement idle expiry but had no caller; UDP sessions accumulated to the bounded capacity and then failed closed.
- `IdleExpirySweeper` (Runtime) is the single wiring point: started in `Program.RunCaptureLoopAsync`, it periodically sweeps `FlowDispatcher.RemoveExpiredFlows`, `TcpProxyCoordinator.RemoveExpiredAsync`, and `UdpProxyCoordinator.RemoveExpiredAsync`. It is disposed (await-using, reverse order) after the capture runtime stops and before the coordinators are released.
- `UdpProxySession` tracks `LastActivityUtc` on send and receive; `UdpProxyCoordinator.RemoveExpiredAsync` disposes idle sessions and releases their `UdpAssociationTable` entries. The relay-alias collision guard (`UdpAssociationTable` claim per flow, design §8) is now live: a second flow claiming the same relay alias is rejected fail-closed.
- Sweep failures are isolated (RCS1075 suppression, same pattern as `CaptureLifecycle.cs`) so a transient teardown error cannot stop the capture loop.

### Native DLL resolution (fixed 2026-08-12)

- `NdisApiNative` registers a `NativeLibrary.SetDllImportResolver` in its static constructor that loads `ndisapi.dll` only from `AppContext.BaseDirectory` — matching the README claim; there is no PATH/current-directory search. Other library names return zero and use default resolution. AOT-safe (no reflection).

### Host vs forwarded policy domains (fixed 2026-08-11)

- Policy eligibility is not the same as capture scope. Adapter-unqualified host rules may require all MSTCP-bound adapters to remain captured, so forwarded eligibility is enforced after self-traffic, TCP reverse handling, and existing-flow resolution, immediately before a genuinely new flow is claimed.
- New `Host` flows evaluate the complete ordered rule list and use the configured `fallbackAction`.
- New `Forwarded` flows evaluate only rules containing `adapterId` and/or `adapterName`, preserving original rule order and every additional matcher condition. If none match, they pass independently of adapter-unqualified rules and `fallbackAction`.
- The same adapter-qualified-only/default-pass contract applies to forwarded packets that cannot be classified as TCP/UDP flows. Host non-flow behavior remains unchanged. **Superseded 2026-08-14**: non-flow frames (non-IP, non-TCP/UDP, unparseable, fragmented) now always pass on both origins without any policy evaluation — blocking them broke ARP/ICMPv6-ND and silently severed L2 (see "Non-flow frames always pass").
- The implicit forwarded pass is cached in `FlowTable`; reverse and cross-adapter observations reuse it before origin-specific policy can run again. Flow-table capacity exhaustion still fails closed.
- `Forwarded` is derived from NDIS `ON_RECEIVE`, not an authoritative Windows routing decision. It includes both traffic Windows may route across adapters and new inbound traffic addressed to a service on the host.
- Regression coverage: `ForwardedPolicySkipsUnqualifiedRulesAndConfiguredFallback`, `DispatcherSeparatesForwardedAdaptersFromHostCatchAllPolicy`, `DispatcherCachesForwardedImplicitPassAcrossOriginsAndFailsClosedAtCapacity`; non-flow pass coverage moved to `DispatcherForwardedNonFlowAlwaysPassesRegardlessOfRules` / `DispatcherHostNonFlowAlwaysPassesRegardlessOfFallbackAndRules` (see "Non-flow frames always pass").

### Cross-family SOCKS5 UDP relay setup (fixed 2026-08-11)

#### 1. Scope / Trigger

- Trigger: creating a SOCKS5 UDP relay for an IPv4 or IPv6 original flow when
  the SOCKS5 control endpoint and returned UDP relay may use a different
  address family.

#### 2. Signatures

- `Socks5ControlConnection.UdpAssociateAsync(CancellationToken)` sends the
  UDP ASSOCIATE request without a caller-provided local endpoint.
- `IUdpProxyTransportFactory.CreateAsync(Socks5Server, CancellationToken)` and
  `Socks5UdpTransport.CreateAsync(Socks5Server, SelfTrafficRegistry,
  CancellationToken)` do not accept the original flow's address family.

#### 3. Contracts

- UDP ASSOCIATE sends `0.0.0.0:0` for an IPv4 control socket or
  `[::]:0` for an IPv6 control socket. This is sent after control connection
  authentication and before UDP socket allocation.
- The returned BND/relay endpoint is authoritative for the UDP socket's
  address family. Allocate and bind the UDP socket in that family, then build
  the relay alias only after local and relay endpoints are same-family.
- Register `(Udp, local UDP endpoint, relay endpoint)` in `SelfTrafficRegistry`
  before the first relay datagram. The TCP control tuple remains registered
  before its SYN through the control connection's existing `onSocketReady`
  contract.
- `Socks5UdpCodec.Encode` preserves the original destination's IPv4/domain/
  IPv6 ATYP independently of the relay socket family. A valid IPv4 relay can
  therefore carry an IPv6 destination ATYP.
- Proxy setup errors remain fail-closed. Socket, UDP self-traffic token, and
  control connection are each released when owned; disposal continues through
  later resources if an earlier disposal throws.

#### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| IPv4 control + IPv4 relay | bind IPv4 UDP socket and send IPv4 ATYP as requested |
| IPv6 control + IPv6 relay | bind IPv6 UDP socket and send IPv6 ATYP as requested |
| IPv4 relay + IPv6 destination | bind IPv4 UDP socket; send IPv6 ATYP unchanged |
| IPv6 relay + IPv4 destination | bind IPv6 UDP socket; send IPv4 ATYP unchanged |
| relay response family differs from original flow | do not throw from `FlowKey.Create`; alias uses same-family transport endpoints |
| control/associate/socket/registration failure | release acquired resources and block the proxy-selected flow |

#### 5. Good/Base/Bad Cases

- Good: loopback IPv4 SOCKS5 control and UDP relay receives an all-zero
  ASSOCIATE and a UDP frame whose destination ATYP is IPv6.
- Base: matching-family IPv4 and IPv6 control/relay integration tests remain
  green.
- Bad: allocate UDP socket from `flow.Local` before ASSOCIATE; an IPv6 local
  endpoint plus IPv4 relay produces `ArgumentException` from `FlowKey.Create`.

#### 6. Tests Required

- Assert exact all-zero IPv4 and IPv6 ASSOCIATE request bytes.
- Assert loopback IPv4 relay receives IPv6 destination ATYP/address/payload and
  that relay self-traffic ownership exists before send and is removed after
  disposal.
- Assert an injected socket-disposal exception still releases the UDP token and
  control connection.
- Preserve coordinator same-flow reuse, relay collision, capacity, failure,
  response routing, and matching-family coverage.

#### 7. Wrong vs Correct

```csharp
// Wrong: original destination/flow family is not the relay transport family.
var socket = new Socket(flowFamily, SocketType.Dgram, ProtocolType.Udp);
var relay = await control.UdpAssociateAsync(...);

// Correct: discover relay first, then use its family for the transport socket.
var relay = await control.UdpAssociateAsync(cancellationToken);
var socket = new Socket(relay.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
```

---

## Batched capture reads and buffer pooling (wired 2026-08-27)

### 1. Scope / Trigger

- Trigger: any change to the capture pump read loop, the frame copy path into `PacketLease`, TCP in-place rewrite, or `NdisPacketBuffer` allocation on the injection path.
- Infra contract: the NDISAPI batched ABI shape, the gate lease granularity, and the pooled-frame lifetime boundary across `WinForward.NdisApi` → `WinForward.Core` → `WinForward.Runtime`.

### 2. Signatures

- `NdisApiDriver.TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) -> int` — queue query + batched read merged into a single `NdisNativeCallGate` lease; returns the driver-filled success count (0 = empty queue).
- `NdisApiDriver.SendPacketsToMstcp/SendPacketsToAdapter(nint, NdisPacketBuffer[], int count)` — batched send overloads (declared and wrapped; the pump does not batch sends yet).
- `PacketLease(ReadOnlyMemory<byte> frame, Action<ReadOnlyMemory<byte>>? onCompleted)` — completion-callback constructor; the plain constructor keeps null semantics.
- `NdisPacketBufferPool` (process-wide `Shared`, capacity 256) with `Rent()`/`Return()`; `NdisPacketBuffer` carries an owner-pool state machine (private ctor → Dispose frees; pool-rented → Dispose returns, double-Dispose no-op).

### 3. Contracts

- **ETH_M_REQUEST ABI** (x64, Pack=1): `hAdapterHandle(8) + dwPacketsNumber(4,in) + dwPacketsSuccess(4,out) + NDISRD_ETH_Packet[N]` (one `INTERMEDIATE_BUFFER*` each, 8 bytes) = 16 + 8N total. Caller fills handle/count/buffer pointers; the driver fills `dwPacketsSuccess` and each buffer's contents on success. Managed shape is `EthernetMultiRequest` (24-byte fixed head, `FirstBuffer` aliases `EthPacket[0]`) with `AssertManagedX64Layout` size/offset assertions (24; 0/8/12/16). Exports verified against the pinned commit 417b8734 (`ndisapi.vs2012/ndisapi.def`, `include/ndisapi.h:300-302`): `ReadPackets`, `SendPacketsToMstcp`, `SendPacketsToAdapter` (all `BOOL __stdcall (HANDLE, PETH_M_REQUEST)`).
- **Frame lifetime boundary (the load-bearing rule)**: `FlowDispatcher.CompleteAsync` calls `lease.TryComplete(disposition)` BEFORE `execute()`, so every frame consumer (pass injection copy, UDP payload parse, clientMac slice, TCP in-place rewrite, RST template) runs AFTER lease completion but must stay INSIDE `CapturePacketProcessor.ProcessAsync`'s await window. Therefore the `ArrayPool<byte>.Shared.Return` lives in `ProcessAsync`'s outer `finally` — never on the lease completion callback. Any new code that lets a `Frame`/`Memory` slice escape the `ProcessAsync` window (returning it, capturing it in a background task, storing it without copying) reintroduces a use-after-return.
- **Copy-out points are mandatory**: data that must outlive the window is copied synchronously — UDP `clientMac` (`ToArray()` at session creation), SOCKS5 payload (`Encode` copies before any await), the bounded SYN template (recorded before rewrite).
- **In-place rewrite ordering**: `RecordClientSyn`/`RecordServerSynAck` (reads of the original frame) MUST run BEFORE `TryRewriteTcpEndpoints` (write) on the same frame, or the RST template is polluted by rewritten endpoints/MACs. `TryRewriteIpv4Tcp/Ipv6Tcp` keeps the invariant "every parse/validation precedes the first field write; no failure branch after writing begins", so an in-place rewrite either leaves the frame untouched or fully rewrites it. A lease whose `Frame` is not array-backed fails closed (`reason=rewrite`).
- **Pump batching**: batch buffers are pump-private for the pump's lifetime, released exactly once (run-loop exit or dispose); packets within a batch are awaited strictly in index order, so reinjection order matches arrival order. Empty batch keeps the poll-delay pacing.
- **Gate granularity**: one gate lease per batch on the read path (query + read inside the same lease); one gate lease per injected packet remains on the send path until batched sends are adopted.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| Queue query native call fails | throw `Win32Exception` (existing `HasQueuedPackets` semantics) |
| Queue empty (`queuedPacketCount == 0`) | `TryReadPackets` returns 0; pump takes poll delay |
| `ReadPackets` returns FALSE with a non-empty queue | throw `Win32Exception` |
| Driver fills `dwPacketsSuccess` > requested count | clamped to the request count (defensive; `InterpretBatchReadResult`) |
| `buffers` empty or contains null entries | 0 / `ArgumentNullException` (parameter validation) |
| Lease frame not array-backed at an in-place rewrite point | fail-closed `TcpRedirectOutcome.Blocked`, `reason=rewrite` |
| Pool return above capacity (256) or after drain | buffer freed immediately, never double-returned |

### 5. Good/Base/Bad Cases

- Good: a full batch of 32 frames flows through classify → dispatch → in-place rewrite → pool-rented inject with zero per-packet managed allocations beyond the single `ArrayPool` copy and zero native allocs.
- Base: zero-length frame (`ArrayPool.Rent(0)` returns an empty array; safe), partial batch (n < capacity) processed in order.
- Bad: returning the pooled array from a lease `TryComplete` callback — the executor then reads a reused array (torn frame); storing `lease.Frame` in a session without copying and reading it after `ProcessAsync` returns.

### 6. Tests Required

- `NdisApiAbiTests`: batch read error matrix (partial-batch clamp, non-empty failure throws).
- `NdisCapturePumpTests`: in-batch ordering with enumeration-handle stamping, partial batch, empty-batch poll, exactly-once batch-buffer release, capacity/null argument checks.
- `CapturePipelineTests`: completion callback fires exactly once across double `TryComplete` + `Dispose`; `Dispose` is a completion path; plain constructor has no callback; pooled copy is byte-identical at actual length.
- `TcpProxyCoordinatorTests`: `SynRewriteParseFailureLeavesFrameByteIdentical` (parse failure leaves the frame byte-identical, returns Blocked, releases resources) — the in-place-rewrite no-intermediate-state lock.
- `NdisPacketBufferPoolTests`: rent/return round-trip reuse, over-capacity free, 64-thread rent uniqueness, double-Dispose no-op, drain-then-rent, foreign-buffer rejection, private-buffer (pump) dispose semantics regression.

### 7. Wrong vs Correct

#### Wrong

```csharp
// Returning the pooled frame when the lease completes: CompleteAsync runs
// TryComplete BEFORE the executor reads Frame, so another pump's Rent() can
// reuse this array before the pass injection copies it.
var lease = new PacketLease(frame, _ => ArrayPool<byte>.Shared.Return(array));
```

#### Correct

```csharp
// The return point is ProcessAsync's outer finally — the proven boundary of
// the whole dispatch chain. Every Frame consumer closes inside the window.
var pooledFrame = ArrayPool<byte>.Shared.Rent(frameSpan.Length);
try { ... await dispatcher.DispatchAsync(...); }
finally { ArrayPool<byte>.Shared.Return(pooledFrame); }
```

```csharp
// Wrong: record the SYN template after rewriting it in place — the RST
// builder then inherits rewritten endpoints/MACs and the client rejects it.
TryRewriteForwardLeg(frame, ...);
RecordClientSyn(frame.Span, association);

// Correct: read-then-write. The template keeps the original client bytes.
RecordClientSyn(frame.Span, association);
TryRewriteForwardLeg(frame, ...);
```

**Related**: task `08-27-fix-datapath-throughput` (prd/design/implement artifacts hold the full audit tables); the parent `08-27-fix-eof-reset-design-flaws` maps the throughput bottleneck to the RST/EOF frequency symptom.

---

## Redirect teardown grace and flow-hold contracts (wired 2026-08-28)

### 1. Scope / Trigger

- Trigger: any change to TCP redirect teardown, the `NotRelevant → Pass` fallback, flow-table expiry, or the idle sweeper ordering.

### 2. Signatures

- `TcpRedirectTombstoneTable` — dual-key (`FlowKey` forward + reverse tuple) → shared `TombstoneEntry` with `ExpiryUtc`; `TryAdd` (FIFO evict-oldest at capacity), both-direction `TryHit(now)` (hit only while `now < ExpiryUtc`), `RemoveExpired(now)`.
- `TcpRedirectOutcome.Dropped` — a dedicated outcome; **never reuse `Blocked`** for grace drops (executor's `Blocked` path fires `LogProxyUnavailable`, mislabeling grace consumption as proxy failure).
- `FlowTable.RemoveExpired(now, isHeld?)` — optional hold predicate; held entries are skipped **without Touch**, so they expire at their original idle point once the hold lapses.

### 3. Contracts

- **Single tombstone write point**: every teardown entry (relay completion, relay failure, fail-closed, global dispose) funnels through `TcpProxyCoordinator.RemoveAssociationFromTable` — the only caller of `TcpRedirectTable.TryRemove` — and that point also writes the tombstone (grace `TombstoneGracePeriod` = 60 s). Any new teardown path must go through it.
- **Late-packet consumption**: after a forward (`TryResolveByOriginal`) or reverse (`IsReverseCandidate`) miss, the coordinator consults the tombstone before falling back. A hit returns `Dropped`; the executor silently consumes (trace `packet.dropped reason=grace`), and the dispatcher maps reverse-straggler `Dropped` to `ProxyConsumed` — **not** `Block` (which would emit `reason=policy`).
- **The `NotRelevant → Pass` fallback remains solely for connections established before capture started** — those packets are data-plane-identical to late packets, so no timestamp can separate them; only the per-flow tombstone expiry can. Baseline smoke: 113 notrelevant pre-fix → 11 (legitimate pre-existing connections) with 55 grace-dropped post-fix.
- **Flow hold**: `TcpProxyCoordinator.HoldsFlow` = has session ∨ has tombstone. Sweeper order is **tcp → flows → udp** so tombstones/sessions are recycled before flows are evaluated; capacity summary stays at tick end. Held flows are not touched, so a silently-idle relaying flow survives flow-idle expiry and resumes without re-evaluation (no `flow.created`).
- **Capacity**: tombstone capacity derives from the same `tcpFlowCapacity` budget at wiring; tombstones occupy neither `_sessions` nor the `TryClaim` gate.
- UDP is deliberately untouched: sessions rebuild directly on expiry (no handshake → no stray-packet rebound); responses have their own reverse branch.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| Late forward/reverse packet within grace | `Dropped`, silent consume, no reinjection to the real server |
| Late packet after grace expiry | falls back `NotRelevant → Pass` (pre-existing-connection semantics) |
| Tombstone table full | evict oldest entry, add new |
| Relay-held flow reaches flow-idle expiry | skipped, no Touch, no re-evaluation on resume |
| Hold lapses (teardown + grace passed) | flow expires at its original idle point |

### 5. Good/Base/Bad Cases

- Good: client's final ACK after relay completion hits the tombstone and is consumed — no RST rebounds from the real server.
- Base: a packet for a connection torn down 90 s ago (grace lapsed) passes as `NotRelevant` — same as pre-capture traffic.
- Bad: reusing `Blocked` for grace drops (mislabels as proxy-unavailable); holding flows by Touching them (defeats original-idle-point expiry).

### 6. Tests Required

- `TcpRedirectTombstoneTableTests`: window hit / evict-oldest / expiry recycle / rewrite-refresh.
- Coordinator tests: both-direction straggler `Dropped`; grace-expiry fallback to `NotRelevant`; relay-failure writes tombstone; executor silent consume + trace (and no executor `packet.completed` for grace); `HoldsFlow` phases; flow expiry at original idle point after hold lapses (split clocks); real-sweeper silent-flow survival (no `flow.created`).
- Dispatcher regression: reverse-straggler `Dropped` maps to `ProxyConsumed` without a `reason=policy` label.

### 7. Wrong vs Correct

#### Wrong

```csharp
// Grace drop reusing Blocked: executor's Blocked branch logs proxy-unavailable
// and the trace says reason=policy — both mislead diagnosis.
if (tombstone.TryHit(key, now)) return TcpRedirectOutcome.Blocked;
```

#### Correct

```csharp
// Dedicated outcome; executor consumes silently with its own trace reason,
// and the dispatcher maps the reverse-straggler form to ProxyConsumed.
if (Tombstones.TryHitReverse(tuple, now) || Tombstones.TryHitForward(key, now))
    return TcpRedirectOutcome.Dropped;
```

---

## Client-reset sequence tracking and injection-failure exits (wired 2026-08-28)

- **Tracked sequences beat ISN+1**: `TcpRedirectAssociation` observes both directions' `seq + payloadLen` (SYN/FIN each count 1, payload length from IP totalLength — never ethernet frame length, padding pollutes it; IPv6 extension headers deducted) at the two pre-rewrite points, wrap-aware advance-only. `TryInjectClientResetAsync` must use `ClientNextSeq ?? clientInitialSeq+1` (ack) and `ServerNextSeq ?? serverInitialSeq+1` (seq): ISN+1 is out-of-window once the client has sent data and the stack silently discards the RST (slow-EOF symptom). New observation sites must read the frame BEFORE rewrite (original bytes) and synchronously (Span must not cross an await).
- **Every `SendPacketTo*` failure exits through `HandleInjectionFailureAsync`**: warn `tcp.redirect.failed reason=injectionFailure` with nativeError/adapterHandle/flow key → best-effort client RST → `FailAssociationAsync` (tombstone single write point). Free-text catches around injections are forbidden — a silent `FailAssociationAsync` after adapter-handle staleness is exactly the invisible-teardown defect this rule exists to prevent.
- **SOCKS5 relay setup budget**: relay call site passes `maxAttempts: 2, perAttemptTimeout: 10s` (internal constants). The default 30s is per-attempt (worst ~150s over multi-address DNS); after redirect accept the client is already established, so every extra budget second is a "connected then reset" second.
