# 独立审计：jb inspectcode 抑制必要性复核

Task: `.trellis/tasks/09-20-jb-inspectcode-cleanup`
审计人：独立 check 代理（与实现批次无重叠）
工具：JetBrains Inspect Code **2026.1.3**（`.direnv/dotnet-tools/jb`）+ .NET SDK 10.0.401
审计日期：2026-09-20
审计对象：本任务新增与既有的**全部** jb 抑制 = 16 个 `.editorconfig` `resharper_*_highlighting = none` 键 + 128 处代码内 `// ReSharper disable` pragma 行（另记录性核对 Roslyn 域 MA0038）。

---

## 1. 方法（worktree 中立化 + 受控微探针 + 全量对账）

### 1.1 隔离 worktree

```bash
git worktree add --detach /tmp/jb-suppression-audit HEAD
git -C /home/paff/Projects/WinForward diff HEAD > /tmp/jb-audit-work.patch   # 7958 行，175 路径
git -C /tmp/jb-suppression-audit apply /tmp/jb-audit-work.patch             # exit 0
```

主树未被审计实验污染；审计结束用 `git worktree remove --force` 清理。

### 1.2 中立化（行数保持不变，保证报告行号可与主树对账）

| 机制 | 中立化方式 | 数量 |
|---|---|---|
| 代码 pragma | 整行替换为**空行**（`python3 /tmp/blank_pragmas.py`，48 文件） | 134 行（126 `disable` + 8 `restore`） |
| `.editorconfig` 键 | 行首加 `# AUDIT-NEUTRALIZED: ` 注释掉 | 14 键（其余 2 键**故意保持启用**，用于检验其是否有效） |
| `dotnet_diagnostic.MA0038/MA0041` | **不动**（Roslyn 域，jb 不报告） | — |

> **方法论教训（已实证）**：首轮尝试仅给指令文本加前缀（`// AUDIT-DISABLED // ReSharper disable …`、`// ReSharperNOP disable …`）**不可靠**——受控微探针（`/tmp/jbprobe`，net10.0 工程，三个形态完全相同的 duplicated-if 类）显示：目标 `if` 对**上方只要存在任意一行注释**，`DuplicatedSequentialIfBodies` 即不再报告（无论该注释是否为指令）。因此最终采用“整行置空”——空行不可能是抑制指令，结论无歧义。

### 1.3 全量扫描（唯一权威证据）

```bash
# 清缓存避免增量污染；单进程、无并发
rm -rf ~/.local/share/JetBrains/ReSharperHost /tmp/JB
cd /tmp/jb-suppression-audit
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-pragmafree.xml WinForward.slnx
```

| 项 | 值 |
|---|---|
| 起止（UTC） | 2026-09-20T09:51:58Z → 10:07:01Z（real 15m02s） |
| 退出码 | `jb-exit=0` |
| 报告 | `/tmp/jb-pragmafree.xml`（158,640 B） |
| 报告总数 | **729** 条（`//Issues/Project/Issue`） |
| 卫生检查 | 无 `CSharpErrors` 伪影（缓存污染签名），规则集合与基线同类 |

对账脚本：`/tmp/reconcile_final.py`（block-aware：`disable once` 取 ±4 行窗口，块 `disable`/`restore` 取块区间），逐条结果 `/tmp/audit-rows-final.tsv`。

### 1.4 完整性与舍入证明

729 = **597**（14 个被中立化键覆盖）+ **132**（pragma 覆盖）：

