# Host 规则 blob（v2）

> 适用范围：`src/common/rule_blob.h`、`src/common/matcher.h`、`src/userland/WinForward.Userland/Policy/`、
> `tools/wfprobe/`。
> 拟定：2026-10-24。对应任务：`.trellis/tasks/10-24-host-rule-arrays-process-match`。

这份文档是 Host 规则层的跨语言契约。用户态把有序规则表编译成字节流，内核只解码与匹配；
两侧共享的是字节布局与语义，不是标识符。任何布局改动都必须提升 `rule_blob_version`，并同时
更新 C++ 的 `static_assert`、C# 的布局测试、`tools/wfprobe` 的夹具与本文档。

## 1. 定位与边界

Host 规则回答「哪个进程、访问哪个远端（网段 / 端口 / 协议 / 族）时做什么」。它与 Forwarded
规则是两份独立 ABI：

| 维度 | host `wf_rule_blob` | forwarded `wf_forwarded_blob` |
|---|---|---|
| magic | `"WRB1"`（`0x31425257`，v2 沿用） | `"WFB1"`（`0x31424657`） |
| 版本 | 2 | 1 |
| 匹配维度 | 进程、目的 CIDR、端口、协议、族 | 入口适配器、客户端 CIDR、目的 CIDR、端口、协议、族 |
| 动作载荷 | 无 | `upstream_id` |
| 任意值表达 | 对应 `*_count == 0` | 网段索引 `0xFFFFFFFF` 等哨兵 |

两者不共用 magic / version / header / rule 结构，任一解码器都不会接受对方的 blob。共享的定义
只有 `rule_blob.h` 里的 `network_prefix`、`rule_action`、原始协议 / 族常量与
`rule_pool_index_any`。**不要复制这些常量。**

Host 层的两条铁律：

- **一条配置规则对应一条 blob 规则。** 选择器数组在 blob 里是区间，不在编译期展开成多条规则；
  事件与计数里的 `rule_index` 因此就是配置顺序。
- **同字段内 OR、字段之间 AND、规则数组顺序即优先级、首个命中生效。** 内核不解释用户意图，
  只做规范化后的比较。

本层不包含：JSON 配置解析（`Configuration/`）、`ALE_AUTH_CONNECT` block filter 的运行时安装
（`block_filters.cpp`）、classify 计数与事件、重定向。控制面 IOCTL ABI 仍是 **v7**，与本数据面
ABI 各自版本化。

## 2. ABI

所有多字节字段为主机字节序（部署目标只有 Windows x64 小端）；所有段起点 4 字节对齐；
`total_size` 必须等于调用方缓冲区的实际长度。C++ 侧对每个结构做尺寸、对齐与逐字段
`offsetof` 的 `static_assert`；C# 镜像结构用 `Pack = 4`，由 `LayoutTests` 锁尺寸与偏移，
`BlobAbiConsistencyTests` 再从 `rule_blob.h` 源码文本读常量，与 `RuleBlobLayout` /
`PolicyLimits` / `tools/wfprobe/Protocol.cs` 三处互锁。

### 2.1 header（56 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | `magic` | `0x31425257`（内存字节 `W R B 1`，v2 不改） |
| 4 | u16 | `version` | 2 |
| 6 | u16 | `header_size` | 56，必须精确等于结构体尺寸 |
| 8 | u32 | `total_size` | 必须等于缓冲区长度 |
| 12 | u32 | `rule_count` | |
| 16 | u32 | `fallback_action` | 0 permit / 1 block / 2 redirect |
| 20 | u32 | `rules_offset` | 4 字节对齐，≥ `header_size` |
| 24 | u32 | `process_pool_offset` | 同上 |
| 28 | u32 | `process_pool_size` | 字节数，不含段间填充 |
| 32 | u32 | `selector_pool_offset` | 同上 |
| 36 | u32 | `selector_pool_size` | 必须是 8 的倍数 |
| 40 | u32 | `network_pool_offset` | 同上 |
| 44 | u32 | `network_pool_size` | 必须是 20 的倍数 |
| 48 | u32 | `port_pool_offset` | 同上 |
| 52 | u32 | `port_pool_size` | 必须是 4 的倍数 |

v2 不支持扩展头：`header_size` 必须精确等于 56，否则 `rules_offset` 的语义会漂移。

