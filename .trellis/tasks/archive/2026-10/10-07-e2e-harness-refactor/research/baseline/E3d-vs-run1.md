# E3-d 回归比对与定向证据（账本 `detail`/`acceptErrors`/`udpReceivers` + `--udp-receivers`）

本批是 E3 的第四批（`10-07-e2e-e3-semantics/implement.md` 的 E3-d 节，D19.2 ⑬/⑥、D19.3 A/B）。
**`src/` 一行未改，本轮未提交。**

判定线（逐条见 §5）：三项字段各有**能移动**的反证（10 条突变全部被杀）；账本键只有新增
（`contract-rename.json` 599 → 604，**只有 5 行 added，既有行逐字未变**）；跑批比对
`contract=0`（零越带）、结构差异只剩 E3-b1 已登记的三条 note 更正；零宽键 39 对三跑逐值相同；
`--strict` 读数摘要见 §6.6；六条门禁 + `effective-lines.py` 全绿（§6.1）。

> **本批没有改任何已发布数值**：账本 110 条路径里 88 条三跑同值，6 条是本批新键，16 条"移动"的
> 全部是时钟/身份值（`utc`、`startedTicks`/`endedTicks`、`peer` 的端口、`udpSummary::ticks`）或
> E3-c 自己的 `truncatedFrames`（run1 缺席、两个 post 都是 0）。**零个既有计数器移动。**

---

## 0. 结论一句话

账本多了三件事：写不成原本那条记录的失败以第五个记录族 `error` 落账（`detail` = 被包裹异常的
**最内层**类型名）、两条 accept 循环的**被拒计数** `acceptErrors` 在两个 listener 家族的两层各自
发布、UDP echo listener 的 `udpReceivers` 记的是**实际启动的接收循环数**而不是配置值；靶机新增
`--udp-receivers <n>`（未声明 = 老公式，非法 = 退出码 2），CLI 快照新增三条拒绝路径并重采
`after/`（登记在 `research/cli-snapshots/INTENTIONAL.md` §3/§4）。

---

## 1. 账本三字段的落点与反证

### 1.1 `detail`：第五个记录族 `error`

| 处 | 落点 |
|---|---|
| 常量 | `ArmKeys.Ledger.ErrorRecord.Detail`（**新分片**；与 `ArmKeys.Common.ErrorRecord.Detail` 同拼写、不同层，D14.17） |
| 族名 | 记录自己的 `type` = `"error"`（值，不是键：`LedgerShapeTests` 的注释写明 kind 是值，D14.16） |
| 写点 | `TargetRunner.WriteErrorRecordAsync`（新，internal），两个守卫调用：`TargetRunner.WriteSummaryAsync` 的 catch 与 `TcpTargetServer.HandleConnectionAsync` 的 catch（连接记不上） |
| 语义 | `failure.GetBaseException().GetType().Name` —— 与 `ArmRecordWriter.cs:66` 同一派生；但账本里**族名本身就是 `error`**，所以只留一个叶，不重复发布外层类型名 |
| 失败面 | 写这条失败只进 `ledgerWriteErrors` 并再报一句 stderr，**不上抛**（D14.7 的吞/计语义不变）；`targetSummary/ledgerWriteErrors` 键名与语义逐字未动 |

**反证（实测）**：生产守卫里抛 `new InvalidOperationException(msg, new SocketException(ConnectionReset))`
⇒ `detail == "SocketException"`，且该记录里**没有任何字符串**是 `"InvalidOperationException"`
（`LedgerShapeTests.TheErrorRecordNamesTheInnermostCauseOfTheFailure`）。突变 M1 把派生换成
`failure.GetType().Name`（发布外层类型名）⇒ 该事实红。

### 1.2 `acceptErrors`：两个家族两层，1 个计数点

