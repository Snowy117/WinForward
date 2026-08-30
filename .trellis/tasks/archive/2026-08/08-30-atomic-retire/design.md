# Design: Atomic retire+remove+tombstone and bounded queues

Evidence: `research/current-state.md` (re-validated at HEAD 0596a74; all
affected store/tombstone/UDP files byte-identical to e5667af). File:line
references are as-of 0596a74; the implementer re-verifies before editing.

## D1. Atomic retire+remove+tombstone (R2, TCP)

### Core move

`RetireSessionUnderGate` (`TcpRedirectSessionStore.cs` L217–225) gains the
table alias removal + tombstone arming, executed while still holding the store
gate, immediately after `Phase = Closing` and `session.Retire()`:

```
RetireSessionUnderGate(session):            // under store gate
    _sessions.Remove(...)                   // existing
    Association.Phase = RelayPhase.Closing  // existing
    session.Retire()                        // existing (cancels lifetime CTS)
    detach relay                            // existing
    _table.TryRemove(association,           // NEW — atomic with the above
        removed => _tombstones.TryAdd(removed...))
```

All three retire entry points (`TearDownSessionAsync` L206, sweep
`RemoveExpiredAsync` L121, `DisposeCoreAsync` L183) route through
`RetireSessionUnderGate`, so one change covers all.

Safety (verified in research §R2): `TryRemove` is synchronous and non-blocking
(dictionary removals + address-pair removal + `Interlocked.Decrement` port
bitmap + `onRemoved` tombstone `TryAdd`); lock order **store → table →
tombstone** is acyclic repo-wide (no reverse acquisition exists; the fix
introduces the nesting rather than closing a cycle). The `ReferenceEquals`
guard in `TryRemove` (L280) keeps repeat calls no-ops, including the port-count
double-decrement protection.

### Call-site restructuring

- `ReleaseRetiredAsync` (L227–245) must stop calling
  `RemoveAssociationFromTable` for the retire path — the removal already
  happened under the gate. Split `ReleaseAssociationAsync` (L266–278):
  - retire path → a variant that only disposes (listener → self-traffic
    token), no table call;
  - session-less paths (`FailAssociationAsync` else-branch L263,
    `TcpRedirectSetup` disposed-store path L135) keep the standalone
    `RemoveAssociationFromTable` exactly as today.
- Listener/relay/lifetime-CTS disposal stays outside the gate (async
  `DisposeAsync` cannot run under the gate); the resulting documented
  invariant: *session-dict removal, Phase=Closing, retire, table alias removal,
  and tombstone arming are one atomic step; disposal trails*.

### Residual window (decision: no Phase==Closing reuse guards)

After this fix the only remaining stale-association window is the synchronous
`TryInjectClientResetAsync` → `_tearDownSession` sequence in
`ObserveRelayCompletionAsync` (µs-scale). A Phase check on the reuse path has
its own TOCTOU (phase can flip after the check), so it narrows but cannot close
— rejected (see prd.md resolved decisions). Revisit only if the AC1 test
cannot be made deterministic.

### Tests (AC1)

Red-to-green strategy: drive teardown with a fake listener whose
`DisposeAsync` parks on a `TaskCompletionSource`; while parked (retire
committed, disposal pending — at 0596a74 the association is still resolvable),
dispatch a same-tuple SYN and assert the redirect outcome is a tombstone
drop (no reinjection toward the dying listener). Pre-fix this SYN resolves via
`TryResolveByOriginal` → reinject; post-fix it hits the tombstone grace.
Existing exactly-once tests (`SynClaimIsExactlyOnce`,
`ConcurrentSynBurstOnOneFlowUsesOneListener`,
`ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce`) must stay green.

## D2. TCP tombstone queue drain (R3-TCP)

In `TcpRedirectTombstoneTable.RemoveExpired` (L78–86), after removing expired
dictionary entries, drain the head of `_insertionOrder`:

```
while (_insertionOrder.Count > 0)
{
    var head = _insertionOrder.Peek();
    if (head.ExpiryUtc <= now
        || !ReferenceEquals(_byForward.GetValueOrDefault(head.Forward), head))
        _insertionOrder.Dequeue();   // stale (refreshed/expired) record
    else break;
}
```

Order-safety: refresh appends a fresh tail record with a new expiry, so queue
order ≈ expiry order (monotonic); a live, unexpired head implies nothing
behind it is drainable. Stale records deeper in the queue surface at the head
on later sweeps — total queue length converges to ~live entry count after
grace-period churn.

Add `internal int QueueCountForDiagnostics` (or equivalent) so AC2 can observe
the queue length without reflection. `TryAdd` refresh semantics (append new
tail) unchanged; `TcpResetCooldownTable` is the discipline reference. Existing
`TcpRedirectTombstoneTableTests` stay green (they do not observe queue
internals).

## D3. UDP setup-tombstone bound (R3-UDP)

