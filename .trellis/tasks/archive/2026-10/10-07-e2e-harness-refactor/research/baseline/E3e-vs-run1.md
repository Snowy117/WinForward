# E3-e 回归比对与定向证据（ODE 统一 + `achievedRate` 单一口径 + `completionRate` 改名）

本批是 E3 的最后一批（`10-07-e2e-e3-semantics/implement.md` 的 E3-e 节，D19.1 ⑧⑨、D19.2 ⑧⑨、D19.3 A/H）。
**`src/` 一行未改，本轮未提交。**

判定线（逐条见 §7）：5 处 ODE 形态各自有「非 teardown 路径仍产生原数据点」的行为用例 + 一条把 31 处分类
当基线的源扫描 gate（8 条突变全部被杀）；`completionRate` 是**纯改名**（同一轮里与改前 `achievedRate` 的
表达式逐值相等）；新 `achievedRate` 在 `requests > responses` 的运行里严格大于它；新键以 `added` 进
`contract-rename.json`（`ADDITIONS` 一行，既有行逐字未变）；六条门禁 + `effective-lines.py` 全绿。

---

## 0. 结论一句话

**teardown 不是测量**：靶机/臂自己关掉的 socket 不再被记成 bulk/page error（MIX 两处）、不再被记成
DNS 的 `tcpAborted`（靶机一处），也不再伪造一个 `TcpVerdict.Error` 的连接判决或一个 `otherError` 的
尝试观测（两处改为显式的 `CommandOutcome.TornDown` 与 `ExchangeStatus.Cancelled`）；同时 `achievedRate`
在全 harness 只剩一个口径 —— **成功发出的请求/秒**（PERSIST 的完成口径改名为 `completionRate`，
旧值一个不少地留在新键下）。

---

## 1. ODE 净影响枚举（**先枚举，后改码**）

### 1.1 改前实测（冻结基线 `research/baseline/run1`，改前二进制）

| 站点 | 该站点发布的键 | run1 实测 |
|---|---|---|
| `MixBulkLoop` 的 ODE 臂 | MIX `metrics/classes/bulk/errors` | **0** |
| `MixPageLoop` 的 ODE 臂 | MIX `metrics/classes/page/errors` | **0** |
| `DnsServer` 的 ODE 臂 | `dnsSummary/tcpAborted` + `targetSummary/dns\|dnsAlt/tcpAborted` | **0 / 0**（两台 listener） |
| `TcpConnectionProtocol` 的 ODE 臂 | `tcpSummary/verdicts/error` + 该连接的 `tcp` 记录 | **0 条**（`connections=157`，判决分布 `clean 61 / reset 25 / partialFin 25 / halfClose 25 / clientClosedEarly 21`） |
| `ReliabilityExchange` 的 ODE 臂 | REL `metrics/outcomes/otherError` + attempt 记录的 `status`/`otherError` | **0**（`connectAttempts = scheduledAttempts = 101`） |

改前所有这些桶都是 0 ⇒ 净影响的**上界**是「每次 ODE 事件各差 1」，而不是某个可观测的位移。

### 1.2 逐项改前 → 改后

| # | 站点 | 改前（ODE 臂） | 改后 | 发布值变化（每次事件） |
|---|---|---|---|---|
| 1 | `MixBulkLoop.cs` | `_bulkErrors++` | 空 catch（注释） | `classes/bulk/errors` **−1**（相对改前；干净跑不动） |
| 2 | `MixPageLoop.cs`（page 连接） | `_pageErrors++` | 空 catch | `classes/page/errors` **−1** |
| 3 | `DnsServer.cs`（stream handler） | `_tcpAborted++` | 空 catch | `tcpAborted`（两层、两台 listener 各自）**−1** |
| 4 | `TcpConnectionProtocol.cs` | `CommandOutcome(TcpVerdict.Error, …)` | `CommandOutcome.TornDown`（`Outcome` 为 `null`） | `verdicts/error` **−1**，并且**少一条 `tcp` 记录**；`connections`（accept 普查）不变 |
| 5 | `ReliabilityExchange.cs` | `attempt.OtherError = true` | `result._status = ExchangeStatus.Cancelled` | attempt 记录的 `status`：`exchanged` → `cancelled`；`otherError` 叶：`true` → `false`；`outcomes/otherError` **不变**（见 §1.4） |

其余 **26 处保持原语义**，逐类清单见 §4。

### 1.3 可达性：为什么这些位移在**任何可达路径上都发生不了**

