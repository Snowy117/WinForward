# 开工前裁定与设计修订（权威增量）

本文件是 2026-10-07 设计稿开工前审查（`codebase-design` skill 判据审阅）的直接产物。
**效力**：凡本文件与 `prd.md` / `design.md` / `implement.md` / 子任务 `prd.md` 冲突，**以本文件为准**；
本文件也记录了每条歧义的最终裁定，施工者不再需要自行选择。

阅读顺序（施工子代理）：本文件 → `prd.md` → `design.md` → `implement.md` → 对应子任务 `prd.md` →
子任务目录下的 `implement.md`（逐批次计划）。

---

## D0. 事实重新基线（最重要的一条）

`harness-audit.md` 的 §二清单描述的是**更早的代码状态**。审查逐条核对后确认：
`benchmarks/WinForward.E2E/**` 自首个提交 `59c3a09` 起未被修改，而审计所列的多数"缺陷"**在树里已经实现**。

**规则**：E3 的第一步是 **premise re-verification**（不是修复）：
逐条跑核对命令，产出 `research/semantic-fixes/index.jsonl`，字段
`{id, title, verdict: implemented|fixed|deferred, evidence_test, evidence_cmd, code_refs, commit}`；
`research/semantic-fixes/README.md` 由它生成。AC5 的验收 = 一条脚本读 `index.jsonl` 逐条 `dotnet test --filter`。

### D0.1 §5.1（13 条）现状

| # | 审查核对结论 | 证据 |
|---|---|---|
| 4 | **已实现** | `LossArm.cs:148 tracker.Retire(Clock.Now, windowTicks)`；`UdpReliability.cs:302-320` |
| 5 | **已实现**（只遍历 `_sent.IsSet` 的序号） | `UdpReliability.cs:341-346`、`:326-328` doc |
| 6 | **已实现**（`ArmSpec.LossWindowMs` + `metrics.window` + Note 已改口径） | `ArmSpec.cs:60`、`LossArm.cs:19/29/47/88` |
| 7 | **已实现**（用 `pacer.IntendedTicks`） | `LossArm.cs:141,156` |
| 8 | **待修**：仅剩"`windowOverflow > 0` 的呈现"（plans 已声明 `window:4096`） | 见 D12 |
| 9 | **已实现**（TCP/UDP 都调 `GraceDrainAsync`） | `LatencyArm.cs:435`、`:668` |
| 10 | **已实现**（按 `_highestArrived` 计重排） | `UdpReliability.cs:281-290` |
| 11 | **LOSS 侧已实现**（`unmatchedReplies` 已接通、`abandonedAtTeardown` 可达）；**真缺口 = 分析器不消费 `undecodable`** | `LossArm.cs:79,87,258`；`UdpEchoServer.cs:23/68/101` |
| 12 | **待修**，且是**四个臂**（`IdleArm:20`、`DnsArm:92`、`ThroughputArm:119`、`ReliabilityArm:165`）；`MixArm:161` 已派生 | `rg -n clientSendLoss` |
| 13 | **从未坏过**（`Ratio` 自 `59c3a09` 即 `denominator == 0 ? null`） | `JsonValue.cs:70-71`；`git log -S` |
| 14 | **半实现**：`pid`/`startUtc` 已写；缺"逐样本身份校验 + 非单调拒收" | `ResourceSampler.cs:288-310,424,481` |
| 15 | **已实现**（`PendingQuery(Id, Intended)` 出队按 id 比对） | `DnsArm.cs:46,504-509` |
| 16 | **大半已实现**（参数来自 plan、`control-pre`/`control-post`、BASE floor 都在） | `BaseArm.cs:29-37,53-57,96`；`analyze.py:1900-1914,2030-2032,3357,3531` |

**因此 E3 的真实工作量**（其余条目改为"回归测试 + `index.jsonl` 登记"）：
`#8` 的呈现与 gate 化、`#12`（四臂）、`#11` 的分析器消费、`#14` 的身份校验半、
F7 并发契约、F8 plan 白名单、F11 `ConnectAsync`、ODE 统一、`achievedRate` 统一、
D7 记账半、`Truncated`、账本三字段、`PerSecond`（若 E1 未做）。

### D0.2 §5.2（公平性 3 条）现状

| # | 结论 | 证据 |
|---|---|---|
| 17 | **已实现**（`NOT_CARRIED_CELL` 逐字一致 + gate 豁免 + 格子置空 + 图排除） | `analyze.py:241,224,3201,3766-3790,5669` |
| 18 | **已实现**（`UDP53_LABEL` 5 处标注 + DNSALT 不可比提示） | `analyze.py:226-232,1629,2627,4200,4300,4312` |
| 19 | **真缺口**（`rg -n 'user-mode|kernel|DPC|ISR|nonpaged' analyze.py` 零命中） | — |

交付方式改为：#17/#18 各加一条**可 grep 的回归断言**（例如 `scripts/check-fairness.py` 断言
`not carried (UDP bypassed)` 出现在 UDP 表且该行无数字格）；`#19` 是本次的实质工作。

### D0.3 其它被证伪的断言（不得再引用）

- 「发送三件套 113 行、归一化后只差 5 行」→ 实测 TCP `442–550` = 109 行、UDP `673–775` = 103 行；
  归一化后 18 行差异、非注释 7 行，另有 TCP 独有注释 4 条（`LatencyArm.cs:482/498/514/541`）与 UDP 独有 1 条（`:765`）。
  **合并时注释取并集**（这些注释是 README「Non-obvious properties」的代码侧依据）。
- 「全仓 pragma 9 处 → 收敛为 1 处」→ E2E 内 pacing 家族 9 处（`ArmContext.cs:73` 定义体 + 8 个同步调用点），
  引擎范围 4 处，**合并后目标 6 处**；"全仓"实际 78 处。
- 「`make_tree.py` 已覆盖全部边界」→ `outOfRange`/`absent` 零命中（D7 与 #13/#14 相关字段），E4 需补造。
- 「账本没有任何代码读它、没有绝对时间戳」→ 假：`analyze.py:2119 ledger_views` 在读并按 `label` 关联，
  `LedgerWriter` 每条写 `utc`+`label`，`verdict.json` 有 `ledger` 键。E5 的披露改写见 D18。
- D7 触发边界：首个越界的 offered 数是 262145（`sequence 262144 > MaxSequence 262143`），
  即校验判据是 `> MaxSequence`（实施时用边界单测钉死，见 D2）。
- `client-infrastructure-code-quality-audit.md` §11 实际 16 行（编号重复）；其第 14/15 条明确禁止改
  `UdpReliabilityTracker` 的记账与 `LossArm` 的序号约定 → E3 不得顺手改。

---

## D1. 效力与冲突优先级

1. 本文件 > 父 `prd.md`/`design.md`/`implement.md` > 子任务 `prd.md` > 子任务计划。
2. 子任务 `prd.md` 末尾的"以父为准"继续有效；父文档中被本文件点名的段落**视为已修订**。
3. 任何施工者发现"实现本文件会导致与代码事实冲突"，**停下来写进 `research/` 并在 check 报告里提出**，
   不得静默改成别的做法。

---

## D2. D7 记账半的口径（AC16）

**裁定**：越界序号（`sequence > MaxSequence` 或 `< 0`）记为**客户端丢失**。

- `UdpReliabilityTracker` 新增发送侧计数（建议名 `SentOutOfRange`），与接收侧触发的
  `OutOfRange` **分开命名、分开发布**（后者的公开名 `outOfRangeSequences` 不变，它是已声明的披露信号）。
- 越界槽：`Supplied` 照涨；`SentOk` **不涨**；不写 `_sendTicks`/`_arrivalMilliseconds`；不进任何到达桶。
- `LossArm` 的 `clientSendLoss = SendFailures + WindowOverflow + Undetermined + SentOutOfRange`
  （与 `LatencyArm` 的 `_supplied - _sentOk` 口径对齐；`windowOverflow` 早已计入，语义同类）。
- 同步改 `LossArm.cs:55` 的 Note 与 `README.md:363/550` 的说明（E5 负责 README；实现者负责 Note）。
- **E1 的守卫必须仍然调用 `_sent.TrySet(sequence)`**（由它维护 `OutOfRange`）；E1 只做守卫 + 单测，
  记账公式在 E3 落地。

---

## D3. 传输接缝重划（取代 design.md §3.2/§3.4 的接口草案）

**裁定**：`ILaneChannel` **取消**。接缝按"能力"拆成三个，且变化侧才是接缝所在：

