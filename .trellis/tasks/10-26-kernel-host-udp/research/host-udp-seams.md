# Research: host UDP 的接缝、方案对比与第一批范围

- **Query**: 复用 `UdpProxyCoordinator` 还是写 host 专属协调器；需要哪些接缝/重构；最小可验证批次；是否需要 ABI 变更；connected-UDP 泄漏如何处置；真实 Windows UDP E2E 矩阵
- **Scope**: 内部（源码 + live 规范 + 归档验证记录），决策分析
- **Date**: 2026-10-26

---

## 0. 推荐（一句话）

走**方案 A**：把 host UDP 的**入口**做成 `UdpProxyCoordinator` 的 WFP listener 适配器，复用会话/容量/空闲/传输；**回包出口不能沿用 listener `sendto`**：A4 实测客户端看到的是 listener 源地址，必须由驱动侧注入或改写源地址（`host-udp-reply-source` 专项批次），该批次需要新的控制 IOCTL/ABI 版本。第一批 baseline 的其它部分（入口、SOCKS5、pass/block/容量/空闲）已在工作树实现，但在回包源修复前不得提交/验收；local target/DNS 与 UoT 仍留在后续子批。

---

## 1. 方案 A：复用协调器，新增 host 入口/出口适配器

### 1.1 形态

```
Kernel driver (ALE_CONNECT_REDIRECT, per UDP association)
        │  datagram 到 (clientLocal, listener_port)
        ▼
KernelHostUdpService (CLI)                        ← 新增
  ├─ 每个 listener UDP socket 一个接收循环
  │    WSARecvMsg → payload + 源端点 + IP_WFP_REDIRECT_CONTEXT(36B)
  ├─ FlowKey.Create(client, destination, Udp, Host)   // NoSlot, generation 0
  ├─ rule_index → ProxyTarget（复用 KernelPolicyCompiler 的表）
  └─ coordinator.TrySendSpanAsync(flow, target, payloadSpan, MacAddress.Invalid, ct)
        ▼
UdpProxyCoordinator（原样复用：容量/setup 队列/冷却/两档空闲/teardown/skip 分类）
        ▼
Socks5UdpTransport / Socks5UotTransport / LocalUdpTransport（原样复用）
        ▼ 回包
HostUdpResponseSink : IUdpResponseSink            ← 新增
  └─ listenerSocket.SendTo(payload, Flow.Local)   // 收到 datagram 的那个 socket
```

### 1.2 需要的接缝（每一处都很小）

