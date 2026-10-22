# 执行计划：Forwarded SOCKS5 网关

> 顺序即依赖。每个阶段独立可验证、独立提交；驱动安装前保留上一个可回滚的签名版本。

## 批次清单（ultra-goal）

| 批次 | 范围 | 依赖 | 验证 |
|---|---|---|---|
| **B1** | forwarded blob + matcher + C# 编译器/布局/差分（规则层） | — | ctest + dotnet test |
| **B2** | JSON `forwarded` 段、多上游、clientCidr 解析/校验、控制器接线 | B1 | dotnet test |
| **B3** | L2 可见性/注入 spike（VM），决定 D1 | — | research 报告 + 手工 TCP |
| **B4** | TCP 数据面：驱动 DNAT/回程 + listener/relay/SOCKS5 E2E | B1/B2/B3 | VM 套件 |
| **B5** | UDP 数据面：关联/NAT + SOCKS5 UDP E2E | B4 检查通过 | VM 套件 |
| **B6** | 多入口/CIDR/热替换/失败/观测/门禁/文档/性能收尾 | B4/B5 | 完整 gate |

并发边界：B1 与 B3 文件集不相交，可并行；B2 依赖 B1；B4 依赖 B1/B2/B3；B5 必须在 B4 检查通过后开始；B6 收尾。

## 0. 前置 spike：驱动形态与可见性

- [ ] 在 Windows VM 上建 Hyper-V Internal switch + 一个 VM 客户端，确认主机 vEthernet 的
      地址/网段，VM 默认网关指向主机。
- [ ] 在现有 `wfprobe.sys` 上加最小 L2 callout（只计数 + permit），确认能看到：
      - VM → 网关的 SYN；
      - 网关 → VM 的回程帧；
      - 主机自身流量不会被误计。
- [ ] 注入验证：
      - `FwpsInjectMacReceiveAsync0` 把 DNAT 后的 SYN 送回本机栈，listener 能 accept；
      - `FwpsInjectMacSendAsync0` 把回程帧送回 VM，VM 收到 SYN-ACK。
- [ ] 量 pass/block 的每包成本；与包版"捕获+用户态回注"对比。
- [ ] 记录可见性矩阵（Internal / External / 热点）到 `research/spike-l2.md`，给出 D1 结论：
      继续 WFP L2，还是转独立 NDIS LWF。
- [ ] 验证：spike 文档 + 一次手工 TCP 回环；不进入正式代码路径。

**回滚点**：spike 代码只在临时分支/未安装版本；失败不影响现有驱动。

## 1. 规则与配置

- [ ] 定义 `wf_forwarded_blob` v1：header（magic/version/长度/规则数/fallback）+ 规则
      （ingress_adapter_id、protocol、family、remote_prefix、remote_port_range、action、
      upstream_id、reserved）；C++ `static_assert` 锁尺寸/偏移。
- [ ] C# 侧镜像 + 布局断言；`Forwarded.Rules` → blob 编译器；`upstream_id` 由 target 名
      稳定映射。
- [ ] 校验：`proxy` 必须有已存在的 target；`process` 仍拒绝；adapter 解析失败给
      domain-qualified 诊断；fallback 只允许 pass/block。
- [ ] Linux 差分测试：同一份夹具在 C++ `matcher` 与 C# 编译器上逐条比对；补边界
      （任意 adapter、端口区间、IPv4/IPv6）。
- [ ] 验证：`cmake --build build && ctest --test-dir build --output-on-failure`；
      `dotnet run --project src/userland/WinForward.Userland.Tests -- test`。

**回滚点**：blob 只新增、不改 host ABI；失败回退到未下发状态。

## 2. 驱动骨架：入口匹配与 pass/block

- [ ] L2 callout 注册/注销（按现有 `FwpsCalloutUnregisterById0` 重试规则）；provider/filter
      与现有 ABI 版本化。
- [ ] 规则表原子替换 + generation；入口适配器 id 匹配；协议/地址族/远端 CIDR/端口匹配。
- [ ] pass：原帧直接放行；block：丢弃并计数；未命中按 fallback。
- [ ] IOCTL：set forwarded config（enabled、listener 端口、self PID）、set blob、get stats。
- [ ] 自流量：self PID 在入口方向直接放行；注入流量防循环。
- [ ] 验证：新增 `wfprobe forwarded-rules`（pass/block/fallback/任意 adapter/多规则顺序）；
      VM 卸载/重载压力；`route print` 前后一致。

