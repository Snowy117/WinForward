# TCP/UDP data-path structural performance review — second pass

> Recorded 2026-10-03. Method: full read of the capture → classify → dispatch → execute trunk
> (`WinForward.NdisApi`, `WinForward.Core`, `WinForward.Runtime/Capture`, `WinForward.Runtime`
> root), plus two dedicated sub-reviews covering all of `Runtime/TcpRedirect/` (22 files) and
> `Runtime/UdpProxy/` + `Runtime/Socks5/` (23 files), cross-checked against the recorded
> benchmark artifacts. This pass deliberately re-verifies the landed state of the 2026-09-29
> research (`09-29-tcp-udp-path-structural-perf`, F1–F8 + addenda A1–A6) so that nothing
> already fixed is re-proposed. Scope: performance and resource *structure* — lock
> architecture, I/O shape, sweep algorithms, per-connection/per-session resource models.

---

## 0. Landed state of the 2026-09-29 findings (do not re-tread)

| Finding | Status in the current tree |
|---|---|
| F1 in-place rewrite + lane-batched redirect injection | **Landed** — `TcpProxyCoordinator.Injections.cs:244-265` rewrites in place on the capture slot and defers through redirect lanes (`CanRewriteInPlace`, `TryDeferRedirectFrame`). |
| F2.1 self-traffic off the warm path | **Landed** — warm entry pays only the lock-free wildcard half (`SelfTrafficRegistry.IsWildcardOwned`, two `ConcurrentDictionary` probes); the exact-tuple half moved to claim time. |
| F2.5 / F3.4 bucketed activity clock | **Landed** — `ActivityBucket`/`ActivityBucketClock` (500 ms buckets); warm touches are one volatile write, no clock call. |
| F3 bounded-pause sweeps | **Partially landed** — `FlowTable.RemoveExpired` is a chunked round (≤256 examinations / ≤1 removal per `_gate` hold, live-slot registry, predicate runs lock-free). The scan itself is still O(N); see §5 item R6. |
| F4 interned keys + parse-once + atomic trackers | **Landed** — `FlowKey` 64 B with interned adapter slot, `PacketLayout` carried on the packet, CAS-max sequence trackers. |
| F5 pump I/O shape | **Landed** — speculative batched read first (query demoted to failure disambiguator, self-healing ABI guard) + event-driven idle wait (`INdisPacketArrivalSignal`). Idle CPU 0.0178 → 0.00055 CPU-s/s. |
| F6 UDP per-session footprint | **Landed** — relay RCVBUF default 128→64 KiB, capacity-sized receive-window pool, two-class idle TTL (one-shot 5 s). |
| F8 attribution off-pump | **Landed** — bounded pending index + setup-worker attribution + owner-table epoch coalescer. pumpBlockedMs p95 7.5 ms → 0.02 ms. |
| A4 FlowTable rebuild (canonical key, sharding, slab, wheel) | **Not landed** — mitigated by the direct-mapped warm cache; the ~17.5 MB pre-allocation, the global `_gate`, and the double dictionary are all still present. See §5 R9. |
| A5 lossy fingerprint caches (tombstones/cooldowns) | **Not landed** — `TcpRedirectTombstoneTable` is still two exact `Dictionary`s under a `Lock`. Recorded as non-urgent (≤16k scale); see §5 R6 note. |
| A6.9 relay window size classes | **Not landed** — fixed 2×64 KiB per connection. See §4 F-big-2. |
| UDP response coalescing (F1 roadmap companion, `08-30-batched-ioctls` Phase 2) | **Not landed** — every response is one single-packet IOCTL. See §3. |

The micro layer underneath all of this is contract-locked at zero allocation on the warm
dispatch path and was re-verified as sound: span parsing on the pump-owned native frame, lazy
lease materialization, RFC 1624 incremental checksums (full recompute kept only as a test
oracle), batched capture reads, `BoundedSetupQueue` (inline first entry, bounded stamp
rotation, exact lease ownership), and the UDP transport's three send shapes (uncontended
gate + sync `SendTo` / cached-SAEA overlapped / documented cold copy on gate contention).

---

## 1. Tier 1 — the warm-cache clamp accident (same formula, two tables; fix first)

