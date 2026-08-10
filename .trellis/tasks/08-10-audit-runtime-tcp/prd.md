# 审查 Runtime TCP 透明重定向

## Goal

验证透明 TCP redirect 从 SYN 到 relay teardown 的状态机对重传、反向包、origin、地址族、并发和失败保持 exactly-once 与 fail-closed，不破坏客户端 TCP 序列空间。

## Scope and Requirements

- R1: 逐文件审查 `TcpProxyCoordinator.cs`、`TcpProxyRelay.cs`、`TcpRedirectInjector.cs`、`TcpRedirectInterfaces.cs`、`TcpRedirectListener.cs`、`TcpRedirectTable.cs`。
- R2: 审核 SYN/retransmit、listener accept、alias claim/lookup、reverse/data rewrite、forwarded/host injection、relay half-close/error、expiry、self-traffic 和所有 resource/token 的所有权。
- R3: 对并发 first packet 建立使用 barrier/TCS 与可观察结果的回归；不得以 `Task.Yield()` 调度结果证明 safety。
- R4: 报告 `research/audit-findings-audit-runtime-tcp.md` 需要把协议 rewrite contract 交叉引用到 protocols 子任务，并给出 Windows TCP gate。

## Acceptance Criteria

- AC1: 六个拥有文件全覆盖，original/translated alias 和 socket/CTS 生命周期有 `file:line` 证据。
- AC2: confirmed bug 有 deterministic regression；测试覆盖 retransmit、reverse、host/forwarded direction、failure、collision、cleanup 与 address-family 隔离或缺口。
- AC3: 报告明确 Linux seam 与 Windows listener/SOCKS/NDIS 实际验证之间的界线。
- AC4: focused、全套 Release build/test、diff check 和相关 Windows gate 结果记录。

## Out of Scope and Dependencies

- packet header/checksum RFC oracle 由 protocols 子任务所有；本项只验证调用和 state-machine 语义。
- 依赖 core/config、protocols、capture-flow 与 Windows adapter/direction contract；不得擅改这些拥有文件。
