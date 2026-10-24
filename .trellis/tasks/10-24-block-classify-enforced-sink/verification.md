# 验证记录：Host block → classify 首命中 + sink 端口

> 环境：WinLTSC Windows 11 IoT Enterprise LTSC（10.0.26100），测试签名；
> 控制面 ABI v8；驱动 Debug/x64。Linux 宿主为仓库 devShell。
> 记录日期：2026-10-24。

## 1. 结论

方案 A 在真实 WFP 上成立：

- block 参与 classify 的首个命中，命中后改道到 `block_sink_port`。
- `ALE_AUTH_CONNECT` 的固定 4 条 filter（V4/V6 × TCP/UDP；协议 + 远端端口 == sink +
  `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`）确实命中被改道的连接。
- TCP block 在连接阶段返回 `AccessDenied(10013)`；UDP block 静默丢包。
- 文件名、目录前缀、精确路径数组、`permit 内网 → block 其余`、`fallback=block` + permit
  全部生效；旧的顺序歧义拒绝不再存在。
- 完整 `gate.ps1` 与 Linux `check.sh --full` 均通过。

## 2. 关键实验

### 2.1 静态 filter 可见性与执行

`wfprobe udp-rules` / `rules` 在 VM 上：

- `U7: any block 覆盖 TCP（连接阶段失败） (AccessDenied (10013))`
- `U7: any block 覆盖 UDP`：客户端 `send` 成功、`recv-failed`，原目标 peer 收不到。
- `U13: 新关联的固定 auth filter 命中（sink 没有收到 datagram） (expected=0 actual=0)`
  —— 证明执行者是 AUTH_CONNECT filter，不是 sink socket 兜底。
- `U13: 新关联按新 blob 被 block`、`udp_blocked +1`、`deferred_blocks +1`、
  `decision=blocked_rule protocol=17`。
- `U13: 已存在的 UDP 关联保持旧 permit 决策`，换表后仍收到回包；新关联才按新表 block
  （R8 的 per-association 语义）。

### 2.2 顺序、选择器、fallback

- `RuleSuite`：`permit → block` 同端口首命中 permit，连接成功；`block → permit` 同端口
  首命中 block，连接失败。
- `HostRuleParitySuite`：
  - block 文件名、目录前缀、精确路径数组：父进程与副本都连接失败，服务器无 accept。
  - `permit 127.0.0.0/8 → block 其余`：环回回显成功；本机非环回地址连接 `10013`，
    `matched_block/blocked/deferred_blocks` 各 +1。
  - `fallback=block` + permit：permit 目标成功，其他目标失败。
- `vm-check` 96 条差分全过：产品编译成功、驱动接受、generation +1、`rejected_blobs` 不增；
  旧的顺序/选择器/预算拒绝矩阵全部转为接受。

### 2.3 产品控制器

完整 `gate.ps1` 的 `userland-e2e` 通过 46 条断言，控制器日志显示：

```text
controller-config enabled=1 listener_port=<P> listener_pid=<PID> self_count=1 block-sink-port=<S>
controller-block-sink-closed port=<S> received=0
```

`BlockSink` 在 v4/v6 × UDP/TCP 上保留同一端口；UDP 收到即丢、TCP 只 bind 不 listen。
`controller-config` 的 `block-sink-port` 非 0 且与 listener 端口不同。

### 2.4 防火墙

`firewall-check.ps1` PASS：block 规则不匹配的连接仍成功；Windows 防火墙的出站 Block
仍然生效，未被 sink 方案截胡。

## 3. 门禁结果

