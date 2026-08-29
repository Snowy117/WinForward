# Design — UDP loss design flaws fix (R1–R6)

Scope: R1–R6 approved by user (2026-08-28). All file anchors refer to current master.

## D1. Non-blocking UDP session setup (R1)

### Problem
`UdpProxyCoordinator.TrySendAsync` awaits the session setup task inline
(`src/WinForward.Runtime/UdpProxyCoordinator.cs:90`), so the first datagram of a flow
drags the whole capture pump through DNS + 4×30s connect attempts + auth + UDP ASSOCIATE
(`src/WinForward.Runtime/Socks5ControlConnection.cs:14-15`), stalling
`TryReadPackets` and overflowing the ndisrd adapter queue.

### Design
Introduce an explicit session state machine in `UdpProxyCoordinator` and make
`TrySendAsync` **never await network I/O**:

```
TrySendAsync(flow, payload):
  under _gate:
    if tombstone(flow) active and now < retryAt  -> return false (dropped, trace log)
    entry = _sessions.GetOrAdd(flow)
    if entry.State == SettingUp or Flushing     -> enqueue into entry.SetupQueue (bounded, drop-oldest); return true
    if entry.State == Ready                      -> mark reference; send inline (see below)
    if new entry                                  -> create marker with State=SettingUp + empty queue,
                                                    kick off background CreateSessionAsync (fire-and-task,
                                                    observed by TrackFailedSetupAsync), enqueue datagram; return true
```

- **Setup queue**: reuse the existing dead-code `BoundedSetupQueue`
  (`src/WinForward.Core/PacketRuntime.cs:147`) per session. Bounds: 32 packets / 32768
  bytes. Overflow policy: **drop-oldest** (freshest state wins for UDP; FIFO delivery
  order of retained datagrams is preserved). Dropped-count surfaced via rate-limited
  debug log + trace event `udp.setupqueue.dropped`.
- **State machine per session**: `SettingUp → Flushing → Ready`; failure at any point →
  teardown (drop queued datagrams fail-closed, remove entry, write tombstone).
  - `CreateSessionAsync` success → state `Flushing` → drain setup queue in FIFO via the
    real transport under the session send gate → `Ready`.
  - Inline sends check state under the session gate: `Ready` → direct
    `_transport.SendAsync` (as today); `SettingUp`/`Flushing` → enqueue (guarantees
    per-flow FIFO, no bypass reordering around the flush).
- **Failure cooldown (tombstones)**: on setup failure, keep a lightweight
  `{flow → retryAtUtc}` marker in the coordinator (regular cleanup during the idle
  sweep). Cooldown 1s. A new datagram during cooldown is dropped fail-closed with a trace
  event. This prevents datagram-rate handshake hammering against a dead SOCKS5 server.
- **Global concurrent setup cap**: `SemaphoreSlim(8)` around actual transport creation;
  waiters beyond the cap fail fast into the cooldown path (bounded resource use when
  thousands of new flows appear at once).
- **Payload lifetime**: the captured frame lives in the pump's native batch slot;
  `TrySendAsync` must copy before returning during SettingUp/Flushing
  (`BoundedSetupQueue.TryEnqueue` already copies). The Ready inline path stays inside
  the awaited dispatch, as today (pump owns the slot until the handler returns).

### Contract changes
- `IUdpProxyTransportFactory`/`IUdpProxyTransport`: unchanged.
- `UdpProxyCoordinator.TrySendAsync` returns `true` when the datagram is accepted
  (queued or sent); `false` only for capacity/cooldown/reject reasons.
- `NdisPacketActionExecutor.HandleUdpProxyAsync` becomes fully fast: no awaited network
  I/O on the Ready path beyond the local UDP `SendToAsync` (unchanged from today), and
  zero network I/O during setup.

## D2. Robust receive loop (R2)

### Problem
`Socks5UdpTransport.ReceiveAsync` throws on unexpected source, oversize sentinel
(`src/WinForward.Runtime/Socks5UdpTransport.cs:132-137,150`), and decode failure;
`UdpProxySession.ReceiveLoopAsync` treats every non-cancellation exception as fatal
(`src/WinForward.Runtime/UdpProxySession.cs:171-174`); response injection exceptions
(`UdpResponseReinjector` → `Win32Exception`) are equally fatal.

### Design
- **Per-datagram anomalies become skips, not throws.** `IUdpProxyTransport.ReceiveAsync`
  returns a discriminated result (add `Socks5UdpReceiveResult` enum or a nullable result
  with `SkipReason`): `UnexpectedSource`, `Oversized`, `Malformed`. The session receive
  loop logs rate-limited (per-session counter, 1 summary/5s) and **continues**.
- **Fatal remains fatal**: only exceptions from the socket itself
  (`SocketException`, `ObjectDisposedException`, OCE on shutdown) tear down the session
  (existing path via `_receiveFailure` → `RemoveReceiveFailedSessionAsync`).
- **Injection isolation**: wrap `_sink.InjectAsync` in try/catch inside the receive
  loop; non-cancellation exceptions log rate-limited and continue (a failed injection of
  one response must not kill the flow). Cancellation still propagates.
- **Uniform oversize behavior**: an oversized datagram (≥ buffer capacity) and an
  over-frame-cap payload (frame build failure in the reinjector) are both skip+log —
  removing the current 1515..1526B "drop with log" vs ≥1527B "kill session" split
  (PRD defect 8 folded here).

