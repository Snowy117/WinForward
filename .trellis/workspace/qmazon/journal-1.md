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
