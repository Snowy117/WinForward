# UDP Relay Contracts

> How WinForward proxies UDP flows through a SOCKS5 UDP relay: datagram handoff, response reinjection, loop prevention, and cross-family relay setup. Split 2026-08-29 from the former monolithic NDISAPI file; NDISAPI transport basics live in [windows-ndisapi.md](./windows-ndisapi.md).

---

## UDP relay wiring (wired 2026-08-09, hardware pass pending)

- A proxy-decided UDP datagram is parsed for its payload (`IPUdpPacket.TryParseSpan`, `src/WinForward.Protocols/IPUdpPacket.cs`) and handed to `UdpProxyCoordinator.TrySendSpanAsync` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Send.cs`); the original frame is consumed (never reinjected) — the SOCKS5 UDP relay transport owns forwarding.
- SOCKS5 UDP responses arrive from the dynamic relay endpoint; `UdpResponseReinjector` (an `IUdpResponseSink`, both in `src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs`) rebuilds a complete Ethernet II + IPv4/IPv6 + UDP frame (`UdpFrameBuilder`, `src/WinForward.Protocols/UdpFrameBuilder.cs`, RFC 768 0→0xFFFF checksum inversion) with the real server as source and `originalFlow.Local` as destination, then injects toward MSTCP (host flow) or the origin adapter (forwarded flow).
- Relay-source validation is port + address-family (not exact `IPEndPoint`): `Socks5UdpTransport.ReceiveAsync` accepts a datagram whose source port equals the relay port and whose family matches the relay, even from a different IP (multi-homed/anycast relay); a different port or family is still rejected. IPv6 scope is deliberately not compared (the receive interface's scope legitimately differs from the relay's advertised scope). Re-tightening to exact-address equality would break multi-homed relays (RFC 1928 does not pin the reply source).
- The response reinjector needs the NDISAPI enumeration handle + the host adapter MAC (from NDISAPI `CurrentAddress`); the MAC is used for both src and dst on host flows.
- **Forwarded flow destination MAC** (fixed 2026-08-15): a forwarded (Hyper-V/VM) flow's response must NOT use the adapter's own MAC as destination — the vSwitch would deliver it to the host, never the VM. The client (VM) MAC is recorded at session creation (`NdisPacketActionExecutor`, `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs`, extracts the Ethernet src MAC from bytes 6..11 of the first proxied frame, after `IPUdpPacket.TryParse` succeeds; `UdpProxySession.ClientMac` is set once, get-only) and plumbed through `IUdpResponseSink.InjectAsync(..., byte[]? clientMac, ...)` to `UdpResponseReinjector`, which uses it as the destination MAC (src = origin adapter MAC) for forwarded flows only. Missing/≠6-byte client MAC on a forwarded flow → fail-closed drop with rate-limited warn (`LogMissingClientMac`, 5s interval, same pattern as missing-origin-adapter). TCP reverse legs are unaffected — they reuse captured frames whose MACs are already correct. Locked by `ForwardedFlowResponseInjectsTowardOriginAdapter` (dst == client MAC) / `ForwardedFlowResponseWithoutValidClientMacIsDroppedFailClosed` / `RelayResponseCarriesRecordedClientMac`.
- **Host-flow response adapter binding** (fixed 2026-08-27, hardware-verified): `SendPacketToMstcp` is adapter-bound. A host UDP response must resolve the flow's origin adapter through the capture-scope `UdpAdapterTarget` map (`UdpResponseReinjector`) and use that target's enumeration handle and MAC for both Ethernet header slots. **F4 (2026-09-30):** the lookup is `IUdpAdapterTargetSource.Resolve(ushort slot)` over a slot-indexed array rebuilt on each capture refresh from the same `scope` the slots were interned from (`UdpAdapterTargetSource`, `DurableCaptureBundle.UpdateUdpTargets`) — one array index, no string hash and no dictionary probe on the per-response path; `AdapterIds` resolves each non-null entry's `StableId` through the slot table for the diagnostic log. The slot↔target map and the flow key share one identity source, so they cannot disagree about which adapter a flow came from (`DurableCaptureBundleTests.ScopeHeadBecomesHostFallbackAndEachAdapterResolves`, `UdpAdapterTargetSourceTests.AdapterIdsResolveThroughTheSlotTable`). The startup-selected `scope[0]` target is only a compatibility fallback when the origin adapter is absent from the current map; that fallback emits a rate-limited warning. Forwarded behavior is unchanged: an unresolved origin still drops fail-closed and a resolved origin still injects toward the adapter with the recorded client MAC as destination.

  Hardware proof (Win11 guest with two Hyper-V adapters, default route on External while GUID sort made Internal the `scope[0]`): the pre-fix build reinjected every host UDP response with Internal's MAC on both slots and the frame was dropped by tcpip with `DropReason "Not locally destined"` (strong-host receive validation — the destination IP 192.168.77.2 belongs to External, not the indicating interface). pktmon captured the drop location `0xE0004136` while the WinForward trace showed `udp.response.reinjected target=mstcp` completing — reinjection executes; delivery dies in the IP layer. 0/20 queries succeeded pre-fix; the origin-adapter fix delivers 20/20 plus 5/5 on a 1-second-settle restart, first attempt included, with zero warnings and one UDP session per query.

  | Flow/adapter condition | Required response action |
  |---|---|
  | Host + origin stable ID resolves | `SendToMstcp(origin.Handle)`; src/dst MAC = `origin.Mac` |
  | Host + origin stable ID missing/unresolved | warn (rate-limited), then `SendToMstcp(fallback.Handle)` |
  | Forwarded + origin resolves + valid client MAC | `SendToAdapter(origin.Handle)`; dst MAC = client MAC |
  | Forwarded + origin unresolved or client MAC invalid | drop fail-closed |

  Good: a WLAN-originated host query is reinjected to MSTCP on the WLAN enumeration handle even when another adapter sorts first in capture scope. Base: a host adapter disappears mid-flow, so the response uses the startup fallback and records a warning. Bad: choosing `scope[0]` for every host response; the indication is attached to an unrelated interface and can fail Windows adapter/source-address validation.

  Required tests: `HostFlowResponseInjectsTowardItsOriginAdapter` asserts handle, ON_RECEIVE flag, and both MAC slots; `HostFlowWithUnresolvedOriginAdapterUsesFallbackAndWarns` asserts fallback injection and warning; forwarded response tests continue to assert `SendToAdapter`, ON_SEND, and client destination MAC. F4 (2026-09-30) adds `ScopeHeadBecomesHostFallbackAndEachAdapterResolves` (slot → the same target the scope's adapter maps to) for the re-keyed map; the three existing tests are unchanged.

  ```csharp
  // Wrong: startup ordering is unrelated to the flow's capture adapter.
  reinjector.SendToMstcp(scope[0].RuntimeHandle, buffer);

  // Correct: resolve the interned adapter slot carried by the flow (F4: slot-indexed array).
  var target = adaptersBySlot[flow.OriginAdapterSlot];
  reinjector.SendToMstcp(target.Handle, buffer);
  ```
- Loop prevention: `Socks5UdpTransport.Create` registers `(Udp, localSocketEndpoint, relayEndpoint)` in `SelfTrafficRegistry` from the lease's current relay endpoint before the first datagram, and releases the token on dispose — catch-all proxy rules never recursively intercept WinForward's own UDP relay traffic. A shared association's in-place re-association publishes a new relay endpoint (see the pooling section below), so the transport replaces the tuple on its next send (`RebindRelay`: register the new tuple, adopt the endpoint, dispose the old token). The factory takes the pool and the registry.
- **Session activity accounting is two-tier** (task 08-29-udp-throughput-loss D3): `UdpProxySession` updates `_lastActivityTicks` on *every* send and receive (Interlocked, exact — idle-expiry decisions in `TryBeginExpiry` read this exact value), while propagation to the association-table observer (`UdpAssociationTable` touch) is throttled to at most once per `ActivityPropagationInterval` (100ms) per session via Interlocked CAS; the first activity after creation or after an interval propagates immediately. The table serves reverse-leg classification and sweep pruning on seconds-scale timeouts, so 100ms granularity is unobservable there; per-datagram table touches were pure overhead. Do not throttle the timestamp itself — that would delay expiry.
- **Relay sockets disable `SIO_UDP_CONNRESET`, and `ConnectionReset` is a skip, not a failure** (task 08-29-proxy-stability-perf S2, 2026-08-29): on Windows an ICMP port-unreachable for a destination the relay socket wrote to surfaces as `SocketException(ConnectionReset)` on the next receive, which used to tear the session down (churn: re-ASSOCIATE per cycle under a noisy path). Two layers, both required: (1) `Socks5UdpTransport.Create` issues the vendor IOCTL `0x9800000C` with a 4-byte `FALSE` **before bind** via an internal seam — the default implementation is guarded by `OperatingSystem.IsWindows()` because Linux `Socket.IOControl` throws `PlatformNotSupportedException` (test seam asserts the call on any OS); (2) `ConnectionReset` is classified as a skip at both the transport layer (`ClassifyReceiveFault`) and the session receive loop — counted in the 5 s skip summary (`connectionReset=`), loop survives, session reusable. All other socket errors remain fatal. Locked by `Socks5UdpConnresetTests` + `UdpReceiveResilienceTests`.
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
- `IUdpProxyTransportFactory.CreateAsync(Socks5Server, CancellationToken)` and the internal
  `Socks5UdpTransport.Create(lease, SelfTrafficRegistry, ...)`
  (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`) do not accept the original flow's address
  family: the relay family is the association lease's negotiated relay family.
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
- Proxy setup errors remain fail-closed. Socket, UDP self-traffic token, and the
  association lease are each released when owned; disposal continues through
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
delivered long-expired; the setup-cooldown index (`UdpSetupCooldownTable`) was
unbounded between sweep ticks.

