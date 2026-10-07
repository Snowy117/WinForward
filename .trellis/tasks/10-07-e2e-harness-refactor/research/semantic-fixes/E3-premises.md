# E3 前提复核（premise re-verification）

**已加载 skill：`codebase-design`（按深度/接缝/删除测试判据审阅）与 `trellis-before-dev`（读 PRD/design/implement 与 `.trellis/spec` 索引后再动手）。本次全程只读，唯一写入是本文件。**

- 复核基线：`HEAD = f8b31c6`（`git rev-parse HEAD`），工作树另有 4 个与本任务无关的 dirty 文件（`10-07-tcp-close-drain/*`）。
- 权威冲突顺序：`design-decisions.md` > 父 `prd.md`/`design.md`/`implement.md` > 子任务 `prd.md`（D1.1）。
- **本文所有 `file:line` 都是 HEAD 实测行号**。`harness-audit.md`、`design-decisions.md` D0、子任务 `prd.md` 里引用的行号**大部分已因 E1/E2 的拆分与迁移失效**，凡失效处本文都给出现行行号（见 §2）。
- 未运行 `dotnet build/test`、`jb inspectcode`、`publish.sh`、`selftest.sh`（禁止项）。所有结论来自 `rg` / `python3` 只读脚本 / `git log` / 直接读源码。

---

## 0. 加载的 skill

| skill | 用途 |
|---|---|
| `codebase-design` | 判据语言：`LaneEngine` 的接口 vs 策略实现、`UdpReliabilityTracker` 的"单写者"是否仍是接口事实、账本三字段的接缝位置（`ArmKeys` vs writer）。§3 的分组与 §4 的冲突裁定都用这套词汇。 |
| `trellis-before-dev` | 读了 `10-07-e2e-e3-semantics/prd.md`、`10-07-e2e-harness-refactor/{prd,design,design-decisions,implement}.md`、`.trellis/spec/` 索引与 `design-decisions.md` D0/D2/D9/D14/D18。 |

---

## 1. 结论表

> 列义：**结论** ∈ {已实现, 部分, 未做, 不适用}；**E3 是否还需要动作** = 今天就需要写代码/写测试/裁定；**建议批次** = 父 `implement.md:229-236` 的 C1–C8。

