# Research: fix-minor-races 可行性研究（D1–D4）

- **Query**: pre-design 研究：RST 序号来源、适配器句柄时效、SOCKS5 30s 窗口、轮询结论、测试基建、改动面、风险边界
- **Scope**: internal（代码考古）+ 归档任务记录
- **Date**: 2026-08-28
- **HEAD**: `42a651621009f44ce5d54c56455c806dc7a47721`（所有行号以此为准；PRD 中引用的行号是旧 HEAD，已漂移）

---

## 1. D1 — RST 确认序号过期

### 1.1 现状：seq/ack 来源

`TcpProxyCoordinator.TryInjectClientResetAsync`（`src/WinForward.Runtime/TcpProxyCoordinator.cs:732-752`）：

```csharp
var reset = TcpResetBuilder.BuildReset(synTemplate, ..., serverInitialSeq + 1, clientInitialSeq + 1);
```

- RST 的 **seq = `ServerInitialSeq + 1`**（listener ISN + 1），**ack = `ClientInitialSeq + 1`**（client ISN + 1）。
- 注入方向：`association.OriginalKey.Origin != FlowOriginKind.Forwarded` → host 流 towardMstcp，forwarded 流走 `association.OriginAdapterHandle`（line 741）。
- 触发链：accept 完成 → `_relayFactory.EstablishAsync` 抛异常 → `RunAcceptLoopAsync` catch（line 819-823）→ `HandleRelaySetupFailureAsync`（line 758-764）→ dispose accepted socket → `TryInjectClientResetAsync` → `TearDownSessionAsync`。

### 1.2 `TcpRedirectAssociation` 现存序号/模板字段

`src/WinForward.Runtime/TcpRedirectTable.cs:19-74`：

| 字段 | 写入点 | 内容 |
|---|---|---|
| `ClientInitialSeq : uint?` | `RecordClientSyn`（Coordinator:375-382），SYN rewrite **前**解析 | 客户端 ISN（seq 偏移 `14 + IpHeaderLength + 4`，big-endian） |
| `OriginalSynFrameCopy : byte[]?` | 同上 | 原 SYN 帧前 128 字节模板（Ethernet 头用于镜像 MAC） |
| `ServerInitialSeq : uint?` | `RecordServerSynAck`（Coordinator:388-396），reverse leg SYN-ACK（flags `SYN|ACK`）rewrite 前解析 | listener 侧 ISN |

**没有任何字段跟踪连接建立后客户端已推进的序号。** 这是 D1 的缺口本体。

### 1.3 forward 方向数据包路径（seq 跟踪的候选写入点）

完整路径：`NdisCapturePump.RunAsync`（批读）→ `CapturePacketProcessor.ProcessAsync`（`PacketFlowClassifier.ClassifyFlow` 建 FlowContext）→ `FlowDispatcher` → `NdisPacketActionExecutor.ProxyAsync`（`NdisPacketActionExecutor.cs:72`）→ `TcpProxyCoordinator.HandlePacketAsync`（Coordinator:535-573）：

1. 非 reverse candidate、非 SYN、`TryResolveByOriginal` 命中 → **`ReinjectExistingFlowDataAsync`（Coordinator:398-434）** —— 客户端所有后续数据（含纯 ACK）都经过这里；
2. 在 `TryRewriteForwardLeg` 改写目的端前，帧仍是**原始形态**（源=client 原 tuple，seq 未动）。

**最自然的跟踪点**：`ReinjectExistingFlowDataAsync` 内、rewrite 之前（与 `RecordClientSyn` 同样的"read-then-write"模式，见 Coordinator:453 的既有先例注释）：
- 解析：`IpTcpUdpPacket.TryParse` → `seq = BE32(frame[14+ipHdr+4 ..])`，payload 长度可用 `帧长 - (14 + ipHdr + tcpHdr)` 或 IPv4 total-length 字段（以太网 padding 场景后者更准）；
- 维护 `max(seq + payloadLen)`（纯 ACK 不推进、重传不超 max，天然单调）；SYN/FIN 各占 1 序号需注意。
- 开销：每 forward 数据包一次 `TryParse` + 一次 32 位读 + 一次比较（无锁写或 `Interlocked`/volatile uint 字段即可，量级 ~几十 ns/包）。classifier 上游已 parse 过但 view 不下传，这里是重新 parse。
- 备选写入点：`HandleReverseAsync` 不行（那是 listener→client 方向）；`RecordServerSynAck` 位置不对（reverse leg）。