这 5 处 catch 的 socket 都由**包住这次调用的那个 `using`** 持有，被调用的方法自己没有任何
dispose 面：

| 站点 | socket 的 owner | 何时 dispose |
|---|---|---|
| `MixBulkLoop` / `MixPageLoop` | 各自方法里的 `using var socket = context.CreateTcpSocket()` | catch 出栈之后 |
| `DnsServer` 的 stream handler | `HandleTcpConnectionAsync` 的 `using (socket)` | catch 出栈之后 |
| `TcpConnectionProtocol` | 调用方 `HandleConnectionAsync` 的 `using (socket)` | `RunConnectionAsync` 返回之后 |
| `ReliabilityExchange` | `RunAttemptAsync` 的 `using var socket` | `ExchangeAsync` 返回之后 |

harness 里没有第二条 dispose 面：停机只取消 token（`OCE`）、只 dispose **listener**（`TcpAcceptLoop.Dispose`）
与 UDP socket（`DnsServer.DisposeAsync`，都在 `RunAsync` 返回之后）。臂侧的 `using var linked` 是
`CancellationTokenSource`，与 socket 无关。

**实测探针**（一次性，未留树；结论写进 spec §3.9）：

| 场景 | 结果 |
|---|---|
| `socket.Dispose()` 之后调用 `ReceiveAsync` | `ObjectDisposedException`（⇒ 这 5 个臂只在「socket 被 dispose 之后又被复用」时可达） |
| `socket.Dispose()` 之后调用 `ConnectAsync` | `ObjectDisposedException` |
| **pending `ReceiveAsync` 期间** `socket.Dispose()` | `SocketException: Operation canceled`（`SocketError.OperationAborted`）——**不是** ODE |

第三条是关键：真实 teardown 竞争产生的是 `SocketException(OperationAborted)`，它落到每个站点**既有的
socket-error 臂**（MIX 两处 `catch (SocketException) { _*Errors++; }`、DNS 的 `catch (SocketException) { _tcpAborted++; }`、
TCP 协议的 `catch (SocketException) → TcpVerdict.Error`、REL 的 `catch (SocketException) → OtherError = true`），
**这些臂本轮一格未动**（前提复核条目 19 的「其余 26 处保持原语义」）。所以：

- **净影响（可达面）= 0**：本批不改任何一次真实运行里任何一个计数器的值；改的是「防御臂一旦被触发时
  会伪造一个测量」这个潜在语义。
- 唯一可在测试里确定驱动的是 `TcpConnectionProtocol`：它的 reader 是**入参**，一个在首次读之前就被
  dispose 的 socket 直接到达该臂（§2.2 fact 1）。

### 1.4 REL 的 5 号站点为什么停在「显式但不进桶」

`outcomes` 的**分区恒等式**是活的正确性门（`analyze.py` 的 `reliability_invariants`：
`sum(outcomes) == connectAttempts == scheduledAttempts`，破坏它会报 correctness-failure；`E3-b1` 的
README 行登记也把它写成 REL 的 `clientSendLoss` 门）。臂里没有「未观测」这个桶，而 `ReliabilityAttempt.Observed`
的默认值就是 catch-all `OtherError`，所以：

- teardown 尝试**仍然出现在** `outcomes`（分区不破，发布值不动）；
- 但它不再被写成一次「peer 造成的异常」：`Status = Cancelled`（attempt 记录里 `status: "cancelled"`、
  `otherError: false`），即 teardown 在记录里是**显式的非观测**，不是观测。

这是本批唯一一处「显式」未走到「不进桶」的地方，登记给 check/E4 裁定（§7-2）。

---

## 2. 5 处落点与反证

### 2.1 三处「计成数据点」→ 忽略（空 catch）

落点：`MixBulkLoop.cs`（`BulkLoopAsync` 末尾）、`MixPageLoop.cs`（`PageConnectionAsync` 末尾）、
`DnsServer.cs`（`HandleTcpConnectionAsync` 的 try 末尾）。三处都只留一行说明**为什么什么都不记**
（空 catch 若不说明，读起来就像一个被吞掉的 bug；另外 19 处同类站点一直是这个形状）。

**非 teardown 路径仍有原数据点的用例**（`ObjectDisposedTeardownTests`）：

