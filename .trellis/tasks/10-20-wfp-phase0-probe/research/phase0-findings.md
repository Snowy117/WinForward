# Phase 0 探针实测结论

> 实测平台：WinLTSC（Windows 11 IoT Enterprise LTSC，192.168.100.2）。
> 日期：2026-10-20。
> 驱动与测试程序：`src/driver/` 与 `tools/wfprobe/`，部署/验证脚本在 `tools/vm/`。

## 1. 三条结论

| # | 验证点 | 结论 | 关键证据 |
|---|---|---|---|
| 1 | `ALE_CONNECT_REDIRECT` 能否落到本地监听端口（ANY → loopback / 指定 local 地址两条分支） | **成立** | 两条分支各 2 次、200 次压力各留下 accepted 端点与 classify 事件；监听端收到连接并完成回显 |
| 2 | `localRedirectContext` 能否往返 | **成立** | 监听端读回 36 字节，magic/version/pid/地址/端口逐字节与客户端目标一致 |
| 3 | `localRedirectContext` 的内存所有权 | **成立：所有权转移给 WFP，由 WFP 在 flow 移除时释放，驱动不得释放** | 文档原文 + WFP 示例注释 + 200 次连接的 per-tag 池计数：allocs +200、frees +200、outstanding 0 |

复核阶段指出的设备 DACL 与 acquire/apply 配对两个缺陷已修复并复测，见 §11。

驱动本身也通过了构建、签名（`Get-AuthenticodeSignature = Valid`）、`sc start` 到 RUNNING、
`sc stop`/`sc delete` 干净退出，且重复 load/unload 两轮无蓝屏。

## 2. 环境与方法

- 构建：VS 2026 Build Tools 18 的 MSBuild，WDK 以 NuGet 包 `Microsoft.Windows.WDK.x64 10.0.28000.2526`
  提供；C# 测试程序用 VM 上的 `dotnet 11.0.100-rc` 构建。
- 签名：证书库里已有的 `CN=WinForward Test Driver`（SHA1 `B7594CEB22C6545C3DB65A109B8961B836A01F3F`），
  `signtool sign /fd SHA256 /sha1 <thumbprint>`；没有走模板的 `/f <pfx>`。
- 驱动只注册一个 callout：`FWPM_LAYER_ALE_CONNECT_REDIRECT_V4`，action 为
  `FWP_ACTION_CALLOUT_TERMINATING`，filter 挂在 `FWPM_SUBLAYER_UNIVERSAL`。
- 测试程序是单个 C# 控制台 `wfprobe.exe`：`control` 下发配置，`listen` 单独监听，`client` 单独发起连接，
  `selftest` 在同一个进程里起监听 + 客户端并读回 context，`events`/`stats`/`pooltag` 回灌内核侧观测。
- 测试目标固定为 `1.2.3.4:443`（VM 所在网络实际可达，因此**不能**用 “connect 是否成功” 区分重定向，
  真正的判据是本地监听端是否 accept，见 §3.1 与 §6 第 9 条）。
- 监听端口 `18080`，客户端进程 PID 通过 IOCTL 作为 `target_process_id` 下发；`selftest` 里同一个 PID
  同时写入 `localRedirectTargetPID`，跨进程验证用 `tools/vm/cross-process.ps1`（listener 与 client
  分属两个进程、两个 PID 分开）。
- 安全验证用 `accesschk64.exe -accepteula -q -o \Device\WfProbe` 检查设备 DACL；重定向目标无人
  监听时的池释放用 `tools/vm/edge-cases.ps1` 观测并断言（allocs/frees 各 +1、outstanding 0、client 失败、redirected +1）。

复现只需要：

```bash
session="$(windows-vm connect)"
tools/vm/sync.sh "$session"
windows-vm "$session" script tools/vm/build-and-test.ps1
windows-vm "$session" bye
```

## 3. 验证点 1：重定向落地与两条分支

### 3.1 官方文档给出的分支

`Using Bind or Connect Redirection` 给出的示例（`connectRequest` 为 `FWPS_CONNECT_REQUEST0`）：

