# 设计：block → classify 首命中 → sink 端口 → 固定 auth filter

## 1. 数据流

```text
client connect / UDP association
  → ALE_CONNECT_REDIRECT classify
      → match(blob) 首个命中
          permit   → PERMIT（直连）
          redirect → redirect 到 listener_port（现路径，带 context）
          block    → redirect 到 block_sink_port（不带 context）
  → ALE_AUTH_CONNECT
      → 固定 4 条 filter：IS_CONNECTION_REDIRECTED + IP_REMOTE_PORT == block_sink_port
          → FWP_ACTION_BLOCK
  → 兜底：sink 端口上 UDP socket 收到即丢；TCP 端口无 listener（或 bind 不 listen）
```

block 不再展开 per-rule filter。规则表只负责 classify 的首个命中；sink 端口是 block 的
“标记”，固定 filter 只认这个标记。这样顺序、选择器、预算三个问题同时消失。

## 2. 控制面 ABI v8

`probe_config`（仍 64 字节）：

| 偏移 | 字段 | 说明 |
|---:|---|---|
| 0 | `abi_version` | 8 |
| 4 | `enabled` | |
| 8 | `listener_process_id` | 仍写进 `localRedirectTargetPID` |
| 12 | `listener_port` | listener + 闸 1 |
| 16 | `self_process_count` | |
| 20 | `self_process_ids[8]` | |
| 52 | `block_sink_port` | **新增**，u32；0 = 未配置 |
| 56 | `reserved[2]` | 必须 0 |

C++：`probe_ioctl.h` 的 `sizeof/offsetof` 断言；C# 产品镜像 `ProbeConfig.cs` /
`DeviceLayout.cs`；夹具镜像 `tools/wfprobe/Protocol.cs` 的 `ProbeConfig` 与
`ProtocolLayout.Verify`。三侧同批升到 8。

校验矩阵：

| 状态 | 条件 | 行为 |
|---|---|---|
| enable | listener_port/PID 为 0 | 拒绝 |
| 任意 | `block_sink_port == listener_port`（均非 0） | 拒绝（配置不变量） |
| enable | 当前表含 block 或 fallback=block，sink=0 | 拒绝，旧 config 不变 |
| enable | sink 端口与已 enable 的 config 不同 | 拒绝（必须先 disable） |
| disable | sink 任意 | 删除固定 filter |
| set blob（enabled） | 新表含 block/fallback，sink=0 | 拒绝 blob，旧表不变 |
| set blob（disabled） | 任意 | 允许；enable 时再校验 |

## 3. 固定 filter 的生命周期

- 4 条 = `ALE_AUTH_CONNECT_V4/V6` × `TCP(6)/UDP(17)`。
- 条件：
  1. `FWPM_CONDITION_IP_PROTOCOL` = 6/17（`FWP_MATCH_EQUAL`, `FWP_UINT8`）；
  2. `FWPM_CONDITION_IP_REMOTE_PORT` = `block_sink_port`（`FWP_MATCH_EQUAL`, `FWP_UINT16`）；
  3. `FWPM_CONDITION_FLAGS` 含 `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`
     （`FWP_MATCH_FLAGS_ALL_SET`, `FWP_UINT32`）。这是“只命中被我们改道到 sink 的连接”的
     标记，避免误伤任何正常流量。
- action `FWP_ACTION_BLOCK`；`FWPM_SUBLAYER_UNIVERSAL`；weight 沿用
  `auth_filter_weight = 0x10`。它们只增加拒绝，不改变防火墙的拒绝。
- **何时装**：与规则表联动，而不是与 config 常驻。`enable` 时若当前表需要 block filter
  则装；`disable` 时删；`set_rule_blob` 时按新旧表的 `contains_block` 先装新、换表、再删旧。
  换表失败（filter add 失败）时拒绝整块 blob，旧表与旧 filter 保持不变。这样删掉全部 block
  规则后不会残留 filter，也避免无 block 策略常驻 4 条 filter。
- **端口稳定性**：enabled 期间不允许改 `block_sink_port`，因此 filter 的端口条件不需要
  在运行中换值。

### 3.1 条件回退树（Batch 2 VM 实验决定）

1. **首选**：`FLAGS + REMOTE_PORT`。验证 auth 层确实能看到 redirect 后的远端端口和
   `IS_CONNECTION_REDIRECTED`。
2. 若 `FLAGS` 不可用但端口可见：改用 `IP_REMOTE_ADDRESS` = loopback + `REMOTE_PORT`，
   并把所有 block 重定向目标固定为 loopback。前提是 specific-bind 的连接也能 redirect
   到 loopback；Batch 2 验证。
3. 若 auth 层看不到 redirect 后的端口：不装固定 filter，完全依赖 sink socket。TCP 保持
   关闭端口（`10061`），UDP 绑定接收并丢弃。此时 A6 仍满足，但 TCP 错误码与
   `10013` 不同；写入 verification 与 spec。
4. 任何回退都必须保持“只影响 block 改道的连接”，不得退化为按端口误伤正常流量；若无法
   区分，则选方案 3。

## 4. 驱动改动