```csharp
// 1) transport adapter：两个真实实现（frame TCP / datagram UDP）
internal interface ILaneTransport : IDisposable
{
    ValueTask<LaneOpenResult> OpenAsync(CancellationToken ct);
    ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    // TCP: 一个完整帧（FrameStreamReader 内部缓冲）；UDP: 一个数据报（可能被截断）
    ValueTask<LaneReceiveResult> ReceiveAsync(CancellationToken ct);
}

// 2) 每臂/每 lane 的策略：构建请求字节 + 解释回复（引擎不解释线格式）
internal interface ILanePolicy
{
    int BuildRequest(long sequence, long intendedTicks, Span<byte> destination);
    void OnReply(in LaneReply reply, LaneBook book);   // 记账由策略做
}

// 3) 引擎：机制（配速、在途窗口、发送循环、接收调度、有界 drain、scheduleTruncated）
internal static class LaneEngine<TTransport> where TTransport : class, ILaneTransport
{
    private sealed class LaneState { /* 引擎真正读写的字段 */ }
    internal static ValueTask<LaneCounts> RunAsync(...);   // 返回不可变快照，不产出共享可变 bag
    internal readonly record struct LaneCounts(long Supplied, long SentOk, long SendWouldBlock,
        long SendFailures, long WindowOverflow, long BacklogDrops, long InFlight, bool ScheduleTruncated);
}
```

- **in-flight 窗口语义是策略点**：`Drop` / `Defer` / `Block` 三种；引擎只执行策略给的行为。
- `LaneState` 私有嵌套 + 对外只给 `LaneCounts`：这样"字段集合 == 引擎读写集合"是**编译期事实**，
  不再需要"人工核对"。验收改为两条可机械判定：
  (a) `LaneCounts` 每个字段都有写入点（fake transport 单测覆盖）；
  (b) `LaneCounts` 的属性名集合与策略侧计数器属性名集合**不相交**（反射断言）。
- `ReplyClassifier` 改成**纯函数**（不吃 `UdpReliabilityTracker`）：
  ```csharp
  internal static ReplyVerdict Classify(ReadOnlySpan<byte> datagram, uint expectedConnectionId);
  internal readonly record struct ReplyVerdict(ReplyKind Kind, long Sequence, int PayloadBytes, FrameDecodeError Error);
  internal enum ReplyKind { Arrived, Corrupt, CorruptKnownSequence, ForeignConnection, Unmatched, Undecodable }
  ```
  调用者拿 verdict 后自己记账（LOSS/MIX 调 tracker；LAT 调 `pending.TryRemove` + RTT + `_inFlight--`）。
  `Undecodable` **必须携带 `FrameDecodeError`**（保住 `MixArm.cs:698-707` 的行为）。
  统一后的差异清单（哪些统一、哪些保持）必须写进子任务计划，不得只写"三份合一"。
- **`DnsArm` 不并入引擎**（它的 TCP 相是长度前缀流，是第三个线格式）。DnsArm 只共享
  `Pacer`、`SocketOps` 与有界 drain helper。`design.md:680` 的"若要并入"字样作废。
- 性能契约 = **零额外分配**（Release 分配 gate 单测：fake transport 跑 N 次发送，断言
  `GC.GetAllocatedBytesForCurrentThread()` 增量 0）+ `ValueTask` 复用手法在引擎覆盖的两个 lane 逐字保持。
  **去虚化只做观测项**（记录 `achievedRate`/`sendWouldBlock`/CPU 前后量级），不作为验收断言。
- 合并镜像函数时**注释取并集**（见 D0.3）。

---

## D4. `UdpReliabilityTracker` 并发契约（决定 E2 接收侧形状，提前裁定）

**裁定**：**发送线程独占 book**。接收线程只把 `(kind, sequence, nowTicks, payloadBytes)` 投递到无锁队列
（`ConcurrentQueue` 或 SPSC ring），由发送线程在**每个 pacer 槽位之前** drain 队列并结算。

**drain 顺序（必须写进实现）**：所有 lane 停止 → 排空投递队列 → 才允许 `Classify` / 收尾统计。
接收侧计数（`ForeignConnection`/`UnmatchedReplies`/`ReceivedDatagrams`/`Corrupt`）由**消费方在 drain 时结算**。
类文档写明该契约，并由并发测试证明（发送与接收并发 N 轮，恒等式不破）。
这条在 E2 抽引擎时**就**按此形状设计接收侧，避免计划里写明的返工。

---

## D5. 契约单一定义点：不引入 source generator

**裁定**：保留 `ArmKeys` 常量 + 强类型 record + 手写 writer，但把"可判定性"补足：

1. 每个 metrics record 的属性**全部** `required`（或经 init 赋值），测试侧为每个 kind 写**显式工厂**
   （`new LossMetrics { Sent = 1, Arrived = 1, LossRate = 0.5, ... }`）——新增属性会让工厂**编译失败**，
   这是编译期覆盖，取代 review 否掉的 `FullyPopulated(metricsType)` 反射。
2. 形状测试比对 **JSON 路径集合**（生产 writer 的输出，经 flatten）与 **`ArmKeys` 子树反射出的声明路径集合**，
   失败时打印两个方向的差集。路径包含层级，能查"写错层级"。
3. 补三条此前缺失的规则：
   - **数组键**：声明为一个路径（值含点号者为形态 A），元素不展开成 `[i]`；元素个数由单独断言覆盖。
   - **条件写出**：只有"该臂根本不发布该量"的字段才整键省略（如纯 UDP 时的 `tcp.*`）；工厂带 flags 参数，
     测试跑两种 flags 各断言一次。
   - **null 三态**：非条件字段即使未知也**写出键、值为 JSON `null`**；因此「缺键」只来自条件省略。
     每个 nullable 字段各有一条 null 用例（表驱动）。
4. 嵌套层级的 JSON key 由**常量路径**承载（writer 引用常量），不得依赖"类名小驼峰"魔法。
5. `parameters` 与 `metrics` 走强类型值对象；`gates` 保留 `Dictionary<string,long>`，但键由 `ArmKeys.Gates` 常量约束。

---

## D6. E4 的 oracle 机制（取代 design.md §6 层三的 `--tables` 方案）

1. **两侧都输出完整文件**；切片交给一个**已跟踪**的驱动脚本 `scripts/oracle-diff.py`：
   解压冻结树到**固定路径** `/tmp/wf-synth/raw`；按 `^## (\d+)\.` 切 `tables.md` 小节、按 top-level key 切
   `verdict.json`；`BATCH_SECTIONS` 映射写在脚本里；切片缺失 = 报错（不允许"空切片 = 通过"）。
2. 未实现小节在正文里写 `<!-- TODO(batch N) -->`；**不允许**把"整文件 diff 为空"当作批 1–4 的判据。
3. **冻结物**（全部入库）：
   - `benchmarks/WinForward.E2E.Analysis/verification/synthetic-tree.tar.gz`（冻结树）
   - `.../verification/golden/py-tables.md`、`py-verdict.json`（冻结的 A）
   - `.../verification/synthetic/make_tree.py`（**保留**，它不在 V3 的删除授权内；移入此处并写明
     "改动即需重新冻结 A"）
4. **`generated_by` 取 (b)**：两侧一起改成中性值（如 `"e2e-analysis v1 (C#)"`），
   在**同一个提交**里重新冻结 A，并把它登记为 Python 侧**唯一**的逻辑改动
   （"只改字段名、逻辑一行不动"的唯一例外）。
5. **Python 参考实现的改动清单必须落盘**：`research/python-oracle-changes.md`，逐条列 `dig` 路径与 key 常量。
6. oracle 通过后按 V3 删除 `analyze.py`（依赖 git 历史）；`make_tree.py` 与冻结物保留。
7. `scripts/analyze.sh`（已跟踪）作为薄封装；`publish-campaign.sh` **不改**（gitignored，不算交付物）。
8. `publish-campaign.sh` 的现状说明：它用 `nix-shell -p python3.withPackages(matplotlib)` 包住调用；
   换 C# 后该包装失去意义，但**不在本次改动范围**（不在 git 里）。

---

## D7. 层零基线（E1 步骤 0.0）修订

1. **跑两次基线**（`/tmp/base-1`、`/tmp/base-2`），把两次之间的差异固化为**抖动带**。
2. `scripts/compare-records.py <base> <after> --normalize research/record-normalize.json`；
   归一化集合至少含：`startedTicks/endedTicks/ticks/*Utc/utc/startUtc/endedUtc/wallSeconds/elapsedSeconds/
   generatorCpuSeconds/envWorkingSetBytes/*Us/*Ticks` + **键序归一化** + 全部路径类键。
