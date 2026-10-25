# 内核子树开发规则

> 本文件只约束 `kernel/` 子树。仓库根的 [`AGENTS.md`](../AGENTS.md)（质量门禁、注释约定）与
> `.trellis/` 优先；内核侧的 live 规范与证据在 `.trellis/spec/backend/kernel/`。文中的
> `.trellis/...` 路径都相对仓库根。

## 项目定位与集成目标

`kernel/` 是 **WinForward 的内核截获后端**，不是独立的代理产品。它替代 WinForward 的 NDISAPI（WinpkFilter）
核心，解决把流量接进代理的入站侧问题：host TCP 与 host UDP 从逐包捕获 / 改写 / 回注，换成
TCP 每连接、UDP 每关联一次 classify。
内核规则表决定放行 / 重定向 / 拦截，用户态只做规则编译与中继。

并入 WinForward 后，它作为 **WinForward.Kernel edition** 的后端，与 WinpkFilter edition 共用同一份
`appsettings.json`、同一套策略语义、同一套事件与计数器。两个 edition 只在“驱动机制”上不同，不应在
用户可见决策上不同。功能规划以 **edition parity** 为准绳，不以“代理软件还缺什么”为准绳。

WinForward 的架构细节见 `.trellis/spec/backend/`，优先读
`traffic-policy-lifecycle.md`、`tcp-local-redirect.md`、`directory-structure.md`；内核侧的
WFP 架构、规则 blob、C++/C# 规范与门禁见 `.trellis/spec/backend/kernel/`。

### 集成缝

WinForward 的 TCP 路径本来就是“本地重定向 + 中继”，只是重定向由包改写完成；内核版替换的是
“把客户端送到监听端口并带回原始目的地”这一段。中继应接收后端无关的流描述符：

`RedirectedTcpFlow(Client, Server, ProcessId, RuleIndex, Decision)`

包后端从 `TcpRedirectTable` 填；内核后端从 redirect context + accept peer + PID 反查填。保持这条缝，
WinForward 的监听、relay、SOCKS5 才能原样复用；否则会出现第二套代理实现。

UDP 的接缝不同：listener 从 redirect cmsg 拿到原始目的地，回程由用户态用**同一个 listener socket**
`sendto` 给客户端源地址；驱动不做回程注入，也不维护 UDP flow / mapping 表。

### 当前优先级

1. **Host TCP parity**：规则降级完整性（进程文件名 / 精确路径 / 目录前缀、CIDR / 端口 / 族集合、
   一条配置规则对应一条 blob 规则）、`Target` 映射、决策差分门禁。
2. **流生命周期**：客户端元组、`TcpFlowCapacity`、失败关闭、block 语义（classify 首命中 +
   sink 端口；TCP RST / UDP 静默丢包）。
3. **入口与可观测性**：`ValidatedConfiguration` 作为唯一配置入口；事件、计数器命名与包版对齐。
4. **Host UDP**：驱动侧已完成（classify / redirect / sink block），当前控制面 ABI **v8**；
   验证与门禁见 `.trellis/spec/backend/kernel/research/10-22-host-udp-verification/`。
5. **Forwarded**：B6a 正确性、B6b 多入口/多 L/热替换/观测已完成并提交（控制面 ABI 已升到
   v8；B6b 验收时为 v7，驱动 SHA-256 `EA271858…0A95`）；B6c 独立 Linux 门禁在
   `tools/linux/forwarded_gate.py`。
6. **结构性缺口（需产品决策）**：Host adapter 选择器、ARM64、D7（WFP L2 是否正式替代
   NDISAPI/WinpkFilter；见 `.trellis/spec/backend/kernel/research/10-23-fwd-b6-hardening/d7-decision.md`）。

### 不在 `kernel/` 做

SOCKS5 认证 / UDP 中继 / SOCKS5 UDP ASSOCIATE / UoT / LocalTargets / DNS 代理、域名分流与 fake-IP、GUI、服务宿主、临时 JSON 格式的扩展。
`Relay/`、`Socks5Client`、`Configuration/` 是脚手架，集成时复用 `WinForward.Runtime` 与
`WinForward.Configuration` 的对应实现，而不是在 `kernel/src/userland/` 里增强。

## Forwarded 现状与边界

- 驱动做入口识别、DNAT 到本机 listener、回程 SNAT/注入；用户态复用 SOCKS5 TCP CONNECT 与
  UDP ASSOCIATE 中继。TCP/UDP 数据面均已 E2E；多入口（Internal/External）、同入口多 L、
  回程 key 含 server、热替换和 StateCommand 观测在 B6b 验证。
- 已知边界（统一记录在 `.trellis/spec/backend/kernel/research/10-23-fwd-b6-hardening/verification.md`
  §14/§15）：产品控制器每个 ifindex 只解析一个
  L；UDP 多 L 并发多目的、UDP 热替换、IPv6 多入口未 E2E；LWF 对注入帧可见性未验证；config
  disabled 的 fail 阶段在 B6c 验收时（ABI v7）不进入 classify，当前 v8 未复验；stop entry 非零采样只在 fault 构建可触发；
  TCP 多 NB 部分注入失败无法从网络构造。
- 门禁：Linux `tools/linux/forwarded_gate.py`（core/fragment/permit-first/host/reject；CI 禁止
  `--no-verify`）；Windows-only `gate.ps1` 不包含需要外部 Linux 客户端的 forwarded 数据面，
  只跑 host/forwarded-rules 等，原因见同一份 `verification.md` §14。

## 关于本机（Linux）

端口40000~41000对Windows VM在192.168.100.4开放，以方便实验
