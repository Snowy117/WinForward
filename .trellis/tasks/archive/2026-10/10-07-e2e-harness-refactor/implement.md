# 执行计划：WinForward.E2E 重构

> **开工前裁定（2026-10-07，设计稿审查后）**：本文件的 E3 步骤 1（"E2 抽出来的接缝在 E3 还会改一次接口"）
> 与 E3 的"每条修复一个提交"、E4 的 `--tables` 切片、层零基线判据等，均已被
> `design-decisions.md`（D4/D8/D6/D7）修订。**冲突处以 `design-decisions.md` 为准**。

对应 `prd.md`（R1–R12）与 `design.md`（§1–§8）。

## 交付物地图

父任务管需求与总验收；下面挂 5 个可独立规划、实现、检查、归档的子任务。
**依赖不是靠树位置表达的**，每个子任务在 `prd.md` 里写明它必须在谁之后。

| 子任务 | 内容 | 依赖 | 对应需求 |
|---|---|---|---|
| **E1 契约与安全网** | Tier 0 止血；建测试项目与 `Contracts` 项目；字段名单一定义点；harness 写出侧类型化；契约改名；`null` 约定 | — | R3, R4, R11（部分）, R13 |
| **E2 结构与传输接缝** | 7 个超标文件拆分；`Lanes/` 接缝；Target 侧拆分；零散 P2 修复 | E1 | R1, R2, R7（部分） |
| **E3 语义修复** | `harness-audit.md` §二 13 条 + §三 3 条 + 竞态与矛盾语义统一 + D7 记账半 | E2 | R5, R6, R7, R8 |
| **E4 C# 分析器** | `WinForward.E2E.Analysis` 重写（5 批）；双向 oracle 对比；Python 版退休 | E1（契约）、E3（新字段） | R9, R10 |
| **E5 文档与验收** | README 三节同步；分析器 README；§四口径披露；Windows 轻量验证；全量门禁 | E2, E3, E4 | R12, R14 |

**编号对照**（`design.md` §7 用 P 编号，本文件用 E 编号，**两者不是一一对应**，以此表为准）：

| design.md §7 | implement.md |
|---|---|
| P-1 Tier 0 止血 + P0 安全网 + P1 契约 | **E1** |
| P2 零散修复 + P3 传输接缝 + P4 结构拆分 | **E2** |
| P5 语义修复 | **E3** |
| P6 分析器 | **E4** |
| P7 文档 + P8 Windows 验证 | **E5** |

E2 与 E4 都只依赖 E1，理论上可并行；但 E4 需要 E3 引入的新字段（`detail`、`acceptErrors`、
`undecodable` 等）才能完整，所以实际顺序是 E1 → E2 → E3 → E4 → E5。

---

## E1：契约与安全网

### 步骤

