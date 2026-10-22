# WFP connected UDP redirect record 引用计数泄漏（Windows 11 26100）

> 状态：**已定性，未修复**。这是 `tcpip.sys` 的行为/缺陷，不是 WinForward.Kernel 驱动侧
> 可以安全修复的问题。驱动侧没有手动释放 `localRedirectContext` 的入口。
>
> 适用环境：Windows 11 / WinLTSC `10.0.26100.9711`，测试签名，Debug/x64 驱动；
> 结论来自公开符号 + `tcpip.sys` 反汇编 + live kernel dump。WFP/tcpip 闭源，没有源码可读。
> 文中地址取自 2026-10-22 的 live dump（`tcpip` 基址 `fffff801`b4800000`），跨 boot 请按
> “符号 + 偏移”理解，不要硬编码绝对地址。

---

## 1. 结论摘要

- 驱动在 `ALE_CONNECT_REDIRECT` 为 connected UDP 分配 36 字节 `redirect_context`（池标签
  `WfPr`），所有权交给 WFP。
- 该 36B 只在 `tcpip!AleRedirectRecordFree` 里释放：record 的 `+0x1E0` 保存 context 指针，
  `+0x1F0` 是 32 位 refcount；refcount 归零时
  `ExFreePoolWithTag([record+0x1E0], 0)` 才会释放 36B。
- **connected UDP 每发一条 datagram，`tcpip!ProcessALEForTransportPacket` 都会调用
  `AleRedirectRecordReference` 给 record 加 1，但重定向完成路径没有配对的
  `AleRedirectRecordDereference`。**
- 因此 record refcount 随 datagram 数线性增长，永远不归零；36B `WfPr` 和 0x228 `Arcl`
  record 一起滞留，直到重启。`sc stop` 不会回收。
- 实测三个样本：1 / 3 / 100 条 datagram → record refcount **1 / 3 / 100**。
- `+0x28 = 00 00` 说明 record 是 hashtable record，且 `WfpHashtableDestroy` 回调已经执行过
  （hashtable 那份引用已经减掉）；泄漏的是每包额外加的那一份。

## 2. 影响

| 维度 | 影响 |
|---|---|
| 功能 | 未观察到 redirect / permit / block 决策错误；新关联仍正常工作 |
| 内存 | 每个 connected 关联至少 36B `WfPr` + 0x228（含池头 0x230）`Arcl` record；不随 datagram 数增加内存 |
| 引用计数 | 32 位 refcount 随 datagram 线性增长；理论上 2^32 后回绕，存在 UAF/提前释放风险 |
| 运维 | `sc stop` 不回收；只有重启清零；驱动升级/重装不会清掉旧 record |
| 安全 | 未观察到跨关联数据泄漏或 fail-open；不是“绕过策略”的安全漏洞 |

粗算：100k datagram/s 的 connected UDP 流，约 11.9 小时达到 2^32；这是理论上限，未做长稳
实验确认回绕后的行为。即使不溢出，长期不重启也会累积每个关联的 36B + 0x228。

## 3. 最小复现

VM 上安装当前 Debug 驱动后：

```powershell
# 1 条 / 3 条 / 100 条 datagram 各跑一次；每次创建一个 connected UDP 关联
wfprobe.exe udp-rules --only U2 --datagrams 1   --timeout-ms 5000  --connected-window-ms 1000
wfprobe.exe udp-rules --only U2 --datagrams 3   --timeout-ms 5000  --connected-window-ms 1000
wfprobe.exe udp-rules --only U2 --datagrams 100 --timeout-ms 30000 --connected-window-ms 1000

# 2 确认池标签增长
wfprobe.exe pooltag --tag WfPr
wfprobe.exe pooltag --tag Arcl
wfprobe.exe pooltag --tag Curt

# 3 抓 live dump（mirror dump 在本机因资源不足失败，用 -ml）
livekd64.exe -k C:\wfdbg\kd.exe -ml -o C:\wfdbg\live.dmp

# 4 在 dump 里找 record
cdb.exe -z C:\wfdbg\live.dmp -y "srv*C:\symbols*https://msdl.microsoft.com/download/symbols" ^
  -c "!poolfind Arcl -nonpaged; q"
