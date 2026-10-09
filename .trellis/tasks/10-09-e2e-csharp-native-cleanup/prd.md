# E2E 三个项目：把 Python 时代的脚手架收干净

> **状态**：Phase 1.1 调研完成，等待 1.4 评审门。
> 证据：`research/00-rng-swap-spike.md`（实测）、`01`–`04`（四路调研，全部带 `文件:行号`）。
> 实施计划见 `design.md`（技术设计）与 `implement.md`（步骤与判据）。

## 目标

让 `benchmarks/WinForward.E2E`、`WinForward.E2E.Contracts`、`WinForward.E2E.Analysis`（含
`tests/WinForward.E2E.Tests` 与它们的脚本）**读起来像本仓库的 C# 项目**，而不是一个 Python
harness 的逐行转写：删掉只为模仿 Python 而存在的复杂度，把命名/结构拉回仓库既有风格，清掉
死脚本与错位的脚本，修掉文档与路径的矛盾。

**契约不动**（记录 key、表格结构、披露要求、CLI 面）；报告里由重采样产生的数字允许变化，
但必须在 oracle 里登记为"已放宽"，而不是悄悄放过。

## 依据

1. **D21（用户裁定，2026-10-08）**：`10-07-e2e-harness-refactor` 已判定"复刻 CPython 的 RNG、
   `%g` 形态与转义规则只增加复杂度、收益为零"，并把 oracle 放宽为语义比对；但 §3/§4 把删除
   **推迟**了（"已写好的代码的修改之后再说"）。本任务是那次收尾。
   原文：`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md:881-905`。
2. **用户本轮追加裁定**：不需要与 Python 对齐行为，项目只要能把 E2E 跑通就行 → RNG 直接用
   标准库最简明的 `System.Random`，不复刻。
3. **D22 的未闭项**：`research/tickets.md` 的 T3（orchestrator 吞退出码）、T4/T6（脚本与
   docstring 漂移）仍然 open；T5 是本机胶水的传输竞态。

## 调研结论（证据表）

### 一、Python 仿真：能删的比想的多，但风险分两类（`research/01`）

C# 侧约 900 行"为模仿 Python 而写"的代码里约 500 行可直接删；连同逐字一致的测试与三张向量表，
总删减约 **6 400 行**（其中 C# 约 700 行）。

| 类别 | 项 | 依据 |
|---|---|---|
| **免费删** | `PythonExponential.cs`（21 行，唯一调用者 `Tables/GateValidity.cs:263`）、`JsonValue.Truthy`（18 行，已有 `IsTrue`）、三份重复的 `Int(double)`（`GateFlow.cs:390`、`IdentityChecks.cs:343`、`FindingsCollector.cs:333`）、`TableLedger` 的 `sum([])` 分支、`NaturalKey` 的叙事 | 冻结树上不触发或只影响一格字形 |
| **换实现（需一次登记）** | `CpRandom`（249 行）→ `System.Random(seed)`，但 `StableHash`/`DeriveSeed` **必须留下**；`VerbatimNumber` 的 BigInteger 半偶机器（约 180 行，金标里半偶中点 0 命中）；`VerbatimJson` → `Utf8JsonWriter`（仅 `--mode byte` 变红） | `research/00` 实测 + `01` §2 E1–E3 |
| **必须留（只删叙事）** | `PosixPathText` 的词法规范化（`verdict.json.raw` 走 `text_identical`，`oracle-diff.py:973-977`；`Path.GetFullPath` 会解析 `..`）、`PythonGlob` 的三条发现规则（名为 `x.jsonl` 的**目录**算臂、隐藏名匹配、序数排序）、`DescriptiveStats.Sum` 的 Neumaier 补偿（§7 斜率靠它）、`MarkdownTable` 不校验格数、`AnalysisRunner.OpenOutput` 的 `\n`+无 BOM、`plots-SKIPPED.md` 字节 | `01` §5「不许动」清单 |
| **需裁定** | `RunLoader.ReferenceName` 的 CPython 异常名（`JSONDecodeError` 等，金标 0 命中，但真实 campaign 会印给人看）、`JsonText` 的 `None`/`True`/`False`（同样 0 命中） | `01` §6 |