0. **Tier 0 止血（最先做，每条独立 commit）**

   让 D1–D7 与 `selftest.sh` 的问题**不再崩溃、不再静默**。这一步只做「止血半」，
   越界槽的记账语义属于 E3。

   | 顺序 | 内容 | 位置 | 门禁 |
   |---|---|---|---|
   | 0.0 | **先冻结回归基线**（**已由 DD D14.8/D14.9 取代本行**：先 `scripts/publish.sh` 强制重建，记录 commit/`git status`/二进制 sha256，跑**两次**并逐次拷到 `research/baseline/runN/`；脚本与归一化配置的路径见 DD） | `scripts/` | 脚本能对自身跑出空 diff |
   | 0.1 | **`MarkSent` 越界止血（D7）**：边界检查提到方法开头，越界序号不写两个数组。**守卫必须仍然让 `_sent.TrySet(sequence)` 被调用**（由它维护 `OutOfRange` 计数）——若在它之前直接 `return`，"已声明的披露" `outOfRangeSequences` 会被关成恒 0 | `UdpReliability.cs:196-201` | **单元测试**：`MarkSent(MaxSequence + 1, …)` 不抛、`OutOfRange == 1`、`SentOk` 不涨。**不要**用"跑越界 plan 看 `outOfRangeSequences > 0`"作判据——它会被 0.2 堵死（两条门禁互斥） |
   | 0.2 | 加载期校验 `ratePerSecond × seconds ≤ MaxSequence`（D7 可诊断化）；建立按 kind 的 `Validate()` 机制，并**顺手把 kind 表收成一处**（`ArmKind` 描述符表：名字 + 校验器，消化 `PlanFile.cs:50` 与 `ArmDispatch.cs:5-17` 的双份清单，也避免 `Validate()` 变成第三处） | `PlanFile` + `ArmSpec` | `20000/s × 20s` 的 plan 变 load error（**退出码 2**）。与 0.1 同 commit |
   | 0.3 | **顶层兜底**：臂级异常 → `error` 记录 + `run.json` 的 `failed:true`；`Program.cs` 两个 verb 各加顶层 catch | `ClientRunner.cs:262-277`、`Program.cs:92-110,68-90` | D1/D4 的 plan：退出码 1、有 `error` 记录、`failed:true` |
   | 0.4 | 空 `--plan`（D2）。**语义明确**：`--plan=`（显式空值）是 **usage error（退出码 2）**；`--plan` 完全不给（缺省）仍使用内置默认 plan。**已由 DD D14.1 取代**：判定落在 `ClientRunner.TryApply` 的 `--plan` 分支（拒绝空串）；`PlanFile.cs:125` 由 `IsNullOrEmpty` 改 `path is null` | `PlanFile.cs:125` + `ClientRunner.cs:445` | `--plan=` → 退出码 2；**不带 `--plan`** → 正常跑内置默认 plan。两条都要测，只改 `ClientRunner.cs:445` 不算完成（那样仍会静默换 plan） |
   | 0.5 | 文件名单射与长度（D3/D4）：重名检查改用**映射后**的名字 + 加载期长度上限 | `PlanFile.cs:103,206-215` | `A/B`+`A_B` 与 270 字臂名都变 load error |
   | 0.6 | 范围校验 + `TryReadInt` 强转（D1/D5）：`dnsPort` 1..65535、各键上下界、`lossWindowMs ≥ 0`、小数写进 int 键报错（**注意**：小数检查今天已存在，病根是它返回 false 后静默回落 0、臂内再回落默认值——要改的是**回落路径**，不是加检查） | `PlanFile.cs:267-321` | `dnsPort:99999` 与 `window:100.5` 都变 load error |
   | 0.7 | 空 `--sampler-process` 拒绝（D6）；`selftest.sh` 漏 plan 参数时退出非零 | `ClientRunner.cs:129-131`；`selftest.sh:34-40` | 单测 + 两条手工命令 |

   **不碰任何记录形状**。"`outOfRangeSequences > 0`"的复现路径是**单测**（0.1）而非端到端 plan；
   这条字段本身是 README:531-535 已声明的披露信号，不是本批次引入的。

1. **建测试项目骨架**
   - `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj`（照 `tests/WinForward.Core.Tests` 的形状：
     `Microsoft.NET.Test.Sdk` + `xunit` + `xunit.runner.visualstudio`，全部走中央版本管理）
   - 加进 `WinForward.slnx` 的 `/tests/` 文件夹
   - `benchmarks/WinForward.E2E/WinForward.E2E.csproj` 加 `<InternalsVisibleTo Include="WinForward.E2E.Tests" />`
   - **先写纯函数测试**（design §6 层一的 1/2/3/5/6 号）：`Crc32C`、`Filler`、`FrameCodec`、
     `TcpCommand`（名字互不相同）、`LedgerWriter`（格式快照）

2. **建 `WinForward.E2E.Contracts` 项目**
   - `benchmarks/WinForward.E2E.Contracts/`，`net10.0`，无 `OutputType`（库）
   - 加进 `WinForward.slnx` 的 `/benchmarks/` 文件夹
   - `ArmKeys.cs`：每臂字段名的 `const string`，**唯一定义点**（design §2.2）
   - `Metrics/`：每臂的强类型值对象（`LossMetrics`、`LatencyMetrics`、`DnsMetrics`、`MixMetrics`、
     `ReliabilityMetrics`、`ThroughputMetrics`、`PersistentMetrics`、`IdleMetrics`、`ControlMetrics`）
   - `Json/Rate.cs`：唯一比率函数，分母为 0 → `null`（修 #13）
   - `Json/JsonlSink.cs`：`JsonlFile` 与 `LedgerWriter` 的合一（design §4），构造参数
     `(path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope)`
   - `Records/`：`ArmResult`、`ArmSummary`、`RunRecord`、`ErrorRecord`

