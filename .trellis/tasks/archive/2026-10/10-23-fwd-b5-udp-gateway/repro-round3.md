# B5 第三轮复现记录（2026-10-23）

## 结论

**已定位：检查者的 suite 收到 0 条客户端帧。** 失败签名 `datagrams=3`、`received=0/3`、`in=24`
（30s）与“不启动外部客户端”的 suite 完全一致；`in=24` 是背景帧，不是 3 条客户端 datagram。用检查者
自己的 `/tmp/b5-udp-client.py` 在手工 `.99` Preferred + `--no-alias` 下复跑，suite PASS、`in=58`、
`udp_ingress=10`、`udp_created=1`。所以 0/10 是客户端未发送/未在窗口内发送，不是驱动规则或注入回归。
## 受控复现（本轮，带时间戳）

### 成功：suite 先启动，客户端自动在 8s 后发送

- 环境：`.99/24` ifIndex 5，`AddressState=Preferred`，`SkipAsSource=False`；B5-ALIGN-40250/40400/41400 防火墙放行。
- suite start：**2026-10-22 18:54:05.933 -07:00**（Linux 侧 2026-10-23 09:54:05 +08:00）。
- client start/end：**18:54:09 / 18:54:15 -07:00**；客户端 count=3、bytes=192、source=`192.168.100.99:40250`，stderr 空。
- pktmon 本轮时间：**18:54:13.042 -07:00**，同一时刻先看到
  `192.168.100.4:54162 > 192.168.100.99:40250`，随后看到
  `192.168.100.4:54162 > 192.168.100.2:40400`（DNAT 后），并在 Rx 组件 2/10/8/19 重复出现。
- suite stats：`in=59 redirected=13 udp_created=1 udp_ingress=10 udp_return=3`；listener 收到 3、verify 成功；U7 decoy `无数据`。
- 证据：`verification/repro-aligned/11-*`、`12-suite.out`、`12-pktmon-utf8.txt`、`09/10-*`。
### 10/10 成功复跑（同一客户端，suite 期待 10）

- READY 精确时间：**2026-10-22 18:56:29.835 -07:00**；客户端在延迟 8s 后发送。
- 客户端：`count=10 bytes=640 source=192.168.100.99:40250 source_ok=1 ok=1`。
- suite：`in=59 redirected=20 udp_created=1 udp_ingress=10 udp_return=10`，verify 10/10、SHA 一致、U7 decoy `无数据`。
- 证据：`verification/repro-aligned/14-*`。

之前 `--expect-datagrams 3` + 10 条客户端得到 `count=3 ok=0`，是因为客户端脚本按 10 条判 `ok`；
suite 只回 3 条，不是数据面失败。用 10/10 后 `ok=1`。

### 失败：客户端在 suite 30s 窗口之后才启动

- suite start：**18:49:41.447 -07:00**；我先轮询 READY，但工具调用延迟导致客户端直到
  **18:51:56 +08:00（18:51:56 -07:00）** 才启动，已超过 30s 窗口。
- suite 输出：`received=0/3`，`in=24 redirected=0 udp_created=0`；decoy 无数据；客户端 count=0、
  verify 连接超时。这与检查者证据同签名。
- 同样，`18:52:29` 启动的 suite 在 `18:53:01` 才收到客户端，仍是 0；只有把客户端延迟 8s、与 suite
  在同一个脚本中启动，才在窗口内。
### 晚到客户端的帧具体死在哪一跳

`06-pktmon-late-client.txt`（本轮 **18:52:00.262 -07:00**）显示客户端 10 条
`192.168.100.4.55119 > 192.168.100.99.40250` 全部到达 Windows Ethernet Rx，然后在
`Component 24` 以 `DropReason INET: transport endpoint was not found` / `Port unreachable` 丢弃。
这说明：

1. 帧没有在 Linux 或 vSwitch 丢失；
2. 它们到了 tcpip，但 suite 的 listener/config 已经结束（30s 窗口过完并恢复 disabled），
   没有 listener 可送达；
3. 因此 suite 的 `in` 不包含这些帧，`redirected/udp_created` 为 0，客户端 verify 超时。

这不是 WFP MAC 子层/ifIndex/blob 不匹配；真正发生的是**发送晚于 suite 等待窗口**。成功轮
（18:54:13）同一端口先出现 `.99:40250`，再出现 `.2:40400`，说明 WFP DNAT 与 listener 都正常。

