# B1-a 回归比对（Json 原语 + `JsonlSink` 合一后的树 vs 基线 run1）

本轮只做「共享 Json 原语 + `JsonlFile`/`LedgerWriter` 合一 + 调用点迁移」，不动任何字段名、记录形状或
臂的语义，所以本报告的判定线是 **结构差异必须为空**（唯一的例外是 A2 已引入、run1 里没有的 `planSource`）。

**被比对的两侧二进制**（`publish.sh` 是强制重建，不是 selftest 的"缺了才 publish"）：

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 |
|---|---|---|
| `run1`（基线） | A0：HEAD `d90707eb`（见 [00-state.md](./00-state.md)） | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` |
| `b1a`（本轮） | A2 全部改动 + 本轮 Contracts/JsonlSink 合一（未提交工作树） | `312c39179576d1515bb1c28515706ea1746756fe574cac83ff344f55250d4263` |

本轮新增的 `linux/WinForward.E2E.Contracts.dll` sha256 =
`15f43bde6f8a71e242e50e76d2826598f322eee1cf7590ee43320568717eed62`（run1 无此文件）。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                    # 强制重建三份产物，退出码 0
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json            # 退出码 0，11 条 result、11 条 armSummary
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/b1a-run/
cd .trellis/tasks/10-07-e2e-harness-refactor/research
python3 ../../../../benchmarks/WinForward.E2E/scripts/compare-records.py \
    baseline/run1 /tmp/b1a-run --normalize record-normalize.json --band baseline/jitter-band.json \
    --json-out /tmp/b1a-findings.json
# 退出码 1（有 findings），文本输出 625 行
```

本轮的 selftest 产物留在 `/tmp/b1a-run/`（`out/`、`ledger.jsonl`、`target.out`、`client.out`、`console.log`），
未随任务提交：run1/run2 是基线定义，本轮是一次回归检查，任何时候都能用上面四条命令重建。

## 2. 三栏结果（`compare-records.py` 原样）

| 栏 | 计数 | 内容 |
|---|---|---|
| 结构性（键集/类型/数组长度/字符串值） | **1** | `records/run.json planSource: only in after` —— **A2 已引入、非本轮**（D14.23 的有意新增，run1 里没有这个键；本轮 selftest 走 `--plan`，值是 `"file"`） |
| 条件键（UseTcp/UseUdp/非空直方图） | **0** | — |
| 数值（逐键抖动带） | **612** / 903 条被测路径 | 四类分解见下 |
| `target.out`（归一化文本） | **0** | 三行归一化后完全相同（监听行、dns-alt 行、`stopped.`） |

**本轮没有任何字段名或记录形状变化**：除 `planSource` 外，结构性差异为空；条件键差异为空。

记录集本身也逐臂核过（比对范围之外的独立证据）：

| 文件 | run1 行数 | b1a 行数 | 记录类型集合 |
|---|---|---|---|
| `IDLE.jsonl` | 7 | 7 | `result`+`armSummary`+5 `sample` |
| `LAT.jsonl` / `LATLOAD.jsonl` / `DNS.jsonl` / `DNSALT.jsonl` / `THRU.jsonl` | 10 | 10 | `result`+`armSummary`+8 `sample` |
| `LOSS.jsonl` / `MIX.jsonl` / `PERSIST.jsonl` / `BASE.jsonl` | 12 | 12 | `result`+`armSummary`+10 `sample` |
| `REL.jsonl` | 18 | 18 | `result`+`armSummary`+6 `attempt`+10 `sample` |
| `ledger.jsonl` | 258 | 258 | 97 `udpSummary` + 157 `tcp` + 2 `dnsSummary` + 1 `tcpSummary` + 1 `targetSummary` |

## 3. 612 条数值差异的 D15 四类分解

分类键是**路径自身**（不按文件），判定规则写在下面，便于复核；903 条被测路径逐条落入四类，
0 条落空：

| D15 分类 | 判定规则 | 被测路径 | 越带路径 | 条目 |
|---|---|---|---|---|
| **结构类** | — | — | **0** | 0 |
| **身份类** | `*/pid`、`planHash`、`clientVersion`、`osDescription`、`frameworkDescription`、`sources\|processes/N/address\|port\|startUtc`、`*Utc` | 11 | 11 | 33 |
| **时钟类** | `*Ticks`、`ticks`、`wallSeconds`、`elapsedSeconds`、`seconds`、`windowSeconds` | 46 | 45 | 131 |
| **契约计数类** | counters、`gates.*`、booleans、枚举名、`latency/*/count` | **305** | **0** | **0** |
| **测量读数类** | `*Us`/`*Ms`/`*Bytes`/`*Bps`/`*Mbps`/`*Seconds`、`*Rate`（含 `achievedRate`）、CPU/内存/`workingSet`/`threads`/`handles`、`spread`、`bytesPerPage`、`latency/*`、目标自读的 `bytes`/`received`/`datagrams` | 411 | 162 | 448 |

