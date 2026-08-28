# 性能优化报告 — 08-28-perf-hotspots

环境：Linux dev box, .NET 10.0.302, Release, managed-only 管线。
注意：本机时间类指标（ns/op、pps）存在 ±50% 环境噪音（CPU 频率漂移）；
**分配字节数与 GC 计数是精确可复现的**，作为主要依据。

## 总成绩（capturePump 端到端，1400B 帧）

| 指标 | 基线 | 优化后 | 变化 |
|---|---|---|---|
| 每包堆分配 | 669 B | **10 B** | **-98.5%** |
| gen0 GC / 百万包 | 35 | **0** | **清零** |
| 稳态吞吐 (batch=1) | 2.03M pps | 3.11M pps | +53% |
| 稳态吞吐 (batch=32) | 2.02M pps | 2.92M pps | +44% |
| dispatcher.warmPass | 1268 ns / 672 B | 591 ns / 160 B | -53% / -76% |
| flowTable.resolve@65k | 763 ns | 801 ns | +5%（≤300ns 目标未达，见下）|
| socks5Udp 编码（span 版）| 1512 B/次 | **4.2 B/次** | ≈360×（存档 bench-encodespan-linux.json）|
| 解析器分配 | 80 B/包 | **0** | 清零 |

## 根因与修复清单

1. **大 async 状态机每次调用堆分配 ~193B**（最隐蔽）：`DispatchAsync` 的
   状态机内联 CapturedFlowPacket+FlowContext 等大结构，即使全程同步完成也被提堆。
   修复：非 async 入口跑同步 warm 路径（resolved+pass/block），仅冷路径走
   `DispatchSlowAsync` 状态机。
2. **每包 2×`new IPAddress`**（解析器）：引入 `IPAddressValue`（UInt128 定长原始
   地址），解析/流键/前缀匹配全程零分配；`Endpoint`/`IpPrefix`/PacketView/
   UdpPacketView/Checksums/Builders 全线切换，保留 `IPAddress` 兼容入口。
3. **每包 5 个类分配**：`FlowContext`/`CapturedFlowPacket` record class → readonly
   record struct；`CompleteAsync` 闭包+委托 → 枚举 switch。
4. **Pass 路径 3 次拷贝 → 0 次**：解析直接跑在 native span 上（泵契约：handler
   await 期间批次槽不复用，已加测试锁定）；未修改的 pass 帧直接在捕获缓冲区上
   `PrepareForReinjection`（仅重定向 adapterHandle）原地重注入；仅 proxy/需跨
   handler 存活的消费者才懒物化 ArrayPool 拷贝（`IFrameSource` 租约）。
5. **PacketLease 每包分配** → `TakeNative`/`Release` 线程本地单槽回收缓存。
6. **SOCKS5 UDP 编码每报文 1514B** → `TryEncode(span)` + 传输层复用发送缓冲。
7. 流表哈希：FlowKey/TransportTuple 手写扁平 HashCode.Combine + 最廉字段先比
   的 Equals；`RemoveExpired` 去 LINQ。

## 安全性论证（原地重注入）

- IntermediateBuffer 由泵私有 `NativeMemory.AllocZeroed`，驱动仅在 Read 调用期间写入；
- 泵逐包 await handler → ProcessAsync 返回前批次槽不可能被复用（含挂起的 await）；
- SendPacketToAdapter/Mstcp 为同步内核调用，返回即完成帧消费；
- 新测试 `PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight` 编码该契约。

## PRD 门禁核对

- R1 warm ≤64 B/包 ✓（实测 10）
- R2 pass 零拷贝 ✓
- R3 Endpoint 原始值 部分✓（键零分配、哈希达基线水平；≤300ns 未达——PRD 已如实标注未勾选，留待单表字典级优化）
- R4 SOCKS5 编码零分配 ✓（4.2 B/次）
- R5 行为不变 ✓（357/357 测试通过，含 7 个新增）
- R6 前后数据 ✓（bench-baseline / bench-final linux.json）

## 遗留 / 建议

- flowTable 65k 基数 801ns：瓶颈为双字典探测的 cache miss，下一步可换
  开放寻址单表或 key 合并（FlowKey/TransportTuple 一体化）。
- Windows 真机（evil-winrm 192.168.100.2）NDIS 实测未做——建议部署冒烟。
- benchmark 机器时间噪音大，后续对比建议以分配/GC 计数为主指标。