| 处 | 落点 |
|---|---|
| 计数点（1 处） | `TcpAcceptLoop.RunAsync` 的 `catch (SocketException)`：`Interlocked.Increment(ref _acceptErrors)` 后 `continue`（**仍是重试而非停服**，只让"不能再 accept 的 listener"可见） |
| 两层常量（4 个） | `TcpSummary.AcceptErrors` + `TargetSummary.TcpTotals.AcceptErrors`；`DnsSummary.AcceptErrors` + `TargetSummary.DnsTotals.AcceptErrors` |
| keyset（2 个） | `TcpTotalsKeys`、`DnsTotalsKeys` 各加一个属性 + 两个实例的参数（声明序 = ctor 参数序 = 写序） |
| writer（2 个） | `TcpTargetServer.WriteTotals`、`DnsServer.WriteTotals`（都读各自的 `_acceptLoop.AcceptErrors`） |
| 干净运行 | 键在且 0（两台 server、两层、两次 selftest 都是 0，§6.4） |

**反证（实测，成对）**：
- `LedgerShapeTests.ARefusedAcceptIsCountedInItsOwnListenersFamilyAtBothLevels`：给 TCP 与**第二个**
  DNS listener 各喂一个「前两次 accept 被拒、之后是真的」的脚本化 accept（D14.18 的喂入接缝：
  一次 accept 会不会被拒由内核决定，见 §7-3），第一个 DNS listener 用真 listener ⇒ 两族 `>= 1`
  且与各自 target 层**逐值相等**，第一个 DNS listener 恰为 0（证明是两个 listener 两个数）。
- `LedgerShapeTests.ARefusedAcceptIsCountedAndTheListenerKeepsServing`：拒绝之后仍然接受一条**真连接**
  （`handled` 被触发），计数保持 2 ⇒ "计数不改行为"。
- 突变：M2（`Increment` → `Add(…, 0)`）红 2 条；M3（tcp 侧写常量 0）红 1 条；M4（dns 侧写常量 0）红 1 条。

### 1.3 `udpReceivers`：实际启动的接收循环数

| 处 | 落点 |
|---|---|
| 计数点 | `UdpEchoServer.ReceiveLoopAsync` 在**进入自己的 receive 调用之前**给自己那一格 `Interlocked.Increment(ref _receiverStarts[index])` |
| 发布值 | `StartedReceivers` = 这些格子的**和**（"接收者计数之和"），写进 `TargetSummary.UdpTotals.UdpReceivers`（`targetSummary/udp/udpReceivers`） |
| 为什么不写配置值 | 启动参数是"打算起几个"，观测值才能让"一个循环都没起来"读成 0 |

**反证（实测）**：`LedgerShapeTests.ThePublishedUdpReceiverCountIsTheLoopsThatStarted` 用
`Receivers = 13`（**高于默认公式的上限 8**，任何默认都不可能凑巧相等）：跑之前和为 0、跑完和为 13、
账本键等于 13。突变 M5（发布 `_receiverCount` 而不是观测和）⇒ 空状态的 0 变 13，
`AZeroCounterKeepsItsKeyAndNoLedgerValueIsNull` 红；突变 M6（循环不再 book 自己的启动）⇒ 4 条事实红
（形状测试 + 本事实 + 端到端的两条）。

---

## 2. `--udp-receivers`：语义、默认、上限与错误文本

| 输入 | 行为 |
|---|---|
| 未声明 | **今天的行为原样**：`Math.Clamp(ProcessorCount / 2, 2, 8)`（老 `TargetRunner.cs:21` 的公式搬进 `TargetOptions.DefaultUdpReceivers`，`TargetRunner` 改读 `options.UdpReceivers`） |
| `<n>`（1..64） | 每个收数据报的 listener 起 n 个接收循环：UDP echo 与**每台** DNS（`TargetRunner` 把同一个数传给 `UdpEchoServer`/`DnsServer`/`DnsServer(alt)`） |
| 非十进制数（`abc`、`1.5`、`-1`） | 退出码 **2**，stderr `e2e target: 'abc' is not a receive-loop count` |
| `0` / `65`（≤0 或 >上限） | 退出码 **2**，stderr `e2e target: the udp receive-loop count must be in the range 1..64` |

- 上限 `TargetOptions.MaxUdpReceivers = 64` 是**常量**，拒绝文本与解析用同一个数
  （`TargetOptionsTests.TheUdpReceiverBoundIsTheOneTheRefusalNames` 钉住这一点）；选择理由见 §7-4。
