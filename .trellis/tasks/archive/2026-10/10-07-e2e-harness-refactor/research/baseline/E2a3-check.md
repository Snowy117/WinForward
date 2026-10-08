# E2-a3 check 报告（真实 transport + `ReplyClassifier` + `LatencyArm` 两 lane 迁移 + 5 处 connect 机制统一）

被检查的树：`436225b` + 未提交工作树（实现轮冻结树，`linux/WinForward.E2E.dll` sha256 = `7de645ae…`）。
本文件是 check 轮的证据留档，与实现轮的 `E2a3-vs-run1.md` 配套。

**检查轮改了三类东西**：测试工程一处实质修复（F1：把一条恒真的排序事实变成真事实）；产品程序集内
**三处纯注释/文档**修复（F2–F4，无一行逻辑）。因此：

| 树 | `linux/WinForward.E2E.dll` sha256 |
|---|---|
| 实现轮冻结树（证据文档 §1 的 `final-*`） | `7de645ae9092dd29f13517a99777dbbf0402ef448581b027a9b5f2f9294df604` |
| check 轮修复后（**本报告的全部判定依据**） | `8c188dd08bcf7081534556d57a5d086e8643347862a449e07d097fbdb4368a22` |

哈希变化**只由注释引起**，已用决定性构建实验证明：给 `ReplyClassifier.cs` 尾部追加一行注释 → 重新构建 →
`809e0ff8…`；还原该行 → 重新构建 → 逐位回到 `8c188dd0…`（§4 的 D1）。判定因此在新二进制上整体重跑
（§5 两条判据运行、§6 门禁），**未复用实现轮的运行结果**。

---

## 0. 结论一句话

**E2-a3 可以提交**：D18.1/D18.2/D18.3/D18.5/D18.6 的逐条契约、9 条有意变更的边界、零宽发布键、
真实 transport 的分配 gate 与反证、TCP/UDP 的映射与 FIFO 保持、14 条 note 与 10 条 gate 的接线，
全部通过独立复核与突变验证；检查轮修了 4 处（1 处实质、3 处文档），**上报 4 项**（其中 1 项是量能缺口，
**均不阻塞本批提交**）。

---

## 1. 已修复的问题

| # | 位置 | 改了什么 | 为什么 | 反证方式 |
|---|---|---|---|---|
| F1 | `tests/WinForward.E2E.Tests/Lanes/ReplyClassifierTests.cs:31` | `AnotherFlowsConnectionIdIsRejectedBeforeItsOwnFillerIsRead` 补一条"**外来且自身 filler 也坏**"的数据报（`ConnectionId+1` + 翻转 payload + 重算校验和），并改正注释 | 旧用例只用一条**自洽**的外来帧（filler 对它自己的 id 成立），而自洽外来帧在两种判据顺序下都会走到 id 判据 → 该事实对"连接 id 先于 filler"（D18.3 的保持项）**恒真**，把两条判据对调照样绿 | 突变 Q（`Classify` 里把 filler 判据提到 id 判据之前）：**修复前绿、修复后红**，`Expected: ForeignConnection / Actual: Corrupt` |
| F2 | `benchmarks/WinForward.E2E/Client/Lanes/ReplyClassifier.cs:97`、`:33` | `Classify` 的顺序注释与 `ReplyKind.ForeignConnection` 的文档改成事实正确的理由 | 原文说"外来帧按它自己的 filler 校验，所以 filler 优先会把混流记账成 corrupt"——**这句话是错的**：自洽的外来帧按它自己的 id 校验 filler 会**通过**，两种顺序都落到 id 判据；顺序只在"外来 ∧ 自身 filler 也坏"时才可观测（F1 的突变证明了这一点） | 与 F1 同一次突变（修注释不单独反证）；文字与 `Classify` 的实际行为逐字对照 |
| F3 | `benchmarks/WinForward.E2E/Client/Lanes/{TcpLaneTransport,UdpLaneTransport}.cs:90`、`:87` | "带 token 的重载才不分配、去掉 token 每次 72 B"改成"三个 `ReadOnlyMemory` 重载内联完成时都是 0 B，72 B 属于 `byte[]` 参数绑定的 `ArraySegment`/`Task<int>` 重载" | 独立探针（§3.4）：`SendAsync(Memory, flags, token)` / `SendAsync(Memory, token)` / `SendAsync(Memory, flags)` **三者都是 0 B/次**；72 B/次是 `SendAsync(ArraySegment<byte>, SocketFlags)`（`Task<int>`）——那是**另一个 API**，只有把参数类型从 `ReadOnlyMemory<byte>` 改成 `byte[]` 才会踩到。原注释会让人以为"参数里少传一个 token"就是性能回归 | 独立探针（§3.4）+ 分配 gate 在真实 adapter 里的注入突变（§4 的 M1） |
| F4 | `benchmarks/WinForward.E2E/Client/Arms/MixArm.cs:713` | 把悬空的 `SendDatagramAsync` 的 `/// <summary>` 移回它自己的方法上方 | 重构插入 `TryOpenAsync` 时，该 summary 被留在 `TryOpenAsync` 上方，与后者的 summary 组成同一个文档块里的**两个 `<summary>`**（`SendDatagramAsync` 因此无文档，`TryOpenAsync` 的文档被拼接污染） | 全改动面扫描"一个声明前出现 >1 个 `<summary>`"：修复前 1 处命中、修复后 0 处 |

