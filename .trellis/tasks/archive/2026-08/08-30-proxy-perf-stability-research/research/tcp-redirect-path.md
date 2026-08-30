# Research: TCP Redirect/Relay Path Review (2026-08-30)

- **Query**: Read-only performance/stability review of the TCP proxy/redirect path.
- **Scope**: full read of `src/WinForward.Runtime/TcpRedirect/*` (16 files) + `src/WinForward.Protocols/TcpResetBuilder.cs` (~2500 lines total).
- **Date**: 2026-08-30, baseline commit `e5667af`.
- **Excluded (already landed)**: batched reads (32/batch), ArrayPool frames, NdisPacketBufferPool, stall-window CTS reuse, incremental/vectorized checksums, capacity RST + cooldown, fragment consume+RST, tombstone grace, RFC793 wrap-aware RST seq tracking.

## A) Architecture Summary

```
Client SYN (captured ON_SEND via NDIS batch read)
  └─ FlowDispatcher → TcpProxyCoordinator.HandlePacketAsync (TcpProxyCoordinator.cs:289)
       ├─ reverse-candidate? → HandleReverseAsync (rewrite back to original tuple, re-inject)
       ├─ SYN empty → HandleSynAsync (95)
       │    ├─ existing association? → ReinjectExistingFlowDataAsync (150)
       │    ├─ capacity gate (118) → ClientResetInjector.InjectCapacityRejectedResetAsync (S4 fast-fail RST + 1s cooldown)
       │    └─ TcpRedirectSetup.SetupNewRedirectAsync: alloc per-flow listener (ephemeral port, 0.0.0.0)
       │         ├─ TcpRedirectTable.TryClaim (4 dictionaries, exactly-once, single gate lock)
       │         ├─ TcpSequenceObservation.RecordClientSyn (ISN + 128B SYN template copy)
       │         ├─ TcpFrameRewriter.TryRewriteForwardLeg (DNAT / WinpkFilter IP-swap shape, incremental checksum)
       │         └─ TcpRedirectInjector.InjectAsync → SendToMstcp (pool-rented NdisPacketBuffer)
       └─ mid-flow data → resolve original → track seq → rewrite → re-inject toward listener

Listener side (per flow): TcpRedirectAcceptor.RunAcceptLoopAsync
  ├─ AcceptAsync → TcpProxyRelayFactory.EstablishAsync
  │    └─ Socks5ControlConnection.ConnectAsync (DNS + TCP + auth, 2×10s attempts) → CONNECT → upstream Stream
  ├─ Store.TryAttachRelay (Phase: Redirecting → Relaying)
  └─ ObserveRelayCompletionAsync → TearDownSessionAsync on relay end
       TcpProxyRelay: 2 pump tasks (8 KiB buffers, 30-min stall window, half-close propagation)

Teardown: SessionStore.TearDownSessionAsync → RetireSessionUnderGate (store gate)
  → ReleaseRetiredAsync → RemoveAssociationFromTable (table gate + 60s tombstone)
  → listener/relay dispose → IdleExpirySweeper reclaims tombstones + half-open redirects
```

## B) Performance Findings

### P1. No `NoDelay` on either relay leg — HIGH (latency)
- **Where**: `TcpRedirectListener.cs:42-48` (accepted socket, zero options set); `Socks5ControlConnection.cs:110-127` (upstream socket, zero options). Verified: no `NoDelay` anywhere in `src/`.
- **Cost**: Nagle is ON by default on both legs of a transparent proxy. Small-write interactive flows (SSH, RDP, game traffic) hit classic Nagle × delayed-ACK interaction — 40–200 ms stalls whenever a write is smaller than the previous unacked segment.
- **Fix**: `accepted.NoDelay = true` in the listener accept; `socket.NoDelay = true` after `ConnectAsync` succeeds in `ConnectOnceAsync`.
- **Impact**: HIGH for interactive traffic; zero risk. Two lines.

