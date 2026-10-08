# E2-a3 回归比对（真实 transport + `ReplyClassifier` + `LatencyArm` 两 lane 迁移 + 5 处 `ConnectAsync` 机制统一；对照基线 run1）

本批是 E2-a 的第三小批（2a-3）：新增 `Client/Lanes/{TcpLaneTransport,UdpLaneTransport,ReplyClassifier}.cs` 与
`Client/Arms/{LatencyTcpPolicy,UdpLatencyPolicy}.cs`；`LatencyArm` 的 TCP/UDP 两个 lane 改走
`LaneEngine<TTransport>` + 各自策略（镜像的发送三件套与两个 `GraceDrainAsync` 删除）；`LossArm`/`MixArm`/
`DnsArm` 的回复阶梯改走 `ReplyClassifier`（三份合一），5 处 UDP `ConnectAsync` 落机制统一。
`src/` 一行未改。

判定线：**结构差异为空（含改名表键集）**、**契约栏零越带**（安静宿主上的判定）；`LatencyArm` 的发布键
按 D18.5 #12 的清单**逐值核对**；读数栏照 D17.4 只作信息性输出，越带项逐条解释。

---

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                        # 退出码 0（门禁 1）
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json                 # 退出码 0（门禁 4）
cp -r /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} /tmp/e2a3/<run>/

R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR <base> <after> --normalize $R/record-normalize.json \
        --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 --strict
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 |
|---|---|---|
| `run1`（基线） | A0（E2a2-vs-run1.md 与本轮 §3 记录） | — |
| `after-*`（本轮树，第一轮，**已被替换**） | HEAD `436225b` + 当时的本轮工作树 | `1c771abad5f4cf053f81e77bb5539829fc376294005c7e7c12bc451bf8aaafbe` |
| `final-*`（实现轮树，**判据**） | HEAD `436225b` + 实现轮工作树 | `7de645ae9092dd29f13517a99777dbbf0402ef448581b027a9b5f2f9294df604` |
| `fix-*`（check 轮树，**最终判据**） | 同 HEAD + check 轮的注释性修复（产品注释 3 处 + 测试 1 处） | `8c188dd08bcf7081534556d57a5d086e8643347862a449e07d097fbdb4368a22` |
| 说明 | check 轮只改注释，DLL 哈希按注释变化（决定性构建实验：尾加一行注释 → `809e0ff8…`，删掉 → 逐位回到 `8c188dd0…`）；**判据已在新二进制上整体重跑**（`run1 → fix-A`：structural/conditional/identity/contract 全 0、改名 9/9、exit 0） | — |

本轮共跑 **8 次** selftest，分两轮：第一轮（`after-run1..3`）在 `1c771aba…` 树上拿到同样的判定结果，但收尾时发现一处
UDP connect 事实的记账瑕疵（§9.1 偏离 4）并修正，故第一轮只留作方法学与噪声地板的记录；**判定用第二轮**。

| 运行 | 二进制 | 宿主（跑前 → 跑后） | 产物 | 用途 |
|---|---|---|---|---|
| `after-run1` | `1c771aba…` | 1.26 → 2.09 | `/tmp/e2a3/after-run1` | 第一轮判据（结论与第二轮一致） |
| `after-run2` | `1c771aba…` | 2.09 → 1.59 | `/tmp/e2a3/after-run2` | 第一轮确认 |
| `after-run3` | `1c771aba…` | 1.59 → 1.50 | `/tmp/e2a3/after-run3` | 第一轮确认，命中一次宿主噪声（§2.1） |
| `final1` | `7de645ae…` | 0.97 → 1.80 | `/tmp/e2a3/final1` | 实现轮判据（安静） |
| `final2` | `7de645ae…` | 1.80 → 1.21 | `/tmp/e2a3/final2` | 实现轮确认（安静） |
| 三次早期运行 | 中间树 | 7.14 → 1.38（inspectcode 刚结束，负载衰减中） | 未采用 | 方法学记录：产物缺 `target.out` 时比对会把"文件缺失"读成 3 条结构差异——复制产物必须整目录 |

`final1 ↔ final2`（同二进制）与第一轮的 `after-run1 ↔ after-run3` 用作**噪声地板**。
比对输出：`/tmp/e2a3/cmp-{run1,run2,run3,final1,final2}.txt`、`/tmp/e2a3/cmp-final1-final2.txt`、
`/tmp/e2a3/cmp-after{1-2,2-3,1-3}.txt`（产物未随任务提交，三条命令随时可重建）。

---

## 2. 判定线

