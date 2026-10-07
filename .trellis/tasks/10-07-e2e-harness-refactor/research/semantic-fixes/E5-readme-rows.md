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

## 5. E3-e（`achievedRate` 口径统一与 `completionRate` 新键）待改行

行号是 **E3-e 施工后**的 `benchmarks/WinForward.E2E/README.md`（HEAD `46429b7` 的工作树）。
核对命令（本文件里每一行都据此判过）：

```console
$ rg -n 'achievedRate|completionRate' benchmarks/WinForward.E2E/README.md
rc=1                       # 零命中：README 里没有这两个驼峰键名
$ rg -n 'achieved rate' benchmarks/WinForward.E2E/README.md
421:| `gates/inFlightCeilingMs` | the tightest direct-measurable-latency ceiling, `window × lanes / achieved rate`; …
$ rg -n 'achievedRate|completionRate' analysis/analyze.py analysis/README.md
rc=1                       # 分析器与分析器 README 都是零命中
```

**结论**：README 里唯一提到这两个速率的地方是 `:421` 的**散文**「achieved rate」（**LAT** 的
`gates/inFlightCeilingMs` 公式，与本轮无关，且 `:421` 是 E3-b1 已改过的行）；`completionRate` 零命中；
两个速率都**不在**分析器的读键表（`:376` 是 `persistent` 的读键行，不含任何速率）。所以 E3-e **不改任何
README 行**，只登记下面两条「将来若要写进文档就该这么写」的语义，交 E5 决定是否落笔。

| 文件:行 | 今天写的 | 应该知道的 |
|---|---|---|
| `benchmarks/WinForward.E2E/README.md:351` | `PERSIST`（persistent）臂的能力行：one long-lived TCP connection … | 若要列发布键：`metrics/achievedRate` = **发送成功的请求/秒**（`_sentRequests`），`metrics/completionRate` = **应答/秒**（`responses`，即 D19.2 ⑧ 之前 `achievedRate` 的那个总体）。两者同分母（臂的 elapsed），所以 `requests > responses` 的运行里 `achievedRate > completionRate`；`requests == responses` 的运行里两者逐值相等（E3-e 证据 §4 的单轮对照） |
| `benchmarks/WinForward.E2E/README.md:372` | `reliability` (REL) 的读键行（含 `metrics/outcomes/<name>` 等，不含 `achievedRate`） | **无需改动**：REL 的 `metrics/achievedRate` 分子从 `attempts.Length` 改成「请求发送完成的尝试数」（`TransferMeasured`），但该键不在分析器读集里。若要写进散文，口径与 PERSIST 同：成功发出的请求/秒 |
| `benchmarks/WinForward.E2E/README.md:376` | `persistent` (PERSIST) 的读键行（`requests`、`responses`、`responseRate`、…，不含速率） | **无需改动**：`completionRate` 不在分析器读集里；`responseRate`（`responses/requests`）与两个速率是不同的统计，别并排引用 |

**分析器侧零改动**（E3-e 证据 §6）：`rg -n 'achievedRate|completionRate' analysis/analyze.py` 无输出；
`analysis/README.md` 同样零命中；`synthetic/make_tree.py` 的 PERSIST 块继续写它自己的 `achievedRate`
（合成树只喂渲染，不校验发布键集），因此 `verification/*` 无需重生成。

**ODE 语义**（teardown 不产生数据点，D19.2 ⑨）在 README 里**没有任何旧说法**可改
（`rg -n 'ObjectDisposed' benchmarks/WinForward.E2E/README.md` 无输出）；契约落在
`.trellis/spec/backend/measurement-harness.md` §3.9/§3.10，若 E5 要在 harness README 里复述一句，
照那两节的措辞即可。
