# UDP Relay Contracts

> How WinForward proxies UDP flows through a SOCKS5 UDP relay: datagram handoff, response reinjection, loop prevention, and cross-family relay setup. Split 2026-08-29 from the former monolithic NDISAPI file; NDISAPI transport basics live in [windows-ndisapi.md](./windows-ndisapi.md).

---

## UDP relay wiring (wired 2026-08-09, hardware pass pending)

- A proxy-decided UDP datagram is parsed for its payload (`IPUdpPacket.TryParse`, `src/WinForward.Protocols/IPUdpPacket.cs`) and handed to `UdpProxyCoordinator.TrySendAsync` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`); the original frame is consumed (never reinjected) — the SOCKS5 UDP relay transport owns forwarding.
- SOCKS5 UDP responses arrive from the dynamic relay endpoint; `UdpResponseReinjector` (an `IUdpResponseSink`, both in `src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs`) rebuilds a complete Ethernet II + IPv4/IPv6 + UDP frame (`UdpFrameBuilder`, `src/WinForward.Protocols/UdpFrameBuilder.cs`, RFC 768 0→0xFFFF checksum inversion) with the real server as source and `originalFlow.Local` as destination, then injects toward MSTCP (host flow) or the origin adapter (forwarded flow).
- Relay-source validation is port + address-family (not exact `IPEndPoint`): `Socks5UdpTransport.ReceiveAsync` accepts a datagram whose source port equals the relay port and whose family matches the relay, even from a different IP (multi-homed/anycast relay); a different port or family is still rejected. IPv6 scope is deliberately not compared (the receive interface's scope legitimately differs from the relay's advertised scope). Re-tightening to exact-address equality would break multi-homed relays (RFC 1928 does not pin the reply source).
- The response reinjector needs the NDISAPI enumeration handle + the host adapter MAC (from NDISAPI `CurrentAddress`); the MAC is used for both src and dst on host flows.
- **Forwarded flow destination MAC** (fixed 2026-08-15): a forwarded (Hyper-V/VM) flow's response must NOT use the adapter's own MAC as destination — the vSwitch would deliver it to the host, never the VM. The client (VM) MAC is recorded at session creation (`NdisPacketActionExecutor`, `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs`, extracts the Ethernet src MAC from bytes 6..11 of the first proxied frame, after `IPUdpPacket.TryParse` succeeds; `UdpProxySession.ClientMac` is set once, get-only) and plumbed through `IUdpResponseSink.InjectAsync(..., byte[]? clientMac, ...)` to `UdpResponseReinjector`, which uses it as the destination MAC (src = origin adapter MAC) for forwarded flows only. Missing/≠6-byte client MAC on a forwarded flow → fail-closed drop with rate-limited warn (`LogMissingClientMac`, 5s interval, same pattern as missing-origin-adapter). TCP reverse legs are unaffected — they reuse captured frames whose MACs are already correct. Locked by `ForwardedFlowResponseInjectsTowardOriginAdapter` (dst == client MAC) / `ForwardedFlowResponseWithoutValidClientMacIsDroppedFailClosed` / `RelayResponseCarriesRecordedClientMac`.
- **Host-flow response adapter binding** (fixed 2026-08-27, hardware-verified): `SendPacketToMstcp` is adapter-bound. A host UDP response must resolve `FlowKey.OriginAdapterId` through the capture-scope `UdpAdapterTarget` map (`UdpResponseReinjector`) and use that target's enumeration handle and MAC for both Ethernet header slots. The startup-selected `scope[0]` target is only a compatibility fallback when the origin adapter is absent from the current map; that fallback emits a rate-limited warning. Forwarded behavior is unchanged: an unresolved origin still drops fail-closed and a resolved origin still injects toward the adapter with the recorded client MAC as destination.

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
- **Session activity accounting is two-tier** (task 08-29-udp-throughput-loss D3): `UdpProxySession` updates `_lastActivityTicks` on *every* send and receive (Interlocked, exact — idle-expiry decisions in `TryBeginExpiry` read this exact value), while propagation to the association-table observer (`UdpAssociationTable` touch) is throttled to at most once per `ActivityPropagationInterval` (100ms) per session via Interlocked CAS; the first activity after creation or after an interval propagates immediately. The table serves reverse-leg classification and sweep pruning on seconds-scale timeouts, so 100ms granularity is unobservable there; per-datagram table touches were pure overhead. Do not throttle the timestamp itself — that would delay expiry.
- **Relay sockets disable `SIO_UDP_CONNRESET`, and `ConnectionReset` is a skip, not a failure** (task 08-29-proxy-stability-perf S2, 2026-08-29): on Windows an ICMP port-unreachable for a destination the relay socket wrote to surfaces as `SocketException(ConnectionReset)` on the next receive, which used to tear the session down (churn: re-ASSOCIATE per cycle under a noisy path). Two layers, both required: (1) `Socks5UdpTransport.CreateAsync` issues the vendor IOCTL `0x9800000C` with a 4-byte `FALSE` **before bind** via an internal seam — the default implementation is guarded by `OperatingSystem.IsWindows()` because Linux `Socket.IOControl` throws `PlatformNotSupportedException` (test seam asserts the call on any OS); (2) `ConnectionReset` is classified as a skip at both the transport layer (`ClassifyReceiveFault`) and the session receive loop — counted in the 5 s skip summary (`connectionReset=`), loop survives, session reusable. All other socket errors remain fatal. Locked by `Socks5UdpConnresetTests` + `UdpReceiveResilienceTests`.
- **Skip-summary counters cover silent drop shapes** (same task S6): domain-typed relay responses (`DestinationAddress == null` after decode) are dropped but **counted** (`domainDestination=` in the 5 s rate-limited debug summary) instead of vanishing silently; the executor's per-datagram UDP failure warn is rate-limited (5 s window, check-first so suppressed calls allocate nothing).

---

## UDP relay hardware findings (Win11, 2026-08-09)

- A UDP proxy flow's reverse datagram (server -> client, the reinjected relay response) MUST be delivered to the local client, not re-proxied back to the relay. The dispatcher detects a reverse-of-stored-key packet on a proxied UDP flow and passes it; without this the response loops forever creating new UDP ASSOCIATEs.
- Self-traffic registry must wildcard-match a socket bound to Any/IPv6Any by port + remote, because an outgoing datagram's source IP is chosen by routing, not the bind address (the UDP relay socket binds 0.0.0.0). Exact tuple matching silently misses it and the relay traffic recurses.
- Known test-harness artifact: a local SOCKS5 server's own forwarded queries (its forwarder target sockets) are re-caught and re-proxied by WinForward, causing an ASSOCIATE storm. Self-traffic protects WinForward's own sockets, not the SOCKS5 server's. A remote SOCKS5 server avoids this entirely; the clean hardware proof on this host is short single queries (nslookup) that are answered before re-interception matters.

---

## Cross-family SOCKS5 UDP relay setup (fixed 2026-08-11)

### 1. Scope / Trigger

- Trigger: creating a SOCKS5 UDP relay for an IPv4 or IPv6 original flow when
  the SOCKS5 control endpoint and returned UDP relay may use a different
  address family.

### 2. Signatures

- `Socks5ControlConnection.UdpAssociateAsync(CancellationToken)` (`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`) sends the
  UDP ASSOCIATE request without a caller-provided local endpoint.
- `IUdpProxyTransportFactory.CreateAsync(Socks5Server, CancellationToken)` and
  `Socks5UdpTransport.CreateAsync(Socks5Server, SelfTrafficRegistry,
  CancellationToken)` (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`) do not accept the original flow's address family.
