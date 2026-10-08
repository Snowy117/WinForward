# E3-c 回归比对与定向证据（`FrameReadStatus.Truncated` + 两台 server 各自记账）

本批是 E3 的第三批（`10-07-e2e-e3-semantics/implement.md` 的 E3-c 节）。**`src/` 一行未改，本轮未提交。**

判定线（逐条见 §5）：**冻结用例先红后绿**；成对反证（完整帧后 EOF ⇒ `EndOfStream` / 半帧后 EOF ⇒ `Truncated`）
10 条突变全部被断言抓住（§3）；结构面只剩 E3-b1 已登记的三条 note 更正（`structural=3`，本批**零新增**），
契约栏 `contract=0`（零越带）；新键 4 条以 `added` 登记（§4，D19.3 A）；零宽键 39 对 / 19 条三跑逐值相同；
`--strict` 读数摘要见 §5.4；六条门禁 + `effective-lines.py` 全绿（§5.1）。

> **本批没有改任何已发布数值**：107 条相关 (记录, 路径) 里 103 条三跑同值，4 条是本批新键（run1 缺席 →
> post1/post2 为 0），1 条（`udpSummary/received` 的区间计数）是宿主噪声且
> **在冻结带宽内**（契约栏 0 越带）。

---

## 0. 结论一句话

`FrameReadStatus` 新增第 6 个成员 `Truncated`（`Frame`/`EndOfStream`/`Truncated`/`BadMagic`/`BadLength`/
`BadChecksum`），EOF 落在帧中间不再是干净的 `EndOfStream`：**它不接进任何 verdict 枚举**（D19.3 D），
而是由四个消费点各自表态——lane 传输 → `IoError`+`FrameDecodeError.Truncated`（终止）、
`ReliabilityExchange` → 显式 `case`（协议错误、**不设** `Eof`）、
`TcpConnectionProtocol` → `TcpVerdict.ProtocolError` + `tcpSummary/truncatedFrames++` + **跳过 trailer**、
DNS 回答器 → **自己的**长度前缀短读计数（不复用该成员）。两台 server 的计数各以四个新键落到账本的两层。

---

## 1. `Truncated` 的产生、映射与表态（三处落点）

### 1.1 产生：`Wire/FrameStreamReader.cs`

```csharp
if (!await FillAsync(cancellationToken).ConfigureAwait(false))
{
    // The stream ended: with nothing buffered the last frame ended exactly where the peer
    // stopped writing, but with bytes still buffered the frame they belong to was cut in
    // half and its boundary can never be found.
    return _end > _start ? FrameReadStatus.Truncated : FrameReadStatus.EndOfStream;
}
```

- 判据只有一条：**EOF 时缓冲区里还有字节**（`_end > _start`）。缓冲为空的 EOF 仍是 `EndOfStream`。
- 因此三种"半个帧"都被覆盖：半个 header（<28 B）、header 完整而 payload 未完、payload 完整而 trailer 未完
  （后两者走的是同一个 `available < frameLength` 分支）。
- 终态是**粘的**：`_endOfStream` 置位后 `FillAsync` 立即返回 false，缓冲里的字节不动，
  下一次 `ReadAsync` 仍报 `Truncated`（用例 `TruncationIsReportedAgainOnTheNextRead`）。
- 枚举成员的 XML 文档逐字写了它"与 `EndOfStream` 不同、与 `BadMagic`/`BadLength` 同族、不是 verdict"。

### 1.2 映射：四个消费点（每个都显式）

