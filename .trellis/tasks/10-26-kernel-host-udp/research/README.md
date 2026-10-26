# Research index: Kernel provider host UDP

任务 `10-26-kernel-host-udp` 的前期研究。三份文档都是「证据 + file:line 锚点」，没有产品代码改动。

| 文档 | 一句话 | 主要读者 |
|---|---|---|
| [host-udp-driver.md](./host-udp-driver.md) | 驱动侧事实：UDP 与 TCP 共用 `ALE_CONNECT_REDIRECT` 按关联 classify、36B context 经 cmsg（level 0/41，type 70）逐 datagram 读回、回包必须用同一个 listener socket `sendto`、listener_port 与 block sink 与 TCP 共用、connected UDP 的 context 不释放 | 写用户态监听/回包的人、审 E2E 口径的人 |
| [host-udp-userland.md](./host-udp-userland.md) | 现有 `UdpProxy` 引擎的复用面：只有 `UdpResponseReinjector`/`UdpAdapterTargetSource` 依赖 NDISAPI，其余（会话/协调器/setup 队列/容量/两档空闲/关联表/UoT）可直接复用；Kernel 后端的三处拒绝点；老脚手架为何应退役；1514 帧上限与 65507 的尺寸差异 | 写实现的人、做设计评审的人 |
| [host-udp-seams.md](./host-udp-seams.md) | 决策：方案 A（复用协调器 + 入口/出口适配器）对比方案 B（host 专属协调器）；5 处小接缝 S1–S8；第一批范围与 Windows E2E 矩阵 A1–A10；ABI 不需要变更；connected 泄漏的接受口径；4 项待定产品决策 | 做排期与拍板的人 |

## 三条最需要记住的结论

1. **不需要第二套代理实现**：`IUdpResponseSink` 是唯一回程接缝，host 实现只有「用收到包的那个 socket `sendto` 到 `Flow.Local`」。
2. **不需要 ABI/context 变更**：`probe_config` 与 v1 `redirect_context` 已经覆盖 UDP（`protocol=17`、`rule_index`、原始目的地），控制面保持 v8。
3. **两个必须先落定的数**：host UDP 的最大 datagram 上限（建议 65507，连带 `UdpSessionCapacity` 默认值）与 connected UDP 泄漏的接受口径。

## 已知的未验证项（写进第一批验收）

- 客户端 `recvfrom` 看到的回包源地址是否等于原始目的地——驱动不参与回程，只能实测。
- `ListenerSet` 现有 `V6ONLY` 的 v4/v6 UDP socket 对 v4-mapped **connected** 关联是否可达；`wfprobe` 用的是单 dual-mode socket，两者不等价。
