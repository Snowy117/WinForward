# B2c 回归比对（LOSS/REL/PERSIST 类型化 + JsonValue 退役 + parameters 强类型化，对照基线 run1）

本轮是 **E1 的最后一批**：`loss`/`reliability`/`persistent` 三个臂的 `metrics` 迁到强类型 record
（新增 `ArmKeys.Loss`/`ArmKeys.Persistent`/`ArmKeys.Reliability` 三个分片），`parameters` 强类型化为
`Contracts/ArmParameters.cs`（34 个可空成员，按 `ArmKeys.Common.Parameters` 的声明序**只写非 null 的键**），
`ControlArm` 的 loss 相位改用 `LossMetrics`（`ArmKeys.Control.LossPhase` 退役 → 组合 `ArmKeys.Loss`），
删除 `Client/JsonValue.cs` 与 `Client/DictionaryMetrics.cs`（`Dictionary<string, object?>` 清零），
`ControlArm.ReadCount`/`ReadMilliseconds` 删除，落地 D14.16 的两条字面量 gate，D17.1 把 `latency/*`
整棵子树归"测量读数"类。

判定线：**结构差异只剩改名表登记的键集变化；契约类 0 越带；身份类 0 finding；读数类只作信息性输出；
`--batch B2` 必须 9/9 satisfied 且 exit 0**。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                     # 强制重建三份产物，退出码 0
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json             # 退出码 0（完整输出落 $work/client.out）
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/b2c-run/
R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR $R/baseline/run1 /tmp/b2c-run2 --normalize $R/record-normalize.json \
        --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 \
        --json-out /tmp/b2c/findings2.json                       # 退出码 0
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 | `linux/WinForward.E2E.Contracts.dll` sha256 |
|---|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e` | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` | 无此文件 |
| `b2c`（本轮） | HEAD `42e13f9` + 本轮未提交工作树 | `f70cd93e31000e69250ecc16a8b2b1a2d149fe1ef5adf52ebc0ac5c7e93cf00a` | `c5d1e330136c4b5cd662c0a4a07269634cd29bd3ba6ba6022ff345f1192be40b` |

本轮的 selftest 产物留在 `/tmp/b2c-run2/`（§1 的 sha256 与 §2–§7 的每个观测值都取自这份最终产物；
门禁 6 的两轮修复之后重跑过 publish + selftest + 比对，收窄前的中间产物 `/tmp/b2c-run/` 已废弃），未随任务提交（与 B1b/B1c/B2a/B2b 相同：run1/run2 是基线定义，
本轮是回归检查，四条命令随时可重建）。

## 2. 三次比对的四类计数

| 比对 | 结构 | 条件键 | 身份 | 契约 | 已声明 | 改名 | 读数（信息性） | 退出码 |
|---|---|---|---|---|---|---|---|---|
| `run1 vs /tmp/b2c-run2 --band --rename-table --batch B2` | **0** | **0** | **0** | **0** | 27 | **0** | 294/366 | **0** |
| 控制 A：`run1 vs 文本改名后的 run1`（无 band，带改名表） | **0** | **0** | **0** | **0**（带宽 0） | 26 | 0 | **0/366** | **0** |
| 控制 B：`文本改名后的 run1 vs /tmp/b2c-run2`（带 band） | 1（`planSource`） | **0** | **0** | 13（全是 fail-closed） | 0 | 0 | 294/366 | 1（只有那 13 条） |

- **契约栏 0 越带**：512 个契约对全部落在逐键带宽内，一条 `exceeded` 都没有。D17.1 之后
  `latency/*` 的 8 个统计量（含 `count`）从契约类移入读数类，契约对由 B2b 的 534 降到 **512**、
  读数对由 349 升到 **366** —— 这正是 B2b §10 那三条 `LATLOAD` 零宽带宽越带的根因被移出硬判据的结果
  （`latency/tcp-rtt/count`、`metrics/tcp.outstandingAtTeardown`、`metrics/tcp.unmatchedReplies` 里
  第一条归读数；后两条本来就落在带宽内）。
