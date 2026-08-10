# 审查 NDISAPI ABI 与捕获边界

## Goal

确认 Native AOT/NDISAPI interop、driver handle 与 capture pump 的布局和所有权正确，防止使用错误 adapter handle、读取越界或在 native 资源关闭后继续操作。

## Scope and Requirements

- R1: 审查 `src/WinForward.NdisApi/NdisApiAbi.cs`、`NdisApiDriver.cs`、`NdisCapture.cs`，以及其与 Runtime 的调用边界。
- R2: 验证 layout/pack/offset/帧上限、`LibraryImport`/last error、SafeHandle、open/close、read/send request 和枚举 handle 与 captured pointer 的分离。
- R3: Linux 覆盖可证明的 ABI/纯边界；真实 DLL/driver 行为按需在 Windows 机器验证，不能以 mock 替代硬件结论。
- R4: 输出 `research/audit-findings-audit-ndisapi.md`，每个 finding/coverage gap 带可执行或明确 pending 的复现路径。

## Acceptance Criteria

- AC1: 三个拥有文件全部有 `file:line` 审读记录；native 约束与已验证的 adapter-handle contract 相符。
- AC2: 所有可纯测的 confirmed bug 有绿色回归；Windows-only finding 明确 driver/DLL/命令/预期观测。
- AC3: 报告覆盖 native error、frame size、buffer/handle 生命周期和 dispose/cancellation。
- AC4: focused ABI 测试、全套 Release build/test 与 diff check 的结果被记录。

## Out of Scope and Dependencies

- 不更换 driver、DLL 或 ABI 支持版本；兼容性变更须交回父任务。
- 发现会影响 capture shutdown/packet direction 的 contract 交给 runtime-capture-flow；本项不改 Runtime。
