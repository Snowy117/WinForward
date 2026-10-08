# T2 独立复核（check 轮，2026-10-08）

复核对象：`benchmarks/WinForward.E2E/Target/SourceCensus.cs` 的按区间回收（作者证据见
[`T2-census-reclaim.md`](T2-census-reclaim.md)）。本轮复核**自己造树、自己写压力复现、自己做变异**，
不重跑作者的脚本作为判据；作者的 4 条单测只作为对照。

复核时的工作树哈希：

| 文件 | 复核开始时 sha256 |
|---|---|
| `Target/SourceCensus.cs`（作者的改动，即被测版本） | `0a3db23a28dbc31b94018000d38016c3b7210e9a602b467e17fd400ba33c6a7f` |
| `tests/WinForward.E2E.Tests/SourceCensusTests.cs`（新增 4 条事实） | 作者版本，未改 |

复核用二进制：由工作树 `dotnet publish -c Release -r linux-x64` 得到，dll sha256
`2d7cd768df175e2d4a40fd61b537e5ef8ddd071ea2ddcdcdeef6c78c0145373e`，与作者的 after 二进制**逐字节相同**
（同一棵树，可复现）。"修前"对照用的是作者留在 `/tmp/wf-t2/before/` 的二进制（`998baf39…`）。

复核脚本（不入库，与作者同样的约定）：`/tmp/wf-check/t2_tree.py`（记录级树）、
`/tmp/wf-check/t2_stress.py`（8 接收循环压力）、`/tmp/wf-check/mutate.py`（变异驱动器）、
`/tmp/wf-check/CheckT2ScratchTests.cs`（复核者的单测，跑完已从工作树移出，见 §3）。

---

## 0. 结论

| 项 | 结论 |
|---|---|
| 记录语义 | **没变**：`sources[]` 仍是本区间增量、`sourceOverflow` 仍是本区间没能入表的包数、键集与 `udpReceivers` 未动。自己造的树（8 区间、持续/间歇/一次性端点混合）逐端点精确：C1 400/400、C2 240/240、C3 160/160、H1 60/60、H2 120/120、239 个一次性端点 2400/2400，`received 3380 = censused 3380 + overflow 0` |
| 计数被错误归零 | **不存在**（但作者文档的措辞偏强，本轮修正）：持续端点的槽位**可以**在新区间被新端点先拿走，认领与计数随之从头开始；但 `sources[]` 按**端点**聚合，新 claim 的整份计数就是该端点本区间的数据报，逐项精确 |
| `_claim` 唯一性 | **成立**：同一槽位的 claim 值严格递增（同一世代不会被认领两次；本区间被碰过的槽不再是空槽）。并发观测 171,016 次 slot/claim，0 次倒退 |
| `Harvest` 双读 | **必要且有效**：变异 M-D（只读一次，把读窗口撑开）让复核者的并发事实变红；原版在所有压力下不误发、不重复 |
| 有界丢失 | **已量化**：1 Hz 真实靶机 8 循环两轮共 **439,445 个数据报，丢失 0**；变异与极端 churn（微秒级区间）下才出现 1.5% / 18.9% 的回退。判断：**可接受**（详见 §2.3） |
| 变异检查 | 4 条自造变异**全部**被某条事实或压力复现钉红；还原哈希回到 `0a3db23a…` |
| 分配 | `Record` 路径 8×4096 次调用 **0 字节**；对照批（每次故意装箱一个端点）131,072 字节 |
| 门禁 | selftest rc=0 / 0 条 error；分析器 rc=0；oracle 51 切片 0 差异 rc=0；build 0 警告；`dotnet test -m:1` 全绿；format 空输出；inspectcode 0 `<Issue>`；effective-lines 无输出；check-readme-contract ok |
| 披露 | 修改了 5 处措辞（spec §3.12 ×3、README ×1、`SourceCensus` 类注释 ×2、作者证据 §1.2 ×1）；改后**够用且准确**，并补上"丢失窗口随摘要频率放大"的边界 |
| 是否阻塞提交 | **不阻塞**。代码无需改动；本轮只改了文档/注释 |