范围声明：F1 是本批新增测试文件内的实质修复，未放宽任何断言；F2–F4 是注释/文档，**未改动任何一行可执行代码**
（§4 的 D1 证明哈希的全部变化都来自注释）。`src/` 一行未改（`git status --short src/` 为空）。

---

## 2. 未修复但需上报

| # | 项 | 原因 | 影响 | 是否阻塞提交 |
|---|---|---|---|---|
| U1 | **`outstandingAtTeardown` 的 arm 侧接线没有任何用例覆盖**：`Σ Pending + Σ DeferredPending` 只写在 `LatencyArm.LatencyTotals.AddPending`/`AddCounts`（私有嵌套类型），测试工程没有任何一条事实驱动 `LatencyArm.RunAsync` | 补一条 arm 级事实需要一个可构造的 `ArmContext`（臂目前只由 `selftest` 端到端覆盖），超出 check 轮的局部修复范围 | D18.5 #12 要求对该零宽键"逐值核对"，但**基线上该键处处为 0**（§3.2），所以"逐值相同"对该键是空转：把 `Outstanding += counts.DeferredPending` 整行删掉，**245 条用例全绿**（§4 的 M3）。策略侧两半（`Pending`/`DeferredPending`）与"不是相减推导"由 `LatencyPolicyTests` 两条事实钉住，**缺的只是最后的求和** | 否（发布值不变）。建议在 E2-b/E2-c 或本批收尾追加一条驱动真实臂的事实，或明确登记为接受的缺口 |
| U2 | **证据文档 §7.1 的"token 重载"结论错误**（见 F3）；§7.2/§3 的零宽键计数口径是 **39**，按 D18.5 #12 的四个模式严格匹配是 **34** 个 (记录,路径) 对 / 18 条路径（差的 5 个是 `receivedBytes`/`receivedDatagrams` 之类的"received 前缀"匹配） | 证据文档是实现轮记录，check 轮不重写它的数字；本报告给出严格口径 | 结论方向不变（两种口径下都是 0 处差异）；但"39"这个数字无法由文档给出的模式复现 | 否 |
| U3 | `UdpLaneTransport.ConnectOk`/`ConnectTicks` **没有生产消费者**（只有 `LaneTransportTests` 读） | UDP lane 有意不记 connect 事实（证据 §9.1 偏离 4，理由充分：`tcp.connectAttempts` 的 note 只数 TCP lane + 探针） | 接缝上多了一个测试专用的观测面；TCP adapter 的同名属性是真消费者 | 否。若 E2-b 要收窄面，删这两个属性会同时删掉 `LaneTransportTests` 的 connect 计时断言；保留也自洽（两个 adapter 同形） |
| U4 | E2-b 的拆分清单行数需更新：`MixArm` 678 → **720**、`DnsArm` 500 → **504**（本批分别 +42/+4 有效行），`LatencyArm` 803 → **471**（仍超标，已登记给 E2-b） | 拆分清单在 `implement.md` 里按计划时点写死 | E2-b 开工前须按当前值重测，否则判据数字对不上 | 否 |

补充（不单列）：adapter 里"**同步**抛 `SocketException` → `Accepted:false`"的 catch 分支（两个 adapter 各一处）
没有用例覆盖；引擎侧的对应行为由 `AThrownSocketErrorFailsOneRequestAndTheLoopContinues` 钉住。该分支只在
病态路径可观测（§3.3），不阻塞。

---

## 3. 逐条验证结论（对应任务书的 1–11）

### 3.1 LAT 的四步顺序与零宽键

- **四步逐字落地**（`UdpLatencyPolicy.Book`，`:210-225`）：`Received++`（只对 `ReplyKind.Arrived`，含重复应答）
  → `TryTakePending(sequence)` → 命中：`_rtt.Record(Clock.ToNanoseconds(receivedTicks - intended))` + `ReleaseInFlight()`
  → 未命中：`UnmatchedReplies++`，**不收 inFlight**。与 D18.5 #3 逐条一致；RTT 用 `receivedTicks`（收到时刻），
  不是 `Settle` 的时刻（D18.5 #1）。
- **`lossRate` 公式逐字未变**：`JsonRate.Rate(SentOk − (Received − UnmatchedReplies), SentOk)`
  （`LatencyArm.cs:196`）与 HEAD 的同名表达式除字段名外完全相同；语义仍是"消费了 pending 的回复数"，
  重复应答在分子里被 `UnmatchedReplies` 抵掉，不可能超过 `SentOk`。
