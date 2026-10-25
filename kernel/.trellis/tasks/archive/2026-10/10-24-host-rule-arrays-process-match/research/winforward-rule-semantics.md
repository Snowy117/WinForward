# WinForward 规则语义与内核表达边界

> 调研日期：2026-10-24。对象：`../WinForward` 的 Host 规则模型、本仓库现有 blob v1 / matcher /
> block filter，以及 Windows VM 上的 WFP 行为实测。

## 1. WinForward 的规则模型

### 1.1 形状

- `RuleDto` 的每个选择器都是数组：`Process`、`AdapterId`、`AdapterName`、`Protocol`、
  `AddressFamily`、`RemoteCidr`、`RemotePort`（`src/WinForward.Configuration/ConfigurationModels.cs:85-114`）。
- `RuleDomainDto.Rules` 是有序数组，`FallbackAction` 是域级兜底
  （`ConfigurationModels.cs:43-50`）。
- 同一字段内是 OR，字段之间是 AND；规则顺序即优先级，首个命中生效
  （`src/WinForward.Core/Policy.cs:3-23`、`Policy.cs:55-79`）。
- 数组存在时不得为空、元素不得为空或纯空白；未知大小写错误的键直接失败
  （`ConfigurationRules.cs:50-98`、`ConfigurationRules.cs:118-133`）。
- Host 域出现 `process` 是合法的；Forwarded 域出现 `process` 在配置校验期拒绝
  （`ConfigurationRules.cs:58-65`）。内核版 Host 域没有 adapter 选择器（D3）。

### 1.2 进程选择器（`src/WinForward.Core/ProcessSelectors.cs`）

