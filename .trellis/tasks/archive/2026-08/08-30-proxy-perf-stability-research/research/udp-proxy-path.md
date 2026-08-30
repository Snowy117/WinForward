# Research: UDP Proxy Path Review (2026-08-30)

- **Query**: Read-only performance/stability review of the UDP proxy path.
- **Scope**: full read of `UdpProxy/*`, `Socks5/Socks5UdpTransport.cs`, `Protocols/Socks5Udp.cs`, `UdpFrameBuilder.cs`, `IPUdpPacket.cs`, `IPFragment.cs`, plus UDP-related config knobs; supporting types (`BoundedSetupQueue`, `FlowKey`/`Endpoint`/`IPAddressValue`, `NdisPacketBufferPool`, `PacketChecksums`, pump/sweeper callers).
- **Date**: 2026-08-30, baseline commit `e5667af`.
- **Excluded (already landed)**: D6 patient semaphore admission, D2 non-blocking sync-send warm path, D3 100ms activity-propagation throttling, C3 session bookkeeping 2.1KB→0.4KB/datagram, SIO_UDP_CONNRESET disabled with ConnectionReset-as-skip, host-flow reinjection via `FlowKey.OriginAdapterId`.

## A) Architecture Summary

**Forward (client → upstream)**: one NDIS pump per adapter (`MultiAdapterCaptureLoop.cs:8`) → `CapturePacketProcessor` → `FlowDispatcher.DispatchAsync` (resolved proxy flows take the sync warm path, `FlowDispatcher.cs:125-146`) → `NdisPacketActionExecutor.HandleUdpProxyAsync` (`NdisPacketActionExecutor.cs:132`): lease materialize → `IPUdpPacket.TryParse` extract payload → `UdpProxyCoordinator.TrySendAsync` (`UdpProxyCoordinator.cs:91`): single `_gate`, lookup `_sessions[FlowKey]`; new flow → `Task.Run(CreateSessionAsync)` (TCP control connect → UDP ASSOCIATE → bind non-blocking UDP socket, 512KB rcvbuf, SIO_UDP_CONNRESET off), datagrams during setup enter `BoundedSetupQueue` (32 pkts / 32KB, drop-oldest, first packet inline, per-packet `ToArray` copy); ready session sends inline → `UdpProxySession.SendAsync` → `Socks5UdpTransport.SendAsync` (per-transport `SemaphoreSlim` serialization; `TryEncode` into a shared 1536B buffer; synchronous `SendTo` when kernel-ready, else overlapped fallback).

**Reverse (upstream → client)**: one receive loop per session (`UdpProxySession.ReceiveLoopAsync`, ArrayPool 1537B buffer): `ReceiveFromAsync` → ConnectionReset skip class → source validation (port + family) → truncation check → `TryDecode` (scope propagation) → per-class exception counting/skip → `UdpResponseReinjector.InjectAsync`: resolve target adapter (host → MSTCP/ON_RECEIVE; forwarded → origin adapter + clientMAC/ON_SEND) → `NdisPacketBufferPool` (256 cap) rent → `UdpFrameBuilder.TryBuildInto` builds Eth+IP+UDP in place (vectorized checksum) → native inject → pool return. Recaptured responses pass through `FlowDispatcher.IsReverseOf` (`FlowDispatcher.cs:142,178`) for loop prevention.

**Idle**: `IdleExpirySweeper` PeriodicTimer 60s → `RemoveExpiredAsync` (UDP idle 2 min): LINQ snapshot under gate → `TryBeginExpiry` (requires `_activeSends==0`) → `RemoveSlotAsync` (ReferenceEquals ownership, drain queue, release association, dispose outside gate).

## B) Performance Findings

### P1. Send-path IPEndPoint round-trip allocation — `UdpProxySession.cs:115` (MED-HIGH)
`new IPEndPoint(destination.Address.ToIPAddress(), port)`: `ToIPAddress` (`IPAddressValue.cs:97-105`) allocates `byte[]` + `IPAddress`, then `new IPEndPoint` = **3 allocations per forwarded datagram**; `Socks5UdpTransport.cs:210` then converts back via `IPAddressValue.From(destination.Address)` — while the actual `SendTo` target is the fixed `RelayEndpoint` (`Socks5UdpTransport.cs:217`); `destination` only feeds the SOCKS5 header encode.
**Fix**: `IUdpProxyTransport.SendAsync` takes an `Endpoint`/`IPAddressValue` struct instead. **Impact: MED-HIGH** (the only significant warm-path allocation source).

