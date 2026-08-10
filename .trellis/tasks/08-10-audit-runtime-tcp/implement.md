# TCP redirect 执行计划

1. 建立六文件状态转换、tuple index、socket/token owner、现有测试图；读取 protocol 和 NDIS direction 契约。
2. 逐一审读 SYN/retransmit、accept/drain、reverse/data、alias collision、relay pump、cancel/expiry/dispose；验证 address family/origin/generation 约束。
3. 以 barrier/TCS 为 race regression，确认 bug 后增加最小测试和最小修复；不在本项实现 protocol helper 改动。
4. 运行 `TcpProxyCoordinatorTests`、`TcpRedirectInjectorTests`、全套 Release build/test/diff check。
5. 写 findings/coverage report，按变更需要执行 host 与 forwarded Windows TCP gate 或标为 pending。
