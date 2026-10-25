# WFP 内核化 —— 架构与已定决策

> 本文件记录**已经拍板的决策**与由它推出的组件划分。未决事项集中放在 §6，讨论中的方案不写进决策表。
> 最后更新：2026-10-24。

---

## 1. 目标与验收

把捕获与重定向从 NDISAPI（WinpkFilter，包级）迁移到 WFP callout（连接级），核心目标是降低 CPU。

**验收（kill criteria）**：480 Mbps THRU 代理路径进程 CPU 不能降到 110% 以下，就停止完整重写。

**成本结构的事实依据**（见 `benchmarks/results/2026-10-18-cpu-usage-analysis/README.md`）：

| 场景 | 单核 CPU |
|---|---:|
| 代理路径（现状） | 154.0% |
| 纯透传、同吞吐（现状） | 58.1% |
| sing-box 同轮 | 47.0% |

其中约 83% 是内核态时间、约 63% 在 `DeviceIoControl`，托管管线只占 3–6%。这组数字定了两件事：CPU 花在包的搬运上而不是托管代码上；以及"放行"在包级世界同样要付 58.1%，而在连接级世界是零。

---

## 2. 已定决策

| # | 决策 | 理由 |
|---|---|---|
| D1 | 主战场 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6`，TCP 每连接、UDP 每关联各 classify 一次 | TCP/UDP 数据面零驱动成本，这是收益的全部来源 |
| D2 | **策略在内核求值**，采用"一份策略、两种编译产物" | "放行"必须在内核完成，否则重定向到用户态再判 pass，会把免费路径变成付费路径。策略语义只有一份：用户态编译出内核 blob，内核侧只执行、不做语义判断 |
| D3 | **host 域不支持 adapter 选择器**（配置校验期拒绝） | `ALE_CONNECT_REDIRECT` 层拿不到接口索引——connect 时刻路由尚未决策。且语义已从"包在哪儿被观测到"位移成"连接将会走哪儿"，硬做出来与旧配置的含义也不一致。forwarded 域保留该选择器（仍在 NDISAPI 上） |
| D4 | block 语义：TCP 接受 RST，UDP 接受静默丢包 | classify 首个命中后改道到 `block_sink_port`，固定 4 条 `ALE_AUTH_CONNECT` filter 执行 `FWP_ACTION_BLOCK`；TCP 实测 `AccessDenied(10013)`，UDP 应用侧 send 成功、对端收不到（U13 实测）。不为这两种语义保留 packet 层 |
| D5 | 重定向方式：单端口 listener + `localRedirectContext` 传原始目标 | `localRedirectTargetPID`、`localRedirectHandle` 为 Win8+ 本地重定向的硬要求 |
| D6 | 重定向目的地址按文档分支：local 地址为 ANY 时指向 loopback，否则指向 local 地址 | 实测：`0.0.0.0` 监听可同时接受 loopback 与网卡地址连接；仅绑 `127.0.0.1` 不接受指向网卡地址的连接 |
| D7 | host 域做 TCP + UDP；forwarded 继续走现有 NDISAPI | UDP 不需要新 WFP 层：同一 `ALE_CONNECT_REDIRECT` 按关联 classify，回程由用户态 listener 用同一 socket 完成。仍不第一版同时打三个战场 |
| D8 | **本仓库自包含**：包含产品的全部必要代码，不依赖原 `WinForward` 仓库 | NDISAPI 到自研驱动的改动幅度已经超过“迁移”，两个产品只在名称与功能上重合、实现上完全不同。原仓库降级为参考读物，不构成编译或运行依赖 |
| D9 | 用户态留在 C#，作为“收 + 转”的控制器 | CPU 分析显示托管管线只占 3–6%，154% 里 83% 是内核态时间；把用户态改写成 C++ 买不到 CPU。用户态与内核的唯一接缝是两个 ABI 结构 |

---

## 3. 组件划分

```
┌──────────────────────────────────────────────────────┐
│ 用户态 —— C#（本仓库）                                 │
│  配置解析与校验                                        │
│  策略编译 → 内核规则 blob                              │
│  IOCTL 推送规则 / 回读计数                             │
│  TCP listener + SOCKS5 中继                            │
└──────────────────────────────────────────────────────┘
        │ IOCTL 推规则          │ redirect context
        ▼                       ▲
