# E3-a 回归比对与定向证据（MIX tracker 并发契约 + `#4`/`#5`/`#7`/`#10`）

本批是 E3 的第一批（`10-07-e2e-e3-semantics/implement.md` 的 E3-a 节）：把 `MixUdpLoop` 的接收任务
改成**只投递 settlement**、发送侧在配速点 drain 结算（D19.3 F 的钉死形状），并为 `#4`/`#5`/`#7`/`#10`
补上定向证据与穷举。**`src/` 一行未改，本轮未提交。**

判定线：记录侧**结构差异为空**（没有新键、没有新 note、没有改发布值）、run1 对照**契约栏零越带**、
D18.5 #12 的零宽键逐值相同；同一二进制噪声地板（post1 → post2）的 13 条差异**全部是 E2 已登记的
"改名后无带宽路径"**，即本批没有引入超出噪声的契约差异。六条门禁全绿。

> **D19.3 G**：本批**不 assert `clientSendLoss`**（该值在 E3-b 的 `#12`/D7 批次里变）。本文件与
> 本批新增的任何测试里都没有 `clientSendLoss` 断言；下面引用的 `clientSendLoss=0` 只是 selftest 的
> 打印读数，不作为判据。

---

## 0. 结论一句话

`compare-records.py` 对 run1（冻结基线）与两个 selftest 运行给出
**structural=0 / conditional=0 / identity=0 / contract=0 / rename 0**（`exit 0`）；同一二进制的
post1 → post2 自身是 `contract=13`，且 13 条**全部**是 `jitter-band.json` 里没有新拼写的
"no recorded jitter band for this path"；D18.5 #12 的零宽键在 run1 / post1 / post2 三次运行之间
**39 个 (记录, 路径) 对 / 19 条路径逐值相同**（`diff` 全空）。七条反证的突变全部实测（逐条红在哪条
断言上见 §4），还原后哈希逐字相同。

---

## 1. 本批的改动面

### 1.1 MIX 的 tracker 并发契约（唯一的发布面改动面）

| 文件 | 变化 |
|---|---|
| `benchmarks/WinForward.E2E/Client/Arms/MixUdpBook.cs` | **新增**（126 有效行）：`MixUdpBooking`（`Verdict`/`ReceiveFailed`）、`MixUdpSettlement`（record struct）、`MixUdpBook`（接收面 `Offer`/`OfferReceiveFailure`；发送面 `BookSupplied`/`BookSent`/`BookSendRefused`/`BookSendFailure`/`Settle`；witness `Pending`/`Settled`/`SettlesUnderLoad`/`ResolvedOnSettle`/`OffersAlreadyResolved`/`OfferThreadId`/`SettleThreadId`） |
| `benchmarks/WinForward.E2E/Client/Arms/MixUdpLoop.cs` | `pending`（`ConcurrentDictionary`）与 `BookDatagram` 删除；接收任务只 `Classify` + `Offer`；发送循环在 `Pacer.WaitUntil` 之后、下一次 `SendDatagramAsync` 之前 `book.Settle()`；drain 循环每次 `Settle()` 后再看 `Pending`；`await receive` 之后**追加最后一次 `Settle()`** |
| `benchmarks/WinForward.E2E/Client/UdpReliability.cs` | 只有类文档：新增 `<remarks>` 逐字写明单写者契约（哪些方法只属于发送侧、接收侧只能投递、`WasSent` 也在发送侧、`Classify`/`Retire` 属于 join 之后的收尾读）。**记账与公式一行未改**（D0.3 的约束） |

**旧行为 → 新行为（逐点）**，行号是改动前的 HEAD（`f8b31c6`）：

