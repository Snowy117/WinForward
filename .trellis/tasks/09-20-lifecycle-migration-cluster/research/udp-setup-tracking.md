# Research: UDP setup tracking, TCP inflight-setup gate, and the `IUdpSessionSlotHost` seam

- **Query**: C3 questions 1–3 — how UDP setup is started/tracked, the exact shape/callers of `TcpRedirectSessionStore.EnterSetup/ExitSetup`, and `UdpProxyCoordinator.UdpSessionSlot` + the five seam operations
- **Scope**: internal
- **Date**: 2026-09-21

## Q1 — How UDP setup work is started and tracked today

`UdpSessionSetup.CreateSessionAsync` (`UdpSessionSetup.cs:62-119`) is **not awaited inline on the packet
path**. It runs as a work item on the injected `ISetupExecutor`, enqueued by the coordinator's cold helper.

Call chain (verified):

1. `UdpProxyCoordinator.TrySendSpanAsync` (`UdpProxyCoordinator.Send.cs:20-73`) — warm, non-async. On a
   new flow, under `_gate`, it creates a slot and calls `ScheduleSessionSetup(...)` `Send.cs:52`.
2. `UdpProxyCoordinator.ScheduleSessionSetup` (`UdpProxyCoordinator.cs:138-157`):
   - `var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);` `:140`
   - `var item = _setupExecutor.RentItem(_setupHandler);` `:141` (`_setupHandler = RunSessionSetupAsync`, ctor `:79`)
   - fills `item._flow/_server/_udp._flowGeneration/_udp._clientMac/_udp._slot`, `item._cancellationToken = _shutdown.Token` `:143-148`
   - `if (!_setupExecutor.TryEnqueue(item)) { completion.TrySetCanceled(...); return false; }` `:149-153`
   - `slot.Completion = completion.Task; return true;` `:155-156`
3. `RunSessionSetupAsync` (`:159-160`) is the executor's handler:
   `=> _setup.CreateSessionAsync(item._flow, item._server!, item._udp._flowGeneration, item._udp._clientMac, item._udp._slot!, item._cancellationToken)`.
4. The completion TCS is never completed by `RunSessionSetupAsync` itself; instead `slot.Completion` is the
   setup task's own task? **No** — `slot.Completion` is the `completion.Task` (`:155`), and
   `completion.TrySetCanceled` is only invoked on enqueue failure (`:151`). The executor
   (`SetupExecutor`) owns completing `item._completion` (the lease's completion is the `SetupWorkItem._completion`
   field, set at `:142` to the same `completion`). So `slot.Completion` completes when the executor
   finishes the item (result/fault/cancel). *This detail matters: C3 must keep the join semantics of
   `slot.Completion` if it replaces the tracking.*

