# 审查 Windows 归因与适配器边界

## Goal

确认 Windows 适配器关联、IP Helper ABI 与进程归因在歧义、地址族、进程重用和不可访问条件下不猜测、不串流，且有清晰的宿主无关和 Windows-only 测试边界。

## Scope and Requirements

- R1: 逐文件审查 `src/WinForward.Windows/AdapterIdentity.cs`、`IpHelperAbi.cs`、`Platform.cs`、`ProcessAttribution.cs`，并检查其 Core/Runtime 调用语义。
- R2: 验证 GUID-primary adapter matching、MAC fallback ambiguity、IPv4/IPv6 owner-table 解码、scope/address/port/creation time、PID reuse、path resolution 与错误/unknown owner 处置。
- R3: 每个纯逻辑/投影缺陷配回归；原生 IP Helper 结论使用 Windows gate，不将 Linux mock 误称为硬件验证。
- R4: 输出 `research/audit-findings-audit-windows.md`，列出四文件结论、tests 和 coverage gaps。

## Acceptance Criteria

- AC1: 所有拥有文件有精确 `file:line` 审读记录，且与 GUID-primary 合同一致。
- AC2: adapter ambiguity、IPv4/IPv6 table、wildcard/reused endpoint、PID/path 失败场景的覆盖或缺口明确。
- AC3: confirmed bug 的 regression 绿色；Windows-only cases 有环境、命令和预期结果。
- AC4: focused 测试、全套 Release build/test 和 diff check 被记录。

## Out of Scope and Dependencies

- 不承诺 NDIS/IP Helper 无法提供的 per-socket UDP 归因；保持 documented unknown/fallback 行为。
- 输出的 adapter/origin/attribution contract 为 capture-flow 与 CLI integration 的依赖。
