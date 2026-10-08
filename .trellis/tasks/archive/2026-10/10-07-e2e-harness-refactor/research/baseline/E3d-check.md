# E3-d check 轮：独立复核、反证与修复

复核对象：**未提交**的 E3-d 工作树（HEAD `ac30de9`，`src/` 零改动）。本文件是 check 子代理的第二双眼睛：
判据、装置与结论全部由 check 轮**自己重跑**得出，不引用作者 `E3d-vs-run1.md` 的读数；作者的文档只在
"作者声称什么" 的意义上被逐条对照。复核清单见本任务 `implement.md` 的 E3-d 节与父 `design-decisions.md`
D19.3 A/B（新键登记）、D14.7（`JsonlSink` 吞/计）、D14.16/D14.20（字面量 gate 与 `typeof` 反射）、
D14.17（每层各一个常量）、`test-stability.md` §2.2/§2.4/§2.9。

**结论一句话**：E3-d 的三字段各自"能移动"、`--udp-receivers` 的边界与同源上限、快照纪律、账本键只增、
跑批等价（`contract=0`）与自测读数全部由 check 轮独立复现；**修掉 3 处缺陷 + 1 处文档编号**（下节 §8），
无阻塞项。发布二进制 `linux/WinForward.E2E.dll` sha256
`c086fb30814d744686d960dcce6c5180a445bd4e37f2c5b53addc8bfd6c4ba65`（check 轮自建，与作者 post1/post2 逐位相同）。

---

## 1. 门禁（check 轮，冻结树，逐条串行）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)**，rc=0 |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | 14 个程序集全绿，rc=0；`WinForward.E2E.Tests` **313**（E3-c 的 297 + 本批 15 + check 轮补的 1） |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0、**0 字节输出** |
| 4 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/check3d/jb-inspectcode.xml WinForward.slnx`（单独跑） | 562 文件；报告 `<Issues />` 空、`<IssueTypes />` 空、`<Issue>` **0** |
| 5 | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/check3d/pub scripts/publish.sh` | rc=0；三份产物；linux dll sha256 见上 |
| 6 | `WF_PUB=/tmp/check3d/pub scripts/selftest.sh scripts/plans/selftest-plan.json` ×2 | 两次 **rc=0**（check1 / check2，就是 §6 的两栏） |
| 7 | `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests` | **无输出、rc=0**；`LedgerShapeTests.cs` 360 / `LedgerShapeTests.Accounting.cs` 125 / `UdpReceiverOptionTests.cs` 77 / `TargetOptionsTests.cs` 105，均 ≤400 |

第 4 条在第 1–3 与第 5–6 条之后单独跑（不与 test/构建并发）；第 5 条的二进制在 check 轮的编辑之后重发，
哈希与作者记录的相同 ⇒ 产品面字节未动（check 轮只改了测试与一处 XML 文档注释）。

## 2. 七条突变（check 轮自造，与作者 M1–M10 无一相同）

装置 `/tmp/check3d/mutate.py`：六个可突变文件先整份备份到 `/tmp/check3d/orig/`（**不用 `git checkout`**，
工作树是未提交的），打补丁 → `dotnet build`（E2E 测试工程）→ `dotnet test --filter
"FullyQualifiedName~LedgerShapeTests|FullyQualifiedName~TargetOptionsTests|FullyQualifiedName~UdpReceiverOptionTests"`
→ 从备份整份还原 → 逐文件 sha256 与备份比对。每条只跑一次，`--filter` 与超时始终在（M9 那类"命令变成可运行"
的突变**不做**，避免回放真的起靶机）。

