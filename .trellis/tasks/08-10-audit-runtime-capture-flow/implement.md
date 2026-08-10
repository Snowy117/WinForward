# Runtime capture / flow 执行计划

1. 从 `Program` 与 NDIS pump 反向建立 11 文件的 frame/CTS/handle ownership 图，标记终结路径。
2. 审读 classifier、dispatcher/reverse/self-traffic、executor/reinjector、mode controller、sweeper、capture loop/lifecycle 的 error/cancel/dispose 分支；复核归档 lifecycle finding。
3. 为确认的路径补 deterministic lifecycle/dispatcher/capture tests，再实施最小修复；将 TCP/UDP 内部问题移交对应所有者。
4. 运行 `CaptureLifecycleTests`、`CapturePipelineTests`、`FlowDispatcherTests`、全套 Release build/test/diff check。
5. 写报告和 coverage gaps；若动到 native/send 路径，执行或明确标注 Windows gate。
