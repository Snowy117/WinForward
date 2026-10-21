# 技术设计：Host UDP 正式接入驱动

> 状态：2026-10-21 规划稿，等待评审。产品代码在 `task.py start` 之后才动。
> 目标：把探针的 UDP local redirect 转成规则驱动的正式能力，移除实验脚手架，补齐 ABI、
> 事件、计数、宿主测试与 VM 门禁。

## 1. 结论与边界

- 路线 A 的 redirect 与 per-datagram context 已经在 WinLTSC 上实测成立；回程不需要驱动
  改源或注入，relay 用收到 datagram 的 listener socket 直接 `sendto` 即可。
- 因此正式接入**不新增 WFP 层**：UDP 仍然在现有的
  `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6` 上被 classify，和 TCP 共用 callout、四道闸与
  规则匹配。唯一的新数据面变化是“UDP 不再在 `permitted_non_tcp` 早退”。
- 驱动**不维护 UDP flow / 映射表**。每次 redirect classify 分配一块 36 字节
  `redirect_context`，所有权交给 WFP；回程由用户态完成。
- 本仓库只做驱动、规则编译与 `wfprobe` 验证工具。WinForward 的 UDP listener / relay /
  SOCKS5 UDP ASSOCIATE / UoT 不在本任务实现。

### 1.1 Spike 实测（2026-10-21）

详见 [spike-udp.md](../10-21-host-udp-spike/research/spike-udp.md)。对设计的影响：

- classify 按“本地端点 + 远端目的地”关联一次，不按 datagram；U3 断言改为“K 个新关联 → K”，
  不能写“≤ N”。
- context 随客户端 socket 关闭立即释放（<1s）；socket 保持打开时 150s 内不因 idle 释放；
  `sc stop` 后本实例全回收。池断言从干净启动开始，正式实现删除注入路径后基线才为 0。
- `ALE_AUTH_CONNECT` 能拦住 UDP，但应用侧是**静默丢包**（send 成功、对端收不到、recv 超时），
  TCP 对照仍是 `AccessDenied(10013)`。用户已确认接受静默丢包（2026-10-21）。
- IPv6 / dual-mode 可行；context family 取决于实际层（v4-mapped unconnected 走 V4）。

## 2. 端到端数据流

```
app: sendto / connect (UDP)
  │
  ▼
ALE_CONNECT_REDIRECT_V4/V6  probe_classify_fn
  │  gather_facts → 四道闸 → matcher → permit / block / redirect
  │  redirect:
  │    remoteAddressAndPort := probe_build_redirect_target(...)
  │    localRedirectTargetPID := listener_pid
  │    localRedirectHandle    := state.redirect_handle
  │    localRedirectContext   := redirect_context{ protocol=17, original_dest, pid, rule }
  │
  ▼
listener socket（setsockopt IP_WFP_REDIRECT_CONTEXT / IPV6_WFP_REDIRECT_CONTEXT）
  │  WSARecvMsg → cmsg 逐 datagram 带回 redirect_context
  │  relay: sendto(payload, app_source)   ← 同一个 listener socket
  │
  ▼
app 收到回包（connected 与 unconnected 都接受 relay 源；探针 A4 实测）

block:
  classify 只记 blocked / deferred_blocks 并 PERMIT；
  ALE_AUTH_CONNECT 上的 FWP_ACTION_BLOCK filter 静默丢弃报文（spike 实测：应用 send 成功）。
```

UDP 的 `sendto` 在 WFP 中的路径是
`ALE_CONNECT_REDIRECT_V4 → ALE_AUTH_CONNECT_V4 → ALE_FLOW_ESTABLISHED_V4 → DATAGRAM_DATA_V4`
（微软 UDP Packet Flows 文档），所以 auth 层的 block filter 对 UDP 有效，且不需要
`DATAGRAM_DATA` callout。IPv6 走同一套 V6 层。

## 3. 规则与 blob

### 3.1 C++

- `src/common/rule_blob.h`
  - 新增 `rule_protocol_udp = 17`。
  - `detail::is_known_protocol` 接受 `0 / 6 / 17`；其他值继续整块拒绝。
  - `rule_blob_version` **保持 1**：布局没有变化，新增协议值属于枚举扩展。旧驱动遇到
    `17` 会按“未知协议”拒绝整块 blob（fail-closed），不会静默放行；新旧用户态与驱动
    按同一版本交付。注释里写明这一点。
