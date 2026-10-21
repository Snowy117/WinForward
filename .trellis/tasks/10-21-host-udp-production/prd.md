# Host UDP 正式接入驱动：规则化 redirect、per-datagram context 与生命周期

## Goal

把 10-21-host-udp-route-a-probe 验证过的 UDP local redirect 从实验探针转为驱动的正式能力：
UDP 与 TCP 一样参与规则匹配，按规则放行 / 重定向 / 拦截；原始目的地通过 redirect context
随每条 datagram 交回用户态；删除探针专用的临时 ABI、回程注入与观测层；补齐 ABI、事件、计数、
宿主测试与 VM 门禁。

本仓库只交付**驱动侧能力与规则编译**。用户态 UDP 监听 / 中继 / SOCKS5 UDP ASSOCIATE / UoT
仍由 WinForward 负责，本任务只把驱动与 ABI 准备好，并用 `wfprobe` 做端到端验证。

## Background

### 探针已经确认的事实

- `ALE_CONNECT_REDIRECT_V4` 对 UDP 的 connected（`connect` + `send`）与 unconnected
  （`sendto`）两种模式都生效，首包被改道到 listener。
- listener 通过 `setsockopt(IPPROTO_IP, IP_WFP_REDIRECT_CONTEXT=70)` + `WSARecvMsg`
  逐 datagram 拿到驱动设置的 `localRedirectContext`（36 字节），原始目的地、协议号、PID
  与客户端一致。
- connected 客户端接受 relay 用 listener socket 普通 `sendto` 回来的包，**不需要伪造源地址，
  也不需要驱动注入**；探针的 `FwpsInjectTransportReceiveAsync0` 返回 `STATUS_NOT_ACCEPTED`
  只说明那条不必要的路径走不通。
- 回程 loopback 包在 `DATAGRAM_DATA`（inbound / outbound）与 `INBOUND_IPPACKET_V4` 均不可见，
  因此探针里的观测层与注入模块没有产品价值。
### Spike 结论（2026-10-21）

完整证据见 [spike-udp.md](../10-21-host-udp-spike/research/spike-udp.md)。

- **classify 按关联**：`ALE_CONNECT_REDIRECT_V4/V6` 对 UDP 的“本地端点 + 远端目的地”关联只
  classify 一次；同一 socket 向同一目的地发 N 条只产生 1 次 classify/redirect/context。
- **context 生命周期**：context 随客户端 socket 关闭立即释放（<1s）；socket 保持打开时
  150s 内不会因 idle 释放；`sc stop` 后本实例全回收。池断言必须从干净启动开始。
- **UDP block**：`ALE_AUTH_CONNECT` 的 BLOCK filter 能真正丢掉 UDP 报文，但应用侧**静默**——
  `connect` / `send` / `sendto` 全部成功，对端收不到，随后 `recv` 超时。TCP 对照仍返回
  `AccessDenied(10013)`。
- **IPv6 / dual-mode**：工具扩展后全部通过；context 的 family 取决于实际层（v4-mapped
  unconnected 走 V4，v4-mapped connected 走 V6），不能笼统断言 family=6。
- 未覆盖：已授权关联在规则热替换后是否继续放行；cmsg_level 精确值；IPv6 scope id。

### 当前代码状态

- [probe_classify.cpp](../../../../src/driver/probe_classify.cpp) 已有实验分支
  `udp_probe_candidate`（`probe_classify.cpp:393`），在四道闸之后调用实验模块，且**不参与规则表**。
- 实验模块 `udp_probe.cpp` / `udp_redirect.cpp` / `udp_ippacket.cpp` / `udp_inject.cpp`
  与临时 IOCTL 0x880 / 0x881 / 0x883（`probe_ioctl.h:253`）、`DATAGRAM_DATA` callout、
  `FwpsInjectionHandleCreate0` 都是探针脚手架，注释明确写着“不是正式 ABI”。
