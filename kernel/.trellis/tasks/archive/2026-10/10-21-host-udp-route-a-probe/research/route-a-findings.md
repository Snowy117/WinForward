# 路线 A 探针：阶段性发现（2026-10-21）

> 环境：WinLTSC（Windows 11 IoT Enterprise LTSC），VM 192.168.100.2。
> 驱动：`wfprobe.sys`（本任务实验分支），用户态：`wfprobe.exe`（Debug/net11.0）。
> 目标：`1.2.3.4:443`，listener `0.0.0.0:5555`。

## 已确认

### 1. UDP local redirect 生效（unconnected）

命令：

```powershell
wfprobe control --enabled 1 --port 5555 --listener-pid <pid>
wfprobe udp-probe --enable 1 --port 5555 --pid <pid> --return-mode off
wfprobe udp-client --remote 1.2.3.4:443 --mode unconnected --payload hello
```

结果：

- `udp-probe-stats`: `seen=1 redirected=1 table=1`
- listener 收到 `from=192.168.77.2:63801 bytes=5`
- cmsg context 36B：`57464331 FFFFFFFF C80B0000 0100 BB01 04 11 0000 01020304 ...`
  - magic `WFC1`，rule=`0xFFFFFFFF`，pid=`0x0BC8`，version 1，port 443，family 4，protocol `0x11`（17/UDP），地址 `01 02 03 04` = 1.2.3.4。
- 结论：`IP_WFP_REDIRECT_CONTEXT` 的 cmsg 就是驱动设置的 `localRedirectContext`，逐 datagram 携带原始目的地。

### 2. connected UDP 也能被 redirect（首包）

命令：

```powershell
wfprobe udp-client --remote 1.2.3.4:443 --mode connected --payload hello
```

结果：

- `seen=1 redirected=1`
- listener 收到 `from=127.0.0.1:63803 bytes=5`
- 结论：微软文档里的“connected UDP 重定向丢包”在本机未复现；connected socket 的首包能到 listener。客户端收不到回包是因为 listener 没有回包，不是 redirect 失败。

### 3. 回程：relay 的 loopback 回包在 DATAGRAM_DATA 层不可见

命令：

```powershell
wfprobe udp-relay --bind 0.0.0.0 --port 5555 --count 1 --linger-ms 30000
wfprobe udp-probe --enable 1 --port 5555 --pid <relay-pid> --return-mode observe --layer inbound
wfprobe udp-client --remote 1.2.3.4:443 --mode unconnected --payload ping
```

结果：

- unconnected 客户端收到 `recv=4B from=192.168.77.2:5555 payload=ping`（relay 回包确实发出）。
- `udp-probe-stats`: `return-seen=0 return-matched=0`（inbound）。
- 改 `--layer outbound` 后仍然 `return-seen=0`。
- 结论：relay 的回包走 loopback，`FWPS_LAYER_DATAGRAM_DATA_V4` 的 inbound/outbound 都看不到，因此不能在该层 inline 改写源地址。
- 副作用：`DATAGRAM_DATA` 的 callout 对 local redirect 投递的 datagram 也不可见，说明 local redirect 的投递绕过了该层。

## 尚未验证

- `FWPS_LAYER_INBOUND_IPPACKET_V4` / `ALE_AUTH_RECV_ACCEPT_V4` 是否能看见 loopback 回包。
- `FwpsInjectTransportReceiveAsync0`（保存 redirect 时的 `transportEndpointHandle`）能否把回包注入应用，并把源地址写成 `original_dest`。
- 如果注入可行，每包一次用户态→内核 IOCTL 的成本。

## 初步判断

路线 A 的 redirect 与 context 两段已经成立；回程不能走 `DATAGRAM_DATA` inline 改写，需要二选一：

1. 驱动注入：redirect 时保存 `transportEndpointHandle`，回包由用户态交给驱动，驱动 `FwpsInjectTransportReceiveAsync0` 注入（源 = 原始目的地）。
2. 换层观察/改写：先在 `IPPACKET` inbound 验证 loopback 可见性，再决定。

### 4. IPPACKET inbound 也不可见

在 `fwprobe.sys` 里新增 `FWPS_LAYER_INBOUND_IPPACKET_V4` 的只读 callout，对每个 inbound IP 包解析 IPv4/UDP 头，
统计 `ippacket_seen` / `ippacket_matched`。

命令与第 3 节相同（relay + unconnected client）。

结果：

