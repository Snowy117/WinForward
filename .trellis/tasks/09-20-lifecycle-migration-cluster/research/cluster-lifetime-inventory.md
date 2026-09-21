# Research: TCP/UDP lifecycle cluster — per-file lifetime inventory

- **Query**: C3 — exhaustive lifetime machinery inventory for the 8 cluster files, exact `path:line`, per-owner disposition vs parent design §4
- **Scope**: internal (verified against working tree, not assumed)
- **Date**: 2026-09-21
- **Normative sources**: `design.md` §2, §4.1–§4.4; `prd.md`; `.trellis/spec/backend/async-lifetime.md`; `.trellis/spec/backend/hot-path.md`; `src/WinForward.Runtime/QuiescenceScope.cs` (C1, 242 lines)

> Line numbers below are from the working tree at research time. The parent design's §4 table
> numbers were checked against these; discrepancies are called out in
> `design-contradictions-and-hazards.md`.

## Compact table — file → machinery → disposition

| File | Lifetime machinery (with anchors) | Parent design disposition |
|---|---|---|
| `TcpRedirectSessionStore.cs` | `_shutdown` CTS `:32`; `_setupsDrained` TCS `:33`, reallocated per 0→1 at `:63`; `_disposeTask` `:34`; `_inflightSetups` `:35`; `_disposed` `:36`; `EnterSetup` `:58-65`; `ExitSetup` `:67-73`; `DisposeAsync` `:139-159`; discarded `_ = RunDisposeAsync(start)` `:157`; `DisposeCoreAsync` `:174-203` | `_inflightSetups` + `_setupsDrained` + `_disposeTask` → `QuiescenceScope` (§4.3 `:32-36,58-73,139-203`); `:157` discard → `new ValueTask(scope.DrainAsync())` (§4.1). `_shutdown`/`_disposed`/`ShutdownToken` not itemized by the design. |
| `TcpRedirectSession.cs` | `Lifetime` linked CTS `:19`; `_retired` `:20`; `_lifetimeDisposed` `:21`; `Relay` `:22`; `AcceptLoop` `:23`; `Token` `:24`; `Retire()` `:27-39`; `DisposeLifetime()` `:41-44` | scope child (`AcceptLoop`) + scope-owned CTS (`Lifetime`) → §4.3 `:19-44` |
| `TcpProxyRelay.cs` | `_disposed` `:114`; `Completion` `:129` (= `RunPumpAsync` started in ctor `:126`); `pumpCancellation` local CTS `:136` (never disposed); `PumpAsync` ×2 `:137-138`; three exit paths `:144-150`, `:155-164`, `:166-172`; `ObservePump` `:287-294` (`_ = first.ContinueWith` `:289`); `_ = ShutdownSend(...)` `:284`; `DisposeAsync` `:302-331` (`await _control.DisposeAsync` `:312`, `await Completion` `:322`, empty catch + `RCS1075` `:324-329`); `StallWindow` `:182-211` | `ObservePump` + `:289` discarded continuation **deleted** (§4.1/§4.4); `:284` becomes synchronous `Socket.Shutdown`; scope tracks both pumps; `Completion` keeps relay-level semantics (§4.3/§4.4) |
| `TcpRelayFaultObserver.cs` | `Observe` `:23-41`; `_ = relay.Completion.ContinueWith` `:27-40`; reads `task.Exception` then Debug-gated `tcp.relay.faulted` | **whole file deleted** (§4.3/§4.4) |
| `TcpRedirectAcceptor.cs` | `RunAcceptLoopAsync` `:18-59` (reads `session.Token` `:22`, finally `session.DisposeLifetime()` `:57`); `ObserveRelayCompletionAsync` `:198-240` (`await relay.Completion.WaitAsync(token)` `:207`, reset `:222-232`, `tearDownSession` `:234`); attach-failure `TcpRelayFaultObserver.Observe` `:120`; `DiscardUnattachedRelayAsync` `:131-150`; `DrainRedundantConnectionsAsync` `:157-184` | `:120` **deleted** (§4.4); rest unchanged (accept loop stays the lifetime-CTS owner) |
| `UdpProxySession.cs` | `_lifetime` linked CTS `:46` (created `:85`); `_activityGate` `:52`; `_disposeGate` `:53`; `_receiveLoop` `:54`; `_disposeTask` `:55`; `_receiveFailure` `:56`; `_expiring` `:66`; `_disposed` `:67`; `_activeSends` `:68`; `Start(Func<…,Task>)` `:124-128`; `SendSpanAsync` `:139-165`; `FinishSpanSendAsync` `:167-179`; `DisposeAsync` `:181-189`; `TryBeginExpiry` `:191-204`; `CancelExpiry` `:206-209`; `CancelLifetime` `:215-226`; `DisposeCoreAsync` `:228-251`; `ReceiveLoopAsync` `:253-314`; discarded `_ = receiveFailureHandler(this)` `:312` | scope (accounting + drain + owned CTS) + `_expiring` stays (§4.3 `:46,54-68,139-251`); `:312` → signal/join split (§4.2). `_receiveFailure`/`_disposed`/`_disposeGate` fate **not stated**. |
| `UdpProxyCoordinator.cs` (+ `.Send.cs`) | `_shutdown` CTS `:24`; `_disposeTask` `:28`; `_disposed` `:29`; `_inFlightTeardowns` `:37`; `DisposeAsync` `:218-247`; `DisposeCoreAsync` `:249-301` (`await slot.Completion` `:278`, `await session.DisposeAsync` `:289`, `DrainInFlightTeardownsAsync` `:292`, `_shutdown.Dispose()` `:297`); `DrainInFlightTeardownsAsync` `:308-329`; `UdpSessionSlot` `:492-498`; `ScheduleSessionSetup` `:138-157` (`slot.Completion = completion.Task` `:155`) | `_inFlightTeardowns` + `_shutdown` → scope; session scopes nested children; `_inFlightTeardowns` deleted (§4.2/§4.3 `:24,28,37,308`). `_disposeTask`/`_disposed`/`UdpSessionSlot.Completion` fate not stated. |
| `UdpSessionSetup.cs` | `_setupLimiter` `SemaphoreSlim(8)` `:39`; `CreateSessionAsync` `:62-119` (limiter wait `:70`, transport create `:81`, claim `:87`, session ctor `:97`, `AttachSession` `:99`, `session.Start(host.RemoveReceiveFailedSessionAsync)` `:100`, flush `:102`, failure `host.RemoveSlotAsync` `:113`, `_setupLimiter.Release()` `:117`); `DisposeLimiter` `:184` | not itemized in §4.3; reached via coordinator scope (§4.2). `DisposeLimiter` is called by coordinator `:296`. |

