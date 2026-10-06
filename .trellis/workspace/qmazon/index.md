# Workspace Index - qmazon

> Journal tracking for AI development sessions.

---

## Current Status

<!-- @@@auto:current-status -->
- **Active File**: `journal-2.md`
- **Total Sessions**: 59
- **Last Active**: 2026-10-07
<!-- @@@/auto:current-status -->

---

## Active Documents

<!-- @@@auto:active-documents -->
| File | Lines | Status |
|------|-------|--------|
| `journal-2.md` | ~35 | Active |
| `journal-1.md` | ~1996 | Archived |
<!-- @@@/auto:active-documents -->

---

## Session History

<!-- @@@auto:session-history -->
| # | Date | Title | Commits | Branch |
|---|------|-------|---------|--------|
| 59 | 2026-10-07 | TCP half-close fidelity: measure the mechanism, ship the close injection, verify on the VM | `7cababb`, `c828548`, `c58a64b` | `master` |
| 58 | 2026-10-06 | appsettings.json configuration surface with the standard Logging section | `4083ef3`, `1ef1a57` | `master` |
| 57 | 2026-10-06 | AOT instruction-set floor: restoring the vectorized checksum and shipping a framework-dependent artifact | `f7bf652`, `5b00109`, `eae25af` | `master` |
| 56 | 2026-10-06 | Logging migration to Microsoft.Extensions.Logging source-generated events | `c584bc5`, `6b35527` | `master` |
| 55 | 2026-10-06 | UoT v2 per-flow UDP transport for SOCKS5 targets | `d77f4e8`, `33c47ae`, `323c76e`, `4a66489`, `7ad76f5` | `master` |
| 54 | 2026-10-05 | R4/R5: remove UDP association sharing (remove-udp-association-sharing) | `1624431`, `e041ee6` | `master` |
| 53 | 2026-10-05 | R3: local targets for proxy-decided UDP flows (local-dns-transport) | `69f89d5`, `9d60898` | `master` |
| 52 | 2026-10-05 | Reply-ownership observability: a count-only counter for misdelivered UDP replies | `fcec029`, `81f4824`, `a843a92` | `master` |
| 51 | 2026-10-05 | UDP shared-association reply ownership: measured, harness corrected, baseline published | `42c2af1`, `2a14b40`, `d5c9cf0` | `master` |
| 50 | 2026-10-05 | Host flow creation logging: the deferred attribution pipeline now emits flow.created, so host flows stop vanishing from the log | `ea6e520`, `c61ce16`, `748e309` | `master` |
| 49 | 2026-10-05 | Host/forwarded rule split: two explicit policy domains, positional rule eligibility, and two pre-existing defects surfaced | `46a167e`, `92c03e6`, `b177533` | `master` |
| 48 | 2026-10-01 | Test project split: twelve layered projects, a shared TestSupport library, and the evidence-backed friend grants | `d368492`, `dda8b83`, `6fb8c16`, `a82d853`, `58ef217`, `47adc1c`, `092e726` | `master` |
| 47 | 2026-10-01 | F6 UDP footprint: 64 KiB relay buffers, a capacity-sized pool and a two-class idle TTL (resident set -78%) | `7dc2979`, `b12baab`, `6457eeb`, `9832012` | `master` |
| 46 | 2026-10-01 | F8 attribution off the pump thread: a bounded pending index and an owner-table epoch coalescer (pump stall 7.5 ms -> 0.02 ms) | `acca507`, `4a0f70e`, `f121a3d`, `ee40987` | `master` |
| 45 | 2026-10-01 | F5 pump I/O: read-first drains with a self-healing ABI guard, and an event-driven idle wake (31x idle CPU) | `6cb27bf`, `a488a99`, `ec0762f`, `e888921` | `master` |
| 44 | 2026-10-01 | F4 keys and parsing: a 64 B interned key, one parse per frame, lock-free trackers (and a live mis-rewrite found by hardening) | `31b340b`, `7cc794a`, `d43d85d`, `59e80fc` | `master` |
| 43 | 2026-09-30 | F2 warm-path lock chain: direct-mapped warm cache (and the ConcurrentDictionary mechanism rejected by the 0 B gate) | `ea13924`, `02fee48`, `cee7063`, `fc0a867` | `master` |
| 42 | 2026-09-30 | F3 expiry sweeps: bounded-pause rounds at minimal hold granularity (and two measurement-driven reversals) | `c73506d`, `f9361da`, `18671e8`, `3563bdc` | `master` |
| 41 | 2026-09-30 | Test stabilization part 3: the fake server's reply-counter race (and the residual family's real boundary) | `d149cb4`, `eb8c42e`, `6cace12`, `aa6c29f` | `master` |
| 40 | 2026-09-30 | Test stabilization part 2: the residual exact-gate lump and the health-signal race | `3962832`, `d3b50c1`, `ac98dbc`, `4a23906` | `master` |
| 39 | 2026-09-30 | Test stabilization part 1: the tiering flake and the SetupExecutor enqueue/dispose race | `9b692c2`, `fa227b6`, `963bbe4`, `dc16b85`, `136d278` | `master` |
| 38 | 2026-09-30 | Benchmark coverage for the remaining structural findings (F2-F8) | `47c3110`, `312add1`, `c18b06c`, `0538819`, `a43645d`, `4d5fb76` | `master` |
| 37 | 2026-09-29 | TCP redirect lane-batched injection + in-place rewrite (research F1) | `98d7232`, `1a56eec`, `9225ae6`, `73d21f2`, `57c7fe5` | `master` |
| 36 | 2026-09-22 | Admission and capacity pre-seed split | `ed204e1`, `a14ebd5`, `30bbed9` | `feat/transport-lifecycle` |
| 35 | 2026-09-22 | UDP teardown and session-tier allocation reduction | `cfd56fd`, `27d0be1`, `2da59b0`, `b2ee992` | `feat/transport-lifecycle` |
| 34 | 2026-09-22 | Session creation cost redo: out-of-process harness, corrected numbers, re-anchored T3 | `d4aeda5`, `6588265`, `dc37516`, `c55e5de` | `feat/transport-lifecycle` |
| 33 | 2026-09-22 | Session creation cost: UDP full-stack decomposition, churn measurement, and the re-anchored budget | `a174f88`, `417ad8d`, `c303837`, `b7c7e66` | `feat/transport-lifecycle` |
| 32 | 2026-09-21 | Complete the structured-concurrency program: C4 migrates the remaining lifecycle owners | `a3c0783`, `ccb0def` | `feat/transport-lifecycle` |
| 31 | 2026-09-21 | Quiescence scope migration: TCP/UDP cluster (C3) | `a0e2b35` | `feat/transport-lifecycle` |
| 30 | 2026-09-21 | Lifetime enforcement analyzers: four WF rules wired into src/** behind a proven allowlist (C2) | `2bfed0a`, `db52a93` | `feat/transport-lifecycle` |
| 29 | 2026-09-21 | Quiescence scope primitive: measured gate choice and a fragile allocation gate found (C1) | `a167a24` | `feat/transport-lifecycle` |
| 28 | 2026-09-20 | Transport lifecycle hardening: ownership, quiescence, session state | `4a2007e` | `feat/transport-lifecycle` |
| 27 | 2026-09-20 | Adopt the JetBrains inspectcode gate and zero its report | `7b199df`, `25db2f5`, `1d612ea`, `baec0a2`, `b19a779`, `40df6e5`, `fb083cb` | `master` |
| 26 | 2026-09-20 | Analyzer diagnostics cleanup: 1369 → 0 (dotnet format --severity info) | `fe21bd3`, `a6c15e0`, `b8484a1`, `1fafb33`, `aa7261d`, `d185cf1` | `master` |
| 25 | 2026-09-19 | Design-deepening refactors (R1-R12) + TCP clock seam follow-up | `d434d00`, `804c790`, `7a8df52`, `ac7b957`, `a634ae7`, `53ddcb4`, `cb70b0d`, `cfad0d6`, `3a9ccfc`, `a4322d3`, `31191ad`, `18d2243`, `10e5798`, `d9ca244`, `e6215ba` | `master` |
| 24 | 2026-09-19 | Compat API cleanup + design-health review | `2ea413d` | `master` |
| 23 | 2026-09-19 | GC-less zero-allocation hot paths (M0-M5) | `c75f30e`, `97f0e01`, `c2ac421`, `fef03fc`, `cf1a570`, `b51965b`, `a0e8024`, `8c6cfe0` | `master` |
| 22 | 2026-09-12 | Scope-sized pass-lane table | `f1d1c1b` | `master` |
| 21 | 2026-09-12 | Recover generation startup from stale adapter handles (native 87) | `975aede` | `master` |
| 20 | 2026-09-08 | Fix adapter.degraded nativeError=87 storm via in-process layered refresh | `398376e` | `master` |
| 19 | 2026-09-06 | UDP burst TTL re-attribution fix | `76abc37`, `dd3985b` | `master` |
| 18 | 2026-09-06 | UDP burst-establishment benchmark + baseline matrix | `4a4d6a6`, `4b00d0b` | `master` |
| 17 | 2026-09-06 | Tolerate TFO SYN-with-payload in TCP redirect | `d06f002` | `master` |
| 16 | 2026-08-30 | Driver resilience: transient-read retry + single-pump degradation, off-pump TCP SYN setup (backlog #10) | `461df0d` | `master` |
| 15 | 2026-08-30 | hot-path-revival child landed: warm entry revived in production via WantsPacket prefilter | `795cc1f`, `679428a` | `master` |
| 14 | 2026-08-30 | fast-hardening child landed: client RST + NoDelay + pooled buffers + throttle | `b0e0c6d`, `a06a587` | `master` |
| 13 | 2026-08-30 | Preserve 2026-08-30 proxy perf/stability deep-dive research | - | `master` |
| 12 | 2026-08-30 | Proxy stability and performance hardening (S1-S6, P1-P2) | `11acc06`, `d0bcd83`, `2f544ab`, `1e94849`, `adfb4ec`, `a0d87da`, `323f747` | `master` |
| 11 | 2026-08-29 | SOCKS5 full-path benchmarks and performance | `e5c9bf2` | `master` |
| 10 | 2026-08-29 | UDP stability: patient setup admission, sync-send fast path, zero loss on both OSes | `47735b6` | `master` |
| 9 | 2026-08-29 | Rewrite benchmarks on BenchmarkDotNet with stability soak runner | `7cf8b9d` | `master` |
| 8 | 2026-08-29 | Fix UDP loss design flaws (R1-R6) with hardware smoke test | `d9a61ed` | `master` |
| 7 | 2026-08-28 | Perf hotspots: zero-allocation packet pipeline | `f161556` | `master` |
| 6 | 2026-08-28 | Refactor oversized files into deep modules | `ae30c1d`, `d56b786`, `068aa14`, `7934468` | `master` |
| 5 | 2026-08-28 | fix-minor-races 落地 + eof-reset 任务树整体收官 | `e2dd831`, `31eeb42` | `master` |
| 4 | 2026-08-28 | fix-table-lifecycle: teardown 墓碑与 flow 豁免 | `604eeb3`, `32fe5a8` | `master` |
| 3 | 2026-08-28 | fix-port-budget: tcpFlowCapacity 预算落地 | `19ce572`, `16022a8` | `master` |
| 2 | 2026-08-27 | Datapath throughput: batched reads, pooling, hardware smoke | `fec967a`, `5ad8e60`, `2519b0d`, `3f30cf8` | `master` |
| 1 | 2026-08-27 | Fix host UDP reinjection delivery failure (hardware-verified) | `4e3c866`, `4c36ad4` | `master` |
<!-- @@@/auto:session-history -->

---

## Notes

- Sessions are appended to journal files
- New journal file created when current exceeds 2000 lines
- Use `add_session.py` to record sessions