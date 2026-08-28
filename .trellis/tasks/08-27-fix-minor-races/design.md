# Design: 次要竞态修复（RST 序号、句柄时效、连接预算）

关联 PRD: `prd.md`。研究依据: `research/minor-races-feasibility.md`（HEAD 42a6516）。

## 0. 研究修正（相对 PRD 的认知更新）

- **D3 更严重**：30s 是 per-attempt 而非总预算（DNS 多地址串行最坏 ~150s）；
  `perAttemptTimeout`/`maxAttempts` 参数管道已存在，relay 调用点未传
  （TcpProxyRelay.cs:27）。
- **D4 未解决**：批量读只消除"有包时的逐包开销"，空批仍 `Task.Delay(1ms)` 轮询。
- D1/D2 与 PRD 认知一致。

## 1. D1: 动态 RST 序号

**问题**：`TryInjectClientResetAsync`（Coordinator:736-737）用
`serverInitialSeq+1 / clientInitialSeq+1`，客户端已发数据后 out-of-window，RST 被
客户端栈丢弃（RFC 5961）→ 挂起至超时（慢 EOF）。

**方案**：
- `TcpRedirectAssociation` 增加两个可变跟踪字段（lock 内写）：
  `ClientNextSeq (uint?)`、`ServerNextSeq (uint?)`——观测到的各自方向
  `seq + payloadLen`（SYN/FIN 计 1）最大推进值。
- 写入点：forward 在 `ReinjectExistingFlowDataAsync`、reverse 在
  `HandleReverseAsync`，均取 rewrite 前的帧（既有 parse 路径旁的轻量 TCP 头读取：
  seq 4B + dataOffset 推 payload 长度；ns 级开销，研究已确认可行）。
- RST 构造：ack = `ClientNextSeq ?? clientInitialSeq+1`，
  seq = `ServerNextSeq ?? serverInitialSeq+1`——覆盖已发数据，必然 in-window。
- 未观测到数据的连接行为与现状逐字节一致（null 回退初值）。

## 2. D2: 注入失败显式化（句柄时效）

**问题**：适配器重建后 `OriginAdapterHandle` 失效 → `SendPacketToAdapter` 抛
`Win32Exception` → 裸 catch → `FailAssociationAsync` **静默**拆连接，无 reason。

**边界**：运行期重枚举/句柄重解析依赖 `SetAdapterListChangeEvent` 接线
（Program.cs 有 "ADAPTER-LIST CHANGE SEAM (deferred)" 注释），属独立大件，本任务
不做。本任务把**静默失效变成可观测 + 尽力通知**：

- 注入路径 catch `Win32Exception` 时：warn 级日志带 `reason=injectionFailure` +
  native error + adapter handle 值 + flow key（既有诊断字段惯例）；
- 随后按序执行：best-effort 注入客户端 RST（若双向序号已知，复用 D1 字段）→
  `FailAssociationAsync`（写墓碑，复用 table-lifecycle 单写点语义）；
- 纯净 catch 不留：任何 `SendPacketTo*` 异常路径都走此出口。

## 3. D3: SOCKS5 连接预算收紧

**方案**：relay 调用点（TcpProxyRelay.cs:27）传
`perAttemptTimeout: 10s, maxAttempts: 2`——最坏 20s（原 150s），典型失败
（refused/unreachable）仍亚秒级。内部常量，不加配置字段（避免配置面膨胀；
PRD 允许 design 定稿目标值）。

**不改**：失败后 RST 注入时序（本就立即）；UDP control 路径。

## 4. D4: 轮询结论（记录关闭）

空批 1ms 轮询保留：纯空闲期每秒 ≤1000 次空查询（gate+queue 查询 µs 级），
单核 <0.5%；有包时无空批代价（批量读已落地）。事件化（`SetPacketEvent`）
维持 datapath 任务 design §7 的否决结论。PRD AC-D4 以此结论勾选。

## 5. 与兄弟任务的交叠（风险）

三项全部触碰 `TcpProxyCoordinator.cs`（D 组共同热点）：
- 墓碑单写点 `RemoveAssociationFromTable`（:1017-1021）——D2 的 fail 路径必须
  经它，不得新增 TryRemove 调用点；
- 端口预算容量门（:98-105）——不触碰；
- 就地改写读-写顺序不变量（RecordClientSyn 先于 rewrite）——D1 的序号跟踪也在
  rewrite 前，注意不引入新的"写后读"。

## 6. 兼容与回滚

三个独立提交（D1 / D2 / D3 各一），可单独 revert。D4 无代码改动。
无配置、无日志事件名、无 exit code 变化；D2 新增一个 warn 事件
（`tcp.redirect.failed reason=injectionFailure`）属新增而非改名。

## 7. 验证

- 单测：D1 三场景（无数据回退初值 / 有数据 ack 覆盖 / SYN 计 1 边界）；
  D2（fake injector 抛 Win32Exception → warn + 墓碑 + 显式拆除）；
  D3（调用点传参断言 + 预算语义）。
- Windows smoke（tmux + evil-winrm-py `runps`/`upload`，用户已提供工具用法）：
  常规 trace 复跑核对无回归 + `reason=injectionFailure` 可观测性抽查
  （无真实句柄失效场景时以单测为准，smoke 只验不回归）。