### 4.1 classify

- 闸 1 扩展：`facts.remote_port == listener_port || facts.remote_port == block_sink_port`
  时放行，避免 sink 自身再次进入 classify。事件仍记 `permitted_listener_port`，注释改为
  “listener 或 block sink 端口”。
- block 分支调用新的 `sink_classification`：
  - `config.block_sink_port == 0` 或 `state.redirect_handle == nullptr` → 失败路径。
  - `probe_build_redirect_target(remote, local, block_sink_port, target, branch)`。
  - `FwpsAcquireClassifyHandle0` → `FwpsAcquireWritableLayerDataPointer0`。
  - 写 `localRedirectTargetPID = config.listener_process_id`（服务持有 redirect handle）、
    `localRedirectHandle = state.redirect_handle`、`localRedirectContext = nullptr`、
    `localRedirectContextSize = 0`、`remoteAddressAndPort = target`。
  - `FwpsApplyModifiedLayerData0`、`FwpsReleaseClassifyHandle0`、`set_permit`。
  - 成功：`matched_block/fallback_block`、`blocked`、`deferred_blocks`、
    `count_udp_block`；事件 `blocked_rule`/`blocked_fallback`，`action=block`，
    `redirect_port=block_sink_port`，`branch`。
  - 失败：`redirect_failures`（+ `apply_failures`/`last_status`），事件 decision
    `block_enforcement_failed`，`action=permit`；不增加 `blocked`/`deferred_blocks`，
    `set_permit` 恢复。**这是已知的 fail-open 边界**：redirect 层无法拦截，只能如实暴露。
- `redirect_classification` 保持带 context 的 proxy 路径；把公共的 acquire/apply/错误处理
  抽成 helper，避免两条路径重复。
- 删除 `set_block` 与 `finish_classify(..., block)` 的 block 参数（redirect 层永远只
  permit）。

### 4.2 `block_filters.cpp`

- 只保留 4 条固定 filter 的构造、安装、删除。
- 删除 `expand_block_protocols`、端口/网段/app-id 条件、四重展开、
  `max_block_filters` 预算检查。
- 新签名：`probe_install_block_filters(std::uint16_t sink_port, UINT64* ids,
  std::uint32_t& count)`；`ids` 固定 4 槽；返回失败时调用方回滚。
- 条件构造失败或 filter add 失败 → 调用方拒绝整块 blob / 拒绝 enable。

### 4.3 `rule_table.cpp` / `rule_table.h`

- 删除 `block_expression_valid`、`block_filter_budget_valid`、`rule_order_valid` 及
  五个 overlap helper（约 200 行）。
- `allocate_rule_table` 只做 `try_open_blob`。
- `contains_block` 变成真实判据：`fallback_action == block || 任一 rule.action == block`；
  供换表时决定是否装/删固定 filter。

### 4.4 `probe_control.cpp`

- `apply_config` 增加第 2 节的校验矩阵；端口变化检查需要读旧 config。
- `enable`：校验后，如当前表需要 filter 且尚未安装，安装 4 条；失败则拒绝 enable。
- `disable`：删除 4 条 filter，再写 config。
- `set_rule_blob`：`allocate_rule_table`（新表）→ 若 `enabled && new.contains_block`，
  安装新 filter（失败则释放新表、保持旧表/旧 filter）→ 换表 → 若 `old.contains_block &&
  !new.contains_block`，删除旧 filter。全部在 `rule_mutex` 下。
- 栈数组从 128 缩到 4。

### 4.5 计数与事件

| 计数 | 新语义 |
|---|---|
| `blocked` | 成功改道到 sink 的 block 决策数 |
| `deferred_blocks` | 同上，block 的“已改道、待 auth filter 执行”次数 |
| `udp_blocked` | UDP block 决策数（成功改道） |
| `redirected` | 仍只统计 proxy redirect |
| `redirect_failures` | 包含 block sink 改道失败 |

`probe_decision` 追加 `block_enforcement_failed = 18`（append-only）；C# `ProbeDecision`
镜像。block 事件保留 `blocked_rule`/`blocked_fallback`。

## 5. 用户态

### 5.1 编译器

- 删除 `RuleOrderValidator.cs`、`RuleOrderSupport.cs` 与相关测试。
- `RuleBlobCompiler`：删除 `ValidateBlockBudget` / `ValidateFallback` / `BlockSelectorError`；
  block 文件名 / 目录选择器不再拒绝；`CompileUnchecked` 不再跳过不存在的检查（保留入口
  作为测试别名，或直接删除并更新调用点）。
- `PolicyLimits.MaxBlockFilters`、`ProbeIoctl.MaxBlockFilters`、`BlobAbiConsistencyTests`
  的 128 互锁删除。
- `tools/wfprobe/RuleBlobOrder.cs` 的镜像校验与 `BlobBuilder.Build` 的调用同步删除；
  `BuildUnchecked` 保留为“只做布局构造”。
- 正向测试：`permit 内网 → block 其余`、block 文件名/目录/数组、fallback=block + permit。

### 5.2 控制器 / 中继（Batch 4）

