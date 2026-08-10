# Windows 边界执行计划

1. 建四文件 -> native input -> projected output -> Runtime consumer 表，读取已验证的 Windows NDISAPI adapter 规范。
2. 审读 GUID normalization、MAC ambiguity、IP Helper 缓冲/offset、IPv4/IPv6 scope、PID creation-time/path lookup 和 unknown owner fallback。
3. 增加可运行的 projection/ABI regression；对 host-only 行为写 Windows smoke steps，确认 defect 后再最小修复。
4. 运行 `AdapterSelectorTests`、相关 `NdisApiAbiTests`、全套 Release build/test/diff check。
5. 写 report/coverage gaps，交叉引用影响 dispatcher/CLI 的 contract。
