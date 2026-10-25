# Host UDP 路线 A 验证：WFP UDP local redirect 最小探针

## Goal

在 WinLTSC 上，用最小驱动改动 + `wfprobe` 探针命令验证路线 A 的三个机制假设，
产出带证据的 **GO / NO-GO** 结论，决定 Host UDP 是否采用 WFP 原生 local redirect 作为数据面。

## Background

- 官方文档明确 UDP 在重定向支持范围内（TCP / UDP / Raw UDPv4 / Raw ICMP），重定向层是
  `ALE_CONNECT_REDIRECT_V4/V6`；`localRedirectHandle` 在 Win8+ 是本地重定向的硬要求。
- UDP 没有 accept，用户态不能用 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读原始目的地
  （UDP socket 上返回 `WSAEOPNOTSUPP`）。正确姿势是
  `setsockopt(IPPROTO_IP, IP_WFP_REDIRECT_CONTEXT=70)` + `WSARecvMsg` 从 ancillary data 逐条取。
- 微软有已知问题文档：connected UDP（`connect` + `send`）被重定向到本地代理时会丢包，
  workaround 是改用 `sendto`；当前 WinLTSC 是否仍复现必须实测。
- connected UDP 应用只接受源地址等于原始服务器的回包；中继直接 `sendto` 客户端时源地址是中继自己，
  必须在回程方向把源改回 `original_dest`。改写落在哪一层（`DATAGRAM_DATA` inbound /
  `ALE_AUTH_RECV_ACCEPT` / `INBOUND_TRANSPORT`）未定，取决于 loopback 可见性。
- 现有驱动 [probe_classify.cpp](../../../src/driver/probe_classify.cpp) 在 redirect 层只处理 TCP，
  UDP 走 `permitted_non_tcp` 直接放行；[probe_ioctl.h](../../../src/driver/probe_ioctl.h) 的
  `probe_decision` / `probe_stats` 没有 UDP 维度。
- 现有 `tools/wfprobe` 已有 `control` / `stats` / `events` / `listen` / `client` / `relay` / `gates`
  等命令与 VM 侧 `gate.ps1` / `load-driver.ps1`，实验可以复用构建、签名、安装与卸载链路。
- 本机没有 `wf.local.json` / `local/`；VM 侧路径与同步由 `windows-vm` 会话手工完成（见 `implement.md`）。

## In Scope

- 驱动侧：在 `ALE_CONNECT_REDIRECT_V4`（IPv4 优先）增加 **实验性** UDP redirect 分支，由独立配置
  开关控制，不影响现有 TCP 语义；回程方向按实验结论选择一层做观测或源改写。
- 用户态：`wfprobe` 新增
  - `udp-probe`：配置实验开关（listener 端口/PID、回程模式、回程层）并读取实验统计；
  - `udp-listen`：绑定 UDP listener，设置 `IP_WFP_REDIRECT_CONTEXT`，用 `WSARecvMsg` 收包并打印
    cmsg 形态（原始目的、context 大小、是否为自定义 blob）；
  - `udp-client`：`connected`（connect+send）与 `unconnected`（sendto）两种模式；
  - `udp-relay`：收到 datagram 后回包，用于验证回程源改写。
- 端到端回程验证：connected 客户端必须实际收到源地址被改写为 `original_dest` 的回包，或给出
  该路径不可行的明确证据。
- **connected UDP 不通时的 workaround 探索**：至少尝试 redirect 目标地址分支（loopback vs 本机
  网卡地址）、context/handle 组合、relay 侧 redirect records、以及 A+B 混合（包级 clone+inject
  兜底）四类手段，并记录每类的结果。
- IPv6 / dual-mode 作为可选观察项，不作为 GO 判据。
- 产出 `research/route-a-findings.md`：每个假设给出结论、证据（命令、日志、池计数）、
  失败原因与复现步骤。

## Out of Scope

- 完整 UDP 中继产品化、SOCKS5 UDP ASSOCIATE / UoT 接入、规则表 UDP 编译与 ABI 定版、性能基准、
  `probe_event` 正式协议字段。这些依赖本轮结论，放到后续任务。
- 回程源改写的生产级设计（每 flow 端口分配、映射表并发与崩溃清理、容量上限）。
- IPv6 的完整实现与 scope 语义。
- 把 A+B 混合做成完整产品路径；本轮只验证它能否作为 connected UDP 的兜底。

## Acceptance Criteria

- [x] **A1 connected UDP**：实测通过（WinLTSC）：`connect` + `send` 的 UDP 客户端首包
      被 redirect 到 listener；listener 收到 `from=127.0.0.1:<port>`，`seen=1 redirected=1`。
      微软文档所述 connected UDP 丢包在本机未复现。
- [x] **A2 unconnected UDP**：实测通过：`sendto` 首包被 redirect，listener 收到
      `from=192.168.77.2:<port>`，`seen=1 redirected=1`。
- [x] **A3 context 往返**：实测通过：cmsg 为驱动自定义的 `redirect_context`（36B），
      原始目的地 `1.2.3.4:443`、协议 17、PID 均与客户端一致；cmsg 逐 datagram 携带。
- [x] **A4 回程端到端**：unconnected 客户端收到 relay 回包；connected 客户端也接受普通 relay
      回包（`from=127.0.0.1:5555`）。说明 local redirect 已把 flow 的 remote/local 调到 relay，
      app 不要求源等于原始服务器，**不需要伪造源，也不需要 A2 注入**。`DATAGRAM_DATA`/`IPPACKET`
      不可见与注入被拒只影响“修改源”这条不必要路径。
- [ ] **A5 清理与所有权**：redirect 失败不崩不挂；`sc stop` 干净卸载；`localRedirectContext`
      的分配/释放无泄漏、无 UAF（池计数或 verifier 证据）。
- [ ] **A6 结论**：`research/route-a-findings.md` 给出可执行的 GO/NO-GO，以及若 GO 时路线 A
      的剩余风险清单。
- [ ] **A7 workaround**：若 A1 或 A4 失败，必须至少探索并记录四类 workaround 的结果；
      若其中一类可用，给出可用范围与代价；若全部不可用，说明为什么。

## Key Decisions

- **D1 回程实验深度（用户已选）**：端到端。必须验证 connected 客户端接受改写源的回包，不接受
  "能改但应用不收"的中间结论。
- **D2 实验代码落点（诗织建议，评审可推翻）**：放进现有 `wfprobe` 驱动的独立实验模块
  （`udp_probe.{h,cpp}` + 独立 IOCTL + `probe_driver.cpp` 注册），复用构建/签名/安装链路；
  实验结束后整体删除或转正，不新建独立探针工程。
- **D3 IPv6 范围（诗织建议，评审可推翻）**：IPv4 先做，IPv6 只做观察；A1~A4 在 IPv4 通过后
  再追加 dual-mode 用例。
- **D4 workaround 范围（用户追加）**：connected UDP 不通时不做"记录失败就结束"，必须试四类
  手段并给出可用性结论（见 `design.md` §9）。

## Notes

- 不改 `rule_blob` 的正式语义；实验 IOCTL 用独立 function code，不进入 ABI 版本号。
- 实验代码遵守内核方言与仓库格式/禁止模式检查（`tools/check.sh --fast`）。
- 本轮只做 planning 与实验设计；`task.py start` 与产品代码改动要等最终规划摘要被明确批准。