- Module boundary: the transport seam (`IUdpProxyTransport`/`IUdpProxyTransportFactory` and their receive-result vocabulary `Socks5UdpReceiveResult`/`Socks5UdpReceiveSkipReason`) lives in `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` and is UdpProxy's sanctioned cross-group edge into Socks5 (mirroring TcpRedirect→Socks5); the datagram wire codec itself (`Socks5UdpDatagram`, `Socks5UdpCodec`) lives in `src/WinForward.Protocols/Socks5Udp.cs`.

### 3. Contracts

- UDP ASSOCIATE sends `0.0.0.0:0` for an IPv4 control socket or
  `[::]:0` for an IPv6 control socket. This is sent after control connection
  authentication and before UDP socket allocation.
- The returned BND/relay endpoint is authoritative for the UDP socket's
  address family. Allocate and bind the UDP socket in that family, then build
  the relay alias only after local and relay endpoints are same-family.
- Register `(Udp, local UDP endpoint, relay endpoint)` in `SelfTrafficRegistry`
  before the first relay datagram. The TCP control tuple remains registered
  before its SYN through the control connection's existing `onSocketReady`
  contract (see [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md)).
- `Socks5UdpCodec.TryEncode/Encode` (`src/WinForward.Protocols/Socks5Udp.cs`) preserves the original destination's IPv4/domain/
  IPv6 ATYP independently of the relay socket family. A valid IPv4 relay can
  therefore carry an IPv6 destination ATYP.