**结论：** 本轮 pktmon 时间戳证明客户端 3 条 UDP 确实到达 Windows NIC，并且在 WFP 处理后变成
`.2:40400`；成功失败只取决于客户端是否在 suite 的 30s 等待窗口内发送。检查者旧 pktmon 的
2026-10-22 18:43:57 不能证明本轮；本轮新证据是 18:54:13。

## 环境对齐矩阵

| 项 | 检查者失败环境（可从输出推断） | 本机复现 |
|---|---|---|
| Linux `.99` | 未在 Linux 本机；应由 eth0 到 Windows | `ip addr` 无 .99；`route get .99` dev eth0 src .4；neigh `00:15:5d:03:72:8d`；ping 通 |
| 客户端源端口 | 38257（<40000） | 38257 单条直发 Windows listener 40250 收到 |
| Windows `.99` | ifIndex 5，Preferred（检查者截图） | ifIndex 5，AddressState Preferred，/24，SkipAsSource=False |
| Windows 防火墙 | 失败时 40250/40400/41400 规则不完整；verify 超时 | 40250 直发 listener 可收；40400/41400 放行后 suite 可回 verify |
| 路径 | 未抓包 | pktmon：Linux .4:40577 → .99:40250 在 Windows Rx 出现；无 listener 时 Drop "transport endpoint not found" |
| suite | client 发送后 suite `in=26`、0 条客户端帧 | 同一 suite 在 **client 先于 suite READY** 时得到 count=0、suite 0；在 **READY 后**发送得到 count=3/10、`in=58` |
| 客户端 verify | `TimeoutError` | verify 端口在 suite READY 前不可连接；READY 后可连接 |

**帧在哪一跳消失：** 在“client 先于 suite/别名就绪”的失败 run 中，帧没有进入 WFP 的 forwarded 处理窗口
（suite `in` 只有背景帧，listener/decoy 0）；而同一 Linux→Windows 路径在 pktmon 中可见。因此不是
“WFP 规则不命中”或“注入失败”，而是**发送发生在 suite 监听/config 就绪之前**（检查者 stderr 的
verify 超时也指向 suite 未就绪/verify 端口不可达）。client 输出只证明 send 循环执行，不证明发送时
suite 已 READY。

**可重复准备脚本：** `tools/windows/prepare-b5-udp-e2e.ps1`（别名 + Preferred 轮询 + 40250/40400/
41400 防火墙）；Linux 侧先启动/等待 suite 的 `READY`，再运行客户端（新客户端默认等待 verify 端口
最多 10s）。不要同时启动 suite 和 client。

## 0/10 的直接证据：客户端 datagram 从未到达 WFP

检查者的 suite 记录是 `datagrams=3`、`received=0/3`、`in=24`，30s 窗口。把同一 suite 在
**不启动外部客户端**的情况下跑 30s，得到的就是同一种签名：`in` 只累积背景帧，`udp_created=0`、
`redirected=0`、listener 0。即 `in=24` 不包含任何客户端 datagram。

用检查者自己的 `/tmp/b5-udp-client.py`（10 条、ephemeral 源端口、自带 40250 echo）在手工
`.99` Preferred + `--no-alias` 的同一 suite 上复跑：

```text
{"count": 3, "bytes": 192, "source": "192.168.100.99:40250", "source_ok": 1, "ok": 0, "local_port": 54290}
[forwarded-udp] core stats: ... in=58 redirected=13 udp_created=1 udp_ingress=10 udp_return=3 ...
```

suite 的 3 条断言全部 PASS（客户端只收到 3 条是 suite `--expect-datagrams 3` 只回 3 条，客户端
脚本按 10 条判 `ok=0`，不影响 suite）。这证明 0/10 是**客户端没有发出/没有在 suite 窗口内发出**，
不是驱动规则或注入失败。

另一个 test harness 问题：检查者客户端脚本自己 bind `0.0.0.0:40250` 做 echo；如果 Linux 已有
UDP echo 占着 40250，它的 echo 线程会 `Address already in use`（实测），但主线程仍会 send，所以
这只能解释 `ok=0`，不能解释 `in=24`。测试时应保证 40250 只由一个 echo 提供者占用。