| 旧 | 新 |
|---|---|
| `MixUdpLoop.cs:151` 接收线程直接调 `BookDatagram`（`context`、`tracker`、`pending` 全在接收线程手上） | 接收任务只 `book.Offer(verdict, arrivedTicks)`；**接收任务只拿到 book、拿不到 tracker**，`Offer`/`OfferReceiveFailure` 不触碰 `_tracker`，结构上无法记账（check 轮更正措辞：`MixUdpBook` 本身有 `_tracker` 字段，发送面才用它） |
| `MixUdpLoop.cs:184` 接收线程读 `tracker.WasSent` | `MixUdpBook.Book`（`Settle` 内）读；`WasSent` 只出现在发送侧 |
| `MixUdpLoop.cs:185` 接收线程 `pending.TryRemove`（RTT 也从这里记） | `Settle` 内 `_pending.Remove` + `_rtt.Record`；`_pending` 从 `ConcurrentDictionary` 降为普通 `Dictionary`（单写者） |
| `MixUdpLoop.cs:194/199/204/208/211` 接收线程写 `MarkArrival`/`MarkCorruptWithKnownSequence`/`MarkForeignConnection`/`MarkUnmatchedReply`/`MarkCorrupt` | 同一个 switch，但只在 `Settle` 内（发送侧）执行 |
| `MixUdpLoop.cs:160` 接收线程 `tracker.MarkSendFailure()`（socket 失败） | `book.OfferReceiveFailure()` 投递 `ReceiveFailed` settlement，由 `Settle` 记 `MarkSendFailure` |
| 发送线程在 `:121` `MarkSent`、`:120` `MarkSupplied`，与接收线程的上述写入**并发** | 发送侧（`BookSent` 先于 socket 调用）与接收侧只通过队列交接；tracker 回到单写者 |

**等价性论证**：settlement 带的 `ArrivedTicks` 是接收时刻（不是结算时刻），所以 `MarkArrival` 的年龄、
`Reordered`、RTT 样本值都与旧路径逐字相同（只改了"谁写"）；`Classify` 仍由臂线程在全部 lane join 之后调用。
唯一需要新增的动作是 **join 之后的最后一次 `Settle`**（check 轮实测更正：干净 loopback 的 selftest 里 `DrainPendingAsync` 已把队列清空、删掉该行读数不可见——它覆盖的是 **drain 以 horizon 退出**与 **offer 循环抛 `SocketException`** 的路径，不是干净路径的数值变化）：旧路径里"最后一次配速点到 socket 关闭之间到达的
应答"由接收线程直接入账，新路径里它们还在队列里，不补这次结算就会**丢已到达的样本**（是数值变化，不是清理）。

**时间刻度差异（登记为边界，不是缺陷）**：drain 循环现在必须自己 `Settle` 才能看到 `Pending` 变小，
退出点因此可能比旧的"接收线程直接删表"晚一个 `Task.Delay(1)`（≤1 ms）。它只可能把
`observationEnds` 往后挪 ≤1 ms，从而让极少数落在 `发送时刻 + W` 边界上的槽位由 `abandonedAtTeardown`
翻到 `never`。run1 → post1 的 `contract=0` 说明这次 selftest 里没有发生；登记为已知边界，E3 判据轮
（500 rps、更长时长）里若出现单槽差异，按"1 ms 结算滞后"解释。

### 1.2 `LossWindow.Admit`（`#4` 的可注入发送路径）

`benchmarks/WinForward.E2E/Client/Arms/LossWindow.cs`（**新增**，23 有效行）：把 `LossArm.cs:168-175`
的三句（`MarkSupplied` → `Retire` → `Outstanding >= window` 判定 + `MarkWindowOverflow`）原样搬成一个
命名单元，调用顺序逐字不变；`LossArm` 侧变成 `if (!LossWindow.Admit(tracker, Clock.Now, windowTicks, window)) continue;`。
这样 `#4` 的仿真能驱动**臂自己的准入决定**，而不是测试里再抄一份判定。

---

## 2. 三次运行与判定比对

运行目录：`run1` = 冻结基线（`research/baseline/run1`）；`post1`/`post2` = 本批发布后的同一二进制连跑两次。
两条都是 `scripts/selftest.sh scripts/plans/selftest-plan.json`（本地靶机、无产品），工作目录沿用
`/tmp/wf-bench/selftest`，发布目录 `WF_PUB=/tmp/e3a/pub`。

```console
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 <run> \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
```

| 对照 | structural | conditional | identity | contract | declared | readings | exit |
|---|---|---|---|---|---|---|---|
| run1 → post1（**判据**） | 0 | 0 | 0 | **0** | 27 | 288/366（212 越带） | 0 |
| run1 → post2（复核） | 0 | 0 | 0 | **0** | 27 | 297/366（207 越带） | 0 |
| post1 → post2（同二进制噪声地板） | 0 | 0 | 0 | 13 | 0 | 288/366（116 越带） | 1 |