## D3. Per-adapter native call gates (R3)

### Problem
One process-wide `Monitor` (`src/WinForward.NdisApi/NdisNativeCallGate.cs:14-24`)
serializes batch reads, per-packet pass reinjection, and UDP response injection across
all adapters.

### Design
- `NdisApiDriver` keeps a **control gate** (open/close/enum/mode snapshot+set/restore —
  cold path) and a **per-adapter-handle gate map**
  (`Dictionary<nint, NdisNativeCallGate>` + creation lock) used by `TryReadPackets`,
  `SendPacketToMstcp`, `SendPacketToAdapter`.
- Within one adapter, native calls stay serialized (preserves existing per-adapter
  ordering and conservative driver safety); **across adapters they proceed in
  parallel**. Reads on adapter A can no longer be blocked by injections toward adapter B.
- The queue-query + batch-read pair keeps sharing one lease on that adapter's gate
  (unchanged semantics from `.trellis/spec/backend/windows-ndisapi.md`).
- `NdisNativeCallGate` itself is unchanged (still a Monitor); only its instantiation
  topology changes. Existing contention telemetry (`MaxConcurrentCalls`) moves to the
  control gate; per-adapter gates expose their own counters for diagnostics.
- Risk note: this assumes ndisrd handles concurrent IOCTLs on distinct adapter queues
  (already implied by multi-process clients of the same driver). Within-adapter
  serialization keeps us conservative. If validation shows driver-level coupling, the
  fallback is a single send-gate + per-adapter read-gates (documented rollback point in
  implement.md).

## D4. Response-path buffers and allocation (R4)

- **Relay socket receive buffer**: set `ReceiveBufferSize` explicitly in
  `Socks5UdpTransport.CreateAsync` (const, 512KB; comment documents why: burst drain
  headroom while the single receive loop reinjects). Not a config knob (schema frozen).
- **Pooled native buffers**: `UdpResponseReinjector.InjectAsync` rents from
  `NdisPacketBufferPool.Shared` (`using`-style return) instead of
  `new NdisPacketBuffer()` per response (`src/WinForward.Runtime/UdpResponseReinjector.cs:104`).
- **In-place frame build**: add span-writing overload
  `UdpFrameBuilder.TryBuildInto(Span<byte> destination, ..., out int length)`; the
  reinjector writes Ethernet+IP+UDP directly into the pooled native buffer's frame span
  and calls a new `NdisPacketBuffer.SetFrameLength/flags` path, eliminating the
  per-response `byte[]` allocation (`src/WinForward.Protocols/UdpFrameBuilder.cs:62`).
  Existing allocating `TryBuild` stays for tests/compat.
- Batched send (`SendPacketsToMstcp`/`SendPacketsToAdapter` multi-request — already
  declared in `NdisApiAbi.cs:217-227`, unused) is **deferred**: per-session responses
  arrive one-at-a-time; cross-session batching adds coordination for little win after
  D3. Recorded as a follow-up optimization note.

## D5. Transport send serialization (R5)

`Socks5UdpTransport` gains a `SemaphoreSlim(1,1)`; `SendAsync` awaits it around the
encode+`SendToAsync` pair, protecting `_sendBuffer`
(`src/WinForward.Runtime/Socks5UdpTransport.cs:118-123`). `WaitAsync` completes
synchronously when uncontended, so the steady single-pump path pays no allocation.
This makes the implicit "one flow ↔ one pump" invariant harmless if it is ever violated
(multi-adapter scope, topology changes).

## D6. Pump wait granularity (R6)

`NdisCapturePump`'s empty-queue `Task.Delay(1ms)` actually waits ~15.6ms on default
Windows timer resolution. Add a scoped `timeBeginPeriod(1)`/`timeEndPeriod(1)` (winmm
P/Invoke) around the capture run — new small `HighResolutionTimerScope` in
`WinForward.Windows` (created by the run command next to `NdisAdapterModeController`
usage; restored in finally). This makes the 1ms poll delay real (~1–2ms) without
touching the pump loop. Event-driven pumping via `SetPacketEvent` (not currently
wrapped in `NdisApiAbi`) is the bigger architectural alternative — deferred with a note.

## Compatibility & rollout

- No config schema changes; no CLI changes; no TCP path behavior changes.
- Public surface additions: `Socks5UdpReceiveResult` (or similar) on the transport
  seam; `UdpFrameBuilder.TryBuildInto`; `HighResolutionTimerScope`. Internal changes:
  coordinator/session state machine, driver gate topology.
- Ordering guarantees: per-flow FIFO preserved (D1 enqueue semantics); per-adapter
  reinjection order preserved (D3 keeps within-adapter serialization).
- Rollback: each D-block is independently revertible; D3 has an explicit fallback
  (single send-gate + per-adapter read-gates).

## Key trade-offs

- Drop-oldest setup queue: freshest datagrams survive a slow setup; loses strict
  "everything sent is relayed" — bounded, logged, and strictly better than the current
  whole-adapter queue overflow.
- Ready-path sends stay inline on the pump: keeps the zero-allocation steady path
  (hot-path spec #3/#7); residual coupling is one local UDP `SendToAsync` that the OS
  buffers — full decoupling would need per-session channels (rejected: per-datagram
  allocations).
- 1s failure cooldown: a flow whose server is down loses datagrams for 1s windows
  instead of stalling the adapter for 30s+ — fail-closed, visible in traces.
