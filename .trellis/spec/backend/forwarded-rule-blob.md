# Forwarded 规则 blob（v1）

> 适用范围：`src/common/forwarded_blob.h`、`src/common/forwarded_matcher.h` 与
> `src/userland/WinForward.Userland/Forwarded/`。
> 拟定：2026-10-23。对应任务：`.trellis/tasks/10-23-fwd-b1-forwarded-blob`。

这份文档是 Forwarded 规则层的跨语言契约。C# 侧负责把规则编译成字节流，内核侧只解码与匹配；
两侧共享的是字节布局与语义，不是标识符。任何布局改动都必须提升 `forwarded_blob_version`，
并同时更新 C++ 的 `static_assert`、C# 的布局测试与本文档。

## 1. 定位与边界

Forwarded 规则回答的问题是「从哪个入口网卡进来、客户端是谁、要去哪里」的流量交给哪个
SOCKS5 上游。它与 host 规则是两份独立 ABI：

| 维度 | host `wf_rule_blob` | forwarded `wf_forwarded_blob` |
|---|---|---|
| magic | `"WRB1"`（`0x31425257`） | `"WFB1"`（`0x31424657`） |
| 版本 | 1 | 1 |
| 匹配维度 | 进程路径、目的 CIDR、端口、协议、族 | 入口适配器、客户端 CIDR、目的 CIDR、端口、协议、族 |
| 动作载荷 | 无 | `upstream_id` |
| 共享定义 | —— | `network_prefix`、`rule_action`、协议/族常量 |

两者不共用 magic/version，也不共用 header 与 rule 结构：任一解码器都不会接受对方的 blob。
共享的定义只有 `src/common/rule_blob.h` 里的 `network_prefix`、`rule_action`、
`rule_protocol_*`、`rule_family_*` 与 `rule_pool_index_any`。**不要复制这些常量。**

本层不包含：JSON 配置解析、适配器 stable ID → 接口 id 的解析、驱动 classify/redirect、
IOCTL 与 redirect 表。这些属于 B2/B4/B5。

## 2. ABI

所有多字节字段为主机字节序（部署目标只有 Windows x64 小端）；所有段起点 4 字节对齐；
`total_size` 是完整缓冲区长度。

### 2.1 header（40 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | `magic` | `0x31424657`（内存字节 `W F B 1`） |
| 4 | u16 | `version` | 1 |
| 6 | u16 | `header_size` | 40，必须精确等于结构体尺寸 |
| 8 | u32 | `total_size` | 必须等于缓冲区长度 |
| 12 | u32 | `rule_count` | |
| 16 | u32 | `fallback_action` | 只允许 permit(0) / block(1) |
| 20 | u32 | `rules_offset` | 4 字节对齐，≥ `header_size` |
| 24 | u32 | `network_pool_offset` | 4 字节对齐，≥ `header_size` |
| 28 | u32 | `network_pool_size` | 必须是 20 的倍数 |
| 32 | u32 | `reserved` | 必须为 0 |
| 36 | u32 | `reserved2` | 必须为 0 |

### 2.2 rule（24 字节，4 字节对齐）

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u32 | `ingress_adapter_id` | `0xFFFFFFFF` = 任意入口 |
| 4 | u32 | `client_network_index` | `0xFFFFFFFF` = 不限；否则是网段池字节偏移 |
| 8 | u32 | `remote_network_index` | 同上 |
| 12 | u16 | `remote_port_start` | 含 |
| 14 | u16 | `remote_port_end` | 含；开始 > 结束即非法 |
| 16 | u32 | `upstream_id` | `0xFFFFFFFF` = 无；redirect 必须有，非 redirect 必须无 |
| 20 | u8 | `protocol` | 0 任意 / 6 TCP / 17 UDP |
| 21 | u8 | `family` | 0 任意 / 4 IPv4 / 6 IPv6 |
| 22 | u8 | `action` | permit(0) / block(1) / redirect(2) |
| 23 | u8 | `reserved` | 必须为 0 |

哨兵约定：

- `ingress_adapter_id = 0xFFFFFFFF` 表示任意入口；**0 是合法的具体适配器编号**，不能用它表达
  「任意」。
- `upstream_id = 0xFFFFFFFF` 表示无上游，只允许出现在非 redirect 规则上。
- 网段索引的任意哨兵复用 host 的 `rule_pool_index_any`（同样是 `0xFFFFFFFF`）。

### 2.3 network_pool

复用 `rule_blob.h` 的 `network_prefix`（20 字节）：