Both per-path warm caches are sized by `Math.Clamp(capacity * 8, 1024, 16384)`, so at the
shipped capacity of 16,384 the intent of *eight slots per entry* collapses to **one** —
load factor λ→1.0:

- `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:230` — `_warmReverse`/`_warmOriginal`.
- `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:85-86` — `_sessionCache`.

At λ=1.0 live entries evict each other constantly, so roughly half of all hot packets fall
back to the global `_gate` — and the more load, the more collisions, the more lock
contention: a positive feedback loop at exactly the moment the table is fullest. Compare
`src/WinForward.Core/FlowTable.cs:66`, which sizes its warm cache at 4 slots/flow with a
collision model (cap 262,144 slots ≈ 2 MB) and a measured ~2 % miss target.

The UDP side has a second, compounding defect: the gated ready-hit branch never writes back
to the cache (`UdpProxyCoordinator.Send.cs:98-109`), so a collided flow misses *forever* and
pays `_gate` + clock + nested cooldown-table lock (`UdpSetupCooldownTable.cs:37-44`) on every
datagram. `FlowTable.TryResolveLocked` (`FlowTable.cs:484`) is write-through — the UDP path
is the only cache consumer that isn't.

**Fix.** Raise both clamps to `capacity × 4..8` (TCP: 65,536 slots × 8 B × 2 caches ≈ 1 MiB;
UDP: 131,072 slots ≈ 1 MiB — the FlowTable already carries a 2 MB precedent) and add the
one-line `Volatile.Write` write-back on the UDP ready branch. A handful of lines total; the
prize is the removal of a per-packet global lock at full load.

---

## 2. Tier 2 — the remaining steady-state allocation sources on hot paths

### F-alloc-1. TCP relay: two cancellation-token registrations per chunk

`src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs:258,275` passes `stall.Token` to every
`ReadAsync`/`WriteAsync`. A linked-CTS token is always cancelable, so the socket registers a
callback on every operation — ~100–200 B of `CallbackNode` per chunk **even when the
operation completes synchronously**. At 10 Gbps single-stream (~16k chunks/s) that is
2–3 MB/s of pure GC, and it is the only allocation source in the whole product that scales
with data rate. `StallWindow` already removed the old design's 160 B/chunk; this is what is
left.

**Approach.** Replace token-based stall detection with a progress timestamp + shared
watchdog: each pump `Volatile.Write`s a timestamp after every read/write (~5 ns), the I/O
calls take `CancellationToken.None` (which routes to the socket's cached awaitable SAEA —
zero registration), and one shared periodic watchdog disposes sockets whose timestamp is
older than the stall window (equivalent to today's D-C3-3 socket-close propagation, which
already relies on close to unblock a parked pump). This also removes `StallWindow`'s
per-second `CancelAfter` re-arm — at 10k connections that is ~20k TimerQueue updates/s
against a global lock. While there: bypass `NetworkStream` and call
`Socket.ReceiveAsync`/`SendAsync` directly.

### F-alloc-2. UDP receive path: two managed allocations per response

`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:465-492` — the async `ValueTask`
receive boxes a state machine per datagram (~150–250 B), and
`SocketReceiveFromResult.RemoteEndPoint` materializes a fresh `IPEndPoint` per datagram.
After the send warm path reached zero, this is UDP's only per-packet allocation point.

**Approach.** Per-transport reusable `SocketAsyncEventArgs` (or a pooled `IValueTaskSource`)
with synchronous decode, and relay-source validation as a byte comparison against the cached
`SocketAddress` (`IsAcceptableRelaySource` only checks port+family anyway) so no `IPEndPoint`
is materialized. Profile first to confirm share.

### F-alloc-3. Per-packet Socks5Server string dictionary lookup

`src/WinForward.Runtime/FlowDispatcher.cs:203` — every proxied packet on the warm path runs
`_servers.TryGetValue(decision.ProxyServerName, out var server)`. .NET string hashes are
**not cached**: every packet pays a Marvin hash over the server name plus a string compare.

**Approach.** `FlowDecision` is immutable and the server snapshot is immutable, so resolve
the name once at claim time and cache the `Socks5Server` reference on the `FlowState` (an
opaque slot the dispatcher casts back), carried through `FlowStateView` snapshots. The warm
path becomes one reference read. Same fix on the slow path's `ExecuteDecisionAsync`
(`FlowDispatcher.cs:483`) for free.

