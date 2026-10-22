# 技术设计：Forwarded SOCKS5 网关

## 1. 结论与决策

| # | 决策 | 理由 |
|---|---|---|
| D1 | 第一版采用 **WFP MAC 层（L2）callout + `FwpsInjectMacSendAsync0/ReceiveAsync0`**；B3 spike 已证明 netvsc 上可见、双向注入可用。继续 L2 的条件见 §13；spike 不达标时才转独立 NDIS LWF | 复用现有 provider/callout/IOCTL/规则框架；L2 层拿得到完整以太网帧，能 DNAT、能回注。LWF 作为退路，数据面行为相同 |
| D2 | 原始目的地 **不走 WFP `redirect_context`**，由驱动维护 redirect 表，用户态用新 IOCTL 查询 | Forwarded 没有本机 socket，不进 `ALE_CONNECT_REDIRECT`，没有 `localRedirectContext` 和 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 可用 |
| D3 | 配置复用 `Forwarded.Rules`：`adapterId/adapterName` 就是入口适配器，`action=proxy` + `target` 就是 SOCKS5 上游；不新增顶层 schema | 与 WinForward 包版语义一致，edition parity 成本最低；用户已有的 forwarded 配置可以直接用 |
| D4 | 驱动侧新增独立的 `wf_forwarded_blob`（v1），不与 host 的 `wf_rule_blob` 混用；两者共享 matcher 的规范化/CIDR/端口代码 | host 规则按进程匹配，forwarded 按入口适配器匹配，字段集不同；独立 blob 避免破坏 host ABI，也避免"一个 blob 两种语义" |
| D5 | TCP 由 Windows 栈终结，驱动只做 DNAT/SNAT，**不碰序列号/ACK/窗口** | 客户端与 listener 之间是真实 TCP 连接；这是包版 local_redirect 的既有形状，风险最低 |
| D6 | 回程注入的以太网目的 MAC **取入口方向记录的客户端源 MAC**，不查 ARP | vEthernet/VM 场景下主机 ARP 表未必有客户端条目；入口包上带的就是最可靠的下一跳信息（包版 UDP 回注也是这个做法） |
| D7 | 热替换策略：**旧流保持旧上游直到结束，新流按新规则**；不中途切换 | 中途切换会破坏已建立的 SOCKS5 会话；保持旧流是可预测且可测试的语义 |
| D8 | 失败一律关闭：无本地地址 L、查表失败、上游失败、容量耗尽 → block/RST/丢包，不直连 | 与 host 路径的 fail-closed 一致；避免"代理失败变直连"的静默泄漏 |
| D9 | pass/block 在驱动内完成，不进用户态；只有 `proxy` 决策才建立 redirect 表项并交给 listener | 这是相对包版最大的成本收益：不代理的流量不付用户态往返 |

## 2. 为什么不是其它路径

- **WFP `ALE_CONNECT_REDIRECT`**：只服务本机 socket。Forwarded 没有本地端点，进不了这一层。
- **WFP `IPFORWARD` 层**：只在启用 Windows IP 转发时触发；该层没有端口/协议条件，也不能改包
  （只能 permit/block）。它能做"阻断型策略"，做不了 SOCKS 重定向。
- **`INBOUND_TRANSPORT` / `ALE_AUTH_RECV_ACCEPT`**：只能 permit/block，没有重定向原语；而且
  它覆盖的是"入站到本机服务"，与 forwarded 的网关语义重叠但不等价。
- **wintun**：L3 TUN，用户态拿 IP 包。SOCKS5 CONNECT 要求字节流，必须自带用户态 TCP 栈；
  TCP+UDP 双协议下得不偿失。
- **hook `ipnat.sys`**：没有公开扩展点；内核 inline hook 被 PatchGuard/HVCI/签名策略挡；它的
  模型是"一对 public/private 接口"，不支持按入口选上游。
- **改系统路由表**：Windows 只有目的地址路由，没有按源/入口的策略路由；改默认路由会污染
  整机，且无法表达"不同入口不同上游"。

## 3. 总体架构

