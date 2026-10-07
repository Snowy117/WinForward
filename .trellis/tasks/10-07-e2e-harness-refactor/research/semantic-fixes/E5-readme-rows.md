# README 行的待改清单（交 E5）

**归属规则**：D19.2 ② 裁定 E3 **只改**与 `windowOverflow` caveat 直接相关的 README 段落（本轮改了
`benchmarks/WinForward.E2E/README.md:421`），其余行登记在此由 **E5** 统一改。行号是 **E3-b1 施工后**的
HEAD 工作树行号（`git rev-parse` 见 `research/baseline/E3b1-vs-run1.md`）。

---

## 1. E3-b1（`#12` / D7）改动的行——这些行今天与代码相反

| 文件:行 | 今天写的 | 应该改成 |
|---|---|---|
| `benchmarks/WinForward.E2E/README.md:414` | `gates/clientSendLoss` 行：「for `LOSS` and `MIX`, the datagrams still in flight when observation stopped plus refused sends and window overflows」+「The four arms that cannot destroy a sample write a literal `0`: `IDLE` … `THRU` … `REL` … `DNS` publishes a pacing slot skipped by a full in-flight window as `metrics.unsent`, which never enters `sent`」 | LOSS/MIX 的公式是**四项**：`abandonedAtTeardown + sendFailures + windowOverflow + sentOutOfRangeSequences`（MIX 的 `windowOverflow` 是结构恒零）；IDLE 是**唯一**结构恒零的臂（无流量、无发送侧计数器，记录保留 `0`，分析器渲染 `n/a`）；THRU 的门是 `metrics.sendFailures`（预算拒绝发生在提供之前，不算毁样本）；REL 的门是 `metrics.scheduledAttempts − metrics.connectAttempts`（背压不丢弃，正常跑为 0，分析器另有该恒等式的身份检查）；DNS 的门是 `metrics.unsent`（**不含** `socketErrors`，D19.2 ④） |
| `benchmarks/WinForward.E2E/README.md:371` | `loss` (LOSS) 的契约键清单 | 加 `metrics/sentOutOfRangeSequences`（新键，只增）。注意该行是「分析器读哪些键」的表，而分析器今天**不读**任何越界键（`rg -c outOfRange analyze.py` = 0）——E5 自行决定是加进读表还是写进契约散文，但 `:583-588` 的旧说法必须改 |
| `benchmarks/WinForward.E2E/README.md:375` | `mix` (MIX) 读键清单 | **无需改动**（分析器不读越界键；此处登记以免 E5 误以为漏了） |
| `benchmarks/WinForward.E2E/README.md:583-588` | 「The UDP sequence space is bounded at 2¹⁸ − 1」段：`metrics.outOfRangeSequences` 是「offered and handed to the socket, but it is not booked as sent … **it is not part of the published `clientSendLoss`**」 | 拆成两个键：`metrics.outOfRangeSequences` 只数**接收侧**（到达/损坏帧命名的越界序号，属于测量 caveat），`metrics.sentOutOfRangeSequences` 数**发送侧**（被拒的 offer 槽，从未交给 socket、不在任何桶里、**计入** `clientSendLoss`）。MIX 侧同名键在 `metrics/classes/udp/` 下 |
| `benchmarks/WinForward.E2E/README.md:605` | 「nothing was dropped by the client」行：只提 `gates.windowOverflow` 是 disclosure | 补一句：该臂的延迟格子由分析器渲染为 `n/a (windowOverflow > 0)`（见 `:421`，E3-b1 已改） |

## 2. E3-b1（`#8`）改了的那一行（登记备查，已由 E3 完成）

| 文件:行 | 改动 |
|---|---|
| `benchmarks/WinForward.E2E/README.md:421` | `gates/inFlightCeilingMs` 行补上「分析器把该臂的延迟格子渲染为 `n/a (windowOverflow > 0)`；`windowOverflow == 0` 的记录渲染逐字节不变」 |

## 3. 分析器文档（E4 收口时一并处理）

| 文件:行 | 应该改成 |
|---|---|
| `benchmarks/results/2026-10-06-e2e-competitors/analysis/README.md:164` | §"The gates" 里「A reached in-flight ceiling is a disclosed `warn`（the tail is measured through the deferred queue）」补一句：同一 gate 还让 §5 的该臂延迟格子渲染 `n/a (windowOverflow > 0)`，零值记录不变 |
| `benchmarks/results/2026-10-06-e2e-competitors/analysis/README.md:180` | `measurement-caveat` 的例子里已含 "a reached in-flight ceiling"（不变），可选：把新格子形式写进同一行 |

## 4. 与 E3-b1 无关但同属 D19.2 ② 的既有待改行（原样登记，未核对）

本文件**只**登记 E3-b1 涉及的行；父任务 `implement.md` 提到的其它 README 旧拼写（E2 改名遗留）
由 E5 按其自己的清单处理，不在此重复。
