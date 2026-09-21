# Research: design contradictions and hazards (C4)

- **Query**: reconcile the parent design's §4.1/§4.3 sketch with the working tree, and enumerate migration hazards the design does not mention — at the detail level of the C3 precedent (`.trellis/tasks/archive/2026-09/09-20-lifecycle-migration-cluster/research/design-contradictions-and-hazards.md`).
- **Scope**: internal, read-only, HEAD `ce70098`.

**Most important findings (read first):**
- **H1/H2**: `QuiescenceScope.Run` invokes the child body inline (`QuiescenceScope.cs:143`, `:208`); the `LayeredCaptureRunner` monitor is a *blocking* loop, not async, so `scope.Run` cannot host it and one of the two `WF0003` sites cannot be removed by `Run`. This is the single hardest part of C4.
- **H3**: D11's "owner teardown is separate from drain" is the only thing preventing teardown-awaits-its-own-tree deadlocks in `TransactionalCaptureRuntime`, `NdisCapturePump`, `IdleExpirySweeper`, and `RuntimeHeartbeat`.
- **C4**: the C4 PRD's SOCKS5 line numbers (`Socks5UdpTransport.cs:255,282,294`, `Socks5ControlConnection.cs:295`) do not name CTS token reads; the real racy reads are `Socks5ControlConnection.cs:208/265/279` vs `:244`.

---

## Contradictions

### C1 — §4.1: `MultiAdapterCaptureLoop` degradation becomes "invoked synchronously"; it cannot

Design §4.1 prescribes for `MultiAdapterCaptureLoop.cs:110` ("`_ = ForwardDegradationAsync`") the fix "Notification: owned callback, invoked synchronously".
Working tree: the callback field is `Func<WindowsAdapter, int, ValueTask>?` (`MultiAdapterCaptureLoop.cs:22`), wired to `HandleDegradedAsync` (`src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs:110-121`), which `await`s `runtime.MarkAdapterDegradedAsync(...)` `:116` and `_onAdapterDegraded(...)` `:119`. The invocation point is `NdisCapturePump.Degrade` (`src/WinForward.NdisApi/NdisCapture.cs:393-398`, `_onDegraded?.Invoke(nativeError)` `:397`), which runs on the pump's dedicated thread (`NdisCapture.cs:169`) and is **synchronous** — it cannot drive an async callback synchronously. A literal sync invocation would require making `MarkAdapterDegradedAsync` sync (mode restore is async) and changing the callback type to a non-`ValueTask` delegate — not a C4-only change.

### C2 — §4.1: `LayeredCaptureRunner` loops become "scope children (loop body is its own async method with a lease)"; the monitor is not async

Design §4.1 maps `LayeredCaptureRunner.cs:144-146,149` to "Real concurrency: scope children (loop body is its own async method with a lease)".
Working tree: `MonitorAsync` (`LayeredCaptureRunner.cs:217-235`) is **not** `async` — it is `Task MonitorAsync(...)` whose body is the blocking loop `while (_changeSource.WaitOne(cancellationToken)) _demandGate.Signal();` `:223` and which returns `Task.CompletedTask` `:234`. Only `PeriodicRefreshTickAsync` (`:201-215`) is a real async method. So "loop body is its own async method with a lease" holds for the periodic tick but **not** for the monitor. See H1/H2.

### C3 — §4.3 has no entries for any C4 owner

Design §4.3 "Per-owner mapping" covers only the C3 cluster (`TcpRedirectSessionStore`, `TcpRedirectSession`, `UdpProxyCoordinator`, `TcpProxyRelay`, `UdpProxySession`). None of C4's seven owners (`LayeredCaptureRunner`, `MultiAdapterCaptureLoop`, `CaptureLifecycle`, `NdisCapturePump`, `IdleExpirySweeper`, `RuntimeHeartbeat`, `Socks5ControlConnection`/`Socks5UdpTransport`) appears. C4's `design.md` must supply that mapping from scratch; the C3 precedent hazards file (162 lines, C1–C8/H1–H12) shows the expected depth.

### C4 — The C4 PRD's SOCKS5 line numbers are not CTS token reads

