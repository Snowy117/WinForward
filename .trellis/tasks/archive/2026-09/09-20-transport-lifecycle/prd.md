# Transport lifecycle hardening: explicit quiescence boundary and session state

## Goal

Harden the transport lifecycle across three subsystems — TCP relay, UDP session, and the shared
SOCKS5 control/relay transport — so that teardown is explicit and bounded rather than resting on
undocumented ordering contracts. Concretely: (1) give each coordinator one quiescence boundary
that tracks and awaits every background task before `DisposeAsync` completes; (2) model the UDP
session lifecycle as an explicit state machine with a teardown *reason* instead of implicit flag
fields; (3) consolidate pool/executor ownership into composition and remove the `_ownsX` boolean
flags; and (4) close the concrete lifecycle holes found in the 2026-09-20 review.

This is a child of `08-30-proxy-perf-stability`: it is a stability/robustness hardening item, not a
performance feature. It carries no new user-visible behavior and no config/schema changes.

## Source Material

- 2026-09-20 transport-lifecycle review (this session): full read of
  `src/WinForward.Runtime/TcpRedirect/*`, `src/WinForward.Runtime/UdpProxy/*`,
  `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`,
  `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs`, plus the composition in
  `src/WinForward.Cli/DurableCaptureBundle.cs`.
- Existing contracts to preserve: `.trellis/spec/backend/hot-path.md`,
  `.trellis/spec/backend/quality-guidelines.md`,
  `.trellis/spec/backend/tcp-local-redirect.md`, `.trellis/spec/backend/udp-relay.md`,
  `.trellis/spec/backend/directory-structure.md`.

## Requirements

### R1 — One explicit quiescence boundary per coordinator (primary)

Every background task started by the capture/TCP/UDP runtime must be registered and awaited before
its owner's `DisposeAsync` completes. No fire-and-forget task may outlive the store/coordinator it
calls back into. Required coverage includes:

- TCP relay pump (`TcpProxyRelay.Completion`).
- Accept loops (`TcpRedirectAcceptor.RunAcceptLoopAsync`).
- Relay-completion observers (`ObserveRelayCompletionAsync`) and fault observers.
- UDP receive loops and the fire-and-forget setup/receive-failure handlers.
- Deferred dispose work (`RunDisposeAsync`).

Today these are `_ =`-discarded or unawaited; disposal can therefore return while callbacks are
still able to re-enter already-disposed state.

### R2 — Relay teardown awaits pump completion; fault observer registered once