| 偏移 | 类型 | 字段 | 说明 |
|---:|---|---|---|
| 0 | u8 | `prefix_length` | IPv4 ≤ 32，IPv6 ≤ 128 |
| 1 | u8 | `family` | 只允许 4 / 6（网段池里不允许 any） |
| 2 | u8[2] | `reserved` | 必须为 0 |
| 4 | u8[16] | `network` | IPv4 只用前 4 字节，其余补零 |

池内记录按 20 字节连续排布，规则里的索引是池内字节偏移。C# 侧的
`NetworkPoolBuilder` 按 `(family, prefix_length, address)` 去重、保留首次出现顺序。

### 2.4 段布局

```text
[0, 40)                      header
[rules_offset, ...)          rule_count × 24
[network_pool_offset, ...)   network_pool_size（20 的倍数）
```

段之间允许 4 字节对齐填充；两段互不重叠。空规则表时 `rules_offset = network_pool_offset =
40`、两段长度都为 0。

## 3. C++ 解码

`try_open_forwarded_blob(data, size, out)`：

- 纯函数、无分配、无异常、无内核依赖；`data` 必须是 4 字节对齐的调用方缓冲区。
- 失败返回 `false`，**`out` 保持原值不变**；成功才写入视图。
- 视图里的指针全部指向 `data` 指向的同一块缓冲区，不拥有内存。

校验顺序（与 host `try_open_blob` 对齐）：

1. 空指针 → 头部对齐；
2. 缓冲区短于 40 字节；
3. `magic` / `version` / `header_size`（必须精确等于 40）；
4. `total_size == size`；
5. `fallback_action` 只允许 permit / block；
6. 头部 `reserved` / `reserved2` 为 0；
7. 段起点 4 字节对齐、不小于 40、不越过 `total_size`；网段池长度不越界；
8. 规则数组不越界（`rule_count ≤ (total_size - rules_offset) / 24`）；
9. 两段不重叠；
10. 网段池长度是 20 的倍数，并逐条校验 `network_prefix`（含未被规则引用的条目）；
11. 每条规则：`reserved` → 已知 `action` / `protocol` / `family` → 端口区间 →
    `upstream_id` 与 `action` 的组合 → 两个网段索引的越界/未对齐/族一致性；
12. 规则的两个网段都具体时，网段族必须相同（`family = any` 也不放宽这一条）。

注意：解码端**不做**规则数/网段池条目数的容量上限检查 —— 上限只属于编译期（§5.3），
内核按缓冲区长度判界，避免把「生产者限制」和「消费端内存安全」混为一谈。

## 4. 匹配语义

`match_forwarded(view, connection)` 是纯函数；它假定 `view` 来自成功的
`try_open_forwarded_blob`，因此不再判界。

连接事实：

```text
ingress_adapter_id, client_address[16], remote_address[16],
remote_port, protocol, family
```

匹配顺序（全部 AND；顺序影响性能不影响语义）：

1. `ingress_adapter_id`：任意或等于连接；
2. `family`：任意或等于连接；
3. `protocol`：任意或等于连接；
4. 远端端口闭区间；`0..0xFFFF` 视为任意；
5. `remote_network_index`：查池、族与连接一致、按前缀比较 `remote_address`；
6. `client_network_index`：查池、族与连接一致、按前缀比较 `client_address`。

规则数组顺序即优先级，**首个命中即返回**：

| 结果字段 | 命中 | 未命中 |
|---|---|---|
| `matched` | `true` | `false` |
| `action` | 规则动作 | `view.fallback_action` |
| `rule_index` | 规则下标 | `rule_pool_index_any` |
| `upstream_id` | 规则的 `upstream_id` | `forwarded_upstream_none` |

`upstream_id` 只在 `action == redirect` 时有意义；调用方一律以 `matched` 为准。
命中非 redirect 规则时 `upstream_id` 必然是 `0xFFFFFFFF`（解码已保证）。

## 5. 用户态编译

命名空间 `WinForward.Userland.Forwarded`，模型为：

- `ForwardedPolicyRule`：`Name`、`IngressAdapterId`（`uint?`）、`ClientNetwork`、
  `RemoteNetwork`（`RemoteNetwork?`）、`Family`、`Protocol`、`RemotePorts`、`Action`、
  `UpstreamId`（`uint?`）；
- `ForwardedPolicyTable`：有序规则表 + `FallbackAction`；
- `ForwardedBlobCompiler.Compile(ForwardedPolicyTable)`：校验 + 布局 + 写字节。

`null` 的适配器/上游分别编译成 `0xFFFFFFFF` 哨兵；其余用 host 的值对象
（`RemoteNetwork`、`PortRange`）与枚举（`RuleAction`、`RuleProtocol`、`RuleAddressFamily`），
不复制一套新语义。

