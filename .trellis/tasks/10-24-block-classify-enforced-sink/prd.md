# Host block：classify 首命中 + sink 端口执行

> 方案 A，2026-10-24 用户确认。保留 block 语义，不删除、不委托 Windows 防火墙。

## 背景

当前 Host 规则的 block 由每条规则展开成 `ALE_AUTH_CONNECT_V4/V6` 的 `FWP_ACTION_BLOCK`
filter 执行。filter 只看自身条件，不参与 blob 的“首个命中”顺序，由此产生四个结构性缺口：

1. 常见配置 `permit 内网 → block 其余` 被编译期与驱动拒绝（顺序歧义）。
2. block 规则的进程选择器只能是精确文件路径；文件名 / 目录前缀无法用 auth filter 表达。
3. 每条规则按 layer × protocol × process × port × network 展开，总数受
   `max_block_filters = 128` 限制。
4. `fallback = block` 与任何非 block 规则组合都会让那些规则变成死规则，只能拒绝整块配置。

方案 A 把 block 变成 classify 的首个命中决策：命中后把连接 / 关联改道到本机保留的 sink
端口，由少量固定 auth filter 执行拒绝。这样顺序、选择器、预算三个问题一起消失，同时保留
D4 的 TCP RST / UDP 静默丢包语义。

## 目标

- block 参与 classify 的 first-match 顺序；文件名 / 精确路径 / 目录前缀、数组、
  `fallback = block` 都能正常工作。
- 删除顺序歧义拒绝、block 表达边界、128 filter 预算与 per-rule filter 展开。
- 保留 D4：TCP 在连接阶段被拒（RST），UDP 静默丢包。
- 控制面 ABI 升到 v8，新增 sink 端口字段；规则 blob 仍是 v2。
- 用户态配置层不再拒绝上述配置；控制器 / 中继负责保留 sink 端口。

## 需求

### R1. 控制面 ABI v8

- `probe_config` 增加 `block_sink_port`（u32，偏移 52），原 `reserved[3]` 缩为
  `reserved[2]`；结构仍为 64 字节。`probe_abi_version = 8`。
- C++、C# 产品镜像、`tools/wfprobe` 镜像与布局断言三侧同步；旧 ABI v7 配置整块拒绝。

### R2. 驱动配置校验

- `enabled != 0` 时 `listener_port` / `listener_process_id` 仍必须非 0。
- `block_sink_port != listener_port`。
- `enabled != 0` 且当前规则表含 block 规则或 `fallback = block` 时，
  `block_sink_port != 0`；否则拒绝启用，旧 config 保持不变。
- `enabled` 期间不允许改变 `block_sink_port`；必须先 disable。
- disabled 时允许 `block_sink_port = 0`；enabled 时设置含 block 的 blob，若 sink 未配置
  则整块拒绝，旧表保持不变。

### R3. classify 改道

- block 命中后构造到 `block_sink_port` 的 local redirect；目标地址复用
  `probe_build_redirect_target`（原本地地址为 `ANY` 时用 loopback，否则用原本地地址）。
- 不分配 `redirect_context`（sink 不需要原始目的地）。若 WFP 要求 context，回退为最小
  context，并把结论写入 `verification.md`。
- 重定向失败（acquire / apply 失败）不得假装 blocked：记 `redirect_failures` 与新的
  decision `block_enforcement_failed`，按 fail-open 放行，事件如实反映。

### R4. 固定 auth filter

- 最多 4 条 `ALE_AUTH_CONNECT_V4/V6` filter（V4/V6 × TCP/UDP），条件：
  - `FWPM_CONDITION_IP_PROTOCOL` = 6 / 17；
  - `FWPM_CONDITION_IP_REMOTE_PORT` = `block_sink_port`；
  - `FWPM_CONDITION_FLAGS` 含 `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`
    （`FWP_MATCH_FLAGS_ALL_SET`）。
- action `FWP_ACTION_BLOCK`，sublayer 与 weight 与现有 block filter 一致。
- 这些 filter 在 config enable 且 sink 端口非 0 时安装，disable 时删除；不再随规则表
  换表增删，也不再按规则展开。

### R5. 防环与计数

- classify 闸：远端端口等于 `listener_port` 或 `block_sink_port` 时放行，避免 sink 自身
  再进入 classify。
- `blocked` 只统计成功改道到 sink 的 block 决策；`deferred_blocks` 表示成功改道次数；
  `udp_blocked` 仍按 UDP block 决策统计。
- block 事件保留 `blocked_rule` / `blocked_fallback`，`redirect_port = block_sink_port`，
  `branch` 记录目标选择。
- `redirected` 只统计 proxy redirect，不统计 block sink。

### R6. 用户态编译器

