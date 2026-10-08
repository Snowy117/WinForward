# E2-b1 拆分回归比对（有效行扫描器 + 四个文件拆分；对照基线 run1）

本批是 E2-b 的第一小批（2b-1）：新建 `scripts/effective-lines.py`；把四个超标文件沿自然接缝拆开
（物理搬移 + 可见性调整，逻辑不动）；补 `LatencyArm` 的 `outstandingAtTeardown` 求和覆盖；
清理 `UdpLaneTransport.ConnectOk/ConnectTicks`。**`src/` 一行未改，本轮未提交。**

判定线：**结构差异为空**、**契约栏零越带**、**D18.5 #12 的四个零宽键在拆分前后逐值相同**、
**纯搬移的机械证据（token 多重集差异可完全归因，字符串字面量逐字相同）**。

---

## 0. 结论一句话

四个超标文件（`ClientRunner` 567、`ResourceSampler` 460、`LatencyArm` 471、`DnsArm` 504）拆成 16 个文件、
每个 ≤ 201 有效行，拆分前后每个分组的 token 差异**只剩**「`private` → `internal` 加宽」+「跨文件调用的
类型限定」+「新文件里新类型自己的声明」，四组的**字符串字面量多重集逐字相同**（63/50/29/13 条）；
D18.5 #12 的 34 个 (记录, 路径) 对在拆分前后**逐值相同**；`LatencyArm` 的 `outstandingAtTeardown`
求和现在有事实覆盖（删掉 `DeferredPending` 项 → 新事实红，246 条里只此一条）；三项目里剩余三个超标文件
（`MixArm` 720、`ReliabilityArm` 612、`PersistentArm` 449）按计划归 E2-b2。

---

## 1. 有效行扫描器与拆分清单

### 1.1 规则（`benchmarks/WinForward.E2E/scripts/effective-lines.py`）

有效行 = **非空且非注释**的行。注释按编译器的方式识别而不是按前缀：`//` 与 `/* */` 在字符串字面量
内部不算注释，字符串（含 `"""` 原始字符串）整段保留，因此多行原始字符串占用的行**算**有效行（那几行
是一个语句的一部分），而只含注释或空白字符的行不算。`bin/`、`obj/` 下的生成副本不扫描；参数可以是
目录（递归 `.cs`）或单个文件；默认上限 400，超过则打印 `<有效行数>  <路径>` 并以退出码 1 结束，
`--all` 打印全部文件、`--limit N` 改上限。

**口径自检**：脚本对拆分前的树给出的七个数字与计划书/check 报告里的实测值**逐个相同**
（`LatencyArm` 471、`MixArm` 720、`ReliabilityArm` 612、`DnsArm` 504、`ResourceSampler` 460、
`PersistentArm` 449、`ClientRunner` 567），即扫描器与之前的记账口径一致。

### 1.2 拆分前的超标清单（判据的起点）

```bash
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests
```

```
  720  benchmarks/WinForward.E2E/Client/Arms/MixArm.cs
  612  benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs
  567  benchmarks/WinForward.E2E/Client/ClientRunner.cs
  504  benchmarks/WinForward.E2E/Client/Arms/DnsArm.cs
  471  benchmarks/WinForward.E2E/Client/Arms/LatencyArm.cs
  460  benchmarks/WinForward.E2E/Client/ResourceSampler.cs
  449  benchmarks/WinForward.E2E/Client/Arms/PersistentArm.cs
exit=1
```

### 1.3 拆分后的清单与去向

同一条命令，拆分后只剩三个（都属 E2-b2，不在本轮范围）：

```
  720  benchmarks/WinForward.E2E/Client/Arms/MixArm.cs
  612  benchmarks/WinForward.E2E/Client/Arms/ReliabilityArm.cs
  449  benchmarks/WinForward.E2E/Client/Arms/PersistentArm.cs
exit=1
```

