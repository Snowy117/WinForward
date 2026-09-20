# Batch Log — jb inspectcode 清理（base 74cbb23）

## B0 基线固化（2026-09-20）

| 项 | 命令 | 结果 |
|---|---|---|
| jb 确定性 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-rerun.xml WinForward.slnx` | exit=0（**有 1317 条 issue 时退出码仍为 0** → CI 以 XML 解析断言，不用退出码）；重跑 1317 = 首扫 1317 ✓ 稳定 |
| Release build | `dotnet build WinForward.slnx -c Release --no-restore` | exit 0，0 Warning / 0 Error |
| Release test | `dotnet test WinForward.slnx -c Release --no-restore --no-build` | exit 0，725/725 全绿 |

产物：research/baseline-jb.txt / baseline-build.txt / baseline-test.txt。

## B1 — A 类机械修复（2026-09-20，实现子代理）

方法：全部逐站点编辑（jb 无 CLI fixer）。XML 报告的 `Offset` 是**基于 base 74cbb23 原始文件**的精确字符区间、`Line` 多数为 1-based（少数 ±1）——大批量编辑按「原文件 offset 抽取 →
`difflib` 映射到当前文件坐标」的脚本化方式执行；每步后 Release build 0 警告。小规模定向验证用 `jb --no-build --include='**/<file>'`（分钟级），全量重跑用于最终核对。

### 逐规则结果（数字 = 修复站点数）

| 规则 | 基线 | 修复 | 跳过 | 说明 |
|---|---|---|---|---|
| RedundantUsingDirective | 79 | 79 | 0 | 逐站点确认（`snippet` 全部为单行 using；仓库全域 0 个 `#if`、单一 TFM net10.0 → 无条件编译风险；剩余可由 global using/父命名空间覆盖，构建兜底） |
| RedundantNameQualifier | 45 | 45 | 0 | `System.Globalization.`/`WinForward.Core.`/`Core.`/`WinForward.`/`System.Net.Sockets.`/`System.ComponentModel.` 前缀删除（using/父命名空间已覆盖） |
| RedundantCast | 280 | 280 | 0 | 250×(nint)、19×(ushort)、3×(IPAddressValue?)、2×(long)、2×(uint)、1×(nuint)/(EndPoint)/(int)/(byte)；逐站点确认移除不改变绑定。特例：CapturePumpBenchmarks `(int)Math.Min(remaining,(long)buffers.Length)`→`(int)Math.Min(remaining,buffers.Length)`（remaining 为 long，外层 (int) 必须保留） |
| RedundantUnsafeContext | 6 | 6 | 0 | 3 个测试方法 + NdisApiDriver 2 个转发方法 + UnicastAddressInventory.ParseRows（方法体无指针类型） |
| RedundantExplicitArrayCreation | 1 | 1 | 0 | `new NdisPacketBuffer?[] { buffer, null }` → `NdisPacketBuffer?[] buffers = [buffer, null]`（保持 IDE0300 干净） |
| RedundantExtendsListEntry | 1 | 1 | 0 | `record struct ... : IEquatable<T>` 隐式已实现 |
| RedundantJumpStatement | 2 | 2 | 0 | 均为 catch 内 return（后随方法/分支末尾，等价）；空 catch 按 S108 加注释 |
| InvalidXmlDocComment | 9 | 9 | 0 | see cref → `<c>`（歧义重载/文件引用）；`UdpProxy.UdpProxyCoordinator`/`UdpProxy.UdpAssociationTable` 相对限定修复不可解析 cref；`paramref`→`<c>`（类型级 summary 内） |
| UseCollectionExpression | 7 | 7 | 0 | `new byte[] {...}` → `[...]`（6 个 [1..6]/[6..1] + 1 个全零改 `new byte[6]`，见 disposition） |
| UseUtf8StringLiteral | 6 | 6 | 0 | 全零数组 `[0,0,...]` → `new byte[N]`（见 disposition-log 决策；jb 定向重跑 0） |
| ConvertToAutoPropertyWhenPossible | 4 | 4 | 0 | BoundedSetupQueue.Bytes、UdpProxyCoordinator.Capacity、FlowDispatcherTests.HandleCount、ScriptedReader.Calls |
| ConvertToAutoPropertyWithPrivateSetter | 3 | 3 | 0 | PacketLease.Disposition、TcpProxyRelay.EndKind、InterceptionHealthMonitor.CounterWindow.Count（无 ref/volatile/Interlocked 依赖，已逐点核查） |
| PropertyCanBeMadeInitOnly.Local | 1 | 1 | 0 | TcpRelayEndResetTests 的假 relay：写入全在对象初始化器 |
| ConvertClosureToMethodGroup | 2 | 2 | 0 | `rented => observed.Add(rented)` → `observed.Add` |
| MergeIntoPattern | 1 | 1 | 0 | `Count == 1 && [0].IsDisposed` → `is [{ IsDisposed: true }]` |
| MergeIntoLogicalPattern | 2 | 2 | 0 | `x < a || x > b` → `x is < a or > b`（常量边界） |
| RawStringCanBeSimplified | 1 | 1 | 0 | 多行 raw `"""{}"""` → `"{}"` |
| VariableHidesOuterVariable | 1 | 1 | 0 | 内层 lambda 参数 `index` → `offset` |
| UseDeconstruction | 2 | 2 | 0 | `var (id, addresses) = adapters[i]`；`var (_, _, originalClient, originalServer, _, _, _) = packet.Context.Key`（FlowKey 7 元，discard 形式；scoped jb 实测通过） |
| ConvertToConstant.Local | 6 | 6 | 0 | `const nint finalA/finalB/originHandle×4/fallbackHandle`（移除相应 `(nint)` 常量转换） |
| ConvertIfStatementToReturnStatement | 18 | 3 | 15 | 采纳：SoakOptions（roll 链末支）、Policy（终检正形式）、TcpRedirectSetup（`address is not null ? ... : (IPAddressValue?)null`）；其余 15 为 guard+throw/副作用条件/长行——见 disposition-log「留 B4 抑制」清单 |
| InlineTemporaryVariable | 18 | 16 | 2 | 采纳：`is { } x` → `is not null` + 直接用 readonly 字段/局部（15 处，含 TcpRedirectSetup raw）+ FlowTable `_expiredScratch` 别名内联 + IPTcpUdpPacket `ipOffset` 内联；跳过 2 = NativeBufferPoolTests 的 `copy`/`stale`（别名拷贝是测试断言对象本身，内联会抹掉测试场景） |

### 过程中发现并处置的新增/连带项

- `MergeConditionalExpression` **3 处新触发**（Clean、baseline 0）：`x is not null ? x() : default` 形式被 jb 反建议 `x?.Invoke() ?? default` → 已按 `?.Invoke() ??` 形式改写（NdisCaptureGeneration.Pumps、RuntimeHeartbeat.usage/ReadGcSnapshot），scoped jb 归零。
- S108（empty block）：RedundantJumpStatement 移除后 2 个空 catch 触发了 SonarAnalyzer S108 → 按仓库既有形态（注释填充）修复。
- TcpRedirectSetup 的 UseDeconstruction + InlineTemporaryVariable + ConvertIf 三规则同一站点叠加：最终形态 `return address is not null ? IPAddressValue.From(address) : (IPAddressValue?)null;`（`is { } raw` 形式会重新触发 InlineTemporaryVariable；`?: null` 无标注会 CS8625）。

### 定向验证（scoped jb，`--no-build --include=...`）

- 全部被修复规则的定向重跑均 0（逐规则归零）；残留仅上述设计内跳过项：ConvertIf 1 处（FlowDispatcher:170）、Inline 2 处（NativeBufferPoolTests copy/stale）。
- 纳入 scoped 复核的文件集合见上表；未纳入 B1 的规则（ArrangeObjectCreationWhenTypeNotEvident/InvertIf/MemberCanBePrivate/ReplaceWithPrimaryConstructorParameter 等）维持基线。

### 门禁

| 项 | 命令 | 结果 |
|---|---|---|
| Release build | `dotnet build WinForward.slnx -c Release --no-restore` | 0 Warning / 0 Error（每步后执行） |
| 定向 dotnet format（规则 6/7/8 敏感面） | `dotnet format WinForward.slnx --no-restore --severity info --verify-no-changes --diagnostics IDE0300 IDE0301 IDE0305 IDE0230 RCS1118 IDE0028 IDE0017` | exit 0 |
| Release test | `dotnet test WinForward.slnx -c Release --no-restore`（+ 7 次连跑 `--no-build`） | 725/725；**1 次偶发失败**：首跑（含 rebuild）`StartupScopeResolutionFailurePropagatesFailClosed` 出现 TCE/IOE 取消竞态；随后 7 次连跑全绿、单测重跑全绿。**基线对照**：`/tmp/wf-base`（74cbb23 独立 worktree）全量套件 13 次中同样复现 1 次 724/725（同族取消竞态），单测 100 次 0 失败 → 判定为**既有低概率 flake（负载/时序相关），非 B1 引入** |
| 全量 dotnet format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 见「B1 收尾」 |
| 全量 jb 重跑核对 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/b1-full.xml WinForward.slnx` | 见「B1 收尾」 |

### B1 收尾（全量核对）

（本节由全量重跑后补记。）

### B1 收尾（全量核对，主会话补记 2026-09-20）

| 项 | 命令 | 结果 |
|---|---|---|
| 全量 jb 重跑 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/b1-full.xml WinForward.slnx` | exit 0；**1317 → 829**（-488） |
| 全量 dotnet format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0**（空输出，既有门禁保持） |

- 20 条规则完全归零（含 RedundantCast 280、RedundantUsingDirective 79、RedundantNameQualifier 45、UseCollectionExpression 7、UseUtf8StringLiteral 6…）；**无新增规则**。
- 预期残留：ConvertIfStatementToReturnStatement 15（留 B4 抑制）、InlineTemporaryVariable 2（留 B4 抑制）。
- 良性重分类/连带减少：InconsistentNaming 37→27（B1 编辑顺带消除了 10 个被点名符号）；ForeachCanBeConverted* 两规则间重分类 1 处（净量不变）。
- 另：B1 代理发现 `StartupScopeResolutionFailurePropagatesFailClosed` 偶发（TCE/IOE 取消竞态），经 base 74cbb23 独立 worktree 对照（13 次 1 复现）判定为**既有低概率 flake**，非本批次引入。