| # | 比对 | 结构 | 条件键 | 身份 | 已声明 | **契约** | 改名 | 读数 | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|---|
| P1 | `run1` → `final1`（**判据**） | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 298/366 | 244/366 | **0** |
| P2 | `run1` → `final2` | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 294/366 | 233/366 | **0** |
| N1 | `final1` → `final2`（同二进制） | 0 | 0 | 0 | — | 13 | — | 290/366 | 161/366 | 1 |
| P3（第一轮） | `run1` → `after-run1` | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 296/366 | 220/366 | **0** |
| P4（第一轮） | `run1` → `after-run2` | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 296/366 | 216/366 | **0** |
| P5（第一轮） | `run1` → `after-run3` | **0** | 0 | 0 | 27 | 6 | 9/9 satisfied | 294/366 | 231/366 | 1 |
| N2（同二进制） | `after-run1` → `after-run3` | 0 | 0 | 0 | — | 19 | — | 294/366 | 166/366 | 1 |

**判定**：两次运行（判据 + 确认）**结构差异为空、契约零越带、改名表 9/9 satisfied、退出码 0**，即判据通过。

### 2.1 第一轮 P5 的 6 条契约移动是宿主噪声（不是回归）

第一轮的 `after-run3`（最安静的一次）多出 6 条契约移动，全部是 `records/BASE.jsonl` 的 loss 相位：
`metrics/loss/reordered` 0 → **88**、`metrics/loss/reorderRate` 0 → 0.035186（`sent = arrived = 2501`，无丢包、无重复、无 corrupt）。
第一轮的同一二进制对跑 `after-run1 ↔ after-run3`（N2）差出的正是这同样的 6 条（外加 13 条改名后无带宽可判的路径 = 19），
即"同一二进制也会量产这 6 条"。

成因可定位到被测端而不是客户端：靶机的 UDP echo 用 `_receiverCount` 个并发接收循环共享同一 socket
（`Target/UdpEchoServer.cs::RunAsync`），回复顺序因此取决于宿主调度；`LossArm` 只按到达顺序记账，`reordered` 是它的
观测值而非被改动面（本批对 `LossArm` 只换了回复分类那一段，`MarkArrival`/窗口/发送循环一行未动，`rg 'MarkArrival' LossArm.cs` 仍只有一处）。
判据按 `measurement-harness.md` §3.5：零宽键上的契约发现必须由**同一二进制的第二次运行**确认——第一轮的三次运行里
`after-run1`/`after-run2` 都是 0，第二轮的两次运行（`final1`/`final2`）都没有再出现。

### 2.2 N1/N2 的组成

同二进制对的契约栏全部由两类构成，且都不参与判据：

- 13 条改名后**无带宽**的路径（`metrics/latency/tcp.sent`、`metrics/udp.sent`、`metrics/desktops/udp.*`、`metrics/udp.lossRate`）：
  `jitter-band.json` 冻结在 B2 改名之前，其键集不含新拼写（这 13 条带着改名表与 run1 比对时是被正确判定的，P1/P2 的 `contract=0` 即证）。
- 第一轮 `BASE.jsonl` 的 loss 相位 `reordered`/`reorderRate`（§2.1）；第二轮不再出现。

也就是说：**凡是带宽表能判的路径，同一二进制的两次运行完全一致**（0 条越带）。

---

## 3. D18.5 #12 的零宽发布键逐值核对

清单（D18.5 #12）在 11 条记录上共 **34 个路径**（check 轮按四个模式严格匹配后的口径；实现轮写的 39 含 `receivedBytes` 这类前缀误配）：`metrics/*.received`（含 `metrics/latency/*.received`）、
`metrics/*.unmatchedReplies`、`metrics/*.outstandingAtTeardown`、`latency/*-rtt/count`。逐值核对脚本对
`run1` ↔ `final{1,2}` 与第一轮的 `run1` ↔ `after-run{1,2,3}` 五对全跑，结论：**每对都是 34/34 逐值相同，0 处差异**；check 轮另跑 7 次（旧/新二进制），唯一差异是 `run-C` 的一次 DNS 超时（`DNSALT latency/dns-rtt/count` 402→401，同二进制可复现）。

被迁移的两条 lane 的具体值（三种运行与基线逐值一致）：

| 记录 | 路径 | run1 | final1 | final2 | （第一轮 after-run1/2/3 同值） |
|---|---|---|---|---|---|
| `LAT` | `metrics/tcp.received` | 322 | 322 | 322 | 322 |
| `LAT` | `metrics/tcp.unmatchedReplies` | 0 | 0 | 0 | 0 |
| `LAT` | `metrics/tcp.outstandingAtTeardown` | 0 | 0 | 0 | 0 |
| `LAT` | `metrics/udp.received` | 161 | 161 | 161 | 161 |
| `LAT` | `metrics/udp.unmatchedReplies` | 0 | 0 | 0 | 0 |
| `LAT` | `metrics/udp.outstandingAtTeardown` | 0 | 0 | 0 | 0 |
| `LAT` | `latency/tcp-rtt/count` | 322 | 322 | 322 | 322 |
| `LAT` | `latency/udp-rtt/count` | 161 | 161 | 161 | 161 |
| `LATLOAD` | `metrics/{tcp,udp}.received` | 1601 | 1601 | 1601 | 1601 |
| `LATLOAD` | `metrics/{tcp,udp}.unmatchedReplies` | 0 | 0 | 0 | 0 |
| `LATLOAD` | `metrics/{tcp,udp}.outstandingAtTeardown` | 0 | 0 | 0 | 0 |
| `LATLOAD` | `latency/{tcp,udp}-rtt/count` | 1601 | 1601 | 1601 | 1601 |
| 两侧 | `latency/tcp-connect/count`（连接探针） | 8 | 8 | 8 | 8 |