| # | 条目 | 结论 | 证据（file:line / 命令） | E3 还需动作 | 建议批次 |
|---|---|---|---|---|---|
| 1 | `UdpReliabilityTracker` 并发契约：谁在什么线程写、有无并发测试 | **未做** | 契约**未**写进类文档（`Client/UdpReliability.cs:119-124` 只讲记账，零线程字样）；LOSS 单线程（`Client/Arms/LossArm.cs:32-34` 独占线程 + `:161` 在发送循环内联 drain）；**MIX 是真多写者**（`Client/Arms/MixUdpLoop.cs:34` 接收任务并发起，`:121` 发送线程写 `MarkSent`，`:151`→`:194` 接收线程写 `MarkArrival`，同一 tracker）；唯一并发测试是 E2 的 `LaneEngine` 契约（`tests/WinForward.E2E.Tests/Lanes/LaneEngineConcurrencyTests.cs:24`），**不是** tracker 的 | **是**：D4 的形状（发送线程独占 + 接收投递队列，或全 `Interlocked`）+ 类文档 + N 轮并发恒等式测试 | **C1** |
| 2 | `#4` 0/10/50% 丢包下 `sent == supplied` 且发满全程 | **部分** | 机制在：槽位按 `发送时刻 + W` 到期释放（`Client/UdpReliability.cs:313-331`），在窗口判定**之前**调用（`LossArm.cs:169` → `:171`）；窗口默认 4096（`LossArm.cs:15`），实发计划 LATLOAD/LOSS 用 `window:4096`、`ratePerSecond:500`（`scripts/plans/full-plan.json:18-19`、`:48`）⇒ W=200 ms 时在途 ≈ 500×0.2×(1−到达率) ≤ ~100 ≪ 4096，`windowOverflow == 0`。**但没有任何仿真证据**：`cd tests/WinForward.E2E.Tests && rg -n -i 'simulat' .` **零命中**（exit 1），没有任何丢包注入驱动的用例 | **是**：补 0/10/50% 仿真（假 socket 或纯 tracker 驱动），断言 `SentOk == Supplied` 且最后一个配速槽位确实发出 | **C2** |
| 3 | `#5` 溢出槽位下 `never` 不含从未发出的序列 | **已实现** | `Classify` 只遍历 `_sent.IsSet(sequence)` 的序号（`Client/UdpReliability.cs:352-357`），而窗口溢出槽**从不**调用 `MarkSent`（`LossArm.cs:171-175` `continue`）⇒ 不在 `_sent`；越界序号也**不置位**（`:203-207` 早退，`SequenceBitmap.TrySet` 在 `:15-19` 拒绝，位置未 set）；doc 明写（`:338-339`） | 回归测试（PRD 要求；已有身份恒等式测试 `UdpReliabilityTrackerTests.cs:12` 覆盖桶划分，但**没有**"溢出槽不进 never"的定向用例） | C1 |
| 4 | `#6` 11 份 plan 的 `{loss,mix}` 臂都声明 `lossWindowMs`；`metrics.window` 等于声明值 | **部分** | 前半**已实现且有测试**（`PlanFileTests.cs:39-50`）；机械复核（见下方命令 A）：11 份 plan、所有 `kind ∈ {loss,mix}` 的臂都声明，`base` 三处不声明属正常（`ControlArm.cs:125` 透传 `LossWindowMs = spec.LossWindowMs`）；发布值 `LossArm.cs:100 Window = windowMs`、`MixMetricsWriter.cs:113 Window = windowMs`。**后半只是硬编码常量断言**：`ContractShapeTests.cs:380-396` 断言 `200.0`，走的是形状工厂（`Shapes/LossShape.cs:15 WindowMs = 200`），**没有**"plan 声明 137 ⇒ `metrics.window == 137`"的用例 | **是**（小）：补一条"非默认 `lossWindowMs` 从 plan 流到 `metrics.window`"的用例 | C2（判据归 C2，测试可 C7） |
| 5 | `#7` 客户端阻塞时 `late`/`never` 反映阻塞 | **已实现** | 帧与 `_sendTicks` 都用**预定**时刻：`LossArm.cs:162 var intended = pacer.IntendedTicks(index)` → `:216 frame.Build(…, intended)` → `:178 MarkSent(index, intended)`；年龄从 `_sendTicks` 起算（`UdpReliability.cs:303`）⇒ 客户端阻塞直接抬高 `elapsedTicks` → `late`。MIX 同样（`MixUdpLoop.cs:45/51/121`） | 回归测试（**无**任何定向用例） | **C3** |
| 6 | `#8` LATLOAD 500 rps 的 `windowOverflow`；`measurement-caveat`；`README.md` 行 | **部分** | 计数接通（`LaneEngine.cs:294-306` → `LatencyPlan.cs:175 WindowOverflow += counts.DeferredQueued` → `LatencyMetricsWriter.cs:38`）；**分析器已有**该 caveat（`analyze.py:1812-1819`，`MEASUREMENT_CAVEAT` 常量在 `:285`）。**缺**：①harness/分析器都没有"该臂延迟格子 `n/a (windowOverflow > 0)`"；②`README.md` **未**同步——D9 说的 `README.md:550` 今天是 `:421`（inFlightCeilingMs 行："a reached ceiling is a disclosure … not a failure"）与 `:605`（Verification 行），措辞仍是 disclosure | **是**：`n/a (windowOverflow > 0)` 的渲染 + README 改动（归属见 §4-②）。"500 rps 下 `== 0`"是**运行时观测**，只能在 E3 的判据轮里实测 | **C4** |
| 7 | `#9` TCP 直方图样本数（臂末尾在途请求被采样） | **已实现** | 顺序写死：offer 循环 → `GraceDrainAsync` → 取消并 join 接收 → 末次 `Settle`（`Client/Lanes/LaneEngine.cs:88-104`）；drain 主体 `:271-289`（三种出口：`BookEmpty` / 接收循环自己结束 / 上界）；上界由臂注入 `Clock.FromSeconds(1)`（`LatencyArm.cs:105`、`LatencyPlan.cs:26`）。**有 4 条引擎级测试**（`LaneEngineTeardownTests.cs:36,71,91,112`） | 只有 AC 措辞的那一半：**没有**"直方图 count 因 drain 而上升"的臂级/策略级用例 | C4（补齐用例） |
| 8 | `#10` `(1,3,2)` 到达顺序计为 1 次重排 | **已实现** | 按 `_highestArrived` 比较（`Client/UdpReliability.cs:292-301`），只在**已发送**的序号上计（`:287-290`）；定向用例在但**不是** `(1,3,2)`：`UdpReliabilityTrackerTests.cs:98-118` 用 `3,1,3`；`(1,3,2)` 走同一分支。**PRD 要求的 1..6 穷举（1956 种）不存在**：`rg -n -i 'permut\|exhaust\|1956' tests/` 零命中 | **是**：穷举 1..6 的到达顺序（含 `(1,3,2) == 1`） | **C5** |
| 9 | `#11` 三个恒零计数器 | **部分** | 三者为 `unmatchedReplies`、`abandonedAtTeardown`、`corruptRate`（审计 §11 原文）。LOSS 侧**都已接通**：`LossArm.cs:301 tracker.MarkUnmatchedReply()`、`:99 AbandonedAtTeardown = counts.Undetermined`、`:104 CorruptRate = JsonRate.Rate(counts.Corrupt, tracker.SentOk)`。**真缺口确认仍在**：分析器不消费 `undecodable`——`rg -c 'undecodable' analyze.py` = **0**（target 侧已写：`Target/UdpEchoServer.cs:146-148,63,96`） | **是**：分析器侧披露 `undecodable`（去程损坏的唯一目击者）+ 前两条的回归测试 | **C7**（分析器消费面与 E4 协同） |
| 10 | `#12` 四臂 `clientSendLoss` 由各自计数器派生、每个 gate 能真的失败 | **未做** | 四臂今天仍写字面量（命令 B，精确命中 4 处）：`IdleArm.cs:29 = 0L`、`ReliabilityArm.cs:65 = 0`、`ThroughputArm.cs:122 = 0L`、`DnsArm.cs:62 = 0L`。**参照点已从 D0 的 `LossArm.cs:43` 迁到 `LossArm.cs:37`**；已派生的实际是**五个**：`LossArm.cs:37`、`LatencyMetricsWriter.cs:37`、`MixMetricsWriter.cs:48/82`、`ControlArm.cs:81`、`PersistentArm.cs:71`。可失败性逐臂：**DNS 与 THRU 的"不能丢样本"论证不成立**——`DnsArm.cs:83 unsent`+`:99 socketErrors`、`ThroughputArm.cs:248 Interlocked.Increment(ref state._sendFailures)`（另 `:215 _connectFailures`）都是客户端毁掉的样本；**IDLE 结构上无样本可丢**；**REL 以背压代替丢弃**（`ReliabilityArm.cs:101-103`），无丢弃计数。分析器**只读** LOSS/LAT/LATLOAD/BASE 的该 gate（`analyze.py:3276-3285`） | **是**：四臂逐条裁定 + 派生（DNS/THRU 真能失败；IDLE/REL 需明确"结构恒零"并留依据），README `:414` 的四臂理由要同步（归属见 §4-②） | **C7** |
| 11 | `#13` 全部比率调用点零发送返回 `null` | **已实现** | `JsonRate.Rate` 的 `denominator == 0 ? null`（`Contracts/Json/Rate.cs:18-19`）、`JsonPerSecond.PerSecond` 的 `ticks <= 0 ? null`（`Json/PerSecond.cs:18-19`）；全部比率调用点都走它们（`LossArm.cs:101-107,109`；`LatencyMetricsWriter.cs:169,170,23`；`DnsArm.cs:103,104`；`MixMetricsWriter.cs:81,126`；`PersistentArm.cs:117,118`；`ReliabilityMetricsWriter.cs:45,64`）；`JsonRateTests.cs:11-24,44-50` 覆盖 `(0,0)`/`(1,0)`/`(-1,0)` 与 `ticks<=0` | 无（登记 `index.jsonl` 即可） | — |
| 12 | `#14` 产品重启的负 CPU 与采样记录的 PID/`StartTime` | **已实现（两侧）** | 记录侧：`ProcessSample.cs:12-23 SampledProcess(Id, StartedUtc, …)`，写入 `ResourceSampleWriter.cs:70`（`pid`）/`:73`（`startUtc`，不可读时 `:77` 写 `null`），采集 `ProcessCounterSource.cs:196-202`、`ResourceSampler.cs:122,125`。**D0 说"缺逐样本身份校验 + 非单调拒收"已被证伪**：分析器已有，`analyze.py:1178-1190`（身份 = `(pid, startUtc)`）、`:1193-1256`（`negative_delta` 计数并**跳过**该身份 `:1249-1251`、`restarts` `:1235-1236`、无可用身份则返回 `None` `:1254-1255`）。审计说的"会把 `-12.34 %` 照常渲染"**今天不成立** | 只有"仿真一次重启"的定向证据缺口 | **C6** |
| 13 | `#15` 乱序/错配的 DNS TCP 响应记为 `answered` | **已实现** | 队列元素携带 id：`DnsTcpPhase.cs:26 PendingQuery(ushort Id, long Intended)`，入队 `:117`，出队 `:161`，**按 id 比对** `:171-176`（mismatch → `_unmatched++` + `_other++`，**不**走 `DnsArm.Classify`）⇒ 不计 `answered`；类文档 `:10-15`。D0 的 `DnsArm.cs:46,504-509` 已失效（DnsArm 今天 157 行） | 回归测试（**无**定向用例） | **C6** |
| 14 | `#16` BASE 参数来自 plan；每轮前后各一次；下限与 LOSS 并列发布 | **已实现** | 参数来自 plan：`ControlArm.cs:26-33` + `:104-126`（`LossPhaseSpec:125` 透传 `LossWindowMs`）；每轮前后各一次：`scripts/orchestrator.ps1:374-383 Invoke-ControlBlock`，`:448`（pre）/`:454`（post），计划用 `base-plan.json`（`:382-383`）；下限并列发布：`analyze.py:3449-3450` 的 3.2 表把 `BASE lossRate pre/post` 与 `LOSS/LAT/LATLOAD clientSendLoss` 放在**同一张表**，判定在 `:3355-3362` | 无（如需更醒目可另议，见 §4-⑦） | — |
| 15 | `#17`/`#18`/`#19` 分析器侧 | **#17 已实现 / #18 已实现 / #19 未做** | **今天有分析器**：`benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py`，6053 行，**git 已跟踪**（`git ls-files --error-unmatch …/analyze.py` 命中；同目录 `README.md`、`synthetic/make_tree.py`、`verification/*` 也在树里）。#17：`analyze.py:241 NOT_CARRIED_CELL`、`:224 UDP_INCAPABLE_ROWS`、`:1626-1627/1656/1949-1955/2322/3087/3522/3668` 的格子置空与排除。#18：`UDP53_LABEL :226`、`:1629`、`:1870-1878`（DNSALT 不可比提示）、`:2595/2666`。#19：`rg -c 'user-mode\|kernel\|DPC\|ISR\|nonpaged' analyze.py` = **0**（命令 F） | **#19 是实质工作**；#17/#18 只差 D0.2 说的"可 grep 回归断言"——`benchmarks/WinForward.E2E/scripts/check-fairness.py` **不存在**（`ls scripts/ \| rg -i 'fairness\|check'` 无输出） | #19 → **C7/C8 之外**，按 D0.2 与 E4 协同；#17/#18 断言 → E3 收尾 |
| 16 | D7 记账口径：越界槽在 `sent`/`supplied`/`clientSendLoss` 的归属 | **部分** | 止血半已落地（E1）：`MarkSent` 守卫在数组写入**之前**早退（`Client/UdpReliability.cs:203-207`），`SentOk`/`Outstanding`/`_highestSent` 都不动，`_sent.TrySet` 只把它记进 `OutOfRange`；测试 `UdpReliabilityTrackerTests.cs:142-156`（断言 `OutOfRange==1`、`SentOk==0`、`Outstanding==0`、`Supplied==0`、分类总和 0）。**`SentOutOfRange` 不存在**（`rg -n 'SentOutOfRange' benchmarks/` 零命中），发布面仍是合并的 `OutOfRange`（`:179`：`_sent + _arrived + _corruptAt`），`LossArm.cs:37` 的 `clientSendLoss = SendFailure + WindowOverflow + Undetermined` **不含**越界项 ⇒ D2 的公式未落地 | **是**：新增发送侧计数（与接收侧 `OutOfRange` 分开命名/发布）、`clientSendLoss` 公式、`LossArm.cs:54` 附近的 Note、`README.md:371/:584-588` 的说明 | **C5** |
| 17 | `FrameStreamReader.Truncated`；EOF 半帧读成什么；两台 server 是否写 trailer；环境键 | **未做** | `FrameReadStatus` 只有 5 个成员（`Wire/FrameStreamReader.cs:5-12`，**无 `Truncated`**）；EOF 落帧中间 → `:77-80` 返回 `EndOfStream`（`FillAsync` 在 `:109-113` 置 `_endOfStream`）；行为被**冻结**在测试里：`FrameStreamReaderTests.cs:58-67`（注释逐字写着 "E3 D9: 将改为 Truncated —— 冻结当前行为"）。喂入接缝（D14.18）**已在**（`:30` internal 委托 ctor，测试 `:17` 已用）。**写 trailer 的只有 TCP 一台**：`Target/TcpConnectionProtocol.cs:162,175-186`（`SendTrailerAsync`，3 帧 × 256 B，`Wire/TrailerProtocol.cs:14,17,20`）；`DnsServer` 不写（`rg -c 'Trailer' Target/DnsServer.cs` = 0）。**账本/环境键都不存在**：`ArmKeys.Ledger.cs` 无 `truncatedFrames`，`targetSummary` 无该字段（见 #18），`environment.json` 由 `orchestrator.ps1:415-433` 写出且无截断键 | **是**（单独成阶段）：`FrameReadStatus.Truncated` + 两个 server 各自记账（`tcpSummary.truncatedFrames`/`dnsSummary.truncatedFrames`）+ 分析器 caveat + `make_tree.py` 用例 + 拆三段/EOF 落中间的测试 | **C8** |
| 18 | 账本 `detail`/`acceptErrors`/`udpReceivers` 三字段与 `--udp-receivers` | **未做** | 全部零命中：`rg -n 'udp-receivers\|udpReceivers\|UdpReceivers' benchmarks/WinForward.E2E`（含 `Cli/`、`Target/`）= 0；`rg -n 'acceptErrors\|receiverExits' …` = 0；`ArmKeys.Ledger.cs` 只有 4 个记录族（`tcp`/`udpSummary`/`dnsSummary`/`targetSummary`，`TargetRunner.cs:66-116`）与 4 组 totals，**没有** `error` 记录类型，唯一同名常量 `ArmKeys.Ledger.cs:358 Error` 是 **verdict 名**（`TcpVerdict.Error` → `"error"`，`TcpCommand.cs:46`），不是异常记录。客户端侧 `detail` 有（`ArmRecordWriter.cs:66,76`，D14.12），属 `error` 记录不是账本。接收者数仍是硬编码 `Math.Clamp(Environment.ProcessorCount / 2, 2, 8)`（`TargetRunner.cs:21`），CLI 白名单无该选项（`Cli/TargetOptions.cs:27-42`） | **是**：账本三字段 + `--udp-receivers`（默认口径不变）+ 分析器披露 | **C8** |
| 19 | `ObjectDisposedException` 的四种策略与 `achievedRate` 的两种口径 | **未做（两项都未统一）** | **ODE 是 5 种形态、31 个 catch（分布在 19 个文件）**（`rg -c 'catch \(ObjectDisposedException' benchmarks/WinForward.E2E --glob '!obj/**' --glob '!bin/**'` 求和 = 31）：①**纯吞掉**（只有注释）**19 处**：`Program.cs:148`、`TargetLog.cs:20`、`UdpEchoServer.cs:185`、`FrameBuffer.cs:83`、`ResourceSampleWriter.cs:30`、`LaneEngine.cs:137,265`、`DnsTcpPhase.cs:71,222`、`ThroughputArm.cs:254,301`、`MixPageLoop.cs:162`、`DnsUdpPhase.cs:55,173`、`LossArm.cs:189,252`、`PersistentArm.cs:185`、`MixUdpLoop.cs:64,162`；②**计数成数据点 3 处**：`MixBulkLoop.cs:73-75 _bulkErrors++`、`MixPageLoop.cs:119-121 _pageErrors++`、`DnsServer.cs:342-344 _tcpAborted++`；③**产出 verdict/outcome 2 处**：`TcpConnectionProtocol.cs:88-90` 返回 `CommandOutcome(TcpMode.Clean, 0, modeKnown:false, TcpVerdict.Error, …)`、`ReliabilityExchange.cs:114-116 attempt.OtherError = true`；④**静默 `return`/`return false`/`break`/`return "unknown"` 6 处**：`DnsServer.cs:212-214,260-262`、`UdpEchoServer.cs:129-131,160-162`、`TcpAcceptLoop.cs:41-43`、`TcpTargetServer.cs:127-129`；⑤**当连接失败返回结果 1 处**：`FrameBuffer.cs:67-69` → 调用方 `LossArm.cs:147 MarkSendFailure()`。**achievedRate 仍是两种口径**：只有 `PersistentArm.cs:118 AchievedRate = PerSecond(state._responses, …)` 用"完成"分子，其余 5 处全用 `SentOk`/`sent`/`attempts.Length`（`LossArm.cs:109`、`LatencyMetricsWriter.cs:139,170`、`DnsArm.cs:104`、`ReliabilityMetricsWriter.cs:64`）；`completionRate` **不存在**（`rg -n 'completionRate' benchmarks/` 零命中；`ArmKeys.Persistent.cs` 只有 `:73 AchievedRate`） | **是**：逐条列出受影响的既有字段（D9 明确要求）、统一为"teardown 不产生数据点"；PERSIST 完成口径新增 `completionRate`、`achievedRate` 改成功发出 | **C8**（D9 归 C8 的"矛盾语义统一"） |
| 20 | `ReliabilityArm` 把取消记成 `ConnectFail`（记录但不修） | **未做（保持不修）** | **PRD 的 `ReliabilityArm.cs:602-606` 已失效**（该文件今天 169 行）。现行位置两处：①`SocketOps.TryConnectAsync` 把 `OperationCanceledException` 吞成 `Ok:false`（`Client/FrameBuffer.cs:63-66`）；②`ExchangeAsync` 只看 `Ok` 就置 `ConnectFail`（`ReliabilityExchange.cs:73-77`），再由 `:36-38` 落成 `Observed = ReliabilityOutcome.ConnectFail`。**可达性**：attempt token 带 10 s 上界（`ReliabilityExchange.cs:27 CancelAfter(10_000)`），一个黑洞 SYN 的产品会让连接悬停 10 s → 取消 → `ConnectFail`；进程级 token（`context.CancellationToken`，来自 `Program.cs:48-49` 的 `CancellationTokenSource` 与 `:57` 的 `CancelKeyPress`）同样经链接触发。**注意方向**：它把"超时"记成"连接失败"，与"取消"混同 | 否（PRD 明写"记录但不修"）——但登记文本必须换成现行行号 | 登记即可 |
| 21 | E1/E2 已完成、E3 只需复核的 13 项 | **全部已实现（1 项不适用）** | 逐条见下方「§1.1 复核清单」 | 否（只登记 `index.jsonl`） | — |

