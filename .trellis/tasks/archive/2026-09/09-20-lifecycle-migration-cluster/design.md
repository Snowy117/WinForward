# C3 — Migrate the TCP/UDP lifecycle cluster to `QuiescenceScope`

Parent `design.md` §4 is the plan of record; this document is the executable per-owner version.
Parent §4 was **corrected on 2026-09-21** against the working tree (revision note in its §4.0) — the
relay-pump tracking rule, the sealed-refusal value, the UDP failure representation, and the store's CTS
ownership were all settled from code evidence, not from the earlier sketch. Research evidence for every
claim below lives in `research/`: `cluster-lifetime-inventory.md`, `relay-completion-semantics.md`,
`udp-setup-tracking.md`, `receive-failure-path.md`, `existing-tests.md`,
`allocation-gate-hardening.md`, `design-contradictions-and-hazards.md`.

## 1. Deliverable, boundary, and decisions

Ships:

1. The cluster's lifetime owners migrated to `QuiescenceScope`: `TcpRedirectSessionStore`,
   `TcpRedirectSession`, `TcpProxyRelay`, `UdpProxySession`, `UdpProxyCoordinator` (+ `UdpSessionSlot`),
   `UdpSessionSetup`.
2. `TcpRelayFaultObserver.cs` **deleted**; `TcpProxyRelay.ObservePump` deleted; the `RCS1075` pragma and
   the empty `catch` in `TcpProxyRelay.DisposeAsync` deleted; `TcpRedirectAcceptor.cs:120` deleted.
3. Every `WF0001`/`WF0002`/`WF0003` allowlist section for these files removed from `.editorconfig`,
   leaving only the permanent `QuiescenceScope.cs` exemption.
4. The allocation-gate hardening of §3, landed **before** any owner migration.
5. One additive primitive change: `QuiescenceScope.IsSealed` (§2).
6. Spec updates (§8).

In scope but not owners: **call-site edits** in `TcpProxyCoordinator` (`:259-283`, `:312`) and
`TcpRedirectAcceptor` (`:120`, and the `AcceptLoop` read at `:57`). Neither gains a scope; both stop
relying on the `EnterSetup` `ObjectDisposedException`.

Not shipped: packet-path behaviour, configuration, `LayeredCaptureRunner` / `MultiAdapterCaptureLoop`
(C4), and anything outside the cluster. `UdpSessionSetup` is not itself a lifetime owner — it is reached
through the coordinator's scope (§4.5).

Boundary check: after C3, no `_ =`, `.ContinueWith`, `Task.Run` or `Task.Factory.StartNew` remains under
`src/WinForward.Runtime/{TcpRedirect,UdpProxy}/`, and the six temporary allowlist sections are gone.

### Decisions made by this child

| # | Decision | Rationale |
|---|----------|-----------|
| D-C3-1 | Add `QuiescenceScope.IsSealed`; owners delete their `_disposed` bools and read it (§2) | The program's point is one source of truth for "closed". Without it, every owner keeps a parallel admission flag that can drift from the seal. Additive and cold-path. |
| D-C3-2 | Owner teardown work keeps today's exception propagation; **child** faults are only recorded (parent D4 applies to children only) | `DrainAsync` cannot throw, so folding owner work into it would silently swallow e.g. a socket-close failure. |
| D-C3-3 | Both relay pumps are scope children; `Completion` keeps its relay-level semantics; the drain is bounded by the socket close (§4.3) | Resolves C4: the abandoned pump is joined by `DisposeAsync`'s drain, and it returns because `_localSocket` was already disposed. |
| D-C3-4 | A sealed-refusal in the pump must not report a stall (§4.3) | Resolves C5: `TcpRedirectAcceptor.cs:222-232` injects a client RST for any non-`CleanEnded` `EndKind`; an ordinary teardown must not look like a stall. |
| D-C3-5 | UDP receive failure has one representation: `scope.Fault`; `_receiveFailure` is deleted, and every reader reads `scope.Fault` **under `_activityGate`** (§4.4) | Resolves C3 and H11: single cause, and the lock preserves today's race relationship between a fault and a concurrent send. |
| D-C3-6 | `TcpRedirectSessionStore._shutdown` and `TcpProxyRelay.pumpCancellation` are replaced by scope-owned tokens (§4.1, §4.3) | Resolves C2 and H1: D7 ("only the scope owns a CTS") applied, and a per-relay CTS leak removed. |
| D-C3-7 | `UdpSessionSetup`'s setup-failure `RemoveSlotAsync` stays a **direct** call, not a scope `Run` (§4.5) | Resolves H10: it is a gate-taking decision, not a fire-and-forget teardown; it runs inside the already-scoped setup task. |
| D-C3-8 | The coordinator scope seals at a defined point in `DisposeCoreAsync`, before the teardown drain (§4.5) | Resolves H5: sealing too early strands in-flight teardowns, too late races the drain. |
| D-C3-9 | The per-teardown warning log moves **inside** the `Run` body (§4.5) | Resolves H3: `Run` records faults but cannot log the owner's domain-specific warning. |

