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

---

## Addendum (2026-09-29, second pass): steady-state memory, the claim-path correction, F8, and the target FlowTable

This addendum covers the resident-memory question (~100 MB private working set on a typical desktop
at steady load) and corrects an over-simplification in the first pass: the claim path is **not**
uniformly cold, and the one configuration shape in which it is genuinely cold exposes a previously
unrecorded structural finding (F8).

### A1. Where the ~100 MB lives (code-grounded census model)

Typical desktop load (~100 live TCP connections, ~300 UDP flows, 1–2 adapters), estimated from the
composition in `src/WinForward.Cli/DurableCaptureBundle.cs` and the table/pool implementations:

| # | Component | Estimate | Mechanism |
|---|-----------|----------|-----------|
| 1a | `FlowTable` pre-allocation | **~20 MB managed** | `_states` pre-sized to 65,536 (~112 B/Entry ≈ 7.3 MB) + `_transportIndex` pre-sized to 131,072 (~88 B/Entry ≈ 11.5 MB) + `_freeStates` 0.5 MB (`src/WinForward.Core/FlowTable.cs:25-27`). Allocated at startup, mostly empty forever. |
| 1b | Managed baseline | ~13 MB | gc-soak measured post-full-GC heap ~12–13 MiB (small-capacity harness; production adds 1a). |
| 1c | GC committed-but-unused headroom | ~10–20 MB | Workstation non-concurrent GC + 128 MiB `HeapHardLimit` (`WinForward.Cli.csproj:35`): segments committed during churn bursts are not returned until a gen2 GC chooses to decommit. |
| 2 | Relay pump windows (native) | **~13 MB @100 conns** | 2 × 64 KiB per TCP connection held for the connection's whole life (`TcpProxyRelay.cs:243-248`); an idle SSH connection costs the same 128 KiB as a saturated one. |
| 3 | Runtime base (AOT code pages, socket plumbing, thread pool) | ~20 MB | Typical single-file AOT networking process; `InvariantGlobalization` already on. |
| 4 | UDP sessions (user mode) | ~3 MB @300 | ~10 KB/session: 1.6 KB native receive window + session/transport/socket/receive-loop objects. (The 128 KiB relay receive buffer is **kernel** memory — not private WS, but still system memory.) |
| 5 | Other native pools | ~1.5 MB | syn-copy / setup-queue / receive-window / NDIS packet pools, each 256 × ~1.5 KB at high-water (all constructed with default capacity, `DurableCaptureBundle.cs:118-195`). |
| 6 | Threads | ~1.5–2 MB | pump per adapter + `SetupExecutor` default `max(2×CPU, 16)` workers + sweeper/heartbeat/monitor. |
| 7 | ndisapi.dll, batch buffers, misc | ~1 MB | 32 × ~2 KB per adapter batch. |

The fixable structural share is items 1a, 1c and 2 — roughly 50–60% of the observed footprint.

### A2. The claim path is not uniformly cold (correction)

The first pass justified lazy table growth with "new-flow claim is already a cold path". Measured
against the code, that claim splits three ways (`FlowDispatcher.DispatchSlowAsync`):

- **Shape A — no process rules** (network/port/adapter rules only): attribution is skipped entirely
  (`AttributeProcessAsync` gates on `_policy.RequiresProcessAttribution`), so claim ≈ self-traffic
  check + reverse prefilter + `Policy.Evaluate` + insert ≈ **1–3 µs — warm-ish, not cold**. Desktop
  churn is dominated by DNS/QUIC/TCP connects (10–100 claims/s, ~1,000/s bursts), so the per-claim
  cost is irrelevant, but the real price of lazy growth is not per-claim: it is the ~6 whole-table
  O(N) rehash stalls per run (1024→…→65,536), each stalling every pump under the global gate
  (~1.5–2 ms worst at 32k→64k). At desktop rates the driver queue absorbs it; under sustained load
  with a genuinely large working set it could overflow the queue.
  **Corrected recommendation:** floor pre-size of 4,096–8,192 (~0.5–1 MB) + lazy doubling beyond,
  or — better — the sharded design of A4, where per-shard growth divides any rehash stall by the
  shard count (~64×) and makes incremental-rehash machinery unnecessary.
- **Shape B — any rule carries `Processes`**: claim is **freezing** (F8 below), and rehash noise
  is irrelevant next to it. The correct response is to fix F8, not to accept it.
- **Shape C — forwarded (VM) flows**: attribution is skipped by origin, so claim ≈ adapter-qualified
  rule scan + insert ≈ 1–2 µs, but VM churn can be tens of times desktop churn — not cold, yet it
  raises the weight of F2 (lock contention) correspondingly.

