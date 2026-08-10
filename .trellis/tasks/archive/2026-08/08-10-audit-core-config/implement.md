# Core / Configuration 执行计划

1. 建立六文件调用/测试表，阅读当前及归档的 policy/config 契约。
2. 对 selector、CIDR、端点与 flow key 做同值/异值、IPv4/IPv6、origin/generation 边界追踪；对配置做 omitted/empty、重复、大小写、范围、未知字段和 redaction 输入矩阵。
3. 对确认 bug 先添加稳定的 focused test，再作最小修复；不改变 JSON schema/公共行为。
4. 写入 finding/coverage 报告，运行 `dotnet test WinForward.slnx -c Release --filter 'FullyQualifiedName~FlowAndConfigurationTests'`、全套 Release build/test 和 `git diff --check`。
5. 将影响 Runtime 的 contract/finding 交叉引用给父汇总，不直接改 Runtime 文件。
