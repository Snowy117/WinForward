# E3-b2 分析器侧语义收尾（`#11` 的 `undecodable` 披露 + `#19` 的 CPU 口径 + `scripts/check-fairness.py`）

本批是 E3 的第三批：`10-07-e2e-e3-semantics/implement.md` 的 E3-b 节第 6/7 条里**分析器侧**的那半
（客户端侧由 E3-b1 交付，HEAD `8adb927`）。**`src/` 与任何 `.cs` 一行未改**
（`git status --porcelain | rg -c '\.cs$'` = 0），所以 publish 与 selftest 两个二进制门禁跳过（理由见 §5.1），
其余四条门禁串行全绿。**本轮未提交。**

判定线（逐条见 §4）：默认合成树（`--undecodable 0`、无越界）上 `verdict.json` **逐字节不变**，
`tables.md` 只变 2 行（§4 caption 的一句指针 + §6 的一条脚注）；`check-fairness.py` 对 **HEAD 的分析器**
红（默认树 2 条 / `undecodable > 0` 树 6 条）、对本批的分析器绿（13/13 与 16/16）；
六条门禁里可跑的四条全绿（build 0/0、test E2E **286**、format 0 字节、inspectcode `<Issue>` **0**）。

---

## 0. 结论一句话

`#11` 今天在分析器里零消费（`rg -c undecodable analyze.py` = 0，E3-premises §1 第 9 行）：现在
target 账本的 `undecodable` 以 **§14.6 一张 target 侧总量表**披露，**只在总量 > 0 时出现**，
**不摊到任何 run/arm**，且 `corruptRate` 的语义与删除都不动（D19.2 ⑤/⑮）；`#19` 的 CPU 口径以
**§6 的脚注**（`user-mode only — no kernel-mode work outside the process`）披露，§4 的 `proxy CPU` 列有一句
指向 §6 的指针；`benchmarks/WinForward.E2E/scripts/check-fairness.py`（新，542 行；check 轮修两处后 **554 行**，见 §8.3）把 `#17`/`#18`/`#19`/`#11`
变成可 grep 的断言，**六条文本负控 + 两个分析器突变**全部实测为红，还原后 `analyze.py` 与突变前逐字节相同。

---

## 1. 改动面

### 1.1 `#11`：`undecodable` 的消费（D19.2 ⑤/⑮、D19.3 H）

| 面 | 内容 |
|---|---|
| 读 | `analyze.py:2590` 新增 `ledger_decode_totals(ctx)`：对**每个不同的账本路径**取 `udpSummary` 与 `targetSummary/udp` 里 `received`/`undecodable` 的**最大值** |
| 为什么是路径 | target 写的是账本**生命周期**的累计值（`Target/UdpEchoServer.cs:22,63,96,148`），所以"这个账本看到了多少"= 它所有记录的最大值；而同一个账本文件会被挂在它跨过的**每个 pass** 上（§14.1 的 `records` 三行相同就是证据），按 pass 取键会把一个 target 的数据报数重复计三次 |
| 渲染 | `analyze.py:5217` 起：`if undecodable > 0:` 追加 `### 14.6 UDP decode quality (target side, unattributable)`：一段口径说明 + 一张表（每个账本一行 + `all ledgers` 合计行），列 `ledger / datagrams received / undecodable / share of received` |
| 只做总量级 | 表按**账本**（= target 实例）分组，**不按 run/arm**；`ledger_views()` 的 `per_arm`（§14.2 的数据源）**不新增**任何 undecodable 字段；`verdict.json` 一行不改（§4 证明） |
| 不删 `corruptRate`（D19.2 ⑮） | `corruptRate`/`lossRate`/`clientSendLoss` 的公式与渲染全部未动；§14.6 正文逐字写明它们 "keep their own meanings and are not adjusted by this number" |
| 0 ⇒ 不出现 | 条件就是 `undecodable > 0`；`>= 0` 的突变（M-c，§3）在**零 undecodable 的默认树**上把披露打出来并让断言红 |

§14.6 的内容（宽段落按本文件的阅读宽度折行，实际输出每段一行）：

```markdown
### 14.6 UDP decode quality (target side, unattributable)

The target drops every datagram it cannot decode as a frame and counts it (`udpSummary`'s
`undecodable`, published again as `targetSummary/udp/undecodable`). That counter is the only witness
of corruption on the **request** path: a datagram the client sent but the target could not read never
comes back, so the client's own `corruptDatagrams` — a corrupted frame that *did* arrive — cannot see
it and the client can only report the result as path loss. The count is disclosed here as a
**target-side total and nothing else**: a datagram that fails to decode carries no sequence number,
so it belongs to no run and no arm, and no arm-level cell, rate or gate anywhere in this file includes
it. `lossRate`, `corruptRate` and `clientSendLoss` keep their own meanings and are not adjusted by
this number.

| ledger | datagrams received | undecodable | share of received |
|---|---|---|---|
| /tmp/e3b2/fairness/counted/ledger-direct.jsonl | 164000 | 0 | 0.0000 % |
| /tmp/e3b2/fairness/counted/ledger-main.jsonl | 174400 | 7 | 0.0040 % |
| all ledgers | 338400 | 7 | 0.0021 % |

A ledger is one target instance's lifetime, and the same file can span every pass, so its row above is
that instance's total and the `all ledgers` row sums the instances. A campaign whose targets could not
decode a single datagram prints no such table at all.
```

