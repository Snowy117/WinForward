# 技术设计：B1 Forwarded 规则 blob 与匹配器

## 1. ABI（v1）

### 1.1 header（40 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | magic | `0x31424657`（内存字节 `W F B 1`） |
| 4 | u16 | version | 1 |
| 6 | u16 | header_size | 40，必须精确等于结构体尺寸 |
| 8 | u32 | total_size | 必须等于缓冲区长度 |
| 12 | u32 | rule_count | |
| 16 | u32 | fallback_action | 只允许 permit(0) / block(1) |
| 20 | u32 | rules_offset | 4 字节对齐，≥ header_size |
| 24 | u32 | network_pool_offset | 4 字节对齐，≥ header_size |
| 28 | u32 | network_pool_size | 必须是 20 的倍数 |
| 32 | u32 | reserved | 必须为 0 |
| 36 | u32 | reserved2 | 必须为 0 |

### 1.2 rule（24 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | ingress_adapter_id | `0xFFFFFFFF` = 任意入口 |
| 4 | u32 | client_network_index | `0xFFFFFFFF` = 不限；否则是 network_pool 字节偏移 |
| 8 | u32 | remote_network_index | 同上 |
| 12 | u16 | remote_port_start | 含 |
| 14 | u16 | remote_port_end | 含；开始 > 结束即非法 |
| 16 | u32 | upstream_id | `0xFFFFFFFF` = 无；redirect 必须有，非 redirect 必须无 |
| 20 | u8 | protocol | 0 any / 6 TCP / 17 UDP（复用 `rule_protocol_*`） |
| 21 | u8 | family | 0 any / 4 IPv4 / 6 IPv6（复用 `rule_family_*`） |
| 22 | u8 | action | 复用 `rule_action`：permit(0) / block(1) / redirect(2) |
| 23 | u8 | reserved | 必须为 0 |

### 1.3 network_pool

复用 `src/common/rule_blob.h` 的 `network_prefix`（20 字节：prefix_length、family、
reserved[2]、network[16]），不复制定义。池内记录按 20 字节对齐，索引是池内字节偏移。

### 1.4 段布局

```
[0, 40)                      header
[rules_offset, ...)          rule_count × 24
[network_pool_offset, ...)   network_pool_size（20 的倍数）
```

段之间允许 4 字节对齐填充；三段互不重叠。`total_size` 是完整缓冲区长度。

## 2. C++ API

### 2.1 `src/common/forwarded_blob.h`

```cpp
namespace winforward::wfp {

inline constexpr std::uint32_t forwarded_blob_magic = 0x31424657U; // "WFB1"
inline constexpr std::uint16_t forwarded_blob_version = 1;
inline constexpr std::uint32_t forwarded_adapter_any = 0xFFFFFFFFU;
inline constexpr std::uint32_t forwarded_upstream_none = 0xFFFFFFFFU;

struct forwarded_blob_header { ... };   // 40 字节，static_assert 锁偏移
struct forwarded_blob_rule { ... };     // 24 字节，static_assert 锁偏移

struct forwarded_blob_view
{
    const std::uint8_t* data;
    std::uint32_t size;
    const forwarded_blob_header* header;
    rule_action fallback_action;
    const forwarded_blob_rule* rules;
    std::uint32_t rule_count;
    const network_prefix* network_pool;
    std::uint32_t network_pool_count;
};

[[nodiscard]] inline bool try_open_forwarded_blob(
    const std::uint8_t* data, std::uint32_t size, forwarded_blob_view& out) noexcept;
}
```

`try_open_forwarded_blob` 的校验顺序与 host `try_open_blob` 对齐：空指针/对齐 → 最短长度 →
magic/version/header_size → total_size → fallback 白名单 → 段对齐/段起点/段越界 →
规则数组越界 → 段重叠 → 网段池整除与逐条合法性 → 每条规则字段（reserved/action/protocol/
family/端口区间/upstream 与 action 的组合/两个 network 索引的越界与对齐/规则族与网段族一致）。
失败时 `out` 保持不变。

### 2.2 `src/common/forwarded_matcher.h`

```cpp
struct forwarded_connection_view
{
    std::uint32_t ingress_adapter_id;
    std::uint8_t client_address[16]; // IPv4 只用前 4 字节
    std::uint8_t remote_address[16];
    std::uint16_t remote_port;
    std::uint8_t protocol;
    address_family family;           // 复用 redirect_context.h
};

struct forwarded_match_result
{
    rule_action action;
    std::uint32_t rule_index;        // 未命中为 rule_pool_index_any
    std::uint32_t upstream_id;       // 未命中/非 redirect 为 forwarded_upstream_none
    bool matched;
};

[[nodiscard]] inline forwarded_match_result
match_forwarded(const forwarded_blob_view& view,
                const forwarded_connection_view& connection) noexcept;
```

匹配顺序（全部 AND，任一失败继续下一条；顺序影响性能不影响语义）：

1. `ingress_adapter_id`：任意或等于连接；
2. `family`：任意或等于连接；
3. `protocol`：任意或等于连接；
4. 远端端口区间；
5. `remote_network_index`：查池、族相等、前缀匹配 `remote_address`；
6. `client_network_index`：查池、族相等、前缀匹配 `client_address`。

首个命中返回 action/rule_index/upstream_id；未命中返回 `view.fallback_action`、
`rule_index = rule_pool_index_any`、`upstream_id = forwarded_upstream_none`、`matched = false`。
`upstream_id` 只在 action 为 redirect 时有意义；调用方以 `matched` 为准。

## 3. C# 类型与编译器

新目录 `src/userland/WinForward.Userland/Forwarded/`：

