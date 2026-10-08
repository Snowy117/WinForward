# T1/T2：账本的 run 归属与源端点普查（2026-10-08）

本轮修 **T1**（`start-targets.sh` 不给靶机 `--label`）、定位 **T2**（源端点普查表打满），并顺带发现
**T7**（两台机器 UTC 时钟差约 3 s）。证据链、原始输出与门禁都在本文；ticket 状态见
[`../tickets.md`](../tickets.md)，语义条目见 [`../semantic-fixes/index.jsonl`](../semantic-fixes/index.jsonl)。

所有实验都在本机重放，不需要 Windows VM；唯一一次 VM 交互是读取它的时钟（T7），只读、不改状态。

---

## 0. 结论

| 项 | 结论 | 判据 |
|---|---|---|
| **T1** | **fixed** | 真实 E5-b 数据重跑：`ledger-endpoint-overlap` 10 → **0**、`ledger-window-ambiguous` 60 → **0**；本机双车道复现：同一棵树 20 条 findings → 3 条（余下 2 条 caveat + 1 条 informational 全是 fixture 自身的形状） |
| **T2** | **located（未修）** | 根因 = 每个接收循环一张 **64 槽**、按 (address, port) 记账、**终身不回收**的普查表；E5-b 轮次里它装下了 **64 个**源端点，`2026-10-08T10:45:58Z` 之后每个数据报都只进 `sourceOverflow`。最小复现：`--udp-receivers 1` 时恰好 64 个端点入表、第 65 个起全部 overflow；三波新端口不再有任何新端点入表 |
| **T7**（本轮新发现） | **observation** | VM 的 `Get-Date` 比主机 `date -u` **快 2.5–3.2 s**（当场实测）；把账本 `utc` 整体 +3.0 s 再分析，`ledger-connection-mismatch` 90 → 13、`ledger-datagram-mismatch` 102 → 33。这是 T1 修完后残余 caveat 的主因，不是产品差异 |
| **AC18 判据 4 可达性** | 仍未可达，但缺口已全部落到 ticket | T1 修完 + 时钟对齐后，账本族只剩 43 条 `ledger-source-overflow` 与 33 条落在 overflow 窗口里的 `ledger-datagram-mismatch`，**全部**是 T2；13 条 TCP `ledger-connection-mismatch`、5 条 `control-drift-undecided`、2 条 `latency-ceiling-reached` 是已定义披露 |

改动落在 **3 个入库文件**（`git status` 可见）+ **2 个被 `.gitignore` 排除的本机文件**（见 §3.1）：

| 文件 | 改动 |
|---|---|
| `benchmarks/WinForward.E2E.Analysis/Findings/LedgerViews.cs` | 归属改成两步：先按账本自报的靶机实例选账本（`run.json.target` ↔ 账本 label），再按臂的 UTC 窗口定界；窗口重叠只在**共享账本**的 run 之间才算歧义 |
| `benchmarks/WinForward.E2E/README.md`、`benchmarks/WinForward.E2E.Analysis/README.md` | `--label` 的两种含义、归属规则、时钟前提 |
| `benchmarks/WinForward.E2E/scripts/start-targets.sh`（**gitignore**）、`AGENTS.local.md`（**gitignore**） | 两个实例各带 `--label target:<port>`；本机说明 |

没有碰 `src/`、没有碰 `verification/` 的冻结物、没有 commit。

---

## 1. 机制：哪个 run ↔ 哪个靶机实例 ↔ 哪个 label

### 1.1 表（E5-b 轮次的实际形状，pass1 与 pass2 同形）

| 目标实例 | 监听 | 账本文件 | 该实例服务的 run（每个 pass） | 客户端 `--label` | 账本记录里的 `label` |
|---|---|---|---|---|---|
| **A**（代理车道） | `192.168.100.4:40010` TCP+UDP、dns 53 + 40053 | `ledger-main.jsonl` | `control-pre`、7 个产品行各自的 run、5 个 dual 行的 **proxied** 车道 = **14 个 run** | `control-pre-p1`、`wf-aot-opt-p1`、…、`wf-aot-opt-dual-proxied` | `""`（启动器没传 `--label`） |
| **B**（直连车道） | `192.168.100.4:40011` TCP+UDP、dns 40054 | `ledger-direct.jsonl` | 5 个 dual 行的 **direct** 车道 = **5 个 run** | `wf-aot-opt-dual-direct`、… | `""` |

装载后分析器看到的 38 个 run（2 pass × (9 行 + 5 行 × 2 车道)）：

```bash
$ python3 /tmp/wf-dual/window_overlap.py /tmp/wf-bench/e5b-campaign/raw
38 run(s) loaded from /tmp/wf-bench/e5b-campaign/raw
  pass1 port 40010: 14 run(s), 91 pair(s) checked
  pass1 port 40011: 5 run(s), 10 pair(s) checked
  pass2 port 40010: 14 run(s), 91 pair(s) checked
  pass2 port 40011: 5 run(s), 10 pair(s) checked

pairs of runs sharing one target instance that overlap: 0
pairs of runs on different instances that overlap:      10 (each is a dual phase's two lanes, which is the shape the label has to separate)
```