3. **判据不是"差异为空"**，而是"差异超出抖动带 **且** 命中改名表或语义清单"。
4. 改名表落**机器可读**的 `research/contract-rename.json`
   （`{kind: "renamed"|"added"|"removed", old_path, new_path, reason}[]` —— 新增键（如 `run.json.planSource`、
   `error` 记录的新字段）用 `added` 表达、删除键用 `removed`）+ 由它生成的 `contract-rename.md`
   （只列 changed/added/removed + 分组统计）；B3 的判据由脚本执行：
   `symmetric_difference(old_paths, new_paths) == rename_pairs`（新增/删除各自计入对应方向）。
   路径集合的**范围**：`<arm>.jsonl` 记录（含 `run.json`）与 `ledger.jsonl`；`target.out` 只做文本归一化比对、不进键集。
   **路径字母表（D14.6 补充）**：`canonical_path = join('/', 段)`，段 = 对象成员名的**字面值**，键内点号不拆。
   例：`metrics/tcp.sentOk`、`metrics/classes/udp/sent`、`metrics/latency/udp.sentOk`、`metrics/parameters/seconds`。
   比对报告必须分三栏：① 结构性差集（缺键/多键/类型改变/数组长度改变）② 条件键差异（`UseTcp`/`UseUdp`/
   非空直方图导致，单列且允许 run-to-run 不重复，需人工确认）③ 数值差异（落在逐键抖动带内或有出处）。
5. 基线产物落任务内可 review 的位置（`research/baseline/`），不只用 `/tmp`。
   脚本显式检查 `python3` 存在并在缺失时报错；**不依赖 `nix-shell`**。
6. E2 批次 2b 的判据改为："结构性相等（键集合 + 类型 + 非计数字段相等）+ 计数字段落在抖动带内"。

---

## D8. 顺序与批次修订

- **并发契约（D4）与接缝（D3）的决定提前到 E2 开工前**，E2 的接收侧一次按定稿形状写；
  `implement.md` 中"接缝在 E3 还会再改一次接口"那段（E3 步骤 1 的引言）作废。
- E2 内部顺序：**2a 接缝（engine+两个 transport+ReplyClassifier+5 处 ConnectAsync+零散修复）
  → 2b 纯搬移拆分 → 2c 解析器合一**；`design.md` §7 的 P2→P4 措辞以 E2 计划为准。
- E1 拆**三个** impl→check 批次：**E1-A**（基线 + 测试工程 + Tier 0 止血）、
  **E1-B1**（Contracts 骨架 + `JsonlSink` + 盘点/改名表 + IDLE/THRU 走生产 typed 路径）、
  **E1-B2**（其余 6 臂 + `ControlArm` + `ClientRunner` + 形状/字面量 gate）。判据见 E1 计划。
- E3 提交按 **C1–C8 分组**（`implement.md` 的「提交图」节，该表在 `:229-236`），
  `e3 prd.md` 里"每条修复一个提交"的那段（`:97` 起）作废。
- E4 与 E3 的衔接：oracle **准备**（升级 `make_tree.py` + 最小改动 Python 版 + 冻结树）可在 E3 期间并行。

---

## D9. 字段与计数命名裁定（施工直接采用）

| 项 | 裁定 |
|---|---|
| `BaseArm` | 纯改名 `ControlArm`；plan/记录里的 `kind:"base"` **不动** |
| `achievedRate` | 统一为"成功**发出**的请求/秒"；PERSIST 的完成口径新增 `completionRate`；不留旧义别名 |
| `PerSecond` | 同批改 `double?`（`ticks <= 0 ⇒ null`），与 `Ratio` 一起进 `Contracts`；`achievedRate` 零 elapsed ⇒ `null` |
| `ObjectDisposedException` | 统一为"teardown 不产生数据点"：良性 teardown 一律不计数、不进样本分类。实施时**逐条列出受影响的既有字段**（哪些计数会消失/改名），写进 check 报告 |
| `FrameStreamReader` | 新增 `FrameReadStatus.Truncated`；**不新增 verdict 成员**（截断仍走 half-close 语义）；`TcpTargetServer` → `tcpSummary.truncatedFrames`、`DnsServer` → `dnsSummary.truncatedFrames`；分析器新增一条 `measurement-caveat` finding；`make_tree.py` 增加截断帧用例；`tests/` 增加"一帧拆三段 + EOF 落中间 ⇒ Truncated" |
| 账本三字段 | `detail`（Error 的异常类型名）、`acceptErrors`、`udpReceivers`；只增不改名。`acceptErrors`/`receiverExits` 的**计数点**落在 E2 抽出的 accept/receive loop，E3 只负责写账本 + 分析器披露 |
| `udpReceivers` | 配 `--udp-receivers` CLI 开关（默认保持 `Clamp(ProcessorCount/2, 2, 8)`），写进 `targetSummary` |
| `error` 记录字段集 | `{type, arm, kind, label, error(异常类型名), message, detail, startedTicks, endedTicks}`，写进 README |
| 退出码 | `0` = 全绿；`1` = 臂级失败但有记录；`2` = usage/plan 校验失败。`130+`（128+N）只可能来自**未处理信号**，由 shell 观察，harness 不主动返回（D14.12） |
| `--plan` | 缺省（完全不给）→ 内置默认 plan（README 写明）；`--plan=` 与 `--plan ""`（显式空值）→ 退出码 2（D14.1：判定在 `ClientRunner.TryApply` 的 `--plan` 分支，`PlanFile.cs:125` 改 `path is null`） |
| `selftest.sh` | 漏 plan → `exit 2`；删掉 `ls /tmp/wf-bench/deploy/e2e/*.json`（新机器上不存在，会把 usage 错误变成 ls 错误） |
| `--label --out x`（值像选项） | 纳入 E1：`--label` 的值不得以 `-` 开头 → 退出码 2 且报错指向 `--label` |
| JSON 键序 | 稳定：顶层按 `design.md:18` 的清单；metrics 按 `ArmKeys` 声明序 |
| `ArmKind` 描述符表 | 一张表 `{Name, Keys[], Validate}`，同时吃掉 `PlanFile.cs:50` 与 `ArmDispatch.cs:5-17` 的双份清单；白名单从 `Keys` 派生。**必须含文本键 `modeMix`/`protocol` 等**，否则会误拒 3 份 plan（含 AC12 的 `selftest-plan.json`） |
| `#6` 的 W 验收 | 改为机械判据：遍历 11 份 plan，对 `kind ∈ {loss,mix}` 的 arm 要求存在 `lossWindowMs`（否则失败并打印 plan 路径 + arm 名）；`metrics.window` 等于声明值。`dns-plan` 无 loss/mix 臂、`base-plan` 由 `ControlArm` 透传（`BaseArm.cs:96`）而**不声明**，两者属正常（**D14.2 更正**：原写 `{loss,mix,base}` 会必红） |
| `windowOverflow > 0` | **不改 gate 为"作废该臂"**：输出 `measurement-caveat` + 该臂延迟格子 `n/a (windowOverflow > 0)`；同步改 `README.md:550`（原文说它是 disclosure 不是 failure） |
| REL `ConnectFail`（`ReliabilityArm.cs:602-606`） | 本次**不动**，登记 `research/` 供后续裁决 |
| `#20/#21/#22` | 不进 AC17 的 7 条清单；只在分析器 README 披露 + 登记后续任务 |
| 账本归属 | **不补** arm/row/product 归属；`undecodable` 只做"target 侧总量"级披露；逐包归属登记为后续任务 |

---

## D10. `JsonlSink` 契约（含此前漏登的三条 bug）

`JsonlSink` 合一 `JsonlFile` 与 `LedgerWriter`，但**不合并异常语义**：

- 构造 `(path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope)`；
  `JsonlPolicy ∈ {Propagate, SwallowAndCount}`（`Propagate` = client 侧，`SwallowAndCount` = ledger 侧的限流语义，保留）。
- `body(writer)` 移出 catch（编程错误必须上抛）；I/O 部分才 catch（"磁盘故障不弄死 target"保留）；
  body 抛出时该连接记一笔失败并关闭、服务继续；`_gate.Release()` 与 `await using` 生命周期一起重排。
- **新增三条修**（来自 `client-infrastructure-code-quality-audit.md` §1.4/§1.5/§1.7）：
  1. 写入**不可取消**（只在取锁处响应取消）——避免 JSONL 中间留半行（分析器会记 `bad_lines`）；
  2. dispose 语义写清（`_gate` 的所有权），不得依赖 `ClientRunner.cs:289` 的跨文件隐式不变量；
  3. client 侧 **1 Hz 定期 flush**（对齐 ledger 的 `FlushLoopAsync`），崩溃不再丢掉整臂样本。
- 三条各配一条单测（半行检测、dispose 复活检测、flush 定时检测——用可控时钟或短间隔注入）。

---

## D11. 其它待钉死的默认裁定

