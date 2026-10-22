# WFP 重定向驱动（规则表版）

WDM 驱动，在 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6` 上挂 callout，按用户态下发的规则 blob
决定放行 / 重定向；block 规则由 `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK`
filter 执行。host TCP 与 host UDP 共用同一套 callout、四道闸与规则匹配：TCP 每连接 classify
一次，UDP 每关联（本地端点 + 远端目的地）classify 一次，不按 datagram 重复分类。Phase 0 的
硬编码探针行为已被规则表取代。

## 文件

| 文件 | 内容 |
|---|---|
| `probe_ioctl.h` | 控制面 ABI v3：IOCTL 码、配置、计数、规则表回读、事件 |
| `rule_table.h/.cpp` | 规则 blob 的池内存副本、校验（含 selector UTF-8/511、fallback 冲突、顺序冲突）、引用计数、原子替换与退役链 |
| `probe_driver.h` | 驱动内部共享面（状态、锁、窄接口、callout 原型、include 顺序约束） |
| `probe_driver.cpp` | `DriverEntry`/卸载、设备、WFP provider/callout/filter 的注册与拆除 |
| `block_filters.cpp` | 把 block 规则编译成 ALE_AUTH_CONNECT 的 `FWP_ACTION_BLOCK` filter（`protocol=any` 与 fallback 按 6/17 展开；增删/滚动替换） |
| `probe_control.cpp` | IOCTL 处理：配置（含 0↔1 切换时 block filter 的挂/摘）、规则下发、计数、事件、调用者校验 |
| `probe_classify.cpp` | classify：facts、四道防循环闸、matcher、三个动作、重定向 |
| `process_path.cpp` | `ALE_APP_ID` 的 UTF-16 → UTF-8 转换（511 字节边界、代理对）与 0x807 诊断 |
| `wfprobe.vcxproj` | WDK 工程，`SignMode=Off`；签名由 `WfBuild.psm1` 调 signtool 完成 |

## 控制面

- `ioctl_set_rule_blob`：下发整块 `rule_blob`（`src/common/rule_blob.h`）。校验失败返回
  `STATUS_INVALID_PARAMETER`，当前规则表保持不变；成功则原子替换并 `generation++`。
- `ioctl_get_rule_info`：规则数、兜底动作、总长、generation。
- `ioctl_get_config` / `ioctl_set_config`：启用开关、监听端口 / PID、self-PID 集合。
- `ioctl_get_stats` / `ioctl_reset_stats`：classify 次数、各动作计数、被拒绝的 blob 数、
  context 分配/失败、四道闸的细分计数、block 决策计数，以及四个 UDP 计数器
  `udp_classify_calls` / `udp_permitted` / `udp_blocked` / `udp_redirected`。
  对任意一次 reset 之后的增量，`udp_classify_calls == udp_permitted + udp_blocked + udp_redirected`。
  `udp_blocked` 是 classify 层的 block 决策数，不是实际被丢掉的 datagram 数；`udp_permitted` 包含
  redirect 失败后的 fail-open；listener 用同一个 socket 回包时复用入站重定向关联，不重新
  classify，`udp_classify_calls` 不增；中继新开 socket 的出站才是新关联，会再计入一次
  `udp_classify_calls` / `udp_permitted`。
  `permitted_non_tcp` 现在只表示“协议既非 TCP 也非 UDP”。
- `ioctl_get_events`：64 条环形事件（decision / action / branch / 命中规则序号 / 地址族 /
  `protocol`）。`protocol` 取 `IPPROTO_TCP(6)` 或 `IPPROTO_UDP(17)`，位于偏移 58；
  `probe_event` 仍是 64 字节。`probe_stats` 扩到 264 字节，四个 UDP 计数器位于
  224 / 232 / 240 / 248。
- `ioctl_measure_process_path`（0x807，诊断，**发布前可移除**）：把 UTF-16LE 缓冲区按
  `collect_process_path` 的规则转成 UTF-8，用于验证 511 字节边界与代理对行为；只读、无状态、
  admin-only。存在的唯一理由是长路径进程无法在测试环境构造，删掉它那段逻辑就不可验证。

控制面 ABI 版本是 **3**。`probe_config` / `probe_rule_info` / `redirect_context` 的布局不变；
`probe_event` 仍是 64 字节，`protocol` 落在偏移 58（原 `branch` 与 `matched_rule` 之间的填充位）；
`probe_stats` 从 232 扩到 264，追加四个 UDP 计数器。旧版 `wfprobe` / 控制器在下发配置时会被
`STATUS_REVISION_MISMATCH` 拒绝，驱动与用户态必须同批升级。

`wfprobe events` 会打印 `protocol=`，`wfprobe stats` 与控制器 `state` 会输出四个 UDP 计数器。

全部 IOCTL 走 `caller_is_admin`；设备用 `IoCreateDeviceSecure` + `SDDL_DEVOBJ_SYS_ALL_ADM_ALL`。
`FILE_ANY_ACCESS` 没有放宽访问控制：DACL 与调用者校验才是边界。

## 规则与防循环

规则 blob 的第一条命中决定动作（`permit` / `block` / `redirect`，未命中用 `fallback_action`）。
协议字段取 `rule_protocol_any=0` / `tcp=6` / `udp=17`；`any` 同时覆盖 TCP 与 UDP（相对旧版
“any 只匹配 TCP”的行为变化）。17 是规则 blob 的枚举扩展，布局没有变化，因此
`rule_blob_version` 仍是 1；旧驱动会把 17 当作未知协议拒绝整块 blob（fail-closed）。

classify 依次检查：

1. 目标端口等于配置的监听端口 → 放行（闸 1）；
2. `previousVersion->localRedirectHandle` 非空（别的 callout 已改写）→ 放行；
3. `FwpsQueryConnectionRedirectState0` 报告 BY_SELF / PREVIOUSLY_BY_SELF → 放行（闸 2）；
4. 条件标志含 `IS_CONNECTION_REDIRECTED` 或 `IS_PROXY_CONNECTION` → 放行（闸 3）；
5. 发起进程在 self-PID 集合里 → 放行（闸 4）。

UDP 的 connected 路径（`connect` + `send`）与 unconnected `sendto` 也会走到这个层；四道闸、规则匹配和三个动作与 TCP 共用。
同一客户端 socket 对同一远端的后续 datagram 复用第一次的决策与 redirect context。

`block` 决策在 redirect 层只记账并放行；真正的拒绝由 auth 层的 block filter 完成。TCP 客户端
通常看到 RST（`AccessDenied(10013)`），UDP 则是静默丢包，详见下一节。TCP 侧的背景见
`.trellis/tasks/10-20-wfp-driver-rules/research/findings.md` §2。

## Host UDP

### 规则语义

- `protocol=udp` 写 17；`protocol=tcp` 写 6；`protocol=any` 同时匹配 TCP 与 UDP。
- 这是有意的行为变化：现有“any 规则只在 TCP 上生效”的配置升级后也会匹配 UDP。需要维持旧行为
  时把规则显式写成 `protocol=tcp`。
- `permitted_non_tcp` 的含义收窄为“既非 TCP 也非 UDP”，旧看板/日志的解读要同步。

### 重定向

UDP 没有新增 WFP 层：仍由 `ALE_CONNECT_REDIRECT_V4/V6` 上的同一个 callout 按关联 classify。
每次 redirect 分配一块 36 字节 `redirect_context`，其中 `protocol=17`，family / 原始地址 /
端口 / 进程 PID / 规则序号与 TCP 相同；所有权交给 WFP，驱动不释放。

listener socket 设置 `IP_WFP_REDIRECT_CONTEXT`（`IPPROTO_IP`）或 `IPV6_WFP_REDIRECT_CONTEXT`
（`IPPROTO_IPV6`），用 `WSARecvMsg` 的 cmsg 逐 datagram 取回 context。回程不经过驱动：
中继用**收到 datagram 的同一个 listener socket** 把响应 `sendto` 回 datagram 的源地址。
驱动不做回程注入、不改源地址、不维护 UDP flow / mapping 表。listener / 中继进程必须把自己放进配置的
`self_process_ids`：同一个 listener socket 的回包复用入站重定向关联，不会再次进入
`ALE_CONNECT_REDIRECT`（实测 `udp_classify_calls` 不增）；但中继新开 socket 访问上游或其他出站
仍会 classify，所以 listener / 中继进程仍应把自己放进 `self_process_ids`，靠闸 4 放行。

IPv6 / dual-mode 的 context family 取决于 WFP 实际走的层，不能只看客户端 socket：
v4-mapped + `sendto`（unconnected）走 V4（family=4、纯 v4 地址），v4-mapped + `connect`
走 V6（family=6、v4-mapped 地址），native IPv6 两种模式都是 V6。listener 要同时接受
cmsg level 0（`IPPROTO_IP`）与 41（`IPPROTO_IPV6`），以及 `sockaddr_in6`。

### block

UDP block 规则编译成 `ALE_AUTH_CONNECT_V4/V6` 上的 `FWP_ACTION_BLOCK` filter。应用侧是
**静默丢包**：`connect` / `send` / `sendto` 返回成功，对端收不到，`recv` 最终超时；没有可用的
socket 错误码路径。TCP 对照仍是 `AccessDenied(10013)`。

`protocol=any` 与 `fallback_action=block` 都按 6 和 17 各生成一条 filter（IPv4 / IPv6 每层一对），
每条都显式写 `FWPM_CONDITION_IP_PROTOCOL`，不使用无条件 filter；这样 ICMP 等非 TCP/UDP 协议
不会被误拦。`max_block_filters` 为 128。

`udp_blocked` 只统计 classify 层的 block 决策。规则热替换后，旧 UDP 关联的后续 datagram 会在
auth 层被新 filter 丢弃，但不会重新进入 `ALE_CONNECT_REDIRECT`，因此 `udp_classify_calls` /
`udp_blocked` 不增加，也没有新事件；只有新关联才按新 blob 分类。

与 TCP 相同，block 规则若没有进程选择器，也会拦到中继自己的上游 UDP 流量。需要放行中继出站时，
用进程选择器收窄 block 规则。

### 生命周期

unconnected UDP 的 redirect context 随客户端 socket 关闭释放（B0 实测 1 秒内）。connected 关联的
释放时机由 WFP 决定，目前**没有“关闭 socket 立即释放”的保证**；B2b 观察到客户端退出 180s 后
仍 outstanding、`sc stop` 后仍然存在。池断言只对 unconnected 做严格判定，connected 按实测记录
限制，不能写成“立即释放”。

**已知 WFP 缺陷（26100）**：connected UDP 每发一条 datagram，`tcpip` 会给 redirect record
加一次引用且不归还，36B `WfPr` 与 `Arcl` record 直到重启才释放；实测 1/3/100 条 datagram →
record refcount 1/3/100。详见
[`wfp-connected-udp-record-refcount-leak.md`](../../.trellis/spec/backend/wfp-connected-udp-record-refcount-leak.md)。

### 验证

`wfprobe udp-rules` 是正式验证入口，覆盖 redirect / permit / fallback / block / any / ICMP /
进程选择器 / IPv6 / enabled 0↔1 / 多 datagram / 多目的地 / 热替换 / 生命周期（U1–U13）。
`wfprobe udp-listen` / `udp-relay` / `udp-client` 是手工排查
工具，不是门禁入口。

套件进程自己持有 listener，并把自身 PID 放进 `self_process_ids`（用于中继新开 socket 的出站；
listener 同 socket 回包复用入站关联，不重新 classify）；UDP 客户端在子进程里运行、不在 self 集合，首包必须被 redirect。

## 系统要求

池内存用 `ExAllocatePool2(POOL_FLAG_NON_PAGED, ...)` 分配（`cpp/drivers/wdk-deprecated-api` 的修复），
最低支持 **Windows 10 2004（10.0.19041）**；更早的系统上 `wfprobe.sys` 会因缺少该导出而加载失败。
要覆盖旧系统时，改为运行时用 `MmGetSystemRoutineAddress` 取 `ExAllocatePool2`、缺失时回退到
`ExAllocatePoolWithTag`。

`DriverEntry` 用 `extern "C" DRIVER_INITIALIZE DriverEntry;` 声明角色类型。CodeQL 的
`cpp/drivers/illegal-field-access-2` 与 PREfast 的 C28175 都按这条声明判定 DriverEntry 上下文，
`probe_driver_entry` / `create_device` 里的 `DriverObject->MajorFunction`、`DriverUnload` 赋值才不会被
判成越权访问——只有 SAL 注解（`_Function_class_(DRIVER_INITIALIZE)`）不够。

## 构建、签名与安装

在任意一台装有 Visual Studio Build Tools（MSVC + WDK）的 Windows 机器上，以管理员 PowerShell 运行仓库门禁：

```powershell
pwsh -File tools/windows/gate.ps1 -Remote <测试目标地址:端口>
```

`-Root` 默认由脚本位置推到仓库根，也可以用 `-Root <仓库根>` 显式指定。工具发现、构建、签名、
安装、卸载与 PREfast 都在 `tools/windows/WfBuild.psm1`；只做构建/安装/启动时用
`tools/windows/load-driver.ps1`。工程用 `..\..\src\common` 指向共享头，仓库目录结构要保持。
产物是 `src\driver\x64\Debug\wfprobe.sys`；签名使用证书库中的 `WinForward Test Driver` 证书。
`NuGet.config` 只配置 nuget.org；`-restore` 会按需下载 WDK / SDK 包，不依赖任何本机包目录。

## 安装与卸载

`tools/windows/WfBuild.psm1` 的 `Install-Driver` / `Uninstall-Driver` 负责签名、复制到
`System32\drivers`、创建/启动/停止/删除服务。不要手工复制 `.sys` 或直接改服务，避免门禁与
手工操作的状态不一致；完整流程用 `gate.ps1`，临时调试用 `load-driver.ps1`。

- block 规则编译成独立的 auth 层 filter；`protocol=any` 与 `fallback_action=block` 按 6/17 各一条
  （IPv4 / IPv6 各一对）。凡是“更早的非 block 规则可能与它重叠”的配置会在换表时整块拒绝
  （`rule_order_valid`），绝不让 filter 抢掉 blob 里本应首个命中的 permit/redirect。
- 进程选择器是 `ALE_APP_ID` 的 UTF-16 device path → UTF-8（ASCII 大小写折叠，代理对按标准
  展开）。上限是 **UTF-8 511 字节（不含 NUL）**，缓冲区 512 字节含 NUL；超限/非法代理项整条
  不匹配、不截断。中文路径与 511/512 边界都已实测。
- 用户态要用 `FwpmGetAppIdFromFileName0` 取 app id；该 API 对 >MAX_PATH 的路径会失败，长路径
  需要用户态自己拼 device path。
- 闸 2 目前只由组合用例覆盖，未单独钉住（需要第二个 redirector，findings §5）。
