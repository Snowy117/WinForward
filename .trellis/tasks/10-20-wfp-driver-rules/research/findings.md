# 实现结论 — 驱动规则表与 IOCTL

> 实测平台：WinLTSC（Windows 11 IoT Enterprise LTSC，192.168.100.2）。
> 日期：2026-10-21。驱动：`src/driver/`；测试程序：`tools/wfprobe/`；脚本：`tools/vm/`。

## 1. 规则表并发模型与原子替换

实际实现（`rule_table.{h,cpp}` + `probe_driver.cpp` 的自旋锁）：

- 一块规则表 = `ExAllocatePoolWithTag(NonPagedPoolNx, sizeof(rule_table) + blob_size, 'rTfW')`，
  结构体后面紧跟 blob 副本；校验直接对副本调用 `try_open_blob`（副本才是之后被求值的内存）。
- **没使用 `EX_RUNDOWN_REF`**：classify 在 `DISPATCH_LEVEL` 跑，`ExAcquireRundownProtection`
  只允许 `IRQL <= APC_LEVEL`。等价语义由「自旋锁保护指针 + 每个读者一份引用」实现：
  - 读者（classify）：在自旋锁内读 `state.rules` 并对 `references` 做 `InterlockedIncrement`；
    锁释放后求值；返回前 `InterlockedDecrement`。锁内只有取指针与加计数，没有阻塞。
  - 写者（IOCTL）：同一把锁内置换指针、`generation++`；然后按 10 ms 一轮等旧表引用归零
    （上限 2 s）。归零即 `ExFreePoolWithTag`；超时则挂到退役链，下次换表或卸载时再释放。
  - 卸载：`FwpsCalloutUnregisterById0` 成功之后不可能再有 classify；此时直接释放当前表与
    全部退役表。
- **enabled 开关与 filter 生命周期**：0↔1 切换与换表共用 `rule_mutex`。enabled=0 时删除全部
  block filter（真正停止拦截，而不是只让 classify 早退）；enabled=1 时按当前表重建。换表时
  若驱动处于禁用态则不创建 filter，避免“禁用状态下装一个 block blob 又开始拦”。
- **失败不动旧表**：`allocate_rule_table` 返回 `nullptr` 时只 `rejected_blobs++` 并返回
  `STATUS_INVALID_PARAMETER`，既不换指针也不碰 generation。A2 的断言（规则数、兜底动作、
  generation 不变 + 旧表仍能命中）逐条覆盖。
- 换表与 block filter 增删由 `KMUTEX rule_mutex` 串行：先挂新 filter，再换表，再删旧 filter。
  任何窗口里都不会出现「规则可见但没人执行」或「filter 常驻但没有对应规则」。
- 100 次连续替换时后台还有一条持续建连/回显的循环（读者与写者并发）；全部成功、
  generation 恰好 +100、期间 classify 持续正确放行。见 `wfprobe rules`。

## 2. block 规则：为什么不在 redirect 层，最终怎么落地

### 2.1 实测结论

`ALE_CONNECT_REDIRECT_V4/V6` 只用于改地址/端口，**不执行拦截**：

- 一条 `block` 规则命中、`classifyOut->actionType = FWP_ACTION_BLOCK` 后，连接仍然建立成功，
  本机 echo 服务的 accept 计数 +1；`matched_block` / `blocked` 计数正常 +1。
- 复现模式：`wfprobe blockprobe --rule-port <port> --remote <host:port>`（见 `BlockProbe.cs`）。
- 依据：`FWPM_LAYER_ALE_CONNECT_REDIRECT_V4` 的定位是 "allows for modification of remote
  addresses and ports"；WFP 文档明确把“阻止连接到特定位置”指向
  `FWPM_LAYER_ALE_AUTH_CONNECT_V*` 上的 `FWP_ACTION_BLOCK` filter。
  （Microsoft Q&A / NTDEV 也明确：redirect 层用于修改 flow，不用于阻断。）