Structure note (2026-09-08, P1): the concerns live in separately named types —
`UdpProxyCoordinator` (slot dict + admission + teardown) composes
`UdpSetupCooldownTable` (setup-failure cooldowns, own leaf lock),
`UdpSetupQueueBudget` (global byte budget, Interlocked-only), `UdpSessionSetup`
(dial/claim/construct/flush pipeline via ctor delegates), and `UdpProxyLogging`
(static event formatting) — mirroring the TCP 1158→5 coordinator split.
Terminology: TCP "tombstone" = 60 s TIME_WAIT grace; the UDP 1 s setup-failure
window is always "setup cooldown" (the word tombstone is retired from UDP).

### 1. Scope / Trigger

- Trigger: any change to `UdpProxyCoordinator` setup admission, the setup
  queue's drain/dispose paths, `UdpSetupCooldownTable` / `UdpSetupQueueBudget`
  / `UdpSessionSetup`, or `BoundedSetupQueue`'s entry shape.

### 2. Signatures

- `UdpSetupQueueBudget` (`UdpProxy/UdpSetupQueueBudget.cs`):
  `SetupQueueGlobalByteBudget` (8 MiB default; coordinator internal ctor
  override for tests), `PendingBytes` (Interlocked), `RejectionCount`,
  `TryCharge`/`Credit` (exactly-once contract), `NoteDrop` (rate-limited drop
  logging). Diagnostics surface as coordinator
  `PendingSetupBytesForDiagnostics` / `SetupBudgetRejectionCount`.
- `UdpSessionSetup` (`UdpProxy/UdpSessionSetup.cs`): `SetupQueueDatagramTtl`
  (5 s), counters surfaced as coordinator `SetupTtlExpiredCount` /
  `SetupStampsRefreshedCount`; the 8-wide `_setupLimiter` and the dial-start
  re-stamp live here (coordinator slot state touched only via ctor delegates
  that take the coordinator gate).
- `UdpSetupCooldownTable` (`UdpProxy/UdpSetupCooldownTable.cs`): bounded
  retry-deadline index (`TryHit`/`Write`/`PruneExpired`), capacity = the
  coordinator's session capacity; diagnostics surface as coordinator
  `SetupCooldownCountForDiagnostics`.
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
  measured pre-fix 2026-09-06)**: `UdpSessionSetup.CreateSessionAsync` re-stamps the slot's queued
  datagrams (`BoundedSetupQueue.RefreshEnqueuedStamps`, under the coordinator gate with
  the flush-style slot-ownership check) right after the setup leaves the 8-wide
  setup limiter, so the 5 s TTL at flush measures dial age. Pre-fix, wave k's
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
  it, take the existing drop-counter path) — no setup cooldown is armed, no
  teardown; the flow retries on its next datagram.
