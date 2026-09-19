# Batch Log — dotnet format 诊断清零（B1/B2）

追加式记录：每轮 pass 的命令、真实 exit code、改动文件数/应用数量、门禁结果。
基线（B0，固化于本目录）：base commit `c8a5453`；`dotnet build WinForward.slnx -c Release --no-restore` 0 warning；`dotnet test` 725/725 全绿。

| 轮次 | 命令 | exit | 结果 |
|---|---|---|---|
| B1 | `dotnet format whitespace WinForward.slnx --no-restore` | 0 | 9 文件：8 FINALNEWLINE（7 src + 1 tests）+ 2 WHITESPACE hunk（EchoReceiver.cs XML 注释缩进） |
| B1 gate | `dotnet build WinForward.slnx -c Release --no-restore` | 0 | Build succeeded, 0 Warning / 0 Error |
| B1 verify | `dotnet format whitespace WinForward.slnx --no-restore --verify-no-changes` | 0 | whitespace 子命令范围已清零（IMPORTS 3 处不在 whitespace 子命令范围，待 style pass 处理） |

## B1 详情

- `EchoReceiver.cs`：2 处 WHITESPACE —— `/// <see cref="BeginWindow"/>...` 两行 XML doc 缩进 4→0，纯注释缩进。
- 8 文件 FINALNEWLINE：BoundedSetupQueue / MacAddress / NdisAdapterModeController / NdisPacketReinjector / PacketFlowClassifier / SetupExecutor / Socks5AddressCache（src）+ TcpRedirectInjectorTests（tests）。
- diff 审查：`git diff --stat` = 9 files, 10 insertions, 10 deletions，全部为上述格式变更，无代码语义改动。

## B2 详情（style pass + analyzers pass + 手工项）

| 轮次 | 命令 | exit | 结果 |
|---|---|---|---|
| B2-style | `dotnet format style WinForward.slnx --no-restore --severity info --diagnostics IDE0300 IDE0301 IDE0305 IDE0042 IDE0230 IDE0028 IDE0017 IDE0007 IDE0057 IDE0039 IDE0034 IDE0031 IDE0037 IDE0074 IDE0056` | 0 | 76 文件；集合表达式/解构/区间/var/局部函数等全部应用 |
| B2-style gate | `dotnet build WinForward.slnx -c Release --no-restore` | **1（52 error S1481）** | IDE0042 fixer 留下 52 处未使用的解构局部变量 → Sonar S1481（error 级） |
| B2-style 修复 | 手工：52 处未使用解构元素改 `_` discard（24 文件） | — | build 恢复 0 warning；同时把 fixer 生成的 87 处 PascalCase 解构局部变量改 camelCase、3 处局部函数改 PascalCase（`Decide`/`Resolver`×2），消除 IDE1006/IDE0090 回归 |
| B2-analyzers | `dotnet format analyzers WinForward.slnx --no-restore --severity info --diagnostics MA0003 MA0007 MA0154 RCS1123 RCS1118 RCS1205 MA0020 RCS1212 RCS1001 RCS1139 MA0176 MA0089 CA2250 CA1854` | 0 | 52 文件；后 0 warning build |
| B2-analyzers gate | build + `dotnet test WinForward.slnx -c Release --no-restore` | 0 | 725/725 全绿 |
| B2-manual CA1861 | 7 站点 hoist 为 `private static readonly` 字段（4 个新字段：s_resetThenTeardownOrder/s_teardownOnlyOrder/s_remoteAddresses/s_expectedRentalEvents×2 文件） | — | 常量数组不再每次调用分配 |
| B2-leftover style | `dotnet format style ... --diagnostics IDE0300 IDE0007 IDE0042 IDE0090`（补跑） | 0 | 清除 4 条 fixer 首轮未应用的残留；fixer 再次产出 PascalCase 解构 → 手工再改 camelCase（TcpEofScenario/CapacityTests） |
| B2-leftover analyzers | `dotnet format analyzers ... --diagnostics MA0007 MA0154`（补跑） | 0 | MA0007 7 处尾逗号应用；MA0154 4 处自动 + 6 处（`<c>null</c>/<c>internal</c>/<c>using</c>/<c>finally</c>`）fixer 不覆盖 → 手工改 `<see langword="..."/>` |
| 终门（本批） | `dotnet format whitespace ... --verify-no-changes` | 0 | whitespace 子命令范围全清 |
| 终门（本批） | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 2（预期） | 1369 → **549**；B1/B2 目标规则全部归零 |

### B2 规则级结果（baseline → 现在，权威 verify report 对比）

| 规则 | 数量 | 规则 | 数量 | 规则 | 数量 |
|---|---|---|---|---|---|
| MA0003 402→0 | IDE0300 97→0 | MA0007 80→0 | IDE0042 51→0 | IDE0305 38→0 | RCS1123 33→0 |
| IDE0230 12→0 | IDE0028 12→0 | IDE0017 10→0 | MA0154 10→0 | IDE0007 9→0 | RCS1118 8→0 |
| FINALNEWLINE 8→0 | IDE0057 8→0 | CA1861 7→0 | RCS1205 6→0 | MA0176 5→0 | MA0089 4→0 |
| IDE0039 3→0 | MA0020 3→0 | IMPORTS 3→0 | CA2250 2→0 | WHITESPACE 2→0 | IDE0034 2→0 |
| IDE0301 2→0 | RCS1001 1→0 | IDE0056 1→0 | RCS1212 1→0 | IDE0031 1→0 | IDE0074 1→0 |
| IDE0037 1→0 | CA1854 1→0 | RCS1139 1→0 | | | |