- 597 = ArrangeTrailingCommaInMultilineLists 148 + ArrangeObjectCreationWhenTypeNotEvident 256 + InvertIf 46 + ArrangeRedundantParentheses 41 + InconsistentNaming 22 + MethodSupportsCancellation 19 + CheckNamespace 19 + NotAccessedPositionalProperty.Local 11 + UnusedAutoPropertyAccessor.Global 11 + LoopCanBeConvertedToQuery 10 + ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator 6 + NotAccessedPositionalProperty.Global 4 + ForCanBeConvertedToForeach 3 + ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator 1
- 132 = AccessToDisposedClosure 64 + ConvertIfStatementToReturnStatement 14 + DisposeOnUsingVariable 10 + ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract 9 + AccessToModifiedClosure 9 + ParameterOnlyUsedForPreconditionCheck.Local 8 + ConvertIfStatementToSwitchStatement 5 + DuplicatedSequentialIfBodies 4 + HeuristicUnreachableCode 2 + CSharpWarnings::CS0162 2 + InlineTemporaryVariable 2 + SwitchStatementHandlesSomeKnownEnumValuesWithDefault 1 + NotResolvedInText 1 + UnusedMemberInSuper.Global 1

即：报告中每一条都能被某个抑制解释，不存在未被任何抑制覆盖的残项（若某抑制已失效，其站点会作为额外命中出现并落在对账窗口内）。

---

## 2. 汇总裁定

| 机制 | 条目数 | NECESSARY | POLICY | STALE | 处置 |
|---|---|---|---|---|---|
| 代码 pragma `disable once` | 118 → **116** | 116 | 0 | **2** | 删除 2 条 |
| 代码 pragma 块 `disable`/`restore` | 10 | 10 | 0 | 0 | 保留 |
| `.editorconfig` 键 | 16 | 14 | 2 | 0 | 全部保留 |
| Roslyn `dotnet_diagnostic.MA0038` + 7 处 `#pragma` | 1 规则级 + 7 站点 | — | — | — | 记录性核对（jb 不报告） |

**STALE 2 条**（实测触发集为空，已从主树删除）：

| 机制 | 规则 | 站点 | 报告证据 | 裁定 |
|---|---|---|---|---|
| `disable once` | `MemberCanBePrivate.Global` | `src/WinForward.Protocols/PacketChecksums.cs:29` | 该规则在 729 条中**命中 0 次**（全解决方案）：public 协议 oracle `TryRewriteUdpEndpoints(Span<byte>, IPAddress, …)` 被同解决方案的测试项目引用，jb 解析得到跨项目使用 | **STALE → 删除** |
| `disable once` | `ConvertIfStatementToReturnStatement` | `src/WinForward.Core/IPAddressValue.cs:105` | 该文件内该规则仅命中 40/47 行（分别由 39/46 的兄弟 pragma 覆盖）；105 行站点（B4a 合并后的早退 guard）**不再被检出** | **STALE → 删除** |

**POLICY 2 键**（命中 0，但属规则级策略性保留）：

| 键（`.editorconfig`） | 对应 inspection | 命中 | 裁定理由 |
|---|---|---|---|
| `resharper_arrange_trailing_comma_in_singleline_lists_highlighting`（**本任务前既有**，HEAD:356） | `ArrangeTrailingCommaInSinglelineLists` | 0 | 与多行版本（148 命中、NECESSARY）同属 trailing-comma 风格族；仓库裁定“不对尾逗号风格出建议”，单行/多行拆开保留会造成同族半开半关。触发模式真实存在（单行集合初始化器内尾逗号），只是当前代码库无此写法 → 作为 warning-default 风格护栏保留 |
| `resharper_foreach_can_be_partly_converted_to_query_highlighting`（本任务新增） | `ForeachCanBePartlyConvertedToQuery` | 0 | 其孪生 inspection `…UsingAnotherGetEnumerator` 命中 6 次（jb 对每个循环只会归入二者之一：无第二枚举器的分支即此 ID）；整族属**性能红线**（循环不转 LINQ，hot-path.md）→ 保持 LINQ 家族完整关闭 |

---

## 3. pragma 逐规则聚合裁定