两个事实决定了修法，两个都由上面这张表和这段输出钉住：

1. **一个实例服务多个 run，单一 label 本质上无法命名 run。** 实例 A 在每个 pass 服务 14 个 run，而
   `--label` 是**目标启动时**写进每条记录的一个字符串，启动一次只写一次。所以"给靶机传它服务的 run
   的 label"在 shipped 拓扑下没有解：不存在一个字符串同时等于 14 个 run 的 label。T1 的原始修法方向
   （"启动器按靶机/实例传 `--label`，分析器用 label 选行"）里，**"标签能对上 run label"这一半做不到**，
   必须说出来。
2. **同一实例内，run 是顺序执行的**（91 + 10 对窗口检查，重叠 0 对）；唯一会重叠的 10 对全部是
   **跨实例**的 dual 两条车道。也就是说：窗口本身完全够用，缺的只是"这条 run 只该读哪个账本"。

### 1.2 分析器原来的判定（为什么缺这一环就崩）

`LedgerViewsBuilder` 原来把一 pass 的所有账本记录**倒进一个池子**，再按"记录 label == run label"选
（`discriminating` 要求 `labels.Count > 1` 且至少一个 label 能对上 run label），否则**只按窗口**选：

```csharp
var discriminating = labels.Count > 1 && labels.Keys.Any(RunLabels(runs).Contains);
...
var selected = records.Where(record => window.Start <= record.Utc && record.Utc <= window.End);
if (discriminating && label is not null) { selected = selected.Where(record => record.label == label); }
var overlap = windows.Where(other => other.Value.Exists(span => 相交));   // 不分账本
Unattributable = overlap.Count > 0 && !discriminating;
```

label 全空 ⇒ `labels.Count == 1` ⇒ `discriminating = false` ⇒ 只剩窗口。于是：

- 每条 dual 车道的每个臂，窗口里既有自己的记录、也有**另一个账本**里同时对跑的另一条车道的记录；
  两条车道的窗口必然相交（它们本来就同时跑）⇒ `ledger-window-ambiguous`。
  5 行 × 3 臂 × 2 车道 × 2 pass = **60** ✓
- 端点分区检查（同一行的 proxied 窗口端点集 vs direct 窗口端点集必须不相交）在两边看到**同一批池子
  记录**时，交集 = 两边端点集的并集 ⇒ `ledger-endpoint-overlap`（correctness-failure）。
  5 行 × 2 pass = **10** ✓

原始输出的两条（`/tmp/wf-bench/e5b-campaign/verdict.json`）：

```text
!! correctness-failure ledger-endpoint-overlap pass1/proxifier  datagram(s) arrived from endpoint(s) used on
   both a proxied and a direct-path window of this run: 192.168.100.2:56972, 192.168.100.2:56973,
   192.168.100.2:56974, 192.168.100.2:56975, 192.168.100.2:56976, 192.168.100.2:56977, 192.168.100.2:56986,
   192.168.100.2:56987; a direct-path arm arriving from a proxied endpoint (or the reverse) means the product
   did not route that traffic where it was configured to
!! measurement-caveat ledger-window-ambiguous pass1/proxifier/proxied LAT  this window overlaps
   proxifier/direct and the ledger carries no per-run label, so its records cannot be attributed to this run
   rather than to the overlapping one; the counts below are the whole overlapping window
```

报出来的端点全是 **VM 自己的临时端口**（两台车道各自的客户端 socket），不是"产品把直连应用抓进了
代理"——同一轮的 sing-box 证据是干净的（5 个 dual 行 `directLeak=0`）。

---

## 2. 修法对比

| 候选 | 做法 | 判定 |
|---|---|---|
| **A（采用）** | 启动器给每个实例 `--label target:<port>`；分析器按 `run.json.target.{tcpPort,udpPort}` 把 run 绑到它声明的实例的账本，再按窗口定界；窗口重叠只在共享账本时算歧义 | **最小且不动既有语义**：per-run label 的路径原样保留（合成树、单 run 靶机、一次跑一个 run 的场景都还走它） |
| L | orchestrator 把每条 run 的 `--label` 改成它所在实例的标签，靶机也这么标——这样**现有**的 `record.label == run.label` 规则直接完成实例绑定，分析器零改动 | 可行但**丢信息**：`run.json.label` 从"这条 run 是谁"变成"它走哪个实例"，§2 的 label 列全是重复值，`RunLoader.Owner()` 靠 label 里的 `-dual-` 找行主的机制在 pass 级 dual 目录布局下失效。否决 |
| H | 让靶机按 run 打 label（客户端在带内注册自己的 label，靶机写进后续记录） | 这是合成树假定的契约，但要让 4 个 listener、每个臂一条新 socket 都注册到，等于改 harness 的线协议；代价远超本轮，且注册前后的记录仍无归属。**记为后续选项**，不在本轮 |
| D | 不碰启动器，分析器用账本自己的 `dnsSummary.port` 反推实例（run 的 `target.dnsPort` 对得上哪个账本） | 技术上能用，但实测会**破坏冻结 oracle**：合成树把两个 dnsSummary 都写进 `ledger-main.jsonl`，proxied 车道（dnsPort 53）会被绑到 main 账本、丢掉 direct 账本里本属于它窗口的记录，§14 的格子随之变化。否决 |
| "声明 dual 相位不参与端点分区检查" | 直接豁免 | 只是把检查删掉，不修归属。否决 |

