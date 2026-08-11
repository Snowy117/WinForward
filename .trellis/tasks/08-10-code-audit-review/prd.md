# 全项目代码审查：功能 bug / 协议正确性 / 测试覆盖

## Goal

对 WinForward 全部源码（`src/` 的 41 个生产 C# 文件，约 5.6k 行 / net10.0）做一次系统性审查，回答三个问题：

1. **功能 bug** — 逻辑错误、边界条件、资源生命周期、并发问题
2. **协议实现不正确** — SOCKS5（RFC 1928/1929）、IPv4/TCP/UDP 头部解析与构造、校验和、DNS 策略处理
3. **测试覆盖明显不完善** — 关键路径（抓包→分类→转发→回注）中未被测试覆盖的行为

产出：每个确认问题都有**复现测试**（`tests/`）+ **归档的审查报告**（`research/`），严重级别、证据、file:line 锚点齐全。

## Confirmed Facts（仓库自查）

- 解决方案：`WinForward.slnx`，net10.0，xunit，`TreatWarningsAsErrors=true`
- 基线状态：`dotnet build` 0 warning / 0 error；`dotnet test` 149 passed / 0 failed（Linux 本地）
- 项目分层：
  - `WinForward.Core` — Domain / Policy / IpPrefix / ProcessSelectors / PacketRuntime
  - `WinForward.Protocols` — IpTcpUdpPacket / IpUdpPacket / PacketChecksums / Socks5State / Socks5Udp / UdpFrameBuilder
  - `WinForward.Runtime` — Capture 生命周期 / FlowDispatcher / TCP redirect & proxy / UDP relay / Socks5Client / SelfTrafficRegistry / IdleExpirySweeper
  - `WinForward.NdisApi` — NDIS 驱动 ABI / 抓包
  - `WinForward.Windows` — 适配器身份 / 进程归因 / IpHelper
  - `WinForward.Configuration` / `WinForward.Cli` — 配置模型 / 入口
  - `tests/WinForward.Core.Tests` — 唯一测试项目（11 个测试文件，149 用例）
- 最近的修复历史（git log）显示 redirect / UDP / SOCKS5 是近期 bug 高发区（H1-H3、M1-M5、L1-L4 review findings；socks5 success-prefix 回归）
- 用户提供了 Windows 开发机访问方式：`evil-winrm-py -i 192.168.100.2 -u neko -p "$PASS"`，可用于 NDIS/硬件相关的运行时验证（Linux 上无法覆盖的部分）

## Requirements

- R1: 逐文件审查全部 `src/**/*.cs`，重点：协议字节处理、packet 改写、TCP/UDP 状态机、异步/并发、资源释放、回环防护
- R2: 每个确认的问题写入 `research/audit-findings-*.md`：严重级别（Critical/High/Medium/Low）、类型（功能bug/协议/覆盖）、证据、file:line、复现路径
- R3: 每个确认的、可在单元测试中复现的问题，在 `tests/` 中新增回归测试
- R4: 评估测试覆盖缺口（哪些公开行为/关键分支无测试），即使未发现 bug 也列出
- R5: NDIS/Windows 专属行为如需运行时验证，使用 Windows 开发机（evil-winrm-py）
- R6: **允许直接修复 `src/` 源码**（用户已确认）。每个修复必须有绿色回归测试；禁止只改代码不留测试

## Acceptance Criteria

- AC1: `research/` 下有完整审查报告，覆盖全部 src 模块，每个发现含严重级别 + file:line 锚点 + 证据
- AC2: 每个可在单元测试复现的确认 bug 都有对应测试；报告与测试一一对应
- AC3: 测试覆盖缺口清单（模块 × 未覆盖行为）
- AC4: 结束后 `dotnet build` 与 `dotnet test` 状态在报告中明确记录（基线 vs 结束）

## Out of Scope

- 性能优化、重构建议（除非直接导致 bug）
- qodana-results/ 静态分析输出的全量复核（可作参考输入）
- 文档/示例配置的审查（除非示例与代码行为矛盾）

## Key Decisions

- D1: 确认的 bug **直接修复源码**（用户 2026-08-10 确认），配绿色回归测试，而非留下失败测试。审查报告仍记录完整根因与 file:line 锚点。
- D2: Windows 开发机仅用于 Linux 无法覆盖的 NDIS/硬件运行时验证；静态审查与单元测试全部本地完成。