**What tracks in-flight UDP setups:** there is **no dedicated in-flight counter** for UDP setups
(unlike the TCP store's `_inflightSetups`). Tracking is:
- `slot.Completion` (`UdpProxyCoordinator.cs:155`), awaited per slot by `DisposeCoreAsync` at `:278`;
- the `_setupLimiter` `SemaphoreSlim(8)` (`UdpSessionSetup.cs:39`), awaited `:70`, released `finally:117`;
- the `ISetupExecutor`'s own ring (`SetupExecutor`, `src/WinForward.Runtime/SetupExecutor.cs`) and its
  `Dispose` join, called by composition (not by the coordinator — see the comment `UdpProxyCoordinator.cs:298-300`).

**Coordinator call site to cite:** `UdpProxyCoordinator.cs:52` (`ScheduleSessionSetup` call) →
`:138-157` (definition) → `:159-160` (`RunSessionSetupAsync`) → `UdpSessionSetup.cs:62`.

**Setup-body start/attach:** inside `CreateSessionAsync`: `host.AttachSession(slot, session)` `:99`, then
`session.Start(host.RemoveReceiveFailedSessionAsync)` `:100` (starts the receive-loop task), then
`await FlushSetupQueueAsync(...)` `:102`. `session.Start` assigns `_receiveLoop` (`UdpProxySession.cs:127`).

## Q2 — `TcpRedirectSessionStore._inflightSetups` / `_setupsDrained` / `EnterSetup` / `ExitSetup`

Exact shape:

```csharp
// TcpRedirectSessionStore.cs:33-36
private TaskCompletionSource _setupsDrained = CompletedSource();
private Task? _disposeTask;
private int _inflightSetups;
private bool _disposed;
```
```csharp
// :58-73
public void EnterSetup()
{
    lock (_gate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inflightSetups++ == 0) _setupsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public void ExitSetup()
{
    lock (_gate)
    {
        if (--_inflightSetups == 0) _setupsDrained.TrySetResult();
    }
}
```
`CompletedSource()` `:328-333` returns an already-completed TCS, disposing `SetResult` on it would be a
no-op; the counter's 0→1 transition replaces it with a fresh uncompleted one.

**Every caller (verified by `rg`, `src` + `tests` + `benchmarks`):**
- `EnterSetup` — only `TcpProxyCoordinator.cs:261`, inside `SetupPendingAsync`, wrapped in
  `try { _store.EnterSetup(); } catch (ObjectDisposedException) { …; return; }` (`:259-269`).
- `ExitSetup` — only `TcpProxyCoordinator.cs:282`, in the `finally` of `SetupPendingAsync` (`:280-283`).
- `_setupsDrained` read — only `TcpRedirectSessionStore.cs:178` (`DisposeCoreAsync`).
- `_inflightSetups` accesses — only `TcpRedirectSessionStore.cs:63,71`.

**Caller contract that migration must preserve:** `EnterSetup` is a *synchronous admission that throws
`ObjectDisposedException`* when the store is disposed; `SetupPendingAsync` relies on that throw to detect
"disposal began between the pump-side retain and this task's start" and to unwind **without** arming a
cooldown (`TcpProxyCoordinator.cs:263-269`). A replacement built on `QuiescenceScope.TryEnter` returning
`false` (D2) changes that control flow — the coordinator must map `false` back to the same
"complete-entry, no-cooldown, return" behavior. Also the comment at `:274-277` requires the entry removal
and cooldown write to complete **before** `ExitSetup` unblocks the store's dispose drain. A scope lease's
`Dispose()` (≈ `ExitSetup`) runs in the same `finally`, so the ordering can be preserved, but the
`_pendingSyn.Complete` call at `:278` must stay before the lease release.

The surrounding flow: `TcpProxyCoordinator.LaunchSetup` sets `item._cancellationToken = _store.ShutdownToken`
(`:232`) and `_pendingSyn.AttachSetup(entry.SetupCompletionSource.Task)` (`:241`); the store's
`ShutdownToken` is the shutdown signal for the setup pipeline (`RunSetupPipelineAsync` `:295,300,308,325`).

## Q3 — `UdpProxyCoordinator.UdpSessionSlot` and the five `IUdpSessionSlotHost` operations

### `UdpSessionSlot` — full definition (`UdpProxyCoordinator.cs:492-498`)
```csharp
internal sealed class UdpSessionSlot
{
    public Task Completion = Task.CompletedTask;
    public readonly BoundedSetupQueue SetupQueue = new(SetupQueueMaximumPackets, SetupQueueMaximumBytes);
    public UdpProxySession? Session;
    public bool Ready;
}
```
- `SetupQueueMaximumPackets = 32`, `SetupQueueMaximumBytes = 32_768` (`:11-12`).
- `Completion` is set by `ScheduleSessionSetup` `:155`.
- `Ready` is set under `_gate` in `DequeueForFlush` `:371` (queue drained) — the transition to inline sends.

### The seam — `IUdpSessionSlotHost` (`IUdpSessionSlotHost.cs:13-29`)
Exactly five operations, each taking `_gate` internally in the coordinator implementation:

| Op | Interface | Coordinator impl | Behavior |
|---|---|---|---|
| `AttachSession` | `:16` | `UdpProxyCoordinator.cs:335-338` | `lock (_gate) slot.Session = session;` — session becomes visible to dispatch. |
| `RefreshSetupStamps` | `:19` | `:345-353` | Under gate: only if the flow still maps to this exact slot (`ReferenceEquals`), calls `slot.SetupQueue.RefreshEnqueuedStamps(_timeProvider.GetUtcNow())`; returns count else 0. |
| `DequeueForFlush` | `:22` | `:362-380` | Under gate: not-owner → `FlushStep.NotOwner`; else `TryDequeue` → `_budget.Credit(length)` and return `Dequeued`; queue empty → `slot.Ready = true` and return `QueueEmpty`. |
| `RemoveSlotAsync` | `:25` | `:429-458` | Under gate: remove only if exact slot; release association; drain setup queue crediting budget; arm cooldown iff `reason == SetupFailure && !_shutdown.IsCancellationRequested`. Outside gate: `await session.DisposeAsync()`. Returns `owned`. |
| `RemoveReceiveFailedSessionAsync` | `:28` | `:460-471` | Registers `RemoveReceiveFailedSessionCoreAsync(session)` in `_inFlightTeardowns` (pruning completed entries) and returns the task. Core `:473-483`: find slot by flow+session under gate, then `await _slotHost.RemoveSlotAsync(..., UdpTeardownReason.Fault)`. |

`UdpSessionSetup` is constructed with the host (`_slotHost = this` ctor `:80`; `_setup = new UdpSessionSetup(..., _slotHost)` `:81-89`)
and calls only these five methods plus `host.RemoveReceiveFailedSessionAsync` at `:100`.

**`UdpTeardownReason`** (`UdpTeardownReason.cs:9-22`): `SetupFailure`, `Expiry`, `Fault`, `Shutdown`; only
`SetupFailure` arms a cooldown.

## Caveats / Not Found

- `SetupExecutor` internals were not read in this pass; the claim that it completes `item._completion` and
  owns `Dispose` is inferred from `UdpProxyCoordinator.cs:141-155` and the design/spec (`hot-path.md`
  §"Native pool family…" lists `SetupExecutor.StartPendingSetup/LaunchSetup` as replacing per-flow `Task.Run`).
- `BoundedSetupQueue` internals (`src/WinForward.Core/BoundedSetupQueue.cs`) were not read here.