## 2. Primitive addition — `IsSealed` (D-C3-1)

```csharp
// src/WinForward.Runtime/QuiescenceScope.cs
public bool IsSealed => (Volatile.Read(ref _state) & Sealed) != 0;
```

- Cold-path only; adds no field, no allocation, and does not touch `TryEnter`/`Exit`.
- `DrainAsync` is the join; `IsSealed` is the *admission* predicate. They are deliberately different:
  a caller can observe sealed before the drain completes, which is what makes a late `TryEnter` refusal
  meaningful (parent D2).
- `async-lifetime.md` gains the member in the surface list and a sentence distinguishing it from
  `IsIdle`.
- Test: seal → `IsSealed` true before the drain completes; a never-sealed scope reports false; and the
  allocation gate stays green.

## 3. Allocation-gate hardening (prerequisite, first step) — **LANDED 2026-09-21**

`HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` measures 64 dispatches
across 64 `await`s (`HotPathAllocationGateTests.cs:105-109`, driven by `CountingTransport` `:423-448`)
while reading `GC.GetAllocatedBytesForCurrentThread()`. The measured chain is entirely
`ConfigureAwait(false)` (`NdisPacketActionExecutor.cs:360,450`), so a single-threaded
`SynchronizationContext` **cannot** capture the continuations; the parent's earlier prescription is
infeasible and `hot-path.md` has been corrected.

The planned branch rule was (1) thread-stable by construction — assert `IsCompletedSuccessfully` at each
seam, keep the exact `Assert.Equal(0, allocated)` and the thread-independent `SpanSends` counter — with
(2) a documented bound as the fallback only if the path must genuinely yield. **Branch 1 landed**, and
the gate now passes in isolation (10/10 runs) and in the full suite.