| 规则 | 机制 | pragma 行数 | 报告命中 | 裁定 |
|---|---|---|---|---|
| `AccessToDisposedClosure` | block/once | 59 | 64 | NECESSARY |
| `ConvertIfStatementToReturnStatement` | once | 15 | 14 | **NECESSARY 14 / STALE 1** |
| `DisposeOnUsingVariable` | once | 10 | 10 | NECESSARY |
| `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | once | 9 | 9 | NECESSARY |
| `AccessToModifiedClosure` | once | 9 | 9 | NECESSARY |
| `ParameterOnlyUsedForPreconditionCheck.Local` | block/once | 7 | 8 | NECESSARY |
| `ConvertIfStatementToSwitchStatement` | once | 5 | 5 | NECESSARY |
| `DuplicatedSequentialIfBodies` | once | 4 | 4 | NECESSARY |
| `HeuristicUnreachableCode` | block | 2 | 2 | NECESSARY |
| `CSharpWarnings::CS0162` | block | 2 | 2 | NECESSARY |
| `InlineTemporaryVariable` | once | 2 | 2 | NECESSARY |
| `SwitchStatementHandlesSomeKnownEnumValuesWithDefault` | once | 1 | 1 | NECESSARY |
| `MemberCanBePrivate.Global` | once | 1 | 0 | **NECESSARY 0 / STALE 1** |
| `NotResolvedInText` | once | 1 | 1 | NECESSARY |
| `UnusedMemberInSuper.Global` | once | 1 | 1 | NECESSARY |

> 规则级说明：上表全部站点在“抑制全部移除”的全量报告中于同文件 ±4 行（块形态取块区间）内出现对应规则命中，故 126 条判为 NECESSARY；唯一无命中的 2 条即第 2 节 STALE。

## 4. `.editorconfig` 键裁定表（含报告证据样例）

| 键 | inspection | 命中 | 样例证据（file:line） | 裁定 |
|---|---|---|---|---|
| `resharper_arrange_trailing_comma_in_multiline_lists_highlighting` | ArrangeTrailingCommaInMultilineLists | 148 | benchmarks/WinForward.Benchmarks/Perf/DispatcherBenchmarks.cs:56 | NECESSARY（既有键） |
| `resharper_arrange_trailing_comma_in_singleline_lists_highlighting` | ArrangeTrailingCommaInSinglelineLists | 0 | —（0 命中） | POLICY（既有键，0 命中） |
| `resharper_arrange_redundant_parentheses_highlighting` | ArrangeRedundantParentheses | 41 | benchmarks/WinForward.Benchmarks/BenchmarkShared.cs:106 | NECESSARY（规则级策略） |
| `resharper_arrange_object_creation_when_type_not_evident_highlighting` | ArrangeObjectCreationWhenTypeNotEvident | 256 | src/WinForward.Cli/DurableCaptureBundle.cs:271 | NECESSARY（规则级策略） |
| `resharper_inconsistent_naming_highlighting` | InconsistentNaming | 22 | src/WinForward.Core/IPAddressValue.cs:15 | NECESSARY（规则级策略） |
| `resharper_not_accessed_positional_property_global_highlighting` | NotAccessedPositionalProperty.Global | 4 | benchmarks/WinForward.Benchmarks/Stability/StabilityShared.cs:55 | NECESSARY（规则级策略） |
| `resharper_not_accessed_positional_property_local_highlighting` | NotAccessedPositionalProperty.Local | 11 | src/WinForward.Runtime/SelfTrafficRegistry.cs:56 | NECESSARY（规则级策略） |
| `resharper_loop_can_be_converted_to_query_highlighting` | LoopCanBeConvertedToQuery | 10 | benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:526 | NECESSARY（性能红线） |
| `resharper_foreach_can_be_partly_converted_to_query_highlighting` | ForeachCanBePartlyConvertedToQuery | 0 | —（0 命中） | POLICY（0 命中，家族封口） |
| `resharper_foreach_can_be_partly_converted_to_query_using_another_get_enumerator_highlighting` | ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator | 6 | src/WinForward.Core/FlowTable.cs:97 | NECESSARY（性能红线） |
| `resharper_foreach_can_be_converted_to_query_using_another_get_enumerator_highlighting` | ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator | 1 | benchmarks/WinForward.Benchmarks/Stability/UdpRawBaselineScenario.cs:62 | NECESSARY（性能红线） |
| `resharper_for_can_be_converted_to_foreach_highlighting` | ForCanBeConvertedToForeach | 3 | benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:811 | NECESSARY（性能红线） |
| `resharper_invert_if_highlighting` | InvertIf | 46 | benchmarks/WinForward.Benchmarks/Stability/UdpBurstInstrumentation.cs:71 | NECESSARY（规则级策略：50 站点仅 4 处采纳） |
| `resharper_method_supports_cancellation_highlighting` | MethodSupportsCancellation | 19 | tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:50 | NECESSARY（tests glob，与既有 MA0040 裁定一致） |
| `resharper_check_namespace_highlighting` | CheckNamespace | 19 | tests/WinForward.Core.Tests/TestHelpers/AdapterFakes.cs:4 | NECESSARY（TestHelpers 路径 glob，与既有 IDE0130 裁定一致） |
| `resharper_unused_auto_property_accessor_global_highlighting` | UnusedAutoPropertyAccessor.Global | 11 | benchmarks/WinForward.Benchmarks/Perf/CapturePumpBenchmarks.cs:22 | NECESSARY（bench Perf glob，BDN [Params] 反射注入） |

---

## 5. 逐条明细（附录，128 行）

| # | 机制 | 规则 | 站点（file:line） | 报告命中行 | 裁定 |
|---|---|---|---|---|---|
| 1 | once | `AccessToDisposedClosure` | `benchmarks/WinForward.Benchmarks/Stability/TcpEofScenario.cs:62` | 63,65,65 | NECESSARY |
| 2 | block | `AccessToDisposedClosure` | `benchmarks/WinForward.Benchmarks/Stability/TcpEofScenario.cs:64` | 65,65 | NECESSARY |
| 3 | once | `AccessToDisposedClosure` | `benchmarks/WinForward.Benchmarks/Stability/TcpThroughputScenario.cs:92` | 93 | NECESSARY |
| 4 | once | `AccessToDisposedClosure` | `benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:141` | 142 | NECESSARY |
| 5 | once | `AccessToDisposedClosure` | `src/WinForward.Cli/Program.cs:323` | 324 | NECESSARY |
| 6 | once | `AccessToDisposedClosure` | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:143` | 145 | NECESSARY |
| 7 | once | `AccessToDisposedClosure` | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs:147` | 145,149 | NECESSARY |
| 8 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:114` | 115 | NECESSARY |
| 9 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:127` | 128 | NECESSARY |
| 10 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:135` | 136,138 | NECESSARY |
| 11 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:137` | 136,138 | NECESSARY |
| 12 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:157` | 158 | NECESSARY |
| 13 | block | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:183` | 184,184 | NECESSARY |
| 14 | block | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:30` | 31,31 | NECESSARY |
| 15 | block | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:49` | 50,50 | NECESSARY |
| 16 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterListWatcherTests.cs:76` | 77 | NECESSARY |
| 17 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/AdapterLocalAddressProviderCacheTests.cs:129` | 130 | NECESSARY |
| 18 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:138` | 139 | NECESSARY |
| 19 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:57` | 58,61 | NECESSARY |
| 20 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:60` | 58,61 | NECESSARY |
| 21 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:94` | 95 | NECESSARY |
| 22 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:98` | 95,99 | NECESSARY |
| 23 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerHealthSignalTests.cs:33` | 34 | NECESSARY |
| 24 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerPeriodicRefreshTests.cs:22` | 23 | NECESSARY |
| 25 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerPeriodicRefreshTests.cs:44` | 45 | NECESSARY |
| 26 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerPeriodicRefreshTests.cs:69` | 70 | NECESSARY |
| 27 | block | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerPeriodicRefreshTests.cs:75` | 77,77 | NECESSARY |
| 28 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:131` | 132 | NECESSARY |
| 29 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:151` | 152 | NECESSARY |
| 30 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:174` | 175 | NECESSARY |
| 31 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:194` | 195 | NECESSARY |
| 32 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:198` | 195,200 | NECESSARY |
| 33 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:209` | 210 | NECESSARY |
| 34 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:231` | 232 | NECESSARY |
| 35 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:237` | 238 | NECESSARY |
| 36 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:265` | 266 | NECESSARY |
| 37 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:362` | 363 | NECESSARY |
| 38 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:369` | 370 | NECESSARY |
| 39 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:397` | 398 | NECESSARY |
| 40 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:403` | 404,406 | NECESSARY |
| 41 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:405` | 404,406 | NECESSARY |
| 42 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:51` | 52 | NECESSARY |
| 43 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:98` | 99 | NECESSARY |
| 44 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerTests.cs:101` | 102 | NECESSARY |
| 45 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerTests.cs:129` | 130 | NECESSARY |
| 46 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerTests.cs:150` | 151 | NECESSARY |
| 47 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:117` | 118 | NECESSARY |
| 48 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:179` | 180 | NECESSARY |
| 49 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:217` | 218 | NECESSARY |
| 50 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:27` | 28 | NECESSARY |
| 51 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:59` | 60 | NECESSARY |
| 52 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:86` | 87 | NECESSARY |
| 53 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs:134` | 135 | NECESSARY |
| 54 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs:249` | 250 | NECESSARY |
| 55 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs:272` | 273 | NECESSARY |
| 56 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs:43` | 44 | NECESSARY |
| 57 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs:71` | 72 | NECESSARY |
| 58 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/RuntimeCountersTests.cs:65` | 66 | NECESSARY |
| 59 | once | `AccessToDisposedClosure` | `tests/WinForward.Core.Tests/RuntimeLogThrottleTests.cs:48` | 49 | NECESSARY |
| 60 | once | `AccessToModifiedClosure` | `src/WinForward.Cli/Program.cs:217` | 218 | NECESSARY |
| 61 | once | `AccessToModifiedClosure` | `src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs:102` | 104 | NECESSARY |
| 62 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/IdleExpirySweeperFailureTests.cs:35` | 36 | NECESSARY |
| 63 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:36` | 37 | NECESSARY |
| 64 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/LayeredCaptureRunnerRefreshTests.cs:50` | 52 | NECESSARY |
| 65 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/UdpProxyCoordinatorLifecycleTests.cs:242` | 243,245 | NECESSARY |
| 66 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/UdpProxyCoordinatorLifecycleTests.cs:244` | 243,245 | NECESSARY |
| 67 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/UdpProxyCoordinatorLifecycleTests.cs:284` | 285,287 | NECESSARY |
| 68 | once | `AccessToModifiedClosure` | `tests/WinForward.Core.Tests/UdpProxyCoordinatorLifecycleTests.cs:286` | 285,287 | NECESSARY |
| 69 | block | `CSharpWarnings::CS0162` | `benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:50` | 54 | NECESSARY |
| 70 | block | `CSharpWarnings::CS0162` | `benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs:38` | 42 | NECESSARY |
| 71 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:73` | 74 | NECESSARY |
| 72 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/FlowDispatcher.cs:150` | 151 | NECESSARY |
| 73 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/FlowDispatcher.cs:188` | 189 | NECESSARY |
| 74 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/FlowDispatcher.cs:324` | 325 | NECESSARY |
| 75 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:117` | 118 | NECESSARY |
| 76 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:389` | 390 | NECESSARY |
| 77 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:496` | 497 | NECESSARY |
| 78 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:526` | 527 | NECESSARY |
| 79 | once | `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:581` | 582 | NECESSARY |
| 80 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Core/Domain.cs:87` | 88 | NECESSARY |
| 81 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Core/IPAddressValue.cs:105` | — | STALE |
| 82 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Core/IPAddressValue.cs:39` | 40 | NECESSARY |
| 83 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Core/IPAddressValue.cs:46` | 47 | NECESSARY |
| 84 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.NdisApi/NdisApiAbi.cs:166` | 167 | NECESSARY |
| 85 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.NdisApi/NdisApiAbi.cs:175` | 176 | NECESSARY |
| 86 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.NdisApi/NdisPacketBuffer.cs:41` | 42 | NECESSARY |
| 87 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.NdisApi/NdisPacketBuffer.cs:79` | 80 | NECESSARY |
| 88 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Runtime/FlowDispatcher.cs:173` | 174 | NECESSARY |
| 89 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Runtime/Socks5/Socks5UdpTransport.cs:330` | 331 | NECESSARY |
| 90 | once | `ConvertIfStatementToReturnStatement` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:564` | 565 | NECESSARY |
| 91 | once | `ConvertIfStatementToReturnStatement` | `tests/WinForward.Core.Tests/HighResolutionTimerScopeTests.cs:77` | 78 | NECESSARY |
| 92 | once | `ConvertIfStatementToReturnStatement` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:187` | 188 | NECESSARY |
| 93 | once | `ConvertIfStatementToReturnStatement` | `tests/WinForward.Core.Tests/TestHelpers/CaptureLifecycleFakes.cs:28` | 29 | NECESSARY |
| 94 | once | `ConvertIfStatementToReturnStatement` | `tests/WinForward.Core.Tests/TestHelpers/Socks5UdpDatagrams.cs:15` | 16 | NECESSARY |
| 95 | once | `ConvertIfStatementToSwitchStatement` | `src/WinForward.Configuration/ConfigurationModels.cs:240` | 241 | NECESSARY |
| 96 | once | `ConvertIfStatementToSwitchStatement` | `src/WinForward.Protocols/TcpResetBuilder.cs:49` | 50 | NECESSARY |
| 97 | once | `ConvertIfStatementToSwitchStatement` | `src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs:151` | 152 | NECESSARY |
| 98 | once | `ConvertIfStatementToSwitchStatement` | `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:374` | 375 | NECESSARY |
| 99 | once | `ConvertIfStatementToSwitchStatement` | `src/WinForward.Windows/UnicastAddressInventory.cs:156` | 157 | NECESSARY |
| 100 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs:80` | 81 | NECESSARY |
| 101 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/FlowDispatcherTests.cs:130` | 131,134 | NECESSARY |
| 102 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/FlowDispatcherTests.cs:133` | 131,134 | NECESSARY |
| 103 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/HighResolutionTimerScopeTests.cs:19` | 20 | NECESSARY |
| 104 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/HighResolutionTimerScopeTests.cs:34` | 35 | NECESSARY |
| 105 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/HighResolutionTimerScopeTests.cs:62` | 63 | NECESSARY |
| 106 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs:336` | 337 | NECESSARY |
| 107 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/NdisPacketBufferPoolTests.cs:129` | 130 | NECESSARY |
| 108 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/TcpPendingSynSetupTests.cs:260` | 261 | NECESSARY |
| 109 | once | `DisposeOnUsingVariable` | `tests/WinForward.Core.Tests/TcpProxyCoordinatorLifecycleTests.cs:399` | 400 | NECESSARY |
| 110 | once | `DuplicatedSequentialIfBodies` | `src/WinForward.Protocols/Socks5State.cs:119` | 120 | NECESSARY |
| 111 | once | `DuplicatedSequentialIfBodies` | `src/WinForward.Runtime/FlowDispatcher.cs:158` | 159 | NECESSARY |
| 112 | once | `DuplicatedSequentialIfBodies` | `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:585` | 586 | NECESSARY |
| 113 | once | `DuplicatedSequentialIfBodies` | `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:204` | 205 | NECESSARY |
| 114 | block | `HeuristicUnreachableCode` | `benchmarks/WinForward.Benchmarks/Stability/UdpBurstScenario.cs:50` | 54 | NECESSARY |
| 115 | block | `HeuristicUnreachableCode` | `benchmarks/WinForward.Benchmarks/Stability/UdpLossScenario.cs:38` | 42 | NECESSARY |
| 116 | once | `InlineTemporaryVariable` | `tests/WinForward.Core.Tests/NativeBufferPoolTests.cs:127` | 128 | NECESSARY |
| 117 | once | `InlineTemporaryVariable` | `tests/WinForward.Core.Tests/NativeBufferPoolTests.cs:278` | 279 | NECESSARY |
| 118 | once | `MemberCanBePrivate.Global` | `src/WinForward.Protocols/PacketChecksums.cs:29` | — | STALE |
| 119 | once | `NotResolvedInText` | `src/WinForward.Runtime/FlowDispatcher.cs:61` | 62 | NECESSARY |
| 120 | once | `ParameterOnlyUsedForPreconditionCheck.Local` | `benchmarks/WinForward.Benchmarks/Stability/GcSoakScenario.cs:206` | 207 | NECESSARY |
| 121 | once | `ParameterOnlyUsedForPreconditionCheck.Local` | `src/WinForward.Core/Domain.cs:34` | 35 | NECESSARY |
| 122 | once | `ParameterOnlyUsedForPreconditionCheck.Local` | `tests/WinForward.Core.Tests/RuntimeDiagnosticLoggingTests.cs:196` | 197 | NECESSARY |
| 123 | once | `ParameterOnlyUsedForPreconditionCheck.Local` | `tests/WinForward.Core.Tests/TcpFragmentHandlingTests.cs:158` | 159 | NECESSARY |
| 124 | block | `ParameterOnlyUsedForPreconditionCheck.Local` | `tests/WinForward.Core.Tests/TestHelpers/CaptureLifecycleFakes.cs:17` | 20,20 | NECESSARY |
| 125 | once | `ParameterOnlyUsedForPreconditionCheck.Local` | `tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs:152` | 153 | NECESSARY |
| 126 | once | `ParameterOnlyUsedForPreconditionCheck.Local` | `tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs:265` | 266 | NECESSARY |
| 127 | once | `SwitchStatementHandlesSomeKnownEnumValuesWithDefault` | `benchmarks/WinForward.Benchmarks/Stability/TcpEofScenario.cs:266` | 267 | NECESSARY |
| 128 | once | `UnusedMemberInSuper.Global` | `src/WinForward.Runtime/RuntimeLogging.cs:14` | 15 | NECESSARY |

---

## 6. MA0038（Roslyn 域）记录性核对

jb inspectcode 不报告 Roslyn/Meziantou 域规则，故按任务要求只做记录性核对：

- `.editorconfig` 存在规则级 `dotnet_diagnostic.MA0038.severity = none`（2026-09-20 用户裁定；作者已弃用、继任 CA1822 已启用且对本批站点 0 命中）。
- 本任务另新增 7 处 `#pragma warning disable/restore MA0038`（LoopbackSocks5TcpServer.cs、TcpRedirectAcceptor.cs、TcpRedirectSessionStore.cs、TcpRedirectSetup.cs ×2、UdpSessionSetup.cs；共 6 站点 + 1 处 bench）配行内理由：primary ctor 参数被正文引用时方法无法 `static`（CS9105）。
- 它们与规则级关闭**功能重复**，属有意保留的“就地理由”；不参与 jb 报告，故不产生 STALE/FIXABLE 判定。
- **后记（2026-09-20，用户裁定）**：上述站点 pragma 已连同配套理由注释全部移除（实测 6 块 / 12 指令行；本节原记“7 处”系按站点口径的笔误），MA0038 抑制终态仅剩 `.editorconfig` 规则级关闭；移除后 Release build 0 警告 + `dotnet format` exit 0 复核通过。