### 二、RNG 实测（`research/00`，报告 01 附录 A 独立复算一致）

把 `BootstrapPair` 换成 `System.Random` 后：51 个 oracle 切片中 10 个变红，**75 条差异全是 p 值**
（`p_value` 58 / `p_equivalence` 10 / `holm_p_equivalence` 7），
**0 结构差异、0 verdict 翻转、0 表格变化、`ci95` 0 移动**（冻结树每行只有 3 个 pass）。
但报告 01 用不落盘的复算补测：15 pass 时 6 次试验里 4 次 `ci95` 端点会动 ⇒ 放宽规则应把
`ci95` 一并纳入，否则真实 campaign 上会红。

### 三、命名与惯例：用户点的"缩写"问题只有 3 个标识符（`research/02`）

从 `src/` 反推出的房规：两字母缩写全大写（`IPPrefix.cs:13`、`IPAddressValue`、
`SupportedOSPlatform`），≥3 字母 Pascal（`Tcp`/`Udp`/`Dns`/`Cpu`/`Json`），`Id` 不写 `ID`
（40 处 `StableId`）；`src/` 里**没有**任何 `*Io*` 托管标识符。逐一遍历全部 7 462 个标识符后，
真正越界的只有：`SocketIo`（`Target/SocketIo.cs:27`，7 处引用 + `README.md:53`）、
`LaneReceiveKind.IoError`（`Client/Lanes/LaneTransportContracts.cs:39`，16 处）、
`ArmKeys.Run.OsDescription`（`Contracts/ArmKeys.Run.cs:30`，3 处，**成员改名不影响输出**，
`check-readme-contract.py:204` 只用常量值）。

真正的可读性收益在别处：**21 个 `(value, reason)` 命名元组**应收成一个 `Reading<T>` 记录
（`src/` 里命名元组返回 **0** 个，`Analysis` 有 39 个）；`Findings/LedgerViews.cs` 385 有效行 4 个类型；
`Client/ArmContext.cs` 塞了 7 个无关类型（`README.md:37` 已经描述错）；`LedgerFindings` 的两个
partial 分片没按 `Type.Topic.cs` 命名；4 个真实未用 `using`。
负面结论同样重要：**无死成员、无 `#region`、无 TODO/FIXME、无注释掉的代码、无命名空间错位、
`effective-lines.py` 退出 0**；19 处 `catch (Exception)` 都带 `when` 过滤，与 `src/` 一致。

### 四、脚本：2 个死、3 类错位、5 处今天就坏（`research/03`）

- **死**（无代码调用者，只有文档行）：`scripts/contract-inventory.py`（E1–E3 迁移工具，其两张输出表
  仍是活输入）、`scripts/normalize-pattern-hits.py`（D16.2 的死模式检查）。加两个 `__pycache__`（8 个 `.pyc`，未跟踪）。
- **错位**：`effective-lines.py` 是**全解决方案**的 400 行门禁（`directory-structure.md:98,100,103`，
  还被 `benchmarks/README.md:433` 用来跑 `WinForward.Benchmarks`），却在 `WinForward.E2E/README.md:694`
  自称"harness 自己的三个门禁"之一；`oracle-diff.py` 与 `check-fairness.py` 的输入全是
  `Analysis/verification/**` + 分析器二进制（`measurement-tooling.md:127` 已经称 oracle-diff 是"外来的"）。
- **今天就坏**：`check-readme-contract.py:47` 指向已归档任务目录（**实测 exit 2**）；
  `check-fixture-drift.py:38-40` 同一路径（**实测 exit 1 且抛 traceback**，违反三态退出码约定，应为 2）；
  `publish-campaign.sh:8,36`（`cd` 进已删除的 `analysis/`）与 `:28`（找 `target-ledger.jsonl`，
  而启动器现在写 `ledger-main.jsonl`/`ledger-direct.jsonl` → 分析器静默拿不到账本）；
  `orchestrator.ps1:64,358,382`（T3：`[void](Invoke-Client …)` 吞掉退出码日志）；
  `make_tree.py:15-16`（T6：docstring 承诺每 pass 一份账本，生成器不写）。