| fact | 驱动 | 断言 |
|---|---|---|
| `ABulkLoopStillBooksAConnectionItCouldNotOpen` | 真 `MixBulkLoop.BulkLoopAsync` → 没有任何 listener 的 loopback 端口 | `_bulkErrors == 1` |
| `APageLoopStillBooksEveryConnectionItCouldNotOpen` | 真 `MixPageLoop.PageLoopAsync` → 同端口，30 s 期限、等待上界 15 s，token 在 13 条连接各失败一次后取消 | `_pageErrors == 13 == PageConnections`、`PageConnectionsPerDesktop[0] == 0` |
| `TheDnsListenerStillCountsAStreamItsPeerAborted` | 真 `DnsServer`：一条 TCP DNS 查询被**回答**（handler 回到读长度前缀处）后取消运行 token | `dnsSummary/tcpAborted == 1`（读被取消的臂，不是 ODE 臂） |

### 2.2 两处「产出 verdict」→ 显式

| 站点 | 改后形状 | 发布面 |
|---|---|---|
| `TcpConnectionProtocol.RunConnectionAsync` | `catch (ObjectDisposedException) { return CommandOutcome.TornDown; }`；`CommandOutcome.Outcome` 从 `TcpModeOutcome` 改成 `TcpModeOutcome?`，`TornDown` 的 `Outcome` 是 `null` | `TcpTargetServer.HandleConnectionAsync` 在 `command.Outcome is not { } outcome` 时直接 `return`：**不写 `tcp` 记录、不 bump 任何 verdict 桶**、不累加 `bytesEchoed`/`protocolErrors`/`truncatedFrames`；accept 普查（`connections`）仍在 accept 时 +1 |
| `ReliabilityExchange.ExchangeAsync` | `catch (ObjectDisposedException) { result._status = ExchangeStatus.Cancelled; }`（不再置 `attempt.OtherError`） | attempt 记录 `status: "cancelled"`、`otherError: false`；`outcomes` 不变（§1.4） |

**反证**：

- `AConnectionTornDownUnderTheProtocolPublishesNoVerdict`：`socket.Dispose()` 后
  `RunConnectionAsync(socket, new FrameStreamReader(socket), token)` ⇒ `command.Outcome is null`（改前这里是
  `TcpVerdict.Error`）。
- `AConnectionTheArmCancelledStillPublishesItsErrorVerdict`：真 loopback 连接 + 已取消的 token ⇒
  `Outcome.Verdict == TcpVerdict.Error`、`ProtocolErrors == 0`、`Truncated == false`（非 teardown 的取消臂原样）。
- `ReliabilityTruncationTests.AnAttemptWhoseStreamEndsInsideAFrameIsAProtocolErrorAndNotACleanEof` 追加
  `assert attempt.Status == ExchangeStatus.Completed`：`otherError` 桶仍由**真实对端行为**喂（截断帧 ⇒
  `protocolError` + `!eof` ⇒ `OtherError`），而 teardown 的 status 是 `cancelled` —— 两者在记录里可区分。

### 2.3 源扫描 gate（把 31 处分类清单当基线）

`tests/WinForward.E2E.Tests/ObjectDisposedCatchGateTests.cs`：

- 扫 `benchmarks/WinForward.E2E/**/*.cs`（排除 `obj/bin`），逐个 `catch (ObjectDisposedException`（含
  `exception`/`when`）按**花括号配对**取 body，去掉注释后归形；
- 注册表（19 文件、**31 处**、按源码顺序）断言四种形状：`ignored`（body 空 = 19 处既有 + 本批 3 处）、
  `ends`（`return;`/`return false;`/`break;`/`return "unknown";` 6 处）、`connectFailure`（`LaneOpenResult(Ok:false,…)`
  1 处）、`noVerdict`/`noObservation`（2 处）；
- 断言总数 31 与文件数 19（少一处、多一处、换一类、把自增写回 ignored 臂都红），并断言**任何**站点的 body
  都不含 `Interlocked`；
- 自控：一段合成片段里「带 `Interlocked` 的 catch」必须被判成 `unregistered`、「只有注释的」判成
  `ignored`、「`return CommandOutcome.TornDown;`」判成 `noVerdict`（扫描器悄悄停止匹配时它先红）；
- `Contracts/Json/JsonlSink.cs` 的 3 处**不在**基线里，理由写在 gate 的 `<remarks>`（sink 的策略就是
  「吞掉并计数写失败」，D14.7；其中一处把失败记进 `ledgerWriteErrors` 是**真实写失败**，不是 teardown）。

**八条突变全部被杀**（`/tmp/e3e/mutate.py`，逐条 patch → `dotnet build` → `dotnet test --filter` →
从备份整份还原并核对 sha256）：