- **零宽键逐值核对（自己重跑）**：脚本按 D18.5 #12 的四个模式（`*.received$`、`*unmatchedReplies$`、
  `*outstandingAtTeardown$`、`*-rtt/count$`）取 run1 的 **34 个 (记录,路径) 对 / 18 条路径**，与
  **7 次自跑**（`run-A…run-E` 在旧二进制上、`fix-A/fix-B` 在新二进制上）逐一比对：

  | 记录 | 路径 | run1 | 7 次自跑 |
  |---|---|---|---|
  | `LAT` | `metrics/tcp.received` / `tcp.unmatchedReplies` / `tcp.outstandingAtTeardown` | 322 / 0 / 0 | 全部逐值相同 |
  | `LAT` | `metrics/udp.received` / `udp.unmatchedReplies` / `udp.outstandingAtTeardown` | 161 / 0 / 0 | 全部逐值相同 |
  | `LAT` | `latency/tcp-rtt/count`、`latency/udp-rtt/count` | 322、161 | 全部逐值相同 |
  | `LATLOAD` | `metrics/{tcp,udp}.{received,unmatchedReplies,outstandingAtTeardown}`、两条 `-rtt/count` | 1601 / 0 / 0、1601 | 全部逐值相同 |

  **34 对 × 7 次运行中只有 1 处差异**：`DNSALT /latency/dns-rtt/count` 在 `run-C` 是 401（run1 = 402），
  同一次运行里 `metrics/answered` 402→401、`timeout` 0→1、`rcodes/0` 402→401、`answerRate` 1→0.997512 ——
  即**一次 DNS 查询超时**。同一二进制对跑 `run-A → run-C` 复现了这同一族（§3.9），故不是回归；
  且它落在 D17.1 归为"读数"的直方图统计量上。`received`/`unmatchedReplies`/`outstandingAtTeardown`
  **没有任何一次移动**。
- **in-flight 不是发布键**：只有 `LatencyPolicyTests.ThePendingBookAndTheInFlightWindowAlwaysCountTheSameRequests`
  一条内部事实，符合 D18.5 #12。

### 3.2 `outstandingAtTeardown` 的组成

- **组成是求和、不是相减**：`LatencyTotals.AddCounts` 里 `Outstanding += counts.DeferredPending`（引擎半边），
  `TotalTcp`/`TotalUdp` 里 `total.AddPending(lane.Book.Pending)`（策略半边）。没有任何一处用别的计数推它。
- **负控（跳过重试）真的会红**：`DeferredPendingIsTheQueueOccupancyAndNotASubtractionOverTheOtherCounters`
  驱动一个"重试返回 `Skip`"的策略（跳过会静默出队、不动任何计数），得 `DeferredQueued=4 / DeferredDropped=0 /
  SentOk=1 / Supplied=5 / DeferredPending=1`；2a-2 check 提的相减式
  `(DeferredQueued−DeferredDropped) − ((SentOk+SendFailures) − (Supplied−DeferredQueued))` 得 **4**，
  事实断言 `NotEqual(1, 4)`，即相减式在该形状下必然出错。我按引擎的 `OfferSlotAsync` 逐槽重演了这五槽，
  确认 `Skip` 分支真的被执行（不是空转）。
- **引擎驱动的事实**：`OutstandingAtTeardownIsThePolicyPendingBookPlusTheEnginesDeferredIntents` 跑真实
  `UdpLatencyPolicy` + 一个从不回应的 transport，得 `Pending=2`、`DeferredPending=4`、
  `Pending + DeferredPending == Supplied − DeferredDropped`。
- **缺口**：见 U1 —— 上面两条钉的是**两半的来源**，不是臂的求和。

### 3.3 发送失败的两种形状

- **同步抛 `SocketException`**：旧 `LatencyArm` 的 `SendFrameAsync`/`SendDatagramAsync` 的 `socket.SendAsync(...)`
  写在 try **之外**，异常逃到 `SendLoopAsync`/`SendUdpLoopAsync` 的 `catch (SocketException)` →
  **结束 offer 循环且不计数**；新代码由 adapter 捕获 → `Accepted:false` → 引擎 `SendFailures++` + `OnSent(false)`
  + **继续**。与登记 #4 逐字相符。`LossArm`/`MixArm` 的发送循环本批没有改动（仍是裸的 `await socket.SendAsync`），
  故这条变更只落在 LAT 的两条 lane 上。
- **异步失败**（`!IsCompleted` 且 await 抛）：旧码 `_sendWouldBlock++` 再 `_sendFailures++`；新码
  `if (wouldBlock || result.WouldBlock) SendWouldBlock++` + `SendFailures++`，**两个都 +1**，与 D18.6 #2 相符。
- **一次发送至多 +1 个 wouldBlock**：并集而不是两次自增；两条事实分别钉"采样位置"（引擎 `IsCompleted`
  在唯一一次 await 之前）与"不重复计数"（transport 也自报 `true` 时仍为 3 而不是 6）。
