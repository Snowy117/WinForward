# 技术设计：B6 Forwarded 收口

## 1. 阶段划分

| 阶段 | 范围 | 依赖 |
|---|---|---|
| **B6a** | U7、分片 raw、reject 产品级、acked、stop/UAF、混合 NBL/return_miss、分片范围、capacity、host UDP flake、LWF 可见性 | B5 |
| **B6b** | 多入口/多 L、回程 key 含 server、热替换、StateCommand 观测 | B6a |
| **B6c** | 独立 Linux 门禁、gate 纳入、A5 成本、文档、最终验收/归档 | B6a/B6b |

## 2. B6a 正确性

### 2.1 U7 decoy `available=1`

- 现象：single/no-verify 场景 decoy（原始目的地监听）收到 1 条，说明原帧可能被 permit。
- 定位：入口/回程 classify 的所有失败/部分失败分支；注入失败时原帧是否误 permit；
  decoy 收到的是原帧还是注入副本（DNAT 后目的应是 listener，不是 decoy）。
- 修复：任何 redirect 决策下，原 NBL 必须 BLOCK；注入失败也 BLOCK + 计数。
- 测试：decoy 硬绑定 + 抓包/计数断言 `available=0`；single/no-verify 也跑。

### 2.2 分片 raw 9 片

- 用 raw socket 构造 IPv4 UDP 分片：首片 MF=1、offset=0；后续 offset；总 9 片。
- 或 Linux 小 MTU/`IP_MTU_DISCOVER` 强制分片，但必须断言实际分片数。
- 断言：`udp_fragmented=9`、`udp_blocked=9`、`udp_created=0`、listener 0；
  IPv4 首片/非首片、IPv6 分片各一条。
- 分片范围：只对 forwarded 入口/命中规则的分片 block；host UDP 分片不应被误伤
  （按入口 ifIndex 或规则收窄）。

### 2.3 reject 产品级

- fake SOCKS5 UDP 返回 `rep=7`（command not supported）。
- 走产品 `ForwardedUdpRelay`/`Socks5UdpAssociation`；断言 `associations=0`、
  `udp_return=0`、客户端无回程、无直连；日志 `failures=N`。

### 2.4 acked 真实 TCP ACK

- 客户端/服务端用 `TCP_INFO.tcpi_bytes_acked` 或服务端 ACK 计数；不要用 `len(received)`。
- `forwarded_tcp_client.py` 与 suite 统一；断言客户端发出的字节被对端 ACK，而不是实收。
- B4b/B5 的测试口径同步。

### 2.5 stop/UAF 与 gate 池检查

- `probe_forwarded.cpp` stop：outstanding 超时后不销毁池（记录告警/泄漏），或把
  `outstanding_injections` 纳入 stats；不能 2s 超时后直接释放池。
- `gate.ps1` 卸载检查加 `WfIj`（注入 NBL 池）/`WfTr`（redirect 表）池标签，和 `WfPr` 一起比较。

### 2.6 混合 NBL / return_miss / 分片范围

- 入口/回程逐 NET_BUFFER 时校验 5 元组与首 NB 同流；不同流 fail-closed + 计数（或明确不支持）。
- `return_miss`：混合命中+miss 的 NBL 不能提前 return，逐 NB 计数。
- 分片全局 block 仅对 forwarded 入口/命中规则生效；host UDP 分片按原路径。

### 2.7 capacity / host UDP flake / LWF

- capacity：4096 真满可用多客户端 IP/端口或测试构建；结论写清“真实 4096 是否压满”。
- host UDP U9/U11 单条 flake：定位计数 reset/顺序/并发；不能只重跑。
- LWF 关闭：带外/可恢复会话复测注入可见性；做不到则记录未验证。

## 3. B6b 多入口/多 L/多目的/热替换/观测

### 3.1 多入口/多 L

- `probe_forwarded_config` 从单 `listener_address/port` 改为 per-ingress 表：
  `ifindex -> {local_address, listener_port}`；或规则/表项带 L。
- 驱动入口 DNAT 按 `ingress_ifindex` 选 L；回程按表项 L/listener。
- 用户态为每个入口绑 listener（或同一 port、不同 L）；`WfpController` 启用顺序。
- 测试：两个入口（Internal + 另一个 vEthernet/别名）各一条流，L/回程正确。

### 3.2 回程 key 含 server

- 回程 key 从 `(listener, client)` 改为 `(listener, client, server)`，支持同一客户端端口
  多目的；表项/双索引同步。
- 测试：同一客户端端口先后/并发访问两个目的，回程各回各的。

### 3.3 热替换

- 表项 generation + upstream；旧流保持旧 upstream，新流新规则。
- 测试：换 blob 后新流走新 upstream，旧流继续旧 upstream；generation 回读。

### 3.4 观测

- `StateCommand`/`WfpDeviceClient` 读 `probe_forwarded_info`/`stats`；
  输出 installed/generation/rule_count/fallback/计数（per protocol）。
- 事件/计数按 ingress/upstream 维度（至少 info/stats 可读）。

## 4. B6c 门禁/性能/文档/验收

- 独立 Linux 门禁：`tools/linux/forwarded_*.py` + `tools/windows/prepare-b5-udp-e2e.ps1`；
  自动加/删 `.99`、防火墙、verify 端口；CI 禁止 `--no-verify`。
- `gate.ps1`：可纳入 forwarded-udp core/fragment/reject（不重装驱动的部分），失败即 gate。
- A5 成本：iperf3/内核 sink；同 pps/包大小 CPU 量级；58.1% 只作参考。
- 文档：`AGENTS.md`（ABI v6、Forwarded 状态）、`wfp-architecture.md`、README、
  `forwarded-rule-blob.md`；D7 产品决策记录。
- 最终：host/forwarded TCP/UDP/gate 全绿；归档 B6/父任务；`update_goal complete`。

## 5. 风险

| 风险 | 处理 |
|---|---|
| U7 原帧 permit 是结构问题 | 若无法修，B6 阻断，不提交为通过 |
| 分片 raw 构造复杂 | 用 raw socket/小 MTU；断言实际片数 |
| 多入口环境只有一块 Internal | 用另一 vEthernet/别名模拟；记录覆盖边界 |
| 4096 真满不可压 | 测试构建 + 文档说明；不宣称已压满 |
| LWF 关闭断管理 | 带外控制台/可恢复会话；做不到记录 |
| 性能测量环境噪声 | 同 pps/包大小、多次采样；只作量级 |
