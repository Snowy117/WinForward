# Host UDP B2b：VM 手工验证记录（2026-10-21）

> 环境：WinLTSC（Windows 11 IoT Enterprise LTSC，10.0.26100.0），VM `192.168.100.2`；
> 对端 Linux `192.168.100.4`（UDP echo 40050/40052/40054，UDP sink 40051，TCP echo 40053）。
> 驱动 ABI v3，Debug/x64，测试签名。工作区用 tar 同步，`load-driver.ps1` 构建/安装。
> 临时 smoke 为 `tools/wfprobe/UdpSmoke.cs`（命令 `udp-smoke` / `udp-smoke-child`），验证后删除，
> 不在提交中。本文记录的是删除前的运行结果。

## 1. 构建与安装

```text
load-driver.ps1: 签名状态 Valid；服务状态 RUNNING
dotnet build tools/wfprobe/wfprobe.csproj -c Release: 0 warning / 0 error
```

## 2. UDP 用例

每个用例开始前 `wfpbe reset`，结束后读取增量；`udp` 四元组顺序为
`calls/permitted/blocked/redirected`，括号内为 class 增量。所有用例末尾的
`udp_classify_calls == udp_permitted + udp_blocked + udp_redirected` 断言均 PASS。

| 用例 | 结果 | UDP 计数（calls/permitted/blocked/redirected） | 其它断言 |
|---|---|---|---|
| redirect，unconnected `sendto` 两条 | listener 收到 2 条，36B context | 1/0/0/1 | context magic/version/protocol=17/family=4/PID/原始地址端口/rule=0 正确；同一 socket 同一目的地只 classify 一次；`redirected` +1 |
| redirect，connected `connect`+`send` | listener 收到 1 条，context protocol=17 | 1/0/0/1 | context PID/原始端口正确；`redirected` +1 |
| permit，命中 UDP permit 规则 | 直连 Linux echo 收到回包，listener 无包 | 1/1/0/0 | `matched_permit` +1；`fallback_redirect` 不变 |
| fallback=redirect，未命中规则 | listener 收到，事件 `matched_rule=0xFFFFFFFF` | 1/0/0/1 | `fallback_redirect` +1；事件 action=redirect、protocol=17 |
| UDP block（`protocol=udp`） | 应用 `sendto` 成功、`recv` 超时；Linux 40051 未收到 | 1/0/1/0 | `blocked`/`deferred_blocks`/`matched_block` 各 +1；同一 blob 下 40052 可达（1/1/0/0） |
| `protocol=any` block | UDP 静默丢包；TCP 连接 `AccessDenied(10013)` | UDP 1/0/1/0 | TCP 侧 `classify=1 blocked=1`，UDP 计数增量为 0；两条都验证 |
| 热替换 | 旧 socket 第一包 permit；换 block blob 后第二包无回显；新关联被丢 | 第一包 1/1/0/0；旧关联第二包 0/0/0/0；新关联 1/0/1/0 | 详见 §3 |

补充：`udp-smoke` 启动时先设置 `enabled=0` 再 reset，随后配置 listener/self-PID；listener 在
suite 进程内，客户端在子进程（`udp-smoke-child`），因此首包必须经过 classify。
**修正（B3 实测）**：同 socket 回包复用入站关联，不重新进入 `ALE_CONNECT_REDIRECT`，
`udp_classify_calls` 不增；此前“回包走 self-PID 闸”的描述不准确。

## 3. 热替换的实际观察

步骤：permit blob（UDP 40050 permit，fallback permit）→ 子进程持有长活 socket 发 `hot-1` 并收到
Linux echo → suite 换 block blob（UDP 40050 block，fallback permit）→ 同一 socket 发 `hot-2`。

实际结果：

```text
PHASE1-SENT#0=5B
PHASE1-RECV#0=5B from=192.168.100.4:40050 payload=hot-1
PHASE2-SENT#0=5B
PHASE2-RECVFAIL#0=TimedOut (10060)
[udp-smoke] hot-replace old-association delta: calls=0 permitted=0 blocked=0 redirected=0
```

Linux 对端日志只出现 `hot-1`，没有 `hot-2`。也就是说：

- 旧关联的后续 datagram **确实被新 block filter 丢弃**（不是“旧关联继续放行”）；
- 但该 datagram **没有重新进入 redirect classify**，所以 `udp_classify_calls` / `udp_blocked`
  都不增加，也没有新事件；
- 新关联按新 blob 正常 classify 为 block，计数 +1。

这与 design §6 的预期（“旧关联可能继续放行直到 socket 关闭”）不同。对产品语义是更强的保证，
但意味着“classify 决策计数”不等于“实际被丢的 datagram 数”。B3 的 U13 需要按此实测固化，并决定
是否需要在文档中把 `udp_blocked` 明确定义为“classify 层 block 决策数”。

## 4. 计数器不变式

三个 helper 只在 `protocol == IPPROTO_UDP` 时自增；`udp_classify_calls` 在 `gather_facts` 之后、
`rules == nullptr` 之前自增；`enabled=0` / `rules=null` / 四道闸 / redirect 失败 / 规则与 fallback
permit 都走 `count_udp_permit`，block 出口走 `count_udp_block`，redirect 成功走
`count_udp_redirect`。临时 smoke 对每个用例单独 reset 后断言增量等式，全部 PASS：

