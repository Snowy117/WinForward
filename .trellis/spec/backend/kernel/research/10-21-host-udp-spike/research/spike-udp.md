# Host UDP Spike：findings（2026-10-21）

> 环境：WinLTSC（Windows 11 IoT Enterprise LTSC，10.0.26100.0），VM 192.168.100.2。
> 代码：本机 HEAD `7b28d33`，用 `git archive` 导出到 VM 的 `C:\workspace\WinForward.Kernel`；
> 驱动 `wfprobe.sys`（Debug/x64，测试签名），用户态 `wfprobe.exe`（Release，net11.0）。
> 所有临时改动在报告前已还原，见 §6。
>
> 结论摘要：
>
> | 项 | 结论 |
> |---|---|
> | S1 classify 频次 | **按关联（association）一次，不按 datagram**：同一 socket 同一目的地 10 条 datagram 只触发 1 次 classify / 1 个 context |
> | S2 context 生命周期 | 客户端 socket 关闭后**立即**释放（<1s）；socket 保持打开 120s 也不释放；`sc stop wfprobe` 后本实例分配全部归还（干净启动下 outstanding=0） |
> | S3 UDP block | **auth filter 能拦住 UDP（对端收不到包）**，但应用侧 `connect`/`send`/`sendto` 全部成功、没有错误码——**静默丢包**。TCP 对照为 `AccessDenied(10013)` |
> | S4 IPv6 / dual-mode | **通过**（工具临时扩展后）：`[::]` dual-mode listener、`sockaddr_in6`、cmsg level 0/41 都可用；context family 取决于 WFP 实际走的层（见 §4） |
> | STOP | 按 PRD 判据（“拦不住 UDP”）**不 STOP**；但设计 §5.2 的“应用收到 socket 错误、不静默丢包”**不成立**，需要在父任务里重新决策 |

## 0. 复现基线

```powershell
# 本机
git archive --format=zip -o /tmp/wf-repo.zip HEAD
# VM
New-Item -ItemType Directory -Force -Path C:\workspace | Out-Null
Expand-Archive -Path C:\workspace\wf-repo.zip -DestinationPath C:\workspace\WinForward.Kernel -Force
pwsh -File C:\workspace\WinForward.Kernel\tools\windows\load-driver.ps1 -Root C:\workspace\WinForward.Kernel
dotnet build C:\workspace\WinForward.Kernel\tools\wfprobe\wfprobe.csproj -c Release -v:q
```

驱动与服务状态（`sc.exe query wfprobe`）：

```text
STATE              : 4  RUNNING
```

注意：`load-driver.ps1` 只构建/安装驱动；`wfprobe.exe` 由 `dotnet build tools\wfprobe\wfprobe.csproj -c Release` 构建
（`gate.ps1` 里也是这一步）。实验命令使用完整路径
`C:\workspace\WinForward.Kernel\tools\wfprobe\bin\Release\net11.0\wfprobe.exe`。

实验期间重启过一次 VM 以取得干净的非分页池基线（§2 的原因）。重启后驱动为 `STOPPED`，用
`sc.exe start wfprobe` 拉起，未重新安装驱动。

## 1. S1：UDP classify 频次

### 命令

```powershell
# 1) listener（relay 自己打印 LISTENER-PID）
wfprobe udp-relay --bind 0.0.0.0 --port 5555 --count 40 --linger-ms 30000
# 2) 配置
wfprobe control --enabled 1 --port 5555 --listener-pid <relay-pid>
wfprobe udp-probe --enable 1 --port 5555 --pid <relay-pid> --return-mode off
# 3) 依次发送
wfprobe udp-client --remote 1.2.3.4:443  --mode unconnected --count 1  --timeout-ms 200
wfprobe udp-client --remote 1.2.3.4:443  --mode unconnected --count 10 --timeout-ms 200
wfprobe udp-client --remote 1.2.3.4:4444 --mode unconnected --count 1  --timeout-ms 200
wfprobe udp-client --remote 1.2.3.5:443  --mode unconnected --count 1  --timeout-ms 200
wfprobe udp-client --remote 1.2.3.4:4455 --mode connected   --count 3  --timeout-ms 200
# 4) 每次之后读
wfprobe udp-probe-stats
wfprobe stats
```

