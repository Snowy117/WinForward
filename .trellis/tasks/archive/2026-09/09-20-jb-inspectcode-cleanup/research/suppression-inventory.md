# 抑制清单 — jb inspectcode 任务（09-20-jb-inspectcode-cleanup，审计输入）

范围：本任务新增/变更的全部 jb 相关抑制 = `.editorconfig` `resharper_*` 键 + 代码内 `// ReSharper disable` pragma。

## 1. editorconfig resharper 规则键
- `resharper_arrange_trailing_comma_in_multiline_lists_highlighting` — 段 `[*.cs]` (L355) — jb/R# ArrangeTrailingCommaInMultilineLists/SinglelineLists：代码库 de facto 使用多行尾逗号 （enums / multiline initializers / method calls），dotnet format 对尾逗号静默=允许， 但 jb 默认偏好"不要尾逗号"与之冲突。jb inspectcode 对风格偏好键解析不稳定
- `resharper_arrange_trailing_comma_in_singleline_lists_highlighting` — 段 `[*.cs]` (L356) — 
- `resharper_arrange_redundant_parentheses_highlighting` — 段 `[*.cs]` (L369) — ArrangeRedundantParentheses：与强制门禁 RCS1123 在**同一 token** 上直接对立——实测 37 个 命中站点中 32 个正是上个任务 RCS1123 强制补齐的清晰括号（如 `4 + (2 * 8)`、 `TryGetValue(...) ? previous : 0`）。dotnet format 门禁为准，jb 侧规则级降级。
- `resharper_arrange_object_creation_when_type_not_evident_highlighting` — 段 `[*.cs]` (L373) — ArrangeObjectCreationWhenTypeNotEvident：jb 偏好"类型不明显时显式写类型"，与代码库 de facto 的目标类型 new(...) 风格相悖（256 处命中，遍布 src/tests/bench）；显式化属纯风格 churn。
- `resharper_inconsistent_naming_highlighting` — 段 `[*.cs]` (L379) — InconsistentNaming：jb 对 .editorconfig dotnet_naming_* 的读取语义与 Roslyn 不同——缩写驼峰化 （IPAddressValue→IpAddressValue，违反 BCL 惯例）、把 field-only 的 constants 规则误用于局部 const、与 [ThreadStatic] t_ 约定冲突；37 处命中全部为工具差异、无真
- `resharper_not_accessed_positional_property_global_highlighting` — 段 `[*.cs]` (L384) — NotAccessedPositionalProperty：record 的位置属性承载**合成的相等性/哈希**（及序列化契约）语义， 直接读取可能为零但作用关键（SelfTrafficRegistry.WildcardKey、TcpRedirectTable.ReverseRedirectKey、 ProcessCacheKey 等均为相等性键）；jb 的分析未建模这部分。16 处命中均为 r
- `resharper_not_accessed_positional_property_local_highlighting` — 段 `[*.cs]` (L385) — 
- `resharper_loop_can_be_converted_to_query_highlighting` — 段 `[*.cs]` (L389) — LINQ 转换家族（性能优先红线，hot-path.md）：显式循环不转 LINQ——LINQ 引入委托分配与迭代器 状态机；19 处命中集中在 soak/burst 场景与测试辅助。规则级拒绝。
- `resharper_foreach_can_be_partly_converted_to_query_highlighting` — 段 `[*.cs]` (L390) — 
- `resharper_foreach_can_be_partly_converted_to_query_using_another_get_enumerator_highlighting` — 段 `[*.cs]` (L391) — 
- `resharper_foreach_can_be_converted_to_query_using_another_get_enumerator_highlighting` — 段 `[*.cs]` (L392) — 
- `resharper_for_can_be_converted_to_foreach_highlighting` — 段 `[*.cs]` (L393) — 
- `resharper_invert_if_highlighting` — 段 `[*.cs]` (L406) — InvertIf（用户红线：逐站点评估，2026-09-20 B4a）：50 处命中仅 **4 处采纳**（StabilityContext.Write 尾部空值测试→早退；SoakRunner 平台门反转后消除重复调用；NdisPacketActionExecutor.RetireLanesExcept 与 TcpRedirectSessionStore.DisposeCoreAsync 的循环
- `resharper_method_supports_cancellation_highlighting` — 段 `[tests/**.cs]` (L410) — 
- `resharper_check_namespace_highlighting` — 段 `[tests/WinForward.Core.Tests/TestHelpers/**.cs]` (L414) — 
- `resharper_unused_auto_property_accessor_global_highlighting` — 段 `[benchmarks/WinForward.Benchmarks/Perf/**.cs]` (L418) — 