---

## 7. 处置记录

| 处置 | 对象 | 证据/验证 |
|---|---|---|
| 删除 | `src/WinForward.Protocols/PacketChecksums.cs:29` `disable once MemberCanBePrivate.Global` | 无抑制全量报告 0 命中；方法 XML doc 已记录 oracle/IVT 职责 |
| 删除 | `src/WinForward.Core/IPAddressValue.cs:105` `disable once ConvertIfStatementToReturnStatement` | 无抑制全量报告该文件仅 40/47 命中（由 39/46 pragma 覆盖） |
| 保留（POLICY） | 2 个 0 命中键（见第 2 节） | 同上 |
| FIXABLE | 无 | 其余 126 条均为：文档化误报类（IVT/BDN/record 位置属性/member-path paramName）、性能红线（LINQ 家族）、可读性红线（InvertIf/括号）、刻意测试模式（dispose 后断言、失败注入 seam）；修复会违反已确认红线或不划算 |

处置后主树 pragma 行数：**134 → 132 行**（`disable once` 118 → 116；块 8 行不变），`.editorconfig` 键仍 16。

### 7.1 处置后终局门禁复验（2026-09-20）

处置（删除 2 条 STALE pragma）后，在主树（非 worktree）完整复跑四道门禁，全绿：

| 门禁 | 命令 | 结果 | 时间（UTC） |
|---|---|---|---|
| Release build | `dotnet build WinForward.slnx -c Release` | `build_exit=0`，`0 Warning(s) / 0 Error(s)` | 2026-09-20T10:08–10:10 |
| Release tests | `dotnet test WinForward.slnx -c Release` | `test_exit=0`，`Passed! - Failed: 0, Passed: 725, Skipped: 0, Total: 725` | 2026-09-20T10:10 |
| format 门禁 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | `format_exit=0`，空输出 | 2026-09-20T10:10–10:11 |
| jb 门禁 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-final-audit.xml WinForward.slnx` | `jb_exit=0`；解析 `//Issues/Project/Issue` = **0**、`//IssueTypes/IssueType` = 0（XML 329 B 空报告） | 2026-09-20T10:11–10:24 |