## (a) Detailed per-file machinery

### `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs` (334 lines)

Owns:
- `private readonly CancellationTokenSource _shutdown = new();` `:32` — store-wide lifetime CTS.
  `ShutdownToken => _shutdown.Token` `:47`. Disposed at `:202` (`_shutdown.Dispose()`), after setups drained.
- `private TaskCompletionSource _setupsDrained = CompletedSource();` `:33` — the 0→1 TCS.
  Re-allocated on every 0→1 transition inside `EnterSetup` `:63`; `ExitSetup` signals it at `:71`.
  `CompletedSource()` `:328-333` pre-completes it.
- `private Task? _disposeTask;` `:34` — single-flight dispose handle.
- `private int _inflightSetups;` `:35` — setup counter.
- `private bool _disposed;` `:36` — admission/disposal flag.
- `EnterSetup()` `:58-65`: `lock(_gate)`, `ObjectDisposedException.ThrowIf(_disposed, this)`,
  `if (_inflightSetups++ == 0) _setupsDrained = new TaskCompletionSource(RunContinuationsAsynchronously)`.
- `ExitSetup()` `:67-73`: `lock(_gate)`, `if (--_inflightSetups == 0) _setupsDrained.TrySetResult()`.
- `DisposeAsync()` `:139-159` (single-flight via `_disposeTask`, see (c)).
- `RunDisposeAsync(TaskCompletionSource)` `:161-172` — wraps `DisposeCoreAsync`; sets completion result/exception.
- `DisposeCoreAsync()` `:174-203`.

Foreign members others depend on (see (b)).

### `src/WinForward.Runtime/TcpRedirect/TcpRedirectSession.cs` (45 lines)

- `private CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);` `:19`.
- `_retired` `:20`, `_lifetimeDisposed` `:21`.
- `public ITcpRelay? Relay { get; set; }` `:22`, `public Task? AcceptLoop { get; set; }` `:23`.
- `Token => Lifetime.Token` `:24`; `IsRetired` `:25`.
- `Retire()` `:27-39`: `Interlocked.Exchange(ref _retired, 1)`; early-return if already retired or
  `_lifetimeDisposed != 0`; `Lifetime.Cancel()` caught `ObjectDisposedException` `:34-38`.