- 规则 blob 只接受协议 `0`（任意）与 `6`（TCP）：`rule_blob.h:38`、`rule_blob.h:151-154`。
- C# 侧 `RuleProtocol` 只有 `Any` / `Tcp`（`RuleProtocol.cs:4`），配置解析只接受
  `any` / `tcp`（`PolicyConfigurationDocument.cs` 的 `ParseProtocol`），编译器拒绝其他协议。
- block filter 对 `protocol=any` 固定按 TCP 下发（`block_filters.cpp:77`），注释写明
  “避免 auth filter 把 UDP connect 一起拦掉”。
- 用户态读取 redirect context 的路径拒绝 UDP：`redirect_context.h:105`
  （`try_read_original_destination` 只接受 TCP）与 `RedirectDestination.cs:51`
  （`ProtocolTcp` 比较）。
- `redirect_context` 本身已经有 `transport_protocol::udp = 17`，布局不需要改。
- 驱动已经同时注册 `ALE_CONNECT_REDIRECT_V4/V6`；UDP 不需要新增 callout 层。
- WinForward 产品侧规则里 `Protocol` 是可选的；不写协议表示匹配所有协议（含 TCP），
  所以内核版的 `protocol=any` 应同时覆盖 TCP 与 UDP。

### 产品语义约束

- block 仍然不能在 redirect 层实现，必须由 `ALE_AUTH_CONNECT` 的 `FWP_ACTION_BLOCK`
  filter 执行（`.trellis/spec/backend/wfp-architecture.md` §7.9）。
- 重定向失败保持 fail-open 直连并计数，与 TCP 相同；不得把“失败”写成“已阻断”。
- 一条规则仍然只有一个协议字节；`any` 表示 TCP + UDP，`tcp` / `udp` 表示单一协议。

## In Scope

1. **规则 blob 与 C# 编译链支持 UDP**
   - C++：新增 `rule_protocol_udp = 17`，`is_known_protocol` 接受 UDP，matcher 增加协议
     一致性断言。
   - C#：`RuleProtocol.Udp`、`PolicyConfigurationDocument` 接受 `"udp"`、编译器校验、
     `RuleOrderValidator` 的协议重叠判定与描述文本。
   - 宿主测试：blob 合法性、matcher、差分、规则顺序、非法协议负向用例。
2. **classify 正式支持 UDP**
   - 删除实验分支与实验模块；TCP / UDP 走同一条“四道闸 → 规则匹配 → 动作”路径。
   - UDP 匹配到 `permit` 时按普通放行记录；匹配到 `redirect` 时复用现有
     `redirect_classification`，context 的 `protocol` 写 `udp`。
   - 非 TCP / 非 UDP 协议继续走 `permitted_non_tcp` 早退。
   - 不为 UDP 建立驱动侧 flow 表；context 所有权仍归 WFP。
3. **block filter 覆盖 UDP**
   - `protocol=udp` 的 block 规则生成 UDP auth filter；`protocol=tcp` 仍只覆盖 TCP。
   - `protocol=any` 必须同时覆盖 TCP 与 UDP：按 6/17 各下发一条 filter（见 design.md §5.1），
     不写协议条件会误拦 ICMP；`max_block_filters` 相应为 128。
   - `fallback_action=block` 同样按 6/17 展开，ICMP 不被拦。
   - 规则顺序冲突校验与错误文案同步更新。
4. **redirect context 与用户态读取**
   - 保持 36 字节布局；`try_read_original_destination` 接受 TCP / UDP；C# 侧
     `RedirectDestination` 接受两种协议并暴露协议字段；TCP 中继显式要求 TCP。
   - `wfprobe udp-listen` / `udp-relay` / `udp-client` 从实验命令转为正式验证工具，
     删除 `--inject` / `--return-mode` / `--filter-port` / `--target` 等实验参数。
5. **事件与计数**
   - `probe_event` 增加协议字段（利用现有 padding，尺寸保持 64 字节），ABI 版本提升。
   - `probe_stats` 增加 `udp_classify_calls` / `udp_permitted` / `udp_blocked` /
     `udp_redirected` 四个计数器，保证在混合流量下区分 UDP 的 permit / redirect / block。
   - 事件与计数的命名、含义写进驱动 README 与 ABI 文档。