---

## 3. Tier 3 — the UDP response path: request direction optimal, response direction three traversals + one IOCTL per packet

`src/WinForward.Runtime/UdpProxy/UdpResponseReinjector.cs:93-133`: every relay response pays
pool rent → frame build (payload copy, `UdpFrameBuilder.cs:165`) → **full** UDP checksum
(`PacketChecksums.cs:315-365`) → a single-packet `SendToMstcp`/`SendToAdapter` IOCTL
(`NdisPacketReinjector.cs:26-32`). The payload is touched three times and crosses the kernel
once per response — while the pass path batches up to 32 frames per IOCTL through lanes
(`NdisPacketActionExecutor.cs:190-209`).

**Approach, in benefit-per-line order:**

1. **Write 0 as the IPv4 UDP checksum on rebuilt responses.** Legal per RFC 768, accepted by
   host and VM stacks, and removes the O(payload) summation entirely — one line plus a config
   escape hatch for middleboxes that drop checksum-0 datagrams. The single biggest
   line-for-line win in this report.
2. **Fuse copy and summation** into one pass where the checksum is still required (IPv6).
3. **Micro-batch the reinjection.** Responses arrive on socket-completion threads, not pump
   threads, so the lane model needs a dual trigger (flush at N accumulated frames or T µs).
   This is the recorded `08-30-batched-ioctls` Phase 2 item; the DNS-dense measurement
   trigger in its PRD should be evaluated against current numbers and scheduled.
4. Deepest option, do last: receive into the native window at a 62 B headroom offset so the
   response frame header can be written in front of the payload in the same buffer (true
   zero-copy rebuild; requires bridging `NativeBufferPool` leases and `NdisPacketBuffer`
   metadata).

---

## 4. Tier 4 — per-connection / per-session resource models: the real capacity ceiling

CPU is not the first limit; the per-flow resource kit is. At the shipped 16,384 capacities:

| Resource | Today | Bill at capacity |
|---|---|---|
| TCP redirect listener | one `new Socket`+`Bind(port 0)`+`Listen(64)` **per connection** (`TcpRedirectListener.cs:19-23`), plus accept-loop + completion-observer + drain + 2 pump tasks per session (`TcpRedirectAcceptor.cs:97-182`, `TcpProxyRelay.cs:144-145`) | 16k listen sockets; ~32k ephemeral ports across listener+upstream (Windows dynamic range ≈49k total); ~64k long-lived tasks |
| TCP relay pump windows | 2×64 KiB native leases held for the connection's whole life (`TcpProxyRelay.cs:243-248`); shared pool default capacity 64 (`TcpProxyRelay.cs:95`) | ≥2 GiB native; below-capacity pool churns `AllocZeroed`/`Free` per pump start (`NativeBufferPool.cs:86-93,112-117`) |
| UDP relay socket | one per flow + 64 KiB kernel RCVBUF + one permanently parked receive loop (`Socks5UdpTransport.cs:177-570`, `UdpProxySession.cs:333-386`) | ~1 GiB kernel buffers; 16,384 parked overlapped reads; ~25 MB managed send buffers + ~25 MB native windows; **UDP ephemeral ports exactly saturate the default dynamic range** |
| SYN template | full-frame (1,514 B) native lease per association holding a ≤128 B template (`TcpSequenceObservation.cs:34-38`) | ~24 MB (144 MB with jumbo frames) |
| FlowTable | `_states`(65,536) + `_transportIndex`(131,072) + 2 MB warm slots + registry arrays (`FlowTable.cs:60-69`) | ~17.5 MB managed, allocated at startup, mostly empty forever |

**Approaches, by ambition:**

1. **Size-classed pools (no algorithmic risk; changes confined to constructor parameters and
   rent/return points).** Relay windows in 4/16/64 KiB classes, migrated at chunk boundaries
   by a fill-rate EMA (an idle SSH session costs 8 KiB, not 128 KiB); SYN templates into a
   256 B fixed pool; align the pump-buffer pool capacity with the relay concurrency budget
   and alarm on `_overflowAllocations`. Roughly an order of magnitude off native residency.