- 两条 run1 对照的 `contract=0`：**没有一个契约计数/布尔/参数越带**，`compared=1160 measured=878`。
- 同一二进制对的 13 条**全部**是同族：`jitter-band.json` 冻结在 B2 改名之前，键集不含新拼写
  （`metrics/latency/{tcp,udp}.sent`、`metrics/udp.sent`、`metrics/desktops/udp.{sent,arrived,foreignConnection}`、
  `metrics/udp.lossRate`），与 `E2a3-vs-run1.md` §2.2 登记的 13 条同族同数。
- 改名表：`table: 592 entries = added 3, identical 580, renamed 9`；`B2: 9/9 satisfied`；
  `declared additions not observed: detail, error`（E2 的错误记录字段与本批无关，`planSource` 已 observed）。
  **本批没有新增发布键**，所以不需要往 `contract-rename.json` 的 `added` 里登记（D19.3 A 不触发）。
- `--strict` 读数摘要是信息性的（读数永不判败）：同一对基线两次运行就有 212 → 207 的越带条数差，
  说明读数越带是宿主噪声（CPU/内存采样、本机时间片），不是本批的信号。
- selftest 读数抽样（post1）：LOSS `sent=2001 arrived=2001 windowOverflow=0 abandonedAtTeardown=0`；
  MIX `udp.sent=602`（每 desktop 301）、`desktops[].udp.arrived=301`、`udp-rtt n=602`。

---

## 3. D18.5 #12 的零宽发布键：三次运行逐值相同

口径：`metrics/*.received`、`metrics/*.unmatchedReplies`、`metrics/*.outstandingAtTeardown`、
`latency/<mode>/count`（本文件的口径还含 `latency/tcp-connect/count`，是 D18.5 #12 四模式的超集）。

```console
$ for d in $R/baseline/run1 /tmp/e3a/post1 /tmp/e3a/post2; do python3 zerowidth.py $d/out > zw-$(basename $d).txt; done
$ diff zw-run1.txt zw-post1.txt && diff zw-run1.txt zw-post2.txt
（无输出）
```

- **39 个 (记录, 路径) 对 / 19 条不同路径**，三次运行之间 `diff` 全空。
- 代表值：`LAT` tcp `received` 322 / udp 161、`LATLOAD` 两向各 1601、`BASE latency/*` 各 101、
  `DNS`/`DNSALT` `dns-rtt/count` 402、`MIX` `dns-rtt 8`/`tcp-rtt 146`/`udp-rtt 602`、
  `PERSIST` `tcp-rtt/count` 15；全部 `outstandingAtTeardown` 与全部 `unmatchedReplies` 为 0。
- `MIX latency/udp-rtt/count = 602` 与 `MIX metrics/udp.sent = 602` 相等且与 run1 相同：这一条正是
  "RTT 仍在到达时刻采样、每个到达恰好消费一个 pending 槽"的读数证据（值一样，只是采样线程换了）。

---

## 4. 定向证据与穷举（`#4`/`#5`/`#7`/`#10`）与七条反证

新增/扩展的用例（`dotnet test tests/WinForward.E2E.Tests -c Release`，276 条全绿，E2-d 的 264 + 12）：

