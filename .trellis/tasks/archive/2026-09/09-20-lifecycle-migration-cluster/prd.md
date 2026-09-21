# Migrate TCP/UDP lifecycle cluster

## Goal

Migrate the TCP/UDP lifecycle cluster to `QuiescenceScope`: `TcpRedirectSessionStore`,
`TcpRedirectSession`, `TcpProxyRelay`, `UdpProxySession`, `UdpProxyCoordinator` (+ slot), and
`UdpSessionSetup`. Eliminate the cluster's fire-and-forget sites by structure — the signal/join split
(F1) and intrinsic fault observation deleting both observer mechanisms (F2) — and remove the
cluster's entries from the C2 allowlist.

## Requirements

- Uses the C1 primitive; introduces no new lifetime mechanism (`design.md` §2).
- **F1 — signal/join split.** `UdpProxySession.Start` becomes `Action<UdpProxySession>`; the
  coordinator maps it to `coordinatorScope.Run(...)`; `_inFlightTeardowns` and
  `DrainInFlightTeardownsAsync` are deleted. The receive loop stays on the session's scope; only the
  teardown moves to the coordinator's (`design.md` §4.2).
- **F2 — intrinsic fault observation.** Each relay pump records its own fault (`RecordFault` +
  rethrow). Delete `TcpRelayFaultObserver.cs` (whole file), `TcpProxyRelay.ObservePump` (`:287-294`),
  the `RCS1075` pragma and empty `catch` in `TcpProxyRelay.DisposeAsync`, and
  `TcpRedirectAcceptor.cs:120` (`design.md` §4.4).
- `TcpProxyRelay.cs:284`'s `_ = ShutdownSend(...)` becomes a synchronous call.
- `ITcpRelay.Completion` keeps its relay-level semantics — do **not** turn it into "both pumps
  finished"; the stall fast-exit is deliberate (`design.md` §4.4).
- Removes exactly the cluster's allowlist entries from `.editorconfig`.
- **Gate hardening precedes touching the UDP send path — LANDED (step 1, 2026-09-21).**
  `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` measures 64 dispatches
  across 64 `await`s with the per-thread `GC.GetAllocatedBytesForCurrentThread()`. Run alone it failed
  repeatedly (`Expected: 0, Actual: 600`, later `3688`/`4328`/`5352`); only the full suite was green. It
  guards the span-send path this task changes, so it was stabilized **before** the migration. The
  originally prescribed single-threaded `SynchronizationContext` is **infeasible** here — the chain
  awaits with `ConfigureAwait(false)` throughout (`NdisPacketActionExecutor.cs:360,450`), so a context
  cannot capture it. The landed fix proves direct admission (`SpanSends` advances by exactly one with
  `Diagnostics.PendingSetupBytes == 0`) and allocation stability before opening the window, keeps the
  exact 0-byte assertion plus the thread-id and `SpanSends`-delta checks, and passes 10/10 in isolation.
  **The earlier "the continuation migrates to another pool thread" diagnosis was wrong** — measured: the
  thread id was constant across 40 instrumented loops (8 runs × 5), and the first batch's delta tracked
  the not-yet-`Ready` setup path instead. See `hot-path.md` → "An allocation gate must open only after
  its path is ready".
- **One additive primitive change.** `QuiescenceScope` gains `IsSealed` (cold-path, allocation-free), and owners delete their parallel `_disposed` flags in favour of it (`design.md` §2, D-C3-1).
- **Single failure cause for UDP.** `UdpProxySession._receiveFailure` is deleted; `State` and the send admission read `scope.Fault` under the existing `_activityGate` (`design.md` §4.4, D-C3-5).
- **`TcpRedirectSessionStore._shutdown` and `TcpProxyRelay.pumpCancellation` become scope-owned tokens** (D7; the latter also removes a per-relay CTS leak) — `design.md` §4.1, §4.3.
- **A sealed `TryEnter` refusal in a relay pump must not report a stall**, and the abandoned pump is joined by the drain only after the socket close that bounds it — `design.md` §4.3, D-C3-3/D-C3-4.

