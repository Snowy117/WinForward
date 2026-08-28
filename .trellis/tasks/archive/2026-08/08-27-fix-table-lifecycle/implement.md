# Implement: 重定向表与流表生命周期修复

关联: `prd.md`、`design.md`。两个独立提交点（A 墓碑 / B 豁免）。

## 实现顺序

### Step A: 墓碑表 + Dropped outcome

- [x] 新建 `TcpRedirectTombstoneTable`（双键 FlowKey/ReverseRedirectTuple →
      ExpiryUtc；容量同源 tcpFlowCapacity 派生、满丢最旧；`TryAdd`/`TryHit`
      （命中即知）/`RemoveExpired(now)`）。
- [x] `TcpProxyOutcome`（或等名枚举）加 `Dropped`；确认 executor 对 `Dropped`
      的映射 = 静默消费 + trace `packet.completed outcome=dropped`（不触
      `LogProxyUnavailable`，design §2.3 caveat）。
- [x] coordinator：拆除伴随点（`TryRemove` 调用侧，一处覆盖全部入口）写墓碑；
      `HandlePacketAsync` forward miss 后查墓碑；`HandleReverseIfApplicableAsync`
      reverse miss 后查墓碑；`RemoveExpiredAsync` 内回收墓碑。
- [x] 测试：两向迟到包命中 → Dropped + 零 reinject；墓碑到期后同包回落
      NotRelevant→Pass；relay 失败/fail-closed 拆除同样写墓碑；容量满丢最旧；
      trace 事件字段。

验证: `dotnet test -c Release --filter "FullyQualifiedName~TcpProxy|FullyQualifiedName~Redirect|FullyQualifiedName~Tombstone"`。

### Step B: flow 谓词豁免 + sweep 顺序

- [x] `FlowDispatcher.RemoveExpiredFlows` 加 `Func<FlowKey,bool>? isHeld`；
      到期且谓词真 → 跳过且 `LastActivityUtc` 不变。
- [x] coordinator 暴露 `internal HoldsFlow(FlowKey)`（sessions ∨ 墓碑）；
      `IdleExpirySweeper` 接线谓词并把顺序改为 tcp→flows→udp（capacity 摘要
      挂载点不动）。
- [x] 测试：relaying 静默流 idle 超时不清、无新 `flow.created`；会话结束后按
      原 idle 时点清除；sweep 顺序回归；null 谓词现状回归。

验证: `dotnet test -c Release`（全量）。

### Step C: 文档 + 收尾

- [x] README Notes：宽限期内迟到包静默丢弃一句。
- [x] implement.md 验证记录 + PRD AC 勾选。

### Step D: Windows smoke（可选，复用 wf-batch-test）

- [ ] trace 复测同等负载 `outcome=notrelevant` 回落；新 `outcome=dropped` 出现
      且量级 ≈ 原 notrelevant 中 teardown 尾部部分；无 warn/error。

## 风险文件与回滚

| 提交 | 触碰 | 回滚 |
|------|------|------|
| A | 新墓碑表、coordinator、executor、枚举、测试 | 整体 revert |
| B | FlowDispatcher、IdleExpirySweeper、coordinator 谓词、测试 | 整体 revert（谓词 null 回现状） |

## start 前检查

- [x] prd.md（研究结论回填）
- [x] design.md（否决项 + 权衡记录）
- [ ] implement.jsonl / check.jsonl 真实条目
- [ ] 用户批准最终规划摘要

---

## 验证记录（2026-08-28，implement agent）

### 改动文件

生产代码（Step A）：

- `src/WinForward.Runtime/TcpRedirectTombstoneTable.cs` — 新建。双键
  （FlowKey + ReverseTuple）→ 共享 `TombstoneEntry(Forward, Reverse, ExpiryUtc)`；
  `TryAdd` 满容量丢最旧（FIFO 队列 + ReferenceEquals 陈旧项跳过）；双向
  `TryHit`（`now < ExpiryUtc` 才命中）；`RemoveExpired(now)`；容量与会话预算同源
  （coordinator 构造时 `_capacity` 派生）。`internal`（InternalsVisibleTo 测试可见）。
- `src/WinForward.Runtime/TcpRedirectInterfaces.cs` — `TcpRedirectOutcome` 加
  `Dropped`（不复用 `Blocked`，避免 `LogProxyUnavailable` 误导日志）。