- 反证：M7（解析后被忽略）⇒ 端到端事实红；M8（默认公式 8→4）⇒ 解析层与端到端两条默认事实红；
  M9（删掉区间拒绝）⇒ 两条拒绝事实红；M10（help 少一行）⇒ 两条快照回放事实红。
- **端到端**：`UdpReceiverOptionTests` 起一台**整的**靶机（`TargetRunner.RunAsync` + 四个 listener +
  真数据报 + 真账本文件），断言 `--udp-receivers 3` ⇒ 账本 `udpReceivers == 3`、未声明 ⇒ 公式值。
  这条事实补上了"解析了但没接上"的盲区（M7 就是照着它设计的）。
- **CLI 快照**：清单 28 → **31** 条，新增三条拒绝路径（只进 `after/`，因为 `before/` 那个二进制只会说
  `unknown argument`）；`after/` 用本批发布的二进制整体重采，`before/` 不动。两棵树 `diff -r` 的结果
  恰好是：**8 个 target help 的 stdout**（§7-8 登记的那两行）+ `index.json` + 三条新 case 的 9 个文件，
  其余逐字节相同。逐字文本见 `INTENTIONAL.md` §3/§4。

---

## 3. 账本键扩容的登记（D19.3 A）

```console
$ python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename \
      --baseline $R/baseline/run1 --run /tmp/e3d/post1 \
      --out-json $R/contract-rename.json --out-md $R/contract-rename.md
renames landed in this run: 9; still published under the old spelling: 0; not observed at all: 0
$R/contract-rename.json: 604 row(s) = added 15, identical 580, renamed 9
declared but not observed in the fresh run: 2
```

- JSON 差量：**只有 5 行新增**（`acceptErrors`、`tcp/acceptErrors`、`dns/acceptErrors`、
  `dnsAlt/acceptErrors`、`udp/udpReceivers`；`only in old` 空、既有行 reason 逐字未变）。
  MD 差量：`fresh run`/`rows` 两行 + `## Added` 表 5 行。
- **`detail` 没有第二行**：它在 canonical path 上就是客户端 `error` 记录的那个 `detail`
  （E3-b1 已登记为 added），账本 `error` 族发布的是同一条路径 —— 这正是"只增"的形状：
  路径集合只有 5 条新路径，但账本多了一个族。
- 5 条新路径全部在 post1 里被观测到；`declared but not observed: 2` 仍是 `detail`/`error`
  （干净跑批不写 error 记录，双臂都一样）。
- `--batch B2` 仍是 `9/9 satisfied`（改名行不带本批的 batch 字段，`added` 按既有惯例不带 batch）。

---

## 4. 环境键：裁定与登记（**不实现**）

前提复核条目 17 的"环境键"半（`environment.json` 补 `tcpSummary.truncatedFrames`/
`dnsSummary.truncatedFrames`）经父代理裁定**不做**，理由是前提本身失效：

1. 真值只有**靶机**知道，而 `environment.json` 由 `scripts/orchestrator.ps1:415-433` 在**客户端 VM**
   上、**跑批之前**写出；本机与跨机都没有把靶机账本送过去的通道，客户端 C# 也零 ledger 引用
   （`rg -n ledger benchmarks/WinForward.E2E/Client` 无输出）。所以那个文件里只能出现**第二个真相**
   （靶机另写一份）或**恒 null 的键**（编排侧填），两者都违反 E3 PRD 的"不留无法移动的计数器"。
2. E3-c 已把真值写进账本的**两层**（`tcpSummary/truncatedFrames`、`dnsSummary/truncatedFrames`、
   `targetSummary/tcp|dns|dnsAlt/truncatedFrames`），而分析器读的正是账本 —— 消费点因此归**报告侧**。
3. 归属 **E4**（D19.3 H：分析器改动必须早于 golden 冻结）：要求"账本 `truncatedFrames > 0` 时，
   报告必须在数据质量小节披露该靶机/该 pass 的截断计数，与 `undecodable` 同级、**不摊到臂**；
   synthetic 树由 `make_tree.py` 注入非零值"已逐字写进
   `.trellis/tasks/10-07-e2e-e4-analyzer/prd.md`（范围 + 验收标准）。
4. 台账：`research/semantic-fixes/index.jsonl` 的 `E3-d-environment-keys-ruled-out`（verdict `deferred`），
   并保留 E3-c 的 `E3-A2-truncated-deferred-halves` 作为历史条目。