### 观察

以每次发送前后的增量计（`stats.classify`、`udp-probe-stats` 的 `seen/redirected/table`、
`stats.contexts_allocated`）：

| 场景 | datagram 数 | classify 增量 | seen 增量 | redirected 增量 | contexts 增量 | table（累计） |
|---|---:|---:|---:|---:|---:|---:|
| 新进程、1 条到 `1.2.3.4:443` | 1 | +1 | +1 | +1 | +1 | 1 |
| 同一进程/socket、10 条到同一目的地 | 10 | **+1** | **+1** | **+1** | **+1** | 2 |
| 两个新进程、两个新目的地各 1 条 | 2 | +2 | +2 | +2 | +2 | 4 |
| 新进程 connected、3 条到 `1.2.3.4:4455` | 3 | **+1** | **+1** | **+1** | **+1** | 5 |

原始摘录：

```text
[udp-client] #0..#9 sent=4B recv=4B from=192.168.77.2:5555 payload=ping
STEP 10 datagrams: udp_seen=2 udp_redirected=2 udp_fail=0 table=2 | classify=2 permitted=0 redirected=2

[udp-client] #0..#2 sent=4B recv=4B from=127.0.0.1:5555 payload=ping
STEP connected 3: udp_seen=5 udp_redirected=5 udp_fail=0 table=5 | classify=5 permitted=0 redirected=5
```

relay 侧看到 10 条 datagram 全部来自同一个本地端口（`192.168.77.2:63363`），3 条 connected datagram 也来自同一端口
(`127.0.0.1:63366`)，每条都带 36 字节 context，地址/端口相同。

另一个细节：`stats` 的 `permitted` 在 UDP 重定向时**不增加**（S1 结束时 `classify=5 permitted=0 redirected=5`）。
实验分支在 `udp_probe_try_redirect` 成功时直接返回，没有走 `permitted` 计数。正式实现里
`udp_classify_calls == udp_permitted + udp_blocked + udp_redirected` 这条不变式需要显式计数，不能沿用实验分支。

### 结论

- 在 `ALE_CONNECT_REDIRECT_V4/V6` 上，UDP 的 classify **按关联触发，不按 datagram**。关联的边界是
  “本地端点 + 远端目的地”这一对；同一 socket 对同一远端的后续 datagram 复用第一次的决策与 context。
- 1 次 classify 对应 1 个 36 字节 context、1 条 `seen`/`redirected` 计数；10 条 datagram 不会让 table 或池计数
  线性增长。
- 换进程（新本地端口）、换远端地址或端口，都会产生新的关联与新的 classify/context。

### 对 B1/B2 的影响

- U3 的 `udp_classify_calls` 增量断言**不能写成 ≤ N** 这种弱断言，也不要写成“每条 datagram 一次”。实测口径：
  **同一客户端 socket 对同一目的地 N 条 datagram 的增量为 1**；要制造 K 个关联就开 K 个客户端进程/新 socket。
- 建议断言：(a) 单 socket N 条 → `udp_classify_calls` / `udp_redirected` / `contexts_allocated` 各 +1；
  (b) 两个新目的地 → 各 +2；(c) `contexts_allocated` 与 `udp_redirected` 增量相等。
- U3 里“context 分配不随 datagram 线性增长”可以直接断言 `contexts_allocated` 只随关联数增长；本 spike 已给出绑定关系。

## 2. S2：redirect context 生命周期与 `WfPr` 池标签

### 背景与零基线

- `WfPr` 是 `'rPfW'`，同时被三处使用：redirect context（36B）、实验注入路径的 NBL 池
  （`NdisAllocateNetBufferListPool` / `PoolTag=WfPr`，一次 4480B）、实验注入的 buffer/allocation。
