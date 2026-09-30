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

## 2026-09-18 — 09-17-adapter-staleness-logging（事故诊断 → 自愈 + 日志改进，已归档）

- 起因：2026-09-17 20:02–20:08 断网事故。诊断结论：18:04 的 degraded/refresh 管线按设计工作（无关）；真实根因是主机链路状态在 NDISRD 绑定列表之外变化（嫌疑 IPv6 临时地址轮换），适配器视图从最后一次 refresh 起永久 stale——sing-box 到节点的 v6 出站 SYN 黑洞，只有重启恢复。交叉证据：smoke/sing-box 日志显示其 loopback inbound 正常、WinForward 握手正常、唯独节点 dial i/o timeout 与 WinForward relay setup failed 逐条对齐。
- 交付：双通道自愈（30s 周期重枚举 + 地址指纹 diff；失败率阈值 forced refresh，三层防风暴）+ 全面诊断日志（6 个静默失败点补 warn、心跳、降噪、README 事件表）。测试 589→649。
- E1 抓到关键 bug：MIB_UNICASTIPADDRESS_ROW 行起始偏移是 8（NET_LUID 对齐）而非 4——原实现会把根因修复在真机静默失效。已修 + 毒值测试锁定。教训入 spec：iphlpapi 表行偏移由行内最大对齐类决定，新表必须推导并用 poisoned-padding 测试钉住。
- 下次 Windows 硬件运行时验证一次地址指纹非空。
- 提交：a6ca464（测试稳定性）/ 6d95301（主实现）/ 325ba53（spec）/ a1d9f31（任务档案）+ 归档自动提交。


## Session 23: GC-less zero-allocation hot paths (M0-M5)

**Date**: 2026-09-19
**Task**: GC-less zero-allocation hot paths (M0-M5)
**Branch**: `master`

### Summary

Completed M0-M5 of the GC-less zero-allocation task: native buffer pool family (syn/udp/relay/window) with MemoryManager bridge, pooled FlowState/setup executor, NdisCapturePump dedicated-thread sync loop, workstation GC + 128 MiB HeapHardLimit fuse, and the gc-soak stability scenario. M3 review fixed a SYN-lease UAF, a missing TCP DNS cache, and a setup shutdown race; M4 fixed a test-only native leak. M5 review found a vacuous soak assertion and a real 72 B/datagram SocketAddress allocation in Socks5UdpTransport. The first 30-min soak failed on an over-strict pool Outstanding-equality gate (a relay return, not a leak); corrected to an overflow-growth window gate plus a bounded post-teardown drain check. Final 30-min gc-soak passed (exit 0): flat working set, 272 B per-thread over 45.06M sends, pools balanced. 724/724 tests, 0 warnings.

### Git Commits

| Hash | Message |
|------|---------|
| `c75f30e` | (see git log) |
| `97f0e01` | (see git log) |
| `c2ac421` | (see git log) |
| `fef03fc` | (see git log) |
| `cf1a570` | (see git log) |
| `b51965b` | (see git log) |
| `a0e8024` | (see git log) |
| `8c6cfe0` | (see git log) |

### Status

[OK] **Completed**


## Session 24: Compat API cleanup + design-health review

**Date**: 2026-09-19
**Task**: Compat API cleanup + design-health review
**Branch**: `master`

### Summary

Audited and removed compatibility APIs that existed only to avoid test rewrites: dead members (NdisApiDriver telemetry, SynCopyPool, NdisApiAbi upstream constants, orphan resolvers), test/benchmark-only convenience wrappers (rewritten to the production span seams), test-only optional parameters (moved to internal overloads; framePool/retryDelay/flowGeneration defaults removed), and benchmark-only diagnostics (narrowed to internal with Core InternalsVisibleTo). Made IUdpProxyTransport.SendSpanAsync the only UDP send seam by deleting the unused memory chain (~98 call sites rewritten; no tests deleted). A design-health deep-module review was produced and its larger refactors routed to follow-up task 09-19-design-deepening-refactors. A check pass caught that the AdapterSelectorTests deletion had taken 10 live WindowsAdapterInventory facts with it; they were restored, keeping the net test decrease at the permitted 3 (deleted-unit tests only). Final: 721/721 tests, 0 warnings, grep gates clean, gc-soak smoke green. Commits 2ea413d, ee6fdd8, a170719.

### Git Commits

| Hash | Message |
|------|---------|
| `2ea413d` | (see git log) |

### Status

[OK] **Completed**


## Session 25: Design-deepening refactors (R1-R12) + TCP clock seam follow-up
<!-- trellis-session: v=2 fp=4b12f89e96096b6b -->

**Date**: 2026-09-19
**Task**: Design-deepening refactors (R1-R12) + TCP clock seam follow-up
**Branch**: `master`

### Summary

Executed 09-19-design-deepening-refactors end-to-end via dispatched trellis-implement sub-agents: six behavior-neutral milestones (RentItem(handler) factory + dead SetupWorkKind removal; FlowTable/UdpSetupQueueBudget clock injection + shared FlowHash; UdpProxyOptions/IUdpSessionSlotHost seam/UdpProxySessionContext/UdpProxyDiagnostics; TcpRedirectOptions/TcpRedirectDiagnostics with 66 mechanical test-site rewrites; NdisPumpDiagnostics + internal test-seam options members with NdisApi Benchmarks IVT; DurableCaptureBundle composers 350->287 effective lines), then closed the review follow-ups in 09-19-tcp-clock-followup: TCP-family clock seam closed over 15 raw UtcNow reads (TcpRedirectOptions.TimeProvider threaded through coordinator/store/setup/reset + IdleExpirySweeper) proven by a load-bearing HoldsFlow grace-boundary regression; TcpRedirectSession moved to its own file; UdpProxySession receive-failure handler threaded as a loop parameter (no more null-forgiving); SetupWorkItem payload planes partitioned into pre-allocated TcpSetupWork/UdpSetupWork; UdpSetupQueueTests split 512->272/146/113 with byte-identical facts and the shared CreateFlow helper consolidated from five copies into TestHelpers/FlowBuilders. Gates at every milestone: zero-warning Release build, full suite 721 baseline -> 725 (4 additive regressions, zero assertion changes), allocation gates and gc-soak clean (gen2=0, zero pool deltas); spec updated for the options/clock/diagnostics conventions.

### Git Commits

| Hash | Message |
|------|---------|
| `d434d00` | chore(task): plan design-deepening refactors (09-19-design-deepening-refactors) |
| `804c790` | refactor(setup): require handler at rent time, drop dead work kind |
| `7a8df52` | refactor(core): inject clocks into FlowTable and UdpSetupQueueBudget; share FlowHash |
| `ac7b957` | refactor(udp): options record, slot-host seam, session context, diagnostics snapshot |
| `a634ae7` | refactor(tcp): options record and diagnostics snapshot |
| `53ddcb4` | refactor(ndis): pump diagnostics snapshot, internal test-seam options members |
| `cb70b0d` | refactor(cli): extract coordinator composers from durable capture bundle |
| `cfad0d6` | docs(spec): record deep-module conventions from design-deepening refactors |
| `3a9ccfc` | chore(task): close out design-deepening refactors verification |
| `a4322d3` | refactor(tcp): close the clock seam across the TCP family |
| `31191ad` | refactor(runtime): move TcpRedirectSession out, tighten receive handler, split setup payload planes |
| `18d2243` | test(udp): split UdpSetupQueueTests into theme files under the 400-line budget |
| `10e5798` | test(udp): consolidate the four duplicate CreateFlow copies onto FlowBuilders |
| `d9ca244` | docs(spec): extend the clock-seam convention to the TCP family |
| `e6215ba` | chore(task): record 09-19-tcp-clock-followup planning artifacts |

### Status

[OK] **Completed**


## Session 26: Analyzer diagnostics cleanup: 1369 → 0 (dotnet format --severity info)
<!-- trellis-session: v=2 fp=8703ef5cfb7a0402 -->

**Date**: 2026-09-20
**Task**: Analyzer diagnostics cleanup: 1369 → 0 (dotnet format --severity info)
**Branch**: `master`

### Summary

Executed 09-19-src-analyzer-cleanup via dispatched trellis-implement/check sub-agents: cleared all 1369 info-level dotnet format diagnostics (src 426 / tests 762 / benchmarks 181) to exit 0. Whitespace pass; 36 mechanical rules (820 diagnostics: MA0003 named arguments, collection expressions, trailing commas, u8 literals, ...); per-site judgment fixes (MA0076 → 79 string.Create(InvariantCulture) wraps, CA1512 ThrowIf*, IDE0290 primary ctors, MA0042/RCS1261 async disposal, CA1859 concrete types, CA1068 token reorders, IDE0059, ...); 105 identifier renames under the user-decided naming convention (internal≡private via s_/_ prefixes, ThreadStatic t_ marked by a localized pragma because naming rules cannot match attributes, public/protected fields incl. perf-motivated public fields stay PascalCase, local consts camelCase). An independent audit agent empirically re-enabled every suppression (37 editorconfig entries → 17 fired; 38 pragma sites → all load-bearing), then deleted 8 stale entries, rewrote 9 comment blocks inherited from another project with repo-accurate facts, and rescoped IDE0130/MA0041 to their evidence paths per the user's glob rule. Final check agent verified semantic equivalence across all batches (single finding: CA1419 rationale rewrite). Gates standardized on Release: build 0 warnings, 725/725 tests (baseline corrected 724→725; Debug deterministically fails the two zero-allocation gates), verify exit 0. AGENTS.md pre-commit gate + GitHub Actions 'Analyzer Gate' workflow (PR/push master) added; naming/suppression conventions written into spec quality-guidelines.md.

