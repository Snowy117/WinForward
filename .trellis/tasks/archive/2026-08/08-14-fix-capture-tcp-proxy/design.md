# Design — 修复抓包路径网络中断与 TCP 代理失效

## 架构现状（相关部分）

```
NDIS capture → CapturePacketProcessor (TryParse)
             ├─ flow (TCP/UDP) → FlowDispatcher.DispatchAsync
             │    selfTraffic → reverse hook(TCP) → flow表 → 新流评估 → ExecuteDecision
             │    └─ Proxy → NdisPacketActionExecutor.ProxyAsync
             │         ├─ TCP → TcpProxyCoordinator.HandlePacketAsync
             │         │    SYN → HandleSynAsync: listener分配 → table.TryClaim → 重写SYN → 注入MSTCP
             │         │         listener.Accept → 校验peer → RelayFactory → SOCKS5 → tcp.relay.started
             │         │    非SYN有association → 重写注入 (ReinjectExistingFlowDataAsync)
             │         │    非SYN无association → NotRelevant → 执行器丢弃   ← F3
             │         └─ UDP → UdpProxyCoordinator（健康）
             └─ nonFlow → FlowDispatcher.DispatchNonFlowAsync → 策略评估 ≠Pass → Block  ← F1
```

TCP redirect 目前只有 WinpkFilter host 形状：SYN 重写为 `src=服务器IP:客户端端口 → dst=客户端IP:监听端口`，交换 MAC，注入 MSTCP（`TcpProxyCoordinator.CompleteNewRedirectAsync:186-209`）。`TcpRedirectAssociation` 端点（TcpRedirectTable.cs:27-29）按 host 形状硬编码：`ReverseSource=(客户端IP,P)`、`ReverseDestination=AcceptedPeer=(服务器IP,客户端端口)`。

## R1 — nonFlow 无条件 pass

`DispatchNonFlowAsync`：self-traffic 检查后不再调用 `EvaluateNonFlow`，一律 `CompleteAsync(Pass, PassAsync)`，trace 事件 `packet.action action=pass reason=nonFlow`。删除私有 `EvaluateNonFlow`（:186-199）。`PassAsync` 已按捕获方向回注（send→adapter / receive→MSTCP），ARP 请求（receive）→MSTCP、ARP 应答（send）→adapter 均自然成立。nonFlow 不入 flow 表（现状如此），逐帧处理无状态。

理由：WinForward 是代理工具而非防火墙；不可代理的帧阻断只会破坏 L2/PMTUD/ND，策略（fail-closed 配置项）只应约束可代理的 TCP/UDP 流。

## R2 — 转发 TCP：DNAT-to-local

### 数据流（forwarded origin，客户端 C=192.168.77.6:40386 → 服务器 S=1.1.1.1:853，L=192.168.77.1，listener 端口 P）

1. SYN 到达桥接网卡（receive，origin=forwarded）→ 策略 → proxy → `HandleSynAsync`。
2. `SetupNewRedirectAsync`：分配 listener（0.0.0.0:P 不变）；`ILocalAddressProvider.SelectLocalAddress(原网卡Id, family, C.Address)` 解析 L；**L 为 null → fail-closed Blocked + warn**（不降级 pass）。
3. `TcpRedirectTable.TryClaim` 携带 L：association 端点按 forwarded 形状构造：
   - `ForwardLocalAddress = L`（新增可空属性；host 形状为 null）
   - `ReverseSource = (L,P)`、`ReverseDestination = (C,C.port)`、`AcceptedPeer = (C,C.port)`
   - `_byReverse` 键 = ((L,P),(C,C.port))；去重检查同样用 forwarded 形状。
4. 重写 SYN：`TryRewriteTcpEndpoints(frame, C.Address, C.Port, L, P)`；**不交换 MAC**（到达帧 src=C_MAC/dst=本机MAC 已是合法入站形状）；`InjectAsync(towardMstcp:true, 原网卡Handle)`。自身注入帧不被重捕获（日志实证：redirect 注入帧只在被 MSTCP 路由外发后才以 send 方向重现，本修复后 dst=L 为本机不再外发）。
5. MSTCP 在 0.0.0.0:P 接受，peer=(C,C.port)=`AcceptedPeerEndpoint` ✓（:571 校验不变）。
6. SYN-ACK 由 MSTCP 经桥接网卡 send 发出 `(L,P)→(C,C.port)` → 捕获 origin=host → self-traffic 不匹配（通配要求 entry.Remote==(C,C.port)，注册的是 0.0.0.0:P ✗）→ reverse hook `IsReverseCandidate` 命中 → `HandleReverseAsync`。
7. 反向重写：`TryRewriteTcpEndpoints(frame, S.Address, S.Port, C.Address, C.Port)`——与 host 形状**完全相同的调用**；**MAC 交换门控为 towardMstcp**（forwarded 不交换：send 帧 src=本机MAC/dst=C_MAC 已正确）；注入 `towardMstcp:false, OriginAdapterHandle`（:326-334 已有分支）。
8. 双向数据：C→S 方向由 `ReinjectExistingFlowDataAsync`（同样按 origin 分支重写 `(C,C.port)→(L,P)`、不交换 MAC）；S→C 方向走第 7 步同路径。

