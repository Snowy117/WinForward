# WinForward.E2E 重构与语义修复

> **开工前裁定（2026-10-07，设计稿审查后）**：本文件的部分事实与验收已被
> `design-decisions.md` 修订（最重要：§二 13 条里约 10 条**已在代码树里实现**，AC5 的判据随之改写；
> 契约测试、传输接缝、E4 oracle 机制均有修订）。**冲突处以 `design-decisions.md` 为准**，施工前必读。

## 目标

把 `benchmarks/WinForward.E2E/` 从「从未完整跑通过、代码结构失控、测量语义有已知缺陷」的状态，
重建为**能产出可信数据的、可维护的测量系统**。

用户已确认的四个决策：

1. **彻底重构**（C 档）：文件拆分、传输接缝抽取、结果模型类型化、JSON 契约重新设计。
2. **不保留任何向后兼容**：harness 从未完整运行过任何一次，没有历史数据要保护，契约可以自由
   重新设计而不是迁就旧拼写。
3. **`analyze.py` 换成 C# 项目**，与 harness 共享契约类型；绘图后置。
4. **`harness-audit.md` 第二节全部 13 条语义缺陷一并修复**，外加公平性 3 条（合计 16 条）与代码审查新发现的
   3 条。

## 背景

### 这是什么

一个跨平台 .NET 控制台程序，同一个二进制分 `client` 与 `target` 两个动词：client 跑在被测产品
所在的 Windows 机器上，经透明代理向 Linux 上的 target 发合成流量；target 回显并记账。产物是
每臂一份 JSONL 记录，由分析器聚合成 campaign 报告。

### 为什么现在动手

`harness-audit.md`（五路独立审查，全部结论用真实数据复核过）记录了 20 余条测量语义缺陷，其中
13 条被作者列为「**跑批前必须修**——会让数字失去意义」。该文档同时指出这套 harness **从未完整
运行过一次实测**。因此：

- 旧记录没有分析价值，契约可以自由重设计；
- 现在修语义的成本最低——以后有了真数据，改语义意味着整轮 campaign 报废；
- 「重构代码结构」与「修正测量语义」必须同时做，否则重跑出来的数据仍然不可信。

### 已确认的事实（均经代码复核，附 `文件:行号`）

**结构问题**

| # | 事实 | 证据 |
|---|---|---|
| F1 | **7 个文件违反本仓库「每 .cs 有效行 ≤ 400」规范**。该规范在 `.trellis/spec/backend/directory-structure.md:79-85` 明确声明「benchmarks/ 同样受此约束」 | `LatencyArm.cs` 792、`MixArm.cs` 669、`ReliabilityArm.cs` 605、`DnsArm.cs` 487、`ResourceSampler.cs` 465、`PersistentArm.cs` 440、`ClientRunner.cs` 439（有效行 = 非空非注释） |
| F2 | **TCP/UDP 双路径逐行镜像**。`LatencyArm` 的发送三件套共 113 行，归一化 diff 后只差 5 行（4 处形参透传 + `pending.Enqueue(x)` vs `pending[seq]=x`）；两个 `GraceDrainAsync` overload 归一化后完全相同 | `LatencyArm.cs:442/673`、`:487/716`、`:519/744`、`:836/852` |
| F3 | **UDP 回复校验阶梯被手抄 3 遍**（`TryDecode` → `ConnectionId` → `Filler` → `WasSent` → 记账），各写各的统计。**第 4 处不是同一套**：`DnsArm.cs:315` 用 `DnsWire.TryParseResponse` 并以 transaction id 为键，不碰 `FrameCodec`/`Filler`/`ConnectionId` | `LatencyArm.cs:776`、`MixArm.cs:717`、`LossArm.cs:218`（三份同源）；`DnsArm.cs:315`（另一套，不并入 `ReplyClassifier`） |
| F4 | **结果是弱类型字典**。字段名是字符串字面量，改名不被编译器检查；`BaseArm` 必须把值从字典读回来重新猜类型才能做算术，键名写错静默发布 0 | `ArmContext.cs:122-131`；`BaseArm.cs:49-56`、`:99-117` |
| F5 | **E2E 项目零单元测试**。全仓 13 个测试项目无一引用它 | `WinForward.slnx`；`rg 'WinForward\.E2E' tests/` 无结果 |
| F6 | **JSON key 就是契约**，同一统计量有 4 种拼写；`analyze.py` 按 `/` 走路径，改名不报错、只让格子静默变成 `n/a` | `README.md:303-353`；`analyze.py:292` |
| F9 | **`analyze.py` 是 6053 行的单一 Python 文件**（145 个顶层 def/class），且住在某一次 campaign 的结果目录里 | `benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py` |
| F10 | **Python 侧不受任何自动化质量约束**：无 `pyproject.toml`、无 ruff/black/mypy 配置、`.editorconfig` 无 `[*.py]` 段、CI 只跑 .NET | 全仓检索为空 |