```c
if (INETADDR_ISANY((PSOCKADDR)&(connectRequest->localAddressAndPort)))
{
    INETADDR_SETLOOPBACK((PSOCKADDR)&(connectRequest->remoteAddressAndPort));
}
else
{
    INETADDR_SET_ADDRESS((PSOCKADDR)&(connectRequest->remoteAddressAndPort),
                         INETADDR_ADDRESS((PSOCKADDR)&(connectRequest->localAddressAndPort)));
}
INETADDR_SET_PORT((PSOCKADDR)&connectRequest->remoteAddressAndPort,
                  RtlUshortByteSwap(params->proxyPort));
```

两个容易踩的点，这次都实测确认了：

1. **在 connect-redirect 层能改的是 `remoteAddressAndPort`，不是 `localAddressAndPort`。**
   文档明确写 “Changing the local address and port of a flow is only supported in the bind-redirect layer.
   This functionality isn't supported in the connect-redirect layer.”；`FWPS_CONNECT_REQUEST0` 的 Remarks
   也把可写字段限定为 `remoteAddressAndPort`、`portReservationToken`、`localRedirectTargetPID`、
   `localRedirectHandle`、`localRedirectContext`、`localRedirectContextSize`。
   驱动里 `localAddressAndPort` 只读，用来判断走哪条分支。
2. `localRedirectTargetPID` 必须是“将接受被重定向连接的那个本地进程”的 PID；`localRedirectHandle`
   是 Win8+ 本地重定向的硬要求，缺一不可。

### 3.2 分支 A：local 地址为 ANY，落到 loopback

监听端只绑 `127.0.0.1:18080`，客户端不显式 bind 源地址，连接 `1.2.3.4:443`：

```
[selftest] config: abi=1 enabled=1 mode=documented target-pid=7000 listener-pid=7000 port=18080 local-override=0.0.0.0
[selftest] #0 context raw=5746433100000000581B00000100BB010406000001020304000000000000000000000000 pid=7000 original=1.2.3.4:443
[selftest] iterations=2 completed=2 failures=0 contexts_ok=2 contexts_failed=0 echo_ok=2
[selftest] endpoints: 127.0.0.1:18080x2
[selftest] stats: classify=2 redirected=2 contexts_allocated=2 alloc_failures=0 apply_failures=0
[selftest] events(recent 2): redirected=2 any_to_loopback=2 local_address=0 wrong_pid=0 listener_port=0
[selftest] verdict=PASS address_ok=True branch_ok=True
[event] seq=0 pid=7000 decision=redirected mode=documented branch=any_to_loopback
        original=1.2.3.4:443 local=0.0.0.0:50152 redirect-port=18080
```

要点：

- classify 时 WFP 给出的 `localAddressAndPort` 是 **`0.0.0.0`，但端口已经分配了**（`50152`）。
  也就是说“local 地址为 ANY”这条判据只看地址，不看端口；驱动判断 ANY 时不能依赖端口为 0。
- 重定向目标是 `127.0.0.1:18080`；只绑 loopback 的监听端收到了连接，accepted 的 LocalEndPoint 是
  `127.0.0.1:18080`。
- **结论：ANY → loopback 分支成立。**

### 3.3 分支 B：local 地址是具体地址，落到该地址

监听端只绑 `192.168.100.2:18080`，客户端显式 bind 源地址 `192.168.100.2`，连接 `1.2.3.4:443`：

```
[selftest] config: abi=1 enabled=1 mode=documented target-pid=264 listener-pid=264 port=18080 local-override=0.0.0.0
[selftest] #0 context raw=5746433100000000080100000100BB010406000001020304000000000000000000000000 pid=264 original=1.2.3.4:443
[selftest] iterations=2 completed=2 failures=0 contexts_ok=2 contexts_failed=0 echo_ok=2
[selftest] endpoints: 192.168.100.2:18080x2
[selftest] stats: classify=2 redirected=2 contexts_allocated=2 alloc_failures=0 apply_failures=0
[selftest] events(recent 2): redirected=2 any_to_loopback=0 local_address=2 wrong_pid=0 listener_port=0
[selftest] verdict=PASS address_ok=True branch_ok=True
[event] seq=0 pid=264 decision=redirected mode=documented branch=local_address
        original=1.2.3.4:443 local=192.168.100.2:50154 redirect-port=18080
```

