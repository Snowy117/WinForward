# B5：Forwarded UDP 关联与 SOCKS5 UDP 中继

## Goal

在 B4b 的 redirect 表/帧注入/回程骨架上实现 Forwarded UDP：首个 datagram 建关联、DNAT 到本机
UDP listener、回程 SNAT 注入入口网卡、原帧消费；用户态 listener 查表、按 upstream_id 建立
SOCKS5 UDP ASSOCIATE、封装/解封装；E2E 覆盖关联复用、超时、多客户端、失败关闭。

## Background

- B4b 已打通 Forwarded TCP：redirect 表、入口 DNAT/回程 SNAT、逐 NET_BUFFER 注入、原帧消费、
  用户态 `IRedirectContextSource`/SOCKS5 中继。
- Host UDP 已有 `UdpProxyCoordinator`/`Socks5UdpTransport`/`UdpResponseReinjector` 等多处实现；
  forwarded UDP 需要复用其 SOCKS5 UDP 封装/解封装与关联生命周期，但原始目的地/upstream 来自
  驱动 redirect 表，而不是 WFP redirect context。
- UDP 无连接：首个 datagram 建表，后续 datagram 查表；没有 SYN/FIN，靠空闲超时回收。
- SOCKS5 UDP ASSOCIATE 是控制连接 + UDP 封装；每个客户端关联一个，回程解封装后 sendto 客户端。

## In Scope

1. 驱动：
   - UDP redirect 规则命中：首个 datagram 建关联表项，DNAT 到 (L, listener_port)，注入本机栈，
     **原帧 BLOCK**；后续 datagram 查表 + 同样改写/注入/block。
   - 回程：outbound 源端口==listener 的 UDP 包按表 SNAT 回原始服务器，注入入口网卡，原帧 BLOCK。
   - 关联表：入口/回程 key、client MAC、upstream_id、ingress、超时（60s）、容量、generation；
     查询 IOCTL 复用 `probe_forwarded_redirect`（protocol=17）。
   - 非首片/无端口/无 config/表满/注入失败 → fail closed（丢包）+ 计数。
2. 用户态：
   - UDP listener（与 host 共用或独立；第一版复用 host 的 listener 端口），`recvmsg` 拿客户端源；
   - 先查 forwarded redirect 表（protocol=17），查不到走 host UDP 逻辑；
   - 按 upstream_id 从 `ForwardedUpstreams` 选 SOCKS5，复用 `Socks5UdpTransport` 封装/解封装；
   - 回程用同一个 listener socket `sendto` 客户端；驱动负责 SNAT 回原始服务器；
   - 关联空闲超时、容量、失败关闭（上游失败/查表失败丢包，不直连）。
3. E2E：
   - Linux UDP echo/DNS 经 Windows 网关（on-link 别名或临时路由）到 fake SOCKS5 UDP；
   - 关联复用：同客户端/目的地 N 条 datagram 只 1 个 SOCKS5 UDP 关联；
   - 多客户端/多目的地不串；超时回收；失败关闭。
4. 文档/ABI/门禁。

## Out of Scope

- TCP（B4b 完成）。
- IPv6 UDP 完整矩阵（只做 happy path）。
- ICMP 错误/分片重组；非首片按 fail closed/文档。
- 多入口/多 L（B6）。
- 性能 benchmark（B6）。
- External switch/物理 NIC/嵌套 VM。

## Requirements

- **R1 关联建立**：首个 datagram 建表；后续命中；空闲超时回收；容量上限 fail closed。
- **R2 改写/消费**：入口 DNAT、回程 SNAT 逐 datagram；原帧必须 BLOCK，只注入副本。
- **R3 SOCKS5 UDP**：每客户端关联一个 UDP ASSOCIATE；封装 target 地址/端口；回程解封装。
- **R4 关联复用**：同客户端/目的地 N 条只建 1 个 SOCKS5 关联；不同目的地/socket 才新建。
- **R5 回程**：源地址还原为原始服务器；客户端看到原目标；字节一致。
- **R6 失败关闭**：无 config/无 L/表满/查表失败/上游失败 → 丢包 + 计数；不直连、不 RST。
- **R7 生命周期**：UDP 关联超时（60s 驱动 / 30s 用户态）、generation、热替换旧流。
- **R8 回归**：host UDP（`wfprobe udp-rules`/E2E）、B4b TCP、gate 不受影响。

## Acceptance Criteria

- [ ] **U1 DNAT**：客户端 UDP datagram 到 `10.99.0.1:<port>`，listener 收到；IOCTL 查到
      server/upstream/client。
- [ ] **U2 关联复用**：同客户端/目的地 N 条 datagram 只建 1 个 SOCKS5 UDP 关联；计数正确。
- [ ] **U3 回程**：响应源地址/端口还原为原始服务器；客户端收到，字节一致。
- [ ] **U4 多客户端/多目的地**：两条关联不串。
- [ ] **U5 超时**：空闲超过阈值后关联回收；后续 datagram 重新建表。
- [ ] **U6 失败关闭**：无 config/表满/上游失败 → 客户端收不到响应（丢包），计数可观测；无直连。
- [ ] **U7 原帧消费**：抓包/计数证明原帧被 block，只有注入副本。
- [ ] **U8 回归**：host TCP/UDP、forwarded TCP、`gate.ps1` 仍 PASS。
- [ ] **U9 路由**：Windows route print 不变；临时别名/路由测试后清理。
- [ ] **U10 IPv6**：至少一条 happy path（无地址则记录环境未验证）。

## Notes

- 第一版单入口、单 listener 端口、单 L；多入口/多 L 留 B6。
- 用户态复用现有 host UDP 的 socket/封装；forwarded 只增加“先查 forwarded 表 + 按 upstream 选”。
- UDP 无 RST；block/失败表现为静默丢包。
- 分片 UDP 只处理首片；非首片无端口，按 fail closed 并写文档。
