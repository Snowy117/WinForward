# UDP Relay Contracts

> How WinForward proxies UDP flows through a SOCKS5 UDP relay: datagram handoff, response reinjection, loop prevention, and cross-family relay setup. Split 2026-08-29 from the former monolithic NDISAPI file; NDISAPI transport basics live in [windows-ndisapi.md](./windows-ndisapi.md).

---

## UDP relay wiring (wired 2026-08-09, hardware pass pending)

- A proxy-decided UDP datagram is parsed for its payload (`IPUdpPacket.TryParseSpan`, `src/WinForward.Protocols/IPUdpPacket.cs`) and handed to `UdpProxyCoordinator.TrySendSpanAsync` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Send.cs`); the original frame is consumed (never reinjected) — the SOCKS5 UDP relay transport owns forwarding.
- SOCKS5 UDP responses arrive from the dynamic relay endpoint; `UdpResponseReinjector` (an `IUdpResponseSink`, both in `src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs`) rebuilds a complete Ethernet II + IPv4/IPv6 + UDP frame (`UdpFrameBuilder`, `src/WinForward.Protocols/UdpFrameBuilder.cs`, RFC 768 0→0xFFFF checksum inversion) with the real server as source and `originalFlow.Local` as destination, then injects toward MSTCP (host flow) or the origin adapter (forwarded flow).
- Relay-source validation is port + address-family (not exact `IPEndPoint`): `Socks5UdpTransport.ReceiveAsync` accepts a datagram whose source port equals the relay port and whose family matches the relay, even from a different IP (multi-homed/anycast relay); a different port or family is still rejected. IPv6 scope is deliberately not compared (the receive interface's scope legitimately differs from the relay's advertised scope). Re-tightening to exact-address equality would break multi-homed relays (RFC 1928 does not pin the reply source). A **local** target is the opposite case and validates exactly: `LocalUdpTransport.ReceiveAsync` accepts a datagram only from the configured endpoint's address bits, port, and family (`IsAcceptableLocalSource`), because that endpoint is one host the configuration chose; a reply from any other source is the `UnexpectedSource` skip.
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
- Loop prevention: `Socks5UdpTransport.Create` registers `(Udp, localSocketEndpoint, relayEndpoint)` in `SelfTrafficRegistry` from the flow's own association's relay endpoint before the first datagram, and releases the token on dispose — catch-all proxy rules never recursively intercept WinForward's own UDP relay traffic. A relayed flow's relay endpoint is fixed for the flow's life (its association never re-associates), so the transport registers one tuple and never replaces it. The factory takes the registry and builds the association.
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
- `IUdpProxyTransportFactory.CreateAsync(ProxyTarget, CancellationToken)` and the internal
  `Socks5UdpTransport.Create(association, SelfTrafficRegistry, ...)`
  (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`) do not accept the original flow's address
  family: the relay family is the flow's own association's negotiated relay family.