- `src/WinForward.Runtime/NdisPacketActionExecutor.cs` — `ProxyAsync` 对
  `Dropped` 静默消费：不 reinject、不 Block、trace
  `packet.completed outcome=Dropped`（序列化小写 `dropped`）；
  `tcp.packet.handled outcome=dropped` 由既有 trace 自动覆盖。
- `src/WinForward.Runtime/TcpProxyCoordinator.cs` — 字段/构造注入墓碑表；
  `RemoveAssociationFromTable` 单一写入点（`ReleaseAssociationAsync` 与
  `FailAssociationAsync` 两处 `_table.TryRemove` 全部改经此 helper，覆盖 relay
  完成/relay 失败/fail-closed/全局 dispose 全部拆除入口）；`HandlePacketAsync`
  forward miss 后、`HandleReverseIfApplicableAsync` reverse miss 后各查一次墓碑；
  `RemoveExpiredAsync` 内回收墓碑（返回值仍只计过期会话，`runtime.expired`
  口径不变）；`TombstoneGracePeriod = 60s` 常量；Step B 的 `internal
  HoldsFlow(FlowKey)`（sessions ∨ 墓碑，不比对 Generation）与测试缝
  `internal Tombstones`。

生产代码（Step B）：

- `src/WinForward.Core/Domain.cs` — `FlowTable.RemoveExpired` 加可选
  `Func<FlowKey,bool>? isHeld`；谓词仅对 idle 到期候选求值；跳过不 Touch
  （`LastActivityUtc` 不变，豁免解除后按原 idle 时点过期）；null = 现状。
- `src/WinForward.Runtime/FlowDispatcher.cs` — `RemoveExpiredFlows` 透传谓词。
- `src/WinForward.Runtime/IdleExpirySweeper.cs` — sweep 顺序改为
  tcp→flows→udp；flows 扫描接线 `_tcp.HoldsFlow` 谓词（tcp 为 null 时不接，
  回现状）；capacity 摘要仍挂 tick 末。

文档（Step C）：

- `README.md` — Notes 补充：proxied TCP 连接结束后短 TIME_WAIT 式宽限条目，
  迟到 ACK/FIN 重传静默丢弃（不发往真实服务器，避免回弹 RST）。

### 测试（新增 14 个）

`tests/WinForward.Core.Tests/TcpRedirectTombstoneTableTests.cs`（新文件，4 个）：

- `BothKeysHitOnlyInsideTheGraceWindow` — 双键窗口内命中、到期（含等于）不命中、
  无关键不命中。
- `FullTableEvictsTheOldestEntry` — 容量 2 写 3 丢最旧。
- `RemoveExpiredReclaimsOnlyElapsedEntriesOnly` — 到期回收、未到期保留。
- `ReAddingTheSameKeysRefreshesTheExpiry` — 同键重写刷新 expiry，无孤儿条目。

`tests/WinForward.Core.Tests/TcpProxyCoordinatorTests.cs`（8 个）：

- `LateForwardPacketAfterTeardownIsDroppedWithinGrace` — relay 完成拆除后迟到
  forward ACK → `Dropped`、零 reinject。
- `LateReversePacketAfterTeardownIsDroppedWithinGrace` — 迟到 reverse FIN/ACK →
  `Dropped`、零 reinject。
- `LatePacketFallsBackToNotRelevantAfterGraceExpiry` — 墓碑到期后同包回落
  `NotRelevant`（现状兼容，经 `Tombstones.TryAdd` 过期时间戳模拟快进）。
- `RelaySetupFailureTombstonesTheFlow` — relay 建立失败拆除同样写墓碑。
- `ExecutorSilentlyConsumesTombstoneHitWithTraceAndNoReinjection` — executor
  映射：零 reinject、`tcp.packet.handled` 的 `outcome=Dropped` 字段、
  `packet.dropped reason=grace` trace（check 阶段更名，见下）、无
  `LogProxyUnavailable` warn。
- `HoldsFlowTracksSessionAndGraceWindow` — half-open/relaying 会话与宽限墓碑
  三阶段持有，宽限过期后释放，无关 flow 不持有。
