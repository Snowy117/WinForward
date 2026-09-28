# C4 — Migrate the remaining lifecycle owners to `QuiescenceScope`

Per-owner executable design. Evidence for every claim lives in `research/`:
`owner-lifetime-inventory.md` (fields, start/observe/dispose edges, nesting),
`spawn-and-allocation-audit.md` (every spawn classified, incl. the primitive's inline-launch
semantics), `socks5-token-audit.md` (the real CTS race), `existing-tests.md` (baseline + which tests
pin which mechanism), `design-contradictions-and-hazards.md` (C1–C8, H1–H18),
`allowlist-shrink.md` (the two sections to delete + the rule-firing proof).

All anchors verified against HEAD `ce70098` on 2026-09-21.

## 1. Deliverable, boundary, and decisions

Ships:

1. `LayeredCaptureRunner` — the monitor becomes a **dedicated OS thread joined by the run scope**; the
   periodic refresh tick becomes a `Run` child; the scope owns the run's CTS; the file nets back under
   the 400-effective-line cap.
2. `MultiAdapterCaptureLoop` — its own scope; the degradation forward becomes a `Run` child drained by
   `DisposeAsync`.
3. `CaptureLifecycle` (`TransactionalCaptureRuntime`) — `_shutdown` becomes a scope-owned CTS, drained
   at the end of the existing single-flight cleanup.
4. `IdleExpirySweeper`, `RuntimeHeartbeat` — scope-owned CTS + a `Run` loop child + a D11 one-shot
   (their double-dispose stops throwing).
5. `Socks5ControlConnection` — a scope provides admission + the lifetime token; the epoch deadline CTS
   stays but is released only after the drain; every reader of the attempt token holds a lease.
6. `Socks5UdpTransport` — an allocation-free disposal guard only (no scope: it owns no CTS and is the
   hot datagram path).
7. The last two `.editorconfig` allowlist sections deleted.

Not shipped: `NdisCapturePump`'s thread/join mechanism (see D-C4-5), `DurableCaptureBundle` /
`Program.cs` ordering (H13 is preserved, not redesigned), and any structural change to the UDP
datagram path.

Boundary check after C4: no `_ =` (awaitable), `.ContinueWith`, `Task.Run` or
`Task.Factory.StartNew` anywhere under `src/**` except the primitive's own documented exemption, and
`.editorconfig` holds only that exemption under `src/**`.

### Decisions made by this child

