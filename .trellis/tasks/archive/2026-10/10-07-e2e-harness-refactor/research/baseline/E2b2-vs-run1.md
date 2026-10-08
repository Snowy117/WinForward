# E2-b2 拆分回归比对（剩余三个超标文件 + Target 侧拆分；对照基线 run1）

本批是 E2-b 的第二小批（2b-2），也是 E2 的最后一个文件结构批次：把 `effective-lines.py` 在三项目上
仍然报出的三个超标文件拆开（`MixArm` 720、`ReliabilityArm` 612、`PersistentArm` 449），并按
`design.md` §4 的目标形态做 Target 侧拆分（`TcpConnectionProtocol`/`SourceCensus`/`SocketIo`/
`TcpAcceptLoop`/`Cli/TargetOptions.cs`）；顺带闭合 E2-b1 check 转来的登记项之一（sample 键族进入
`ArmKeys` 与字面量 gate）。**`src/` 一行未改，本轮未提交。**

判定线：`effective-lines.py` 在三项目上**无输出**（AC1）；结构差异为空、契约栏零越带、
D18.5 #12 的四个零宽键在四次运行之间逐值相同、拆前/拆后差异 ≤ 同一二进制噪声地板；
三个 client 拆分组的机械搬移证据（token 多重集 + 字符串字面量多重集 + 有序检查）。

---

## 0. 结论一句话

三个超标文件（`MixArm` 720、`ReliabilityArm` 612、`PersistentArm` 449）拆成 14 个文件、每个
≤ 214 有效行；Target 侧四个文件按「协议状态机 / accept 循环 / 流 I/O / 源端点普查 / CLI 解析」五个
接缝拆成 9 个文件；`effective-lines.py` 在三个项目上**无输出**（AC1 达成）；三个 client 组的 token 差异
只剩「`private` → `internal` 加宽 + 跨文件限定 + 新类型声明」三类、字符串字面量多重集逐字相同
（9/18/5 条）、按「文件内声明顺序不倒退」的有序检查给出的 5 处成员换序全部可归因（Reliability 组的
两个常量与 `ModeTally` 一族，Mix/Persistent 组 0 处）；`run1 → post-run1`/`post-run2` 两次都是
结构/条件键/身份/契约全 0、改名 9/9 satisfied、退出码 0；拆前（HEAD 二进制）与拆后的 13 条契约差异
与同一二进制两次运行的 13 条**逐行相同**；D18.5 #12 的 34 个 (记录, 路径) 对在 run1 / 拆前 / 拆后两次
共四次运行之间逐值相同。

---

## 1. 拆分清单与旧文件去向

### 1.1 三个超标文件（纯搬移；AC1 的判据）

```bash
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests
# 拆分前：720 MixArm / 612 ReliabilityArm / 449 PersistentArm（3 行输出，exit 1）
# 拆分后：无输出，exit 0
```

| 旧文件 | 有效行 | 新文件 | 有效行 | 内容 |
|---|---|---|---|---|
| `Client/Arms/MixArm.cs` | 720 | `Client/Arms/MixArm.cs` | **82** | `RunAsync`（编排、`parameters`、7 条 note）、`RunDesktopAsync`（每桌面三 lane 起线程）、`DefaultDesktops`、`UdpPacketsPerSecond` |
| | | `Client/Arms/MixCounters.cs` | **37** | `MixCounters`（每桌面见证数组 + 臂级计数） |
| | | `Client/Arms/MixMetricsWriter.cs` | **173** | `UdpTotals`（提到顶层）+ `WriteMetrics` + `ClassifyDesktops`/`BuildUdpClass`/`CountIdleLanes`/`BuildDesktopLanes` + 三个 `Build*Class` |
| | | `Client/Arms/MixPageLoop.cs` | **209** | `PageLoopAsync`/`RequestsForConnection`/`PageConnectionAsync`/`PageDnsAsync`/`SendPageDnsQueryAsync`/`WaitForResponseAsync` + 4 个页几何常量 + `s_pageInterval` |
| | | `Client/Arms/MixBulkLoop.cs` | **68** | `BulkLoopAsync` + `BulkBitsPerSecond`/`BulkPayloadBytes` |
| | | `Client/Arms/MixUdpLoop.cs` | **175** | `UdpLoopAsync`/`TryOpenAsync`/`SendDatagramAsync`/`DrainPendingAsync`/`ReceiveUdpLoopAsync`/`BookDatagram` + `UdpPayloadBytes` |
| `Client/Arms/ReliabilityArm.cs` | 612 | `Client/Arms/ReliabilityArm.cs` | **143** | `RunAsync`（编排 + 5 条 note）、`PumpAsync`、`CollectAsync`、`BuildSchedule`、`GreatestCommonDivisor`、`s_outcomeNames`、`FramePayloadBytes` |
| | | `Client/Arms/ReliabilityAttempt.cs` | **104** | `ReliabilityOutcome`/`ExchangeStatus`/`ExchangeResult`/`ReliabilityAttempt` + `AttemptEvidence`（提到顶层，含两个预算常量） |
| | | `Client/Arms/ReliabilityExchange.cs` | **190** | `ExpectedOutcome`/`RunAttemptAsync`/`ExchangeAsync`/`ReceivePhaseAsync`/`Classify`/`SendRequestAsync` + `AttemptTimeoutMilliseconds` |
| | | `Client/Arms/ReliabilityTally.cs` | **108** | `ReliabilityTally`（`TallyAttempts` 成为它的静态工厂）+ `ModeTally` |
| | | `Client/Arms/ReliabilityMetricsWriter.cs` | **85** | `BuildMetrics`/`Outcomes`/`BuildModeBreakdown` |
| `Client/Arms/PersistentArm.cs` | 449 | `Client/Arms/PersistentArm.cs` | **202** | `PersistentCounters` + `RunAsync`/`BuildMetrics`/`BuildSchedule`/`WaitForInstant`/`RunPersistentAsync`/`RunRoundAsync` |
| | | `Client/Arms/PersistentPlan.cs` | **41** | `PersistentPlan`/`PersistentSchedule` |
| | | `Client/Arms/PersistentExchange.cs` | **214** | `PersistentExchange`/`PersistentFrame`/`PersistentWait` 三个枚举 + `PersistentLink` + `PersistentConnection`（`ExchangeAsync`/`TryReadEchoAsync`/`TryReadFrameAsync`/`WaitForReadable`/`TryOpenLinkAsync` + `ConnectionIdBase`） |