| 条目 | 用例 | 断言要点 |
|---|---|---|
| 并发契约 | `MixUdpBookConcurrencyTests.EveryDatagramOfferedByTheReceiveThreadIsSettledExactlyOnceByTheSendSide` | 1000 轮 × 8 条：`delivered == sent == settled == 8`、`SettlesUnderLoad ≥ 1`、`ResolvedOnSettle == 8`、`OffersAlreadyResolved == 0`、`Pending == 0`、`Outstanding == 0`、`Arrived == 8`、`Duplicate/Reordered/Corrupt/Unmatched == 0`、`rtt.Count == 8`、`OutOfRange == 0`、收发线程 id 不同且发送侧就是调用线程 |
| 并发契约（竞态） | `…AReplyThatBeatsItsOwnSendIsStillResolvedByTheSendSide` | 同上，但应答先上线再 `BookSent`：应答"先于它自己的发送记录"到达时仍必须由结算路径判定为 Arrived（暂存第 1 条，确定性） |
| `#4` | `LossWindowSimulationTests.TheWholeScheduleIsSentWhateverThePathDrops(0/0.10/0.50)` | `Supplied == 500×30+1 == 15001`（**精确值，不是 `>0`**）、`Sent == Supplied`、`WindowOverflow == 0`、首末槽位都发出、`Arrived == 已送达`、`Never == 丢包数`、`Late/Undetermined == 0` |
| `#4` 负控 | `…AWindowThatNeverReleasesStopsTheClientAndSaysSo` | `WindowOverflow > 0`、`Sent < Supplied`、末槽位没发出（与上一条同形反证） |
| `#5` | `UdpReliabilityTrackerTests.AnIndexTheWindowRefusedIsNeverClassifiedAsLost` | **前置** `WindowOverflow > 0`（=3）且 `Supplied=5 SentOk=2`；被拒的 2/3/4 号**低于**最高已发序号 5；`Never == 2`（只有真发出的两条）、恒等式合计 2 |
| `#7` | `…AClientStallIsCountedAsLateAndNeverRatherThanSubtracted` | `MarkSent(1,0)`/`MarkSent(2,0)` 之后 `MarkArrival(1, 3W)`、收尾在 `W+1`：`Late == 1`、`Never == 1`、`Arrived == 0`、`Undetermined == 0` |
| `#10` | `UdpArrivalOrderTests.EveryArrivalOrderOfAllSixSequencesCountsExactlyItsOwnDescents` | **720** 个全排列，逐序断言 `Reordered == 该序列自身的下降数`（公式，不是查表） |
| `#10`（1956 口径） | `…TheAuditArrivalPopulationIs1956OrderedSelections` | 审计口径 1..6 的**有序选取**：`6+30+120+360+720+720 = 1956` 条，同一公式 |
| `#10` 单列 | `…AnAscendingArrivalOrderIsNeverReordered` / `…ArrivingOneThreeTwoIsOneReorder` | 升序 `Reordered == 0`；`(1,3,2)` 恰为 1 |

### 4.1 七条反证（逐条实测；`/tmp/e3a/mutate.py` 打补丁 → `dotnet test --filter` → 还原 → 校验哈希）

M3–M6 由 `/tmp/e3a/mutate.sh` 一轮跑完（结果与哈希见 `/tmp/e3a/mutations.txt`）；M1/M2 在
inspectcode 处置（§5.1：witness 改自动属性）之后**在新树上重跑过一遍**，报文与下表逐字一致，
下表哈希是重跑后的值。

| # | 突变 | 预期红的断言 | 实测 |
|---|---|---|---|
| M1 | `MixUdpBook.Offer` 自己记账（`tracker.MarkArrival`，不入队） | `Settled == count` / `SettlesUnderLoad ≥ 1` / `Pending == 0` / `rtt.Count` | **两条并发用例全红**，报 `settled 0 … settled under load 0 … pending 8 … rtt samples 0`（`book.Settled == count` 那一行） |
| M2 | `Offer` 自行 `WasSent` + `pending.Remove` + 记 RTT（即旧代码形状）后入队**已解析**的 verdict | `ResolvedOnSettle == count`（暂存的第 1 条应答先于自己的 `BookSent`，早读必然答"没发过"） | **reply-first 用例红**：`resolved on settle 7 … arrived 7 … unmatched 1`（`ResolvedOnSettle == count` 那一行；另 `Pending==1`、`Outstanding==1` 亦红） |
| M3 | `LossWindow.Admit` 删掉 `tracker.Retire(...)` | 50% 用例的 `Sent == Supplied` / `WindowOverflow == 0` / 末槽位 | **50% 红**：`sent 7959, overflow 7042, last slot sent False`；0% 与 10% 仍绿（正是"丢包率跑不过窗口就看不出来"） |
| M4 | `Classify` 删掉 `if (!_sent.IsSet(sequence)) continue;` | `#5` 用例的 `Never == 2` | **2 条红**：`#5` 用例 `Expected 2 / Actual 5`，以及既有的 `ARefusedSendLeavesTheClassifiedPopulation` |
| M5 | `MarkSent` 用 `Clock.Now` 而不是传入的预定时刻 | `#7` 用例的 `Late == 1`（阻塞被减掉） | **4 条红**，其中 `#7` 用例 `Expected 1 / Actual 0`（Late），另 3 条既有恒等式用例 |
| M6 | `MarkArrival` 的排序判据换成"下一个期望序号" | 两个穷举用例 | **2 条红**：`arrival order [2]: reordered 1 (expected 0)`、`[1,2,3,5,4,6]: reordered 2 (expected 1)` |
| M7 | （探针）`windowTicks = long.MaxValue` 是否等价于"永不退窗" | —— | **不等价**：`1 + long.MaxValue` 运行期回绕为 `long.MinValue`，`Retire` 因此把**全部**槽位立即释放（`Outstanding 2 → 0`，即哨兵值等价于"窗口 0"，是负控的反面）；`sendTicks == 0` 时又不回绕、什么都不退 —— 行为取决于发送时刻是否为 0。见 §6-① |

