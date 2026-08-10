# NDISAPI 执行计划

1. 建立三文件的 native symbol/layout/request/owner 表，交叉读取 Windows NDISAPI 规范与调用点。
2. 审读 buffer length、struct packing、last-error、open/close/exception 与 pump stop 路径；复核 enumeration handle 不等于 captured adapter pointer。
3. 对可纯测 bug 添加 ABI 或 fake seam regression 并最小修复；需要 driver 的问题写出 Windows 命令和判据。
4. 运行 `dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~NdisApiAbiTests'`、全套 Release build/test/diff check。
5. 记录 report/coverage gaps，并将 lifecycle/handle finding 交给 runtime-capture-flow 与 CLI integration。
