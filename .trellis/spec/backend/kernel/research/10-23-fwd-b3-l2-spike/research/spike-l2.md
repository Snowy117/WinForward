# B3 阶段 2：L2 可见性与注入 spike 结果

> 环境矩阵见同目录 `environment.md`。本阶段在 WinLTSC 上以**独立最小驱动**（不动 `src/driver`、
> 不改正式 ABI）验证 `FWPS_LAYER_INBOUND/OUTBOUND_MAC_FRAME_NATIVE` 的可见性与 L2 注入。
> 采集时间：2026-10-23。
>
> **结论口径**：A3/A4 只证明“MAC 层注入能力”，**不证明“DNAT 重定向”**。本 spike 的 classify 对原帧
> 始终 `FWP_ACTION_PERMIT`，注入的是复制帧；原帧继续走原路径。B4 必须消费/改写原帧，不能照搬
> 本 spike 的加法注入（见 §4、§9）。

## 1. 结论

| 验收项 | 结果 | 证据 |
|---|---|---|
| A2 L2 可见性 | **通过（有条件）** | 5 次 ping → `icmp_in=5 / icmp_out=5`；3 次 TCP 连接 → `syn_in=3 / syn_out=3`，TCP 摘录全部 `ifIndex=5`；计数口径与摘录状态见 §3 |
| A3 入栈注入 | **通过（仅注入能力）** | `FwpsInjectMacReceiveAsync0` 注入 DNAT 后的 SYN 后，本机监听套接字进入 `SynReceived` |
| A4 回程注入 | **通过（仅注入能力）** | `FwpsInjectMacSendAsync0` 注入改回源端口的 SYN-ACK 后，客户端握手成功；record 32 的轨迹说明见 §4.2 |
| A5 成本 | **未达成，转 B4/B6** | 测量被用户态 sink 限死，开关驱动无差异；0.05% 不可引用，补测方式见 §7 |
| D1 技术可行性 | **通过（条件见 §9）** | 合成 NIC（netvsc）上 MAC 帧层可见且可双向注入；B4 主路线 WFP L2 |

端到端证据：客户端连**没有监听**的 `192.168.100.2:4501`，返回
`Connection to 192.168.100.2 4501 port [tcp/*] succeeded!`，因为驱动把 SYN 复制并改写成 4500 注入
本机栈、再把 SYN-ACK 的源端口改回 4501 注入出去。**原 4501 SYN 仍被 PERMIT 到本机栈**；本机栈在
4501 无监听，通常会回 RST。这是“加法注入”，不是完整 DNAT 重定向（见 §4.1）。

## 2. 方法与约束

- 驱动是**独立的最小 WDM 驱动** `wfl2spike.sys`（单文件 `wfl2spike.cpp`），不是 `wfprobe` 的变体。
  原因：`wfprobe` 已占用 `\Device\WfProbe` 与既有 ALE callout key，且正在运行；复用它必须替换/停掉
  生产实例，风险明显高于收益。
- 临时副本在 `C:\workspace\spike-l2`；仓库 `src/driver` 全程未改（`git status` 无驱动变更）。
- 计数/注入配置通过**私有 IOCTL** 读出：设备 `\Device\WfL2Spike` → `\DosDevices\WfL2Spike`，
  `IOCTL 0x222000` 读快照、`0x222004` 清零、`0x222008` 配置注入。这是 spike 专用通道，
  与正式 ABI 无关。
- 调试输出走 `DbgPrint`，但 **DebugView 在本 VM 上不可用**：`dbgviewcli64` 起得来，却不安装
  `Dbgv.sys`（`sc query Dbgv` → 1060），日志恒为 0 字节。因此判定全部依赖 IOCTL 快照，
  DbgPrint 仅作旁证。
- **注入是复制注入**：`classify_common` 先置 `FWP_ACTION_PERMIT`，随后 `maybe_inject` /
  `maybe_inject_reply` 只把改写后的 NBL 注入，不修改也不拦截原 NBL。A3/A4 的“成功”是注入 API
  被接受 + 对端看到帧，不是“原帧被消费”。

