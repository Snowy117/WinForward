# C3 执行计划

> 每个 A/B/C 组一个 commit。改之前先确认基线：`oracle-diff.py` 全批次 rc=0、
> `check-fairness.py` 全 PASS、`check-boundary-trees.py` 绿、`dotnet build -c Release` 零警告。
> 证据：`../10-09-e2e-csharp-native-cleanup/research/03-scripts-audit.md`（含每条的 `file:line`）。

## A 组 · 删除（互不依赖）

1. `git rm benchmarks/WinForward.E2E/scripts/contract-inventory.py`
   —— E1–E3 迁移工具，调用者只有 `README.md:65` 一行文档；**先确认它产出的两张表被保留**。
2. `git rm benchmarks/WinForward.E2E/scripts/normalize-pattern-hits.py`
   —— 调用者只有 `README.md:67` 与 `measurement-judgement.md:104`。
3. `rm -rf benchmarks/WinForward.E2E/scripts/__pycache__ benchmarks/WinForward.E2E.Analysis/verification/__pycache__`
   （未跟踪、已被根 `.gitignore` 覆盖）。

**判据**：`rg -n 'contract-inventory\.py|normalize-pattern-hits\.py'` 只剩文档里对"已退役"的叙述
（或干脆删掉那些文档行，见 D 组）。

## B 组 · 搬迁（每一处都要改引用）

1. **`effective-lines.py` → 根级 `tools/`**（新建目录）
   ```bash
   mkdir -p tools && git mv benchmarks/WinForward.E2E/scripts/effective-lines.py tools/effective-lines.py
   ```
   更新 5 处引用：`.trellis/spec/backend/directory-structure.md:103`、
   `.trellis/spec/backend/measurement-tooling.md:80-90`、
   `benchmarks/README.md:433`、`benchmarks/WinForward.E2E/README.md:71`（删掉这一行）与 `:695`
   （改掉"harness 自己的三个门禁"的说法）、脚本自身的 usage docstring。
   复跑：`python3 tools/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests` → 无输出 rc=0。
2. **`oracle-diff.py` + `check-fairness.py` → `Analysis/verification/`**
   ```bash
   git mv benchmarks/WinForward.E2E/scripts/oracle-diff.py benchmarks/WinForward.E2E.Analysis/verification/
   git mv benchmarks/WinForward.E2E/scripts/check-fairness.py benchmarks/WinForward.E2E.Analysis/verification/
   ```
   注意路径解析**实测结论：搬家后两个脚本都不需要改任何路径代码**——
   `oracle-diff.py:106` 是 `parents[3]`，`scripts/` 与 `verification/` 距仓库根都是 3 层，数值不变，
   其余常量都由 `REPO_ROOT` 派生；`check-fairness.py:110-114` 的 `repository_root()` 是
   **标记搜索**（向上找含 `benchmarks/WinForward.E2E` 的目录），同样与位置无关。
   ⚠️ **不要改 `oracle-diff.py:106`**（复核 B3：改成 `parents[4]` 会直接弄坏 differ）。
   唯一要做的是**跑一遍验证**（见判据）与更新文档里的路径引用。
   同步引用：`row-profiles.json:4`、`measurement-tooling.md`（多处）、两个 README、
   `10-07-e2e-harness-refactor` 归档任务里的引用**不改**（历史文档）。
3. **两张契约表 → `verification/`（复核 B1：必须明确选"搬"，不留二选一）**：
   `contract-inventory.json`、`contract-rename.json`、`contract-rename.md`。
   它们今天**只存在于** `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/`
   （`git ls-files` 已证），活路径不存在；archive-aware 方案要在两个脚本里复制
   `RepoPaths.cs:53-67` 的查找逻辑，还把地面真值永远留在历史里——所以用 `git mv`。
   ⚠️ `check-fixture-drift.py` 的改法（复核 B2）：
   `:38` 的 `parents[3]` **保留**；**:39 的 `SCRIPTS = …/WinForward.E2E/scripts` 必须保留**
   （它是 `:42-44` `import jsonl_paths` 能解析的原因，删了就 import 失败）；
   **只改 `:40`** 指到 `REPO_ROOT / "benchmarks" / "WinForward.E2E.Analysis" / "verification"`；
   `:12-14` 的陈旧 docstring 一并改。
   其余同步点：`check-readme-contract.py:47`、`compare-records.py:836`（可选参数）、`make_tree.py:23`（docstring）。

**判据**：`python3 benchmarks/WinForward.E2E.Analysis/verification/oracle-diff.py` 全批次 rc=0；
`python3 benchmarks/WinForward.E2E.Analysis/verification/check-fairness.py` 全 PASS。

