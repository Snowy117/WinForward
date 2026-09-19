# 独立抑制审计（2026-09-19）

审计对象：本任务工作树（base c8a5453）全部被忽略的诊断 = `.editorconfig` 37 条 `severity = none` + 25 文件 38 行 `#pragma warning disable`（44 个"规则×站点"）。
审计者：独立子代理（非实现者）。审计原则：实证优先、先启用实验取证、处置可逆、绝不 `git checkout/stash`。
方法：临时启用全部抑制（editorconfig 全量 verify+report；pragma 全量翻转为 restore 后 verify+report）→ 逐条裁定 → 应用处置 → 复跑终局门。

## 0. 实验方法与原始证据

| 实验 | 操作 | 结果 | 证据文件 |
|---|---|---|---|
| E1 启用（warning 级） | 37 条 `severity = none` 临时改 `warning`，全量 `dotnet format --severity info --verify-no-changes --report` | **43 秒即构建失败**：`TreatWarningsAsErrors=true` 把 MA0041(11)/MA0048(205)/IDE0130(19) 等 warning 级命中直接升为 error → 部分报告（证明多条抑制是"构建级"防护，非仅门禁） | `/tmp/audit-warninglevel-partial.log` |
| E1' 启用（suggestion 级） | 同 37 条改 `severity = suggestion`（等效 info 呈现，但不触发 TWAEs 升级），全量 verify+report（覆盖全部 9 工程） | exit 2；**526 条诊断 / 17 条规则命中**（清单见 §1）；20 条零命中 | `/tmp/audit-ec-suggestion.log`、`/tmp/audit-report-ec-suggestion/format-report.json` |
| E1'' 恢复复验 | 从 /tmp 备份恢复 `.editorconfig`（md5 一致）后复跑 verify | **exit 0** | `/tmp/audit-restore-ec.log` |
| E2 pragma 启用 | 25 个 .cs 备份到 /tmp 后，全部 38 行 `#pragma warning disable` → `#pragma warning restore`（保留配对行），全量 verify+report | exit 2；**66 条诊断，38/38 站点全部命中（0 空转）** | `/tmp/audit-pragma.log`、`/tmp/audit-report-pragma/format-report.json` |
| E2' 恢复复验 | 逐文件从 /tmp 恢复（md5 全部一致）后复跑 verify | **exit 0** | `/tmp/audit-restore-pragma.log` |
| E3 默认严重级 | 反射加载 6 个分析器程序集（Meziantou/Roslynator/Sonar/VSThreading/xUnit/SDK NetAnalyzers），枚举 `SupportedDiagnostics` 的 `DefaultSeverity` | 见 §3 表；**关键更正：CA1822 默认 Info（非 design 假定的 warning）**，MA0004/MA0048/MA0026/MA0009/MA0056/MA0015/MA0051/xUnit1030/1031 等默认 Warning | `/tmp/sevtest/out3.txt` |
| E4 模式存在性 | grep 全仓：AccessControlContracts / RemoveUserAsync / GameSettingsContract / ASP.NET Core / EF / TODO / Regex / `extension(` / InterpolatedStringHandlerArgument / tests 内 `yield`/`virtual`/`throw new Argument*` | 全部为 0；另：src ConfigureAwait 231 处、`[InlineData(null)]` 1 处、Assert.Same/Assert.Empty 等大量使用 | 见下各条证据 |
| E5 溯源 | `git log -S` 上述失实文本 | 全部来自 **初始提交 eb0cab8**（仓库初始化时继承的其它仓库文本），本仓/`.trellis` 无任何复现记录 | — |

## 1. editorconfig 37 条逐条裁定（E1' 命中 / 样本）