| 旧文件 | 有效行 | 新文件 | 有效行 | 内容 |
|---|---|---|---|---|
| `Client/ClientRunner.cs` | 567 | `Client/ClientRunner.cs` | **201** | 臂循环、臂的失败边界、输出路径校验、`--help` |
| | | `Cli/ClientOptions.cs` | **168** | `ClientOptions` + 参数解析（`TryCreate`/`TryApply`/…） |
| | | `Client/ArmRecordWriter.cs` | **132** | `error` / `result` / `armSummary` 三个记录 |
| | | `Client/RunFileWriter.cs` | **86** | `run.json`（环境 + 每臂摘要），`ArmSummary` |
| `Client/ResourceSampler.cs` | 460 | `Client/ResourceSampler.cs` | **174** | 1 Hz 采样循环、目标屏障、两个采样动作 |
| | | `Client/ProcessCounterSource.cs` | **180** | OS 计数器读取（`Try*` + 按名求和） |
| | | `Client/ProcessSample.cs` | **29** | 每进程身份与按名聚合 |
| | | `Client/ResourceSampleWriter.cs` | **90** | `sample` / `samplerError` 的 JSON 形状与 stderr 上报 |
| `Client/Arms/LatencyArm.cs` | 471 | `Client/Arms/LatencyArm.cs` | **180** | `RunAsync` 编排、`StartLanes`、三条 lane 体与探针、`Lane`/`LaneStates` |
| | | `Client/Arms/LatencyPlan.cs` | **146** | `LatencyPlan` / `LatencyTotals` / `MeasurementValidity` |
| | | `Client/Arms/LatencyMetricsWriter.cs` | **153** | `TcpMetrics`/`UdpMetrics`/`WriteGates`/`WriteNotes` + ceiling |
| `Client/Arms/DnsArm.cs` | 504 | `Client/Arms/DnsArm.cs` | **137** | `RunAsync`、`MetricsOf`、`BuildRcodes`/`BuildQueryTypes`、`Classify` |
| | | `Client/Arms/DnsCounters.cs` | **16** | 一个 lane 的计数器 |
| | | `Client/Arms/DnsQueryBuilder.cs` | **30** | `BuildQuery` / `QueryTypeFor` / `TypeName` |
| | | `Client/Arms/DnsUdpPhase.cs` | **144** | udp 发送阶段 + 接收循环 + drain |
| | | `Client/Arms/DnsTcpPhase.cs` | **196** | tcp 发送阶段 + 接收循环 + `PendingQuery` |

16 个文件全部 ≤ 400（最大的 `ClientRunner` 201）；同时新加/改动的文件也在限内：
`tests/…/Lanes/LatencyArmTotalsTests.cs` 38、`tests/…/JsonKeyLiteralGateTests.cs` 114、
`Client/Lanes/UdpLaneTransport.cs` 62（清理后从 73 降到 62）。

**没有建 pass-through 别名层**：旧方法一律搬到新类型上、只加调用点限定，没有留一行转调的旧入口；
`ClientRunner.TryCreate` 的 5 处测试调用与 `Program.cs` 的 1 处调用改成 `ClientOptions.TryCreate`，
`ClientRunner.WriteResultAsync` 的 1 处测试调用改成 `ArmRecordWriter.WriteResultAsync`。
`ClientOptions` 现在住 `WinForward.E2E.Cli`（目录=命名空间后缀），`ArmContext`/`LossArm`/两个新
writer 补 `using WinForward.E2E.Cli;`。

**字面量 gate 随之加宽而不是失效**：`JsonKeyLiteralGateTests` 原来扫描 `Client/Arms/**` 与
`Client/ClientRunner.cs`；本轮把 `Client/ArmRecordWriter.cs`、`Client/RunFileWriter.cs` 加进
`s_writerFiles`（逐名 `File.Exists` 自检，缺一个就红），所以搬到新文件里的 `ArmKeys` 写入点仍在 gate 内。

---

## 2. 纯搬移的机械证据

### 2.1 方法

`move-check.py`（临时脚本，逻辑自包含、可重建）把每个分组的**文件并集**当作一份源码：先按 §1.1 的
规则去掉注释、忽略 `using`/`namespace` 这类文件脚手架行，再分词（字符串字面量整段算一个 token，
但**插值洞 `{...}` 里的表达式按代码分词**，所以洞里的限定改变看得见）。对照的两侧分别是
`git show HEAD:<旧文件>` 与当前工作树。判据是三条**可完全归因**的等式：

