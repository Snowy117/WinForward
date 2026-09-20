# Design — Transport lifecycle hardening

Technical design for `.trellis/tasks/09-20-transport-lifecycle`. Requirements live in `prd.md`;
this file records boundaries, contracts, decisions, and tradeoffs. Execution ordering lives in
`implement.md`. Line numbers were captured from the 2026-09-20 review; re-verify each at edit time.

## Scope

Three coupled changes to the TCP / UDP / SOCKS5 transport lifecycle:

1. One explicit quiescence boundary per coordinator (R1, R2).
2. UDP session lifecycle as an explicit state model with a teardown reason (R3, R4, R5).
3. Ownership consolidation into composition, removing the `_ownsX` flags (R6), plus the concrete
   lifecycle-hole fixes (R7).

Non-goals: local mux/pooling, new features, config/API change, line-count-only refactors.

## Verified composition facts

- `DurableCaptureBundle` creates all four native pools and the single `SetupExecutor`
  (`DurableCaptureBundle.cs:113,116,124,186,190`) and disposes them
  (`:132-134`, `:144-146`, `:199-200`, `:210-211`, `:365-366`, `:377-379`).
- `SetupExecutor` is a **single instance shared by both coordinators**: created once at
  `DurableCaptureBundle.cs:124`, injected into TCP via `TcpRedirectComposer.cs:53` and UDP via
  `UdpProxyComposer.cs:65`. `Socks5AddressCache` is likewise shared.
- Therefore no single coordinator can own the executor; composition must be its owner.
- The two composer doc comments claiming "the coordinator owns the pools exactly as it always has"
  (`TcpRedirectComposer.cs:16`, `UdpProxyComposer.cs:16`) are **inaccurate** and must be corrected.
- Idle cadence: UDP relay idle threshold = 2 min, sweep interval = 1 min
  (`IdleExpirySweeper.cs:49`, `:46`); TCP redirect idle = 5 min, relay = 2 min (`:47-48`).

## Decision Log

### D1 — Ownership model: composition owns; coordinators borrow (DECIDED 2026-09-20)

Chosen **Option A** (user decision). Full rationale in §3.

### D2 — Quiescence mechanism: ownership-await tree (DECIDED 2026-09-20)

Chosen **Option A** (user decision). Full rationale in §1. No shared/global task registry; the
await tree already covers accept loops, receive loops, and setup completions.

### D3 — UDP expiry-vs-in-flight-send: explicit drop (DECIDED 2026-09-20)

Chosen **Option B** (user decision). On the `_expiring` race, do **not** throw; count + rate-limited
log the drop. Rationale:

- A flow can only reach `_expiring` after being idle ≥ 2 min (`IdleExpirySweeper.cs:49`), so the
  losing datagram belongs to an already-dying flow.
- The race window is the purely synchronous gap between `_expiring = true`
  (`UdpProxySession.cs:173`) and slot removal (`UdpProxyCoordinator.cs:385`) — sub-microsecond.
- Estimated incremental loss: `δ / T_sweep ≈ 1e-6 / 60 ≈ 1.7e-8` per idle→resume event; expected 0,
  bounded ~1 datagram per event. Once the slot is gone, resumed traffic re-setups and is delivered.
- Option A (re-buffer into a replacement setup) was rejected: it re-dials SOCKS5 for a straggler of
  a just-expired idle flow and requires a synchronous copy of the native span.

## Design

### 1. Quiescence boundary (R1, R2) — ownership-await tree

**1.1 TCP relay pump gets an owner.** `TcpProxyRelay.DisposeAsync` (`TcpProxyRelay.cs:302-311`)
becomes: idempotent `Interlocked` guard → dispose `_localSocket` → `await _control.DisposeAsync()` →
`try { await Completion } catch (Exception) { }`. Disposal closes the socket/upstream, which
terminates the pumps; awaiting `Completion` makes the boundary real. The fault disposal manufactures
is already logged by `TcpRelayFaultObserver`, so the await swallows it. New contract: after
`TcpProxyRelay.DisposeAsync` completes, no pump task is running and `Completion` is completed.

**1.2 Fault observer per discard path (corrected during Phase B).** The two
`TcpRelayFaultObserver.Observe` call sites — `TcpProxyRelay.DisposeAsync` and
`TcpRedirectAcceptor`'s attach-failure branch — are **distinct discard paths, not duplicates**:
the acceptor may discard an arbitrary `ITcpRelay` whose `DisposeAsync` need not observe. Keep
exactly one registration per discard path (verified by `TcpRelayObservationTests` /
`AttachFailureObservesFaultedRelayCompletion`). `ContinueWith`-based registration is not
idempotent, so no single path may register twice. Preserve the documented .NET gotcha: the
observer must read `task.Exception` before any `IsEnabled` logger gate (attaching an
`OnlyOnFaulted` continuation does not mark the exception observed).