## 3. 2A 可见性

### 3.1 计数口径（先读）

`frames_*` 是经过该层 callout 的**全部帧**，包含测试期间 WinRM 管理流量等背景帧；
`icmp_*` / `syn_*` 只是测试子集。`other_*` 在源码里只在 NBL 解析失败时自增，**不是“其他协议”
计数**。因此：

| 测试 | 全帧 | 测试子集 | 结论 |
|---|---|---|---|
| 5 ping | `frames_in=15 / frames_out=13` | `icmp_in=5 / icmp_out=5` | ICMP 请求/应答 1:1；15/13 不是重复计数 |
| 3 TCP | `frames_in=19 / frames_out=17` | `syn_in=3 / syn_out=3` | 3 个 SYN / 3 个 SYN-ACK；19/17 不是重复计数 |

背景帧主要是本会话自身的 WinRM 管理流量（Linux 192.168.100.4 ↔ VM 192.168.100.2），另有少量
非测试协议帧。报告不把 `frames_*` 当作测试帧总数，也不把 15/13、19/17 解释成重复。

### 3.2 ICMP（Linux → VM）

清零后从 Linux `ping -c 5 192.168.100.2`：

```
frames_in=15 frames_out=13
arp_in=0 arp_out=0 icmp_in=5 icmp_out=5 syn_in=0 syn_out=0 errors=0
```

**5 个请求 / 5 个应答，ICMP 子集严格 1:1**。方向由 `IN/OUT` 计数区分；接口索引见 §3.4。
说明：ping 段的 `frame_record` 未在报告中逐条摘录，原始 ring 快照未保存；当次 `read-stats.ps1`
输出确认了 `ifIndex=5`。本报告不补造 ping 的 MAC/IP 行。

### 3.3 TCP（Linux → VM:445）

清零后从 Linux 发起 3 次 `< /dev/tcp/192.168.100.2/445`：

```
frames_in=19 frames_out=17
icmp_in=0 icmp_out=0 syn_in=3 syn_out=3 errors=0
```

抓到的记录（节选，`tcpc` 是 TCP flags）：

```
11 IN   5  74 0800 6 46026  445 02    <- SYN
12 OUT  5  74 0800 6   445 46026 12   <- SYN-ACK
13 IN   5  66 0800 6 46026  445 10    <- ACK
14 IN   5  66 0800 6 46026  445 11
15 OUT  5  66 0800 6   445 46026 10
```

3 条连接各一个 SYN，`syn_in=3`，**无重复**。记录摘录只保留方向 / ifIndex / 长度 / EtherType /
协议 / 端口 / flags；源/目的 MAC 与 IP 在 `frame_record` 结构中存在，但 `read-stats.ps1` 只打印
IP、不打印 MAC，且原始 ring 快照未保存。因此本文**不补造 MAC/IP 行**；B4 复测时应把
MAC/IP/ifIndex 一并落盘。`implement.md` 的“源/目的 MAC、IP、端口”要求，本轮报告只完成了
ifIndex/IP 级别的可读输出，MAC 未落盘。

### 3.4 字段可得性（重要）

`FWPS_LAYER_*_MAC_FRAME_NATIVE` 的 `inFixedValues` **只有**：
`NDIS_MEDIA_TYPE`、`NDIS_PHYSICAL_MEDIA_TYPE`、`INTERFACE`、`INTERFACE_TYPE`、
`INTERFACE_INDEX`、`NDIS_PORT`、`L2_FLAGS`、`COMPARTMENT_ID`。

**没有 MAC 地址、没有 EtherType、没有 IP/端口**——这些必须自己从 `layer_data`（`NET_BUFFER_LIST*`）
解析。实测 `layerData` 确为 NBL，数据自以太网头开始（offset 0 = 目的 MAC）。