### 5.1 编译期校验

顺序：兜底动作 → 规则数上限 → 逐条规则（动作/协议/族 → 端口区间 → 适配器哨兵 →
上游组合 → 网段族一致性）→ 网段池上限。诊断消息带规则序号（`规则 #N：...`），
因为内核侧只会整块拒绝，用户态是唯一能给可操作信息的时刻。

1. `FallbackAction` 只允许 Permit / Block；Redirect 与未知值都拒绝。
2. 每条规则的 `Action` 只允许 Permit / Block / Redirect。
3. `Redirect` 必须设置 `UpstreamId`，且不能等于 `0xFFFFFFFF`；非 Redirect 不得设置
   `UpstreamId`。
4. `IngressAdapterId` 若设置，不能等于 `0xFFFFFFFF`（该值是「任意入口」哨兵）。
5. `Family` 具体时，`ClientNetwork` / `RemoteNetwork` 的族必须与之一致；两个网段都具体
   且族不同时拒绝（即使 `Family = Any`）。
6. `RemotePorts.Begin <= End`。
7. 规则数 ≤ `MaxRules`；去重后的网段池条目数 ≤ `MaxNetworkPoolEntries`。
8. `reserved` 字段全部写 0。

### 5.2 段布局与去重

段序与 host 编译器一致：header → rules → network_pool。

```text
rules_offset        = 40
network_pool_offset = AlignUp(40 + rule_count * 24, 4)
total_size          = network_pool_offset + network_entry_count * 20
```

网段按 `(family, prefix_length, body)` 去重、按首次出现顺序排布；IPv4 地址的主机位在
`RemoteNetwork` 构造时清零，IPv4 的后 12 字节补零。

### 5.3 容量上限

| 常量 | 值 | 含义 |
|---|---:|---|
| `ForwardedPolicyLimits.MaxRules` | 4096 | 规则条数上限（含） |
| `ForwardedPolicyLimits.MaxNetworkPoolEntries` | 4096 | 去重后网段池条目上限（含） |

上限只在编译期强制。内核解码不做条目数上限检查（§3）。

## 6. 两侧锁死与测试

- C++：`src/common/forwarded_blob.h` 的 `static_assert` 锁尺寸与逐字段偏移；
  `tests/host/forwarded_blob_test.cpp` 锁解码的接受/拒绝路径，`forwarded_matcher_test.cpp`
  锁匹配语义（含 IPv6、族一致性、首命中与 fallback）。
- C#：`ForwardedLayoutTests` 锁 `Marshal.SizeOf` / `Unsafe.SizeOf` / `Pack = 4` /
  逐字段偏移与黄金字节；`ForwardedCompilerTests` 锁字段、段布局、去重顺序、容量上限与
  每条拒绝路径。
- 差分测试（C# 产 blob、C++ 消费）在 B1 未接入：现有 `manifest.txt` 记录类型（`connect`
  行）绑定 host 的进程路径语义与精确用例数，扩展 forwarded 段需要改 host 夹具格式与
  `differential_test.cpp`，超出 B1 的「不改既有夹具」边界。留到 B2/B4 与配置接线一起做。

## 7. 与后续批次的接缝

- **B2**：JSON `forwarded` 段解析成 `ForwardedPolicyTable`；`upstream_id` 与上游表的映射
  在同一次下发中完成。
- **B4/B5**：驱动用 `ingress_adapter_id`（接口标识）与连接元组调用匹配器；命中 redirect
  时把 `upstream_id` 写进 redirect 表，交给用户态选 SOCKS5 上游。
- 适配器重建后旧 id 的规则不命中，按 fallback 处理（block 时失败关闭）；本层的 `0xFFFFFFFF` 哨兵语义
  不随入口解析方式变化。

## 8. 配置映射（B2）

暂定 JSON 的 forwarded 配置由 `Configuration/PolicyConfigurationDocument.cs` 解析（forwarded 与多上游
在 `PolicyConfigurationDocument.Forwarded.cs`）。链路是：

```text
JSON → ControllerConfiguration.Forwarded (ForwardedPolicyTable) → ForwardedBlobCompiler.Compile → wf_forwarded_blob
```

B2 只做到“解析 + 编译 + 报告”；把 blob 下发进驱动是 **B4a**（`ioctl_set_forwarded_blob`，ABI v4）。
B4b 起驱动执行 permit / block / **redirect** 三种动作：含 redirect 规则的 blob 正常安装，由 MAC 帧层
classify 建 redirect 表并做 DNAT；B4a 的 `STATUS_NOT_SUPPORTED` 整块拒绝已移除。
`ForwardedRuntimeSupport.EnsureExecutable` 保留为执行前校验缝，但当前不再按动作拒绝。

