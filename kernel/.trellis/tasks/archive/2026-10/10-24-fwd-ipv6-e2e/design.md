# Design — Forwarded IPv6 数据面 E2E

## 1. 目标与边界

本任务只验证数据面与夹具，不改驱动语义：

- 覆盖 native IPv6 的 TCP core / upstreams、UDP core / socks5，以及 TCP 同入口
  多 L + Internal/External 跨入口。
- 不覆盖产品控制器 v6 L 自动解析：`WfpController.ResolveForwardedListenerPlan`
  仍只选 IPv4，`ForwardedListenerAddress` 没有 JSON 入口；留给 WinForward.Runtime /
  Configuration 集成。
- 不覆盖 UDP 多 L、IPv6 热替换、逐片 raw、atomic fragment、性能与长稳。

## 2. 地址与环境生命周期

### 2.1 地址表

| 角色 | 地址 | 来源 |
|---|---|---|
| Internal 主 L（L1） | `fe80::8ca8:508a:7f89:be57` | 现有链路本地，prepare 只读发现 |
| Internal 第二 L（L2） | `fe80::98` | prepare 添加 |
| Internal target | `fe80::99` | prepare 添加 |
| External L | `fe80::a09e:8f4a:9bb4:71fc` | 现有链路本地，prepare 只读发现 |
| External target | `fe80::199` | prepare 添加 |
| Linux Internal 源 | `fe80::215:5dff:fe03:728a%eth0` | 现有 |
| Linux External 源 | `fe80::215:5dff:fe03:728b%eth1` | 现有 |

target 必须与 L 不同：否则回程 SNAT 即使写错成 L，客户端也看不出来。Internal 的
target/L2 用额外链路本地地址，External 的 target 同理；Linux 侧只用现有地址，不需要
root 或 `ip addr add`。

### 2.2 prepare / cleanup

`tools/windows/prepare-b6c-forwarded.ps1` 增加 `-WithIpv6`：

- 只读发现 Internal / External 的主链路本地地址，选择 `Preferred` 且非本次添加的地址。
- 用 `netsh interface ipv6 add address "<Interface>" <addr>` 添加 L2 / 两个 target；
  用 `Get-NetIPAddress` 轮询到 `AddressState=Preferred`（DAD 完成）再继续。
- 增加 v6 防火墙规则：40250/40251（TCP/UDP）、40400（TCP/UDP）、40420（TCP）、
  40600（UDP）、41400（TCP；虽然 verify 走 v4，保留一致）。规则名用
  `B6C-ENV-V6-*`，便于 `-Remove` 的 `B6C-ENV-*` 通配清理。
- 输出机器可读行：
  `B6C-V6-READY internal_l1=... internal_l2=... internal_target=... external_l1=... external_target=...`
- `-Remove` 无论是否带 `-WithIpv6` 都尝试删除已知 v6 地址与 `B6C-ENV-V6-*` 规则，
  忽略不存在；删除地址用 `netsh interface ipv6 delete address`，不用
  `Remove-NetIPAddress`（本机 scope 查询匹配不可靠，见 research/environment.md）。
- 不切换 `ms_tcpip6`：脚本检查 Internal 的绑定状态，禁用时明确失败并提示手工启用；
  不静默改宿主状态。

IPv6 绑定检查：

```powershell
$binding = Get-NetAdapterBinding -Name Internal -ComponentID ms_tcpip6
if (-not $binding.Enabled) { throw "Internal ms_tcpip6 未启用" }
```

### 2.3 生命周期

1. gate 调用 prepare（v6 开关时带 `-WithIpv6`）→ 解析地址 → 写 `prepare.log`。
2. 每个 phase 由 `run_phase` 启动 suite、等待 READY、跑 Linux 客户端、等 suite 退出。
3. `finally` 调用 `prepare -Remove`；地址、规则、别名全部删除。
4. suite 自身的 watchdog 仍在 phase 异常时恢复空 forwarded 表与 disabled config。

## 3. 夹具改造

### 3.1 共享地址/scope helper

在 `tools/wfprobe/TestSupport.cs` 的 `SocketHelpers` 增加：

```csharp
internal static IPAddress WithScope(IPAddress address, uint ifIndex);
// IPv6 link-local 且 ScopeId == 0 时返回 new IPAddress(bytes, (int)ifIndex)，否则原值。

internal static bool SameAddress(IPAddress left, IPAddress right);
// 按 AddressFamily + GetAddressBytes() 比较，忽略 ScopeId。
```

用途：

- `BindOn` / 多 L listener 绑定链路本地时补 scope。
- decoy / 测试 socket 绑定 target 时补 scope。
- redirect 表断言比较 server 地址时忽略 scope。

### 3.2 TCP suite

`tools/wfprobe/ForwardedTcpSuite.cs`：