| 消费点 | 映射 | 依据 |
|---|---|---|
| `Client/Lanes/TcpLaneTransport.ReceiveAsync` | 独立 `case FrameReadStatus.Truncated:` ⇒ `LaneReceiveResult(LaneReceiveKind.IoError, 0, FrameDecodeError.Truncated)`（终止） | D18.6 #3：与 `BadMagic`/`BadLength` 同族（`IoError` 终止），但 `Detail` 保留自己的原因；`default:` 改成"每个 reader 状态都必须在上面点名"的兜底注释 |
| `Client/Arms/ReliabilityExchange.ReceivePhaseAsync` | 独立 `case` ⇒ `attempt.ProtocolError = true; return;`（**不设** `attempt.Eof`） | D19.3 ②（前提复核）：`default:` 会把新成员吸收成"protocol error"而不自知。语义选择：`Eof` 是"peer 在帧边界关闭"的观察，半帧关闭不是它；于是 `Classify` 走 `protocolError && !eof` ⇒ `OtherError`（终态），与 `BadMagic`/`BadLength` 的既有归属一致 |
| `Target/TcpConnectionProtocol`（连接入口 + 回显循环） | `TcpVerdict.ProtocolError` + `protocolErrors++` + `TcpModeOutcome.Truncated = true`；**不调用 `CompleteOnEndOfStreamAsync`** ⇒ 不写 trailer | D19.3 D |
| `Client/Arms/PersistentExchange.TryReadFrameAsync` | 与 `BadMagic`/`BadLength` 同组 ⇒ `state._protocolErrors++` + `Dead` | 与上一条同族；此前它由 `default:` 吸收（行为相同，现在显式） |
| `Client/Arms/MixPageLoop` / `MixBulkLoop` / `ThroughputArm` | 走各自既有的"不是帧就是错误"桶（`status != Frame`） | 无需改动：半帧在改前也落这个桶（`EndOfStream` 对 MIX 两处同样计错误；THRU 改前把半帧当**干净结束**、改后计 `protocolErrors`，这正是本批要的效果——见 §3.5） |

### 1.3 成对反证（D19.3 K）

`tests/WinForward.E2E.Tests/FrameStreamReaderTests.cs`（6 → **10** 条）：

| 事实 | 输入 | 断言 |
|---|---|---|
| `EndOfStreamInsideAFrameIsReportedAsTruncated`（**原冻结用例**） | `frame[..10]`（半个 header） | `Truncated` |
| `EndOfStreamInsideAPayloadIsReportedAsTruncated` | 一帧拆三段，最后一段截短（header 完整、payload 未完） | `Truncated` |
| `AWholeFrameFollowedByACutOneKeepsTheWholeFrame` | 完整帧 + 半帧 | `Frame`（序号=原序号）→ `Truncated` |
| `TruncationIsReportedAgainOnTheNextRead` | 半帧，连读两次 | 两次都 `Truncated` |
| `EndOfStreamAtAFrameBoundaryIsReportedAsEndOfStream`（既有，未改） | 完整帧 | `Frame` → `EndOfStream` |
| `AnEmptyStreamIsReportedAsEndOfStream`（新） | 空流 | `EndOfStream` |

**冻结用例确实先红**（`/tmp/e3c/frozen-red.txt`，改动只落在 `FrameStreamReader.cs` 之后、改测试之前）：

```console
[xUnit.net] WinForward.E2E.Tests.FrameStreamReaderTests.EndOfStreamInsideAFrameIsReportedAsEndOfStream [FAIL]
  Assert.Equal() Failure: Values differ
Expected: EndOfStream
Actual:   Truncated
  at .../FrameStreamReaderTests.cs:line 66
Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 144 ms
```

改完测试后同一过滤条件 **10/10 绿**（`/tmp/e3c/frozen-green.txt`）。

### 1.4 接缝与臂级事实

| 用例 | 装置 | 断言 |
|---|---|---|
| `LaneTransportTests.APeerFinInsideAFrameIsTerminalTruncation` | 真 loopback peer：发 10 B 半帧后 `Shutdown(Send)` | `IoError` + `Detail == FrameDecodeError.Truncated` + `Length == 0`（与既有 `APeerFinEndsTheStream` 同文件成对：后者仍 `EndOfStream`） |
| `ReliabilityTruncationTests.AnAttemptWhoseStreamEndsInsideAFrameIsAProtocolErrorAndNotACleanEof` | 真 `ReliabilityExchange.RunAttemptAsync`（mode `clean`、4096 B）+ loopback peer：读掉命令帧 → 回 10 B 半帧 → `Shutdown(Send)` → 继续读干净（避免 RST 把截断变成 socket 错误） | `Observed == OtherError`、`ProtocolError == true`、`Eof == false`、`Echoed == 0` |
| `ReliabilityTruncationTests.TheAttemptNamesTheTruncatedStatusRatherThanLettingTheDefaultAbsorbIt` | 源码装置（空白不折叠，逐字子串） | `ReliabilityExchange.cs` 里有 `case FrameReadStatus.Truncated:` |

---

## 2. trailer 的阳性与阴性证据（D19.3 D / K）

`tests/WinForward.E2E.Tests/TruncatedConnectionTests.cs`——两条事实发**同一条** `halfClose` 命令帧，
唯一区别是流在哪结束；`RunTargetAsync` 起真 `TcpTargetServer`（真 accept loop、真 socket），
关掉 listener 之后才写摘要，所以记录一定在断言之前落盘。