### 2.2 rule（32 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | `process_first` | selector 池的**记录下标** |
| 4 | u32 | `process_count` | 0 = 任意，此时 `process_first` 必须为 0 |
| 8 | u32 | `network_first` | 网段池的**记录下标** |
| 12 | u32 | `network_count` | 0 = 任意，此时 `network_first` 必须为 0 |
| 16 | u32 | `port_first` | 端口池的**记录下标** |
| 20 | u32 | `port_count` | 0 = 任意，此时 `port_first` 必须为 0 |
| 24 | u8 | `protocol_mask` | bit0 TCP、bit1 UDP；0 = 任意，只允许 ≤ 3 |
| 25 | u8 | `family_mask` | bit0 IPv4、bit1 IPv6；0 = 任意，只允许 ≤ 3 |
| 26 | u8 | `action` | permit(0) / block(1) / redirect(2) |
| 27 | u8 | `reserved` | 必须为 0 |
| 28 | u32 | `reserved2` | 必须为 0 |

三个 `*_first` 都是**记录下标**，不是字节偏移；只有 `process_selector.string_offset` 是进程串池
内的字节偏移。`count == 0` 时 `first` 必须为 0 是规范化编码：它让「任意」只有一种写法，负向
夹具可以只违反一条规则。

### 2.3 process_selector（8 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | `string_offset` | 进程串池内的字节偏移，指向串首 |
| 4 | u8 | `kind` | 0 = 文件名，1 = 路径 |
| 5 | u8 | `is_directory` | 0 / 1；只对 `kind = path` 有意义 |
| 6 | u8[2] | `reserved` | 必须为 0 |