| # | 突变 | 杀掉它的断言 |
|---|---|---|
| M1 | `MixBulkLoop` 的 ODE 臂恢复 `_bulkErrors++` | gate（unregistered） |
| M2 | `MixPageLoop` 的 ODE 臂恢复 `_pageErrors++` | gate |
| M3 | `DnsServer` 的 ODE 臂恢复 `_tcpAborted++` | gate |
| M4 | `TcpConnectionProtocol` 改回 `TcpVerdict.Error` 的 `CommandOutcome` | gate |
| M5 | `ReliabilityExchange` 改回 `attempt.OtherError = true` | gate |
| M6 | `MixBulkLoop` 的 connect-failure 臂不再计数 | `ABulkLoopStillBooksAConnectionItCouldNotOpen` |
| M7 | `DnsServer` 的取消臂不再计数 | `TheDnsListenerStillCountsAStreamItsPeerAborted` |
| M8 | `MixPageLoop` 的 connect-failure 臂不再计数 | `APageLoopStillBooksEveryConnectionItCouldNotOpen` |

（M6–M8 是「计数器仍然活着」的负控：它们证明上表的「非 teardown 路径仍有原数据点」不是空断言。）

---

## 3. `achievedRate` 单一口径（D19.2 ⑧）

### 3.1 六个站点、一个分子口径（改后）

| 臂 | 分子 | 站点 | 本轮是否改 |
|---|---|---|---|
| LAT / LATLOAD | `tcp.SentOk` / `udp.SentOk` | `LatencyMetricsWriter.cs:139,170` | 否（已是「成功发出」） |
| LOSS | `UdpReliabilityTracker.SentOk` | `LossArm.cs:120` | 否 |
| DNS / DNSALT | `sent`（写进 socket 的查询数，`_sent++` 在 `SendAsync` 之后） | `DnsArm.cs:110` | 否 |
| REL | **请求发送完成的尝试数**（`tally._transferSamples`，与 `meanTransferMs` 同总体） | `ReliabilityMetricsWriter.cs:76` | **是**（原 `attempts.Length`：包含从未发出请求的尝试） |
| PERSIST | **`_sentRequests`**（帧 `SendAsync` 完成时 +1） | `PersistentArm.cs:119` | **是**（原 `_responses`：完成口径） |

`_sentRequests++` 落在 `PersistentConnection.ExchangeAsync` 的成功发送之后（与 `_lastSendTicks` 同处），
所以「发出但没被应答」的请求进 `achievedRate` 而不进 `completionRate`。

### 3.2 反证：纯改名 + 分离

`tests/WinForward.E2E.Tests/PersistentRateCaliberTests.cs`（两条都跑**真臂**）：

| fact | 驱动 | 断言 |
|---|---|---|
| `EveryRequestAnsweredPublishesBothCalibersWithTheSameValue` | 真 `TcpTargetServer`（mode clean 回显）+ PERSIST 臂 3 s / 500 ms | `requests > 0`、`requests == responses`、`completionRate == achievedRate > 0` |
| `RequestsSentButNeverAnsweredSeparateTheTwoCalibers` | 一个「accept、读完每一帧、从不回答」的 peer + PERSIST 臂 5 s / 2000 ms | `requests > 0`、`responses == 0`、`timeouts > 0`、`completionRate == 0`、`achievedRate > completionRate` |

两条合起来就是「逐值等价」的证明：改前 `achievedRate = PerSecond(responses, elapsed)`，而
（a）`responses == requests` 时它与 `PerSecond(sentRequests, elapsed)` **同一个数**（第一条的等号），
（b）`responses == 0` 时它等于 0（第二条的等号）——`completionRate` 正是这两条里被断言的那个值。

### 3.3 新键登记（D19.3 A）

- `ArmKeys.Persistent.CompletionRate = "completionRate"`（紧随 `AchievedRate` 声明 ⇒ 写序 = 声明序，
  由 `ContractShapeTests.TheMetricsAreWrittenInKeyDeclarationOrder` 钉住）；
- `PersistentMetrics.CompletionRate`（`required double?`）+ `Reading.Write`（可空 ⇒ `null` 走 `<see langword="null"/>` 约定）；
- `Shapes/PersistentShape.cs`：`Nullable` 列表加该路径、工厂加 `CompletionRate`（可空形状测试的两半）；
- `scripts/contract-inventory.py` 的 `ADDITIONS` 加 `metrics/completionRate`（一行），随后
  `contract-inventory.py rename` 把它写进 `research/contract-rename.json` 的 `added`（读数见 §5.1）；