| JSON | 模型 | blob |
|---|---|---|
| `socks5Servers[].name` | `ControllerConfiguration.ForwardedUpstreams`（有序 id→endpoint）的 `Name` | 上游 id 的来源 |
| `socks5Servers[].host/port` | `Socks5Endpoint` | 不在 blob 里；B4b 用它建 SOCKS5 连接（按 `upstream_id` 选） |
| 旧 `socks5` | `ForwardedUpstreams` 的 `default` 项（`Upstreams["default"]` 供 target 查找） | 同上 |
| `forwarded.fallbackAction` | `ForwardedPolicyTable.FallbackAction` | header `fallback_action` |
| `rules[i].action` | `ForwardedPolicyRule.Action` | rule `action`（`redirect` 即包版 `proxy`） |
| `rules[i].target` | `ForwardedPolicyRule.UpstreamId` | rule `upstream_id` |
| `rules[i].adapterId/adapterName` | `IAdapterIdResolver` 的输入 → `ForwardedPolicyRule.IngressAdapterId` | rule `ingress_adapter_id` |
| `rules[i].clientCidr` | `ForwardedPolicyRule.ClientNetwork` | rule `client_network_index` → network_pool |
| `rules[i].remoteCidr` | `ForwardedPolicyRule.RemoteNetwork` | rule `remote_network_index` → network_pool |
| `rules[i].remotePorts` | `ForwardedPolicyRule.RemotePorts` | rule `remote_port_start/end` |
| `rules[i].protocol/family` | 同名枚举 | rule `protocol` / `family` |

约定：

- **上游 id**：`socks5Servers` 按数组顺序取 0..n-1；旧 `socks5`（如果有）以 `default` 追加在最后。
  `target` 先查命名上游，再查旧 `socks5`；名字比较大小写不敏感，`socks5Servers` 里再写 `default`
  与旧 `socks5` 同时存在视为冲突。这个编号是本次配置内的顺序，不跨配置稳定。
- **上游表**：解析结果同时维护 `ControllerConfiguration.ForwardedUpstreams`（`Id` → `Socks5Endpoint` 的有序表，
  `Id` 就是 blob 的 `upstream_id`）和 `Upstreams`（大小写不敏感的名字字典）。`Upstreams` 只用于 `target`
  查找；B4b 按 `upstream_id` 从 `ForwardedUpstreams` 取 endpoint，不依赖字典枚举顺序。
- **适配器**：`adapterId` / `adapterName` 可单独给，也可同时给；同时给时配置层分别调用
  `IAdapterIdResolver.TryResolve` 再比对，结果不一致即报错。没有 resolver 时带 adapter 选择器的
  forwarded 规则直接报错；解析结果 `0xFFFFFFFF` 是“任意入口”哨兵，不能当具体 id。
- **target**：`redirect` 必填且必须指向已存在的上游；`permit` / `block` 出现 `target` 直接报错，
  避免“看起来配了代理、实际不生效”。
- **process**：forwarded 规则没有宿主进程，出现 `process` 直接报错。
- **诊断**：配置层消息统一带 `forwarded.rules[i].<field>`，target / adapter 错误附带规则名；
  CIDR 族冲突、端口区间颠倒、`fallbackAction = redirect` 都在配置层提前拒绝。
- **控制器**：`WfpController.Start` 在 `ForwardedBlob` 非空时下发并回读校验（installed / 规则数 /
  fallback / total_size / generation），再输出 `controller-forwarded count=<rules> upstreams=<n> blob=<bytes>`
  与 `controller-forwarded-installed ...`；随后按「bind listener → forwarded config(disabled) →
  blob → 回读 → config(enabled)」启用数据面。v7 起 listener 映射按规则里出现的 ingress 逐个解析 L，
  用 `probe_forwarded_ingress_config` 下发。驱动接口支持同一 ifindex 多条记录（同端口多 L）；当前控制器每个 ifindex 只解析一个 L，`ListenerSet` 为解析出的每个 L 绑 TCP/UDP socket。没有 forwarded 段时下发空 permit 表并同样回读。

  退出/回滚时 disabled config（清 redirect 表）+ 空 permit 表并回读，异常退出不保证恢复。
  `CheckCommand` 只编译与报告，不再因 redirect 提前拒绝。
- **上限**：配置层不做规则数/网段池上限的重复判断，最终仍由 `ForwardedBlobCompiler`
  按 `ForwardedPolicyLimits` 拒绝。

