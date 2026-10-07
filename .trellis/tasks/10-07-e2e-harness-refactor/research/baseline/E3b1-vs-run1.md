# E3-b1 回归比对与定向证据（`#12` 五臂派生 + D7 记账 + `#6`/`#8`/`#9`）

本批是 E3 的第二批（`10-07-e2e-e3-semantics/implement.md` 的 E3-b 节，客户端侧那半；分析器侧的
`#11`/`#19`/`check-fairness.py` 归 E3-b2）。**`src/` 一行未改，本轮未提交。**

判定线（逐条见 §5）：结构面**只有 §4 登记的三条 note 更正**，其余结构面（键集 / arity / 其它字符串 /
`target.out`）为 0；契约栏 `contract=0`（零越带）；D18.5 #12 的零宽键 39 对 / 19 条逐值相同；同一二进制
噪声地板 16 条 = E2 已登记的 13 条"改名后无带宽路径" + 本批 3 个新键的"无带宽"；`--strict` 读数摘要见 §5.4；
六条门禁全绿（§5）。

> **E3-a 的登记已解除**：`E3a-vs-run1.md` 写明"本批不 assert `clientSendLoss`（该值在 E3-b 变）"。
> E3-b1 正是那一批：五臂的门现在都由各自的计数器派生，并且**每臂都有"驱动到非零"的用例**（§2）。

---

## 0. 结论一句话

`compare-records.py` 对 run1（冻结基线）与两个 selftest 运行给出 **contract=0 / rename=0 / conditional=0 /
identity=0，structural=3**（`exit 1`，三条全是 §4 登记并逐字列出的 note 更正）；同一二进制的
post1 → post2 是 `contract=16`，其中 13 条与 `E2a3/E2b1/E2b2/E2c/E2d/E3a` 的噪声地板**逐行同族**
（`jitter-band.json` 冻结在改名之前的"no recorded jitter band for this path"），另 3 条是 §5.3 登记的
**新键无带宽**；D18.5 #12 的零宽键在 run1 / post1 / post2 之间 `diff` 全空；`#12`/D7/`#6`/`#9` 的九条
反证全部实测（§3），其中一条（REL 的**调用点**写成字面量）在第一次跑时**没有被抓到**，因此补了第二个
装置并登记（§2.4、§6-2）。

---

## 1. 本批的改动面

### 1.1 `#12`：五臂的 `gates/clientSendLoss` 由各自计数器派生

| 臂 | 公式（生产代码位置） | "驱动到非零"的反证 |
|---|---|---|
| `LOSS` | `LossArm.ClientSendLoss(tracker, counts) = tracker.SendFailure + tracker.WindowOverflow + counts.Undetermined + tracker.SentOutOfRange`（`Client/Arms/LossArm.cs:37` 调用、`:75` 定义；`metrics.clientSendLoss` 与 `gates.clientSendLoss` 同源） | 四项各驱动 1 → `== 4`（`TheLossArmGateIsEveryWayTheClientDestroysADatagram`）；删掉越界项 → `Expected 4 / Actual 3`（M4） |
| `MIX` | `MixMetricsWriter.UdpTotals.Add`：`_clientSendLoss += tracker.SendFailure + tracker.WindowOverflow + counts.Undetermined + tracker.SentOutOfRange`（`Client/Arms/MixMetricsWriter.cs:53`），与 LOSS 同式（D19.2 ⑥） | 同一个 tracker 经 `MixMetricsWriter.WriteMetrics` 折出 `clientSendLoss == 4`、`classes.udp.clientSendLoss == 4`、`metrics.clientSendLoss == 4`；删掉越界项 → `Expected 4 / Actual 3`（M5） |
| `DNS` | `metrics.Unsent`（`Client/Arms/DnsArm.cs:58`；`socketErrors` **不**入分子，D19.2 ④） | 黑洞 UDP（绑了端口、无人应答）逼出 `metrics.Unsent > 0`（实测 545）→ 门 `== metrics.Unsent`；门改回字面量 `0L` → `Expected 545 / Actual 0`（M1） |
| `THRU` | `metrics.SendFailures`（`Client/Arms/ThroughputArm.cs:126`） | 监听器 accept 后读到命令帧即 RST → `metrics.SendFailures > 0`（实测 2）→ 门 `== metrics.SendFailures`；门改回 `0L` → `Expected 2 / Actual 0`（M2） |
| `REL` | `ReliabilityMetricsWriter.ClientSendLoss(metrics.ScheduledAttempts, metrics.ConnectAttempts) = max(0, scheduled − attempts)`（`ReliabilityArm.cs:66` 调用、`ReliabilityMetricsWriter.cs:15` 定义） | 真跑一臂断言两项相等且门 `== ClientSendLoss(...)`；**伪造** `(5,2) == 3`、`(7,7) == 0`；调用点写成 `0L` → 源码装置红（M3b），两参数互换 → 源码装置红（M3c） |
| `IDLE` | **结构恒零**（`IdleArm.NoTrafficClientSendLoss = 0`，`IdleArm.cs:15`） | 无发送侧计数器可驱动（D19.3 E）：用例断言的是"这门为零是因为这条臂没有可丢的样本"——`Gates` 恰好两个键、`Metrics` 是 `IdleMetrics`（无发送/窗口/到达计数）。分析器**不渲染** IDLE 的这个门（§3.2 的合法性表只取 LOSS/LAT/LATLOAD/BASE），所以记录里既没有误导的 `0` 也没有伪造的 `n/a`；README 的旧措辞登记给 E5（`E5-readme-rows.md` §1） |

`BASE` 的 `gates.clientSendLoss` 仍是两个相位门之和（`ControlArm.cs:81`），因此自动继承 `LOSS` 与 `LAT` 的新口径。

### 1.2 D7：`SentOutOfRange` 与 `OutOfRange` 分开发布