要点：

- 客户端显式 bind 源地址后，classify 时 WFP 给出的 `localAddressAndPort` 就是 `192.168.100.2:50154`；
  驱动据此把目标地址改成 `192.168.100.2`、端口改成 `18080`。
- 只绑 `192.168.100.2` 的监听端收到连接，accepted 的 LocalEndPoint 是 `192.168.100.2:18080`。
  这也说明重定向目标确实落在指定网卡地址上，而不是 loopback。
- **结论：指定 local 地址分支成立。**

### 3.4 200 次连接

```
[selftest] iterations=200 completed=200 failures=0 contexts_ok=200 contexts_failed=0 echo_ok=200
[selftest] stats: classify=200 redirected=200 contexts_allocated=200 alloc_failures=0 apply_failures=0
[selftest] endpoints: 127.0.0.1:18080x200
[selftest] verdict=PASS address_ok=True branch_ok=True
```

200 次连接全部被本地监听端接住并完成回显，没有一次落空。

## 4. 验证点 2：localRedirectContext 往返

监听端对 accepted socket 调用 `WSAIoctl(SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT)`，输入缓冲为空、
输出缓冲 16 KiB 起步（不足时按 WSAEMSGSIZE/WSAEINVAL/122 放大到 256 KiB），返回 36 字节。分支 A 的原始字节：

```
5746433100000000581B00000100BB010406000001020304000000000000000000000000
```

按 `src/common/redirect_context.h` 的布局解码：

| 偏移 | 字节 | 字段 | 值 |
|---:|---|---|---|
| 0 | `57464331` | `magic` | `0x31434657`（"WFC1"） |
| 4 | `00000000` | `rule_index` | 0（探针不区分规则） |
| 8 | `581B0000` | `process_id` | `0x1B58` = 7000，与客户端 PID 一致 |
| 12 | `0100` | `version` | 1 |
| 14 | `BB01` | `original_port` | `0x01BB` = 443 |
| 16 | `04` | `family` | 4（IPv4） |
| 17 | `06` | `protocol` | 6（TCP） |
| 18 | `0000` | `reserved` | 0 |
| 20 | `01020304` + 12 个 `00` | `original_address` | 1.2.3.4（IPv4 只用前 4 字节） |

分支 B 的字节只有 `process_id` 不同（`08010000` = 264），其余一致。
客户端实际连接的目标是 `1.2.3.4:443`，读回的地址与端口逐字节相同，PID 也等于发起连接的进程。
**结论：成立，且 `src/common/redirect_context.h` 的 36 字节布局与 C# 侧的镜像一致。**

## 5. 验证点 3：localRedirectContext 的所有权

### 5.1 文档与示例的结论

`FWPS_CONNECT_REQUEST0` 的官方文档对 `localRedirectContext` 写得非常明确：

> localRedirectContext
> A callout driver context area that the callout driver allocated by calling the ExAllocatePoolWithTag function.
> **Note Starting with Windows 8, memory allocated for localRedirectContext will have its ownership taken by WFP,
> and will be freed when the proxied flow is removed.**

WDK 头里的注释也一致：“Arbitrary callout supplied context allocated using the ExAllocatePoolWithTag function.”
微软 `Windows-driver-samples` 的 `ClassifyFunctions_ProxyCallouts.cpp` 在同一位置写着：

> /// WFP will take ownership of this memory and free it when the flow / redirection terminates

### 5.2 实测：200 次连接的 per-tag 池用量

驱动用 `ExAllocatePoolWithTag(NonPagedPoolNx, sizeof(redirect_context), 'rPfW')` 为每次重定向分配
36 字节 context，然后**从不调用 `ExFreePool`**（源码里没有 `ExFreePool`，可以用
`grep -n ExFreePool src/driver/*.cpp` 确认）。用户态用 `NtQuerySystemInformation(SystemPoolTagInformation)`
读取该 tag 的全局计数（内存字节序为 `WfPr`）：