**语义缺陷（`harness-audit.md` 第二节，编号 4–16 共 13 条）**

| # | 缺陷 | 影响 |
|---|---|---|
| 4 | LOSS 臂的在途窗口对丢失的数据报**永不释放** | 丢包超 6.8% 的产品在第 16～80 秒**完全停发**；`sent` 跨行差 7 倍 |
| 5 | `Classify` 按 `1..SentOk` 遍历而非实际发送集合 | 从未发出的数据报被发布为 `never`，抬高 `lossRate` |
| 6 | LOSS 公布的 W 恒为 200 ms 下限，注释却声称来自 5×p99 | 注释与分析脚本说明都是错的 |
| 7 | LOSS 从**实际发送时刻**起算而非预定时刻 | 客户端自身阻塞被从样本年龄里减掉，`lossRate` 被低估 |
| 8 | LAT/LATLOAD 的在途窗口会**删掉**慢样本 | 窗口 64、500 rps 时可测延迟上限仅 128 ms |
| 9 | TCP 延迟通道没有 drain（UDP 有） | 臂末尾在途的一批请求被从直方图丢掉 |
| 10 | `reordered` 是死代码（1956 种到达顺序穷举验证恒为 0） | RFC 4737 要求的重排指标根本没被测量，却打印统计上界 |
| 11 | 另三个结构性恒零计数器（`unmatchedReplies` 无调用点、`abandonedAtTeardown` 在 LOSS 不用、`corruptRate` 只能看到回程损坏） | 「测不到」被呈现成「测到了零」 |
| 12 | 四个臂硬编码 `clientSendLoss = 0`：`IdleArm.cs:20`、`DnsArm.cs:92`、`ThroughputArm.cs:119`、`ReliabilityArm.cs:165` | 永远不可能失败的校验位。**注意**：`LatencyArm`/`MixArm`/`LossArm`/`PersistentArm` 已由各自计数器派生，不要动（参照实现 `LossArm.cs:43`） |
| 13 | ~~分母为 0 时 `Ratio` 返回 0~~ —— **前提不成立，已核实**：`JsonValue.Ratio` 自首个提交 `59c3a09` 起就是 `denominator == 0 ? null`，README:276 与 `:281-283` 已把它写进契约，`client-infrastructure-code-quality-audit.md` §11 第 5 条把它列为「不要动」 | **没有 bug 可修**。本条重新定义为：把 `Ratio` 搬进 `Contracts` 作为唯一下口，并加一条**禁止裸 `(double)a / b`** 的规则检查，防止将来绕过。真正的证据是「证明不存在绕过 `Ratio` 的比率计算」 |
| 14 | 资源采样没有进程身份（PID / StartTime） | 产品重启会让 CPU 变成任意小甚至负数，**最不稳定的产品得到最低的 CPU** |
| 15 | DNS 的 TCP 响应按 FIFO 匹配，id 校验是同义反复 | 错配或重复的响应被记成 `answered` |
| 16 | BASE 控制组跑两倍时长、只覆盖两个臂、参数不来自 plan、且总跑在第一个 | 唯一能发现污染的行永远不跟在产品后面 |

