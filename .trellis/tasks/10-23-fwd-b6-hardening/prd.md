# B6：Forwarded 收口（正确性、多入口、门禁、性能与验收）

## Goal

关闭 B3/B4b/B5 的正确性遗留，补齐多入口/多 L/热替换/观测，建立独立 Linux 门禁，
完成 A5 成本基线与文档/最终验收，使 Forwarded SOCKS5 网关达到可交付状态。

## Background

- B1–B5 已完成：规则层、配置、L2 骨架、TCP DNAT/回程、UDP 关联/SOCKS5 UDP。
- B3/B4b/B5 留下若干 B6 阻断/测试项：
  - U7 decoy `available=1` 原帧 permit 异常；
  - 分片测试仍是 MTU 触发，不是 raw/显式 9 片；
  - reject 仍是 suite 替身，不是产品 `ForwardedUdpRelay`；
  - `acked` 仍用客户端实收字节，不是真实 TCP ACK；
  - stop 等待 outstanding 仅 2s，超时仍销毁池（理论 UAF）；gate 卸载只查 `WfPr`；
  - 混合 NET_BUFFER/入口同流、`return_miss` 混合命中+miss、分片全局 block 范围；
  - capacity=8 测试构建，生产 4096 真满未压测；host UDP gate 单条 flake；
  - LWF 对注入帧可见性未测；回程 key 不含 server（一个客户端端口一个目的）。
- 多入口/多 L/多目的/热替换/观测尚未做；A5 成本未测；D7 产品决策未拍板。

## In Scope

1. **正确性收口（B6a）**：
   - U7 decoy `available=1` 复现/定位/修复；原帧必须 block。
   - 分片 raw/显式构造 9 片（IPv4 首片/非首片、IPv6），精确断言。
   - reject 改为产品 `ForwardedUdpRelay` 的 ASSOCIATE 拒绝 E2E。
   - `acked` 改用真实 TCP ACK（服务端/客户端 `TCP_INFO`）。
   - stop outstanding 超时不销毁池/告警；gate 加 `WfIj`/`WfTr` 池检查。
   - 混合 NET_BUFFER 流/入口同流校验或 fail-closed；`return_miss` 计数。
   - 分片全局 block 范围收窄（按入口/规则）或明确风险。
   - capacity=8/4096 结论；host UDP 单条 flake 定位或记录。
   - LWF 关闭注入可见性：带外/可恢复会话复测，或明确未验证。
2. **多入口/多 L/多目的/热替换（B6b）**：
   - 至少两个入口适配器/NIC，各自的 L/listener 映射。
   - 回程 key 含 server，支持同一客户端端口多目的。
   - 热替换：旧流保持旧 upstream，新流新规则，generation 可回读。
   - `StateCommand` 增加 forwarded 只读行（installed/generation/rule_count/fallback/计数）。
3. **门禁/性能（B6c）**：
   - 独立 Linux 门禁：forwarded-tcp/udp 客户端脚本、verify 端口、CI 禁止 `--no-verify`。
   - `gate.ps1` 可选纳入 forwarded-udp/fragment/reject（不重装驱动也可跑的部分）。
   - A5 成本：内核 sink/iperf3，同 pps/包大小；记录基线，不设硬性 kill criteria。
4. **文档/验收**：
   - `AGENTS.md`（ABI v6、Forwarded 状态）、`wfp-architecture.md`、驱动/用户态 README、spec。
   - D7（forwarded 是否正式从 NDISAPI 迁移）产品决策记录。
   - 最终 gate、归档父任务、`update_goal complete`。

## Out of Scope

- 新数据面功能（TCP/UDP 已实现）。
- External switch/物理 NIC/嵌套 VM、VMQ/SR-IOV。
- WinForward.Runtime 集成（B6 评估，不强制换回）。
- ARM64。

## Requirements

- **R1 正确性**：U7/分片/reject/acked/stop/混合 NBL/return_miss 逐项关闭或有明确证据与风险记录。
- **R2 多入口**：至少两个入口/L 可用；回程按表项正确还原。
- **R3 热替换**：旧流不串新上游；新流按新规则。
- **R4 观测**：StateCommand/事件/计数器可读 forwarded 状态。
- **R5 门禁**：独立 Linux 门禁可重复；CI 禁止 `--no-verify`。
- **R6 性能**：A5 基线可引用（同 pps/包大小），不伪装。
- **R7 文档**：代码/配置/测试/边界与 spec 一致。
- **R8 验收**：host TCP/UDP、forwarded TCP/UDP、gate 全绿；已知边界写清。

## Acceptance Criteria

- [ ] **A1 U7**：原帧 permit 异常复现/修复，或明确 B6 阻断不通过。
- [ ] **A2 分片**：raw/显式 9 片，IPv4 首片/非首片、IPv6；精确断言。
- [ ] **A3 reject**：产品 `ForwardedUdpRelay` ASSOCIATE 拒绝 E2E，不直连。
- [ ] **A4 acked**：真实 TCP ACK 语义，不拿实收字节冒充。
- [ ] **A5 stop/UAF**：outstanding 超时不销毁池/告警；gate 检查 `WfIj`/`WfTr`。
- [ ] **A6 混合/计数**：混合 NBL/入口同流 fail-closed 或文档；return_miss/分片范围一致。
- [ ] **A7 多入口/多 L/多目的**：两条入口、回程多目的不串。
- [ ] **A8 热替换**：旧流保持旧 upstream；新流新规则；generation 回读。
- [ ] **A9 观测**：StateCommand 输出 forwarded 状态。
- [ ] **A10 门禁**：独立 Linux 门禁 PASS；CI 禁 `--no-verify`。
- [ ] **A11 性能**：A5 基线记录。
- [ ] **A12 文档/验收**：spec/README/AGENTS 一致；最终 gate 全绿；归档。

## Notes

- 分阶段：B6a 正确性 → B6b 多入口/热替换/观测 → B6c 门禁/性能/文档/验收。
- 不把“测试替身 PASS”当产品验证；U7/reject/acked 必须产品级或明确 B6 阻断。
- 已知环境限制（无 root/External switch）写清，不伪装。