- **澄清**：五个本机脚本的跟踪状态与 README/`.gitignore` 的说法**完全一致**（4 个 gitignored、
  `orchestrator.ps1` 已跟踪），没有矛盾；但 `.gitignore` 给的理由（"只描述一对机器"）站不住——
  被跟踪的 `orchestrator.ps1` 才是机器耦合最重的那个。
- **没有任何 CI 跑这些脚本**：`.github/workflows/` 只有 `analyzer-gate.yml`（`dotnet format` +
  `jb inspectcode`）与 `release-build.yml`，`rg -i 'e2e|benchmark' .github/workflows/` 无命中。

### 五、脚手架与文档（`research/04`）

- **无属性缺口**：四个 E2E 项目都继承了根 `Directory.Build.props`（nullable/TWAE/分析器齐全）。
- **只有 E2E 测试项目没有分析器**：`tests/WinForward.E2E.Tests.csproj:4` 的
  `<IsTestProject>true</IsTestProject>` 在 **restore 期**可见（此时 xunit 的 props 因
  `ExcludeRestorePackageImports` 不参与），于是四个分析器包一个都不解析。实测：
  其余 14 个测试项目 `analyzers=4`，只有它 `analyzers=0`；`-p:IsTestProject=false` 立刻变成 4 个。
  （报告 02 说这行"冗余"——在 build 求值下确实看不出差别，但 restore 期的差别是真的。）
- **plans/configs**：`plans-windows/full-shape-plan.json` = `plans/full-plan.json` 只覆盖 9 个
  `seconds` 叶子；但 `PlanFile.cs` **没有**覆盖/合并机制，去重需要新增 loader 行为而不是改数据。
  6 个 configs 没有任何名字引用（只有 gitignored 的 `deploy-campaign.sh:43-49` 映射它们），
  `wf-fdd-opt.json` 与 `wf-aot-opt.json` 逐字节相同，README 暗示的 `bench.ppx` 不在仓库里。
