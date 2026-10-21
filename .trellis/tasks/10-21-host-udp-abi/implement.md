# 执行计划：Host UDP B2a

## 1. C++ ABI v3

- [ ] `src/driver/probe_ioctl.h`：`probe_abi_version=3`；`probe_event` 加 protocol/reserved0；
      `probe_stats` 加四个 UDP 计数器（224/232/240/248；total 264）；更新 static_assert。
- [ ] 删除 `udp_probe_*` 结构与 0x880/0x881/0x883。
- [ ] `src/driver/probe_control.cpp`：删除实验 handler 与 case。
- [ ] `src/driver/probe_classify.cpp`：删除 `udp_probe_candidate` 分支，恢复非 TCP 早退；
      `log_decision` 写 `event.protocol`。
- [ ] `src/driver/probe_driver.h/.cpp`：删除 include 与 start/stop。
- [ ] 删除 `udp_probe.h/cpp`、`udp_redirect.cpp`、`udp_ippacket.cpp`、`udp_inject.cpp`；
      `wfprobe.vcxproj` 同步。
- [ ] 保留 B1 的 `rule_table.cpp` 临时 UDP block 闸不动。

## 2. wfprobe ABI 镜像与命令清理

- [ ] `tools/wfprobe/Protocol.cs`：AbiVersion 3；Event/Stats 字段；删除实验结构与常量；
      `ProtocolLayout.Verify` 更新。
- [ ] `tools/wfprobe/WfpDevice.cs`：删除实验方法。
- [ ] `tools/wfprobe/Program.cs`：删除 `udp-probe*`；usage 更新。
- [ ] `tools/wfprobe/UdpProbeCommands.cs` → `UdpCommands.cs`：只留 client/listen/relay；
      删除实验参数与注入逻辑。
- [ ] `UdpSockets.cs` / `BlockProbe.cs` / `RuleSuite.cs` / `SelfTest.cs`：按 ABI 尺寸/字段
      与删除后的 API 清理编译错误；不做功能扩展。
- [ ] `tools/wfprobe/wfprobe.csproj` 无需改（文件删除即可）。

## 3. C# ABI 与读取方

- [ ] `ProbeIoctl.cs`：AbiVersion 3。
- [ ] `ProbeStats.cs` / `ProbeEvent.cs`：镜像字段。
- [ ] `DeviceLayout.cs`：Stats 264、LastStatus 256、AbiVersion 260、Event Protocol 58。
- [ ] `AbiConsistencyTests.cs`：transport_protocol UDP；确认两个既有 ABI 失败消失。
- [ ] `DeviceAbiTests.cs` / `LayoutTests.cs`：事件 protocol、stats 新字段/尺寸。
- [ ] `src/common/redirect_context.h`：`try_read_original_destination` 接受 UDP。
- [ ] `RedirectContext.cs`：ProtocolUdp。
- [ ] `Relay/RedirectDestination.cs`：接受 TCP/UDP + Protocol/ProtocolText。
- [ ] `Relay/RelayServer.cs`：非 TCP refuse，日志带 reason。
- [ ] `tests/host/redirect_context_test.cpp`：UDP 接受、未知协议拒绝。

## 4. 验证

- [ ] `tools/check.sh --fast`；`tools/check.sh --full` **全绿**。
- [ ] C# 测试全过；`ctest` 66/66。
- [ ] VM：`load-driver.ps1` 构建/安装；`wfprobe rules` / `selftest` / `gates` 通过；
      UDP 仍非 TCP 早退（不要求 redirect）。
- [ ] `git status` 只含本批文件；不 commit，报告给主会话。

## 回滚点

- ABI v3 是破坏性变更；回滚整批 revert。B1 的临时闸与规则层不受影响。

## 交付记录（2026-10-21）

- ABI v3、实验 UDP IOCTL/模块/wfprobe 命令全部删除；classify 恢复非 TCP 早退，TCP 行为不变。
- 宿主：`tools/check.sh --full` 8/8；C# 134/134；`ctest` 66/66（64 基线 + 2 条 redirect_context）；
  `verify-gates.sh` 23/23（含 ABI 版本变异 3→4）。
- VM：完整 `gate.ps1` **GATE-RESULT=PASS**、249 条 ASSERT 0 FAIL；userland-e2e 48 checks、
  socks5-e2e 38 checks 均 PASS，`config/rule abi=3` 断言生效；驱动构建 0 warning、签名 Valid、
  PREfast 0、DACL 通过；`rules` / `selftest` V4+V6 / `gates` / `vm-check` 全 PASS。
- 检查者发现并已修：两个 e2e 脚本的 `abi=2` 硬编码；`RelayServer` 的拒绝决策抽为
  `RelayRefusalReason`（TCP→null / 非 TCP→`non-tcp-redirect-context`），单测覆盖 TCP/UDP，
  变异 UDP→null 时用例变红；调用点由代码审查 + B3 中继集成用例兜底。`UdpCommands.cs`
  提交时必须 `git add`。
- 留到 B3：`README` 的“ABI v2”文案、`PrintEvent`/`PrintStats`/`StateCommand` 的 UDP 可观测性、
  B2b 的 `probe_classify.cpp:314` 硬编码 TCP 改 `facts.protocol`。