| # | 接缝 | 位置 | 建议形状 | 为什么必须 |
|---|---|---|---|---|
| S1 | UDP cmsg 接收器 | 新文件，建议 `src/WinForward.Cli/Backends/Kernel/` 或 `WinForward.Kernel.Runtime/Relay/` | 「纯解析函数（`byte[] control, int length → context/level`）」+「Windows 专属阻塞接收器（`WSARecvMsg` P/Invoke）」两半 | 逐 datagram 的原始目的地只有 cmsg 能拿到（`UdpSockets.cs:70-116`）；纯解析函数可以在 Linux 单测里跑，与 `wfprobe` 的 `FindControl` 同构 |
| S2 | host 回包 sink | `src/WinForward.Cli/Backends/Kernel/HostUdpResponseSink.cs`（临时） | 实现 `IUdpResponseSink`（`UdpResponseReinjector.cs:16-19`）；当前为 listener `sendto`，A4 已证明源地址错误，**必须替换为驱动注入/源改写 sink** | 回包源地址是产品语义；同 socket `sendto` 只把源地址变成 listener。raw socket 伪造源在 Windows 不可行；需要新的驱动 IOCTL/ABI（`host-udp-reply-source`） |
| S3 | 协调器构造去掉 `IPacketReinjector` 依赖 | `UdpProxyComposer.cs:65-86` | 拆成 `Create(transportFactory, IUdpResponseSink sink, composition)`；保留原重载委托给它 | 否则 CLI 只能自己抄一遍工厂拼装 |
| S4 | host 侧的 sweep 驱动 | `KernelHostUdpService` | `PeriodicTimer(IdleExpirySweeper.DeriveUdpSweepInterval(...))` + `coordinator.RemoveExpiredAsync(now, idle, OneShotIdleTimeout)` | `IdleExpirySweeper` 强制要求 `FlowDispatcher`（`IdleExpirySweeper.cs:47-62`），host 没有也不该造一个 |
| S5 | host 的帧上限与池尺寸 | CLI 组合 | 新常量（见 §5）+ `NativeBufferPool(ReceiveWindowSize(cap), ReceiveWindowPoolCapacity(capacity))` | 1514 会丢掉大 DNS 响应（`host-udp-userland.md` §5） |
| S6 | listener UDP socket 形态 | `ListenerSet.cs:121-182`（或 host 专属绑定） | 单个 dual-mode v6 通配 socket（与 `UdpSuite.cs:1203` 一致），bind 前开 `IP_WFP_REDIRECT_CONTEXT` | v4-mapped **connected** 关联在 V6 层被改道到 `::ffff:<local>`，`V6ONLY=1` 的 socket 按 Winsock 语义收不到（现有绑定见 `ListenerSet.cs:83`、`:142`；`host-udp-driver.md` §5.1，**待实测**） |
| S7 | listener 侧 `SIO_UDP_CONNRESET` | 同上 | 在 bind 前关掉（复用 `LocalUdpTransport` 的常量 `0x9800000C`，`LocalUdpTransport.cs:82-88`） | listener 也会 `sendto` 回包；客户端端口已消失时 ICMP 会让下一次 `WSARecvMsg` 以 `WSAECONNRESET` 失败，不能因此终止接收循环 |
| S8 | 配置闸 + 编译期放行 | `ConfigurationKernelCapabilities.cs:52-55`、`KernelPolicyCompiler.cs:366-372` | 删除 UDP-proxy 拒绝；local target 的接受与否取决于批次（见 §4） | 当前任何「proxy + 未限定 tcp」的规则都会被拒 |

### 1.3 正确性收益

- **去重**：容量、setup 队列（32 包/32 KiB + 全局预算）、失败冷却、两档空闲、TTL、skip 分类、`udpResponseSourceMismatch`、UoT、关联丢失语义全部只有一份实现，两个 edition 的行为按构造一致。
- **风险集中**：`UdpProxySession`/`UdpProxyCoordinator` 已有完整单测与基准（`tests/WinForward.Runtime.UdpProxy.Tests/`、`benchmarks/WinForward.Benchmarks/Perf/UdpSessionBenchmarks.cs`），host 侧新增的代码只是「读 cmsg + 组 key + sendto」。
- **内存**：`NativeBufferPool` 是 `WinForward.Core` 的原生缓冲池（`src/WinForward.Core/NativeBufferPool.cs`），不是 NDISAPI 句柄；`MacAddress.Invalid` 只是随值传递的字段，host sink 忽略。方案 A **不需要**触碰 MAC/adapter/`NativeLease` 假设。

### 1.4 风险

- 改到 `UdpProxyComposer` 与 `ListenerSet` 就会碰到包路径与 forward 路径的共用文件；缓解：S3 只做「重载 + 委托」，S6 只对 host 新增绑定模式，不改变既有调用点的行为。
- 一旦 host 侧需要「同 socket 回包」以外的语义（例如将来把 forwarded 也接进同一协调器），`IUdpResponseSink` 的 `MacAddress` 参数会显得多余——但为它改造接口不值得，host sink 忽略即可。

---

## 2. 方案 B：host 专属协调器/会话，只复用 transport

能做，但会重复以下逻辑（全部有既有实现与既有测试）：

| 必须重写的部分 | 现成实现 | 重写代价 |
|---|---|---|
| setup 队列 + 全局字节预算 + TTL + 打戳 | `UdpProxyCoordinator.cs:276-354`、`UdpSessionSetup.cs:82-139`、`216-246`、`UdpSetupQueueBudget.cs` | 约 500 行 + 一族边界测试 |
| 失败冷却 / teardown 原因分类 / 关联丢失 | `UdpProxyCoordinator.cs:496-527`、`Send.cs:214-220`、`UdpSetupCooldownTable.cs` | 语义微妙（哪些原因写冷却） |
| 两档空闲 + 活动桶 | `UdpProxySession.cs:267-303`、`530-543`、`ActivityBucket.cs` | 500 ms 量化、一次性交换判定 |
| skip 分类 + 回包源核对 + 限速日志 | `UdpProxySession.cs:392-528` | 观测面 |
| 容量/拒绝计数 | `Send.cs:70-93` | 与包版同名计数器 |