`received == sentOk == supplied` 且 `unmatchedReplies == outstandingAtTeardown == 0`：两次运行里每个发出的请求都被
恰好一个回复消费（`latency/*-rtt/count` 与 `sentOk` 相等也在同一方向上佐证）。in-flight 不是发布键，它只需内部单测
（`LatencyPolicyTests.ThePendingBookAndTheInFlightWindowAlwaysCountTheSameRequests`）。

### 3.1 `achievedRate` / `sendWouldBlock` 与 A0 基线的量级对照

| 记录 | 读数 | run1 | final1 | final2 |
|---|---|---|---|---|
| `LAT` | `metrics/tcp.achievedRate` | 40.191 | 40.188 | 40.189 |
| `LAT` | `metrics/udp.achievedRate` | 20.096 | 20.094 | 20.094 |
| `LATLOAD` | `metrics/{tcp,udp}.achievedRate` | 200.051 | 200.043 | 200.046 |
| `LOSS` | `metrics/achievedRate` | 196.115 | 196.114 | 196.099 |
| `BASE` | `metrics/loss/achievedRate` | 480.831 | 480.881 | 480.833 |
| `DNS` | `metrics/achievedRate` | 50.220 | 50.193 | 50.206 |
| 全部记录 | `/{tcp.,udp.,latency/tcp.,latency/udp.}sendWouldBlock` | 0 | 0 | 0 |

量级相同（差异在小数第二/三位的抖动内，`LAT`/`LATLOAD` 的 `sent`/`supplied` 逐值相同），`sendWouldBlock` 处处为 0。

### 3.2 `--strict` 读数移动摘要（P1 的 244 条越带读数，逐条归类）

| 族 | 条数 | 叶子（条数） |
|---|---|---|
| 宿主资源采样 | 90 | `cpuSeconds` 22、`privateBytes` 20、`generatorCpuSeconds` 11、`peakWorkingSetBytes` 11、`workingSetBytes` 11、`envWorkingSetBytes` 10、`threads` 5 |
| boot 相对时钟 | 45 | `startedTicks` 14、`endedTicks` 14、`ticks` 12、`elapsedSeconds` 2、`transferTicks` 1、`wallSeconds` 1 |
| 延迟直方图样本 | 84 | `p999Us` 14、`p99Us` 14、`maxUs` 12、`meanUs` 12、`minUs` 11、`p50Us` 11、`p90Us` 11 |
| 速率读数 | 12 | `achievedRate` 5、`tcp.achievedRate` 3、`udp.achievedRate` 2、`goodputBps` 2、`goodputMbps` 1 |
| 由读数派生的 ceiling / connect / transfer 均值 | 11 | `inFlightCeilingMs` 3、`tcp.windowCeilingMs` 3、`udp.windowCeilingMs` 2、`tcp.meanConnectMs` 1、`meanConnectMs` 1、`meanTransferMs` 1 |
| 账本观测计数（记录条数不是其值） | 2 | `ledger::bytes` 1、`ledger::received` 1 |

全部落在 D15 的"读数类"（逻辑上随宿主与 boot 变），**没有一条落在契约计数类**；读数栏按 D17.4 不参与判定。
`latency/*` 的直方图统计量按 D17.1 归读数类，`windowCeilingMs`/`inFlightCeilingMs` 是它们的派生量（D15 已归读数）。

---

## 4. 接线表：每个计数如何喂 gates / notes / ceiling

**引擎（`LaneCounts`，发送侧）**

| 计数 | 喂给 | 说明 |
|---|---|---|
| `Supplied` | `metrics/{tcp,udp}.supplied`、`tcp.laneSupplied[]`、`validity` 的 idle 判据、`abandonedAtTeardown`/`clientSendLoss` 的减数 | 每槽一次，含 Skip/Defer 的槽 |
| `SentOk` | `metrics/{tcp,udp}.sent`、`tcp.laneSentOk[]`、`achievedRate`、`windowCeilingMs`/`inFlightCeilingMs`、`clientSendLoss`、两条 ceiling note 的 achieved 读数 | |
| `SendWouldBlock` | `metrics/{tcp,udp}.sendWouldBlock` | 每次发送至多 +1（`!send.IsCompleted \|\| result.WouldBlock`） |
| `SendFailures` | `metrics/{tcp,udp}.sendFailures`、`gates.sendFailures` | 两种失败形状都计数（D18.6 #2） |
| `DeferredQueued` | `metrics/{tcp,udp}.windowOverflow`、`gates.windowOverflow`、两条 ceiling note 的 reached 判据 | D18.5 #7 的显式映射，臂不另存一份 |
| `DeferredDropped` | `metrics/{tcp,udp}.backlogDrops`、`gates.backlogDrops`、`abandonedAtTeardown` 的减数 | D18.5 #7 |
| `DeferredPending` | `metrics/{tcp,udp}.outstandingAtTeardown` 的"未发出"半边（D18.6 #1） | 返回时 Defer 队列占用数 |
| `ScheduleTruncated` | `metrics/{tcp,udp}.scheduleTruncated`、`gates.scheduleTruncated`、`validity.Truncated`、条件 note「a lane never connected…」 | connect 失败或 offer 循环早停 |

