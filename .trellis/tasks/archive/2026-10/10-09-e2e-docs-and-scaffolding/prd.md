# C4 · 文档、数据与 plumbing 收尾

父任务：`10-09-e2e-csharp-native-cleanup`（需求 R5/R6、AC7）。
逐条证据：`../10-09-e2e-csharp-native-cleanup/research/04-scaffolding-build-docs.md`（536 行；
§7 是 16 条陈旧声明的双端 `file:line`，§6 是"可安全删除"清单）。

## 目标

三份 README 之间、以及文档与代码之间不再自相矛盾；plans/configs 的重复与缺失**如实记录**而不是
悄悄加机制；plumbing 的最后一个悬案（E2E 测试项目的分析器覆盖）有结论；oracle 按 D7 接进 CI。

## 前置

C1、C2、C3 已归档——本任务的"陈旧声明"清单要在它们改完之后复核一遍
（有些条目会被它们顺手修掉，例如 `oracle-diff.py` 的路径）。

## 验收标准

| # | 判据 |
|---|---|
| AC1 | `research/04` §7 的 16 条陈旧声明逐条勾掉（每条给出改法或"已被 C1/C2/C3 修掉"的证据） |
| AC2 | **门禁覆盖不缩水**：`check-readme-contract.py` 在改动前后同样 rc=0，且 `111 key(s) checked against 401 declared constant path(s): ok` 那一行的数字不变；若不得不变，逐字解释原因 |
| AC3 | `measurement-tooling.md:9` 的 "line 372" 与 `:65` 的 "README.md:58-72" 行号引用与实际一致（在 372 行之前插入过内容的，必须同步更新） |
| AC4 | plans/configs：`plans-windows/full-shape-plan.json`（9 个 `seconds` 覆盖）与 `plans-short/`（7 个非 `seconds` 差异）的用途与差异写进 README；`wf-fdd-opt.json` 与 `wf-aot-opt.json` 逐字节相同这件事有处置（合并或写明理由）；README 暗示但仓库里没有的 `bench.ppx` 或补齐或改文档。**不新增 `PlanFile` 的覆盖机制**（去重另行登记） |
| AC5 | `AGENTS.local.md:163` 指向已删 campaign 目录的问题修好；它与 `start-targets.sh` 的 label 约定一致 |
| AC6 | `IsTestProject`：确认 C2 的 spike 结论已写进 csproj 注释或 spec（本任务不重复做实验，只负责"有结论且落在纸面上"） |
| AC7 | **D7 落地**：`.github/workflows/analyzer-gate.yml` 增加一步跑 `oracle-diff.py`（全批次）+ `check-fairness.py`，或在 workflow 里写明为何不接（若接，CI 需要 Release build + python3，两者 runner 都有） |
| AC8 | 三份 README 的脚本/文件表与 `git ls-files` 一致；`benchmarks/README.md:13` 等死任务路径修好 |

## 风险

| # | 风险 | 处理 |
|---|---|---|
| 1 | 改 README 契约表区间（`:372-447`）导致检查器**静默**缩小覆盖 | 每次改动前后各跑一次检查器并对比那行计数；插入 `## ` 或移动该节都算高危操作 |
| 2 | 同步行号引用时把 `measurement-tooling.md` 的其它引用改错 | 只改被证明漂移的两处，`git diff` 逐行复核 |
| 3 | CI 步骤让 `analyzer-gate` 变慢或在不具备条件时红 | 先本地完整跑一遍 oracle（约 5 秒 + build），确认无外部依赖（它只读仓库内的 tarball 与 golden） |
| 4 | 把 C1/C2/C3 已经修过的条目重复修一遍 | 开工前先 `git log` 三个子任务的 commit，逐条核对 |

## 不在范围内

- 任何 `.cs` 语义/命名改动（C1/C2）。
- 脚本的搬家与坏路径（C3）——本任务只在文档层面复核。
- `benchmarks/results/**` 的历史数据（除非发现误跟踪）。
- `benchmarks/WinForward.Benchmarks`（BenchmarkDotNet 项目）与产品代码。
