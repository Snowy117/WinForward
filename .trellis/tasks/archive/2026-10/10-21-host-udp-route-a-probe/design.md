# 技术设计：Host UDP 路线 A 最小探针

> 实验性设计。目标是回答"路线 A 能不能用"，不是交付产品代码。
> 所有实验改动集中在独立模块与独立 IOCTL，实验结束后整体删除或转正。

## 1. 边界与落点

| 改动 | 位置 | 性质 |
|---|---|---|
| 实验配置/统计 IOCTL | `src/driver/probe_ioctl.h` 追加 0x880/0x881 | 临时，不进 ABI 版本 |
| 实验模块 | 新增 `src/driver/udp_probe.h` / `udp_probe.cpp` | 临时 |
| UDP redirect 分支 | `probe_classify.cpp` 中 `facts.protocol != IPPROTO_TCP` 之前 | 受实验开关控制 |
| 回程 callout | `probe_driver.cpp` 注册；`udp_probe.cpp` 实现 | 受实验开关控制 |
| 用户态命令 | `tools/wfprobe/Udp*.cs` + `Program.cs` + `Protocol.cs` | 临时 |
| 实验记录 | `.trellis/tasks/10-21-host-udp-route-a-probe/research/` | 入库 |

不改：`rule_blob.h`、`matcher.h`、现有 TCP classify 的判定路径、正式 `probe_event`/`probe_stats`。

## 2. 实验拓扑

客户端与 listener 都在 VM 本机（WinLTSC，192.168.100.2），目标地址用不可达的 `1.2.3.4:443`
（避免真实服务干扰），驱动把首包 redirect 到 `127.0.0.1:<listener_port>`：

```
udp-client --remote 1.2.3.4:443
        │  (connect+send 或 sendto)
        ▼
  wfprobe.sys  ALE_CONNECT_REDIRECT_V4  ── 匹配实验开关 ──▶ localRedirectTargetPID/Handle/Context
        │  remoteAddressAndPort := 127.0.0.1:listener_port
        ▼
  udp-listen / udp-relay  (127.0.0.1:listener_port)
        │  WSARecvMsg: cmsg = IP_WFP_REDIRECT_CONTEXT(70)
        │  回包 sendto(client)
        ▼
  wfprobe.sys  回程层（DATAGRAM_DATA inbound 或 ALE_AUTH_RECV_ACCEPT）
        │  source := original_dest
        ▼
  udp-client 收到 echo（connected 模式要求源匹配）
```

第二组目标用 `8.8.8.8:53`，验证不同 remote port / 不同地址下 cmsg 与映射仍正确。

## 3. 驱动设计

### 3.1 实验 IOCTL

```c
struct udp_probe_config {
    uint32_t version;          // 1
    uint32_t enabled;
    uint32_t listener_pid;
    uint16_t listener_port;
    uint8_t  return_mode;      // 0=off, 1=observe, 2=rewrite_inline, 3=rewrite_inject
    uint8_t  return_layer;     // 0=datagram_data, 1=recv_accept
    uint16_t remote_port_filter; // 0=all
    uint16_t reserved;
};

struct udp_probe_stats {
    uint64_t seen;             // 进入 UDP 实验分支的次数
    uint64_t redirected;
    uint64_t redirect_failures;
    uint64_t return_seen;      // 回程层看到的包
    uint64_t return_matched;   // 命中映射表
    uint64_t return_rewritten;
    uint64_t return_failures;
    uint32_t table_entries;
    uint32_t last_status;
    uint32_t abi_version;
    uint32_t reserved;
};
```

IOCTL：

- `ioctl_udp_probe_set = make_ioctl(0x880)`：`METHOD_BUFFERED`，输入 `udp_probe_config`；
  admin-only；校验 `version` 与 `return_mode` 范围；关闭时清空映射表。
- `ioctl_udp_probe_get_stats = make_ioctl(0x881)`：输出 `udp_probe_stats`。

### 3.2 UDP redirect 分支

在 `probe_classify_fn` 中，`gather_facts` 之后、现有 `facts.protocol != IPPROTO_TCP` 早退之前：

