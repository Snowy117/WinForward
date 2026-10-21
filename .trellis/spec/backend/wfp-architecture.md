# WFP 内核化 —— 架构与已定决策

> 本文件记录**已经拍板的决策**与由它推出的组件划分。未决事项集中放在 §6，讨论中的方案不写进决策表。
> 最后更新：2026-10-22。

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
| D4 | block 语义：TCP 接受 RST，UDP 接受静默丢包 | `FWP_ACTION_BLOCK` 让 TCP 客户端立即收到 RST；UDP 没有可用的 send 错误路径，应用侧 send 成功、对端收不到（已实测并确认）。不为这两种语义保留 packet 层 |
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
- **`wf_rule_blob`**：magic、版本、总长、规则数与各段偏移。
- 规则 blob 的字节布局、布局断言与解码/求值入口见 [`src/common/rule_blob.h`](../../../src/common/rule_blob.h) 与 [`src/common/matcher.h`](../../../src/common/matcher.h)。

两侧都有布局断言：C++ 侧 `static_assert`（尺寸、对齐、`offsetof`），C# 侧以布局一致性测试锁定。版本号变更即视为破坏性变更。

控制面 ABI（`probe_ioctl.h`）当前是 v3：`probe_event` 增加 `protocol`，`probe_stats` 增加 `udp_classify_calls` / `udp_permitted` / `udp_blocked` / `udp_redirected`；驱动与用户态必须同批升级。

---

## 6. 未决

| # | 事项 | 状态 |
|---|---|---|
| O1 | ~~仓库边界~~ —— 已由 D8 结案：自包含，用户态也在本仓库 | 已决 |
| O2 | IOCTL 协议的形状与版本化策略 | 未开始 |
| O3 | 无调试器时计数器与诊断的回灌通道 | 未开始 |
| O4 | 防循环各道闸的具体判据与顺序 | 未开始 |
| O5 | 进程选择器在内核侧的比较形式（DOS 路径 / NT 路径 / appId）与编译期规整规则 | 未开始 |
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

**正确做法**：把 block 规则编译成 `ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK` filter。这类 filter 只会增加拒绝、从不放行，因此在结构上就不可能让别处要拦的流量通过。只有规则表里存在 block 规则（或兜底动作为 block）时才创建。

注意 `FWP_V4_ADDR_AND_MASK` 的 `addr` 与 `mask` 是**主机字节序**（fwptypes.h 原文），写成网络序会得到 `FWP_E_INVALID_NET_MASK`。

**遗留的语义偏差**：filter 只看自身条件，不参与 blob 的「首个命中」顺序。若一条 block 规则的匹配集被更早的 permit 规则覆盖，filter 仍然会拦，与配置作者的本意相反。这类重叠必须在**编译期拒绝**，不许静默改变语义。
**失败路径的语义要写实话**：既然这一层不执行拦截，那么任何「失败就 BLOCK」的写法的实际效果都是**放行到真实目的地**（fail-open），而计数器还会记成 `blocked`——既没保护也不诚实。正确的做法是如实置 PERMIT 并单独计数（`redirect_failures`），让「重定向失败 = 直连」成为一个可观测的事实。要真的拒绝，必须落到 `ALE_AUTH_CONNECT` 的 BLOCK filter 上。

顺带一条同类教训：`fallback_action = block` 会为 TCP(6) 与 UDP(17) 各生成一条协议过滤器（IPv4/IPv6 每层一对），把同表里所有 TCP/UDP 的 permit / redirect 规则变成死规则，而 classify 仍按规则表报 `permitted`；这些过滤器只写 6/17，ICMP 等非 TCP/UDP 协议不受影响。这类「兜底与规则组合起来自相矛盾」的配置必须在编译期拒绝，驱动侧再拒绝一次。

### 7.10 进程选择器的字节契约

`ALE_CONNECT_REDIRECT` 层**没有** `FWPS_METADATA_FIELD_PROCESS_PATH`（实测该层 `PROCESS_PATH` metadata 计数恒为 0，而同一次运行里进程规则能命中）。这一层能拿到的是固定的 `ALE_APP_ID`，即 **UTF-16LE 的设备路径**，形如 `\device\harddiskvolumeN\...`。

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
| 用户态归一化 | `FwpmGetAppIdFromFileName0` 把 DOS 路径解析成设备路径，再按上表编码 |

