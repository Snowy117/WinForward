# A2 回归比对（Tier 0 止血后的树 vs 基线 run1）

**被比对的两侧二进制**（`publish.sh` 是强制重建，不是 selftest 的"缺了才 publish"）：

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 |
|---|---|---|
| `run1`（基线） | A0：HEAD `d90707eb`（见 [00-state.md](./00-state.md)） | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` |
| `a2`（本轮） | A1 `FrameStreamReader` ctor + A2 的 Tier 0 全部改动 | `9d715829c7d4bd31f5e3e827d7156a2936f86b79a6a9f7f3a9878b699346fa94` |

A1 的改动（internal 读委托 ctor）不写任何记录，A1 自己已用同一条 `run1` 做过比对（结构性差异 0，见
[A1-gates.md](./A1-gates.md)），所以本报告的差异全部归 A2。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                    # 强制重建三份产物
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json            # 退出码 0，11 条 result
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/a2-run/
cd .trellis/tasks/10-07-e2e-harness-refactor/research
python3 ../../../../benchmarks/WinForward.E2E/scripts/compare-records.py \
    baseline/run1 /tmp/a2-run --normalize record-normalize.json --band baseline/jitter-band.json
# 退出码 1（有 findings），输出 557 行
```

本轮的 selftest 产物留在 `/tmp/a2-run/`（`out/`、`ledger.jsonl`、`target.out`、`client.out`、`console.log`），
未随任务提交：run1/run2 是基线定义，本轮是一次回归检查，任何时候都能用上面四条命令重建。

## 2. 三栏结果（`compare-records.py` 原样）

| 栏 | 计数 | 内容 |
|---|---|---|
| 结构性（键集/类型/数组长度/字符串值） | **1** | `records/run.json planSource: only in after` |
| 条件键（UseTcp/UseUdp/非空直方图） | **0** | — |
| 数值（逐键抖动带） | **544** / 903 条被测路径 | 见下 |

**唯一的结构性差异就是 `planSource`**（DD D14.23 有意新增：`"file"` / `"builtin"`，与旧有的
`planPath` 并存；本轮 selftest 走 `--plan`，所以是 `"file"`）。本轮**不做任何字段改名**，因此没有任何
"键名变化"可归因——这与 A3 的提交切分一致：改名表要到 B1 才有。

## 3. 544 条数值差异的分类（DD D15 的四类口径）

544 条 finding 对应 **191 条唯一路径**（每条路径有 min/max/mean 三个条目）：

| D15 分类 | 唯一路径 | 条目 | 代表键 | 判定 |
|---|---|---|---|---|
| **身份类** | 11 | 33 | `processes/pid`（11 个臂各一条；`ledger.sources/*` 本轮恰好落在带内，没进 findings） | 值不参与比对（D15：只校验存在与类型）。本轮全是 arm 自己的新进程号 |
| **时钟类**（D15 四类未列，单独统计） | 43 | 129 | `*Ticks`、`ticks`、`wallSeconds`、`elapsedSeconds`（含 `run.json` 的 `startedTicks`/`endedTicks`） | 测量读数。run1 与 A2 相隔 67.1 分钟（`startedUtc` 17:34:47.12 → 18:41:50.42，本机 `Stopwatch.Frequency` = 10⁹，Δ = 4.0233×10¹² ticks = 4023.3 s），单调时钟必然前移；informational |
| **测量读数类** | 137 | 382 | `latency/*/{min,mean,p50,p90,p99,p999,max}Us`、`cpuSeconds`、`privateBytes`、`workingSetBytes`/`envWorkingSetBytes`/`peakWorkingSetBytes`、`threads`、`generatorCpuSeconds`、`goodputBps/Mbps`、`achievedRate`、`meanConnectMs`/`meanTransferMs`、`windowCeilingMs`、`gates/inFlightCeilingMs` | 真实读数抖动，按 **D15 判为信息性**，不算失败 |
| **契约计数类**（counters / `gates.*` / booleans / 枚举名 / `latency/*/count`） | **2** | **6** | `BASE`、`LATLOAD` 的 `gates/inFlightCeilingMs` | 见下：唯一的跨类键，按读数归类 |

分类无遗漏：191 条唯一路径逐条落入上面四类之一（0 条落空）。

`gates/inFlightCeilingMs` 是唯一一个跨类的键：它按 `gates.*` 属契约计数类，按 `*Ms` 属测量读数类。
本轮它是超带的：BASE 3169.415 → 3170.043（Δ = 0.628 ms > 带宽 0.471），LATLOAD 20474.779 → 20475.086
（Δ = 0.307 ms > 带宽 0，因为 run1↔run2 恰好测得同一个值）；LAT 的 Δ = 15.2 ms 落在它 15.215 ms 的带内。
**它实质是"在途窗口排队延迟的最大值"，是读数不是计数**，本报告按测量读数归类。给 B1 的四类实现留一条
待办：`gates/inFlightCeilingMs` 要么移出"契约计数类"，要么在配置里显式声明它的带宽不能是 0
（两轮测量恰好相同不等于这个量是恒等的）。

## 4. 契约计数逐键核对（精确相等，不看带宽）

对 220 条契约计数/闸门/布尔路径做**逐值精确比对**（不是带宽比对）：**215 条完全相同，5 条不同**——
3 条是 `gates/inFlightCeilingMs`（读数，见 §3），2 条是 `THRU.metrics/frames`、`metrics/framesEchoed`
（4877 → 4876，冻结带宽 1，与 A1 报告里 run1↔run2 的 4877↔4876 是同一现象）。**没有任何布尔值、
枚举值或 `latency/*/count` 发生变化**。