### P2. 8 KiB pump buffers — MED-HIGH (per-flow throughput ceiling)
- **Where**: `TcpProxyRelay.cs:62` (`BufferSize = 8192`), `:167` (`new byte[BufferSize]` per direction, held per relay).
- **Cost**: One `ReadAsync` + one `WriteAsync` syscall pair (plus 2 memcpys) per 8 KiB per direction. At 1 Gbps ≈ 15 K syscall-pairs/s per flow; in-flight depth is one user buffer (kernel socket buffers do the real windowing), so the ceiling is syscall/CPU-bound.
- **Fix**: Rent 32–64 KiB from `ArrayPool<byte>.Shared` per pump direction, return in `finally`. Pool reuse keeps steady-state memory bounded.
- **Impact**: MED-HIGH for bulk flows; LOW effort.

### P3. `StallWindow.Arm()` runs twice per 8 KiB chunk — MED-LOW
- **Where**: `TcpProxyRelay.cs:174,191` — each `Arm()` = `TryReset` + `CancelAfter` (TimerQueue update).
- **Cost**: ~100–200 ns per timer op; at 150 K chunks/s (10 Gbps single flow) ≈ 600 K timer ops/s ≈ 5–10% of one core.
- **Fix**: Rate-limit re-arms with a `Stopwatch` — only `Arm()` if >1 s elapsed. Drift irrelevant vs 30-min window; lifetime-token link semantics unchanged (never disarmed between ops).
- **Impact**: MED-LOW; ~10 lines.

### P4. Double reverse lookup per packet — MED (lock traffic)
- **Where**: `TcpProxyCoordinator.cs:299` (`IsReverseCandidate`) → `HandleReverseAsync:201` (`TryResolveByReverse`); same pattern on `HandleReverseIfApplicableAsync:270` → `:201`. Each is a separate `_gate` acquisition + `ReverseRedirectTuple` hash + dictionary probe on the hottest per-packet path.
- **Fix**: Drop the `IsReverseCandidate` pre-check; call `TryResolveByReverse` once (a miss means not-a-candidate → fall through to tombstone check as `:275` already does).
- **Impact**: MED at high aggregate pps; halves table-lock acquisitions for reverse traffic.

### P5. Frame parsed 2–3× per packet — LOW-MED
- **Where**: Reverse data packet parses the same frame in `TcpFrameRewriter.ClassifyTcpSyn` (coordinator `:297`), `TcpSequenceObservation.RecordServerSynAck` (`TcpSequenceObservation.cs:32`), and `TryReadTcpSequenceAdvance` (`:50`). Forward data: 2 parses.
- **Fix**: Parse once into the `IPTcpUdpPacket` view and thread it through (`RecordServerSynAck` + `TrackServerSequence` share one parse; `ClassifyTcpSyn` accepts a pre-parsed view).
- **Impact**: LOW-MED; touches signatures (effort M).

### P6. Single global table gate serializes all flows — MED at scale
- **Where**: `TcpRedirectTable.cs:134` — every packet of every proxied flow (reverse resolve, forward resolve, `Touch`) takes the one `_gate`. Sections ~100 ns but writes (`Touch` on every resolve) prevent read-lock conversion; convoys above ~100–200 Kpps multicore.
- **Fix**: Stage 1 = P4. Stage 2 = `ReaderWriterLockSlim` (resolves = read; `TryClaim`/`TryRemove` = write) or striped dictionaries. Do not stripe before measuring — the exactly-once invariant across 4 dictionaries makes striping error-prone.
- **Impact**: MED at high load; effort S (P4) / M (RWLS).

### P7. LINQ + allocation inside locks on sweep paths — LOW-MED
- **Where**: `TcpRedirectSessionStore.cs:119-122` — `Where(...).Select(...).ToArray()` under the store gate (live: `IdleExpirySweeper.cs:73`); holds the gate for an O(n) allocating scan (ms-scale at 16 K sessions), stalling SYN setup/teardown. `TcpRedirectTable.cs:277` — `Where(...).Distinct().ToArray()` under the table gate **with zero callers (dead code; the sweeper never invokes it)**; `Distinct()` redundant anyway (dictionary values unique).
- **Fix**: Store: copy references out under the gate, filter outside. Table: delete `RemoveExpired` or fix the same way.
- **Impact**: LOW-MED (periodic hiccup); effort S.