### 1.4 `TcpResetBuilder.Build` 构造参数

`src/WinForward.Protocols/TcpResetBuilder.cs:31-50`：`BuildReset(originalSynFrame, serverAddress, serverPort, clientAddress, clientPort, uint serverSequenceNext, uint clientSequenceNext)` —— **seq/ack 本来就是任意 uint 入参，动态值零障碍**，无需改签名即可传 `maxClientSeq`。flags 固定 `RST|ACK = 0x14`（line 22, 108）。若 design.md 选 RFC 5961 式 RST（`seq = server_next`、省 ack/不用 ACK 位），需在此文件加变体（当前无此构造）。

---

## 2. D2 — OriginAdapterHandle 时效性

### 2.1 Generation / AdapterContext 机制现状

- `AdapterContext(string? StableId, string? Name, long Generation)`：`src/WinForward.Core/Domain.cs:67`；`FlowKey` 携带 `OriginAdapterId` + `OriginAdapterGeneration`（Domain.cs:75-76），在 `PacketFlowClassifier.ClassifyFlow` 由 pump 绑定的 `WindowsAdapter` 填充。
- `WindowsAdapter(StableId, FriendlyName, InternalName, RuntimeHandle : nint, Generation)`：`src/WinForward.Windows/Platform.cs:30-35`。
- **Generation 是"每次库存快照自增计数器"**：`WindowsAdapterInventory.GetCurrentAdapters()`（`src/WinForward.Windows/AdapterIdentity.cs:47-60`）里 `var generation = ++_generation;` —— 它标记快照版本，不是适配器生命周期标识。
- **运行期不重新枚举**：`Program.cs:166-171` 有明确注释 `"ADAPTER-LIST CHANGE SEAM (deferred)"` —— capture scope 只在启动时对快照解析一次，`SetAdapterListChangeEvent` 驱动的重解析是Deferred milestone。ABI 层（`NdisApiAbi.cs`）目前**没有**绑定 `SetAdapterListChangeEvent`，只有 `GetTcpipBoundAdaptersInfo`（Abi:189-191；`NdisApiDriver.GetAdapters()` Driver:53）。
- **结论：数据路径上没有"当前代"校验先例**；`FlowKey.OriginAdapterGeneration` 在整个 run 内恒等于 pump 启动值，目前无人比较它。

### 2.2 reverse 注入失败时的行为

链路：`HandleReverseAsync`（Coordinator:436-497）→ 句柄选择 `targetHandle = towardMstcp ? packet.Metadata.AdapterHandle : association.OriginAdapterHandle`（line 476）→ `TcpRedirectInjector.InjectAsync`（`TcpRedirectInjector.cs:11-23`，towardMstcp=false 时 `reinjector.SendToAdapter`）→ `NdisPacketReinjector.SendToAdapter`（`NdisPacketReinjector.cs:27`）→ `NdisApiDriver.SendPacketToAdapter`（`NdisApiDriver.cs:271-280`）——native 返回 0 即 **`throw new Win32Exception(...)`**（Driver:279）。

异常被 `HandleReverseAsync` 的裸 `catch`（Coordinator:490-494）捕获 → `FailAssociationAsync`（Coordinator:952-958）→ session teardown：listener dispose、relay dispose（活跃连接被拆，客户端侧看 socket 关闭）、表别名移除 + 写墓碑。**没有任何 reason 日志区分"句柄失效"与其他注入失败**（只有 `tcp.reverse.injected` 成功 trace，无失败专属事件）。

既有唯一"校验"：line 471-475 的 `OriginAdapterHandle == 0` 零值检查（fail-closed）。

### 2.3 可行 seam

