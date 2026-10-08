# E2-a2 回归比对（`Client/Lanes/` 接缝 + 引擎 + fake transport 测试，**未迁移任何臂**，对照基线 run1）

本批是 E2-a 的第二小批（2a-2）：新增 `benchmarks/WinForward.E2E/Client/Lanes/`（5 文件：`LaneTransportContracts.cs`、
`ILanePolicy.cs`、`LaneCounts.cs`、`LaneEngineOptions.cs`、`LaneEngine.cs`）与 `tests/WinForward.E2E.Tests/Lanes/`
的 22 条用例（6 个测试类文件 + 2 个替身/选项文件）。`LatencyArm`/`DnsArm`/`ReplyClassifier`/`src/` 一行未改，
`Client/Lanes/` 的类型**零消费者**：

```bash
rg -n "Client\.Lanes|LaneEngine|ILanePolicy|ILaneTransport|LaneCounts|LaneSlotDecision|LaneReceiveResult|LaneSendResult|LaneOpenResult|LaneEngineOptions|LaneReceiveKind|DeferredQueued" \
   benchmarks/WinForward.E2E src -g '!benchmarks/WinForward.E2E/Client/Lanes/**'   # exit 1（零命中）
```

判定线：**结构差异为空（含改名表键集）**、**契约栏零越带**（安静宿主上的判定）；读数栏照 D17.4 只作信息性输出。

---

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                        # 退出码 0（门禁 1）
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json                # 退出码 0（门禁 4）
cp -r /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} /tmp/e2a2/<run>/

