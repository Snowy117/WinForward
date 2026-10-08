# B1c 回归比对（D15 四类判定 vs 基线 run1）

本轮只动工具链（`compare-records.py` 的四类判定、`jsonl_paths.require_python3`、`contract-inventory.py` 的
`batch` 列、`record-normalize.json` 的 version 3），以及 `Client/PlanFile.cs` 一处**错误文本**（越界数值键不再
报 "is not an integer"，同时把 `100.0` 这类整数值小数的接受路径原样保住）加对应测试。记录形状、字段名、算术一行未改，所以判定线是：**结构差异只剩已登记的
`planSource`；契约类 0 越带；身份类 0 finding；读数类只作信息性输出**。

`run1`/`run2`/`jitter-band.json` 未重新生成（`git status` 里无改动），因此不需要 `run1b/`。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                     # 强制重建三份产物，退出码 0
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json             # 退出码 0
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/b1c-run/
R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR $R/baseline/run1 /tmp/b1c-run --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
        --strict --json-out /tmp/b1c/findings-b1c.json          # 退出码 1（唯一一条结构项，见 §2）
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 | `linux/WinForward.E2E.Contracts.dll` sha256 |
|---|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e` | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` | 无此文件 |
| `b1c`（本轮） | HEAD `4f83aa3` + 本轮未提交工作树 | `9f40a649a032bf6f4f57beee083b6ae388a004e3a6fb418d13d25bf32d0873d6` | `a37269ee53b69ea708d9d4b3f4fb7572ef2c0131c05889ef8b94c271bf259460` |

本轮的 selftest 产物留在 `/tmp/b1c-run/`，未随任务提交（与 B1b 相同：run1/run2 是基线定义，本轮是回归检查，
四条命令随时可重建）。

## 2. 三次比对的四类计数

| 比对 | 结构 | 条件键 | 身份 | 契约 | 已声明 | 读数（信息性） | 退出码 |
|---|---|---|---|---|---|---|---|
| `run1 vs run1` | **0** | **0** | **0** | **0** | 0 | 0/349 移动 | **0** |
| `run1 vs run2 --band` | **0** | **0** | **0** | **0** | 0 | 284/349 移动 | **0** |
| `run1 vs /tmp/b1c-run --band` | **1**（`records/run.json planSource: only in after`） | **0** | **0** | **0** | 0 | 296/349 移动 | 1 |
| `run1 vs /tmp/b1c-run --band --rename-table` | **0** | 0 | 0 | 0 | **1**（同一条 `planSource`，被改名表登记为 `added`） | 296/349 | **0** |

三次比对都覆盖 **1173** 条两侧都存在的路径，其中 891 条是数值路径（契约 542 + 读数 349，即 `measured`），
另有 12 个身份数值对只做存在/类型检查（`classes(... identity=12)`）。

- `run1 vs run1` 四类全空、读数 0 条移动——判定器不会自己制造差异。
- `run1 vs run2 --band` 四类全空：**逐键带宽刚好覆盖它自己那一对**，而身份类把身份值、读数类把时钟与读数
  的位移移出失败面（A0 的旧脚本输出 `compare-run1-run2.txt` 在同一对上是 `numeric=0 measured=903`，
  即 903 条数值路径全在"数值栏"里，正是 D15 要精化的前提）。
- `run1 vs b1c`（第三次运行）**契约类 0 越带**：542 条契约路径在三次独立运行之间逐位相同。
- 唯一的结构差异是 A2 引入、D15/rename 表登记的 `planSource`；带 `--rename-table` 时它落到
  `declared`（不算失败），不带表时按 D15"结构差异必须命中改名表"报结构失败——两种行为都记录在案。

记录集本身也逐文件核过（比对范围之外的独立证据）：12 个 JSONL 文件（11 臂 + `run.json`）与账本的行数与 run1
**逐一对齐**（`IDLE 7`、`LAT/LATLOAD/DNS/DNSALT/THRU 10`、`LOSS/MIX/PERSIST/BASE 12`、`REL 18`、`ledger 258`），
所以 `allowedCountDelta` 默认 0 没有触发任何 `count` finding。

`targetSummary`（45 个叶子）**42 个逐位相同**，只有三个时钟不同（`utc`/`startedTicks`/`endedTicks`，整轮前移
1.5173×10¹³ ticks ≈ 4.21 小时）；
整轮总量不变：`udp.received/bytes = 6967/1419144`、`tcp.connections = 157`、`tcp.verdicts` 8 项、
`dns.*`/`dnsAlt.*` 各 12 项、`ledgerWriteErrors = 0`。`planSource = "file"`（本轮 selftest 走 `--plan`）。

## 3. 越带项逐条归类（`run1 vs b1c`）