| 事实 | 驱动 | 实测断言 |
|---|---|---|
| `AConnectionCutInsideAFrameIsAProtocolErrorAndEarnsNoTrailer`（负） | `halfClose` 命令帧（完整）→ 下一帧只发 10 B → FIN | 客户端读到 **0 字节**（没有 trailer）；tcp 记录 `verdict == "protocolError"`；`tcpSummary/truncatedFrames == 1` 且 `protocolErrors == 1` |
| `ACleanHalfCloseStillReceivesTheWholeTrailer`（正） | 同一条命令帧 → FIN（帧边界） | 收到 **864 B** = `TrailerFrameLength(288) × FrameCount(3)`；解出 3 帧、payload 合计 **768 B** = `TrailerProtocol.TotalBytes`；`verdict == "halfClose"`；`truncatedFrames == 0` |

- **阳性对照的作用**：一个"干脆不写 trailer"的改动会让负事实绿、正事实红；一个"截断也写 trailer"的改动
  （突变 M2，§3）让负事实红且实测 `Expected: 0 / Actual: 864`。两条缺一不可。
- `SendTrailerAsync` 与 `CompleteOnEndOfStreamAsync` 的整体接线**未动**：截断路径根本不进入
  `CompleteOnEndOfStreamAsync`（判据在 `RunModeAsync` 的分支里）。
- 同一条判据也在账本形状测试里：`LedgerShapeTests.PublishLedgerAsync` 现在多驱动一条
  "命令帧 `halfClose` + 10 B 半帧 + FIN" 的连接（`RunOneTruncatedConnectionAsync`），
  于是 `tcpSummary` 的 measured 态固定为 `connections=2` / `clean=1` / `protocolError=1` / `truncatedFrames=1`。

---

## 3. 十条突变（逐条实测；每条都校验还原后 sha256 逐位相同）

装置：`/tmp/e3c/mutate.sh`、`mutate2.sh`、`mutate3.sh`、`mutate4.sh`、`mutate5.sh`
（打补丁 → `dotnet build` → `dotnet test --filter` → `cp`+`touch` 还原 → `sha256sum -c`）。
**还原一律用 `cp` + `touch`，不用 `mv`**（E3-b1 §3.1 的教训：`mv` 保留 mtime 会让 MSBuild 跳过重建）。

| # | 突变 | 杀它的断言 | 实测 |
|---|---|---|---|
| M1 | `ReadAsync` 的 EOF 分支改回恒 `EndOfStream`（= 改前行为） | 五条 reader 事实 + 传输/臂级/账本事实 | **红 9 条**：`FrameStreamReaderTests` 4（`Expected: Truncated / Actual: EndOfStream`）、`LaneTransportTests` 1、`ReliabilityTruncationTests` 1、`TruncatedConnectionTests` 1、`LedgerShapeTests` 2；同时绿 21 条（其余 reader 事实不误伤） |
| M2 | 截断分支改走 `CompleteOnEndOfStreamAsync`（= 截断也写 trailer） | 负事实的 0 字节 + 计数 | **红 3 条**：`Expected: 0 / Actual: 864`（trailer 真被写了）、`truncatedFrames` `Expected: 1 / Actual: 0`、账本三态事实红 |
| M2b | `truncated: true` → `truncated: false`（表态对了但不计数） | `tcpSummary/truncatedFrames == 1` | **红 3 条**：`Expected: 1 / Actual: 0` |
| M3 | 删掉 `TcpLaneTransport` 的 `case`（退回 `default:` 吸收） | 传输事实的 `Detail` | **红 1 条**：`Expected: Truncated / Actual: None` |
| M4 | REL 的显式分支改成 `attempt.Eof = true`（把半帧当干净 EOF） | 臂级事实 | **红 1 条**：`Expected: OtherError / Actual: UnexpectedEof` |
| M4b | 删掉 REL 的 `case`（退回 `default:` 吸收） | 源码装置 | **红 1 条**（源码装置）；臂级事实**仍绿**——登记为可判性边界（§6-2） |
| M5 | `DnsServer` 的短读判据改成 `EndOfStream`（数错了那一半） | 账本 DNS 三态 + 两 listener 分离 | **红 2 条**：`Expected: 1 / Actual: 0` |
| M6 | `TcpTargetServer` 的 `command.Outcome.Truncated` 换成 `ProtocolErrors > 1`（计数条件错） | 负事实 + 账本三态 | **红 3 条**：`Expected: 1 / Actual: 0` |
| M7 | `SocketIo.ReadExactAsync` 把短读报成 `EndOfStream`（DNS 看不见截断） | 账本 DNS 事实 | **红 2 条**：`Expected: 1 / Actual: 0` |
| M8 | 在 `DnsServer.cs` 里加一句提到 `FrameReadStatus` 的注释 | DNS 独立性源码装置 | **红 1 条**：`Assert.DoesNotContain() Failure: Sub-string found` |

