# TCP Relay and Store Lifecycle

> The lifetime of a redirect session's relay and store: how a pump ends and what that end means, where
> fault observation lives, the stall window, dispose ordering and single-flight, the setup admission
> lease, and who owns the session's lifetime. Read it when you change `TcpProxyRelay`'s pumps or
> `DisposeAsync`, `TcpRedirectSession` / `TcpRedirectSessionStore` lifetime ownership, the
> `TryEnterSetup` seam, the acceptor's completion observation or attach-failure branch, or the SOCKS5
> control-socket handoff. Part of the TCP local-redirect family; the hub
> [tcp-local-redirect.md](./tcp-local-redirect.md) has the pipeline overview, the cross-cutting
> invariants and the topic map. `QuiescenceScope` itself — admission, seal, join, work leases — is
> [async-lifetime.md](./async-lifetime.md)'s contract and carries the per-owner table; this document
> states only the redirect family's use of it.

## What a relay end is

`TcpProxyRelay` implements `ITcpRelay` and the internal `ITcpRelayEndInfo { RelayEndKind EndKind; long
ServerStreamBytes }`. `RelayEndKind` is `CleanEnded` / `Stalled` / `Faulted` and is meaningful only once
`Completion` completes; a relay that does not implement the capability is treated as `CleanEnded`. The
enum is internal, so it lives on a separate capability interface rather than on the public `ITcpRelay`.
`EndKind` **defaults to `Faulted`** — the fail-visible kind: a relay whose `RunPumpAsync` throws before its
first classification write (for example during `NetworkStream`/CTS construction) must still surface as a
client reset, so `CleanEnded` is assigned only explicitly, after both pumps verifiably completed (caught in
review 2026-08-30). `ServerStreamBytes` is the server→client delivered byte count, read with `Volatile.Read`.
The acceptor turns the kind into the client-visible close: a crafted RST|ACK for a non-clean end, and for a
clean one the bounded drain — the relay is released, the stack's own FIN is carried by the live alias, and
the retire waits for the client's acknowledgement — described in
[tcp-client-close-injection.md](./tcp-client-close-injection.md).

## Pump result classification

`PumpAsync` returns one of three results and never leaves a faulted task:

- **Admission refused** (`!_scope.TryEnter(out var lease)`) → `PumpResult.Ended`. A refusal means disposal
  already began, so the pump never ran. Reporting `Stalled` here would drive `EndKind = Stalled`, and the
  acceptor injects a client RST for any non-`CleanEnded` kind — an ordinary teardown would look like a
  stall timeout. The branch is unreachable in practice (both pumps start synchronously in the constructor,
  before the relay can be sealed); it exists to make the worst case harmless.
- **`OperationCanceledException`** from the read/write (the stall window or the lifetime token) →
  `PumpResult.Stalled`.
- **Any other exception** → `RecordPumpFault(exception)`: the exception is recorded on the scope as
  `tcp.relay.pump` and reported through the Debug event `tcp.relay.faulted`, and the pump returns
  `PumpResult.Faulted`.

`RunPumpAsync` orchestrates the pair with `Task.WhenAny`. A first result of `Ended` is *not* a relay end:
it joins both pumps and finishes cleanly. A first result of `Stalled` cancels the scope, sets
`EndKind = Stalled` and returns without awaiting the sibling — the stall fast-exit — so `Completion`
completes on a stall without waiting for both pumps. A first result of `Faulted` cancels the scope and
rethrows the recorded `_scope.Fault` on `Completion` (`PumpFaultMessage` is the unreachable fallback).
Otherwise each direction propagates its FIN (`ShutdownSend`), both pumps are joined, and any fault makes
the relay `Faulted`, any stall makes it `Stalled`, and only a fully clean pair makes it `CleanEnded`.

**Fault observation is intrinsic.** The pump body records the fault and reports it as a result, so a pump
abandoned by the fast-exit can never become an unobserved fault — no external reaper or observer is needed.
`RunPumpAsync` surfaces the recorded fault on `Completion`, and every path observes `Completion`:
`DisposeAsync` through `ObserveCompletionAsync` (which records the completion fault as `tcp.relay.completion`),
and the acceptor's `ObserveRelayCompletionAsync` inside a catch. `ContinueWith` is a build error in this
repository (`WF0002`), with no allowlist entry — the mechanism is unrepresentable, not merely discouraged.

## The stall window