副作用顺带减少的 J 规则：MA0076 116→115、CA1859 15→14、RCS1206 2→1（各 1 处站点被 M 类修复覆盖）。

### 本批引入/遗留回归（已处理与待后续）

- **已修复**：S1481 52 error（discard 化）；IDE1006 87 处解构局部变量 camelCase 化；IDE0090 1 处（`new(...)`）；4 处 fixer 首轮未应用的 M 残留；MA0007/MA0154 残留。
- **留给 B5（机制已定，零改名）**：IDE1006 238 = baseline 230 + 8（RCS1118 把局部变量 const 化后落入 D 桶"局部 const 被判 Pascal"；B5 的 `constants` 规则去掉 `local` 即全部归零，含 baseline 85 处同为 D 桶）。新增 8 站点：GcSoakScenario.maximumFrameSize、DurableCaptureBundle.maximumFrameSize、ClientResetInjector.connectAttempts、NdisApiBatchedSendAbiTests.worstChunkBytes、TcpEndpointRewriteTests.tcpOffset×2、UdpPacketParsingTests.udpOffset×2。
- **留给 B4（判定项，本批不动）**：MA0041 11 处（TcpFragmentHandlingTests 10 + TcpCoordinatorFakes 1）—— 实测 `static` 不可行（CS9105 "Cannot use primary constructor parameter in this context" / CS9113），MA0041 对其已 deprecated 的误报；属性读取的是 primary ctor 参数，无法静态化，需 B4 决定重构或抑制。
- 剩余 549 条全部为 J 类判定规则（MA0076 115、MA0042 33、RCS1085 21、IDE0290 20、CA1859 14、CA1512 14、RCS1261 12、MA0040 12、CA1068 11、MA0189 11、IDE0059 6、RCS1229 4、RCS1163 4、CA2012 3、MA0001 3、RCS1222 2、RCS1084 2、RCS1021 2、IDE0270/IDE0060/RCS1206/CA1419/CA2016/RCS1236/CA2208/MA0159/MA0182/VSTHRD104/MA0166 各 1）。

### 改动规模

`git diff --stat`（相对 base c8a5453，未提交）：143 files changed, 805 insertions(+), 802 deletions(-)。其中 B1 9 文件、B2 其余约 134 文件。

### 门禁（本批终态）

- `dotnet build WinForward.slnx -c Release --no-restore` → Build succeeded, 0 Warning / 0 Error
- `dotnet test WinForward.slnx -c Release --no-restore` → Passed! Failed: 0, Passed: 725, Total: 725
- `dotnet format whitespace WinForward.slnx --no-restore --verify-no-changes` → exit 0
- `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` → exit 2，549 条（全为 B4/B5 范围规则）

## 2.2 检查（round 1，B1+B2 复核）

复核者：独立 check 子代理（非实现者）。范围仅 B1+B2；J 类与 IDE1006 属 B4/B5，未触碰。对象 = 工作树 143 文件（相对 base c8a5453）。

### 复核方法（独立证据，不依赖实施者自述）

1. **规范化 token 级 diff**：143 个改动文件逐一做 base ↔ 工作树 token 序列比对（剔除空白/注释，保留数字、字符串、运算符 token）。结果：**13 文件零 token 变化**（B1 纯格式：final newline / 注释缩进），130 文件有 token 变化。
2. **命名实参标签剥离对比**：再比对时把实参位 `name:` 标签整体剥离：token op 数 1263 → 861，**差值 402 恰等于 MA0003 诊断总数**，证明 402 处命名实参均为"就地插入标签"，无实参位置/绑定变化（20 个文件由此变为零 token 变化）。
3. **行级多重集扫描**：42 对"同内容不同顺序"候选行全部核为 MA0007 尾逗号；唯一真实重排是 RCS1205 的 7 处（RuntimeHeartbeatTests `gc.Read`×5、Socks5UdpAssociateTests `socketFactory`/`onSocketReady`、TcpProxyCoordinatorLifecycleTests `payload`/`mutateFrame`），**全部是命名实参之间的重排**（编译期重绑定，语义不变）。
4. **解构顺序核对（IDE0042）**：逐处回溯 `Deconstruct` 来源：`List<(byte[] Frame, bool TowardMstcp, nint AdapterHandle)>`（InjectedFrames）、`Channel<(FlowKey, Endpoint, byte[], MacAddress)>`（FakeResponseSink.Responses）、`List<(Endpoint, byte[])>`（Sent）、`Task<(long Received, TransferOutcome Outcome)>`（TcpEofScenario.ReceiveAsync）、`Task<(Socket Peer, Socket Relay)>`（CreateSocketPairAsync，两处 Socket 同型 → 人工 `pairRelay`/`upstreamRelaySocket` 命名已核对位置映射正确）、`DequeueForFlush`（Step/Lease/Length/EnqueuedAt）、`record`/日志事件记录（Level/Name/Fields）等；元素位置与旧属性访问一一对应，含实施者手工 discard 化与 camelCase 重命名（`logged.Level→level`、`response.Payload→payload`、`previous.End→end` 等）。
5. **CA1861 提升核对**：`s_expectedRentalEvents`×2、`s_resetThenTeardownOrder`、`s_teardownOnlyOrder`、`s_remoteAddresses` 仅出现在 `Assert.Equal(expected, actual)` 与 `Select(...)`，无写入/原地排序，静态共享安全；数组元素与提升前逐项一致。
6. **IDE0230 u8 字面量核对**：`{0x20,0x21}`→`" !"u8`、`{0x42}`→`"B"u8`、`{0x70}`→`"p"u8`、`{9}`→`"\t"u8`、`{0x56,0x78}`→`"Vx"u8`、`{9×6}`→6×`"\t"`、`{0x51,0x52,0x53}`→`"QRS"u8` 全部逐字节一致。
7. **MA0176 核对**：5 处 `Guid.Parse(...)` → `new Guid(0xdd8cd9a1, 0x0b6f, 0x4e6e, 0x9a, …)` 逐分量比对（含 `11223344-…-ff00` 的尾零字节），与 `ToString("D")` 断言闭环；fixer 保留原字符串注释。
8. **IDE1006 身份比对**：baseline `format-report.json` 的 230 条 vs 新 verify 的 238 条按 (文件, 消息) 对比 —— 差集恰为 8 条新增 "upper case" = RCS1118 产生的 const 局部（GcSoakScenario/DurableCaptureBundle.maximumFrameSize、ClientResetInjector.connectAttempts、NdisApiBatchedSendAbiTests.worstChunkBytes、TcpEndpointRewriteTests.tcpOffset×2、UdpPacketParsingTests.udpOffset×2）→ **无改名回归**；"Missing prefix" 143 与 "Prefix not expected" 2 逐文件数量不变。
9. **守恒校验**：全部改动 .cs 的 `Assert.` 次数 base=2328 / head=2328，token 级 0 处 `Assert` 变化；无新增 `#pragma`、无 `SuppressMessage`、`.editorconfig` 未改、`git diff --check` 干净。
10. **hot-path 复核**：FlowDispatcher / PacketChecksums / TcpProxyRelay / PacketRuntime(PacketLease) / Socks5UdpTransport 等热路径改动仅限命名实参（编译期）、RCS1123 括号、`Slice(0,n)`→`[..n]`（等价零分配）与 `??=`；`[.. x]` 替换 `ToArray()/ToList()` 的站点全在冷路径（诊断快照 RuntimeCounters/RuntimeHeartbeat、sweeper TcpRedirectSessionStore、teardown UdpProxyCoordinator/TcpPendingSynSetup、适配器枚举、`Snapshot()`），集合表达式对数组/List/IReadOnlyList 目标与旧代码语义、分配等价。