> **M6 的第一版不可编译**：把 `Interlocked.Increment(ref _truncatedFrames)` 删掉后
> `TreatWarningsAsErrors` 直接把 `CS0649`（字段从未赋值）变成 build error。
> 也就是说"计数点消失"这条突变在编译期就被拦住，比测试更早——记录在案，
> 实际用的 M6 是"计数条件错"（可编译、测试红）。

**还原**：`Wire/FrameStreamReader.cs`、`Target/{TcpConnectionProtocol,TcpTargetServer,DnsServer,SocketIo}.cs`、
`Client/Lanes/TcpLaneTransport.cs`、`Client/Arms/ReliabilityExchange.cs` 七个文件在每条突变后
`sha256sum -c` 全部 `OK`（`/tmp/e3c/sha-before.txt`；其中 `FrameStreamReader.cs` 在格式化门禁修掉
`RCS1268` 之后重录过基线并重跑 M1，见 §5.1 注）。

### 3.5 本批在其他臂上的**有意**行为变化（半帧不再读成干净结束）

| 臂 | 改前（半帧 + EOF） | 改后 | 说明 |
|---|---|---|---|
| THRU (`ThroughputArm.ReceiveLoopAsync`) | `EndOfStream` ⇒ `break`，**不计任何错误** | `protocolErrors++` 后退出 | 半帧从"干净结束"变成"协议错误"，正是本批要的语义 |
| PERSIST (`PersistentExchange`) | `remoteClosed++` | `protocolErrors++` | 同上；`remoteClosed` 只留给帧边界关闭 |
| MIX page/bulk | `!= Frame` ⇒ 自有错误桶 | 同一个桶 | 无变化 |
| REL | `Eof = true` ⇒ `UnexpectedEof`/`HalfCloseViolation` | `ProtocolError`（无 `Eof`）⇒ `OtherError` | §1.2 的裁定；两条都是"未达模式契约"，`metrics/mismatches` 与 `metrics/truncated`（回显短于声明即计）不受影响 |
| LAT/LATLOAD（lane 接缝） | `EndOfStream` ⇒ `RemoteClosed++` | `IoError` ⇒ `ProtocolErrors++` | `TcpLaneTransport` 的映射 |

**干净 selftest 上这些计数器全是 0**（§5.3），所以这些变化只在真的发生截断时才可见。

---

## 4. 账本键扩容（D19.3 B）与登记（D19.3 A）

### 4.1 四处齐动

| 处 | 文件 | 内容 |
|---|---|---|
| 两层常量 | `benchmarks/WinForward.E2E.Contracts/ArmKeys.Ledger.cs` | `TcpSummary.TruncatedFrames`、`TargetSummary.TcpTotals.TruncatedFrames`、`DnsSummary.TruncatedFrames`、`TargetSummary.DnsTotals.TruncatedFrames`（写序：TCP 在 `protocolErrors` 之后、`verdicts` 之前；DNS 在 `tcpMalformed` 之后、`tcpConnections` 之前） |
| keyset 结构体 + 实例 | `Target/TcpTargetServer.cs` 的 `TcpTotalsKeys`、`Target/DnsServer.cs` 的 `DnsTotalsKeys` | 各加一个属性 + `Summary`/`Target` 两个实例的参数 |
| writer | `TcpTargetServer.WriteTotals`、`DnsServer.WriteTotals` | `Interlocked.Read(ref _truncatedFrames)` / `_tcpTruncated` |
| 计数点 | `TcpTargetServer.HandleConnectionAsync`（`command.Outcome.Truncated` ⇒ `Interlocked.Increment`）、`DnsServer.ReadExactAsync`（`ReadExactOutcome.Short` ⇒ `Interlocked.Increment`） | 两台 server 各一个 `long` |

`DnsTotalsKeys` 的字段声明序、ctor 参数序与写序三者一致（`LedgerShapeTests` 的写序事实读的就是声明序）。

### 4.2 DNS 的截断是独立定义（D19.3 C）