---

## 5. 十条突变（逐条实测；每条都校验还原后 sha256 逐位相同）

装置：`/tmp/e3d/mutate/mutations.py`（M1–M8：打补丁 → `dotnet build` → `dotnet test` 整个 E2E 工程，
每条测试都带超时 → `cp` + `touch` 还原 → 逐文件 sha256 校验）与 `/tmp/e3d/mutate/run2.py`
（M9/M10，带 `--filter`，见下）。十条全部实测，还原后**六个文件逐位相同**。

| # | 突变 | 杀它的断言 | 实测 |
|---|---|---|---|
| M1 | `detail` 发布外层类型名（`GetType()` 取代 `GetBaseException().GetType()`） | `LedgerShapeTests.TheErrorRecordNamesTheInnermostCauseOfTheFailure` | **红 1 条** |
| M2 | 被拒的 accept 不再计数（`Increment` → `Add(0)`） | 计数点事实 + 账本两族事实 | **红 2 条** |
| M3 | tcp 家族的 `acceptErrors` 写常量 0 | `ARefusedAcceptIsCountedInItsOwnListenersFamilyAtBothLevels` | **红 1 条** |
| M4 | dns 家族的 `acceptErrors` 写常量 0 | 同上（DNS 那一半） | **红 1 条** |
| M5 | `udpReceivers` 发布配置值而不是观测和 | `AZeroCounterKeepsItsKeyAndNoLedgerValueIsNull`（空状态读成 13） | **红 1 条** |
| M6 | 接收循环不再 book 自己的启动（`Add(0)`） | 形状测试的 0/13 + 端到端两条默认/声明事实 | **红 4 条** |
| M7 | `--udp-receivers` 解析后被忽略（`workers` 换成老的硬编码公式） | `UdpReceiverOptionTests.TheDeclaredReceiveLoopCountIsTheOneTheRunStartsAndPublishes` | **红 1 条**（+1 条宿主分配抖动，见下） |
| M8 | 默认公式 8 → 4 | 解析层的公式事实 + 端到端的默认事实 | **红 2 条** |
| M9 | 删掉区间拒绝（`receivers is < 1 or > MaxUdpReceivers` → 恒假） | 两条拒绝事实（**用 `--filter` 跑**，理由见下） | **红 2 条** |
| M10 | `target --help` 少掉 `--udp-receivers` 两行 | `CliSnapshotTests` 的两条回放事实 | **红 2 条** |

> **宿主分配门禁的抖动（登记，非本批缺陷）**：整解跑的两轮突变里各出现一次
> `LaneEngineAllocationGateTests.TheSendPathAllocatesNoManagedBytesOnTheCallersThread`（M3 与 M7 那两轮），
> 而**未突变**的树上同一程序集连着四次整解全绿（312/312）。这与 `hot-path.md` §6 的"宿主残余分配抖动"
> 是同一类（E3-c §5.1 也登记过 `WinForward.Performance.Tests` 的同类一次），本批未碰任何分配路径。

> **M9 的装置注意（登记，非本批缺陷）**：删掉区间拒绝之后，快照的两条新 case（`--udp-receivers 0` /
> `65`）不再是拒绝，`CliSnapshotTests` 回放 `Program.Main` 时会**真的把靶机跑起来**并一直
> 听下去（没有超时，因为 `Program.Main` 不接受令牌）——测试**挂住**而不是失败，实测在仓库根留下
> `target-ledger.jsonl`（默认账本路径，775 条 1 Hz `udpSummary` 记录、140 KB，已删除）。所以 M9 用
> `--filter FullyQualifiedName~TargetOptionsTests` 读，挂住这件事本身登记为快照清单的既有性质：
> **清单的每条命令都必须是"拒绝/帮助"**，一条被改成可运行的命令会让回放失去终止性
> （check 轮若要重跑 M9，务必带 filter 与超时）。

**还原**：`TargetRunner.cs`、`TcpAcceptLoop.cs`、`TcpTargetServer.cs`、`UdpEchoServer.cs`、
`DnsServer.cs`、`TargetOptions.cs` 六个文件在全部突变后逐文件 sha256 与开工前相同
（`mutations.py` 结尾的 restore check 与 `run2.py` 的 `restored: True`）。

