# Design: TCP 重定向端口预算与连接上限保护

关联 PRD: `prd.md`。父任务: `08-27-fix-eof-reset-design-flaws`。研究依据:
`research/port-budget-feasibility.md`（下称 [R§n]，对应其第 n 节）。

## 1. 方案总纲

采用「统一预算 + 快速失败」，不做 listener 复用：

- 新增单一配置字段 `tcpFlowCapacity`，作为并发被代理 TCP 流的唯一预算来源；
- coordinator 会话容量与 `TcpRedirectTable` 容量均由该字段派生（消除两处独立
  16_384 的不联动，[R§6]）；
- 超限路径复用既有 capacity gate（TcpProxyCoordinator.cs:98-105，位置已正确），
  行为保持 fail-closed `reason=capacity` + Blocked，新增 info 级计数可观测。

否决项：多流共享 listener / SOCKS5 连接池——`byTranslatedListener` 的 1:1 语义是
硬阻塞（[R§1]），重构面横跨 accept 生命周期、selfTraffic 引用计数与表唯一性
（[R§2]），收益（端口减半）不抵风险；`TcpRedirectTable.TryResolveByTranslated`
当前无调用方，未来若做共享化需先重设计该索引。

## 2. 预算模型

- Windows 动态端口池默认 16,384（49152-65535，per-transport，KB 929851）。
- 每流消耗 2 端口（listener + SOCKS5 control），关闭后各有 TIME_WAIT 滞留。
- 默认预算 `tcpFlowCapacity = 4096`：16,384 × 50%（TIME_WAIT 与其他应用余量）
  ÷ 2（端口/流），向上取整。
- 校验规则：`1..8192`；`> 4096` 时 `validate` 与 `run` 启动期输出 warn（提示端口
  池压力），`> 8192` 或 `< 1` 拒绝（配置错误，exit 1）。
- 该模型是保守默认而非硬保证：真实端口耗尽取决于整机其他消耗，预算把 WinForward
  自身压到安全水位。

## 3. 配置契约（7 处触达，[R§4]）

```json
{ "tcpFlowCapacity": 4096 }
```

- 顶层可选字段，省略 = 4096；未知属性拒绝语义不变。
- 触达清单：
  1. `ConfigurationModels.cs`：DTO 字段（int?，null → 默认）；
  2. `TryValidate`：范围与警告校验（warn 不阻断，拒绝才阻断）；
  3. `ValidatedConfiguration`：归一化后的只读属性（int，非空）；
  4. `Program.cs` 接线：coordinator 与 table 构造传参（替换两处硬编码 16_384）；
  5. README：Configuration 节补字段说明（默认值、范围、警告语义）；
  6. `WinForward validate` 输出：包含解析值与（若有）警告；
  7. 测试：ConfigurationTests 补字段全矩阵。

## 4. 容量派生与 gate 改造

- `TcpProxyCoordinator` 构造函数新增 `capacity` 参数（会话上限 = tcpFlowCapacity）；
  `TcpRedirectTable` 构造函数新增 `capacity` 参数（表上限 = 同值）。两处默认值
  保留 16_384 签名兼容（既有测试零改动），生产接线由 Program 显式传同源值。
- gate 判定（`TryClaimSessionCapacity` 语义不变）数据源换成构造传入值。
- 新增计数：超限计数器（long，Interlocked），随既有 info 摘要周期输出
  （沿用 IdleExpirySweeper 的周期或 lifecycle 摘要通道，[R§5]）；trace 级每次
  拒绝已有事件，无需新增。

## 5. 边界与不改动项

- SOCKS5 建立失败的 30s 体验与前置检查归 `fix-minor-races` D3；本任务只保证
  预算超限的拒绝是**即时**的（SYN 处理路径同步判定，无网络等待）。
- `reason=listenerAllocation` 路径（TcpProxyCoordinator.cs:148）保留——预算把
  概率压到工程噪声级，不承诺归零（验收见 prd）。
- UDP 会话容量不动；`fallbackAction`/规则语义不动。

## 6. 兼容性与回滚

- 省略 `tcpFlowCapacity` 的既有配置行为变化：容量从 16_384 收紧到 4,096——这是
  **有意的保护性收紧**，README 明示；需要旧行为的用户显式配置更大值（≤8192）。
- 单提交点：Configuration 层 + 接线 + gate + 测试一个原子提交，可整体 revert。

## 7. 验证策略

- 单测：配置矩阵（默认/边界 4096、8192/拒绝 0、8193、非整数）、容量派生一致性
  （coordinator 与 table 同源）、超限 Blocked + reason=capacity + 计数递增。
- `WinForward validate` 手测：省略字段、超范围值、警告值三态输出。
- Windows smoke（可选，winrm 复用 wf-batch-test 部署）：短连接风暴下观察
  info 计数与无 listenerAllocation。
