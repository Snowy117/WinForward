# Host 规则数组与进程选择器 parity —— 设计

## 1. 目标与边界

把 Host 规则的形状与匹配语义对齐 WinForward，同时保持“一条配置规则 = 一条 blob 规则”和
“classify 首个命中”的既有结构：

- 规则选择器改为集合：进程、远端网段、远端端口、协议、地址族；同字段 OR、字段间 AND。
- 进程选择器三种语义：文件名、精确路径、目录前缀。
- blob v1 → v2；用户态与驱动同批升级。
- block 规则只允许精确文件路径；文件名 / 目录前缀写在 block 规则里在编译期拒绝。
  auth filter、顺序冲突拒绝、D4 的 RST / 静默丢包语义都不变（sink 方案另开任务）。
- 不动 Forwarded blob、Target 映射、adapter 选择器、域名 / LocalTargets。

## 2. 分层与边界

```
配置（临时 JSON） ──► PolicyRule（集合模型）
                        │
                        ▼
              RuleBlobCompiler（归一化 + 校验 + v2 编码）
                        │  blob v2
                        ▼
  驱动 try_open_blob（纯布局校验） ──► blob_view
                        │
        ┌───────────────┼────────────────┐
        ▼               ▼                ▼
  matcher（classify）  rule_order_valid  block_filters（auth 层）
```

- 语义判断仍然只在编译期和 classify；驱动只解码、匹配与装 filter。
- `src/common/rule_blob.h` 是唯一 ABI 权威；C# 侧镜像 + 布局断言；`tools/wfprobe` 是独立夹具。
- `src/common` 继续遵守内核方言（无 STL 容器、无异常、无堆分配）。

## 3. blob v2 布局

全部多字节字段主机字节序（Windows x64 小端）；段起点 4 字节对齐；版本号 2 即破坏性变更。

### 3.1 头部（56 字节）

| 偏移 | 类型 | 字段 |
|---:|---|---|
| 0 | u32 | magic `0x31425257` |
| 4 | u16 | version = 2 |
| 6 | u16 | header_size = 56 |
| 8 | u32 | total_size |
| 12 | u32 | rule_count |
| 16 | u32 | fallback_action |
| 20 | u32 | rules_offset |
| 24 | u32 | process_pool_offset |
| 28 | u32 | process_pool_size |
| 32 | u32 | selector_pool_offset |
| 36 | u32 | selector_pool_size |
| 40 | u32 | network_pool_offset |
| 44 | u32 | network_pool_size |
| 48 | u32 | port_pool_offset |
| 52 | u32 | port_pool_size |

段序：`header | rules | process_pool | selector_pool | network_pool | port_pool`。

### 3.2 规则（32 字节）

| 偏移 | 类型 | 字段 |
|---:|---|---|
| 0 | u32 | process_first（selector 数组下标） |
| 4 | u32 | process_count（0 = 任意） |
| 8 | u32 | network_first（网段记录下标） |
| 12 | u32 | network_count（0 = 任意） |
| 16 | u32 | port_first（端口记录下标） |
| 20 | u32 | port_count（0 = 任意） |
| 24 | u8 | protocol_mask（bit0 TCP / bit1 UDP；0 = 任意） |
| 25 | u8 | family_mask（bit0 IPv4 / bit1 IPv6；0 = 任意，3 = 两者） |
| 26 | u8 | action |
| 27 | u8 | reserved = 0 |
| 28 | u32 | reserved2 = 0 |

`count == 0` 时对应 `first` 必须为 0（规范化编码，便于负向夹具只违反一条规则）。

### 3.3 进程选择器（8 字节）

| 偏移 | 类型 | 字段 |
|---:|---|---|
| 0 | u32 | string_offset（进程串池字节偏移） |
| 4 | u8 | kind：0 = 文件名，1 = 路径 |
| 5 | u8 | is_directory：0 = 否，1 = 是（仅 kind=1 有意义） |
| 6 | u8 | reserved[2] = 0 |

