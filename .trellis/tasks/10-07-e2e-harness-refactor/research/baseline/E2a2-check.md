# E2-a2 check 报告（`Client/Lanes/` 接缝 + 引擎 + fake transport 测试）

被检查的树：`master` 未提交工作树（HEAD `64e1b72`）。本文件是 check 轮的证据留档，与 `E2a2-vs-run1.md`
（实现轮的回归比对）配套。**产品源码一行未改**：check 修复后 `linux/WinForward.E2E.dll` 的 sha256 仍是
`faeaeaf1912b5feb9db3a040991a1a05c498786520e6d84c82742ed979592c42`，与实现轮冻结树逐位相同（`Deterministic=true`）；
两处修复全在测试工程内（§1）。

## 0. 结论一句话

**E2-a2 可以提交**：D18.1/D18.2/D18.5 的逐条契约（计数器映射、WouldBlock 语义、grace 三出口与 join 顺序、
分配 gate 与反证、不相交反射含负控、并发恒等式、零消费者、规范）全部通过独立复核；check 轮修了 **2 处测试侧
缺陷**（1 处恒真断言、1 处无覆盖的 drain 出口），报告 **5 项**留给 2a-3/后续（其中 1 项是接缝量能缺口，
**均不阻塞本批提交**——本批零消费者，没有任何发布值随本批改变）。

---

## 1. 已修复的问题

| # | 文件:行 | 改了什么 | 为什么 | 反证方式 |
|---|---|---|---|---|
| 1 | `tests/WinForward.E2E.Tests/Lanes/LaneEngineSendTests.cs:175,187` | `ADeferredIntentGoesOutLaterInFifoOrderAndNeverCountsASecondSlot` 的 offer 循环由 rate 0 改为 `ratePerSecond: 200`，并加一条"重试的 intended 必须互不相同"的守卫 | `ratePerSecond: 0` 时 `Pacer.IntendedTicks(i) == StartTicks` 对**所有** i 成立，于是"重试仍带首次 offered 的 instant"的等式**恒真**：把重试改成带当前槽的 instant 照样绿（这句断言的语义正是 D18.5 #7/#8 要保的东西——延迟样本的年龄绑在它被想要的时刻上） | 突变 R（`Build(deferred.Sequence, deferred.IntendedTicks, …)` → `Build(deferred.Sequence, intendedTicks, …)`）：改前 **Passed**，改后 **Failed**，`Expected 56451300133895 / Actual 56451305133895`（正好一个 5 ms 配速间隔） |
| 2 | `tests/WinForward.E2E.Tests/Lanes/LaneTestDoubles.cs:473,483` + `LaneEngineTeardownTests.cs:91-108` | `LaneTransportFake` 加 `EndOfStreamAtReceive` 旋钮（对端 FIN 形状），新增 `AReceiveLoopThatEndedOnItsOwnEndsTheGraceDrain` | drain 的三个出口里"**接收循环已自行结束**"（`!receive.IsCompleted`，旧 `GraceDrainAsync:862/878` 逐字带过来的条件）此前**没有任何用例覆盖**：所有 fake 的接收都只会被取消。这条正是 TCP lane 对端半关（`EndOfStream`）的路径，2a-3 迁移时是活条件 | 突变 S（去掉 `&& !receive.IsCompleted`）：新用例 **Failed**，`SettlesAfterTheOfferLoop` `Expected 1 / Actual 756`、整轮耗时 1 s（drain 的 1 s 上限被烧完，而 offer 窗只有 0.03 s）|

范围声明：两处都在本轮改动面内（同一批新增的测试文件），**未改动任何生产代码**，也**未改动既有断言的语义**
（第 1 处只是把恒真断言变成真断言，其余断言逐字保留）。`dotnet test` 计数 210 → 211、Lanes 22 → 23。

---

## 2. 未修复但需上报