| # | 突变（与作者十条的关系） | 杀它的断言 | 实测 |
|---|---|---|---|
| N1 | `detail` 发布 `GetBaseException().Message` 而不是最内层**类型名**（作者 M1 改的是外层类型名，方向不同） | `TheErrorRecordNamesTheInnermostCauseOfTheFailure` | 红 1 条 |
| N2 | 被拒的 accept 记在**停机**的 `catch (ObjectDisposedException)` 上而不是 `catch (SocketException)`（作者 M2 是 `Add(0)`） | `ARefusedAcceptIsCountedInItsOwnListenersFamilyAtBothLevels`、`ARefusedAcceptIsCountedAndTheListenerKeepsServing` | 红 2 条 |
| N3 | 每次拒绝把计数 `Add(2)` 而不是 `Increment`（作者没有这条） | `ARefusedAcceptIsCountedAndTheListenerKeepsServing`（末态 2 ⇒ 4） | 红 1 条 |
| N4 | 每个接收循环给自己的槽位 `Add(2)`（"观测和 = 循环数"）；**第一版**（所有循环写 `[0]` 槽）被分析器 `S1172`（`index` 未使用）拦成 **build error** | `AZeroCounterKeepsItsKeyAndNoLedgerValueIsNull`、`ThePublishedUdpReceiverCountIsTheLoopsThatStarted`、`UdpReceiverOptionTests` 两条 | 红 4 条 |
| N5 | 默认公式的上限 `2..8` → `2..16`（作者 M8 改的是 8→4） | `TheUndeclaredUdpReceiverCountIsTheFormulaTheTargetAlwaysUsed`、`AnUndeclaredReceiveLoopCountKeepsTheDefaultFormula` | 红 2 条 |
| N6 | 拒绝文本写 `1..{MaxUdpReceivers + 1}`（上限与解析**不同源**；作者没有这条） | `TheUdpReceiverBoundIsTheOneTheRefusalNames` | 红 1 条 |
| N7 | 区间下界 `< 1` → `< 2`（即 `--udp-receivers 1` 被拒；**check 轮补的事实**才杀得住，见 §8-1） | `TheLowestReceiverCountTheRefusalNamesIsAccepted` | 红 1 条 |

**还原**：`TargetRunner.cs`、`TcpAcceptLoop.cs`、`UdpEchoServer.cs`、`TcpTargetServer.cs`、`DnsServer.cs`
五文件与备份 sha256 逐位相同；`TargetOptions.cs` 在突变期间也逐位还原为 `92beadd5…`，其后 check 轮只改了
`TryReceivers` 的三行 `<summary>`（`diff` 已核对，行为零变化），当前哈希 `85f9460c…`。工作树其余部分未被
突变装置写过。

> **登记**：N4 的第一版说明"某条记账路径不可用删除来反证"——删掉槽位写入会让分析器先红（与 E3-c 的
> `CS0649` 同型）。真正可判的负控是"记的**数**不对"。

## 3. `--udp-receivers`：边界、默认与同源（发布二进制实测）

```console
$ B=/tmp/check3d/pub/linux/WinForward.E2E
$ $B target --udp-receivers 0    # exit=2  stderr: e2e target: the udp receive-loop count must be in the range 1..64
$ $B target --udp-receivers 65   # exit=2  同上
$ $B target --udp-receivers abc  # exit=2  stderr: e2e target: 'abc' is not a receive-loop count
$ $B target --udp-receivers 1.5  # exit=2  stderr: e2e target: '1.5' is not a receive-loop count
$ $B target --udp-receivers -1   # exit=2  stderr: e2e target: '-1' is not a receive-loop count
$ $B target --udp-receivers      # exit=2  stderr: e2e target: missing value for '--udp-receivers'
```

| 输入 | 实测 |
|---|---|
| `1` | 合法：整台靶机起来、收下一发数据报（`udp.received=1`）、SIGTERM 后 **exit 0**、账本 `targetSummary/udp/udpReceivers = 1` |
| `64` | 合法：同上，`udpReceivers = 64` |
| 未声明 | `udpReceivers = 8` = `Math.Clamp(nproc/2, 2, 8)`（本机 32 核）。逐值对照 `git show HEAD:benchmarks/WinForward.E2E/Target/TargetRunner.cs` 第 21 行 `Math.Clamp(Environment.ProcessorCount / 2, 2, 8)` —— 公式逐字相同，且它是**搬进** `TargetOptions.DefaultUdpReceivers` 而不是重写 |
| `Target/**` 里的旧硬编码 | `rg -n 'ProcessorCount' benchmarks/WinForward.E2E/Target/` **无输出**；`TargetRunner.cs` 只读 `options.UdpReceivers` |
| 上限同源 | `MaxUdpReceivers = 64` 一个常量供解析与拒绝文本；N6（把文本改成另一个数）红 ⇒ 同源有判据；`1` 的下界由 check 轮补的事实钉住（N7） |