**结论：当前 0/10 的根因是外部客户端未发送，而不是 B5 数据面回归。** 需要检查者提供客户端
stdout（JSON）和启动顺序；否则无法从驱动侧修复一个没有输入帧的测试。


## 本机复现命令与结果

### UDP core（自动别名）

```text
wfprobe forwarded-udp --phase core --remote-address 192.168.100.99 --remote-port 40250
  --backend 192.168.100.4:40250 --listener-port 40400 --verify-port 41400
  --expect-datagrams 5 --wait-ms 20000

[alias] 已添加 192.168.100.99/24 到 Internal（测试后删除）
[forwarded-udp] READY phase=core listener=40400 ... decoy=bound
client: received=5/5 source_ok=True last_source=192.168.100.99:40250
sha=8948cf895f4e50480510eb2830b0933546dc245ac56f1a4b9c811b2deb87fe23
udp_created=1（同客户端端口关联复用） udp_ingress=5 udp_return=5
matched_redirect=5 blocked=0 no_config=0
ASSERT PASS: 入口 DNAT 注入全部 datagram (5/5)
ASSERT PASS: 回程 SNAT 注入全部 datagram (5/5)
ASSERT PASS: 客户端源地址恢复 (192.168.100.99:40250)
```

### UDP 单条、不同源端口

为排除源端口 ACL：

- 40501：PASS，`udp_created=1 udp_ingress=1 udp_return=1`。
- 54321：PASS，`udp_created=1 udp_ingress=1 udp_return=1`。
- 未 bind 的 ephemeral 源端口：PASS，`udp_created=1`。
- 4000B 分片：之前测得 `fragmented=9 udp_blocked=9`；检查者测得 `fragmented=0`，说明双方客户端/路径不同，需要用 raw frame 或明确 MTU 的构造。

### TCP core（自动别名）

```text
wfprobe forwarded-tcp --phase core --local-ip 192.168.100.2 --remote-address 192.168.100.99
  --remote-port 40250 --backend 192.168.100.4:40250 --listener-port 40400
  --verify-port 41400 --expect-accepts 1 --bytes 2048

client: port=58800 bytes=2048 acked=2048 sha=7a79d546... ok=1
SYN 建表 1、入口 DNAT 7、回程 SNAT 5、verify PASS
```

## 本轮发现的未关闭问题

1. **U7 decoy 在单条/no-verify 场景收到 1 条 datagram**（`available=1`），而 5/5+verify 场景 decoy=0。
   这是原始帧消费的异常，尚不能解释 0/10；必须先定位，不能把 U7 当作已通过。
2. `OnLinkAlias` 仍固定 `Thread.Sleep(2500)`，没有轮询 `AddressState=Preferred`；`netsh add` 失败也只打印不 fail-fast。检查者已指为阻断。
3. `verify-gates` ABI mutation 仍写 5→6，夹具已经是 6；必须改 6→7 或动态取值，最终 23/23。
4. 4000B 客户端在检查者环境没有真正分片（`fragmented=0`），需要 raw socket/显式构造 IPv4 分片，不能再用普通 UDP 大包代替。
5. 旧 `back=0` 根因说明错误：旧日志有 stop 行，真实原因是 fake 上游未回显/回程收包失败；本文件修正，verification.md 也需同步。
6. 回程 UDP 部分注入失败、exit 顺序、capacity/4096、文档口径等检查者 should-fix 尚未全部处理。

## 驱动 / 环境

- 服务 `wfprobe` RUNNING；驱动为当前源码（ABI v6、capacity 4096）签名版本。
- 最后一次 suite 已恢复 `rules=0 fallback=permit config=disabled`；`.99` 别名已删除。
- 保留 `C:\workspace\WinForward.Kernel` 源码和 `C:\workspace\rollback\wfprobe-abi5-b5.sys`；没有 reset VM。
- 未 commit / push。

## 请求检查者提供

1. 外部客户端完整命令（目标 IP/端口、源端口、payload 大小、是否 bind、是否等响应）。
2. 失败 suite 的完整 stdout/stderr（尤其 `in`、`classify_calls`、`matched_redirect`、`blocked`、
   `udp_no_config`、`udp_fragmented`、`ifindex`、`decoy`、`READY` 行）。
3. 失败时的 `Get-NetIPAddress .99` 状态、`Get-NetForwardedConfig`/`probe_forwarded_config` 回读、
   blob 规则（ifIndex/CIDR/端口）。
