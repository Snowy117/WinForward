# Research: Kernel host UDP 的驱动与 WFP 路径

- **Query**: 驱动侧如何交付一条 host UDP 关联、listener 收到什么、回包怎么发、生命周期与已知缺陷、listener_port 能否与 TCP 共用、block sink 对 UDP 的行为
- **Scope**: 内部（驱动源码 + 宿主测试工具 + live 规范），不含外部资料
- **Date**: 2026-10-26

---

## 0. 结论摘要

1. UDP 没有独立 WFP 层：它与 TCP 共用 `ALE_CONNECT_REDIRECT_V4/V6` 上一次 classify，**按关联**（本地端点 + 远端目的地）而不是按 datagram。同一 socket 对同一目的地 N 条 datagram 只 classify 一次、只分配一个 36 字节 context。
2. 关联被改道到 `(客户端本地地址, listener_port)`；原始目的地由 `IP_WFP_REDIRECT_CONTEXT`（IPv4，level `IPPROTO_IP`）/ `IPV6_WFP_REDIRECT_CONTEXT`（IPv6，level `IPPROTO_IPV6`）的 cmsg 逐 datagram 读回，类型号都是 **70**，数据就是那块 36 字节结构。
3. 回程由用户态用**收到 datagram 的那个 listener socket** `sendto` 给 datagram 的源地址；驱动不注入 NBL、不改源地址、不维护 UDP flow 表。同 socket 回包复用入站关联，**不再进入 classify**（`udp_classify_calls` 不增）。
4. `listener_port` 与 `block_sink_port` 各只有一个字段，TCP 与 UDP 共用；`ListenerSet` 已经在同一端口上同时绑 TCP 与 UDP。
5. block 不是 classify 层拒绝：命中后把关联改道到 `block_sink_port`，由固定 4 条 `ALE_AUTH_CONNECT` filter（V4/V6 × TCP/UDP，协议 + 远端端口 == sink + `IS_CONNECTION_REDIRECTED`）拒绝。UDP 的应用侧表现是**静默丢包**（send 成功、recv 超时）。
6. 已知 WFP 缺陷（Windows 11 26100）：connected UDP 每条 datagram 给 redirect record 加一次引用且不归还，36B `WfPr` + `Arcl` record 直到重启才释放。unconnected 正常释放。
7. 待实测的一项：客户端 `recvfrom` 看到的回包源地址是否等于它原本的目的地。驱动不参与回程，按 WFP 本地重定向语义应当成立，但现有套件只**打印** `from=` 而没有断言（见 §3.3）。

---

## 1. 交付方式：一次 classify = 一条关联

### 1.1 callout 与层

host TCP 与 host UDP 用同一个 `probe_classify_fn`（`kernel/src/driver/probe_classify.cpp:289`），挂在 `ALE_CONNECT_REDIRECT_V4/V6`。协议分流只发生在 classify 内部：

- `probe_classify.cpp:343` 只接受 `IPPROTO_TCP` 与 `IPPROTO_UDP`，其余保持直连语义（`permitted_non_tcp`，`probe_classify.cpp:345-351`）。
- UDP 专属计数点 `udp_classify_calls` 在 `probe_classify.cpp:316-321`，permit/block/redirect 三个终点各自按协议自增（`probe_classify.cpp:20-47`），构成不变式 `udp_classify_calls == udp_permitted + udp_blocked + udp_redirected`（`kernel/src/driver/probe_ioctl.h:324-327`）。

按关联而不是按 datagram 的实测口径：

- `kernel/tools/wfprobe/UdpSuite.cs:176-226`（U2）connected 模式 N 条 datagram 只 `classify +1`。
- `UdpSuite.cs:229-274`（U3）unconnected 同一 socket 同一目的地 N 条也只 `classify +1`，且每条 context 完全相同。
- `UdpSuite.cs:277-320`（U4）同一 socket 换目的地 → 新关联，`classify +2`。

实现含义：**UDP 的容量与生命周期按关联计**，与会话模型一一对应；不要写「≤ N」这类弱断言。

### 1.2 改道目标怎么算

`probe_build_redirect_target`（`kernel/src/driver/redirect_target.cpp:27-65`）用 classify facts 的 `local`（客户端 socket 的本地地址）与 `listener_port` 构造目标：

