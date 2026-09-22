# Design — out-of-process loopback server and the corrected measurement program

## 1. Problem statement

Every real-dial instrument runs the client and the loopback SOCKS5 server in one process, so
the server's per-connection allocations land in `GC.GetTotalAllocatedBytes` next to the
client's. The measured contamination is ~75 KB/session (64 KiB relay-loop buffer + 4 MiB
relay socket + control-side arrays/stream), i.e. ~9× the client's own ~8.3 KB/session
framework path. The fix is process separation, which is also the production topology
(sing-box runs as a separate process).

## 2. Architecture and boundaries

Three process tiers when a real-dial instrument runs:

```
BDN host ──spawns──> BDN benchmark process ──spawns──> server child process
                      (measured client)                 (LoopbackSocks5UdpServer
                      + coordinator, sink                + EchoReceiver)
```

- **Ownership**: the child owns the loopback SOCKS5 UDP server and the echo/discard receiver
  it relays into. The parent owns the client, the coordinator and the response sink. Only the
  parent's allocations are read; the child's are invisible to the instrument by construction.
- **Why the receiver moves too**: in the in-process shape the relay's forward destination
  (`EchoReceiver`) is harness plumbing in the measured process. Moving it into the child
  removes its per-datagram allocations from the parent's counters and keeps the
  server↔receiver hop inside one process.
- **Unchanged**: `RelaySocket_CreateBindClose` and `SelfTraffic_RegisterRelease` (no server
  involved), all fake-transport instruments (Noop probe, S0–S5 stage decomposition,
  `udp.sessionFootprint`), and the packet hot path.

## 3. Child-process server mode (CLI contract)

New stability mode, reusing the existing classes without behavior change:

```
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --serve-socks5-udp --flows <N> [--dial-delay-ms <D>]
```

- Builds `EchoReceiver(N)` + `LoopbackSocks5UdpServer(receiver.Endpoint, associateDelay)`
  exactly as `UdpChurnScenario` does today.
- Prints **one** JSON line on stdout, then stays alive:
  `{"controlPort":<P>,"echoPort":<E>}`
- Lifetime contract: exits when stdin reaches EOF (parent died) or on SIGTERM/SIGINT. No
  other stdout output after the handshake line; diagnostics go to stderr.

## 4. Parent-side helper and per-instrument wiring

New helper `ExternalLoopbackSocks5UdpServer` (benchmark project, `Stability/`):

- `StartAsync(int flows, TimeSpan associateDelay, CancellationToken)` — spawns the same
  executable (`Environment.ProcessPath` with the current assembly), redirects stdin/stdout/
  stderr, reads the handshake line with a 15 s timeout, fails fast with the child's stderr
  content on timeout/parse error.
- Exposes `ControlEndpoint` (the handshake still reports the echo port; no current instrument
  needs it in the parent, so the helper does not surface it); `IAsyncDisposable` closes stdin, waits 5 s,
  then `Kill(entireProcessTree: true)`; `Process.Exited` is wired so a dead child surfaces as
  an immediate instrument error, not a hang.

Wiring:

| Instrument | Change |
|---|---|
| `UdpSessionBenchmarks` (real rows) | `[GlobalSetup]` starts the helper when the real-transport case runs; `_socks` points at `ControlEndpoint`. Noop rows ignore it. |
| `FrameworkSetupBenchmarks` | Same; the ladder's server-using variants take `_socks` from the helper. In-process default remains available for the A/B cross-check (§7). |
| `UdpChurnScenario` | New `--socks5-external` option: when set, do not construct `EchoReceiver`/`LoopbackSocks5UdpServer`; start the helper instead (same knobs: `--burst-flows`, `--dial-delay-ms`). |
| `UdpBurstScenario` / `UdpLossScenario` / `GcSoakScenario` | Not required by this task (no allocation sampling on the dial path). Leave in-process; note as follow-up. |

`udp.sessionFootprint` needs no change (fake transports).

### 4.1 Accepted deviations from §3/§4 (review of the harness implementation, 2026-09-22)

- **The external probe is echo-fed, not discard-fed.** The child always hosts the
  `EchoReceiver` pair, so the out-of-process probe's sessions now receive one echoed response
  each, whereas the recorded probe's in-process shape relayed into a silent discard socket.
  The readiness signal is therefore the parent-visible count of echoed responses
  (`ResponseCountingSink`), not the child's forwarded counter. This is a *shape* change to
  the probe, and it is kept deliberately: it is production-realistic (real DNS flows receive
  responses) and it makes the previously-unattributed "1.6–3.3 KB/session response/retire
  residue" directly measurable. The corrected probe row must be reported with this shape
  named, and the reconciliation must present the recorded discard shape (92,255, harness-
  inflated) next to it rather than as a bare number-for-number correction.