3. **harness 接入**
   - `WinForward.E2E` 引用 `Contracts`
   - 各臂改为产出强类型值对象；`ArmOutcome` 的 `Dictionary<string, object?>` 退役
   - `ControlArm`（原 `BaseArm`）直接持有子臂结果，删掉 `ReadCount`/`ReadMilliseconds`
   - `ClientRunner` 的 4 个 `Write*Async` 改用 `JsonlSink` + `ArmKeys`
   - **契约改名**（design §2.2.1）：同一概念的多种拼写收敛为唯一叶子名；结构性分组保留
   - 形状测试：值对象属性集合 == `ArmKeys` 常量集合（design §2.3）

### 验证

```bash
dotnet build WinForward.slnx -c Release                      # 零警告
dotnet test  tests/WinForward.E2E.Tests -c Release           # 绿
cd benchmarks/WinForward.E2E && scripts/publish.sh
scripts/selftest.sh scripts/plans/selftest-plan.json         # 绿
```

外加一条**契约形状对比**：对同一份 selftest 输出，新旧 key 集合的差异**恰好等于**改名表。
改名表落盘 `research/contract-rename.md`。

### 风险文件

`Client/ArmContext.cs`（`ArmOutcome`）、`Client/JsonValue.cs`、`Client/JsonlFile.cs`、
`Target/LedgerWriter.cs`、每个臂的 `WriteMetrics`。

### 回退点（三个边界，与"每条独立 commit"调和）

E1 内部有三次性质不同的改动，不能笼统地"整体回退本批次"：

| 边界 | 内容 | 回退粒度 |
|---|---|---|
| **B1** | Tier 0 止血（步骤 0.0–0.7） | **逐条回退**（0.1–0.7 各自独立 commit，互不依赖；只有 0.1+0.2 要同 commit） |
| **B2** | 测试工程 + `Contracts` 骨架（步骤 1–2） | 整体回退（新项目：删目录 + 撤 `slnx` 与 `InternalsVisibleTo`） |
| **B3** | 契约改名 + 类型化改造（步骤 3） | 整体回退（改名牵动所有臂，半途状态不可用）；判据是"新旧**路径**集合差异 == `research/contract-rename.md`" |

B1 结束、B2 尚未开始时是一个天然的安全点：此时 harness 行为已止血、测试工程已就位，
但契约还没动——**任何一步失败都能退回到这里，且退回后仍然是一个比今天更好的状态**。

---

## E2：结构与传输接缝

### 步骤

1. **零散修复先做**（低风险，且会减少后续搬移的干扰）
   - `TcpCommand.Name` 去掉两个 `_ =>` 兜底改 `throw`（已由 E1 的测试封住）
   - `LedgerWriter`/`JsonlSink` 把 `body(writer)` 移出 catch。**同时定明异常归属**：body 在 try 外
     之后，它抛出的编程错误会向上传到 `TcpTargetServer`/`DnsServer`/`UdpEchoServer` 的连接处理路径
     —— 计划要求**该连接记一笔失败并关闭，服务本身继续**；"磁盘故障不弄死 target"这条保证保留
     （只对 I/O 部分 catch）。`_gate.Release()` 与缓冲/writer 的 `await using` 生命周期要一起重排
   - `JsonValue.Write` 的 `default:` 从 `Convert.ToString` 改成 **`throw`**（今天不可达，但它会把
     `float`/`decimal`/enum 静默写成带引号的字符串）；配套两个单测：`float` 必须抛、
     `int?`/`double?` 的可空装箱必须写数字。**同时说明 `JsonlPolicy` 如何处理这条新异常路径**
   - 新增 `Target/Sockets.cs`：统一 bind，Unix 上显式清 SO_REUSEPORT
   - `TargetOptions` 补齐端口冲突校验（`dnsPort ∉ {tcpPort, udpPort}`）
   - `PlanFile` 加 kind↔key 白名单；`FrameCodec.MaxPayloadLength` 提为 `internal` 并被用它校验