| # | 项 | 原因 | 影响 | 是否阻塞提交 |
|---|---|---|---|---|
| 1 | **`metrics/{tcp,udp}.outstandingAtTeardown` 的"未发出"半边在接缝里没有对应计数**：旧 `LatencyArm.cs:460/691` 是 `pending.Count + unsent`，`unsent = SendLoopAsync 返回的 backlog.Count`（= 结束时 defer 队列深度）；`LaneCounts` 的七个计数里没有它，`LaneState` 私有、`Snapshot()` 只给七个字段 | D18.1 把引擎计数**显式枚举为七个**，加第八个是 DD 级裁定，check 不改 | 2a-3 的"计数→派生量接线表"必含 `outstandingAtTeardown`（D18.5 #12 的零宽键之一）。目前只有一条**带前提**的推导：`unsent = (DeferredQueued − DeferredDropped) − ((SentOk + SendFailures) − (Supplied − DeferredQueued))`，它只在策略**从不返回 `Skip`** 时成立（`Skip` 在 retry 上会静默消费一个 intent 而不动任何计数），而 `Skip` 是 D18.5 #7 明确承认的第三态 | 否（本批零消费者，发布值不变）。**建议 2a-3 开工前先裁定**：要么让策略侧自记"重试发出/重试跳过"，要么请 DD 所有者裁定 `LaneCounts` 是否加一个 `DeferredPending` |
| 2 | **发送失败的两种形状与旧代码计数不一致**（新引擎 `LaneEngine.cs:196-233`） | ① 同步抛 `SocketException`：旧 `SendFrameAsync:550` 的 `socket.SendAsync` 在 try **之外**，异常逃到 `SendLoopAsync` 的 `catch (SocketException)` → offer 循环**结束**且 `_sendFailures` **不 +1**；新引擎把整个调用包在 try 里 → `SendFailures++` + `OnSent(false)` + 循环**继续**。② 异步失败（`IsCompleted == false` 后 await 抛）：旧先 `_sendWouldBlock++` 再 `_sendFailures++`（两者都加）；新只加 `SendFailures`，wouldBlock 只作为参数交给 `OnSent` | `metrics/*.sendWouldBlock` 与 `gates/sendFailures` 都是**发布键**，`jitter-band.json` 里它们的 `maxAbsDelta = 0`。形状 ① 是"更宽容"的行为变化（多测到样本），形状 ② 会让 `sendWouldBlock` 比旧少 1。基线里这两个计数恒为 0（selftest 无发送失败），所以本批证据不受影响 | 否（本批零消费者）。**2a-3 的差异清单必须逐条登记**（D18.3 要求），或按旧语义把 `if (wouldBlock) _state.SendWouldBlock++;` 加进 catch 分支对齐 |
| 3 | **TCP 接收侧的终结性映射必须在 adapter 里选对**：旧 `ReceiveLoopAsync:608-611` 把 `FrameReadStatus` 的 `BadMagic/BadLength/BadChecksum` 记 `_protocolErrors++` 并 **return（终止）**；旧 `:618` 的接收 `SocketException` 也记 `_protocolErrors++`。新接缝的 `Malformed` 文档写明**非终结**、`IoError` 才是终结 | adapter（`TcpLaneTransport`）是 2a-3 的产物，本批没有 | 若 2a-3 把这三个状态映成 `Malformed`，TCP lane 会在失步的流上继续读；正确映射是 `IoError`（`LaneReceiveResult.Detail` 用 `FrameDecodeError.BadMagic/BadLength/BadChecksum` 携带原因——枚举已覆盖这五个值）。`protocolErrors`/`remoteClosed` 的接线同样要登记 | 否 |
| 4 | 分配 gate 只用 **fake** collaborator | `hot-path.md`（`quality-guidelines.md:48` 转述）要求"任何真实协作者可能分配的热路径，至少一条对真实协作者的 gate（loopback）"；2a-2 还没有真实 transport | 现在的 gate 覆盖 `BuildRequest → 引擎 SendAsync → OnSent` 的调用线程窗口（突变 N2 证明它看得见 `BuildRequest` 里的分配），但看不见真实 socket adapter 内部的分配 | 否（真实 adapter 在 2a-3 才存在）。**2a-3 收尾时必须补一条真实 transport 的 gate** |
| 5 | `SettleBurstLimit = 8` 用尽后 `IsDrained` 仍为 false 时引擎**静默返回**（`LaneEngine.cs:97-104`） | 引擎没有"策略没排空"的错误通道，加一条会改接缝的错误面（DD 只写"最后一次 `Settle`"） | `ILanePolicy.Settle` 的文档承诺"把队列取空"，只要真实策略每次 `Settle` 全排空（fake 与 `SettlingPolicy` 都是），8 次上限永不可达；但一个"每次只结一条"的策略会让 `received`/`outstandingAtTeardown` 少记且无任何信号 | 否。建议 2a-3 在接线表里显式写"`Settle` 每次必须排空整队" |