14 个文件全部 ≤ 214 有效行。**没有建 pass-through 别名层**：旧方法一律搬到新类型上、只加调用点限定
（`MixPageLoop.PageLoopAsync`、`ReliabilityMetricsWriter.BuildMetrics`、`PersistentConnection.TryOpenLinkAsync`…），
没有一行转调的旧入口；`ReliabilityArm.s_outcomeNames`（失败/成功词汇表，被 `AttemptEvidence`、
`TallyAttempts`、`ModeTally` 三处读）与 `ReliabilityArm.FramePayloadBytes` 是仅有的两处「留在臂上的
internal 成员」。

### 1.2 Target 侧（**重构，不是纯搬移**；接缝与收益见 §4）

| 文件 | 有效行 | 变化 |
|---|---|---|
| `Target/TcpTargetServer.cs` | 121（原 360 总行） | 只剩监听器生命周期、连接记账、账本 JSON、`targetSummary` 的 tcp 段 |
| `Target/TcpConnectionProtocol.cs` | **157**（新） | 一条 TCP 连接的 command/echo 状态机 + 两个 DTO（`TcpModeOutcome`/`CommandOutcome`）+ `s_stallDelay` |
| `Target/SocketIo.cs` | **32**（新） | `TrySendAllAsync`/`ReadExactAsync` |
| `Target/TcpAcceptLoop.cs` | **49**（新） | accept 循环 + 连接表 + 修剪阈值 + 排水（`DrainAsync`）+ 监听器释放 |
| `Target/SourceCensus.cs` | **119**（新） | `SourceCensus`（原 `UdpEchoServer` 的嵌套类）+ `SourceKey` + `Slot` + `ToAddress` |
| `Target/UdpEchoServer.cs` | 167 | 只剩收发循环、`summary` 与 `udpSummary` 的组装 |
| `Target/DnsServer.cs` | 225 | accept 循环/连接表/两个流 helper 换成 `TcpAcceptLoop` + `SocketIo` |
| `Target/TargetRunner.cs` | 127 | 只剩编排、账本信封、四个 summary 的守卫与 `--help` |
| `Cli/TargetOptions.cs` | **146**（新；原 `Target/TargetOptions.cs` 35 总行） | `TargetOptions` + 参数解析（`TryCreate`/`ValidatePorts`/`Apply`/`TryPort`），命名空间 `WinForward.E2E.Target` → `WinForward.E2E.Cli` |
| `Target/TargetOptions.cs` | 删除 | 内容并入 `Cli/TargetOptions.cs` |

### 1.3 登记项闭合涉及的文件（**不是搬移**，见 §4.3）

`benchmarks/WinForward.E2E.Contracts/ArmKeys.Sample.cs` 39 有效行（新分片）、
`Client/ResourceSampleWriter.cs` 100（新增 `WriteSampleHeader`，按 gate 同款正则实测 22 处字面量键归零——check 轮更正；实现轮写的 21 是粗口径）、
`Client/ResourceSampler.cs` 165（按同一正则在 HEAD 上实测 19 处字面量键归零——check 轮更正；实现轮写的 10 是粗口径）、
`tests/WinForward.E2E.Tests/SampleRecordShapeTests.cs` 139（新，4 条）、
`tests/WinForward.E2E.Tests/JsonKeyLiteralGateTests.cs` 125（gate 覆盖面加宽：两个文件入列 + 新键入已知键集）。

