# Host UDP B2a：ABI v3 与实验脚手架清除

## Goal

把控制面 ABI 提升到 v3（事件带协议、计数带 UDP 维度），删除探针留下的实验 UDP IOCTL/模块/
wfprobe 命令，更新 C# 镜像与 redirect context 读取方。**TCP classify 行为不变**：
删除实验分支后 UDP 回到 `permitted_non_tcp` 早退，B1 的临时 UDP block 安全闸保留到 B2b。

## Background

- B1 已让规则层接受 `protocol=udp`，但驱动不匹配 UDP；含 UDP block 规则的 blob 被临时闸拒绝。
- 当前 HEAD 的 `probe_ioctl.h` 还带 0x880/0x881/0x883 实验 ABI，`ProbeIoctl`/`Protocol.cs`
  没有镜像，导致两个既有 ABI 测试失败；本批删除实验 ABI 后应恢复全绿。
- B0 spike：静默丢包已定稿；classify 按关联；context 关闭即释放；IPv6 可用。

## In Scope

- `probe_abi_version` 2 → 3。
- `probe_event` 增加 `protocol`（offset 58）与显式 `reserved0`（59），尺寸仍 64。
- `probe_stats` 增加 `udp_classify_calls` / `udp_permitted` / `udp_blocked` /
  `udp_redirected` 四个计数器，尺寸 264；B2a 中它们保持 0。
- 删除 `udp_probe_*` 结构、0x880/0x881/0x883、`probe_control.cpp` 实验 handler、
  `probe_driver.*` 的 start/stop/include、四个 `udp_*.cpp` 与 vcxproj 条目。
- `probe_classify.cpp` 的 `log_decision` 写 `event.protocol`（当前只有 TCP 事件）。
- `wfprobe`：删除 `udp-probe` / `udp-probe-stats`、实验结构/IOCTL、`WfpDevice` 实验方法；
  `UdpProbeCommands` 收敛为 `UdpCommands`（只留 listen / relay / client）。
- C# ABI 镜像：`ProbeIoctl` / `ProbeStats` / `ProbeEvent` / `DeviceLayout` /
  ABI 一致性测试 / DeviceAbiTests / LayoutTests。
- redirect context 读取方：`try_read_original_destination` 接受 UDP，`RedirectContext.ProtocolUdp`，
  `RedirectDestination` 接受两种协议并暴露协议，`RelayServer` 显式要求 TCP。
- 宿主 `redirect_context_test.cpp`：UDP 接受、未知协议拒绝。

## Out of Scope

- classify 匹配 UDP、block filter 覆盖 UDP、删除 B1 临时安全闸、UDP 计数实际自增（B2b）。
- UdpSuite、gate.ps1、IPv6 工具扩展、热替换用例、README/spec（B3）。
- 任何产品级 UDP 中继。

## Acceptance Criteria

- [ ] `probe_abi_version == 3`；C++ `static_assert` 与 C# `DeviceLayout` 对
      `probe_config` 64 / `probe_stats` 264 / `probe_rule_info` 32 / `probe_event` 64 /
      `redirect_context` 36 全部一致。
- [ ] `probe_event.protocol` 在 58、`matched_rule` 仍在 60；TCP 事件的 protocol=6。
- [ ] `probe_stats` 四个 UDP 计数器偏移 224/232/240/248，`last_status` 256、`abi_version` 260。
- [ ] 实验 ABI/模块/命令全部删除；`wfprobe udp-probe*` 不存在；`udp-listen/relay/client` 仍可用。
- [ ] `try_read_original_destination` 接受 TCP/UDP；C# `RedirectDestination` 暴露协议；
      `RelayServer` 对非 TCP context refuse。
- [ ] `tools/check.sh --full` **全绿**（两个既有 ABI 失败消失）；C# 测试全过；`ctest` 66/66。
- [ ] VM 构建/安装通过；TCP `rules` / `selftest` / `gates` 不退化；UDP 行为仍保持“非 TCP 早退”。
- [ ] 不改 `probe_config` / `probe_rule_info` 布局；不改 classify 的 TCP 语义。

## Notes

- ABI v3 是破坏性变更：旧 wfprobe / WinForward 集成必须同批升级。
- B1 临时闸在 B2b 删除；本批不要动它的逻辑。
- 四个 UDP 计数器在 B2a 中定义但保持 0；B2b 才接计数点。