- **alias**：与 UDP suite 对齐，v6 + `--no-alias` 时跳过 `OnLinkAlias`。`RunAsync` 和
  `RunMultiAsync` 都使用同一个 helper；不再让 IPv6 在 `OnLinkAlias.Ensure` 的 IPv4
  检查处直接失败。
- **listener family**：新增
  `BindTcpAny(int port, byte family)`；v6 用 `IPAddress.IPv6Any` 且
  `Server.DualMode = false`，v4 保持 `IPAddress.Any`。core / upstreams / fail 改用它。
  verify listener 仍绑 v4 Any（Linux 客户端通过 VM IPv4 回报）。
- **multi**：
  - `--no-alias` + v6 时跳过 `secondL` / `externalTarget` 两个 alias。
  - `BindOn(address, port, ifIndex)` 对链路本地地址补 scope；仍然每个 L 一个 listener。
  - 断言 server/L 地址用 `SameAddress`，不再依赖 `IPAddress.Equals` 的 ScopeId 语义。
  - `RunMultiAsync` 继续按源端口 40500/40501 分跨入口组与同入口组；地址由 options
    传入，默认值保持 v4。
- **upstreams**：listener 改 family-aware；两个假 SOCKS5 仍绑 v4 loopback，不影响
  v6 目的。外部客户端顺序连 40250/40251，套件等 2 个 accept。
- `RelaySessionAsync`、`QueryFor`、`Socks5TestClient` 已有 v6 能力，无需改协议。

### 3.3 UDP suite

`tools/wfprobe/ForwardedUdpSuite.cs`：

- 把 `BindUdp(int port)` 改为 `BindUdp(int port, AddressFamily family)`，core / socks5 /
  capacity / reject / fail 都按 `remoteAddress.AddressFamily` 绑定。
- 所有 `EndPoint any = new IPEndPoint(IPAddress.Any, 0)` 改为
  `AnyFor(family)`：v4 `IPAddress.Any`，v6 `IPAddress.IPv6Any`。
- `TryBindDecoy(remoteAddress, remotePort, ifIndex)`：
  - 按 remote 族建 socket；
  - 链路本地地址先 `WithScope` 再 bind；
  - 保留 30 次 `AddressNotAvailable` 重试。
- `BindEphemeralUdp(backend.AddressFamily)`：backend 通常仍是 Linux IPv4，但 helper
  不再写死 v4。
- `RunSocks5Async` 使用 family-aware listener/any；`Socks5UdpFakeServer` 保持 v4
  loopback 上游，`Socks5UdpWire` / `Socks5UdpProbeClient` 已支持 atyp 0x04。
- `FindAdapterIndex` 用 `SameAddress` 匹配，避免链路本地 scope 导致找不到入口。
- fragment / fragment-host 已 family-aware，不改语义。

### 3.4 Linux 客户端

`tools/linux/forwarded_tcp_client.py`：

- target/verify 解析支持 `[v6]:port`、裸 `v6:port` 和 `%scope`；`socket.create_connection`
  负责选族与 scope。
- 数据/SHA/TCP ACK 校验保持不变；verify 仍走 v4。
- upstreams 由 gate 调用两次（40250/40251），不需要单进程多 target。

`tools/linux/forwarded_udp_client.py`：

- target 解析支持 v6；按 target 族创建 socket。
- v6 绑定 `("::", source_port)`，由内核选择链路本地源；发送用
  `getaddrinfo` 返回的 4 元组（含 scope）。
- 回程源比较忽略 scope（`ipaddress.ip_address` 比较地址，不比较 `%iface`）。
- sweep/capacity 仍只支持 v4；v6 请求 sweep 时明确报错，不静默跑错。
- verify 仍走 v4。

`tools/linux/forwarded_tcp_b6b.py`：

- `--bind-address` 改为可重复：1 个值用于全部 target，或与 target 数量一致时逐个配对。
- 按 target 解析族；创建 socket、设置 `SO_REUSEADDR`/`SO_REUSEPORT`。
- bind 地址支持 `%iface`，通过 `getaddrinfo` 得到 4 元组后 bind；跨入口多连接可以用
  同一源端口在不同接口的链路本地地址上。
- `result_line` 继续报告 local/bytes/SHA；verify 仍走 v4。

## 4. Linux 门禁

`tools/linux/forwarded_gate.py` 新增 `--include-v6`，默认行为完全不变。

- prepare：`-WithIpv6`；解析 `B6C-V6-READY` 的 key=value 到 `cfg`。
- 新增 phase（顺序在 v4 之后）：
  1. `tcp-core6`：suite `--phase core --no-alias`；客户端连
     `internal_target%eth0:40250`。
  2. `tcp-upstreams6`：suite `--phase upstreams --no-alias --expect-accepts 2`；
     客户端顺序连 40250/40251。
  3. `udp-core6`：suite `--phase core --no-alias`；客户端连
     `internal_target%eth0:40250`，10 个 datagram。
  4. `udp-socks5-6`：suite `--phase socks5 --no-alias`；客户端两个 target
     （40250/40251），source port base 区分。
  5. `tcp-multi6`：suite `--phase multi --no-alias`，传 L1/L2/External L 与两个 target；
     客户端两次：
     - 跨入口：Internal target%eth0 + External target%eth1，源端口 40500，
       两个 target 分别绑定 Linux eth0/eth1 链路本地；
     - 同入口：Internal target 的 40250/40251，源端口 40501，绑定 eth0 链路本地。