---

## 2. 机械搬移证据（三个 client 组）

### 2.1 方法

`/tmp/e2b2/move-check.py`（临时脚本，逻辑自包含、可重建）把每个分组的**文件并集**当作一份源码：
按 `effective-lines.py` 的规则去注释、去 `using`/`namespace` 脚手架，再分词（字符串字面量整段算一个
token，插值洞里的表达式按代码分词）。对照两侧是 `git show HEAD:<旧文件>` 与当前工作树。判据是三条
**可完全归因**的等式（与 E2-b1 §2.1 相同）：

| 允许的差异 | 等式 |
|---|---|
| 可见性加宽 | `added['internal'] = removed['private'] + 新类型数` |
| 跨文件限定 | `added['.'] = Σ added[限定名] − 新类型数` |
| 新类型自己的声明 | `added['class'|'static'|'{'|'}'] = 新类型数` |
| 其余 | 不得有别的 token 差异（白名单为空则失败） |

外加一条独立的**字符串字面量多重集相同**判定（插值洞清空后比较）：记录里发布的每一条文本
（MIX 的 7 条 note、REL 的 5 条 note、全部 `ArmKeys` 用法与错误文本）逐字未动。

### 2.2 结果（`MECHANICAL MOVE`，退出码 0）

| 分组 | token 前→后 | `private`→`internal`（净） | 跨文件限定 | 新类型 | 字面量 |
|---|---|---|---|---|---|
| `MixArm` | 4496 → 4544 | 12（含 `UdpTotals` 提顶层） | 12 处（`MixPageLoop` 7 + `MixBulkLoop` 3 + `MixMetricsWriter` 2，`MixArm.UdpPacketsPerSecond` 1 处属「留在臂上」不计） | 4（`MixMetricsWriter`、`MixPageLoop`、`MixBulkLoop`、`MixUdpLoop`） | 9 条逐字相同 |
| `ReliabilityArm` | 3746 → 3784 | 9 | 13 处（`ReliabilityArm` 7 + `ReliabilityExchange` 2 + `ReliabilityMetricsWriter` 2 + `AttemptEvidence` 1 + `ReliabilityTally` 1；括号里是调用点） | 2（`ReliabilityExchange`、`ReliabilityMetricsWriter`） | 18 条逐字相同 |
| `PersistentArm` | 2603 → 2615 | 2（`ExchangeAsync`/`TryOpenLinkAsync`） | 3 处（`PersistentConnection` 2 + `PersistentArm` 1） | 1（`PersistentConnection`） | 5 条逐字相同 |

**可见性加宽的完整清单**（逐组，脚本枚举；`private` → `internal`）：

- `MixArm`（12）：类型 `UdpTotals`；常量 `PageConnections`、`PageTotalRequests`、`PageMessageBytes`、
  `DnsQueriesPerPage`、`s_pageInterval`、`BulkBitsPerSecond`、`UdpPayloadBytes`；
  方法 `WriteMetrics`、`PageLoopAsync`、`BulkLoopAsync`、`UdpLoopAsync`。
  （`BulkPayloadBytes` 拆分后只被 `MixBulkLoop` 自己的循环读，`jb inspectcode` 的
  `MemberCanBePrivate` 让它保持 `private`，所以不在加宽清单里。）
- `ReliabilityArm`（9）：常量 `FramePayloadBytes`、`s_outcomeNames`；方法 `BuildMetrics`、`TallyAttempts`、
  `Outcomes`、`RunAttemptAsync`、`ExchangeAsync`、`ReceivePhaseAsync`、`SendRequestAsync`。
  （`ExpectedOutcome` 同理保持 `private`：拆分后它的两个读者都在 `ReliabilityExchange.cs` 内。）
- `PersistentArm`（2）：方法 `ExchangeAsync`、`TryOpenLinkAsync`。

这些是**可见性调整**，不是逻辑改动：每一处的函数体、语句顺序、常量取值、方法签名逐字未动。

### 2.3 有序检查（E2-b1 §2.4 的两种口径）

**(a) 行级有序检查**（`/tmp/e2b2/order-check.py`）：把每组的拆分后文件按「与原文件最匹配的排列」
拼接后与拆分前逐行 diff，每一行必须落在四类之一：等值行、只差可见性/限定的替换行、新类型的
声明外壳行、以及「在某处删除又在另一处逐字插入」的搬移行。结果：