| 项 | 裁定 |
|---|---|
| `Ratio`/`PerSecond` 搬进 `Contracts` 后 | 删除 `JsonValue` 里的旧实现（不留别名），同提交改全部调用点；加一条"无裸 `(double)a / b`"的规则检查 |
| `Clock`/`Pacer`/`SocketOps`/`Dedicated`/`ArmContext` 的最终位置 | `Client/Time/Clock.cs`、`Client/Pacing/Pacer.cs`、`Client/Net/SocketOps.cs`；引擎**依赖**它们而非拥有；`ArmContext.cs` 的 6 个类型也纳入拆分表 |
| `FrameCodec.MaxPayloadLength` | 提为 `internal`，`PlanFile` 用它校验 plan |
| 端口冲突 | 显式拒绝 `dnsPort ∈ {tcpPort, udpPort}`（`dnsAltPort` 已有校验） |
| Windows 验证 plan | 取 (a)：新增 `scripts/plans-windows/full-shape-plan.json`，保留 `full-plan` 的负载形状（LOSS `120s×500/s`、LATLOAD 500 rps、PERSIST idle、DNS 200 rps），只压缩其余臂时长 |
| AC18 | 三态（pass/fail/blocked）；blocked 必须留下失败命令与原始输出到 `E5-WINDOWS-BLOCKED.md`，E5 完成声明显式写 `AC18=blocked` |
| `reordered`/`#4` 等回归测试 | 每条定向测试必须**能对当前代码失败**（反证目标写在注释里），否则算同义反复 |
| 文件归属 | 见 §D12 的表 |
| 性能判据 | Release 分配 gate 单测（见 D3）；不做 BDN 微基准（`achievedRate`/`sendWouldBlock` 前后量级对比足够） |

---

## D12. 文件归属表（子任务边界，防两子任务同改一文件）

| 文件 / 目录 | 归属 | 备注 |
|---|---|---|
| `tests/WinForward.E2E.Tests/**` | E1 建，E2/E3 续加用例 | 每批次各自新增文件为主 |
| `benchmarks/WinForward.E2E.Contracts/**` | E1 | E3 只增字段（`ArmKeys`/record），E4 读 |
| `Client/JsonValue.cs`、`JsonlFile.cs`、`ArmContext.cs` | E1 | `JsonlSink` 合一后旧文件删除 |
| `Client/PlanFile.cs`、`ArmSpec.cs`、`ArmDispatch.cs`、`Cli/ExitCodes.cs` | E1（Tier 0 + 白名单）；E2 抽 `Cli/CommandLine.cs` 时再动 | 白名单表 E1 定形，E2 只搬 |
| `Client/Lanes/**`（新建） | E2 | E3 只改接收侧形状（若 D4 已定稿则不改） |
| `Client/Arms/LatencyArm.cs`、`DnsArm.cs`、`LossArm.cs`、`MixArm.cs`、`ReliabilityArm.cs`、`PersistentArm.cs`、`IdleArm.cs`、`ThroughputArm.cs`、`BaseArm.cs`→`ControlArm.cs` | E1 类型化；E2 搬移/接缝；E3 语义 | 按批次计划切分具体方法 |
| `Client/ClientRunner.cs`、`Client/ResourceSampler.cs`、`Client/UdpReliability.cs`、`Client/LogHistogram.cs` | E1/E2 拆分；E3 语义 | — |
| `Target/**` | E2 拆分与零散修复；E3 账本三字段。**E1 例外**：`LedgerWriter` → `JsonlSink` 合一 + 4 处调用点（`TcpTargetServer`/`TargetRunner`/`DnsServer`/`UdpEchoServer`）的异常处理（D14.7） | — |
| `Wire/**` | E2 零散修复；E3 `Truncated`。**E1 例外**：只加 `FrameStreamReader` 的 internal 读委托 ctor（D14.18） | — |
| `scripts/selftest.sh`、`scripts/compare-records.py`、`scripts/jsonl_paths.py`、`scripts/contract-inventory.py` | E1 | 脚本一律放 `benchmarks/WinForward.E2E/scripts/`（**仓库根没有 `scripts/`**，D14.9） |
| `scripts/oracle-diff.py`、`scripts/analyze.sh` | E4 | |
| `scripts/plans-windows/full-shape-plan.json` | E5 | |
| `benchmarks/WinForward.E2E.Analysis/**` | E4 | |
| `benchmarks/WinForward.E2E/README.md` | **E1 改 4 小节**（`:122-142` 选项与退出码、`:168-175` plan schema、`:238` `error` 记录、`:531-535` 序列上界）；**E5 全面改写**（契约表/Non-obvious/Verification/Layout/线格式交叉引用） | D14.10 |
| 分析器 README | E5 | |
| `research/**` 证据产物（基线、盘点、改名表、semantic-fixes） | E1/E3 各自落盘并**随任务提交**（父任务目录当前未被 git 跟踪，见 D14.9） | |
| `Sample` 记录（ResourceSampler 的 23 个字面量键与三态） | **E2**（随 ResourceSampler 拆分），E1 不动 sampler | D14.13 |

---

## D13. 仍开放但**不阻塞**的问题（登记，不在本次修）

1. `ReliabilityArm.cs:602-606` 取消写成 `ConnectFail`（两份审查冲突，无可达实验）。
2. 账本逐包归属（需要 target 侧包级身份，属新功能）。
3. `#20/#21/#22`（THRU 上限、防火墙未记录、单进程名）——只披露 + 登记。
4. `WinForward.Benchmarks/` 3 个既有行数违规（登记为已知债务）。
5. `make_tree.py` 对 `outOfRange`/`absent`/截断帧的构造缺失（E4 补齐其中与 oracle 相关的部分）。

---

## D14. E1 计划审查（2026-10-07）裁定补充

本节优先于 E1 的子任务 `prd.md` / `implement.md` 中与之冲突的措辞。

### D14.1 `--plan=`（三处语义分开）
- `ClientRunner.TryApply` 的 `--plan` 分支：值为空串 → usage error（退出码 2，错误信息指名 `--plan`）。
  这同时覆盖 `--plan=` 与 `--plan ""`。
- `PlanFile.cs:125` 的 `IsNullOrEmpty` 改 `path is null`（注释说明"空串已在 CLI 层拒绝，null 才是缺省"）。
- `ClientRunner.cs:445` 保持 `is null`。
- 判据：`--plan=` → 2；完全省略 → 正常跑内置默认 plan 且 `run.json` 的 `planPath` 为 `null`。

### D14.2 `#6` 的 W 判据
`kind ∈ {loss,mix}` 必须声明 `lossWindowMs`；`base` 由 `ControlArm` 透传（`BaseArm.cs:96`）、`dns` 无相关臂，均属正常。

### D14.3 `ArmKind` 描述符表形状
`{ string Name; string[] Keys; string? Validate(ArmSpec); Func<ArmContext, Task<ArmOutcome>> Run; }`，
一条 `static readonly ArmKind[]`。`PlanFile` 用它判 kind/白名单/校验；`ArmDispatch.RunAsync` 改为查表调 `Run`。
表住 `Client/Arms/ArmKind.cs`（E1-A 建，E2 只搬）。`Keys[]` 必须含 `name`/`kind`/`seconds` 与该 kind 真实读取的
全部键（**含文本键 `protocol`/`modeMix`**；建表用 `rg -o '(spec|Spec)\.[A-Z][A-Za-z]*'` —— 只查 `spec.` 会漏掉
`MixArm`/`IdleArm` 的 `context.Spec.*`，`ReliabilityArm` 的常量 `DefaultModeMix` **不得**进表）；
未知 key 与非法值同一错误通道，消息含 `arm '<name>' (kind '<kind>'): …`。
**逐 kind 键清单（已实测：这 9 组接受全部 11 份 shipped plan + 内置默认 plan，0 拒绝）**：

| kind | 除 name/kind/seconds 之外的键 |
|---|---|
| `idle` | — |
| `latency` | ratePerSecond, payloadBytes, protocol, window, lanes |
| `loss` | ratePerSecond, payloadBytes, window, lossWindowMs |
| `mix` | desktops, lossWindowMs |
| `dns` | ratePerSecond, tcpPercent, cnameEvery, dnsPort |
| `reliability` | connectionsPerSecond, modeMix, expectedBytes |
| `throughput` | streams, targetBytesPerSecond |
| `persistent` | expectedBytes, idleSeconds, intervalMs, payloadBytes |
| `base` | ratePerSecond, payloadBytes, protocol, window, lanes, lossWindowMs |

### D14.4 `MaxSequence` 加载期校验的作用域与公式
只对 `kind ∈ {loss, mix, base}` 检查（只有它们持有 `UdpReliabilityTracker`）：
`effectiveRate = mix → 30（MixArm.cs:111）；base → spec.RatePerSecond > 0 ? 它 : 500（BaseArm.cs:87-97）；loss → spec.RatePerSecond`；
拒绝条件 `(long)Math.Ceiling(effectiveRate * spec.Seconds) > UdpReliabilityTracker.MaxSequence`（`Seconds` 是 double）。
错误信息含 arm 名、算出的最高序号、`MaxSequence`。边界用例：`262143/1s` 通过、`262144/1s` 拒绝，loss 与 base 各一条。
**latency/dns/reliability/throughput/persistent 不做此校验**（它们没有按序号索引的数组）——在代码注释里写明理由，
防止后来者"顺手统一"。