### 1.1 第 21 项的逐条复核（E1/E2 声称已完成）

| 项 | 结论 | 现在在哪（HEAD 行号） | 还需动作 |
|---|---|---|---|
| D1–D7 止血（Tier 0） | 已实现 | `UdpReliability.cs:15-19,203-207`（越界守卫）+ `Program.cs:95-100,122-128` 与 `ClientRunner.cs:159-173,181-237`（臂级失败边界）+ `PlanFile.cs:66-95`（数值域）；单测 `UdpReliabilityTrackerTests.cs:142-156`、`PlanFileValidationTests.cs:16-184`、`ClientRunnerTests.cs` | 否 |
| 顶层不兜异常 | 已实现 | 缺陷原文是"顶层**不兜**任何异常"（父 `prd.md:103`），修法是**加**顶层兜底：`Program.cs:95-100`（target）/`:122-128`（client）；臂内失败先经 `ClientRunner.RunGuardedAsync:159-173` 落 `error` 记录 + `run.json.failed`（`ArmRecordWriter.cs:60-80`） | 否 |
| `PlanFile` 数值校验 | 已实现 | 15 键表 `PlanFile.cs:74-90`（含 `payloadBytes ≤ FrameCodec.MaxPayloadLength`、`dnsPort ≤ 65535`）；三态 `IntReadOutcome`（`:69-73`）区分"非整数/超范围"；`ReadInt`/`ReadArmNumbers` 已删（`rg -n 'ReadArmNumbers\|static int ReadInt'` 零命中）；`TryReadInt` 的 int 范围判断见 `:69-73` 与 `PlanFileValidationTests.cs:168-175` | 否 |
| `selftest.sh` 参数 | 已实现 | 漏 plan → `exit 2` 且在任何启动之前（`scripts/selftest.sh:16-20`）；`ls /tmp/wf-bench/deploy/e2e/*.json` 已删（全文无 `ls `）；client 输出落 `$work/client.out`（`:49`） | 否 |
| `TcpCommand` 兜底 | 已实现 | 两个 `_ =>` 兜底换成名字表 + 未命中 `throw`（`Wire/TcpCommand.cs:24-64`，`Name(TcpMode)`/`Name(TcpVerdict)`）；测试 `TcpCommandTests.cs`（9 条） | 否 |
| `LedgerWriter` catch | 已实现（语义） | `Target/LedgerWriter.cs` **已删除**，合一为 `Contracts/Json/JsonlSink.cs`：`JsonlPolicy ∈ {Propagate, SwallowAndCount}`（`Json/JsonlPolicy.cs`），I/O 写入用 `CancellationToken.None`（`:116`，不留半行），close 永不抛（`CloseAsync`/`TryCloseStepAsync:252-262`），1 Hz flush（`FlushLoopAsync:216-235`，ctor 注入 `flushInterval`，`:38-47`）。**结构上 `body` 仍与 I/O 共用同一 try**（`:113-125`）——按 D14.7 的终裁（吞/抛按策略）语义等价 | 否（若坚持 D10 的字面"body 移出 try"，见 §4-⑩） |
| SO_REUSEPORT | 已实现 | `Target/Sockets.cs:47-58`（TCP：保留 `SO_REUSEADDR`，`ClearReusePort`）、`:65-79`（UDP：不设任何 reuse 选项，`ClearReusePort`）、`:115-130`（Linux-only raw option 15）；测试 `SocketsTests.cs`（5 条） | 否 |
| 端口冲突 | 已实现 | `Cli/TargetOptions.cs:70-78`（`dnsPort` 撞 `tcp/udp` → exit 2，消息点名两个选项）+ `:80-92`（`dnsAltPort` 撞三者）；测试 `TargetOptionsTests.cs` | 否 |
| `MaxPayloadLength` | 已实现 | `Wire/FrameCodec.cs:45 internal const uint MaxPayloadLength = 4u*1024u*1024u`（4 MiB，`internal`），`PlanFile.cs:81` 用作 `payloadBytes` 上界；测试 `PlanFileValidationTests.cs:122-135`（边界 4194304/4194305，断言字面量） | 否 |
| plan key 白名单 | 已实现 | 一张描述符表 `Client/Arms/ArmKind.cs:14-47`，`s_identityKeys`（`:14`）含 `name/kind/seconds`，文本键都在（`latency`/`base` 的 `protocol` `:23,:44`，`reliability` 的 `modeMix` `:36`）；11 份 plan 全通过 + 内置 plan 8 臂（`PlanFileTests.cs:9-34`） | 否 |
| UDP `ConnectAsync` 保护 | 已实现 | `Client/` 内 **13 个** `SocketOps.TryConnectAsync` 调用点**全部**走 helper（`rg -n 'ConnectAsync' Client/` 共 15 行：`FrameBuffer.cs:52` 是 helper 签名、`:56` 是 helper 内唯一的裸 `socket.ConnectAsync`，其余 13 处为 `ThroughputArm:213`、`DnsTcpPhase:39`、`UdpLaneTransport:60`、`PersistentExchange:215`、`TcpLaneTransport:53`、`ReliabilityExchange:73`、`LatencyArm:170`、`MixUdpLoop:93`、`LossArm:142`、`MixBulkLoop:21`、`DnsUdpPhase:29`、`MixPageLoop:73`、`MixPageLoop:133`） | 否 |
| `JsonValue.Write` 的 `default:` | **不适用（已由删除闭合）** | `Client/JsonValue.cs` 已删，删除提交 `925c695 feat(bench): finish the typed contract and retire the dictionary carriers`（`git log --diff-filter=D --oneline -- 'benchmarks/WinForward.E2E/Client/JsonValue.cs'`）；D14.23 的裁定就是"不做 `default: throw`，由删除闭合" | 否 |