### Git Commits

| Hash | Message |
|------|---------|
| `fe21bd3` | chore(analysis): rework naming rules and suppression scope |
| `a6c15e0` | chore(quality): zero dotnet format --severity info diagnostics (1369 -> 0) |
| `b8484a1` | docs(spec): record field-naming convention and suppression-scope policy |
| `1fafb33` | docs(agents): require the analyzer-diagnostic gate before commits |
| `aa7261d` | ci: gate master pushes and PRs on the analyzer diagnostics |
| `d185cf1` | chore(task): record 09-19-src-analyzer-cleanup artifacts |

### Status

[OK] **Completed**


## Session 27: Adopt the JetBrains inspectcode gate and zero its report
<!-- trellis-session: v=2 fp=5f01db11835fdf8d -->

**Date**: 2026-09-20
**Task**: Adopt the JetBrains inspectcode gate and zero its report
**Branch**: `master`

### Summary

Introduced the jb inspectcode gate (pin 2026.1.3) and drove the baseline report 1317 findings -> 0: mechanical fixes, dead-code removal, closure/disposal correctness, nullable family, control-flow style, and primary-ctor parameterization. Audited every suppression in an isolated worktree (rule-level keys 16 -> 14 NECESSARY + 2 POLICY; pragmas 128 -> 126 NECESSARY + 2 STALE deleted); noted that jb verdicts can be comment-sensitive, so neutralize directives by deleting the whole line. Closed MA0038 at rule level and removed its now-redundant site pragmas. Added the AGENTS.md pre-commit gate, renamed format-gate.yml to analyzer-gate.yml with a jb job, and recorded the method in quality-guidelines.md. Gates green: Release build 0-warning, tests 725/725, dotnet format exit 0, jb 0 Issue.

### Git Commits

| Hash | Message |
|------|---------|
| `7b199df` | chore(analysis): clear jb inspectcode findings in src |
| `25db2f5` | test(analysis): clear jb inspectcode findings in tests |
| `1d612ea` | chore(analysis): clear jb inspectcode findings in benchmarks |
| `baec0a2` | chore(analysis): rework rule-level suppressions for the jb gate |
| `b19a779` | docs(agents): require the jb inspectcode gate before commits |
| `40df6e5` | docs(spec): record the jb gate and suppression-audit method |
| `fb083cb` | ci: extend the analyzer gate to jb inspectcode |

### Status

[OK] **Completed**


## Session 28: Transport lifecycle hardening: ownership, quiescence, session state
<!-- trellis-session: v=2 fp=ba1fdaa64c4120af -->

**Date**: 2026-09-20
**Task**: Transport lifecycle hardening: ownership, quiescence, session state
**Branch**: `feat/transport-lifecycle`

### Summary

Planned and implemented 09-20-transport-lifecycle across Phases A-E. Ownership consolidated into DurableCaptureBundle (coordinators take pools + shared SetupExecutor as required ctor deps, never dispose them); TCP quiescence made explicit (relay DisposeAsync awaits Completion, acceptor owns terminal observation + session lifetime, store defers lifetime CTS disposal, attach failure tears the session down, RegisterSession releases on partial failure, setup cooldown written inside the inflight section); UDP got a per-session lifetime CTS, UdpSessionState/UdpTeardownReason enums, and SendSpanAsync returning bool with a counted rate-limited fail-closed drop instead of throwing. Gates: format clean, Release build 0 warnings, 744/744 tests, jb inspectcode 0 issues. Specs updated: tcp-local-redirect, udp-relay, error-handling, quality-guidelines. Deferred follow-up (not tasked): structured concurrency (TaskScope) + lifetime analyzers.

### Git Commits

| Hash | Message |
|------|---------|
| `4a2007e` | feat(runtime): explicit transport quiescence boundary and session state |

### Status

[OK] **Completed**


## Session 29: Quiescence scope primitive: measured gate choice and a fragile allocation gate found (C1)
<!-- trellis-session: v=2 fp=5703a70be5b7df0a -->

**Date**: 2026-09-21
**Task**: Quiescence scope primitive: measured gate choice and a fragile allocation gate found (C1)
**Branch**: `feat/transport-lifecycle`

### Summary

