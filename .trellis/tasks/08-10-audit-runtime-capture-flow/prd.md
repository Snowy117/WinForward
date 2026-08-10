# 审查 Runtime 捕获、流与生命周期

## Goal

验证从 captured frame 到 pass/block/proxy/reinject 的 ownership、方向、loop prevention 和关闭流程；尤其防止 packet 被重复/遗漏处置或 adapter/driver 在 handler 未结束时恢复/关闭。

## Scope and Requirements

- R1: 逐文件审查 `CaptureAdapterScopeResolver.cs`、`CaptureLifecycle.cs`、`CapturePacketProcessor.cs`、`FlowDispatcher.cs`、`IdleExpirySweeper.cs`、`MultiAdapterCaptureLoop.cs`、`NdisAdapterModeController.cs`、`NdisPacketActionExecutor.cs`、`NdisPacketReinjector.cs`、`PacketFlowClassifier.cs`、`SelfTrafficRegistry.cs`。
- R2: 追踪 lease/packet 从 pump、classification、self-traffic、reverse hook、policy/flow cache 到 terminal action 的 exactly-once 结局；审查 cancellation、exception、expiry 与 stop/restore 顺序。
- R3: 复核既有“pump 不被 await 即恢复 adapter/释放 driver”线索，只有能从当前代码重现/证明时才报告为 confirmed bug。
- R4: 每个可测 confirmed bug 有绿色回归；报告 `research/audit-findings-audit-runtime-capture-flow.md` 还须列出无 OS/hardware 情况下不可证明的 coverage gap。

## Acceptance Criteria

- AC1: 11 个拥有文件逐一在报告中有 `file:line` 结论和调用链。
- AC2: 测试或明确缺口覆盖 pass/block/proxy、reverse、self traffic、cancel/failure、duplicate packet、adapter direction、expiry 与 stop/dispose。
- AC3: confirmed defect 有 severity/type/最小复现/修复/回归测试；不把已有 fix 重复上报。
- AC4: focused、全套 Release build/test、diff check 和必要 Windows gate 被报告。

## Out of Scope and Dependencies

- 先读取 Core/Protocols/NDISAPI/Windows 的契约；不承担 TCP/UDP coordinator 内部状态机的修复，但记录跨界引用。
- 若修复会改变 proxy fail-closed 或 Windows packet direction contract，退交父任务协调。