**策略（`LatencyTcpState` / `UdpLatencyState`，接收侧 + 窗口）**

| 计数 | 喂给 | 说明 |
|---|---|---|
| `Started` | `metrics/{tcp,udp}.laneStarted`、`metrics/tcp.laneStarted`、`gates.lanesStarted`、`gates.laneShortfall`、note「lanes actually run」 | 0/1，由 lane body 置位 |
| `Received` | `metrics/{tcp,udp}.received`（**零宽键**）、`udp.lossRate` | udp：含重复应答（保持项）；tcp：每个成帧消息一次 |
| `Corrupt` | `metrics/{tcp,udp}.corrupt` | tcp：BadChecksum；udp：filler 不匹配 + 校验和可归因 + 无法解码 + 超长数据报 |
| `ProtocolErrors` | `metrics/{tcp,udp}.protocolErrors` | tcp：IoError（失步 BadMagic/BadLength、socket 错误）；udp：socket 错误 |
| `RemoteClosed` | `metrics/tcp.remoteClosed` | EndOfStream |
| `UnmatchedReplies` | `metrics/{tcp,udp}.unmatchedReplies`（**零宽键**）、`udp.lossRate` | 没消费任何 pending 请求的回复（含重复） |
| `ForeignConnection` | `metrics/udp.foreignConnection`、note 3 | udp 专有 |
| `InFlight` | 窗口准入（`BuildRequest` 的 `>= window` → `Defer`）；不发布 | 只有一条内部单测（D18.5 #12） |
| `Pending` | `metrics/{tcp,udp}.outstandingAtTeardown` 的 pending 半边（D18.6 #1）、`BookEmpty` | pending 书的容量 |
| `ConnectSamples`/`ConnectFailures`/`ConnectTicks` | `metrics/tcp.connectAttempts`（= samples+failures）、`connectFailures`、`meanConnectMs`（成功样本的均值）、note 4 | 由臂从 transport 的 connect 事实喂入（`BookConnect`） |

**臂（`LatencyTotals`，只做"引擎半边 + 策略半边"的求和）**

| 发布值 | 公式 | 备注 |
|---|---|---|
| `abandonedAtTeardown` | `max(0, Supplied − SentOk − SendFailures − BacklogDrops)` | 与原式逐字相同 |
| `clientSendLoss`（metric + gate） | `max(0, Supplied − SentOk)` | 与原式逐字相同 |
| `outstandingAtTeardown` | `Σ Pending + Σ DeferredPending` | D18.6 #1；两个半边分别来自策略与引擎 |
| `windowCeilingMs` | `InFlightCeilingMs(window, lanes, SentOk, elapsed)` | 与原式相同 |
| `gates.inFlightCeilingMs` | `TightestCeiling(validity.TcpCeilingMs, validity.UdpCeilingMs)` | TCP 用 `plan.Lanes`，UDP 用 1 |
| `gates.laneShortfall` | `Planned − Started + idle`，`idle = Started>0 ∧ Supplied==0` | 逐 lane 用 `book.Started` + `counts.Supplied` |
| `udp.lossRate` | `(SentOk − (Received − UnmatchedReplies)) / SentOk` | 与原式逐字相同 |
| `gates.windowMs` | 常量 0 | LAT 无毫秒窗口 |
| 14 条 note | 见 §4.1 | 文本逐字保留 |

### 4.1 note 的逐条来源（14/14，文本与旧文件逐字相同）