| 门禁 | 结果 |
|---|---|
| Linux `tools/check.sh --full` | 8 PASS / 0 FAIL / 0 SKIP；C# 254/254；ctest 84/84 |
| WinLTSC `tools/windows/gate.ps1`（首轮） | `GATE-RESULT=PASS`（failures=0；e2e 46 断言） |
| `wfprobe rules` / `udp-rules` / `selftest` / `gates` / `firewall-check` | PASS |
| `vm-check` | 96 checks / 0 failures |
| `userland-e2e` | 46 断言 / PASS |
| 卸载池检查 | `WfPr` 214→214，`WfIj` 1→0，`WfTr` 6→0，无新增 |
| 变异子集 `-OnlyIndex 0,3,11,19` | 全部 `MUTATION PASS`，恢复源码后基线全绿 |

清理 `max_block_filters`、对齐文档后重跑：

- Linux `check.sh --full`：8/8 PASS。
- WinLTSC `gate.ps1`：唯一失败是 `U7: 控制组 UDP permit +1 (expected=1 actual=2)`。
  窗口里多了一条背景 UDP 关联（`udp_classify_calls` 增量 3 = block 1 + 控制组 1 + 背景 1），
  `udp_blocked +1`、控制组 `permitted_fallback` 事件与端口都正确；block 本身、TCP `10013`、
  sink 收到 0 个 datagram、`userland-e2e` 47/47 均通过。这是测试对背景流量的精确计数过严，
  不是实现回归。已把 U6/U7 控制组的 `UdpPermitted` / `UdpClassifyCalls` 改为 `AtLeast`，保留
  控制组事件与端口的精确断言；重跑结果见 §5。

## 4. 已知边界

1. **产品 e2e 未覆盖 block 数据面**：`userland-e2e` 验证了 sink 端口下发/关闭与配置编译，
   但 TCP 失败 / UDP 静默由 `wfprobe rules` / `udp-rules` 覆盖；控制器级 block 数据面
   场景没有单独跑。
2. **TCP bind-not-listen 的 RST 未单独验证**：静态 filter 是主路径，实测 10013；即使
   TCP 兜底不产生 RST，也只退化为连接被拒。
3. **IPv6 是硬要求**：`BlockSink` 同时绑 v4/v6；IPv6 被禁用的主机上控制器会启动失败。
   与 `ListenerSet` 的 wildcard 行为一致，当前不降级。
4. **connected UDP refcount 泄漏**：block 不分配 `redirect_context`，不新增 36B `WfPr`；
   blocked connected UDP 是否仍触发 tcpip 的 per-datagram record 引用增长，未做长稳实验。
   现有 `WfPr` 卸载检查没有新增 outstanding。
5. **热替换**：已存在的 UDP 关联保持旧决策，直到关联结束；这是与旧 auth filter 即时生效
   不同的、已确认的语义（U13）。
6. **fail-open**：classify 内 sink 改道失败（acquire/apply 失败）无法在 redirect 层拦截，
   按 permit 放行并记 `redirect_failures` + `block_enforcement_failed` 事件；这是已知边界。
7. **`max_block_filters` 已删除**：C++ 常量、static_assert、C# / wfprobe 镜像与变异条目全部
   移除；代码 / 脚本中 0 引用，文档已改为 sink 方案。

## 5. 最终重跑补记

- Linux `check.sh --full`（最终工作区）：**8/8 PASS**（格式、有效行、cppcheck、禁止模式、
  配置、clang-tidy、wf_check）。
- WinLTSC `gate.ps1`（最终工作区，UdpSuite 计数修正后）：**`GATE-RESULT=PASS`**。
  `rules` / `udp-rules` / `selftest` / `gates` / `firewall-check` / 用户态单测 /
  `vm-check` / `userland-e2e`（47 断言）全过；卸载无新增 `WfPr` / `WfIj` / `WfTr`。
- 首轮唯一失败 `U7: 控制组 UDP permit +1 (expected=1 actual=2)` 是背景 UDP 关联导致的
  精确计数过严；已把 U6/U7 控制组的 `UdpPermitted` / `UdpClassifyCalls` 改为 `AtLeast`，
  保留控制组 `(pid, port)` 事件与 block 侧精确计数，重跑通过。
