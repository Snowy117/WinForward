# E2-c：Target 账本键族类型化（D14.16 的 `Target/**` 部分）

本批是 E2 的第三小批（2c）：把 target 侧写出的 `ledger.jsonl` 从**字面量键**改成 `ArmKeys` 声明，
并把它接进字面量 gate 与形状测试。这是 D14.16 的最后一块（E1 已闭合 client 的 `result`/`armSummary`/
`metrics` 子树与 `run.json`，E2-b2 闭合 `sample`/`samplerError`），也是 **E4 分析器读账本之前**必须
落地的一批：账本是跨二进制契约，键名/形状只增不改。**`src/` 一行未改，本轮未提交。**

判定线：四个 writer 的 51 处 key 位字面量归零且 JSON 字节不变；账本记录形状与 `ArmKeys.Ledger`
**逐向**相等（缺键/多键/层级/键序）；gate 扩容后仍会因字面量变红、且不误判非键字面量；
`ledger.jsonl` 与 run1 **逐记录字节级相同**（仅归一化字段不同）；六条门禁全绿。

---

## 0. 结论一句话

新增 `benchmarks/WinForward.E2E.Contracts/ArmKeys.Ledger.cs`（112 有效行，70 个 `const string`：
信封 2 + `tcp` 记录 8 + `tcpSummary` 4 + `udpSummary` 6 + `sources[]` 元素 3 + `dnsSummary` 12 +
`targetSummary` 7 + `targetSummary/{tcp,udp,dns}` 三个层级各 4/4/12 + `verdicts` 名字集 8），四个
target writer 的 **51 处 key 位字面量归零**、JSON 形状与键序逐字节不变（§2/§6.3）；新增 6 条账本记录
形状测试（§3）与 gate 的两条阴性/阳性对照（§4）；`ledger.jsonl` 与 run1 的 **258 条记录全部 1:1 配对、
归一化后逐字节相同**（§6.3），零宽键 34 对仍逐值相同（§6.4）；六条门禁全绿（§5）。

---

## 1. `ArmKeys.Ledger` 分片：键计数与层级

`ArmKeys.Ledger`（`ArmKeys.Ledger.cs`，112 有效行、总行 361）按**记录族**组织，每个**层级**各自声明
常量（D5 第 4 条 / D14.17）。键位计数以 gate 同款正则实测（`.Write*(␣*"` 与 `["…"]`，跳过注释行）：

| 层级（JSON 路径前缀） | 类 | 键数 | 写入者 |
|---|---|---|---|
| （记录根） | `Ledger.Envelope` | 2：`utc`、`label` | `TargetRunner.WriteLedgerEnvelope`（sink 信封） |
| （记录根）`type` | **复用** `Common.Record.Type` | —（不重复声明） | 四个族 |
| `tcp` 记录根 | `Ledger.TcpRecord` | 8：`connectionId`、`mode`、`expectedBytes`、`bytesEchoed`、`verdict`、`peer`、`startedTicks`、`endedTicks` | `TcpTargetServer.WriteConnectionAsync` |
| `tcpSummary` 根 | `Ledger.TcpSummary` | 4：`connections`、`bytesEchoed`、`protocolErrors`、`verdicts` | `TcpTargetServer.WriteTotals(Summary)` |
| `verdicts` 成员 | `Ledger.VerdictNames` | 8：`clean`、`reset`、`partialFin`、`halfClose`、`stall`、`clientClosedEarly`、`protocolError`、`error` | 两个 `verdicts` 对象（`Enum.GetValues<TcpVerdict>` + `TcpCommand.Name`） |
| `udpSummary` 根 | `Ledger.UdpSummary` | 6：`received`、`undecodable`、`bytes`、`ticks`、`sources`、`sourceOverflow` | `UdpEchoServer.WriteSummaryAsync` |
| `sources[]` 元素 | `Ledger.UdpSummary.SourceEntry` | 3：`address`、`port`、`datagrams` | 同上（数组元素层） |
| `dnsSummary` 根 | `Ledger.DnsSummary` | 12：`port`、`udpQueries`、`udpAnswers`、`udpEmptyAnswers`、`udpMalformed`、`udpSendErrors`、`tcpQueries`、`tcpAnswers`、`tcpEmptyAnswers`、`tcpMalformed`、`tcpConnections`、`tcpAborted` | `DnsServer.WriteTotals(Summary)` |
| `targetSummary` 根 | `Ledger.TargetSummary` | 7：`startedTicks`、`endedTicks`、`ledgerWriteErrors`、`tcp`、`udp`、`dns`、`dnsAlt` | `TargetRunner.WriteSummariesAsync` |
| `targetSummary/tcp` | `Ledger.TargetSummary.TcpTotals` | 4（与 `tcpSummary` 同拼写、另一层级 → 另一常量） | `TcpTargetServer.WriteTotals(Target)` |
| `targetSummary/udp` | `Ledger.TargetSummary.UdpTotals` | 4：`received`、`undecodable`、`bytes`、`sendErrors` | `UdpEchoServer.WriteTotals` |
| `targetSummary/dns`、`targetSummary/dnsAlt` | `Ledger.TargetSummary.DnsTotals` | 12（同上，另一层级） | `DnsServer.WriteTotals(Target)`，两个容器共用同一键集 |

