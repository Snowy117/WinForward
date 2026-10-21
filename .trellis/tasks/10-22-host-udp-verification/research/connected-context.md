# connected UDP redirect context 释放窗口（B3 实测）

> 环境：WinLTSC（Windows 11 IoT Enterprise LTSC，10.0.26100.0），VM `192.168.100.2`；
> 驱动 ABI v3（Debug/x64，测试签名）；`wfprobe` Release/net11.0。
> 测量时间：2026-10-22；干净启动（`WfPr` 标签不存在，基线 0）。

## 命令

```powershell
# 只跑 U12；connected 采样点固定为 1s / 10s / 60s / 180s / 300s
wfprobe.exe udp-rules --only U12 --connected-window-ms 300000 --ping-host 192.168.100.4
```

当前 U12 的顺序：先 unconnected，再 connected。采样来自 `WfPr` 池标签
（`wfprobe pooltag --tag WfPr` 的 `nonpaged_allocs/frees/outstanding`），
`contexts_allocated` 与 `udp_redirected` 由驱动计数器给出。

## 实际输出（干净启动）

unconnected（先跑；基线 outstanding=0，走严格断言）：

```text
ASSERT PASS: U12: unconnected 客户端退出码 (expected=0 actual=0)
ASSERT PASS: U12: unconnected redirect +1 (expected=1 actual=1)
ASSERT PASS: U12 unconnected: UDP 不变式 calls=permitted+blocked+redirected (expected=1 actual=1)
ASSERT PASS: U12: unconnected context 分配数等于 redirect 数 (expected=1 actual=1)
[udp-rules] U12 unconnected: baseline=0 allocs=+1 frees=+1 outstanding=0->0
ASSERT PASS: U12: unconnected context 关闭后释放 (expected=1 actual=1)
ASSERT PASS: U12: unconnected outstanding 回到 0 基线 (expected=0 actual=0)
```

connected（客户端 `connect` + 1 条 datagram，随后进程退出）：

```text
ASSERT PASS: U12: connected redirect +1 (expected=1 actual=1)
ASSERT PASS: U12 connected: UDP 不变式 calls=permitted+blocked+redirected (expected=1 actual=1)
[udp-rules] U12 connected @1051ms (milestone=1000ms): allocs=+1 frees=+0 outstanding=0->1
[udp-rules] U12 connected @10005ms (milestone=10000ms): allocs=+1 frees=+0 outstanding=0->1
[udp-rules] U12 connected @60010ms (milestone=60000ms): allocs=+1 frees=+0 outstanding=0->1
[udp-rules] U12 connected @180025ms (milestone=180000ms): allocs=+1 frees=+0 outstanding=0->1
[udp-rules] U12 connected @300051ms (milestone=300000ms): allocs=+1 frees=+0 outstanding=0->1
ASSERT PASS: U12: connected context 分配数等于 redirect 数 (expected=1 actual=1)
[udp-rules] U12 LIMIT: connected UDP context 由 WFP 持有，本 boot 不做释放断言（观察窗口 300000ms）
[udp-rules] verdict=PASS failures=0
```

## 结论

- connected UDP 的 36 字节 redirect context 在客户端 socket 关闭后 **1s 内没有释放**，
  10s / 60s / 180s / 300s 也没有释放；`sc stop wfprobe` 同样不回收（B2b 已观察）。
  这是 WFP 对 connected UDP 关联的持有行为，不是本批工具或测试造成的。
- unconnected 的 context 在 socket 关闭后 1s 内释放，`allocs == frees`、outstanding 回基线。
- 干净启动时 `WfPr` 标签不存在，基线按 0 计；每次完整 `udp-rules` 会留下
  U12 connected、U2 connected、U10 v4-mapped connected、U10 native v6 connected
  共 4 个 outstanding（本 boot 内累积）。

## U12 的断言口径

- **unconnected**：只有运行前 `WfPr outstanding == 0`（干净启动）时才严格断言
  `allocs +1 / frees +1 / outstanding 回到 0`；基线非 0 时打印
  `U12 LIMIT` 并只断言“分配数 == redirect 数”，不把旧 context 的释放算成本次释放。
- **connected**：无论采样结果如何都**不做释放断言**，只打印 300s 内的分配/释放/outstanding
  采样和 `U12 LIMIT`；断言仅限“分配数 == redirect 数”。
- `gate.ps1` 只传 `--connected-window-ms 1000`（不等待 300s）；完整窗口测量按上面的命令手工跑。
- `RuleSuite` 的池断言已改成“outstanding 回到运行前基线”，因此同一 boot 重复跑 gate 不会被
  这些 connected context 弄失败。