- `src/common/matcher.h`
  - 增加 `static_assert(transport_protocol::udp == rule_protocol_udp)`。
  - matcher 的协议比较已经是 `selected.protocol != any && != connection.protocol`，
    `protocol=any` 天然覆盖 UDP；不需要逻辑改动。
- `rule_table.cpp` 的 `protocols_overlap` 已经按数值比较，UDP 加入后规则顺序校验自动
  正确；不改判定。

### 3.2 C#

- `RuleProtocol` 增加 `Udp = 17`，注释从“v1 只认任意与 TCP”改为“0 任意 / 6 TCP / 17 UDP”。
- `PolicyConfigurationDocument.ParseProtocol` 接受 `"udp"`；错误文案改为 `any / tcp / udp`。
- `RuleBlobCompiler.ValidateProtocol` 接受 `Udp`；错误文案补 UDP。
- `RuleOrderValidator.Describe` 的协议文本增加 UDP；`ProtocolsOverlap` 不改。
- `PolicyConfigurationDocument` 的 schema 注释同步。

### 3.3 `protocol=any` 的语义

WinForward 产品侧的规则没有 `Protocol` 时匹配所有协议（TCP 包含在内）。内核版从本任务起
`any` = TCP + UDP；这是 **有意的行为变化**：现有“any 规则只在 TCP 上生效”的配置在升级后
也会匹配 UDP。发版说明与驱动 README 要写明。

## 4. classify

### 4.1 移除实验分支

`src/driver/probe_classify.cpp` 当前在 `probe_classify.cpp:393` 附近有
`udp_probe_candidate`，并在 `:476` 调用 `udp_probe_try_redirect`。正式实现：

1. 删除 `udp_probe_candidate` 与整个实验分支。
2. `rules == nullptr` 的早退保持原样（UDP 没有规则时放行）。
3. 协议早退改为：`facts.protocol != IPPROTO_TCP && facts.protocol != IPPROTO_UDP`
   → `permitted_non_tcp`。注释写明该计数器现在的含义是“既不是 TCP 也不是 UDP”。
4. 其余 TCP 路径不动：四道闸、matcher、三个动作。

### 4.2 redirect

- `redirect_classification(...)` 增加一个 `std::uint8_t protocol` 参数（或直接把
  `connection_facts` 传进去），把 `destination.protocol` 设为
  `facts.protocol == IPPROTO_UDP ? transport_protocol::udp : transport_protocol::tcp`。
- context 仍然 36 字节；`family`、`port`、`address`、`process_id`、`rule_index` 不变。
- 失败姿态不变：拿不到 handle / writable data / context 时 fail-open 直连，记
  `redirect_failures`、`apply_failures` / `context_alloc_failures`，事件用
  `redirect_failed` / `context_alloc_failed`，不写 blocked。
- 不新增容量表；context 分配次数按 classify 次数计，`contexts_allocated` 语义不变。

### 4.3 IPv6

- `gather_facts` 已经同时支持 V4/V6；`probe_build_redirect_target` 已按族处理 ANY→loopback
  与 local address 两个分支。UDP 不需要额外代码，但必须有用例覆盖 V6 / dual-mode。
- dual-mode / IPv6 的 context family 取决于实际走的层：spike 实测 v4-mapped unconnected
  走 V4（family=4、纯 v4 地址），v4-mapped connected 走 V6（family=6、v4-mapped 地址），
  不能笼统断言 family=6。U10 必须按客户端 API 路径分别断言。

## 5. block

### 5.1 filter 条件

`src/driver/block_filters.cpp` 当前对 `protocol=any` 强制写成 TCP
（`block_filters.cpp:77`）。正式实现：

- `protocol == tcp` → 加 `FWPM_CONDITION_IP_PROTOCOL = 6`。
- `protocol == udp` → 加 `= 17`。
- `protocol == any` → **不加协议条件**。`ALE_AUTH_CONNECT` 只仲裁 TCP 与 UDP 连接；
  不写条件等价于两者都拦，也保持“filter 只增加拒绝、从不放行”的结构性保证。
  这样 `max_block_filters` 不需要翻倍。
- `fallback_action == block` 的无条件 filter 不变，它本来就覆盖该层所有协议。

### 5.2 可观测语义

- UDP block 由 auth filter 执行；spike 实测为**静默丢包**：应用的 `send` / `sendto` /
  `connect` 成功，对端收不到，`recv` 超时。没有找到让 UDP send 返回 socket 错误的 WFP
  路径；这与包版 WinForward 的静默丢包一致。用户已确认接受该语义（2026-10-21）。