**A 为什么成立**，对照 T1 的判定条件：

- `labels.Count > 1`：两个实例的 label 是 `target:40010` / `target:40011`，labels 集合大小 2 ✓（而且
  新规则**不依赖**这个数——单实例也照样绑定）。
- "label 能对上 run label"：**这一步换成"label 能对上 run 自己声明的靶机端口"**。`run.json` 里本来就有
  `target.{address,tcpPort,udpPort}`（客户端写的，谁也没动过），账本 label 给出实例的端口（启动器写的），
  两边独立声明、在分析器里汇合。同一个 run 的窗口里因此**不可能**再出现别的实例的记录。
- 重叠只在**共享账本**的 run 之间才算歧义：dual 两条车道落在两个账本里，窗口再重叠也不歧义
  （`SharesLedger` 返回 false）；同一账本上真出现两条重叠 run 时，仍然如实报 `ledger-window-ambiguous`。
- 没有实例 label 的账本（合成树、`selftest.sh` 的 `--label selftest`、任何第三方调用）走的还是原来的
  池子 + 窗口规则，**逐字节不变**（§5 的 oracle 全绿）。

**契约只增不改名**：`--label` 选项名、`run.json`/账本记录的键集合、分析器 CLI 都没变；新增的是
"`target:<port>` 这个取值有约定含义"这一条文档化约定，以及分析器多了一条归属分支。

---

## 3. 本机 before/after

### 3.1 先说一件必须说清楚的事：启动器不入库

`benchmarks/WinForward.E2E/.gitignore` 明确写着 `scripts/start-targets.sh` 是本机胶水（"these are not
[committed], because they describe one particular pair of machines"）。所以 T1 的修法必然分成两半：

- **入库的一半**：分析器规则（`LedgerViews.cs`）+ 约定文档（两个 README、`AGENTS.local.md`）；
- **不入库的一半**：本机 `start-targets.sh` 的两个 `--label`（已改，见下），下一轮 campaign 必须用改过的这份。

这也解释了 T1 为什么一直没被发现：`selftest.sh`（入库）给自己的靶机传了 `--label selftest`，而
`start-targets.sh`（不入库）没有。

```bash
# benchmarks/WinForward.E2E/scripts/start-targets.sh（本机，gitignore）
"$binary" target --bind "$bind" --tcp-port 40010 --udp-port 40010 --dns-port 53 --dns-alt-port 40053 \
    --label target:40010 --ledger "$ledgers/ledger-main.jsonl" &
"$binary" target --bind "$bind" --tcp-port 40011 --udp-port 40011 --dns-port 40054 \
    --label target:40011 --ledger "$ledgers/ledger-direct.jsonl" &
```

### 3.2 shipped selftest（harness 能用性检查）

```bash
$ bash benchmarks/WinForward.E2E/scripts/selftest.sh plans/dual-plan.json
e2e client: arm LAT (latency) starting
e2e client: arm LAT finished in 40.0s
e2e client: arm MIX (mix) starting
e2e client: arm MIX finished in 40.1s
e2e client: arm LOSS (loss) starting
e2e client: arm LOSS finished in 40.2s
e2e client: 3 arm(s) written to /tmp/wf-bench/selftest/out
...
$ python3 -c "…读 /tmp/wf-bench/selftest/ledger.jsonl…"
selftest ledger records: 221 labels: {'selftest': 221}
targetSummary.udp: {"udpReceivers": 8, "received": 16004, "undecodable": 0, "bytes": 3392688, "sendErrors": 0}
```

（`selftest.sh` 的靶机与客户端用同一个 label `selftest`，单 run，不触发任何归属分支——这正是"原路径
不变"的一手证据。把它的产物按 flat 树跑一遍分析器，归属文字仍是原来那一句，findings 只剩 flat 树固有
的 `control-block-missing`：）

```bash
$ bash …/analyze.sh --raw /tmp/wf-bench/selftest/out --flat --ledger /tmp/wf-bench/selftest/ledger.jsonl --out /tmp/wf-dual/selftest-flat
e2e-analysis: flat ledger(s): /tmp/wf-bench/selftest/ledger.jsonl
findings_by_severity: {"correctness-failure": 0, "path-interference": 0, "harness-error": 0, "measurement-caveat": 1, "informational": 0}
  measurement-caveat   control-block-missing        1
attribution: 1 ledger(s); the arm's UTC window only, because the ledger carries one label, so a record's own label cannot select a row)
```