## B2 — B 类未使用代码家族（2026-09-20，实现子代理；**达到 150 轮上限中止**，主会话按 jb 重跑重建记录）

状态重建（全量 jb 重跑 /tmp/b2-state.xml，829 → **798**）：

| 规则 | B1 后 | B2 后 | 处置 |
|---|---|---|---|
| UnusedVariable | 5 | 0 | FIX（全部） |
| UnusedParameter.Global | 5 | 0 | FIX（全部） |
| UnusedType.Global | 2 | 0 | FIX：`BarrierListenerFactory` 经全仓 grep 零引用证实后**删除**（含同文件 CollectionNeverQueried 一并清零）；另一处同类删除 |
| CollectionNeverQueried.Global | 1 | 0 | FIX（同上） |
| UnusedMember.Local | 1 | 0 | FIX |
| UnusedMember.Global | 8 | 2 | FIX 6 处 |
| UnusedAutoPropertyAccessor.Global | 15 | 11 | FIX 4 处 |
| NotAccessedPositionalProperty.Global | 9 | 5 | FIX 4 处 |
| MemberCanBePrivate.Global | 70 | 69 | 仅 1 处 FIX 后中止（代理报告"开始批量核查各成员用法"即到上限） |
| 连带 | — | — | InconsistentlySynchronizedField 2→1、RedundantArgumentDefaultValue 6→5（编辑顺带消除） |

**未完成（留 B2b 续派）**：MemberCanBePrivate.Global 69 + .Local 2、UnusedAutoPropertyAccessor.Global 11、NotAccessedPositionalProperty(.Global 5 + .Local 11)、UnusedMember.Global 2、UnusedMethodReturnValue.Global 1、UnusedMemberInSuper.Global 1（≈102 站点）。

**教训记录**：本批代理未按协议逐规则写日志（上下文耗尽时全部记录丢失，靠主会话重跑重建）；后续批次改为更小 scope + 明确"每完成一规则立即落盘"。

### B2b 事故与修复记录（主会话，2026-09-20）

**事故**：主会话用 Python 脚本批量应用 MemberCanBePrivate（71 站点）时出现两类缺陷：
1. 正则未按"成员类型"区分——`Accessor 'X.init'` 的成员名解析成 "init"，导致 SoakOptions 7 个属性被**整体**误改 `private`（正确形态应为 `{ get; private init; }`）、`Constructor 'IPPrefix'` 误改到**类型声明行**（`private readonly record struct`）、MacAddress 误改 `From`（应为 `TryFrom`）。
2. 行尾处理缺陷——替换行丢失 `\n`，造成多文件**行合并**（如 `...; public int DurationSeconds...` 挤一行）；且 `csharp_preserve_single_line_statements = true` 使 whitespace fixer 不会自动拆分同槽声明。

**修复**：
- 用 `git diff --unified=0` 精确识别"纯可访问性翻转"并逐条逆转（30 处）；
- 行合并类交给 `dotnet format whitespace`（自动按格式规则拆行，exit 0）；
- 剩余合并对（SoakOptions/InterceptionHealthMonitor/DurableCaptureBundle/UnicastAddressInventory）逐处 Edit 拆行 + 关键字修正；SoakOptions/IPPrefix 恢复原状；
- **PacketChecksums**：jb 建议 `TryRewriteUdpEndpoints(IPAddress…)` 重载私有化——但该重载是 **tests 经 IVT 使用的协议 oracle**（doc 注释明示 "no production caller… kept as the independent protocol oracle"），私有化即触发 S1144/RCS1213+友元不可达 → **恢复 public**（jb MemberCanBePrivate 对 IVT 友元用量是盲区，记入误报清单，后续抑制）；
- BOM 状态全部核对与 HEAD 一致。

**教训（写入 spec）**：批量机械化编辑必须（a）按成员**类型**定位（ctor/accessor/type 各不同）、（b）保全行尾、（c）每步立即 build+format verify 兜底；（d）`preserve_single_line_statements` 下同槽声明不会被 fixer 拆分，需显式检测 `;` + 声明关键字同槽模式。

## B2c — 未使用家族收尾（实现子代理，2026-09-20）

范围：/tmp/sup-state.xml（366 条）中的 MemberCanBePrivate.Global 45 + .Local 2、UnusedMember.Global 2、UnusedMethodReturnValue.Global 1、UnusedMemberInSuper.Global 1、NotResolvedInText 1（52 站点）。

| 规则 | 站点 | 处置 |
|---|---|---|
| MemberCanBePrivate.Global | 45 | FIX 45。按成员**类型**逐站点：const/static readonly 字段 → `private`（EchoReceiver.Size、GcSoakScenario×3、ConfigurationModels×5、NativeBufferPool.DefaultCapacity、NdisCapture.TransientRetryMaxAttempts、NdisPacketBufferPool/Socks5AddressCache.DefaultCapacity、LayeredCaptureRunner×4、Socks5AddressCache、ClientResetInjector.s_capacityResetCooldownWindow、TcpPendingSynSetup.DefaultGlobalByteBudget、TcpProxyRelay.PumpBufferSize、FrameBuilders.TcpFlagPshAck）；普通属性 → `private`（IPPrefix.PrefixLength、NativeBufferPool.Capacity、Socks5State.GreetingNoCredentials、UdpProxySession.ClientMac）；**Accessor** → 仅访问器私有（SoakOptions 14× `{ get; private init; }`、NdisCapturedPacket.Flags.init）；**Constructor** → 仅改 ctor 行（IPPrefix）；方法 → `private`（MacAddress.TryFrom、PacketChecksums.TryRewriteUdpEndpoints(IPAddressValue…)——其 public(IPAddress) 包装保留原有 pragma，无需新增抑制） |
| MemberCanBePrivate.Local | 2 | FIX（LoopbackSocks5UdpServer.RelayEndpoint、AdapterLocalAddressProviderCacheTests 的 CacheHarness.Provider） |
| UnusedMember.Global | 2 | DELETE（NdisApiAbi.GetDriverVersion / ReadPacket：全仓 grep + spec 零引用，非 ABI 必需；`EthernetRequest` 仍被 SendPacketToMstcp/Adapter 使用而保留） |
| UnusedMethodReturnValue.Global | 1 | FIX：RuntimeCounters.Add `long` → `void`（唯一调用方 RuntimeCountersTests 三处全部忽略返回值） |
| UnusedMemberInSuper.Global | 1 | SUPPRESS-LOCAL：IRuntimeLogger.Trace（级别对等契约；ConsoleRuntimeLogger.Trace 被 RuntimeLoggingTests 覆盖，删除接口成员只会把 NullRuntimeLogger/RecordingLogger.Trace 变成新的孤立未用成员） |
| NotResolvedInText | 1 | SUPPRESS-LOCAL：FlowDispatcher `"packet.Lease"` member-path paramName（与既有 MA0015/S3928/CA2208 叙事同一刻意模式） |

**命名门禁连带改名**（private static 字段必须 `s_camelCase`）：RuntimeHeartbeat.DefaultInterval → `s_defaultInterval`；SetupExecutor.DefaultWorkerCount → `s_defaultWorkerCount`（`<see cref>` 同步）。SoakOptions 的 `with`/初始化全部在类型内 → `{ get; private init; }` 构建验证可用。

**构建**：每批 `dotnet build WinForward.slnx -c Release --no-restore` 0 Warning / 0 Error（3 次批次构建）；**无 CS0122/CS0117（IVT 盲区未实际命中，无需还原站点）**。

