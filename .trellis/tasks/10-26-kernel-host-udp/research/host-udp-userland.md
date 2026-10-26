# Research: 现有 WinForward UDP 引擎与 Kernel 后端组合点

- **Query**: `UdpProxy/*` 哪些是包路径专用、哪些可复用；CLI 组合与 Kernel 后端接缝；target/local target/DNS 建模；老 Kernel 脚手架哪些该退役
- **Scope**: 内部（源码 + 规范）
- **Date**: 2026-10-26

---

## 0. 结论摘要

1. `src/WinForward.Runtime/UdpProxy` 里**只有 `UdpResponseReinjector`（及其 `UdpAdapterTargetSource`）依赖 NDISAPI**。会话、协调器、setup 队列、容量/空闲、关联表、skip 分类、UoT 全部与抓包形态无关，可以直接被 host UDP 复用。
2. 回程的唯一接缝是 `IUdpResponseSink`（一个方法，`UdpResponseReinjector.cs:16-19`）。host 侧只需要实现「用收到 datagram 的 socket `sendto` 给 `Flow.Local`」。
3. `FlowKey` 有 adapter-less 构造（`Domain.cs:98-99`：`NoSlot` + generation 0），host 流不需要 MAC 或 adapter slot；`UdpProxySession` 的 `ClientMac` 只是随值传递的字段，host sink 忽略它即可。
4. 会话模型与包路径的语义一一对应且更强（每 flow 独立 SOCKS5 关联、setup 队列带 TTL 与全局字节预算、两档 idle、teardown 原因分类）；不要另写一套。
5. `KernelHostTcpService` / `KernelPolicyCompiler` / `KernelHostTcpService.TryResolveTarget` 都在**三处**主动拒绝 UDP 与 local target：`KernelPolicyCompiler.cs:366-372`、`KernelHostTcpService.cs:159-163`、`ConfigurationKernelCapabilities.cs:52-55`。这是 host UDP 要拆掉的闸门。
6. 老脚手架（`kernel/src/userland/WinForward.Userland/Relay/`）的 UDP 中继**从未支持 host**：`TryReadUdp` 是 stub，从不读 cmsg，只按 4-tuple 查 forwarded 表。应退役而不是增强。
7. 缓冲尺寸是关键差异：包路径把 payload 上限定在 1472 字节（1514 帧 − 42），host UDP 的 datagram 上限是 65507 字节。沿用 1514 会丢掉大 DNS 响应（EDNS0）与任何大 UDP 报文。

---

## 1. 现有引擎：文件与可复用性