6. **IPv4 与 IPv6**
   - `ALE_CONNECT_REDIRECT_V4/V6` 都覆盖；redirect target 继续复用
     `probe_build_redirect_target` 的 ANY→loopback / 本地地址分支。
   - dual-mode socket 至少有一条用例，确认不会退回 `permitted_non_tcp`。
7. **VM 端到端验证与门禁**
   - 用真实规则 blob 复现探针的 A1–A4；新增 block、permit、fallback、IPv6、生命周期用例。
   - 池标签（`WfPr` / `WfTr`）在重复 datagram、启停、卸载后守恒。
   - 现有 TCP 门禁不退化。

## Out of Scope

- WinForward 的 UDP 中继、SOCKS5 UDP ASSOCIATE、UoT、UDP session / capacity / 超时管理。
- 本仓库新增生产级 UDP listener / relay；`Relay/` 仍是 TCP 脚手架。
- Forwarded 域的 UDP、adapter 选择器、ARM64。
- DNS 代理、fake-IP、域名分流、LocalTargets。
- 每 datagram 的静默丢包式 block：已决定走 `ALE_AUTH_CONNECT` 的连接级拒绝（见 Key Decisions），静默丢包另立任务。
- 源地址伪造与 `FwpsInjectTransportReceiveAsync0` 路径。

## Requirements

- **R1 规则可表达 UDP**：配置、C# 编译器、C++ blob 解码、matcher 对 `udp` 的接受与拒绝
  行为一致；非法协议整块拒绝，不静默降级。
- **R2 UDP 参与同一套规则顺序**：首个命中生效；`permit` / `block` / `redirect` 与 TCP
  语义一致；`fallback_action` 同样作用于 UDP。
- **R3 重定向携带原始目的地**：UDP redirect context 的 family、protocol、port、address、
  PID、rule_index 正确；listener 逐 datagram 可读。
- **R4 防循环闸对 UDP 生效**：listener 端口、previousVersion、redirect state、条件标志、
  self-PID 五条路径在 UDP 上不退化。
- **R5 失败姿态可观测**：redirect 失败 / context 分配失败 / apply 失败均 fail-open 直连，
  计数与事件如实记录，不记成 blocked。
- **R6 block 在 auth 层执行**：UDP block 规则的拒绝由 `ALE_AUTH_CONNECT` filter 完成；
  redirect 层的 classify 只记账。
- **R7 ABI 可演进**：`probe_abi_version` 提升后，C# 镜像、布局断言、`wfprobe`、用户态
  读取方同步更新；旧版本配置被明确拒绝。
- **R8 生命周期干净**：不新增驱动侧 UDP 表；context 由 WFP 释放；启停、卸载、
  重复 datagram 后池计数守恒。
- **R9 可回归**：宿主 `tools/check.sh --full` 与 VM `gate.ps1` 覆盖新增行为，且每条新断言
  都做过“故意破坏后变红”的反向验证。

## Acceptance Criteria

- [ ] **A1 UDP redirect（unconnected）**：`protocol=udp` + `redirect` 规则，`sendto` 首包
      被改道到 listener；listener 收到 `IP_WFP_REDIRECT_CONTEXT`，原始目的地、协议 17、
      PID、规则序号与客户端一致。
- [ ] **A2 UDP redirect（connected）**：`connect` + `send` 同样被改道；客户端能收到
      listener socket 发回的普通回包；不依赖源伪造或注入。
- [ ] **A3 多 datagram / 多目的地**：同一 socket 连续发 N 条到同一目的地，listener 收到
      N 条且 context 不串；`udp_classify_calls` / `udp_redirected` / `contexts_allocated`
      各只 +1；K 个新关联各 +K。发往两个不同目的地时 context 分别正确；客户端 socket
      关闭后 1s 内 context outstanding 回到基线。
- [ ] **A4 permit 与 fallback**：`permit` 规则的 UDP 直连、不进 listener；未命中规则时
      `fallback_action` 对 UDP 生效（permit 直连 / redirect 进 listener）。
