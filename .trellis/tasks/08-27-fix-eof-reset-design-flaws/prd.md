# 修复导致 EOF/connection reset 频率异常增高的设计缺陷

## Goal

WinForward 在运行期间被代理应用的连接出现明显高于直连（或 ProxiFyre 类 WFP 方案）的
EOF / connection reset。经代码审计确认这不是单一 bug，而是若干设计层面缺陷的叠加。
本任务作为父任务，承载缺陷全景、子任务地图与跨子任务集成验收；具体修复在子任务中
独立完成并验证。

## 缺陷全景（源需求集）

按对 reset 频率的影响排序。每项的详细成因分析与解决方案见对应子任务 PRD。

### A. 数据面吞吐瓶颈（主因①）

- 位置：`src/WinForward.NdisApi/NdisCapture.cs`（单包 `ReadPacket` + 逐包 await）、
  `src/WinForward.Runtime/CapturePacketProcessor.cs`、`TcpProxyCoordinator.cs`、
  `TcpRedirectInjector.cs`、`NdisPacketActionExecutor.cs`。
- 机制：适配器处于 `SentTunnel|ReceiveTunnel` 时全部流量（含 pass 流量）都经过
  "内核→用户态→内核"往返；处理路径为每适配器单线程串行、单包 API、每包两次托管
  `ToArray()`、每次注入一次非池化 `NativeMemory.AllocZeroed(IntermediateBuffer)`、
  record class 反复分配。高 pps 下处理循环跟不上，WinpkFilter 驱动队列溢出导致静默
  丢包，MSTCP 重传耗尽后发出 RST，或应用层读超时表现为 EOF。
- 症状特征：reset 频率随负载 / 运行时长恶化，吞吐骤降伴随重传。

### B. 每流双临时端口消耗、无预算控制（主因②）

- 位置：`src/WinForward.Runtime/TcpRedirectListener.cs`、`TcpProxyRelay.cs`。
- 机制：每条被代理 TCP 流占用 redirect listener 与 SOCKS5 control 各一个临时端口，
  短连接叠加双份 TIME_WAIT（240s）后耗尽 Windows 临时端口池（约 16K）。listener
  bind 失败走 fail-closed block（客户端 connect 超时）；SOCKS5 connect 失败在 accept
  完成后向客户端注入 RST（"连接成功后立即被 reset"）。redirect 表容量 16_384 与端口
  池同数量级，起不到保护作用。
- 症状特征：高频短连接场景下新连接批量失败；表现为 connect timeout 或 established
  后立即 reset。

### C. 表项生命周期与拆除时序（主因③④）

- 位置：`src/WinForward.Runtime/TcpProxyCoordinator.cs`（`ObserveRelayCompletionAsync`
  即时拆表、`IdleExpirySweeper.cs`）、`FlowDispatcher.cs`、
  `NdisPacketActionExecutor.cs`（`NotRelevant → Pass`）。
- 机制一（迟到包回弹 RST）：relay 完成即移除 redirect 表项，客户端最后 ACK / FIN
  重传落入场外路径 `NotRelevant → Pass`，被原样发给真实服务器；服务器从未见过该
  连接，回 RST，RST 又被 Pass 回客户端，打断 TIME_WAIT / 半开连接。smoke 日志
  （smoke/winforward-service_2026-08-27-new.err.log）中 113 个 `outcome=notrelevant`
  证明该路径高频发生。
- 机制二（flow 表过期不对称）：coordinator 的 sweep 特意豁免 `Relaying` 会话（M4），
  但 `FlowDispatcher.RemoveExpiredFlows(5min)` 无同等豁免；静默活跃连接丢失决策后
  重新评估，若进程归因失败且 `fallbackAction: block`，连接被黑洞至 RTO 耗尽。
- 症状特征：连接关闭尾期偶发 reset；静默 ≥5 分钟的活跃连接在 block-fallback 配置下
  恢复流量时被重置。

### D. 次要竞态与放大因素（次因）

- 注入 RST 的确认序号 `clientInitialSeq+1` 可能过期（out-of-window 被丢弃，客户端
  挂起至超时，表现为慢 EOF）；
- SOCKS5 连接 30s 超时期间连接已"建立"，失败后才 RST，形成"连上 30 秒后被 reset"
  的体验；
- `TryReadPacket` 失败后 1ms 轮询未用 WinpkFilter 事件机制，交互流延迟抖动；
- forwarded 流 reverse 注入依赖捕获时的 `OriginAdapterHandle`，适配器重建后失效 →
  注入失败 → Blocked；
- 上游 SOCKS5 不可用期间全部新流 fail-closed（smoke 日志实录
  `Unable to connect to the configured SOCKS5 server`），上游质量问题被如实放大。

## 任务地图（子任务）

| 子任务 | 覆盖 | 状态 |
| --- | --- | --- |
| `fix-datapath-throughput` | 主因①：批量读包 + buffer 池化 + 分配削减 | done (archived) |
| `fix-port-budget` | 主因②：端口预算与连接上限保护 | done (archived) |
| `fix-table-lifecycle` | 主因③④：TIME_WAIT 宽限 + flow 表过期豁免 | done (archived) |
| `fix-minor-races` | 次因 D 组竞态修复 | done (archived) |

## Requirements

- 父任务自身不承载实现；各子任务独立完成"规划 → 实现 → 检查 → 归档"。
- 子任务的修复不得改变对外可见的配置语义（README 契约），除非子任务 PRD 明确声明。
- 修复不得引入新的 fail-open 路径：proxy 决策的流依旧只在明确安全时才 pass。
- 各子任务的方案若与 `.trellis/spec/backend/windows-ndisapi.md` 的既有约定冲突，
  需先更新 spec 再实现。

## Acceptance Criteria（跨子任务集成验收）

- [x] 四个子任务全部归档，各自验收标准通过。（archive/2026-08/，各自 trellis-check PASS）
- [x] smoke 复测（WinLtsc 实机，winrm）：`notrelevant` 113 → 11/14（-90%，剩量为
      capture 启动前已建立连接的合理水平，见 table-lifecycle/minor-races 验证记录）。
- [x] 两次 smoke（table-lifecycle、minor-races）均零 `listenerAllocation`、零 SOCKS5
      失败潮、零 warn/error；预算上限 tcpFlowCapacity=4096 先行截流。
- [x] 基准（Linux，fake reader 管线稳态 2.2-2.5M pps，35 gen0/百万包）已补充并
      记录；Windows 实机新旧 A/B 压测因需受控流量源未执行，为已知限制
      （datapath implement.md Step 5 记录）。
- [x] README 差异已同步：`tcpFlowCapacity` 字段说明 + Notes 宽限期说明
      （port-budget / table-lifecycle 子任务内完成）。

## Notes

- 本 PRD 的缺陷分析基于 2026-08-27 的代码审计，证据包括源码行号引用与
  smoke/winforward-service_2026-08-27-new.err.log 的统计（17399 包、33 条 relay、
  113 个 notrelevant）。
- 父任务最终集成 review 时应回到本 PRD 的 Acceptance Criteria 逐项核对。
