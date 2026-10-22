# 执行计划：B3 L2 可见性与注入 spike

> 先侦察、再动驱动。侦察不通过就停下来报告，不要硬造环境或反复装驱动。

## 0. 侦察（不装驱动）

- [x] `windows-vm status`；必要时 `windows-vm connect` 建会话。
- [x] VM 内确认：
  - `Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All`（是否安装/启用）
  - `systeminfo` 或 `Get-ComputerInfo` 的 Hyper-V 要求字段（嵌套虚拟化是否可用）
  - `Get-VMSwitch`、`Get-NetAdapter`、`Get-NetIPConfiguration`
  - `bcdedit /enum {current}`（testsigning）
  - 可用客户端：能否建子 VM；Linux 主机 192.168.100.4 是否可达
- [x] 记录到 `research/environment.md`，选拓扑 A/B/C（见 design §1）——实际选 A′（兄弟 VM 客户端）。
- [x] 如果 A/B 都不可用或风险过高：停止，报告替代方案与对 D1 的影响——A/B 不可用，按 A′ 继续。

## 1. 准备

- [x] 看 `windows-vm --help` 是否支持 checkpoint；有就做，没有就记录 reset 兜底。
- [x] 在 VM 上复制 `src/driver` 到临时目录（如 `C:\workspace\spike-l2`），不直接改仓库。
- [x] 用现有构建脚本（`tools/windows/WfBuild.psm1` / `load-driver.ps1`）确认基线能编译/安装。

## 2. 最小 L2 callout

- [x] 在临时副本里注册 inbound/outbound MAC 层 callout，只计数 + permit；
      可选：只对 TCP SYN 打印，避免刷屏。
- [x] 注入 handle（`FWPS_INJECTION_TYPE_L2`）；本轮靠端口改写避免自触发。
- [ ] 显式 `FwpsQueryPacketInjectionState0` 防循环（本轮未实现，转 B4 硬约束）。
- [x] 构建/测试签名/安装；记录加载与卸载是否干净。

## 3. 可见性

- [x] 客户端发 TCP SYN / ping；确认 callout 看到 inbound 帧（计数/日志含方向、接口索引、
      源/目的 MAC、IP、端口）——报告只摘录五元组/ifIndex；MAC/IP 原始快照未保存。
- [x] 记录看不到的入口/方向（如果存在）。

## 4. 注入

- [x] 构造 DNAT 后的 SYN（目的改本机地址/端口，重算校验和），
      `FwpsInjectMacReceiveAsync0` 注入 inbound；确认本机 TCP 栈/listener 看到。
      注意：本轮是复制注入；原帧未消费，转 B4 硬约束。
- [x] listener 回 SYN-ACK；`FwpsInjectMacSendAsync0` 注入 outbound 回客户端；
      确认客户端收到。
- [x] 失败记录 NTSTATUS、方向、接口索引、帧长度；不要只写“失败”。

## 5. 成本

- [ ] pass 路径在固定 pps/吞吐下的 CPU 量级；记录包大小、采样方式——转 B4/B6。
- [ ] 与包版“捕获+用户态回注”的 58.1% 只作量级对照，不追求同条件——转 B4/B6；0.05% 不可引用。

## 6. 产出与清理

- [x] `research/spike-l2.md`：环境矩阵、拓扑、可见性证据、注入证据、成本、D1 结论、
      覆盖边界（是否覆盖 vSwitch/External switch）。
- [x] 卸载临时驱动；确认 `get-windbg`/驱动列表无残留；恢复源码副本。
- [x] 确认正式 `src/driver` 无 spike 残留；临时二进制/日志不入库。
- [x] 报告：D1 = 技术可行性通过 / B4 主路线 WFP L2，附证据与未覆盖项；A5 转 B4/B6。

## 回滚点

- 侦察失败：不装驱动，直接报告。
- 驱动崩溃/蓝屏：先卸载/回滚到上一快照，隔离环境；不把崩溃直接归因驱动。
- 任何阶段都可以停在 research 结论，不强行进入下一阶段。