- **Setup cooldowns are bounded**: `UdpSetupCooldownTable` capacity = the
  coordinator's session `capacity`; a write at capacity evicts the
  oldest-deadline entry (refusal would degrade the cooldown into an
  immediate-retry storm). Lazy prune on touch and the sweeper's UDP leg — its own derived cadence,
  15 s at the default 30 s retention (see the pooling section below) — are unchanged.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Aggregate pending bytes + new datagram > 8 MiB | enqueue rejected, charge rolled back, counter++ |
| Queued entry older than 5 s at flush | dropped (not delivered), credited, counter++ |
| Setup failure drains the slot queue | every drained byte credited |
| Dispose drains pending queues | every drained byte credited (previously a silent abandon) |
| Setup cooldown table at capacity on a new failure | oldest-deadline entry evicted, write proceeds |

### 5. Good/Base/Bad Cases

- Good: a flash crowd against a dead server holds ≤8 MiB; TTL-discards
  long-queued datagrams; after failure/dispose the budget is fully credited.
- Base: normal <1 s setups never observe the budget or the TTL.
- Bad: a `TryDequeue` sink without a credit; TTL compared against the slot's
  creation time instead of the entry's stamp; refusing the setup cooldown
  at capacity.

### 6. Tests Required

- `UdpSetupQueueTests`: `GlobalSetupBudgetRejectsBeyondTheAggregateAndCreditsBackOnFlush`,
  `SetupFailureCreditsBackThePendingBudget`, `DisposeCreditsBackDatagramsStillQueuedForSetup`,
  `FlushDropsSetupDatagramsOlderThanTheTtl` (fake TimeProvider; a late-arriving
  fresh entry on an old slot must still be delivered — entry-stamp, not
  slot-stamp), `SetupCooldownsAreBoundedAndEvictTheOldestAtCapacity`, and the
  credit-zero assertion appended to the drop-oldest FIFO test.
- Existing FIFO / drop-oldest / no-bypass / 1 s-cooldown / 8-way-cap /
  flash-crowd / dispose-drain tests stay green.

---

## UDP endpoint zero-allocation + frame-cap buffer sizing (wired 2026-08-30)