- **两条新事实覆盖两种形状**：`AThrownSocketErrorFailsOneRequestAndTheLoopContinues`（抛形状）、
  `AnAsynchronousRefusalCountsInBothTheWouldBlockAndTheFailureCounter`（`Refuse + IncompleteSends`）。
- 基线两个计数恒 0（7 次自跑的 `/sendWouldBlock`、`/sendFailures` 处处 0），故发布值等价由这些事实承担。
- 未覆盖：adapter 自己的同步 catch 分支（见 §2 补充）。

### 3.4 真实 transport 的分配 gate 与 token 重载

- **gate 跑在调用线程**：`LaneTransportAllocationGateTests` 用 `LaneEngine`（不是 `Dedicated`），
  逐批断言 `BatchThreadIds == 调用线程`，`SentOk == SendCalls == 768`，readiness（首个 0 批）后
  **≥4 个稳定批**且逐批**精确 0**。
- **覆盖 `BuildRequest → SendAsync → OnSent`**：窗口在 decorator 的第 1 次 `SendAsync` 打开、第 128 次关闭，
  中间是真实 `UdpLaneTransport.SendAsync` 的 socket 调用。
- **反证会红（我独立做的，不是用例自带的）**：在**真实 adapter 的发送路径**里注入
  `GC.KeepAlive(new byte[8]);` → 正证事实红，
  `the real udp send path allocated on every batch: batches [4608, 4096, 4096, 4096, 4096, 4096], sentOk 768, sendCalls 768`
  （4096/128 = **32 B/次**，正是 `new byte[8]` 的代价），同时反证事实仍绿 —— 即窗口确实罩住真实 adapter，
  不是罩住 decorator 自己（§4 的 M1）。第一次我把注入放进了 `ReceiveAsync`，gate **仍绿**，这从反面确认了
  "窗口只覆盖发送路径"这一契约（也说明注入必须落在被测路径上）。
- **token 重载的探针结论（独立复跑，`/tmp/e2a3-check/probe`，256 次/批 × 6 批）**：

  | 调用 | 每次分配 |
  |---|---|
  | `SendAsync(ReadOnlyMemory<byte>, SocketFlags, CancellationToken)`（**两个 adapter 用的就是它**） | **0 B** |
  | `SendAsync(ReadOnlyMemory<byte>, CancellationToken)` | **0 B** |
  | `SendAsync(ReadOnlyMemory<byte>, SocketFlags)`（默认 token） | **0 B** |
  | `SendAsync(ArraySegment<byte>, SocketFlags)`（返回 `Task<int>`；`byte[]` 实参会绑定到它） | **72 B** |
  | `Send(byte[])`（同步） | 0 B |

  结论：adapter 用的确实是 `ValueTask` 的 `ReadOnlyMemory` 重载（不是 `Task<int>` 的 `ArraySegment` 那条），
  发送路径每请求没有多一次分配；但"去掉 token 就会 72 B"是错的 —— 72 B 属于另一个 API，
  只有把参数类型改成 `byte[]` 才会踩到（`SocketOps.SendCommandAsync` 正是 `byte[]`，每条 lane 一次，不在热点上）。
  实现者探针的偏差来源已定位：`/tmp/allocprobe/Program.cs` 的 "SendAsync(no token)" 传的是 `byte[]`，
  重载解析落到了 `ArraySegment`/`Task<int>` 上。注释已按 F3 改正；证据文档的对应句子见 U2。

### 3.5 TCP/UDP 的映射与 FIFO

- **TCP 终结性映射**（`TcpLaneTransport.ReceiveAsync`）：`BadChecksum → Malformed + BadChecksum`（策略记
  `corrupt`，**继续**）；`BadMagic`/`BadLength → IoError + 对应 Detail`（策略记 `protocolErrors`，**终止**）；
  `EndOfStream → EndOfStream`（`remoteClosed`，终止）；未知状态 → `IoError`（无 Detail）。与 D18.6 #3 一致。
  突变：把 `BadChecksum` 并进终止分支 → `AFrameWhoseChecksumFailsIsOneBadMessageAndAFrameBoundaryLossIsTerminal` 红（§4 的 M4）。
- **TCP 仍 FIFO、不用分类器**：`LatencyTcpPolicy` 的书是 `Queue<long>`（按到达顺序的出队），`OnReceive`
  把 `payload` 整个丢掉（`.cs:162-164` 的 ReSharper 抑制写明了理由）；`rg 'ReplyClassifier.Classify'` 全仓**恰好 3 处**
  （`LossArm:271`、`MixArm:814`、`UdpLatencyPolicy:162`），TCP 不在其中。`ATcpReplyIsMatchedInArrivalOrderAndNotByTheSequenceItEchoes`
  用 1 秒差的两条请求证明"按到达顺序、不看它回声的序号"。
