# PRD: SOCKS5 full-path benchmarks and performance

## Goal

WinForward 的核心价值是把捕获的流量经 SOCKS5 代理转发（全链路），pass/block 只是实现这一功能过程中的附属能力（用户重申的产品定位，2026-08-29）。

当前基准与优化历史集中在公共管线（pass 视角）与 UDP 稳定性上；SOCKS5 专属段——TCP 重定向握手、每包帧重写、SOCKS5 控制连接、UDP 会话建立——既没有被基准覆盖，也没有被优化过。

本任务补齐 SOCKS5 全链路的基准与稳定性覆盖，并基于测量结果做性能优化，使"转发到 SOCKS5"这条主路径的性能可测、可证、可持有。

## Background（已确认事实，含证据锚点）

### 基准现状（BenchmarkDotNet.Artifacts/results/*-report-github.md，2026-08-29，Linux/9955HX）

| 环节 | 现有基准 | 结果 |
|---|---|---|
| 公共管线（Pass 端到端） | CapturePump EndToEndAsync | 2.9–3.2M pps，9.3B/包 |
| Dispatcher | 仅 `WarmPassDisabledTraceAsync`（Pass 配置） | 221ns/160B |
| TCP relay | TcpRelay OneWayAsync（裸 socket pair，无 SOCKS5） | 16MB@8KB chunk ≈ 1GB/s |
| SOCKS5 UDP codec | Parser（decode/TryEncode/TryEncodeSpan） | decode 20ns；TryEncode 30–45ns 零分配 |
| UDP 会话建立 | UdpSession PopulateSessionsAsync（Noop transport） | 1 会话 220μs/5.2KB；1000 会话 24.1ms/5MB，超线性（10x 量 → 29x 时、14x 分配） |
| FlowTable / SelfTraffic / NdisBuffer | 各自微基准 | 零分配，7–250ns |

### 稳定性现状（benchmarks/WinForward.Benchmarks/Stability/）

- `udp.lossRate` 走真实 SOCKS5 UDP dial 路径（LoopbackSocks5UdpServer.cs），但指标是丢包率，无吞吐/延迟。
- `tcp.unexpectedEof` 测 EOF 鲁棒性，无性能指标。
- 无 SOCKS5 TCP 吞吐 soak。

### 代码事实（决定基准可行性）

- `src/WinForward.Runtime/TcpRedirect/TcpFrameRewriter.cs`（91 行）：无平台标记、不依赖 NdisApi/Windows —— 纯托管，Linux 可基准。
- `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs`（349 行）：纯托管，可对回环假 SOCKS5 服务器基准握手（Stability 已有 UDP 假服务器先例）。
- `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs:22`：单 `Dictionary<FlowKey, UdpSessionSlot>` + 全局 `lock(_gate)`；populate 超线性原因待查。
- `TcpProxyCoordinator.cs` / `TcpRedirectSetup.cs` 依赖 `WinForward.Windows`（NDIS 重注入）：TCP 全链路（捕获→重定向→SOCKS5→重注入）无法在 Linux 完整复现。
- BenchmarkShared.cs:77 的 no-op `ProxyAsync` / :99 `BenchmarkUdpTransportFactory` 已存在但未被任何 perf 基准使用。

### 优化历史（journal + git log）

- `f161556`（08-28 perf-hotspots）：公共管线零分配化（669→10B/包，+44–53% pps）。所有相关基准以 Pass 配置测量。
- `d9a61ed`（08-28）/ `47735b6`（08-29）：UDP 丢包修复与准入排队化，目标是稳定性（零丢包）而非吞吐。
- TCP SOCKS5 专属段（重定向握手、帧重写、SOCKS 建连）：无优化记录、无基准记录。

## Requirements

### R1 基准补缺（BenchmarkDotNet，Linux 可运行）

