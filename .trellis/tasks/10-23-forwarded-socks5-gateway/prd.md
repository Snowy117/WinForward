# Forwarded SOCKS5 网关：按入口网卡选上游

## Goal

把 Forwarded 域做成"按入口网卡选择 SOCKS5 上游"的透明网关：指定入口网卡上的客户端 TCP/UDP
流量，经本机 listener 与用户态中继送到对应 SOCKS5 上游；客户端无感，系统路由表不变。

第一版由内核驱动做入口识别、DNAT 到本机 listener、回程源地址还原与注入，用户态复用现有
SOCKS5 TCP CONNECT / UDP ASSOCIATE 中继骨架。

## Background

- 现有 WinForward（NDISAPI 包版）的 Forwarded 路径是：逐包捕获到用户态 → 按到达适配器匹配
  `Forwarded.Rules` → DNAT 到本机 listener → SOCKS5 中继 → 回程注入 origin adapter。CPU 成本
  结构见 `benchmarks/results/2026-10-18-cpu-usage-analysis/README.md`。
- 本仓库的 WFP 内核版目前只覆盖 host（`ALE_CONNECT_REDIRECT`）；Forwarded 是 `AGENTS.md`
  列出的结构性缺口（D3/D7 明确 forwarded 仍走 NDISAPI）。
- 本任务的需求不是"代理所有 forwarded"，而是"本机作网关，给某些入口网卡指定不同 SOCKS5
  上游"，协议 TCP + UDP，不改系统路由表。
- Forwarded 流量没有本机 socket，不进 ALE；WFP `IPFORWARD` 只能 permit/block，且只在启用
  Windows 转发时触发。因此必须走包级驱动 + 用户态中继。
- 不采用 wintun：SOCKS5 CONNECT 是字节流，wintun 需要用户态 TCP 栈。不 hook `ipnat.sys`：
  没有公开扩展点，内核 hook 会被 PatchGuard/HVCI/签名策略挡，且它的模型不支持按入口选上游。
- 第一版覆盖"主机协议栈可见的入口"：Hyper-V Internal switch、移动热点、宿主虚拟网卡。
  External switch 的纯二层桥接流量不经主机栈，留作后续 vSwitch 扩展任务。

## In Scope

1. **驱动入口处理**：在入口网卡上识别新 TCP 流 / UDP 关联，按"到达适配器"匹配 Forwarded
   规则，DNAT 到本机 listener，维护 redirect 表；出口方向识别 listener 回程，还原源地址，
   注入入口网卡。
2. **用户态中继**：listener + SOCKS5 TCP CONNECT / UDP ASSOCIATE；按 redirect 表里的
   upstream 选择服务器；复用现有 `Relay/`、`Socks5Client`、UDP 关联骨架。
3. **配置模型**：复用 `Forwarded.Rules`——`adapterId/adapterName` 即入口适配器，
   `action=proxy` + `target` 即 SOCKS5 上游；新增 `clientCidr`（客户端源地址）用于按客户端
   网段分流，`remoteCidr` 继续匹配目的网段。驱动规则 blob 增加 forwarded 段。
4. **接缝**：新增 redirect 表查询 IOCTL/ABI（原始目的地、upstream id、入口适配器、客户端
   元组），因为 forwarded 没有 WFP `redirect_context`。
5. **失败关闭**：无本地地址 L、查表失败、上游不可用、容量耗尽 → 拒绝/RST/丢包，不直连。
6. **观测**：事件与计数器按 ingress / upstream / protocol 维度，命名与现有 host 路径对齐。
7. **验证**：Hyper-V Internal switch + VM 客户端；TCP/UDP；多入口多上游；回程；热替换；
   失败路径；IPv6 happy path。
8. **文档**：`wfp-architecture.md`、驱动/用户态 README、`AGENTS.md` 更新。

## Out of Scope

- External switch 纯桥接的 VM 流量（需要 Hyper-V vSwitch 扩展，另立任务）。
- ICMP 代理；ICMP 只按规则 pass/block，不进 SOCKS5。
- 系统路由表修改、DNS 代理、fake-IP、GUI、服务宿主。
- 完整 NAT（上游是 SOCKS，不是 IP 网关）。
- ARM64。

## Requirements