---

## 2. 与审计原文的差异（哪些"缺陷"今天已不成立）

> 判据：能给 `file:line` 或命令，且该行今天确实如此。**审计原文（`harness-audit.md` §二/§三）与 D0 里的行号大多已失效，本节同时给出"原文说的落点 → 今天的落点"。**

| 审计条目 | 原文断言 | 今天的实测 | 证据 |
|---|---|---|---|
| §二 #4 | 窗口槽位永不释放，10%/50% 丢包下第 80/16 秒就停发 | **不成立**：`Retire(now, windowTicks)` 在窗口判定前按 `发送时刻 + W` 释放 | `UdpReliability.cs:313-331`；`LossArm.cs:169`（先）→ `:171`（后） |
| §二 #5 | `Classify` 按 `1..SentOk` 遍历，未发出的序号被发布为"从未到达" | **不成立**：只遍历 `_sent.IsSet` 的序号；溢出槽不进 `_sent` | `UdpReliability.cs:352-357`；`LossArm.cs:171-175` |
| §二 #6 | W 恒为 200 ms 下限，注释谎称 5×p99 | **不成立**：W 是 plan 的 `lossWindowMs`，无声明时用常量 200，且**注释已改成不变量陈述** | `LossArm.cs:24`、`UdpReliability.cs:438-446`；`LossArm.cs:54` 的 Note |
| §二 #7 | 从实际发送时刻起算，客户端阻塞被减掉 | **不成立**：帧与 `_sendTicks` 都用 `pacer.IntendedTicks` | `LossArm.cs:162,216,178` |
| §二 #8 | 修复建议是"窗口放大到 4096 + 把 overflow 变成作废 gate" | 窗口**已**放大（plan 声明 `window:4096`），但 D9 **推翻**了"作废 gate"：改为 caveat + 格子 `n/a` | `scripts/plans/full-plan.json:18-19`；`design-decisions.md:262`（D9 行） |
| §二 #9 | TCP 通道没有 drain，尾 cohort 被丢 | **不成立**：引擎的 offer→有界 grace→join→末次 settle 顺序，臂注入 1 s 上界 | `LaneEngine.cs:88-104,271-289`；`LatencyArm.cs:105`；`LatencyPlan.cs:26` |
| §二 #10 | `reordered` 是死代码，1956 种顺序恒为 0 | **不成立**：按 `_highestArrived` 计，`(3,1,3)` 用例已证明计 1 | `UdpReliability.cs:292-301`；`UdpReliabilityTrackerTests.cs:98-118` |
| §二 #11 | `unmatchedReplies` 在 LOSS 无调用点；`abandonedAtTeardown` 从不使用 | **LOSS 侧不成立**：两者都接通；**真缺口只剩**分析器不消费 `undecodable` | `LossArm.cs:301`、`:99`；`analyze.py` 中 `undecodable` 0 命中 |
| §二 #12 | 五个臂硬编码 `clientSendLoss = 0` | **部分不成立**：Mix 早已派生（D0 已认）；今天硬编码的是**四臂** | `rg -n 'ClientSendLoss\] = 0' Client/Arms/` → `IdleArm.cs:29`、`ReliabilityArm.cs:65`、`ThroughputArm.cs:122`、`DnsArm.cs:62` |
| §二 #13 | `JsonValue.Ratio` 在分母 0 时返回 0，"没测到"渲染成满分 | **不成立**：`JsonRate.Rate` 自首个提交即返回 `null`（D0 已认，本次复核同结论） | `Contracts/Json/Rate.cs:18-19`；`JsonRateTests.cs:11-24` |
| §二 #14 | 采样无 PID/`StartTime`；CPU 由首末样本之差算，重启会出负数；分析脚本只查 `t1 <= t0` 就照常渲染 | **不成立（两侧都不成立）**：记录有 `pid`/`startUtc`；分析器按 `(pid, startUtc)` 逐身份算差、**拒收负差**并计 `negative_delta`、返回 `None` | `ResourceSampleWriter.cs:70,73,77`；`processes[]` 采集 `ProcessCounterSource.cs:196-202`；`analyze.py:1188,1193-1256` |
| §二 #15 | DNS TCP 按 FIFO 匹配，id 校验是同义反复 | **不成立**：队列存 `(id, intended)`，出队按 id 比对，错配记 `other`+`unmatched` | `DnsTcpPhase.cs:26,117,161,171-176` |
| §二 #16 | BASE 跑两倍时长、只覆盖两臂、参数不来自 plan、只跑一次 | **部分不成立**：参数**来自 plan**、`control-pre`/`control-post` **每轮各一次**；"两倍时长"是**有意并已发布**（`parameters.seconds = 2×phase`，`ControlArm.cs:64`）；"只覆盖 LAT/LOSS"仍是事实（它就是两相控制组） | `ControlArm.cs:26-33,64,104-126`；`orchestrator.ps1:374-383,448,454` |
| §三 #17 | Proxifier 的 UDP 泄漏却按成绩发布 | **不成立**：`NOT_CARRIED_CELL` 置空 + 排除 + gate 豁免 | `analyze.py:224,241,1626-1627,1949-1955,3087,3522,3668` |
| §三 #18 | WinForward 的 DNS 走直连，`dns-rtt.p50` 不可比 | **不成立**：`UDP53_LABEL` + DNSALT 不可比提示 | `analyze.py:226,1629,1870-1878,2595,2666` |
| §三 #19 | CPU 只统计用户态，无内核/DPC/ISR 披露 | **成立，是本次唯一原样存活的公平性缺口** | `rg -c 'user-mode\|kernel\|DPC\|ISR\|nonpaged' analyze.py` = 0 |
| §四「账本没有任何代码读它、没有绝对时间戳」 | — | **D0.3 已证伪，本次确认**：`analyze.py` 有 `LedgerData`/`load_ledger`/`ledger_views`（`:689-690,857-935`），`TargetRunner.cs:53-58` 每条写 `utc`+`label` | `TargetRunner.cs:53-58`；`analyze.py:857-935` |
| §五「LAT lanes 竞态」 | 代码确是 bug（共享计数器非原子） | **不成立**：臂在全部 lane join 后求和，`LatencyArmTotalsTests` 钉住 | `LatencyArm.cs:36-44`；`Lanes/LatencyArmTotalsTests.cs:15-52` |

**行号迁移速查（施工者最容易踩的）**

