# Design — startup stale-handle recovery in `LayeredCaptureRunner`

## 1. Fault path recap (what ships today)

```
Enumerate() → [storm guard / old-generation teardown] → InstallGenerationAsync
  → ICaptureGeneration.RunAsync → TransactionalCaptureRuntime.StartCoreAsync
    → SnapshotAsync → NdisApiDriver.GetAdapterMode ── Win32Exception(87) ✗
```

The fault propagates: `StartCoreAsync` (finally runs cleanup; restore list is still empty, so
nothing is restored — correct) → `RuntimeCaptureGeneration.RunAsync` task faults → runner observes
it either in the main loop's `WhenAny` → `ObserveGenerationExitAsync` (rethrow) or inside
`ProcessRefreshDemandAsync` → `StopGenerationAsync` (rethrow) → `Program.cs` logs
`Runtime failure` and exits 3. The 09-07 R3 degrade path never engages: it lives in the pumps,
which have not started yet.

Why 87 here is *always* stale handles: during the startup phase the only native adapter-associated
calls are the mode snapshot/apply (`GetAdapterMode`/`SetAdapterMode` per scope adapter). Pumps
start strictly after. Pump-path 87s degrade (R3), and every restore-path exception is swallowed.
So `Win32Exception(87) && !pumpsStarted` is a precise signature of "the list rebuilt after our
enumeration".

## 2. Phase latch

- `TransactionalCaptureRuntime` gains an internal latch set (under its gate) in `StartCoreAsync`
  immediately before `await _capture.RunAsync(...)`, exposed as `bool ReachedPumpRun`.
  Read-after-task-completion ordering makes the latch race-free: if the task completed, the
  latch's write (or its absence) is already visible.
- `ICaptureGeneration` gains `bool ReachedPumpRun { get; }`; `RuntimeCaptureGeneration` delegates.
  Test fakes implement it as a settable knob. This is the only contract change; it keeps
  `TransactionalCaptureRuntime` free of NDIS error-code knowledge (classification stays in the
  runner, next to the existing `AdapterListRebuiltNativeError = 87`).

## 3. Runner changes (`LayeredCaptureRunner`)