### 3.3 自造的双车道 fixture（本机复现 dual 相位）

脚本 `/tmp/wf-dual/run.sh`（不入库，仅在 `/tmp`）：

- 起两个靶机实例（`127.0.0.1:41010` + dns 5310/5311、`127.0.0.1:41011` + dns 5312），
  `unlabelled` 模式**不带** `--label`（复现 T1），`labelled` 模式带 `--label target:<port>`；
- 跑 row 自己的 run（`--label wf-aot-opt-p1`，走代理靶机）；
- **同时**起两条车道：`wf-aot-opt-dual-proxied` → 41010、`wf-aot-opt-dual-direct` → 41011，
  各自用同一份最小 dual plan（LAT 8 s、MIX 8 s、LOSS 8 s），这是窗口重叠的根源；
- 组装成分析器的 campaign 形状（`raw/pass1/wf-aot-opt/{run.json,…}`、`…/dual/{proxied,direct}/`、
  `order.txt`、`environment.json`、`proxy-truth.json`），收尾时 SIGTERM 两个靶机让汇总落盘；
- 按行 id 用 `wf-aot-opt`（`RowProfiles` 里的声明行，UDP 走代理路径），车道 run id 由 label 里的
  `-dual-` 命名成 `wf-aot-opt/proxied` / `wf-aot-opt/direct`。

```bash
$ /tmp/wf-dual/run.sh unlabelled /tmp/wf-dual/out-unlabelled
$ /tmp/wf-dual/run.sh labelled   /tmp/wf-dual/out-labelled
# 第三份：把 labelled 那份的账本记录按 run 窗口改写成 per-run label（合成树假定的契约），做等价性交叉检查
$ python3  …改 label…  && bash …/analyze.sh --raw /tmp/wf-dual/out-perrun/raw --out /tmp/wf-dual/out-perrun
```

**计数对比**（`/tmp/wf-dual/logs/local-comparison.out`）：

```text
finding (severity/kind)                        unlabelled target:label per-run label
correctness-failure/ledger-endpoint-overlap             1          0          0
informational/udp53-direct                              1          1          1
measurement-caveat/control-block-missing                1          1          1
measurement-caveat/declared-arm-missing                 1          1          1
measurement-caveat/ledger-connection-mismatch           4          0          0
measurement-caveat/ledger-datagram-mismatch             6          0          0
measurement-caveat/ledger-window-ambiguous              6          0          0
TOTAL                                                  20          3          3

unlabelled (what start-targets.sh shipped)     {"correctness-failure": 1, "path-interference": 0, "harness-error": 0, "measurement-caveat": 18, "informational": 1}
labelled target:<port> (this round)            {"correctness-failure": 0, "path-interference": 0, "harness-error": 0, "measurement-caveat": 2, "informational": 1}
labelled + per-run labels                      {"correctness-failure": 0, "path-interference": 0, "harness-error": 0, "measurement-caveat": 2, "informational": 1}
```

before 的原始输出（`/tmp/wf-dual/logs/analyze-unlabelled.out` 末尾）：

```text
measurement-caveat   ledger-datagram-mismatch   pass1/wf-aot-opt/proxied MIX   the ledger counted 918 datagram(s)…
measurement-caveat   ledger-window-ambiguous    pass1/wf-aot-opt/proxied MIX   this window overlaps wf-aot-opt/direct and the ledger carries no per-run label…
measurement-caveat   ledger-connection-mismatch pass1/wf-aot-opt/direct LAT    the ledger saw 18 connection(s)… against the client's own 9.0
measurement-caveat   ledger-window-ambiguous    pass1/wf-aot-opt/direct LAT    this window overlaps wf-aot-opt/proxied…
…
correctness-failure  ledger-endpoint-overlap    pass1/wf-aot-opt               datagram(s) arrived from endpoint(s) used on both a proxied and a direct-path window of this run: 127.0.0.1:35…
```

after 的原始输出（`/tmp/wf-dual/logs/analyze-labelled.out` 末尾）：

```text
findings_by_severity: {"correctness-failure": 0, "path-interference": 0, "harness-error": 0, "measurement-caveat": 2, "informational": 1}
  informational        udp53-direct                 1
  measurement-caveat   control-block-missing        1
  measurement-caveat   declared-arm-missing         1
```

留下的这两条 caveat 是 **fixture 自己的形状**，修前修后都在、与本 ticket 无关：这份 fixture 只有一行、
没有 `control-pre`/`control-post` 块，且它跑的 3 臂不是 `wf-aot-opt` 声明的 full plan。**labelled 与
per-run label 两份的输出逐项相同**，说明在同机时钟下"实例绑定"与"per-run label"是同一件事。

---

## 4. 真实数据 before/after（E5-b 拉回的 9.5 MB 结果树）

树在 `/tmp/wf-bench/e5b-campaign/`（不入库）。after 的做法是把这份真实数据的账本按**修好的启动器
会写的 label** 重打一遍（记录内容一个字节没动，只改 `label` 字段），再跑同一个分析器：

