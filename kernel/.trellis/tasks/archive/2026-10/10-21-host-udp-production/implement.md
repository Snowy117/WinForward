# 执行计划：Host UDP 正式接入驱动

> 顺序执行；每步先构建/测试再进入下一步。第 0 步是可失败门禁：spike 不成立就停下来回到规划，
> 不带着未验证的假设往下写。
> 所有 VM 命令在 `windows-vm` 会话里执行；本机没有 `wf.local.json` 时用会话里的仓库根。

## 批次映射（ultra-goal）

| 批次 | 子任务 | 内容 | 依赖 |
|---|---|---|---|
| B0 | `10-21-host-udp-spike` | 第 0 步 spike；只产出研究结论，临时改动必须还原 | 无 |
| B1 | `10-21-host-udp-rules` | 第 1–2 步：规则 blob / C# 编译链 / 宿主测试；驱动暂不匹配 UDP，block 规则有临时安全闸 | B0 |
| B2a | `10-21-host-udp-abi` | 第 3、6 步：ABI v3、删除实验 ABI/模块、wfprobe ABI 镜像、redirect 读取方；classify 行为不变 | B1 |
| B2b | `10-21-host-udp-classify` | 第 4–5 步：classify 匹配 UDP、block filter 覆盖 UDP、删除临时安全闸、事件/计数 | B2a |
| B3 | `10-22-host-udp-verification` | 第 7–9 步：UdpSuite、gate.ps1、IPv6/生命周期、热替换、文档与 spec、全量门禁；A 工具/B 文档并行 | B2b |

每批独立提交；B0 的临时代码改动不得进入提交。B1 已通过检查（结论：通过，4 条建议），
其临时安全闸与已知基线记录在 B1 任务的 implement.md 末尾。

## 0. Spike：先验证三个未知量（VM）

目标不是写产品代码，而是把设计里的假设变成带证据的事实。全部记录到
`research/spike-udp.md`；结论会影响第 5、8 步的断言。

> **B0 已完成（2026-10-21）**：findings 在
> `.trellis/tasks/10-21-host-udp-spike/research/spike-udp.md`。结论：classify 按关联一次；
> context 随客户端 socket 关闭立即释放；UDP auth block 是静默丢包（应用无错误）；
> IPv6/dual-mode 可用，family 取决于实际层。S3 没有触发“拦不住”的 STOP，但推翻了
> “应用可见错误”的 D1 假设，用户已确认接受静默丢包（2026-10-21），B2 按此写断言。

- [ ] `windows-vm status` / `connect`；同步当前仓库（当前 HEAD 仍带实验探针）。
- [ ] 基线构建/安装：`pwsh -File tools\windows\load-driver.ps1 -Root <repo>`。
- [ ] **S1 每次 UDP 关联 classify 几次**：用现有实验开关
      `wfprobe udp-probe --enable 1 --port 5555 --pid <relay-pid>`，客户端
      `udp-client --remote 1.2.3.4:443 --mode unconnected --count 10`；
      读 `udp-probe-stats` 的 `seen`。记录 `seen` 与 datagram 数的关系。
- [ ] **S2 context 生命周期**：同样发 N 条，关闭客户端 socket，按 1s / 5s / 30s 读
      `wfprobe pooltag --tag WfPr`，记录 allocs/frees/outstanding 曲线；再 `sc stop` 后读一次。
- [ ] **S3 UDP block 的 auth 行为**：临时让 `rule_blob.h` 接受协议 17，并加一个临时
      UDP block 探测命令；实测 `send`/`connect` 的应用侧结果、对端是否收到、
      `blocked`/`deferred_blocks` 计数。结论已记录：auth filter 能丢弃 UDP，但应用侧静默。
      spike 结束后还原这处临时改动，不留在工作区。
- [ ] **S4 IPv6 / dual-mode**：临时扩展 `UdpSockets` 解析 `sockaddr_in6` + cmsg level 41，
      用 dual-mode listener 与 `[::ffff:1.2.3.4]` 客户端验证 context family 与原始地址。
      如果工具改动较大，可以只记录“需要哪些改动”，把实测放到第 8 步。
