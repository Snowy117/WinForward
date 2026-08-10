# 审查 Runtime UDP 与 SOCKS 控制

## Goal

验证 SOCKS5 control socket、UDP ASSOCIATE/relay、动态 alias 与 response reinject 在并发、取消、地址边界、过期和 host/forwarded origin 下不回环、不串流且 fail-closed。

## Scope and Requirements

- R1: 逐文件审查 `Socks5Client.cs`、`UdpAssociations.cs`、`UdpProxyCoordinator.cs`、`UdpResponseReinjector.cs`。
- R2: 审核 DNS/connect、DNS 多地址、SOCKS greeting/auth/endpoint reply、control registration-before-SYN、unspecified/mapped/scope address、association claim、relay port、wildcard self-traffic、session activity/expiry 与回注方向。
- R3: 共享 setup 的 caller cancellation 不得错误 teardown；alias collision 与并发 flow 必须决定性地拒绝或复用正确 session。
- R4: `research/audit-findings-audit-runtime-udp-socks.md` 必须包含每个 finding/test 映射以及 Linux/Windows gate 的界线。

## Acceptance Criteria

- AC1: 四个拥有文件全部有 `file:line` 结论，control/UDP socket/token 的获取释放路径可追踪。
- AC2: confirmed bug 有稳定绿色 regression；tests 或 gap 明确覆盖 relay dynamic port、IPv4/IPv6/mapped/unspecified/scope、reverse packet、host/forwarded、cancel/expiry/collision。
- AC3: 代理失败始终 fail-closed，不会误将选择的 flow pass 或递归代理自身流量。
- AC4: focused、全套 Release build/test、diff check 和必要 Windows UDP gate 结果记录。

## Out of Scope and Dependencies

- SOCKS frame 编解码/RFC byte oracle 属 protocols 子任务；capture disposition 属 capture-flow 子任务。
- 依赖 Core flow-key、Protocols、Windows direction/adapter 和 dispatcher reverse/self-traffic contract；不并行改共享测试大文件。