证据脚本 `/tmp/taskc.sh`，日志 `/tmp/taskc.log`（`build_exit=0` @ L20、`test_exit=0` @ L37、`format_exit=0` @ L39、`jb_exit=0` @ L62939），终局报告 `/tmp/jb-final-audit.xml`。jb 日志中的 `System.Composition.AttributedModel` Roslyn-worker 报错为已知非致命环境噪音（不影响检查运行），无 `CSharpErrors`。

**处置范围核对**：接受报告（`/tmp/final-accept.xml`，15:34）之后仅改动 2 个源文件（`src/WinForward.Core/IPAddressValue.cs`、`src/WinForward.Protocols/PacketChecksums.cs`，均为删除一条 STALE pragma，行数 −1）；`bin/obj` 之外的其余源文件 mtime 未变。上述终局 jb 复跑即针对该处置后树。

---

## 8. 结论与遗留风险

**结论**：全部 144 个抑制对象（16 键 + 128 pragma 行）经“移除抑制后全量重跑”实证：**142 个 NECESSARY/POLICY**（126 pragma 行 + 14 键 + 2 POLICY 键），**2 条 pragma 行 STALE 并已删除**。无 FIXABLE 项；未发现抑制掩盖真实缺陷的情形。处置后四道门禁（build/test/format/jb）在全量复跑中全绿（见 §7.1）。

