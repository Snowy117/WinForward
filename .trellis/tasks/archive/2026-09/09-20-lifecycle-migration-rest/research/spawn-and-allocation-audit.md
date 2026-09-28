# Research: spawn site and allocation audit (C4)

- **Query**: For every fire-and-forget / background spawn C4 touches, classify the exact call, the spawned body, its lifetime, thread affinity, closure capture, hot-path applicability, and whether `scope.Run(...)` is a correct replacement.
- **Scope**: internal. Primitive launch semantics verified in `src/WinForward.Runtime/QuiescenceScope.cs`.
- **Method**: files read in full; analyzer probe run (see `allowlist-shrink.md`).

## Primitive launch semantics (load-bearing)

`QuiescenceScope.Run` (`src/WinForward.Runtime/QuiescenceScope.cs:131-145`) calls `RunChildAsync(body, lease, name)` **synchronously on the calling thread**. `RunChildAsync` (`:204-218`) begins `await body(Token).ConfigureAwait(false)` at `:208` and, being `async`, executes `body`'s synchronous prologue inline before returning the first time it yields. **There is no `Task.Run`/ThreadPool hop inside `Run`** — the only async hop is whatever `body` itself yields on.

Consequence: `Run` can only host bodies that return a `Task` promptly at their first `await`. A body that blocks synchronously (never yields) blocks the `Run` caller for the body's whole lifetime.

Also from `Run`'s doc (`:123-130`) and `RunChildAsync`: the returned child `Task` is deliberately not handed out; a child fault is recorded (`RecordFault(exception, name)`) and swallowed, and is observed through `Fault`/`FaultSite`, not through the returned value. `Run` itself allocates a `Func<CancellationToken,Task>` delegate (and a closure/display class if the lambda captures), plus the `RunChildAsync` async state machine box, plus the `Task` it produces — consistent with parent design §3.3 "`Run` is cold-path only".

---

## Site S1 — `LayeredCaptureRunner` monitor spawn

- **Exact call** (`src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:144-146`):
  ```csharp
  var monitor = Task.Factory.StartNew(
      () => MonitorAsync(monitorCancellation.Token),
      CancellationToken.None,
      TaskCreationOptions.LongRunning,
      TaskScheduler.Default).Unwrap();
  ```
  Full statement spans `:144-146`; the `Unwrap()` runs the extra unwrap task.
- **Body** `MonitorAsync` (`:217-235`): **not** `async`; a blocking loop `while (_changeSource.WaitOne(cancellationToken)) _demandGate.Signal();` `:223`, returning `Task.CompletedTask` `:234`. `IAdapterListChangeSource.WaitOne` is a blocking wait (comment `:221-222`: "Blocking wait: this loop owns its dedicated (LongRunning) thread by design").
- **Lifetime**: one instance per `RunAsync` call; runs until the linked `monitorCancellation` fires or the change source is disposed. Joined by `TeardownAsync` at `:460` (`await monitor`) after `CancelBestEffortAsync` (`:459`).
- **Dedicated thread**: **mandatory.** `TaskCreationOptions.LongRunning` is the mechanism; the body never yields, so a ThreadPool thread would be permanently occupied.
- **Closure capture**: the lambda captures `this` (for `_changeSource`/`_demandGate`) and the local `monitorCancellation` → one compiler display class + one `Func<Task>` delegate + the `Task` + the `Unwrap` proxy.
- **Hot-path applicability**: none. This runs once per generation/run, not per packet; `hot-path.md` zero-allocation windows (`HotPathAllocationGateTests`, `Socks5UdpTransport.SendSpanAsync`) do not cover this path.
- **Is `scope.Run` a correct replacement?** **No, not for this body.** `scope.Run(() => MonitorAsync(token), "monitor")` would invoke the blocking `WaitOne` loop inline on the thread calling `scope.Run` (the `RunAsync` caller) and never return — the loop would stall `RunAsync` itself and the lease would never be released. `Run` provides accounting, not a thread. The monitor needs its own OS thread.
- **WF0003 tension**: the allowlist entry C4 must delete covers exactly this `Task.Factory.StartNew`. Any shape that keeps a dedicated thread via `Task.Factory.StartNew`/`Task.Run` still trips `WF0003` (the analyzer is syntactic and fires regardless of enclosing lambda). A raw `new Thread(...)` is not matched by `WF0003` (which matches `Task.Run`/`Task.Factory.StartNew` only), but a raw `Thread` yields no `Task` for the scope to join — it would need a `TaskCompletionSource`-style completion bridge like `NdisCapturePump._runCompletion` (`src/WinForward.NdisApi/NdisCapture.cs:124`, `:417-423`). **This is the central migration tension for S1.** Recorded as a hazard; the repository does not yet contain a scope-tracked dedicated-thread helper.