- `DisposeLifetime()` `:41-44`: `Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0` then `Lifetime.Dispose()`.

### `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs` (332 lines)

- `_disposed` int `:114` — `DisposeAsync` single-flight via `Interlocked.Exchange` `:304`.
- `Completion` `:129` = `RunPumpAsync(upstream)` `:126` (started in the constructor).
- `RunPumpAsync` `:133-173`:
  - `await using var localStream = new NetworkStream(_localSocket, ownsSocket: true);` `:135`.
  - `using var pumpCancellation = new CancellationTokenSource();` `:136` — **never disposed** on any path.
  - `localToUpstream = PumpAsync(localStream, upstream, …)` `:137`; `upstreamToLocal = PumpAsync(upstream, localStream, …)` `:138`.
  - Exit path 1 — stall fast-exit `:144-150`: `await Task.WhenAny` `:142`, `await first` `:143`;
    if `Stalled` → `pumpCancellation.CancelAsync()` `:146`, `ObservePump(other)` `:147`,
    `EndKind = Stalled` `:148`, `return` (other pump is abandoned, never awaited/gathered).
  - Exit path 2 — normal `:152-164`: `ShutdownSend(upstream)` or `ShutdownSend(_localSocket)` `:152-153`;
    `await Task.WhenAll(localToUpstream, upstreamToLocal)` `:155`; Stalled→`Stalled` `:156-160`, else `CleanEnded` `:163`.
  - Exit path 3 — fault `:166-172`: `pumpCancellation.CancelAsync()` `:168`;
    `ObservePump(localToUpstream.IsCompleted ? upstreamToLocal : localToUpstream)` `:169`;
    `EndKind = Faulted` `:170`; rethrow.
- `ObservePump` `:287-294`: `_ = first.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
  OnlyOnFaulted | ExecuteSynchronously, TaskScheduler.Default)`.
- `ShutdownSend(Stream)` `:281-285`: `_ = ShutdownSend(networkStream.Socket)` `:284` (the §4.1 spurious-async site).
- `PumpAsync` `:218-268`: rents one 64 KiB native lease `:225`, `StallWindow` `:228`; returns
  `PumpResult.Stalled` on OCE `:237-243`/`:254-261`, `Ended` on read 0 `:245-248`; lease released in `finally` `:266`.
- `DisposeAsync` `:302-331` (see (c)).

### `src/WinForward.Runtime/TcpRedirect/TcpRelayFaultObserver.cs` (42 lines)

- `Observe(ITcpRelay relay, IRuntimeLogger logger)` `:23-41`: `_ = relay.Completion.ContinueWith(
  task => { var exception = task.Exception?.InnerException ?? task.Exception; if (!logger.IsEnabled(Debug)) return;
  logger.Event(Debug, "tcp.relay.faulted", new RuntimeLogField("error", exception?.GetType().Name)); },
  CancellationToken.None, OnlyOnFaulted | ExecuteSynchronously, TaskScheduler.Default)` `:27-40`.
- Call sites (verified): `TcpProxyRelay.DisposeAsync` `:308`, `TcpRedirectAcceptor.cs:120`, test
  `TcpRelayObservationTests.cs:108`.

### `src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs` (241 lines)

- `RunAcceptLoopAsync` `:18-59`: `var token = session.Token;` `:22`; accept loop `:23-49`;
  `finally { session.DisposeLifetime(); }` `:51-58` — the **only** `session.Token` reader and the
  lifetime-CTS disposal owner (R1).
- `TryEstablishRelayAsync` `:66-124`: unrelated-peer branch `:72-83`; `relayFactory.EstablishAsync` `:84`;
  `tryAttachRelay` `:85`; attached branch `:89-100` starts `ObserveRelayCompletionAsync` `:96`, awaits
  `DrainRedundantConnectionsAsync` `:97`, then awaits `relayCompletion` `:98`; cancellation catch `:102-106`;
  setup-failure catch `:107-111`; attach-failure branch `:113-123` (Observe `:120`, Discard `:121`, tearDown `:122`).
