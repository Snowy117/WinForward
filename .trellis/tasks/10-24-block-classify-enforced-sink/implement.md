# 实现计划

## 通用纪律

- 主会话派实现 / 检查子代理；实现者不 `git commit` / `git push`，提交由主会话做。
- 每批固定链条：实现 → 独立检查 → 修复 → Linux 门禁 → 相关 Windows 门禁 → 对齐文档
  → 提交。检查者必须是干净上下文，只看计划 + diff + spec。
- 每批结束报告：改了哪些文件、验证命令与结果、计划未覆盖 / 需要裁决的点。
- 设计偏差（尤其是 §3.1 的回退分支）先改 `design.md`，再继续实现。
- 工作分支：`feat/block-sink`；批次提交信息用 `feat(block): ...` / `test(block): ...` /
  `docs(block): ...`。
- 不在本任务里改 Forwarded 域、blob 布局或 matcher 语义。

## Batch 1：控制面 ABI v8 + sink 端口 plumbing（行为不变）

**范围**

- `src/driver/probe_ioctl.h`：`probe_abi_version = 8`；`probe_config` 的
  `reserved[3]` → `block_sink_port`（u32, offset 52）+ `reserved[2]`；`sizeof/offsetof`
  断言；删除 `max_block_filters`（本批先保留引用，Batch 2 删除）或加
  `block_sink_filter_count = 4`。
- `src/driver/probe_control.cpp`：`apply_config` 接受新字段；本批只做
  `block_sink_port != listener_port` 与 reserved 校验，不改变 block 执行。
- `src/userland/WinForward.Userland/Device/ProbeConfig.cs`：新增 `BlockSinkPort`；
  `EnabledFor` 增加参数或重载（保持旧调用点可编译）；`Disabled()` 置 0。
- `src/userland/WinForward.Userland/Device/ProbeIoctl.cs`、`DeviceLayout.cs`：v8 与偏移。
- `tools/wfprobe/Protocol.cs`：`AbiVersion = 8`；`ProbeConfig` 字段与
  `ProtocolLayout.Verify`。
- 测试：`AbiConsistencyTests`、`DeviceAbiTests`、`LayoutTests`、
  `BlobAbiConsistencyTests` 的版本/偏移；`VmChecks` 的 `EnabledFor` 调用补 0。
- 不改变 `HostRuleParitySuite` / `RuleSuite` 的行为；block 仍由 per-rule filter 执行。

**验证**

```bash
tools/check.sh --full
```

Windows：`Build-Driver` 0 warning；安装后用现有 `wfprobe rules` / `appid` 确认 ABI v8
三侧一致（旧 block 用例仍应绿）。

**提交**

`feat(block): 控制面 ABI v8 增加 block sink 端口`

## Batch 2：驱动 sink 执行 + 删除旧校验/展开 + wfprobe/VmChecks

**范围**

- `probe_classify.cpp`：新增 `sink_classification`；block 分支改道；闸 1 加 sink 端口；
  失败路径 `block_enforcement_failed`；删除 `set_block` / block 参数。
- `block_filters.cpp`：固定 4 条 filter（`FLAGS` + `REMOTE_PORT` + 协议）；删除展开、
  条件池、预算；新签名接收 sink 端口。
- `probe_control.cpp`：enable/disable 与换表时的 4 条 filter 生命周期；校验矩阵；
  128 栈数组缩为 4。
- `probe_driver.h`：`block_filter_ids[4]`；删除 `max_block_filters` 断言；注释更新。
- `probe_ioctl.h`：删除 `max_block_filters`；`deferred_blocks` 注释；
  `probe_decision::block_enforcement_failed = 18`。
- `rule_table.cpp` / `rule_table.h`：删除 `block_expression_valid`、
  `block_filter_budget_valid`、`rule_order_valid` 及 overlap helpers；
  `contains_block` 变成真实判据。
- `redirect_target.cpp`：不需要改；确认 sink 端口复用。
- `tests/host/rule_blob_validation_test.cpp`：删除 block 表达边界 / 预算 / 顺序负向夹具；
  保留布局/UTF-8/选择器夹具。