### 2.2 尝试过的两种 callout 方案（都不成立）

1. **AUTH_CONNECT callout 返回 PERMIT**：终止型 PERMIT 在 `FWPM_SUBLAYER_UNIVERSAL`（权重 256）
   里先于 MPSSVC Windows Firewall 子层（权重 2）仲裁，导致 Windows 防火墙的 block 规则被截胡。
   实测：驱动加载、auth filter 活动、防火墙对 1.2.3.4:19003 出站 Block →
   `connected=True`（绕过）；把 auth filter 摘掉 → `connected=False`。这是安全洞。
2. **AUTH_CONNECT callout 返回 `FWP_ACTION_CONTINUE`**：终止型 callout 返回 CONTINUE 会被引擎
   当成“没有决定”，实际效果是**所有**连接都被拒（实测 `rules` 套件全红，connect 全部
   `WSAEACCES`）。不可用。

### 2.3 最终实现：按规则编译 `FWP_ACTION_BLOCK` filter

- 规则表里出现 `action == block` 的规则（或 `fallback_action == block`）时，为每条规则在
  `FWPM_LAYER_ALE_AUTH_CONNECT_V4/V6` 上创建 `FWP_ACTION_BLOCK` filter：
  - 条件按规则字段翻译：`IP_PROTOCOL`（TCP）、`IP_REMOTE_PORT`（`FWP_MATCH_RANGE`）、
    `IP_REMOTE_ADDRESS`（`FWP_V4_ADDR_AND_MASK` / `FWP_V6_ADDR_AND_MASK`）、
    `ALE_APP_ID`（进程选择器，见 §3 的字节形态）。
  - **字节序坑**：`FWP_V4_ADDR_AND_MASK` 的 `addr` 与 `mask` 都是**主机字节序**
    （fwptypes.h 文档原文 “specifies IPv4 address and mask in host order”）。第一版把 mask
    写成网络序，`FwpmFilterAdd0` 直接返回 `FWP_E_INVALID_NET_MASK (0x8032001F)`；改成
    主机序（/8 → 0xFF000000）并通过 `wfprobe rules` 的网段用例验证。IPv6 用
    `FWP_V6_ADDR_AND_MASK` 的 16 字节地址 + `prefixLength`，没有字节序问题。
  - `fallback_action == block` 时创建**无条件** filter（两条：V4/V6），即整机出站阻断。
  - 权重：显式 `FWP_UINT64 = 0x10`，子层 `FWPM_SUBLAYER_UNIVERSAL`。选择理由：该 filter 只会
    增加拒绝、从不放行，因此即使排在防火墙之前也不会让防火墙要拦的流量通过；跨子层次序由
    子层权重决定（防火墙子层权重 2，本驱动 256），同子层内用固定值避免引擎默认权重推导。
  - **protocol=any 统一按 TCP 下发** `IP_PROTOCOL` 条件：这个层只观察 TCP（classify 对非 TCP
    直接 `permitted_non_tcp` 早退），否则 auth filter 会把 UDP connect 也一起拦掉，行为与计数
    不一致。规则语言里 protocol 只允许 any/TCP（`rule_blob.h` 已校验）。
  - 规则替换时整组换掉：先创建新 filter（key 用递增序号，绝不与旧 filter 撞 key），再换表，
    最后按 id 删除旧 filter。任一步失败则回滚新 filter、拒绝整块 blob、旧表与旧 filter 保持不变。
  - 没挂 callout 到 AUTH_CONNECT：callout 的“放行”决定会抢占更高优先级的过滤器，而 filter
    没有这个问题。这与 design.md 的“auth 层 callout”写法不同，是 §2.2 实测后的裁决。
- **安全评审项（已实证排除）**：验证脚本 `tools/vm/firewall-check.ps1` 在“我们的 block filter
  活动”前提下加一条 Windows 防火墙出站 Block 规则，确认连接仍然被防火墙拒绝；撤掉防火墙规则
  后连接恢复。三条断言全过（见 §7）。
