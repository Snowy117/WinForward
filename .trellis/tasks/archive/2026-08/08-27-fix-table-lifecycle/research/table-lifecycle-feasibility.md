# Research: 重定向表与流表生命周期修复可行性

- **Query**: teardown 时序 / 迟到包路径 / flow↔redirect 关联 / 宽限条目形态 / sweep 结构 / 启动时间戳 / UDP 边界
- **Scope**: internal（代码库勘察）
- **Date**: 2026-08-28
- **基线**: HEAD = `c1f59a0`。所有行号以此为准；PRD 中 TcpProxyCoordinator.cs 旧行号（774-790/461/491/539-547）已因 datapath/port-budget 改动全部偏移，本文一律给新行号。

---

## 1. teardown 序列：relay completion → 表移除的精确调用链

### 调用链（当前 HEAD 行号）

1. **观察者挂接**：accept loop 建立 relay 后 fire-and-forget
   `_ = ObserveRelayCompletionAsync(session, relay, token)` — `src/WinForward.Runtime/TcpProxyCoordinator.cs:766`
2. **等待 relay 完成**：`ObserveRelayCompletionAsync` await `relay.Completion`（`TcpProxyCoordinator.cs:833-849`，await 在 837，teardown 调用在 848）。
   - shutdown token 的 OCE 直接 return 不 teardown（839-842，dispose 路径负责）；正常结束与异常结束都走 teardown（843-848）。
3. **Completion 的语义**：`TcpProxyRelay.RunPumpAsync`（`src/WinForward.Runtime/TcpProxyRelay.cs:77-110`）在双向 pump 均结束后完成：
   - 一方向 EOF（`read == 0`，`TcpProxyRelay.cs:132-135`）→ 对另一端 `ShutdownSend`（95-96）→ `Task.WhenAll`（98）；
   - 或任一方向 Stalled（30 分钟 StallTimeout，`TcpProxyRelay.cs:58`）→ 取消（88-92）；
   - 或异常 → faulted Completion（104-109）。
4. **retire**：`TearDownSessionAsync`（`TcpProxyCoordinator.cs:851-859`）→ `TryRetireSessionUnderGate`（861-865，校验 `_sessions` 中当前实例未被替换）→ `RetireSessionUnderGate`（867-875）：`_sessions.Remove`（869）、`Phase = RelayPhase.Closing`（870）、`Retire()` 取消 lifetime token（871）、摘下 Relay 引用（872-873）。
5. **释放**：`ReleaseRetiredSessionAsync`（877-895）→ `ReleaseAssociationAsync`（883）→ **`_table.TryRemove(association)`（`TcpProxyCoordinator.cs:942`）**，随后 listener dispose（945）、self-traffic token 释放（950）、relay dispose（889-893，内部 `_localSocket.Dispose()` — `TcpProxyRelay.cs:185-190`）。
6. **三索引移除**：`TcpRedirectTable.TryRemove`（`src/WinForward.Runtime/TcpRedirectTable.cs:181-191`）——
   `_byOriginal.Remove`（186）、`_byTranslatedListener.Remove`（187）、`_byReverse.Remove`（188），单锁原子。

### 其他 teardown 入口（同样收敛到 TryRemove）

- relay 建立失败：`HandleRelaySetupFailureAsync`（`TcpProxyCoordinator.cs:714-720`）。
- reverse/forward 处理失败：`FailAssociationAsync`（908-914，913 直接 `_table.TryRemove`）。
- 全局关闭：`DisposeCoreAsync`（651-677）同样经 `ReleaseRetiredSessionAsync`。
- 即：**`TcpRedirectTable.TryRemove`（或 `ReleaseAssociationAsync`:942）是所有 teardown 路径的单一汇聚点**，宽限/墓碑逻辑挂这里即可全覆盖。

### 并发形态与时间关系