┌──────────────────────────────────────────────────────┐
│ 内核 —— C++ WFP callout 驱动（本仓库）                 │
│  engine        入口、callout 注册、IOCTL、自流量注册   │
│  rule_table    规则 blob 的原子替换与并发读            │
│  blob          blob 解码与校验（可移植）               │
│  matcher       谓词求值（可移植）                      │
│  classify      取 facts → 匹配 → 放行/BLOCK/重定向     │
│  redirect      WFP 重定向的封装与 redirect context     │
└──────────────────────────────────────────────────────┘
```

UDP 的 listener / 中继在 WinForward 侧；本仓库负责驱动侧 redirect context 与 `wfprobe` 验证工具。

**D2 落地的关键**：内核侧没有"规则语义"。`blob` 只做校验与解码，`matcher` 只做规范化后的比较——进程字符串在编译期已规整成内核实际会比较的形式，CIDR 已展开成掩码比较，端口区间已排序。内核不解释用户意图，只执行。

---

## 4. 可移植核心与测试

`blob` 与 `matcher` 是纯函数，不引用任何 WFP 或 WDK 类型。内核里那一层只负责把 WFP 给出的 facts 塞进一个中立的 `connection_view`，然后调用它们。

这条边界决定了测试形态：**匹配语义、blob 校验、边界条件可以在 Linux 上用 gtest 全量覆盖**（见 `flake.nix`），内核侧只剩薄薄一层 WFP 胶水，靠上机验证。最容易出错的逻辑因此不依赖 Windows VM。

`classify` / `redirect` / `engine` / `rule_table` 含内核依赖，只能上机测。

---

## 5. 跨语言契约

内核与 C# 之间有两个数据面 ABI 面，定义在本仓库、由 C# 侧镜像：

- **`wf_redirect_context`**：原始目的地（地址族、地址、端口）+ 诊断用标识。内核分配、用户态经 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读取。
- **`wf_rule_blob`**（Host 规则 blob，当前 **v2**）：56 字节头 + 每条规则 32 字节。规则的选择器全部是「池下标 + 数量」区间：进程指向 8 字节 `process_selector`，网段指向 20 字节 `network_prefix`，端口指向 4 字节 `port_range`；段序固定为 `header | rules | process_pool | selector_pool | network_pool | port_pool`，段首 4 字节对齐。v2 相对 v1 的破坏性变化是进程 / 网段 / 端口 / 协议 / 族都从单值变成集合（同字段 OR、字段间 AND，一条配置规则仍只编译成一条 blob 规则）；magic 仍是 `"WRB1"`，版本号为 2。
- 规则 blob 的字节布局、布局断言与解码 / 求值入口见 [`src/common/rule_blob.h`](../../../src/common/rule_blob.h) 与 [`src/common/matcher.h`](../../../src/common/matcher.h)；字段语义、`try_open_blob` 校验清单、进程选择器与 block sink 执行见 [host-rule-blob.md](./host-rule-blob.md)。

两侧都有布局断言：C++ 侧 `static_assert`（尺寸、对齐、`offsetof`），C# 侧以布局一致性测试锁定。v2 的常量由 `BlobAbiConsistencyTests` 从 `rule_blob.h` 源码文本读取，与 `RuleBlobLayout` / `PolicyLimits` / `tools/wfprobe/Protocol.cs` 三处互锁；版本号变更即视为破坏性变更。

控制面 ABI（`probe_ioctl.h`）当前是 **v8**：v3 的 `probe_event` / `probe_stats` 字段不变；v4 新增 `probe_forwarded_info` / `probe_forwarded_stats` 与四个 forwarded IOCTL（`0x808`–`0x80B`）；v5 新增 `probe_forwarded_config` / `probe_forwarded_redirect_query` / `probe_forwarded_redirect` 与 `0x80C`–`0x80E`，并在 `probe_forwarded_stats` 尾部追加 redirect 计数（296 字节）；v6 追加 13 个 UDP 计数扩到 400 字节；v7 新增 ingress 配置/查询（`0x80F`/`0x810`）、24→40B 查询、64→88B 输出与 stats 尾部 7 个 u64（456 字节，448 字节输出仍兼容）；**v8 在 `probe_config` 增加 `block_sink_port`（偏移 52），`reserved` 缩为 2 项**。v4 的 0–143 字段偏移不变，`last_status` / `abi_version` 后移。驱动与用户态必须同批升级。Host 规则 blob v2 不改控制面；IOCTL 按 v8 下发整块字节流。

---

## 6. 未决

| # | 事项 | 状态 |
|---|---|---|
| O1 | ~~仓库边界~~ —— 已由 D8 结案：自包含，用户态也在本仓库 | 已决 |
| O2 | IOCTL 协议的形状与版本化策略 | 未开始 |
| O3 | 无调试器时计数器与诊断的回灌通道 | 未开始 |
| O4 | 防循环各道闸的具体判据与顺序 | 未开始 |
| O5 | 进程选择器在内核侧的比较形式（DOS 路径 / NT 路径 / appId）与编译期规整规则 | **已决**：内核只比较 `ALE_APP_ID` 的 UTF-16LE 设备路径转成的 UTF-8；编译期用句柄法把 DOS 路径解析成同形设备路径。文件名 / 精确路径 / 目录前缀三种语义、`\device\` 前缀、ASCII 折叠与 511 字节上限见 §7.10 与 [host-rule-blob.md](./host-rule-blob.md) |
| O6 | Phase 0 验证范围 —— 选定 D3 后收敛为：redirect 能否落地、用户态能否取到 context 与 records | **已结案**：三条全部成立，见 `.trellis/tasks/10-20-wfp-phase0-probe/research/phase0-findings.md` |
| O7 | Host UDP 的接入范围与回程 —— 已由 D7 结案：host 域同时做 TCP 与 UDP，UDP 回程由用户态 listener 用同一 socket 完成，驱动不注入 | **已决**；实现事实见 §7.11 |

---

## 7. 内核实现事实（Phase 0 与 Host UDP 实测）

以下每一条都踩过一次，重新发现一遍的成本很高。写驱动前先读这一段。

### 7.1 重定向上下文的所有权

`FWPS_CONNECT_REQUEST0::localRedirectContext` 必须用 `ExAllocatePoolWithTag` 分配。**Win8 起所有权归 WFP**，flow 移除时由引擎释放——驱动**绝不**自己 `ExFreePool`。实测覆盖了两种结局：重定向目标接住了连接、以及 apply 成功但监听端没接住（池计数各 +1/+1，outstanding 归零）。

`FwpsApplyModifiedLayerData0` 返回 `void`，真失败时是否已被接管无从判定——宁可每次泄漏 36 字节，也不要尝试释放。UDP 关联的 context 释放时机与 TCP flow 不同，见 §7.11。

### 7.2 acquire 必须配对 apply

`FwpsAcquireWritableLayerDataPointer0` 会把 `classifyOut->actionType` 置为 `FWP_ACTION_BLOCK` 并清掉 `FWPS_RIGHT_ACTION_WRITE`。因此：

- **每一次 acquire 都必须配对一次 `FwpsApplyModifiedLayerData0`**，即使什么都没改；
- 「只读不修改、本意放行」的分支必须显式恢复 `rights |= FWPS_RIGHT_ACTION_WRITE` 与 `actionType = FWP_ACTION_PERMIT`，再 apply。漏掉这一步会把连接**拒掉**，而代码看起来像是在放行——这是本层最容易犯、也最难看出来的错。

**但只要不写数据，就不要 acquire。** 只读判断（例如读 `FWPS_CONNECT_REQUEST0::previousVersion`）可以直接读 raw layer data，把它放在 acquire 之前，这条分支就整段没有 handle 生命周期，配对问题也就不存在。本仓库的 already-redirected 分支就是这么收的。

### 7.3 connect-redirect 层的可写范围

可写：`remoteAddressAndPort`、`portReservationToken`、`localRedirectTargetPID`、`localRedirectHandle`、`localRedirectContext(Size)`。
只读：`localAddressAndPort`。判断「local 地址是否为 ANY」只看地址，端口此时已被分配，不能当作"未绑定"的证据。

`localRedirectTargetPID` 与 `localRedirectHandle` 是 Win8+ 本地重定向的硬要求。

### 7.4 控制面设备必须显式指定 SDDL

用 `IoCreateDevice` 建的命名设备，默认 DACL 实测是 **`RW Everyone`**；配合 `FILE_ANY_ACCESS` 的 IOCTL，等于任何登录用户都能改写驱动配置——把任意进程的连接改道、读走原始目的地。必须用 `IoCreateDeviceSecure` 指定 SDDL，并做调用者校验。`FILE_DEVICE_SECURE_OPEN` 只管命名空间打开，不决定 DACL，不是缓解。

### 7.5 只挂 V4 会漏掉 dual-mode IPv6 连接

只注册 `ALE_CONNECT_REDIRECT_V4` 时，dual-mode socket 的连接看不到——本地端点是 `[::ffff:...]`，走的是 V6 层。.NET 的 `TcpClient.Connect(string, int)` 默认就是 dual-mode，浏览器同理。D1 要求 V4/V6，这一条是必测项，不是可选优化。

### 7.6 卸载必须检查注销返回值

`FwpsCalloutUnregisterById0` 返回 `STATUS_DEVICE_BUSY` / `STATUS_FWP_IN_USE` 时必须重试，`STATUS_FWP_CALLOUT_NOT_FOUND` 视为已注销。**绝不能在未注销成功时继续卸载**——引擎里留下指向已卸载代码的 callout，之后在任意上下文随机崩，最难查。

重试窗口 2 秒；耗尽后主动 `KeBugCheckEx`，把故障钉在卸载路径上，而不是留给未来的一次随机蓝屏。这个窗口按本驱动的当前不变量不可达（不关联 flow context、单线程卸载），bugcheck 的作用是把「不可达」这个假设变成可验证的——假设一旦不成立，立刻可见。

### 7.7 构建约束

- `<ndis.h>` 之前必须先 `#define NDIS630`；
- `fwpmk.h` / `fwpsk.h` 需要 `INITGUID`；
- `$(VC_IncludePath)` 必须前置，否则 WDK 自带的 `km\crt` 旧 `cstddef` 会遮蔽 MSVC 的 `std::size_t`；
- clang-format 会重排 include，与「include 顺序有语义」直接冲突——这类区块必须用 `// clang-format off` / `on` 包住。

