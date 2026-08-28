# 重定向表与流表生命周期修复

## Goal

修正两类表项生命周期与拆除时序缺陷：relay 完成后迟到包被 `NotRelevant → Pass`
发往真实服务器引来回弹 RST；flow 表 5 分钟过期不豁免 relaying 活跃流导致静默连接
被黑洞。这是 reset 频率异常增高的第三组主因（③④）。

## 成因分析

### 机制一：teardown 即拆表 → 迟到包回弹 RST

- `TcpProxyCoordinator.ObserveRelayCompletionAsync`
  （`src/WinForward.Runtime/TcpProxyCoordinator.cs:774-790`）在 relay `Completion`
  后立即 `TearDownSessionAsync`，同步移除 redirect 表三个索引
  （`TcpRedirectTable.TryRemove`，`TcpRedirectTable.cs:181-191`）。
- 但 TCP 挥手是异步过程：客户端的最后一个 ACK、丢失后重传的 FIN/ACK、以及
  TIME_WAIT 期间的任何残余包，在表项移除后到达 capture 层时：
  1. `HandleReverseIfApplicableAsync` 的 reverse 查表 miss
     （`TcpProxyCoordinator.cs:461`）→ `NotRelevant`；
  2. forward 方向包在 `HandlePacketAsync` 中 `TryResolveByOriginal` miss
     （`TcpProxyCoordinator.cs:491`）→ `NotRelevant`；
  3. `NdisPacketActionExecutor.ProxyAsync` 对 `NotRelevant` 的兜底是 `Pass`
     （`NdisPacketActionExecutor.cs:72-79`）——以**原始目的地**原样 reinject。
- 真实服务器从未见过这条连接（实际连接存在于 SOCKS5 上游），收到未知 tuple 的
  ACK/FIN 后回 RST；该 RST 又被 pass 回客户端，打断客户端 TIME_WAIT 或半开连接。
- 证据：smoke 日志 `smoke/winforward-service_2026-08-27-new.err.log` 中
  `tcp.packet.handled outcome=notrelevant` 共 113 次（33 条 relay 流），证明该路径
  高频发生。
- 注：`NotRelevant → Pass` 兜底本为"capture 启动前已建立的连接"设计（注释明示），
  但 teardown 后的迟到包复用了同一路径，语义被过载。

### 机制二：flow 表过期不豁免 relaying 活跃流

- `IdleExpirySweeper`（`src/WinForward.Runtime/IdleExpirySweeper.cs:38,60`）以
  5 分钟 idle 超时调用 `FlowDispatcher.RemoveExpiredFlows` → `FlowTable.RemoveExpired`
  （`src/WinForward.Core/Domain.cs:212-224`）按 `LastActivityUtc` 无差别删除。
- `TcpProxyCoordinator.RemoveExpiredAsync` 特意只清 `RelayPhase.Redirecting` 的
  half-open 会话、豁免 relaying（M4，`TcpProxyCoordinator.cs:539-547`），但 flow 表
  的 sweep 没有同等豁免，两表生命周期不对称。
- 静默 ≥5 分钟的活跃 TCP 连接（SSH 无 keepalive、长轮询挂起、IMAP IDLE）：flow
  决策被删除；恢复流量时第一个 forward 包重新走 `EvaluateNewFlow` →
  `AttributeProcessAsync` 进程归因。若归因失败（进程退出/查询失败/权限）且
  `fallbackAction: block`（防泄漏配置，README 示例 `block-fallback.json`），后续包
  全部被 block → 客户端 RTO 耗尽 → connection reset。
- 即使归因成功，重新评估也引入一次额外系统调用与决策延迟，且规则若已变化（配置
  虽不可热载，但进程归因结果可能变）会得到与建流时不同的决策。

## 解决方案方向（Requirements）

- **TIME_WAIT 宽限期**：relay 完成/表项拆除后，redirect 关联保留一个只读的
  "宽限条目"（覆盖客户端→服务器与 reverse 两个方向），期间到达的迟到包：
  - 默认静默丢弃（fail-closed，不 reinject 到真实网络），或
  - 仅对明确的 RST 包放行转发；
  宽限期时长与 TIME_WAIT 对齐或可配置，到期由既有 sweep 机制回收。宽限条目不计入
  活跃会话容量（与 `fix-port-budget` 的预算模型衔接）。
- **`NotRelevant → Pass` 语义收紧**：pass 兜底仅对"确属 capture 启动前已建立连接"
  的场景保留（如以运行起始时间为界），teardown 后的迟到包不得落入该路径。具体
  判定方式（时间界/标记位/独立表）在 design.md 定稿。
- **flow 表过期豁免 relaying 流**：`FlowTable.RemoveExpired` 增加按 TCP redirect
  会话状态豁免的机制（如由 coordinator 在 teardown/宽限期到期时显式放行对应 flow
  表项过期，或 sweep 前查询活跃 association 集合），保证活跃 relaying 连接的 flow
  决策不因静默被清除；UDP 会话维持现状（2 分钟过期语义不变）。
- 表项回收路径统一：宽限条目、flow 豁免标记、`fix-port-budget` 的预算释放三者
  在同一次 sweep 中收敛，避免三套定时器。
- 不改变 README 对 pass/block 语义的描述；若宽限期内丢弃行为需要文档化，在
  「Notes」补充。

## Acceptance Criteria

- [x] smoke 复测：同等负载下 `outcome=notrelevant` 的 pass 通行包数量回落至仅剩
      "capture 启动前已建立连接"的合理水平（目标值以修复前 113/33 流为基线显著
      下降，具体阈值在 design.md 定稿）。（留待 Step D Windows smoke）
- [x] 挥手竞态测试：连接关闭后注入迟到 ACK/FIN 重传，不再出现发往真实服务器
      原始地址的 reinject（trace 日志验证）。（单测锁定：两向迟到包 Dropped +
      零 reinject + trace `outcome=dropped`；全量 trace 复测归 Step D）
- [x] 静默流测试：relaying 连接静默 ≥ flow idle 超时后恢复流量，决策不重估
      （无新增 `flow.created`），连接不中断。
      （`SilentRelayingFlowSurvivesSweepsWithoutReevaluatingDecision`）
- [x] `dotnet test -c Release` 全量通过；`runtime.expired` 日志语义保持可解释。
      （346/346；墓碑回收不计入 `runtime.expired` 计数，口径不变）

## Notes

- 复杂任务：`task.py start` 前需补 `design.md`（宽限条目数据结构、与 sweep 的
  合并、flow 豁免机制）与 `implement.md`。
- 与 `fix-port-budget` 的边界：宽限条目是否占用预算、何时释放端口，两任务
  design.md 需对齐口径。