| 允许的差异 | 等式 |
|---|---|
| 可见性加宽 | `added['internal'] = removed['private'] + 收窄数 + 新类型数` |
| 跨文件限定 | `added['.'] = Σ added[类型名] − 新类型数` |
| 新类型自己的声明 | `added['class'|'static'|'{'|'}'] = 新类型数` |
| 其余 | 不得有别的 token 差异（白名单为空则失败） |

（「收窄数」只在一处非零：`ClientRunner` 组的 5 个 setter，见 §2.3b。）

多重集对**顺序**与**类型归属**都不敏感：成员在文件内换序、常量换个类型声明，token 计数完全不变。
§2.4 记录了 check 轮用有序检查补出来的第四类差异。

外加一条独立的**字符串字面量多重集相同**判定（插值洞清空后比较，洞里的表达式由上面的 token 判定覆盖）：
记录里发布的每一条文本（14 条 latency note、6 条 dns note、所有 `ArmKeys` 用法、`run.json` 的环境文本）
逐字未动。

### 2.2 结果（`MECHANICAL MOVE`，退出码 0）

| 分组 | token 前→后 | `private`→`internal`（净） | 跨文件限定 | 新类型 | 字面量 |
|---|---|---|---|---|---|
| `ClientRunner` | 3921 → 3956 | 2（7 处加宽 − 5 个 setter 收窄） | 9 处（`ArmRecordWriter` 8 次调用 + `RunFileWriter` 1 次调用） | 2（`ArmRecordWriter`、`RunFileWriter`） | 63 条逐字相同 |
| `ResourceSampler` | 2354 → 2388 | 7 | 11 处（`ProcessCounterSource` 3 + `ResourceSampleWriter` 8） | 2（`ProcessCounterSource`、`ResourceSampleWriter`） | 50 条逐字相同 |
| `LatencyArm` | 3610 → 3636 | 13 | 10 处（`LatencyMetricsWriter` 6 + `LatencyPlan` 4） | 1（`LatencyMetricsWriter`） | 29 条逐字相同 |
| `DnsArm` | 2958 → 2998 | 7 | 11 处（`DnsArm` 4 + `DnsQueryBuilder` 5 + `DnsUdpPhase` 1 + `DnsTcpPhase` 1） | 3（`DnsQueryBuilder`、`DnsUdpPhase`、`DnsTcpPhase`） | 13 条逐字相同 |

> 「跨文件限定」一列的括号里是**调用点**（新类型声明自身的名字不算），脚本自己算总数
> （`Σ added[类型名] − 新类型数`）并与 `added['.']` 比对，不靠人工记账。

**可见性加宽的完整清单**（`private` → `internal`，逐组，脚本枚举）：

- `ClientRunner`（7）：`ArmSummary`、`LostRecords`、`TryWriteFailureAsync`、`WriteFailureAsync`、
  `EmptyOutcome`、`WriteArmSummaryAsync`、`WriteRunFileAsync`。
- `ResourceSampler`（7，最终值；`TryRefresh`/`TryReadLong` 一度提到 `internal`，按 §2.3b 收回 `private`）：
  `TryReadStartTime`、`TryReadCounters`、`ReadProcessTotals`、`ReportToStderr`、`WriteCounters`、
  `WriteProcesses`、`ReportSamplingFailureAsync`。
- `LatencyArm`（13）：类型 `LatencyPlan`、`LatencyTotals`、`MeasurementValidity`、`Lane<TBook>`、`LaneStates`；
  常量 `BacklogSeconds`、`MaxBacklogPerLane`、`DrainSeconds`；方法 `WriteGates`、`WriteNotes`、
  `InFlightCeilingMs`、`TcpMetrics`、`UdpMetrics`。
- `DnsArm`（7）：常量 `DrainWindowMilliseconds`；方法 `Classify`、`BuildQuery`、`QueryTypeFor`、
  `TypeName`、`RunUdpAsync`、`RunTcpAsync`。