实测取值：入向 `layerId=58`（= `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE`），
`INTERFACE_INDEX=5`，`NDIS_PORT=0`。出向层是 `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`（59）；
报告 §5 的“实测 58”只适用于入向，B4 不要拿 58 去注入出向。

### 3.5 登记方式

- 两层各注册一个 `FWP_ACTION_CALLOUT_INSPECTION` callout（callout key 独立 GUID）。
- 2A 阶段 classify 只做：`classify_out->actionType = FWP_ACTION_PERMIT`、清
  `FWPS_RIGHT_ACTION_WRITE`，然后计数 + 解析 + 记录环形缓冲。**最终 spike 的同一个 classify
  还调用注入**；原帧动作仍是 PERMIT，不改变原帧转发（这正是“加法注入”的来源）。

## 4. 2B 注入（只证明“注入能力”）

### 4.1 入栈（A3）

触发：入向 TCP SYN，目的端口 = `trigger_port`(4501)。驱动**复制**该帧，把目的端口改成
`target_port`(4500)、重算 IPv4 头校验和与 TCP 校验和，用 `FwpsInjectMacReceiveAsync0` 从
`FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE`、`interfaceIndex=5` 注入。原 4501 SYN 仍被 PERMIT 到
本机栈；本机栈在 4501 无监听，通常会回 RST。A3 证明的是“本机栈看到了 DNAT 后的 SYN”，
不是“4501 原连接被消费”。

客户端连 4501（无监听），`Get-NetTCPConnection -LocalPort 4500` 抓到：

```
LocalAddress   LocalPort RemoteAddress RemotePort State        OwningProcess
192.168.100.2       4500 192.168.100.4      52358 SynReceived          3080
```

客户端的目标端口是 **4501**，这条 4500 上的半开连接只可能来自注入帧 → 本机 TCP 栈确实看到了
DNAT 后的 SYN，且监听套接字（PID 3080）已进入 `SynReceived`。

计数：`inject_attempts=1 inject_success=1 last_status=0x00000000`。

**负控制状态**：`C:\workspace\spike-l2\l2listener.ps1` 只绑定 `192.168.100.2:4500`，因此 4501
无监听；本轮**未保存** `Get-NetTCPConnection -LocalPort 4501` 为空或“注入前 nc 4501 失败”的
命令输出。A3 的客户端端口 52358 与 A4 ring 的 50304 **不是同一次运行**。B4/复测应补：
4501 空监听、注入前 `nc` 失败、同一次运行的客户端抓包。

### 4.2 回程（A4）

触发：出向 TCP 帧，源端口 = `target_port`(4500)。驱动**复制**该帧，把源端口改成
`trigger_port`(4501)、重算校验和，用 `FwpsInjectMacSendAsync0` 从
`FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`（59）注入。原 4500 SYN-ACK 仍被 PERMIT 发给客户端；
客户端会同时看到“原 4500 SYN-ACK”和“注入的 4501 副本”。这也是加法注入。

驱动环形缓冲里的记录：

```
32 IN   5  66 0800 6 50304 4501 11   <- 上一次连接的 FIN（不是本次握手）
33 OUT  5  74 0800 6  4500 50304 12  <- 本机栈自己发的 SYN-ACK（源头 4500）
34 OUT  5  74 0800 6  4501 50304 12  <- 驱动注入的副本（源端口改回 4501）
35 IN   5  66 0800 6 50304 4501 10   <- 客户端 ACK，握手完成
```

**轨迹说明**：record 32 的 FIN 在 33 的 SYN-ACK 之前，同一条连接不可能 FIN 先于 SYN-ACK；它
属于上一次连接（同四元组复用）。本次连接的 SYN 在 32 之前，未摘录。33/34/35 才是本次握手的
证据：原 4500 SYN-ACK、注入的 4501 副本、客户端 ACK。`out_att=4 / out_ok=4` 说明出向注入
发生了 4 次，最可能是 SYN-ACK 重传 / 多次尝试；原始 ring 快照未保存，无法补出另外 3 条，
本文**不编造**连续轨迹。完整轨迹应在新一轮复测中从新连接 SYN 开始捕获。