2. **抽 `Client/Lanes/`**（**先做接缝，后拆文件**——`design.md` §4 的拆分表是按 P3 之后
   剩下什么来切的，顺序反了会拆出很快空掉的文件，例如 `LatencyPlan.cs` 一栏里今天就塞着
   将被 `LaneEngine` 吃掉的 `DeferredQueue`）
   - `ILaneChannel` + `TcpLaneChannel` + `UdpLaneChannel`：**两个适配器都是 `sealed class`**，
     约束 `where TChannel : class, ILaneChannel`（design §3.2 未定项 A）
   - `LaneEngine<TChannel>` + `LaneState`：配速、发送、接收调度、有界 drain、`scheduleTruncated`
     （design §3.3，含**计数归属**表：引擎不自持接收计数）
   - `Client/Lanes/ReplyClassifier.cs`：三份回复校验阶梯合一（**不是 `Wire/`**，否则形成
     `Wire → Client` 反向依赖）。**这是有意的行为修正**——给 `LatencyArm` 补上 `WasSent`，
     会改变它的 `unmatchedReplies` 语义，不得混进"行为零变化"的搬移
   - `LatencyArm` 的 TCP/UDP 两个 lane 改用引擎；`DnsArm` 两相复用引擎的配速/发送/drain 三项
     （窗口取 Drop 策略）。**`LossArm`/`MixArm` 不走引擎**（design §3.1.1），它们只共享 `ReplyClassifier`
   - 至少 5 处 UDP `ConnectAsync` 移进 `LaneEngine.OpenAsync`（含容易漏的 `MixArm.cs:457`）
   - **把 `Pacer` 的 pragma 与注释一起搬进引擎**（实测全仓 9 处，按引擎范围应搬 4 处并收敛为 1 处）
     ——它们必须随代码走，否则 quality gate 会红

3. **拆文件**（design §4，按 P3 之后的形态）
   - 7 个超标文件按表拆
   - Target 侧：`SourceCensus.cs`、`TcpConnectionProtocol.cs`、`TcpAcceptLoop.cs`、`SocketIo.cs`、
     `ILedgerSection`、`Cli/CommandLine.cs`
   - **`Cli/CommandLine.cs` 的解析器合一是重构而非搬移**（要抽"已知选项集合 + `TryApply` 回调"），
     单独一批，判据是"两个 verb 的 `--help` 文本与错误消息**逐字不变**"

### 验证

```bash
dotnet build WinForward.slnx -c Release
dotnet test  tests/WinForward.E2E.Tests -c Release
cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json
# 有效行检查 —— 只扫本任务范围内的三个项目
python3 - <<'PY'
import re, pathlib
roots = ['benchmarks/WinForward.E2E', 'benchmarks/WinForward.E2E.Contracts', 'benchmarks/WinForward.E2E.Analysis']
for root in roots:
    for p in pathlib.Path(root).rglob('*.cs'):
        if '/obj/' in str(p) or '/bin/' in str(p): continue
        t = re.sub(r'/\*.*?\*/', '', p.read_text(encoding='utf-8'), flags=re.S)
        n = sum(1 for l in t.splitlines() if l.strip() and not l.strip().startswith('//'))
        if n > 400: print(f'{n:>5}  {p}')
PY
```

**范围说明（审核发现）**：全域扫描 `pathlib.Path('benchmarks')` 会打进 `WinForward.Benchmarks/`，
那里有 **3 个既有的规范违反**：`Perf/SessionSetupDecompositionBenchmarks.cs`（909 有效行）、
`Stability/GcSoakScenario.cs`（684）、`Stability/UdpChurnScenario.cs`（410）。它们**不在本任务范围内**，
但也不能当作不存在——登记为已知债务（见 E5），扫描脚本必须按上面收窄到三个项目。

### 风险文件

`LatencyArm.cs`（热点路径）、`FrameStreamReader.cs`（6 个调用点）、三台 Target server 的 shutdown 顺序。

### 回退点

**先抽接缝（步骤 2），再拆文件（步骤 3）**，两批各自一个提交：
- 批次 2a：`Lanes/` 接缝 + 零散修复，判据是 `selftest` 绿 + `achievedRate`/`sendWouldBlock` 量级不变
- 批次 2b：纯搬移（行为零变化），判据是归一化记录比对为空
- 批次 2c：解析器合一（有行为风险），单独判据

---

## E3：语义修复

### 步骤

**先证据，后代码**——但证据的形态变了（审核建议）：**每条修复的定向证据直接写成
`tests/WinForward.E2E.Tests` 里的用例**，命名带缺陷编号（如
`SemanticFix_004_LossWindowReleasesWithoutArrival`）。它们是纯逻辑、本来就满足 R11 的
"不依赖网络与真实时钟"，写成 Markdown 落盘只会变成一次性的、不在任何门禁里的文本。
`research/semantic-fixes/` 只放"测试名 → 缺陷编号 → 结论 + 一条可重跑命令"的索引。

