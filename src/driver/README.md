# WFP 重定向驱动（规则表版）

WDM 驱动，在 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6` 上挂 callout，按用户态下发的规则 blob
决定放行 / 重定向；block 规则由 `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK`
filter 执行。host TCP 与 host UDP 共用同一套 callout、四道闸与规则匹配：TCP 每连接 classify
一次，UDP 每关联（本地端点 + 远端目的地）classify 一次，不按 datagram 重复分类。Phase 0 的
硬编码探针行为已被规则表取代。

## 文件

| 文件 | 内容 |
|---|---|
| `probe_ioctl.h` | 控制面 ABI v4：IOCTL 码、配置、计数、host 规则表回读、forwarded 表回读、事件 |
| `rule_table.h/.cpp` | 规则 blob 的池内存副本、校验（含 selector UTF-8/511、fallback 冲突、顺序冲突）、引用计数、原子替换与退役链 |
| `probe_driver.h` | 驱动内部共享面（状态、锁、窄接口、callout 原型、include 顺序约束） |
| `probe_driver.cpp` | `DriverEntry`/卸载、设备、WFP provider/callout/filter 的注册与拆除 |
| `block_filters.cpp` | 把 block 规则编译成 ALE_AUTH_CONNECT 的 `FWP_ACTION_BLOCK` filter（`protocol=any` 与 fallback 按 6/17 展开；增删/滚动替换） |
| `probe_control.cpp` | IOCTL 处理：配置（含 0↔1 切换时 block filter 的挂/摘）、规则下发、计数、事件、调用者校验 |
| `probe_classify.cpp` | classify：facts、四道防循环闸、matcher、三个动作、重定向 |
| `process_path.cpp` | `ALE_APP_ID` 的 UTF-16 → UTF-8 转换（511 字节边界、代理对）与 0x807 诊断 |
| `probe_forwarded.h/.cpp` | MAC 帧层（`INBOUND/OUTBOUND_MAC_FRAME_NATIVE`）的 forwarded 规则表、blob 校验、generation、IOCTL 面；callout / filter 的注册与注销 |
| `probe_forwarded_classify.cpp` | NBL 解析（VLAN/IPv4/IPv6/TCP/UDP）、方向映射、防环、`match_forwarded`、pass/block 与计数 |
| `probe_forwarded_state.h` | 两个翻译单元共享的 forwarded 状态、自旋锁与「计数增量原子发布」helper |
| `wfprobe.vcxproj` | WDK 工程，`SignMode=Off`；签名由 `WfBuild.psm1` 调 signtool 完成 |

## 控制面

- `ioctl_set_rule_blob`：下发整块 `rule_blob`（`src/common/rule_blob.h`）。校验失败返回
  `STATUS_INVALID_PARAMETER`，当前规则表保持不变；成功则原子替换并 `generation++`。
- `ioctl_get_rule_info`：规则数、兜底动作、总长、generation。
- `ioctl_get_config` / `ioctl_set_config`：启用开关、监听端口 / PID、self-PID 集合。
- `ioctl_get_stats` / `ioctl_reset_stats`：classify 次数、各动作计数、被拒绝的 blob 数、
  context 分配/失败、四道闸的细分计数、block 决策计数，以及四个 UDP 计数器
  `udp_classify_calls` / `udp_permitted` / `udp_blocked` / `udp_redirected`。
  对任意一次 reset 之后的增量，`udp_classify_calls == udp_permitted + udp_blocked + udp_redirected`。
  `udp_blocked` 是 classify 层的 block 决策数，不是实际被丢掉的 datagram 数；`udp_permitted` 包含
  redirect 失败后的 fail-open；listener 用同一个 socket 回包时复用入站重定向关联，不重新
  classify，`udp_classify_calls` 不增；中继新开 socket 的出站才是新关联，会再计入一次
  `udp_classify_calls` / `udp_permitted`。
  `permitted_non_tcp` 现在只表示“协议既非 TCP 也非 UDP”。
- `ioctl_get_events`：64 条环形事件（decision / action / branch / 命中规则序号 / 地址族 /
  `protocol`）。`protocol` 取 `IPPROTO_TCP(6)` 或 `IPPROTO_UDP(17)`，位于偏移 58；
  `probe_event` 仍是 64 字节。`probe_stats` 扩到 264 字节，四个 UDP 计数器位于
  224 / 232 / 240 / 248。
- `ioctl_measure_process_path`（0x807，诊断，**发布前可移除**）：把 UTF-16LE 缓冲区按
  `collect_process_path` 的规则转成 UTF-8，用于验证 511 字节边界与代理对行为；只读、无状态、
  admin-only。存在的唯一理由是长路径进程无法在测试环境构造，删掉它那段逻辑就不可验证。
- `ioctl_set_forwarded_blob`（0x808）：下发整块 `forwarded_blob`（`src/common/forwarded_blob.h`）。
  ABI 非法整块拒绝并返回 `STATUS_INVALID_PARAMETER`；含 `redirect` 规则时整块拒绝并返回
  `STATUS_NOT_SUPPORTED`（B4a 不执行重定向，见下文）；两种失败都不改动旧表。
- `ioctl_get_forwarded_info`（0x809）：`installed` / 规则数 / `fallback_action` / `total_size` /
  `network_pool_count` / `generation`。
- `ioctl_get_forwarded_stats`（0x80A）与 `ioctl_reset_forwarded_stats`（0x80B）：MAC 帧层的计数与
  清零，与 host 的 `probe_stats` 完全独立（`ioctl_reset_stats` 只清 host 计数）。

控制面 ABI 版本是 **4**。host 侧结构不变：`probe_config` / `probe_rule_info` / `probe_event` /
`probe_stats` / `redirect_context` 的布局与语义与 v3 完全一致。v4 只新增四个 forwarded IOCTL 与
`probe_forwarded_info`（32 字节）/ `probe_forwarded_stats`（152 字节）两个结构，旧版 `wfprobe` /
控制器仍会在下发配置时被 `STATUS_REVISION_MISMATCH` 拒绝，驱动与用户态必须同批升级。

`wfprobe events` 会打印 `protocol=`，`wfprobe stats` 与控制器 `state` 会输出四个 UDP 计数器。

全部 IOCTL 走 `caller_is_admin`；设备用 `IoCreateDeviceSecure` + `SDDL_DEVOBJ_SYS_ALL_ADM_ALL`。
`FILE_ANY_ACCESS` 没有放宽访问控制：DACL 与调用者校验才是边界。

## 规则与防循环

规则 blob 的第一条命中决定动作（`permit` / `block` / `redirect`，未命中用 `fallback_action`）。
协议字段取 `rule_protocol_any=0` / `tcp=6` / `udp=17`；`any` 同时覆盖 TCP 与 UDP（相对旧版
“any 只匹配 TCP”的行为变化）。17 是规则 blob 的枚举扩展，布局没有变化，因此
`rule_blob_version` 仍是 1；旧驱动会把 17 当作未知协议拒绝整块 blob（fail-closed）。

classify 依次检查：

1. 目标端口等于配置的监听端口 → 放行（闸 1）；
2. `previousVersion->localRedirectHandle` 非空（别的 callout 已改写）→ 放行；
3. `FwpsQueryConnectionRedirectState0` 报告 BY_SELF / PREVIOUSLY_BY_SELF → 放行（闸 2）；
4. 条件标志含 `IS_CONNECTION_REDIRECTED` 或 `IS_PROXY_CONNECTION` → 放行（闸 3）；
5. 发起进程在 self-PID 集合里 → 放行（闸 4）。

UDP 的 connected 路径（`connect` + `send`）与 unconnected `sendto` 也会走到这个层；四道闸、规则匹配和三个动作与 TCP 共用。
同一客户端 socket 对同一远端的后续 datagram 复用第一次的决策与 redirect context。

`block` 决策在 redirect 层只记账并放行；真正的拒绝由 auth 层的 block filter 完成。TCP 客户端
通常看到 RST（`AccessDenied(10013)`），UDP 则是静默丢包，详见下一节。TCP 侧的背景见
`.trellis/tasks/10-20-wfp-driver-rules/research/findings.md` §2。

## Host UDP

### 规则语义

- `protocol=udp` 写 17；`protocol=tcp` 写 6；`protocol=any` 同时匹配 TCP 与 UDP。
- 这是有意的行为变化：现有“any 规则只在 TCP 上生效”的配置升级后也会匹配 UDP。需要维持旧行为
  时把规则显式写成 `protocol=tcp`。
- `permitted_non_tcp` 的含义收窄为“既非 TCP 也非 UDP”，旧看板/日志的解读要同步。

### 重定向

UDP 没有新增 WFP 层：仍由 `ALE_CONNECT_REDIRECT_V4/V6` 上的同一个 callout 按关联 classify。
每次 redirect 分配一块 36 字节 `redirect_context`，其中 `protocol=17`，family / 原始地址 /
端口 / 进程 PID / 规则序号与 TCP 相同；所有权交给 WFP，驱动不释放。

listener socket 设置 `IP_WFP_REDIRECT_CONTEXT`（`IPPROTO_IP`）或 `IPV6_WFP_REDIRECT_CONTEXT`
（`IPPROTO_IPV6`），用 `WSARecvMsg` 的 cmsg 逐 datagram 取回 context。回程不经过驱动：
中继用**收到 datagram 的同一个 listener socket** 把响应 `sendto` 回 datagram 的源地址。
驱动不做回程注入、不改源地址、不维护 UDP flow / mapping 表。listener / 中继进程必须把自己放进配置的
`self_process_ids`：同一个 listener socket 的回包复用入站重定向关联，不会再次进入
`ALE_CONNECT_REDIRECT`（实测 `udp_classify_calls` 不增）；但中继新开 socket 访问上游或其他出站
仍会 classify，所以 listener / 中继进程仍应把自己放进 `self_process_ids`，靠闸 4 放行。

IPv6 / dual-mode 的 context family 取决于 WFP 实际走的层，不能只看客户端 socket：
v4-mapped + `sendto`（unconnected）走 V4（family=4、纯 v4 地址），v4-mapped + `connect`
走 V6（family=6、v4-mapped 地址），native IPv6 两种模式都是 V6。listener 要同时接受
cmsg level 0（`IPPROTO_IP`）与 41（`IPPROTO_IPV6`），以及 `sockaddr_in6`。

### block

UDP block 规则编译成 `ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK` filter。应用侧是
**静默丢包**：`connect` / `send` / `sendto` 返回成功，对端收不到，`recv` 最终超时；没有可用的
socket 错误码路径。TCP 对照仍是 `AccessDenied(10013)`。

`protocol=any` 与 `fallback_action=block` 都按 6 和 17 各生成一条 filter（IPv4 / IPv6 每层一对），
每条都显式写 `FWPM_CONDITION_IP_PROTOCOL`，不使用无条件 filter；这样 ICMP 等非 TCP/UDP 协议
不会被误拦。`max_block_filters` 为 128。

`udp_blocked` 只统计 classify 层的 block 决策。规则热替换后，旧 UDP 关联的后续 datagram 会在
auth 层被新 filter 丢弃，但不会重新进入 `ALE_CONNECT_REDIRECT`，因此 `udp_classify_calls` /
`udp_blocked` 不增加，也没有新事件；只有新关联才按新 blob 分类。

与 TCP 相同，block 规则若没有进程选择器，也会拦到中继自己的上游 UDP 流量。需要放行中继出站时，
用进程选择器收窄 block 规则。

### 生命周期

unconnected UDP 的 redirect context 随客户端 socket 关闭释放（B0 实测 1 秒内）。connected 关联的
释放时机由 WFP 决定，目前**没有“关闭 socket 立即释放”的保证**；B2b 观察到客户端退出 180s 后
仍 outstanding、`sc stop` 后仍然存在。池断言只对 unconnected 做严格判定，connected 按实测记录
限制，不能写成“立即释放”。

**已知 WFP 缺陷（26100）**：connected UDP 每发一条 datagram，`tcpip` 会给 redirect record
加一次引用且不归还，36B `WfPr` 与 `Arcl` record 直到重启才释放；实测 1/3/100 条 datagram →
record refcount 1/3/100。详见
[`wfp-connected-udp-record-refcount-leak.md`](../../.trellis/spec/backend/wfp-connected-udp-record-refcount-leak.md)。

### 验证

`wfprobe udp-rules` 是正式验证入口，覆盖 redirect / permit / fallback / block / any / ICMP /
进程选择器 / IPv6 / enabled 0↔1 / 多 datagram / 多目的地 / 热替换 / 生命周期（U1–U13）。
`wfprobe udp-listen` / `udp-relay` / `udp-client` 是手工排查
工具，不是门禁入口。

套件进程自己持有 listener，并把自身 PID 放进 `self_process_ids`（用于中继新开 socket 的出站；
listener 同 socket 回包复用入站关联，不重新 classify）；UDP 客户端在子进程里运行、不在 self 集合，首包必须被 redirect。

## Forwarded（MAC 帧层）

B4a 在现有 ALE 路径之外再注册一组 callout，挂在 `FWPM_LAYER_INBOUND_MAC_FRAME_NATIVE` 与
`FWPM_LAYER_OUTBOUND_MAC_FRAME_NATIVE` 上，只处理 `forwarded_blob` 下发的规则。两条路径的对象、
规则表、generation 与计数彼此独立：host 的 ALE callout、`probe_config`、`probe_stats` 语义不变。

### 层、字段与 NBL 解析

- 两层都用 `FWPS_LAYER_*_MAC_FRAME_NATIVE` 常量注册（实测入向 layer id 58、出向 59；代码不写死数字）。
- 这两层的 `inFixedValues` 只有 `NDIS_MEDIA_TYPE` / `NDIS_PHYSICAL_MEDIA_TYPE` / `INTERFACE` /
  `INTERFACE_TYPE` / `INTERFACE_INDEX` / `NDIS_PORT` / `L2_FLAGS` / `COMPARTMENT_ID`：没有 MAC、
  EtherType、IP 与端口。驱动自己从 `layer_data`（`NET_BUFFER_LIST*`）解析，数据自以太网头开始。
- 解析窗口取 `NdisGetDataBuffer` 的前 192 字节：以太网头 14 + 最多两个 VLAN 标签 8 + IPv4 选项
  或 IPv6 扩展头 + 传输层头部足够。数据连续时 `NdisGetDataBuffer` 返回原指针并忽略调用方缓冲区，
  实现只用返回值（B3 的排查结论）。
- VLAN 按 `0x8100`（802.1Q）与 `0x88A8`（802.1ad）逐层剥，最多两层；IPv4 用 IHL、IPv6 走
  逐跳 / 路由 / 目的选项 / 分片四类常见扩展头（上限四跳）。非首片没有传输层端口：协议照常参与
  匹配，`remote_port` 记为 0。
- 非 IPv4/IPv6 帧（ARP、LLDP 等）**不参与 forwarded 规则**：一律放行并单独计数。forwarded 规则
  是 IP 流维度，B4a 不能因为一条规则把 ARP 拦掉导致整段失联。
- NBL / 以太网 / IP / 传输层头部解析失败：放行并计入 `malformed`，不误伤。

### 方向与匹配事实

`forwarded_connection_view` 的方向是「客户端 → 远端」：

| 方向 | client_address | remote_address | remote_port |
|---|---|---|---|
| 入向（外部客户端 → 本机） | 源地址 | 目的地址 | 目的端口 |
| 出向（本机 → 外部客户端） | 目的地址 | 源地址 | 源端口 |

`ingress_adapter_id` 取两层各自的 `FWPS_FIELD_*_INTERFACE_INDEX`（就是 `ifIndex`，与用户态
`NetworkInterface` 的 IPv4/IPv6 Index 同一编号空间）；`0xFFFFFFFF` 表示任意入口。
`protocol` 用 `IPPROTO_*`（TCP 6 / UDP 17 / ICMP 1 …），`family` 用 4/6。非 TCP/UDP 的
`remote_port` 为 0，只有端口区间包含 0 的规则才会命中它们。

### 动作与 block 语义

- 第一个命中的规则决定动作；未命中用 blob 头部的 `fallback_action`（只允许 permit/block）。
- pass：`FWP_ACTION_PERMIT`，原帧继续走原路径，驱动**不修改** NBL。
- block：`FWP_ACTION_BLOCK`，帧被丢弃。**本批不注入 RST，也不伪造任何回包**：被拦的 TCP 连接
  在客户端表现为 SYN 重传后超时/连接失败，UDP 表现为静默丢包。RST 注入留到 B4b 评估。
- filter 用 `FWP_ACTION_CALLOUT_TERMINATING`（INSPECTION 不能改动作，block 会不生效），子层
  `FWPM_SUBLAYER_UNIVERSAL`。**MAC_FRAME_NATIVE 层当前没有 Windows 防火墙 filter**：防火墙仍在
  ALE 层（§7.9 的实测只适用于 ALE），所以这里的终止型 PERMIT 不会碰到防火墙；它只对同层里权重
  更低的第三方 L2 filter 有优先权，多驱动共存（WinpkFilter 等）需要 B4b 实测。
- 没有 forwarded 表时全部放行并计入 `no_table`；表存在但规则为空时走 fallback。
- redirect 规则由 B4b 的数据面执行（见下一节）；B4a 的 permit/block 路径本身不修改帧。

### 防环

classify 一开始就用 `FwpsQueryPacketInjectionState0(injection_handle, nbl, nullptr)` 查询注入状态：
`FWPS_PACKET_INJECTED_BY_SELF` / `FWPS_PACKET_PREVIOUSLY_INJECTED_BY_SELF` 的帧直接 permit 并计入
self_injected，不再参与规则匹配。注入句柄在 probe_forwarded_start 里用
FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_L2, ...) 创建；B4b 的 DNAT/回程注入共用它。

### 计数

`probe_forwarded_stats` 的字段与恒等式：

    classify_calls == no_write_right + inbound_frames + outbound_frames
    inbound_frames + outbound_frames ==
        self_injected + no_table + malformed + non_ip + permitted + blocked + redirected
    permitted == matched_permit + fallback_permit
    blocked   == matched_block + fallback_block + redirect_blocked + redirect_return_failed
    matched   == matched_permit + matched_block + matched_redirect
    fallback  == fallback_permit + fallback_block

`inbound_frames` / `outbound_frames` 是经过该层的全部帧（含 WinRM 等背景流量），不是测试帧数。
按 adapter / 规则序号的细分计数留给 B4b/B6。

### 与 host 路径的边界

- host 的 `rule_table`、`probe_config`、`probe_stats`、ALE callout 与 block filter 不动；
  `ioctl_reset_stats` 只清 host 计数。
- 注册顺序 host → forwarded，卸载逆序：`stop_wfp` 先拆 L2 callout/filter，再拆 ALE。
- forwarded 表替换与 host 换表共用同一把 `rule_mutex` 串行化（两边的退役等待都发生在 PASSIVE）；
  两边的表本身互不影响。
- forwarded 的 `generation` 与 host 的 `generation` 各自独立递增。

### 验证

```powershell
wfprobe forwarded-rules --peer 192.168.100.4:40250 --timeout-ms 3000
wfprobe forwarded-rules --only F1i --inbound-port 40260 --inbound-wait-ms 45000
wfprobe forwarded-rules --restore-only          # 只恢复空 permit 表（看门狗 / 运维兜底）
```

默认值面向本套实验环境：`--local-ip 192.168.100.2`（用于推导入口 ifIndex 与 client/remote CIDR）、
`--peer` 无默认（出向用例缺它就算 SKIP）、`--inbound-port 40260`、`--source-port 40310`（F1/F2/F4/F5
依次用 base+0..base+6）、`--watchdog-seconds` 默认 max(90, inbound-wait + 60)。

SKIP 不等于通过：F1/F2/F3/F6 是必需用例，缺 `--peer` 等前置条件被跳过时套件以退出码 6 结束并在
汇总里打印 `verdict=SKIPPED required_skipped=True`；F7 需要 `--peer6` 且本 VM 默认关闭 Internal IPv6，
属于**环境未验证**，同样计入 skipped 但不单独判失败。gate.ps1 跑的是 `--only F1,F2,F3,F6`。

F2/F4 的 block 规则用固定源端口 + 端口收窄（出向 `remote_port` = 源端口），只命中测试流，不碰 WinRM
的 5985/5986；整个套件外层还有一个恢复看门狗，进程被杀时超时后恢复空 permit 表。

`wfprobe forwarded-rules` 是这一层的验证入口：

- F1/F2：本机主动连外部（出向）的 permit / block（`--peer HOST:PORT`）。
- F3：fallback permit / block。fallback=block 的用例先放行 WinRM 的 5985/5986 并启动一个本地
  看门狗（套件级，超时恢复空 permit 表），用 ping 网关触发未命中帧。
- F4：同一组规则换顺序，验证 first-match。
- F5：client/remote CIDR 命中与不命中、端口不命中。
- F6：非法 blob（magic / 截断 / 非 redirect 带 upstream）整块拒绝、generation 不变、`rejected_blobs` 增加。
- F7：IPv6 happy path（`--peer6`，需要两端有可达的 v6 地址；本环境 Internal 的 IPv6 绑定默认关闭）。
- F1i/F2i：外部客户端 → 本机监听端口（入向）的 permit / block；用 `--inbound-wait-ms` 让套件
  停在监听状态，外部客户端在窗口内发起连接。

block 用例只断言「连接失败/超时」与计数，不承诺 RST —— 与上面的 block 语义一致。

### 未覆盖 / B4b 边界

- B4b 的数据面见下一节；本节的 permit/block 语义在 B4b 下保持不变。
- UDP forwarded 规则的入向验证在 B5；B4b 对 UDP redirect 规则仍 block。
- 合成 NIC（netvsc）与 Internal vSwitch 入口已验证；物理 NIC / External switch / VMQ-SR-IOV、
  嵌套 guest、VLAN 标签帧、LWF 对注入帧的可见性均未验证。
- 每帧解析与注入的成本未测，B6 做 benchmark。

## Forwarded TCP 数据面（B4b）

B4b 在 B4a 的 L2 callout 之上执行 TCP 的入口 DNAT、redirect 表与回程 SNAT。原帧一律被消费
（FWP_ACTION_BLOCK），改写后的副本才注入；不存在「PERMIT 原帧再注入副本」的加法路径。

### 配置与 ABI

probe_forwarded_config（ABI v5，28 字节）：

| 字段 | 说明 |
|---|---|
| enabled | 1 = 启用 redirect 数据面；0 = 关闭并清空 redirect 表 |
| listener_port | DNAT 目标端口；也是回程识别的源端口 |
| family | 4 / 6；必须与入口帧地址族一致 |
| listener_address[16] | DNAT 目标地址 L（listener 所在的本机地址） |

- ioctl_set_forwarded_config / ioctl_get_forwarded_config（0x80C / 0x80D）。
- ioctl_query_forwarded_redirect（0x80E）：输入 probe_forwarded_redirect_query
  （listener_port + client 地址/端口），输出 probe_forwarded_redirect（原始服务器、upstream_id、
  rule_index、ingress、state）。查不到返回 STATUS_NOT_FOUND。
- 关闭配置或在 listener 端口/地址/族变化时清空 redirect 表；只换规则 blob 不清表，旧流继续用旧表项
  与旧 upstream（表项带建表时的 generation / upstream_id）。
- probe_forwarded_stats 在尾部追加 redirect 计数（matched_redirect、redirected、
  redirect_created/ingress/return/blocked/...、redirect_active/high_water），并追加诊断计数
  redirect_multi_nb、inject_complete_calls、inject_complete_failed；结构 296 字节。v4 的 0–143
  字段偏移不变，redirect_active/high_water/last_status/abi_version 整体后移（ABI v5，旧夹具必须同批升级）。

### redirect 表

- 入口 key：(ingress_ifindex, family, client_ip, client_port, server_ip, server_port)；
  回程 key：(family, listener_port, client_ip, client_port)。两条索引指向同一批表项（哈希桶 + 链）。
- 容量 4096。SYN-only 帧建表；数据帧命中后把状态从 syn 提升到 established。
- FIN/RST 标记 closing，保留 5s grace；syn 30s、established 300s 无活动后回收。
  超时回收是惰性的：查表时判定过期，槽位在下一次建表失败扫描时释放。
- 客户端端口复用时，回程 key 冲突的旧表项先被删除，再插入新表项。
- 表项记录入口帧的源 MAC（client_mac）与目的 MAC（local_mac），回程注入用它们还原以太网头。

### 入口 DNAT

命中 redirect 规则的入向 TCP 帧：

1. 非 TCP（B4b 的 UDP 仍归 B5）→ 原帧 block + redirect_non_tcp。
2. 没有 config / L / 族不匹配 → block + redirect_no_config。
3. 查入口表：命中则 DNAT；未命中且是纯 SYN → 建表后 DNAT；未命中且非 SYN → block +
   redirect_no_state（无法中继已建立连接）。
4. 表满 → block + redirect_capacity；注入失败 → block + redirect_inject_failed。
5. DNAT = 目的地址/端口改成 (L, listener_port)，重算 IPv4 头校验和与 TCP 校验和（IPv6 用伪头），
   复制整帧后用 FwpsInjectMacReceiveAsync0 注入；原帧 FWP_ACTION_BLOCK。

出向帧命中 redirect 规则时不能执行本机 DNAT，按失败关闭处理（block + redirect_no_state）。

### 回程 SNAT

出向 TCP 帧的源端口等于 listener_port 时按回程 key 查表。MAC 层一个 NBL 可以携带多个 NET_BUFFER
（每个 NET_BUFFER 是一帧），必须逐 NET_BUFFER 查询/改写/注入；这是 B4b 返工修掉的真实丢包根因：
只处理首个 NET_BUFFER 时，同一 NBL 里后续的 1448B/376B 段被 block 后没有副本，接收端出现 1824B 空洞，
TCP 停在 ISN+1 或只收到 60–63KiB。

- 命中：每个 NET_BUFFER 独立复制、源地址/端口改回原始服务器 (server_ip, server_port)，目的 MAC =
  表项 client_mac，源 MAC = 表项 local_mac，重算校验和，FwpsInjectMacSendAsync0 注入入口网卡；
  整个原 NBL block。FIN/RST 同时把表项标记 closing。
- 未命中：不误改，继续走 B4a 的规则匹配（通常 fallback permit）；只有确实出现 listener 源端口候选
  但查表失败时才计 redirect_return_miss，普通本机出向流量不会污染这个计数。
- 一个 NBL 里的 NET_BUFFER 必须属于同一 listener 回程流；B4b 按同流假设处理，混合流不在本版支持范围。
- 回程注入调用失败：block 原帧 + redirect_return_failed；部分 NET_BUFFER 注入失败时仍计 redirected，
  失败段数进 redirect_inject_failed，由 TCP 重传恢复，不 fail-open。
- 注入完成例程检查 NET_BUFFER_LIST_STATUS：非 SUCCESS 计 inject_complete_failed 并记录 last_status，
  不再静默丢副本。
### 防环与注入

- 两层 classify 开头显式调用 FwpsQueryPacketInjectionState0；本驱动注入的帧直接 permit 并计
  self_injected。
- 注入的 NBL 由 FwpsAllocateNetBufferAndNetBufferList0 分配，数据缓冲用独立池标签 WfIj；
  完成例程释放 NBL / MDL / 数据。MDL 用 NdisAllocateMdl 且 NdisHandle 传 NBL 池句柄：
  传 nullptr 会被 PREfast C6387 判缺陷；改用 IoAllocateMdl 的 MDL 实测注入后被本机栈丢弃。
- 帧长按整帧复制，不截断到 1600 字节；VLAN 标签与 IPv4 选项 / IPv6 扩展头按解析出的偏移处理。
- 一帧一个注入 NBL：即使源 NBL 有多个 NET_BUFFER，也逐帧 FwpsAllocateNetBufferAndNetBufferList0，
  避免把一个 NBL 的整块数据当成单个以太网帧发送。
- stop 时先 FwpsInjectionHandleDestroy0（文档保证等待 outstanding 回调），再显式等待
  outstanding_injections 归零，最后销毁 NBL 池，避免完成例程晚到踩已释放池。
- 帧长按整帧复制，不截断到 1600 字节；VLAN 标签与 IPv4 选项 / IPv6 扩展头按解析出的偏移处理。
### 计数

在 B4a 恒等式之外追加：

    inbound_frames + outbound_frames ==
        self_injected + no_table + malformed + non_ip + permitted + blocked + redirected
    blocked == matched_block + fallback_block + redirect_blocked + redirect_return_failed
    matched == matched_permit + matched_block + matched_redirect
    matched_redirect == redirect_ingress + redirect_blocked
    redirect_blocked == no_config + no_state + capacity + non_tcp + inject_failed
    redirected == redirect_ingress + redirect_return

redirect_active / redirect_high_water 是快照时的表状态；reset_forwarded_stats 把 high_water
重置为当前 active。

### 验证

VM 侧（套件会打印 [forwarded-tcp] READY 后等待外部客户端）：

    wfprobe forwarded-tcp --phase core --local-ip 192.168.100.2 --listener-port 40400 \
        --remote-address 10.99.0.1 --remote-port 40250 --backend 192.168.100.4:40250
    wfprobe forwarded-tcp --phase upstreams ...   # 两条规则 → 两个 SOCKS5 测试上游
    wfprobe forwarded-tcp --phase fail ...        # 只有规则、没有 config：连接超时、redirect_no_config 增加
    wfprobe forwarded-tcp --restore-only          # 兜底：disabled config + 空 permit 表

core 断言：listener accept、IOCTL 查回 server/upstream/ingress、直连或 SOCKS5 中继字节一致、
半关闭正常收尾、redirect_created/ingress/return 增长、帧去向与 redirect 拆分恒等式成立。
upstreams 断言两个端口分别命中 upstream 0/1 且两个测试 SOCKS5 各收到一次 CONNECT。
fail 断言 listener 收不到连接、redirect_no_config 与 redirect_blocked 增长。

Linux 侧 E2E：外部客户端连「原始服务器地址:端口」，套件/控制器把它 DNAT 到 listener 并中继回
Linux 目标；客户端看到的对端仍是原始地址。环境没有 root 加测试路由时，用 VM Internal 网卡上的
on-link 别名（例如 192.168.100.99/24）代替 10.99.0.0/24 路由；两种拓扑验证的是同一条 L2 入向
路径，测试后都要删除别名/路由与防火墙规则。

B4b 实测证据（2026-10-23）：
- 根因：回程只处理 NET_BUFFER_LIST_FIRST_NB。pktmon 抓到 .99:40250→.4 的 44×1448B 已发出，
  下一个 376B 段完全没出现；同一 NBL 的多个 NET_BUFFER 被 block 后只注入了首帧，接收端出现
  63712B/1824B 空洞，TCP 停在 ISN+1 或 60–63KiB。修复后 multi_nb=7~33，单客户端 10/10、
  core 13/13、5 客户端并发（core 与 SOCKS 相各一轮）5/5，客户端都实收 65536B 且 SHA/ACK 一致。
- core 单客户端 13/13：redirected=59 (ingress=52 return=7)、multi_nb=7、complete=102/failed=0、
  redirect_return_miss=0；客户端 acked=65538，服务端 peer 仍是 192.168.100.99:40250。
- upstreams：顺序与并发（两个 SOCKS5）都 65536B 双向一致；--expect-accepts 5 的单上游并发
  redirected=306 (ingress=266 return=40)、multi_nb=33、complete=520/failed=0。
- fail：客户端超时，listener 无 accept，redirect_no_config=3、redirect_blocked=3、blocked=3。
- 产品 E2E：relay accept #1 original=192.168.100.99:40250 source=forwarded ... upstream_id=0
  ingress=5 upstream=socks5:192.168.100.4:40500，in/out 各 65536；退出 enabled=0、空 blob、
  accepted=1 refused=0、code=0。
- host 回归：rules 10 轮、selftest、gates、udp-rules、forwarded-rules（F1/F2/F3/F6）全 PASS。
### 边界

- 第一版只支持单入口、单 listener 端口、单 L；多入口/多 L 留 B6。
- UDP redirect 在 B4b 仍然 block（B5 接入）。
- 只处理首片；非首片没有传输层头部，按 no_state block（文档与实现一致）。
- 表满路径没有用 4096 条真实连接压测；容量语义由建表返回值与 redirect_capacity 计数锁住。
- LWF（WinpkFilter）对注入帧的可见性仍未测；显式注入状态查询只解决本驱动自触发。

## 系统要求

池内存用 `ExAllocatePool2(POOL_FLAG_NON_PAGED, ...)` 分配（`cpp/drivers/wdk-deprecated-api` 的修复），
最低支持 **Windows 10 2004（10.0.19041）**；更早的系统上 `wfprobe.sys` 会因缺少该导出而加载失败。
要覆盖旧系统时，改为运行时用 `MmGetSystemRoutineAddress` 取 `ExAllocatePool2`、缺失时回退到
`ExAllocatePoolWithTag`。

`DriverEntry` 用 `extern "C" DRIVER_INITIALIZE DriverEntry;` 声明角色类型。CodeQL 的
`cpp/drivers/illegal-field-access-2` 与 PREfast 的 C28175 都按这条声明判定 DriverEntry 上下文，
`probe_driver_entry` / `create_device` 里的 `DriverObject->MajorFunction`、`DriverUnload` 赋值才不会被
判成越权访问——只有 SAL 注解（`_Function_class_(DRIVER_INITIALIZE)`）不够。

## 构建、签名与安装

在任意一台装有 Visual Studio Build Tools（MSVC + WDK）的 Windows 机器上，以管理员 PowerShell 运行仓库门禁：

```powershell
pwsh -File tools/windows/gate.ps1 -Remote <测试目标地址:端口>
```

`-Root` 默认由脚本位置推到仓库根，也可以用 `-Root <仓库根>` 显式指定。工具发现、构建、签名、
安装、卸载与 PREfast 都在 `tools/windows/WfBuild.psm1`；只做构建/安装/启动时用
`tools/windows/load-driver.ps1`。工程用 `..\..\src\common` 指向共享头，仓库目录结构要保持。
产物是 `src\driver\x64\Debug\wfprobe.sys`；签名使用证书库中的 `WinForward Test Driver` 证书。
`NuGet.config` 只配置 nuget.org；`-restore` 会按需下载 WDK / SDK 包，不依赖任何本机包目录。

## 安装与卸载

`tools/windows/WfBuild.psm1` 的 `Install-Driver` / `Uninstall-Driver` 负责签名、复制到
`System32\drivers`、创建/启动/停止/删除服务。不要手工复制 `.sys` 或直接改服务，避免门禁与
手工操作的状态不一致；完整流程用 `gate.ps1`，临时调试用 `load-driver.ps1`。

- block 规则编译成独立的 auth 层 filter；`protocol=any` 与 `fallback_action=block` 按 6/17 各一条
  （IPv4 / IPv6 各一对）。凡是“更早的非 block 规则可能与它重叠”的配置会在换表时整块拒绝
  （`rule_order_valid`），绝不让 filter 抢掉 blob 里本应首个命中的 permit/redirect。
- 进程选择器是 `ALE_APP_ID` 的 UTF-16 device path → UTF-8（ASCII 大小写折叠，代理对按标准
  展开）。上限是 **UTF-8 511 字节（不含 NUL）**，缓冲区 512 字节含 NUL；超限/非法代理项整条
  不匹配、不截断。中文路径与 511/512 边界都已实测。
- 用户态要用 `FwpmGetAppIdFromFileName0` 取 app id；该 API 对 >MAX_PATH 的路径会失败，长路径
  需要用户态自己拼 device path。
- 闸 2 目前只由组合用例覆盖，未单独钉住（需要第二个 redirector，findings §5）。