**这个上限必须由两侧共用同一个具名常量**，不要在任一侧写死数字。它曾经出现过三方不一致：契约写 511、classify 的条件写 `>= 512`（实际只接受 510）、block filter 按 511 算、而用户态完全没有检查——最终症状是 C# 编译出 624 字节的路径、blob 合法、内核折成 0，**规则永久静默不命中**。

差分测试对这种不一致无能为力：C++ 的 `matcher` 不认识这个上限，夹具里的路径又都很短。这条只能靠两侧各自的编译期/解码期检查，以及一条「恰好 511 通过、512 拒绝」的边界用例。

同理，`FwpmGetAppIdFromFileName0` 的真实行为（返回什么形状、失败时指针是否为空）只在 Windows 上可验证，Linux 单测只能覆盖注入的映射——真实 API 的覆盖属于 VM 集成测试，不要把它算进 Linux 门禁的覆盖范围。

### 7.11 Host UDP 的实现事实（B0–B2b 实测）

- **没有新的 WFP 层，也没有回程注入。** UDP 和 TCP 共用 `ALE_CONNECT_REDIRECT_V4/V6` 的 callout；redirect classify 分配 36 字节 `redirect_context`（`protocol=17`），所有权交给 WFP。listener 用 `IP_WFP_REDIRECT_CONTEXT`（IPv4，`IPPROTO_IP`）/ `IPV6_WFP_REDIRECT_CONTEXT`（IPv6，`IPPROTO_IPV6`）从 `WSARecvMsg` 的 cmsg 逐 datagram 取回原始目的地；回包由用户态用**同一个 listener socket** `sendto`，驱动不改源、不注入 NBL、不维护 UDP flow 表。
- **同 socket 回包不重新 classify。** 中继用同一个 listener socket 把响应 `sendto` 回客户端时，
  datagram 复用入站重定向关联，不会再次进入 `ALE_CONNECT_REDIRECT`，`udp_classify_calls` 不增；
  self-PID 闸由 U1 的独立 socket 出站场景验证（permitted_self_process 增长）。`self_process_ids` 仍然要配置：中继新开 socket 访问上游或其他出站
  是新关联，需要闸 4 放行，否则会被再次 redirect / block。
- **classify 按关联，不按 datagram。** 同一 socket 对同一目的地 N 条 datagram 只产生 1 次 classify / 1 个 context；换目的地、换本地端口或换 socket 才是新关联。因此 classify 计数必须按关联数断言，不能写成「≤ N」这种会把「每条一次」和「每关联一次」都放过的弱断言。
- **block 在 auth 层静默丢包。** classify 只记 block 决策并放行，真实丢弃由 `ALE_AUTH_CONNECT_V4/V6` 的 `FWP_ACTION_BLOCK` filter 完成。UDP 的 `connect` / `send` / `sendto` 返回成功、对端收不到、`recv` 超时；TCP 对照仍是 `AccessDenied(10013)`。`udp_blocked` 是 classify 层的决策数，不是实际丢弃数；热替换后旧关联在 auth 层被丢，但不重新 classify，因此计数与事件都不增加。
- **any / fallback 按 6 和 17 各一条 filter。** 每条 filter 都显式写 `FWPM_CONDITION_IP_PROTOCOL`；不写协议条件的 filter 会误拦首个出站 ICMP（以及按 UDP 处理的其他 IP 协议），而 classify 对这些协议走 `permitted_non_tcp` 早退。`max_block_filters` 为 128。
- **IPv6 / dual-mode 的 family 取决于实际层。** v4-mapped unconnected 走 V4（family=4、纯 v4 地址），v4-mapped connected 与 native v6 走 V6（family=6、v4-mapped/16 字节地址）。cmsg 解析要同时接受 level 0 与 41、type 70，并支持 `sockaddr_in6`；不能笼统断言「dual-mode 一定 family=6」。
- **context 生命周期。** unconnected 在客户端 socket 关闭后 1s 内释放；connected 的释放窗口由 WFP 决定，B2b 观察到客户端退出 180s 后仍 outstanding、`sc stop` 后仍然存在，因此不能把「关闭即释放」当成保证，池断言只对 unconnected 严格。
- **规则语义。** `protocol=udp` 是 17；`protocol=any` 同时覆盖 TCP 与 UDP（相对旧版的行为变化）；`permitted_non_tcp` 只表示既非 TCP 也非 UDP。
- **配置约束。** 无进程选择器的 block 规则会连中继自己的上游 UDP 流量一起拦（与 TCP 相同），需要用进程选择器收窄。
- **验证入口。** `wfprobe udp-rules` 是正式套件；`udp-listen` / `udp-relay` / `udp-client` 只用于手工排查。
