# B3 侦察：Windows VM 环境矩阵

> 阶段：B3 第 0 步（只读侦察）。未安装/卸载驱动，未改动 `src/driver`，未创建 VM 或 vSwitch。
> 采集时间：2026-10-23。目标机：WinLTSC（`192.168.100.2`）。

## 1. 结论速览

| 项 | 状态 | 证据 |
|---|---|---|
| VM 开机 | 是 | `windows-vm status`：Running，uptime ≈ 1d，Generation 2，16 vCPU，1.62 GB（采集时值；VM 为动态内存） |
| Hyper-V 角色（VM 内） | **未安装** | `Microsoft-Hyper-V-All` = `Disabled`（含 Hypervisor / Services / Tools 全部 Disabled） |
| Hyper-V cmdlet | **全部缺失** | `Get-VM` / `New-VM` / `Get-VMSwitch` / `New-VMSwitch` / `Get-VMProcessor` / `Get-VMNetworkAdapter` 均 MISSING |
| 嵌套虚拟化（SVM/NP） | **不可用** | `coreinfo64 -v`：`SVM -`、`NP -`、`HYPERVISOR *`；CPU 为 AMD Ryzen 9 9955HX |
| Win32_Processor 扩展位 | 未暴露 | `VMMonitorModeExtensions=False`、`SecondLevelAddressTranslationExtensions=False` |
| 测试签名 | **已开** | `bcdedit`：`testsigning Yes`；Secure Boot `False` |
| `wfprobe` 驱动 | **已安装且运行中** | `sc query wfprobe` → `STATE: 4 RUNNING`；`C:\Windows\System32\drivers\wfprobe.sys`，55184 B，2026-10-21 |
| 驱动签名 | 测试证书，验证通过 | `CN=WinForward Test Driver`（自签发，有效期至 2031），`Status: Valid` |
| 现有 WFP 层 | ALE_CONNECT_REDIRECT_V4/V6 | `probe_classify.cpp`；`FwpsCalloutRegister1` 一次注册；`wgrep MAC_FRAME` 在 wfprobe 过滤器中计数 = 0 |
| 网卡 | 2 × `netvsc.sys`（合成 vNIC，非物理） | `DriverFileName=netvsc.sys`，NDIS 6.70，PNPDeviceID `VMBUS\...` |
| 已装 LWF | **WinpkFilter 3.6.2.1 已绑定且启用** | `nt_ndisrd` Enabled=True 于 Internal；`ndisrd.sys` RUNNING |
| WSL / Docker | **均不可用** | `wsl --status`：not installed；`docker`/`podman`/`nerdctl` MISSING |
| 构建环境 | 可用 | VS Build Tools 18 + `vswhere`；WDK 走 NuGet（`Microsoft.Windows.WDK.x64 10.0.28000.2526` 已在缓存），nuget.org 可达（HTTP 200） |
| WinDbg | 有 | `WinDbgX.exe`（`C:\Users\Neko\AppData\Local\Microsoft\WindowsApps`） |
| SysInternals | 有 | `C:\SysInternals`（含 coreinfo / strings / sigcheck / handle / tcpview 等） |
| 回滚点 | 有快照 | Hyper-V checkpoint「驱动开发二」（10-20 20:37）；CLI 无 checkpoint 子命令，兜底 `windows-vm reset` |

## 2. 网络与 L2 邻接

Windows VM：

| 别名 | ifIndex | 地址 | MAC | 网关 |
|---|---|---|---|---|
| Internal | 5 | 192.168.100.2/24 | 00-15-5D-03-72-8D | 无 |
| External | 14 | 192.168.77.2/24 | 00-15-5D-03-72-8E | 192.168.77.1 |

Linux 开发机（`nixos`）：

| 接口 | 地址 | MAC |
|---|---|---|
| eth0 | 192.168.100.4/24 | 00:15:5d:03:72:8a |
| eth1 | 192.168.77.4/24 | 00:15:5d:03:72:8b |

**关键发现：Linux 开发机自身也是 Hyper-V guest**（`systemd-detect-virt` = `microsoft`，DMI 厂商
`Microsoft Corporation` / `Virtual Machine`，MAC 前缀同为 `00:15:5D`，与 WinLTSC 的 8D/8E 属同一动态
分配段，其间缺 8C）。即 Linux 与 WinLTSC 是**同一台 Hyper-V 宿主机上的兄弟 VM**，各自挂在
192.168.100.0/24 与 192.168.77.0/24 两个 L2 段上。