- Module boundary (seam moved 2026-10-05, task 10-05-local-dns-transport C1/C6): the transport seam
  (`IUdpProxyTransport`, `IUdpProxyTransportFactory`, `IUdpExchangeCounters`,
  `UdpAssociationLostException`, and the neutral receive vocabulary `UdpTransportReceiveResult` /
  `UdpTransportSkipReason` / `UdpTransportDatagram`) lives in
  `src/WinForward.Runtime/UdpProxy/UdpTransportContracts.cs`, beside the coordinator and session
  that own it. It is transport-neutral by construction, and it now has two implementations:
  `Socks5UdpTransport` (the relayed path, which maps its decoded wire datagram into
  `UdpTransportDatagram` field for field on receive — a readonly-record-struct copy, no allocation
  on the receive path) and `LocalUdpTransport` (`UdpProxy/LocalUdpTransport.cs`: one socket per flow
  pointed at a `localTargets` endpoint, payload forwarded verbatim, replies accepted only from that
  endpoint and declared to have come from the flow's own destination).
  `IUdpProxyTransportFactory.CreateAsync` takes the resolved `Configuration.ProxyTarget` — the
  SOCKS5-server/local-endpoint union — and the seam's own `UdpTransportFactory` composite dispatches
  by kind, refusing a target that carries neither, so a further implementation slots in behind the
  same decision point without touching the coordinator, the session, or the reinjector. The
  dependency for the contract therefore runs Socks5→UdpProxy; the two concrete factories are still
  chosen by the composition root (`src/WinForward.Cli/UdpProxyComposer.cs`). The datagram wire codec
  itself (`Socks5UdpDatagram`, `Socks5UdpCodec`) stays in `src/WinForward.Protocols/Socks5Udp.cs`.

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
- Proxy setup errors remain fail-closed. Socket, UDP self-traffic token, and the flow's
  association are each released when owned; disposal continues through
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
  5 s at the default 30 s retention and the 5 s one-shot class (see the retention section below) —
  are unchanged.

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
- `Socks5UdpTransportFactory(SelfTrafficRegistry selfTraffic, int maximumFrameSize, int relayReceiveBufferBytes = DefaultRelaySocketReceiveBufferSize, Socks5AddressCache? addressCache = null, IRuntimeLogger? logger = null, Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null, Func<AddressFamily, Socket>? socketFactory = null, Action<Socket>? disableUdpConnectionReset = null)` — the factory dials one association per flow and owns no association itself; the frame cap stays required, and the internal `Socks5UdpTransport.Create(association, …)` seam mirrors the cap and buffer with defaults (`UdpFrameBuilder.DefaultMaximumEthernetFrame`, `DefaultRelaySocketReceiveBufferSize`).
- Per-transport cached `_receiveSenderTemplate` (`IPEndPoint`, ctor-computed from the relay address family).

### 3. Contracts

- **Forward leg passes `Endpoint` straight through** (`UdpProxySession.SendSpanAsync` → transport): no `ToIPAddress()`/`new IPEndPoint` materialization anywhere on the send path; the transport encodes via `Socks5UdpCodec.TryEncode(IPAddressValue, ...)` and the actual `SendTo` target is one `SocketAddress` the association serialized once at connect time and the transport cached at construction (see the ownership section below).
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
  `SetupFailure` arms the 1 s setup cooldown. An association whose control stream ended is
  `AssociationLost` — counted, and deliberately without the cooldown (see the ownership section
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
- Sweep gates (task 09-30-expiry-sweep-bounded-pause): `SweepAllocationGateTests.UdpProxyCoordinatorSweepAllocatesNoManagedBytes` (no-op tick over 16 fake-transport sessions — the tick that repeats every 5 s — 272 B → 0) and `…UdpAssociationTableSweepAllocatesNoManagedBytes` (retiring tick over 64 idle associations, 4,184 B → 0). Both hold a reused scratch and a `SemaphoreSlim(1,1)` (or `Lock`) sweep gate. The **two-class** tick has its own gate in `UdpAdaptiveSweepAllocationGateTests.UdpProxyCoordinatorAdaptiveSweepAllocatesNoManagedBytes` (the three-argument overload over 16 fake-transport sessions driven past the short-class pre-filter cutoff and inside the long class's, so the candidate scan, the per-candidate class read and the per-class cutoff comparison all run and nothing retires); its window contract is the same, and injecting one `new byte[64]` inside the window failed it at `Actual: 88` before restoring.

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

## UDP association ownership: one flow, one authenticated association (rewritten 2026-10-05, task 10-05-remove-udp-association-sharing)

> Supersedes the former "UDP association pooling" section. Sharing was removed by decision: at
> concurrency a shared association misdelivers replies (4–7 of 48 churn flows received their own
> reply; 3 of 48 in the burst scenario), and the misdelivery is undetectable when two flows share a
> destination, so no detector could ever demote a server on that evidence. A flow's association is
> now its own and is never shared.

### 1. Scope / Trigger

- Trigger: any change to `Socks5UdpAssociation` / `Socks5UdpTransportFactory` /
  `Socks5UdpTransport`, the relay-socket contract, `UdpTeardownReason`, or the UDP side of
  `DurableCaptureBundle` composition.
- `UdpAssociationTable` / `UdpAssociation` stay and are now only the alias registry: the original
  key ↔ `(local relay endpoint, relay endpoint)` pair that makes a relay reply classifiable as the
  reverse of a stored flow, plus the per-flow `Generation` the log fields print (the flow's
  association correlation id, not a re-association epoch).
- Removed with the feature: `UdpAssociationPool`, `UdpControlAssociation`, `UdpAssociationLease`,
  `UdpAssociationEvidence`, `UdpServerCapability` + `UdpAssociationCapabilitySampler`,
  `UdpAssociationReuseMode`, the in-place re-association, and the `udpAssociationReuse` /
  `udpAssociationMaxPerServer` / `udpAssociationFlowsPerAssociation` keys.

### 2. Signatures

- `Socks5UdpTransportFactory(SelfTrafficRegistry, int maximumFrameSize, int relayReceiveBufferBytes = DefaultRelaySocketReceiveBufferSize, Socks5AddressCache? addressCache = null, IRuntimeLogger? logger = null, Func<Socks5Server, CancellationToken, ValueTask<Socks5ControlConnection>>? createControl = null, Func<AddressFamily, Socket>? socketFactory = null, Action<Socket>? disableUdpConnectionReset = null)` (`Socks5/Socks5UdpTransport.cs`) — one factory per composition; every `CreateAsync(ProxyTarget, ct)` dials a fresh association for the one flow it serves. The factory owns no association between calls; it owns the `udp.association.lost` log throttle every association it creates reports through.
- `Socks5UdpAssociation.ConnectAsync(Socks5Server, Socks5UdpAssociationContext, CancellationToken)` (`Socks5/Socks5UdpAssociation.cs`): dials the authenticated `Socks5ControlConnection`, sends `UDP ASSOCIATE`, publishes the relay endpoint and its serialized form, and starts the control-stream watchdog. Surface: `RelayEndpoint`, `RelaySocketAddress`, `RelayAddressFamily`, `Server`, `Fault` (`UdpAssociationLostException?`), `IsFaulted`, `DisposeAsync`.
- `Socks5UdpTransport.Create(association, selfTraffic, socketFactory, disableUdpConnectionReset, maximumFrameSize, relayReceiveBufferBytes)` binds the relay socket in the association's relay family and returns a transport owning **both** the association and the socket.
- `UdpProxyCoordinator.ReceiveWindowRetireFloor = 64` (internal const): the fixed floor of the receive-window pool's retire allowance, `max(ReceiveWindowRetireFloor, sessionCapacity / 16)`. It is the one surviving number from the pooling configuration (four fault bursts of the sixteen flows one control connection used to serve).

### 3. Contracts

- **One flow, one association, one relay socket (R2).** The transport's association is dialed, ASSOCIATEs, and is disposed with the flow; nothing about it is pooled, leased, refcounted, or re-associated. The local relay port still identifies the flow, so `RelayAlias` uniqueness, the per-flow self-traffic tuple, and reverse routing are unchanged. The descriptor floor is now two per live flow (the control connection and the relay socket), and the kernel receive-buffer estimate stays `live sessions × udpRelayReceiveBufferKb` (one relay socket per flow, unchanged).
- **The association owns the control connection, its relay publication, and the watchdog.** The relay endpoint is published once before the transport is handed the association and is never replaced, so `PeerEndpoint` is fixed for the transport's life and the send path uses the `SocketAddress` the association serialized once at connect time (no per-send rebind, no reference compare). The watchdog blocks on the control stream; a 0-byte read (EOF), a stream fault, or an `IOException`/`SocketException` on that read is the association's death. RFC 1928 defines no control-connection traffic after ASSOCIATE, so any received byte is not death and the read continues.
- **Death is fail-closed and explicit (R5).** The watchdog records one `UdpAssociationLostException` (the control-stream death as its inner exception), emits the rate-limited `udp.association.lost` warn with `proxy`/`relay`/`reason`, and closes the dead control connection. The transport refuses every later datagram with that stored exception *before* its socket and *before* its send gate, so a dead association can never write to a relay no one is watching. There is no in-place recovery and no address-family follow: the flow's next datagram establishes a new association.
- **Association loss is not a setup failure (I5).** The coordinator maps the exception to `UdpTeardownReason.AssociationLost`, counts `udpAssociationLost` once per send failure classified as association-lost, and arms **no** setup cooldown, so the flow re-establishes on its next datagram with a fresh association. The same mapping covers the setup-queue flush window, which rides the same send path. A failed dial/ASSOCIATE is a setup failure: counted `udpSetupFailures` with the 1 s cooldown armed.
- **Ownership and disposal ordering (`async-lifetime.md`).** Each association is an owner: one `QuiescenceScope` whose only child is the watchdog, and an explicit one-shot teardown. `Socks5UdpTransport.DisposeAsync` releases, in order, the relay socket → its self-traffic token → the association (sealing the scope **before** closing the control connection, so the watchdog's faulted read is a teardown rather than a death), and continues through later releases when an earlier one throws. The send gate is deliberately left undisposed: disposing a `SemaphoreSlim` with waiters parked strands those waits forever, and a stranded sender would hold its caller's work lease (see [hot-path.md](./hot-path.md), "The UDP transport's disposal guard is outside the warm shape"). `DurableCaptureBundle` disposes sweeper → UDP coordinator → UDP native pools → TCP: the coordinator's drain disposes every session's transport, and with it every flow's own association, so nothing association-shaped is released separately.
- **Setup failure releases what it acquired.** A relay socket that cannot be created, an unappliable receive buffer, or a failed bind releases the association the factory just dialed (closing its control connection); a control dial or ASSOCIATE failure releases the half-built association. No path leaves a control connection behind.
- **The flow's own exchange counters stay on the transport (I3).** `IUdpExchangeCounters` is implemented by `Socks5UdpTransport` over its own two fields: one `Interlocked` increment after the kernel accepted a send, and a write-once flag for the first successfully decoded relay response. Skipped relay datagrams (unexpected source, oversized, malformed, connection reset) record nothing. The one-shot retirement class and the sweep allocation gates are unchanged; a transport that does not implement the interface is classified *sustained*.
- **Hot path unchanged (R6/I3).** No association interaction per datagram beyond one volatile fault read for fail-closed: the only additions on the established path are the send counter, the write-once response flag, and the cached `SocketAddress` handed to `SendTo`.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| A flow sets up | one control connection, one `UDP ASSOCIATE`, one relay socket, one alias claim — all owned by the flow's transport |
| Two concurrent flows | two control connections, two ASSOCIATE replies, two relay sockets (the 1:1 shape; `Socks5UdpAssociationOwnershipTests`) |
| Control stream ends (0-byte read) or faults | watchdog records `UdpAssociationLostException`, emits the rate-limited `udp.association.lost` warn, closes the control connection |
| Send after that death | transport throws the stored `UdpAssociationLostException` before its socket; `AssociationLost` slot removal, `udpAssociationLost`++, **no** setup cooldown |
| Flow's next datagram after the loss | a fresh setup: new dial, new ASSOCIATE, new relay socket, served normally |
| Failed dial/ASSOCIATE during setup | `SetupFailure`, `udpSetupFailures`++, 1 s setup cooldown armed |
| Relay socket create/apply/bind failure after a successful ASSOCIATE | the association is released (control connection closed) and the setup fails closed |
| Transport disposed | relay socket closed, self-traffic token released, association disposed (watchdog joined, control connection closed), once |
| Association death while the setup queue flushes | the same `AssociationLost` mapping as the ready path (no cooldown, not a setup failure) |

### 5. Good/Base/Bad Cases

- Good: two churn flows are served by two associations; each flow's reply arrives on the socket that sent it, so no reply can be delivered to a sibling — the ambiguity a shared association has cannot occur.
- Base: a server that drops a flow's control connection mid-flow loses that flow only; the flow re-establishes on its next datagram with no cooldown and no sibling impact.
- Bad: sharing one control connection across flows (removed: misdelivers at concurrency); keeping a warm association for reuse (removed: a stale-reply window); arming the setup cooldown for `AssociationLost`; reusing one relay socket across flows (alias ambiguity — out of scope by construction).

### 6. Tests Required

- `Socks5UdpAssociationOwnershipTests`: two concurrent flows ⇒ two control connections, two ASSOCIATE replies, two relay sockets; both the relay socket and the control connection are released when each flow's transport is disposed.
- `UdpAssociationLossTests`: a control connection dying mid-flow fails the flow closed (the send path throws, the slot is removed as `AssociationLost` with no setup cooldown and the `udp.association.lost` warn) and the next datagram re-establishes and is served; the death leaves no unobserved task fault; the setup-flush window maps to the same reason.
- `Socks5UdpTransportFactoryTests`: the association is released on every relay-socket construction-failure path; the transport reports the negotiated relay and releases the association on dispose; dispose is idempotent; the configured relay receive buffer reaches a real socket.
- `UdpReceiveResilienceTests` / `Socks5UdpConnresetTests` / `Socks5UdpTransportSendTests` / `Socks5UdpAssociateTests`: the per-flow factory's loop-prevention tuple, pre-bind SIO_UDP_CONNRESET, allocation-free warm send, cross-family ASSOCIATE, and skip-class receive contracts stay green.
- `UdpProxyCompositionTests`: the session-capacity/relay-buffer transposition guard at the composition seam (the composition carries no association owner).
- `SweepAllocationGateTests`: the coordinator and alias-table sweep gates stay green.

### 7. Wrong vs Correct

```csharp
// Wrong: the transport borrows a shared association from a pool, so two flows can send through
// one authenticated control connection and the server can answer the wrong one.
var lease = await _pool.RentAsync(server, cancellationToken);
return Socks5UdpTransport.Create(lease, _selfTraffic, ...);

// Correct: the factory dials, ASSOCIATEs, and binds per flow, and the transport owns both halves.
var association = await Socks5UdpAssociation.ConnectAsync(server, _associations, cancellationToken);
return Socks5UdpTransport.Create(association, _associations.SelfTraffic, ...);
```

```csharp
// Wrong: follow a re-association in place — the endpoint moves under a live flow and its relay
// socket, which is exactly the ambiguity the ownership move removed.
if (_lease.IsFaulted) throw new UdpAssociationLostException(..., _lease.Fault);

// Correct: the association is the flow's own, so death is the flow's death — refuse the datagram
// and let the next one establish a fresh association.
if (_association.Fault is { } lost) throw lost;
```

---

## UDP session retention and the relay receive buffer (two-class retention)

### 1. Scope / Trigger

- Trigger: any change to the `udpSessionIdleSeconds` key, `UdpProxyCoordinator.OneShotIdleTimeout`,
  `IdleExpirySweeper.EffectiveUdpRetentionFloor` or the UDP sweep cadence, `IUdpExchangeCounters`, or
  `udpRelayReceiveBufferKb`.

### 2. Contracts

- **Two-class session retention.** UDP *sessions* keep `udpSessionIdleSeconds` (default 30 s) **except a
  completed one-shot exchange** — a session whose flow sent at most `OneShotDatagramThreshold = 1`
  datagram and has already received a response — which is retained for
  `UdpProxyCoordinator.OneShotIdleTimeout = 5 s`. The class is read from the per-flow exchange evidence
  (`IUdpExchangeCounters`, implemented by `Socks5UdpTransport` over its own send/response counters and by
  `LocalUdpTransport` over its own — the local implementation is what makes a completed one-shot retire
  at the 5 s class for a local target too; a transport that does not implement the interface is
  classified sustained), once per idle candidate per sweep tick, and the classification is monotone (the
  counters only grow, the response flag is write-once).
- **The sweep cadence derives from the effective retention floor.** `IdleExpirySweeper.EffectiveUdpRetentionFloor`
  is the shorter of the configured retention and the one-shot class, and the UDP leg runs at
  `min(mainInterval, max(5 s, floor / 2))` — **5 s at the defaults**, 15 s under uniform retention. Both
  classes' TTLs and the never-early rule are unchanged; the bounds move with the tick (long class
  45.5 s → 35.5 s, short class `(5, 10.5] s`). One other retention rides the same tick: the F8
  attribution pending entry tightens `(5, 20] s → (5, 10] s` (see [error-handling.md](./error-handling.md)).
- **The relay receive buffer is per session, and the ownership move did not change it.** Every relay
  socket applies `udpRelayReceiveBufferKb` (default **64 KiB**, range 16..1024;
  `udpRelayReceiveBufferKb: 128` restores the historical value) before bind, and the kernel aggregate
  estimate stays `live sessions × the configured value` — one relay socket per live flow, whether or not
  its control connection is shared.

### 3. Tests Required

- `UdpSessionRetentionTests` (class selection, `EffectiveUdpRetentionFloor`, sweep-cadence derivation),
  `LocalUdpTransportRetentionTests` (the local transport's own counters feed the same class), and
  `HotPathAllocationGateTests` (the counters add no allocation to the datagram path).

---

## Reply-ownership observability: the foreign-source counter (wired 2026-10-05, task 10-05-reply-ownership-observability)

### 1. Scope / Trigger

- Trigger: any change to `UdpProxySession.TryGetReceiveSource` / `InjectResponseAsync`, the
  `udpResponseSourceMismatch` counter, or the `udp.response.foreign_source` event.
- Pre-change state: the decoded reply source was handed straight to the reinjector as the injected
  frame's source without ever being compared to `Flow.Remote`, so a reply the server delivered to the
  wrong flow was injected toward that wrong flow's client as a legitimate frame and nothing recorded it.

### 2. Signatures

- `UdpProxySession.TryGetReceiveSource(UdpTransportReceiveResult receive, out Endpoint source)` — signature
  and return contract unchanged; only the observational call below is new.
- `RuntimeCounters.UdpResponseSourceMismatch = "udpResponseSourceMismatch"`
  (`src/WinForward.Runtime/RuntimeCounters.cs`) — cumulative, beside `udpOriginUnresolved` /
  `udpFailClosedDrop`. No registration step exists: `RuntimeCounters.Snapshot()` and the heartbeat pick
  up any key that has been incremented, and the heartbeat reports it as `udpResponseSourceMismatch=<delta>`
  in the 60 s `runner.heartbeat` line whenever the delta is non-zero.
- Event `udp.response.foreign_source` at **warn**, fields `destination` (the flow's own), `source` (the
  reply's declared source), `origin` (the flow's origin kind) and `udpAssociation` (the association
  generation).

### 3. Contracts

- **Count, then deliver.** One peer comparison against `Flow.Remote`; on a mismatch the counter is
  incremented and the warn may be emitted, and the method then returns `true` with that same decoded
  source. `InjectResponseAsync` and `UdpResponseReinjector` are untouched, so the delivered frame's
  source is the reply's declared source exactly as before. The observation is **non-dispositional by
  contract**: `RuntimeCounters` counters "must never influence packet disposition, fail-closed, relay, or
  shutdown decisions".
- **The comparison is `MatchesPeerIgnoringScope`, not `Endpoint` equality.** The decoded source's IPv6
  scope comes from the relay socket's bind address (`Socks5UdpTransport` propagates it into the decode)
  while the flow's destination scope comes from the captured packet, so the two legitimately differ for
  one link-local peer; `Endpoint.Equals` compares `IPAddressValue.ScopeId` and would count that as a
  foreign source. This is the same scope-independence the relay-source validation above already applies,
  and the same call `TcpRedirectAcceptor` makes when it compares an accepted socket endpoint against a
  wire-decoded one. A real foreign source differs in address bits and still counts.
- **A mismatch is not an error.** Relay-source validation is deliberately port + address-family rather
  than exact `IPEndPoint` (the wiring section above), because RFC 1928 does not pin the reply source and
  multi-homed/anycast relays answer from a different address; TFTP-style exchanges also continue from a
  new endpoint by design. So the count is sound and dropping on it is a separate, riskier decision this
  task does not take — the count is the evidence that would make that decision.
- **The counter is cumulative; only the warn is windowed.** The throttle is the CAS-on-ticks shape of
  `MaybeLogSkipSummary` with the same 5 s `s_rateLimitedLogInterval`, but the counter is a lifetime total
  the log path never drains. Do not route it through `RecordSkippedDatagram` or add a
  `UdpTransportSkipReason` member: that path means "not delivered", and reusing it would silently
  convert the observation into a drop.
- **Cost.** One struct `Endpoint` peer comparison (`MatchesPeerIgnoringScope`: port, family and raw
  address bits — no allocation, no boxing) plus one interlocked increment, which is unconditional; a
  suppressed window costs one clock read, one interlocked read and a compare, and only a window that
  actually trips pays the compare-exchange and the event formatting. A domain-typed response
  (`DestinationAddress == null`) keeps its existing skip path and never reaches the comparison.
- **The warn is throttled per session, so a fleet of foreign-source sessions is a fleet of lines.**
  The 5 s window is per `UdpProxySession`, deliberately: each session reports its own evidence, and the
  first mismatch on a session is never suppressed. N concurrent sessions whose peer answers from another
  endpoint therefore produce up to N warns per window. That volume is the signal rather than a defect to
  damp — one line per session per window is exactly the evidence that a specific set of flows is seeing
  another source — and the cumulative counter, not the line count, is the measurement. Read a flood as
  "the server is answering this population from an unexpected endpoint at a sustained rate", and use
  `udpAssociation` plus the address pair in the line to see which flows and which peer are involved.
  A coarser (global) throttle would lose the per-session attribution this event exists for; do not add
  one without a replacement for that attribution.
- **The counter's blind spot is now structural, not a gap.** Two flows to the same destination are
  indistinguishable by address: a reply delivered to the wrong one carries the source the receiving flow
  also expects, so it never reaches the comparison. That was the undetectable misdelivery shape of a
  shared association, and it is exactly why sharing was removed (design §4 of task
  10-05-remove-udp-association-sharing): a flow's relay socket belongs to that flow alone, so a reply
  can only arrive on the socket that sent the request, and cross-flow misdelivery cannot occur. The
  counter therefore observes a server answering from an unexpected endpoint, not proxy misdelivery.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Reply source ≠ `Flow.Remote` | counter++, warn (5 s throttle), reply injected with its declared source, session survives |
| Reply source = `Flow.Remote` | no counter, no event, reply injected |
| Reply source = `Flow.Remote` up to the IPv6 scope (link-local peer, relay socket's scope vs captured scope) | not a mismatch: no counter, no event, reply injected |
| Several mismatches inside one window | every reply counted, exactly one warn for the window per session |
| N sessions each misdelivering inside one window | up to N warns (per-session throttle), every reply counted |
| Two flows to one destination, cross-delivered reply | no counter (address-indistinguishable), reply injected as the receiving flow's own |
| Domain-typed response (`DestinationAddress == null`) | unchanged `RecordSkippedDomainDestination` skip; never the mismatch counter |
| Counter read while no mismatch ever occurred | key absent from the snapshot; the heartbeat omits zero deltas (an absent field is not evidence of safety) |

### 5. Tests Required

- `UdpResponseSourceMismatchTests`: a foreign-source reply is counted, delivered with its declared source,
  and the session keeps sending; a matching reply does not move the counter and emits no event; an IPv6
  reply from the same address bits with a different scope does not move it either (no scope false
  positive), while the same scope with different address bits does (the control that keeps the scope
  tolerance from swallowing a real mismatch); a burst inside one window counts every reply and warns once,
  and the next window warns again; two flows to the same destination with a cross-delivered reply do not
  move the counter (the blind-spot pin). `RuntimeCounters.Shared` is process-wide, so read it as a
  before/after delta.
- `RuntimeCountersTests.SharedInstanceExposesTheStandardVocabulary` pins the key string.
- `HotPathAllocationGateTests` stays green: the comparison adds no allocation to the UDP path.

### 6. Wrong vs Correct

```csharp
// Wrong: the observation becomes a filter — the counter's verdict decides delivery, the reply is
// reported as "not delivered", and a legitimate TFTP-style endpoint change is lost.
if (source != Flow.Remote)
{
    RecordSkippedDatagram(UdpTransportSkipReason.UnexpectedSource);
    return false;
}

// Correct: count, then fall through to the unchanged return — the reply keeps its declared source
// and is delivered exactly as before. The peer comparison ignores the scope, which belongs to the
// receiving interface rather than to the peer.
if (!source.MatchesPeerIgnoringScope(Flow.Remote))
{
    RecordForeignSource(source);
}

return true;
```

---

## UDP over TCP per flow: one flow, one stream connection (wired 2026-10-06, task 10-06-uot-per-flow-transport)

> The opt-in second carriage for a SOCKS5 target, and the extension of the ownership section above
> from **one flow per association** to **one flow per connection**. UoT v2 connect mode puts a flow's
> datagrams on a TCP stream bound to the flow's single destination, so the flow's descriptor floor
> drops from two to one, the setup round trip the native path serializes behind `UDP ASSOCIATE`
> disappears from the first datagram's path, and the reply-source question disappears with the wire
> field that carried it. The native relay stays the default: this is one `udpOverTcp` field on a
> `Socks5Server`, off unless set.

### 1. Scope / Trigger

- Trigger: any change to `Socks5UotTransport`, the `UdpOverTcp` branch of
  `Socks5UdpTransportFactory.CreateAsync`, `UotCodec` or `Socks5Messages`' domain `WriteRequest`
  overload, `UdpProxyLogging.UdpTransportOf`, or the two typed transport exceptions in
  `UdpProxy/UdpTransportContracts.cs`.
- Composition is unchanged: UoT is a **mode of the `socks5` target kind**, not a third kind. The
  `UdpTransportFactory` composite, `UdpProxySession`, the coordinator's send/admission path, the
  response reinjector, the alias table, and the two-class retention are untouched.
- The native path is untouched by construction: `Socks5UdpTransportFactory.CreateAsync` branches on
  `server.UdpOverTcp` and every other server keeps the association + relay-socket path of the
  ownership section above.

### 2. Signatures

- `Socks5UotTransport : IUdpProxyTransport, IUdpExchangeCounters`
  (`src/WinForward.Runtime/Socks5/Socks5UotTransport.cs`). `PeerEndpoint` is the SOCKS5 server
  endpoint (the connection's remote) and `LocalEndpoint` the connection's local socket endpoint:
  both are per-flow because the connection is, so `UdpSessionSetup`'s alias claim
  (`UdpSessionSetup.cs:102-110`) is unchanged and unique by construction.
- `Socks5UotTransport.DialAsync(server, context, ct)` dials
  `Socks5ControlConnection.ConnectDeferredHandshakeAsync` — the same attempt loop, address cache and
  before-the-SYN self-traffic registration as the native association, but the dial writes the
  greeting (and the RFC 1929 message when credentials are configured) and returns **without reading
  a reply**. `Create(control, maximumFrameSize)` takes the stream and socket, sets
  `socket.Blocking = false` for the warm send path, and sizes its send buffer
  `30 + UotCodec.MaximumRequestHeaderLength + UotCodec.FrameHeaderSize + maximumFrameSize` =
  `52 + maximumFrameSize`: the 30-byte magic `CONNECT`, the longest UoT request header (IPv6,
  20 bytes) and the 2-byte frame prefix, then the payload.
- `UotCodec` (`src/WinForward.Protocols/UotCodec.cs`): `MagicAddress = "sp.v2.udp-over-tcp.arpa"`,
  `Version = 2`, `RequestHeaderLength(AddressFamilyKind)`, `TryWriteRequestHeader(bool isConnect,
  IPAddressValue, ushort, Span<byte>, out int)`, `FrameHeaderSize = 2`, `TryWriteFrameHeader`. The
  request destination carries the **shared SOCKS address types**
  (`Socks5Messages.AddressTypeIPv4`/`AddressTypeIPv6`, with `Socks5Messages.AddressFieldLength` the
  one decode mapping the fixtures share); `isConnect: false` is refused, because the per-datagram
  `0x00`/`0x01`/`0x02` form of protocol version 1 is not implemented by this codec.
- `UdpTransportHandshakeRejectedException : IOException` beside `UdpAssociationLostException`
  (`UdpProxy/UdpTransportContracts.cs`); `UdpProxyCoordinator.TeardownReasonFor`
  (`UdpProxyCoordinator.Send.cs:214-220`) classifies both, and
  `RemoveReceiveFailedSessionCoreAsync` (`UdpProxyCoordinator.cs:563-575`) classifies the session's
  recorded fault through the same helper instead of hard-coding `Fault`.
- `UdpProxyLogging.UdpTransportOf` (`UdpProxyLogging.cs:42-46`): `"uot"` for a `Socks5Server` with
  `UdpOverTcp`, `"native"` for any other SOCKS5 server, null for a local target or a targetless
  event.

### 3. Contracts

- **One flow, one connection, owned and disposed with it (R1).** The transport owns the control
  connection for the flow's whole life; nothing is pooled, leased, or reused across flows, and
  `DisposeAsync` releases the connection (which owns the socket, the stream, and the self-traffic
  tuple), exactly once, through every path; the send gate is deliberately left undisposed so a parked
  sender is never stranded (same rule and rationale as the native transport, [hot-path.md](./hot-path.md)).
  A connection whose construction failed is released by the factory's catch, mirroring the native
  association path.
- **Pipelined establishment (R2).** The dial writes the greeting `[+ credential message]` and never
  reads; the first send writes one buffer — the domain-typed `CONNECT` to `UotCodec.MagicAddress`,
  the UoT request header with `isConnect = 1`, and the first `u16be length | payload` frame — and
  does not await the `CONNECT` reply. Reply validation moves to the receive path, whose first call
  consumes, in the order the server wrote them, the method selection, `[+ the credential reply]`,
  and the `CONNECT` reply status before the first frame. No handshake reply is on the first
  datagram's critical path.
- **Framing (R3).** Datagrams are `u16be length | payload` on the stream. The receive loop reads the
  2-byte prefix and the payload separately (`ReadExactlyAsync`), so a frame split across segments is
  reassembled rather than dropped; a zero-length frame is a legal empty datagram; a frame longer than
  the caller's buffer is **consumed** to keep the stream aligned and reported
  `UdpTransportSkipReason.Oversized`. One writer gate per transport serializes frames — two
  concurrent sends must never interleave the 2-byte prefixes — and the payload is encoded into the
  transport's reusable send buffer before any await, so a capture buffer's span lifetime stays legal
  (the contended-gate path is the only one that copies).
- **Fail-closed guards.** A later send whose destination differs from the one the first send
  captured, a payload above `ushort.MaxValue`, and a payload above the transport's frame ceiling all
  throw rather than mis-frame the stream. Connect mode binds the stream to one destination for its
  life; silently framing a second destination is the one unacceptable outcome.
- **Typed faults, and never a raw stream fault (R4).**

  | Observed | Recorded and thrown | Classified as |
  |---|---|---|
  | A non-success `CONNECT` reply, EOF before it, or the setup window elapsing | `UdpTransportHandshakeRejectedException` | `UdpTeardownReason.SetupFailure` — the setup cooldown is armed (1 s), exactly as a refused `UDP ASSOCIATE` |
  | EOF / RST / any stream fault after establishment | `UdpAssociationLostException` | `UdpTeardownReason.AssociationLost` — counted as `udpAssociationLost`, **no** cooldown, the flow re-establishes on its next datagram |

  The transport records the first fault and rethrows the same instance from every later send and
  receive, so a dead stream refuses datagrams before the socket. **A stream fault must never surface
  as the datagram-level `ConnectionReset` skip**: `UdpProxySession`'s receive loop treats that code as
  a one-datagram anomaly and continues (`UdpProxySession.cs:353-359`) — correct for an ICMP
  port-unreachable answering a UDP send, an infinite spin on a dead stream. Every `IOException` /
  `SocketException` observed on a live, non-cancelled transport is translated into one of the two
  types above before it leaves. The `CONNECT` reply read is additionally bounded by the transport's
  own setup window (30 s), so a server that accepts the exchange and never answers fails the flow's
  setup instead of parking its receive loop until idle expiry.
- **The descriptor budget is one per live flow.** The flow's stream connection replaces both the
  native path's descriptors (the `UDP ASSOCIATE` control connection **and** the relay socket), so the
  floor is one local descriptor per live UoT flow against two per live native flow. The kernel
  receive-buffer estimate changes with it: `udpRelayReceiveBufferKb` is applied to a relay socket,
  and a UoT flow has none.
- **Reply source is synthesized, and a foreign source is structurally impossible.** Connect mode
  carries no on-wire source, so `ReceiveAsync` declares the flow's captured destination as every
  reply's `SourceAddress` (and therefore as the reinjected frame's source). The
  `udpResponseSourceMismatch` counter and its `udp.response.foreign_source` warn cannot fire on this
  path: the observation is impossible by structure, not suppressed by policy. A frame that no send
  ever framed (no captured destination) is `UdpTransportSkipReason.UnexpectedSource` instead of a
  default source the session would count as foreign.
- **Retention is the transport's own evidence (I3/R5).** `IUdpExchangeCounters` is implemented from
  the transport's own observations: one increment after the kernel accepted a whole frame, and a
  write-once flag on the first successfully decoded frame (a skip records nothing), so the completed
  one-shot 5 s retirement class is preserved on this path exactly as it is for the native transport.
- **Observability.** `targetKind` stays `local|socks5` — UoT is a mode, not a third kind — and the
  session-lifecycle debug event gains `udpTransport=uot|native` beside it, so a column or a product
  event can attribute the carriage without a new event name and without changing `targetKind`.
- **Carried residuals, documented rather than fixed.**
  1. **The magic `CONNECT` request is 30 bytes, not 29**, because the FQDN `sp.v2.udp-over-tcp.arpa`
     is 23 bytes: `4 (VER 5 | CMD 1 | RSV 0 | ATYP 3) + 1 (length) + 23 + 2 (port)`. The send buffer
     follows from that (`52 + maximumFrameSize`); an earlier estimate assumed a 22-byte FQDN.
  2. **A rejection discovered on the receive path arms the cooldown without incrementing
     `udpSetupFailures` and without emitting `udp.setup.failed`.** Those two live in the setup
     pipeline's failure sink (`UdpSessionSetup.HandleSetupFailureAsync`, `UdpSessionSetup.cs:152-167`),
     which the receive path never calls: `RemoveReceiveFailedSessionCoreAsync` classifies the fault and
     removes the slot (`UdpProxyCoordinator.cs:563-575`), and only `RemoveSlotAsync`'s
     `SetupFailure` branch arms the cooldown (`UdpProxyCoordinator.cs:515-518`). The flow's next
     datagram still traces `udp.setup.cooldown` (`UdpProxyCoordinator.Send.cs:66`) and re-establishes
     after 1 s. Consequence for diagnosis: a systematically rejecting UoT server shows as cooldown
     churn on the trace, **not** as `udpSetupFailures` growth or as `udp.setup.failed` events — read
     the cooldown trace, not the counter. The setup/flush window is the one path that does count it
     (the same send path, reached through the setup pipeline).
- **Interop status.** [to confirm] The wire facts this section describes are pinned against the
  server in the task's `research/r7-server-verification.md` (sing-box `testing`
  @`2ff3985c`, sing @`6f21f24`). The encoding now matches that pin: the UoT request header's
  destination uses the ordinary SOCKS address types (`Socks5Messages.AddressTypeIPv4`/
  `AddressTypeIPv6`, shared with the SOCKS5 encoder), and both loopback fixtures decode it with the
  server's own mapping and refuse an undefined family as `unknown address family: <byte>`. What
  remains open is the runtime half: the memo's on-box probe has not run, so treat this section's
  wire claims as source-pinned rather than as verified interop. The R8 columns are
  internal-consistency evidence — the fixture is not the pinned server — not interop evidence.

### 4. Validation & Error Matrix

| Condition | Required result |
|---|---|
| Flow created on a server with `UdpOverTcp` | one deferred-handshake dial; greeting `[+ auth]` written, no reply read; endpoints published; queued datagram flushes |
| First send | one buffer: `CONNECT`(magic) + UoT request header + first frame; destination captured; no reply awaited |
| `CONNECT` reply after establishment | consumed before the first frame, in server order (method, `[+ credential reply]`, `CONNECT`) |
| Later send to a different destination | fail-closed throw (connect mode binds one destination) |
| Payload above `ushort.MaxValue` or the transport's frame ceiling | fail-closed throw |
| Frame split across TCP segments | reassembled by the prefix-then-payload reads |
| Frame longer than the receive buffer | consumed to keep the stream aligned, reported `Oversized` |
| Zero-length frame | delivered as a legal empty datagram |
| Frame on a flow that never sent | `UnexpectedSource` skip (no synthesized source) |
| Non-success `CONNECT` reply / EOF before it / setup window elapsed | `UdpTransportHandshakeRejectedException` → slot removed as `SetupFailure`, cooldown armed |
| Stream fault after establishment | `UdpAssociationLostException` → slot removed as `AssociationLost`, counted, no cooldown |
| Any raw `ConnectionReset` on a live transport | never escapes; translated to the established-phase type above |
| Transport disposed | connection released (socket, stream, self-traffic tuple), once; the send gate is never disposed, so a parked sender completes and is refused by the post-wait guard; a repeat dispose returns |
| Transport construction fails | the factory releases the control connection it just dialed |

### 5. Tests Required

- `Socks5UotTransportTests`: the frame-before-`CONNECT`-reply observation (a scripted server that
  reads the datagram before writing the reply), echo round trip, split-frame reassembly, oversized
  frame consumed and reported, zero-length frame, destination mismatch and oversized payload both
  fail closed, concurrent sends never interleave, zero managed allocations on the warm send, dispose
  releases the connection exactly once, self-traffic tuple registered before the SYN.
- `UdpUotFlowLifecycleTests`: `CONNECT` refusal → `SetupFailure` with the cooldown armed;
  mid-flow connection death → `AssociationLost` with no cooldown and a successful re-establishment
  on the next datagram; the flow's own `IUdpExchangeCounters` put a completed one-shot in the 5 s
  class.
- `UdpProxyLoggingTests`: `udpTransport` is `uot` for a `UdpOverTcp` server, `native` for every
  other SOCKS5 server, null elsewhere, with `targetKind` unchanged beside it.
- The native suites (`Socks5UdpAssociationOwnershipTests`, `UdpAssociationLossTests`,
  `UdpReceiveResilienceTests`, `Socks5UdpConnresetTests`, the retention and sweep gates) stay green
  unchanged — the mode's branch must not move the native path's behaviour.

### 6. Measured evidence

`benchmarks/results/2026-10-06-uot-per-flow/` carries the mode's column beside the native per-flow
column on one binary, one target field apart:
`fileDescriptorsPerSession` **2.00 → 1.00**, burst-establishment first-response p50
**156.87 → 57.12 ms** and p95 **307.89 → 58.11 ms**, churn p50 **155.5–165.2 → 57.7–64.8 ms**, with
zero misdelivery and zero loss in both columns and exactly one connection per flow in the counters
(`304 = 256 background + 48 burst`; `1816 = 1816` flows in the session-budget run). The same
directory states the limits of that evidence: loopback RTT and zero loss make the TCP-carriage cost
invisible, so the native relay stays the recommendation on lossy legs, and the mode remains
off-by-default whichever way the numbers read.

### 7. Wrong vs Correct

```csharp
// Wrong: let the stream's socket fault escape. The session's receive loop classifies a
// SocketError.ConnectionReset as a one-datagram skip (correct for an ICMP unreachable on a UDP
// send) and would spin on a dead stream instead of tearing the flow down.
catch (SocketException exception) { throw; }

// Correct: translate every fault observed on a live, non-cancelled transport into the flow's typed
// vocabulary, once, and rethrow the same recorded exception from every later send and receive.
catch (Exception fault) when (IsConnectionFault(fault, cancellationToken))
{
    throw RecordFault(new UdpAssociationLostException("... the flow must be re-established.", fault));
}
```

```csharp
// Wrong: treat the UoT stream like the native relay socket and register a relay tuple after the
// connection exists — the tuple must be in the registry before the SYN, or a catch-all proxy rule
// re-intercepts WinForward's own connection.
var control = await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server, ct, ...);
selfTraffic.Register(...);

// Correct: register through the dial's onSocketReady callback, exactly as the native association
// does; the transport owns the connection's disposal, which releases the token.
await Socks5ControlConnection.ConnectDeferredHandshakeAsync(server, ct,
    (local, remote) => selfTraffic.Register(new SelfTrafficRegistry.SelfTrafficKey(...)));
```
