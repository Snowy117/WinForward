# E3 执行计划（批次化，前提复核后）

**效力**：父 `design-decisions.md`（**D19 是本轮权威**，另有 D0/D7/D9/D14.x/D18.x）> 父 `prd.md`/`design.md` >
本目录 `prd.md` > 本文件。**施工前必须先读 `D19.2 ⑲` 的失效行号速查**。

前提复核：`.trellis/tasks/10-07-e2e-harness-refactor/research/semantic-fixes/E3-premises.md`（21 条逐条结论）。

---

## 起点（E1/E2 已交付）

契约单一定义 + 账本键类型化、`Client/Lanes/` 接缝（引擎/策略/transport）、7+ 文件拆分（AC1 达成）、
CLI 合一（快照护栏）、264 用例、`research/baseline/*` 全套比对证据。
E3 **只需**做 D19.1 的 10 件事；审计原文的其余条目**已有回归护栏**或**已不成立**。

---

## 批次总览

| 批次 | 内容 | 结束判据 | 提交 |
|---|---|---|---|
| **E3-a** | MIX tracker 并发契约（镜像 D4）+ tracker 并发测试 + `#4`/`#5`/`#7`/`#10` 的定向证据/穷举 | 并发恒等式 N 轮不破；0/10/50% 丢包 `sent == supplied`；1..6 穷举 `(1,3,2)` 计 1 | 1 |
| **E3-b** | `#12` 四臂 + MIX 的 `clientSendLoss` 派生；D7 `SentOutOfRange` + 新键；`#6` plan→`metrics.window`；`#8` 格子 `n/a` + README 段；`#9` 臂级 drain 用例；`#11` `undecodable` 消费；`#19` 仅用户态 + `check-fairness.py` | 每个 gate 都能真的失败（逐个反证）；新键形状测试；分析器可 grep 断言 | 1–2 |
| **E3-c** | `FrameReadStatus.Truncated`（EOF 半帧）+ 两台 server 记账 + 环境键 + 不写 trailer + `ReliabilityExchange` 的 `default:` | 单测：半帧不再读成干净 half-close；target 不再为截断连接写 trailer；selftest 无误报 | 1 |
| **E3-d** | 账本 `detail`/`acceptErrors`/`udpReceivers` + `--udp-receivers`（只增；走 `JsonlSink`）+ 形状测试 | 账本字节等价（除新增字段）；选项非法 → 退出码 2 | 1 |
| **E3-e** | ODE 统一（先枚举净影响）+ `achievedRate` 单一口径 + `completionRate` 改名 + 数值变化登记 | 5 处 ODE 形态归位；发布值变化逐条登记；比对证据 | 1 |

每批：六条门禁 + `compare-records.py`（带 `--band`/`--rename-table`，**本任务新增的键必须先登记进 `contract-rename.json` 的 `added`**，见 D19.3 A）+ 同一二进制噪声地板对照 +
`--strict` 读数摘要，落 `research/baseline/E3x-vs-run1.md`；改动数值的批在 commit message 里引用
`research/semantic-fixes/index.jsonl` 条目。

---

## E3-a：并发契约与数值证据

1. **MIX tracker 并发契约**（D19.2 ①/⑯ + **D19.3 F**）：`MixUdpLoop` 的接收任务**只**投递 settlement
   （携带**未 Resolve** 的 verdict + `arrivedTicks`），发送线程在配速点 drain 结算，**`tracker.WasSent` 与
   `pending.TryRemove` 都在发送线程做**（照 `UdpLatencyPolicy.Book`）；`UdpReliabilityTracker` 保持单写者。类文档逐字写明契约（照 `ILanePolicy.Settle` 的形状，但不共享类型）。
2. **并发测试**：发送 + 接收并发 N 轮（≥1000），恒等式 `delivered == sent == settled`、
   逐序号恰好一次、`pending`/`inFlight` 归零；在负载下重复跑。
3. `#4` 仿真：0/10/50% 丢包下 `sent == supplied` 且发满全程（用假的/仿真的发送路径，不依赖真实网络）。
4. `#5` 回归：溢出槽位场景下 `never` 不含从未发出的序列。
5. `#7` 回归：人为制造客户端阻塞 → `late`/`never` 反映阻塞（不是被减掉）。
6. `#10` **穷举 1..6 的到达顺序**（today 只有 `3,1,3` 一例）：断言枚举规模（720，或按审计 1956 口径并注明）、
   升序 `Reordered == 0`、`(1,3,2)` 单列为 1。
7. 本批**不 assert `clientSendLoss`**（D19.3 G），并在证据里写明。

## E3-b：数值语义与记账

