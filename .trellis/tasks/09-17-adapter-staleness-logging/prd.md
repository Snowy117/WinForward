# PRD: 适配器视图过期导致的静默断网修复 + 运行时日志可观测性改进

## 背景（2026-09-17 20:02–20:08 断网事故档案）

- 症状：所有经代理的流量中断约两分钟以上；**重启 WinForward 立即恢复**。上游为同机 sing-box（mixed 127.0.0.1:30890）。
- 证据链（WinForward 日志 + smoke/sing-box-service_2026-09-17.err.log 交叉分析）：
  - sing-box 全程健康：每分钟正常接受 100+ loopback inbound；WinForward 的 redirect → relay → SOCKS5 握手链路正常（sing-box 侧能看到 fake-ip 目标的完整 CONNECT）。
  - 唯一失效环节：**sing-box 到代理节点的 IPv6 出站 SYN 黑洞**（sing-box 侧 `dial tcp [2406:da18::/…]:443: i/o timeout 5.3s`；WinForward 侧对应 `TCP redirect relay setup failed` ×11，时间逐条对齐）。
  - `20:02:27 "Host UDP response origin adapter is not resolved in the reinjection map"` —— 距上次出现 115 分钟，表明主机网络状态在 WinForward 视图之外发生了变化（头号嫌疑：IPv6 临时隐私地址轮换；本机 v6 为电信 240c:c001 段）。
  - 最后一次 `adapter.refresh` 在 18:04:47；此后零触发。`NdisAdapterListWatcher` 只感知 NDISRD 绑定适配器列表重建，**感知不到地址增删/轮换等链路状态变化**，适配器视图（句柄/MAC/再注入映射）从 18:04:47 起永久 stale，直到进程重启。
  - 18:04 的以太网 degraded(error 87) 与 refresh 重建管线按设计工作，与本事故无关（78 分钟间隔）。
- 诊断受阻：真正关键的失败路径全部无日志或仅 Trace/Debug 级（见 R2 清单），现有 warn 多为无害噪音（UDP no MAC 每次 refresh 刷屏）。

## 需求

### R1 适配器视图自愈（根因修复）

R1.1 WinForward 必须能检测到「适配器绑定列表未重建，但主机网络链路状态已变化」的场景，并自动刷新适配器视图（重新枚举 → diff → 必要时按现有 refresh 管线重建 generation）。
- 已知检测信号（实现取其一或组合，设计文档定夺）：
  - a) 周期性重枚举 diff（低频、storm-guard 保护）
  - b) 再注入解析失败 / pass 直通失败的频率阈值
  - c) relay setup 连续失败率
R1.2 恢复动作复用现有 `LayeredCaptureRunner` refresh 管线（含 storm-guard 与 no-op 判定），不得绕过 mode 快照/恢复事务语义。
R1.3 不引入重建风暴：误报信号不得造成高频 generation 重建（沿用/扩展现有防抖）。
R1.4 视图外变化的最小覆盖集：IPv6 临时地址轮换、地址增删、MAC 变化、MTU 变化（后两者现有 diff 已覆盖，随重枚举自然生效）。

### R2 日志可观测性（事故取证增强）

R2.1 静默失败点补日志（warn 级、带节流/变化时输出）：
  - flow 表 capacity 满导致的 Block（现仅 Trace，`FlowDispatcher.cs:214` 附近）
  - 进程归因失败（`WindowsProcessAttributor.FindAsync` 返回 null 的慢路径结果，含重试后仍失败）
  - pass 直通再注入失败（现被吞或无独立事件）
  - UDP 再注入 fallback / fail-closed drop（保留但节流，附累计计数）
R2.2 既有 warn 增加诊断字段：
  - `TCP redirect relay setup failed`：异常类型、SocketError/Win32 错误码、上游 endpoint、尝试次数（`ClientResetInjector.cs:137`）
  - `TCP redirect accepted an unrelated peer`：listener endpoint、期望 peer、实际 peer
  - `Host UDP response origin adapter is not resolved`：流 key、当前 map 的适配器集摘要
R2.3 info 级增加周期性运行摘要（心跳）：活跃 flow 数、TCP 会话数/容量、UDP 会话数、每适配器泵状态（running/degraded）、自上次摘要以来的关键计数（redirect 次数、relay 失败数、reinject fallback 数）。周期默认 60s。
R2.4 降噪：UDP no MAC 类每次 refresh 重复输出的 warn 收敛为「首次 + 集合变化时」输出。
R2.5 日志字段遵循现有 `RuntimeLogField` 结构化格式；新增事件命名沿用 `dot.namespace` 惯例。

## 非目标

- 不改动 NDISRD 驱动 / ndisapi.dll。
- 不实现 IPv6 地址变化的内核级订阅（NotifyIpInterfaceChange 等）除非设计确认必要且代价可接受。
- 不调整策略规则语义。

## 验收标准

- A1 模拟「绑定列表不变但链路状态变化」（单测：fake 枚举返回变化后的地址/MAC）能触发 refresh 管线并重建视图。
- A2 误报场景（无实际变化的重枚举）不产生 generation 重建（no-op 判定保持），风暴上限可配置并有测试。
- A3 R2.1–R2.4 每一项在对应单测/集成测试中可断言（事件名、级别、关键字段存在）。
- A4 现有测试套件全绿；`dotnet build` 无新警告。
- A5 用本次事故日志回放口径验证：按新日志规范，同类事故可从日志直接读出「失效环节 + 触发信号 + 恢复动作」三要素。

## 任务形态

复杂任务：需要 `design.md`（检测信号与恢复机制取舍、日志事件清单与节流策略）与 `implement.md`（分步执行清单）。