- 观察者在 .NET 线程池 fire-and-forget，与 capture 泵（`HandlePacketAsync` / `HandleReverseAsync`）完全并发；表操作在 `TcpRedirectTable._gate` 锁内与查表互斥，无 torn state。竞态窗口 = `TryRemove` 返回之后到达的每个同 tuple 包。
- **表移除先于客户端最后 ACK/FIN**（结构上必然）：relay `Completion` 由本地 accepted socket 双向 EOF 驱动；客户端侧挥手的收尾（本地栈 FIN-ACK 之后的客户端最后 ACK、TIME_WAIT 期间的 FIN 重传）发生在 socket 关闭之后，而表移除与 socket 关闭在同一个 teardown 序列里几乎同时完成。典型时序（客户端主动关）：客户端 FIN → 本地 pump EOF → ShutdownSend(上游) → 上游 FIN → WhenAll 完成 → 表三索引移除 → listener/socket dispose → **客户端最后 ACK 才到达 capture 层** → 查表 miss。
- smoke 复核：`smoke/winforward-service_2026-08-27-new.err.log` 中 `outcome=notrelevant` 113 次（与 PRD 一致，已重新计数确认）。

## 2. 迟到包路径：NotRelevant → Pass 的精确分支

前置事实：迟到包所属 flow 在 flow 表中有 proxy 决策（决策在建流时已作出，`FlowDispatcher.DispatchAsync` — `src/WinForward.Runtime/FlowDispatcher.cs:86-103` 命中后 `ExecuteDecisionAsync`:187-205 走 `ProxyAsync`:198-199）。

### forward 方向（客户端 → 真实服务器 tuple 的 ACK/FIN 重传）

1. `DispatchAsync` flow 查表命中（`FlowDispatcher.cs:86`，transport index 双向解析 `Domain.cs:226-236` 保证方向无关）→ `ExecuteDecisionAsync` → `NdisPacketActionExecutor.ProxyAsync`（`src/WinForward.Runtime/NdisPacketActionExecutor.cs:56`）。
2. `TcpProxyCoordinator.HandlePacketAsync`（`TcpProxyCoordinator.cs:525-556`）：
   - `IsReverseCandidate` miss（535）；
   - `ClassifyTcpSyn` 返回 None（533，迟到 ACK/FIN 非 SYN）；
   - **`TryResolveByOriginal` miss（550）→ 555 返回 `NotRelevant`**。
3. executor 兜底：**`outcome == NotRelevant → PassAsync`（`NdisPacketActionExecutor.cs:74-81`）**，注释（76-79）明示该兜底为"capture 启动前已建立的连接"设计。
4. `PassAsync`（37-47）以捕获方向原样 reinject（43-44）→ 帧以**原始目的地**进入真实网络 → 真实服务器对未知 tuple 回 RST → RST 被 pass 回客户端。

### reverse 方向（本地栈 → 客户端，源端口 = listener port）

1. `DispatchAsync:84` → `TryHandleReverseAsync`（`FlowDispatcher.cs:136-147`，139 调 reverseHandler）。
2. `HandleReverseIfApplicableAsync`（`TcpProxyCoordinator.cs:510-523`）：**`IsReverseCandidate` miss（520）→ `NotRelevant`**（`HandleReverseAsync` 内的 `TryResolveByReverse` miss 在 445，为第二道，正常时序下 520 已 miss）。
3. `TryHandleReverseAsync` 对 NotRelevant 返回 false（`FlowDispatcher.cs:140`）→ 回到 flow 查表（86）→ 同 forward 路径汇入 executor `ProxyAsync` → `HandlePacketAsync` 550 miss → 555 `NotRelevant` → `NdisPacketActionExecutor.cs:74-81` Pass。

**结论**：两个方向的迟到包都汇聚到 `NdisPacketActionExecutor.cs:74-81` 这一个兜底分支；收紧点唯一、清晰。

## 3. flow ↔ redirect 关联与 relaying 豁免挂点

### 现成关联键（两套，都已存在）