| # | note（首句） | 数值来源 |
|---|---|---|
| 1 | latency is measured from each request's intended send instant… | 无计数（口径声明） |
| 2 | sendWouldBlock counts sends the kernel did not accept synchronously… | 口径声明，对应引擎 `SendWouldBlock` |
| 3 | a reply carrying another flow's connection id… | 口径声明，对应 `ForeignConnection`/`Corrupt` |
| 4 | tcp.connectAttempts counts every TCP connect the arm made… | `ConnectSamples`+`ConnectFailures`、`ConnectTicks`、`latency/tcp-connect` |
| 5 | a request offered at a full in-flight window is deferred… | 口径声明，对应 `WindowOverflow` |
| 6 | the deferred queue holds at most {BacklogLimit} request(s) per lane… | `plan.BacklogLimit`、`BacklogSeconds`、`MaxBacklogPerLane` |
| 7 | sendFailures counts individual sends that threw… | 口径声明，对应 `SendFailures` |
| 8 | each lane drains its receive side for up to {DrainSeconds} s…outstandingAtTeardown counts what was still unanswered, plus any deferred request that never got a slot | `DrainSeconds`（现由臂显式传 `DrainLimitTicks`）、`outstandingAtTeardown` 的组成 |
| 9 | tcp and udp counters are summed only after every lane has joined… | 口径声明（求和时机） |
| 10 | in-flight ceiling (tcp): … | `plan.Lanes`、`tcp.SentOk`、`plan.InFlightWindow`、`validity.TcpCeilingMs`、`tcp.WindowOverflow` |
| 11 | in-flight ceiling (udp): … | `udp.SentOk`、`plan.InFlightWindow`、`validity.UdpCeilingMs`、`udp.WindowOverflow` |
| 12 | lanes actually run: tcp … of …, udp … of 1 | `tcp.Started`、`udp.Started`、`plan.Lanes` |
| 13 | a lane never connected or an offer loop ended before its deadline…（条件） | `tcp.Truncated \|\| udp.Truncated` |
| 14 | {started} of {planned} planned lane(s) ran and supplied traffic…（条件） | `validity.LaneShortfall` |

---

## 5. 差异清单

### 5.1 统一项（三份阶梯合一后各处都走同一套）

| 项 | 落地 | 影响 |
|---|---|---|
| 解码/连接 id/filler 三条判据 | `ReplyClassifier.Classify`（纯函数）在 LAT/LOSS/MIX 三处共用 | 三处的顺序与判据自此只有一份 |
| `CorruptKnownSequence` 携带 `FrameDecodeError` | 校验和失败且 header 可读 → 该 verdict；LOSS/MIX 记 `MarkCorruptWithKnownSequence`，LAT 记 `corrupt` | LAT 的 `corrupt` 值不变 |
| `Undecodable` 分支 | 其余解码失败单列一档 | LOSS/MIX 记 `MarkCorrupt()`（与旧 `BookUndecodable` 的 else 分支逐字同义） |
| `WasSent` 检查 | LOSS/MIX：`ReplyClassifier.Classify(...).Resolve(tracker.WasSent(seq))`；LAT：pending 书的成员资格（D18.5 #3 的四步） | 见 §5.3 |
| 5 处 UDP `ConnectAsync` | LAT lane → `UdpLaneTransport.OpenAsync`（引擎调）；LOSS/MIX×2/DNS → `SocketOps.TryConnectAsync`（返回 `LaneOpenResult`，带失败原因文本） | 失败不再裸奔；行为变化见 §5.3 |

### 5.2 保持项（明确不抹平）

| 项 | 保持内容 |
|---|---|
| TCP 的回复匹配 | 仍是 `FrameStreamReader` + FIFO 队列（按到达顺序，不看 sequence），**不使用** `ReplyClassifier`（D18.3） |
| LAT 的 `received` 含重复应答 | 四步顺序的第一步就是它；`lossRate` 仍按 `Sent − (Received − UnmatchedReplies)` 发布 |
| LOSS/MIX 的 tracker 记账路径 | 阶梯只替换分类那一段；`MarkArrival`/`MarkCorrupt*`/`MarkUnmatchedReply`/窗口/发送循环与 `observationEnds` 全留在臂里 |
| `foreignConnection` 语义 | 连接 id 先于 filler，三处一致 |
| LOSS/MIX 的窗口模型 | 仍是 tracker 的槽位；两臂的记账 switch 仍各自成文（只共享分类器，D18.1/design §3.1.1） |
| 臂末尾顺序 | 停止 offer → 有界 grace（每 tick `Settle`，`BookEmpty` 或 1 s 上限）→ 取消并 join 接收 → 最后一次 `Settle` → 算 gates/metrics（D18.5 #4） |
| `send.IsCompleted` 的 ValueTask 复用手法 | 引擎与两个 adapter 各自在唯一一次 `await` 之前取样；引擎取并集，一次发送至多 +1 |

### 5.3 有意行为变更（逐条登记，均只在病态路径上可观测）