### 2.4 重叠规则：编译期拒绝，不允许静默改变语义

block filter 只看条件、不看规则顺序；如果一条更早的 `permit`/`redirect` 规则本应“首个命中”，
它在 auth 层就会被后面的 block filter 抢掉。这种配置现在**整块 blob 拒绝**，不进入运行时：

- 驱动侧（`rule_table.cpp` 的 `rule_order_valid`）：对每条 block 规则，检查是否存在更早的
  非 block 规则与它“可能重叠”——协议相容（任一侧 any 或相等）、地址族相容（any 或相等，
  有网段时取网段族）、端口区间相交、网段前缀相交（CIDR 只会嵌套或不相交，比较较短前缀）、
  进程选择器任一侧 any 或字节相等。命中则 `allocate_rule_table` 返回 nullptr，整块 blob 被
  `STATUS_INVALID_PARAMETER` 拒绝，旧表/旧 filter 不变，`DbgPrint` 打印两条规则序号。
- 测试侧镜像（`tools/wfprobe/BlobBuilder.cs` 的 `ValidateRuleOrder`）：`Build` 直接抛
  `InvalidOperationException`，消息里带 `#更早序号`、`#block 序号` 和两条规则的条件摘要，
  对应用户态编译器的报错；`BuildUnchecked` 只给“驱动必须独立拒绝”的负向用例用。
- 实测：`[rules] conflict: builder-rejected=True driver-rejected=True generation=0->0`；
  冲突规则必须**编译失败**，而且错误信息指出两条规则（断言 `#0`/`#1`）。
- 这是“拒绝歧义配置”而不是“运行时复刻 first-match”；用户要么收窄条件、要么调整顺序。
- 注意它只处理**顺序**问题：两条 block 规则互相重叠是允许的（都拒绝，语义相同）。
- **fallback=block 的同款破坏也在这一层拦**：`fallback_action == block` 会生成无条件 filter，
  表里任何 non-block 规则都会变成死规则。`allocate_rule_table` 在
  `fallback == block && 存在 action != block 的规则` 时整块拒绝；C# `BlobBuilder.ValidateFallback`
  做同样的拒绝（`builder-rejected=True`、`driver-rejected=True`，generation 不变）。
  只有“表里全是 block 规则 + fallback=block”是合法配置，A4 已覆盖“命中仍走 matched_block、
  未命中走 fallback_block”两条路径。

### 2.5 redirect 失败 = 直连（fail-open），不是 fail-closed

三条 redirect 失败路径（没有 listener/handle、`FwpsAcquireClassifyHandle0` 失败、拿不到
writable data、context 分配失败）以前把 `classifyOut` 置 BLOCK 并记 `blocked`。但按 §2.1 的
实测，**该层不执行拦截**——BLOCK 不会阻断，连接会直连真实目的地。所以：
- 行为上一直是 **fail-open（直连）**；
- 现在 `classifyOut`、事件、计数都改成 permit + `redirect_failures`/`apply_failures`，
  不再虚报 `blocked`；
- Phase 0 文档里“分配失败 → BLOCK，fail-closed 是刻意语义”已订正为“失败 = 直连”，并注明
  若要真的拒绝，必须落到 ALE_AUTH_CONNECT。

## 3. 进程选择器：ALE_APP_ID，不是 PROCESS_PATH

- 官方 metadata 可用性表
  <https://learn.microsoft.com/en-us/windows-hardware/drivers/network/metadata-fields-at-each-filtering-layer>
  里，
  `FWPS_LAYER_ALE_CONNECT_REDIRECT_V4/V6` 可用的 metadata 只有 token / processId /
  transport endpoint / parent endpoint / icmp / localRedirectTargetPID /
  redirectRecordHandle / subProcessTag —— **没有 `FWPS_METADATA_FIELD_PROCESS_PATH`**。
  实测也确认该 metadata 不出现，因此 design.md 的“取 PROCESS_PATH（DOS 路径）”不可实现。