- `PersistentArm` 的第 1 条 note 补一句口径（**这是本批唯一的结构差异**，见 §5.2）。

---

## 4. 26 处保持项的分类清单（源扫描基线）

31 = 19 文件。**改后**的形状分布：`ignored` 22（原 19 纯吞 + 本批 3 处计数点）、`ends` 6、
`connectFailure` 1、`noVerdict` 1、`noObservation` 1；**保持原语义的 26 处** = 19 纯吞 + 6 静默 return +
1 当连接失败（`FrameBuffer.cs:67` → 调用方 `LossArm.cs:158 MarkSendFailure()`）。

| 类 | 站点（文件:行，E3-e 施工后；与 §2.3 的 gate 同一套扫描重算） |
|---|---|
| 纯吞（注释）19 | `DnsTcpPhase.cs:71,222`、`DnsUdpPhase.cs:55,173`、`LossArm.cs:196,259`、`MixPageLoop.cs:162`、`MixUdpLoop.cs:79,191`、`PersistentArm.cs:187`、`ThroughputArm.cs:258,305`、`FrameBuffer.cs:83`、`LaneEngine.cs:137,265`、`ResourceSampleWriter.cs:30`、`Program.cs:148`、`TargetLog.cs:20`、`UdpEchoServer.cs:211` |
| 静默 return 6 | `DnsServer.cs:241`（`return;`）、`DnsServer.cs:289`（`return false;`）、`UdpEchoServer.cs:155,186`（`return;`）、`TcpAcceptLoop.cs:62`（`break;`）、`TcpTargetServer.cs:151`（`return "unknown";`） |
| 当连接失败 1 | `FrameBuffer.cs:67`（`return new LaneOpenResult(Ok: false, …)`） |

三处**本批从「计数」搬到「纯吞」**的站点（`MixBulkLoop.cs:73`、`MixPageLoop.cs:119`、`DnsServer.cs:388`）
与两处**本批改成显式**的站点（`TcpConnectionProtocol.cs:111` → `noVerdict`、`ReliabilityExchange.cs:114` →
`noObservation`）在 gate 的注册表里与老站点同表登记，因此「把自增写回去」或「改回旧 verdict」重来一次
仍然红（§2.3 的 M1–M5）。

---

## 5. 比对结果

### 5.1 改名表与结构差异

```console
$ python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename \
      --baseline $R/baseline/run1 --run /tmp/e3e/post1 \
      --out-json $R/contract-rename.json --out-md $R/contract-rename.md
$R/contract-rename.json: 605 row(s) = added 16, identical 580, renamed 9   # E3-d 后是 604 / added 15
$ git diff -- $R/contract-rename.json | rg '^[+-]'
+  {
+    "kind": "added",
+    "old_path": null,
+    "new_path": "metrics/completionRate",
+    "reason": "persistent metrics: the arm's completion caliber -- responses per elapsed second, the population metrics/achievedRate carried before the harness unified that name on requests successfully sent (D19.2 ⑧, E3-e)"
+  },
```

`added 15 → 16` 的那一行就是新键；既有 580 条 `identical` + 9 条 `renamed` **逐字未变**（`git diff` 只有这
6 行）。MD 侧多 `fresh run`/`rows` 两行 + `## Added` 一行。

### 5.2 结构差异（本批唯一一条）

`records/PERSIST.jsonl` 的第 1 条 note：

- 改前：`… explained by connectFailures, sendFailures, timeouts, remoteClosed and protocolErrors.`
- 改后：同句 + ` achievedRate counts the requests whose send completed per elapsed second and completionRate counts the responses per elapsed second, so requests neither sent nor answered are in neither rate.`

理由与 E3-b1 的三条完全同型：note 是记录的公开说明，新键落地后不写明两个速率会留下「记录里两个名字没有
解释」的坑；`compare-records.py` 的 `notes` 按字符串集比对且没有声明机制，所以它进 structural 栏（§7.2
第 1 栏 = 4 = E3-b1 的 3 + 本批 1）。**结构面其余为 0**：键集/类型/数组长度/字符串值除这一条外全等。

### 5.3 零宽键逐值相同 / 噪声地板 / `--strict`

（见 §7。）

---

## 6. 数值变化登记（本批改了哪些发布值）