| 面 | 内容 |
|---|---|
| 发送侧 | `UdpReliabilityTracker.SentOutOfRange => _sent.OutOfRange`（`Client/UdpReliability.cs:215`）；`MarkSent` 的守卫里 **`_sent.TrySet(sequence)` 保留**（`:241`，D2 的硬约束），因此越界槽仍进位图、仍被计数 |
| 接收侧 | `UdpReliabilityTracker.OutOfRange => _arrived.OutOfRange + _corruptAt.OutOfRange`（`:207`）——只剩接收侧两类拒绝 |
| 发布键（新，只增） | `ArmKeys.Loss.SentOutOfRangeSequences = "sentOutOfRangeSequences"`、`ArmKeys.Mix.UdpClass.SentOutOfRangeSequences`；`LossMetrics.SentOutOfRangeSequences`、`MixUdpClassMetrics.SentOutOfRangeSequences` 都是 `required`（显式工厂不得不赋值，D19.2 ⑬/D5 手法），写在 `outOfRangeSequences` 之后 |
| 发布面 | `metrics/sentOutOfRangeSequences`（LOSS）、`metrics/loss/sentOutOfRangeSequences`（BASE 的 loss 相位，同一个 `LossMetrics`）、`metrics/classes/udp/sentOutOfRangeSequences`（MIX） |
| 公式 | LOSS 与 MIX 的 `clientSendLoss` 各加该越界项（§1.1）；`clientSendLossRate` 的分母仍是 `supplied` |
| 两侧分离的证据 | `ASendPastTheBoundedSequenceSpaceIsRefusedWithoutTouchingTheArrays`：`MarkSent(MaxSequence+1)` ⇒ `SentOutOfRange == 1` **且 `OutOfRange == 0`**、`SentOk == 0`、`Outstanding == 0`、分类桶全 0；`ACorruptDatagramNamingAnImpossibleSequenceIsRefusedAndCounted`：`MarkCorruptWithKnownSequence(long.MaxValue)` ⇒ `OutOfRange == 1`（接收侧）；两个事实合起来说明两边**不是**同一个数。**反证**：把 `OutOfRange` 换回两侧并集 → 第一条 `Expected 0 / Actual 1`（M6） |

### 1.3 记录里的 note（**本批唯一允许的结构差异**，见 §4）

D2 明写"同步改 `LossArm.cs` 的 Note"（E5 负责 README，实现者负责 Note）。D7 拆分后旧句**变成假的**：
`outOfRangeSequences` 的旧说明写"a non-zero value means part of the offered schedule was never tracked"，
而拆分后该键只数**接收侧**拒绝（有到达/损坏帧命名越界序号，提供的调度完全可以全部被跟踪），"从未被跟踪"
那一半搬到了新键 `sentOutOfRangeSequences`。因此改了 3 条 note（LOSS 的 1 条、MIX 的 2 条）。

### 1.4 `#6`：plan 声明的 `lossWindowMs` ⇒ `metrics.window`

- 新增 plan fixture `tests/WinForward.E2E.Tests/Fixtures/plans/loss-window-declared.json`
  （`lossWindowMs: 137`，刻意**不是** 200 ms 默认值）。
- 用例 `LossWindowPlanTests.TheDeclaredLossWindowIsTheWindowTheRecordPublishes` 走
  **plan 文件 → `PlanFile.TryLoad` → `ArmDispatch` → 真跑 LOSS 臂**，断言 `metrics.window`、
  `parameters.lossWindowMs`、`gates.windowMs` 三者都等于 137（D19.2 ⑱；`{loss,mix}` 口径，
  base 透传不在此判据内）。
- **反证**：把 `LossArm` 的 `spec.LossWindowMs > 0 ? spec.LossWindowMs : Default` 改成恒用默认
  → `Expected 137 / Actual 200`（M7）。

### 1.5 `#8`：`windowOverflow > 0` 时该臂延迟格子渲染 `n/a (windowOverflow > 0)`

- 分析器新增 `WINDOW_OVERFLOW_CELL`（`analyze.py:242`）与 `window_overflow_reached(ctx, row_id, arm_name)`
  （`:3701`，就读该臂自己的 `gates/windowOverflow`）；`table_latency` 在"该格本来会印数字"的前提下把
  count 与全部百分位换成该串（`:3770`）。**不改** `DeferredQueued→windowOverflow` 接线（D18.5 #7），
  也不改 gate 语义（D9）。
- 渲染优先级登记：`not-in-plan` / `not-carried` / "该臂没有这个直方图"（如 `LAT` 的 `dns-rtt`）**先于**
  caveat——一个类根本没有测量，ceil 不是它缺失的原因（§3.2 的三态）。
- `synthetic/make_tree.py` 新增 `--window-overflow ROW`（`LAT` 臂在 pass3 报
  `gates.windowOverflow = 3`，且 `supplied == sentOk`、`clientSendLoss == 0`：**延期不是丢弃**，
  这正是真实形状）。默认树不含任何越界 ⇒ 默认输出逐字节不变（§3.4）。
- **反证（D19.3 J）**：`value > 0` → `value >= 0` 的突变体在**零**越界的默认树上把
  36 个格子（`wf-aot-opt`/`wf-fdd-opt`/`proxifyre` 等的 LAT/LATLOAD/BASE × 3 pass 聚合）全部翻成
  `n/a (windowOverflow > 0)`，`tables.md` 变动 **72 行**（§3.4）。
- README 同步改了 `benchmarks/WinForward.E2E/README.md:421`（`gates/inFlightCeilingMs` 行，D19.2 ⑲ 指定的
  `:550 → :421`）；其余受影响的行登记进 `research/semantic-fixes/E5-readme-rows.md`（D19.2 ②）。

### 1.6 `#9`：臂末尾在途请求被采样（TCP 直方图样本数上升）

- `LaneTransportFake` 新增 `PayloadAtReceive` / `PayloadNotBeforeTicks`：把该 lane 的**唯一**一条应答
  暂存到"offer 循环 deadline 之后 20 ms"才交付（延迟是 `Task.Delay(..., token)`，引擎 join 时会取消它）。
- 用例 `LatencyArmDrainSamplingTests.AReplyThatLandsAfterTheOfferLoopStillReachesTheTcpHistogram`：真
  `LatencyTcpPolicy` + `LaneEngine<LaneTransportFake>`，drain 上限 0.2 s ⇒ `state.Received == 1` 且
  `histogram.Count == 1`——这条样本**只能**由臂末尾那段"继续收、继续结算"的 drain 产生。