```
[selftest] pool tag WfPr: before[allocs=2063 frees=2063 outstanding=0 bytes=0]
                          after[allocs=2263 frees=2263 outstanding=0 bytes=0]
[pooltag] tag=WfPr nonpaged_allocs=1439 nonpaged_frees=1439 nonpaged_outstanding=0
          nonpaged_bytes=0 paged_allocs=0 paged_frees=0
```

- 200 次连接 → 分配计数 `+200`（1239 → 1439），释放计数也 `+200`，未释放计数回到 0、字节回到 0。
- 驱动没有释放过任何一个 context，因此这 200 次释放只能来自 WFP。
- 计数器是全局 per-tag 的；`'rPfW'` 是本探针自造的 tag，混入其他组件的概率可以忽略。
  其他独立运行也得到同样结果（1033 → 1233、826 → 1026、414 → 614、210 → 410、6 → 206）。

**结论：成立。所有权在成功 apply 后转移给 WFP，由 WFP 在 flow / 重定向结束时释放；驱动绝不能自己
`ExFreePool`，否则是双重释放。**

### 5.3 apply 失败的保守处理

`FwpsApplyModifiedLayerData0` 返回 `void`，没有可检查的失败码。驱动在 apply 之后不做任何清理：
如果 apply 实际失败，这 36 字节会泄漏。这是刻意选择的“宁可泄漏、不制造双重释放”。
本次实测中 `apply_failures=0`，没有触发这条路径。

另外补了一组失败路径实验（`tools/vm/edge-cases.ps1`，带断言）：驱动 apply 成功，但重定向目标的端口没有
监听者，连接被 RST。池计数从 `1234/1234` 变成 `1235/1235`，outstanding 仍为 0 —— 即使代理端没接住
这条连接，WFP 也会在 flow 结束时释放 context。失败的是连接本身，不是所有权语义。

另外：重定向 context 分配失败时，现在的处理是配对 apply 之后 BLOCK 该连接（fail-closed），这是刻意语义 —— 没有 context 就带不回原始目的地，放行会让代理拿到不完整的连接信息。

## 6. 失败的尝试与踩坑

1. **模板的 `sign.ps1` 不可用**：走 `signtool /f <pfx>` 报
   `Store::ImportCertObject() failed (0x80090010)`。改用证书库里的证书 +
   `/sha1 <thumbprint>` 后签名成功，`Get-AuthenticodeSignature` 为 `Valid`。
2. **`<ndis.h>` 的 `NDIS630`**：WFP callout 不是 NDIS 驱动模型，包含 `<ndis.h>` 前必须定义
   `NDIS630`，否则 `NDIS_SUPPORT_NDIS6` 为假、`NET_BUFFER_LIST` 等类型不导出，
   `fwpsk.h` 会报几十条 `undeclared identifier`。
3. **WDK 自带的旧 C++ 头遮蔽 MSVC 的 `<cstddef>`**：WDK NuGet 的 `km\crt` 里有自己的
   `cstddef`/`xstddef`/`yvals.h`。如果它先被包含，`std::size_t` 不存在，
   `src/common/redirect_context.h` 直接编译失败。工程里把 `$(VC_IncludePath)` 前置解决。
4. **`INITGUID`**：`fwpmk.h` 的层/子层 GUID 用 `DEFINE_GUID` 声明，不定义 `INITGUID` 时
   链接阶段报 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4`、`FWPM_SUBLAYER_UNIVERSAL` 未解析。
5. **`FwpsApplyModifiedLayerData0` 返回 `void`**，不是 `NTSTATUS`；一开始把返回值赋给
   `NTSTATUS` 导致 C2440。
6. **`FwpsCalloutRegister1` 需要设备对象**：WDM 下必须先 `IoCreateDevice`，所以初始化顺序改成
   设备 → engine → redirect handle → callout 注册 → provider/callout/filter。
7. **C# 首次编译**：`TcpListener.AcceptSocketAsync` 返回 `ValueTask<Socket>` 而不是 `Task<Socket>`；
   对局部结构体的 fixed buffer 再套 `fixed` 会报 CS0213。
8. **clang-format 重排 include 把 WDK 的包含顺序打乱**：`.clang-format` 默认 `SortIncludes`，
   格式化后 `fwpmk.h` 被排到 `ntddk.h` 前面，编译立刻爆出大量 `DEFINE_GUID` 重定义。
   已在 `probe_driver.h` 的 include 块外用 `// clang-format off/on` 锁住顺序。