1. `UdpReliabilityTracker` 的并发契约（**先修**，因为多条修复依赖它的数据结构）
   - 选定模型（发送线程独占 book + 接收线程投递队列，或全 `Interlocked`），**并写进类文档**
   - 并发测试证明：发送与接收并发跑 N 轮，恒等式不破
   - **这个决定会反向影响 `LaneEngine` 接收侧的接口形状**：E2 抽出来的接缝在 E3 还会改一次接口，
     所以 E2 的验收只是"发送/排空路径就位"，不是"接缝定稿"
2. §5.1 逐条：#4、#5、#6、#7、#8、#9、#10、#11、#12、#14、#15、#16
3. §5.3 剩余项：
   - `ObjectDisposedException` 统一、`achievedRate` 统一（PERSIST 的完成口径改名 `completionRate`）
   - **D7 的记账半**（止血已在 E1）：越界槽在 `sent`/`supplied`/`clientSendLoss` 之间的归属
   - **`FrameReadStatus.Truncated`**（不是 E2 —— 它新增一条 verdict 路径，与"行为等价"冲突）
   - **账本三个新字段**：`detail` / `acceptErrors` / `udpReceivers`（只增不改名）
4. §5.2 公平性 3 条：**落点归 E4 的 `Analysis/Model/RowProfile`**，不写进 `Contracts`——
   `RowProfile` 承载的是产品级事实（"Proxifier 的 UDP 是泄漏的"、"WinForward 的 DNS 走直连"），
   那是分析器知识，不是记录契约。E3 只负责在记录里补齐支撑它的事实（target 侧的 UDP 源端点归属、
   DNS 路径标签），并给出这些事实的字段名与写入位置

### 提交图（审核指出"每条一个 commit"与依赖链冲突）

语义修复条目之间有硬依赖，机械地"一条一 commit"会让某些提交单独编译不过、或让"每条可单独回退"落空。
**按下面的分组提交**，组内不可分：

| 提交 | 内容 | 理由 |
|---|---|---|
| C1 | 并发契约 + 发送位图（#5） | 多条修复的数据结构基础 |
| C2 | #4 + #6（窗口按到期释放 + W 显式化） | #4 依赖 #5 与 #6；单独提交行为更糟 |
| C3 | #7（从预定时刻起算） | 独立 |
| C4 | #8 + #9（窗口放大 + gate 化 + TCP drain） | 都落在 `LaneEngine` 的 drain/窗口路径 |
| C5 | #10（重排按已发送最高序号）+ D7 记账半 | 都依赖 C1 的位图与越界槽口径 |
| C6 | #14（采样进程身份）、#15（DNS TCP id）、#16（BASE 控制组） | 各自独立，可拆三个提交 |
| C7 | #11、#12（恒零计数器、clientSendLoss 派生） | 都是输出字段的清理 |
| C8 | `Truncated` + 账本三字段 + 矛盾语义统一 | 都是新增/改语义的输出面 |

**证据与提交的关系**：证据是测试 → **与代码同一个提交**（测试红了就不该提交代码）；
证据是数据快照/仿真输出 → 与代码同提交并在 commit message 里引用 `research/` 的索引条目。
这样不会出现"证据说应该 X、代码还没改"的中间态。

### 验证

- 每条修复一份定向证据（仿真输出 / 穷举测试 / 前后对比）
- `#4` 必须有仿真：0/10/50% 丢包下 `sent == supplied` 且发满全程
- `#10` 必须穷举 1..6 的到达顺序，`(1,3,2)` 的重排计数为 1
- `#14` 必须仿真一次产品重启，CPU 不出现负数
- `#8` 必须在 LATLOAD 500 rps 下 `windowOverflow == 0`
- `scripts/selftest.sh` 绿

### 风险文件

`UdpReliability.cs`、`LaneEngine.cs`、`ResourceSampler.cs`、`DnsArm.cs`、`LossArm.cs`。

### 回退点

**每条修复一个提交**。改变数值的修复（#4/#7/#8/#10/#12/#15）与纯结构修复分开提交，
便于单独回退。

