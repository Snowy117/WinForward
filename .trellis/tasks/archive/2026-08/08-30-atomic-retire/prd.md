# Atomic retire+remove+tombstone and bounded queues

Parent: `08-30-proxy-perf-stability` (backlog item #4). Sources:
research/synthesis-and-backlog.md §3 R2/R3/R4 (baseline e5667af) re-validated
against HEAD 0596a74 in `research/current-state.md` (this task's research dir).

## Goal

Close the three remaining structural stability defects on the TCP teardown path
and UDP setup path:

1. **R2** — a µs–ms window between session retire (store gate) and table alias
   removal (table gate) in which a same-tuple SYN/data is honored against an
   about-to-be-disposed listener, producing client RTO/teardown churn. The
   fast-hardening relay-end RST (landed) makes a landing in this window *more*
   likely: the client can react to the RST while the stale association is still
   resolvable.
2. **R3** — unbounded memory growth: TCP tombstone insertion-order queue never
   drains on refresh/expiry (only at-capacity eviction dequeues); UDP
   `_setupTombstones` dictionary has no capacity bound between 60s sweeps.
3. **R4** — UDP setup aggregate memory has no global bound (16,384 flows × 32KB
   queues = 512 MiB worst case, held for hours when the SOCKS5 server is
   slow/dead) and buffered datagrams have no TTL (flush delivers long-expired
   packets).

## Confirmed facts (research, HEAD 0596a74)

- R2 scope is narrower than the backlog wording: `TryRemove(onRemoved: tombstone
  TryAdd)` already makes table-removal + tombstone-arming one atomic step inside
  the table gate (existed at baseline). The genuine gap is only
  retire (store gate) → `RemoveAssociationFromTable`.
- Proposed fix is feasible and safe at HEAD: `TcpRedirectTable.TryRemove` is
  synchronous and non-blocking (the X1 port-bitmap decrement is an
  Interlocked op); lock order store → table → tombstone verified acyclic
  repo-wide; exactly one call site each for `_table.TryRemove` and
  `_tombstones.TryAdd` (TcpRedirectSessionStore.cs:295).
- Session-less removal paths (`FailAssociationAsync` else-branch,
  `TcpRedirectSetup` disposed-store path) must keep a standalone
  `RemoveAssociationFromTable`.
- Listener/relay/token `DisposeAsync` cannot move under the store gate; only the
  synchronous table+tombstone pair moves.
- R3-TCP: queue order ≈ expiry order stays monotonic (refresh appends a fresh
  tail record with new expiry), so a head-drain predicate
  `(head stale) || (head.ExpiryUtc <= now)` in `RemoveExpired` is order-safe.
  Reference implementation: `TcpResetCooldownTable.TryClaim` (refresh does not
  re-enqueue).
- R3-UDP: `_setupTombstones` writers = `RemoveSlotAsync` only; readers prune
  lazily on touch (`TrySendAsync`) and via the 60s sweep (`PruneExpiredTombstones`).
- R4: `BoundedSetupQueue` (public Core class) is dual-bounded per flow
  (32 pkts / 32KB) but there is no cross-flow budget; no timestamps on queued
  datagrams (a TTL requires an entry-shape change or a coordinator-side
  alternative); setup runs 8-way concurrent (`MaximumConcurrentSetups`),
  `ConnectAttemptTimeout` 30s × 4 attempts; caller implements drop-oldest.
- All affected store/tombstone/UDP-coordinator files are byte-identical to the
  e5667af baseline (only `TcpRedirectTable`/`TcpRedirectAcceptor`/
  `TcpProxyCoordinator`/`TcpProxyRelay` drifted, all analyzed).
- No existing test observes the retire→removal gap directly; a red-to-green
  test is required. Existing tests that pin current semantics and must stay
  green: `TcpProxyCoordinatorLifecycleTests`, `TcpProxyCoordinatorCapacityTests`
  (grace/expiry), `TcpProxyCoordinatorConcurrencyTests` (exactly-once),
  `TcpRelayEndResetTests` (RST-before-teardown), `TcpRedirectTombstoneTableTests`,
  `UdpSetupQueueTests` (FIFO/drop-oldest/1s cooldown/8-way cap),
  `UdpProxyCoordinatorLifecycleTests`.

## Requirements

### REQ1 — Atomic retire+remove+tombstone (R2)

`RetireSessionUnderGate` (store gate) must also perform the table alias removal
and tombstone arming, making session-dict removal + `Phase=Closing` + retire +
table removal + tombstone arming one atomic step for all three callers
(`TearDownSessionAsync`, sweep `RemoveExpiredAsync`, `DisposeCoreAsync`).
Session-less release paths keep working via the standalone
`RemoveAssociationFromTable`. Disposal ordering (listener → self-traffic token →
relay → lifetime CTS, all outside the gate) is unchanged.

### REQ2 — Bound the TCP tombstone insertion-order queue (R3-TCP)

The tombstone queue must not grow monotonically with total TryAdd calls;
refresh/expiry must eventually reclaim queue records. Head-drain in
`RemoveExpired` with the order-safe predicate is the preferred mechanism.

### REQ3 — Bound `_setupTombstones` (R3-UDP)

The UDP setup-tombstone dictionary gains a capacity bound with defined
eviction/pruning behavior, mirroring the TCP bounded-table discipline.
Evidence-resolved design: bound = the coordinator's existing `capacity`
(natural upper bound — one tombstone per distinct flow), eviction = oldest
(timestamped value), checked on write in `RemoveSlotAsync`. A full table
evicts the oldest entry rather than refusing the write (refusal would
degrade the setup-failure cooldown into an immediate-retry storm).

### REQ4 — Global UDP setup memory budget + buffered-datagram TTL (R4)

A cross-flow bound on aggregate setup-queue bytes with defined drop semantics
on exhaustion, and a TTL so flush does not deliver long-expired datagrams.

**User decision (2026-08-30)**: byte-budget + TTL. Global Interlocked byte
budget (default 8 MiB ≈ 250 full queues) checked at enqueue; overflow rejects
the new datagram's enqueue (counted, no failure tombstone); queued datagrams
carry timestamps and flush drops entries older than the TTL (default 5 s —
normal setup completes in <1 s; a datagram queued >5 s has already been
retransmitted or timed out at the application layer). `BoundedSetupQueue`
gains per-entry timestamps (coordinator passes its `_timeProvider`).

## Acceptance criteria

- [ ] AC1: A test demonstrates the retire→removal gap is closed: after retire
      begins (session removed from store), a same-tuple SYN/data can no longer
      resolve to the dying association; it observes tombstone grace-drop
      semantics instead. Exactly-once concurrency tests stay green.
- [ ] AC2: A test demonstrates the tombstone queue drains on refresh+expiry:
      after churn with capacity never reached, queue length returns to ~live
      entry count (not total-TryAdd count). Existing tombstone-table tests stay
      green.
- [ ] AC3: A test demonstrates `_setupTombstones` cannot exceed its bound
      (eviction or prune behavior per design); the 1s cooldown retry test stays
      green.
- [ ] AC4: Tests demonstrate the global setup budget rejects/bounds beyond the
      configured aggregate and credits back on drain/dispose; TTL-expired
      datagrams are dropped at flush, not delivered.
- [ ] AC5: Full suite green, zero-warning build, no allocation-gate benchmark
      regressions on affected paths (dispatch/UDP warm paths untouched).
- [ ] AC6: Cross-cutting invariants preserved: exactly-once pool returns, OCE
      token discipline, batch-slot stability, in-place mutation order, teardown
      single-writer semantics.

## Resolved decisions (recorded for traceability)

- R4 mechanism: global byte budget + per-entry TTL (user-approved, see REQ4).
- R2 reuse-path `Phase == Closing` guards: **not added**. Rationale: after the
  atomic fix, the residual relay-end→teardown window is the synchronous
  sequence `TryInjectClientResetAsync` → `_tearDownSession` inside
  `ObserveRelayCompletionAsync` (µs-scale, no arbitrary interleaving), and a
  Phase check itself has a TOCTOU (phase may flip after the check) so it
  narrows but cannot close a window — cost on the per-packet reuse path with
  no complete guarantee. Revisit only if the red-to-green test cannot be made
  deterministic.
- R3-UDP: bound = coordinator `capacity`, evict-oldest-on-write (see REQ3).

## Out of scope

- Zero-copy data path (X2), batched IOCTLs (X3), UDP endpoint allocation diet
  (X6), jumbo buffer sizing (R5), keepalive/lock-cleanup/ServerGC bundle (#8),
  driver resilience (#10), SOCKS5 setup budget tightening (X9).
- Replacing the insertion-order queue with a linked list unless the head-drain
  proves insufficient.
