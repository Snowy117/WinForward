# TCP/UDP data-path structural performance research

> Recorded 2026-09-29. Reviewer: full read of the capture pump, dispatcher, core tables,
> TCP redirect, UDP proxy, SOCKS5 transport, NDISAPI driver layers (see the file/line evidence
> inline). Scope: performance and resource *structure* — lock architecture, I/O shape, sweep
> algorithms, key data structures. Per-packet managed allocation is deliberately out of scope:
> it is already contract-locked at zero by `hot-path.md` and its allocation gates.

## 0. What is already optimal (do not re-tread)

The micro-optimization layer is mature and measured; none of the findings below ask to revisit it:

- Zero-allocation warm dispatch (`FlowDispatcher.DispatchAsync` non-async warm entry, X1 listener-port
  prefilter), span-based parse/classify on the pump-owned native frame, lazy lease materialization.
- RFC 1624 incremental checksums for TCP endpoint rewrites, with a full-recompute property-test oracle.
- Batched capture reads (one kernel round trip per 32-packet batch) and batched **pass** reinjection
  through per-(adapter, direction) lanes flushed once per pump iteration.
- Span-consuming UDP send (`Socks5UdpTransport.SendSpanAsync`): reusable encode buffer, cached
  `SocketAddress`, non-blocking socket with synchronous `SendTo`, overlapped fallback.
- Native buffer pooling with in-band rental state; relay pump windows rented once per direction;
  stall-window CTS reuse with re-arm throttling.

The structural findings live one level up: how many serialized locks and kernel crossings a packet
pays *beyond* its zero-alloc budget, and how background bookkeeping scales with table size.

Cost figures below are order-of-magnitude estimates to prioritize work, not measurements; every
proposal names the benchmark that must prove it.

---

## F1. TCP redirect: per-packet pool rent + full-frame copy + single-packet IOCTL

**Priority: highest. This is the throughput ceiling of the product's main path.**

### Evidence

