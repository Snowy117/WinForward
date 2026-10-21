# B2 · 驱动警告即错误与 PREfast

> 父任务：`.trellis/tasks/10-21-quality-gates-modernization`（design §4.6 是权威设计）

## Goal

驱动在 `/W4` + `/WX` 下零告警构建；PREfast 的 10 条基线全部消解（注解或带理由抑制），并把分析调用口径固化成可复现命令。

## Scope（只改这些）

- `src/driver/wfprobe.vcxproj`：`<WarningLevel>Level4</WarningLevel>`、`<TreatWarningAsError>true</TreatWarningAsError>`。
- `src/driver/*.cpp` / `*.h`：只加 SAL 注解与带理由的 `#pragma warning(suppress: ...)`，**不改运行逻辑**。
- `tools/check.sh`：本批范围例外——仅补 SAL 宏的 cppcheck 空定义（消除 unknownMacro 级联）。
- 不改 `tools/vm/*`（B3 范围）、不改用户态、不改变异标记。

## Acceptance

- **A1** 普通构建（`-t:Rebuild`）0 warning，且警告即错误生效。
- **A2** 注入未引用 `static` 函数 → 构建失败（`C2220` + `C4505`）。
- **A3** PREfast 分析 0 告警（基线 10 条：C28175×4、C28169×3、C28101×1、C28167×2 全部消解或抑制并写明理由）。
- **A4** 摘掉任意一个 `_Dispatch_type_` → 分析出现 `C28169`，门禁失败。
- **A5** 产物可加载：签名 → 安装 → 启动到 RUNNING → 停止 → 删除，全流程退出码 0。
- **A6** 不改动任何运行逻辑：diff 里不出现条件分支、返回值、控制流的改变（注解与抑制除外）。

## Out of Scope

- Windows 脚本重构与去硬编码（B3）。
- 变异测试区域标记（B3）。
- CodeQL（B5）。