- `kind = file_name`：串内不得含 `/` 或 `\`；匹配连接路径的最后一段。
- `kind = path`：串必须是 `\device\` 开头（ASCII 大小写不敏感）、不以 `\` 结尾的设备路径；
  卷根 `\device\harddiskvolumeN` 合法。匹配「完整路径相等，或路径以 `选择器 + \` 开头」。
- 两种形态都只折叠 ASCII 大小写；串本身在编译期已折叠为小写，非 ASCII 字节原样比较。
- `kind = file_name` 时 `is_directory` 必须为 0；`kind = path` 时 `is_directory` 只影响 block
  表达边界与顺序校验的精确度，**不影响** matcher 的前缀语义。

### 2.4 network_prefix（20 字节，1 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u8 | `prefix_length` | IPv4 ≤ 32，IPv6 ≤ 128 |
| 1 | u8 | `family` | 只允许 4 / 6 |
| 2 | u8[2] | `reserved` | 必须为 0 |
| 4 | u8[16] | `network` | IPv4 只用前 4 字节，其余补零 |

「任意网段」用规则的 `network_count = 0` 表达，不再使用 v1 的索引哨兵。网段记录在编译期做
主机位清零；网段池按规则顺序追加，**不做跨规则去重**（池是连续区间，简化校验与实现）。

### 2.5 port_range（4 字节，2 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u16 | `start` | 闭区间起点 |
| 2 | u16 | `end` | 闭区间终点；`start <= end` |

「任意端口」用 `port_count = 0` 表达。编译期会排序并合并相邻 / 重叠区间，因此一条记录就是
一组连续的端口；配置层写 `"8000-8100"` 与数组写 `["8000", "8001-8100"]` 编出来的池是同一份。

### 2.6 段序

```text
[0, 56)                      header
[rules_offset, ...)          rule_count × 32
[process_pool_offset, ...)   process_pool_size 字节的 NUL 结尾串
[selector_pool_offset, ...)  selector_pool_size（8 的倍数）
[network_pool_offset, ...)   network_pool_size（20 的倍数）
[port_pool_offset, ...)      port_pool_size（4 的倍数）
```

段之间允许 4 字节对齐填充，两两不得重叠。用户态编译器的空表输出从 56 开始依次排布所有段
（长度都为 0）；解码端只按头部给出的偏移与长度判界，不做任何推断。头部 `rule_count` 决定
规则数组长度，规则的三个 `*_first / *_count` 必须落在各自池内。

## 3. 进程串池

- 串以 NUL 结尾，长度按 UTF-8 字节数计算，**1..511 字节（不含 NUL）**；缓冲区按 512 字节
  （含 NUL）理解。
- 严格 UTF-8：拒绝超长编码、代理项（U+D800–U+DFFF）、大于 U+10FFFF 的码点，以及控制字符
  **C0（U+0000–U+001F）、DEL（U+007F）、C1（U+0080–U+009F）**。
- 进程串池末尾必须有一个 NUL，保证 selector 扫描必然在池内终止；`selector.string_offset`
  必须指向串首（偏移 0 或前一个字节是 NUL）。
- 用户态按归一化后的串去重（`string_offset` 复用）；同一规则里的重复 selector 也会去重。
- 连接侧：classify 把 `ALE_APP_ID` 的 UTF-16LE 设备路径手工转成 UTF-8（纯算术、无分配，
  `DISPATCH_LEVEL` 可用），超限或孤立代理项时把长度置 0——整条不匹配，**绝不截断**。

## 4. try_open_blob 校验清单

`try_open_blob(data, size, out)` 是纯函数、无分配、无异常、无内核依赖；失败返回 `false`，
**`out` 保持原值**；成功才写入指向 `data` 的视图。校验顺序与代码一致：

1. `data != nullptr`，且地址按 `alignof(rule_blob_header)`（4）对齐；
2. `size >= 56`（防止把短缓冲区当头部读；ASan / 守卫页负责越界证明）；
3. `magic == 0x31425257`、`version == 2`；
4. `header_size == 56`；
5. `total_size == size`；
6. `fallback_action` 是已知动作（0 / 1 / 2）；
7. 五个段（rules / process / selector / network / port）的起点按 4 字节对齐、不小于 56、
   不大于 `total_size`；段长不越过 `total_size`；
8. 五个段两两不重叠；
9. `selector_pool_size % 8 == 0`、`network_pool_size % 20 == 0`、`port_pool_size % 4 == 0`；
10. 进程串池末尾（长度为 0 时跳过）是 NUL；
11. 网段池逐条校验（含未被规则引用的条目）：`reserved` 为 0、族只允许 4 / 6、前缀长度不超过
    32 / 128；
12. selector 池逐条校验：
    - `kind` 只能是 0 / 1，`is_directory <= 1`，`reserved[2]` 为 0；
    - `kind = file_name` 时 `is_directory` 必须为 0；
    - `string_offset < process_pool_size`，且偏移 0 或前一个字节为 NUL；
    - 串在池内以 NUL 终止、长度 1..511，并通过严格 UTF-8 与控制字符检查；
    - 文件名型不含 `/` 与 `\`；路径型以 `\device\` 开头、不以 `\` 结尾；
13. 端口池逐条校验：`start <= end`；
14. 每条规则：`reserved` / `reserved2` 为 0；`action` 已知；`protocol_mask` / `family_mask`
    ≤ 3；三个「区间」合法（`count == 0` 时 `first == 0`，否则 `first + count` 不越界）；
15. 规则的 `family_mask` 只含一个族时，它引用的每个网段都必须是该族（否则规则永远不可能命中）。

解码端**不做**规则数 / 池条目数的容量上限检查；上限属于编译期（`rule_blob_max_size = 64 KiB`、
`max_block_filters = 128`），内核只按缓冲区长度判界，避免把「生产者限制」和「消费端内存安全」
混为一谈。

## 5. matcher 语义

`match(view, connection)` 是纯函数，假定 `view` 来自成功的 `try_open_blob`，不再判界。连接事实：

```text
process_path（UTF-8，已 ASCII 折叠）, process_path_length,
remote_address[16], remote_port, protocol, family
```

单条规则的匹配式：

```text
rule_matches =
    family_mask 匹配连接族
 && protocol_mask 匹配连接协议
 && (port_count == 0     || 任一端口区间包含远端端口)
 && (network_count == 0  || 任一网段包含远端地址)
 && (process_count == 0  || 任一进程选择器匹配连接路径)