`TcpProxyRelay.DisposeAsync` must await `Completion` (or the store's teardown must), so an observed
relay end cannot invoke client reset / session teardown after the store is disposed. The relay fault
observer must be attached exactly once (currently attached from both `TcpProxyRelay.cs:308` and
`TcpRedirectAcceptor.cs:78`).

### R3 — UDP session lifecycle is an explicit state machine with a teardown reason

The per-slot implicit state (`Session` / `Ready` / `_expiring` / `_activeSends` / `_receiveFailure`)
must become an explicit, documented state model. A normal idle-expiry teardown must NOT:

- route through the fault catch and set `_receiveFailure`,
- fire an unobserved fire-and-forget handler, or
- depend on `_shutdown.IsCancellationRequested` to distinguish "normal" from "fault".

Teardown *reason* (Normal / Expiry / Fault / Shutdown / Capacity) must be carried as data, not
inferred from cancellation flags.

### R4 — Expiry vs in-flight send must not drop the datagram or surface an internal exception

When expiry races a concurrent send, the caller must not receive an `IOException` naming internal
state, and the datagram must not be silently dropped without either delivery or an explicit,
documented policy (e.g. re-buffering into the replacement setup). Current behavior surfaces
`IOException("SOCKS5 UDP relay session is expiring.")` up to the packet dispatcher.

### R5 — Cancellation semantics stop overloading a single token

Coordinator shutdown cancellation, per-session receive cancellation, and per-setup cancellation must
be distinguishable. Remove or correctly wire the dead per-item cancellation token on the TCP setup
work item (currently always `CancellationToken.None`).

### R6 — Ownership consolidated into composition; no `_ownsX` flags

Pool/executor ownership must have exactly one owner. Either composition owns them and coordinators
never dispose them, or coordinators own everything they use — one choice, applied consistently, with
the `_ownsSynCopyPool` / `_ownsSetupExecutor` / `_ownsSetupQueuePool` / `_ownsReceiveWindowPool`
flags removed. `TcpProxyCoordinator` must gain its own disposed guard so repeated `DisposeAsync` is
a no-op rather than re-running teardown and re-disposing injected resources.

### R7 — Close the concrete lifecycle holes

- Attach-failure path must tear the session down instead of leaving a half-open `Redirecting`
  session until the idle sweep.
- No code path may read a session's `Token` after its lifetime CTS is disposed.
- `RegisterSession` must release an already-claimed listener/alias when self-traffic registration or
  session construction throws.
- The setup-complete vs `RemoveAll()` ordering must not re-write a cooldown entry after cooldown
  state has been cleared at shutdown.

### R8 — No hot-path regression

These changes must be behavior-preserving on the packet data path. The existing zero-allocation
gates and per-iteration batching contracts must remain green; no allocation, delegate, or lock may
be added to a steady-state packet path.

## Constraints

- **Performance-first**: packet-path code stays allocation-free; no LINQ/delegate conversions on
  hot paths (repo-wide rule). Any task-tracking added to hot paths must be allocation-free (e.g.
  pre-sized arrays / pooled registration), not a per-packet allocation.
- **Compatibility**: no config-schema, CLI, or public-API changes; no new user-visible behavior.
- **Quality gates**: `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`
  clean, `dotnet build -c Release` zero-warning, `dotnet test -c Release` green, and
  `jb inspectcode -e=HINT` report with zero `<Issue>` entries.
- **Structure**: keep every `.cs` file within the ≤400 effective-line rule; new seams follow the
  established "interface beside its implementation" and "physical move, logic untouched" discipline.
- **Platform**: Windows-only runtime paths stay guarded by injectable seams so they remain testable
  on the Linux CI host.

## Acceptance Criteria

- [ ] A test proves that disposing the TCP coordinator/store joins all relay and accept-loop tasks:
      after dispose returns, no callback re-enters store teardown (no reset injection / tombstone
      write after dispose), asserted with a real relay-socket loopback scenario or a tracked-task
      fake.
- [ ] `TcpProxyRelay.DisposeAsync` awaits `Completion`; a regression test pins that the pump task is
      completed when dispose returns.
- [ ] The relay fault observer is registered exactly once per relay; a test asserts single
      observation.
- [ ] An idle-expiry teardown of a UDP session leaves no `_receiveFailure` recorded, fires no
      failure handler, and is indistinguishable in tests from a clean teardown.
- [ ] A concurrent "expiry vs send" test shows the datagram is not dropped against policy: it is
      either delivered or buffered into the replacement setup, and no internal-state `IOException`
      reaches the dispatcher.
- [ ] Repeated `TcpProxyCoordinator.DisposeAsync` is idempotent (no double-dispose of injected
      pool/executor), verified by a test.
- [ ] The `_ownsX` flags no longer exist; ownership is enforced by a single composition path, with a
      test or composition assertion covering the owned-vs-injected matrix.
- [ ] Attach-failure leaves no session in the store (asserted by inspecting store session count after
      the failure path).
- [ ] All existing tests stay green; the Release build is zero-warning; the format and inspectcode
      gates are clean.

## Notes

### Review findings (evidence, per requirement)

- R1: `TcpRedirectSessionStore.cs:156` (`_ = RunDisposeAsync`),
  `TcpRedirectAcceptor.cs:84` (`_ = ObserveRelayCompletionAsync`),
  `TcpProxyRelay.cs:284,289` (`_ = ShutdownSend`, pump continuation),
  `TcpRelayFaultObserver.cs:27` (`ContinueWith`), `UdpProxySession.cs:259`
  (`_ = receiveFailureHandler(this)`).
- R2: `TcpProxyRelay.cs:302-311` (dispose does not touch `Completion`; pump started at
  `TcpProxyRelay.cs:126`); double observe at `TcpProxyRelay.cs:308` + `TcpRedirectAcceptor.cs:78`.
- R3: `UdpProxySession.cs:185-188` (dispose transport then await loop), `:242` (ODE guard depends on
  `_shutdown`), `:246-259` (generic catch → `_receiveFailure` → fire-and-forget);
  `UdpProxyCoordinator.cs:427-433` (slot as four bare fields).
- R4: `UdpProxySession.cs:122` (throws `IOException` when `_expiring`), surfaced at
  `NdisPacketActionExecutor.cs:461`; race via `TryBeginExpiry` at `UdpProxySession.cs:170-175` and
  slot removal at `UdpProxyCoordinator.cs:354`.
- R5: `TcpProxyCoordinator.cs:224-232` (`item._cancellationToken` never assigned; `TrySetCanceled`
  passes `None`).
- R6: `TcpProxyCoordinator.cs:67-69` + `:636-650` (no disposed guard),
  `UdpProxyCoordinator.cs:64-69`, production disposal at `DurableCaptureBundle.cs:347-384`.
- R7: `TcpRedirectAcceptor.cs:73-82` (attach-failure leaves session),
  `TcpRedirectSessionStore.cs:249` vs `ClientResetInjector.cs:36` (late `Token` read),
  `TcpRedirectSetup.cs:167-169` (register-then-construct with no cleanup),
  `TcpProxyCoordinator.cs:304` vs `:307` + `:647` (cooldown re-written after `RemoveAll`).
- Lock discipline is already sound (store gate never held across await; documented acyclic order
  store→table→tombstone at `TcpRedirectSessionStore.cs:22-25`; UDP `_gate` likewise) and must not
  regress.

### Explicitly out of scope

- Local connection pool + stream multiplexing (`09-06-local-mux-transport`).
- Any new feature, config knob, or schema change.
- Line-count-only refactors of files that already meet the ≤400 rule.

### Open questions (resolve during planning)

- Buffer-into-replacement-setup (R4) vs a documented drop policy — which is acceptable?
- Single background-task tracker shared across subsystems vs one per coordinator?
- Does R6 pick "composition owns" (current production shape) or "coordinator owns"?