**70 个声明、47 个不同拼写**（`port`/`bytesEchoed`/`verdicts`/`received`/`bytes`/`startedTicks` 等在不同层级各声明一次）。

**"同一叶子名在不同层级各写一个常量"的落地方式**：同一个 totals 块在两个层级由**同一个 writer**
写出（`WriteTotals` 的数值只有一份），所以层级由**键集参数**承载而不是在调用点拼字符串：
`TcpTargetServer.cs` 的 `TcpTotalsKeys{Summary,Target}` 与 `DnsServer.cs` 的 `DnsTotalsKeys{Summary,Target}`
（两个 `readonly struct`，字段即该层级的键名，构造点引用对应层级的常量）。`udpSummary` 根与
`targetSummary/udp` 是两个不同的方法体（前者含 `ticks`/`sources`/`sourceOverflow`，后者含 `sendErrors`），
各自直接引用本级常量，不需要键集参数。

**唯一的共享名字集**是数据驱动容器的成员名：`Ledger.VerdictNames` 被 `tcpSummary/verdicts` 与
`targetSummary/tcp/verdicts` 两个层级共用，与 `ArmKeys.Reliability.OutcomeNames`（三个分布、三层级共用
一个名字集）、`ArmKeys.Common.LatencyRecord.Histogram`（四个直方图共用叶子）同形；容器键本身仍各层一份
（`TcpSummary.Verdicts` / `TargetSummary.TcpTotals.Verdicts`）。`dns` 与 `dnsAlt` 是**同层级**的两个容器、
同一个 writer，故键集一份（分片注释写明），shape 测试用两个前缀分别绑定它。

**条件字段（写进分片 `<remarks>`）**：

1. `targetSummary/dnsAlt` 及其整个块：只有 target 用 `--dns-alt-port` 起第二个 DNS 监听时写出；同时该
   监听自己会多写一条 `dnsSummary` 记录（记录不是键，但形状测试覆盖）。
2. `sources[]` 的元素键（`address`/`port`/`datagrams`）：只有该区间真的收到过数据报时才写出。`sources`
   数组本身**总是**写出（无流量时 `[]`）。
3. `verdicts` 的 8 个成员**总是**全写（writer 遍历枚举），不是条件字段。

**null 口径（写进分片 `<remarks>`）**：账本**没有任何一个键会写 JSON `null`**；没有内容的量写成 `0` 并
保留键。于是"缺键"只来自上面两条条件，形状测试把"0"与"缺键"分开断言（§3 F3/F4）。

---

## 2. 四个 writer 的迁移点与"键序不变"