| # | 变更 | 旧 | 新 | 可观测差异 | 基线影响 |
|---|---|---|---|---|---|
| 1 | LAT 的 UDP 回复四步顺序：`inFlight--` 只在 pending 命中时发生（D18.5 #3） | 每个有效帧都 `inFlight--`（重复应答/未发出的序号也会释放槽位，可能让窗口过度准入） | 只有消费了 pending 请求的回复释放槽位 | 仅 `inFlight`（内部量，不发布）与窗口准入时机；`received`/`unmatchedReplies` **不变** | 无（selftest 无重复应答） |
| 2 | LAT 的 `WasSent` 一步 | LAT 的阶梯没有单独的 WasSent 检查 | 统一阶梯的 WasSent 步在 LAT 上由 pending 书实现（D18.5 #3 的"不在 pending / WasSent 不成立"是同一判据） | 与 #1 同源；D18.3 的"重复应答改道"在 D18.5 #3 的四步下**不成立**（四步把 pending 未命中一律记 unmatched），见 §8 偏离 1 | 无 |
| 3 | TCP 的 `inFlight--` 同样只在命中时发生 | 每个成帧消息都释放一个槽位 | 只有消费了 pending 的回复释放 | 仅 `inFlight`（内部量） | 无 |
| 4 | 同步抛 `SocketException` 的发送 | 逃到 offer 循环的 catch → **结束循环**，且不计数 | adapter 捕获并回答 `Accepted:false`，引擎记 `SendFailures`（若已异步则连 `SendWouldBlock` 一起）并**继续** | `sendFailures`/`sendWouldBlock` 计数与调度长度 | 无（基线恒 0） |
| 5 | connect（含 TCP 命令帧）失败 | UDP：异常逃出 lane body → 整臂失败；TCP：命令帧异常同样逃出 | 结果为 `OpenResult.Ok=false`：引擎置 `ScheduleTruncated`，臂记 connect 失败并继续（LOSS/MIX 记 client send loss / 各自错误桶；DNS 记 `socketErrors`） | 失败时的记录形状（臂不再失败） | 无（基线 connect 全成功） |
| 6 | UDP 超长数据报 | 内核静默截断 → 解码失败 → `corrupt` | transport 用比目标缓冲宽 64 B 的缓冲读，超长则报 `Malformed + Truncated`；策略记 `corrupt`（同一发布桶，但成因可见） | 仅成因可见性（发布值相同） | 无 |
| 7 | TCP 接收侧终结性映射（D18.6 #3） | `BadMagic/BadLength/BadChecksum` 都记 `protocolErrors` 并 return | `BadMagic/BadLength` → `IoError`（终结）；`BadChecksum` → `Malformed`（`corrupt`，继续） | `corrupt`/`protocolErrors` 在失步流上的分配 | 无（基线无坏帧） |
| 8 | 引擎的 `SendWouldBlock` 与 `SendFailures` 并集（D18.6 #2） | 旧码先 `_sendWouldBlock++` 再 `_sendFailures++` | 同（一次发送至多一次 wouldBlock；失败时两个都 +1） | 无 | 无 |
| 9 | `LaneCounts` 第 8 个计数 `DeferredPending`（D18.6 #1） | 旧码用 `SendLoopAsync` 的返回值 `backlog.Count` | 引擎快照里的队列占用 | 无（同义） | 无 |

---

## 6. 排除的截断/异常映射与理由

| 情形 | 映射 | 理由 |
|---|---|---|
| UDP 数据报 > 引擎目标缓冲（含内核自身截断，.NET 无 `MSG_TRUNC`） | `Malformed + FrameDecodeError.Truncated`，`Length=0`；策略记 `corrupt` | 不能当成合法到达；也不能静默算作"坏帧"——transport 明确命名该情形（缓冲比目标宽 64 B，所以 ≤ 目标+64 的超长可被**精确**测出，更大者只知其 ≥ 缓冲长度）。发布值仍落在旧的 `corrupt` 桶，属 §5.3 #6 |
| TCP 帧 payload > 引擎接收缓冲 | `IoError + Truncated` → `protocolErrors` 并终止该 lane | 接收缓冲由 plan 定尺、必然容得下一整帧，所以这是配置错误；宁可有声终止也不静默丢回复 |
| TCP `BadMagic`/`BadLength`（帧边界丢失） | `IoError` + 对应 `Detail`，终止 | 流已失步，后续读出的任何字节都不再是消息（D18.6 #3） |
| TCP `BadChecksum` | `Malformed + BadChecksum`，继续 | 边界仍可定位，一条坏消息不等于一条坏流（D18.6 #3） |
| UDP 接收 `SocketException` | `IoError` → `udp.protocolErrors`，终止接收循环 | 与旧 `catch (SocketException) { _protocolErrors++; }` 同义 |
| TCP 接收 `SocketException` | 同上（`protocolErrors`） | 同义 |
| `ObjectDisposedException`（teardown） | 不捕获，由引擎的接收循环吸收 | 引擎的循环已把它当作正常终结（旧码亦然） |
| 取消（`OperationCanceledException`） | 不捕获，由引擎/臂吸收 | 同上 |
| UDP `EndOfStream` | 不存在（数据报无半关）；策略的 `default` 分支按 `TransportFailed` 记账 | 接口的四个 kind 是并集，不是每个 transport 都会产生全部 |
| `FrameReadStatus` 的未知取值 | `IoError + Detail=None` → `protocolErrors` | 与旧码 `default: _protocolErrors++; return;` 同义 |
| 发送缓冲过小的 UDP 数据报（> `SendBufferBytes`） | 不检查：plan 的 `payloadBytes` 决定两者，写不进目标缓冲的帧由 `FrameCodec`/`Slice` 直接抛 | 不做防御性静默截断（与旧码相同） |