## 2. 代码内 ReSharper pragma（本任务新增，按规则聚合）
### AccessToDisposedClosure（59 处）
- 理由样例：ReSharper disable once AccessToDisposedClosure // The receive leg is awaited below before the finally disposes either peer; the timeout path disposes
- 文件分布：LayeredCaptureRunnerRefreshTests.cs×16；AdapterListWatcherTests.cs×9；NdisCapturePumpTests.cs×6；CaptureLifecycleTests.cs×5；NdisCaptureResilienceTests.cs×5；LayeredCaptureRunnerPeriodicRefreshTests.cs×4；LayeredCaptureRunnerTests.cs×3；TcpEofScenario.cs×2；LayeredCaptureRunner.cs×2；TcpThroughputScenario.cs×1
### ConvertIfStatementToReturnStatement（14 处；原 15，审计移除 1）
- 理由样例：ReSharper disable once ConvertIfStatementToReturnStatement // Guard-clause + throw reads failure-first; the suggested `cond ? throw ... : value` form
- 文件分布：IPAddressValue.cs×2（审计移除 105 行那条：该文件仅 40/47 命中，105 行站点不再被检出）；NdisApiAbi.cs×2；NdisPacketBuffer.cs×2；Domain.cs×1；FlowDispatcher.cs×1；Socks5UdpTransport.cs×1；TcpProxyCoordinator.cs×1；HighResolutionTimerScopeTests.cs×1；NdisCapturePumpTests.cs×1；CaptureLifecycleFakes.cs×1
### DisposeOnUsingVariable（10 处）
- 理由样例：ReSharper disable once DisposeOnUsingVariable // The explicit DisposeAsync is the act under test: the Closed state and the captured teardown are asser
- 文件分布：HighResolutionTimerScopeTests.cs×3；FlowDispatcherTests.cs×2；CaptureLifecycleTests.cs×1；NdisCapturePumpTests.cs×1；NdisPacketBufferPoolTests.cs×1；TcpPendingSynSetupTests.cs×1；TcpProxyCoordinatorLifecycleTests.cs×1
### AccessToModifiedClosure（9 处）
- 理由样例：ReSharper disable once AccessToModifiedClosure // One-shot wiring: runnerRef is assigned before RunAsync starts, and the callback can fire only from a
- 文件分布：UdpProxyCoordinatorLifecycleTests.cs×4；LayeredCaptureRunnerRefreshTests.cs×2；Program.cs×1；NdisCaptureGeneration.cs×1；IdleExpirySweeperFailureTests.cs×1
### ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract（9 处）
- 理由样例：ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract // Deliberate fail-closed capture-boundary guard: Lease is declared
- 文件分布：TcpProxyCoordinator.cs×5；FlowDispatcher.cs×3；NdisPacketActionExecutor.cs×1
### ParameterOnlyUsedForPreconditionCheck.Local（7 处）
- 理由样例：ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local // Deliberate vacuity contract: the parameter feeds the guard that fails the soak w
- 文件分布：TcpCoordinatorFakes.cs×2；GcSoakScenario.cs×1；Domain.cs×1；RuntimeDiagnosticLoggingTests.cs×1；TcpFragmentHandlingTests.cs×1；CaptureLifecycleFakes.cs×1
### ConvertIfStatementToSwitchStatement（5 处）
- 理由样例：ReSharper disable once ConvertIfStatementToSwitchStatement // Range-pattern precondition — a switch over the same value with a relational pattern adds
- 文件分布：ConfigurationModels.cs×1；TcpResetBuilder.cs×1；CaptureAdapterScopeResolver.cs×1；NdisPacketActionExecutor.cs×1；UnicastAddressInventory.cs×1
### DuplicatedSequentialIfBodies（4 处）
- 理由样例：ReSharper disable once DuplicatedSequentialIfBodies // Distinct parse stages sharing the Invalid exit: ATYP-3 zero-length address (malformed) vs missi
- 文件分布：Socks5State.cs×1；FlowDispatcher.cs×1；TcpProxyCoordinator.cs×1；TcpRedirectTable.cs×1
### HeuristicUnreachableCode（2 处）
- 理由样例：ReSharper disable HeuristicUnreachableCode, CSharpWarnings::CS0162
- 文件分布：UdpBurstScenario.cs×1；UdpLossScenario.cs×1
### CSharpWarnings（2 处）
- 理由样例：ReSharper disable HeuristicUnreachableCode, CSharpWarnings::CS0162
- 文件分布：UdpBurstScenario.cs×1；UdpLossScenario.cs×1
### InlineTemporaryVariable（2 处）
- 理由样例：ReSharper disable once InlineTemporaryVariable // `copy` IS the asserted scenario object: the test locks "a lease copy releases the same rental window
- 文件分布：NativeBufferPoolTests.cs×2
### SwitchStatementHandlesSomeKnownEnumValuesWithDefault（1 处）
- 理由样例：ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault // TransferOutcome.Other is the enum's catch-all member and is deliberatel
- 文件分布：TcpEofScenario.cs×1
### ~~MemberCanBePrivate.Global（1 处）~~ — 审计 STALE，已删除
- 原站点：PacketChecksums.cs:29（public 协议 oracle `TryRewriteUdpEndpoints(Span<byte>, IPAddress, …)`）
- 审计结论：抑制移除后的全量报告中 `MemberCanBePrivate.Global` 命中 0 次（该 oracle 被同解决方案的测试项目引用，jb 解析得到跨项目使用）→ 抑制无作用，已删除（见 `suppression-audit.md` §2/§7）。
### NotResolvedInText（1 处）
- 理由样例：ReSharper disable once NotResolvedInText // Deliberate member-path paramName: the centralized guard route (MA0015/S3928/CA2208 are scoped-suppressed f
- 文件分布：FlowDispatcher.cs×1
### UnusedMemberInSuper.Global（1 处）
- 理由样例：ReSharper disable once UnusedMemberInSuper.Global // Level-parity logger contract: the `trace` log level is user-configurable (ConfigurationModels) an
- 文件分布：RuntimeLogging.cs×1

合计：editorconfig 键 16 个；pragma **126 行**（指令行 132 = `disable once` 116 + 块 `disable`/`restore` 8；审计删除 STALE 2 条 vs 初版 128 行）。

> 终局独立审计：见 `suppression-audit.md`（2026-09-20）。本文件的命令计数为审计输入（B 阶段中间态）；**最终权威计数与逐条裁定以审计报告为准**（无抑制全量重跑 = 729 条，128 行 → 126 NECESSARY / 2 STALE；16 键 → 14 NECESSARY / 2 0 命中按 POLICY 保留）。