---

## E4：C# 分析器

### 步骤

1. **准备 oracle**（这里的工作量此前被低估，审核指出）
   - 升级 **`synthetic/make_tree.py`（1210 行，内嵌全部 harness schema）** 生成**新契约**的树。
     **它必须算进 Python 侧改动清单**——它和 `analyze.py` 一样住在结果目录里，只是没人抱怨过它
   - **最小改动 Python 参考实现**：只替换字段名/路径（`dig(record, "metrics/sent")` → 新名），
     统计/判定/渲染逻辑一行不动。**改动清单必须落盘**（哪些 `dig` 路径、哪些 key 常量），
     否则"只改字段名"这个前提无法被检验
   - **把生成结果冻结成数据**（`verification/synthetic-tree.tar` 或 golden JSONL），oracle 只对冻结
     数据跑——否则"两侧跑的是同一棵树"依赖于生成器在那次对比中没被动过，而 E4 恰恰要改这个生成器
     （自指风险）。生成器改动时显式重新冻结并重新批准 `A`
   - **处理 `verdict.json` 的 `generated_by`**（`analyze.py:5512` 硬编码
     `benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py`）。AC9 要求逐字一致，
     而 V3 要删除那个文件。**二选一，写进计划**：(a) C# 版原样输出该字符串，README 注明它是历史标签；
     或 (b) 两侧一起改成中性值，**在同一提交里重新冻结 `A`**

2. **建 `WinForward.E2E.Analysis` 项目**，引用 `Contracts`
   - 命名空间 `WinForward.E2E.Analysis`；加进 `WinForward.slnx` 的 `/benchmarks/` 文件夹
   - 按 design §1 的目录分模块（`Loading/` `Model/` `Stats/` `Checks/` `Metrics/` `Findings/` `Tables/` `Verdict/`）
   - 每个文件 ≤400 有效行
   - CLI 参数与 Python 版一致（`--raw` `--out` `--ledger` `--flat` `--warmup-seconds` `--resamples` `--seed`）
   - 产出 `tables.md` + `verdict.json`；`plots/` 输出 `SKIPPED.md` 等价声明
   - **预算 analyzer 告警面**：新项目会吃到 `Directory.Build.props` 的全套 4 个 analyzer 包 +
     `TreatWarningsAsErrors`，一个 6000 行 Python 的重写会大量触发长方法/认知复杂度/`ConfigureAwait`
     一类规则。按 `AGENTS.md` 的抑制策略逐条窄化并附理由，这是**独立的工作量**，不是"顺手过门禁"

3. **分 5 批对比**。**判据是"该批负责的表格小节"，不是整个文件**——`tables.md` 与 `verdict.json`
   都是单文件，只要还有一张表没实现，整文件 `diff` 就非空，"每批 diff 为空"永远不成立（审核指出）。

   | 批次 | 内容 | 判据 |
   |---|---|---|
   | 1 | `Loading` + `Model` + `Stats` + 环境/可用性表 | 这两节的**分段 diff** 为空；未实现小节在输出里显式写 `TODO` 占位 |
   | 2 | 不变量校验 + findings 分级 + gates 表 | 同上 |
   | 3 | **headline / latency / udp / dns（四张核心表）** | 同上（此批完成即覆盖绝大多数结论） |
   | 4 | cpu / memory / persist / tcp | 同上 |
   | 5 | dual / control / ledger / verdict.json | **全量 `diff` 为空 = AC9 达成** |

   实现上建议给 C# 版加 `--tables headline,latency,udp,dns` 只渲染该批的表，让 `diff` 只比对应切片。

4. **公平性 3 条**（§5.2）与 `#11` 的 `undecodable` 披露在对应表格里落实。
   **落点归 E4，不写进 `Contracts`**：`RowProfile` 承载的是产品级事实（"Proxifier 的 UDP 是泄漏的"、
   "WinForward 的 DNS 走直连"），那是分析器知识，不是记录契约。E3 只负责在记录里补齐支撑它的
   事实（target 侧的 UDP 源端点归属、DNS 路径标签），并给出字段名与写入位置。

### 验证