### D14.5 D7 的测试配方
- `_sendTicks`/`_arrivalMilliseconds` 无访问器；`Ensure`（`UdpReliability.cs:401-406`）对越界早退 ⇒ 数组长度上界 262144
  ⇒ **"不抛" 就是 "两数组未被写" 的证明**。断言写成：`MarkSent(MaxSequence + 1, 0)` 不抛、
  `OutOfRange == 1`、`SentOk == 0`、`Outstanding == 0`、`Supplied == 0`；不要反射私有字段。
- 0.1（单测直接构造 tracker）与 0.2（plan 加载校验）是**两个 seam**，可以同时成立；禁止端到端跑越界 plan 做 0.1 判据。
- 补一条**接收侧**的 `OutOfRange` 活路径回归护栏：坏帧携带 `sequence = 2^63` 时
  `MarkCorruptWithKnownSequence` 不崩、`OutOfRange == 1`、`Duplicate == 1`（今天也通过，按 D11 标注为回归护栏）。

### D14.6 路径字母表与比对分栏
见 D7 第 4 条（已就地写入）。`jsonl_paths.py`（flatten）由盘点与比对**共用**，避免两套字母表。

### D14.7 `JsonlSink` 的异常/生命周期契约（取代 D10 的模糊处）
- `JsonlPolicy.Propagate`（client）：I/O 与 body 异常上抛给调用者；**close/dispose 不抛**（计入 `WriteErrors` + stderr 限流）。
- `JsonlPolicy.SwallowAndCount`（ledger/target）：I/O、body、close 一律不抛、一律计入 `WriteErrors`
  （保持 `LedgerWriter.cs:96-97` 现状与"磁盘故障不弄死 target"）。
- `body(writer)` 移出 I/O try；但策略的吞/抛语义按上面两行执行（"编程错误必须上抛"只对 `Propagate` 成立）。
- **调用点处理清单（B1 必须逐点落）**：
  1. `TcpTargetServer` 的每连接账本写（fire-and-forget，`:97`/`:187`/`:196`）：把 `WriteConnectionAsync` 的账本调用
     包进 try/catch → 记一笔失败 + 关闭该连接 + 服务继续；改掉 `TcpTargetServer.cs:50-52` 那条"writer 会吞掉"
     的注释，使其与新契约一致；
  2. 4 处 summary 写（`TargetRunner.cs:73`、`TcpTargetServer.cs:131`、`DnsServer.cs:89`、`UdpEchoServer.cs:96`）：
     不得让异常逃出 `TargetRunner`（同样 catch + 计数）；
  3. client：sink 的释放纳入**臂的失败边界**（显式 `CompleteAsync()` 于臂 try 内，`await using` 仅作兜底的非抛释放），
     保证"臂级失败必留 `error` 记录 + `run.json.failed:true`"对 dispose 故障同样成立；
  4. `targetSummary.ledgerWriteErrors` 键名与语义不变（账本是跨二进制契约，只增不改）。
- 配测试：internal `JsonlSink(Stream, …)` 重载 + 抛异常的 body/流 → 断言 `WriteErrors` 增长、`Propagate` 的 close 不抛。
- 1 Hz flush 通过 internal ctor 参数 `TimeSpan flushInterval = 1s` 注入（**不引新测试包**，xunit 2.9 无 FakeTimeProvider）。

### D14.8 基线取得方式（A0）
先 `benchmarks/WinForward.E2E/scripts/publish.sh`（或删除 `${WF_PUB:-${TMPDIR:-/tmp}}/wf-bench/pub/linux`，
与 `selftest.sh:7` 的默认路径一致）——`selftest.sh:15-18` **不会重建已存在的二进制**。A0 步骤：
1. 记录 `git rev-parse HEAD`、`git status --porcelain`、发布二进制的 `sha256sum` 到 `research/baseline/`；
2. 声明基线期间不得有并行构建/编辑；
3. run1 结束后**立即**拷贝 `out/`、`ledger.jsonl`、`target.out`、完整脚本输出（`tee`）到 `research/baseline/run1/`，
   再跑 run2；
4. 比对覆盖三类：JSONL 记录、`ledger.jsonl`、`target.out`（外加 run1↔run2 抖动带报告）。
A2 的 0.7 顺手让 `selftest.sh` 把 client 的完整输出落到 `$work/client.out`（现在只有 `tail -20`）。

### D14.9 脚本与产物路径
- 仓库根**没有** `scripts/`：`compare-records.py`、`jsonl_paths.py`、`contract-inventory.py` 放
  `benchmarks/WinForward.E2E/scripts/`（已跟踪）。
- `research/` 指 `.trellis/tasks/10-07-e2e-harness-refactor/research/`；其中产物**随任务提交**。
- `benchmarks/WinForward.E2E/AGENTS.local.md` 是 gitignored 的本地文档，E1 不改。

### D14.10 README 归属（修订 D12）
E1 在自己改动行为的**同一提交**里更新 `benchmarks/WinForward.E2E/README.md` 的这几小节：
`:122-142`（client 选项 + 退出码）、`:168-175`（plan schema：未知 key 现在是硬错误）、`:176-197`（各键取值域与
"0 = 未声明"）、`:238-240`（`error` 记录字段集 + `run.json` 的 `planSource`）、`:531-535`（序列空间上界与越界槽记账）。
E5 仍负责契约表 / Non-obvious / Verification / Layout / 线格式交叉引用与全面复核。

### D14.11 预提交门禁进批次判据
每个 E1 批次结束（每次 commit 前）必须跑：
`dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`（空输出）**与**
`jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx`（零 `<Issue>`），
时间预算：format 数分钟、inspectcode 10–20 分钟。Contracts 不豁免分析器包（`Directory.Build.props:14` 的条件）。

### D14.12 `error` 记录字段集（确认 D9）
`{type, arm, kind, label, error, message, detail, startedTicks, endedTicks}`；`error` = 异常类型名，
`detail` = 最内层异常类型名（`GetBaseException().GetType().Name`）。**不**新增 `run.json` 数组，
`run.json` 保持 `arms[].failed` + 顶层 `failed`。
**取消的记账（E1-A check 后的裁定）**：`OperationCanceledException` **恢复改前行为**——写一条 `error` 记录，
`error = "OperationCanceledException"`（异常**族**，与真错误区分）、`message = "cancelled"`（改前文案）、
`detail` 仍按上面的公式（因此 `Task.Delay` 触发的取消会得到 `detail = "TaskCanceledException"`——
`error` 记族、`detail` 记具体类型，这是有意的分工，不是不一致）。臂仍 `failed:true`、退出码 1。

### D14.13 `Sample` 记录归 E2
E1 不动 `ResourceSampler`；sample 的字段常量与强类型化随 E2 的 ResourceSampler 拆分一起做。

### D14.14 plan 键的取值域（0 = 未声明）
`dnsPort ∈ {0} ∪ [1,65535]`；`tcpPercent ∈ [0,100]`；`cnameEvery ≥ 0`；
`ratePerSecond / payloadBytes / window / lanes / desktops / streams / intervalMs / idleSeconds / expectedBytes /
targetBytesPerSecond / lossWindowMs ≥ 0`；`seconds > 0`。臂内的 `Math.Clamp`/`Math.Max` **保留**作防御，
但静默 clamp 的输入在加载期变成硬错误。"0 = 未声明"写进 README 的 plan schema。

### D14.15 强类型 metrics 的生产路径（M1 裁定）
B1 起（按 E1 计划编号：E1-B1.2 的机制步），`result` 记录的唯一写手 `ClientRunner.WriteResultAsync` 走统一的 `IJsonWritable`：
每个臂的 metrics 值对象实现 `IJsonWritable.WriteTo(Utf8JsonWriter)`；未迁移的臂用**过渡适配器**
`DictionaryMetrics : IJsonWritable` 包住旧字典（B2 随臂迁移逐个删除）。
于是 IDLE/THRU 的形状测试**直接读落盘的 `.jsonl` 字节**，测试面 == 生产面。

### D14.16 裸字段名字面量的作用域与 gate
AC 收窄为："`result`/`armSummary` 的顶层与 `metrics` 子树字段名只由 `ArmKeys` 定义"。
gate：① `rg` 在 `Client/Arms/**` 与 `ClientRunner` 的 4 个 writer 内无 `writer.Write*( "…" )` 形式的字面量命中；
② 一条 xunit 源扫描测试（`[CallerFilePath]`）断言已知键名不以字面量出现在这些文件里。
`ResourceSampler` 与 `Target/**` 的键显式归 E2（D12）。

