# Design: UDP stability — close Windows/Linux gap and eliminate loss

## Diagnosis model

Per-datagram cost in the soak chain = (OS socket hop cost × 4 hops) + (product per-datagram cost)
+ (harness pacing error). The Windows↔Linux gap comes from these asymmetries:

1. **Async socket completion cost (H1, primary).** On Linux, .NET completes a UDP
   `SendToAsync` synchronously (sendto accepts the datagram inline; no thread transition). On
   Windows every async socket op queues overlapped I/O and resumes via IOCP → thread-pool
   dispatch (~5-15 μs each). The soak chain performs ≥4 async socket ops per datagram, so
   Windows saturates at ~6-7k pps regardless of the target rate — matching the 2026-08-29 data.
2. **Pacing granularity (H2, secondary).** `Task.Delay(10ms)` on Windows quantizes to the
   ~15.6 ms system timer unless timer resolution is raised; 1952/6000 ticks fired late.
3. **Ambient cost / hardware ceiling (H3/H4, unquantified).** Defender/WFP layers, ndisrd in
   the stack, unknown box hardware — quantified by the raw baseline below, not guessed.

## Changes

### D1 — `udp.rawBaseline` stability scenario (harness, additive)

New scenario in `benchmarks/WinForward.Benchmarks/Stability/` that mirrors the soak's socket
topology with raw sockets and no product code: sender loop (same pacing as `UdpLossScenario`) →
forwarder socket (recv + send) → echo socket (recv + send-back) → forwarder → sender-side receive
count. 4 async socket hops, 2 forwarding loops — identical shape to the product chain minus
locks/coordinator/session/codec. Metrics: `achievedPps`, `lossRate`, `sendLoopOverflows` at the
requested target — same JSONL schema. Registered in `--scenario all` and `--quick`.

Purpose: produces B_linux / B_win, the environment ceilings the acceptance bars are computed
from. Also the control variable: product overhead = baseline numbers vs `udp.lossRate` numbers.

### D2 — Windows sync-send fast path in `Socks5UdpTransport` (product)

Follows the hot-path convention #3 (sync warm shape / async slow shape):

- The relay socket is created with `NonBlocking = true` (one-time, at `CreateAsync`).
- `SendAsync` becomes a non-async entry: encode into the reusable `_sendBuffer` under the
  existing `_sendGate`, then warm-path `_socket.SendTo(...)` — a non-blocking UDP send either
  completes inline (zero allocation, zero IOCP, works identically on Linux) or throws
  `SocketException(WouldBlock)`.
- WouldBlock / any transient fallback goes to the existing `SendToAsync` slow path (async
  method, honors the cancellation token). The fallback is rare (kernel send queue full), so its
  allocation is not on the steady-state path.
- The receive loop's `ReceiveFromAsync` is unaffected by non-blocking mode (async ops never block).
- `_sendGate` serialization and the R5 shared-buffer contract stay unchanged.
- Cancellation semantics: the sync path completes or throws immediately, so token cancellation
  only matters on the fallback, which keeps honoring it.