| 文件 | 职责 | host 复用 | 依据 |
|---|---|---|---|
| `UdpProxy/UdpProxyCoordinator.cs` | 会话字典、ready 缓存、setup 调度、容量、teardown 协议 | ✅ 直接用 | `UdpProxyCoordinator.cs:60-112` 构造参数只有 transport 工厂、sink、两个 `NativeBufferPool`、`ISetupExecutor`、options |
| `UdpProxy/UdpProxyCoordinator.Send.cs` | span 入口、admission、send 桥、teardown 原因 | ✅ 直接用 | `Send.cs:21-36`、`55-111`、`121-152`、`214-220` |
| `UdpProxy/UdpProxyCoordinator.Sweep.cs` | 两档空闲回收 + 冷却清理 | ✅ 直接用，但**需要有人定时调** | `Sweep.cs:41-90` |
| `UdpProxy/UdpSessionSetup.cs` | 8 宽 setup 限流、dial-start 重打戳、关联 claim、FIFO flush | ✅ 直接用 | `UdpSessionSetup.cs:82-139`、`216-246` |
| `UdpProxy/UdpProxySession.cs` | 收发循环、活动戳、skip 分类、foreign-source 检测、注入 | ✅ 直接用（忽略 `ClientMac`） | `UdpProxySession.cs:200-225`、`337-390`、`406-430`、`433-450` |
| `UdpProxy/UdpTransportContracts.cs` | transport/sink/factory 接口、skip 原因、两类异常 | ✅ 直接用 | `UdpTransportContracts.cs:114-137`、`157-165` |
| `UdpProxy/UdpAssociations.cs` | `(原 flow → relay alias)` 关联表 | ✅ 直接用 | `UdpAssociations.cs:24-63` |
| `UdpProxy/UdpProxyOptions.cs` | 容量、relay 接收缓冲、帧上限、时钟 | ✅ 直接用，需要 host 的帧上限值 | `UdpProxyOptions.cs:16-50` |
| `UdpProxy/UdpSetupQueueBudget.cs`、`UdpSetupCooldownTable.cs` | 全局字节预算、失败冷却 | ✅ 直接用 | 由协调器构造 |
| `UdpProxy/LocalUdpTransport.cs` | 每 flow 一个 socket，verbatim 转发到配置端点 | ✅ 直接用 | `LocalUdpTransport.cs:50-62`、`166-218` |
| `Socks5/Socks5UdpTransport.cs` | 每 flow 一条 UDP ASSOCIATE + relay socket | ✅ 直接用（UoT 分支在同工厂） | `Socks5UdpTransport.cs:24-104` |
| `Socks5/Socks5UotTransport.cs` | UoT v2 connect-mode 承载 | ✅ 直接用 | `Socks5UotTransport.cs:24-45` |
| `UdpProxy/UdpResponseReinjector.cs` | 重建以太帧并注入 MSTCP/adapter | ❌ 包路径专用 | 唯一 import `WinForward.NdisApi` 的文件（`UdpResponseReinjector.cs:5`、`110-118`） |
| `UdpProxy/UdpAdapterTargetSource.cs` | adapter 句柄 + MAC 快照 | ❌ 包路径专用 | `UdpAdapterTargetSource.cs:12`、`27-40` |

`grep -rn "NdisApi|IPacketReinjector|AdapterSlotTable" src/WinForward.Runtime/UdpProxy/` 的结果集中在 `UdpResponseReinjector.cs` 与 `UdpAdapterTargetSource.cs`，其余文件零命中。

### 1.1 会话/协调器的可观测语义（host 侧照抄即可）

- 容量：`_sessions.Count >= Capacity` 时拒绝并计数 `udpCapacityRejections`（`Send.cs:70-78`），阈值就是 `UdpSessionCapacity`（`ConfigurationModels.cs:184`）。
- setup 窗口：新 flow 的 datagram 进每 flow 有界队列（32 包 / 32 KiB，`UdpProxyCoordinator.cs:14-15`），并有**全局**字节预算（`UdpProxyCoordinator.cs:308-354`）；flush 时超过 5 秒 TTL 的丢弃（`UdpSessionSetup.cs:39`、`223-228`）。
- setup 失败冷却：只有 `SetupFailure` 会写冷却（`UdpProxyCoordinator.cs:518-521`），因此拒绝的 ASSOCIATE 每个冷却窗口只重拨一次，而不是每次客户端重传都重拨。
- 两档空闲：一条 datagram 且已收到回包 = 一次性交换，用 5 秒档；其余用配置的 `UdpSessionIdleSeconds`（`UdpProxySession.cs:267-294`、`UdpProxyCoordinator.cs:267`）。
- 回包源核对：解码出的源与 `Flow.Remote` 不一致时计 `udpResponseSourceMismatch` 并限速告警，**但不改变投递**（`UdpProxySession.cs:406-430`、`488-502`）。
- skip 分类：`UnexpectedSource` / `Oversized` / `Malformed` / `ConnectionReset`（`UdpTransportContracts.cs:14-34`），ICMP 端口不可达不得终止会话（`LocalUdpTransport.cs:390-394` 关掉 `SIO_UDP_CONNRESET`）。

---

## 2. 身份与目标建模