```bash
$ python3 - <<'PY'   # 只改 label：ledger-main → target:40010，ledger-direct → target:40011
… 13466 + 6994 条记录 …
PY
$ bash benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh --raw /tmp/wf-bench/e5b-after/raw --out /tmp/wf-bench/e5b-after
```

**计数对比**（`/tmp/wf-dual/logs/real-comparison.out`）：

```text
finding                                              baseline  target:label  +utc 3.0s  per-run label
correctness-failure/foreign-connection                      2              2          2              2
correctness-failure/ledger-endpoint-overlap                10              0          0              0
informational/udp-not-carried                               1              1          1              1
informational/udp53-direct                                  5              5          5              5
measurement-caveat/control-drift-undecided                  5              5          5              5
measurement-caveat/latency-ceiling-reached                  2              2          2              2
measurement-caveat/ledger-connection-mismatch              71             90         13             90
measurement-caveat/ledger-datagram-mismatch               102            102         33            102
measurement-caveat/ledger-source-overflow                  64             50         43             50
measurement-caveat/ledger-window-ambiguous                 60              0          0              0
path-interference/direct-lane-latency                       4              4          4              4
TOTAL                                                     326            261        108            261
```

- **T1 的两族（10 + 60）清零** ✓
- `ledger-source-overflow` 64 → 50：direct 车道的窗口不再继承 main 账本的 overflow 计数（真值）；
- `ledger-connection-mismatch` 71 → 90：原来的池子让 dual 车道的 MIX 臂**碰巧**凑近客户端数字，正确
  归属后这些臂如实报出差异。这 90 条里绝大多数是 T7（时钟）造成的，见 §6；
- `ledger-datagram-mismatch` 102 条不变，其中 34 条与 `ledger-source-overflow` 同 scope（T2）。
- **"target:label" 与 "per-run label" 两列逐项相同**——再次说明在这轮数据上（同实例内无重叠）实例绑定
  与理想 per-run label 等价，这是修法正确性的强判据。

after 的分析器自述（`§14.1` 与 `§2` 的同一段文本）：

```text
| pass1 target ledger(s) | …/ledger-main.jsonl, …/ledger-direct.jsonl (20460 record(s), 0 unparsable line(s);
  attribution: 2 ledger(s); the ledger's own label names the target instance that wrote it (target:40010,
  target:40011), so a run is read against the ledger of the target its own run.json declares, then the arm's
  UTC window bounds it) |
```

**回归检查**：同一份修好的分析器跑**没打 label** 的真实树，逐项等于基线（320 条白名单外 + 6 条
informational，`endpoint-overlap` 10、`window-ambiguous` 60、`connection-mismatch` 71、
`datagram-mismatch` 102、`source-overflow` 64）：

```bash
$ bash …/analyze.sh --raw /tmp/wf-bench/e5b-campaign/raw --out /tmp/wf-bench/e5b-before-check
findings_by_severity: {"correctness-failure": 12, "path-interference": 4, "harness-error": 0, "measurement-caveat": 304, "informational": 6}
  correctness-failure  ledger-endpoint-overlap      10
  measurement-caveat   ledger-connection-mismatch   71
  measurement-caveat   ledger-datagram-mismatch     102
  measurement-caveat   ledger-source-overflow       64
  measurement-caveat   ledger-window-ambiguous      60
```

---

## 5. 冻结 oracle：改动是严格无操作

```bash
$ python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a,1b,1c,2,3,4,5
…
compared 51 slice(s): 0 differ(ent), 0 missing
differences: 0 structure, 0 value, 0 missing
rc=0: every slice of this batch is equal
```

合成树里每个 run 都带自己的 label（`wf-aot-opt-p1`、`wf-aot-opt-dual-proxied`、`pass1-target`、`""`），
没有一个是 `target:<port>` 形状 ⇒ 新分支在 fixture 上永不触发 ⇒ 51 个切片全部相等。这也是为什么
候选 D（用 `dnsSummary.port` 反推实例）被否决：它在 fixture 上**会**触发并改变输出。

---

## 6. T7（本轮新发现）：两台机器的时钟差约 3 s

### 6.1 当场实测

```bash
$ date -u +%Y-%m-%dT%H:%M:%S.%N
2026-10-08T12:23:22.105247956
$ ./wf.sh run '(Get-Date).ToUniversalTime().ToString("o")' 90
2026-10-08T12:23:25.3356761Z
$ date -u +%Y-%m-%dT%H:%M:%S.%N
2026-10-08T12:23:22.757654240
```

整条命令墙钟只花了 0.65 s，而 VM 自报的时间落在主机窗口之后 2.6–3.2 s ⇒ **VM 的时钟比主机快约 3 s**
（现在仍然如此）。

### 6.2 从 campaign 自己的记录里量出来