关键计数器的原值（两侧逐值相同）：

| 路径 | run1 | a2 |
|---|---|---|
| `LOSS.metrics/{sent,supplied,arrived}` | 2001 / 2001 / 2001 | 2001 / 2001 / 2001 |
| `LOSS.metrics/{never,outOfRangeSequences,clientSendLoss,lossRate}` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| `LAT.metrics/tcp.sentOk`、`udp.sentOk`、`tcp.connectAttempts` | 322 / 161 / 10 | 322 / 161 / 10 |
| `LATLOAD.metrics/tcp.sentOk`、`udp.sentOk`、`latency/tcp-rtt/count` | 1601 / 1601 / 1601 | 1601 / 1601 / 1601 |
| `DNS.metrics/{sent,answered,answerRate}`、`DNSALT` 同 | 402 / 402 / 1 | 402 / 402 / 1 |
| `REL.metrics/{connectAttempts,scheduledAttempts}` | 101 / 101 | 101 / 101 |
| `MIX.metrics/classes/udp/sent` | 602 | 602 |
| `THRU.metrics/{frames,framesEchoed}` | 4877 / 4877 | 4876 / 4876（带宽 1，落在带内） |
| 每个臂的 `gates/clientSendLoss` | 0 | 0 |
| `run.json` 的 `failed`、`arms[].failed` | false / 全 false | false / 全 false |
| `run.json` 的 `planPath` | `…/scripts/plans/selftest-plan.json` | 同（逐字节相同） |

`outOfRangeSequences` 两侧都是 0：selftest plan 的 LOSS 是 200/s × 10 s、MIX 是 30/s × 10 s，远在序列空间
之内，所以 0.1 的守卫与 0.2 的加载期校验在这条路径上都不被触发（0.1 的回归证据是单测，0.2 的是夹具
与退出码，见 [tier0-evidence.md](./tier0-evidence.md)）。

## 5. 结论

1. **本轮有且只有一个结构性变化**：`run.json.planSource`（D14.23，有意）。没有改名、没有删键。
2. **契约计数零差异**：220 条契约计数/闸门/布尔路径里 215 条逐值相同，余下 5 条全部有出处
   （3 条排队延迟读数 + 2 条在带宽内的吞吐计数），没有一条计数器越过它自己的带宽。
3. 其余 544 条数值差异全部落在"身份类 / 时钟类 / 测量读数类"，按 **D15** 属信息性；**没有为了让它们变绿
   去改任何配置或放宽任何带宽**（`record-normalize.json` 与 `jitter-band.json` 本轮零改动）。
4. 与 A1 的 478 条超带项相比，本轮数量级相同、构成相同（时钟 129 / 身份 33 / 读数 382 条目），
   这是"两次运行的带宽用于第三次运行"的固有极限，不是行为回归；D15 的四类精化按裁定在 B1 落地。
   同一棵树重复跑本比对（本报告前后共跑 3 次，其中 2 次是同一二进制）得到 512 / 546 / 544 条，
   差值本身就是读数抖动的量级；三次的结构性差异都只有 `planSource` 一条。

## 6. check 轮复现（E1-A 第二轮复核）

复现①：用**同一批输入**（`baseline/run1` ↔ `/tmp/a2-run`，A2 二进制 `9d7158…`）重跑 §1 的命令，
输出 **557 行、`summary: structural=1 conditional=0 numeric=544 measured=903`**，与 §2 的表格逐字一致；
第①栏唯一一行仍是 `records/run.json planSource: only in after`。

复现②：check 轮把取消也写成 `error` 记录（D14.12 裁定）之后重新发布，再跑一次 selftest 与 `run1` 比对：

| 运行 | `linux/WinForward.E2E.dll` | structural | conditional | numeric |
|---|---|---|---|---|
| A2 | `9d715829…` | 1（`planSource`） | 0 | 544 |
| check 轮（注释修订前） | `f0755fe2…` | 1（`planSource`） | 0 | 568 |
| check 轮 | `05e8ae77…` | 1（`planSource`） | 0 | 527 |
| check 轮最终树（文本键类型校验后） | `03b040af…` | 1（`planSource`） | 0 | 619（与 format / inspectcode 并行跑，读数抖动偏大） |

复现③（独立实现，不复用 `compare-records.py`）：按 D14.6 的路径字母表自行 flatten 两侧的
11 个 `<arm>.jsonl` + `run.json` + `ledger.jsonl`，逐路径**精确**比对并分四类：

```
paths: base=458 after=459 | only-base=0 only-after=1
  only in after: planSource
per class (paths / differing): clock 14/11, contract-count 96/2, identity 15/0, measurement 164/52, other 169/4
contract-count paths that moved: metrics/frames 4877→4876, metrics/framesEchoed 4877→4876
gates/* paths that moved: gates/inFlightCeilingMs only（读数，非计数）
boolean/enum/status paths that moved: （空）
```

即：**没有改名、没有删键**，96 条契约计数路径里只有 §4 已登记的那两条吞吐计数动了（带宽 1，带内）；
`gates.*` 里动的只有 `inFlightCeilingMs` 这条读数；布尔/枚举/状态零变化。与 §4/§5 的结论一致。