- `local` 是通配（`0.0.0.0` / `::`）→ 目标落到 `127.0.0.1` / `::1`（`redirect_target.cpp:38-42`、`54-58`，`redirect_branch::any_to_loopback`）。
- 否则目标地址 = 客户端本地地址，端口 = `listener_port`（`redirect_target.cpp:43-49`、`59-64`，`redirect_branch::local_address`）。
- 地址族取 **remote 的族**（`redirect_target.cpp:34`），不是本地族的复制。

`probe_facts.cpp:94-114` 把 `local` 取自 `FWPS_CONNECT_REQUEST0::localAddressAndPort`、`remote` 取自 `remoteAddressAndPort`。因此 listener 端看到的本地地址是**客户端自己的本地 IP**，不是固定的回环地址——listener 必须绑通配地址（或覆盖全部本机地址），见 §5.1。

### 1.3 context 里有什么

`kernel/src/common/redirect_context.h:36-57`：36 字节，`magic`（`WFC1`）、`rule_index`、`process_id`、`version=1`、`original_port`、`family`（4/6）、`protocol`（6/17）、`original_address[16]`（IPv4 只用前 4 字节，尾部为零）。构造点在 `probe_classify.cpp:207-212`：`protocol` 取 `IPPROTO_UDP` 时写 17，地址取 `matcher_remote_address`（v4-mapped 归一后的地址），端口取 `remote_port`。

`rule_index` 是**命中规则在 blob 里的下标**，未命中（兜底动作）写 `0xFFFFFFFF`（`probe_classify.cpp:9`、`432`；U9 在 `UdpSuite.cs:606` 断言）。这是 Kernel 后端「规则下标 → target」映射的唯一依据。

### 1.4 family 归一与 cmsg 的 level 不是同一件事

`probe_facts.cpp:68-74` 在交给 matcher 与 context 之前把 `::ffff:0:0/96` 归一成纯 IPv4，但 `facts.remote` / `facts.local`（用来算改道目标、写事件）保持 WFP 原族。所以：

| 客户端 | classify 层 | context family | cmsg level |
|---|---|---|---|
| IPv4 `sendto`（unconnected） | V4 | 4，纯 v4 地址 | 0（`IPPROTO_IP`） |
| v4-mapped + `sendto` | V4 | 4，纯 v4 地址 | 0 |
| v4-mapped + `connect` | V6 | 4，纯 v4 地址 | 41（`IPPROTO_IPV6`） |
| native IPv6 | V6 | 6 | 41 |

**family 与 cmsg level 是两件独立的事。** 5f 之后驱动在写 context 前已把 `::ffff:0:0/96` 归一成
IPv4，所以 v4-mapped connected 的 context family 是 4、地址是纯 v4，但 cmsg 仍由 V6 层给出
（level 41）。解析器必须同时接受 level 0/41；不要再用「level 41 ⇒ family 6」推断。任何情况下
都以 context 的 family 为准；若 context family 与客户端源端点的族不一致，先把 v4-mapped 源端点
映射成 IPv4 再建 `FlowKey`，仍不一致则丢弃。

证据：`UdpSuite.cs:654-745`（U10 四个子用例），说明文字见 `.trellis/spec/backend/kernel/wfp-architecture.md:235-263`（§7.11）与 `kernel/src/driver/README.md` 的「Host UDP / 重定向」。**解析器必须同时接受 level 0 与 41、type 70，并支持 `sockaddr_in6`。**

---

## 2. listener 收到的内容

### 2.1 打开 context 传递

socket 必须先设置 `IP_WFP_REDIRECT_CONTEXT`（IPv4，`IPPROTO_IP`）或 `IPV6_WFP_REDIRECT_CONTEXT`（`IPPROTO_IPV6`），值为 BOOL TRUE，**在 bind 之前**设置：

- `kernel/tools/wfprobe/UdpSockets.cs:11-12`：`IpWfpRedirectContext = 70`、`IpProtoIp = 0`、`IpProtoIpv6 = 41`。
- `UdpSockets.cs:23-57`：`CreateListener` 先 `TrySetRedirectContext` 再 `Bind`；IPv6 侧同时试 level `IPv6` 与 `IP`；失败只吞 `SocketException`（不是所有平台都认这个选项）。

### 2.2 只有 `WSARecvMsg` 能逐 datagram 拿到它

`Socket.ReceiveFromAsync` / `ReceiveMessageFromAsync` 不暴露这个 cmsg，必须 `WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER, WSAID_WSARECVMSG)` 取函数指针再用 `WSARecvMsg`：