---

## 6. 六条门禁与等价性

### 6.1 门禁（本批冻结树，逐条串行）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | 14 个程序集全绿，`rc=0`；`WinForward.E2E.Tests` **312**（E3-c 的 297 + 本批 15） |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出** |
| 4 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e3d/gates/4-jb.xml WinForward.slnx`（单独跑） | `<Issue>` **0**、`<IssueType>` **0**（第一次 4 条，逐条处置见 §7-11） |
| 5 | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3d/pub scripts/publish.sh` | exit 0；三份产物；`linux/WinForward.E2E.dll` sha256 `c086fb30814d744686d960dcce6c5180a445bd4e37f2c5b53addc8bfd6c4ba65` |
| 6 | `WF_PUB=/tmp/e3d/pub scripts/selftest.sh scripts/plans/selftest-plan.json` ×2 | 两次 **exit 0**（post1 / post2；就是 §6.2 的两栏读数） |

`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E
benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests`：**无输出、exit 0**（第一次红在
`LedgerShapeTests.cs` 475 行，拆分见 §7-10）。

> **注（门禁之间的迭代，登记）**：`jb inspectcode` 第一次跑出 4 条（`MemberCanBePrivate.Global`、
> `InvalidXmlDocComment`、`RedundantUsingDirective`、`RedundantCast`），逐条改完后第二次干净（§7-11）；
> 第 7 条门禁第一次红（有效行 475 > 400），拆成两个文件后转绿（§7-10）。两次都发生在**同一批的工作树**上，
> 不是在"冻结之后又改"：上表的六条与 §6.2 的两次 selftest 都是在这两个修复之后重跑的（同一二进制哈希）。

### 6.2 判据比对（`compare-records.py`）

```console
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e3d/post1 \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
```

| 对照 | structural | conditional | identity | declared | contract | rename | readings | exit |
|---|---|---|---|---|---|---|---|---|
| run1 → post1（**判据**） | **3**（E3-b1 §4 的三条 note，本批零新增） | 0 | 0 | **39** | **0** | 0 | 296/366（227 越带） | 1（仅那三条 note） |
| post1 → post2（同二进制噪声地板） | 0 | 0 | 0 | 0 | **25** | 0 | 286/366（124 越带） | 1 |

两个 post 都是**同一个已发布二进制**跑的：`WinForward.E2E.dll` sha256
`c086fb30814d744686d960dcce6c5180a445bd4e37f2c5b53addc8bfd6c4ba65`（§6.1 第 5 条重采后逐位相同）。

- **第 5 栏 `declared=39`** = 改名命中 27 + E3-b1 的 3 个 `sentOutOfRangeSequences` + E3-c 的 4 个
  `truncatedFrames` + 本批 5 个（`acceptErrors`、`tcp/acceptErrors`、`dns/acceptErrors`、
  `dnsAlt/acceptErrors`、`udp/udpReceivers`）；`added observed/not observed` 里"not observed"仍只有
  `detail`/`error`（干净跑批不写 error 记录）。
- **第 1 栏三条**与 E3-b1/E3-c 逐字相同（`records/{LOSS,BASE,MIX}.jsonl` 的 `notes`），不是本批造成的。
- **第 4 栏 run1→post1 = 0**：没有任何契约路径越带；噪声地板的 25 条**全部**是
  "no recorded jitter band for this path"（本批 5 条新键 + E3-c 的 4 条 + E3-b1 的 16 条族），
  **没有一条是"值动了"**。

### 6.3 账本逐路径值表（110 条）

`/tmp/e3d/ledger-values.py`（读 run1/post1/post2 的 `ledger.jsonl`，逐记录族逐路径比较）：