**还原哈希**（`sha256sum`，突变前 == 还原后；`/tmp/e3a/mutations.txt`）：

| 文件 | 哈希 |
|---|---|
| `Client/Arms/MixUdpBook.cs`（M1、M2） | `5fd5963c05f1b18695c5a115e86bd8fd58abafbe9bc96ea10dfbda58a2a07b66` |
| `Client/Arms/LossWindow.cs`（M3） | `0d6bba9f1afe7defdfa96aca72ef2d90b16f1cbcec26545e56cd8f5c4ea822d9` |
| `Client/UdpReliability.cs`（M4/M5/M6） | `a86079ced6935067e1e639d2a2313e1b2d42a0a91d96602675d50b010e5c681c` |

**编译期反证（附带发现）**：M1 的第一次写法顺带删掉了 witness 字段的赋值 —— 树**根本编译不过**
（`CS0649` + `S3459` "Remove unassigned field `_offersAlreadyResolved`, or set its value"）。
也就是说"把 witness 悄悄摘掉"这条路被分析器堵住了，必须显式保留它才能构建。

---

## 5. 门禁（冻结树，逐条）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `WF_PUB=/tmp/e3a/pub scripts/publish.sh` | exit 0；linux/win/win-direct 三份产物；`linux/WinForward.E2E.dll` sha256 `a80d94b8b17a699246fabd63b49db39fd3ed5690d2a0065554c923d6482ce915` |
| 2 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| 3 | `dotnet test WinForward.slnx -c Release` | 全绿；`WinForward.E2E.Tests` **Failed: 0, Passed: 276**（E2-d 的 264 + 12）；其余五个测试项目同样 Failed: 0 |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 本批二进制**连续两次 exit 0**（post1/post2） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出**（首跑即空） |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e3a/jb-inspectcode-final.xml WinForward.slnx` | 解析 XML：`<Issue>` **0**、`<IssueType>` **0**、无 `CSharpErrors`（首跑 17 条已在 §5.1 全部采纳修好，随后整套门禁重跑） |

### 5.1 inspectcode 首跑（17 条，全部落在本批新增文件）与处置

首跑（`/tmp/e3a/jb-inspectcode.xml`）在整个解决方案上只有 17 条，且**全部**在本批新增的文件里
（既有树 0 条）。逐条按 spec 的裁决顺序（能改好就改好，抑制只作最后手段）：

| 规则 | 条数 | 位置 | 处置 |
|---|---|---|---|
| `ConvertToAutoPropertyWhenPossible` | 4 | `MixUdpBook.cs` 的 `Settled`/`SettlesUnderLoad`/`ResolvedOnSettle`/`OffersAlreadyResolved` | **采纳**：改成 `{ get; private set; }` 自动属性（语义不变，写入点仍全在类内；witness 仍不发布） |
| `MergeIntoPattern` | 10 | 三个测试文件里 `a == 0 && b == 0` 的布尔链（`LossWindowSimulationTests` 2、`MixUdpBookConcurrencyTests` 5、`UdpArrivalOrderTests` 3，按被建议合并的 `&&` 计） | **采纳**：改成属性模式 `counts is { Late: 0, … }` 与 `scenario is { FirstSlotSent: true, … }`，可读性更好 |
| `AccessToDisposedClosure` | 3 | `MixUdpBookConcurrencyTests`：lambda 捕获了外层 `using` 的 `stop`（CTS）与两个 `ManualResetEventSlim` | **采纳**：两个信号改用 `TaskCompletionSource`（不可释放类型），CTS 移到接收线程内部 `using`（令牌与线程同生共死，外层不再释放它） |

处置后**整套门禁重跑**（build/test/effective-lines/format/publish/selftest ×2/三条比对/零宽键/反证里
受影响的两条 M1/M2），并在冻结树上重跑 inspectcode（§5 第 6 行）。M1/M2 的目标文件哈希因此变为上表
的新值，两条反证的结论与报文与 §4.1 记录的逐字一致。

另：`effective-lines.py` 在 `benchmarks/WinForward.E2E`、`benchmarks/WinForward.E2E.Contracts`、
`tests/WinForward.E2E.Tests` 三项目上**无输出、exit 0**（新增文件 `MixUdpBook` 126 有效行、
`LossWindow` 15、`MixUdpBookConcurrencyTests` 122（check 轮修复 worker 线程未处理 OCE 后 +6）、`LossWindowSimulationTests` 103、
`UdpArrivalOrderTests` 102、`MixUdpLoop` 137、`UdpReliability` 273、`UdpReliabilityTrackerTests` 188）。

---

## 6. 偏离与登记

1. **`windowTicks = long.MaxValue` 的负控换成 `long.MaxValue / 2`**（见 §4.1 M7；check 轮限定：仿真的首个槽位 `sendTicks == 0` 时 `Retire` 在 sequence 1 就返回、**碰不到回绕**，所以裸 `long.MaxValue` 在这种形状下照样绿——回绕只在首个发送时刻 ≠ 0 时发生）：
   `Retire` 的比较是 `_sendTicks[sequence] + windowTicks > nowTicks`，`long.MaxValue` 会回绕成负数，
   于是"永不退窗"变成"全部立即退窗"。测试里的 `NeverRetireWindowTicks = long.MaxValue / 2`（≈146 年，
   任何实际时钟都到不了，且不与 `sendTicks` 相加回绕），负控因此保持原意（`WindowOverflow > 0` 且
   `SentOk < Supplied`）。**登记为观察项**：`lossWindowMs` 的 plan 上界是 `int.MaxValue`（≈24.8 天），
   对应的 `windowTicks` ≈ 2.1e12，离回绕还差 6 个数量级，所以生产面到不了；但"哨兵值 = 永不"这条
   直觉在本类的算式里是错的，后来者不要用 `long.MaxValue` 表达它。
2. **`#4` 的期望槽位是 `rate × seconds + 1`（15001），不是 15000**：`LossArm` 的循环条件在配速等待**之前**
   判断（`while (Clock.Now < deadlineTicks)`），所以"预定时刻恰好落在 deadline 上"的那一槽仍会被提供并发出。
   用例按臂的真实循环（同一个 `Pacer`）仿真，断言这个精确值。