- **控制 A（改名是纯改名）**：`band = 0 (not measured)` 下四类全 0、**读数 0/366 移动**，说明 9 条改名
  只换名字：值、类型、数组长度、键序、其它路径一律不动。`"tcp.sentOk"`/`"udp.sentOk"` 各命中 3 处、
  `"udpSent"` 5 处、`"udpArrived"`/`"udpForeignConnection"` 各 2 处、`"udpLossRate"` 1 处，正是 9 条改名
  在产物里的全部实例。
- **控制 B（强类型 writer 的产出形状 == 旧 writer 的产出形状）**：文本改名后的 run1 vs 真实 b2c 树（`/tmp/b2c-renamed2` vs `/tmp/b2c-run2`），
  结构栏只有 **1** 条（`run.json planSource`，A2 引入并登记），条件/身份 0，契约 13 条**全是
  "no recorded jitter band for this path"**、**无一条越带**：冻结带宽是在旧拼写下测的，9 个新拼写在它
  里面没有条目，工具 fail-closed 报出来（与 B2b §2 控制 B 同一条解释）。除此之外**没有任何其它键集、
  类型或 arity 差异**——这就是"类型化没有动记录形状"的直接证据。

## 3. 改名执行情况（`--batch B2` 9/9）

```
  table: 592 entries = added 3, identical 580, renamed 9
  path sets: base 589, after 588; only in base 9 (hit 9), only in after 8 (hit 8)
  renamed observed: landed 9, pending 0 (0 of them already publishing the target spelling), vanished 0
  added observed: 1, not observed 2
  executed batch: B2; 9 entries required, 9 satisfied, 0 not observed
  declared additions not observed: detail, error
```

9 条改名全部由 B2a/B2b 落地，本轮**没有新增改名**：本轮的 `ArmKeys.Loss`/`Persistent`/`Reliability`
三个分片的键值就是记录里原有的拼写（loss 的 28 个键在 B2b 里已由 `ArmKeys.Control.LossPhase` 声明），
所以 `only in base 9`/`only in after 8` 与 B2b 逐条相同。`added` 里未观察到的 `detail`/`error` 是
`error` 记录的两个字段，本次 selftest 全绿、没有 `error` 记录，属正常。

**对齐登记（B2a check 登记项）**：`scripts/jsonl_paths.py:6` 的字母表示例改成新拼写
（`metrics/tcp.sent` names the member `tcp.sent` of `metrics`）。

## 4. 三个臂的类型化与 `ArmKeys` 键计数

| 分片 | 常量数 | 结构 |
|---|---|---|
| `ArmKeys.Loss` | **28** | 扁平；loss 臂直接写在 `metrics/*`，control 的 loss 相位写在 `metrics/loss/*`（同一个 `LossMetrics`） |
| `ArmKeys.Persistent` | **18** | 扁平 |
| `ArmKeys.Reliability` | **55** = 21 顶层 + `OutcomeNames` 7 + `ModeNames` 4 + `Mode` 9 + `Attempt` 14 | `byMode` 是真嵌套（每个模式一块），`observed` 是真嵌套；21 顶层里 3 个是容器键 |
| `ArmKeys.Run`（新） | **29** = 19 run 级 + `Arm.Arm` 6 + `TargetObject` 4 | `run.json` 的键，含 `arms[]` 元素与 `target` 对象 |
| `ArmKeys.Common` 增补 | +4 = `ArmSummary.ResultFile` 1 + `ErrorRecord.{Error,Message,Detail}` 3 | armSummary / error 记录的自有键 |

- `ArmKeys` 全量常量数 **344**（`rg -c 'public const string' ArmKeys.*.cs` 求和）。
- `ControlArm` 的 loss 相位改用 `LossMetrics` 后，`metrics/loss/*` 的 28 条路径逐条不变（控制 B 的结构栏
  0 除外 `planSource`），且 BASE 记录里**不再存在 `sentOk` 残渣**。
- 形状契约把三个 kind 都注册进 `ContractRegistry.s_all`（现在 9 个 kind），
  `EveryMigratedKindWritesExactlyTheMetricPathsItsKeysDeclare` / `TheMetricsAreWrittenInKeyDeclarationOrder` /
  `TheKeySetAndTheRecordDeclareTheSameMembers` / `AnUnknownReadingIsNullAndNeverAMissingKey` /
  `DataDrivenContainersAreWrittenByShapeAndStayWhenEmpty` 全部覆盖它们：
  LOSS 28 键（8 个 null 用例）、PERSIST 18 键（3 个 null 用例）、REL 21 键 + `byMode` 4 块 × 17 路径
  （20 个 null 用例，含模式块里的 16 个极值）。