- **UDP 超长数据报 → `Malformed + Truncated`**：transport 读进比目标宽 `TruncationSlack = 64` 的缓冲，
  `received > destination.Length` 时报 `Malformed + FrameDecodeError.Truncated`、`Length = 0`；策略把它记进
  `corrupt`（与旧码"内核截断 → 解码失败 → corrupt"同一发布桶）。事实
  `ADatagramTooLargeForTheDestinationIsNamedAsTruncatedAndNeverAsCorrupt` 送 `目标+1` 字节并断言
  `Kind=Malformed / Detail=Truncated / Length=0`。突变：把该分支改成返回 `Payload` → 该事实红（§4 的 M5）。
- **UDP `SocketException` → `IoError`**（终止接收循环），与旧 `catch (SocketException) { _protocolErrors++; }` 同义；
  `ObjectDisposedException`/取消由引擎循环吸收，与旧码相同。

### 3.6 接线完整性（程序化对照 `git show HEAD:…LatencyArm.cs`）

- **字符串字面量序列对照**：HEAD 29 条、新版 29 条，`difflib` 的序列差异**只有 6 处**，全部是访问器改名
  （`tcp._sentOk → tcp.SentOk`、`lanes.Udp._windowOverflow → udp.WindowOverflow`、`tcp._started → tcp.Started`、
  `lanes.Udp._started → udp.Started` 等）。**14 条 note 的文本逐字未变**。
- **note 与 gate 的写入点数**：两版都是 **14 个 `outcome.Notes.Add(`**、**4 个 `if (` 守卫**（`UseTcp`/`UseUdp`/
  条件 note 13/条件 note 14）、**10 个 `outcome.Gates[...]`**，键名逐一相同。
- **`WriteGates` 的取值来源对照**（成员名集合）：差异仅 `Udp/_backlogDrops/_sendFailures/_windowOverflow`
  → `BacklogDrops/SendFailures/WindowOverflow`，即**每个 gate 的取值来源都在**，没有哪个 gate 变成常量。
  `gates/windowMs` 在两版里都是**有意的常量 0**（LAT 无毫秒窗口），逐字对照确认。
- **`scheduleTruncated` / `laneShortfall` 语义未漂**：
  `Truncated = tcp.Truncated || udp.Truncated || LaneShortfall > 0`，其中 `LatencyTotals.Truncated` 是逐 lane
  `counts.ScheduleTruncated` 的 `|=`（探针不贡献，与旧 `_scheduleTruncated` 只在 lane 处置位一致）；
  `LaneShortfall = Planned − Started + idle`，`idle = Started > 0 ∧ Supplied == 0`，TCP 侧逐 lane 读
  `lane.Book.Started` + `lane.Counts.Supplied`，UDP 侧读 `udp.Started` + `udp.Supplied`。与 HEAD 逐字同义。
- **计数→派生量的接线**（与证据 §4 的接线表逐行核对，全部有落点）：引擎 8 个计数分别喂
  `metrics/{tcp,udp}.{supplied,sent,sendWouldBlock,sendFailures,windowOverflow,backlogDrops,scheduleTruncated}`、
  `tcp.laneSupplied[]`/`tcp.laneSentOk[]`、`gates.*`、`validity.Truncated`、两条 ceiling note；
  策略侧计数喂 `received`/`corrupt`/`protocolErrors`/`remoteClosed`/`unmatchedReplies`/`foreignConnection`/
  connect 三元组。`DeferredQueued → windowOverflow`、`DeferredDropped → backlogDrops`、
  `DeferredPending → outstandingAtTeardown 的未发出半边`、`Pending → 另一半`，映射是显式的、臂里没有第二份。

### 3.7 9 条有意变更的边界

| # | 是否只在病态路径可观测 | 判定依据 |
|---|---|---|
| 1 | **是** | 只有出现重复应答/未发出序号的回复时 `inFlight--` 的位置才不同；`received`/`unmatchedReplies` 不受影响（3.1），7 次自跑两者逐值相同、`lossRate` 恒 0 |
| 2 | **是** | LAT 的 `WasSent` 就是 pending 书的成员资格（D18.5 #11 明确 LAT 不引入 tracker），与 #1 同源 |
| 3 | **是** | TCP 只在"成帧消息多于在册请求"时不同；`tcp.received` 322/1601 逐值相同、`unmatchedReplies` 恒 0 |
| 4 | **是** | 需要一次同步 `SocketException`；基线 `sendFailures`/`sendWouldBlock` 恒 0 |
| 5 | **是** | 需要一次 connect（含 TCP 命令帧）失败；基线 connect 全成功（`connectFailures` 恒 0） |
| 6 | **是** | 发布值仍落 `corrupt` 桶，只多了"成因可见"；基线 `corrupt` 恒 0 |
| 7 | **是** | 需要失步的流；基线 `corrupt`/`protocolErrors` 恒 0 |
| 8 | **否（本来就不是变更）** | 新码是旧码的并集写法，一次发送仍至多 +1；由两条事实钉住 |
| 9 | **否（同义）** | `DeferredPending` 就是旧 `SendLoopAsync` 返回的 `backlog.Count`（返回时的队列占用） |