| 文件 | 迁移点 | 键位字面量（迁移前 → 后） |
|---|---|---|
| `Target/TcpTargetServer.cs` | 新 `TcpTotalsKeys`（`Summary`/`Target`）+ `WriteTotals(writer, keys)`；`WriteSummaryAsync` 传 `Summary`；`WriteConnectionAsync` 8 键 + `type`（可见性不动：形状测试经**真连接**走到这条 writer，不需要测试专用加宽） | 14 → **0** |
| `Target/UdpEchoServer.cs` | `WriteTotals` 4 键改引 `TargetSummary.UdpTotals`；`WriteSummaryAsync` 6 键 + 元素 3 键 + `type` | 14 → **0** |
| `Target/DnsServer.cs` | 新 `DnsTotalsKeys`（`Summary`/`Target`）+ `WriteTotals(writer, keys)`；`WriteSummaryAsync` 传 `Summary` | 13 → **0** |
| `Target/TargetRunner.cs` | 信封 2 键；`targetSummary` 体 9 键（含 4 个容器键与两次 `WriteTotals(…, DnsTotalsKeys.Target)`）；`WriteSummariesAsync` 由 `private` 提 `internal`（形状测试驱动整个 runner 组合，含条件 `dnsAlt`） | 10 → **0** |

合计 **51 → 0**（`TcpConnectionProtocol.cs` / `SourceCensus.cs` / `TcpAcceptLoop.cs` / `SocketIo.cs` /
`Sockets.cs` / `TargetLog.cs` 实测本来就没有 key 位字面量，未改）。

**键序不变的证据**：迁移只把每个 `Write*(<literal>` 的第一个实参换成常量，语句顺序、`WriteStartObject`/
`WriteEndObject`/`WriteStartArray` 的结构、`WritePropertyName`+`WriteStartObject` 的两段式写法全部逐字不动
（`git diff` 可逐行核对：每个 hunk 都是"字面量 → 常量"）。值字面量（`"tcp"`/`"tcpSummary"`/`"unknown"`/
`"targetSummary"` 等）按 D14.16 的口径**不是键**，保持原样（E2-b2 §4.3.1 同判）。机械判定见 §3 的键序事实
与 §6.3 的账本字节级比对（键序不同会直接表现为 normalized JSON 文本不同）。

---

## 3. 账本记录形状测试（`tests/WinForward.E2E.Tests/LedgerShapeTests.cs`，291 有效行，6 条）

发布方式：**生产 writer 全链路**——`JsonlSink`（`SwallowAndCount` 策略 + `TargetRunner.WriteLedgerEnvelope`）、
两台 `DnsServer`（含第二监听）、`TcpTargetServer`、`UdpEchoServer`，然后
`TargetRunner.WriteSummariesAsync` 写"空态"（无流量、无 `dnsAlt`）；接着**一条真连接**（
`SocketOps.SendCommandAsync` + 半关闭，经 `TcpAcceptLoop`+`TcpConnectionProtocol`）与**一个真数据报**
（经 `UdpEchoServer.ReceiveLoopAsync` + `SourceCensus`）走真实监听器；取消并 join（`DrainAsync` 保证连接
记录已落盘）后写"测量态"（含 `dnsAlt`）。所有事实读的是这批落盘字节。

| # | 事实 | 断言 |
|---|---|---|
| F1 | `EveryLedgerRecordPublishesExactlyTheDeclaredKeys` | 按记录 `type` 分组，逐族把**声明路径集**与**实际 flatten 路径集**双向比对（`DeclaredKeys.Differences` 打印"declared but not written"/"written but not declared"两侧） |
| F2 | `EveryLedgerRecordWritesItsKeysInDeclarationOrder` | 逐条记录：实际路径的文档序 == 声明序（限定在该记录写出的路径上）；空态/测量态/周期记录都跑 |
| F3 | `AZeroCounterKeepsItsKeyAndNoLedgerValueIsNull` | 整份账本**没有任何** `null` 叶子；空态的 `connections`/`verdicts.clean`/`received`/`sourceOverflow`/`udpQueries`/`ledgerWriteErrors`/`expectedBytes`/`bytesEchoed` 都是"键在、值为 0"；测量态同样的键变 1 / ≥1 |
| F4 | `TheSourceCensusIsAlwaysWrittenAndItsElementsOnlyWhenASourceArrived` | 空态 `sources` 键在且 arity 0、无 `sources/*` 路径；测量态 arity ≥1 且元素键集==`UdpSummary.SourceEntry` 声明 |
| F5 | `TheDnsAltBlockIsOmittedWholeAndTheSameDnsBlockServesBothContainers` | 无 alt 态**没有** `dnsAlt` 键也没有 `dnsAlt/*`；有 alt 态两个容器的路径集互为前缀替换；`dnsSummary` 记录数 3（空态 1 + 测量态 2），`targetSummary/dns/port` 与 `…/dnsAlt/port` 分别等于两条测量态 `dnsSummary` 的 `port` 且二者不同 |
| F6 | `TheDeclaredVerdictNamesAreThePublishedOnes` | `Ledger.VerdictNames` 的声明序 == `Enum.GetValues<TcpVerdict>().Select(TcpCommand.Name)`（8 个，顺序敏感） |