---

## 3. 逐条验证结论

### 3.1 计数器映射（要求 1）

- **`DeferredDropped ⊂ DeferredQueued` 且逐值等于旧代码**。旧 `OfferFrameAsync:532-537`（UDP `:758-762` 同形）：
  `state._windowOverflow++; if (!backlog.TryEnqueue(request)) state._backlogDrops++;`，`DeferredQueue.TryEnqueue` 的
  判据是 `_requests.Count >= _capacity`（`:980-989`）；新 `LaneEngine.Defer:290-302`：`DeferredQueued++; if (Deferred.Count >= BacklogLimit) { DeferredDropped++; return; }`。
  逐行同构：**每个"发现窗口关闭的槽"两个计数都按同一条判据落账**，`BacklogLimit` 就是 `plan.BacklogLimit`（旧 `LatencyPlan.BacklogLimit`）。
- **一次一槽、重试不增**：`Defer` 只在两处被调用——`OfferSlotAsync` 的 `blocked` 短路（`:175`）与新建槽的 `Defer` 决策（`:188`）；
  重试路径（`:151-169`）**只 dequeue、不 Defer**。旧代码的 `_windowOverflow++` 同样每槽一次。
  `blocked` 与旧 `while (Interlocked.Read(ref _inFlight) < window && …)` 的失败条件等价（对 LAT 策略，
  `BuildRequest == Defer ⟺ inFlight >= window`；策略侧无第二真相，见 §3.5）。
- **`Supplied` 每槽一次**：`_state.Supplied = slot + 1` 在每个循环迭代一次（`:116-125`），`slot` 就是 0 基下标，
  与旧 `index++; state._supplied = index;` 逐字等价；`ASkippedSlotCountsNothingAndIsStillASuppliedSlot`（4 supplied / 0 send）、
  `ADeferredIntentGoesOutLater…`（6 supplied / 11 build / `OfferedSlots == Supplied`）两条 fact 在 check 轮仍绿。
- `ScheduleTruncated`：新 `Clock.Now < DeadlineTicks`（每 run 全新状态）对应旧 `Clock.Now < deadlineTicks || state._scheduleTruncated`，
  旧字段只在同一处置位、`AddFrom` 用 `|=` 合并（`:504`），故 `||` 是恒等冗余，非行为差异。

### 3.2 WouldBlock 语义（要求 2）

- 取值点：`LaneEngine.cs:200-206`，`wouldBlock = !send.IsCompleted;` 在**唯一一次 `await` 之前**；
  计数是并集 `if (wouldBlock || result.WouldBlock) _state.SendWouldBlock++;`（`:217-220`），**每次发送最多 +1**。
- **两条 fact 都能红，且各自只对一种突变红**：
  - 突变 M（`IsCompleted` 采样挪到 await 之后）：`AnIncompleteSendIsCountedAsWouldBlockEvenWhenTheTransportDoesNotSaySo`
    **红**（诊断 `supplied 3, sentOk 3, wouldBlock 0, failures 0, sends 3`）；同批的
    `AReportedWouldBlockIsCountedOncePerSendAndNotTwice` 仍**绿**——它本来就对采样位置不敏感（transport 自报 `true`）。
  - 突变 N（拆成两个 `if` → 双重计数）：`AReportedWouldBlockIsCountedOncePerSendAndNotTwice` **红**（`wouldBlock 6`）。
  即"采样位置"由第一条 fact 钉、"不重复计数"由第二条钉，覆盖无重叠。
- `ILaneTransport.SendAsync` 的文档（`LaneTransportContracts.cs:91-98`）把 transport 自报与引擎自读表述为
  "同一属性在接缝两侧的观测"，与实现一致。

### 3.3 grace 与终止（要求 3）

- **三个出口都在**：`LaneEngine.cs:275` `while (Clock.Now < until && !receive.IsCompleted && !_policy.BookEmpty)`
  = 上限（`DrainLimitTicks`）/ 接收循环已结束 / book 空；旧 `GraceDrainAsync:862` 是同一条件的
  `Clock.Now < until && !receive.IsCompleted && !pending.IsEmpty`。