---

## 7. 性能与规范

### 7.1 真实 transport 的分配 gate（D18.6 #4）

- 正证：`LaneTransportAllocationGateTests.TheRealUdpTransportSendPathAllocatesNoManagedBytes` —— 引擎跑在**调用线程**，
  loopback 上的真实 `UdpLaneTransport` + 一个零分配的策略，6 批 × 128 次发送，逐批 `GC.GetAllocatedBytesForCurrentThread()`
  增量**恰为 0**（readiness 后 ≥4 批稳定），批内线程 id 未变，`SentOk == SendCalls == 768`。
- 反证：同文件 `TheRealTransportGateGoesRedWhenTheAdapterAllocatesOnPurpose` —— 在同一个接缝上故意 `new byte[8]` 并
  `GC.KeepAlive`，每批 > 0，即窗口确实罩住真实 adapter 的 socket 调用。
- 附带的方法学结果（写进 adapter 的注释）：`Socket.SendAsync(Memory<byte>, SocketFlags, CancellationToken)`
  （**带 token 的重载**，两个 adapter 用的就是它）在内联完成时 0 B。check 轮的独立探针更正：**三个 `Memory` 重载**
  （`(Memory,flags,token)` / `(Memory,token)` / `(Memory,flags)`）**都是 0 B**；72 B/次属于 `byte[]` 实参绑定的
  `ArraySegment`/`Task<int>` 重载，与 token 无关（原文"去掉 token 就 72 B"是错的，代码注释已一并改正）。
  这条 gate 保护的是"别把发送改回 `byte[]`/`ArraySegment` 重载"。

### 7.2 `Pacer` pragma 实际数字（D18.5 #10）

```bash
rg -n 'pragma warning disable S6966' benchmarks/WinForward.E2E --type cs | wc -l   # 8
```

`ArmContext.cs`（定义体）1 + `DnsArm` 2 + `LossArm` 1 + `MixArm` 1 + `PersistentArm` 1 + `ReliabilityArm` 1 +
`LaneEngine.cs` 1 = **8**，正好压在上限上（2a-2 的 10 处里，`LatencyArm` 的两处随迁移删除）。
引擎**没有**引入非 async 的 `Pace(...)` 包装，所以引擎那一处仍需 pragma，注释与旧 `LatencyArm:481-484` 逐字相同。

### 7.3 有效行（新文件 ≤ 400）

| 新文件 | 有效行 |
|---|---|
| `Client/Lanes/ReplyClassifier.cs` | 37 |
| `Client/Lanes/TcpLaneTransport.cs` | 89 |
| `Client/Lanes/UdpLaneTransport.cs` | 73 |
| `Client/Arms/LatencyTcpPolicy.cs` | 119 |
| `Client/Arms/UdpLatencyPolicy.cs` | 130 |
| `tests/.../LaneTransportTests.cs` | 184 |
| `tests/.../LaneTransportAllocationGateTests.cs` | 139 |
| `tests/.../ReplyClassifierTests.cs` | 93 |
| `tests/.../LatencyPolicyTests.cs` | 317 |

新文件全部 ≤ 400（脚本口径：去空行、去 `//` 与 `/* */`）。被改的旧文件里 `LatencyArm.cs` 803 → 471 有效行、
`LaneEngine.cs` 196 → 210、`LossArm.cs` 260、`MixArm.cs` 720（仍超标，属 E2-b 的拆分清单）。

---

## 8. 测试清单与结果

| 文件 | 条数 | 覆盖 |
|---|---|---|
| `Lanes/ReplyClassifierTests.cs` | 8 | 合法到达 / 连接 id 先于 filler / filler 不匹配 / 校验和可归因 / BadMagic / BadLength / 过短 / `Resolve` 的 WasSent 步 / 二次调用同判 |
| `Lanes/LatencyPolicyTests.cs` | 15 | 成帧内容、四步顺序（含重复与未发出序号）、窗口准入与重开、`BookEmpty`/`IsDrained` 之别、超长/socket 失败的记账、RTT 用收到时刻、pending≡inFlight 恒等式、TCP FIFO（用 RTT 读数证明消费了哪一条）、`outstandingAtTeardown` 组成、`DeferredPending` 不是相减推导 |
| `Lanes/LaneTransportTests.cs` | 7 | UDP 双向收发 + connect 事实、超长数据报映射、连不上的 UDP 回答结果而非抛、TCP 打开/命令帧/收发、BadChecksum vs BadMagic、BadLength、对端 FIN |
| `Lanes/LaneTransportAllocationGateTests.cs` | 2 | 真实 transport 分配 gate（正证 + 反证） |
| `Lanes/LaneCountsDisjointnessTests.cs` | 3（+1 新增） | `LaneCounts` ∩ 真实策略状态（属性与私有字段两半）+ 负控 |
| `Lanes/LaneEngineSendTests.cs` | 9（+1 新增） | `DeferredPending` 的三种形状 + 异步失败两个计数 |