| 分组 | 等值行 | 限定替换 | 新类型外壳 | 搬移行 | 未归因 |
|---|---|---|---|---|---|
| `MixArm` | 631 | 9 | 26 | 64 | **0** |
| `ReliabilityArm` | 462 | 10（+2 加宽替换） | 44 | 112 | **0** |
| `PersistentArm` | 379 | 4（+1 加宽替换） | 27 | 46 | **0** |

**(b) 成员顺序检查**（判据：**文件内**声明顺序不得倒退，每条拆分前的声明只能被消费一次）：
`MixArm` 0 处、`PersistentArm` 0 处；`ReliabilityArm` 5 处，全部可归因：

1. `MaxAttemptRecords`、`AttemptSampleStride` 两个常量**换了归属**（`ReliabilityArm` 的常量块 →
   `AttemptEvidence`），因为它们的唯一读者是证据写入器，臂只在 note 里插值引用（现在读
   `AttemptEvidence.MaxAttemptRecords`/`.AttemptSampleStride`）；
2. `ModeTally` 及其 `Add`/`ToRecord` 从「`ReliabilityArm` 的私有嵌套类、声明在 `BuildModeBreakdown`
   之后」变成「`ReliabilityTally.cs` 的顶层类、声明在 `ReliabilityTally` 之后」，于是相对
   `TallyAttempts`（搬进 `ReliabilityTally` 成为静态工厂）提前了。

判定：**不影响行为**，依据与 E2-b1 §2.4 相同的三条——(a) 成员的类内顺序、`const` 的归属、`record`/
`class` 的声明位置都不改 IL 语义；(b) 拆分涉及的 14 个文件里唯一的静态初始化器是
`MixPageLoop.s_pageInterval = TimeSpan.FromSeconds(20)`（只读 BCL 类型）与
`ReliabilityArm.s_outcomeNames`（只读字面量），`ModeTally._observed = new long[ReliabilityArm.s_outcomeNames.Length]`
虽然跨类型读，但 `ModeTally` 只在 `BuildMetrics`（←`RunAsync`）里被构造，此时 `ReliabilityArm` 的静态
初始化必然已完成，不存在初始化顺序依赖；(c) 行级有序检查下三组各有 0 行未归因。

### 2.4 Target 侧：不是搬移，按「重复消失」与字面量记账

Target 侧是**重构**（下面的 §4.1 逐条声明），token 多重集不再适用；这里给出可复核的三件事。

**(a) 字符串字面量多重集**：整组（9 个文件）前后 102 条逐字相同，**只多一条**
`"The peer closed while a frame was being echoed."`（声明的）：它原来写在 `TcpTargetServer.SendAllAsync`
内部，现在 `SocketIo.TrySendAllAsync` 回答 bool，于是 `TcpConnectionProtocol` 的两个调用点各自抛出这句
原文——同一条文本多出现一次，没有任何文本被改写或丢失。

**(b) 每文件 token 数**（前 → 后，脚本输出）：

```
2001 → 768   TcpTargetServer.cs          1106  TcpConnectionProtocol.cs（新）
1419 → 1173  DnsServer.cs                 169  SocketIo.cs（新）
1646 → 906   UdpEchoServer.cs             189  TcpAcceptLoop.cs（新）
1558 → 890   TargetRunner.cs              742  SourceCensus.cs（新）
 115 → —     TargetOptions.cs（删除）      788  Cli/TargetOptions.cs（新）
```

**(c) 真正消失的重复**（`rg` 计数，HEAD → 现在）：

| 重复 | 拆分前 | 拆分后 |
|---|---|---|
| accept 循环（accept 阶梯 + `NoDelay` + 入表 + 修剪） | 2 份（`TcpTargetServer.RunAsync`、`DnsServer.AcceptLoopAsync`），`AcceptAsync` 命中 2 文件 | 1 份（`TcpAcceptLoop.RunAsync`），`AcceptAsync` 只命中 `TcpAcceptLoop.cs` |
| 连接表与修剪 | 2 份（`_connections`/`_tcpConnections` 各一套，各有 `RemoveAll` 与字面量 `256`） | 1 份（`_connections` + `PruneThreshold`） |
| 「发满」循环 | 2 份（`TcpTargetServer.SendAllAsync` 抛 `IOException`、`DnsServer.SendAllAsync` 静默返回） | 1 份（`SocketIo.TrySendAllAsync`，失败策略留在各自调用点） |
| 「读满」循环 | 1 份（`DnsServer.ReadExactAsync`） | 1 份（`SocketIo.ReadExactAsync`，与发满同居） |
| 源端点普查 | 宿主文件的嵌套类（122 行） | 顶层 `SourceCensus.cs`（含 `SourceKey`/`Slot`/`ToAddress`） |
| CLI 解析 | 混在 `TargetRunner`（`s_knownOptions` 是 `TargetOptions` 的 public 数组、解析器在 runner 里） | `TargetOptions.TryCreate`（解析器与选项同居，数组收成 `private`） |

