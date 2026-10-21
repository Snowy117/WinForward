# Host UDP B2b：classify 转正与 block 覆盖 UDP

## Goal

让 UDP 正式走进 classify 的四道闸与规则匹配：`permit` / `block` / `redirect` 与 TCP 同语义，
redirect context 写 `protocol=udp`，block filter 覆盖 `protocol=udp` 与 `protocol=any`，
4 个 UDP 计数器接上真实计数点，删除 B1 的临时 UDP block 安全闸。用 VM 手工用例验证
redirect / permit / fallback / block / any / 热替换，并确认 TCP 不退化。

## Background

- B0：classify 按关联一次；context 关闭即释放；UDP auth block 静默丢包；IPv6 可用。
- B1：规则层接受 UDP；驱动仍早退；临时闸拒绝 UDP block。
- B2a：ABI v3；实验脚手架删除；UDP 仍早退；4 个 UDP 计数器定义为 0。
- 本批只改驱动 classify / block / rule_table 临安闸 / wfprobe 测试辅助；不改 ABI 布局。

## In Scope

- `probe_classify.cpp`：协议早退改为“非 TCP 且非 UDP”；UDP 走四道闸与 matcher；
  `redirect_classification` 按 `facts.protocol` 写 context；`log_decision` 已有 protocol；
  4 个 UDP 计数器在三个出口显式自增。
- `block_filters.cpp`：所有 block filter 显式写 `IP_PROTOCOL`；`any` 与 `fallback_action=block`
  按 6/17 各生成一条；`max_block_filters` 64 → 128。这样 ICMP 不被策略误拦。
- `rule_table.cpp`：删除 `udp-block-temporary-guard`（以及 mutation 标记）。
- `tools/wfprobe/BlobBuilder.cs` / `TestRule`：补协议参数/helper，便于 VM 构造 UDP 规则。
- VM 手工验证：UDP redirect + context、permit、fallback redirect、UDP block、any block、
  TCP block 对照、规则热替换、TCP `rules`/`selftest`/`gates` 回归。
- 临时 smoke 工具（跑完删除）：用于设置 UDP 规则并观测 listener/对端；B3 会转成正式 UdpSuite。

## Out of Scope

- UdpSuite/gate.ps1/CI 集成、IPv6 工具扩展、生命周期池断言（B3）。
- README/spec/AGENTS 文档更新（B3）。
- 用户态 UDP 中继、SOCKS5 UDP ASSOCIATE、UoT。
- ABI 布局变更。

## Acceptance Criteria

- [ ] `protocol=udp` + `redirect` 规则：UDP `sendto`/`connect` 首包被改道到 listener；
      context 的 protocol=17、family/地址/端口/PID/rule 正确；`udp_redirected` +1。
- [ ] UA `permit` 规则：UDP 直连、listener 无包；`udp_permitted` +1。
- [ ] `fallback_action=redirect` 对未命中 UDP 生效；事件 matched_rule=0xFFFFFFFF。
- [ ] `protocol=udp` block：应用 `send` 成功、对端收不到、未 block 端口可达；classify 记
      `blocked`/`udp_blocked`/`deferred_blocks`，auth filter 执行丢弃。
- [ ] `protocol=any` block：TCP 仍返回 `AccessDenied` 一类错误，UDP 被静默丢弃；两条都验证。
- [ ] 规则热替换：先 permit 建关联，再换 block blob；记录原关联是否继续放行（连接级 block
      语义），新关联必须被丢。
- [ ] 计数不变式：单次测试增量满足
      `udp_classify_calls == udp_permitted + udp_blocked + udp_redirected`。
- [ ] B1 临时闸删除：含 UDP block 规则的 blob 可加载；`rule_table.cpp` 无残留 guard 标记。
- [ ] 宿主 `tools/check.sh --full` 全绿；C# 测试全过。
- [ ] VM：UDP 手工用例通过；TCP `rules` / `selftest` / `gates` 全 PASS；无蓝屏。
- [ ] 不改 `probe_abi_version` / `probe_event` / `probe_stats` / `redirect_context` 布局。

## Notes

- UDP block 的应用侧语义是**静默丢包**（用户已确认 D1）；不要写“send 报错”的断言。
- `permitted_non_tcp` 从本批起只表示“既非 TCP 也非 UDP”。
- 临时 smoke 工具不得进入提交；B3 用正式 UdpSuite 取代。
