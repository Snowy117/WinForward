# Tier 0 证据：D1–D7 与 `selftest.sh` 的实测命令与观测值

**二进制**：`/tmp/wf-bench/pub/linux/WinForward.E2E`，由 `benchmarks/WinForward.E2E/scripts/publish.sh`
强制重建（不是 selftest 的"缺了才 publish"）：

```
linux/WinForward.E2E.dll  sha256 9d715829c7d4bd31f5e3e827d7156a2936f86b79a6a9f7f3a9878b699346fa94   # A2 完工时
linux/WinForward.E2E.dll  sha256 05e8ae773fd4191b9b68c32a43188c8c1af928414bed6642ea66b86de87f787d   # check 轮：新增取消的 error 记录
linux/WinForward.E2E.dll  sha256 03b040afa360e75792a0e799c9e9f705bdd2d69ce42e89ec55509d20428c2012   # check 轮最终树：再加文本键的类型校验
```

下表的每一行都在 check 轮用最后一个二进制**重跑过**，命令与观测值均未变。

**判据通道**（DD D14.19）：加载期错误文本在单元层断言（`dotnet test`），**退出码**用发布后的二进制跑最小
plan 观测，命令与观测值记录在本文件。夹具全部在 `tests/WinForward.E2E.Tests/Fixtures/plans/`。

下文的 `$PUB` = `/tmp/wf-bench/pub/linux/WinForward.E2E`，`$F` = `tests/WinForward.E2E.Tests/Fixtures/plans`。

## 1. 逐条判据