- **注入短上限复验**：teardown 五条 fact 注入 `DrainLimitTicks ∈ {0.05 s, 1 s}`：
  `AnEmptyBookEndsTheGraceDrainWithoutASettle`（book 空 → 0 次 drain settle）、
  `TheGraceDrainStopsAsSoonAsTheBookIsEmpty`（2 tick 后停）、`TheGraceDrainStopsAtItsBoundWhenTheBookNeverEmpties`
  （50 ms 上限 → 放弃，book 仍非空）。突变 O（删 `!BookEmpty`）让前两条同时红（`Expected 1 / Actual 742`）。
- **接收循环出口**：check 轮补的 fact；突变 S 变红（见 §1 #2）。
- **"join 先于最后一次 `Settle`"**：`RunAsync:88-104` 顺序 = `OfferLoop` → `GraceDrain`（内含 settle）→ `CancelAsync` →
  `await receive` → settle 束。突变 P（把 settle 束挪到 join 之前）让 `TheReceiveLoopIsJoinedBeforeTheFinalSettle` **红**。
- **没有无界循环/无界等待**：offer 循环由上界 + 取消两重约束（rate 0 时 `Pacer.WaitUntil` 不查 token，
  但循环头的 `!cancellationToken.IsCancellationRequested` 会退出）；重试循环每次迭代要么 break 要么 dequeue（有界）；
  receive 循环每次迭代都 `await` transport；drain 有 `DrainLimitTicks` 上限；settle 束上限 8。

### 3.4 分配 gate（要求 4）

- **窗口在调用线程上、覆盖 `BuildRequest→SendAsync→OnSent`**：`AllocationWindowTransport.SendAsync` 在**第 1 次**
  发送里开窗、第 256 次里关窗，批量 6 × 256 次；两处断言 `SentOk == 1536` 与 `SendCalls == 1536`（闸门不能靠"没跑到"通过），
  `BatchThreadIds` 逐批断言 = 调用线程。窗口边界之间的 255 个 `BuildRequest` 与 256 个 `OnSent` 都在窗内。
- **反证有两个方向**：
  - 用例自带的反证（策略每次 `BuildRequest` 分配）：`TheGateGoesRedWhenThePathAllocatesOnPurpose` 断言**每一批 > 0**，绿。
  - check 侧突变 N2（把分配塞进引擎的 `Build` 包装，即真正的 `BuildRequest` 调用点）：
    `var scratch = new byte[8]; GC.KeepAlive(scratch);` → 绿闸门 **红**，`the send path never became allocation-stable: batches [8160, 8160, 8160, 8160, 8160, 8160]`。
    **8160 / 255 = 32 B**，正好是 255 次分配（每个测量批覆盖第 2…256 次 `BuildRequest`）——这条数字同时证明窗口确实罩住了 `BuildRequest`。
- **不会假绿**：`Debug.Assert`/`Console.`/`Trace.` 在 `Client/Lanes/**` 与 `Lanes/**` 测试里 `rg` 归零；断言里的
  字符串插值都在测量窗**之外**求值；窗口每批的开/关都在同一个方法体内，没有 `await` 跨线程。
  （附带记录一个**方法学坑**：最初那次突变写成 `_ = new byte[8];`，闸门**仍绿**——优化 JIT 把结果未使用的 `newarr` 消掉了；
  这正是测试文件里"策略把数组写进字段、`OnSent` 里 `GC.KeepAlive`"那句注释存在的原因，不是闸门漏检。凡做这类突变必须
  让分配可逃逸，否则得到的是假阴性。）
- **按 `hot-path.md` 的开窗/配对规则**：readiness（首个 0 批）+ 至少 4 个稳定批 + 断言**精确 0**（不放宽为界）+
  线程 id 未变 + 与调用计数配对，四条都在。

### 3.5 `LaneCounts` ↔ 策略不相交反射（要求 5）

- 正向：`Collisions(typeof(LaneCounts), typeof(LanePolicyFake))` = ∅；且断言策略侧确实暴露计数属性
  （`AcceptedSends`/`InFlight`）与非空私有字段集，交叉发生在两个**非空**名字集上。
- 负控两半都真的命中：`TheCheckSeesBothACollidingPropertyAndACollidingPrivateField` 对
  `CollidingCounters`（同时带 `Supplied` 属性与 `_sentOk` 私有字段）断言两条都在命中集里。