| 键 | 改前 | 改后 | 何时会不同 |
|---|---|---|---|
| PERSIST `metrics/achievedRate` | `responses / elapsed` | `_sentRequests / elapsed` | 请求发出但未应答（或没发出）的运行：**新值更大**；`requests == responses` 时逐值相同 |
| PERSIST `metrics/completionRate` | （不存在） | `responses / elapsed` | 新键；值 = 改前 `achievedRate` |
| REL `metrics/achievedRate` | `attempts.Length / elapsed` | `transferSamples / elapsed` | 有尝试连不上或发不出时**新值更小**；全部发出时逐值相同 |
| `classes/bulk/errors`、`classes/page/errors`、`dnsSummary/tcpAborted`、`verdicts/error`、attempt `status`/`otherError` | 见 §1.2 | 见 §1.2 | **只在 ODE 臂被触发时**；harness 无路径触发（§1.3），冻结基线里全为 0 |

本批**没有**改：账本任何既有计数器、LAT/LOSS/DNS 的 `achievedRate`、任何 `gates/*`、任何 `parameters/*`、
分析器（`analyze.py`/`analysis/README.md` 对 `achievedRate|completionRate` 零命中）。

---

## 7. 读数（门禁、比对、`--strict`）

### 7.1 六条门禁 + `effective-lines.py`

第 1–5 条在**同一棵冻结树**上串行跑（`/tmp/e3e/gates2/*.log`），第 6/7 条是本批发布二进制上的两次
selftest（`/tmp/e3e/gates/6-publish.log`、`7-post1.log`、`8-post2.log`；最后一次测试文件改动后重新发布，
linux `WinForward.E2E.dll` 的 sha256 与判据轮**逐位相同** ⇒ 复用成立）。

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)**，rc=0 |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | 14 个程序集全绿，rc=0；`WinForward.E2E.Tests` **322**（E3-d 的 313 + 本批 9） |
| 3 | `python3 scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests` | **0 字节输出、rc=0**（新增三个测试文件 ≤300 有效行） |
| 4 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0、**0 字节输出** |
| 5 | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`（单独跑） | XML 解析后 `<Issues>` 容器 **0 个子元素**（`<Issue>` 0、`<IssueType>` 0），rc=0。第一次跑出一条 HINT：新 gate 测试里 `if/else if` 链的 `ConvertIfStatementToSwitchStatement`，改成 `switch` 后转绿；这一条改的是测试代码，产品面零变化（重发布 sha256 逐位相同，见第 6 条） |
| 6 | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3e/pub scripts/publish.sh` | rc=0；三份产物；linux dll sha256 `0ab6d9f71c60d5774a8f4bdb1c9767819f4f613f80f6e0ef5dd9354393faa78c`（重发布后逐位相同） |
| 7 | `WF_PUB=/tmp/e3e/pub scripts/selftest.sh scripts/plans/selftest-plan.json` ×2 | 两次 **rc=0**（post1 / post2，同一个二进制） |

**收尾复跑**：8 条突变逐条还原（5 个文件 sha256 与突变前逐位相同）之后，重新 `dotnet build WinForward.slnx -c Release`
（0 Warning / 0 Error）并 `dotnet test WinForward.slnx -c Release -m:1 --no-build` 跑满 14 个程序集
（E2E **322** 全绿）⇒ 上表读的是最终源码状态，不是某次突变后的二进制。

### 7.2 判据比对（`compare-records.py`）

```console
$ python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename \
      --baseline $R/baseline/run1 --run /tmp/e3e/post1 \
      --out-json $R/contract-rename.json --out-md $R/contract-rename.md
renames landed in this run: 9; still published under the old spelling: 0; not observed at all: 0
$R/contract-rename.json: 605 row(s) = added 16, identical 580, renamed 9
declared but not observed in the fresh run: 2
```

**JSON 差量只有 1 行新增**（`metrics/completionRate`；`only in old` 空、既有 604 行逐字未变），MD 只多
`fresh run`/`rows` 两行 + `## Added` 一行。

| 对照 | structural | conditional | identity | declared | contract | rename | readings | exit |
|---|---|---|---|---|---|---|---|---|
| run1 → post1（**判据**） | **4**（E3-b1 的三条 note + 本批 PERSIST 的 1 条，见 §5.2） | 0 | 0 | **40** | **0** | 0 | 294/366（217 越带） | 1（仅那 4 条 note） |
| post1 → post2（同二进制噪声地板） | 0 | 0 | 0 | 0 | **26** | 0 | 286/366（143 越带） | 1 |

