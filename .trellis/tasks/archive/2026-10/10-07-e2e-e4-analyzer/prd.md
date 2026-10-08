# E4 C# 分析器

> **开工前裁定（2026-10-07）**：oracle 机制以 `../10-07-e2e-harness-refactor/design-decisions.md` **D6**
> 为准（两侧输出完整文件 + 已跟踪的 `scripts/oracle-diff.py` 切片 + 冻结树/golden A + 中性 `generated_by` +
> 保留 `make_tree.py`）。本文件旧措辞已就地修正。

父任务：`10-07-e2e-harness-refactor`。详细设计见
`../10-07-e2e-harness-refactor/design.md` §6（验证策略，尤其是"层三：双向 oracle"），
执行步骤见同目录 `implement.md` 的「E4」节。

本子任务展开的父需求：R9 / R10；AC9 / AC10。

## 目标

把 6053 行的 `analyze.py`（145 个顶层 def/class，住在某一次 campaign 的结果目录里，且不受本仓
任何自动化质量约束——没有 `pyproject.toml`、没有 ruff/mypy、`.editorconfig` 里没有 `[*.py]`）
换成一个 C# 项目，与 harness **共享契约类型**。

换栈的核心动机不是语言偏好，而是**契约漂移**：今天 JSON 字段名是散落的字符串，改名不会让任何
东西报错，只会让分析器的格子静默变成 `n/a`。共享 `WinForward.E2E.Contracts` 之后，字段名只有
一处定义，写出侧与读入侧不可能再漂移。

## 范围

- 新建 `benchmarks/WinForward.E2E.Analysis`，引用 `Contracts`。
- 按 `design.md` §1 的目录分模块：`Loading/` `Model/` `Stats/` `Checks/` `Metrics/` `Findings/`
  `Tables/` `Verdict/`。每个文件 ≤400 有效行。
- 产出 `tables.md` + `verdict.json`；CLI 参数与 Python 版一致
  （`--raw` `--out` `--ledger` `--flat` `--warmup-seconds` `--resamples` `--seed`）。
- **双向 oracle，按表格分 5 批**（机制见 D6）：升级 `synthetic/make_tree.py` 生成新契约的树并**冻结成
  `verification/synthetic-tree.tar.gz`**；最小改动 Python 参考实现（只换字段名 + `generated_by` 中性化，
  统计/判定/渲染逻辑一行不动，改动清单落 `research/python-oracle-changes.md`）；两套实现都输出**完整文件**，
  由 `scripts/oracle-diff.py` 按小节切片比对（固定解压路径 `/tmp/wf-synth/raw`；切片缺失即报错）。

  | 批次 | 内容 |
  |---|---|
  | 1 | `Loading` + `Model` + `Stats` + 环境/可用性表 —— 骨架与 oracle 机制打通 |
  | 2 | 不变量校验 + findings 分级 + gates 表 |
  | 3 | **headline / latency / udp / dns（四张核心表）** |
  | 4 | cpu / memory / persist / tcp |
  | 5 | dual / control / ledger / verdict.json |

  每批由 `oracle-diff.py` 对该批负责的小节比对；未实现小节写 `<!-- TODO(batch N) -->` 占位。
  若某批卡住，前几批的交付已经有价值（四张核心表覆盖绝大多数结论）。
- 落实 §5.2 的公平性 3 条：**`#17`/`#18` 已在 `analyze.py` 实现**（审查核对：`NOT_CARRIED_CELL` 逐字一致、
  `UDP53_LABEL` 5 处标注），本任务的交付是**加可 grep 的回归断言**；**`#19`（CPU 口径）是真缺口**。
  另加 `#11` 的 `undecodable` 消费点。
- **截断 caveat（E3-d 转来的要求，D19.3 H：分析器改动必须早于 golden 冻结）**：账本
  `tcpSummary/truncatedFrames` 与 `dnsSummary/truncatedFrames`（两层，`targetSummary/tcp|dns|dnsAlt` 同名）
  **> 0** 时，报告必须在**数据质量小节**披露该靶机/该 pass 的截断计数，与 `undecodable` 的 §14.6
  披露**同级**——按**靶机/pass 总量**给，**不得摊到臂**（臂级归属不存在，D9 的账本归属裁定）。
  截断的两台 server 是两种机制、同键名（D19.3 C），披露时要分开写：TCP 是帧被对端关闭切断，
  DNS 是它自己的长度前缀短读。synthetic 树由 `make_tree.py` 注入这两组键的**非零**值，
  使 caveat 在 oracle 里可见（E3-c 的裁定：caveat 与注入都归 E4）。
  > 真值来源：靶机账本（E3-c 已发布四层键）；`environment.json` 侧**不加**第二份来源
  > （E3-d 的裁定：客户端编排取不到靶机的数，见 `research/semantic-fixes/index.jsonl` 的
  > `E3-d-environment-keys-ruled-out`）。
- 新增**已跟踪**的薄封装 `scripts/analyze.sh`（内部 `dotnet run --project benchmarks/WinForward.E2E.Analysis -c Release`）。
  **不改** `publish-campaign.sh`（gitignored、不入库、不算交付物）。

## 不在范围内

