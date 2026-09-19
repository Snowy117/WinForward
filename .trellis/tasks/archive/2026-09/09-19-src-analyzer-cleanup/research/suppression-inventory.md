# Suppression Inventory（审计输入，2026-09-19 终态工作树）

范围：本任务工作树全部抑制 = .editorconfig `severity = none` + 全部 `#pragma warning disable`。
来源标记：[NEW]=本任务新增（相对 base c8a5453）；[PRE]=任务前已存在。

## 1. .editorconfig `severity = none` 条目

| 规则 | 位置 | 理由（注释摘要） | 来源 |
|---|---|---|---|
| RS1039 | .editorconfig:L162 | https://github.com/dotnet/roslyn-analyzers/issues/7436 - False positives from valid GetDeclaredSymbol calls | [PRE] |
| CA1822 | .editorconfig:L171 | CA1822 / RCS1146 / RCS1197 / MA0038（成员可标记为 static）：dotnet format 的 fixer 在 C# 14 extension 块 （receiver 参数语义）和 InterpolatedStringHandlerArgument("") 约定 | [PRE] |
| RCS1146 | .editorconfig:L172 |  | [PRE] |
| RCS1197 | .editorconfig:L173 |  | [PRE] |
| MA0038 | .editorconfig:L174 |  | [PRE] |
| MA0041 | .editorconfig:L175 |  | [NEW] |
| IDE0130 | .editorconfig:L182 | IDE0130（namespace 与文件夹不匹配）：dotnet format 的 fixer 试图改 MSBuild 的 document properties （<RootNamespace> 等），触发 System.NotSupportedException: Changing docum | [PRE] |
| MA0048 | .editorconfig:L189 | 第三方分析器仓库级抑制：以下规则都与本项目架构/技术栈根本不兼容，无法靠改代码消除。 MA0048（文件名必须匹配类型名）：本项目刻意采用"一文件多类型"组织模式—— contracts/records 聚合文件（如 AccessControlContracts.cs 含 UserId/Access | [PRE] |
| MA0004 | .editorconfig:L193 | MA0004（async 调用缺 ConfigureAwait(false)）：本项目全部运行在 ASP.NET Core / 服务端 host， 无 SynchronizationContext，ConfigureAwait(false) 在现代 .NET 中无意义（与旧 .NET Framewo | [PRE] |
| VSTHRD003 | .editorconfig:L198 | VSTHRD003（避免 await/return 外部任务）：与 MA0004 同理——服务端无 SynchronizationContext， 不存在 await 外部任务导致的死锁风险。此外 quality-guidelines 强制的并发测试"释放屏障"模式 （TaskCompletionS | [PRE] |
| MA0026 | .editorconfig:L202 | MA0026 / S1135（TODO 注释必须处理）：TODO 注释是本项目有意的 issue 追踪工作流， 用于标注待办（如需先加数据库列才能实现的 RemoveUserAsync），不是 bug。强制处理会破坏该工作流。 | [PRE] |
| S1135 | .editorconfig:L203 |  | [PRE] |
| S1144 | .editorconfig:L212 |  | [PRE] |
| MA0056 | .editorconfig:L213 |  | [PRE] |
| MA0051 | .editorconfig:L214 |  | [PRE] |
| S927 | .editorconfig:L215 |  | [PRE] |
| MA0009 | .editorconfig:L216 |  | [PRE] |
| MA0015 | .editorconfig:L217 |  | [PRE] |
| S6966 | .editorconfig:L218 |  | [PRE] |
| S4144 | .editorconfig:L219 |  | [PRE] |
| S4136 | .editorconfig:L220 |  | [PRE] |
| S3459 | .editorconfig:L221 |  | [PRE] |
| S3237 | .editorconfig:L222 |  | [PRE] |
| S2699 | .editorconfig:L223 |  | [PRE] |
| S4456 | .editorconfig:L224 |  | [PRE] |
| VSTHRD002 | .editorconfig:L225 |  | [PRE] |
| VSTHRD103 | .editorconfig:L226 |  | [PRE] |
| VSTHRD200 | .editorconfig:L227 |  | [PRE] |
| MA0040 | .editorconfig:L230 | MA0040（方法支持取消但未传 CancellationToken）：测试用例多数只断言单一行为，刻意不串取消令牌； 在 src/ 仍受保护，避免生产代码遗漏 token。 | [PRE] |
| MA0182 | .editorconfig:L234 | MA0182（internal 类型从未被引用）：测试 fixture 经常通过反射 / assembly 扫描被消费 （GameSettingsContract.Discover(assembly)、UserConfigurableSettingsContract 等）， 编译期分析器看不到反射引 | [PRE] |
| xUnit1012 | .editorconfig:L241 | These xUnit analyzers were disabled temporarily to let us move to the new xUnit and get past several component governance issues. The following issue | [PRE] |
| xUnit1030 | .editorconfig:L242 |  | [PRE] |
| xUnit1031 | .editorconfig:L243 |  | [PRE] |
| xUnit2005 | .editorconfig:L244 |  | [PRE] |
| xUnit2020 | .editorconfig:L245 |  | [PRE] |
| xUnit2023 | .editorconfig:L246 |  | [PRE] |
| xUnit2029 | .editorconfig:L247 |  | [PRE] |