### P8. Per-flow kernel memory ≈ 300 KiB → ~4.5 GiB at capacity — MED
- **Where**: No TCP socket buffer tuning anywhere (UDP leg sets 512 KiB at `Socks5UdpTransport.cs:149`; TCP legs use Windows defaults: 64 KiB × rcv+snd × 2 sockets) + 16 KiB user pump buffers + per-flow listener socket (`TcpRedirectListener.cs:19-33`) + ~4 tasks/flow.
- **Cost**: 16,384 relaying flows ≈ 48 K sockets and ~4.5 GiB buffers/non-paged pool before the capacity gate blinks. The capacity gate bounds flows, not bytes.
- **Fix**: Set `ReceiveBufferSize`/`SendBufferSize` (16–32 KiB) on accepted + upstream sockets in many-flow regimes, and/or document a lower default capacity. Effort S, config-sensitive.

### P9. Per-relay DNS resolution — LOW
- **Where**: `Socks5ControlConnection.cs:56` — `Dns.GetHostAddressesAsync(server.Host)` on every relay establishment; no app-level cache in repo.
- **Cost**: Mitigated by the Windows DNS Client cache (system resolver, TTL-honored); residual is interop cost + first-miss latency per TTL expiry. Matters only at very high setup rates or cache-disabled hosts.
- **Fix**: Optional tiny TTL cache keyed by host. LOW priority.

### P10. Per-packet synchronous native injection — MED at high pps, effort M-H
- **Where**: `TcpRedirectInjector.cs:21-22` — one `SendToMstcp`/`SendToAdapter` P/Invoke + full frame copy per packet, executed on the capture/dispatch thread.
- **Cost**: The capture path already reads 32-packet batches; reverse/forward rewrites within one batch could be collected and flushed with a batched native send (`SendPacketsToAdapter/Mstcp` already imported in the ABI). Requires a batch-aware `InjectAsync` seam restructure.
- **Impact**: MED; effort M-H. Flip side: the fully-async relay means slow *upstreams* never block the pump — only these bounded injection syscalls do (by design).

### Positive notes (no action needed)
- `PumpAsync` is allocation-free per chunk (reused buffer, no closures/LINQ/interpolation).
- `StallWindow` reuse already eliminated the 160 B/chunk.
- Logging fully `IsEnabled`-gated (`TcpRedirectLogging.cs:15,26`); `ConfigureAwait(false)` consistent; fail-closed `NdisPacketBufferPool` rent is struct-free.

## C) Stability Findings

### S1. Mid-flow relay fault/stall leaves the client connection blackholed — **HIGH**
- **Where**: `TcpRedirectAcceptor.cs:152-168` (`ObserveRelayCompletionAsync`), interacting with `TcpRedirectSessionStore.cs:227-245` and `TcpProxyCoordinator.cs:270-278/319-324`. **Spot-checked 2026-08-30: confirmed — the catch swallows, teardown follows, no client reset on this path.**
- **Scenario** (upstream RSTs mid-flow):
  1. Upstream socket reset → `upstreamToLocal` pump faults → `RunPumpAsync` rethrows (`TcpProxyRelay.cs:117-122`) → `Completion` faults.
  2. `ObserveRelayCompletionAsync` catch (162-165) swallows; calls `_tearDownSession` (167) — **no client reset**.
  3. `ReleaseRetiredAsync` removes the table alias and arms the 60 s tombstone (`store:233` → `:293-296`) **before** `relay.DisposeAsync()` (`:241`).
  4. Disposing the accepted socket makes the kernel emit RST (listener tuple → client tuple); captured → `IsReverseCandidate` now false → tombstone reverse hit → `Dropped` (`coordinator:270-278`).
  5. The client's socket (to the *original server* tuple) never sees anything. Its next send retransmits into `TryResolveByOriginal`-miss → tombstone → `Dropped` (`coordinator:324`) — ETIMEDOUT after minutes.
  - The **stall** path is identical but worse: `RunPumpAsync` returns *normally* on `PumpResult.Stalled` (`TcpProxyRelay.cs:100-106`), so even the swallowing catch is skipped.