1. `udp_probe.enabled == 0` → 走现有 `permitted_non_tcp`（行为完全不变）。
2. `facts.protocol != IPPROTO_UDP` → 原有 TCP 路径。
3. 命中 `remote_port_filter != 0 && facts.remote_port != filter` → 放行并计数。
4. 依次复用现有四道闸：listener 端口、`previousVersion->localRedirectHandle`、
   `FwpsQueryConnectionRedirectState0`、条件标志、self-PID。任何一道命中 → 放行。
5. 查映射表是否有空位；没有 → `redirect_failures++`，放行（fail-open 直连），**不 block**。
6. 分配 `redirect_context`，`protocol = udp`，填原始目的、PID、rule_index = 0xFFFFFFFF；
   写 `localRedirectTargetPID = listener_pid`、`localRedirectHandle = state.redirect_handle`、
   `localRedirectContext/Size`；`remoteAddressAndPort` 复用 `probe_build_redirect_target`
   （ANY → loopback，否则本地地址）。
7. 写映射表：key = `(localAddressAndPort of the request)`，value = `(remoteAddressAndPort 原始值)`。
8. `redirected++`，`probe_log_event` 记一条实验 decision（复用 `probe_event`，`matched_rule=0`）。

实验阶段**不接规则表**：所有符合条件的 UDP flow 都 redirect。这样实验只验证机制，
不会因为规则 blob 的 UDP 编译未实现而阻塞。

### 3.3 映射表

- 容量 `udp_probe_table_capacity = 64`（实验值），固定数组，`KSPIN_LOCK` 保护。
- 表项：`{ local_addr[16], local_port, remote_addr[16], remote_port, family, last_seen_100ns, valid }`。
- 插入：表满则拒绝（fail-open 直连 + 计数）。
- 查询：回程层按 `(dest local addr/port)` 线性扫描（64 项，DISPATCH_LEVEL 可接受）。
- 过期：实验里不主动清理，关闭开关时整表清空；产品设计另定。

### 3.4 回程分支

先只注册 `DATAGRAM_DATA_V4` 的 callout（IPv4），配置 `return_layer = datagram_data`。
若 observe 阶段发现 loopback 回包不出现在该层，再增量注册 `ALE_AUTH_RECV_ACCEPT_V4`，
用配置切换生效层。

`udp_probe_return_fn` 逻辑：

1. `udp_probe.enabled == 0 || return_mode == 0` → `FWP_ACTION_PERMIT`。
2. 只处理 `FWP_DIRECTION_INBOUND` 的 UDP 包。
3. 识别 relay 回包：源端口 == `listener_port`（源地址可以是 127.0.0.1 或本机地址），
   目的地址/端口命中映射表。
4. 查表得 `original_dest`。
5. 按 `return_mode`：
   - `observe`：`return_seen++`；`DbgPrint("[wfudp] return seen src=%x:%u dst=%x:%u")`；PERMIT。
   - `rewrite_inline`：`FwpsAcquireClassifyHandle0` + `FwpsAcquireWritableLayerDataPointer0`
     取 `FWPS_DATAGRAM_DATA0`，把 `remoteAddressAndPort`（inbound 的源）改成 `original_dest`，
     IPv4 UDP 校验和置 0（栈重算），`FwpsApplyModifiedLayerData0`。失败 → `return_failures++`，PERMIT。
   - `rewrite_inject`：`FwpsAllocateCloneNetBufferList0` → 改 UDP 头源端口 + 构造
     `FWPS_TRANSPORT_SEND_PARAMS0`（`remoteAddress = original_dest`）→
     `FwpsInjectTransportReceiveAsync0`；原包 `FWP_ACTION_BLOCK | FWPS_CLASSIFY_OUT_FLAG_ABSORB`。
     这是 inline 改写不可用时的兜底。

`injectionHandle` 在 `DriverEntry` 用 `FwpsInjectionHandleCreate0(AF_INET, ...)` 创建，
卸载时 `FwpsInjectionHandleDestroy0`；只在 `rewrite_inject` 模式下需要。

### 3.5 注册与卸载

- `probe_driver.cpp`：在现有 redirect callout 注册之后，注册 `DATAGRAM_DATA_V4` 的实验 callout
  与 filter（子层沿用现有 provider/subLayer）。注册失败不阻止驱动加载，只把实验置为不可用。
- 卸载：反序注销实验 callout/filter；`FwpsInjectionHandleDestroy0`；清空映射表。
- 实验开关关闭时 callout 仍被调用，但第一行直接 PERMIT（每包一次函数调用，实验可接受）。

