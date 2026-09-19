# PRD: TCP 时钟收口与设计健康后续修补

Origin: `09-19-design-deepening-refactors` 收尾评审列出的 5 项遗留（2026-09-19 用户确认全部修补）。
所有项均为行为零变更；验收沿用该任务的测试门槛（基线 724）。

## Goal

收口 TCP 家族及其邻居的时钟注入留白（R8 当时只圈了 `FlowTable`/`UdpSetupQueueBudget`），
并清理 4 项结构性遗留，使 09-19 深模块化工作的边界完整、可测面一致。

## Requirements

### F1 — TCP 家族时钟收口（TimeProvider 注入）

当前 TCP 家族有 ~17 处裸 `DateTimeOffset.UtcNow`，使墓碑宽限、setup 冷却、容量重置冷却、
pending-SYN TTL 的边界不可确定性测试：

- `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` 12 处：L129、L140、L146、L184、
  L232、L264、L306、L392、L501、L522（已是局部变量，可复用一次读取）、L573、L605。
- `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs:329`（墓碑写入 = now + 60 s 宽限）。
- `src/WinForward.Runtime/TcpRedirect/TcpRedirectSetup.cs:90`（`TryClaim` 的 now）。
- `src/WinForward.Runtime/TcpRedirect/ClientResetInjector.cs:103`（容量重置冷却的 now）。
- `src/WinForward.Runtime/IdleExpirySweeper.cs:64`（sweep `now`，喂给 TCP/UDP 过期与
  `RemoveExpiredFlows`）与 `:105`（丢包摘要日志节流 ticks，镜像 `UdpSetupQueueBudget` 的处理）。

设计（沿用 P1 范式）：
- `TcpRedirectOptions` 增加 `public TimeProvider TimeProvider { get; init; } = TimeProvider.System;`
  （镜像 `UdpProxyOptions.TimeProvider`，XML 文档同风格标注"可注入假时钟测试"）。
- `TcpProxyCoordinator` 持有 `_timeProvider`，构造时把同一实例传给 `TcpRedirectSessionStore`、
  `TcpRedirectSetup`、`ClientResetInjector`（各自 ctor 新增参数；均在协调器 ctor 内单点构造）。
- `IdleExpirySweeper` 增加可选 `TimeProvider? timeProvider = null`（null → System），两处读取改用它。
- 新增强性测试：`MutableTimeProvider` 经 `TcpRedirectOptions` 注入 → 墓碑宽限边界确定性断言
  （到期前 `HoldsFlow` true、到期后 false），证明 `TryHit` 消费注入时钟。
- 行为零：默认 System 下语义不变；异常类型不变。

Out of scope（记录为已知留白）：`NdisPacketActionExecutor.cs:538` 的 `DateTime.UtcNow.Ticks`
（限频告警日志节流，非过期语义，无测试依赖）。

### F2 — `TcpRedirectSession` 独立成文件

`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` 尾部顶层类 `TcpRedirectSession`
（:656 起，~22 行）移到新文件 `TcpRedirectSession.cs`（同命名空间，零引用变更）。
类文档更新：保留"顶层类型（非嵌套）以避免 acceptor/reset/relay 模块循环依赖"的理由，
删除"留在本文件以精简文件数"的过时理由（文件=主类型约定优先）。

### F3 — `_receiveFailureHandler!` 收紧

`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:267` 的 `_receiveFailureHandler!(this)`
是 Start() 约定式非空断言（R1 同类问题）。修法：handler 作为 `ReceiveLoopAsync` 的**参数**
传入（由 `Start(handler)` 传递，null 检查已在 `Start`），删除可空字段与 `!`。

### F4 — `SetupWorkItem` 字段平面分区

`src/WinForward.Runtime/SetupExecutor.cs` 的 `SetupWorkItem` 把 TCP/UDP 两套互斥载荷压在
同一字段平面。分区：嵌套 `TcpSetupWork`（Entry、Frame）与 `UdpSetupWork`（FlowGeneration、
ClientMac、Slot）两个**随 item 一次性分配**的随行对象（租借/回收路径仍零分配）；共享字段
（Handler、Completion、Flow、Server、CancellationToken）留在 item；`Reset()` 委托两个分区各自
重置。两个协调器只触碰各自分区。

### F5 — `UdpSetupQueueTests.cs` 拆分

`tests/WinForward.Core.Tests/UdpSetupQueueTests.cs` 现 512 有效行（>400）。按主题机械拆分到
≤400 有效行/文件（先读文件找自然接缝：冷却/预算/TTL/冲刷/容量各自成组）；断言语义一字不改；
被 ≥2 个拆分文件共用的 helper 提升到 `TestHelpers/`（按 directory-structure.md 规则）。

## Constraints

- 行为零变更：零警告构建；全量测试 == 724 + F1 新增强性测试数；分配门禁（HotPathAllocationGate）
  与 gc-soak 契约不变；测试搬移不得改断言语义（quality-guidelines.md）。
- 文件 ≤400 有效行；文件名 = 主类型名。
- 全部改动在仓库内，无外部消费者。

## Acceptance criteria

- AC1：F1–F5 每项有实现记录（本 PRD 即清单；实现报告记录改动与证据）。
- AC2：构建零警告；全量测试绿（预期 725 = 724 + 1 个 F1 边界测试）；分配门禁与 gc-soak 干净。
- AC3：F5 拆分后每个受影响测试文件 ≤400 有效行，断言零改动；F2 移动后零引用变更。

## Open questions

None（5 项范围与修法在 2026-09-19 评审中已逐项确认）。