> **合成 fixture 的已知形状偏差**（不影响披露逻辑，登记）：`make_tree.py` 的两份账本共用**一个**累计
> `received` 计数器（`ledger_udp_cumulative` 在主流与直连流之间共享），所以"每个账本取最大值再相加"在这棵树
> 上会重复计一段（338400）。真实 harness 里每个 target 实例各自持计数器（`UdpEchoServer` 的 `_received`），
> 逐账本最大值就是该实例的生命周期总量。本批不改 fixture 的这个形状：改它会动默认树的 §14.2 数值，
> 从而把"零回归"的 diff 面糊掉。

### 1.2 `#19`：CPU 口径披露（D19.2 ⑩ 指定的唯一原样存活条目）

| 位置 | 文本 |
|---|---|
| §6 CPU detail 脚注（`analyze.py:3976`，紧跟 CPU 表之后、在 denominators 段之前） | **CPU scope: user-mode only — no kernel-mode work outside the process.** Every cell above is the sampled process's own `Process.TotalProcessorTime`: the time the operating system charges to *that process's* threads, its user time plus the privileged time those threads spend in system calls. Work the product causes in kernel mode outside those threads is measured nowhere in this table: interrupt, DPC and ISR time in a kernel data path, packets a driver serves on behalf of other processes, and machine-wide CPU are all outside the number. A kernel-heavy product can therefore show a low cell here while still costing the machine real CPU; compare the columns as equally-scoped process CPU, never as a product's total cost. |
| §4 Headline matrix caption（`analyze.py:3692`，同一句里追加从句） | "…`proxy CPU` is percent of one vCPU over the loaded arms (IDLE excluded)**, measured as user-mode process CPU only — section 6 states the scope in full —** and `steady-state private bytes` is…" |

口径的**准确性**（不是笼统的"只算用户态"）：harness 采的是
`Process.TotalProcessorTime`（`Client/ProcessCounterSource.cs:153`），即 OS 记在**该进程线程**上的时间（用户态 +
这些线程自己的 privileged/系统调用时间）。内核**在这些线程之外**为产品做的工作——内核数据路径的
interrupt/DPC/ISR、驱动替别的进程处理的包、以及机器级 CPU——都不在数里。脚注按这条边界写：读作
"同样口径的进程 CPU"，不可读作"产品的总成本"。

`analysis/README.md` 的 "CPU and memory" 段与 "Judgement calls" 列表**本批不动**：它是 E4 冻结/重写分析包时
的文件（D12），§6 的脚注是权威文本；登记见 §7。

### 1.3 `scripts/check-fairness.py`（新文件，D19.2 ⑤ 指定的载体）

```console
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --workdir /tmp/fairness --undecodable 3
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --tables analysis/verification/synthetic-tables.md
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --analysis-dir <dir with analyze.py>   # 判定"改动前"用
```

退出码：**0** 全部成立；**1** 至少一条断言失败；**2** 输入造不出来/读不到（`make_tree.py` 或 `analyze.py` 非 0 退出、
`--tables` 指向不存在的文件）。默认自己造树（`make_tree.py --undecodable {0,7}` → `analyze.py`），
不依赖真实跑批、不需要网络；`--tables` 用来检查既有产物（负控就是这么做的）。

**规则来自分析器源码本身**：脚本 `sys.dont_write_bytecode = True` 之后 import `analyze.py`（那个被 git 跟踪的
`analysis/__pycache__/analyze.cpython-314.pyc` 因此不会被改写），断言比较的是"源码里的规则 vs 渲染出来的格子"，
不是"记住的字符串 vs 渲染"。**树形状自适应**：§5/§9 是按延迟 class / DNS 分表的，§5 只查 `udp-rtt`/`dns-rtt`
（`tcp-connect`/`tcp-rtt` 对 UDP 无能的行走的本来就是 TCP 数）；非战役形状的树（例如一行的 selftest 树）由
`designed_rows()` 判定"这棵树里没有战役的行"，给 NOTE 而不是假红。

guard 清单（默认树 13 条 / `undecodable > 0` 树 16 条）：

