# Research: the UDP receive-failure path, end to end

- **Query**: C3 question 4 — receive-loop fault handling, the handler delegate's exact type and every signature it passes through, `_inFlightTeardowns` register/remove + `DrainInFlightTeardownsAsync`, and every caller
- **Scope**: internal
- **Date**: 2026-09-21

## 1. Receive-loop fault handling in `UdpProxySession.cs`

`ReceiveLoopAsync(Func<UdpProxySession, Task> receiveFailureHandler)` `:253-314`:

- Rents the receive window: `var lease = _receiveWindowPool.Rent();` `:255`.
- Loop guard: `while (!_lifetime.IsCancellationRequested)` `:258`.
- Receive: `receive = await _transport.ReceiveAsync(lease.Memory[.._receiveBufferSize], _lifetime.Token)` `:263`.
- Catch blocks, in order:
  - `catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)` `:265-271`
    → `RecordSkippedDatagram(ConnectionReset); continue;` (skip-class, never fatal).
  - `catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)` `:291-294` → normal teardown, swallowed.
  - `catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)` `:295-298` → disposal closed the socket, swallowed.
  - `catch (Exception exception)` `:299-302` → `Volatile.Write(ref _receiveFailure, exception);` `:301` (the **only** fault write).
- `finally { lease.Dispose(); }` `:303-306`.
- After the `finally`: `if (Volatile.Read(ref _receiveFailure) is not null) { _ = receiveFailureHandler(this); }` `:308-313`.
  The comment `:310-311` states the fire-and-forget is on purpose: the handler tears the session down and
  awaiting it would make session disposal (which awaits this loop) re-enter itself.

Note the signal is emitted **after** the lease is returned and **outside** the try/catch, so it is reached on
both the "fault" and "swallowed cancellation/disposal" exits *only if* `_receiveFailure` is non-null (which is
only set by the general catch). Idle expiry / shutdown therefore do not fire the handler (also pinned by
`UdpProxySessionTests.IdleExpiryEndsTheReceiveLoopWithoutRecordingAFailure`).

## 2. The handler delegate — exact type and every signature it passes through

| Layer | Declaration | Type |
|---|---|---|
| `UdpProxySession.Start` | `UdpProxySession.cs:124` | `public void Start(Func<UdpProxySession, Task> receiveFailureHandler)` |
| field assignment | `:127` | `_receiveLoop = ReceiveLoopAsync(receiveFailureHandler);` |
| loop parameter | `:253` | `private async Task ReceiveLoopAsync(Func<UdpProxySession, Task> receiveFailureHandler)` |
| invocation | `:312` | `_ = receiveFailureHandler(this);` |
| interface seam | `IUdpSessionSlotHost.cs:28` | `Task RemoveReceiveFailedSessionAsync(UdpProxySession session);` |
| coordinator impl | `UdpProxyCoordinator.cs:460-471` | `Task IUdpSessionSlotHost.RemoveReceiveFailedSessionAsync(UdpProxySession session)` |
| construction call | `UdpSessionSetup.cs:100` | `session.Start(host.RemoveReceiveFailedSessionAsync);` (method-group → `Func<UdpProxySession, Task>`) |
| test fake | `UdpSessionSetupTests.cs:134` | `public Task RemoveReceiveFailedSessionAsync(UdpProxySession session) => Task.CompletedTask;` |

So the delegate is `Func<UdpProxySession, Task>` throughout; the coordinator supplies its
`IUdpSessionSlotHost.RemoveReceiveFailedSessionAsync` method group. `design.md` §4.2 changes `Start` to
`Action<UdpProxySession>` (synchronous signal), so this method-group conversion and all four
pass-through points must change type together.

## 3. Coordinator `_inFlightTeardowns` register / remove + `DrainInFlightTeardownsAsync`

- Field: `private readonly List<Task> _inFlightTeardowns = [];` `UdpProxyCoordinator.cs:37`.
- **Register** — `RemoveReceiveFailedSessionAsync` `:460-471`:
  ```csharp
  Task IUdpSessionSlotHost.RemoveReceiveFailedSessionAsync(UdpProxySession session)
  {
      var teardown = RemoveReceiveFailedSessionCoreAsync(session);   // :462
      lock (_gate)
      {
          _inFlightTeardowns.RemoveAll(static teardownTask => teardownTask.IsCompleted);   // :467
          _inFlightTeardowns.Add(teardown);                                                // :468
      }
      return teardown;                                                                     // :470
  }
  ```
  Called from exactly one place: `UdpSessionSetup.cs:100` (`session.Start(host.RemoveReceiveFailedSessionAsync)`).
- **Core teardown** — `RemoveReceiveFailedSessionCoreAsync` `:473-483`: resolve the slot under `_gate` by
  `_sessions[session.Flow]` with `ReferenceEquals(current.Session, session)` `:478`; if null return `:480`;
  else `await _slotHost.RemoveSlotAsync(session.Flow, slot, UdpTeardownReason.Fault)` `:481`; log
  `udp.session.closed` `:482`.
- **Drain** — `DrainInFlightTeardownsAsync` `:308-329`: snapshot+clear under `_gate` `:310-315`; await each
  in a `foreach`, catching any exception and warning `:316-328`.
- **Caller of the drain** — exactly one: `DisposeCoreAsync` `:292` (after all `slot.Completion` awaited `:278`
  and all sessions disposed `:289`).
- Tests exercising the register/remove path: `UdpProxyCoordinatorLifecycleTests.ReceiveFaultDisposesAndRemovesSessionWithoutAnotherSend`
  `:82-98`, `ImmediateReceiveFaultRemovesSessionAfterCoordinatorRegistration` `:101-120`.

## 4. Per `design.md` §4.2 replacement

- `Start` becomes `Action<UdpProxySession>`; the loop keeps writing `_receiveFailure` and then invokes the
  signal without `_ =` (`:308-313`).
- The coordinator maps the action to `coordinatorScope.Run(ct => RemoveReceiveFailedSessionCoreAsync(session), "udp.receive-failure")`.
- `_inFlightTeardowns` and `DrainInFlightTeardownsAsync` (`:37,308-329,461-471`) are deleted; the
  comment-invariant at `:303-306` becomes structural (seal ⇒ late `Run` fails).

## Caveats / Not Found

- `RemoveReceiveFailedSessionAsync`'s register path is the only writer of `_inFlightTeardowns`; the `RemoveAll`
  prune at `:467` is the only removal besides `Clear` in the drain `:314`.
- No test directly asserts `_inFlightTeardowns.Count`; the invariant is covered indirectly by the two
  lifecycle tests above.
