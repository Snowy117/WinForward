# Research: TCP 重定向端口预算与连接上限保护 — 可行性研究

- **Query**: 端口预算/容量保护设计前置研究（PRD: `.trellis/tasks/08-27-fix-port-budget/prd.md`）
- **Scope**: mixed（源码 + Microsoft 公开文档）
- **Date**: 2026-08-28

## 1. TcpRedirectTable 索引语义

- 三索引单锁（`src/WinForward.Runtime/TcpRedirectTable.cs:84-87`），capacity 默认 16_384（`:91`），加入仅在 `TryClaim`（`:137-139`），移除在 `TryRemove`（`:185-189`）与 `RemoveExpired`（`:193-206`）。
- `_byOriginal`: key = 原始 FlowKey（客户端原始四元组）。
- `_byTranslatedListener`: key = listener 的 Endpoint（wildcard 地址 + 临时端口，来自 `TcpRedirectListener.cs:31-32`）；`TryClaim` 强制一个 tuple 只属一个流（`TcpRedirectTable.cs:125`）。注意 `TryResolveByTranslated`（`:145`）**无任何调用方**（死代码/预留）。
- `_byReverse`: key = `ReverseRedirectTuple(Source, Dest)`（`:227`）。host 形态 Source = 客户端IP:listenerPort、Dest = 服务器IP:客户端原端口（`:33-34`）；forwarded DNAT 形态 Source = 适配器本地IP:listenerPort、Dest = 客户端IP:客户端原端口（`:41-42`）。
- 结论：listener 端口嵌在 byTranslatedListener key 与 byReverse Source 中；共享端口时 byReverse 仍可按 Dest 唯一定位，但 byTranslatedListener 的 1:1 唯一性检查（`:125`）是共享化的硬阻塞点。

## 2. 每流一 listener 的耦合面（共享单 listener 需改什么）

- 消费点：listener 创建 `TcpProxyCoordinator.cs:140`；translatedTuple → TryClaim(`:166`)、selfTraffic 注册(`:269`)、SYN 端口改写(`:231`,`:300-309`)、mid-flow 重注入(`:378`)。
- accept 绑定：`setup.Session.AcceptLoop = RunAcceptLoopAsync(...)`（`:117`）；循环内 `session.Listener.AcceptAsync`（`:702`）、单一期望 peer 校验 `accepted.RemoteEndPoint != AcceptedPeerEndpoint`（`:724`，AcceptedPeerEndpoint 定义 `TcpRedirectTable.cs:44`）、relay 建立后转 `DrainRedundantConnectionsAsync`（`:739`,`:755-785`）继续 accept 并关闭多余连接直到 listener 被 dispose。
- 关闭顺序：TearDown → Retire（取消 token）→ Release（`table.TryRemove` + `listener.DisposeAsync` + selfTraffic 释放，`:823-831`,`:849-867`,`:912-924`）；listener 关闭正是 drain 循环退出机制；DisposeAsync 逐个 await AcceptLoop（`:640-645`）。
- reverse hook：`HandleReverseIfApplicableAsync` 仅做 TCP 门 + `IsReverseCandidate(local,remote)` 前置过滤（`:482-495`），端口参与 key 但无需改。
- 共享化冲突：① session 状态机（每 session 持 listener/CTC/token/单 relay，`:955-975`,`:869-878`）与"一个 accept loop 服务多流"冲突，listener 需独立 owner+引用计数；② `SelfTrafficRegistry.Register` 同 key 覆盖、last-generation-wins（`SelfTrafficRegistry.cs:18`,`:41`），共享端口下会提前解除保护，需引用计数；③ 表 `:125` 唯一性检查需放宽；④ 共享 accept loop 故障波及全部流，违背现有 per-session fail-closed 边界。
- 结论：技术可行但触及 accept 生命周期核心，改动面大、风险高 → 本任务不做，采用"预算 + 快速失败"。

## 3. 端口消耗清单

- 每流 2 个临时端口：listener `Bind(Any, 0)`（`TcpRedirectListener.cs:22`）+ SOCKS5 control 出站 `Bind(Any,0)+Connect`（`Socks5Client.cs:122-128`，经 `TcpProxyRelay.cs:27` 调用）。
- TIME_WAIT 归属：listener 腿关闭后 TIME_WAIT 落在 (clientIP:clientPort ↔ hostIP:listenerPort) 四元组上，本机 accepted socket 主动关时占住该四元组 ~2MSL；control 腿 TIME_WAIT 落在 (本机IP:临时端口 → proxyIP:proxyPort)，同一 proxy 复用该端口被阻断 ~2MSL（出站腿是经典耗尽源）。PRD 的"listener 侧占端口 240s"为保守建模（wildcard bind(0) 理论上可重发已被 TIME_WAIT 四元组占用的端口，见 Winsock 文档"closed socket 未必立即可重用"的讨论）。
- Windows 动态端口范围：默认 49152–65535（=16,384/传输/族，IPv4/IPv6 各自独立），Vista/2008+ 默认（KB 929851）；查询 `netsh int ipv4 show dynamicport tcp`，设置 `netsh int <ipv4|ipv6> set dynamic <tcp|udp> start=N num=R`（min 范围 255）。
- bind(0.0.0.0:0) 与 connect 出站**同一池**：均为 per-transport 动态端口范围（Winsock 文档明确 ephemeral = >49151 且客户端应 bind(0)）。TIME_WAIT 时长：RFC 793 2MSL=240s（legacy 默认）；现代 Windows 广泛报道固定 ~120s（注册表键 Vista+ 失效）——未能对官方文档验证（检索工具故障），模型按参数 T_tw∈{120,240} 处理。