- `UdpSockets.cs:70-116`：`Poll` 等可读 → 取指针 → 一次 `WSARecvMsg`，同时拿到 payload、源地址（`name`，`sockaddr_in`/`sockaddr_in6`）与 control 缓冲。
- `UdpSockets.cs:139-163`：按 `WSACMSGHDR` 链遍历，`cmsg_type == 70` 且 `cmsg_level ∈ {0, 41}` 时取出 `cmsg_len - 16` 字节（即 36 字节 context）。链接按 `IntPtr.Size` 对齐。
- control 缓冲实测取 2048 字节（`UdpSuite.cs:18`：`MaxControl = 2048`），payload 缓冲取 65536（`UdpSuite.cs:17`）。
- 断言口径：`UdpSuite.cs:998-1008`（`ParseContext`）同时检查长度 36、level 是 0 或 41、magic、version。

### 2.3 一条 datagram 交出的完整事实

listener 侧一次接收就可以拿到：

| 字段 | 来源 | 用途 |
|---|---|---|
| 客户端源地址 + 端口 | `WSARecvMsg` 的 `name` | flow 的 client 半边；回包目标 |
| payload | `WSARecvMsg` 的 buffer | 交给中继 |
| 原始目的地 port | `redirect_context.original_port` | flow 的 remote 半边 |
| 原始目的地地址 | `redirect_context.family` + `original_address` | 同上 |
| 命中规则下标 | `redirect_context.rule_index` | 选 target |
| 客户端 PID | `redirect_context.process_id` | 日志/事件；不需要反查 |

local（被改道后的本地地址）本身不在 context 里，但 `recvfrom` 的 `WSAMSG.name` 只给对端；需要本地地址时由「哪个 listener socket 收到」决定（每个 socket 的 `LocalEndPoint`）。host 路径绑通配时这一点不重要（回包由同一个 socket 发出即可）。

---

## 3. 回包怎么发

### 3.1 同一个 listener socket `sendto`

`.trellis/spec/backend/kernel/wfp-architecture.md:237-239`：回包由用户态使用**同一个 listener socket** `sendto` 回客户端，驱动不改源、不注入、不维护表。实现证据就在套件里：`RedirectListener.Reply` 是 `socket.SendTo(datagram.Payload, datagram.Source)`（`UdpSuite.cs:1226`），用的是接包的那个 socket（`UdpSuite.cs:1197-1229`）。

用户态约束（`kernel/src/driver/README.md` 的「Host UDP / 重定向」与 `UdpSuite.cs` 一致）：

- **不要**为回包新建 socket：新 socket 是一条新关联，会再走一次 classify（此时会被 self-PID 闸放行，但对端看到的源地址也不再是原始目的地）。
- 中继自身向上游发起的出站（SOCKS5 relay socket、local target socket）**是**新关联，需要闸 4 放行，因此 `self_process_ids` 必须包含中继进程。

### 3.2 同 socket 回包不进入 classify

`UdpSuite.cs:151-159`（U1）：回包后 `udp_classify_calls` 与 `udp_permitted` 都不变，且没有 `permitted_self_process` 事件。这就是「复用入站关联」的直接证据。

UDP 侧与 TCP 的一个重要差别：TCP 的中继必须把 redirect records 挂到出站 socket 上（`SocketIoctl.SetRedirectRecords`，`src/WinForward.Kernel.Runtime/Relay/SocketIoctl.cs:50-72`）才能让闸 2 认出「自己的出站」；UDP 的回包里没有 records 这一步，靠的是同 socket。中继向上游的新关联则完全靠 self-PID 闸。

### 3.3 客户端看到的回包源地址（已实测：不保留）

`UdpCommands.cs:192-204` 的客户端在收到时打印 `recv=<n>B from=<source> payload=...`，但所有套件断言只匹配 `recv=2B` / `recv=4B` 这样的子串（例如 `UdpSuite.cs:149`、`UdpSuite.cs:219`），**没有断言 `from=`**。

2026-10-26 的 Kernel host UDP E2E 用 unconnected socket 实测：发往 `192.168.100.4:40010` 的
代理回包，客户端看到 `FROM=192.168.100.2:<listener_port>`，而不是原始目的地；pass 直连臂则
正确显示 `192.168.100.4:40011`。同 socket `sendto` 不保留回包源地址，WFP 的 host UDP local
redirect 也没有为用户态提供 return-source mapping。用户态 raw socket 伪造源地址在 Windows 上
不可行：本地源可投递，远端伪造源在防火墙开/关两种情况下都被丢弃。

