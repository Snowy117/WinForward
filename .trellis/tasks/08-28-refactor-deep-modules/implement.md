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

- [ ] R1 `Socks5Client.cs` → `Socks5ControlConnection.cs` + `Socks5UdpTransport.cs`（最机械，先做）
- [ ] R2 `UdpProxyCoordinator.cs`：`UdpProxySession` 迁出；`IUdpResponseSink` 移到 `UdpResponseReinjector.cs`；三个移除块合并为 `TryRemoveSession` 私有方法
- [ ] R3 `TcpProxyCoordinator.cs`：提取 `TcpFrameRewriter` / `TcpSequenceObservation`（static 纯簇）→ `ClientResetInjector` → `TcpRedirectAcceptor` → `TcpRedirectSession` 提升；内联删除 `ReinjectExistingSynAsync` 别名
- [ ] R-gate：`dotnet test` 全绿；`wc -l` 检查 src/WinForward.Runtime 无 >400
- [ ] R-commit：`refactor(runtime): split coordinators into focused modules`

## Batch N：NdisApi / Cli / Configuration 拆分

- [ ] N1 `NdisApiDriver.cs`：迁出 `NdisAdapter` / `NdisNativeCallStatus` / `NdisNativeCallGate` / `NdisPacketBuffer` 四类型；删除死面（`Version`/`TryReadPacket`/批量 Send*，删除前 rg 复验零调用点）
- [ ] N2 `Cli/Program.cs` → `Program.cs` + `AdaptersCommand.cs` + `CaptureHost.cs` + `CoordinatorShutdownCaptureLoop.cs`
- [ ] N3 `ConfigurationModels.cs` → `ConfigurationJson.cs` + `ConfigurationContract.cs` + `PolicyRuleParser.cs` + 瘦身 `ConfigurationLoader` 所在文件
- [ ] N-gate：`dotnet build -warnaserror` + `dotnet test` 全绿
- [ ] N-commit：`refactor(ndis/cli/config): split oversized files, remove dead public surface`

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
