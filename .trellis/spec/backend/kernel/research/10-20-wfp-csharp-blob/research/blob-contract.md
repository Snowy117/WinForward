# blob 契约实测记录 —— C# 侧对照

> 记录人：实现子代理（B4）
> 日期：2026-10-20
> 结论先行：C# 侧（`src/userland`）产出的字节与 `src/common/rule_blob.h` 逐字节一致，
> 由 32 组 × 15 条连接（480 条决策断言）的差分测试锁定。本文件记录**实际测量到的数值**与几处必须知道的约定。

---

## 1. 工具链

- 目标框架 `net10.0`。仓库 devShell 提供 `dotnet-sdk_10`（10.0.401，`DOTNET_NOLOGO=1`、
  遥测关闭），Windows VM 上是 SDK 11，都能编 `net10.0`；C# 侧不依赖宿主的全局 dotnet。
- 测试工程零 NuGet 依赖：只靠 SDK 自带的 BCL，`dotnet run` 即可跑，避免 Windows VM 上
  `NuGet.config` 指向本地源时 restore 失败。

---

## 2. 实测布局

`Marshal.SizeOf` 与 `Unsafe.SizeOf` 三处全部相等；`[StructLayout(LayoutKind.Sequential,
Pack = 4)]` 的 `Pack` 也在测试里断言。

### 2.1 头部 `RuleBlobHeader` —— 40 字节

| 偏移 | C# 字段 | C++ 字段 | 类型 |
|---:|---|---|---|
| 0 | `Magic` | `magic` | `uint32` |
| 4 | `Version` | `version` | `uint16` |
| 6 | `HeaderSize` | `header_size` | `uint16` |
| 8 | `TotalSize` | `total_size` | `uint32` |
| 12 | `RuleCount` | `rule_count` | `uint32` |
| 16 | `FallbackAction` | `fallback_action` | `uint32` |
| 20 | `RulesOffset` | `rules_offset` | `uint32` |
| 24 | `ProcessPoolOffset` | `process_pool_offset` | `uint32` |
| 28 | `ProcessPoolSize` | `process_pool_size` | `uint32` |
| 32 | `NetworkPoolOffset` | `network_pool_offset` | `uint32` |
| 36 | `NetworkPoolSize` | `network_pool_size` | `uint32` |

`Marshal.SizeOf<RuleBlobHeader>() = 40`、`Unsafe.SizeOf = 40`。

### 2.2 规则 `RuleBlobRule` —— 16 字节

| 偏移 | C# 字段 | C++ 字段 | 类型 |
|---:|---|---|---|
| 0 | `ProcessIndex` | `process_index` | `uint32` |
| 4 | `NetworkIndex` | `network_index` | `uint32` |
| 8 | `RemotePortStart` | `remote_port_start` | `uint16` |
| 10 | `RemotePortEnd` | `remote_port_end` | `uint16` |
| 12 | `Protocol` | `protocol` | `uint8` |
| 13 | `Family` | `family` | `uint8` |
| 14 | `Action` | `action` | `uint8` |
| 15 | `Reserved` | `reserved` | `uint8` |

### 2.3 网段记录 `RuleBlobNetworkPrefix` —— 20 字节

| 偏移 | C# 字段 | C++ 字段 | 类型 |
|---:|---|---|---|
| 0 | `PrefixLength` | `prefix_length` | `uint8` |
| 1 | `Family` | `family` | `uint8` |
| 2 | `Reserved0` | `reserved[0]` | `uint8` |
| 3 | `Reserved1` | `reserved[1]` | `uint8` |
| 4 | `Network` | `network[16]` | 16 字节内联数组 |

C++ 侧是 `reserved[2]`，C# 侧拆成两个字节字段：偏移与「两者都必须为 0」的语义完全等价，
内核校验不会因此有区别。

---

## 3. 两段真实字节

### 3.1 空规则表（40 字节，兜底 block）

```
57 52 42 31 01 00 28 00 28 00 00 00 00 00 00 00
01 00 00 00 28 00 00 00 28 00 00 00 00 00 00 00
28 00 00 00 00 00 00 00
```