账本段落 `ILedgerSection` **未抽**（收益不明确，见 §4.2）。

---

## 3. D18.5 #12 的零宽发布键：四次运行逐值相同

脚本按 D18.5 #12 的四个模式（`metrics/*.received$`、`metrics/*unmatchedReplies$`、
`metrics/*outstandingAtTeardown$`、`latency/*-rtt/count$`）取每次运行的**全部 result 记录**，
列出 (记录, 路径) 对与值。覆盖四次运行：冻结基线 `run1`、拆前（HEAD 二进制）一次、拆后两次。

**34 个 (记录, 路径) 对 / 18 条路径，五对比较（run1↔拆前、run1↔拆后×2、拆前↔拆后、拆后↔拆后）
`diff` 全空**：

| 记录 | 路径 | 值 |
|---|---|---|
| `LAT` | `metrics/tcp.received` / `tcp.unmatchedReplies` / `tcp.outstandingAtTeardown` | 322 / 0 / 0 |
| `LAT` | `metrics/udp.received` / `udp.unmatchedReplies` / `udp.outstandingAtTeardown` | 161 / 0 / 0 |
| `LAT` | `latency/tcp-rtt/count`、`latency/udp-rtt/count` | 322、161 |
| `LATLOAD` | 同上六个 + 两条 `-rtt/count` | 1601 / 0 / 0、1601 / 0 / 0、1601、1601 |
| `BASE` | `metrics/latency/{tcp,udp}.*`、`metrics/loss/unmatchedReplies`、两条 `-rtt/count` | 101 / 0 / 0、101 / 0 / 0、0、101、101 |
| `DNS` / `DNSALT` | `latency/dns-rtt/count` | 402 / 402 |
| `LOSS` / `PERSIST` | `metrics/unmatchedReplies` | 0 / 0 |
| `PERSIST` | `latency/tcp-rtt/count` | 15 |
| `MIX` | `metrics/classes/udp/unmatchedReplies`、`latency/{tcp,udp,dns}-rtt/count` | 0、146、602、8 |

（本条同时覆盖 sample 键族改动：`sample` 记录的键集与值由 compare-records 的结构类判定，见 §6 的
`structural=0`。）

---

## 4. 偏离与登记（逐条声明）

### 4.1 Target 侧：五处声明

1. **`SocketIo.TrySendAllAsync` 返回 `bool` 而不是抛/静默**：两份「发满」循环的差别**只在失败策略**
   （`TcpTargetServer` 抛 `IOException`、`DnsServer` 静默返回并被调用方忽略）。共享的是循环本身，
   策略留在调用点：`TcpConnectionProtocol` 的两个调用点各自 `if (!await …) throw new IOException("The peer closed while a frame was being echoed.");`
   （异常类型、文本、传播路径与原来相同，只是抛出点从 helper 内部移到协议文件）；`DnsServer.AnswerTcpAsync`
   的两个调用点 `_ = await …`（与原来忽略 void 返回同义：应答丢弃、下一条读看到流结束），并写了一条
   注释说明这个丢弃是有意的。
2. **`TcpAcceptLoop` 不拥有「谁停连接」**：accept 阶梯、连接表、修剪阈值与排水搬进 `TcpAcceptLoop`，
   但 `TcpTargetServer` 的 `_shutdown`（连接 token）与 `DnsServer` 的运行 token 仍由各自持有，
   `RunAsync(handle, token)` + `DrainAsync()` 两步调用，与两份原实现逐句对应（`TcpTargetServer`：
   循环 → `_shutdown.CancelAsync()` → 排水；`DnsServer`：accept 作为第 N+1 个 worker → 排水）。
3. **`SourceCensus` 提升为顶层类型**：`SourceCapacity`/`Slot` 随它搬走；`ToAddress` 从
   `UdpEchoServer` 的私有方法变成 `SourceCensus.ToAddress`（它的输入类型是普查的键）；
   `SourceKey` 从嵌套类型提升为顶层 `internal readonly record struct`（token/行级检查里都记为
   「类型提升」而不是新类型）。
4. **`TargetOptions` 换命名空间并入 `Cli/`**：与 E2-b1 的 `ClientOptions` 同形（目录名 = 命名空间后缀）；
   连带 `Program.cs` 1 处调用点与 `TargetOptionsTests` 5 处调用点改名（`TargetRunner.TryCreate` →
   `TargetOptions.TryCreate`）。同时把 7 个属性 setter 收窄为 `private set`、`s_knownOptions` 收窄为
   `private`：解析器搬进类型之后，这些写入者只剩本类型的 `Apply`（`rg` 复验：`TargetRunner.cs`/`Program.cs`/
   测试都只读）。这是**面收窄**，与 E2-b1 §2.3b 同类。
