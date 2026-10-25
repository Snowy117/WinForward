# Host UDP B1：规则 blob 与 C# 编译链支持 UDP

## Goal

让 `protocol=udp` 从配置到 blob 解码到 matcher 全线可表达、可验证，为 B2 的驱动 classify
转正铺路。本批不改驱动 classify 行为：UDP 仍走 `permitted_non_tcp` 早退；UDP block 规则
在驱动侧用临时安全闸拒绝，避免“classify 没记录、auth filter 却静默丢包”的中间状态。

## Background

- B0 spike 已确认：classify 按关联一次、context 关闭即释放、UDP auth block 静默丢包、
  IPv6/dual-mode 可用。
- 当前规则 blob 只认 `0`（任意）与 `6`（TCP）；C# `RuleProtocol` 只有 `Any/Tcp`。
- 父任务 B2 才会移除实验分支、加 ABI v3、改 block filter 与 UdpSuite。

## In Scope

- C++ `src/common/rule_blob.h`：新增 `rule_protocol_udp = 17`，`is_known_protocol` 接受；
  `rule_blob_version` 保持 1。
- C++ `src/common/matcher.h`：加 UDP 的 `static_assert`；比较逻辑不变。
- C# `RuleProtocol.Udp`、配置解析 `"udp"`、编译器校验、规则顺序描述。
- 宿主测试：blob 接受 17 / 拒绝 18、matcher UDP 规则、C# 编译/配置/顺序/差分 UDP 用例。
- 驱动临安闸：`allocate_rule_table` 遇到 `action=block && protocol=udp` 整块拒绝（B2 删除）。

## Out of Scope

- 驱动 classify 匹配 UDP、ABI v3、事件/计数、block filter 产品化、删除实验模块。
- wfprobe UdpSuite / gate.ps1 / VM 门禁；这些在 B2/B3。
- Q1（UDP block 应用侧语义）的最终裁决；本批不写任何应用可见行为的断言。

## Acceptance Criteria

- [ ] `try_open_blob` 接受 protocol 17；18 仍整块拒绝；UDP 规则字段解码正确。
- [ ] matcher：`protocol=udp` 只命中 UDP；`protocol=any` 命中 UDP；TCP 规则不命中 UDP。
- [ ] C# 配置接受 `"protocol": "udp"`；编译器输出字节 17；非法协议仍拒绝。
- [ ] `RuleOrderValidator` 的 `tcp` vs `udp` 不重叠、`any` 与两者都重叠；错误文案能显示 udp。
- [ ] 差分用例：C# 编译的 UDP-only 规则在 C++ 侧对 UDP 连接命中、对 TCP 不命中。
- [ ] 驱动临时安全闸：含 UDP block 规则的 blob 被整块拒绝；permit/redirect 的 UDP 规则可加载
      （但 classify 暂不匹配，行为不变）。宿主测试无法覆盖驱动闸，代码审查 + B2 的 VM 用例兜底。
- [ ] `tools/check.sh --full` 全过；C# 测试全过；TCP 既有测试不退化。
- [ ] 不修改 `probe_abi_version`、`probe_event`、`probe_stats`、`redirect_context` 的布局。

## Notes

- B2 删除临时安全闸时必须补一条“UDP block 规则可加载”的 VM 用例。
- 本批不碰 `block_filters.cpp` 的 `protocol=any` 行为；那在 B2。