| 关联键 | flow 侧 | redirect 侧 |
|---|---|---|
| `FlowKey`（值相等 record struct，`Domain.cs:69-85`） | `FlowTable._states` 键（`Domain.cs:120`） | `TcpRedirectTable._byOriginal` 键（`TcpRedirectTable.cs:84`）、coordinator `_sessions` 键（`TcpProxyCoordinator.cs:29,309`） |
| `Generation`（long） | `FlowState.Generation`（`Domain.cs:112`） | `TcpRedirectSession.FlowGeneration`（`TcpProxyCoordinator.cs:989`，由 `RegisterSession`:235 从 `packet.FlowGeneration` 传入；源头在 `FlowDispatcher.cs:88/115`） |

即 coordinator 的 `_sessions` 字典可以按 `FlowKey` 精确查询一条 flow 是否 relaying。注意 FlowKey 含 `Origin/OriginAdapterId/OriginAdapterGeneration`，同一 tuple 稳定；adapter 重建（generation 变化）时匹配会退化（罕见，可接受）。

### idle 判定现状

- `IdleExpirySweeper` tick → `FlowDispatcher.RemoveExpiredFlows`（`FlowDispatcher.cs:71`）→ `FlowTable.RemoveExpired`（`Domain.cs:212-224`）：216 行 `now - LastActivityUtc >= idleTimeout` **无差别删除，无任何豁免**。
- 对照：`TcpProxyCoordinator.RemoveExpiredAsync`（`TcpProxyCoordinator.cs:598-614`）在 604 行只清 `Phase == RelayPhase.Redirecting` 的 half-open 会话，**明确豁免 Relaying（M4）**——两表生命周期不对称即 PRD 机制二。

### 豁免的最小改动候选

- **(i) 表条目加标记（推荐）**：`FlowState`（`Domain.cs:100-116`）加纯数据字段（如 `HoldUntilUtc` 或 `IsPinned` + pin 计数）；`TcpProxyCoordinator.TryAttachRelay`（897-906）设标记、teardown/宽限到期清标记；`RemoveExpired`（216）判定时尊重标记。Core 层不引入 Runtime 依赖（字段是通用数据），与 coordinator 的 Phase 豁免语义对称，sweep 无需跨对象查询。
- **(ii) sweep 前查 coordinator**：`FlowTable.RemoveExpired` / `FlowDispatcher.RemoveExpiredFlows` 加 `Func<FlowKey,bool>` 豁免谓词重载；`IdleExpirySweeper.RunAsync`（`IdleExpirySweeper.cs:60`）传入 `tcp` 的活跃查询。改动面 3 文件、不动 Core 数据模型；代价是 sweeper→coordinator 新增每轮查询（只对 expired 候选查，O(1)/条）。若采用，注意三表顺序需调整为 tcp 先于 flows（见 §5）。
- **(iii) 显式放行回调**：coordinator teardown/宽限到期时回调 dispatcher 释放 flow。生命周期方向倒挂（Runtime 触发 Core 表操作），且 relaying 期间本就不该释放、teardown 后 flow 过期与否无关紧要（迟到包已被宽限丢弃）——语义上没有真正的"释放时机"，不推荐。

## 4. 宽限条目形态候选评估

### (a) 独立墓碑表（tuple → expiry）——推荐