**契约计数类 305 条被测路径 0 条越带**：`sentOk`/`supplied`/`received`/`arrived`/`late`/`never`/
`clientSendLoss`/`windowOverflow`/`backlogDrops`/`connectAttempts`/`framesSent`/`budgetReached`/
`scheduleTruncated`/`verdicts.*`/`dns.*`/`outcomes.*`/`rcodes.*`/`queryTypes.*`/`idleLanes` 等全部落在带宽内
（绝大多数带宽为 0，即逐位相同）。

身份类 11 条 = 每个臂自己的新 `pid`（11 个臂各一条）+ `ledger.sources/*`（见下）。时钟类 44 条全部是
单调时钟前移：run1 起跑于 `2026-10-06T17:34:47Z`（ticks `19808074756754`），本轮起跑于
`2026-10-06T19:55:14Z`（ticks `28234964532535`），相隔 8426.9 s = 8.427×10¹² ticks（`Stopwatch.Frequency`
= 10⁹），属 D15 的信息性移动。

### 3.1 目标侧"计数器形状"的越带项（逐条解释）

`ledger.jsonl` 的这三条按**键名形状**是计数器，按**语义**是目标自己观测到的流量读数，因此归读数类
并逐条解释（它们是本轮唯一"计数形状"的越带项）：

| 路径 | run1 → b1a | 解释 |
|---|---|---|
| `ledger/ledger.jsonl received` | 均值 2844.56 → 2842.91（Δ −1.65，带宽 0.887） | 97 条 1 Hz `udpSummary` 的**每秒到达曲线**形状差异；**整轮总量逐位相同**：两侧都是 6967 个数据报、1419144 字节（见下表） |
| `ledger/ledger.jsonl bytes` | 均值 529701 → 529366（Δ −335，带宽 174） | 同上，`received` 的字节孪生量；整轮总量 1419144 字节两侧相同 |
| `ledger/ledger.jsonl sources/datagrams` | min 1 → 2（带宽 0） | `sources[]` 按（地址，**临时端口**）成行，端口每次运行都变（`40572` → `44088`），该行本身就是身份类行；整轮总量 6967 不变 |

整轮总量对照（`targetSummary`，两侧逐位相同）：

| 量 | run1 | b1a |
|---|---|---|
| `udp.received` / `udp.bytes` / `udp.undecodable` / `udp.sendErrors` | 6967 / 1419144 / 0 / 0 | 6967 / 1419144 / 0 / 0 |
| `tcp.connections` / `tcp.protocolErrors` | 157 / 0 | 157 / 0 |
| `tcp.verdicts` | `clean 61, reset 25, partialFin 25, halfClose 25, stall 0, clientClosedEarly 21, protocolError 0, error 0` | 同左，逐项相同 |
| `dns.*`（12 个计数） | `udpQueries 329, udpAnswers 259, udpEmpty 70, udpMalformed 0, udpSendErrors 0, tcpQueries 81, tcpAnswers 77, tcpEmpty 4, tcpMalformed 0, tcpConnections 1, tcpAborted 0` | 同左 |
| `ledgerWriteErrors` | 0 | 0 |
| `sourceOverflow`（97 条求和） | 0 | 0 |

### 3.2 读数类的越带项是抖动，不是改动

448 条读数类条目覆盖 `latency/*/{min,mean,p50,p90,p99,p999,max}Us`、`windowCeilingMs`、
`gates/inFlightCeilingMs`、`cpuSeconds`、`privateBytes`、`workingSetBytes`、`goodputBps/Mbps`、
`achievedRate`、`meanConnectMs`、`meanTransferMs` 等。它们相对带宽的超出量级很小：`achievedRate` 一类的
位移在 0.002–0.09 之间（带宽 0–0.013），即 0.02% 量级。`achievedRate` 本轮从 `JsonValue.PerSecond`
换成 `JsonPerSecond.PerSecond` 时**表达式逐位不变**（唯一变化是 `ticks <= 0` 的分支，而 selftest 里
`elapsedTicks` 恒为正），所以这些位移只能来自测量抖动。

## 4. 结论

1. **结构差异为空**（除 A2 的 `planSource`）：本轮把两个 JSONL writer 合成一个、把四个数值原语搬进
   Contracts、把 client 的 flush 从"臂结束一次"改成 1 Hz，没有让任何一行的键集、键序、类型或形状变化。
2. **契约计数零越带**：8 个臂的全部计数、`gates.*`、booleans、枚举名与 `latency/*/count` 都在带宽内，
   其中带宽为 0 的那些逐位相同。
3. **账本等价**：记录条数、类型分布、键序（`utc` → `label` → body）、字段集合与整轮总量都与 run1 一致，
   `ledgerWriteErrors` 仍为 0；`JsonlFile`/`LedgerWriter` 合一没有动账本这个跨二进制契约。
4. 越带项全部落在身份/时钟/读数三类（612 条 = 33 + 131 + 448），按 D15 是信息性移动。
