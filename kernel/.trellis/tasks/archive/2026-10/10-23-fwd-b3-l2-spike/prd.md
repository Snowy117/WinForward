# B3：L2 可见性与注入 spike

## Goal

在 Windows VM 上回答 D1：现有 WFP 驱动增加 MAC 层（L2）callout 后，能否看到目标入口的
forwarded 流量（Hyper-V Internal switch 的 VM 客户端，或退化为物理网卡 + Linux 客户端）、
DNAT 后能否把帧注入本机栈交给 listener、回程能否把帧注入回客户端；并粗测 pass 路径成本。
产出 `research/spike-l2.md` 与明确的 D1 结论（继续 WFP L2，或转独立 NDIS LWF）。

## Background

- 父任务 D1 优先复用现有 `wfprobe.sys` 框架，在 WFP MAC 层做 callout + L2 注入；若可见性/
  注入不达标，转 NDIS LWF。
- 当前 Windows VM 是 WinLTSC，本身可能运行在 Hyper-V 里；嵌套虚拟化不一定可用。
  **先侦察环境，再决定用 Internal switch + 子 VM，还是退化为物理网卡 + Linux 客户端。**
- spike 代码只用于验证，不进入正式构建、不提交到主干；产出是 research 文档与结论。

## In Scope

1. VM 环境侦察：Hyper-V 功能/嵌套虚拟化、可用 vSwitch、可用客户端（子 VM、Linux 主机）、
   测试签名/驱动加载状态。
2. 最小 L2 callout：在现有驱动源码的临时副本/分支上，注册
   `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE` / `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`，
   只做计数 + permit，不改正式 classify 路径。
3. 可见性验证：客户端发包，确认 callout 能看到（计数、方向、接口索引、MAC/IP 头）。
4. 注入验证：
   - `FwpsInjectMacReceiveAsync0`：DNAT 后的 SYN 送回本机栈，listener 能 accept；
   - `FwpsInjectMacSendAsync0`：回程帧送回客户端，客户端收到 SYN-ACK。
5. 粗略成本：pass 路径的每包/吞吐量级（不要求精确 benchmark）。
6. `research/spike-l2.md`：环境矩阵、可见性、注入、成本、D1 结论、后续建议。

## Out of Scope

- 正式驱动实现、TCP/UDP 数据面、用户态中继（B4/B5）。
- 完整性能 benchmark、长时间稳定性。
- 修改 `src/common`、用户态配置、正式 IOCTL/ABI。
- 把 spike 代码合入主干。

## Requirements

- **R1 先侦察后动驱动**：先确认 Hyper-V/客户端可用性；不可用就明确记录并给出替代验证方案，
  不要硬造环境。
- **R2 最小改动**：spike 代码在临时副本/分支；正式 `src/driver` 不受影响；不新增正式 ABI。
- **R3 可见性证据**：计数、日志或调试输出能证明帧来自目标入口/方向；不是“看起来能跑”。
- **R4 注入证据**：listener accept 或客户端收到 SYN-ACK 的实际日志/抓包；失败要记录失败码。
- **R5 成本量级**：pass 路径的每包成本或吞吐量级；与包版“捕获+用户态回注”的 58.1% 对照
  （只作量级参考，不追求同条件）。
- **R6 清理**：卸载临时驱动、恢复源码；不把临时文件/驱动二进制留在仓库。

## Acceptance Criteria

- [ ] **A1 环境矩阵**：记录 Hyper-V 功能、嵌套虚拟化、vSwitch、客户端、测试签名状态；
      不可用项写明替代方案。
- [ ] **A2 可见性**：L2 callout 能看到目标入口的 inbound 帧，计数 > 0，方向/接口可区分。
- [ ] **A3 入栈注入**：DNAT 后 `FwpsInjectMacReceiveAsync0` 能让本机 TCP 栈/ listener 看到 SYN。
- [ ] **A4 回程注入**：`FwpsInjectMacSendAsync0` 能让客户端收到 SYN-ACK（或等价的回程帧）。
- [ ] **A5 成本**：给出 pass 路径每包/吞吐量级；记录测量条件。
- [ ] **A6 结论**：D1 = 继续 WFP L2 / 转 NDIS LWF，附证据与失败项；research 文档落盘。
- [ ] **A7 工作区干净**：正式 `src/driver` 无 spike 残留；临时驱动已卸载。

## Notes

- 先做 VM checkpoint；蓝屏/崩溃先隔离环境，不要直接归因驱动。
- 若嵌套虚拟化不可用，退化验证“物理网卡 + Linux 客户端”仍能回答 L2 可见性/注入问题，
  只是不覆盖 vSwitch 场景；在结论里写明覆盖边界。
- 不要为了 spike 改 `src/common` 或用户态正式代码。
