# 技术设计：B4b TCP DNAT、redirect 表与回程中继

## 1. 配置与 ABI

ABI 升到 v5（B4a 的 v4 结构不变）：

```c
struct probe_forwarded_config {
    uint32_t enabled;
    uint16_t listener_port;
    uint8_t  family;        // 4 / 6
    uint8_t  reserved0;
    uint8_t  listener_address[16];
    uint32_t reserved1;
};
```

- `ioctl_set_forwarded_config` / `ioctl_get_forwarded_config`；
- `ioctl_query_forwarded_redirect`：

```c
struct probe_forwarded_redirect {
    uint32_t magic;          // 'WFR1'
    uint16_t version;
    uint8_t  family;
    uint8_t  protocol;       // 6
    uint8_t  client_address[16];
    uint16_t client_port;
    uint8_t  server_address[16];
    uint16_t server_port;
    uint16_t listener_port;
    uint16_t reserved0;
    uint32_t ingress_ifindex;
    uint32_t upstream_id;
    uint32_t rule_index;
    uint32_t state;
};
```

- 用户态按 `(listener_port, client_ip, client_port)` 查询；查不到返回 NOT_FOUND。
- `probe_forwarded_info` 增加 redirect 表计数（active/high_water/rejected），或在 stats 里；
  B4b 优先把计数放 stats，避免改 info 布局。

## 2. redirect 表

`probe_forwarded_redirect_entry`：

| 字段 | 说明 |
|---|---|
| ingress_ifindex | 入口接口 |
| client_ip/port | 客户端 |
| server_ip/port | 原始服务器 |
| listener_ip/port | DNAT 目标（L + listener port） |
| client_mac[6] | 入口帧源 MAC，回程用 |
| upstream_id | B2 的 target id |
| rule_index | 命中的 forwarded 规则 |
| state | syn/established/closing |
| last_activity | 超时 |
| generation | 热替换 |

- key：`(ingress, client_ip, client_port, server_ip, server_port)`；
- 回程 key：`(listener_port, client_ip, client_port)`，由入口表项派生；
- 容量 4096；满 → fail closed + `redirect_capacity`；
- 超时 300s（established），FIN/RST 时标记 closing，grace 后回收；
- 表项在 SYN 时建立，后续数据包按 key 命中；
- 热替换：旧表项保留 generation，新表项按新规则；旧流继续用旧 upstream。

## 3. 入口路径（inbound L2 classify）

在 B4a 的匹配之后：

1. `FwpsQueryPacketInjectionState0` → self-injected → permit（防环）。
2. 匹配 forwarded 规则；action=permit/block 保持 B4a。
3. action=redirect：
   - 非 TCP → block + 计数（B5 再接 UDP）。
   - 查表命中 → DNAT（dst=L:listener），重算 IP/TCP 校验和，clone/改写后
     `FwpsInjectMacReceiveAsync0`，原帧 `FWP_ACTION_BLOCK`。
   - 未命中且是 SYN → 建表（含 client MAC、upstream、rule），同上注入。
   - 未命中且非 SYN → fail closed block + `redirect_no_state`（无法中继已建立连接）。
   - 无 config/无 L/表满/注入失败 → block + 对应计数；`last_status` 记录。
4. 只做 TCP；UDP redirect 在 B4b 先 block（B5 放开）。

## 4. 回程路径（outbound L2 classify）

1. 防环 permit。
2. 若 TCP 源端口 == listener_port：
   - 按 `(listener_port, dst_ip, dst_port)` 查回程表；
   - 命中 → SNAT 源 IP/端口为 server，目的 MAC=client_mac，源 MAC=入口适配器 MAC
     （从入口帧的目的 MAC 取），重算校验和，`FwpsInjectMacSendAsync0` 注入入口网卡，
     原帧 BLOCK。
   - 未命中 → permit（普通本机流量，不能误改）。
3. 其它出向包 → permit。

## 5. 注入与帧处理

- 以太网头：入口帧目的 MAC 是本机；DNAT 后保持。回程源 MAC 用入口帧目的 MAC，
  目的 MAC 用表项 `client_mac`。
- VLAN：入向/出向都按 B4a 的解析偏移改写；保持 VLAN 标签。
- 校验和：IPv4 header + TCP（IPv6 伪头）；原地/克隆改写后重算。
- 不碰序列号/ACK/窗口/选项。
- `FwpsInjectMacReceiveAsync0` 入栈、`FwpsInjectMacSendAsync0` 出栈；注入帧长度不变。
- 失败：记录 NTSTATUS、方向、帧长；计数；原帧 block（不能 fail-open）。

## 6. 用户态接缝

新增 `IRedirectContextSource`：

```csharp
public interface IRedirectContextSource
{
    bool TryRead(Socket client, out RedirectDestination destination, out string error);
}
```

- host 实现：现有 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT`；
- forwarded 实现：`ioctl_query_forwarded_redirect`，按 listener 端口 + client 查；
- `RelayServer` 依次尝试 forwarded、host；都失败 → 拒绝。
- `RedirectDestination` 增加 `UpstreamId`/`IngressIfIndex`（或新类型），中继按
  `ForwardedUpstreams[upstream_id].Endpoint` 选 SOCKS5。
- `WfpController`：
  1. 绑 listener（host 已有）；
  2. `SetForwardedConfig(enabled=0, listener_port, L)`；
  3. `SetForwardedBlob`；
  4. 回读校验；
  5. `SetForwardedConfig(enabled=1)`；
  6. 退出/回滚：enabled=0、清表、恢复空 permit。

## 7. E2E 拓扑

- Linux（192.168.100.4）：
  - 临时路由 `10.99.0.0/24 via 192.168.100.2`；
  - 目标 echo/HTTP 服务监听 40250；
  - fake SOCKS5 上游（40000–41000）或真实 SOCKS5。
- Windows：
  - forwarded 规则：adapter Internal、client 192.168.100.0/24、remote 10.99.0.0/24、
    TCP 40250、redirect、target；
  - listener 端口（与 host 共用或独立，第一版独立更简单）；
  - 客户端连 `10.99.0.1:40250`。
- 断言：
  - listener accept；IOCTL 查到 server=10.99.0.1:40250、upstream；
  - 字节一致；客户端看到对端 10.99.0.1:40250；
  - 原帧 block（抓包/计数）；
  - 半关闭/RST；
  - Windows route print 不变；Linux 路由测试后删除。

## 8. 失败/容量/热替换

| 条件 | 行为 |
|---|---|
| 无 forwarded config / L | block + `redirect_no_config` |
| 表满 | block + `redirect_capacity` |
| 非 SYN 且无表项 | block + `redirect_no_state` |
| 查询失败 | 用户态拒绝 accept；计数 |
| 上游失败 | RST/关闭；不直连 |
| 注入失败 | block + `inject_failed`；不 fail-open |
| 热替换 | 旧表项保持旧 upstream；新表项新规则；generation |

## 9. 风险

| 风险 | 处理 |
|---|---|
| 原帧未消费导致客户端收到 RST/重复 | 入口/回程都 BLOCK 原帧；抓包验证 |
| 注入帧被再次 classify | 显式 `FwpsQueryPacketInjectionState0` |
| 回程误改普通 listener 包 | 必须查表命中才 SNAT；未命中 permit |
| L2 注册失败拖垮 host | B4a 已记录；B4b 不改结构 |
| 表项泄漏/超时 | FIN/RST + 超时；容量上限；`redirect_active` 计数 |
| 多入口/多 L | 第一版单入口；B6 扩展 |
| 校验和/片段 | 只处理首片；非首片按 B4a 的 malformed/permit 策略，文档写明 |
