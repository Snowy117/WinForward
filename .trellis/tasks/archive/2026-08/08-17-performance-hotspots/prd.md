# 性能热点分析与转发路径优化

## Goal

在不改变 WinForward 透明转发语义、fail-closed 行为和 NDISAPI ABI 正确性的前提下，降低抓包/转发热路径的内存分配、数据复制、锁竞争与端到端延迟，并建立可复现的性能基线，避免仅凭静态推断优化。

## Background

- 项目为 Windows x64、.NET 10 Native AOT 的透明 TCP/UDP SOCKS5 转发器；生产数据路径依赖 WinpkFilter/NDISAPI。
- 当前环境为 Linux，无法执行真实 NDISAPI/Windows ETW 硬件测试；Release 单元测试基线为 289/289 通过。
- 仓库暂无 benchmark 项目、分配基准、吞吐结果或原始延迟 trace。
- 已记录的唯一硬件基线是当前 `ReadPacket` + 1 ms 轮询在 tunnel mode 下增加约 5-15 ms RTT，平均约 8 ms，而 direct 平均约 0.6 ms（`.trellis/spec/backend/windows-ndisapi.md:92`）。
- 完整静态分析与实验建议见 `research/performance-hotspots.md`。

## Confirmed Hotspots

1. `NdisCapturePump` 每轮新建 1,566 字节非托管缓冲，先查询队列再单包读取，空队列延迟 1 ms；所有 adapter 共用一个 `NdisNativeCallGate`，读取和回注互相串行（`NdisCapture.cs:35-50`; `NdisApiDriver.cs:100-121,193-297`）。
2. 普通 pass 包至少发生 native -> managed `byte[]` 与 managed -> native 两次整帧复制；TCP/UDP proxy 路径还有额外整帧、payload、SOCKS5 frame 和响应重建复制（`CapturePacketProcessor.cs:34`; `NdisPacketActionExecutor.cs:39-43`; `TcpProxyCoordinator.cs:214,346,391`; `IpUdpPacket.cs:73`; `Socks5Udp.cs:14,61`; `UdpFrameBuilder.cs:49`）。
3. 新流 miss 先 `TryResolve`，随后 `TryClaimResolved` 内再次 resolve；每次 direct lookup miss 都可能在线性扫描最多 65,536 个 flow 的单锁临界区中执行（`FlowDispatcher.cs:86-108`; `Domain.cs:147-175,223-263`）。
4. 每个 UDP session 固定持有 65,535 字节 managed receive buffer；默认 16,384 session 容量仅该缓冲的理论上界约 1 GiB，未计 socket、task、字典和 control connection（`UdpProxyCoordinator.cs:28-51,487-505`）。
5. TCP relay 每方向持有 8 KiB buffer，并在每次 read/write 创建 linked `CancellationTokenSource` 和 timer（`TcpProxyRelay.cs:112-150`）。
6. 新 host flow 始终枚举完整 TCP/UDP owner table 后再做 process identity cache lookup；miss 还默认等待 2 ms 并重试，即使 policy 不含 process selector（`FlowDispatcher.cs:105-108,149-153`; `ProcessAttribution.cs:28-38,179-275`）。
7. `SelfTrafficRegistry`、flow/association/session 表和同步 console logger 使用共享锁；self-traffic wildcard lookup 与 expiry sweep 在高基数时执行线性扫描（`SelfTrafficRegistry.cs:22-40`; `Domain.cs:209-220`; `RuntimeLogging.cs:57-90`）。
8. 部分 disabled trace 调用在 `IsEnabled` 检查前已创建 `params` 数组和 boxed 字段；启用 trace 后还同步格式化并写 stderr（`FlowDispatcher.cs:214-227`; `NdisPacketActionExecutor.cs:136-145`; `RuntimeLogging.cs:57-90`）。

## Requirements

- 先建立 pass、block、TCP proxy、UDP proxy、host/forwarded、cold/warm flow 的可重复 microbenchmark 与 Windows 端到端测量矩阵。
- 优化必须由 benchmark、allocation profile、ETW/WPR 或明确的复制/容量计数证明；不接受只基于代码观感的改写。
- 第一优先级处理抓包唤醒/批量读取、NDIS packet buffer 生命周期及 native call gate 带来的 RTT 与 adapter 扩展性问题。
- 第二优先级处理 UDP session 固定大缓冲、UDP payload/frame 多重复制、TCP relay 每次 I/O 的 CTS/timer 分配。
- 第三优先级处理 flow/self-traffic lookup 的线性扫描、无条件 process attribution 与 disabled trace 构造成本。
- 可使用 `unsafe`、固定缓冲、池化和 NativeMemory，但所有权、长度边界、取消、异常清理和 double-dispose 行为必须可测试。
- 保持 NDISAPI enumeration handle、direction flags、metadata flags、TCP host/forwarded rewrite、UDP response reinjection、self-traffic loop prevention、fail-closed 和 shutdown ordering 合同。
- 每个优化批次必须保留纯单元测试，并在 Windows 硬件上执行适用的 smoke/性能矩阵后才能宣称端到端收益。

## Acceptance Criteria

- [ ] 提供可重复的基线命令/脚本，报告 allocated bytes/op、ops/s 或 packets/s、working set、CPU，以及适用场景的 RTT/TTFB p50/p95/p99。
- [ ] 对 pass、TCP proxy、UDP proxy 分别给出优化前后数据；测试参数至少覆盖 64、512、1514 字节 frame，并记录 adapter/session/flow 基数。
- [ ] idle 和负载下的内存、handle/socket 数、GC/分配率可观测；UDP session 数增长时不再按每 session 固定增加约 64 KiB managed buffer。
- [ ] 抓包层不再依赖 1 ms 空轮询作为主要唤醒机制，并支持批量 drain；Windows tunnel pass RTT p99 相对任务基线明显下降，且不引入丢包、重复回注或方向错误。
- [ ] 新流 admission 不再因 flow table 基数产生两次 O(n) 扫描；相关 benchmark 证明 miss latency 不随表容量线性增长。
- [ ] TCP relay 稳态 I/O 不再为每个 read/write 创建 linked CTS/timer；吞吐和 allocated bytes/sec 相对基线改善。
- [ ] disabled trace 热路径不创建 `params` field array/boxing；启用 trace 仍保持现有格式、隐私和 observational-only 语义。
- [ ] `dotnet test -c Release` 全部通过；`dotnet build -c Release`、trim/AOT analyzer 无新增 warning。
- [ ] Windows smoke 覆盖 pass 双方向、host TCP proxy、UDP proxy，以及有条件的 Hyper-V forwarded 路径；adapter mode 恢复和 fail-closed 行为保持不变。

## Out of Scope

- 改变用户配置格式、policy 语义、支持协议范围或 fail-closed 策略。
- 为性能牺牲 malformed/fragment/IPv6 extension-header 的边界检查。
- 在没有真实测量前承诺绝对 RTT、吞吐或内存数字。
- 本规划阶段修改任何产品代码。

## Decision

- 已确认：以低延迟 pass/短连接作为首要优化门槛；高并发 UDP session 内存占用和持续 TCP throughput 作为次级门槛。第一批优化优先验证 polling/native gate/packet-copy 路径，再处理 session footprint 与 relay 稳态分配。