C4 PRD names unguarded post-dispose token reads at `Socks5UdpTransport.cs:255,282,294` and `Socks5ControlConnection.cs:295`.
Working tree:
- `Socks5UdpTransport.cs:255` = `_ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, _relaySocketAddress);` — a synchronous `SendTo` returning `int`, no token.
- `Socks5UdpTransport.cs:282` / `:294` = `_ = await _socket.SendToAsync(..., cancellationToken)...` — awaited, and the token is a method parameter (`Socks5UdpTransport` owns no CTS at all; fields `:125-131`).
- `Socks5ControlConnection.cs:295` = `if (_attemptCancellation.IsCancellationRequested) throw new IOException(...)` — `IsCancellationRequested` is safe after `Dispose` and does not throw.
The genuine race is `AttemptToken` (`Socks5ControlConnection.cs:208`, evaluates `_attemptCancellation.Token`) read at `:265`/`:279` racing `_attemptCancellation.Dispose()` at `:244`. The PRD line list for `Socks5UdpTransport` corresponds to the C2 `_ =`-discard audit (see `allowlist-shrink.md`), not to CTS reads. Full evidence in `socks5-token-audit.md`.

### C5 — §4.1's `LayeredCaptureRunner` guard is not mentioned

`PeriodicRefreshTickAsync` is only spawned when `_periodicRefreshInterval > TimeSpan.Zero` (`LayeredCaptureRunner.cs:148-150`), because `PeriodicTimer` rejects a non-positive period. `scope.Run(PeriodicRefreshTickAsync, ...)` would have to run unconditionally; the guard must live outside the scope (`LayeredCaptureRunnerPeriodicRefreshTests.ZeroPeriodicIntervalDisablesTheTickEntirely` / `NegativePeriodicIntervalIsRejected` pin this). The design sketch omits the guard.

### C6 — §6/E6 raw-`Thread` claim does not cover the monitor

Design §6/E6 (`parent prd.md:181-182`) says raw `Thread` is deliberately not banned because the two src sites are already joined (`NdisCapture.cs:169`, `SetupExecutor.cs:229`). That is true for the pump, but `LayeredCaptureRunner`'s monitor achieves its dedicated thread via `Task.Factory.StartNew(..., LongRunning)` (`:144-146`), which **is** a `WF0003` site, and there is no raw-`Thread` precedent that also provides a join for a scope. E6 does not tell C4 how to keep the monitor on its own thread while removing the `WF0003`.

### C7 — "2 of 6 legacy sites" vs the C4 fire-and-forget set

Parent design §3.3 says `Run` carries only "2 of 6 legacy fire-and-forget sites". Distinct awaitable-discard spawns in `src/**` today are three: `MultiAdapterCaptureLoop.cs:110` (`_ =`, WF0001), `LayeredCaptureRunner.cs:144` (`Task.Factory.StartNew`, WF0003), `LayeredCaptureRunner.cs:149` (`Task.Run`, WF0003). The other `_ =` sites are non-awaitable or awaited discards (verified by `rg`; see `allowlist-shrink.md`). C4's exact count and rule mapping are 1× WF0001 + 2× WF0003.

### C8 — C4 PRD "shared/wrong-owner CTS" framing overstates the UDP side

The C4 PRD points at `Socks5UdpTransport` as owning a CTS; the type owns none (verified `rg`). The wrong-owner CTS is `Socks5ControlConnection._attemptCancellation`, created by the connection (`:171`), owned by the connection (`:32`/`:42`), disposed by the connection (`:244`), but disposed as a child by two different owners (`Socks5UdpTransport.cs:391`, `TcpProxyRelay.cs:352`). The D7 fix must inject the owner scope into the connection, and the owner differs by construction path.

---

## Hazards

### H1 — `QuiescenceScope.Run` executes the child body inline on the caller thread

`Run` (`QuiescenceScope.cs:131-145`) calls `RunChildAsync(body, lease, name)` directly at `:143`; `RunChildAsync` (`:204-218`) begins `await body(Token)` at `:208`. Being `async`, it runs `body`'s synchronous prologue inline before the first yield; there is **no ThreadPool hop**. A body that blocks before yielding blocks the `Run` caller. This is load-bearing for S1 (monitor) and rules out the design's implied shape for `MonitorAsync`. It also means `scope.Run` from a thread with an affinity requirement (the pump thread, `NdisCapture.cs:397`) executes the forward body's prologue on that thread.