**公平性（`harness-audit.md` 第三节，3 条）**

| # | 缺陷 | 影响 |
|---|---|---|
| 17 | Proxifier 的 UDP 配置为 `mode_bypass`（全部泄漏直连），却按产品成绩发布 | 它以「0% 丢包」赢得 UDP 精度对比，实际一条 UDP 流都没经代理 |
| 18 | WinForward 的 DNS 走 `localTarget` 直连，绕过 SOCKS5（4 份配置均为 2% TCP 份额真走代理） | DNS 延迟栏在混合两条不同路径，跨产品不可比 |
| 19 | CPU 只统计用户态，而五个产品的内核/用户划分差约 5 倍 | 这是真实架构差异，**绝不能当成用户态效率排名** |

**代码审查新发现（`arms-code-quality-audit.md`）**

| # | 事实 | 证据 |
|---|---|---|
| F7 | **真实数据竞态**：`UdpReliabilityTracker` 无并发契约（类注释只讲账目），而 `MixArm` 让发送循环与并发接收循环同时调它的 `MarkSent`/`MarkArrival`/`MarkCorrupt*`，全部裸 `++` 与非原子位图读改写。`LossArm` 单线程使用是安全的 | `UdpReliability.cs:39/199/229-233`；`MixArm.cs:661-667`、`:736-763` |
| F8 | **plan key 打错完全静默**：`PlanFile` 为每个 arm 解析全部 15 个 key，每个 arm 只读认识的几个 | `PlanFile.cs:267-284` |
| F11 | **至少 5 处 UDP `ConnectAsync` 在 try 之外**：一次 `SocketException` 让整个臂变成 `type:"error"`，而 TCP 侧同类失败只是计数器。容易漏掉的是 `MixArm.cs:457`（page DNS 的 UDP connect，`try` 从 `:461` 才开始） | `LatencyArm.cs:660`、`LossArm.cs:129`、`MixArm.cs:638`、`DnsArm.cs:220`、`MixArm.cs:457` |
| F12 | 同一异常四种策略：`ObjectDisposedException` 在 19 个 catch 里，16 处当良性 teardown、2 处当错误计数、1 处当**样本级 OtherError**（teardown 期间被切断的 REL 尝试被发布成产品的 `fidelityMismatch`） | `arms-code-quality-audit.md` §8.2 |
| F13 | `achievedRate` 在不同臂是两个统计量：PERSIST 的分母是「完成数」，其余五臂是「发出数」 | `PersistentArm.cs:208` |

**实测崩溃与静默数据损坏（`client-infrastructure-code-quality-audit.md`，全部在 Linux 上用仓库自带二进制复现）**

| # | 触发（只写 plan 或 CLI 就够） | 实测后果 |
|---|---|---|
| D7 | plan `{"kind":"loss","seconds":20,"ratePerSecond":20000}` | `IndexOutOfRangeException`，**exit 134**，`LOSS.jsonl` 只有 13 条 `sample`，**没有 `result`、没有 `run.json`**。触发条件 `ratePerSecond × seconds > 262143`；默认 500/s 时是 **525 秒**。根因：`MarkSent` 的边界检查只护住了位图，`_sendTicks[sequence]` / `_arrivalMilliseconds[sequence]` 两个数组写下标未受保护，而 `Ensure` 对越界序号早退 → 数组最大恰好 262144 |
| D3 | 臂名 `A/B` 与 `A_B` | `SanitizeFileName` **非单射** → 后者 `FileMode.Create` **截断**前者全部记录；`run.json` 里两臂指向同一文件、都 `failed:false`、**exit 0**。**静默数据丢失**，最危险的一条 |
| D2 | `--plan=`（显式空值） | 先**静默改用内置 8 臂默认 plan**（实测 `planHash = e49acbd4c9ad7284`），再崩在 `Path.GetFullPath("")`，留下 207 字节非法 `run.json` |
| D1 | `"dnsPort": 99999` | `ArgumentOutOfRangeException`，exit 134，臂文件 0 字节，无 `run.json` |
| D4 | 270 字符臂名 | `PathTooLongException`，exit 134（抛在 `try` 之前） |
| D5 | `"window": 100.5` | **静默**回落臂默认 64（发布 `inFlightWindow = 64`），exit 0，无 error 无 note |
| D6 | `--sampler-process=` | 记录里出现 `"process":"","absent":true` 的垃圾序列 |
| — | `scripts/selftest.sh` 无 plan 参数 | `exit 0`（`selftest.sh:32-37`）—— **任何人手工漏参数都会拿到 0 退出码**。注意：本仓 CI 只有 `analyzer-gate.yml` 与 `release-build.yml`，**没有任何一处调用 `selftest.sh`**，所以这不是 CI 问题而是手工调用问题 |

