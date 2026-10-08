# E1-B1a check 报告（Contracts `Json/` 原语 + `JsonlSink` 合一 + 调用点迁移）

被检查的树：`master` 未提交工作树（HEAD `c3fc3c8`）。所有门禁在 check 修复之后重跑。
本文件是 check 轮的证据留档，与 `B1a-vs-run1.md`（实现轮的回归比对）配套。

## 0. 结论一句话

**E1-B1a 可以提交**：异常语义矩阵、不可取消性、client 失败边界、字节级等价、部署完整性、规范与全部门禁
逐条通过；check 轮只做了 3 处小修（1 处 README 陈旧描述、2 处测试加固），没有发现实现缺陷。

## 1. 已修复的问题

| # | 文件:行 | 改了什么 | 为什么 | 反证方式 |
|---|---|---|---|---|
| 1 | `benchmarks/WinForward.E2E/README.md:42` | `Target/` 行的 "ledger writer" → "the ledger summaries, the target log" | `Target/LedgerWriter.cs` 已删除、新的 `Target/TargetLog.cs` 未被任何一行提到；旧描述指向不存在的文件概念 | `ls benchmarks/WinForward.E2E/Target/` 只剩 `DnsServer/TargetLog/TargetOptions/TargetRunner/TcpTargetServer/UdpEchoServer`；`rg -i 'ledger writer' README.md` 归零 |
| 2 | `tests/WinForward.E2E.Tests/JsonlSinkTests.cs:170-190`、`:248-259` | 新增 `APeriodicFlushFailureIsCountedAndNeverThrown`（`[Theory]` 双策略，2 例）+ `FlushFailingStream` 假流 | **1 Hz flush 失败**是 `RecordFailure` 里唯一没有调用者可以抛给它的分支，也是 `ClientRunner` 的 `WriteErrors > 0 → LostRecords` 分支的触发源；此前无任何常驻用例覆盖 | 突变 2（close 失败改为 Propagate 上抛）让该用例同批变红；`dotnet test` 计数 155 → 157 |
| 3 | `tests/WinForward.E2E.Tests/ClientRunnerTests.cs:60-62` | `Assert.Equal(1, types.Count(type => type == "error"))` | 任务书第 3 条要求核对"`error` 记录每臂最多一次"，原用例只断言 `>= 0` 与"在 result 之前"，重复记账不会被发现 | 断言直接钉在 `failureAttempted` 的语义上；当前实现只在 `ClientRunner.cs:396/401/413` 三处置位，每臂只有第一次落盘 |

范围声明：三处都在本轮改动面内（README 属同一批的文档同步，两个测试文件测的就是本轮新增的 `JsonlSink`
与 `WriteArmRecordsAsync`）。**未改动任何生产代码**——探针与突变全部按字节还原（见 §4）。

## 2. 未修复但需上报

| # | 项 | 原因 | 影响 | 是否阻塞提交 |
|---|---|---|---|---|
| 1 | `scripts/deploy-campaign.sh` 是 **gitignored** 的本机脚本，它已自带 `WinForward.E2E.Contracts.dll`（`:35`/`:38` 两处 stage 列表，且带注释"omits it deploys a client that cannot start"） | 该文件不在交付物里；`README.md:57` 只把它列为"机器专有文件，fresh checkout 里没有" | 新克隆的机器上部署 payload 由 E5/部署文档负责；`README.md` 的 publish 小节只列 `.exe` 级产物，没有点名 Contracts DLL | 否。但 **E5 需要在部署/布局文档里写明第二份 DLL**（已跟踪的清单纯净：`selftest.sh`/`orchestrator.ps1`/`publish.sh`/`start-targets.sh` 都不枚举 DLL，`publish.sh` 整目录拷贝） |
| 2 | `ClientRunner.WriteArmRecordsAsync` 的 catch 过滤掉了 `OperationCanceledException`（`ClientRunner.cs:429`） | 设计只要求"取消单独判"发生在臂体（`RunGuardedAsync` 已覆盖）；写路径全程 `CancellationToken.None`，实测不可达 | 若未来有人在写路径引入可取消 await，OCE 会逃出 `RunArmAsync` → `RunAsync`，`run.json` 不落盘 | 否（当前不可达） |
| 3 | `sink.WriteErrors > 0 → LostRecords` 分支（`ClientRunner.cs:410-417`）无常驻单测 | 需要给 `RunArmAsync` 注入失败 `Stream`，而它内部 `new JsonlSink(path, …)` 没有接缝；临时用 `/dev/full` 端到端证过（§3.3） | 该分支是"磁盘中途坏了也要让臂失败"的唯一实现；回归只能靠人肉 e2e | 否 |
| 4 | `_stream.DisposeAsync` 失败这一格只有探针覆盖（`TryCloseStepAsync` 三个 step 共用一个 helper，源码可见） | 常驻用例的 `FailingStream` 只让 Write/Flush 失败 | 覆盖面损失很小（同一 helper、同一计数路径） | 否 |
| 5 | 任务书写的"测试 +54 例"= **新增** 54 例；`LedgerWriterTests` 同时删掉 2 例，故净增 52。check 又加 2 例，现净增 54 | 记账口径 | 无语义影响 | 否 |

