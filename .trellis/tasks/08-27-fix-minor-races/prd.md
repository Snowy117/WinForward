# 次要竞态修复（RST 序号、句柄时效、轮询延迟）

## Goal

修复审计发现的 D 组次要竞态与体验缺陷：注入 RST 的确认序号过期、forwarded 流
reverse 注入依赖过期的适配器句柄、SOCKS5 30s 失败的用户体验。它们单独影响较小，
但会放大前三个主因造成的 reset 感知频率。

## 成因分析

### D1. RST 确认序号过期

- `TcpProxyCoordinator.TryInjectClientResetAsync`
  （`src/WinForward.Runtime/TcpProxyCoordinator.cs:629-649`）使用
  `clientInitialSeq + 1` 作为 RST 的 ack 序号，仅在建连初期有效。
- relay 建立失败若发生在客户端已发送数据之后（例如 accept 完成但 SOCKS5 握手慢，
  客户端已经发了请求体），ack 落后于客户端实际发送窗口 → RST 被客户端栈判定
  out-of-window 丢弃 → 客户端挂起至自身超时，表现为"慢 EOF"而非快速失败。
- `TcpResetBuilder`（`src/WinForward.Protocols/TcpResetBuilder.cs`）本身构造正确，
  问题仅在序号来源未随连接推进。

### D2. OriginAdapterHandle 时效性

- forwarded 流的 reverse 注入使用 claim 时记录的 `OriginAdapterHandle`
  （`TcpProxyCoordinator.cs:410-416`）。Hyper-V vSwitch / 虚拟适配器重建后旧句柄
  失效，注入失败 → `FailAssociationAsync` → 整条活跃连接被拆除（Blocked），客户端
  被 reset。
- 适配器 Generation 概念已存在（`AdapterContext`），但 reverse 注入路径未做句柄
  有效性校验或重解析。

### D3. SOCKS5 失败的 30 秒窗口

- `Socks5Client.ConnectAttemptTimeout = 30s`
  （`src/WinForward.Runtime/Socks5Client.cs:16`）：DNS 解析 + 逐地址串行连接总预算
  30 秒。
- redirect listener 先于 relay 完成握手（客户端在数十毫秒内看到 established），
  SOCKS5 失败最长 30s 后才注入 RST → 用户感知"连接成功 30 秒后被 reset"。
  smoke 日志中 `Unable to connect to the configured SOCKS5 server` 说明该路径真实
  发生。
- 该窗口内客户端发送的数据被 relay 丢弃（尚无上游），也贡献 EOF 感知。

### D4. 1ms 轮询

- `NdisCapturePump` 空队列时 `Task.Delay(1ms)` 轮询
  （`src/WinForward.NdisApi/NdisCapture.cs:42`）。交互式流（SSH/游戏）每个方向
  切换的首包平均多 0~1ms+ 定时器粒度延迟；长时运行 CPU 占用偏高。此项若由
  `fix-datapath-throughput` 的批量读取/事件化改造顺带解决，则在本任务中关闭。

## 解决方案方向（Requirements）

- **D1**：RST 构造前获取客户端当前发送序号——可用途径：association 持续记录
  forward 方向数据包的最大 `seq + payload length`（改写路径已有解析点），或按
  RFC 793/5961 使用 0 窗口探测式 RST（`seq = server_next`，ack 省略）。方案取舍
  在 design.md 定稿；最低要求：客户端已发数据场景下 RST 仍在窗口内。
- **D2**：reverse 注入前校验 `OriginAdapterHandle` 对应适配器仍为当前 Generation
  （复用 `CaptureAdapterScopeResolver`/驱动枚举），失效时重解析或走显式拆除+
  客户端 RST，而不是注入失败后才被动 fail。
- **D3**：缩短 SOCKS5 建立对 redirect 流的可感知窗口：候选包括更短的 connect
  预算（区分"首发失败快速 RST"与"多地址尝试"）、失败后立即注入 RST（现状即如此
  但预算过长）、或在 design.md 评估 connect 失败前置到 accept 之前（与
  `fix-port-budget` 的边界衔接，避免两任务改同一段代码）。
- **D4**：跟踪 `fix-datapath-throughput` 进展；若其完成品已消除轮询问题则本项验收
  直接引用其结论。
- 每项修复独立可回滚；不改配置语义。

## Acceptance Criteria

- [x] D1：单测覆盖"客户端已发送数据后注入 RST"场景，RST ack 序号在客户端当前
      窗口内（或采用 design.md 定稿的替代构造并被单测验证）。
- [x] D2：适配器句柄失效场景的测试或仿真：连接被显式、可观测地拆除（日志含
      reason），不出现注入异常被动 fail。
- [x] D3：SOCKS5 不可达时客户端感知失败的时间从最长 30s 收敛到 design.md 定稿的
      目标值（10s×2，refused 仍亚秒级），且有相应测试。
- [x] D4：结论被记录（design.md §4：1ms 空批轮询保留，空闲单核 <0.5%；事件化
      维持 fix-datapath-throughput design §7 的否决结论）。
- [x] `dotnet test -c Release` 全量通过（353/353）。

## Notes

- 轻量偏中等复杂度：如四项均落地，仍建议补简版 `design.md`（每项方案取舍）；
  `implement.md` 可与 design 合并为单文档，由激活前的 review 决定。
- 与其他子任务的边界已在各 PRD 中标注，冲突时以父任务集成验收为准。
