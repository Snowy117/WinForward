# Design: Fast hardening (client RST + NoDelay + pooled pump buffers)

Baseline evidence: `archive/2026-08/08-30-proxy-perf-stability-research/research/tcp-redirect-path.md`
(S1, P1, P2, P3). File:line references below are as-of `e5667af`; the implementer must
re-verify against current source before editing (acceptance criterion of the parent).

## D1. RelayEndKind + client reset on relay end (R1)

### Surface

- Add `internal enum RelayEndKind { CleanEnded, Stalled, Faulted }` (in
  `TcpProxyRelay.cs` or its namespace file, per directory-structure spec).
- `TcpProxyRelay` exposes `RelayEndKind EndKind { get; }` — valid after `Completion`
  completes. Derivation:
  - `RunPumpAsync` returns `PumpResult.Stalled` → both-direction completion → `Stalled`
  - pump faults (`OperationCanceledException` filtered on the relay token, or any other
    exception) → `Faulted`
  - both pumps complete without stall/fault (FINs already propagated via
    `ShutdownSend`) → `CleanEnded`

### Reset decision (in `TcpRedirectAcceptor.ObserveRelayCompletionAsync`)

```
await relay.Completion (existing)
catch { }                    // existing swallow — keep, but classify after
switch (relay.EndKind):
    CleanEnded -> no reset
    Stalled / Faulted ->
        try { await _clientReset.TryInjectClientResetAsync(session.Association, CancellationToken.None); }
        catch (Exception ex) { rate-limited warn log }   // reset failure must not block teardown
then _tearDownSession(...)   // existing, unchanged
```

Key ordering: the reset is injected while the association still exists (before
`_tearDownSession` retires it) — `ClientResetInjector` reads the recorded SYN template
and `ClientNextSeq`/`ServerNextSeq` trackers from it. `TryInjectClientResetAsync` is
already idempotent-safe (cooldown-claimed, best-effort: it fails closed on unparseable
frames without blocking callers — verified in research S1/S7).

Concurrency: `ObserveRelayCompletionAsync` is the single completion observer
(fire-and-forget from the accept loop), so no new synchronization is introduced. The
teardown tail should be wrapped so an injecting/reset throw cannot escape as an
unobserved task exception (research S5 — hardening, cheap here).

### Tests

- Fault mid-flow: fake upstream reset → assert injector seam received an in-window
  RST|ACK for the association (assert seq comes from trackers).
- Stall: force `PumpResult.Stalled` (existing stall test helpers) → same assertion.
- Clean end: no injector call.
- Unit-test `EndKind` derivation per path.

## D2. NoDelay (X4)

Two one-liners, no design decisions:

- `TcpRedirectListener` accept path: `accepted.NoDelay = true;` right after
  `AcceptAsync` returns (before handing to relay factory).
- `Socks5ControlConnection.ConnectOnceAsync`: `socket.NoDelay = true;` after
  `ConnectAsync` succeeds (with the socket options block near the existing setup).

Both sockets are stream relays (byte pipes); there is no scenario where Nagle helps.
Test: assert option set on the sockets exposed by existing test fakes (or via
`TcpRelayBenchmarks`-style socket pair tests).

## D3. Pooled 64 KiB pump buffers (X5)

- Replace `BufferSize = 8192` + `new byte[BufferSize]` per direction with:
  - `const int PumpBufferSize = 64 * 1024;`
  - `byte[] buffer = ArrayPool<byte>.Shared.Rent(PumpBufferSize);` per pump direction
  - `try { ...pump loop... } finally { ArrayPool<byte>.Shared.Return(buffer); }`
- Rent/return must bracket the whole pump loop (not per chunk) — identical lifetime to
  the current `new byte[]`, so no correctness change, only allocation source.
- `Rent` may return a larger array: the loop must use `PumpBufferSize` (or
  `buffer.Length`) consistently — prefer `buffer.Length` to avoid any slicing subtlety
  (read into `buffer.AsMemory(0, len)` is not needed; plain `byte[]` offsets suffice).
- Do NOT pool across both directions with one rent: keep one rent per pump task
  (directions have independent lifetimes via half-close).
- Benchmark note: `TcpRelayBenchmarks` chunk-65536 case must show no allocation
  regression; per-invocation allocation should drop (the two 8 KiB arrays disappear from
  per-relay cost; pooled rent is amortized).

## D4. Stall re-arm throttle (X8a)

- In the pump loop, keep a `long _lastArmTicks` (Stopwatch timestamp) per pump; replace
  unconditional `StallWindow.Arm()` with:

```
if (_lastArmTicks == 0 || stopwatch.ElapsedTicks - _lastArmTicks > ArmThrottleTicks)
{
    _window.Arm();
    _lastArmTicks = stopwatch.ElapsedTicks;
}
```

- `ArmThrottleTicks` = 1 second (`Stopwatch.Frequency`-scaled, `const` computed).
- Semantics: the window is still armed before every interval boundary within 1s drift;
  the 30-min stall window drifts by at most 1s — irrelevant. Never disarmed between
  operations (lifetime-token link semantics untouched).
- First arm must be unconditional (`_lastArmTicks == 0` case).

## Tradeoffs / alternatives considered

- **Reset-on-stall could instead extend the window or keepalive-probe first** (S4's full
  fix): rejected for this task — keepalive is backlog #8; making the *current* teardown
  client-visible is the 0.5-day win.
- **RW lock / buffer-pool class for pump buffers**: overkill; `ArrayPool<byte>.Shared`
  is the established project pattern (frames already use it).
- **Injecting RST after teardown with a captured association snapshot**: rejected —
  trackers/SYN template ownership is cleaner while the session is alive; teardown
  disposes sockets, not the injector state, but keeping the order inject→teardown
  matches the setup-failure path precedent (`HandleRelaySetupFailureAsync`).

## Compatibility / rollout

- No public API, config, wire-format, or ABI changes. All changes are internal to the
  relay/accept/listener path. Rollback = revert the single commit.