- **第 1 栏 4 条**逐字是：`records/BASE.jsonl`/`records/LOSS.jsonl`/`records/MIX.jsonl` 的 `notes`（E3-b1 已登记
  的三条）与 `records/PERSIST.jsonl` 的 `notes`（**本批唯一新增**，§5.2）。
- **第 4 栏 40 = 27 条改名命中 + E3-b1 的 3 + E3-c 的 4 + E3-d 的 5 + 本批 1**（`metrics/completionRate`）；
  `added observed/not observed` 里 "not observed" 仍只有 `detail`/`error`。
- **第 5 栏 run1→post1 = 0**：没有任何契约路径越带；噪声地板的 26 条**全部**是 "no recorded jitter band for
  this path"（本批 1 条新键 + E3-b1/c/d 的族），没有一条是"值动了"。

### 7.3 零宽键、账本逐值与 `--strict`

```console
$ for d in $R/baseline/run1 /tmp/e3e/post1 /tmp/e3e/post2; do python3 /tmp/e3a/zerowidth.py $d/out; done | diff
（无输出：三段逐行相同）
```

**39 个 (记录, 路径) 对 / 19 条不同路径**，三跑逐值相同（与 E3-b1/c/d 完全一致）。

账本逐路径值表（`/tmp/e3d/ledger-values.py`，110 条路径）：**same=86、added=6（全部是 E3-d 的
`acceptErrors`/`udpReceivers`）、moved=18**；moved 的 18 条**没有一条既有计数器** —— 全部是
`utc`/`startedTicks`/`endedTicks`/`peer` 端口/`udpSummary::ticks`、E3-c 的 `truncatedFrames`
（run1 缺席、两个 post 都是 0）、以及 `udpSummary::<records>`（97/98/97）与 `sources<len>`（1/0/1）这类
**区间记录条数**（采样节奏，不是计数器的值）。本批**一处账本计数器都没动**，符合 §6 的登记。

`--strict` 读数摘要：

- run1 → post1：`readings=294/366`，217 条越带 —— 与本批无关：PERSIST/REL 的 `achievedRate` 在冻结跑批里
  **逐值相同**（见下），越带全部落在 CPU 秒、`ticks`、`elapsedSeconds` 与各 `achievedRate` 的小数尾
  （噪声地板自己就有 143 条）。
- post1 → post2：`readings=286/366`，143 条越带 ⇒ 越带条数是宿主噪声而不是本批的位移。

**口径改名的逐值对照（同一计划、冻结基线 vs 本批两次跑）**：

| 键 | run1（改前） | post1 | post2 |
|---|---|---|---|
| PERSIST `metrics/achievedRate` | **1.5**（= `responses` 15 / 10 s） | **1.5** | **1.5** |
| PERSIST `metrics/completionRate` | 不存在 | **1.5** | **1.5** |
| PERSIST `requests` / `responses` | 15 / 15 | 15 / 15 | 15 / 15 |
| REL `metrics/achievedRate` | 10.096（101/101 尝试全部发出） | 10.096 | 10.096 |
| LAT `tcp.achievedRate` / `udp.achievedRate` | 40.191 / 20.096 | 40.183 / 20.091 | 40.199 / 20.099 |
| LOSS / DNS / DNSALT `achievedRate` | 196.115 / 50.22 / 50.236 | 196.103 / 50.211 / 50.235 | 196.098 / 50.23 / 50.235 |

- PERSIST 那一行就是「纯改名」的**实测**形式：该轮 `requests == responses`，因此改前的 `achievedRate`
  与改后的 `completionRate` 是同一个数（1.5），而新 `achievedRate` 也等于它 —— 三个值在两端点上重合（§3.2
  的单测把两个端点都钉住，包括 `responses == 0` 的端点）。
- REL 的分子换了总体但该轮 101/101 全部发出 ⇒ 10.096 逐值不变；其余五个站点本轮未改分子，差异全是读数噪声。

---

## 8. 偏离与登记

1. **REL 的 teardown 尝试仍进 `outcomes`**（§1.4）：分区恒等式 `sum(outcomes) == connectAttempts == scheduledAttempts`
   是活的正确性门，臂里没有「未观测」桶，`ReliabilityAttempt.Observed` 的默认值就是 catch-all `OtherError`。
   本批把该处改成**显式**（`Status = Cancelled`，attempt 记录里可见、`otherError` 叶为 false），但没有把它
   移出桶；要做到「不进桶」必须同时改分析器的恒等式（`analyze.py reliability_invariants`）并新增一个非观测
   计数键，这超出本轮范围，登记给 check/E4 裁定。
