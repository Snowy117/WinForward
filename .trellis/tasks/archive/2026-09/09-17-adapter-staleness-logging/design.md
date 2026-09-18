# 技术设计：适配器视图自愈 + 运行时诊断日志

任务：09-17-adapter-staleness-logging（PRD 见 prd.md）

## 1. 总体结构

两个正交子系统，共享少量计数基础设施：

```
┌─ R1-A 周期重枚举 ──────────┐   ┌─ R1-B 失败率触发 ────────────┐
│ PeriodicTimer(30s) → demand │   │ 失败计数器(30s 窗) → 阈值     │
│ → ProcessRefreshDemandAsync │   │ → runner.RequestForcedRefresh │
│ （地址指纹纳入 diff）        │   │ （arm _forceRebuild + demand）│
└─────────────┬───────────────┘   └──────────────┬───────────────┘
              └──────────► LayeredCaptureRunner（现有 storm-guard / no-op / forced 语义全部复用）◄─┘

┌─ R2 诊断日志 ─────────────────────────────────────────────────┐
│ 事件增强(R2.2) + 静默失败点补 warn(R2.1, 节流) + 心跳(R2.3)     │
│ + 降噪(R2.4)：共享一个轻量计数器集合，心跳聚合输出               │
└───────────────────────────────────────────────────────────────┘
```

## 2. R1-A 周期性重枚举 + 地址指纹 diff

### 2.1 枚举项扩展

`AdapterEnumerationItem` 增加 `AddressFingerprint`（string，规范化单播地址集合指纹）：

- 来源：`WindowsAdapterInventory` 关联路径新增单播地址查询（iphlpapi `GetUnicastIpAddressTable`，控制路径冷调用；按 `Luid`/`IfIndex` 归组到适配器）。
- 指纹 = 该适配器全部单播地址（含 v6 临时地址，**排除 v6 link-local fe80::/10**）排序后 `string.Join(';')`——不需要 hash，可读性即诊断价值。
- `AdapterEnumerationDiff.LinkStateEquals` 扩展比较该指纹：IPv6 临时地址轮换 → `changed` → 走现有 rebuild。
- 测试影响：现有 no-op 测试的 fake 枚举两侧指纹相同，语义不变；新增「地址变化触发 changed」测试。

### 2.2 周期 tick 接入 runner

- `LayeredCaptureRunner` 新增构造参数 `periodicRefreshInterval`（默认 30s，`TimeSpan.Zero` 禁用）。
- `RunAsync` 内起一个 `PeriodicTimer` 任务：每次 tick 调 `_demandGate.Signal()`（**非 forced**——空 diff 自然 no-op，地址/句柄/MAC/MTU 任一变化才 rebuild）。
- storm-guard（1s 最小间隔）天然吸收 tick 与 NDISRD 事件的并发；tick 信号与现有信号共用 demand gate，不新增状态机。
- 生命周期：随 `RunAsync` 的 finally 一并释放（与 monitor 同归）。

## 3. R1-B 失败率即时触发（forced refresh）

### 3.1 信号接口

```csharp
/// 由 LayeredCaptureRunner 实现，durable 层组件持有并上报。
public interface IInterceptionHealthSignal
{
    /// 上报一次「再注入/转发路径失败」观察；内部判定阈值与冷却。
    void ReportFailure(string counter);   // counter: "udp.originUnresolved" / "udp.failClosedDrop" / "tcp.relaySetupFailed" / "reinject.passFailed"
}
```

- 实现放在 runner 侧（`InterceptionHealthMonitor`）：每 counter 一个 30s 滑动窗计数，阈值默认 `udp.originUnresolved ≥ 8`、`tcp.relaySetupFailed ≥ 3`、`reinject.passFailed ≥ 3`（可配置 `PolicySnapshot`/构造参数）。
- 触发动作：`_forceRebuild = true; _demandGate.Signal()`，事件 `runner.forcedRefresh`（warn：reason、counter 快照）。复用现有 forced 语义（`diff.IsEmpty && !forced` 跳过 no-op，强制重建 generation 拿全新句柄/MAC/地址视图）。
- 防风暴三层：风暴间隔（storm-guard）→ 触发后 60s 冷却（所有 counter 共享）→ 连续 forced rebuild 超过 3 次仍失败则降频至 5 分钟并 `error` 级日志（防重建循环，语义对齐 `MaxConsecutiveStartupRecoveries`）。

### 3.2 信号源接线