**突变对照（每条都实跑，跑完还原）**：

| # | 突变 | 结果 |
|---|---|---|
| M1 | `TcpTargetServer` 把 `keys.Connections` 写回字面量 `"connections"` | gate `NoWriterSpellsAKnownKeyAsALiteral` **红**：`Target/TcpTargetServer.cs:92: the key 'connections' is written as a literal`（阴性对照，§4） |
| M2 | `WriteTotals` 里把 `bytesEchoed` 与 `protocolErrors` 两句换序 | 只有 F2 **红**（打印 written vs declared 两个序列），F1/F3/F4/F5/F6 全绿 ⇒ 键序事实有牙 |
| M3 | `TargetRunner` 把 `targetSummary/tcp` 与 `…/udp` 两个块互换 | F1+F2 **红**，且两侧差异精确：`declared but not written: tcp/bytesEchoed, tcp/connections, …`、`written but not declared: tcp/bytes, tcp/received, …`（各 16 条）⇒ 层级写错可判 |
| M4 | `TcpTotalsKeys.Target` 改用 `Ledger.TcpSummary.*`（拼写相同的另一层级常量） | **6 条全绿**（见 §8.1 偏离：拼写相同的层级互换在运行期不可判，这是声明纪律而非可判据） |

---

## 4. 字面量 gate 扩容（`JsonKeyLiteralGateTests`，161 有效行）

1. **扫描面**：`Client` 常量改为 harness 根 `benchmarks/WinForward.E2E`，`s_writerFiles` 改为项目相对路径
   并加入四个 target writer（`Target/TcpTargetServer.cs`、`Target/UdpEchoServer.cs`、`Target/DnsServer.cs`、
   `Target/TargetRunner.cs`）；逐名 `File.Exists` 自检保留（漏名=红）。`Client/Arms/**` 仍全目录扫描。
2. **已知键集**：加入 `Ledger` 的 11 个子树（Envelope/TcpRecord/TcpSummary/UdpSummary/SourceEntry/DnsSummary/
   TargetSummary/TcpTotals/UdpTotals/DnsTotals/VerdictNames）。名字集 **276 → 304**（新增 28 个不同拼写）。
3. **阴性对照（自动化）**：新增事实 `TheGateSeesAKeyLiteralAndNothingElse`，把一段**取自 Target 真实形态**
   的文本喂给 gate 的扫描器（`KeyLiteralsIn` 拆出纯文本重载，不再需要临时文件）：命中必须**恰好**是
   `["connections", "received"]`（一个 `WriteNumber("connections", …)` 键 + 一个字典下标
   `datagrams["received"]`），且 `"connections"` 必须在已知键集里。文本里同时含
   `writer.WriteString(ArmKeys.Ledger.TcpRecord.Mode, "unknown")`（**值**字面量）、
   `TargetLog.ReportAsync($"e2e target: the udpSummary record could not be written …")`（日志文本）、
   `Console.Out.WriteLineAsync($"e2e target listening on tcp …")`（`"listening"` 那类文本）、
   `"target-ledger.jsonl"`（文件路径）——**一条都不能被当成键**。
