# Implementation Plan: UDP stability — close Windows/Linux gap and eliminate loss

Ordered checklist with validation gates. Each numbered step is a commit-sized unit (D-numbers
match design.md).

## Step 1 — Harness: pacing fix + raw baseline scenario (D4 + D1)

- [ ] Add `timeBeginPeriod(1)`/`timeEndPeriod` wrapper (no-op off-Windows) used by
      `UdpLossScenario` and the new scenario for the whole run.
- [ ] Add `udp.rawBaseline` scenario: raw-socket 4-hop echo chain, same pacing loop and JSONL
      metric shape as `udp.lossRate`; register in `--scenario all` / `--quick` and
      `SoakOptions` (no new required flags).
- [ ] Local validation: `dotnet run -c Release --project benchmarks/WinForward.Benchmarks --
      --stability --scenario all --quick` on Linux — baseline row appears, numbers plausible
      (baseline pps ≥ udp.lossRate pps).
- [ ] `dotnet test -c Release` green.
- [ ] Commit (harness-only, additive).
- [ ] Update `benchmarks/README.md`: new scenario row + Windows pacing series note.

## Step 2 — Baseline matrix (Linux + Windows)

- [ ] Linux: full stability run (`--scenario all --duration 60 --pps 25000 --seed 42`); record
      `B_linux` (udp.rawBaseline) and current product numbers under new pacing.
- [ ] Windows: publish self-contained benchmarks, upload via evil-winrm-py (runbook in
      design.md), same full run; record `B_windows` and pre-fix product numbers.
- [ ] Decision gate: compare `udp.lossRate` achieved pps vs baseline on Windows.
      - If baseline itself ≈ current product numbers ⇒ the gap is environmental (H3/H4);
        re-present findings to the user before product changes (acceptance may already be met
        or unreachable on this box).
      - Else proceed to Step 3.
- [ ] Save both JSONL files under `benchmarks/results/` alongside the 2026-08-29 set.

## Step 3 — Product: patient setup admission (D6, the loss fix)

- [ ] `UdpProxyCoordinator.CreateSessionAsync`: `_setupLimiter.WaitAsync(TimeSpan.Zero)` →
      patient `WaitAsync(cancellationToken)`; cap-failure IOException path removed; genuine
      failures keep the 1s cooldown tombstone; shutdown cancellation unwinds waiters cleanly.
- [ ] Update tests asserting the old cap-failure/cooldown semantics; add a flash-crowd
      regression (all setups succeed, zero setup-queue drops, every accepted datagram forwarded).
- [ ] Harness: make `CountingRuntimeLogger` opt-in (const bool, default false), keep `hops`.
- [ ] Linux stability re-run: expect lossRate == 0 at 5000 AND 25000 pps (warmup absorbs ramp).
- [ ] Commit.

## Step 4 — Product: sync-send fast path + activity amortization (D2 + D3)

- [ ] `UdpProxySession`: keep `_lastActivityTicks` Interlocked per op; throttle
      `_activityObserver` propagation to ≥100 ms apart (Interlocked compare-exchange).
- [ ] `Socks5UdpTransport`: relay socket `NonBlocking = true` at creation; `SendAsync`
      restructured per hot-path #3 (non-async entry, `_sendGate` + span encode + sync
      `SendTo` warm path, `SocketException` → async `SendToAsync` slow path).
- [ ] Tests: D3 — association table receives first touch immediately, then at most one per
      100 ms under rapid sends (fake `TimeProvider`); idle-expiry semantics unchanged.
      D2 — sync send reaches a loopback echo peer; disposal during pending send faults
      safely; loop-prevention registration unaffected; existing tests stay green.
- [ ] Linux stability re-run (both changes together): pps not worse, loss still 0; commit.

## Step 5 — Acceptance & wrap-up

- [ ] Compute `C_os`, `T_zero` (design.md formula); run `udp.lossRate --pps T_zero` on both OS:
      `lossRate == 0`, no reordering, no duplicates (A2).
- [ ] A1: `C_windows ≥ 0.7 × B_windows`. A3: `C_linux ≥ 22000`, default loss ≤ 0.19%.
- [ ] Perf spot-check: `dotnet run -c Release --project benchmarks/WinForward.Benchmarks --
      --filter '*UdpSession*' --job short` (and any suite touching changed code) — allocation
      columns unchanged (A4).
- [ ] `dotnet test -c Release` full suite green.
- [ ] Archive acceptance JSONL under `benchmarks/results/`; update the results README with the
      new series.
- [ ] Spec update (Phase 3.3): hot-path.md gains the "sync socket send fast path
      (non-blocking + WouldBlock fallback)" pattern; udp-relay.md notes the activity-touch
      throttling contract.
- [ ] If A1/A2 fail after Steps 3-4: stop, present measured per-hop attribution
      (baseline vs product deltas), propose D5-class follow-ups (receive batching, topology
      change) — do not silently expand scope.

## Validation commands

```text
dotnet test -c Release
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario all --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario all --duration 60 --pps 25000 --seed 42 --output <path>
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp --pps <T_zero> --duration 60 --seed 42
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpSession*' --job short
```

Windows legs use the evil-winrm-py runbook (design.md) with the same commands on the remote box.

## Rollback points

Every step is an independent commit; revert per step. Step 2 makes no repo changes (results
files only). No config schema, wire-format, or ABI changes anywhere in this task.

## Risky files

- `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs` (send semantics, non-blocking mode)
- `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` (activity/expiry semantics)
- `benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs` (pacing change — series
  discontinuity handled by README note)
