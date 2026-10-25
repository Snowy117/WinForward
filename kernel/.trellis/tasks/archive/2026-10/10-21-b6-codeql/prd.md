# B6 · CodeQL 深度档接入

> 父任务：`.trellis/tasks/10-21-quality-gates-modernization`（design §6）

## Goal

在 Windows 侧用 CodeQL 对驱动建库并跑 `microsoft/windows-drivers` 的 recommended 套件，产出 SARIF；脚本可复现、工具发现式、缺依赖时有清晰报错。作为**独立深度档**，不进 `gate.ps1` 的默认流程。

## Scope

- 新增 `tools/windows/codeql.ps1`。
- `README.md`（根与 `tools/windows/README.md`）补用法与前置条件。
- 不改 `gate.ps1`、不改宿主 `tools/check.sh`、不新增查询。

## Acceptance

- **A1** 工具发现：`-CodeqlHome` → `$env:CODEQL_HOME` → PATH；都找不到时输出可读的环境错误并 exit 2（不是 1）。
- **A2** 建库：追踪一次 MSBuild 驱动构建生成 CodeQL 数据库；命令里带 `-restore`，在干净同步的树上可复现。
- **A3** 分析：跑 `microsoft/windows-drivers:windows-driver-suites/recommended.qls`，SARIF 非空；查询包缺失时能自行 `codeql pack download`（含 `microsoft/cpp-queries@0.0.5` 这个必须显式补的依赖）。
- **A4** 能变红：向驱动注入一个 mustfix 类缺陷（如 `if (x + 1 < x)` 触发 `cpp/bad-addition-overflow-check`），SARIF 必须出现对应结果；恢复后回到基线。
- **A5** 成本与位置：记录建库/分析耗时；默认不执行，README 写清前置条件（bundle 不在仓库内）、用法与输出位置（`build/codeql/`）。

## Out of Scope

- 上传 SARIF 到 GitHub（无 remote、无 GHAS）。
- 自定义查询或调整 suite。
- 把 CodeQL 接入 `gate.ps1` 默认流程。

## 风险

- 许可：当前仓库非开源，CLI 许可不覆盖；用户裁定先部署，风险记录在案，仓库开源后自然消解。
