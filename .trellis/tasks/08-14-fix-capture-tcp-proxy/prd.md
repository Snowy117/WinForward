# 修复抓包路径网络中断与 TCP 代理失效

## Goal

修复当前版本"开了 WinForward 就断网"的三个抓包/代理路径缺陷，使网关本机与下游客户端在 WinForward 运行时网络正常、TCP 按策略走代理。用户价值：WinForward 从"启动即全网断连"恢复为可用的透明代理网关。

## Background / Confirmed Facts（诊断已证实）

- **F1（致命，根因）nonFlow 帧被策略吞掉**：ARP/ICMP/ICMPv6 ND 等无法解析为 TCP/UDP 流的帧（`CapturePacketProcessor.cs:48` 一律走 `DispatchNonFlowAsync`），被 `FlowDispatcher.DispatchNonFlowAsync:163` 按策略评估，`action != Pass` 即 Block。host origin 的 nonFlow 必然命中 catch-all 空规则 `{}`→proxy→吞。日志实证：26 次 `packet.dropped reason=policy` 全部 kind=nonFlow，其中 23 帧 bytes=42（eth14+ARP28）。L2 解析断裂 → 测试报文（curl→1.1.1.1:443、q→1.0.0.1:53）从未进入捕获层（winforward.log 全程 grep 不到），表现为"connect 永远 pending / DNS 超时"。
- **F2（致命）转发 TCP redirect 架构不成立**：`TcpProxyCoordinator.CompleteNewRedirectAsync:191` 对转发流沿用 WinpkFilter host 模式重写（src=服务器IP:客户端端口 → dst=客户端IP:监听端口），目的地址非本机，MSTCP 路由回客户端，listener 永远收不到 SYN。日志实证：1.1.1.1:853 转发流 redirect 后包变为 `1.1.1.1:40386→192.168.77.6:51023` 被 pass rule=1 发回客户端；`tcp.relay.started=0`、`tcp.redirect.reused=0`。
- **F3 已存在连接被静默吞**：proxy 决策的 TCP 流首见包非 SYN（如 WinForward 启动前已建立的 Foxmail IMAP 连接，首包 85 字节带载荷）→ `HandlePacketAsync:415` 返回 NotRelevant → `NdisPacketActionExecutor.cs:103-105` 注释明写"丢弃而非放行"→ 应用挂死。
- **F4 健康面**：UDP 代理全链路健康（svchost DNS 经 SOCKS5 有应答）；sing-box 侧经实测正常（`curl -x socks5://local:***@192.168.77.1:30890 https://1.1.1.1` → 301）；WinForward 停运时底层路由/NAT/ARP 全通。`PolicySnapshot.EvaluateForwarded`（Policy.cs:51）只评估 adapter 限定规则，否则 Pass——catch-all 与 remoteCidr 直连规则对转发流不生效是设计语义，非 bug。用户配置 remoteCidr 缺 192.168.1.0/24（WLAN 网段）属配置问题。

## Requirements

- **R1**：nonFlow 帧（非 IP、非 TCP/UDP、解析失败、分片）无条件 pass，不再经策略评估。改动点：`FlowDispatcher.DispatchNonFlowAsync:163`（删除 `EvaluateNonFlow` 策略路径，self-traffic 检查后一律 PassAsync + trace 日志）。
- **R2**：转发 TCP 代理修复为 DNAT-to-local（决策 D1）：转发流 SYN 重写为 `src=客户端:端口 → dst=本机地址L:监听端口`（L=原网卡上的本机 IP，禁用 127.0.0.1），不交换 MAC；listener accept 后按既有 relay→SOCKS5 链路转发；反向报文重写源为 `服务器:端口` 发往原网卡（不交换 MAC）。改动点：`TcpProxyCoordinator`、`TcpRedirectTable/TcpRedirectAssociation`（按 origin 区分 reverse/accept-peer 端点）、新增 `IAdapterLocalAddressProvider` seam 及 Windows 实现、`Program.cs:224` 接线。host 流保持现有 WinpkFilter 模式不变。
- **R3**：proxy 决策 TCP 流的 NotRelevant 报文（首见非 SYN 的已存在连接）改为 pass 放行（决策 D2），不再丢弃。改动点：`NdisPacketActionExecutor.ProxyAsync`（:103-105）。
- **R4**：relay 建立失败（SOCKS5 不可达/认证失败/上游失败）时向客户端注入合法 RST 快速失败（D1 附带约束，host 与转发流均覆盖），替代当前静默挂起。改动点：`TcpProxyCoordinator`（SYN/SYN-ACK 序号记录 + 失败路径）、新增 RST 帧构造器（`WinForward.Protocols`）。
- **R5**：单元/集成测试更新与新增（nonFlow pass、NotRelevant pass、转发 DNAT 重写形状、L 选择、按 origin 的 accept-peer/reverse 端点、MAC 交换门控、RST 注入），`dotnet test` 全绿。
- **R6**：网关实机 smoke 验证（验收标准 AC1-AC7 的可观测判据）。