因此 host UDP 的回包必须由驱动侧注入或改写源地址（transport receive injection / 受控的
source rewrite），不能沿用 listener `sendto`。证据见
`.trellis/tasks/10-26-host-udp-baseline/verification/a4-failure.md`。这是 A4 的阻塞性发现，
也是 `host-udp-reply-source` 专项批次的入口。

---

## 4. 生命周期与已知缺陷

### 4.1 connected vs unconnected

| 形态 | classify 次数 | context 释放 | 备注 |
|---|---|---|---|
| unconnected（`sendto`） | 首次发送时 1 次 | 客户端 socket 关闭后 1 秒内 | 每次换目的地都是新关联（U4） |
| connected（`connect` + `send`） | 连接时 1 次 | **不由 WFP 及时释放** | 见 §4.2 的缺陷 |

证据：`.trellis/spec/backend/kernel/research/10-22-host-udp-verification/research/connected-context.md:18-45`（1s/10s/60s/180s/300s 采样：unconnected `allocs+1 frees+1 outstanding 0→0`；connected `allocs+1 frees+0 outstanding 0→1` 且 300s 不变），`sc stop` 也不回收。

### 4.2 Windows 11 26100 的 connected UDP 引用计数泄漏

`.trellis/spec/backend/kernel/wfp-connected-udp-record-refcount-leak.md`：

- 36B context 只在 `tcpip!AleRedirectRecordFree` 释放，record `+0x1F0` 是 32 位 refcount（`:17-24`）。
- connected UDP 每发一条 datagram，`tcpip!ProcessALEForTransportPacket` 调一次 `AleRedirectRecordReference`，重定向完成路径没有配对 deref（`:135-175`）。
- 实测 1/3/100 条 datagram → refcount 1/3/100（`:92-98`）。
- 影响：每条 connected 关联至少 36B `WfPr` + 0x228 `Arcl`，内存不随 datagram 增长，但 refcount 线性增长，2^32 后理论上有 UAF 风险（`:31-40`）；只有重启清零（`:36`）。
- 驱动侧**没有**安全释放入口：手动 `ExFreePool` 会 double free/UAF（`:207-215`）。

产品侧含义与处置选项见 `host-udp-seams.md` 的「风险与未决议题」。

### 4.3 规则热替换

已有 UDP 关联保持旧决策：U13（`UdpSuite.cs:904-967`）先 permit 后换 block blob，旧关联仍收到回包且不产生新 classify 事件；只有新关联按新 blob 分类。这与 TCP 相同，是实现时不需要特殊处理但必须写进 E2E 预期差异表的行为。

### 4.4 无规则 / 关闭

`enabled=0` 时仍 classify 并记 `udp_permitted`，但不 redirect、不分配 context（U11，`UdpSuite.cs:747-815`）。启动顺序上这意味着「先关配置再动 listener」不会造成窗口期的误重定向。

---

## 5. listener_port 共享与 block sink

### 5.1 listener_port 是 TCP/UDP 共用的单字段

`probe_ioctl.h:269-280` 的 `probe_config` 只有 `listener_port`（偏移 12）与 `block_sink_port`（偏移 52），没有按协议分开的字段。闸 1 的判据也就同时覆盖两种协议（`probe_classify.cpp:356-364`，`(listener_port != 0 && remote_port == listener_port) || (block_sink_port != 0 && remote_port == block_sink_port)`）。

因此后端**必须**让 TCP 与 UDP 监听落在同一个端口上。现成的实现已经这样：`src/WinForward.Kernel.Runtime/Relay/ListenerSet.cs:28-47` 说明「一个 (L, 端口) 上的 TCP + UDP 监听 endpoint」，`BindWildcard`（`ListenerSet.cs:121-182`）在 `0.0.0.0` 与 `::` 上把 TCP 与 UDP 绑到同一个端口，任一侧失败就整体换端口。

**需要实测的边界**：`ListenerSet` 的 UDP socket 都是 `DualMode = false`（`ListenerSet.cs:81-84`、`142`）。而 §1.4 说明 v4-mapped **connected** 关联是在 V6 层被改道到 `::ffff:<local>`。`IPV6_V6ONLY=1` 的 socket 按 Winsock 语义不接受 v4-mapped 流量。套件用的 listener 恰好是**单个 dual-mode v6 socket**（`UdpSuite.cs:1203`：`UdpSockets.CreateListener(new IPEndPoint(IPAddress.IPv6Any, 0), dualMode: true)`），U1（native v4）与 U10（v4-mapped connected）都能收到。所以 host UDP 监听口大概率需要一个 dual-mode v6 通配 socket，而不是 `ListenerSet` 现在这对 v4+v6（`V6ONLY`）socket。这一条必须用真实 dual-mode 客户端（例如 .NET `UdpClient(AddressFamily.InterNetworkV6)` + `DualMode=true`，或 `wfprobe udp-client --remote [::ffff:...]`）在 VM 上确认。

