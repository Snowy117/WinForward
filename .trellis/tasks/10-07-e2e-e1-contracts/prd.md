# E1 契约与安全网

> **开工前裁定（2026-10-07）**：见父任务目录 `design-decisions.md`（D2 D5 D7 D10 D11 直接适用于本任务）。
> **冲突处以该文件为准**；本文件 0.1 的判据已就地修正为单测判据。

父任务：`10-07-e2e-harness-refactor`。本子任务的详细设计见
`../10-07-e2e-harness-refactor/design.md` §2，执行步骤见同目录 `implement.md` 的「E1」节。

本子任务展开的父需求：R3 / R4 / R11（部分）/ R13；AC1–AC4 / **AC7** / AC11 / AC15。
（步骤编号以 `implement.md` 为准——它含 0.8/0.9 与批次切分。）

## 目标

给这套从未有过单元测试、字段名散落 9000 行的 harness 建立**两层地基**：

1. **安全网**——新建 `tests/WinForward.E2E.Tests`，先把纯函数（CRC32C、Filler、FrameCodec、
   TcpCommand 名字唯一性、LedgerWriter 格式）锁进可执行约束；
2. **契约的单一定义点**——新建 `benchmarks/WinForward.E2E.Contracts`，把每个 JSON 字段名变成
   一个 `const string`，写出侧与读入侧都引用它。改名的动作从此是"改一处常量的值"，而不是
   "在 6000 行里找字符串"。

## 范围

### 第一批：Tier 0 止血（最先做，每条独立 commit）

让 D1–D7 与 `selftest.sh` 的问题**不再崩溃、不再静默**，把它们变成**明确的错误**或
**已声明的披露**。约 50 行改动 + 6 个最小 plan 作回归用例。

| # | 内容 | 门禁 |
|---|---|---|
| 0.1 | `MarkSent` 越界止血（D7）：边界检查提到方法开头，**守卫必须仍然调用 `_sent.TrySet(sequence)`** | **单元测试**：`MarkSent(MaxSequence + 1, …)` 不抛、`OutOfRange == 1`、`SentOk` 不涨、两个数组未被写。**不要**用"跑越界 plan 看 `outOfRangeSequences > 0`"作判据——它会被 0.2 堵死（两条门禁互斥，这是有意的） |
| 0.2 | 加载期校验 `ratePerSecond × seconds ≤ MaxSequence`，建立按 kind 的 `Validate()` 机制 | 同一 plan 变 load error（退出码 2）；与 0.1 同 commit |
| 0.3 | 顶层兜底：臂级异常 → `error` 记录 + `run.json` 的 `failed:true`；`Program.cs` 两个 verb 各加顶层 catch | D1/D4 的 plan：退出码 1、有 `error` 记录 |
| 0.4 | 空 `--plan`（D2）：**判定在 `ClientRunner.TryApply` 的 `--plan` 分支**（值空串 → usage error）；`PlanFile.cs:125` 改 `path is null`；`ClientRunner.cs:445` 保持 `is null`（DD D14.1） | `--plan=` 与 `--plan ""` → 退出码 2；**不带 `--plan`** → 正常跑内置默认 plan 且 `run.json.planPath == null` |
| 0.5 | 文件名单射与长度（D3/D4）：重名检查用**映射后**的名字 + 长度上限 | `A/B`+`A_B` 与 270 字臂名都变 load error |
| 0.6 | 范围校验 + `TryReadInt` 强转（D1/D5） | `dnsPort:99999` 与 `window:100.5` 都变 load error |
| 0.7 | 空 `--sampler-process` 拒绝（D6）；`selftest.sh` 漏 plan 参数时退出非零 | 单测 + 两条手工命令 |

**只做止血半**：D7 修完后越界槽在 `sent`/`supplied`/`clientSendLoss` 之间怎么记账属于 E3。
本批**不碰任何记录形状**。

### 第二批：安全网与契约

- 新建两个项目并加入 `WinForward.slnx`。
- `ArmKeys`：每臂字段名的唯一定义点。
- 每臂的强类型 metrics 值对象；harness 内部不再传 `Dictionary<string, object?>`。
- `Json/Rate.cs`：唯一比率函数，分母为 0 → `null`（修 `harness-audit.md` #13）。
- `Json/JsonlSink.cs`：`JsonlFile` 与 `LedgerWriter` 合一。
- **契约改名**：`design.md` §2.2.1 的最小改名映射：同一概念的多种拼写收敛为唯一叶子名，结构性分组保留。
- `ArmOutcome` 退役；`ControlArm`（原 `BaseArm`）直接持有子臂结果对象。