- 协调器构造时注入 `IWindowsAdapterInventory`（或 `Func<...>` 查询）——inventory 本身就是委托 seam（`ndisAdapters`/`ipAdapters` 两个 Func，AdapterIdentity.cs:24-25），**可无 Windows 直接 fake**；按 `association.OriginAdapter.StableId` 重解析当前 `RuntimeHandle`。
- 或者更轻：注入一个 `nint → bool` 句柄有效性探针（驱动侧可用 `GetAdapterMode` 之类的既有调用当探针，Driver:85 附近），失效时走显式拆除 + 客户端 RST（可复用 `TryInjectClientResetAsync` 的构造）。
- 重解析的天然落点：`HandleReverseAsync` 的 `targetHandle` 选择处（line 476）之前；或在 `PacketFlowClassifier`/pump 层做全局句柄刷新（改动面更大）。

---

## 3. D3 — SOCKS5 30s 失败窗口

### 3.1 `ConnectAttemptTimeout` 实现结构

`src/WinForward.Runtime/Socks5Client.cs:15-16`：`MaxConnectionAttempts = 4`、`ConnectAttemptTimeout = 30s`（`private static readonly`，**非配置**）。

`ConnectAsync`（Socks5Client.cs:45-87）结构：
1. **DNS 解析**：`ResolveAddressesAsync`（line 314-336）用同一个 `timeout`（30s）做 `CancelAfter` + `WaitAsync` 双保险；
2. **逐地址串行尝试**：`ConnectOnceAsync`（line 96-158）每次 `attemptCancellation.CancelAfter(timeout)`，**预算覆盖 TCP connect + authenticate**（line 126-134），成功后该预算还延续到 SOCKS command + reply 解析（`RunWithinAttemptAsync` 用 `AttemptToken`）；
3. 连接被拒（RST）→ 立刻 `SocketException` 返回失败试下一地址；黑洞（无响应）→ 吃满 30s；
4. 尝试上限 4；全败抛 `IOException("Unable to connect to the configured SOCKS5 server.")`（line 86，smoke 日志里那句）。

**注意：30s 是 per-attempt 而非总预算** —— 理论最坏 ≈ DNS 30s + 4×30s ≈ 150s。PRD"总预算 30 秒"的表述对常见单地址场景成立，多地址场景实际更差。

`ConnectAsync` 已有可选参数 `maxAttempts`、`perAttemptTimeout`（line 51-52）+ `resolveAddresses`/`socketFactory` fake seam —— **缩短预算的参数管道已存在，只是 relay 调用点没传**。

### 3.2 从 accept 到 RST 注入的完整链路

`TcpProxyCoordinator.HandleSynAsync` → 注入改写 SYN（客户端数十 ms 内 established）→ `RunAcceptLoopAsync` 后台任务（Coordinator:147 起，非 await）→ `AcceptAsync` → **`await _relayFactory.EstablishAsync(...)`（Coordinator:802，inline await，阻塞的是该 session 的 accept loop，不影响别的流）** → `TcpProxyRelayFactory.EstablishAsync`（`TcpProxyRelay.cs:12-45`）→ `Socks5ControlConnection.ConnectAsync(server, ct, onSocketReady)`（**未传 perAttemptTimeout → 默认 30s**）→ `ConnectDestinationAsync`。失败 → catch（Coordinator:819-823）→ `HandleRelaySetupFailureAsync`：dispose accepted → `TryInjectClientResetAsync`（**失败后 RST 是立即的，问题只在失败来得慢**）→ teardown。

窗口内客户端数据：经 `ReinjectExistingFlowDataAsync` 改写给 listener，accept 前在 SYN backlog、accept 后在 accepted socket 接收缓冲——直到失败时 `accepted.DisposeAsync()` 才丢弃。

### 3.3 候选分段点（事实清单，取舍归 design.md）

1. **更短 connect 预算**：`TcpProxyRelayFactory.EstablishAsync` 传 `perAttemptTimeout`（一行，seam 现成）；首访失败（拒连）本就快，缩短的只是黑洞等待。
2. **区分首发失败快速 RST vs 多地址尝试**：`ConnectOnceAsync` 的 `SocketException`（拒连，快）与超时（慢）已天然区分在 `ConnectAttempt.Error`，可按此分级。
3. **UDP/TCP 分开预算**：UDP 控制连接是另一调用点 `Socks5UdpTransport.CreateAsync`（Socks5Client.cs:411-419，同样用默认 30s），可各自定预算。
4. **connect 前置到 accept 之前**（PRD 提及）：动 `SetupNewRedirectAsync`/`RunAcceptLoopAsync` 时序与 listener 生命周期 → 与 fix-port-budget 改动面重叠（见 §7）。
5. 配置层：`Socks5Server` record（`ConfigurationModels.cs:58-63`）只有 Name/Host/Port/Username/Password，**无超时字段**；PRD 约束"不改配置语义"——如需可调参数要么内部常量要么 design.md 重新议。