### 2.1 FlowKey

`FlowKey.Create(local, remote, protocol, origin)`（`src/WinForward.Core/Domain.cs:98-99`）走 `NoSlot` + generation 0 的 adapter-less 形态；`Equals` 比较 origin 与 adapter，hash 故意不含它们（`Domain.cs:171-197`）。host 流：

```
Local  = 客户端源端点（WSARecvMsg 的 name）
Remote = redirect context 的原始目的地（family + original_address + original_port）
Origin = FlowOriginKind.Host
```

这样回包目标天然是 `Flow.Local`，而 `UdpProxySession` 的源核对比对的是 `Flow.Remote`（`UdpProxySession.cs:424`）——正好与包路径同构。

### 2.2 ProxyTarget / LocalTarget / DNS

- `ProxyTarget(Name, Socks5?, Local?)`，`IsLocal => Local is not null`（`ConfigurationModels.cs:160-163`）。名字命名空间在两种 target 之间共享。
- `LocalTarget(Name, Endpoint)`（`ConfigurationModels.cs:152`），配置校验要求 Host 是 **IP 字面量**（`ConfigurationTargets.cs:79`、`99` 的注释与错误文案），因此 local target 自身不会触发 DNS 解析。
- 「DNS」在产品里不是独立引擎：它只是「`Protocol: ["udp"]` + `RemotePort: ["53"]` + target」的一种配置形态，见 `examples/local-dns.json`（local target `127.0.0.1:53`）与 `examples/dns-proxy.json`（SOCKS5 target）。DNS 的差别只体现在**流量形态**（一问一答，正好命中一次性交换的 5 秒档）与**报文尺寸**（EDNS0 常见 1232–4096 字节）。
- 配置层已经强制「local target 规则必须 `Protocol` 恰好是 `udp`」（`ConfigurationRules.cs:87-92`），即 local target 天生只服务 UDP；这与 WFP 驱动「local target 只能配 UDP」的判断一致。

### 2.3 rule_index → target 的映射

`KernelPolicyCompiler` 建立 `ProxyTarget?[] TargetsByRuleIndex`，契约是「配置里的第 i 条 host 规则 = blob 的第 i 条规则」，pass/block 为 null（`KernelPolicyCompiler.cs:20-25`、`75-88`、`154-157`）。redirect context 的 `rule_index` 直接当数组下标用（`KernelHostTcpService.cs:144-168`）。

host UDP 需要在这张表上做的唯一改动：**允许 local target 进入表**。目前有两道拒绝：

- 配置闸：`ConfigurationKernelCapabilities.cs:52-55` 直接拒绝任何「proxy + 未限定 tcp」的 host 规则。
- 编译与运行期：`KernelPolicyCompiler.cs:366-372` 与 `KernelHostTcpService.cs:159-163` 拒绝 local target。

TCP 侧对 local target 的拒绝在 host UDP 落地后**仍要保留**：一条 local-target 规则按配置只能是 UDP，但 `TryResolveTarget` 是共用的，TCP 路径拿到 local target 说明 blob 与配置不一致，应当 fail-closed 而不是用本机端点服务 TCP。

---

## 3. CLI 组合现状与 host 差异

### 3.1 包路径的组合

`UdpProxyComposition`（`src/WinForward.Cli/UdpProxyComposer.cs:21-31`）携带 `UdpAdapterTargetSource`、`AdapterSlotTable`、`MaximumFrameSize`、两个 `NativeBufferPool`、`SetupExecutor`、`Socks5AddressCache`、容量/缓冲。`UdpProxyComposer.Create`（`UdpProxyComposer.cs:65-86`）**强制要求 `IPacketReinjector`**，因为它直接用 `new UdpResponseReinjector(reinjector, ...)`。

真正的资源归属在 `DurableCaptureBundle`：