## Site S2 — `LayeredCaptureRunner` periodic tick spawn

- **Exact call** (`LayeredCaptureRunner.cs:148-150`):
  ```csharp
  var periodicTick = _periodicRefreshInterval > TimeSpan.Zero
      ? Task.Run(() => PeriodicRefreshTickAsync(monitorCancellation.Token), monitorCancellation.Token)
      : null;
  ```
- **Body** `PeriodicRefreshTickAsync` (`:201-215`): `async Task`; `using var timer = new PeriodicTimer(_periodicRefreshInterval, _time)` `:205`; `while (await timer.WaitForNextTickAsync(cancellationToken)) _demandGate.Signal();` `:206-209`; catches `OperationCanceledException` `:211-214`. It yields at the first `WaitForNextTickAsync` → returns a `Task` promptly.
- **Lifetime**: one per `RunAsync` invocation, only when `_periodicRefreshInterval > TimeSpan.Zero`; nulled out at `:151` when disabled. Joined by `TeardownAsync` `:461` (`if (periodicTick is not null) await periodicTick`).
- **Dedicated thread**: **not required** — pure async wait.
- **Closure capture**: lambda captures `this` and local `monitorCancellation` → display class + `Func<Task>` + `Task`.
- **Hot-path applicability**: none (cold, once per run).
- **Is `scope.Run` a correct replacement?** **Yes in shape.** `scope.Run(PeriodicRefreshTickAsync, "periodic-refresh")` is signature-compatible (`Func<CancellationToken,Task>`; method group, no closure) and the body yields immediately, so the inline start is harmless. Caveat: `Run` returns `bool`, and the null-when-disabled branch (`:148`) would disappear — the scope would have to host it unconditionally, and the `_periodicRefreshInterval <= 0` guard is currently enforced before the spawn (verified by `LayeredCaptureRunnerPeriodicRefreshTests.ZeroPeriodicIntervalDisablesTheTickEntirely` / `NegativePeriodicIntervalIsRejected`, see `existing-tests.md`). A `PeriodicTimer` with a non-positive period throws `ArgumentOutOfRangeException`, so the guard must be preserved outside `Run`.

## Site S3 — `MultiAdapterCaptureLoop` degradation forward (`WF0001`)

- **Exact call** (`src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs:110`):
  ```csharp
  _ = ForwardDegradationAsync(_onAdapterDegraded, adapter, nativeError);
  ```
  inside `OnPumpDegraded` (`:103-111`).
- **Body** `ForwardDegradationAsync` (`:113-124`): `private static async ValueTask ForwardDegradationAsync(Func<WindowsAdapter,int,ValueTask>? callback, WindowsAdapter adapter, int nativeError)`; awaits `callback` `:117`, catches every exception and `GC.KeepAlive(exception)` `:119-123`. Returns `ValueTask`.
- **Caller chain**: `OnPumpDegraded` ← `NdisCapturePump.Degrade` (`src/WinForward.NdisApi/NdisCapture.cs:393-398`, invokes `_onDegraded?.Invoke(nativeError)` `:397`) on the **pump's dedicated thread** (`NdisCapture.cs:169`). `Degrade` is synchronous and cannot await, which is why the `ValueTask` is discarded.
- **Lifetime**: one forwarding operation per pump degradation; ends when `callback` completes. Currently unbounded (nothing joins it).
- **Dedicated thread**: not required by the forwarding body itself; it runs wherever the caller invokes it (pump thread), but it is not thread-affine.
- **Closure capture**: **no closure at this call site** — `ForwardDegradationAsync` is a static method and the arguments are values. The discarded `ValueTask` may allocate a state-machine box if it awaits. Cold/error path (one per degraded pump).
- **Hot-path applicability**: none per-packet; it fires on the read-failure/degradation path, not the steady datagram path.
- **Is `scope.Run` a correct replacement?** **Shape yes, ownership/await no.** `scope.Run` is the intended door: it returns `bool`, starts the body inline, records faults, and joins on drain — matching the "cannot synchronously await, must not lose the work" need. But:
  1. `MultiAdapterCaptureLoop` owns **no scope** (fields are only `_pumps`, `_onAdapterDegraded`, `_degradedAdapterCount` at `:21-23`); the owner of the lifetime is `TransactionalCaptureRuntime` (`CaptureLifecycle.cs:28`), which is what disposes the loop (`CaptureLifecycle.cs:244-245`). The scope would have to live on the runtime (or the callback be routed there).
  2. Design §4.1's stated replacement — "owned callback, invoked synchronously" — is **not literally possible**: the callback is `Func<WindowsAdapter,int,ValueTask>` (`MultiAdapterCaptureLoop.cs:22`) wired to `HandleDegradedAsync` (`src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs:110-121`), which awaits `runtime.MarkAdapterDegradedAsync(...)` `:116` and `_onAdapterDegraded(...)` `:119`. `Degrade` on the pump thread cannot drive an async callback synchronously. So the work must either be tracked via `scope.Run` (async) or the callback chain must become fully synchronous (not a C4-only change; `MarkAdapterDegradedAsync` awaits mode restore).
  3. The runtime's own run task is `StartCoreAsync` → `await _capture.RunAsync(...)` (`CaptureLifecycle.cs:99`). A degradation forward started by `OnPumpDegraded` fires as a pump exits; if `RunAsync`'s `Task.WhenAll` has already observed the pump's return, the forward could still be in flight when `StartAsync`'s task completes. Tracking it in the runtime's scope closes that gap only if the scope drains after `_capture.RunAsync` returns — i.e. the ordering must be explicit.

