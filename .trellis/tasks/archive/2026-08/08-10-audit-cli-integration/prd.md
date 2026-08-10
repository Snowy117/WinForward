# 审查 CLI 组合与 Windows 集成门禁

## Goal

确认 CLI 将 configuration、driver、adapter scope、capture、runtime coordinators 和 shutdown 以正确顺序组合，且可观察的 command/exit/log/redaction 与 Native AOT/Windows 操作门禁有完整测试或明确缺口。

## Scope and Requirements

- R1: 审查 `src/WinForward.Cli/Program.cs` 的所有命令、参数、异常、start/stop/dispose、logging 与 composition root 路径。
- R2: 交叉验证 validate/adapters/run 的 exit code、配置在 driver 打开前完成验证、adapter selection、owned resource 的逆序释放、Ctrl+C、credentials redaction 与 fail-closed error output。
- R3: 采用可行的 process-level 或 seam tests；无法在 Linux 实现的 driver/Native AOT/real traffic 行为定义 Windows commands 和判据。
- R4: 写 `research/audit-findings-audit-cli-integration.md`，链接各子任务 contract/finding，不重新拥有其生产源码。

## Acceptance Criteria

- AC1: `Program.cs` 的所有 command 和 startup/shutdown 分支有 `file:line` 审读结论。
- AC2: confirmed bug 有绿色 regression；CLI 当前不可测行为以 coverage gap 和 Windows gate 列出。
- AC3: 报告包括 build/test/publish、validate/adapters/run、Ctrl+C cleanup、proxy outage 和 Windows/Hyper-V 矩阵的执行状态。
- AC4: Release build/test、diff check 和适用的 Windows publish/smoke 结果被记录。

## Out of Scope and Dependencies

- 不改变 CLI 配置语法/exit-code contract；若必须改变，交父任务获得用户决定。
- 本项最后执行，消费所有前序 audit 的 contracts/findings，只修改 `Program.cs` 和本项新增测试/报告。
