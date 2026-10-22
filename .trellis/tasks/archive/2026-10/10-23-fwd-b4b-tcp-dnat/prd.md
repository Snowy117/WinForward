# B4b：TCP DNAT、redirect 表与回程中继

## Goal

在 B4a 的 L2 骨架上打通第一条 Forwarded TCP 数据面：入口 SYN/数据 DNAT 到本机 listener，
记录 redirect 表并查询原始目的地/upstream；回程由 listener socket 发出后 SNAT 回原始服务器，
注入入口网卡；消费原帧，不做 B3 那种“加法注入”。用户态 listener 按 upstream_id 选择 SOCKS5
上游并中继，客户端无感。

## Background

- B4a 已交付：L2 callout、forwarded blob 匹配、pass/block、ABI v4、Windows adapter resolver、
  `wfprobe forwarded-rules`。redirect 规则在 B4a 整块拒绝（`STATUS_NOT_SUPPORTED`），B4b 放开。
- B3 的硬约束：原帧必须消费/改写，不能 PERMIT 原帧再注入副本；流级触发不是端口触发；
  VLAN/出向 layer 59；显式 `FwpsQueryPacketInjectionState0`；LWF 注入可见性未测。
- B2 已交付：`ForwardedUpstreams` 有序表、target→upstream_id、`IAdapterIdResolver`。
- B4b 只做 TCP；UDP 是 B5。多入口/多 L 第一版不做（单入口、单 listener 端口、单 L）。

## In Scope

1. 驱动：
   - forwarded config：listener 端口、listener 本地地址 L、enabled（ABI 递增）。
   - 入口 TCP redirect：命中 redirect 规则时建/查 redirect 表，改写目的 IP/端口到 (L, listener)，
     重算校验和，`FwpsInjectMacReceiveAsync0` 注入本机栈；**原帧 BLOCK**。
   - 中间数据包：按 redirect 表 DNAT + 注入 + BLOCK 原帧。
   - 回程：outbound 捕获源端口=listener 的 TCP 包，按表 SNAT 回原始服务器，
     `FwpsInjectMacSendAsync0` 注入入口网卡；**原帧 BLOCK**。
   - redirect 表：key/反向 key、客户端 MAC、upstream_id、ingress、状态、超时、容量、
     generation；查询 IOCTL。
   - 新流但没有 SYN/表项、无 L、表满、注入失败 → fail closed + 计数。
2. 用户态：
   - listener 接受后先查 forwarded redirect 表（新 IOCTL），查不到再走 host 的
     `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT`；抽象成 `IRedirectContextSource`。
   - 按 upstream_id 从 `ForwardedUpstreams` 选 SOCKS5，CONNECT 原始服务器，双向泵。
   - 失败关闭：查表失败拒绝；上游失败 RST；不直连。
   - `WfpController` 绑 listener → 下发 forwarded config/blob → enable；退出清理。
3. E2E：
   - Linux 客户端临时路由 `10.99.0.0/24 via 192.168.100.2`（测试后删除）。
   - 客户端连 `10.99.0.1:<port>`；Windows DNAT 到 listener；SOCKS5 上游（Linux fake 或真实）
     CONNECT 到 Linux 上的 echo/HTTP 目标。
   - 验证字节一致、回程源地址、半关闭/RST、失败路径。
4. 文档/ABI/门禁。

## Out of Scope

- UDP（B5）。
- 多入口/多 L/多 listener 端口（B6）。
- External switch/物理 NIC/嵌套 VM。
- 性能 benchmark（B6）。
- 完整 TCP 状态机（驱动不碰序列号；依赖本机栈）。

## Requirements

- **R1 原帧消费**：入口/回程命中 redirect 时，原帧必须 BLOCK 并注入改写副本；不允许原帧继续走原路径。
- **R2 流级触发**：按 5-tuple/redirect 表匹配，不用端口触发；端口只作为 listener 识别的一部分。
- **R3 表生命周期**：SYN 建表；FIN/RST/超时回收；容量上限 fail closed；generation 支持热替换
  （旧流保持旧 upstream，新流新规则）。
- **R4 回程**：listener 发出的包按表改写源 IP/端口为原始服务器；目的 MAC 用入口记录 client MAC；
  未命中表的 listener 包按普通本机流量处理（不能误改）。
- **R5 查询接缝**：用户态在 accept 后拿到原始目的地/upstream_id/rule；查不到拒绝，不猜。
- **R6 失败关闭**：无 L、表满、查表失败、注入失败、上游失败 → RST/block/丢包；不直连。
- **R7 路由表**：Windows 系统路由表不变；Linux 临时路由只用于测试并清理。
- **R8 回归**：host 路径、B4a pass/block、UDP/host gate 不受影响。

## Acceptance Criteria

- [ ] **B1 DNAT**：客户端 SYN 到 `10.99.0.1:<port>`，listener accept；IOCTL 能查到原始目的地/upstream。
- [ ] **B2 中继**：SOCKS5 CONNECT 到真实目标；双向数据字节一致；客户端看到的对端仍是 `10.99.0.1:<port>`。
- [ ] **B3 原帧消费**：抓包/日志证明原帧被 block，只有注入副本；客户端不会收到原帧的 RST/重复响应。
- [ ] **B4 回程**：listener 响应的源地址/端口被还原为原始服务器；客户端正常收到。
- [ ] **B5 半关闭/RST**：客户端/服务端半关闭、异常 RST 行为与直连一致。
- [ ] **B6 失败关闭**：无 L、表满、查表失败、上游失败 → RST/超时；无直连。
- [ ] **B7 多客户端/upstream**：两个客户端或两条规则命中不同 upstream_id，互不串。
- [ ] **B8 回归**：host `wfprobe rules`/selftest/gates、B4a forwarded pass/block、gate.ps1 仍 PASS。
- [ ] **B9 路由**：Windows `route print` 前后一致；Linux 临时路由测试后删除。

## Notes

- 第一版单入口、单 listener 端口、单 L；多入口/多 L 留 B6。
- 驱动不碰序列号/ACK/窗口；TCP 由 Windows 栈终结。
- redirect 表满/无 L/注入失败时必须计数，禁止“失败变直连”。
- 用户态先查 forwarded 表，再查 host context；两条路径共用 `RelayServer`/`PumpPair`。