**遗留风险**：
1. **版本敏感**：验收与 CI 均固定 Inspect Code 2026.1.3。升级版本后规则集/默认严重级变化会让门禁重新报出，需要重新裁量（届时 `research/jb-inventory.md` 的规则表是起点）。
2. **jb 增量缓存污染**：与快速编辑交替的增量运行可能产出伪 `CSharpErrors` 与连带 unused-member 误报（本审计已复现过“注释形态影响规则检出”的敏感性）。AGENTS.md 已写明处置：清 `~/.local/share/JetBrains/`、`/tmp/JB` 后重跑。
3. **跨工具盲区**：`// ReSharper disable` 对 dotnet format / Roslyn 分析器不可见，反向亦然；两条门禁必须同时为绿。
4. **0 命中键的再评估**：`arrange_trailing_comma_in_singleline_lists` 与 `foreach_can_be_partly_converted_to_query` 当前对报告无影响；若后续对风格族或性能族改判，应优先复核这两项。
5. **注释敏感规则**：实证发现部分 jb 规则（如 `DuplicatedSequentialIfBodies`）的检出受邻近注释影响；后续若在成对相邻 `if` 前加注释/移除注释，需重跑 jb 门禁（本审计的 3 条 Duplicated pragma 正是因此仍为 NECESSARY）。