## 3. 逐条验证结论

### 3.1 异常语义矩阵（两策略 × 全部失败类）

探针 `tests/WinForward.E2E.Tests/ZzProbeTests.cs`（临时，8 个 fact 全绿后已删除），逐格结论：

| 失败类 | `Propagate` | `SwallowAndCount` | 证据 |
|---|---|---|---|
| body 抛 | 上抛 `InvalidOperationException`、`WriteErrors=1`、文件 0 字节（无半行） | 不抛、`WriteErrors=1`、文件 0 字节 | `BodyFailureCell_*`：`propagate=1 swallow=1` |
| 写失败 | 上抛 `IOException`、`WriteErrors=1` | 不抛、`WriteErrors=1` | `WriteFailureCell_*`：两侧都计数（含随后 close 的 flush 失败，各 2） |
| 周期 flush 失败 | 不抛（无调用者）、计数 | 不抛、计数 | `P4_*`/`S3_*`：`WriteErrors=1`（20 ms 注入间隔） |
| close 失败（flush 步） | **不抛**、计数 | 不抛、计数 | `CloseFailureCells_*`：两策略各 `WriteErrors=1` |
| close 失败（`DisposeAsync` 步） | **不抛**、计数 | 不抛、计数 | 同上，两策略各 1 |
| 关闭后写 | 上抛 `ObjectDisposedException`、计数 1 | 不抛、计数 1 | `WriteAfterCloseCell_*` |
| 取消（未取到锁） | 上抛 OCE、**不计数** | 上抛 OCE、不计数 | `CancellationCell_*`：`WriteErrors=0`、0 字节 |
| 半行恢复 | 第二条记录完好 | 第二条记录完好 | `HalfLineCell_*` → `{"type":"good"}\n` |

- 两策略 `WriteErrors` 语义一致：同一注入失败下计数相同（`WriteFailureCell_*` 输出 `propagate=2 swallow=2`）。
- `targetSummary.ledgerWriteErrors` 仍读同一计数：`TargetRunner.cs:97` `writer.WriteNumber("ledgerWriteErrors", ledger.WriteErrors)`；
  本轮 selftest 的 `targetSummary` 里该值仍为 `0`（§3.4 的 42/45 逐位相同项之一）。
- **突变验证**（备份 `/tmp/JsonlSink.cs.orig`，按字节还原并核对 sha256）：
  - 突变 A：把 `Serialize` 的 body 改成直写 `_stream` 的 writer（模拟"记录不再整条先入内存"）→
    `JsonlSinkTests.ABodyThatThrowsLeavesNoPartialLineAndTheNextRecordIsIntact` 与探针 `HalfLineCell_*` **双红**（`Assert.Equal Failure: Strings differ`）。
  - 突变 B：`TryCloseStepAsync` 在 `Propagate` 下 `throw;`（模拟"close 失败上抛"）→
    `JsonlSinkTests.ACloseFailureIsCountedAndNeverThrown` 与 `CloseFailureCells_*` **双红**（`IOException: injected flush failure`）。

### 3.2 不可取消性

- `rg -n 'cancellationToken|CancellationToken' benchmarks/WinForward.E2E.Contracts/Json/JsonlSink.cs`：
  调用者的 token **只**出现在 `:100` `_gate.WaitAsync(cancellationToken)`；
  `:116` 流写入是 `CancellationToken.None`；`:195`/`:205` close 路径是 `None`；`:216-224` 是内部 flush 循环（用 `_shutdown.Token`）。