### 发现与修复（本批范围内）

1. **MA0003 fixer 遗留 17 处列 0 实参行 —— 已修复**：6 文件（Socks5UdpAssociateTests 10、Socks5UdpTransportSendTests 3、RuntimeDiagnosticLoggingTests 1、EndpointAndPolicyTests 1、Socks5UdpConnresetTests 1、SelfTrafficBenchmarks 1）。fixer 插入 `label:` 时丢掉了被替换行的缩进；实测 `dotnet format whitespace`（write 模式、定向文件）**不会**修正这类行（Roslyn formatter 对"行首即命名实参标签"的行不产生缩进诊断），故按被替换行的原缩进手工修正（纯空白，行为零变更），修正后定向 whitespace verify exit 0、全局门禁全绿。
2. **MA0041 跳过理由 —— 复核通过**：11 处 = TcpFragmentHandlingTests.FragmentHarness 10（primary 构造器 + `=> 参数` 属性）+ TcpCoordinatorFakes.ParkingDisposeListener.Inner 1；抽查确认属性体读取 primary ctor 参数，加 `static` 必 CS9105/CS9113；MA0041 自标记 deprecated（CA1822 旧版）→ 留 B4 处置（建议记"分析器误报 + 局部抑制/规则级处置"）。
3. **给 B5 的提示（超范围，未动）**：现 238 条 IDE1006 中 93 条为 "must begin with upper case"：92 条是局部 const（D 桶，B5 去掉 `constants` 的 `local` 即归零），另 1 条是 `tests/WinForward.Core.Tests/TcpRedirectInjectorTests.cs:82` 的 `private void record(...)`（baseline 既有，由 catch-all `members_should_be_pascal_case`（applicable_kinds=*）触发）——只改 `constants` 规则**不会**归零该条，B5 需单独处置（改名为 PascalCase 或调整该 kind 规则）。
4. 未发现其他语义偏差；B1/B2 目标规则全部归零，无 M 类残留（MA0041 已按上述理由留给 B4）。