| 文件 | 内容 |
|---|---|
| `ForwardedPolicyRule.cs` | `Name`、`uint? IngressAdapterId`、`RemoteNetwork? ClientNetwork`、`RemoteNetwork? RemoteNetwork`、`RuleAddressFamily Family`、`RuleProtocol Protocol`、`PortRange RemotePorts`、`RuleAction Action`、`uint? UpstreamId` |
| `ForwardedPolicyTable.cs` | `IReadOnlyList<ForwardedPolicyRule> Rules`、`RuleAction FallbackAction` |
| `ForwardedBlobLayout.cs` | Magic/Version/HeaderSize(40)/RuleSize(24)/NetworkPrefixSize(20)/SegmentAlignment(4)/AdapterAny/UpstreamNone/PoolIndexAny |
| `ForwardedBlobHeader.cs` | `[StructLayout(Pack = 4)]` 镜像，字段顺序按 §1.1 |
| `ForwardedBlobRule.cs` | `[StructLayout(Pack = 4)]` 镜像，字段顺序按 §1.2 |
| `ForwardedBlobCompiler.cs` | `Compile(ForwardedPolicyTable)`；校验 + 段布局 + 写字节 |
| `ForwardedPolicyLimits.cs` | `MaxRules = 4096`、`MaxNetworkPoolEntries = 4096` |

复用：
- `RemoteNetwork`（CIDR 解析/归一化/族）；
- `PortRange`、`RuleAction`、`RuleProtocol`、`RuleAddressFamily`；
- `NetworkPoolBuilder`（internal，同一程序集可用）与 `RuleBlobNetworkPrefix` 写网段池；
- `PolicyCompileException`（规则序号诊断）。

### 3.1 编译期校验

1. fallback 只能是 Permit / Block；Redirect 拒绝。
2. action 只能是 Permit / Block / Redirect。
3. `Redirect` 必须有 `UpstreamId`，且不能等于 `0xFFFFFFFF`；非 Redirect 不得设置 `UpstreamId`。
4. `IngressAdapterId` 若设置，不能等于 `0xFFFFFFFF`（该值是"任意"哨兵）。
5. `Family` 具体时，`ClientNetwork`/`RemoteNetwork` 的族必须与之一致；两个网段都具体且族不同时拒绝。
6. `RemotePorts.Begin <= End`。
7. 规则数 ≤ `MaxRules`；网段池条目数 ≤ `MaxNetworkPoolEntries`。
8. reserved 全部写 0。

### 3.2 字节写入

段序与 host 编译器一致：header → rules → network_pool。`rules_offset = HeaderSize`，
`network_pool_offset = AlignUp(rules_offset + rule_count * RuleSize, 4)`，`total_size =
network_pool_offset + network_size`。使用 `MemoryMarshal.Write` 写每条结构；网段池用
`NetworkPoolBuilder.ToArray()` 的 `RuleBlobNetworkPrefix` 逐条写。

## 4. 测试

### 4.1 C++

新增 `tests/host/forwarded_blob_builder.h`（构造合法 blob 的测试辅助）与两个测试文件：

- `forwarded_blob_test.cpp`：空表、单规则/多规则、各 action、v4/v6 网段、合法边界；
  拒绝坏 magic/version/header_size/total_size/未对齐/越界/重叠/池不整除/坏 prefix/
  rule.reserved/未知 action/protocol/family/端口颠倒/redirect 无 upstream/非 redirect 带
  upstream/网段索引越界或未对齐/规则族与网段族不一致。
- `forwarded_matcher_test.cpp`：adapter 任意/具体、client CIDR、remote CIDR、端口区间、
  协议、族、顺序首命中、fallback、未命中、IPv6。

`tests/host/CMakeLists.txt` 把两个新文件加入 `wf_common_tests`（或新增独立 target，
优先加入现有 target，减少构建面）。

### 4.2 C#

新增 `ForwardedCompilerTests.cs`、`ForwardedLayoutTests.cs`，并在
`src/userland/WinForward.Userland.Tests/Program.cs` 的 `RegisterAll()` 中注册：

- 编译空表 → header 字段正确；
- 每条规则字段/偏移/网段池偏移正确；
- 非法输入逐条拒绝（与 §3.1 对应）；
- 网段去重/首次出现顺序；
- 布局尺寸/偏移断言（与 C++ `static_assert` 对齐）。

### 4.3 差分（条件项）

先看 `DifferentialFixtureWriter`/`differential_fixture.h` 的现有格式：
- 若能在同一夹具里加 forwarded 段而不破坏 host 用例，就加一组 forwarded 用例；
- 若格式耦合 host 语义，则本批不做，报告里说明并留给 B2/B4。

## 5. spec

新增 `.trellis/spec/backend/forwarded-rule-blob.md`：ABI 表、匹配顺序、校验规则、容量上限、
与 host blob 的边界（不共享 magic/version，只共享 `network_prefix`/动作/协议/族常量）。

## 6. 风险

| 风险 | 处理 |
|---|---|
| 两侧字段顺序/尺寸漂移 | C++ `static_assert` + C# 布局测试 + 字节级断言；条件允许时加差分 |
| 复用 `NetworkPoolBuilder` 的内部可见性 | 同程序集，internal 可用；若不方便就在 Forwarded 内做 20 行等价实现，并在报告说明 |
| 与 host 常量重复定义 | 只复用 `rule_blob.h` 的常量，不复制 |
| 规则语义过严导致配置难写 | 严格只在"redirect 必须带 upstream / 非 redirect 不得带"这类明确歧义上；其余按 AND 语义 |