- **突变 C**：`:116` 改为透传 `cancellationToken` → `JsonlSinkTests.ACancellationDuringTheWriteDoesNotCutTheRecordInHalf`
  **变红**（`TaskCanceledException`），而同一 `[Fact]` 组的 `ACancellationBeforeTheRecordIsTakenIsHonoured` 仍绿（证明突变定向、非整组崩）。
  还原后 sha256 `b54a13bf866f293d9a9d1ffc168b2d2697cea499658c6c0937c1f2704573dfd1`，`diff -q` 与备份一致。

### 3.3 client 失败边界

- **同一个 catch**：`ClientRunner.cs:397-427` 的 `try` 里依次是 `WriteFailureAsync`/`WriteResultAsync`/`WriteArmSummaryAsync`
  → `WriteErrors` 回读（`:410`）→ `sampler.ClearTargetAsync()`（`:419`）→ `sink.CompleteAsync()`（`:425`）→ 再回读（`:426`）；
  唯一的 `catch`（`:429`）覆盖全部四步。`await using`（`:325`）只是兜底：`CloseAsync` 以 `Interlocked.Exchange(ref _closed,1)` 幂等
  （`JsonlSink.cs:173`），三个 close step 全走 `TryCloseStepAsync`（不抛），因此显式 `CompleteAsync()` 之后再 `Dispose` 不会二次记账。
- **`error` 记录每臂最多一次**：`failureAttempted`（`:396`）在两种入口置位（臂体已失败 `:401`、丢记录 `:413`），
  catch 里 `if (!failureAttempted)`（`:433`）才补写；写失败再被 `TryWriteFailureAsync` 吞进 stderr（`:449-465`）。
  check 已把该条款钉进 `ClientRunnerTests`（见 §1 第 3 行）。
- **真实 sink 写失败仍写出 `run.json.failed=true`**（端到端，命令与观测值）：
  ```bash
  mkdir -p /tmp/wf-failprobe && ln -sfn /dev/full /tmp/wf-failprobe/IDLE.jsonl
  # plan: {"arms":[{"name":"IDLE","kind":"idle","seconds":4}]}
  /tmp/wf-bench/pub/linux/WinForward.E2E client --target 127.0.0.1 \
      --plan /tmp/wf-failprobe-plan.json --out /tmp/wf-failprobe --label failprobe
  # stderr: e2e jsonl /tmp/wf-failprobe/IDLE.jsonl: write failed 1 time(s): IOException: No space left on device
  # stdout: e2e client: arm IDLE (idle) finished in 4.0s (FAILED) / 1 arm(s) written
  # exit=1
  ```
  `run.json` 完整落盘（879 字节）：`"arms":[{"name":"IDLE",…,"failed":true}], "failed":true, "planSource":"file"`。
  这条路径真实走的是 `WriteErrors>0 → LostRecords → error 记录 → run.json.failed` 分支（flush 循环先记账），不是臂体异常路径。

### 3.4 字节级等价（新一次 selftest vs 基线 run1）

```bash
cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json     # exit 0
# 产物拷到 /tmp/b1a-check/{out,ledger.jsonl,target.out,client.out,console.log}
cd .trellis/tasks/10-07-e2e-harness-refactor/research
python3 ../../../../benchmarks/WinForward.E2E/scripts/compare-records.py baseline/run1 /tmp/b1a-check \
    --normalize record-normalize.json --band baseline/jitter-band.json --json-out /tmp/b1a-check-findings.json
# exit 1；summary: structural=1 conditional=0 numeric=579 measured=903
```

- **结构栏只有 1 条**：`records/run.json planSource: only in after` —— A2 引入、非本轮（`--json-out` 里 `column=structural` 唯一一条）。
- **条件键 0 条**；`target.out` 归一化文本 0 条（`diff` 原始三行也完全相同，只有时间戳行不同）。
- **记录构成逐位相同**：11 个臂文件行数与 `type` 直方图逐一相同（IDLE 7、REL 18 含 6 `attempt`、其余 10/12），
  `ledger.jsonl` 258 行 = 97 `udpSummary` + 157 `tcp` + 2 `dnsSummary` + 1 `tcpSummary` + 1 `targetSummary`。