## Site S4 — `NdisCapturePump` raw `Thread` (design E6, not an allowlist entry)

- **Exact call** (`src/WinForward.NdisApi/NdisCapture.cs:169-175`):
  ```csharp
  var thread = new Thread(() => RunLoop(outcome, cancellationToken))
  {
      IsBackground = true,
      Name = ...,
  };
  Volatile.Write(ref _pumpThread, thread);
  thread.Start();
  ```
  followed by `return new ValueTask(outcome.Task);` `:176`.
- **Body** `RunLoop` (`:207-246`): fully synchronous; blocking native `_driver.TryReadPackets(...)` (`:269`), `Thread.Sleep(_pollDelay)` (`:318`), and the pending-handler block (`:320-346`); completes `outcome` at `:244-246`.
- **Lifetime**: one thread per pump per generation; bounded by `_runCompletion` (`:124`), which `DisposeAsync` awaits (`:417-423`).
- **Dedicated thread**: **mandatory** (blocking native read + sleep); class doc `:59-93` documents the zero-alloc design reason.
- **Closure capture**: `() => RunLoop(outcome, cancellationToken)` captures `this`, the `outcome` TCS, and the `cancellationToken` → display class + `ThreadStart` delegate.
- **Hot-path applicability**: the pump's *loop body* is a zero-allocation gate (`NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes`, `:130`), but the thread creation is once per generation and is not inside that window.
- **Is `scope.Run` a correct replacement?** **No** — same reason as S1: `Run` provides no thread. Design §6/`prd.md:181-182` E6 explicitly keeps raw `Thread` legal here because the thread is already joined via `new ValueTask(outcome.Task)` (`:176`). C4 does not need to touch this spawn to satisfy WF0003 (it is not a `Task.Run`/`StartNew` site); it is in scope only for its completion/`DisposeAsync` shape (see `owner-lifetime-inventory.md` §4).

---

## Cross-site summary

| Site | `file:line` | Needs own thread | Delegate/closure alloc | Cold path | `scope.Run` replacement |
|------|------------|:----------------:|:----------------------:|:---------:|--------------------------|
| S1 monitor | `LayeredCaptureRunner.cs:144-146` | **yes** (blocking `WaitOne` `:223`) | closure (this+CTS local) + delegate + task | yes | **No** — inline would block caller |
| S2 periodic | `LayeredCaptureRunner.cs:148-150` | no (awaits timer `:206`) | closure + delegate + task | yes | Yes (guard non-positive period outside) |
| S3 degrade forward | `MultiAdapterCaptureLoop.cs:110` | no | none at site; ValueTask box | yes | Yes in shape; owner/scope + drain ordering unresolved |
| S4 pump thread | `NdisCapture.cs:169` | **yes** (native read + sleep) | closure + ThreadStart + Thread | yes | **No** — raw `Thread` is the correct shape (E6) |

**Allocation rule applicability**: parent design §2.3 and the zero-allocation gates target the packet datagram path. None of S1–S4 is per-packet, so the `Run`-allocates-is-fine reasoning holds for S2/S3. The only allocation-sensitive *body* among them is S4's `RunLoop` (covered by its own pump gate) and the UDP relay path (Socks5, see `socks5-token-audit.md`), which C4 must not perturb. **S1 cannot be migrated to `scope.Run` without violating its own blocking-thread design**, which the design sketch (§4.1 "loop body is its own async method with a lease") does not account for because `MonitorAsync` is not async.