```bash
dotnet test tests/WinForward.E2E.Tests -c Release   # Failed: 0, Passed: 245（2a-2 的 211 + 本轮 34）
```

---

## 9. 偏离与给 check 的独立验证点

### 9.1 偏离

1. **`WasSent` 的落地方式**：D18.3 说"LAT 补上 `WasSent`"、design.md §3.4 说"重复应答目前落进 `_unmatchedReplies`，补后会改道"，
   但 D18.5 #3（更晚的裁定）把 LAT 的四步钉成"未命中 pending（`/ WasSent 不成立`）→ `_unmatchedReplies++`"。
   在本实现里 LAT 的 WasSent **就是** pending 书的成员资格（D18.5 #11 明确 LAT 不引入 tracker；D14.4 明确 LAT 不按序号索引数组），
   因此重复应答仍计 `received` + `unmatchedReplies`——这正是 D18.5 #12 要求 `received`/`unmatchedReplies` 零宽逐值相同的前提。
   design.md 的"改道"读法会让 `unmatchedReplies`（零宽发布键）改变数值，与 D18.5 #12 冲突，故按 D18.5 #3 实现，并在此登记。
2. **`SocketOps.TryConnectAsync` 的返回类型**：由 `bool` 改为 `LaneOpenResult`（带失败原因文本），6 个调用点同步更新。
   这是"5 处 `ConnectAsync` 落机制统一：… 或共享 `SocketOps.TryConnectAsync`（**返回结果**）"的落地方式。
3. **`LatencyArm` 的每 lane 计数容器**：`LatencyTcpState`/`UdpLatencyState` 现在是**策略的书**（只有接收侧计数 + pending + connect 事实），
   臂另有一个私有 `LatencyTotals` 把"引擎半边 + 策略半边"求和。D18.1 的不相交断言因此落在真实策略状态类型上，而发布值仍来自两次求和。

4. **UDP lane 不记 connect 事实**（收尾时修正）：`metrics/tcp.connectAttempts/connectFailures/meanConnectMs` 的 note 逐字写着
   "the per-lane lane connects plus the 1 Hz connect probe"，所以只有 TCP lane + 探针进这个总体；UDP lane 的 connect 失败
   不进（否则会污染 `tcp.connectFailures`），它按与 TCP lane 相同的方式显形：`metrics/udp.scheduleTruncated = 1` 且
   `gates.laneShortfall = 1`（lane 起了但一个都没发出）。第一轮的 `1c771aba…` 树在这一点上是错的（UDP lane 的 connect
   事实会被折进 `tcp.*`），修正后重新走了一遍全部门禁与判定（§1 的两轮）。

### 9.2 最需要独立验证的 5 点

1. **LAT 的四步顺序与 `received`/`unmatchedReplies` 的逐值一致**（§5.3 #1/#2、§9.1 偏离 1）：请独立复核"重复应答/未发出序号的回复"
   在四步下的落账，以及 `metrics/udp.lossRate` 的公式（`Received − UnmatchedReplies`）在新代码里是否仍等于"消费了 pending 的回复数"。
2. **`outstandingAtTeardown` 的组成**（D18.6 #1）：`Σ Pending + Σ DeferredPending`；`LatencyPolicyTests` 有一条 fact 断言
   `Pending + DeferredPending == Supplied − DeferredDropped`（无回复的场景），另一条 fact 证明 `DeferredPending` 是队列快照而非相减推导。
3. **`SendWouldBlock`/`SendFailures` 的对齐**（D18.6 #2）：`AnAsynchronousRefusalCountsInBothTheWouldBlockAndTheFailureCounter`
   与 `AThrownSocketErrorFailsOneRequestAndTheLoopContinues` 两条 fact 是否覆盖了两种形状；基线里两个计数恒为 0，因此发布值等价主要靠这两条 fact。
4. **真实 transport 的分配 gate 与 token 重载**（§7.1）：请独立复跑该 gate，并复核"去掉 CancellationToken 重载 → 72 B/次"的探针结论
   （它决定了 adapter 的写法不能退化）。
5. **判定运行的宿主噪声**：第一轮的 `after-run3` 出现过 `contract=6`（全在 `BASE.jsonl` 的 loss 相位：`reordered` 0 → 88、
   `reorderRate` 0 → 0.035186，`sent == arrived == 2501`），同一二进制对跑正是这 6 条；判据轮（`final1`/`final2`）没有再出现。
   请独立复核这一归因（靶机 UDP echo 的并发接收循环 `Target/UdpEchoServer.cs`，以及本批对 `LossArm` 的到达记账一行未改），
   以及"带宽可判的路径在同二进制两次运行间零差异"这一说法。