**没有任何一条会在正常路径上改动 band 0 的契约量**：7 次自跑 + 2 次修复后自跑里，`metrics/*`、`gates/*`
的契约越带只出现在 `BASE.jsonl metrics/loss/reordered`（详见 3.9），且被同二进制对跑复现。

### 3.8 LOSS/MIX/DNS 的回归与 `DnsArm` 的边界

- **三臂的发布值与 run1 一致**：7 次自跑中 6 次 `contract=0`；唯一一次（`run-C`）的移动全在 `BASE` loss 相位
  （见 3.9），`LOSS`/`MIX`/`DNS`/`DNSALT` 的 `unmatchedReplies`/`corruptRate`/`foreignConnection`/`receivedDatagrams`
  等**没有一次移动**（任务书点名的那几个量在 34 个零宽对里逐值相同，其余按 `--band` 判，契约栏 0 越带）。
- **阶梯替换的逐分支等价**（对照 HEAD 的 `LossArm`/`MixArm`）：坏校验和且 header 可读 → `MarkCorruptWithKnownSequence(seq)`；
  其余解码失败 → `MarkCorrupt()`；外来连接 id → `MarkForeignConnection()`（**在 filler 之前**）；filler 不匹配 →
  `MarkCorruptWithKnownSequence(seq)`；`WasSent` 不成立 → `MarkUnmatchedReply()`；否则 `MarkArrival(seq, now, payloadBytes)`。
  新旧逐分支一一对应（`now` 仍是接收时刻）。
- **`LossArm`/`MixArm` 的到达记账一行未动**：`rg 'MarkArrival'` 在两臂各**恰好一处**；`UdpReliabilityTracker`
  不在本批改动面内。`MixArm` 的 `BookDatagram` 保持"先 `Resolve(WasSent)`、再 `pending.TryRemove` 记 RTT、
  最后 `MarkArrival`"的旧顺序。
- **`DnsArm` 没有并入引擎**：`rg 'LaneEngine|ILanePolicy|ReplyClassifier|LaneCounts' DnsArm.cs` = **0 命中**，
  它只共享 `Pacer`/`SocketOps`（D18.1 的裁定）。

### 3.9 噪声归因（第一轮的 6 条契约移动）

- **第一轮的产物确实是那 6 条**：`/tmp/e2a3/after-run3` 的 `BASE.jsonl` 里 `reordered = 88`、
  `reorderRate = 0.035186`，同时 `sent = arrived = 2501`（无丢包、无重复、无 corrupt），`run1/run2/final1/final2`
  都是 0 —— 与证据 §2.1 的记录逐值相同。
- **我自己复现了同一族噪声**：`run-C`（旧二进制，静默主机）得到 `BASE.jsonl` 的 `reordered = 2`、
  `reorderRate = 0.0008`，并额外带出 `DNSALT` 的一次超时（`answered` 402→401、`timeout` 0→1）。
- **同二进制对跑复现（measurement-harness §3.5 的确认程序）**：

  | 对 | 契约 | 组成 |
  |---|---|---|
  | `run-A → run-B`（同二进制） | 13 | 全部是"改名后无带宽"的 13 条（非本次移动） |
  | `run-D → run-E`、`run-B → run-E` | 13 | 同上 |
  | **`run-A → run-C`** | **37** | 13 + **run-C 相对 run1 的那 24 条**（`BASE` 的 `reordered`/`reorderRate` + `DNSALT` 的四个计数与 `answerRate`，各按 min/max/mean 计 3 条） |
  | **`fix-A → fix-B`**（修复后的同二进制） | **19** | 13 + **`fix-B` 相对 run1 的那 6 条**（`reordered`/`reorderRate`） |

  即：**同一二进制两次运行之间就能量产这些移动**，且构成完全相同 —— 它们是宿主/靶机噪声，不是回归。
- **成因可定位到靶机**：`Target/UdpEchoServer.cs::RunAsync` 起 `_receiverCount` 个并发接收循环共享**同一个 socket**，
  `TargetRunner.cs:20` 取 `Math.Clamp(ProcessorCount / 2, 2, 8)`（本机 16 逻辑核 → **8 个**），回复顺序取决于宿主调度；
  `LossArm` 只按到达顺序记账，`reordered` 是它的**观测值**而非被改动面（3.8）。
  `DNSALT` 的那一次则是一次 DNS 回复超时，同样落在"零宽带宽 + 同二进制可复现"的 D17 类别里。
- **判定运行的读数栏**：`fix-A` 与 run1 的 366 条读数里 240 条越带（全部是 D15 的读数类：
  宿主资源采样、boot 相对时钟、`latency/*` 直方图、速率与 ceiling 派生量），按 D17.4 不参与判定。

### 3.10 规范

