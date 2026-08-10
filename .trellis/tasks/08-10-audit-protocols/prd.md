# 审查协议解析、校验和与 SOCKS5

## Goal

验证所有 packet/SOCKS5 字节处理在截断、畸形和正常 IPv4/IPv6 情形下符合其声明协议契约，且拒绝路径绝不改写输入；修复确认问题并量化未覆盖 wire 行为。

## Scope and Requirements

- R1: 审查 `src/WinForward.Protocols/IpTcpUdpPacket.cs`、`IpUdpPacket.cs`、`PacketChecksums.cs`、`Socks5State.cs`、`Socks5Udp.cs`、`UdpFrameBuilder.cs` 的全部路径和调用者。
- R2: 对 RFC 1928/1929、SOCKS UDP、IPv4/IPv6、TCP/UDP header、extension/fragment、address family/ATYP/REP/FRAG、checksum 和 frame cap 做字节级复核。
- R3: parser/rewrite 的每条 reject path 必须有“不修改输入”的证据；checksum 使用独立 oracle，不能重用生产实现。
- R4: 每个 confirmed defect 有绿色回归，结果和 coverage gaps 写入 `research/audit-findings-audit-protocols.md`。

## Acceptance Criteria

- AC1: 六个拥有文件逐个列出审读结论及 `file:line` 锚点。
- AC2: 改写测试证明 expected offsets 唯一可变、round trip 字节相同、IPv4 header/transport checksum 独立有效。
- AC3: SOCKS5 greeting/auth/request/reply/UDP IPv4/IPv6/domain 的边界和错误状态覆盖状态明确。
- AC4: focused、全套 Release build/test 与 diff check 全绿并被报告。

## Out of Scope and Dependencies

- 不扩展 release-1 不支持的 IP/SOCKS 功能；当前不支持输入仍按既有 fail-closed contract 评估。
- 本项为 TCP/UDP runtime state-machine 的协议预言机所有者；不改 Runtime 文件。