- **绘图**：Python 版的 320 行 matplotlib 段落后置。C# 版输出 `plots/SKIPPED.md` 等价声明。
  绘图库选型（ScottPlot 等在 NixOS 上的 native 依赖）不在本任务内。
- 语义修复本身（E3）；本任务只负责让分析器**呈现**那些修复的结果。

## 依赖

- E1（契约）——必须，共享类型是重写的前提。
- E3（语义修复）——需要它引入的新字段（`detail`、`acceptErrors`、`udpReceivers`、`undecodable`、
  统一后的 `achievedRate`/`completionRate`）才能完整。
- 因此本子任务排在 E3 之后；但**准备 oracle**（升级 `make_tree.py` + 最小改动 Python 版）
  可以在 E3 期间并行开始。

## 验收标准

- [x] `WinForward.E2E.Analysis` 建好并加入 `WinForward.slnx`；引用 `Contracts`。
- [x] CLI 参数与 Python 版一致；可由单条命令运行（不需要 nix-shell 与 python 环境）。
- [x] **oracle：对同一棵新契约 synthetic 树，两套实现的 `tables.md` 与 `verdict.json` 逐字一致**
      （两条 `diff` 均为空）。
- [x] 按 5 批推进，每批一次 `diff`；批次 3（四张核心表）完成时即已覆盖绝大多数结论。
- [x] 公平性 3 条落实：泄漏的 UDP 行输出 `not carried (UDP bypassed)` 而非数字并被排除出成对比较；
      DNS 路径差异在表中标注；CPU 表显式声明「仅用户态、不含驱动/DPC/ISR/非分页池」。
- [x] 截断 caveat 落实：synthetic 树注入非零 `truncatedFrames` 时，数据质量小节出现该靶机/pass 的
      截断计数（TCP 与 DNS 两种机制分开写），且**没有任何臂级格子**消费它。
- [x] `plots/` 输出降级声明。
- [x] 每个文件有效行 ≤400。
- [x] `dotnet build WinForward.slnx -c Release` 零警告。

## 风险

分析器重写是本任务最大的单项工作。缓解：

- 双向 oracle 让进度可度量（按表格数量分批，每批对比一次）；
- Python 版在 oracle 通过前**不删**（V3 已定：oracle 通过后立即删除，依赖 git 历史保留）；
- 若工作量超预期，按"表格分组"切分成更小的交付，先交付 headline/latency/udp/dns 四张核心表。

> **权威规则**：父 `prd.md` 的 R/AC 是本子任务的**验收上限**；本文件的清单是它的展开，
> 冲突时**以父为准**，且本文件每条验收都必须能追溯到父的一个 R 或 AC。
> 父任务：`.trellis/tasks/10-07-e2e-harness-refactor/`。

---

## 完成记录（2026-10-08）

- 批次：**E4-a**（oracle 机制：升级 `make_tree.py` + 最小改动 Python 参考 + 冻结树/golden A + `oracle-diff.py` +
  骨架 + fixture 漂移 guard）、**E4-a2**（按用户裁定把 differ 改成**语义比对**，DD **D21**）、
  **E4-b1a/b1b/b1c2**（Loading/Model/§15/§2、`CpRandom`+格式化基元、§1/§0/§3 + 三键）、
  **E4-b3/b4/b5**（§5/§8/§9 + 15 metrics；§4/§6/§7/§10/§11 + 6 metrics；§12/§13/§14 + 3 键）、
  **E4-c**（§14.7 截断 caveat + `--zero-denominator` + 补偿求和锚 + 删 `analyze.py`）。
- **双向 oracle 达成**：default 树上 `--mode semantic` **全批次 51/51 rc=0**、`--tolerance 0` rc=0；
  byte 只在 `ledger.passes[*].types` 的 CPython set 成员序上有已登记差异。
- 每批 impl → 独立 check（突变树 + 负控 + 清缓存 inspectcode）→ 修复 → 提交；测试 322 → **1656**；
  六条门禁每批全绿，未新增任何 `.editorconfig` 抑制（仅 4 处带理由的 `S1244` pragma）。
- **check 轮抓到的真缺陷**（冻结树照不到、靠自造输入/突变树才现形）：身份键把数值 `1203` 与字符串 `"1203"` 压平、
  §13 不可用分支打印 `List\`1[String]`、`ledger_paths` 应按排序集合发布、`DnsFindings` 跳过判据看错边、
  `ledger-write-errors` 用错 JSON 键导致永不触发、`dual_row_summary` 累加器被重置。
- 产物：`verification/{synthetic-tree.tar.gz,golden/**,FROZEN.md,row-profiles.json,check-*.py,synthetic/make_tree.py}`；
  证据 `research/baseline/E4{a,a2,b1a,b1b,b1c2,b3,b4,b5,c}-*.md` 与各自的 `*-check.md`；
  `research/python-oracle-changes.md`（Python 侧的三类改动与必须保留的渲染怪癖）。
- **交给 E5**：`publish-campaign.sh`（gitignored）里的 `python3 analyze.py` 应指向 `scripts/analyze.sh`；
  `.trellis/spec/backend/measurement-harness.md` 里旧 `analyze.py` 字样已在本轮 §8 重写时清掉（其余归 E5）。
