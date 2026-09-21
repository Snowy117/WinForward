# Research: owner lifetime inventory (C4 scope)

- **Query**: Migrate the remaining lifecycle owners to `QuiescenceScope` — exact lifetime handles, start/observe/dispose edges, guards, CTS ownership, and nesting for each C4 owner.
- **Scope**: internal (read-only; all anchors verified against the working tree at HEAD `ce70098`, 2026-09-21).
- **Method**: files read in full; `rg -uu -p` for cross-references.

Effective-line counts use `rg -v -c '^\s*(//|/\*|\*|$)'` (non-blank, non-comment lines) per `directory-structure.md:57` (cap ≤ 400).

| File | Effective lines | Over cap? |
|------|----------------:|-----------|
| `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` | **417** | **YES (568 physical)** |
| `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs` | 93 | no |
| `src/WinForward.Runtime/Capture/CaptureLifecycle.cs` | 197 | no |
| `src/WinForward.NdisApi/NdisCapture.cs` | 231 | no |
| `src/WinForward.Runtime/IdleExpirySweeper.cs` | 102 | no |
| `src/WinForward.Runtime/RuntimeHeartbeat.cs` | 200 | no |
| `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs` | 304 | no |
| `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` | 255 | no |
| `src/WinForward.Runtime/QuiescenceScope.cs` (primitive, for reference) | 139 | no |

> **Cap finding**: `LayeredCaptureRunner.cs` is already at **417 effective lines**, 17 over the 400 cap in `directory-structure.md:57`. Any C4 edit there must net-reduce lines (removing the two spawns and hoisting delegates is the intended direction), not add.

---

## 1. `LayeredCaptureRunner`

