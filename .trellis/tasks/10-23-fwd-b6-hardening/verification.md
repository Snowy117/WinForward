# B6a 验证记录：Forwarded 正确性收口（2026-10-23）

环境：WinLTSC `192.168.100.2`（Internal ifIndex=5），Linux `192.168.100.4`；驱动 ABI v6、
`forwarded_redirect_capacity=4096`。本轮最终源码包含：分片跟随表、TCP 回程部分注入一致性、
stop 排水（完成例程最后 decrement）、看门狗 generation 所有权、分片 permit-first 与
host 同 remote 不同端口测试、reject 产品脚本。原始日志在 `verification/`。`final-*` 是第二轮跟随表构建，`final2-*` 是第三轮前的重构构建（未单独记录
SHA）；第三轮构建（含测试后门）SHA-256 `5E7FB1A3…C104E` 的复跑是 `final3-*`；第四轮交付构建
SHA-256 `206CCC6F…BA6A`（`WFPROBE_FAULT_INJECTION` 未定义，无测试 hook）的复跑是 `final4-*`，
交付结论以 `final4-*` 为准；第五轮交付构建 SHA-256 `DACF0DD5…C606`（无测试 hook、无 stop 诊断注册表写入）
的复跑是 `final5-*`，结论以 `final5-*` 为准。

## 1. U7：decoy `available=1` / 原帧 permit

### 1.1 复现

旧构建下用**被外部终止的 suite 遗留的恢复看门狗**复现：suite A 被 kill 后它的
`--restore-only` 看门狗在 suite B 的等待窗口里触发，把 B 的 forwarded blob 清成
`rules=0 fallback=permit`；此时 B 的 decoy 仍绑定在 `.99:40250`，客户端的一条 datagram 按
fallback permit 进入本机协议栈，decoy 收到 64 字节：

```text
ASSERT FAIL: U7：原始目的地没有收到未改写帧（decoy 无数据）
  (available=0 from=192.168.100.4:40501 bytes=64 head=030A11181F262D343B424950575E656C)
```

证据：`u7-decoy-forced-watchdog-repro.out`（旧构建，强制提前看门狗）。

### 1.2 根因

U7 不是 redirect/注入失败分支的 permit：入口 DNAT、回程 SNAT、注入失败、非 IP、无表等
分支都 block；唯一会 permit 原帧的路径是 `match_forwarded` **未命中 + fallback_action=permit**。
旧 suite 的恢复看门狗只带 listener 端口，不带“它是哪一次安装”的身份；被 kill 的 suite 的
看门狗会清掉后来者的表，decoy 才会看到原帧。

### 1.3 修复

1. `ForwardedUdpSuite` / `ForwardedTcpSuite` 在 install 之后才启动看门狗，并把安装后的
   generation 传给 `--restore-only`；`Restore` generation 不匹配就打印
   `忽略过期的看门狗 expected_generation=… installed=1 generation=…` 并跳过。
2. 看门狗输出追加到 `%TEMP%\wfprobe-watchdog-<listener_port>.log`，便于事后判定。
3. `RunAsync` 的 `finally` 也传 `s_installedGeneration`：只恢复自己安装的那一代，不会清掉
   后来 suite 的表；`Restore` 返回 bool，`--restore-only` 只在真的恢复时打印
   `restore-only rules=0…`，跳过时不再打印误导行。
4. core 阶段在 decoy 断言前重新读 forwarded info/config，断言规则数=1、config enabled=1；
   窗口被外部改动单独 FAIL。
5. decoy 断言改为硬计数：排空 `ReceiveFrom`，要求 `datagrams == 0`，失败打印条数/字节/来源。

### 1.4 回归

- A(gen31) 被 kill、B(gen32) 活跃，A 的看门狗在 B 窗口内触发并跳过；B 客户端 1/1、decoy
  无数据：`b6-u7a5.out`、`b6-u7b5.out`、`u7-watchdog.log`。
- **single/no-verify 复跑**：`forwarded-udp --phase core --expect-datagrams 1 --no-verify` +
  客户端 `--no-verify`，U7 窗口/decoy 断言全 PASS（`u7-single-no-verify.log`）。

结论：redirect 决策下原 NBL 始终 block；剩余 permit 只属于“无规则命中时的 fallback permit”
这一显式语义。B6a 用 generation 所有权 + finally 守卫 + 窗口校验关闭测试侧假象。

## 2. 分片：显式 9 片、范围、permit-first

### 2.1 旧实现的问题

旧实现按“首个地址空间命中（忽略端口）”判断：permit 规则排在 redirect 前时，redirect 流的
分片会 fail-open；同一 remote IP、不同端口的 host 分片又会被误 block。

### 2.2 新实现：首片完整匹配 + 跟随表