LOSS 臂是一条平的 500/s 突发，账本累计 `received` 因此每个 run 抬升一次；把它最后出现的那个秒和
客户端自己写的臂结束时刻（`startedUtc` + tick 偏移）相减，就是两台时钟之差（脚本
`/tmp/wf-dual/clock_offset.py`，输出 `/tmp/wf-dual/logs/clock-offset.out`）：

```text
run                               LOSS arm    burst  start delta  end delta
pass1/proxifier/direct                40.2     40.0        -2.64      -2.86
pass1/proxifyre/direct                40.2     39.0        -1.91      -3.12
pass1/proxybridge/direct              40.2     40.0        -2.83      -3.04
pass1/wf-aot-opt/direct               40.2     40.0        -2.38      -2.60
pass1/wf-fdd-opt/direct               40.2     40.0        -2.76      -2.98
pass2/proxifier/direct                40.2     39.0        -2.07      -3.28
pass2/proxifyre/direct                40.2     39.0        -2.20      -3.42
pass2/proxybridge/direct              40.2     40.0        -2.52      -2.75
pass2/wf-aot-opt/direct               40.2     39.0        -1.99      -3.22
pass2/wf-fdd-opt/direct               40.2     39.0        -2.12      -3.34

the LOSS arm is a flat burst, so the last second it occupies on the target's clock is its end:
median -3.08 s over the 10 run(s) whose burst stands alone, i.e. the target's clock is that much behind the client's
```

（其余行是长臂，突发与相邻 run 的突发连成一片，起点不可读，脚本标了出来。）

### 6.3 影响：把账本 `utc` 整体平移再分析

| 平移 | conn-mismatch | datagram-mismatch | source-overflow | measurement-caveat 总数 |
|---|---|---|---|---|
| **0（现状）** | 90 | 102 | 50 | 249 |
| +2.0 s | 68 | 49 | 50 | 174 |
| +2.5 s | 71 | 39 | 44 | 161 |
| **+3.0 s** | **13** | **33** | **43** | **96** |
| +3.5 s | 30 | 45 | 43 | 125 |
| +4.0 s | 32 | 70 | 43 | 152 |

+3.0 s 那一列里，**33 条 `ledger-datagram-mismatch` 全部落在报 `sourceOverflow > 0` 的 scope 里**，
即全部是 T2 的账；对齐后消失的是 77 条 connection-mismatch（90→13）与 69 条 datagram-mismatch
（102→33），它们由时钟错位解释。敏感性说明：每个 run 自己的偏移在 **2.0–3.4 s** 之间抖（§6.2 的
end delta 取负：突发独立的 10 个 run 是 −2.60…−3.42 s），单值平移只能对齐一部分 run，所以 +3.0 是
拟合值而不是"真值"；方向与量级是稳的（2.0–4.0 s 的任何平移都把 caveat 从 249 压到 96–174）。

**建议**（已写进 `AGENTS.local.md` §10 的可信前提）：跑之前让 VM 对时（`w32tm /resync` 或 `Set-Date`），
跑完把两边的 `Get-Date`/`date -u` 记进证据。属于 T7，本轮不修（改容忍度或让分析器自己量偏移都是更大的
决定）。

---

## 7. T2：源端点普查表打满

### 7.1 机制（代码）

`Target/SourceCensus.cs`：

```csharp
private const int SourceCapacity = 64;                       // 每个接收循环一张表
private readonly Slot[] _slots = new Slot[SourceCapacity];   // 槽位一次认领，终身不回收
```

- 键是 `(address, port)`，`UdpEchoServer` 给**每个接收循环**建一张 `SourceCensus`，循环数 =
  `--udp-receivers`（默认 `clamp(核数/2, 2, 8)`；本机与 E5-b 的靶机都是 **8**）。
- `Record` 命中就 `_datagrams++`，未命中就找空槽；**表满 ⇒ `_unplaced++`**，这条数据报对 `sources`
  永久不可见（`Harvest` 只发被认领槽位的增量）。
- `WriteSummaryAsync`（1 Hz + 收尾一次）把各表增量合并成 `sources[]`，并发布 `sourceOverflow`
  = 本区间 `_unplaced` 的增量。
- **槽位没有淘汰、没有复位、没有 TTL**：注释自己写着 "An endpoint keeps its slot for the life of the
  server"。而 `sources` 的语义是**每区间增量**——容量按整个进程生命期消耗，产出却按秒清账，这就是缺陷。

容量算术：总槽位 = 64 × 8 个接收循环 = 512；一个**持续**发包的源端点会被每一个接收循环看到，于是在
每张表里各占一格 = 8 格 ⇒ 有效容量 = 512 / 8 = **64 个端点**（整个 campaign 累计，不是每秒）。
一次性的突发端点则只落在收到它的那几个循环里（§7.2 的 8 接收循环复现就是这种形状，并集能到 80），
而真实臂的 socket 会连续发几秒到几分钟，属于前一种。

### 7.2 最小复现（本机，`/tmp/wf-dual/census_repro.py`）

起一个靶机，从 N 个新源端口各发若干轮数据报（多轮是为了让每个接收循环都看到每个端口，模拟真实臂的
socket 连续发几秒到几分钟），再读账本自己的 `udpSummary`：