### 4.3 端到端

```
$ nc -v 192.168.100.2 4501
Connection to 192.168.100.2 4501 port [tcp/*] succeeded!
```

4501 上没有任何监听进程；能握手成功说明“入向 DNAT 注入 + 出向回程注入”的**注入闭环**成立。
但它不是完整重定向：原 4501 SYN 仍到本机栈（通常 RST），原 4500 SYN-ACK 仍发给客户端；客户端
的 `connect()` 成功不代表连接能稳定保持。B4 必须消费/改写原帧。

## 5. 失败码与排查路径（留档）

| 现象 | 根因 | 处理 |
|---|---|---|
| `sc start` 后驱动能起来，但 `\Device\WfProbe` 冲突 | 复用生产驱动的设备名与 callout key 不可能共存 | 改成独立最小驱动 + 独立设备名/GUID |
| `DbgPrint` 一条都抓不到 | `dbgviewcli64` 未安装 `Dbgv.sys`；`Debug Print Filter` 也不存在 | 放弃 DebugView，改私有 IOCTL 读快照 |
| 注入返回 `0xc000000d` (`STATUS_INVALID_PARAMETER`)，`alloc=0 / call=0` | 先用 `NdisAllocateNetBufferList` + 手工拼 NB，WFP 不接受 | 改 `FwpsAllocateNetBufferAndNetBufferList0` |
| 仍 `0xc000000d`，分阶段码 `0xE0000003`（IP 头长度检查失败） | **`NdisGetDataBuffer` 在数据连续时返回原指针、忽略调用方传入的 Storage**，代码却继续用 Storage（未初始化内存） | 用返回值；`返回值 != Storage` 时再 `RtlMemcpy` 到自己的缓冲 |
| 另一处 `0xc000000d`（**非根因**） | `NdisPortNumber` 传了硬编码 0；实测本机字段也是 0，改取对应字段是规范性修正 | 改为取 `FWPS_FIELD_..._NDIS_PORT`；不能把这一行当根因 |

结论：

- 入向 `layerId` 用 `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE`（实测 58）；出向用
  `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`（59）。
- `FwpsInjectionHandleCreate0` 的参数顺序是 `(ADDRESS_FAMILY addressFamily, UINT32 flags,
  HANDLE* injectionHandle)`；源码用 `(AF_UNSPEC, FWPS_INJECTION_TYPE_L2, &g_injection)`。
   B4 以这个顺序为准（设计文档旧版曾写反，已修正）。
- `NdisPortNumber` 用 classify 的对应字段；NBL 必须由
  `FwpsAllocateNetBufferAndNetBufferList0` 分配。
- 本 spike **没有**调用 `FwpsQueryPacketInjectionState0`；靠“改写后的端口不再匹配触发条件”
  避免自触发。B4 必须显式查询注入状态（见 §6.1、§9）。

## 6. 与 WinpkFilter LWF 共存

`nt_ndisrd`（WinpkFilter 3.6.2.1）在 Internal（ifIndex 5）上 Enabled、`ndisrd.sys` RUNNING。

实测口径：**本轮 WFP 层未见重复计数**。
- 5 个 ping → `icmp_in=5 / icmp_out=5`（若被 LWF 触发重复指示，会出现 10/10）。
- 3 次 TCP 连接 → `syn_in=3 / syn_out=3`。
- record ring 里也没有成对重复的同一帧。