| 文档里的旧引用 | HEAD 的现行位置 |
|---|---|
| `LossArm.cs:43`（clientSendLoss 参照点） | `Client/Arms/LossArm.cs:37` |
| `LossArm.cs:55`（Note） | `Client/Arms/LossArm.cs:54`（W 的 Note 在第 54 行起） |
| `IdleArm:20` / `DnsArm:92` / `ThroughputArm:119` / `ReliabilityArm:165` | `IdleArm.cs:29` / `DnsArm.cs:62` / `ThroughputArm.cs:122` / `ReliabilityArm.cs:65` |
| `ReliabilityArm.cs:602-606`（取消记 ConnectFail） | `ReliabilityExchange.cs:73-77` + `FrameBuffer.cs:63-66` |
| `DnsArm.cs:46,504-509`（PendingQuery） | `Client/Arms/DnsTcpPhase.cs:26,117,161-176` |
| `BaseArm.cs:29-37,53-57,96` | `Client/Arms/ControlArm.cs:26-33,104-126` |
| `ResourceSampler.cs:288-310,424,481` | `ResourceSampler.cs:115-146`、`ProcessCounterSource.cs:196-202`、`ResourceSampleWriter.cs:63-102` |
| `LatencyArm.cs:435` / `:668`（两个 drain） | `Client/Lanes/LaneEngine.cs:271-289`（合一），臂侧 `LatencyArm.cs:105` |
| `README.md:550`（windowOverflow 是 disclosure） | `README.md:421`（`:416` 是 windowOverflow 行本身）；Verification 侧 `README.md:605` |
| `README.md:363`（越界槽记账说明） | `README.md:371`（LOSS 契约键）与 `:584-588`（序列空间与 `outOfRangeSequences`） |
| `ClientRunner.cs:445`（`--plan` 缺省） | `Cli/ClientOptions.cs:107-115`（空串 → exit 2）；`PlanFile.cs:186-190`（`path is null` → 内置 plan） |
| `analyze.py:3357,3531` | 仍在（`BASE floor` 判定 `:3355-3362`；`:3531` 附近为门禁文本），**但**"下限与 LOSS 并列"的实际表头在 `:3449-3450` |

---

## 3. 真实剩余工作的最小清单

分组顺序 = 依赖顺序（数据/接缝前置 → 数值语义 → 新 verdict → 账本/CLI → 分析器侧）。每组给出**改哪里**、**机械判据**、**与 D18.x 的冲突面**。

### 组 A：数据/接缝前置（必须最先）

**A1. `UdpReliabilityTracker` 的并发契约（条目 1）**
- 改哪里：`Client/UdpReliability.cs`（类文档 + 数据结构）；若选 D4 的投递队列形状，另改 `Client/Arms/MixUdpLoop.cs:34,137-166`（接收循环只入队）与 `:51-54`（发送线程在配速槽位前结算）。
- 机械判据：①`Client/UdpReliability.cs` 的**类文档里有一段逐字写明"哪个线程写哪些字段"**（今天 `:119-124` 完全没有线程字样；机制无关，选 D4 或全 `Interlocked` 都要写）；②并发测试 N 轮（建议 ≥4×1024）后恒等式 `arrived+late+never+undetermined+corruptDatagrams == sent` 与 `Supplied == SentOk + WindowOverflow + SendFailure` 同时成立，且断言**接收线程从不写 book**（用一个只有发送线程能通过的"写者令牌"或 `Environment.CurrentManagedThreadId` 比对）。
- **D18.x 冲突面（关键）**：`D18.5 #11` 明写"E2 只让 `UdpLatencyState` 单写者；`UdpReliabilityTracker` 的多写者问题仍归 E3"——**不得**为了修 MIX 去改 `LaneEngine` 或 `ILanePolicy` 的接口形状（父 `implement.md:206-210` 的"E3 还会再改一次接缝"已被 `D8` 作废）。修法必须落在 `MixUdpLoop` + tracker 内部。
- 另：`design-decisions.md:68-69`（D0.3）明令 **E3 不得顺手改 `UdpReliabilityTracker` 的记账与 `LossArm` 的序号约定**——并发修法只能是"谁写"的搬迁，不能改公式。

**A2. `FrameReadStatus.Truncated` 的读侧接入点（条目 17，单独成阶段）**
- 改哪里：`Wire/FrameStreamReader.cs:5-12`（枚举）+ `:77-80`（EOF 分支区分"帧边界"与"帧中间"）；两个消费者 `Target/TcpConnectionProtocol.cs`（`ReceivePhaseAsync` 的 status 阶梯）与 `Target/DnsServer.cs`（长度前缀流）；计数字段 `ArmKeys.Ledger.cs` 的 `tcpSummary`/`dnsSummary` 两组（各一层）。
- 机械判据：①`FrameReadStatus.Truncated` 存在且 `EndOfStream` 只在**帧边界**返回；②`FrameStreamReaderTests.cs:58-67` 的冻结用例必须**变红**（这就是"改动可见"的证明），改为断言 `Truncated`；③新增"一帧拆三段 + EOF 落中间 ⇒ `Truncated`"与"帧边界 EOF ⇒ `EndOfStream`"两条；④`selftest.sh` 无误报（`truncatedFrames == 0`）。
- **D18.x 冲突面**：D18.6 #3 已把 TCP 接收的终结性映射写死（`EndOfStream` 终止；`BadMagic`/`BadLength` → `IoError`；`BadChecksum` → `Malformed` 继续）。`Truncated` 必须是**第 4 种非终止/终止状态**，不能改这三条的语义——`LaneTransportTests.cs:52-69` 与 `LatencyPolicyTests.cs:133` 钉住了它们。

### 组 B：数值语义（条目 2、3、5、8、16）

**B1. D7 记账半 + `SentOutOfRange`（条目 16）**
- 改哪里：`Client/UdpReliability.cs`（新增发送侧计数，`MarkSent` 守卫分支 +1；与 `:179 OutOfRange` 分开发布）；`Client/Arms/LossArm.cs:37`（公式）、`:99-108`（发布）、`:54` 附近（Note）；`Client/Arms/MixMetricsWriter.cs:48`（MIX 的同类项，若一并统一）。
- 机械判据：①`MarkSent(MaxSequence+1, 0)` 后 `SentOutOfRange == 1` 且**接收侧** `OutOfRange == 0`（两者可分）；②`clientSendLoss == SendFailures + WindowOverflow + Undetermined + SentOutOfRange`（一条断言，或把它写成单一表达式 + 守卫测试）；③`gates.clientSendLoss` 的语义与 `metrics.clientSendLoss` 一致（`LossArm.cs:50` vs `:95`）。
- **D18.x 冲突面**：D2 明写"E1 的守卫**必须仍然**调用 `_sent.TrySet(sequence)`（由它维护 `OutOfRange`）"——新增计数时**不能**把 `TrySet` 的副作用删掉，否则 `OutOfRange` 的接收侧含义（`:173-179` 的文档）被破坏。

**B2. `#4`/`#5`/`#7`/`#10` 的定向证据（条目 2、3、5、8）**
- 改哪里：只加 `tests/WinForward.E2E.Tests/`（`UdpReliabilityTrackerTests.cs` 或新文件），**不改生产代码**（机制已成立）。
- 机械判据（逐条，必须能对当前代码反向失败）：
  - `#4`：假发送驱动 0/10/50% 丢包，断言 `SentOk == Supplied`、最后一个配速槽位仍发出、`WindowOverflow == 0`；**反证**：把 `Retire` 调用从 `LossArm.cs:169` 删掉，该用例必须红。
  - `#5`：制造溢出槽（`Supplied` 涨而 `MarkSent` 不调）后断言 `Never == 0`；反证：把 `Classify` 的 `if (!_sent.IsSet(...)) continue;` 删掉即红。
  - `#7`：`MarkSent(1, intended)` 后再模拟"客户端阻塞"（`now` 远大于 `intended`），断言该包落 `Late`；反证：把 `_sendTicks` 改成实际时刻即红。
  - `#10`：穷举 1..6 的全部到达顺序，断言 `(1,3,2)` 的 `Reordered == 1`、恒等式不破；反证：把 `_highestArrived` 换成 `_nextExpected` 即红。
- **D18.x 冲突面**：`D11` 的"每条定向测试必须能对当前代码失败（反证目标写在注释里）"。以上四条都是**回归护栏**（今天必绿），注释里要写明这句话，否则触发 D11 的同义反复判据。

**B3. `#6` 的补强（条目 4）**
- 改哪里：`tests/WinForward.E2E.Tests/ContractShapeTests.cs:380-396` 附近，或 `PlanFileTests.cs`。
- 机械判据：一条 plan → arm → `metrics.window` 的端到端（或用一个 `lossWindowMs: 137` 的计划 fixture 走 `LossArm` 的 metrics 构造）。反证：把 `LossArm.cs:24` 的 `spec.LossWindowMs > 0 ? … : Default` 改成恒用默认即红。

### 组 C：新 verdict / 呈现（条目 6）

**C1. `#8` 的呈现与 gate 语义（条目 6）**
- 改哪里：`Client/Arms/LatencyMetricsWriter.cs:35-47`（gate 值不变，但需要把"该臂延迟读数作废"表达出来）与 `:73-85`（Note）；`benchmarks/WinForward.E2E/README.md:416,421,605`（**归属冲突见 §4-②**）；分析器的延迟表格渲染（`analyze.py` §5/§8 的格子）加 `n/a (windowOverflow > 0)`。
- 机械判据：①仓内 `rg -n 'n/a \(windowOverflow > 0\)'` 命中**生产渲染代码**（不是测试字面量）；②`windowOverflow > 0` 的记录，其 `latency/*` 格子输出该串；③`windowOverflow == 0` 的记录**逐字节不变**（用 E2 的 `compare-records.py` 跑同一 pair）。
- **不冲突的前提**：D9 明确"**不改 gate 为'作废该臂'**"，所以 `gates.scheduleTruncated`/`gates.laneShortfall` 的判定不受影响；`LaneCounts.DeferredQueued → gates.windowOverflow` 的接线（`LatencyPlan.cs:175`，D18.5 #7）**不得**改。

### 组 D：账本 / CLI（条目 18）

