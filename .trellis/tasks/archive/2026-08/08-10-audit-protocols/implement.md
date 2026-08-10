# Protocols 执行计划

1. 建立六文件 API、wire input、现有测试和 Runtime caller 表；核对归档协议修复是否仍存在。
2. 为 IPv4/IPv6/TCP/UDP parser、fragment/extension、rewrite/checksum 和 SOCKS5 state/UDP codec 创建边界矩阵。
3. 用独立 checksum/offset oracle 添加回归，确认 defect 后最小修复；每个拒绝路径验证 input 未变。
4. 运行 `TcpEndpointRewriteTests`、`UdpRelayTests` 和协议相关 `FlowAndConfigurationTests` filter，再全套 Release build/test/diff check。
5. 写报告并把调用协议契约交给 TCP/UDP runtime 子任务。