Planned and delivered C1 (09-20-quiescence-scope) of the structured-concurrency program, after grilling the design tree in rounds and creating the parent 09-20-structured-concurrency plus four children (quiescence-scope, lifetime-analyzers, lifecycle-migration-cluster, lifecycle-migration-rest). C1 ships QuiescenceScope + WorkLease: a packed-word CAS gate (bit 0 = sealed, rest = pending) chosen by measurement over the lock variant (18.60 ns vs 49.50 ns per enter/exit pair, both 0 B/op, outside the dev-box noise band); Enter/Exit allocate 0 bytes and the drain cell is created once at seal, never on a 0->1 transition. An independent trellis-check pass found 7 issues; the fix pass made DrainAsync identity-stable single-flight (the sealing caller previously got a different Task than later callers) with the drain cell allocated exactly once, added fault call-site attribution (RecordFault(exception, site) + FaultSite, fed by Run's name), corrected the spec's benchmark numbers, and asserted the D3/P6 no-sibling-cancellation property. While verifying, found that HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes is not a flake but a thread-migration measurement artifact: its window spans 64 awaits while reading GC.GetAllocatedBytesForCurrentThread(), so it fails 4/4 in isolation (600 B observed) and is green only inside the full suite -- and in the dangerous direction a migrated continuation can mask a small real regression. The gate file is unmodified (pre-existing), so the contract 'an allocation gate must evaluate on one thread' was recorded in hot-path.md and the hardening was scheduled as a hard prerequisite of C3, which changes the span-send path it guards. Gates: format exit 0 empty, Release build 0 warnings, 763/763 tests, jb inspectcode 0 issues. Specs: new async-lifetime.md (glossary, I1/I2, primitive contract, Run admission rules, WF-rule stub) plus its index row; hot-path.md gate contract.

### Git Commits

| Hash | Message |
|------|---------|
| `a167a24` | feat(runtime): counted quiescence scope primitive |

### Status

[OK] **Completed**


## Session 30: Lifetime enforcement analyzers: four WF rules wired into src/** behind a proven allowlist (C2)
<!-- trellis-session: v=2 fp=21ccd5410e3e41d3 -->

**Date**: 2026-09-21
**Task**: Lifetime enforcement analyzers: four WF rules wired into src/** behind a proven allowlist (C2)
**Branch**: `feat/transport-lifecycle`

### Summary

Delivered C2 of the structured-concurrency program: analyzers/WinForward.Analyzers (netstandard2.0, Roslyn 4.14.0 pinned against SDK 10.0.401) with four build-breaking rules - WF0001 (discard of an unawaited awaitable), WF0002 (Task/Task<T>.ContinueWith), WF0003 (Task.Run / Task.Factory.StartNew), WF0004 (bare unawaited awaitable expression statement). WF0004 was restored after measuring that CS4014 fires only inside async methods: 'void M() { FooAsync(); }' is silent, so the synchronous-method hole needed its own rule. Rules key on awaitable types (well-known task types by OriginalDefinition plus a structural GetAwaiter/IsCompleted/OnCompleted/GetResult check) so custom awaitables are covered. A real design error was found by probing: Task<TResult> declares its own ContinueWith overloads, so matching only Task missed TcpProxyRelay.cs:289 - the very site its allowlist entry exists for; both original definitions are now matched. Wiring is deliberately narrow: a new src/Directory.Build.props re-imports the repo-root props and references the analyzer as OutputItemType=Analyzer, so src/** is governed while tests/** and benchmarks/** are not (pinned by tests). .editorconfig declares the severities as error rather than relying on TreatWarningsAsErrors and carries six temporary per-file exemptions (each with evidence and its C3/C4 remover) plus one permanent exemption for QuiescenceScope's tracked children. src/** product code is byte-identical - the rules landed with the allowlist, not with migration. Independent check: stripping every exemption reproduces exactly the 11 expected diagnostics line-for-line (no over-broad glob hides a site); per-rule adversarial breaks fail the expected tests (1 failure for WF0002, 7 for WF0001/0003/0004); deleting one exemption per rule id turns the build red at that site; a violating line under tests/ and benchmarks/ builds green; the analyzer project is inside the dotnet format gate. Gates: format exit 0 empty, Release build 0 warnings, 781 tests (763 Core + 18 Analyzers, exact), jb inspectcode zero issues. Spec: async-lifetime.md WF table completed plus the allowlist state. Two non-blocking AwaitableClassifier edges (over-fires on a non-System.Action parameterless OnCompleted delegate, under-fires on inherited awaiter members) recorded in the task notes rather than speculatively patched. Also committed the previously-missed bookkeeping that records 09-20-transport-lifecycle under its parent 08-30-proxy-perf-stability. Next: C3 migrates the TCP/UDP cluster and starts deleting these exemptions, with the allocation-gate hardening as its hard prerequisite.

### Git Commits

| Hash | Message |
|------|---------|
| `2bfed0a` | feat(analyzers): build-time rules for the fire-and-forget escape syntaxes |
| `db52a93` | chore(trellis): record 09-20-transport-lifecycle under 08-30-proxy-perf-stability |

### Status

[OK] **Completed**


## Session 31: Quiescence scope migration: TCP/UDP cluster (C3)
<!-- trellis-session: v=2 fp=e091961c35405e6d -->

**Date**: 2026-09-21
**Task**: Quiescence scope migration: TCP/UDP cluster (C3)
**Branch**: `feat/transport-lifecycle`

### Summary

Migrated the TCP/UDP lifecycle cluster to QuiescenceScope: TcpRedirectSessionStore/TcpRedirectSession (root + nested scope, TryEnterSetup), TcpProxyRelay (lease-taking pumps, intrinsic fault observation, observer file deleted, bounded drain), UdpProxySession (single failure cause scope.Fault, synchronous Action signal, lease-based send admission), UdpProxyCoordinator (scope.Run teardown, _inFlightTeardowns removed, defined seal point). Hardened the UDP allocation gate before touching the path it guards and corrected its recorded root cause (not-yet-ready window, not thread migration). Recorded D11 (owner teardown keeps its own one-shot claim; IsSealed-then-DrainAsync is TOCTOU). Gates: format exit 0, Release build 0 warnings, Core.Tests 773 + Analyzers.Tests 18, UDP allocation gate green in isolation, jb inspectcode zero issues. Allowlist shrunk to the two C4 Capture entries plus the primitive exemption.

### Git Commits

| Hash | Message |
|------|---------|
| `a0e2b35` | feat(runtime): migrate the TCP/UDP lifecycle cluster to QuiescenceScope |

### Status

[OK] **Completed**


## Session 32: Complete the structured-concurrency program: C4 migrates the remaining lifecycle owners
<!-- trellis-session: v=2 fp=ef21a9b20ef58275 -->

**Date**: 2026-09-21
**Task**: Complete the structured-concurrency program: C4 migrates the remaining lifecycle owners
**Branch**: `feat/transport-lifecycle`

### Summary

C4 (09-20-lifecycle-migration-rest) migrated the last lifecycle owners to QuiescenceScope and emptied the C2 analyzer allowlist, completing the parent program 09-20-structured-concurrency. Migrated: LayeredCaptureRunner (the run scope owns the CTS; the blocking monitor became a dedicated Thread joined through the scope's lease, so DrainAsync IS the join; the periodic tick a Run child; refresh workers extracted so the file dropped 417 -> 388 effective lines, back under the cap), MultiAdapterCaptureLoop (own scope; the degradation forward a Run child drained by DisposeAsync, pumps disposed first), TransactionalCaptureRuntime (scope-owned CTS drained at the end of the existing single-flight cleanup; the caller-awaited run task deliberately not registered as a scope child), IdleExpirySweeper + RuntimeHeartbeat (scope-owned CTS + a Run loop child; a second DisposeAsync now joins instead of throwing ObjectDisposedException), Socks5ControlConnection (own scope; both RunWithinAttemptAsync overloads admit with a lease so the per-attempt deadline cannot be disposed under a reader; the deadline CTS itself stays, released after the drain), Socks5UdpTransport (no scope: a zero-allocation Interlocked disposal guard, the datagram path unchanged). NdisCapturePump deliberately unchanged - the primitive is Runtime-internal and the dependency direction is Runtime -> NdisApi. 13 new regression tests, each probe-verified non-vacuous; Core 786 + Analyzers 18; format, Release build (0 warnings), tests and jb inspectcode (0 issues) all clean. The parent's integration review passed all six cross-child criteria and fixed two documentation defects: D7 in async-lifetime.md overclaimed (three lifetime handles stay outside the primitive by design - SetupExecutor's worker-joining synchronous IDisposable, NdisCapturePump which owns no CTS, and the CLI's process-root CTS), and two comments still named the deleted EnterSetup/ExitSetup methods. Work commits a3c0783 (C4) and ccb0def (integration-review fixes).

### Git Commits

| Hash | Message |
|------|---------|
| `a3c0783` | feat(runtime): migrate the remaining lifecycle owners to QuiescenceScope |
| `ccb0def` | docs(runtime): reconcile the lifetime contract after the program |

### Status

[OK] **Completed**


## Session 33: Session creation cost: UDP full-stack decomposition, churn measurement, and the re-anchored budget
<!-- trellis-session: v=2 fp=15aab02e3e974ac2 -->

**Date**: 2026-09-22
**Task**: Session creation cost: UDP full-stack decomposition, churn measurement, and the re-anchored budget
**Branch**: `feat/transport-lifecycle`

### Summary

Delivered 09-21-session-creation-cost end-to-end as evidence + decision: staged Noop attribution (100%: capacity 489 / admission 829 / setup <=172 / session tier 2,891 / teardown 1,459-1,968 B/session; C2 = 93% of the tier is async/lifetime machinery), framework dial 83,442 B/session (control connect+handshake 92.8%), churn flat ~91 KB/session (<=0.9% spread, zero establishment loss) via the new udp.churn scenario; hot-path.md §3/§6 re-anchored to three falsifiable tiers (Noop probe <=6,000 raw / <=5,500 product-shaped; setup bookkeeping <=1,500; churn <=95,000 / framework <=84,000), superseding the stale <=1 KB / <=4 KB claims. Ranked follow-ups (not built): control-connection reuse ADR, teardown probe (approx 2 first-chance exceptions/session), session-tier managed slab + CTS TryReset (unsafe rejected: no allocation advantage, loses the lifetime safety net), admission/capacity; .NET 11 trial sanctioned measurement-first; TCP deferred. New benchmarks: SessionSetupDecompositionBenchmarks, FrameworkSetupBenchmarks, UdpChurnScenario. Side fix: the AnalyzerReleases AdditionalFiles duplication that had made every dotnet format run non-empty since C2. Gates: format empty, build 0w, 804 tests, jb 0.

### Git Commits

| Hash | Message |
|------|---------|
| `a174f88` | chore(analyzers): drop the duplicate AnalyzerReleases AdditionalFiles (package targets already add them) |
| `417ad8d` | bench(benchmarks): session-setup decomposition, framework dial probes, and the udp.churn scenario (09-21-session-creation-cost) |
| `c303837` | docs(spec): re-anchor the UDP session budgets from the 2026-09-22 measurements |
| `b7c7e66` | chore(task): record 09-21-session-creation-cost artifacts |

### Status

[OK] **Completed**


## Session 34: Session creation cost redo: out-of-process harness, corrected numbers, re-anchored T3
<!-- trellis-session: v=2 fp=f9416b94a5eb21b0 -->

**Date**: 2026-09-22
**Task**: Session creation cost redo: out-of-process harness, corrected numbers, re-anchored T3
**Branch**: `feat/transport-lifecycle`

### Summary

Redid 09-21-session-creation-cost on a clean instrument. Diagnosis: the recorded 'framework path 83,442 B/session' was ~90% benchmark-harness artifact — the in-process loopback SOCKS5 server allocates a 64 KiB relay-loop buffer + 4 MiB relay socket + control arrays per accepted control connection, all charged to the client by the process-wide GC counter. Fix: added a --serve-socks5-udp child-process server mode + ExternalLoopbackSocks5UdpServer parent helper, switched the churn scenario (--socks5-external) and both BDN benchmark classes (WINFORWARD_BENCH_EXTERNAL_SERVER=1) onto it; in-process default kept byte-identical (A/B reproduced the archived ladder within 1 B). Corrected results (3-run batches): framework path 7,952.0 B/session (dial 3,792.2; ASSOCIATE 959.0; relay socket 576; self-traffic 160; transport ctor 2,464.8), real probe marginal 17,021.2 (echo-fed), churn 13,248.9-14,069.9 wave / 13,720.2-13,868.6 sustained (was 90.8-92.5 KB), harness share ~75.5 KB/session. Bookkeeping ledger (T1/T2) and the stage/teardown findings re-validated byte-identically. Deliverables: three corrected research docs, errata banners on the three archived reports, hot-path.md §3/§6 re-anchored (T3a/b/c + out-of-process rule + falsification sentence) plus a 7-section code-spec for the harness, and a re-ranked reduction list where the control-reuse lever's allocatable share is ~3.8 KB/session (not 77 KB) and bookkeeping now carries 42% of the clean whole cycle. All four quality gates green; trellis-check pass audited the numbers and found 5 doc defects (fixed).

### Git Commits

| Hash | Message |
|------|---------|
| `d4aeda5` | bench(benchmarks): out-of-process loopback SOCKS5 server mode for the real-dial instruments (09-22-session-creation-cost-redo) |
| `6588265` | docs(spec): re-anchor the framework session budget to the out-of-process harness (09-22-session-creation-cost-redo) |
| `dc37516` | docs(tasks): errata the 09-21 framework/churn/reconciliation reports for the harness inflation (09-22-session-creation-cost-redo) |
| `c55e5de` | chore(task): record 09-22-session-creation-cost-redo artifacts |

### Status

[OK] **Completed**


## Session 35: UDP teardown and session-tier allocation reduction
<!-- trellis-session: v=2 fp=bead59247912662f -->

**Date**: 2026-09-22
**Task**: UDP teardown and session-tier allocation reduction
**Branch**: `feat/transport-lifecycle`

### Summary

Split-probed the UDP teardown leg (T) and session tier (C2) with new decomposition cases; attributed the teardown exceptions (2/session = one canceled receive across two await sites, BCL-inherent; S5's fixed 128 = SetupExecutor.Dispose 64 workers x 2) with E1/E2 micro-cases. Implemented the four adopted no-retained-cost reductions: context record struct (240), cached setup delegates (128), merged receive async methods (112), scope drain idle fast path (88) - Noop probe 5,727.0 -> 5,161.8 B/session, churn N=48/D=0 cell 13,248.9 -> 12,560.3 out of process; behavior-zero, 807 tests + build/format/jb gates green; hot-path.md and async-lifetime.md re-anchored. Pooling/slab deferred to a follow-up decision with the split numbers recorded.

### Git Commits

| Hash | Message |
|------|---------|
| `cfd56fd` | perf(runtime): cut per-session UDP teardown and session-tier allocations (09-22-udp-teardown-session-tier-alloc) |
| `27d0be1` | bench(benchmarks): UDP teardown/session-tier split probes and exception attribution (09-22-udp-teardown-session-tier-alloc) |
| `2da59b0` | docs(spec): re-anchor the Noop probe and churn session budgets (09-22-udp-teardown-session-tier-alloc) |
| `b2ee992` | chore(task): record 09-22-udp-teardown-session-tier-alloc artifacts |

### Status

[OK] **Completed**


## Session 36: Admission and capacity pre-seed split
<!-- trellis-session: v=2 fp=be9530fc63d1f618 -->

**Date**: 2026-09-22
**Task**: Admission and capacity pre-seed split
**Branch**: `feat/transport-lifecycle`

### Summary

Split the UDP admission leg (S1 - S0 = 828.8) and the capacity pre-seed (S0 = 488.9) with 13 new A-series decomposition cases. Exact reconciliations: S0 = association-dict 325.9 + sessions-dict 163.0 + cooldown 0.0 = 488.9; S1 = 488.9 + H 356.8 + steady-state 200.0 (slot+queue+completion cell) + cold rent 272.0; the warm-rent variant A8 = 1,045.7 proves production steady-state admission is ~200 B/session, with 79.7 B/add amortized dictionary growth beyond the 1,024 clamp (A9 contrast). No product change adopted: the one candidate (inline the per-slot queue object) measured 24.0 net after the slot's inline payload, below the 64 rule - the verified class-to-struct conversion was reverted and its patch archived for any future decision. hot-path.md section 3 now carries the split note (T2 unchanged at 1,432.9); gates green on the final tree (build/tests/807/format/jb zero issues); Noop 5,162.4 and churn 12,591.2 re-confirmed inside the established spreads.

### Git Commits

| Hash | Message |
|------|---------|
| `ed204e1` | bench(benchmarks): admission/capacity split probes with warm-rent and growth variants (09-22-udp-admission-capacity-alloc) |
| `a14ebd5` | docs(spec): annotate the admission/capacity sub-anchor with the split (09-22-udp-admission-capacity-alloc) |
| `30bbed9` | chore(task): record 09-22-udp-admission-capacity-alloc artifacts |

### Status

[OK] **Completed**


## Session 37: TCP redirect lane-batched injection + in-place rewrite (research F1)
<!-- trellis-session: v=2 fp=8f41af6b4f43508b -->

**Date**: 2026-09-29
**Task**: TCP redirect lane-batched injection + in-place rewrite (research F1)
**Branch**: `master`

### Summary

Task 09-29-tcp-redirect-batched-injection (research F1): the client-facing TCP redirect data legs joined the batched-injection mechanism and rewrite in place on the capture slot — ~32x fewer injection IOCTLs, one memcpy and one pool round trip removed per proxied packet. Independent trellis-check found a real R6 zero-allocation violation in the lane container (264 B per (adapter,direction) per iteration), fixed with a spare-lane pool and gated by a new allocation test; three residual findings (orphan cross-adapter lane, degraded-retry duplication, spurious DEBUG assert) were fixed and pinned.

### Main Changes

- Redirect data legs now accumulate into per-(adapter, target direction) lanes flushed once per pump iteration and rewrite in place on the pump's capture slot (no rental, no copy); control frames stay immediate; cross-adapter targets are scope-gated; a failed batch degrades per frame into the established client-reset/fail-closed tail.

### Git Commits

| Hash | Message |
|------|---------|
| `98d7232` | perf(redirect): batch TCP redirect data-leg injection and rewrite in place (09-29-tcp-redirect-batched-injection) |
| `1a56eec` | test(redirect): cover lane batching, in-place staging and the 0 B lane gate (09-29-tcp-redirect-batched-injection) |
| `9225ae6` | fix(test): assert accept-loop back-off by attempt count, not wall-clock elapsed (09-29-tcp-redirect-batched-injection) |
| `73d21f2` | docs(spec): record the redirect deferred-injection and in-place contracts (09-29-tcp-redirect-batched-injection) |
| `57c7fe5` | chore(task): record 09-29-tcp-redirect-batched-injection artifacts |

### Testing

- [OK] Release: build 0W/0E; 952 Core.Tests + 18 Analyzers passed; dotnet format --verify-no-changes empty; jb inspectcode 0 issues. Windows-only anchors (TcpThroughputScenario socks5/bare, gc-soak) deferred to windows-real-nic.

### Status

[OK] **Completed**

### Next Steps

- Archive-check 09-28-udp-association-reuse (looks complete); Windows real-NIC validation of the batched redirect path; remaining F2/F3/F4/F5/F6/F8 findings from 09-29-tcp-udp-path-structural-perf.


## Session 38: Benchmark coverage for the remaining structural findings (F2-F8)
<!-- trellis-session: v=2 fp=92411211c09e78fa -->

**Date**: 2026-09-30
**Task**: Benchmark coverage for the remaining structural findings (F2-F8)
**Branch**: `master`

### Summary

Built the measurement coverage the archived F2-F8 research names, recorded before-baselines, and made each claim either an exact gate or a labelled series. No product behaviour changed.

### Main Changes

- New scenarios and rows: scaling contention with the real self-traffic registry A/B, UDP ready-path contention, flow-table production shapes, sweep-pause probe plus a controlled claim loop, TCP churn with an attribution tail, composed IPv4/IPv6 redirect data path, pump idle/wake, live-residency census. Four exact gates (sweep zero-allocation, pump read calls and idle allocation, composed-leg zero allocation) and frame-builder validity tests. Independent check verified every quoted number against its artifact, proved five self-checks can fail by injection, and raised two blockers: the JetBrains gate (13 genuine findings, fixed; the 57 CSharpErrors were a cold-cache artifact) and a pre-existing flaky allocation gate plus one 41-minute hang, both recorded for a dedicated follow-up.

### Git Commits

| Hash | Message |
|------|---------|
| `47c3110` | chore(task): archive 09-29-benchmark-coverage-remaining-findings |
| `312add1` | chore(task): record the benchmark-coverage task and sync the parent backlog (benchmark-coverage-remaining-findings) |
| `c18b06c` | docs(spec): record measurement self-check conventions (benchmark-coverage-remaining-findings) |
| `0538819` | docs(bench): document the new scenarios, artifacts and the inspection evidence (benchmark-coverage-remaining-findings) |
| `a43645d` | test(bench): sweep, frame-builder, read-call and data-path allocation gates (benchmark-coverage-remaining-findings) |
| `4d5fb76` | perf(bench): F2-F8 measurement coverage: scenarios, rows and harness plumbing (benchmark-coverage-remaining-findings) |

### Testing

- [OK] build 0 warnings; Core.Tests 979 + Analyzers 18, 0 failures (also under --blame-hang); dotnet format --verify-no-changes exit 0 with empty output; jb inspectcode 0 Issue / 0 CSharpErrors; git diff -- src/ empty

### Status

[OK] **Completed**

### Next Steps

- Operator pipeline: stabilise the flaky/hang tests first, then one Trellis task per finding in the order F3 sweeps, F2 locks, F4 keys/parsing, F5 pump I/O, F8 attribution, F6 UDP footprint, F7 WFP — each with PRD, a review sub-agent, implement, check, a benchmark proof recorded under benchmarks/results/, commit and archive.


## Session 39: Test stabilization part 1: the tiering flake and the SetupExecutor enqueue/dispose race
<!-- trellis-session: v=2 fp=9daf47afb9b8ee02 -->

**Date**: 2026-09-30
**Task**: Test stabilization part 1: the tiering flake and the SetupExecutor enqueue/dispose race
**Branch**: `master`

### Summary

Diagnosed and fixed both defects the benchmark-coverage check found, and reported honestly that the suite-level stability criterion is not met by the residual family.

### Main Changes

- Defect A: an expert review killed three false premises, and the diagnosis then showed the failing test was the dispatcher gate (7520 = 4 x 1880 summed lumps), with the lump living in a counter-read-only control loop - tiered compilation publishing hot code allocates once on the calling thread. Fixed by a host contract (TieredCompilation=false) plus the landed gate shape (synchronous completion, asserted thread id, exact zero, call-count backstop), with discrimination re-proven at 5632 B and 22528 B exact. Defect B: the SetupExecutor enqueue-after-drain race fired naturally (pre-fix 3/3 host aborts and 1997/1998 stranded completions), fixed with the pool family's post-enqueue recheck and a worker guard. Hunt: 47 suite runs, no hang, p < 6.4 pct, with the naming mechanism demonstrated by an injected hang.

### Git Commits

| Hash | Message |
|------|---------|
| `9b692c2` | chore(task): archive 09-30-test-flake-and-hang-stabilization |
| `fa227b6` | chore(task): record the test-stabilization task and seed its follow-up (test-flake-and-hang-stabilization) |
| `963bbe4` | docs(spec): record the tiering host contract, the gate shape and the repeat-run proof (test-flake-and-hang-stabilization) |
| `dc16b85` | test: pin the allocation-gate host contract and the gate shape (test-flake-and-hang-stabilization) |
| `136d278` | fix(runtime): settle every accepted setup item when Dispose races an enqueue (test-flake-and-hang-stabilization) |

### Testing

- [OK] build 0 warnings; suite 980 + 18 green under --blame-hang; dotnet format exit 0 empty; jb inspectcode 0 Issue after fixing its one genuine finding (a vestigial probeKeepGoing variable, fixed by asserting it); filter 100/100 consecutive green; full-suite >=40 consecutive NOT met - 29 runs, longest streak 12, residual carried by 09-30-exact-gate-residual-lumps

### Status

[OK] **Completed**

### Next Steps

- Task 09-30-exact-gate-residual-lumps: diagnose the once-per-process host lumps that survive the tiering-off contract (pump 168/5216 B, sweep 7384 B) and the LayeredCaptureRunnerHealthSignalTests forced-refresh race, land a gate shape or fix that still fails on an injected allocation, then prove >=40 consecutive green full-suite runs. After that, the operator's F2-F8 pipeline in the order F3, F2, F4, F5, F8, F6, F7.


## Session 40: Test stabilization part 2: the residual exact-gate lump and the health-signal race
<!-- trellis-session: v=2 fp=0018c9eb987c9c4d -->

**Date**: 2026-09-30
**Task**: Test stabilization part 2: the residual exact-gate lump and the health-signal race
**Branch**: `master`

### Summary

Attributed the residual suite-level lump as far as the evidence allows, landed disposition (c) with a per-gate-process proof, and fixed the health-signal race at its ordering cause. The suite-level rate is recorded, not claimed.

### Main Changes

- Multiplicity: 0/27 processes and 0/249,881 windows in isolation, so the residual is suite-process-conditional, not window-conditional. Attribution: dotnet-trace installed with its observation guarantee pre-registered, proved able to name a continuously allocating type and proved blind to the one-shot class under diagnosis (0 samples for a single 8 KB allocation the counter read as 8,280 B), so R1 was re-scoped and the product is explicitly NOT claimed excluded. Disposition (c): no product fix for the lump, no gate change; the per-gate-process proof (20 runs x 4 gates, 80/80 green, 0 signature matches, class totals asserted) plus the accepted rates with CIs and the sensitivity floor. Race: InterceptionHealthMonitor now captures a ForcedRefreshTrigger snapshot in the same locked section that arms the trigger, so NoteRefreshCompleted cannot rewrite what the runner logs; a new deterministic test runs the reset inside the handler.

### Git Commits

| Hash | Message |
|------|---------|
| `3962832` | chore(task): archive 09-30-exact-gate-residual-lumps |
| `d3b50c1` | chore(task): record the residual-lump task and seed its follow-up (exact-gate-residual-lumps) |
| `ac98dbc` | docs(spec): record the residual lump's multiplicity, attribution limit and per-gate proof (exact-gate-residual-lumps) |
| `4a23906` | fix(runtime): snapshot the forced-refresh trigger under the monitor gate (exact-gate-residual-lumps) |

### Testing

- [OK] build 0 warnings; suite 981 + 18 green; dotnet format exit 0 empty; jb inspectcode 2 genuine findings in the new test code fixed -> test project 0 issues; frozen tree rev=e28cb87 tree=883583fc055516886b90b30091c07597a9ae677e; per-gate 80/80; suite rate measured 2/13 with one signature-matched hit (sweep 7448 B) and one new failure (UdpAssociationHeadTests 282 vs 281) carried by 09-30-udp-association-head-count-flake [CORRECTION 2026-09-30: that failure was at UdpAssociationHeadTests.cs:110 on server.AssociateReplyCount, not :104 on pool.AssociationCount; the pool's shape assertions passed in the same run, and the cause was the scripted fake publishing its reply counter after the reply write, fixed in the follow-up task]

### Status

[OK] **Completed**

### Next Steps

- Task 09-30-udp-association-head-count-flake (part 3): diagnose the fixture reply-counter race (Expected 282, Actual 281 at UdpAssociationHeadTests.cs:110, server.AssociateReplyCount; the pool's shape was intact), then the operator's F2-F8 pipeline in the order F3 sweeps, F2 locks, F4 keys/parsing, F5 pump I/O, F8 attribution, F6 UDP footprint, F7 WFP - each with a PRD reviewed by a sub-agent, implementation, an independent check, a recorded benchmark proof before archiving, commit, archive and journal.


## Session 41: Test stabilization part 3: the fake server's reply-counter race (and the residual family's real boundary)
<!-- trellis-session: v=2 fp=b64c99f2a0eb0b8a -->

**Date**: 2026-09-30
**Task**: Test stabilization part 3: the fake server's reply-counter race (and the residual family's real boundary)
**Branch**: `master`

### Summary

Fixed a test-fixture ordering race that made ten assertions flaky, corrected an earlier misattribution in the archive and the journal, and widened the residual host family's definition to the gate shape after it appeared at four gates and three new sizes.

### Main Changes

- The 282-vs-281 failure was NOT pool.AssociationCount (that assertion passed, as did three others above it): it was server.AssociateReplyCount at UdpAssociationHeadTests.cs:110, and the cause was ScriptedSocks5UdpServer incrementing its counter after the reply write while a client's rent completes on reading it. The fix orders publish the counter, run the test gate, then write; the new ScriptedSocks5UdpServerOrderingTests holds the write open and failed Expected 1 Actual 0 before the change. Ten dependent assertions in five files, all deterministic after the fix. Also corrected the archived residual record and the journal, which had blamed pool.AssociationCount, and added an addendum widening the residual host family to 'any *AllocateNoManagedBytes exact gate' after this session produced four gates and three sizes not in the recorded signature (56, 6128, 7872 B plus 7448 B).

### Git Commits

| Hash | Message |
|------|---------|
| `d149cb4` | chore(task): archive 09-30-udp-association-head-count-flake |
| `eb8c42e` | chore(task): record the fixture race task and correct the earlier misattribution (udp-association-head-count-flake) |
| `6cace12` | docs(spec): record the publish-before-await and publish-before-readable invariants (udp-association-head-count-flake) |
| `aa6c29f` | fix(test): publish the fake's ASSOCIATE reply counter before the reply is readable (udp-association-head-count-flake) |

### Testing

- [OK] build 0 warnings; suite 982 + 18 green; dotnet format exit 0 empty; jb inspectcode 0 issues / 0 CSharpErrors; proof on frozen rev=3b9689c tree=e1e3800: new class 20/20, five dependent classes 100/100 (totals asserted each run), full suite 22/26 green with the 4 failures all in the residual host family and zero AssociateReplyCount failures in 146 runs; code and spec byte-identical between the frozen tree and the final tree (verified by tree diff)

### Status

[OK] **Completed**

### Next Steps

- F2-F8 pipeline is PAUSED by the operator. On resume, the order is F3 sweeps, F2 locks, F4 keys/parsing, F5 pump I/O, F8 attribution, F6 UDP footprint, F7 WFP (scope by research first), each with a PRD reviewed by a sub-agent, implementation, an independent check, a recorded benchmark proof before archiving, commit, archive and journal. The residual suite-level host lump family remains an accepted, documented host property (disposition (c) of 09-30-exact-gate-residual-lumps) with its gate coverage now widened.


## Session 42: F3 expiry sweeps: bounded-pause rounds at minimal hold granularity (and two measurement-driven reversals)
<!-- trellis-session: v=2 fp=95194a693766cf3c -->

**Date**: 2026-09-30
**Task**: F3 expiry sweeps: bounded-pause rounds at minimal hold granularity (and two measurement-driven reversals)
**Branch**: `master`

### Summary

Five sweep sites stop scanning their whole population under a table gate: FlowTable gets a live-slot registry and a chunked round whose holds examine <=256 entries and remove <=1 (predicate outside every table lock), the other sites reuse their retirement scratch and gate their async ticks with a SemaphoreSlim. Acceptance is work-per-hold by exact counts; the wall-clock pause series is report-only because a no-sweep control reproduces it. Batched removals were implemented, measured and rejected (they cost an order of magnitude of warm-path progress). Warm resolves during sweep windows: 6.5-10.4k -> 13.1-14.1M.

### Main Changes

### Main Changes

F3 of the structural perf research (`09-29-tcp-udp-path-structural-perf`) — the expiry sweeps stop walking
their whole population under a table gate. Site 1 (`FlowTable.RemoveExpired`) became a chunked round at
**minimal hold granularity**: a new `_liveStates` live-slot registry replaces the 65,536-entry scan, every
`_gate` hold examines at most `SweepChunkEntries` (256) entries and removes at most one, and the holds-flow
predicate now runs with no table lock held, which removes the store/tombstone lock-nesting edge. Sites 2–6
(`TcpRedirectTable`, `TcpRedirectSessionStore` + `TcpRedirectTombstoneTable`, `UdpProxyCoordinator`,
`UdpAssociationPool`, `UdpAssociationTable`) reuse their retirement scratch, re-check under one short hold,
and the three async sites gate their tick with `SemaphoreSlim(1,1)` because a `Lock` cannot span an await;
two per-tick delegate allocations (`_prunePendingSyn`, `_holdsFlow`) were hoisted.

### Measurement-driven reversals (the load-bearing part of this task)

1. The first acceptance instrument was wrong: of the baseline's 21,252 pauses > 500 us, >=96 % came from the
   scenario's own 65,536 refill claims contending on the same gate, and a control run with the sweep window
   armed but **no product call at all** still measured a 5.19 ms in-window max. Acceptance moved from
   wall-clock pause to **work per hold, by exact counts**, with the pause series demoted to report-only.
2. The reviewer's proposed batched-removal sketch had a counterexample: without a cursor rewind the
   all-idle-elapsed round removes only 32,768 of 65,536 (simulated, then reproduced by deleting the rewind
   line: `Expected: 65536 / Actual: 32768`).
3. Batched removals were then implemented, measured and **rejected**: 256 removals per hold bought the
   sweep's own duration (120 -> 35 ms) with an order of magnitude of the property the task exists to fix
   (13.1–14.1 M in-window resolves -> 89–120 k). The landed shape is minimal hold granularity and the
   sweep's own duration is deliberately report-only.

### Evidence

| Criterion | Result |
|---|---|
| Work per hold (exact counts) | MaxExaminations 1 (all-idle fixture) / 256 (production shape), **MaxRemovals 1**, one call still removes all 65,536 |
| Warm path stops starving | resolves completed inside sweep windows 6,469–10,446 -> **13,145,035–14,070,168** per 15 s (3 runs) |
| Production-shaped sweep | 4,096 live / 32 idle: 48 scan + 32 removal holds, 0.033–0.036 ms, duty cycle ~0.0001 % at the 60 s cadence |
| Allocation gates | sites 2–6 red -> 0 B (355,672 / 520 / 272 / 328 / 4,184 -> 0); site 1's gate and the predicate/registry facts green |
| Suite / gates | 1,013 tests green; per-gate proof **80/80** process runs green (totals 11/3/12/14, no lump, no vacuous match); `dotnet format` empty; `jb inspectcode` 0 issues |

### Testing

- [OK] Release build 0 warnings/0 errors; `dotnet test -c Release` 995 + 18 green on the final tree.
- [OK] Nine exact allocation windows re-discriminated (one injected `new byte[64]` -> `Actual: 88`, restored).
- [OK] Specs updated: `hot-path.md` (registry + minimal-granularity + no-op-tick gate shape + acceptance
  classification + totals), `traffic-policy-lifecycle.md` (per-site retirement, semaphore gate, corrected
  `DeriveUdpSweepInterval`), gate rows in `tcp-local-redirect.md` / `udp-relay.md`.

### Status

[OK] **Completed** (archived 2026-09-30)

### Next Steps

- F2 locks (next in the operator's F2–F8 pipeline): self-traffic reorder, lock-free resolve, UDP
  ready-path. The F3.4 activity bucket is deferred to it, with the seven-item representation contract in
  the archived task's `research/implementation-notes.md` §8 and the `FlowState.Reset` clock-source defect
  (D9) named there. Then F4 keys/parsing, F5 pump I/O, F8 attribution, F6 UDP footprint, F7 WFP scoping.


### Git Commits

| Hash | Message |
|------|---------|
| `c73506d` | test(bench): phase-scoped sweep-window metrics, control mode and the F3 before/after evidence (expiry-sweep-bounded-pause) |
| `f9361da` | perf(flow): bounded-hold expiry sweeps at every site; hold predicate off the table lock (expiry-sweep-bounded-pause) |
| `18671e8` | docs(spec): record the F3 sweep contracts, gate shapes and the cadence correction (expiry-sweep-bounded-pause) |
| `3563bdc` | chore(task): record the F3 sweep task and sync the parent backlog (expiry-sweep-bounded-pause) |

### Status

[OK] **Completed**


## Session 43: F2 warm-path lock chain: direct-mapped warm cache (and the ConcurrentDictionary mechanism rejected by the 0 B gate)
<!-- trellis-session: v=2 fp=c53d28701d2b1a10 -->

**Date**: 2026-09-30
**Task**: F2 warm-path lock chain: direct-mapped warm cache (and the ConcurrentDictionary mechanism rejected by the 0 B gate)
**Branch**: `master`

### Summary

The warm path stops paying 3-5 global locks per packet: lock-free 500 ms activity buckets ticked once per pump iteration, a pre-allocated direct-mapped warm cache with exact key validation over the gated dictionary, the self-traffic exact-tuple check moved to claim time with the wildcard half kept lock-free, one reverse probe for TCP, and a wait-free UDP ready send. Warm-arm self-normalised four-thread ratio 0.153-0.161 -> 0.933-0.980, UDP ReadySend 523 -> 186-188 ns, 0 B everywhere. The first mechanism (ConcurrentDictionary + FlowState view) hit the ratio and was rejected for allocating 488 B per claim against the exact 0 B gate.

### Main Changes

### Main Changes

F2 of the structural perf research (`09-29-tcp-udp-path-structural-perf`) — the warm packet path stops
paying its lock chain. Activity became a lock-free 500 ms bucket (`ActivityBucket`/`ActivityBucketClock`,
ticked once per pump iteration in the composition through `FlushPendingInjections`, with `FlowState.Reset`
routed through the injected clock, fixing F3's D9 defect); `FlowTable` gained a **pre-allocated
direct-mapped warm cache** with exact key validation (seqlock snapshot + transport-tuple corroboration)
over the still-authoritative gated `Dictionary`, so the warm resolve takes no gate while the exact 0 B
claim/expire gate stays exact; the self-traffic **exact-tuple** half moved to claim time while the
**wildcard relay-socket** half stays on the warm path, lock-free; TCP pays one reverse probe instead of two
gate entries; the UDP ready send is wait-free (no `_activityGate` on the send/touch paths, cached session
lookup).

### Measurement-driven reversals (again the load-bearing part)

1. The first mechanism — `ConcurrentDictionary` indexes plus a `FlowState` view — **delivered the ratio**
   (0.628 four-thread self-normalised, 11.06 M/s) and then was **rejected by the allocation contract**: it
   allocates a `Node` per inserted key, 488 B per claim (three nodes), so
   `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` read `Expected: 0, Actual: 124928`. Sanctioning
   that would have regressed a deliberate pooled-state gate.
2. The replacement cache then **missed the criterion as first specified** (2 × capacity slots → λ ≈ 0.4 →
   ~33 % miss → ratio 0.39–0.50 modelled) until the sizing was derived from the measured endpoints and set
   at 64 × capacity (m ≈ 1.6 %, 2 MB).
3. After the cache landed, the ratio still read 0.389–0.406 — and the decomposition showed why: the cache
   probe costs ~7 ns at a 0.73–0.93 % miss rate, while the **still-gated wildcard guard** cost ~142 ns, and
   the four-thread ceiling was identical to the rejected variant's. Step 4's lock-free guard was the lever,
   not the resolve mechanism.

### Evidence

| Criterion | Result |
|---|---|
| Warm resolve takes no global gate (exact) | flow table 1→0 gate entries, redirect 2→0 gates / 2→1 probes, UDP coordinator 1→0, session activity gate 2→0; parked-gate facts green, red `Expected: 0, Actual: 256` before |
| Self-traffic split | 0 exact probes over 256 warm hits, 8/8 claims, relay-wildcard fact green (red only against the naive-deletion variant: 1/2 — recorded honestly) |
| Activity bucket | no clock read in `Touch` (throwing-clock facts), `ReadActivityClock` retired, 16 pump iterations → 16 ticks |
| Scaling | warm arm self-normalised four-thread ratio **0.980 / 0.957 / 0.933** (line 0.6); 1-thread 5.96–6.03 M/s; fixed-denominator 1.753 / 1.733 / 1.685; miss 0.69–1.10 %; per-lookup CPU flat 166–178 ns across 1/2/4 threads |
| UDP ready path | four-worker `ReadySend` **186.1 / 187.8 / 186.8 ns** (line ≤261.7), 0 B every row |
| Memory | `_warm` 2,097,152 B; residency +2.61 MB (+7.9 %) vs recorded floor; gc-soak slope 0, pool outstanding/overflow delta 0 |

### Testing

- [OK] Release build 0 warnings/0 errors; full suite 1021 + 18 green (two runs; one earlier run hit the
  documented suite-conditional host lump on `SynRetentionWithWarmSynCopyPoolAllocatesNoManagedBytes`
  — 4,520 B, a new size in the widened `*AllocateNoManagedBytes` family, green 6/6 in per-gate process runs
  and green in the next full-suite run; recorded in `gate-stability.txt`).
- [OK] `dotnet format` exit 0 with empty output; `jb inspectcode` **0 issues** (21 fixed in two passes,
  none by suppression).
- [OK] Per-gate stability: `HotPathAllocationGateTests` 11/11, `SweepAllocationGateTests` 20/20 (Total 12),
  `CapturePumpReadCallTests` 20/20, plus the structural 20/20 for the warm-path classes.
- [OK] Six spec files updated with the new contracts and the spec-row → proof map.

### Status

[OK] **Completed** (archived 2026-09-30)

### Next Steps

- F4 keys/parsing (next in the F2–F8 pipeline): interned adapter slot, parse-once view, atomic sequence
  trackers, slimmer `FlowContext`. Then F5 pump I/O, F8 attribution, F6 UDP footprint, F7 WFP scoping.


### Git Commits

| Hash | Message |
|------|---------|
| `ea13924` | test(bench): warm-resolve arm, gate/clock probes and the scaling/UDP evidence (warm-path-lock-chain) |
| `02fee48` | perf(flow): lock-free warm path - bucketed activity, direct-mapped warm cache, claim-time self-traffic, one reverse probe, wait-free UDP send (warm-path-lock-chain) |
| `cee7063` | docs(spec): record the F2 warm-path contracts, cache failure modes and the spec-row-to-proof map (warm-path-lock-chain) |
| `fc0a867` | chore(task): record the F2 warm-path task and sync the parent backlog (warm-path-lock-chain) |

### Status

[OK] **Completed**


## Session 44: F4 keys and parsing: a 64 B interned key, one parse per frame, lock-free trackers (and a live mis-rewrite found by hardening)
<!-- trellis-session: v=2 fp=ff2a890f3ac37b28 -->

**Date**: 2026-10-01
**Task**: F4 keys and parsing: a 64 B interned key, one parse per frame, lock-free trackers (and a live mis-rewrite found by hardening)
**Branch**: `master`

### Summary

FlowKey 128->64 B via an interned adapter slot (generation kept, refusal instead of aliasing), parse-once through a 16-byte PacketLayout with a validity stamp, CAS-max sequence trackers with the gate deleted, and an 80 B interned per-packet context. All 16 datapath rows improve (worst 1.50x; the IPv6 reverse row 1.68-1.88x), ResolveWarmHit 76->26 ns, walks 4->1 per leg. Hardening the defaulted layout exposed a live latent mis-rewrite at offset 26 that the allocation gate had been passing over. The optional IPv6 checksum step was rejected by measurement.

### Main Changes

### Main Changes

F4 of the structural perf research (`09-29-tcp-udp-path-structural-perf`) — the packet path stops paying for
data structures built for convenience. `FlowKey` went 128 → **64 B**: the `string? OriginAdapterId` became a
`ushort OriginAdapterSlot` interned through a process-lived `AdapterSlotTable` (monotone, never-reused slots;
an un-internable adapter is refused rather than aliased onto `NoSlot`; the generation stays an integer key
field so equality keeps today's semantics), the endpoints are packed with computed `Local`/`Remote`
accessors, and F2's warm-cache hash/equality were re-proven against an independent pre-F4 oracle rather than
against the packed function itself. A frame is now parsed **once**: a 16-byte `PacketLayout` (with the
address family, plus a private-constructor stamp that makes `default` invalid) is carried on the packet and
consumed by `IsTcpSyn`, all four sequence observations, the rewriter and the checksum path, with the
span-taking entry points kept as the oracle. The two `uint?` sequence trackers and their `_sequenceGate`
became CAS-max `long`s (two lock entries per packet gone), and the per-packet context is interned
(`FlowContext` 168 → **80 B**, `CapturedFlowPacket` 224 → **152 B**, `FlowStateView` → 96 B).

### The hardening that found a live bug

The previous agent flagged that a `default(PacketLayout)` is indistinguishable from a valid TCP layout
(`PacketTransport.Tcp == 0`). Hardening it produced three red-before facts on the unmodified tree, and one
was **live**: `HotPathAllocationGateTests.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes` had been
passing while its leg mis-rewrote a frame at offset 26. Only `PacketLayout.From(in PacketView)` can stamp a
valid layout now, every consumer gates on `IsTcp`, and a dispatched flow packet provably carries a parsed
layout.

### Measurement

- All 16 `tcp-redirect-data-path` rows improve, none regresses (worst 1.50×, max after/before 0.665); the
  AC-4 row `ReverseLegForwarded` IPv6 112.05 → 59.64 ns (128 B) and 122.07 → 72.87 ns (1400 B); the
  IPv6:IPv4 ratio reading moved 1.65 → 1.92 (recorded, not gated) while the absolute gap narrowed
  48.07 → 34.97 ns.
- `flow-table-production-shape` `ResolveWarmHit` 76.21 → 26.06 ns; walk counts (not timings) carry the
  parse-once claim: 4 walks/leg → 1 parse + 0 revalidations + 1 view rewrite, red-before re-derived from
  the pre-change source.
- The optional single-pass IPv6 checksum delta was **rejected by measurement**: the adoption precondition
  ("still slow while every other row is unmoved") was false, and its isolated ~5.5 ns win is under this
  host's noise floor.
- Sizes asserted exactly: `FlowKey` 64, `FlowContext` 80, `CapturedFlowPacket` 152, `FlowStateView` 96,
  `PacketLayout` 16.

### Testing

- [OK] Release build 0 warnings/0 errors; full suite **1057 + 18** green (five runs across the task).
- [OK] `dotnet format` exit 0 with empty output; `jb inspectcode` **0 issues** (29 + 44 findings fixed
  across two passes, 4 narrow suppressions, each with a verifiable reason — `[ThreadStatic]` cannot back an
  auto-property, the cross-assembly layout accessors are the repo's documented false-positive class, and two
  join-before-dispose orderings).
- [OK] Per-gate stability: 42 process runs over 17 classes, 0 failures, 0 vacuous totals, every total
  stable; the four exact allocation gates unchanged at 11/3/12/14.
- [OK] Six spec files updated plus a 14-row F4 spec-row → proof map.

### Status

[OK] **Completed** (archived 2026-09-30)

### Next Steps

- F5 pump I/O (next in the F2–F8 pipeline): speculative batched read and an event-driven idle wake, with the
  on-Windows ABI question recorded. Then F8 attribution, F6 UDP footprint, F7 WFP scoping.


### Git Commits

| Hash | Message |
|------|---------|
| `31b340b` | test(bench): F4 walk/gate probes, size facts and the before/after series (flow-key-parse-once) |
| `7cc794a` | perf(core): interned adapter slot in a 64 B flow key, parse-once layout, lock-free sequence trackers, 80 B context (flow-key-parse-once) |
| `d43d85d` | docs(spec): record the F4 key/parse/context contracts, the layout stamp and the spec-row map (flow-key-parse-once) |
| `59e80fc` | chore(task): record the F4 task and sync the parent backlog (flow-key-parse-once) |

### Status

[OK] **Completed**


## Session 45: F5 pump I/O: read-first drains with a self-healing ABI guard, and an event-driven idle wake (31x idle CPU)
<!-- trellis-session: v=2 fp=988d148d34614f7f -->

**Date**: 2026-10-01
**Task**: F5 pump I/O: read-first drains with a self-healing ABI guard, and an event-driven idle wake (31x idle CPU)
**Branch**: `master`

### Summary

The capture drain issues the batched read first (the queue query demoted to the non-success disambiguator, both ABI hypotheses proven at a new driver-native seam) and idles on the driver's packet-arrival event instead of a 1 ms sleep. Idle CPU 0.0178 -> 0.00055 CPU-s/s (~31x, same-session A/B), cadence 886 -> 10 waits/s, wake p50 0.078 -> 0.066 ms. Since the real ABI cannot be verified on this host, a self-healing guard arms a sticky query-first shape on a read failure with a non-empty queue and logs it, instead of retrying into a false adapter degradation (red-before True|5|1|31). Nine Windows experiments recorded with fallbacks.

### Main Changes

### Main Changes

F5 of the structural perf research (`09-29-tcp-udp-path-structural-perf`) — the capture pump stops paying
two IOCTLs per drain and a 1 ms sleep per idle poll. The drain now issues the **batched read first** and the
queue-size query is demoted to the **non-success disambiguator**: the empty case is recognised from the read
result where the ABI carries it, and where the read does not succeed the query separates "empty" from
"driver error", reproducing the old classification state-for-state. Both ABI hypotheses are implemented and
proven at the **driver↔native** seam, whose read/query counters are new (the pump↔driver seam already
existed and proves nothing new); the fail-closed equivalence table was written down and pinned row by row.

Because this host has no Windows driver, the read-first shape had an unverified hazard: an ABI that fails a
request larger than the queue depth would retry into a **false adapter degradation** after ~3.1 s. The
operator rejected both "detect it on Windows later" and re-introducing a pre-emptive query, so the task
landed a **self-healing ABI-mismatch guard**: a failed read with a non-empty queue arms a sticky
per-handle query-first shape, publishes a rate-limited `adapter.readShape.mismatch` diagnostic (adapter,
requested count, observed depth, native error) through a sink the driver owns, and retries the drain
immediately instead of entering the transient budget. A conforming driver never arms it. The red-before is
recorded: without the guard the mismatching fake degrades the adapter (`True|5|1|31`, no throw).

The idle path became a bounded **event wait** on the driver's packet-arrival signal
(`SetPacketEvent`, auto-reset, with the retention argument that makes "empty read → signal → wait" safe),
behind an injectable `INdisPacketArrivalSignal`; a pump with no signal keeps the sleep-poll shape
byte-identically.

### Measurement

| Reading | Before | After |
|---|---|---|
| idle CPU (CPU-s per idle second) | 0.017798 / 0.018501 / 0.017826 | **0.000548 / 0.000534 / 0.000580** (~31×, same-session A/B) |
| poll cadence | 877.6 / 885.6 / 885.8 polls/s | **10.0 waits/s** (~90× fewer) |
| wake p50 | 0.0779 / 0.0783 / 0.0791 ms | **0.0660 / 0.0658 / 0.0653 ms** |
| park confirmations/wake | n/a | **1.0002**, with 5,000/5,000 signal-driven returns |
| drained reads (loaded) | `[Query, Read]` | `[Read]` |
| empty drain | `[Query]` then read | `[Read]` (hypothesis B) or `[Read, Query]` (hypothesis A), no error |

### Testing

- [OK] Release build 0 warnings/0 errors; full suite **1081 + 18** green (the +24 delta is exactly the new
  facts); per-gate stability **140/140** across 7 classes plus **40/40** after the gate-carrying files moved.
- [OK] `dotnet format` exit 0 with empty output; `jb inspectcode` **0 issues** (22 diagnostics fixed across
  three rounds; two narrow suppressions, each with a verifiable reason).
- [OK] Two new exact 0 B gates (`IdleWaitIterationsAllocateNoManagedBytes`,
  `DriverReadPathAllocatesNoManagedBytes`), both discrimination-proved — with the finding that a
  non-escaping `new byte[64]` inside a window does **not** fail on .NET 10, so the probe must escape.
- [OK] Nine Windows experiments recorded with expected observation and fallback, the partial-batch row
  acceptance-relevant; the pinned ABI itself stays unverified and nothing in the docs reads as if it were.

### Status

[OK] **Completed** (archived 2026-10-01)

### Next Steps

- F8 pump-thread attribution (next in the F2–F8 pipeline): move process attribution off the pump thread and
  cache the owner tables as a short-lived snapshot. Then F6 UDP footprint, F7 WFP scoping.


### Git Commits

| Hash | Message |
|------|---------|
| `6cb27bf` | test(bench): F5 read-shape counters, idle-wait rows and the before/after evidence (pump-io-shape) |
| `a488a99` | perf(ndis): read-first drain with a self-healing ABI-mismatch guard and an event-driven idle wait (pump-io-shape) |
| `ec0762f` | docs(spec): record the F5 read-shape, guard and arrival-signal contracts plus the Windows open items (pump-io-shape) |
| `e888921` | chore(task): record the F5 task and sync the parent backlog (pump-io-shape) |

### Status

[OK] **Completed**


## Session 46: F8 attribution off the pump thread: a bounded pending index and an owner-table epoch coalescer (pump stall 7.5 ms -> 0.02 ms)
<!-- trellis-session: v=2 fp=c5c45837ea680f02 -->

**Date**: 2026-10-01
**Task**: F8 attribution off the pump thread: a bounded pending index and an owner-table epoch coalescer (pump stall 7.5 ms -> 0.02 ms)
**Branch**: `master`

### Summary

Process attribution leaves the capture pump: a bounded per-flow pending index admitted by its own adapter's pump, a setup worker running attribution while the claim stays on the pump at delivery, in-order drain through a claim-last critical section, and an epoch coalescer so concurrent new flows share one system-wide table scan (UDP deliberately never caches - its predicate is local-port-only). Pump-thread attributions 64 -> 0, pumpBlockedMs p95 7.52-7.56 -> 0.019-0.028 ms (~380x), 16 concurrent flows -> 1 scan; tcpChurn calibration held; suite 1124+18 green; both commit gates at zero.

### Main Changes

### Main Changes

F8 of the structural perf research (`09-29-tcp-udp-path-structural-perf`, addendum §A3) — process
attribution leaves the capture pump thread. A flow-table miss that requires attribution is now admitted into
a bounded per-flow pending index (cap, global byte budget, 1 s cooldown, 5 s TTL, exactly one counted
refusal per class) **by its own adapter's pump**; a setup worker runs the attribution while the **claim stays
on the pump** at delivery, which is what keeps F1's batched-lane contract and F2's warm path untouched. The
pump drains a decided flow's retained packets in order through a claim-last critical section, the batch is
never detached from its entry (a per-batch `finally` Blocks the unexecuted remainder), and the failure arm
never claims — it Blocks, counts and arms the cooldown, with shutdown cancellation exempt. Admission is
gated to the attribution-eligible shape (process rules configured, host origin), so forwarded and no-rule
flows keep today's inline path.

The owner tables got an **epoch coalescer**: concurrent misses share one system-wide scan, and a retry joins
a later epoch by request instant instead of scanning again. **UDP never serves from the snapshot** — its
predicate matches the local port alone, so a recycled port inside the window would attribute a flow to the
previous process (planning caught this fail-open and closed it by caching TCP only).

### Measurement

| Reading | Before | After |
|---|---|---|
| pump-thread attributions (scenario, 64 flows) | 64 | **0** |
| setup-worker attributions | 0 | **64** |
| `pumpBlockedMs` p95 | 7.519–7.561 ms | **0.019–0.028 ms** (~380×) |
| scans for 16 concurrent new flows | 16 | **1** (`scansPerFlow` 0.0625) |
| `tcpChurn` allocated bytes/connection (must-not-move) | 91,088 / 91,066 / 91,031 | 91,046 / 91,069 / 91,057 |

### Testing

- [OK] Release build 0 warnings/0 errors; full suite **1124 + 18** green (six consecutive green suite runs on
  the final tree; the +25 facts are exactly the new ones).
- [OK] `dotnet format` exit 0 with empty output; `jb inspectcode` **0 issues** (58 findings fixed across two
  rounds — the dead `AttributionDiagnostics` snapshot, several dead fake members and an unused index field
  deleted rather than suppressed; three narrow suppressions with inline reasons).
- [OK] Per-gate stability 45/45 (9 classes × N=5) plus refreshed totals; the composite arrival gate and the
  rewritten coalescing fact both re-proved discriminating.
- [OK] Fixes worth naming: seven promised `RuntimeCounters` keys were never incremented (wired); the wake
  registry leaked one `EventWaitHandle` per adapter registration and was never disposed (now one per handle,
  disposed by the bundle); TTL-reclaimed packets are counted fail-closed drops; the wake fires on the
  `TryEnqueue`-refusal arm too; and one new fact was timing-flaky (rewritten deterministically).
- [OK] Windows residuals recorded: the handle→pump 1:1 precondition, iphlpapi behaviour, the deferred UDP
  snapshot, accepted UDP ring refusals, and N=5 (not 20) per gate.

### Status

[OK] **Completed** (archived 2026-10-01)

### Next Steps

- F6 UDP per-session footprint (next in the F2–F8 pipeline): the relay receive-buffer default, the
  receive-window pool sizing and the adaptive idle TTL. Then F7 WFP scoping (scope by research first; if it
  is not implementable in this repository, the PRD and its review record that outcome).


### Git Commits

| Hash | Message |
|------|---------|
| `acca507` | test(bench): F8 attribution-off-pump scenario, fakes and the before/after evidence (attribution-off-pump) |
| `4a0f70e` | perf(flow): attribution off the pump thread with a bounded pending index and an owner-table epoch coalescer (attribution-off-pump) |
| `f121a3d` | docs(spec): record the F8 pending-index, coalescer and wake contracts plus the Windows items (attribution-off-pump) |
| `ee40987` | chore(task): record the F8 task and sync the parent backlog (attribution-off-pump) |

### Status

[OK] **Completed**