- Proxy setup errors remain fail-closed. Socket, UDP self-traffic token, and
  control connection are each released when owned; disposal continues through
  later resources if an earlier disposal throws.

### 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| IPv4 control + IPv4 relay | bind IPv4 UDP socket and send IPv4 ATYP as requested |
| IPv6 control + IPv6 relay | bind IPv6 UDP socket and send IPv6 ATYP as requested |
| IPv4 relay + IPv6 destination | bind IPv4 UDP socket; send IPv6 ATYP unchanged |
| IPv6 relay + IPv4 destination | bind IPv6 UDP socket; send IPv4 ATYP unchanged |
| relay response family differs from original flow | do not throw from `FlowKey.Create`; alias uses same-family transport endpoints |
| control/associate/socket/registration failure | release acquired resources and block the proxy-selected flow |

### 5. Good/Base/Bad Cases

- Good: loopback IPv4 SOCKS5 control and UDP relay receives an all-zero
  ASSOCIATE and a UDP frame whose destination ATYP is IPv6.
- Base: matching-family IPv4 and IPv6 control/relay integration tests remain
  green.
- Bad: allocate UDP socket from `flow.Local` before ASSOCIATE; an IPv6 local
  endpoint plus IPv4 relay produces `ArgumentException` from `FlowKey.Create`.

### 6. Tests Required

- Assert exact all-zero IPv4 and IPv6 ASSOCIATE request bytes.
- Assert loopback IPv4 relay receives IPv6 destination ATYP/address/payload and
  that relay self-traffic ownership exists before send and is removed after
  disposal.
- Assert an injected socket-disposal exception still releases the UDP token and
  control connection.
- Preserve coordinator same-flow reuse, relay collision, capacity, failure,
  response routing, and matching-family coverage.

### 7. Wrong vs Correct

```csharp
// Wrong: original destination/flow family is not the relay transport family.
var socket = new Socket(flowFamily, SocketType.Dgram, ProtocolType.Udp);
var relay = await control.UdpAssociateAsync(...);

// Correct: discover relay first, then use its family for the transport socket.
var relay = await control.UdpAssociateAsync(cancellationToken);
var socket = new Socket(relay.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
```

---

## Bounded UDP setup memory: global budget + datagram TTL + cooldown bound (wired 2026-08-30)

Task 08-30-atomic-retire (research R4/R3-UDP). Pre-fix worst case: 16,384 flows
× 32 KiB queues = 512 MiB held for hours against a dead SOCKS5 server, then
delivered long-expired; `_setupTombstones` was unbounded between 60 s sweeps.

### 1. Scope / Trigger

- Trigger: any change to `UdpProxyCoordinator` setup admission, the setup
  queue's drain/dispose paths, `_setupTombstones`, or `BoundedSetupQueue`'s
  entry shape.

### 2. Signatures

- `UdpProxyCoordinator`: `SetupQueueGlobalByteBudget` (8 MiB default; internal
  ctor override for tests), `long _pendingSetupBytes` (Interlocked),
  `SetupQueueDatagramTtl` (5 s), counters `SetupBudgetRejectionCount` /
  `SetupTtlExpiredCount`, diagnostics `PendingSetupBytesForDiagnostics`.
- `BoundedSetupQueue` (`WinForward.Core/BoundedSetupQueue.cs`): public class; entry
  shape is `(ReadOnlyMemory<byte>, DateTimeOffset)`. Timestamp overloads
  `TryEnqueue(frame, enqueuedAt)` / `TryDequeue(out frame, out enqueuedAt)`;
  the timestamp-less overloads forward with a default stamp — additive only,
  never break the existing signatures.

### 3. Contracts

- **Charge/credit exactly-once**: the global budget is charged at enqueue
  (before per-flow `TryEnqueue`) and credited back exactly once wherever the
  datagram leaves the pending set — flush send, flush TTL drop, drop-oldest
  eviction, per-flow-bounds rejection rollback, slot drain (setup failure),
  dispose drain. Every `BoundedSetupQueue.TryDequeue` call site MUST credit;
  a new dequeue sink without a credit is a budget leak (a leaked charge
  permanently shrinks the budget).
- **TTL at flush, entry-stamp granularity**: flush drops entries whose own
  enqueue stamp is older than the TTL (5 s — normal setup <1 s; older
  datagrams were retransmitted or expired at the application layer). Age
  filtering belongs to flush only; drop-oldest eviction credits regardless of
  age. Stamps come from the coordinator's `_timeProvider` (fake-time
  testable).