- 干净启动、驱动未启动时 `wfprobe pooltag --tag WfPr` 输出 `[pooltag] 未找到 tag=WfPr`
  —— 符合 `PoolTags.Zero` 的语义：**null 按 0 算**。
- 干净启动、驱动刚启动时基线是 `allocs=1 frees=0 outstanding=1`：这 1 个是实验注入路径的 NBL 池，
  **不是** redirect context。正式实现删除注入路径后，这个基线才真正是 0。

### 命令与时间序列

```powershell
wfprobe udp-probe --enable 0
wfprobe control --enabled 0 --clear-self
# 基线
wfprobe pooltag --tag WfPr
# relay + 配置
wfprobe udp-relay --bind 0.0.0.0 --port 5555 --count 200 --linger-ms 240000
wfprobe control --enabled 1 --port 5555 --listener-pid <relay-pid>
wfprobe udp-probe --enable 1 --port 5555 --pid <relay-pid> --return-mode off
# S2a：20 个一次性客户端进程，5 个远端轮换（每个进程 1 条）
# S2b：一个长活 socket，向 5 个远端各发 4 条，保持打开 120s 后再关闭
# 每个观测点读 wfprobe pooltag --tag WfPr（同时读 stats.contexts_allocated）
```

干净启动下的一次完整序列（`outstanding` 包含 NBL 池的 1）：

| 观测点 | allocs | frees | outstanding | contexts_allocated |
|---|---:|---:|---:|---:|
| 驱动刚启动（基线） | 1 | 0 | 1 | 0 |
| 打开 probe 配置后 | 1 | 0 | 1 | 0 |
| S2a：20 个一次性客户端发完（已退出） | 21 | 20 | 1 | 20 |
| S2a +1s / +5s / +30s | 21 | 20 | 1 | 20 |
| S2b：长活 socket 向 5 个远端发 20 条（socket 仍打开） | 26 | 20 | 6 | 25 |
| S2b 打开 +30s / +60s / +90s / +120s | 26 | 20 | 6 | 25 |
| S2b `Close()` 之后立即 | 26 | 25 | 1 | 25 |
| 关闭 +1s / +5s / +30s | 26 | 25 | 1 | 25 |
| 关闭 relay、`sc.exe stop wfprobe` 后 | 26 | 26 | **0** | — |
| `sc.exe start wfprobe` 之后 | 27 | 26 | 1 | 0 |

补充证据（重启 VM 之前的一次受污染基线）：进入 S2 前 `WfPr` 已经是 `151/138/13`，`sc stop` 之后只回到
`180/168/12` 而不是 0。也就是说有 12 个块是**历史驱动实例遗留在非分页池里的**，与当前实例无关。
这批历史残留与实验注入路径的分配最吻合（NBL / buffer / allocation 都以 `WfPr` 打标），干净启动后基线
只剩 `1`（NBL 池），`sc stop` 后能归零。正式实现删除注入路径后，`WfPr` 应当只剩 redirect context。

### 结论

- context 的分配是**每个 UDP 关联一次**，与 S1 的 classify 次数一一对应。
- 释放由**客户端 socket 关闭**驱动：关闭后立即归还（本次观测在 1s 内 frees 追平），不需要等 WFP idle 超时。
- 客户端 socket 保持打开时，context **不会**因为 120s 无流量而释放（显式测了 30/60/90/120s）。
- 驱动停止时，当前实例分配的所有 `WfPr` 都被回收（`outstanding=0`）；驱动重新启动后 NBL 池再占 1。
- 实验注入路径会泄漏/留存 `WfPr` 块，不能用“跨驱动重启的绝对 outstanding”做断言。

### 对 B1/B2 的影响

