# Journal - qmazon (Part 1)

> AI development session journal
> Started: 2026-08-27

---



## Session 1: Fix host UDP reinjection delivery failure (hardware-verified)

**Date**: 2026-08-27
**Task**: Fix host UDP reinjection delivery failure (hardware-verified)
**Branch**: `master`

### Summary

Root-caused and fixed host UDP response delivery: SendPacketToMstcp is adapter-bound, and pre-fix scope[0] reinjection was dropped by Windows strong-host receive validation (pktmon proof: 'Not locally destined', 0/20 queries). Host flows now reinject via FlowKey.OriginAdapterId; verified 20/20 plus 5/5 on 1s-settle restart on the two-adapter Win11 fixture with a remote sing-box SOCKS5 server. Also added wall-clock timestamp prefixes to every runtime log line. Specs updated (windows-ndisapi host-flow binding contract, logging guidelines).

### Git Commits

| Hash | Message |
|------|---------|
| `4e3c866` | (see git log) |
| `4c36ad4` | (see git log) |

### Status

[OK] **Completed**


## Session 2: Datapath throughput: batched reads, pooling, hardware smoke

**Date**: 2026-08-27
**Task**: Datapath throughput: batched reads, pooling, hardware smoke
**Branch**: `master`

### Summary

Analyzed EOF/reset root causes into a parent task with four children; completed the datapath-throughput child: ETH_M_REQUEST batched ReadPackets ABI (export-verified against the real DLL), pump batching (cap 32, in-order), ArrayPool-backed frame lease with the return point moved to ProcessAsync finally (design §4.4 audit falsified the complete-after-read assumption), in-place TCP rewrite (RecordClientSyn moved before write), NdisPacketBufferPool for injection. Linux benchmark 2.44M pps steady state (~669B/pkt clones remain for phase 2); WinLtsc smoke with real ndisapi.dll: 1497/1497 captured/completed paired, zero failed/warn, 15 relays. Spec contracts landed in windows-ndisapi.md.

### Git Commits

| Hash | Message |
|------|---------|
| `fec967a` | (see git log) |
| `5ad8e60` | (see git log) |
| `2519b0d` | (see git log) |
| `3f30cf8` | (see git log) |

### Status

[OK] **Completed**



## Session 3: fix-port-budget: tcpFlowCapacity 预算落地

**Date**: 2026-08-28
**Task**: fix-port-budget: tcpFlowCapacity 预算落地
**Branch**: `master`

### Summary

端口预算任务完成：tcpFlowCapacity 配置字段（默认 4096、1..8192、>4096 警告）、单一来源派生 coordinator+table 容量、复用 capacity gate + info 摘要计数；共享 listener 经可行性研究否决（byTranslatedListener 1:1 硬阻塞）。332/332 测试，trellis-check 零缺陷，README/spec 同步。Windows 压测为遗留可选项。

### Git Commits

| Hash | Message |
|------|---------|
| `19ce572` | (see git log) |
| `16022a8` | (see git log) |

### Status

[OK] **Completed**


## Session 4: fix-table-lifecycle: teardown 墓碑与 flow 豁免

**Date**: 2026-08-28
**Task**: fix-table-lifecycle: teardown 墓碑与 flow 豁免
**Branch**: `master`

### Summary

表生命周期任务完成：TcpRedirectTombstoneTable 双键墓碑（60s 宽限、容量同源、单一写入点）+ Dropped 静默消费 + flow 谓词豁免（sweep 顺序 tcp→flows→udp）。Windows smoke：notrelevant 113→11（-90%），55 个 grace 丢弃，配对完整零错误。347/347 测试，spec 新增 teardown-grace 契约章节。

### Git Commits

| Hash | Message |
|------|---------|
| `604eeb3` | (see git log) |
| `32fe5a8` | (see git log) |

### Status

[OK] **Completed**