```

`!poolfind` 会列出 `Arcl` record 地址；对每条 record 读：

```text
dq <record>+1E0 L1      ; localRedirectContext 指针
dd <record>+1F0 L1      ; refcount
db <record>+28  L2      ; 00 00 = hashtable 标志已清（不是 latch 的 00 01）
db poi(<record>+1E0) L 24
```

本次 `!poolfind` 只列出了 3 条 `Arcl`（扫描较慢，约 10 分钟；13 条 outstanding 中只列出 3 条），
但这 3 条正好覆盖 1 / 3 / 100 三个样本；10 条样本未在列表中，不影响线性结论。

## 4. 实验证据

环境：WinLTSC `10.0.26100.9711`；同一 boot；驱动 ABI v3；`wfprobe udp-rules --only U2`。

池标签（跑完 1/3/100 后）：

```text
WfPr: nonpaged_allocs=55 frees=42 outstanding=13 bytes=832
Arcl: nonpaged_allocs=246 frees=233 outstanding=13 bytes=7488
Curt: nonpaged_allocs=22 frees=22 outstanding=0
```

`Curt=0` 说明 `tcpip!CreateUdpConnectRedirectCallbackContext` 的目标地址副本已经释放，
36B 不是 `Curt` 拷贝泄漏。

找到的三条 record：

| datagram 数 | record (Arcl) | context (WfPr) | context PID | 原始端口 | 协议 | `+0x1F0` | `+0x28..29` |
|---:|---|---|---:|---:|---:|---:|---|
| 1 | `ffff9209556cd610` | `ffff920952fee350` | 3588 (0x0e04) | 63434 (0xf7ca) | 17 UDP | **1** | `00 00` |
| 3 | `ffff920957b1a4d0` | `ffff920959a81e90` | 4436 (0x1154) | 65272 (0xfef8) | 17 UDP | **3** | `00 00` |
| 100 | `ffff92095887cda0` | `ffff920959a79410` | 6408 (0x1908) | 63524 (0xf824) | 17 UDP | **100** | `00 00` |

context 字节（100 条样本）：

```text
57 46 43 31 00 00 00 00 08 19 00 00 01 00 24 f8  WFC1..........$.
04 11 00 00 7f 00 00 01 00 00 00 00 00 00 00 00  ................
```

解码：magic `WFC1`、rule_index 0、PID 6408、version 1、原始端口 63524、family 4、
protocol 0x11=17、地址 `127.0.0.1`。

## 5. 反汇编链

### 5.1 36B 的释放点

`tcpip!AlepCreateRedirectRecord`（本 dump `fffff807`85da8520`，符号 + 偏移）：

- `+0x31b`：`mov dword [record+1F0h], 1`（refcount 初值 1）
- `+0x2ca/+0x2d8`：`[record+1E0h] = localRedirectContext`、
  `[record+1E8h] = localRedirectContextSize`
- `+0x3cb`：`WfpHashtableInsert([endpoint+2B0h], &record+0x10)`
- `+0x416`：`lock add [record+1F0h], 1`，把 caller 那份交出去（`*arg12 = record`）

`tcpip!AleRedirectRecordFree`（`85dbd730`）：

```text
85dbd740  mov rcx,[record+1E0h]
85dbd747  test rcx,rcx
85dbd74a  jne 85dbd7b2
...
85dbd7b2  xor edx,edx
85dbd7b4  call ExFreePoolWithTag(rcx, 0)   ← 36B WfPr 在这里释放
```

tag 传 0，所以 `tcpip.sys` 里不需要出现 `WfPr` 字节；这也解释了为什么搜 `WfPr` 找不到。

### 5.2 每包 +1 的位置