- 池：`udp.setupQueue` = `new NativeBufferPool(maximumFrameSize)`（`DurableCaptureBundle.cs:259`）；`udp.receiveWindow` = `new NativeBufferPool(ReceiveWindowSize(maximumFrameSize), ReceiveWindowPoolCapacity(capacity))`（`:265`）。
- sweep：`new IdleExpirySweeper(dispatcher, tcpCoordinator, udpCoordinator, relayIdleTimeout: configuration.UdpSessionIdleTimeout, ..., udpOneShotIdleTimeout: UdpProxyCoordinator.OneShotIdleTimeout)`（`:331`）。

### 3.2 host 侧不可直接复用的三处

1. **`UdpProxyComposer.Create` 的 `IPacketReinjector` 参数**。host 没有 reinjector。需要把「sink 的构造」从 `Create` 里拆出来（例如 `Create(transportFactory, IUdpResponseSink sink, composition)` 的重载），否则 CLI 只能自己重新拼一遍工厂。
2. **`IdleExpirySweeper` 要求 `FlowDispatcher`**（`src/WinForward.Runtime/IdleExpirySweeper.cs:47-62`，构造函数第一个参数非空）。`FlowDispatcher` 是抓包路径的 flow 表。host 侧不需要它。可行的最小做法是在 host UDP 服务里自己起一个 `PeriodicTimer`，直接调 `coordinator.RemoveExpiredAsync(now, idle, oneShot)`（`Sweep.cs:41-90`），并复用 `IdleExpirySweeper.DeriveUdpSweepInterval`（`IdleExpirySweeper.cs:84-90`，`internal static`，CLI 通过 `InternalsVisibleTo "WinForward"` 可见）来保持与包路径相同的节拍。
3. **`UdpProxyComposition` 里的 adapter/帧上限**。host 侧只需要 `NativeBufferPool ×2`、`SetupExecutor`、`Socks5AddressCache`、容量、缓冲、时钟与一个 `SelfTrafficRegistry`。

### 3.3 可直接沿用的 CLI 便利件

- `UdpProxyComposer.PrimeSocks5AddressCacheAsync`（`UdpProxyComposer.cs:41-58`）：启动时把 SOCKS5 主机名解析一次，host 侧同样需要（否则首次 UDP 关联会踩 DNS）。
- `InternalsVisibleTo "WinForward"`（`src/WinForward.Runtime/WinForward.Runtime.csproj`）：CLI 可以调用 `UdpProxyCoordinator.TrySendSpanAsync`（`internal`）与 `IdleExpirySweeper.DeriveUdpSweepInterval`。已有先例（`DurableCaptureBundle.OnScopeInstalled`）。

---

## 4. Kernel 后端现状

### 4.1 生命周期与顺序（host TCP 已验证，host UDP 必须插进同一条序）

`KernelHostTcpService.TryStart`（`KernelHostTcpService.cs:107-136`）：

1. `VerifyControlAbi`（`:291-299`，读 stats 的 `abi_version`）。
2. `DisableStaleState`（`:306-311`）：先 `SetConfig(disabled)`，再清 forwarded。
3. 编译策略（`:112-116`）。
4. `ListenerSet.Bind()`（`:118`，通配 v4+v6，TCP 与 UDP 同端口）。
5. 需要 block 时 `BlockSink.Bind(listeners.Port)`（`:121`）。
6. 下发并回读校验规则 blob（`:124-126`、`:323-337`）。
7. `ProbeConfig.EnabledFor(processId, listenerPort, [processId], blockSinkPort)` + 回读校验（`:128-131`、`:339-357`）。
8. 起 accept 循环（`:133`、`:359-410`），报 ready。

`DisposeAsync`（`:238-284`）的顺序是：先关驱动配置 → 取消 scope → 释放 listener → 释放 block sink → 排空 → 清 forwarded。host UDP 的接收循环必须落在这个 scope 里（`QuiescenceScope`，`src/WinForward.Runtime/QuiescenceScope.cs:37`，`internal`），并在 liststener 释放前停止。

