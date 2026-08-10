# UDP / SOCKS runtime 执行计划

1. 建立四文件的 key/alias/socket/token/activity owner 与现有 tests/callers 表，复核归档 UDP/SOCKS fixes。
2. 追踪 control connect、多地址失败、reply normalize、ASSOCIATE、relay IO、response reconstruction、self-traffic、concurrent setup/cancel/expiry 分支。
3. 对确认 defect 用 deterministic fake/TCS 或 pure endpoint fixture 先写 regression，再最小修复；跨模块协议问题移交 protocols。
4. 运行 `UdpProxyCoordinatorTests`、`UdpRelayTests`、SOCKS 相关 `FlowAndConfigurationTests`，再全套 Release build/test/diff check。
5. 写 finding/coverage report，并对影响的 host/forwarded Windows UDP/SOCKS 路径执行或标注 hardware gate。