- [ ] 产出 `research/spike-udp.md`：每个问题的命令、观察、结论、对设计的影响。
- [ ] **门禁**：B0 已完成；S1/S2 结论写入 U3/U12 的断言（K 个关联 → K；关闭 socket 后
      1s 内 outstanding 回基线），S3 的静默丢包写入 U6（已确认），S4 的 family 分支写入 U10。
      不把“应用可见错误”写进断言。

## 1. 规则 blob / matcher（C++ 共享层）

- [ ] `src/common/rule_blob.h`：加 `rule_protocol_udp = 17`，扩展 `is_known_protocol`，
      注释写明协议值是可扩展枚举、未知值整块拒绝、布局版本不变。
- [ ] `src/common/matcher.h`：加 UDP 的 `static_assert`；不改变比较逻辑。
- [ ] `tests/host/rule_blob_test.cpp`：17 接受、18 拒绝；UDP 规则解码/字段断言。
- [ ] `tests/host/matcher_test.cpp`：UDP 规则命中 UDP、不命中 TCP；any 命中 UDP；
      现有 `rejects_a_different_transport_protocol` 保留。
- [ ] 验证：`tools/check.sh --fast`；`tools/check.sh --full` 的 `wf_check`。

## 2. C# 规则编译链

- [ ] `RuleProtocol.cs`：`Udp = 17`，注释更新。
- [ ] `PolicyConfigurationDocument.cs`：解析 `"udp"`；schema 注释与错误文案更新。
- [ ] `RuleBlobCompiler.cs`：`ValidateProtocol` 接受 UDP；错误文案更新。
- [ ] `RuleOrderValidator.cs`：`Describe` 支持 UDP；`ProtocolsOverlap` 不改。
- [ ] `CompilerTests.cs`：UDP 输出字节 17；`ConfigurationTests.cs`：解析 `"udp"`；
      `InvalidInputTests.cs`：未知协议从 17 改为 18；
      `TransportCases.cs`：增加 UDP-only 差分用例（命中 connection 12，不命中 TCP）。
- [ ] 验证：`dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test`。

## 3. ABI v3

- [ ] `src/driver/probe_ioctl.h`
  - `probe_abi_version = 3`。
  - `probe_event` 加 `protocol`（offset 58）与显式 `reserved0`（59），保持 64 字节。
  - `probe_stats` 追加 `udp_classify_calls` / `udp_permitted` / `udp_blocked` /
    `udp_redirected`，更新 size/offset `static_assert`（264 字节）。
  - 删除 `udp_probe_*` 结构与 0x880/0x881/0x883。
- [ ] `src/driver/probe_control.cpp`：删除 UDP 实验 handler 与 switch case；
      stats 重置/初始化不用改（整体清零 + abi_version）。
- [ ] `tools/wfprobe/Protocol.cs`：`AbiVersion = 3`；`ProbeEvent` 加 `Protocol`/`Reserved0`；
      `ProbeStats` 加四个字段；删除实验结构；`ProtocolLayout.Verify` 更新尺寸。
- [ ] `src/userland/WinForward.Userland/Device/ProbeIoctl.cs`：`AbiVersion = 3`。
- [ ] `ProbeStats.cs` / `ProbeEvent.cs`：镜像字段。
- [ ] `DeviceLayout.cs`：`ProbeStatsSize = 264`，更新 LastStatus/AbiVersion 与新字段偏移；
      `ProbeEvent.Protocol` offset 58、`MatchedRule` 仍 60。
- [ ] `AbiConsistencyTests.cs`：`transport_protocol` 增加 UDP 断言；结构/常量自动比对
      会因为头文件与夹具同时变更而通过，仍要显式跑一次。
- [ ] `DeviceAbiTests.cs` / `LayoutTests.cs`：事件 protocol 往返、stats 新字段偏移。
- [ ] 验证：C# 测试全过；`tools/check.sh --full`。

## 4. classify 转正