- Forward leg (mid-flow data and reused-SYN reinjection):
  `TcpProxyCoordinator.ReinjectExistingFlowDataAsync` — `_framePool.Rent()`, full-frame
  `source.CopyTo(buffer.GetFrameStorage())`, in-place rewrite, `_injector.Inject(...)`, `buffer.Dispose()`
  (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:326-376`).
- Reverse leg: `HandleReverseAsync` — identical rent → copy → rewrite → inject → dispose shape
  (`TcpProxyCoordinator.cs:400-449`).
- The inject itself is a **single-packet** synchronous IOCTL per frame per direction:
  `TcpRedirectInjector.Inject` → `reinjector.SendToMstcp/SendToAdapter` →
  `NdisApiDriver.SendPacketToMstcp/SendPacketToAdapter`
  (`src/WinForward.Runtime/TcpRedirect/TcpRedirectInjector.cs:26-36`,
  `src/WinForward.NdisApi/NdisApiDriver.cs:219-243`).
- Contrast with the pass path, which already solved this exact problem: unmodified frames are
  reinjected **in place in the capture slot** (`PrepareForReinjection`, zero copy) and accumulated
  into per-(adapter, direction) lanes that flush as **one batched IOCTL per pump iteration**
  (`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:71-97,190-209`;
  batched driver sends exist: `NdisApiDriver.SendPacketsToMstcp/SendPacketsToAdapter`).

### Cost model

Per redirected TCP packet, per direction, today: one pool CAS round trip, one up-to-1514 B memcpy,
one rewrite (cheap, incremental), and **one user→kernel IOCTL**. A user→kernel round trip of this
driver class costs single-digit microseconds; at 10 Gbps / MTU 1500 (~800k pps per direction) the
redirect path would need more than one core for IOCTLs alone, before any copy or rewrite work.
The pass path pays one IOCTL per up-to-32 packets — the redirect path pays up to 32× more kernel
crossings plus an extra MTU-sized copy per packet.

### Proposal: in-place rewrite + lane-batched redirect injection

The pump contract already guarantees what this needs: batch slots stay stable for the whole
iteration, the handler is awaited before any slot reuse, and the iteration-end `OnBatchCompleted`
flush runs before the next read (`src/WinForward.NdisApi/NdisCapture.cs:299-309`).

1. Extend the executor's lane table (or a sibling table) with redirect lanes keyed by
   (adapter handle, target direction), reusing `PendingPassLane` mechanics verbatim.
2. Forward leg, directly on the capture slot: `TcpSequenceObservation.TrackClientSequence(frame)` →
   `TcpFrameRewriter.TryRewriteForwardLeg(frame, ...)` → `CompleteFrame(len, flipped direction, handle)`
   → append to lane. Reverse leg symmetrically (MAC swap already operates in place).
3. Iteration-end flush sends redirect lanes with the same batched IOCTLs as pass lanes. Per-connection
   ordering is preserved: one connection's leg always lands in one lane, appended in capture order.

Savings per redirected packet: one pool round trip, one ≤1514 B memcpy, and ~97% of injection IOCTLs.
Added latency: at most one poll cycle (~1 ms idle, ~0 under load since every batch is full) — invisible
to TCP, which already traverses the proxy stack.

### Semantics & risks

- **Failure attribution changes.** Today a failed inject surfaces a client RST via
  `ClientResetInjector.HandleInjectionFailureAsync` with per-packet attribution. Batched flush failures
  are lane-level. Proposal: keep the lane flush fail-closed (count + rethrow, like pass), and on flush
  failure degrade that lane's frames to per-packet single sends so the RST attribution path still fires
  (cold path; allocation is acceptable there).
- Sequence observation must run **before** the frame enters the lane (read-then-write invariant already
  documented in the coordinator); this stays synchronous on the pump thread, no new concurrency.
- The tombstone/cooldown/RST paths (rare) keep immediate single sends — batching is for the data legs.

### Measurement

New benchmark: `TcpRedirectInjectionBenchmarks` (rewrite + inject against a fake reinjector counting
IOCTL calls and bytes; assert IOCTL count ≈ packets/32, 0 B managed). Validate end-to-end with
`TcpThroughputScenario` (socks5 vs bare ratio, anchor ≥70%) and `gc-soak`.

---

## F2. Global lock chain: 3–5 serialized locks + 5–9 dictionary probes per packet

### Evidence (one warm packet on an established flow)

| # | Site | Cost |
|---|------|------|
| 1 | `SelfTrafficRegistry.IsOwned` (`src/WinForward.Runtime/SelfTrafficRegistry.cs:24-33`) — called **before** the flow lookup on the warm entry (`FlowDispatcher.cs:159`) | 1 global lock + up to **4 dictionary probes** (forward/reverse exact + forward/reverse wildcard) |
| 2 | `FlowTable.TryResolve` (`src/WinForward.Core/FlowTable.cs:47-53,138-148`) | 1 global lock + 1–2 probes + `Touch`, which calls `TimeProvider.GetUtcNow()` **on every hit** (`FlowTable.cs:142`) |
| 3–4 | TCP: `TcpRedirectTable.IsReverseCandidate` then `TryResolveByOriginal` (`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:253-256,352-364`) | the same `_gate` entered **twice** per packet |
| 3–5 | UDP: coordinator `_gate` (cooldown probe + session probe, `UdpProxyCoordinator.Send.cs:27-73`) → session `_activityGate` (`UdpProxySession.cs:142-148`) → transport `_sendGate` semaphore (`Socks5UdpTransport.cs:321`) | lock + lock + semaphore per datagram |

An uncontended lock round trip is tens of ns; the fixed tax is ~100–200 ns/packet single-adapter.
The structural problem appears with multiple adapter pump threads: these are single-instance locks, so
every additional pump adds cache-line ping-pong on the same lines — throughput does not scale with
adapters and can regress. There is also one latent hazard: `FlowTable.RemoveExpired` invokes the
`isHeld` predicate **under the flow-table gate**, and that predicate enters the TCP store/tombstone
locks (`FlowTable.cs:90-118`; `TcpProxyCoordinator.HoldsFlow`) — a lock-nesting edge that any future
reverse-order acquisition would turn into a deadlock.

### Proposals (in increasing invasiveness)

1. **Reorder self-traffic off the warm path.** Self-traffic tuples number in the dozens and exist only
   while WinForward's own relay connections live. Run the full `IsOwned` check once at flow-claim time
   and cache "proven not self" in the `FlowState`; the warm entry resolves the flow first and skips
   `IsOwned` entirely on a hit, paying it only on table misses. Registration ordering (register before
   first SYN) is already the established contract, so this is semantics-preserving. Saves 1 lock + 4
   probes per packet for all established flows.
2. **Lock-free read path over immutable decisions.** A `FlowDecision` never changes after claim (key
   invariant). Demote `FlowState.LastActivityUtc` to a `long _lastActivityTicks` written with
   `Volatile.Write` (benign race; sweep precision is seconds). The table can then be two
   `ConcurrentDictionary`s (or a custom open-addressing table) with a fully lock-free resolve;
   only claim/expiry serialize. This also removes the sweep-predicate lock nesting.
3. **Shard with a direction-normalized hash.** 64–256 stripes by hash low bits, stripe-local locks,
   dictionaries and free lists. Reverse packets must land on the same stripe: normalize the endpoint
   pair before hashing (commutative mix, e.g. order-independent combine of local/remote), so forward
   and reverse share a stripe and the reverse alias needs no cross-stripe probe. The same trick folds
   `TcpRedirectTable._byReverse` into the main index and drops one lock entry per TCP packet.
4. **UDP ready-path lock-free.** `_sessions` as a concurrent map, `slot.Ready` volatile, cooldowns in a
   concurrent structure; only admission (new slot insert) serializes. `UdpProxySession._activityGate`'s
   expiring/fault check becomes volatile reads + a single `TryEnter`.
5. **Coarsen the clock.** One timestamp per pump iteration passed down, or bucketed activity time (F3.4);
   removes the per-hit `GetUtcNow()` (~15–25 ns each).

### Measurement

`FlowTableBenchmarks` and `DispatcherBenchmarks` A/B under 1/2/4 pump-thread shapes (new multi-thread
rows needed — current benchmarks are single-threaded); `SelfTrafficBenchmarks` for the reorder.

---

## F3. O(N) stop-the-world expiry sweeps, with allocation under the lock

### Evidence

- `FlowTable.RemoveExpired` (`FlowTable.cs:90-118`): enumerates all 65,536 states under the global gate,
  per-entry `DateTimeOffset` subtraction, plus the nested-lock `isHeld` predicate.
- `TcpRedirectTable.RemoveExpired` (`TcpRedirectTable.cs:319-335`): `Where(...).Distinct().ToArray()`
  **LINQ allocation under the gate**.
- `UdpProxyCoordinator.RemoveExpiredAsync` (`UdpProxyCoordinator.cs:341-368`): LINQ snapshot allocation
  under the gate; the UDP leg runs every max(5 s, idleTimeout/2) (`IdleExpirySweeper.cs:19-22,161-163`).
- `UdpAssociationPool.SweepIdleAssociationsAsync` (`UdpAssociationPool.cs:214-230`): `SelectMany/Where`
  list allocation under the pool gate.

At 65k flows each sweep stalls every pump thread for single-digit milliseconds (worse under contention),
and the UDP leg does it every 5 seconds.

### Proposals

1. **Intrusive timing wheel.** Embed a doubly-linked list node in `FlowState`/`UdpSessionSlot`/
   `TcpRedirectAssociation` (zero allocation). `Touch` unlinks/relinks into the current-second bucket in
   O(1); the sweep advances the wheel pointer and processes only the buckets it crosses —
   **O(expired) instead of O(N)**. One wheel implementation serves flows, UDP sessions, TCP associations,
   tombstones and cooldowns; combined with F2.3 sharding, each stripe owns a small wheel and sweeps
   independently.
2. Lighter alternative: **incremental cursor sweep** — the table holds an enumeration cursor; each tick
   scans only K entries (e.g. 2,048), completing a round in N/K ticks. Minimal diff, no new structure.
3. Regardless of choice: **no allocation under any table lock.** Reuse scratch buffers the way
   `FlowTable._expiredScratch` already does; port the pattern to the TCP/UDP/pool sweeps.
4. **Bucketed activity time.** Replace `DateTimeOffset LastActivityUtc` with a `uint _activityBucket`
   (1–2 s granularity). `Touch` degrades to one int store, expiry compares buckets, and the per-hit clock
   call from F2.5 disappears.

### Measurement

New micro: sweep pause = max time a `TryResolve` blocks during a sweep tick at 65k seeded flows
(assert < 0.5 ms). `GcSoakScenario` unchanged-green; sweep allocation asserted zero by gate test.

---

## F4. Data structures: fat FlowKey, repeated parsing, per-packet sequence lock

### Evidence

- `FlowKey` is ~100+ bytes: two `Endpoint`s (each ~20 B: `UInt128` bits + family + scope + port),
  family/protocol/origin, a **`string? OriginAdapterId`**, and a generation long
  (`src/WinForward.Core/Domain.cs:76-118`). `Equals` performs `string.Equals(OriginAdapterId, Ordinal)`
  on every dictionary probe, and the struct spans multiple cache lines. `FlowContext` stacks two more
  strings (`AdapterId`, `AdapterName`, `Domain.cs:146-152`), and `CapturedFlowPacket` is copied by value
  several times per packet (`packet with { ... }` at dispatch).
- The same frame is parsed repeatedly per packet: the processor's `IPTcpUdpPacket.TryParse`
  (`CapturePacketProcessor.cs:67`), `TcpFrameRewriter.IsTcpSyn` re-parses (`TcpFrameRewriter.cs:67-76`),
  `TcpSequenceObservation.TryReadTcpSequenceAdvance` re-parses (`TcpSequenceObservation.cs:53-71`), and
  the checksum rewriter revalidates the full header chain (`PacketChecksums.cs:105-144`). Four header
  walks per redirected packet.
- `TcpRedirectAssociation._sequenceGate`: two lock entries per packet (client-leg and server-leg
  trackers) guarding two `uint?` fields (`TcpRedirectTable.cs:110-140`).

### Proposals

1. **Intern adapter identity.** Assign each capture-scope adapter a `ushort AdapterSlot` at composition
   (side table maps slot ↔ StableId/generation/friendly name). The key stores the slot instead of the
   string: `FlowKey` shrinks to ≤48 B — one cache line, all-integer equality. Log formatting resolves
   names via the slot table (log paths only).
2. **Parse once, carry the view.** The classifier already produced a `PacketView`; persist the derived
   offsets (IP header length, transport offset, protocol, flags-byte position) as a small struct on
   `CapturedFlowPacket`. `IsTcpSyn`, sequence observation and the rewriter consume the offsets instead
   of re-walking headers. Saves three redundant parses and their validation branches per packet.
3. **Atomize sequence trackers.** `uint?` → `long` (−1 = unobserved); writers use a CAS-max loop (the
   tracked value only advances), readers `Volatile.Read`. The RST builders are cold paths and tolerate
   the weakly-consistent read. Removes two lock entries per packet.
4. **Slim FlowContext.** Process name/path and adapter names are consumed by policy evaluation (once
   per flow) and logging — move them into an interned `FlowMetadata` reference created at claim time,
   cutting the per-packet struct copies from ~150 B to ~64 B.

### Measurement

`FlowTableBenchmarks` probe ns before/after; `ParserBenchmarks`; dispatcher warm-path 160 B gate must
stay green (slimming only reduces struct copy size, allocation stays zero).

---

## F5. Pump I/O shape: queue-size query per poll + 1 ms sleep polling

### Evidence

`NdisApiDriver.TryReadPackets` performs `GetAdapterPacketQueueSize` (IOCTL #1) and only then, when
non-empty, the batched `ReadPackets` (IOCTL #2) (`src/WinForward.NdisApi/NdisApiDriver.cs:142-166`).
Under load every batch pays the query; at idle every adapter pays one query IOCTL per ~1 ms poll cycle
(`NdisCapturePump` paces empty polls with `Thread.Sleep(1 ms)`, `NdisCapture.cs:142,318`).

### Proposals

1. **Speculative read.** Issue the batched `ReadPackets` unconditionally and interpret a zero-fill
   result as the empty queue (a shape `NdisNativeCallStatus.InterpretBatchReadResult` already handles).
   Halves IOCTLs under load; idle cost is unchanged (one IOCTL either way). Must be validated against
   the pinned ABI's empty-queue semantics on Windows before adoption.
2. **Event-driven wake instead of sleep-polling.** The ndisapi API supports binding a Win32 event per
   adapter (`SetPacketEvent`) signaled when the queue transitions non-empty. Idle pump:
   `WaitForSingleObject(event, timeout)`; on wake, drain-till-empty, then re-arm. Idle CPU goes to zero
   and per-packet tail latency drops from ~0.5 ms average poll jitter to wake-on-arrival; burst behavior
   keeps today's drain loop. Cancellation/stop latency stays bounded by the wait timeout, preserving the
   pump's documented disposal contract.

### Measurement

`CapturePumpBenchmarks` extended with an idle-CPU row and a wake-latency row; existing
`IdlePollIterationsAllocateNoManagedBytes` gate must stay green.

---

## F6. UDP per-session resident footprint

### Evidence

Each UDP session owns: one relay socket with a **128 KiB kernel receive buffer** by default
(`Socks5UdpTransport.DefaultRelaySocketReceiveBufferSize`, `Socks5UdpTransport.cs:174`), one ~1.6 KiB
native receive-window lease (pool default capacity 256 — every session beyond 256 is an overflow
allocation, `NativeBufferPool.cs:22`), and one parked async receive loop (`UdpProxySession.cs:247-300`).
At the 16,384-session capacity this projects to ~2 GiB of kernel receive buffers and 16k pending IOCP
reads.

### Proposals

- Lower the default per-session relay receive buffer to 32–64 KiB (config validation already warns on
  the capacity×buffer product); bursts are still absorbed by the proxy's own pacing.
- Size the receive-window pool from the session capacity at composition so steady state stops running
  on tracked overflow allocations.
- **Adaptive idle TTL.** One-shot exchanges (DNS is the UDP majority) get a short TTL (~5 s); sessions
  with sustained exchange keep the long one. The capability-evidence counters already count datagrams
  per flow, so an exchange-count threshold is nearly free; shorter one-shot retention shrinks the
  resident set proportionally.
- Dead end, recorded so it is not re-explored: multiple flows sharing one relay socket with
  destination-based demultiplexing cannot disambiguate replies when several client flows target the
  same destination (the SOCKS5 UDP reply header carries only the peer address). Per-flow source ports
  are a NAT requirement; per-flow sockets are irreducible.

---

## F7. The ceiling lever (awareness, not a near-term plan)

Every proxied byte today makes a user-mode per-packet round trip: driver → pump → rewrite → inject →
Windows TCP stack → relay socket → SOCKS5 → kernel. The terminal optimization is a WFP
(Windows Filtering Platform) ALE_REDIRECT kernel callout, which performs redirection in-kernel and
leaves only the relay in user mode — per-packet user-mode crossings go to zero. The cost is driver
development and signing, an effort class above everything above. F1 (batched injection) captures most
of the attainable win first; revisit WFP only if the target becomes "10 Gbps+ at low CPU".

---

## Sequenced roadmap (benefit/risk order)

| # | Item | Nature | Expected gain | Risk |
|---|------|--------|---------------|------|
| 1 | F1: in-place rewrite + lane-batched redirect injection; UDP response coalescing | Recomposes the proven lane mechanism | −1 memcpy/packet, ~97% fewer injection IOCTLs | Low; semantics answered in-place |
| 2 | F3: generational/incremental sweeps + zero allocation under locks | Local algorithm swap | Removes periodic pipeline-wide stalls | Low |
| 3 | F2.1/F2.2/F2.4: self-traffic reorder, lock-free resolve, UDP ready-path | Lock architecture | −2–3 locks + 5 probes/packet; multi-adapter scaling | Medium; needs concurrency tests |
| 4 | F4: interned keys, parse-once, atomic trackers | Data structures | Cache-line and branch savings across all paths | Medium; wide diff, intern first |
| 5 | F5: speculative read, event wait | Driver interaction | Idle CPU, tail latency, −50% read IOCTLs | Needs on-Windows ABI verification |
| 6 | F6: UDP footprint (buffer default, pool sizing, adaptive TTL) | Config/policy | Several-fold resident memory reduction | Low |

## Validation plan

- New benchmarks before changing code: `TcpRedirectInjectionBenchmarks` (IOCTL-count assertion),
  sweep-pause probe at 65k seeded flows, multi-threaded `FlowTableBenchmarks` rows.
- Existing gates that must stay green: the `HotPathAllocationGateTests` 0 B suites, dispatcher
  warm-path 160 B gate, `gc-soak` (no overflow growth, conservation identity, flat working set),
  `TcpThroughputScenario` socks5/bare ≥ 70%, `UdpBurstScenario`/`UdpLossScenario` shape anchors.
- Repo gates: `dotnet format --verify-no-changes`, `jb inspectcode` zero-Issue, Release build
  zero-warning, full test suite behavior-zero.

## Rejected / dead-end ideas (recorded against re-exploration)

- Shared relay socket across flows with destination demux — ambiguous replies (NAT requirement), F6.
- Rewriting redirect frames into a *managed* ArrayPool staging buffer — strictly worse than in-place
  on the capture slot; the lane flush owns native buffers.
- Per-packet `Task.Delay` pacing or per-packet timers anywhere on the pump — allocation and timer-queue
  costs dominated historical designs; the pump's synchronous shape is deliberate.
- Relaxing the 0 B allocation gates to admit "small" per-packet costs — `hot-path.md` explicitly
  forbids threshold relaxation; every proposal above keeps the steady-state 0 B shape.