解出来是 `magic=WRB1`、`version=1`、`header_size=40`、`total_size=40`、`rule_count=0`、
`fallback_action=1`，三个段起点全部等于 40、两个池长度 0。

**零长度段落在 `total_size` 端点**是合法的，`try_open_blob` 的重叠检查对空区间恒为 false。
这条不是巧合，是空表能被表达的前提。

### 3.2 一条规则 + 进程池 + 网段池（132 字节）

以 `process-and-network-together` 为例（规则：进程 svchost + `10.1.0.0/16` + 端口 443）：

| 字段 | 值 | 说明 |
|---|---:|---|
| `total_size` | 132 | |
| `rule_count` | 1 | |
| `rules_offset` | 40 | 紧接头部 |
| `process_pool_offset` | 56 | 40 + 1×16 |
| `process_pool_size` | 53 | `\device\harddiskvolume1\windows\system32\svchost.exe` + NUL |
| `network_pool_offset` | 112 | 56 + 53 = 109，**向上对齐到 4** |
| `network_pool_size` | 20 | 一条记录 |

那段 3 字节的填充不会被任何字段引用，`process_pool_size` 写的是 53 而不是 56 ——
与「精确串字节数、不含对齐填充」的约定一致。规则本体是
`process_index=0`、`network_index=0`、`ports=443..443`、`protocol=0`、`family=4`、`action=1`。

进程池里放的是**设备路径**而不是配置里的 DOS 路径 —— 内核在 ALE_APP_ID 里看到的正是前者。
网段池第一条是 `10 04 00 00 0a 01 00 00 ...`：`prefix_length=16`、`family=4`、`reserved=0/0`、
`network=10.1.0.0`。

---

## 4. 编译期的规整规则

| 规整 | 做法 | 理由 |
|---|---|---|
| 进程串归一化 | 生产实现 `FwpmAppIdNormalizer`：DOS 路径 → `FwpmGetAppIdFromFileName0` 的 ALE_APP_ID 设备路径 → 只折叠 ASCII 小写；底层折叠复用 `AsciiLowercaseNormalizer` | 内核在 ALE_CONNECT_REDIRECT 层只有 ALE_APP_ID（设备路径）；两边必须比较同一种表示。归一化是编译期唯一一处「路径表示」决策点 |
| 进程串去重 | 按**归一化后**的串去重，首次出现顺序 | 大小写不同的同一路径在内核看来就是同一条串 |
| 进程串按 UTF-8 计长 | `process_pool_size` 与索引都用 UTF-8 字节数，不是字符数 | 池是字节缓冲；中文路径的字节数与字符数不同，用字符数会把后续串的索引全部写错 |
| 进程路径上限 | 归一化后的串超过 **511 字节**（UTF-8，不含 NUL）拒绝编译 | 内核 `collect_process_path` 的缓冲区是 512（含 NUL），超限路径会被整条丢弃（`process_path_length = 0`）—— 规则永久静默不命中 |
| 主机位清零 | 构造 `RemoteNetwork` 时按前缀掩掉主机位 | 同一网段的不同写法归一成一条记录，池不膨胀 |
| 网段去重 | 按「族/前缀/16 字节地址」去重 | 记录定长 20 字节，索引必须是 20 的倍数 |
| 端口区间 | 原样写 `uint16` 端点，闭区间 | `matcher.h` 按闭区间比较 |
| 任意哨兵 | 无进程 → `0xFFFFFFFF`；无网段 → `0xFFFFFFFF`；端口 0..65535；协议 0；族 0 | 与 `rule_blob.h` 的常量一一对应 |

非法输入一律在编译期拒绝，消息带规则序号：未知动作/协议/族、端口区间颠倒、规则族与网段族
不一致、规则为 null、兜底动作未知。进程串的字符校验在 `ProcessSelector.Parse`（空串、
控制字符、NUL、孤立代理项都拒绝；非 ASCII 允许）。归一化结果还会再校验一次：空串、null、
控制字符、孤立代理项、超过 511 字节都是编译错误 —— 这条路径对自定义实现是真实可达的。
上限常量是 `PolicyLimits.MaxProcessPathBytes`，与内核 `max_process_path_length = 512`（含 NUL）成对维护。