2. **Shared listener + accept demultiplex (the single largest architectural cut).** The
   prerequisite is already in the tree: `_byReverse` is a full-tuple index
   (`TcpRedirectTable.cs:196`) and does not depend on the per-flow port; the acceptor already
   validates the accepted peer (`TcpRedirectAcceptor.cs:73`). Dispatch accepted sockets by
   peer tuple against the reverse index; keep the per-flow listener as a cold fallback for
   the rare (client-addr:port, different-server) collision. Removes 16k sockets/tasks/ports.
   Client transparency is preserved because rewriting happens at the NDIS layer.
3. **UDP sockets cannot be merged — compress the unit cost instead.** The SOCKS5 UDP reply
   header carries only the peer address, so two flows to the same remote (DNS, the majority)
   cannot be demultiplexed on one socket; the dead end is recorded and should not be
   re-explored. Do instead: adaptive RCVBUF (start 16–32 KiB, raise on burst evidence), and
   write the `udpSessionCapacity` × `netsh dynamicport udp` relationship into configuration
   validation so the port-range ceiling is a deployment error, not a runtime surprise.
4. **FlowTable rebuild per the A4 target shape** (fully designed in the 2026-09-29 addendum):
   canonical order-independent transport key kills `_transportIndex` (−9 MB) → 64 shards of
   open-addressing `(hashTag, stateIndex)` indexes over a grow-only state slab → memory
   follows the live working set (<1 MB at 3–5k flows vs 17.5 MB today), lock-free reads,
   O(expired) bucketed sweeps. The design is ready; it needs scheduling, not research.

---

## 5. Tier 5 — work done under locks that should not be

- **R1. Policy evaluation runs inside the flow-table gate, with LINQ closures and per-evaluation
  string normalization.** `FlowTable.TryClaimResolved` invokes `decide()` under `_gate`
  (`FlowTable.cs:254`); `RuleMatcher.IsMatch` (`Policy.cs:22-23`) allocates a closure per
  evaluation via `RemoteNetworks.Any(...)`/`RemotePorts.Any(...)`; `ProcessSelectorMatcher`
  re-runs `Replace('/', '\\')` + `Path.GetFullPath` + `GetFileName` — allocating strings —
  per selector per evaluation. **Fix:** precompute the decision outside the lock and use the
  existing by-value overload (`FlowTable.cs:272`); compile the ruleset at load time
  (pre-normalized selectors, filename `OrdinalIgnoreCase` HashSet, CIDRs bucketed by family,
  sorted port ranges with binary search, loops instead of LINQ).
- **R2. UDP setup work happens inside the coordinator's global gate.** Datagram enqueue does
  pool rent + up-to-1.5 KB memcpy + evicted-lease disposal under `_gate`
  (`UdpProxyCoordinator.cs:299-345`, called from `Send.cs:105`); session-setup scheduling
  builds a TCS and — worse — calls `SetupExecutor._signal.Release()` under the same gate
  (`UdpProxyCoordinator.cs:88 → 267-286 → SetupExecutor.cs:224`), an immediate worker wakeup
  / potential context switch inside the global lock during new-flow storms. **Fix:** gate
  does `TryCharge`/slot registration only; rent+copy+enqueue+signal move outside (adopt as a
  hard rule: *locks migrate state; I/O and allocation happen outside*).
- **R3. TCP SYN path: five entries across four locks, plus an allocation cluster.**
  `HandleSynAsync` (`TcpProxyCoordinator.cs:115-156`) chains table → tombstone → pendingSyn →
  store → pendingSyn gates, with `ToArray()`, a per-entry `PendingSynSetup`+TCS, and a
  class `PacketLease` per new flow. **Fix:** `SessionCount`/`ActiveCount` as Interlocked
  approximate counters for the fast capacity check (exact accounting stays in `TryClaim`);
  pool `PendingSynSetup`; per-worker reuse buffers for the frame copy. Also fold the three
  `GetUtcNow()` calls (:115, :126, :132) into one.
