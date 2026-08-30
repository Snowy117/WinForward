# PassAsync call-site audit + S5 wiring notes (task 08-30-batched-ioctls)

> implement.md S5 requires a documented audit that every `NdisPacketActionExecutor.PassAsync`
> caller lives inside the pump's serialized batch-loop chain, since the batching accumulator is
> safe only under that invariant (design D2 / PRD R1).

## Audit result: all callers are inside the pump batch-loop chain

Production callers of `NdisPacketActionExecutor.PassAsync` (verified 2026-08-30, working tree of
this task; line numbers at audit time):

| # | Call site | Chain position |
|---|---|---|
| 1 | `FlowDispatcher.DispatchAsync` warm path (`src/WinForward.Runtime/FlowDispatcher.cs:157`) | the inline `return _executor.PassAsync(...)` for a resolved Pass decision |
| 2 | `FlowDispatcher.DispatchSlowAsync` → `ExecuteDecisionAsync` (`FlowDispatcher.cs:321`) | slow-path Pass execution |
| 3 | `NdisPacketActionExecutor.ProxyAsync` NotRelevant fallback (`NdisPacketActionExecutor.cs:258`) | self-call; a pre-existing connection on a proxy-decided flow passes |

Upward chain (each level's only production callers):

- `DispatchAsync` / `DispatchSlowAsync` / `DispatchNonFlowAsync` are called only by
  `CapturePacketProcessor.ProcessAsync` (`CapturePacketProcessor.cs:71,76`).
- `CapturePacketProcessor.ProcessAsync` is called only by the `MultiAdapterCaptureLoop`
  per-adapter pump closures (`MultiAdapterCaptureLoop.cs:29`) — the handler the pump awaits
  strictly in slot order inside one batch iteration.
- Benchmarks drive `DispatchAsync`/`ProcessAsync` with fake executors
  (`CountingExecutor` in `BenchmarkShared.cs`), never `NdisPacketActionExecutor`, so no
  benchmark appends to the lanes.

Therefore every Pass append happens on the single logical chain of one pump iteration, and the
per-(adapter, direction) lanes are touched only by that adapter's pump — appends and per-adapter
flushes need no lane lock (documented on `TryGetOrAddPendingLane`). The `MultiAdapterCaptureLoop`
runs one pump per adapter concurrently over one shared executor; `FlushPendingPasses(nint)` is
scoped per adapter handle so concurrent pumps never touch each other's lanes (the lane ARRAY is
shared, published via volatile writes under a creation lock).

## Wiring (D3)

- `NdisCapturePump` gains an optional `onBatchCompleted` callback, invoked after the slot loop of
  EVERY iteration (including empty batches, before the poll delay) and once more in the run
  loop's `finally` BEFORE `ReleaseBatchBuffers()` — in-place frames are still valid during the
  exit flush.
- `CapturePacketProcessor` holds the optional `Action<nint>` pass-through
  (`OnBatchCompleted`); `MultiAdapterCaptureLoop` wires each pump with
  `() => onBatchCompleted(adapter.RuntimeHandle)` — the executor stays out of the pump layer.
- Composition (`src/WinForward.Cli/Program.cs`):
  `new CapturePacketProcessor(dispatcher, logger, executor.FlushPendingPasses)`.
- Deviation from design D2's letter, with reason: D2 specifies `FlushPendingPasses()` flushing
  "each key in insertion order". Because one executor serves ALL pumps concurrently, an
  adapter-scoped `FlushPendingPasses(nint adapterHandle)` is required for lane ownership (a
  global flush would let pump A send pump B's mid-iteration frames — memory-safe but it would
  couple pumps and complicate exactly-once reasoning). Per-adapter flush preserves the design's
  ordering contracts: same (adapter, direction) frames in capture order; cross-key order is not
  a contract. With the expected ≤2 in-scope adapters, per-iteration batched calls stay ≤2 per
  direction — the "2 IOCTLs worst case" shape D2 wanted per pump.
- The every-iteration flush invariant is pinned structurally by the pump (unconditional callback)
  and by tests; `DebugAssertNoPendingPasses(nint)` + `PendingPassCount` provide the debug/test
  surface (a start-of-iteration assert cannot live in the shared executor because current-iteration
  pending frames are indistinguishable from stragglers at flush entry).

## Pre-existing test flakiness observed (not caused by this task)

`UdpProxyCoordinatorLifecycleTests.ActiveSessionRetainsRelayAliasAfterItsCreationTimestampExpires`
fails intermittently in isolation on the clean baseline (3 of 6 runs at master 7393869 in a
separate worktree; 2 of 5 at the working tree). It is timing-dependent (polls a cooldown
tombstone transition with frozen fake time). Left untouched — outside this task's scope — but
full-suite runs may report it nondeterministically.