**格式门禁**：全部编辑后 `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → **exit 0**（含首次私有化触碰的 IDE1006 命名面：RuntimeHeartbeat/SetupExecutor 两个 static 字段已改 `s_camelCase`）。BOM 抽查（git diff）无 U+FEFF 变化。

**spec 同步**（2 处成员提及）：`hot-path.md` DefaultWorkerCount → `s_defaultWorkerCount`；`windows-ndisapi.md` `MaxConsecutiveStartupRecoveries` 括注 "internal const" → 私有化说明。

**全量 jb 重跑核对**：见「B2c 收尾」。

### B2c 收尾（全量核对，2026-09-20）

| 项 | 命令 | 结果 |
|---|---|---|
| 全量 jb 重跑 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/b2c-full.xml WinForward.slnx` | exit 0；**366 → 313（-53）**，本批 6 规则全部归零（MemberCanBePrivate.Global 45→0、.Local 2→0、UnusedMember.Global 2→0、UnusedMemberInSuper.Global 1→0、UnusedMethodReturnValue.Global 1→0、NotResolvedInText 1→0）；**无新增规则、无规则计数上升** |
| 差额说明 | — | 预期 -52，实际 -53：额外 1 条 `PreferConcreteValueOverDefault`（SetupExecutor.cs:43 `_cancellationToken = default`）在全量重跑中消失（属 B4 范围）。**scoped 复核（`--no-build --include=**/SetupExecutor.cs`）复现该条**（offset 1723-1730 与旧报告一致）→ 判定为 jb 全量分析的波动/环境噪音（与 PRD 记录的 Roslyn worker 噪声同类），非本批编辑所致；该站点保持触发，留给 B4 处置 |
| Release 测试 | `dotnet test WinForward.slnx -c Release --no-restore` | exit 0，**725/725 全绿**（0 failed / 0 skipped） |
| 格式门禁（复核） | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`（全部编辑后二跑，含 blank-line 恢复） | **exit 0** |
| 时序核对 | `find src tests benchmarks -name '*.cs' -newermt '05:51'` | 空 —— 全量 jb 分析窗口（~05:51 起）之后无任何 .cs 编辑 → 313 条结果即最终代码状态 |

**B2c 结论**：本批 6 规则全部归零；总计数 366 → 313（-53，含 1 条 jb 波动项）；无新增规则、无计数上升；三工具门禁（build 0 warning / tests 725 / format exit 0）全绿。无新增抑制以外的 pragma：仅 2 处本地抑制（IRuntimeLogger.Trace、FlowDispatcher NotResolvedInText），另有 1 处既有 pragma（PacketChecksums IPAddress 重载）不变。


## B3a — C 类闭包/释放正确性（AccessToDisposedClosure 64 + AccessToModifiedClosure 9；实现子代理，2026-09-20）

**报告口径校正**：派单提到的 `/tmp/sup-state.xml` 实为 **366 条**状态（B2c 前，04:47 生成，Offset/Line 已与当前树失配）；与当前工作树一致的 313 条报告是 **`/tmp/b2c-full.xml`**（05:52，B2c 收尾全量重跑）。本批一律以 `b2c-full.xml` 的 Offset 定位站点（19 文件 / 73 站点：AccessToDisposedClosure 64 + AccessToModifiedClosure 9）。

### 逐站点结论

- **全部 73 站点 = SUPPRESS-LOCAL（0 个 FIX）**。逐站点精读后无一处属于"闭包确实可能在释放后执行"：
  - **结构性安全（tests，61 站点）**：闭包在同作用域内被 `await`/`Task.WhenAll` 汇合后才离开 using 作用域（`WaitForAsync` 轮询、`Task.Run` + `await`、`barrier.SignalAndWait` + `WhenAll`、`cts.Cancel` 后 await run）；`CaptureRunnerHarness.DisposeAsync` 自身先 `Cancel` 再 `await RunTask` 才释放 ChangeSource/Cancel，测试体退出即完成 drain。
  - **刻意模式（tests，8 站点）**：`NdisAdapterListWatcherTests.DisposeUnblocksAParkedWaitOne`（Dispose 即解阻塞机制）、`FakeAdapterListChangeSource` 的 cancel-on-dispose（helper XML doc 明示 Dispose→Cancel 释放 parked waiter）、ScriptedReader 读回调中 `cts.Cancel()`（取消是 pump 正常退出路径）——"dispose/Cancel 与闭包重叠"正是被断言的行为。
  - **src（4 站点，从严核查，全部选抑制+确证执行序）**：
    - `Cli/Program.cs:322`（`shutdown.Cancel()` in OnCancel local function）：onCancel 唯一可达路径是 `Console.CancelKeyPress` 订阅，而 `finally`（L345）在 using 作用域释放 `shutdown` 之前退订 → 顺序路径不可达释放后访问；残余"已派发的并发 Ctrl+C handler"窗口属 .NET 事件模型固有（任何退订都如此），非本次可修结构问题，理由写入行内 pragma 供审计复核。
    - `Runtime/Capture/LayeredCaptureRunner.cs:144/147`（monitorCancellation）：`TeardownAsync` 先 `await monitor`、`await periodicTick`（L459/460）汇合两个捕获任务，`RunAsync` 的 using 才释放；`CancelBestEffortAsync` 只吞 ODE，不会跳过汇合 → 执行序确证。
    - `Cli/Program.cs:217` 与 `NdisCaptureGeneration.cs:103`（AccessToModifiedClosure）：单次赋值早于一切回调（generation/pump 只能在赋值后的 RunAsync/StartAsync 内创建），原注释已述同一事实 → jb 误读（无法建模"赋值先于回调可触发"）。

### 机制与实测（关键经验）

- `// ReSharper disable once <Rule> // 理由` **只压制一条 issue**；同一语句上有 2 条 issue（2 个捕获变量 / 两条规则）时必须用 **disable/restore 对**（实测：4 个多 issue 站点用 pair 形式后清零）。行内 `once` 放在**语句首行**前（续行前放置仅压制"第一条"）。
- 定向验证（scoped jb，`--no-build --include=`19 文件；3 次实验 + 1 次全量核对）：
  | 运行 | 范围 | 结果 |
  |---|---|---|
  | /tmp/b3a-exp.xml | Program.cs/RuntimeCountersTests/TcpEofScenario/PeriodicRefreshTests | 确认 `once` 生效（src 2 站点、barrier 1 站点清零）；发现"一行两 issue 只清一条" |
  | /tmp/b3a-exp2/3.xml | TcpEofScenario + PeriodicRefreshTests | 确认 pair 形式可清"多 issue 语句"；多 `--include` 以 `;` 分隔有效 |
  | **/tmp/b3a-verify1.xml** | **全部 19 文件** | **AccessToDisposedClosure 0 + AccessToModifiedClosure 0**；同批文件内其余规则 16 条全部为 B4 范围既有项，**无新增规则组** |
- 门禁：Release build（`dotnet build WinForward.slnx -c Release --no-restore`）**0 Warning / 0 Error**；BOM 状态核对（157 个改动 .cs 对照 HEAD）**0 失配**。
- 注意：本批在 19 个文件插入 73 行注释（73 站点 = 63 个 `once` 行 + 5 个 pair 站点（10 行 disable/restore）；pair 覆盖 10 条 issue，另 63 条由 `once` 覆盖）→ **这些文件内其它规则站点的 Offset/Line 已整体下移**；后续批次（B4 等）对这些文件的定位必须基于新的 jb 重跑报告，不能沿用旧 Offset。

### B3a 收尾（全量门禁，2026-09-20）