- [ ] `src/driver/probe_classify.cpp`
  - 删除 `udp_probe_candidate` 与实验分支。
  - 协议早退改为“非 TCP 且非 UDP”。
  - UDP 走现有四道闸与 matcher；`permit` / `block` / `redirect` 三个动作复用。
  - `redirect_classification` 接受协议参数，`destination.protocol` 从 B2a 的硬编码 TCP
    改为按 `facts.protocol` 映射（B2a 检查指出 `probe_classify.cpp:314`）。
  - 每个最终 permit 路径增加 UDP 计数（集中 helper）；block 路径增加 `udp_blocked`；
    redirect 成功增加 `udp_redirected`；进入 classify 且协议 UDP 增加 `udp_classify_calls`。
  - `log_decision` 写 `event.protocol`。
  - 在 UDP 分支/计数点加 `<mutation id="...">` 区域，供变异测试定位。
- [ ] `src/driver/probe_driver.h`：删除 `#include "udp_probe.h"`。
- [ ] `src/driver/probe_driver.cpp`：删除 `udp_probe_start/stop`。
- [ ] `src/driver/wfprobe.vcxproj`：删除四个 `udp_*.cpp`。
- [ ] 删除 `udp_probe.h/cpp`、`udp_redirect.cpp`、`udp_ippacket.cpp`、`udp_inject.cpp`。
- [ ] 验证：VM 上构建/安装；手动 U1/U2/U5/U6 雏形；TCP `rules` / `selftest` / `gates`
      不退化。

## 5. block filter 覆盖 UDP

- [ ] `src/driver/block_filters.cpp`
  - 所有 block filter 显式写协议条件：6 / 17；`any` 与 fallback 各展开两条。
  - 原因：不写条件会误拦 ICMP；`max_block_filters` 64 → 128。
  - `rule_applies_to_layer` 不变；仍只增加拒绝。
- [ ] 删除 `rule_table.cpp` 的 `udp-block-temporary-guard`（B1 临安闸），补一条
      “UDP block 规则可加载”的 VM 用例；`protocol=any` block 同时验证 TCP/UDP 都被拦。
- [ ] `tools/wfprobe/BlobBuilder.cs` / `TestRule`：增加协议参数与 UDP 规则 helper。
- [ ] VM 手动验证：UDP block（send 成功、对端收不到、未 block 端口可达）、`any` block、
      TCP block 对照（仍应是 AccessDenied 一类错误）。
- [ ] 验证：`rules` 套件的 block 用例不退化；UDP block 用例通过。

## 6. 实验脚手架与旧用户态读取

- [ ] `tools/wfprobe/UdpProbeCommands.cs` → `UdpCommands.cs`：只保留 listen / relay / client；
      删除 `Configure` / `PrintStats` / `InjectReturn` / `--inject` / `--return-mode` /
      `--filter-port` / `--target` / `--layer` / `--interface`。
- [ ] `UdpSockets.cs`：IPv4 / IPv6 listener、双 cmsg level、`sockaddr_in6` 解析、
      `IPV6_WFP_REDIRECT_CONTEXT`。
- [ ] `WfpDevice.cs`：删除 UDP 实验方法。
- [ ] `Program.cs`：删除 `udp-probe` / `udp-probe-stats`；usage 更新；保留 UDP 三命令。
- [ ] `src/common/redirect_context.h`：`try_read_original_destination` 接受 UDP。
- [ ] `RedirectContext.cs`：`ProtocolUdp = 17`。
- [ ] `RedirectDestination.cs`：接受两种协议，暴露 `Protocol` / `ProtocolText`。
- [ ] `RelayServer.cs`：解析后要求 TCP，否则 refuse 并记录 `reason=non-tcp-redirect-context`。
- [ ] `tests/host/redirect_context_test.cpp`：UDP 接受、未知协议拒绝。
- [ ] 验证：`tools/check.sh --full`；C# 测试；`wfprobe` 构建。

## 7. wfprobe UDP 套件

- [ ] 新增 `tools/wfprobe/UdpSuite.cs`：按 design.md §8 的 U1–U13 实现。
      listener 在套件进程，客户端在子进程；`self_process_ids = [当前 PID]`；
      解析子进程 `pid=` 与 `send-failed` / `recv=` 输出。
- [ ] `Program.cs`：新增 `udp-rules` 命令；`--timeout-ms` / `--settle-ms`。
- [ ] 池标签：unconnected 按“关闭 socket 后 1s 回基线”写；**connected 先测释放窗口**
      （B2b 观察 180s 仍 outstanding、`sc stop` 后仍存在），决定是延长等待、只对 unconnected
      严格断言，还是标成已知限制。不要直接断言 connected 立即归零。