- `Target/SocketIo.ReadExactAsync` 的返回类型从 `bool` 改成三态 `ReadExactOutcome { Complete, EndOfStream, Short }`：
  `offset == 0` 的 EOF 是 `EndOfStream`（**不计数**），读了半截的 EOF 是 `Short`（计数）。
- `DnsServer` 新增私有 `ReadExactAsync` 包装：`Short` ⇒ `_tcpTruncated++`；长度前缀与消息体两次读都走它。
  **`DnsServer.cs` 里没有 `FrameReadStatus`、没有 `FrameStreamReader`**（源码装置 `TheDnsListenerCountsItsOwnShortReadsRatherThanTheFrameReadersTruncation` 钉住这一点，M8 证明它不是空断言）。
- 键名含义写在分片 `<remarks>`：`DnsSummary.TruncatedFrames` 的 `<remarks>` 逐字说明"读两字节长度前缀再读该长度，
  任一次读短了就是一条消息被截断，与 `TcpSummary.TruncatedFrames` 同键名不同机制"；`Ledger` 类级
  `<remarks>` 也加了一段"同名的叶子可以由两个家族用两种机制发布，各家族各有常量、各读各的机制"。
- 键名与 TCP 侧**字面相同**（`truncatedFrames`）——这正是 `contract-rename.json` 里只有 4 个新路径
  （而不是 5 个）的原因：账本组内 `tcpSummary` 根与 `dnsSummary` 根拍平到同一个 canonical path。

### 4.3 登记：`added`（D19.3 A）

`scripts/contract-inventory.py` 的 `ADDITIONS` 新增 4 条；用本批的 post1 重新生成
`research/contract-rename.json` 与 `.md`：

```console
$ python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename \
      --baseline $R/baseline/run1 --run /tmp/e3c/post1 \
      --out-json $R/contract-rename.json --out-md $R/contract-rename.md
renames landed in this run: 9; still published under the old spelling: 0; not observed at all: 0
$R/contract-rename.json: 599 row(s) = added 10, identical 580, renamed 9
declared but not observed in the fresh run: 2
```

- JSON 差量：**只有 4 行新增**（`only in new` = 4 条 `added`；`only in old` 空；既有行 reason 逐字未变）。
- MD 差量：`fresh run` / `rows` 两行 + `## Added` 表 4 行（`+6 −2`）。
- `declared but not observed: 2` = `detail`/`error`（E3-b1 既有，正常跑不写 error 记录时缺席）；
  本批 4 条**全部**在 post1 里被观测到（含 `dnsAlt/truncatedFrames`，selftest 的 target 起第二个 DNS 端口）。
- `--batch B2` 仍是 `9/9 satisfied`（改名行不带本批的 batch 字段，`added` 按既有惯例不带 batch，见 E3-b1 §5.2）。

---

## 5. 六条门禁与等价性