### 7.8 放行分支的写权限终态只能选一种

`actionType = FWP_ACTION_PERMIT` 时把 `FWPS_RIGHT_ACTION_WRITE` 置位还是清除，两种都合法，但**同一个驱动里必须统一**，并在注释里写明理由，不要一半置位一半清位。

本仓库取「直接 PERMIT + 清位、定终态」：filter 是 terminating，不需要留给低权重 callout 二次修改。

引用 WDK 示例给决定背书之前，**先去看示例源码**。WFPSampler 用的是 `rights ^= FWPS_RIGHT_ACTION_WRITE`，且都发生在 `FwpsAcquireWritableLayerDataPointer0` **之后**——acquire 已清位，XOR 等价于置位。在 acquire 之前照抄这句话，结论正好相反。

### 7.9 block 不能在 redirect 层实现，也不能用 callout 实现

**第一层结论**：`ALE_CONNECT_REDIRECT` / `ALE_BIND_REDIRECT` 只改地址与端口，**不执行拦截**。在这一层把 `actionType` 置 `FWP_ACTION_BLOCK`，虽然计数器会涨、日志也会写，但**连接照样建立**——一个假的 block，比没有更糟。对 UDP 也一样：放行的是关联，datagram 仍会发出去。

**第二层结论**：在 `ALE_AUTH_CONNECT` 层用 callout 实现 block 也不行，两条路都被实测否掉：

| 做法 | 实测结果 |
|---|---|
| callout 返回终止型 `FWP_ACTION_PERMIT` | 把 Windows 防火墙的 Block 截胡——防火墙拦住的目标，连接变成成功 |
| callout 返回 `FWP_ACTION_CONTINUE` | 终止型 callout 的 CONTINUE 被引擎当作「没有决定」，所有连接被拒 |

**正确做法（方案 A）**：block 在 classify 里按 blob 首个命中决定；命中后把连接 / 关联改道到
`block_sink_port`，再由 `ALE_AUTH_CONNECT_V4/V6` 上固定 4 条 `FWP_ACTION_BLOCK` filter 执行
（协议 + 远端端口 == sink + `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`）。这些 filter 只
命中被我们改道到 sink 的流量，只增加拒绝、从不放行，因此不会截胡防火墙，也不会误伤该端口上
的正常流量。实测（26100）TCP 仍是 `AccessDenied(10013)`，UDP 静默；U13 证明 auth filter 命中
且 sink socket 收到 0 个 datagram。

**顺序与选择器不再受限**：block 参与 classify 首个命中，`permit 内网 → block 其余`、文件名 /
目录前缀 / 数组、`fallback = block` + 任意规则都可以表达；`RuleOrderValidator`、
`block_expression_valid`、`block_filter_budget_valid` 与 `max_block_filters` 已删除。

注意 `FWP_V4_ADDR_AND_MASK` 的 `addr` 与 `mask` 是**主机字节序**（fwptypes.h 原文），写成网络序会得到 `FWP_E_INVALID_NET_MASK`。

**失败路径**：classify 内的 sink 改道仍可能因内部错误失败；redirect 层不能拦截，按 fail-open
放行，记 `redirect_failures` 与 `block_enforcement_failed` 事件，不增加 `blocked`。不要把失败
写进 `blocked`。

`fallback_action = block` 现在只在没有规则命中时触发，不会让同表的 permit / redirect 变成死规则；
classify 对非 TCP/UDP 协议仍走 `permitted_non_tcp` 早退。

### 7.10 进程选择器的字节契约

`ALE_CONNECT_REDIRECT` 层**没有** `FWPS_METADATA_FIELD_PROCESS_PATH`（实测该层 `PROCESS_PATH` metadata 计数恒为 0，而同一次运行里进程规则能命中）。这一层能拿到的是固定的 `ALE_APP_ID`，即 **UTF-16LE 的设备路径**，形如 `\device\harddiskvolumeN\...`。

Host blob v2 的进程选择器有两种 kind、三种语义：