- **负控非空转**：突变 Q（删掉 `GetFields` 那半）→ 负控 **红**（`Item not found in collection: "_sentOk"`）。
- 名字规范化：策略侧 `TrimStart('_')` + `OrdinalIgnoreCase`，所以"删属性留 `_sentOk` 字段"逃不掉（D18.5 #13 的原意）。
- 说明：真实策略状态类型（`UdpLatencyState`/`LatencyTcpState`）本批仍是旧臂里的 `internal` 类、无属性、无消费者，
  不相交断言落在 `LanePolicyFake` 上是本批唯一可做的落点（D18.1 的落地条款在 2a-3 才换成真实类型）。

### 3.6 并发契约（要求 6）

- **单写者**：`SettlingPolicy` 的 `OnReceive` 只入队（无任何计数，D18.5 #2），`Settle` 只在发送线程跑；
  `Assert.True(policy.SendThreadId == callerThread)` 直接钉"结算在调用线程（=引擎线程）"。
  接收角色的线程 id **有意不比**（调用线程本身是池线程，发送角色一停接收续体可以合法地落回它）——
  这一点在 `E2a2-vs-run1.md` §6 已作为替身缺陷的记录修正过，check 复核认可。
- **恒等式**：`counts.SentOk == policy.Sent == transport.Delivered == policy.Settled == 1024`、
  `Pending == 0`、`InFlight == 0`、`IsDrained`，并且**逐序号恰好结算一次**（`Bookings[i] == 1` 全部）。
- **负载下重复**：本 check 共跑 Lanes 全套 **67 次零失败**——修复前 20 次串行 + 3 轮 × 6 路并发（18 次）；
  修复后 1 + 10 次串行 + 3 轮 × 6 路并发（18 次）。用例内部是 `Rounds = 4 × 1024` 条回复。
  （未改 `Rounds`：`E2a2-vs-run1.md` §6 的"4 轮 × 1024"是既有记录口径，改它会让证据与代码脱节；
  运行级重复由上面的 67 次覆盖，`E2a2-vs-run1.md` §6 记的 10 轮 × 6 路 soak 也是同一手法。）
- **结算携带收到时刻**：`ReceiveLoopAsync:242-247` 在接收线程 `Clock.Now` 取值并随 settlement 传递；
  `TheSettlementCarriesTheReceiveInstantAndNotTheSettleInstant` 用 20 offers/s 把"收到"与"结算"拉开 50 ms 后断言
  `LastSettledReceivedTicks < IntendedTicks(1)`。突变 T（把 fake 的落账改用 settle 时刻）→ 该 fact **红**（`… is not before the instant slot 1 was paced for …`，差 22199 ticks）。

### 3.7 零消费者与可比性（要求 7）

- **零消费者**（复跑实现轮的判据，exit 1 = 零命中）：
  ```bash
  rg -n "Client\.Lanes|LaneEngine|ILanePolicy|ILaneTransport|LaneCounts|LaneSlotDecision|LaneReceiveResult|LaneSendResult|LaneOpenResult|LaneEngineOptions|LaneReceiveKind|DeferredQueued|DeferredDropped" \
     benchmarks/WinForward.E2E src -g '!benchmarks/WinForward.E2E/Client/Lanes/**'    # exit 1
  ```
- **本批不改任何臂**：`git status --short benchmarks tests src` = 只有两个新目录；`git diff --stat -- benchmarks tests src` 为空
  （所有改动都是新增未跟踪文件）。
- **`contract=0` 可复现**（自己重跑，带 `--band` 与 `--rename-table`）：
  ```bash
  benchmarks/WinForward.E2E/scripts/publish.sh                      # exit 0
  cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json   # exit 0
  R=.trellis/tasks/10-07-e2e-harness-refactor/research
  python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/wf-bench/selftest \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
  # 第 1 次（负载 6.0→2.3）：exit 0
  # summary: structural=0 conditional=0 identity=0 declared=27 contract=0 rename=0 readings=293/366
  # rename table check: 9 entries required, 9 satisfied, 0 not observed
  ```
  与 `E2a2-vs-run1.md` 的 P10 行逐栏一致（读数栏 293/366 vs 291/366 属宿主/boot，D17.4 只作信息性输出）。