D1/D2/D4/D7 的**共同放大器**：`ClientRunner.cs:258-277` 只兜 4 种异常，`Program.cs:101-109` 顶层不兜任何异常 → 任何臂级失败都升级为「SIGABRT + 丢掉整份 `run.json`」。

### 与既有文档的关系

全部位于 `.trellis/tasks/10-06-e2e-competitor-benchmark/research/`：

| 文档 | 用途 | 在本任务中的地位 |
|---|---|---|
| `harness-audit.md` | 五路独立审查的测量**语义**缺陷清单（20 余条，全部用真实数据复核过） | **权威来源**：本任务修其 §二全部 13 条与 §三 3 条 |
| `arms-code-quality-audit.md` | `Client/Arms/`（4364 行）的代码**质量**审查：22 项按收益/风险排序的重构建议、11 类「刻意为之不要动」的清单 | **重构清单来源**；其 §9.11「输出契约字段名」与 §9.1「Notes 文本」在本任务中**不再是约束**（已确认不需要兼容） |
| `wire-target-code-quality-audit.md` | `Wire/` + `Target/`（2125 行）的审查：13 个文件逐行读完，另做三组**运行时探针实测** | **重构清单来源**；其 §11「不要动的地方」中线格式相关的第 1–6 条、第 11 条（AOT/trim）**完全有效** |
| `client-infrastructure-code-quality-audit.md`（在 `10-07-e2e-harness-refactor/research/`） | Client 基础设施（12 文件 2638 行）的审查：实测复现 7 条崩溃/静默数据损坏、纠正 AOT 前提、给出 oracle 比对脚本 | **重构清单来源**；其 Tier 0 清单是本任务的第一批工作，§11 的 15 条「不要动」全部有效 |

**三份代码审查建议合计 50+ 项。范围裁定（用户已确认）**：代码质量项**由结构重构整体覆盖**，
不逐项登记——结构重构的目标本身就是消除它们；但 **bug 类必须逐条显式登记**（它们不是"质量差"，
是"错的"），见 R7。

两份代码质量审查的第 10/12 节都给出「建议的重构顺序（收益/风险）」。本任务的
`design.md` §7 与 `implement.md` 的阶段划分**采纳其排序精神**（先纯搬移、再改数值），
但重组为按依赖关系排列的五个子任务。

两份审查都建议「动手前先建回归基线」。本任务采纳其精神但**改变期望**：基线用于
**发现意外变更**，而语义修复项会**故意**改变数值——所以基线不是"必须逐字不变"的判据，
而是"每一处变化都要能解释"。真正逐字不变的判据留给分析器的双向 oracle（`design.md` §6 层三）。

## 需求

### 结构

- **R1 文件拆分**：7 个超标文件沿自然接缝拆到有效行 ≤ 400，遵循 `directory-structure.md` 的拆分
  纪律（物理搬移 + 可见性调整，逻辑不动；不搞 pass-through 别名；先找自然接缝）。