`src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` — `public sealed class LayeredCaptureRunner` at `:32`. **It implements neither `IAsyncDisposable` nor `IDisposable`** (verified `rg 'IAsyncDisposable|IDisposable'` → no hit); its quiescence point is the Task returned by `RunAsync`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_started` | `int` | `:58` | one-shot `RunAsync` claim |
| `_generation` | `ICaptureGeneration?` | `:60` | current generation (the `TransactionalCaptureRuntime` wrapper) |
| `_refreshCancellation` | `CancellationTokenSource?` | `:61` | per-generation refresh token (cancels the generation) |
| `_generationCancellation` | `CancellationTokenSource?` | `:62` | per-generation linked token (`cancellationToken` + `_refreshCancellation`) |
| `_runTask` | `Task?` | `:63` | the generation's `RunAsync` task |
| `_currentScope` | `IReadOnlyList<AdapterEnumerationItem>` | `:64` | last installed scope |
| `_startupFaultStreak` | `int` | `:66` | recovery budget |
| `_forceRebuild` | `bool` | `:67` | forced-refresh flag |
| `_demandGate` | `RefreshDemandGate` (nested, `:536`) | `:56` | coalescing signal |
| `_pendingDegradedAdapters` | `ConcurrentQueue<string>` | `:57` | degraded-adapter queue |

### Start and local spawns

`RunAsync(CancellationToken)` at `:137`:

1. `Interlocked.Exchange(ref _started, 1)` one-shot guard, `:139`.
2. `using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` — a **local** linked CTS, `:140`.
3. `await using var cancelRegistration = cancellationToken.Register(static state => ((RefreshDemandGate)state!).Signal(), _demandGate)` — `:141-142`.
4. **Spawn A** (`WF0003`, allowlisted): `Task.Factory.StartNew(() => MonitorAsync(monitorCancellation.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()` → local `monitor` (`Task`), `:144-146`.
5. **Spawn B** (`WF0003`, allowlisted): `Task.Run(() => PeriodicRefreshTickAsync(monitorCancellation.Token), monitorCancellation.Token)` → local `periodicTick` (`Task?`), `:148-150`.
6. Initial generation install, `:165`; main loop `:167-185`; `finally { await TeardownAsync(monitor, monitorCancellation, periodicTick); }`, `:189`.

`MonitorAsync` (`:217-235`) is a **blocking** loop: `while (_changeSource.WaitOne(cancellationToken)) _demandGate.Signal();` (`:223`). Its class comment at `:221-222` states the loop "owns its dedicated (LongRunning) thread by design" and the comment at `:143` documents the join-before-dispose contract. `ITcpRedirect`-style `IAdapterListChangeSource.WaitOne` is a blocking native/managed wait → this spawn requires its own OS thread (the reason `LongRunning` is used).

`PeriodicRefreshTickAsync` (`:201-215`) is a `PeriodicTimer` wait — true async, no thread affinity.

### Completion observation

`RunAsync`'s returned `Task` completes only after `TeardownAsync` finishes. The sole production caller is `src/WinForward.Cli/Program.cs:331` (`await runner.RunAsync(shutdown.Token)` inside `RunUntilCancelledAsync`, constructed at `Program.cs:280`).

### Teardown / disposal steps

`TeardownAsync(monitor, monitorCancellation, periodicTick)` at `:454-471`:

1. `try { await StopGenerationAsync() } catch { stopFault = exception }` `:457-458`.
2. `await CancelBestEffortAsync(monitorCancellation)` `:459`.
3. `await monitor` `:460`.
4. `if (periodicTick is not null) await periodicTick` `:461`.
5. `await _disposeDurableAsync(CancellationToken.None)` `:464` (wired to `bundle.DisposeAsync()` at `Program.cs:291`).
6. rethrow `stopFault` `:470`.

`StopGenerationAsync` (`:342-370`): clears `_generation`/`_runTask` `:346-347`, `await CancelBestEffortAsync(_refreshCancellation)` `:349`, `await runTask` `:352` with `OperationCanceledException` swallow `:353-356` and startup-fault classification `:357-361`, `await generation.DisposeAsync()` `:364`, disposes `_generationCancellation` `:365` and `_refreshCancellation` `:367`, rethrows `fault` `:369`.

`InstallGenerationAsync` (`:312-335`): creates `refreshCancellation = new CancellationTokenSource()` `:315`, `generationCancellation = CreateLinkedTokenSource(cancellationToken, refreshCancellation.Token)` `:316`, `_runTask = generation.RunAsync(generationCancellation.Token)` `:319`, publishes `_refreshCancellation`/`_generationCancellation`/`_currentScope` `:328-331`.

`RecoverFromStartupFaultAsync` (`:428-441`) repeats the release sequence: cancel `_refreshCancellation` `:433`, `generation.DisposeAsync()` `:434`, dispose both CTSes `:435-438`.

### Disposal guard

`_started` is a one-shot **RunAsync** claim; there is **no disposal guard** and no dispose method. `_refreshCancellation`/`_generationCancellation` are nulled after disposal and `CancelBestEffortAsync` (`:444-452`) swallows `ObjectDisposedException` to tolerate a disposed CTS.

### CTS ownership and token reads

- `monitorCancellation`: local to `RunAsync`, linked to the caller token; token read at `PeriodicRefreshTickAsync` `:206` and `MonitorAsync` `:223`.
- `_refreshCancellation`: per generation; token is *not* read directly — it is a link source for `_generationCancellation` `:316`, and is cancelled/disposed.
- `_generationCancellation`: per generation; token read at `generation.RunAsync(...)` `:319`.

### Nesting / ownership edges

`LayeredCaptureRunner` → (`generationFactory.Create(scope)` at `:314`) `NdisCaptureGenerationFactory` (`NdisCaptureGeneration.cs:69`, `Create` `:97`) → `new TransactionalCaptureRuntime(...)` (`NdisCaptureGeneration.cs:106`) wrapped in `RuntimeCaptureGeneration` (`:125`; `RunAsync` → `_runtime.StartAsync` `:137-140`; `DisposeAsync` → `_runtime.DisposeAsync()` `:150`). The runner awaits `generation.DisposeAsync()` at `:364`/`:434`.

---

## 2. `MultiAdapterCaptureLoop`

`src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs` — `public sealed class MultiAdapterCaptureLoop : IPacketCaptureLoop` at `:19`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_pumps` | `NdisCapturePump[]` | `:21` | one pump per adapter |
| `_onAdapterDegraded` | `Func<WindowsAdapter, int, ValueTask>?` | `:22` | degradation notification callback |
| `_degradedAdapterCount` | `long` | `:23` | telemetry counter |

**No `_runTask`, no CTS, no `_disposed` flag, no disposal guard.**

### Start and completion

`RunAsync(CancellationToken)` `:63-76`: `using var linked = CreateLinkedTokenSource(cancellationToken)` `:65`; `var tasks = _pumps.Select(pump => RunPumpAsync(pump, linked)).ToArray()` `:66`; `await Task.WhenAll(tasks)` `:69`; on fault `await linked.CancelAsync()` then rethrow `:71-75`. `RunPumpAsync` (`:86-101`) awaits `pump.RunAsync(linked.Token)`.

### The `WF0001` site and its invocation thread

`_ = ForwardDegradationAsync(_onAdapterDegraded, adapter, nativeError);` at `:110`, inside `OnPumpDegraded` (`:103-111`). `ForwardDegradationAsync` (`:113-124`) awaits the callback and swallows every exception with `GC.KeepAlive(exception)` `:119-123`.

`OnPumpDegraded` is reached from `NdisCapturePump.Degrade` (`NdisCapture.cs:393-398`, `_onDegraded?.Invoke(nativeError)` `:397`) — i.e. on the **pump's dedicated thread** (`NdisCapture.cs:169`). `NdisCapturePump.Degrade` is synchronous and cannot await; the callback `_onAdapterDegraded` returns `ValueTask` and is therefore discarded.

### Dispose today

`DisposeAsync` `:78-84`: sequentially `await pump.DisposeAsync()` for each pump; no single-flight claim (the pumps themselves are idempotent). `_onAdapterDegraded` is **not** awaited; a forwarding callback in flight when `DisposeAsync` returns is unobserved — exactly the gap the `= _` hides.

### Nesting / ownership edges

Constructed by `NdisCaptureGenerationFactory.Create` (`NdisCaptureGeneration.cs:100-106`) and assigned as the `_capture` of `new TransactionalCaptureRuntime(...)` (`:106`). The callback wiring is a closure `(adapter, nativeError) => HandleDegradedAsync(runtime!, adapter, nativeError)` (`:101-102`), where `HandleDegradedAsync` (`:110-121`) awaits `runtime.MarkAdapterDegradedAsync(...)` `:116` and `_onAdapterDegraded(...)` `:119` — i.e. the callback is an **async** `ValueTask` returning body, not a synchronous action. The outer wiring `Program.cs:216-222` calls `runnerRef!.SignalDegraded(...)` and returns `ValueTask.CompletedTask`.

Disposed by `TransactionalCaptureRuntime.DisposeCaptureAsync` (`CaptureLifecycle.cs:244-245`) via `CleanupCoreAsync` (`:213`).

---

## 3. `CaptureLifecycle` (`TransactionalCaptureRuntime`)

`src/WinForward.Runtime/Capture/CaptureLifecycle.cs` — `public sealed class TransactionalCaptureRuntime : IAsyncDisposable` at `:28`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_shutdown` | `CancellationTokenSource` | `:32` | owned shutdown CTS |
| `_applied` | `List<AdapterModeSnapshot>` | `:33` | applied modes |
| `_gate` | `Lock` | `:34` | state/cleanup gate |
| `_state` | `CaptureRuntimeState` | `:35` | Created/Prepared/…/Closed |
| `_runTask` | `Task?` | `:36` | `StartCoreAsync` task |
| `_cleanupTask` | `Task?` | `:37` | single-flight cleanup |
| `_captureDisposed` | `int` | `:38` | one-shot capture dispose |
| `_reachedPumpRun` | `bool` | `:39` | startup-fault classifier latch |

### Start

`StartAsync` `:73-82` under `_gate`: state `Created→Prepared`, `_runTask = StartCoreAsync(cancellationToken)` `:79`, returns `new ValueTask(_runTask)` `:80`.
`StartCoreAsync` `:84-106`: `using var linkedCancellation = CreateLinkedTokenSource(cancellationToken, _shutdown.Token)` `:86` (the only `_shutdown.Token` read, `:86`); snapshots/apply modes; latches `_reachedPumpRun = true` under `_gate` `:98`; `await _capture.RunAsync(runtimeCancellation)` `:99`; `finally { SetStoppingState(); await CleanupAsync(); }` `:101-105`.

### Completion observation

`StartAsync`'s returned Task *is* `_runTask`; a caller awaiting it observes the whole start+run. `StopAsync` (`:108-132`) awaits it too.

### Dispose today, step by step

`DisposeAsync() => StopAsync()` `:134`. `StopAsync` `:108-132`:

1. under `_gate`: if `Closed` return `:114`; if not `Stopping`, set `Stopping` and `cancelShutdown = true` `:115-119`; `runTask = _runTask` `:120`.
2. `if (cancelShutdown) await _shutdown.CancelAsync()` `:123`.
3. `if (runTask is not null) { try { await runTask } catch (OperationCanceledException) {} return; }` `:124-129`.
4. else `await CleanupAsync()` `:131`.

`CleanupAsync` `:200-207`: `_cleanupTask ??= CleanupCoreAsync()` under `_gate` (single-flight), returns `_cleanupTask`.
`CleanupCoreAsync` `:209-220`: `DisposeCaptureAsync()`, then `RestoreBestEffortAsync()`, then `SetClosedState()`.
`RestoreBestEffortAsync` `:177-198`: snapshots `_applied`, restores each in reverse with a `#pragma warning disable RCS1075` catch-all `:188-193`, `_applied.Clear()`, `await _modes.DisposeAsync()` `:196`, **`_shutdown.Dispose()` `:197`**.
`DisposeCaptureAsync` `:244-245`: `Interlocked.Exchange(ref _captureDisposed, 1) == 0 ? _capture.DisposeAsync() : ValueTask.CompletedTask`.

### Disposal guard

Three layers: `_cleanupTask ??=` single-flight under `_gate` (`:204`), `_captureDisposed` `Interlocked.Exchange` (`:245`), and `_state` transitions under `_gate`. `_shutdown.Dispose()` runs at `:197` inside `RestoreBestEffortAsync`, i.e. after `await _runTask` in every normal path.

### CTS ownership and token reads

`_shutdown` is the owner's CTS (`:32`). Its token is read only at `:86` (link source in `StartCoreAsync`) and cancelled at `:123`; disposed at `:197`.

### Nesting / ownership edges

Created by `NdisCaptureGenerationFactory.Create` (`NdisCaptureGeneration.cs:106`); wrapped by `RuntimeCaptureGeneration` (`NdisCaptureGeneration.cs:125-151`, `_runtime` `:127`). `LayeredCaptureRunner` calls `RuntimeCaptureGeneration.RunAsync` (`:319`) and `DisposeAsync` (`:364`/`:434`). `_capture` is the `MultiAdapterCaptureLoop`.

---

## 4. `NdisCapture` (`NdisCapturePump`)

`src/WinForward.NdisApi/NdisCapture.cs` — `public sealed class NdisCapturePump : IAsyncDisposable` at `:96`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_stopped` | `int` | `:118` | stop flag (set by dispose) |
| `_buffersReleased` | `int` | `:119` | buffer-release single-flight |
| `_runStarted` | `int` | `:120` | one-shot run claim |
| `_runCompletion` | `TaskCompletionSource` | `:124` | completed by the loop exit, awaited by dispose |
| `_pumpThread` | `Thread?` | `:130` | dedicated loop thread |

### Start

`RunAsync(CancellationToken)` `:159-177`: `Interlocked.Exchange(ref _runStarted, 1)` one-shot `:161`; allocates `outcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)` `:168`; `var thread = new Thread(() => RunLoop(outcome, cancellationToken)) { IsBackground = true, Name = ... }` `:169-173`; `Volatile.Write(ref _pumpThread, thread)` `:174`; `thread.Start()` `:175`; `return new ValueTask(outcome.Task)` `:176`.

### Completion observation

`RunAsync`'s `ValueTask` completes when `RunLoop` sets `outcome` (`:244-246`). `DisposeAsync` (`:417-423`) parks on `_runCompletion.Task` (`:420`) when a run started. The run thread is exposed for tests as `PumpThread` (`:180`).

### Dispose today

`DisposeAsync` `:417-423`: `Interlocked.Exchange(ref _stopped, 1)` `:419`; if `_runStarted != 0` return `new ValueTask(_runCompletion.Task)` `:420`; else `ReleaseBatchBuffers()` and return `ValueTask.CompletedTask` `:421-422`.
`RunLoop` exit sequence (`:217-246`): final `_onBatchCompleted?.Invoke()` `:222`, `ReleaseBatchBuffers()` `:231`, `_runCompletion.TrySetResult()` `:240`, then completes `outcome` (`TrySetException` / `TrySetCanceled` / `TrySetResult`) `:244-246`.

### Disposal guard

No single-flight field; idempotency comes from `_buffersReleased` (`ReleaseBatchBuffers` `:425-429`, `Interlocked.Exchange` `:427`) and the fact `_runCompletion` completes once. Disposal does not cancel the run — it sets `_stopped` and waits (class doc `:406-415`).

### CTS ownership and token reads

**The pump owns no CTS.** It takes a `CancellationToken` parameter and captures it in the thread closure (`:169`). Token reads: `ShouldContinue` `:255` (`ThrowIfCancellationRequested`), `RunIteration` `:293`, `RetryTransientRead`/`SleepInterruptible` `:361`/`:379` (`WaitHandle.WaitOne`). There is nothing to dispose.

### Thread affinity

The loop **must** stay on its own OS thread: the body is fully synchronous, uses `Thread.Sleep(_pollDelay)` (`:318`) and calls the blocking native `_driver.TryReadPackets(...)` (`:269`); class doc `:59-93` documents the zero-alloc reason and the deliberate blocking of a pending handler (`:320-346`). Production spawns exactly one such thread per adapter.

### Nesting / ownership edges

Constructed by `MultiAdapterCaptureLoop` (`:33-43`). Disposed by `MultiAdapterCaptureLoop.DisposeAsync` `:82`; awaited by `RunPumpAsync` `:90`. Design E6 (`prd.md:181-182`) keeps raw `Thread` legal because this thread is already joined via `new ValueTask(outcome.Task)`.

---

## 5. `IdleExpirySweeper`

`src/WinForward.Runtime/IdleExpirySweeper.cs` — `public sealed class IdleExpirySweeper : IAsyncDisposable` at `:14`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_shutdown` | `CancellationTokenSource` | `:25` | owned shutdown CTS |
| `_loop` | `Task?` | `:29` | the loop task |

### Start and completion

`Start()` `:54-58`: throws if `_loop is not null` `:56`; `_loop = RunAsync()` `:57`.
`RunAsync` `:60-104`: `using var timer = new PeriodicTimer(_interval)` `:62`; loop `while (await timer.WaitForNextTickAsync(_shutdown.Token))` `:65`; sweep body with `OperationCanceledException when (_shutdown.IsCancellationRequested)` `:88-91`; outer `catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)` `:100-103`. Token reads: `_shutdown.Token` `:65`, `_shutdown.IsCancellationRequested` `:88`/`:100`/`:121`.

### Dispose today

`DisposeAsync` `:115-127`: `await _shutdown.CancelAsync()` `:117`; if `_loop is not null` `await _loop` with OCE swallow `:118-125`; `_shutdown.Dispose()` `:126`.
**No disposal guard** — a second `DisposeAsync` calls `_shutdown.CancelAsync()` on a disposed source, which throws `ObjectDisposedException` (not caught).

### CTS ownership / nesting

`_shutdown` owned by this type (`:25`). Constructed in `DurableCaptureBundle.Create` `src/WinForward.Cli/DurableCaptureBundle.cs:237` and started `:238`; disposed first in `DurableCaptureBundle.DisposeCoreAsync` `DurableCaptureBundle.cs:351` (ordering sweeper → UDP → TCP, comment `:20`).

---

## 6. `RuntimeHeartbeat`

`src/WinForward.Runtime/RuntimeHeartbeat.cs` — `public sealed class RuntimeHeartbeat : IAsyncDisposable` at `:46`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_shutdown` | `CancellationTokenSource` | `:57` | owned shutdown CTS |
| `_loop` | `Task?` | `:62` | the loop task |

### Start and completion

`Start()` `:104-112`: throws if `_loop is not null` `:106`; sets `_startedUtc`/`_gcStartupMark`/`_lastCounters` `:107-110`; `_loop = RunAsync()` `:111`.
`RunAsync` `:114-137`: `using var timer = new PeriodicTimer(_interval, _time)` `:116`; `while (await timer.WaitForNextTickAsync(_shutdown.Token))` `:119`; body `Emit()` wrapped in a warn-and-continue catch `:125-130`; outer OCE catch `:133-136`. Token reads: `_shutdown.Token` `:119`, `_shutdown.IsCancellationRequested` `:133`/`:256`.

### Dispose today

`DisposeAsync` `:250-262`: identical shape to the sweeper — `await _shutdown.CancelAsync()` `:252`; await `_loop` with OCE swallow `:253-260`; `_shutdown.Dispose()` `:261`. **No disposal guard** (same double-dispose `ObjectDisposedException` exposure).

### CTS ownership / nesting

`_shutdown` owned (`:57`). Constructed and started by `Program.StartHeartbeat` (`src/WinForward.Cli/Program.cs:302-314`, `heartbeat.Start()` `:312`); consumed `await using var heartbeat = StartHeartbeat(...)` at `Program.cs:225`, so it is disposed at the end of that scope — before `bundle.DisposeAsync()` in the enclosing `finally` (`Program.cs:229-232`, comment `:222-224`).

---

## 7. `Socks5ControlConnection`

`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs` — `public sealed class Socks5ControlConnection : IAsyncDisposable` at `:9`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_socket` | `Socket` | `:29` | control TCP socket (owned via the stream) |
| `_stream` | `NetworkStream` | `:30` | `ownsSocket: true` `:40` |
| `_loopPrevention` | `IDisposable?` | `:31` | registration returned by `onSocketReady` |
| `_attemptCancellation` | `CancellationTokenSource` | `:32` | **owned** per-attempt CTS |
| `_connectCancellation` | `CancellationToken` | `:33` | caller token (value) |
| `_addressCache` | `Socks5AddressCache?` | `:34` | optional cache |
| `_handshakeScratch` | `byte[]` | `:35` | reusable handshake buffer |

### CTS creation / ownership

`ConnectOnceAsync` creates the per-attempt CTS: `attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` `:171`, `attemptCancellation.CancelAfter(timeout)` `:172`, uses its token for `socket.ConnectAsync` `:173`, then transfers it into the constructed connection `:178`. The constructor stores it as `_attemptCancellation` `:42`. It is the connection's owned CTS (D7-relevant).

### Token reads

- `AttemptToken => _attemptCancellation.Token` property `:208`.
- `connection.AttemptToken` used at `:182` (authenticate with the attempt token).
- `RunWithinAttemptAsync` links it: `CreateLinkedTokenSource(AttemptToken, cancellationToken)` `:265` and `:279` — **two `.Token` reads on the owned CTS during a live operation**.
- `_attemptCancellation.IsCancellationRequested` `:295` (safe after `Dispose`: `IsCancellationRequested` does not throw on a disposed CTS; only `.Token` does).

### Dispose today

`DisposeAsync` `:230-247`: `try { await _stream.DisposeAsync() }` `:234`; `finally { try { _loopPrevention?.Dispose() } finally { _attemptCancellation.Dispose() } }` `:240-245`.

### Disposal guard

**None** (no `_disposed`/`Interlocked`/latch — verified by `rg`). `_attemptCancellation.Dispose()` is idempotent; `_stream.DisposeAsync()` is idempotent; `_loopPrevention?.Dispose()` idempotency is the registered object's contract. A concurrent `RunWithinAttemptAsync` reading `_attemptCancellation.Token` (`:265`/`:279`) while `DisposeAsync` disposes it (`:244`) throws `ObjectDisposedException`. `DisposeFailedAttemptAsync` `:387-398` disposes `attemptCancellation` at `:397` when the connection was never constructed.

### Nesting / ownership edges

Owned and disposed by:
- `Socks5UdpTransport` as `_control` — `Socks5UdpTransport.cs:126`, disposed `Socks5UdpTransport.cs:391`;
- `TcpProxyRelay` as `_control` (`IAsyncDisposable`) — disposed in `TcpProxyRelay.DisposeAsync` `TcpProxyRelay.cs:352` (after `_localSocket.Dispose()` and concurrent with the scope drain).
Retrieved at relay setup `TcpProxyRelay.cs:40`/`:51`/`:53`.

---

## 8. `Socks5UdpTransport`

`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` — `public sealed class Socks5UdpTransport : IUdpProxyTransport` at `:102`.

### Lifetime fields/handles

| Name | Type | Declared | Role |
|------|------|---------:|------|
| `_socket` | `Socket` | `:125` | relay UDP socket (non-blocking) |
| `_control` | `Socks5ControlConnection` | `:126` | owned control connection |
| `_selfTrafficToken` | `SelfTrafficRegistry.SelfTrafficToken?` | `:127` | loop-prevention registration |
| `_sendGate` | `SemaphoreSlim(1,1)` | `:128` | serializes the shared send buffer |
| `_sendBuffer` | `byte[]` | `:129` | reusable encode buffer |
| `_relaySocketAddress` | `SocketAddress` | `:130` | cached serialized endpoint |
| `_receiveSenderTemplate` | `IPEndPoint` | `:131` | per-receive sender template |

**No `CancellationTokenSource` of any kind** (verified by `rg 'CancellationTokenSource'` → zero hits in this file). Tokens used are method parameters.

### Tokens read

- `SendSpanAsync` `:230`: `_sendGate.WaitAsync(cancellationToken)` `:236`; sync send `:255`; overlapped fallback token at `:282` (`SendAfterGateAsync`) and `:294` (`SendOverlappedAsync`).
- `ReceiveAsync` `:311`: `_socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken)` `:317`.

### Dispose today

`DisposeAsync` `:375-401`: `_socket.Dispose()` `:379`; `finally { _selfTrafficToken?.Dispose()` `:385`; `finally { await _control.DisposeAsync()` `:391`; `finally { _sendGate.Dispose()` `:397` } }`. Comment `:395-396`: `_sendGate` is disposed last so in-flight senders release the gate from their `finally` as the disposed socket faults their sends.

### Disposal guard

**None** — no `_disposed`/`Interlocked` latch. `DisposeAsync` is not idempotent-safe in the strong sense: `_sendGate.Dispose()` `:397` races a sender that has not yet called `_sendGate.WaitAsync` (`:236`), which would throw `ObjectDisposedException` out of `SendSpanAsync`; and `_socket.Dispose()`/`_control.DisposeAsync()` repeated calls rely on the callees.

### Hot path

**Yes.** `SendSpanAsync`/`ReceiveAsync` are the UDP relay datagram path. `hot-path.md` #3/#7 and the UDP allocation gates apply:
- warm uncontended `SendSpanAsync` is a non-async, zero-alloc sync `SendTo` (`:229-270`; `#pragma warning disable RCS1229` `:229`);
- the contended branch copies with `payload.ToArray()` as a documented cold-path exemption (`:239-243`, `hot-path.md:290-293`);
- `WarmSyncSendAllocatesNoManagedBytes` (`tests/WinForward.Core.Tests/Socks5UdpTransportSendTests.cs:171`) and `SendSpanAsyncWarmPathRunsNoAsyncStateMachine` `:161` pin this.

