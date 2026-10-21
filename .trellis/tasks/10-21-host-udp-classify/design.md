# 技术设计：Host UDP B2b

## 1. classify

当前 `probe_classify_fn` 在 `gather_facts` 后：

```cpp
if (facts.protocol != IPPROTO_TCP) { permitted_non_tcp++; permit; return; }
```

改为：

```cpp
if (facts.protocol != IPPROTO_TCP && facts.protocol != IPPROTO_UDP)
{
    permitted_non_tcp++; set_permit; return;
}
if (facts.protocol == IPPROTO_UDP) { udp_classify_calls++; }
```

之后 UDP 与 TCP 共用四道闸、matcher、三个动作。注意：`rules == nullptr` 的早退在协议判断
之前；UDP 也要计 `udp_classify_calls` 和 `udp_permitted`，因此把计数放在 facts 之后、
`rules == nullptr` 之前。

## 2. redirect

`redirect_classification(state, config, facts, rule_index, classify_context, filter, classify_out)`
已经有 `facts`。把：

```cpp
destination.protocol = transport_protocol::tcp; // B2a 硬编码
```

改为：

```cpp
destination.protocol = facts.protocol == IPPROTO_UDP ? transport_protocol::udp : transport_protocol::tcp;
```

`udp_redirected` 在成功 `finish_classify` 之后自增；失败路径（no handle / no writable /
no context）在 `permitted` 自增处同时 `udp_permitted`。

## 3. 计数器

在 `probe_state&` 附近加三个窄 helper，避免十来个返回路径重复写：

```cpp
void count_udp_permit(probe_state& state, std::uint8_t protocol) noexcept
{
    if (protocol == IPPROTO_UDP) probe_increment(state.stats.udp_permitted);
}
void count_udp_block(probe_state& state, std::uint8_t protocol) noexcept
{
    if (protocol == IPPROTO_UDP) probe_increment(state.stats.udp_blocked);
}
void count_udp_redirect(probe_state& state, std::uint8_t protocol) noexcept
{
    if (protocol == IPPROTO_UDP) probe_increment(state.stats.udp_redirected);
}
```

`udp_classify_calls` 在 facts 之后单独自增。所有最终 permit 路径（`enabled=0`、`rules=null`、
四道闸、redirect 失败、规则 permit/fallback permit）都要 `count_udp_permit`；block 路径
`count_udp_block`；redirect 成功 `count_udp_redirect`。

不变式（单次测试增量）：

```
udp_classify_calls == udp_permitted + udp_blocked + udp_redirected
```

只对“走过 gather_facts 且协议 UDP”的流成立；`no_write_right` / gather 失败不计数。

## 4. block filter

`src/driver/block_filters.cpp` 的实现：所有 block filter 都显式写 `IP_PROTOCOL` 条件：

```cpp
protocol_expansion expand_block_protocols(std::uint8_t protocol)
{
    if (protocol == rule_protocol_any)
        return { { rule_protocol_tcp, rule_protocol_udp }, 2 };
    return { { protocol, 0 }, 1 };
}
```

- `protocol=tcp` → 6；`protocol=udp` → 17；`protocol=any` → 6 + 17 两条 filter。
- `fallback_action=block` 也按 6/17 两条，不再使用无条件 filter。
- 原因：`ALE_AUTH_CONNECT` 不写协议条件时还会匹配首个出站 ICMP 等协议，而 classify
  对这些协议早退；显式 6/17 把策略限定在 TCP/UDP，ICMP 不被误拦。
- 容量：`max_block_filters` 64 → 128（any/fallback 每层两条）。
- 仍然只增加拒绝、从不放行。

## 5. 删除 B1 临时闸

`rule_table.cpp` 的 `udp-block-temporary-guard` 整段删除（含 mutation 标记与注释）。
删除后 UDP block 规则可以加载；`rule_order_valid` 与 fallback 冲突校验不变。

## 6. 热替换语义

UDP 的 auth filter 在关联建立时评估。B2b 实测（见 `research/b2b-smoke.md` §3）：

- 新关联：按新 blob 的 block/permit 生效；
- 旧关联：后续 datagram **会被新的 auth filter 丢弃**，但不会重新进入 `ALE_CONNECT_REDIRECT`，
  所以 `udp_classify_calls` / `udp_blocked` 不增加、也没有新事件。

这不是“旧关联继续放行”，而是“阻塞发生在 auth 层、classify 看不到”。因此 `udp_blocked`
只能定义成“classify 层 block 决策数”，不是实际丢包数。B3 U13 按实测固化。

## 7. VM 手工验证（临时 smoke）

B2b 不建正式 UdpSuite；用临时 `tools/wfprobe` 命令（跑完删除）或临时 C# harness：

1. 套件进程持有 UDP listener（`0.0.0.0`），`self_process_ids=[当前 PID]`；
2. `BlobBuilder` 构造 UDP redirect/permit/block/any 规则，`SetRuleBlob`；
3. 子进程 `wfprobe udp-client` 发 `sendto`/`connect`；
4. listener 校验 context；对端用 Linux 192.168.100.4 的 UDP echo 观测 block；
5. 热替换：长活 socket 先 permit，换 block blob 后再发，记录结果；
6. 每次测试 reset stats，断言计数器增量与不变式。

smoke 文件不进入提交；证据记录在任务 `research/b2b-smoke.md`。

## 8. 风险

| 风险 | 处理 |
|---|---|
| UDP 计数漏路径 | 三个 helper + 单次测试不变式；逐条返回路径审查 |
| `any` / fallback block 误拦 ICMP 等协议 | `any` 与 fallback 都按 6/17 各生成一条 filter，
  ICMP 不被 block；`max_block_filters` 相应提到 128 |
| 热替换旧关联不重新 classify | 记录为“auth 层丢弃、classify 不记账”；`udp_blocked` 定义成决策数，B3 U13 固化 |
| 四道闸对 UDP 的 redirect state/records 行为未测 | VM 用例覆盖 listener 端口、self-PID；其余由 B3 |
| TCP 回归 | 现有 rules/selftest/gates + 事件 protocol=6 |