- **TTL ages from dial start — limiter queue-wait is not staleness (fixed 2026-09-06,
  measured pre-fix 2026-09-06)**: `CreateSessionAsync` re-stamps the slot's queued
  datagrams (`BoundedSetupQueue.RefreshEnqueuedStamps`, under the coordinator gate with
  the flush-style slot-ownership check) right after the setup leaves the 8-wide
  `_setupLimiter`, so the 5 s TTL at flush measures dial age. Pre-fix, wave k's
  triggering datagram under a burst of N flows with dial latency D was k×D old at flush
  (first-datagram loss began at N > 8 × floor(TTL/D); 93.75 % at 128 × 4 s); post-fix
  the same probe delivers 128/128 with timeToLast ≈ ceil(N/8)×D (acceptance matrix
  `benchmarks/results/2026-09-06-udp-burst-ttl-fix/`). Consequences: total retention is
  bounded by dial start + TTL (the 32-pkt/32-KiB slot bounds and the 8 MiB global
  budget cap the limiter-wait component); `SetupStampsRefreshedCount` is the diagnostic.
  Established sessions are immune either way — the same matrices show zero background
  loss and sub-0.1 ms send p95 in every window. Any change that re-attributs the stamp
  again or widens the limiter must re-run the `udp.burstEstablishment` matrix as its
  acceptance gate.
- **Budget exhaustion rejects the new datagram** (rollback the charge, count
  it, take the existing drop-counter path) — no failure tombstone, no
  teardown; the flow retries on its next datagram.
- **Cooldown tombstones are bounded**: `_setupTombstones` capacity = the
  coordinator's session `capacity`; a write at capacity evicts the
  oldest-deadline entry (refusal would degrade the cooldown into an
  immediate-retry storm). Lazy prune on touch and the 60 s sweep are unchanged.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Aggregate pending bytes + new datagram > 8 MiB | enqueue rejected, charge rolled back, counter++ |
| Queued entry older than 5 s at flush | dropped (not delivered), credited, counter++ |
| Setup failure drains the slot queue | every drained byte credited |
| Dispose drains pending queues | every drained byte credited (previously a silent abandon) |
| `_setupTombstones` at capacity on a new failure | oldest-deadline entry evicted, write proceeds |

### 5. Good/Base/Bad Cases

- Good: a flash crowd against a dead server holds ≤8 MiB; TTL-discards
  long-queued datagrams; after failure/dispose the budget is fully credited.
- Base: normal <1 s setups never observe the budget or the TTL.
- Bad: a `TryDequeue` sink without a credit; TTL compared against the slot's
  creation time instead of the entry's stamp; refusing the cooldown tombstone
  at capacity.

### 6. Tests Required

- `UdpSetupQueueTests`: `GlobalSetupBudgetRejectsBeyondTheAggregateAndCreditsBackOnFlush`,
  `SetupFailureCreditsBackThePendingBudget`, `DisposeCreditsBackDatagramsStillQueuedForSetup`,
  `FlushDropsSetupDatagramsOlderThanTheTtl` (fake TimeProvider; a late-arriving
  fresh entry on an old slot must still be delivered — entry-stamp, not
  slot-stamp), `SetupTombstonesAreBoundedAndEvictTheOldestAtCapacity`, and the
  credit-zero assertion appended to the drop-oldest FIFO test.
- Existing FIFO / drop-oldest / no-bypass / 1 s-cooldown / 8-way-cap /
  flash-crowd / dispose-drain tests stay green.

---

## UDP endpoint zero-allocation + frame-cap buffer sizing (wired 2026-08-30)