```bash
$ ROUNDS=40 python3 /tmp/wf-dual/census_repro.py 1 80
--udp-receivers 1, burst 1: 80 datagram(s) from 80 fresh port(s) (58125..44862)
   ledger now: 4 udpSummary record(s), 64 distinct source port(s) accounted for, sourceOverflow(cumulative published) = 640
   the 65th port of the burst, 33593, is in the census: False
   port 58125 (in the table since burst 1) sent 15 more datagrams; the 3 summary record(s) written since report 15 of them
   distinct ports ever accounted for across 7 record(s): 64

$ ROUNDS=40 python3 /tmp/wf-dual/census_repro.py 8 80
--udp-receivers 8, burst 1: 80 datagram(s) from 80 fresh port(s) (35872..40428)
   ledger now: 4 udpSummary record(s), 80 distinct source port(s) accounted for, sourceOverflow(cumulative published) = 454
   the 65th port of the burst, 45331, is in the census: True
   distinct ports ever accounted for across 7 record(s): 80

$ ROUNDS=20 python3 /tmp/wf-dual/census_repro.py 8 70 70 70      # 三波，每波 70 个新端口
--udp-receivers 8, burst 1: … 70 distinct source port(s) accounted for, sourceOverflow … = 0
--udp-receivers 8, burst 2: … 76 distinct source port(s) accounted for, sourceOverflow … = 1418
   the first port of this burst, 36392, is in the census: False
--udp-receivers 8, burst 3: … 76 distinct source port(s) accounted for, sourceOverflow … = 2814
   the first port of this burst, 46409, is in the census: False
```

判据：`--udp-receivers 1` 时容量**恰好 64**（80 端口 × 40 轮 = 3200 条，入表 64×40 = 2560 条，
overflow 正好 640 = 3200−2560）；表里的端口**继续被正确累计**（15 条全部入账），新端口进不来：三波
复现里第一波 70 个全进（overflow 0），第二波只挤进 6 个（70→76，overflow 1418），第三波**一个也没进**
（76→76，overflow 2814），每波的第一批端口都不在表里。8 张表时并集上限更高（80 个端点分散到 8 张表），
但"满即永久"同形。

### 7.3 真实轮次的时间线（账本自己说的）

```bash
$ python3 /tmp/wf-dual/census_timeline.py /tmp/wf-bench/e5b-campaign/ledger-main.jsonl
minute            received  censused  overflow
2026-10-08T09:47        511       511         0
…
2026-10-08T10:44        858       858         0
2026-10-08T10:45       3523      3145       378        <- 从这一分钟起开始溢出
2026-10-08T10:46      11858         0     11858        <- census full from here on
2026-10-08T10:47       6789         0      6789
…
2026-10-08T11:26      12984         0     12984

distinct source endpoints the census ever accounted for: 64
    1  2026-10-08T09:47:34.3162716+00:00  192.168.100.2:52456
    2  2026-10-08T09:48:34.3138068+00:00  192.168.100.2:53011
…
   63  2026-10-08T10:45:18.3130734+00:00  192.168.100.2:57149
   64  2026-10-08T10:45:18.3130734+00:00  192.168.100.2:57150

last summary: received=1202818 sources=0 sourceOverflow=0
```

- campaign 一共出现 **64 个**源端点就满了（与 §7.1 的"有效容量 64"算术一致，也与 `--udp-receivers 1`
  复现出来的 64 一致），第 64 个出现在 `10:45:18`，`10:45:58` 起 `received` 每一秒都 100 % 进 overflow；
- 此后到收尾的 **42 分钟**里，`sources` 数组一直是 0 条，`received` 累计到 1202818——pass 2 的每一个
  UDP 窗口因此都读成 0（这正是 E5b 看到的 `ledger counted 0 … against the client's own 60001`）；
- 64 个端点全是 VM 的临时端口（`192.168.100.2:53xxx/57xxx`）：每个 run 的每个 UDP 臂各一条 socket，
  36 个 run × 若干臂，一小时就攒满，从此**整轮剩余时间**的普查都是空的。

### 7.4 修法方向与代价

| 方向 | 做法 | 代价 / 风险 |
|---|---|---|
| **推荐：按区间回收** | 表的生命期对齐它产出的语义：摘要器每秒 harvest 之后把**本区间没再出现**的槽位回收（槽位加 epoch/世代号，接收侧把陈旧 epoch 的槽当空槽用，沿用现有"端口最后发布"的无锁握手） | 改 `SourceCensus` + `UdpEchoServer` 约 30–50 行；记录键集不变；需要一个并发形状的测试（现有 `UdpReceiverOptionTests`/形状测试可扩展）。收益：容量变成"每接收循环每秒 64 个活跃端点"，任意长 campaign 都不会退化 |
| 加大表 | 每循环 4096 槽（内存 ~1 MB），线性扫描成本 ×64 | 只是推迟：端点仍然只增不减，长 campaign 仍会满；且每数据报多 64 倍比较 |
| 有界淘汰 | LRU/最久未见优先覆盖 | 与"按区间回收"同效，但淘汰逻辑更复杂、且要保证不丢未 harvest 的增量 |
| 只改披露 | 分析器把 `sourceOverflow` 讲成"普查永久不可用" | 不是修，只是把 T2 从"数据缺失"变成"声明数据缺失"；AC18 判据 4 仍不可达 |