### 5.2 block sink

- classify 命中 block → `sink_classification` 把关联改道到 `block_sink_port`，context 传 `nullptr`（`probe_classify.cpp:234-264`，尤其 `250`），`blocked` / `deferred_blocks` 只在改道成功时增长。
- 真正丢弃由 4 条固定 `ALE_AUTH_CONNECT` filter 完成（`kernel/src/driver/block_filters.cpp:95-125`）：V4/V6 × TCP(6)/UDP(17)，条件 = 协议 + 远端端口 == sink + `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`（`block_filters.cpp:11-62`）。
- 应用侧语义：UDP `connect`/`send`/`sendto` 返回成功、对端收不到、`recv` 超时；TCP 是 `AccessDenied(10013)`（U6 `UdpSuite.cs:354-410`、U7 `UdpSuite.cs:412-520`；`wfp-architecture.md:243-249`、决策表 `:33`）。
- 用户态 sink 只是兜底：`src/WinForward.Kernel.Runtime/Relay/BlockSink.cs:6-17` 用 UDP 收到即丢 + TCP bind-not-listen；端口不能等于 listener 端口。
- UDP 的 block 是「关联级」：被 block 的关联在 auth 层每包静默丢弃，`udp_blocked` 只统计 classify 层的改道次数，热替换后旧关联在 auth filter 层丢的包不计入（`probe_ioctl.h:326`）。

---

## 6. 可以直接转成实现要求的约束清单

1. listener 必须在 bind 前开 `IP_WFP_REDIRECT_CONTEXT`（IPv4=level 0，IPv6=level 41）。
2. 只能用 `WSARecvMsg` 读 cmsg；解析要同时接受 level 0/41、type 70、`sockaddr_in6`，并校验 36 字节 + magic + version。
3. host UDP 监听口要能收 native v4、v4-mapped（V6 层）与 native v6 三种形态（§5.1 的 dual-mode 待实测）。
4. 回包必须用**收到 datagram 的那个 socket** `sendto` 到 datagram 源地址；不得新建 socket，不得依赖驱动注入。
5. listener 端口不能与 block sink 端口相同（闸 1 会把两者都放过，混用会让 sink 自身流量绕过规则）。
6. 中继进程必须进 `self_process_ids`（最多 8 个，`probe_ioctl.h:27`）；否则自己的上游出站会被 classify 并可能被规则命中。
7. UDP 的计量与容量按**关联**算；`udp_classify_calls` 增量应等于「本次新增关联数」，不是 datagram 数。
8. context 的 `rule_index` 是 blob 下标；`0xFFFFFFFF` 是兜底动作，必须与「未命中任何 proxy 规则」区分开。
9. connected 关联的 context 不会及时释放；任何池/句柄断言只能对 unconnected 严格，connected 只能记录采样。
10. 规则热替换不影响已有 UDP 关联。

---

## 7. Caveats / 未找到

- **客户端回包源地址**（§3.3）：驱动侧无法从源码证明，只有 WFP 语义推断 + 现有套件的 `from=` 打印。属于必须实测项。
- **dual-mode 与 `V6ONLY`**（§5.1）：`ListenerSet` 的 UDP socket 形态与 v4-mapped connected 关联的投递是否兼容，仓库内没有此组合的实测记录。`wfprobe` 用的是单 dual-mode socket，两者不等价。
- **`ListenerSet` 的具体绑定形态**（`Bind(addresses)` 与 forwarded 的多 L 需求）不在本任务范围；只记录 host 路径需要通配这一事实。
- UDP 大 datagram（> 1472 字节 payload）在 host 路径上没有驱动侧限制，但产品侧 `UdpProxy` 的接收窗口按「帧上限」推导（见 `host-udp-userland.md` §5），属于产品决策而不是驱动约束。
- 未在驱动侧找到任何「UDP 回程溯源」或「UDP 端口映射」结构：`probe_forwarded_stats` 里的 UDP 计数属于 forwarded 数据面（`probe_ioctl.h:450-469`），与 host 无关，不要误引。