- **反证**：把 drain 上限改成 0（末尾不再有预算）→ `Expected 1 / Actual 0`（M8），即"直方图样本数上升"
  确实由 drain 造成。

---

## 2. 五臂的公式与"驱动到非零"结果（D19.3 I）

用例文件 `tests/WinForward.E2E.Tests/ClientSendLossGateTests.cs`（7 条，跑真实臂、真实折叠函数或源码装置）：

| 用例 | 驱动 | 实测读数 |
|---|---|---|
| `TheIdleArmPublishesAStructurallyZeroLossGate` | 无法驱动（结构恒零） | 门 `== 0`，且 `Gates` 键集恰好 `{clientSendLoss, windowMs}`、`Metrics` 是 `IdleMetrics` |
| `TheDnsArmGatesTheQueriesItsWindowRefusedToSend` | 黑洞 UDP（`RatePerSecond = 4000`、`seconds = 0.5`、`tcpPercent = 0`） | `sent > 0`、`unsent = 545`、门 `== 545` |
| `TheThroughputArmGatesTheFramesItsSocketRefused` | 读到命令帧即 RST 的监听器（`seconds = 1.0`、`streams = 1`、100 MB/s） | `streamConnects == 1`、`sendFailures = 2`、门 `== 2` |
| `TheReliabilityArmGatesTheScheduledSlotsNoAttemptRanFor` | 真跑一臂（无监听 → 全部 connectFail，`seconds = 1.0`、50/s） | `scheduledAttempts == connectAttempts > 0`、门 `== ClientSendLoss(...)`；另伪造 `(5,2) → 3`、`(7,7) → 0` |
| `TheReliabilityArmPublishesTheDerivedDifferenceRatherThanALiteral` | 源码装置（见 §2.4） | 调用点逐字为 `[Gates.ClientSendLoss] = ReliabilityMetricsWriter.ClientSendLoss(metrics.ScheduledAttempts, metrics.ConnectAttempts),` |
| `TheLossArmGateIsEveryWayTheClientDestroysADatagram` | `MarkSent(1)` + `MarkSendFailure` + `MarkWindowOverflow` + `MarkSent(MaxSequence+1)` + 接收侧越界 | 四项各 1 → `== 4`；`SentOutOfRange == 1`、`OutOfRange == 1`（两侧同时非零）、`SentOk == 1`、分类桶全 0 |
| `TheMixUdpClassGateIsEveryWayTheClientDestroysADatagram` | 同一个 tracker 走 `MixMetricsWriter.WriteMetrics` | `classes.udp.sent == 1`、`sentOutOfRangeSequences == 1`、三个 `clientSendLoss` 都 `== 4` |

### 2.4 REL 的**调用点**：一次真实的漏洞与它的第二个装置

第一次跑反证时 **M3（把 `ReliabilityArm` 的门改回字面量 `0L`）没有变红**：REL 的差在任何"把尝试跑完"
的运行里恒为 0（这正是它的语义，§6-2），所以"公式正确"与"调用点写字面量"在臂级观测上**不可区分**。
补的装置是 D19.3 I 允许的那么一条：用例读 `ReliabilityArm.cs` 的源码（空白折叠后）断言调用点是
派生表达式；M3 之后重跑为红（§3.1），伪造/互换两个参数（M3c）同样红。登记为"臂级事实 + 源码事实"
两半，缺一不可。

---

## 3. 定向证据与九条反证

### 3.1 反证（逐条实测；`/tmp/e3b1/mutate.sh`、`/tmp/e3b1/mutate2.sh`：打补丁 → build → `dotnet test --filter`
→ 还原 → 校验 `sha256`）

| # | 突变 | 预期红的断言 | 实测 |
|---|---|---|---|
| M1 | `DnsArm` 的门改回 `0L` | 门 `== metrics.Unsent` | **红**：`Expected 545 / Actual 0` |
| M2 | `ThroughputArm` 的门改回 `0L` | 门 `== metrics.SendFailures` | **红**：`Expected 2 / Actual 0` |
| M3 | `ReliabilityArm` 的门改回 `0L` | （第一次）**没红** → 漏洞，见 §2.4 | **绿**（登记），补 M3b 后闭合 |
| M3b | `ReliabilityArm` 调用点改回 `0L` | 源码装置 | **红**：`TheReliabilityArmPublishesTheDerivedDifferenceRatherThanALiteral` |
| M3c | `ClientSendLoss(Attempts, Scheduled)` 两参数互换 | 源码装置（臂级事实此时仍绿，如实登记） | **红**（源码装置）；臂级 `Passed: 1` |
| M4 | `LossArm.ClientSendLoss` 删掉 `tracker.SentOutOfRange` | `== 4` | **红**：`Expected 4 / Actual 3` |
| M5 | `UdpTotals.Add` 删掉 `tracker.SentOutOfRange` | MIX 的 `== 4` | **红**：`Expected 4 / Actual 3` |
| M6 | `OutOfRange` 换回两侧并集 | `OutOfRange == 0`（发送侧拒绝后） | **红**：`Expected 0 / Actual 1` |
| M7 | `LossArm` 的 `lossWindowMs` 恒用默认 | `metrics.window == 137` | **红**：`Expected 137 / Actual 200` |
| M8 | `#9` 的 drain 上限改 0 | `histogram.Count == 1` | **红**：`Expected 1 / Actual 0` |
| M9 | `analyze.py` 的 `value > 0` → `value >= 0` | 默认树（零越界）的渲染逐字节不变 | **红**：`tables.md` 变动 72 行 / 36 个格子 |

**还原**：六个生产文件的 sha256 突变前 == 还原后（`/tmp/e3b1/mutations.txt`、`mutations2.txt`）。

> **方法学坑（登记）**：第一轮的还原用 `mv`，而 `mv` **保留原 mtime**，于是 MSBuild 认为"源比产物旧"
> 而**跳过重建**，DLL 里留着上一条突变（`LossWindowPlanTests` 因此在"已还原"的树上红成 200）。
> 第二轮的还原改成 `cp` + `touch` 并重跑 build。为证明树确实回到证据状态，重新 publish 一次，
> `linux/WinForward.E2E.dll` sha256 与判据轮**逐位相同**（`582e28dc…`，§5.1）。后来者请勿用 `mv` 还原源码。