| guard | 断言 | 输入来自 |
|---|---|---|
| `#17/rule` | 源码声明了至少一条 UDP 无能行（否则后续断言空转） | `UDP_INCAPABLE_ROWS` |
| `#17/profile` | §1 的 profile 表把每条 UDP 无能行都印成 `not carried (UDP bypassed)` | §1 + 源码 |
| `#17/section 4` | §4 headline 里该行的每个 UDP 列（列名含 `udp`/以 `dns`/`loss` 开头）都是该串（10 格） | §4 |
| `#17/section 5` | §5 的 `udp-rtt`/`dns-rtt` 两张表里该行的每格都是该串（160 格） | §5 |
| `#17/section 8` | §8 该行的每个非标识格都是该串（36 格） | §8 |
| `#17/section 9` | §9 该行的 4 个 DNS 测量格是该串，`comparability` 说明"UDP bypassed" | §9 |
| `#17/counterweight` | 载 UDP 的行仍然印数字（防止"全标 not carried"这种退化渲染） | §8 + 源码 |
| `#18/section 9` | 每个测量了 53 端口臂的行，其 `UDP/53 carriage` 等于源码自己的 `UDP53_LABEL`；非 relayed 的行必须有"not cross-product comparable"（UDP 无能行则要求排除说明） | §9 + 源码 |
| `#18/coverage` | §9 恰好覆盖"计划里有 DNS 臂"的每一行（6/6） | §1 + §9 |
| `#18/section 8` | §8 里每行的 `UDP/53 carriage` 同样等于该标签（5 行） | §8 + 源码 |
| `#19/scope` | §6 里有 `user-mode only` | §6 |
| `#19/gap` | §6 里点名 kernel / DPC / ISR 之遗漏 | §6 |
| `#11/zero` | 账本 undecodable 为 0 时**没有任何** `undecodable` 披露 | 全表 + 账本重算 |
| `#11/arms` | `undecodable` 这个词只出现在 §14 一个 section 里 | 全表 |
| `#11/table` | §14 里只有一张带 undecodable 列的表（防止摊到臂级表） | §14 |
| `#11/value` | 披露里的最大值 = **从账本 JSONL 独立重算**出来的总量（7） | §14 + 账本 |
| `#11/total-row` | 表的**最后一行**是合计行且等于该总量（合计行写错会被抓住） | §14 |

### 1.4 fixture：`make_tree.py --undecodable N`

`analysis/synthetic/make_tree.py:763` 的 `USAGE`、`:766` 的 `parse_arguments`、`:793` 的 `stamp_undecodable`、
`:1258` 的调用点：`--undecodable N` 把**主流账本**的累计 undecodable 打到 `N`（最后一条 `udpSummary` +
收尾的 `targetSummary/udp`，符合 target 的累计写法），不新增任何臂级记录（"哪个臂损坏的"正是这个计数器
说不了的事）。参数解析改成任意顺序的循环（`--window-overflow` 与 `--undecodable` 都可用，原实现只认
`--window-overflow` 在最前）。**默认树逐字节不变**：同一路径重新生成后
`diff -r /tmp/e3b2/synth-snapshot/raw /tmp/wf-synth/raw` 与两份账本 `diff` 全空。

---

## 2. 红 → 绿

`--analysis-dir /tmp/e3b2/head-analysis`（只放 **HEAD 的 `analyze.py`** 与本批的 `make_tree.py`）vs 工作树：

| 断言组 | HEAD 分析器 | 本批分析器 |
|---|---|---|
| `#17` 7 条 | 全绿（回归护栏：E3-premises §1 第 15 行与 D0.2 都判"已实现"） | 全绿 |
| `#18` 3 条 | 全绿（同上） | 全绿 |
| `#19` 2 条 | **红 2 条**（`#19/scope`、`#19/gap`） | 绿 |
| `#11` 4 条 | **红 4 条**（`#11/arms` 词缺席、`#11/table` 无表、`#11/value`、`#11/total-row`） | 绿 |
| 合计 | **exit 1**（默认树 2/13 红，`undecodable` 树 6/16 红） | **exit 0**（13/13、16/16） |

即"至少一条断言在改动前失败"成立，而且失败的正是本批要做的两件事；`#17`/`#18` 在改动前就绿，是
D0.2 已登记的**回归护栏**（这一点必须如实说：本批没有为它们改任何渲染，只是第一次给它们装上断言）。

```console
# 红（HEAD 的分析器 + 本批 fixture）
$ git show HEAD:benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py > /tmp/e3b2/head-analysis/analyze.py
$ cp benchmarks/results/2026-10-06-e2e-competitors/analysis/synthetic/make_tree.py /tmp/e3b2/head-analysis/synthetic/
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --analysis-dir /tmp/e3b2/head-analysis --workdir /tmp/e3b2/fairness-head
  # exit 1：fixture with 0 undecodable … 2 of 13 guard(s) failed；fixture with 7 … 6 of 16 failed
# 绿（工作树）
$ python3 benchmarks/WinForward.E2E/scripts/check-fairness.py --workdir /tmp/e3b2/fairness
  # exit 0：fixture with 0 undecodable … all 13 guard(s) held；fixture with 7 … all 16 guard(s) held
```

---

## 3. 负控（全部实测，日志在 `/tmp/e3b2/neg/`）