- 文件名型：串内不得含 `/` 或 `\`；匹配连接路径的最后一段。
- 路径型：串必须是 `\` 开头的设备路径、不得以 `\` 结尾（卷根 `\device\harddiskvolumeN` 合法）；
  匹配“完整路径相等，或路径以 `选择器 + \` 开头”。
- 两种形态都按 ASCII 大小写不敏感比较；串本身在编译期已折叠为小写。
- `is_directory` 只影响 block 表达边界与顺序校验的精确度，不影响匹配语义。

### 3.4 网段与端口

- 网段沿用 v1 的 20 字节记录（`prefix_length` / `family` / `reserved[2]` / 16 字节地址），
  「任意」用 `network_count = 0` 表示，不再使用索引哨兵。
- 端口记录 4 字节：`u16 start; u16 end;`（闭区间，start ≤ end）；`port_count = 0` 表示任意。
  编译期排序并合并相邻 / 重叠区间，因此一条记录就是一个 filter。

### 3.5 解码与校验（`try_open_blob`）

在 v1 的段边界 / 重叠 / 对齐校验之上，v2 增加：

1. 版本 = 2、header_size = 56；
2. 四个池的偏移 + 长度在 `total_size` 内、互不重叠、4 字节对齐；
3. `selector_pool_size % 8 == 0`、`network_pool_size % 20 == 0`、`port_pool_size % 4 == 0`；
4. 每条规则的 `first + count` 不越界；`count == 0` 时 `first == 0`；
5. 每个 selector：kind / is_directory / reserved 合法；串偏移指向池内串首且 NUL 终止；
   串是合法 UTF-8、1..511 字节、无控制字符；文件名型不含分隔符；路径型以 `\` 开头且不以 `\` 结尾；
6. 每个端口记录 start ≤ end；
7. 每个网段记录沿用 v1 的逐条校验；
8. 协议掩码 ≤ 3、地址族掩码 ≤ 3；
9. `family_mask` 只含一个族时，规则引用的每个网段必须是该族。

UTF-8 / 长度校验从驱动的 `rule_table.cpp` 移进 `rule_blob.h`，让宿主 gtest 与驱动共用同一份
实现；驱动侧只保留「block 表达边界」和「filter 预算」这类策略检查。

## 4. 用户态模型与编译

### 4.1 规则模型

```csharp
public sealed class PolicyRule
{
    public string? Name { get; init; }
    public IReadOnlyList<ProcessSelector>? Processes { get; init; }        // null = 任意
    public IReadOnlyList<RemoteNetwork>? RemoteNetworks { get; init; }     // null = 任意
    public IReadOnlyList<PortRange>? RemotePorts { get; init; }            // null = 任意
    public IReadOnlyList<RuleProtocol>? Protocols { get; init; }           // null = 任意
    public IReadOnlyList<RuleAddressFamily>? Families { get; init; }       // null = 任意
    public RuleAction Action { get; init; } = RuleAction.Permit;
}
```

- 显式空列表在编译期拒绝（对齐 WinForward 的 “Array must not be empty”）；`null` 表示不限制。
- `ProcessSelector` 改为 `Kind`（FileName / Path）+ `Value`；`Parse` 负责 trim、控制字符 /
  UTF-8 校验、`/`→`\` 归一；路径长度上限仍在归一化之后检查。

### 4.2 路径归一化

`IProcessPathNormalizer` 改为返回 `ProcessPathResolution? { DevicePath, IsDirectory }`：

- 文件名型不经归一化，只做 ASCII 小写折叠；
- 路径型走新的 Windows 实现：`Path.GetFullPath` → `\\?\` 前缀（长路径）→ `CreateFileW`
  （`FILE_FLAG_BACKUP_SEMANTICS`）→ `GetFinalPathNameByHandleW(VOLUME_NAME_NT)` →
  `GetFileInformationByHandle` 判定目录 → ASCII 小写折叠；
- 解析失败（路径不存在、无权限、非绝对路径等）→ 编译错误，带规则序号与选择器原文；
- 非 Windows 测试注入解析器；宿主侧不触碰 Win32 API；
- 不再使用 `FwpmGetAppIdFromFileName0`（目录不支持，且长路径失败）；句柄法对同一文件产出的
  设备路径与它一致（VM 实测）。

### 4.3 编译期校验

在现有校验之上新增：

- 集合非空、元素合法；端口排序 / 合并；网段主机位清零并去重；协议 / 族去重；
- 路径型选择器解析成功；文件名型不含分隔符；
- 族集合与每个网段的族一致（族集合只含一个族时）；
- block 规则的每个进程选择器必须是路径型且 `IsDirectory == false`，否则：
  `规则 #i：block 规则不支持文件名 / 目录前缀进程选择器「…」；WFP auth filter 只能精确匹配
  ALE_APP_ID。请改用精确文件路径，或把该规则改成 permit/redirect。`
- block filter 预算：按 §6 的展开公式计算，超过 `PolicyLimits.MaxBlockFilters = 128` 时拒绝；
- blob 总大小 ≤ `PolicyLimits.RuleBlobMaxSize = 64 KiB`。

`CompileUnchecked` 只跳过「顺序冲突」与「block 表达边界 / 预算」，用于构造驱动必须独立拒绝的
负向夹具；布局必须仍然合法。

### 4.4 池与索引

- 进程串池：按归一化后的串去重，`string_offset` 是字节偏移（沿用 v1 语义）；
- selector 池：按规则顺序追加，同一规则内可去重；规则持有下标 + 数量；
- 网段池：按规则顺序追加 20 字节记录，不做跨规则去重（简化连续区间，20 字节的重复成本可接受）；
- 端口池：按规则追加已排序合并的 4 字节记录。

## 5. 匹配语义

```
rule_matches =
    family_mask 匹配连接族
 && protocol_mask 匹配连接协议
 && (port_count == 0 || 任一端口区间包含远端端口)
 && (network_count == 0 || 任一网段包含远端地址)
 && (process_count == 0 || 任一进程选择器匹配进程路径)