| 项 | 命令 | 结果 |
|---|---|---|
| Release build | `dotnet build WinForward.slnx -c Release --no-restore` | **0 Warning / 0 Error** |
| Release test | `dotnet test WinForward.slnx -c Release --no-restore` | **725/725 全绿 × 5 连跑**（第 1 次跑出现 1 次 `NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes`：Expected 0 / Actual 1840 字节；该测试单跑 5/5 通过、随后全量 5 连跑全部通过；本批对该文件只增注释行（diff 复核），与"零分配门禁为已知环境敏感项"一致 → **判定为环境性 flake（冷 JIT/并行负载下的分配计数噪声），非本批引入**） |
| dotnet format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0** |
| 全量 jb 重跑 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/b3a-final2.xml WinForward.slnx`（冻结树；前一次 /tmp/b3a-final.xml 同值） | exit 0；**313 → 241**；`AccessToDisposedClosure 64→0`、`AccessToModifiedClosure 9→0`；**无规则计数上升**，唯一 +1 为 B2c 收尾已记录的 jb 波动项 `PreferConcreteValueOverDefault`（SetupExecutor:43，scoped 可复现/全量间跳变，属 B4 范围）。冻结前对 RefreshTests 的一条 pragma 理由文本做了措辞修正（仅注释），改后 scoped jb 复核该文件 0 issue、build/format 门禁复跑仍全绿 |
| BOM | 157 个改动 .cs 对照 HEAD | 0 失配 |

**B3a 结论**：两条规则 73 站点全部归零（73 SUPPRESS-LOCAL / 0 FIX，逐站点理由见行内 pragma 与 disposition-log B3a 表）；三工具门禁全绿（build 0 warning、tests 725、format exit 0）。

## B3b — nullable 家族 + 释放类 + 小项正确性（实现子代理，2026-09-20）

**报告口径**：`/tmp/b3a-final2.xml`（241 条，Offset/Line 已实测与当前工作树一致——所有站点 RANGE 文本逐条核对通过）。批内逐规则推进，每规则后 Release build 0 警告；每完成一规则即落盘本表与 disposition-log。

### 1. RedundantSuppressNullableWarningExpression（31 站点）— FIX 31 / SUPPRESS 0

全部为逐站点确认「`!` 所压制的警告在当前标注下不再存在」的恒等删除（`!` 是编译期注解，删除不产生运行时代码差异）。三类站点：

- `(IPEndPoint)x.LocalEndpoint!` ×12（benchmarks 9 + tests 3）：`Socket.LocalEndpoint` 声明为 `EndPoint?`，但**显式 cast 到非空引用类型**不产生 CS8600（Roslyn 只在赋值目标为可空上下文时警告）——删除后 build 0 警告实测通过，jb 判定成立。
- xUnit `Assert.NotNull(x)` 后的 `x!` ×12（ConfigurationValidationTests 11 + ConfigurationAssert 1）：xUnit v3 `Assert.NotNull` 带 `[NotNull]` 流分析标注，删除后编译器仍窄化为非空 → 0 警告。
- src/tests 其余 7 站点：
  - `FlowDispatcher.InspectionSpan`（src L47）`Lease!.Frame.Span`：`Lease` 是 record struct 的非空声明成员（`PacketLease Lease`），`!` 纯冗余（doc 已声明 requires non-null，注释无需改）。
  - `UdpProxyCoordinator.Send.cs:71` `readySlot!, readySession!`：两者在 `if (slot.Ready)` 分支赋值、else 分支 return，L71 处编译期已确定非空。
  - `EchoReceiver`/`LoopbackSocks5UdpServer`/`UdpRawBaselineScenario` 的 `result.RemoteEndPoint!` ×4：`Socks5UdpReceiveResult.RemoteEndPoint` 声明非空（接收结果构造已拒绝 null）。
  - `TcpProxyCoordinatorRewriteTests:31` `claimed!.LastActivityUtc`：前一行 `Assert.True(...out var claimed...)` 建立非空流状态。
  - `TcpProxyRelayTests`/`TcpRelayEndResetTests`/`TcpRelayObservationTests` 的 `((IPEndPoint)x.LocalEndpoint!).Port`：同 cast 类。

**验证**：`dotnet build WinForward.slnx -c Release --no-restore` → 0 Warning / 0 Error（无 CS86xx/TWAEs 触发，**无需任何站点回退或 pragma**）。

### 2. nullable 条件家族（ConditionIsAlwaysTrueOrFalse 10 / ConditionalAccessQualifier 2 / NullCoalescing 1）— FIX 4 / SUPPRESS 9

- **SUPPRESS ×9（ConditionIsAlwaysTrueOrFalseOnNullableAPIContract）**：全部为 `if (packet.Lease is null) CapturedFlowPacketGuards.ThrowLeaseRequired();` 集中式 fail-closed 边界守卫（FlowDispatcher×3、NdisPacketActionExecutor×1、TcpProxyCoordinator×5）。`Lease` 声明为非空，但 `CapturedFlowPacket` 是 struct，default/未赋 lease 的实例在运行时可到达（FlowDispatcher L50-64 的 doc 明确此设计：守卫让 `ArgumentNullException.ParamName` 指向真正为 null 的成员）。逐站点行内 pragma + 理由（引用 quality-guidelines.md 的集中守卫叙事）。
- **FIX ×4**：
  - `InterceptionHealthMonitor.ReportFailure`：`string? fired` 声明（唯一赋值在锁内最后一段，所有提前返回都在赋值之前）→ 改 `string fired;` 并删 `if (fired is not null)`（jb 判定 always true 正确；等价简化）。
  - `GcSoakScenario` TcpFlood.Stop：`thread?.Join(...)` → `thread.Join(...)`（`_threads` 为 `Thread[]`，构造器全量填充；同类 UdpFlood L485 早已是无 `?.` 形态）。
  - `NdisCapture.ReleaseBatchBuffers`：`buffer?.Dispose()` → `buffer.Dispose()`（`_batchBuffers` 为 `NdisPacketBuffer[]`，构造器全量填充、运行期无置空）。
  - `RuntimeLogging.FormatValue`：删 `IFormattable.ToString(...) ?? string.Empty` 的 `??`（BCL 标注为非空返回；实现方违约返回 null 属标注外行为，且调用点 `Event` 已有 try/catch 兜底）。保留 `_ => value.ToString() ?? string.Empty`（`object.ToString()` 标注为可空，非本规则范围）。

### 3. ConvertTypeCheckPatternToNullCheck（9 站点）— FIX 9 / SUPPRESS 0

站点全部为可空值类型（`long?`/`int?`/`uint?`）上的类型模式——对值类型可空包装，`x is T y` 与 `x is { } y`（空属性模式 + 绑定）完全等价，`x is not T y` 与 `is not { } y` 等价，`x is not T`（无绑定）与 `is not null` 等价。逐点确认无子类型/装箱语义差异（T 均为被包装的值类型），改写未改变任何控制流：

| 文件:行 | 原形态 | 改写 |
|---|---|---|
| benchmarks/Stability/UdpBurstScenario.cs:196 | `TryGetBurstFirstResponseTicks(flow) is long responseTicks` | `is { } responseTicks` |
| tests/TestHelpers/TcpCoordinatorFakes.cs:297,305 | `throwOnCall is int call` | `is { } call` |
| src/TcpRedirect/ClientResetInjector.cs:46 | `ClientInitialSeq is not uint clientInitialSeq \|\| ServerInitialSeq is not uint serverInitialSeq` | `is not { } …`（绑定保留，后续 `+ 1` 使用） |
| src/TcpRedirect/ClientResetInjector.cs:128 | `ClientInitialSeq is not uint \|\| ServerInitialSeq is not uint` | `is null \|\| … is null` |
| src/TcpRedirect/TcpRedirectTable.cs:129,138 | `_clientNextSeq is not uint current` / `_serverNextSeq is not uint current` | `is not { } current` |

### 4. DisposeOnUsingVariable（11 站点）— FIX 1 / SUPPRESS 10

- **FIX ×1**：`TcpProxyRelayTests:51` 的 `local.Dispose()`（`using var local` 已在同方法尾释放；该显式 dispose 无断言价值，属重复）→ 删除；`relayLocal.Dispose()` 保留（未被 using 覆盖）。
- **SUPPRESS ×10（显式 dispose 即被测行为/编排）**：
  - `CaptureLifecycleTests:80`（dispose 后立即断言 Closed/捕获的 teardown）、`NdisPacketBufferPoolTests:129`（dispose 后断言 `ObjectDisposedException`）、`TcpPendingSynSetupTests:260`（dispose 后断言 pending 计数/字节归零）、`TcpProxyCoordinatorLifecycleTests:399`（dispose 必须及时终结持续抛错的 accept 循环，否则测试挂起）、`HighResolutionTimerScopeTests:19/33/60`（dispose 后断言 End 记录/不记录）、`FlowDispatcherTests:130/132`（同一 key 双 lease 的 dispose 顺序与 owned→released 转换就是场景）、`NdisCapturePumpTests:335`（DisposeAsync 与 parked 读的刻意重叠编排）。
  - `using`/`await using` 在这些站点只是断言失败路径的兜底；逐站点 pragma 理由已注明断言对象。

**验证**：Release build 0 Warning / 0 Error（规则 2/3/4 各一次）；scoped jb（规则 1+2 的 22 文件，`--no-build --include`）→ **0 issue**（见下「scoped 复核」）。

### 5. 小项正确性（14 规则 / 31 站点）— FIX 26 / SUPPRESS 5

逐站点结论（站点号见 disposition-log 同节）：

| 规则 | 站点 | 处置 |
|---|---|---|
| HeuristicUnreachableCode 2 + CSharpWarnings::CS0162 2 | UdpBurstScenario.cs:50、UdpLossScenario.cs:38 | SUPPRESS（2 站点 × 2 规则，disable/restore 对）。两站点是同一形态：`private const bool CaptureProductEvents = false` 的**文档化 opt-in 诊断开关**（XML doc 明确"enabling it makes the product emit per-datagram trace events…default runs stay undistorted"）。先核实 Debug/Release 差异：Release build 0 警告（编译器对该三元不产生 CS0162），jb 以 Debug 分析并自建"常量折叠后分支不可达"模型 → 判定非死代码，行内 pragma + 理由。**注意**：jb 的 CS0162 属 jb 合成项（同一常量三元在 Roslyn 两个配置下都不报）。 |
| InconsistentlySynchronizedField 1 | TcpCoordinatorFakes.cs:335 | FIX：`GatedListenerFactory.Listeners` 改为 `lock (_listeners) return [.. _listeners];`（与 CreateAsync 的 `lock (_listeners) _listeners.Add(...)` 一致；同仓 RecordingRuntimeLogger.Events/Lines 同款快照形态）。全部 20+ 使用点均为即时断言读取，无长引用语义依赖 |
| EqualExpressionComparison 1 | NdisAdapterGateMapTests.cs:14 | FIX：`Assert.True(ReferenceEquals(map.Get(1), map.Get(1)))` → `var gate = map.Get(1); Assert.Same(gate, map.Get(1));`（同一"同 handle 同 gate 实例"断言，改用 xUnit 惯用形态；两次调用语义保留） |
| SuspiciousTypeConversion.Global 1 | ProcessAttribution.cs:183 | **FIX（真缺陷，本批唯一行为纠正）**：`row.Address`（`System.Net.IPAddress`）`.Equals(local.Address)`（`WinForward.Core.IPAddressValue`）**恒为 false**——实测探针（/tmp/probe，引用 Release 产物）：`ip.Equals(endpoint.Address)` = False、`ip.Equals((object)endpoint.Address)` = False。后果：`IPHelperTables.FindUdpOwner` 只可能命中 wildcard 分支，具体地址绑定的 UDP socket 永远无法归因。修复：`var localAddress = local.Address;` + `IPAddressValue.From(row.Address).Equals(localAddress)`（`From` 为 stackalloc/TryWriteBytes，无堆分配；同文件 `FindTcpOwner` 用 Endpoint 两侧 IPAddressValue 本就正确，修复后两侧一致）。修复理由与风险已在行内注释与 disposition-log 记录 |
| RedundantArgumentDefaultValue 5 | IPAddressValue.cs:40、IPPrefix.cs:70、NdisPacketActionExecutorBatchingTests.cs:72、UdpProxyCoordinatorTests.cs:174×2 | FIX（删显式 `0`/`TransportProtocol.Tcp`/`0,0` 参数，全部等于形参默认值） |
| TryStatementsCanBeMerged 1 | TcpRedirectSessionStore.cs:290 | FIX：嵌套 `try { try { … } catch (ObjectDisposedException) } finally { … }` → 单层 `try/catch/finally`（catch 先于 finally 执行，语义等价；注释一并展开） |
| SwitchStatementMissingSomeEnumCasesNoDefault 2 | FlowDispatcher.cs:388（PacketAction）、TcpEofScenario.cs:150（AbortKind） | FIX：分别补 `case PacketAction.None:`（= 调用方自行 complete lease、无 executor 动作的 reverse/fragment 路径；显式 no-op 语义，行为不变）与 `case AbortKind.Clean:`（clean 传输无 abort 事件）。两处均为显式枚举覆盖，未加 `default` |
| SwitchStatementHandlesSomeKnownEnumValuesWithDefault 1 | TcpEofScenario.cs:259（TransferOutcome） | SUPPRESS（行内 pragma + 理由）。先尝试把 `Other` 显式化（`case Other:` 叠在 `default:` 上）→ **SonarAnalyzer S3458（empty case clause）构建失败**；该处 `Other` 是枚举的 catch-all 成员、与未来成员共用 default 计数体（计数器名 `_otherErrors`），显式空 case 与 S3458 门禁直接冲突 → 保留原结构 + jb 站点抑制 |
| AsyncMethodWithoutAwait 3 | NdisPacketActionExecutorBatchingTests.cs:87、TcpProxyCoordinatorConcurrencyTests.cs:296、LayeredCaptureRunner.cs:217 | FIX：两个测试改 `void`（纯同步体；xUnit 同步测试）；src 的 `MonitorAsync` 去 `async`，`while(true)` 退出改 `break` + 末尾 `return Task.CompletedTask;`（无 await 的 async 状态机本就是同步执行，异常路径全部被 OCE/ODE catch 吞掉，`Task.Factory.StartNew(...).Unwrap()` 契约不变） |
| CanReplaceCastWithLambdaReturnType 1 | CaptureRunnerFakes.cs:209 | FIX：删 `(IReadOnlyList<Runtime.RuntimeLogField>)` 转换——`entry.Fields` 为 `RuntimeLogField[]`，数组到接口的隐式引用转换使集合表达式目标类型仍成立（构建验证） |
| CanSimplifyDictionaryTryGetValueWithGetValueOrDefault 2 | StabilityShared.cs:47、RuntimeHeartbeat.cs:172 | FIX：`TryGetValue(...) ? x : 0` → `GetValueOrDefault(...)`（`IReadOnlyDictionary`/`Dictionary` 扩展，缺省 0 与改写前一致；RuntimeHeartbeat 为心跳间隔路径非每包热路径） |
| ParameterOnlyUsedForPreconditionCheck.Local 7 | GcSoakScenario:206、Domain:34、RuntimeDiagnosticLoggingTests:196、TcpFragmentHandlingTests:158、CaptureLifecycleFakes:17×2、TcpCoordinatorFakes:266 | SUPPRESS（行内 pragma + 每站点具体理由）：5 处为 UI 测试失败注入 seam / vacuity 守卫（断言"场景真的跑过"）/ 公共边界契约（Endpoint 家族-地址一致性校验）；1 处为 jb 把 xUnit 断言谓词误判为 precondition check（lambda 参数就是断言输入）。CaptureLifecycleFakes 同行为两 issue → disable/restore 对 |
| PreferConcreteValueOverDefault 1 | SetupExecutor.cs:43 | FIX：`_cancellationToken = default` → `CancellationToken.None`（等价形态；B2c/B3a 记录的 jb 波动项就此落地） |

**验证**：Release build 0 Warning / 0 Error（含 S3458 冲突处置后复建）；scoped jb（23 文件）结果见下节。

#### 5-修正（scoped3 复核后）：jb 枚举 switch 规则语义实证 + 2 处二次修正

**jb 探针实验**（`/tmp/swprobe`：11 个最小 switch 形态的小工程，jb 分析 ~40s）——确定了两条 jb 规则的精确语义与可行形态：

| 形态 | jb 结果 |
|---|---|
| 全枚举值显式 + 无 default | `SwitchStatementHandlesSomeKnownEnumValuesWithDefault`："Some cases are not processed: **default**"（= 缺 default 子句） |
| 全值 + `default: break;`（含仅注释） | 同上消失，但触发 **`RedundantEmptySwitchSection`**（"Redundant empty 'switch' section"） |
| 全值 + `default: throw …` | **完全干净**（无 switch 规则命中） |
| 全值 + `default: return;` | 完全干净 |
| 缺值 + 无 default | `SwitchStatementMissingSomeEnumCasesNoDefault`："…: C, default" |
| 缺值 + default（有体） | `SwitchStatementHandlesSomeKnownEnumValuesWithDefault`："…: C" |
| `// ReSharper disable once RuleA, RuleB`（逗号分隔多 ID） | **两类规则同时被压制**（once 与 disable/restore 对两种形态均实测有效） |