- `ObserveRelayCompletionAsync` `:198-240`: `await relay.Completion.WaitAsync(token)` `:207`;
  cancellation returns `:209-212`; any other catch falls through `:213-216`; EndKind != CleanEnded → client reset `:222-232`;
  `tearDownSession` `:234`.
- `DiscardUnattachedRelayAsync` `:131-150` awaits `relay.DisposeAsync()` `:135` then `accepted.DisposeAsync()` `:144`.

### `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` (414 lines)

Owns:
- `private readonly CancellationTokenSource _lifetime;` `:46`, assigned `CreateLinkedTokenSource(context.Shutdown)` `:85`.
- `_activityGate` `:52` (admission + send accounting + `_expiring`/`_receiveFailure`/`_disposed`),
  `_disposeGate` `:53` (dispose single-flight).
- `_receiveLoop` `:54`, `_disposeTask` `:55`, `_receiveFailure` `:56`.
- `_expiring` `:66`, `_disposed` `:67`, `_activeSends` `:68`.
- `State` `:105-116` reads `_disposed`/`_receiveFailure`/`_expiring` under `_activityGate`.
- `Start(Func<UdpProxySession, Task> receiveFailureHandler)` `:124-128` — assigns `_receiveLoop = ReceiveLoopAsync(...)`.
- `SendSpanAsync` `:139-165` (admission under gate, `_activeSends++`; inline path decrements at `:161`;
  async path `FinishSpanSendAsync` `:167-179` decrements in `finally` `:177`).
- `DisposeAsync` `:181-189`; `TryBeginExpiry` `:191-204` (requires `_activeSends == 0`, sets `_expiring`, `CancelLifetime`);
  `CancelExpiry` `:206-209`; `CancelLifetime` `:215-226`; `DisposeCoreAsync` `:228-251`.
- `ReceiveLoopAsync` `:253-314`; `InjectResponseAsync` `:317-340` (reads `_lifetime.Token` `:321`).

### `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` (499) + `.Send.cs` (165)

- `_shutdown` CTS `:24`; `_disposeTask` `:28`; `_disposed` `:29`; `_inFlightTeardowns` `List<Task>` `:37`.
- `DisposeAsync` `:218-247` single-flight; `DisposeCoreAsync` `:249-301`.
- `DrainInFlightTeardownsAsync` `:308-329` (snapshot+clear under gate, await each, swallow+warn).
- `IUdpSessionSlotHost` implementations `:335-471` (see `udp-setup-tracking.md`).
- `UdpSessionSlot` `:492-498`.
- `.Send.cs` does **not** own a CTS/Task; it routes teardown through `_slotHost.RemoveSlotAsync`.

### `src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs` (187 lines)

- `_setupLimiter` `:39`. `CreateSessionAsync` `:62-119`; `RefreshSetupStampsAtDialStart` `:128-132`;
  `FlushSetupQueueAsync` `:147-177`; `DisposeLimiter` `:184`.

## (b) Public/internal members other files depend on

### `TcpRedirectSessionStore` (as consumed in `src/`, verified by grep)
| Member | Consumer(s) |
|---|---|
| `IsDisposed` `:45` | `TcpProxyCoordinator.cs:122,410,517,548,602` |
| `ShutdownToken` `:47` | `TcpProxyCoordinator.cs:232,295,300,308,325` |
| `Tombstones` `:50` | `TcpProxyCoordinator.cs:143,529,584,642,639` |
| `SessionCount` `:53` | `TcpProxyCoordinator.cs:85,158` |
| `EnterSetup` `:58` | `TcpProxyCoordinator.cs:261` |
| `ExitSetup` `:67` | `TcpProxyCoordinator.cs:282` |
| `TryRegister` `:80` | `TcpRedirectSetup.cs:180`; tests `TcpRedirectSessionStoreTests.cs:22,59`, `TcpRedirectAcceptorTests.cs:24` |
| `Holds` `:98` | `TcpProxyCoordinator.cs:639` |
| `RemoveExpiredAsync` `:114` | `TcpProxyCoordinator.cs:627` |
| `DisposeAsync` `:139` | `TcpProxyCoordinator.cs:672` |
| `TearDownSessionAsync` `:205` | `TcpProxyCoordinator.cs:77,78`; `TcpRedirectSetup.cs:123,133,150`; `TcpRedirectSessionStore.cs:281` |
| `TryAttachRelay` `:262` | `TcpProxyCoordinator.cs:78` (`_store.TryAttachRelay` passed to acceptor) |
| `FailAssociationAsync` `:273` | `TcpProxyCoordinator.cs:77,385,392` |
| `ReleaseAssociationAsync` `:291` | `TcpRedirectSetup.cs:112,184` |

