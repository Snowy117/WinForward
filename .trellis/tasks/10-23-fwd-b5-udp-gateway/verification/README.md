# B5 证据与 VM 保留物

本目录保留第三轮/第四轮复核的原始产物：

- `b5-client-round3.out/.err`：检查者客户端 stdout/stderr。
- `b5-race.out`：实现者按“客户端晚于 suite 30s 窗口”复现的客户端 stdout（count=0）。
- `gate-b5-final4.log`：最终 gate `GATE-RESULT=PASS`（PREfast 0、userland-e2e 48、vm-check 78）。
- `b5-controller-udp-final.log`：最终产品 E2E 日志（10/10、`back=10`、退出顺序 disabled → 停 relay → 清 blob）。
- `product-client.out/.err`：产品 E2E 客户端 verify 原文（count/bytes/SHA/source/source_ok/ok）。
- `fake-socks5-product.log`：fake SOCKS5 UDP 的回显记录（1 次 ASSOCIATE、10 条 datagram）。
- `tcp-core-client.out`、`tcp-core-suite.out`、`tcp-core-ready.txt`：修复 payload 后的 TCP core（2048B，sha
  `7c7272c9...`，`ok=1`，suite PASS）。
- `env-matrix.txt`：Linux 地址/路由/邻居/防火墙/Windows alias 的现场。
- `b5-40250.txt`：Windows pktmon 对 `.4:40577 > .99:40250` 的 Rx/Drop 记录。
- `repro-aligned/`：受控时间戳证据（见 `repro-round3.md`）。

## gate 时序

`gate-b5-final4.log` 产生于当前 worktree（`HEAD=e9e7e1110170f88e9343d2e7265df7c22369f361`，B5 改动未提交）。
gate 之后只改了测试/证据侧文件：`tools/linux/*client.py`、`tools/windows/prepare-b5-udp-e2e.ps1`、
`tools/wfprobe/OnLinkAlias.cs` 的注释，以及 `verification/` 文档；没有改驱动、ABI、表、或
`WfpController` 行为。因此 gate 路径不受这些改动影响，未重跑完整 gate；PREfast 0 与 gate PASS 仍对应当前
驱动/产品代码。

## B6 记录

以下不阻塞 B5 提交，已在任务中记录：

- U7 decoy `available=1` 原帧 permit 异常（有序复跑为 0，但未定位，suite 遇到即 FAIL）。
- 分片 raw/显式构造 9 片测试（当前用 4000B + MTU 触发 9 片）。
- `reject` 产品级 `ForwardedUdpRelay` ASSOCIATE 拒绝 E2E（当前是 wfprobe 测试替身）。
- B4b `acked` 真实 TCP ACK 语义（当前客户端用实收字节填充）。
- capacity=8 测试构建 / 生产 4096 真满。
- host UDP gate 单条断言偶发 flake。

## VM 保留物

- `C:\workspace\WinForward.Kernel`：最终源码 + 构建产物 + 本轮日志，供检查者按同一 suite/wfprobe 复跑。
- `C:\workspace\rollback\wfprobe-abi5-b5.sys`、`wfprobe-abi5.sys`：回滚用签名驱动；按任务要求保留。
- 当前为便于复跑保留 `.99/24` 与 `B5-ALIGN-*` 防火墙规则；验收后清理。
- 驱动 `wfprobe` 当前 RUNNING；forwarded config 已恢复 `rules=0 fallback=permit config=disabled`。
