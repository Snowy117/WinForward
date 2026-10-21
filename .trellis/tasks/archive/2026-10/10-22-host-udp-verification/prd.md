# Host UDP B3：UdpSuite、gate、可观测性与文档收尾

## Goal

把 B0–B2b 的 UDP 能力固化成可重复的正式验证与文档：新增 `wfprobe udp-rules` 套件覆盖
U1–U13（redirect/permit/fallback/block/any/进程选择器/IPv6/生命周期/热替换/ICMP），
接入 `gate.ps1` 与 `mutation.ps1`，补事件/计数输出，更新 README/spec/AGENTS，最后在稳定
VM 上跑完整门禁并归档 Trellis 任务。

## Background

- B0 spike：classify 按关联一次；unconnected context 关闭即释放；connected 180s 仍 outstanding；
  UDP auth block 静默丢包；IPv6/dual-mode 可用。
- B1/B2a/B2b：规则/ABI v3/classify/block 已完成；`any`/fallback block 改为 6/17 两条 filter，
  ICMP 不被误拦；`udp_blocked` 是 classify 决策数。
- 已知风险：connected UDP context 释放窗口未定；VM 出现过一次 0x11D/0xc000021a，证据不支持
  归因 wfprobe；B3 必须用稳定快照/checkpoint，不靠反复重启。
- 待修工具 bug：`wfprobe control --clear-self` 未清 `self_process_ids` 数组。

## In Scope

- `tools/wfprobe/UdpSuite.cs` + `udp-rules` 命令：U1–U13。
- `UdpSockets` IPv6/dual-mode：`sockaddr_in6`、cmsg level 0/41、`IPV6_WFP_REDIRECT_CONTEXT`、
  `[addr]:port` 客户端。
- 生命周期：unconnected 严格池断言；connected 先测释放窗口并决定断言/记录限制。
- 热替换 U13：旧关联 auth 层丢弃但不重新 classify；新关联按新 blob。
- ICMP：`protocol=any` / fallback block 下 `ping` 仍通。
- 可观测性：`wfprobe PrintEvent` 输出 protocol；`PrintStats` 与 Controller `StateCommand`
  输出 4 个 UDP 计数器。
- `gate.ps1` 增加 `udp-rules` 步骤；`mutation.ps1` 接入 6 个 UDP mutation 并验证变红。
- `control --clear-self` 清零数组。
- 文档：`src/driver/README.md`、`.trellis/spec/backend/wfp-architecture.md`、`AGENTS.md`、
  `src/userland/README.md` 与 `RuleOrderValidator.cs` 的旧“无条件 filter”措辞。
- 最终：宿主 `check.sh --full`、VM 完整 `gate.ps1`、mutation、池基线，归档 B0–B3 与父任务。

## Out of Scope

- 生产级 UDP 中继/SOCKS5 UDP ASSOCIATE/UoT。
- Forwarded UDP、ARM64。
- 新的 ABI 字段或布局变更。

## Acceptance Criteria

- [x] `wfprobe udp-rules` 在 VM 上全 PASS，覆盖 U1–U13：
      redirect unconnected/connected、context protocol=17、permit、fallback redirect、
      UDP block、any block（含 TCP/UDP/ICMP）、进程选择器、IPv6/dual-mode、enabled 0↔1、
      多 datagram/多目的地、热替换、生命周期/池。
- [x] U3 断言按关联计数，并在发请求前 reset、收包后先读计数；同 socket 回包不重新 classify。
- [x] U7 在 any/fallback block 下验证 ICMP `ping` 仍通。
- [x] connected UDP context 先测释放窗口：connected 只记录 LIMIT，unconnected 在干净基线严格断言；
      未伪绿。
- [x] `gate.ps1` 包含 `udp-rules` 且完整 PASS（500+ 断言）；`mutation.ps1` 的 6 个 UDP
      变异全部变红。
- [x] 事件/计数输出可见 protocol 与 4 个 UDP 计数器；`control --clear-self` 正常。
- [x] README/spec 与当前实现一致，无“any 不写条件 / fallback 无条件 filter”旧措辞；
      AGENTS.md 因仓库 `.gitignore` 忽略而只在本机生效，任务记录已注明。
- [x] 宿主 `tools/check.sh --full` 全绿；C# 测试全过；无临时 smoke 残留。
- [ ] B0–B3 子任务与父任务归档，goal 完成。

## Notes

- 先做 VM checkpoint/稳定快照；不要用反复重启取池基线。
- 并发实现：A 负责工具/门禁/可观测性，B 负责文档；文件集不相交，合并后做结构审查。
- 不 commit；主会话提交。