---

## 1. 记录语义：自己造的树

### 1.1 记录级（真实靶机，`--udp-receivers 1`，8 个区间）

```
$ python3 /tmp/wf-check/t2_tree.py /tmp/wf-check/after/WinForward.E2E /tmp/wf-check/tree
tree over 8 interval(s): steady {'C1': 50, 'C2': 30, 'C3': 20} each interval, 30 one-shot
  endpoint(s) x 10, H1 15 every other interval, H2 every interval
target received 3380; census listed 244 distinct port(s), attributed 3380, sourceOverflow 0
received - (censused + overflow) = 0
  C1 port 39992: sent 400, published 400, per-interval [0, 50, 50, 50, 18, 32, 50, 50, 50, 50, 0, 0, 0]
  C2 port 50479: sent 240, published 240, per-interval [0, 30, 30, 30, 0, 30, 30, 30, 30, 30, 0, 0, 0]
  C3 port 41361: sent 160, published 160, per-interval [0, 20, 20, 20, 0, 20, 20, 20, 20, 20, 0, 0, 0]
  H1 port 46685: sent  60, published  60, per-interval [0, 15, 0, 0, 15, 0, 15, 0, 15, 0, 0, 0, 0]
  H2 port 50753: sent 120, published 120, per-interval [0, 15, 15, 0, 15, 15, 15, 15, 15, 0, 15, 0, 0]
one-shot endpoints: 239 sent 2400, published 2400, mismatched 0
failures: 0
```

- 每区间的值约为该端点在**那一个区间**发出的包数（一次 18+32 是跨区间边界被切开的两半，合计正好 50）；
  没有任何区间发布过累计值。持续、间歇、只来一次、迟到的四类端点全部逐端点精确。
- 244 个不同端口在整轮出现过 ⇒ 表的 64 格确实在回收。

### 1.2 同一棵树跑"修前"二进制

```
target received 3380; census listed 64 distinct port(s), attributed 1570, sourceOverflow 1810
one-shot endpoints: 240 sent 2400, published 590, mismatched 181
failures: 1
```

修前：整轮只有 64 个端口进过表，181 个一次性端点的包**永远**只能进 overflow——正是 T2 缺陷的形状。
这同时说明复核者的树**能**发现旧行为，不是恒绿的空壳。

### 1.3 单测（复核者自己写的调度）

`ContinuingEndpointsArePublishedAsEachIntervalsOwnDelta` 用 6 个区间的显式调度（一个端点每区间换一个
包数、一个静默一区间再回来、两个只出现一次、一个迟到）逐区间断言"发布值 = 本区间实际包数"，并断言
键数相等——基线被错误归零会少发，被错误保留会多发。绿。

### 1.4 "认领变化会不会把续存端点的计数错误归零"

不会**错误**归零，但作者的措辞（"持续端点跨区间续存、计数基线不变"）比实际强，本轮已按实测修正：

- 空闲判定是 `_lastSeen != 当前世代`，而**区间一开所有槽都是这个状态**。所以一个**新**端点若抢在
  持续端点本区间的第一个数据报之前到达，可以把持续端点的槽拿走（复核者的
  `AContinuingEndpointWhoseSlotIsTakenStillPublishesTheIntervalsOwnDelta` 与"64 个新端点先到"的
  反例实验都复现了这一点）。
- 拿走之后：旧计数被覆盖，持续端点的下一次到达认领新槽、计数从 1 开始；**本区间的数据报仍整份发布在
  它自己的键下**，因为 `Harvest` 产出的是"键 → 计数"的字典，键是端点而不是槽。逐端点精确（§1.1）。
- 唯一的代价是旧计数里"摘要器读完之后"的那几个包（§2.3）。

用反射直接读表核对：认领值（`_claim`）在换手时确实变化，且同一槽位的认领值**从不倒退**
（§2.1）。作者单测第 1 条钉住的是"发布值仍是本区间增量"，这一点成立；不成立的是"计数基线不变"
这个机制描述。

---

## 2. 并发与内存序

