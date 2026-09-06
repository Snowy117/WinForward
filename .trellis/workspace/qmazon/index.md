# Workspace Index - qmazon

> Journal tracking for AI development sessions.

---

## Current Status

<!-- @@@auto:current-status -->
- **Active File**: `journal-1.md`
- **Total Sessions**: 18
- **Last Active**: 2026-09-06
<!-- @@@/auto:current-status -->

---

## Active Documents

<!-- @@@auto:active-documents -->
| File | Lines | Status |
|------|-------|--------|
| `journal-1.md` | ~494 | Active |
<!-- @@@/auto:active-documents -->

---

## Session History

<!-- @@@auto:session-history -->
| # | Date | Title | Commits | Branch |
|---|------|-------|---------|--------|
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