| 信号源 | 挂点 | counter |
|---|---|---|
| `UdpResponseReinjector` origin 解析失败（host fallback） | `UdpResponseReinjector.cs:239` 路径 | `udp.originUnresolved` |
| `UdpResponseReinjector` fail-closed drop | `UdpResponseReinjector.cs:219/249` 路径 | `udp.failClosedDrop` |
| relay 建立失败 | `ClientResetInjector.HandleRelaySetupFailureAsync` | `tcp.relaySetupFailed` |
| pass 直通再注入失败 | `NdisPacketActionExecutor` PassAsync 的 native 异常路径 | `reinject.passFailed` |

- 依赖注入：`IInterceptionHealthSignal` 经 `DurableCaptureBundle.CreateAsync` 新参数传入，由 `Program.cs` 把 runner 的实现同时交给两侧（bundle 组件构造时下传，可空——null 时为 no-op，保持测试兼容）。

## 4. R2 诊断日志

### 4.1 事件清单（新增/修改）

| 事件 | 级别 | 变更 | 字段 |
|---|---|---|---|
| `flow.capacity-block` | warn（5s 节流） | 新增 | flowKey、tableSize、capacity |
| `flow.attribution-miss` | warn（5s 节流） | 新增 | protocol、local、remote、afterRetry |
| `reinject.pass-failed` | warn（5s 节流） | 新增 | adapter(stableId)、nativeError、flowKey |
| `runner.forcedRefresh` | warn | 新增 | reason、counters、cooldownState |
| `tcp.redirect.relaySetupFailed`（替换原固定文案 warn） | warn | 修改 | error、socketError、upstream、attempts、flowKey |
| `tcp.redirect.unrelatedPeer`（替换） | warn | 修改 | listener、expected、actual |
| `udp.reinject.unresolved`（替换 host fallback 文案） | warn（30s 节流+计数） | 修改 | flowKey、mapAdapters 摘要 |
| `udp.reinject.drop`（替换 fail-closed 文案） | warn（30s 节流+计数） | 修改 | flowKey、originKind |
| `udp.targets.noMac`（替换） | warn 首次+集合变化 | 修改 | adapters 摘要（去每次刷屏） |
| `runner.heartbeat` | info | 新增 | 见 4.2 |

节流器：`RuntimeLogThrottle`（key+窗口，共享静态实例），避免每失败一次刷一行。

### 4.2 心跳（`runner.heartbeat`，60s）

- 实现：`RuntimeHeartbeat`（独立 `PeriodicTimer`，随 durable bundle 生命周期）。
- 数据源：新增 `RuntimeCounters`（`ConcurrentDictionary<string, long>` 的薄封装，`Interlocked.Increment` 零分配计数）。计数点：relay 建立/失败、UDP 再注入 fallback/drop、capacity block、归因 miss、pass 失败、redirect 建立、forced refresh 次数。
- 字段：flowsActive、flowsCapacity、tcpSessions、tcpCapacity、udpSessions、pumpsRunning、pumpsDegraded、自上次心跳以来的 delta 计数、uptime。
- 泵状态来源：`LayeredCaptureRunner` 暴露只读快照（当前 generation 的泵计数），心跳拉取。

### 4.3 降噪

- `DurableCaptureBundle.UpdateUdpTargets` 的 no-MAC warn：记录上次输出的适配器集合，仅在首次或集合变化时输出（每次 refresh 刷屏 → 状态变化时一行）。

## 5. 兼容与风险

- **no-op 语义变化**：地址指纹进入 diff 意味着「主机地址变化但 NDISRD 未重建」现在会 rebuild generation（每次 rebuild 有亚秒级拦截中断）——这是修复目标本身；storm-guard 保证频率上限。
- **forced rebuild 风险**：失败率触发可能由上游真实故障（如 sing-box 挂）导致而非视图 stale——设计上可接受：多一次重建无害（幂等），三层防风暴兜底；`runner.forcedRefresh` warn 留痕供事后区分。
- **测试兼容**：`IInterceptionHealthSignal`/心跳全部可空注入，现有测试不动；新增测试用 fake signal 断言。
- **性能**：周期枚举为冷路径控制操作（驱动 gate 串行），30s 一次可忽略；心跳/计数为 Interlocated long 与 60s 一次聚合，可忽略。

## 6. 回滚

每个阶段独立提交；R1-B 可通过阈值不配置（禁用）单独回退；R1-A 周期可配置为 0 关闭。R2 为纯日志，无行为风险。
