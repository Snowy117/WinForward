# Forwarded IPv6 E2E — 现状、环境与夹具研究

日期：2026-10-25。以下均为仓库代码或本机/VM 只读检查结果；唯一的状态改动是临时
添加并删除 `Internal` 上的 `fe80::100` 地址，用于验证链路本地地址可增删。

## 1. 环境清单

WinLTSC（Running，16 vCPU）：

| ifIndex | 接口 | IPv6 地址 | 状态 |
|---:|---|---|---|
| 5 | Internal | `fe80::8ca8:508a:7f89:be57%5/64` | Preferred |
| 14 | External | `fe80::a09e:8f4a:9bb4:71fc%14/64` | Preferred |
| 14 | External | `fd00:1234:5678:1::2/64` | Preferred |
| 1 | Loopback | `::1/128` | Preferred |

Linux 客户端主机：

| 接口 | IPv4 | IPv6 |
|---|---|---|
| eth0 | 192.168.100.4/24 | `fe80::215:5dff:fe03:728a/64` |
| eth1 | 192.168.77.4/24 | `fe80::215:5dff:fe03:728b/64`、`fd00:1234:5678:1::4/64` |

- Linux 默认 v6 路由：`default via fd00:1234:5678:1::1 dev eth1`。
- `fd00:100::/64` 当前未在 Linux 上配置；本设计不需要它。
- 本机用户 `paff` 无免密 sudo，`ip addr add` 需要额外授权；因此方案只使用 Linux
  现有链路本地地址，不在 Linux 侧改地址。

### 链路本地地址增删验证

在 Internal（ifIndex=5）临时添加 `fe80::100/64`：

```powershell
New-NetIPAddress -InterfaceIndex 5 -IPAddress fe80::100 -PrefixLength 64
# Get-NetIPAddress 显示 fe80::100%5 Preferred
netsh interface ipv6 delete address "Internal" fe80::100
# 删除后只剩 fe80::8ca8:508a:7f89:be57%5
```

结论：

- Windows 允许在同一接口添加额外链路本地地址，可作第二 L / target。
- `Remove-NetIPAddress -IPAddress fe80::100` 在本机会因 scope/查询匹配失败；用
  `netsh interface ipv6 delete address "<Interface>" <addr>` 稳定。
- 脚本必须等待 `AddressState=Preferred`（DAD 完成）后再跑用例。

## 2. 代码现状与缺口

### 驱动 / 匹配 / 帧路径

- IPv6 解析：`src/driver/probe_forwarded_frame.cpp:107`（EtherType 0x86DD、最多 4 跳
  扩展头、fragment header、atomic fragment 视为非分片）。
- v6 伪头校验和：`src/driver/probe_forwarded_frame.cpp:434`（UDP）、`:450`（TCP）。
- DNAT/SNAT 16 字节地址：`:402`、`:406` 附近。
- 分片跟随表 key 含族与 v6 fragment ID：`src/common/forwarded_fragment_table.h:58`。
- 规则族匹配：`src/common/forwarded_blob.h:132`、
  `src/common/forwarded_matcher.h:40`。
- 结论：数据面代码路径齐全；本任务不改驱动，除非 E2E 暴露真实缺陷。

### 用户态

- `ListenerSet` 显式 L 支持 v6（`DualMode=false`），wildcard 同时绑 v4/v6：
  `src/userland/WinForward.Userland/Relay/ListenerSet.cs:51`、`:121`。
- redirect cmsg / sockaddr_in6：`Relay/RedirectDestination.cs:91`、`:135`。
- UDP 回程从收到 datagram 的 L 发出：`Relay/ForwardedUdpRelay.cs:110` 附近。
- SOCKS5 `atyp=4`：`src/userland/README.md:118`。
- 控制器 v6 L 缺口：`Control/WfpController.cs:414` 的
  `ResolveForwardedListenerPlan`；`FindAdapterAddress` 仅
  `AddressFamily.InterNetwork`（`:469`），错误文案也是 IPv4；`FindFirstAdapterAddress`
  同样只找 v4（`:498`）。`ForwardedListenerAddress` 是
  `Control/ControllerSettings.cs:53` 的代码属性，`PolicyConfigurationDocument` 没有
  对应 JSON 字段。本任务不修，留给 Runtime/Configuration 集成。

### wfprobe 夹具

TCP：

- `RunAsync` 无条件调用 `OnLinkAlias.Ensure`（`tools/wfprobe/ForwardedTcpSuite.cs:70`），
  `OnLinkAlias` 只接受 IPv4（`tools/wfprobe/OnLinkAlias.cs:40`）。
- core/upstreams/fail listener 固定 `new TcpListener(IPAddress.Any, listenerPort)`
  （`ForwardedTcpSuite.cs:140`、`:264`、`:728`）。
- multi 用 `OnLinkAlias` 加第二 L 和 External target（`:364`），listener 用
  `BindOn(localIp, ...)`（`:335`），`BindOn` 没有 link-local scope 处理。