- **实测记录**：驱动在 `gather_facts` 里对 `FWPS_METADATA_FIELD_PROCESS_PATH` 做存在性计数，
  `wfprobe rules` 跑完后 `wfprobe stats` 的 `process_path_metadata=0`（14 次 classify 全部
  来自 ALE_CONNECT_REDIRECT）；同一次运行里进程选择器规则能命中（`matched_permit` / 进程串
  匹配的 block 用例），说明该层拿到的确实是 ALE_APP_ID 而不是 PROCESS_PATH。计数器长期保留，
  以后若 metadata 行为变化会直接显形。
- 该层可用的固定值是 `FWPS_FIELD_ALE_CONNECT_REDIRECT_{V4,V6}_ALE_APP_ID`，
  文档定义就是 “The full path of the application”，字节形态是 UTF-16LE、device path、
  末尾带 UTF-16 NUL。用户态 `FwpmGetAppIdFromFileName0` 返回同一串（实测
  `C:\Windows\System32\notepad.exe` → `\device\harddiskvolume3\windows\system32\notepad.exe`）。
- **内核侧的比较契约**（逐条）：
  1. 连接侧：`ALE_APP_ID` 是 UTF-16LE 码元序列（末尾 NUL）。内核按码元手工转换成 UTF-8：
     代理对按标准展开成 4 字节，孤立代理项整条按不匹配处理；遇到 NUL 停止。
     **不能用 `RtlUnicodeToUTF8N`**——它只允许 PASSIVE_LEVEL，而 classify 在 DISPATCH_LEVEL。
  2. 只折叠 ASCII `A-Z → a-z`（与 `matcher.h` 的 `ascii_lower` 同一实现），非 ASCII 字节原样。
  3. 结果是一个 **UTF-8、反斜杠分隔的 device path**，
     e.g. `\device\harddiskvolume3\users\张三\wfprobe.exe`。
  4. 上限是 **UTF-8 字节数**：≤511 字节（+NUL 恰好 512）。超限或非法代理项 → 整条不匹配
     （`process_path_length = 0`），**绝不截断**（截断会产生错误匹配）。
  5. 规则 blob 的进程池必须是同样的 UTF-8 字节串（ASCII 已折叠、非 ASCII 原样）、NUL 结尾、
     `\device\harddiskvolumeN\...` 形式。**驱动在 `allocate_rule_table` 里对每条 selector 做
     UTF-8 合法性 + ≤511 字节校验，失败整块拒绝**（permit/redirect 同样），不依赖上游编译器。
  6. block filter 的 `ALE_APP_ID` 条件要反向转换：规则池 UTF-8 → UTF-16LE（`RtlUTF8ToUnicodeN`，
     IOCTL 在 PASSIVE_LEVEL 可以调用）→ 条件字节。转换失败（非法 UTF-8/超长）时**拒绝整块
     blob**，绝不退化成“无条件 block”。
- **用户态对齐要求**：进程选择器不能直接用 DOS 路径（`C:\...`），必须先用
  `FwpmGetAppIdFromFileName0` 取 app id，再做与内核完全相同的归一化（ASCII 大小写折叠后按
  UTF-8 写进进程池）。当前 userland 批次产的是 DOS 路径，需要改；本批没有修改 `src/userland/`。