### 3.2 D7 的两侧分离（单测）

`UdpReliabilityTrackerTests`：`ASendPastTheBoundedSequenceSpaceIsRefusedWithoutTouchingTheArrays`
（`SentOutOfRange == 1`、`OutOfRange == 0`）与既有的
`ACorruptDatagramNamingAnImpossibleSequenceIsRefusedAndCounted`（`OutOfRange == 1`）、
`AnArrivalNamingAnImpossibleSequenceIsRefusedAndCounted`（`OutOfRange == 3`）合起来把两侧锁死；
`EverySentDatagramLandsInExactlyOneClassificationBucket` 与 `MixUdpBookConcurrencyTests` 里的
`OutOfRange == 0` 仍然成立（接收侧本来就为 0）。**`_sent.TrySet(sequence)` 保留**：越界后位图的
`OutOfRange` 才会涨（`SentOutOfRange` 就是它的读法），删掉 `TrySet` 会让 M6 的另一半（`SentOutOfRange == 1`）红。

### 3.3 `#8` 的四次运行（同一棵树、同一个路径）

```console
$ python3 synthetic/make_tree.py /tmp/e3b1/default/raw
$ python3 synthetic/make_tree.py --window-overflow wf-aot-opt /tmp/e3b1/overflow/raw
$ python3 /tmp/e3b1/analyze-before.py --raw <tree> --out <out>     # HEAD 的 analyze.py
$ python3 ./analyze.py            --raw <tree> --out <out>
```

| 对照 | 结果 |
|---|---|
| 默认树：改前 vs 改后 | `tables.md` 与 `verdict.json` **逐字节相同**（`diff` 空）——D19.3 J 的前半 |
| 越界树：改前 vs 改后 | 恰好 **3 行**变化：`wf-aot-opt | LAT` 的 `tcp-connect` / `tcp-rtt` / `udp-rtt` 三行由数字变成 `n/a (windowOverflow > 0)`；`verdict.json` **相同**；`LAT` 的 `dns-rtt` 行保留原来的 `n/a (LAT has no dns-rtt histogram)` |
| 突变体（`>= 0`）对默认树 | `tables.md` 变动 **72 行**（36 个格子翻成 caveat）——负控红 |
| 再生成于文档路径 `/tmp/wf-synth/raw` | 改前 vs 改后仍**逐字节相同**（§3.5 的既有漂移与本批无关） |

### 3.4 `#6` / `#9`

见 §1.4 / §1.6，反证 M7 / M8。`#6` 的判据是**三条发布值同时**等于 137（`metrics.window`、
`parameters.lossWindowMs`、`gates.windowMs`），不是只看其中一条。

### 3.5 附带发现（登记，不在本批范围）

`benchmarks/results/2026-10-06-e2e-competitors/analysis/verification/synthetic-tables.md` 与当前
`analyze.py` 在**默认树**上有 2 行漂移（`BASE metrics.loss.window` 的单元格、REL 表格 caption 少一句
`meanConnectMs`/`meanTransferMs`/`byMode` 的说明）。**不是本批造成的**：HEAD 的 `analyze.py` 在同一棵树、
同一路径上产生同样的 2 行差异（`diff` 4 行），而本批改前/改后在两处都逐字节相同；
`verification/synthetic-verdict.json` 则与当前输出**完全相等**。这是 E2 某批改了渲染却没重生成表格式
golden 的遗留，登记给 E4 冻结 `verification/golden/` 时一并处理。

---

## 4. 本批唯一允许的结构差异：三条 note 更正（D2）

`compare-records.py` 的 `structural` 栏按**字符串集合**比对 `notes`（`notes` 只在
`numericTextKeyNames` 里，即只把插值小数换成 `<n>`），没有声明机制。D2 又明写"实现者负责 Note"，因此
这三条是**预期变更**，逐字登记如下（结构比对实测：`structural=3`，base/after 各 3 条）。

### 4.1 `records/LOSS.jsonl` 与 `records/BASE.jsonl`（同一条 note，两条记录组各 2 行）

**旧（run1）**：

```
outOfRangeSequences counts sequences the tracker refused as outside its bounded sequence space; a non-zero value means part of the offered schedule was never tracked, so this record's classification covers fewer datagrams than sent and must not be read as a complete loss measurement.
```

**新（post1/post2）**：

```
outOfRangeSequences counts sequences a received datagram named that the tracker refused as outside its bounded sequence space, and sentOutOfRangeSequences counts offered slots the tracker refused to send for the same reason; the refused slot reached no socket, so it is counted as client send loss and lands in no classification bucket, and a non-zero value of either means this record covers fewer datagrams than it claims to have measured.
```

为什么旧句假：拆分后 `outOfRangeSequences` 只数**接收侧**拒绝（`OutOfRange => _arrived.OutOfRange +
_corruptAt.OutOfRange`），而"提供的调度有一部分从未被登记"这件事只可能发生在**发送侧**（`SentOutOfRange`）。
旧句把两件事合成一句，在拆分后把接收侧的非零值读成"调度没被跟踪完"，与数据相反。

### 4.2 `records/MIX.jsonl`（两条 note，各 2 行）

**旧 1（公式）**：

```
… classes.udp.clientSendLoss is classes.udp.abandonedAtTeardown + classes.udp.sendFailures + classes.udp.windowOverflow with all three terms published beside it; …
```

**新 1**：

```
… classes.udp.clientSendLoss is classes.udp.abandonedAtTeardown + classes.udp.sendFailures + classes.udp.windowOverflow + classes.udp.sentOutOfRangeSequences with all four terms published beside it; …
```

为什么必须改：D7 之后公式是**四项**，三项的陈述与记录里的数字相反（新键就写在同一块里）。

**旧 2（越界键）**：

```
… classes.udp.outOfRangeSequences counts sequences the tracker refused as outside its bounded sequence space: a non-zero value means part of the offered schedule was never tracked, so the classification covers fewer datagrams than sent.
```

**新 2**：

```
… classes.udp.outOfRangeSequences counts sequences a received datagram named that the tracker refused as outside its bounded sequence space, and classes.udp.sentOutOfRangeSequences counts offered slots it refused to send for the same reason: such a slot is folded into classes.udp.clientSendLoss and lands in no bucket, so a non-zero value of either means the classification covers fewer datagrams than the record claims to have measured.
```