## 4. 快照纪律

```console
$ diff -rq research/cli-snapshots/before research/cli-snapshots/after
Files before/04-target-help.stdout and after/04-target-help.stdout differ
… 05/06/07/08/09/10/11 共 8 个 target help stdout …
Only in after: 28-target-udp-receivers-not-a-number.{exit,stderr,stdout}
Only in after: 29-target-udp-receivers-zero.{exit,stderr,stdout}
Only in after: 30-target-udp-receivers-out-of-range.{exit,stderr,stdout}
Files before/index.json and after/index.json differ
```

- 差异面**恰好**是 8 个 stdout + `index.json` + 3 条新 case 的 9 个文件；`before/` 在 `git status` 里干净。
- 8 个 stdout 的逐字 diff **只有**两条登记替换：E2-d 的退出码句（§1）+ E3-d 的 `--udp-receivers` 两行（§3）；
  没有第三个字符。`index.json` 的差量只有末尾三条新 case，`after/` 的 3 条 `.exit` 全是 `2`，stderr 逐字
  与 §3 相同。
- **独立重采**：用 check 轮自建的二进制重跑 `cli-snapshots.py` 到 `/tmp/check3d/after`，`diff -r` 与仓库里
  的 `after/` 树**逐字节相同**（31 条 case，无 `.cli-snapshot-out` 残留）⇒ `after/` 确实是本批二进制录的。
- **旧二进制的预期行为**：`/tmp/e3c/pub/linux/WinForward.E2E`（E3-c 的、即 HEAD 的二进制）对
  `target --udp-receivers abc` 打印 `e2e target: unknown argument '--udp-receivers'`、exit 2；
  `target --help` 里没有该行 ⇒ 三条新 case 不能进 `before/`，`CliSnapshotTests` 的
  `TheCasesTheBeforeTreePredatesStillPrintTheirRecordedText`（带 `Assert.NotEmpty(added)`，非空断言）是它们
  唯一的回放通道。

## 5. 账本键扩容与登记（D19.3 A/B）

- **四处齐动**（逐处 `rg` 定位）：
  - `acceptErrors`：4 个常量（`Ledger.TcpSummary:118`、`TargetSummary.TcpTotals:233`、`DnsSummary:286`、
    `TargetSummary.DnsTotals:382`）+ 2 个 keyset（`TcpTotalsKeys`/`DnsTotalsKeys` 的 `Summary`/`Target`
    实例，声明序 = ctor 序 = 写序）+ 2 个 writer（`TcpTargetServer.cs:115`、`DnsServer.cs:199`）+ **1 个**
    计数点（`TcpAcceptLoop` 的 `catch (SocketException)`）；
  - `udpReceivers`：常量 `TargetSummary.UdpTotals:316` + writer `UdpEchoServer.cs:83` + 计数点
    （每个循环进入 receive 前给自己的槽位 +1）+ keyset（形状测试经 `DeclaredKeys.Under(...UdpTotals)` 反射）；
  - `detail`：常量 `Ledger.ErrorRecord.Detail` + writer `TargetRunner.WriteErrorRecordAsync:169` + 两个写点
    （summary 守卫、连接守卫）+ keyset（形状测试把 `ErrorRecord` 作为第五个 kind 注册）。
- **形状测试双向 + 写序 + 三态**：`EveryLedgerRecordPublishesExactlyTheDeclaredKeys` 的 `DeclaredKeys.Differences`
  同时报 "declared but not written" 与 "written but not declared"（双向）；`EveryLedgerRecordWritesItsKeysInDeclarationOrder`
  逐记录比对写序；`AZeroCounterKeepsItsKeyAndNoLedgerValueIsNull` 覆盖空态（0 与键在）与 measured 态
  （TCP 1 / DNS 1 / 干净 0）。E2E 全量 313 绿。
- **登记**：`contract-rename.json` 604 行 = identical 580 + renamed 9 + **added 15**（本批 +5；`only in old` 空、
  既有行逐字未变 —— 用 `git show HEAD:` 的旧 JSON 与工作树逐行比对确认，`changed rows: 0`）。
  `contract-rename.md` 的差量是 2 行表头读数 + `## Added` 表 5 行。