**回滚点**：只做 pass/block，不建立 redirect 表；出问题可 disable 回退到纯放行。

## 3. TCP 重定向与回程（单入口单上游）

- [ ] 入口 SYN：匹配 proxy 规则 → 分配表项 → DNAT 到 (L, listener_port) → 注入本机栈；
      无 L → fail closed。
- [ ] 中间数据包：入口方向按表 DNAT；出口方向按 listener 端口 + client 查表 SNAT 回原始
      服务器；目的 MAC 用入口记录的 client MAC。
- [ ] 表项生命周期：SYN 建立、FIN/RST/超时回收；表满 fail closed。
- [ ] 查询 IOCTL `ioctl_query_forwarded_redirect` + C# 布局/版本；用户态
      `IRedirectContextSource` 抽象。
- [ ] 用户态：listener accept → 查表 → 按 upstream_id 选 SOCKS5 → CONNECT → 双向泵；
      复用 `RelayServer`/`Socks5Client`/`PumpPair`。
- [ ] E2E：VM 客户端 `curl`/大文件下载；校验字节与直连一致；半关闭/RST。
- [ ] 验证：`wfprobe forwarded-socks tcp`；`route print` 不变。

**回滚点**：TCP 路径关闭 `enabled` 即回到 pass；表项随 disable 清空。

## 4. UDP 关联与回程

- [ ] 入口 datagram：DNAT + 关联表项（客户端元组 → 原始目的地/upstream）。
- [ ] 用户态 listener `recvmsg` + 查表（每关联一次缓存）→ SOCKS5 UDP ASSOCIATE →
      封装/发送。
- [ ] 回程：上游响应 → listener `sendto` 客户端 → 驱动出口 SNAT 源地址为原始服务器 →
      注入入口网卡。
- [ ] 关联超时回收；源地址校验（只接受对应上游）；容量限制。
- [ ] E2E：VM 客户端 DNS/UDP echo；N 条 datagram 只建 1 个关联；多目的地不串。
- [ ] 验证：`wfprobe forwarded-socks udp`；计数器与事件。

**回滚点**：UDP 路径可单独 disable；TCP 不受影响。

## 5. 多入口、多上游、热替换、失败路径

- [ ] 第二条 vEthernet + 第二个 SOCKS5 上游；交叉验证不串流。
- [ ] 热替换：新 generation 下发；旧流保持旧 upstream；新流走新规则；表项 generation
      校验。
- [ ] 失败路径：无 L、查表超时、上游拒绝、表满、listener 未就绪；逐条断言 RST/超时/
      无直连。
- [ ] 验证：套件 A1–A8 全绿；失败路径每条的计数与事件。

**回滚点**：按 generation 回退配置；不中断已有流。

## 6. 观测、文档、门禁、性能基线

- [ ] 事件/计数器：`forwarded.*`，按 ingress/upstream/protocol；与 host 命名对齐。
- [ ] `wfprobe` 输出、Controller `state` 增加 forwarded 计数。
- [ ] 文档：`wfp-architecture.md`（D3/D7 更新、§Forwarded facts）、驱动/用户态 README、
      `AGENTS.md` 结构性缺口更新。
- [ ] `gate.ps1` 增加 `forwarded-socks` 步骤；`mutation.ps1` 加关键变异（入口匹配、DNAT、
      回程 SNAT、upstream 选择、fail-closed），每条期望变红。
- [ ] 性能基线：1/4 流 TCP、UDP pps、480 Mbps；记录每包成本/吞吐/CPU，与包版同形状对比。
- [ ] 宿主 `tools/check.sh --full`、C# 测试、VM 完整 gate、mutation、`route print` 断言。
- [ ] 归档任务、写 journal。

**回滚点**：文档/门禁与代码分开提交；最终 gate 失败按最近一批回退，不伪绿。

## 验收映射

| PRD | 由哪一步验证 |
|---|---|
| A1 多入口多上游 | §5 |
| A2 TCP 端到端 | §3 |
| A3 UDP 端到端 | §4 |
| A4 未命中 | §2 |
| A5 路由表 | §2/§3/§6 |
| A6 失败路径 | §5 |
| A7 热替换 | §5 |
| A8 可观测性 | §6 |
| A9 性能基线 | §6 |
| A10 IPv6 | §3/§4（happy path） |
| A11 文档 | §6 |
| A12 门禁 | §6 |

## 完成记录（待填）

- 驱动形态结论：
- 门禁结果：
- 性能数字：
- 未覆盖项：