- `attempt` 记录（REL 文件里的 `type:"attempt"` 行）也是契约：14 个键 + `type` 进
  `ArmKeys.Reliability.Attempt`，由 `TheAttemptRecordPublishesExactlyTheDeclaredKeys` 走**生产 writer**
  （`ReliabilityArm.AttemptEvidence`）落盘后逐路径、逐序比对。实测产物里 6 条 attempt 记录的键序与声明序
  逐位相同。

## 5. `ArmParameters` 的形状与"只写非 null"

- **34 个可空属性**，全部 `init`（**不**用 `required`：一个 kind 只设它自己那几个），`WriteTo` 按
  `ArmKeys.Common.Parameters` 的**声明序**逐个判空写出，null 直接跳过；`Latency`/`Loss` 两个成员是
  递归的 `ArmParameters`，写出的是**子臂 outcome 的同一个对象**（不是键的副本），所以相位与独立臂
  不可能对"它跑的是什么"产生分歧。
- **形状不变由"谁设谁写"保证**：`ArmOutcome.Parameters` 现在是 `required ArmParameters`，
  各臂在构造 outcome 时只设自己发布的那几个成员；"发布了哪些 key"由形状契约的
  `KindContract.Parameters` 声明，测试改成**双向集合相等**（`EveryPublishedGateAndParameterIsDeclared`
  的 parameters 段），于是"多发布一个键"和"少发布一个键"都会红。
- **参数键序变化（有意，工具不判）**：`WriteTo` 按常量声明序输出，MIX/PERSIST/REL 三个臂的
  `parameters` 文档序因此改变（集合不变）：

  | 臂 | run1 顺序 | b2c 顺序 |
  |---|---|---|
  | `MIX` | seconds, desktops, pageIntervalSeconds, … , lossWindowMs | seconds, **lossWindowMs**, desktops, … |
  | `PERSIST` | seconds, intervalMs, idleSeconds, payloadBytes, expectedBytes, responseTimeoutMs | seconds, payloadBytes, expectedBytes, **intervalMs, idleSeconds**, responseTimeoutMs |
  | `REL` | seconds, connectionsPerSecond, expectedBytes, modeMix, framePayloadBytes | seconds, **framePayloadBytes**, connectionsPerSecond, modeMix, expectedBytes |

  其余 8 个臂的参数序与 run1 完全相同。四个类的判定都是**路径集合**语义（`Differences` 用 `Except`），
  所以这条变化在 §2 的三次比对里都不产生 finding；它换来的是"参数对象在任何 kind 上读起来都是同一个
  顺序"。
- **`metrics` 的文档序逐位不变**：LOSS 28、REL 103、PERSIST 18、BASE 73 条路径，把 run1 的键按 9 条改名
  做文本替换后与 b2c 的同一记录**逐路径、逐序**相同（`TheMetricsAreWrittenInKeyDeclarationOrder`
  是这条的常驻护栏）。
- **`#6` 的 W 判据**（D9/D14.2）在形状层补齐：`ThePublishedLossWindowIsTheDeclaredOne` 断言工厂的
  `metrics/window`（LOSS）与 `metrics/classes/udp/window`（MIX）就是声明值 200；落盘产物里
  `LOSS.metrics.window == parameters.lossWindowMs == 200`（BASE 的 loss 相位同）。

## 6. 字典清零与字面量 gate

- **`Dictionary<string, object?>` 在本任务三个项目内零命中**：
  `rg -n 'Dictionary<string, object\?>' benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests -g '!obj' -g '!bin'` → exit 1。
  `Client/JsonValue.cs`（75 行）与 `Client/DictionaryMetrics.cs`（36 行）删除；失败臂的 `metrics` 由
  新的 `EmptyMetrics`（写 `{}`）承担，`parameters` 是空的 `ArmParameters`。