### 2.1 `_claim` 唯一性的复核

论证（复核者独立推导）：一张表只有一个写者（一个接收循环，`Record` 不重入），而 `Record` 顶部读到的
局部世代在同一调用内使用、跨调用单调不减；认领要求目标槽 `_lastSeen != epoch`，而认领会把
`_lastSeen` 写成该世代 ⇒ 同一世代不可能在同一槽位上认领两次；又因 `_lastSeen ≥ 上一次认领值`，
"可认领"意味着上一次认领值 < 当前世代 ⇒ **同一槽位的认领值严格递增**。

观测（`ClaimValuesPerSlotAreStrictlyIncreasing`，反射读私有 `_slots`，与一个满速 `Harvest` 线程并发，
4000 个端点 × 5 包）：

```
slot/claim observations 171016, decreasing handovers 0
```

### 2.2 `Harvest` 双读的作用（变异反证）

把"读身份与计数前后各读一次 `_claim`"改成"只读一次"，并把这一次读到的值**撑开到换手发生之后**
（变异 M-D），复核者的压力事实立刻变红：

```
== M-D-harvest-reads-claim-once: Harvest reads the claim once, held open so a reclaim happens under the read
   Failed WinForward.E2E.Tests.CheckT2ScratchTests.TheSummariserNeverCreditsAKeyMoreThanItSent [970 ms]
```

原版在同一压力下（160,000 包、35,096 个端点、6,151 次 harvest）**没有**任何键被多记、也没有幻影键：

```
recorded 160000 over 35096 endpoint(s) in 6151 harvest(s); published 129791; shortfall 30209 (18.8806 %)
```

（该场景的 harvest 间隔是微秒级，shortfall 是 §2.3 的窗口代价，不是错记：断言的两侧是"每个键发布 ≤
实际发送"与"没有从未出现的键"。）

### 2.3 压力复现与丢失量化

**真实靶机、8 个接收循环、1 Hz（生产节奏）**：

| 轮次 | 形状 | 发送 | 目标收到 | 普查入表 | overflow | `received-(censused+overflow)` |
|---|---|---|---|---|---|---|
| A | 10 个 churn 线程 × 40 波 × 250 包，换手时用 barrier 同步；2 个端点交替发包、切换瞬间重叠 150 ms | 129,928 | 129,928 | 129,928（401 端口） | 0 | **0**（逐区间最大偏差 ±23，是 `received` 与普查读数的时刻差，总和 0） |
| B | 10 个 churn 线程 × 1500 波 × 20 包（每区间上千个新端点） | 309,517 | 309,517 | 56,573（2,489 端口） | 252,944 | **0** |
| 修前 | 同 A | 129,973 | 129,973 | 45,469（69 端口） | 84,504 | 0（但 65.02% 的包永远进 overflow，331 个端点从未入表） |

`targetSummary.udp.udpReceivers = 8`（生效并发度），`udpSummary` 的键集仍是
`bytes,label,received,sourceOverflow,sources,ticks,type,undecodable,utc`——**没有新字段**。

A 轮还逐个端点核了归属：400 个 churn 端口每一个的发布值都等于 `250 × 它被复用的次数`（有一个端口号被
两次 bind 复用），两个 handover 端点分别是 14,973 / 14,955，全部精确——没有任何一个端点被少记或多记。

**单测层（把 harvest 频率推到远超 1 Hz 的对抗值）**：

| 形状 | 记入 | 发布 | overflow | 缺口 |
|---|---|---|---|---|
| 8 表 × 20,000 包、23 包一波、harvest 满速（700 次/70 ms，约 100 µs 一个区间） | 160,000 | 130,385 | 27,242 | 2,373（1.48%） |
| 同上但 harvest 每 20 ms 一次（整轮只有 1 个区间） | 160,000 | 11,776 | 148,224 | **0** |
| 4 表 × 40,000 包、绝大多数端点只发 1–2 包、harvest 满速（微秒级区间） | 160,000 | — | — | 30,209（18.88%） |

**判断：可接受。** 理由与边界：