| # | Decision | Rationale |
|---|----------|-----------|
| D-C4-1 | **D7 covers owner lifetimes only.** An *operation- or epoch-scoped* CTS (a stall window, a per-attempt deadline) stays, owned by the operation that creates it, and its release is ordered after the drain by the lease discipline. | Resolves H6's open wrinkle without inventing a scope feature: `CancelAfter` on a scope CTS would time out the whole owner. Precedent: `TcpProxyRelay.StallWindow` (a per-direction re-armed CTS) shipped in C3 unchanged. |
| D-C4-2 | The capture **monitor keeps a dedicated raw `Thread`** and is joined through the run scope (it takes a lease for its lifetime); `WF0003`'s own message permits "a dedicated worker its owner joins". | Resolves C2/H1/H2: `Run` invokes the body **inline** (`QuiescenceScope.cs:143`, `:208`) and `MonitorAsync` (`LayeredCaptureRunner.cs:217-235`) blocks in `WaitOne` (`:223`), so `Run` would stall the caller. A lease makes the scope's drain the join, so no TCS bridge is needed. |
| D-C4-3 | `LayeredCaptureRunner`'s refresh workers move to a new file, and the file must end **≤400 effective lines**. | Resolves H10: the file is already **417** effective lines, 17 over `directory-structure.md`; removing spawns alone does not pay for the added scope/thread plumbing. |
| D-C4-4 | `MultiAdapterCaptureLoop` owns the scope for its degradation forwards; `DisposeAsync` disposes the pumps **first**, then drains. | Resolves H17/C1: the loop owns the pumps (so it owns their exit notifications), and disposing the pumps first means a pump that degrades *while* being disposed is still admitted, then joined. Doing it in the other order would refuse a live degradation notification. |
| D-C4-5 | **`NdisCapturePump` is not migrated to the primitive.** | Hard reason, not preference: the primitive is `internal` to `WinForward.Runtime` and the dependency direction is `Runtime → NdisApi`, so `NdisApi` cannot reference it. E6 already sanctions its raw `Thread`, which is joined via `ValueTask(outcome.Task)` (`NdisCapture.cs:176`, `DisposeAsync` `:417-423`). |
| D-C4-6 | `IdleExpirySweeper` / `RuntimeHeartbeat` run their loop as a `Run` child and keep their throw-on-second-`Start` contract explicitly. | The loop body is a true async wait (`WaitForNextTickAsync`), so `Run` is the correct door; the throw is a pinned contract (`RuntimeHeartbeatTests.SecondStartIsRejected`, H14) and `Run` returns `bool`, so the guard stays in `Start()` and maps a `false` to the throw. |
| D-C4-7 | `Socks5ControlConnection`'s **attempt-token race is closed by admission, not by ownership**: every reader takes a scope lease, and `DisposeAsync` releases the epoch CTS only after the drain. | Resolves C4/C8/H5/H6 faithfully: the deadline's *semantics* (one budget spanning the attempt) are pinned by `Socks5ControlTimeoutTests`, so it must stay a shared epoch CTS; making readers leases guarantees no `.Token` read can race the release (a reader either holds a lease — the release waits — or is refused by the seal and never reads). |
| D-C4-8 | `Socks5UdpTransport` gets an `Interlocked` disposal guard, **not** a scope. | Resolves H11: it owns no CTS (`rg` zero hits) and `SendSpanAsync` is the warm hot path; its real race is `_sendGate.Dispose()` (`:397`) against a sender that has not yet entered `WaitAsync` (`:236`). An `Interlocked` read is allocation-free and does not touch the warm shape or the gate order. |

## 2. Per-owner design

### 2.1 `LayeredCaptureRunner` (`src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs`)

Today: `_started` one-shot (`:58`), `_runTask` (`:63`), the local `monitorCancellation` (`:140`), the
two spawns (`:144-146`, `:148-150`), `TeardownAsync(monitor, monitorCancellation, periodicTick)`
(`:454-471`), per-generation `_refreshCancellation`/`_generationCancellation` (`:61-62`, disposed in
`StopGenerationAsync` `:365`/`:367` and again in `RecoverFromStartupFaultAsync` `:435-438`).
It implements neither `IAsyncDisposable` nor `IDisposable`; its quiescence point is the `Task`
returned by `RunAsync` (the sole production caller is `Program.cs:331`).

After:

