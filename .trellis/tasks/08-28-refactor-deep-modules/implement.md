# 执行计划

## 前置（Batch 0：基线）

- [ ] `dotnet build WinForward.slnx` 确认零警告零错误（同时核实 NdisApiDriver.cs 的 LSP 报错为缓存假象）
- [ ] `dotnet test` 记录基线：通过测试总数、警告数 → 写入本文件末尾"基线记录"
- [ ] 记录重构前行数 top 榜（已知：1812/1275/1158/789/634/570/564/508/504/493/467/448/436）

## Batch T：测试侧拆分（纯移动，零产品代码变更）

- [x] T1 创建 `tests/WinForward.Core.Tests/TestHelpers/`，按 design.md §3 表逐个提取 10 组共享 helper/fake（2026-08-28 完成：8 文件 513 行；TcpEndpointRewrite 467→331、UdpRelay 508→398 已提前达标 ≤400；`dotnet test` 353/353 == 基线；T8 的 helper 外移随本步完成）
- [x] T2 拆 `TcpProxyCoordinatorTests.cs` → 4 个主题测试文件（Lifecycle/Capacity/Rewrite/Concurrency；376/374/359/296 行，原文件已删，353/353 全绿）
- [x] T3 拆 `FlowAndConfigurationTests.cs` → 6 个主题文件（2026-08-28 完成：EndpointAndPolicy 180 / CoreFlowStructures 179 / Socks5Protocol 189 / UdpPacketParsing 221 / ConfigurationValidation 385 / ConfigurationLimits 137；共享 `AssertInvalid` 提取为 `TestHelpers/ConfigurationAssert.cs`；72 方法 == 原文件 72，`dotnet test` 353/353 == 基线）
- [x] T4 拆 `CapturePipelineTests.cs` → 3 文件 + fakes 外移（2026-08-28 完成：PacketParsing 6 / AdapterScopeAndFlowTable 8 / FlowDispatcherExecutor 19；fakes → `TestHelpers/CapturePipelineFakes.cs`，CreateIpv4/6* 帧包装 → `FrameBuilders.cs`；33 方法 == 原文件 33）
- [x] T5 拆 `UdpProxyCoordinatorTests.cs` → 2 文件（2026-08-28 完成：主文件 184 行 10 方法 / Lifecycle 279 行 12 方法；Gated/CancellationAware/CollidingAlias factory、TrackingArrayPool、MutableTimeProvider → `TestHelpers/UdpCoordinatorFakes.cs`；22 方法 == 原文件 22）
- [x] T6 拆 `Socks5ControlConnectionTests.cs` → 2 文件 + Socks5TestServer（2026-08-28 完成：Timeout 8 方法 / UdpAssociate 6 方法 + NoopResponseSink/OrderingRegistration 随迁；server helper 簇 → `TestHelpers/Socks5TestServer.cs`；IgnoreExpectedCancellationAsync 统一用 TestHelpers 版本；14 方法 == 原文件 14）
- [x] T7 拆 `UdpRelayTests.cs` — T1 后 398 行已 ≤400，主题拆分不再必要
- [x] T8 `TcpEndpointRewriteTests.cs` helper 外移（ChecksumMath/FrameBuilders）— 已在 T1 中完成（467→331 行）
- [x] T-gate：`dotnet test` 全绿且测试总数 == 基线；`wc -l` 检查 tests/ 无 >400（2026-08-28：Passed 353/353 == 基线；tests/ 最大文件 400 行 = FlowDispatcherExecutorTests）
- [ ] T-commit：`refactor(tests): split oversized test files, extract shared TestHelpers`

## Batch R：Runtime 拆分