- `tools/wfprobe`：
  - `Protocol.cs` / `Program.cs`：`--block-sink-port`、decision 镜像、`DeferredBlocks`
    注释。
  - 新增 `BlockSinkBinding` 测试辅助（UDP 绑定丢弃、TCP bind-not-listen 或关闭）。
  - `RuleSuite` / `SelfTest` / `UdpSuite` / `GateSuite` / `HostRuleParitySuite` /
    `BlockProbe`：所有 `ProbeConfig` 设置 sink 端口；block 断言改为 sink 语义。
  - `RuleBlobOrder.cs` / `BlobBuilder.cs`：删除顺序/表达/预算镜像；`Build` 只做布局。
  - `HostRuleParitySuite`：新增/改写 A3/A4/A5 用例；删除 128 预算用例。
  - `RuleSuite`：顺序冲突 / fallback 冲突改为“接受并生效”。
  - `UdpSuite` U6/U7：block 静默；TCP 连接失败（错误码按 §3.1 实验结果）。
  - `VmChecks`：顺序矩阵改为“驱动接受”，用 `CompileUnchecked`；sink 配置往返。
- 变异：
  - `tools/windows/mutation.ps1`：删除 `rule-order-conflict` / `fallback-block-conflict`；
    改写 `block-filter-skip` / `block-filter-protocol`；新增“少装一条协议”“sink 端口
    条件写错”“block 不 redirect”。
  - `tools/userland/verify-gates.sh`：删除 128 / RuleOrderValidator 变异；新增固定 filter
    数量与 sink 端口校验变异。
- **VM 实验矩阵（§6.1）**：可见性、TCP 错误码、UDP 静默、connected UDP 泄漏、兜底。
  结论写入任务 `verification.md`（新建）与 `research/spike-a.md`（若需要）。

**具体落点（按此顺序改）**

1. `probe_ioctl.h`：删除 `max_block_filters`；需要时加 `block_sink_filter_count = 4`；
   `probe_decision` 末尾加 `block_enforcement_failed = 18`。
2. `probe_driver.h`：`block_filter_ids[4]`；`probe_install_block_filters(std::uint16_t sink_port,
   UINT64* ids, std::uint32_t& count)`；删除 128 断言；`contains_block` 注释改为“是否需要
   固定 sink filter”。
3. `rule_table.cpp`：删除 overlap / expression / budget / order helpers 与
   `allocate_rule_table` 中的四连拒绝；在 `allocate_rule_table` 返回前设置
   `contains_block = fallback == block || 任一 rule.action == block`。
4. `block_filters.cpp`：缩成固定 4 条；条件顺序 `IP_PROTOCOL`、`IP_REMOTE_PORT`、
   `FWPM_CONDITION_FLAGS`（`FWP_MATCH_FLAGS_ALL_SET`，值
   `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED` = `0x00100000`）；action BLOCK、
   sublayer/weight 不变；失败由调用方回滚。
5. `probe_classify.cpp`：`redirect_classification` 保留 proxy（带 context），新增
   `sink_classification`（block，无 context）；block 分支调后者；闸 1 加
   `remote_port == config.block_sink_port`；失败路径 `block_enforcement_failed` +
   `redirect_failures` + `set_permit`，不增 `blocked`。
6. `probe_control.cpp`：
   - `apply_config` enable：`state.rules->contains_block` 且 `block_sink_port == 0` → 拒绝；
     否则按 sink 端口装 4 条。
   - `apply_rule_blob`：`new_needs = table->contains_block`；enabled && new_needs &&
     sink == 0 → 释放新表、拒绝；enabled && new_needs → 先装新 filter；换表后若旧表有
     filter 且新表没有 → 删除；新旧都有时保留（enabled 期间端口不变）。
   - enabled 期间改 sink 端口 → 拒绝（design §2）。
7. `tools/wfprobe`：新增 `BlockSinkBinding`（UDP 绑定丢弃；TCP 不 bind 或 bind-not-listen）；
   所有 `ProbeConfig` 初始化补 sink；`RuleSuite` 顺序 / fallback 用例改用
   `BlobBuilder.Build`（Batch 2 产品编译器仍保守）；`HostRuleParitySuite` 正向用例；
   `UdpSuite` U6/U7；`VmChecks` 顺序矩阵用 `CompileUnchecked` 断言驱动接受。
8. 变异：`mutation.ps1` 与 `verify-gates.sh` 按上面“变异”条目删改。

**验证**

