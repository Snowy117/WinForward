# C3 · 脚本处置：删、搬、修

父任务：`10-09-e2e-csharp-native-cleanup`（需求 R4、AC6）。
逐条证据：`../10-09-e2e-csharp-native-cleanup/research/03-scripts-audit.md`（717 行，每个脚本一行处置 + `file:line`）。

## 目标

`benchmarks/WinForward.E2E/scripts/` 与 `WinForward.E2E.Analysis/{scripts,verification}/` 只留下
**活着的、属于本项目的**脚本；全解决方案级的门禁搬回它该在的地方；今天就坏掉的路径全部修好并复跑。

## 范围

- **改**：`benchmarks/WinForward.E2E/scripts/**`、`benchmarks/WinForward.E2E.Analysis/scripts/**`、
  `benchmarks/WinForward.E2E.Analysis/verification/**`（脚本与它们引用的表）、
  新建根级 `tools/`、以及引用它们的文档（`README.md` ×3、`.trellis/spec/backend/*.md`）。
- **不改**：`plans*/**` 与 `configs/**` 的内容（C4 处理）、任何 `.cs`（C1/C2 处理）、
  4 个 gitignored 本机脚本的**跟踪状态**（就地修，不入库）。
- **顺序**：本任务在 C1 之后执行——C1 会修改 `oracle-diff.py` 的内容，本任务要 `git mv` 它，
  两个改动撞同一文件，串行才干净。

## 验收标准

| # | 判据 |
|---|---|
| AC1 | `contract-inventory.py`、`normalize-pattern-hits.py` 已删除；**它们产出的两张契约表仍在**（`contract-inventory.json`/`contract-rename.json`，随搬迁移动），且 `check-fixture-drift.py`/`check-readme-contract.py`/`compare-records.py` 的引用已同步 |
| AC2 | 两个 `__pycache__` 目录已删除（未跟踪、已被根 `.gitignore` 覆盖） |
| AC3 | 根级 `tools/effective-lines.py` 存在，且 5 处引用已更新（`directory-structure.md:103`、`measurement-tooling.md:80-90`、`benchmarks/README.md:433`、`WinForward.E2E/README.md:71,695`、脚本自身 usage），四路径复跑无输出 |
| AC4 | `oracle-diff.py`、`check-fairness.py` 与两张契约表在 `WinForward.E2E.Analysis/verification/`；`row-profiles.json:4` 等引用同步；`oracle-diff.py` 复跑全批次 rc=0 |
| AC5 | 七处坏路径修好：`check-readme-contract.py` rc=0（输出含 `111 key(s) checked against 401 declared constant path(s): ok`；复核证这两个计数不会随搬表改变）、`check-fixture-drift.py` **两个判据**（`--tree /nonexistent` rc=**2**，`--tree /tmp/wf-synth` rc=0 输出 `fixture drift: none`）、`publish-campaign.sh:8,28,36`、`deploy-campaign.sh:9,30,52`、`orchestrator.ps1:62-64,262,358,382`（T3）、`make_tree.py:15-16`（T6）、`FROZEN.md:35,92` 与 `10-06-e2e-competitor-benchmark` 的 4 处陈旧指针 |
| AC6 | README 的脚本表与 `git ls-files` **逐项核对**（表里每个被跟踪脚本都在 `git ls-files`，反之亦然；本机胶水在表里明确标注，见复核：`publish-campaign.sh`/`deploy-campaign.sh`/`start-targets.sh`/`wf.sh` **不在** `git ls-files`）；`WinForward.E2E/README.md:694` 不再把全解决方案门禁说成"harness 自己的"；明确指出**没有任何 CI 跑这些脚本** |
| AC7 | `bash -n` 对每个改过的 `*.sh` 通过；`orchestrator.ps1` 做人工语法复核并记录（除非有 pwsh） |

## 风险

| # | 风险 | 处理 |
|---|---|---|
| 1 | 搬表后检查器仍读旧路径（它们今天就是坏的） | 先修路径再搬，或搬的同时改；每步复跑 |
| 2 | `check-fixture-drift.py` 的退出码语义（三态约定：2 = 输入缺失）被当成"新需求"漏掉 | 它是 AC5 的显式判据 |
| 3 | 4 个 gitignored 脚本改了但无法提交 | 明确写进证据：改动是本机生效的；README 说明它们是本机胶水 |
| 4 | `tools/` 是新目录，可能触发路径假设 | 检查 `.github/workflows/**` 与 `Directory.Build.props` 是否有 `tools/` 相关假设 |

## 不在范围内

- 把 Python 脚本改写成 C#（`effective-lines.py` 明确保留 Python：纯文本工具，注释剥离器已经写好且正确；
  用 Roslyn 重写是另一件事，登记为后续条目）。
- `compare-records.py` / `jsonl_paths.py` / `check-fixture-drift.py` 的**存废**（只修不删；
  存废见 `research/03` §7 的 open question，交给父任务）。
- 4 个本机脚本的入库与参数化。
