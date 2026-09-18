# 实施计划：适配器视图自愈 + 运行时诊断日志

任务：09-17-adapter-staleness-logging ｜ 设计依据：design.md

## 执行顺序（每步独立可验证，完成后运行对应验证命令）

### 阶段 A：R2.1/R2.2 既有失败点日志增强（纯取证，无行为变化）

- [x] A1 新增 `RuntimeLogThrottle`（key+窗口节流）与 `RuntimeCounters`（Interlocked 计数封装），放 `WinForward.Runtime`（或 Core，按现有分层惯例）+ 单测
- [x] A2 `ClientResetInjector.HandleRelaySetupFailureAsync`：warn 携带异常类型/SocketError/上游 endpoint/尝试数（需把异常从 acceptor 传入——`TryEstablishRelayAsync` catch 处包装）
- [x] A3 `TcpRedirectAcceptor` unrelated peer：携带 listener/expected/actual endpoint
- [x] A4 `UdpResponseReinjector`：两处 warn 改结构化事件 `udp.reinject.unresolved` / `udp.reinject.drop`（flowKey、map 摘要）+ 节流 + 计数
- [x] A5 `FlowDispatcher` capacity block（Trace → `flow.capacity-block` warn 节流）+ 归因失败（`flow.attribution-miss`，含 afterRetry）+ 计数
- [x] A6 `NdisPacketActionExecutor` pass 直通 native 失败 → `reinject.pass-failed` warn 节流 + 计数
- 验证：`dotnet build && dotnet test`；相关既有测试断言文案处适配

### 阶段 B：R1-A 周期重枚举 + 地址指纹

- [x] B1 `WindowsAdapterInventory`/WinForward.Windows 新增单播地址表查询（iphlpapi `GetUnicastIpAddressTable`，ABI 放 `IPHelperAbi`；按适配器归组、排除 fe80::/10）+ 单测（表解析/分组/link-local 过滤）
- [x] B2 `AdapterEnumerationItem` 增加 `AddressFingerprint`；`NdisAdapterEnumerationProvider.Enumerate` 填充；`AdapterEnumerationDiff.LinkStateEquals` 比较
- [x] B3 `LayeredCaptureRunner` 新增 `periodicRefreshInterval`（默认 30s，0 禁用）：`RunAsync` 起 `PeriodicTimer` → `_demandGate.Signal()`，finally 释放
- [x] B4 测试：地址指纹变化 → diff changed → rebuild；指纹不变 → no-op 保持；周期 tick 与 NDISRD 事件并发只处理一次（storm-guard 吸收）
- 验证：`dotnet test tests/WinForward.Core.Tests`（LayeredCaptureRunner*、AdapterScopeDiff*）；全量 build

### 阶段 C：R1-B 失败率 forced refresh

- [x] C1 `IInterceptionHealthSignal` 接口 + `InterceptionHealthMonitor`（30s 滑窗、默认阈值、60s 冷却、连续 3 次后降频 5 分钟 + error）+ 单测
- [x] C2 `LayeredCaptureRunner` 实现/持有 monitor：对外暴露 `IInterceptionHealthSignal`；触发时 arm `_forceRebuild` + signal + `runner.forcedRefresh` warn
- [x] C3 四个信号源接线（design §3.2 表）：`UdpResponseReinjector`×2、`ClientResetInjector`/acceptor 链、`NdisPacketActionExecutor`；`DurableCaptureBundle.CreateAsync` + `Program.cs` 注入（可空）
- [x] C4 测试：阈值触发 forced rebuild（fake signal 计数推进）；冷却期内不重复触发；降频路径；forced rebuild 后计数重置
- 验证：`dotnet test`；`dotnet build` 无警告

### 阶段 D：心跳 + 降噪收尾

- [x] D1 `RuntimeHeartbeat`：60s `PeriodicTimer`，聚合 `RuntimeCounters` + runner 泵快照 + 会话/容量，输出 `runner.heartbeat` info + 单测（fake timer）
- [x] D2 `DurableCaptureBundle.UpdateUdpTargets` no-MAC warn 首次+集合变化输出 + 单测
- [x] D3 `Program.cs` 组装全部新件；`README.md` 日志事件表补充新事件（若有文档段落）
- 验证：全量 `dotnet build && dotnet test`

### 阶段 E：收口

- [x] E1 全量质量检查（lint/build/test；对照 PRD A1–A5 逐条核验）
- [x] E2 用 2026-09-17 事故日志口径走查 A5：新日志能否读出「失效环节 + 触发信号 + 恢复动作」
- [x] E3 spec 更新（trellis-update-spec：诊断日志惯例、地址指纹 diff 契约）+ 提交

## 回滚点

- A/D：纯日志，可整体 revert
- B：`periodicRefreshInterval=0` 配置禁用；指纹比较单独 revert 需同步 revert B2
- C：阈值不配置即禁用；接口可空注入

## 风险跟踪

- B2 改 no-op 判定语义 → 相关旧测试（DegradedError87WithUnchanged…）需确认 fake 枚举指纹一致（已确认：默认空指纹两侧相等，no-op 保持；隔离运行 11 次全绿）
- C3 改 `DurableCaptureBundle.CreateAsync` 签名 → 调用方（Program.cs、测试）同步
- **测试套件间歇性 flake（2026-09-17 Phase B 验证时发现，属 Phase A/既有余量问题，非 Phase B 引入）**：全套并行运行时多个既有测试（`RuntimeDiagnosticLoggingTests.UnrelatedPeerWarnCarriesEndpoints` 隔离复现 5/6 失败、DegradedError87WithUnchanged…、IdleExpirySweeper、UdpSetupQueue 等）在 2s `WaitForAsync` 上限时超时，偶发 testhost 整体 hang；pristine baseline 3×全绿，排除 Phase B 周期测试后仍复现 → 怀疑 Phase A 测试加剧宿主机负载 + 既有 1s guard/2s timeout 余量过窄。E1 收口前需处理（修 UnrelatedPeerWarnCarriesEndpoints 或放宽余量）。