4. **不误判非键字面量的实测**：迁移后对 `Target/**` 全部 10 个文件跑 gate 同款正则，**key 位命中 0**
   （迁移前 51），即 target 侧已无任何可误判对象；gate 的"键位"判别仍是与 client 一致的两条正则
   （`Write*` 属性名成员 + 字典下标），不是 `Write*` 通配。

> 负控 M1 是**真跑过的**：把 `keys.Connections` 换回字面量后 gate 报 `TcpTargetServer.cs:92`，
> 还原后 257/257 全绿。

---

## 5. 六条门禁（冻结树，逐条）

| # | 门禁 | 命令 | 结果 |
|---|---|---|---|
| 1 | publish | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2c/pub scripts/publish.sh` | 退出 0；`linux`/`win`/`win-direct` 三份产物齐全（两个 Windows 客户端按镜像名可分）；`linux/WinForward.E2E.dll` sha256 = `abdaedc08cf2dedca5ca62e63e231301d0642904d7efe30e35b470d11ff1146a` |
| 2 | build | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)`（全解） |
| 3 | test（E2E） | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 257, Skipped: 0, Total: 257`（E2-b2 的 250 + 账本形状 6 + gate 对照 1） |
| 4 | selftest | `cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json` | **连续两次**退出 0（`/tmp/e2c/run1`、`/tmp/e2c/run2`，串行、无并发构建） |
| 5 | format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0、**0 字节输出** |
| 6 | inspectcode | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e2c/jb-inspectcode.xml WinForward.slnx` | 退出 0；547 个文件被 Inspecting、无 `CSharpErrors`；解析 XML：`<Issue>` **0**、`<IssueType>` **0**（只认元素计数，不认 `rg '<Issue'`） |

**第六条的中间态**：第一遍 inspectcode 跑在中间态报 **7 条**（`MemberCanBePrivate.Global`——
`TcpTargetServer.WriteConnectionAsync` 本可保持 `private`，形状测试改用**真连接**驱动后不再需要加宽，已收回；
`InvalidXmlDocComment`——`ArmKeys.Ledger` 的 `<see cref="WinForward.E2E.Wire.TcpCommand"/>` 跨项目解析不到
（Contracts 不引用 harness），改成 `<c>TcpCommand</c>`；`PossibleMultipleEnumeration` ×2、
`ConvertToConstant.Local` ×2、`RedundantAssignment` ×1——全部在 `LedgerShapeTests` 内，逐条修好后
**重新跑 publish + 两次 selftest + 两对 compare-records + 账本字节级 + 零宽键**，§5/§6 的数字全部来自这个
最终二进制）。第一遍 format 同样报过 4 条（1 IDE1006 + 3 CA1859），已修。

**第一次 publish 的中间态 sha256**：`9d0885c65866e1fcae4f33e90824864d91806389aa4adf7839f145e02a52a5c9`（inspectcode
修正前；两次 selftest 与证据文档的读数以最终二进制 `abdaed…` 为准）。

**AC1（有效行）**：`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E
benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests` → **无输出、退出码 0**。新文件有效行：
`ArmKeys.Ledger.cs` 112、`LedgerShapeTests.cs` 291、`JsonKeyLiteralGateTests.cs` 161、
`DnsServer.cs` 296、`TcpTargetServer.cs` 148、`UdpEchoServer.cs` 168、`TargetRunner.cs` 128、
`ContractShapeTests.cs` 357、`DeclaredKeys.cs` 42（全部 ≤400）。

---

## 6. 判定比对（对照冻结基线 run1）

命令（两对共用，`$R=.trellis/tasks/10-07-e2e-harness-refactor/research`）：

```bash
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e2c/<run> \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict
```

| # | 比对 | 结构 | 条件键 | 身份 | 已声明 | **契约** | 改名 | 读数 | 越带读数 | 退出码 |
|---|---|---|---|---|---|---|---|---|---|---|
| P1 | `run1` → `/tmp/e2c/run1`（**判据**） | **0** | 0 | 0 | 27 | **0** | 9/9 satisfied | 296/366 | 218/366 | **0** |
| P2 | `run1` → `/tmp/e2c/run2`（确认） | **0** | 0 | 0 | 27 | **6** | 9/9 satisfied | 295/366 | 224/366 | 1 |
| N1 | `/tmp/e2c/run1` → `/tmp/e2c/run2`（**同一二进制**，噪声地板；不带改名表） | 0 | 0 | 0 | — | 19 | — | 294/366 | 161/366 | 1 |