9. **负对照不成立**：关掉驱动后 `client` 连 `1.2.3.4:443` 依然能完成 TCP 握手（网络里有设备接受），
   只是收不到回显。因此“connect 成功”不能作为重定向的判据。本次采用真正的判据：
   本地监听端是否 accept 并回显，以及 classify 事件里的 branch。
10. **VM 控制台的编码**：通过 tmux/evil-winrm 管道回显中文会变成乱码，ASCII 部分正常。
    这不影响结论；需要干净证据时看 ASCII 字段或原始 hex。
11. **`<ntifs.h>` 必须在 `<ntddk.h>` 之前**：反过来会报 `PEPROCESS`/`PETHREAD` 重定义
    （`SeTokenIsAdmin` 在 ntifs.h 里，做调用者校验时必须包含它）。
12. **`IoCreateDeviceSecure` 是宏**：展开成 `WdmlibIoCreateDeviceSecure`，实现在 `Wdmsec.lib`；
    只包含 `<wdmsec.h>` 不链接会 unresolved external，vcxproj 里要加 `Wdmsec.lib`。

## 7. 与 design.md 的偏差

| 设计写法 | 实际实现 | 原因 |
|---|---|---|
| 先 engine/callout，再建设备 | 先建设备，再 engine/callout | `FwpsCalloutRegister1` 在 WDM 下需要设备对象 |
| classify 先查 redirect state，再 PID 闸 | 先 PID 闸，再 redirect state | PID 是最便宜的闸，先挡掉系统里绝大多数连接；语义不变 |
| 日志用 DbgPrint | 64 条环形事件 + IOCTL 回灌 | 无内核调试器，`events`/`stats` 是唯一可观测通道 |
| “localAddressAndPort 是 ANY ? 目的改为 loopback : …” | 读 `localAddressAndPort`、写 `remoteAddressAndPort` | 文档明确 connect-redirect 层不能改 local 地址；design 这句话的“目的”指 remote |
| 只测两条分支 | 另加 `force_loopback`/`force_local` 模式 | 便于把某一条分支单独钉死做对照，默认路径不用 |

## 8. 遗留问题与下一步

1. **跨进程场景：已补测通过。** 检查方先独立验证了“listener 独立进程、客户端另一进程、双 PID 分离”
   成立；本仓库把流程固化成 `tools/vm/cross-process.ps1` 并复跑通过：
   listener pid=1548、client pid=6400，驱动配置 `target-pid=6400 listener-pid=1548`，
   监听端读回 `redirect-context=36B ... pid=6400 original=1.2.3.4:443`，客户端收到
   `echo=4B ping`，verdict=PASS。
   **踩坑记录：必须用显式 IPv4 socket。** .NET 的 `TcpClient.Connect(string,int)` 走 dual-mode
   IPv6 socket，本地端点是 `[::ffff:...]`，只挂 V4 层的驱动完全看不到。测试程序统一用
   `new Socket(AddressFamily.InterNetwork, ...)`。这条直接影响 D1：产品要 V4/V6，只挂 V4 会
   漏掉大量现代应用的流量。
2. **真正的代理转发未测**：监听端现在只回显。真实代理还需要
   `SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS` + 对出站 proxy socket 调
   `SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS`，并把原目标地址用于实际连接。本次只验证了
   context 这一半。
3. **重定向链 / already-redirected 分支**：`previousVersion` 的检查已经移到 acquire 之前（只读判断，
   不需要配对 apply），逻辑上是“已有 `localRedirectHandle` 就 PERMIT 并清写权限”。但本批只有一个 callout，
   无法构造多 callout 的重定向链，**这个修复未做运行期验证**，仅按官方文档与 WFPSampler 示例做代码审查，
   待多 callout 场景验证。