- 形态：`TryRemove`（`TcpRedirectTable.cs:181-191`）成功时写墓碑（双键：`FlowKey`（forward 查）+ `ReverseRedirectTuple`（reverse 查，结构与 `TcpRedirectTable.cs:227` 相同），值为 expiry 时间戳）。查表 miss 点（`TcpProxyCoordinator.cs:520/445/550`）之后查墓碑，命中 → 静默丢弃。
- 需要新 outcome：`TcpRedirectOutcome`（`src/WinForward.Runtime/TcpRedirectInterfaces.cs:77-82`）加成员（如 `Dropped`）。executor 侧 `NdisPacketActionExecutor.cs:74-81` 只特判 `NotRelevant`，对 `Dropped` 走"消费不 reinject"即可（注意复用 `Blocked` 会触发 82-85 的 `LogProxyUnavailable` warn 日志，语义误导，宜加新值）；`FlowDispatcher.TryHandleReverseAsync:141` 的 `Injected ? ProxyConsumed : Block` 对 `Dropped` 自然落 Block（= BlockAsync 丢弃，`NdisPacketActionExecutor.cs:49-54`），无需改。
- 回收：挂 `TcpProxyCoordinator.RemoveExpiredAsync` 或 sweeper tick（见 §5）。
- 优点：**不占 `TcpRedirectTable` 容量**（`TryClaim` 的容量检查在 `TcpRedirectTable.cs:131` 用 `_byOriginal.Count`），**不占 `_sessions` 预算**（`TcpProxyCoordinator.cs:127`，与 fix-port-budget 的口径一致——PRD 明确要求宽限条目不计入活跃会话容量）；状态机不污染活跃表；teardown 汇聚点单一（§1），写入点唯一。
- 代价：一个新类型文件 + 查表点 2-3 处各加一次 miss 后查询。

### (b) 复用 byOriginal 索引加 grace 状态

- 形态：`TryRemove` 改为"降级"——条目保留三索引、`Phase=Closing`（已有该状态）、`LastActivityUtc=now`；查表 API（`TryResolveByOriginal/TryResolveByReverse/IsReverseCandidate`，`TcpRedirectTable.cs:145-174`）都要对 Closing 返回丢弃信号；`RemoveExpired`（`TcpRedirectTable.cs:193-206`）按 graceTimeout 清 Closing。注意 `TcpRedirectTable.RemoveExpired` 目前**生产代码零调用**（仅 `tests/WinForward.Core.Tests/TcpProxyCoordinatorTests.cs:730`），改造它不影响任何生产路径。
- 缺点：grace 条目占用 `_byOriginal.Count` → `TryClaim`（131）会把宽限期内的旧 tuple 计入新流拒绝判定，**直接违反"宽限条目不计入容量"**，除非再造第二套计数；三个查表 API 全部状态化，改动面反而更大；`RemoveExpiredAsync`（`TcpProxyCoordinator.cs:598-614`）按 Phase 过滤的现有逻辑需同步区分。
- 不推荐。

### (c) 时间界（capture 启动时间戳）替代墓碑

- 前提缺失：当前**没有**任何 per-pump/runtime 启动时间戳（见 §6），需先新增。
- 本质缺陷：即使有了时间戳，也无法区分两类 NotRelevant 包——"capture 前建连"的中流包可以在**运行全程任意时刻**首次出现（长连接静默后恢复），"teardown 迟到包"出现在 teardown 后 ~2MSL 内；两者在数据面上形态完全相同（proxy flow + 非 SYN + 双索引 miss）。唯一可用的时间界是 **per-flow 的 teardown 时刻**——那正是 (a) 墓碑的 expiry 字段。runtime 启动时间只能作为辅助信息（如收紧 pass 兜底的注释语义），不能单独承载判定。
- 结论：(c) 单独不可行；(a) 是把"per-flow teardown 时间界"落成数据结构的正确形态。

## 5. sweep 结构与宽限回收挂点

- `IdleExpirySweeper`（`src/WinForward.Runtime/IdleExpirySweeper.cs`）：默认 interval 1min / flowIdle 5min / redirectIdle 5min / relayIdle 2min（37-40）；`RunAsync`（50-87）每 tick 顺序执行：
  1. `_dispatcher.RemoveExpiredFlows`（60）
  2. `_tcp.RemoveExpiredAsync`（61）
  3. `_udp.RemoveExpiredAsync`（62）
  4. `_tcp.LogCapacitySummary()`（64，fix-port-budget 新挂的 capacity 摘要，实现在 `TcpProxyCoordinator.cs:90-100`）
  5. 汇总日志 `runtime.expired`（65-69）
