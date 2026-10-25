# Host 规则数组与进程选择器 parity

## Goal

让 Host 域规则的形状与匹配语义对齐 WinForward 的策略模型，使 WinForward 的配置规则可以原样降级成
内核规则表：一条配置规则对应一条 blob 规则，选择器字段支持数组，进程选择器支持文件名、精确路径与
目录前缀三种形式。

## Background

### WinForward 的规则模型（`../WinForward`）

- 规则位于 `WinForward.Host.Rules` / `WinForward.Forwarded.Rules`，每条规则的每个选择器都是数组：
  `Process`、`AdapterId`、`AdapterName`、`Protocol`、`AddressFamily`、`RemoteCidr`、`RemotePort`。
  同一字段内是 OR，字段之间是 AND；规则顺序即优先级，首个命中生效
  （`src/WinForward.Core/Policy.cs`、`src/WinForward.Configuration/ConfigurationModels.cs`）。
- `Process` 的三种写法（`src/WinForward.Core/ProcessSelectors.cs`）：
  - 不含 `/` 或 `\`：文件名匹配，比较进程名或进程完整路径的最后一段；
  - 含分隔符：先按 `\` 归一（`/` → `\`、去尾部 `\`、Windows 上取全路径），再匹配
    “完整路径相等 **或** 路径位于该目录之下（前缀 + `\`）”；
  - 比较全部大小写不敏感；`dns` 不匹配 `dns.exe`。
- `RemoteCidr` 元素是 `address/prefix`，v4 / v6 可混写；`RemotePort` 元素是 `"443"` 或
  `"8000-8100"`，解析后排序并合并相邻区间；`Protocol` 元素是 `tcp` / `udp`；`AddressFamily`
  元素是 `IPv4` / `IPv6`（也接受 `InterNetwork` / `InterNetworkV6`）。字段缺省表示不限制，
  数组存在时不得为空（`src/WinForward.Configuration/ConfigurationRules.cs`）。
- Host 域没有 adapter 选择器（内核版 D3，`wfp-architecture.md` §2）；Forwarded 域没有进程选择器。

### 本仓库现状

- 规则模型的选择器全部是单值（`src/userland/WinForward.Userland/Policy/PolicyRule.cs`）。
- blob v1 每条规则只有一个 `process_index`、一个 `network_index`、一个连续端口区间、一个协议与
  一个地址族（`src/common/rule_blob.h`）。
- 内核 matcher 把进程选择器当作完整 ALE_APP_ID 的精确匹配，没有文件名或目录语义
  （`src/common/matcher.h`）。
- 编译期用 `FwpmGetAppIdFromFileName0` 把 DOS 路径解析成设备路径并做 ASCII 小写折叠
  （`IProcessPathNormalizer` / `FwpmAppIdNormalizer` / `WindowsAppId`）。
- 临时 JSON 配置的选择器都是单值（`PolicyConfigurationDocument`）。

### 已确认的技术事实

- `ALE_CONNECT_REDIRECT_V4/V6` 只能拿到 `ALE_APP_ID`（UTF-16LE 设备路径），没有
  `PROCESS_PATH`；比较契约见 `wfp-architecture.md` §7.10。
- `FwpmGetAppIdFromFileName0` 实测把结果归一成小写设备路径，但**对目录路径失败**
  （`ERROR_ACCESS_DENIED` / `ERROR_PATH_NOT_FOUND`），只接受已存在的文件。
- 句柄法（`CreateFileW` + `GetFinalPathNameByHandleW(VOLUME_NAME_NT)`）对文件与目录都可用，
  输出与 app-id 同形的设备路径；再做 ASCII 小写折叠即可。VM 实测记录见
  `research/winforward-rule-semantics.md` §3。
- block 规则由 `ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK` filter 执行；进程条件只能写成
  `FWPM_CONDITION_ALE_APP_ID` 的精确 `FWP_MATCH_EQUAL`。WFP 没有 app-id 前缀匹配
  （`FWP_MATCH_PREFIX` 实际是后缀匹配），目录路径也无法构造 app-id。
- classify 层的 block 决策只记账；真正的拒绝只来自 auth filter。filter 表达不了的选择器若仍当
  block 处理，会出现“计数说拦了、连接却成功”的假 block。

## Requirements

- **R1 规则数组**：Host 规则的选择器支持多值：进程、远端网段、远端端口、协议、地址族。
  同一字段内 OR、字段之间 AND，规则顺序即优先级。
- **R2 进程选择器语义**：文件名、精确路径、目录前缀三种形式与 WinForward 一致；大小写只折叠
  ASCII，非 ASCII 原样比较。
- **R3 一条配置规则对应一条 blob 规则**：规则表条目数等于配置规则数，事件/计数里的
  `rule_index` 与配置顺序一致；数组不得在编译期展开成多条 blob 规则。
- **R4 ABI 升版**：规则 blob 从 v1 升到 v2，支持每条规则的进程选择器列表、网段列表与端口区间
  列表；用户态与驱动同批升级，旧版 blob 整块拒绝。
- **R5 编译期归一化**：路径型进程选择器用句柄法解析成内核实际会看到的 ALE_APP_ID 设备路径
  （含目录，判定 `IsDirectory`）；ASCII 折叠、UTF-8 ≤511 字节、非法/孤立代理项拒绝。
  文件名型选择器只折叠大小写，不做路径解析。
- **R6 配置数组**：临时 JSON 配置接受数组与单值两种写法（单值等价于单元素数组）；空数组、
  空元素、非法值带 JSON 路径拒绝；端口排序合并；语义与 WinForward 对齐。
- **R7 规则顺序校验**：用户态校验器与驱动 `rule_order_valid` 按新语义（集合 OR、进程前缀）
  判断 block 规则是否被更早的非 block 规则遮蔽，两侧判定必须一致。
- **R8 block 边界（本任务不改变 block 架构）**：block 规则的进程选择器只允许**精确文件路径**
  （可写数组）；文件名 / 目录前缀出现在 block 规则里在编译期拒绝并带规则序号。网段 / 端口 /
  协议 / 族数组按 auth filter 可表达的方式展开，总数受 `max_block_filters = 128` 限制。
  不引入 sink、auth callout 或包级执行；D4 的 RST / 静默丢包语义与现有顺序歧义拒绝保持不变。
- **R9 测试与夹具**：C# 单测、C++ gtest、跨语言差分、`wfprobe` 夹具、驱动上机门禁全部覆盖新
  语义；至少包含“目录前缀不误伤同名前缀目录（`tools` vs `tools2`）”与数组 OR/AND 矩阵。
- **R10 文档**：更新 `wfp-architecture.md`、`src/common/rule_blob.h` 注释、用户态/驱动 README，
  并新增 Host 规则 blob v2 的 spec。

## Acceptance Criteria

- [ ] 一条同时写满 `process` / `remoteNetwork` / `remotePorts` / `protocol` / `family` 数组的
      配置规则编译成 **1** 条 blob 规则；驱动回读的规则数与配置规则数相等。
- [ ] 文件名选择器 `dns.exe` 命中 `\device\...\dns.exe`，不命中最后一段不同的路径；`dns` 不命中
      `dns.exe`。
- [ ] 目录选择器 `C:\Program Files\MyApp`（带或不带尾斜杠）命中该目录的直接子文件与任意深度的
      后代；不命中 `C:\Program Files\MyApp2\x.exe`。
- [ ] 精确路径选择器命中该文件本身；同名文件位于其他目录不命中。
- [ ] 同一字段多值按 OR 匹配；任一字段不满足则整条规则不命中；规则顺序保持配置顺序。
- [ ] block 规则写精确文件路径数组时，每条路径都生成对应的 auth filter 并生效；写文件名 /
      目录前缀时编译期失败，消息包含规则序号与原因；驱动对同形 blob 整块拒绝。
- [ ] 现有顺序歧义拒绝继续生效：更早的非 block 规则与后面的 block 规则可能重叠时整表拒绝，
      错误消息带两条规则序号。
- [ ] 现有编译期校验（fallback=block 矛盾、端口区间颠倒、族/网段不一致、511 字节上限）继续生效。
- [ ] C# 产出的 blob 由 C++ `try_open_blob` + `match` 消费时，对同一组连接事实与 C# 参考判定
      逐条一致（差分用例）。
- [ ] 驱动门禁：`appid` / `rules` / `udp-rules` / `forwarded-rules` / `selftest` / `gates` 全绿；
      64 KiB blob 上限边界（最大约 2046 条空规则）与现有 E2E 不回归。4096 条空 permit 表在 v2 下
      不可构造，改为上限边界用例；修订依据见 `research/plan-review.md` P2 #13。

## Out of Scope

- classify-enforced block（sink / 重定向到本地黑洞）与 auth callout 方案：另开任务做 VM spike 与
  架构改造。本任务保留现有 auth filter 与顺序歧义拒绝。
- WinForward 的 `Target` / SOCKS5 目标映射、`appsettings.json` 整体 schema 集成、
  `Forwarded.Rules` 的数组化（forwarded blob 是另一套 ABI，本任务不动）。
- Host adapter 选择器（内核版 D3 明确不支持）。
- 域名 / LocalTargets / UDP 中继等不在本仓库范围的能力。

## Technical Notes

- 路径型选择器要求编译期路径存在（文件或目录）；不存在的路径编译失败，带规则序号与选择器原文。
  这与现有 `FwpmGetAppIdFromFileName0` 对文件的要求一致，且更可诊断。
- 路径型选择器在 matcher 中始终按“相等或目录前缀”匹配，即使解析结果是文件；顺序校验因此对
  路径选择器保守。
- 网段池不做跨规则去重；进程串池仍去重。blob 总大小上限 64 KiB，编译期检查。
- 驱动与用户态、`wfprobe` 必须同批升级；控制面 IOCTL ABI 仍是 v7。