结论要降级：**本轮只能说明“WFP MAC 帧 callout 没有看到成对重复”**；LWF 自己是否处理了同一帧、
以及**注入帧是否会被 LWF 再次看到，未测**。§9 的 D1 条件 (5) 要求 B4 补测或防御；B4 不能把
“无双计数”当保证。
但要注意两点：(a) 本 spike 只统计“WFP 层看到的帧”，WinpkFilter 自己是否也处理了同一帧不影响
本计数；(b) 注入帧会被 WFP 再次 classify（文档明确要求用 `FwpsQueryPacketInjectionState0`
防环），本 spike 靠“改写后的端口不再匹配触发条件”天然避免自触发，**正式实现必须显式查询注入
状态**。

### 6.1 B4 硬约束（与 §9 条件对应）

1. **消费/改写原帧**：不能 PERMIT 原帧再注入副本；本 spike 是加法注入，原 4501 SYN / 4500
   SYN-ACK 都还在。
2. **流/连接上下文触发**：本 spike 是端口触发（入向 dst=4501、出向 src=4500），不是流级匹配；
   B4 不能照搬。
3. **显式防环**：注入帧会被再次 classify；B4 必须在注入后/再次 classify 前调用
   `FwpsQueryPacketInjectionState0`，自身注入的帧直接 permit。
4. **LWF 注入可见性**：未测；B4 要补测或做防御（例如避免 LWF 对注入帧二次处理）。
5. **VLAN 偏移与出向 layer 59**：`inject_rewrite` 当前按 14 字节以太网头硬编码（源码
   `network = 14`）；VLAN 标签帧会改错偏移。B4 要按实际 L2 头计算，出向用 59。
6. **A5 成本**：本轮未测；B4/B6 按 §7 的方法补，不要引用 0.05%。

## 7. 成本（A5 未达成，转 B4/B6）

对照实验：Linux 用 `nc` 推 200 MB 到 VM 上的 TCP sink，分别在
“spike 驱动 RUNNING”与“spike 驱动 STOPPED”下各跑一次。

| 条件 | 耗时 | 吞吐 |
|---|---|---|
| 驱动 RUNNING（两层 inspection callout + 每帧解析 + 环形缓冲 + 自旋锁） | 119.932 s | 1.7 MB/s |
| 驱动 STOPPED（基线） | 119.985 s | 1.7 MB/s |

差异 0.05%，**仅本实验、不可引用**。瓶颈是 PowerShell 写的用户态 sink（逐块
`NetworkStream.Read`），1.7 MB/s ≈ 1200 pps，远低于任何有意义的包处理速率。

因此诚实的结论是：**在 ~1200 pps 量级下测不出 L2 callout 的开销，不能据此判定“成本可接受”**。
A5 本轮**不通过**，转 B4/B6。

补测方式与归属：

- 方式：内核侧 sink，或 iperf3 的 Windows 服务端；NDISAPI 回环只作对照。
- 归属：**B4** 实现数据面并做首轮量级，**B6** 做正式 benchmark / 门禁。
- 判据：与包版**同 pps / 同包大小**下的进程/系统 CPU 量级对照；58.1% 只作量级参考，不追求
  同条件。
- 本轮缺包大小、pps、CPU 采样方式；0.05% 不是可引用结论。A5 的“每包/吞吐量级”必须在
  B4/B6 补齐后才能回答。

## 8. 未覆盖边界

- **合成 NIC**：被测网卡是 `netvsc.sys`（Hyper-V 合成网卡），不是物理 NIC。
  「物理 NIC + VMQ / SR-IOV」路径未覆盖。
- **External switch**：本轮是 Internal（ifIndex 5）；External switch / 物理入口未覆盖。
- **无嵌套**：VM 内 Hyper-V 不可用（`SVM -` / `NP -`），无法验证「guest 内再跑 vSwitch、
  子 VM 当客户端」。本轮客户端是与 WinLTSC 同宿主的兄弟 VM（Linux 192.168.100.4）。