3. **`#4` 引入了一处生产面搬移**（`LossWindow.Admit`）：父任务要求"可注入的假发送路径"，本轮选择把
   **准入决定**抽成可调用单元（顺序逐字不变），而不是给 `LossArm` 加 transport 接缝。发送本身仍是
   仿真里的假路径（丢包按种子随机），不碰真实网络。
4. **并发测试的被测单元是 `MixUdpBook`，不是 `MixUdpLoop` 本体**：MIX 没有 transport 接缝
   （D19.2 ①：不并入 `LaneEngine`），所以用例用"发送侧同步跑在测试线程 + 接收侧独立线程"驱动 book，
   与 `LaneEngineConcurrencyTests` 用假 transport 驱动引擎是同一手法。`MixUdpLoop` 里
   "配速点结算 / drain 里结算 / join 后最后一次结算"这三处接线由阅读 + selftest 覆盖（**登记为需要
   check 独立复核的点**，见 §7）。
5. **零宽键口径**：本文件用 39 个 (记录, 路径) 对 / 19 条路径（含 `latency/tcp-connect/count`），
   是 D18.5 #12 四模式的超集；E2-d 记录的是 34 对 / 18 条（更严的匹配）。两者的交集值逐条相同。
6. **本批不改发布值**：`MixArm` 的 note 一条未动（note 数组进结构比对，加 note 会被判成结构差异）；
   `UdpReliabilityTracker` 的记账与公式一行未改；没有新增/改名任何 JSON 键。
7. **未做（按范围）**：`#12` 四臂派生、D7 记账、`#6`/`#8`/`#9`/`#11`/`#19`（E3-b）、`Truncated`（E3-c）、
   账本/CLI（E3-d）、ODE/`achievedRate`（E3-e）。`#4`/`#5`/`#7`/`#10` 本批只补回归护栏，机制未动
   （`#5`/`#7`/`#10` 的"已实现"结论见 `E3-premises.md` §1）。

---

## 7. 给 check 轮的独立复核点