- **verification/**：12 个 FROZEN.md 哈希复测全部精确；`py-tables.md`/`py-verdict.json` 只有 oracle 用；
  三张向量表是测试硬依赖（无 skip 逻辑）→ 属于 C1 的删除集。
- **文档**：三份 README 没有文本级重复（共享行 0–2），重复是主题性的（7 个主题各讲两遍）。
  **只有** `check-readme-contract.py` 读 `WinForward.E2E/README.md`，只读 `:372-447`，
  且用字面标题 + 下一个 `## ` 定界 → 改动该区间或移动它都会**静默缩小覆盖**；
  `measurement-tooling.md:9` 还按**行号**引用"line 372"。
  16 条陈旧声明已逐条列出（如 `README.md:72`"28 CLI commands" vs `cli-snapshots.py:42-76` 的 31；
  `quality-guidelines.md:125` 把 `analyzer-gate.yml` 说成 build/test 的所在地，而该 workflow 两者都不跑）。

## 需求

| # | 需求 | 来源 |
|---|---|---|
| R1 | 删掉零收益的 Python 仿真并换成 .NET 常规写法（含 `CpRandom`→`System.Random`），同时保留"实现即契约"的那一类**行为不变** | C1 · `01` |
| R2 | 把 oracle 对**重采样派生数字**的比对改成已登记的窄放宽，并保证 verdict 字符串、表格、结构仍逐字/逐格比对 | C1 · `00`+`01` |
| R3 | 命名与结构回归房规：**两字母缩写全大写（`IO`/`IP`/`OS`，`Id` 例外）、≥3 字母视作单词（`Html`/`Json`）、`IPv4`/`IPv6` 的 `v` 小写**；`Reading<T>` 收编 21 个元组、4 处文件拆分/改名、4 个未用 using | C2 · `02` |
| R4 | 脚本收敛：删 2 个死脚本与 `__pycache__`、把全解决方案级门禁移出本项目、修 5 处坏路径、把"谁跑什么"写准 | C3 · `03` |
| R5 | 文档与数据收敛：修 16 条陈旧声明、plans/configs 的重复与缺失如实记录、`AGENTS.local.md` 与实物一致 | C4 · `04` |
| R6 | 新账：E2E 测试项目为何没有分析器、要不要打开，必须落成决定（打开或写明豁免理由） | C2/C4 · `04` |

## 验收标准

| # | 判据 | 验证 |
|---|---|---|
| AC1 | `research/01` §4 的 S0–S6 每步完成后，全批次 oracle 语义比对 rc=0（放宽项除外，且放宽项在 diff 报告里可见） | `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py`（默认 semantic，全批次） |
| AC2 | `--mode byte` 的变红范围不超出已登记项（`VerbatimJson`/`VerbatimNumber` 换实现导致的结构面差异） | `python3 .../oracle-diff.py --mode byte` 全批次，与 `design.md` 的登记表逐条对照 |
| AC3 | 三个项目里不再有"为模仿 Python"的代码路径；金标里 0 命中的 `ReferenceName`/`JsonText` 分支已按裁定处置 | `rg -n 'cpython|CPython' benchmarks/WinForward.E2E*` 只剩 `verification/FROZEN.md` 的叙述；`rg` 清单逐条勾掉 |
| AC4 | 删减量达到 `research/01` §3 的量级（C# ≥500 行、冻结物/测试 ≥5 000 行），且 `FROZEN.md` 的哈希表与实物一致 | `wc -l` 对比；`FROZEN.md` 12 个哈希复测（`research/04` §5 的方法） |
| AC5 | 命名清单清零：3 个缩写、21 个元组、4 处拆分/改名、未用 using；`Reading<T>` 落地且 `src/` 风格一致 | `research/02` 的 S0–S6 清单逐条勾掉 + `dotnet build -c Release` 零警告 |
| AC6 | 脚本：2 个删除、3 类迁移完成、5 处坏路径修好并复跑 rc=0（`check-readme-contract.py`、`check-fixture-drift.py` 的退出码语义正确：读不到输入必须 rc=2） | 每个脚本按其文档跑一次，退出码记进证据 |
| AC7 | 三份 README 的 16 条陈旧声明逐条修正；README 契约表区间（`:372-447`）的覆盖不缩小；`measurement-tooling.md` 的行号引用同步 | `check-readme-contract.py` rc=0（111 key 的实测输出）+ 16 条逐条勾掉 |
| AC8 | 全套门禁绿：build 零警告、`dotnet test -c Release` 绿、`dotnet format --severity info --verify-no-changes` 空输出、`jb inspectcode` 0 issue、`effective-lines.py` 四路径无输出、`check-fairness.py` 全 PASS、`check-boundary-trees.py` 绿、`selftest.sh` rc=0 | 逐条命令，输出留证 |
| AC9 | 无契约漂移：`ArmKeys` 值无 diff、`CliSnapshotTests`/`ContractShapeTests`/`JsonKeyLiteralGateTests` 全绿、`tables.md` 16 节与 `verdict.json` 14 key 不变 | `git diff` + 上述测试 |

## 任务地图（父 + 4 子）

四个交付物可独立验收、可独立归档；顺序 = 依赖顺序。

| # | 子任务 | 依赖 | 一句话 | 主要 AC |
|---|---|---|---|---|
| C1 | `e2e-analysis-native-rng-and-format` | — | Python 仿真收尾：免费项先删、RNG 换 `System.Random`、格式化换 .NET、oracle 窄放宽登记、删三张向量表与逐字测试 | AC1–AC4 |
| C2 | `e2e-naming-and-idiom-pass` | C1 | 命名与结构：3 缩写、`Reading<T>`、4 处拆分/改名、未用 using、迁移vocabulary | AC5 |
| C3 | `e2e-scripts-triage` | —（可与 C1 并行） | 脚本：删 2 个、迁 3 类、修 5 处、把"谁跑什么"写准 | AC6 |
| C4 | `e2e-docs-and-scaffolding` | C1 C2 C3 | 文档与数据：16 条陈旧声明、plans/configs 如实记录、`AGENTS.local.md`、分析器覆盖决定 | AC7 + R6 |

## 裁定记录

| # | 问题 | 裁定（2026-10-09） |
|---|---|---|
| D1 | oracle 窄放宽的范围 | ✅ **p 值与 `ci95` 一并纳入**（冻结树测不出，真实 campaign 15 pass 时 4/6 会动） |
| D2 | 脚本搬家 | ✅ 照办：`effective-lines.py` → 新建根级 `tools/`；`oracle-diff.py` + `check-fairness.py` + 两张契约表 → `Analysis/verification/` |
| D3 | 4 个 gitignored 本机脚本 | ✅ 就地修 + README 写明是本机胶水，不入库 |
| D4 | `IsTestProject` 的处置 | ✅ 先 spike（删掉看 4 个分析器在 ~90 个测试文件上出多少条），少就修、多就写明豁免理由 |
| D5 | CI 缺口 | ✅ 另立任务，不塞进本次 |
| D6 | `E8` 的 CPython 异常名 / `JsonText` 的 `None`/`True`/`False` | ✅ 改成 .NET 名 |
| D7 | oracle 的定位：保留 / 退役 / 接 CI | ✅ **保留 + 修剪 + 接进 `analyzer-gate.yml`**（用户 2026-10-09："可以"）。不采用"用 C# 侧重冻金标"——那会把它降级成变更探测器 |

### D7 · oracle 的定位

`oracle-diff.py` + 冻结的 Python 产物目前是分析器**唯一覆盖"整份报告输出面"的回归网**
（16 张表 + 14 个顶层 key，端到端跑完 loading→metrics→bootstrap→findings→tables）。
本次要换 RNG、换 JSON 写手、换数字格式化，它正是"只动了 p 值"这句话的证据来源；
同时 C1 会删掉那三张向量表与逐字一致的断言，**它的重要性只增不减**。

它现在的弱点有三条，都不是"该退役"的理由，而是"该修"的理由：

1. **没有任何 CI/测试跑它**（`research/03` §0）：全仓库没有 workflow 碰 E2E。它跑一次只要 **4.6 秒**
   （本次实测），接进 `analyzer-gate.yml` 的成本极低。
2. **背着一半不属于它的行李**：三张向量表是单测输入、不是 oracle 输入；`--mode byte` 不是批次判据
   （D21 §2）。这些在 C1 里删掉/降级。
3. **金标无法就地再生**：参考实现 `analyze.py` 已删除，重冻要按 `FROZEN.md:2.1` 从 git 历史恢复。
   这是"独立答案键"的代价，也是它的价值来源（两侧是独立写成的实现）。

**建议**：保留 + 修剪 + 接进 `analyzer-gate.yml`（一行 step），并把文档口径从"与 Python 等价"
改成"与冻结答案键的回归比对"。若你想让它就地可再生产，另一条路是清理完成后**用 C# 侧重冻金标**——
但那会把它从"独立校验"降级成"变更探测器"，我不建议。

## 风险

| # | 风险 | 处理 |
|---|---|---|
| R1 | 把"实现即契约"的那一类误删（`PosixPathText`/`PythonGlob`/补偿求和/表格形状） | `01` §5 的"不许动"清单进 `implement.jsonl`；C1 的每一步先跑全批次 oracle |
| R2 | oracle 放宽过宽，把真实回归放过去 | 放宽只限数值路径（不碰 verdict 字符串/表格/结构）；`--mode byte` 保留为结构面回归；每步 diff 报告留证 |
| R3 | C2 的改名撞上门禁的字面路径：`.editorconfig:238,241,244,247` glob 了四个 Analysis 文件；3 个测试把源码路径当字符串读；`README.md:30-56,506-514` 点名字段 | `02` 的"不许动"清单 + 每步 `dotnet format`/测试；README 区间改动前后跑 `check-readme-contract.py` |
| R4 | C4 改 README 时静默缩小契约表覆盖 | 改动前后各跑一次 `check-readme-contract.py`，对比"111 key(s) checked against 401 declared constant path(s)"这行输出 |
| R5 | 三个子任务并行改同一批文件 | C1 → C2 串行（先删后改名）；C3 只碰 `scripts/`（与 C1/C2 无交集）；C4 最后 |