- 客户端仍收到 `recv=4B from=192.168.77.2:5555`；
- `udp-probe-stats`: `ippacket-seen=0 ippacket-matched=0`。

结论：relay 的 loopback 回包在 `DATAGRAM_DATA`（inbound/outbound）与 `INBOUND_IPPACKET_V4` 都不可见。
这说明 loopback 流量不经过这些 WFP 过滤层，**A1（relay 普通回包 + 驱动在包路径上改源）不可行**。
回程只能走驱动注入（`FwpsInjectTransportReceiveAsync0` / `FwpsInjectNetworkReceiveAsync0`），
或整个 UDP 数据面转 B（DDProxy 式 clone+inject）。

### 5. 下一步（A2）

- redirect 时从 `FWPS_INCOMING_METADATA_VALUES0` 保存 `transportEndpointHandle` 到映射表；
- 新增 IOCTL：用户态把回包 payload + app 地址交给驱动；
- 驱动构造 UDP NBL，`FwpsInjectTransportReceiveAsync0` 注入，源地址写 `original_dest`；
- 验证 connected 客户端接受回包，并测每包 IOCTL 的成本。

### 6. A2 注入尝试：`STATUS_NOT_ACCEPTED`

按 A2 实现了：
- `ioctl_udp_probe_return`（0x883）：用户态把 payload + app 地址 + 原始目的地交给驱动；
- 驱动构造 IPv4 + UDP 头（源 = 原始目的地，目的 = app），用 `FwpsAllocateNetBufferAndNetBufferList0`
  + 显式 MDL/buffer 分配 NBL；
- 依次尝试了 `FwpsInjectNetworkReceiveAsync0` 与 `FwpsInjectTransportReceiveAsync0`。

结果：

- IOCTL 返回成功，`injected original=1.2.3.4:4444 app=127.0.0.1:50451`；
- 完成回调里 NBL 的 `Status = 0xC000021B`（`STATUS_NOT_ACCEPTED`）；
- connected 客户端仍然收不到回包；
- `--interface 0` 时同步返回 `0xC000000D`（`STATUS_INVALID_PARAMETER`），说明 1 是合法的 loopback 索引，
  拒绝发生在注入阶段而不是参数校验。

判断：不是接口索引或 IP/UDP 头构造的明显错误，而是 WFP 拒绝“没有原始 flow 关联、且源地址不在本机”的
注入包。DDProxy 能工作，是因为它 clone 的是**真实网络接口收到的原始包**（源是真实服务器），只改端口/地址；
我们的 relay 回包没有任何“原始服务器包”可 clone，必须凭空伪造源地址。

### 7. 下一步候选

1. 验证 local redirect 是否改变了 app connected socket 的 `RemoteEndPoint`（若变成 relay 地址，回包源就不必伪造）。
2. 研究 `ALE_AUTH_RECV_ACCEPT` 层的 inbound redirect（`FwpsRedirectHandleCreate0` + redirect records），
   让 relay 的回包被 WFP 当作“原始服务器”投递给 app，而不是直接伪造 IP 包。
3. 若以上都不可行，路线 A 的 connected UDP 回程只能放弃透明源；unconnected 应用仍可用。
4. 转 B 也要面对同一个伪造源问题（B 的回程同样需要源 = 原始服务器）。

### 8. 关键修正：connected 客户端接受 relay 的普通回包

实验：connected 客户端 + **普通** `udp-relay`（不加 `--inject`）：

```powershell
wfprobe udp-relay --bind 0.0.0.0 --port 5555 --count 1 --linger-ms 30000
wfprobe udp-probe --enable 1 --port 5555 --pid <relay-pid> --return-mode off --filter-port 4444
wfprobe udp-client --remote 1.2.3.4:4444 --mode connected --payload ping
```

结果：

- `[udp-client] #0 sent=4B recv=4B from=127.0.0.1:5555 payload=ping`
- relay 日志：`from=127.0.0.1:61285 ... echoed to 127.0.0.1:61285`

结论：**local redirect 之后，connected app 的 socket 接受源为 relay 地址的回包**。
也就是说 WFP 已经把该 flow 的 remote（连同 local）按 redirect 目标调整过了，relay 不需要伪造源、
不需要注入。A2 的“伪造源注入”是不必要的路径；第 6 节的 `STATUS_NOT_ACCEPTED` 只说明这条
路走不通，并不阻塞路线 A。

因此路线 A 的回程就是：relay 用收到 datagram 的 socket 直接 `sendto(app)`，源地址是 relay 的
地址/端口，connected 与 unconnected 客户端都能接受。
