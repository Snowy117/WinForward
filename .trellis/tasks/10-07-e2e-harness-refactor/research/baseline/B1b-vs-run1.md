# B1b 回归比对（`ArmKeys` + `IJsonWritable` + IDLE/THRU 类型化 vs 基线 run1）

本轮把 `result` 记录的 `metrics` 子树接上 `IJsonWritable` 生产路径、把 IDLE/THRU 两个臂的 metrics 换成强类型
record、并给 `ArmKeys` 建了 Common/Idle/Throughput 三个分片。**记录形状与字段名一个都没动**（IDLE/THRU 的键
不存在需要收敛的拼写），所以判定线是：结构差异必须只剩 A2 引入、run1 里没有的 `planSource`；条件键差异为
0；`targetSummary` 的整轮总量逐位相同。

**被比对的两侧二进制**（`publish.sh` 强制重建三份产物，不是 selftest 的"缺了才 publish"）：

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 | `linux/WinForward.E2E.Contracts.dll` sha256 |
|---|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e`（见 [00-state.md](./00-state.md)） | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` | 无此文件（A0 时尚未拆出 Contracts） |
| `b1b`（本轮） | HEAD `5219470` + 本轮未提交工作树 | `c8083bd2d9b3cc94ce2236a6a6b16bc1b4e5ac031cef3191ba13a705e9e2528b` | `53aa16044a113c6ca3b981a67e933ffb5a0d5442545c5e89e58d3a61ae93aae1` |

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                    # 强制重建三份产物，退出码 0
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json            # 退出码 0，11 条 result、11 条 armSummary
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/b1b-run/
cd .trellis/tasks/10-07-e2e-harness-refactor/research
python3 ../../../../benchmarks/WinForward.E2E/scripts/compare-records.py \
    baseline/run1 /tmp/b1b-run --normalize record-normalize.json --band baseline/jitter-band.json \
    --json-out /tmp/b1b-findings.json
