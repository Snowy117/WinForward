# 执行计划：质量门禁与工具链现代化

> 依据 `prd.md` 与 `design.md`。每阶段结束工作区可提交，便于单独回退。

## P1 · 宿主入口与配置契约（AC1、AC4）

1. `flake.nix` 的 devShell 增加 `cppcheck`。
2. 新建 `wf.local.example.json`（字段见 design §2.2）；`.gitignore` 增加 `wf.local.json`、`local/`。
3. 新建 `tools/check.sh`：参数（`--fast` / `--full` / `--staged` / `--build-dir`）、环境自检、逐步 `PASS|FAIL|SKIP`、汇总、退出码 0/1/2。
4. 接入格式检查。
5. `check-effective-lines.py` 默认目标改为 `src/` + `tests/`。
6. README 增加"质量门禁"一节：devShell、`tools/check.sh`、本地配置放哪。

验证：

- `tools/check.sh --fast` → 全 PASS，退出码 0。
- 改乱一处缩进 → 退出码 1 并指出文件。
- 新建 501 行的 `.cpp` → 有效行报出；删除后恢复。
- 删除 `wf.local.json`（本来就不存在）→ 不产生任何报错。

## P2 · cppcheck 与死符号收尾（AC2、AC3）

1. 接入 `cppcheck --enable=unusedFunction ...`（design §4.4）。
2. 先在未修改代码上跑：**期望恰好报出 `block_classification`**；对 `redirect_context.h` 的 `try_read_original_destination` 加 `// cppcheck-suppress unusedFunction` 与理由。
3. 删除 `block_classification`。
4. 把 `mutation-test.ps1` 的匹配改为区域标记，先只改依赖死函数的那一条；其余用例留给 P4 一起改。

验证：

- `tools/check.sh --fast` → PASS。
- 注入 `namespace { void wf_probe_dead() {} }` → 退出码 1，报出文件与符号；删除后恢复。
- `tools/check.sh --full` → PASS。

## P3 · 驱动静态分析（AC5、AC9）

1. `wfprobe.vcxproj`：`<WarningLevel>Level4</WarningLevel>`、`<TreatWarningAsError>true</TreatWarningAsError>`。
2. 在 `WfBuild.psm1` 之前先用命令行验证 PREfast 口径（`RunCodeAnalysis=true` + `PreferredToolArchitecture=x64`）。
3. 按 design §4.6 清理 10 条基线：注解 6 条、抑制 4 条（带理由）。

验证：

- `/WX` 构建 → 0 告警。
- 注入 `static void wf_probe_dead() {}` → `C2220` + `C4505`。
- 分析构建 → 0 告警；摘掉一个 `_Dispatch_type_` → C28169。

## P4 · Windows 侧脚本与机器解耦（AC6）

1. `tools/vm/` → `tools/windows/`，删除 `sync.sh`（其职责归 `local/`）。
2. 新建 `WfBuild.psm1`：`Resolve-MSBuild`、`Resolve-SignTool`、`Build-Driver`、`Install-Driver`、`Uninstall-Driver`、`Invoke-Prefast`、`Invoke-WfSuite`。
3. `build-and-test.ps1` → `gate.ps1`，`mutation-test.ps1` → `mutation.ps1`，`load-driver.ps1` 保留；三者改为薄壳。
4. 参数：`-Root` 默认 `$PSScriptRoot\..\..`；`-Remote` 取 `-Remote` → `$env:WF_REMOTE` → 报错说明。
5. 全部变异用例改用区域标记。
6. 分析构建的 PASS 判定解析 `warning C28xxx` 或 `Warning(s)` 计数，不依赖 MSBuild 退出码（design §4.6）。

验证：

- `git grep -nE "windows-vm|C:\\\\wfbench|10\\.0\\.26100|Visual Studio\\\\18|1\\.2\\.3\\.4"` → 零命中。
- 在 Windows 机器上 `pwsh tools/windows/gate.ps1 -Remote <地址>` → `GATE-RESULT=PASS`。
- `mutation.ps1` → 全部 PASS，结束后 `git status` 干净。

## P5 · 禁止模式、钩子与文档（AC7、AC8）

1. `tools/lint/forbidden.py` + `forbidden-rules.txt`（design §4.5）。
2. 接入 `--fast`；`.githooks/pre-commit` 调 `--fast --staged`，`.githooks/install.sh` 设置 `core.hooksPath`。
3. 更新 `cpp-conventions.md` §8：门禁清单、豁免语法、`local/` 约定；`quality-guidelines.md` 的空模板换成指针。

验证：

- 在 `rule_table.cpp` 写 `std::vector<int> x;` → 退出码 1；加 `// WF-LINT-ALLOW(stl): ...` → 通过；删除。
- 安装钩子后制造格式漂移并 `git commit` → 被拦下。
- `time tools/check.sh --fast` < 5 s。

## P6 · CodeQL（AC10）

1. 前置：Windows 机器上装好 CodeQL bundle，路径写进 `codeql_home` 或 PATH（不进仓库）。
2. `tools/windows/codeql.ps1`：`database create`（`--command` 指向 P3 的 msbuild 调用）→ `database analyze --format=sarif-latest` → SARIF 落 `build/codeql/`。
3. 先报告，不阻断；清完 mustfix 后再决定是否纳入 `gate.ps1`。

验证：

- 一次建库 + 分析成功，SARIF 非空。
- 注入 pool tag 非字面量 → 结果出现 `cpp/drivers/default-pool-tag` 一类查询命中。
- 记录耗时，确认不进快速档。

## P7 · 全量回归

1. `tools/check.sh --full`
2. Windows 上 `gate.ps1` 与 `mutation.ps1`
3. Windows 上 `codeql.ps1`

按 AC1–AC10 逐条勾选。

## 风险文件与回滚

| 文件 | 风险 | 回滚 |
|---|---|---|
| `src/driver/probe_classify.cpp` | 删函数 + 标记 | 单函数回退 |
| `src/driver/wfprobe.vcxproj` | 构建门禁 | 删两行 |
| `tools/windows/*.ps1` | 日常调试入口 | 单独一次提交，可整体回退 |
| `tools/lint/*` | 误报 | 整目录删除 |