- **vSwitch 扩展层**：未涉及 `FWPS_LAYER_INGRESS/EGRESS_VSWITCH_*`。
- **IPv6**：解析代码支持，但没有构造 IPv6 用例验证。
- **UDP**：未做功能验证；仅共享解析代码覆盖，不能声称“UDP 计数分类已验证”。
- **VLAN**：解析路径支持 `0x8100/0x88A8`，但注入路径按 14 字节以太网头硬编码；VLAN 标签帧
  未验，B4 需补。
- **LWF 注入可见性**：未测；B4 要补测或防御。
- **原帧消费/改写**：未验；本 spike 是加法注入。
- **A5 成本**：未测，转 B4/B6。
- **正式 ABI**：本 spike 不进正式驱动、不定义任何 IOCTL 供生产使用。

## 9. D1 结论与条件

**技术可行性通过**：netvsc 上 MAC 帧层可见、双向注入可用。B4 的主路线采用 WFP L2；继续 L2 的条件是：

1. A5 成本在 B4/B6 用内核侧 sink 或 iperf3 补测，判据与包版同 pps/包大小的 CPU 量级对照；
2. B4 必须消费/改写原帧，不能 PERMIT 原帧再注入副本（本 spike 是加法注入）；
3. B4 用流/连接上下文触发，不是端口触发；
4. B4 必须显式 `FwpsQueryPacketInjectionState0` 防环；
5. LWF 对注入帧的可见性未测，B4 要补测或防御；
6. VLAN 偏移与出向 layer 59 要按实际 L2 头处理。

D7（forwarded 是否正式从 NDISAPI 迁到 WFP L2）是产品决策，另行拍板；本 spike 只证明技术可行性。

**覆盖边界**：本证据只覆盖**合成 NIC（netvsc）上的 vSwitch 客户端入口**；不覆盖无嵌套
（SVM/NP 不可用）、物理 NIC / External switch / VMQ/SR-IOV、vSwitch 扩展层、IPv6、UDP 注入、
VLAN、原帧消费/改写、LWF 注入可见性；A5 成本未测（转 B4/B6）。A3/A4 只证明“注入能力”，
不证明“DNAT 重定向”。

## 10. 关键代码片段

callout 注册（两层各一次）：

```cpp
FWPS_CALLOUT1 callout{};
callout.calloutKey = callout_key;
callout.classifyFn = classify;
callout.notifyFn = notify;
status = FwpsCalloutRegister1(g_device, &callout, &slot.callout_id);

FWPM_CALLOUT0 definition{};
definition.calloutKey = callout_key;
definition.applicableLayer = layer_key;   // FWPM_LAYER_INBOUND/OUTBOUND_MAC_FRAME_NATIVE
status = FwpmCalloutAdd0(g_engine, &definition, nullptr, nullptr);

FWPM_FILTER0 filter{};
filter.filterKey = filter_key;
filter.layerKey = layer_key;
filter.action.type = FWP_ACTION_CALLOUT_INSPECTION;
filter.action.calloutKey = callout_key;
filter.weight.type = FWP_EMPTY;
status = FwpmFilterAdd0(g_engine, &filter, nullptr, &slot.filter_id);
```

classify 主干（只放行 + 计数；注入路径另见 §4）：

```cpp
classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
classify_out->actionType = FWP_ACTION_PERMIT;

const UINT32 interface_index = field_u32(in_fixed_values, FWPS_FIELD_INBOUND_MAC_FRAME_NATIVE_INTERFACE_INDEX);
const UINT32 ndis_port       = field_u32(in_fixed_values, FWPS_FIELD_INBOUND_MAC_FRAME_NATIVE_NDIS_PORT);

UCHAR window[128];
const ULONG wanted = min(length, 128);
const PVOID data = NdisGetDataBuffer(const_cast<NET_BUFFER*>(buffer), wanted, window, 1, 0);
// data 可能指向原始 MDL，也可能是 window —— 必须用返回值
```

注入帧构造（DNAT + 校验和重算）：

