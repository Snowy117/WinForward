# Migrate remaining lifecycle owners

## Goal

Migrate the last group of lifecycle owners to `QuiescenceScope` — `CaptureLifecycle`,
`LayeredCaptureRunner`, `MultiAdapterCaptureLoop`, `IdleExpirySweeper`, `RuntimeHeartbeat`, and the
`Socks5*` pair — so the C2 allowlist empties and the program's invariant (I1 admission / I2 quiescence)
holds for `src/**` without exemptions. Completing this child is the parent's completion condition.

## Background (corrections taken during planning, 2026-09-21)

Evidence: `research/owner-lifetime-inventory.md`, `research/spawn-and-allocation-audit.md`,
`research/socks5-token-audit.md`, `research/design-contradictions-and-hazards.md`. The program design's
§4.1/§4.3 sketches disagree with the working tree in three places, all now settled:

1. **`QuiescenceScope.Run` invokes the child body inline on the caller thread** — there is no
   ThreadPool hop. The earlier plan for `LayeredCaptureRunner` ("the spawns become `Run` children; the
   loop body is its own async method with a lease") therefore cannot hold for the monitor:
   `MonitorAsync` is a **blocking** `WaitOne` loop, not an async method, so `Run` would stall its
   caller for the monitor's whole lifetime. `WF0003`'s own diagnostic text already names the correct
   alternative — "or use a dedicated worker its owner joins" — and the repository's proven shape for
   that is `NdisCapturePump`'s raw `Thread` + `TaskCompletionSource` completion bridge.
2. **The earlier SOCKS5 line numbers do not name the race.** `Socks5UdpTransport.cs:255/282/294` are
   a synchronous `SendTo` and two awaited `SendToAsync` calls (the transport owns no CTS at all), and
   `Socks5ControlConnection.cs:295` is `IsCancellationRequested`, which is safe after `Dispose`. The
   genuine race is the `AttemptToken` property (`:208`, which evaluates `_attemptCancellation.Token`)
   read from `RunWithinAttemptAsync` (`:265`, `:279`) while `DisposeAsync` disposes that source
   (`:244`).
3. **`NdisCapturePump` is out of scope for the primitive.** Its assembly, `WinForward.NdisApi`, cannot
   reference `QuiescenceScope` (the primitive is `internal` to `WinForward.Runtime`, and the dependency
   direction `Runtime → NdisApi` forbids the reverse). Its raw `Thread` is already joined through the
   `ValueTask(outcome.Task)` bridge, which E6 sanctions.

## Requirements

- Uses the C1 primitive; introduces no new lifetime mechanism, and does not touch
  `QuiescenceScope.TryEnter`/`Exit`/`DrainAsync`.
- **`LayeredCaptureRunner`** (hard constraint: the file is **417 effective lines**, 17 over the cap in
  `directory-structure.md`; C4 must **net-reduce** it to ≤400):
  - the monitor keeps a **dedicated OS thread** joined by its owner (the WF0003 door for a
    synchronously-blocking worker), replacing `Task.Factory.StartNew(…, LongRunning)`;
  - the scope owns the run's `CancellationTokenSource` (replacing the local `monitorCancellation`), and
    the periodic refresh tick becomes a `Run` child on it;
  - the `_periodicRefreshInterval > TimeSpan.Zero` guard stays **outside** `Run` (`PeriodicTimer`
    rejects a non-positive period; two tests pin the guard).
- **`MultiAdapterCaptureLoop`** owns its own scope; `_ = ForwardDegradationAsync(...)` (`:110`) becomes
  a `Run` child, and its `DisposeAsync` drains it after disposing the pumps — today the loop disposes
  the pumps without ever awaiting an in-flight degradation forward.
- **`CaptureLifecycle` (`TransactionalCaptureRuntime`)** replaces `_shutdown` with a scope-owned CTS and
  drains it at the end of its existing single-flight cleanup. The run task is an **awaited entry point**,
  not a spawned child, so it is not registered.
- **`IdleExpirySweeper` and `RuntimeHeartbeat`** replace their `_shutdown`/`_loop` pair with a
  scope-owned CTS plus a `Run` child; `Start()` keeps its throw-on-second-start contract, and a second
  `DisposeAsync` becomes a join (today it throws `ObjectDisposedException` from `CancelAsync` on the
  disposed source).
- **`Socks5ControlConnection`** stops owning its lifetime CTS: it gains its own scope linked to its
  owner's token, the per-attempt `CancelAfter` deadline becomes an **operation-scoped local** linked
  CTS (mirroring `TcpProxyRelay.StallWindow`), and the cold operations (`ConnectOnceAsync`,
  `RunWithinAttemptAsync`) admit into that scope so the token is never read after its release.
- **`Socks5UdpTransport`** gains **no** scope (it owns no CTS and is the hot datagram path). It gains
  only an allocation-free disposal guard so `_sendGate.Dispose()` cannot race a sender that has not yet
  reached `_sendGate.WaitAsync`, preserving the warm zero-allocation shape and the "gate disposed last"
  order.
- The **teardown-awaits-its-own-tree** deadlock class (present in four owners) is prevented by D11
  plus the rule that a caller-awaited entry-point task is never registered as a scope child.
- Removes the last two allowlist entries (`.editorconfig:455-458` for `MultiAdapterCaptureLoop.cs`
  WF0001; `:460-463` for `LayeredCaptureRunner.cs` WF0003), leaving only the primitive's permanent
  `QuiescenceScope.cs` exemption.

## Acceptance Criteria

- [ ] Every migrated owner has a test proving `DisposeAsync` returns only after its registered (or
      inline-awaited) work completes.
- [ ] No `_ =`, `.ContinueWith`, `Task.Run`/`Task.Factory.StartNew`, or allowlist entry remains anywhere
      in `src/**` except the primitive's own documented exemption.
- [ ] The monitor still runs on its own OS thread and the runner still joins it; the periodic-interval
      guard still holds.
- [ ] `LayeredCaptureRunner.cs` is ≤400 effective lines after the change.
- [ ] The SOCKS5 per-attempt timeout semantics are unchanged (`HandshakeTimeoutIncludesMethodSelectionRead`,
      `CommandTimeoutIncludesReplyRead`, `UpstreamStreamClearsPerAttemptSocketTimeouts` green), and no
      path can read a `.Token` of a disposed source.
- [ ] A degradation forward in flight when `MultiAdapterCaptureLoop.DisposeAsync` runs is awaited, not
      orphaned.
- [ ] `HotPathAllocationGateTests` green, plus `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes`
      and `SendSpanAsyncWarmPathRunsNoAsyncStateMachine`, with no added allocation on the UDP path.
- [ ] `CaptureLifecycleTests`' concurrency tests and `NdisCapturePumpTests`' thread/join tests stay
      green with no weakened assertions.
- [ ] Specs updated (`hot-path.md`, `error-handling.md`, plus `async-lifetime.md` per-owner notes).
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Out of Scope

- `NdisCapturePump`'s thread/join mechanism (see Background 3; E6 already sanctions it).
- The UDP datagram hot path's structure: `SendSpanAsync`'s warm shape, `_sendBuffer` reuse, the
  `_sendGate` ordering, and `ReceiveAsync`.
- `Socks5UdpTransport`'s absence of a CTS (correct by design — every token it uses is a caller
  parameter).
- `DurableCaptureBundle` / `Program.cs` disposal ordering (H13) — C4 preserves it, does not redesign it.

## Notes

- Depends on C1; follows C3. Emptying the C2 allowlist is the parent program's completion condition.
- This child is a complex task: `design.md` and `implement.md` are required before `task.py start`.
- Baseline at planning time: `WinForward.Core.Tests` 773 + `WinForward.Analyzers.Tests` 18 (both green,
  HEAD `ce70098`); new tests raise the total by exactly their count.