### 门禁（复核后重跑，Release，含上述手工修复）

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build WinForward.slnx -c Release --no-restore` | exit 0，0 Warning / 0 Error |
| 测试 | `dotnet test WinForward.slnx -c Release --no-restore` | exit 0，Passed 725 / Failed 0 / Total 725 |
| 空白子系统 | `dotnet format whitespace WinForward.slnx --no-restore --verify-no-changes` | exit 0 |
| 全量 verify | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 2（预期），**549** 条 = 548 info + 1 error |

规则分布（与 B2 终态一致）：IDE1006 238、MA0076 115、MA0042 33、RCS1085 21、IDE0290 20、CA1859 14、CA1512 14、RCS1261 12、MA0040 12、MA0189 11、MA0041 11、CA1068 11、IDE0059 6、RCS1229 4、RCS1163 4、MA0001 3、CA2012 3、RCS1222 2、RCS1084 2、RCS1021 2，以及各 1 的 11 项（VSTHRD104 / RCS1236 / RCS1206 / MA0182 / MA0166 / MA0159 / IDE0270 / **IDE0060(error)** / CA2208 / CA2016 / CA1419）。WHITESPACE / IDE0055 / FINALNEWLINE / IMPORTS 命中 **0**。全部落在 B4/B5 范围，B1/B2 目标规则全 0。

## B4a（输出确定性类判定：MA0076 / MA0001 / MA0159）

范围：仅三条"输出确定性"规则——MA0076 隐式区域性插值 115（src 57 / tests 22 / bench 36）、MA0001 缺失 StringComparison 3、MA0159 OrderBy→Order 1。IDE1006 属 B5、结构/性能类属 B4b，未触碰；`.editorconfig` 未动、无新增 pragma/SuppressMessage（`git diff` 实证 0 处）。

| 轮次 | 命令/操作 | exit | 结果 |
|---|---|---|---|
| B4a-manual | 手工 4 处：MA0001×3 加 `StringComparison.Ordinal`；MA0159×1 | — | 语义零变更（详见 disposition-log） |
| B4a-manual gate | `dotnet build WinForward.slnx -c Release --no-restore` | 0 | 0 Warning / 0 Error |
| B4a-MA0076 pass1 | `dotnet format analyzers WinForward.slnx --no-restore --severity info --diagnostics MA0076` | 0 | 35 文件、49 处 `string.Create(CultureInfo.InvariantCulture, $"…")` 包装 |
| B4a-MA0076 pass2 | 同上（重跑收敛） | 0 | 累计 **79 处包装**（bench 21 / src 37 / tests 21），覆盖全部 115 条诊断 |
| B4a-手工归一 | RuntimeLogging.cs 修复器 trivia 残留（`)` 独立行）并回单行 | — | 纯空白，语义零变更 |
| B4a 定向 verify | `dotnet format analyzers … --verify-no-changes --diagnostics MA0076 MA0001 MA0159 --report …` | **0** | report = `[]`；三条规则全清（0 剩余、0 抑制） |
| B4a gate build | `dotnet build WinForward.slnx -c Release --no-restore` | 0 | 0 Warning / 0 Error |
| B4a gate test | `dotnet test WinForward.slnx -c Release --no-restore` | 0 | Passed 725 / Failed 0 / Total 725 |
| B4a 全量 verify | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore --report …` | 2（预期，B4b/B5 规则仍在） | 549 → **430**；histogram 与"B2 分布 − 119"逐规则完全一致（无新增规则、无规则回涨） |

### B4a 修复器行为观察（供后续判定批次参考）

- 单次 `dotnet format analyzers` 调用只应用每个文件的一部分 fix：同一文件内多处替换同时携带"新增 `using System.Globalization;`"的编辑，Roslyn BatchFixer 做批量变更合并时逐轮收敛——重跑同一命令即可，无需人工干预（MA0076 两轮全清）。
- fixer 新增的 `using System.Globalization;` 按 B1 有序导入插入，无重复 using、无 IMPORTS/IDE0005 回归（全量 verify 实证）。
- 全部 79 处站点逐 hunk 审查：日志 / 异常消息 / 配置诊断路径 / 线程名 / 测试失败消息 → InvariantCulture 化 = 期望的确定性改进；无刻意本地化输出站点；无 steady-state 站点（logging/diagnostics 属 spec hot-path.md 冷边界；`string.Create(provider, ref DefaultInterpolatedStringHandler)` 无中间字符串分配，分配中性）。
- tests 21 处：断言条件零变更（仅消息 / 期望事件字符串，生产与假件两侧同步 Invariant），en_US 环境下无观察差异；725 全绿兜底。
- NdisApiBatchedSendAbiTests 的 baseline MA0076 站点由 B2 的 RCS1118 const 化消除（已知常量值不触发 MA0076），故 B4a 起点 115 条（tests 22）。


## B4b（结构/判断类剩余规则，2026-09-19）

范围：除 IDE1006（B5）外的全部 192 条剩余诊断（430 − 238）。逐规则处置明细见 `disposition-log.md` 的 B4b 表。IDE1006/命名未触碰；MA0041 为唯一新增的规则级 `.editorconfig` 条目（并入既有 CA1822 块，中文注释）。

| 轮次 | 命令/操作 | exit | 结果 |
|---|---|---|---|
| B4b-manual1 | CA1512(14)→ThrowIf*、CA1859(13)→具体类型、IDE0270/RCS1206/RCS1021/RCS1084/RCS1236、IDE0059(6)、RCS1163(tests 3)→discard、IDE0055 邻域手工 | — | 逐处与调用方同步 |
| B4b-manual2 | MA0042(30)→await CancelAsync/await using/DisposeAsync/await dispatch；RCS1261(12)→await using；MA0040(12)→token 转发；CA1068(9)→重排+调用点；MA0166/VSTHRD104 修正 | — | 见 disposition-log |
| B4b-manual3 | 抑制：MA0189(11, 5 pragma 对)、MA0041(11, .editorconfig)、RCS1229(4)、MA0042 GetResult(3)、CA2012(3)、CA1419、MA0182、CA2208(扩展既有 pragma)、IDE0060+RCS1163(1) | — | 全部附理由注释 |
| B4b fixer1 | `dotnet format style WinForward.slnx --no-restore --severity info --diagnostics IDE0290` | 0 | 20/20 转换；含回退/校验语义的类由 fixer 保留为字段初始化器（`?? 回退`/`?? throw` 逐字保留） |
| B4b gate1 | `dotnet build … -c Release` | 1 | 2 error CS0019：RCS1084 首次去 cast 的 `??` 类型不成立（UdpBurst/UdpLoss）→ 补 `(IRuntimeLogger?)` 转换 |
| B4b fixer2 | `dotnet format analyzers … --diagnostics RCS1085` | 0 | 21/21 自动属性 |
| B4b gate2 | build | 1 | 5 error：`.AsTask()` 变体触发 xUnit CS0619/xUnit2014 + 级联 S5034 → 改回 Action 绑定 lambda + CA2012 pragma |
| B4b verify1 | `dotnet format … --verify-no-changes --diagnostics <B4b 全规则>` | 2 | 7 条残留：CA1859 级联（Program.cs:183）、CA1068 重排不完整（ConnectOnceAsync 的 addressCache 无默认值）、VSTHRD104 修复后 async 化引发的 MA0042×5（Wait/Result） |
| B4b-manual4 | 残留修复：Program.cs 恢复 IRuntimeLogger+CA1859 pragma（级联理由）；ConnectOnceAsync CT 移至真正末尾；AdapterListWatcher 测试全 await 化（WhenAny/IsCompleted 探针） | — | 无新增诊断 |
| B4b gate3 | `dotnet build WinForward.slnx -c Release --no-restore` | 0 | 0 Warning / 0 Error |
| B4b gate4 | `dotnet test WinForward.slnx -c Release --no-restore` | 0 | **725/725 全绿**（Failed 0, Skipped 0） |
| B4b 全量 verify | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **2（预期）** | 430 → **238 = 全部 IDE1006**（B5 范围）；B4b 规则全部归零，无 IDE0079/IDE0055/WHITESPACE 回归 |

