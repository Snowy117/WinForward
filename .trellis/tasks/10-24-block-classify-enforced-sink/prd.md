# Block 顺序语义与 classify-enforced sink

> TODO / 后续任务。本任务只记录问题与候选方案，尚未进入规划或实现。

## 为什么需要这个任务

当前 block 由 `ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK` filter 执行。filter 只看自身条件，
不参与 blob 的“首个命中”顺序。为避免 filter 抢掉更早的 permit/redirect，编译期与驱动用
`rule_order_valid` / `RuleOrderValidator` 拒绝“更早的非 block 规则可能与后面的 block 规则重叠”的配置。

由此产生两个 parity 缺口：

1. 常见配置 `permit 内网` → `block 其余` 会被拒绝；
2. block 的进程选择器只能用精确文件路径；文件名 / 目录前缀无法用 WFP auth filter 表达
   （目录解析不出 app-id，WFP 也没有 app-id 前缀匹配）。

## 候选方案（待 spike）

1. **classify 判定 + 重定向到本地 sink（推荐先验证）**
   block 成为 classify 的首个命中决策；命中后把连接/关联改道到本机 sink。
   - UDP：绑定本机地址的 UDP sink，收到即丢 → 应用侧静默丢包，与 D4 一致；
   - TCP：优先验证“改道到本机关闭端口”能否让客户端在 connect 阶段拿到 RST；若 WFP 必须有
     listener，则退化为“sink accept 后立即 SO_LINGER=0 关闭”。
   - 成功则删除 auth filter 与顺序歧义拒绝，所有进程选择器形式都能 block。
2. **包级执行（L2 丢包 / RST 注入）**
   语义最接近 D4，但每包解析/查表，和连接级零数据面成本的立项目标相反；除非 sink 不可行，
   否则不优先。
3. **动态 per-process filter**
   监控进程创建，为匹配目录/文件名的实际 exe 建精确 filter。复杂、有竞态，暂不考虑。

## 必须先回答的问题

- TCP 改道到本机关闭端口 / 无 listener 的 local redirect，客户端实际看到什么错误？
- UDP sink 是否完全不产生 ICMP，应用侧是否只表现为超时？
- sink 连接会不会重新进入 `ALE_CONNECT_REDIRECT`（是否需要新的防环闸）？
- sink 方案下 fallback=block、进程/网段/端口数组、热替换、UDP 关联生命周期的语义如何定义？
- 是否修改 D4（TCP 的 connect 时刻 RST）？

## 关联

- 前置任务：`10-24-host-rule-arrays-process-match`（规则数组、进程选择器语义、blob v2）。
- 研究证据：`.trellis/tasks/10-24-host-rule-arrays-process-match/research/winforward-rule-semantics.md` §4。
- 现有实现事实：`.trellis/spec/backend/wfp-architecture.md` §7.9、§7.10。