| 写法 | 判定 |
|---|---|
| 不含 `/` 或 `\` | 先 trim；与 `ProcessName` 做 OrdinalIgnoreCase 相等，或与 `GetFileName(ProcessPath)` 相等 |
| 含 `/` 或 `\` | `NormalizePath`：trim、`/`→`\`、Windows 上 `Path.GetFullPath`、去尾部 `\`；与归一化后的完整路径 OrdinalIgnoreCase 相等，**或**完整路径位于该目录之下（前缀 + `\`） |

- 文件名带扩展名的写法不会匹配不带扩展名的进程名（`dns` ≠ `dns.exe`）。
- 目录选择器同时匹配“路径相等”和“目录下任意深度”，没有单独的“目录”标记；
  路径末尾带不带 `\` 都一样。
- 单测：`tests/WinForward.Core.Tests/EndpointAndPolicyTests.cs:164-205`。

### 1.3 其它选择器（`ConfigurationRules.cs:135-215`）

- `RemoteCidr`：每个元素 `IPPrefix.TryParse`；v4 / v6 可混写。
- `RemotePort`：每个元素 `"443"` 或 `"8000-8100"`；端口 0 非法；解析后按起点排序并合并
  **相邻**区间（`range.Start > end + 1` 才另起一段）。
- `Protocol`：只接受 `tcp` / `udp`；数组缺省表示任意。
- `AddressFamily`：接受 `IPv4` / `InterNetwork` / `IPv6` / `InterNetworkV6`（忽略大小写）；
  数组缺省表示任意。
- `Action`：`proxy` / `pass` / `block`；`proxy` 必须带 `Target`，非 proxy 禁止 `Target`
  （`ConfigurationRules.cs:100-116`、`ConfigurationRules.cs:74-82`）。`Target` 映射不在本任务范围。

### 1.4 实物配置

`../WinForward/src/WinForward.Cli/appsettings.example.json` 与 `examples/process-proxy.json`
给出了三种进程写法的同框示例：

```json
"Process": [
  "C:\\Path\\To\\TargetExe.exe",
  "D:\\Path\\To\\Folder\\",
  "Just-A-Name.exe"
]
```

## 2. 本仓库现状

- 规则模型是单值：`PolicyRule.Process` / `RemoteNetwork` / `RuleProtocol` /
  `RuleAddressFamily` / `RemotePorts`（`src/userland/WinForward.Userland/Policy/PolicyRule.cs`）。
- blob v1 每条规则只有一个 `process_index`、一个 `network_index`、一个连续端口区间、一个协议、
  一个地址族（`src/common/rule_blob.h:85-106`）。
- matcher 的进程比较是**完整 ALE_APP_ID 的精确字节比较**
  （`src/common/matcher.h:69-102`）；没有文件名或目录语义。
- 编译期只用 `FwpmGetAppIdFromFileName0` 解析 DOS 路径
  （`src/userland/WinForward.Userland/Policy/WindowsAppId.cs`、`FwpmAppIdNormalizer.cs`）。
- block 规则编译成 `ALE_AUTH_CONNECT_V4/V6` 的 `FWP_ACTION_BLOCK` filter；进程条件只写
  `FWPM_CONDITION_ALE_APP_ID` + `FWP_MATCH_EQUAL`（`src/driver/block_filters.cpp:153-165`）。
- 规则顺序冲突由 `RuleOrderValidator`（C#）与驱动 `rule_order_valid`
  （`src/driver/rule_table.cpp:206-231`）两侧拒绝；block filter 本身不参与顺序比较。

## 3. Windows VM 实测（WinLTSC / 26100，2026-10-24）

### 3.1 `FwpmGetAppIdFromFileName0`

脚本：`local/appid-probe.ps1`（本机临时文件，未入库）。

| 输入 | 结果 |
|---|---|
| `C:\Windows\System32\notepad.exe` | `\device\harddiskvolume3\windows\system32\notepad.exe`，size=106 |
| `C:\WINDOWS\SYSTEM32\NOTEPAD.EXE` | 同上（**输出已小写归一**） |
| `C:\Windows\System32` | `ERROR_ACCESS_DENIED (5)` |
| `C:\Windows\System32\` | `ERROR_PATH_NOT_FOUND (3)` |
| `C:\Program Files` | `ERROR_ACCESS_DENIED (5)` |
| `C:\NoSuchFile.exe` | `ERROR_FILE_NOT_FOUND (2)` |

结论：该 API 只接受**已存在的文件**，不能用来解析目录选择器。

### 3.2 句柄 + `GetFinalPathNameByHandleW(VOLUME_NAME_NT)`

脚本：`local/ntpath-probe.ps1`（本机临时文件，未入库）。
`CreateFileW(path, 0, share=7, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS)` +
`GetFinalPathNameByHandleW(..., FILE_NAME_NORMALIZED | VOLUME_NAME_NT)`。

| 输入 | 结果 |
|---|---|
| `C:\Windows\System32` | `\Device\HarddiskVolume3\Windows\System32` |
| `C:\Windows\System32\` | 同上（尾斜杠被归一） |
| `C:\WINDOWS\SYSTEM32\NOTEPAD.EXE` | `\Device\HarddiskVolume3\Windows\System32\notepad.exe`（按磁盘大小写） |
| `C:\Program Files` | `\Device\HarddiskVolume3\Program Files` |
| `C:\` | `\Device\HarddiskVolume3\` |
| `C:\NoSuchDir` | `open-failed err=2` |

结论：同一个 API 同时支持文件与目录，输出与 ALE_APP_ID 的设备路径同形；再做 ASCII 小写折叠即可
与 `FwpmGetAppIdFromFileName0` 的结果一致。目录选择器必须走这条路。

## 4. block 的 WFP 表达边界

### 4.1 为什么 auth filter 表达不了目录前缀

- 这一层的进程条件只有 `FWPM_CONDITION_ALE_APP_ID`，值是 UTF-16LE 完整设备路径。
- WFP 的匹配类型里没有 app-id 前缀匹配；`FWP_MATCH_PREFIX` 文档明确说命名有误导性，
  实际是**后缀**匹配，且只支持字符串型的 `FWP_BYTE_BLOB_TYPE` / `FWP_UNICODE_STRING_TYPE`
  （<https://learn.microsoft.com/en-us/windows/win32/api/fwptypes/ne-fwptypes-fwp_match_type>）。
- 即使有前缀运算符，目录选择器也构造不出条件值（§3.1）。
- 认证层 callout 的“不匹配就交给别的 filter”没有可用返回语义：终止型 callout 返回 PERMIT
  会抢占防火墙的 Block，返回 CONTINUE 被引擎当成“没有决定”而拒绝所有连接
  （`wfp-architecture.md` §7.9 实测）。

### 4.2 对顺序语义的影响

- classify 按 blob 顺序求值，首个命中的 block 只记账；真正的拒绝由独立 filter 完成。
- 为避免“filter 抢掉更早的 permit/redirect”，两侧都拒绝“更早的非 block 规则与后面的 block
  规则可能重叠”的配置。因此顺序语义严格，但以**禁止歧义配置**为代价。
- 结论：本任务保持这一边界；block 规则的进程选择器只允许**精确文件路径**；文件名 / 目录前缀
  出现在 block 规则里在编译期拒绝并带规则序号。classify-enforced block（sink）另开任务。

## 5. 对设计的直接约束

1. blob 必须能表达“每条规则多个进程选择器 / 多个网段 / 多个端口区间 / 协议集合 / 族集合”，
   且一条配置规则仍只对应一条 blob 规则。
2. 进程选择器需要两种匹配形态（文件名 / 路径），路径型还需要区分解析结果是文件还是目录，
   供 block 表达边界与顺序校验使用。
3. 路径型选择器的归一化必须改走句柄 API，才能解析目录；文件名型不解析。
4. 编译期与驱动侧的顺序校验、block filter 展开都必须按集合语义重写，并保持两侧一致。