- [x] R1 `Socks5Client.cs` → `Socks5ControlConnection.cs` + `Socks5UdpTransport.cs`（2026-08-28 完成：物理移动零逻辑改动，原 504 行删除；新文件 349 / 161 行）
- [x] R2 `UdpProxyCoordinator.cs`：`UdpProxySession` 迁出；`IUdpResponseSink` 移到 `UdpResponseReinjector.cs`；三个移除块合并为 `TryRemoveSessionAsync(flow, expected, resolvedSession)` 私有方法（2026-08-28 完成：coordinator 570→362 行、UdpProxySession.cs 201 行、UdpResponseReinjector.cs 214 行；锁内查表+Task 身份校验+association 释放、锁外 dispose 时序不变，faulted-吞异常/CancelExpiry/日志差异留在调用点；`dotnet test` 353/353 == 基线）
- [x] R3 `TcpProxyCoordinator.cs`（2026-08-28 完成：1158→357 行；除 design §1 的 5 文件外，因"stays 清单合计 ≈790 行 > 400"，追加 3 个自然接缝提取以满足 PRD ≤400 硬约束：`TcpRedirectSessionStore.cs` 303 行——单锁并发核心（sessions/disposed/disposeTask/drain/tombstone 单写点）整体迁出，所有 lock body 逐字保留；`TcpRedirectSetup.cs` 193 行——setup pipeline + RedirectSetup record + ConcurrentLoserCount；`TcpRedirectLogging.cs` 33 行——LogDebug/LogTrace 静态化并去重 acceptor 内副本。`ClientResetInjector` 104 行（吸收 HandleInjectionFailureAsync，ctor 注入 tearDown/failAssociation 回调）；`TcpRedirectAcceptor` 154 行（ctor 注入 relayFactory/logger/clientReset + tryAttachRelay/tearDown 回调）；`TcpFrameRewriter` 91 / `TcpSequenceObservation` 82（static 纯簇）/ `TcpRedirectSession` 33（嵌套类提升）。`ReinjectExistingSynAsync` 别名已删，两调用点直调 `ReinjectExistingFlowDataAsync`。coordinator 保留：入口路由、ReinjectExistingFlowDataAsync、capacity 计数、委托属性（Table/Tombstones/ConcurrentLoserCount/HoldsFlow/RemoveExpiredAsync/DisposeAsync）。`dotnet test` 353/353 == 基线）
- [x] R3a 收敛（2026-08-28 完成：`TcpRedirectSession` 并回 coordinator——coordinator 246 有效行 / 386 wc-l；`TcpRedirectLogging` **保留独立文件**，rg 复验被 4 文件 16 处调用：TcpProxyCoordinator 6 / TcpRedirectSetup 7 / TcpRedirectAcceptor 2 / TcpRedirectSessionStore 1，并回会造成跨文件反向引用；保留 6 个提取模块 FrameRewriter/SequenceObservation/ClientResetInjector/Acceptor/SessionStore/Setup；后续行数衡量统一用**有效行数（非空非注释）**）
- [x] R-gate：`dotnet test` 全绿；`wc -l` 检查 src/WinForward.Runtime 无 >400（2026-08-28：353/353；Runtime 最大 362 = UdpProxyCoordinator）
- [ ] R-commit：`refactor(runtime): split coordinators into focused modules`

## Batch N：NdisApi / Cli / Configuration 拆分（2026-08-28 缩减）

> 有效行数复核（非空非注释）：仅 NdisApiDriver(406) 超标；Program.cs(388) / ConfigurationModels(367) 已达标。用户决定：达标者不拆，保留 composition root 内聚性。

- [x] N1 `NdisApiDriver.cs`（2026-08-28 完成：死面 rg 复验均零外部调用点——`Version` 0 / `TryReadPacket` 0 / 批量 `SendPacketsToMstcp[]`/`SendPacketsToAdapter[]` 0（L237 匹配为 ABI P/Invoke 非方法调用）；删除三组公共死面 + 只服务批量 send 的私有链 `SendPacketsBatch`/`SendPacketsBatchCore`/`SendPacketsRequest`（共 -85 行）；`NdisNativeCallStatus.EnsureDriverVersion`/`InterpretReadResult` 被 `NdisApiAbiTests` 直接调用故保留；`NdisAdapter`(2 有效行)/`NdisNativeCallStatus`(37)/`NdisNativeCallGate`+嵌套 GateLease(46)/`NdisPacketBuffer`(78) 四类型迁出各成文件；残留 driver 174 有效行 / 205 wc-l，保留 Open/adapters/mode/批量读/单包收发/dispose + ReadPacketsBatch/BuildMultiRequest 私有 helper）
- [x] N2 ~~`Cli/Program.cs` 拆分~~ — 取消（388 有效行已达标，不拆）
- [x] N3 ~~`ConfigurationModels.cs` 拆分~~ — 取消（367 有效行已达标，不拆）
- [x] N-gate：`dotnet build -warnaserror` + `dotnet test` 全绿；全仓有效行数复核无 >400（2026-08-28：0 Warning 0 Error；Passed 353/353 == 基线；全仓 106 个 .cs 文件 0 个超 400 有效行，top = Program.cs 388）
- [ ] N-commit：`refactor(ndis): remove dead public surface, extract focused types`

## 收尾（Phase 3）

- [ ] 全仓库 `wc -l` top ≤ 400（src + tests，排除 obj/bin）
- [ ] 测试总数 ≥ 基线
- [ ] trellis-check 全量质量检查
- [ ] 更新 spec（若有可沉淀约定：类-每-文件、TestHelpers 组织方式）
- [ ] 3.4 commit 收尾

## 回滚点

每批一个 commit；单批内出问题 `git checkout -- <files>` 或 revert 该批 commit，不影响其他批次。

## 基线记录（2026-08-28）

- 构建：`dotnet build WinForward.slnx` 零警告零错误（LSP 报 NdisPacketBufferPool 找不到为缓存假象，实际编译通过）
- 测试：Passed! Failed: 0, Passed: 353, Skipped: 0, Total: 353（WinForward.Core.Tests, net10.0）
- 超标文件数：13（src 6 / tests 7）