**Root-cause correction (measured while landing this step).** The framing that motivated "thread-stable
by construction" — that the per-thread counter is invalid because a continuation migrates to another
pool thread — was **wrong**. Across 40 instrumented 64-dispatch loops (8 runs × 5) the managed thread id
was constant, and the per-thread delta was non-zero **only in the first batch**, where it always
coincided with a first-batch send-count shortfall (`SpanSends` delta 17/40/67 where 64 was expected).
The real mechanism is **readiness**: while a session is not yet `Ready`, datagrams take the bounded
drop-oldest setup queue and are flushed later by the background pipeline, so an early window rides the
cold setup path — which allocates once and leaks sends into the window. The landed gate therefore proves
direct admission (`SpanSends` advanced by exactly one with `Diagnostics.PendingSetupBytes == 0`), then
opens the window only on an allocation-stable probe batch (bounded retries), while keeping the exact
0-byte assertion plus the thread-id and `SpanSends`-delta assertions. The thread check is retained as
cheap insurance, not because migration was ever observed. Readiness is not a licence to relax the
threshold: a real per-packet allocation never stabilizes, so the bounded loop fails (verified by
injecting `new byte[1]` on the warm path — the gate then reports "the UDP warm path never became
allocation-stable").

This step is done and green before any owner migrates, because every later step changes the path this
gate protects.

## 4. Per-owner migration

Order matters: each step leaves the build green and the suite passing.

### 4.1 `TcpRedirectSessionStore` — the root scope (owns the CTS)

Today: `_shutdown` CTS `:32`, `_setupsDrained` TCS `:33`, `_disposeTask` `:34`, `_inflightSetups` `:35`,
`_disposed` `:36`, `EnterSetup`/`ExitSetup` `:58-73`, `RunDisposeAsync` `:161-172`,
`DisposeCoreAsync` `:174-203`.

After:

- `_scope = new QuiescenceScope()`; the scope owns the CTS (D-C3-6), so `_shutdown` is deleted and
  `ShutdownToken` (`:47`) becomes `_scope.Token`. The five coordinator consumers keep reading that
  property — its meaning is unchanged.
- `_inflightSetups`, `_setupsDrained`, `_disposeTask`, `_disposed` are deleted (D-C3-1).
- `EnterSetup` becomes `bool TryEnterSetup(out WorkLease lease)` and `ExitSetup` becomes
  `lease.Dispose()` at the call site. The coordinator's `catch (ObjectDisposedException)`
  (`TcpProxyCoordinator.cs:259-269`) is replaced by a `false` branch with the same body
  (`_pendingSyn.Complete(..., writeCooldown: false, ...)` then return); `ExitSetup`'s `finally`
  (`:280-283`) keeps `_pendingSyn.Complete` (`:278`) **before** the release (H9).
- The other four `_disposed` guards (`TryRegister` `:84`, `RemoveExpiredAsync` `:120`,
  `TearDownSessionAsync` `:210`, `FailAssociationAsync` `:278`) read `_scope.IsSealed` instead.
- `DisposeAsync` → `_scope.Cancel(); await _scope.DrainAsync();` — the drain is identity-stable
  single-flight (C1's contract), so `_disposeTask` and `RunDisposeAsync` disappear.
- `DisposeCoreAsync`'s ordered teardown (cancel → drain setups → retire sessions → await accept loops)
  is preserved; the setup drain is now the scope's join. `ReleaseRetiredAsync`'s
  `if (session.AcceptLoop is null) session.DisposeLifetime()` (`:259`) becomes
  `session.DisposeLifetime()` — the second caller is a no-op because the scope's drain is
  identity-stable, which is a **stronger** guarantee than the old null check (D-C3-2).

### 4.2 `TcpRedirectSession` — a nested scope

Today: `Lifetime` linked CTS `:19`, `_retired` `:20`, `_lifetimeDisposed` `:21`, `AcceptLoop` `:23`,
`Retire()` `:27-39`, `DisposeLifetime()` `:41-44`.

After: `_scope = new QuiescenceScope(parent)` where `parent` is the store's scope token; `Token` =
`_scope.Token`; `Retire()` = `_scope.Cancel()`; `DisposeLifetime()` = `_scope.DisposeAsync()`; the three
fields are deleted. `AcceptLoop` stays the property it is today (it is read by the store and by the
acceptor), and the loop's body takes a lease on the session's scope, so the session's drain joins it
(H12). The accept loop's `finally` (`TcpRedirectAcceptor.cs:57`) calls `DisposeLifetime()`, which is now
idempotent by the scope's single-flight.

### 4.3 `TcpProxyRelay` — pumps tracked, abandonment preserved

Today: `_disposed` `:114`, `Completion` `:126,129`, `pumpCancellation` `:136` (leaked, H1), the three
exit paths `:144-172`, `ObservePump` `:287-294`, `_ = ShutdownSend(...)` `:284`, `DisposeAsync`
`:302-331` (stale comment `:303-307`, `RCS1075` pragma `:324`, empty catch `:325-328`).

After:

- `_scope = new QuiescenceScope()`; `pumpCancellation` is deleted and both pumps take the scope's token
  (D-C3-6), which also removes the leak. `_disposed` becomes `_scope.IsSealed`.
- `PumpAsync` becomes an **instance** method (it needs `_scope`) and takes a lease:
  `if (!_scope.TryEnter(out var lease)) return PumpResult.Ended;` … `finally { lease.Dispose(); }`.
  Returning a **clean end** on refusal is D-C3-4: the refusal means disposal already began, and anything
  other than `CleanEnded` would make `TcpRedirectAcceptor.cs:222-232` inject a client RST for an ordinary
  teardown. The branch is unreachable in practice — both pumps start synchronously inside the
  constructor (`:137-138`), before the relay can be sealed — so it exists to make the worst case
  harmless, not to model a live state.
- Each pump body records its own fault: `catch (Exception exception) { _scope.RecordFault(exception); throw; }`.
  This is what deletes the *external* observation mechanisms.
- `ObservePump` (`:287-294`), `_ = first.ContinueWith(...)` (`:289`), and `_ = ShutdownSend(...)` (`:284`,
  now the synchronous `ShutdownSend(Socket)` overload) go.
- `RunPumpAsync`'s **three exit paths are unchanged in behaviour**: path 1 (stall fast-exit `:144-150`)
  still `return`s without awaiting the other pump, so `Completion` still completes on a stall without
  waiting for both pumps; path 2 still `WhenAll`s; path 3 still rethrows.
- `DisposeAsync` (`:302-331`) becomes: seal + `_scope.Cancel()` → `_localSocket.Dispose()` →
  `await _control.DisposeAsync()` → `await _scope.DrainAsync()`. **The socket close before the drain is
  what makes the drain bounded**: it forces the pump abandoned by path 1 to return (D-C3-3). Deleted:
  the `RCS1075` pragma, the empty `catch`, the `catch (Exception) { }` swallow, and the stale comment.

`ITcpRelay.Completion` and `RelayEndKind` are untouched.

### 4.4 `TcpRelayFaultObserver` — **file deleted**, and the acceptor

`TcpRelayFaultObserver.Observe` (`:23-41`) and its `_ = relay.Completion.ContinueWith(...)` (`:27`) are
replaced by the pumps' intrinsic `RecordFault` (§4.3). `TcpRedirectAcceptor.cs:120` is deleted; the
acceptor's `DiscardUnattachedRelayAsync` (`:131-150`) already calls `relay.DisposeAsync()`, which now
drains — so a discarded relay is observed by construction rather than by an attached observer.

The parent's §4.4 finding stands as a **claim to verify, not a fact**: `DisposeAsync` already awaits
`Completion`, so the observers look redundant for observation today and their only live effect was the
`tcp.relay.faulted` debug event. §6 requires a fault-injection test that proves no unobserved task
exception results and that the event is still recorded. If the event turns out to be the observers' only
effect and is worth keeping, it is emitted from the pump's own `catch` — it must not resurrect an
observer.

### 4.5 `UdpProxySession`, `UdpProxyCoordinator`, `UdpSessionSetup`

**`UdpProxySession`** — today `_lifetime` `:46`, `_activityGate`/`_disposeGate` `:52-53`, `_receiveLoop`
`:54`, `_disposeTask` `:55`, `_receiveFailure` `:56`, `_expiring` `:66`, `_disposed` `:67`,
`_activeSends` `:68`, the loop tail `:301,308-313`.

- `_scope = new QuiescenceScope();` owns the CTS (`_lifetime` deleted); `_activeSends`/`_expiring`
  become scope accounting and owner admission respectively — `_expiring` **stays** (parent P4/D1: the
  scope never unseals, so an expiry-vs-send policy cannot live in it).
- `SendSpanAsync` takes a lease instead of `_activeSends++`; `State` and the send admission read
  `_scope.Fault` instead of `_receiveFailure`, **under `_activityGate`** (D-C3-5/H11) — the lock, not
  the scope, provides the ordering, so the fault-vs-send race is exactly today's.
- `Start`'s delegate becomes `Action<UdpProxySession>`; the loop's tail calls the signal synchronously
  after releasing the receive-window lease (`finally` `:305`), and the signal must return promptly (H4).
- `DisposeAsync` seals the scope and drains; `_disposeTask`/`_disposeGate` go.

**`UdpProxyCoordinator`** — today `_shutdown` `:24`, `_disposeTask` `:28`, `_disposed` `:29`,
`_inFlightTeardowns` `:37`, `DrainInFlightTeardownsAsync` `:308-329`, `DisposeCoreAsync` `:249-301`
(cancel `:251` → await `slot.Completion` `:278` → dispose sessions `:289` → drain teardowns `:292`).

- `_scope = new QuiescenceScope();` owns the CTS (`_shutdown` deleted); `_disposed` → `_scope.IsSealed`;
  `_inFlightTeardowns` and `DrainInFlightTeardownsAsync` are deleted.
- The receive-failure handler signature changes to `Action<UdpProxySession>` (`IUdpSessionSlotHost`
  seam `:28`, group at `UdpSessionSetup.cs:100`); the coordinator maps it to
  `_scope.Run(ct => RemoveReceiveFailedSessionCoreAsync(session), "udp.receive-failure")`. The
  per-teardown warning (`DrainInFlightTeardownsAsync:322-327`) moves **inside** the `Run` body
  (D-C3-9), because `Run` records the fault but cannot log the owner's warning.
- `DisposeCoreAsync` gains exactly one new ordered step (D-C3-8): cancel `_shutdown`/seal the scope
  **after** `await slot.Completion` (`:278`) and **before** the teardown drain. Sealing earlier would
  refuse teardowns still needed by in-flight sessions; sealing later would race the drain. The setup
  task's failure path keeps calling `host.RemoveSlotAsync` directly (D-C3-7/H10).
- Session scopes nest under the coordinator's by construction (`new QuiescenceScope(coordinatorToken)`),
  so the coordinator's drain transitively joins every session.

**`UdpSessionSetup`** — no scope of its own (it is not an owner). It reaches the coordinator only through
the `IUdpSessionSlotHost` seam, and its in-flight work is tracked by the setup executor plus the
coordinator scope (§ above, D-C3-7).

## 5. Allowlist shrink

Removed from `.editorconfig` at the end of each step: `TcpProxyRelay.cs` (WF0001+WF0002),
`TcpRelayFaultObserver.cs` (WF0001+WF0002 — section disappears with the file), `TcpRedirectSessionStore.cs`
(WF0001), `UdpProxySession.cs` (WF0001). `MultiAdapterCaptureLoop.cs` and `LayeredCaptureRunner.cs`
(WF0003) belong to C4 and stay. After C3 the file holds the four `[src/**.cs]` severities plus the single
permanent `QuiescenceScope.cs` exemption.

## 6. Verification

- **Gate hardening** (§3) green **in isolation** before any migration step.
- **Per-owner quiescence test**: for each migrated owner, `DisposeAsync` returns only after registered
  work completes. Extend the existing tests rather than replacing them (`existing-tests.md`):
  `DisposeIsSingleFlightAndLateTeardownNeverReEnters`, `ConcurrentDisposalIsSingleFlightAndRejectsNewSends`,
  `DisposeLeavesCompletionCompletedAndReturnsPumpBuffers`, `IdleExpiryEndsTheReceiveLoopWithoutRecordingAFailure`,
  `GenuineReceiveFaultRecordsFaultedAndFiresTheFailureHandler`, `TcpRelayObservationTests` (5),
  `TcpRelayEndResetTests` (7) all stay green.
- **Fault injection (F2)**: with `TcpRelayFaultObserver` deleted, a faulted pump produces **no**
  unobserved task exception and `tcp.relay.faulted` is still recorded (§4.4).
- **Drain is bounded (C4/D-C3-3)**: a relay whose stall fast-exit abandoned a pump still reaches the end
  of `DisposeAsync` — the socket close forces the abandoned pump to return.
- **No spurious reset (C5/D-C3-4)**: disposing a relay mid-transfer does not inject a client RST.
- **UDP single-cause (D-C3-5)**: `_receiveFailure` no longer exists; `State` and the send admission agree
  with `scope.Fault` under `_activityGate`.
- **Stall semantics preserved**: a stalled/cancelled pump still reaches the drain; `Completion` still
  completes without waiting for both pumps on the stall path.
- **Allocation**: `HotPathAllocationGateTests` green; no packet-path allocation delta from the UDP
  send-gate change (lease instead of `_activeSends++`).
- **Allowlist**: the four cluster sections are gone and `src/**` builds with only the permanent
  exemption.
- **Full gates**: `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`
  (exit 0, empty), `dotnet build WinForward.slnx -c Release` (0 warnings), `dotnet test
  WinForward.slnx -c Release` (green), `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml
  WinForward.slnx` (zero `<Issue ` — parse the XML; `grep -c '<Issue'` also matches `<Issues />`).

## 7. Risks

| Risk | Mitigation |
|------|-----------|
| `WorkLease` copy discipline (H8) — a lease stored in a field, boxed as `IDisposable`, or passed by value to a helper that disposes it releases twice | The highest-risk spots are the store's `DisposeCoreAsync` and the coordinator's `DisposeCoreAsync`; review each lease as a local with a single `finally`, and keep the allocation gate plus a scope-accounting test (state returns to sealed+zero). |
| The relay drain waits for a pump that never returns (C4) | The drain runs only after `_localSocket.Dispose()`, which forces the abandoned read/write to fail; the "drain is bounded" test is the evidence. |
| Sealing the coordinator scope at the wrong point strands or races teardowns (H5) | D-C3-8 fixes the point relative to the existing ordered steps; a test asserts an in-flight teardown still completes. |
| Behaviour drift in the relay's exit paths | Path 1/2/3 behaviour is asserted by the existing `TcpProxyRelayTests` / `TcpRelayEndResetTests` / `TcpRelayObservationTests`; any change there is a regression, not a design choice. |
| `IsSealed` tempts callers to treat "sealed" as "drained" | §2 states the distinction; `async-lifetime.md` records it next to `IsIdle`. |
| Line drift in this document | Every anchor was re-verified on 2026-09-21; a shifted anchor is reconciled against the code, never forced. |

## 8. Spec updates

- `async-lifetime.md`: `IsSealed` in the surface + the sealed-vs-drained distinction; per-owner notes for
  the six migrated owners (which scope, what it owns, what it deleted); the WF allowlist state after C3.
- `tcp-local-redirect.md`: `TcpProxyRelay`'s pump tracking and the corrected sealed-refusal rule;
  `TcpRelayFaultObserver` removal and intrinsic fault observation; the store's scope-owned CTS.
- `udp-relay.md`: single failure representation (`scope.Fault`), the `Action<UdpProxySession>` signal, the
  coordinator scope's seal point, `_inFlightTeardowns` removal.
- `hot-path.md`: the corrected allocation-gate prescription (already committed in the parent's revision;
  confirm it matches whichever §3 branch landed).