这些是**可见性调整**，不是逻辑改动：每一处的函数体、语句顺序、常量取值、方法签名逐字未动，
只是从 `private` 变成同一程序集内可见（`InternalsVisibleTo` 之外没有被打开的面）。

### 2.3 有意超出「纯搬移」的两处（单列，各自有 gate 依据）

**(a) `UdpLaneTransport` 的 connect 观测面删除**（E2-a3 check 必做项 2）。删掉 `ConnectOk`/`ConnectTicks`
（无生产消费者）与 `OpenAsync` 里的计时，`OpenAsync` 直接回传 `SocketOps.TryConnectAsync` 的结果；
类文档写明理由：「udp connect 无握手可计时、记录只在 `tcp.*` 发布 connect 事实，失败以
`scheduleTruncated`/`laneShortfall` 显形」。事实强度不变：连不上仍必须「回答结果而不是抛」，
`LaneTransportTests` 的两条断言改成断 `LaneOpenResult`（`open.Ok`/`open.Error`）。
判据：`rg -c 'ConnectOk|ConnectTicks' Client/Lanes/UdpLaneTransport.cs` 无命中（退出 1）；
全仓剩下的命中只有 `TcpLaneTransport`（真消费者 `LatencyArm`）、`LatencyTcpPolicy`/`LatencyPlan`/
`LatencyMetricsWriter` 的同名 state 字段、`ReliabilityArm` 自己的 attempt 字段与它的 `ArmKeys` 常量。

**(b) `ClientOptions` 的五个 setter 收窄为 `private set`**（本轮 `jb inspectcode` 的 7 条
`MemberCanBePrivate.Global` 中的 5 条）。参数解析搬进 `ClientOptions` 之后，`TcpPort`/`UdpPort`/
`DnsPort`/`InjectCorruptEvery`/`InjectRewriteEvery` 的 setter 只被本类型的 `TryApply` 写（`rg` 复验：
`TargetRunner.cs` 与 `PlanFile.cs` 里同名的赋值对象是 `TargetOptions`/`ArmSpec`，不是 `ClientOptions`），
所以 `private set` 是当前真实的最小面；读侧（`RunFileWriter`、`LossArm`）不受影响。
同理，`ProcessCounterSource.TryRefresh`/`TryReadLong` 从本轮误加的 `internal` 收回 `private`
（它们只被同文件的 `TryReadCounters` 调用）——这两条收窄也让「加宽清单」从 9 降到 7（§2.2 已按最终值列出）。

### 2.4 check 轮的补正：token 多重集看不到的第四类差异（行为无关，但必须登记）

§2.2 的等式只约束**计数**。check 轮用一条有序检查（逐文件把拆分后的每一行按去掉可见性、去掉搬移强加的
`Type.` 限定、去掉新类型自身 `class` 外壳后的样子，在拆分前的行序列里做单调匹配，判据是「拆分后的行都能在
拆分前逐字找到，且文件内相对顺序不倒退、没有一行被用超」）补出第四类差异：

1. **7 个常量随 `LatencyPlan` 从嵌套类型提升为顶层类型而改归属**（拆分前它们是 `LatencyArm` 的成员，
   `LatencyPlan`/`LatencyTotals`/`MeasurementValidity` 是 `LatencyArm` 的嵌套类型，所以能不带限定读到它们）：
   `DefaultRatePerSecond`/`DefaultPayloadBytes`/`DefaultWindow`/`DefaultLanes`（在 `LatencyPlan.cs:11-14`
   仍是 `private`，因为只有 `LatencyPlan` 的构造器读它们；`LatencyArm.cs` 已不再声明）
   + `BacklogSeconds`/`MaxBacklogPerLane`/`DrainSeconds`（落在 `LatencyPlan.cs`，按 §2.2 的清单取 `internal`，
   被 `LatencyArm.cs:102` 的 `LatencyPlan.DrainSeconds` 与 `LatencyMetricsWriter` 的 note 读）。