5. **`TcpTargetServer.TcpModeOutcome`/`CommandOutcome` 两个 DTO 随协议搬走**：它们是协议的输出形状
   （`RunConnectionAsync` 的返回值），服务器只读它们的属性。

### 4.2 未做（按范围 / 收益不明确）

1. **`ILedgerSection` 未抽**（`implement.md` E2-b 的「若实际收益明确」）：三台 server 的
   `WriteSummaryAsync` 里只有 `TcpTargetServer` 与 `DnsServer` 是「`type` + `WriteTotals`」两行体
   （约 18 行），`UdpEchoServer` 的 summary 还要先算普查、写 `sources` 数组，无法用同一个体；
   抽接口需要给三台 server 各加 `SummaryType` 与 `Ledger` 暴露面，换来的只是删掉 18 行与 4 个 lambda，
   而 `WriteSummariesAsync` 的守卫、顺序与 `targetSummary` 的嵌套仍要手写。判定：**收益不明确，未做**。
2. **Target 侧的账本键仍用字面量**（D14.16 把 `Target/**` 的键也划给 E2，本轮未做）：四个 target 文件
   现有 51 处 key 位字面量（`TcpTargetServer` 14、`UdpEchoServer` 14、`DnsServer` 13、`TargetRunner` 10——check 轮按 gate 同款正则实测更正），
   它们属于**另一族记录**（target 账本：`tcp`/`udpSummary`/`dnsSummary`/`targetSummary`），
   与本轮闭环的 sample 族（写进 client 的 arm 文件）不同族。本轮任务只点名了 sample 族；
   该族需要 `ArmKeys` 新分片 + 4 条记录的形状测试 + gate 行，**登记为后续批次（E5 或专门一批）的必做项**。
3. **E2-c 的 CLI 解析器合一未做**（按范围：`ClientOptions` 与 `TargetOptions` 的 `TryCreate` 仍是两份；
   本轮只把 target 的那份搬进 `Cli/TargetOptions.cs`，与 `Cli/ClientOptions.cs` 并列，为 E2-c 备好接缝）。
4. **E3 语义修复未做**（按范围）。

### 4.3 登记项收尾（E2-b1 check 转来的两条）

1. **sample 键族：本轮做了**（D12 的「`Sample` 记录（ResourceSampler 的 23 个字面量键与三态）| E2（随
   ResourceSampler 拆分）」）。落地：
   - 新增 `benchmarks/WinForward.E2E.Contracts/ArmKeys.Sample.cs`：`Sample`（11 键：`ticks`/`process`/
     `self`/`matched`/`absent`/`handles`/`generatorCpuSeconds`/`envWorkingSetBytes`/`processes`/
     `readErrors`/`readError`）+ 嵌套 `Counters`（5）/`ProcessEntry`（5，数组元素层）/`SamplerError`（2）；
     记录的 `type`/`arm` 复用 `ArmKeys.Common.Record`（同一层的同一个成员不重复声明，与分片注释的规则一致）。
   - `Client/ResourceSampleWriter.cs`（22 处）与 `Client/ResourceSampler.cs`（19 处）的 key 位字面量归零
     （`rg 'Write\w+\("' 两个文件均无命中）。
   - 新增 `ResourceSampleWriter.WriteSampleHeader`：两份 `sample` 记录的**公共壳**（`type`/`ticks`/`arm`/
     `process`/`self`/`matched`）从 `ResourceSampler` 的两个 lambda 里搬进 writer——`design.md` §4 把这
     一族记录的「JSON 形状」划给该文件，这同时是形状测试能直接驱动壳的原因（否则壳只能靠跑 1 Hz 采样器
     间接覆盖）。
   - 字面量 gate（`JsonKeyLiteralGateTests`）把 `ResourceSampler.cs`/`ResourceSampleWriter.cs` 加入
     `s_writerFiles`（逐名 `File.Exists` 自检），已知键集加入 `ArmKeys.Sample` 的四个子树。
   - 形状测试 `SampleRecordShapeTests`（4 条）用**生产 writer**发布三态并逐向比对：self（含
     `readErrors`/`readError` 两个只在未读到的进程上出现的键）、命中的具名样本、未命中的具名样本
     （只留 `absent` + 普查）、`samplerError`。**没做**的写法是把 `sample` 记录的字段名/值也搬进
     `ArmKeys` 之外的地方；值的字面量（`"sample"`/`"samplerError"`/`"exchanged"`…）按 D14.16 的口径
     不是键，保持原样。
2. **`ProcessSample.cs` 文件名：本轮不做**（check 已裁定「接受」）。它装的是 `SampledProcess`（一个进程
   的身份）与 `ProcessTotals`（按名求和），与「文件名 = 主类型名」的字面规则不符，但 `design.md` §4
   点名的就是这个名字，且同项目已有按概念命名的先例（`LaneTransportContracts.cs`、`UdpReliability.cs`）。
   改名是纯改名、无行为影响，登记给 E5 的目录重排。

### 4.4 其它

1. **测试侧改动（没有放宽任何断言）**：`ContractShapeTests` 改调顶层 `AttemptEvidence`
   （原 `ReliabilityArm.AttemptEvidence`）；`TargetOptionsTests` 换命名空间与调用接收者；新增
   `SampleRecordShapeTests`；`JsonKeyLiteralGateTests` 覆盖面**加宽**。
2. **`README.md` 的 Layout 表**：改准 `Client/Arms/` 与 `Target/` 两行并补 5 行新文件（D12 把 README 的
   全面改写归 E5；这里只是不让表格指错文件）。
3. **注释**：三个 client 组与 Target 侧的搬移一律**逐字带注释**（含 `#pragma`/`// ReSharper disable`
   的理由注释、`catch` 空体的说明注释）；唯一的**新**注释是 `SocketIo`/`TcpAcceptLoop`/`TcpConnectionProtocol`/
   `WriteSampleHeader`/`ToAddress` 的类型与方法文档（新接缝的契约），以及 `DnsServer` 里说明 `_ = await` 的
   两行——它们在 token/字面量检查里都被剥掉，但都是「读者需要知道」的契约说明。