1. 窗口的物理含义是"某端点的计数在摘要器读过之后、世代推进之前又被写入的那几个包，且该槽在下一个
   区间被别人先认领"。它是**每次换手 ≤ 一个读窗口**的量，不是每个包都要交的税。
2. 生产节奏（1 Hz）下，两次独立形状、共 **439,445 个数据报、8 个接收循环**的实测丢失是 **0**；作者的
   独立观测是 32,040 里差 1。两者同量级，远在分析器 ±1% band 之内。
3. 对抗场景（微秒级摘要、绝大多数端点只活一个包）里缺口可以到 1.5%–18.9%：这说明**丢失率随"摘要频率
   / 端点寿命"放大**。§6 已把"1 Hz 摘要间隔属于契约的一部分"写进 spec §3.12，避免以后有人为了让
   报告更细而调快 `WriteSummaryAsync`。
4. 该缺口不会伪装成 overflow（overflow 只在同一区间有超过 64 个端点被碰过时出现），因此**不会**被
   分析器误读成"容量事件"；分析器的 datagram band 会把它记成计数偏差。

---

## 3. 变异检查

4 条自造变异（都不是"把文件换回旧版"），每条都让某个事实/压力变红；每条跑完立即还原并核对哈希：

| # | 变异 | 变红的事实 | 判据 |
|---|---|---|---|
| M-A | 认领变化时不再把发布基线归零（`_published[index] = datagrams`） | 8 条（含作者单测 2/3/4 与复核者 4 条） | 新 claim 的整份计数被旧基线吞掉 |
| M-B | 认领时不写 `_lastSeen` | 7 条（含作者单测 1/2/3/4） | 同一世代里新槽仍被当空槽，被下一个端点抢走 |
| M-C | `_claim` 在身份之前发布（+`Thread.Yield()` 撑开窗口） | 复核者压力事实 1 条 | 摘要器把"新认领 + 旧身份"配成一行，旧键被多记 |
| M-D | `Harvest` 只读一次 `_claim`（把该次读撑开到换手之后） | 复核者并发事实 1 条 | 把"旧认领 + 新身份/计数"发布出去，随后基线归零再发一次 ⇒ 多记 |

```
$ python3 /tmp/wf-check/mutate.py
snapshot sha256 0a3db23a28dbc31b94018000d38016c3b7210e9a602b467e17fd400ba33c6a7f
...
   restored, sha256 0a3db23a28dbc31b (snapshot 0a3db23a28dbc31b)   # 每条变异都是这一行
```

**还原哈希：`0a3db23a28dbc31b94018000d38016c3b7210e9a602b467e17fd400ba33c6a7f`**（复核后的
`SourceCensus.cs` 只差注释，见 §6/§7，最终哈希 `8ddce3e6b817aca36dd8feaae7b19a26c514aaaab6fafabc870584015c46200f`）。

> M-A 的第一次构造（只留下 `_publishedClaim[index] = claim;`）**编译不过**：SonarAnalyzer S3440
> 认为条件已无用（仓库把分析器警告当错误）。改成保留分支、换掉赋值即可——这本身也说明该字段的
> 读写是配套的。

复核者的单测文件**没有留在工作树里**（`tests/WinForward.E2E.Tests/CheckT2ScratchTests.cs` 已删除，
副本在 `/tmp/wf-check/CheckT2ScratchTests.cs`）：它是复核脚手架（含一处反射 + 局部 `#pragma`），
是否要作为常驻事实收编由作者决定。若要收编，建议只收 `TheSummariserNeverCreditsAKeyMoreThanItSent`
一条——它是唯一覆盖"回收与摘要器并发"的事实（作者的第 4 条用 8 个**从不换手**的端点，回收路径根本
没被走到，所以 M-C/M-D 都抓不到）。

---

## 4. 分配

`Record` 的调用图只有 `Volatile.Read/Write`、`Interlocked`、`stackalloc` 与字段读写；`Endpoint`
对象的分配来自调用方（socket 的 `RemoteEndPoint`），不在普查里。测量（每批 4,096 次 `Record`，
端点对象预分配复用）：