| 结果 | 条数 | 明细 |
|---|---|---|
| 三跑同值 | **88** | 含 `tcpSummary/{connections,bytesEchoed,protocolErrors,verdicts/*}`、`dnsSummary` 的全部查询/回答计数、`targetSummary/*` 的全部既有键、`udpSummary` 的计数器与 `sourceOverflow` |
| 本批新键（run1 缺席） | **6** | `tcpSummary::acceptErrors`、`dnsSummary::acceptErrors`、`targetSummary::tcp|dns|dnsAlt::acceptErrors`、`targetSummary::udp/udpReceivers`（canonical path 5 条，按记录族摊开是 6 行） |
| 移动 | **16** | **全部**是时钟/身份（`utc`、`startedTicks`/`endedTicks`、`tcp::peer` 的端口、`udpSummary::ticks`）或 E3-c 的 `truncatedFrames`（run1 缺席、两个 post 都是 0） |

### 6.4 selftest 的新键读数（无误报）

```console
post1: targetSummary/udp/udpReceivers=8  tcpSummary/acceptErrors=0  targetSummary/tcp/acceptErrors=0
       dnsSummary/acceptErrors=[0,0]     targetSummary/dns|dnsAlt/acceptErrors=[0,0]   error 记录 0 条
post2: 同上（逐值相同）
```

- `8` 是本机 `ProcessorCount/2` 被夹到 8 的结果（默认公式），两次相同 ⇒ 干净运行下"实际启动的
  循环数"就是配置的那个数；`acceptErrors` 在两台 server、两层、两次运行上都是 0 ⇒ 干净
  half-close / 客户端行为没有被误判成 accept 失败（阳性对照在 §1.2 的单测里）。
- 判决分布与 run1 相同（`clean 61 / reset 25 / partialFin 25 / halfClose 25 / clientClosedEarly 21`），
  目标层的 `connections=157`、`protocolErrors=0`、`truncatedFrames=0` 逐值未变。

### 6.5 零宽发布键（D18.5 #12）

```console
$ for d in $R/baseline/run1 /tmp/e3d/post1 /tmp/e3d/post2; do python3 /tmp/e3a/zerowidth.py $d/out; done | diff
（无输出）
```

**39 个 (记录, 路径) 对 / 19 条不同路径**，三跑逐值相同（与 E3-b1/E3-c 完全一致）。

### 6.6 `--strict` 读数摘要

- run1 → post1：`readings=296/366`，227 条越带 —— 与 E3-c 的 291/366（210 越带）同量级，
  差异全部落在宿主噪声的读数列（CPU 秒、`ticks`、`wallSeconds`、`achievedRate` 的小数尾）。
  **本批没有任何读数路径被本批触碰**：新键是计数类，`analyze.py` 不渲染它们。
- post1 → post2：`readings=286/366`，124 条越带 ⇒ 越带条数是宿主噪声而不是本批的位移
  （同一棵树的三次噪声地板读数里，越带条数自己就在 124–180 之间摆动）。

---

## 7. 偏离与登记

1. **`acceptErrors` 落在两个 listener 家族而不是 `targetSummary` 的一个键**：任务是"两层常量 + keyset"
   的形状（E3-c 的形状），审计 §5.6 也点名 `tcpSummary`/`dnsSummary` 都缺它；两台 server 各有自己的
   accept 循环，合成一个数会丢掉"哪台 listener"。`TargetSummary` 侧仍然有它（一 level down 的两个块）。
2. **`udpReceivers` 落在 `targetSummary/udp`（udp 块里）而不是 `targetSummary` 根**：`udp` 块就是
   UDP listener 自己的块（`received`/`bytes`/`sendErrors`），并发度和它同层最好读；这与
   `dnsTotals.port`（配置值跟着它所在的 listener 块）同形。任务原文"写进 targetSummary"满足。
3. **`acceptErrors` 的驱动是脚本化的 accept 而不是真的 EMFILE**：一次 accept 会不会被拒由内核决定，
   所以按 D14.18 的喂入接缝把「拒绝」脚本在 accept 调用处（前两次抛
   `SocketException(TooManyOpenSockets)`，之后交回真 accept）。计数点、keyset、writer、发布键、
   两层读数全是真的；差异只在"拒绝是造的"这一点上，而这正是 `test-stability.md` §2.4/§2.5 允许的形状。
   实测过的一条真替代（Linux 上 `shutdown()` 一个 listening socket 之后 accept 会返回
   `EINVAL`）**没有采用**：它在 Windows 上不给同样的错误（`Socket.Shutdown` 对 listening socket
   返回 `WSAENOTCONN`），会让套件变成平台相关。
