# 技术设计：Host UDP B2a ABI v3 与清理

## 1. ABI v3

### probe_event

```c
std::uint64_t timestamp_100ns;   // 0
std::uint32_t sequence;          // 8
std::uint32_t process_id;        // 12
std::uint8_t  remote_address[16];// 16
std::uint8_t  local_address[16]; // 32
std::uint16_t remote_port;       // 48
std::uint16_t local_port;        // 50
std::uint16_t redirect_port;     // 52
std::uint8_t  decision;          // 54
std::uint8_t  family;            // 55
std::uint8_t  action;            // 56
std::uint8_t  branch;            // 57
std::uint8_t  protocol;          // 58  IPPROTO_TCP / IPPROTO_UDP
std::uint8_t  reserved0;         // 59
std::uint32_t matched_rule;      // 60
```

`log_decision` 写 `event.protocol = facts.protocol`；B2a 只会产生 TCP=6。

### probe_stats

在 `process_path_metadata_seen`（216）之后追加：

```c
std::uint64_t udp_classify_calls; // 224
std::uint64_t udp_permitted;      // 232
std::uint64_t udp_blocked;        // 240
std::uint64_t udp_redirected;     // 248
std::uint32_t last_status;        // 256
std::uint32_t abi_version;        // 260
```

总长 264。B2a 不接计数点，四个值保持 0；B2b 在 classify 的三出口显式自增。

## 2. 删除实验 ABI 与模块

- `probe_ioctl.h`：删除 `udp_probe_config` / `udp_probe_stats` / `udp_probe_return_header`、
  `udp_probe_config_version`、`udp_probe_table_capacity`、0x880/0x881/0x883。
- `probe_control.cpp`：删除 `apply_udp_probe_config` / `apply_udp_probe_return` /
  `get_udp_probe_stats` 与三个 switch case。
- `probe_driver.h`：删除 `#include "udp_probe.h"`。
- `probe_driver.cpp`：删除 `udp_probe_start/stop`。
- 删除 `udp_probe.h/cpp`、`udp_redirect.cpp`、`udp_ippacket.cpp`、`udp_inject.cpp`；
  `wfprobe.vcxproj` 同步删除。
- `probe_classify.cpp`：实验模块删除后 `udp_probe_candidate` 无法编译，本批必须删除实验
  分支，恢复 `facts.protocol != IPPROTO_TCP` 早退；UDP 回到 B1 前的 early-exit。
  B2b 再把早退改成 TCP/UDP 都进入匹配。
- B1 的 `rule_table.cpp` 临时 UDP block 闸保留；它不依赖实验模块。

## 3. wfprobe

- `Protocol.cs`：`AbiVersion=3`；`ProbeEvent` 加 `Protocol`/`Reserved0`；`ProbeStats` 加四个
  counter；删除实验结构/常量；`ProtocolLayout.Verify` 更新尺寸（Stats 264 / Event 64）。
- `WfpDevice.cs`：删除 `SetUdpProbeConfig` / `GetUdpProbeStats` / `SendUdpReturn`。
- `Program.cs`：删除 `udp-probe` / `udp-probe-stats` 分支与 usage；保留 `udp-listen` /
  `udp-relay` / `udp-client`。
- `UdpProbeCommands.cs` → `UdpCommands.cs`：只保留 `Client` / `Listen` / `Relay`；
  删除 `Configure` / `PrintStats` / `InjectReturn` / `ParseReturnMode` / `--inject` /
  `--return-mode` / `--filter-port` / `--target` / `--layer` / `--interface`。
- `UdpSockets.cs`：保留 cmsg 解析；若 `RedirectRecords` 只被实验命令使用，可一并删除。
- `BlockProbe.cs` / `RuleSuite.cs` / `SelfTest.cs` 只改受 ABI 结构影响的字段/尺寸。

## 4. C# ABI 镜像与读取方

- `ProbeIoctl.AbiVersion=3`。
- `ProbeStats` 四字段插在 `ProcessPathMetadataSeen` 之后、`LastStatus` 之前。
- `ProbeEvent` 加 `Protocol`/`Reserved0`，`MatchedRule` 仍 60。
- `DeviceLayout`：Stats 264；LastStatus 256 / AbiVersion 260；Event Protocol 58。
- `AbiConsistencyTests`：`transport_protocol` 增加 UDP 断言；实验 IOCTL 消失后两个基线失败
  应恢复。
- `redirect_context.h`：`try_read_original_destination` 接受 TCP/UDP。
- `RedirectContext.cs`：`ProtocolUdp=17`。
- `RedirectDestination`：接受 6/17，增加 `Protocol` / `ProtocolText`；日志带协议。
- `RelayServer`：解析后要求 `ProtocolTcp`，否则 refuse 并记录 `reason=non-tcp-redirect-context`。
- `tests/host/redirect_context_test.cpp`：UDP 接受；未知协议拒绝。

## 5. 验证

- `tools/check.sh --full` 全绿（重点：两个 ABI 测试恢复）。
- `dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test` 全过。
- `ctest --test-dir build/check --output-on-failure` 66/66（64 基线 + 2 条 redirect_context 新用例）。
- VM：构建/安装；TCP `rules` / `selftest` / `gates` 不退化；UDP 仍早退（不是本批目标）。
