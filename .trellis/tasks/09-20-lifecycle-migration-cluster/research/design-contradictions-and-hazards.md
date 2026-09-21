# Research: design §4 vs. actual code — contradictions and migration hazards

- **Query**: C3 — places where `design.md` §4 claims disagree with the working tree, plus migration hazards the design does not mention
- **Scope**: internal
- **Date**: 2026-09-21

> Prominence note: item **C1** (allocation-gate prescription incompatible with `ConfigureAwait(false)`) and
> **C2** (D7 vs the §4.3 omission of `TcpRedirectSessionStore._shutdown`) are the two findings most likely to
> change C3's plan. Hazards H1–H12 are not in the design.

## Contradictions / disagreements with `design.md`

### C1 (prominent) — the prescribed "single-threaded SynchronizationContext" fix cannot capture the measured chain
`hot-path.md:248-262` and `prd.md:26` prescribe installing a single-threaded `SynchronizationContext` around
the `EstablishedUdpDatagramPathAllocatesNoManagedBytes` window. The measured chain is entirely
`ConfigureAwait(false)`:
`NdisPacketActionExecutor.ProxyAsync` `Capture/NdisPacketActionExecutor.cs:356` awaits
`HandleUdpProxyAsync(...).ConfigureAwait(false)` `:360`; that awaits
`udpProxy.TrySendSpanAsync(...).ConfigureAwait(false)` `:450`; the send tail awaits
`send.ConfigureAwait(false)` (`UdpProxySession.cs:171`, `UdpProxyCoordinator.Send.cs:121`).
`ConfigureAwait(false)` does not capture a `SynchronizationContext`, so installing one cannot force these
continuations back to the measuring thread. Only the second PRD option — "assert a bound plus the
thread-independent `SpanSends` counter" — is directly actionable. Details: `allocation-gate-hardening.md`.

### C2 (prominent) — D7 vs §4.3: `TcpRedirectSessionStore._shutdown` is a CTS the mapping omits
D7 (`design.md:98-101`, `async-lifetime.md:100-101`) says *only* `QuiescenceScope` owns a
`CancellationTokenSource`. `TcpRedirectSessionStore` owns two independent lifetime objects:
`_shutdown` (`TcpRedirectSessionStore.cs:32`) and the setup-drain TCS/`_inflightSetups`. §4.3's "Today" column
for the store lists only `_inflightSetups + _setupsDrained + _disposeTask` (`design.md:323`) — `_shutdown` is
absent, although `_shutdown` is the store's actual per-owner CTS and `ShutdownToken` (`:47`) is consumed by
the coordinator at five sites. Under D7 it must be replaced by the scope's owned CTS. The mapping is
incomplete.

### C3 — §4.2 vs §4.3/D9 on `UdpProxySession._receiveFailure`
§4.2 says the receive loop "keeps its tail behaviour of writing `_receiveFailure` and then invoking the
signal" (`design.md:299-300`). §4.3's "After" column for `UdpProxySession` lists only "scope (accounting +
drain + owned CTS) + `_expiring`" (`design.md:324`) and omits `_receiveFailure`; D9 says fault observation is
intrinsic to the child body via `RecordFault`. Whether `_receiveFailure` stays (a session-level admission
signal read by `State` `:112` and `SendSpanAsync` `:143`) or becomes `scope.Fault` is therefore
self-contradictory across §4.2 and §4.3/D9.

### C4 — §4.3 "scope tracks both pumps" vs §4.4/§6 "Completion keeps relay-level semantics"
§4.3 says for `TcpProxyRelay`: "scope tracks both pumps" (`design.md:326`). §4.4 says `ITcpRelay.Completion`
keeps its relay-level semantics and the stall fast-exit is deliberate (`design.md:357-359`), and §6 warns
"DrainAsync awaits pumps that today nobody awaits" (`design.md:389-391`). If both pumps become scope leases,
the drain necessarily awaits the pump the stall fast-exit deliberately abandons — the exact hang §6 warns
about. The design does not resolve how the abandoned pump is both "tracked" and non-blocking for
`Completion`.

### C5 — §4.4 snippet conflates a sealed refusal with a stall
The illustrative pump body (`design.md:347-352`) does `if (!_scope.TryEnter(out var lease)) return PumpResult.Stalled;`.
`PumpResult.Stalled` drives `EndKind = Stalled`, and `TcpRedirectAcceptor` treats any non-`CleanEnded`
`EndKind` as needing a client reset (`TcpRedirectAcceptor.cs:222-232`). A scope-sealed refusal (a normal
disposal) would then be classified as a stall and inject a client RST. The design does not distinguish the
two.