- `HeldFlowExpiresAtOriginalIdlePointAfterGraceLapses` — 接线后的 sweep 序列
  （tcp 先、flows 携谓词）：拆除后 flow 被墓碑持有，宽限失效后按原 idle 时点清除。
- `SilentRelayingFlowSurvivesSweepsWithoutReevaluatingDecision` — 真实
  `IdleExpirySweeper`（100ms interval / 250ms flowIdle）下 relaying 静默流 900ms
  （≈9 tick）不被清除、恢复流量无新 `flow.created`、mid-flow 数据照常 rewritten。

`tests/WinForward.Core.Tests/FlowDispatcherTests.cs`（1 个）：

- `RemoveExpiredFlowsSkipsHeldEntriesAndDefaultsToRemoveAll` — 谓词真跳过 /
  谓词假清除 / null 谓词现状回归。

`tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs`（1 个）：

- `FlowTableRemoveExpiredHonorsHoldPredicateWithoutTouchingActivity` — 跳过时
  `LastActivityUtc` 不变；豁免解除后同一 sweep 时点按原 idle 清除。

### 验证命令与结果

- `dotnet build -c Release`：**0 警告 0 错误**。
- `dotnet test -c Release --filter "FullyQualifiedName~TcpProxy|FullyQualifiedName~Redirect|FullyQualifiedName~Tombstone"`：
  58/58 通过（含既有回归）。
- `dotnet test -c Release` 全量：**346/346 通过**（新增 14 个：墓碑表 4 +
  coordinator 8 + dispatcher 1 + FlowTable 1；既有 332 全部保持绿色）。
- `dotnet format --verify-no-changes`：本任务全部改动/新建文件**干净**；报错仅
  存于 7 个未触碰的存量文件（`CaptureAdapterScopeResolver.cs`、
  `MultiAdapterCaptureLoop.cs`、`NdisAdapterModeController.cs`、
  `NdisPacketReinjector.cs`、`PacketFlowClassifier.cs`、
  `TcpRedirectInjectorTests.cs`、`CaptureLifecycleTests.cs` 的 FINALNEWLINE/IMPORTS
  旧账），与本次改动无关，未处理。

### PRD AC 对应

- AC1（smoke `outcome=notrelevant` 回落）：留待 Step D（Windows smoke，主会话）。
- AC2（挥手竞态）：单测验证（两向迟到包 Dropped + 零 reinject；trace
  `outcome=dropped` 来自 `tcp.packet.handled`/`packet.reverseHandled` 两个事件）；
  trace 全量复测归 Step D。
- AC3（静默流）：`SilentRelayingFlowSurvivesSweepsWithoutReevaluatingDecision`
  锁定。
- AC4（全量测试 + `runtime.expired` 语义）：347/347；`runtime.expired` 计数口径
  不变（墓碑回收不计入返回值）。

---

## Check 阶段记录（2026-08-28，check agent）

### 核对结论

- 墓碑写入点：`_table.TryRemove` 在 coordinator 内仅剩 `RemoveAssociationFromTable`
  一处调用；relay 完成（`ObserveRelayCompletionAsync`→`TearDownSessionAsync`→
  `ReleaseRetiredSessionAsync`→`ReleaseAssociationAsync`）、relay 失败
  （`HandleRelaySetupFailureAsync`）、fail-closed（`FailAssociationAsync`）、全局
  dispose（`DisposeCoreAsync`）全部经该伴随点写墓碑 ✓。
- 查询点：forward 在 `TryResolveByOriginal` miss 后（`HandlePacketAsync`）、
  reverse 在 `IsReverseCandidate` miss 后（`HandleReverseIfApplicableAsync`，且在
  TCP 协议门之后，UDP 不受影响）✓；reverse 键序 `(key.Local, key.Remote)` 与
  redirect 表 `_byReverse` 的 `(ReverseSource, ReverseDestination)` 同构 ✓。
- 容量：墓碑独立表，容量 = `_capacity`（生产装配即 `TcpFlowCapacity`），不占
  `_sessions` 预算、不占 `_byOriginal.Count`（`TryClaim` 判定）；满丢最旧
  （FIFO + ReferenceEquals 陈旧跳过，`_byForward.Count >= capacity` 保证队列中必有
  活跃节点、循环必然终止）✓。