```
┌─────────────────────────────────────────────────────────────┐
│ 用户态（C#）                                                 │
│  配置 → Forwarded 规则编译 → wf_forwarded_blob               │
│  listener（TCP accept / UDP recvmsg）                        │
│  redirect 表查询（IOCTL）→ 原始目的地 + upstream_id          │
│  SOCKS5 CONNECT / UDP ASSOCIATE + 双向中继                   │
└─────────────────────────────────────────────────────────────┘
        │ IOCTL：规则 / 配置 / 查询 / 统计        ▲ redirect 表
        ▼                                        │
┌─────────────────────────────────────────────────────────────┐
│ 内核驱动（WFP L2 callout；必要时 LWF）                        │
│  入口方向：匹配 forwarded 规则 → pass/block/proxy            │
│           proxy：DNAT 到 (L, listener_port)，记 redirect 表  │
│  出口方向：识别 listener 回程 → SNAT 回原始服务器 → 注入入口 │
│  规则表 / redirect 表 / 计数器 / 事件                        │
└─────────────────────────────────────────────────────────────┘
```

## 4. 数据流

### 4.1 TCP

```
客户端（VM/热点） ── SYN dst=server:443 ──▶ 入口网卡
  驱动：入口匹配规则 → upstream_id
        DNAT: dst → (L, listener_port)
        记表：{ingress, client, listener} → {original_server, upstream_id}
        注入本机栈（inbound MAC）
  本机 TCP 栈 → listener accept
  用户态：IOCTL 查表 → original_server + upstream_id
          按 upstream_id 选 SOCKS5 → CONNECT server
          双向泵（复用 RelayServer）
  回程：listener socket 发出 src=(L, listener_port) dst=client
        驱动出口方向：src 端口 ∈ listener 端口集合 → 查表
        SNAT: src → original_server
        目的 MAC = 入口方向记录的 client MAC
        注入入口网卡（outbound MAC）
```

- 驱动不参与序列号；客户端与本机栈之间的 TCP 是完整连接。
- 中间数据包走同一张表：入口方向所有匹配该流的包都 DNAT，出口方向所有 listener 包都 SNAT。
- 半关闭/RST 由本机栈产生，驱动只改写地址。

### 4.2 UDP

```
客户端 ── datagram src=client dst=server:53 ──▶ 入口网卡
  驱动：DNAT → (L, listener_port)，记表（按客户端关联）
        注入本机栈
  listener recvmsg → 用户态 IOCTL 查表（每关联一次，缓存）
  SOCKS5 UDP ASSOCIATE → 封装 datagram → 上游
  回程：上游响应 → listener sendto client
        驱动出口方向：SNAT src=server → 注入入口网卡
```

- 每个客户端关联一个 SOCKS5 UDP ASSOCIATE（与包版 `UdpProxyCoordinator` 语义一致）。
- 关联空闲超时回收；表项与关联同生命周期。
- 回程源地址校验：只接受该关联对应上游的响应，不匹配的丢弃并计数。

## 5. 驱动形态（D1）与 spike 判据

第一版在现有 `wfprobe.sys` 上增加 L2 callout：

- 注册层：`FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE` / `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`
  （或 `..._ETHERNET`，取决于 vSwitch 场景；spike 决定）。
- 入口字段：`..._INTERFACE_INDEX`、源/目的 MAC、以太网类型；IP/TCP/UDP 头由驱动自己解析。
- 注入：`FwpsInjectionHandleCreate0(FWPS_INJECTION_TYPE_L2)`；
  `FwpsInjectMacReceiveAsync0` 用于 DNAT 后送回本机栈，
  `FwpsInjectMacSendAsync0` 用于回程注入入口网卡。
- 防循环：`FwpsQueryPacketInjectionState0` + 注入标记；listener/中继/上游 socket 的流量
  不进 redirect。

**spike 必须回答**（任一不成立就转 LWF）：

1. 在 Hyper-V Internal switch 上，L2 层 callout 能看到 VM 客户端发往网关的帧；
2. DNAT 后 `FwpsInjectMacReceiveAsync0` 能让本机 TCP 栈接受并交给 listener；
3. 回程 `FwpsInjectMacSendAsync0` 能把帧送回 VM，客户端收到；
4. pass/block 路径的每包成本可测量，且明显低于包版的"捕获+用户态回注"。

## 6. redirect 表与接缝 ABI

### 6.1 表结构（驱动内）

| 字段 | 内容 |
|---|---|
| key | `ingress_adapter_id`、`client_ip/port`、`listener_ip/port`、`protocol` |
| value | `original_ip/port`、`upstream_id`、`client_mac`、`state`、`last_activity` |
| 反向 key | `listener_port` + `client` → 同一 value，用于回程 SNAT |