**由此产生的 2 处二次修正**（第一版"显式补 case"把基线 MissingSome 2 条转化成了 HandlesSomeKnown 2 条，净量不变但未归零）：

- `FlowDispatcher.cs` CompleteAsync（PacketAction）：`case None:`（显式 no-op）+ `default: throw new InvalidOperationException($"Unhandled packet action '{action}'.")`。PacketAction 为私有封闭枚举，全调用点传字面值；default 分支今日不可达（零行为变更），仅为"未来新增成员必须显式接线"的 fail-loud 兜底（空 default 会被 jb 的 RedundantEmptySwitchSection 拒绝）。
- `TcpEofScenario.cs` FireEventAsync（AbortKind）：`case Clean:`（显式 no-op）+ 同形 `default: throw`。
- `TcpEofScenario.cs:259`（TransferOutcome）维持 SUPPRESS（`Other` 与未来成员共用 default 计数体；显式 `case Other:` 叠 default 会被 **SonarAnalyzer S3458** 拒绝——实测构建失败——故保留结构 + jb 站点抑制，理由可验证）。

**RCS1218 连带修正**：去 `async` 后 MonitorAsync 的 `while (true) { if (!WaitOne(…)) break; … }` 触发了新规则 `RCS1218`（Simplify code branching，jb 会报告 Roslynator 诊断；基线 0 条）→ 改为 `while (_changeSource.WaitOne(cancellationToken)) _demandGate.Signal();`（RCS1218 期望形态；`WaitOne` 返回 false = change source 被释放/解除阻塞，循环退出语义不变）。

**scoped 复核方法学修正**：`--include` 多次传参为**交集**语义（22 个独立 `--include` 得到空集 → 首轮 scoped"0 issue"无效）；正确形态是**单个 `--include` + `;` 分隔**（B3a 记录一致）。另：`--no-build` 需先把 **Debug** 配置建到最新（jb 默认分析 Debug），否则出现 TcpRedirectTable 成员"Cannot resolve symbol"类伪 CSharpErrors（scoped2 实测 38 条，Debug 重建后 scoped3 归零）。

#### 5-复核（scoped jb，分三轮回读）

| 运行 | 范围 | 结果 |
|---|---|---|
| scoped1（作废） | 22 文件（规则 1+2），22 个独立 `--include` | 0 —— **方法学无效**：多 `--include` 传参为交集语义 → 空集假阴性，已改单 `--include` 分号形式 |
| 正向对照 | `--include=**/FlowDispatcher.cs` | 正确报出该文件既有 3 条 B4 项（含 +2/+3 行号位移与插入注释行数一致）→ 分号/单文件形式可信 |
| scoped2 | 33 文件（规则 1-4），单 `--include` 分号 | 规则 1-4 全部 0；但出现 38 条伪 `CSharpErrors`（"TcpRedirectTable has no constructors"/"Cannot resolve symbol TryResolveByOriginal"…）→ 根因：`--no-build` 使用**过期 Debug 程序集**，本次编辑与 Debug 构建不同步 |
| scoped3 | Debug 重建后，23 文件（规则 5） | 目标规则仅余 2 条——均为本轮"显式补 case"后 jb 的**对偶规则** `SwitchStatementHandlesSomeKnownEnumValuesWithDefault`（"Some cases are not processed: default"，见 5-修正节）；另发现 1 条 **RCS1218**（本轮去 async 引入）与 6 条既有 B4 项 |
| scoped4 | 二次修正后，5 文件（LayeredCaptureRunner/FlowDispatcher/TcpEofScenario/UdpBurst/UdpLoss） | 目标规则全 0；仅余 FlowDispatcher 3 条既有 B4 项（行号位移与插入注释一致） |

**门禁（B3b 全量）**：

| 项 | 结果 |
|---|---|
| Release build（每规则后 + 终局） | 0 Warning / 0 Error |
| Release test | **725/725 全绿**（Failed 0 / Skipped 0） |
| dotnet format（全量，全编辑后） | **exit 0**（空输出） |
| Debug build（jb 分析配置） | 0 Warning / 0 Error |
| BOM 核对（163 个改动 .cs vs HEAD） | 0 失配 |
| 全量 jb | 见「B3b 收尾」 |

### B3b 收尾（全量核对）

（本节由全量重跑后补记。）

### B2 期未记录改动的补充认定（主会话复核，2026-09-20）

- `TcpRedirectAssociation`：移除未使用的 `AdapterContext originAdapter` 构造参数与 `OriginAdapter` 属性（对应当时 UnusedAutoPropertyAccessor/未用参数族），调用点已同步；Release/Debug 构建与 725 测试验证通过 → 认定为 B2 合法成果，予以保留（此前因 B2 代理未留日志而未被记录）。
- 复核同时确认：`TcpRedirectTable.cs` 内容完整（类/构造器/TryClaim/TryResolveByReverse 等成员均在），Release+Debug 构建零错误。

### jb CSharpErrors 38 条事件（2026-09-20）

- 症状：b3b 报告与 10:20 复跑均含 `CSharpErrors 38`（集中于 TcpRedirectTable.cs 及其消费方）+ 连带 `UnusedMember.Global 3`；但 Roslyn Release/Debug 构建、725 测试、format verify 全绿。
- 排查：语法探针（/tmp/patprobe，含 `is not { }` 负模式）jb 解析正常；文件字节级完整性 OK；怀疑 **jb 增量缓存**在 B3b 高频编辑+scoped 运行期间被污染。
- 处置：`--caches-home=/tmp/jbcache-b3b` 全新缓存重跑核对（结果见下）。

### jb CSharpErrors 38 条事件 — 结论（缓存污染，2026-09-20）

- **根因**：jb 默认 caches-home（`~/.local/share/JetBrains/InspectCode`）在 B3b 代理高频编辑 + 多次 scoped 运行期间被污染——对 `TcpRedirectTable.cs` 缓存了破损中间态模型，导致"类无构造器/符号无法解析/成员未使用"共 41 条**伪影**持续出现在默认缓存运行中。
- **证据**：`--caches-home=/tmp/jbcache-b3b` 全新缓存 → **零 CSharpErrors**；语法探针实验排除 `is not { }` 解析问题；文件字节级完整性与双配置构建零错误。
- **处置**：将污染缓存移出（备份 `/tmp/jb-inspectcode-cache-poisoned-backup`），以**原样验收命令**（默认缓存）重跑核对（结果见下）。
- **B3b 真实成果**：241 → **147**（94 条清除：RedundantSuppressNullable 31、ConditionIs 家族 13、ConvertTypeCheck 9、DisposeOnUsing 11、小项 30；另有 1 处真缺陷修复：`IPHelperTables.FindUdpOwner` 的 IPAddress/IPAddressValue 跨类型比较恒 false 的归因 bug）。
- **教训（写入 spec）**：jb 增量缓存对"编辑中态"敏感；代理高频 scoped 运行后，**验收前必须用干净缓存或默认缓存已重建**的状态复核一次，再下结论。

### 缓存污染 — 最终定位与解决（2026-09-20）

- 逐步排除：仅移 `InspectCode` → 仍复现（该目录被重建后仍报错）；**将 `~/.local/share/JetBrains/{InspectCode,Shared,Transient}` 与 `/tmp/JB` 全部备份移走**后，原样验收命令 → **147 条、零 CSharpErrors** ✓。
- 结论：污染位于 `Shared`/`Transient`（或 `/tmp/JB` 影子程序集）层级，被默认路径复用而 `--caches-home` 路径不读。备份保留于 `/tmp/jb-cache-backup-*` 与 `/tmp/jb-shadow-backup`（纯缓存，任务结束后可删）。
- **运维注意（写入 spec/AGENTS）**：jb 对高频编辑后的增量缓存敏感；如遇"真实构建绿但 jb 报 CSharpErrors/连带未用成员"的伪影，清理 `~/.local/share/JetBrains/` 与 `/tmp/JB` 后用原样命令复核。

## B4a — 控制流风格判定批（实现子代理，2026-09-20）

**报告口径**：`/tmp/accept-check2.xml`（147 条，与当前工作树一致、无缓存伪影）。本批范围 7 条规则共 94 站点：
InvertIf 50、ConvertIfStatementToReturnStatement 15、InlineTemporaryVariable 2、UseVerbatimString 9、
ConvertIfStatementToSwitchStatement 5、DuplicatedSequentialIfBodies 5、MoveLocalFunctionAfterJumpStatement 4、
SeparateLocalFunctionsWithJumpStatement 4（ReplaceWithPrimaryConstructorParameter 48 / ArrangeObjectCreationWhenTypeEvident 5 留 B4b）。
全部逐站点编辑（jb 无 CLI fixer）；每规则后 Release build 0 警告；每完成一条规则即落盘本表与 disposition-log。
注意：本批对同一文件的多处编辑会使行号下移，故各规则站点一律按**规则级 Offset**（报告为基于当前树的精确字符区间）定位，不沿用旧行号。

### 1. InvertIf（50）— **FIX 4 / SUPPRESS-GLOBAL 46（92% 拒绝）**

判定标准（写入 .editorconfig 原位注释）：采纳 = 反转后是自然早退路径（尾部空值测试→早退、循环尾守卫→continue、
平台门反转消除重复调用）且不破坏读序；拒绝 = interop guard+throw 惯用法、条件含副作用（Try*Remove/TryMarkRented/
Interlocked/掩码计数）、需取反复合条件、反转造成尾部 return 重复、快路径正向优先约定、同方法未命中守卫的一致性。