Task 08-30-udp-alloc-jumbo (backlog #5, research X6 + R5). Pre-fix: 3 heap
allocations per forwarded datagram (`IPEndPoint` round-trip in
`UdpProxySession.SendSpanAsync`), 2 per relay response (`new IPAddress(bytes)` in
`Socks5UdpCodec.TryDecode`, immediately converted and discarded), 1-2 per
receive (`new IPEndPoint(Any/IPv6Any, 0)` sender template), and the transport
send buffer hard-wired to the Protocols constant instead of the frame cap the
coordinator/reinjector already honor — on a jumbo-capable ABI every payload
> 1508 would fail `TryEncode` → IOException → catch-all teardown → full
	SOCKS5 handshake re-run per datagram (storm).

### 1. Scope / Trigger

- Trigger: any change to `IUdpProxyTransport.SendSpanAsync`'s signature,
  `Socks5UdpDatagram`'s address field, SOCKS5 UDP decode address materialization,
  the transport send-buffer sizing, or the composition of
  `Socks5UdpTransportFactory`.

### 2. Signatures

- `IUdpProxyTransport.SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken)` — the Core struct, not `IPEndPoint`; span-only since task 09-19-compat-api-cleanup (the memory overload was removed).
- `Socks5UdpDatagram(IPAddressValue? DestinationAddress, string? DestinationDomain, ushort DestinationPort, ReadOnlyMemory<byte> Payload)` — `null` (nullable struct) still marks a domain-typed datagram.
- `Socks5UdpTransportFactory(UdpAssociationPool pool, SelfTrafficRegistry selfTraffic, int maximumFrameSize, int relayReceiveBufferBytes)` — the pool is the association source and the frame cap stays required; the internal `Socks5UdpTransport.Create(lease, …)` seam mirrors the cap and buffer with defaults (`UdpFrameBuilder.DefaultMaximumEthernetFrame`, `DefaultRelaySocketReceiveBufferSize`).
- Per-transport cached `_receiveSenderTemplate` (`IPEndPoint`, ctor-computed from the relay address family).

### 3. Contracts

- **Forward leg passes `Endpoint` straight through** (`UdpProxySession.SendSpanAsync` → transport): no `ToIPAddress()`/`new IPEndPoint` materialization anywhere on the send path; the transport encodes via `Socks5UdpCodec.TryEncode(IPAddressValue, ...)` and the actual `SendTo` target is the lease's current relay publication, re-read per send as one reference compare against the cached publication (only an in-place re-association replaces it, see the pooling section below).
- **Decode produces `IPAddressValue?` directly** (`FromIPv4` / `FromIPv6(bytes, scopeId)`): the caller-supplied relay scope lands in `IPAddressValue.ScopeId` exactly as it previously landed in `IPAddress.ScopeId`; the session's reverse leg builds `Endpoint.From(address, port)` with zero framework-address round-trips. The `IPAddress`-taking `TryEncode`/`Encode` overloads were removed (task 09-19-compat-api-cleanup); `TryEncode(IPAddressValue, ...)` is the sole encode seam, and tests/loopback callers materialize a `byte[]` themselves.
- **One sender template per transport**: `ReceiveFromAsync` does not mutate the passed endpoint (the observed remote arrives in `SocketReceiveFromResult.RemoteEndPoint`), so a readonly ctor-computed template is shared across receives.
- **Send buffer derives from the same frame cap as every sibling**: `_sendBuffer = new byte[6 + 16 + maximumFrameSize]` (guard `> 0`), mirroring the coordinator's receive sizing (`cap + 22 + 1`) and the reinjector's frame bound (`cap`). Capture bounds payloads at `cap − 42`, so the send buffer always encodes anything the pipeline can capture; the fail-closed IOException for a genuinely larger datagram stays as the assumption guard.
- **Composition single source of truth** (`Cli/UdpProxyComposer.cs`): one hoisted `NdisApiAbi.MaximumEthernetFrame` flows to `Socks5UdpTransportFactory`, `UdpResponseReinjector`, and `UdpProxyCoordinator`. Send buffer, receive buffer, reinjector cap, and the native ABI must agree; only the ABI constant should ever change.
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

- `SendSpanAsyncForwardsTheSessionEndpointToTheTransportUnchanged` (endpoint fidelity through the session).
- `SocksUdpIpv4RoundTripDecodesToTheRawAddressValue`, `Socks5UdpDecodeCarriesRelayScopeForIpv6Address` (`.Value.ScopeId == 9`).
- `JumboCapSendBufferEncodesPayloadsBeyondTheDefaultCap` (9014 cap, 2000 B payload, end-to-end through a loopback relay), `DefaultCapSendBufferFailsClosedOnOversizedPayloads`.
- Existing skip-class/connreset/reinjection suites stay green (463/463 at landing).

### 6. Migration note

xUnit `Assert.Equal` generic inference does not apply the `IPAddress` → `IPAddressValue` implicit conversion; migrated assertions cast explicitly (`(IPAddressValue?)expected`). `Assert.Equal(IPAddress, IPAddressValue)` still compiles elsewhere (e.g. `UdpPacketView` assertions) through inference participation — do not mistake those sites for unmigrated ones.

---

## UDP session lifetime, teardown reason, and fail-closed send drop (wired 2026-09-20, task 09-20-transport-lifecycle)

> Superseded in part by the C3 section below (2026-09-21, task `09-20-lifecycle-migration-cluster`):
> `_lifetime`, `_receiveFailure`, `_activeSends`, `_disposed`, the coordinator's `_shutdown`,
> `_inFlightTeardowns` and `DrainInFlightTeardownsAsync` no longer exist — the owners run on
> `QuiescenceScope` and `scope.Fault` is the single failure representation. Keep this section for its
> teardown-reason and fail-closed-drop *reasoning*; take the mechanisms and signatures from the C3
> section.

### 1. Scope / Trigger

- Trigger: any change to `UdpProxySession`'s receive loop / activity guard / disposal, the
  coordinator's slot removal and setup cooldown, or the ready-path send result.

### 2. Signatures

- `UdpSessionState { SettingUp, Active, Expiring, Faulted, Disposed }` and
  `UdpTeardownReason { SetupFailure, Expiry, Fault, AssociationLost, Shutdown }`
  (`UdpProxy/UdpSessionState.cs`, `UdpProxy/UdpTeardownReason.cs`).
- `UdpProxySession.SendSpanAsync(Endpoint destination, ReadOnlySpan<byte> payload, CancellationToken)`
  -> `ValueTask<bool>` (`true` = sent; `false` = not sent because the session is expiring or
  has failed).
- `UdpProxySession.State` — computed under the session `_activityGate` (lifecycle only; the send and
  touch paths no longer take it).
- `UdpProxySession.LastActivityUtc` — derived from the internal activity bucket (500 ms quantum), the
  same representation the coordinator's expiry scan and `TryBeginExpiry` compare.
- `UdpProxyCoordinator.RemoveSlotAsync(slot, UdpTeardownReason)`; the cooldown is armed only
  for `SetupFailure`.

### 3. Contracts

- **Per-session lifetime token**: `UdpProxySession` owns
  `_lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown)` (mirroring
  `TcpRedirectSession.Lifetime`). The receive loop and every `InjectAsync` route through
  `_lifetime.Token`; catch guards `when (_lifetime.IsCancellationRequested)` treat
  cancellation as **normal teardown** (idle expiry OR shutdown). `_receiveFailure` is written
  only by the generic catch, so **idle expiry no longer manufactures a `_receiveFailure`** and
  the fire-and-forget failure handler no longer fires on expiry.
- **Expiry cancels outside the activity lock**: `TryBeginExpiry` cancels `_lifetime` *outside*
  `_activityGate` (the lock is non-reentrant; a cancellation callback must not self-deadlock).
- **Send reports sent/not-sent instead of throwing**: the `_expiring` / `_receiveFailure`
  preconditions return `false`; no `IOException` reaches the dispatcher for those. The
  coordinator counts a rate-limited fail-closed drop (`RuntimeCounters.UdpFailClosedDrop`,
  `udp.send.dropped reason=sessionUnavailable`, 5 s throttle) and does **not** remove the slot
  (Expiry -> the sweeper owns removal; Fault -> the failure handler owns removal). A genuine
  transport exception keeps the existing remove-slot + rethrow path.
- **Teardown reason is data**: every slot removal passes a `UdpTeardownReason`; only
  `SetupFailure` arms the 1 s setup cooldown. An association that died without recovering in place
  is `AssociationLost` — counted, and deliberately without the cooldown (see the pooling section
  below). Teardown logging carries the reason.
- **Owned drain for the residual handler**: the coordinator tracks in-flight receive-failure
  teardowns and awaits them in its `DisposeCoreAsync` (`DrainInFlightTeardownsAsync`); the
  receive loop still does **not** await the handler (re-entrancy hazard).

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Idle-expiry admitted | `_lifetime` cancelled; loop exits as normal teardown; no `_receiveFailure`, no failure handler |
| Coordinator shutdown cancels the loop | normal teardown (no `_receiveFailure`) |
| Genuine socket fault | `_receiveFailure` set, `State == Faulted`, failure handler fires once |
| Send against an expiring/faulted session | `false`; counted rate-limited drop; slot NOT removed by the sender |
| Send transport throws | existing remove-slot + rethrow |
| Slot removed for `SetupFailure` | setup cooldown armed |
| Slot removed for `Expiry`/`Fault`/`Shutdown` | no cooldown |
| Coordinator `DisposeAsync` with an in-flight failure teardown | drained before dispose completes |
| Session teardown after `_lifetime` disposal | lifetime disposed exactly once |

### 5. Good/Base/Bad Cases

- Good: an idle session expires cleanly — the receive loop observes cancellation, the handler
  never fires, and a datagram racing the expiry is dropped without an exception.
- Base: a real socket fault sets `Faulted`, fires the handler once, and the coordinator drains
  that teardown on dispose.
- Bad: throwing `IOException` from `SendSpanAsync` for the expiry precondition; cancelling
  `_lifetime` while holding `_activityGate`; arming the cooldown for `Expiry`.

### 6. Tests Required

- `UdpProxySessionTests` — idle expiry records no failure / ends the loop / refuses the send;
  a genuine fault sets `Faulted` + fires the handler once.
- `UdpProxyCoordinatorTests.SessionStateReportsSettingUpWhileDialingThenActiveWhenReady`.
- `UdpSessionSetupTests` — setup failure maps to `SetupFailure`, a cancelled dial to
  `Shutdown` (fake host records `RemovedReason`).
- Existing ready-path, skip-class, connreset, budget-credit, and dispose-drain suites stay
  green; `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes`
  unchanged.
- Sweep gates (task 09-30-expiry-sweep-bounded-pause): `SweepAllocationGateTests.UdpProxyCoordinatorSweepAllocatesNoManagedBytes` (no-op tick over 16 fake-transport sessions — the tick that repeats every 15 s — 272 B → 0), `…UdpAssociationPoolSweepAllocatesNoManagedBytes` (no-op tick over shared associations with outstanding leases, 328 B → 0) and `…UdpAssociationTableSweepAllocatesNoManagedBytes` (retiring tick over 64 idle associations, 4,184 B → 0). All three hold a reused scratch and a `SemaphoreSlim(1,1)` (or `Lock`) sweep gate; the pool gate releases every lease before asserting so a failing window cannot hang the pool drain.

### 7. Wrong vs Correct

#### Wrong

```csharp
// Precondition failure as an exception: internal state leaks out as a packet-path throw,
// and the sender would tear the slot down even though the sweeper owns expiry.
lock (_activityGate) { if (_expiring) throw new IOException("session is expiring"); }
```

#### Correct

```csharp
// Report sent/not-sent; the coordinator counts a fail-closed drop and leaves the slot alone.
lock (_activityGate) { if (_expiring || _scope.Fault is not null) return false; }
```

---

## Scope-owned UDP lifetime and the receive-failure signal/join split (wired 2026-09-21, task 09-20-lifecycle-migration-cluster)

### 1. Scope / Trigger

- Trigger: any change to `UdpProxySession`'s lifetime / send admission / receive loop, the coordinator's
  slot teardown or disposal ordering, or the `IUdpSessionSlotHost` receive-failure seam.

**This section supersedes the previous section's mechanisms** (`async-lifetime.md` is the primitive's
contract and carries the per-owner table): `_lifetime` → the session's own `QuiescenceScope`, which owns
the CTS; `_receiveFailure` → **deleted**, `_scope.Fault` is the single failure representation;
`_activeSends` → scope accounting via `WorkLease`; `_disposed` → `_scope.IsSealed`; the coordinator's
`_shutdown` → its own `QuiescenceScope`; `_inFlightTeardowns` + `DrainInFlightTeardownsAsync` →
`_scope.Run(...)`; and the failure signal is a synchronous `Action<UdpProxySession>` rather than a
fire-and-forget `Func<UdpProxySession, Task>`.