L2 邻接已实测确认：

- VM → Linux：`Test-Connection 192.168.100.4` 3/3 Success；ARP `192.168.100.4 → 00-15-5D-03-72-8A`
  状态 `Reachable`（不同 MAC，非本机回环）。
- Linux → VM：`ip neigh` 显示 `192.168.100.2 lladdr 00:15:5d:03:72:8d REACHABLE`。
- 双向 TCP 已验证：Linux 在 `192.168.100.4:40000` 临时监听，VM `Test-NetConnection` 返回
  `TcpTestSucceeded=True`，监听端记录到 `ACCEPTED ('192.168.100.2', 50378)`。
- 同段还存在 `192.168.100.1`（MAC `00-07-29-55-35-57`，非 Hyper-V 前缀），Linux 侧可达。

## 3. 拓扑判定

| 方案 | 判定 | 原因 |
|---|---|---|
| A：VM 内 Hyper-V Internal switch + 子 VM | **不可用** | Hyper-V 功能未安装，且 `SVM`/`NP` 未暴露，装了也起不了 hypervisor；`Get-VM`/`New-VM` 全缺 |
| A′：宿主机 vSwitch 上的兄弟 VM（Linux 192.168.100.4）作客户端 | **推荐** | 已实测同 L2 段、双向可达；Windows 侧网卡是 vSwitch 后的 `netvsc` 合成 NIC，正是 B3 关心的「vSwitch/VM 入口帧」形态 |
| B：物理网卡 + Linux 客户端 | **退化为 B′** | VM 无物理 NIC 直通，两个网口都是 `netvsc` 合成口；「物理入口」这一形态在本机无法复现 |
| C：主机自身 vEthernet / loopback 对打 | 不适用 | 宿主机是 Linux（NixOS），不是 Windows，无法在宿主机上跑 WFP 驱动 |

推荐：**A′ 为主，辅以 B 的客户端角色**——WinLTSC 作为 L2 端站跑 WFP L2 callout，Linux
（192.168.100.4）作为发帧客户端。覆盖 vSwitch 客户端入口，不覆盖「嵌套 guest-in-guest」与
「物理 NIC / External switch + VMQ/SR-IOV」。

## 4. 注入 API 可用性

WDK `10.0.28000.2526` 的 `km\fwpsk.h` 中确认存在：

- `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE` / `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`（第 149/150 行），
  另有 `_FAST` 变体（174/175）。
- `#define FWPS_INJECTION_TYPE_L2 0x00000010`（2959）。
- `FwpsInjectionHandleCreate0`（2965）、`FwpsInjectMacReceiveAsync0`（3183）、
  `FwpsInjectMacSendAsync0`（3200）、`FwpsQueryPacketInjectionState0`（3286）。

编译期 API 面完整；运行期是否对 `netvsc` 合成 NIC 生效仍需 spike 实测。

## 5. 风险

1. **嵌套虚拟化彻底不可用**（`SVM -`、`NP -`）：不是配置问题，是宿主机未向 guest 暴露
   `ExposeVirtualizationExtensions`；在 AMD 上即使打开也需宿主机侧操作，属侦察阶段禁区。
2. **WinpkFilter LWF 已绑定在 Internal 口**：`ndisrd.sys`（3.6.2.1）处于 RUNNING，位于 miniport 与
   协议栈之间。WFP MAC 帧层与之的相对位置、以及 `FwpsInjectMacReceive` 注入的帧是否会被 LWF 再次
   看到，必须实测；也可能干扰「帧计数」的解释（同一帧可能被两侧各算一次）。
3. **合成 NIC 而非物理 NIC**：`netvsc` 的 MAC 帧来自 VMBus；MAC 帧层是否对所有合成 miniport 开放，
   是本次 spike 的核心未知项。
4. **内存偏小（采集时）**：1.62 GB / 16 vCPU（VM 为动态内存，复核时可见约 2.28 GB）。编译与加载驱动够用（`wfprobe.sys` 已于 10-21 构建并运行），
   但不宜再起第二个 VM 或做重负载压测。
5. **无法枚举宿主机上的其他客户端**：guest 内没有 Hyper-V cmdlet，看不到宿主机上还有哪些 VM 可当
   客户端；只能依赖已确认的 Linux（192.168.100.4）。