---

## 5. 门禁（冻结树，六条逐条）

| # | 门禁 | 命令 | 结果 |
|---|---|---|---|
| 1 | publish | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2b2/post scripts/publish.sh` | 退出 0；`linux`/`win`/`win-direct` 三份产物齐全（两个 Windows 客户端按镜像名可分）；`linux/WinForward.E2E.dll` sha256 = `1d02db8b430d6b4c04be85c39111e6a7d8cdea1bccbe8ae9cbbf90a903bd85e6` |
| 2 | build | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)`（全解） |
| 3 | test（E2E） | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 250, Skipped: 0, Total: 250`（E2-b1 的 246 + sample 形状 4） |
| 4 | selftest | `cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json` | 三次退出 0：拆后二进制**连续两次**（`post1`/`post2`）+ 拆前的 HEAD 二进制一次（`pre1`，即拆前对照） |
| 5 | format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0、**0 字节输出** |
| 6 | inspectcode | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e2b2/jb-inspectcode.xml WinForward.slnx` | 545 个文件被 Inspecting、无 `CSharpErrors`；解析 XML：`<Issue>` **0**、`<IssueType>` **0**（只认元素计数，不认 `rg '<Issue'`） |

> **第六条的中间态**：第一遍 inspectcode 跑在拆分后的中间态，报 **12 条**（7 `RedundantUsingDirective`——
> `ArmContext`/`FrameBuffer` 住在祖先命名空间 `WinForward.E2E.Client`，新文件里多余的
> `using …Contracts`/`…Wire` 被点出；2 条 `MemberCanBePrivate.Global`——`MixBulkLoop.BulkPayloadBytes` 只被
> 本文件的循环读、`ReliabilityExchange.ExpectedOutcome` 只被同文件的两处读；`ConvertClosureToMethodGroup`
> ——`socket => HandleConnectionAsync(socket)` 收成方法组；`InvalidXmlDocComment`——`ArmKeys.Sample` 的
> `<see cref="ProcessEntry"/>` 要写成 `Sample.ProcessEntry`；`AccessToDisposedClosure`——形状测试的
> lambda 改捕获 `SamplerTarget` 而不是外层 `await using` 的 sink）。逐条修好后（build 0/0、测试 250/250、
> **重新 publish + 两次 selftest + 四对 compare-records + 零宽键重跑**，§5/§6 的数字全部来自这个最终二进制）
> 在冻结树上重跑为 0。

**有效行（AC1）**：`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E
benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests` → **无输出、退出码 0**（拆分前是 3 行、
退出码 1）。

---

## 6. 判定比对（对照冻结基线 run1）

命令（四对共用，`$R=.trellis/tasks/10-07-e2e-harness-refactor/research`）：

```bash
python3 benchmarks/WinForward.E2E/scripts/compare-records.py <base> <after> \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict
```

| # | 比对 | 结构 | 条件键 | 身份 | 已声明 | **契约** | 改名 | 读数 | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|---|
| P1 | `run1` → `post1`（**判据**） | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 297/366 | 221/366 | **0** |
| P2 | `run1` → `post2`（确认） | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 298/366 | 234/366 | **0** |
| N1 | `post1` → `post2`（**同一二进制**，噪声地板） | 0 | 0 | 0 | — | 13 | — | 287/366 | 154/366 | 1 |
| E1 | `pre1` → `post1`（**拆前 vs 拆后**） | 0 | 0 | 0 | — | 13 | — | 287/366 | 167/366 | 1 |