- redirect 层的 classify 仍然记 `blocked`、`matched_block` / `fallback_block`、
  `deferred_blocks`，事件 action=block；真实丢弃在 auth 层发生。
- 不为 UDP 增加 `DATAGRAM_DATA` callout，也不维护 block flow 状态。

### 5.3 规则顺序

- 重叠判定不变；`protocol=any` 与 tcp/udp 都重叠，`tcp` 与 `udp` 不重叠（现有
  `protocols_overlap` 已经如此）。
- 编译期与驱动侧都继续整块拒绝“更早的非 block 规则遮蔽后面的 block 规则”的配置。

## 6. ABI v3

### 6.1 `probe_event`

```c
struct probe_event
{
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
    std::uint8_t  reserved0;         // 59  显式占位，保持 matched_rule 在 60
    std::uint32_t matched_rule;      // 60
};
```

尺寸保持 64。`log_decision` 写 `event.protocol = facts.protocol`。C# `ProbeEvent`、
`tools/wfprobe/Protocol.cs`、`DeviceLayout` 同步加字段/偏移断言。

### 6.2 `probe_stats`

在 `process_path_metadata_seen` 之后、`last_status` 之前追加四个 UDP 维度计数：

```c
std::uint64_t udp_classify_calls; // 进入 classify 且协议为 UDP
std::uint64_t udp_permitted;      // 最终放行（含 redirect 失败后的 fail-open）
std::uint64_t udp_blocked;        // block 决策（真正拒绝在 auth filter）
std::uint64_t udp_redirected;     // 成功挂上 redirect context
```

- 偏移：224 / 232 / 240 / 248；`last_status` 256、`abi_version` 260；总长 264。
- 不变式：`udp_classify_calls == udp_permitted + udp_blocked + udp_redirected`。
  用例要断言这个等式（按单次测试的增量）。
- 计数点集中成两个小 helper（`count_udp_permit` / `count_udp_block`），避免十个返回路径
  各写一遍漏掉。
- `permitted_non_tcp` 的 C# 注释与 README 改为“既非 TCP 也非 UDP”。

### 6.3 版本与兼容

- `probe_abi_version` 2 → 3。`ProbeIoctl.AbiVersion`、`Protocol.AbiVersion`、C# 镜像、
  布局断言、ABI 一致性测试同步。
- `probe_config` / `probe_rule_info` / `redirect_context` 布局不变。
- 旧版 `wfprobe` / 控制器在 ABI 握手段会被拒绝；WinForward 集成必须同步升级。
- `redirect_context` 的 `transport_protocol::udp` 已经存在，无需提升 context 版本。

### 6.4 用户态读取

- `src/common/redirect_context.h` 的 `try_read_original_destination` 接受 TCP 与 UDP。
- `src/userland/WinForward.Userland/Device/RedirectContext.cs` 增加 `ProtocolUdp = 17`。
- `Relay/RedirectDestination.cs`
  - `TryParse` 接受 `ProtocolTcp` / `ProtocolUdp`，保留协议字段；
  - 增加 `Protocol`、`ProtocolText`，日志打印协议；
  - `Relay/RelayServer.cs` 在解析后显式要求 `ProtocolTcp`，否则 refuse 并记录原因
    （TCP 中继不能被 UDP context 误用）。
- WinForward 的 UDP 中继可以直接复用 `RedirectDestination.TryParse` 解析 cmsg 里的 36 字节。

## 7. 实验代码清除

删除/替换清单：

| 位置 | 处理 |
|---|---|
| `src/driver/udp_probe.h`、`udp_probe.cpp`、`udp_redirect.cpp`、`udp_ippacket.cpp`、`udp_inject.cpp` | 删除 |
| `src/driver/probe_driver.h` 的 `#include "udp_probe.h"` | 删除 |
| `src/driver/probe_driver.cpp` 的 `udp_probe_start/stop` | 删除 |
| `src/driver/probe_control.cpp` 的 UDP 实验 handler 与 switch case | 删除 |
| `src/driver/probe_ioctl.h` 的 `udp_probe_*` 结构与 0x880/0x881/0x883 | 删除 |
| `src/driver/wfprobe.vcxproj` 的四个 `udp_*.cpp` | 删除 |
| `tools/wfprobe/UdpProbeCommands.cs` | 改成 `UdpCommands.cs`，只保留 listen / relay / client |
| `tools/wfprobe/WfpDevice.cs` 的 UDP 实验方法 | 删除 |
| `tools/wfprobe/Protocol.cs` 的 UDP 实验结构与 IOCTL | 删除 |
| `tools/wfprobe/Program.cs` 的 `udp-probe` / `udp-probe-stats` | 删除；保留 `udp-listen` / `udp-relay` / `udp-client`，新增 `udp-rules` |

