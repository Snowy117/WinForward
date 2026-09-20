# Disposition Log（jb inspectcode 判断型决定记录）

规则：每条规则/站点的决定在此登记：FIX / SUPPRESS-LOCAL / SUPPRESS-GLOBAL → 理由。
决策权：实现子代理自行决策（用户指示 2026-09-19 沿用）；任何抑制必须注明明确理由；检查阶段逐条复核；最终独立审计（移除抑制重跑实证）。

## 预先裁定（规划期，2026-09-20）

| 规则 | 范围 | 决定 | 理由 |
|---|---|---|---|
| ArrangeRedundantParentheses | 41（32/37 实测与 RCS1123 直接对立） | SUPPRESS-GLOBAL | 与强制门禁 RCS1123 同一 token 相反；dotnet format 门禁为准（design §3 D） |
| InconsistentNaming | 37 | SUPPRESS-GLOBAL | jb 对 dotnet_naming 的读取语义不同（缩写驼峰化/局部 const 误读/t_ 冲突）；命名由 IDE1006 更严格覆盖 |
| CheckNamespace | 19（TestHelpers） | SUPPRESS-GLOBAL（glob: tests/.../TestHelpers） | 与 IDE0130 同一裁定同路径 |
| LoopCanBeConvertedToQuery / ForeachCanBePartly... / ForCanBeConvertedToForeach | 19 | SUPPRESS-GLOBAL | 性能优先红线（hot-path spec） |
| MethodSupportsCancellation | 19（tests） | SUPPRESS-GLOBAL（[tests/**.cs]） | 与上次 MA0040 tests 裁定一致 |
| ArrangeObjectCreationWhenTypeNotEvident | 256 | SUPPRESS-GLOBAL（先试 style 键改向） | 与代码库 de facto 目标类型 new 风格相悖 |
| NotResolvedInText | 1 | SUPPRESS-LOCAL | member-path paramName 刻意模式（同 MA0015/S3928/CA2208 叙事） |

## 批次决定（B1 起追加）

### B1 — A 类机械修复（2026-09-20）

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| RedundantUsingDirective | 79（全部） | FIX | 逐站点核对 snippet 均为单行 using；仓库全域无 `#if`、单一 TFM net10.0，无条件编译风险；父命名空间/global using 覆盖由 Release build（0 警告）验证 |
| RedundantNameQualifier | 45（全部） | FIX | 移除的前缀均由显式 using 或父命名空间覆盖（构建兜底） |
| RedundantCast | 280（全部） | FIX | 逐站点确认「移除后重载绑定/值不变」，语义由 725 测试兜底；唯一手写例外：CapturePumpBenchmarks `(long)buffers.Length` 移除后仍保留外层 `(int)`（Min(long,long)→int，remaining 为 long，绑定不变） |
| RedundantUnsafeContext / RedundantExplicitArrayCreation / RedundantExtendsListEntry / RedundantJumpStatement | 10（全部） | FIX | 均为恒等移除；空 catch 由 S108 触发补注释（不改变语义）；显式数组 → 集合表达式以保 IDE0300 门禁 |
| InvalidXmlDocComment | 9（全部） | FIX | 只修 doc 语法（歧义 cref → `<c>`；相对限定 cref → `UdpProxy.X`；类型级 `<paramref>` → `<c>`），不改语义 |
| UseCollectionExpression | 7（全部） | FIX | `new byte[] {...}` → `[...]`（目标类型 byte[]，常量转换合法） |
| UseUtf8StringLiteral | 6（全部） | FIX（非 u8 字面量路线） | 站点均为全零字节数组（零 MAC / Win32 BOOL FALSE）。`[.. "\0\0..."u8]` 形态对非文本 wire 值可读性差且仍需装箱数组；改用等价且更直白的 `new byte[N]`（同为零填充分配，行为零变更）→ 消除「有初始化列表可转换」触发点。jb 定向重跑 0 |
| ConvertToAutoProperty(+PrivateSetter/WhenPossible)/PropertyCanBeMadeInitOnly | 8（全部） | FIX | 逐点核查无 ref/volatile/Interlocked 依赖（`_bytes/_capacity/_disposition/_endKind/_calls/_count` 均为普通读写；`Capacity { get; }` 只读字段）；`EndKindRelay.EndKind` 写入全在对象初始化器 |
| ConvertClosureToMethodGroup / MergeIntoPattern / MergeIntoLogicalPattern / RawStringCanBeSimplified / VariableHidesOuterVariable | 7（全部） | FIX | 等价改写；lambda 参数 `index`→`offset` 解遮蔽；raw string 值实测为 `"{}"` |
| UseDeconstruction | 2（全部） | FIX | `adapters[i]`（2 元 record struct）→ `var (id, addresses)`；`packet.Context.Key`（FlowKey 7 元）→ `var (_, _, originalClient, originalServer, _, _, _)`（discard 形式，scoped jb 实测清除）。可读性：discard 形式偏机械但语义直白（只取 Local/Remote）；备选「保留 key 局部」会持续触发该 HINT |
| ConvertToConstant.Local | 6（全部） | FIX | `const nint` + 移除常量 `(nint)` 转换（避免随后 RedundantCast）；`finalB` 一并 const 化（finalA const 后其初始化式为常量表达式，避免级联新报） |
| ConvertIfStatementToReturnStatement | SoakOptions:42 / Policy:23 / TcpRedirectSetup:116 | FIX | 三处为纯值选择（无 throw、无副作用条件、行 ≤ ~130 字符）；TcpRedirectSetup 需同时消除 InlineTemporaryVariable → `address is not null ? From(address) : (IPAddressValue?)null`（`is { } raw` 三元形式实测会重新触发 InlineTemporaryVariable，`?: null` 无标注触发 CS8625） |
| ConvertIfStatementToReturnStatement | 其余 15 站点 | **留 B4 抑制**（不采纳） | 两类理由：(a) guard-clause + throw 惯用法（Domain:86、IPAddressValue:41/47、NdisApiAbi:166/174、NdisPacketBuffer:41/78、CaptureLifecycleFakes:24、HighResolutionTimerScopeTests:74、Socks5UdpDatagrams:16、IPAddressValue:106（嵌套级联））——JB 期望的 `cond ? throw X : value` 形式在仓库无先例、行长达 135–200+ 字符，显著降低可读性；(b) 条件含副作用（FlowDispatcher:170 `TryComplete`、NdisCapturePumpTests:182 `TrySetResult`、Socks5UdpTransport:330 `TryDecode` out var、TcpProxyCoordinator:560 长行）——副作用隐藏在条件表达式中破坏该惯用式的「失败即早退」可读性；其中 FlowDispatcher 站点位于热路径 warm-shape 方法，保持守卫结构。 |
| InlineTemporaryVariable | FlowTable / IPTcpUdpPacket / AdapterEnumeration / AdapterListWatcher / LayeredCaptureRunner ×2 / MultiAdapterCaptureLoop / NdisCaptureGeneration ×2 / NdisPacketActionExecutor / FlowDispatcher / RuntimeHeartbeat ×3 / TcpRedirectSetup | FIX | 统一形态 `is { } x` → `is not null` + 直接使用目标表达式（全部字段为 `private readonly`，无双重读取风险；局部变量 null-state narrowing 由构建验证 0 警告）；FlowTable 内联 `_expiredScratch` 别名（6 处使用，门内执行）；IPTcpUdpPacket 内联 `const ipOffset = EthernetHeaderLength`（22 处） |
| InlineTemporaryVariable | NativeBufferPoolTests:128（copy）/ :278（stale） | **留 B4 抑制**（不采纳） | 这两个局部是测试断言对象本身：`LeaseCopiesReleaseExactlyOnce` 锁「lease 副本 dispose 幂等」、stale 用例锁「过期副本 release 为 no-op」。jb 的内联改写（`lease.Dispose()`/`reused.Dispose()`）会把场景退化为「同一变量连续 dispose」，静默移除回归覆盖——语义敏感，不采纳 |
| MergeConditionalExpression（新触发） | NdisCaptureGeneration.Pumps / RuntimeHeartbeat.usage / ReadGcSnapshot | FIX | B1 中 `x is not null ? x() : default` 改写后被 jb 反建议 `x?.Invoke() ?? default`（baseline 无此规则）→ 按该形式落地，scoped jb 归零 |

### B2b — 未使用家族续（主会话，2026-09-20；含事故修复后终态）

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| MemberCanBePrivate.Global | ~30 站点（bench consts、CLI/Config/NDIS/Core/Testing 常量与私有化可行成员） | FIX（private 已应用） | 逐站点脚本化 + 构建兜底；构建全绿 |
| MemberCanBePrivate（PacketChecksums.TryRewriteUdpEndpoints(IPAddress…)） | 1 | **REVERT 回 public + 列入抑制** | jb 对 **IVT 友元用量是盲区**：该重载被 tests（UdpPacketParsingTests/ProtocolAuditTests）经 IVT 作为协议 oracle 使用（XML doc 明示）；私有化触发 S1144/RCS1213 且友元不可达。误报类记录，待 B5 抑制 + 审计复核 |
| MemberCanBePrivate（SoakOptions 14 accessor） | 14 | 待续：`{ get; private init; }` | 事故回滚后恢复 public；正确形态是 accessor 私有（注意 `with` 表达式需 init 可达，构建验证） |
| MemberCanBePrivate（IPPrefix ctor/PrefixLength、MacAddress.TryFrom、ConfigModels 4 consts、LayeredCaptureRunner、RuntimeHeartbeat、SetupExecutor、Socks5AddressCache、TcpPendingSynSetup、TcpProxyRelay.PumpBufferSize、ClientResetInjector.s_capacityResetCooldownWindow、NdisCapture×2、NativeBufferPool×2、AdapterLocalAddressProviderCacheTests.Provider、FrameBuilders.TcpFlagPshAck、Socks5State.GreetingNoCredentials） | 余量 | 待续（脚本按成员类型逐点，含构建门禁） | 事故后重启保守流程 |

### B-suppress — 规则级抑制批（主会话，2026-09-20；.editorconfig 追加于尾部，含实证理由）

| 规则 | 命中 | 机制 | 理由（详见 .editorconfig 原位注释） |
|---|---|---|---|
| ArrangeRedundantParentheses | 41 | GLOBAL | 与 RCS1123 同 token 对立 32/37（真括号 vs 冗余），dotnet format 门禁为准 |
| ArrangeObjectCreationWhenTypeNotEvident | 256 | GLOBAL | 与 de facto 目标类型 new 风格相悖；显式化纯 churn |
| InconsistentNaming | 27 | GLOBAL | jb 读 dotnet_naming 语义偏差（缩写驼峰化/局部 const 误读/t_ 冲突）；IDE1006 已覆盖 |
| NotAccessedPositionalProperty(.Global/.Local) | 16 | GLOBAL | record 位置属性承载合成相等性/哈希（等于性键），jb 不建模 |
| LINQ 转换家族（Loop/Foreach×3/For） | 21 | GLOBAL | 性能优先红线（hot-path.md）：不引入委托/迭代器分配 |
| MethodSupportsCancellation | 19 | [tests/**.cs] | 与 MA0040/tests 裁定一致 |
| CheckNamespace | 19 | [tests/.../TestHelpers/**.cs] | 与 IDE0130 同路径同裁定 |
| UnusedAutoPropertyAccessor.Global | 11 | [benchmarks/.../Perf/**.cs] | BDN [Params] 反射注入 setter，静态分析盲区 |
| MemberCanBePrivate.Global（PacketChecksums 站点） | 1 | ~~SITE-PRAGMA~~ → **审计删除（STALE）** | 抑制移除后的全量报告中该规则 0 命中（该 public oracle 被同解决方案 tests 引用，jb 解析得跨项目使用）→ 无作用，已删除；方法 XML doc 保留 oracle/IVT 职责说明。见 `suppression-audit.md` |

### B2c — 未使用家族收尾（实现子代理，2026-09-20）

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| MemberCanBePrivate.Global | 45（全部） | FIX | 按成员类型逐站点：常量/静态字段→private；属性→private 或 `{ get; private init; }`（SoakOptions×14、NdisCapturedPacket.Flags；`with`/初始化全部在类型内，构建验证）；ctor→仅改构造行（IPPrefix，类型声明不动）；方法→private（MacAddress.TryFrom、PacketChecksums 的 IPAddressValue 重载）。Release build 0 警告兜底；无 IVT 构建失败 |
| MemberCanBePrivate.Local | 2（LoopbackSocks5UdpServer.RelayEndpoint、测试 CacheHarness.Provider） | FIX | 同类型内使用（分别为 RelayEndpoint.Port、SelectLocalAddress 参数） |
| UnusedMember.Global | 2（NdisApiAbi.GetDriverVersion / ReadPacket） | **DELETE** | 全仓 grep 零引用（src/tests/bench），spec 未列为 ABI 必需；09-08 设计评审已将其记为 dead native surface。删除后 `EthernetRequest` 仍由 SendPacketToMstcp/Adapter 使用，ABI 布局断言不变 |
| UnusedMethodReturnValue.Global | 1（RuntimeCounters.Add） | FIX | 唯一调用方 RuntimeCountersTests 三处全部忽略返回值 → `long` 改 `void`（保留 Interlocked.Add 语义），消除"返回值存在但无人使用"的误导面 |
| UnusedMemberInSuper.Global | 1（IRuntimeLogger.Trace） | SUPPRESS-LOCAL | 级别对等契约的一部分：`trace` 是用户可配置级别（ConfigurationModels 解析、ConsoleRuntimeLogger 阈值过滤由 RuntimeLoggingTests 覆盖）；当前唯一调用走具体类型。删除接口成员只会把 NullRuntimeLogger.Trace/RecordingLogger.Trace 变成新的孤立未用成员，不能简化任何行为 → 保留 + 行内 pragma + 理由 |
| NotResolvedInText | 1（FlowDispatcher "packet.Lease"） | SUPPRESS-LOCAL | 集中式 member-path paramName（CapturedFlowPacketGuards）：MA0015/S3928/CA2208 已因同一原因作用域抑制；ReSharper 无法把成员路径解析为符号 → `// ReSharper disable once NotResolvedInText` + 理由 |
| （连带）RuntimeHeartbeat.DefaultInterval / SetupExecutor.DefaultWorkerCount | 2 | FIX（改名） | 私有化后触发 IDE1006 s_camelCase 命名门禁 → `s_defaultInterval` / `s_defaultWorkerCount`（后者 `<see cref>` 同步） |
| （连带）spec 提及 | 2 处 | 同步 | hot-path.md 的 `DefaultWorkerCount = …`、windows-ndisapi.md 的 `MaxConsecutiveStartupRecoveries (internal const…)` 括注随私有化更新（见 B2c 收尾） |

**B2c 复核**：全量 jb（/tmp/b2c-full.xml，366→313）确认 6 规则全部归零、**两处新抑制 pragma 均已生效**（UnusedMemberInSuper 1→0、NotResolvedInText 1→0）、无新增规则/计数上升；Release build 0 warning、725/725 tests、format exit 0。唯一波动项：`PreferConcreteValueOverDefault`（SetupExecutor:43，B4 范围）在本次全量中消失但 scoped 运行可复现 → jb 分析噪音，非本批引入。


### B3a — AccessToDisposedClosure（64）+ AccessToModifiedClosure（9）（实现子代理，2026-09-20）

口径：报告 = `/tmp/b2c-full.xml`（313 条，与当前树一致；派单所引 `/tmp/sup-state.xml` 实为 366 条旧态）。**零 FIX、73 SUPPRESS-LOCAL**：逐站点精读后没有任何一处闭包可能在释放后执行（详见 batch-log「B3a」的机制分类与 src 执行序论证）。机制：单 issue 语句用 `// ReSharper disable once <Rule> // 理由`，同语句多 issue 用 `disable/restore` 对（实测必要）。

#### src（4 站点，从严：执行序确证后才抑制）

| 文件:行 | 规则 | 捕获 | 结论与理由（行内 pragma 同文） |
|---|---|---|---|
| Cli/Program.cs:322 | AtDClosure | `shutdown` | 结构性安全：`OnCancel` 唯一可达路径是 `Console.CancelKeyPress` 订阅，`finally`(L345) 在方法退出（using 释放）之前退订 → 顺序路径无释放后访问。残余"已被派发的并发 Ctrl+C handler"窗口是 .NET 事件模型固有（任何退订皆然），非可修结构问题 |
| Cli/Program.cs:217 | AtMClosure | `runnerRef` | jb 误读：单次赋值先于一切回调（degraded 回调只能来自 generation 的 pump，而 generation 只在赋值后的 `RunAsync` 内创建）；原注释 L213-215 已述同一事实 |
| Runtime/Capture/LayeredCaptureRunner.cs:144 | AtDClosure | `monitorCancellation` | 结构性安全：`TeardownAsync` L459 `await monitor` 汇合该任务后 `RunAsync` 的 using 才释放；`CancelBestEffortAsync` 只吞 ODE、不会跳过汇合 |
| Runtime/Capture/LayeredCaptureRunner.cs:147 | AtDClosure | `monitorCancellation` | 同上（`await periodicTick` L460 汇合后才释放） |
| Runtime/Capture/NdisCaptureGeneration.cs:103 | AtMClosure | `runtime` | jb 误读：`runtime` 在下一语句赋值，回调只能由"运行中的 pump"触发，而 loop 必须由赋值后的 runtime 启动 → 读到的必为已赋值引用 |

#### benchmarks（5 站点）

| 文件:行 | 捕获 | 形态 | 理由（行内 pragma 同文） |
|---|---|---|---|
| Stability/TcpEofScenario.cs:62 | `upstreamPeer` | once | receive 腿在 finally 释放前被 await；超时路径**刻意** Dispose `upstreamPeer` 以解开 parked receive，`ReceiveAsync` 以 `ObjectDisposedException`→Outcome 分类（场景设计） |
| Stability/TcpEofScenario.cs:63 | `localPeer`,`upstreamPeer` | pair | send 腿在任何 Dispose 之前 await；adversarial abort 刻意打断 client 腿，`SendAsync` 按预期分类异常 |
| Stability/TcpThroughputScenario.cs:92 | `client` | once | `sender` 在 finally 释放 client 之前 await；relay 拆除提前终结 client 腿属分类可容忍路径 |
| Stability/UdpBurstScenario.cs:136 | `senderCancellation` | once | 先 `CancelAsync` 再 `await senderTask`，loop 返回后才离开 using 作用域 |

#### tests（64 站点）

统一事实（同文件同模式共享表述，且与代码一致）：闭包（`WaitForAsync` 谓词 / `Task.Run` 委托 / `barrier.SignalAndWait` / reader 回调）都**在同作用域内被 await/WhenAll 汇合**；`CaptureRunnerHarness.DisposeAsync` 自身先 Cancel、再 await RunTask（drain）才释放 ChangeSource/Cancel。逐站点理由均已写入行内 pragma（含被轮询成员名，如 `Generations.Generations.Count`、`harness.RefreshEvents`、`harness.Generation(0).DisposeCount`、`harness.Logger.Lines`）。

| 文件 | 站点 | 形态 | 说明（逐站点理由见行内 pragma） |
|---|---|---|---|
| AdapterListWatcherTests.cs | 12（L30×2, L47×2, L72, L109, L121, L128, L129, L148, L173×2） | 3×pair + 6×once | L30/L47/L173 各 2 捕获 → pair。安全型：waiter await 在作用域内；刻意型：L72 `DisposeUnblocksAParkedWaitOne`（Dispose 即解阻塞机制）、L109/L121 弃置 waiter 由 using 释放时 Cancel 释放、L128/L129 parked waiter 由 Dispose→Cancel 释放（helper XML doc 明示 cancel-on-dispose） |
| AdapterLocalAddressProviderCacheTests.cs | 1（L129） | once | `barrier.SignalAndWait()` 的 4 个 worker 由 `Task.WhenAll` 汇合后才离开 using |
| CaptureLifecycleTests.cs | 5（L57,59,91,94,133） | once | 各 `Task.Run(runtime.X)` 均被 `Task.WhenAll`/`await start` 汇合；DisposeStarted/Started 门控使重叠成为断言目标 |
| IdleExpirySweeperFailureTests.cs | 1（L35，AtM） | once | `sweepFailures` 仅经 `Interlocked`/`Volatile` 访问（跨线程安全的刻意计数），jb 的"捕获修改"模型不适用 |
| LayeredCaptureRunnerHealthSignalTests.cs | 1（L33） | once | poll 只读 live harness；dispose 在 await 返回且 run drain 之后 |
| LayeredCaptureRunnerPeriodicRefreshTests.cs | 5（L22,43,67,73×2） | 4×once + 1×pair | 同上（L73 为多行语句 + 2 个捕获引用 → pair） |
| LayeredCaptureRunnerRefreshTests.cs | 18（AtD×16 + AtM×2：L36、L49） | once | AtD：`WaitForAsync` 轮询 live harness/capture（含 L49 capture）。AtM：L36 `harness` 两阶段别名（赋值早于 `Start()`，回调只在安装 generation 时触发）；L49 `generation` 为 for 变量，自增严格晚于本轮 await 返回 |
| LayeredCaptureRunnerTests.cs | 3（L101,128,148） | once | 同 harness 轮询形态（RefreshEvents / Generations） |
| NdisCapturePumpTests.cs | 6（L27,58,84,114,175,211） | once | 前 5 处为 `ScriptedReader` 读回调内的 `cts.Cancel()`（pump 正常退出路径，run 先被 await）；L211 为 `Task.Run` 捕获 token，run+dispose 握手先完成 |
| NdisCaptureResilienceTests.cs | 5（L43,70,132,246,268） | once | 3 处同 ScriptedReader cancel 回调；2 处 `Task.Run(runtime.StartAsync)` 由 `await start` 汇合后才 dispose |
| RuntimeCountersTests.cs | 1（L65） | once | `barrier` 由 `Task.WhenAll` 汇合 |
| RuntimeLogThrottleTests.cs | 1（L48） | once | `barrier` 由 `Task.WhenAll` 汇合 |
| UdpProxyCoordinatorLifecycleTests.cs | 4（L242,243,282,283，AtM） | once | 两阶段测试接线：`snapshotTaken`/`resumeSweep` 在构造后赋值，recheck seam 仅由 `RemoveExpiredAsync` 调用（发生在赋值之后），并用 TCS 门控使顺序可观察 |

**实测**：scoped jb（19 文件）→ 两规则 **0**；Release build 0 warning；BOM 无失配。

**B3a 收尾核对**：全量 jb（/tmp/b3a-final.xml，313 → 241）确认 `AccessToDisposedClosure` 64→0、`AccessToModifiedClosure` 9→0、无规则计数上升（唯一 +1 = 既有波动项 `PreferConcreteValueOverDefault`）；Release build 0 warning；725/725 测试（5 连跑，含 1 次已知环境性零分配门禁 flake 记录）；`dotnet format --severity info --verify-no-changes` exit 0。暂停用 pragma 共 73 条注释行（63 `once` + 5 pair）待审计复核。

### B3b — nullable 家族 + 释放类 + 小项正确性（实现子代理，2026-09-20；报告 /tmp/b3a-final2.xml 241 条）

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| RedundantSuppressNullableWarningExpression | 31（全部） | FIX | 逐站点确认 `!` 为编译器注解层冗余、删除无运行时代码差异：`(IPEndPoint)x.LocalEndpoint!` 的 cast 本身不触发 CS8600（×12）；`Assert.NotNull(x)` 带 `[NotNull]` 流标注（×12）；`FlowDispatcher.Lease` 为非空声明成员；`UdpProxyCoordinator.Send.cs:71` 两局部在分支内赋值、else 返回；`Socks5UdpReceiveResult.RemoteEndPoint` 声明非空；`TcpProxyCoordinatorRewriteTests` 前一行 `Assert.True(out var claimed)` 建立非空状态。Release build 0 警告实测兜底，零回退 |

### B3b 续 — nullable 条件家族 / 类型模式 / dispose 家族

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract | 9（catch-boundary 守卫） | SUPPRESS-LOCAL | `CapturedFlowPacket.Lease` 声明非空但 struct 的 default 实例运行时可带 null lease；这是集中式 fail-closed 守卫（FlowDispatcher L50-64 doc：ParamName 指向真正为 null 的成员；quality-guidelines.md 同款叙事）。删除守卫即移除边界防御 |
| ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract | 1（InterceptionHealthMonitor.ReportFailure） | FIX | `fired` 唯一赋值在锁内最后一段、所有提前返回均在其前；`string?` 声明与 `is not null` 均冗余 → `string fired;` + 直接 `handler?.Invoke(fired)`（jb 判定与执行序一致） |
| ConditionalAccessQualifierIsNonNullableAccordingToAPIContract | 2（GcSoakScenario.Stop `thread?.Join`、NdisCapture.ReleaseBatchBuffers `buffer?.Dispose`） | FIX | 两集合（`Thread[]`/`NdisPacketBuffer[]`）均在构造器全量填充、运行期无置空；`?.` 冗余（JD 正确） |
| NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract | 1（RuntimeLogging.FormatValue IFormattable 臂） | FIX | `IFormattable.ToString` BCL 标注非空返回，`?? string.Empty` 冗余；调用点 Event 自带 try/catch 兜底；保留 `_ => object.ToString() ?? …`（标注为可空，规则未命中） |
| ConvertTypeCheckPatternToNullCheck | 9（全部） | FIX | 全部是 `T?` 包装值类型上的类型模式：`is T y`→`is { } y`、`is not T y`→`is not { } y`、`is not T`→`is not null`；对值类型包装无子类型差异，控制流/绑定语义全等（构建 + 既有测试兜底） |
| DisposeOnUsingVariable | 10（CaptureLifecycleTests:80、FlowDispatcherTests:130/132、HighResolutionTimerScopeTests:19/33/60、NdisCapturePumpTests:335、NdisPacketBufferPoolTests:129、TcpPendingSynSetupTests:260、TcpProxyCoordinatorLifecycleTests:399） | SUPPRESS-LOCAL | 显式 dispose 即被测行为（dispose 后立即断言语义/状态/计数）或并发编排（dispose 与 parked 读重叠）；using 仅兜底断言失败路径。逐站点理由已在行内 pragma |
| DisposeOnUsingVariable | 1（TcpProxyRelayTests:51 `local.Dispose()`） | FIX | `using var local` 已覆盖；显式 dispose 无断言价值，属重复清理 → 删除（`relayLocal.Dispose()` 保留） |

### B3b 小项批（14 规则 / 31 站点）

| 规则 | 站点 | 决定 | 理由 |
|---|---|---|---|
| HeuristicUnreachableCode + CSharpWarnings::CS0162 | UdpBurstScenario:50、UdpLossScenario:38（各 2 条） | SUPPRESS-LOCAL | 文档化 opt-in 诊断开关（const false，XML doc 明示 flip 用途）；"不可达"分支即开关的启用态。Release build 实测无 CS0162（jb 以 Debug 分析并自建常量折叠模型）→ 非死代码 |
| InconsistentlySynchronizedField | TcpCoordinatorFakes:335 | FIX | Listeners 读侧补 `lock (_listeners)` + 快照（与写侧一致）；使用点全为即时断言 |
| EqualExpressionComparison | NdisAdapterGateMapTests:14 | FIX | 保留"两次 Get(1) 同实例"断言，改 `Assert.Same(gate, map.Get(1))` |
| SuspiciousTypeConversion.Global | ProcessAttribution:183 | **FIX（真缺陷）** | 探针实测 `IPAddress.Equals(IPAddressValue)` 恒 false；FindUdpOwner 的精确地址分支全死。改为 `IPAddressValue.From(row.Address).Equals(localAddress)`（无堆分配）；**这是本批唯一改变运行时行为的站点（Windows-only 归因路径），需主会话/审计知悉** |
| RedundantArgumentDefaultValue | IPAddressValue:40、IPPrefix:70、BatchingTests:72、UdpProxyCoordinatorTests:174×2 | FIX | 显式实参 = 形参默认值，删除 |
| TryStatementsCanBeMerged | TcpRedirectSessionStore:290 | FIX | try-catch 内嵌 try-finally → 单层 try-catch-finally，语义等价 |
| SwitchStatementMissingSomeEnumCasesNoDefault | FlowDispatcher:388（PacketAction）、TcpEofScenario:150（AbortKind） | FIX | 补 `case None:` / `case Clean:` 显式 no-op（行为不变） |
| SwitchStatementHandlesSomeKnownEnumValuesWithDefault | TcpEofScenario:259 | SUPPRESS-LOCAL | `Other` 与未来成员共用 default 计数体；显式 `case Other:` 触发 SonarAnalyzer S3458（构建门禁），故保留结构 + jb 抑制 |
| AsyncMethodWithoutAwait | BatchingTests:87、ConcurrencyTests:296、LayeredCaptureRunner:217 | FIX | 测试改 void；src 去 async（同步体 + `break`/`return Task.CompletedTask`；异常全被 OCE/ODE 捕获，Unwrap 契约不变） |
| CanReplaceCastWithLambdaReturnType | CaptureRunnerFakes:209 | FIX | 删 cast（数组→接口隐式引用转换仍满足集合表达式目标类型） |
| CanSimplifyDictionaryTryGetValueWithGetValueOrDefault | StabilityShared:47、RuntimeHeartbeat:172 | FIX | GetValueOrDefault 缺省 0 与原三元一致 |
| ParameterOnlyUsedForPreconditionCheck.Local | GcSoakScenario:206、Domain:34、RuntimeDiagnosticLoggingTests:196、TcpFragmentHandlingTests:158、CaptureLifecycleFakes:17×2、TcpCoordinatorFakes:266 | SUPPRESS-LOCAL | 逐站点理由见行内 pragma：测试失败注入 seam（3）/ 场景 vacuity 守卫（1）/ 公共边界契约（1）/ jb 对 xUnit 断言谓词的误判（1，lambda 参数即断言输入） |
| PreferConcreteValueOverDefault | SetupExecutor:43 | FIX | `default` → `CancellationToken.None`（等价，落定 B2c 波动项） |

**B3b 小项批修正（scoped 复核后）**：

| 项 | 决定 | 理由 |
|---|---|---|
| FlowDispatcher CompleteAsync（PacketAction） | FIX（最终形态） | `case PacketAction.None:`（显式 no-op，文档化 reverse/fragment 无动作路径）+ `default: throw new InvalidOperationException(...)`。jb 探针实证：全值 + 无 default 会被 `HandlesSomeKnown…`（"Some cases are not processed: default"）命中；全值 + 空 default 被其自身 `RedundantEmptySwitchSection` 拒绝；`default: throw` 完全干净。今日不可达（私有封闭枚举、全调用点字面值）→ 行为零变更，同时给未来新成员 fail-loud 兜底 |
| TcpEofScenario FireEventAsync（AbortKind） | FIX（最终形态） | 同上：`case Clean:` + `default: throw`（同探针依据） |
| LayeredCaptureRunner.MonitorAsync | FIX（最终形态） | 去 async 后 `while(true){ if(!WaitOne) break; }` 触发 RCS1218 → 改 `while (_changeSource.WaitOne(token)) _demandGate.Signal();`（RCS1218 期望形态；WaitOne 返回 false = change source 被释放/解除阻塞，退出语义不变） |
| TcpEofScenario.TransferOutcome | SUPPRESS-LOCAL（维持） | 显式 `case Other:` 叠 default 被 SonarAnalyzer S3458（构建门禁）拒绝——实测构建失败；保留 default 共享计数体 + jb 站点抑制，理由可验证 |

### B4a — 控制流风格判定批（实现子代理，2026-09-20；报告 /tmp/accept-check2.xml 147 条）

范围：InvertIf 50、ConvertIfStatementToReturnStatement 15、InlineTemporaryVariable 2、UseVerbatimString 9、
ConvertIfStatementToSwitchStatement 5、DuplicatedSequentialIfBodies 5、MoveLocalFunctionAfterJumpStatement 4、
SeparateLocalFunctionsWithJumpStatement 4（共 94 站点）。B4b 另批：ReplaceWithPrimaryConstructorParameter 48 + ArrangeObjectCreationWhenTypeEvident 5。

#### 1) InvertIf — FIX 4 / SUPPRESS-GLOBAL 46（拒绝率 92% ≥ 80% → 规则级）

判定标准：采纳 = 反转后是**自然的早退路径**且不降低可读性（简单条件、无副作用、去嵌套、不产生重复语句）；
拒绝 = 破坏既有 guard+throw/guard+continue 读序、条件含副作用（`TryRemove`/`TryMarkRented`/`Interlocked`/掩码计数）、
需取反复合条件、反转使尾部 `return` 重复、快路径正向优先约定、与同方法未命中守卫形态分裂。

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| InvertIf | SoakRunner.cs:9、StabilityContext.cs:47、NdisPacketActionExecutor.cs:275、TcpRedirectSessionStore.cs:193 | **FIX（4）** | 逐站点改造为早退守卫：平台门反转消除重复调用（SoakRunner）；方法尾空值测试 → `return`（StabilityContext）；循环尾守卫 → `continue`（NdisPacketActionExecutor，同循环已有 continue 惯用法；TcpRedirectSessionStore，try/catch 出嵌套）。build 0 警告 + tests 兜底 |
| InvertIf | 其余 46 站点（Cli Program:31/52/77、ConfigurationModels:259/342、BoundedSetupQueue:122、FlowTable:108/122、IPPrefix:58、NativeBufferPool:77、ProcessSelectors:41、NdisApiDriver:103/116/129/158/225/238/284、NdisCapture:276、NdisPacketBufferPool:67、PacketChecksums:313、TcpResetBuilder:57、UdpFrameBuilder:76、NdisPacketActionExecutor… 见 batch-log B4a 表、FlowDispatcher:267、InterceptionHealthMonitor:176、RuntimeHeartbeat:206、SelfTrafficRegistry:41、TcpPendingSynSetup:241/258/332、TcpProxyCoordinator:156/208、TcpRedirectSessionStore… 、TcpRedirectSetup:88、TcpRedirectTable:282、UdpAssociations:38、UdpProxyCoordinator.Send:100、UdpProxySession:137、UdpResponseReinjector:198、UdpSetupQueueBudget:49、ProcessAttribution:32、RuntimeHeartbeatTests:80/102/248/299、UdpTransportFakes:102） | **SUPPRESS-GLOBAL** | 逐站点审查后 92% 拒绝：仓库偏好显式嵌套/守卫读序；jb 的扁平淡化不足补偿"条件取反 + 执行序读序"损失。机制 = `.editorconfig` `resharper_invert_if_highlighting = none`（全局段，中文注释含采纳/拒绝统计与 6 类拒绝理由）。逐站点归属见 batch-log「B4a / 1. InvertIf」表 |

| ConvertIfStatementToReturnStatement | 15（全部） | **SUPPRESS-LOCAL ×15** | 沿用 B1 论证：10 处 guard+throw（`cond ? throw …` 无先例、失败路径须先读）、4 处条件含副作用/out 绑定（TryComplete/TryDecode/TrySetResult/TryEncode）、1 处长行三元（NdisApiAbi:166 的 NativeLibrary.Load）+1 处早退级联一致性（IPAddressValue:104）。逐站点 pragma 理由已行内落地；无采纳（B1 已把 3 处真正的纯值选择改造完毕） |
| InlineTemporaryVariable | NativeBufferPoolTests.cs:127（`copy`）/ :277（`stale`） | **SUPPRESS-LOCAL ×2** | 别名局部即测试断言对象本体（lease 副本幂等释放 / 过期副本 no-op）；内联会把场景退化为同一变量连续 dispose，静默丢失回归覆盖（B1 disposition） |
| UseVerbatimString | 9（全部） | **FIX ×9** | `"\\"`→`@"\\"`（RuntimeLogging JSON 转义臂）、`"C:\\Apps\\browser.exe"`→`@"C:\Apps\browser.exe"` 等路径字面量、`"\\DEVICE\\"`→`@"\DEVICE\"`（WindowsAdapterInventoryTests×3）；逐点核对字符值等价（反斜杠计数逐一对照；verbatim 尾反斜杠合法、无 `\uXXXX`/插值/内嵌引号站点） |

| ConvertIfStatementToSwitchStatement | 5（全部） | **SUPPRESS-LOCAL ×5** | 逐点判定"switch 形态不更清晰"：范围模式前置校验（ConfigurationModels:240）、etherType+双族守卫（TcpResetBuilder:49，热路径）、0/1/>1 三态判别（CaptureAdapterScopeResolver:151）、带长注释的 outcome 链（NdisPacketActionExecutor:374）、行解析 if/throw 级联（UnicastAddressInventory:156） |
| DuplicatedSequentialIfBodies | IPAddressValue.cs:105/107 | **FIX（合并）** | 两早退体完全相同且合并条件读作单一决策（IPv4 无 scope / 零 scope 无需 scope 实参）→ `if (Family == AddressFamilyKind.IPv4 \|\| ScopeId == 0) return new IPAddress(bytes);` |
| DuplicatedSequentialIfBodies | Socks5State:119、FlowDispatcher:158、TcpProxyCoordinator:584、TcpRedirectTable:204 | **SUPPRESS-LOCAL ×4** | 条件均无副作用（合并安全），但合并会塌掉"两个不同阶段/理由共用一个退出"的语义：解析阶段（畸形 vs 截断）、热路径旁路枚举（每行一个文档化理由）、两阶段 out 绑定、fail-closed 认领门（已属他键 vs 表满）→ 站点 pragma + 理由。**审计复核（2026-09-20）**：4 条全部 NECESSARY——抑制置空后的全量报告在同站点重现该规则命中（4 条）；附带实证：该规则的检出受相邻注释影响（注释行存在即不报），故这些 pragma 确为承重抑制 |
| MoveLocalFunctionAfterJumpStatement / SeparateLocalFunctionsWithJumpStatement | HotPathAllocationGateTests:270/335、Socks5AddressCacheTests:27/56、UdpReceiveResilienceTests:67/98/225/264 | **FIX ×8** | jb 探针实证（/tmp/jfprobe 8 变体）：局部函数须位于块末尾且前置显式 `return;`。中段声明者移至末尾 + `return;`（3 文件 4 处）；已在块尾者仅补 `return;`（4 处）。构建 0 警告（无 S3626 误报） |

**B4a 结果汇总（门禁全绿）**：8 条规则 94 站点 → **FIX 22 / SUPPRESS 72**（FIX = InvertIf 4 + UseVerbatim 9 + Duplicated 1 + Move/Separate 8；SUPPRESS-GLOBAL 46 = InvertIf 规则级；SUPPRESS-LOCAL 26 = ConvertIf 15 + Inline 2 + Switch 5 + Duplicated 4）。Release build 0 warning、725/725 测试、`dotnet format --verify-no-changes` exit 0、全量 jb **147 → 53**（残留仅为 B4b 的 PrimaryCtor 48 + OCevident 5），无新增规则/无 CSharpErrors 伪影。

### B4b — primary ctor 参数化 + 目标类型 new（实现子代理，2026-09-20；报告 /tmp/b4a-final.xml 53 条）

| 规则 | 站点/范围 | 决定 | 理由 |
|---|---|---|---|
| ReplaceWithPrimaryConstructorParameter | 48（16 文件） | **FIX ×48（0 抑制）** | 全部为"primary ctor 参数 + 纯转发只读字段（`private readonly T _x = param;`）"形态：删除字段、正文改用 primary ctor 参数。等价性（探针 /tmp/pcprobe + 语义分析）：编译器对正文引用的 primary ctor 参数生成每参数 1 个隐藏捕获字段、构造期赋值——字段数量/布局/分配与显式 `_x = param` 零差异；lambda 内使用同为 `this` 字段读取（无新增闭包/捕获类）。站点均在非稳态路径（TCP setup/accept/failure、UDP setup/预算冷边、日志、bench 夹具、tests fake；NativeMemoryManager 为"每次新原生分配一次"的冷路径），无 hot-path 风险。构造语义敏感形态（`?? throw`/`?? 回退`/条件赋值）不在报告内（jb 不标记；探针 Case 2 实证）→ 本轮无拒绝站点。逐文件编辑、每文件后 Release build 0 警告 |
| ArrangeObjectCreationWhenTypeEvident | 5（4 文件） | **FIX ×5（0 抑制）** | 目标类型逐点可推断：Dictionary<string,Socks5Server> 索引初始化器（DispatcherBenchmarks:56）、IReadOnlyList<IPAdapterUnicastInfo> 集合表达式元素（AdapterLocalAddressProviderCacheTests:140/146）、属性初始化器所属类型（ClientResetInjector:38 TcpResetCooldownTable、TcpRedirectSessionStore:53 TcpRedirectTombstoneTable）；`new Type(...)`→`new(...)` 与仓库 de facto 目标类型 new 风格一致，且与既有 `resharper_arrange_object_creation_when_type_not_evident` 规则级抑制（type 不明显时保留显式类型）互补。构建 0 警告兜底 |

**B4b 说明**：两条规则均无 SUPPRESS；48 站点内 `_healthSignal/_bufferPool/_timeProvider`（ClientResetInjector）、`CapacityResets`（`capacity ?? 16_384`）等条件/回退初始化字段保持原样（jb 未报告）。命名门禁：primary ctor 参数为 camelCase，不受 s_/_ 规则约束；未重命名任何现存字段。

### B4b 连带项（本次内联的新触发，逐站点抑制）

| 规则 | 站点 | 决定 | 理由 |
|---|---|---|---|
| MA0038（Make method static，info，已弃用） | 6：LoopbackSocks5TcpServer:172、TcpRedirectAcceptor:146、TcpRedirectSessionStore:230、TcpRedirectSetup:97/165、UdpSessionSetup:181 | SUPPRESS-LOCAL ×6（`#pragma warning disable/restore MA0038` + 理由）→ **终态：规则级关闭**（站点 pragma 已于 2026-09-20 用户裁定后移除，见 batch-log「配置补记」） | 参数内联后这些方法的实例依赖只剩 primary ctor 捕获参数（分析器不建模捕获字段）→ 建议 static 不可实现（读取 primary ctor 参数的 static 方法 = CS9105 编译失败）。依据 .editorconfig MA0041 条目既有裁定"src 出现同类命中时按局部 pragma 处置"；继任规则 CA1822（已启用）正确建模 primary ctor 捕获且这些站点 0 命中 → 族覆盖不丢失。dotnet format 定向实测（--diagnostics MA0038, 5 文件）exit 0 / 0 条 |
| ParameterOnlyUsedForPreconditionCheck.Local | 1：TcpCoordinatorFakes.FakeListenerFactory（`throwOnCreate`，L152） | SUPPRESS-LOCAL ×1（`// ReSharper disable once` + 理由） | 测试失败注入 seam：`if (throwOnCreate) throw` 使"listener 分配失败"路径可确定性触发（此前参数仅用于字段初始化故未触发）。与同文件既有兄弟站点 FakeRelayFactory（L264，同叙事）同一形态与措辞 |

---

## 补录（2026-09-20 独立审计发现的记录缺口）

审计用「75 条规则 × 归一化匹配」核对本文件时发现 6 条规则（8 个变体名）只有间接/缩写式记录，此处补齐逐条处置，确保 75/75 全覆盖。

| 规则 | 站点 | 决定 | 理由 |
|---|---|---|---|
| UnusedVariable | 5：tests/WinForward.Core.Tests/Socks5AddressCacheTests.cs:34/42（`first`/`second`）、src/WinForward.NdisApi/NdisApiDriver.cs:63/114（`gateLease`） | **FIX ×5** | 前两者为改写后遗留的未用局部，删除；后两者是 `using (var gateLease = _controlGate.Enter())` 的租约变量，改为 `using (_controlGate.Enter())`（租约是 ref struct，`using (expr)` 同样保证退出即释放）——语义不变，构建 + 测试兜底 |
| UnusedParameter.Global | 5：src/WinForward.Runtime/Capture/CaptureLifecycle.cs:19/20/21、src/WinForward.Runtime/FlowDispatcher.cs:72 | **FIX ×5** | 接口方法实现无一消费 `cancellationToken`（`IPacketActionExecutor.PassAsync/BlockAsync`、`ICaptureLifecycle` 各方法）；从接口与实现一并移除该参数，调用点同步（`ProxyAsync(packet, server, token)` 保留 token）。行为等价：折叠前该 token 从未被读取 |
| UnusedType.Global | 2：tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs:175（`BarrierListenerFactory`）、src/WinForward.Windows/IProcessAttributor.cs:12（`UnsupportedProcessAttributor`） | **DELETE ×2** | 二者全仓 0 引用（rg 实测）；`BarrierListenerFactory` 系并发测试改写为 TCS 门后的遗留夹具（09-19 审计已标"确认无用可删"），`UnsupportedProcessAttributor` 为未接入的占位实现。删除后 Release build 0 警告 + 725 测试全绿 |
| CollectionNeverQueried.Global | 1：tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs:186（`Listeners`） | **DELETE ×1** | 唯一写入方是已删除的 `BarrierListenerFactory`；集合只被更新从未被读取，随该类一并删除 |
| ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator | 6：src/WinForward.Core/FlowTable.cs:97、src/WinForward.Runtime/TcpRedirect/TcpPendingSynSetup.cs:230/253/315、src/WinForward.Runtime/UdpProxy/UdpSetupCooldownTable.cs:68/98 | **SUPPRESS-GLOBAL** | 性能红线：循环转 LINQ 会引入委托分配与迭代器状态机（hot-path.md）；显式循环保留。规则级键 `resharper_foreach_can_be_partly_converted_to_query_using_another_get_enumerator_highlighting = none`（`.editorconfig`，实测生效于审计复核：键中立化后 6 处命中重现） |
| ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator | 1：benchmarks/WinForward.Benchmarks/Stability/UdpRawBaselineScenario.cs:62 | **SUPPRESS-GLOBAL** | 同上（LINQ 家族性能红线）；键 `resharper_foreach_can_be_converted_to_query_using_another_get_enumerator_highlighting = none`（审计复核：中立化后该命中重现） |

**命名变体说明**：`ConvertToAutoPropertyWhenPossible` / `ConvertToAutoPropertyWithPrivateSetter` 两条由本文件 L31 的家族行 `ConvertToAutoProperty(+PrivateSetter/WhenPossible)/PropertyCanBeMadeInitOnly`（FIX ×8）覆盖——该批按成员形态逐点处置（只读字段→`{ get; }`、私有 setter→`{ get; private set; }`），与 jb 的三条变体 ID 一一对应。

**B4 收尾补充（审计归档）**：MoveLocalFunctionAfterJumpStatement / SeparateLocalFunctionsWithJumpStatement 同批已处置（FIX ×8），规则级抑制键与理由见 `.editorconfig` 对应条目。

---

## 终局独立审计处置（2026-09-20，独立 check 代理）

方法与逐条证据：`suppression-audit.md`。结论：**128 条 pragma 行 → 126 NECESSARY / 2 STALE；16 键 → 14 NECESSARY / 2 个 0 命中按 POLICY 保留；无 FIXABLE**。

| 处置 | 对象 | 依据 |
|---|---|---|
| **删除** | `src/WinForward.Protocols/PacketChecksums.cs:29` `disable once MemberCanBePrivate.Global` | 无抑制全量重跑中该规则全解决方案 0 命中 |
| **删除** | `src/WinForward.Core/IPAddressValue.cs:105` `disable once ConvertIfStatementToReturnStatement` | 该文件仅 40/47 命中（由 39/46 pragma 覆盖），105 行站点不再被检出 |
| 保留（POLICY） | `resharper_arrange_trailing_comma_in_singleline_lists_highlighting`（既有）、`resharper_foreach_can_be_partly_converted_to_query_highlighting`（新增） | 均 0 命中；前者为 trailing-comma 风格族封口、后者为 LINQ 性能红线家族封口（孪生 inspection 命中 6 次） |
| 保留（NECESSARY） | 其余 126 条 pragma + 14 键 | 抑制置空后的 729 条报告逐条落在同文件同规则窗口内（块形态取块区间） |

**处置后计数**：pragma 指令行 134 → 132（`disable once` 118 → 116；块 8 行不变）；`.editorconfig` 键 16 不变。