- **判据 P1/P2 两次都是结构/条件键/身份/契约全 0、改名 9/9 satisfied、退出码 0**；27 条「已声明」与
  E2-b1 相同，全部是改名表 B2 的许可证（`tcp.sentOk → tcp.sent` 之类）。
- **N1 与 E1 的 13 条逐行相同**（`diff` 只差「读到第 N 条」的汇总行）：全部是
  `records/{BASE,DNS,DNSALT,LAT,LATLOAD,MIX}.jsonl` 上 8 条改名后无带宽的 `*.sent`、
  `MIX` 的 `metrics/desktops/udp.{arrived,foreignConnection,sent}` 与 `metrics/udp.{sent,lossRate}`，
  句式都是 `no recorded jitter band for this path`——与 E2-a3/E2-b1 已记录的同一族。
  **拆前/拆后的差异不超过同一二进制两次运行的噪声地板，没有第十四处差异**：这是本批「行为零变化」的
  判定性证据。
- `--strict` 读数摘要（P1）：366 条读数里 297 条有移动、221 条越带（P2：298/234）。按主类构成：
  latency 直方图、内存读数、boot 相对时钟与时长（`startedTicks`/`endedTicks`/`wallSeconds`/
  `elapsedSeconds`/`ticks`）、CPU 时间、毫秒/微秒测量、速率与派生量以及少量进程 gauge（同一路径可能落入
  两类，计数按主类）。按 D17.4 读数不参与判定；构成与 E2-b1 §6 同族（`jitter-band.json` 冻结自同一 boot 的
  run1↔run2，跨 boot 的时钟与宿主采样读数必然越带，见 `E2-a1-reading-counter` 条目）。
- **契约栏零越带**：`metrics/*`、`gates/*`、`parameters/*`、布尔量全部落在各自 per-key 带宽内（512 条
  契约读数，P1/P2 `contract=0`），其中含 D18.5 #12 的四个零宽键（§3 已逐值核对）。
- **sample 键族改动的等价性**：`sample` 记录是 `records/*.jsonl` 的一部分，compare-records 的结构类
  逐路径比对它们的键集与值——P1/P2 的 `structural=0` 即「95 条 `sample` 记录的键集与值逐条不变」的
  机械判定（`samplerError` 在 selftest 里不产生记录，由 §4.3 的形状测试覆盖）。

---

## 7. 重建方式（check 轮可逐条重跑）

```bash
# 0. AC1 工具：三项目无输出、退出码 0
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests

# 1. 六条门禁（顺序不可换：publish 冻结二进制，selftest/compare 都在它之上）
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2b2/post scripts/publish.sh && cd ../..
dotnet build WinForward.slnx -c Release                       # 0 Warning(s) / 0 Error(s)
dotnet test tests/WinForward.E2E.Tests -c Release             # Failed: 0, Passed: 250
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2b2/post scripts/selftest.sh scripts/plans/selftest-plan.json && cd ../..
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0，空输出
jb inspectcode -f=Xml -e=HINT -o=/tmp/e2b2/jb-inspectcode.xml WinForward.slnx   # 解析 XML：<Issue> 0

# 2. 判定比对（P1/P2 与噪声地板 N1/E1；pre1 用拆前的 HEAD 二进制跑同一条 selftest）
R=.trellis/tasks/10-07-e2e-harness-refactor/research
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e2b2/post1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict

# 3. 零宽键（D18.5 #12 的四个模式）在 run1 / pre1 / post1 / post2 之间逐值比对
python3 /tmp/e2b1/zerowidth.py <run>/out | diff - <run2>/out   # 34 对 / 18 路径，四次全同

# 4. 机械搬移：token 多重集（三组 MECHANICAL MOVE、Target 组按声明记字面量）
python3 /tmp/e2b2/move-check.py      # exit 0
python3 /tmp/e2b2/order-check.py     # 三组各行归因，0 未归因
python3 /tmp/e2b2/member-order.py    # 5 处成员换序（Reliability 组），Mix/Persistent 0
```

本轮第一遍 inspectcode 之后的 12 条修正也重新走了一遍 publish → 两次 selftest → 四对 compare-records（check 轮注明：第一遍的 XML 报告已被最终那次覆盖，条数以 check 轮枚举的 12 条为准）
（§5/§6 的 sha256、读数与零宽键都是最终二进制的读数）。

本轮全部临时产物留在 `/tmp/e2b2/`（`pre`/`post` 两份发布产物、`post1`/`post2`/`pre1` 三份 selftest
产物、四份比对输出、两份 inspectcode XML 与日志、三个检查脚本）；仓库内只新增/修改 §1、§4 列出的源码、
测试与本文档。判定运行（三次 selftest 与四次 compare-records）之间没有别的构建在跑，串行执行。