```bash
# 造树（冻结后的数据，不是现跑生成器）
python3 benchmarks/results/2026-10-06-e2e-competitors/analysis/synthetic/make_tree.py /tmp/wf-synth/raw
# 两套实现各跑一遍
python3 .../analyze.py --raw /tmp/wf-synth/raw --out /tmp/wf-synth/py
dotnet run --project benchmarks/WinForward.E2E.Analysis -c Release -- --raw /tmp/wf-synth/raw --out /tmp/wf-synth/cs
# 批次 1–4：分段 diff（按小节切）；批次 5：全量 diff
diff /tmp/wf-synth/py/tables.md      /tmp/wf-synth/cs/tables.md
diff /tmp/wf-synth/py/verdict.json   /tmp/wf-synth/cs/verdict.json
```

**批次 5 的两条 `diff` 为空是 AC9 的判据。**

### 风险文件

整个新项目；`synthetic/make_tree.py`；以及 `publish-campaign.sh` 的 analyze 调用行。

**关于 `publish-campaign.sh`**（审核指出）：它是 **gitignored 的机器专有脚本**，改它等于交付一处
不可 review、不可回退、不在 git 历史里的改动。**约定**：把 analyze 的调用做成一个**已跟踪**的薄封装
`scripts/analyze.sh`（内部按新工具链调用），机器专有脚本只调用那一层；`publish-campaign.sh` 本身
明确列为"仅本机、不入库、不算交付物"。顺带：它今天用
`nix-shell -p python3.withPackages(matplotlib)` 包住调用是为了让绘图生效，换成 C# 后这层包装整个
失去意义，应一并简化。

### 回退点

Python 版在批次 5 通过前**不删**（V3 已定：oracle 通过后立即删除，依赖 git 历史保留）。
**顺带决定 `make_tree.py` 的归属**：它同样住在结果目录里，E4 完成时一并决定搬到 `tests/` 还是随
`analyze.py` 一起删。

---

## E5：文档与验收

### 步骤

1. `benchmarks/WinForward.E2E/README.md`
   - 契约表（`:303-353`）逐项改为新字段名，并注明"定义在 `WinForward.E2E.Contracts/ArmKeys.cs`"
   - `Non-obvious properties`（`:490-534`）逐条复核是否仍成立（§5 的修复会改变若干条）
   - `Verification`（`:536-560`）更新为新字段名与新 gate
   - `Layout` 表加入新项目
   - `Wire/` 的规格表（`:412-420`、`:434-437`）加交叉引用到 `FrameCodec.cs`/`TcpCommand.cs`
2. 分析器 README：命令行、输入布局、`#17/#18/#19` 的口径披露，外加 **`harness-audit.md` §四 7 条**：
   - `tcp-connect` 的样本总体**排除了连接失败的请求** —— 这一栏可能反转连接排序
   - `tcp-connect` 与 `meanConnectMs` 是**两个统计量**，绝不能并列引用
   - MIX 的 `dns-rtt` 用 `Socket.Available` + `Task.Delay(1)` 轮询测的（Windows 上可加 15.6 ms）
   - 直方图在 **17.18 秒处饱和**，更长的挂起无法区分
   - 账本**没有归属信息、也没有任何代码读它**
   - 分析器声明的 5 秒预热**只作用于内存，没有作用于 CPU**
   - 内存泄漏斜率的拟合序列按**固定臂序**而非实际运行序拼接

   前两条与最后一条能让一个看起来成立的结论直接翻掉，务必写清。
3. **Windows 轻量验证**。目标：VM 上按 `AGENTS.local.md` §5 已部署的 `wf-aot` + sing-box，
   Windows client → 经透明代理 → Linux target，用 `plans-short/` 的短 plan 跑几分钟；
   结果拉回本地后用 C# 分析器跑一遍。

   **判据必须可机械判定**（原稿只说"跑通"，没说怎么算跑通）：
   - client 退出码 **0**
   - 记录里**无 `error` 记录**，`run.json` 的 `failed == false`
   - 分析器 verdict 的 `findings_by_severity["correctness-failure"] == 0`
   - 允许出现的 findings 白名单（自测固有，见 harness README:556-560）：DNS 端口声明那条、
     "平树没有控制块"那条
   - **明确不做的事**：`plans-short/` 不只是"更短"，它同时改了负载形状（`full-plan` 的 LOSS 从
     `120s × 500/s` 变成 `24s × 250/s`，DNS 从 200 降到 100 rps，PERSIST 的 `idleSeconds` 从 25 降到 6）
     → **#4（窗口释放）与 #8（LATLOAD 500 rps 的 `windowOverflow`）在短 plan 下验不到**。
     二选一：(a) 为 Windows 验证单独做一份"**保留原负载形状、只压缩时长**"的 plan；或
     (b) 明确声明这两条不在 Windows 验证范围内、只由 E3 的单元测试覆盖
   - **环境依赖已知**：这条验收依赖 `scripts/wf.sh` 与 `AGENTS.local.md`，**两者都是 gitignored 的
     机器专有资产**（`.gitignore:4` 与根 `.gitignore:18`），在一台新机器上无法复现。
     按"有环境则跑、无环境则记 blocked"处理，不假装它是可复现的门禁