- `_scope = new QuiescenceScope(cancellationToken)` is created at the top of `RunAsync` (the scope
  owns the run's lifetime CTS). `monitorCancellation` is deleted; `monitorCancellation.Token` becomes
  `_scope.Token`. `StopGenerationAsync`'s `CancelBestEffortAsync`-then-`await runTask` shape is
  unchanged.
- **Monitor** — a dedicated thread whose body admits itself into the scope and runs the blocking loop:
  `if (!_scope.TryEnter(out var lease)) return; try { MonitorLoop(_scope.Token); } catch (Exception e) { _scope.RecordFault(e, "capture.monitor"); } finally { lease.Dispose(); }`.
  The thread is `IsBackground = true`. `MonitorAsync` becomes the synchronous `MonitorLoop` (it already
  returns `Task.CompletedTask`, `:234`), and the `Task.Factory.StartNew(…, LongRunning)` +
  `Unwrap()` at `:144-146` are deleted. Because the monitor holds a lease, `await _scope.DrainAsync()`
  **is** the join: `TeardownAsync` no longer needs a monitor task parameter.
- **Periodic tick** — `_scope.Run(PeriodicRefreshTickAsync, "capture.periodic-refresh")`, called only
  when `_periodicRefreshInterval > TimeSpan.Zero` (the guard stays outside `Run`: `PeriodicTimer`
  rejects a non-positive period, pinned by `ZeroPeriodicIntervalDisablesTheTickEntirely` /
  `NegativePeriodicIntervalIsRejected`). `PeriodicRefreshTickAsync` takes the token `Run` supplies
  (method group, no closure); the local `periodicTick` handle and its `await` are deleted.
- **Teardown** — `StopGenerationAsync` → `_scope.Cancel()` (replacing
  `CancelBestEffortAsync(monitorCancellation)`) → `await _scope.DrainAsync()` (joins the monitor thread
  *and* the periodic tick, then releases the run CTS) → `await _disposeDurableAsync(...)`. The rethrow
  of a captured generation-stop fault is preserved.
- The per-generation `_refreshCancellation`/`_generationCancellation` pair **stays** under D-C4-1
  (epoch CTSes; they are cancelled and disposed on every path, and `CancelBestEffortAsync` keeps
  swallowing `ObjectDisposedException` — that must remain a no-op, never a throw, per H7).
- **Cap (D-C4-3)** — the refresh workers move to a new file as their own internal type; the loops move
  verbatim (behaviour-zero), and the new type receives the change source plus an `Action` signal wired
  to `RefreshDemandGate.Signal` so the gate type can stay nested in the runner. `LayeredCaptureRunner.cs`
  must end ≤400 effective lines.

### 2.2 `MultiAdapterCaptureLoop` (`src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs`)

Today: no scope, no CTS, no `_disposed` (`:21-23`); `RunAsync` awaits the pumps via `Task.WhenAll`
(`:66-69`); `OnPumpDegraded` discards the forward at `:110`; `DisposeAsync` (`:78-84`) disposes the
pumps and never awaits an in-flight forward.

After (D-C4-4):

- `_scope = new QuiescenceScope()` (parent-less — the loop's lifetime is bounded by its owner's
  `RunAsync` await, and `NdisCaptureGeneration` wires the callback to the runtime) and
  `_disposeStarted` (D11).
- `OnPumpDegraded` → `_scope.Run(ct => ForwardDegradationAsync(_onAdapterDegraded, adapter, nativeError, ct), "capture.degrade-forward")`.
  The forward's synchronous prologue still runs on the pump's thread exactly as today (H8), so no new
  blocking; `ForwardDegradationAsync` gains the token parameter and keeps swallowing every fault with
  `GC.KeepAlive`.
- `DisposeAsync`: the D11 winner disposes the pumps sequentially, then `await _scope.DrainAsync()`;
  the loser awaits the same drain. A forward admitted while a pump degraded during its disposal is
  therefore awaited instead of orphaned (H17).
- `RunAsync` is untouched (both pumps are awaited inline — already I1-compliant).

### 2.3 `TransactionalCaptureRuntime` (`src/WinForward.Runtime/Capture/CaptureLifecycle.cs`)

Today: `_shutdown` CTS (`:32`), `_runTask` (`:36`), `_cleanupTask` (`:37`), `_captureDisposed` (`:38`),
`_state` (`:35`); `_shutdown.Token` is read once as a link source (`:86`); `StopAsync` cancels it
(`:123`); `RestoreBestEffortAsync` disposes it (`:197`).

After:

- `_scope = new QuiescenceScope()` replaces `_shutdown`; `:86` links `_scope.Token`;
  `StopAsync`'s `await _shutdown.CancelAsync()` becomes `_scope.Cancel()`.
- `CleanupCoreAsync` (`:209-220`) gains a final ordered step: `DisposeCaptureAsync()` →
  `RestoreBestEffortAsync()` → `await _scope.DrainAsync()` (releases the owned CTS) →
  `SetClosedState()`. `RestoreBestEffortAsync`'s `_shutdown.Dispose()` is deleted.
- **The run task is not registered.** `_runTask` is an *entry point* a caller awaits
  (`StartAsync` returns `new ValueTask(_runTask)`), not a spawned child — registering it would make
  `StopAsync`'s `await _runTask` (`:126`) wait on a drain that waits on the run task (H3). The three
  existing disposal layers (`_cleanupTask ??=`, `_captureDisposed`, `_state`) stay and remain the
  owner's single-flight (D11/H12).
- `Cancel()` discipline (H7): it is reachable only via the first `Stopping` transition, which precedes
  the cleanup, so it can never run after the CTS release; each `CancelBestEffortAsync`-style swallow
  stays a no-op.

### 2.4 `IdleExpirySweeper` (`:14`) and `RuntimeHeartbeat` (`:46`)

Today (identical shapes): `_shutdown` CTS (`IdleExpirySweeper.cs:25` / `RuntimeHeartbeat.cs:57`),
`_loop` (`:29` / `:62`); `Start()` throws if `_loop is not null` (`:56` / `:106`); `RunAsync` reads
`_shutdown.Token`; `DisposeAsync` (`:115-127` / `:250-262`) cancels, awaits `_loop`, disposes the CTS —
**unguarded**, so a second dispose throws `ObjectDisposedException` from `CancelAsync`.

After (D-C4-6):

- `_scope = new QuiescenceScope()` replaces `_shutdown`; `_started` (`int`) replaces the
  `_loop is not null` check; `_loop` is deleted.
- `Start()`: throw on a second start (preserved contract), then
  `if (!_scope.Run(RunAsync, "<owner>.loop")) throw new ObjectDisposedException(...)`.
  `RunAsync` becomes `private async Task RunAsync(CancellationToken token)` (method group, no closure);
  its `_shutdown.IsCancellationRequested` guards become `token.IsCancellationRequested`.
- `DisposeAsync`: D11 one-shot → `await _scope.DrainAsync()`, which seals, cancels (the loop's timer
  wait unwinds), joins the in-flight tick, and releases the CTS last. A second `DisposeAsync` joins
  instead of throwing.
- The sweeper's session-teardown side effects (H16) stay direct calls inside the tick; they are not
  registered, so the drain never waits on work that waits on the sweeper.

### 2.5 `Socks5ControlConnection` (`src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`)

Today: `_attemptCancellation` (field `:32`, created `:171` with `CancelAfter(timeout)` `:172`,
transferred at `:178`, read through `AttemptToken` `:208` by `RunWithinAttemptAsync` `:265`/`:279`, and
disposed in `DisposeAsync` `:244`); no disposal guard at all.

After (D-C4-7):

- The connection gains `_scope = new QuiescenceScope(cancellationToken)` (the ctor already receives the
  caller's token as `_connectCancellation` `:33`, so no signature change is needed) plus a D11
  one-shot.
- `_attemptCancellation` **stays** as the epoch deadline CTS under D-C4-1, now linked to the scope's
  token instead of standing alone; `AttemptToken` keeps returning its token, so the timeout semantics
  (one budget per attempt, checked at `:295`) are unchanged.
- **Every reader holds a lease.** `ConnectOnceAsync` (connect + authenticate) and both
  `RunWithinAttemptAsync` overloads do `if (!_scope.TryEnter(out var lease)) throw new ObjectDisposedException(...)`
  and release in a `finally`. Because the reader holds a lease, `DisposeAsync` cannot release the epoch
  CTS under it; a reader arriving after the seal is refused *before* it touches `AttemptToken`. The
  `:265`/`:279` linked operation CTSes stay per-operation locals (`using`).
- `DisposeAsync` order: D11 claim → `_scope.Cancel()` → `await _stream.DisposeAsync()` →
  `_loopPrevention?.Dispose()` → `_attemptCancellation.Dispose()` → `await _scope.DrainAsync()`
  (joins the admitted operations, then releases the scope CTS). `DisposeFailedAttemptAsync`'s disposal
  of the *local* `attemptCancellation` (`:397`) is untouched.
- Not allowed to change: the `_handshakeScratch` reuse, the socket-timeout clearing
  (`UpstreamStreamClearsPerAttemptSocketTimeouts`), and the L1 fail-closed "next candidate" retry —
  all pinned by `Socks5ControlTimeoutTests`.

### 2.6 `Socks5UdpTransport` (`src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`)

Today: no CTS anywhere (`:125-131`), no disposal guard; `_socket.Dispose()` (`:379`),
`_selfTrafficToken?.Dispose()` (`:385`), `await _control.DisposeAsync()` (`:391`), then
`_sendGate.Dispose()` last (`:397`).

After (D-C4-8): add `private int _disposed;` and a D11 one-shot; `DisposeAsync` sets it before
`_socket.Dispose()`; `SendSpanAsync` fails closed (its existing `bool` result) when it reads
`Volatile.Read(ref _disposed) != 0`, so a sender that has not yet entered `_sendGate.WaitAsync`
(`:236`) cannot observe a disposed gate. Nothing else changes: the non-async warm shape
(`:229-270`), `_sendBuffer`/`_receiveSenderTemplate` reuse, the `payload.ToArray()` contended branch,
and the "gate disposed last" order all stay byte-for-byte, so
`WarmSyncSendAllocatesNoManagedBytes`, `SendSpanAsyncWarmPathRunsNoAsyncStateMachine` and
`HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` stay green.

### 2.7 `NdisCapturePump` — deliberately unchanged (D-C4-5)

The pump owns no CTS and its raw `Thread` is already joined (`ValueTask(outcome.Task)` `:176`;
`DisposeAsync` `:417-423`). The primitive is `internal` to `WinForward.Runtime`, so `WinForward.NdisApi`
cannot reference it — a migration is not merely unnecessary, it is impossible without widening
internals. Its tests (`DisposeDuringRunWaitsForTheRunLoopToExit`,
`DisposeBeforeAnyRunCompletesSynchronously`, `SecondRunAsyncThrowsWithoutDisturbingTheFirstRun`,
`DedicatedPumpThreadExitsAndIsBackgroundAfterRun`, `IdlePollIterationsAllocateNoManagedBytes`) stay
untouched and green.

## 3. Allowlist shrink

Deleted in the step that migrates each file (`.editorconfig`, verified line numbers):

| Section | Lines | Removed by |
|---------|-------|-----------|
| `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs` (WF0001) | 455-458 | step 2 |
| `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` (WF0003) | 460-463 | step 1 |

Both rules were proven load-bearing by scratch probes (`allowlist-shrink.md`: `error WF0001: Await this
awaitable or start it as a tracked child with QuiescenceScope.Run`; `error WF0003: Start tracked work
with QuiescenceScope.Run, or use a dedicated worker its owner joins`). After C4 the only
`severity = none` under `src/**` is the primitive's permanent `QuiescenceScope.cs` WF0001 exemption —
the parent program's completion condition.

## 4. Verification

- **Per-owner quiescence** (one test each, mirroring the C3 shape): `DisposeAsync` does not complete
  while registered/inline work is outstanding and completes once it finishes — runner (monitor thread
  + tick), loop (a `TaskCompletionSource`-gated degradation callback), runtime, sweeper, heartbeat,
  control connection (a gated operation).
- **D11 single-flight**: a second `DisposeAsync` joins rather than re-running teardown, and no longer
  throws for the sweeper/heartbeat (H15); `NdisCapturePump`'s existing join tests stay green.
- **Monitor threads/join**: the monitor still runs on a non-ThreadPool thread and the runner still
  joins it before durable disposal (`UserCancelDisposesGenerationBeforeDurableDisposal` and the
  `LayeredCaptureRunnerTests` ordering set).
- **Cap**: `LayeredCaptureRunner.cs` effective lines ≤400.
- **Degradation**: `DegradedPumpKeepsSiblingsRunningAndForwardsCallback` (plus a new "forward in
  flight at dispose is awaited" case).
- **SOCKS5**: `Socks5ControlTimeoutTests` green, and a new test proving a concurrent
  `RunWithinAttemptAsync` during `DisposeAsync` never surfaces `ObjectDisposedException` from a token
  read.
- **Hot path**: `HotPathAllocationGateTests` green in isolation, plus
  `WarmSyncSendAllocatesNoManagedBytes` and `SendSpanAsyncWarmPathRunsNoAsyncStateMachine`.
- **Behaviour-zero**: `CaptureLifecycleTests` (incl. the two concurrency tests),
  `NdisCapturePumpTests`, `CaptureDegradationPlumbingTests`, `NdisCaptureResilienceTests`,
  `LayeredCaptureRunnerPeriodicRefreshTests`, `RuntimeHeartbeatTests`, `IdleExpirySweeperFailureTests`,
  `DurableCaptureBundleTests` all green with no weakened assertions.
- **Boundary**: the `rg` scan for the banned syntax over `src/**` returns only the primitive's
  exemption; `.editorconfig` holds only that exemption.
- **Full gates**: `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`
  (exit 0), `dotnet build WinForward.slnx -c Release` (0 warnings), `dotnet test WinForward.slnx -c
  Release` (green — baseline 773 + 18 plus exactly the new tests), `jb inspectcode -f=Xml -e=HINT`
  (zero `<Issue ` — parse the XML).

## 5. Risks

| Risk | Mitigation |
|------|-----------|
| The monitor's lease makes the run drain wait on a thread that never returns | The drain cancels the owned token *first*, and today's `CancelBestEffortAsync` + `await monitor` already depends on that cancellation interrupting `WaitOne` — so the drain is exactly as bounded as the existing teardown. The per-owner test asserts disposal completes. |
| Moving the refresh workers out (D-C4-3) is where behaviour drift hides | Behaviour-zero move: the loop bodies and the guard move verbatim; `LayeredCaptureRunnerPeriodicRefreshTests` + `LayeredCaptureRunnerHealthSignalTests` are the oracle, and the cap check is mechanical. |
| The degradation forward's prologue runs on the pump thread under `Run` (H1/H8) | Unchanged from today: `_ = ForwardDegradationAsync(...)` already ran the same prologue inline on that thread; the callback chain still yields at its first await. |
| Disposing the loop's pumps before the drain refuses a live degradation notification | D-C4-4's order is pumps-then-drain precisely so a pump degrading *during* disposal is admitted; the new test covers the in-flight case. |
| The SOCKS5 lease adds cost to a cold path | The lease is an interlocked increment on a per-operation (not per-datagram) path; the hot path (`SendSpanAsync`/`ReceiveAsync`) is untouched and its zero-alloc gates are re-run. |
| `Cancel()` after the CTS release throws `ObjectDisposedException` | Every owner calls `Cancel()` only through its single-flight pre-drain transition (documented per owner in §2); the drain is the only place the CTS is released. |
| `.editorconfig` section deletion outruns the code | Each section is deleted in the same step as its file's migration, and the boundary `rg` plus a build are the gate — a partial step cannot leave a site both unmigrated and unexempted. |

## 6. Spec updates

- `async-lifetime.md`: per-owner notes for the six migrated owners (which scope, what it owns, what it
  deleted) and the D-C4-1 line (owner lifetimes vs operation/epoch CTSes) beside D7; the allowlist
  table drops to the primitive's single permanent exemption; the WF0003 note gains the
  "dedicated worker its owner joins" form as a sanctioned shape.
- `hot-path.md`: record that the UDP transport's disposal guard is a plain `Interlocked` read that
  does not enter the warm shape, and that the capture refresh workers are off the packet path.
- `error-handling.md`: the sweeper/heartbeat double-dispose change (a join instead of
  `ObjectDisposedException`) and the guarantee that a second `DisposeAsync` on a migrated owner never
  throws.
