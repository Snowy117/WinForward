# B5 验证记录（2026-10-23）

环境：WinLTSC 192.168.100.2（入口 ifIndex=5，Internal），Linux 192.168.100.4。控制面 ABI 升到 **6**，
`probe_forwarded_stats` 仍为 400 字节。驱动为最终签名版本；测试后 restored 为 `rules=0 fallback=permit
config=disabled`。

## 1. 独立复跑失败的根因：临时 on-link 地址被清理

上一轮清理删掉了 `192.168.100.99/24` 临时别名，外部客户端发往 `.99:40250` 的帧到不了 VM，表现为
suite listener 0/10、`redirected=0`、`udp_created=0`。这不是 B5 驱动回归。

修复：`wfprobe` 的 `ForwardedUdpSuite` / `ForwardedTcpSuite` 在开始前通过 `OnLinkAlias` 调 `netsh`
自动添加远端地址别名，等 DAD 到 Preferred 后打印 READY；结束时删除（地址已存在或就是本机地址时不重复
添加、也不删除）。因此独立复跑不再依赖手工 `New-NetIPAddress`。

复跑（不手工加别名）：

```text
wfprobe forwarded-udp --phase core --remote-address 192.168.100.99 --remote-port 40250
  --backend 192.168.100.4:40250 --listener-port 40400 --verify-port 41400 --expect-datagrams 3
client: received=3/3 source_ok=True last_source=192.168.100.99:40250
sha=366c3a7688b86b7393046a033db17cce5a2a791d38a8497c40e59b3ce9832223
udp_created=3 udp_ingress=3 udp_return=3 self_injected=6 complete=6/0
U7 decoy: available=0（原始目的地没有收到未改写帧）
```

B4b TCP core 复跑（同一自动别名逻辑）：

```text
wfprobe forwarded-tcp --phase core --local-ip 192.168.100.2 --remote-address 192.168.100.99
  --remote-port 40250 --backend 192.168.100.4:40250 --listener-port 40400 --verify-port 41400
client: port=48638 bytes=2048 acked=2048 sha=7a79d546... ok=1
SYN/入口 DNAT/回程 SNAT/verify 全部 PASS
```

## 2. 分片 fail-open 修复

代码改为：在规则匹配之前，只要 `facts.protocol == 17 && facts.fragmented` 且 forwarded config enabled，
统一 `block + udp_blocked + udp_fragmented`；不建表、不注入。`plan_forwarded_inbound` 删除了
`fragmented` 分支；注入层对分片再拒绝一次（防御性）。IPv4 首片不再“半处理”，非首片也不再 permit。

恒等式相应改为：

```text
matched_redirect + udp_fragmented ==
    redirect_ingress + redirect_blocked + udp_ingress + udp_blocked
```

9 个 IPv4 分片 E2E（Linux 发送 3 个 4000 字节 datagram，MTU 1500 分片）：

```text
udp_blocked=9 fragmented=9 udp_created=0 udp_ingress=0
ASSERT PASS: U6/U1：分片 datagram 不会到达 listener (received=False)
ASSERT PASS: 每个分片都计入 udp_fragmented (expected>=3 actual=9)
ASSERT PASS: 分片不建表 / 没有 DNAT 注入 / udp_blocked == udp_fragmented
```

（3 个 datagram × 3 片 = 9；最后一片不设 MF，仍按 `fragmented` 标记。）

## 3. 产品 E2E 的 back=0：旧日志有 stop 行，真实原因是回程没有收到/回显

旧产品日志确实有 `udp-relay stop` 行，但 `back=0`；不是 Stop-Process 导致没有 stop 行。重放确认是
fake SOCKS5 UDP 没有把 datagram 正确回显到 association data socket（或回程没有到达 relay），因此
客户端看不到回程。本轮修正 fake 上游回显逻辑，并继续用 stop-file 优雅退出以保留完整 stop 行。