- **允许的强类型容器清单**（D14.21：值对象内部构造嵌套块仍然用字典，但键与值类型都是具体的）：

  | 容器 | 位置 | 承载 |
  |---|---|---|
  | `Dictionary<string, double>` | `ArmOutcome.Gates`、`ClientRunner.WriteGates`、`ControlArm.Sum` | `gates` 的 name/value（`inFlightCeilingMs` 是真小数，所以值是 double；整数值的 double 序列化成同一个 JSON 文本，产物逐字节不变） |
  | `IReadOnlyDictionary<string, long>` | `DnsMetrics.Rcodes`/`QueryTypes`、`DnsArm.BuildRcodes`/`BuildQueryTypes` | rcode / queryType 分布 |
  | `Dictionary<string, ReliabilityModeMetrics>` / `IReadOnlyDictionary<string, ReliabilityModeMetrics>` | `ReliabilityArm.BuildModeBreakdown`、`ReliabilityMetrics.ByMode` | `byMode` 的模式块 |
  | `Dictionary<string, ArmKind>`、`Dictionary<string, string>` | `ArmKind.BuildIndex`、`PlanFile` 的文件名映射 | 不是 JSON 载体（查表） |

- `ControlArm.ReadCount`/`ReadMilliseconds` 删除：gates 现在是 `Dictionary<string, double>`，
  相位求和直接 `Sum(latency.Gates, loss.Gates, key)`（缺键即 0，`GetValueOrDefault`），
  **不再有"从字典读回值再猜类型"的辅助函数**（AC2 点名项）。
- **字面量 gate ①（`rg`）**：

  ```bash
  rg -n 'writer\.Write[A-Za-z]*\(\s*"' \
     benchmarks/WinForward.E2E/Client/Arms benchmarks/WinForward.E2E/Client/ClientRunner.cs
  # 零命中（exit 1）
  ```

  为了让这条 `rg` 覆盖 `ClientRunner.cs` 的**全部**四个 writer，`run.json` 的 29 个键也进了新的
  `ArmKeys.Run` 分片（含 `arms[]` 元素与 `target` 对象），`armSummary` 的 `resultFile` 与
  `error` 记录的 `error`/`message`/`detail` 进了 `ArmKeys.Common`。`Client/ResourceSampler.cs`
  与 `Target/**` 的键按 D14.16/D12 显式归 E2，**不在本 gate 的扫描面内**。
- **字面量 gate ②（xunit 源扫描）**：`tests/WinForward.E2E.Tests/JsonKeyLiteralGateTests.cs`，
  用 `[CallerFilePath]` 定位仓库根，扫描 `Client/Arms/**/*.cs` 与 `Client/ClientRunner.cs`，
  正则抓**键位**上的字符串字面量（`Write*(` 的第一个实参、字典下标的字面量键），断言它们与
  `ArmKeys` 的 344 个常量值不相交。**为什么是"键位"而不是"整个文件里出现过"**：同一个拼写在这些
  文件里也合法地是 JSON **值**（REL 的 `observed: "clean"` 与 `byMode` 的成员名 `clean` 同形），
  值不是键。扫描前会断言"扫到了文件"且"已知键集非空"，避免 gate 空转。
- **负控（两条都实测红过，改回后 `cmp` 逐字节核过再跑绿）**：

  | 负控 | 改动 | 观测 |
  |---|---|---|
  | gate ① | `ClientRunner` 里把 `writer.WriteNumber(ArmKeys.Common.Record.StartedTicks, startedTicks)` 临时写成 `writer.WriteNumber("startedTicks", startedTicks)` | 上面的 `rg` 命中 `ClientRunner.cs:540`，exit 0（红） |
  | gate ② | 同一处 + `LossArm` 里把 `[ArmKeys.Common.Gates.ClientSendLoss]` 临时写成 `["clientSendLoss"]` | `JsonKeyLiteralGateTests` 报两条：`LossArm.cs:48: the key 'clientSendLoss' is written as a literal`、`ClientRunner.cs:540: the key 'startedTicks' is written as a literal`，`Failed: 1` |

## 7. 结构证据（比对范围之外的独立核对）

- **记录数逐一对齐**：`BASE 12`、`DNS 10`、`DNSALT 10`、`IDLE 7`、`LAT 10`、`LATLOAD 10`、`LOSS 12`、
  `MIX 12`、`PERSIST 12`、`REL 18`、`THRU 10`、`ledger 258` 与 run1 完全相同。
