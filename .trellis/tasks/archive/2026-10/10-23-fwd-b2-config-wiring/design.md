# 技术设计：B2 Forwarded 配置、多上游与 clientCidr 接线

## 1. JSON schema 扩展

旧字段保持不变：`fallbackAction`、`socks5`、`rules`。新增：

```json
{
  "socks5Servers": [
    { "name": "main", "host": "proxy.example.com", "port": 1080 }
  ],
  "forwarded": {
    "fallbackAction": "permit",
    "rules": [
      {
        "name": "vm-tcp",
        "adapterName": "vEthernet (MyVM)",
        "clientCidr": "10.0.0.0/24",
        "remoteCidr": "203.0.113.0/24",
        "remotePorts": "443",
        "protocol": "tcp",
        "family": "ipv4",
        "action": "redirect",
        "target": "main"
      }
    ]
  }
}
```

约定：

| 字段 | 必填 | 说明 |
|---|---|---|
| `socks5Servers[].name` | 是 | 非空、唯一（OrdinalIgnoreCase） |
| `socks5Servers[].host/port` | 是 | 与旧 `socks5` 同规则（host 非空、port 1..65535） |
| `forwarded.fallbackAction` | 否 | `permit` / `block`，默认 `permit` |
| `forwarded.rules[].action` | 是 | `permit` / `block` / `redirect`；`redirect` 即包版 `proxy` |
| `forwarded.rules[].target` | redirect 必填 | 引用命名上游；`default` 指向旧 `socks5` |
| `adapterId` / `adapterName` | 否 | 可单独给或同时给；同时给必须解析为同一 id |
| `clientCidr` / `remoteCidr` | 否 | 单个 CIDR；与 `family` 一致 |
| `remotePorts` | 否 | `*`、单端口、闭区间；默认 `*` |
| `protocol` / `family` | 否 | 与 host 规则同枚举 |
| `process` | 禁止 | forwarded 没有宿主进程，出现即配置错误 |

旧 `socks5` 如存在，视为名为 `default` 的上游；`socks5Servers` 里再定义 `default` 视为冲突。
`target` 先查 `socks5Servers`，再查旧 `socks5`。

## 2. 适配器解析接缝

新增 `src/userland/WinForward.Userland/Forwarded/IAdapterIdResolver.cs`：

```csharp
public interface IAdapterIdResolver
{
    bool TryResolve(string? adapterId, string? adapterName, out uint id, out string error);
}
```

- B2 只在配置解析时调用；B4 提供 Windows 实现（stable ID/名字 → 接口 id）。
- 规则带 adapter 选择器而 resolver 为 null → `ConfigurationException`，路径 `forwarded.rules[i]`。
- 两个选择器都给时，解析结果必须一致；否则报错。
- 解析结果 0xFFFFFFFF（B1 的“任意”哨兵）视为错误；具体 id 写进 `ForwardedPolicyRule.IngressAdapterId`。

## 3. 解析与数据结构

`PolicyConfigurationDocument`：

- `Load(string path, IAdapterIdResolver? adapterResolver = null)`
- `LoadConfiguration(string path, IAdapterIdResolver? adapterResolver = null)`
- `Parse(string json, IAdapterIdResolver? adapterResolver = null)`
- `ParseConfiguration(string json, IAdapterIdResolver? adapterResolver = null)`

默认参数保证现有调用点不改；带 adapter 的 forwarded 配置必须显式传 resolver。

`ControllerConfiguration` 增加：

```csharp
public ForwardedPolicyTable? Forwarded { get; init; }
public IReadOnlyDictionary<string, Socks5Endpoint> Upstreams { get; init; } = ...;
```

保留 `Policy` 与 `Socks5`。`Upstreams` 含 `socks5Servers` 与旧 `socks5` 的 `default`。

解析流程：

1. `RejectUnknownProperties(root, "", "fallbackAction", "rules", "socks5", "socks5Servers", "forwarded")`。
2. 解析旧 host 配置（不变）。
3. 解析 `socks5Servers`（新）：数组、每项 name/host/port、重名拒绝。
4. 解析旧 `socks5`；若存在且 `socks5Servers` 已含 `default` → 冲突。
5. 解析 `forwarded`（可选）：fallback、rules；每条规则走 §2 的 adapter 解析、target 查找、
   CIDR/端口/族校验；构造 `ForwardedPolicyRule`。