New state (all mutated only on the runner's main loop, like `_generation`/`_runTask` today):

- `int _startupFaultStreak`, `bool _forceRebuild` — see below.
- `internal const int MaxConsecutiveStartupRecoveries = 3` — worst case ~3 storm-guard intervals
  plus teardown time of guaranteed-dead generations; small enough to fail fast on a genuine
  defect, large enough to ride out a churn burst (each retry re-enumerates, so a settling system
  recovers on attempt 2-3).

Classification helper (pure):

```csharp
private static bool IsRecoverableStartupFault(ICaptureGeneration generation, Exception fault) =>
    fault is Win32Exception { NativeErrorCode: AdapterListRebuiltNativeError }
    && !generation.ReachedPumpRun;
```

Two observation points absorb the fault (they are the only places a generation task is awaited):

1. **`ObserveGenerationExitAsync`** (fault won, no demand pending): classify the caught fault;
   if recoverable → `RecoverFromStartupFaultAsync`: streak++, warn event, dispose the dead
   generation (its cleanup tail already ran; `StopAsync` on `Closed` is a no-op), clear
   `_generation`/`_runTask`, set `_forceRebuild = true`, `_demandGate.Signal()`, return to the
   loop. Non-recoverable → today's rethrow.
2. **`StopGenerationAsync`** (demand won the `WhenAny` race, generation faulted meanwhile):
   the existing `catch (Exception) { fault = ... }` branch classifies before the rethrow;
   recoverable → absorb (streak++, warn, `_forceRebuild = true`; the demand processing in flight
   performs the install right after, so no extra signal), else rethrow as today.

Cap enforcement (both paths): if streak would exceed the limit, log a final error event and
rethrow the original fault fail-closed.

Streak reset: whenever the runner finishes a generation whose `ReachedPumpRun` is true (natural
exit observation, refresh stop, teardown) — one generation that actually intercepted proves the
system recovered, so a much-later isolated race starts from zero.

`ProcessRefreshDemandAsync`: consumes `_forceRebuild` at entry. When set, the empty-diff
early-return does not apply — the runner must install a replacement generation even when the
fresh enumeration matches the (dead) current scope; `LogRefresh` records `forced=true` so the
no-op-skip invariant of 09-07 stays honest for live generations. Non-empty diff behaves exactly
as today (forced or not). The flag clears on consumption; a change-source demand arriving with no
dead generation is unaffected.

## 4. Flow after the fix

```
gen N snapshot 87 → fault observed → classified (87, !ReachedPumpRun)
  → warn generation.startup-fault attempt=k/limit → dispose dead gen N
  → _forceRebuild, signal demand
loop → ProcessRefreshDemand (guard wait, consume force)
  → enumerate → diff (possibly empty→forced) → StopGeneration(no-op, gen null)
  → InstallGenerationAsync(gen N+1, fresh handles)
  → gen N+1 reaches pump run → streak = 0
```

## 5. Edge cases

- **Pointer-reuse empty diff**: after a rebuild the fresh handles can numerically equal the old
  ones (pool reuse) → diff empty → force flag installs anyway. Correct: handles are compared by
  value, the dead generation is the truth that matters.
- **Demand + fault simultaneous**: covered by the `StopGenerationAsync` absorb (case 2); the
  in-flight demand install lands, force flag only guards the empty-diff skip.
- **Recovery storm (churn burst)**: each recovery is ≥ storm-guard interval apart; after
  `MaxConsecutiveStartupRecoveries` the original fault exits fail-closed — bounded by
  construction, mirroring the 09-07 anti-refresh-loop argument.
- **Ctrl+C during recovery**: cancellation exits the loop before the forced demand is processed
  (`if (cancellationToken.IsCancellationRequested) break;` stands); `TeardownAsync` →
  `StopGenerationAsync` on an already-drained generation is a no-op.
- **All scope adapters gone at the forced refresh**: the existing pause path applies (scope
  empty, warn, wait for change event) — unchanged semantics.
- **`InstallGenerationAsync` throwing synchronously** (factory failure): not classified (not a
  Win32Exception from the mode phase in the general case) → propagates fail-closed, as today.

## 6. Telemetry

- New structured warn event `generation.startup-fault` with fields `nativeError` (the code, 87)
  and `attempt` (`{streak}/{limit}`), emitted at each absorption; a final error-level variant
  precedes the fail-closed exit when the cap is exceeded. Follows `RuntimeLogField` conventions
  (no handle values printed — handles are not identity per the NDISAPI spec).
- The forced rebuild remains visible through the existing `adapter.refresh` event
  (`forced=true` field added only on forced installs).

## 7. Tests (`LayeredCaptureRunnerRefreshTests` + `CaptureRunnerFakes`)

Fake generation knob: `FaultAtStartupWith` (exception thrown before `ReachedPumpRun = true`) and
the existing healthy-script machinery. Cases map 1:1 to AC1-AC6:

1. snapshot-87 → recovery → next generation runs (AC1)
2. snapshot-87 with unchanged enumeration → forced install, log contains `forced=true` (AC2)
3. 87 faults beyond limit → `RunAsync` throws the original `Win32Exception` (AC3)
4. startup fault 6/other-code → rethrow; 87 after `ReachedPumpRun` → rethrow (AC4)
5. demand-source signal races the fault (fake source fires before the loop observes) → live
   generation after processing (AC5)
6. recovered generation runs; later isolated 87 recovered again (AC6)
7. `TransactionalCaptureRuntime.ReachedPumpRun` latch unit tests in `CaptureLifecycleTests`
   (latch false on snapshot fault; true once the loop run starts)

## 8. Compatibility / rollback

Additive-only surface changes (`ReachedPumpRun` on the runtime + generation interface; new
internal consts). No config, no ABI, no driver interaction changes. Rollback = revert the commit.