## Acceptance Criteria

- **AC1（R1）**：网关上 WinForward 运行期间，下游客户端 ARP 解析与 ping 网关正常；winforward.log 中 kind=nonFlow 的 `packet.dropped reason=policy` 为 0；curl→1.1.1.1 等报文出现在捕获日志中。
- **AC2（R2）**：从 VM（192.168.100.2）与本机（192.168.77.6）`curl https://1.1.1.1` 经 WinForward 代理成功；winforward.log 出现 `tcp.redirect.created` 与 `tcp.relay.started`（>0）；singbox.log 出现来自 127.0.0.1 的 TCP inbound 且目的地为 1.1.1.1:443。
- **AC3（R3）**：WinForward 启动前已建立的代理策略内连接不中断（如 Foxmail IMAP 保持）；日志中 `tcp.packet.handled outcome=notrelevant` 后跟随 `packet.reinjected`。
- **AC4（R4）**：停掉 sing-box（或阻断 30890）后，新的 proxy 流 TCP connect 立即失败（RST/ECONNREFUSED），不再挂起；sing-box 恢复后新连接恢复代理。
- **AC5（R5）**：`dotnet test` 全部通过。
- **AC6（R2）**：无捕获回环病理：redirect 注入帧不被自身重复捕获，报文计数无爆炸增长。
- **AC7（R2 回归）**：网关本机（host origin）`curl https://1.1.1.1` 同样经代理成功（host 路径首次实证）。

## Key Decisions

- **D1（转发 TCP 路线，已拍板）DNAT-to-local**：保持 listener+relay 架构；转发流 SYN 重写为 `src=客户端:端口 → dst=L:监听端口`，redirect 表按 origin 区分端点；reverse 方向源改回 `服务器:端口` 发往原网卡。约束：
  - L 必须取该网卡上的本机 IP（如 192.168.77.1），禁用 127.0.0.1（martian 源回复可能在到达 miniport 前被协议栈丢弃，失去改写机会）。
  - smoke 需确认 Windows 防火墙不拦截注入的入站 listener 连接（host 路径同样从未被验证过）；若拦截，回退措施为配置防火墙放行规则（文档化，不纳入代码）。
  - 反向 tuple 簿记按 origin 分支；`HandleReverseAsync:326-334` 已有 forwarded 注入分支预留。
  - 附带修复：relay 建立失败注入 RST 快速失败（→ R4）。
  - 不选用户态 TCP 栈（.NET 无成熟方案）；不选降级不代理（与网关代理意图相悖）。
- **D2（已存在连接行为，已拍板）pass 放行**：连接保持直连直到自然结束；零断流，策略绕过有界（连接总会关闭，下一个连接即被代理）。不选注入 RST（构造合法 RST 成本高、边缘情况多，且会踢断到网关自身的 RDP 等长连接）。

## Out of Scope

- 调整 `EvaluateForwarded` 语义（catch-all/remoteCidr 对转发流生效）：设计语义，如需变更另行提案。
- 修改用户配置补 192.168.1.0/24：属配置侧建议，不动代码。
- sing-box 侧任何变更。
- 性能优化、ICMP 代理、TUN 模式。

## Risks / Deferred

- Windows 防火墙默认拦截入站 listener 连接的风险未在代码层排除——smoke 首个验证点（见 D1）。
- host origin TCP redirect 路径（WinpkFilter 模式）从未实机验证（smoke 时 relay=0 全为转发流），AC7 首次覆盖。
- 多 IP 网卡上 L 选择策略（同子网优先）可能在特殊拓扑选错——smoke 环境单 IP，风险记录。
- 部署路径：本机 dotnet 构建 win-x64 产物 → 用户拷贝到网关运行（网关无 WinRM，仅能从 VM 做客户端侧观测）。

## Notes

- 调试入口：evil-winrm-py → 192.168.100.2（WinLtsc VM，下游客户端，192.168.77.2/192.168.100.2/fd00:1234:5678:1::2）；网关 192.168.77.1/192.168.100.1/192.168.1.3 无 WinRM。开发机本身即下游客户端 192.168.77.6/192.168.100.6/fd00:1234:5678:1::6。
- 日志锚点：smoke/winforward.log（3085 行，止于 packet=504）、smoke/singbox.log（15:58:11 起）；测试流量 15:58:41+ 缺席于捕获层。
- 相关测试文件：tests/WinForward.Core.Tests/{TcpProxyCoordinatorTests,FlowDispatcherTests,TcpEndpointRewriteTests,NdisApiAbiTests}.cs。