- 27 条"已声明"全部是改名表 B2 的许可证（`tcp.sentOk → tcp.sent` 之类），与 E2-b1/E2-b2 同族。
- **P2 的 6 条契约差异全部是已登记的宿主噪声**，落在 `BASE.jsonl` 的 loss 相位：
  `metrics/loss/reordered` 0 → 2、`metrics/loss/reorderRate` 0 → 0.0008。同族在 E2-a1 记录过九次运行里
  0/1/5 的漂移，在 E2-a3 §2.1 记录过 0 → 88（`UdpEchoServer` 多收线程 → 回复顺序取决于宿主调度，
  `LossArm` 按到达顺序记账），E2-a3 §8.5 也记录过"某一次确认运行 `contract=6`，全在 BASE 的 loss 相位"。
- **噪声地板（N1）**：同一二进制的两次运行本身就有 `contract=19` = 13 条"改名后无带宽"
  （`no recorded jitter band for this path`，与 E2-b1/E2-b2 的噪声地板逐行同族）+ **6 条**；
  这 6 条与 P2 的 6 条**逐行完全相同**。即 P2 的差异是同一二进制噪声地板的**子集**，不是本次改动带来的。
- **账本组参与判定**：报告的 `measurements` 里 `ledger/ledger.jsonl::` 有 **73 条**（contract 63 +
  reading 10）；**63 条账本契约路径在 P1 两对运行里 baseMean/afterRange 与 after 完全相同**（逐条 band 0），
  即"账本组结构 0、契约零越带"是可读到的，不是没比较。逐条示例：`connections` 157/157、
  `tcp/verdicts/clean` 61/61、`dns/udpQueries` 329/329、`dnsAlt/udpQueries` 321/321、
  `ledgerWriteErrors` 0/0、`protocolErrors` 0/0、`sourceOverflow` 0/0（P2 的 6 条差异全在 `records/BASE.jsonl`，
  账本路径一条不动）。
- **`target.out` 归一化 0 差异**：脚本的 `compare_text` 只按**集合与顺序**判 `normalized line order differs`；
  P1/P2 的 findings 里 **没有任何 `GROUP_TEXT`（target.out）条目**。显式复核（用脚本自己的
  `Config.normalize_text`）：两边各 3 行，集合相等、顺序相等（run1/run2 皆然）。
- `--strict` 读数摘要（P1）：366 条读数里 296 条移动、218 条越带（P2：295/224；N1：294/161）。越带项按主类构成与
  E2-b1/E2-b2 同族：latency 直方图、内存读数、boot 相对时钟与时长（`startedTicks`/`endedTicks`/
  `wallSeconds`/`elapsedSeconds`/`ticks`）、CPU 时间、毫秒/微秒测量、速率与派生量。读数不参与判定。

### 6.3 账本**字节级**对照（本批新增的证据）

口径：用 `compare-records.py` 的同一份 `record-normalize.json` 判定"什么不是契约值"，把每条记录里被
该配置声明为 volatile（`utc`/`*Utc`/`ticks` 一类时钟）、endpoint（`peer` 只留地址）、identity
（`sources/port` 等）、reading（`*Ticks`/`received`/`bytes`/`bytesEchoed`/`sources/datagrams` …）的**叶子值**
替换为占位符（键名与键序保留，`json.dumps(separators=(",",":"))` 复现 writer 的紧凑字节），然后**逐记录**
比对两条账本的 normalized 文本。脚本 `/tmp/e2c/ledger-bytes.py`（临时产物，方法与数字如下，可照此复跑）。

配对方式：按记录 `type` 分组；`tcp` 按 `connectionId`（同 id 多条时按下标，因为"客户端没发首帧"的连接
也带 id 0）；`dnsSummary` 按 `port`；`udpSummary` 按区间序号（1 Hz 计时器驱动，条数不判）；`tcpSummary`/
`targetSummary` 各一条。