- [ ] **A5 block**：`protocol=udp` 的 block 规则使报文在 auth 层被丢弃：应用 `send` /
      `sendto` / `connect` 成功，对端收不到；未 block 的端口仍可达。redirect 层计数与
      事件记为 blocked。静默丢包已由用户确认（2026-10-21），不再要求应用可见错误。
- [ ] **A6 `protocol=any` 与进程选择器**：`any` 同时覆盖 TCP 与 UDP，block 规则不会只拦
      TCP；`permit` / `redirect` 对两种协议都生效；UDP 规则里的进程选择器能命中匹配的
      进程、放过不匹配的进程。
- [ ] **A7 IPv6 / dual-mode**：至少一条 IPv6 或 dual-mode UDP 用例通过；context 的
      family 与地址正确；不退回 `permitted_non_tcp`。
- [ ] **A8 失败姿态**：可达的放行分支（`enabled=0`、无规则）不误记 blocked；难以稳定
      注入的 redirect 失败分支（拿不到 handle / writable data / context）通过代码审查与
      变异测试确认 fail-open 且事件不写 blocked，不要求在 VM 上人为制造驱动故障。
- [ ] **A9 生命周期**：从干净启动开始，重复 datagram / 多次 enabled 0↔1 后 `WfPr`
      allocs 增量 == `udp_redirected` 增量，客户端 socket 关闭后 outstanding 回基线；
      `sc stop` 后 outstanding=0；`WfTr` 规则表计数守恒；无蓝屏、无残留服务。
- [ ] **A10 TCP 回归**：现有 selftest / gates / 规则套件全绿；TCP redirect、block、
      fallback、进程选择器、V6 用例不退化。
- [ ] **A11 宿主门禁**：`tools/check.sh --full` 全过；新增 C++ / C# 单测与差分用例覆盖
      UDP，且变异后能变红。
- [ ] **A12 环境清理**：实验模块、临时 IOCTL、`DATAGRAM_DATA` callout、注入 handle、
      wfprobe 实验参数全部删除；README / spec / ABI 文档更新。

## Key Decisions

- **D1 UDP block 语义（用户已确认，2026-10-21）**：继续用 `ALE_AUTH_CONNECT` 的
  `FWP_ACTION_BLOCK` filter。正式语义是**连接级静默丢包**：应用 `send` 成功、对端收不到、
  `recv` 超时；不追求可捕获的 socket 错误，也不新增每包路径。与包版 WinForward 的
  block 语义对齐。
- **D2 `protocol=any` 覆盖 TCP + UDP**：与 WinForward 产品语义一致（规则不写协议即匹配
  所有协议，TCP 包含在内）。这会改变现有“any 只在 TCP 上生效”的事实，属于正式接入
  的预期行为变化。
- **D3 UDP 由规则驱动，不设全局 UDP 开关**：`enabled=0` 仍是总开关；没有单独的
  “UDP 实验开关”。实验 IOCTL 全部删除。
- **D4 IPv4 + IPv6 都在本任务范围内**：探针只实测了 IPv4，IPv6/dual-mode 由本任务的
  VM 用例补上。至少 dual-mode 一条用例必须通过；纯 IPv6 环境不具备时记录为明确限制，
  不能静默跳过。
- **D5 驱动不维护 UDP flow / 映射表**：redirect context 每次 classify 分配，所有权归
  WFP；回程不需要驱动改源或注入。用户态中继从 listener socket 直接回包。
- **D6 交付边界**：驱动 + 规则编译 + `wfprobe` 验证工具；WinForward 的 UDP 中继、
  SOCKS5 UDP ASSOCIATE、UoT 不在本仓库实现。

## Notes

- 本任务先完成规划；`task.py start` 与产品代码改动要等 PRD / design / implement 评审通过。
- 实验代码的删除与正式代码的加入放在同一批提交里，避免留下两套 UDP 路径。
- `wfprobe` 的 UDP 命令保留为验证工具，但它不再是“实验 ABI”，而是正式 ABI 的测试客户端。
- A8 的失败分支不可稳定注入，按代码审查 + 变异测试处理；门禁不依赖人为制造驱动故障。
