# 实施计划：E2E 的 C# 化清理

> 配套 `prd.md` / `design.md`。本文件是执行清单：每一步都可独立验证、可独立回滚。
> 平台说明：DSH 走 **inline** 路线（主代理直接改代码），**跳过 `implement.jsonl`/`check.jsonl` 的
> JSONL 策展**；动代码前加载 `trellis-before-dev`，改完加载 `trellis-check`。

## 0. 开工前（Phase 1.4 评审门）

- [ ] 用户对 `prd.md` 的 D1–D6 表态（至少 D1 oracle 放宽范围、D2 脚本搬家、D4 `IsTestProject`）。
- [ ] 按裁定的任务地图创建四个子任务：
      `task.py create "<title>" --slug <name> --parent 10-09-e2e-csharp-native-cleanup`。
- [ ] 基线复核（在改任何东西之前，把这四条的**当前**结果记进证据）：
      `dotnet build WinForward.slnx -c Release`、`dotnet test WinForward.slnx -c Release`、
      `oracle-diff.py`（全批次 rc=0）、`check-fairness.py` + `check-boundary-trees.py` 绿。
- [ ] `git status` 干净；`10-07-tcp-close-drain` 的未提交改动与本任务无关，别动它。

## C1 · Python 仿真收尾（先做，C2 依赖它）

> **权威版本是子任务的 `implement.md` v2**：`.trellis/tasks/10-09-e2e-analysis-native-rng-and-format/implement.md`
> ——已吸收 `research/plan-review.md` 的四条 BLOCKER 与七条 SHOULD-FIX。本文件不再复述步骤细节
> （早先的版本有两处被评审证伪：`Truthy` 的调用点，以及 `Utf8JsonWriter` + `UnsafeRelaxedJsonEscaping`）。
>
> 评审改出来的关键事实，供父任务复核时对照：
>
> | 项 | 结论 |
> |---|---|
> | `Truthy` 真实调用点 | `RunSamples.cs:86,269`、`GateFlow.cs:233`、`TableEnvironment.cs:222,317`（不是写侧文件） |
> | `StableHash`/`DeriveSeed` | `AnalyzerRandomGoldenTests.cs:93-102` 是唯一钉住它们的测试 → **改写保留**，不能整文件删 |
> | S2/S3 的测试 | 金标测试在这两步就会编译不过 → **步内同步改**，不是留到 S5 |
> | JSON 编码器 | `UnsafeRelaxedJsonEscaping` 与金标方向相反（非 ASCII 字节 0、`\u` 360、22 个字面 `+`）→ **自定义 `JavaScriptEncoder`**，不换 `Utf8JsonWriter` |
> | S5 放宽挂载点 | `Comparer.compare_json` 的数字分支 `oracle-diff.py:968-972`；path 是**点分**的 |
> | 容差 | p 值四件套绝对 `1e-2`；`ci95` 用相对 `max(1e-2, 1e-2·abs(expected))` |
> | S4 判据 | `differences:` 行须匹配 `0 structure, ~75 value, 0 missing`，且所有差异 path 都在 `verdict.json:metrics/*.pairs[*].{p_value,p_equivalence,holm_p_equivalence}`，`tables.md:*` 不得出现 |
>
> 派发顺序：α = S0+S1（已派）→ β = S2+S3 → γ = S4+S5+S6。**串行**（并发 `dotnet build` 会撞 obj/bin）。

## C2 · 命名与结构（C1 之后）

> **权威版本是子任务的 `implement.md`**：`.trellis/tasks/10-09-e2e-naming-and-idiom-pass/implement.md`
> ——已吸收 `research/plan-review.md` 的两条 BLOCKER 与 SHOULD-FIX。评审改出来的关键事实：
>
> | 项 | 结论 |
> |---|---|
> | 判据不能用 `check-readme-contract.py` | 它在 HEAD 上 rc=2（`:47` 指向已归档的表，`:272` 无条件读）→ 用 `rg` 判据 + 三个契约测试；检查器修复归 C3 |
> | 缩写改名 | **4 处、26 处引用**（不是 3 处/41 处），外加 `.trellis/spec/backend/measurement-lane-seam.md:19,25` |
> | `LedgerViews.cs` | **没有** `.editorconfig` glob；`:247` 的 glob 属于既有的 `Findings/LedgerFindings.cs` → 不许改名撞上去 |
> | 命名元组 | 字面声明 **14 个**（不是 21）；`Detail`/`Error`→`Reason` 与 `MetricCell` 的非空 `Status` **不做**；`CpuDetail.cs:58` 的 3 元组单独裁定 |
> | `IPv4`/`IPv6` 规则 | 只对**新代码**生效——`src/` 有 108 处 `Ipv[46]`，别立一条 `src/` 自己违反的法（另立后续条目） |
> | `IsTestProject` | 机制由父 session 实测确认（见 `design-decisions.md` D4），评审标"未验证"的部分以父 session 的测量为准 |

## C3 · 脚本（可与 C1 并行；只碰 `scripts/` 与 `verification/`）