**1.3 Acceptor end-handling gets an owner.** Today
`TcpRedirectAcceptor.RunAcceptLoopAsync` (`TcpRedirectAcceptor.cs:18`) does
`_ = ObserveRelayCompletionAsync(...)` (`:84`) then a terminal `DrainRedundantConnectionsAsync`
(`:85`). Change it to **await** both as its terminal steps (no discard). Then `session.AcceptLoop`
spans the whole session lifetime, so the store's existing `await session.AcceptLoop`
(`TcpRedirectSessionStore.cs:191`) becomes a true quiescence wait and no callback can re-enter the
store after disposal. Teardown stays idempotent via `TryRetireSessionUnderGate`'s `ReferenceEquals`
guard (`TcpRedirectSessionStore.cs:209-213`).

**1.4 Store lifetime-disposal ordering.** `ReleaseRetiredAsync` must not `DisposeLifetime()`
(`TcpRedirectSessionStore.cs:249`) while the accept loop can still read `session.Token`
(`ClientResetInjector.cs:36`, `TcpRedirectAcceptor.cs:72`). `Retire()` already cancels the lifetime
(`TcpRedirectSession.cs:28`); defer only the CTS **dispose** to `DisposeCoreAsync` after
`await session.AcceptLoop`. Add an `IsDisposed` gate so any late teardown entry no-ops.

**1.5 UDP per-session lifetime token (enabler for R3).** `UdpProxySession` gains
`CancellationTokenSource _lifetime = CreateLinkedTokenSource(context.Shutdown)`, mirroring
`TcpRedirectSession.Lifetime` (`TcpRedirectSession.cs:19`). The receive loop (`UdpProxySession.cs:210`)
and injections (`:268`) use `_lifetime.Token`; `BeginExpiry` cancels it; `DisposeAsync` cancels and
disposes it. Catch guards become `when (_lifetime.IsCancellationRequested)` → normal teardown
(expiry *or* shutdown); the generic catch sets `_receiveFailure` only for genuine faults. This
removes the spurious `_receiveFailure` on idle expiry (today the ODE guard at `:242` depends on
`_shutdown`, which is not cancelled during expiry).

**1.6 Residual UDP receive-failure handler.** Today `_ = receiveFailureHandler(this)`
(`UdpProxySession.cs:259`) is unobserved. After 1.5 it fires only on genuine faults. Give it an owner
in the coordinator: the UDP coordinator tracks in-flight slot-removal tasks in a per-coordinator set
and awaits that set in `DisposeCoreAsync`. This is the per-coordinator fallback D2 permits; never a
global registry. The loop itself must keep **not** awaiting the handler (that would re-enter session
disposal, per `UdpProxySession.cs:257-258`).

### 2. UDP session state model + teardown reason (R3, R4, R5)

**2.1 Explicit states.** Document and expose the state vocabulary:
- Slot level (under coordinator `_gate`): `SettingUp` (`slot.Session == null`) → `Ready`
  (`slot.Ready == true`, session attached).
- Session level (under `_activityGate`): `Active` → `Expiring` → `Faulted` / `Disposed`.
Introduce `enum UdpSessionState { SettingUp, Active, Expiring, Faulted, Disposed }` and a `State`
accessor computed under the owning lock, with the transition rules documented in one place. The
existing fields (`Ready`, `_expiring`, `_receiveFailure`, `_lifetime`) remain the implementation;
the enum makes the model explicit without a risky rewrite.

**2.2 Teardown reason as data.** Replace `RemoveSlotAsync`'s `armCooldown` bool
(`UdpProxyCoordinator.cs:377`) with `enum UdpTeardownReason { SetupFailure, Expiry, Fault, Shutdown }`.
`SetupFailure` arms the cooldown (`:397-400`); the rest do not. Logging and metrics key off the
reason instead of inferring from `OperationCanceledException`.

**2.3 Send returns sent/not-sent instead of throwing (R4).** `UdpProxySession.SendSpanAsync`
(`UdpProxySession.cs:116`) changes to `ValueTask<bool>`: `true` = sent; `false` = not sent because
`_expiring` (`:122`) or `_receiveFailure` (`:121`). No `IOException` for these precondition cases.
`UdpProxyCoordinator.SendOnReadySessionSpanAsync` (`UdpProxyCoordinator.Send.cs:82`) treats `false` as
a counted, rate-limited drop and does **not** remove the slot (Expiry → the sweeper owns removal;
Faulted → the failure handler owns removal). A genuine `_transport.SendSpanAsync` exception keeps the
existing remove-slot + rethrow path (`:95`, `:130-134`).