**D1. 账本三字段 + `--udp-receivers`**
- 改哪里：`Contracts/ArmKeys.Ledger.cs`（`targetSummary` 加 `acceptErrors`/`udpReceivers`；`tcpSummary`/`dnsSummary` 加 `truncatedFrames`；新增 `error` 记录族的 `detail`）；`Target/TcpAcceptLoop.cs`（accept 错误计数点）、`Target/UdpEchoServer.cs`/`DnsServer.cs`（receive loop 退出计数点）；`Cli/TargetOptions.cs:27-42` + `Target/TargetRunner.cs:21`（`--udp-receivers`，默认 `Clamp(ProcessorCount/2, 2, 8)` 不变）。
- 机械判据：①`LedgerShapeTests`（`tests/WinForward.E2E.Tests/LedgerShapeTests.cs`，E2-c 建）的**声明↔写出双向比对**在加键后仍绿，且新键有 factory 覆盖（`D5` 的"新增属性会让工厂编译失败"是同一手法）；②`--udp-receivers 3` 后 `targetSummary/udpReceivers == 3`；③`error` 记录的 `detail == GetBaseException().GetType().Name`（与 `ArmRecordWriter.cs:66` 同式）。
- **D18.x 冲突面**：D14.7 第 4 条"`targetSummary.ledgerWriteErrors` 键名与语义不变（账本是跨二进制契约，**只增不改名**）"——三字段只能**增**，且必须走 `JsonlSink`（不得复活 `LedgerWriter`）。另 D9 明写"`acceptErrors`/`receiverExits` 的**计数点**落在 E2 抽出的 accept/receive loop，E3 只负责写账本 + 分析器披露"。

**D2. ODE 与 `achievedRate` 统一（条目 19）**
- 改哪里：31 个 `catch (ObjectDisposedException)`（分类清单见 §1 第 19 行；②③两类的 5 处是真正要动的）；`Client/Arms/PersistentArm.cs:118` + `Contracts/ArmKeys.Persistent.cs:73`（新增 `completionRate`，`achievedRate` 改成功发出）。
- 机械判据：①一条源扫描 gate（形如 `JsonKeyLiteralGateTests` 的 `[CallerFilePath]` 手法）断言 `catch (ObjectDisposedException)` 的**净计数**等于登记清单的长度，且清单里没有任何一处对计数器/verdict 写入；②`rg -n 'completionRate' benchmarks/WinForward.E2E.Contracts` 命中且 `PersistentMetrics` 有 `required` 属性 + `PersistentShape` 工厂已更新（编译期覆盖）；③一条断言 `PersistentMetrics.AchievedRate` 的分子是"成功发出的请求数"的用例。
- **D18.x 冲突面**：D9 要求"实施时**逐条列出**受影响的既有字段（哪些计数会消失/改名）"——`DnsServer.cs:342-345 _tcpAborted`、`MixBulkLoop.cs:75 _bulkErrors`、`MixPageLoop.cs:121 _pageErrors` 会从那三个计数里消失，这三条必须写进 check 报告（它们是**已发布**的 `metrics.*`/ledger 字段，属"改变数值"）。

### 组 E：分析器侧（条目 9、15、14 的仿真）

**E1. `undecodable` 消费 + 恒零计数器披露（条目 9）**
- 改哪里：`analyze.py` 的 target-ledger 消费面（`load_ledger:890`、`ledger_views:2119` 附近）加 `udpSummary.undecodable` 的披露/表格列；#11 的三者各一条定向测试。
- 机械判据：`rg -n 'undecodable' analyze.py` **非零命中**，且命中处在一张表格/一条 finding 里（不是常量声明）。
- **D18.x 冲突面**：D9 明写"账本归属：**不补** arm/row/product 归属；`undecodable` 只做'target 侧总量'级披露；逐包归属登记为后续任务"——分析器**不得**把 `undecodable` 摊到某个臂。

**E2. `#19` 仅用户态披露 + `#17`/`#18` 的可 grep 断言（条目 15）**
- 改哪里：`analyze.py` 的 CPU 表（§6）与脚注；新增 `benchmarks/WinForward.E2E/scripts/check-fairness.py`（D0.2 指定的可 grep 断言载体）。
- 机械判据：①`rg -n 'user-mode\|仅用户态\|kernel/DPC/ISR' analyze.py` 命中且字符串出现在 CPU 表的表头/脚注文本里；②`check-fairness.py` 断言 `not carried (UDP bypassed)` 出现在 UDP 表且该行无数字格（exit 0/1 可判）。
- **D18.x 冲突面**：无直接冲突；但 D12 的文件归属把 `benchmarks/WinForward.E2E.Analysis/**` 划给 E4、`scripts/oracle-diff.py`/`analyze.sh` 划给 E4——**`check-fairness.py` 是新文件，需要主会话确认它归 E3 还是 E4**（见 §4-⑤）。

**E3. `#14` 的重启仿真（条目 12）**
- 改哪里：`tests/`（纯逻辑，喂两份 `processes[]`：同一 `pid` 不同 `startUtc`，第二份 `cpuSeconds` 更低）。
- 机械判据：`analyze.py` 的 `cpu_detail` 已有 `negative_delta`；E3 的判据是**harness 侧**确认 `pid`+`startUtc` 逐样本写出（`SampleRecordShapeTests.cs` 已有三态用例，加一条"同名多进程 ⇒ `processes[]` 长度 ≥2 且 `pid` 互异"）。

---

## 4. 歧义与风险（需要主会话裁定；尽量穷尽，附建议默认）