- **独立重生成**：用 check 轮自己的 `post1` 重跑 `contract-inventory.py rename`，输出与仓库里的表**逐行相同**
  （除 `fresh run:` 那一行路径），`declared but not observed: 2`（仍是 `detail`/`error`：干净跑批不写 error 记录）。
- `--batch B2`：`compare-records.py` 打印 `executed batch: B2; 9 entries required, 9 satisfied, 0 not observed`。
- **`detail` 只有一行**：`new_path == "detail"` 在表里恰 1 行（E3-b1 登记的客户端 arm 记录叶），账本 `error` 族
  发布的是同一条 canonical path，所以账本多了一个族而路径集合只多 5 条。

## 6. 等价性、噪声地板与零宽键（check 轮自己的两次自测）

```console
$ R=.trellis/tasks/10-07-e2e-harness-refactor/research
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/check3d/post1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict
summary: structural=3 conditional=0 identity=0 declared=39 contract=0 rename=0 readings=292/366 readingsOutOfBand=210/366
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py /tmp/check3d/post1 /tmp/check3d/post2 \
    --normalize … --band … --strict
summary: structural=0 conditional=0 identity=0 declared=0 contract=25 rename=0 readings=289/366 readingsOutOfBand=198/366
```

- 第 1 栏三条与 E3-b1/E3-c 的 note 更正**逐字相同**（`records/{BASE,LOSS,MIX}.jsonl` 的 `notes`），本批零新增；
  exit=1 只因为这三条。第 4 栏 `contract=0` ⇒ **没有任何契约路径越带**。
- 噪声地板 25 条：`sed -n '/== 4. contract/,/== 5\./p'` 过滤掉 `no recorded jitter band` 之后**只剩表头** ⇒
  25 条**全部**是"没有记录带宽"的新键/新族，没有一条是"值动了"（D18.5 第 2 条的 `zero-width band` 打印也照旧）。
- **账本逐路径值表**（check 轮自写 `/tmp/check3d/ledger-paths.py`，按 `(kind, dnsSummary 的 port)` 分组，
  比作者按 `type` 分组更细）：`paths: 128 same=103 added=7 moved=18`。与作者口径（110 = 88 + 6 + 16）的差
  全部来自"`dnsSummary` 两台 listener 是否分开计"：合并 18 条同名路径后 `7→6`、`18→16`、`103→88`，
  两个口径**算术自洽**。`moved` 的 18 条逐条是人眼核对过的时钟/身份值（`utc`、`startedTicks`/`endedTicks`、
  `tcp::peer` 的端口、`udpSummary::ticks`）或 E3-c 的 `truncatedFrames`（run1 缺席、两个 post 都是 0）；
  **没有一条是计数器**。新键 7 条全部被观测（5 条 canonical path，`acceptErrors` 在 dnsSummary 上摊成 2 行）。
- **零宽键**（check 轮自写 `/tmp/check3d/zerowidth.py`，D18.5 #12 的叶集）：三跑各 **39 个 (记录, 路径) 对 /
  19 条不同路径**，`diff` 三份输出**无差异**。
- **selftest 读数**（check 轮两次）：`targetSummary/udp/udpReceivers = 8`（默认公式）；
  `tcpSummary/acceptErrors = 0`、`targetSummary/tcp/acceptErrors = 0`、两台 DNS 的 `dnsSummary/acceptErrors = [0,0]`、
  `targetSummary/dns|dnsAlt/acceptErrors = 0`；**error 记录 0 条**；`connections=157`、`protocolErrors=0`、
  `truncatedFrames=0`（TCP 与两台 DNS）、判决分布 `clean 61 / reset 25 / partialFin 25 / halfClose 25 / clientClosedEarly 21`
  —— 与 run1 相同，两次自测逐值相同。

## 7. 环境键：裁定被执行

- `index.jsonl` 新增 `E3-d-environment-keys-ruled-out`，verdict `deferred`，正文写的是"**不实现**"与消费点归
  E4，**没有**发明生产者；`E3-c` 的 `E3-A2-truncated-deferred-halves` 作为历史条目保留。