- **R2 传输接缝**：抽出 TCP 与 UDP 共用的 lane 通道抽象，消除 F2 的镜像函数与 F3 的四份 UDP 收包
  循环。抽象不得在发包路径引入额外分配或虚调用开销。
- **R3 契约项目**：新建 `WinForward.E2E.Contracts`，承载记录模型、序列化与字段名定义，供 harness
  写出与 C# 分析器读入共享。字段名改一次，两侧同时编译期报错。
- **R4 契约重新设计**：消除 F6 的 4 种拼写与 F4 的弱类型字典；同一统计量只有一处定义。因为不保留
  兼容，可以按「利于跨臂比较」重新设计记录形状，而不是迁就现有拼写。

### 语义

- **R5 修复 §二 13 条**：逐条修复并各留可复核证据（见验收标准）。
- **R6 修复公平性 3 条**：在分析器侧落实（`not carried` 标注、DNS 路径标注、CPU 口径披露）。
- **R7 修复 17 条 bug 类发现**（代码质量项由结构重构覆盖，bug 必须逐条显式登记）：

  | 来源 | 条目 |
  |---|---|
  | Client 基础设施（Tier 0） | D7 序号越界崩溃 · D3 臂名碰撞静默截断 · D2 空 `--plan` 静默换 plan · D1 越界 `dnsPort` 崩溃 · D4 臂名过长崩溃 · D5 小数键静默回落 · D6 空 `--sampler-process` · `selftest.sh` 漏参数静默 exit 0 |
  | Client 基础设施（其他） | 顶层不兜异常（D1/D2/D4/D7 的共同放大器）· `JsonValue.Write` 的 `default:` 把未知类型静默写成字符串 · `PlanFile` 15 个数值键零校验 · `TryReadInt` 的 unchecked 强转 |
  | Arms | `UdpReliabilityTracker` 真实数据竞态 · `achievedRate` 两种口径 · `ObjectDisposedException` 四种策略 |
  | Wire/Target P0 | SO_REUSEPORT 静默窃取端口（实测 TCP 40/0、UDP 38/2）· `TcpCommand` 兜底造**重复 JSON key**（实测 `{"error":1,"error":2}`） |
  | Wire/Target P1 | `LedgerWriter` 的 catch 包住记录体（编程错误静默消失）· `FrameStreamReader` EOF 丢半帧被读成「干净 half-close」· `MaxPayloadLength` 只解码侧校验 · 端口冲突校验不全 |
  | 账本新增字段（原报告 §12 第 12 项，此前在子任务间**悬空**） | `detail`（Error 的原因：异常类型名）· `acceptErrors`（accept 循环的静默失败计数）· `udpReceivers`（生效的 UDP 接收并发度，写进 `targetSummary`）。三者**只新增不改名**，指派给 **E3** |
  | **记录，不修** | `ReliabilityArm.cs:602-606` 把**取消**写成 `ConnectFail`（在契约读集里）。两份审查结论冲突，且未构造出可达实验——登记到 `research/` 供后续裁决，本次不动 |

- **R13 Tier 0 止血（最先做）**：D1–D7 与 `selftest.sh` 的问题**先让它不崩、不静默**。
  必须**拆成两半**：
  - **止血半（E1）**：边界检查、顶层兜底、加载期校验。**D7 的守卫必须仍然调用
    `_sent.TrySet(sequence)`**（由它维护 `OutOfRange` 计数），否则"已声明的披露"
    `outOfRangeSequences` 会恒为 0，反而把一条披露信号关掉了
  - **记账半（E3）**：D7 修完后越界槽**既不在 `sent` 里也不在任何桶里**，`supplied` 与 `sent` 的
    差值变大，`gates.clientSendLoss` 的语义要跟着复核。
    **注意**：D7 的可诊断化（加载期校验 → exit 2）落地后，合法 plan 下客户端序号已被约束在
    `MaxSequence` 内，`outOfRangeSequences` 只能由网络侧触发——所以止血半的判据是**单测**
    （`MarkSent(MaxSequence + 1)` 不抛 + `OutOfRange == 1`），不是"跑一个越界 plan 看计数"
