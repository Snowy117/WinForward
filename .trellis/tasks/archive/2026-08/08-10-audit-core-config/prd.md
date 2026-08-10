# 审查 Core 与配置契约

## Goal

确认 Core 领域/策略/流键与 JSON 配置加载是否在边界条件下保持可预测、fail-closed 且不泄露凭据；将可复现的 defect 修复并用回归测试锁定，同时记录实际覆盖缺口。

## Scope and Requirements

- R1: 逐文件审查 `src/WinForward.Core/Domain.cs`、`IpPrefix.cs`、`PacketRuntime.cs`、`Policy.cs`、`ProcessSelectors.cs` 与 `src/WinForward.Configuration/ConfigurationModels.cs`，追踪其被 Runtime/CLI 使用的契约。
- R2: 验证规则顺序、IP prefix、端点和 flow-key 标识、origin/adapter generation、进程 selector 路径语义、配置 normalization、端口/CIDR/server/rule 校验、未知字段和凭据诊断。
- R3: 每个可单测重现的 confirmed bug 必须有绿色回归测试；优先新增模块专属测试而不大幅改写共享测试。
- R4: 生成 `research/audit-findings-audit-core-config.md`，包含每个拥有文件、findings/test 映射、coverage gaps 与无 bug 结论。

## Acceptance Criteria

- AC1: 六个拥有文件都在报告中以 `file:line` 结论覆盖。
- AC2: 所有确认 defect 都附 severity/type/证据/复现/修复/回归测试；可单测的均绿。
- AC3: policy/config 的 omitted vs empty、大小写/路径、IPv4/IPv6、范围、未知 JSON 字段、UTF-8 认证长度与 redaction 覆盖状态明确。
- AC4: focused 测试、Release build/test 和 `git diff --check` 结果写入报告。

## Out of Scope and Dependencies

- 不改变配置格式或 policy 语义；若修复需要这样的兼容性改变，交回父任务规划。
- 本项可先行，但其报告是 runtime-capture-flow、runtime-tcp 与 runtime-udp-socks 的输入。