### C6 — line-number drift (minor)
- `design.md:356` cites the `RCS1075` pragma + empty catch at `TcpProxyRelay.cs:320-329`; actual pragma
  `:324`, catch `:325-328`, restore `:329`.
- `design.md:305` cites the "every handler registered before snapshot" comment at
  `UdpProxyCoordinator.cs:304-306`; actual comment is `:303-307`.
- `design.md:300` cites the loop tail at `UdpProxySession.cs:302-313`; actual fault write `:301`, signal
  block `:308-313`.
- `prd.md:26` cites the measured window at `HotPathAllocationGateTests.cs:106-111`; actual window is
  `:105-109` (asserts through `:121`).

### C7 — §2 surface of `RecordFault` is stale
`design.md:54` shows `public void RecordFault(Exception exception);`; actual is
`QuiescenceScope.cs:91` `RecordFault(Exception exception, string? site = null)`. The spec
(`async-lifetime.md:73`) and D9 include the site.

### C8 — §2 "double-dispose is harmless" is imprecise
`design.md:74-76` says `WorkLease` "is idempotent, so a double-dispose is harmless." Actual/spec: it is
idempotent **per copy**; disposing two different copies of one lease releases the scope twice
(`QuiescenceScope.cs:19-22`, `async-lifetime.md:187-189`). `design.md:121-125` states the mutable/copy rule
correctly, so the §2.2 bullet is a local inconsistency.

## Migration hazards not mentioned in the design

### H1 — `TcpProxyRelay.RunPumpAsync` leaks its `pumpCancellation` CTS
`using var pumpCancellation = new CancellationTokenSource();` does not exist; `TcpProxyRelay.cs:136` is a bare
`using`-less local with no `Dispose` on any of the three exit paths. Migrating to a scope-owned CTS would
incidentally fix a per-relay CTS+timer leak the design does not note.

### H2 — store `_disposed` admission is not covered by the `_disposeTask` → scope mapping
`TcpRedirectSessionStore.DisposeAsync` marks `_disposed = true` under `_gate` `:151`; `_disposed` gates
`TryRegister` `:84`, `RemoveExpiredAsync` `:120`, `TearDownSessionAsync` `:210`, `FailAssociationAsync` `:278`,
and `EnterSetup`'s throw `:62`. Replacing only `_disposeTask` with `scope.DrainAsync()` leaves `_disposed`'s
semantics to the implementer; the scope surface has no `IsSealed` property (only `TryEnter` returning false).

### H3 — coordinator teardown-fault logging changes under `Run`
Today `DrainInFlightTeardownsAsync` logs a warning for each faulting teardown (`UdpProxyCoordinator.cs:322-327`).
Under §4.2 the teardown runs via `coordinatorScope.Run(...)`, which **swallows** the child's fault and records
it in `scope.Fault` (`QuiescenceScope.RunChildAsync:200-203`, D9). Unless the body itself logs, the
per-teardown warning disappears, and the only observation point becomes the coordinator scope's `Fault`.