### H2 — The monitor's two goals are in tension: dedicated thread vs `WF0003` removal

Removing the `Task.Factory.StartNew` at `LayeredCaptureRunner.cs:144-146` is required (it is the allowlist entry). Keeping the monitor on its own OS thread is also required (H1). The analyzer `WF0003` matches `Task.Run`/`Task.Factory.StartNew` syntactically, so any re-spawn through them still fires. `new Thread(...)` is not matched by `WF0003`, but a raw `Thread` has no `Task` for a scope to join; the repository's only dedicated-thread-plus-join precedent is `NdisCapturePump`'s `_runCompletion` TCS bridge (`NdisCapture.cs:124`, `:168`, `:176`, `:417-423`). C4 must choose between (a) a raw `Thread` + TCS bridge owned by the runner (mirroring the pump), or (b) accepting a `WF0003`-suppressed shape. The repo has no scope-aware dedicated-thread helper today.

### H3 — Teardown that awaits its own tree (D11 is the only guard)

Four owners await their own loop task inside `DisposeAsync`, while the loop body is the natural scope child:
- `TransactionalCaptureRuntime`: `DisposeAsync()=>StopAsync()` (`CaptureLifecycle.cs:134`) awaits `_runTask` `:126`, and `_runTask` is `StartCoreAsync`, whose `finally` calls `CleanupAsync` (`:103-104`). If `StartCoreAsync`/the capture loop becomes a scope child and `CleanupAsync` drains that scope, the child awaits its own drain → deadlock.
- `NdisCapturePump`: `DisposeAsync` parks on `_runCompletion` (`NdisCapture.cs:420`); if `RunLoop` awaited a scope drain on exit, deadlock.
- `IdleExpirySweeper` (`:115-127`) and `RuntimeHeartbeat` (`:250-262`): `DisposeAsync` awaits `_loop`; the loop body must not await its own drain.
D11 ("owner keeps its own teardown single-flight; `DrainAsync` covers only the drain") is the mitigation; the C3 `TcpProxyRelay` shape (`TcpProxyRelay.cs:334-361`) and `UdpProxySession` shape (`UdpProxySession.cs:181-188`) are the precedents.

### H4 — `ValueTask`-vs-`Task` completion shapes

`QuiescenceScope.Run` accepts only `Func<CancellationToken, Task>`. Owners/entry points that expose `ValueTask`:
- `TransactionalCaptureRuntime.StartAsync` returns `new ValueTask(_runTask)` (`CaptureLifecycle.cs:80`);
- `NdisCapturePump.RunAsync` returns `new ValueTask(outcome.Task)` (`NdisCapture.cs:176`);
- `Socks5ControlConnection.UdpAssociateAsync`/`ConnectDestinationAsync` return `ValueTask` (`:212`, `:222`);
- `Socks5UdpTransport.SendSpanAsync`/`ReceiveAsync` return `ValueTask` (`:230`, `:311`) and are **hot**.
Wrapping a hot `ValueTask` method in `Run` would add a delegate + task on the datagram path (forbidden). Adapters for the cold owners (`.AsTask()`) are fine but allocate; the design does not note this.

### H5 — `Socks5ControlConnection._attemptCancellation` is disposed before the UDP session's scope drains

`UdpProxySession.DisposeCoreAsync` (`UdpProxySession.cs:209-236`) disposes `_transport` `:217` (which disposes `_control` at `Socks5UdpTransport.cs:391`, which disposes `_attemptCancellation` at `Socks5ControlConnection.cs:244`) **before** `await _scope.DrainAsync()` `:233`. A `RunWithinAttemptAsync` (`:263`/`:277`) that is in flight or enters during teardown reads `AttemptToken` (`:208`) after the dispose. Under D7 the control CTS must be owned by the session/relay scope so its token outlives the transport. See also H6.

### H6 — Wrong-owner CTS across two construction paths

`Socks5ControlConnection` is constructed inside `Socks5UdpTransport.CreateAsync` (control factory `Socks5UdpTransport.cs:184-185`) and inside `TcpProxyRelay` (`TcpProxyRelay.cs:40`). The owner scope that should own the per-attempt CTS is therefore `UdpProxySession._scope` in one path and `TcpProxyRelay._scope` in the other. The per-attempt `CancelAfter(timeout)` (`Socks5ControlConnection.cs:172`, checked at `:295`) cannot move onto a long-lived scope CTS without timing out the whole scope; how the attempt timeout survives D7 is an open design question (no C3 precedent has `CancelAfter`).