| # | 输入 / 突变 | 期望 | 实测 |
|---|---|---|---|
| **N1** | 默认树 `tables.md` 里 `not carried (UDP bypassed)` → `1234`（**任务书指定的负控**） | 非 0 | **exit 1**：`#17/profile`、`#17/section 4/5/8/9`、`#18/section 9` 共 6 条红 |
| **N2** | 只删掉 `**CPU scope: user-mode only …**` 这个标签句（保留后文） | 非 0 | **exit 1**：`#19/scope` 红 |
| **N2b** | 整段删掉 §6 的 CPU scope 脚注 | 非 0 | **exit 1**：`#19/scope` + `#19/gap` 两条红 |
| **N3** | `proxifyre` 的 UDP/53 标签（`direct via the product's hardcoded port-53 pass-through`）→ `n/a` | 非 0 | **exit 1**：`#18/section 9`（`'n/a' != …`）、`#18/section 8`（同一条臂的 2 行各一次） |
| **N4** | 把 `undecodable` 这个词挪进 §8（臂级小节） | 非 0 | **exit 1**：`#11/arms`（词出现在 section 8、14） |
| **N5** | `undecodable` 树的合计行 7 → 3 | 非 0 | `--tables` 模式**判不了**（该模式没有账本可比，只有 NOTE）⇒ 用分析器突变 **M-a**：**exit 1**，`#11/total-row` 红 |
| **N6** | §8 的 `direct via the local DNS target (…)` → `n/a`（三个 53 端口走直连的行同时中招） | 非 0 | **exit 1**：`#18/section 9` + `#18/section 8`（`'n/a' != …` 逐行列出） |
| **M-a** | `analyze.py` 的 §14.6 合计行渲染成字面量 `"3"` | 非 0 | **exit 1**：`#11/total-row`（`#11/value` 仍绿——逐账本行还写着 7，正是这两个断言分工的地方） |
| **M-c** | `if undecodable > 0:` → `>= 0`（零也打印） | 非 0 | **exit 1**：`#11/zero`（默认树） |

**还原证明**：M-a/M-c 之后 `cp /tmp/e3b2/analyze.py.good analyze.py`，
`md5sum analyze.py` = `af8e0effc13d4534c86fad31d805b2a6`（与突变前相同），随后重跑默认树 diff 与绿断言
（§2、§4 的读数都是还原后取的）。

---

## 4. 零回归 diff 面（默认合成树）

```console
$ A=benchmarks/results/2026-10-06-e2e-competitors/analysis
$ mkdir -p /tmp/e3b2/head-analysis/synthetic
$ git show HEAD:$A/analyze.py > /tmp/e3b2/head-analysis/analyze.py      # HEAD 的分析器
$ cp $A/synthetic/make_tree.py /tmp/e3b2/head-analysis/synthetic/       # 本批的 fixture
$ python3 $A/synthetic/make_tree.py /tmp/wf-synth/raw
$ python3 /tmp/e3b2/head-analysis/analyze.py --raw /tmp/wf-synth/raw --out /tmp/e3b2/head-run
$ python3 $A/analyze.py --raw /tmp/wf-synth/raw --out /tmp/e3b2/after
$ diff /tmp/e3b2/head-run/verdict.json /tmp/e3b2/after/verdict.json          # 空
$ diff /tmp/e3b2/head-run/tables.md   /tmp/e3b2/after/tables.md              # 2 处（下表）
```

| # | 位置 | 变化 | 归属 |
|---|---|---|---|
| 1 | `tables.md:438`（§4 caption） | `(IDLE excluded) and` → `(IDLE excluded), measured as user-mode process CPU only — section 6 states the scope in full — and` | 本批**有意**的 `#19` 披露（指针） |
| 2 | `tables.md:780a781,782`（§6 表后） | 新增空行 + CPU scope 脚注一段 | 本批**有意**的 `#19` 披露（正文） |
| — | `tables.md` §14 | **无** §14.6（默认树 undecodable = 0） | `#11` 的 0 分支 |
| — | `verdict.json` | **逐字节相同** | 本批不写 verdict |

同一份默认树的 `verification/synthetic-tables.md` 已按新分析器**重生成**（`synthetic-verdict.json` 逐字节不变）。
它对 **HEAD 提交里的那份**的 diff 是 **4 行**，其中 2 行是**本批之前就有**的漂移（E3-b1 的
`E3-b1-analysis-fixture` 条目已登记过同类现象，这次顺手闭合）：

| # | 行 | 变化 | 归属 |
|---|---|---|---|
| 1 | 168 | `BASE metrics.loss.window` 的第 4 格 `n/a` → `200.0 [200.0–200.0] ms (n=6)` | **既有漂移**：HEAD 的 `analyze.py` 在同一棵树、同一路径上产生同样的差异（`diff <git show HEAD:verification/synthetic-tables.md> /tmp/e3b2/head-run/tables.md` = 这 2 处） |
| 2 | 875 | §11 REL caption 补回了 `meanConnectMs`/`meanTransferMs`/`byMode` 的说明 | **既有漂移**（同上） |
| 3 | 438 | §4 caption 的 CPU 口径指针 | 本批 |
| 4 | 781 | §6 的 CPU scope 脚注 | 本批 |