```cpp
PVOID data = ExAllocatePool2(POOL_FLAG_NON_PAGED, capacity, 'pLfW');
PVOID source_data = NdisGetDataBuffer(source, capacity, data, 1, 0);
if (source_data != data) { RtlCopyMemory(data, source_data, capacity); }   // 关键

frame[transport + port_offset] = (new_port >> 8) & 0xFF;   // 入向改目的端口，出向改源端口
frame[transport + port_offset + 1] = new_port & 0xFF;

frame[network + 10] = 0; frame[network + 11] = 0;
USHORT ipc = ipv4_header_checksum(frame + network, header_length);
frame[network + 10] = ipc >> 8; frame[network + 11] = ipc & 0xFF;

frame[transport + 16] = 0; frame[transport + 17] = 0;
USHORT tc = tcp_checksum(frame + network, frame + transport, tcp_length);
frame[transport + 16] = tc >> 8; frame[transport + 17] = tc & 0xFF;

PMDL mdl = NdisAllocateMdl(nullptr, data, capacity);
NET_BUFFER_LIST* nbl = nullptr;
FwpsAllocateNetBufferAndNetBufferList0(g_nbl_pool, 0, 0, mdl, 0, capacity, &nbl);
FwpsInjectMacReceiveAsync0(g_injection, nullptr, 0,
                           FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE,
                           interface_index, ndis_port, nbl, injection_complete, nullptr);
```

注入 API 关键点：

- 入向 layer 58，出向 layer 59；不要用同一个 id 注入两个方向。
- `FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_L2, &handle)`；参数顺序是
  `(addressFamily, flags, handle)`。
- NBL 必须由 `FwpsAllocateNetBufferAndNetBufferList0` 分配；`NdisGetDataBuffer` 必须用返回值；
  `NdisPortNumber` 取 `FWPS_FIELD_..._NDIS_PORT`。
- 注入帧会被再次 classify；正式实现必须显式 `FwpsQueryPacketInjectionState0`。

## 11. 清理与现场

已完成：

- 删除 `wfl2spike` 服务（`sc query` → 1060），删除 `System32\drivers\wfl2spike.sys`。
- 删除临时防火墙规则 `WFSPIKE L2 test 4500`。
- 删除 `Debug Print Filter` 注册表键（创建前本不存在，已整体移除）。
- 删除 `C:\Windows\Temp` 下的 spike 日志；停掉 sink / listener / dbgviewcli 进程。
- **生产驱动 `wfprobe` 全程未动**，清理后仍是 `RUNNING`。
- 仓库 `src/driver` 无任何修改；**阶段 2 仅新增本文件**，B3 任务目录另含 `environment.md`
  与任务记录。

回滚点：`windows-vm reset` 在宿主 JEA 上执行 `Reset-WinLTSCToLatestCheckpoint`，
即恢复到最新 checkpoint「驱动开发二」。本 spike 未触发蓝屏，未使用该回滚。

spike 源码留在 `C:\workspace\spike-l2\src\driver\wfl2spike.cpp`（不进仓库）。

## 12. 复现材料与记录布局摘要

- VM 路径：`C:\workspace\spike-l2\src\driver\wfl2spike.cpp`；脚本
  `read-stats.ps1`、`l2listener.ps1`、`l2sink.ps1`。
- 设备 / IOCTL：`\Device\WfL2Spike`；`0x222000` 读快照、`0x222004` 清零、`0x222008` 配置注入。
- 快照 2600 B；`frame_record` 70 B，32 条 ring；字段偏移：`seq(0) dir(4) ifidx(8) len(12)
  eth(16) sport(18) dport(20) proto(22) tcpc(23) fam(24) arp(25) src_mac(26-31) dst_mac(32-37)
  src_ip(38-53) dst_ip(54-69)`。
- `read-stats.ps1` 打印 IP 与 ifIndex，但**不打印 MAC**；原始 ring 快照未保存。B4 复测应把
  MAC/IP/ifIndex 与完整 ring 一并落盘。
- 不提交二进制/日志；VM reset 后需重新取。
