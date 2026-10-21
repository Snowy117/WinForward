# 技术设计：Host UDP B3

## 1. UdpSuite 结构

`tools/wfprobe/UdpSuite.cs`，命令 `udp-rules`：

- 套件进程持有 UDP listener（v4 `0.0.0.0` / v6 `[::]` + DualMode）；`listener_pid = 当前 PID`，
  `self_process_ids = [当前 PID]`，客户端子进程不在 self 集合里。
- `WfpDevice`：ResetStats → SetConfig（enabled=0 先）→ SetRuleBlob → SetConfig（listener/self）。
- 每个用例开始 reset stats，读取增量；permit/block 等按场景断言。
- 客户端子进程用 `wfprobe udp-client`（带 `pid=` 输出）；listener 用同一个 socket 回包，复用入站
  重定向关联、不重新 classify；self-PID 用于中继新开 socket 的出站。
- U1/U2 redirect：context magic/version/protocol=17/family/地址/端口/PID/rule。
- U3 多 datagram：同一 socket 同目的地 N 条 → 1 次 classify/redirect/context；发请求前 reset，
  收包后先读计数再回包，窗口里不混入其他出站。
- U4 多目的地：两条 context 不串。
- U5 permit；U6 UDP block；U7 any/fallback block + TCP block 对照 + **ICMP ping 仍通**。
- U8 进程选择器：匹配当前 exe / `OtherAppId` 不匹配。
- U9 fallback redirect/permit。
- U10 IPv6/dual-mode：v4-mapped unconnected → family=4；v4-mapped connected / native v6 → family=6。
- U11 enabled 0↔1。
- U12 生命周期：unconnected 严格；connected 先测释放窗口（180s/更长时间），按实测决定。
- U13 热替换：permit→block，旧关联 auth 层丢弃但不重新 classify，新关联 block 计数 +1。

## 2. UdpSockets IPv6

- `CreateListener(IPEndPoint, bool dualMode)`：v6 设 `DualMode`；同时尝试
  `IP_WFP_REDIRECT_CONTEXT`（level 0）与 `IPV6_WFP_REDIRECT_CONTEXT`（level 41），忽略不适用的一侧。
- `FindControl` 接受 level 0/41、type 70。
- `ParseEndpoint` 支持 `sockaddr_in6`；`ParseEndpoint` 文本支持 `[addr]:port`、v4-mapped。
- 客户端 socket：v6 remote 时 `DualMode=true`，兼容 v4-mapped。

## 3. 池与 connected context

B0/B2b 观察：unconnected 关闭 socket 后 1s 内释放；connected 180s 后仍 outstanding，
`sc stop` 后仍存在。B3 先做一次独立测量（干净快照/checkpoint）：

- 记录 allocs/frees/outstanding 在 1s/10s/60s/180s/300s 的变化；
- 若仍不释放，记录为“connected UDP context 由 WFP 延迟/不释放”的限制，U12 只对
  unconnected 做严格断言；gate 的卸载后池断言不要求 connected 归零，或按基线扣减；
- 不把“未释放”伪造成“释放”。

## 4. gate 与 mutation

- `gate.ps1`：在 `rules` 后加 `udp-rules --timeout-ms ...`；失败即 gate 失败。
- 池标签：unconnected 用例前后差值；gate 卸载后按 B2b 结论决定是否断言 WfPr=0。
- `mutation.ps1` 增加 6 个 id：
  `classify-udp-branch`、`udp-classify-counter`、`udp-permit-counter`、`udp-block-counter`、
  `udp-redirect-counter`、`block-filter-protocol`；每条替换后跑 `udp-rules`，期望非 0。
- 脚本按 id 定位，命中数必须 1；运行前后源码字节一致。

## 5. 可观测性

- `Program.PrintEvent`：输出 `protocol=`。
- `Program.PrintStats`：输出 `udp_calls/permitted/blocked/redirected`。
- `WinForward.Controller/StateCommand`：counters 行输出 4 个 UDP 计数器。
- U1/U6 的断言依赖这些输出；不新增 ABI。

## 6. 文档

- `src/driver/README.md`：ABI v3、UDP 规则/context/事件/计数、block 静默丢包、
  `udp_blocked` 定义、any/fallback 6/17 两条 filter 与 ICMP 不被拦、同 socket 回包不重分类
  与 self-PID 的用途。
- `.trellis/spec/backend/wfp-architecture.md`：D7 改为 Host TCP + UDP；补 UDP 事实；
  未决清单更新。
- `AGENTS.md`：结构性缺口列表移除/标注 Host UDP 已完成，指向本任务。
- `src/userland/README.md`、`RuleOrderValidator.cs` 注释：删除“fallback 无条件 filter”旧措辞。
- `src/driver/README.md` 的 ABI v2 文案改 v3。

## 7. 风险

| 风险 | 处理 |
|---|---|
| connected context 不释放 | 先测；只对 unconnected 严格断言，记录限制，不伪绿 |
| VM 再次 0x11D/0xc000021a | 用 checkpoint/稳定快照；崩溃先隔离环境，不直接归因驱动 |
| U3 回包被误认为重新 classify | 实测同 socket 回包复用入站关联、不重新 classify；仍控制窗口避免其他出站混入 |
| ICMP 断言依赖 ping | 用 `ping -n 2` 退出码/输出；无 ping 时记录环境缺口 |
| mutation 惰性 | 每条 mutation 必须有对应 UdpSuite 用例变红 |
| 并发文件冲突 | A 工具/门禁/可观测性，B 文档；合并后结构审查 |