One reusable `StallWindow` per pump direction owns a single linked CTS: `TryReset()` + `CancelAfter` of the
30-minute window (`s_stallTimeout`), re-armed at most once per second (`IsRearmDue`,
`s_armThrottleTicks = Stopwatch.Frequency`) because the arm does timer-queue work per operation and the
window only drifts by up to that second. `TryReset` keeps the lifetime-token link armed, so session-wide
and cross-pump cancellation still cancel an in-flight operation immediately; the source is recreated only
when a previous stall timer raced with operation completion (`TryReset` returned false). The window is
never disarmed between operations. A stalled relay therefore completes without faulting and is reclaimed
without a per-flow wall-clock timeout.

## Dispose ordering and single-flight (D11)

`TcpProxyRelay.DisposeAsync` takes an explicit one-shot claim (`Interlocked.Exchange`); a second caller
joins the drain and observes `Completion` instead of re-running the socket/control teardown. The claimant
runs: `DrainAsync()` → `Cancel()` → `_localSocket.Dispose()` → `await _control.DisposeAsync()` → await the
drain → `ObserveCompletionAsync()`.

- **The drain is bounded by the socket close.** Closing the client-facing socket is what forces a pump
  abandoned by the stall fast-exit to return, so the drain cannot hang.
- **A precheck on `IsSealed` would be TOCTOU**, because the seal happens inside `DrainAsync`; that is why
  the claim is an explicit `Interlocked` flag rather than a scope query.
- **A late caller does not observe the claimant's teardown fault** — it joins only the drain, then observes
  `Completion`.
- **On a clean end the acceptor's drain is the first caller.** It disposes the relay before the retire, and
  the store's later release joins that single-flight teardown instead of owning it: the same D11 shape with
  one caller earlier, so no socket is disposed twice and no `tcp.redirect.closed` record repeats.

## Setup admission is a bool, not an exception

`TcpRedirectSessionStore` holds one `QuiescenceScope`: `ShutdownToken => _scope.Token`,
`IsDisposed => _scope.IsSealed`, and `bool TryEnterSetup(out WorkLease lease) => _scope.TryEnter(out
lease)`. A `false` return means disposal already began: the caller must not start the setup and must
unwind **without** a cooldown — `_pendingSyn.Complete(..., writeCooldown: false, ...)` runs before
`lease.Dispose()`, so a disposal racing a launched setup leaves no cooldown and no charge. The lease is
disposed by the caller in its `finally`, after the completion. The setup-side bounds and worker are in
[tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md).

The store's dispose joins the same inflight counter: a store dispose with an in-flight setup waits for
`session.AcceptLoop` for each session and for the registered setup leases, and a late teardown entry after
disposal is a no-op through the `IsSealed` gate.

## The accept loop owns the session lifetime

- `TcpRedirectAcceptor.RunAcceptLoopAsync` awaits **both** terminal steps in order —
  `ObserveRelayCompletionAsync` (which delivers the client-visible close — the crafted RST for an
  abnormal end, the bounded drain for a clean one — and then tears the session down) and
  `DrainRedundantConnectionsAsync` — with no `_ =` discards. The loop owns
  `session.DisposeLifetimeAsync()`, so `session.AcceptLoop` spans the whole session lifetime and the
  store's `await session.AcceptLoop` in `DisposeCoreAsync` is a true quiescence wait.
- **The lifetime CTS is disposed after its last reader.** `TcpRedirectSession.Retire()` cancels the scope
  and returns; the *dispose* is deferred to `DisposeLifetimeAsync()` after `AcceptLoop` has ended, so the
  accept loop and the client-reset path that read `session.Token` can never touch a disposed
  `CancellationTokenSource`. `Retire()` must never seal or drain for exactly that reason: the accept loop
  and `ClientResetInjector` read the token while unwinding.
- **The accept loop deliberately holds no scope lease (documented deviation).** The store joins
  `session.AcceptLoop` explicitly in `DisposeCoreAsync`, which is where that loop's quiescence comes from;
  the session's own `DisposeAsync` is therefore *not* a quiescence point for it. A lease would deadlock
  against the in-loop `DisposeLifetimeAsync()` reached through `ObserveRelayCompletionAsync`.
- `TcpRedirectSession.IsRetired` stays the owner's admission flag; `_retired` is the one-shot behind it.

## Failure boundaries around the relay

- **An attach failure leaves no half-open session.** When `tryAttachRelay` returns `false`, the accepted
  connection is closed and the relay discarded **outside** the setup `try`, then the session is torn down.
  A throw from that disposal must never fall into `HandleRelaySetupFailureAsync`, which would inject a
  client close against an already-retired session.
