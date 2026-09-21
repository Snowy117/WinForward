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
- **Gate hardening precedes touching the UDP send path.** `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` measures 64 dispatches across 64 `await`s with the per-thread `GC.GetAllocatedBytesForCurrentThread()`, so it is only valid while the continuation resumes on the same thread: run alone it fails 4/4, the whole `HotPathAllocationGateTests` class alone leaves 1 of 10 failing, and only the full suite is green (observed 2026-09-21, task 09-20-quiescence-scope). It guards the span-send path this task changes, so stabilize the gate **before** the migration — make the measured window thread-stable (single-threaded `SynchronizationContext`) or assert a bound plus the thread-independent `SpanSends` counter, per `hot-path.md` → "An allocation gate must evaluate on one thread".

## Acceptance Criteria

- [ ] No `_ =`, `.ContinueWith`, or `Task.Run`/`StartNew` remains in the cluster files, and their
      allowlist entries are gone.
- [ ] Per-owner quiescence test: `DisposeAsync` returns only after registered work completes.
- [ ] Fault injection: after deleting the observers, a faulted pump produces **no** unobserved task
      exception and `tcp.relay.faulted` is still recorded (`design.md` §4.4).
- [ ] A stalled/cancelled pump still reaches the drain — `DrainAsync` does not hang (`design.md` §6).
- [ ] `HotPathAllocationGateTests` green; no packet-path allocation delta from the UDP send-gate
      change.
- [ ] The UDP allocation gate is thread-stable: it passes when run **alone**
      (`--filter "FullyQualifiedName~EstablishedUdpDatagramPathAllocatesNoManagedBytes"`), not only
      inside the full suite.
- [ ] Specs updated (`tcp-local-redirect.md`, `udp-relay.md`, `hot-path.md`, plus `async-lifetime.md`
      per-owner notes).
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Notes

- Depends on C1. Should follow C2 so new violations are caught during migration.
- Must leave the C2 allowlist strictly smaller; C4 removes the remainder.
- This child is a complex task: write its own `design.md` and `implement.md` before `task.py start`,
  referencing the parent `design.md` §4.1–§4.4.