4. **`--udp-receivers` 的上限取 64**（任务留了口子）：默认公式的上限是 8，64 足以把并发度调到
   远超核数做敏感性实验，又小到打错一个数量级会被拒绝；它是 `TargetOptions.MaxUdpReceivers` 常量，
   拒绝文本从同一个数生成。
5. **`error` 族的第二个写点在真实靶机上不可达**（登记）：`TcpTargetServer.HandleConnectionAsync` 的
   守卫只在 `WriteConnectionAsync` **抛出**时触发，而账本走 `SwallowAndCount`，I/O 失败被 sink 吞掉
   并计数、body 失败才抛。行为证据只在第一个写点（summary 守卫）上，第二个写点保留为"一旦发生就
   留痕"的兜底 —— 与 E3-c 的 `ReliabilityExchange default:` 同型（行为装置 + 源码/类型装置）。
   台账：`E3-d-error-record-reachability`（verdict `observation`）。
6. **审计 §5.5（`TcpVerdict.Error` 没有原因字段）本批只闭合了一半**：现在"连接记不上账"会留一条
   `error` 记录（含 `detail`），但"连接以 `Error` verdict 结束"（停机取消/套接字故障）仍然只有
   verdict、没有原因叶。任务把 `detail` 定义成 **`error` 记录**的叶（前提复核 D1 的"新增 `error` 记录族"），
   所以没有给 `tcp` 记录加条件叶 —— 那会引入一个"有时在、有时不在"的声明（与账本
   "每个声明的键每条记录都写"的 `<remarks>` 冲突），留给 check 轮裁定是否值得。
7. **环境键不做**（§4）：父代理裁定，只登记 + 把要求写进 E4 的 PRD。
8. **CLI 快照的两处有意变更**（`INTENTIONAL.md` §3/§4）：help 多两行（`--udp-receivers`），
   三条新 case **只存在于 `after/`**（`before/` 那个二进制只会说 `unknown argument`，用它当冻结文本会把
   "这个选项不存在"冻成契约）。为此 `CliSnapshotTests` 的登记机制从"一条 `Replace`"扩成
   "两条登记项 + 一条只回放 `after/` 独有 case 的事实"，并在类 `<remarks>` 里写明。
9. **测试基础设施的一次搬家与一次修补**：`FreeDualPort()` 与 `EchoOneDatagramAsync()` 从
   `LedgerShapeTests` 的私有副本搬进 `ArmRunFixture`（新事实 `UdpReceiverOptionTests` 是第二个用户）；
   同时 `ArmRunFixture.FreePort` 加了**进程内已发放端口去重**（`s_handedOut` + 重探）——
   实测到一次真实竞态：突变 M6 那一轮里 `LedgerShapeTests` 的一条事实拿到 `127.0.0.1:47062` 时
   被另一个并行测试类的探针抢先绑走（`cannot bind … Address already in use`）。端口探针"先探后放"的
   窗口在并行类之间是真实存在的，本批新增的 listener 数量把它放大到可观测。纯测试代码，产品面零变化。
10. **`LedgerShapeTests` 超了有效行门禁（475 > 400）后按"事实"拆成两半**：账本形状（族、声明路径、
   发布组合）留在 `LedgerShapeTests.cs`，计数器的行为反证（error 族、acceptErrors、udpReceivers）
   搬进 `LedgerShapeTests.Accounting.cs`（同一 `sealed partial class`，共享夹具，零复制）。
   第 7 条门禁从红转绿。
11. **inspectcode 的四条发现**（第一次跑）逐条落地：`MemberCanBePrivate.Global`（`DefaultUdpReceivers`
   只被自己的初始化器用 ⇒ 收成 `private`）、`InvalidXmlDocComment`（类级 `<remarks>` 里的嵌套引用
   与同块其它引用同形：`Ledger.ErrorRecord.Detail`）、`RedundantUsingDirective`（`FrameBuffer` 住在
   `Client`，`ArmRunFixture` 的 `Wire` using 冗余）、`RedundantCast`（`Assert.Equal` 的重载不需要
   `(long)`）。第二次跑 `<Issue>` 0、`<IssueType>` 0。