- 新增 `BlockSink`（`WinForward.Userland/Relay` 或 `Control`）：
  - 选端口：先绑定 UDP v4 `0.0.0.0:0`，得到端口；再绑定 UDP v6 `[::]:port`；TCP v4/v6
    只 `Bind` 不 `Listen`（保留端口，SYN → RST）。任一绑定失败换下一个候选端口。
  - UDP 两个 socket 各起一个接收循环，收到即丢；只计数，不回复。
  - `Dispose` 释放全部 socket。
  - 若实测“bind 不 listen”不发 RST，则 TCP 改为不绑定（保持关闭端口），并在文档记录
    端口被别的进程抢占时的兜底风险。
- `WfpController.Start`：绑定 relay listener 后创建 `BlockSink`，把 `Port` 传给
  `ProbeConfig.EnabledFor`；`VerifyConfig` / `ReportConfig` 增加 sink 字段；停止时先
  disable driver，再释放 sink。
- 产品配置 schema 不新增字段：sink 端口由控制器自动分配。

## 6. 测试与实验

### 6.1 Batch 2 的 VM 实验（决定 3.1 的回退分支）

1. **可见性**：block 规则命中后，client 的 TCP connect 失败；`blocked/deferred_blocks`
   增长；sink 的 UDP 接收计数为 0（说明 auth filter 命中，而非 socket 兜底）。
2. **TCP 错误码**：记录 `10013`（filter 命中）或 `10061`（关闭端口兜底）。
3. **UDP 静默**：`send` 成功、对端无回包、sink 接收计数与 error code。
4. **connected UDP 泄漏**：跑 blocked connected UDP 的 1/3/100 datagram，检查
   `pooltag WfPr` / `Arcl`；不分配 context 时应少于现方案。
5. **兜底**：临时让固定 filter 不匹配（例如只留端口条件或只留 flag），确认 sink socket
   仍给出 RST/静默。

### 6.2 分层验证

| 层 | 入口 |
|---|---|
| Linux 宿主 | `tools/check.sh --full`（format / 有效行 / cppcheck / forbidden / clang-tidy / C# 测试 / C++ host） |
| Windows 驱动 | `Build-Driver`（0 warning）、PREfast、`gate.ps1` |
| 套件 | `wfprobe rules`、`wfprobe udp-rules`、`selftest`、`gates`、`firewall-check` |
| 控制器 | `tools/windows/userland-e2e.ps1`（Batch 4 增加 block 场景） |
| 变异 | `tools/windows/mutation.ps1`、`tools/userland/verify-gates.sh` |

### 6.3 新增/改写的测试

- `HostRuleParitySuite`：
  - `block` 用文件名、精确路径、目录前缀、数组；全部应命中。
  - `permit 内网 → block 其余`：内网成功、其他失败。
  - `fallback=block` + permit：permit 成功、其他失败。
  - 删除 128 预算用例。
- `RuleSuite`：原来的顺序冲突 / fallback 冲突用例改为“接受并生效”；block 断言从
  “auth filter 执行”改为“sink 改道 + auth filter / socket 兜底”。
- `UdpSuite` U6/U7：block 仍静默；TCP 对照允许 `AccessDenied` 或 `ConnectionRefused`
  （Batch 2 实验决定严格度）。
- `VmChecks`：顺序矩阵改为“驱动接受”，用 `CompileUnchecked` 下发；新增 sink 端口
  配置往返。
- ABI/布局：v8 三侧互锁；`probe_config` 偏移 52；`block_enforcement_failed` 镜像。
- 变异：删除 `rule-order-conflict` / `fallback-block-conflict`；新增
  “少装一条协议 filter”“sink 端口条件写错”“block 不 redirect”三类。

## 7. 兼容性与回滚

- **破坏性**：控制面 ABI v8，驱动 / 控制器 / wfprobe 必须同批升级；旧 v7 配置被拒绝。
- **blob 不变**：规则 blob 仍是 v2；旧 blob 可直接使用，sink 端口来自 config。
- **回滚**：整分支回退即可，无数据迁移；旧 per-rule filter 随代码恢复。
- **已知差异**：
  - TCP 错误码可能是 `10061` 而非 `10013`（取决于 3.1 的实验结论）。
  - 已存在的 UDP 关联在热替换后保持旧决策，直到关联结束。
  - classify 内 redirect 失败时 fail-open（有独立事件/计数，无法在 redirect 层拦截）。

## 8. 批次与依赖

| 批次 | 内容 | 依赖 |
|---|---|---|
| 1 | ABI v8 + `block_sink_port` 三侧 plumbing，行为不变 | 无 |
| 2 | 驱动 sink 执行 + 删除旧校验/展开 + wfprobe/VmChecks/变异；VM 实验 | 1 |
| 3 | 用户态编译器放宽 + C# 单测 | 2 |
| 4 | 控制器/中继 `BlockSink` + e2e | 2 |
| 5 | spec/README/AGENTS 对齐 + 全量 Linux/Windows 门禁 + 归档 | 3、4 |

Batch 2 是核心；Batch 3 之前产品编译器仍保守拒绝，驱动已能接受（不产生错误放行）。