Expected effect: removes one IOCP hop per datagram on Windows (the send side), and removes the
async state machine on both OSes (hot-path #3 compliance for this method).

### D3 — Amortize per-datagram activity propagation (product)

`UdpProxySession.TouchActivity` runs per send AND per receive: `GetUtcNow` + lock +
`_activityObserver` → `UdpAssociationTable.TryTouch` (table lock + lookup). Change:

- `_lastActivityTicks` stays updated on every operation (Interlocked, no lock) — idle-expiry
  semantics (`TryBeginExpiry` reads `LastActivityUtc`) remain exact per datagram.
- The `_activityObserver` propagation (association-table touch) is throttled to at most once per
  100 ms per session via Interlocked compare-exchange on a last-propagation timestamp. The
  association table serves reverse-leg classification and sweep pruning (seconds-scale idle
  timeouts), so 100 ms granularity loses nothing observable.
- The `_activityGate` lock discipline around `_activeSends` / `_expiring` is NOT touched
  (correctness of the expiry race relies on it; an uncontended lock is not the bottleneck).

### D4 — Harness pacing: `timeBeginPeriod(1)` on Windows

`UdpLossScenario` (and the new baseline scenario) raise the Windows timer resolution to 1 ms for
the process lifetime of the run (P/Invoke `timeBeginPeriod`/`timeEndPeriod` in try/finally,
no-op on other OS) so 10 ms `Task.Delay` ticks fire on time. No spin-waiting (burning a core
distorts the measurement). This starts a new comparison series for Windows stability numbers —
documented in `benchmarks/README.md` (the 2026-08-29 Windows rows used the old pacing).

### D5 — Receive path: explicitly out of scope for this task

`ReceiveFromAsync` is genuinely async; portable receive batching (recvmmsg) does not exist on
Windows. Revisit only if D1-D4 leave the acceptance bars unmet (decision gate in implement.md).

### D6 — Patient setup admission in `UdpProxyCoordinator` (product; added 2026-08-29 after diagnosis)

The per-hop census (instrumented harness run, 5000/2000 pps) closed the loss books exactly:
every lost datagram died in the setup-failure retry chain. 256 simultaneous flows →
`_setupLimiter.WaitAsync(TimeSpan.Zero)` admits 8, the other 248 throw
`IOException("concurrency cap reached")` → `RemoveSlotAsync(writeTombstone: true)` drops the
already-accepted triggering datagram (setup-queue drain) and writes a 1s cooldown; retry waves
keep tripping the cap for tens of seconds, each failure eating one accepted datagram. After all
sessions exist the chain is loss-free (relay forward/reply counts match exactly).

Fix: in `CreateSessionAsync`, replace the zero-wait probe with a patient
`_setupLimiter.WaitAsync(cancellationToken)` — the 9th+ setup QUEUES behind the 8-wide gate
instead of failing. Bounded-concurrency contract preserved; genuine setup failures (server
dead) still tombstone with the 1s cooldown; shutdown cancellation still unwinds waiters
(`OperationCanceledException` → no tombstone, existing catch paths). Flash-crowd math: 256
flows × ~3ms handshake / 8 concurrent ≈ 100ms full ramp, zero drops; per-flow setup queue
(32 packets) absorbs the ramp window at every soak rate.

Tests: the existing tests that assert the cap-failure IOException/cooldown path must be updated
to the new semantics (queued admission, no drop, no cooldown on cap); add a flash-crowd test
(e.g. 64 concurrent first-datagrams on distinct flows through a slow fake factory gated on a
barrier) asserting: all setups eventually succeed, every accepted datagram is forwarded, zero
`udp.setupqueue.dropped`.

### D7 — Diagnostic instrumentation posture (harness)

Keep the per-hop `hops` counters (Interlocked, zero distortion) in `udp.lossRate` result rows.
Make the trace-capturing `CountingRuntimeLogger` opt-in via a private const bool (default
false) so default runs are undistorted; `productEvents` omitted when disabled.

## Acceptance computation (from PRD R2/R3, baseline-relative)

Let `B_os` = `udp.rawBaseline` achieved pps at the 25k default target on OS ∈ {linux, windows},
and `C_os` = post-fix `udp.lossRate` achieved pps at the 25k default target (the ceiling when
under-run saturates the pipeline).

- **A1 throughput (Windows):** `C_windows ≥ 0.7 × B_windows`.
- **A2 zero loss (both OS):** `T_zero = 25000` if `C_os ≥ 25000`, else `T_zero = ⌊0.8 × C_os⌋`;
  the `udp.lossRate` run at target `T_zero` reports `lossRate == 0` (exact zero) with
  `outOfOrder == 0`, `duplicates == 0`.
- **A3 Linux no-regression:** `C_linux ≥ 22000` and default-run `lossRate ≤ 0.19%`.
- **A4 discipline:** `dotnet test -c Release` green; perf benchmark allocation columns unchanged
  for the affected paths (steady-state send path stays zero-allocation).

## Risks & mitigations

- **Non-blocking `SendTo` behavioral surprises** (e.g. exceptions other than WouldBlock on some
  stack): the fallback catch is `SocketException`-wide → slow path; a persistent fault still
  surfaces through the existing failure path (`RemoveSlotAsync`). Unit tests cover the
  sync-works and disposal-during-send shapes.
- **`timeBeginPeriod` process-wide side effect:** benchmark process only; restored in finally.
- **Series discontinuity:** pacing fix changes Windows comparability — handled by the README
  series note, same as the 2026-08-29 BenchmarkDotNet rewrite note.
- **Rollback:** D1 (additive scenario), D2, D3, D4 are separate commits; any can be reverted
  independently. No schema/config/protocol changes.

## Windows runbook (established this session)

evil-winrm-py to 192.168.100.2 → `dotnet publish benchmarks -r win-x64 --self-contained
/p:PublishSingleFile=true` on Linux → upload zip → Expand-Archive → run
`WinForward.Benchmarks.exe --stability ...` via Start-Process with redirected stderr, or
`runps` a local .ps1. Results land in JSONL; download for comparison. Details:
`benchmarks/results/2026-08-29-windows-real-machine/README.md`.