- U12 的池断言建议：
  - 开始时取基线（`null` 视为 0）；断言 `allocs 增量 == udp_redirected 增量`；
  - 关闭客户端 socket 后 1s 内断言 `outstanding` 回到基线（不需要等 WFP idle）；
  - `gate.ps1` 卸载驱动后断言 `outstanding == 0`，但**必须从干净启动/干净安装开始**；
  - 在删除实验注入路径之前，绝对基线要减掉 NBL 池的 1（或者干脆只断言增量）。
- “outstanding ≤ 活跃关联数”在这个实现里比“等 idle”更贴近事实；关联的寿命跟随 socket，而不是超时。

## 3. S3：UDP block 的 auth 行为（临时改动）

### 临时改动

- `src/common/rule_blob.h`：新增 `rule_protocol_udp = 17`，`is_known_protocol` 接受 17。
- `tools/wfprobe/UdpBlockProbe.cs`（新文件）+ `Program.cs` 注册 `udp-blockprobe`：
  构造 `protocol=17`（或 TCP 对照时 `protocol=6`）的 block 规则，分别用 connected/unconnected UDP 与 TCP 发往
  本机 Linux 监听端（`192.168.100.4`，端口 40003/40004）。
- `block_filters.cpp` **不需要改**：它对非 `any` 协议直接下发 `selected.protocol`，17 会原样进入
  `FWPM_CONDITION_IP_PROTOCOL`。

### 命令

```powershell
# 本机 Linux：监听 udp/40003、udp/40004、tcp/40003、tcp/40004
python3 /tmp/udp_listener.py
# VM
wfprobe udp-blockprobe --host 192.168.100.4 --rule-port 40003 --control-port 40004 --timeout-ms 800
```

### 观察

| 用例 | 应用侧结果 | 对端是否收到 | 备注 |
|---|---|---|---|
| permit-only 控制组，connected → 40003 | `send=ok sent=11B` | 收到 `permit-conn` | 证明可达 |
| permit-only 控制组，unconnected → 40003 | `sendto=ok sent=13B` | 收到 `permit-sendto` | 证明可达 |
| `protocol=17` block 40003，connected | `connect=ok send=ok sent=10B`，随后 `recv=TimedOut(10060)` | **未收到** `block-conn` | 静默丢弃 |
| `protocol=17` block 40003，unconnected | `sendto=ok sent=12B` | **未收到** `block-sendto` | 静默丢弃 |
| `protocol=17` block 40003，connected → 40004 | `send=ok sent=9B` | 收到 `block-ctl` | 端口选择性 |
| `protocol=17` block 40003，unconnected → 40004 | `sendto=ok sent=16B` | 收到 `block-ctl-sendto` | 端口选择性 |
| `protocol=6` block 40003，TCP connect | `AccessDenied(10013)`，10ms | 未建连 | auth filter 路径可用 |
| `protocol=6` block 40003，TCP → 40004 | `ok`，1ms | 建连成功 | 对照 |

VM 侧监听端日志（关键几行）：

```text
21:23:57 UDP udp:40003 from=('192.168.100.2', 53802) bytes=11 data=b'permit-conn'
21:23:57 UDP udp:40003 from=('192.168.100.2', 53803) bytes=13 data=b'permit-sendto'
21:23:57 UDP udp:40004 from=('192.168.100.2', 53806) bytes=9  data=b'block-ctl'
21:23:58 UDP udp:40004 from=('192.168.100.2', 53807) bytes=16 data=b'block-ctl-sendto'
21:23:58 TCP accept tcp-listen:40004 from=('192.168.100.2', 49725)
# 40003 在 block 生效期间一条也没有
```

驱动计数器：

```text
# UDP 阶段（6 次 send/sendto：控制组 2 + block 组 4）
classify=6 permitted=0 blocked=0 deferred_blocks=0 matched_block=0 non_tcp=6 contexts=0
# TCP 阶段
classify=2 permitted=1 blocked=1 deferred_blocks=1 matched_block=1
```