- flow 豁免：谓词仅对 idle 到期候选求值、跳过不 Touch；null 谓词回现状；sweep
  顺序 tcp→flows→udp，capacity 摘要仍挂 tick 末；生产装配
  （`Program.cs:261`）把 coordinator 传给 sweeper，谓词真实生效 ✓。
- 锁序：FlowTable `_gate` → coordinator `_gate` →（非嵌套）tombstone `_gate`，
  单向无反向路径（coordinator 不引用 FlowTable）✓。
- `TcpRedirectTable.RemoveExpired` 维持零生产调用 ✓。

### Check 阶段修复（3 处 + 测试同步）

1. **`NdisPacketActionExecutor.cs` — executor trace 事件撞名**：原实现为墓碑命中
   新增 `packet.completed outcome=Dropped`，但 `packet.completed` 是 dispatcher
   `CompleteAsync` 拥有的每包一条完成事件（单轮 smoke 日志实测每包恰一条；多轮
   日志中 packet=N 重复系服务重启序号重置）。executor 同名事件会破坏该不变量并
   干扰 grep 统计。改为 `packet.dropped reason=grace`（与 `BlockAsync` 的
   `packet.dropped reason=policy` 同族、可区分）。`tcp.packet.handled
   outcome=dropped`（forward）与 `packet.reverseHandled outcome=dropped`
   （reverse）仍满足 PRD AC2 的 trace 验证口径。
2. **`FlowDispatcher.cs` — reverse 迟到包被误标为策略丢弃**：
   `TryHandleReverseAsync` 原把 `Dropped` 映射为 `Block` disposition →
   `BlockAsync` 打出 `packet.dropped reason=policy`，把 TIME_WAIT 宽限丢弃误标成
   策略丢弃。改为 `Dropped` → `ProxyConsumed`（与 `Injected` 同为代理层消费，
   不调 executor），真实原因由 `packet.reverseHandled outcome=dropped` 记录。
   新增回归测试 `DispatcherConsumesReverseStragglerAsProxyConsumedWithoutPolicyLabel`
   （dispatcher 级：lease `ProxyConsumed` + 零 reinject + 无 `reason=policy`）。
3. **`TcpProxyCoordinator.cs` — 错位的重复 XML summary（存量）**：
   `HandleReverseIfApplicableAsync` 头上叠了两个 `<summary>`，第一个实为
   `HandlePacketAsync` 的文档。将其归位到 `HandlePacketAsync`（本任务触碰文件内
   的存量文档缺陷，顺手修复）。

测试同步：`ExecutorSilentlyConsumesTombstoneHitWithTraceAndNoReinjection` 断言改为
`packet.dropped reason=grace`，并新增"不得出现 `packet.completed outcome`"负向断言。

### Check 阶段验证

- `dotnet build -c Release`：**0 警告 0 错误**。
- `dotnet test -c Release` 全量：**347/347 通过**（implement 阶段 346 + check
  阶段新增 1）。
- `dotnet format --verify-no-changes`：本任务全部改动文件干净；报错仍仅为上述
  7 个存量文件旧账，未触碰。

### Step D: Windows smoke（2026-08-28，WinLtsc via winrm）

- 部署：交叉构建 self-contained（非 AOT）→ tar → http.server 拉取到
  `wf-batch-test`；配置 = winforward-test/config.json 文本替换 logLevel=trace。
- 负载与基线一致：8× curl example.com + 8× nslookup @8.8.8.8 + ping×4。
- 结果：TOTAL=4949；captured=890 / completed=890（完美配对）；failed=0；
  HTTP 8/8、DNS 8/8；warn=0 error=0；relay 9 started / 8 ended（第 9 条为
  kill 时活跃，正常）；rejected=0。
- **核心对照（vs 修复前基线 113）**：`outcome=notrelevant` = **11**（回落
  90%，剩余为 capture 启动前已建连接的合法 pass 语义）；墓碑命中
  `outcome=dropped` = 55（forward `packet.dropped reason=grace` = 4，其余为
  reverse 方向 reverseHandled 消费），迟到包不再发往真实服务器。
- 操作注意：PS5 下 scriptblock 不能 `$cnt(...)` 调用；`ConvertFrom-Json` 往返
  写配置失败过一次，改用文本替换；winrm 回显吞输出 → 统计结果经文件回传。