- **Registration releases on partial failure.** `TcpRedirectSetup.RegisterSessionAsync` returns
  `ValueTask<TcpRedirectSession?>`; a construction fault after the listener/alias was claimed releases it
  through `store.ReleaseAssociationAsync` exactly once, and the success path releases nothing. An
  already-disposed store is not a fault: `TryRegister` returns `null` after retiring the session and
  releasing its self-traffic token, and the caller runs the session's lifetime drain (the store's
  registration is synchronous and may not discard an awaitable).

## SOCKS5 control-socket handoff timeouts (fixed 2026-08-15)

- `Socks5ControlConnection.ConnectOnceAsync` sets `socket.ReceiveTimeout` / `socket.SendTimeout` to the
  per-attempt timeout (default 30 s; the TCP relay call site passes 10 s) as the connect/authenticate
  ceiling. .NET honors these on async socket reads/writes, so any socket handed to a long-lived consumer
  keeps that per-attempt ceiling.
- `GetUpstreamStream()` — the handoff point `TcpProxyRelayFactory.EstablishAsync` uses — **must** reset
  both to `Timeout.Infinite` before returning the stream. Otherwise an idle relay connection dies at 30 s
  with `SocketException(TimedOut)` and defeats the relay's own 30-minute stall window (M4). The CONNECT
  command (`ConnectDestinationAsync`) runs *before* the handoff, so the per-attempt window still governs
  setup.
- The UDP control socket never goes through `GetUpstreamStream()` and has no post-associate operations, so
  it is intentionally left unchanged.
- Locked by `UpstreamStreamClearsPerAttemptSocketTimeouts`, which asserts `<= 0` after the handoff: a
  disabled timeout reads back as 0 on Linux and -1 on Windows, so an exact `== -1` assertion would be
  platform-false.

## Tests Required

- `TcpProxyRelayTests` — `DisposeLeavesCompletionCompletedAndReturnsPumpBuffers` (5 s drain bound),
  `ConcurrentDisposalRunsTheOwnerTeardownOnce`,
  `PumpFaultFaultsCompletionAndEmitsExactlyOneFaultEvent` (plus the debug-gate suppression case),
  `DisposingARelayObservesItsFaultedCompletionSoItNeverEscapes` (uses `UnobservedExceptionProbe`), and
  `RelayReportsTheServerStreamBytesItWroteToTheClient`.
- `TcpRelayEndCloseTests.RelayEndKindIsStalledWhenPumpStalls` pins `Completion`'s stall fast-exit.
- `TcpRelayObservationTests` covers the **two discard paths**: the acceptor's attach-failure branch
  (`AttachFailureDisposesTheDiscardedRelay` asserts `relay.IsDisposed` — observation now *is* the relay's
  own dispose) and the intrinsic pump fault (exactly one `tcp.relay.faulted`).
- `TcpRedirectAcceptorTests.UnattachableRelayIsDiscardedAndTheSessionIsTornDown`,
  `TcpRedirectSetupTests.RegistrationFaultReleasesTheClaimedListenerAliasAndToken`.
- `TcpRedirectSessionTests` — the drained-lifetime contract (a late `Retire` after the drained lifetime is
  harmless, no `ObjectDisposedException`).
- `TcpRedirectSessionStoreTests` — the dispose/lease family (`DisposeWaitsForTheRegisteredSetupLease`,
  `TryEnterSetupIsRefusedOnceDisposed`, `RetireIsOrderedBeforeTheRelayIsDisposed`,
  `DisposeIsSingleFlightAndLateTeardownNeverReEnters`,
  `RegistrationLosingTheDisposeRaceLeavesTheAssociationReleasable`).

## Wrong vs Correct

#### Wrong

```csharp
// A refused admission classified as a stall: an ordinary teardown looks like a stall timeout
// and the acceptor injects a client RST. Rethrowing after recording leaves an abandoned pump's
// task faulted with nothing left to reap it.
if (!_scope.TryEnter(out var lease)) return PumpResult.Stalled;
catch (Exception exception) { _scope.RecordFault(exception); throw; }
```

#### Correct

```csharp
// Refusal = disposal already began, so report a clean end. Faults are recorded AND reported as a
// result, so no pump task can ever be faulted-but-unobserved.
if (!_scope.TryEnter(out var lease)) return PumpResult.Ended;
catch (OperationCanceledException) { return PumpResult.Stalled; }
catch (Exception exception) { RecordPumpFault(exception); return PumpResult.Faulted; }
```