### H7 — `StartCoreAsync` links the owner CTS and reads its token

`StartCoreAsync` (`CaptureLifecycle.cs:84-106`) reads `_shutdown.Token` at `:86` in `CreateLinkedTokenSource(cancellationToken, _shutdown.Token)`. If `_shutdown` becomes scope-owned, this read must occur while the scope token is live (before `DrainAsync` releases it — `async-lifetime.md` drain ordering / `TokenIsReleasedOnceDrained`). `StopAsync` calls `_shutdown.CancelAsync()` at `:123`; under D7 that becomes `scope.Cancel()`, and the `CancelBestEffortAsync` swallowing of `ObjectDisposedException` (`:444-452`, and `LayeredCaptureRunner.cs:444-452`) becomes a no-op but must not become a throw.

### H8 — Degradation forward started from the pump thread during pump exit

`NdisCapturePump.Degrade` (`NdisCapture.cs:393-398`) is invoked from `RunLoop`'s exit path (read-failure/degradation classification `:320-346`); `MultiAdapterCaptureLoop.OnPumpDegraded` (`:103-111`) then starts the forward. Under `scope.Run` (H1) the forward body's synchronous prologue runs on the pump thread while the pump is unwinding. `HandleDegradedAsync` awaits `MarkAdapterDegradedAsync` (mode restore I/O) and `_onAdapterDegraded`; the prologue up to the first await is equivalent to today's `_ = ForwardDegradationAsync(...)` call, so no new blocking is expected, but the scope must be owned by `TransactionalCaptureRuntime` (the loop has no scope, `MultiAdapterCaptureLoop.cs:21-23`) and the drain ordering must ensure the forward completes before the runtime's teardown finishes. Today `MultiAdapterCaptureLoop.DisposeAsync` (`:78-84`) does **not** await the forward at all.

### H9 — `LayeredCaptureRunner` CTSes are disposed on multiple paths

`_refreshCancellation`/`_generationCancellation` are disposed in `StopGenerationAsync` (`:365`/`:367`) and again in `RecoverFromStartupFaultAsync` (`:435-438`), relying on null-out (`:346-347`) and `CancelBestEffortAsync`'s `ObjectDisposedException` swallow (`:444-452`). Any scope migration must preserve idempotent cancel/dispose. The runner is also **not** `IAsyncDisposable` and has no scope field; adding one plus a teardown path is a net line increase.

### H10 — `LayeredCaptureRunner.cs` is over the 400-effective-line cap

`src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` is **417** effective lines (568 physical), 17 over `directory-structure.md:57`. C4 must net-reduce; removing two spawns and hoisting the bodies helps, but adding a scope field + `Run` calls + teardown can push it further over unless code is deleted.

### H11 — `Socks5UdpTransport`'s real race is the send gate, not a token