- **新文件有效行 ≤ 400**（去空行、去 `//` 与 `/* */`）：`ReplyClassifier` 37、`TcpLaneTransport` 89、
  `UdpLaneTransport` 73、`LatencyTcpPolicy` 119、`UdpLatencyPolicy` 130、`LaneEngine` 210、
  `LaneTransportTests` 184、`LaneTransportAllocationGateTests` 139、`LatencyPolicyTests` 317、
  `ReplyClassifierTests` **93 → 100**（check 轮的 F1）。全部达标。
- **`LatencyArm` 迁移后 471 有效行（803 → 471），仍超 400**：证据 §7.3 已明确登记给 E2-b；
  `MixArm` 678 → **720**、`DnsArm` 500 → **504**（U4）。
- **无变更日志式注释**：`rg -i 'previously|used to|no longer|changed from|formerly|was renamed'` 在改动面上只有
  4 处命中，逐条看都是"与旧实现的非显然等价/物理陈述"（`no longer frees its slot before the next offer` 是延迟
  ceiling 的口径、`can no longer be framed` 是流状态、`before the seam could tell the two apart` 是发布桶的口径），
  没有一处是"本次把 X 改成了 Y"的流水账。
- **反射只用 `typeof(...)` 字面量**：`LaneCountsDisjointnessTests` 的 6 个调用点全是字面量，辅助函数的 `Type`
  形参带 `[DynamicallyAccessedMembers(...)]`；`GetType()`/`Assembly.*`/`Activator.*` 在 `Client/Lanes` 与
  `tests/.../Lanes` 零命中（`EnableTrimAnalyzer`/`EnableAotAnalyzer` 全仓开、零警告即为硬证据）。
- **pragma 实测数**：`rg -n 'pragma warning disable S6966' benchmarks/WinForward.E2E --type cs | wc -l` = **8**
  （`ArmContext` 1 + `DnsArm` 2 + `LossArm` 1 + `MixArm` 1 + `PersistentArm` 1 + `ReliabilityArm` 1 + `LaneEngine` 1），
  与证据 §7.2 一致、正好压在 D18.5 #10 的上限上；引擎那处的注释与旧 `LatencyArm` 逐字相同。
- **抑制都有可核验的理由**：`Client/Lanes/**` 共 8 处（1 个 pragma 块 + 7 条 `// ReSharper disable once`），
  逐条给出了本仓可核对的理由。

### 3.11 门禁复核（全部修复之后，同一棵静止的树）

| 门禁 | 命令 | 结果 |
|---|---|---|
| publish | `cd benchmarks/WinForward.E2E && scripts/publish.sh` | 退出 0；三份产物齐全；`linux/WinForward.E2E.dll` sha256 = `8c188dd0…`（与 `bin/Release/net10.0/linux-x64/` 的同一文件逐位相同） |
| build | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| test（E2E） | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 245, Total: 245` |
| test（全解） | `dotnet test WinForward.slnx -c Release` | 14 个测试工程全绿，退出 0 |
| selftest | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 修复后 2 次（`fix-A`/`fix-B`）全部退出 0；`fix-A` 契约栏 **0**（判据运行） |
| 判定比对 | `compare-records.py run1 fix-A … --band … --rename-table … --batch B2 --strict` | `structural=0 conditional=0 identity=0 declared=27 **contract=0** rename=0`、`9 entries required, 9 satisfied`、**退出码 0** |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0、**0 字节输出** |
| inspectcode | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e2a3-check/jb-inspectcode.xml WinForward.slnx` | 退出 0；解析 XML：`<Issue>` **0**、`<IssueType>` **0**；日志确认 515 个文件被 Inspecting（含 13 个新/改动文件），无 `CSharpErrors` |
| 判定运行次数 | 本轮自跑 selftest **7 次**（旧二进制 5 + 新二进制 2） | 6 次 `contract=0`；1 次（`run-C`）的移动 = 同二进制对跑的子集 |

> `rg -c '<Issue'` 会把 `<IssueTypes>`/`<Issues>` 数进去，**必须解析 XML 元素**才得到 0。

---

## 4. 突变验证

全部突变只改一个文件、跑完即还原；产品与测试文件按 `cmp` / `sha256sum` 逐文件核对回冻结值
（备份留在 `/tmp/e2a3-check/orig/`）。修复前的五个突变（M1–M5）还原后 `git diff | sha256sum` 逐位回到
`a01fd9e0…`（与突变前相同）；修复后的突变（Q 重跑、D1）还原后逐文件 `cmp` 为空，其中 D1 的还原把
`linux-x64` 产品 dll 的 sha256 带回 `8c188dd0…`，即当前发布产物。

冻结（修复后）哈希：