2. **4/5 的站点没有行为装置**（MIX bulk/page、DNS 的 stream handler、REL）：它们的 socket 由包住这次调用的
   `using` 持有，harness 里没有第二条 dispose 面；而真实 teardown 竞争的实测形状是
   `SocketException(OperationAborted)`（§1.3），落到各站点既有的 socket-error 臂、**不是** ODE 臂。因此
   这 4 处的 teardown 半由**源扫描 gate**（31 处分类基线 + 形状断言）钉住，非 teardown 半由 4 条行为用例
   钉住（§2.1、§2.2）。这与 E3-c 的 `ReliabilityExchange default:`、E3-d 的第二个 `error` 写点同型
   （「行为装置 + 源码装置」两半）。
3. **`connections` 仍数被 teardown 掐掉的 accept**：`_connectionCount` 在 accept 时 +1（它是 accept 普查，
   不是判决），所以一次 ODE 会让 `tcpSummary/connections` 比 `tcp` 记录数大 1。本批只按「判决与记录 = 测量」
   划界，把普查留在原处；分析器的账本连接交叉核对（`LEDGER_CONNECTION_*`）读的是**记录数**，不受影响。
4. **REL 的 `achievedRate` 分子也改了**（D19.2 ⑧ 只点名 PERSIST）：`attempts.Length` 把「从未发出请求的
   尝试」（连不上 / 命令帧发不出）算进「成功发出」，与单一口径矛盾；改成 `transferSamples` 后，
   全部尝试都发出时逐值不变，有失败时新值更小。按任务「所有会改发布值的项逐条登记」的要求登记在 §6。
5. **PERSIST 第 1 条 note 改了**（本批唯一的结构差异）：原文只解释 `requests`/`responses`，没写两个速率；
   新键落地后不写会留下「记录里两个名字没有说明」的坑。与 E3-b1 的三条 note 更正并列登记（§5.2）。
6. **`ADDITIONS` 的登记是手写的一行**（`metrics/completionRate`）：`contract-inventory.py` 的 `ADDITIONS`
   本来就是人工维护的「冻结基线不可能含有的路径」清单（E3-b1/c/d 各批同样），本批照旧；`rename` 的输出
   会验证它（多登记的路径会以 `unexpected` 报错、少登记会以 `added` 缺席表现成结构差异）。

---

## 9. 给 check 的独立复核点

1. **自己重跑 8 条突变**（`python3 /tmp/e3e/mutate.py`，逐条 patch→build→`--filter`→整份还原并核对 sha256）：
   重点看 M4/M5（把两处显式改回旧 verdict/观测 ⇒ gate 红）与 M6–M8（把三处**非 teardown** 计数臂改成不记
   ⇒ 对应行为事实红）——前者证明 teardown 语义有判据，后者证明计数器仍然活着。
2. **自己重算 31 处分类**：`rg -c 'catch \(ObjectDisposedException' benchmarks/WinForward.E2E`（31 处 / 19 文件），
   并独立重放本文档 §9-2 的那种花括号扫描（**注意先剥注释再折叠空白**：先折叠会把 `//` 注释后面的代码
   一起吃掉，得到假的 `ignored`）。
3. **自己实测可达性那两条**（§1.3）：`Dispose()` 后调用 ⇒ ODE；pending `ReceiveAsync` 期间 `Dispose()`
   ⇒ `SocketException: Operation canceled`。整条「净影响 = 0」的论证都压在这两条上，值得独立复现。
4. **改名表与比对**：`git diff` 的 `research/contract-rename.json` 必须恰好 1 行 `added`（`metrics/completionRate`）
   且既有行逐字未变；`compare-records.py run1 post1 --rename-table … --batch B2 --strict` 必须 `contract=0`、
   `rename=0`、`B2 satisfied`，结构差异只剩登记的 note。
5. **口径等价**：`PersistentRateCaliberTests` 两条自己跑一遍；如果要更强的形状，可以另造一个
   「一部分请求被应答、一部分超时」的运行，验证 `achievedRate/completionRate == sentRequests/responses`
   这个比值恒等式（本轮只钉了两个端点：全应答与全不应答）。
6. **裁定 §1.4 的 REL 桶问题**：teardown 尝试是否值得一个显式的非观测键（代价：改分析器恒等式 +
   新发布键 + 契约登记），还是保持「显式 status + 留在 catch-all 桶」。