2. **成员在文件内换序**：`ArmRecordWriter.cs`（`EmptyOutcome` 提到 `LostRecords` 之前）、
   `RunFileWriter.cs`（`ArmSummary` 声明提到 `WriteRunFileAsync` 之前）、
   `LatencyMetricsWriter.cs`（`WriteGates`/`WriteNotes` 提到 `TcpMetrics`/`UdpMetrics` 之前，`RateText` 移到其后）。

判定：**不影响行为**，无需改代码。依据三条——(a) 方法在类内的顺序、`const` 的归属、`record` 声明的位置都不改
IL 语义；(b) 这 4 个文件里没有带初始化器的字段，本批其余文件的初始化器（`Lane<TBook>.Book`、
`LaneStates.Tcp/Probe/Udp`、`ProcessTotals.Processes`、`ClientOptions.SamplerProcesses`/`s_knownOptions`/
`s_stringOptions`、`ResourceSampler._shutdown`/`_gate`/`s_interval`、`DnsCounters._rcodes`/`_queryTypes`、
`ClientRunner.s_pathSeparators`）只读字面量或 BCL 类型，没有一个读同类型的另一个成员，所以也不存在初始化
顺序依赖；(c) 全 16 个文件在有序检查下 **0 行在拆分前不存在**（也不存在被用超的行），即内容逐字搬移、无新增行；
配合 §6 的判定比对（E1 与 N1 的 13 条逐行相同）与 246 条测试，行为等价的结论不变。

因此 §2.2 的「只差三类」应读作「**在 token 多重集上**只差三类；加上本节这一类，搬运账才闭合」。
常量无重复声明：这 7 个常量在 `LatencyArm` 组的文件里各只有一处定义（都在 `LatencyPlan.cs`），全仓也没有
`LatencyArm.Default*`/`LatencyArm.Backlog*`/`LatencyArm.Drain*` 的残留引用（`rg` 无命中）；同名的
`LossArm`/`DnsArm`/`PersistentArm`/`UdpReliability` 常量是各自类型的默认值，不是同一概念的副本。

---

## 3. D18.5 #12 的四个零宽键：拆分前后逐值相同

脚本按 D18.5 #12 的四个模式（`metrics/*.received$`、`metrics/*unmatchedReplies$`、
`metrics/*outstandingAtTeardown$`、`latency/*-rtt/count$`）取每次 selftest 的**全部 result 记录**，
列出 (记录, 路径) 对与值。比对覆盖**四次运行**：冻结基线 `run1`、拆分前的保存二进制一次、
拆分后的最终二进制两次。下表「拆分前 / 拆分后」两列在四次之间 `diff` 全空：

| 记录 | 路径 | 拆分前 | 拆分后 |
|---|---|---|---|
| `LAT` | `metrics/tcp.received` / `tcp.unmatchedReplies` / `tcp.outstandingAtTeardown` | 322 / 0 / 0 | 322 / 0 / 0 |
| `LAT` | `metrics/udp.received` / `udp.unmatchedReplies` / `udp.outstandingAtTeardown` | 161 / 0 / 0 | 161 / 0 / 0 |
| `LAT` | `latency/tcp-rtt/count`、`latency/udp-rtt/count` | 322、161 | 322、161 |
| `LATLOAD` | `metrics/{tcp,udp}.{received,unmatchedReplies,outstandingAtTeardown}`、两条 `-rtt/count` | 1601 / 0 / 0、1601 / 0 / 0、1601、1601 | 同左 |
| `BASE` | `metrics/latency/{tcp,udp}.*`、`metrics/loss/unmatchedReplies`、两条 `-rtt/count` | 101 / 0 / 0、101 / 0 / 0、0、101、101 | 同左 |
| `DNS` / `DNSALT` | `latency/dns-rtt/count` | 402 / 402 | 402 / 402 |
| `LOSS` / `PERSIST` | `metrics/unmatchedReplies` | 0 / 0 | 0 / 0 |
| `MIX` | `metrics/classes/udp/unmatchedReplies`、`latency/{tcp,udp,dns}-rtt/count` | 0、146、602、8 | 同左 |

**34 个 (记录, 路径) 对 / 18 条路径，`diff` 为空**（逐值相同，不是「同量级」）。本条同时是
必做项 3 的判据。

---

## 4. 必做项三条的落地