## 4. 配置扩展点（顶层加可选字段触达清单）

- `WinForwardConfigDto` 加属性（`src/WinForward.Configuration/ConfigurationModels.cs:8-27`）；源生成上下文 `UnmappedMemberHandling.Disallow`（`:51-53`）意味着 DTO 属性即白名单。
- `TryValidate` 解析/钳制/诊断（`:112-162`，诊断路径如 `maxConcurrentProxyFlows`）；`ValidatedConfiguration` record 加字段（`:76-80`，尾部可选参数不破坏 benchmarks 的既有构造 `benchmarks/WinForward.Benchmarks/Program.cs:272,372`）。
- 接线：`src/WinForward.Cli/Program.cs:234-241`（现为默认 capacity，需改为传入配置值 → `TcpRedirectTable` + `TcpProxyCoordinator`）。
- validate 输出：`Program.cs:336-356` 仅打印 diagnostics，容量一致性提示需由 TryValidate 产出。
- README 契约：JSON 样例 `README.md:54-77`、字段说明 `:97-111`、"unknown property 拒绝"承诺 `:52`、Notes `:158-168`。
- 测试：`tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs`（loader/validator）；`tests/WinForward.Core.Tests/TcpProxyCoordinatorTests.cs:144-145`（capacity=1 现有模式）。`.trellis/spec/` 为空，无额外 spec 触点。

## 5. 失败路径现状

- listener bind 失败：catch 于 `TcpProxyCoordinator.cs:138-151`，确切字符串 reason=`listenerAllocation`（`:148`，trace 事件 `tcp.redirect.rejected`）+ Warn "TCP redirect failed: listener allocation failed, blocking the flow."（`:149`）。
- relay 建立失败 → RST：accept loop catch（`:747-750`）→ `HandleRelaySetupFailureAsync`（`:686-692`）：Warn、accepted.Dispose、`TryInjectClientResetAsync`（`:660-680`，`TcpResetBuilder.BuildReset` `:664`、事件 `tcp.redirect.clientReset` `:670`）、TearDown。
- 预算快速失败最小改动面：capacity gate 已存在且位置正确（`:98-105`，在 listener 分配 `:107` 之前）——只需把数据源换成配置预算 + 补 info 级计数摘要（现仅 trace `reason=capacity`，`:102`）。`HandleRelaySetupFailureAsync` 本身无需为预算改动（边界归 fix-minor-races）。

## 6. 容量现状

- coordinator 会话容量 16_384：ctor 默认参数 `TcpProxyCoordinator.cs:46`，超限行为 = 静默 Blocked + trace 事件（`:100-104`），无 Warn/计数。
- TcpRedirectTable 容量 16_384：`TcpRedirectTable.cs:91`，超限 TryClaim 拒绝 → coordinator 记 `reason=claim`（`TcpProxyCoordinator.cs:166-171`）。
- 接线处两者均用默认值（`Program.cs:234-241`），互不联动，也未与端口池挂钩；相邻容量：FlowDispatcher flowCapacity 65_536（`FlowDispatcher.cs:50`）。半开 Redirecting 会话由 `RemoveExpiredAsync` 回收（`:570-586`，sweep 默认 1min/5min idle，`IdleExpirySweeper.cs:37-40`）。

## 推荐预算模型草案

- 默认并发流上限 N = floor(P × h / 2)，P=动态端口池大小（默认 16,384），h=50% 余量系数（预留 TIME_WAIT churn + 其他进程），2 = 端口/流 → **默认 4,096**。
- 配置字段：`tcpFlowCapacity`（可选，默认 4096，范围 1..8192）；validate 对 >4,096 给 warning、>8,192（2N ≥ P，零余量）拒绝；coordinator `_capacity` 与 `TcpRedirectTable` capacity 均由该字段派生（单一事实来源）。
- 超限语义：沿用 `tcp.redirect.rejected reason=capacity` + Blocked（gate 位于 `TcpProxyCoordinator.cs:98-105`），补 info 摘要计数；预算生效后 `reason=listenerAllocation` 对端口耗尽归零（PRD 验收）。
- 共享 listener / control 连接池：评估为高风险大改（见 §2），不作为本任务方案。

## Caveats / Not Found

- 现代 Windows TIME_WAIT 确切值（120s vs 240s）未获官方文档证实（web 检索工具本会话故障）；KB 929851 与 Winsock SO_REUSEADDR 文档已核实（链接见上）。
- `TcpRedirectTable.TryResolveByTranslated`（`:145`）无调用方，属预留 API。
- External References: [KB 929851 dynamic port range](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/default-dynamic-port-range-tcpip-chang)、[Winsock SO_REUSEADDR/SO_EXCLUSIVEADDRUSE](https://learn.microsoft.com/en-us/windows/win32/winsock/using-so-reuseaddr-and-so-exclusiveaddruse)、RFC 793 §3.5（2MSL）。