`blocked`/`deferred_blocks` 不涨是实验分支的既有行为：UDP classify 在 `probe_classify.cpp` 的
`permitted_non_tcp` 早退，根本走不到 matcher。真实拒绝发生在 `ALE_AUTH_CONNECT` 的 filter 上。
TCP 阶段 `blocked`/`deferred_blocks` 各 +1，说明 classify 侧的计数语义与 auth filter 是两条独立路径。

### 结论与 STOP 判定

- **拦截本身成立**：`protocol=17` 的 `ALE_AUTH_CONNECT` BLOCK filter 确实让报文到不了对端，且只拦匹配的
  远端端口，不影响其他端口。
- **应用不可见**：UDP 的 `connect`、`send`、`sendto` 全部返回成功；随后 300ms 的 `Receive` 得到
  `TimedOut(10060)`，没有任何 socket 错误。按设计 §5.2 的原文（“应用的 `send`/`connect` 收到 socket 错误，
  **不静默丢包**”）判定：**该假设不成立**。
- 按 PRD 的 STOP 判据（“`ALE_AUTH_CONNECT` 的 BLOCK filter 拦不住 UDP”）——**不触发 STOP**：拦得住。
- 但如果产品坚持 D1 的“应用可见错误”，那这条路线在语义上就是 STOP：在这台机器上，UDP block 的
  auth filter 只能做到静默丢包。
- 这是父任务必须做的产品取舍，不是实现 bug：要么接受 UDP block = 静默丢包（与 TCP 的
  `AccessDenied(10013)` 语义不同），要么换机制（在本 spike 范围内没有找到能让 UDP `send` 报错的 WFP 路径）。

### 对 B1/B2 的影响

- U6 的错误码断言要改：**不能断言 `send` 失败或某个错误码**。应断言：
  - `send`/`sendto` 成功（应用无感）；
  - 对端/listener 收不到被 block 的 datagram；
  - 未被 block 的端口正常收到（选择性）。
- `udp_blocked` 的语义：classify 侧可以记“决策为 block”（正式实现移除 UDP 早退后成立），但**应用可见的
  拒绝语义不在 auth 层发生**。事件/计数器要写清楚“deferred block”与“实际丢弃”的差别。
- 若父任务把 D1 改成“UDP block = 静默丢弃”，这个实现可以直接用；否则要在设计评审里明确 STOP/替代方案。

## 4. S4：IPv6 / dual-mode（尽力而为）

### 工具临时改动（全部已还原）

- `tools/wfprobe/UdpSockets.cs`
  - `CreateListener`：按地址族选择 setsockopt level；IPv6 用 `SocketOptionLevel.IPv6` + `IPV6_WFP_REDIRECT_CONTEXT(70)`；
    `[::]` 时置 `DualMode = true`，并在 dual-mode 下尽力补设 `IP` level；
  - `FindControl`：`cmsg_level` 接受 `0`（`IPPROTO_IP`）与 `41`（`IPPROTO_IPV6`）；
  - `ParseEndpoint`：支持 `sockaddr_in6`（family=`AF_INET6`，含 scope id）。
- `tools/wfprobe/UdpProbeCommands.cs`
  - `ParseEndPoint` 支持 `[addr]:port`（含 `[::ffff:1.2.3.4]:443`）；
  - `ReceiveFrom` 的源端点按地址族用 `IPv6Any`；
  - 客户端对 v4-mapped 目的地置 `DualMode = true`（否则 `sendto` 报
    “The requested address is not valid in its context”）。
- 驱动**没有**改动：V6 的 callout/filter 已在 `probe_driver.cpp` 注册，`redirect_target.cpp` 已支持 v6。

### 命令