`diff /tmp/e3b2/head-run/tables.md verification/synthetic-tables.md` 恰好只有 #3/#4 两处。

**selftest 产物不动**：`verification/selftest-tables.md` / `-verdict.json` 是**更早一次 selftest 树**的快照
（用当前 `/tmp/wf-bench/selftest/out` 重跑得到 `2 harness-error / 3 measurement-caveat`，而产物写的是
`0 / 1`，DNS 端口也不同）⇒ 不可复现，本批不重生成（重生成会把 E2/E3-b1 的分析器演进混进本批 diff）。
对该产物跑 `--tables` 的读数是 **exit 1，且只有 `#19` 的两条**（快照早于本批的脚注），`#17`/`#18` 全绿——
即这条红正是本批有意新增的那 2 行，不是 `#17`/`#18` 的回归。登记见 §7。

---

## 5. 六条门禁与等价性

### 5.1 门禁（串行；本轮**不**与 inspectcode 并发，见 E3b1 §5.1 的血泪登记）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `cd benchmarks/WinForward.E2E && scripts/publish.sh` | **跳过**：本批 `git status --porcelain \| rg -c '\.cs$'` = **0**，二进制与 HEAD 逐位同源；重跑 publish 只能复现 E3-b1 已登记的 sha256，不能证明本批任何东西 |
| 2 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)**（34.8 s） |
| 3 | `dotnet test WinForward.slnx -c Release`（独占，串行） | 15 个程序集全绿，**19.5 s**；`WinForward.E2E.Tests` **Failed: 0, Passed: 286**（= E3-b1 check 轮的 286，无增减）。**check 轮复跑时并行模式在本机因内存不足假红，串行才全绿——见 §8.4** |
| 4 | `cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json` | **跳过**：同 #1 的理由；本批不触碰任何被 selftest 驱动的代码路径（Python 分析器不参与 selftest），且 selftest 的产物形态由 E3-b1 的判据轮覆盖 |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e3b2/jb-inspectcode.xml WinForward.slnx` | 解析 XML：`<Issue>` **0**、`<IssueType>` **0**、无 `CSharpErrors` |

`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests`：**无输出、exit 0**。

### 5.2 与"同二进制噪声地板"的关系

本批**没有**改任何 C#、也没有重跑 selftest，因此不存在可比的 `record`/`ledger` 字节面：E2/E3-a/E3-b1 的
`compare-records.py` 判据不受本批影响（账本 JSONL 由 target 写，分析器只读）。本批的等价性判据是
**分析器产物的 diff 面**（§4）与**断言的红绿**（§2/§3），不是记录契约带宽。

---

## 6. 重建方式（check 轮可逐条重跑）

```console
A=benchmarks/results/2026-10-06-e2e-competitors/analysis
CF=benchmarks/WinForward.E2E/scripts/check-fairness.py
cp $A/analyze.py /tmp/e3b2/analyze.py.good          # 突变前的还原点（md5 af8e0effc13d4534c86fad31d805b2a6）
mkdir -p /tmp/e3b2/head-analysis/synthetic
git show HEAD:$A/analyze.py > /tmp/e3b2/head-analysis/analyze.py
cp $A/synthetic/make_tree.py /tmp/e3b2/head-analysis/synthetic/

# 1) 红：HEAD 的分析器 + 本批 fixture
python3 $CF --analysis-dir /tmp/e3b2/head-analysis --workdir /tmp/e3b2/fairness-head        # exit 1
# 2) 绿：工作树（同样的两条 fixture：undecodable 0 与 7）
python3 $CF --workdir /tmp/e3b2/fairness                                                    # exit 0
# 3) 既有产物
python3 $CF --tables $A/verification/synthetic-tables.md                                    # exit 0
python3 $CF --tables $A/verification/selftest-tables.md                                     # exit 1（仅 #19 两条，见 §4）

# 4) 零回归 diff 面
python3 $A/synthetic/make_tree.py /tmp/wf-synth/raw
python3 /tmp/e3b2/head-analysis/analyze.py --raw /tmp/wf-synth/raw --out /tmp/e3b2/head-run
python3 $A/analyze.py --raw /tmp/wf-synth/raw --out /tmp/e3b2/after
diff /tmp/e3b2/head-run/verdict.json /tmp/e3b2/after/verdict.json                          # 空
diff /tmp/e3b2/head-run/tables.md /tmp/e3b2/after/tables.md                                 # 2 处（§4 表）

# 5) 负控（把绿产物改坏就必须红）
python3 - <<'PY'
clean = open('/tmp/e3b2/fairness/clean/out/tables.md', encoding='utf-8').read()
counted = open('/tmp/e3b2/fairness/counted/out/tables.md', encoding='utf-8').read()
open('/tmp/n1.md','w').write(clean.replace('not carried (UDP bypassed)', '1234'))
open('/tmp/n2.md','w').write(clean.replace('**CPU scope: user-mode only — no kernel-mode work outside the process.**', 'CPU numbers:'))
open('/tmp/n2b.md','w').write(''.join(line for line in clean.splitlines(keepends=True) if '**CPU scope: user-mode only' not in line))
open('/tmp/n3.md','w').write(clean.replace("direct via the product's hardcoded port-53 pass-through", "n/a"))
open('/tmp/n4.md','w').write(counted.replace('## 9. DNS comparability detail', "The LOSS arm's own undecodable datagrams are counted above.\n\n## 9. DNS comparability detail"))
open('/tmp/n6.md','w').write(clean.replace("direct via the local DNS target (udp/53 forwarded verbatim, bypasses the proxy)", "n/a"))
PY
for f in /tmp/n1.md /tmp/n2.md /tmp/n2b.md /tmp/n3.md /tmp/n4.md /tmp/n6.md; do python3 $CF --tables $f > /dev/null; echo "$f exit=$?"; done   # 全 1

# 6) 分析器突变（M-a 合计行写成 3；M-c 零也打印），跑完必须还原
sed -i '0,/fmt_num(undecodable, 0),/s//"3",/' $A/analyze.py && python3 $CF --workdir /tmp/e3b2/fm-a; # exit 1（#11/total-row）
sed -i 's/    if undecodable > 0:/    if undecodable >= 0:/' $A/analyze.py && python3 $CF --workdir /tmp/e3b2/fm-c; # exit 1（#11/zero）
cp /tmp/e3b2/analyze.py.good $A/analyze.py && md5sum $A/analyze.py   # af8e0effc13d4534c86fad31d805b2a6（还原；不要用 git checkout，会把本批改动一起还原）
```