- **落盘记录的形状**（`/tmp/b2c-run/out/`）：

  ```
  LOSS.metrics      = 28 键（sent, supplied, arrived, … , outOfRangeSequences, achievedRate）
                      identity: arrived+late+never+abandonedAtTeardown+corruptDatagrams == sent（2001 == 2001）
                      window == parameters.lossWindowMs == 200
  REL.metrics       = 21 顶层键；byMode = [clean, resetAfterN, partialFin, halfClose]
                      每个模式块 = 9 键（attempts, observed, truncated, echoedBytes, trailerBytes, min/maxEchoedBytes, min/maxTrailerBytes）
                      attempt 记录 = 15 键（type + 14），键序与 ArmKeys.Reliability.Attempt 声明序逐位相同
  PERSIST.metrics   = 18 键
  BASE.metrics      = [elapsedSeconds, latency, loss]；loss 相位 28 键、无 sentOk 残渣；
                      parameters = [seconds, phaseSeconds, phases, latency, loss]，
                      parameters.latency/loss 是两个真对象（相位参数各 6 / 4 键）
  ```
- **gates 的字节形状不变**：`gates` 的值从 `object?`（long/double 混装）改成 `double` 后，
  `LOSS.gates = {"clientSendLoss":0,"windowMs":200}`、`LAT.gates.inFlightCeilingMs = 203791.233`（本轮实测，非整数）
  与 run1 的同一键逐字节同形（整数值的 double 序列化不带小数点，如 `windowMs: 200`）。

## 8. 六条门禁

| # | 命令 | 观测 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 退出 0；三份产物重建（§1 的 sha256） |
| 2 | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 173`（B2b 169 + 4：模式名绑定、W 判据、attempt 记录、字面量 gate） |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出 0（本轮 b2c 产物的来源） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0，**输出 0 字节** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | 见 §8.1 |

补记（首轮 → 归零，全部按"改代码"而不是"放宽规则"处理）：

- 门禁 2 首轮报 **13 条 `S3218`**（新增分片里与外层同名、按 D14.17"同一叶子名在不同层级各写一个
  常量"必然产生的遮蔽），按仓库既有做法**逐个成员**加 `#pragma warning disable S3218` +
  `// ReSharper disable once MemberHidesStaticFromOuterClass`，并在每个成员上留一行 `<remarks>`
  指出它遮蔽了谁、分片的 `<remarks>` 里写明"遮蔽是声明规则不是意外"。**没有**用整类级别的抑制。
  `ArmKeys.Control.Loss`、`ArmKeys.Common.Parameters.Loss` 两处与 `ArmKeys.Loss` 同名的容器常量
  各带一对同款抑制（`ArmKeys.Control.Latency` 已有先例）。
- 门禁 5 首轮报 **3 条**：`IDE1006` 1 条（RELI 模式工厂里的局部常量 `Attempts` → `attempts`）、
  `SYSLIB1045` 2 条（字面量 gate 的两个 `new Regex(...)` → `[GeneratedRegex]` 源生成，类改
  `partial`）。改完复跑输出 0 字节，且门禁 2/3 全部重跑。
- 门禁 6 首轮报 **27 条**，逐条按"改代码"处理，详见 §8.1；改完复跑 0 条 `<Issue>`，
  并在**同一份最终源码**上重跑了门禁 1–5 与三份比对（§1–§7 的全部观测值取自这次重跑）。

### 8.1 第 6 条门禁的观测

```
$ python3 -c "import xml.etree.ElementTree as ET; print(len(ET.parse('/tmp/b2c/jb-inspectcode2.xml').getroot().findall('.//Issue')))"
0
```

首轮报 **27 条**，全部按"改代码"处理（没有加 `ReSharper disable`）：