### H4 — the receive-failure action must not block the receive loop
`ReceiveLoopAsync` invokes the signal *after* releasing the receive-window lease (`finally` `:305`) but still
inside the loop method (`:308-313`). After `Start` becomes `Action<UdpProxySession>`, the action must return
promptly (the design's `Run(...)` returns immediately, which is correct); a synchronous action that awaited or
blocked on `session.DisposeAsync()` would deadlock against `DisposeCoreAsync`'s `await _receiveLoop` (`:236`).

### H5 — coordinator scope seal ordering is unspecified
§4.2 asserts "During coordinator shutdown the scope is already sealed" (`design.md:308-310`) but does not
give the ordering against `DisposeCoreAsync`'s steps: cancel `_shutdown` `:251`, await `slot.Completion`
`:278`, dispose sessions `:289`, drain teardowns `:292`. Sealing too early rejects teardowns still needed by
in-flight sessions; too late races the drain. `UdpProxyCoordinator` has no field for the scope today
(`_shutdown`/`_disposeTask`/`_disposed` only, `:24,28,29`).

### H6 — coordinator dispose exception propagation changes
`DisposeAsync` today faults `_disposeTask` if `DisposeCoreAsync` throws (`:240-243`) and rethrows at `:246`.
`QuiescenceScope.DrainAsync` never throws for child faults (D4), and `DisposeAsync() => new(DrainAsync())`.
Swapping the mechanism changes whether an in-dispose fault surfaces to the caller.

### H7 — store dispose exception propagation changes
`TcpRedirectSessionStore.RunDisposeAsync` sets `completion.TrySetException(exception)` `:170`, so a
`DisposeCoreAsync` fault propagates to `await DisposeAsync()`. `QuiescenceScope.DrainAsync` cannot throw
(D4). Today `DisposeCoreAsync` contains most per-session faults (`ReleaseRetiredAsync` tries/catches `:247-255`,
`AcceptLoop` catches `:194-195`), so the practical difference may be nil — but the contract changes.

### H8 — `WorkLease` copy discipline across the new teardown paths
`WorkLease` is a mutable struct whose `Dispose()` nulls its own field (`QuiescenceScope.cs:225-241`); disposing
two copies releases twice. C3 introduces leases in `TcpProxyRelay` pumps, the store's setup drain, the
coordinator's teardowns, and `UdpProxySession` sends. Storing a lease in a field, boxing it as `IDisposable`,
or passing it by value to a helper that disposes it are all silent double-release bugs. The store's
`RunDisposeAsync(TaskCompletionSource)` and `DisposeCoreAsync` structure (`:161-203`) and the coordinator's
`DisposeCoreAsync` (`:249-301`) are the highest-risk spots.

### H9 — `EnterSetup` throw → `TryEnter` bool mapping
`SetupPendingAsync` catches `ObjectDisposedException` from `EnterSetup` to detect disposal-between-retain-and-start
(`TcpProxyCoordinator.cs:259-269`). `TryEnter` returns `false` instead (D2), so that `catch` becomes dead and
the `_pendingSyn.Complete(..., writeCooldown: false, ...)` + `return` must move to the `false` branch. Also
`ExitSetup`'s `finally` (`:280-283`) must become `lease.Dispose()`, keeping `_pendingSyn.Complete` (`:278`)
before the release.

### H10 — `UdpSessionSetup` failure path calls `RemoveSlotAsync` directly, not via `Run`
`CreateSessionAsync`'s catch awaits `host.RemoveSlotAsync(flow, slot, reason)` (`UdpSessionSetup.cs:113`).
That is a direct gate-taking call, not a fire-and-forget teardown; if the coordinator scope is sealed during
shutdown, the setup's failure path must still be able to remove the slot (it does not need `Run`). Do not
route it through the scope; the design's "only the teardown moves to the coordinator's scope" refers to the
receive-failure teardown, not the setup-failure slot removal.

### H11 — session `State` gate consistency if `_receiveFailure` becomes `scope.Fault`
`State` reads `_disposed`, `_receiveFailure`, `_expiring` under `_activityGate` (`UdpProxySession.cs:109-115`),
and `SendSpanAsync` reads `_receiveFailure` under the same gate `:141-145`. `QuiescenceScope.Fault` is read
lock-free (`Volatile.Read`, `:49`). If `_receiveFailure` is replaced by `scope.Fault`, the admission decision
leaves the activity gate, which changes the race relationship between a receive fault and a concurrent send.
The design does not address this.

### H12 — accept-loop ownership under the scope is unspecified
`setup.Session.AcceptLoop = _acceptor.RunAcceptLoopAsync(setup.Session)` (`TcpProxyCoordinator.cs:312`) starts
the loop on the setup worker and stores the `Task` on the session; the loop's `finally` disposes the session
lifetime CTS (`TcpRedirectAcceptor.cs:57`), and the store awaits `session.AcceptLoop` in `DisposeCoreAsync`
(`TcpRedirectSessionStore.cs:193`). If `AcceptLoop` becomes a scope child, it is unclear whether the owning
scope is the session's or the store's; the store's dispose currently relies on the property read `:191,259`
and on `ReleaseRetiredAsync`'s `if (session.AcceptLoop is null) session.DisposeLifetime()` `:259`.

## Caveats / Not Found

- No claim here is an implementation instruction; these are observations for the main agent to weigh.
- Design lines cited are from `design.md` as read on 2026-09-21; code lines from the working tree at the same
  time.