---

## 7. 登记、偏差与未做

| # | 内容 | verdict |
|---|---|---|
| 1 | **合成 fixture 的两账本共享一个累计 `received`**（§1.1 的注）：本批不改（改它会动默认树 §14.2 的数值面）。真实 harness 无此问题 | `observation` |
| 2 | **`analysis/README.md` 未同步**：其 "CPU and memory" 段与 "Judgement calls" 列表还没有 user-mode 口径那一句（§6 脚注是权威文本）。它是 E4 冻结/重写分析包的文件（D12），本批不动 | `observation` |
| 3 | **§5 的"多一格"渲染（既有）**：`table_latency` 对"该臂没有这个直方图 / not carried"的行输出 `2 + 1 + len(PERCENTILES)+1 = 11` 格，而表头是 10 列（`md_table` 不做补齐），markdown 里表现为这些行多一个无表头的尾格。**不是本批造成的**，本批不改（属于会重写大量 §5 行的渲染缺陷，且与 `#11`/`#19` 无关）；`check-fairness.py` 的表格解析按 `zip(header, row)` 截断尾格并在注释里写明，缺点是它抓不到"未来新增的同类多格" | `observation` |
| 4 | **`verification/selftest-*` 未重生成**：更早一次 selftest 树的快照，不可复现（§4 末）。`--tables` 对它的读数是"只有 `#19` 两条红"，即本批有意新增的披露 | `observation` |
| 5 | **`verification/synthetic-*` 已重生成**：闭合 E3-b1 登记的 2 行漂移 + 本批 2 处披露；`synthetic-verdict.json` 逐字节不变 | `fixed` |
| 6 | 本批**未做**（按范围）：E3-c 的 `Truncated`、E3-d 的账本三字段/`--udp-receivers`、E3-e 的 ODE 与 `achievedRate` 统一；`README.md`（harness 侧）的措辞仍归 E5，`#19` 的 README 行本批只登记不写 | `deferred` |
| 7 | **`analysis/__pycache__/analyze.cpython-314.pyc` 是 git 跟踪文件**：任何 `import analyze` / `python3 -m py_compile analyze.py` 都会重写它的字节（本批施工中就被 `py_compile` 覆盖过一次，已 `git checkout --` 还原，blob hash `99b68dc8…` 与 HEAD 相同）。`check-fairness.py` 用 `sys.dont_write_bytecode = True` 规避了 import 路径；E4 冻结 Python 侧时应把该产物移出版本控制或写进 `.gitignore`，否则每次跑分析器都会产生假 diff | `observation` |
| 8 | **`analysis/README.md` 与 `.trellis/spec/backend/measurement-harness.md` 都没登记 `check-fairness.py`**：README 的那张 `scripts/*` 清单本来就只列运行路径脚本（`compare-records.py`/`contract-inventory.py`/`effective-lines.py`/`cli-snapshots.py` 同样不在里面），spec §2 的 Signatures 只在 `compare-records.py`/`contract-inventory.py` 处列工具。不是本批造成的偏差，登记给 E4/E5 决定是否补一行 | `observation` |
| 9 | **并行 `dotnet test WinForward.slnx -c Release` 在这台机器上会假红**（check 轮实测，见 §8.4）：15 个 testhost 并发把 11 GiB 内存打满（swap 942/945 MiB），两个用例 `System.OutOfMemoryException`（`Thread.StartCore` → `SetupExecutor.EnsureWorkers`，与产品逻辑无关），随后一个 testhost 崩溃把整轮挂住（28 min 无进展）。串行 `-m:1` 全绿。建议 E4/E5 把"判据轮用串行"写进 spec 的门禁一节 | `observation` |