### 3.4 一个测试基建事实

`TcpProxyRelayFactory.EstablishAsync` 第 17-20 行**硬要求 `TcpAcceptedConnection` 具体类**（非接口就抛 ArgumentException），所以 coordinator 测试全部用 `FakeRelayFactory` 绕开；要测真实 relay 建立路径必须真 socket（`TcpProxyRelayTests` 即如此，走 loopback TcpListener）。

---

## 4. D4 — 轮询结论

**fix-datapath-throughput 的批量读已落地，但空队列轮询未消除**：其 implement.md（`.trellis/tasks/archive/2026-08/08-27-fix-datapath-throughput/implement.md` Step 2，行 36-37）明确记录"空批保留 poll delay"。当前 `NdisCapturePump.RunAsync`（`src/WinForward.NdisApi/NdisCapture.cs:70-74`）在 `readCount == 0` 时仍 `await Task.Delay(_pollDelay)`，默认 1ms（NdisCapture.cs:52）。**结论：批量读只摊薄了每包开销，1ms 空转轮询仍在，D4 未被兄弟任务解决，本任务需自行处置（或再次明确关闭理由）。**

---

## 5. 测试基建盘点

均在 `tests/WinForward.Core.Tests/`（单测试工程）：

| Fake/基建 | 位置 | 能力 | 缺口 |
|---|---|---|---|
| `FakeInjector` | TcpProxyCoordinatorTests.cs:1550-1562 | 记录 (frame, towardMstcp, adapterHandle)；throwOnCall 第 N 次抛 | 不校验句柄语义，无失效模拟 |
| `CountingReinjector` | 同上:1618-1626 | 计数 SendToAdapter/Mstcp | 同上 |
| `RecordingRedirectReinjector` | TcpRedirectInjectorTests.cs:48-62 | 记录注入方向 | — |
| `FakeListenerFactory`/`FakeListener`（Channel 驱动 accept） | 同上:1419-1514 | 可编程 accept 时序、fixedTuple | — |
| `FakeAcceptedConnection`/`FakeRelay`/`FakeRelayFactory`(throwOnEstablish)/`GatedRelayFactory`/`CompletableRelayFactory` | 同上:1516-1548, 1578-1595, 1644-1653 | 控制建立成功/失败/门控时序 | **无"先发数据再失败"的组合场景**（D1 验收需要） |
| `RecordingRuntimeLogger` | 同上:1564-1576 | 记录事件/级别 | — |
| SOCKS5 服务端 stub | Socks5ControlConnectionTests.cs 内静态 helper（`StallAfterGreetingAsync`:403、`AcceptGreetingAsync`:413、`StallAfterUdpAssociateRequestAsync`:422、`ServeUdpAssociateAsync`:435、`ServeAdvertisedAssociateAsync`:479） | 真 loopback TcpListener 手写最小 SOCKS5 应答；配合 `resolveAddresses`/`socketFactory` 注入 seam | **无可复用的 CONNECT 场景 stub 类**（均为私有 static 方法散落在该文件），无"慢 connect"计时断言型测试 |
| `WindowsAdapterInventory` 委托 seam | AdapterIdentity.cs:24-25（ndisAdapters/ipAdapters 两个 Func） | 无 Windows 可构造任意适配器快照 | **无任何测试把它接进 coordinator 做句柄重解析**（D2 需新建 fake + 接线） |
| `NdisCapturePumpTests` | tests/.../NdisCapturePumpTests.cs | 批读保序/部分批/空批/释放（fake `INdisPacketReader`） | D4 若改轮询需补事件化用例 |
| `TcpResetBuilderTests` | 3 个用例（IPv4/IPv6/模板拒绝），静态 seq/ack | D1 若加 RFC 5961 变体需新增用例 |