- **`targetSummary` 总量逐位相同**：键集与键序一致，45 个叶子里 42 个相同，只有 `utc`/`startedTicks`/`endedTicks`（时钟）不同：
  `ledgerWriteErrors=0`、`udp.received/bytes/undecodable/sendErrors = 6967/1419144/0/0`、
  `tcp.connections/protocolErrors = 157/0`、`tcp.verdicts` 8 项全同、`dns.*`/`dnsAlt.*` 各 12 项全同。
- **579 条数值越带项独立分类**（不照抄 `B1a-vs-run1.md`）：按路径去重得 200 个 `(文件, 路径)` 对，
  逐条落进 D15 的身份类（`processes/pid`）、时钟类（`*Ticks`/`ticks`/`wallSeconds`/`elapsedSeconds`/`connectTicks`）、
  测量读数类（`*Us`/`*Ms`/`*Bytes`/`cpuSeconds`/`threads`/`achievedRate`/`windowCeilingMs`/`inFlightCeilingMs`/
  `goodput*`/`ledger 的 bytes|received|sources/datagrams`）。**契约计数类 0 条越带**：
  `sentOk`/`supplied`/`arrived`/`late`/`never`/`clientSendLoss`/`windowOverflow`/`connectAttempts`/`framesSent`/
  `budgetReached`/`scheduleTruncated`/`verdicts.*`/`dns.*`/`outcomes.*`/`rcodes.*`/`queryTypes.*`/`idleLanes` 一个都没出现。
- **生产 envelope**：`JsonlSinkLedgerTests.cs:27` 与 `:75` 直接传 `TargetRunner.WriteLedgerEnvelope("probe-label"|"append")`，
  没有测试自建 lambda；envelope 本体在 `TargetRunner.cs:50-55`（`utc` + `label`）。

### 3.5 部署完整性 / 最高风险后果

- `scripts/publish.sh` 带 `-p:AssemblyName=WinForward.E2E.Direct` 的三份产物：
  `win-direct/` = `WinForward.E2E.Direct.exe/.dll/.pdb/.deps.json/.runtimeconfig.json` **加** `WinForward.E2E.Contracts.dll/.pdb`；
  `win/`、`linux/` 同理保留 `WinForward.E2E.Contracts.dll`。两个 Windows 客户端仍按镜像名可区分（脚本末行自检通过）。
- **`TreatAsLocalProperty="AssemblyName"` 必要且最小**：去掉它后立刻
  `dotnet publish … -p:AssemblyName=WinForward.E2E.Direct` → `error : Ambiguous project name 'WinForward.E2E.Direct'`（NuGet.targets:198，exit 1），
  与 csproj 注释里的原话逐字一致；还原后 sha256 `f826757d2d1d8c601f3b160dc4f42821440aa4988f5297ccd6e49834fa947c66` 与备份一致。
  最小性：不能用 Directory.Build.props（全局更糟），也不能只在 publish.sh 里回避——全局属性会流经 `ProjectReference`，
  `TreatAsLocalProperty` 是唯一"把重命名关在这一个项目里"的单点属性。
- 已跟踪清单复查：`publish.sh` 整目录 publish、`selftest.sh` 直接跑 `$pub/linux/WinForward.E2E`、
  `orchestrator.ps1` 只引用两个 `.exe`、`start-targets.sh`（gitignored）只跑 linux 二进制 →
  **没有任何已跟踪脚本/清单需要补 DLL**。补 DLL 的那一步只存在于 gitignored 的 `deploy-campaign.sh`（且它已经补好）→ 见 §2 第 1 条（E5/部署文档）。
- 三份发布产物的 `WinForward.E2E.dll` sha256 = `312c3917…`、`WinForward.E2E.Contracts.dll` = `15f43bde…`，
  与 `B1a-vs-run1.md` 记录的两侧哈希逐位相同（check 期间 6 次发布/还原后仍然如此，`Deterministic=true` 生效）。

### 3.6 规范与工程质量

- **公共面最小**：Contracts 只有 `JsonlSink`（含 `WriteErrors`/`WriteAsync`/`CompleteAsync`/`DisposeAsync`）、
  `JsonlPolicy`、`NumberFormat`、`JsonRate`、`JsonPerSecond`；两个内部 ctor（path/stream + flushInterval）是仅有的 internal 面。