## Session 5: fix-minor-races 落地 + eof-reset 任务树整体收官

**Date**: 2026-08-28
**Task**: fix-minor-races 落地 + eof-reset 任务树整体收官
**Branch**: `master`

### Summary

D 组竞态修复：RST 动态序号跟踪（RFC793 wrap-aware）、注入失败显式化（reason=injectionFailure 经墓碑单写点）、SOCKS5 预算 10s×2（原最坏 150s）；smoke 零回归（820/820 配对）。父任务 eof-reset-design-flaws 集成验收全勾选归档：notrelevant 113→11（-90%）、端口预算 4096 先行截流、基准 2.2-2.5M pps。四子任务全部完成，Windows A/B 压测为已知限制。

### Git Commits

| Hash | Message |
|------|---------|
| `e2dd831` | (see git log) |
| `31eeb42` | (see git log) |

### Status

[OK] **Completed**


## Session 6: Refactor oversized files into deep modules

**Date**: 2026-08-28
**Task**: Refactor oversized files into deep modules
**Branch**: `master`

### Summary

Split all 13 oversized .cs files (metric clarified mid-task to effective lines = non-blank non-comment, cap 400). Tests: extracted 13 TestHelpers files (~620 dup lines removed), split god-class test files into theme files, 353/353 green throughout. Runtime: Socks5Client -> ControlConnection+UdpTransport; UdpProxyCoordinator -> coordinator+Session with TryRemoveSessionAsync merge; TcpProxyCoordinator 1158 -> coordinator+6 modules (FrameRewriter/SequenceObservation/ClientResetInjector/Acceptor/SessionStore/Setup) after user-directed coalescing. NdisApi: deleted dead surface (Version/TryReadPacket/batch SendPackets) and extracted 4 types. Program.cs/ConfigurationModels.cs descoped per user decision (already compliant). Conventions captured in backend spec (directory-structure.md filled, quality-guidelines.md refactor gate).

### Git Commits

| Hash | Message |
|------|---------|
| `ae30c1d` | (see git log) |
| `d56b786` | (see git log) |
| `068aa14` | (see git log) |
| `7934468` | (see git log) |

### Status

[OK] **Completed**


## Session 7: Perf hotspots: zero-allocation packet pipeline

**Date**: 2026-08-28
**Task**: Perf hotspots: zero-allocation packet pipeline
**Branch**: `master`

### Summary

Diagnosed per-packet hotspots via bench suite + alloc-probe bisect. Fixed: IPAddressValue raw UInt128 addresses end-to-end (parser/keys/CIDR/checksums, family-aligned IPv4 masks regression-locked); FlowContext/CapturedFlowPacket structified with enum-driven completion; DispatchAsync split into non-async sync fast path (fat async state machine was heap-allocating ~193B/call) + DispatchSlowAsync; zero-copy pass via native-span parse, lazy lease materialization, in-place capture-buffer reinjection (pump slot contract test); PacketLease per-thread recycle; Socks5Udp TryEncode span path; flat FlowKey hashing; Ip->IP renames. Results: 669->10 B/pkt, gen0 35->0/M pkts, +44-53% pps, encode 1512->4.2 B. Two trellis-check rounds (final CLEAN incl IPPrefix IPv4 always-true mask defect found+fixed). Spec: backend/hot-path.md contracts.

### Git Commits

| Hash | Message |
|------|---------|
| `f161556` | (see git log) |

### Status

[OK] **Completed**


## Session 8: Fix UDP loss design flaws (R1-R6) with hardware smoke test

**Date**: 2026-08-29
**Task**: Fix UDP loss design flaws (R1-R6) with hardware smoke test
**Branch**: `master`

### Summary

