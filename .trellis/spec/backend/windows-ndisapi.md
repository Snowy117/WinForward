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

- Unit (hardware-independent, via injected `IpAdapterInfo` provider): GUID correlation success; brace/case-insensitive GUID compare; MAC fallback when internal name is not a GUID; bare-GUID internal name; duplicate-MAC interfaces must NOT corrupt GUID correlation; uncorrelated adapters fall back to internal name.
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
- This matches the official samples: `ETH_M_REQUEST.hAdapterHandle` is set once from the adapter list and reused for read/write requests.
- All NDISAPI `[LibraryImport]` declarations use `SetLastError = true`; send-path exceptions must include `Marshal.GetLastWin32Error()` — driver-side rejections are otherwise undiagnosable.
- Diagnostics context worth logging on send failure: native error, frame length, device flags, adapter handle.

**Symptom / Cause / Fix** (recorded as a verified bug class):

- Symptom: capture works, tunnel mode applies, but every pass reinjection fails with native error 87 and the runtime shuts down fail-closed.
- Cause: request built with the captured buffer's `m_hAdapter` instead of the enumeration handle.
- Fix: `NdisCapturedPacket(buffer, _adapterHandle, buffer.DeviceFlags)` in the pump.
- Verification: scratch harness `sendtest` (read -> reinject captured buffer with enumeration handle: 97/97 OK, both directions). Product re-verified: 100/100 ICMP pass-through with 0% loss and no duplicates.

**Performance note**: the current polling pump (`ReadPacket` + 1 ms delay) adds ~5-15 ms RTT under tunnel mode (observed avg 8 ms vs 0.6 ms direct). Event-driven reads (`SetPacketEvent` + `ReadPackets` batch, as in `simple_packet_filter`) are the documented upgrade path when throughput work starts.

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

### UDP relay wiring (wired 2026-08-09, hardware pass pending)

- A proxy-decided UDP datagram is parsed for its payload (`IpUdpPacket.TryParse`) and handed to `UdpProxyCoordinator.TrySendAsync`; the original frame is consumed (never reinjected) — the SOCKS5 UDP relay transport owns forwarding.
- SOCKS5 UDP responses arrive from the dynamic relay endpoint; `UdpResponseReinjector` (an `IUdpResponseSink`) rebuilds a complete Ethernet II + IPv4/IPv6 + UDP frame (`UdpFrameBuilder`, RFC 768 0→0xFFFF checksum inversion) with the real server as source and `originalFlow.Local` as destination, then injects toward MSTCP (host flow) or the origin adapter (forwarded flow).
- The response reinjector needs the NDISAPI enumeration handle + the host adapter MAC (from NDISAPI `CurrentAddress`); the MAC is used for both src and dst on host flows.
- Loop prevention: `Socks5UdpTransport` registers `(Udp, localSocketEndpoint, relayEndpoint)` in `SelfTrafficRegistry` when `UDP ASSOCIATE` returns the dynamic relay endpoint, and releases the token on dispose — catch-all proxy rules never recursively intercept WinForward's own UDP relay traffic. The factory takes the registry.

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
