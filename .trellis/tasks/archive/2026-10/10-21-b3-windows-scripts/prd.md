# B3 · Windows 侧脚本去机器耦合

> 父任务：`.trellis/tasks/10-21-quality-gates-modernization`（design §2、§5 是权威设计）

## Goal

仓库里的 Windows 侧脚本只回答"在这台装有 VS + WDK 的机器上跑什么"，不再出现任何具体机器的痕迹；PREfast 分析接入门禁，且判定不依赖 MSBuild 退出码。

## Scope

- `tools/vm/` → `tools/windows/`（`git mv`），删除 `sync.sh`。
- 新增 `tools/windows/WfBuild.psm1`：`Resolve-MSBuild`、`Resolve-SignTool`、`Build-Driver`、`Install-Driver`、`Uninstall-Driver`、`Invoke-Prefast`、`Invoke-WfSuite`。
- `build-and-test.ps1` → `gate.ps1`；`mutation-test.ps1` → `mutation.ps1`；`load-driver.ps1` 保留；三者改为导入模块的薄壳。
- `firewall-check.ps1`、`userland-e2e.ps1`、`userland-socks5-e2e.ps1`：去掉硬编码路径，更新对 `tools\windows\` 的引用。
- 根 `README.md` 与 `tools/windows/README.md` 同步。

## Acceptance

- **A1** `git grep -nE "windows-vm|C:\\\\wfbench|1\\.2\\.3\\.4|Visual Studio\\\\18|10\\.0\\.26100" -- tools README.md` 零命中。
- **A2** 工具一律发现式：MSBuild 用 `vswhere -products *`，signtool 用 `Windows Kits\10\bin\*\x64\signtool.exe` 取最高版本；`-Root` 默认 `$PSScriptRoot\..\..`；`-Remote` 无默认值，取参数 → `$env:WF_REMOTE` → 报错说明。
- **A3** `gate.ps1 -Root <仓库根> -Remote 1.2.3.4:19003` 在 Windows 机器上跑完，输出 `GATE-RESULT=PASS`。
- **A4** gate 的 PREfast 判定不能只看退出码：临时摘掉一个 `_Dispatch_type_` 时，`gate.ps1` 必须以非零退出（分析输出里出现 `warning C28xxx` 即失败）。
- **A5** `mutation.ps1` 改名后仍能工作：至少跑两条用例（含 `-OnlyIndex`），并确认跑完源码恢复原样。
- **A6** 调用方式向后兼容：`-Root` / `-Remote` 参数名不变，旧的单独脚本仍可独立执行。

## Out of Scope

- 变异锚点改成区域标记（B4）。
- CodeQL（B5）、禁止模式与钩子（B4）。
- C# 工具本身的硬编码（`tools/wfprobe`）。

## 已知偏差

- 缺 `tools/wfprobe/wfprobe.csproj` 时 `gate.ps1` / `mutation.ps1` 仍以 1 退出（普通异常路径），未统一成环境错误 2；其余环境错误场景均为 2。留待后续批次统一。