| 站点 | 结论 | 理由（逐站点） |
|---|---|---|
| benchmarks/Stability/SoakRunner.cs:9 | **FIX** | `if (OperatingSystem.IsWindows()) {…}` 反转后 `if (!IsWindows()) return await RunScenariosAsync(options);` 消除 if 内外的**重复调用**并去掉一层嵌套；`using var timer` 作用域移到方法级（释放时刻与原 if 块退出等价：都在 await 完成后） |
| benchmarks/Stability/StabilityContext.cs:47 | **FIX** | 方法尾 `if (_fileOutput is not null) {…}` → `if (_fileOutput is null) return;` + 2 条语句出嵌套（简单空值测试，无副作用） |
| Runtime/Capture/NdisPacketActionExecutor.cs:275 | **FIX** | 循环体尾 `if (stale > 0) {…}` → `if (stale == 0) continue;`（同循环 L263/L271 已有 continue 守卫惯用法；去一层嵌套） |
| Runtime/TcpRedirect/TcpRedirectSessionStore.cs:193 | **FIX** | 循环体尾 `if (session.AcceptLoop is not null) { try/catch }` → `if (… is null) continue;`（try/catch 出嵌套） |
| Cli/Program.cs:31、52、77 | SUPPRESS | 平台门/`TryCheck`/`TryValidate` 守卫链：反转需要 `!` 包裹带 out 的 Try* 调用，且与同链中未被点名的同类守卫（L47、L72）产生风格分裂 |
| Configuration/ConfigurationModels.cs:259、342 | SUPPRESS | 校验守卫 + 错误收集惯用法；259 反转需取反范围模式（`is < a or > b` → `is >= a and <= b`） |
| Core/BoundedSetupQueue.cs:122、ProcessSelectors.cs:41 | SUPPRESS | 反转只会把尾部 `return` 复制进守卫（重复语句），嵌套不变 |
| Core/FlowTable.cs:108、NativeBufferPool.cs:77 / NdisApi/NdisPacketBufferPool.cs:67 / Runtime/UdpProxy/UdpAssociations.cs:38 | SUPPRESS | 条件为带副作用的调用/构造器内条件分配：把 `TryRemove`/`TryMarkRented`（CAS）放进取反守卫破坏执行序读序；UdpAssociations 为 ctor 内条件分配（早退会跳过 readonly 字段赋值路径） |
| Core/FlowTable.cs:122、IPPrefix.cs:58 | SUPPRESS | 正向快路径优先（"池中有则复用"/"IPv4 分支"）；反转需引入无收益的比较取反 |
| NdisApi/NdisApiDriver.cs:103、116、129、158、225、238、284、NdisCapture.cs:276 | SUPPRESS | interop 错误码守卫（`== 0 → throw`）+ `degrade/retry` 错误路径：反转把错误构造/降级移入更深层级，破坏 fail-closed 抓读序；158 还需取反 4 参谓词 |
| Protocols/PacketChecksums.cs:313、TcpResetBuilder.cs:57、UdpFrameBuilder.cs:76 | SUPPRESS | 313 = SIMD 热循环内 `++blocks`（副作用条件）；57 需取反三合一 `&&`（类型+双族）；76 为"失败即清零并返回 false"的校验守卫 |
| Runtime/FlowDispatcher.cs:267、InterceptionHealthMonitor.cs:176、SelfTrafficRegistry.cs:41、TcpPendingSynSetup.cs:241/258/332、TcpProxyCoordinator.cs:156/208、TcpRedirectSetup.cs:88、TcpRedirectTable.cs:282、UdpProxyCoordinator.Send.cs:100、UdpProxySession.cs:137、UdpResponseReinjector.cs:198、UdpSetupQueueBudget.cs:49、RuntimeHeartbeat.cs:206 | SUPPRESS | 现有 `guard+early-return`（miss/blocked/loser/drop 路径）反转后要改成正向短路（否定前置）、把 `TryGetValue(out)`/`TryRemove(out)`/`Interlocked.Add`/`LaunchSetup` 等调用搬进条件；`IsCompletedSuccessfully` 快路径族保持"同步完成优先"；RuntimeHeartbeat:206 为方法尾守卫但与同方法未命中的兄弟守卫（:196 gcCollections）保持形态一致 |
| Windows/ProcessAttribution.cs:32 | SUPPRESS | 复合条件 `result is null && s_retryDelay > TimeSpan.Zero`（取反为 `is not null ||` 复合） |
| tests/RuntimeHeartbeatTests.cs:80、102、248、299 | SUPPRESS | `WaitForAsync` 谓词的"扫描-命中即记录并 return true"读序；80/102 为多子句相等断言，取反成 `&&` 复合否定 |
| tests/TestHelpers/UdpTransportFakes.cs:102 | SUPPRESS | 条件为 `Interlocked.Increment(ref _calls) == 1`（副作用计数） |

**机制**：FIX 4 处已改造（build 0 警告）；其余 46 处由规则级 `resharper_invert_if_highlighting = none`
（`.editorconfig` 全局段，中文注释含采纳/拒绝统计与分类理由）替代 46 条站点 pragma——避免逐站点注释噪音。

### 2. ConvertIfStatementToReturnStatement（15）— SUPPRESS-LOCAL 15 / FIX 0

按 B1 既有论证逐站点 pragma（`// ReSharper disable once ConvertIfStatementToReturnStatement // 理由`，置于语句首行前）：

| 分类 | 站点 | 理由 |
|---|---|---|
| guard+throw（10） | Domain.cs:87、IPAddressValue.cs:39/45、NdisApiAbi.cs:174、NdisPacketBuffer.cs:41/78、HighResolutionTimerScopeTests.cs:77、CaptureLifecycleFakes.cs:28 | jb 期望的 `cond ? throw X : value` 形式在仓库无先例，且把失败路径折叠进条件表达式破坏"失败即早退"读序（含 2 处测试失败注入 seam：throw 即被测行为本体） |
| 条件含副作用（4） | FlowDispatcher.cs:172（`Lease.TryComplete` 完成租约 = 状态转移）、Socks5UdpTransport.cs:330（`TryDecode` out 绑定）、NdisCapturePumpTests.cs:187（`TrySetResult` 门控信号）、Socks5UdpDatagrams.cs:15（`TryEncode` out 填充） | 副作用/out 绑定塞进 `?:` 会隐藏"已消费/格式非法/握手已完成"的早退语义 |
| 纯值选择但上下文不宜（1+1） | NdisApiAbi.cs:166、IPAddressValue.cs:104 | 逐点复核过 jb 的 `?:` 形态：166 的 `NativeLibrary.Load(...)` 进三元后单行 >150 字符；104 位于早退级联中段（同方法 IPv4 臂同形态早退），单独三元化破坏方法内形态一致性 → 维持早退 |

**验证**：Release build 0 警告（15 条 pragma 后）。

### 3. InlineTemporaryVariable（2）— SUPPRESS-LOCAL 2 / FIX 0

NativeBufferPoolTests.cs:127（`copy`）/ :277（`stale`）：别名局部即测试断言对象本体（lease 副本幂等释放 / 过期副本 release no-op）；
内联会把场景退化为同一变量连续 dispose，静默抹掉回归覆盖 → 站点 pragma + B1 理由（行内已注明断言对象）。

### 4. UseVerbatimString（9）— FIX 9 / SUPPRESS 0

机械采纳，逐点核对**字符值等价**（普通字面量反斜杠数 → verbatim 反斜杠数一一对应；无 `\uXXXX`、无插值、无内嵌双引号站点；
verbatim 尾反斜杠合法）：

| 站点 | 改写 |
|---|---|
| RuntimeLogging.cs:123 | `"\\\\"`（JSON 转义臂，值 `\\`）→ `@"\\"` |
| ConfigurationValidationTests.cs:148 | `"C:\\\\Apps\\\\browser.exe"` → `@"C:\\Apps\\browser.exe"`（`[InlineData]` 常量实参） |
| RuntimeLoggingTests.cs:108 | `"C:\\Users\\test\\browser.exe"` → `@"C:\Users\test\browser.exe"` |
| RuntimeLoggingTests.cs:119 | `"C:\\Users\\test"` → `@"C:\Users\test"`（`DoesNotContain` 断言） |
| RuntimeLoggingTests.cs:134 | `"C:\\Apps\\browser.exe"` → `@"C:\Apps\browser.exe"` |
| RuntimeLoggingTests.cs:138 | `"processPath=C:\\Apps\\browser.exe"` → `@"processPath=C:\Apps\browser.exe"` |
| WindowsAdapterInventoryTests.cs:33/113/133 | `"\\DEVICE\\"` → `@"\DEVICE\"`（3 处） |

**验证**：Release build 0 警告（9 处改写后）。

### 5. ConvertIfStatementToSwitchStatement（5）— SUPPRESS-LOCAL 5 / FIX 0

逐站点评估"switch 形态是否明显更清晰"→ 全部判定**不更清晰**（故不采纳，站点 pragma + 理由）：

| 站点 | 不采纳理由 |
|---|---|
| ConfigurationModels.cs:240 | 范围模式前置校验：switch 需带关系模式，且掩盖"报错→返回默认值"的 fail-closed 顺序（与下方阈值警告同族） |
| TcpResetBuilder.cs:49 | 每臂同时守卫 etherType + 双方地址族；`switch`+`when` 会把守卫埋进子句（热路径帧构造） |
| CaptureAdapterScopeResolver.cs:151 | 0/1/>1 三态判别：if/else-if/else 直接表达意图；长度 switch 反而绕 |
| NdisPacketActionExecutor.cs:374（报告 376，行号随本批早前编辑下移） | 每分支自带长注释 + await；switch 化还会引入枚举覆盖检查（default 处理）到热路径包处理器 |
| UnicastAddressInventory.cs:156 | 行解析 if/throw 级联：switch 表达式臂会把多行地址构造压成超长单行并隐藏兜底 throw |

注：首版 pragma 注释中的 `case 0`/`default` 等代码样文本触发 **SonarAnalyzer S125**（构建失败）→ 已改写注释措辞（仅注释变化）。

### 6. DuplicatedSequentialIfBodies（5）— FIX 1 / SUPPRESS-LOCAL 4

审查条件副作用后逐点：条件均为纯查询/读取（无副作用），故"是否合并"取决于可读性——**合并仅当合并条件读作单一决策且 ≤2 子句**：