## Resolved decisions (confirmed 2026-09-21)

Six design questions were settled before this child's `design.md` was written; the full rationale and the
nine `D-C3-*` decisions live in that document, and the parent `design.md` §4 was corrected to match.

1. **Allocation gate**: prove the path ready + allocation-stable, then a thread-checked exactly-zero window; a documented bound only if the path genuinely must yield (the `SynchronizationContext` route is infeasible). The original "thread-stable by construction" framing assumed continuation migration, which measurement disproved — readiness was the real cause, and the thread-id check is retained as a guard.
2. **Relay pumps**: both are scope children; `Completion` keeps its relay-level semantics; the drain is bounded by the socket close; a refusal reports a clean end, never a stall.
3. **UDP failure**: `scope.Fault` is the single cause, read under `_activityGate`; `_receiveFailure` deleted.
4. **Store CTS**: the scope owns it (D7), replacing `_shutdown`.
5. **Coordinator teardown warning**: moves inside the `Run` body, since `Run` records faults but cannot log the owner's domain-specific warning.
6. **Dispose exception semantics**: the owner's own teardown keeps today's propagation; only *child* faults are recorded without throwing (parent D4 applies to children).

## Acceptance Criteria

- [ ] No `_ =`, `.ContinueWith`, or `Task.Run`/`StartNew` remains in the cluster files, and their
      allowlist entries are gone.
- [ ] Per-owner quiescence test: `DisposeAsync` returns only after registered work completes.
- [ ] Fault injection: after deleting the observers, a faulted pump produces **no** unobserved task
      exception and `tcp.relay.faulted` is still recorded (`design.md` §4.4).
- [ ] A stalled/cancelled pump still reaches the drain — `DrainAsync` does not hang (`design.md` §6),
      and disposing a relay mid-transfer injects **no** spurious client reset (D-C3-4).
- [ ] `QuiescenceScope.IsSealed` is covered by tests and the primitive's allocation gate stays green.
- [ ] `UdpProxySession` has exactly one failure representation (`scope.Fault`); `_receiveFailure` is gone
      and the admission reads stay under `_activityGate`.
- [ ] The coordinator's per-teardown warning is still logged on a faulting teardown (`Run` swallows the
      fault, so the body must log it).
- [ ] `HotPathAllocationGateTests` green; no packet-path allocation delta from the UDP send-gate
      change.
- [x] The UDP allocation gate is verifiable in isolation: it passes when run **alone**
      (`--filter "FullyQualifiedName~EstablishedUdpDatagramPathAllocatesNoManagedBytes"`), not only
      inside the full suite. *(Landed step 1: 10/10 isolated runs; the window is opened only after
      direct admission and allocation stability are proven, and it asserts the thread id is unchanged.)*
- [ ] Specs updated (`tcp-local-redirect.md`, `udp-relay.md`, `hot-path.md`, plus `async-lifetime.md`
      per-owner notes).
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Notes

- Depends on C1. Should follow C2 so new violations are caught during migration.
- Must leave the C2 allowlist strictly smaller; C4 removes the remainder.
- This child is a complex task: write its own `design.md` and `implement.md` before `task.py start`,
  referencing the parent `design.md` §4.1–§4.4.
- **Steps 1–2 landed and independently checked (2026-09-21)**: the gate hardening (branch 1) plus
  `QuiescenceScope.IsSealed` (`src/WinForward.Runtime/QuiescenceScope.cs:57`, additive: +10 lines, no
  field/fence/allocation, `TryEnter`/`Exit`/`DrainAsync` untouched) with two non-vacuous tests. Gates:
  format exit 0, Release build 0 warnings, `WinForward.Core.Tests` 765 + `WinForward.Analyzers.Tests` 18
  = 783 green, the C1 primitive allocation gate green, and the `QuiescenceScope` tests 21/21. The
  owner migrations (steps 3–6), the `.editorconfig` shrink, and the remaining acceptance criteria are
  still open. The independent check also found the recorded root cause for the gate failure to be
  **wrong** (no thread migration; a not-yet-`Ready` window), and `hot-path.md` was corrected
  accordingly.