`_sendGate.Dispose()` (`Socks5UdpTransport.cs:397`) races a sender that has not yet reached `_sendGate.WaitAsync(cancellationToken)` (`:236`); the disposal comment (`:395-396`) only covers senders already parked inside the gate. `_socket.Dispose()` (`:379`) races in-flight `SendToAsync`/`ReceiveFromAsync` (treated as the stop signal by `UdpProxySession.DisposeCoreAsync`'s `ObjectDisposedException` catch `:226-230`). If C4 adds a guard it must be an allocation-free `Interlocked` flag (D7-safe) and must not alter the `SendSpanAsync` warm shape (`:230-269`) or the `_sendGate` "dispose last" order, or `WarmSyncSendAllocatesNoManagedBytes` / `SendSpanAsyncWarmPathRunsNoAsyncStateMachine` / `HotPathAllocationGateTests` break.

### H12 — `TransactionalCaptureRuntime` already has three disposal layers; a scope must not double-count

`_cleanupTask ??=` single-flight under `_gate` (`CaptureLifecycle.cs:200-207`), `_captureDisposed` `Interlocked.Exchange` (`:244-245`), and `_state` transitions under `_gate`. D11 says the owner keeps its own teardown single-flight; the scope's `DrainAsync` must not duplicate the `_cleanupTask` claim, or `StopAsync`'s `await _runTask` (`:126`) plus the finally `CleanupAsync` (`:103-104`) can deadlock (H3).

### H13 — Heartbeat/bundle disposal ordering is external to C4

`RuntimeHeartbeat` is consumed with `await using var heartbeat` (`Program.cs:225`) and therefore disposed before the enclosing `finally { await bundle.DisposeAsync(); }` (`Program.cs:229-232`, comment `:222-224`). `IdleExpirySweeper` is disposed first inside `DurableCaptureBundle.DisposeCoreAsync` (`DurableCaptureBundle.cs:351`), before UDP/TCP. A scope migration must preserve these orderings; the bundle's `_disposeTask ??=` single-flight (`:340-343`) is the D11 precedent.

### H14 — One-shot start guards: `Run` returns `false`, not throws

`RuntimeHeartbeat.Start` throws on second start (`RuntimeHeartbeat.cs:106`), `IdleExpirySweeper.Start` throws (`:56`), and `NdisCapturePump.RunAsync` throws on second call (`NdisCapture.cs:161`). `QuiescenceScope.Run` returns `false` once sealed (`async-lifetime.md` D10). Re-expressing these starts through `Run` changes the observable contract; `SecondStartIsRejected` (`RuntimeHeartbeatTests.cs`), `SecondRunAsyncThrowsWithoutDisturbingTheFirstRun` (`NdisCapturePumpTests.cs`) pin the current throw.

### H15 — Double-dispose semantics change for sweeper and heartbeat

`IdleExpirySweeper.DisposeAsync` (`:115-127`) and `RuntimeHeartbeat.DisposeAsync` (`:250-262`) have **no guard**: a second dispose calls `_shutdown.CancelAsync()` on a disposed CTS → `ObjectDisposedException`. Moving to `Interlocked` one-shot + drain join (D11) makes the second dispose a join instead. Their tests do not currently cover double-dispose, so new coverage is needed rather than an update.

### H16 — `IdleExpirySweeper`'s loop body is not a session owner; re-entrancy is bounded but real

The sweeper holds `_dispatcher`, `_tcp`, `_udp` (`IdleExpirySweeper.cs:19-21`, constructed `DurableCaptureBundle.cs:233`), not the bundle, so a sweep cannot call `bundle.DisposeAsync` and await its own `_loop`. However, a sweep can dispose expired sessions (`RemoveExpired` on the coordinators), which is work performed inside `_loop`; if the sweeper gets a scope, those teardowns must not be admitted as sweeper children after seal, and the drain must not wait on session work that itself waits on the sweeper. The design does not discuss the sweeper's session-teardown side effects.

### H17 — `MultiAdapterCaptureLoop` currently starts forwarding without ownership; disposal may complete first

`MultiAdapterCaptureLoop.DisposeAsync` (`:78-84`) only disposes pumps; it never awaits an in-flight `ForwardDegradationAsync`. A pump can degrade (starting the forward) and then the loop be disposed while the forward is still awaiting `MarkAdapterDegradedAsync`/the outer callback. Tracking the forward in a runtime-owned scope closes the gap only if the runtime drains that scope after `_capture.RunAsync` returns (`CaptureLifecycle.cs:99`); the ordering must be explicit, and `CaptureDegradationPlumbingTests.DegradedPumpKeepsSiblingsRunningAndForwardsCallback` (`NdisCaptureResilienceTests.cs:181+`) observes the delivery.

### H18 — The `WF0003` sites and the "no new allocation on a hot path" rule

None of the C4 spawn sites is on the packet datagram path (S1/S2 per-run, S3 per-degradation, S4 per-generation), so `Run`'s delegate/closure/task allocation is admissible there (parent design §2.3/§3.3, "`Run` is cold-path only"). The one place the rule bites is **not** a spawn: `Socks5UdpTransport.SendSpanAsync` (`:230-269`) is the warm hot path and must not gain a `Run`, a CTS, a closure, or a guard allocation (H11). `NdisCapturePump.RunLoop`'s zero-alloc gate (`IdlePollIterationsAllocateNoManagedBytes`) likewise bounds anything C4 adds inside the loop, not the thread creation.