12. **`TargetOptions` 的七个 setter 仍是 `private set`**（E2-d 的登记继续成立）：新选项
    `UdpReceivers` 同样在里面赋值，没有加宽可见性。

---

## 8. 重建方式（check 轮可逐条重跑）

```console
R=.trellis/tasks/10-07-e2e-harness-refactor/research

# 门禁（串行！不要与 jb inspectcode 并发）
dotnet build WinForward.slnx -c Release                                     # 0/0
dotnet test WinForward.slnx -c Release -m:1                                 # 14 程序集全绿（E2E 312：E3-c 的 297 + 本批 15）
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
    benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests          # 无输出、exit 0
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0、空输出
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3d/pub scripts/publish.sh
WF_PUB=/tmp/e3d/pub scripts/selftest.sh scripts/plans/selftest-plan.json    # ×2（post1/post2）
jb inspectcode -f=Xml -e=HINT -o=/tmp/e3d/gates/4-jb.xml WinForward.slnx    # XML 里 <Issue> 0

# 定向事实
dotnet test tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj -c Release \
    --filter "FullyQualifiedName~LedgerShapeTests|FullyQualifiedName~TargetOptionsTests|FullyQualifiedName~UdpReceiverOptionTests|FullyQualifiedName~CliSnapshotTests"

# 判据比对
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e3d/post1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict    # structural=3（E3-b1 的 note）、contract=0
python3 benchmarks/WinForward.E2E/scripts/compare-records.py /tmp/e3d/post1 /tmp/e3d/post2 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json --strict   # 噪声地板 contract=25（全部无带宽）

# 注册表重生成 + 数值核对
python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename --baseline $R/baseline/run1 \
    --run /tmp/e3d/post1 --out-json $R/contract-rename.json --out-md $R/contract-rename.md
python3 /tmp/e3d/ledger-values.py $R/baseline/run1/ledger.jsonl /tmp/e3d/post1/ledger.jsonl /tmp/e3d/post2/ledger.jsonl
for d in $R/baseline/run1 /tmp/e3d/post1 /tmp/e3d/post2; do python3 /tmp/e3a/zerowidth.py $d/out; done | diff

# 快照重采（只需 after/；before/ 是 E2-d 之前那个二进制的记录，不重采）
python3 benchmarks/WinForward.E2E/scripts/cli-snapshots.py /tmp/e3d/pub/linux/WinForward.E2E $R/cli-snapshots/after
diff -r $R/cli-snapshots/before $R/cli-snapshots/after   # 8 个 stdout + index.json + 3 条新 case 的 9 个文件

# 十条突变：python3 /tmp/e3d/mutate/mutations.py（M9/M10 另见 run2.py，理由见 §5 的注意）
```

---

## 9. 给 check 的独立复核点

1. **三项字段的"能移动"都自己动手做一次**：`git stash` 之后按 §5 的表跑突变（M1–M10）；注意
   **M9 必须带 `--filter FullyQualifiedName~TargetOptionsTests` 与超时**（不带就会挂在快照回放上，
   原因见 §5 的注意）。
2. **账本键确实只有新增**：`git diff $R/contract-rename.json` 应当只有 5 行 `added` 且既有行逐字未变；
   `rg -n 'acceptErrors|udpReceivers' $R/contract-rename.md` 命中 5 行 `## Added`。
3. **零个既有数值移动**：§6.2 的 `contract=0`（run1→post1）与 §6.3 的 110 条路径表；后者可以逐行读
   "移动"的 16 条，确认没有一条是计数器。
4. **`--udp-receivers` 的三条拒绝文本逐字**：`$R/cli-snapshots/after/28..30-*.stderr` 与
   `TargetOptionsTests` 的 theory 数据必须一致；`after/04-target-help.stdout` 与
   `INTENTIONAL.md` §3 的 diff 块必须一致。
5. **环境键的裁定可复核**：`rg -n 'environment' benchmarks/WinForward.E2E/Client benchmarks/WinForward.E2E/Cli`
   应当只有 `RunFileWriter.WriteEnvironment`（run.json 的环境块），没有任何 `environment.json` 的写入面；
   `rg -n 'truncatedFrames' .trellis/tasks/10-07-e2e-e4-analyzer/prd.md` 应当命中那条 E4 要求。