| # | 缺陷 | 命令（`$PUB client --target 127.0.0.1 …`） | 观测退出码 | 观测错误文本（首行，原样） |
|---|---|---|---|---|
| 0.1 (D7) | `MarkSent` 越界写数组 | 单测 `ASendPastTheBoundedSequenceSpaceIsRefusedWithoutTouchingTheArrays` | — | 改前：`System.IndexOutOfRangeException`（该用例实测红，栈顶 `UdpReliabilityTracker.MarkSent`）；改后：绿，断言 `OutOfRange==1`、`SentOk==0`、`Outstanding==0`、`Supplied==0`、无桶记账（D14.5 配方） |
| 0.2 (D7) | 越界 `ratePerSecond×seconds` | `--out out-d7 --plan $F/overrun-loss-20000x20.json` | **2** | `e2e client: arm 'LOSSOVERRUN' (kind 'loss'): the schedule offers sequences up to 400000, past the tracker's MaxSequence of 262143` |
| 0.2 边界 | `262143/1s` 合法 | `--out out-at --plan $F/max-sequence-loss-at.json` | **0** | 臂正常跑完（`arm LOSSBOUND (loss) finished in 0.2s`，`run.json failed=false`）；`outOfRangeSequences=0` |
| 0.2 边界 | `262144/1s` 拒（loss） | `--out out-bo --plan $F/max-sequence-loss-over.json` | **2** | `… arm 'LOSSPAST' (kind 'loss'): the schedule offers sequences up to 262144, past the tracker's MaxSequence of 262143` |
| 0.2 边界 | `262144/1s` 拒（base） | `--out out-bo2 --plan $F/max-sequence-base-over.json` | **2** | `… arm 'BASEPAST' (kind 'base'): the schedule offers sequences up to 262144, past the tracker's MaxSequence of 262143` |
| 0.3 | 臂级异常逃逸（D1/D4 的共同放大器） | `--plan $F/arm-level-exception.json --out out-armfail --label a2` | **1** | 臂文件第一条记录：`{"type":"error","arm":"IDLEOVERFLOW","kind":"idle","label":"a2","error":"OverflowException","message":"TimeSpan overflowed because the duration is too long.","detail":"OverflowException","startedTicks":…,"endedTicks":…}`；`run.json` 存在且 `failed=true`、`arms[0].failed=true`。**只加 `Program` 层 catch 会没有 `run.json`**：异常会在 `WriteRunFileAsync` 之前离开臂循环 |
| 0.3 取消 | 取消与臂级错误留同样的证（DD D14.12） | `--out out-cancel --label sigterm --plan /tmp/cancel-idle.json`（单个 idle 600 s 臂，3 s 后 `kill -TERM`） | **1** | `error` 记录：`{"type":"error","arm":"IDLELONG","kind":"idle","label":"sigterm","error":"OperationCanceledException","message":"cancelled","detail":"TaskCanceledException",…}`；`run.json failed=true`、`arms[0].failed=true`；client 打印 `e2e client: interrupted; the arms above are incomplete.`。进程内判据：`ClientRunnerTests.ACancelledArmLeavesAnErrorRecordAndAFailedRunFile`（预取消的 token，无网络无时钟） |
| 0.6 补充 | 文本键类型错误（D5 的同族） | `--out out-proto --plan …/bad-protocol.json`（`{"protocol":5}`）与 `…/bad-mode-mix.json`（`{"modeMix":null}`） | **2** | `e2e client: arm 'LATBADPROTO' (kind 'latency'): 'protocol' is 5, which is not a string` / `arm 'RELBADMIX' (kind 'reliability'): 'modeMix' is null, which is not a string` |
| 0.4 (D2) | 显式空 `--plan=` | `--out out-d2a --plan=` | **2** | `e2e client: --plan needs a path; omit the option to run the built-in plan` |
| 0.4 (D2) | 显式空 `--plan ""` | `--out out-d2b --plan ""` | **2** | 同上（两条命令走同一条 `TryApply` 分支） |
| 0.4 | 省略 `--plan` → 内置 plan | `--out out-builtin --label a2`（6 s 后 SIGTERM） | **1**（中断） | `planPath=null`、`planSource="builtin"`；臂照常启动（`arm LAT (latency) starting`）。SIGTERM 后 `run.json` 仍然写出 |
| 0.5 (D3) | 映射后重名 | `--out out-d3 --plan $F/colliding-file-names.json` | **2** | `e2e client: arm names 'A/B' and 'A_B' both map to the output file 'A_B.jsonl'` |
| 0.5 (D4) | 臂名过长 | `--out out-d4 --plan $F/arm-name-too-long.json` | **2** | `e2e client: arm 'AAAA…/////'(270 字，原样打印) maps to a 270-character file name 'AAAA…_____', above the 128-character limit` |
| 0.5 | 输出路径总长 | `--out /tmp/a2-final/<240×d>/out` | **2** | `e2e client: the output path '…' is 258 characters long, above the conservative 250-character limit`（数字随 `--out` 前缀长度变化） |
| 0.5 | 输出路径单组件 | `--out /tmp/a2-final/<260×c>/out` | **2** | `e2e client: the output path '…' has a 260-character component, above the 255-character limit` |
| 0.6 (D1) | `dnsPort:99999` | `--out out-d1 --plan $F/dns-port-out-of-range.json` | **2** | `e2e client: arm 'DNSBAD' (kind 'dns'): 'dnsPort' is 99999, outside 0..65535` |
| 0.6 (D5) | `window:100.5` | `--out out-d5 --plan $F/fractional-window.json` | **2** | `e2e client: arm 'LATFRACTION' (kind 'latency'): 'window' is 100.5, which is not an integer` |
| 0.6 | 负值 | `--out out-neg --plan $F/negative-rate.json` | **2** | `… arm 'LOSSNEGATIVE' (kind 'loss'): 'ratePerSecond' is -1, outside 0..2147483647` |
| 0.6 | `dnsPort:0` 合法 | `--out … --plan $F/dns-port-zero.json` | 0 | 加载通过（`DnsPort == 0` = 未声明，单元层断言）；不许出现 dnsPort 相关错误 |
| AC7 | 未知 key | `--out out-ac7 --plan $F/unknown-key.json` | **2** | `e2e client: arm 'LOSS' (kind 'loss'): unknown key 'ratePerSeconds' (expected one of: name, kind, seconds, ratePerSecond, payloadBytes, window, lossWindowMs)` |
| 0.7 (D6) | 空 `--sampler-process=` | `--out out-d6 --sampler-process= --plan $F/minimal-idle.json` | **2** | `e2e client: --sampler-process needs a process name; a process is matched by name and an empty one matches nothing` |
| 0.7 | `selftest.sh` 漏 plan | `cd benchmarks/WinForward.E2E && scripts/selftest.sh` | **2** | `usage: scripts/selftest.sh <plan.json>` + `shipped plans: …/scripts/plans/*.json and …/scripts/plans-short/*.json`；usage 检查在起 target **之前**，退出后没有任何 target 进程残留（`pgrep -f WinForward.E2E` 只剩此前 campaign 留下的 40011 实例） |
| 0.8 | 值像选项 | `--target 127.0.0.1 --label --out x` | **2** | `e2e client: '--label' value '--out' starts with '-'; a value that looks like an option usually means its own is missing` |

## 2. 单测覆盖（`dotnet test tests/WinForward.E2E.Tests -c Release`）

`Passed! - Failed: 0, Passed: 103, Skipped: 0, Total: 103`（check 轮；A2 完工时为 95，新增 1 条取消用例 + 5 条边界 InlineData + 2 条文本键类型用例）。
与本批直接相关的用例：