| # | 项 | 落地 | 判据 |
|---|---|---|---|
| 1 | `outstandingAtTeardown` 求和覆盖 | 新增 `tests/WinForward.E2E.Tests/Lanes/LatencyArmTotalsTests.cs`（38 有效行）：真 `UdpLatencyPolicy` 写在臂自己的 `Lane<UdpLatencyState>.Book` 上，引擎对着「永不回应」的 fake transport 跑一条 lane（先 `LaneTestOptions.WarmAsync()` 付掉 JIT，窗口 0.2 s @2000/s、backlog 4），然后按 `TotalUdp` 的两步（`AddUdp` + `AddPending`）喂 `LatencyTotals`，断言 `Outstanding == Pending + DeferredPending`；两个半边各自 `Assert.True(... > 0)`，所以求和不是恒真式 | 突变 M3′：删掉 `LatencyPlan.LatencyTotals.AddCounts` 里的 `Outstanding += counts.DeferredPending;` → **只有这条新事实红**：`Assert.Equal() Failure: Values differ / Expected: 6 / Actual: 2`，`Failed: 1, Passed: 245, Total: 246`（E2-a3 check 的 M3 在这一行上是 245/245 全绿）；还原后 246/246 全绿 |
| 2 | `UdpLaneTransport.ConnectOk/ConnectTicks` | 删除（无生产消费者），类文档写明理由（§2.3a）；`LaneTransportTests` 两条断言改断 `LaneOpenResult` | `rg -c 'ConnectOk|ConnectTicks' Client/Lanes/UdpLaneTransport.cs` 无命中（退出 1）；`LaneTransportTests` 7 条（原名 `…AndReportsItsConnect` 改成 `TheUdpTransportSendsAndReceivesDatagrams`）全绿 |
| 3 | 拆分前后四个零宽键逐值相同 | 见 §3 | 34 个 (记录, 路径) 对在 `run1`、拆分前、拆分后两次共四次运行之间 `diff` 全空 |

---

## 5. 门禁（冻结树，六条逐条）