1. `#12`：`IdleArm`/`DnsArm`/`ThroughputArm`/`ReliabilityArm` 的 `clientSendLoss` 由各自计数器派生
   （参照 `LossArm.cs:37`；MIX 一并改，D19.2 ⑥）；IDLE 记录里**保留 0**、`n/a` 只在分析器/README 渲染（D19.3 E）；REL 的恒等式分析器已有、记录侧不新增键；
   DnsArm 分子只用 `unsent`（④）。**每个 gate 都要有反证**（构造能失败的输入）。
2. D7 记账：新增 `SentOutOfRange` + 发布键 `sentOutOfRangeSequences`（只增、`required`），
   `clientSendLoss` 公式含越界项；守卫里的 `_sent.TrySet` **必须保留**；证明"越界槽不在任何桶里"的变化可解释。
3. `#6`：一份端到端用例证明"plan 声明非默认 `lossWindowMs` ⇒ `metrics.window` 等于声明值"（D19.2 ⑱）。
4. `#8`：`windowOverflow > 0` 时该臂延迟格子输出 `n/a (windowOverflow > 0)`；README 对应段落同步
   （其余 README 行登记给 E5，D19.2 ②）；**不改** `DeferredQueued→windowOverflow` 接线。
5. `#9`：臂级用例证明"臂末尾在途请求被采样"（TCP 直方图样本数上升）。
6. `#11`：`undecodable` 在分析器有消费（只做 target 侧总量级披露，D19.2 ⑤/⑮：**不删** `corruptRate`）。
7. `#19`：CPU 口径显式披露为"仅用户态"，并新增 `scripts/check-fairness.py`（#17/#18 的可 grep 断言）。

## E3-c：`Truncated`

- `FrameReadStatus.Truncated`（新增成员，**不接进 verdict 枚举**）；`Truncated` ⇒ **跳过 trailer + 既有 `TcpVerdict.ProtocolError` + `tcpSummary/truncatedFrames++`**（D19.3 D）；**DNS 的截断是独立定义**（长度前缀短读，不复用该成员，D19.3 C）；账本键扩容（两层常量 + 两个 keyset + writer 四处）由本批拥有（D19.3 B）；
- EOF 落在帧中间 → `Truncated`（今天读成干净的 half-close，且 TCP server 会写 trailer）；
- 两台 server 各自记账（环境/摘要键）；截断连接**不写 trailer**；
- 喂入接缝已在（`FrameStreamReader` 的内部 read-delegate ctor）；
- `ReliabilityExchange` 的 `default:` 必须显式处理新成员（不得被吸收成 ProtocolError）；
- 判据：定向单测 + selftest 无误报 + 账本/摘要键只有新字段差异。

## E3-d：账本与 CLI

- 账本新增 `detail`/`acceptErrors`/`udpReceivers`（只增，`JsonlSink` 路径，`ledgerWriteErrors` 语义不变）；
- `--udp-receivers`（`{}`/整数；非法 → 退出码 2；默认值 = 今天的硬编码 `TargetRunner.cs:21`）；
- `ArmKeys.Ledger` 分片扩容 + 形状测试（含三态与新字段缺省）；
- 判据：账本归一化字节等价（除新增字段）；CLI 快照新增条目（有意变更登记）。

## E3-e：ODE 与口径统一

1. **先枚举净影响**（哪些发布值会变、变多少）落证据，再改；
2. ODE 唯一语义 = "teardown 不产生数据点"：5 处形态归位（3 处计成数据点 → 忽略；2 处产出 verdict → 显式）；
3. `achievedRate` 单一口径（成功发出的请求/秒）；PERSIST 的完成口径改名 `completionRate`；
4. 数值变化逐条登记进 `index.jsonl` 与证据；
5. 收尾：README 变更清单交给 E5；spec 更新 + 归档。

---

## 证据与落盘

| 产物 | 位置 |
|---|---|
| 每批比对（含噪声地板与 `--strict`） | `research/baseline/E3{a,b,c,d,e}-vs-run1.md` |
| 前提复核与逐条结论 | `research/semantic-fixes/E3-premises.md` + `index.jsonl` 的 `E3-*` 条目 |
| 并发契约文档与仿真/穷举证据 | 同上（E3-a 节） |
| README 待改行清单（交 E5） | `research/semantic-fixes/E5-readme-rows.md` |

## 风险与回退

| 风险 | 缓解 |
|---|---|
| 并发契约改动引入 flake | 恒等式测试 + 负载下重复；不引入真实网络依赖 |
| `Truncated` 误报（正常 EOF 变截断） | 单测覆盖"完整帧后 EOF"与"半帧 EOF"两种；selftest 对照 |
| `clientSendLoss` 公式变化改发布值 | 先枚举净影响；零宽键逐值核对；新键只增 |
| ODE 统一吃掉真实错误 | 只改"teardown 不得产生数据点"，任何非 teardown 的 ODE 保持原语义并逐个列出 |
| README 与 E5 重叠 | E3 只改 caveat 段，其余列清单 |

**回退点**：E3-a…E3-e 各自一个边界（每批门禁全绿才进入下一批）。
