# 技术设计：B3 L2 可见性与注入 spike

## 1. 验证拓扑（按可用性降级）

| 方案 | 拓扑 | 能回答什么 | 前提 |
|---|---|---|---|
| A | VM 内 Hyper-V Internal switch + 子 VM 客户端 | vSwitch/VM 入口的 L2 可见性、注入回 VM | 嵌套虚拟化可用 |
| B | 物理网卡 + Linux 主机（192.168.100.4）作为客户端 | 物理入口的 L2 可见性、注入回客户端 | 无 |
| C | 主机自身 vEthernet / loopback 对打 | 本机栈接受注入、回程 SNAT | 无 |

先做侦察，按 A → B → C 选择；结论里写明覆盖边界（A 覆盖 vSwitch，B/C 不覆盖）。

## 2. 最小 L2 callout

在现有驱动的临时副本/分支上：

- 注册 `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE` / `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`
  （若字段/可见性不足，再试 `..._ETHERNET`）。
- classify 只做：计数（方向、接口索引）、可选日志/ETW；返回 `FWP_ACTION_PERMIT`。
- 注入 handle：`FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_L2, &handle)`。
  参数顺序是 `(ADDRESS_FAMILY addressFamily, UINT32 flags, HANDLE* handle)`。
- 2A 阶段说明：注入阶段原帧仍 PERMIT，B4 必须消费/改写原帧，不能照搬本 spike。
- 防循环：`FwpsQueryPacketInjectionState0`；自身注入的帧直接 permit。本 spike 未实现，
  靠端口改写避免自触发；B4 必须显式实现。
- 不注册正式 provider/filter 语义，不改现有 ALE callout 与 IOCTL。

## 3. 注入验证

1. 客户端发 TCP SYN 到某个地址；
2. L2 callout 在 inbound 看到 SYN，记录源/目的 MAC、IP、端口；
3. 构造 DNAT 后的帧（目的地址改为本机地址、目的端口改为 listener），
   `FwpsInjectMacReceiveAsync0` 注入 inbound；
4. 本机 listener 是否 accept / TCP 栈是否回 SYN-ACK；
5. 把 SYN-ACK 用 `FwpsInjectMacSendAsync0` 注入 outbound 到客户端；
6. 客户端是否收到。

> 本 spike 的注入是**复制注入**：classify 对原帧始终 PERMIT，原帧继续走原路径；
> record 33/34 是原 4500 SYN-ACK + 注入的 4501 副本。A3/A4 只证明“注入能力”，不证明
> “DNAT 重定向”；B4 必须消费/改写原帧。

失败要记录 NTSTATUS、注入方向、接口索引、帧长度；不要只写“失败”。

## 4. 成本

- pass 路径：固定 pps/吞吐下，比较“L2 callout permit”与“现有 NDISAPI 捕获+回注”的
  进程/系统 CPU 量级（不要求同条件精确）。
- 记录包大小、pps、CPU 采样方式；结论只用于“值不值得继续 L2”的判断。
- A5 本轮不可测；0.05% 不可引用。补测归 **B4/B6**：内核侧 sink 或 iperf3，判据与包版
  同 pps/同包大小下的 CPU 量级对照；58.1% 只作量级参考。

## 5. 成功/失败判据

- **技术可行性成功**：A2/A3/A4 全部有证据；D1 = 技术可行性通过，B4 主路线采用 WFP L2。
  继续 L2 的条件（正式表述见 `research/spike-l2.md` §9）：
  1. A5 成本在 B4/B6 用内核侧 sink 或 iperf3 补测，判据与包版同 pps/包大小的 CPU 量级对照；
  2. B4 必须消费/改写原帧，不能 PERMIT 原帧再注入副本（本 spike 是加法注入）；
  3. B4 用流/连接上下文触发，不是端口触发；
  4. B4 必须显式 `FwpsQueryPacketInjectionState0` 防环；
  5. LWF 对注入帧的可见性未测，B4 要补测或防御；
  6. VLAN 偏移与出向 layer 59 要按实际 L2 头处理。
- **D7**：forwarded 是否正式从 NDISAPI 迁到 WFP L2 是产品决策，另行拍板；本 spike
  只证明技术可行性。
- **部分成功**：可见性有、注入失败 → 记录失败码；D1 可能是 LWF，或先修注入路径再复测。
- **失败**：看不到目标入口 → D1 = LWF（或 vSwitch 扩展），记录原因与替代方案。
- **A5 说明**：本轮 0.05% 不可引用；不作为本次 spike 的阻断，转 B4/B6。

## 6. 清理

- 卸载临时驱动；恢复 VM 快照/源码；确认 `src/driver` 无 spike 残留；
- research 文档保留在任务目录，临时二进制/日志不提交。