- [ ] U3 的测量窗口：listener 用同一个 socket 回包时复用入站关联，不重新进入
      `ALE_CONNECT_REDIRECT`（`udp_classify_calls` 不增）。仍在发请求前 reset、收包后先读计数，
      避免把中继新开 socket 的其他出站算进窗口。
- [ ] 把 B2b 新增的 mutation 标记接入 `tools/windows/mutation.ps1`：
      `classify-udp-branch`、`udp-classify-counter`、`udp-permit-counter`、`udp-block-counter`、
      `udp-redirect-counter`、`block-filter-protocol`，并确认每条变异都有 UdpSuite 用例变红。
- [ ] `wfprobe control --clear-self`：`self_process_count=0` 时同时清零 `self_process_ids`，
      避免驱动以 87 拒绝；补一条工具侧回归。
- [ ] `tools/windows/gate.ps1`：在 `rules` 之后加 `udp-rules` 步骤；失败即 gate 失败。
- [ ] 若采用“卸载后 outstanding=0”的断言，在 gate 的卸载路径里读取 `wfprobe pooltag --tag WfPr`
      并纳入断言；注意断言必须发生在结果汇总之前，不能在 `finally` 里做无效断言。
      connected 的残留先按上一条的实测结论处理。
- [ ] 可观测性：`wfprobe Program.PrintEvent` 输出 `protocol`；`PrintStats` 与
      `WinForward.Controller/StateCommand` 输出 4 个 UDP 计数器；U1/U6 依赖这些输出做断言。
- [ ] VM 上跑 `gate.ps1`，至少一次完整 PASS；记录命令与输出。

## 8. 文档与规范

- [ ] `src/driver/README.md`：ABI v3、UDP 规则/context/事件/计数、block 语义、any 行为变化、
      中继回包要求与 self-PID 配置；`udp_blocked` 明确定义为“classify 层 block 决策数”，
      热替换后旧关联的丢弃发生在 auth 层、不会增加该计数。
- [ ] `.trellis/spec/backend/wfp-architecture.md`：
  - D7 从“v1 只做 host TCP”更新为“Host TCP + UDP”；
  - 新增 UDP 实现事实：不需要回程注入、context 每关联、auth block、IPv6 cmsg level、
    `any` 覆盖 UDP、同 socket 回包不重新 classify、self-PID 用于中继新开 socket；
  - 未决清单里 Host UDP 状态更新。
- [ ] `AGENTS.md` 的“结构性缺口”列表把 Host UDP 移除或标注已完成，指向本任务。
- [ ] `research/spike-udp.md` 与 findings 结论入库。
- [ ] `tools/wfprobe` usage / 注释不再出现“实验 ABI”。

## 9. 全量验证

- [ ] 宿主：`tools/check.sh --full`（格式、有效行、cppcheck、clang-tidy、wf_check）。
- [ ] C#：`dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test`。
- [ ] VM：`pwsh -File tools\windows\gate.ps1 -Root <repo> -Remote <target>` 全绿。
- [ ] 变异：`pwsh -File tools\windows\mutation.ps1 -Root <repo> -Remote <target>`；
      新增 UDP 变异锚点必须变红。
- [ ] 重复一次 VM gate，确认无偶发失败；必要时重启 VM 后再跑一次池标签断言。
- [ ] 汇总：验收 A1–A12 逐条勾选，未通过项写明原因与后续任务。

## 回滚点与风险文件

- 高风险文件：`probe_classify.cpp`（动到 TCP 路径）、`block_filters.cpp`（所有 block 规则）、
  `probe_ioctl.h`（ABI）、`rule_blob.h` / `matcher.h`（共享 ABI）、`RelayServer.cs`（TCP 中继）、
  `tools/wfprobe/Protocol.cs`（ABI 夹具）。
- 每完成第 1、3、4、5 步打一个可回退的提交点；第 0 步的临时 spike 改动必须还原，
  不与正式改动混在一起。
- 回退：整批 revert；不提供运行时 UDP 开关。临时停用用 `enabled=0`，但 ABI v3 仍要求
  配套的新用户态。
