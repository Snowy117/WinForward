# B2：Forwarded 配置、多上游与 clientCidr 接线

## Goal

把 B1 的 forwarded 规则层接进现有 JSON 配置与控制器：新增 `socks5Servers[]` 多上游和
`forwarded` 段（fallback + rules），支持 `adapterId/adapterName`、`clientCidr`、
`remoteCidr`、`remotePorts`、`protocol`、`family`、`action`、`target`；解析适配器、校验
target/族/端口，并编译出 `wf_forwarded_blob`。B2 不接驱动 IOCTL（B4 接）。

## Background

- B1 已交付 `ForwardedPolicyTable`、`ForwardedBlobCompiler` 与 C++ 解码/匹配器。
- 当前 `PolicyConfigurationDocument` 只有 `fallbackAction`、单个 `socks5`、host `rules`；
  没有 forwarded 域，也没有多上游。
- `WfpController` 目前只下发 host 规则 blob；forwarded 的驱动侧（IOCTL/redirect 表）
  是 B4。B2 的目标是配置与编译链闭合，控制器能编译/报告但不发送 forwarded blob。
- WinForward 包版的语义是 `Forwarded.Rules` + `adapterId/adapterName` 选入口 + `target`
  选 SOCKS5；本任务沿用这套语义，只是配置字段落在本仓库的暂定 JSON 上。

## In Scope

1. JSON 新增 `socks5Servers[]`：`name`（唯一，非空）、`host`、`port`；旧 `socks5` 保留，
   视为名为 `default` 的单上游。
2. JSON 新增 `forwarded` 段：
   - `fallbackAction`：`permit` / `block`，默认 `permit`；
   - `rules[]`：`name`、`adapterId`、`adapterName`、`clientCidr`、`remoteCidr`、`remotePorts`、
     `protocol`、`family`、`action`、`target`。
3. 适配器解析接缝：`IAdapterIdResolver`（B4 提供 Windows 实现，B2 用测试 fake）；
   规则带 adapter 选择器但未提供 resolver 时，配置期报错。
4. 配置 → `ForwardedPolicyTable` → `ForwardedBlobCompiler` 的编译链；校验 target 存在、
   `redirect` 必须有 target、`process` 字段拒绝、CIDR/端口/族合法。
5. `ControllerConfiguration` / `ControllerSettings` 携带 forwarded 表、上游映射与编译产物；
   `WfpController.Start` 报告 forwarded 规则数/上游数（不发送 IOCTL）。
6. 测试：配置成功/失败、多上游、clientCidr、adapter 解析、编译字节断言、现有 host 配置不变。
7. 文档：spec 的配置映射一节 + `PolicyConfigurationDocument` 的 schema 注释。

## Out of Scope

- 驱动 IOCTL、redirect 表、classify（B4/B5）。
- 真实 Windows 适配器枚举（B4 的 `IAdapterIdResolver` 实现）。
- forwarded 差分夹具（B1 已留到 B2/B4；本批若易做则做，否则留 B4）。
- 配置热加载、GUI、服务宿主。
- `clientCidr` 多值/列表（B1 模型是单个 CIDR；列表留后续）。

## Requirements

- **R1 兼容**：旧配置（只有 `fallbackAction`/`socks5`/`rules`）继续通过；host 行为不变。
- **R2 多上游**：`socks5Servers` 的 name 唯一（OrdinalIgnoreCase），非空；`target` 先查
  命名上游，再查 `socks5`（名字 `default`）；找不到即配置错误。
- **R3 forwarded 规则**：字段集固定；`action=redirect` 必须有 `target`；`permit/block`
  不得有 `target`；`process` 在 forwarded 规则里直接拒绝（与包版一致）。
- **R4 适配器**：`adapterId`/`adapterName` 可单独给，也可同时给；至少给一个时必须
  有 resolver；同时给时解析结果必须一致；解析结果写入 `IngressAdapterId`；解析失败给
  domain-qualified 诊断。
- **R5 校验**：`clientCidr`/`remoteCidr` 是合法 CIDR；与 `family` 一致；端口区间合法；
  `fallbackAction` 只 permit/block；上游名字/端口合法。
- **R6 编译**：配置能编译出合法 `wf_forwarded_blob`；规则数/网段数与 B1 limits 一致；
  诊断带 `forwarded.rules[i]` 路径。
- **R7 控制器**：`WfpController.Start` 在 forwarded 表非空时报告规则数与上游数；不向驱动
  发送未实现的 IOCTL，不改变现有启动/清理顺序。

## Acceptance Criteria

- [ ] **A1 旧配置兼容**：现有 `ConfigurationTests` 全绿；只写 `socks5`/`rules` 的配置行为不变。
- [ ] **A2 多上游**：两条 forwarded 规则引用不同命名上游；`target` 解析正确；重名/缺失
      给带路径的错误。
- [ ] **A3 clientCidr**：同一入口下 10.0.0.0/24 与 10.0.1.0/24 能编译成两条规则，
      `client_network_index` 不同且匹配语义与 B1 一致。
- [ ] **A4 adapter**：`adapterName` 经 fake resolver 解析成 id；解析失败/未提供 resolver
      时配置期拒绝；`adapterId`+`adapterName` 冲突时拒绝。
- [ ] **A5 编译**：配置编译出的 blob 能被 B1 的 C# 布局/字节断言接受；规则顺序、fallback、
      网段池正确。
- [ ] **A6 控制器报告**：`WfpController.Start` 的 report 含 forwarded 规则数/上游数；
      不新增驱动调用。
- [ ] **A7 门禁**：`dotnet build` 零警告；`dotnet run ... -- test` 全绿；
      `tools/check.sh --fast` 通过；`cmake --build build --target wf_check` 通过。

## Notes

- `forwarded` 的 `action` 沿用本仓库枚举 `permit/block/redirect`，文档注明 `redirect` 即
  包版的 `proxy`。
- `adapterId` 是字符串（稳定的 GUID/名字），不是 B1 的 uint32；转换只在配置层发生，
  `ForwardedPolicyTable` 仍只持有 uint32。
- 本批不碰 `src/common/*`、驱动、B1 的 ABI。