`--json-out` 的 `measurements` 里，**契约类 542 对越带 0 条**；越带全部落在读数类（203 对 / 349 对），
按"选中它的那条规则"分组如下（`--explain-classes` 的分类规则名）：

| 读数子类（规则） | 越带对数 | 语义 |
|---|---|---|
| `*Us` | 65 | 延迟直方图读数（`minUs`/`maxUs`/`meanUs`/`p50..p999`） |
| `*Ticks` + `*ticks` + `wallSeconds` + `*elapsedSeconds` | 45 | 单调时钟：运行发生在 4.21 小时后（`*Ticks` 位移恒为 1.51726×10¹³，`Stopwatch.Frequency` = 10⁹） |
| `cpuSeconds` / `generatorCpuSeconds` / `*/cpuSeconds` | 30 | CPU 时间 |
| `peakWorkingSetBytes` / `workingSetBytes` / `privateBytes`（含 `*/` 与 `env` 变体） | 37 | 进程内存采样 |
| `metrics/*Ms` | 8 | `meanConnectMs`/`meanTransferMs`/`windowCeilingMs` |
| `*achievedRate` / `*goodputBps` / `*goodputMbps` | 9 | 吞吐 |
| `threads` | 4 | 进程线程数 |
| 账本每秒曲线（`received`/`bytes`/`sources/datagrams`） | 3 | 见下 |
| `classOverrides[gates/inFlightCeilingMs]` | 2 | **跨类键按 D15 第 6 条归读数**：`LAT`/`LATLOAD` 的 `gates.inFlightCeilingMs` 越带（`BASE` 那条位移 0.471 ms 正好等于其带宽） |

（65 + 45 + 30 + 37 + 8 + 9 + 4 + 3 + 2 = **203**，分组规则名来自 `--explain-classes`。B1b 手工分解时
读数类越带 115 对，本轮的 203 对与它不同口径也不同运行——**这恰好是 D15 的论点**：两次运行冻结出来的带宽
覆盖不了第三次运行，读数越带的对数本身就随运行漂移。）

`gates/inFlightCeilingMs` 出现在这份表里正是 D15 第 6 条要的效果：它按形状是 `gates.*`，按语义是毫秒读数；
配置里的 `classOverrides` 把它显式归读数（`--explain-classes` 显示规则名就是 `classOverrides[...]`，
不是某条 `*Ms` 模式的巧合）：`LAT` 203821.7 ms → 203781.1 ms（|Δ| 40.6 ms，带宽 15.2 ms）、`LATLOAD`
|Δ| 0.307 ms（带宽 0）两条越带，`BASE` 的 |Δ| 0.471 ms 正好等于其带宽——三条都只作信息性报告，不进退出码。

账本每秒曲线那 3 条与 B1b §3.1 同源：`sources[]` 按（地址，**临时端口**）成行，行数每次运行不同，
`received`（均值 2844.56 → 2842.28，带宽 0.887）与 `bytes`（529701 → 529235，带宽 173.5）是同一批数据报的
每秒到达曲线形状差异，`sources/datagrams` 的带宽为 0 而（同一行的）行集合变了；**整轮总量逐位相同**
（6967 个数据报 / 1419144 字节）。注意 `metrics/*Bytes`（THRU 转移量、REL 的回显字节）在本轮 0 条越带。

读数类 349 对的构成：**53 对逐位相同、93 对在带宽内移动、203 对越带**——全部只出现在信息性小节；
不加 `--strict` 时连路径名都不打印，只有一行计数。

## 4. 配对矩阵（identity / 契约的边界核对）

- 冻结带宽 903 条里 **296 条带宽非零**：**284 条读数 + 12 条身份，0 条契约**。
- 542 个契约对的记录带宽**全部为 0**：契约类"必须落在带宽内"在当前基线上等价于"必须逐位相同"，
  这正是 D15 说的"契约计数器带宽为 0"。
- 12 个身份数值对 = `*/pid` 11 + `sources/port` 1；它们的值在三次运行间全变，类型全是 number，
  身份类因此 0 finding（负向自检 ② 把 `pid` 改动放大到带宽的 138 倍仍为 0）。

## 5. 改名表判定：现状结论（B2 的判据）

工具：`compare-records.py --rename-table contract-rename.json [--batch B2]`。规则四条：

1. **每个差集项必须命中一条**：只在 base 出现的路径必须是某条 `renamed.old_path` 或 `removed.old_path`；
   只在 after 出现的必须是某条 `renamed.new_path` 或 `added.new_path`，否则报错退出 1；