| 规则 | 位置 | 默认级 | 命中 | 样本（文件:行） | 裁定 | 处置 |
|---|---|---|---|---|---|---|
| RS1039 | L162 | Warning | 0 | — | UNNECESSARY（仅适用于 Roslyn 分析器开发代码；本仓 0 处 `SemanticModel.GetDeclaredSymbol`，无分析器/源生成器工程） | **删除** |
| CA1822 | L171 | **Info** | 0 | — | UNNECESSARY（0 命中；原"fixer 误修 extension 块/InterpolatedStringHandlerArgument"理由本仓 0 处该模式、不可复现；info 级无构建风险） | **删除** |
| RCS1146 | L172 | Info | 8 | Policy.cs:18,19,20,21,22(×6)；AdapterLocalAddressProvider.cs:177；Socks5UdpAssociateTests.cs:143 | RATIONALE-STALE（原注释误标为"成员可标记为 static"；实为 Use conditional access——null 守卫链） | 保留，注释改写 |
| RCS1197 | L173 | Info | 0 | — | UNNECESSARY（同 CA1822 块：理由不可复现；0 命中） | **删除** |
| MA0038 | L174 | Info | 0 | — | UNNECESSARY（同上，且规则已被作者弃用） | **删除** |
| MA0041 | L175 | Info | 11 | TcpFragmentHandlingTests.cs:238-247(×10)；TcpCoordinatorFakes.cs:255 | NECESSARY（primary ctor 参数属性，加 static 必 CS9105/CS9113，B4 实测；理由准确） | 保留，仅删"与 CA1822 共同恢复"句 |
| IDE0130 | L182 | Info | 19 | 全部 tests/WinForward.Core.Tests/TestHelpers/*.cs（如 TcpCoordinatorFakes.cs:15） | RATIONALE-STALE（src 命中为 0；实态=测试辅助刻意用父命名空间；原"contracts 聚合/partial、fixer 改 MSBuild 属性崩溃"系继承文本） | 保留，注释改写 |
| MA0048 | L189 | Warning | 205 | src 132/tests 48/bench 25；Domain.cs、ConfigurationModels.cs、NdisApiAbi.cs、TcpRedirectInterfaces.cs、BenchmarkShared.cs、TcpCoordinatorFakes.cs | RATIONALE-STALE（模式真实=一文件多类型；原举例 AccessControlContracts.cs/EF 实体/JsonStreamEvent 全仓不存在） | 保留，注释改写为真实样本 |
| MA0004 | L193 | Warning | 99 | tests 78/bench 17/src 4（Program.cs:224、LayeredCaptureRunner.cs:145、IdleExpirySweeper.cs:65、TcpProxyRelay.cs:138） | RATIONALE-STALE（原"ASP.NET Core host"失实：本仓为 Exe 控制台/服务主机、0 AspNetCore 引用；规则确实命中且 warning 级） | 保留，注释改写 |
| VSTHRD003 | L198 | Warning | 39 | tests 25/src 10/bench 4（LayeredCaptureRunner.cs:378 等） | RATIONALE-STALE（"服务端 host"失实；真实理由：无 JTF/VS 宿主、await 均为同进程自建任务、测试 TCS 屏障系 spec 要求） | 保留，注释改写 |
| MA0026 | L202 | Warning | 0 | 全仓 0 条 TODO | RATIONALE-STALE（原举例 RemoveUserAsync/数据库不存在，已删；0 命中，工作流声明无法实证） | 保留（design §5"不确定则保留"），注释改写 |
| S1135 | L203 | Warning | 0 | 同上 | 同上 | 保留，注释改写 |
| S1144 | L212 | Warning | 1 | TcpPendingSynSetupTests.cs:40（未读取的 Listeners 属性，真阳性） | NECESSARY（站点真实；原"反射驱动"表述不准确） | 保留，注释改写（真阳性说明） |
| MA0056 | L213 | Warning | 0 | 测试树 0 处 virtual/abstract 成员 | UNNECESSARY（触发载体不存在） | **删除** |
| MA0051 | L214 | Warning | 2 | LayeredCaptureRunnerRefreshTests.cs:16；UdpSetupQueueTests.cs:168 | NECESSARY（长场景方法，规则准确） | 保留 |
| S927 | L215 | Warning | 4 | TcpCoordinatorFakes.cs:203,277；BatchedPassReinjectionE2eTests.cs:143,172 | NECESSARY（实为 `_` vs 接口声明名，理由成立） | 保留 |
| MA0009 | L216 | Warning | 0 | 全仓 0 处 Regex | UNNECESSARY（原"测试配置类"分组成员不存在载体） | **删除** |
| MA0015 | L217 | Warning | 0 | tests 0 处 `throw new Argument*` | UNNECESSARY（tests 段无载体；src 的刻意 member-path paramName 由局部 pragma 覆盖） | **删除** |
| S6966 | L218 | Warning | 0 | 同步 socket/stream 调用大量存在 | 保留（构造在使用中，防退化），注释说明 |
| S4144 | L219 | Warning | 2 | RuntimeLoggingTests.cs:146；TcpProxyCoordinatorRewriteTests.cs:60 | NECESSARY（fake 内重复实现） | 保留 |
| S4136 | L220 | Warning | 0 | 测试树重载普遍 | 保留（同上） |
| S3459 | L221 | Warning | 0 | fixture 属性普遍 | 保留（同上） |
| S3237 | L222 | Warning | 0 | fixture setter 普遍 | 保留（同上） |
| S2699 | L223 | Warning | 3 | AdapterListWatcherTests.cs:93（不抛即通过）；NdisApiAbiTests.cs:42；NdisAdapterGateMapTests.cs:96 | NECESSARY（规则准确） | 保留 |
| S4456 | L224 | Warning | 0 | 测试树 0 处 `yield` | UNNECESSARY（原"迭代器桩"载体不存在） | **删除** |
| VSTHRD002 | L225 | Warning | 4 | AdapterListWatcherTests.cs:109,121；NdisCapturePumpTests.cs:355；FakeAdapterListChangeSource.cs:64 | NECESSARY（测试刻意阻塞探测时序） | 保留 |
| VSTHRD103 | L226 | Warning | 0 | 阻塞调用存在（仅在同步方法内） | 保留（构造在使用中），注释说明 |
| VSTHRD200 | L227 | Warning | 2 | Socks5AddressCacheTests.cs:27,56（局部函数 Resolver 返回 ValueTask） | NECESSARY（站点真实；原注释块未单列，已在新注释块说明） | 保留 |
| MA0040 | L230 | Info | 30 | 全部 tests（NdisCapturePumpTests.cs:211 等） | NECESSARY（理由准确：测试刻意不串 token；src/bench 不在抑制范围） | 保留，注释微调 |
| MA0182 | L234 | Info | 1 | TcpCoordinatorFakes.cs:175（BarrierListenerFactory，当前无引用=真阳性） | RATIONALE-STALE（原"反射消费/GameSettingsContract.Discover"不存在；实态为未接线 fixture 辅助类型） | 保留，注释改写 |
| xUnit1012 | L241 | Warning | 0 | `[InlineData(null)]` 存在（UdpRelayTests.cs:334，参数可空） | 保留（构造在使用中） | 保留 |
| xUnit1030 | L242 | Warning | 94 | LayeredCaptureRunnerRefreshTests.cs 40 处等（`.ConfigureAwait(false)` 测试惯例） | RATIONALE-STALE（原"迁移新 xUnit/组件治理/roslyn#75093"系继承文本） | 保留，注释块改写 |
| xUnit1031 | L243 | Warning | 2 | AdapterListWatcherTests.cs:109,121 | 同上（阻塞操作；与 VSTHRD002 同站点） | 保留，注释块改写 |
| xUnit2005 | L244 | Warning | 0 | Assert.Same 大量使用（均为引用类型） | 保留（构造在使用中） | 保留 |
| xUnit2020 | L245 | Warning | 0 | Assert.True 大量使用 | 保留（同上） | 保留 |
| xUnit2023 | L246 | Info | 0 | 集合断言大量使用 | 保留（同上） | 保留 |
| xUnit2029 | L247 | Warning | 0 | Assert.Empty 大量使用 | 保留（同上） | 保留 |

统计：NECESSARY 21（含"保留+注释微调"）；RATIONALE-STALE 8（RCS1146/IDE0130/MA0048/MA0004/VSTHRD003/MA0026/S1135/MA0182，另 xUnit 注释块覆盖 7 条）；UNNECESSARY **8 条删除**；FIXABLE 0（无事例达到"修复明显优于抑制"）。

## 2. pragma 38 行逐条裁定（E2 全部命中 → 全部 NECESSARY）

| 站点 | 规则 | 命中数 | 理由与代码事实核对 |
|---|---|---|---|
| ClientResetInjector.cs:156 | CA1416 | 1 | 读取 windows 门控工厂的 const（编译期内联），值本身平台中性 ✓ |
| CapturePumpBenchmarks.cs:1 | CA1416 | 4 | 基准用 fake reader 驱动 managed 管线 ✓ |
| Socks5HandshakeBenchmarks.cs:1 | CA1416 | 2 | 仅使用工厂常量（纯值）✓ |
| TcpRelayBenchmarks.cs:1 | CA1416 | 2 | relay 为平台中性 managed 代码 ✓ |
| GcSoakScenario.cs:1 | CA1416 | 4 | 同上（工厂/relay 特性与实现分离）✓ |
| TcpEofScenario.cs:1 | CA1416 | 5 | 同上 ✓ |
| TcpThroughputScenario.cs:1 | CA1416 | 4 | 同上 ✓ |
| NdisApiAbi.cs:74 / :93 | MA0189 | 5 / 2 | ndisapi.h 逐字节镜像 + AssertManagedX64Layout 尺寸/offset 背书 ✓ |
| NdisApiAbi.cs:137 | CA1419 | 1 | 句柄仅经 Open 路径产生；无 interop 返回该类型 ✓ |
| IPHelperAbi.cs:68 / :90 / :95 / :112 | MA0189 | 1 / 1 / 1 / 1 | iphlpapi 声明镜像 + AssertManagedLayout/毒化 padding 测试背书 ✓ |
| NdisCapture.cs:336 / :345 | VSTHRD002 | 1 / 1 | 专用泵线程刻意同步等待，相邻代码注释完整 ✓ |
| SetupExecutor.cs:271 | VSTHRD002 | 1 | 专用 worker 线程同步等待，注释完整 ✓ |
| GcSoakScenario.cs:682 | S5034,VSTHRD002,CA2012 | 3 | 专用 load 线程同步消费 ValueTask 恰一次，注释完整 ✓ |
| GcSoakScenario.cs:359 | S1215 | 2 | GC 基线卫生（warmup 后标记零 GC 前 flush）✓ |
| AdapterTransientRetryLogGate.cs:13 | MA0182 | 1 | 经 IVT 被 WinForward.Cli（Program.cs）与测试消费（分析器看不到 IVT）✓ |
| AdapterListWatcher.cs:44 | S3869 | 1 | 向驱动交付裸 event handle 是本仓文档化 ABI ✓ |
| CaptureLifecycle.cs:189 | RCS1075 | 1 | rollback 必须继续恢复其余 adapter ✓ |
| FlowDispatcher.cs:59 | MA0015,S3928 | 2 | paramName 刻意指向 null 成员（packet 为 struct 不可能为 null）✓ |
| UdpProxySession.cs:72 | MA0015,S3928,CA2208 | 4 | 同上（context.ReceiveWindowPool 成员路径）✓ |
| Socks5ControlConnection.cs:72 | CA1068 | 1 | 注入缝重载镜像 public 重载的 CT 位置 ✓ |
| Socks5UdpTransport.cs:158 | CA1068 | 1 | CT 位于标识参数后、测试缝工厂前（生产调用点可读性）✓ |
| Socks5UdpTransport.cs:229 / TcpProxyCoordinator.cs:309,:384,:441 | RCS1229 | 1 / 1 / 1 / 1 | 逐包热路径刻意非 async 入口（hot-path.md #3）✓ |
| TcpProxyCoordinator.cs:573 | IDE0060,RCS1163 | 2 | 委托签名固定、teardown 刻意不取 caller token ✓ |
| PacketRuntime.cs:28 | IDE1006 | 1 | `t_` [ThreadStatic] 团队约定，命名规则无法匹配特性（B5 实证）✓ |
| Program.cs:157 | CA1859 | 1 | 私有组合链刻意以 IRuntimeLogger 为 seam，收窄级联收益为零 ✓ |
| NdisAdapterGateMapTests.cs:66,:106；NdisApiAbiTests.cs:128 | MA0042 | 1 / 1 / 1 | lease 包装 Monitor（线程亲和），await 会换线程致 Monitor.Exit 抛异常 ✓ |
| NdisCapturePumpTests.cs:325；NdisPacketActionExecutorBatchingTests.cs:239 | CA2012 | 1 / 1 | 守卫在产生 ValueTask 前同步抛出，无消费 ✓ |

结论：**38/38 站点均为真实抑制（0 空转），全部 NECESSARY，零改动**（VSTHRD002 三处站点理由由相邻代码注释承载，审计核对一致）。

## 3. 应用的全部变更

删除（8 条 editorconfig 条目，均在 E1' 下 0 命中且载体不存在）：
`RS1039`、`CA1822`、`RCS1197`、`MA0038`、`MA0009`、`MA0056`、`MA0015`(tests)、`S4456`(tests)。

改写（9 处注释块，均按本仓实证事实重写并注明 2026-09-19 审计；含"原注系其它仓库文本"说明）：
RCS1146（拆分纠正）、IDE0130、MA0048、MA0004、VSTHRD003、MA0026+S1135、tests 段头（含 S1144/S2699/S927/MA0051 实测数）、MA0182、xUnit 段（覆盖 7 条）；MA0041 仅删过时句。
新增：文件头审计说明（列出删除清单与"未来触发按局部 pragma 处理"的指引）；pragma 无任何改动。

## 4. 终局门（处置后，工作树终态）

| 门 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build WinForward.slnx -c Release --no-restore` | **0 Warning / 0 Error**，exit 0 |
| 测试 | `dotnet test WinForward.slnx -c Release --no-restore` | **725/725 passed**（基线 724，B4 新增 1），exit 0 |
| 门禁 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0** |

原始日志：`/tmp/audit-final-gates.log`（BUILD_EXIT=0 / TEST_EXIT=0 / FORMAT_EXIT=0）；最终注释微调后复跑 build/test/format 复核：`/tmp/audit-final-gates2.log` + FORMAT_EXIT=0（工作树终态 = 全部处置已应用）。

## 5. 遗留、发现与建议

1. `TcpCoordinatorFakes.BarrierListenerFactory`（tests/…/TcpCoordinatorFakes.cs:175）当前无引用（MA0182 真阳性，注释文档完备）。若确认无用可删除；MA0182 条目届时可再评估移除。
2. MA0026/S1135 的"TODO 工作流"声明无法在仓内实证（0 TODO）。若团队确认不使用 TODO，可直接删除这两条（删除后普通 TODO 会触发 warning 级构建错误）。
3. 更正 design §5 的假设：CA1822 默认严重级实为 **Info**（E3 反射实测）；本次删除不含任何"warning 默认级且模式存在"的规则。
4. 被删条目均为"当前不触发"：未来若触发（RS1039/CA1822/RCS1197/MA0038 为 info；MA0009/MA0056/MA0015/S4456 为 warning→构建失败），按 spec 以局部 `#pragma`+理由或按需重建规则级条目处理。
5. 审计过程已遵守可逆性：两轮实验（E1'/E2）的中间态均从 /tmp 备份恢复并 md5 校验一致、复跑 verify exit 0；工作树终态含全部处置且三门全绿。

---

## 复审补记（2026-09-19，用户指示）

用户指出：抑制的作用域必须用 editorconfig glob 按证据路径限定；不得以"当前 src/ 无命中"之类的
现状陈述为由做全局豁免（未来可能变化）。据此调整两条：

| 条目 | 调整前 | 调整后 | 理由 |
|---|---|---|---|
| IDE0130 | 全局 `severity = none` | 限定 `[tests/WinForward.Core.Tests/TestHelpers/**.cs]` | 19 处命中全部在该目录（测试辅助类型的父命名空间惯例）；src 与其它 tests 目录重回管辖 |
| MA0041 | 全局 `severity = none` | 移入 `[tests/**.cs]` 段 | 11 处命中全部为测试 fixture 的 primary-ctor 捕获误报；src 未来同类命中按局部 pragma 处置 |

调整后复验：`dotnet format --verify-no-changes` exit 0、Release build 0 warning、test 725/725（见 batch-log）。
其余全局条目复核结论：MA0048（205 命中横跨 src/tests/bench）、MA0004（99 命中横跨三树）、VSTHRD003（39 命中横跨三树）
的理由均为跨树成立的仓库级策略，维持全局；tests 段条目本就经 `[tests/**.cs]` 限定。