---

## 8. trellis-check 轮（独立复核 + 两处修复）

复核不采信本文任何读数，全部按 §6 的配方重跑；命令与产物在 `/tmp/e3b2-check/`。**`analyze.py` 与 `make_tree.py`
在本轮一行未改**（md5 仍为 `af8e0effc13d4534c86fad31d805b2a6` / `ef8db633bbe4b3e649b2a48597dce295`）。

### 8.1 逐条独立复核（全部通过）

| 判据 | 复核读数 |
|---|---|
| `undecodable` 只做总量、不摊到臂 | 在计数值树上直接 `import analyze` 调 `ledger_views(ctx)`：**255 条 `per_arm`** 的键集不含任何 `undecodable`/含 `decode` 的字段，整个 `ledger_views` 的 JSON 里该词**零命中**；`build_tables_md` 的全文里该词只落在 §14（3 次，§14 之外 0 次） |
| 0 ⇒ 不出现 | 干净树全文该词 **0 命中**；`corruptRate` 仍照常渲染（干净树 7 处，计数值树 8 处 = 7 + §14.6 正文提到它一次），公式与列一行未动 |
| 零回归 | 同一棵 `/tmp/wf-synth/raw`：两侧 `verdict.json` md5 同为 `dbf5e9f2e4fe078b829e560250f0ee88`；`tables.md` 只差 `438`（§4 caption）与 `780a781,782`（§6 脚注）两处 |
| 2 行漂移的归属 | `git show HEAD:…/verification/synthetic-tables.md` vs **HEAD 分析器**在同一棵树上的输出 = 恰好 `168` 与 `875` 两行（既有漂移）；HEAD 分析器 vs 工作树产物 = 恰好 `438` 与 `780a781,782`（本批）；工作树产物 vs 新分析器的一次全新重生成 = **逐字节相同**；`synthetic-verdict.json` 与 HEAD 提交版逐字节相同 |
| 红 → 绿 | 对 HEAD 分析器：0 树 **2/13** 红、7 树 **6/16** 红，exit 1；对工作树：**13/13 + 16/16**，exit 0 |
| CPU 口径与代码一致 | `Client/ProcessCounterSource.cs:153` 读的确实是 `Process.TotalProcessorTime`；target 侧 `_undecodable` 是实例级累计值（`Target/UdpEchoServer.cs:22,63,96,148`，`Interlocked.Increment` + 两种记录都写累计），所以"按账本取最大值 = 该实例生命周期总量"成立 |
| `__pycache__` 未被本批改动 | `git status` 无该路径；`git cat-file -p HEAD:…analyze.cpython-314.pyc \| md5sum` = 工作树文件 md5 = `8e7a7bd089aba43d6f62c645ac75b714`（blob `99b68dc8…`） |

### 8.2 复核轮新增的负控（与作者 N1–N6 不同）

| # | 突变 | 期望 | 实测 |
|---|---|---|---|
| **C-1** | §9 里 `wf-aot-dnsrelay` 的 `relayed through the proxy` → 数字 `53`（作者的负控换的是 `not carried` 标记或 direct 行的 `n/a`；这条换的是 **relayed** 行、且换成数字） | exit 1 | **exit 1**：只有 `#18/section 9` 红（`'53' != 'relayed through the proxy'`）。输入 md5 `15ca4c19c19f4f9135dddbe5fda9a4ab` |
| **C-2** | 把 §14.6 **整块**（标题+正文+表+收尾段，12 行）从 §14 **搬到** §8（作者 N4 只是把词插进 §8，§14.6 仍在原位） | exit 1 | **exit 1**：`#11/arms` 红（词出现在 section 8，§14 里反而没有）。输入 md5 `8f4503279176c56afe5ce4655dcd39be` |
| **C-3** | fixture 接受 `--undecodable 7` 但 `stamp_undecodable` 空转（fixture 退化） | 应红 | **修前 exit 0**：第二条断言集从 16 条静默退化成 13 条（`(ledger total 0)` 是唯一破绽）；**修后 exit 2** + 明确消息（§8.3-F2）。突变副本 md5 `beb1040d28b0132050f8f49ef0196de3` |

**`--tables` 模式的 N5 局限（复核确认，作者已如实登记）**：把 §14.6 整块从一份计数值 `tables.md` 删掉，
`--tables` 只打印 `NOTE #11: no undecodable disclosure in this file` 并 **exit 0** —— 该模式没有账本可比，
分不清"干净账本所以不披露"与"计数值账本漏披露"（探针输入 md5 `6e0615da72926d126f4c101fda5806ca`）。
默认模式（自己造树）没有这个洞：`expected` 永远来自账本重算，`> 0` 时四条 `#11` 断言全部生效。
可选加固（本批未做，登记）：给 `--tables` 加 `--expect-undecodable N`。