2. **`--batch B2` 的条目必须恰好出现**：`renamed` 必须"旧拼写消失 **且** 新拼写出现"，否则报错；
3. 条目状态一律打印（`landed`/`pending`/`vanished`），`pending` 里还标出"目标拼写本来就存在"的收敛条目；
4. 同一文件同时出现新旧拼写（一个 writer 两个名字）→ 报错。

现状（`run1 vs /tmp/b1c-run`）：

```
== rename table check ==
  table: 592 entries = added 3, identical 580, renamed 9
  path sets: base 589, after 590; only in base 0 (hit 0), only in after 1 (hit 1)
  renamed observed: landed 0, pending 9 (1 of them already publishing the target spelling), vanished 0
  added observed: 1, not observed 2
  executed batch: B2; 9 entries required, 0 satisfied, 9 not observed
  pending: metrics/desktops/udpArrived -> metrics/desktops/udp.arrived, …（9 条，逐条列出）
  declared additions not observed: detail, error
summary: structural=0 conditional=0 identity=0 declared=1 contract=0 rename=9 readings=296/349 compared=1173 measured=891 classes(contract=542 reading=349 identity=12)
```

**结论：成立。** 本轮不改名，所以 9 条 `renamed`（均标 `batch: "B2"`）全部是 `pending`，
`--batch B2` 逐条报"declared executed in batch B2 but the rename is pending"并退出 **1**。
`added` 的 `planSource` **命中**（`only in after 1 (hit 1)`、`added observed: 1`）；另外两条登记新增
`error`/`detail` 未观测是预期的（绿 run 不产生 `error` 记录），只列不报错。

两个方向性自检（证明这 9 条不是工具缺陷）：

- **合成"B2 已落地"**：把 9 条旧拼写按表改名后作为 after（`/tmp/b1c/b2-landed`），断言变成
  `landed 9, pending 0, vanished 0`、`executed batch B2: 9 required, 9 satisfied, 0 not observed`，
  退出 **0**；差集是 `only in base 9 (hit 9)`、`only in after 8 (hit 8)`——**9 条旧 → 8 条新**，
  因为 `metrics/udp.sentOk` 与 `metrics/udpSent` 收敛到同一个 `metrics/udp.sent`。
- **未登记的新路径**：往 IDLE 的 result 里塞 `metrics.brandNewCounter`，判定报
  `metrics/brandNewCounter: only in after and not declared by any entry`，退出 **1**。

`contract-rename.json` 本轮新增的只有 9 个 `"batch": "B2"` 字段（与 B1b 版本逐行 diff：9 行新增，其余不动），
`contract-rename.md` 的 Renamed 四张族表多了 batch 列。生成器 `contract-inventory.py rename` 重跑两次输出
**逐字节相同**（json 与 md 都验过）；`contract-inventory.json` 与 B1b 版本只差 provenance 的 `run` 路径
（`/tmp/b1b-run` → `/tmp/b1c-run`），590 条 distinct path 不变。

## 6. 六条门禁

| # | 命令 | 观测 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 退出 0；三份产物重建（见 §1 的 DLL sha256） |
| 2 | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 167` |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出 0（本轮 B1c 产物的来源） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0，**输出 0 字节** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | 报告 `<Report>` 下 **0 条 `<Issue>`**（解析 XML，不看退出码） |

补记：门禁 1 在 selftest 之后又跑了一次，两份 DLL 的 sha256 与 §1 表里**逐位相同**
（`9f40a649…` / `a37269ee…`），即交付的产物就是产出 B1c 证据的那一份；门禁 3 额外跑了全 solution
`dotnet test WinForward.slnx -c Release --no-build`，14 个测试工程全绿（E2E 167 passed）。

`PlanFile.cs` 的文本改动同步了两处既有断言（`dns-port-out-of-range`、`negative-rate` 的 `outside` →
`which is outside`），并新增两个夹具与两条用例：`Fixtures/plans/beyond-int-window.json`
（`"window": 3000000000`）断言 `'window' is 3000000000, which is outside 0..2147483647`；
`Fixtures/plans/integral-double-window.json`（`"window": 100.0`）断言整数值写成小数仍被接受并落到 100
（旧实现里 `TryGetInt32` 之后那条"double 回退"不允许丢掉，否则 `100.0` 会变成超范围）。

措辞改动同样落在 **D14.19 的退出码通道**上：[tier0-evidence.md](./tier0-evidence.md) §1 的两行观测文本
（`dnsPort:99999`、`ratePerSecond:-1`）已按 B1c 的二进制重测更新，§3 补上两个新夹具，§5 里那条"观察项，未改"
改为指向新增的 §6（该节记了 5 条命令的退出码与首行输出，含 `beyond-int-window` 的 exit 2 与
`integral-double-window` 的 `parameters.inFlightWindow = 100`）。