| # | 歧义/风险点 | 施工者会怎么猜 | 建议默认裁定 |
|---|---|---|---|
| ① | **并发契约选哪种形状**：D4 的"发送线程独占 + 接收投递队列"需要改 `MixUdpLoop` 的接收循环（`MixUdpLoop.cs:137-166`，那里现在直接写 tracker）；全 `Interlocked` 则改 tracker 的每个计数器（`UdpReliability.cs:143-187`），把 `long` 换成 `Interlocked` 语义，`_sendTicks`/`_arrivalMilliseconds` 数组仍是竞态源（两个线程写**不同**下标是安全的，但 `MarkArrival` 读 `_sent.IsSet` 与 `MarkSent` 写 `_sent` 之间有窗口） | 建议**保持 D4 的投递队列**并把它扩到 MIX：接收线程只 `Book(verdict)` 入队，发送线程在配速槽位前 `Settle`；理由是它与 `LaneEngine` 已建立的 `Settle` 接缝同形（`codebase-design`：一个真接缝、两个适配器），而全 `Interlocked` 会让 `Outstanding` 的"至多一次"语义失去单点。**若选后者，必须在类文档里写明 `Outstanding` 是近似值**，并接受 `#4` 的恒等式从"精确"降为"±竞态窗口" |
| ② | **README 归属**：D2 说"同步改 `LossArm.cs:55` 的 Note 与 `README.md:363/550`（**E5 负责 README**，实现者负责 Note）"，但 `D12` 同时把 `README.md` 的 `:122-142/:168-175/:238/:531-535` 划给 E1、其余给 E5 | 施工者会直接改 README（因为 AC 明写"同步改 `README.md:550`"） | 建议**沿用 D2 的裁定**：E3 只改代码侧 Note，README 的 `:414/:416/:421/:605/:371/:584-588` 由 E5 统一改；但 E3 必须在 check 报告里**逐行列出**需要 E5 改的措辞（否则 E5 没有输入）。若主会话认为 E3 必须闭环，则把 README 的这 6 行作为 E3 的最后一批显式登记 |
| ③ | **`IdleArm`/`ReliabilityArm` 的 gate 是否允许"结构恒零"** | 施工者会为了"每个 gate 都能失败"给 IDLE 造一个假计数器，或给 REL 把背压次数当损失 | 建议：**IDLE = 不适用**（无样本可丢，保留 `0` 但在 `Notes` 里说明"结构恒零"）；**REL = 允许恒零但必须留可失败路径的证据**——`scheduledAttempts == connectAttempts` 恒等式（`analyze.py:3335-3340` 的 `scheduled-attempts` 身份检查）已经是它的 gate；若主会话要求 gate 也能失败，最小改法是 `clientSendLoss = 0` 改成"被背压落下的配速槽位数"（需新增一个计数器，`ReliabilityArm.cs:101-103` 的 `slots.WaitAsync` 处） |
| ④ | **`DnsArm` 的 gate 分子**：`unsent`（`:83`，窗口满跳过的配速槽）与 `socketErrors`（`:99`）二者都算"客户端毁样本"，但前者从未进 `sent`（`DnsArm.cs:69` 的 Note 明说 `offered = sent + unsent`），后者是 socket 级 | 施工者会直接把 `unsent + socketErrors` 相加 | 建议：**用 `unsent` 而不是二者之和**——`socketErrors` 与 `sent` 是不同总体（Note `:69` 已声明"belongs to no single query"），相加会让 `clientSendLossRate = clientSendLoss/supplied` 的分母口径失真。请主会话钉死，并在 Note 里同步 |
| ⑤ | **`check-fairness.py` 归 E3 还是 E4**（D0.2 说 E3 交付"可 grep 回归断言"，D12 把 `scripts/**` 的分析器侧划给 E4） | 施工者会放到 `benchmarks/WinForward.E2E/scripts/`（E1 建的目录） | 建议归 **E3**（它断言的是**记录/表格**的公平性标注存在性，不依赖分析器重写；且 D0.2 就是这么派的），路径 `benchmarks/WinForward.E2E/scripts/check-fairness.py`；若主会话要 E4 收口，E3 至少留下断言文本 |
| ⑥ | **MIX 的 `clientSendLoss` 是否要一起改**：`MixMetricsWriter.cs:48` 用 `SendFailure + WindowOverflow + Undetermined`，与 `LossArm.cs:37` 同式，**都不含**越界项 | 施工者可能只改 LOSS（D2 只点名 `LossArm`） | 建议**一起改**（D2 的理由"与 LatencyArm 的 `_supplied - _sentOk` 口径对齐"对 MIX 同样成立；MIX 已发布 `classes.udp.outOfRangeSequences`（`MixMetricsWriter.cs:123` 写出，键在 `ArmKeys.Mix.cs:202`；LOSS 的键在 `ArmKeys.Loss.cs:103`）。若只改 LOSS，两个臂的 `clientSendLoss` 会分叉成两种口径，属新增不一致 |
| ⑦ | **"BASE 下限与 LOSS 数字并列发布"是否算达标**：今天在 §3.2 validity 表（`analyze.py:3449-3450`）里并列，但 §8「UDP accuracy detail」表里**没有** BASE 行 | 施工者会认为已达标（D0.1 也这么认为） | 建议**判为达标**并在 `index.jsonl` 里写明"并列位置 = §3.2 measurement-validity 表"。若主会话要求进 §8，需新增一行 BASE 并把 `metrics/loss/*` 与 `classes.udp/*` 的路径差异显式处理（E4 的 `RowProfile` 更合适，登记给 E4） |
| ⑧ | **`achievedRate` 统一的数值影响面**：PERSIST 改成"成功发出"后 `metrics/achievedRate` 会变（现在分子是 `_responses`），而 README 的契约表把 `achievedRate` 写在 PERSIST 的读键里 | 施工者会顺手改 README（冲突见 ②） | 建议：同批新增 `completionRate` 时**保留旧语义可读**——即 `completionRate = responses/elapsed`（新键），`achievedRate = requests 中成功发出的/elapsed`。必须显式登记"这是**改变数值**的改动"，并在 `research/semantic-fixes/index.jsonl` 里带 commit 引用 |
| ⑨ | **`ObjectDisposedException` 统一的净影响未知**：三处"计数成数据点"会消失（`_tcpAborted`/`_bulkErrors`/`_pageErrors`），一处"产出 verdict"会变（`TcpConnectionProtocol.cs:88-90` 返回 `Clean` 模式的 `Error` verdict） | 施工者可能只删计数、不改 verdict 路径 | 建议：先**枚举净影响**（D9 明令"逐条列出受影响的既有字段"），把它作为 C8 的第一个 commit 的内容，再改代码。`TcpConnectionProtocol.cs:88-90` 那处返回 `TcpMode.Clean` + `modeKnown:false`（`README.md:600` 说 `mode: unknown` 的 `clientClosedEarly` 记录是 connect 探针的**正常**记录）——区分"探针的正常路径"与"teardown 的异常路径"是这条的核心难点，建议主会话明确二者是否共用该 catch |
| ⑩ | **`JsonlSink` 的 `body` 是否真的要从 I/O try 里移出**：D10 说"移出"，D14.7 的终裁是"按策略吞/抛" | 施工者会照 D10 改结构，从而让 `SwallowAndCount` 也开始上抛 body 异常 → 弄死 target | 建议：**不改结构**。D14.7 晚于 D10 且明写"body(writer) 移出 I/O try；但策略的吞/抛语义按上面两行执行（'编程错误必须上抛'只对 `Propagate` 成立）"——今天 `:113-125` 的合并 try + `:121-124` 的策略分支已经实现了这个语义。若主会话坚持结构调整，必须同时给 `TcpTargetServer` 的 fire-and-forget 账本写入加 catch（D14.7 第 1 条已要求） |
| ⑪ | **`Truncated` 与"不改 verdict 成员"**：D9 说"**不新增 verdict 成员**（截断仍走 half-close 语义）"，但 `FrameReadStatus` 不是 verdict（它被 `TcpConnectionProtocol`/`ReliabilityExchange.ReceivePhaseAsync:131-160`/`ThroughputArm:265-291`/`DnsTcpPhase` 消费） | 施工者可能把 `Truncated` 接到 `ReliabilityOutcome`/`TcpVerdict` 上（那里已有 `ProtocolError`/`Error`） | 建议：`FrameReadStatus.Truncated` **只**新增枚举成员 + 各消费点显式分支；**不**动 `TcpVerdict`/`ReliabilityOutcome`（D9 的原文约束）。同时 `ReliabilityExchange.ReceivePhaseAsync:155-159` 的 `default:` 分支会把新成员吸收成 `ProtocolError`——必须显式列出它的归属，否则是"静默语义变化" |
| ⑫ | **`#8` 的 "LATLOAD 500 rps 下 `windowOverflow == 0`" 是个运行时断言**，E3 不能只靠读代码 | 施工者会把它写成单元测试（假 transport），从而与"真产品"脱钩 | 建议：判据分两层——单元层断言"窗口 4096 @500 rps 的直测上限 = 8.192 s"（`LatencyMetricsWriter.InFlightCeilingMs` 已是纯函数，可直接断言）；运行时层放进 E3 判据轮的证据文档（`scripts/selftest.sh` + `compare-records.py` 的读数），登记 `index.jsonl` 时 `verdict: fixed` + `evidence_cmd` 指向该轮 |
| ⑬ | **`D7` 的 `SentOutOfRange` 与 `outOfRangeSequences` 的发布面**：D2 要求"分开命名、分开发布"，但 `ArmKeys.Loss` 今天只有一个常量（`ArmKeys.Loss.cs` 的 `OutOfRangeSequences`），MIX 的 `classes.udp.outOfRangeSequences` 同理 | 施工者会复用同一个键 | 建议：新增键（如 `metrics.sentOutOfRangeSequences`）**只增**，并在 `LossShape`/`MixShape` 工厂加 `required` 属性（编译期覆盖，D5 手法）；同时 `README.md:371` 的契约键清单要加（E5，见 ②） |
| ⑭ | **`#14` 的"非单调拒收"到底算不算"已实现"**：记录侧只写不判，判定在分析器 | 施工者可能误以为要在 `ResourceSampler` 里加拒收逻辑（那会让 harness 丢样本，与"如实记录"相反） | 建议：**判定归分析器**（今天已在 `analyze.py:1249-1251`），harness 侧只补"同名多进程 `processes[]` arity ≥2"的用例；**不要**在 sampler 里加拒收 |
| ⑮ | **`#11` 的"三个"到底是哪三个**：审计 §11 列的是 `unmatchedReplies`/`abandonedAtTeardown`/`corruptRate`，但 `corruptRate` 在 LOSS 已有真实分子（回程损坏），不是"结构性恒零" | 施工者可能把 `corruptRate` 整条删掉 | 建议：**不删**。它今天能非零（`--inject-rewrite-every` 路径，`LossArm.cs:121-125`），只是**去程**损坏测不到——正确动作是"分析器披露 `undecodable`"（E1）而不是删键，并在 Note 里写明"去程损坏只能从 target 账本看到" |
| ⑯ | **`Settle` 的时机是否受 E3 影响**：如果并发修法改 `MixUdpLoop`，`Settle` 的调用点（今天它**没有** `Settle`，而是在发送循环里内联 `MarkSupplied/MarkSent` + 接收线程直接写）会与 `D18.2` 的"`Settle` 在 `WaitUntil` 之后、下一次 `BuildRequest` 之前"分叉 | 施工者可能把 `LaneEngine` 的 `Settle` 契约套到 MIX 上 | 建议：MIX **不并入引擎**（D18.1 末条只把 `Defer` 策略建给 `LatencyArm`），但 MIX 的 `Settle` 应遵守**同一条时序规则**（在 `Pacer.WaitUntil` 之后、下一次 `SendDatagramAsync` 之前），并在 `MixUdpLoop` 的文档里写明这条是对 `ILanePolicy.Settle` 的**同形但独立**约束 |
| ⑰ | **E3 的 `research/semantic-fixes/index.jsonl` 字段**：D0 定的字段是 `{id,title,verdict,evidence_test,evidence_cmd,code_refs,commit}`，但已有文件里出现了 `verdict: "observation"`（E2-a1-reading-counter）这个不在枚举里的值 | 施工者会自造枚举值 | 建议：枚举固定为 `implemented\|fixed\|deferred\|observation\|n/a`（后者是 E2 的既成事实），并在 `research/semantic-fixes/README.md` 里写明；E3 的条目 id 建议 `E3-<audit#>[-suffix]`（如 `E3-004-sim`），与 E1/E2 的 `E1-B*`/`E2-*` 并列 |
| ⑱ | **`plan key 白名单` 里的 `base` 是否要收 `lossWindowMs`**：`ArmKind.cs:44` 的 base 键表**含** `lossWindowMs`（合法），但 3 份含 base 臂的 plan 都不声明它 | 施工者看到 `#6` 的 AC 写 `{loss,mix,base}` 会以为要强制声明 | 建议：以 `D14.2` 为准（`{loss,mix}`），并在 `EveryLossAndMixArmDeclaresItsLossWindow`（`PlanFileTests.cs:39-50`）的注释里补一句"base 透传、dns 无相关臂"，防止后来者"顺手统一"把它改成三 kind（那条注释今天在 `:36-38` 已有，保持） |