- multi 期望 4 条连接，按源端口分两组；外部客户端脚本是
  `tools/linux/forwarded_tcp_b6b.py`，固定 `AF_INET` 且只有一个 `--bind-address`
  （`:25`）。
- 查询 `QueryFor` / `Socks5TestClient` 已支持 v6 地址（`Socks5TestClient` 按地址长度
  选 atyp 0x01/0x04）。

UDP：

- `RunCoreAsync` 的 listener 用 `BindUdp(listenerPort)`（固定 v4，
  `ForwardedUdpSuite.cs:140`），`any` 固定 `IPAddress.Any`（`:172`），decoy
  `TryBindDecoy` 固定 `AddressFamily.InterNetwork`（`:733`），backend socket 固定 v4
  （`BindEphemeralUdp`，`:754`）。
- fragment / fragment-host 已按 family 选择 listener 和 any；fragment 曾在 B6 用
  `fe80::8ca8:508a:7f89:be57` 跑过 9 片 block。
- socks5 阶段也用 `BindUdp` v4（`:513`）和 v4 any（`:530`）；但
  `Socks5UdpWire.Encode` 已支持 atyp 0x04，`Socks5UdpProbeClient` 也支持 v6 回复地址。
- `TryBindDecoy` 对 v6 需要按地址族建 socket，并给链路本地地址设置 scope。
- Linux `forwarded_udp_client.py` 固定 `AF_INET` / `0.0.0.0`（`:150`、`:177` 附近），
  不能跑 v6。

### 门禁与准备脚本

- `tools/linux/forwarded_gate.py` 只跑 v4 阶段，明确把 IPv6 9 片列为手工；
  `--no-verify` 被 guard 拒绝（`:124`）。
- `tools/windows/prepare-b6c-forwarded.ps1` 只加 IPv4 别名
  `192.168.100.99/24` 和端口放行，没有 v6 地址或 v6 专用规则。
- `tools/linux/ipv6_mtu_fragment.py` 仍需要改 Linux MTU（root），本任务不复用它做
  happy path。

## 3. 地址方案

目标：原始目的 target 与 L 不同，且不要求 Linux 侧提权。

| 角色 | 地址 | 来源 |
|---|---|---|
| Internal 主 L（L1） | `fe80::8ca8:508a:7f89:be57` | 现有链路本地 |
| Internal 第二 L（L2） | `fe80::98` | prepare 添加 |
| Internal target | `fe80::99` | prepare 添加 |
| External L | `fe80::a09e:8f4a:9bb4:71fc` | 现有链路本地 |
| External target | `fe80::199` | prepare 添加 |
| Linux Internal 源 | `fe80::215:5dff:fe03:728a%eth0` | 现有 |
| Linux External 源 | `fe80::215:5dff:fe03:728b%eth1` | 现有 |

- target 与 L 不同，客户端回程源地址检查/连接有效性才能真正证明 SNAT 恢复了原始目的。
- target 与 L 都链路本地，Windows 侧加地址即可；Linux 用 `%eth0` / `%eth1` 指定 scope。
- External 也可以改用现有 ULA `fd00:1234:5678:1::2` 与 `.99` target，但链路本地已够用，
  且避免 Linux 侧新增地址。
- 若链路本地 DNAT/注入在实测中失败，fallback 是 Internal 加 ULA（Windows `fd00:100::/64`，
  Linux 需要 root 加地址/路由），作为风险缓解而不是默认方案。

## 4. 关键测试语义

- TCP：客户端连接 `target:40250`；套件 listener 收到 DNAT 后的连接；查询 redirect 表
  断言 server=target、ingress/L 正确；数据 64 KiB 双向 SHA/ACK；回程源错误会直接
  破坏 TCP 连接。
- UDP：客户端校验回程源 `peer == target`；target 上的 decoy socket 必须收不到原帧；
  套件断言 `udp_ingress/return/created` 和去向恒等式。
- multi：跨入口组用不同 target/ingress/L；同入口组用同一 target 的两个端口触发两个 L；
  套件按源端口分组，断言 `redirect_multi_l >= 1`、`return_ambiguous=0`。
- 所有这些用例都必须带客户端内容校验，不能只用驱动计数器。

## 5. 证据来源

- B6 IPv6 分片日志：
  `.trellis/tasks/archive/2026-10/10-23-fwd-b6-hardening/verification/final3-ipv6-frag.log`、
  `final3-ipv6-permit-first.log`、`final3-ipv6-host-same-remote.log`。
- B5 native IPv6 未 E2E：
  `.trellis/tasks/archive/2026-10/10-23-fwd-b5-udp-gateway/verification.md:170`。
- B6b multi 证据：
  `.trellis/tasks/archive/2026-10/10-23-fwd-b6-hardening/verification/b6b-recheck-multi.log`。