- **R14 口径披露**：`harness-audit.md` §四的 7 条「只需在报告里如实披露」纳入 E5，
  写进分析器 README 与相关表格脚注（详见「范围外」一节的修正说明）。
- **R8 统一矛盾语义**：`ObjectDisposedException` 的四种策略（F12）与 `achievedRate` 的两种口径
  （F13）各自收敛到一种，并在 README 写明。

### 分析器

- **R9 C# 分析器**：新建 `WinForward.E2E.Analysis`，取代 `analyze.py` 的核心（`tables.md` +
  `verdict.json`），引用 `Contracts`。绘图后置。
- **R10 分析器等价性**：C# 分析器对 `synthetic/make_tree.py` 造的树，产出与 Python 版
  **逐字一致**的 `tables.md` 与 `verdict.json`（这是重写的客观成功判据）。

### 测试与文档

- **R11 单元测试**：为不依赖网络与真实时钟的纯逻辑建测试项目，至少覆盖帧编解码、CRC32C、DNS
  报文编解码、延迟直方图、UDP 序列记账与分类、plan 解析与校验、契约序列化。
- **R12 文档同步**：更新 `benchmarks/WinForward.E2E/README.md`（含契约表、Non-obvious properties）
  与分析器 README，使文档与代码逐项对应。

## 验收标准

### 结构与契约

- [ ] **AC1** `benchmarks/WinForward.E2E/**/*.cs` 与 `WinForward.E2E.Contracts`、`WinForward.E2E.Analysis`
      中每个文件有效行（非空非注释）≤ 400。
- [ ] **AC2** 不存在以 `Dictionary<string, object?>` 承载 `parameters`/`metrics`/`gates` 的路径；
      `BaseArm`（或其新名字）中不再出现「从字典读回值再做算术」的辅助函数。
- [ ] **AC3** 同一统计量在全部臂中只有一种拼写；README 契约表与代码逐项对应。
- [ ] **AC4** 记录模型定义在 `WinForward.E2E.Contracts` 中；harness 与分析器均引用它；字段名定义
      在两侧共享。

### 语义

- [ ] **AC5** `harness-audit.md` §二 13 条逐条修复，每条附可复核证据（测试、仿真输出或数据对比），
      证据汇总落盘到本任务 `research/`。
- [ ] **AC6** 公平性 3 条（#17/#18/#19）在分析器侧落实：泄漏的 UDP 行标为 `not carried` 而非数字；
      DNS 路径差异在表中标注；CPU 口径在报告中显式披露。
- [ ] **AC7** `UdpReliabilityTracker` 有明确的并发契约（文档化 + 由并发测试证明）；plan 的未知
      key 会让 `TryLoad` 失败并指出 arm 与 key；UDP `ConnectAsync` 失败与 TCP 侧同策略。
- [ ] **AC8** `ObjectDisposedException` 与 `achievedRate` 各自只有一种语义，README 写明。
- [ ] **AC15（Tier 0 止血）** D1–D7 与 `selftest.sh` 的问题全部不再崩溃、不再静默。
      **每条一个独立判据**（不是笼统的"变成明确错误"）：
      - **D7 止血**判据是**单元测试**：`MarkSent(MaxSequence + 1, …)` 不抛、`OutOfRange == 1`、
        `SentOk` 不涨。**不能**用"跑 `20000/s × 20s` 的 plan 得到 `outOfRangeSequences > 0`"
        作判据——那条路会被 D7 的可诊断化（加载期校验 → exit 2）堵死，两条门禁互斥
      - **D7 可诊断化**判据是：同一 plan 变 load error（**退出码 2**）
      - **D3/D4/D5/D6** 判据：各自的最小 plan 变 load error（退出码 2）
      - **D1/D2** 判据：顶层兜底生效（有 `error` 记录 + `run.json` 的 `failed:true`），或解析期拒绝
      - **`selftest.sh`** 判据：漏 plan 参数时退出非零
      - **`Ratio`（原 #13）** 判据：证明不存在绕过 `Ratio` 的裸比率计算（**不是**"把 0 改成 null"——
        它自首个提交起就是 `null`，见语义缺陷表 #13 的修正说明）