### 5.1 门禁（本批冻结树，逐条串行）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | 14 个程序集全绿，`rc=0`；`WinForward.E2E.Tests` **297**（E3-b2 的 286 + 11） |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出** |
| 4 | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`（单独跑） | `<Issue>` **0**、`<IssueType>` **0**（见 §5.5 的三次迭代） |
| 5 | `cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3c/pub scripts/publish.sh` | exit 0；三份产物；`linux/WinForward.E2E.dll` sha256 `01ba9f4e30dab487472491b4fcc157b25b6e731691ebd9d4f64483641722e2cf` |
| 6 | `WF_PUB=/tmp/e3c/pub scripts/selftest.sh scripts/plans/selftest-plan.json` ×2 | 两次 **exit 0**（post1 / post2；**在冻结树上重新跑的**，§5.2 的两栏读数就是这两跑） |

`python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E
benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests`：**无输出、exit 0**。

> **注（门禁之间的迭代，登记）**：`dotnet format` 第一次跑出 1 条 `RCS1268`（`_end - _start > 0` 建议写成
> `_end > _start`），改掉后重录了 `FrameStreamReader.cs` 的 sha256 并**重跑 M1**（`/tmp/e3c/mutations5.log`，
> 仍红 9 条，还原逐位相同）。`jb inspectcode` 第一次跑出 4 条（`ConvertIfStatementToSwitchStatement`、
> 两条 `UnusedVariable`、一条 `ConvertToConstant.Local`），第二次 1 条
> （`SwitchStatementHandlesSomeKnownEnumValuesWithDefault`），第三次干净——三次都在 §5.5 逐条记录。
> **第一次整解 `dotnet test` 有 1 条红**（`WinForward.Performance.Tests`，137 条里的 1 条分配门禁），
> 单跑该程序集 **137/137 绿**，随后两次整解 `-m:1` 全绿（`rc=0`）：这是 `hot-path.md` §6 已登记的
> 宿主残余分配抖动，本批**未碰 `src/`**，登记而非解释。

### 5.2 判据比对（`compare-records.py`）

```console
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e3c/post1 \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
```

| 对照 | structural | conditional | identity | declared | contract | rename | readings | exit |
|---|---|---|---|---|---|---|---|---|
| run1 → post1（**判据**） | **3**（E3-b1 §4 的三条 note，本批零新增） | 0 | 0 | **34** | **0** | 0 | 291/366（210 越带） | 1（仅那三条 note） |
| post1 → post2（同二进制噪声地板） | 0 | 0 | 0 | 0 | **20** | 0 | 285/366（150 越带） | 1 |

- **第 5 栏 `declared=34`** = 改名命中 27 + 本批 4 个新键（`truncatedFrames`、`tcp/truncatedFrames`、
  `dns/truncatedFrames`、`dnsAlt/truncatedFrames`）+ E3-b1 的 3 个 `sentOutOfRangeSequences` 路径；
  `added observed/not observed` 里"not observed"仍只有 `detail`/`error`。
- **第 1 栏三条**与 E3-b1 §4 逐字相同（`records/{LOSS,BASE,MIX}.jsonl` 的 `notes`），**不是本批造成的**：
  run1 冻结在 E3-b1 之前。本批**没有**新增结构差异（`arity`/`kind`/其它字符串/`target.out` 全 0）。
- **第 4 栏 20 条全部是 "no recorded jitter band for this path"**：本批 4 条新键（无带宽，值为 0）+ E3-b1 的
  16 条既有族（13 改名后无带宽 + 3 个 `sentOutOfRangeSequences`）。**没有一条是"值动了"**。
- `--strict` 读数：post1 是 291/366（210 越带），post2 是 285/366（150 越带）⇒ 越带条数是宿主噪声；
  **本批没有任何读数路径被改动**（新键是计数类，`analyze.py` 不渲染它们）。

### 5.3 selftest 无误报（D19.3 K ④）

两次 selftest 的账本（post1 / post2 各自）：

```console
post1: tcpSummary truncatedFrames=0 protocolErrors=0 connections=157
       verdicts={'clean': 61, 'reset': 25, 'partialFin': 25, 'halfClose': 25, 'clientClosedEarly': 21}
       dnsSummary port=5301 truncatedFrames=0 tcpQueries=81 tcpAborted=0 tcpMalformed=0
       dnsSummary port=5302 truncatedFrames=0 tcpQueries=81 tcpAborted=0 tcpMalformed=0
       targetSummary tcp={..., 'protocolErrors': 0, 'truncatedFrames': 0} dns/truncatedFrames=0 dnsAlt/truncatedFrames=0