```powershell
wfprobe udp-relay --bind :: --port 5555 --count 10 --linger-ms 30000
wfprobe control --enabled 1 --port 5555 --listener-pid <relay-pid>
wfprobe udp-probe --enable 1 --port 5555 --pid <relay-pid> --return-mode off
wfprobe udp-client --remote "[::ffff:1.2.3.4]:443"  --mode unconnected --count 1 --timeout-ms 300
wfprobe udp-client --remote "[::ffff:1.2.3.4]:4444" --mode connected   --count 1 --timeout-ms 300
wfprobe udp-client --remote "[2001:db8::1]:443"      --mode unconnected --count 1 --timeout-ms 300
wfprobe udp-client --remote "[2001:db8::1]:4444"     --mode connected   --count 1 --timeout-ms 300
wfprobe udp-client --remote "1.2.3.4:4455"           --mode unconnected --count 1 --timeout-ms 300
wfprobe udp-probe-stats
```

### 观察

每次发送 `seen/redirected` 各 +1、table +1（`seen=4` 起是上一轮累积，增量都是 1）。relay 收到的
context（36B，按字段解析）：

| 客户端 | context family | context 原始目的地 | listener 看到的 source |
|---|---:|---|---|
| `[::ffff:1.2.3.4]:443` unconnected（dual-mode 客户端） | **4** | `1.2.3.4:443` | `[::ffff:192.168.77.2]:53813` |
| `[::ffff:1.2.3.4]:4444` connected | **6** | `::ffff:1.2.3.4:4444` | `[::1]:53814` |
| `[2001:db8::1]:443` unconnected | **6** | `2001:db8::1:443` | `[fd00:1234:5678:1::2]:53815` |
| `[2001:db8::1]:4444` connected | **6** | `2001:db8::1:4444` | `[::1]:53816` |
| `1.2.3.4:4455` unconnected（纯 v4 客户端） | **4** | `1.2.3.4:4455` | `[::ffff:192.168.77.2]:53817` |

原始 context 示例：

```text
# v4-mapped connected：family=06，地址 = ::ffff:1.2.3.4
57464331 FFFFFFFF 741F0000 0100 5C11 06 11 0000 00000000000000000000FFFF01020304
# native v6 unconnected：family=06，地址 = 2001:db8::1
57464331 FFFFFFFF 541B0000 0100 BB01 06 11 0000 20010DB8000000000000000000000001
# v4-mapped unconnected：family=04，地址 = 1.2.3.4（走的是 V4 层）
57464331 FFFFFFFF E8180000 0100 BB01 04 11 0000 01020304000000000000000000000000
```

### 结论

- 工具侧补齐后，UDP 的 IPv6 / dual-mode **可用**：
  - `[::]` + `DualMode` listener 能收到 IPv6（`::1` / `fd00:...::2`）与 v4-mapped 两种来源；
  - cmsg 能取到 36 字节 context（level 0 与 41 都接受）；
  - 驱动侧 `family`、原始地址与端口正确，redirect target 按 `probe_build_redirect_target` 的分支工作。
- **重要细节**：context 的 family 取决于 WFP 实际走的层，不是“客户端是 v6 socket 就一定 family=6”。
  - v4-mapped + `sendto`（unconnected）走了 **V4** 层（family=4，地址是纯 v4）；
  - v4-mapped + `connect` 走了 **V6** 层（family=6，地址是 v4-mapped 16 字节）；
  - native v6 两种模式都是 family=6。
  设计 §4.3 里“dual-mode 客户端走 `ALE_CONNECT_REDIRECT_V6`、context family=6”这句**不完整**，U10 的断言
  要按具体的客户端 API 路径分别写。
- cmsg level 的精确值本次没有单独记录（解析同时接受 0/41），下一批可以在 `FindControl` 里打印
  `cmsg_level` 以钉住这一点；scope id 也没有覆盖链路本地地址。

### 对 B1/B2 的影响

- IPv6 / dual-mode 建议进 B3 必过项（U10）：至少覆盖 native v6（unconnected + connected）与 v4-mapped 两种路径。
- 断言要分开写：
  - native v6 → context family=6、地址 16 字节正确、listener 收到；
  - v4-mapped unconnected → family=4（本次实测）；v4-mapped connected → family=6；
  - `[::]` dual-mode listener + `IPV6_WFP_REDIRECT_CONTEXT`，并解析两种 cmsg level。