## 6. 对 D1 的影响

- 侦察**未推翻** D1 的 WFP L2 路线：MAC 帧层与 L2 注入 API 在编译面完整，且存在一个真实的
  vSwitch 后客户端（Linux）可发帧。
- 但**覆盖边界必须写明**：本环境只能验证「合成 NIC（netvsc）上的 vSwitch 客户端入口」，不能验证
  「嵌套 guest 内再跑 vSwitch」与「物理 NIC 直通」。若 D1 的结论依赖后者，需另行安排硬件。
- 若 A2/A3 在 netvsc 上失败，建议不要直接判 LWF——先区分是「MAC 帧层不覆盖合成 NIC」还是
  「注入方向/接口索引用错」，再决定。

## 7. 实际命令与关键输出摘要

```text
windows-vm status
  State: Running / Generation 2 / 16 vCPU / 1.62 GB（采集时） / Checkpoint「驱动开发二」

Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
  State: Disabled      （全部 Hyper-V-* 子特性均 Disabled）

Get-Command Get-VM,New-VM,Get-VMSwitch,New-VMSwitch,Get-VMProcessor,Get-VMNetworkAdapter
  全部 MISSING

C:\SysInternals\coreinfo64.exe -v    （重定向到文件后读取）
  HYPERVISOR *  Hypervisor is present
  SVM        -  Supports AMD hardware-assisted virtualization
  NP         -  Supports AMD nested page tables (SLAT)
  Note: Coreinfo must be executed on a system without a hypervisor running ...

bcdedit /enum {current}
  testsigning Yes / isolatedcontext Yes / nx OptIn

sc query wfprobe
  STATE: 4 RUNNING (STOPPABLE, NOT_PAUSABLE, IGNORES_SHUTDOWN)
  BINARY_PATH_NAME: \??\C:\WINDOWS\System32\drivers\wfprobe.sys

Get-AuthenticodeSignature C:\Windows\System32\drivers\wfprobe.sys
  Status: Valid / CN=WinForward Test Driver（自签发，至 2031-10-20）

Get-NetAdapter -IncludeHidden
  Internal   Microsoft Hyper-V Network Adapter      Up  10 Gbps  ifIndex 5
  External   Microsoft Hyper-V Network Adapter #2   Up  10 Gbps  ifIndex 14
  DriverFileName: netvsc.sys（两者），NDIS 6.70

Get-NetAdapterBinding -Name Internal
  nt_ndisrd  WinpkFilter LightWeight Filter  Enabled=True
  （ndisrd.sys 3.6.2.1，System 启动，RUNNING）

Test-Connection 192.168.100.4 -Count 3
  3/3 Success

Test-NetConnection 192.168.100.4 -Port 40000   （Linux 侧临时监听）
  TcpTestSucceeded = True
  Linux 侧: ACCEPTED ('192.168.100.2', 50378)

wsl.exe --status
  The Windows Subsystem for Linux is not installed.
Get-Command docker,podman,nerdctl
  MISSING

vswhere -latest -products * -property installationPath
  C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools
（WDK 不在 Windows Kits\10\Include\*\km，改由 NuGet 包提供）

%USERPROFILE%\.nuget\packages\microsoft.windows.wdk.x64\10.0.28000.2526
  c\Include\10.0.28000.0\km\fwpsk.h
    :149  FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE,
    :150  FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE,
    :2959 #define FWPS_INJECTION_TYPE_L2 0x00000010
    :2965 FwpsInjectionHandleCreate0(
    :3183 FwpsInjectMacReceiveAsync0(
    :3200 FwpsInjectMacSendAsync0(
    :3286 FwpsQueryPacketInjectionState0(

Invoke-WebRequest https://api.nuget.org/v3/index.json
  200
```

## 8. 下一步建议

1. 采用 A′ 拓扑：WinLTSC 跑 L2 callout，Linux 192.168.100.4 当客户端。
2. 复制 `src/driver` 到 `C:\workspace\spike-l2`（`C:\workspace` 已存在，含历史 spike 目录），不改仓库。
3. 先只做计数 + permit，验证 `INBOUND/OUTBOUND_MAC_FRAME_NATIVE` 在 `netvsc` 上是否触发；再谈注入。
4. 注入测试先在 Internal（ifIndex 5）上做，避免 External 口上无关流量干扰。