```text
controller-forwarded-config enabled=1 listener_port=50584 listener=192.168.100.2 family=4
udp-relay start port=50584 listeners=2 upstreams=1
udp-relay association client=192.168.100.4:40561 server=192.168.100.99:40250 upstream_id=0 via=192.168.100.4:40500
udp-relay associated client=192.168.100.4:40561 relay=192.168.100.4:40600 upstream_id=0
udp-relay drop reason=no-redirect no-source          # 直接打到 listener 的 query-miss datagram
controller-forwarded-config-disabled enabled=0 port=50584
controller-forwarded-cleared rules=0 fallback=permit
udp-relay stop in=6 relayed=5 back=5 associations=1 failures=0 drops=1
controller-exit code=0
```

Linux 客户端实收 5/5，源地址恢复为 `192.168.100.99:40250`，SHA 一致：

```text
received=5/5 source_ok=True last_source=192.168.100.99:40250
client_port=40561 target_port=40250 bytes=320 count=5 sha=8948cf895f4e...
```

fake SOCKS5 UDP 日志（每条 datagram 回显；relay 端口 40600 在允许窗口内）：

```text
[socks5-udp] associate peer=('192.168.100.2', 50585) relay=0.0.0.0:40600
[socks5-udp] datagram #1..#5 len=74 peer=('192.168.100.2', 59497)
```

## 4. gate 第一次失败的根因：PREfast C6387（不是产品数据面 flake）

第一次 gate 的 `GATE-RESULT=FAIL failures=1` 来自 PREfast，而不是数据面：

```text
src\driver\probe_forwarded_frame.cpp(434): warning C6387: '_Param_(1)' could be '0':
  this does not adhere to the specification for the function 'NdisAllocateMdl'.
ASSERT FAIL: PREfast 0 告警 (... analysis-warnings=0 summary-warnings=1)
失败：PREfast 分析失败（/WX 不会提升 PREfast 告警，判据是输出）
```

修复：在 `NdisAllocateMdl` 前显式检查 `state.nbl_pool == nullptr`，释放临时池并返回
`STATUS_INVALID_DEVICE_STATE`。随后单独复跑 `Invoke-Prefast -Root .`：

```text
success=True exit=0 ran=True c28=0 summary=0
Build succeeded. 0 Warning(s) 0 Error(s)
```

第一次失败的原文保存在 VM tmux 会话 `windows-vm-3655ae` 的 scrollback；复现命令：
`pwsh -File tools/windows/gate.ps1 -Remote 192.168.100.4:40250`。第二次 gate 是在修复前跑的，不能
单独作为结论；C6387 修复后的中间版本 gate `GATE-RESULT=PASS`，PREfast `analysis-warnings=0 summary-warnings=0`
（日志 `/tmp/gate-b5-final.log`，VM 侧 `C:\workspace\gate-b5-final.log`）；第一次失败是真实 C6387，
不是产品数据面 flake。

## 5. 表满、上游失败、查表失败

- 表满：生产容量 4096，但测试客户端只有一个 Linux IP，可用源端口被环境限制在 40000–41000（1001 个）；
  回程 key 是 `(listener_port, client_ip, client_port)`，同一客户端端口覆盖多个 server 会替换旧表项，
  无法用 4096 个真实表项压满。使用 **capacity=8 的测试构建**（临时把
  `forwarded_redirect_capacity` 改为 8，测完已恢复 4096 并重新构建/签名/安装）做故障注入：

  ```text
  udp_created=8 udp_ingress=8 udp_blocked=1 capacity=1
  ASSERT PASS: 表项数等于容量 (8/8)
  ASSERT PASS: 第 N+1 条触发 udp_capacity (1)
  ASSERT PASS: 第 N+1 条被 block (1)
  ASSERT PASS: 每条 datagram 都成功注入或被 block (9/9)
  ```

  测试使用 9 个不同源端口、对角目标端口（`--sweep-diagonal`），避免回程 key 冲突。
- 上游 ASSOCIATE 失败：`--phase reject`，fake server 返回 `rep=7`：

  ```text
  udp_created=1 udp_ingress=3 udp_return=0
  ASSERT PASS: 3 条都尝试上游且失败；associations=0；上游记录 3 次失败
  ASSERT PASS: 没有回程注入（不直连）
  ```