## 4. 用户态设计

### 4.1 cmsg 收包（`tools/wfprobe/UdpSockets.cs`）

- 常量：`IpWfpRedirectContext = 70`（`IPPROTO_IP`）。
- `setsockopt(socket, IPPROTO_IP, 70, 1)` 必须在 `bind` 前后都可以，实验放在 `bind` 之后。
- P/Invoke：`WSARecvMsg`（`LPFN_WSARECVMSG` 通过 `WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER)` 取），
  结构 `WSAMSG` / `WSABUF` / `WSACMSGHDR`。
- 解析：遍历 `WSA_CMSG_FIRSTHDR` / `WSA_CMSG_NXTHDR`，匹配 `cmsg_level == IPPROTO_IP (0)` 且
  `cmsg_type == 70`；打印 `cmsg_len`、hex、以及按 `redirect_context`（36 字节）尝试解析的字段。
- 同时支持 `IP_WFP_REDIRECT_RECORDS(60)` 的观测（只打印长度），用于判断是否需要 records。

### 4.2 命令

| 命令 | 作用 | 关键参数 |
|---|---|---|
| `udp-probe` | 配置驱动实验开关 / 打印统计 | `--enable 0/1 --port N --pid P --return-mode off/observe/rewrite-inline/rewrite-inject --layer datagram/recv --filter-port N` |
| `udp-listen` | 收 datagram，打印 source + cmsg | `--bind A.B.C.D --port N --count K --timeout-ms M` |
| `udp-client` | 发 datagram，可选等待回复 | `--remote HOST:PORT --mode connected/unconnected --payload TEXT --count K --timeout-ms M` |
| `udp-relay` | 收包后 `sendto(source)` 回 echo | `--bind A.B.C.D --port N --count K --linger-ms M` |

`udp-listen` / `udp-relay` 启动后先打印 `LISTENER-PID=<pid>` 与 `LISTENER-PORT=<port>`，
便于脚本把 PID/端口喂给 `udp-probe`。实验用固定端口（默认 5555），避免动态端口与驱动配置的时序问题。

### 4.3 协议镜像

`tools/wfprobe/Protocol.cs` 追加 `UdpProbeConfig` / `UdpProbeStats` 的 `StructLayout` 镜像，
字段顺序与 `probe_ioctl.h` 一致；`ProtocolLayout.Verify()` 增加尺寸断言（config=24, stats=64）。
`WfpDevice` 追加 `SetUdpProbeConfig` / `GetUdpProbeStats` 两个 IOCTL 封装。

## 5. 实验矩阵

| 编号 | 场景 | 预期 | GO 判据 |
|---|---|---|---|
| E0 | 实验关闭，unconnected 发 `1.2.3.4:443` | 驱动 `permitted_non_tcp++`，listener 收不到 | 基线，确认实验开关隔离 |
| E1 | 实验开启，unconnected | listener 收到；cmsg 原始目的 = `1.2.3.4:443` | A2 + A3 |
| E2 | 实验开启，connected | 同 E1；若失败记录错误码/丢包 | A1 |
| E3 | `udp-relay` 回包，unconnected 客户端 | 客户端收到 echo | A4 前半 |
| E4 | `udp-relay` 回包，connected 客户端 + 回程改写 | 客户端收到，且源 = `1.2.3.4:443` | A4 后半（生死点） |
| E5 | 200 次 redirect + 卸载 | 池计数归零 / 无泄漏；`sc stop` 干净 | A5 |
| E6 | 可选：`8.8.8.8:53`、dual-mode | 行为一致或记录差异 | 不进 GO 判据 |

E4 的三种回程模式按 `observe → rewrite_inline → rewrite_inject` 顺序尝试；每一步都记录
"包是否出现、字段、改写是否成功、客户端是否接受"。

## 6. 失败姿态与回滚

- redirect 失败（无 context / 无表位 / apply 失败）：fail-open 直连 + 计数 + `DbgPrint`，不 block。
- 回程改写失败：计数 + `DbgPrint`，包按原样走（connected 客户端会丢，属可观测失败）。
- 实验开关关闭：UDP 完全回到现状。
- 回滚：删除 `udp_probe.*`、IOCTL 0x880/0x881、`probe_driver.cpp` 的注册段、C# 的 `Udp*.cs`
  与 `Program.cs` 分支；`probe_driver.h` 的 `probe_state` 字段回退。