```
batches: [0, 0, 0, 0, 0, 0, 0, 0], first zero at 0
counter-proof batch allocated 131072 byte(s)
```

对照批（每次故意 `object boxed = Endpoint(...)` 再传入）在同一窗口里被量到 131,072 字节 ⇒ 仪器确实
看得见分配。数据路径无委托、无 LINQ、无加锁（静态复核 + 该测量）。

---

## 5. 回归与门禁

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `selftest.sh scripts/plans/selftest-plan.json`（从 `benchmarks/WinForward.E2E` 跑，靶机二进制由最终树发布） | **rc=0**，`error records: 0` |
| 2 | `analyze.sh --raw /tmp/wf-bench/selftest/out --flat --ledger …` | **rc=0**，findings = 1 informational（`dns-arm-port`）+ 1 measurement-caveat（`control-block-missing`），均为 flat 单 run 树固有；**没有** `ledger-*` |
| 3 | `oracle-diff.py --batch 1a,1b,1c,2,3,4,5` | **rc=0**：51 切片、0 差异、0 缺失 |
| 4 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s), 0 Error(s)** |
| 5 | `dotnet test WinForward.slnx -c Release -m:1` | 全绿（见 §5.1） |
| 6 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **rc=0，输出 0 字节** |
| 7 | `jb inspectcode -f=Xml -e=HINT`（先清 `~/.local/share/JetBrains/`、`/tmp/JB`） | **0 `<Issue>`**（见 §5.2） |
| 8 | `effective-lines.py <四路径>` | **rc=0，无输出** |
| 9 | `check-readme-contract.py` | **ok**：111 key(s) / 401 constant path(s) |

### 5.1 全量测试

```
$ dotnet test WinForward.slnx -c Release -m:1      # rc=0
Passed!  - Failed: 0, Passed:   18 - WinForward.Analyzers.Tests.dll
Passed!  - Failed: 0, Passed:  121 - WinForward.Configuration.Tests.dll
Passed!  - Failed: 0, Passed:   63 - WinForward.Core.Tests.dll
Passed!  - Failed: 0, Passed:  361 - WinForward.E2E.Tests.dll
Passed!  - Failed: 0, Passed:   24 - WinForward.Integration.Tests.dll
Passed!  - Failed: 0, Passed:   74 - WinForward.NdisApi.Tests.dll
Passed!  - Failed: 0, Passed:  137 - WinForward.Performance.Tests.dll
Passed!  - Failed: 0, Passed:   72 - WinForward.Protocols.Tests.dll
Passed!  - Failed: 0, Passed:  120 - WinForward.Runtime.Capture.Tests.dll
Passed!  - Failed: 0, Passed:  183 - WinForward.Runtime.Flow.Tests.dll
Passed!  - Failed: 0, Passed:  108 - WinForward.Runtime.Socks5.Tests.dll
Passed!  - Failed: 0, Passed:  157 - WinForward.Runtime.TcpRedirect.Tests.dll
Passed!  - Failed: 0, Passed:  164 - WinForward.Runtime.UdpProxy.Tests.dll
Passed!  - Failed: 0, Passed:   58 - WinForward.Windows.Tests.dll
    14 个项目 / 1660 passed / 0 failed / 0 skipped
```

这与作者的记录逐项相同（复核者的脚手架已移出工作树，所以 E2E 项目仍是 361 条 —— 作者 4 条新事实
已含在内）。

### 5.2 inspectcode

```
$ rm -rf ~/.local/share/JetBrains/ /tmp/JB
$ jb inspectcode -f=Xml -e=HINT -o=/tmp/wf-check/gates/inspectcode.xml WinForward.slnx
Inspection report was written to /tmp/wf-check/gates/inspectcode.xml
inspectcode rc=0
issues: 0 issue types: 0
has CSharpErrors: False
```

解析 XML 而不是信退出码：`<IssueTypes />` 与 `<Issues />` 都是空的 ⇒ **0 条 `<Issue>`**，也没有
`CSharpErrors`（清缓存后的全量）。

---