| # | 门禁 | 命令 | 结果 |
|---|---|---|---|
| 1 | publish | `cd benchmarks/WinForward.E2E && scripts/publish.sh` | 退出 0；`linux`/`win`/`win-direct` 三份产物齐全（两个 Windows 客户端按镜像名可分）；`linux/WinForward.E2E.dll` sha256 = `975c508d04392d0d98f269888f1b170a11b0e10c7372d7737d0e407c8cdd8b8a` |
| 2 | build | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)`（全解） |
| 3 | test（E2E） | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 246, Skipped: 0, Total: 246`（E2-a3 的 245 + 本轮 1） |
| 4 | selftest | `cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json` | 三次退出 0：最终二进制两次（§6 的 P1/P2）、拆分前的保存二进制一次（E1 的对照侧） |
| 5 | format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0、**0 字节输出** |
| 6 | inspectcode | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx` | 冻结树上解析 XML：`<Issue>` **0**、`<IssueType>` **0**，日志 528 个文件被 Inspecting、无 `CSharpErrors`（第一遍跑在拆分中间态，报了 7 条 `MemberCanBePrivate.Global`；按 §2.3b 收窄面后在冻结树上重跑为 0） |

> `rg -c '<Issue'` 会把 `<IssueTypes>`/`<Issues>` 数进去，必须解析 XML 元素（E2-a3 的提醒，本轮同样只认元素计数）。

**有效行**（§1.1 的脚本，三项目：`benchmarks/WinForward.E2E`、`benchmarks/WinForward.E2E.Contracts`、
`tests/WinForward.E2E.Tests`）：拆分前 7 行输出、拆分后 **3 行输出**（`MixArm` 720、`ReliabilityArm` 612、
`PersistentArm` 449），本批处理的四个文件与全部新文件都为 0 命中。

---

## 6. 判定比对（对照冻结基线 `run1`）

命令（四对共用，`$R=.trellis/tasks/10-07-e2e-harness-refactor/research`）：

```bash
python3 benchmarks/WinForward.E2E/scripts/compare-records.py <base> <after> \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict
```

| # | 比对 | 结构 | 条件键 | 身份 | 已声明 | **契约** | 改名 | 读数 | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|---|
| P1 | `run1` → `final-post-run1`（**判据**） | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 295/366 | 228/366 | **0** |
| P2 | `run1` → `final-post-run2`（确认） | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 299/366 | 227/366 | **0** |
| N1 | `final-post-run1` → `final-post-run2`（**同一二进制**，噪声地板） | 0 | 0 | 0 | — | 13 | — | 288/366 | 199/366 | 1 |
| E1 | `final-pre-run` → `final-post-run1`（**拆分前 vs 拆分后**） | 0 | 0 | 0 | — | 13 | — | 292/366 | 167/366 | 1 |

- **判据 P1/P2 两次都是结构/条件键/身份/契约全 0、改名 9/9 satisfied、退出码 0**；27 条「已声明」的
  键集变化全部是改名表 B2 的许可证（`tcp.sentOk → tcp.sent` 之类）。
- N1 的 13 条全部是 `no recorded jitter band for this path` 的**改名后无带宽**路径（`BASE metrics/latency/tcp.sent`、
  `LAT metrics/*.sent`、`MIX metrics/udp.sent` 等），是 E2-a3 已记录的同一族：改名表把键换了名字，
  冻结带宽里没有新拼写的带宽，于是按带宽 0 判。
- **E1 的 13 条与 N1 的 13 条逐行相同**（同一组路径、同一句 "no recorded jitter band"）：
  拆分前后的差异**不超过同一二进制两次运行的噪声地板**，没有第十四处差异。这是本批「行为零变化」
  的判定性证据。
- `--strict` 读数摘要：P1 的 366 条读数里 295 条有移动、228 条越带。按 D17.4 读数不参与判定，
  构成是宿主/时钟类——latency 直方图 70、内存读数 54、CPU 时间 33、boot 相对时钟与时长 41
  （`startedTicks`/`endedTicks` 26 + `wallSeconds`/`elapsedSeconds`/`ticks` 15）、毫秒/微秒测量 13、
  速率与派生量 10、进程 gauge 与计数 6、`connectTicks` 1（同一路径可能落入两类，计数按主类）。
  这些量在同一宿主上换个时刻跑就会移动（E2-a3 §3.9 已用同一二进制对跑量出同一族），本批没有任何
  契约键（含四个零宽键）随之移动。
- 出现噪声的一对（第一遍时的 `run1 → post-run-A`）命中 `LOSS metrics/reordered 0 → 2` 与
  `reorderRate`（6 条契约读数），与 E2-a3 的 `run-C`/`fix-B` 同族（靶机 `UdpEchoServer` 的 8 个并发接收
  循环决定回复顺序）；判据改用安静两次运行（P1/P2），并按 measurement-harness §3.5 由同一二进制对跑
  确认。

---

## 7. 偏离与登记

1. **不在纯搬移内的两处面收窄**：`UdpLaneTransport` 的 connect 观测面删除（§2.3a，来自 E2-a3 check 的
   必做项 2）；`ClientOptions` 五个 setter 收窄为 `private set` + `ProcessCounterSource.TryRefresh`/
   `TryReadLong` 收回 `private`（§2.3b，本轮 `jb inspectcode` 的 7 条 findings）。
2. **`ClientOptions` 换命名空间**（`WinForward.E2E.Client` → `WinForward.E2E.Cli`）：`Cli/` 目录已有
   `ExitCodes`，目录名=命名空间后缀是仓库约定；连带 6 个文件的 `using` 与 6 处调用点（`Program.cs` 1 处 +
   `ClientOptionsTests` 5 处）改名。类型仍是 `internal`，程序集面未变。
3. **测试侧改动**（没有放宽任何断言）：`ClientOptionsTests` 换调用接收者；`ContractShapeTests` 改调
   `ArmRecordWriter.WriteResultAsync`；`JsonKeyLiteralGateTests` 的 `s_writerFiles` 加进两个新 writer
   （gate 覆盖面**加宽**，逐名 `File.Exists` 自检）；`LaneTransportTests` 的 connect 断言改断
   `LaneOpenResult`；新增 `LatencyArmTotalsTests`。
4. **`README.md` 的 Layout 表**只改准指向已搬走文件的两行并补 6 行新文件（D12 把 README 的全面改写归 E5；
   这里只是不让表格指错文件）。
5. **未做（按范围）**：`MixArm`/`ReliabilityArm`/`PersistentArm` 的拆分（E2-b2）、Target 侧拆分、
   E2-c 的 CLI 合一、E3 语义修复。因此 `effective-lines.py` 在三项目上仍有 3 行输出（§5 末），
   归属 E2-b2。
6. **台账**：六个残留的靶机进程（`192.168.100.4:40011` 一个，`/tmp/e2a1chk/v1` 与 `/tmp/e2a1/old-bin`
   五个）不是本批启动的，未动；判定运行时的宿主 1 分钟负载 3.7（32 逻辑核），15 分钟均值 12.5
   （含本批的 inspectcode）。判定运行之间没有别的构建在跑（三次 selftest 串行，inspectcode 在其后）。
7. **check 轮裁定：`ProcessSample.cs` 的文件名接受不改**。该文件的两个类型是 `SampledProcess`（一个进程的身份）
   与 `ProcessTotals`（按名的求和），没有任何类型叫 `ProcessSample`，与「文件名 = 主类型名」的字面规则不符。
   接受的理由：`design.md` §4 的拆分表点名的就是 `ProcessSample.cs`（"每进程聚合模型"），且同项目已有按概念命名
   的文件先例（`Client/Lanes/LaneTransportContracts.cs`、`Client/UdpReliability.cs`，都不含与文件名同名的类型）；
   两个类型互为"一份样本"与"样本之和"，取任一个作文件名都只覆盖一半。若要收敛，改名为 `ProcessTotals.cs`
   即可（纯改名，无行为影响），登记给 E2-b2/E5 的目录重排。

---

## 8. 重建方式（check 轮可逐条重跑）

```bash
# 0. 有效行扫描（本批的 AC1 工具；拆分前 7 行输出，拆分后 3 行，归属 E2-b2）
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests

# 1. 六条门禁
cd benchmarks/WinForward.E2E && scripts/publish.sh && cd ../..
dotnet build WinForward.slnx -c Release                       # 0 Warning(s) / 0 Error(s)
dotnet test tests/WinForward.E2E.Tests -c Release             # Failed: 0, Passed: 246
cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json && cd ../..
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0，空输出
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb.xml WinForward.slnx  # 解析 XML：<Issue> 0

# 2. 判定比对（判据 P1/P2 与噪声地板 N1/E1）
R=.trellis/tasks/10-07-e2e-harness-refactor/research
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 <post-run1> \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict

# 3. 零宽键（D18.5 #12 的四个模式）在 run1 / 拆分前 / 拆分后 N 次之间逐值比对：
#    结果记录里匹配 metrics/*.received$、metrics/*unmatchedReplies$、
#    metrics/*outstandingAtTeardown$、latency/*-rtt/count$ 的 (记录, 路径) 对，共 34 对 / 18 条路径

# 4. 突变 M3′（覆盖判据）
#    删掉 benchmarks/WinForward.E2E/Client/Arms/LatencyPlan.cs 里
#    LatencyTotals.AddCounts 的 `Outstanding += counts.DeferredPending;`
#    → dotnet test tests/WinForward.E2E.Tests -c Release
#      => Failed: 1, Passed: 245（唯一红的是 LatencyArmTotalsTests，Expected: 6 / Actual: 2）
#    还原（cmp 逐字节相同）后 246/246 全绿

# 5. 机械搬移判定：/tmp 下的临时脚本 move-check.py（逻辑见 §2.1）→ `MECHANICAL MOVE`，退出 0
```

本轮全部临时产物留在 `/tmp/e2b1/`（`pre-linux`/`post-linux` 两份发布产物、
`final-{pre,post-run1,post-run2}` 三份 selftest 产物、四份比对输出、两份 inspectcode XML 与日志）；
仓库内只新增/修改 §1.3、§7 列出的源码与本文档。
