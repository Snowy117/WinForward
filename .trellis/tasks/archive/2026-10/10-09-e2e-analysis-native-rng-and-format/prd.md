# C1 · 分析器：把 Python 仿真换成 .NET 原生行为

父任务：`10-09-e2e-csharp-native-cleanup`（需求 R1/R2、AC1–AC4）。
本文件只写 C1 的边界与判据；**逐项证据在父任务的调研里**，实施时按下面的顺序读：

| 读什么 | 为什么 |
|---|---|
| `../10-09-e2e-csharp-native-cleanup/design.md` §0–§1 | 两类原则（可换实现 / 实现即契约）、S0–S6 步骤、oracle 放宽规则 |
| `../10-09-e2e-csharp-native-cleanup/research/01-python-emulation.md` | 18 个仿真项逐条的调用者、依赖、判据；§4 删除顺序；**§5「不许动」18 条** |
| `../10-09-e2e-csharp-native-cleanup/research/00-rng-swap-spike.md` | 换 RNG 的实测：75 条 p 值差异、0 结构、0 verdict 翻转 |

## 目标

`benchmarks/WinForward.E2E.Analysis` 里约 900 行"为模仿 Python 而写"的代码删掉约 500 行
（连同逐字一致的测试与三张向量表总计约 6 400 行），RNG 换 `System.Random`、数字与 JSON 文本换
.NET 常规写法；**被逐字/逐格/逐字节比对的那些行为一行不改**。

## 范围

- **改**：`benchmarks/WinForward.E2E.Analysis/**`、`tests/WinForward.E2E.Tests/Analyzer*Tests.cs`、
  `Analysis/verification/**`（金标、向量表、脚本）、`scripts/oracle-diff.py`（窄放宽），
  以及 `measurement-tooling.md` / `FROZEN.md` 中与之相关的段落。
- **不改**：`WinForward.E2E.Contracts` 的 `ArmKeys` 常量值、`tables.md` 与 `verdict.json` 的结构、
  CLI 参数、`verification/golden/py-tables.md` 与 `py-verdict.json`
  （它们是 oracle 的答案键，不是"Python 时代的残留"）。

## 验收标准

| # | 判据 | 命令 / 证据 |
|---|---|---|
| AC1 | S0–S6 每步之后，全批次语义 oracle rc=0（放宽项以外无差异） | `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py` |
| AC2 | S4（换 RNG）之后、S5（放宽）之前，oracle 报**约 75 条 `[value]`、0 `[structure]`、0 missing**，且差异全部落在 `p_value`/`p_equivalence`/`holm_*` 上——与 `research/00` 对齐 | 同上，差异按字段分组 |
| AC3 | 放宽规则只覆盖**数值叶子**，路径按 differ 的真实点分形状匹配：`re.fullmatch(r"verdict\.json:metrics/.+\.pairs\[\d+\]\.(p_value\|holm_p_value\|p_equivalence\|holm_p_equivalence\|ci95\[[01]\])", path)`（metric 成员名自带点，`.+` 是刻意的）；p 值四件套用**绝对 `5e-2`**（γ 段实测修正：容差该由量的复现性决定，实测同树换种子 p 值最大位移 0.0304、SD 0.0064；原 `1e-2` 是误按打印粒度推的，只吸收 75 条里的 48 条），`ci95` 用相对容差 `max(1e-2, 1e-2·abs(expected))`（两轮实验里 `ci95` 一次都没动）。理由写进 `oracle-diff.py` docstring + `FROZEN.md` + `measurement-tooling.md` | `git diff` 审查 + 三处文档 |
| AC4 | `CpRandom.cs`、`PythonExponential.cs`、三张向量表、`make_cp_vectors.py` 已删除；**`AnalyzerRandomGoldenTests.cs` 只删 CPython 向量部分，`StableHash`/`DeriveSeed` 的断言（`:93-102`，含 `DeriveSeed(20261006, key) == 21128053`）必须改写保留在它们的新家**；`RepoPaths` 里指向已删金标的字段一并删除 | `rg` 无残留 + `dotnet test` |
| AC5 | `research/01` §5 的 18 条「不许动」全部未被触碰（尤其 `PosixPathText`、`PythonGlob`、Neumaier 求和、`\n`+无 BOM、`plots-SKIPPED.md` 字节、154 行 11 格） | 逐条 diff 审查 |
| AC6 | 门禁：`dotnet build -c Release` 零警告、`dotnet test -c Release` 绿、`dotnet format --severity info --verify-no-changes` 空、oracle 全批次 rc=0、`check-fairness.py` 全 PASS、`check-boundary-trees.py` 绿 | 逐条命令 |
| AC7 | 文档不再承诺"与 Python 逐字一致"：`--seed` 的表述改为"同一运行时下可复现"；`FROZEN.md` §1.1 随三张表退役更新 | `rg -i 'cpython|逐字' benchmarks/WinForward.E2E.Analysis/README.md` 只剩对冻结节点的叙述 |

## 不在范围内

- 命名/结构清理（C2）、脚本搬家（C3）、README 的 16 条陈旧声明（C4）。
  遇到这些**只登记、不动手**（写进 `research/notes.md` 交给父任务）。
- 给 bootstrap 换算法、改门禁阈值、改表格结构。
- 把分析器做成 C# 原生工具链（例如用 Roslyn 重写 `effective-lines`）。

## 依赖与顺序

无前置。S0 与 S1 可并行；S2、S3 各自独立可回滚；**S4 必须在 S5 之前**（先看见 75 条差异，
再让放宽解释它）；S5 之后才允许删文件与向量表。

## 风险

| # | 风险 | 处理 |
|---|---|---|
| 1 | 误删「实现即契约」项 | `research/01` §5 是硬清单；每步跑 oracle，出现 `[structure]` 立刻回滚该步 |
| 2 | 放宽过宽，真实回归溜过去 | 只按**路径**放宽数值；verdict 字符串/表格/结构/key 集一律不放宽 |
| 3 | `Utf8JsonWriter` 的转义与 `VerbatimJson` 不同导致 `--mode byte` 变红 | 这是**已登记**的预期差异（D21 §2：byte 不是批次判据）；但语义模式必须绿 |
| 4 | `%.3g`/`%.3e` 回退被一并删掉 | oracle 的「参考打印过的分数不能丢」规则会立刻红 27 格——这是判据，不是运气 |