- **实测（中文路径命中）**：把 `wfprobe.exe` 整套复制到 `C:\Users\张三\`，用
  `wfprobe rules` 里的子进程用例让 `…\张三\wfprobe.exe` 去连被 block 的端口：
  `[rules] chinese-path: app-id=\device\harddiskvolume3\users\张三\wfprobe.exe blocked-exit=3 allowed-exit=0`
  —— 中文路径参与进程匹配，block filter 的 ALE_APP_ID 条件也能往返。
- **长度边界（511/512）**：上限是 **UTF-8 字节数 ≤ 511（不含结尾 NUL）**，缓冲区 512 字节含
  NUL；超限整条不匹配（length=0），不截断。诊断 IOCTL `ioctl_measure_process_path`（0x807）
  把一段 UTF-16LE 交给同一个 `collect_process_path`，实测：
  `[rules] boundary: utf8-511=511 utf8-512=0 emoji=6 surrogate=0`
  —— 511 字节完整通过；512 字节被拒（0）；代理对 U+1F600 展开成 4 字节；孤立高代理项整条
  不匹配。补测还覆盖了“代理项出现在路径中间/结尾”的三种位置（`61 00 3D D8`、`61 00 62 00
  00 D8 63 00`、`61 00 62 00 00 DC 63 00`）：修复前的实现会留下前缀，现在统一整条置 0
  （`collect_process_path` 在每条失败分支都写 `length = 0; target[0] = 0`）。另有 3 字节
  序列的 508+1 与 509+1 边界断言。
- **长路径 API 坑**：`FwpmGetAppIdFromFileName0` 对超过 MAX_PATH 的 DOS 路径会失败（实测
  `C:\Users\张三\boundary\…\wfprobe.exe` 直接报错），用户态编译器要自己处理长路径
  （例如从卷前缀拼 device path），否则这些进程路径无法进入规则。
- AUTH_CONNECT 层的 block filter 用同样的字节形态构造 `ALE_APP_ID` 条件：把规则串的每个
  ASCII 字节展开成 UTF-16LE 并补 UTF-16 NUL（见 `fill_block_app_id`）。`wfprobe rules` 里
  “同一端口、进程串匹配才拦 / 不匹配则放行”两个用例覆盖了这条往返。

## 4. 条件标志：两层各查各的

- `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`（0x00100000）按文档只适用于
  `ALE_AUTH_CONNECT_*`（和 DISCARD）；在 `ALE_CONNECT_REDIRECT_*` 上不会置位。
- `ALE_CONNECT_REDIRECT_*` 上会置位的是 `FWP_CONDITION_FLAG_IS_PROXY_CONNECTION`
  （0x00200000，“连接被代理过、存在 previous redirect records”）。
- 因此闸 3 同时检查两个标志：`IS_CONNECTION_REDIRECTED | IS_PROXY_CONNECTION`。
  `permitted_condition_flags` 计数与事件 decision 都覆盖。

## 5. 四道防循环闸（顺序与验证）

实现顺序：监听端口 → previousVersion（其他 callout 已改写）→ redirect state（闸 2）→
条件标志（闸 3）→ self-PID（闸 4）。与 design.md 的“port → flag → query → pid”相比，把
query 放在 flag 之前，原因见下。

| 闸 | 正向验证 | 负向验证（变异） |
|---|---|---|
| 1 监听端口 | `wfprobe gates`：客户端直连监听端口，relay 收到的连接 `context=0B`，事件 decision=`permitted_listener_port` | 把条件改恒假 → 连接被再次重定向，relay 读到 36B context，断言变红 |
| 2 redirect state | 同一场景：relay 在出站 socket 上 `SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS`，事件 decision=`permitted_redirect_state`，relay 出站直达目标、没有第二次 accept | 把条件改恒假 → 命中的是闸 3，decision 变成 `permitted_condition_flags`，断言变红 |
| 3 条件标志 | 与闸 2 同场景（`IS_PROXY_CONNECTION` 置位） | 与闸 2 一起拆掉 → relay 出现第二次 accept（成环），断言变红 |
| 4 self-PID | self-PID 集合含 relay PID、出站不带 records：decision=`permitted_self_process`，出站 echo 成功、没有第二次 accept | 改恒假 → 出站被重定向回 relay，出现第二次 accept，断言变红 |

**测试覆盖的精确说法**：闸 2 的 **decision 已被单独钉住**（变异 #5 把 `redirect_state_hit`
改假 → 事件变成 `permitted_condition_flags` → gates 变红）。但“**拆掉闸 2 也不成环**”这件事
只由组合用例覆盖（变异 #7 同时拆闸 2 + 闸 3 → 成环）：单拆闸 2 时闸 3 仍会兜底，无法构造出
成环。要单独验证“闸 2 缺失时的行为”，需要第二个 redirector（多 callout 链）构造
`REDIRECTED_BY_OTHER` 场景，留给后续批次。

## 6. V6 / dual-mode 实测

- 注册两个 callout + 两个 filter（`FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6`），classify 按
  `layerId` 分支；V6 的重定向目标用 `SOCKADDR_IN6`（ANY → `::1`；指定 local → 该 v6 地址）。
- **A5 实测（.NET `TcpClient.Connect(string,int)` 连 IPv4 字面量）**：
  `wfprobe selftest --dual --remote 1.2.3.4:19003 --iterations 3`
  - 客户端是 dual-mode socket；`context raw=...0606...FFFF01020304` 即 family=6、
    original=`::ffff:1.2.3.4:19003`、pid 正确；
  - 监听端（`[::]` + DualMode）accepted 端点是 `::1:52639`；
  - 3/3 连接被捕获、context 读回、echo 成功；`redirected=3`、`contexts_allocated=3`。
  - 负向：变异“只挂 V4 filter”后同一命令失败（监听端等不到连接）。
- 归因：只挂 V4 时 dual-mode 连接走 V6 层，驱动完全看不到。这条已由变异测试钉住。

## 7. 验证结果（2026-10-21 本轮）

- `wfprobe rules`：PASS。覆盖 A1（规则数/兜底动作/总长回读）、A2（11 种非法 blob 逐个拒绝
  + generation/规则数/兜底动作不变 + 旧表仍命中）、A3（permit/block/redirect 三个动作，
  block 连接被拒且 echo 服务无 accept；进程串匹配/不匹配的 block；网段 127.0.0.0/8 匹配与
  10.0.0.0/8 不匹配）、A4（fallback=permit；fallback=block 只允许全 block 表：未命中被拒且
  记 fallback_block，**命中的 block 规则仍走 matched_block**）、
  A6（100 次替换 + 并发连接）、A8（每场景的 matched_*/fallback_*/blocked/redirected/
  contexts_allocated 精确增量）、context 池分配/释放计数（`WfPr`：allocs/frees == 重定向次数、
  outstanding 0）。
- `wfprobe selftest --remote ...`：PASS（V4，3/3）。
- `wfprobe selftest --dual --remote ...`：PASS（V6，3/3，family=6、`::ffff:1.2.3.4`）。
- `wfprobe gates`：PASS（闸 1/3/4 的正向断言、无第二次 accept、事件 decision 逐条命中）。
- `tools/vm/firewall-check.ps1`：PASS。
  - 无防火墙规则：`connected=True`（基线，证明后续变化来自防火墙）
  - 防火墙出站 Block 1.2.3.4:19003，且我们的 block filter 活动：`connected=False`
    → 终止型 PERMIT 的截胡问题不存在（本实现根本没有 auth callout，只有 BLOCK filter）
  - 撤掉防火墙规则：`connected=True`（反向对照）
- `process_path_metadata=0`：redirect 层没有 `FWPS_METADATA_FIELD_PROCESS_PATH`（实测计数）。
- 冲突规则：`[rules] conflict: builder-rejected=True driver-rejected=True generation=0->0`
  （编译期报错 + 驱动独立拒绝，generation 不变）。
- 中文路径：`[rules] chinese-path: app-id=\device\harddiskvolume3\users\张三\wfprobe.exe blocked-exit=3 allowed-exit=0`。
- 511/512 边界：`[rules] boundary: utf8-511=511 utf8-512=0 emoji=6 surrogate=0`。
- fallback=block 冲突：`builder-rejected=True` + `driver-rejected=True`，generation 不变。
- 禁用开关：`[rules] disabled: filter-off=True filter-on=True`（enabled=0 真的撤掉 filter，连接
  放行；enabled=1 重新挂上，连接被拒）。
- selector 闸：`[rules] selector-guard: long-rejected=True case-overlap=True`（512 字节 permit
  blob 被驱动拒绝；仅大小写不同的两条 selector 视为重叠）。
- 构建：0 warning（/W4 /WX）、签名 `Valid`、`sc start` RUNNING、`sc stop` STOPPED、重复
  load/unload 与规则替换后卸载均干净，VM boot time 未变。
- `clang-format --dry-run --Werror` 通过；驱动各文件有效行：classify 445、process_path 88、
  control 296、driver 330、block_filters 215、redirect_target 61、rule_table 272、头文件 95/147/22，
  全部 ≤500。

## 8. 变异测试（每一条都必须让对应套件变红）

`tools/vm/mutation-test.ps1`：逐条改源码 → 重建 → 签名 → 装载 → 跑目标套件 → 期望非 0 →
恢复源码。16 条变异全部变红；恢复源码后重新构建并跑 4 个正向基线，全部 exit 0
（`mutation verdict: PASS`）。

| # | 变异 | 目标套件 | 退出码 |
|---|---|---|---|
| 0 | block filter 不创建（block 规则不再阻断） | rules | 6 |
| 1 | redirect 分支退化成放行 | selftest V4 | 5 |
| 2 | block 分支退化成放行 | rules | 6 |
| 3 | permit 分支退化成 block | rules | 6 |
| 4 | 闸 1（监听端口）恒假 | gates | 7 |
| 5 | 闸 2（redirect state）恒假 | gates | 7 |
| 6 | 闸 4（self-PID）恒假 | gates | 7 |
| 7 | 闸 2 + 闸 3 同时拆掉（成环） | gates | 7 |
| 8 | V6 层 filter 不挂（只挂 V4） | selftest --dual | 5 |
| 9 | 非法 blob 被当成成功 | rules | 6 |
| 10 | 非 ASCII 进程路径被丢弃（UTF-8 转换退化） | rules | 6 |
| 11 | 规则顺序冲突检测被绕过 | rules | 6 |
| 12 | fallback=block 冲突检测被绕过 | rules | 6 |
| 13 | enabled=0 不撤 block filter | rules | 6 |
| 14 | selector UTF-8/511 校验被绕过 | rules | 6 |
| 15 | permit 计数器记到 block 上 | rules | 6 |

注：第 0 条最初写成“函数开头 early return”，`/WX` 以 C4702（unreachable code）拒绝了这次
变异构建；第 10 条最初把 NUL 写成 `'\\0'`，C4305/C4309（int→char 截断）又拒绝了构建。
两条都改成运行期恒真/正确字符字面量后才是有效变异——变异本身必须能编译，否则脚本只会
把它记成“变异失败”，而不是“测试变红”。

另外：驱动侧“重叠判定 ASCII 折叠”没有单独变异——测试侧 `BlobBuilder` 的镜像会先拒绝，
驱动那道 fold 只是纵深防御（同一份配置两边都判重叠）。

## 9. 与 design.md 的偏差与需要裁决/遗留

| # | design.md 写法 | 实际实现 | 原因 |
|---|---|---|---|
| 1 | `block` 在 classify 里置 `FWP_ACTION_BLOCK` | redirect 层只下决策并放行；真正的拒绝由 ALE_AUTH_CONNECT 的 `FWP_ACTION_BLOCK` filter 执行 | 该层不执行拦截（§2.1 实测）；callout 方案会截胡防火墙或把连接全拒（§2.2） |
| 2 | auth 层用 callout 共用 matcher | auth 层没有 callout，改为按规则编译 filter | 终止型 callout 无法既阻断命中项、又让其他过滤器正常仲裁 |
| 3 | 进程路径取 `FWPS_METADATA_FIELD_PROCESS_PATH`（DOS 路径） | 取 `ALE_APP_ID`（UTF-16 device path），手工转 UTF-8、折叠 ASCII 大小写 | 该 metadata 在 redirect 层不可用（§3） |
| 4 | 四道闸顺序 port → flag → query → pid | port → previousVersion → query → flag → pid | query 在 flag 前，闸 2/3 才都能被观测（§5） |
| 5 | rundown 保护 | 自旋锁 + 引用计数 + 退役链 | `EX_RUNDOWN_REF` 在 DISPATCH_LEVEL 不可用（§1） |
| 6 | 只提“V4/V6 两个层” | 另加 AUTH_CONNECT 两个层（只有 filter，没有 callout） | block 语义需要（§2） |

遗留：
- 闸 2 无法单独做负向验证（§5）：**目前只由组合用例覆盖，未单独钉住**。
- 用户态进程选择器需要改成 app-id 形态（`FwpmGetAppIdFromFileName0` + ASCII 折叠 + UTF-8），
  由父代理另派；本批只保证内核侧双向往返与中文路径实测。
- 用户态编译器需要复刻 §2.4 的**顺序冲突**与 **fallback=block 冲突**检查（测试侧已有镜像；
  驱动是最后一道闸）。
- `FwpsQueryConnectionRedirectState0` 需要 redirect records，records 由代理设置；没有 records
  的代理出站连接依赖 self-PID 闸（已在 gates 里分别验证）。

## 10. 构建/API 坑（给后来者）

1. **`FWP_V4_ADDR_AND_MASK` 是主机字节序**（`addr` 与 `mask` 都是；fwptypes.h 原文
   "specifies IPv4 address and mask in host order"）。写成网络序会在 `FwpmFilterAdd0` 得到
   `FWP_E_INVALID_NET_MASK (0x8032001F)`。IPv6 用 `FWP_V6_ADDR_AND_MASK` 的 16 字节地址，无此问题。
2. **`RtlUnicodeToUTF8N` / `RtlUTF8ToUnicodeN` 只允许 PASSIVE_LEVEL**。classify 在
   DISPATCH_LEVEL，UTF-16 → UTF-8 必须手写（纯算术、无分配）；UTF-8 → UTF-16 只在 IOCTL
   （PASSIVE）里用 RTL。
3. **测试程序改名会找不到程序集**：.NET apphost 把 `wfprobe.dll` 编进了自身，复制/改名为
   `工具.exe` 后启动会报 "The application to execute does not exist"（exit=0x8000809A 一类）。
   要测中文路径就保留文件名、把整个程序放进中文目录。
4. **`ALE_CONNECT_REDIRECT` 不执行拦截**：不要在这个层尝试 `FWP_ACTION_BLOCK` 或终止型
   callout；block 必须落到 `ALE_AUTH_CONNECT`（§2）。
5. **`FwpmGetAppIdFromFileName0` 不支持 >MAX_PATH 的路径**：长路径进程的 app id 需要用户态
   自己拼（卷前缀 + 相对路径），否则规则里的进程选择器根本拿不到。
6. **0x807 是诊断接口，发布前可移除**：它只读、无状态、admin-only，存在的唯一理由是
   `CreateProcess`/`FwpmGetAppIdFromFileName0` 都拒绝 >MAX_PATH，端到端造不出 511 字节的
   真实进程；正式路径的 511/512 仍由 `wfprobe rules` 的构造断言钉住。移除它不影响产品路径。
7. **变异脚本的“变异”本身必须能编译**：最初用 early return 做 block 变异，`/WX` 以
   C4702（unreachable code）拒绝构建，脚本会把它记成“变异失败”而不是“测试变红”。改用
   运行期恒真条件。
