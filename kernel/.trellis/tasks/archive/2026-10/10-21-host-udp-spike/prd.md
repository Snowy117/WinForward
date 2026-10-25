# Host UDP Spike：classify 频次、context 生命周期、UDP block 行为

## Goal

在 WinLTSC 上把 Host UDP 正式接入前最关键的三个未知量变成带证据的事实，产出
`research/spike-udp.md`，作为 B1/B2 批次断言的依据。若 UDP block 的 auth 行为不成立，
本批次直接给出 STOP 结论，后续批次不启动。

## Background

- 父任务 `10-21-host-udp-production` 的探针结论：UDP local redirect 与 per-datagram
  `IP_WFP_REDIRECT_CONTEXT` 已成立；回程不需要伪造源或注入。
- 尚未验证：
  1. 同一 UDP 关联到底 classify 几次（每 datagram 还是每关联）；
  2. redirect context 在客户端关闭 / 驱动卸载时何时释放，池计数会不会持续增长；
  3. `ALE_AUTH_CONNECT` 的 BLOCK filter 对 UDP 到底让应用看到什么（send 失败？connect
     失败？错误码？）；
  4. IPv6 / dual-mode 的 listener、cmsg level 与 redirect target 是否可行。
- 当前 HEAD 仍带实验探针（`udp_probe` / `udp_redirect` / `udp_ippacket` / `udp_inject`），
  本批次可以临时使用它们，但**不得把临时改动留在工作区**。

## In Scope

- S1：用现有实验开关测 `udp-probe-stats` 的 `seen` 与 datagram 数的关系。
- S2：测 `WfPr` 池标签的 allocs/frees/outstanding 随时间与 `sc stop` 的变化。
- S3：临时把 UDP 协议加入 blob 校验，并加一个临时 UDP block 探测命令，测 auth filter
  对 UDP 的实际拒绝行为与错误码。
- S4（尽力而为）：临时扩展 wfprobe 的 IPv6 解析，验证 dual-mode / IPv6 的 listener 与
  cmsg；若工具改动过大，记录需要改什么，把实测留到 B3。
- 产出 `research/spike-udp.md`：命令、输出摘要、结论、对设计的影响。

## Out of Scope

- 任何永久的产品代码改动；B0 结束时 `src/`、`tools/` 必须与 HEAD 一致（临时改动还原）。
- 完整 UDP 规则、ABI v3、block filter 产品化、UdpSuite、gate 改造。
- git commit / push（提交由主会话做）。

## Acceptance Criteria

- [ ] S1 有明确数字：N 条 datagram 对应的 classify/seen 次数；若与 N 不等，解释关联边界。
- [ ] S2 有 allocs/frees/outstanding 的时间序列；说明关闭客户端、等待、`sc stop` 后是否归零。
- [ ] S3 有应用的 `send`/`connect` 结果与错误码；说明 auth filter 是否真的拦住 UDP。
- [ ] S4 有结论（通过 / 明确缺口 + 需要的工具改动），不静默跳过。
- [ ] `research/spike-udp.md` 每个结论都带命令与输出摘录，并给出 B1/B2 的断言建议。
- [ ] 工作区除本任务目录外没有未提交的代码改动；临时改动已还原。
- [ ] 若 S3 失败，明确写出 STOP 与原因。

## Notes

- 本批次是研究，不是产品实现；可以临时改代码，但结束前必须 `git diff` 确认只有任务目录。
- VM 命令走 `windows-vm`；仓库根/VM 路径以实际会话为准。
