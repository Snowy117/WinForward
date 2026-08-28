# TCP 重定向端口预算与连接上限保护

## Goal

控制每条被代理 TCP 流的双临时端口消耗，在高频短连接场景下防止 Windows 临时端口池
耗尽引发的 listener bind 失败（fail-closed block → 客户端 connect 超时）与 SOCKS5
connect 失败（accept 后注入 RST → "连接成功后立即被 reset"）。这是 reset 频率异常
增高的第二主因。

## 确认事实（设计前置研究，2026-08-27）

详见 `research/port-budget-feasibility.md`：

- `TcpRedirectTable.byTranslatedListener`（TcpRedirectTable.cs:125）强制 listener
  端口与会话 1:1，是多流共享 listener 的硬阻塞；`TryResolveByTranslated` 无调用方。
- 共享 listener 技术上可行，但需重构 accept 生命周期、selfTraffic 引用计数与表
  唯一性，风险高——**本任务不采用**（PRD 备选路线落选，最终方案"预算 + 快速失败"）。
- 每条被代理 TCP 流消耗 2 个本地端口（listener bind + SOCKS5 control connect），
  二者出自同一 per-transport 动态端口池（默认 49152-65535，Microsoft KB 929851）。
- capacity gate 已存在且位置正确（TcpProxyCoordinator.cs:98-105，超限
  `reason=capacity` + Blocked）；预算化只需将数据源从硬编码 16_384 换为配置派生值
  并补 info 级计数。
- 会话容量与表容量为两处独立默认 16_384（TcpProxyCoordinator.cs:46、
  TcpRedirectTable.cs:91），接线处未传参，互不联动。
- 配置新增可选字段的触达面共 7 处（DTO、TryValidate、ValidatedConfiguration、
  Program 接线、README、validate 输出、测试）。

## 成因分析

1. **每流双端口**：每条被代理 TCP 流占用两个本地临时端口：
   - redirect listener：`TcpRedirectListenerFactory.CreateAsync`
     （`src/WinForward.Runtime/TcpRedirectListener.cs:22`）`Bind(Any, 0)` 从临时端口
     池取一个端口；
   - SOCKS5 control：`TcpProxyRelayFactory.EstablishAsync`
     （`src/WinForward.Runtime/TcpProxyRelay.cs:27`）出站连接再取一个。
2. **TIME_WAIT 双份累积**：连接关闭后，listener 侧（本机 MSTCP 的 listener socket
   关闭，连接进入 TIME_WAIT）与 SOCKS5 侧各留下约 240s 的 TIME_WAIT 端口占用。
   Windows 临时端口池默认约 16K（49152-65535），高频短连接下 ~8K 条近期连接即可
   耗尽。
3. **容量保护形同虚设**：`TcpProxyCoordinator` 会话容量 16_384
   （`TcpProxyCoordinator.cs:45`）与端口池同数量级；`TcpRedirectTable` 容量默认
   16_384（`TcpRedirectTable.cs:91`）。表未满时端口可能已耗尽，反之亦然，两者没有
   联动的预算约束。
4. **失败形态直达用户**：
   - 端口耗尽时 listener bind 抛异常 → `SetupNewRedirectAsync` 捕获后
     fail-closed block（`TcpProxyCoordinator.cs:140-150`）→ 客户端 SYN 无响应，
     应用 connect 超时；
   - SOCKS5 connect 失败（端口耗尽或网络问题）→ `RunAcceptLoopAsync` 的
     relay 建立异常 → `HandleRelaySetupFailureAsync`
     （`TcpProxyCoordinator.cs:655-661`）→ `TcpResetBuilder` 向客户端注入 RST，
     此时客户端已完成握手，应用看到"连接成功后第一个读写立即 reset"。

## 解决方案方向（Requirements）

- 引入统一的连接/端口预算：以可配置的并发被代理 TCP 流上限（低于临时端口池安全
  水位，考虑 2×端口/流 + TIME_WAIT 余量）约束 `HandleSynAsync` 的准入；超限时保持
  fail-closed block 语义，但应产生可观测的计数与日志（区别于错误型失败）。
- 使会话表容量与端口预算联动而非独立默认值；配置校验阶段（`WinForward.Configuration`）
  拒绝或钳制超出安全水位的容量组合，并在 `validate` 输出中体现。
- 评估消除双端口的方案可行性（作为 design.md 的备选研究项，不作为 PRD 硬性要求）：
  - listener 复用（多路复用单 listener + 按 4-tuple 分流）或
  - SOCKS5 control 连接池/流水线复用；
  若不可行或风险过高，记录结论并以"预算 + 快速失败"为最终方案。
- SOCKS5 不可达/失败时的用户可见行为应可区分：注入 RST 前置检查（例如 connect
  阶段即失败则不解 accept、直接 RST 未完成的握手），避免"established 后才 reset"
  的窗口拉长（与 `fix-minor-races` 中 30s 超时项衔接，边界在两任务 design.md 中
  划清）。
- 不改变 README 中 fail-closed 的承诺语义；预算超限属于显式容量管理，需在 README
  「Notes」或配置文档补充说明。

## Acceptance Criteria

- [x] 存在单一事实来源的并发流预算（Program 接线 coordinator/table 同传
      `validated.TcpFlowCapacity`，测试锁定）；`reason=listenerAllocation` 路径保留，
      归零验证归 Windows smoke（见 Notes）。
- [ ] 高频短连接压测（如持续创建/关闭 HTTP 连接 ≥ 2×TIME_WAIT 时长）下，新连接
      失败率仅在预算超限时出现，且恢复时间与预算释放同步。（需 Windows 实机，
      Step 4 可选项）
- [x] 预算超限时有计数器/日志可观测（info 级 `tcp.redirect.capacity` 摘要 +
      trace 级 `reason=capacity` 明细，测试断言）。
- [x] `WinForward validate` 对容量配置给出一致性校验结果（三态实测：默认/警告/
      拒绝）。
- [x] `dotnet test -c Release` 全量通过（332/332）；README 配置说明同步更新。

## Notes

- 复杂任务：`task.py start` 前需补 `design.md`（预算模型、listener 复用可行性
  结论）与 `implement.md`。
- 与 `fix-table-lifecycle` 的边界：本任务只管"准入与端口"，表项何时释放归
  `fix-table-lifecycle`。