---

## 6. 改动面预估

| 项 | 文件 | 说明 |
|---|---|---|
| **D1** | `src/WinForward.Runtime/TcpProxyCoordinator.cs` | `ReinjectExistingFlowDataAsync` 加跟踪写入；`TryInjectClientResetAsync` 改 ack 来源 |
| | `src/WinForward.Runtime/TcpRedirectTable.cs` | `TcpRedirectAssociation` 新字段（如 `ClientNextSeqHighWater : uint?`） |
| | `src/WinForward.Protocols/TcpResetBuilder.cs` | 仅当选 RFC 5961 变体时加构造 |
| | `tests/.../TcpProxyCoordinatorTests.cs`（+ `TcpResetBuilderTests.cs` 若动 builder） | "发数据后失败"场景用例 |
| **D2** | `src/WinForward.Runtime/TcpProxyCoordinator.cs` | `HandleReverseAsync` 句柄校验/重解析 + 显式拆除路径 + reason 日志；构造函数新依赖 |
| | `src/WinForward.Cli/Program.cs` | 组装点接线（inventory 或探针） |
| | （视方案）`src/WinForward.Windows/AdapterIdentity.cs` / `src/WinForward.NdisApi/NdisApiDriver.cs` | 探针或重枚举 API |
| | tests：inventory fake + 失效场景用例（新建） | |
| **D3** | `src/WinForward.Runtime/TcpProxyRelay.cs` | `EstablishAsync` 传预算参数（最小改动落点） |
| | `src/WinForward.Runtime/Socks5Client.cs` | 仅当预算结构重构（总预算/分级）时动 |
| | `src/WinForward.Runtime/TcpProxyCoordinator.cs` | 仅当采用"connect 前置"方案 |
| | tests：Socks5ControlConnectionTests / 新增 relay 失败计时用例 | |
| **D4** | 结论记录（或 `NdisCapture.cs` 轮询改造，若自研） | |

---

## 7. 风险与边界（与兄弟任务交互）

- **`TcpProxyCoordinator.cs` 是 D1/D2/D3(部分) 三项共同热点文件**（本文件 1073 行），建议实现顺序错开或合并 review。
- **墓碑表（fix-table-lifecycle，已落地）**：`RemoveAssociationFromTable`（Coordinator:1017-1021）是全部拆除路径的单一墓碑写点。D1 的 RST 注入紧邻 `TearDownSessionAsync`；D2 的"显式拆除+RST"也会汇入此处——**必须保持单写点语义**，别在显式拆除里另起 `TryRemove`。
- **端口预算（fix-port-budget，已落地 archive）**：改了 `TcpProxyCoordinator` 容量门（Coordinator:127-135）、`LogCapacitySummary`、`ConfigurationModels`/`Program` 接线。D3 若"connect 前置到 accept"，listener 占用时长变化直接影响容量占用时长语义；`TcpRedirectListenerFactory`/`Program.cs` 是重叠文件。
- **批量读（fix-datapath-throughput，已落地）**：lease 归还在 `ProcessAsync` 外层 finally，帧读取必须留在 `ProcessAsync` await 窗口内——D1 在 `ReinjectExistingFlowDataAsync` 里读 seq 天然满足（注入前读完），无冲突，但勿引入异步滞留帧引用。
- **PRD 行号漂移**：PRD 引用的 `TryInjectClientResetAsync` 629-649 / claim 410-416 均为旧 HEAD；当前为 732-752 / 469-477，design.md 应以本文件行号为准。

## Caveats / Not Found

- 未发现任何运行期适配器重枚举/句柄刷新代码路径（`SetAdapterListChangeEvent` 无 ABI 绑定）；D2 的"复用驱动枚举"需要新 seam，属新改动而非既有能力接线。
- 没有现成的"客户端已发数据后 relay 失败"端到端测试；现有 RST 相关测试只覆盖 SYN 时刻序号。
- `Win32Exception` 失败时 association teardown 对客户端的实际可见行为（FIN vs RST，取决于 accepted socket 缓冲状态）未在代码中显式处理，属 Socket.Dispose 默认行为，未做实验验证。