- **Why infrastructure exists but is unused**: `ClientResetInjector.TryInjectClientResetAsync` (`ClientResetInjector.cs:50-78`) builds exactly the needed in-window RST|ACK from the recorded SYN template + `ClientNextSeq`/`ServerNextSeq` trackers — but only the relay-*setup* failure path calls it (`HandleRelaySetupFailureAsync:135-141`). Mid-flow faults and stalls never do. The graceful path is fine (FINs propagate via `ShutdownSend` while the association is alive, `TcpProxyRelay.cs:108-111`).
- **Fix**: In `ObserveRelayCompletionAsync`, before `_tearDownSession`: on faulted completion call `_clientReset.TryInjectClientResetAsync(session.Association, CancellationToken.None)`; for stalls, expose the relay end kind (internal `RelayEndKind { CleanEnded, Stalled, Faulted }` on `TcpProxyRelay`) and reset on `Stalled`/`Faulted` only (clean end already propagated FINs). ~20 lines.
- **Severity**: HIGH — every upstream fault/stall currently degrades to a minutes-long client hang instead of an instant abort.

### S2. The two-lock retire/removal window (`SessionStore.cs:293-294`, flagged residual) — MED
- **Where**: `TearDownSessionAsync` (`:201-209`) retires under the store gate, then `ReleaseRetiredAsync` → `RemoveAssociationFromTable` (`:293-296`) takes the table gate (+ tombstone gate) *afterwards*. Neither `HandleSynAsync`'s reuse check (`coordinator:112`) nor `TryClaim` (`TcpRedirectTable.cs:164-169`) inspects `Phase == Closing`, so both honor a stale association.
- **Scenario**:
  1. Relay ends → store gate: session removed, `Phase = Closing`, token cancelled → gate released.
  2. µs–ms later (before `_table.TryRemove` commits), a new SYN for the same tuple arrives (client fast reconnect reusing the source port, or SYN retransmit racing teardown).
  3. `TryResolveByOriginal` still hits the stale association → `ReinjectExistingFlowDataAsync` rewrites the SYN toward the **old, about-to-be-disposed listener** and injects it.
  4. Teardown finishes: listener disposed, tombstone armed. The redirected handshake's SYN-ACK/RST now hit the tombstone → `Dropped` → client hangs to RTO (or briefly establishes against a dead listener and dies on first data — also tombstone-eaten).
- **Concrete fix**: Move the `_table.TryRemove(association, removed => _tombstones.TryAdd(...))` call *into* `RetireSessionUnderGate`/`TryRetireSessionUnderGate` so alias removal + tombstone publication are atomic with session removal. Lock order `store → table → tombstone` is safe — verified no path acquires them in reverse (`EnterSetup`/`ExitSetup` take the store gate without holding others; `FailAssociationAsync:258-264` takes them sequentially, never nested; table/tombstone never call upward). `TryRemove` is idempotent (`TcpRedirectTable.cs:263` ReferenceEquals guard), so the later call in `ReleaseAssociationAsync` returns false. Keep listener/relay/token disposal outside the gate as today. Alternative (weaker): gate reuse and `TryClaim` on `Phase != Closing` — but the window then moves to `TryClaim`; the nested removal is the complete fix.
- **Severity**: MED (narrow race, churn-dependent, consequence = client RTO + teardown churn).

### S3. Tombstone insertion-order queue can grow without bound — MED (slow leak)
- **Where**: `TcpRedirectTombstoneTable.cs:48-62`. Every `TryAdd` enqueues (`:60`); a refresh of an existing key removes the dictionary entry (`:53-54`) but never dequeues the old queue record; `RemoveExpired` (`:78-86`) never drains the queue. The only dequeue is `EvictOldestUnderGate` (`:88-98`), which runs **only when the dictionary is at capacity** (`:55`).
- **Scenario**: Long-running proxy, teardown churn below capacity (or repeated teardown/re-claim of the same tuples): `_insertionOrder` accumulates one heap `TombstoneEntry` (holding `FlowKey` + endpoints) per `TryAdd`, forever — the bounded dictionaries hide the growth. Days of uptime → hundreds of MB.
- **Fix** (either or both): (a) mirror `TcpResetCooldownTable.TryClaim`'s documented approach (`TcpResetCooldownTable.cs:44-47`) — don't re-enqueue on refresh, accept the conservative eviction direction; (b) drain stale heads inside `RemoveExpired`: `while (Count > 0 && !ReferenceEquals(_byForward.GetValueOrDefault(Peek().Forward), Peek())) Dequeue();`.
- **Severity**: MED (uptime × churn dependent).