Fixed 6 UDP loss amplifiers: non-blocking session setup with bounded FIFO setup queues + 1s cooldown tombstones + SemaphoreSlim(8) cap; skip-and-continue receive loop (oversized/malformed/unexpected-source no longer kill sessions); per-adapter native call gates replacing the global Monitor; 512KB relay socket buffers + pooled in-place response frame building (zero per-datagram managed alloc); serialized transport sends; scoped timeBeginPeriod(1). 380/380 tests, 0 warnings. Hardware-verified on WinLtsc via WinRM + Linux sing-box: 30-query bursts zero loss, SOCKS5 blackhole (SIGSTOP) left adapter traffic healthy with 385ms recovery, adapter modes auto-restored after hard kills. Specs updated (windows-ndisapi gate topology, quality-guidelines skip matrix, error-handling setup convention). Residual: AC6 timing smoke done behaviorally; Windows-side timing instrumentation deferred.

### Git Commits

| Hash | Message |
|------|---------|
| `d9a61ed` | (see git log) |

### Status

[OK] **Completed**


## Session 9: Reorganize WinForward.Runtime into sub-namespaces

**Date**: 2026-08-29
**Task**: Reorganize WinForward.Runtime into sub-namespaces
**Branch**: `master`

### Summary

Split the flat 32-file WinForward.Runtime project into domain sub-namespaces: root keeps dispatch core + logging (FlowDispatcher incl. CapturedFlowPacket/PacketCaptureMetadata/NativeFrameHandle/IPacketActionExecutor/ISelfTrafficGuard, PacketFlowClassifier, IdleExpirySweeper, SelfTrafficRegistry, RuntimeLogging — 15 types total); Capture/ (7), TcpRedirect/ (14, ClientResetInjector reassigned by type dependency, dissolving the Dispatch↔TcpRedirect cycle), UdpProxy/ (4), Socks5/ (2, shared by TCP relay + UDP transport). Pure git mv + namespace rewrite + scripted using fixup (53 files, 12 pruned); zero behavior change, 27×R100 renames verified, build 0 warnings, tests 380/380 identical to baseline. Layout rules and allowed cross-group using edges recorded in .trellis/spec/backend/directory-structure.md. Note for future: root namespace carries 15 types, not just the 5 files' primary types.

### Git Commits

| Hash | Message |
|------|---------|
| `d4464c6` | refactor(runtime): split flat project into Capture/TcpRedirect/UdpProxy/Socks5 sub-namespaces |

### Status

[OK] **Completed**


## Session 9: Rewrite benchmarks on BenchmarkDotNet with stability soak runner

**Date**: 2026-08-29
**Task**: Rewrite benchmarks on BenchmarkDotNet with stability soak runner
**Branch**: `master`

### Summary

Replaced the 840-line hand-rolled benchmark harness with two modes: BenchmarkDotNet 0.15.8 perf benchmarks (all 9 scenario families migrated, statistics from BDN) and a stability soak runner (udp.lossRate via real SOCKS5 UDP dial path, tcp.unexpectedEof with adversarial abort mix, udp.sessionFootprint). Benchmarks now subject to the 400-effective-line limit (spec updated); test baseline corrected to 380. Full gate: zero-warning build, 380/380 tests, full BDN matrix --job short, --stability --quick all green.

### Git Commits

| Hash | Message |
|------|---------|
| `7cf8b9d` | (see git log) |

### Status

[OK] **Completed**


## Session 10: UDP stability: patient setup admission, sync-send fast path, zero loss on both OSes

**Date**: 2026-08-29
**Task**: UDP stability: patient setup admission, sync-send fast path, zero loss on both OSes
**Branch**: `master`

### Summary