## 不在范围内

- 文件拆分与传输接缝（E2）。
- 语义修复（E3），**除了** #13（它属于契约的 `null` 约定）。
- 分析器（E4）。

## 依赖

无。本子任务是 E2/E4 的前置。

## 验收标准

**Tier 0（第一批）**

- [ ] D1–D7 与 `selftest.sh` 的问题全部不再崩溃、不再静默；每条附一个最小 plan 作回归用例。
- [ ] `ratePerSecond × seconds > MaxSequence` 的 plan 得到退出码 2 与可诊断的错误信息（错误含 arm/最高序号/
      `MaxSequence`），而不是 `IndexOutOfRangeException`。校验只对 `kind ∈ {loss,mix,base}` 生效（DD D14.4）。
- [ ] **AC7**：plan 的未知 key 让 `TryLoad` 失败并指出 arm 与 key（`ArmKind.Keys` 白名单，含文本键）。
- [ ] 臂名 `A/B` 与 `A_B` 不再互相截断（变 load error）；270 字臂名不再崩（sanitize 后上限 128，DD D14.23）。
- [ ] 顶层不再逃逸异常：任何臂级失败都留下 `error` 记录与 `run.json` 的 `failed:true`，
      **包括 sink 写入/释放故障**（DD D14.7）。

**安全网与契约（第二批）**

- [ ] `tests/WinForward.E2E.Tests` 建好并加入 `WinForward.slnx`；`InternalsVisibleTo` 就位；
      工程设了 `<IsTestProject>true</IsTestProject>`。
- [ ] `WinForward.E2E.Contracts` 建好并加入 `WinForward.slnx`；`WinForward.E2E` 引用它；类型为 `public`（DD D14.20）。
- [ ] `ArmKeys` 是 `result`/`armSummary` 顶层与 `metrics` 子树字段名的唯一定义点（作用域与 gate 见 DD D14.16）；
      `ResourceSampler` 与 `Target/**` 的字面量显式归 E2。
- [ ] 形状测试：读**生产写出的 JSONL** 的路径集合，与 `ArmKeys` 声明路径集合比对；
      显式工厂让"新增属性忘加常量/忘写出"在编译期或测试期变红；含 null 用例、条件字段两种 flags、数组 arity。
- [ ] `Dictionary<string, object?>` 仅允许出现在值对象内部构造嵌套块（DD D14.21）；
      `BaseArm.ReadCount`/`ReadMilliseconds` 一类"从字典读回再猜类型"的辅助函数消失。
- [ ] 契约改名的映射表落盘 `../10-07-e2e-harness-refactor/research/contract-rename.json`（全量，含 identical）与
      `contract-rename.md`（只列 changed）；对同一份 selftest 输出，新旧**路径**集合的差异恰好等于该表的 changed 子集。
- [ ] 改名取最小粒度：只消除同一概念的多种拼写，结构性嵌套（MIX 的 `classes.*`、BASE 的相位）保留。
- [ ] **原 #13 已重新定义**（前提不成立：`Ratio` 自首个提交起就是 `denominator == 0 ? null`，
      没有 bug 可修）：`Ratio`/`PerSecond` 搬进 `Contracts` 作为唯一下口（`ticks<=0 ⇒ null`），
      并有测试或规则检查**证明不存在绕过它们的裸比率/裸除法计算**。
- [ ] `dotnet build WinForward.slnx -c Release` 零警告；`dotnet test tests/WinForward.E2E.Tests -c Release` 绿；
      `dotnet format … --verify-no-changes` 空输出；`jb inspectcode …` 零 `<Issue>`。
- [ ] `scripts/selftest.sh scripts/plans/selftest-plan.json` 绿。

> **权威规则**：父 `prd.md` 的 R/AC 是本子任务的**验收上限**；本文件的清单是它的展开，
> 冲突时**以父为准**，且本文件每条验收都必须能追溯到父的一个 R 或 AC。
> 父任务：`.trellis/tasks/10-07-e2e-harness-refactor/`。