- **宽限到期回收最自然挂点**：`TcpProxyCoordinator.RemoveExpiredAsync`（`TcpProxyCoordinator.cs:598-614`）内部追加墓碑表清扫（coordinator 拥有表生命周期，sweeper 无需感知新表、不加第四个定时器，满足 PRD"同一次 sweep 收敛"）；`graceTimeout` 可作为方法参数或构造注入。
- **顺序注意**：若 flow 豁免采用 §3(ii) 谓词方案，需把 61 行（tcp）挪到 60 行（flows）之前或改为 flows 扫描时回调查询；采用 §3(i) 标记方案则现有顺序无需变动。

## 6. capture 启动时间戳现状

- `TransactionalCaptureRuntime.StartAsync`（`src/WinForward.Runtime/CaptureLifecycle.cs:57-66`）与 `StartCoreAsync`（68 起）不记录任何时间。
- `NdisCapturePump.RunAsync`（`src/WinForward.NdisApi/NdisCapture.cs:64`）无时间记录；`MultiAdapterCaptureLoop`（全文件 68 行）亦无。
- 代码库中唯一的时间戳先例是 `ProcessAttribution.cs:66` 的进程创建时间（归因缓存键，与 capture 生命周期无关）。
- **结论**：无现成启动时间戳；新增只需一行（如 coordinator/sweeper 构造时记 `DateTimeOffset.UtcNow`）。但如 §4(c) 分析，时间界不能单独区分"迟到包丢弃 vs capture 前建连放行"——区分依据必须落在 per-flow 的 redirect 历史（墓碑）上，时间戳仅可作辅助。

## 7. UDP 边界：维持现状是否成立

**成立，未发现反例。**依据：

- UDP 会话无握手/挥手语义：过期由 `UdpProxyCoordinator.RemoveExpiredAsync`（`src/WinForward.Runtime/UdpProxyCoordinator.cs:230-273`）按 `LastActivityUtc` 2 分钟清理（235），且有 `_activeSends`/`_expiring` 防竞态（246 → `TryBeginExpiry`:479-487）。
- 迟到/恢复的客户端 datagram：`TrySendAsync`（69-105）在 `_sessions` miss 时**直接重建 session**（77-89，新 SOCKS5 UDP ASSOCIATE），无"中流包无处可去"的问题——不存在 TCP 式的 NotRelevant 兜底。
- 反向 response：session 过期后 receive loop 已停、response 无处投递即丢弃；UDP 上层（DNS 重传、QUIC keepalive/connection ID 路由）自愈，QUIC 不依赖 4-tuple 连续性，重建 relay 端口无害。
- response 注入帧若再被 capture 观察：`FlowDispatcher.DispatchAsync:95-100` 有专门的 UDP reverse 放行分支（`IsReverseOf`），flow 表过期重建后仍按 proxy 决策 → `TrySendAsync` 走（可能新建的）session，无黑洞、无回弹。
- UDP 的 flow 表条目同样受 5 分钟过期影响，但重建 flow 只需重新评估（无进程归因失败放大问题？——UDP 也会归因，但 UDP 重建 session 的代价本来就是常态路径，无 TCP 的"活跃连接被打断"后果）。PRD 维持 UDP 现状的口径与代码事实一致。

---

## 推荐方案要点（供 design.md 起点）

1. **墓碑表 (a)**：`TcpRedirectTable.TryRemove` 成功时写双键墓碑（`FlowKey` + `ReverseRedirectTuple` → expiry）；查表 miss 点（`TcpProxyCoordinator.cs:520/445/550`）后查墓碑，命中返回新 `TcpRedirectOutcome.Dropped`；executor 静默消费。不占表容量、不占 session 预算。
2. **flow 豁免**：`FlowState` 加 pin/HoldUntilUtc 标记（§3(i)），`TryAttachRelay` 置位、teardown+宽限到期复位；`FlowTable.RemoveExpired` 尊重标记。备选谓词注入（§3(ii)）。
3. **sweep 收敛**：墓碑清扫挂 `TcpProxyCoordinator.RemoveExpiredAsync` 内部，与 `LogCapacitySummary` 同 tick，无新定时器。
4. **时间界仅作辅助**：无现成启动时间戳；不单独承载判定。
5. **UDP 不动**。