- 表容量有上限（第一版 4096，可配置）；满则 fail closed 并计数。
- 表项生命周期：TCP 由 SYN 建立、FIN/RST 或超时回收；UDP 由首个 datagram 建立、空闲超时
  回收。
- 热替换不清空已有表项（D7）。

### 6.2 查询 IOCTL

新增 `ioctl_query_forwarded_redirect`：

```c
struct wf_forwarded_lookup {
    uint32_t magic;            // 'WFL1'
    uint16_t version;
    uint8_t  protocol;         // 6 / 17
    uint8_t  family;           // 4 / 6
    uint8_t  client[16];
    uint16_t client_port;
    uint16_t listener_port;
    uint32_t ingress_adapter_id; // 驱动内部 id；用户态只回传
    // output
    uint8_t  original[16];
    uint16_t original_port;
    uint32_t upstream_id;
    uint32_t rule_index;
    uint32_t state;
};
```

- 用户态在 accept / 首个 datagram 时调用；TCP 每连接一次，UDP 每关联一次并缓存。
- 查不到返回 `STATUS_NOT_FOUND`，用户态必须拒绝会话（R5）。
- 版本化与布局断言与现有 ABI 一致：C++ `static_assert` + C# 布局测试。

### 6.3 规则 blob

独立 `wf_forwarded_blob` v1：

- header：magic、version、总长、规则数、fallback 动作；
- 每条规则：`ingress_adapter_id`（0 = 任意）、`protocol`、`family`、`remote_prefix`、
  `remote_port_range`、`action`、`upstream_id`；
- 第一版不含 `client_prefix`（后续扩展，字段留 reserved）；
- 与 host 共用 `matcher` 的 CIDR/端口/协议比较代码，但结构体独立。

### 6.4 适配器标识

- 用户态把 `adapterId`（GUID）解析成驱动可匹配的接口标识（LUID/ifindex）后编进 blob。
- 适配器重建/热插拔时重新下发；旧 blob 里失效的 id 规则不命中（fail closed 到 fallback）。
- 诊断输出保留 stable ID，便于和配置对应。

## 7. 用户态接缝

- 新增 `IRedirectContextSource` 抽象：
  - host 路径：`SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT`；
  - forwarded 路径：`ioctl_query_forwarded_redirect`。
- `RelayServer` 只依赖抽象，不直接调 WFP ioctl；这样 host/forwarded 共用一套中继。
- 上游选择：`upstream_id` → `Socks5Servers[target]`；每个会话独立。
- UDP：`UdpProxyCoordinator` 增加 forwarded 来源；listener 用 `recvmsg` 拿客户端源地址，
  查表拿原始目的地和 upstream。
- 自流量：listener/中继/上游进程的 PID 加入 self 集合；驱动在入口方向直接放行，避免
  把中继自己的出站再代理。

## 8. 配置与校验

```json
{
  "forwarded": {
    "fallbackAction": "pass",
    "rules": [
      { "adapterName": ["vEthernet (MyVM)"], "action": "proxy", "target": "main" },
      { "adapterName": ["vEthernet (Hotspot)"], "action": "proxy", "target": "backup" }
    ]
  }
}
```

- 复用现有 `Forwarded.Rules` 字段：`adapterId/adapterName`、`protocol`、`addressFamily`、
  `remoteCidr`、`remotePort`、`action`、`target`。
- 校验：
  - `proxy` 规则必须指定已存在的 `target`；
  - forwarded 规则仍禁止 `process`；
  - 未指定 adapter 的 forwarded 规则仍然合法（匹配所有入口），但第一版网关场景建议显式；
  - `fallbackAction` 只允许 pass/block。
- 编译产物：`wf_forwarded_blob` + 用户态 `upstream_id → Socks5Server` 映射；两侧同一次
  IOCTL 下发，带 generation。

## 9. 回程、MAC 与路由

- **回程 SNAT**：出口方向按 `listener_port` + `client` 查表；命中才改写，未命中按普通本机
  流量处理（不能误改中继自己的出站）。
- **目的 MAC**：入口方向记录客户端源 MAC；回程用它作为以太网目的地址。客户端不在同一
  L2 网段时，用入口适配器的网关 MAC（第一版只支持同网段客户端，见 §13）。