4. **已知债务登记**（审核发现，不在本任务范围内但不能当作不存在）
   - `benchmarks/WinForward.Benchmarks/` 有 **3 个既有的规范违反**：
     `Perf/SessionSetupDecompositionBenchmarks.cs`（909 有效行）、
     `Stability/GcSoakScenario.cs`（684）、`Stability/UdpChurnScenario.cs`（410）。
     它们与 E2E 无关，登记到 README 或另开任务
   - `harness-audit.md` §三的 **20/21/22** 三条（THRU 臂被自身上限卡住 → 四行字节级完全相同、
     防火墙被关闭但从未记录、每行只采样一个进程名）**不在 R6 的"公平性 3 条"里，也不在任何子任务
     范围内**。其中"THRU 上限之上无区分度"会让吞吐表看起来像"四行等速"——至少要在分析器 README
     里如实披露

5. 全量门禁（见下）

### 验证

```bash
dotnet build WinForward.slnx -c Release                                  # 零警告
dotnet test  WinForward.slnx -c Release                                  # 绿
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # 退出 0 且输出为空
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # 零 <Issue>
cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json
```

---

## 全任务的总验收

| 验收项 | 命令 / 判据 |
|---|---|
| AC1 文件行数 | 扫描脚本（**收窄到三个项目**）无输出 |
| AC2–AC4 契约 | `rg 'Dictionary<string, object\?>'` 在三项目内无命中；形状测试绿；`ArmKeys` 是唯一定义点 |
| AC5 语义 13 条 | `research/semantic-fixes/` 每条一份证据 |
| AC6 公平性 3 条 | 分析器表格与脚注 |
| AC7–AC8 新发现与矛盾语义 | 测试 + README |
| AC9 分析器 oracle | 两次 `diff` 为空 |
| AC10 分析器门禁 | 零警告 + 单条命令运行 |
| AC11 测试 | `dotnet test -c Release` 绿 |
| AC12 selftest | 绿，退出码 0，无 `error` 记录 |
| AC13 门禁 | 三条命令全过 |
| AC14 文档 | 契约表与代码逐项对应 |
| AC15 Tier 0 止血 | **逐条独立判据**（见 E1 步骤 0 的表）：D7 用单测（`MarkSent(MaxSequence+1)` 不抛 + `OutOfRange == 1`）；D7 可诊断化与 D1/D3/D4/D5/D6 用 exit 2；D1/D2 用顶层兜底；`selftest.sh` 用非零退出；`Ratio` 用"证明无绕过" |
| AC16 记账语义 | D7 越界槽的记账规则有证据；`supplied` 与 `sent` 的差值变化可解释 |
| AC17 口径披露 | §四 7 条全部落进分析器 README 或表格脚注 |
| AC18 Windows 验证 | 经 `wf-aot` + sing-box 的短链路：client exit 0、无 `error` 记录、`run.json.failed == false`、findings 白名单外为空；C# 分析器能分析该结果。**依赖 gitignored 的 `wf.sh`/`AGENTS.local.md`，无环境则记 blocked** |

## `task.py start` 前的检查

- [x] `prd.md` 已过收敛（无临时小节、无重复事实、锚点未丢）
- [x] `design.md`、`implement.md` 已就绪
- [x] 5 个子任务已建好并互相挂接
- [x] 三个待决问题已全部解决：V1 验证深度（Linux 自测 + Windows 轻量经产品链路）、
      V2 绘图（不做）、V3 Python 版退休（oracle 通过后立删）
- [x] 15 项设计决策已固化进 `prd.md` / `design.md`
- [ ] **用户 review 通过并明确批准开工** ← 唯一未勾选