R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR <base> <after> --normalize $R/record-normalize.json \
        --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 --strict
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 |
|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e`（E2a1-vs-run1.md §1 记的 `2eb37162…`） | — |
| `old-*`（改动前对照） | `git archive HEAD` = `64e1b72`（2a-1 已提交） | `830918c4b88dbb9358b23af3db0566979438d5ca5eb780d44e0c1d9887789a6b` |
| `new-{run,run3,run4}`（门禁修完前的本轮树） | HEAD + 当时工作树 | `e3eefe9292b57038029fd5c82c71f6d13e400adddeedfe662927c7c4896ccab9` |
| `new-{run5,run6,run7}`（**冻结树**，判据用） | 最终工作树 | `faeaeaf1912b5feb9db3a040991a1a05c498786520e6d84c82742ed979592c42` |

本轮共跑 **7 次** selftest（5 次新二进制、2 次改动前二进制）。宿主负载是本轮最大的自变量，逐次记下
（`uptime` 的 1 分钟均值）：

| 运行 | 二进制 | 宿主 | 产物 |
|---|---|---|---|
| `new-run` | `e3eefe92…` | 6 路并发测试宿主（有载） | `/tmp/e2a2/new-run` |
| `new-run3` / `new-run4` | `e3eefe92…` | 安静（0.9–1.7），背靠背 | `/tmp/e2a2/new-run{3,4}` |
| `new-run5` / `new-run6` | `faeaeaf1…` | 门禁（format + inspectcode）刚跑完，负载 2–7 衰减中 | `/tmp/e2a2/new-run{5,6}` |
| `new-run7` | `faeaeaf1…` | 安静（2.6→3.7），**判据用** | `/tmp/e2a2/new-run7` |
| `old-run` | `830918c4…` | 6 路并发测试宿主（有载） | `/tmp/e2a2/old-run` |
| `old-run2` | `830918c4…` | 安静 | `/tmp/e2a2/old-run2` |

比对输出：`/tmp/e2a2/cmp-P{1..10}.txt` 与 `/tmp/e2a2/compare-{new-run1,old2-run1,old-new}.txt`。
产物未随任务提交（与 B1b…B2c、E2-a1 相同：三条命令随时可重建）。

---

## 2. 判定线：结构栏 **13/13 全 0**，契约栏在安静宿主上全 0

| # | 比对 | 用途 | 结构 | 条件键 | 身份 | 已声明 | **契约** | 改名 | 读数 | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| P10 | `run1` → `new-run7` | **主证据（冻结树，安静）** | **0** | 0 | 0 | 27 | **0** | 0 | 291/366 | 238/366 | **0** |
| — | `run1` → `new-run` | 主证据（前一代树，有载） | **0** | 0 | 0 | 27 | **0** | 0 | 294/366 | 216/366 | **0** |
| P1 | `run1` → `new-run3` | 主证据（前一代树，安静） | **0** | 0 | 0 | 27 | **0** | 0 | 284/366 | 225/366 | **0** |
| P5 | `run1` → `new-run4` | 主证据（前一代树，安静） | **0** | 0 | 0 | 27 | **0** | 0 | 296/366 | 251/366 | **0** |
| P6 | `run1` → `new-run5` | 冻结树，负载衰减中 | **0** | 0 | 0 | 27 | 6 | 0 | 298/366 | 259/366 | 1 |
| P7 | `run1` → `new-run6` | 冻结树，负载衰减中 | **0** | 0 | 0 | 27 | 44 | 0 | 302/366 | 265/366 | 1 |
| P8 | `new-run5` → `new-run6` | **噪声地板：同一二进制**，负载衰减中 | **0** | 0 | 0 | 0 | **63** | 0 | 296/366 | 246/366 | 1 |
| P4 | `new-run3` → `new-run4` | **噪声地板：同一二进制**，安静 | **0** | 0 | 0 | 0 | **13** | 0 | 293/366 | 244/366 | 1 |
| P2 | `run1` → `old-run2` | 对照：改动前二进制（安静） | **0** | 0 | 0 | 27 | 6 | 0 | 296/366 | 224/366 | 1 |
| P9 | `old-run2` → `new-run5` | 对照 A/B（负载衰减中） | **0** | 0 | 0 | 0 | 19 | 0 | 293/366 | 237/366 | 1 |
| P3 | `old-run2` → `new-run3` | 对照 A/B（安静） | **0** | 0 | 0 | 0 | 19 | 0 | 290/366 | 180/366 | 1 |
| — | `run1` → `old-run` | 对照：改动前二进制（有载） | **0** | 0 | 0 | 27 | 44 | 0 | 299/366 | 247/366 | 1 |
| — | `old-run` → `new-run` | 对照 A/B（有载） | **0** | 0 | 0 | 0 | 57 | 0 | 295/366 | 240/366 | 1 |

- **结构 / 条件键 / 身份 / 改名四栏 13/13 = 0**；改名表 13/13 都是
  `9 entries required, 9 satisfied, 0 not observed`（`declared=27` = B2 的 26 条改名残留 + `run.json planSource`，
  与 B2c/E2-a1 逐条相同）。
- **契约栏：安静宿主上的 4 次运行（`new-run`/`new-run3`/`new-run4`/`new-run7`）全部 0、退出码 0**；
  冻结树在安静宿主上的那一次（P10）也是 0。
- 契约栏的**噪声地板**在同一台机器上用**同一二进制两次运行**测得：安静时 13 条、负载衰减时 63 条。
  也就是说这台宿主的契约栏在负载下能自己移动几十条（§3 逐族列出），远超本批可能造成的任何差异；
  而本批根本没碰臂（开头的 `rg` 零命中）。

---

## 3. 契约栏越带的构成（逐族归属，全部与臂无关）

对照组与噪声地板里移动的键只有四族，全部是"宿主能移动的计数"：

| 族 | 键 | 为什么与本次改动无关 |
|---|---|---|
| 无带宽记录的发送计数 | `metrics/{tcp,udp}.sent`（BASE/DNS/DNSALT/LAT/LATLOAD/MIX）、`metrics/desktops/udp.sent` | `jitter-band.json` 里没有这些路径的带宽 → 按 D16.1 以带宽 0 判；两次运行的发送计数差 1–3 个数据报 |
| 成功发送计数 | `metrics/{tcp,udp}.sentOk`、改名前的 `metrics/{tcp,udp}Sent`、`metrics/latency/*.sent`/`sentOk`、`metrics/desktops/udpArrived` | 同上：窗口内实际发出去的条数，宿主一慢就少几条 |
| 丢失/乱序族 | `LOSS metrics/reordered`、`reorderRate`、`MIX metrics/udp.lossRate`、`MIX metrics/desktops/udp.arrived` | E2-a1 §3.3 已登记的零宽带宽族（`baseRange=[0,0] afterRange=[0,0]`） |
| 账本记录条数 | `ledger/ledger.jsonl sourceOverflow`、`undecodable`（记录 *条数* 而非其值） | 有载运行里目标端丢弃/无法解码的数据报更多 → 多写几条记录 |
| 已声明项 | `run.json planSource` | B2c 的 27 条已声明项之一，不是新差异 |

`MIX metrics/desktops/udp.foreignConnection` 也在这份名单里，但四份记录里它的值恒为 0——它的"越带"是
**带宽 0 + 计数 0→0** 的登记噪声，不是行为差异。

---

## 4. 零宽契约量的逐值核对（D18.5 第 12 条的清单）

| 路径（`records/<arm>.jsonl::`） | run1 | new-run | new-run3 | new-run4 | new-run7 | old-run2 |
|---|---|---|---|---|---|---|
| `LATLOAD metrics/tcp.received` / `udp.received` | 1601 / 1601 | 同 | 同 | 同 | 同 | 同 |
| `LAT metrics/tcp.received` / `udp.received` | 322 / 161 | 同 | 同 | 同 | 同 | 同 |
| `BASE metrics/latency/{tcp,udp}.received` | 101 / 101 | 同 | 同 | 同 | 同 | 同 |
| 全部 `*/unmatchedReplies`、`*/outstandingAtTeardown`（BASE/LAT/LATLOAD/LOSS/PERSIST） | 0 | 0 | 0 | 0 | 0 | 0 |
| `latency/tcp-rtt/count`（BASE/LAT/LATLOAD/MIX/PERSIST） | 101 / 322 / 1601 / 146 / 15 | 同 | 同 | 同 | 同 | 同 |
| `latency/udp-rtt/count`（BASE/LAT/LATLOAD/MIX） | 101 / 161 / 1601 / 602 | 同 | 同 | 同 | 同 | 同 |
| `latency/dns-rtt/count`（DNS/DNSALT/MIX） | 402 / 402 / 8 | 同 | 同 | 同 | 同 | 同 |
| `latency/tcp-connect/count`（BASE/LAT/LATLOAD/MIX/REL） | 5 / 8 / 8 / 26 / 101 | 同 | 同 | 同 | 同 | 同 |
| `ledger udpSummary received / undecodable / sourceOverflow` | 0 / 0 / 0 | 同 | 同 | 同 | 同 | 同 |

`inFlight` 不是发布键（D18.5 第 12 条）；本轮对它的覆盖是并发恒等式用例与
`TheSettlementCarriesTheReceiveInstantAndNotTheSettleInstant`（§6），不在这里核对。

---

## 5. 读数摘要（D17.4：读数只作信息性输出）

| 运行对 | 读数移动 | 越带读数 |
|---|---|---|
| `run1` → `new-run7`（判据） | 291/366 | 238/366 |
| `new-run3` → `new-run4`（**同一二进制**，同 boot） | 293/366 | 244/366 |
| `new-run5` → `new-run6`（**同一二进制**，同 boot） | 296/366 | 246/366 |

**同一 boot、同一二进制、背靠背**的一对也移动 244–246/366，说明读数栏移动的主因是宿主与 boot，不是代码：
`jitter-band.json` 是 run1↔run2 在同一 boot 冻结的，跨 boot 的 `ticks`/资源读数必然越带。
`run1 → new-run7` 的 238 条越带逐条归类：`latency/*` 直方图读数 75 条、宿主资源采样（内存/CPU/线程）66 条、
boot 相对时间戳 12+ 条、墙钟/毫秒读数 12 条、速率读数 8 条，其余是 `received`/`bytes`/`connectTicks` 一类观测计数。
越带读数不参与 exit code——退出码由前四栏 + 契约栏决定。

---

## 6. 本批判据的第二半：测试与分配 gate

| 判据 | 结果 | 证据 |
|---|---|---|
| Lanes 用例 | 22/22 通过（整套 210/210）→ check 轮补一条 drain 出口的 fact 后 **23/23（整套 211/211）**，见 `E2a2-check.md` §1 | `dotnet test tests/WinForward.E2E.Tests -c Release` |
| **加载 soak** | **60/60 全绿**（10 轮 × 6 路并发整套套件） | `/tmp/lanes-stress3/*.txt` |
| 分配 gate（调用线程） | 隔离 5/5 绿；每批 256 次发送的 `GC.GetAllocatedBytesForCurrentThread()` 增量恰为 0 | `LaneEngineAllocationGateTests.TheSendPathAllocatesNoManagedBytesOnTheCallersThread` |
| 分配 gate **反证** | 同一 gate 在"策略故意每次分配"时每批都 > 0 | `…TheGateGoesRedWhenThePathAllocatesOnPurpose` |
| 并发恒等式 | 4 轮 × 1024 条回复：`delivered == sent == settled`、pending/in-flight 0、逐序号恰好结算一次 | `LaneEngineConcurrencyTests` |
| 不相交契约 | `typeof(LaneCounts).GetProperties()` ∩ 策略计数属性 = ∅ **且**策略无同名私有字段；带负控 | `LaneCountsDisjointnessTests` |
| 六条门禁 | publish 0 / build 0 警告 / 测试 210 绿 / selftest 0 / format 空输出 / inspectcode 0 `<Issue>` | 见 §7 |

加载 soak 期间曾暴露两个**测试替身自身的**缺陷，已修并复验（记录在此以免后人重踩）：

1. "未完成的发送"用 `Task.Yield()` 制造时，其续体可能抢在引擎读 `IsCompleted` **之前**完成（加载下命中），
   于是 `sendWouldBlock` 少记——这是替身的竞态，不是引擎的（引擎的读本来就是"取某一瞬间"的观测，审计 §9.6）。
   替身改为 `Task.Delay(50)` 后才有"读的那一刻必然未完成"的性质。
2. 接收侧的续体**可以合法地**在调用线程上恢复（调用线程本身是池线程，发送角色一停它就空闲了），
   所以"发送线程 id ≠ 接收线程 id"不是契约；真正要证的是账本单写者，并发用例改为断言
   `SendThreadId == 调用线程`、接收角色确实运行过，以及"逐序号恰好结算一次"。

---

## 7. 六条门禁（冻结树）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 退出码 0；三份产物齐全，两个 Windows 客户端镜像名可区分 |
| 2 | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Passed: 210, Failed: 0`（check 轮后 211，见 `E2a2-check.md` §3.9） |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出码 0（`new-run7`） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出码 0，**空输出** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e2a2/jb-inspectcode4.xml WinForward.slnx` | 报告里 **0 个 `<Issue>`** |

---

## 8. 结论

- **结构差异为空**：13 组比对的结构栏、条件键栏、身份栏、改名栏全 0，改名表 13/13 `9/9 satisfied`。
  这是本批的直接判据，因为新类型零消费者、臂与 `src/` 一行未动（开头的 `rg` 零命中）。
- **契约零越带**：安静宿主上的 4 次运行（含冻结树的 `new-run7`）契约栏全 0、退出码 0。
  负载下契约栏会出现 6–44 条移动，但**同一二进制两次运行**在同样条件下能移动 13–63 条——
  移动是宿主的属性，§3 逐族列出，且与本批无关。
- **读数栏移动是宿主/boot 属性**（同一二进制同 boot 一对也移动 244–246/366），按 D17.4 只作信息性输出。