| 站点 | 决定 | 理由 |
|---|---|---|
| IPAddressValue.cs:105/107（报告 103） | **FIX** | 两个早退体完全相同（`return new IPAddress(bytes);`）：合并为 `if (Family == AddressFamilyKind.IPv4 || ScopeId == 0) return new IPAddress(bytes);`（IPv4 无 scope / 零 scope 无需 scope 实参 = 一个决策）；原 ConvertIf 站点 pragma 理由同步更新 |
| Socks5State.cs:119/120 | SUPPRESS | 两个不同解析阶段共用 Invalid 出口（ATYP-3 零长地址=畸形 vs 端口字节缺失=截断）；合并成 `(A && B) || C` 会丢掉逐步校验顺序 |
| FlowDispatcher.cs:158/159 | SUPPRESS | 热路径旁路枚举：每行一个已文档化理由（trace-only 旁路、X1 reverse 认领、self-traffic、未解析流）；合并会把无关谓词耦合成 >150 字符守卫 |
| TcpProxyCoordinator.cs:584/585 | SUPPRESS | 两阶段（分片解析、关联解析）共用 NotRelevant 出口；合并需把 2 个 out 绑定折进一个取反三子句守卫，隐藏"哪种 not-ours 命中" |
| TcpRedirectTable.cs:204/210 | SUPPRESS | fail-closed 认领门：doc 契约明列两个拒绝理由（tuple 已属他键 / 表满）；合并成三子句条件会塌掉该区分 |

### 7. MoveLocalFunctionAfterJumpStatement（4）+ SeparateLocalFunctionsWithJumpStatement（4）— FIX 8 / SUPPRESS 0

**jb 语义实证探针**（`/tmp/jfprobe`，8 个变体，`--caches-home=/tmp/jbcache-jfprobe`）：局部函数须**位于块末尾且其前有显式 `return;`**——
中间/块首声明 → `MoveLocalFunctionAfterJumpStatement`；块尾但无 return → `SeparateLocalFunctionsWithJumpStatement`；块尾 + `return;` → **两条规则均不报**（sync void/async 一致）。

| 站点 | 形态 | 处置 |
|---|---|---|
| HotPathAllocationGateTests.cs:270（`MakePacket`）、:335（`Decide`） | 方法中段声明 | 移至方法末尾 + 前置 `return;`（`MakePacket` 在声明前即被调用——C# 局部函数前向引用合法，构建验证） |
| Socks5AddressCacheTests.cs:27/56（`Resolver`） | 方法中段声明 | 同上（移至末尾 + `return;`） |
| UdpReceiveResilienceTests.cs:67/98/225/264（`Datagram`） | 已在块尾 | 仅补显式 `return;` 于声明前 |

**验证**：Release build 0 警告（**无 SonarAnalyzer S3626 误报**——`return;` 后随局部函数声明不被判冗余）；725/725 测试全绿；`dotnet format --severity info --verify-no-changes` exit 0。

### B4a 收尾（全量门禁 + jb 核对，2026-09-20）

| 项 | 命令 | 结果 |
|---|---|---|
| Release build（逐规则后 + 终局） | `dotnet build WinForward.slnx -c Release --no-restore` | 0 Warning / 0 Error（含 S125 注释措辞修正后复建） |
| Release test | `dotnet test WinForward.slnx -c Release --no-restore` | **725/725 全绿**（Failed 0 / Skipped 0） |
| dotnet format（全量） | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0**（空输出） |
| 全量 jb 重跑 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/b4a-final.xml WinForward.slnx` | **exit 0；147 → 53**；本批 8 条规则**全部归零**（InvertIf 50→0、ConvertIfStatementToReturnStatement 15→0、InlineTemporaryVariable 2→0、UseVerbatimString 9→0、ConvertIfStatementToSwitchStatement 5→0、DuplicatedSequentialIfBodies 5→0、MoveLocalFunctionAfterJumpStatement 4→0、SeparateLocalFunctionsWithJumpStatement 4→0） |
| 残留核对 | 报告 = 53 条 | 恰为本批范围外：`ReplaceWithPrimaryConstructorParameter` 48 + `ArrangeObjectCreationWhenTypeEvident` 5（B4b 范围）；**无新增规则、无计数上升**、无 `CSharpErrors`/连带 `UnusedMember` 伪影（默认缓存直接产出干净报告，无需清理缓存） |
| BOM | 169 个改动 .cs 对照 HEAD 首字节 | 0 失配（本批未触碰任何文件首行；唯一差异项 CaptureLifecycle.cs 属此前批次） |

**机制生效实证**：`.editorconfig` 规则级键 `resharper_invert_if_highlighting = none` 实测生效（50 处 InvertIf 全部从报告中消失，其中 4 处为已改造站点）；26 条站点 pragma（ConvertIf×15、Inline×2、Switch×5、Duplicated×4）全部生效。

**过程记录**：jb 局部函数语义探针（/tmp/jfprobe）确认两条 Move/Separate 规则的精确期望形态（块尾 + 前置 `return;`），避免了逐站点试错；首版 switch pragma 注释因含代码样文本触发 SonarAnalyzer S125 → 改写措辞（仅注释）。全量 jb 本次耗时 ~17 分钟（本机负载下），报告仍为确定性结果。

## B4b — 最后一批：primary ctor 参数化（48）+ 目标类型 new（5）（实现子代理，2026-09-20）

**报告口径**：`/tmp/b4a-final.xml`（53 条 = ReplaceWithPrimaryConstructorParameter 48 + ArrangeObjectCreationWhenTypeEvident 5；B4a 收尾全量产物）。全部 53 站点按报告 `Offset` 在当前工作树上逐条抽取核对（文本完全吻合）后编辑。jb 无 CLI fixer，全部逐站点编辑；每文件编辑后 Release build（`dotnet build WinForward.slnx -c Release --no-restore`）。

### 1. ReplaceWithPrimaryConstructorParameter（48 站点 / 16 文件）— **FIX 48 / SUPPRESS 0**

站点形态完全统一：类已具 primary 构造器（09-19 IDE0290 批已转换），残留 `private readonly T _x = param;` 纯转发字段（无 `??` 回退、无条件赋值）；jb 期望正文直接使用 primary ctor 参数（删除字段）。

**探针实证（/tmp/pcprobe，jbcache 隔离）**：
| 形态 | jb 结果 |
|---|---|
| primary ctor + `private readonly T _x = param;`（正文经 `_x` 使用，含 lambda 捕获） | **报** ReplaceWithPrimaryConstructorParameter |
| primary ctor + `_x = param ?? throw`（条件/校验初始化） | 不报（构造语义敏感站点天然不在报告内） |
| 经典 ctor + 字段（未转 primary） | 报 `ConvertToPrimaryConstructor`（另一检查，本仓 0 条 —— 上次 IDE0290 批已全部转换） |
| 删除字段 + 正文改用参数（修复后） | 该规则消失，无新规则触发；`dotnet build` 0 警告 |

**等价性/性能核查（逐站点）**：
- 语义：primary ctor 参数若被正文引用，编译器生成"每参数 1 个隐藏捕获字段、构造期赋值"——与显式 `_x = param;` 的字段数量/布局/构造顺序（捕获赋值先于显式字段初始化器，本批无依赖该顺序的站点）零差异；隐藏字段不可被用户代码写入，只读性等价。lambda 内使用参数与使用字段同为经 `this` 的字段读取，闭包开销不变（无新增捕获类）。
- 命名门禁：primary ctor 参数为 camelCase 参数（不受 s_/_ 字段命名规则约束）；未重命名任何现存字段，仅删除纯转发字段。
- hot-path 核查：48 站点无一在每包路径——分布为 TCP redirect setup/accept/failure 路径（TcpRedirectSetup/Acceptor/SessionStore/ClientResetInjector/StallWindow）、UDP setup/预算（UdpSessionSetup/UdpSetupQueueBudget，setup 冷边）、日志（ConsoleRuntimeLogger）、bench 夹具（GcSoakScenario.TcpFlood / LoopbackSocks5TcpServer.Connection / UdpBurstInstrumentation / DispatcherBenchmarks）、tests fake（4 文件）。NativeMemoryManager 属"每次新原生分配一次"的冷路径（其 doc 明示）。
- 每文件编辑 → Release build 0 Warning / 0 Error（16 次文件级构建全部通过）。

**逐文件（字段数）**：
| 文件 | 字段 |
|---|---|
| TcpRedirectSetup.cs | 10（listenerFactory/table/selfTraffic/localAddresses/injector/logger/store/clientReset/synCopyPool/timeProvider） |
| UdpSessionSetup.cs | 8（transportFactory/associations/responseSink/timeProvider/logger/receiveWindowPool/receiveBufferSize/host） |
| TcpRedirectAcceptor.cs | 5（relayFactory/logger/clientReset/tryAttachRelay/tearDownSession） |
| ClientResetInjector.cs | 4（injector/logger/tearDownSession/failAssociation；`healthSignal/bufferPool/timeProvider` 为 `??` 回退字段，未被报告） |
| TcpRedirectSessionStore.cs | 3（table/logger/timeProvider） |
| UdpSetupQueueBudget.cs | 3（byteBudget/logger/timeProvider） |
| LoopbackSocks5TcpServer.cs（Connection） | 3（socket/owner/shutdown） |
| GcSoakScenario.cs（TcpFlood） | 2（factory/server） |
| NativeBufferPool.cs（NativeMemoryManager） | 2（pointer/length，`void*` 捕获字段构建通过） |
| TcpCoordinatorFakes.cs（FakeListenerFactory） | 2（fixedTuple/throwOnCreate） |
| UdpBurstInstrumentation.cs / RuntimeLogging.cs / TcpProxyRelay.cs（StallWindow）/ Socks5UdpAssociateTests.cs / CapturePipelineFakes.cs / UdpTransportFakes.cs | 各 1 |

### 2. ArrangeObjectCreationWhenTypeEvident（5 站点 / 4 文件）— **FIX 5 / SUPPRESS 0**

逐点确认目标类型可从上下文推断后机械采纳（与仓库 de facto 目标类型 `new()` 风格一致）：
| 站点 | 上下文（目标类型来源） | 改写 |
|---|---|---|
| DispatcherBenchmarks.cs:56 | `Dictionary<string, Socks5Server>` 索引初始化器 | `new Socks5Server(...)` → `new(...)` |
| AdapterLocalAddressProviderCacheTests.cs:140 | `IReadOnlyList<IPAdapterUnicastInfo>` 集合表达式元素 | `new IPAdapterUnicastInfo(...)` → `new(...)` |
| AdapterLocalAddressProviderCacheTests.cs:146 | 同上 | 同上 |
| ClientResetInjector.cs:38 | `TcpResetCooldownTable` 属性初始化器 | `new TcpResetCooldownTable(...)` → `new(...)` |
| TcpRedirectSessionStore.cs:53 | `TcpRedirectTombstoneTable` 属性初始化器 | `new TcpRedirectTombstoneTable(...)` → `new(...)` |

构建 0 警告；与既有 `resharper_arrange_object_creation_when_type_not_evident` 规则级抑制（type 不明显时保留显式类型）方向互补、无冲突。

### 3. 连带新触发项与处置（scoped jb 首轮发现，2026-09-20）

scoped jb（18 文件）确认两条目标规则全部归零，但暴露 **7 处新触发**（均为本次参数内联的衍生效应，全量 b4a 报告此前无这些规则）：

**(a) MA0038 ×6（"Make method static"，info、作者已弃用）**：内联后以下方法的实例依赖只剩 primary ctor 参数（此前经 `_x` 字段访问），Meziantou 分析器不把捕获参数建模为实例状态 → 建议 static，但加 `static` 读取 primary ctor 参数直接编译失败（CS9105；与 .editorconfig MA0041 条目同族实证）→ **不可修**。处置：**站点 pragma**（`#pragma warning disable/restore MA0038` + 行内理由，5 文件 6 处）——依据 .editorconfig MA0041 条目的既有裁定"src 出现同类命中时按局部 pragma 处置"；继任规则 CA1822 已启用且对这些站点 0 命中（Roslyn 正确建模 primary ctor 捕获），族覆盖不丢失。站点：LoopbackSocks5TcpServer.cs:172（EchoLoopAsync）、TcpRedirectAcceptor.cs:146（ObserveRelayCompletionAsync）、TcpRedirectSessionStore.cs:230（ReleaseRetiredAsync）、TcpRedirectSetup.cs:97（ResolveForwardLocalAddress）/ :165（RegisterSession）、UdpSessionSetup.cs:181（OnSessionActivity）。**后记（同日，用户裁定）**：MA0038 规则级关闭入册后，这 6 处站点 pragma 与配套注释已成为冗余，已全部移除（终态见「配置补记」）。