结论：方案 B 会造出**第二套代理实现**，这正是仓库 `kernel/AGENTS.md` 明确禁止的（「否则会出现第二套代理实现」）。它的唯一好处是把改动隔离在 `Kernel/` 目录内，代价是两个 edition 的 UDP 语义从第一天起就可能漂移。

明确一点：方案 B **不能**简化回程。`IUdpResponseSink` 的签名只有一个方法、host 实现只有一句 `SendTo`，所以「host 专属会话」省不下任何回程复杂度。

---

## 3. 对比

| 维度 | 方案 A（复用协调器） | 方案 B（host 专属协调器） |
|---|---|---|
| 正确性 / edition parity | 按构造一致（同一份会话与传输） | 需要人工对齐；两套 teardown/空闲/skip 语义 |
| 去重 | 5 处小接缝，零重复 | 约 1000+ 行重复 + 双份测试 |
| 改动面 | `UdpProxyComposer`、`ListenerSet`（或 host 专属绑定）、`KernelPolicyCompiler`、`KernelHostTcpService`、配置闸、新增 2–3 个文件 | 只在 `Kernel/` 内新增（约 1500 行），不碰包路径 |
| 包路径回归风险 | 低（S3 是重载委托，S6 是新增绑定模式；`ListenerSet`/`UdpProxyComposer` 的既有调用点行为不变） | 零 |
| ABI 影响 | 无 | 无 |
| 首批规模 | 约 600–900 行产品代码 + 测试 + 1 份 Windows E2E 记录 | 约 1500–2000 行，且之后每次修 UDP bug 要改两遍 |
| 长期 | host/forwarded 都能落在同一协调器上 | 三条路径三套实现 |

**推荐方案 A。**

---

## 4. 推荐的第一批范围

目标：一条**真机上可复现**的最小闭环，能回答「host UDP 到底通不通、语义对不对」。

### 4.1 在批内

1. 配置闸放开：host proxy 规则允许 `Protocol` 含 `udp` 或未限定（`ConfigurationKernelCapabilities.cs:52-55` 删除）。local target 的处理见 4.2。
2. 编译期：`KernelPolicyCompiler` 的 target 表可以携带 local target（若 4.2 推迟，则保留拒绝但改写文案，不要出现「没有 host UDP 中继」这种已经过期的说法）。
3. `KernelHostUdpService`：listener UDP 接收循环（S1/S6/S7）、FlowKey 组键、rule_index → target、**按 4096 上限在 admission 前长度检查**、逐 datagram 异常隔离（超长/transport 拒绝不能拆掉会话或杀死接收循环）、`TrySendSpanAsync`、未命中/无 context/解析失败的 fail-closed 丢弃与计数。
4. `HostUdpResponseSink`（S2）：当前同 socket `sendto` 到 `Flow.Local`，A4 已失败；待 `host-udp-reply-source` 替换为驱动注入/源改写，baseline 在该修复前不提交。
5. 组合：S3 重载 + S4 sweep + S5 帧上限与池；`UdpSessionCapacity` / `UdpSessionIdleSeconds` / `UdpRelayReceiveBufferKb` 三个目前「inert」的键真正生效（`ConfigurationKernelCapabilities.cs:90-111` 的警告随之删除）。
6. 观测：`kernel.ready` 能表达 UDP 是否启用；新增 UDP 侧的接受/丢弃/拒绝事件；沿用 `RuntimeCounters` 的既有键。
7. 单元测试：cmsg 纯解析函数（含 level 0/41、36 字节、坏长度）、FlowKey 组键、无 context 丢弃、sink 的目标端点选择。
8. Windows E2E：§6 的 A1–A5。

### 4.2 明确不在批内

- local target / DNS 形态（A6）与 UoT（A7）：数据面已经存在，缺的是配置放行与一条 E2E，作为同一任务的后续子批。
- 规则热替换（后端目前没有 reload 路径）、heartbeat 段、IPv6 完整矩阵（只做 A8 的最小样本）、connected 关联的释放断言。
- forwarded 域的任何改动。