6. 返回 `ControllerConfiguration`。

诊断路径统一 `forwarded.rules[i].<field>`；target 错误带规则名（若有）。

## 4. 编译与控制器接线

- `RunCommand` / `CheckCommand` 构造 `ControllerSettings` 时：
  - `settings.Forwarded = configuration.Forwarded`；
  - `settings.ForwardedBlob = configuration.Forwarded is null ? null : ForwardedBlobCompiler.Compile(configuration.Forwarded)`；
  - `settings.Upstreams = configuration.Upstreams`。
- `ControllerSettings` 增加：`Forwarded`、`ForwardedBlob`、`Upstreams`。
- `WfpController.Start`：若 `ForwardedBlob` 非空，只报告
  `controller-forwarded count=<rules> upstreams=<n> blob=<bytes>`；**不调用任何新 IOCTL**。
  现有启动/清理顺序不变。
- `CheckCommand`：报告 forwarded 规则数/上游数；编译失败走现有退出码路径。

## 5. 校验清单（配置层）

| 条件 | 行为 |
|---|---|
| 旧配置无 `forwarded` | 合法，`Forwarded = null` |
| `forwarded.rules` 非数组 | 错误 |
| 规则含 `process` | 错误（forwarded 没有宿主进程） |
| `redirect` 无 `target` | 错误 |
| `permit/block` 带 `target` | 错误（避免静默无效配置） |
| `target` 不存在 | 错误，带规则路径 |
| `socks5Servers` 重名 / 与 `default` 冲突 | 错误 |
| `adapterId` + `adapterName` 同时给 | 解析结果必须一致，否则错误 |
| 有 adapter 但无 resolver | 错误 |
| `clientCidr`/`remoteCidr` 非法或族冲突 | 错误（消息带 `forwarded.rules[i]`） |
| `remotePorts` 颠倒/越界 | 错误 |
| `fallbackAction` 非 permit/block | 错误 |

B1 的 `ForwardedBlobCompiler` 仍做最终编译校验；配置层提前给出更可读的 domain-qualified 诊断。

## 6. 测试

新增 `ForwardedConfigurationTests.cs`（注册到 `Program.cs` 的 `RegisterAll()`）：

1. 最小 forwarded 配置解析成功；`Forwarded.Rules.Count`、fallback、adapter id、client/remote CIDR。
2. 多上游：两条规则引用不同 target；`Upstreams` 映射正确。
3. 旧 `socks5` 作为 `default`；`target: "default"` 正确解析。
4. 非法：`process`、redirect 无 target、permit 带 target、target 缺失、重名、adapter 冲突、
   无 resolver、CIDR 族冲突、端口颠倒、fallback redirect。
5. 编译：解析后的 `ForwardedPolicyTable` 经 `ForwardedBlobCompiler.Compile` 产出合法 blob；
   读回 header/rule，断言规则字段与网段索引。
6. 现有 `ConfigurationTests` 全绿。

`FakeAdapterIdResolver` 放在测试工程内，按 name/id 映射到固定 uint。

差分：B2 不做；B4 用真实配置生成 C#→C++ 夹具（检查者建议的落点）。

## 7. 文档

- `PolicyConfigurationDocument` 的 XML 注释更新 schema。
- `.trellis/spec/backend/forwarded-rule-blob.md` 增加“配置映射”一节（字段 → 模型 → blob）。
- 父任务 `design.md` 的 D3 补充：配置字段落在本仓库暂定 JSON 上，语义对齐 `Forwarded.Rules`。

## 8. 风险

| 风险 | 处理 |
|---|---|
| 旧 `Load`/`Parse` 调用点被可选参数破坏 | 默认参数，编译期检查；现有测试保证 |
| adapter 解析在 B2 没有真实实现 | 接口 + fake；B4 接 Windows API；配置层不猜 |
| 多上游与旧单上游语义冲突 | `default` 冲突显式拒绝，文档写明 |
| forwarded 规则编译失败难诊断 | 配置层先给 domain-qualified 错误；编译器再做最终校验 |
| 控制器误发送未实现 IOCTL | B2 只报告，不新增设备调用；B4 才接线 |