- **R1.1 TcpFrameRewriter 每包重写基准**：proxy 数据面相比 pass 多出的唯一每包工作（SEQ/ACK 重写），当前零覆盖。
- **R1.2 SOCKS5 TCP 建连延迟基准**：`Socks5ControlConnection.ConnectAsync`（greeting/method/auth）+ `ConnectDestinationAsync`（CONNECT）对回环假 SOCKS5 TCP 服务器的完整握手成本。
- **R1.3 Dispatcher Proxy 分支基准**：policy 命中 → `ProxyAsync`（规则匹配、server 字典查找）路径；可复用 CountingExecutor 模式。
- **R1.4 UdpSession 基准改真实 transport**：现有 Noop 只测簿记；改走真实 SOCKS5 UDP ASSOCIATE（对回环假服务器），并调查 100→1000 会话超线性（29x）与每会话 ~5KB 分配。

### R2 稳定性补充（soak runner）

- **R2.1 SOCKS5 TCP 吞吐 soak**：经真实 SOCKS5 建连后的持续传输吞吐与稳定性（在 Linux 可测的最大子链：本地 relay + SOCKS5 握手 + 帧重写逻辑，不含 NDIS 重注入）。

### R3 性能优化（SOCKS5 主路径优先）

- **R3.1 数据面**：基于 R1.1/R2.1 热点，降低 proxy 数据面每包成本（帧重写、缓冲拷贝）。
- **R3.2 建连路径**：基于 R1.2 热点，降低每连接建立成本（握手分配、SelfTraffic 注册等）。
- **R3.3 UDP 会话路径**：基于 R1.4 发现，处理超线性 scaling 与每会话分配。

### R4 基线记录

- 优化前后基准结果落盘 `benchmarks/results/`（沿用 `2026-08-29-udp-fix/` 惯例），供回归对比。

## Acceptance Criteria

- [ ] R1.1–R1.4、R2.1 对应的新基准/soak 场景全部存在且在全量基准矩阵（`--job short` + `--stability --quick`）中绿。
- [ ] 新基准产出基线数字并记录在 `benchmarks/results/` 下（含优化前对照）。
- [ ] **R3 数据面零分配**：SOCKS5 主路径每包工作（TcpFrameRewriter 重写、UDP 会话数据面）在对应微基准中 Gen0/Alloc 归零（1 会话冷路径除外）。
- [ ] **R3 UDP populate 线性化**：100→1000 会话耗时增长 ≤ 1.5x 数量增长比——**B 阶段已达成**（真实 transport + await-ready 语义下 10.8x@10x，旧 29x 为 Noop 入队伪影）；每会话分配目标口径修正（B2 复核）：真实路径 90KB/会话中**产品簿记部分 ≤1KB**（slot/SetupQueue/Task/MAC 拷贝），框架 socket 成本不在目标内。
- [ ] **R3 吞吐比例目标**：分母口径修正（B3 复核）：`TcpThroughputScenario` 增加 bare 对照模式（同 worker/echo/transfer，relay 腿用裸 socket pair），验收 = socks5 模式 ≥ bare 模式 70%；TcpRelay OneWayAsync（0.94GB/s）仅作单连接理论上限参考。
- [ ] 建连路径（R1.2）仅基准化并产出基线，本轮不做硬性优化（用户决策 2026-08-29）。
- [ ] 全仓零警告构建 + 全量测试通过（380/386 基线之上不得回归）。
- [ ] Windows 实机冲烟通过（WinLtsc：SOCKS5 转发功能完好 + udp.lossRate 快速轮）。
- [ ] 性能契约沉淀到 `.trellis/spec/backend/hot-path.md`（SOCKS5 段新增契约，沿用 f161556 惯例）。

## Out of Scope

- pass/block 路径的进一步优化（公共管线已达标，且属附属能力）。
- Windows 实机 NDIS 重注入路径的性能基准化（TcpProxyCoordinator 全链路 Windows-only；实机仅做功能冲烟验证，见 Q2 决策）。
- SOCKS5 服务器本身的行为规格（假服务器只服务基准，不做兼容性矩阵）。

## Open Questions

（全部已决策，2026-08-29）

- **Q1 优化验收目标**：零分配 + 线性化 + 吞吐比例目标（≥裸 relay 70%，基线后确认）；建连仅基准化。已固化进 Acceptance Criteria。
- **Q2 Windows 实机验证**：做一轮 WinLtsc 实机冲烟（功能完好 + udp.lossRate 快速轮）；基准数字以 Linux BDN 为准。
- **Q3 任务结构**：单任务三阶段（测量→优化→复测固化），不拆父子。