### `TcpRedirectSession`
`Association`, `Listener`, `SelfTrafficToken`, `Server`, `FlowGeneration` (`:14-18`),
`Relay` `:22`, `AcceptLoop` `:23`, `Token` `:24`, `IsRetired` `:25`, `Retire` `:27`, `DisposeLifetime` `:41`.
Consumers: `TcpRedirectAcceptor` (`Token` `:22,68,102`, `DisposeLifetime` `:57`), `TcpRedirectSessionStore`
(`AcceptLoop` `:191,193,259`; `Retire` `:226`; `DisposeLifetime` `:88,199,259`; `Token`. doc `:257`),
`TcpProxyCoordinator.cs:312` (`setup.Session.AcceptLoop = _acceptor.RunAcceptLoopAsync(...)`),
`ClientResetInjector` (`session` overload).

### `TcpProxyRelay` / `ITcpRelay` (from `TcpRedirectInterfaces.cs`)
- `ITcpRelay` `:55-58`: only `Task Completion { get; }` (public).
- `ITcpRelayEndInfo` `:81-85`: `RelayEndKind EndKind { get; }` (internal).
- `RelayEndKind` `:69-74`: `CleanEnded`, `Stalled`, `Faulted`.
- `Completion` consumers: `TcpRedirectAcceptor.cs:207` (`WaitAsync(token)`), `TcpProxyRelay.DisposeAsync:322`,
  `TcpRelayFaultObserver.cs:27`; tests `TcpProxyRelayTests` `:79,90,116,166,193`, `TcpRelayObservationTests` `:73,93`,
  `TcpRelayEndResetTests` `:111,125,138`.
- `TcpProxyRelayFactory.EstablishAsync` `:25`; public const `PumpBufferSize` `:23`;
  internal consts `RelayConnectMaxAttempts`/`s_relayConnectAttemptTimeout` `:19-20`.

### `UdpProxySession`
- `Flow` `:94`, `FlowGeneration` `:95`, `Association` `:96`, `LastActivityUtc` `:97`,
  internal `State` `:105`, `Start` `:124`, `SendSpanAsync` `:139`, `DisposeAsync` `:181`,
  internal `TryBeginExpiry` `:191`, `CancelExpiry` `:206`.
- Consumers: `UdpSessionSetup` (`SendSpanAsync` `:165`, `Start` `:100`, ctor `:97`),
  `UdpProxyCoordinator` (`State` `:112`, `LastActivityUtc` `:395`, `TryBeginExpiry` `:404`,
  `CancelExpiry` `:407`, `DisposeAsync` `:289,456`, `slot.Session`), `IUdpSessionSlotHost.AttachSession`/`RemoveReceiveFailedSessionAsync`,
  tests `UdpProxySessionTests.cs:26,41,72,99,102,131`.

### `UdpProxyCoordinator`
- Public `TrySendSpanAsync` (`Send.cs:20`) — the sole warm send entry; `SessionCount` `:93`, `Capacity` `:99`,
  `RemoveExpiredAsync` `:388`, `DisposeAsync` `:218`, `IAsyncDisposable`, `IUdpSessionSlotHost`.
- Internal `SessionState` `:108`, `Diagnostics` `:121`, `ReceiveWindowSize` `:128`, `UdpSessionSlot` `:492`.
- `.Send.cs`: `SendOnReadySessionSpanAsync` `:83`, `SendSpanTailAsync` `:116` (`RemoveSlotAsync` `:130`),
  `RemoveSlotSpanAsync` `:153`.
- `IUdpSessionSlotHost` is implemented by the coordinator (`_slotHost = this` ctor `:80`) and supplied to `UdpSessionSetup` `:89`.

### `UdpSessionSetup` / `IUdpSessionSlotHost`
- `IUdpSessionSlotHost` `IUdpSessionSlotHost.cs:13-29`: `AttachSession` `:16`, `RefreshSetupStamps` `:19`,
  `DequeueForFlush` `:22`, `RemoveSlotAsync` `:25`, `RemoveReceiveFailedSessionAsync` `:28` (five gate-taking ops).