Related small print: `Policy.Evaluate` runs **inside** the flow-table gate today (the `decide()`
factory is invoked under `TryClaimResolved`'s lock) and its `RemoteNetworks.Any(...)` /
`RemotePorts.Any(...)` LINQ allocates a closure per evaluation — worth hand-rolling into loops when
the claim path is next touched.

### A3. F8: process attribution runs on the pump thread (new finding)

With any process rule configured, every new **host** flow pays, synchronously on the capture pump
thread (`WindowsProcessAttributor.FindAsync`, `src/WinForward.Windows/ProcessAttribution.cs:28`):

1. `GetExtendedTcpTable`/`GetExtendedUdpTable` — a **system-wide connection-table enumeration**
   (thousands of rows on a busy desktop), preceded by a size probe and followed by a full managed
   copy (`new TcpOwner[rowCount]`, plus two `new IPAddress` per TCP row) and a LINQ
   `Where/Select/Distinct/ToArray`;
2. on a miss, `await Task.Delay(2 ms)` and the **whole scan again**;
3. on a hit, `Process.GetProcessById` + `OpenProcess` + `QueryFullProcessImageName`.

The pump's `InvokeHandler` blocks on the handler's ValueTask (`NdisCapturePump.cs:327-346`), so the
entire sequence — including the 2 ms retry — stalls **every** flow on that adapter, ~0.5–7 ms per
new flow, at tens of new flows per second during browsing bursts, while generating MB/s of transient
allocations that contradict the GC-off posture. One new flow stalls all flows.

**Proposals (two, complementary):**

- **Move attribution off the pump, reusing the proven R8 shape.** On a flow miss that requires
  attribution, retain the packet in a bounded per-flow pending structure (the TCP pending-SYN index
  and the UDP setup queue are the existing templates), return the pump immediately, and run
  attribution + policy evaluation + claim on a setup worker; the completion applies the decision and
  drains the flow's pending packets in order. This generalizes `TcpPendingSynSetupIndex` from
  "SYN retention" to "first-packet-of-flow retention".
- **Cache the owner tables as a ~250–500 ms snapshot.** Flow churn arrives in bursts (one page load
  = dozens of near-simultaneous connections); one scan should serve a whole burst. On a snapshot
  miss, fall back to a fresh scan (= today's behavior) and refresh the snapshot with the result, so
  the worst case is unchanged and the common case costs one scan per burst. Staleness risk is a
  ≤250 ms window where a very young socket is unattributed — already covered by the existing
  attribution-miss path (policy continues without a process match), and the miss-retry already
  accepts a smaller version of the same window today.

### A4. The target FlowTable shape (unified design; supersedes the scattered F2/F3/F4/M1 sketches)

Design goals: memory follows the live working set (not the capacity cap); the warm resolve is
lock-free, single-probe, and clock-call-free; one table serves both orientations; expiry is
O(expired)-ish with bounded pause; the public API (`TryResolve` / `TryClaimResolved` /
`RemoveExpired` / `Count` / `Capacity`) and the dispatcher's semantics are unchanged.

1. **Canonical transport key.** Normalize the endpoint pair order-independently (order the two
   endpoints by (address bits, port); the pair (ep_lo, ep_hi) is the canonical key, with family and
   protocol). Forward and reverse packets hash identically, so the `_transportIndex` dictionary
   (~12 MB) dies; origin kind/adapter stay provenance on the state, preserving today's
   reverse/cross-adapter aliasing semantics. Combined with F4's interned adapter slot, the key is
   ≤48 B of pure integers.
2. **Layout: grow-only state slab + per-shard open-addressing index.** 64 shards selected by the
   hash's high bits. Each shard holds a power-of-two `(ulong hashTag, int stateIndex)` index
   (12–16 B/slot, linear probe, backward-shift deletion, grow at load 0.7) and its own write lock.
   The full key lives once, in the state slab. Readers snapshot the index array reference
   (`Volatile.Read`) and probe lock-free; a slot tag match is **confirmed against the full key in
   the slab**, so a torn/concurrent slot read can only produce a false miss (which the claim path
   re-checks under the shard lock), never a false hit.
3. **Warm resolve.** Hash (64-bit mix over the canonical key, computed from the parse-once view) →
   shard → probe → confirm → `Volatile.Write(state.activityBucket, currentBucket)` with the bucket
   passed down from the pump iteration. No lock, no clock call, ~15–40 ns.
4. **Claim.** Miss → full self-traffic check (moved here from the dispatcher warm entry, F2.1; self
   flows are deliberately **not** cached, so an unregistered relay tuple can never alias a later
   non-self flow to a stale pass) → (after F8, attribution already happened on a setup worker) →
   `Policy.Evaluate` → shard lock → re-probe (double-check) → global `Interlocked` capacity gate
   (fail-closed block beyond 65,536, unchanged) → publish fully-initialized state, then the slot.
   Evaluation stays under the shard lock in v1 (contention is already divided by 64); if rule-heavy
   configs profile hot, v2 moves evaluation out with a per-key claiming marker.
5. **Activity and expiry.** `uint activityBucket` (1–2 s granularity) replaces `DateTimeOffset`;
   touch is one volatile store, relinked into a per-shard wheel only when the bucket changes (≤1
   lock per flow per ~2 s on hot flows, zero on cold). Sweep walks aged-out wheel buckets per shard
   — O(expired), pause bounded by shard count, zero allocation, and the `isHeld` predicate runs
   **outside** any table lock (today it runs under the flow gate and enters TCP-store locks — a
   lock-nesting edge worth removing regardless). Lighter fallback: per-shard incremental cursor
   scan (K slots per tick) if the wheel's links prove fiddly.
6. **Growth.** Per-shard doubling from a small floor (64 slots/shard ≈ 4k total ≈ <100 KB at
   startup). A shard rehash of ~1k entries costs ~20–30 µs under that shard's lock only — sharding
   already supplies the "incremental rehash" benefit, so no Redis-style migration machinery.
   Readers probing a stale (pre-grow) array false-miss and re-check under the shard lock: safe.
7. **What dies:** `_transportIndex` (~12 MB), the 65,536/131,072 pre-allocations (~7.8 MB), the
   `_freeStates` recycling (states become short-lived gen0 objects; per-flow allocation is already
   blessed by the session-bookkeeping budgets), `DateTimeOffset` activity tracking, the per-hit
   clock call, and the read-path global gate.

   Memory at 3–5k live flows (typical desktop): slab ~0.5 MB + index ~0.2 MB ≈ **<1 MB vs ~20 MB
   today**; at the full 65,536 capacity: ~7–8 MB, still below today's empty-table cost.

### A5. Randomized/approximate structures: two safe targets, three forbidden ones

The safe targets share a property: they remember **bad news**, so forgetting early degrades to the
pre-table behavior (harmless) and false hits are made astronomically rare with wide fingerprints.

- **TIME_WAIT tombstones** (today exact dictionaries, ~4–6 MB at capacity): replace with a
  direct-mapped fingerprint cache — `hash(key)` selects a slot storing a 32-bit fingerprint +
  8-byte timestamp; inserts overwrite collisions. Collision = false negative = a straggler leaks to
  the pass path (exactly the pre-tombstone behavior); fingerprint hit rate 2⁻³² ≈ never. Prefer
  this over a Bloom filter: Bloom false positives are *deterministic per key*, so an unlucky new
  connection's SYN would be swallowed for the whole grace window, while the fingerprint cache's
  effective false-positive rate is ~8 orders of magnitude lower. ~256 KB replaces ~4–6 MB.
- **Setup/RST cooldown tables** (UDP 1 s, TCP capacity-reset): same lossy-cache treatment; they are
  rate limiters, so early eviction is unobservable. ~64–256 KB each.

Forbidden: **flow decisions** (a false positive routes traffic to the wrong proxy or blocks the
wrong flow — a security-relevant error, and membership structures cannot store a decision anyway);
**SelfTrafficRegistry** (a false negative loops the proxy into its own traffic); **redirect-table
indexes** (routing correctness). The `_candidatePorts` reference counts could shrink from
`int[65536]` (256 KB) to a 4-bit nibble array (32 KB) with saturation — minor, optional.

### A6. Roadmap delta

| # | Item | Nature | Expected gain | Risk |
|---|------|--------|---------------|------|
| 7 | A4 FlowTable rebuild (canonical key, sharding, slab, bucketed activity) | Data structure + concurrency | −19 MB steady state; lock-free resolve; O(expired) sweeps | Medium; wide diff, gated by existing table tests |
| 8 | F8 attribution off-pump (R8 shape) + owner-table snapshot cache | Pipeline | Removes ms-scale all-flow stalls and MB/s transient garbage with process rules | Medium; pending-packet ordering needs the TCP/UDP templates' care |
| 9 | Relay window size classes (4/16/64 KiB by fill-rate EMA) + pool trim | Resource | −10 MB @100 conns; idle connections at 8 KiB | Medium; `TcpThroughputScenario` ≥70% anchor must hold |
| 10 | Idle GC compaction + `TotalCommittedBytes` in the heartbeat snapshot | Runtime | −10–15 MB committed headroom; production visibility | Low; a few ms at idle transitions |
| 11 | Lossy fingerprint caches for tombstones/cooldowns | Approximation | −5–8 MB | Low; failure modes converge to pre-table behavior |
| 12 | UDP adaptive TTL (one-shot sessions ~5 s) + smaller default relay receive buffer | Policy/config | −2–3 MB + kernel memory | Low |

(Items 1–6 from the first-pass roadmap stand as recorded.)