### 2. Signatures

- `UdpProxySession`: `_scope = new QuiescenceScope(context.Shutdown)` owns the CTS;
  `Start(Action<UdpProxySession> receiveFailureHandler)`; `SendSpanAsync(...) -> ValueTask<bool>`;
  `UdpSessionState State`; `Task DisposeAsync()`.
- `IUdpSessionSlotHost.RemoveReceiveFailedSession(UdpProxySession session)` — **`void`**, not `Task`:
  VSTHRD200 forbids an `Async` suffix on a non-awaitable, and the signal must not be awaitable.
- `UdpProxyCoordinator`: `_scope = new QuiescenceScope()` owns the CTS; session scopes nest under
  `_scope.Token`; `_inFlightTeardowns`/`DrainInFlightTeardownsAsync` deleted.

### 3. Contracts

- **One failure representation.** `scope.Fault` is the only failure state; there is no parallel
  `_receiveFailure` field. The receive loop records the fault (`RecordFault(exception, "udp.receive")`)
  and then signals.
- **The send path takes no `_activityGate` (task 09-30-warm-path-lock-chain, 2026-09-30).**
  `SendSpanAsync`'s admission is `Volatile.Read(ref _expiring) || _scope.Fault is not null ||
  !_scope.TryEnter(out var workLease)` — the scope's lock-free CAS is the admission authority and
  its drain joins outstanding leases, so disposal still cannot free the transport under a sender.
  `_activityGate` is retained only for the lifecycle transitions (`State`, `TryBeginExpiry`,
  `CancelExpiry`), which are off the packet path; `_expiring` is read/written volatile there.
  Consequence, recorded: a sender admitted between the sweeper's idle re-check and its `_expiring`
  store goes **out** instead of becoming a counted `UdpFailClosedDrop` — the benign direction
  (design §6.3/§7 delta 5). The READY-path contract is stronger still: a cache-resident ready
  session takes **zero** coordinator gate entries, **zero** session activity-gate entries and
  **zero** clock reads (`UdpReadySendTakesZeroActivityGateEntries`,
  `UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads`); the cooldown probe and the
  clock read live on the admission path, a ready session never consults them (a flow in cooldown
  has no slot by construction — the cooldown write and the slot removal share one `_gate` hold).
- **The signal is synchronous and must return promptly.** The loop tail invokes
  `Action<UdpProxySession>` after releasing the receive-window lease. Awaiting or blocking on
  `session.DisposeAsync()` there deadlocks against the session's own disposal (which awaits the receive
  loop). The coordinator maps the signal to
  `_scope.Run(_ => RemoveReceiveFailedSessionAsync(session), "udp.receive-failure")`, which returns
  immediately and makes the teardown a tracked child.
- **The per-teardown warning lives inside the `Run` body** (with a rethrow so `Run` records the fault):
  `Run` swallows the child's fault into `scope.Fault` and cannot log the owner's domain-specific warning.
- **The coordinator seals at a defined point.** `DisposeCoreAsync` cancels the scope, clears
  sessions/cooldowns, awaits each `slot.Completion` + `session.DisposeAsync()`, and only **then** drains
  the scope (sealing + joining in-flight `Run` teardowns) before releasing the setup limiter. Sealing
  earlier would refuse teardowns that in-flight sessions still need; sealing later would race the drain.
- **`UdpSessionSetup`'s setup-failure `RemoveSlotAsync` stays a direct call** — a gate-taking decision,
  not a fire-and-forget teardown, and it must still work while the coordinator's scope is sealed.
- **Owner teardown single-flight (D11).** `UdpProxySession.DisposeAsync` and
  `UdpProxyCoordinator.DisposeAsync` each keep an explicit one-shot claim; a later caller joins
  `_scope.DrainAsync()`.
- **`_expiring` is retained** as the owner's admission policy (the scope never unseals, so
  expiry-vs-send cannot live in it).

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Send admitted while the session is active | lease taken with no `_activityGate` (scope CAS only), released exactly once in the send tail |
| Ready session hit (cache-validated, `Session.Flow` matches) | zero coordinator gate entries, zero activity-gate entries, zero clock reads; the transport send runs inline |
| Session expiring/faulted at admission | `false` returned, counted `UdpFailClosedDrop` by the caller, no lease taken |
| Flow inside its 1 s setup cooldown | rejected on the admission path before any slot; the ready path cannot observe this state |
| `DisposeAsync` with an outstanding send lease | does not complete until the lease is released; then `State == Disposed` |
| Genuine receive fault | `scope.Fault` set; `State == Faulted`; a subsequent send fails closed (`false`, counted drop); the coordinator teardown runs via `_scope.Run` |
| Receive fault racing coordinator shutdown | the in-flight teardown still completes (joined by the drain) |
| Faulting teardown | warning logged inside the `Run` body; no unobserved task exception; coordinator disposal still completes |
| Idle expiry | normal teardown; no fault recorded; the handler never fires |
| Setup failure while the coordinator scope is sealed | slot removal still happens (direct call) |
| Two concurrent coordinator `DisposeAsync` calls | owner teardown runs once; the second joins the drain |

### 5. Good/Base/Bad Cases

- Good: a receive fault records `scope.Fault`, signals synchronously, and the coordinator tears the
  session down as a tracked child its drain joins.
- Base: idle expiry ends the loop with no fault and no signal.
- Bad: re-awaiting the teardown from the loop tail; reading `scope.Fault` outside `_activityGate`;
  asserting on a `_receiveFailure` field; sealing the coordinator scope before
  `await slot.Completion`; leaving the teardown warning outside the `Run` body.

### 6. Tests Required

- `UdpProxySessionTests` — `DisposeAsyncWaitsForAnOutstandingSendLease`,
  `FaultedSessionStateHasNoParallelReceiveFailureField`, idle-expiry and genuine-fault suites.
- `UdpProxyCoordinatorLifecycleTests` — `ReceiveFailureTeardownInFlightAcrossDisposalIsStillJoined`,
  `FaultingReceiveFailureTeardownLogsTheWarningAndNeverEscapes`.
- `UdpProxyCoordinatorTests` single-flight disposal; `UdpSessionSetupTests` setup-failure /
  cancelled-dial reasons.
- `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` — unchanged and still
  exactly 0 B (the lease replaces `_activeSends++`), and it must pass **in isolation**.

### 7. Wrong vs Correct

#### Wrong

```csharp
// The loop awaits its own teardown => disposal re-enters itself (deadlock), and the failure lives
// in a second field the send admission must remember to read.
lock (_activityGate) { if (_receiveFailure is not null) return false; }
_ = receiveFailureHandler(this);
```

#### Correct

```csharp
// Single failure representation read under the same gate; a synchronous signal the coordinator
// turns into a tracked scope child.
lock (_activityGate) { if (_scope.Fault is not null) return false; }
_scope.RecordFault(exception, "udp.receive");
receiveFailureHandler(this);   // Action: returns immediately; _scope.Run(...) owns the teardown
```

---

## UDP association pooling: one authenticated control connection, one relay socket per flow (wired 2026-09-28, task 09-28-udp-association-reuse)

### 1. Scope / Trigger

- Trigger: any change to `UdpAssociationPool` / `UdpControlAssociation` / `UdpAssociationLease`, the
  `Socks5UdpTransportFactory` seam, `UdpTeardownReason`, the UDP placement/capability configuration
  keys, or the UDP side of `DurableCaptureBundle` composition.
- `udpAssociationReuse: off` is the rollback lever and reproduces per-flow associations byte for
  byte: one private association per flow, closed as soon as its last lease is released.

### 2. Signatures

- `UdpAssociationPool(SelfTrafficRegistry, UdpAssociationReuseMode, Socks5AddressCache?, TimeProvider?, IRuntimeLogger?, Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl, TimeSpan? recoveryTimeout, int maxAssociationsPerServer = DefaultMaxAssociationsPerServer, int flowsPerAssociation = DefaultFlowsPerAssociation)` (`UdpProxy/UdpAssociationPool.cs`) — one pool serves the whole runtime, one warm set per `Socks5Server`; `RentAsync(server, ct) -> UdpAssociationLease` places a flow. `DurableCaptureBundle` creates and owns the pool through `UdpProxyComposer.CreateAssociationPool` from the validated `udpAssociationReuse` / `udpAssociationMaxPerServer` / `udpAssociationFlowsPerAssociation` values.
- `UdpControlAssociation` (`UdpProxy/UdpControlAssociation.cs`): one authenticated `Socks5ControlConnection`, one `UDP ASSOCIATE`, the control-stream watchdog, and the in-place re-association. Diagnostics `LeaseCount`, `IsFaulted`, `RelayEndpoint`, `RelayTarget`.
- `UdpAssociationLease` (`UdpProxy/UdpAssociationLease.cs`): `RelayTarget` / `RelayEndpoint` / `RelayAddressFamily` / `IsFaulted` / `Fault`, plus the two datagram-path evidence calls `RecordDatagramSent()` / `RecordResponseReceived()`; `DisposeAsync()` releases the lease exactly once and is the transport's only association mutation.
- `UdpServerCapability { Unknown, SharedOk, PerFlowOnly }`, `UdpAssociationEvidence`, and `UdpAssociationCapabilitySampler.Evaluate(evidence, attached)` with `PinningSuspicionThreshold = 3` (`UdpProxy/UdpAssociationCapability.cs`).
- `UdpAssociationLostException` (`Socks5/Socks5UdpTransport.cs`): the fail-fast the transport throws on its next send once its lease is faulted.
- Caps and defaults: `udpAssociationMaxPerServer` default 1,024 (`1..16384`), `udpAssociationFlowsPerAssociation` default 16 (`1..256`). The product is the shared head — 16,384 flows per server at the defaults, exactly the default `udpSessionCapacity` — pinned by `UdpAssociationHeadTests`.

### 3. Contracts

- **Sharing covers the control connection only (R1/I1/I2).** One authenticated association serves up to `udpAssociationFlowsPerAssociation` concurrent flows, and **every flow keeps its own relay socket**: the local relay port still identifies the flow, so `RelayAlias` uniqueness, the per-flow self-traffic tuple, and reverse routing are unchanged. The descriptor floor is therefore ≈1 per live flow plus one shared control connection per association (≈1/16 at the defaults); the kernel receive-buffer estimate stays `live sessions × udpRelayReceiveBufferKb`.
- **Placement never refuses a flow (I6).** `RentAsync` takes the least-loaded shared association with room (creation order breaks ties), opens a new shared association while the per-server ceiling allows, and otherwise serves the flow from a *private* association. The ceiling is a bound on connections, not a preallocation; the scan is O(shared associations) and runs once per flow setup, never on the datagram path. A private association is never handed to a second flow.
- **Placement publishes and claims before it awaits (task 09-30-udp-association-head-count-flake).** `RentAsync`'s `Acquire` runs under `lock (_gate)` and, in that same critical section, adds a freshly created association to `set.All` (`Create`) and claims the lease with its evidence (`Attach` → `StartLease`); only then does `RentAsync` await `EnsureAssociatedAsync`. The order is the invariant: no lease can exist before its association is counted, so `AssociationCount` never under-reports the placed population and a sampler or maintenance tick never observes a placed flow without its evidence. Never move a publish or a lease claim after the await.
- **Capability is a sticky per-server verdict (R2/I8).** `auto` starts every server `Unknown` (share + sample), `always` forces `SharedOk` with detection disabled, `off` forces `PerFlowOnly` from the first placement. A flip changes only *future* placement: associations already placed keep serving their attached flows, no session is torn down, and the verdict lasts the run.
- **The sampler rule is conservative (design §5).** On the pool's 5 s maintenance tick, for each association of a still-`Unknown` server the sampler reads the evidence of the leases **currently attached**: two attached flows with a decoded response ⇒ `SharedOk` (detection stops); one responding flow plus a sibling with ≥3 successful sends and no response ⇒ `PerFlowOnly`; fewer than two attached flows ⇒ no verdict. The asymmetry is deliberate: a false `PerFlowOnly` costs only the sharing win, while a false `SharedOk` costs datagrams, so `SharedOk` requires positive proof. A `PerFlowOnly` flip emits the one-shot `udp.association.fallback` warn (`reason=source-port-pinned`, with `flows`/`sent`/`unanswered`/`relay`) and `udpAssociationFallbacks`, exactly once per server per run (the verdict is recorded under the pool gate before the log).
- **Evidence lives with the live attached set, never with a ring (I3).** Each lease owns one `UdpAssociationEvidence` (one `Interlocked` sent counter plus a write-once response flag); `StartLease` adds the record and `ReleaseLeaseAsync` removes it, both under the association's leaf `_evidenceGate`, so `SnapshotEvidence()` returns exactly the leases attached at that instant however many leases the association has served before. An earlier index-by-attach-count ring mixed stale, live, and released slots — never reintroduce that shape.
- **Known limit of the rule (accepted).** The rule needs a *live* responding sibling, so a pinning server whose answered flow has already been released is not detectable by that association. The window is bounded by the lease lifetime and the 5 s tick, and the verdict is per server and sticky, so any other association of the same server still carries it.
- **Association death and the one bounded recovery (R3/I4/I7).** The watchdog blocks on the control stream: a 0-byte read or a stream fault is death (RFC 1928 defines no control-connection traffic after ASSOCIATE, so any received byte is not); our own cancellation/disposal is a normal exit. Each detected death gets **one bounded in-place attempt** (dial + ASSOCIATE, 5 s default budget, no backoff, no retry budget). Same address family ⇒ the new relay endpoint is published, the attached flows keep their session, relay socket, and alias, and `udpAssociationRecovered` + the debug `udp.association.recovered` event record it. A changed address family or a failed recovery faults the association; each attached transport then fails closed on its next send with `UdpAssociationLostException`.
- **Association loss is not a setup failure (I5).** The coordinator maps that exception to `UdpTeardownReason.AssociationLost`, counts `udpAssociationLost` once per send failure classified as association-lost (each attached flow's next send), and arms **no** setup cooldown, so the flow re-establishes on its next datagram (through the pool, which dials a fresh association). The same mapping covers the setup-queue flush window, which rides the same send path. In contrast a failed dial/ASSOCIATE is a setup failure: counted `udpSetupFailures` and the 1 s cooldown is armed.
- **Retention.** A shared association with no outstanding lease is kept warm for 60 s (`UdpAssociationPool.s_idleRetireTimeout`) and reused by a later flow; the pool's own 5 s maintenance child also retires a faulted zero-lease association. UDP *sessions* are retained for `udpSessionIdleSeconds` (default 30 s), and the sweeper's UDP leg derives its cadence from that value: `min(mainInterval, max(5 s, idle/2))` — 15 s at the defaults. Each relay socket's receive buffer is `udpRelayReceiveBufferKb` (default 128 KiB, range 16..1024), applied before bind.
- **Ownership (`async-lifetime.md`).** The pool and each association are owners: one `QuiescenceScope` each, an explicit one-shot teardown, and every watchdog/recovery child is a `scope.Run` child. `DurableCaptureBundle` disposes sweeper → UDP coordinator → **association pool** → UDP native pools → TCP, so the coordinator has drained every lease before the pool is released; the pool's drain seals, joins every lease holder, and only then closes the associations.
- **Hot path unchanged (R6/I3).** No pool interaction per datagram: the only additions on the established path are one `Interlocked` increment after the kernel accepted a send, a write-once flag for the first successfully decoded relay response, and one reference compare for the current relay publication. Skipped relay datagrams (unexpected source, oversized, malformed, connection reset) record nothing.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| `auto`, server `Unknown`, association has room | flow joins the least-loaded shared association; one control connection for up to `flowsPerAssociation` flows |
| Every shared association full, ceiling not reached | a new shared association is dialed (ceiling is a bound, not a preallocation) |
| Ceiling reached | the flow is served from a private per-flow association, never refused |
| `off` mode | one private association per flow; closed on its last lease release (rollback shape) |
| Two or more attached flows decoded a response | sticky `SharedOk`; sampling stops for the server |
| One responder + a sibling with ≥3 unanswered sends | sticky `PerFlowOnly`, one `udp.association.fallback` warn, `udpAssociationFallbacks`++ |
| Fewer than two attached flows | no verdict; keep sharing and sampling |
| Control stream ends (0-byte read) or faults | watchdog recovers in place once, or faults the association |
| Re-association returns the same address family | new relay published; sessions, relay sockets, and aliases survive; `udpAssociationRecovered`++ |
| Re-association changes family / fails / exceeds the 5 s budget | association faulted; each attached transport throws `UdpAssociationLostException` on its next send |
| Send against a faulted lease | `AssociationLost` slot removal, `udpAssociationLost`++, **no** setup cooldown |
| Failed dial/ASSOCIATE during setup | `SetupFailure`, `udpSetupFailures`++, 1 s setup cooldown armed |
| Association idle with no lease for 60 s | retired by the pool's 5 s maintenance sweep |
| Faulted association with no lease (last release raced the fault) | retired by the same sweep, never left in the live set |

### 5. Good/Base/Bad Cases

- Good: a permissive server carries 16 flows on one authenticated control connection at
  `--socks5-external`, one relay socket each; the recorded Step 2/3 churn runs measure
  **7,556.7** (`always`, independent re-run) / **7,564.7** (`always`, implementer) / **7,577.5**
  (`auto`) B/session against the Step 1 per-flow **13,066.5 B/session**
  (`benchmarks/results/2026-09-28-udp-reuse/`, 48-flow × 120 s sustained churn, out-of-process
  SOCKS5 server) — the saved share is the per-flow control connect + greeting + ASSOCIATE. In the
  same directory, the Step 4 `udp.sessionBudget` soak measures
  1.94–1.95 descriptors per live session at 100 new flows/s while the pooled head was saturated at
  256 flows per server (the pool's original 16 × 16 default, superseded by this task's caps) and
  1.06 in the pooled regime (rate 5), i.e. the pooling ratio is real but one relay socket per live
  flow remains.
- Base: a server that pins one client source port per association flips to per-flow associations
  after the first sampled window; attached sessions keep running and no datagram is lost to the
  flip (Step 3 records `udp.association.fallback: 0` for the permissive loopback server — the
  detector fired only on the pinning fake).
- Bad: placing a flow on a faulted or recovering association; indexing evidence by attach count;
  arming the setup cooldown for `AssociationLost`; sharing one relay socket across flows (the
  2026-08-07 determinism objection — still out of scope).

### 6. Tests Required

- `UdpAssociationPoolTests`: rent/return refcount, warm reuse, least-loaded placement, ceiling
  overflow to private associations, drain joins lease holders, single-flight teardown, no orphan
  sockets.
- `UdpAssociationCapabilityTests` / `UdpAssociationEvidenceLifetimeTests`: the pinning rule
  (positive, negative, single-attach `Unknown`), sticky verdict, `always`/`off` overrides, the flip
  keeping existing sessions alive, and the >`FlowsPerAssociation` lease case that the live-set
  model exists for.
- `UdpAssociationRecoveryTests`: watchdog-detected death, in-place re-association keeping sessions
  and sockets, family change faulting them with `AssociationLost`, no setup cooldown armed, no
  unobserved task exception, and the self-traffic registration swap after a rebind.
- `Socks5UdpTransportLeaseTests`: the lease is released exactly once on every construction-failure
  path; `off` reproduces per-flow associations.
- `UdpAssociationHeadTests` / `UdpProxyCompositionTests`: the default caps cover the default
  session capacity, the configured bounds reach placement distinctly, and the transposition guard
  pins the two adjacent placement bounds at the composition seam.
- `ScriptedSocks5UdpServerOrderingTests`: the scripted fake publishes its ASSOCIATE-reply counter
  **before** the reply bytes are readable, proven by a gate that holds the write open
  (`ScriptedSocks5UdpServer.AssociateReplyWriteGate`) while the test asserts the counter already
  reports the reply. A fixture that reports protocol progress must publish its counter before the
  bytes the client completes on: the client's rent completes on the read, so an increment after the
  write is racy by construction under suite load (the 09-30 flake read `Expected: 282, Actual: 281`
  at `UdpAssociationHeadTests.cs:110` while the pool's own shape assertions on the lines above had
  passed). The ten assertions that read `AssociateReplyCount` and depend on this order are
  `UdpAssociationHeadTests.cs:110,140`, `UdpAssociationCapabilityTests.cs:47,148,269,304`,
  `UdpAssociationRecoveryTests.cs:44,57`, `UdpAssociationEvidenceLifetimeTests.cs:173`, and
  `UdpAssociationPoolTests.cs:276`.
- `UdpSessionSetupTests`: an association lost while the setup queue flushes maps to
  `AssociationLost` with the counter and no cooldown, distinct from a genuine setup failure.

### 7. Wrong vs Correct

```csharp
// Wrong: derive the capability verdict from a per-association ring indexed by the attach count —
// after FlowsPerAssociation leases it reads mixed stale/live/released slots and both flips are wrong.
var lease = _ring[Interlocked.Increment(ref _attachCount) % _ring.Length];

// Correct: the association holds its live attached set; the sampler copies exactly those records.
internal (UdpAssociationEvidence[] Evidence, int Attached) SnapshotEvidence()
{
    lock (_evidenceGate) return _liveEvidence.Count == 0 ? ([], 0) : ([.. _liveEvidence], _liveEvidence.Count);
}
```

```csharp
// Wrong: the transport owns a control connection and re-dials per flow — one descriptor pair and
// one full SOCKS5 handshake per flow, the churn cost the pool exists to remove.
_control = await Socks5ControlConnection.ConnectAsync(server, token);
_relay = await _control.UdpAssociateAsync(token);

// Correct: the transport borrows a lease from the per-server pool and owns only its relay socket.
var lease = await _pool.RentAsync(server, cancellationToken);
return Socks5UdpTransport.Create(lease, _selfTraffic, ..., _relayReceiveBufferBytes);
```