- **源 MAC**：入口适配器的 MAC。
- **路由表**：DNAT 到 L（入口适配器本地地址）使本机栈认为目标是本机，不需要路由；回程
  由驱动直接注入入口网卡，不经过系统路由。用户态到 SOCKS5 上游的出站走系统默认路由；
  如需指定出口，用 `FwpmConnectionPolicyAdd0` 或 socket bind，不改全局路由表。

## 10. 失败语义与容量

| 条件 | 行为 |
|---|---|
| 入口适配器没有可用 L | 该流 block/RST，计数 `forwarded_no_local_address` |
| 规则命中 proxy 但 upstream 未配置 | 编译期拒绝；运行期 fail closed |
| redirect 表满 | 新流 block，计数 `forwarded_capacity` |
| 用户态查表失败 / 超时 | 拒绝 accept / 丢 datagram，计数 |
| SOCKS5 上游连接失败/握手失败 | RST（TCP）/丢包（UDP），不回退直连 |
| listener 未就绪 | 驱动仍 DNAT，连接超时；启动顺序必须先起 listener 再 enable |
| 热替换 | 旧流保持旧 upstream；新流新规则；generation 可回读 |

## 11. 观测

- 事件：`forwarded.flow.created`、`forwarded.redirect.applied`、`forwarded.relay.started`、
  `forwarded.relay.failed`、`forwarded.udp.association.*`；字段含 ingress、upstream、
  client、server、rule、generation。
- 计数：按 protocol × ingress × upstream 的 accepted/redirected/passed/blocked/failed；
  容量拒绝、查表失败、上游失败分别计数。
- 命名与 host 路径对齐；不新增与现有计数器重复的语义。

## 12. 性能与验证

- **目标**：pass/block 不进用户态；proxy 流每包成本记录基线，不设硬性 CPU kill criteria，
  但必须与包版同形状对比，证明"不代理的流量不付费"。
- **测量**：1/4 流 TCP THRU、UDP pps、480 Mbps 形状；采样进程 CPU、ETW 热点、每包 µs。
- **Linux 门禁**：forwarded blob 布局/编译/差分测试（复用现有夹具生成器）。
- **VM 门禁**：Hyper-V Internal switch + 一个 VM 客户端；`wfprobe forwarded-socks` 套件；
  `gate.ps1` 新增步骤；关键 mutation（入口匹配、DNAT、回程 SNAT、upstream 选择）变红。
- **路由断言**：测试前后 `route print` 对比；允许的局部路由逐条列在测试输出里。

## 13. 风险与未决

| 风险 | 处理 |
|---|---|
| L2 层看不到 VM 流量（External switch 纯桥接） | 第一版明确不覆盖；spike 记录可见性矩阵，后续 vSwitch 扩展任务 |
| 回程 MAC 错误导致 VM 收不到 | 入口记录 client MAC（D6）；spike 先验证单 VM |
| 驱动蓝屏/卸载竞态 | L2 callout 注册/注销按现有 `FwpsCalloutUnregisterById0` 重试规则；spike 阶段做卸载压力 |
| SOCKS5 上游不支持 UDP ASSOCIATE | 运行期失败关闭并计数；文档写明上游要求 |
| 热替换串流 | 表项带 generation；旧流不查新表 |
| IPv6 scope/link-local | 第一版 happy path；link-local 目的按包版策略 pass |
| 加法注入不等于 DNAT | B3 spike 只证明“复制帧注入”；B4 必须消费/改写原帧，不能 PERMIT 原帧再注入副本 |
| 性能不达标 | 记录基线；驱动内批量注入、UDP 批量作为后续优化 |

**未决**：

- D1 的 spike 结果：**已结案（技术可行性通过）**。netvsc 上 MAC 帧层可见、双向注入可用；继续 L2 的条件是 A5 成本补测、原帧消费/改写、流级触发、显式 `FwpsQueryPacketInjectionState0`、LWF 注入可见性补测、VLAN/出向 layer 59。A5 转 B4/B6；D7 是否正式从 NDISAPI 迁移另行产品决策。
- External switch 是否在后续任务用 vSwitch 扩展覆盖。
- `clientCidr` 是否需要在第一版之后加入。
- 表容量默认值与上游连接数上限。