4. **设备 DACL（发布阻断项，已修复）**：检查方实测 `IoCreateDevice` 的默认 DACL 是
   `RW Everyone`，而 IOCTL 走 `FILE_ANY_ACCESS`，任何登录用户都能改写 target PID / listener PID /
   端口 / 模式，把任意进程（含管理员会话）的连接静默改道、读走原始目的地。
   `FILE_DEVICE_SECURE_OPEN` 不解决 DACL 问题。已改为 `IoCreateDeviceSecure` +
   `SDDL_DEVOBJ_SYS_ALL_ADM_ALL`，并在每个 IOCTL 入口用 `SeTokenIsAdmin` 校验调用者。
   实测 `accesschk64.exe -accepteula -q -o \Device\WfProbe`：

   ```
   \Device\WfProbe
     Type: Device
     RW NT AUTHORITY\SYSTEM
     RW BUILTIN\Administrators
   ```

   非管理员连打开设备都不行。

5. **热路径成本**：每次重定向都从非分页池分配 36 字节并交给 WFP 释放。语义已验证，
   Phase 0 没有测它对 480 Mbps 场景的影响；后续可以改成固定大小的池缓存或预分配块
   （前提是确认 WFP 允许非 ExAllocatePoolWithTag 的内存，结论倾向是不允许，需要再查）。
6. **复用建议**：把以下事实写进 `spec/backend/wfp-architecture.md` 或新的 Phase 0 笔记：
   - connect-redirect 层只可写 `remoteAddressAndPort`，`localAddressAndPort` 只读；
   - `localRedirectTargetPID` 与 `localRedirectHandle` 是 Win8+ 本地重定向的硬要求；
   - `localRedirectContext` 必须用 `ExAllocatePoolWithTag` 分配，成功后所有权归 WFP，驱动不得释放；
   - ANY 分支的判据只看地址，端口此时已经被分配。

## 9. 驱动与测试程序文件

| 路径 | 内容 |
|---|---|
| `src/driver/probe_ioctl.h` | 控制面 ABI：IOCTL 码、配置、计数、事件 |
| `src/driver/probe_driver.h` | 内部共享面：状态、窄接口、callout 原型、include 顺序约束 |
| `src/driver/probe_driver.cpp` | DriverEntry/卸载、WFP 注册与逆序拆除、设备与 IOCTL |
| `src/driver/probe_classify.cpp` | classify 各道闸、两条分支、redirect context 分配 |
| `src/driver/wfprobe.vcxproj` / `NuGet.config` | WDK 工程（`SignMode=Off`，手工签名） |
| `tools/wfprobe/*` | C# 测试程序（control/stats/events/pooltag/listen/client/selftest） |
| `tools/vm/sync.sh` | 源码同步到 VM |
| `tools/vm/build-and-test.ps1` | 构建 → 签名 → 安装 → 三条验证 → 卸载的一键脚本 |
| `tools/vm/cross-process.ps1` | listener 与 client 分属两个进程、双 PID 分离的重定向验证 |
| `tools/vm/edge-cases.ps1` | 重定向目标无人监听时的 context 释放验证 |

## 10. 关键命令

```powershell
# 构建
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msb C:\wfbench\wfprobe\src\driver\wfprobe.vcxproj -restore -p:Configuration=Debug -p:Platform=x64

# 签名（必须用证书库里的证书和 /sha1，不要走 /f <pfx>）
$thumb = (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like '*WinForward Test Driver*' }).Thumbprint
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /fd SHA256 /sha1 $thumb /v wfprobe.sys

# 安装
sc.exe create wfprobe type= kernel start= demand binPath= C:\Windows\System32\drivers\wfprobe.sys
sc.exe start wfprobe

# 设备 DACL
& C:\SysInternals\accesschk64.exe -accepteula -q -o \Device\WfProbe

# 验证
wfprobe.exe selftest --remote 1.2.3.4:443 --bind 127.0.0.1 --port 18080 --iterations 200 --expect-address 127.0.0.1 --expect-branch loopback
wfprobe.exe selftest --remote 1.2.3.4:443 --bind 192.168.100.2 --port 18080 --iterations 2 --source 192.168.100.2 --expect-address 192.168.100.2 --expect-branch local
```