- **The in-process default stays byte-identical**: the probe keeps the singleton
  `NoopUdpResponseSink.Instance` in-process (the counting sink exists only under
  `WINFORWARD_BENCH_EXTERNAL_SERVER=1`).
- `ThrowIfExited()` and `IsEnabled`/`EnvironmentVariable` are helper additions beyond §4;
  they exist to make a dead child fail the instrument immediately (a `Process.Exited` event
  alone cannot interrupt a polling wait).

## 5. Measurement protocol (unchanged conventions)

- Allocation-only decision unit: `GC.GetTotalAllocatedBytes` (BDN `Allocated`, or the
  scenario's own sampler for churn). ns/µs are ordinals on this box (same-binary drift up to
  2.8×) and are never used as a claim.
- ≥3 runs per cell, one process at a time, `/proc/loadavg` + ISO timestamps captured per run
  (`research/raw/*.load`).
- Marginal convention: `(alloc@1000 − alloc@1)/999` for sweeps; BDN `--job short` for the
  ladder; the churn wave/sustained command lines are those of the original task with
  `--socks5-external` added.
- Shape discipline: every number is quoted with the window shape it was measured in
  (single-pass vs matched `InvocationCount`, wave vs sustained) — the original task's
  §2 shape ladder remains the convention.

## 6. Compatibility and rollback

- The child mode and the helper are additive; the in-process default is untouched, so every
  recorded command line from the original task still reproduces its (contaminated) numbers.
- Rollback points (each leaves the repo green):
  1. after the child mode + helper land (harness-only; the A/B of §7 is already possible);
  2. after the ladder re-measure;
  3. after the full campaign;
  4. after the research docs;
  5. spec edit (`hot-path.md` §3/§6) last, as its own reviewable diff.
- `src/**` is untouched throughout; quality gates run as usual
  (`dotnet build -c Release` zero-warning, tests, `dotnet format --verify-no-changes`,
  `jb inspectcode`).

## 7. Mandatory A/B cross-check (artifact quantification inside this task)

Before the campaign, run the ladder once in-process (default) and once out-of-process:
the per-session delta must reproduce the scratch-probe finding (~83.7 KB in-process vs
~8.3 KB out-of-process, i.e. ~75 KB/session harness share; relay-loop-disabled shape lands
near 15.5 KB). This makes the correction self-evidencing from the repo's own harness instead
of citing `/tmp` probes. If the delta does not reproduce, stop and re-diagnose before
re-measuring.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Orphaned child processes | stdin-EOF lifetime + kill-on-dispose + `Kill(entireProcessTree)`; helper used via `await using` everywhere |
| Handshake hang / stdout pollution | single-line JSON contract, 15 s timeout, stderr for diagnostics, child stdout otherwise silent |
| Child GC pressure in sustained runs (~1.2 GB of 64 KiB relay buffers over ~19k connections) | acceptable: child counters are not read; verify child RSS stays bounded, note ServerGC option if it does not |
| Cross-process loopback changes latency | ordinals only; the topology now matches production (server is a separate process) |
| Results drift from the scratch-probe expectation | §7 gate: re-diagnose before writing any corrected number |

## 9. Expected magnitudes (sanity gates for the redo)

| Quantity | Expected (clean) | Recorded (contaminated) | Observed (2026-09-22, §3 of the research docs) |
|---|---:|---:|---:|
| framework ladder: create+dispose | ≈8.3 KB/session | 83,442 | **7,952.0** |
| framework ladder: connect+greeting | ≈2.4 KB/session | 77,448 | **3,792.2** |
| framework ladder: ASSOCIATE delta | ≈2.9 KB/session | 2,872 | **959.0** |
| real probe marginal | ≈8.3 + 5.8 + residue KB/session | 92,255 | **17,021.2** (echo-fed shape) |
| churn whole cycle | ≈15–17 KB/session | 90,829–92,451 | **13,248.9–14,069.9** wave / **13,720.2–13,868.6** sustained |
| Noop probe marginal | 5,737 (unchanged) | 5,737 | **5,727.0** (ext) / **5,734.0** (in) |

The pre-run expectations for connect+greeting (≈2.4 KB) and the ASSOCIATE delta (≈2.9 KB) came
from the standalone scratch probe and the archived decomposition; the measured ladder supersedes
them — the archived ASSOCIATE delta was itself harness-inflated (clean value 959.0 B), which is
exactly the class of error this redo exists to remove. The create+dispose, probe, churn and
Noop expectations were met.