### 4.3 为什么这个批次是「最小可验证」

它同时覆盖了三条互相独立的风险线：**驱动交付**（A1/A2/A3 的 `udp_redirected`/`udp_permitted`/`udp_blocked` 增量）、**代理链路**（A1 的 sing-box relay 证据）、**产品语义**（A4 的回包源地址 + A5 的容量/空闲）。少了任意一条，就有一条风险只能靠「代码看起来对」来相信。

---

## 5. 必须显式决定的数值：host 的帧上限

包路径把 payload 上限定在 1472（`UdpFrameBuilder.cs:15` = 1514 − 42）。host UDP 的 datagram 上限是 65507 字节，接收窗口 = 帧上限 + 23（`UdpProxyCoordinator.cs:217-218`），池容量 = 会话数 + 余量（`UdpProxyCoordinator.cs:233-257`）。

| 选项 | 覆盖能力 | 每会话窗口 | 4096 会话稳态上限 |
|---|---|---|---|
| 1514（沿用） | 只覆盖 ≤1472 的 datagram；**EDNS0 DNS 响应、QUIC 大包、任何 >1472 的报文直接 `Oversized` 丢弃** | 1.5 KiB | 6 MiB |
| 4096 | 覆盖常见 DNS/EDNS0（1232–4096）与多数游戏/QUIC 探测 | 4 KiB | 16 MiB |
| 65507（推荐） | 覆盖 IPv4 UDP 全部合法 payload | 64 KiB | 256 MiB |

推荐 65507（或 65535 的整数口径）：`NativeBufferPool` 按需分配，稳态只与并发会话数相关；256 MiB 是「4096 会话全部活跃」的最坏值。如果这个内存上限不可接受，则结论变成「4096 并降低默认 `UdpSessionCapacity`」，两者必须一起定（见 §9 的 D1）。

---

## 6. 真实 Windows UDP E2E 矩阵

环境沿用已归档的 Kernel host TCP E2E 做法（`.trellis/tasks/archive/2026-10/10-26-windows-kernel-e2e/design.md:1-27`、`verification/verification.md:1-40`）：

- WinLTSC 26100，`wfprobe` 驱动服务（ABI 8）已装并运行，测试签名开启。
- 产品 CLI：`win-x64` publish 到 `C:\wfbench\kernel-e2e\wf-kernel-cli`，以**计划任务（SYSTEM）**启动以跨过 WinRM 会话生命周期。
- 配置：`Provider = WinForward.Kernel`，一个 SOCKS5 target `main → 127.0.0.1:1080`，fallback pass。
- 代理：sing-box 1.14.2-singfix，`base-singbox-socks5.json`（`benchmarks/WinForward.E2E/scripts/singbox/base-singbox-socks5.json`）。
- 靶机（Linux 192.168.100.4）：`benchmarks/WinForward.E2E` 的 `target` 角色，自带 UDP echo 与 DNS 应答器（`Target/TargetRunner.cs:24-27`、`--udp-port` / `--dns-port`），端口用 `40010`（echo）、`40011`（直连对照）、`53`/`40053`（DNS）。
- 客户端：unconnected `UdpClient`（必须**打印并断言** `ReceiveFrom` 的 `RemoteEndPoint`）。