### 4.2 现有的拒绝点

| 位置 | 行为 |
|---|---|
| `KernelHostTcpService.cs:144-168` | `TryResolveTarget`：下标越界 / 非 proxy 规则 / local target 一律拒绝（fail-closed，不回落默认 target） |
| `KernelRelaySession.cs:103-109` | 接受连接后如果 context `protocol != TCP` 直接拒绝（「驱动会把 host UDP 关联改道到同一端口」已写在注释里） |
| `KernelPolicyCompiler.cs:366-372` | local target 在编译期就被拒 |
| `ConfigurationKernelCapabilities.cs:52-55` | proxy 规则必须显式 `Protocol: ["tcp"]` |

这几处正好构成 host UDP 的改造边界：配置闸放开、编译期接受 local target、运行期按上下文协议分流（TCP 走 `KernelRelaySession`，UDP 走新的接收循环），而 `KernelRelaySession` 的协议拒绝保留为纵深防御。

### 4.3 日志与计数

`KernelBackendLog` 现有事件全部是 TCP 措辞：`kernel.ready` 的文案是 "Kernel host TCP interception is ready"（`KernelBackendLog.cs:33-34`），`kernel.started` 同（`:36-37`），`kernel.listener.bound` 只报 TCP listener 数（`:27-28`）。host UDP 需要新增 UDP 侧的就绪/停止/拒绝事件，并让 ready 行能表达「UDP 是否启用」。计数值落在 `RuntimeCounters`（共享）：`udpCapacityRejections`、`udpSetupRejections`、`udpSetupFailures`、`udpAssociationLost`、`udpFailClosedDrop`、`udpResponseSourceMismatch`、`udpLocalTargetFlows/Failures`（`src/WinForward.Runtime/RuntimeCounters.cs:20-91`）。这些是**全局**计数器，两个 edition 共用，不需要新命名。

---

## 5. 缓冲与尺寸：包路径与 host 的最大差异

- `UdpFrameBuilder.DefaultMaximumEthernetFrame = 1514`（`src/WinForward.Protocols/UdpFrameBuilder.cs:15`）。包路径的 payload 上限因此是 1472（1514 − 42），`LocalUdpTransport` 的注释也这样写（`LocalUdpTransport.cs:376-382`）。
- 接收窗口 = `MaximumFrameSize + 22（SOCKS5 UDP 头）+ 1（oversize 哨兵）`（`UdpProxyCoordinator.cs:217-218`）；发送缓冲分别是 `6+16+cap`（SOCKS5，`Socks5UdpTransport.cs:167-171`）、`cap`（local，`LocalUdpTransport.cs:128-131`）、`UoT 前缀 + cap`（`Socks5UotTransport.cs:36-45`）。
- 每次接收一旦填满缓冲就按 `Oversized` 跳过（`LocalUdpTransport.cs:382`、`Socks5UdpTransport` 同构），因此**上限必须覆盖真实最大 datagram**：IPv4 UDP payload 上限 65507 字节，IPv6 理论更大但实际同量级。
- 池容量：`ReceiveWindowPoolCapacity(capacity) = capacity + max(64, capacity/16)`（`UdpProxyCoordinator.cs:233-257`），`NativeBufferPool` 按需分配、只保留租出过的量（`UdpProxyCoordinator.cs:226-231`）。因此把窗口从 1514 抬到 64 KiB，**稳态内存 = 并发会话数 × 窗口**，4096 会话约 256 MiB、16384 会话约 1 GiB。
- 结论：host UDP 的帧上限是一个必须显式决定的产品参数（建议 ≥ 4096 以覆盖 EDNS0，理想 65507/65535），不能沿用 1514。

---

## 6. 老脚手架：可复用 vs 应退役