---

## 5. 我核对过的事实（通过 / 失败）

### 5.1 通过（可用一条命令复现）

| 事实 | 命令 | 观测 |
|---|---|---|
| 11 份 plan、`{loss,mix}` 臂全声明 `lossWindowMs` | 见下方「命令 A」 | `plans found: 11` / `FAILURES: none`；base 三处不声明 |
| 四臂仍硬编码 gate | `rg -n 'ClientSendLoss\] = 0' benchmarks/WinForward.E2E/Client/Arms/` | 恰好 4 行：`IdleArm.cs:29`、`ReliabilityArm.cs:65`、`ThroughputArm.cs:122`、`DnsArm.cs:62` |
| 分析器不消费 `undecodable` | `rg -c 'undecodable' benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py` | `0` |
| 分析器没有内核/DPC/ISR 披露 | `rg -c 'user-mode\|kernel\|DPC\|ISR\|nonpaged' …/analyze.py` | `0` |
| `measurement-caveat` 只在分析器、由 `windowOverflow` 触发 | `rg -n 'MEASUREMENT_CAVEAT' …/analyze.py` | 常量 `:285`；`windowOverflow` 触发点 `:1812-1819` |
| `FrameStreamReader` 无 `Truncated` | `rg -n 'Truncated' benchmarks/WinForward.E2E/Wire/FrameStreamReader.cs` | 无输出（`FrameReadStatus` 5 成员，`:5-12`） |
| 冻结用例存在 | `sed -n '58,67p' tests/WinForward.E2E.Tests/FrameStreamReaderTests.cs` | 注释逐字 "E3 D9: 将改为 Truncated" |
| 只有 TCP 一台写 trailer | `rg -c 'Trailer' benchmarks/WinForward.E2E/Target/DnsServer.cs` | `0`（`TcpConnectionProtocol.cs:162,175-186` 有） |
| 账本三字段与 `--udp-receivers` 全无 | `rg -n 'udp-receivers\|udpReceivers\|acceptErrors\|receiverExits' benchmarks/WinForward.E2E` | 无输出 |
| ODE 的规模 | `rg -c 'catch \(ObjectDisposedException' benchmarks/WinForward.E2E --glob '!obj/**' --glob '!bin/**' \| awk -F: '{s+=$2} END {print s}'` | `31`（19 个文件，5 种形态，逐条见 §1 第 19 行） |
| `analyze.py` 已跟踪 | `git ls-files --error-unmatch benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py`（在**仓库根**运行） | 命中。注意：第一次我以 `git -C <root> … analyze.py`（相对路径）调用得到"untracked"，那是**我的调用错误**，已纠正 |
| `JsonValue.cs` 由删除闭合 | `git log --diff-filter=D --oneline -- 'benchmarks/WinForward.E2E/Client/JsonValue.cs'` | `925c695 feat(bench): finish the typed contract and retire the dictionary carriers` |
| 13 个 UDP `ConnectAsync` 全走 helper | `rg -n 'ConnectAsync' benchmarks/WinForward.E2E/Client/` | 15 行 = `FrameBuffer.cs:52`(helper 签名) + `:56`(helper 内唯一裸调用) + **13 处** `SocketOps.TryConnectAsync` |
| `TcpCommand` 无兜底 | `rg -n '_ =>' benchmarks/WinForward.E2E/Wire/TcpCommand.cs` | 无输出（两个 `TryGetValue` + `throw`） |
| 端口冲突校验在 | `rg -n 'collides with' benchmarks/WinForward.E2E/Cli/TargetOptions.cs` | `:75`（dnsPort）、`:89`（dnsAltPort） |
| `selftest.sh` 漏 plan → exit 2 且先于启动 | `sed -n '16,20p' benchmarks/WinForward.E2E/scripts/selftest.sh` | `exit 2` 在第 19 行，`mkdir`/启动在 `:22` 之后 |
| 测试方法数（供 E3 估算增量） | `cd tests/WinForward.E2E.Tests && rg -c '\[Fact\]\|\[Theory\]' --glob '*.cs' . \| awk -F: '{s+=$2; n++} END {print "files="n, "attrs="s}'` | `files=34 attrs=207`（xunit 用例数更多：`InlineData` 逐行展开，E2-c 的 check 记录为 **Passed: 257**） |

**命令 A**（`#6` 的机械判据，只读）

```bash
cd benchmarks/WinForward.E2E && python3 - <<'PY'
import json,glob,os
plans=sorted(glob.glob('scripts/plans/*.json')+glob.glob('scripts/plans-short/*.json'))
print("plans found:", len(plans))
bad=[]
for p in plans:
    for a in json.load(open(p))['arms']:
        if a.get('kind') in ('loss','mix') and 'lossWindowMs' not in a:
            bad.append((p,a.get('name'),a.get('kind')))
print("FAILURES:", bad if bad else "none")
PY
```

**命令 B**（四臂硬编码）

```bash
rg -n 'ClientSendLoss\] = 0' benchmarks/WinForward.E2E/Client/Arms/
```

**命令 C**（#13 的比率口径）

```bash
rg -n 'JsonRate\.Rate|JsonPerSecond\.PerSecond' benchmarks/WinForward.E2E/Client benchmarks/WinForward.E2E.Contracts   # 23 处调用点
sed -n '18,19p' benchmarks/WinForward.E2E.Contracts/Json/Rate.cs       # denominator == 0 ? null : …
sed -n '18,19p' benchmarks/WinForward.E2E.Contracts/Json/PerSecond.cs  # ticks <= 0 ? null : …
```

**命令 D**（ODE 全清单，供 D2 的机械判据基线）

```bash
rg -n -A3 'catch \(ObjectDisposedException' benchmarks/WinForward.E2E --glob '!obj/**' --glob '!bin/**'
rg -c 'catch \(ObjectDisposedException' benchmarks/WinForward.E2E --glob '!obj/**' --glob '!bin/**' | awk -F: '{s+=$2} END {print "total ODE catches =", s}'   # 31（分布 19 个文件）
```

**命令 E**（`achievedRate` 两种口径）

```bash
rg -n 'AchievedRate = ' benchmarks/WinForward.E2E/Client
rg -n 'completionRate' benchmarks/ || echo "completionRate: absent"
```

**命令 F**（#17/#18/#19）

```bash
A=benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py
rg -c 'NOT_CARRIED_CELL' $A; rg -c 'UDP53_LABEL' $A
rg -c 'user-mode|kernel|DPC|ISR|nonpaged' $A || echo "#19: 0 hits"
```

### 5.2 失败 / 未能核对（列为待办，不是结论）

| 项 | 为什么没核对 | E3 该怎么补 |
|---|---|---|
| `#4` 的 0/10/50% 仿真、`#10` 的 1..6 穷举、`#7` 的阻塞仿真、`#14` 的重启仿真 | **不存在**。证明"不存在"的命令：`cd tests/WinForward.E2E.Tests && rg -n -i 'simulat' .`（exit 1，0 命中）与 `rg -n -i 'permut\|exhaust\|1956\|arrival order\|all orders' .`（exit 1，0 命中） | 见 §3 组 B / 组 E |
| `#8` 的 "LATLOAD 500 rps 下 `windowOverflow == 0`" | 需要真跑（禁止运行 build/test/selftest），只能给解析上界 8.192 s | 见 §4-⑫ |
| `#9` 的"直方图 count 上升" | 引擎级测试已有（`LaneEngineTeardownTests.cs:36-115`），但**没有**策略/臂级 count 断言 | 见 §3 组 C 的补强（或登记为已足） |
| `dotnet build`/`test`/`format`/`inspectcode`/`selftest.sh` 的绿 | 明令禁止运行 | E3 每个 C 批次按 D14.11 跑；`index.jsonl` 的 `evidence_cmd` 用 `dotnet test --filter FullyQualifiedName~<测试名>` 逐条落地（D0 的 AC5） |
| `analyze.py` 的 6053 行里 `:2119 ledger_views`（D0.3 引用） | 我只核到 `load_ledger:890`/`ledger_paths:689-690` 一族，未逐行确认 `:2119` | 无阻塞；E4 会重写该文件 |

---

### 附：E3 的 `index.jsonl` 建议骨架（不写进本文件之外）

```json
{"id":"E3-001-concurrency","title":"UdpReliabilityTracker 的并发契约（谁写 book）","verdict":"deferred","evidence_test":"...","evidence_cmd":"...","code_refs":["benchmarks/WinForward.E2E/Client/UdpReliability.cs","benchmarks/WinForward.E2E/Client/Arms/MixUdpLoop.cs"],"commit":null}
```

`verdict` 建议枚举：`implemented`（已实现 + 本次补了回归测试）、`fixed`（本次改了代码）、`deferred`（判定不修/登记后续）、`observation`（口径观测，E2 已有先例）、`n/a`（不适用，如 `JsonValue` 的 `default:`）。逐条对应的今天结论见 §1 表格。