- `UdpSessionSetup.FlushStep` enum `:50-60`; internal `TtlExpiredCount` `:44`, `StampsRefreshedCount` `:47`,
  `CreateSessionAsync` `:62`, `DisposeLimiter` `:184`.

## (c) DisposeAsync / Dispose call graphs

### `TcpRedirectSessionStore.DisposeAsync` `:139-159`
1. Under `_gate`: if `_disposeTask is not null` → return the stored task; else set `_disposed = true`,
   create `start = new TaskCompletionSource(RunContinuationsAsynchronously)`, store `_disposeTask = start.Task`.
2. First caller only: `if (start is not null) _ = RunDisposeAsync(start);` `:157`.
3. Return `new ValueTask(disposeTask)` `:158`. All concurrent/sequential callers observe the same task.
- `RunDisposeAsync` `:161-172` → `await DisposeCoreAsync()` → `completion.TrySetResult()` / `TrySetException`.
- `DisposeCoreAsync` `:174-203` order:
  1. `await _shutdown.CancelAsync()` `:176`;
  2. snapshot `_setupsDrained.Task` under `_gate` `:178`, `await` it `:179` (join all in-flight setups);
  3. snapshot all sessions and `RetireSessionUnderGate` each `:182-185` (removes alias + arms tombstone under gate);
  4. per retired: `await ReleaseRetiredAsync` `:189` (listener+token, then relay dispose);
  5. if `session.AcceptLoop is not null` `await session.AcceptLoop` `:193` catching OCE-if-shutdown `:194` and ObjectDisposedException `:195`;
  6. `session.DisposeLifetime()` `:199` (idempotent; the loop's `finally` usually already did it);
  7. `_shutdown.Dispose()` `:202`.
- Single-flight mechanism: `_disposeTask` under `_gate`. No re-entrancy path found. `DisposeCoreAsync` never takes `_gate` across an `await` except the brief snapshots.
- Test pin: `TcpRedirectSessionStoreTests.DisposeIsSingleFlightAndLateTeardownNeverReEnters`.

### `TcpProxyRelay.DisposeAsync` `:302-331`
1. `if (Interlocked.Exchange(ref _disposed, 1) != 0) return;` `:304` — single-flight.
2. `TcpRelayFaultObserver.Observe(this, _logger);` `:308` (hooked **before** sockets go away).
3. `_localSocket.Dispose();` `:309`.
4. `try { await _control.DisposeAsync(); }` `:312`.
5. `finally { await Completion; }` `:322` with empty `catch (Exception)` + `RCS1075` pragma `:324-329`.
- Re-entrancy: none — `Completion` is `RunPumpAsync`, which does not call `DisposeAsync`.
- Note: `pumpCancellation` `:136` is never disposed; `StallWindow._source` is disposed in `PumpAsync`'s `using` `:228`.

### `UdpProxySession.DisposeAsync` `:181-189`
1. `lock (_activityGate) _disposed = true;` `:183`.
2. `lock (_disposeGate) _disposeTask ??= DisposeCoreAsync();` `:186`; return `new ValueTask(_disposeTask)`.
- `DisposeCoreAsync` `:228-251`: `CancelLifetime()` `:230`; `await _transport.DisposeAsync()` `:233`;
  `await _receiveLoop` `:236` catching OCE-if-lifetime `:237` and ObjectDisposedException `:241`;
  `finally { _lifetime.Dispose(); }` `:249`.
- Re-entrancy risk: the receive loop's tail calls `receiveFailureHandler(this)` which (today) tears the
  session down → `DisposeAsync` → awaits `_receiveLoop` (the loop that invoked the handler). That is the
  cycle `design.md` §4.2 fixes with the signal/join split.

### `UdpProxyCoordinator.DisposeAsync` `:218-247`
1. Under `_gate`: first caller sets `_disposed = true`, creates completion TCS, `_disposeTask = completion.Task`; all callers read `_disposeTask`.
2. First caller: `await DisposeCoreAsync()` `:237`, then `completion.TrySetResult()`/`TrySetException`.
3. All callers: `await disposeTask` `:246`.
- `DisposeCoreAsync` `:249-301`: cancel `_shutdown` `:251`; under `_gate` snapshot+clear `_sessions`, clear cooldowns,
  drain queued datagrams and credit budget `:252-273`; per slot `await slot.Completion` `:278` (catches OCE/faulted) and
  `await session.DisposeAsync()` `:289`; `await DrainInFlightTeardownsAsync()` `:292`; `_setup.DisposeLimiter()` `:296`;
  `_shutdown.Dispose()` `:297`.
- Single-flight: `_disposeTask` under `_gate`. Test pin: `UdpProxyCoordinatorLifecycleTests.ConcurrentDisposalIsSingleFlightAndRejectsNewSends`.

### `UdpSessionSetup.CreateSessionAsync`
No DisposeAsync; `DisposeLimiter` `:184` is called by `UdpProxyCoordinator.DisposeCoreAsync:296` **after** all
`slot.Completion` are awaited. `CreateSessionAsync` releases the limiter in its `finally` `:117`.

## (d) CancellationToken reads that could follow owning-CTS disposal

| File | Token read | Owner CTS disposed at | Analysis |
|---|---|---|---|
| `TcpRedirectSessionStore.cs` | `ShutdownToken` consumed by coordinator `:232,295,300,308,325` | `_shutdown.Dispose()` `:202` | `DisposeCoreAsync` awaits `_setupsDrained` `:179` before `:202`; `EnterSetup` throws after `_disposed`. A setup task that passed `EnterSetup` is in the counter and joined. No known read-after-dispose, **but** the coordinator reads `_store.ShutdownToken` at `:295,300,308,325` inside `RunSetupPipelineAsync`, which runs before its matching `ExitSetup` `:282`. |
| `TcpRedirectSession.cs` | `Token` read in acceptor `:22,68`, `ClientResetInjector` | `DisposeLifetime()` `:44` | `Retire` `:32` catches `ObjectDisposedException`. The acceptor is the only direct reader and owns disposal; its `finally` runs after it stops reading. |
| `TcpProxyRelay.cs` | `pumpCancellation.Token` read by both pumps `:137,138`; `StallWindow` reads `lifetime` token | `pumpCancellation` never disposed (`:136`) | No ODE, but the CTS (and its timer) is leaked per relay until GC. |
| `TcpProxyRelay.cs` | `_control`/`_localSocket` disposed before `await Completion` `:309,312` | n/a | `PumpAsync` reads/writes may throw `ObjectDisposedException` on the disposed socket/stream; `RunPumpAsync` converts OCE to `Stalled` and propagates other faults. |
| `UdpProxySession.cs` | `_lifetime.IsCancellationRequested` `:237,258,291,295,323`; `_lifetime.Token` `:263,321` | `_lifetime.Dispose()` `:249` | `DisposeCoreAsync` awaits `_receiveLoop` `:236` before `:249`, so the loop's reads precede disposal. `CancelLifetime` `:215-226` tolerates an already-disposed source. `SendSpanAsync` passes the **caller's** token, not the lifetime token. |
| `UdpProxyCoordinator.cs` (+`.Send.cs`) | `_shutdown.Token` captured into `SetupWorkItem._cancellationToken` `:148`; `_shutdown.IsCancellationRequested` `:90,123,449`; `_shutdown.Token` at `:148` | `_shutdown.Dispose()` `:297` | `IsCancellationRequested` is safe after dispose (does not throw); `Token` after dispose throws. `SetupWorkItem._cancellationToken` is captured **before** dispose and consumed by joined setup tasks. A `SendSpanTailAsync` continuation that resumes after `:297` only reads `IsCancellationRequested` (safe). No known `Token` read after dispose. |
| `UdpSessionSetup.cs` | `cancellationToken` (= `_shutdown.Token`) across `:70,77,81,97,102,165` | `_shutdown.Dispose()` `:297` | The setup task is joined via `slot.Completion` before `:297`; a task starting after disposal is rejected at `ScheduleSessionSetup` by `ObjectDisposedException.ThrowIf(_disposed)` `Send.cs:28` (which runs under `_gate`). |

## Caveats / Not Found

- The store's `_disposed`/`_shutdown`/`ShutdownToken` and the coordinator's `_disposed`/`_disposeTask` and
  `UdpSessionSlot.Completion` are **not itemized in `design.md` §4.3**; their post-migration fate is not
  stated there (see `design-contradictions-and-hazards.md`).
- `UdpProxySession._receiveFailure` is present in the §4.3 "Today" column but absent from the "After"
  column; its fate under D9/`scope.Fault` is not specified.
- `TcpProxyRelay.RunPumpAsync`'s `pumpCancellation` `:136` has no `using`/`Dispose` on any path — an
  existing resource leak, not introduced by C3, and not mentioned by the design.
