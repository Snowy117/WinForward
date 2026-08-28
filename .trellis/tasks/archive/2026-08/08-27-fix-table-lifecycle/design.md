# Design: 重定向表与流表生命周期修复

关联 PRD: `prd.md`。父任务: `08-27-fix-eof-reset-design-flaws`。研究依据:
`research/table-lifecycle-feasibility.md`（下称 [R§n]，基线 HEAD c1f59a0）。

## 1. 方案总纲

两个独立但同属表生命周期的修复：

- **A. 迟到包墓碑（TIME_WAIT 宽限）**：`TcpRedirectTable.TryRemove` 时同步写入
  独立墓碑表；两处查表 miss 后查墓碑，命中 → 静默消费（新 outcome `Dropped`），
  不再 `NotRelevant → Pass` 发往真实服务器。到期由既有 sweep 回收。
- **B. flow 表豁免 relaying 流**：`FlowDispatcher.RemoveExpiredFlows` 注入活跃
  谓词（实时查 coordinator 会话集），relaying 中/宽限中的 flow 不因静默被清除。

研究否决项：复用 byOriginal 加 grace 状态（占用 `TryClaim:131` 容量，违反
PRD"宽限条目不计入活跃会话容量"）；capture 启动时间界（[R§6]：时间戳不存在，
且数据面同形不可区分——迟到包与"capture 前建连的静默流首个包"唯一可靠的区分
就是墓碑的 per-flow expiry）。

## 2. 墓碑表设计

### 2.1 数据结构

`TcpRedirectTombstoneTable`（WinForward.Runtime，coordinator 私有）：

- 双键查询：`FlowKey`（forward 方向）+ `ReverseRedirectTuple`（reverse 方向）——
  与 redirect 表两个查入口同构，一次写入建两个键或双字典均指向同一 expiry。
- 条目：`DateTimeOffset ExpiryUtc`（写入时刻 + GracePeriod）。
- 容量：与 coordinator 会话容量同源派生（`tcpFlowCapacity`）；满时丢最旧
  （最坏回退为现状行为，不 fail 也不撑内存）。
- 常量 `GracePeriod = 60s`：覆盖挥手尾部（最后 ACK）与常见 FIN 重传
  （RTO 退避后通常 < 10s）。不采用完整 TIME_WAIT 240s——墓碑条目驻留 4 倍
  时长换不回对等的回弹防护；不新增配置字段（避免又一轮 7 触达，理由见 §5）。

### 2.2 写入点（唯一）

`TcpRedirectTable.TryRemove` 的全部调用方（relay 完成 retire `942`、relay 失败
`714-720`、fail-closed `908-914`、全局 dispose，[R§1]）汇聚在 coordinator——
墓碑写入放 coordinator 的移除伴随点（一处代码，覆盖全部入口），不放 table 内部
（table 保持纯索引职责）。

### 2.3 查询点（两处）与 outcome

- forward：`HandlePacketAsync:550` `TryResolveByOriginal` miss → 查墓碑 →
  命中返回 `Dropped`；
- reverse：`HandleReverseIfApplicableAsync:520` `IsReverseCandidate` miss →
  查墓碑 → 命中返回 `Dropped`；
- executor 侧 `Dropped` → 静默消费（不 reinject、不 Block、计数 + trace 事件）。
  **勿复用 `Blocked`**（[R§caveats]：会触发 `LogProxyUnavailable` 误导日志）。

### 2.4 回收

挂 `TcpProxyCoordinator.RemoveExpiredAsync` 内部（[R§5]，零新定时器）；
`TcpRedirectTable.RemoveExpired:193` 生产零调用（[R§caveats]），本任务不动它。

## 3. flow 表豁免设计

- `FlowDispatcher.RemoveExpiredFlows` 签名加可选谓词
  `Func<FlowKey, bool>? isHeld`（null = 现状）；条目 idle 到期但谓词为真 →
  跳过（不删、可顺带 Touch 或保 LastActivityUtc 不变——保不变，让豁免解除后
  按原 idle 时点自然过期）。
- 谓词实现：coordinator 暴露 `internal bool HoldsFlow(FlowKey key)` =
  `_sessions` 含该键（Relaying）∨ 墓碑表含该键（宽限中）。Generation 不比对：
  豁免是"这个 4 元组别清"，新流重用同 tuple 时 `TryClaim` 会换代，代不同则
  豁免自然失效。
- sweep 顺序调整：`IdleExpirySweeper` 现为 flows→tcp→udp（[R§5]），改为
  **tcp→flows→udp**——tcp sweep 先移除到期 half-open，其 flow 随后可按 idle
  自然清除，避免"会话已亡 flow 仍被豁免一轮"。capacity 摘要挂载点不动（tick 末）。

## 4. 验证与观测

- trace：`packet.completed outcome=dropped`（墓碑命中）新增事件；smoke 复测
  `outcome=notrelevant` 显著回落（PRD 验收）。
- 单测：迟到 ACK/FIN（forward+reverse 两向）命中墓碑 → Dropped、不 reinject；
  墓碑到期后同包 → 回落 NotRelevant→Pass（现状兼容）；容量满丢最旧；flow 豁免
  （relaying 静默流 idle 过期不清、会话结束后按原时点清除）；sweep 顺序回归。
- README Notes：补一句宽限期内迟到包静默丢弃的说明（PRD 要求）。

## 5. 权衡记录

| 决策 | 理由 |
|------|------|
| 60s 常量，不配置化 | 挥手尾部 + 重传全覆盖；配置化收益低、触达面大；墓碑表容量同源派生兜底 |
| 墓碑独立表，不并表 | byOriginal 复用会占 TryClaim 容量（[R§4]）；独立表可整体 revert |
| 谓词注入 vs FlowState 加 pin | 谓词无状态、无刷新时机问题（pin 需要 sweep 外的刷新点，静默流没有包触达）；代价是 sweep 顺序约束 |
| 保 LastActivityUtc 不变 | 豁免解除即按原时点过期，语义最可预测 |

## 6. 兼容性与回滚

- 对外零语义变化：配置、规则、fail-closed 全不动；唯一行为差异是"teardown 后
  60s 内同 tuple 包被丢弃而非发给真实服务器"——这正是修复目标。
- 两个提交点：A（墓碑+Dropped）、B（谓词豁免+顺序），独立可 revert。

## 7. 遗留边界

- `UdpResponseReinjector`/UDP 路径不动（[R§7]：无握手回弹问题）。
- 与 `fix-minor-races` D2 的 `OriginAdapterHandle` 失效拆除共享 `TryRemove`
  伴随点——本任务先落地墓碑写入，D2 后续在同一拆除序列上加固，不冲突。
