# Session creation cost redo: clean out-of-process harness and re-anchored budget

## Goal

Redo the evidence program of `09-21-session-creation-cost` on a harness whose real-dial
instruments no longer count the in-process benchmark SOCKS5 server's allocations as the
client's, then re-derive the per-session numbers, the budget anchors and the reduction
ranking from the corrected data.

## Background (confirmed facts, 2026-09-22)

- **Trigger.** The archived framework claim — "control connect + greeting = 77,448 B/session"
  — was investigated and reproduced as a harness artifact. `LoopbackSocks5UdpServer` runs in
  the same process as the measured client and allocates, per accepted control connection:
  a 64 KiB relay-loop buffer (`benchmarks/WinForward.Benchmarks/Stability/LoopbackSocks5UdpServer.cs:240`,
  started unconditionally at `:150`), a per-connection UDP relay socket with a 4 MiB kernel
  receive buffer (`:139-144`), plus control-side arrays/stream/socket (`:169-210`) and a
  dictionary entry + `ContinueWith` (`:106-112`).
- **Measured A/B** with the real product client (`Socks5UdpTransportFactory.CreateAsync` +
  `DisposeAsync`, 1→1000 marginal; scratch probes `/tmp/dialprobe`, `/tmp/socketprobe`):

  | instrument | B/session |
  |---|---:|
  | real client × out-of-process SOCKS5 server (clean) | **8,346** |
  | real client × in-process server, relay loop disabled | 15,504 |
  | real client × in-process server, faithful harness shape | 83,707 |
  | recorded `FrameworkSetupBenchmarks.RealTransport_CreateDisposeAsync` | 83,442 |

  Faithful reproduction error 0.3%. Decomposition: 68,203 B/session = the 64 KiB buffer +
  relay socket/loop machinery; 7,158 B/session = the server's control-side per-connection
  work. The client's own path is ~8.3 KB/session.
- **Every real-dial instrument is inflated by ~75 KB/session**: the framework ladder
  (83,442), the real-transport probe marginal (92,255), the churn matrices (90,829–92,451),
  and the derived anchors in `hot-path.md` §3 (T3 ≤95,000 churn / ≤84,000 framework) and §6.
  Sanity closure from the recorded numbers: 92,255 ≈ 75.4 KB harness + 8.3 KB client
  framework + 5.8 KB bookkeeping + ~2.8 KB response/retire residue.
- **Clean instruments**: the Noop probe (5,737 B/session), the S0–S5 stage decomposition
  (fake transports) and `udp.sessionFootprint` do not touch the server; their values stand.
- **Client-side path breakdown** of the real 8.3 KB/session: control connect + greeting
  ≈2.4 KB (a bare .NET TCP socket lifecycle floor probe measures 2,589 B/session for the same
  shape), UDP ASSOCIATE ≈2.9 KB, relay socket 576 B, self-traffic 160 B, transport ctor
  ≈2.4 KB (1,536 B send buffer dominates).
- **Environment.** All measurements on this Linux dev box; ns/µs are ordinals only
  (same-binary drift up to 2.8×). `src/**` stays untouched; changes are benchmark-side.

## Requirements

- **R1 — Out-of-process harness.** Add a child-process server mode to the benchmark CLI that
  hosts the existing loopback SOCKS5 UDP server (and its echo/discard receiver), reports its
  endpoints on stdout, forwards the associate-delay knob, and dies with its parent; add a
  parent-side helper that spawns/awaits/kills it. The in-process default stays unchanged so
  previously recorded runs remain reproducible; the real-dial instruments opt in.
- **R2 — Framework ladder, re-measured.** `FrameworkSetupBenchmarks` (create+dispose,
  connect+greeting, connect+ASSOCIATE, relay socket, self-traffic), ≥3 runs against the
  out-of-process server; corrected framework table with the harness share quantified (not
  assumed).