- 首片（offset=0）有端口：按 `match_forwarded` **完整规则**（含端口）匹配；命中 redirect 就
  block 并记下 `(入口 adapter, 族, 协议, 源/目的地址, IPv4 ID 或 IPv6 fragment ID)` 到 256 项
  轮转表（2 秒有效），不建 redirect 表、不注入。
- 非首片没有端口：按同一张跟随表查；命中就 block（`udp_fragmented`/`udp_blocked`），否则按
  原路径（fallback permit 或普通匹配）。
- host 分片（同 remote IP、不同端口）不命中 redirect，不写表，非首片也查不到，按原路径重组。
- 边界：256 项写满覆盖最旧项（无 LRU）；2 秒后过期不匹配；键含入口，不同入口不互相跟随；
  IPv4 ID 只有 16 位，同入口/地址/协议的跨流碰撞会误跟随；非首片先于首片到达（或首片丢失）时
  没有表项，按普通流量放行；TCP 分片与出向分片不在闸内。这些边界由
  `tests/host/forwarded_fragment_table_test.cpp` 的 6 条 host 单测覆盖（容量轮转/过期/入口/键/碰撞/
  空表不匹配）。非首片先到的 E2E 反序发送受本机/网卡重排影响，本轮观测到 9 片仍全部 block，未作为
  通过证据；IPv6 atomic fragment（offset=0/M=0）按非分片处理，仅代码级，未 E2E。

### 2.3 证据

| 场景 | 结果 | 文件 |
|---|---|---|
| IPv4 raw 显式 9 片（redirect） | 9/9/0 | `final3-frag4.log` |
| IPv6 1280 MTU 9 片（redirect，跟随表版本） | 9/9/0 | `final3-ipv6-frag.log` |
| IPv4 permit-first（permit 9999 在 redirect 40250 前） | 9/9/0 | `final3-permit-first.log` |
| IPv6 permit-first | 9/9/0 | `final3-ipv6-permit-first.log` |
| IPv4 host 同 remote 不同端口（`.99:40600`，ID 0xb6a3） | 重组收到 544B，`fragmented=0/blocked=0` | `final3-host-same-remote.log` |
| IPv6 host 同 remote 不同端口（fe80…:40600） | 重组收到，计数 0 | `final3-ipv6-host-same-remote.log` |

交付构建 206C 在 final4 复跑了 core / IPv4 fragment / IPv6 fragment / capacity / U7 single：
`final4-core.log`、`final4-frag4.log`、`final4-ipv6-frag.log`、`final4-capacity.log`、`final4-u7-single.log`。
第五轮交付构建 DACF 又复跑了 core/IPv4 fragment/IPv6 fragment/permit-first/host/fail-udp/fail-tcp：
`final5-*.log`。

IPv4 用 `tools/linux/raw_fragment_sender.py` 逐片构造；IPv6 的 raw HDRINCL 在本机交替 EPERM
（已记录），改用 `tools/linux/ipv6_mtu_fragment.py` 临时把 eth0 MTU 设为 1280 + PMTUDISC_DONT，
内核切成 `8×1232+944=9` 片，片数由驱动计数精确断言。

## 3. reject：产品级 `ForwardedUdpRelay` + `Socks5UdpAssociation`

- 新增 `tools/windows/forwarded-udp-reject-e2e.ps1`：启动真实 `WinForward.Controller`，在
  forwarded 原始目的地址绑 decoy，等待外部客户端，写 stop 文件，解析 `udp-relay stop` 并断言
  `relayed=0 / back=0 / associations>0 / failures>0 / decoy=0`。
- 本轮（最终二进制）跑通：`REJECT-E2E-RESULT=PASS`，客户端回报 0/3、decoy 0、controller log
  `udp-relay stop in=3 relayed=0 back=0 associations=3 failures=3`，fake SOCKS5 3 次
  `associate-failed rep=7`。
- 证据：`reject-final.log`、`reject-final-client.out`、`reject-final-fake.log`、
  `reject-final-controller.log`、`reject-config.json`。
- `ForwardedUdpSuite` 的 `reject` phase 使用进程内 `Socks5UdpFakeServer`，是**夹具级**验证；
  产品级以 Controller + `ForwardedUdpRelay` 的脚本为准（suite 源码已注明）。

## 4. acked：真实 TCP ACK

客户端读 `TCP_INFO.tcpi_bytes_acked`（本机内核偏移 120，含 SYN 的 +1），客户端与 suite 统一
为 `[payload, payload+1]`；实测 2048 数据 → acked=2049、ok=1（`final3-tcp.log`）。

## 5. stop/UAF

### 5.1 修复

- `injection_complete` 的 `InterlockedDecrement64(outstanding_injections)` 移到
  `FwpsFreeNetBufferList0` / `NdisFreeMdl` / `ExFreePoolWithTag` **之后**：counter 归零即代表
  所有 free 完成。