- **R4. Sweep predicate lock burst.** `FlowTable.RemoveExpired` takes a lock whose whole work
  is `cursor++` per held candidate (`FlowTable.cs:352-360`), and the `isHeld` predicate
  itself crosses store+tombstone locks (`TcpProxyCoordinator.HoldsFlow`). With 16k relaying
  connections all idle-elapsed but held, each sweep round is a concentrated 16k×3-lock burst
  — a periodic tail-latency spike. **Fix:** fold cursor advancement into the next `ScanChunk`
  hold; give `isHeld` a lock-free pre-verdict (warm slot / Bloom).
- **R5. TCP injection-failure tail blocks the pump thread.**
  `TcpProxyCoordinator.Injections.cs:216` runs the RST/teardown tail via
  `GetAwaiter().GetResult()` on the pump thread; one flow's adapter fault stalls packet
  processing for the whole adapter. **Fix:** the pump collects the failed association, marks
  it closing, and queues the RST/teardown to the cold path.
- **R6. Expiry scans remain O(N) everywhere** (FlowTable chunked but full; UDP every ≥5 s
  over `_sessions.Values`, `UdpProxyCoordinator.Sweep.cs:51-64`; tombstones/cooldowns
  similar). At ≤16k entries and current cadences this is *fine today* — do not optimize
  early. The pre-planned upgrade point is capacity ≥10⁵ or a tighter cadence: bucketed
  intrusive lists / timing wheel on the existing `ActivityBucket` infrastructure, one shared
  wheel implementation serving flows, UDP sessions, TCP associations, tombstones and
  cooldowns. The A5 fingerprint caches for tombstones/cooldowns (~256 KB replacing ~4–6 MB)
  belong to the same upgrade point.
- **R7. Cache-line bouncing on association hot fields.** `Touch` writes `_activityBucket`
  per packet (`TcpRedirectTable.cs:183`) adjacent to the CAS-max sequence trackers
  (:158-177); forward/reverse packets on different pump threads invalidate each other's
  lines every packet. **Fix:** touch only when the bucket changes (≤1 write per 500 ms) and
  pad the tracker fields apart.
- **R8. Dead write: UDP association activity propagation has no production reader.**
  `UdpProxySession.cs:496-509` propagates every bucket into `UdpAssociations` under its
  table-level gate, but `LastActivityUtc`'s only reader is test-only
  (`UdpAssociations.cs:132-137`). Every active session pays a global lock every 500 ms for
  nothing — and the association table has no leak backstop. **Fix:** either wire the table
  into `IdleExpirySweeper` (gaining the missing backstop, with propagation relaxed to every
  4–8 buckets) or delete the propagation chain.
- **R9. Smaller items.** `_candidatePorts` is `int[65536]`=256 KiB for a zero/non-zero
  prefilter — `byte[65536]` with saturating counts suffices (64 KiB)
  (`TcpRedirectTable.cs:210`); `UdpSetupCooldownTable.PruneExpired` allocates a
  `List<FlowKey>` per sweep (:62-76) against the "every sweep site allocates nothing"
  convention — reuse a scratch; `Socks5AddressCache.EvictOneUnderGate` (:59-63) evicts the
  first key in dictionary enumeration order — effectively arbitrary — and can evict the
  configured server's hot hostname, forcing a real DNS on the next dial; pin server entries
  or use a trivial LRU stamp. `SessionCacheSlot` recomputes `FlowKey.GetHashCode()` per
  datagram — cheap but foldable into the parse-once hash.

---

## 6. Reviewed and sound (verified non-issues; do not re-audit)

- Warm dispatch trunk: zero-allocation warm entry, wildcard self-traffic prefilter, lazy
  materialization, lease recycle cache, batched pass lanes, lane migration across capture
  generations.
- Capture pump: dedicated-thread synchronous loop, bounded event wait, transient-retry
  budget, disposal contract, speculative read + self-healing query-first guard.
- TCP redirect data legs: in-place rewrite, incremental checksums, lane-batched injection,
  CAS-max sequence trackers, port-bitmap reverse prefilter (exact for negatives).
- UDP send warm path: direct-mapped session cache probe, per-session send gate with cached
  `SocketAddress` and sync `SendTo`; all three send shapes correct.
- `BoundedSetupQueue`, `SetupExecutor` ring, `QuiescenceScope` (packed-word lock-free),
  `NativeBufferPool` (in-band rental state, race-free drain).