- 消费者侧真值来源只有账本：`rg -n 'environment' benchmarks/WinForward.E2E/{Client,Cli,Target}` 只命中
  `ResourceSampler` 的 `Environment.WorkingSet`（另一个量）与 `RunFileWriter` 的注释，**没有任何
  `environment.json` 的写入面**。
- E4 的 PRD 逐字写着该要求（`10-07-e2e-e4-analyzer/prd.md:48-57` + 验收项 `:84-85`）：
  "账本 `tcpSummary/truncatedFrames` 与 `dnsSummary/truncatedFrames`（两层…）**> 0** 时，报告必须在**数据质量
  小节**披露该靶机/该 pass 的截断计数…按**靶机/pass 总量**给，**不得摊到臂**…synthetic 树由 `make_tree.py`
  注入这两组键的**非零**值"。

## 8. check 轮修掉的问题（4 条，全部机械/局部；`src/` 未动）

1. **`--udp-receivers 1` 没有判据（覆盖缺口）**：区间是 `1..64`，但测试只钉住 64（`MaxUdpReceivers` 的两端），
   下界 1 没有任何事实；把 `receivers is < 1` 改成 `< 2` 整套测试仍然全绿（N7 复现）。补
   `TargetOptionsTests.TheLowestReceiverCountTheRefusalNamesIsAccepted`（1 被接受且 `UdpReceivers == 1`），
   N7 随即红。同处把理论上的注释"Three refusals"改成"Three **kinds of** refusal"（5 行数据、3 类文本），
   纯注释准确性。
2. **`LedgerShapeTests.cs` 的空行**：E3-d 的拆分留下 1 处成员间双空行 + 文件结尾 `}` 前的两行空行（`dotnet format`
   不查这一项）。删除，纯空白。
3. **`TargetOptions.TryReceivers` 的 `<summary>` 与代码不符**：注释写"zero **or less** is refused by the same
   range check"，而 `int.TryParse(value, NumberStyles.None, …)` 根本不接受符号，`-1` 走的是"不是十进制数"分支，
   区间检查的 `< 1` 只可能由 `0` 触发。改成"a value that is not a decimal number at all -- a sign or a fraction
   included -- … A count of zero is refused by the same range check …"。**行为零变化**（发布二进制哈希与作者相同）。
4. **`E3d-vs-run1.md` §7 的编号**：最后一条被编成第二个 `10.`（§7 共 11 条），改成 `12.`；文内对 §7-10/§7-11 的
   交叉引用不受影响。

## 9. 未修复、需上报（均**不阻塞提交**）

1. **帮助文本里的默认公式是第二个字面量**：`TargetRunner.PrintHelp` 写死 "half the processors, clamped to 2..8"，
   与 `TargetOptions.DefaultUdpReceivers` 的 `2, 8` 无判据耦合——把公式改成 `2..16`（N5 那一类）时公式事实会红，
   但**帮助文本会静默过期**（只有回放快照钉住它的字面）。属"两个今天恰好相同的字面量"，不建议在 check 轮
   为此扩接口（要暴露 clamp 边界并重采快照）。
2. **靶机侧公用件住在客户端夹具里**：`ArmRunFixture` 的类文档说它是"一条 arm 的端到端夹具"，而
   `FreeDualPort()`/`EchoOneDatagramAsync()` 是靶机侧的量（作者 §7-9 已登记为"搬家 + 第二个用户"）。属设计判断
   （是否另立 `LoopbackTarget` 帮助类），不在 check 轮重写。
3. **快照回放没有超时**：`CliSnapshotTests`/`cli-snapshots.py` 逐条同步等 `Program.Main`，一条 case 若从"拒绝"
   变成"可运行"就会**挂住**而不是失败（作者 §5 的 M9 实测，含仓库根 140 KB 残留账本，已删）。当前 31 条全 exit 2、
   采集器结尾的 `.cli-snapshot-out` 断言照旧成立，所以是清单的既有性质而不是本批缺陷；登记给 E5/后续批次。