- stop 先 `FwpsInjectionHandleDestroy0`，再 `probe_forwarded_drain_injections(1000, 10)`（10s）；
  仍非零则**不释放 NBL 池**并 DbgPrint，宁可泄漏池也不 UAF。
- 诊断：stop 把 `LastStopOutstandingAtEntry/AfterDrain` 写入服务键；故障注入注册值
  `InjectionCompleteDelayUs` 让完成例程延迟（默认 0，仅测试用），并记录
  `InjectionDelayEffectiveUs` / `LastStopCompletionDelayUs` / `LastStopDelayedCompletions`。
  该 hook 只在 `WfFaultInjection=true` 的测试构建里定义 `WFPROBE_FAULT_INJECTION`；交付构建不定义，
  二进制无 `KeStallExecutionProcessor` / `RtlQueryRegistryValues` / `RtlWriteRegistryValue` import
  （`findstr` 实测 ABSENT；`record_stop_counter` 与 LastStop*/Delayed 诊断写入也一并放在宏下）。
  注入 API 失败路径的 decrement 也移到所有 free 之后。

### 5.2 故障注入结果（诚实记录）

- 延迟 4.5s 时：`InjectionDelayEffectiveUs=0x44aa20`，
  `LastStopCompletionDelayUs=0x44aa20`，`LastStopDelayedCompletions=2`——完成例程确实按注入值
  延迟，outstanding 在延迟期间为非零。
- **未能可靠捕获 stop entry 非零**：控制通道（WinRM/`sc start`）在 DPC 延迟期间被阻塞，
  等 stop 真正执行时完成例程已结束（`LastStopOutstandingAtEntry=0`）；把延迟加长到 12s 只是
  让控制通道更晚到达。没有用崩溃/故意泄漏去换这个采样。
- 测试构建（`WfFaultInjection=true`）留档在 `C:\workspace\wfprobe-fault.sys`，SHA
  `7AA5548E…7F53`；构建命令：`$env:WfFaultInjection="true"; pwsh -File tools/windows/load-driver.ps1`。
  交付构建不含 hook，
  `stop-diag-final.log` 是测试构建的记录。
- 原始日志 `stop-diag-final.log` 同时包含：`InjectionDelayEffectiveUs=0x44aa20`、
  `LastStopCompletionDelayUs=0x44aa20`、`LastStopDelayedCompletions=2`、
  `LastStopOutstandingAtEntry=0`、`LastStopOutstandingAfterDrain=0`。
- 替代运行证据：5 次连续 `sc stop`/`sc start` 循环全部回到 RUNNING，`WfIj` 每次
  `outstanding=1`（当前池本身），alloc/free 成对、无累积泄漏、无 bugcheck
  （`stop-reload-loop.log`）。
- 结论：decrement 顺序修复是确定的代码修复；outstanding 非零的 stop 采样受控制通道延迟限制，
  记录为未完全验证（不伪装成已捕获）。

## 6. 混合 NBL / return_miss

- 入口 redirect：注入前逐 NB 校验 IP/协议/负载/与首 NB 同 5 元组，不同流整块 fail-closed；
  TCP core 的 `multi_nb=7` 正常同流通过。
- TCP 回程：**全成功才**计 `redirect_return/redirected`；`failed>0`（无论是否有部分 injected）
  计 `blocked + redirect_return_failed + redirect_inject_failed`，不再把部分成功当 redirected。
  命中+miss 且无失败时，miss 按 NB 累加 `redirect_return_miss`（原帧 block）。
- UDP 回程：全成功才计 return；命中+miss 且无失败时整体 block，miss 按 NBL 计（per-NB 会破坏
  `permitted == matched_permit + fallback_permit + udp_return_miss` 的帧去向不变式）。
- **未验证**：多 NB 的“部分注入失败”无法从网络构造；TCP 部分失败路径是代码审查级修复，
  没有 E2E 触发，记录为未验证。

## 7. capacity：生产 4096

4097 个不同源端口、1ms 间隔：`udp_created=4096 udp_blocked=1 capacity=1 complete=4096/0`，
listener 4096/4096，全部恒等式 PASS（`final-capacity.log`）。不再依赖 capacity=8 测试构建。

## 8. host UDP gate 单条 flake（U9/U11）

根因是全局计数器精确 diff 与后台 UDP 竞争；U9 改为 `AtLeast(1)` + 事件归属（pid/remote_port/
rule），U11 enabled=0 用计数器、enabled=1 用事件缺失判断，并补了“确实发生 redirected 事件”的
正向断言，避免事件缺失时 `Any=false` 静默通过。`udp-rules` 连续两次 `verdict=PASS failures=0`
（`udp-rules-final3-1.log`、`udp-rules-final3-2.log`）。

## 9. LWF 注入可见性