In `UdpProxyCoordinator.RemoveSlotAsync` (L431–434), before writing
`_setupTombstones[flow] = now + SetupFailureCooldown`: if
`_setupTombstones.Count >= _capacity` (the coordinator's existing session
`capacity` — natural upper bound, one tombstone per distinct flow), evict the
entry with the oldest timestamp (O(n) min-scan; setup failure is not a hot
path). Eviction over refusal: refusing would skip the cooldown and turn a
failing-server storm into an immediate-retry storm. Lazy prune on touch
(`TrySendAsync` L109) and the 60s sweep stay as-is.

Test (AC3): fill `_setupTombstones` beyond capacity via failing flows; assert
count never exceeds capacity and the oldest entry was evicted;
`FailedSetupEntersCooldownAndRetriesAfterOneSecond` stays green.

## D4. Global setup byte budget + datagram TTL (R4, UDP)

### Global budget

- `UdpProxyCoordinator`: `const long SetupQueueGlobalByteBudget = 8 MiB`
  (≈250 full 32 KiB queues; DNS-sized traffic ≈100k datagrams) +
  `long _pendingSetupBytes` (Interlocked).
- Charge at `EnqueueSetupDatagram` (L177–192) before the per-flow `TryEnqueue`:
  `if (Interlocked.Add(ref _pendingSetupBytes, len) > budget) {
  Interlocked.Add(ref _pendingSetupBytes, -len); drop + NoteSetupQueueDrop; }`.
  Transient overshoot from racing adders is acceptable (bounded by in-flight
  adders).
- **Credit exactly-once invariant** (new cross-cutting invariant, spec-worthy):
  every charged byte is credited back exactly once, at whichever point the
  datagram leaves the pending set — flush send (`FlushSetupQueueAsync` L335),
  drop-oldest inside `EnqueueSetupDatagram`'s retry loop, slot drain
  (`RemoveSlotAsync` L429), dispose drain (`DisposeCoreAsync`). Slot ownership
  (ReferenceEquals single-drainer) already serializes dequeue points; the
  implementer audits every `TryDequeue` call site on
  `BoundedSetupQueue` and adds the credit.

### Per-entry TTL

- `BoundedSetupQueue` (`WinForward.Core`, `PacketRuntime.cs` L147–219) is a
  public class: **add overloads, do not break existing signatures**.
  - `TryEnqueue(ReadOnlyMemory<byte> frame, DateTimeOffset enqueuedAt)` —
    entry stores the timestamp alongside the frame (entry struct
    `(ReadOnlyMemory<byte>, DateTimeOffset)`; per-entry ~16 B overhead).
  - `TryDequeue(out ReadOnlyMemory<byte> frame, out DateTimeOffset enqueuedAt)`.
  - Existing parameterless-timestamp overloads forward with a default stamp
    (preserves compile compatibility for any non-coordinator callers).
- `UdpProxyCoordinator` passes `_timeProvider.GetUtcNow()` at enqueue;
  `FlushSetupQueueAsync` drops entries older than
  `SetupQueueDatagramTtl = 5 s` (normal setup <1 s; older datagrams were
  retransmitted or expired at the application layer) — dropped entries credit
  the budget and count via a diagnostics counter.
- Drop-oldest inside `EnqueueSetupDatagram` needs no TTL logic (the flush
  filter handles age); bytes are credited on dequeue regardless of sink.

### Tests (AC4)

- Budget: force budget below N queued flows' total → the (N+1)-th flow's
  enqueue drops with a counter increment; after drain/dispose, budget is fully
  credited (a new flow can enqueue again).
- TTL: fake `TimeProvider`, enqueue, advance >5 s, complete setup → flush
  sends nothing / only fresh entries; dropped-age counter increments.
- Exactly-once credit: full lifecycle (setup success and failure variants)
  ends with `_pendingSetupBytes == 0` (observable via behavior or a
  diagnostics property).
- Existing `UdpSetupQueueTests` (FIFO, drop-oldest, no-bypass, 1s cooldown,
  8-way cap, flash-crowd, dispose-drain) stay green.

## Tradeoffs / alternatives considered

- **LinkedList O(1) removal** for the tombstone queue: rejected unless
  head-drain proves insufficient — head-drain is O(drained) per sweep and the
  queue converges; a LinkedList adds node allocations.
- **Pending-setup flow-count cap**: rejected — the byte budget bounds the
  actual resource (memory) directly; a flow cap duplicates the mechanism with
  a worse proxy (DNS flows are ~70 B, jumbo flows 1.5 KiB).
- **TTL at slot granularity** (drop whole queue if slot waited > TTL): avoids
  the entry-shape change but drops fresh datagrams that arrived late in the
  wait — worse fidelity for one fewer field.
- **Refusing tombstone writes at capacity (UDP)**: rejected — degrades
  cooldown into retry storms (see D3).

## Compatibility / rollout

- No wire-format, ABI, or config-file changes. One public Core class
  (`BoundedSetupQueue`) gains additive overloads only.
- New documented lock order: store gate → table gate → tombstone gate.
- Rollback: D1–D4 are independent; each reverts cleanly (D1 is the only
  ordering-sensitive one — revert restores the old race, not a new failure).