```

进程选择器：

- 文件名型：取连接路径最后一个 `\` 之后的部分，与选择器做 ASCII 大小写不敏感相等比较；
  `dns` 不命中 `dns.exe`，也不做扩展名补全。
- 路径型：完整路径相等，或连接路径以 `选择器 + \` 开头；前缀边界必须正好是 `\`，因此
  `\device\...\tools` 不命中 `\device\...\tools2\x.exe`。选择器在解码期已保证不以 `\` 结尾。
- 连接路径缺失或长度为 0 时，两种形态都不命中。
- `is_directory` 不参与匹配：路径型选择器即使解析结果是文件，也保留前缀语义，以对齐
  WinForward 的目录写法。

协议 / 族：

- `protocol_mask == 0` 或对应位为 1 才算命中；连接协议不是 TCP(6) / UDP(17) 时 `protocol_bit`
  为 0，任何规则都不命中（classify 另有 `permitted_non_tcp` 计数）。
- `family_mask` 同理；`network_matches` 还会要求网段记录的族等于连接族。

数组与顺序：同一字段的区间内 OR、字段之间 AND；规则数组顺序即优先级，**首个命中即返回**：

| 结果字段 | 命中 | 未命中 |
|---|---|---|
| `matched` | `true` | `false` |
| `action` | 规则动作 | `view.fallback_action` |
| `rule_index` | 规则下标 | `rule_pool_index_any` |

调用方一律以 `matched` 为准，不要把未命中时的 `action` 当成命中证据。

## 6. 用户态模型与编译

`PolicyRule` 的五个选择器都是集合，`null` 表示不限制：

```csharp
public IReadOnlyList<ProcessSelector>? Processes { get; init; }
public IReadOnlyList<RemoteNetwork>? RemoteNetworks { get; init; }
public IReadOnlyList<PortRange>? RemotePorts { get; init; }
public IReadOnlyList<RuleProtocol>? Protocols { get; init; }
public IReadOnlyList<RuleAddressFamily>? Families { get; init; }
```

显式空集合在编译期拒绝（对齐 WinForward 的 “Array must not be empty”）。

### 6.1 进程选择器的解析与归一化

`ProcessSelector.Parse`：

1. `Trim`；`ArgumentException.ThrowIfNullOrWhiteSpace`；
2. 控制字符（C0 / DEL / C1）与孤立代理项拒绝（严格 UTF-8 校验）；
3. `/` 归一成 `\`；
4. 含 `\` 为路径型，否则为文件名型；文件名型再检查不含 `/`、`\`。

路径型走 `IProcessPathNormalizer`；生产实现 `WindowsProcessPathNormalizer.ForWindows()` 调用
`WindowsDevicePath.Resolve`：

1. `Path.GetFullPath`（相对路径先按当前目录展开）；
2. 加 `\\?\` 前缀（UNC 用 `\\?\UNC\`）以支持长路径；
3. `CreateFileW(..., FILE_FLAG_BACKUP_SEMANTICS | OPEN_EXISTING)` 打开文件或目录；
4. `GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_NT)` 取 NT 设备路径；
5. `GetFileInformationByHandle` 判 `FILE_ATTRIBUTE_DIRECTORY`；
6. `TrimEnd('\\')` 后返回 `ProcessPathResolution { DevicePath, IsDirectory }`（卷根本身没有
   尾斜杠，不会被削掉）。

任一步失败返回 `null`，编译器带规则序号与选择器原文拒绝。**路径必须存在**（文件或目录）；
产品路径解析不再使用 `FwpmGetAppIdFromFileName0`：它只接受文件，对目录返回 `ERROR_ACCESS_DENIED` /
`ERROR_PATH_NOT_FOUND`，长路径也不可靠。文件名型不经过解析器，只做 ASCII 小写折叠。`tools/wfprobe`
的 `AppId` 夹具对已存在的文件仍用该 API 取参照值，目录路径则走句柄法。

### 6.2 编译期检查

在现有校验之上：

- 集合非空、元素合法；端口排序 / 合并；网段主机位清零并在规则内去重；协议 / 族去重；
- 端口 0 拒绝（`PortRange.Single(0)` 与以 0 开头的具体区间）；`PortRange.Any`（0..65535）是
  程序化 API 的整段「任意」，配置层的 `*` 则直接编译成 `port_count = 0`；
- 路径型选择器解析成功；文件名型不含分隔符；
- 路径型归一化结果必须以 `\device\` 开头、不以 `\` 结尾；
- 规则只声明一个族时，每个网段的族必须与之一致；
- block 规则的每个进程选择器必须是路径型且 `IsDirectory == false`（见 §8）；
- block filter 预算（见 §8）；
- blob 总大小 ≤ `PolicyLimits.RuleBlobMaxSize = 64 KiB`（空池时约 2046 条规则：56 + 32 × N）；
- 进程串 UTF-8 ≤ 511 字节（不含 NUL）；ASCII 折叠后检查，非 ASCII 按原字节计数。

`CompileUnchecked` 只跳过「顺序冲突」「block 表达边界」「filter 预算」，用于构造驱动必须独立
拒绝的负向夹具；布局必须仍然合法。

### 6.3 池与索引

- 进程串池：按归一化后的串去重，`string_offset` 是字节偏移；
- selector 池：按规则顺序追加，同一规则内按 `(kind, text)` 去重；
- 网段池：按规则顺序追加 20 字节记录，不做跨规则去重；
- 端口池：按规则顺序追加已排序合并的 4 字节记录。

一条配置规则只写一条 32 字节 rule，三个 `*_first` 指向各自池的起始记录下标。

## 7. 顺序校验（C# 与驱动同算法）

`RuleOrderValidator.Validate`（用户态）与 `rule_order_valid`（`rule_table.cpp`）逐条对应：对每条
block 规则，检查是否存在更早的非 block 规则可能命中同一条连接。block 被编译成独立的
`ALE_AUTH_CONNECT` filter，只看自身条件、不参与「首个命中」顺序；若更早的 permit/redirect
本应先命中，后面的 filter 仍会拦掉这条连接，语义与配置作者写的相反。

| 字段 | 重叠判定 |
|---|---|
| 协议 | `maskA == 0 || maskB == 0 || (maskA & maskB) != 0` |
| 地址族 | 有效族集 = `(mask == 0 ? 3 : mask) & 网段族集合`（无网段时就是前者）；两侧有效集相交 |
| 端口 | 任一对端口区间相交；任一侧 `count == 0` 视为重叠 |
| 网段 | 任一对同族 CIDR 前缀相交（较短前缀比较）；任一侧 `count == 0` 视为重叠 |
| 进程 | 任一对选择器重叠（见下）；任一侧 `count == 0` 视为重叠 |

进程选择器对：

- 文件名 vs 文件名：ASCII 相等；
- 文件名 vs 路径：路径是目录 → 重叠（目录可能包含该文件名）；路径是文件 → 文件名与该路径
  最后一段相等才重叠；
- 路径 vs 路径：相等，或一方以「另一方 + `\`」开头（与 matcher 的前缀语义一致，保守）。

`fallback = block` 且表里存在任何非 block 规则时，两侧同样拒绝：驱动会为 TCP(6) / UDP(17)
各生成协议过滤器（IPv4 / IPv6 每层一对），那些规则命中的连接全部变成死规则，而 classify
仍会记 `permitted`。错误消息必须带两条规则序号与条件摘要（数组用逗号列出）。

## 8. block 表达边界与 filter 预算

**表达边界。** block 规则由 `ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK` filter 执行；
`FWPM_CONDITION_ALE_APP_ID` 只支持 `FWP_MATCH_EQUAL`，WFP 没有可用的 app-id 前缀匹配
（`FWP_MATCH_PREFIX` 实际是后缀匹配），目录路径也构造不出 app-id。因此：

- block 规则的进程选择器只允许**路径型且 `is_directory = 0`**（精确文件路径，可写数组）；
- 文件名 / 目录前缀出现在 block 规则里，编译期带规则序号拒绝，错误消息说明
  「WFP auth filter 只能精确匹配 ALE_APP_ID」；
- 驱动 `allocate_rule_table` 的 `block_expression_valid` 对同形 blob 整块拒绝并 `DbgPrint`
  规则序号；不做「classify 记 block、auth 层拦不住」的假 block。

**展开公式**（编译期 `RuleBlobCompiler`、驱动 `block_filter_budget_valid` 与
`probe_install_block_filters` 同一份）：

```text
filters(rule) = Σ_layer ( protocol_count × process_count' × port_count' × network_count' )
  layer ∈ {IPv4, IPv6}；family_mask 不含该层、或显式网段列表里没有该族时跳过该层
  protocol_count = mask 为 0 或 3 ? 2 : 1
  process_count' = max(1, process_count)   // 已保证全是精确文件路径
  port_count'    = max(1, port_count)
  network_count' = network_count == 0 ? 1 : 该层的网段数
```

- WFP 同一字段的多个条件是 AND，表达不了数组的 OR，所以规则内多个进程 / 端口 / 网段必须
  展开成多条 filter；
- `fallback = block` 额外占 2 层 × 2 协议 = 4 条只带协议条件的 filter；
- 总数上限 `max_block_filters = 128`（`probe_ioctl.h`，`PolicyLimits.MaxBlockFilters` 与
  `tools/wfprobe/Protocol.cs` 镜像）；编译期先算，驱动在换表时再按同一公式兜底；
- 超限时驱动删除已建 filter 并返回 `STATUS_INVALID_PARAMETER`，旧表与旧 filter 保持不变；
- 每条 filter 都显式写 `FWPM_CONDITION_IP_PROTOCOL` 的 6 / 17：不写协议条件的 filter 会误拦
  首个出站 ICMP（以及按 UDP 处理的其他 IP 协议）；
- 进程串写入 filter 前用 `RtlUTF8ToUnicodeN` 转成 UTF-16LE + NUL；非法 UTF-8 或超长时拒绝
  整块 blob，绝不退化成「无条件 block」；
- 网段 `/0` 省略地址条件（该层已是这一族），端口用 `FWP_MATCH_RANGE`。

顺序歧义拒绝与 D4 的 block 语义（TCP RST、UDP 静默丢包）都不因数组化而改变。

## 9. 三侧对应与验证

| 侧 | 文件 | 职责 |
|---|---|---|
| C++ 权威 | `src/common/rule_blob.h` | 布局常量、`static_assert`、`try_open_blob` 全部字节级校验 |
| C++ 求值 | `src/common/matcher.h` | 集合 OR/AND、三种进程语义、首命中 |
| C# 镜像 | `Policy/RuleBlobLayout.cs`、`RuleBlobHeader.cs`、`RuleBlobRule.cs`、`RuleBlobProcessSelector.cs`、`RuleBlobPortRange.cs`、`RuleBlobNetworkPrefix.cs` | 结构布局与常量 |
| C# 编译 | `Policy/RuleBlobCompiler.cs`、`RuleOrderValidator.cs`、`ProcessSelector.cs`、`WindowsProcessPathNormalizer.cs`、`WindowsDevicePath.cs`、`PolicyLimits.cs` | 归一化、校验、编码、预算与顺序 |
| C# 配置 | `Configuration/PolicyConfigurationDocument.cs`、`PolicyConfigurationDocument.HostSelectors.cs` | 单值 / 数组、JSON 路径错误、端口合并 |
| C++ 宿主测试 | `tests/host/rule_blob_test.cpp`、`rule_blob_validation_test.cpp`、`matcher_test.cpp`、`matcher_array_test.cpp`、`differential_test.cpp` | 布局负向矩阵、匹配语义、跨语言差分 |
| C# 测试 | `WinForward.Userland.Tests/{LayoutTests, BlobAbiConsistencyTests, CompilerTests, CompilerSelectorTests, CompilerLimitTests, RuleOrderTests, RuleOrderProcessTests, ConfigurationTests, ConfigurationArrayTests, ProcessPathTests}.cs` | 单侧规范化与布局、预算、顺序矩阵 |
| 夹具 | `tools/wfprobe/{BlobBuilder, RuleBlobOrder, Protocol, HostRuleParitySuite}.cs` | 独立 v2 构造器、顺序/预算镜像、驱动 parity 用例 |

驱动侧对应：`src/driver/rule_table.cpp`（`block_expression_valid`、`block_filter_budget_valid`、
`rule_order_valid`、`allocate_rule_table`）、`src/driver/block_filters.cpp`（展开与安装）、
`src/driver/process_path.cpp`（`ALE_APP_ID` → UTF-8）。

验证入口：

```bash
# 宿主：C# 单测 + 布局/ABI + 差分夹具
dotnet run --project src/userland/WinForward.Userland.Tests -- test
cmake --build build --target wf_check

# 全量宿主门禁（含差分与 clang-tidy）
tools/check.sh --full

# Windows VM：驱动套件（含 HostRuleParitySuite 的文件名/精确路径/目录/数组/block 用例）
wfprobe rules
pwsh -File tools/windows/gate.ps1 -Root C:\workspace\WinForward.Kernel -Remote 192.168.100.4:40001
```

## 10. 已知边界

- **路径型选择器要求编译期路径存在**（文件或目录）；不存在的路径编译失败，而不是装一条
  永远不命中的规则。
- **路径型选择器始终带前缀语义**，即使编译期解析结果是文件；顺序校验因此对路径选择器保守
  （可能拒绝理论上不可能同时命中的组合）。
- **block 不支持文件名 / 目录前缀**；这是 WFP auth filter 的结构限制。classify-enforced block
  与 auth callout 方案另开任务，本层不引入 sink。
- **网段池不做跨规则去重**：blob 比完全去重略大，但规则区间连续，实现与校验更简单。
- **v1 不兼容**：`rule_blob_version` 已是 2，旧 blob 整块拒绝；控制器、驱动、`wfprobe` 必须
  同批升级。配置层接受单值（等价于单元素数组），旧配置文件不需要改。
- **Host 域没有 adapter 选择器**（D3）；Forwarded 域才有入口适配器。
- Host 规则只治理 TCP / UDP；其他协议由 classify 计入 `permitted_non_tcp`，不命中任何规则。
- 进程串 511 字节是 UTF-8 不含 NUL 的上限；超过时连接侧整条不匹配（长度置 0）、不截断，
  所以用户态必须提前拒绝。