- **R1 入口识别与匹配**：驱动必须按"包到达的适配器"、客户端 CIDR（`clientCidr`）、目的
  CIDR（`remoteCidr`）、协议、端口匹配 Forwarded 规则；同入口多条规则按顺序首个命中；
  未命中走 `Forwarded.FallbackAction`。
- **R2 上游选择**：命中 `proxy` 规则的流必须绑定该规则的 SOCKS5 target；不同入口/规则可以
  选择不同上游；驱动在 redirect 表里携带 upstream id，用户态据此选择服务器。
- **R3 TCP 语义**：客户端与本机 listener 之间的 TCP 由 Windows 栈终结；驱动只做 DNAT/SNAT，
  不碰序列号。握手、双向数据、半关闭、RST、TIME_WAIT 语义与直连一致。
- **R4 UDP 语义**：每条客户端关联（或按目的地）建立 SOCKS5 UDP ASSOCIATE；datagram 的
  封装/解封装正确；回程源地址还原为原始服务器；关联超时回收。
- **R5 原始目的地**：用户态必须在 accept / 首个 datagram 时拿到原始目的地与 upstream id；
  查不到必须拒绝，不得猜测，不得回退直连。
- **R6 路由表**：功能不得要求修改系统路由表；唯一允许的例外是到客户端网段的局部直连路由，
  且必须在文档和 `route print` 差异中显式列出。
- **R7 失败关闭**：无本地地址 L、表缺失、上游失败、容量耗尽 → block/RST/丢包；不允许
  回退直连；失败可观测。
- **R8 热替换**：规则/上游变更后，新流按新配置；已有流按明确策略执行（保持旧上游直到结束，
  或按规则切断），不得串流。
- **R9 观测**：每入口/每上游的 TCP/UDP 计数与关键事件；命名与 host 路径对齐。
- **R10 性能**：pass/block 在内核完成，不进用户态；只有 proxy 流进用户态；记录每包成本、
  吞吐、CPU 基线。

## Acceptance Criteria

- [ ] **A1 多入口、多 CIDR、多上游**：入口 A 的 TCP 流量经上游 S1，入口 B 经 S2；同一入口内客户端网段 10.0.0.0/24 走 S1、10.0.1.0/24 走 S2；交叉验证不串流。
- [ ] **A2 TCP 端到端**：客户端看到的源地址/端口仍是原始服务器；三次握手、双向大数据、
      半关闭、RST 正常；与服务端直连的字节流一致。
- [ ] **A3 UDP 端到端**：同一客户端关联的 N 条 datagram 只建立 1 个 SOCKS5 关联；回程源
      地址还原；关联超时后回收；多目的地不串。
- [ ] **A4 未命中**：未配置规则/未配置入口的 forwarded 流量按 fallback pass/block；不被
      误代理。
- [ ] **A5 路由表**：`route print` 前后一致（允许的局部路由逐条列出）；功能不依赖
      `IPEnableRouter` / `netsh forwarding` 开启。
- [ ] **A6 失败路径**：无 L、查表失败、上游拒绝、容量耗尽 → 客户端收到 RST/超时；不发生
      直连；计数与事件可观测。
- [ ] **A7 热替换**：旧流按策略结束，新流走新上游；无串流；规则 generation 可回读。
- [ ] **A8 可观测性**：事件/计数器覆盖 ingress / upstream / protocol；与 host 路径命名一致。
- [ ] **A9 性能基线**：记录 1/4 流 TCP、UDP pps 下的每包成本/吞吐/CPU，与包版同形状对比。
- [ ] **A10 IPv6**：IPv4 必须；native IPv6 与 v4-mapped 的 happy path 至少跑通一条。
- [ ] **A11 文档**：spec / README / AGENTS 与实现一致；未覆盖项写明。
- [ ] **A12 门禁**：新增套件进 `gate.ps1` 并 PASS；关键 mutation 变红。

## Notes

- 第一版驱动形态在 spike 后定：优先在现有 WFP 驱动上加 MAC 层 callout + L2 注入；若可见性/
  注入不达标，转独立 NDIS LWF。见 `design.md` D1。
- 不 hook `ipnat`，不用 wintun。
- 配置复用 `Forwarded.Rules`，不新增顶层 schema；驱动 blob 新增 forwarded 段或提升版本。
- 与 host 路径共用规则编译、matcher、事件/计数器命名；两条驱动机制不同，但用户可见语义
  必须一致。