## 9. B4b 数据面接缝

redirect 规则的 `upstream_id` 是用户态选 SOCKS5 的唯一依据；驱动不解释 upstream 的名字或端点。

- 驱动按 `upstream_id` 原样写进 redirect 表，`ioctl_query_forwarded_redirect` 回读；
- 用户态 `ForwardedRedirectContextSource` 用 accepted socket 的 (L, listener_port, client) 查询；

- `RelayServer` 按 `ForwardedUpstreams[upstream_id]` 取 endpoint，再走 SOCKS5 CONNECT；
  表项缺失时拒绝连接，不直连；
- ABI v7 的 `probe_forwarded_stats` 在 v6 400 字节前缀后追加 7 个 u64（回程歧义、未配置入口、
  多 L 选择、UDP 混合 NBL 丢弃），结构 456 字节；只给 448 字节输出时写 v7 前缀。
  `probe_forwarded_redirect_query` 追加 listener_address（24 → 40 字节），

  查询输出追加 listener_address/generation（64 → 88 字节，version=2）。v4 的 0–143 字段偏移不变，
  `last_status`/`abi_version` 仍在 392/396。B5 的 protocol 字段（偏移 22，0/6 = TCP、17 = UDP）不变，
  见 §10。

- `forwarded-rules` 的 F6 不再把 redirect 当作非法 blob；数据面验证入口是
  `wfprobe forwarded-tcp --phase core|upstreams|fail|multi|hotswap`；套件默认用 verify 端口
  （listener+1000）断言外部客户端实收字节/SHA-256/ACK，缺回报或内容不一致即 FAIL。multi 覆盖
  Internal + External 两个入口与同一入口两个 L；hotswap 覆盖旧表项保持旧 upstream/generation。


## 10. B5 UDP 映射与接缝

规则层不需要新字段：UDP 规则就是 `protocol = udp(17)`（或 `any`）的 redirect 规则，`upstream_id`
的含义与 TCP 相同。驱动在执行层按协议分派：

- `protocol=udp` 的 redirect 规则命中后，非分片首个 datagram 建表（没有 SYN），后续 datagram 查表；
  首片（带端口）按完整规则匹配，命中 redirect 才 block + `udp_fragmented` 并记录跟随表；非首片查
  `(入口, 族, 协议, 源/目的地址, IP ID)` 跟随表，命中才 block。跟随表 256 项、2s 轮转；host/其他
  端口的分片按原路径。表项 key 增加协议，TCP/UDP 共用 listener 端口也不会互串。UDP 空闲 60s 回收，
  容量与 TCP 共享 4096。
- 回程 UDP 帧按 listener 源端口 + 客户端地址/端口 + protocol=17 查表，再 SNAT 回原始服务器；
  用户态 listener 用**同一个 socket** `sendto` 客户端，驱动负责源地址还原。
- 用户态查询复用 `ioctl_query_forwarded_redirect`：`probe_forwarded_redirect_query.protocol=17`；
  输出 `probe_forwarded_redirect.protocol` 回填 17，其余字段（server/upstream/ingress/rule）不变。
- `forwarded-udp` 套件验证 core/socks5/fail/fragment/fragment-host/capacity/reject：core 额外绑定原始
  目的地 decoy；fragment 用 raw 显式 9 片（IPv4）或 1280 MTU 9 片（IPv6）验证 forwarded 空间内
  fail closed；fragment-host 验证同一 remote IP、不同端口的 host 分片仍按原路径重组；capacity 用
  生产 4096 真满（4097 个源端口）；reject 验证 ASSOCIATE 失败静默丢包。
- 产品 E2E 使用 WinForward.Controller + Linux fake SOCKS5 UDP：5/5，源地址恢复，产品日志
  `udp-relay stop in=6 relayed=5 back=5 associations=1 failures=0 drops=1`；未命中 forwarded 表的
  直接 datagram 计 drop，不直连。

映射表补充：

| JSON | 模型 | blob / 驱动 |
|---|---|---|
| `rules[i].protocol = "udp"` | `ForwardedPolicyRule.Protocol = Udp` | rule `protocol = 17` |
| 同一规则同时覆盖 TCP/UDP | `Protocol = Any` | rule `protocol = 0`；UDP 分支独立建表 |
| `target` / `upstream_id` | 与 TCP 相同 | 表项记录 `upstream_id`，用户态按它选 SOCKS5 UDP 上游 |
| UDP listener 端口 | 与 TCP listener 同端口号 | `probe_forwarded_config.listener_port` 同时用于两种协议 |