- **判据运行（A）之后又跑了三次同二进制 selftest（B/C/D），再加一对同二进制对跑作为噪声地板**（本机被用户的
  `remote-dev-serv`/Rider 占住，1 分钟负载在 1.7–11.7 之间摆动）。四次判据运行的二进制都是 `faeaeaf1…`（逐位相同）：

  | 运行 | 负载（跑前→跑后） | 结构/条件键/身份 | 契约 | 改名表 | 退出码 |
  |---|---|---|---|---|---|
  | A（判据） | 6.0 → 2.3 | 0 / 0 / 0 | **0** | 9/9 satisfied | **0** |
  | D | 3.2 → 1.7 | 0 / 0 / 0 | 6 | 9/9 satisfied | 1 |
  | C | 2.9 → 2.4 | 0 / 0 / 0 | 15 | 9/9 satisfied | 1 |
  | B | 6.8 → 11.7 | 0 / 0 / 0 | 35 | 9/9 satisfied | 1 |
  | **B → C（同一二进制对跑）** | — | 0 / 0 / 0 | **61** | 未带表（`declared=0`） | 1 |

  即：**同一二进制两次运行之间能移动 61 条契约计数**，比任何一对"基线↔本批"的移动都大；D 的 6 条只有一族
  （`LATLOAD metrics/tcp.{outstandingAtTeardown,unmatchedReplies}` 各 +1，正是 D17 记的零宽族：一个回复在 drain 上限之后才到），
  B/C 的移动也都落在 `E2a2-vs-run1.md` §3 已归因的族里。按 `measurement-harness.md` §3.5 的要求，零宽键上的契约发现
  "必须由同一二进制的第二次运行确认后才可称为回归"——本 check 正是这么做的，结论是**没有回归**。
- **读数栏逐条归类**（`--strict --json-out`，run D 的 233 条越带读数，无一条未归类）：

  | 族 | 条数 | 叶子（条数） |
  |---|---|---|
  | 宿主资源采样 | 96 | `cpuSeconds` 22、`privateBytes` 20、`envWorkingSetBytes` 11、`generatorCpuSeconds` 11、`peakWorkingSetBytes` 11、`workingSetBytes` 11、`threads` 10 |
  | 延迟直方图样本 | 67 | `p90Us` 12、`maxUs` 10、`p999Us` 10、`meanUs` 9、`p99Us` 9、`minUs` 8、`p50Us` 8、`count` 1 |
  | boot 相对时钟 | 45 | `startedTicks` 14、`endedTicks` 14、`ticks` 12、`elapsedSeconds` 2、`connectTicks` 1、`transferTicks` 1、`wallSeconds` 1 |
  | 速率读数 | 11 | `achievedRate` 8 条、`goodputBps` 2、`goodputMbps` 1 |
  | 由读数派生的 ceiling / connect 均值 | 11 | `windowCeilingMs` 4、`inFlightCeilingMs` 2、`meanConnectMs` 4、`meanTransferMs` 1 |
  | 账本观测计数（记录条数不是其值） | 3 | `ledger::bytes`、`ledger::received`、`ledger::sources/datagrams` |

  全部是 D15 的"读数类"（逻辑上随宿主与 boot 变），没有一条落在 D15 的契约计数类；读数栏按 D17.4 不参与判定。
  这些移动**不可能由本批造成**：四次运行的产品 DLL 与实现轮冻结树逐位相同（§1 开头），且本批零消费者、臂一行未改。

### 3.8 规范（要求 8）

- **有效行 ≤ 400**（非空非注释口径）：`LaneEngine` 211、`ILanePolicy` 16、`LaneCounts` 11、`LaneEngineOptions` 16、
  `LaneTransportContracts` 22；测试侧最大 `LaneTestDoubles` 343、`LaneEngineSendTests` 173、`LaneEngineConcurrencyTests` 148，
  全部达标（check 加的一条 fact 使 `LaneEngineTeardownTests` 79 → 96）。
- **D18.2 线程契约写进类文档**：`LaneEngine.cs:12-29`（"**Threading contract (D18.2).**" 一段，含"send thread exclusively calls
  `BuildRequest`/`SendAsync`/`OnSent`/`Settle`…receive loop … only ever calls `OnReceive`…the engine defines no settlement type"）
  与 `ILanePolicy.cs:31-46`（"**Thread contract (D18.2, D18.5 #2/#4).**"，含"counters move in `Settle`"）。
  `LaneTransportContracts.cs:72-79` 另写明两个失败通道是结果而非异常。
