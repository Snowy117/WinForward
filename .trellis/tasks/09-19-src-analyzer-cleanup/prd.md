# PRD: 提高 src/ 代码质量 —— dotnet format 诊断器清零

Task: `.trellis/tasks/09-19-src-analyzer-cleanup`
Base commit: `c8a5453` (master)

## Goal

把 `dotnet format --severity info --verify-no-changes` 诊断清零：格式缺陷（缩进/换行/导入排序）直接修复；分析器与 IDE 诊断逐条分析——听从警告改代码若能提升质量/可维护性就改代码，否则以最小范围、附书面理由的 pragma / editorconfig 抑制。最终由单独派遣的独立代理审查所有被忽略（抑制）的诊断是否必要。

## Background（已核实事实，base c8a5453）

- 基线运行 `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore`：真实 exit=2，共 **1369 条诊断 / 182 文件**（报告与原始日志已固化到 `research/format-report.json`、`research/analyzer-inventory.md`）：
  - **src 426 条 / 75 文件**（Runtime 199、NdisApi 73、Windows 63、Protocols 33、Core 30、Configuration 17、Cli 11）
  - **tests 762 条 / 83 文件**；**benchmarks 181 条 / 24 文件**
- src 主要规则（46 条）：IDE1006 命名 114、MA0076 插值字符串隐式 ToString 57、MA0003 命名实参 45、MA0007 尾逗号 23、IDE0305 18、RCS1085 自动属性 18、CA1859 14、CA1512 ThrowIf 14、IDE0290 主构造器 13、CA1068 CT 参数位置 11、MA0189 InlineArray 11、MA0154 langword 10、CA1068/MA0154 等；格式类仅 8 处（7 FINALNEWLINE + 1 IMPORTS）。
- 构建基线：`TreatWarningsAsErrors=true`（warning 级历史任务已清零），本次面对的主要是 **info 级**诊断；四分析器包（Meziantou/Roslynator/Sonar/VSThreading）+ IDE 规则。
- `.editorconfig` 既有 **36 条 `severity = none`** 抑制 + IDE0055/IDE0060 调整。多处理由引用本仓库不存在的架构（ASP.NET Core host、EF 实体、AccessControlContracts、GameSettingsContract；TODO 计数为 0；而 ConfigureAwait 在 src 出现 231 次）——疑似继承自其它项目，最终审计需实证其必要性。
- IDE1006 抽查证据（决定"改名 vs 重配"的关键）：
  - public ABI struct 字段 PascalCase（NdisApiAbi.cs，~25 处）被判"缺 `_` 前缀"——因 `instance_fields` 规则无可见性过滤；
  - 私有 `static readonly` PascalCase（如 PacketChecksums.SwapAdjacentBytes）被判"缺 `s_` 前缀"——代码库实际不用 `s_`；
  - 局部 const camelCase（ipOffset/required/offset）被判应 PascalCase；
  - internal readonly 字段 `_camel`（NativeLease._pool）被判"不应带 `_`"（因 non_private_readonly→PascalCase），而 internal 字段 PascalCase（SetupExecutor.Handler 等）又被判"缺 `_`"——配置自相矛盾。
- 项目惯例（`.trellis/spec/backend/quality-guidelines.md`）：抑制必须局部、附文档理由（`#pragma` 仅用于行为有意且已文档化时）；结构性改动行为零变更，门禁 = 零警告构建 + 测试基线全绿（724，2026-09-19 起）。

## Requirements

- R1 格式缺陷（whitespace/final newline/imports）直接修复（`dotnet format whitespace` 自动修复 + diff 审查为默认机制）。
- R2 分析器/IDE 诊断逐条分析：听从警告改代码确能提升质量/可维护性 → 改代码；反之可抑制。
- R3 抑制必须最小范围：个案用局部 `#pragma`（附理由），系统性不兼容用 `.editorconfig` 规则级条目（附理由注释）；无理由不抑制。
- R4 最终验收目标（范围已定：**整个 solution**，src+tests+benchmarks）：`dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` 返回 0。
- R5 行为零变更：构建零警告；`dotnet test` 全绿且 ≥ 基线总数 724（新增测试需说明）。
- R6 收尾必须单独派遣独立代理（非实现者）审查**全部**被忽略/抑制的诊断必要性（范围已定：本任务新增 + 既有 36 条 editorconfig 抑制 + 所有 pragma；实证法验证，含处置），产出审查结论。

## Acceptance Criteria

- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` exit 0
- [ ] `dotnet build` 零警告
- [ ] `dotnet test` 全绿，总数 ≥ 724
- [ ] 每条新增/变更抑制均有书面理由（editorconfig 注释或 pragma 注释）；判断型决定记录于 `research/disposition-log.md`
- [ ] 独立审计代理报告 `research/suppression-audit.md` 已产出，覆盖全部抑制（新增 + 既有 36 条 + pragma），不必要项已处置并复验 exit 0
- [ ] 无功能性行为变更

## Out of Scope

- 功能性修改与性能优化（除非诊断指向且改动安全等价）
- 与诊断清零无关的代码重构

## Decisions（已定）

- 验收范围：整个 solution（1369 条：src 426 / tests 762 / benchmarks 181）——命令原样跑通。
- 审计范围：全部抑制（本任务新增 + 既有 36 条 + 全部 pragma），实证法逐条裁定，含处置。
- IDE1006（230 条）：**按用户裁定约定**——public/protected（可能公开暴露）字段 PascalCase（公开字段出于性能允许保留）；internal 与 private 同级（static → s_camelCase/t_，实例 → _camelCase）；const（private/internal）PascalCase、局部 const camelCase。共 **87 处改名**（private static 66 + internal static 9 + internal 实例 12）；执行流按用户指示：**先改 editorconfig，再跑 dotnet format**（IDE1006 fixer 应用改名）；其余 143 处经配置修正零改名归零。清单见 design.md §2 N 与 research/naming-inventory.md。