### D14.17 `ArmKeys` 按族分片
`ArmKeys.Latency.cs`、`ArmKeys.Mix.cs`、`ArmKeys.Dns.cs`、`ArmKeys.Loss.cs`、`ArmKeys.Reliability.cs`、
`ArmKeys.Persistent.cs`、`ArmKeys.Idle.cs`、`ArmKeys.Throughput.cs`、`ArmKeys.Control.cs`、`ArmKeys.Common.cs`
（顶层记录键 + `parameters` 6 键 + `gates` 11 键）。每个分片 ≤ 400 有效行；BASE 相位用嵌套类表达层级；
`classes.*` 每层各写常量；`parameters` 也建常量。

### D14.18 `FrameStreamReader` 的喂入接缝
加 internal ctor 重载 `FrameStreamReader(Func<Memory<byte>, CancellationToken, ValueTask<int>> read, int capacity = DefaultCapacity)`；
现有 `Socket` ctor 委托它一行。测试用脚本化委托喂"一帧拆三段"与"EOF 落帧中间"。

### D14.19 退出码的判据通道
- **单元层**：断言 `PlanFile.TryLoad` 失败且 `error` 文本含 arm/键/观测值（含 offered 与 `MaxSequence`）。
- **退出码层**：用发布后的二进制跑最小 plan，命令与观测退出码记进 check 报告（不追求在 `dotnet test` 里断言进程码）。

### D14.20 Contracts 的可见性与反射测试约束
`Contracts` 的类型一律 `public`（它就是契约 seam；`WinForward.Configuration.ConfigurationModels.cs:116` 有先例）；
harness 其余保持 internal，仅 `WinForward.E2E.Tests` 一条 IVT（若确有必要 internal，则三条 IVT 全列）。
形状测试的反射只允许 `typeof(...)` **字面量**（注册表每 kind 一行 `typeof(XxxMetrics)`/`typeof(ArmKeys.Xxx)`），
不得用 `Type` 变量反射（`EnableTrimAnalyzer`/`EnableAotAnalyzer` 在 `Directory.Build.props:10-11`，
测试工程同样吃，`IL2075` 会因 `TreatWarningsAsErrors` 直接构建失败）。

### D14.21 `Dictionary<string, object?>` 清零的确切范围
允许值对象**内部**用字典构造嵌套块（`MixArm`/`ReliabilityArm`/`DnsArm` 的 `classes.*` 等），
但不得再有"返回 `Dictionary<string, object?>` 的辅助函数"或把它当跨模块载体；
`BaseArm.ReadCount`/`ReadMilliseconds` 必删（AC2 点名）。

### D14.22 E1-A 的测试清单补齐
- **design §6 层一 #8 的 E1 部分**：`UdpReliabilityTracker` 的记账恒等式与 `OutOfRange` 上界（纯逻辑，无并发）+
  D14.5 的 D7 边界与接收侧回归护栏（**并发契约测试归 E2，记账口径归 E3**）。
- **design §6 层一 #10 全量归 E1-A**：`PlanFile` 白名单拒绝未知 key（AC7）、11 份 shipped plan 全部通过、
  **内置默认 plan**（`TryLoad(null)`）通过。
- E1 的 AC 清单补 **AC7**（未知 key 报 arm + key）。
- `#6` 的 W 判据在 E1 的落点：A1 第 10 条（遍历 11 份 plan，`kind ∈ {loss,mix}` 必须声明 `lossWindowMs`，
  失败打印 plan 路径 + arm 名）；`metrics.window == 声明值` 的行为断言归 B1.4/B2。

### D14.23 其它小项
- **不做** `JsonValue.Write` 的 `default: throw`（该 dispatcher 随 `JsonValue` 在 B2 删除，"审计 §1.2 由删除闭合"）。
- `PerSecond` 的 9 个调用点逐个处理：`LatencyArm.cs:241-242` 是算术消费者（显式处理 null）；`:349/:356` 的插值格式化要支持 null。
- `JsonValue.Round`/`Microseconds` 搬 `Contracts/Json/NumberFormat.cs` 并加值快照测试；"无裸 `(double)a / b`"
  在 E1 先落一条源扫描 gate，长期家是 `analyzers/WinForward.Analyzers`（登记后续）。
- `TcpCommand.Name` 唯一性测试标注为"回归护栏（今天必绿）"，并强化为"distinct 名字数 == 成员数 +
  已定义成员不得映射到兜底字面量"。
- `selftest.sh` 的 usage 检查移到启动 target 之前；前导 `-` 检查只对字符串类选项，错误信息给选项名与值。
- 臂名上限：sanitize 后 **128 字符**（错误信息含原臂名、映射后文件名、长度）；运行前另做一次
  输出路径长度检查（组件 ≤ 255，总长保守 ≤ 250），超标为 usage error。
- 内置默认 plan 的 8 臂 = LAT/LOSS/REL/THRU/DNS/MIX/IDLE/BASE（`PlanFile.cs:35-48`）；
  新增 `planSource: "builtin" | "file"` 到 `run.json`（旧的 `planPath:null` 语义保留并写进 README）。
- `TryReadInt`：删 `ReadInt` 与 `ReadArmNumbers` 的 15 个调用点，改三态读取；`TryReadInt` 加 int 范围判断；
  `1e-9` 提为常量。
- 空值/前导 `-` 等 CLI 校验集中在 `Cli/`（E2 抽 `CommandLine.cs` 时只搬不改语义）。
- `UdpReliability.cs:127-129` 的注释改为不变量陈述并指向上游加载期校验处（一行，零行为风险）。
- 抖动带（D7.3）按**逐键带宽**实现：结构类必须相同或有出处；恒等类必须相同；数值类落在 run1↔run2 带宽内或有出处。
  **不得**用全局 tolerance。

---

## D15. 比对分类的精化（E1-A check 后裁定，**在 E1-B1 落地**）

E1-A 的实测暴露：两次运行带宽**不足以**覆盖第三次运行的延迟/CPU/内存抖动（478 条超带项里 128 条是时钟、
33 条是 `pid`、213 条是真实测量的读数）。因此把 `compare-records.py` 的比对精化为**四类**：

| 类 | 例子 | 判据 |
|---|---|---|
| **结构类** | 键集、类型、数组长度、字符串值、`arms[].file` 映射 | 必须相同，或差异命中 `contract-rename.json`/语义清单 |
| **身份类** | `*/pid`、`sources/port`、路径类、版本/hash | 只校验**存在与类型**，值不比对（新增 `identityPathPatterns`） |
| **契约计数类** | counters（`sent`/`arrived`/…）、`gates.*`、booleans、枚举名 | 必须逐键落在带宽内（契约计数器带宽为 0） |
| **测量读数类** | `*Us`/`*Ms`/`*Bytes`、CPU、内存、吞吐、`latency/*/count` 之外的直方图读数 | 作为 `observed movement` **信息性**报告，不算失败；`--strict` 下逐条列出 |

配套清理（同批）：
1. `volatileKeyNames`/`volatileKeySuffixes` 里对数值无效的条目**删掉**——配置不得声明与行为不符的语义；
2. `Side()` 自动识别 `out/`（被指到 `runN/out` 时不再静默跳过 ledger/target.out）；
3. `allowedCountDelta` 默认 **0**：记录数漂移必须由配置显式声明，否则算结构差异；
4. `require_python3()` 改为真实检查（`sys.version_info >= (3, 10)`）或删除；
5. 越界数值键的错误文本区分"非整数"与"超范围"（现状 `"window":3000000000` 报 "is not an integer"，略失准）；
6. `gates/inFlightCeilingMs` 是四类里的跨类键（`gates.*` vs `*Ms`）——B1 实现四类时必须显式处理，按测量读数归类。

**登记、不在 E1/E2 修**：`seconds` 无上界（D14.14 只要求 `> 0`）——`latency` 臂给 `seconds:1e18` 会立即结束并
披露 `gates.scheduleTruncated`/`laneShortfall`（不是静默），gate 的分析器消费面属 E3。
`ClientRunner.cs` 493 有效行 > 400 与 E2E 其余 6 个超标文件的拆分正是 E2 的工作；
`D13.4` 登记的 `WinForward.Benchmarks/` 3 个文件与本任务无关，二者不要混淆。

---

## D16. E1-B1c 之后的裁定与 B2 批次

### D16.1 比对工具：没有 `--band` 时契约值按**带宽 0** 判（U1）
`--band` 缺失不再等于"契约不检查"（那种模式会让真实漂移静默通过）。`--write-band` 仍是**测量**模式
（跑两次基线、写出逐键带宽）；summary 必须显式写出"band = 0（未测量）"，不得只打印 `contract=0`。
check 已实测：该改动对 `run1↔run1`、`run1↔run2`、`run1↔b1c` 三对 verdict 零影响。

