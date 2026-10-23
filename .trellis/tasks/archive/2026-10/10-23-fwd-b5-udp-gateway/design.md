# 技术设计：B5 Forwarded UDP 关联与 SOCKS5 UDP 中继

## 1. 驱动

复用 B4b 的 `probe_forwarded_redirect` 表与帧注入，新增 UDP 分支：

- 表项：协议 17；key `(ingress, client_ip, client_port, server_ip, server_port)`；value
  listener_ip/port、client_mac、upstream_id、rule、state、last_activity、generation。
- 首个 datagram 建表（没有 SYN）；后续 datagram 查表；空闲 60s 回收；容量 4096。
- 入口：命中 redirect 规则 + UDP → 查/建表 → 改写 dst 到 (L, listener_port) →
  重算 IPv4 UDP 校验和（IPv4 可 0；IPv6 必须）→ clone/改写 → `FwpsInjectMacReceiveAsync0`
  → 原帧 BLOCK。
- 回程：outbound 源端口==listener 的 UDP → 查回程表 → 源 IP/port 改回 server、
  目的 MAC=client_mac、源 MAC=入口帧目的 MAC → 重算校验和 → `FwpsInjectMacSendAsync0`
  → 原帧 BLOCK；未命中 permit。
- 失败：无 config/无 L/表满/非首片/注入失败 → 丢包 + 计数（`udp_no_config`、
  `udp_capacity`、`udp_no_state`、`udp_inject_failed`、`udp_fragmented`）。
- 分片：只处理首片（有端口）；非首片无端口，fail closed + 计数；文档写明。
- 防环：显式 `FwpsQueryPacketInjectionState0`。
- ABI：`probe_forwarded_stats` 尾部追加 UDP 计数（v5 结构偏移不变）；或 ABI v6。
  优先追加，避免破坏 B4b。

## 2. 用户态

- UDP listener：`Socket(AddressFamily.InterNetwork/InterNetworkV6, Dgram)` bound
  `0.0.0.0:listener_port`（与 TCP listener 同端口号，协议不同）；可选独立端口。
- `recvmsg`/`ReceiveFrom` 拿客户端源；先查 forwarded redirect 表（protocol=17）：
  - 命中：按 `upstream_id` 从 `ForwardedUpstreams` 选 SOCKS5；复用 `Socks5UdpTransport`
    建 UDP ASSOCIATE（控制连接 + 封装 target=原始服务器）；回程 `sendto` 客户端；
  - 未命中：走现有 host UDP 逻辑（WFP redirect context/cmsg）。
- 关联复用：同一客户端/目的地复用同一个 SOCKS5 UDP 关联；不同目的地/客户端新建。
- 生命周期：用户态空闲 30s（与 host 一致）；驱动表 60s；失败关闭。
- 回程：listener socket `sendto` 客户端；源地址是 L:listener_port；驱动 SNAT 回 server。
- `WfpController`：绑 UDP listener → 下发 forwarded config（含 UDP listener 端口）→ enable；
  退出 disabled/清表。
- `IRedirectContextSource` 扩展 `TryReadUdp(client, out destination, out upstream)`。
- Host UDP 与 forwarded 共用 listener：先 forwarded 后 host；两者都查不到拒绝/丢包。

## 3. 配置

- `forwarded` 段不变；UDP 规则用 `protocol: "udp"` 或 `any`。
- `probe_forwarded_config` 的 `listener_port` 同时用于 TCP/UDP（第一版）；如需要独立 UDP 端口，
  新增字段而不是改语义（B6）。
- `ForwardedUpstreams` 同 B4b；UDP 关联按 `upstream_id` 选。

## 4. E2E

- Linux：UDP echo/DNS 监听 40250；fake SOCKS5 UDP 40500（40000–41000）；临时别名/路由。
- Windows：forwarded UDP 规则 + listener；客户端 `192.168.100.99:40250` 发 datagram。
- 断言：
  - listener 收到；IOCTL 查到 server/upstream/client；
  - N 条 datagram 只 1 个 SOCKS5 UDP 关联（控制连接 1 次 ASSOCIATE）；
  - 回程源地址还原；字节一致；
  - 多客户端/多目的地不串；
  - 超时回收；
  - 失败关闭（无 config/表满/上游失败）→ 客户端无响应，计数；
  - 原帧 block（抓包/计数）；
  - Windows route print 不变。

## 5. 失败/容量/超时

| 条件 | 行为 |
|---|---|
| 无 config / L | 丢包 + `udp_no_config` |
| 表满 | 丢包 + `udp_capacity` |
| 非首片/无端口 | 丢包 + `udp_fragmented` |
| 查表失败 | 丢包 + 计数 |
| 上游失败 | 丢包（UDP 无 RST），不直连 |
| 注入失败 | 丢包 + `udp_inject_failed` |
| 超时 | 表项回收；后续重建 |

## 6. 风险

| 风险 | 处理 |
|---|---|
| 用户态 listener 与 host UDP 端口冲突 | 复用同一 socket；先 forwarded 后 host；文档写清 |
| UDP 无连接，表项泄漏 | 60s/30s 超时 + 容量 + 计数 |
| 多 NET_BUFFER/分片 | 逐 NET_BUFFER；非首片 fail closed |
| 回程 sendto 源地址 | 驱动 SNAT；未命中 permit |
| SOCKS5 UDP 上游不支持 ASSOCIATE | 失败关闭 + 计数；文档 |
| 多入口/多 L | B6 |
| IPv6 | happy path；无地址则记录环境未验证 |