`kernel/src/userland/WinForward.Userland/` 是导入前的控制器脚手架，csproj 注释已说明它只是兼容壳（`WinForward.Userland.csproj` 顶部注释：ABI/blob/设备客户端已移到 `WinForward.Kernel.Contracts`，context 查询/`BlockSink`/`ListenerSet` 已移到 `WinForward.Kernel.Runtime`）。

| 文件 | 内容 | 判定 |
|---|---|---|
| `Relay/ForwardedUdpRelay.cs`（530 行） | 自建 `AssociationKey`、`MaxAssociations=1024`、30 秒空闲、5 秒 sweep、`Channel` 队列、自写 SOCKS5 UDP ASSOCIATE | **退役**。它是第二套代理实现，且**从不读 cmsg**：目的地靠 `IRedirectContextSource.TryReadUdp(client, listener, ...)`（`:343`、`:339-356`），host 源是 stub（`src/WinForward.Kernel.Runtime/Relay/HostRedirectContextSource.cs:31-40`，错误文案「host UDP 中继不在本仓库」）。 |
| `Relay/Socks5UdpAssociation.cs` / `Socks5UdpCodec.cs`（212 + 150 行） | 简化版 ASSOCIATE 与编解码 | **退役**：`WinForward.Runtime/Socks5` 已有完整实现（鉴权、超时、UoT、错误分类、关联丢失语义）。 |
| `Relay/RelayServer.cs`（274 行） | TCP 中继服务器 | 已由 `KernelRelaySession` + `TcpProxyRelayFactory` 替代。 |
| `Relay/Socks5Client.cs`（285 行） | 脚手架 SOCKS5 客户端 | 由 `WinForward.Runtime/Socks5` 替代。 |
| `src/WinForward.Kernel.Runtime/Relay/*` | `HostRedirectContextSource`、`ForwardedRedirectContextSource`、`RedirectDestination`、`SocketIoctl`、`BlockSink`、`ListenerSet`、`IRedirectContextSource` | **保留并扩展**。`IRedirectContextSource.TryReadUdp` 的签名（`EndPoint client, IPEndPoint listener`）是为「按 4-tuple 查表」设计的，host 的 cmsg 路径拿不到 `listener` 端点的语义（context 随 datagram 到达），需要新的接缝而不是复用这个签名。 |

约束：`WinForward.Kernel.Runtime` **不能**引用 `WinForward.Runtime`（csproj 注释明确：不得引用 Runtime / NdisApi / 驱动）。因此 host UDP 的服务与 sink 应放在 `src/WinForward.Cli/Backends/Kernel/`（与 `KernelHostTcpService` 同级），`Kernel.Runtime` 只放平台无关的 socket/ioctl 小件（例如 cmsg 解析器）。

---

## 7. Caveats

- `UdpProxyCoordinator.SessionCount`、`RelayReceiveBufferBytes`、`Capacity` 是 public（`UdpProxyCoordinator.cs:114-125`、`180-184`），可直接用于 heartbeat；但 host 路径的 heartbeat 目前不存在（`RuntimeHeartbeat` 由包路径 runner 驱动，`src/WinForward.Runtime/RuntimeHeartbeat.cs:28`）。第一批是否需要新的 heartbeat 段属于产品决策。
- `UdpProxySession` 是 `internal sealed`，只有协调器能建；host 侧不得直接构造会话。
- `Socks5UdpTransportFactory` / `LocalUdpTransportFactory` 需要一个 `SelfTrafficRegistry`（`SelfTrafficRegistry.cs:7`）。host 路径下它是无害的空转（驱动用 self-PID 闸），但接口是必填，不能传 null。
- 未找到任何 host UDP 的既有实现或半成品；`git log` 里也没有相关提交。
- `UdpProxyCoordinator.TrySendSpanAsync` 的 `packetSequence` / `flowGeneration` 默认 0，日志会省略这两项（`Send.cs:222-233`）。host 路径没有包序号，保持 0 即可。