4. **`error` 族第二个写点不可从行为侧驱动**：`TcpTargetServer.HandleConnectionAsync` 的守卫只在
   `WriteConnectionAsync` **抛出**时触发，而账本走 `SwallowAndCount`，I/O 失败被吞并计数。行为证据只在
   summary 守卫上（形状测试驱的正是它），第二个写点由源码/类型保证（作者 §7-5 已登记为 `observation`）。
5. **工作树里另有一个任务的未提交改动**：`.trellis/tasks/10-07-tcp-close-drain/` 的 `prd.md`/`task.json`/
   `check.jsonl`/`implement.jsonl` 被改、`design.md` 未跟踪（内容是「TCP close drain」的规划，指向 `src/**`），
   与 E3-d 无关。提交 E3-d 时**不应**把它们带进去（也不是本批造成的）。

## 10. 复核清单逐条结论

| # | 复核项 | 结论 |
|---|---|---|
| 1 | 三字段各自"能移动" + check 轮自造反证 | 通过：`detail`（N1）、`acceptErrors`（N2/N3，两族两层逐值相等、真 listener 恰 0、干净运行键在且 0）、`udpReceivers`（N4，声明 13 ⇒ 账本 13、未声明 ⇒ 公式 8）；7 条突变全部被杀，还原哈希逐位一致 |
| 2 | `--udp-receivers` 边界、默认与同源 | 通过：未声明 = HEAD 公式逐字；1/64 合法；0/65/abc/1.5/-1/缺值 ⇒ exit 2 + 明确文本；上限与解析同源（N6）；`Target/**` 无旧硬编码 |
| 3 | 快照纪律 | 通过：`diff -r` 恰好 8 + 1 + 9；`before/` 未动；独立重采与 `after/` 逐字节相同；旧二进制对新 case 说 `unknown argument`（预期） |
| 4 | 账本键四处齐动 + 双向/写序/三态 + 604/added 15 + B2 9/9 + `detail` 一行 | 通过：逐处 `rg` 定位；`Differences` 双向；E2E 313 绿；604 = 580 + 9 + 15（既有行 0 变化，独立重生成逐行相同）；B2 9/9；`detail` 恰 1 行 |
| 5 | 等价性与误报 | 通过：`structural=3`（仅 E3-b1 的 note）、`contract=0`；噪声地板 25 条全部"no recorded jitter band"；账本 128/103+7+18（作者 110/88+6+16 的合并口径，算术自洽，18 条 moved 无一是计数器）；零宽 39 对/19 路径三跑相同；selftest `udpReceivers=8`、`acceptErrors=0`、0 条 error |
| 6 | 环境键裁定 | 通过：`deferred` 条目无生产者；客户端/靶机无 `environment.json` 写入面；E4 PRD 逐字含"按靶机/pass 总量、不得摊到臂、`make_tree.py` 注入非零" |
| 7 | 规范与门禁 | 通过：新文件 360/125/77 ≤400；无变更日志式注释（`rg` 逐条读过新增注释）；反射只有 `typeof(...)` 字面量（D14.20）；六条门禁串行 + `effective-lines.py` 无输出；`index.jsonl` 6 条新条目 verdict 全在 D19.2 ⑰ 枚举内 |

## 11. 重建方式（check 轮可逐条重跑）

```console
B=/tmp/check3d/pub/linux/WinForward.E2E
python3 /tmp/check3d/mutate.py backup && python3 /tmp/check3d/mutate.py N1 N2 N3 N4 N5 N6 N7 && python3 /tmp/check3d/mutate.py verify
python3 /tmp/check3d/run_target.py                      # 1 / 64 / 未声明 的账本读数
python3 benchmarks/WinForward.E2E/scripts/cli-snapshots.py $B /tmp/check3d/after && diff -r /tmp/check3d/after $R/cli-snapshots/after
python3 /tmp/check3d/ledger-paths.py $R/baseline/run1/ledger.jsonl /tmp/check3d/post{1,2}/ledger.jsonl
for d in $R/baseline/run1/out /tmp/check3d/post1/out /tmp/check3d/post2/out; do python3 /tmp/check3d/zerowidth.py $d; done | diff
```

> `post1`/`post2` 由 check 轮的两次 `selftest.sh` 产出（`/tmp/wf-bench/selftest` 快照到 `/tmp/check3d/post{1,2}/`）。