Adding a per-datagram CTS or an `Interlocked` guard that allocates here would violate `hot-path.md`. An `Interlocked` int flag itself is allocation-free.

### Nesting / ownership edges

Created by `Socks5UdpTransportFactory.CreateAsync` `:98-99` / `Socks5UdpTransport.CreateAsync` `:159-227`; owned by `UdpProxySession._transport` (`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:44`, assigned `:78`). `UdpProxySession` disposes it in `DisposeCoreAsync` `UdpProxySession.cs:217`, before awaiting `_receiveLoop` `:218-231` and before `_scope.DrainAsync()` `:233`. The tokens it receives come from `UdpProxySession`: send token is the caller's parameter (`UdpProxySession.cs:150`, supplied by `UdpProxyCoordinator.Send.cs:88` / `UdpSessionSetup.cs:165`); receive token is `_scope.Token` (`UdpProxySession.cs:239`, passed to `_transport.ReceiveAsync` `:259`).

---

## Cross-owner nesting summary

```
LayeredCaptureRunner.RunAsync (Program.cs:331)
  └─ generation = RuntimeCaptureGeneration (NdisCaptureGeneration.cs:125)
       └─ TransactionalCaptureRuntime (CaptureLifecycle.cs:28)
            ├─ _capture = MultiAdapterCaptureLoop (CaptureLifecycle.cs:31)  → NdisCapturePump[] (NdisCapture.cs:96)
            └─ _modes = NdisAdapterModeController
IdleExpirySweeper (DurableCaptureBundle.cs:237)      ─ disposed DurableCaptureBundle.cs:351
RuntimeHeartbeat (Program.cs:304)                    ─ `await using` Program.cs:225
TcpProxyRelay._control = Socks5ControlConnection     ─ disposed TcpProxyRelay.cs:352
UdpProxySession._transport = Socks5UdpTransport      ─ disposed UdpProxySession.cs:217
  └─ Socks5UdpTransport._control = Socks5ControlConnection ─ disposed Socks5UdpTransport.cs:391
```