理由同 §4.1，并补上"发送侧拒绝计入 `clientSendLoss`"。

### 4.3 其余结构面为 0（逐栏读数）

`compare-records.py run1 → post1 --strict` 的第 1 栏只有上面 3 条（`records/LOSS.jsonl`、
`records/BASE.jsonl`、`records/MIX.jsonl` 的 `notes`），此外：

- **键集**：单边路径 9 + 11 条**全部**落在改名表的 `renamed`/`added` 上（第 5 栏 `declared=30` =
  改名 27 + 本批新增 3），没有一条 `rename-table` 越带；
- **arity**：第 2 栏 `conditional=0`（数组长度、条件块无一变化；`notes` 的**长度**没变，只换了两个元素）；
- **其它字符串**：`arm`/`kind`/`label`/`planHash` 等字符串无一变化（第 1 栏只报 notes）；
- **`target.out`**：文本行集合与顺序都相同（第 1 栏无 `target.out` 条目）；
- **identity**：`identity=0`；
- **契约**：`contract=0`（零越带）。

---

## 5. 六条门禁与等价性

### 5.1 门禁（本批冻结树，逐条）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3b1/pub scripts/publish.sh` | exit 0；三份产物；`linux/WinForward.E2E.dll` sha256 `582e28dc46ad6ff9dec38fa5eaf55cca96d4eac3f40e866f1846e5e3e3333398`（事后重新 publish 到 `/tmp/e3b1/pub-verify` 逐位相同 ⇒ 突变全部还原） |
| 2 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| 3 | `dotnet test WinForward.slnx -c Release`（串行） | 15 个程序集全绿，**20.0 s**；`WinForward.E2E.Tests` **Failed: 0, Passed: 285**（E3-a 的 276 + 9） |
| 4 | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3b1/pub scripts/selftest.sh scripts/plans/selftest-plan.json` | 本批二进制**连续两次 exit 0**（post1/post2） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出**（两次，最后一次在冻结树上） |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e3b1/jb-inspectcode-final2.xml WinForward.slnx` | 解析 XML：`<Issue>` **0**、`<IssueType>` **0**（首跑 2 条 `MemberCanBePrivate.Global` 落在新增的 `ArmRunFixture`，已把两个属性收成 `private`） |

`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E
benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests`：**无输出、exit 0**。

**稳定性**：`test-stability.md` §4 要求的 loaded soak —— 两条并发 `dotnet test tests/WinForward.E2E.Tests -c Release`
流 × 3 轮 = **6 次 285 全绿**（`/tmp/e3b1/soak-{a,b}-{1,2,3}.log`，rc 全 0）。

> **登记：门禁不要与 inspectcode 并发。** 本轮有一次 `dotnet test WinForward.slnx` 与 `jb inspectcode`
> 同时跑，15 个测试程序集被 15 分钟的静态分析抢 CPU，结果 5 个项目各红 1 条**互不相干**的时序事实
> （含本批新加的 THRU 事实，当时的写法是"0.3 s 臂 + 服务端 50 ms 延迟后 RST"），并且 3 个 testhost
> 一直不退出、整轮 30 分钟不结束。串行重跑 20 s 全绿；本批因此把 THRU 事实改成**事件驱动**（服务端先读到
> 命令帧再 RST）并把预算放宽到 1.0 s / 100 MB/s，DNS/REL/LOSS 三条事实的预算也同步放宽。
> 其余 4 条红是既有套件在饥饿下的假失败（本批未碰 `src/`），已由串行重跑证伪。

### 5.2 判据比对（`compare-records.py`）

```console
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 <run> \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
```

| 对照 | structural | conditional | identity | declared | contract | rename | readings | exit |
|---|---|---|---|---|---|---|---|---|
| run1 → post1（**判据**） | **3**（§4） | 0 | 0 | 30 | **0** | 0 | 298/366（251 越带） | 1（仅 note 更正） |
| run1 → post2（复核） | **3**（同三条） | 0 | 0 | 30 | **0** | 0 | 294/366（224 越带） | 1（同上） |
| post1 → post2（同二进制噪声地板） | 0 | 0 | 0 | 0 | **16** | 0 | 293/366（185 越带） | 1 |

- 改名表：`table: 595 entries = added 6, identical 580, renamed 9`；`B2: 9/9 satisfied`；
  `added observed: 4, not observed 2`（`detail`/`error`，"只增"键在不写 error 记录的正常运行里不出现）；
  `path sets: base 589, after 591; only in base 9 (hit 9), only in after 11 (hit 11)`。
- **新键登记**（D19.3 A）：`sentOutOfRangeSequences` 的三个路径写在 `contract-inventory.py` 的 `ADDITIONS`
  里（生成器把 `added` 行写进 `contract-rename.json`，`contract-rename.md` 同步重生成）。
  该表的**批次字段只出现在 `renamed` 行**（生成器只给 `CONVERGENCE_BATCH = B2` 的改名行写 `batch`），
  `added` 行按既有惯例**不带批次**：`compare-records.py` 对单边新路径的放行只看 `added` 条目本身，
  `--batch E3` 不是必需项（本批按既有惯例处理并在此说明）。
- **噪声地板 16 条**（post1 → post2，同一二进制）逐条：13 条是既有族
  `metrics/latency/{tcp,udp}.sent`、`metrics/udp.sent`、`metrics/desktops/udp.{sent,arrived,foreignConnection}`、
  `metrics/udp.lossRate`（`E2a3/E2b1/E2b2/E2c/E2d/E3a` 逐行同族），**另有 3 条**是
  `sentOutOfRangeSequences` 的三个新路径——`jitter-band.json` 冻结在本批之前，新路径当然没有带宽
  （§5.3）。两组都不是"值动了"：三条新键在 post1/post2 都是 `0`。
- `--strict` 读数摘要（post1）：298/366 条移动、251 条越带；同一对基线的 post2 是 294/224 ⇒ 读数的
  越带条数是**宿主噪声**（CPU/内存/时钟采样 + 本机时间片），不是本批的信号；本批**没有任何**读数路径
  被改动（`analyze.py` 与 `metrics` 的渲染都不读越界键）。