### D16.2 配置里 0 命中的模式（U4）
`record-normalize.json` 中当前 0 命中的 `readingPathPatterns`/数组模式（`*/workingSetBytes`、`*/threads`、
`metrics/*bytesEchoed`、`tcp/expectedBytes`、`*/sources`、`metrics/udp.lane*`）**要么改成真实拼写、要么删除并留注释**——
配置不得声明无效的形状。

### D16.3 登记债（不在 B2 修）
- `compare-records.py` 有效行 348 → 702（Python 不受 400 行 `.cs` 规则约束，但建议按
  `Config`/`render`/`rename` 三个现成接缝拆分）；`contract-inventory.py::groups_of()` 有同形的潜在重复（U2/U3）。
- 二者登记给 **E2 的收尾或 E5 的清理**，不阻塞 B2。
- `--write-band` 与 `--band` 同时给出时应直接报错（现在会"按旧 band 判再覆写"）——B2b 顺手修。

### D16.5 `D14.20` 的收窄（B2a check 后裁定）
「Contracts 的类型一律 public」收窄为：**seam 类型（harness、测试、分析器要消费的）必须 public；
仅在一个类型内部使用的辅助类型允许 `internal`**（当前唯一实例是 `Json/Reading.cs`，harness/测试都不消费它）。
理由：公共面越小越好，且 `Reading` 改 public 只会改变二进制哈希、不增加任何可测能力。

---

## D18. E2 接缝与并发契约的最终形状（E2 计划审查后裁定，取代 D3/D4 的草案）

### D18.1 引擎/策略边界（一条真正的接缝：发送机制 vs 回复解释）

```csharp
// 传输 adapter：两个实现。它只报告"收到了什么"，绝不解释线格式是否合法。
internal enum LaneReceiveKind { Payload, EndOfStream, Malformed, IoError }
internal readonly record struct LaneReceiveResult(LaneReceiveKind Kind, int Length);
internal readonly record struct LaneOpenResult(bool Ok, string? Error);
internal readonly record struct LaneSendResult(bool Accepted, bool WouldBlock, string? Error);

internal interface ILaneTransport : IDisposable
{
    ValueTask<LaneOpenResult> OpenAsync(CancellationToken ct);          // connect 在这里，失败=结果而非异常
    ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken ct);
}

// 策略：窗口准入 + 帧构建 + 回复解释 + 全部接收侧计数（per-arm 状态住在策略里）
internal interface ILanePolicy
{
    int BuildRequest(long sequence, long intendedTicks, Span<byte> destination); // 0 = 该槽位不发（策略自己的窗口决定）
    void OnSent(long sequence, long intendedTicks, in LaneSendResult result);    // 发送线程
    void OnReceive(in LaneReceiveResult result, ReadOnlySpan<byte> payload, long nowTicks); // 接收线程：解码 + 分类 + 入队
    void Settle(long nowTicks);                                                  // 发送线程：结算队列
    bool IsDrained { get; }                                                      // 臂末尾的收尾条件
}
```

- **`LaneEngine<TTransport>` 只拥有发送侧**：配速、offer 循环（`BuildRequest` → `SendAsync` → `OnSent`）、
  有界 Defer 队列、`scheduleTruncated`，以及**引擎自己的计数** `LaneCounts`
  （`Supplied / SentOk / SendWouldBlock / SendFailures / DeferredQueued / DeferredDropped / ScheduleTruncated`），
  对外只给不可变快照。**窗口准入与 in-flight 由策略持有**（`UdpLatencyState`），引擎不复制。
- `LaneCounts` 与策略计数器"不相交"的判据落地为：`typeof(LaneCounts).GetProperties()` 的名字集合 ∩
  策略状态类型（`UdpLatencyState`/`LatencyTcpState`）中**表示计数的公开属性**名字集合 = ∅；
  策略侧计数必须去掉下划线前缀暴露为属性（否则断言空转）。
- **窗口策略只暴露 `Defer`**（唯一真实消费者是 `LatencyArm`）。Drop/Block 不建枚举值（避免零消费者），
  `DnsArm`/`LossArm`/`MixArm` 的窗口语义留在各自臂里并**如实登记为已知重复**。

### D18.2 接收/结算的线程契约（取代 D4 的"排空→才 Classify"）

1. **分类在接收线程**（解码 + `ReplyClassifier` + 计数），产物是一条小的 **settlement 记录**
   投进 `ConcurrentQueue<Settlement>`（每回复一次入队；只有引擎覆盖的 lane，LAT ≤ 数百 rps，可接受）。
   span 不出接收循环。
2. **结算 `Settle` 在发送线程**：`Pacer.WaitUntil` **之后**、下一次 `BuildRequest` 之前调用一次；
   臂末尾再调用一次直到 `IsDrained`。`pending.TryRemove`、RTT、直方图、`inFlight--`、接收侧计数
   **只在 `Settle` 里发生**（这样 `UdpReliabilityTracker` 的单写者契约在 E2 就成立，E3 的并发修复只剩 LOSS/MIX）。
3. **臂末尾顺序**：停止 offer → 取消接收循环并 join → `Settle` 排空 → 计算 gates/metrics。
4. 发送线程独占"账本"（`UdpLatencyState` 的字段 + `UdpReliabilityTracker`）；接收线程只准写队列。
   类文档逐字写明这条契约，并由**并发测试**（E2 必须写：N 轮发送/接收并发，恒等式不破）覆盖。

### D18.3 回复分类的归属（不抹平真实差异）

- `ReplyClassifier.Classify(ReadOnlySpan<byte>, uint expectedConnectionId) → ReplyVerdict(Kind, Sequence, PayloadBytes, FrameDecodeError)`
  是**纯函数**，**只被 UDP 阶梯使用**（LAT/LOSS/MIX 三处合一）；调用者自己记账。
- **TCP lane 不使用它**：TCP 的回复匹配是 `FrameStreamReader` + FIFO 队列（按到达顺序，不看 sequence），
  这条差异**保持不变**并在计划里显式登记。
- 三处阶梯的统一差异清单（必须写进 E2 证据）：统一 `WasSent` 检查（**LAT 补上**，登记为有意行为修正）、
  `CorruptKnownSequence` 携带 `FrameDecodeError`、`Undecodable` 分支；**保持**：LAT 的
  `_received` 含重复应答（`UdpLatencyState` 文档）、LOSS/MIX 的 tracker 记账路径、`foreignConnection` 语义。
- **验收**：`received`/`unmatchedReplies`/`inFlight`（三个零宽带宽契约量）在 E2 之后与基线**逐值相同**
  （除非命中登记的行为修正并逐条解释）。

### D18.4 其余 E2 裁定

| 项 | 裁定 |
|---|---|
| 5 处 UDP `ConnectAsync` | E2 落**机制**（引擎 `OpenAsync` 或共享 `SocketOps.TryConnectAsync` 返回结果，不再裸奔）；行为变化（失败→计数+臂继续）**在 E2 登记**，E3 只复核。AC 可 grep：`ConnectAsync` 不得出现在 try/catch 或该 helper 之外 |
| `MaxPayloadLength` 用于 plan 校验 | 语义：`payloadBytes` 的域为 `0..FrameCodec.MaxPayloadLength`；超限 → 退出码 2，错误文本 `'payloadBytes' is <v>, which is outside 0..<max>` |
| 有效行扫描脚本 | E2-b 新建 `benchmarks/WinForward.E2E/scripts/effective-lines.py`，规则与 `directory-structure.md` 一致（去空行、去 `//` 与 `/* */` 注释）；**严格口径**下的当前值是 803/678/612/500/460/449/567。AC1 的判据 = 该脚本在三项目上无输出 |
| pragma 目标 | 引擎合并 LAT 的 4 个调用点 → 目标 **6 处**；DnsArm 的两处留在臂内（不并入引擎）；完成时用 `rg` 报实际数字 |
| 分配 gate | 引擎跑在**调用线程**（不用 `Dedicated`）时测 `GC.GetAllocatedBytesForCurrentThread()`；按 `hot-path.md` 的开窗/配对规则；**必须有反证用例**（故意分配的实现必须被判红） |
| 形状测试 kind 数 | 9（计划里的"8"是笔误） |
| `PlanFile` 路径 | `benchmarks/WinForward.E2E/Client/PlanFile.cs` |
| 比对工具欠账 | D17.4 的"默认 summary 报越带读数计数"→ **E2-a**；D17.3（多次运行带宽）与 D16.3（脚本拆分）→ **E5** |
| E2-c | `--help` 与全部错误消息快照逐字比对；target help 里补 E1 新增的退出码 1 的说明属于**有意变更**，单独登记 |
| `LatencyArm` 行号 | 发送三件套现在是 `463-571`/`694-796`；`MixArm` 的 page DNS connect 在 `:467` |