## C 组 · 修坏路径（每处复跑，退出码进证据）

| # | 位置 | 修法 | 判据 |
|---|---|---|---|
| 1 | `check-readme-contract.py:47` | 指向表的新家（随 B3） | rc=0，输出 `111 key(s) checked against 401 declared constant path(s): ok`（复核：这两个计数读的是 README token 与 `ArmKeys` 常量，**搬表不会改变它们**） |
| 2 | `check-fixture-drift.py:38-40` | 见 B3 的精确改法（保留 `:38`/`:39`，只改 `:40`） | **两个判据**：`--tree /nonexistent` rc=**2**（现在 rc=1 + traceback）；`--tree /tmp/wf-synth` rc=0 且输出 `fixture drift: none`（复核指出原判据测的是一个计划从不修复的输入） |
| 3 | `publish-campaign.sh:8,36` | 结果根改为参数化/现有目录，去掉 `cd .../analysis` | `bash -n` + 人工走一遍路径（**本机生效，不可提交**） |
| 4 | `publish-campaign.sh:28` | `target-ledger.jsonl` → `ledger-main.jsonl`/`ledger-direct.jsonl`（对齐 `start-targets.sh:29,33`） | 同上（**不可提交**） |
| 5 | `deploy-campaign.sh:9,43-57` | 不再依赖无人创建的 `/tmp/wf-bench/deploy`；`:30` 去掉重复路径；`:52` 补 `plans-windows/` | `bash -n` + 说明前提（**不可提交**） |
| 6 | `orchestrator.ps1:62-64,262,358,382`（**T3**） | `Write-Log` 走的是 `Write-Output`，两个 `Invoke-Client` 调用点用 `[void](…)` 丢掉整条输出流 → 退出码日志消失。改成 `Write-Host` 或把退出码单独写进结果文件 | 人工复核 + 语法检查（**这个文件是被跟踪的，可以提交**） |
| 7 | `make_tree.py:15-16`（**T6**） | docstring 与生成器对齐（它写两份账本，不是每 pass 一份） | 读一遍即可 |
| 8 | `FROZEN.md:35,92`；`10-06-e2e-competitor-benchmark` 的 4 处陈旧指针 | 指向归档路径 / 现存 campaign 目录 | `rg` 无残留 |

**注意**：`publish-campaign.sh`/`deploy-campaign.sh`/`start-targets.sh`/`wf.sh` **不在 `git ls-files`**（复核已证），
所以 3/4/5 三条是**本机编辑、无法提交**——报告里必须如此陈述，别假装它们进了 commit。
`orchestrator.ps1` 是唯一被跟踪的，它的修复可以进 commit。

## D 组 · 文档口径

1. `WinForward.E2E/README.md` 的脚本表：删掉 `contract-inventory.py`/`normalize-pattern-hits.py`/
   `effective-lines.py`/`oracle-diff.py`/`check-fairness.py` 五行（后三者搬走了）；
   `:694-697` 改成"本项目自己的门禁是 X、Y、Z；400 行限制是全解决方案规则，工具在 `tools/`"。
2. 三份 README + `measurement-tooling.md`：写明**这些脚本没有任何 CI 跑**
   （`.github/workflows/` 只有 analyzer-gate 与 release-build），并保留"人手动跑"的定位。
3. `measurement-judgement.md:91-108` 对 `compare-records.py` 的引用按新路径校正。

**判据**：`git ls-files benchmarks/WinForward.E2E/scripts` 与 README 表**逐项核对**——
注意复核指出"逐行一致"不可能（表里也记录 gitignored 的本机脚本与目录），正确判据是：
表里出现的每个**被跟踪**脚本都存在于 `git ls-files`，反之每个被跟踪脚本都在表里（或被明确标为本机胶水）；
`rg -n 'scripts/(oracle-diff|check-fairness|effective-lines|contract-inventory|normalize-pattern-hits)'`
只剩历史归档文档。

## 收尾

- 全套门禁按父任务 `design.md` §5 跑一遍（**`tools/` 是新目录，`.slnx` 逐项目枚举、`.editorconfig`
  与 `Directory.Build.props` 里没有 glob 假设，所以"build 一次以验证 tools/"是空动作**——
  复核已证，不要把它写成判据）。
- spec 更新（复核列出的六处，缺一不可）：`measurement-tooling.md:71,73,75,84,127,128`、
  `measurement-judgement.md:72,75,89,103-104`、`measurement-record-contract.md:119`、
  `directory-structure.md:103`（新命令）。
- 交给父任务登记的（不在本任务做）：`compare-records.py`/`jsonl_paths.py` 的存废、
  4 个本机脚本的入库与参数化、D7 的 CI step。