### 8.3 复核轮的两处修复（都在 `check-fairness.py`，断言条数不变：仍 13/16）

| # | 问题 | 修复 |
|---|---|---|
| **F1** | 文件自己的退出码契约写"**2** = 输入造不出来/读不到"，但 `repository_root`/`resolve_analysis_dir`/`run` 三处用 `raise SystemExit("<字符串>")`，Python 对字符串参数一律 **exit 1**：实测"`--analysis-dir` 指向没有 `analyze.py` 的目录" exit 1、"fixture 起不来"（HEAD 的 `make_tree.py`）exit 1 | 新增 `unusable(message)`（stderr + `raise SystemExit(2)`），三处改调它。实测三条路径现在都是 **exit 2**；`--undecodable -1` 与 `--tables <不存在>` 原本就 2，不变 |
| **F2** | fixture 退化不会失败：`make_tree.py` 若不再盖 `undecodable` 章，第二条 fixture 的重算总量变 0，`#11` 四条断言**静默**退化成零值分支，整轮 exit 0（C-3） | `fixture_tables` 里"要求了 N>0 而账本重算总量为 0"⇒ `unusable(...)`（exit 2，符合"输入造不出来"的口径） |

修复后重跑：默认 **13/13 + 16/16（exit 0）**、HEAD **2/13 + 6/16（exit 1）**、C-1/C-2 仍 exit 1、
N5 探针仍 exit 0 + NOTE、`--tables` 对 `synthetic-tables.md` exit 0 / 对 `selftest-tables.md` exit 1（只 `#19` 两条）、
`--undecodable 0` 显式传参 exit 0。脚本自身 md5 `fa386ba3a8854ddd9935ad96636bb6bc`、**554 行**（§1.3 的 542 行是修复前的）。

### 8.4 门禁复核（串行：build → test → format → inspectcode，互相不并发）

| # | 门禁 | 复核读数 |
|---|---|---|
| 1 | `scripts/publish.sh` | **跳过成立**：脚本只做 `dotnet build -c Release` + 三次 `dotnet publish`（28 行，无任何非 C# 输入），本批 `git status --porcelain \| rg -c '\.cs$'` = 0，产物与 HEAD 逐位同源 |
| 2 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)**（8.2 s） |
| 3 | `dotnet test WinForward.slnx -c Release` | **必须串行**：并行跑在本机假红（15 个 testhost 打满 11 GiB 内存，swap 942/945 MiB；`WinForward.Performance.Tests.UdpAdaptiveSweepAllocationGateTests` 与 `WinForward.Runtime.Socks5.Tests.Socks5UdpAssociateTests` 各一条 `System.OutOfMemoryException`，栈顶是 `System.Threading.Thread.StartCore` → `SetupExecutor.EnsureWorkers`；随后一个 testhost 崩溃，vstest 等了 28 min 无进展）。**串行 `-m:1`：14 个测试程序集全绿，Failed 0 / Passed 1585 / Total 1585，exit 0**；其中 `WinForward.E2E.Tests` **286/286**（= E3-b1 的 286，无增减） |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | **跳过成立**：它驱动的是同一批二进制（`publish.sh` 的产物）与未改动的 `selftest-plan.json`，且内联 Python 只打印记录、不跑分析器；本批不触碰任何被 selftest 驱动的路径。**残留**：`verification/selftest-*` 快照不可复现（§7-4），仍未重生成 |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e3b2-check/jb.xml WinForward.slnx` | 报告 `<IssueTypes />` 与 `<Issues />` **都是空元素**（0 条），无 `CSharpErrors`；exit 0 |

`scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests`：无输出、exit 0。

### 8.5 复核轮的裁定（不改的地方）

| # | 裁定 |
|---|---|
| **CPU 标题不改** | §6 的标题句 `user-mode only — no kernel-mode work outside the process` 与 §4 的指针保留不动。理由：同一段正文立刻把口径写全（"`Process.TotalProcessorTime`：OS 记在**该进程线程**上的时间 = 用户态 + 这些线程自己的 privileged/系统调用时间"），破折号从句把 `only` 限定在"**进程之外**的内核工作"，§4 又明说"section 6 states the scope in full"；这条 `user-mode only` 是 D19.2 ⑩ 的原样口径，也是 `#19/scope` 断言的标记串。改标题会同时改断言与两处已登记的文本，收益只是措辞 |
| **`--tables` 的 N5 不加 `--expect-undecodable`** | 作者已如实登记该局限，且默认模式无此洞；加固属可选，留给 E4 决定 |
| **stale `selftest-*` 快照不重生成** | 与 §7-4 同：快照来自更早的树（复核实测当前 `/tmp/wf-bench/selftest/out` 重跑是 `2 harness-error / 3 measurement-caveat`、DNS 端口 5301 **与 5302**，而产物写的是 `0 / 1`、只有 5301），重生成会把分析器演进混进本批 diff |