`tcpip!ProcessALEForTransportPacket` 的 connected UDP 分支（本 dump
`fffff807`85e5c7d1` 附近）：

```text
85e5c78f  call IsUdpPacketGoingToConnectedPeer
85e5c79d  je   <非 connected 路径>
...
85e5c7d1  call ProcessConnectedUdpRedirect
...
85e5c7f1  mov  [rax+30h], rcx        ; record 存入包上下文
85e5c7f5  mov  rcx,[r14]             ; endpoint
85e5c7f8  mov  rcx,[rcx+1C0h]        ; record
85e5c7ff  call AleRedirectRecordReference   ; lock inc [record+1F0h]
```

`AleRedirectRecordReference`（`85dc0784`）本身只有一条：

```text
lock inc dword ptr [rcx+1F0h]
```

`AleRedirectRecordDereference`（`85dbd700`）只有减到 0 才调用 `AleRedirectRecordFree`：

```text
lock xadd dword ptr [rcx+1F0h], -1
cmp eax,1
je  AleRedirectRecordFree
```

### 5.3 容易混淆的那次引用是配平的

`ProcessConnectedUdpRedirect` 通过 `TlShimGetRedirectRecordForNonTCPEndpoint`
（`85dd4348`）取 record，`85dd4436` 会 `lock inc [record+1F0]`；这次引用在
`85cc000a → 85cc0294` 有配对 `AleRedirectRecordDereference`，错误路径也汇到那里。
**这次是配平的，不是泄漏点。**

泄漏的是 `85e5c7ff` 的 `AleRedirectRecordReference`：它把 record 存进 `[rax+30h]` 的包上下文，
但在 connected UDP 重定向完成/包上下文销毁路径上，没有找到配对的
`AleRedirectRecordDereference`。

### 5.4 为什么只有 connected UDP

- `IsUdpPacketGoingToConnectedPeer` 为真才进入 `ProcessConnectedUdpRedirect` 和
  `AleRedirectRecordReference`；unconnected UDP 不走这条分支，1s 内释放。
- TCP 没有这条每包 connected UDP 引用；走连接 endpoint 的 record 生命周期。
- `AlepCreateRedirectRecord` 的 hashtable record 本身由
  `WfpAlepEndpointCleanup` 的 `WfpHashtableDestroy` 释放；本次样本 `+0x28=00 00`
  说明 hashtable 销毁已经发生，不是“Protocol==6 跳过 hashtable”。

## 6. 已排除的方向

| 方向 | 排除依据 |
|---|---|
| `tcpip!CreateUdpConnectRedirectCallbackContext` 的 `Curt` 副本泄漏 | `Curt` outstanding = 0；`Curt` 复制的是目标 sockaddr，不是 36B `localRedirectContext` |
| `ProcessConnectedUdpRedirect` 的 `TlShimGet` record 引用泄漏 | `85dd4436` 的 +1 在 `85cc0294` 有配对 deref，错误路径也配平 |
| `Protocol==6` endpoint 跳过 `+0x2B0` hashtable | 样本 `+0x28=00 00`，record 曾在 hashtable，且 `WfpHashtableDestroy` 已执行；refcount 等于 datagram 数 |
| remote ALE endpoint `+0x2E0` 缓存引用 | 数据面 getter/setter 引用配平；缓存泄漏最多泄漏 remote endpoint，不能解释 record refcount = N |
| 每关联固定漏一个引用 | 1/3/100 条 datagram → refcount 1/3/100，明显随 datagram 线性增长 |

## 7. 证据边界

- 公开符号 + 反汇编，没有 `tcpip.sys` 源码；函数语义根据指令和调用约定推断。
- 地址来自 26100.9711 的 live dump；不同 build/boot 地址会变，引用时用符号 + 偏移。
- `!poolfind` 本次只列出 3 条 `Arcl`，10 条样本未列出；1/3/100 三个点已足够证明线性，
  但如果要写进 Microsoft case，建议补一次 1000 条 datagram 的长稳样本，确认 refcount
  继续线性并观察是否出现 32 位回绕。
- 32 位回绕后的 UAF 是理论风险，尚未实测。

## 8. 规避与处置

### 驱动侧

- **不要手动 `ExFreePool` 36B context**：所有权在 tcpip/WFP，硬释放会 double free/UAF。
- 如果产品必须彻底避免该泄漏，只能评估“connected UDP 不使用 `localRedirectContext`”：
  这会失去 listener 侧取原始目的地的能力，等于放弃 connected UDP redirect；替代方案是
  block + 用户态接管。
- 短期接受限制：每个 connected 关联 36B + 0x228 直到重启；监控 `WfPr` / `Arcl`，
  长期不重启的环境安排维护重启。
- 向 Microsoft 报 case，附本文件的实验、地址和 `wfprobe` 复现步骤。

### 运维

- 不要把 `WfPr` / `Arcl` 的 outstanding 当作“必须为 0”；应按启动后累计量监控。
- `sc stop` 不回收，驱动升级/重装不会清掉旧 record；需要重启。

## 9. 复验命令

```powershell
# 创建 N 条 connected UDP datagram
wfprobe.exe udp-rules --only U2 --datagrams N --timeout-ms 30000 --connected-window-ms 1000

# 池标签
wfprobe.exe pooltag --tag WfPr
wfprobe.exe pooltag --tag Arcl
wfprobe.exe pooltag --tag Curt

# live dump
livekd64.exe -k C:\wfdbg\kd.exe -ml -o C:\wfdbg\live.dmp
cdb.exe -z C:\wfdbg\live.dmp -y "srv*C:\symbols*https://msdl.microsoft.com/download/symbols" `
  -c "!poolfind Arcl -nonpaged; q"
```

在线内核调试时可以在以下符号下断，统计 inc/dec 配对：

- `tcpip!AleRedirectRecordReference`（`85dc0784`）
- `tcpip!AleRedirectRecordDereference`（`85dbd700`）
- `tcpip!AleRedirectRecordFree`（`85dbd730`）
- `tcpip!ProcessALEForTransportPacket` 的 `85e5c7ff`

## 10. 相关记录

- 任务归档：
  `.trellis/tasks/archive/2026-10/10-22-host-udp-verification/research/connected-context.md`
- Host UDP 实现事实：`.trellis/spec/backend/wfp-architecture.md` §7.11
- 驱动 README：`src/driver/README.md` 的“生命周期”一节