### 5.3 数值变化逐条登记

以 run1 / post1 / post2 三跑对 54 条相关发布路径逐值核对（脚本见 §7），**变化只有 3 条**，全是新键
"absent → 0"：

| 记录 | 路径 | run1 | post1 | post2 |
|---|---|---|---|---|
| `records/LOSS.jsonl` | `metrics/sentOutOfRangeSequences` | *（键不存在）* | 0 | 0 |
| `records/BASE.jsonl` | `metrics/loss/sentOutOfRangeSequences` | *（键不存在）* | 0 | 0 |
| `records/MIX.jsonl` | `metrics/classes/udp/sentOutOfRangeSequences` | *（键不存在）* | 0 | 0 |

**没有移动的**（逐条核对过、三跑同值）：`gates/clientSendLoss`（IDLE/DNS/DNSALT/LOSS/MIX/REL/THRU/PERSIST/BASE 全 0，
LAT/LATLOAD 0）、`gates/windowOverflow`、`gates/windowMs`（LOSS/MIX/BASE 200，其余 0）、
`metrics/clientSendLoss`、`metrics/classes/udp/clientSendLoss`、`metrics/outOfRangeSequences`（两侧都是 0）、
`metrics/unsent`（DNS 0）、`metrics/sendFailures`（THRU 0）、`metrics/scheduledAttempts` == `metrics/connectAttempts`（REL 101/101）、
`parameters/lossWindowMs` 与 `metrics/window`（200）。也就是说：**五个门的派生在干净 selftest 上是行为中性的**，
只有 sum 里多了一个在这条路径上恒为 0 的项。

### 5.4 零宽发布键（D18.5 #12）

```console
$ for d in $R/baseline/run1 /tmp/e3b1/post1 /tmp/e3b1/post2; do python3 /tmp/e3a/zerowidth.py $d/out > zw-$(basename $d).txt; done
$ diff zw-run1.txt zw-post1.txt && diff zw-run1.txt zw-post2.txt
（无输出）
```

**39 个 (记录, 路径) 对 / 19 条不同路径**，三跑逐值相同：`LAT` tcp `received` 322 / udp 161、
`LATLOAD` 两向各 1601、`BASE latency/*` 各 101、`DNS`/`DNSALT` 402、`MIX` 146/602/8、`PERSIST` 15；
全部 `outstandingAtTeardown` 与 `unmatchedReplies` 为 0。

---

## 6. 偏离与登记

1. **三条 note 更正**（§4）：D2 要求的 Note 同步与"结构差异 0"冲突，按 D1 的效力顺序服从 D2（父代理在
   E3-b1 中期已裁定同意），逐字登记并在 `index.jsonl` 里挂钩。**没有**为了归零而保留假 note，也**没有**
   用 `record-normalize.json` 掩掉这句话。
2. **REL 的"派生"需要两个装置**（§2.4）：臂级事实证明公式非恒零，源码事实证明**调用点**是派生。第一次
   反证（M3）暴露了这个漏洞；登记为"可判性边界"的补法而不是接受一个抓不到字面量的门。
3. **`IdleArm` 的 `0` 是命名的常量**（`NoTrafficClientSendLoss`），不是裸字面量，也不是新增计数器：
   D19.3 E 要求记录保留 `0`、`n/a` 只在分析器/README；分析器今天**不渲染** IDLE 的这个门，所以没有地方
   需要改渲染（README 的行已登记给 E5）。
4. **分析器只加单元格、不动 caption**：§5 表格的引导段一个字未改，因此 `windowOverflow == 0` 的记录
   连"表格文本"都逐字节不变（§3.3）。D19.3 H 提到的"断言先对当前输出红"由 §3.3 的突变体承担；
   `check-fairness.py` 之类的**可 grep 断言载体**按父代理的分工归 E3-b2，本批只留下可重跑的
   `make_tree.py --window-overflow` + 三段命令。
5. **`verification/synthetic-tables.md` 的 2 行既有漂移**（§3.5）：登记给 E4，本批不重生成（重生成会把
   与本批无关的渲染差异混进 diff）。
6. **`analyze.py` 的 `--window-overflow` 注入是自洽的**：延期不丢弃（`supplied == sentOk`、
   `clientSendLoss == 0`），所以越界树里的合法性 gate 仍是 PASS、只多一条既有的 `measurement-caveat`
   finding（那是 `#8` 想披露的东西本身）。
7. **未做（按范围）**：`#11` 的 `undecodable` 消费、`#19` 的 CPU 口径、`scripts/check-fairness.py`
   （E3-b2）；`Truncated`（E3-c）；账本/CLI（E3-d）；ODE/`achievedRate`（E3-e）。`src/` 一行未改。

---

## 7. 重建方式（check 轮可逐条重跑）

```console
R=.trellis/tasks/10-07-e2e-harness-refactor/research

# 门禁（串行！不要与 jb inspectcode 并发，见 §5.1）
dotnet build WinForward.slnx -c Release                                     # 0/0
dotnet test WinForward.slnx -c Release                                      # 20 s，15 程序集全绿（E2E 285）
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
    benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests          # 无输出、exit 0
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0、空输出
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3b1/pub scripts/publish.sh
rm -rf /tmp/wf-bench/selftest && WF_PUB=/tmp/e3b1/pub scripts/selftest.sh scripts/plans/selftest-plan.json
jb inspectcode -f=Xml -e=HINT -o=/tmp/e3b1/jb.xml WinForward.slnx           # XML 里 <Issue> 0

# 判据比对（post1/post2 = 上面两次 selftest 的 out/ + ledger.jsonl + target.out）
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e3b1/post1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict   # structural=3（仅 §4 的 note）、contract=0
python3 benchmarks/WinForward.E2E/scripts/compare-records.py /tmp/e3b1/post1 /tmp/e3b1/post2 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json --strict   # 噪声地板 contract=16

# 新键的登记（生成器 + 比对表）
python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename --baseline $R/baseline/run1 \
    --run /tmp/e3b1/post1 --out-json $R/contract-rename.json --out-md $R/contract-rename.md

# #8：四次渲染对照 + 突变体（命令见 §3.3）
python3 benchmarks/results/2026-10-06-e2e-competitors/analysis/synthetic/make_tree.py --window-overflow wf-aot-opt /tmp/e3b1/overflow/raw

# 反证：/tmp/e3b1/mutate.sh 与 /tmp/e3b1/mutate2.sh（补丁表就是 §3.1 的逐行文字）
```