| 用例 | 缺陷/步骤 |
|---|---|
| `UdpReliabilityTrackerTests.ASendPastTheBoundedSequenceSpaceIsRefusedWithoutTouchingTheArrays` | 0.1（D14.5 配方：不抛 ⇔ 数组未被写；不反射私有字段） |
| `UdpReliabilityTrackerTests.ACorruptDatagramNamingAnImpossibleSequenceIsRefusedAndCounted` | D14.5 的接收侧回归护栏（今天也通过） |
| `PlanFileValidationTests.ASchedulePastTheBoundedSequenceSpaceIsRefusedWithItsHighestSequence` | 0.2 |
| `PlanFileValidationTests.TheSequenceBoundaryIsExact`（4 条 InlineData） | 0.2（loss/base 各两个边界） |
| `PlanFileValidationTests.ATextKeyOfAnotherTypeIsRefusedWithTheValueAsWritten`（2 条） | 0.6 补充（`protocol`/`modeMix` 的类型校验） |
| `PlanFileValidationTests.AnUnknownKeyIsRefusedAndNamesTheArmAndTheKey` | AC7 |
| `PlanFileValidationTests.TwoArmNamesThatMapToTheSameFileAreRefused` | 0.5（D3） |
| `PlanFileValidationTests.AnArmNameTooLongToBeAFileNameIsRefusedWithBothNames` | 0.5（D4） |
| `PlanFileValidationTests.AnOutOfRangeDnsPortIsRefused` | 0.6（D1） |
| `PlanFileValidationTests.AFractionalValueOnAnIntegerKeyIsRefusedWithTheValueAsWritten` | 0.6（D5） |
| `PlanFileValidationTests.ANegativeValueIsRefused` | 0.6（D14.14 下界） |
| `PlanFileValidationTests.ADnsPortOfZeroMeansUndeclared` | 0.6（0 = 未声明） |
| `PlanFileValidationTests.TextValuedKeysAreAcceptedByTheKindsThatReadThem`（2 条） | 0.2（`modeMix` / `protocol` 文本键） |
| `PlanFileValidationTests.ALegalPlanWhoseArmThrowsIsStillLegalForTheLoader` | 0.3 的夹具必须能被加载（否则测不到臂级 catch） |
| `ClientRunnerTests.ACancelledArmLeavesAnErrorRecordAndAFailedRunFile` | 0.3 取消（D14.12 字段集 + `run.json failed=true` + 退出码 1；预取消 token，无网络、无真实等待、无顺序依赖） |
| `PlanFileValidationTests.TheSequenceBoundIsMeasuredOnlyWhereSequencesAreIndexed`（5 条 InlineData） | D14.4 的作用域与两个回退：latency 500/s×600 s 必须加载（300000 > 262143）；base 未声明 rate 时 524 s 通过 / 525 s 拒（回退 500/s）；mix 8738 s 通过 / 8739 s 拒（常量 30/s） |
| `ClientOptionsTests.AnEmptyPlanArgumentIsAUsageError`（2 条） | 0.4（D2） |
| `ClientOptionsTests.OmittingThePlanKeepsTheBuiltInPlan` | 0.4 |
| `ClientOptionsTests.AnEmptySamplerProcessIsAUsageError` | 0.7（D6） |
| `ClientOptionsTests.AValueThatLooksLikeAnOptionIsAUsageError` | 0.8 |
| `PlanFileTests.EveryLossAndMixArmDeclaresItsLossWindow` | `#6` 的 W 判据（11 份 shipped plan；失败打印 plan 路径 + arm 名） |

## 3. 夹具清单（`tests/WinForward.E2E.Tests/Fixtures/plans/`）

| 夹具 | 用途 | 加载期结果 |
|---|---|---|
| `overrun-loss-20000x20.json` | D7：20000/s × 20 s | 拒（400000 > 262143） |
| `max-sequence-loss-at.json` / `max-sequence-loss-over.json` | 0.2 边界（loss） | 通过 / 拒 |
| `max-sequence-base-at.json` / `max-sequence-base-over.json` | 0.2 边界（base） | 通过 / 拒 |
| `colliding-file-names.json` | D3：`A/B` + `A_B` | 拒 |
| `arm-name-too-long.json` | D4：270 字臂名 | 拒（sanitize 后 270 > 128） |
| `dns-port-out-of-range.json` | D1：`dnsPort:99999` | 拒 |
| `dns-port-zero.json` | 0.6：`dnsPort:0` = 未声明 | 通过 |
| `fractional-window.json` | D5：`window:100.5` | 拒 |
| `negative-rate.json` | 0.6：负值 | 拒 |
| `unknown-key.json` | AC7：`ratePerSeconds` | 拒（含 arm 与 key） |
| `mode-mix-text-key.json` | 0.2：`modeMix` 文本键 | 通过（reliability） |
| `protocol-text-key.json` | 0.2：`protocol` 文本键 | 通过（latency） |
| `minimal-idle.json` | 可跑的最小 plan（idle 1 s，不需要 target） | 通过 |
| `arm-level-exception.json` | 0.3：加载合法但臂内抛异常（`seconds:1e18` → `TimeSpan.FromSeconds` 溢出） | 通过（`ALegalPlanWhoseArmThrowsIsStillLegalForTheLoader`） |

