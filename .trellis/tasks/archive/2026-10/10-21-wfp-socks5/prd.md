# SOCKS5 上游与跨机性能复测

## Goal

让控制器能经 SOCKS5 上游出站（对齐基线里「WinForward → sing-box」的形态），然后用跨机 target 复测 THRU，得到可与 154% 直接比较的数字。

## Requirements

- **SOCKS5 客户端**：no-auth CONNECT，支持 IPv4 / IPv6 / 域名三种地址形态；连不通或握手失败要有明确错误，不能静默。
- **配置**：策略文件可选一个上游（`socks5: { "host": ..., "port": ... }`）。没配置时保持现有直连行为。
- **每连接一次握手**：SOCKS5 只有在建连时付出成本，数据面仍是纯转发——这正是我们要跟基线比的形态。
- **测试替身**：`tools/socks5/` 一个最小 SOCKS5 服务端，no-auth + CONNECT，支持 IPv4/IPv6/域名，带连接计数。它是**测试工具**，不进产品。

## Acceptance Criteria

- [ ] A1 配置了 SOCKS5 时，中继的出站走 SOCKS5 CONNECT，原始目的地作为目标地址
- [ ] A2 三种地址形态（IPv4、IPv6、域名）都有用例
- [ ] A3 上游不可达 / 握手被拒 时，客户端能看到明确失败，而不是挂住
- [ ] A4 不配置 SOCKS5 时行为与现在完全一致（直连）
- [ ] A5 `wf_check` 与全部 C# 用例绿
- [ ] A6 端到端：客户端 → 驱动重定向 → 控制器 → SOCKS5 → 跨机 target，1 MiB 校验和一致

## Notes

- 依赖现有控制器与中继（commit `257a6b5`）。
- 宿主防火墙开放 40000–41000，跨机 target 用这个区间。
- 禁止 `git commit`。