### B4b 规则级结果（430 → 238）

FIX 151 条：CA1512 14、RCS1085 21、IDE0290 20、CA1859 13、CA1068 9、MA0042 30、RCS1261 12、MA0040 12、IDE0059 6、CA2016 1、RCS1163 3、RCS1222 2、RCS1084 2、RCS1021 2、IDE0270 1、RCS1206 1、RCS1236 1、VSTHRD104 1、MA0166 1。
SUPPRESS 41 条：MA0041 11（规则级）、MA0189 11（局部 pragma ×5 对）、RCS1229 4、MA0042 3（Monitor 线程亲和）、CA2012 3、CA1068 2（seam 镜像）、CA1859 1（级联）、CA1419 1、MA0182 1、CA2208 1（并入既有 pragma 列表）、IDE0060+RCS1163 1（同站点）。

### 修复器行为观察（供 B5/审计参考）

- IDE0290 fixer 对"字段初始化器可用"的类采用**保守半转换**（primary ctor + `_x = 参数` 字段保留），对有 `?? 回退`/`?? throw` 的站点逐字保留语义；build/725 全绿背书。
- RCS1085 fixer 正确处理跨实例访问（`MacAddress.Equals` → `other.IsValid`）与构造器赋值（property 赋值），无 `ref`/`Interlocked` backing field 冲突。
- CA1859 会**级联**：改造入口参数后会沿私有调用链继续报下一层；Program.cs 处因级联进入 composition seam（bundle/generation 工厂）而改为局部抑制并注明理由。
- CA1068 要求 CT 在**含默认值参数之前、全部必选参数之后**：`addressCache`（无默认值）必须排在 CT 之后 → 真正的"最后一个参数"。

## B5（N 命名：IDE1006 归零，2026-09-19）

范围：仅 IDE1006（238 条 = baseline 230 + B2/RCS1118 局部 const 8）。用户裁定约定：public/protected（含 protected internal）字段 PascalCase；internal 与 private 同级（static → s_camelCase、`[ThreadStatic]` → t_camelCase、实例 → _camelCase）；private/internal const → PascalCase；局部 const → camelCase；按**声明**可访问性匹配。其它规则未触碰。

### 1. editorconfig 重配（6 处 + 约定注释，无其它改动）

| 配置项 | 变更 |
|---|---|
| `non_private_static_fields.applicable_accessibilities` | `public, protected, internal, protected_internal, private_protected` → `public, protected, protected_internal` |
| `non_private_readonly_fields.applicable_accessibilities` | 同上 |
| `static_fields.applicable_accessibilities` | **新增** `internal, private_protected, private`（原无此项） |
| `instance_fields.applicable_accessibilities` | **新增** `internal, private_protected, private` |
| `constants.applicable_kinds` | `field, local` → `field` |
| 注释 | 命名配置区新增 2026-09-19 中文约定块；各规则块注释同步修正 |

### 2. t_ 机制试探（先于 fixer；实证多规则语义）

| 轮次 | 命令 | exit | 结果 |
|---|---|---|---|
| B5-probe | `dotnet format style WinForward.slnx --no-restore --severity info --verify-no-changes --diagnostics IDE1006 --report`（editorconfig 临时新增平行规则 `thread_static_fields`：field + static + `internal, private_protected, private` + 前缀 `t_`，**声明在 s_ 规则之前**、severity suggestion） | 2 | 报告 106 条：`t_recycleCache` 仍报 `Missing prefix: 's_'`（由 s_ 规则裁决）；全仓 **0 条 `missing t_`**；s_ 静态字段零新增。→ 多规则"任一满足"语义不成立：每个符号由**单一裁决规则**判定，同域平行规则在任何声明位置都不能表达"s_ 或 t_" |

结论：移除试探规则（保留将把 s_ 字段整体改指 t_），按 implement.md 兜底方案在 `src/WinForward.Core/PacketRuntime.cs` 为 `[ThreadStatic] t_recycleCache` 加**单站点** `#pragma warning disable IDE1006`（成对 restore，理由注释：命名规则无法匹配 `[ThreadStatic]` 特性，t_ 为团队约定）。t_recycleCache 不参与改名。

### 3. 改名应用（dotnet format fixer 不可用 → 逐行审阅的替换脚本）