| 文件 | sha256 |
|---|---|
| `Client/Arms/LatencyArm.cs` | `8779c4233576b40c…` |
| `Client/Arms/UdpLatencyPolicy.cs` | `f0fa87b33d4fbcc3…` |
| `Client/Arms/LatencyTcpPolicy.cs` | `05bf1bf99dfa2cbd…` |
| `Client/Arms/LossArm.cs` | `ae5da7d1d1aa7695…` |
| `Client/Arms/MixArm.cs` | `f6fc1278b046129d…` |
| `Client/Arms/DnsArm.cs` | `574a51be05f966e6…` |
| `Client/Lanes/ReplyClassifier.cs` | `f0939f4d60d2266d…` |
| `Client/Lanes/TcpLaneTransport.cs` | `c6370c36a5040051…` |
| `Client/Lanes/UdpLaneTransport.cs` | `de77ee9c44e2d85d…` |
| `tests/…/ReplyClassifierTests.cs` | `ca788b7572e9bb6d…` |

| # | 目标契约 | 突变 | 期望 | 观测 | 还原 |
|---|---|---|---|---|---|
| M1 | 分配 gate 覆盖**真实 adapter** | 在 `UdpLaneTransport.SendAsync` 里 `GC.KeepAlive(new byte[8]);` | 必红 | 正证红：`batches [4608, 4096 ×5], sentOk 768, sendCalls 768`（32 B/次）；反证事实仍绿 | `cmp` OK，哈希回 `de77ee9c…` |
| M1b | （方法学负结果）注入落在被测路径**之外** | 同一注入但放进 `UdpLaneTransport.ReceiveAsync` | 应绿 | gate **仍绿** —— 窗口只罩发送路径，注入必须落在被测路径上 | 同上 |
| M2 | LAT 四步顺序（D18.5 #3） | `Book` 里把 `ReleaseInFlight()` 提到 `TryTakePending` 之前（= 旧码行为） | 必红 | 3 条事实红：`ADuplicateReplyCountsAsReceivedAndUnmatchedAndReleasesNoSecondSlot`、`AReplyForASequenceThisSocketNeverSentIsUnmatchedAndHoldsNoSlot`、`ThePendingBookAndTheInFlightWindowAlwaysCountTheSameRequests` | `cmp` OK，回 `f0fa87b3…` |
| M3 | `outstandingAtTeardown` 的 arm 侧接线 | 删掉 `Outstanding += counts.DeferredPending;` | 若有覆盖则红 | **245/245 全绿** —— 无覆盖（U1 的证据） | `cmp` OK，回 `8779c423…` |
| M4 | TCP 终结性映射（D18.6 #3） | 把 `BadChecksum` 并进 `BadMagic/BadLength` 的终止分支 | 必红 | `AFrameWhoseChecksumFailsIsOneBadMessageAndAFrameBoundaryLossIsTerminal` 红 | `cmp` OK，回 `c6370c36…` |
| M5 | UDP 超长数据报不是静默 payload | 超长分支返回 `Payload` 而不是 `Malformed + Truncated` | 必红 | `ADatagramTooLargeForTheDestinationIsNamedAsTruncatedAndNeverAsCorrupt` 红 | `cmp` OK，回 `de77ee9c…` |
| Q | "连接 id 先于 filler"（D18.3 保持项） | `Classify` 里把 filler 判据提到 id 判据之前 | 必红 | **修复前绿（恒真，F1）→ 修复后红**：`Expected: ForeignConnection / Actual: Corrupt` | `cmp` OK，回 `f0939f4d…` |
| D1 | 构建决定性（哈希变化的归因） | 给 `ReplyClassifier.cs` 尾部追加一行注释 → 构建 | 哈希必须变 | `809e0ff8…`（原 `8c188dd0…`）；还原后重新构建**逐位回到** `8c188dd0…` | `cmp` OK |

M1/M2/M4/M5/Q 是**实现**的契约（检验测试有效性），M3 是**覆盖缺口**的负证据，D1 是哈希归因。

---

## 5. 判据运行（修复后的二进制，`8c188dd0…`）

| # | 比对 | 宿主负载（跑前→跑后） | 结构 | 条件键 | 身份 | 已声明 | **契约** | 改名 | 读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|---|
| P1 | `run1` → `fix-A`（**判据**） | 5.44 → 3.86 | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 296/366 | **0** |
| P2 | `run1` → `fix-B`（确认） | 3.86 → 2.71 | **0** | 0 | 0 | 27 | 6 | 9/9 satisfied | 295/366 | 1 |
| N1 | `fix-A` → `fix-B`（**同二进制**） | — | 0 | 0 | 0 | — | **19** | — | 290/366 | 1 |

P2 的 6 条 = `records/BASE.jsonl` 的 `metrics/loss/reordered` 0 → 4 与 `reorderRate` 0 → 0.001599（各按
min/max/mean 计 3 条）；N1 的 19 条 = 13 条"改名后无带宽"的路径 + 这同样 6 条。
按 `measurement-harness.md` §3.5：**零宽键上的契约发现由同一二进制的第二次运行确认**，N1 正是这个确认，
故 P2 的 6 条不是回归。P1（安静、无该噪声）给出契约栏 0。
