# 设计 — 规则 blob 与 matcher

## 数据格式

所有多字节字段为主机字节序（Windows x64 小端），全部 4 字节对齐。

### 头部 `rule_blob_header`（40 字节）

blob 必须自描述：所有段的位置都由头部给出，不靠推断。

| 偏移 | 字段 | 类型 | 说明 |
|---|---|---|---|
| 0 | `magic` | `uint32` | `0x31425257`，内存中为 `"WRB1"` |
| 4 | `version` | `uint16` | 当前为 1 |
| 6 | `header_size` | `uint16` | 头部字节数，必须等于 `sizeof(rule_blob_header)` |
| 8 | `total_size` | `uint32` | 整个 blob 的字节数 |
| 12 | `rule_count` | `uint32` | 规则条数 |
| 16 | `fallback_action` | `uint32` | 未命中任何规则时的动作（同 `action` 取值） |
| 20 | `rules_offset` | `uint32` | 规则数组起点 |
| 24 | `process_pool_offset` | `uint32` | 进程串池起点 |
| 28 | `process_pool_size` | `uint32` | 进程串池字节数 |
| 32 | `network_pool_offset` | `uint32` | 网段池起点 |
| 36 | `network_pool_size` | `uint32` | 网段池字节数 |

`fallback_action` 放进 blob，是为了让"规则表 + 兜底动作"作为一个整体原子替换；拆成两处会有一致性窗口。

## 布局不变量（解码时必须逐条校验）

- `header_size == 40`、`version == 1`、`magic` 匹配、`total_size` 等于调用方传入的长度；
- `rules_offset`、两个池的 offset/size 全部落在 `[header_size, total_size]` 内且 4 字节对齐。**零长度段允许落在 `total_size` 端点**——否则 40 字节的空规则表无法表达，而空表是 PRD 明确要求支持的形态；
- 规则数组 `[rules_offset, rules_offset + rule_count * 16)` 不越过 `total_size`，且不与两个池重叠；
- `process_pool_size` 不为 0 时，池的最后 1 字节必须是 `\0`；每个 `process_index` 必须 `< process_pool_size` 且指向池内的 NUL 结尾串；
- `network_pool_size % 20 == 0`；每个 `network_index` 必须 `< network_pool_size` 且是 20 的倍数；
- `fallback_action` 与每条规则的 `action` 必须是已知取值，`reserved` 必须为 0；
- 字段取值白名单：`protocol ∈ {0, 6}`，`family ∈ {0, 4, 6}`，`remote_port_start <= remote_port_end`，网段记录的 `family ∈ {4, 6}`、`prefix <= 32/128`、`reserved == 0`，且规则的 `family` 与它所引网段记录的 `family` 必须一致（`0` 除外）。

以上是**硬约束**：违反任意一条，整个 blob 被拒绝，不是逐规则丢弃。

校验在 `try_open_blob` 里一次做完，之后 `match` 的扫描不再判界。
### 规则 `rule`（16 字节）

| 字段 | 类型 | 说明 |
|---|---|---|
| `process_index` | `uint32` | 进程池内字节偏移；`0xFFFFFFFF` = 任意 |
| `network_index` | `uint32` | 网段池内字节偏移；`0xFFFFFFFF` = 任意 |
| `remote_port_start` | `uint16` | 闭区间起点；`0` + `end` `0xFFFF` = 任意 |
| `remote_port_end` | `uint16` | 闭区间终点 |
| `protocol` | `uint8` | `0` = 任意，`6` = TCP |
| `family` | `uint8` | `0` = 任意，`4` / `6` |
| `action` | `uint8` | `0` permit，`1` block，`2` redirect |
| `reserved` | `uint8` | 必须为 0 |

### 池

- **进程池**：连续的 NUL 结尾小写字符串，`process_index` 指向串首字节。
- **网段池**：定长 20 字节一条 —— `prefix_length:u8`、`family:u8`、`reserved:u8[2]`、`network:u8[16]`。

各段依次排列，段首 4 字节对齐。

## 接口

```cpp
namespace winforward::wfp
{
struct connection_view
{
    const char* process_path;        // 已小写化
    std::uint32_t process_path_length;
    std::uint8_t remote_address[16];
    std::uint16_t remote_port;
    std::uint8_t protocol;
    address_family family;
};

enum class rule_action : std::uint8_t { permit = 0, block = 1, redirect = 2 };

struct match_result
{
    rule_action action;
    std::uint32_t rule_index;   // 未命中时为 0xFFFFFFFF
    bool matched;
};

// 解码 + 校验；失败返回 false，out 不产生任何有效视图
[[nodiscard]] bool try_open_blob(const std::uint8_t* data, std::uint32_t size, blob_view& out) noexcept;

// 首个命中；未命中时调用方使用配置的 fallback
[[nodiscard]] match_result match(const blob_view& view, const connection_view& connection) noexcept;
}
```

## 实现约束

- 只用指针 + 偏移，不做分配、不建容器。
- 比较按"掩码逐字节与"实现 CIDR，IPv4 只看前 4 字节。
- 端口区间闭区间比较。
- 规则按数组顺序扫描，**首个命中即返回**（顺序即优先级）。
- 未命中时 `match_result.matched == false`，`action` 填 `view.fallback_action`、`rule_index` 填 `0xFFFFFFFF`；**调用方一律以 `matched` 为准**，不要把 `action` 当成命中的证据。
- 校验阶段把所有偏移一次性验证完，保证后续扫描不必再判界。

## 测试

`tests/host/rule_blob_test.cpp`、`tests/host/matcher_test.cpp` 分开。blob 用测试内构造的小工具函数生成，不引入外部文件。

## 交给 B4（C# 侧 blob 编译器）的约定

C# 侧要产出的字节必须与 `rule_blob.h` 逐字节一致。落地细则：

- 头部用 `[StructLayout(LayoutKind.Sequential, Pack = 4)]` 定义，`Marshal.SizeOf` 必须等于 40，并对每个字段的 `Marshal.OffsetOf` 做断言；版本变更即破坏性变更。
- 段的先后顺序由编译器自行决定，只受不变量约束。推荐 `header | rules | 进程串池 | 网段池`。
- `process_pool_size` 写**精确串字节数**，不含为对齐补的填充；`network_pool_offset` 由编译器向上对齐到 4。
- 进程串全部小写、NUL 结尾、池内去重；`process_index` 必须指向串首，不能指向串中。
- 网段记录定长 20 字节；`network_index` 必须是 20 的倍数。
- 兜底动作写在头部的 `fallback_action` 里，不另设参数——规则表与兜底动作必须作为整体原子替换。
- 规则顺序即优先级，C# 侧要保持配置里的书写顺序。