### P2. Per-response IPAddress allocation — `Socks5Udp.cs:108` (MED)
`TryReadAddress` does `new IPAddress(bytes)` (internally copies byte[] again) = 2 allocations per response; the caller `UdpProxySession.cs:200` immediately converts to `IPAddressValue` and discards it.
**Fix**: decode straight to `IPAddressValue` (write UInt128, zero alloc). **Impact: MED**.

### P3. Send buffer vs jumbo mismatch — `Socks5UdpTransport.cs:267` (MED)
`_sendBuffer = 6+16+1514` hard-coded 1536B; coordinator and reinjector both accept `maximumFrameSize` (`UdpProxyCoordinator.cs:48`, `UdpResponseReinjector.cs:69`). On jumbo (9014) deployments, payload >1508 → `TryEncode` fails → IOException → session teardown → next datagram re-runs the full SOCKS5 handshake (see S3).
**Fix**: size the buffer `6+16+65535` or pass the frame cap through the factory. **Impact: MED** (jumbo only, but the consequence is a handshake storm).

### P4. Single global `_gate` — `UdpProxyCoordinator.cs:97-142` (MED)
All pumps (one per adapter) + 8-way concurrent setup + `FlushSetupQueueAsync` (`:340-350`, **takes the gate once per drained datagram**, up to 32×) + teardown + sweep share one lock. Critical sections are compact (dictionary ops); acceptable at current scale.
**Fix (if needed)**: hoist the Ready flag read to volatile/out-of-gate interplay, or shard the dictionary. **Impact: MED** (high pps + high session churn).

### P5. Sweep holds lock doing LINQ — `UdpProxyCoordinator.cs:368-371` (LOW-MED)
`Where().Select().ToArray()` under `_gate` enumerates up to 16K sessions, allocating delegate+enumerator+array per sweep. `UdpAssociations.cs:145` `RemoveExpired` is likewise LINQ+`Distinct()` (and has no production caller — see S5).
**Fix**: hand-rolled loop + reusable List, or snapshot-in-gate/filter-outside. **Impact: LOW-MED** (every 60s but under lock).

### P6. Per-receive sender template allocation — `Socks5UdpTransport.cs:271` (LOW)
`new IPEndPoint(IPAddress.Any, 0)` per call (family fixed at construction) + framework-internal `RemoteEndPoint` allocation.
**Fix**: cache a per-transport template. **Impact: LOW** (1–2 small allocations per response).

### P7. Send-path double locking on activity gate — `UdpProxySession.cs:105-121 + 308-315` (LOW)
Per send: 2× `lock(_activityGate)` + 1× `TouchActivity`; `_lastActivityTicks` is already Interlocked. `_activeSends` could be Interlocked, leaving the lock only for the `_expiring` transition.

### P8. Frame rebuild double-passes payload — `UdpFrameBuilder.cs:148,152` (LOW)
`payload.CopyTo` then `WriteUdpChecksum` re-scans the same bytes (copy pass + checksum pass). The checksum is already vectorized (`PacketChecksums.cs:285-324`); fusion is marginal.

### P9. One frame materialization per proxied datagram — `NdisPacketActionExecutor.cs:134` (MED)
`packet.Lease.Frame` triggers `Materialize()` (`PacketRuntime.cs:74-83`): ArrayPool.Rent + full-frame memcpy (~1.5KB), because the downstream API takes `ReadOnlyMemory` (setup-queue copy and async fallback need ownership). The warm path (synchronous SendTo) could use `GetFrameSpan()` zero-copy straight into the encoder.
**Fix**: attempt a span fast path for synchronous encode+send before the first suspension point. **Impact: MED** (CPU/cache at high pps).

## C) Stability Findings

### S1. Setup-queue aggregate memory has no global bound — `UdpProxyCoordinator.cs:13-15,129` (MED-HIGH)
Slots with 32KB queues are created per-flow on first datagram, but setup runs only 8-way concurrent (D6 patient queuing): lightning flash crowd of N ≤ 16384 flows → worst case **N × 32KB = 512MB** (DNS-sized ≈ 52MB) held for the entire serialized setup period. If the SOCKS5 server is dead (`ConnectAttemptTimeout=30s`, `Socks5ControlConnection.cs:15`), draining takes hours. Additional hazard: buffered datagrams carry no TTL, so flush delivers long-expired packets.
**Fix**: global Interlocked byte budget (TryEnqueue checks it) / bound concurrent flows pending setup / drop over-aged datagrams at flush. **Severity: MED-HIGH**.