# 退出码 1（有 findings），文本输出 607 行
```

本轮的 selftest 产物留在 `/tmp/b1b-run/`（`out/`、`ledger.jsonl`、`target.out`、`client.out`、`console.log`），
未随任务提交：run1/run2 是基线定义，本轮是一次回归检查，任何时候都能用上面四条命令重建。

**第一轮 selftest 是红的，抓到并修掉了一个真实缺陷**（细节见 §6）。

## 2. 三栏结果（`compare-records.py` 原样）

| 栏 | 计数 | 内容 |
|---|---|---|
| 结构性（键集/类型/数组长度/字符串值） | **1** | `records/run.json planSource: only in after` —— A2 已引入、非本轮（D14.23 的有意新增；本轮 selftest 走 `--plan`，值是 `"file"`） |
| 条件键（UseTcp/UseUdp/非空直方图） | **0** | — |
| 数值（逐键抖动带） | **594** / 903 条被测路径 | 四类分解见 §3 |
| `target.out`（归一化文本） | **0** | 三行归一化后完全相同（监听行、dns-alt 行、`stopped.`） |

**结构差异 ⊆ 改名表的 changed/added/removed 子集**：唯一一条结构项 `planSource` 在
[`contract-rename.json`](./contract-rename.json) 里就是 `kind: "added"` 的 `planSource` 行；
本轮没有任何 `renamed` 行出现在结构差异里（IDLE/THRU 的键名恰好都不在四个收敛族内，见 §5）。

记录集本身也逐臂核过（比对范围之外的独立证据）：

| 文件 | run1 行数 | b1b 行数 | 记录类型集合 |
|---|---|---|---|
| `IDLE.jsonl` | 7 | 7 | 1 `result` + 1 `armSummary` + 5 `sample` |
| `LAT.jsonl` / `LATLOAD.jsonl` / `DNS.jsonl` / `DNSALT.jsonl` / `THRU.jsonl` | 10 | 10 | 1 + 1 + 8 |
| `LOSS.jsonl` / `MIX.jsonl` / `PERSIST.jsonl` / `BASE.jsonl` | 12 | 12 | 1 + 1 + 10 |
| `REL.jsonl` | 18 | 18 | 1 `result` + 1 `armSummary` + 6 `attempt` + 10 `sample` |
| `ledger.jsonl` | 258 | 258 | 97 `udpSummary` + 157 `tcp` + 2 `dnsSummary` + 1 `tcpSummary` + 1 `targetSummary` |

`targetSummary`（50 个叶子）**47 个逐位相同**，只有 `utc`/`startedTicks`/`endedTicks` 三个时钟字段不同：
`udp.received/bytes/undecodable/sendErrors = 6967/1419144/0/0`、`tcp.connections/protocolErrors = 157/0`、
`tcp.verdicts` 8 项全同、`dns.*`/`dnsAlt.*` 各 12 项全同、`ledgerWriteErrors = 0`。三类记录（JSONL、账本、
`target.out`）与 run1 的差异面因此只剩身份/时钟/读数。

## 3. 594 条数值差异的 D15 四类分解

分类键是**路径自身**（不按文件），规则按 D15 的表，在 `--json-out` 的 `measured`（903 个
`(文件, 路径)` 对）与 `findings`（595 条，其中 594 条 numeric）上逐条执行，0 条落空：

| D15 分类 | 判定规则（按路径最后一段） | 被测对 | 越带对 | 条目 |
|---|---|---|---|---|
| **结构类** | — | — | **0** | **0** |
| **身份类** | `pid`/`port`、`*Utc`、`planHash`/`clientVersion`/`osDescription`/`frameworkDescription` | 26 | 22 | 64 |
| **时钟类** | `*Ticks`、`ticks`、`wallSeconds`、`elapsedSeconds`、`seconds`、`windowSeconds`、`cpuSeconds`、`idleSecondsObserved`/`idleSecondsScheduled` | 83 | 66 | 192 |
| **契约计数类** | counters（`sentOk`/`supplied`/`arrived`/`late`/`never`/`corrupt`/`clientSendLoss`/`windowOverflow`/`backlogDrops`/`connectAttempts`/`sourceOverflow`/`verdicts.*`/`dns.*`/`outcomes.*`/`rcodes.*`/`queryTypes.*`/`idleLanes` 等）、`gates/*`（`inFlightCeilingMs` 除外，按 D15 第 6 条归读数）、booleans、枚举名、`latency/*/count` | **270** | **0** | **0** |
| **测量读数类** | `*Us`/`*Ms`/`*Bytes`/`*Bps`/`*Mbps`/`*Seconds`、`*Rate`、CPU/内存/`workingSet`/`threads`/`handles`、`latency/*` 的直方图读数、`goodput*`、`budget*`、转移量（`bytes`/`bytesSent`/`frames*`/`perStreamMin/MaxBytes`） | 524 | 115 | 338 |

**契约计数类 270 条被测路径 0 条越带**：全部 `gates.*`、全部计数器、`verdicts.*`/`outcomes.*`/`rcodes.*`/
`queryTypes.*`/`dns.*` 与 `latency/*/count` 都落在逐键带宽内（带宽为 0 的那些逐位相同）。

转移量（THRU 的 `bytes`/`bytesSent`/`frames*`/`perStreamMinBytes`/`perStreamMaxBytes`、账本的
`bytes`/`bytesEchoed`）按 D15 的 `*Bytes` 与"吞吐是读数"两条归**测量读数类**：它们是共享预算下各流/各秒
实际搬了多少，不是"一个固定总体里有多少个事件"。其中 `metrics/perStreamMinBytes` 本轮越带 **32800 字节
（恰好一帧）**：run1↔run2 该键的带宽恰好为 0（两次跑出了同一个最小值），而第三、第四次运行里各流从共享
预算里抢到的帧数不同，最小值就正好差一帧。这正是 D7 说的"两次运行的带宽不足以覆盖第三次运行"的一个实例；
它不是契约计数的位移，故按读数报告。

### 3.1 三条"计数形状"的越带项（按语义归读数，逐条解释）

`ledger.jsonl` 的这三条按**键名形状**是计数器，按**语义**是目标自己观测到的每秒到达曲线，因此与 B1a 的
口径一致，归读数类并逐条解释（B1a 报告 §3.1 的同三条）：

| 路径 | run1 → b1b | 解释 |
|---|---|---|
| `ledger/ledger.jsonl received` | 均值 2844.56 → 2843.55 | 97 条 1 Hz `udpSummary` 的每秒到达曲线形状差异；**整轮总量逐位相同**（两侧 6967 个数据报） |
| `ledger/ledger.jsonl bytes` | 均值 529701.28 → 529490.80 | 同上，`received` 的字节孪生量；整轮总量两侧都是 1419144 字节 |
| `ledger/ledger.jsonl sources/datagrams` | min 1 → 2（带宽 0） | `sources[]` 按（地址，**临时端口**）成行，端口每次运行都变，该行本身是身份类行；整轮总量 6967 不变 |

### 3.2 读数类与身份类的越带项是抖动，不是改动

身份/时钟两类的越带项全部是运行身份与单调时钟前移：run1 的 `run.json.startedTicks` =
`19808074756754`（`2026-10-06T17:34:47Z`），本轮 = `32056173673481`（`2026-10-06T20:58:55Z`），
相隔 1.2248099×10¹³ ticks（`Stopwatch.Frequency` = 10⁹，即约 3.40 小时）；`pid` 与
`ledger.sources/*` 的临时端口每次运行都变，属 D15 的信息性移动。读数类的位移量级与 B1a 相同
（`achievedRate` 一类在 0.1% 量级），
本轮**没有改任何算术**：IDLE 的 `elapsedSeconds`、THRU 的 `goodputBps`/`goodputMbps` 与迁移前
逐表达式相同（`NumberFormat.Round` 的小数位不变），所以位移只能来自测量抖动。

## 4. IDLE/THRU 两个已迁移 kind 的字段级证据

生产写出路径（`IdleMetrics`/`ThroughputMetrics.WriteTo` → `ClientRunner.WriteResultAsync` → `JsonlSink`）
落盘的 `result` 记录与 run1 逐字段对照：

| 项 | IDLE | THRU |
|---|---|---|
| 顶层键序 | 相同（`type/arm/kind/label/parameters/metrics/latency/gates/notes/startedTicks/endedTicks`） | 相同 |
| `metrics` 键序 | 相同 | 相同（19 键，全部 `ArmKeys.Throughput` 的声明序） |
| `metrics` 值差异 | 只有 `elapsedSeconds`（读数） | 只有读数：`bytes`/`frames`/`framesEchoed`/`elapsedSeconds`/`goodputBps`/`goodputMbps`/`perStreamMinBytes` |
| `parameters` / `gates` / `latency` / `notes` | 完全相同 | 完全相同 |
| 缺键（null 或省略） | 无（IDLE 没有 nullable 字段，也没有条件字段） | 无（19 键全在；两个 goodput 在两个 run 里都是数字） |

## 5. 盘点与改名表的一致性检查

```bash
R=.trellis/tasks/10-07-e2e-harness-refactor/research
python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py inventory \
    --run /tmp/b1b-run --out $R/contract-inventory.json
python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename \
    --baseline $R/baseline/run1 --run /tmp/b1b-run \
    --out-json $R/contract-rename.json --out-md $R/contract-rename.md
```

- 盘点：13 个 group（11 个臂文件 + `run.json` + 账本）、**590 条** distinct path。
- 改名表：**592 行** = `identical` 580 + `renamed` 9 + `added` 3 + `removed` 0。
- **本轮落地的改名：0 条**（IDLE/THRU 的键名都在四个收敛族之外）；9 条 `renamed` 全部处于
  "仍在使用旧拼写"状态，由 B2 执行。
- 交叉检查：fresh run 里**没有任何未登记的路径**（脚本的 `unexpected` 栏为空，退出码 0）；
  未观测到的登记项 2 条 = `error`/`detail`（绿 run 不产生 `error` 记录，登记为 `added`）。
- 独立核对：`ArmKeys.Common.Parameters` 的 34 个常量 == 产物里的 34 个 `parameters/*` 路径（对称差为空）；
  `ArmKeys.Common.Gates` 的 11 个常量 == 产物里的 11 个 `gates/*` 路径（对称差为空）。

## 6. 本轮抓到的缺陷（selftest 红 → 修 → 加护栏）

第一轮 selftest 在最后一个臂红：

```
e2e jsonl /tmp/wf-bench/selftest/out/BASE.jsonl: write failed 1 time(s):
InvalidOperationException: Cannot write a JSON property name following another property name. A JSON value is missing.
```

根因：`IJsonWritable.WriteTo` 的语义是"写一个**完整的 JSON 值**"，但 `BASE` 臂会把相位臂的 metrics **嵌套**进
自己的 `metrics` 字典（`["latency"] = latency.Metrics`）。`JsonValue.Write` 新增的 `IJsonWritable` 分支当时
按"只写成员、不写花括号"处理，于是外层属性名后面直接跟了内层属性名。修法：`IdleMetrics`/`ThroughputMetrics`/
`DictionaryMetrics` 三个 `WriteTo` 各自写 `WriteStartObject`/`WriteEndObject`，`WriteResultAsync` 不再自己开合
括号。新护栏 `ContractShapeTests.ARecordThatNestsAnotherRecordsMetricsWritesItAsOneObject` 复现该嵌套形状
（迁移前的 7 条形状用例都覆盖不到它，因为已迁移的两个 kind 不嵌套 metrics）。

## 7. 结论

1. **结构差异只有 A2 的 `planSource`**：本轮把 metrics 子树接上 `IJsonWritable`、把两个臂换成强类型 record，
   没有让任何一行的键集、键序、类型或形状变化；条件键差异为 0。
2. **契约计数零越带**：270 条计数/门/布尔/枚举/`latency/*/count` 路径全部落在逐键带宽内。
3. **IDLE/THRU 的字节形状与 run1 相同**，只有读数与时钟在动；`targetSummary` 的整轮总量逐位相同。
4. 594 条数值差异全部落在身份（64 条）/时钟（192 条）/读数（338 条）三类，按 D15 是信息性移动。