### S4. No TCP keepalive + 30-min stall kill + slot squatting — MED
- **Where**: `TcpProxyRelay.cs:69` (`StallTimeout = 30 min`); no `SetSocketOption(KeepAlive)` anywhere (verified).
- **Scenario**: (a) A healthy but idle connection (SSH without keepalives) is silently killed at 30 min — via the S1 blackhole path (stall returns normally, no reset). (b) A hostile client dribbling 1 byte per 29 min keeps its session slot forever; 16 K such flows exhaust capacity and fail-fast-reject legitimate traffic.
- **Fix**: Enable TCP keepalive on the accepted socket (e.g. 2 min idle / 1 min interval / 5 probes); make `StallTimeout` configurable with a lower default (e.g. 5 min); combined with S1's stall-reset the teardown becomes client-visible.
- **Severity**: MED.

### S5. Fire-and-forget tasks with throw-capable tails — LOW (defensive)
- **Where**: `TcpRedirectAcceptor.cs:90` `_ = ObserveRelayCompletionAsync(...)` with `_tearDownSession` **outside** any try (`:167`) — any throw faults an unobserved task (default policy: process crash). Similarly the accept loop's catch-path calls (`:96`, `:101` → `accepted.DisposeAsync()`, `HandleRelaySetupFailureAsync`) can escape `RunAcceptLoopAsync`, which nobody awaits after teardown (`TearDownSessionAsync` never awaits `AcceptLoop`; only `DisposeCoreAsync:192` does).
- **Fix**: Wrap `ObserveRelayCompletionAsync`'s teardown tail and the accept loop body in try/catch-warn. Hardening only.
- **Severity**: LOW.

### S6. `HandleSynAsync` capacity check race — negligible
`coordinator.cs:118` reads `SessionCount` before setup completes; concurrent SYN setups can overshoot `_capacity` by the number of in-flight callers. Bounded, benign (documented behavior would suffice).

### S7. Cooldown claim consumed before reset build — negligible
`ClientResetInjector.cs:92-94`: `TryClaim` wins, `BuildResetFromSyn` returns null (unparseable frame) → the 1 s guard is spent with no reset sent. Only affects already-failing degenerate frames.

### Verified non-issues
- Self-traffic wildcard registrations are owned and disposed with the control connection (`Socks5ControlConnection.cs:123,191` + `DisposeFailedAttemptAsync:337-348`) — no registry leak on failed or successful relays.
- Dispose-vs-relay races: `TcpProxyRelay.DisposeAsync` hooks the fault observer *before* socket disposal (`TcpProxyRelay.cs:239-245`); `TryRetireSessionUnderGate`'s ReferenceEquals guard makes double-teardown a no-op; relaying sessions are exempt from the idle sweeper so single-writer teardown holds.
- OCE discipline: every `OperationCanceledException` filter checks the correct token with sensible generic-OCE fallbacks; the SOCKS5 attempt-timeout OCE correctly lands in the acceptor's generic catch → client reset + teardown.
- In-place frame mutation follows the documented read-then-write invariant (`TrackClientSequence` before rewrite, `coordinator:158-163`); `MemoryMarshal.TryGetArray` failure fails closed.
- `LastActivityUtc` cross-lock reads (written under table gate, read under store gate) are single-field atomic in practice — worst case one-sweep-late expiry.

## D) Top 5 Improvement Ranking

| # | Change | Files | Effort |
|---|--------|-------|--------|
| 1 | Client-visible RST on relay fault/stall end (S1) + relay end-kind surface | `TcpRedirectAcceptor.cs`, `TcpProxyRelay.cs` | S (~0.5 d) |
| 2 | Atomic retire+remove+tombstone (S2) | `TcpRedirectSessionStore.cs` | S-M (~0.5–1 d) |
| 3 | `NoDelay = true` on both legs (P1) | `TcpRedirectListener.cs`, `Socks5ControlConnection.cs` | S (<0.5 d) |
| 4 | 64 KiB pooled pump buffers + throttled stall re-arm (P2+P3) | `TcpProxyRelay.cs` | S (~0.5 d) |
| 5 | Tombstone queue bounded (S3) | `TcpRedirectTombstoneTable.cs` | S (<0.5 d) |

Runners-up: merged reverse lookup (P4, S), sweep LINQ outside locks + delete dead `TcpRedirectTable.RemoveExpired` (P7, S), TCP socket buffer sizing / capacity doc (P8, S), keepalive on accepted socket (S4, pairs with #1).