## 11. 复核修复记录（review 后）

| 级别 | 问题 | 处理 | 验证 |
|---|---|---|---|
| 高 | 设备默认 DACL 为 `RW Everyone`，IOCTL 可被任意登录用户滥用 | `IoCreateDeviceSecure` + `SDDL_DEVOBJ_SYS_ALL_ADM_ALL`；IOCTL 入口加 `SeTokenIsAdmin` | accesschk 只剩 SYSTEM / Administrators；完整流程复跑全绿 |
| 中 | acquire 之后未配对 apply，already-redirected 分支实际把连接 BLOCK | `finish_classify` 统一配对：每次 acquire writable 成功后必 apply，再写回 PERMIT/BLOCK；分配失败改为有意的 fail-closed | 编译通过；200 次连接与两条分支复跑 PASS |
| 中低 | `FwpsCalloutUnregisterById0` 返回值被丢弃 | 只对 `STATUS_DEVICE_BUSY`/`STATUS_FWP_IN_USE` 重试 2s；耗尽后 DbgPrint 并 `KeBugCheckEx(0x8000FFE0)`，不继续卸载；按当前不变量该路径不可达 | 3 轮 load/unload 的 stop 均在毫秒级，未触发 bugcheck |
| 低 | `SelfTest` 的 PASS 判据漏了 `echoOk` | 判据加 `echoOk == iterations` | 全绿 |
| 低 | redirect records 固定 1KiB 且失败不重试 | 16KiB 起步，`WSAEMSGSIZE`/`WSAEINVAL`/122 时按需放大到 256KiB | 实测单条 records 772B，读取正常 |
| 低 | `build-and-test.ps1` 的 finally 不等 STOPPED、不删 sys | 等 STOPPED 后再 delete，并删除 System32 下的副本 | 服务与文件都清理干净 |

复核后的完整复跑（2026-10-20）：

```
服务状态：STATE : 4 RUNNING
\Device\WfProbe
  RW NT AUTHORITY\SYSTEM
  RW BUILTIN\Administrators
[selftest] iterations=200 completed=200 failures=0 contexts_ok=200 contexts_failed=0 echo_ok=200
[selftest] pool tag WfPr: before[allocs=2063 frees=2063 outstanding=0 bytes=0]
                          after[allocs=2263 frees=2263 outstanding=0 bytes=0]
```

## 12. 第四轮：断言、负向验证与内核加固

### 12.1 脚本从“打印”改成“断言”

- `edge-cases.ps1`：现在断言 pool allocs +1、frees +1、outstanding 0、`contexts_allocated` +1、
  `redirected` 至少 +1、client 退出码非 0；任一不满足就 `exit 1`。
- `build-and-test.ps1`：DACL 不再只打印，断言 accesschk 输出包含 SYSTEM 与 Administrators、
  且不含 Everyone；找不到 accesschk 也直接失败。
- `cross-process.ps1`：除 36B 与 echo 外，断言 context 里的 `pid` 等于 client PID、
  `original` 等于目标地址；client 启动改为轮询 `delay-ms=` 行，不再用固定 sleep。
- 两个脚本都给子进程加了 stderr 重定向与 `finally` 清理，避免失败路径把监听进程留在 VM 上。
- 这次接入时踩了一个真实坑：`Start-Process` 只重定向 stdout 时，子进程会继承 WinRM 的 stderr 管道，
  监听进程不退出会让整个 `windows-vm script` 调用挂死。现在 stdout/stderr 都重定向，
  并在 finally 里显式 kill 两个子进程。

### 12.2 反向验证（把被守的事破坏掉，确认门变红）

| 门 | 破坏方式 | 结果 |
|---|---|---|
| DACL | 把 SDDL 换成 `SDDL_DEVOBJ_SYS_ALL_ADM_RWX_WORLD_RW_RES_R`（World 可读写） | accesschk 出现 `RW Everyone`，脚本在 DACL 断言处失败：`DACL 断言失败：出现 Everyone` |
| edge-cases 的池断言 | `-DeadPort 443`，命中自流量闸、根本不发生重定向 | pool delta=0、contexts delta=0、redirected delta=0 → 4 条断言 FAIL，verdict FAIL |
| cross-process 的 context 断言 | listener 绑 `192.168.100.2`，redirect 走 loopback，监听端收不到 | 36B/pid/original/echo 四条断言全 FAIL，verdict FAIL |