---

## 8. 给 check 轮的独立复核点

1. **五臂的门是不是真的"派生"**：读 `LossArm.cs:37/75`、`MixMetricsWriter.cs:53`、`DnsArm.cs:58`、
   `ThroughputArm.cs:126`、`ReliabilityArm.cs:66`，确认没有一个是字面量；然后只用 §3.1 的 M1/M2/M4/M5/M8
   重现"删项即红"，并独立判断 REL 的**双装置**是否必要（M3 的绿是本批记录在案的漏洞）。
2. **D7 的两侧分离是否真分离**：`SentOutOfRange` 与 `OutOfRange` 的定义（`UdpReliability.cs:207/215`）、
   `MarkSent` 守卫里 `_sent.TrySet` 是否还在（`:241`）；再核对三个新键在 LOSS/BASE/MIX 的记录里**只增**
   （`compare-records.py` 第 5 栏），以及 `clientSendLoss` 的四项在 `metrics` 里都能逐项找到。
3. **`#8` 的优先级与 `> 0`**：在一个自己造的 `windowOverflow > 0` 记录上跑 `analyze.py`，确认
   (a) 只有该臂的**有数字**的格子被换掉、(b) `not-carried`/`no histogram` 的格子**不**被换、
   (c) `>= 0` 突变在零越界树上大面积翻格（§3.3 的 72 行）。
4. **三条 note 的登记是否诚实**：把 §4.1/§4.2 的旧句/新句与 `records/{LOSS,BASE,MIX}.jsonl` 的 `notes`
   逐字对照；确认结构栏**只有**这 3 条，且 `notes` 数组长度未变（arity 0）。
5. **门禁的串行前提**（§5.1）：先单跑 `dotnet test WinForward.slnx -c Release`（应 ~20 s 全绿），再单独跑
   `jb inspectcode`；不要并发，否则会看到 5 条互不相干的假失败与不退出的 testhost。

---

## 9. check 轮的独立复核与三处修复（本节的读数**取代** §2/§5.1 中受影响的数字）

§8 的五点逐条复核完毕，另加三处修复。**本节由上节的复核者写入**：§1–§8 的原文未改，读者遇到
§2（`ClientSendLossGateTests` 7 条）与 §5.1（E2E 285）时以本节为准（现在是 **8 条 / 286**）。

### 9.1 修复一：`#9` 的用例在满负载下会 flake（阻塞级）

**现象**：独立复核的第一次 `dotnet test WinForward.slnx -c Release`（15 个测试程序集并行、本机
32 核 11 GB）里 `LatencyArmDrainSamplingTests.AReplyThatLandsAfterTheOfferLoopStillReachesTheTcpHistogram`
红：`the offer loop sent nothing`。

**机制（确定性复现，不靠运气）**：`OfferSeconds = 0.15` 是"从算 deadline 到 offer 循环第一次判
`Clock.Now < DeadlineTicks`"之间允许的**全部**时间，而 `LaneTestOptions.WarmAsync` 只预热了引擎与
`LanePolicyFake`，measured run 这一次才第一次编译 `LatencyTcpPolicy`/`LatencyTcpState`/`LogHistogram`/
`PayloadAfterAsync`；主机在重负载下把这段同步路径拖过 150 ms，循环一次都没进去。复现探针：在
`deadlineTicks` 之后、构造引擎之前插 `Thread.Sleep(300)`，`OfferSeconds = 0.15` 时红，且失败消息
逐字为 `the offer loop sent nothing (0 slots supplied, scheduleTruncated False)`——`Supplied == 0`
而 `scheduleTruncated == False` 正是"循环从未进入"，不是别的路径。

**修复**：新增 `WarmTheArmPolicyAsync()`（用真 `LatencyTcpPolicy` + `LatencyTcpState` + `LogHistogram` +
`LaneTransportFake { CancelAfterSends = 3, PayloadAtReceive = 1 }` 跑一条一次性 lane，把该臂自己的
编译成本移出测量窗口——照 `LaneTestOptions.WarmAsync` 的既有手法），`OfferSeconds` 0.15 → **0.5**
（`test-stability.md` §2.9：窗口要吸收宿主停顿，不能被它吃光），并把失败消息补上
`SlotsSupplied/ScheduleTruncated` 以便下次自证。同一个 300 ms 探针在修复后**绿**。

**稳定性证据（spec §4 的形状）**：整解 ×3 全绿；受影响的四个事实类 12 轮串行全绿；两条并发
E2E 流 ×3 轮 6 次全绿；修复前正是同一条整解运行把它跑红的。

### 9.2 修复二：DNS 门的"另一个总体"没有被任何反证覆盖

**发现**：用**自造**的突变（不是作者的 M1）`metrics.Unsent` → `metrics.Unsent + metrics.SocketErrors`
时，`ClientSendLossGateTests` **全绿**——D19.2 ④ 禁止的那个公式在臂级完全不可判，因为黑洞场景里
`socketErrors == 0`，两个公式给出同一个数。

**修复**：新增事实 `TheDnsArmDoesNotGateItsSocketErrorsAsClientSendLoss`：DNS 端口**什么都不绑**，
resolver 的 socket 在第一次发送后即失败（实测稳定 `sent=2 unsent=0 socketErrors=2 gate=0`，三种
速率下同形）。断言 `SocketErrors > 0`（驱动器非零）且 `gate == metrics.Unsent`；该突变体现在红
（`Expected 0 / Actual 2`）。这条事实**不**断言 `unsent == 0`，所以宿主让窗口填满时它只会更强。

### 9.3 修复三：LOSS/MIX 公式的"另一侧计数器"没有被反证覆盖

**发现**：第二个自造突变把 `tracker.SentOutOfRange` 换成 `tracker.OutOfRange`（接收侧）时，LOSS 与
MIX 两条事实**全绿**——`DrivenTracker` 把两侧都驱动到 1，换用一个数看不出来。