- gate 只读发现 Linux 链路本地地址（`ip -6 -o addr show dev <iface> scope link`），
  不需要 root。
- 所有 suite 参数带 `--ifindex 5`（Internal）和 `--local-ip <L1>`；multi 的 External
  ifIndex 由 suite 的 `FindAdapterIndex(externalLocalIp)` 解析。
- 失败时保留 `run.log` 与各 phase 日志；`finally` 仍调用 `prepare -Remove`。
- 文档 `tools/linux/forwarded_gate.md` 增加 `--include-v6` 前置条件、地址发现与清理。
- 现有 `run_phase` 的 `--no-verify` guard 不变。

## 5. 数据流与断言

### 5.1 TCP core v6

```
Linux client fe80::...%eth0
  -> target fe80::99:40250
  -> driver match redirect / DNAT -> L1 fe80::8ca8...:40400
  -> suite listener (IPv6Any, DualMode=false) accept
  -> suite query redirect: server=fe80::99, ingress=5, L=L1
  -> relay to v4 backend 192.168.100.4:40250
  -> return frame source L1:40400
  -> driver return SNAT -> source fe80::99:40250
  -> client sees original target; payload/SHA/ACK 一致
```

关键断言：server=target ≠ L1；`redirect_ingress/return/created`、`inject_failed=0`、
`return_failed=0`；套件数据与客户端 verify 一致。

### 5.2 UDP core v6

```
Linux client -> target fe80::99:40250 -> L1:40400 listener
  -> suite query/relay/echo -> return SNAT -> client sees fe80::99:40250
```

关键断言：所有 datagram 到达 listener；客户端 `source == target`；target 上的 decoy
收不到原帧；`udp_ingress/return/created` 与去向恒等式通过。

### 5.3 TCP multi v6

- 跨入口：Internal target%eth0 与 External target%eth1，源端口 40500；两条回程分别
  使用 Internal/External L，`ingress` 分别为 5/14。
- 同入口：Internal target 的 40250/40251，源端口 40501；两条回程使用 L1/L2，
  `redirect_multi_l >= 1`。
- 共 4 条连接，全部双向 2048B；`return_ambiguous=0`、`no_ingress=0`。

## 6. 兼容性与回滚

- 不改驱动 ABI（v8）、不改 blob 布局、不改产品网络语义；wfprobe/gate 改动对 v4
  默认路径保持兼容。
- v6 全部由 `--include-v6` / `-WithIpv6` 触发；不带开关时行为与当前一致。
- 环境回滚：`prepare -Remove` 删除新增地址和 `B6C-ENV-V6-*` 规则；suite watchdog
  恢复 forwarded config；listener 随进程退出。若脚本中断，手工执行
  `netsh interface ipv6 delete address ...` 与 `Remove-NetFirewallRule -DisplayName 'B6C-ENV-*'`。
- 如果实测发现链路本地 DNAT/注入不可行，回退方案是 ULA（Internal `fd00:100::/64`，
  Linux 侧需 root 加地址/路由）。该方案不进入默认实现，只作为阻断时的排查路径。

## 7. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 链路本地 scope 不一致导致 bind/断言失败 | `WithScope`、`SameAddress`；客户端 target 带 `%iface` |
| Windows 防火墙拦 v6 | prepare 显式加 `B6C-ENV-V6-*` 规则；先用单 phase 验证 |
| DAD 未完成就开测 | prepare 轮询 `AddressState=Preferred` |
| 主链路本地地址变化 | prepare 输出实际 L1/External L，gate 使用输出值，不写死 |
| `TcpListener` IPv6 DualMode 收到 v4 | v6 listener 显式 `DualMode=false` |
| UDP decoy bind 链路本地失败 | `WithScope` + 30 次重试；失败即明确报 decoy unbound |
| 跨入口同一源端口冲突 | SO_REUSEADDR/SO_REUSEPORT；按 target 逐接口绑定 |
| 测试只凭计数器通过 | 客户端内容/SHA/ACK、UDP 源地址、decoy 断言 |
| v4 回归被夹具改动破坏 | 先跑完整 v4 gate；v6 全部走新开关 |

## 8. 交付与后续

- 本任务通过后更新 `src/driver/README.md`、`src/userland/README.md`、
  `.trellis/spec/backend/wfp-architecture.md` 的 IPv6 状态，并写 `verification.md`。
- 后续独立任务：`WfpController` 的 v6 L 自动解析 / 配置字段；UDP 多 L 与多入口 v6；
  IPv6 热替换。