Task 08-30-udp-alloc-jumbo (backlog #5, research X6 + R5). Pre-fix: 3 heap
allocations per forwarded datagram (`IPEndPoint` round-trip in
`UdpProxySession.SendAsync`), 2 per relay response (`new IPAddress(bytes)` in
`Socks5UdpCodec.TryDecode`, immediately converted and discarded), 1-2 per
receive (`new IPEndPoint(Any/IPv6Any, 0)` sender template), and the transport
send buffer hard-wired to the Protocols constant instead of the frame cap the
coordinator/reinjector already honor — on a jumbo-capable ABI every payload
> 1508 would fail `TryEncode` → IOException → catch-all teardown → full
	SOCKS5 handshake re-run per datagram (storm).

### 1. Scope / Trigger

- Trigger: any change to `IUdpProxyTransport.SendAsync`'s signature,
  `Socks5UdpDatagram`'s address field, SOCKS5 UDP decode address materialization,
  the transport send-buffer sizing, or the composition of
  `Socks5UdpTransportFactory`.

### 2. Signatures

- `IUdpProxyTransport.SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken)` — the Core struct, not `IPEndPoint`.
- `Socks5UdpDatagram(IPAddressValue? DestinationAddress, string? DestinationDomain, ushort DestinationPort, ReadOnlyMemory<byte> Payload)` — `null` (nullable struct) still marks a domain-typed datagram.
- `Socks5UdpTransportFactory(SelfTrafficRegistry selfTraffic, int maximumFrameSize)` — required cap parameter; internal `Socks5UdpTransport.CreateAsync` seam mirrors it with a `UdpFrameBuilder.DefaultMaximumEthernetFrame` default.
- Per-transport cached `_receiveSenderTemplate` (`IPEndPoint`, ctor-computed from the relay address family).

### 3. Contracts

- **Forward leg passes `Endpoint` straight through** (`UdpProxySession.SendAsync` → transport): no `ToIPAddress()`/`new IPEndPoint` materialization anywhere on the send path; the transport encodes via `Socks5UdpCodec.TryEncode(IPAddressValue, ...)` and the actual `SendTo` target stays the fixed `RelayEndpoint`.
- **Decode produces `IPAddressValue?` directly** (`FromIPv4` / `FromIPv6(bytes, scopeId)`): the caller-supplied relay scope lands in `IPAddressValue.ScopeId` exactly as it previously landed in `IPAddress.ScopeId`; the session's reverse leg builds `Endpoint.From(address, port)` with zero framework-address round-trips. `IPAddress`-taking `TryEncode`/`Encode` overloads remain for cold edges (tests, loopback server echo).
- **One sender template per transport**: `ReceiveFromAsync` does not mutate the passed endpoint (the observed remote arrives in `SocketReceiveFromResult.RemoteEndPoint`), so a readonly ctor-computed template is shared across receives.
- **Send buffer derives from the same frame cap as every sibling**: `_sendBuffer = new byte[6 + 16 + maximumFrameSize]` (guard `> 0`), mirroring the coordinator's receive sizing (`cap + 22 + 1`) and the reinjector's frame bound (`cap`). Capture bounds payloads at `cap − 42`, so the send buffer always encodes anything the pipeline can capture; the fail-closed IOException for a genuinely larger datagram stays as the assumption guard.
- **Composition single source of truth** (`Program.cs` `CreateUdpCoordinator`): one hoisted `NdisApiAbi.MaximumEthernetFrame` flows to `Socks5UdpTransportFactory`, `UdpResponseReinjector`, and `UdpProxyCoordinator`. Send buffer, receive buffer, reinjector cap, and the native ABI must agree; only the ABI constant should ever change.
- **Oversized-response boundary**: the deliverable relay-response payload ceiling is `cap − 42` (1472 B at the pinned 1514 ABI — standard-MTU QUIC/WireGuard 1472 B packets pass exactly); larger responses skip as `Oversized` (5 s summary counter) and oversized rebuilt frames drop fail-closed. A jumbo-capable ABI lifts the ceiling end-to-end now that the send buffer follows the cap.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Session sends a datagram | transport receives the same `Endpoint` (address, port, family); 0 endpoint allocations |
| Relay response decodes (IPv4 / IPv6 / IPv6-with-scope) | `DestinationAddress` is the raw `IPAddressValue` with `ScopeId` propagated from the relay endpoint |
| Transport constructed with cap 9014, payload 2000 B | encodes and delivers without the buffer-size IOException |
| Transport constructed with default cap, payload > `6 + 16 + cap` | fail-closed IOException (assumption guard) |
| Negative scopeId into decode | `OverflowException` from `checked` cast (unreachable: scope comes from `IPAddress.ScopeId`, always ≥ 0) |

### 5. Tests

- `SendAsyncForwardsTheSessionEndpointToTheTransportUnchanged` (endpoint fidelity through the session).
- `SocksUdpIpv4RoundTripDecodesToTheRawAddressValue`, `Socks5UdpDecodeCarriesRelayScopeForIpv6Address` (`.Value.ScopeId == 9`).
- `JumboCapSendBufferEncodesPayloadsBeyondTheDefaultCap` (9014 cap, 2000 B payload, end-to-end through a loopback relay), `DefaultCapSendBufferFailsClosedOnOversizedPayloads`.
- Existing skip-class/connreset/reinjection suites stay green (463/463 at landing).

### 6. Migration note

xUnit `Assert.Equal` generic inference does not apply the `IPAddress` → `IPAddressValue` implicit conversion; migrated assertions cast explicitly (`(IPAddressValue?)expected`). `Assert.Equal(IPAddress, IPAddressValue)` still compiles elsewhere (e.g. `UdpPacketView` assertions) through inference participation — do not mistake those sites for unmigrated ones.