post2: 同上（逐值相同）
```

- `truncatedFrames == 0` 在**两台 server、两层、两次运行**上都成立 ⇒ 干净 half-close 没有被误判成截断
  （阳性对照在 §2 的单测里，人为半帧 ⇒ `tcpSummary/truncatedFrames == 1`）。
- 判决分布（61/25/25/25/21）与 run1 相同 ⇒ 截断路径没有吃掉任何正常连接的 verdict。

### 5.4 数值变化逐条登记

以 107 条与本批相关的发布路径（TCP/DNS 两族计数 + 客户端侧四个消费点的计数器）对 run1 / post1 / post2
逐值（多重集）核对（`/tmp/e3c/values2.py`）：

| 结果 | 条数 | 明细 |
|---|---|---|
| 三跑同值 | **103** | 含 `tcpSummary/{connections,protocolErrors,verdicts/*}`、`targetSummary/tcp/*`、两族 DNS 的全部 TCP 计数、`LAT/LATLOAD` 的 `latency/*.protocolErrors`、`REL` 的 `unexpectedEof`/`otherError`/`mismatches`/`truncated`、`THRU.protocolErrors`、`PERSIST.{protocolErrors,remoteClosed}` |
| 新键（run1 缺席 → post1/post2 = 0） | **4** | `truncatedFrames`、`tcp/truncatedFrames`、`dns/truncatedFrames`、`dnsAlt/truncatedFrames` |
| 变动（非本批，在带宽内） | **1** | `ledger/udpSummary/received` 的区间计数（post1 vs post2 也不同；**契约栏 0 越带**） |

**没有移动的**（逐条核对）：`tcpSummary/protocolErrors`（0）、`targetSummary/tcp/protocolErrors`（0）、
`dnsSummary/{tcpQueries,tcpAnswers,tcpEmptyAnswers,tcpMalformed,tcpConnections,tcpAborted}`（81/77/4/0/1/0）、
`REL/metrics/{unexpectedEof,otherError,timeout,connectFailures,mismatches,truncated}`（全 0）、
`THRU/metrics/protocolErrors`（0）、`PERSIST/metrics/{protocolErrors,remoteClosed}`（0/0）、
`LAT,LATLOAD/metrics/latency/*.protocolErrors`（0）。

### 5.5 零宽发布键（D18.5 #12）

```console
$ for d in $R/baseline/run1 /tmp/e3c/post1 /tmp/e3c/post2; do python3 /tmp/e3a/zerowidth.py $d/out > zw-$(basename $d).txt; done
$ diff zw-run1.txt zw-post1.txt && diff zw-run1.txt zw-post2.txt
（无输出）
```

**39 个 (记录, 路径) 对 / 19 条不同路径**，三跑逐值相同（与 E3-b1 §5.4 完全一致）：
`LAT` tcp `received` 322 / udp 161、`LATLOAD` 两向各 1601、`BASE latency/*` 各 101、`DNS`/`DNSALT` 402、
`MIX` 146/602/8、`PERSIST` 15；全部 `outstandingAtTeardown` 与 `unmatchedReplies` 为 0。

### 5.6 inspectcode 的三次迭代（登记）

| 次 | `<Issue>` | 内容 | 处置 |
|---|---|---|---|
| 1 | 4 | `ConvertIfStatementToSwitchStatement`（`TcpConnectionProtocol.cs:117`）、`UnusedVariable` ×2（`LedgerShapeTests.cs:437` 的 `var length`、`TruncatedConnectionTests.cs:52` 的 `var length`）、`ConvertToConstant.Local`（`TruncatedConnectionTests.cs:36` 的 `static readonly int`） | 两个 `var length = frame.Build(...)` 改成语句调用；`s_trailerFrameLength` → `const TrailerFrameLength` |
| 2 | 1 | `SwitchStatementHandlesSomeKnownEnumValuesWithDefault`（改造后的 `RunModeAsync`；同一次 `dotnet format` 还报了 `S3458` 空 case ×3 与 `MA0051` 方法 62 行 > 60） | 改用"两个顶层 `if` + 嵌套 `if`"的形状（**≥3 个同变量顶层 `if` 才会触发那条提示**；switch 形状又会同时触发 `S3458`/`IDE0066`/`MA0051`），并顺带把 `connectionId` 局部量换成 `reader.Header.ConnectionId` |
| 3 | **0** | — | 冻结 |

---

## 6. 偏离与登记

1. **`PersistentExchange` 的显式 `case` 是本批的主动扩围**（任务只点了 `TcpLaneTransport` 与
   `ReliabilityExchange`）：新成员在第四个 `FrameReadStatus` 消费点同样会被 `default:` 静默吸收，
   而 D19.3 D 的原则是"每个消费点自己表态"。行为与 `default:` **完全相同**（`_protocolErrors++` + `Dead`），
   纯显式化；`Mix{Page,Bulk}Loop`/`ThroughputArm` 用的是"不是帧就是错误"的桶，语义本来就对，未动。
2. **`ReliabilityExchange` 的显式分支与 `default:` 在臂级不可区分**（M4b）：`default:` 恰好也把
   `Truncated` 记成"协议错误且不设 `Eof`"，所以删掉 `case` 后臂级事实仍绿，只有源码装置红。
   与 E3-b1 §2.4/§9.5 的 REL 调用点同型：**双装置（行为 + 源码）是必要的**，登记为可判性边界。
3. **`Truncated` 在 DNS 侧不是同一个概念**：DNS 用 `ReadExactOutcome.Short`（长度前缀短读），
   键名与 TCP 侧相同但机制不同——这是 D19.3 C 要求的分片 `<remarks>` 与源码装置共同钉住的。
4. **本批不动 `tcpSummary` 的 verdict 分布**：截断走既有 `TcpVerdict.ProtocolError`，
   不新增 verdict 成员（D19.3 D / D19.2 ⑪），所以 `verdicts` 对象逐字节不变。
5. **未做（按严格范围）**：`environment.json` 的截断环境键（前提复核条目 17 提到它，但本轮的任务范围
   只列了"两台 server 各自记账（账本/摘要键）"四处扩容；`orchestrator.ps1` 是 Windows 侧、
   本机无法端到端验证）——**留给父代理裁定**；E3-d 的 `detail`/`acceptErrors`/`udpReceivers` 与
   `--udp-receivers`、E3-e 的 ODE/`achievedRate` 未动；分析器的截断 caveat 与 `make_tree.py` 注入
   （前提复核条目 17 的"分析器 caveat"半）**不在本轮范围**（任务范围 1–6 只要求账本键与表态）。
6. **`analyze.py` 一行未改**：本批的新键是计数类，分析器不消费它们；`verdict.json`/`tables.md` 因此
   逐字节不变（`compare-records.py` 的读数栏也没有任何路径被本批触碰）。

---

## 7. 重建方式（check 轮可逐条重跑）

```console
R=.trellis/tasks/10-07-e2e-harness-refactor/research

# 门禁（串行！不要与 jb inspectcode 并发）
dotnet build WinForward.slnx -c Release                                     # 0/0
dotnet test WinForward.slnx -c Release -m:1                                 # 14 程序集全绿（E2E 297）
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
    benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests          # 无输出、exit 0
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0、空输出
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e3c/pub scripts/publish.sh
WF_PUB=/tmp/e3c/pub scripts/selftest.sh scripts/plans/selftest-plan.json    # ×2（post1/post2）
jb inspectcode -f=Xml -e=HINT -o=/tmp/e3c/jb.xml WinForward.slnx            # XML 里 <Issue> 0

# 定向事实
dotnet test tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj -c Release \
    --filter "FullyQualifiedName~FrameStreamReaderTests|FullyQualifiedName~TruncatedConnectionTests|FullyQualifiedName~ReliabilityTruncationTests|FullyQualifiedName~LedgerShapeTests|FullyQualifiedName~LaneTransportTests"

# 判据比对
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/e3c/post1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict    # structural=3（E3-b1 的 note）、contract=0
python3 benchmarks/WinForward.E2E/scripts/compare-records.py /tmp/e3c/post1 /tmp/e3c/post2 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json --strict   # 噪声地板 contract=20

# 注册表重生成 + 数值/零宽键核对
python3 benchmarks/WinForward.E2E/scripts/contract-inventory.py rename --baseline $R/baseline/run1 \
    --run /tmp/e3c/post1 --out-json $R/contract-rename.json --out-md $R/contract-rename.md
python3 /tmp/e3c/values2.py                       # 107 条：103 同值 + 4 新键 + 1 宿主噪声
for d in $R/baseline/run1 /tmp/e3c/post1 /tmp/e3c/post2; do python3 /tmp/e3a/zerowidth.py $d/out; done | diff

# 十条突变：/tmp/e3c/mutate{,-2,-3,-4,-5}.sh（补丁表就是 §3 的逐行文字）
```

---

## 8. 给 check 轮的独立复核点

1. **冻结用例是否真的先红**：在只改 `Wire/FrameStreamReader.cs` 的树上跑
   `--filter FullyQualifiedName~EndOfStreamInsideAFrame`，应看到 `Expected: EndOfStream / Actual: Truncated`。
2. **成对反证是否成对**：`APeerFinEndsTheStream` 与 `APeerFinInsideAFrameIsTerminalTruncation` 同文件相邻，
   驱动只差 10 字节；`TruncatedConnectionTests` 的两条事实发同一条命令帧，只差流的终点。
3. **trailer 阳性对照不能被删**：把 `AConnectionCutInsideAFrameIsAProtocolErrorAndEarnsNoTrailer` 里那条
   `halfClose` 命令改成 `clean`，`AConnectionCut...` 会失去它的意义（`clean` 本来就没有 trailer）——
   复核"两条事实必须发同一个 mode"这一点。
4. **DNS 的独立定义**：`rg -n 'FrameReadStatus|FrameStreamReader' Target/DnsServer.cs` 应无输出；
   `rg -n 'ReadExactOutcome' Target/{DnsServer,SocketIo}.cs` 应同时命中。
5. **账本两层与两 listener 的分离**：`LedgerShapeTests.TheTruncationCountersReachBothLevelsAndEachListenerCountsItsOwn`
   （第一个 DNS listener 1、第二个 0、`targetSummary/{tcp,dns,dnsAlt}` 与各自记录逐值相等）。
6. **新键只增**：`compare-records.py` 第 5 栏里本批 4 条必须在 `rename-table` 命中，
   `contract-rename.json` 的 JSON 差量只有 4 行 `added`。