| 运行 | run1 记录分布 | 新产物记录分布 | 配对 | 归一化后不同 | 结构问题 |
|---|---|---|---|---|---|
| `/tmp/e2c/run1` | 258（tcp 157 / udpSummary 97 / dnsSummary 2 / tcpSummary 1 / targetSummary 1） | **258（同分布，逐类相同）** | **258/258** | **0** | **0** |
| `/tmp/e2c/run2` | 同上 | 同上 | 258/258 | 0 | 0 |

即：**258 条记录 1:1 配对、除上述归一化字段外逐字节相同**——包括 157 条 `tcp` 逐连接记录、
97 条区间 `udpSummary`、2 条 `dnsSummary`（端口 5301/5302）、`tcpSummary` 与 `targetSummary`。

**该脚本的对照（证明不是空转）**：把 run1 的账本人为改三处再比 ——
① `tcpSummary/verdicts/clean` 61→60（契约值）**被报出**；② 一条 `dnsSummary` 里交换两个键的顺序
**被报出**（`port` 与 `udpQueries` 位置互换）；③ 全部非空 `udpSummary` 的 `received` +1（配置声明的
reading）**未被报出**（run1 的 97 条区间记录里 91 条非空，这 91 条改动不产生差异）。合计 `paired 258 record(s); 2 differ after
normalization; 0 structural problem(s)`、退出码 1。

### 6.4 D18.5 #12 的零宽键

`python3 /tmp/e2b1/zerowidth.py <run>/out`（口径同 E2-a3/E2-b1/E2-b2），与 run1 的 `out/` 比对：
**34 个 (记录, 路径) 对 / 18 条路径，`diff` 全空**（run1 与 run2 各一次）。含 LAT 的
`metrics/*.received` 322/161、`unmatchedReplies` 0、`outstandingAtTeardown` 0、`latency/*-rtt/count`
322/161，LATLOAD 1601，BASE 101，DNS/DNSALT 402，MIX 146/602/8。

---

## 7. 重建方式（check 轮可逐条重跑）

```bash
# 0. AC1 工具：三项目无输出、退出码 0
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py \
    benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests

# 1. 六条门禁（顺序不可换：publish 冻结二进制，selftest/compare 都在它之上）
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2c/pub scripts/publish.sh && cd ../..
dotnet build WinForward.slnx -c Release                       # 0 Warning(s) / 0 Error(s)
dotnet test tests/WinForward.E2E.Tests -c Release             # Failed: 0, Passed: 257
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2c/pub scripts/selftest.sh scripts/plans/selftest-plan.json && cd ../..
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0，空输出
jb inspectcode -f=Xml -e=HINT -o=/tmp/e2c/jb-inspectcode.xml WinForward.slnx   # 解析 XML：<Issue> 0

# 2. 判定比对（P1/P2）
R=.trellis/tasks/10-07-e2e-harness-refactor/research
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e2c/run1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict

# 3. 账本字节级（脚本见 §6.3 的口径说明，落在 /tmp/e2c/ledger-bytes.py）
python3 /tmp/e2c/ledger-bytes.py $R/baseline/run1/ledger.jsonl $R/record-normalize.json /tmp/e2c/run1/ledger.jsonl

# 4. 零宽键（D18.5 #12）
python3 /tmp/e2b1/zerowidth.py /tmp/e2c/run1/out | diff - /tmp/e2c/zerowidth-base.txt
```

本轮全部临时产物留在 `/tmp/e2c/`（发布产物 `pub/`、两次 selftest 产物 `run1`/`run2`、两份比对输出、
账本字节级脚本与输出、突变实验的备份文件、inspectcode XML 与日志）；仓库内只新增/修改 §1/§2/§3/§4
列出的源码、测试与本文档。判定运行（两次 selftest 与两次 compare-records）之间没有别的构建在跑，串行执行。

---

## 8. 偏离与登记（逐条声明）