- 若后续实现把 listener 固定为 v4-only，U10 会失败——这不是驱动问题，而是工具/listener 配置问题。

## 5. 对 B1/B2 断言的建议（汇总）

1. **U3 classify 增量**：按关联断言，而不是按 datagram。单 socket 对单目的地 N 条 → `udp_classify_calls`、
   `udp_redirected`、`contexts_allocated` 各 +1；K 个新关联 → 各 +K。断言“≤ N”太弱，会把“每条一次”和
   “每个关联一次”都放过。
2. **context 池**：基线用 `PoolTags.Zero` 语义；断言 `allocs 增量 == udp_redirected 增量`；
   客户端 socket 关闭后 1s 内 `outstanding` 回到基线；`sc stop` 后断言 0（从干净启动开始）。
   实验注入路径存在时，绝对基线要减 1（NBL 池），正式删除后不需要。
3. **UDP block**：断言应用 `send`/`sendto` 成功、错误码为空、对端收不到；不要断言应用可见错误。
   `udp_blocked` 可以在 classify 侧计数，但要与“实际丢弃”分开写文档；U6 的预期必须与 D1 重新对齐。
4. **IPv6**：U10 进必过；断言按实际层写 family（见 §4），不要笼统断言“dual-mode 一定 family=6”。
5. **计数不变式**：正式实现里 `udp_classify_calls == udp_permitted + udp_blocked + udp_redirected` 需要
   在三个出口都显式计数；实验分支目前只加 `seen/redirected`，`permitted` 不动，不能直接沿用。

## 6. 临时改动与还原

| 文件 | 改动 | 状态 |
|---|---|---|
| `src/common/rule_blob.h` | 加 `rule_protocol_udp = 17`，`is_known_protocol` 接受 17 | 已 `git checkout --` 还原 |
| `tools/wfprobe/Program.cs` | 注册临时 `udp-blockprobe` 命令与 usage | 已还原 |
| `tools/wfprobe/UdpBlockProbe.cs` | 新增的临时命令实现 | 已删除（未跟踪文件） |
| `tools/wfprobe/UdpSockets.cs` | IPv6/cmsg/dual-mode 扩展 | 已还原 |
| `tools/wfprobe/UdpProbeCommands.cs` | `[addr]:port`、v6 源端点、v4-mapped DualMode | 已还原 |

本地工作区确认：

```text
$ git status --short
?? .trellis/tasks/10-21-host-udp-production/
?? .trellis/tasks/10-21-host-udp-spike/
```

VM 侧的 `C:\workspace\WinForward.Kernel` 也已用 `git archive` 的 HEAD 包重新展开，并重新构建、签名、
安装驱动（`sc query wfprobe` = RUNNING），临时文件 `UdpBlockProbe.cs` 已删除；VM 不是 git 工作区，
这里只保证它回到 HEAD 内容。

## 附录：本次用到的关键命令

```powershell
# S1
wfprobe reset
wfprobe udp-relay --bind 0.0.0.0 --port 5555 --count 40 --linger-ms 30000
wfprobe control --enabled 1 --port 5555 --listener-pid <pid>
wfprobe udp-probe --enable 1 --port 5555 --pid <pid> --return-mode off
wfprobe udp-client --remote 1.2.3.4:443 --mode unconnected --count 10 --timeout-ms 200
wfprobe udp-probe-stats; wfprobe stats

# S2
wfprobe pooltag --tag WfPr
sc.exe stop wfprobe
sc.exe start wfprobe

# S3（临时命令）
wfprobe udp-blockprobe --host 192.168.100.4 --rule-port 40003 --control-port 40004 --timeout-ms 800

# S4
wfprobe udp-relay --bind :: --port 5555 --count 10 --linger-ms 30000
wfprobe udp-client --remote "[::ffff:1.2.3.4]:443" --mode unconnected --count 1 --timeout-ms 300
wfprobe udp-client --remote "[2001:db8::1]:443" --mode unconnected --count 1 --timeout-ms 300
```