```bash
tools/check.sh --full
# Windows VM（按 tools/windows/README.md 的既有流程）
wfprobe rules
wfprobe udp-rules --only U6,U7 --timeout-ms 10000
wfprobe gates
tools/windows/firewall-check.ps1
```

若 auth 层可见性不成立，按 design.md §3.1 回退，并重新跑上述套件；不要为了绿而放宽断言，
先记录实际错误码/行为。

**提交**

`feat(block): block 改为 classify 改道 sink，删除顺序与预算限制`

## Batch 3：用户态编译器放宽 + C# 单测

**范围**

- 删除 `RuleOrderValidator.cs`、`RuleOrderSupport.cs`、`RuleOrderTests.cs`、
  `RuleOrderProcessTests.cs`。
- `RuleBlobCompiler.cs`：删除 `ValidateBlockBudget` / `ValidateFallback` /
  `BlockSelectorError`；`CompileUnchecked` 语义更新。
- `PolicyLimits.MaxBlockFilters`、`ProbeIoctl.MaxBlockFilters` 删除。
- 测试：`CompilerSelectorTests`（文件名/目录改为接受）、`CompilerLimitTests`（预算用例
  删除）、`CompilerTests`（fallback=block）、`AbiConsistencyTests`、
  `BlobAbiConsistencyTests`、`PriorityCases` / `NetworkCases` 的描述。
- 新增正向用例：`permit 内网 → block 其余`、block 文件名/目录/数组、
  `fallback=block` + permit。
- `tools/wfprobe` 若还有 `MaxBlockFilters` / `RuleBlobOrder` 引用，一并清理。

**验证**

```bash
tools/check.sh --full
```

Windows：`gate.ps1`（至少 `rules` / `udp-rules` / `vm-check` / `firewall-check`）。

**提交**

`feat(block): 配置层接受 block 顺序、目录/文件名与 fallback 组合`

## Batch 4：控制器 / 中继 BlockSink + e2e

**范围**

- 新增 `BlockSink`（建议 `src/userland/WinForward.Userland/Relay/BlockSink.cs`）：
  UDP v4/v6 绑定并丢弃；TCP v4/v6 bind-not-listen 或关闭；端口分配与释放。
- `WfpController.cs`：创建/释放 sink，传入 `ProbeConfig`；`VerifyConfig` /
  `ReportConfig`。
- `ControllerSettings` / `RunCommand` 如需要，只做内部端口传递，不扩产品 schema。
- `tools/windows/userland-e2e.ps1`：新增 block 配置场景（TCP 失败、UDP 静默、
  process/目录选择器）。
- 单元测试：`RelayPumpTests` 或新增 `BlockSinkTests`（端口分配、释放、UDP 丢弃）。

**验证**

```bash
tools/check.sh --full
# Windows VM
tools/windows/userland-e2e.ps1
```

**提交**

`feat(block): 控制器绑定 block sink 端口并接入 e2e`

## Batch 5：文档 / spec 对齐 + 全量门禁 + 收尾

**范围**

- `.trellis/spec/backend/host-rule-blob.md`：删除 §7 / §8，改写 §6.2 / §9 / §10。
- `.trellis/spec/backend/wfp-architecture.md`：D4、§7.9、§7.10、§7.11。
- `src/driver/README.md`、`src/userland/README.md`、`AGENTS.md`、
  `tools/windows/README.md`。
- `verification.md`：VM 实验、错误码、泄漏、热替换结论。
- 全量门禁：

```bash
tools/check.sh --full
# Windows VM
pwsh -File tools/windows/gate.ps1 -Root C:\workspace\WinForward.Kernel -Remote <host>:<port>
```

- 任务 `task.py finish` / `archive`；更新 workspace journal。
- 最终验收按 PRD 的 A1–A10 逐条勾选。

**提交**

`docs(block): 对齐 sink 方案 spec 与门禁记录`

## 回滚点

- Batch 1 失败：只回退 ABI 改动，旧 per-rule block 仍可用。
- Batch 2 失败且 §3.1 无可行回退：回退 Batch 2，保留 ABI v8 的 sink 字段但不启用，
  在任务里记录否决理由（不半途保留双机制）。
- Batch 3/4 失败：回退对应批次，不影响已通过的驱动语义。
- 任何批次都要先 `git status --porcelain` 确认没有用户的未提交改动，再提交。