**关键事实（实测）**：SDK 10.0.401 的 `dotnet format` 无法应用 IDE1006 —— `NamingStyleCodeFixProvider` 不支持 Fix All in Solution（工具固定请求 `FixAllScope.Solution`）。solution / project / project+`--include` 三种范围均只打印 `Unable to fix IDE1006. Code fix NamingStyleCodeFixProvider doesn't support Fix All in Solution.`，exit 0 但零改动（3 次实测）。故按"脚本化改名 + 编译器/verify 兜底"执行：**365 处替换、52 文件**的改名计划（旧→新 + 引用行）逐条人工审阅后应用，随后：

| 桶 | 站点数 | 改名（约定） | 备注 |
|---|---|---|---|
| A1 private static 缺 s_ | 66（+ t_recycleCache 1 走 pragma） | `s_camelCase` | bench 34 / src 25 / tests 7（合计 66；A1+A2 的 s_ 站点共 bench 34 / src 34 / tests 7 = 75） |
| A2 internal static PascalCase | 9（InterceptionHealthMonitor 3、TcpProxyRelay 3、LayeredCaptureRunner 2、ClientResetInjector 1） | `s_camelCase` | 重配后由 static_fields 新裁决（原先被 non_private_static 的 Pascal 满足而"免检"） |
| A3 internal 实例字段 PascalCase | 12（SetupWorkItem 7、TcpSetupWork 2、UdpSetupWork 3） | `_camelCase` | 其中 `Tcp`/`Udp`（internal readonly）原先被 non_private_readonly 满足而"免检"，重配后落入 instance_fields |
| 局部 const 现 PascalCase | 17（B5 新暴露：constants 不再管局部后由 locals 规则裁决） | `camelCase` | `Syn/Ack/Fin/Guid/InternalGuid/IPHelperId/HandleCount/ResolvesPerHandle/IpTotalLengthOffset/TcpFlagsOffset/FramePoolName/FoldBlockInterval/MaximumPathLength`（多文件同名各归各符号）；`IPHelperId` 定为 `ipHelperId` |
| 小写方法名 | 1（`TcpRedirectInjectorTests.record`） | `Record` | members 规则既有命中 |

引用同步：经 IVT 的 tests/bench 引用（`TcpProxyRelay.ArmThrottleTicks`×5、`TcpProxyRelayFactory.RelayConnectAttemptTimeout`×3、`SetupWorkItem`/`TcpSetupWork`/`UdpSetupWork` 的 item 链式引用）、XML doc `<see cref="Tcp/Udp"/>` 均已同步；零改名区实证保持（`exception.CancellationToken`、`slot.Completion`、`using System.Text.Json.Serialization;`、`TcpResetBuilder.TcpFlagsOffset`、`var record = new {}`、`ITcpRelay.Completion` 等）。执行中修复 2 类脚本自身问题：`CancellationToken` 字段的类型/名称同行（只改末位）与 `exception.CancellationToken` 误伤（已还原）；`Server` 同名字段两条消息（SetupExecutor `_` / HotPathAllocationGateTests `s_`）前缀分桶修正（测试侧 14 处 `_server` → `s_server`）。