**(b) ParameterOnlyUsedForPreconditionCheck.Local ×1（jb 专有）**：TcpCoordinatorFakes.FakeListenerFactory 的 `throwOnCreate` 内联后直接进入 `if (throwOnCreate) throw`（此前仅用于字段初始化）→ 判为"参数仅用于前置检查"。处置：`// ReSharper disable once` + 理由，与同文件既有兄弟站点 `FakeRelayFactory.throwOnEstablish`（L264）同一"失败注入 seam"叙事完全一致。

**实证**：`dotnet format analyzers ... --severity info --diagnostics MA0038 --include <5 文件>` → exit 0、0 条（pragma 生效）；scoped jb 复跑（6 文件）结果见下节。

### B4b 定向验证（scoped jb）

- scoped jb（`--include`，`/tmp/b4b-scoped.xml`）确认 `ReplaceWithPrimaryConstructorParameter` 与 `ArrangeObjectCreationWhenTypeEvident` 两条目标规则**全部归零**，同时暴露 **7 处**本次内联衍生触发（MA0038 ×6 + ParameterOnlyUsedForPreconditionCheck.Local ×1：LoopbackSocks5TcpServer.cs:172、TcpRedirectAcceptor.cs:146、TcpRedirectSessionStore.cs:230、TcpRedirectSetup.cs:97/165、UdpSessionSetup.cs:181、TcpCoordinatorFakes.cs:152），均按上节 (a)/(b) 处置。
- 处置后再跑 scoped jb：**0 条**残留（`/tmp/b4b-scoped2.xml` = `<IssueTypes />` + `<Issues />`）；`dotnet format ... --diagnostics MA0038 --include <5 文件>` exit 0、0 条，证明 pragma 生效。
- **口径说明**：`jb inspectcode --include` 在本仓可用（jb 仍会加载并分析整个 solution，仅对 include 的文件出报告，本机耗时 ~16 分钟，与全量接近），因此 scoped 与全量结论一致；终局以 B5/B7 的全量命令为准。

### B5 — 抑制汇总与交叉验证（2026-09-20）

- 抑制清单登记：`research/suppression-inventory.md`（16 个 `.editorconfig` 键 = 2 既有 + 14 新增；128 处代码 pragma 行）。
- `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → **exit 0，空输出**（jb 批次全部改完后复跑，无 RCS/IDE 回退）。
- 全量 jb → 剩余 0 条（见 B7）。

### B6 — 交付（AGENTS.md + CI，2026-09-20）

- `AGENTS.md` Pre-Commit Quality Gate 增补「Second gate — the JetBrains inspector (reports HINT severity and above)」段：命令 `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx`、**零 `<Issue>`** 才可提交、jb 退出码恒 0（必须解析 XML）、修复优先/窄抑制策略、性能与可读性红线、已知误报类、缓存污染处置（`~/.local/share/JetBrains/`、`/tmp/JB`）。
- `.github/workflows/analyzer-gate.yml`（原 `format-gate.yml` 改名）：保留 `format-verify` job，新增 `jb-inspectcode` job（windows-latest、`timeout-minutes: 60`、dotnet 10.0.x、`dotnet tool install --global JetBrains.ReSharper.GlobalTools --version 2026.1.3`、`dotnet restore`、同一 jb 命令输出到 `$RUNNER_TEMP\jb-report.xml`、pwsh 解析 `//Issues/Project/Issue` 计数断言为 0（否则 exit 1）、报告 artifact 上传 `always()`）。工具版本固定 2026.1.3，与本地一致。

### B7 — 终局门（2026-09-20）

| 项 | 命令 | 结果 |
|---|---|---|
| jb 全量 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/final-accept.xml WinForward.slnx` | **零 issue**（`<IssueTypes />` + `<Issues />`，2026-09-20 15:34） |
| 代码冻结核对 | python `os.walk` 对 src/tests/benchmarks（排除 bin/obj）比 mtime > 2026-09-20 15:34 | 无结果（报告后未再编辑代码） |
| dotnet format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0**（空输出，用户确认） |
| Release build | `dotnet build WinForward.slnx -c Release --no-restore` | 0 Warning / 0 Error |
| Release test | `dotnet test WinForward.slnx -c Release --no-restore` | **725/725 全绿** |

**改动规模**：175 路径（含任务目录）/ 174 代码或配置文件；`git diff` +1136/−1070。

**终局独立审计**（check 代理）：见 `research/suppression-audit.md`（含实证方法：worktree 中立化 + 受控微探针 + 全量重跑对账）。

### 配置补记（2026-09-20，用户裁定）

- **MA0038 规则级关闭**：`dotnet_diagnostic.MA0038.severity = none` 重新入册（原 2026-09-19 审计以"0 命中"移除）。用户裁定：MA0038 是已被作者弃用的规则（继任者 CA1822），弃用规则启用无收益，且本仓已因 fixer 在 extension 块/InterpolatedStringHandlerArgument 场景误修而停用该族——规则级关闭是更稳的终态（与 MA0041 同族叙事一致）。已更新审计注释块（MA0038 移出"审计移除"清单）。
- **MA0038 站点 pragma 清理（2026-09-20，同日后续裁定）**：规则级关闭生效后，代码内 6 处 `#pragma warning disable/restore MA0038` 连同配套理由注释（LoopbackSocks5TcpServer.cs、TcpRedirectAcceptor.cs、TcpRedirectSessionStore.cs、TcpRedirectSetup.cs ×2、UdpSessionSetup.cs）成为冗余 → 用户裁定移除。移除后复验：`rg MA0038 src tests benchmarks` 无匹配；Release build 0 Warning / 0 Error（证 `.editorconfig` 规则级关闭承接全部抑制）；Release test 725/725；`dotnet format --verify-no-changes` exit 0；全量 jb 零 issue。

### 终局独立审计处置 + 门禁复验（2026-09-20，check 代理）

审计以隔离 worktree（`/tmp/jb-suppression-audit` = HEAD + 全部工作区改动）**移除全部抑制后全量重跑 jb** 实证：报告 729 条，恰好等于被抑制集合（597 键覆盖 + 132 pragma 覆盖，`729 = 597 + 132`，无缺口）。裁定：128 条 pragma 行中 **126 NECESSARY + 2 STALE**；16 个 `.editorconfig` 键中 **14 NECESSARY + 2 POLICY**（2 个 0 命中键按策略保留：单行尾逗号属既有、`foreach_can_be_partly_converted_to_query` 为 6 命中变体键的同族孪生）。产出 `research/suppression-audit.md`。

处置（主树）：删除 2 条 STALE pragma —— `src/WinForward.Protocols/PacketChecksums.cs:29`（`MemberCanBePrivate.Global`，全仓 0 命中）、`src/WinForward.Core/IPAddressValue.cs:105`（`ConvertIfStatementToReturnStatement`，该文件仅 40/47 由 39/46 pragma 覆盖）。`MemberCanBePrivate` 与 `DuplicatedSequentialIfBodies` 的其余疑似失效 pragma 经实证为 NECESSARY（删后规则在 Socks5State:119 / TcpProxyCoordinator:585 / TcpRedirectTable:204 重现——检出受邻近注释影响）。处置后 pragma 指令行 132 行（`disable once` 116 + 块 8），键 16。

处置后主树四道门禁全量复跑（`/tmp/taskc.sh`，日志 `/tmp/taskc.log`）：

| 门禁 | 结果 | 时间（UTC） |
|---|---|---|
| `dotnet build WinForward.slnx -c Release` | `build_exit=0`（0 警告 / 0 错误） | 10:08–10:10 |
| `dotnet test WinForward.slnx -c Release` | `test_exit=0`（725/725 全绿） | 10:10 |
| `dotnet format … --verify-no-changes --no-restore` | `format_exit=0`（空输出） | 10:10–10:11 |
| `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-final-audit.xml WinForward.slnx` | `jb_exit=0`，解析 `//Issues/Project/Issue` = **0** | 10:11–10:24 |

接受报告（15:34）后仅上述 2 个源文件发生改动（各删除一条 pragma 行）；其余源文件 mtime 未变。