## 6. 披露复核（`measurement-harness.md` §3.12 与 README）

判断：**方向正确、量级诚实，但有三处措辞比代码强**，本轮已修：

1. spec §3.12 第 1 条原说"a slot no datagram has touched **since the previous interval**"——实际是
   "**本区间**还没有碰过"（区间一开，所有槽都满足）。已改成"the lowest-numbered slot **no datagram of
   the current interval has touched yet**"，并点明空闲时钟每次 `Harvest` 重置。
2. spec §3.12 第 3 条原说"An endpoint that keeps arriving keeps its slot *and* its running count"——
   持续端点的槽**可以**被先到的新端点拿走（§1.4）。已改成：丢掉的是计数，不是记录；`sources[]` 按端点
   聚合，新 claim 的整份计数就是本区间的数据报；并把 overflow 的口径写准（"**先被碰到的** 64 个端点"，
   不是"最活跃的 64 个"）。
3. `SourceCensus` 类注释与 `<remarks>` 第三段有同样两处措辞，已同步改（这是本仓锁-自由握手的既有
   记载方式，改了措辞、没改论证）。
4. README 的 `udpSummary` 行原说"a slot no datagram of an interval touched is handed to the next
   endpoint"——同上，已改成"a slot the current interval has not touched yet"，并补一句"新 claim 的整份
   计数就是该区间该端点的数据报"。
5. 作者的证据 §1.2 那句"只有**整整一个区间没有数据报**的槽位才会被下一个端点拿走"已按实测更正，
   并注明是 check 轮补记。

补充的一条**边界披露**（spec §3.12 有界代价条目）：丢失窗口随"摘要频率 / 端点寿命"放大——1 Hz 下
439,445 包实测 0 丢失，微秒级摘要下可到 1.5%–18.9%。所以 1 Hz 的摘要间隔是契约的一部分，不能为了
报告更细而随手调快。

未改而**仍然够用**的部分：分析器侧对 `sources`/`sourceOverflow` 的读取与 finding 文本
（"the endpoint census table was full, so the endpoint list for this window is incomplete"）在新模型下
依然准确（它说的是"本窗口的端点清单不完整"，不是"普查已死"）；分析器 README 的"按 per-interval 增量
计数、overflow 如实报出"也仍然成立。

---

## 7. 规范

- **有效行**：`SourceCensus.cs` 144 有效行 ≤ 400（`effective-lines.py` rc=0 无输出）；四路径同样无输出。
- **没有变更日志式注释**：新增注释只有两类——类头的契约（槽位寿命、容量模型、代价）与 `<remarks>`
  里的内存序论证。都是"从代码本身推不出来的约定"：发布/获取配对、作废-全栅栏-发布三步、以及
  `Harvest` 为什么要读两次 `_claim`——删掉它们，读者无法从 `Volatile.Write` 的存在推出这些约束。
- **内存序论证属于必要约定**：本仓该类握手一直以这种方式记载（修前版本也有一段"端口最后发布"的
  等价论证）；spec §3.12 的 handshake 条目与代码注释互为镜像。
- 注释行宽 ≤ 120，与既有风格一致。

---

## 8. 未修复但需上报

1. **复核脚手架未入库**（§3）：4 条变异、7 条复核事实、两个脚本都在 `/tmp/wf-check/`。若作者希望
   常驻，建议只收并发那条；否则本条证据即为其配方与实测数字。
2. **"持续端点跨区间续存、计数基线不变"这句话在作者的证据与 ticket 摘要里仍是旧措辞**：§1.2 已补
   更正，但 `tickets.md` / `semantic-fixes/index.jsonl` 的摘要字段仍写着"计数基线不变"。它们描述的是
   **记录语义**（`sources[]` 仍是本区间增量）而结论也正确，故本轮不动，留作者决定是否润色。
3. **`sourceOverflow` 的语义在报告读者眼里仍是"每区间"**：分析器 finding 文本准确，但如果将来出现
   高频 overflow，需要读者知道那是"本区间端点太多"而不是"普查坏了"——spec §3.12 已写死这点。
