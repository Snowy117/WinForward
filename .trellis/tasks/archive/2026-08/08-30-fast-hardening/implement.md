# Implementation Plan: Fast hardening

Read order for the implementer: `implement.jsonl` entries → `prd.md` → `design.md` →
this file. Re-verify every file:line below against current source before editing
(research baseline was `e5667af`; intervening commits may have shifted lines).

## Step 0 — Re-verify research claims

- [ ] Read current `TcpRedirectAcceptor.cs` (`ObserveRelayCompletionAsync`), 
      `TcpProxyRelay.cs` (pump loop, buffers, stall window), `TcpRedirectListener.cs`
      (accept), `Socks5ControlConnection.cs` (connect), `ClientResetInjector.cs`
      (`TryInjectClientResetAsync` + `HandleRelaySetupFailureAsync` call site as
      precedent). Confirm R1/X4/X5/X8a still hold as described; report drift if not.

## Step 1 — RelayEndKind + client reset (D1)

- [ ] Add `RelayEndKind` + `EndKind` on `TcpProxyRelay`; derive in
      `RunPumpAsync`/completion aggregation.
- [ ] In `TcpRedirectAcceptor.ObserveRelayCompletionAsync`: on `Stalled`/`Faulted`,
      `TryInjectClientResetAsync` before `_tearDownSession`; wrap in try/catch with
      rate-limited warn; also wrap the teardown tail (S5 hardening).
- [ ] Tests: fault→RST, stall→RST, clean→no RST, EndKind unit tests.
- [ ] Run: `dotnet test` (full suite).

## Step 2 — NoDelay (D2)

- [ ] `TcpRedirectListener` accept: `accepted.NoDelay = true`.
- [ ] `Socks5ControlConnection.ConnectOnceAsync`: `socket.NoDelay = true` after connect.
- [ ] Test: socket-option assertion via existing fakes/tests.
- [ ] Run: `dotnet test`.

## Step 3 — Pooled pump buffers (D3)

- [ ] 64 KiB `ArrayPool<byte>.Shared` rent per pump direction, `finally` return,
      `buffer.Length`-based loop bounds.
- [ ] Run: `dotnet test` + relay benchmarks; confirm no allocation regression
      (per-invocation should improve).

## Step 4 — Stall re-arm throttle (D4)

- [ ] Stopwatch-ticks throttle (1 s) around `StallWindow.Arm()`; unconditional first
      arm; no disarm semantics change.
- [ ] Run: `dotnet test` (existing stall/window tests must stay green).

## Validation commands

```bash
dotnet build -warnaserror           # zero-warning gate (match repo standard)
dotnet test                          # full suite green
# benchmarks (Linux dev box, ShortRun acceptable for gate check):
dotnet run --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRelay*'
# stability quick gate:
dotnet run --project benchmarks/WinForward.Benchmarks -- --stability --quick
```

(Adjust to the repo's actual benchmark invocation; check
`benchmarks/WinForward.Benchmarks/Program.cs` for the current arg shape.)

## Review gates

- After Step 1: review reset ordering (inject before teardown) and error containment —
  this is the only step with concurrency/lifecycle sensitivity.
- After Steps 2–4: mechanical; single review pass.

## Rollback

Each step is one logical commit; any step can be reverted independently. Step 1 has the
only behavioral risk (new RST traffic) — revert restores the blackhole behavior
(unacceptable long-term, but safe short-term).