### D18.5 聚焦复核后的修订（**开工 2a-2/2a-3 前必须遵守**）

1. **RTT 基准**：`Settlement` 必须携带 `ReceivedTicks`，RTT 一律用**收到时刻**算（今天就是收到时刻；
   用 `Settle` 的时刻会给每个样本加 0–1 个配速间隔，而 `latency/*` 是读数类、默认比对不报错）。
2. **接收线程只解码+分类，不做任何计数**（D18.2 第 1 条的"…+计数"作废）；计数在 `Settle`。
3. **LAT 的 UDP 回复四步顺序写死**：`_received++`（含重复应答）→ `pending.TryRemove` →
   成功：`RTT(ReceivedTicks)` + `inFlight--`；失败（不在 pending / WasSent 不成立）：`_unmatchedReplies++`，**不减 inFlight**。
4. **臂末尾保留 grace drain**：停止 offer → **有界 grace**（继续接收并每 tick `Settle`，直到 book 空或 1 s 上限）→
   取消并 join 接收 → 最后一次 `Settle` → 计算 gates/metrics。（今天的 `GraceDrainAsync` 是尾 cohort RTT 的来源。）
5. `IsDrained => settlements.IsEmpty`；另加 `BookEmpty`（pending 空且 in-flight 0）作为 grace 的终止条件。
6. `OnSent` **失败时也调用**（`Accepted=false`）；`WouldBlock` 必须在 `await` **之前**从 `IsCompleted` 取
   （审计 §9.6 的复用手法）。
7. `BuildRequest` 返回**三态**：`Send(length)` / `Defer`（窗口满但可排队）/ `Skip`（该槽位整槽跳过）；
   `DeferredQueued`/`DeferredDropped` 必须显式映射到契约键 `windowOverflow`/`backlogDrops`（接线表逐行写）。
8. **严格串行**：同一 lane 上 `BuildRequest → SendAsync → OnSent` 不得重入；`OnSent` 必须先于下一次 `BuildRequest`；
   `BuildRequest` 幂等、无副作用（Defer 后的重试走同一 sequence）。
9. `Settlement` 是 `readonly record struct`；入队在**接收线程**（分配 gate 只覆盖发送路径，这一点写进性能契约）。
10. **pragma 数字**：删掉"LAT 的 4 个调用点 / 目标 6"的说法；实施完成后用 `rg` 报实际值（上限 8），
    并说明是否把等待包进非 async 的 `Pace(...)`（那会让部分调用点免 pragma）。
11. **单写者范围**：E2 只让 `UdpLatencyState` 单写者；`UdpReliabilityTracker` 的多写者问题仍归 **E3**
    （LAT 不用 tracker，`rg 'UdpReliabilityTracker' LatencyArm.cs` = 0）。
12. **零宽发布键的核对清单**（不是"三个"）：`metrics/*.received`、`metrics/*.unmatchedReplies`、
    `metrics/*.outstandingAtTeardown`、`latency/*-rtt/count`（冻结带逐键 `maxAbsDelta=0`）；
    in-flight 只需一条内部单测（它不是发布键）。
13. **不相交断言要带负向**：除 `typeof(LaneCounts)` 的属性名 ∩ 策略计数属性名 = ∅ 外，还要断言策略状态类型
    **没有**同名的私有字段（`GetFields(Instance|NonPublic)`），否则"删了属性、留下 `_sentOk` 字段"照样绿。

### D18.6 2a-2 check 后的补充裁定（2a-3 必须遵守）

1. **`LaneCounts` 增加 `DeferredPending`（第 8 个计数）**：返回时 Defer 队列的占用数。
   `metrics/*.outstandingAtTeardown` = 策略侧 pending 数 + `DeferredPending`（与旧 `pending.Count + backlog.Count` 同义）；
   不要用"七计数相减"去推（只在策略从不返回 `Skip` 时成立）。
2. **发送失败的两种形状与旧计数对齐**：
   - 同步抛 `SocketException`：**计数 + 继续**（与异步失败同策；旧代码结束循环属偶然，登记为有意变更）；
   - 异步失败（`WouldBlock && !Accepted`）：**`SendWouldBlock` 与 `SendFailures` 都 +1**（保住旧发布值）。
3. **TCP 接收终结性映射**（2a-3 的 adapter）：`BadMagic`/`BadLength` → `IoError` + `Detail`（流已失步，计数后终止）；
   `BadChecksum` → `Malformed` + `Detail`（计数后继续）。`EndOfStream` 终止。
4. **分配 gate 必须覆盖真实协作者**：2a-3 真实 transport 落地后补一条对真实 transport 的 gate
   （`hot-path.md` / `quality-guidelines.md:48` 的要求），fake 的那条保留。
5. **`Settle` 必须排空整队**：引擎的 `SettleBurstLimit=8` 用尽仍未 `IsDrained` 时会返回；
   策略的 `Settle` 契约是"尽可能排空（循环到队列空或预算用尽）"，不能只结算一条。接线表里写明。

---

## D17. 零宽带宽与测量统计的裁定（B2b check 后）

**现象**：`LATLOAD` 的 `latency/tcp-rtt/count`、`metrics/tcp.outstandingAtTeardown`、
`metrics/tcp.unmatchedReplies` 在冻结带宽里是零宽（两次基线恰好相同），宿主争用时第三次运行会越带——
同一二进制再跑一次即回到基线的值。这不是回归。

**裁定**：
1. **延迟直方图的统计量（`latency/*` 的 `count`/`minUs`/`maxUs`/`meanUs`/各百分位）归"测量读数"类**——
   它们是样本聚合，和已经归读数的 `p99Us` 同类，不该用契约带宽判。
2. 其余契约计数（`metrics/*`、`gates/*`）保持硬判据；**零宽带宽路径的越带**必须在报告里显式标注
   `zero-width band (unmeasured spread)`，并在证据文档里附一次**同二进制确认运行**的结论
   （B2b check 即照此办理）。工具不得因此自动放行。
3. 工具改进（登记，E2/E5）：带宽测量从"两次运行"扩到 **N 次（≥3）**，对零宽路径给出
   `observedSpread` 与建议重测标记；`--write-band` 支持多目录。当前两次基线仍是 B2c 的判据基线。
4. **读数敏感性的流程补充**（B2c check 实测：把 `latency/tcp-rtt/*` 全部 ×2，工具仍 exit 0、
   默认汇总读数计数不变，只有 `--strict` 可见）：**默认 summary 必须增加一行"越带读数计数"**；
   **凡是声称"行为等价"的批次，证据文档必须附 `--strict` 的读数移动摘要，并对超出带宽的路径逐条解释**
   （工具改动归 E2/E5，流程要求立即生效）。
5. `gates` 的值类型是 `Dictionary<string, double>`（D5 第 5 条原写 `long`）：
   `gates/inFlightCeilingMs` 是真小数，`long` 会改契约值；整数值 double 序列化成同一文本（B2c 已验证字节等价）。
6. `parameters` 的键序在强类型化后按 `ArmKeys.Common.Parameters` 的声明序（MIX/PERSIST/REL 的文档序因此变化，
   集合不变、工具按路径集合判）；`byMode` 的成员集跟随 plan 的 `modeMix`，形状契约钉每块 schema + 模式名绑定。

### D16.4 B2 批次（9 条改名按臂分布决定顺序）
| 批次 | 内容 |
|---|---|
| **B2a** | `LatencyArm` + `DnsArm` 类型化（`ArmKeys.Latency`/`ArmKeys.Dns` 分片）；执行 3 条改名（`metrics/tcp.sentOk`、`metrics/udp.sentOk`、`metrics/udpSent`）；形状测试覆盖这两个 kind；D16.1/D16.2 同批落地 |
| **B2b** | `MixArm`（`classes.*` + `desktops.*`，5 条改名）+ `BaseArm` → **纯改名** `ControlArm`（`kind:"base"` 不动；执行 `metrics/latency/*.sentOk` 2 条改名）；`ArmKeys.Mix`/`ArmKeys.Control` 分片 |
| **B2c** | `LossArm`/`ReliabilityArm`/`PersistentArm` 类型化；`JsonValue` 退役；`Dictionary<string, object?>` 清零（AC2/D14.21，含 `ControlArm.ReadCount`/`ReadMilliseconds` 删除）；`parameters` 强类型化；字面量 gate（D14.16）；改名表全量判定（`--batch B2` 9/9 satisfied） |

每批次结束都要跑六条门禁 + 一次 `compare-records.py`（带改名表与批次）并落 `research/baseline/B2x-*.md`。