| 写法 | blob kind / `is_directory` | 匹配 |
|---|---|---|
| `dns.exe`（不含 `/`、`\`） | 文件名 / 0 | 只比较连接路径最后一个 `\` 之后的部分；`dns` 不命中 `dns.exe` |
| `C:\Tools\curl.exe` | 路径 / 0（精确文件） | 完整路径相等，**或**位于该路径之下（前缀 + `\`） |
| `C:\Program Files\MyApp` | 路径 / 1（目录） | 同上；路径型选择器在 matcher 里始终带前缀语义，`is_directory` 只保留为解析结果标记 |

路径型选择器写进 blob 的串必须是 **`\device\` 开头**（ASCII 大小写不敏感）、不以 `\` 结尾的设备路径；卷根 `\device\harddiskvolumeN` 合法。不满足这个形状的串永远命中不了内核看到的 `ALE_APP_ID`，所以编译期、`try_open_blob` 与 `wfprobe` 三处一致拒绝。

因此进程选择器两侧必须约定同一套字节形式：

| 项 | 约定 |
|---|---|
| 来源 | `ALE_APP_ID`（设备路径），不是 DOS 路径 |
| 编码 | UTF-16 → **UTF-8** |
| 大小写 | 只折叠 ASCII `A-Z`；非 ASCII 原样保留（中文路径必须能匹配） |
| 分隔符 | 反斜杠 |
| **长度上限** | **511 字节，UTF-8，不含结尾 NUL**（缓冲区 512 含 NUL） |
| 超限行为 | **整条不匹配**（`process_path_length` 置 0）。**不截断**——截断会产生错误匹配，比不匹配危险 |
| 孤立代理项 | 不匹配；用户态在编译期就拒绝 |
| 控制字符 | C0（U+0000–U+001F）、DEL（U+007F）、C1（U+0080–U+009F）在编译期与 `try_open_blob` 都拒绝 |
| 用户态归一化 | 句柄法（见下），输出与 `ALE_APP_ID` 同形 |

**路径解析改用句柄法。** `FwpmGetAppIdFromFileName0` 实测把结果归一成小写设备路径，但**对目录路径失败**（`ERROR_ACCESS_DENIED` / `ERROR_PATH_NOT_FOUND`），长路径也不可靠；它只接受已存在的文件。现在 `WindowsDevicePath.Resolve` 走 `Path.GetFullPath` → `\\?\` / `\\?\UNC\` 长路径前缀 → `CreateFileW(..., FILE_FLAG_BACKUP_SEMANTICS)` → `GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_NT)` → `GetFileInformationByHandle` 判目录 → 去尾部 `\`，再做 ASCII 小写折叠。路径型选择器要求编译期路径存在（文件或目录），解析失败带规则序号拒绝——这比“装一条永远不命中的规则”可诊断。

**这个上限必须由两侧共用同一个具名常量**，不要在任一侧写死数字。它曾经出现过三方不一致：契约写 511、classify 的条件写 `>= 512`（实际只接受 510）、block filter 按 511 算、而用户态完全没有检查——最终症状是 C# 编译出 624 字节的路径、blob 合法、内核折成 0，**规则永久静默不命中**。现在 `rule_blob.h` 的 `process_selector_text_max_size` 是唯一权威，`try_open_blob` 解码时逐条检查，用户态 `PolicyLimits.MaxProcessPathBytes` 与 `tools/wfprobe/Protocol.cs` 由 `BlobAbiConsistencyTests` 三处互锁。

差分测试对这种不一致无能为力：C++ 的 `matcher` 不认识这个上限，夹具里的路径又都很短。这条只能靠两侧各自的编译期/解码期检查，以及一条「恰好 511 通过、512 拒绝」的边界用例。

同理，句柄法的真实行为（`GetFinalPathNameByHandleW` 的输出形状、目录标记、UNC / 映射盘）只在 Windows 上可验证，Linux 单测只能覆盖注入的映射——真实 API 的覆盖属于 VM 集成测试，不要把它算进 Linux 门禁的覆盖范围。

**block 不再受 auth filter 的 app-id 精确匹配限制。** block 走 classify 首命中 + sink 改道，
所以文件名 / 精确路径 / 目录前缀（以及数组）都可以出现在 block 规则里；执行 filter 只认
sink 端口与 redirect 标记，不再关心进程选择器。

### 7.11 Host UDP 的实现事实（B0–B2b 实测）

- **没有新的 WFP 层，也没有回程注入。** UDP 和 TCP 共用 `ALE_CONNECT_REDIRECT_V4/V6` 的 callout；redirect classify 分配 36 字节 `redirect_context`（`protocol=17`），所有权交给 WFP。listener 用 `IP_WFP_REDIRECT_CONTEXT`（IPv4，`IPPROTO_IP`）/ `IPV6_WFP_REDIRECT_CONTEXT`（IPv6，`IPPROTO_IPV6`）从 `WSARecvMsg` 的 cmsg 逐 datagram 取回原始目的地；回包由用户态用**同一个 listener socket** `sendto`，驱动不改源、不注入 NBL、不维护 UDP flow 表。
- **同 socket 回包不重新 classify。** 中继用同一个 listener socket 把响应 `sendto` 回客户端时，
  datagram 复用入站重定向关联，不会再次进入 `ALE_CONNECT_REDIRECT`，`udp_classify_calls` 不增；
  self-PID 闸由 U1 的独立 socket 出站场景验证（permitted_self_process 增长）。`self_process_ids` 仍然要配置：中继新开 socket 访问上游或其他出站
  是新关联，需要闸 4 放行，否则会被再次 redirect / block。
- **classify 按关联，不按 datagram。** 同一 socket 对同一目的地 N 条 datagram 只产生 1 次 classify / 1 个 context；换目的地、换本地端口或换 socket 才是新关联。因此 classify 计数必须按关联数断言，不能写成「≤ N」这种会把「每条一次」和「每关联一次」都放过的弱断言。
- **block 在 classify 改道 sink，由 AUTH_CONNECT 固定 filter 丢弃。** classify 命中 block 时
  记 `blocked` / `deferred_blocks` 并把关联改道到 `block_sink_port`；真正丢弃由固定的
  `ALE_AUTH_CONNECT_V4/V6` filter 完成。UDP 的 `connect` / `send` / `sendto` 返回成功、对端
  收不到、`recv` 超时；TCP 对照是 `AccessDenied(10013)`。`udp_blocked` 是成功改道 sink 的
  UDP 关联数；热替换后已存在的 UDP 关联保持旧决策（U13），新关联才按新表 block。
- **固定 4 条 sink filter。** V4/V6 × TCP(6) / UDP(17) 各一条；每条显式写协议 + 远端端口 ==
  sink + `IS_CONNECTION_REDIRECTED`；不随规则数展开，也没有 128 预算。ICMP 等非 TCP/UDP
  协议不进 classify，也不会被这些 filter 命中。
- **IPv6 / dual-mode 的 family 取决于实际层。** v4-mapped unconnected 走 V4（family=4、纯 v4 地址），v4-mapped connected 与 native v6 走 V6（family=6、v4-mapped/16 字节地址）。cmsg 解析要同时接受 level 0 与 41、type 70，并支持 `sockaddr_in6`；不能笼统断言「dual-mode 一定 family=6」。
- **context 生命周期。** unconnected 在客户端 socket 关闭后 1s 内释放；connected 的释放窗口由 WFP 决定，B2b 观察到客户端退出 180s 后仍 outstanding、`sc stop` 后仍然存在，因此不能把「关闭即释放」当成保证，池断言只对 unconnected 严格。**已知 WFP 缺陷（26100）**：connected UDP 每包会给 `AleRedirectRecord` 加一次引用且不归还，refcount 随 datagram 增长，36B `WfPr` 直到重启才释放；实测 1/3/100 条 → 1/3/100，详见 [wfp-connected-udp-record-refcount-leak.md](./wfp-connected-udp-record-refcount-leak.md)。
- **规则语义。** `protocol=udp` 是 17；`protocol=any` 同时覆盖 TCP 与 UDP（相对旧版的行为变化）；`permitted_non_tcp` 只表示既非 TCP 也非 UDP。
- **配置约束。** 中继自身的出站由 `self_process_ids` 闸放行；没有配置 self-PID 时，无进程
  选择器的 block 规则会把中继上游 UDP 流量一起拦。
- **验证入口。** `wfprobe udp-rules` 是正式套件；`udp-listen` / `udp-relay` / `udp-client` 只用于手工排查。

### 7.12 MAC 帧层（forwarded）的实现事实（B4a 实测）

- **层与字段。** 入向 `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE`（实测 layer id 58）、出向
  `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`（59）。两层的 `inFixedValues` 只有媒体类型 / 接口 /
  接口类型 / 接口索引 / NDIS 端口 / L2 标志 / 分区，没有 MAC、EtherType、IP 或端口；全部从
  `layer_data`（`NET_BUFFER_LIST*`，数据自以太网头开始）自己解析。代码用常量注册，不写死 58/59。
- **block 必须由终止型 callout 给出。** `FWP_ACTION_CALLOUT_INSPECTION` 只能观察、不能改动作；
  要让 `FWP_ACTION_BLOCK` 真正丢帧，filter 必须是 `FWP_ACTION_CALLOUT_TERMINATING`。子层用
  `FWPM_SUBLAYER_UNIVERSAL`。**MAC_FRAME_NATIVE 层当前没有 Windows 防火墙 filter**：防火墙仍在
  ALE 层，§7.9 的实测只适用于 ALE，不能把那里的“更高权重子层先仲裁”搬过来；终止型 L2 filter
  只对同层里权重更低的第三方 L2 filter 有优先权，多驱动共存（WinpkFilter 等）需要 B4b 实测。
- **丢弃即超时，不注入 RST。** block 生效时客户端看到 SYN 重传后超时/连接失败；驱动不伪造
  回包。F2 / F2i 用例按这个口径断言。
- **防环要用 NBL，不是 metadata。** `FwpsQueryPacketInjectionState0` 的第二个参数是
  `const NET_BUFFER_LIST*`；`FWPS_PACKET_INJECTED_BY_SELF` / `..._PREVIOUSLY_..._BY_SELF` 直接
  permit。注入句柄用 `FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_L2, &h)` 创建。
- **非 IP 帧不参与规则。** ARP / LLDP 等既没有地址也没有端口，纳入规则会让一条 block 规则
  直接切断 L2 邻接；驱动对它们一律 permit 并单独计数（`non_ip`）。解析失败同理（`malformed`）。
- **方向语义。** 入向 client=源 / remote=目的 / remote_port=目的端口；出向 client=目的 /
  remote=源 / remote_port=源端口。`ingress_adapter_id` 就是 ifIndex，与用户态
  `NetworkInterface.GetIPv4Properties().Index` 同一编号空间。
- **NBL 取数要用返回值。** `NdisGetDataBuffer` 在数据连续时返回原指针并忽略调用方缓冲区；
  继续用缓冲区会读到未初始化内存（B3 排查结论）。VLAN 0x8100/0x88A8、IPv4 IHL、IPv6 扩展头
  都要按实际 L2 头长度偏移。
- **验证入口。** `wfprobe forwarded-rules`（F1–F7 + F1i/F2i）；host 既有 `rules` / `gates` /
  `selftest` 与 `userland-e2e` 在 L2 callout 常驻下仍全绿。block 用例（F2/F4）用固定源端口 +
  端口收窄规则只命中测试流，套件级看门狗在进程被杀时恢复空 permit 表。
- **已知风险（B4b/B6 复核）。** 两层 L2 callout 与 host 的 ALE callout 在同一驱动里注册：L2 注册失败
  会让整个驱动加载失败（不是 host fail-open）。B4b/B6 需要决定是否把 host 路径改成“L2 失败不影响
  host”，本批不改结构。控制器退出时会把 forwarded 表恢复成空 permit 表并回读校验，但异常退出 /
  进程被杀不保证恢复——那是套件级看门狗与运维脚本的职责。

### 7.13 Forwarded TCP 数据面（B4b 实测）

- **控制面 ABI v8 与两套配置（forwarded 布局沿用 v5/v6/v7 前缀）。** forwarded 数据面保留 v5/v6 的 probe_forwarded_config（enabled /
  listener_port / family / listener_address=L，28 字节）作为 legacy 单 listener 形式；v7 新增
  probe_forwarded_ingress（28 字节）与 probe_forwarded_ingress_config（16 字节头 + 0..16 条记录），
  IOCTL 0x80F/0x810，按 ifindex 给出各自的 L/端口。驱动允许同一 ifindex 有多条记录（同一端口
  多个 L，夹具/回归用）；产品 WfpController 每个 ifindex 只取一个 L。没有精确 ifindex 时退回
  0xFFFFFFFF 通配记录，两者都没有则 fail-closed。

  关闭 config 或映射变化会清空 redirect 表；换规则 blob 不清表，旧表项保持旧 generation / upstream。
  probe_forwarded_stats 的 B4b 尾部是 redirect 计数 + redirect_multi_nb、inject_complete_calls、
  inject_complete_failed；B5 追加 13 个 UDP 计数后扩到 400 字节（ABI v6），B6b 再追加 7 个 u64
  （456 字节，ABI v7；448 字节输出仍兼容），见 §7.15。


- **原帧消费。** 入口 DNAT 与回程 SNAT 都返回 FWP_ACTION_BLOCK，只注入改写副本。B3 的加法注入
  （原帧 permit + 注入副本）没有进入正式驱动。实测：客户端连接成功、监听端只接住一次、注入帧全部
  被 FwpsQueryPacketInjectionState0 识别为 self_injected。
- **redirect 表。** 入口 key 是 (ingress, family, protocol, client, server)；回程精确 key 是
  (family, protocol, L, listener_port, client, server)，另有候选 key (family, protocol, L,
  listener_port, client)。容量 4096，SYN 建表，FIN/RST 标记 closing，超时惰性回收。回程帧只带
  (L, listener_port, client)，候选必须唯一；多条时 block 原帧并计 redirect_return_ambiguous，
  不猜 server。新表项优先选同入口下候选空闲的 L，全部占用时替换旧表项（客户端端口复用）。

- **一个 NBL 可以有多个 NET_BUFFER。** MAC 层每个 NET_BUFFER 是一帧；B4b 返工的丢包根因就是只处理
  NET_BUFFER_LIST_FIRST_NB。回程必须逐 NET_BUFFER 查表/改写/注入，入口 DNAT 也逐帧注入；原 NBL 整体
  block，计数按 NBL 记（redirected == ingress + return 不被多 NB 撑破），另用 redirect_multi_nb 观察。
  实测一个回程 NBL 可带 7 个 NET_BUFFER（1448B + 376B 等），只处理首帧会让接收端停在 63712B。
- **校验和按实际偏移重算。** IPv4 头校验和 + TCP 校验和（IPv6 用伪头）；VLAN、IPv4 option、IPv6
  扩展头都用解析出的 network_offset / transport_offset，不假设 14 字节以太网头。
- **注入 MDL 必须用 NdisAllocateMdl。** NdisHandle 传 NBL 池句柄，并在调用前显式拒绝 null 池
  （第一版只在注释里说明，PREfast 仍报 C6387；gate 首次失败即此告警）；实测 IoAllocateMdl 创建的
  MDL 经 FwpsInjectMacReceiveAsync0 后会被本机栈丢弃（SYN 有去无回）。
- **回程 MAC 还原。** 表项记录入口帧的源 MAC（client）与目的 MAC（local）。回程注入的目的 MAC =
  client_mac、源 MAC = local_mac；未命中回程表的 listener 端口包按普通本机流量继续走规则匹配。
- **失败关闭。** 无 config/L → redirect_no_config，非 SYN 且无表项 → redirect_no_state，
  表满 → redirect_capacity，非 TCP → redirect_non_tcp，注入失败 → redirect_inject_failed；
  回程注入失败 → redirect_return_failed。以上都 block 原帧并计数，没有 fail-open。
- **用户态接缝。** IRedirectContextSource 让 RelayServer 先查 forwarded redirect 表、再查 host
  SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT；forwarded 命中时按 upstream_id 从
  ForwardedUpstreams 选 SOCKS5，缺表项直接拒绝，不回退直连。WfpController 的顺序是
  bind listener → forwarded config(disabled) → blob → 回读 → config(enabled)；退出时 disabled + 空 blob。
- **验证入口。** wfprobe forwarded-tcp --phase core|upstreams|fail|multi|noingress|hotswap（--restore-only 兜底），
  外部 Linux 客户端发起连接；默认要求客户端结束后连 verify 端口（listener+1000）回报实收字节、
  SHA-256 与 TCP ACK 字节数，套件按 client_port 配对断言，缺回报/内容不符即 FAIL（--no-verify 仅手工）。
  multi 用 Internal + External 两个入口、同一入口两个 L，客户端从同一源端口并发访问两个目的；
  hotswap 在两条连接之间替换 blob，断言旧表项 generation/upstream 不变、新表项使用新规则，且旧连接
  在换表后继续收发；noingress 只配置一个入口，另一入口命中 redirect_no_ingress。

  产品 E2E 由 WinForward.Controller run 驱动。实测计数与恒等式见 src/driver/README.md 的 B4b/B6b 段。
  forwarded-rules 的 F6 不再把 redirect 当作非法 blob。
- **边界。** 多入口/多 L 已 E2E（Internal + External、同一入口两个 L）；同一 (L, listener_port,
  client) 的并发多目的受 L 数量限制，L 用尽时替换旧表项（单 L 退化为一个客户端端口一个目的；
  旧目的在途回程会按新表项重写，不是 fail-closed）。
  UDP 数据面见 §7.14；表满路径用生产 4096 真满验证；LWF 对注入帧的可见性仍未测。
  一个 NBL 内的 NET_BUFFER 按同一 listener 回程流处理，混合流不在本版支持范围。


### 7.14 Forwarded UDP 数据面（B5 实测）

- **表与生命周期。** UDP 复用 TCP 的 redirect 表框架，但 key 增加 protocol：入口
  (ingress, family, protocol, client, server)、回程精确 key (family, protocol, L, listener_port,
  client, server) 与候选 key (family, protocol, L, listener_port, client)。同一 listener 端口上的
  TCP/UDP 表项不能互串，同一端口上的多个 L 也能区分。TCP/UDP 共用容量 4096；UDP 首个 datagram 建表，
  状态直接 established，60s 无活动回收；TCP 的三段超时不变。回收是惰性的，过期后下一个 datagram
  重建表项。新表项优先选候选空闲的 L；未配置入口且无通配记录 → block + udp_no_ingress。
- **入口 DNAT。** forwarded config enabled 时，首片（offset=0，带端口）按**完整规则（含端口）**匹配：
  命中 redirect 才 block + udp_fragmented，并把 `(入口, 族, 协议, 源/目的地址, IPv4 ID/IPv6 fragment ID)`
  记入 256 项、2s 的跟随表；非首片没有端口，查跟随表命中才 block + udp_fragmented，否则按普通帧
  继续。host/其他端口的分片不会被误伤；非分片帧按规则匹配。
  非分片帧查/建表，目的地址/端口改为 (L, listener_port)，重算 IPv4 头与 UDP 校验和（IPv4/IPv6
  都重算），逐 NET_BUFFER 用 FwpsInjectMacReceiveAsync0 注入，原帧 block。
- **回程 SNAT。** 出向 UDP 帧源 (L, listener_port) 命中入口映射时按 (family, protocol=17, L,
  listener_port, client) 查候选；命中则源地址/端口改回原始服务器、目的 MAC=client_mac、源 MAC=
  local_mac，重算 UDP 校验和，FwpsInjectMacSendAsync0 注入入口网卡，原 NBL block。候选全部未命中
  时 permit（listener 回客户端的普通本机流量），并计 udp_return_miss；候选不唯一 → block +
  udp_return_ambiguous；命中但注入失败 → block + udp_return_failed。混合 NBL 的未命中帧随原 NBL
  丢弃，进 `udp_return_miss_blocked`（独立诊断，不参与 permitted 恒等式）。


- **失败关闭。** 无 config/L → udp_no_config，表满 → udp_capacity，**forwarded redirect 规则地址空间
  内**的分片 → udp_fragmented（首片完整匹配 redirect 或非首片命中跟随表），注入失败 →
  udp_inject_failed；入口失败统一计 udp_blocked 并 block 原帧。UDP 无 RST，失败是静默丢包。
  分片 block 不建 redirect 表，因此 matched_redirect 恒等式带 udp_fragmented 项。
  恒等式见 src/driver/README.md 的 B5 段与 probe_ioctl.h 注释。
- **ABI。** probe_forwarded_stats 扩到 400 字节（UDP 计数 udp_created/ingress/return/blocked/
  return_failed/return_miss/no_config/no_state/capacity/fragmented/inject_failed/expired/multi_nb）。
  v7 再追加 7 个 u64：redirect_return_ambiguous、udp_return_ambiguous、redirect_no_ingress、
  udp_no_ingress、redirect_multi_l、udp_multi_l、udp_return_miss_blocked（456 字节）；v6 的 400 字节
  前缀不变，只给 448 字节输出时写 v7 前缀。见 §7.15。


  probe_forwarded_redirect_query 追加 listener_address（偏移 24，24 → 40 字节；旧 24 字节输入按
  任意 L 处理）；查询输出追加 listener_address 与 generation（64 → 88 字节，version=2；64 字节
  输出仍写 v1）。旧用户态必须在同一批升级。

- **用户态。** ListenerSet 支持在一组本机地址（L）上绑定同一端口的 TCP/UDP socket；每个 L 的
  UDP socket 独立收包，回程从收到 datagram 的 L 发出。ForwardedRedirectContextSource 查询带
  (L, 端口)；ForwardedUdpRelay 关联 key 含 (client, server, upstream_id, listener 端点)，空闲 30s
  回收、容量 1024，缺 upstream/上游失败一律丢包不回退直连。WfpController 按 forwarded 规则的
  ingress 解析 L（显式 ForwardedListenerAddress 可覆盖），先 disabled 再 enabled，退出 disabled +
  空 blob；UDP relay 在 config 启用后启动。

- **验证入口。** wfprobe forwarded-udp --phase core|socks5|fail|fragment|fragment-host|capacity|reject（--restore-only
  兜底）；core 直连 Linux UDP echo 并绑定原始目的地 decoy 证明原帧未 permit，socks5 用进程内两个假
  SOCKS5 UDP 上游验证 upstream_id 选择与关联复用，fail 验证无 config 时静默丢包，fragment 用
  raw 显式 9 片 / 1280 MTU 9 片验证 forwarded 空间内 fail closed，fragment-host 验证 host 分片
  不被误伤，capacity 用生产 4096 真满，reject 验证 ASSOCIATE 失败静默丢包。外部 Linux 客户端经 verify TCP 端口回报实收字节/SHA/源地址；--expect-created 2
  配合客户端 65s 间隔可验证 60s 超时回收。
- **实测（2026-10-23）。** core 10/10；socks5 两客户端各 10/10、两个上游各只 1 次 ASSOCIATE；
  fail 5 条 datagram 全部 udp_no_config；60s 超时后 udp_created=2/udp_expired=1；forwarded-tcp core、
  forwarded-rules F1/F2/F3/F6、host udp-rules/rules/selftest/gates 全 PASS；产品 E2E
  （WinForward.Controller + Linux fake SOCKS5 UDP 40500）5/5、源地址恢复为原始服务器，产品日志
  `udp-relay stop in=6 relayed=5 back=5 associations=1 failures=0 drops=1`（drops 是直接打到 listener 的
  query-miss datagram）。
- **边界。** 多入口/多 L 与 TCP 共用同一套映射，UDP core 在单 L 下回归；UDP 的多 L 并发多目的
  未单独 E2E（复用同一表与回程逻辑；UDP 热替换也未单独 E2E）。分片闸只覆盖 forwarded redirect 规则地址空间：
  首片按完整规则（含端口）匹配并写 (入口, 族, 协议, 源/目的地址, IP ID) 跟随表，非首片查表 block；
  host/其他端口分片按原路径。非首片先于首片到达时无法跟随（放行），TCP 分片与出向分片不在闸内。
  native IPv6 UDP 代码路径已实现；native IPv6 的 UDP/TCP happy path 在 §7.17 完成入口网卡 E2E
  （本节的 1280 MTU 9 片仍是分片闸证据）；host UDP 的 listener/中继不在本仓库。

### 7.15 B6b 多入口、多 L、回程 key 与热替换（2026-10-23 实测）

- **映射与选择。** `probe_forwarded_ingress_config` 按 ifindex 给出 L/端口；同一 ifindex 可重复
  （同端口多 L）。`plan_forwarded_inbound` 在锁内按 ingress/family 选记录，优先选回程候选空闲的 L，
  全部占用时退回第一条并由 create 替换旧表项。未配置入口 fail-closed（redirect_no_ingress /
  udp_no_ingress），不再落 fallback permit。
- **回程。** 表项保存 L；回程精确索引含 (L, listener_port, client, server)，帧路径查候选索引；
  候选不唯一时 block 并计 ambiguous，不猜 server。
- **用户态。** ListenerSet 支持在一组本机地址上绑定同一端口（每 L 一个 TCP/UDP socket）；
  ForwardedRedirectContextSource 查询带 L；ForwardedUdpRelay 关联 key 增加 listener 端点并从收到
  datagram 的 L 发回程。WfpController 按 forwarded 规则的 ingress 自动解析 L（显式
  ForwardedListenerAddress 可覆盖；当前每个 ifindex 只解析一个 L，同 ifindex 多 L 由驱动接口/夹具覆盖），启动时先 disabled 再 enabled，退出 disabled + 空 blob。
- **热替换。** 只换规则 blob，旧表项保留旧 generation/upstream；新流按新规则。实测 generation
  32 → 33（复核复跑 57 → 58），旧表项保持旧值，旧连接 upstream 0、新连接 upstream 1。
- **观测。** StateCommand 输出 `forwarded`（installed/generation/rules/fallback/enabled/ingresses）、
  `forwarded-ingress`（ifindex/family/listener）与全部 `forwarded-stat` 行（含 ambiguous/no_ingress/
  multi_l、udp_return_miss_blocked）。WfpDeviceClient 读写 ingress 配置与 v7 查询/统计。

- **实测。** TCP `--phase multi`：Internal + External 两个入口、同一入口两个 L，客户端同一源端口
  并发 4 条连接全部 2048B 双向一致，回程按 L 还原到两个原始目的；`redirect_multi_l=1`、
  `return_ambiguous=0`、`no_ingress=0`。TCP `--phase hotswap` 见上。UDP core 10/10 回归通过。
  第二轮新增 `noingress` 正测、任意 L 查询歧义、旧尺寸 IOCTL（vm-check 86/0）与换表后旧连接继续收发。

  host rules/selftest/gates/udp-rules 与 forwarded-rules F1/F2/F3/F6 全 PASS。

### 7.16 B6c 门禁、成本与验收（2026-10-23）

- **独立 Linux 门禁。** `tools/linux/forwarded_gate.py` 在 Linux 客户端侧自动 prepare/cleanup
  （`.99` 别名、40400/40600/41400 等端口放行），跑 TCP/UDP core、IPv4 9 片、permit-first、
  host 同 remote 和产品 reject；`--include-v6` 追加五个 native IPv6 阶段（见 §7.17）；CI 禁止
  `--no-verify`。默认日志在 `verification/forwarded-gate/`，IPv6 证据见 §7.17 的任务目录。
  IPv4 的 hotswap/noingress/StateCommand、IPv6 9 片、A5 不在该门禁内，原因见 `tools/linux/forwarded_gate.md`。
- **fail 阶段边界。** B6c 验收时 ABI v7 下 config disabled 时 WFP filter 不再进入 classify，
  B6a 的 `udp_no_config`/`blocked` 断言不成立；当前控制面已升到 v8，该 fail 阶段未在 v8 复验；
  `--include-fail` 保留诊断，不作为门禁通过条件。这是 B6c 发现，未改数据面语义。
- **Windows gate。** `gate.ps1` 保持 host/WFP/forwarded-rules/池检查；forwarded 数据面需要外部
  Linux 客户端，未塞进 Windows-only gate，由 Linux 门禁单独承担，失败即 Linux 门禁失败。
- **A5 成本。** `verification/a5-cost.md`：forwarded TCP 1MiB 0.8–4.5 MB/s、UDP 200×64B burst
  209pps；host 对照 12.9 MB/s、1590pps。数字包含 C# suite 中继、VM vSwitch、单次采样噪声，
  只作量级参考，不设 kill criteria；未用 iperf3/内核 sink。
- **D7。** 见 `.trellis/tasks/archive/2026-10/10-23-fwd-b6-hardening/d7-decision.md`：WFP L2 保留为 kernel
  edition 候选，不正式替代 NDISAPI/WinpkFilter；采用条件（独立性能、LWF、IPv6 多入口
  （数据面见 §7.17，产品控制器 v6 L 自动解析仍缺）、UDP 多 L/热替换、产品多 L、Runtime
  集成、稳定性）全部满足后再评估。
- **交付边界。** B6b 收口时驱动 ABI v7，SHA-256 `EA271858D8A3AD12D7263B09A558C32F2035CAE27611A4A852820075D59C0A95`；
  当前控制面已升到 v8（block sink 批次），forwarded 数据面未改。已知边界统一见任务 `verification.md` §15。

### 7.17 Forwarded native IPv6 数据面 E2E（2026-10-25，ABI v8）

- **环境与地址。** WinLTSC Internal（ifIndex=5）主链路本地 L1 只读发现；prepare
  `-WithIpv6` 添加 Internal 第二 L `fe80::98`、Internal target `fe80::99`、External target
  `fe80::199`，并加 `B6C-ENV-V6-*` 端口放行；`-Remove` 不论开关都删除这些地址与规则。
  Linux 只用 eth0/eth1 现有链路本地地址，客户端 target 带 `%eth0`/`%eth1` scope。
  target 与 L 必须是不同地址，回程 SNAT 写错才会在客户端暴露。
- **夹具。** `SocketHelpers.WithScope` / `SameAddress` 统一链路本地 scope 与无 scope 比较；
  TCP 的 core/upstreams/fail 按地址族绑定 `IPv6Any`（DualMode=false），multi 的每个 L 补
  scope；UDP 的 listener/any/decoy/backend 全部按各自地址族分离（v6 listener + v4 backend
  不能共用 `Any`）。假 SOCKS5 上游记录 `V6Connections` / `V6Datagrams`，显式断言 TCP
  `atyp=4` CONNECT 与 UDP `atyp=4` datagram。
- **实测。** `tools/linux/forwarded_gate.py --include-v6` ALL PASS（1m20s），v4 默认门禁
  ALL PASS（50s）：
  - `tcp-core6`：64 KiB 双向一致（客户端 acked=65537），redirect 表 server=`fe80::99`、L=L1，
    ingress/return 恒等式通过；
  - `tcp-upstreams6`：40250/40251 分别命中 upstream 0/1，两个假上游各 1 次 `atyp=4`
    CONNECT，两会话各 2048B 双向；
  - `udp-core6`：10/10、源地址 `fe80::99:40250`、decoy 无数据、SHA 一致；
  - `udp-socks5-6`：两个目的各 1 次 UDP ASSOCIATE、各 10 个 `atyp=4` datagram、回程源地址正确；
  - `tcp-multi6`：4 条连接（Internal+External 跨入口、同入口两 L），`redirect_multi_l=1`、
    `return_ambiguous=0`、`no_ingress=0`。
- **负向与恢复。** target 改成不存在的 `fe80::dead` 时门禁在 tcp-core6 客户端超时退出 1；
  `--no-verify` 被 argparse 拒绝（exit 2）。`finally` 的 `prepare -Remove` 后
  `residual_v6=none b6c_rules=none`。
- **剩余边界。** UDP 多 L / 多入口、IPv6 热替换、atomic fragment、逐片 raw 构造未 E2E；
  `WfpController.ResolveForwardedListenerPlan` 仍只解析 IPv4 L，v6 L 由 wfprobe 直接下发
  ingress。证据：`.trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-final/`（最终脚本
  复跑；实现阶段的日志保留在 `verification/full-v6/`）、`verification.md`。