| 类别 | 条数 | 处理 |
|---|---|---|
| `InvalidXmlDocComment`（cref 解析不到） | 20 | 文件级 `<remarks>` 的宿主是**外层** `ArmKeys` 类，所以 `ArmKeys.Reliability`/`ArmKeys.Run` 的 `<see cref>` 要写成 `Reliability.Outcomes`/`Run.Arm` 这类限定名（与 B2b §8 同一处理，无抑制） |
| `MoveLocalFunctionAfterJumpStatement` | 1 | `ControlArm` 的局部函数 `Sum` 移到 `return` 之后 |
| `RedundantCast` | 1 | `MixArm` 的 `(double)windowMs` —— gates 改成 `double` 之后这个转换确实多余，删掉（值是同一个） |
| `MemberCanBePrivate.Global` | 3 | `LossShape.WindowMs`、`PersistentShape.s_parameterNames`、`ReliabilityShape.s_parameterNames` 收窄为 `private` |
| `UnusedMember.Global` | 1 | `RecordContract.Parameters` 在"emitted == declared"改写后没有读者，删除 |
| `UseRawString` | 2 | 两条 `[GeneratedRegex]` 的正则改用 raw string（结尾的引号写成 `["]` 字符类，避免与定界符冲突） |
| `InvalidXmlDocComment`（`TcpCommand.Name` 重载歧义） | 1 | 写成 `<see cref="TcpCommand.Name(TcpMode)"/>` |

第二份报告的 `<Issues />` 为空元素，`Report` 下 **0 条 `<Issue>`**；修复后门禁 1–5 全部重跑，
§1–§7 的每个观测值都来自重跑后的最终产物（`/tmp/b2c-run2`、sha256 见 §1）。

## 9. E1 收尾判据（AC1–AC4）

**AC1（每个 `.cs` 有效行 ≤ 400）** —— 有效行 = 非空且不以 `//` 开头的行：

- `WinForward.E2E.Contracts/**`：**全部 ≤ 400**（最大 `Json/JsonlSink.cs` 202、`Metrics/MixMetrics.cs` 195、
  新增的 `ArmParameters.cs` 130 / `Metrics/ReliabilityMetrics.cs` 124 / `ArmKeys.Reliability.cs` 94）。
- `tests/WinForward.E2E.Tests/**`：**全部 ≤ 400**（最大 `ContractShapeTests.cs` 371、`Shapes/MixShape.cs` 249、
  新增 `Shapes/ReliabilityShape.cs` 232、`JsonKeyLiteralGateTests.cs` 108（check 轮把逐行扫描改成整文件扫描并收窄成员表后）。
- `benchmarks/WinForward.E2E/**`：**7 个文件超 400，全是本轮之前就超的存量**，本轮只报告、不拆
  （拆分归 E2，D15 已登记）：

  | 文件 | HEAD 有效行 | 本轮 | Δ |
  |---|---|---|---|
  | `Client/Arms/LatencyArm.cs` | 816 | 816 | 0 |
  | `Client/Arms/MixArm.cs` | 687 | 687 | 0 |
  | `Client/Arms/ReliabilityArm.cs` | 606 | 612 | +6 |
  | `Client/ClientRunner.cs` | 562 | 567 | +5 |
  | `Client/Arms/DnsArm.cs` | 509 | 509 | 0 |
  | `Client/ResourceSampler.cs` | 469 | 469 | 0 |
  | `Client/Arms/PersistentArm.cs` | 446 | 452 | +6 |

  增量的来源是把 `DictionaryMetrics` 的逐个下标赋值换成强类型对象的成员赋值（每个成员一行）与
  `ArmParameters` 的构造；`ControlArm.cs` 反而从 104 降到 96（删掉两个读字典的辅助函数）。
  命令：`python3 -c "..." # 见 §1 同目录脚本，逐文件计"`（本报告 §9 的表格由
  `sum(1 for l in open(p) if l.strip() and not l.strip().startswith('//'))` 生成）。

**AC2（`Dictionary<string, object?>` 清零 + 从字典读回值做算术的辅助函数消失）**：

```bash
rg -n 'Dictionary<string, object\?>' benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts \
   tests/WinForward.E2E.Tests -g '!obj' -g '!bin'          # exit 1（零命中）
rg -n '\bReadCount\(|\bReadMilliseconds\(' benchmarks/WinForward.E2E   # exit 1（辅助函数已删；\b 是为了不误命中 ResourceSampler 的 TryReadCounters）
```

**AC3（同一统计量只有一种拼写）**：`--batch B2` 9/9 satisfied、0 not observed（§3）；
改名的旧拼写在三个项目内的**全部**残留（check 轮核对）：`contract-inventory.py` 的改名表本身（必须写旧拼写）、
`LatencyArm` 的内部字段名 `_sentOk`（非 JSON 键）、`README.md` 的 8 行契约表（`357/361/362/364/382/384/387-389`、`430`，归 E5）、
`MixShape.cs:276-280` 的两个测试局部形参名（非 JSON 键）与
`LatencyArm` 的内部字段名 `_sentOk`（不是 JSON 键）。README 契约表与代码的逐项对应归 **E5**（已登记）。