WinpkFilter `nt_ndisrd` 在 Internal 启用，全部 forwarded E2E 在 LWF 存在下通过；但 VM 无
WinpkFilter 用户态 API/工具，注入副本在 LWF 内的可见性**未直接测量**，记录为未验证。

## 10. 环境状态

- 第五轮交付驱动 SHA-256 `DACF0DD519EF8D436C4468873A0C0CBD9A0D7D1D619EF1D361088774A79DC606`
  （`final5-*` E2E 使用的同一文件，已备份到 `C:\workspace\wfprobe-final.sys`）。gate 使用同一源码
  重新构建/签名；gate 后安装的是这份备份，不再重建。回滚版本在 `C:\workspace\rollback\`。
- `.99/24`、`B5-ENV-*`、`B6-ENV-40600U`/`B6-ENV-40600U6`/`B6-ENV-40251U6` 保留用于复跑
  （40600U6 是本轮为 IPv6 host 分片新增的 UDP 40600 放行规则）。
- Internal 的 `ms_tcpip6` 保持启用（原 False）；恢复：
  `Disable-NetAdapterBinding -Name Internal -ComponentID ms_tcpip6`。
- Linux echo 40250 保留；reject fake 40500 已停止（`forwarded-udp-reject-e2e.ps1` 按需启动）。
- `C:\workspace` 下 b5/b6、b2/b3 旧日志/tar、sync 包、`b6-start-suite.ps1` 与 out/err 已清理；
  保留 `wf-b6-reject.json`（reject 脚本）、`wfprobe-final.sys`（SHA DACF…C606 的交付已签名驱动）与
  `wfprobe-fault.sys`（SHA 7AA5…7F53 的 fault-injection 测试构建，用于复现 stop-diag）。
- 未 commit / push；未 reset VM。

## 11. 门禁

- U13：删除全局 `UdpClassifyCalls==0` 断言（后台 permitted 流量会造成 calls>0），改为
  `udp_blocked==0`、`deferred_blocks==0` + `(pid, remote_port)` 事件归属；`udp-rules` 连续两次
  PASS（`udp-rules-final5-1.log`、`udp-rules-final5-2.log`），U13 旧关联断言均为 0。
- Linux `tools/check.sh --full`：8 PASS / 0 FAIL（含 `forwarded_fragment_table` 6 条 host 单测）。
- `tools/userland/verify-gates.sh`：23/23 PASS。
- Windows `gate.ps1 -Remote 192.168.100.4:40250` 连续两次：
  - `gate-b6a-r5-1.log`：**GATE-RESULT=PASS**，gate 构建 SHA `5067845F…4B37`，WfPr 149→149、
    WfIj 1→0、WfTr 5→0。
  - `gate-b6a-r5-2.log`：**GATE-RESULT=PASS**，gate 构建 SHA `B57257DD…4FAC`，WfPr 153→153、
    WfIj 1→0、WfTr 5→0。
  - 两次的 `udp-rules`、selftest、gates、userland-e2e 48、vm-check 78 全 PASS；gate 构建两次 SHA
    不同是签名时间戳差异，源码相同。
- 日志与 SHA 对照：`final3-*` → 5E7F（含测试 hook 旧构建）；`final4-*` → 206C；
  `final5-*` → **DACF**（第五轮交付，无 hook、无 RtlWrite 诊断）；`gate-b6a-round4b.log` → D3A1；
  `gate-b6a-r5-1.log` → 5067；`gate-b6a-r5-2.log` → B572；fault 测试构建 → 7AA5（`wfprobe-fault.sys`）。
- 交付安装/当前 RUNNING 的是 DACF 备份；三个 hook/诊断 import 均 ABSENT。
## 12. 剩余风险与未关闭项

| 项 | 状态 |
|---|---|
| stop outstanding 非零采样 | 故障注入证明延迟生效（2 次），但控制通道延迟使 stop entry 采样为 0；5 次 stop/start 无泄漏无崩溃。未完全验证。 |
| TCP 部分注入失败 | 代码已改为全成功才 redirected、失败即 blocked+return_failed；无 E2E 触发（无法构造多 NB 部分失败），未验证。 |
| 分片范围边界 | 非首片先于首片到达时放行；同一 remote 空间的 permit/redirect 按首片端口匹配；TCP 分片、出向分片不在闸内。 |
| UDP 混合 NBL miss 计数 | 按 NBL 计（`udp_multi_nb` 标注），per-NB 会破坏帧去向不变式。 |
| LWF 注入可见性 | 未验证。 |
| IPv6 分片构造 | 跟随表版本已 E2E（9 片 + permit-first + host 同 remote）；仍用 1280 MTU 内核分片，非逐片 raw；raw HDRINCL 交替 EPERM。 |
| 环境 | IPv6 保持启用、防火墙/别名保留；测试进程见 §10。 |