```

进程选择器：

- 文件名型：取连接路径最后一个 `\` 之后的部分，与选择器做 ASCII 大小写不敏感相等比较；
- 路径型：完整路径相等，或完整路径以 `选择器 + \` 开头；
- 连接路径缺失 / 长度为 0 时，两种形态都不匹配（与 v1 精确匹配一致）；
- 数组内 OR，数组之间 AND；规则顺序与 `rule_index` 不变。

## 6. block filter 展开

只有 block 规则需要 auth filter；展开公式与驱动 `probe_install_block_filters` 完全一致：

```
filters(rule) = Σ_layer ( protocol_count × process_count' × port_count' × network_count' )
  layer ∈ {IPv4, IPv6}，受 family_mask 与网段族约束
  protocol_count = mask 为 0 或 3 ? 2 : 1
  process_count' = max(1, process_count)   // 已保证全是精确文件路径
  port_count'    = max(1, port_count)
  network_count' = network_count == 0 ? 1 : 该层的网段数（为 0 时跳过该层）
```

- 规则内多个网段 / 端口需要展开成多条 filter：WFP 同一字段的多个条件是 AND，不能表达 OR；
- 总预算 128；编译期先算，驱动在安装时再按同一公式兜底（超限返回 `STATUS_INVALID_PARAMETER`，
  旧表与旧 filter 不变）；
- fallback = block 仍为 2 层 × 2 协议 = 4 条协议过滤；与规则冲突的检查不变。

## 7. 顺序校验（C# 与驱动同算法）

对每条 block 规则，检查是否存在更早的非 block 规则可能命中同一条连接：

| 字段 | 重叠判定 |
|---|---|
| 协议 | `maskA == 0 || maskB == 0 || (maskA & maskB) != 0` |
| 地址族 | 有效族集 = `mask == 0 ? 3 : mask`，再与“网段族集合”取交；两侧有效集相交 |
| 端口 | 任一对端口区间相交 |
| 网段 | 任一对 CIDR 前缀相交（较短前缀比较） |
| 进程 | 任一对选择器重叠（见下） |

进程选择器对：

- 任一侧 `count == 0` → 重叠；
- 文件名 vs 文件名：ASCII 相等；
- 文件名 vs 路径：路径是目录 → 重叠（可能包含该文件名）；路径是文件 → 文件名与该路径最后一段相等才重叠；
- 路径 vs 路径：相等，或一方以 `另一方 + \` 开头（与 matcher 的前缀语义一致，保守）。

两侧实现必须逐条对应；差分夹具之外，再用一组专门的 C# / C++ 表驱动用例锁住同一矩阵。

## 8. 配置层

临时 JSON 配置保持现有键名，每个选择器同时接受单值与数组（单值 = 单元素数组）：

```json
{
  "rules": [
    {
      "name": "browser",
      "action": "redirect",
      "process": ["browser.exe", "C:\\Tools\\curl.exe", "D:\\Portable\\App\\"],
      "protocol": ["tcp", "udp"],
      "family": ["ipv4", "ipv6"],
      "remoteNetwork": ["10.0.0.0/8", "192.168.0.0/16"],
      "remotePorts": ["80", "443", "8000-8100"]
    }
  ]
}
```

- 空数组、空元素、非字符串元素、未知值 → 带 JSON 路径报错；
- `remotePorts` 的 `*` 只允许单独出现，含义为任意；端口 0 拒绝（对齐 WinForward）；
- `protocol` / `family` 出现 `any` 视为不限制；与具体值混写时 `any` 优先（文档写明）；
- 数组顺序保留；端口排序合并；网段 / 协议 / 族去重。

## 9. 兼容性与迁移

- blob v2 与 v1 不兼容；控制器、驱动、`wfprobe` 必须同批升级，旧 v1 blob 整块拒绝。
- 配置层接受单值，现有脚本与手写配置不需要改。
- 控制面 IOCTL ABI 不变（仍是 v7）；`probe_rule_info` 的规则数语义不变。
- block 的 D4 语义与顺序拒绝边界不变；本任务不引入 sink / callout / 包级路径。

## 10. 验证

- 宿主：`tools/check.sh --full`（格式、有效行、cppcheck、禁止模式、clang-tidy、`wf_check`）；
  `wf_check` 会先跑 C# 测试并生成差分夹具，再跑 C++ gtest 与差分测试。
- C#：数组 / 标量配置、端口排序合并、选择器解析、路径归一化（注入）、编译产物布局、
  一条规则 = 一条 blob 规则、block 预算与拒绝、顺序校验矩阵、511 / 64 KiB 边界。
- C++：`try_open_blob` 负向矩阵（每个新检查一条只违反它的夹具）、matcher 三种进程语义与
  集合 OR/AND、`rule_order_valid` 矩阵、差分消费。
- 驱动（WinLTSC VM）：`wfprobe rules` / `udp-rules` / `gates` / `selftest`；新增数组、文件名、
  目录前缀、`tools` vs `tools2`、block 精确路径数组、block + 文件名负向用例；`gate.ps1`。
- 变异验证：`tools/userland/verify-gates.sh` 增补新检查；驱动侧关键检查用 `wfprobe` 负向用例。

## 11. 回滚

- 整批回滚：blob v2、C# 编译器、驱动、`wfprobe`、夹具与文档一起回退；不存在新旧混跑窗口。
- 配置层向后兼容，回滚后旧配置仍可用。
- 驱动侧新增的策略检查失败时保持旧表 / 旧 filter 不变，与现有换表路径一致。

## 12. 风险与已记录的边界

- 路径型选择器要求编译期路径存在（文件或目录）；不存在的目录会在编译期失败，而不是静默不命中。
  这与现有 `FwpmGetAppIdFromFileName0` 对文件的要求一致，且更可诊断。
- 路径型选择器在 matcher 中始终按“相等或目录前缀”匹配；即使编译期解析为文件，也保留前缀语义
  以对齐 WinForward。顺序校验因此对路径选择器保守（可能拒绝理论上不可能同时命中的组合）。
- block 无法表达文件名 / 目录前缀；这是 WFP auth filter 的结构限制，已在研究文档中记录。
- 网段池不做跨规则去重：blob 会比“完全去重”略大，但规则区间保持连续，实现和校验更简单。
- 驱动与用户态同批升级；不做 v1 兼容读取。