- 删除 `RuleOrderValidator` 与 block 表达边界 / 预算校验。
- 删除 `PolicyLimits.MaxBlockFilters`；`ProbeIoctl` / `BlobAbiConsistencyTests` 的镜像同步。
- `CompileUnchecked` 不再跳过顺序 / 表达边界 / 预算（这些检查已不存在），或按测试需要
  改为普通编译入口。
- 配置层接受 `permit 内网 → block 其余`、block 文件名 / 目录 / 数组、
  `fallback = block` + 任意规则。
- `tools/wfprobe` 的 `RuleBlobOrder`、`BlobBuilder.Build` 镜像同步，不再拒绝这些配置。

### R7. 控制器 / 中继

- 选一个保留端口 `block_sink_port`，绑定 UDP socket 收到即丢；TCP 侧保留（无 listener，
  或 bind 但不 listen）作为 auth filter 失效时的兜底。
- 将端口写入 `ProbeConfig`；停止时释放。`enabled` 期间端口稳定。
- 回读校验与状态报告包含该字段。

### R8. 热替换语义

- 新连接 / 新 UDP 关联按新表 classify；已存在的 UDP 关联保持旧决策直到关联结束。
- sink 端口在 enabled 期间不变；要改端口必须先 disable。
- 该差异写入 spec，并有测试覆盖。

### R9. 测试与门禁

- Linux 宿主：blob / matcher 用例不变；删除 block 边界 / 预算 / 顺序负向夹具；新增
  C# 正向编译用例。
- Windows `wfprobe`：order、selector、array、fallback、TCP/UDP、hot-swap、ABI v8。
- 变异测试：删除 order / fallback 冲突变异；新增 sink 端口、固定 filter 条件、协议
  条件变异。
- `tools/check.sh --full` 与 WinLTSC `gate.ps1` 通过。

### R10. 文档

- `.trellis/spec/backend/host-rule-blob.md`：删除 §7 顺序校验、§8 表达边界与预算，改写为
  sink 方案；更新 §6.2 / §9 / §10。
- `.trellis/spec/backend/wfp-architecture.md`：§7.9 / §7.10 / §7.11 与 D4 的 block 执行描述。
- `src/driver/README.md`、`src/userland/README.md`、`AGENTS.md`、`tools/windows/README.md`
  同步。

## 验收标准

- **A1** Linux `tools/check.sh --full` PASS。
- **A2** WinLTSC `gate.ps1` PASS（host / rules / udp / gates / firewall-check；forwarded
  外部数据面按现有门禁边界处理）。
- **A3** `permit 内网 → block 其余` 被接受；内网连接成功，其他连接在连接阶段失败。
- **A4** block 的文件名、精确路径、目录前缀、进程 / 网段 / 端口数组均生效。
- **A5** `fallback = block` 与一条 permit 规则共存：permit 目标成功，其他目标失败。
- **A6** TCP block 在连接阶段失败；UDP block 的 `send` 成功且对端收不到。
- **A7** 只有 4 条固定 auth filter；规则表不再展开 per-rule filter；删掉全部 block 规则后
  不残留 block filter。
- **A8** ABI v8 在三侧往返一致，布局断言全绿。
- **A9** `RuleOrderValidator`、`max_block_filters`、`block_expression_valid`、
  `block_filter_budget_valid` 不再被引用（历史文档除外）。
- **A10** 热替换按 R8 的 per-association 语义有测试和文档。

## 非目标

- 不做包级 block、不做 Windows 防火墙规则翻译、不回到 auth callout。
- 不引入 blob v3，不改 matcher 语义。
- 不改 Forwarded 域。
- 不新增产品配置 schema（sink 端口由控制器自动分配）。
- 不保证 TCP 错误码一定是 `10013`；A6 只要求连接阶段失败。实际错误码（`10013` 或
  `10061`）由 Batch 2 的 VM 实验记录，并写入文档。

## 风险与待验证

1. **auth 层可见性**：`ALE_AUTH_CONNECT` 的 filter 是否能看到 redirect 后的
   `IP_REMOTE_PORT` 和 `IS_CONNECTION_REDIRECTED` 标志。Batch 2 首先验证；若不成立，
   退化为纯 sink socket（TCP RST、UDP 静默），并记录 TCP 错误码变化。
2. **TCP 错误码**：固定 filter 命中时应为 `10013`；未命中时关闭端口给出 `10061`。
   二者都满足“连接阶段失败”，但用户可见错误不同。
3. **connected UDP redirect record 泄漏**：不分配 context 可避免 36B `WfPr` 泄漏；每包
   refcount 是否仍增长需实测，结论写入 verification。
4. **sink 端口占用**：端口选择、TCP 保留方式、与其他应用冲突的处理。
5. **热替换**：已存在 UDP 关联保持旧决策，与当前 auth filter 的即时生效不同。
6. **fail-open**：classify 内 redirect 失败时无法在 redirect 层拦截；用独立事件与计数暴露，
   并作为已知边界记录。
