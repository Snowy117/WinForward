# Implement — Transport lifecycle hardening

Execution plan for `.trellis/tasks/09-20-transport-lifecycle`. Requirements: `prd.md`. Design and
decisions (D1–D3): `design.md`. All line numbers below were captured during the 2026-09-20 review —
**re-read each site before editing** (they drift).

## Conventions

- One commit per phase; each phase is independently revertible.
- Behavior-preserving moves where possible; no line-count-only churn.
- Run the per-phase validation after every phase; run the full gate once in Phase E.
- Never suppress a diagnostic globally. Narrow `#pragma` / `.editorconfig` with a reason only.

## Validation commands

```bash
# Per-phase (fast feedback)
dotnet build WinForward.slnx -c Release
dotnet test  WinForward.slnx -c Release

# Phase E / pre-commit (full gate — do not pipe; piping hides the exit code)
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx   # parse XML: zero <Issue>
```

## Phase A — Ownership consolidation (R6 / design §3)

Rationale for going first: it changes coordinator constructor shapes and ripples into test
construction sites, so land it before phases that add tests.

- [ ] A1. `TcpProxyCoordinator`: make `SynCopyPool` / `SetupExecutor` required, non-null; delete the
      `_ownsSynCopyPool` / `_ownsSetupExecutor` flags and the self-creation branches
      (`TcpProxyCoordinator.cs:67-69`); make `DisposeAsyncCore` never dispose injected collaborators.
- [ ] A2. Add a `_disposed` guard to `TcpProxyCoordinator.DisposeAsync` (`TcpProxyCoordinator.cs:636-650`)
      so repeated disposal is a no-op.
- [ ] A3. `UdpProxyCoordinator`: same for `SetupQueuePool` / `ReceiveWindowPool` / `SetupExecutor`,
      deleting `_ownsSetupQueuePool` / `_ownsReceiveWindowPool` / `_ownsSetupExecutor`
      (`UdpProxyCoordinator.cs:64-69`).
- [ ] A4. Move any creation-failure pool disposal reliance to the bundle (verify
      `DurableCaptureBundle.cs:132-134,144-146,199-200,210-211,365-366,377-379` still cover it).
- [ ] A5. Correct the composer doc comments (`TcpRedirectComposer.cs:16`, `UdpProxyComposer.cs:16`).
- [ ] A6. Update test construction sites (`InternalsVisibleTo` already configured) to pass pools/executor.
- [ ] A7. Test: fake pool/executor with a dispose counter — coordinator `DisposeAsync` leaves the
      counter at 0; second `DisposeAsync` is a no-op.
- [ ] Gate: build + tests green.

Rollback point: revert Phase A commit (test construction sites revert with it).

## Phase B — TCP quiescence (R1, R2 / design §1.1–1.4)

- [ ] B1. `TcpProxyRelay.DisposeAsync` (`TcpProxyRelay.cs:302-311`): after disposing socket/control,
      `try { await Completion } catch { /* observed by TcpRelayFaultObserver */ }`.
- [ ] B2. Remove the duplicate fault-observer registration; keep exactly one site (design §1.2).
- [ ] B3. `TcpRedirectAcceptor.RunAcceptLoopAsync` (`TcpRedirectAcceptor.cs:18`): `await`
      `ObserveRelayCompletionAsync` and `DrainRedundantConnectionsAsync` as terminal steps; drop the
      `_ =` discards (`:84`).
- [ ] B4. Defer `DisposeLifetime()` (`TcpRedirectSessionStore.cs:249`) to after
      `await session.AcceptLoop` in `DisposeCoreAsync` (`:191`); add the `IsDisposed` teardown gate.
- [ ] B5. Tests: relay dispose leaves `Completion` completed; single fault observation; after store
      `DisposeAsync` no teardown re-entry (frozen tombstone/reset counters).
- [ ] Gate: build + tests green.

Rollback point: revert Phase B commit.

## Phase C — UDP lifecycle (R3, R4, R5 / design §1.5, §2)

- [ ] C1. Add `UdpProxySession._lifetime` (linked to `context.Shutdown`); route the receive loop
      (`UdpProxySession.cs:210`) and injections (`:268`) through it; cancel on `BeginExpiry` and in
      `DisposeAsync`; switch catch guards to `when (_lifetime.IsCancellationRequested)`.
- [ ] C2. Add `enum UdpSessionState` + explicit transition documentation (design §2.1).
- [ ] C3. Add `enum UdpTeardownReason`; change `RemoveSlotAsync` (`UdpProxyCoordinator.cs:377`) to take
      the reason; arm cooldown only for `SetupFailure`.
- [ ] C4. `UdpProxySession.SendSpanAsync` → `ValueTask<bool>`; `SendOnReadySessionSpanAsync`
      (`UdpProxyCoordinator.Send.cs:82`) counts/logs the `false` drop without removing the slot.
- [ ] C5. Owning drain for the residual receive-failure handler (design §1.6): coordinator tracks
      in-flight removal tasks and awaits them in `DisposeCoreAsync`.
- [ ] C6. TCP: set `item._cancellationToken = _store.ShutdownToken` in `LaunchSetup`
      (`TcpProxyCoordinator.cs:224-229`).
- [ ] C7. Tests: idle expiry records no `_receiveFailure`; expiry-vs-send returns false with no
      exception; setup token equals store shutdown token.
- [ ] Gate: build + tests green.

Rollback point: revert Phase C commit. If §2.3 (C4) proves too invasive, it can be dropped
independently while keeping C1.

## Phase D — Lifecycle holes (R7 / design §4)

- [ ] D1. Attach-failure: tear down the session and restructure the disposal block
      (`TcpRedirectAcceptor.cs:73-82`).
- [ ] D2. `RegisterSession` (`TcpRedirectSetup.cs:167-169`): release the claimed listener/alias on a
      registration/construction throw.
- [ ] D3. Fix the cooldown-after-`RemoveAll` ordering (`TcpProxyCoordinator.cs:304` vs `:307`, `:647`).
- [ ] D4. Tests: attach-failure returns store session count to 0; setup-failure leak guarded.
- [ ] Gate: build + tests green.

Rollback point: revert Phase D commit.

## Phase E — Full quality gate

- [ ] E1. `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` — exit 0,
      empty output.
- [ ] E2. `dotnet build WinForward.slnx -c Release` — zero warnings.
- [ ] E3. `dotnet test WinForward.slnx -c Release` — green (baseline + new tests).
- [ ] E4. `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` — parse the XML
      and confirm **zero** `<Issue>` entries.
- [ ] E5. Dispatch `trellis-check` for the spec-compliance + cross-layer review.

## Review gates

- After each phase: build + tests green (A/B/C/D).
- After Phase D: full gate (E1–E4) + `trellis-check` (E5) before any commit.
- Do not start implementation before the user reviews this plan and `task.py start` is run.

## Risks / notes

- B3 changes the accept loop's terminal semantics; confirm nothing relies on `AcceptLoop` completing
  early.
- C5 must not make the receive loop await the handler (re-entrancy — see `UdpProxySession.cs:257-258`).
- C4 changes an internal signature used by the capture hot path; verify no allocation is added and
  the zero-alloc gates stay green.