- [ ] **AC16（记账语义）** D7 的越界槽在 `sent`/`supplied`/`clientSendLoss` 之间的记账规则已定
      并有证据；`supplied` 与 `sent` 的差值变化可解释。
- [ ] **AC17（口径披露）** `harness-audit.md` §四 7 条全部落进分析器 README 或表格脚注。

### 分析器

- [ ] **AC9** C# 分析器对 `synthetic/make_tree.py` 的树产出与 Python 版逐字一致的 `tables.md` 与
      `verdict.json`。
- [ ] **AC10** 分析器在 `dotnet build -c Release` 中零警告，且可由单条命令运行（无需 nix-shell
      与 python 环境）。

### 测试与门禁

- [ ] **AC11** 新增测试项目在 `dotnet test -c Release` 中全绿，覆盖 R11 列出的全部模块。
- [ ] **AC12** `scripts/selftest.sh` 端到端跑绿，client 退出码 0，无 `error` 记录。
- [ ] **AC13** `dotnet build WinForward.slnx -c Release` 零警告；
      `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` 退出 0 且输出为空；
      `jb inspectcode -f=Xml -e=HINT WinForward.slnx` 零 `<Issue>`。
- [ ] **AC14** README 的契约表、Non-obvious properties、Verification 三节与重构后的代码逐项一致。

## 范围外

- **绘图（`plots/`）**：`analyze.py` 的 320 行 matplotlib 段落后置。C# 分析器先输出
  `plots/SKIPPED.md` 或等价声明。绘图方案的选型（ScottPlot / 其它）不在本任务内。
- **不改 target 与 client 之间的线格式（wire format）**：两端是独立部署的二进制。
- **不改账本（ledger）格式**：除非语义修复要求（#14 采样是 client 侧，不涉及）。
- **不改 `src/` 下任何产品代码**。
- **不改 `scripts/orchestrator.ps1` 与机器专有脚本**，除非契约变更强制要求（`publish-campaign.sh`
  的 analyze 调用行需要改）。
- **~~不改 `harness-audit.md` §四「如实披露」的条目~~** —— **已修正**：7 条全部纳入 R14，
  写进 E5 的文档工作（分析器 README + 表格脚注）。它们的载体本就是报告与 README，而 E5 正在改
  这两个文件；其中 `tcp-connect` 的样本总体、两个统计量不可并列、内存斜率按固定臂序拼接三条
  能让一个看起来成立的结论直接翻掉。

## 已定的验证与退役决策

**V1 验证深度**：本任务的端到端验收分两级——

1. **Linux 自测**（每次改动的常规门禁）：`scripts/selftest.sh` 跑绿 + 单测绿 + 三条门禁过。
   client 与 target 是同一个跨平台二进制，整套 harness 能在单机上端到端跑完。
2. **Windows 轻量验证**（E5 总验收时做一次）：**经一条产品的短链路**——VM 上按 `AGENTS.local.md` §5
   已部署的 `wf-aot` + sing-box，Windows client → 经透明代理 → Linux target，用 `plans-short/`
   的短 plan 跑几分钟。

   选它而不是"跨机裸跑 selftest"的理由：裸跑只验证跨机连通，而经产品链路才验证
   **E3 的语义修复在真实透明代理下的行为**。产品已装好，成本主要是占用开发机几分钟。

**不需要**跑完整 campaign、不需要跑齐五个产品。带完整产品对比的正式 campaign 属于后续任务。

**V2 绘图**：本次**不画图**。C# 分析器输出降级声明（`plots/SKIPPED.md` 等价物）。
绘图库选型不在本任务内。

**V3 `analyze.py` 退休**：双向 oracle 通过后**立即删除**，依赖 git 历史保留。