- **无变更日志式注释**：`rg -i "previously|used to|no longer|changed from|formerly|was renamed"` 归零；
  仅有的历史比较是"与旧实现的非显然等价"陈述（`LaneTransportContracts.cs:23`、`LaneCounts.cs:22`、`LaneEngineOptions.cs:48-49`），
  它们对照的 `LatencyArm` 仍在树里、逐字未改，属 README「Non-obvious properties」口径。
- **反射只用 `typeof(...)` 字面量**：`LaneCountsDisjointnessTests.cs:19/25/35` 三处全是 `typeof(...)`；
  辅助函数用 `Type` 形参但带 `[DynamicallyAccessedMembers(...)]` 标注，调用点全是字面量——这正是 D14.20 要防的
  `IL2075` 的合规写法（`EnableTrimAnalyzer`/`EnableAotAnalyzer` 全仓开、`TreatWarningsAsErrors=true`，构建零警告即为硬证据）。
- **`LaneState` 私有嵌套**：`LaneEngine.cs:304-342` `private sealed class LaneState` 嵌在 `LaneEngine<TTransport>` 内，
  其成员是 `internal`（对程序集可见）但外层类型不可命名，故外部（含本程序集其他类型）**无法声明**它的字段；
  对外只有 `RunAsync` 返回的不可变 `LaneCounts`。
- 文件/类型布局：`Client/Lanes/` 按域成目录（`directory-structure.md` 的目录=命名空间后缀规则）；`LaneTransportContracts.cs`
  （interface + 三个结果词汇）与既有先例 `UdpTransportContracts.cs`（"接口/工厂/接收结果词汇"）同形，
  `ILanePolicy.cs`（interface + 它的三态枚举）同理。测试侧 `Lanes/` 与既有的 `Shapes/`、`Fixtures/` 同属"主题子目录 + 本地 helper"，
  未进 `WinForward.TestSupport`（后者是跨测试工程共享的库，而这些 fake 依赖 E2E 程序集的 internal 接缝）。
- **pragma 实际数字**（D18.5 #10 要求"实施完成后用 `rg` 报实际值，上限 8"）：
  `rg -n "pragma warning disable S6966" benchmarks/WinForward.E2E --type cs` = **10 块**——`ArmContext.cs:75`（定义体）+
  9 个同步调用点：`LossArm:153`、`DnsArm:304/446`、`PersistentArm:238`、`LatencyArm:481/711`、`ReliabilityArm:200`、
  `MixArm:666`、**`LaneEngine.cs:118`（新）**。2a-2 是 10、不是 AC 记的 9：引擎把 LAT 的调用点**照搬**了一份，
  而 `LatencyArm:481/711` 的两处要到 2a-3 才删，故 2a-3 完成后应为 **8**（正好压在上限上，没有余量）。
  引擎**没有**引入非 async 的 `Pace(...)` 包装，所以这一处仍需 pragma，且 `disable`/`restore` 与注释都与
  `LatencyArm:481-484` 逐字相同（"pragma 与注释随代码搬进引擎"的 AC 达成）。`Client/Lanes/` 全部 **8 处**
  suppression（1 个 pragma 块 + 7 条 `// ReSharper disable once`：`LaneTransportContracts` 3、`ILanePolicy` 2、`LaneEngine` 2）
  都带可对本仓核验的理由。

### 3.9 门禁复核（全部修复之后，同一棵静止的树）

| 门禁 | 命令 | 结果 |
|---|---|---|
| publish | `cd benchmarks/WinForward.E2E && scripts/publish.sh` | 退出 0；三份产物齐全、两个 Windows 镜像名可区分；`linux/WinForward.E2E.dll` sha256 = `faeaeaf1912b5feb9db3a040991a1a05c498786520e6d84c82742ed979592c42`（**与实现轮冻结树逐位相同**，`Deterministic=true`） |
| build | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| test（E2E） | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 211, Total: 211`（实现轮 210 + check 新增 1） |
| test（Lanes 子集 ×重复） | `--filter "FullyQualifiedName~Lanes"` | 修复前 38 次、修复后 29 次，共 **67 次零失败**（含 6 轮 6 路并发） |
| selftest | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 4 次全部退出 0；判据那次（A）契约栏 **0**、退出码 0；B/C/D 三次见 §3.7 的宿主噪声地板 |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0、**0 字节输出** |
| inspectcode | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-e2a2.xml WinForward.slnx` | 退出 0；解析 XML：`<IssueTypes />`、`<Issues />` **均空**、无 `CSharpErrors`；日志确认 13 个新文件（`ILanePolicy.cs`/`LaneCounts.cs`/`LaneEngine.cs`/`LaneEngineOptions.cs`/`LaneTransportContracts.cs` + 8 个测试文件）逐个被 Inspecting |