### 4. 门禁（B5 终态）

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build WinForward.slnx -c Release --no-restore` | exit 0，0 Warning / 0 Error（改名跨项目引用零遗漏，一次通过） |
| 测试 | `dotnet test WinForward.slnx -c Release --no-restore` | exit 0，Passed 725 / Failed 0 / Total 725 |
| 定向 verify | `dotnet format style WinForward.slnx --no-restore --severity info --verify-no-changes --diagnostics IDE1006 --report` | **exit 0**，report = `[]`（106 条诊断全部消除/抑制） |
| 全量 verify | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0**（本任务核心验收达成；0 条输出） |

105 个诊断站点逐一复核为"站点已按约定改名"，无遗漏；其它批次的规则未回涨（全量 verify 0 输出实证）。

## 2.2 检查（final，B4a/B4b/B5 + 抑制逐条核验）

复核者：独立 check 子代理（非实现者）。对象 = 工作树全部未提交 diff（相对 base c8a5453，178 文件）。round 1 已覆盖 B1/B2（whitespace + 机械组），本轮覆盖 B4a/B4b/B5 与全部抑制。

### 1. B4b 判断类修复语义复核（逐规则）

- **IDE0290（20 处主构造器）**：全部逐站点核对。含回退/校验语义的站点以字段初始化器逐字保留：InterceptionHealthMonitor（`_thresholds ?? DefaultThresholds`/`_logger ?? NullRuntimeLogger.Instance`/`_time ?? TimeProvider.System`/`_onTrigger = onTrigger`）、ConsoleRuntimeLogger（`_writer ?? Console.Error`）、ClientResetInjector（healthSignal/bufferPool/timeProvider 回退 + `CapacityResets` 自动属性 `new TcpResetCooldownTable(capacity ?? 16_384)`）、NdisPacketReinjector（`driver ?? throw ArgumentNullException`）、UdpAdapterTargetSource（`_snapshot = CreateSnapshot(host, CopyMap(byStableId))`，CreateSnapshot/CopyMap 均为 static，字段初始化器合法且与 ctor 赋值等价）、StallWindow（`_source = CreateArmed(lifetime)`）、TcpRedirectSession/Store/Setup/Acceptor/SessionStore、UdpSessionSetup/UdpSetupQueueBudget、bench TcpFlood/Connection、tests fakes（FakeAttributor/FakeCaptureGeneration/FakeListenerFactory/TrackingSocket（基类 ctor 参数透传）/FakeTransportFactory）。无初始化器互依、无捕获顺序变化（所有初始化器仅读参数/常量）。含锁类（InterceptionHealthMonitor `Lock _gate`、TcpRedirectSessionStore `_gate`+`_shutdown` CTS）锁字段保持字段初始化器，语义不变。
- **CA1512（14）**：逐站点比对原守卫：`if (x <= 0) throw new AOORE(nameof(x))` → `ThrowIfNegativeOrZero(x)`（BoundedSetupQueue×2/FlowTable/NativeBufferPool×2/NdisPacketBufferPool/SetupExecutor/Socks5UdpTransportFactory/Socks5UdpTransport/UdpResponseReinjector/ProcessAttribution）；`capacity < 1` → `ThrowIfLessThan(capacity, 1)`（Socks5AddressCache）；`initialCapacity < 0` → `ThrowIfNegative`（UdpAssociationTable×2 计 1 新）。异常类型/paramName（CallerArgumentExpression = 实参名）一致；全仓测试无 ParamName/Message 断言（grep 实证）。Socks5ControlConnection 两处 `if (IsCancellationRequested) throw new OperationCanceledException(tok)` → `tok.ThrowIfCancellationRequested()`：抛出同为 `new OperationCanceledException(token)`，等价。
- **CA1068（9 重排 + 2 抑制）**：重排仅涉 private/internal 签名（NdisCapturePump.RunLoop、Socks5ControlConnection 私有 ctor/ConnectOnceAsync/ResolveAddressesAsync、TcpProxyRelay.PumpAsync、TcpRedirectSession primary ctor、UdpProxyCoordinator.SendOnReadySessionSpanAsync、UdpProxySessionContext record、UdpSessionSetup.CreateSessionAsync）；**无 public API 签名重排**（diff 全量扫描 added 行实证）。CT 均移至最后一个必选参数之后、可选参数保持末尾；调用点同步（编译保证，类型不同必报错）。两处抑制（ConnectAsync 缝重载镜像 public 重载；CreateAsync 缝工厂后置）理由与代码事实一致。
- **MA0042（30 FIX）**：24 处 `Cancel()`→`await CancelAsync()`（tests）逐 hunk 核对：await 使取消回调完成先于后续断言，确定性只增不减；3 处 CTR `using`→`await using`（CancellationTokenRegistration.DisposeAsync 语义=Dispose）；1 处 `dispatch.Result`→`await dispatch`（任务在 WaitAsync 后已完成）；2 处 `upstream.Dispose()`→`return upstream.DisposeAsync()`（Stream.DisposeAsync 默认即 Dispose；NetworkStream 同关闭 socket）。3 处 `GetAwaiter().GetResult()` 抑制：实证 gate lease 为 Monitor（NdisNativeCallGate/NdisAdapterGateMap：`Monitor.Enter` + lease `Monitor.Exit`），await 换线程必抛 SynchronizationLockException——理由准确。
- **RCS1261（12）**：NetworkStream（ownsSocket: true）`using`→`await using` 与 CTR 注册：系统无缓冲差异，DisposeAsync 等价关闭。
- **RCS1085（21）**：全部自动属性转换；grep 全仓无 `ref/Volatile/Interlocked` 触碰被消除的 backing field（FlowTable.Capacity、NativeBufferPool.Capacity/BufferSize、NativeLease.Length、MacAddress.IsValid、Tombstones/CapacityResets、UdpProxySession.Flow/FlowGeneration/Association、TcpAcceptedConnection.Socket/RemoteEndPoint、CapturePacketProcessor.OnBatchCompleted（原已是属性）等）。
- **CA1859（13 FIX + 1 抑制）**：站点全为 private static（SoakRunner/ConfigurationModels×4/LayeredCaptureRunner/UdpAdapterTargetSource/AdapterIdentity/AdapterLocalAddressProvider/ProcessAttribution×4）；返回/参数收敛为 List/Dictionary/HashSet/数组，调用方编译核对；Program.cs 抑制理由（composition seam + 级联，line 273 工厂链同持 IRuntimeLogger）与代码一致。
- **MA0040（12）+ CA2016**：src 4 处（Task.Run token、DrainItem/LaunchSetup/ScheduleSessionSetup `TrySetCanceled(token)`——TCP 处 token 恒为 default 等价，UDP 处恒为 `_shutdown.Token` 且消费者只按 IsCanceled/_shutdown.IsCancellationRequested 过滤，异常 token 差异无观察面）；bench 8 处（accept/sender Task.Run、SendToAsync、窗口 Delay 传 sender token；drain Delay 显式 `CancellationToken.None` 与理由一致）。
- **IDE0059（6）/IDE0270/RCS1206/RCS1084/RCS1021/RCS1236/RCS1163×3/VSTHRD104/MA0166/RCS1222**：逐站点核对。IDE0059 均为死初始化删除（编译器 definite-assignment 兜底）；IDE0270 `?? throw` 保留 paramName(`buffers`)；RCS1084 `(IRuntimeLogger?)x ?? NullRuntimeLogger.Instance` 与三元式逐字等价（null→Null）；RCS1236 catch-filter 与原 if/else throw 等价（null 时不捕获、栈原样）；RCS1163 lambda 改 discard 且保留在用处（`async (index, _)`）；VSTHRD104 断言改为 `Task.Delay + IsCompleted`/`WhenAny.WaitAsync`（失败模式由 Assert 变 TimeoutException，均失败判定，可接受）。

### 2. B4a 抽查（MA0076）

- 计数实证：diff 新增 `string.Create(CultureInfo.InvariantCulture, $"…")` 恰 **79** 处（与 batch-log 一致）。
- 抽样 ≥10 处（RuntimeLogging EndPoint 分支、NdisApi driver/abi 异常消息、ConfigurationModels 诊断路径、SetupExecutor 线程名、LayeredCaptureRunner 事件字段、GcSoakScenario 监管消息、UdpSessionBenchmarks、TcpEndpointRewriteIncrementalTests/TcpReversePrefilterTests 断言消息）：语法闭合正确、无嵌套插值陷阱（同行的第二个 `$""` 是独立实参、不含文化敏感 hole，未被包——符合 MA0076 语义）；无双重包装；`:X8/:F0` 等格式符经 handler provider 正常格式化。MA0001×3 显式 Ordinal（char 重载默认即 Ordinal，零行为变更）、MA0159 `Order(comparer)` 与 identity-key OrderBy 等价——与 disposition-log 一致。
- 热路径纪律：全仓 `string.Create(...)` 包装无一落在 FlowDispatcher/PacketChecksums/TcpProxyRelay/Socks5UdpTransport/Core 热文件（grep 实证）；均属日志/异常/诊断/测试消息（spec hot-path.md 冷边界），且 `string.Create(provider, ref handler)` 无中间字符串分配。

### 3. B5 改名复核

- 旧标识符残留扫描：19 个代表性旧名（DefaultWindow/DefaultResolver/ConnectAttemptTimeout/RelayConnectAttemptTimeout/ArmThrottleTicks/TombstoneGracePeriod/…）全仓 **0 命中**；`item.Handler/Completion/Flow/Server/Tcp/Udp/Slot/...` 旧成员访问 0 命中。
- 跨文件引用：`TcpProxyRelayFactory.s_relayConnectAttemptTimeout`、`TcpProxyRelay.s_armThrottleTicks` 在 tests/bench（IVT）同步；XML doc `<see cref="_tcp"/>`/`<see cref="_udp"/>` 存在且编译零警告（cref 可解析）；`SetupWorkItem` 链式引用（tests 的 `item._completion`/`_cancellationToken`）同步。
- 零改名区保持实证：`TcpResetBuilder.TcpFlagsOffset`（private const → PascalCase）、`exception.CancellationToken`（误伤还原）、`t_recycleCache` 引用一致。
- `src/WinForward.Core/PacketRuntime.cs` pragma：成对 disable/restore 包裹 `[ThreadStatic] private static PacketLease? t_recycleCache;` 单字段；理由（editorconfig 命名规则无法匹配特性、t_ 为团队约定）与 B5-probe 实证一致，准确。
- `.editorconfig` 命名重配（6 处）与用户裁定约定一致（public/protected 系 PascalCase；internal/private static→s_、实例→_；const 去 local；t_ 单点 pragma）。

### 4. 抑制合理性逐条核验

- **本任务新增**（pragma 48 行 / 24 对 + 1 个 editorconfig 条目）：逐条打开站点核验，全部理由成立——MA0189 6 站点与 `AssertManagedX64Layout`（8836/1566、Reserved@36/Buffer@52、TcpAdapterList 全 offset）及 `AssertManagedLayout`（56/RemoteAddress@24、80/InterfaceLuid@32）+ UnicastAddressInventory 毒化 padding 实证一致；CA2012 3 处（守卫同步抛出先于 ValueTask 产生，Action 绑定 lambda 只钉同步异常）与 NdisCapturePump.RunAsync 守卫实现一致；MA0042 3 处 Monitor 线程亲和（见上）；RCS1229 4 处均为非 async ValueTask 暖入口 + 冷尾 async helper（hot-path.md #3 双重背书）；IDE0060/RCS1163 站点参数由 dispatcher fragment-handler 委托签名固定且方法体确实不消费 token；MA0182 由 Cli Program.cs:278 经 IVT 消费 + 测试直接构造；MA0015/S3928/CA2208 为既有 member-path paramName 模式（quality-guidelines.md 已文档化）；CA1859（Program.cs composition seam）、IDE1006（t_）理由与代码事实一致；MA0041 规则级理由成立（命中属性全部读 primary ctor 参数，FragmentHarness/ParkingDisposeListener.Inner 实证）。
- **修正 1 处理由措辞**：`NdisApiAbi.cs` CA1419 pragma——原注 "no interop signature needs one (native calls use raw nint handles)" 与代码事实不符（NdisApiAbi 的 P/Invoke 以 `NdisApiSafeHandle` **参数**接收句柄），改为精确表述（无 import 以 SafeHandle 作返回类型；入参句柄不需要公共构造器）。
- **[PRE] 粗筛（仅标注，不处置）**：多处注释引用的架构/符号在本仓库不存在，疑似从他项目继承——MA0004/VSTHRD003（"ASP.NET Core / 服务端 host"，全仓无 ASP.NET 代码）、MA0048（AccessControlContracts.cs 不存在）、MA0026/S1135（RemoveUserAsync/数据库列不存在）、MA0182（GameSettingsContract.Discover/UserConfigurableSettingsContract 不存在）、xUnit 段（"temporarily disabled…move to the new xUnit" 陈旧）。深层必要性由后续独立审计代理处置（PRD R6/design §5 已界定）。

### 5. 门禁（复核后重跑，终态）

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build WinForward.slnx -c Release --no-restore` | exit 0，0 Warning / 0 Error |
| 测试 | `dotnet test WinForward.slnx -c Release --no-restore` | exit 0，Passed 725 / Failed 0 / Skipped 0 / Total 725 |
| 全量 verify | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **exit 0**（0 条输出，命令原始退出码） |

复核修正：仅 1 处注释（NdisApiAbi CA1419 pragma 理由措辞回调精确，见 §4），修正后三门重跑如上；`git diff --check` exit 0；diff 规模 178 文件 / +1583 / −1658（相对 base）。无其他发现，无功能行为变更。