| # | 臂 | 规则 / 配置 | 必须断言 |
|---|---|---|---|
| **A1** | 代理（SOCKS5 UDP ASSOCIATE） | `Protocol:["udp"], RemotePort:["40010"] → proxy main` | 收到回包；sing-box 日志出现到 `192.168.100.4:40010` 的中继记录（UDP 的中继日志措辞与 TCP 的 `inbound connection` 不同，判据按实际输出取，确认后写进 verification）；驱动 `udp_redirected +1`、`udp_classify_calls +1`（**关联数**，不是 datagram 数） |
| **A2** | pass | `RemotePort:["40011"] → pass` | 靶机直接收到；sing-box 无该流；`udp_permitted` 增长、`udp_redirected` 不变 |
| **A3** | block | `RemotePort:["40013"] → block` | `send` 返回成功、`ReceiveFrom` 超时、靶机 0 条；`udp_blocked +1`、`deferred_blocks +1`；在 host 服务停止行记录 `BlockSink` 的接收计数为 0（当前没有实时日志，需要补上） |
| **A4** | 回包源地址（产品语义） | 用 A1 的同一条流 | **已实测失败**：unconnected 客户端看到 `FROM=192.168.100.2:<listener>`，不是 `192.168.100.4:40010`。该断言保留为 `host-udp-reply-source` 的验收门槛；驱动注入/源改写通过前 baseline 不提交 |
| **A5** | 容量 + 空闲 | `UdpSessionCapacity=1`；`UdpSessionIdleSeconds=5` | 第一条流占满后第二条无回包（fail-closed，不排队）；空闲到期后用**新的客户端源端口**再发，重新建链并重新 classify（`udp_classify_calls` 再 +1）；同一 socket/目的地不会再次 classify；`udpCapacityRejections ≥ 1` |
| A6 | local target / DNS 形态（子批） | `LocalTargets: dns = 192.168.100.4:53`；`Protocol:["udp"], RemotePort:["53"] → proxy dns` | DNS 应答原样回传（含 >512 字节的 EDNS0 应答，验证 §5 的帧上限）；`from=` 等于 `192.168.100.4:53`；一次性交换 5 秒内回收 |
| A7 | UoT v2（子批） | `UdpOverTcp: true` | 同 A1 的回包 + sing-box 日志出现 UoT connect（`orchestrator.ps1:579-592` 的判据可复用） |
| A8 | dual-mode 监听（最小样本） | 一个 native v6 目标（`[::1]` 或 v6 靶机）+ 一个 v4-mapped **connected** 客户端 | 两者都收到回包（验证 §1.2 的 S6） |
| A9 | 生命周期 | 一条 connected + 一条 unconnected | unconnected 关闭后 `WfPr` outstanding 回基线；connected **只记录采样**，不做释放断言（`connected-context.md:57-66`） |
| A10 | 负向 | 直接对本机 listener 端口 `sendto` 一个没有 redirect context 的 datagram | 被丢弃、不建会话、不崩、有计数（listener 端口对任何本地进程可达） |

驱动侧回归不需要新工作：`gate.ps1:112-114` 已经在跑 `udp-rules`（U1–U13），覆盖 redirect/permit/block/any/进程选择器/IPv6/生命周期/热替换。

---

## 7. ABI / context 变更

**不需要。**

- `probe_config`（`probe_ioctl.h:269-280`）已经有 `listener_port`、`block_sink_port`、`self_process_ids`、`listener_process_id`，两者共用；host UDP 不需要按协议分开的端口或 PID。
- `redirect_context` v1（`redirect_context.h:36-57`）已经带 `protocol=17`、`family`、`original_address/port`、`rule_index`；C# 镜像 `RedirectDestination.TryParse` 已经接受 UDP（`src/WinForward.Kernel.Runtime/Relay/RedirectDestination.cs:81-85`、`172`）。
- 控制面 ABI 保持 v8（`probe_ioctl.h:16`）。不需要新的 IOCTL、不需要 `probe_forwarded_*` 的任何字段。
- 唯一与驱动有关的待确认项是 §1.2 的 S6（监听 socket 形态），那是用户态选择，不是 ABI。

---

## 8. connected-UDP 泄漏：接受还是缓解

事实（`.trellis/spec/backend/kernel/wfp-connected-udp-record-refcount-leak.md`）：

- 36B `WfPr` + 0x228 `Arcl` 每条 connected 关联一份，`sc stop` 不回收，只有重启清零（`:17-40`）。
- refcount 随 datagram 线性增长，2^32 后理论 UAF；100k pps 约 11.9 小时（`:39`）。
- 驱动侧**没有**安全释放入口；手动 `ExFreePool` 会 double free/UAF（`:207-215`）。

可选处置：