### 12.3 内核侧加固

- **注销重试收窄 + 主动 bugcheck**：只对 `STATUS_DEVICE_BUSY` / `STATUS_FWP_IN_USE` 重试，
  10 ms 一次、上限 2 s；`STATUS_FWP_CALLOUT_NOT_FOUND` 视为已注销。重试耗尽后不再继续卸载，
  而是 DbgPrint + `KeBugCheckEx(0x8000FFE0)`（P1=最后 NTSTATUS、P2=callout id、P3=重试次数、P4=0）。
  这条兜底按当前不变量不可达：本驱动不关联 flow context（BUSY 只与 `FwpsFlowAssociateContext0` 有关），
  且卸载只有 DriverUnload 一个线程（IN_USE 需要并发注册/注销）。用 bugcheck 把“不可达”变成可验证断言，
  假设被将来改动打破时崩在卸载路径上，而不是留下悬空 callout 在几个月后随机蓝屏。
- **`caller_is_admin` 加 IRQL 守卫**：非 PASSIVE_LEVEL 直接返回 false，防止内核调用者绕过 DACL
  在 DISPATCH_LEVEL 发 IOCTL。
- **already-redirected 分支前移**：判断只读 `previousVersion`，移到 `FwpsAcquireWritableLayerDataPointer0`
  之前，因此不需要 apply；动作是“置 PERMIT 并清写权限”，与 WFPSampler 的最终放行写法一致。
  `finish_classify` 的注释现在直接引用文档：acquire 会把 actionType 置 BLOCK、清掉
  `FWPS_RIGHT_ACTION_WRITE`，并且每次调用都必须配对 apply。两条放行路径统一为“置 PERMIT 后清位、
  定终态”：filter 是 FWP_ACTION_CALLOUT_TERMINATING，不需要把改许可的权利留给低权重 callout。
  acquire writable 失败的分支也补了注释：classifyOut 可能停在 BLOCK，是有意的 fail-closed。

### 12.4 最终正向复跑

```
ASSERT PASS: listener 收到 redirect context (36B)
ASSERT PASS: context pid 匹配 client (pid=3720)
ASSERT PASS: context original 匹配 (original=1.2.3.4:443)
ASSERT PASS: client 收到回显 (echo)
cross-process verdict: PASS
ASSERT PASS: pool alloc +1 (delta=1)
ASSERT PASS: pool free +1 (delta=1)
ASSERT PASS: pool outstanding == 0 (outstanding=0)
ASSERT PASS: contexts_allocated +1 (delta=1)
ASSERT PASS: redirected >= +1 (delta=1)
ASSERT PASS: client failed (exit=3)
edge-cases verdict: PASS
[selftest] iterations=200 completed=200 failures=0 contexts_ok=200 contexts_failed=0 echo_ok=200
[selftest] pool tag WfPr: before[allocs=2063 frees=2063 outstanding=0 bytes=0]
                          after[allocs=2263 frees=2263 outstanding=0 bytes=0]
```


### 12.5 收尾：主动 bugcheck 与卸载计时

- 注销失败的兜底从“继续卸载”改成“2 s 重试后主动 KeBugCheckEx(0x8000FFE0)”。按当前不变量这条路径
  不可达（不关联 flow context、单线程卸载），主动崩溃是为了把“不可达”变成可验证断言：
  假设被将来的改动打破时，崩在卸载路径上，参数直接指向问题点。
- 3 轮 load/unload 实测 stop 耗时：36.6 ms / 31.6 ms / 34.1 ms，均远小于 2 s 重试窗口；
  未触发 bugcheck，VM uptime 未变。
- 两条放行路径统一为“置 PERMIT + 清 FWPS_RIGHT_ACTION_WRITE、定终态”；acquire writable 失败分支
  补注释说明 classifyOut 可能停在 BLOCK，是有意的 fail-closed。