## 2. 源码 pragma（按规则聚合）

| 规则 | 站点数 | 来源 | 理由（首条注释摘要） | 站点 |
|---|---|---|---|---|
| CA1068 | 2 | [NEW] | Deliberate shape: the token follows the identifying arguments and precedes the test-only seam factories, so th | Socks5UdpTransport.cs:158, Socks5ControlConnection.cs:72 |
| CA1416 | 7 | [PRE] | Reading the const inlines a literal from the windows-gated relay factory; the value (the dial budget this even | ClientResetInjector.cs:156, TcpThroughputScenario.cs:1, GcSoakScenario.cs:1, TcpEofScenario.cs:1, Socks5HandshakeBenchmarks.cs:1, TcpRelayBenchmarks.cs:1 … |
| CA1419 | 1 | [NEW] | The handle is produced only by the driver's Open path via FromRawHandle; a public constructor would let caller | NdisApiAbi.cs:137 |
| CA1859 | 1 | [NEW] | The private composition chain deliberately types its logger as IRuntimeLogger (the composition seam); narrowin | Program.cs:157 |
| CA2012 | 3 | [NEW] | The lease guard throws synchronously before any ValueTask is produced; the Action-bound lambda pins exactly th | NdisPacketActionExecutorBatchingTests.cs:239, NdisCapturePumpTests.cs:325, GcSoakScenario.cs:682 |
| CA2208 | 1 | [NEW] | The paramName deliberately names the null member (the context parameter itself is never null); these analyzers | UdpProxySession.cs:72 |
| IDE0060 | 1 | [NEW] | The cancellationToken parameter is fixed by the dispatcher's fragment-handler delegate; the teardown path take | TcpProxyCoordinator.cs:573 |
| IDE1006 | 1 | [NEW] | The t_ prefix is the team convention for [ThreadStatic] fields (2026-09-19): the editorconfig naming rules can | PacketRuntime.cs:28 |
| MA0015 | 2 | [MIX] | The paramName deliberately names the null member (the packet struct is never itself null); both analyzers only | FlowDispatcher.cs:59, UdpProxySession.cs:72 |
| MA0042 | 3 | [NEW] | The lease wraps a Monitor (thread-affine): awaiting would resume on another thread and Monitor.Exit in the lea | NdisAdapterGateMapTests.cs:66, NdisAdapterGateMapTests.cs:106, NdisApiAbiTests.cs:128 |
| MA0182 | 1 | [NEW] | Consumed by WinForward.Cli through InternalsVisibleTo (Program.cs wires it into the pump's retry logging) and  | AdapterTransientRetryLogGate.cs:13 |
| MA0189 | 6 | [NEW] | Fixed buffers mirror the iphlpapi MIB_UDP6ROW_OWNER_PID declaration; size/offsets pinned by AssertManagedLayou | IPHelperAbi.cs:68, IPHelperAbi.cs:90, IPHelperAbi.cs:95, IPHelperAbi.cs:112, NdisApiAbi.cs:74, NdisApiAbi.cs:93 |
| RCS1075 | 1 | [PRE] | Rollback must continue restoring the remaining adapters. | CaptureLifecycle.cs:189 |
| RCS1163 | 1 | [NEW] | The cancellationToken parameter is fixed by the dispatcher's fragment-handler delegate; the teardown path take | TcpProxyCoordinator.cs:573 |
| RCS1229 | 4 | [NEW] | Deliberate non-async warm entry (hot-path.md #3): the steady-state send path must not pay an async state machi | Socks5UdpTransport.cs:229, TcpProxyCoordinator.cs:309, TcpProxyCoordinator.cs:384, TcpProxyCoordinator.cs:441 |
| S1215 | 1 | [PRE] | Baseline hygiene: flush warmup garbage before recording the zero-GC mark. | GcSoakScenario.cs:359 |
| S3869 | 1 | [PRE] | Handing the raw event handle to the driver is the documented ABI here. | AdapterListWatcher.cs:44 |
| S3928 | 2 | [MIX] | The paramName deliberately names the null member (the packet struct is never itself null); both analyzers only | FlowDispatcher.cs:59, UdpProxySession.cs:72 |
| S5034 | 1 | [NEW] | (见文件内注释) | GcSoakScenario.cs:682 |
| VSTHRD002 | 4 | [MIX] | (见文件内注释) | SetupExecutor.cs:271, NdisCapture.cs:336, NdisCapture.cs:345, GcSoakScenario.cs:682 |

合计：editorconfig 37 条；pragma 规则 20 类 / 站点 44 处。

> 注：多数条目共享同一注释块（如 CA1822/RCS1146/RCS1197/MA0038/MA0041 一块，MA0026/S1135 一块，tests 段首注释块覆盖其下多条），完整理由见 `.editorconfig` 原位注释；pragma 的完整理由见各站点行内 `//` 注释。本清单用于审计索引，权威来源是工作树本身。