### S2. Setup-tombstone dictionary unbounded between sweeps — `UdpProxyCoordinator.cs:23,433` (MED)
`_setupTombstones[flow]=...` has no capacity check; trimmed only by the 60s sweep (entries themselves expire after 1s). Port-scan + failing-server scenario: one entry per distinct FlowKey inside a 60s window → dictionary grows unbounded then clears, cycling.
**Fix**: refuse tombstone writes past N entries (backpressure via the setup limiter) or opportunistically trim inside `TrySendAsync`. **Severity: MED**.

### S3. Jumbo datagrams → session-teardown storm — `Socks5UdpTransport.cs:267` + `UdpProxyCoordinator.cs:166` (MED)
Consequence of P3: `SendOnReadySessionAsync` catch-all → `RemoveSlotAsync` (no tombstone) → every oversized datagram triggers a full TCP connect + UDP ASSOCIATE + bind cycle. **Fix**: unify buffer sizing. **Severity: MED**.

### S4. Oversized responses silently dropped — `Socks5UdpTransport.cs:284,301` (MED-LOW)
Response payload > cap−42 (default 1472) truncates → Oversized skip, visible only via the 5s-throttled summary counter (`UdpProxySession.cs:296`). QUIC/WireGuard server packets near 1500B get blackholed.
**Fix**: raise the cap together with S3, or document explicitly. **Severity: MED-LOW**.

### S5. Reverse index and RemoveExpired are production dead code — `UdpAssociations.cs:101,141` (LOW)
`TryFindRelay`/`TryFindOriginal`/`RemoveExpired` are test-only (grep-verified); reverse routing actually goes through `FlowDispatcher.IsReverseOf`. `_byRelay` (`:92`, one dictionary slot per association) is write-only; the `RemoveExpired` safety net is never invoked by the sweeper (`IdleExpirySweeper.cs:73-76` calls the coordinator only).
**Fix**: wire it up or delete it — a dead safety net gives false assurance. **Severity: LOW**.

### S6. Teardown fire-and-forget exceptions unobservable — `UdpProxySession.cs:224` (LOW)
`_ = _receiveFailureHandler!(this)`: `RemoveReceiveFailedSessionAsync` → `DisposeAsync` → `control.DisposeAsync()` (network I/O) throwing becomes an unobserved task exception. The slot is already removed under the gate (no leak), but the failure disappears.
**Fix**: try/catch + rate-limited log inside the handler. **Severity: LOW**.

### S7. Kernel memory theoretical ceiling — `Socks5UdpTransport.cs:89,149` (LOW)
512KB SO_RCVBUF × 16384-session capacity = multi-GB kernel buffer ceiling (committed on demand). Realistic session counts (<1K) are fine; saturation is the risk.
**Fix**: lower it or lower the UDP session capacity. **Severity: LOW**.

### S8. Race audit (verified sound, no action)
Ready flip and queue-emptiness check share a critical section (`:335-354`); `TryBeginExpiry` requires `_activeSends==0` (`UdpProxySession.cs:137`) countering send increments (`:110`); `DisposeCoreAsync` awaits `slot.Completion` before reading `slot.Session` (`:248-263`) so late-registered sessions are still disposed; `RemoveSlotAsync` ReferenceEquals ownership prevents double teardown; cross-family (IPv4↔IPv6) translates through the SOCKS5 header ATYP with scope propagation (M2).

## D) Top 5 Improvement Ranking

| # | Item | Content | Effort |
|---|------|---------|--------|
| 1 | P1+P2+P6 | Zero out bidirectional endpoint allocations: `SendAsync(Endpoint)`, `IPAddressValue` decode, cached sender template (3–5 allocations saved per datagram) | S (~0.5 d) |
| 2 | S1 | Setup-queue global byte budget + buffered-datagram TTL | S-M (~1 d) |
| 3 | S3/S4/P3 | Buffer sizing consistency (send buffer tracks frame cap / 65535), clarify oversized-response policy | S (jumbo end-to-end: M) |
| 4 | S2 | Setup-tombstone capacity cap + opportunistic trim | S (hours) |
| 5 | P5(+P4) | De-LINQ the sweep under lock, reusable containers; (optional) finer lock granularity | S |

**Overall**: the UDP warm path is highly engineered (sync send, pooling, throttling all landed); what remains is **endpoint-allocation residue** and **aggregate memory bounds** — the former is a pure-win quick fix, the latter needs a real design decision.