> 注：`rg -c '<Issue'` 会把 `<IssueTypes>`/`<Issues>` 数进去（得 2），**必须解析 XML 元素**才得到 0——与 AGENTS.md 的告警一致。

---

## 4. 突变验证结果汇总

全部突变只改一个文件、跑完即还原；产品文件按 `sha256sum -c` 核对回冻结值，测试文件按备份 `diff` 核对。
冻结（修复后）哈希：`LaneEngine.cs` = `5746fbaa…`（= 实现轮值，未变）、`LaneEngineSendTests.cs` = `a0397950…`、
`LaneEngineTeardownTests.cs` = `e0540326…`、`LaneTestDoubles.cs` = `fb103b81…`。

| # | 目标契约 | 突变 | 期望 | 观测 | 还原 |
|---|---|---|---|---|---|
| M | `WouldBlock` 在 await **之前**采样 | `wouldBlock = !send.IsCompleted;` 挪到 `await` 之后 | 必红 | `AnIncompleteSendIsCounted…` 红（wouldBlock 3 → **0**）；`…NotTwice` 仍绿（对采样位置本就不敏感） | `sha256sum -c` 全 OK |
| N | 每次发送最多 +1 | `if (wouldBlock \|\| result.WouldBlock)` 拆成两个 `if` | 必红 | `AReportedWouldBlockIsCountedOncePerSendAndNotTwice` 红（**6**） | 同上 |
| N2 | 闸门覆盖 `BuildRequest` | 在引擎 `Build` 里 `var scratch = new byte[8]; GC.KeepAlive(scratch);` | 必红 | 绿闸门红：`never became allocation-stable: batches [8160 ×6]` = 32 B × 255 次 | 同上 |
| N3 | （方法学负结果）不可逃逸的 `_ = new byte[8];` | — | — | 闸门**仍绿**：优化 JIT 消掉了结果未使用的 `newarr`；**不是**闸门漏检（见 §3.4） | 同上 |
| O | `DeferredDropped ⊂ DeferredQueued`（掉队槽两个计数都加） | 把 `DeferredQueued++` 挪到容量判断之后 | 必红 | `AFullDeferQueueDropsTheNewestIntentAndCountsItInBothCounters` 红（`Expected 5 / Actual 2`） | 同上 |
| O2 | drain 的"book 空"出口 | 删 `!_policy.BookEmpty` | 必红 | `AnEmptyBookEndsTheGraceDrainWithoutASettle` + `TheGraceDrainStopsAsSoonAsTheBookIsEmpty` **双红**（`Expected 1 / Actual 742`） | 同上 |
| P | join 先于最后一次 `Settle` | 把 settle 束挪到 `CancelAsync`/`await receive` 之前 | 必红 | `TheReceiveLoopIsJoinedBeforeTheFinalSettle` 红（"the arm-end settle ran before the receive loop was joined"） | 同上 |
| Q | 不相交负控的字段半边 | 删掉 `Collisions` 里 `GetFields` 的那半 | 必红 | 负控红（`Not found: "_sentOk"`，命中集只剩 `["Supplied"]`） | 同上 |
| R | 重试仍带首次 offered 的 instant | 重试改用当前槽的 `intendedTicks` | 必红 | **修复前绿（恒真）、修复后红**：`Expected 56451300133895 / Actual 56451305133895` | 同上 |
| S | drain 的"接收循环已结束"出口 | 删 `&& !receive.IsCompleted` | 必红 | check 新增的 fact 红（settle `Expected 1 / Actual 756`、1 s vs 0.03 s） | 同上 |
| T | settlement 携带收到时刻 | fake 落账改用 `nowTicks`（结算时刻） | 必红 | `TheSettlementCarriesTheReceiveInstantAndNotTheSettleInstant` 红 | `LaneTestDoubles.cs` 按修复版备份还原，`diff` 为空 |

M/N/N2/O/O2/P/Q/S/T 是**实现**的契约（检验测试有效性），R 是**测试**的契约（检验修复有效性）。
`LaneEngine.cs` 在所有突变后回到 `5746fbaa…`，与实现轮逐位相同——本 check 轮没有在产品代码里留下任何痕迹。