- `UdpSessionSetup` pipeline: 8-wide patient limiter, dial-start re-stamp, TTL accounting,
  per-instance delegate caching.
- `Socks5ControlConnection`: one control connection + parked 1-byte watchdog read per
  association is the right amortized shape; its per-connection CTS/timer allocations are
  cold and not worth touching.
- Coordinator sweep structure: gate-collect → outside-gate teardown, single-flight, reused
  scratch — sound at current cadence.

---

## 7. Benchmark anchors already in the repo

- `TcpRelayBenchmarks`: `OneWayAsync` at `ChunkBytes=1` is ~10× slower than at 1024
  (467.63 ms vs 44.57 ms) — per-operation fixed cost dominates small-chunk relay traffic;
  motivates §2 F-alloc-1 and any future read-coalescing (a small first read triggers one
  more speculative fill before the write; zero added latency when no more data is ready).
- `UdpReadyPathContentionBenchmarks`: `ReadySend` 127.6 ns @1 worker → 186.8 ns @4, 0 B —
  wait-free shape holds under contention.
- `UdpSessionBenchmarks`: ~5.1 KB managed per session at 1,000 sessions — matches the §4
  per-session census.

---

## 8. Ranked roadmap

| # | Item | Nature | Expected gain |
|---|---|---|---|
| 1 | §1: raise both warm-cache clamps (TCP ×4–8, UDP ×8) + UDP ready-hit write-back | a few lines | per-packet global lock at full load disappears |
| 2 | §3.1: IPv4 UDP response checksum = 0 (with config escape hatch) | one line + config | one O(payload) traversal per response |
| 3 | §2 F-alloc-1: relay stall detection via timestamp + shared watchdog; I/O on `CancellationToken.None` | small surgery | removes the only data-rate-scaled GC source; drops TimerQueue pressure |
| 4 | §2 F-alloc-3: claim-time Socks5Server cache on FlowState | local refactor | warm path free of string hashing |
| 5 | §5 R1/R2: policy precompute outside the gate + ruleset precompilation; "no I/O or allocation under locks" rule applied to UDP enqueue/setup | local refactor | lighter global gates; harder burst isolation |
| 6 | §4.1: size-classed pools (relay windows, SYN templates, RCVBUF) + pool-capacity alignment | resource policy | ~10× off native residency |
| 7 | §2 F-alloc-2: UDP receive allocation removal (reusable SAEA + SocketAddress compare) | local refactor | UDP's last per-packet allocation |
| 8 | §3.3: UDP response micro-batch (`08-30-batched-ioctls` Phase 2 trigger re-evaluation) | medium | DNS-dense throughput |
| 9 | §5 R3/R4/R5: SYN lock-chain + sweep-predicate burst + off-pump failure tail | medium | tail-latency spikes under churn |
| 10 | §5 R8/R9: dead-write removal (or sweep wiring), candidatePorts byte array, cooldown scratch, address-cache pinning | small cleanups | correctness-adjacent hygiene |
| 11 | §4.2: shared listener + accept demultiplex | architectural | capacity ceiling up an order of magnitude |
| 12 | §4.4: FlowTable A4 rebuild (canonical key, shards, slab, bucketed sweep) | architectural, design ready | −17 MB pre-allocation, lock-free reads, O(expired) sweeps |
| 13 | Relay read-coalescing for small-chunk traffic (speculative second fill under a watermark) | medium, needs latency care | small-packet relay throughput (§7 anchor) |

## 9. Explicitly not recommended

- Merging UDP relay sockets across flows — protocol-forced dead end (reply header carries
  only the peer address; same-remote flows cannot be demultiplexed). Recorded twice now.
- Timing-wheel/fingerprint migrations of tombstone & cooldown structures at current
  capacities (≤16k) — amortized cost is negligible; revisit at ≥10⁵.
- Flow-hash sharding of the pump thread model, and the WFP kernel-redirect endgame (F7) —
  both are ceiling levers for a "10 Gbps+ at low CPU" target that has not been set; the
  per-adapter read/write sharing of one `NdisNativeCallGate` (reads block same-adapter
  sends) is the related item to verify on Windows before any such work.