- [ ] 删 `contract-inventory.py`、`normalize-pattern-hits.py`、两个 `__pycache__`
      （**两张契约表必须留下**）。
- [ ] 建根级 `tools/`，`git mv` `effective-lines.py`；更新 5 处引用
      （`directory-structure.md:103`、`measurement-tooling.md:80-90`、`benchmarks/README.md:433`、
      `WinForward.E2E/README.md:71,695`、脚本自己的 usage docstring）。
- [ ] `git mv` `oracle-diff.py` + `check-fairness.py` → `Analysis/verification/`；
      **两张契约表也搬（评审已裁定"搬"，不留二选一）**——它们今天只在归档任务目录里，活路径不存在；
      同步 `row-profiles.json:4` 等引用。
      ⚠️ **不要改 `oracle-diff.py:106` 的 `parents[3]`**（评审：改 `parents[4]` 会直接弄坏 differ；
      `check-fairness.py` 的 `repository_root()` 是标记搜索，同样不用改）。
- [ ] 修七处坏路径（`design.md` §3.3），每处复跑并留退出码；
      `check-fixture-drift.py`：**保留 `:38`/`:39`，只改 `:40`**，并加两个判据
      （`--tree /nonexistent` rc=2；`--tree /tmp/wf-synth` rc=0 输出 `fixture drift: none`）。
      注意 `publish-campaign.sh`/`deploy-campaign.sh`/`start-targets.sh`/`wf.sh` **不在 `git ls-files`**，
      它们的修复是本机编辑、进不了 commit；`orchestrator.ps1` 被跟踪、可提交。
- [ ] README 的脚本表与"谁跑什么"改写：没有 CI 跑这些脚本（`research/03` §0），
      且 `README.md:694` 不该自称全解决方案门禁是本项目的。
- [ ] 判据：每个门禁脚本按文档跑一次 rc=0；`bash -n` 全部语法通过；
      README 表与 `git ls-files` **逐项核对**（不是"逐行一致"——表里也有本机胶水与目录）。

## C4 · 文档与数据（C1–C3 之后）

- [ ] 16 条陈旧声明逐条修（`research/04` §7）。改动 `README.md:372-447` 前后各跑一次
      `check-readme-contract.py`，对比 `111 key(s) ... 401 declared constant path(s): ok` 那行。
- [ ] 在 372 行之前插入内容 / 改动 `:58-72` 时，同步 `measurement-tooling.md:9` 与 `:65` 的行号引用。
- [ ] plans/configs：三套 plan 的用途与差异如实写进 README（**不新增 loader 机制**，去重另行登记）；
      `wf-fdd-opt.json` 与 `wf-aot-opt.json` 的重复、`bench.ppx` 的缺失，或补齐或写明。
- [ ] `AGENTS.local.md:163` 指向已删 campaign 目录 → 修；与 `start-targets.sh` 的 label 约定对齐。
- [ ] `FROZEN.md:35,92,137,148,172` 五处失效链接；`10-06-e2e-competitor-benchmark`（未归档任务）的 4 处陈旧指针。
- [ ] `benchmarks/WinForward.E2E/README.md:37` 对 `ArmContext` 的描述（C2 拆分后）复核。
- [ ] 判据：`check-readme-contract.py` rc=0 且 key 计数不缩水；`rg` 逐条勾掉 16 条清单。

## 收尾（Phase 3）

- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` 空输出。
- [ ] `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` → 0 `<Issue>`
      （若报 `CSharpErrors`，先清 `~/.local/share/JetBrains/` 缓存再判）。
- [ ] 全门禁 `design.md` §5 的整块命令，输出留证。
- [ ] spec 更新：`measurement-tooling.md`（脚本新家、oracle 放宽、`FROZEN.md` 的向量表退役）、
      `directory-structure.md:103` 的新命令、`quality-guidelines.md:125` 的事实更正。
- [ ] 四个子任务各自归档；父任务记录最终证据（删减量、门禁输出、被放宽项的清单）。
- [ ] 用户提醒 + 提交（提交信息按仓库习惯：`refactor(e2e): ...` / `chore(e2e): ...` 分组）。

## 预判的坑

| 坑 | 信号 | 处理 |
|---|---|---|
| `--mode byte` 变红被当成回归 | C1 S2/S3 之后 byte 模式红 | 那是登记的预期差异；语义模式才是批次判据（D21 §2） |
| oracle 放宽后真实回归溜过去 | 某次 oracle 绿但数字明显异常 | 放宽只限 `pairs[*]` 的数值路径；verdict/表格/结构仍严格 |
| C2 改名撞上门禁字面量 | build 绿但某个测试/检查器红 | 改名禁区清单；每次改完跑 `check-readme-contract.py` |
| C4 改 README 静默缩小门禁覆盖 | 检查器仍 rc=0 但 key 计数变小 | 每次对比 "111 key(s) / 401 constants" 那行 |
| `System.Random` 换运行时后报告变化 | 换 .NET 版本后 oracle 红 | 已登记的 p 值放宽会吸收它；README 的 `--seed` 承诺已改写 |