Root-caused the UDP soak loss to the setup fail-fast chain (zero-wait cap probe dropped one accepted datagram per failed retry; per-hop census closed the books exactly). Fixes: D6 patient semaphore admission, D2 non-blocking sync-send warm path (hot-path #3), D3 100ms activity-propagation throttling. Harness: udp.rawBaseline scenario, 1ms Windows timer, warmup/windowing, parallel echo loops. Results: Linux exact zero loss at full 25k target (achieved 99.99%, was 89.6%/0.19%); Windows exact zero at T_zero, product = 100.1% of raw baseline (VM environment identified as the throughput gap). 386 tests green, specs updated (error-handling/hot-path/udp-relay), acceptance artifacts under benchmarks/results/2026-08-29-udp-fix/.

### Git Commits

| Hash | Message |
|------|---------|
| `47735b6` | (see git log) |

### Status

[OK] **Completed**


## Session 11: SOCKS5 full-path benchmarks and performance

**Date**: 2026-08-29
**Task**: SOCKS5 full-path benchmarks and performance
**Branch**: `master`

### Summary

Closed the SOCKS5-measurement gap (product positioning: proxy forwarding is the main path, pass/block are byproducts). Phase A: RFC1928 LoopbackSocks5TcpServer, FrameRewriter/Socks5Handshake micro-benchmarks, dispatcher proxy-branch benchmark, UdpSession on real transport with await-ready semantics (old 29x superlinearity was a Noop-enqueue artifact; real path scales linearly), tcp.throughput soak with socks5/bare control modes. Baselines under benchmarks/results/2026-08-29-socks5-perf/. Phase C: C2 cold-edge IPAddressValue storage closed the forwarded-shape 4.8x gap (2887.9->607ns @1400, equals host shape); C2b Proxy joined the dispatcher non-async warm entry (352B->160B, alloc equals Pass) and the always-false action gate it left behind was caught by user review and removed; C1 indexed MAC swap; C3 UDP session bookkeeping 2.1KB->0.4KB (single-slot setup queue, no registered TCS, inlined failure handling, cached delegates, clamped pre-sizing); C4 socks5/bare = 86-94% (acceptance >=70%). trellis-check 7/7 PASS; one pre-existing flaky localized (TcpRedirectSessionStore.cs:293-294 two-lock window, residual fix recommendation). Windows WinLtsc smoke: udp.lossRate zero loss twice, 3 complete TCP redirect/relay cycles via real sing-box (server-side ESTABLISHED connections as evidence, 61 debug events 0 warnings), 6 UDP sessions. Methodology finding recorded: fake-IP upstream router makes egress-IP proof invalid; reliable evidence is the SOCKS5-server connection table plus product debug events. Spec: hot-path.md contract 3 extended + new SOCKS5 Path Contracts section. 386/386 tests, zero warnings.

### Git Commits

| Hash | Message |
|------|---------|
| `e5c9bf2` | (see git log) |

### Status

[OK] **Completed**


## Session 12: Proxy stability and performance hardening (S1-S6, P1-P2)

**Date**: 2026-08-30
**Task**: Proxy stability and performance hardening (S1-S6, P1-P2)
**Branch**: `master`

### Summary

Fixed all audit findings from the 2026-08-29 proxy audit: UDP relay sockets disable SIO_UDP_CONNRESET with ConnectionReset-as-skip + observability counters/warn fixes (S2/S6); relay completions observed on all paths incl. the read-Exception-before-log-gate fix (S3); capacity-rejected SYNs get in-window RST|ACK with 1s/tuple cooldown (S4); fragments on associated flows consumed+RST instead of policy-leaking pass (S1, address-pair index); adapter local addresses cached behind change-event+TTL snapshot, allocation-free read (S5); relay stall-window CTS reuse (per-op 160B->0B, -77% alloc) + RFC1624 incremental endpoint checksums (18.7x) + vectorized Internet checksum with fold invariants (16x) (P1/P2). Suite 386->431 tests, zero-warning build, quick soak zero loss, artifacts under benchmarks/results/2026-08-29-proxy-hardening/. Specs updated: error-handling, tcp-local-redirect, udp-relay, hot-path.

### Git Commits

| Hash | Message |
|------|---------|
| `11acc06` | (see git log) |
| `d0bcd83` | (see git log) |
| `2f544ab` | (see git log) |
| `1e94849` | (see git log) |
| `adfb4ec` | (see git log) |
| `a0d87da` | (see git log) |
| `323f747` | (see git log) |

### Status

[OK] **Completed**


## Session 13: Preserve 2026-08-30 proxy perf/stability deep-dive research

**Date**: 2026-08-30
**Task**: Preserve 2026-08-30 proxy perf/stability deep-dive research
**Branch**: `master`

### Summary

Four read-only review agents (TCP redirect/relay, UDP relay, SOCKS5 control + capture/dispatch, measurement gaps) analyzed the SOCKS5 proxy forwarding paths at baseline e5667af; main agent spot-verified the three highest-impact claims (dead production hot dispatch entry, missing client RST on mid-flow relay end, missing NoDelay). Five research docs persisted with file:line evidence and a ranked improvement backlog (top items: hot-path revival via TCP-gated reverse diversion, client-visible RST on relay fault/stall, NoDelay + 64KiB pooled pump buffers, atomic retire+remove+tombstone, UDP allocation zero-out + jumbo sizing). Task archived; next step agreed with user: parent task + child tasks for fast-hardening and hot-path-revival.

### Git Commits

(No commits - planning session)

### Status

[OK] **Completed**


## Session 14: fast-hardening child landed: client RST + NoDelay + pooled buffers + throttle

**Date**: 2026-08-30
**Task**: fast-hardening child landed: client RST + NoDelay + pooled buffers + throttle
**Branch**: `master`

### Summary

First implementation child of 08-30-proxy-perf-stability landed all four research items: R1 client-visible RST on relay fault/stall (RelayEndKind surface, inject-before-teardown, _endKind defaults Faulted after review catch), X4 NoDelay on both relay legs, X5 64KiB ArrayPool pump buffers (chunk-8192 alloc 188->171KB), X8a 1s stall re-arm throttle. Two implement-agent runs + one check-agent pass (PASS-WITH-FIXES). 442/442 tests, zero warnings, tcp stability otherErrors=0, socks5/bare 92.0%. Specs updated (hot-path.md, tcp-local-redirect.md). Task archived; next: 08-30-hot-path-revival (PRD ready, needs design.md+implement.md before start).

### Git Commits

| Hash | Message |
|------|---------|
| `b0e0c6d` | (see git log) |
| `a06a587` | (see git log) |

### Status

[OK] **Completed**


## Session 15: hot-path-revival child landed: warm entry revived in production via WantsPacket prefilter

**Date**: 2026-08-30
**Task**: hot-path-revival child landed: warm entry revived in production via WantsPacket prefilter
**Branch**: `master`

### Summary

Second implementation child of 08-30-proxy-perf-stability landed backlog #1 (X1): replaced the bare Func reverse handler with ITcpReverseHandler exposing WantsPacket (protocol gate + TcpRedirectTable int[65536] port reference counts, Inc-before-inject in TryClaim, guarded Dec in TryRemove/RemoveExpired); FlowDispatcher warm entry diverts only true reverse candidates. Slow path keeps the full handler so prefilter misses degrade to old behavior (fall-through theorem, pinned by tombstone-straggler test). Per user direction, benchmarks gained production-composition variants with real-predicate fakes; pre-fix evidence 352B==diverted control (warm entry dead in production), post-fix 160B==warm baseline (-55% alloc, ~2x ns). 451/451 tests, zero warnings, stability clean. Specs updated (hot-path.md contract 3, tcp-local-redirect.md reverse hook). Task archived; parent remains open with backlog items 4-10 (atomic-retire, udp-alloc-jumbo, zero-copy, batched-ioctls, hardening-bundle, windows-reality, driver-resilience).

### Git Commits

| Hash | Message |
|------|---------|
| `795cc1f` | (see git log) |
| `679428a` | (see git log) |

### Status

[OK] **Completed**