`wfprobe udp-*` 不再是实验 ABI 的客户端，而是正式 ABI 的验证工具；`--inject`、
`--return-mode`、`--filter-port`、`--target`、`--interface` 全部移除。

## 8. wfprobe 验证套件

### 8.1 套件结构

新增 `tools/wfprobe/UdpSuite.cs`，命令 `wfprobe udp-rules`，由 `gate.ps1` 调用。套件进程
自己持有 listener（因此 `listener_pid = 当前 PID`），把当前 PID 放进 `self_process_ids`
（让 listener 的回包被闸 4 放行）；UDP 客户端跑在子进程里（不是 self，首包必须被 redirect）。
子进程使用同一个 `wfprobe.exe`，启动后打印 `pid=`，套件解析后用于断言 context 的 PID。

`UdpSockets` 扩展：

- `CreateListener` 支持 IPv4 / IPv6；IPv6 监听 `[::]` 时开 `DualMode`。
- 同时设置 `IP_WFP_REDIRECT_CONTEXT`（IPPROTO_IP=70）与
  `IPV6_WFP_REDIRECT_CONTEXT`（IPPROTO_IPV6=70），忽略不适用的一侧。
- cmsg 解析同时接受 `cmsg_level == IPPROTO_IP (0)` 与 `IPPROTO_IPV6 (41)`，
  `cmsg_type == 70`。
- `ParseEndpoint` 支持 `sockaddr_in6`；客户端命令行支持 `[addr]:port` 与 v4-mapped 地址。

### 8.2 用例矩阵

| 编号 | 场景 | 断言 |
|---|---|---|
| U1 | unconnected `sendto` + `protocol=udp` redirect | listener 收到；context magic/version/protocol=17/PID/rule/地址端口正确；事件 protocol=17、decision=redirected |
| U2 | connected `connect`+`send` + 回包 | listener 收到；`sendto` 回包后子进程打印 `recv=`；不依赖注入 |
| U3 | 同一目的地连续 N 条 | listener 收到 N 条，context 相同；`udp_classify_calls` / `udp_redirected` / `contexts_allocated` 各只 +1；K 个新关联各 +K |
| U4 | 两个不同目的地 | 两条 context 的地址/端口不同；不串 |
| U5 | permit 规则 | listener 超时；`udp_permitted` 增量、无 context 分配 |
| U6 | block `protocol=udp` | 子进程 `send`/`sendto` 成功、对端收不到、`recv` 超时；listener 无包；`udp_blocked`、`deferred_blocks` 增量；未 block 端口可达 |
| U7 | block `protocol=any` | 同 U6，证明 any 覆盖 UDP；另有一条 TCP block 用例证明 TCP 仍被拦 |
| U8 | 进程选择器 | 匹配当前 exe 的规则命中；`OtherAppId` 规则不命中 |
| U9 | fallback redirect / fallback permit | 未命中规则时按 fallback 动作；事件 matched_rule=0xFFFFFFFF |
| U10 | dual-mode / IPv6 | listener `[::]`；v4-mapped unconnected 断言 family=4，v4-mapped connected / native v6 断言 family=6；原始地址按实际层 |
| U11 | enabled 0↔1 | 关闭时 UDP 直连、listener 无包；重新启用后 redirect 恢复 |
| U12 | 生命周期 | 干净启动基线；`WfPr` allocs 增量 == `udp_redirected`；客户端 socket 关闭后 1s 内 outstanding 回基线；`sc stop` 后 0 |
| U13 | 规则热替换 | 先 permit 建立关联，再换 block blob：原关联可能继续放行（连接级 block 语义），新关联必须被丢；记录事实并写进 README |

U3 的断言按 spike 实测写成“K 个新关联 → K”，不再写“≤ N”：同一 socket 对同一目的地发 N 条
只产生 1 次 classify/redirect/context。U6 按静默丢包写，不写错误码（Q1 已确认）。

### 8.3 池标签与清理