### host origin 形状（不变）

`ForwardLocalAddress=null`；association 端点、IP/MAC 双交换、`towardMstcp:true` 全部保持现状。

### ILocalAddressProvider（新 seam）

```csharp
public interface IAdapterLocalAddressProvider
{
    IPAddress? SelectLocalAddress(string adapterId, AddressFamilyKind family, IPAddress clientAddress);
}
```

- 实现 `WindowsAdapterLocalAddressProvider`（与接口同置于 WinForward.Windows/AdapterLocalAddressProvider.cs——Runtime 本就引用 Windows，接口随实现同层以保持单一程序集边界）：`NetworkInterface.GetAllNetworkInterfaces()` → `AdapterIdentity.TryExtractGuid` 匹配 Id → `GetIPProperties().UnicastAddresses`；v4 取首个（同子网优先，掩码可得时）；v6 排除 link-local，前缀匹配客户端者优先；两族均排除 loopback；无候选 → null。
- 每次新 redirect setup 调用（仅 SYN 路径，低频），不缓存（KISS；网卡改 IP 即生效）。
- 注入 `TcpProxyCoordinator` 构造器（必传）；`Program.cs:224` 接线。测试用 fake。

### 失败与边界

- 容量/claim/重写/注入失败：沿用现有 fail-closed + `tcp.redirect.rejected`。
- 每流一 listener（现状不变），ephemeral 端口池 16K 上限与表容量一致。
- SYN 带载荷（TFO）：`HandlePacketAsync:400` Blocked 现状不变（Windows 不出站 TFO 载荷 SYN；保持 fail-closed）。

## R3 — NotRelevant → pass

`NdisPacketActionExecutor.ProxyAsync`：`outcome == NotRelevant` 时调用 `PassAsync(packet)`（按捕获方向回注原始帧），替换 :103-105 的丢弃注释。disposition 仍是 ProxyConsumed（lease 已由 dispatcher 完成），可观测性 = `tcp.packet.handled outcome=notrelevant` 紧跟 `packet.reinjected`。flow 表中该流决策保持 proxy，后续每包重复 NotRelevant→pass（代价为每包一次解析+两次字典查询，可接受；不引入 flow 决策突变）。

## R4 — relay 失败注入 RST

- SYN 时记录客户端 ISN：`HandleSynAsync` 路径从 SYN 帧解析 seq 存入 session（`ClientInitialSeq`），并保留原始 SYN 帧副本（≤128B，作 RST 模板）。
- `HandleReverseAsync` 检测到 SYN+ACK（flags 0x12）时记录 seq 到 association（`ServerInitialSeq`）。
- `RunAcceptLoopAsync` relay 建立失败 catch（:594-599）：先构造并注入 RST，再 `TearDownSessionAsync`：
  - 新构造器 `TcpResetBuilder.Build(原始SYN帧副本, S, C, seq: ServerInitialSeq+1, ack: ClientInitialSeq+1)`：交换 MAC（两种 origin 都需要——host 要"来自路由器"形状，forwarded 要发回客户端）、重写端点为 `(S:S.port)→(C:C.port)`、截断为 eth14+ip20+tcp20（data offset=5、flags=RST|ACK、IP totlen=40、重算校验和）。
  - 注入：host → `towardMstcp:true`；forwarded → `OriginAdapterHandle, towardMstcp:false`。
  - RST seq=对端下一期望序号，在窗口内，客户端立即 ECONNABORTED/ECONNREFUSED；不依赖 association 存活（无竞态）。
- SYN-ACK 未观察到即失败的场景不存在（relay 失败必在 accept 之后，accept 必已完成握手即 SYN-ACK 已经过 reverse hook）；序号缺失时退化为现状（teardown，不注入）。

## 兼容与回滚

- 配置 schema 不变；`EvaluateForwarded` 语义不变；UDP 路径零改动。
- 回滚按需求独立：R1/R3 单文件；R2/R4 集中在 coordinator+table+新增文件，`ForwardLocalAddress=null` 即退化为现 host 行为。

## 风险与验证点

- **Windows 防火墙拦截入站 listener**（host 路径同险）：smoke 第一验证点；若拦截 → 文档化防火墙放行规则，不改代码。
- **注入帧重捕获回环**：smoke 观察注入后无同源重复捕获、报文计数线性。
- **多 IP 网卡 L 选错**：同子网优先策略覆盖 smoke 拓扑；选不到 → fail-closed 有日志。
- **PMTUD 经代理断裂**：透明代理通病，R1 放行 ICMP 后 pass 流 PMTUD 正常；proxy 流由 MSTCP 终结，MSS 由 listener 侧协商，可接受。
