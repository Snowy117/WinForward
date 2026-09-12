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


## Session 16: atomic-retire child landed: atomic teardown + bounded setup memory

**Date**: 2026-08-30
**Task**: atomic-retire child landed: atomic retire+remove+tombstone + bounded queues (backlog #4)
**Branch**: `master`

### Summary

Third implementation child of 08-30-proxy-perf-stability landed R2/R3/R4. Research agent re-validated all findings at HEAD 0596a74 (all CONFIRMED; key refinement: table-removal+tombstone were already atomic at baseline, the genuine gap was only retire→table-removal). User decision: R4 = 8 MiB global byte budget + 5 s per-entry TTL (8 MiB = 256 full queues / ~3600 DNS flows / 32× setup-concurrency headroom; precision insensitive because TTL bounds hold-duration). Implementation: D1 RetireSessionUnderGate performs session removal+Closing+retire+alias removal+tombstone in one store-gate critical section (lock order store→table→tombstone, repo-verified acyclic; disposal trails outside); necessary deviation: HandleSynAsync gained a tombstone check after resolve-miss (closes a pre-existing SYN-grace gap, required by AC1). D2 tombstone queue head-drains on RemoveExpired (order-safe: refresh appends fresh tail, queue order ≈ expiry order). D3 _setupTombstones bounded by session capacity, oldest-deadline eviction. D4 BoundedSetupQueue timestamped entries (additive overloads), charge/credit exactly-once across all dequeue sinks (incl. new dispose-drain credit and per-flow-rejection rollback). One implement run + check run (PASS-WITH-FIXES: credit-zero assertion added to drop-oldest test). 459/459 tests (451+8), zero warnings, dispatcher 160 B gate exact, UDP Noop +16 B/slot (entry timestamp, within design budget). Specs updated: tcp-local-redirect.md (atomic retire contract, queue drain, SYN grace check), udp-relay.md (bounded-setup-memory scenario), error-handling.md. Task archived; parent backlog remaining: udp-alloc-jumbo (#5), zero-copy (#6), batched-ioctls (#7), hardening-bundle (#8), windows-reality (#9), driver-resilience (#10). Checker noted pre-existing residual: FailAssociationAsync looks up session by key not instance (narrow, grace-protected).

### Git Commits

| Hash | Message |
|------|---------|
| `f0ac4ec` | feat(tcp,udp): atomic retire+remove+tombstone and bounded setup memory (R2/R3/R4) |
| (auto) | chore(task): archive 08-30-atomic-retire |

### Status

[OK] **Completed**

## 2026-08-30 — 08-30-udp-alloc-jumbo (UDP allocation zero-out + jumbo buffer sizing)

### Summary

Fourth implementation child of 08-30-proxy-perf-stability landed backlog #5 (X6 + R5). Planning re-validated all findings at HEAD c91ab68 (all CONFIRMED; refinements: _sendBuffer now references the UdpFrameBuilder constant but still ignores the factory cap; the oversized-response drop policy already follows the cap consistently, only documentation was missing; capture-side payloads are bounded at cap−42 so 6+16+cap always suffices). Implementation (D1-D5): IUdpProxyTransport.SendAsync takes Endpoint (3 fwd allocs/datagram gone); Socks5UdpDatagram.DestinationAddress is IPAddressValue? with scope preserved into .ScopeId (2 rev allocs/response gone); per-transport cached receive sender template; _sendBuffer = 6+16+factory-fed cap with Program.cs hoisting one NdisApiAbi.MaximumEthernetFrame for send/receive/reinjector (single source of truth, jumbo-ABI safe); oversized boundary documented (1472B @ 1514 ABI). One implement run + check run (PASS, 0 fixes). 463/463 tests (459+4), zero warnings; UdpSession benchmarks: Noop probe ≈3.9 KB/session (within ≤~4 KB anchor), populate paths no regression. Documented deviations: factory cap param required (3 non-prod sites pass default explicitly); new cold-edge Encode(IPAddressValue) overload for the loopback-server echo; one compiler-found migration beyond inventory (UdpProxyCoordinatorTests tuple type). Coordinator diff = 0 bytes (OCE filter/teardown/charge-credit physically untouched). Specs updated: udp-relay.md (endpoint zero-allocation + frame-cap buffer sizing section, migration note re Assert.Equal inference), hot-path.md (contract #1 extends raw addresses to SOCKS5 datagram decode). Parent backlog remaining: zero-copy (#6), batched-ioctls (#7), hardening-bundle (#8), windows-reality (#9), driver-resilience (#10).

## Session 9: 08-30-windows-reality — Windows VM 测量程序（backlog #9）

**Date**: 2026-08-30
**Task**: 08-30-windows-reality (child of 08-30-proxy-perf-stability)
**Branch**: `master`

### Summary

在 Win11 IoT LTSC 虚机（32C/4GB，winrm/evil-winrm-py + tmux PTY 驱动）完成 backlog #9：R1 stability 矩阵（UDP 丢失 2.475%→0、overflow -43%、footprint -54% 确认 #5 收益；WSAEADDRINUSE 8.2%→13.9%/96.65% throughput）；R2 BDN 双侧 in-process（托管路径平台等价、分配门 byte 级一致；TcpRelay 12.4×→2.9× 揭示 per-IO 成本是结构差距）；R3 一小时 soak（928k 换联/219GB，WS +0.9% 句柄 -4.0%，无泄漏 PASS）；R4 归因实验（仅扩端口池 96.65%→0.40%，纯 OS 容量问题，产品无罪）。父 backlog 更新：#7 升权、#6 降权，新候选 port-budget-windows / local-mux-transport(VLESS+mux, sing-box 无 UDS) / windows-real-nic。方法论沉淀到 benchmarks/README（无 SDK guest 需 --inProcess、单 --filter 多值、须仓库根启动、高性能电源）。trellis-check 全 AC PASS、数字抽查全吻合、构建 0w0e。

### Git Commits

| Hash | Message |
|------|---------|
| `eecbb27` | bench(benchmarks): Windows VM measurement program — stability, BDN, 1h soak, port attribution |
| (auto) | chore(task): archive 08-30-windows-reality |

### Status

[OK] **Completed**

## Session 10: 08-30-batched-ioctls — 批量重注入 IOCTL（backlog #7 Phase 1）

**Date**: 2026-08-30
**Task**: 08-30-batched-ioctls (child of 08-30-proxy-perf-stability)
**Branch**: `master`

### Summary

落地批量重注入：executor Pass 处置按 (adapter, direction) lane 累积，pump 批尾/退出时统一 flush——一次迭代同方向 Pass 从 N 次内核穿越降到 1 次（E2E 实测 96 帧→6 调用=16×）。S2 导出验证推翻了部分成功语义假设（send IOCTL lpOutBuffer=NULL + METHOD_BUFFERED ⇒ PacketsSuccess 不可观测），D1 采用 fail-the-batch 分支。driver 批量 send ≤126/chunk 单 gate lease；gate map 换 ConcurrentDictionary 免锁；遥测 BatchedSendFlushCount/PacketCount。476/476 测试（+13），分配门字节级不变（Dispatcher 160/352B、CapturePump 1.86MB）。check 确证 UdpProxyCoordinatorLifecycleTests 一个用例 pre-existing flaky（干净基线 4/6 失败）。spec windows-ndisapi.md 新增批量 send 契约节。Phase 2（UDP response 微批）按 PRD 条件触发待 DNS 密集测量。

### Git Commits

| Hash | Message |
|------|---------|
| `f9fdb67` | perf(ndisapi,capture): batch Pass reinjection IOCTLs (backlog #7 / X3) |
| (auto) | chore(task): archive 08-30-batched-ioctls |

### Status

[OK] **Completed**


## Session 16: Driver resilience: transient-read retry + single-pump degradation, off-pump TCP SYN setup (backlog #10)

**Date**: 2026-08-30
**Task**: Driver resilience: transient-read retry + single-pump degradation, off-pump TCP SYN setup (backlog #10)
**Branch**: `master`

### Summary

Landed backlog #10 (R7+R8) as child 08-30-driver-resilience. R7: NdisNativeCallStatus.IsTransientReadError (21/170/1237/995/1167/31; ndisrd closed-source, classification evidence-anchored via Npcap nmap#2036, unknown codes permanent-conservative); NdisCapturePump bounded backoff retry (5x100ms doubling, ~3.1s worst) then degraded exit (onDegraded once, finally flush+release); MultiAdapterCaptureLoop no-sibling-cancel + onAdapterDegraded; TransactionalCaptureRuntime.MarkAdapterDegradedAsync restores only the degraded adapter; Program structured events adapter.degraded/adapter.retry carrying full native error for future table refinement. R8: TcpRedirectOutcome.SetupPending; TcpPendingSynSetupIndex (1024 cap, 1MiB exactly-once budget, 5s TTL, 1s failure cooldown); HandleSynAsync fast path fully synchronous (reuse/tombstone/cooldown/capacity untouched, capacity now counts pending), listener bind+claim+rewrite-inject in Task.Run background wrapped by EnterSetup/ExitSetup; retransmitted SYN overwrites pending (single listener); executor consumes SetupPending silently. Tests 476 to 496 (3 green runs), zero-warning build; trellis-check passed all 12 focus areas, fixed 2 issues (structured events, fakes format). Deviations accepted: pending-cap reject returns Blocked+trace; barrier exactly-once test replaced by pending-absorption tests (loser branch kept as defense-in-depth). S8 VM spot-check skipped (no VM available). Spec updated: windows-ndisapi.md, error-handling.md, tcp-local-redirect.md. Parent child-map synced (#7, #10).

### Git Commits

| Hash | Message |
|------|---------|
| `461df0d` | (see git log) |

### Status

[OK] **Completed**


## Session 17: Tolerate TFO SYN-with-payload in TCP redirect

**Date**: 2026-09-06
**Task**: Tolerate TFO SYN-with-payload in TCP redirect
**Branch**: `master`

### Summary

TFO data-bearing SYNs were fail-closed as Blocked and silently consumed, blackholing TFO clients (ETIMEDOUT). Now they ride the bare-SYN pipeline: forward-leg rewrite is RFC 1624 incremental over addresses/ports only, sequence tracking already counts SYN data, and the non-TFO listener relies on RFC 7413 graceful degradation. TcpSynKind/ClassifyTcpSyn collapsed into boolean predicate IsTcpSyn (user review); executor logging test retriggered via genuine sync reuse-path rewrite failure; spec contract recorded in tcp-local-redirect.md. 501/501 tests green, build 0 warnings.

### Git Commits

| Hash | Message |
|------|---------|
| `d06f002` | (see git log) |

### Status

[OK] **Completed**


## Session 18: UDP burst-establishment benchmark + baseline matrix

**Date**: 2026-09-06
**Task**: UDP burst-establishment benchmark + baseline matrix
**Branch**: `master`

### Summary

Built the udp.burstEstablishment stability scenario (burst N new UDP flows at one instant, background flows pacing through control/burst/post windows, --dial-delay-ms knob modeling remote dial). 19-point baseline matrix + TTL corner probe: establishment latency = ceil(N/8)xD wave serialization (fit +2.9-4.3%), zero head-of-line impact on established flows, first-datagram TTL loss begins at N > 8 x floor(5s/D) (93.75% at 128x4s). Spec contract added to udp-relay.md; follow-up children created: 09-06-udp-burst-ttl-attribution (small fix) + 09-06-local-mux-transport (research).

### Git Commits

| Hash | Message |
|------|---------|
| `4a4d6a6` | (see git log) |
| `4b00d0b` | (see git log) |

### Status

[OK] **Completed**


## Session 19: UDP burst TTL re-attribution fix

**Date**: 2026-09-06
**Task**: UDP burst TTL re-attribution fix
**Branch**: `master`

### Summary

Landed the burst follow-up fix: BoundedSetupQueue.RefreshEnqueuedStamps + UdpProxyCoordinator.RefreshSetupStampsAtDialStart re-stamp a slot's queued datagrams when its setup leaves the 8-wide limiter (dial start), so the 5s setup TTL measures dial age instead of enqueue age. Acceptance matrix: 128x4s probe 8/128 -> 128/128 first responses, loss 93.75% -> 0, timeToLast 64037ms matching the 16-wave model; spot points within noise; background windows zero loss. Mutation-verified unit coverage (LimiterQueueWaitDoesNotExpireTheTriggeringDatagram). Spec contract in udp-relay.md updated to post-fix semantics with matrix re-run gate.

### Git Commits

| Hash | Message |
|------|---------|
| `76abc37` | (see git log) |
| `dd3985b` | (see git log) |

### Status

[OK] **Completed**


## Session 20: Fix adapter.degraded nativeError=87 storm via in-process layered refresh

**Date**: 2026-09-08
**Task**: Fix adapter.degraded nativeError=87 storm via in-process layered refresh
**Branch**: `master`

### Summary

Diagnosed production adapter.degraded nativeError=87: ndisrd rebuilds its bound-adapter list on NIC changes, invalidating all enumeration handles. Implemented layered capture refresh (task 09-07-adapter-list-refresh): SetAdapterListChangeEvent watcher + durable/generation split (coordinators survive, pumps/modes/UDP targets rebuild), non-fatal scope re-resolution, 87-as-refresh-signal. 555/555 tests, trellis-check 5-dim pass, hardware smoke on Win11 dev host via WinRM (87 -> refresh in 77ms, cage-crossing connections survived, R-2 auto-reset verified). Spec contract added to windows-ndisapi.md.

### Git Commits

| Hash | Message |
|------|---------|
| `398376e` | (see git log) |

### Status

[OK] **Completed**

## 2026-09-08 — design review -> full remediation (qmazon)

Full-codebase deep-module design review (5 explore agents) -> parent task 09-08-design-review-remediation with 3 children, all archived same day:
- P0 correctness (branch p0-correctness, merged): lane retirement RetireLanesExcept + OnScopeInstalled wiring + overflow observability; IPv4Any classify fix; iphlpapi ReadTable bounds; pump DisposeAsync awaits run exit; lease-guard paramName via CapturedFlowPacketGuards. 555->569 tests.
- P2 hygiene (branch p2-hygiene, merged): FlowTable dead surface, IWindowsAdapterInventory, Platform.cs split, TestHelpers convergence (CaptureLifecycleFakes/ScriptedReader promoted), indexed [i] config diagnostics, AdapterTransientRetryLogGate + DurableCaptureBundle tests. 569->578. Deferred: logLevel JsonElement->string? (custom converter needed).
- P1 structure (branch p1-structure, merged): FlowTable/BoundedSetupQueue re-homed; NdisCapturePumpOptions; UdpProxy->Socks5 edge sanctioned (codec already in Protocols); UdpProxyCoordinator 471->329 via 4-part split (tombstone->cooldown rename, check found+fixed one PruneExpired leaf-lock gap); benchmarks StabilityShared + burst instrumentation, BenchmarkShared to root. 578 throughout.
Final: 578/578, zero warnings, master ~20 commits ahead of origin (not pushed). Gateway note: upstream "No active API keys" errors hit twice mid-session; resuming the same subagent conversation worked both times.


## Session 21: Recover generation startup from stale adapter handles (native 87)

**Date**: 2026-09-12
**Task**: Recover generation startup from stale adapter handles (native 87)
**Branch**: `master`

### Summary

Closed the last unhandled 87 surface: a bound-adapter-list rebuild racing the generation install phase faulted the mode snapshot before pumps started and exited the process. TransactionalCaptureRuntime gained a race-free ReachedPumpRun latch (set under the gate immediately before the capture run) exposed via ICaptureGeneration; LayeredCaptureRunner classifies Win32Exception(87) && !ReachedPumpRun as recoverable, absorbs it at both generation-await points (exit observation releases the dead generation like a refresh stop + signals a demand; StopGenerationAsync absorbs with no extra signal), and rebuilds through the forced storm-guarded refresh - empty diff still installs a replacement (forced=true, never noop=true), streak capped at MaxConsecutiveStartupRecoveries=3 with fail-closed rethrow of the original fault beyond it, reset on any pump-run completion. 10 new tests (3 lifecycle latch + 7 refresh AC1-AC6); trellis-check 7-dim PASS, 588/588, zero warnings. Spec contracts updated in windows-ndisapi.md (signatures/contracts/matrix/tests) + error-handling.md (new bounded-recovery exemption). Deleted stray duplicate RefreshDemandGate.cs; noted 10 pre-existing format violations in untouched files for a future cleanup.

### Git Commits

| Hash | Message |
|------|---------|
| `975aede` | (see git log) |

### Status

[OK] **Completed**


## Session 22: Scope-sized pass-lane table

**Date**: 2026-09-12
**Task**: Scope-sized pass-lane table
**Branch**: `master`

### Summary

Diagnosed the frequent 'pass batching degraded' warn: the fixed 8-slot lane table (4 NICs x 2 directions) overflowed on multi-NIC hosts whose capture scope widens to every MSTCP-bound adapter. Implemented scope-sized rebuild: RetireLanesExcept now rebuilds the lane table at 2 x scope-count under the lane-creation lock, migrating in-scope lanes with pending frames (runner starts new-generation pumps before the scope-installed callback, so dropping would strand frames); overflow stays a defensive pre-install/paused backstop. Tests updated + new rebuild/migration test; spec windows-ndisapi.md lane contracts refreshed; 589 tests green, 0 warnings.

### Git Commits

| Hash | Message |
|------|---------|
| `f1d1c1b` | (see git log) |

### Status

[OK] **Completed**