- **`InternalsVisibleTo WinForward.E2E.Tests` 确实被用到**：删掉该 ItemGroup 后
  `dotnet build tests/WinForward.E2E.Tests -c Release` 报 ≥15 条 `CS1729: 'JsonlSink' does not contain a constructor that takes 4 arguments`
  （`JsonlSinkTests.cs:25/38/53/65/79/101/121` 等），随即按字节还原。
- **有效行 ≤400**：新增/改动文件最大者 `Json/JsonlSink.cs` 198、`JsonlSinkTests.cs` 193、`TargetLog.cs` 17；
  超出 400 的 7 个文件（`LatencyArm` 799、`MixArm` 670、`ReliabilityArm` 604、`ClientRunner` 562、`DnsArm` 488、
  `ResourceSampler` 460、`PersistentArm` 441）**在 HEAD 就已超标**（逐文件 HEAD↔now 有效行对照），
  且 DD `design-decisions.md:542` 明确把它们的拆分划给 E2。
- **`NumberFormat` 与旧 `JsonValue` 逐位相同**（脚本抽取方法体比对）：
  `Round` 全同（仅形参名 `digits`→`decimals`）、`Microseconds` 全同、`Rate` 与旧 `Ratio` 全同（仅多了 `NumberFormat.` 限定）；
  `PerSecond` 唯一差异是 `ticks <= 0` 时 `0` → `null`，即 D14.23/prd 明示的有意变更。
- **旧实现无别名残留**：`rg -n 'Ratio|PerSecond|Round\(|Microseconds' benchmarks/WinForward.E2E/Client/JsonValue.cs` 归零；
  `JsonValue` 只剩 B2 要用的 dispatcher + `WriteProperties`（5 个调用点，均在 `ClientRunner` 待 B2 迁移）。
  调用点计数：`JsonPerSecond.PerSecond` 9 处、`JsonRate.Rate` 14 处，与任务书数字吻合。
- **无变更日志式注释**：新代码里的 `Counter-target:` 注释描述的是"这条用例拒绝什么实现"（回归测试的契约陈述），
  不是"我这轮改了什么"；无 `TODO`/`FIXME`/调试输出残留（rg 归零）。

### 3.7 门禁复核（全部修复之后，同一棵静止的树）

| 门禁 | 命令 | 结果 |
|---|---|---|
| publish | `cd benchmarks/WinForward.E2E && scripts/publish.sh` | 退出 0；三份产物；产物哈希与 §3.5 一致 |
| build | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| test（E2E） | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 157, Total: 157` |
| selftest | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出 0（11 result / 11 armSummary） |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0、**0 字节输出** |
| inspectcode | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | 解析 XML：`<Issue>` **0**、`<IssueTypes>` 空、无 `CSharpErrors`；日志确认 `JsonlSink.cs`/`NumberFormat.cs`/`TargetLog.cs`/4 个新测试文件都被检视 |

## 4. 突变验证结果汇总

| # | 目标契约 | 突变 | 期望 | 观测 | 还原 |
|---|---|---|---|---|---|
| A | body 抛不留半行 | `Serialize` 的 body 直写 `_stream` | 必红 | 2 条用例红（`Strings differ`） | sha256 `b54a13bf…`，`diff -q` 一致 |
| B | close/dispose 失败不抛 | `TryCloseStepAsync` 在 `Propagate` 下 `throw;` | 必红 | 2 条用例红（`IOException`） | 同上 |
| C | 记录写中不可取消 | `:116` 透传 `cancellationToken` | 必红 | `ACancellationDuringTheWrite…` 红、同组"取消发生在取锁前"仍绿 | 同上 |
| D | `TreatAsLocalProperty` 必要性 | 删除该属性后跑 win-direct publish | 必失败 | `error : Ambiguous project name 'WinForward.E2E.Direct'`，exit 1 | sha256 `f826757d…`，`diff -q` 一致 |
| E | IVT 必要性 | 删除 `InternalsVisibleTo` 后 build 测试工程 | 必失败 | 14+ 条 `CS1729` | sha256 `f826757d…`，`diff -q` 一致 |

突变 D/E 只改项目文件、不改行为；A/B/C 只改 `Json/JsonlSink.cs`，全部按字节还原并核对 sha256（见各条）。