**修复**：`DrivenTracker` 现在把接收侧拒绝驱动到 **2**（`MarkCorruptWithKnownSequence` 两次，不同
不可能序号），发送侧仍是 **1**；两条事实分别断言 `SentOutOfRange == 1` / `OutOfRange == 2`（MIX 侧
`SentOutOfRangeSequences == 1` / `OutOfRangeSequences == 2`），门仍是 4。换用另一侧现在红
（`Expected 4 / Actual 5`）。

### 9.4 check 轮的反证表（自造突变 11 条，全部红；每条都校验还原哈希）

| 突变 | 杀它的断言 | 实测 |
|---|---|---|
| M1a' `DnsArm` 门读 `metrics.TcpUnsent` | 黑洞事实 `gate == unsent` | 红（`Expected 1745 / Actual 0`） |
| M1b' `DnsArm` 门读 `Unsent + SocketErrors` | 新增的闭端口事实 | 红（`Expected 0 / Actual 2`） |
| M2' `ThroughputArm` 门读 `Frames − FramesSent` | THRU 事实 | 红（`Expected 1 / Actual 0`） |
| M4' LOSS 公式删 `counts.Undetermined` | LOSS 事实 `== 4` | 红（`Expected 4 / Actual 3`） |
| M4b' LOSS 公式读 `tracker.OutOfRange` | LOSS 事实 `== 4` | 红（`Expected 4 / Actual 5`） |
| M5' MIX 折叠删 `counts.Undetermined` | MIX 事实 `== 4` | 红（`Expected 4 / Actual 3`） |
| M5b' MIX 折叠读 `tracker.OutOfRange` | MIX 事实 `== 4` | 红（`Expected 4 / Actual 5`） |
| M6' `MarkSent` 守卫删掉 `_sent.TrySet` | `SentOutOfRange == 1` | 红（`Expected 1 / Actual 0`） |
| M7' `LossArm` 恒用默认窗口 | `metrics.window == 137` | 红（`Expected 137 / Actual 200`） |
| M8' drain 上限 0.2 s → 0.005 s（比 staged 应答还短） | `histogram.Count == 1` | 红（`Expected 1 / Actual 0`） |
| M3'（文本级）REL 调用点写成 `0L` **并在注释里原样粘贴期望串** | 源码装置 | **绿 —— 装置的可判性边界**（见 9.5） |

### 9.5 未修复但须登记的判断

1. **REL 的双装置是必要的**：`scheduledAttempts − connectAttempts` 在"把尝试跑完"的运行里结构性为 0
   （背压不丢弃，`ReliabilityMetricsWriter.ClientSendLoss` 的注释即此意），所以臂级事实**无法**把
   "派生"与"字面量 0"分开；源码装置是唯一可判的补充。它的边界：装置比对的是**调用点形状**，
   把表达式抽成局部变量再赋值（行为等价）会**假红**；唯一的"绕过"是在注释里粘贴**整段**期望赋
   值文本（`[key] = ReliabilityMetricsWriter.ClientSendLoss(...),`），这是刻意伪造而非回归路径，
   不修。
2. **§5.3「数值变化只有 3 条」的适用范围**：逐值核对 104 条相关路径时，除 3 个新键
   `absent → 0` 外，**派生读数** `gates/inFlightCeilingMs`、`metrics/{tcp,udp,latency/*}.windowCeilingMs`
   （BASE/LAT/LATLOAD）也随 achieved rate 移动。它们是 `jitter-band.json` 有带宽的**读数**类
   （§5.2 的 `--strict` 摘要已逐条披露），不是本批碰过的计数；§5.3 的"54 条"指的是本批公式/键集
   涉及的那一组，此处补明以免读者把 ceiling 当作被本批改动。
3. **§3.5 的 2 行漂移**在文档路径 `/tmp/wf-synth/raw` 上复现（golden `synthetic-tables.md` vs HEAD
   与本批都差那 2 行；HEAD 与本批**逐字节相同**；`synthetic-verdict.json` 与两者的 verdict 相等）。

### 9.6 check 轮的六条门禁与等价性（修复后的冻结树）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `WF_PUB=/tmp/chk/pub scripts/publish.sh` | exit 0；`linux/WinForward.E2E.dll` sha256 `582e28dc…`，与 §5.1 判据轮**逐位相同**（测试侧修复不进该 DLL，同时证明还原是字节级的） |
| 2 | `dotnet build WinForward.slnx -c Release` | 0 Warning(s) / 0 Error(s) |
| 3 | `dotnet test WinForward.slnx -c Release`（串行 ×3） | 三次全绿；`WinForward.E2E.Tests` **286**（285 + §9.2 的一条） |
| 4 | `WF_PUB=/tmp/chk/pub scripts/selftest.sh scripts/plans/selftest-plan.json` ×2 | 两次 exit 0 |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节**输出 |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/chk/jb.xml WinForward.slnx`（单独跑） | `<Issue>` 0、`<IssueType>` 0（与 §5.1 的报告同为 329 字节） |

另：`python3 …/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts
tests/WinForward.E2E.Tests` 无输出；受影响的四个事实类 12 轮、两条并发 E2E 流 ×3 轮全绿（§9.1）。

**等价性（用 check 轮自己的两次 selftest 跑，不重跑作者产物）**：

```console
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/chk/post-a \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
summary: structural=3 conditional=0 identity=0 declared=30 contract=0 rename=0 readings=296/366
         （第 1 栏三条 = §4 登记的三条 note；第 5 栏 = 改名 9 + 本批 3 个新键；B2 9/9 satisfied）
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py /tmp/chk/post-a /tmp/chk/post-b \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json --strict
summary: structural=0 conditional=0 identity=0 declared=0 contract=16 rename=0
         （16 = §5.2 的 13 条既有族 + 3 条新键无带宽）
```

零宽键（D18.5 #12）在 `run1 / post-a / post-b` 之间仍 **39 对 / 19 条逐值相同**；
`contract-rename.json`/`.md` 由 `contract-inventory.py rename --baseline … --run /tmp/chk/post-a`
**重新生成后逐字节相同**（595 = added 6 + identical 580 + renamed 9）。