## 4. 两条不在退出码通道里的判据

- **0.9**：`Client/UdpReliability.cs` 的 `MaxSequence` 注释改为不变量陈述，并指向加载期校验
  （`PlanFile` 的按 kind 校验）；零行为改动，代码评审项。
- **0.1 ⇔ 0.2 的互斥**是有意的（D14.5）：0.2 之后"越界 plan"在加载期就被拒，端到端跑不出
  `outOfRangeSequences > 0`，所以 0.1 的判据只能是单测。

## 5. check 轮补充（E1-A 第二轮复核）

- **0.1 的两次突变验证**（改前先 `cp` 到 `/tmp`，结果核对哈希）：
  ① 删掉 `MarkSent` 的边界守卫 ⇒ `ASendPastTheBoundedSequenceSpaceIsRefusedWithoutTouchingTheArrays`
  红（`System.IndexOutOfRangeException`，栈顶 `MarkSent`）；② 只删守卫里的 `_sent.TrySet(sequence)`
  ⇒ 同一条用例红在 `OutOfRange`（Expected 1 / Actual 0）。两次都按字节还原，
  `sha256 = be48e440be7e69301b0a6ab8d143ecacd0b5a3167e456e418c6ec0d67c211973`。
- **D14.4 的作用域**不只在单测里：见 §2 的 `TheSequenceBoundIsMeasuredOnlyWhereSequencesAreIndexed`。
  该用例的针对性也用突变证明过：给 latency 挂上同一个校验 ⇒ latency 行红；把
  `BaseArm.DefaultLossRatePerSecond` 改成 1000 ⇒ base 回退的两行红。
- **取消记录的 `detail`**：`error` 按裁定写成 `OperationCanceledException`（token 驱动的取消具体抛哪个
  子类型取决于发现它的 await），`message` 是改前的字面量 `cancelled`；`detail` 仍按 D14.12 的公式
  `GetBaseException().GetType().Name`，所以 `Task.Delay` 这条路径上是 `TaskCanceledException`。两者不一致
  是"`error` 记族、`detail` 记实际类型"的直接结果；若主会话要求一致，把 `detail` 也归一到
  `nameof(OperationCanceledException)` 即可（一行，未擅自改）。
- **0.6 补充：文本键的类型校验**（本轮新发现的静默口子）。加载期的类型校验原先只覆盖数值键：
  `{"protocol":5}` 会被 `TryReadText` 当作"读不出字符串"而退回默认 `tcp`，`{"modeMix":null}` 退回默认
  modeMix，两者都 **exit 0** 并发布一个 plan 从未声明的运行——与 D5 同一族，也让 README 那句
  "Nothing in a plan is silently ignored or silently clamped" 变成不实之词。现改为：键缺席取默认，
  键在场但类型不是字符串即 load error（`'protocol' is 5, which is not a string`，指名 arm/key/值）。
  判据：`PlanFileValidationTests.ATextKeyOfAnotherTypeIsRefusedWithTheValueAsWritten`（2 条）+
  发布二进制上的两条命令（见 §1 的 0.6 补充行，均 exit 2）。缺席键仍取默认（11 份 shipped plan 与内置
  plan 的 `TryLoad` 全部通过、0 误拒，见 §2 的 `EveryShippedPlanLoads` / `TheBuiltInPlanUsesTheDocumentedDefaults`）。
- **未新增夹具**：§3 仍是 16 个；边界扫描用 `PlanFileValidationTests.TryLoadJson` 现写一个临时单臂 plan
  （`Path.GetTempPath()` + GUID，与 `LedgerWriterTests` 同一手法），"at" 与 "over" 两侧因此都不需要文件。
- **数值键超过 `int` 上限时的措辞**（观察项，未改）：`"window": 3000000000` 走 `TryReadInt` 的非整数分支，
  报 `'window' is 3000000000, which is not an integer`——指名了 arm/key/值，但"not an integer"对一个整数值
  略失准。D14.14 的取值域判据不受影响（该值确实非法）。
- **`seconds` 没有上界**（D14.14 只要求 `> 0`）：`seconds: 1e18` 的 **loss** 臂仍在加载期被序列上界拒掉
  （`(long)` 转换在本运行时饱和到 `long.MaxValue`，报 `up to 9223372036854775807`）；同一个 `seconds` 的
  **latency** 臂（不受该校验）会加载、0.1 s 内结束，并在 `gates.scheduleTruncated=1`、`gates.laneShortfall=1`
  与配套 note 里披露"整条 schedule 没被提供"，而不是静默成功——`run.json` 仍是 `failed=false`，所以这属于
  E3 的 gate/分析器消费面，不是 Tier 0 的判据。

