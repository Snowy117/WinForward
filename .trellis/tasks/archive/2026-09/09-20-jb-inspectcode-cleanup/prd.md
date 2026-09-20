# PRD: 引入 JetBrains inspectcode 检查器并清零报告

Task: `.trellis/tasks/09-20-jb-inspectcode-cleanup`
Base commit: `74cbb23` (master)

## Goal

引入 JetBrains inspectcode（`jb` CLI，2026.1.3）作为质量检查手段，并以用户指定命令清零其报告：
`jb inspectcode -f=Xml -e=HINT -o=<path> WinForward.slnx`（`-e/--sEverity` = 最小报告严重级；HINT = 报告 HINT 及以上全部，见 `jb inspectcode --help`）。每条报告项逐一处置：能提升质量则修代码，否则合理抑制并附书面理由。最终由独立代理审计全部抑制（沿用 09-19-src-analyzer-cleanup 的模式）。

## 红线（用户明确要求）

- **性能优先**：会显著影响性能的更改不应用（以 hot-path.md 与分配门禁测试为准绳）。
- **可读性**：invert-if 等会显著降低可读性的改写不采用（抑制 + 理由）。
- 行为零变更：Release build 0 警告、725/725 测试全绿、`dotnet format --severity info --verify-no-changes` 保持 exit 0（上一任务的验收不得回退）。

## Background（已核实事实）

- `jb` 工具可用：`/home/paff/Projects/WinForward/.direnv/dotnet-tools/jb`（JetBrains Inspect Code 2026.1.3，.NET 10.0.12）。
- 命令语义：`-f=Xml` 输出 XML；`-e=HINT` 最小严重级 HINT（级别序 [INFO, HINT, SUGGESTION, WARNING, ERROR]）；默认 `--build` 会先构建（Debug 配置）。
- 抑制机制（预期）：代码内 `// ReSharper disable ...` 注释，或 `.editorconfig` 的 `resharper_<inspection>_highlighting = none` 键（仓库已有先例：`resharper_arrange_trailing_comma_*_highlighting = none`）。
- 环境噪音（非致命）：jb 日志反复出现 Roslyn worker 无法加载 `System.Composition.AttributedModel`（.NET 10 SDK 的 NetAnalyzers MEF fixer 实例化失败）——不影响 jb 自身检查的运行；但报告可能缺少部分 CA fixer 支撑项（CA 类已由 dotnet format 体系覆盖）。
- 基线清单已固化：1317 条 / 75 规则 / 194 文件（`research/jb-inventory.md`；原始 XML `research/jb-inspectcode-report.xml`；扫描日志 `/tmp/jb-run.log`）。

## Requirements

- R1 固化扫描清单（XML + 解析后的规则/项目维度统计）到 `research/`。
- R2 逐条处置：修复优先（用户红线内）；不应用时抑制——优先局部 `// ReSharper disable`（附理由），系统性用 `.editorconfig` `resharper_*_highlighting = none`；抑制按证据路径用 glob 限定（延续上次的用户裁定原则）。
- R3 判断记录：逐条/逐规则决定写入 `research/disposition-log.md`（FIX / SUPPRESS-LOCAL / SUPPRESS-GLOBAL + 理由）。
- R4 性能红线核查：所有源自将进入 hot-path 的修改，须通过分配门禁与既有测试；明显影响性能的规则/站点一律抑制并记录。
- R5 可读性红线：invert-if 类改写若显著降低可读性则不采用（抑制+理由）。
- R6 门禁：`dotnet build -c Release` 0 警告；`dotnet test -c Release` ≥725 全绿；`dotnet format ... --verify-no-changes` exit 0；**`jb inspectcode -f=Xml -e=HINT ... WinForward.slnx` 报告为空**（验收命令）。
- R7 最终独立代理审计全部抑制（含既有 resharper 相关条目）的必要性，产出 `research/suppression-audit.md`。

## Acceptance Criteria

- [ ] 指定 `jb inspectcode` 命令对 solution 的报告为空（exit 0 / 无 issue）
- [ ] `dotnet format ... --verify-no-changes` 仍 exit 0；Release build 0 警告；测试 ≥725 全绿
- [ ] 全部抑制有书面理由；disposition-log 完整
- [ ] 独立审计报告产出；不必要项已处置并复验
- [ ] 无功能性行为变更；无显著性能退化

## Decisions（已定）

- 基线清单：1317 条 / 75 规则 / 194 文件（WARNING 681 / HINT 449 / SUGGESTION 187；Tests 711、Runtime 327、Benchmarks 100…），见 `research/jb-inventory.md` + `research/jb-inspectcode-report.xml`。
- Q1 已决（用户）：**门禁+CI 全套** —— jb inspectcode 写入 AGENTS.md 提交前门禁，并接入 CI `Analyzer Gate`（PR/push master；固定工具版本 2026.1.3；CI 以 XML 解析断言零 issue）。
- Q2 抑制默认机制：`.editorconfig` `resharper_<inspection>_highlighting = none`（glob 限定）为主、局部 `// ReSharper disable` 注释为辅——沿用仓库既有先例与上次 glob 原则；每个新键必须实测生效。
- Q3 验收口径：固定为用户指定命令 `-e=HINT`（HINT 及以上全部报告）。

## Out of Scope

- 与 jb 报告无关的重构；新功能；性能优化本身（除非检查项自身且安全等价）。