---

## 5. 差分夹具

C# 测试写出一份 `manifest.txt` 加每个用例一个 `.blob`：

- 每行 TAB 分隔，`#` 开头为注释；
- 记录类型：`version` / `case` / `blob` / `rules` / `fallback` / `connect`；
- `connect` 的字段：地址 hex、端口、协议、族、进程路径（`-` 表示 nullptr）、
  期望是否命中、期望规则序号（未命中写 `-`）、期望动作、人读标签。

地址 hex 在 ipv4 时 8 个字符、ipv6 时 32 个字符，C++ 侧按族补零到 16 字节。
进程路径按 UTF-8 原样写出（含中文），生成端保证里面没有 TAB 与换行 —— 否则会把清单撕成两行。

夹具格式的详细定义写在 `tests/host/differential_fixture.h` 的头注释里 —— 消费端与生成端
各写一遍是刻意的：读不懂就报错，不猜。

---

## 6. 差分测试

| 项 | 数值 |
|---|---|
| 用例数 | 32（精确值断言） |
| 每组连接数 | 15（精确值断言） |
| 「连接 → 决策」断言 | 480 条（每条比命中标志、动作、规则序号） |
| blob 接受性断言 | 32 条（`try_open_blob` 必须接受）+ 每组 3 条头部字段对照 |
| 覆盖维度 | 另有必需用例名单，删掉或改名会指出缺哪一类 |

覆盖维度：空规则表 ×2（兜底 block / redirect）、多规则优先级（顺序、具体优先、索引上报）、
CIDR `/0` 与 `/32`（v4 与 v6 各一组）、`/24` 主机位、`/128`、双默认路由、端口单点/区间端点/
`0..0`/`65535`、进程串大小写与「整串相等而非前缀」、协议任意与 TCP（含一条 protocol=17 的
连接）、无进程路径的连接、IPv4 与 IPv6 混合、进程 + 网段 + 端口同时命中、中文设备路径
（规则写 DOS 路径、连接写设备路径，验证 ALE_APP_ID 解析与 UTF-8 字节比较）。

失败信息示例（变异测试实测输出）：

```
Expected equality of these values:
  action_name(result.action)      Which is: "block"
  action_name(connection.expected_action)  Which is: "redirect"
决策动作
Google Test trace:
  connection=6 (ipv4 10.1.2.3:65535 other) family=4 protocol=6 port=65535 address=0a010203 process=c:\other.exe
  case=port-max-only
```

---

## 7. 变异测试记录

门禁要能变红才算门禁。两条都实测过：

1. 把某条连接的期望动作从 `block` 改成 `redirect` → 差分测试红，指出 case、连接序号与两侧动作；
2. 把头部 `header_size` 改成 41 → C# 夹具自检红（`期望 40，实际 41`），同时 C++ 侧
   `try_open_blob` 拒绝全部 32 个 blob；
3. 只改 C# 期望但不重跑生成，直接 `ctest`：**仍然全绿**（C++ 读的是旧夹具）—— 这是门禁的洞；
4. 同样只改 C# 期望，跑 `cmake --build build --target wf_check`（先重新生成夹具再跑全套）：
   红，退出码 8，指出 `case=port-max-only` / `connection=6` / 期望 `redirect` 实际 `block`。

---

## 8. 留给后续批次

- **进程路径的表示**（`wfp-architecture.md` 的 O5，已由 B3 交付契约）：内核只拿得到
  `ALE_APP_ID`（设备路径），C# 侧在编译期用 `FwpmAppIdNormalizer` 调
  `FwpmGetAppIdFromFileName0` 解析成同一形式，再折叠 ASCII 小写、按 UTF-8 落盘。真实解析
  只能在 Windows 上跑；Linux 测试注入解析表（`TestAppId`），夹具里的连接侧就是设备路径。
- 本批只做「模型 → blob」：配置解析、IOCTL 下发、监听中继在后续批次。