**不是产品问题**：overflow 从 `10:45:58` 起对**每一个**窗口整齐等于 `received` 增量（含无产品的
`control-post`），与哪一行、有没有代理无关。

---

## 8. 门禁

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | **通过**：`0 Warning(s), 0 Error(s)` |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | **通过**：末轮（最终字节）exit=0，14 个项目、**1656 passed / 0 failed**。中间有一轮 `WinForward.E2E.Tests` 报 1 条失败：`LaneEngineAllocationGateTests.TheSendPathAllocatesNoManagedBytesOnTheCallersThread` 得到 `Expected 0, Actual 6768`——该测试单独重跑 **3/3 绿**，量级与 `tests/Directory.Build.props` 记录的 tiering/GC 噪声同类（7,336–7,360 B），且它测的是客户端 `LaneEngine` 发送路径，与本轮只动分析器的改动无关（三次全量：0 失败 / 1 失败 / 0 失败） |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **通过**：exit 0，输出 **0 字节** |
| 4 | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`（先清 `~/.local/share/JetBrains/` 与 `/tmp/JB`） | **通过**：`<IssueTypes />` 与 `<Issues />` 全空，0 条 `<Issue>`（第一次报的 1 条 `MergeIntoPattern` 已按建议修掉并重跑全量，见 §8.1） |
| 5 | `python3 scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests` | **通过**：exit 0，无输出 |
| 6 | `python3 scripts/oracle-diff.py --batch 1a,1b,1c,2,3,4,5` | **通过**：51 切片、0 差异、rc=0 |

### 8.1 inspectcode（清缓存全量）

第一次全量跑报出 **1 条** `<Issue>`，是本轮新代码自己招来的：

```text
<Project Name="WinForward.E2E.Analysis">
  <Issue TypeId="MergeIntoPattern" File="benchmarks\WinForward.E2E.Analysis\Findings\LedgerViews.cs"
         Offset="12720-12722" Line="300" Message="Merge into pattern" />
</Project>
```

`TargetPorts` 里写的是 `is { } port && port is > 0 and <= 65535`，按检查器建议合并成
`is > 0 and <= 65535 and var port`（等价、更短），随后**清缓存重跑全量**：

```bash
$ rm -rf ~/.local/share/JetBrains/ /tmp/JB
$ jb inspectcode -f=Xml -e=HINT -o=/tmp/wf-dual/logs/jb-inspectcode2.xml WinForward.slnx
Inspection report was written to /tmp/wf-dual/logs/jb-inspectcode2.xml
exit=0
$ rg -c "<Issue " /tmp/wf-dual/logs/jb-inspectcode2.xml
（无输出）
$ cat /tmp/wf-dual/logs/jb-inspectcode2.xml
  <IssueTypes />
  <Issues />
```

`<IssueTypes />` 与 `<Issues />` 都是空的 ⇒ **0 条 `<Issue>`**。改完之后 format（空输出）与
`oracle-diff`（51 切片 0 差异）都重跑过，真实数据与本机 fixture 的计数也重跑核对过，均不变。

---

## 9. 偏离、未做与给 check 的点

1. **启动器不入库**（§3.1）。T1 的"改启动器"这一半只在本机生效；入库的是分析器规则与文档约定。check
   若要验证，得直接读 `/home/paff/Projects/WinForward/benchmarks/WinForward.E2E/scripts/start-targets.sh`
   （gitignore 但文件在）。
2. **没有新增单元测试**。`WinForward.E2E.Analysis` 按 D20.6 不开放 `InternalsVisibleTo`，`LedgerViews`
   是 internal；`verification/` 下的 boundary-tree 检查是冻结物（改它要重冻 + 换哈希），本轮明令不许动。
   替代验证 = 冻结 oracle 全 5 批 + 本机端到端 before/after + 真实数据 before/after。
3. **+3.0 s 是拟合值**（§6.3），不是测出来的真值；每个 run 的偏移抖 ±0.5 s。T7 的结论是"跨机时钟错位是
   主因"，不是"必须平移 3.0 s"。
4. **没有跑新的 Windows campaign**。本轮的 before/after 用 E5b 已拉回的真实数据 + 本机 fixture；下一轮
   campaign 需要（a）用改过的 `start-targets.sh`，（b）先给 VM 对时。
5. **T2 只定位未修**：修法与代价写在 §7.4，建议作为独立 ticket（按区间回收普查表）。
6. 本机 fixture 的 3 条 findings 里，2 条（`control-block-missing`、`declared-arm-missing`）是 fixture
   形状自带的，修前修后都在；比较时只看差值。
