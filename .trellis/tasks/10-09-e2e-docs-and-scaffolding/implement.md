# C4 执行计划

> 前置：C1/C2/C3 已归档。开工第一件事是**复核**：`git log --oneline -20` 看三个子任务实际改了什么，
> 再逐条核对 `research/04` §7 的 16 条陈旧声明——有些会被它们顺手修掉。
> 每一步：`git status` 干净 → 改 → 复跑相关门禁 → commit。

## P0 · 先建"防缩水"基线

```bash
python3 <check-readme-contract 的新路径> | tee /tmp/crc-before.txt   # 记下 rc 与那行计数
```
之后每次动 `WinForward.E2E/README.md` 或它引用的表，都要与 `/tmp/crc-before.txt` 对比
（AC2：`111 key(s) checked against 401 declared constant path(s): ok`）。

## P1 · 16 条陈旧声明（一批一 commit，按文件分组）

逐条以 `research/04` §7 的表格为准（每条都有两端的 `file:line`）。高价值几条：

1. `WinForward.E2E/README.md:72` "28 CLI commands" ↔ `cli-snapshots.py:42-76` 的 **31**
   （`before/` 树是 28、`after/` 是 31）→ 改成与脚本一致的表述。
2. `WinForward.E2E/README.md:694-697` "三个门禁同样跑法" → 只有 `CliSnapshotTests` 真跑；
   其余是人手动跑（C3 已改写一部分，本任务复核并补全）。
3. `.trellis/spec/backend/quality-guidelines.md:125` 把 `analyzer-gate.yml` 说成 build/test 的所在地
   → 该 workflow 两者都不跑；改成事实（并指向真正跑它们的地方/说明没有 CI）。
4. `benchmarks/README.md:13` 的死任务路径；`WinForward.E2E/README.md:65,393` 的 `research/`
   死路径（C3 搬表后按新家写）。
5. `Analysis/README.md:498` 把 `check-fairness.py` 的路径写错（C3 搬家后按新家写）。
6. `WinForward.E2E/README.md:227` `payloadBytes` "0 or more" ↔ `PlanFile.cs:81` + `FrameCodec.cs:45`
   的上限 `4194304`。
7. `WinForward.E2E/README.md:241` "every numeric key is an integer" ↔ `seconds` 是 double
   （`PlanFile.cs:325,497`）。
8. `WinForward.E2E/README.md:58-72` 漏掉 `compare-records.py` 整行 → 补上。

**行号引用同步（AC3）**：`measurement-tooling.md:9` 的 "line 372" 与 `:65` 的 "README.md:58-72"
——在 372 行之前插入过任何内容就要重算。

## P2 · 数据（plans / configs）如实记录（一批）

1. README 里写清三套 plan 的关系：`plans/` 6 份（正式）、`plans-short/` 5 份（短时长，7 个非
   `seconds` 差异）、`plans-windows/full-shape-plan.json`（`plans/full-plan.json` 的 9 个
   `seconds` 覆盖）。**不新增 loader 机制**；把"可以用覆盖机制去重"登记为后续条目。
2. `configs/wf-fdd-opt.json` 与 `wf-aot-opt.json` 逐字节相同 → 合并（并用 `rg` 确认无引用）
   或在 README 写明为何要两份。
3. `bench.ppx`（Proxifier 配置）仓库里没有，而 README:74-84 暗示清单完整 → 或补文件、或改文档。

**判据**：`rg -F full-shape-plan`、`rg -F wf-fdd-opt`、`rg -F bench.ppx` 的结果与 README 一致。

## P3 · `AGENTS.local.md` 与 FROZEN.md 的收尾（一批）

1. `AGENTS.local.md:163` 指向已删除的 `benchmarks/results/2026-10-06-e2e-competitors/analysis`
   → 改为现状（C3 已修脚本侧，这里修 runbook）。
2. `FROZEN.md` 的失效链接（`:35,92,137,148,172` 一类的活任务路径）：C1 会改 §1.1（三张表退役）、
   C3 修 `:35,92`；本任务做**最终一致性复核**：每个 `sha256` 行都能对上现存文件、
   每个路径都存在（用 `git ls-files` + `sha256sum` 逐条验）。
3. `10-06-e2e-competitor-benchmark`（**未归档**）的 4 处陈旧指针（含 `design.md:210` 把已退役的
   `analyze.py` 说成活的读取者）→ 修。

## P4 · `IsTestProject` 的纸面结论（一批）

读 C2 的 `research/notes.md` 与 `tests/WinForward.E2E.Tests.csproj`：
- 若 C2 选择了"打开分析器覆盖"→ 本任务确认 `dotnet build -c Release` 零警告；
- 若选择"保留豁免"→ 确认 csproj 里有一行注释写明**为什么**（restore 期
  `ExcludeRestorePackageImports=true` 使 xunit props 不参与，显式 `IsTestProject=true`
  是唯一让四个分析器包不解析的原因）与**何时复核**；
- 两种情形都要把结论写进 `.trellis/spec/backend/quality-guidelines.md` 的分析器一节（R6）。

## P5 · D7：把 oracle 接进 CI（一批）

`.github/workflows/analyzer-gate.yml` 增加一步（在既有 `dotnet format`/`jb inspectcode` 之后）：

```yaml
- name: Oracle regression
  run: |
    dotnet build benchmarks/WinForward.E2E.Analysis/WinForward.E2E.Analysis.csproj -c Release
    python3 benchmarks/WinForward.E2E.Analysis/verification/oracle-diff.py
    python3 benchmarks/WinForward.E2E.Analysis/verification/check-fairness.py
```

注意：路径按 C3 搬家后的实际位置；先**本地完整跑一遍**确认它只依赖仓库内的
`synthetic-tree.tar.gz`/`golden/`/`row-profiles.json`（无外部路径、无 `/tmp` 预设）。
若 runner 不具备条件（例如没有 python3），就在 workflow 里写明为何不接——**不许默默不做**。

**判据**：workflow 文件 diff 审查 + 本地三条命令 rc=0。

## 收尾

- 全套门禁（父任务 `design.md` §5）。
- spec 更新：`measurement-tooling.md`（oracle 接 CI、脚本新家、16 条里涉及的契约表说明）、
  `quality-guidelines.md`（分析器覆盖 + CI 事实）。
- 父任务收尾：更新父 `prd.md` 的"父任务完成记录"、写 `design-decisions.md`（把 D1–D7 与
  实施中新增的裁定落盘）、归档父任务。
