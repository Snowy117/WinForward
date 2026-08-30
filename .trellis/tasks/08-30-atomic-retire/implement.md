# Implementation Plan: Atomic retire + bounded queues

Read order for the implementer: `implement.jsonl` entries → `prd.md` →
`design.md` → this file. Re-verify every file:line against current source
before editing (references are as-of 0596a74). Baseline gate: build
`-warnaserror` clean, full suite green (451 tests at 0596a74).

## Step 0 — Re-verify research claims

- [ ] Read `TcpRedirectSessionStore.cs` (teardown/retire/release paths),
      `TcpRedirectTable.cs` (TryClaim/TryRemove/onRemoved), 
      `TcpRedirectTombstoneTable.cs`, `UdpProxyCoordinator.cs` (TrySendAsync/
      EnqueueSetupDatagram/FlushSetupQueueAsync/RemoveSlotAsync/DisposeCoreAsync),
      `PacketRuntime.cs` (BoundedSetupQueue). Confirm design assumptions;
      report drift in the final report if any.

## Step 1 — Atomic retire (D1, R2)

- [ ] Move table removal + tombstone arming into `RetireSessionUnderGate`
      (store gate); split `ReleaseAssociationAsync` so the retire path only
      disposes while session-less paths keep the standalone
      `RemoveAssociationFromTable`.
- [ ] Red-to-green test (AC1): parked-listener-dispose teardown + same-tuple
      SYN → assert tombstone drop, no reinjection toward the dying listener.
- [ ] Confirm exactly-once + lifecycle + concurrency test files stay green
      (`TcpProxyCoordinatorLifecycleTests`, `TcpProxyCoordinatorConcurrencyTests`,
      `TcpProxyCoordinatorCapacityTests`, `TcpRelayEndResetTests`).
- [ ] Run: `dotnet test`.

## Step 2 — TCP tombstone queue drain (D2, R3-TCP)

- [ ] Head-drain predicate in `RemoveExpired`; diagnostics property for queue
      length.
- [ ] Test (AC2): churn (repeated refresh + expiry) below capacity → queue
      length converges to ~live entries, not total-TryAdd count.
- [ ] Run: `dotnet test` (tombstone table tests green).

## Step 3 — UDP setup-tombstone bound (D3, R3-UDP)

- [ ] Capacity check + oldest-eviction in `RemoveSlotAsync`'s tombstone write;
      bound = coordinator `capacity`.
- [ ] Test (AC3): over-capacity tombstone writes evict the oldest; 1s-cooldown
      retry test green.
- [ ] Run: `dotnet test`.

## Step 4 — Global setup budget + TTL (D4, R4)

- [ ] `BoundedSetupQueue`: timestamped-entry overloads (additive; existing
      signatures forward with a default stamp).
- [ ] `UdpProxyCoordinator`: 8 MiB Interlocked budget charge/credit at every
      dequeue sink (flush, drop-oldest, slot drain, dispose drain);
      5 s TTL drop in flush; `_timeProvider` stamps at enqueue; diagnostics
      counters for budget-drops and TTL-drops.
- [ ] Tests (AC4): budget exhaustion drop + full credit-back after success/
      failure/dispose lifecycles; TTL expiry at flush with fake TimeProvider.
- [ ] Run: `dotnet test` (`UdpSetupQueueTests`, `UdpProxyCoordinatorLifecycleTests`
      green).

## Step 5 — Spec + benchmark gate

- [ ] Update `.trellis/spec/backend/tcp-local-redirect.md`: documented lock
      order (store → table → tombstone), the new atomic-teardown invariant,
      the tombstone-queue drain discipline.
- [ ] Update `.trellis/spec/backend/udp-relay.md` (if that is the right file):
      setup budget/TTL/tombstone-bound invariants + exactly-once credit rule.
- [ ] Run full gate:

```bash
dotnet build -warnaserror
dotnet test
# allocation-gate spot check (dispatch/UDP warm paths must not regress):
dotnet run --project benchmarks/WinForward.Benchmarks -- --filter '*Dispatch*'
dotnet run --project benchmarks/WinForward.Benchmarks -- --filter '*Udp*'
```

(Verify actual benchmark filter names against
`benchmarks/WinForward.Benchmarks/Program.cs` before running.)

## Review gates

- After Step 1: lock-order + call-site restructuring review (only
  ordering-sensitive step) — confirm no path acquires table→store.
- After Step 4: credit-exactly-once audit — enumerate every `TryDequeue`
  sink and verify one credit per charged byte.
- Steps 2–3: mechanical; single review pass.

## Rollback

Each step is one logical commit, independently revertible. Step 1 revert
restores the pre-existing race (no new failure mode); Step 4 revert removes
additive overloads + budget (no persisted state).

## Follow-up checks before `task.py start`

- [x] Research re-validated at HEAD (this task's `research/current-state.md`)
- [x] Baseline build + suite verified green at 0596a74
- [ ] User approval of the final planning summary