- **R3 — Probe marginals and churn, re-measured.** `UdpSessionBenchmarks` real-transport
  marginal and `udp.churn` wave matrix (N∈{48,128,256} × D∈{0,5,20} ms) + sustained shape
  (30 s, D∈{0,5}), ≥3 runs per cell; corrected whole-cycle per-session cost with GC counts
  and ordinals.
- **R4 — Clean instruments, cross-checked.** Re-run the Noop probe, the S0–S5 stage
  decomposition and the footprint scenario; confirm they match the recorded values within
  the recorded spreads (or explain any deviation before reusing old numbers).
- **R5 — Reconciliation and anchors.** Produce corrected equivalents of
  `framework-decomposition` / `churn-measurements` / `reconciliation-and-decision` in this
  task's `research/`, with the corrected residual attribution; re-anchor `hot-path.md` §3
  (T3, framework sub-anchor) and §6 with explicit falsification conditions; re-rank the
  reduction directions against the corrected magnitudes.
- **R6 — Errata on the record.** Add a short correction banner to `framework-decomposition.md`,
  `churn-measurements.md` and `reconciliation-and-decision.md` in the archived task,
  pointing at the corrected numbers. Archived text stays as the historical record; this task
  owns the corrected truth.

## Acceptance Criteria

- [x] A documented command set re-runs every real-dial instrument against the out-of-process
  server; raw logs/JSONL archived under `research/raw/` (52 `.log` + 52 `.load` + 37 `.jsonl`).
- [x] Attribution: the corrected framework table sums to the isolated path exactly (7,952.0);
  the recorded probe-derived difference is fully accounted — 86,520.2 = 75,233.6 harness +
  11,294.2 clean, of which the clean share is 70.4 % framework table and 29.6 % residue. The
  residue is named, bounded at both retire shapes (≈0.4 KB per-wave / ≈3.3 KB
  populate-then-drain) and carried as an explicit gap; the ≥95 % target is superseded for the
  *residue split* by the echo-fed shape deviation documented in `design.md` §4.1 (there is no
  64 KiB-class harness term left in any corrected figure).
- [x] Churn rows report per-session allocation for the whole fire → responses → retire cycle,
  3 runs per cell (27 cell runs + 6 sustained), with the harness share quantified
  (≈77–79 decimal KB/session of the recorded figures).
- [x] R4 cross-check: stage decomposition re-runs byte-identical on every deterministic row
  (F/H/C1/C2/S0/S1) and within combined spreads on the noisy legs (S2–S5, S3 bracket not
  reproduced: +502.8 vs the archived +356.9); Noop probe within spread; footprint within the
  recorded band; teardown exception counts byte-identical (130/328/2,128). Deviations are
  explained in `research/churn-measurements.md` §in-process sanity cell.
- [x] `hot-path.md` §3/§6 carry the re-anchored numbers, the out-of-process rule, and an
  anchor-falsification sentence; the superseded figures are explicitly marked incomparable.
- [x] Errata banners present in `framework-decomposition.md`, `churn-measurements.md` and
  `reconciliation-and-decision.md` (the archived §3 text quoted contaminated figures);
  `noop-decomposition.md` is server-free and unaffected.
- [x] `src/**` untouched (verified by `git status`); `dotnet build -c Release` zero-warning,
  804 tests green, `dotnet format --severity info --verify-no-changes` exit 0 with empty
  output, `jb inspectcode` zero `<Issue>` (all verified by the `trellis-check` pass).
- [x] Every harness change is documented with its exact command lines in `design.md` §3/§4 and
  `implement.md` Step 4.

## Out of scope

- The reduction work itself (teardown, session tier, admission, control-connection reuse) —
  this task re-anchors evidence only; the ranked list is refreshed, not executed.
- TCP-layer session cost — still deferred; note that `LoopbackSocks5TcpServer` carries the
  same in-process class of artifact for a future follow-up.
- Windows guest re-verification (same Linux-only envelope as the original task).

## Open Questions

- None blocking.