### 8.1 声明的偏离

1. **`ArmKeys.Ledger` 的"层级常量"用的是"同一块两个层级各声明一份"**（D14.17 的字面口径），代价是
   `DnsTotals` 的 12 个拼写与 `DnsSummary` 逐字相同。**可判性边界**：拼写相同的两套常量互换
   （M4）在运行期**不可判**——账本写出的字节完全一样，没有任何测试能区分。因此"层级"这件事的可判部分
   是：① 拼写不同的层级（`tcp`/`udp`/`dns` 三块互换、`bytes` 与 `bytesEchoed` 之类）由形状测试双向判定（M3）；
   ② 拼写相同的层级互换由**声明纪律 + 键集参数**承担（writer 只能在调用点选择 `Summary`/`Target`）。
   若 check 认为这 12 份重复常量收益不足，替代方案是只声明一份"块"常量 + 在测试里登记"块×前缀"表——
   已记录，供 E5/后续裁定。
2. **一处可见性加宽（仅供形状测试驱动生产 writer）**：`TargetRunner.WriteSummariesAsync`
   （`private` → `internal`）。理由与 E2-b2 新增 `ResourceSampleWriter.WriteSampleHeader` 同：`targetSummary`
   记录的 JSON 就写在这个组合里（含条件 `dnsAlt` 容器），形状测试要经**生产 writer** 发布而不是复制一份。
   它在 internal 类上，生产面（public API/二进制行为）不变。**没有第二处**：`TcpTargetServer.WriteConnectionAsync`
   曾一并加宽，但形状测试改成走**真连接**（`SocketOps.SendCommandAsync` + 半关闭）后不再需要，inspectcode
   的 `MemberCanBePrivate.Global` 提示也确认了这点，已收回 `private`。
3. **测试侧重构**：`ContractShapeTests` 的私有 `Differences` 提为 `DeclaredKeys.Differences`（两个测试
   文件共用，仓库约定"≥2 文件重复就提取"），8 处调用点改名，消息格式逐字不变；`KeyLiteralsIn` 拆出纯文本
   重载供对照事实使用。没有放宽任何断言。

### 8.2 未做（按范围）

1. **E2-d（CLI 解析器合一）未做**：按范围，`ClientOptions`/`TargetOptions` 的 `TryCreate` 仍是两份。
2. **E3 语义修复未做**：账本三字段（`detail`/`acceptErrors`/`udpReceivers`）与 `Truncated` 语义仍按 D9
   留给 E3；本批只保证键族可判。
3. **E4 分析器未做**：`analyze.py:2119` 的 `ledger_views` 只读不写，本批没碰 `benchmarks/WinForward.E2E.Analysis/**`。
4. **`ArmKeys.Ledger` 的"块×前缀"表未引入**（见 §8.1.1）。

### 8.3 登记项

1. **键序契约的落点**：账本的键序由 `ArmKeys.Ledger` 的声明序 + 形状测试 F2 承载；分片不写"按声明序"的
   自动机制（writer 手写序），E3 若给账本加键（`detail`/`acceptErrors`/`udpReceivers`），必须同时改分片、
   writer 与 F1/F2 的期望表（`DeclaredPaths()`）。
2. **`ArmKeys.Ledger.TcpRecord` 的 `startedTicks`/`endedTicks` 与 `TargetSummary` 的同名常量各声明一份**：
   `type` 是"所有记录根上的同一个成员"故复用 `Common.Record.Type`；`startedTicks`/`endedTicks` 属于两个
   不同记录族，按"每族自证"各声明一份（若 check 认为应与 `Common.Record` 复用，改动是 1 行/处）。
3. **`Target/**` 的其它文件不设 gate 行**：`TcpConnectionProtocol.cs`/`SourceCensus.cs`/`TcpAcceptLoop.cs`/
   `SocketIo.cs`/`Sockets.cs`/`TargetLog.cs` 实测无 key 位字面量；一旦将来它们开始写账本键，gate 的
   `s_writerFiles` 需要同步补名（`File.Exists` 自检保证"漏名=红"，但漏一个**新**文件不会红）。