**AC4（记录模型定义在 `Contracts`）**：`ArmKeys` 的 11 个分片 + `ArmParameters` + 9 个 metrics record
全部在 `WinForward.E2E.Contracts`（`public`，D14.20/D16.5）；harness 引用它（`ProjectReference` +
`InternalsVisibleTo`），写出侧只引用常量（字面量 gate 就是这条的机械判据）；分析器侧的引用者是 **E4**
（C# 分析器尚未存在，冻结的 Python 参考仍按改名表登记的口径读同一批拼写）。

## 10. 偏离与登记

| 项 | 说明 | 归属 |
|---|---|---|
| `gates` 的值类型是 `double` 而不是 D5 第 5 条字面写的 `Dictionary<string, long>` | `gates/inFlightCeilingMs` 是真小数（如 `203821.656`），`long` 会改变契约值；`double` 的整数值序列化成同一个 JSON 文本，产物逐字节不变（§7）。键仍由 `ArmKeys.Common.Gates` 约束、容器仍是强类型 | 本轮（已在本报告与 `ArmOutcome.Gates` 的注释里说明） |
| `parameters` 的文档序在 MIX/PERSIST/REL 上改变 | 强类型化要求"按声明序只写非 null"（D14.9 的键序稳定性延伸），集合不变、工具不判（§5） | 本轮（有意） |
| `byMode` 在形状契约里按"计划排满 4 个模式"建模 | 真实运行的模式成员集跟随 plan 的 `modeMix`；契约钉的是**每块 schema + 4 个模式名与 `TcpCommand.Name` 一致**（`TheDeclaredReliabilityModeNamesAreTheScheduledOnes`） | 本轮（建模取舍，已在 `ArmKeys.Reliability` 的 `<remarks>` 与 `ReliabilityShape` 里写明） |
| 7 个 harness 文件 > 400 有效行 | 存量；本轮只 +5/+6 增量（AC1 表） | **E2** |
| README 契约表逐项复核（含仍写旧拼写的 LAT/MIX 行）与 `:430` 的 `udpSent` | B2a/B2b 已登记 | **E5** |
| 冻结 Python 参考与 `make_tree.py` 的旧拼写 | D6.4 | **E4** |
| `attempt` 记录（REL 内嵌）没有进 c 盘比对的范围 | 它已在 `ArmKeys.Reliability.Attempt` 与单测里钉死；比对工具按文件级别看路径集合，`REL.jsonl` 的路径集合已含 15 个 attempt 键 | — |

## 11. check 轮（独立复核）的修复与新增登记

**修复（只动测试）**：`JsonKeyLiteralGateTests.cs` 的有效行由 92 → **108**。三处真缺陷：
① 逐行扫描 → 跨行调用（`writer.WriteNumber(\n "startedTicks", …)`）可同时绕过 `rg` 与 xunit 两条 gate → 改为整文件扫描并把行号定位到字面量；
② 正则过宽 → `Console.WriteLine("message")` 被误判为"写了键 message" → 收窄为 `Utf8JsonWriter` 取属性名的成员表
（真实树上旧模式只多命中两条 Console 消息，零覆盖损失）；
③ 防空转断言 `files.Count > 0` 因无条件追加 `ClientRunner.cs` 恒真 → 两个来源分开断言（空 Arms 目录/空键集都会红）。
负控与还原哈希见 check 报告；本报告的 §6 表格与 §9 行数已按此同步。

**新增登记（工具口径）**：D17.1 把 `latency/*` 归读数后，把 LAT 的 `tcp-rtt/*` 全部 ×2，工具 **exit 0**、
默认汇总的读数计数甚至不变，只有 `--strict` 里能看到（均值 965→1880，带宽 39.3）。
最小改进（**E2/E5**）：默认 summary 增加一行"越带读数计数"，并在流程上要求"声称行为等价"的批次附 `--strict`
读数摘要、逐条解释越带项（见 DD D17.4）。