```text
redirect-unconnected: udp calls=1 permitted=0 blocked=0 redirected=1
redirect-connected:   udp calls=1 permitted=0 blocked=0 redirected=1
permit:               udp calls=1 permitted=1 blocked=0 redirected=0
fallback-redirect:    udp calls=1 permitted=0 blocked=0 redirected=1
udp-block:            udp calls=1 permitted=0 blocked=1 redirected=0
udp-block-control:    udp calls=1 permitted=1 blocked=0 redirected=0
any-block-udp:        udp calls=1 permitted=0 blocked=1 redirected=0
hot-replace phase1:   udp calls=1 permitted=1 blocked=0 redirected=0
hot-replace phase2:   udp calls=0 permitted=0 blocked=0 redirected=0
hot-replace new:      udp calls=1 permitted=0 blocked=1 redirected=0
```

## 5. UDP redirect context 生命周期（B3 需要关注）

干净启动后 `wfprobe pooltag --tag WfPr` 为“未找到 tag”（无基线）。分别只跑单个 redirect 用例后：

| 用例 | allocs 增量 | frees 增量 | outstanding 增量 |
|---|---:|---:|---:|
| redirect-unconnected（同一 socket 2 条） | +1 | +1 | 0 |
| redirect-connected（connect+send 1 条） | +1 | 0 | **+1** |
| fallback-redirect（unconnected 1 条） | +1 | +1 | 0 |

结论：**connected UDP 的 redirect context 在客户端进程退出后没有及时释放**。unconnected 与
fallback（同样是 unconnected 路径）都正常释放。connected-only 用例运行后 180 秒复查，outstanding
仍为累积值（3，含此前各次 connected 用例），没有出现后续释放；该 outstanding 在驱动 `sc stop`
之后仍然存在，说明不是当前驱动实例停止时能回收的分配。
这个现象在 B2b 的验收范围之外（B3 U12 才做池断言），但 B3 的“allocs == frees、关闭 socket 后
回基线”断言会因此失败，需要先确认这是 WFP 对 connected UDP association 的延迟释放（需要更长
观察窗口）还是真正的 context 泄漏。

另：第一次跑 `wfprobe rules` 时 `没有未释放的 context (expected=0 actual=1)` FAIL，原因是该 VM
在本批之前已有历史驱动实例残留的 `WfPr` 块；`sc stop` 后仍为 1，证明不是本次运行泄漏。重启 VM
取得干净基线后 `rules` 全 PASS（`WfPr` 基线“未找到 tag”，最终 outstanding=0）。因此 B3 的池断言
必须从干净启动开始，或按 `PoolTags.Zero` 语义减去基线。

## 6. TCP 回归（干净启动）

全部 PASS：

- `wfprobe rules --iterations 3`：`verdict=PASS failures=0`；`没有未释放的 context` PASS；
- `wfprobe selftest --remote 192.168.100.4:40053 --iterations 3`：V4，`verdict=PASS`；
- `wfprobe selftest --dual --remote 192.168.100.4:40053 --iterations 3`：V6/dual，`verdict=PASS`；
- `wfprobe gates`：四道闸，`verdict=PASS failures=0`。

事件/上下文 protocol 字段在 TCP 用例中仍为 6，未出现 ABI 或布局回归。

## 7. 其它观察

- `wfprobe control --clear-self` 在 self PID 数组残留非零值时，会把 `self_process_count` 置 0 但
  不清数组，驱动按 `STATUS_INVALID_PARAMETER(87)` 拒绝。临时 smoke 期间因此偶发配置失败；
  这是 wfprobe 的测试工具问题，不影响驱动。B3 若继续用 `--clear-self`，建议顺手清零数组。
- UDP block 的应用侧语义按 Q1 确认为静默丢包：`sendto` 成功、对端收不到、`recv` 超时；smoke 的
  断言按此编写，没有错误码断言。
- `protocol=any` 的 block blob 已可加载（B1 临时闸已删除）；TCP 仍为 `AccessDenied(10013)`。

### protocol=any / fallback block 的 ICMP 复核（two-filter 修正后）

`block_filters.cpp` 改成“any / fallback 都按 6 与 17 各一条 filter”后，额外验证：

| 场景 | TCP | UDP | ICMP（`ping 192.168.100.4`） |
|---|---|---|---|
| `protocol=any` block | AccessDenied(10013) | send 成功、对端收不到 | **2/2 reply，未被拦** |
| `fallback_action=block` | AccessDenied(10013) | send 成功、对端收不到 | **2/2 reply，未被拦** |
| 具体 UDP block | 同 blob 下 TCP 40053 正常 | 40051 丢、40052 回显 | 正常 |
| 具体 TCP block | TCP 40053 被拒 | 正常 | 正常 |

结论：`any` 不再误拦 ICMP；`max_block_filters` 已从 64 提到 128，`block-filter-protocol`
变异会展开成 6/17，B3 U7 应把 ICMP 断言固化进 UdpSuite。
## 8. 复现命令（临时 smoke 删除后不可直接复现）

```powershell
# 本机 Linux
python3 /tmp/b2b_peer.py

# VM
wfprobe control --enabled 0
wfprobe reset
wfprobe udp-smoke --peer-host 192.168.100.4 --timeout-ms 5000
wfprobe rules --iterations 3
wfprobe selftest --remote 192.168.100.4:40053 --iterations 3
wfprobe selftest --dual --remote 192.168.100.4:40053 --iterations 3
wfprobe gates
```