## 7. 风险

1. `IP_WFP_REDIRECT_CONTEXT` 的 cmsg 内容是驱动的 `localRedirectContext` 还是 WFP 合成的
   SOCKADDR，文档没有逐字节说明；E1 会直接给出答案。
2. connected UDP 可能完全收不到 redirect（微软已知问题）；E2 决定 A 的生死。
3. loopback 回包可能不在 `DATAGRAM_DATA` inbound 出现；E4 observe 阶段确认，必要时换层。
4. `FWPS_DATAGRAM_DATA0` 的地址字段可写性与校验和语义需要实测；不成立就走 inject 兜底。
5. 实验用 `DbgPrint` 在 release 也输出，可能影响吞吐；实验只测功能，不测性能。

## 8. 证据与产物

- 每个实验的命令与输出保存到 `research/logs/E<N>-<name>.txt`。
- `research/route-a-findings.md` 按 E0~E6 分节，每节：命令、观察、结论、失败原因、复现步骤。
- 最终给出 GO / CONDITIONAL GO / NO-GO 与依据；若 CONDITIONAL，写明"哪一条不满足、用什么兜底"。

## 9. connected UDP 不通时的 workaround 探索

用户要求：如果 E2（connected UDP redirect）失败，不能只记录失败，要尝试让它通或找到可用的兜底。
实验按以下顺序做，每类记录"是否让 connected 客户端收到、代价是什么"。

### 9.1 候选手段

| # | 手段 | 机制 | 预期代价 | 失败时的含义 |
|---|---|---|---|---|
| W1 | redirect 目标改走本机网卡地址 | 不复用 ANY→loopback 分支，改 `remoteAddressAndPort` 为当前网卡的本地地址（D6 的另一条分支） | 0，只是地址选择 | loopback 路径是问题根源 |
| W2 | context/handle 组合对照 | 分别试「有无 `localRedirectContext`」「有无 `localRedirectHandle`」「PID 是否正确」三组，定位是否因记录不完整丢包 | 0~1 天 | 是驱动记录不完整，不是 OS |
| W3 | relay 侧设置 redirect records | 中继的上游 socket 用 `SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS` 挂 records，让 WFP 把回包关联回原 flow | 每 flow 一次 WSAIoctl | records 缺失是问题根源 |
| W4 | A+B 混合兜底 | 决策仍在 ALE；对 connected flow 改走 `DATAGRAM_DATA` 的 clone+inject（B 的数据面），对 unconnected flow 继续走 A | 每包 clone+inject；驱动多一套数据面 | A 无法覆盖 connected，但产品可用 |

补充观察（不单独作为一类，但记录）：

- 目标端口用 53/443 是否有差异（Windows DNS client 对源地址最敏感）；
- redirect 到 listener 的端口是否必须是 `0.0.0.0` 监听（当前用 `0.0.0.0:5555`）；
- `FwpsQueryConnectionRedirectState0` 在 connected flow 上的返回值；
- 是否只有首次 `connect` 丢包、后续 datagram 正常。

### 9.2 实验入口（E7）

在 E4 之后追加 E7，按 W1→W4 顺序：

1. `udp-probe --redirect-target loopback|local` 切换目标地址分支，重跑 E2。
2. `udp-probe --context on|off --handle on|off` 重跑 E2；记录 `redirected` / 客户端错误码 /
   listener 是否收到。
3. `udp-relay --attach-records`（relay 用 `SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS` 挂上从
   listener 侧读到的 records）重跑 E4。
4. 若前三类都不行，在实验驱动里加 `--fallback packet`：对 connected flow 走 B 的数据面
   （`DATAGRAM_DATA` clone+inject），重跑 E3/E4，确认兜底可用。

### 9.3 判定

- 任一类让 connected 客户端端到端收到 → 记录该类为 "A 的补丁"，GO 但附条件。
- W4 可用 → 产品可以采用 "A 为主、B 兜底 connected" 的混合路径；此时 GO 的条件是两套数据面
  都要维护，成本要写进 findings。
- 全部不可用 → NO-GO，路线 A 只能覆盖 unconnected UDP，产品需整体走 B。