- 查表失败：产品 E2E 中从 Linux 直接发一条 UDP 到 listener（不经过 `.99` redirect），产品日志
  `udp-relay drop reason=no-redirect no-source`，客户端无响应；证明缺表项时不回退直连。
- 60s 超时：`udp_created=2 udp_expired=1`（65s 间隔）；默认 `expectCreated=1` 时断言
  `udp_expired == 0`，不再出现 `AtLeast(0)` 空断言。
### 5.1 部分 NET_BUFFER 注入失败的计数

入口 DNAT 只有在 **全部 NET_BUFFER 注入成功**（`injected > 0 && failed == 0`）时才计
`redirected/ingress`；只要有一段失败就计 `blocked + udp_blocked/redirect_blocked + inject_failed`，
不再同时计 `redirected`。这样 `udp_blocked == ... + udp_inject_failed` 与帧去向守恒在部分失败时仍成立。
修复后 core 1/1、PREfast 0；完整 gate 是在此小修正之前跑的（该分支不被 gate 触发），修正本身已由
core 最小用例与 PREfast 覆盖。

## 6. 回归与门禁

- Linux：`tools/check.sh --full` → 8 PASS（format/effective lines/cppcheck/forbidden/config/clang-tidy/
  wf_check；wf_check 内 C# 226/226、ctest 全绿）。
- 驱动：最终 ABI v6、capacity 4096、PREfast 0；`forwarded-udp` core/fragment/capacity(8)/reject、
  `forwarded-tcp` core、host `udp-rules` 均 PASS。
- 产品 E2E：Controller + Linux fake SOCKS5 UDP 40500，5/5、`back=5`、`associations=1`、`drops=1`。
- 最终完整 Windows gate（`-Remote 192.168.100.4:40250`）：`GATE-RESULT=PASS`，PREfast 0，
  userland-e2e 48，vm-check 78；最终日志 `/tmp/gate-b5-final4.log`。此前两次 gate 的 host UDP
  U9/U11 各 1 条 flake 记录见 §8。
- 清理：`route-before-b5.txt` 与 `route-after-cleanup-b5.txt` 的 `Compare-Object` 为 0 行；临时别名
  `192.168.100.99/24`、防火墙规则 `WF-B5-DIRECT`、VM 测试日志/仓库副本已删除；驱动仍 RUNNING，
  forwarded config 恢复为 `rules=0 fallback=permit config=disabled`。
- `forwarded-udp` 尚未加入 `gate.ps1`（需要 Linux 外部客户端）；按任务决策留给 B6 的独立 Linux 门禁。

## 7. 未覆盖 / 风险

- native IPv6 UDP 未在入口网卡做 E2E（Linux 的 fd00 地址在另一张网卡）；代码路径已实现但不宣称已支持。
- 生产 4096 容量没有用真实 4096 表项压测；capacity=8 测试构建覆盖了同一失败分支。
- host UDP 未命中 forwarded 表时直接丢包；host listener/中继留 B6。
- 回程 key 不含 server：同一客户端端口并发访问多个目的时，建表会按回程 key 删除旧表项，回程只能
  还原最后一个目的。B5 假定“一个客户端端口一个目的”，多目的支持留 B6（独立 listener 端口或映射）。
- 用户态 UDP 自包含实现（`Relay/Socks5UdpAssociation`、`Socks5UdpCodec`、`ForwardedUdpRelay`）是
  临时偏差，B6 评估薄适配/接回 WinForward.Runtime。
- LWF（WinpkFilter）对注入帧的可见性未测。

## 7.1 B6 记录（不阻塞 B5 提交）

- U7 decoy `available=1` 原帧 permit 异常（有序复跑为 0，未定位；suite 遇到即 FAIL）。
- 分片 raw/显式构造 9 片测试（当前用 4000B + MTU 触发 9 片）。
- `reject` 产品级 `ForwardedUdpRelay` ASSOCIATE 拒绝 E2E（当前是 wfprobe 替身）。
- B4b `acked` 真实 TCP ACK 语义（当前用实收字节）。
- capacity=8 测试构建 / 生产 4096 真满。
- host UDP gate 单条断言偶发 flake。