| 选项 | 代价 | 评价 |
|---|---|---|
| 接受 + 文档 + 监控 `WfPr`/`Arcl` 累计量 | 每次重启前累积；长期不重启的机器需要维护窗口 | **推荐**：功能无损，风险可控，已有池标签监控手段（`wfprobe pooltag`） |
| 让 connected UDP 不进 redirect（改用 block + 用户态接管） | 失去 listener 侧取原始目的地的能力，等于放弃 connected UDP 代理 | 不可接受：产品会静默不支持一大批应用（很多 DNS/游戏客户端用 `connect`） |
| 在驱动里伪造释放 | double free / UAF | 禁止 |
| 定期重启 / 由 install/升级流程强制重启 | 运维成本 | 作为「接受」策略的一部分写进运维文档 |

建议的产品口径：**接受**，并在 `kernel.*` 观测里给出 `WfPr`/`Arcl` 的累计增长率（只读诊断），在 README/运维文档里写清「connected UDP 关联的 context 在重启前不回收」。是否要额外向 Microsoft 报 case 属于对外事务，不影响实现。

---

## 9. 真正需要用户拍板的产品决策

| # | 决策 | 选项 | 建议 |
|---|---|---|---|
| D1 | host UDP 的最大 datagram 上限（连带每会话内存预算） | 1514 / 4096 / 65507 | 65507；若不接受 256 MiB 最坏值，则 4096 + 下调默认 `UdpSessionCapacity`。**必须与 `UdpSessionCapacity` 默认值一起定**，否则「默认配置能用多少内存」说不清 |
| D2 | connected UDP 泄漏的处置口径 | 接受 / 其他 | 接受 + 监控 + 运维文档（§8） |
| D3 | 第一批是否包含 local target / DNS | 包含 / 推迟到子批 | 数据面已就绪，只缺配置放行与一条 E2E；若 D1 已定，建议**包含**，因为「DNS 代理」是 host UDP 最主要的真实用途 |
| D4 | 是否在第一批就把 `kernel.*` 的 UDP 观测（心跳/计数段）对齐包版 | 是 / 否 | 先做「就绪行 + 拒绝/丢弃事件 + 既有 `RuntimeCounters` 键」，完整心跳段留给观测子批 |

除 D1/D2 外，其余都可以由实现者在批次内自行取舍——它们不影响对外语义。

---

## 10. 风险登记

| # | 风险 | 触发条件 | 缓解 |
|---|---|---|---|
| R1 | v4-mapped **connected** 客户端收不到回包 | `V6ONLY=1` 的 v6 listener 不接 v4-mapped（§1.2 S6） | dual-mode v6 通配 listener；E2E A8 必须先跑 |
| R2 | 大 datagram 静默丢弃 | 帧上限沿用 1514 | D1 定值；`Oversized` skip 摘要有计数可查（`UdpProxySession.cs:452-474`） |
| R3 | 回包源地址不是原始目的地 | WFP 语义与套件断言都没有覆盖 | **已发生（阻塞性）**：A4 实测源地址是 listener；raw 伪造源不可行。出口必须是驱动注入/源改写，见 `host-udp-reply-source` |
| R4 | listener 接收循环被 ICMP 打断 | 客户端端口消失后 `WSARecvMsg` 报 `WSAECONNRESET` | S7 关掉 `SIO_UDP_CONNRESET`，并把 `WSAECONNRESET` 当 skip |
| R5 | 阻塞式 `WSARecvMsg` 的线程与取消 | 无 IOCP 集成 | 每 socket 一个专属线程，取消=关闭 socket；接收循环纳入服务的 scope 并在 listener 释放前排空 |
| R6 | listener 端口成为公开入口 | 任何本地进程都能向它发包 | 无 context 的 datagram 一律丢弃 + 计数（A10）；不为它们建会话 |
| R7 | 规则表与 target 表漂移 | local target 进入表，TCP 路径却能取到 | `TryResolveTarget` 的 TCP 侧 local 拒绝保留为纵深防御（`KernelHostTcpService.cs:159-163`） |
| R8 | 两个 edition 的 UDP 语义漂移 | 选方案 B | 选方案 A |
| R9 | 热替换后旧关联仍走旧规则 | 驱动既有行为（U13） | 不需要处理，写进预期差异表 |
| R10 | 启动顺序回归 | UDP 接收循环先于驱动 enable 启动，或晚于 disable 停止 | 沿用 `TryStart`/`DisposeAsync` 的既有顺序（`KernelHostTcpService.cs:107-136`、`238-284`），UDP 循环与 TCP accept 循环同属一个 scope |
