# Design: SOCKS5 full-path benchmarks and performance

## 总体边界

- **新增测量基建**全部落在 `benchmarks/WinForward.Benchmarks/`（Perf/ 与 Stability/），不改变产品对外契约。
- **产品代码改动**仅限优化本身（预计：`TcpFrameRewriter.cs`、`UdpProxyCoordinator.cs`/`UdpProxySession.cs`、可能 `TcpProxyRelay.cs` 数据面），全部为内部实现级改动。
- 基准与 soak 在 Linux 运行（BDN ShortRun 惯例）；Windows 实机冲烟只做功能完好性验证，不产基准数字。

## 测量基建设计

### M1 LoopbackSocks5TcpServer（Stability/，也被 Perf 复用）

最小 RFC 1928 服务端，仿照现有 `LoopbackSocks5UdpServer.cs` 惯例：

- greeting：接受 `[0x05, nMethods, methods]` → 回 `[0x05, 0x00]`（NO AUTH）。
- CONNECT：接受请求 → 回 `[0x05, 0x00, 0x01, 0,0,0,0, 0,0]`（成功、IPv4 占位）→ 透传后续字节。
- UDP ASSOCIATE 复用现有 `LoopbackSocks5UdpServer`（如缺 TCP 侧所需的绑定端口回报逻辑则小补）。

保真度是第一风险：字节序列必须符合 RFC 1928，否则测到的不是产品路径（`Socks5ControlConnection` 对响应格式有校验）。

### M2 Perf/FrameRewriterBenchmarks（R1.1）

- 预构 Ethernet 帧（SYN 与 mid-flow data 两形态；128/1400 字节参数，沿用 CapturePump 惯例）。
- 基准方法：`TryRewriteForwardLeg`（forwarded DNAT shape + host shape 两种 association）、`ClassifyTcpSyn`、`SwapEthernetMacs`。
- `[MemoryDiagnoser]` 直接暴露 `SwapEthernetMacs` 的 `new byte[6]` 每包分配（TcpFrameRewriter.cs:55 已实锤）。

### M3 Perf/Socks5HandshakeBenchmarks（R1.2，建连仅基准化）

- 对 M1 假服务器跑 `Socks5ControlConnection.ConnectAsync` + `ConnectDestinationAsync` 完整握手。
- 参数：无认证（auth 字段 null）。预期以回环 RTT 为主，BDN 数字是"每连接固定成本"，供后续任务做优化对照。

### M4 DispatcherBenchmarks 扩展（R1.3）

- 新增 `WarmProxyDisabledTraceAsync`：`PolicySnapshot` 一条命中规则 → `FlowAction.Proxy` + server 字典含目标 `Socks5Server`，executor 复用 CountingExecutor 的 no-op `ProxyAsync`（BenchmarkShared.cs:77 已有）。
- 与现有 `WarmPassDisabledTraceAsync` 同构，数字可直接对比 pass vs proxy 的分发差。

### M5 UdpSessionBenchmarks 改造（R1.4）

- transport factory 从 Noop 换成真实路径：复用 `Socks5UdpTransport` + `LoopbackSocks5UdpServer`（真实 UDP ASSOCIATE 握手）。
- 基准语义改为 populate 后 **await 所有 slot ready**（当前只测入队；`TrySendAsync` 冷路径是 fire-and-forget 的 `Task.Run` setup，UdpProxyCoordinator.cs:118）。
- 保留冷路径（新会话）测量；超线性（100→1000 会话 29x）与 ~5.2KB/会话分配的调查结论写进基线报告。

### M6 Stability/TcpThroughputScenario（R2.1）

- 新 soak scenario `tcp.throughput`：N worker 循环 { `TcpProxyRelayFactory.EstablishAsync` 全握手（经 M1）→ relay 传输 16MB → 校验字节数 }；沿用 `TcpEofScenario` 的 socket-pair/counter 骨架与 `#pragma warning disable CA1416` 惯例。
- `SoakScenario` 枚举 + `SoakRunner.SelectScenarios` 注册；输出 MB/s 与传输成功率。
- 对照锚点：同参数裸 relay（不经 SOCKS5）数字来自 TcpRelay OneWayAsync，验收的 70% 以此为分母。

## 优化设计（以基线为准，先验假设如下）

| 假设热点 | 证据 | 预期手法 |
|---|---|---|
| `SwapEthernetMacs` 每包 6B 分配 | TcpFrameRewriter.cs:55 `new byte[6]` | `stackalloc byte[6]` / `Span` 交换，零分配 |
| FrameRewriter 其他分配 | 待 M2 基线 | 逐项消除至 Gen0=0 |
| UDP 冷路径 ~5.2KB/会话 | UdpSession 基线 5.23KB | 拆解 slot/SetupQueue/Task/TCS/clientMac 数组分配 |
| UDP populate 超线性 29x | 100→1000 会话 29x，Gen2=125 | 疑 GC + `Task.Run` 调度 + `TrackFailedSetup` 结构；测量定位后决定（合并 setup 任务/预分配/削减持有） |
| relay 数据面 per-copy 分配 | 待 M6 对照 | 若 TcpProxyRelay 拷贝路径有分配则池化 |

明确不做：建连路径硬性优化（用户决策）；pass/block 优化；NDIS 注入路径基准化。

## 数据流与契约

- 无配置 schema、wire format、ABI、trace event 变更。
- `hot-path.md` 将新增 SOCKS5 段契约（数据面零分配、会话建立分配上限）。
- 基线产物：`benchmarks/results/2026-08-29-socks5-perf/`（优化前/后两份，沿用 udp-fix 惯例）。

## 兼容性与回滚

- 全部产品改动为内部实现，386 测试基线守护行为不变。
- 每阶段独立 commit：A 测量基建 → B 基线 → C 优化（C1/C2/C3 各自可 revert）→ D 复测固化。回滚 = revert 对应 commit，基准基建可独立保留。

## 风险

| 风险 | 缓解 |
|---|---|
| 假 SOCKS5 服务器保真度 | 按 RFC 1928 编写；`Socks5ControlConnection` 校验失败会在基准里立刻抛错暴露 |
| BDN ShortRun 噪声（现有矩阵 Error 栏偏大） | 结论以数量级与分配为准，不追小百分比；沿用 ShortRun 保证矩阵可跑完 |
| M5 等 ready 把基准时间拉长 | 异步基准 + Sessions 参数缩到 1/100/1000；quick 模式覆盖 |
| 优化触碰 UdpProxyCoordinator（并发敏感，503 行） | 改动最小化；全量测试 + udp.lossRate soak 回归 + 实机冲烟 |