## 8. 第三轮复核补充（2026-10-23）

- **0/10 根因**：检查者客户端在 suite 30s 等待窗口之后才发送。本轮 18:52:00 的 pktmon 显示 10 条
  `.4:55119 > .99:40250` 到达 Windows Ethernet Rx，随后在 tcpip `Component 24` 因
  `transport endpoint was not found` / `Port unreachable` 丢弃（listener/config 已结束）；
  suite 的 `in=24` 只有背景帧。10/10 在 READY 后发送时 PASS（18:56:29 READY；客户端 10/10、
  source/SHA 正确）；18:54:13 pktmon 同时看到 `.99:40250` 与 DNAT 后 `.2:40400`。详见
  `repro-round3.md` 与 `verification/repro-aligned/`。不是驱动规则/注入回归。

- **OnLinkAlias**：改为添加后轮询 **可 bind**（等价于 Preferred/Tentative 就绪），15s 超时；
  `--no-alias` 也检查远端地址可 bind；添加失败/不可 bind 直接返回 7，不再 READY。添加成功时写
  lease 并启动 10 分钟清理 watchdog；正常退出先删 lease 再杀 watchdog 再删地址；手动已存在地址
  不删除。
- **verify-gates**：ABI 变异从过期的 5→6 改为 6→7；`tools/userland/verify-gates.sh` 23/23 全 PASS
  （19 条变异红、4 条基线绿）。
- **分片**：3×4000B（MTU 1500）实测 `fragmented=9`、`udp_blocked=9`、`created=0`；检查者的 0
  同样源于没有客户端帧。分片早退在规则匹配前，启用 forwarded 时会覆盖该 callout 可见的所有 UDP
  分片（含 host/其他入口）；这是 B5 的全局 fail-closed 边界，B6 再按入口/规则收窄。
- **回程部分注入**：只有全部 NET_BUFFER 成功才 `udp_return/redirected`；失败/部分失败改为
  `blocked + udp_return_failed + udp_inject_failed`，不再把部分失败算成成功回程。
- **退出顺序**：`WfpController.DisposeAsync` 改为 disabled config → 停 UDP/TCP relay → 空 blob，
  与 spec 一致。
- **U7**：decoy 绑定失败现在直接 FAIL（不再“跳过”）；绑定成功时断言 `available=0`。
- **产品 E2E 最终复跑**：Controller + fake SOCKS5 UDP，10/10、`back=10`、`associations=1`、`drops=0`；
  客户端 verify `count=10 bytes=640 sha=deac... source=192.168.100.99:40250 source_ok=1 ok=1`；
  fake 上游 1 次 ASSOCIATE + 10 条回显。退出顺序 disabled → `udp-relay stop` → 清 blob。证据
  `verification/b5-controller-udp-final.log`、`product-client.out`、`fake-socks5-product.log`。
- **TCP client payload 已统一**为 `index*31+7`；`forwarded-tcp --phase core --bytes 2048` 复跑 PASS，
  客户端 `sha=7c7272c9... ok=1`，suite verify/stats 全部 PASS。证据 `verification/tcp-core-*`。

- **capacity**：生产 4096；capacity=8 测试构建覆盖表满，4096 真满留 B6。
- **第三轮最终 gate：`GATE-RESULT=PASS`**，PREfast 0、userland-e2e 48、vm-check 78（日志
  `/tmp/gate-b5-final4.log`，VM 侧 `C:\workspace\gate-b5-final4.log`）。
- 在最终 PASS 之前有两次 gate 各 1 条 **host UDP** 断言 flake（U9 `fallback_redirect` actual=2；U11
  首包 permit actual=2），不是 forwarded UDP/TCP、不是 PREfast；单独连续 3 次 `udp-rules` 均 PASS。
  最终重跑全绿；host UDP 套件的偶发问题仍建议单独跟进。