**2.4 Per-setup cancellation (R5).** TCP: assign `item._cancellationToken = _store.ShutdownToken` in
`LaunchSetup` (`TcpProxyCoordinator.cs:224-229`) so `TrySetCanceled(item._cancellationToken)`
(`:232`) carries a meaningful token (parity with `UdpProxyCoordinator.cs:125`). UDP: setup keeps
`_shutdown`; session cancellation is now the per-session `_lifetime` (1.5). This distinguishes
shutdown, per-setup, and per-session cancellation.

### 3. Ownership consolidation (R6)

Coordinators take **required non-null** pools/executor. Delete `_ownsSynCopyPool` /
`_ownsSetupExecutor` (`TcpProxyCoordinator.cs:67-69`) and `_ownsSetupQueuePool` /
`_ownsReceiveWindowPool` / `_ownsSetupExecutor` (`UdpProxyCoordinator.cs:64-69`), their self-creation
branches, and the creation-failure pool disposal (the bundle already owns those paths:
`DurableCaptureBundle.cs:132-134,144-146,199-200,210-211,365-366,377-379`). New `DisposeAsync`
contract: *quiesce own background work and dispose only internally-constructed state; never dispose
injected collaborators.* Add a `_disposed` guard to `TcpProxyCoordinator` (absent today:
`TcpProxyCoordinator.cs:636-650`; the store is already single-flight). Fix the two misleading
composer comments.

### 4. Concrete lifecycle-hole fixes (R7)

- **Attach-failure leaves a half-open session** (`TcpRedirectAcceptor.cs:73-82`): on
  `tryAttachRelay` false, close the accepted connection and call `tearDownSession(session)`; move the
  relay/accepted disposal outside the outer `try` so a throw there does not fall into
  `HandleRelaySetupFailureAsync` against a retired/attached session.
- **`RegisterSession` partial failure** (`TcpRedirectSetup.cs:167-169`): wrap self-traffic-token
  registration + session construction so a throw releases the already-claimed listener/alias via
  `store.ReleaseAssociationAsync`.
- **Cooldown re-written after `RemoveAll`** (`TcpProxyCoordinator.cs:304` vs `:307`, `:647`): run
  `_pendingSyn.Complete(..., writeCooldown, ...)` before `ExitSetup()` unblocks the drain, or gate
  cooldown writes on `!store.IsDisposed`.
- **Late `Token` read** is covered structurally by 1.4.

## Interfaces / signatures changed

| Member | Change |
|---|---|
| `TcpProxyRelay.DisposeAsync` | awaits `Completion` (behavioral) |
| `UdpProxySession.SendSpanAsync` | `ValueTask` → `ValueTask<bool>` |
| `UdpProxyCoordinator.RemoveSlotAsync` | `bool armCooldown` → `UdpTeardownReason reason` |
| new `enum UdpTeardownReason`, `enum UdpSessionState` | added (internal) |
| `TcpProxyCoordinator.LaunchSetup` | sets `item._cancellationToken` |
| coordinator ctors | mandatory non-null pool/executor deps |
| `TcpRedirectComposer.cs:16`, `UdpProxyComposer.cs:16` | comment correction |

## Compatibility

- No public API / config / schema change; `ITcpRelay` keeps its shape.
- Hot path: no new allocation (bool return is a struct; the relay await runs only on the cold
  teardown path). Existing zero-allocation gates and batching contracts must stay green.
- File-size and structure rules unchanged (≤400 effective lines, interface beside implementation).

## Test strategy

- **Relay quiescence**: disposing a relay against a real loopback socket leaves `Completion`
  completed and no pump task running.
- **Fault observer**: registered exactly once per relay (assert single observation).
- **Store quiescence**: after `DisposeAsync`, no callback re-enters teardown (tombstone/reset
  counters frozen).
- **UDP expiry**: idle-expiry records no `_receiveFailure` and fires no handler; the loop ends via
  `_lifetime` cancellation (normal path).
- **UDP send race**: expiry-vs-send returns `false` (counted drop) with no exception reaching the
  dispatcher.
- **Cancellation**: TCP setup item token equals the store shutdown token.
- **Ownership**: a fake pool/executor with a dispose counter proves coordinator `DisposeAsync` does
  not dispose injected collaborators; repeated `DisposeAsync` is a no-op.
- **Attach-failure**: store session count returns to 0.
- **Regression**: full Release test suite green; format + inspectcode gates clean.

## Rollback shape

Each phase in `implement.md` is one commit and independently revertible. Exception: the §3 constructor
change ripples into tests, so it lands before the phases that add new tests. If the UDP send-result
change (§2.3) proves too invasive, it can be dropped while keeping §1.5 (which alone removes the
false-failure path).