1. **`MixUdpLoop` 的三处结算点是否真的够**：配速点、drain 循环、`await receive` 之后各一次。
   反证方式：删掉 join 之后那次 `Settle()`，跑一次 selftest，看 `MIX latency/udp-rtt/count` 是否
   低于 `udp.sent`（读数级证据，不依赖本文件的断言）。
2. **drain 的 ≤1 ms 结算滞后**（§1.1）在判据轮（E3-b/c 的长时 selftest 或 campaign）里是否产生
   单槽 `abandonedAtTeardown → never` 翻转；若有，按"1 ms"解释并核对是否与 `Pending` 归零同时发生。
3. **MIX 侧 witness 的语义**：`SettlesUnderLoad`/`ResolvedOnSettle`/`OffersAlreadyResolved` 都是
   `internal` 且**不发布**（不在任何 `ArmKeys`/记录里）；确认它们没有意外出现在结构比对面上
   （`structural=0` 已证，但请独立 grep 一次写盘路径）。
4. **`LossWindow.Admit` 的搬移等价性**：与 `LossArm.cs:168-175`（旧行号）逐句对照，确认
   `MarkSupplied → Retire → 判定 → MarkWindowOverflow` 的顺序与短路位置没有变化。
5. **`#4` 的 15001**：独立推导一次（`Pacer.IntendedTicks` 的首次为 `start + 0`，循环条件先于等待），
   确认 `+1` 不是测试为了通过而凑的数。
6. **inspectcode 的 XML**：`/tmp/e3a/jb-inspectcode-final.xml` 解析出的 `<Issue>` 与 `<IssueType>` 均为 0
   （冻结树实测；首跑的 17 条与其处置见 §5.1）。

---

## 8. 重建方式（check 轮可逐条重跑）

```console
R=.trellis/tasks/10-07-e2e-harness-refactor/research

python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
    benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests        # 无输出、exit 0
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0、空输出
dotnet build WinForward.slnx -c Release                                  # 0/0
dotnet test WinForward.slnx -c Release                                   # E2E 276 绿
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3a/pub scripts/publish.sh
rm -rf /tmp/wf-bench/selftest && WF_PUB=/tmp/e3a/pub scripts/selftest.sh scripts/plans/selftest-plan.json
#   第二次跑后把 out/ ledger.jsonl target.out console 收成 post1/post2（见 §2）

python3 scripts/compare-records.py $R/baseline/run1 /tmp/e3a/post1 --normalize $R/record-normalize.json \
    --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 --strict
python3 scripts/compare-records.py /tmp/e3a/post1 /tmp/e3a/post2 --normalize $R/record-normalize.json \
    --band $R/baseline/jitter-band.json --strict            # 同二进制噪声地板：contract=13，全为 no-band

# 七条反证：补丁表是 /tmp 的一次性脚本（未留树），突变内容已逐条写在 §4.1，可按表手工重放
jb inspectcode -f=Xml -e=HINT -o=/tmp/e3a/jb-inspectcode.xml WinForward.slnx
```

## 9. check 轮（E3-a）

- **阻塞级修复**：`MixUdpBookConcurrencyTests` 的接收 worker 用 `GetConsumingEnumerable(token)` 且无 `catch`——
  令牌取消时它**抛** `OperationCanceledException` 而不是干净结束，任何被断言提前放弃的轮次都会在 10 s 预算到点后
  崩掉 test host（真实发生过：`Test host process crashed`，E2E 只报 272/276）。修法：包 `try/catch (OperationCanceledException)`
  （+6 有效行，语义不变）；确定性反证（必败断言 + 14 s 脚手架）在修前崩、修后干净失败（`Failed:1 Passed:2 Total:3`）。
  该课已写进 `.trellis/spec/backend/test-stability.md`。
- check 轮独立复现：单写者负控（`Offer` 自行 Resolve）→ 两条并发用例全红；`LossWindow.Admit` 与 HEAD 逐句等价
  （删 `Retire` ⇒ 50% 红、0/10% 绿）；`#4` 的 `15001` 按四种 `Stopwatch.Frequency` 重算一致；`#10` 的 720/1956 自算一致；
  `contract=0` 两次复现、零宽键零 diff；六条门禁全绿（publish 的 dll 哈希与本文档逐字相同）。
- 未复现项（如实登记）：删掉 join 后 `Settle()` 的读数反证**没有复现**（见上）；裸 `long.MaxValue` 负控在
  `sendTicks == 0` 的形状下仍绿（见上）。