- `WfPr`（redirect context）：套件在开始前取基线，结束后再取；断言分配次数等于实际
  redirect 次数。spike 已确认客户端 socket 关闭后立即释放：断言 frees == allocs、
  outstanding 回到基线；`sc stop` 后 outstanding == 0。池断言必须从干净启动开始，
  实验注入路径的 NBL 基线在正式实现删除后不再存在。
- `WfTr`（规则表）：UDP 用例不改变规则表生命周期，沿用现有断言。
- `gate.ps1` 新增 `udp-rules` 步骤；不改变现有 TCP 套件顺序与预算。

### 8.4 宿主测试

- C++ `tests/host/rule_blob_test.cpp`：17 接受；18 拒绝；UDP 规则的完整解码。
- C++ `tests/host/matcher_test.cpp`：UDP 规则只命中 UDP；any 命中 UDP；TCP 规则不命中 UDP。
- C++ `tests/host/redirect_context_test.cpp`：UDP context 可读；未知协议拒绝。
- C#：`CompilerTests` 断言 UDP 字节 17；`ConfigurationTests` 解析 `"udp"`；
  `InvalidInputTests` 的未知协议改用 18；`TransportCases` 增加 UDP-only 差分用例；
  `DeviceAbiTests` / `LayoutTests` / `AbiConsistencyTests` 覆盖 ABI v3。
- 每条新增校验都要有“删掉后用例变红”的变异验证；驱动侧在 `probe_classify.cpp` /
  `block_filters.cpp` 加 `<mutation id=...>` 区域，接入 `tools/windows/mutation.ps1`。

## 9. 兼容性与发版说明

- ABI v3：旧 `wfprobe` / 控制器 / WinForward 集成必须同步；驱动与用户态同批发布。
- `protocol=any` 从“仅 TCP”变成“TCP + UDP”：现有配置可能开始代理 UDP。发布说明要
  明确；需要维持旧行为的用户应把规则显式写成 `protocol=tcp`。
- `block` 规则若没有进程选择器，auth filter 也会拦到中继自己的上游 UDP 流量
  （与现有 TCP 行为一致）。这是配置编写约束，写进 README：需要放行中继出站时，
  用进程选择器收窄 block 规则。
- `permitted_non_tcp` 含义收窄为“既非 TCP 也非 UDP”，旧看板/日志的解读要同步。

## 10. 风险与回退

| 风险 | 处理 |
|---|---|
| `ALE_AUTH_CONNECT` 对 UDP 的 BLOCK 是否真的让 `send` 失败 | 第 0 步 spike 先验证；失败则回到规划，评估 `DATAGRAM_DATA` 方案，不在本设计里硬做 |
| UDP 关联的 context 释放时机（UDP 终止不在 WFP 层上报） | spike 测分配/释放；池断言按实测值写；卸载后必须归零 |
| IPv6 / dual-mode 的 cmsg level 与 redirect target | `IPV6_WFP_REDIRECT_CONTEXT=70` + 双 level 解析；U10 覆盖；失败记录为明确缺口 |
| listener 回包被再次 classify | self-PID 闸覆盖；U2/U3 验证；relay 必须用同一 listener socket 回包 |
| `protocol=any` 行为变化 | 发布说明 + 显式 `tcp` 的迁移路径 |
| ABI v3 破坏旧客户端 | 同批发布；启动时 ABI 握手拒绝旧版 |
| 已授权 UDP 关联在 block 规则热替换后继续放行 | B2 补一条热替换用例；按“连接级 block”记录为已知语义，不假装每包阻断 |

回退：本任务不引入运行时 UDP 开关；回退靠 revert 整批提交。若只想临时停用，
`enabled=0` 会停止所有 redirect，但 ABI v3 仍要求配套的新用户态。

## 11. 验收映射

| 验收 | 对应验证 |
|---|---|
| A1 / A2 | U1 / U2 |
| A3 | U3 / U4 |
| A4 | U5 / U9 |
| A5 | U6（send 成功 + 对端收不到 + 未 block 端口可达）+ U13 热替换用例 |
| A6 | U7 / U8 |
| A7 | U10 |
| A8 | 可达放行分支（U5/U11）+ 失败分支的代码审查与变异测试（不注入驱动故障） |
| A9 | U12 + 池标签 + `gate.ps1` 卸载 |
| A10 | 现有 `rules` / `selftest` / `gates` |
| A11 | `tools/check.sh --full` + 宿主测试 + 变异 |
| A12 | 文件删除清单 + README / spec 更新 |
