# C1 计划对抗性复核（plan review）

**日期**：2026-10-09 · **性质**：只读复核，未改任何代码或计划；唯一写入的文件是本报告。
**方法**：逐条把计划里的载荷性断言拿去对代码/门禁脚本求证；能便宜跑的命令都跑了
（`oracle-diff.py --help`、`--batch` 解析、`check-fairness.py --help`、`check-boundary-trees.py --help`、
`effective-lines.py`、以及大量 `rg`/`python3` 只读复算）。**没有**跑 build、analyzer、
`dotnet format`、`jb inspectcode`。

---

## 判决

**可执行，但必须先修四处**（executable with fixes）。S1–S6 的大方向对：可换实现 / 实现即契约的
两分法站得住，S4 在 S5 之前、先看见 75 条差异再放宽的顺序是对的，S5 的「按路径放宽」在
`oracle-diff.py` 里**确实挂得上**（不是 BLOCKER）。但计划里有 **2 个 BLOCKER**（S0.1 的五个调用点写成了
写侧文件，照做达不到目标；S5.2 删 `AnalyzerRandomGoldenTests.cs` 与 AC4 的「`StableHash`/`DeriveSeed`
被测试钉住」直接矛盾），**2 个会让门禁在错误步骤变红的步骤边界错误**（S2/S3 会先弄坏
`AnalyzerJsonGoldenTests.cs`/`AnalyzerNumberGoldenTests.cs` 的编译，而计划把它们的处置放在 S5），
外加若干会留下悬空引用的遗漏（`RepoPaths.AnalyzerGolden`、`DeriveSeed` 的另外两个调用者、
`VerbatimNumber.cs` 的文档）。S2 的技术前提（`UnsafeRelaxedJsonEscaping`）被仓库自己的金标
**证伪**：金标 `py-verdict.json` 里非 ASCII 字节数为 **0**、`\u` 转义 **360** 个，而计划推荐的那个
编码器恰好不转义非 ASCII。S4 的判据**可测**（differ 会打印 `differences: N structure, M value, K missing`
与逐条 `[value]` 行），但措辞需要收紧才能区分「75 个不同路径」与「75 条条目」。

---

## Findings

### BLOCKER 1 · S0.1 的五个 `Truthy` 调用点写成了写侧文件，照做达不到目标

`implement.md:17-18` 写：

> `JsonValue.Truthy` → 已有的 `IsTrue`（5 个调用点：`ResourceSampleWriter.cs:80,100`、
> `ResourceSampler.cs:164`、`RunFileWriter.cs:52,57`）

这五个位置是 `research/01` **E9 的「事实：这些键只可能是布尔」那一段的写侧证据**
（`01-python-emulation.md:446-448`），不是调用点。实测调用点只有五处，全在 `Analysis` 树：

```
benchmarks/WinForward.E2E.Analysis/Model/RunSamples.cs:86
benchmarks/WinForward.E2E.Analysis/Model/RunSamples.cs:269
benchmarks/WinForward.E2E.Analysis/Tables/GateFlow.cs:233
benchmarks/WinForward.E2E.Analysis/Tables/TableEnvironment.cs:222
benchmarks/WinForward.E2E.Analysis/Tables/TableEnvironment.cs:317
```

（`rg -n 'JsonValue\.Truthy' benchmarks/ tests/` 的全部输出；`01-python-emulation.md:442-443` 列的
也是这五处。）而计划点名的 `ResourceSampleWriter.cs:80,100` 是
`writer.WriteBoolean(ArmKeys.Sample.ProcessEntry.CountersRead, …)` 与
`writer.WriteBoolean(ArmKeys.Sample.ReadError, value: true)`——它们在 `WinForward.E2E`（harness）
而不是 `WinForward.E2E.Analysis`，**根本引用不到 `JsonValue`**，`rg` 在主树里对这三个文件零命中。
按计划字面执行会改错文件、真正的五处不动，于是 `IsTruthy` 仍有调用者、删不掉，
S0.1 的唯一产出落空（而且不会编译报错，属于静默失败）。

**改法**：把 `implement.md:17-18` 的括号内容原样换成：

```
（5 个调用点：`Model/RunSamples.cs:86,269`、`Tables/GateFlow.cs:233`、
`Tables/TableEnvironment.cs:222,317`；写侧证据见 `research/01` E9 `:446-448`）
```

### BLOCKER 2 · S5.2 删掉唯一钉住 `StableHash`/`DeriveSeed` 的测试，与 AC4 自相矛盾

`prd.md:34`（AC4）要求：

> `CpRandom.cs`、`PythonExponential.cs`、三张向量表、`make_cp_vectors.py`、
> `AnalyzerRandomGoldenTests.cs` 已删除；`StableHash`/`DeriveSeed` 以新家存活**并被测试钉住**

`implement.md:74-75`（S5.2）要求删 `AnalyzerRandomGoldenTests.cs`。而
`tests/WinForward.E2E.Tests/AnalyzerRandomGoldenTests.cs:93-102`
（`TheDerivedSeedIsTheReferenceFormula`）是**整个仓库里唯一**驱动 `CpRandom.StableHash` /
`CpRandom.DeriveSeed` 的测试：

```
:99   Assert.Equal(1973867047u, CpRandom.StableHash(metricKey));
:100  Assert.Equal(867047u, CpRandom.StableHash(metricKey) % 1000000u);
:101  Assert.Equal(21128053, CpRandom.DeriveSeed(20261006, metricKey));
```

删掉整个文件后，`StableHash`/`DeriveSeed` 既没有测试，也（见 SHOULD-FIX 3）在新家里不会被
任何测试引用——AC4 的后半句无法达成。

**改法**：`implement.md:74` 改成「把 `AnalyzerRandomGoldenTests.cs` **重写**成
`AnalyzerComparisonSeedTests.cs`：删 `:18-91` 三个 `CpRandom` 测试，保留 `:93-102` 并把
`CpRandom.StableHash`/`DeriveSeed` 改名为 `ComparisonSeed.StableHash`/`DeriveSeed`」，
并在 S5 判据里加一条 `rg -n 'ComparisonSeed\.(StableHash|DeriveSeed)' tests/` 非空。

### BLOCKER 3（步骤边界）· S2 与 S3 会在自己那一步就把测试文件弄得不编译，而处置写在 S5

`implement.md:38`（S2）说「先写一条『解析后等价』的单测，再切约 140 个调用点，最后删手写转义」，
`implement.md:46-52`（S3）说换掉 `Fixed`/`Json`/`General`。但：

- `AnalyzerJsonGoldenTests.cs` 的**七个测试方法全部**直接调用 `VerbatimJson` 的
  `Object`/`Array`/`StringArray`/`String`/`Integer`/`Number`/`Boolean`/`Null`
  （`:37-40,48,56-62,68-70,80-84,90-96`）。S2 一旦把调用点切到 `Utf8JsonWriter`
  （`Utf8JsonWriter` 是流式 writer，没有 `Object(level, params …)` 这种返回 `string` 的形状），
  这个文件**编译不过**，`dotnet test` 连构建都过不了。
- `AnalyzerNumberGoldenTests.cs` 同理：`:29,47,66,73,95,108,128,135-143,151-159` 全是
  `VerbatimNumber.Fixed/General/Json/Cell` 的直接断言，其中 `:85-115`
  （`TheMidpointsOfTheGridRoundToTheEvenNeighbour`、`TheMidpointTableTellsHalfToEvenFromHalfAwayFromZero`）
  断言的正是 S3 要删掉的半偶行为。
- 而这两处测试的处置写在 `implement.md:76-78`（S5.3）。

所以「每一步 = 一个可独立回滚的 commit」+「基线 `dotnet test` 全绿」这两条在 S2/S3 上不成立：
S2、S3 各自都会留下一个**测试项目编译失败**的 commit，回滚 S3 也回不到绿（S2 的破坏还在）。

**改法**：把 S5.3 拆到 S2/S3：

- `S2` 追加一条：「同步重写 `AnalyzerJsonGoldenTests.cs`：保留『解析后等价』这一条，
  `:33-41`/`:73-85`/`:87-97` 改写为新 writer 的等价断言，删 `:43-71` 的逐字符转义断言；
  `py-json-vectors.json` 的退役仍留在 S5.3」。
- `S3` 追加一条：「同步删 `AnalyzerNumberGoldenTests.cs:17-34,80-115,130-144`（`Fixed` 半偶与
  `Cell` 回退的逐字断言随实现一起退役），保留 `:117-128` 的 `General` 形状表并改写为断言新的
  格式串行为」。
- 或者（更省事）：明确写出「S2、S3 的 commit 允许 `dotnet test` 不绿，S5 之后才恢复」，
  但这与 `implement.md:3-10` 的通用前置和父设计 `design.md:201` 的回滚点叙述冲突，需要一并改。

### BLOCKER 4（S2 技术前提被证伪）· `UnsafeRelaxedJsonEscaping` 与金标的转义方向相反

`implement.md:39-41` 要求「写手要设 `Indented = true` 与 `UnsafeRelaxedJsonEscaping`」。
实测金标：

```
benchmarks/WinForward.E2E.Analysis/verification/golden/py-verdict.json
  反斜杠 u 转义     : 360
  非 ASCII 字节     : 0
```

即参考侧把**所有**非 ASCII 写成 `\uXXXX`（`ensure_ascii=True`），而
`JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 的卖点恰恰是**不转义**非 ASCII。仓库自己的
`VerbatimJson.cs:30-37` 也把转义规则写死了：「`\uXXXX` for everything else outside the printable
ASCII range」；测试把它钉在 `AnalyzerJsonGoldenTests.cs:52-63`
（`Assert.Equal("\"\\u2013\"", VerbatimJson.String("–"))`、`"\\u007f"`、`"\\ud83d\\ude42"`）。
换成 relaxed 编码器后 `verdict.json` 会第一次出现非 ASCII 字节，`--mode byte` 的红面**远大于必要**。

计划在下一条留了后路（「若默认编码器行为不同，选一个能保持现有测试语义的 `JavaScriptEncoder`」），
但这条路也走不通：**没有任何内建编码器能同时满足金标的两条约束**——

- `JavaScriptEncoder.Default` 转义所有非 ASCII ✅，但它同时转义 `+`、`<`、`>`、`&`、`'` ❌，
  而金标里有 **22 个字面 `+`**，且 `AnalyzerJsonGoldenTests.cs:43-49`
  明说「`'`, `+`, `<`, `>`, `&` and `/` … are not」。
- `UnsafeRelaxedJsonEscaping` / `Create(UnicodeRanges.All)` 方向相反 ❌。

也就是说，要保持现有转义语义，只能**手写一个 `JavaScriptEncoder` 子类**（覆写
`Encode`/`EncodeUtf8`/`EncodedLength`）——那时「删手写转义」这个卖点就不成立了，代码量未必比现在少。

**改法**（二选一，必须写进计划而不是留给实施者猜）：

1. **推荐**：S2 降级为「只把 `VerbatimJson.String` 的转义委托给一个显式的
   `JavaScriptEncoder`（自定义 or `Default`）+ 把 `Number` 换成 .NET 格式化」，**不引入
   `Utf8JsonWriter`**；并明确写出「金标要求转义全部非 ASCII、放过 `'+<>&/`；`Default` 与
   `UnsafeRelaxed` 各错一半，所以自定义编码器是唯一选项」。
2. 如果确实要 `Utf8JsonWriter`：把 `implement.md:39-41` 改成
   「设 `Indented = true`（`JsonWriterOptions.IndentCharacter` 默认空格、
   `IndentSize` 默认 2，与 `json.dumps(indent=2)` 一致），编码器用**自定义**
   `JavaScriptEncoder`：放过 `0x20..0x7E`，其余（含 `0x7F`、非 ASCII、代理对）写成
   `\uXXXX`；不要用 `UnsafeRelaxedJsonEscaping`（实证：金标非 ASCII 字节 0、`\u` 转义 360）」。

### SHOULD-FIX 1 · `RepoPaths.AnalyzerGolden` 会成为死代码，计划未提

`tests/WinForward.E2E.Tests/RepoPaths.cs:22-29` 的 `AnalyzerGolden(name)` 只被三张向量表的三个测试
调用（`rg -n 'AnalyzerGolden' tests/ benchmarks/` → `AnalyzerNumberGoldenTests.cs:21,40,60`、
`AnalyzerRandomGoldenTests.cs:22`、`AnalyzerJsonGoldenTests.cs:19`）。三张表删掉后它零调用者，
文档注释里还留着 `make_cp_vectors.py` 的名字（`RepoPaths.cs:23-26`）。父任务调研已经点名了这件事
（`01-python-emulation.md:651-654`：「三张表删掉后这个方法没有调用者，应一并删除」），**C1 计划漏掉了**。

**改法**：`implement.md:79` 的 S5.4 追加一项：
「删 `tests/WinForward.E2E.Tests/RepoPaths.cs:22-29` 的 `AnalyzerGolden` 及其文档注释」。

### SHOULD-FIX 2 · `DeriveSeed` 有两个额外调用者，计划只提 `BootstrapPair`

`implement.md:60-61` 只说把 `StableHash`/`DeriveSeed` 从 `CpRandom` 摘到
`Stats/ComparisonSeed.cs`。实测 `CpRandom.DeriveSeed` 在 `BootstrapPair` 之外还有两个调用点，
另有一处 `<see cref>`：

```
benchmarks/WinForward.E2E.Analysis/Checks/ControlDrift.cs:197     CpRandom.DeriveSeed(campaign.Seed, …)
benchmarks/WinForward.E2E.Analysis/Metrics/MetricComparisons.cs:225  CpRandom.DeriveSeed(campaign.Seed, …)
benchmarks/WinForward.E2E.Analysis/Metrics/MetricComparisons.cs:22   <see cref="CpRandom.DeriveSeed"/>
```

漏掉的话 S5.2 删 `CpRandom.cs` 时编译失败（好在是硬失败，不是静默）。

**改法**：`implement.md:60` 追加：「同时改 `Checks/ControlDrift.cs:197`、
`Metrics/MetricComparisons.cs:225` 与 `MetricComparisons.cs:22` 的 `<see cref>`」。

### SHOULD-FIX 3 · S4 的字段清单与 `holm_p_value` 的实际情况

`implement.md:63-64` 写「差异字段只有 `p_value`/`p_equivalence`/`holm_p_equivalence`」，
而 `prd.md:32-33`（AC2/AC3）把 `holm_p_value` 也算进「`holm_*`」并列入放宽名单。
两者不矛盾但容易误读：金标里 `holm_p_value` 有 **212** 处，但它在冻结树上**不会动**——它是
Holm 校正后的单调上包，饱和于 `1.0`，而基线里动的那些对的 `p_value` 多数已经饱和到 `1.0`。
这正是 spike 表（`00-rng-swap-spike.md:38-42`）里只有 58+10+7=75、没有 `holm_p_value` 行的原因。

**改法**：`implement.md:63-64` 改成「差异字段为 `p_value`(≈58)/`p_equivalence`(≈10)/
`holm_p_equivalence`(≈7)；`holm_p_value` 在金标里 212 处但都饱和在 `1.0`，本次观察不到变化，
仍列入 S5 的放宽名单作为前瞻」。

### SHOULD-FIX 4 · S5 的路径写法不是 differ 里的真实路径

`implement.md:69-73` 与 `prd.md:33` 用 `metrics/*/pairs[*]/{p_value,…}` 与 `ci95[0]`/`ci95[1]` 的
花括号记法。differ 内部构造出来的 path **是点分**的（`oracle-diff.py:1016` 起 `verdict.json:<slice>`，
再经 `:991`/`:999` 逐级追加）：

```
verdict.json:metrics/lat.tcp_rtt.p50.pairs[8].p_value
verdict.json:metrics/loss.lossRate.pairs[11].ci95[0]
```

注意 metric 成员名本身含点（`lat.tcp_rtt.p50`），所以 `metrics/*` 里的 `*` 不能当成「任意字符」，
`pairs` 前那个分隔符在 differ 的 path 里是 `.` 而不是 `/`。照计划的斜杠写法去写正则，一条都匹配不上。

**改法**：`implement.md:69-73` 的路径说明改成：

> 命中 `compare_json` 收到的 path 满足
> `re.fullmatch(r"verdict\.json:metrics/.+\.pairs\[\d+\]\.(p_value|holm_p_value|p_equivalence|holm_p_equivalence|ci95\[[01]\])", path)`

（`.+` 里含 `.` 是刻意的：metric 成员名本身就是点分标识符。）

### SHOULD-FIX 5 · 「绝对 1e-2」对 `ci95` 不成立，理由句也是错的

`implement.md:71` 的理由是「（建议绝对 `1e-2`，这些量是 `count/10000`）」。实测：`ci95` 不是
`count/10000`——它是**该度量的比较尺度**上的一个区间，`p_value`/`p_equivalence` 才是：

```
metrics/lat.tcp_rtt.p50      unit=us  ci95 ∈ [0.960, 1.054]  （比值尺度，跨度 0.094）
metrics/latload.tcp_rtt.p99  unit=us  ci95 ∈ [0.942, 1.039]  （跨度 0.096）
metrics/loss.lossRate        unit=pp  ci95 ∈ [-0.4, 0.4]     （跨度 0.8）
```

`p_value` 的粒度确实是 `1e-4`（实测取值 `0.5148 / 0.518 / 0.5216 / 0.526 / 0.5262 …`），
所以 `1e-2` 对它们是「宽 100 个原子」，够用；但对 `loss.lossRate` 的 `pp` 区间，`1e-2` 只有
0.8 跨度的 1.2%，bootstrap 重抽完全可能一次跳得更多——这条放宽会**实现不了它自己声明的目的**
（`design.md:54-56` 明说是为了「给真实 campaign 埋雷」）。

**改法**：`implement.md:71` 把「建议绝对 `1e-2`，这些量是 `count/10000`」改成
「`p_value`/`holm_p_value`/`p_equivalence`/`holm_p_equivalence` 用绝对 `1e-2`（它们是
`count/10000`，粒度 `1e-4`）；`ci95[0]`/`ci95[1]` 是度量尺度上的区间（实测跨度 0.09–0.8），
用**相对**容差 `max(1e-2, 1e-2 * abs(expected))`，并在 docstring 里写明这两类的粒度不同」。

### SHOULD-FIX 6 · 计划的「不许动」名单不完整：`JsonReader` 的 `utf-8-sig` 容忍

`implement.md` 的 keep 列表（`prd.md:35` AC5 + `design.md:13`）是
`PosixPathText`、`PythonGlob`、`DescriptiveStats.Sum`（Neumaier）、`MarkdownTable`、
`AnalysisRunner.OpenOutput`、`plots-SKIPPED.md`。父调研 §0 那条「一行 C# 都不该动、只该改注释」
里还有**第三件**：`JsonReader` 的 `utf-8-sig` 容忍：

- `benchmarks/WinForward.E2E.Analysis/Loading/JsonReader.cs:31-32`（文档：BOM / 无效 UTF-8 替换 /
  通用换行）
- `:42` `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)`
- `:56` `detectEncodingFromByteOrderMarks: true`

这正是 `01-python-emulation.md:36-38` 点名的三件套之一，`01-python-emulation.md:592-602`（E16）
也确认「留代码，改文档」。C1 计划**从未提到它**，一个照 AC5 逐条 diff 的复核者会以为它不在名单里。

**改法**：`prd.md:35` 的括号里加「`JsonReader.cs:31-32,42,56` 的 `utf-8-sig`/无效字节替换/通用换行；
`PlotsNotice.Text`（`Cli/PlotsNotice.cs:20-35`）」；并加一条：「`RunLoader.ReferenceName`
（`Loading/RunLoader.cs:223-230`）与 `Model/JsonText.cs` 的 `None`/`True`/`False` 属 D6，
**本任务不表态、不动**」。（这两处 `01-python-emulation.md:485-494` 与 `design.md:69-71` 都要求
「保行为」，但 C1 的两份文件一个字都没写。）

### SHOULD-FIX 7 · S0 漏掉 E14（两处 pragma 的理由文案）

`design.md:26` 的 S0 行里有「两处 pragma 注释」，`01-python-emulation.md:694`（§4 S0 第 4 项）
也有 E14，但 `implement.md:15-27` 的 S0 五条里**没有它**。实测两处确实存在：

```
benchmarks/WinForward.E2E.Analysis/Model/RunClocks.cs:29-31
  #pragma warning disable S1244 // An exact zero is the reference's own test here.
benchmarks/WinForward.E2E.Analysis/Model/ArmAccess.cs:152-154
  #pragma warning disable S1244 // An exact zero is the reference's own test here.
```

（`Stats/CpuDetail.cs:171` 与 `Stats/OlsSlope.cs:73` 也有 S1244，但理由不同，不属于 E14。）

**改法**：`implement.md:23` 第 4 条改成「`research/01` §2 的 E13/E14/E15/E16/E17：
只改注释/文档叙事，代码不动（E14 是 `RunClocks.cs:29` 与 `ArmAccess.cs:152` 两处 S1244 的理由文案）」。
另外 `.editorconfig:241-248` 用精确文件路径 glob 了 `GateValidity.cs`/`GateFlow.cs`/`LedgerFindings.cs`
的 S1244 抑制——C1 删掉那些精确浮点比较后要复核是否还需要（`design.md:115` 已登记，属 C2，C1 不动）。

### NIT 1 · S1 不能是随便一条「显式格式串」，必须点名 `"0.000e+00"`

`implement.md:31-32` 说换成一个显式格式串保持 `8.000e-03` 字形。.NET 的**标准**指数说明符
（`"e3"`/`"E3"`）写的是**三位**指数（`8.000e-003`），会一次打红 27 格；能给出 `8.000e-03` 的是
**自定义**指数说明符 `"0.000e+00"`（`e` 定小写、`+` 定符号、两个 `0` 定最少两位指数），
见 Microsoft Learn《Custom numeric format strings》的 "E"/"e" 自定义说明符表
（`1503.92311 ("0.0##e+00") -> 1.504e+03`）。

**改法**：`implement.md:32` 改成「换成 `value.ToString("0.000e+00", CultureInfo.InvariantCulture)`；
**不要**用 `"e3"`/`"E3"`——标准指数说明符写三位指数，会打红那 27 格」。

### NIT 2 · S0.3 的字形变化方向写反了

`implement.md:21-22` 说「唯一字形变化：测到的零从 `0.0` 变 `0`」。实测
`Tables/TableLedger.cs:174-176` 现在是

```csharp
private static string DatagramCount(LedgerArmView arm) =>
    arm.UdpEndpoints.Count == 0 ? "0" : VerbatimNumber.Json(arm.UdpDatagrams);
```

空普查**今天**印的是字面量 `"0"`；删掉这个分支后它改走 `VerbatimNumber.Json(...)`，
而 `VerbatimNumber.Json(0.0)` 印的是 `"0.0"`（`AnalyzerNumberGoldenTests.cs:151` 钉了 `Json(1.0) == "1.0"`）。
所以方向是 `0` → `0.0`。`01-python-emulation.md:536-539` 也是同一个反向笔误。
结论不变（oracle 按数值判等：`oracle-diff.py:386-387` 的 `left == right` 在 `Decimal` 层面短路，
`"0"` 与 `"0.0"` 相等），但方向写反会让实施者以为该去动 `Json()` 的 `.0` 规则。

### NIT 3 · S2 的「约 140 个调用点」不是分散的 140 处，而是两个文件

实测 `rg -o 'VerbatimJson\.[A-Za-z]+' benchmarks/WinForward.E2E.Analysis tests/` 共 **179** 处，
其中生产代码正好 **140** 处、只落在**两个文件**：

```
Verdict/VerdictSections.cs   88
Metrics/MetricComparisons.cs 52
tests/…/AnalyzerJsonGoldenTests.cs 33（测试）
```

这对工作量是好消息（不是 140 处散点，是两整篇文件的重写），但也意味着 S2 实际是
**一次架构改动**：现在是「自底向上地把每个值渲染成 `string`，
`VerdictWriter.Write(TextWriter, IReadOnlyDictionary<string,string>)`
（`VerdictWriter.cs:42-59`）最后拼装」，`MetricComparisons.Render` 也返回 `string`
（`MetricComparisons.cs:44-47`）。换成流式 `Utf8JsonWriter` 必须把这两处从「返回字符串」
改成「写进 writer」。计划应该显式选择一条路，而不是只说「切调用点」：

- **最小改法**：保留 `string` 组合的架构，只把 `String()` 的转义与 `Number()` 的格式化换掉
  （不引入 `Utf8JsonWriter`），S2 的删减量从 120 行降到约 40 行但风险也降一个数量级。
- **全流式改法**：`VerdictSections.Build` 变成 `Write(Utf8JsonWriter)`；若想保留 `string` 组合，
  .NET 6+ 的 `Utf8JsonWriter.WriteRawValue(ReadOnlySpan<char>, skipInputValidation: true)`
  可以把已渲染的片段当原始 JSON 嵌入——**计划里没有提到这个桥**，而它是把 S2 拆成
  「先桥接、再逐片迁移」的关键。
  （另外：`AnalysisRunner.OpenOutput`（`AnalysisRunner.cs:75-79`）目前给的是 `StreamWriter`；
  `Utf8JsonWriter` 只吃 `Stream`/`IBufferWriter<byte>`，无 BOM + `\n` 的契约要么改成
  `FileStream` + 手动尾换行，要么先写 `MemoryStream` 再落盘。这一步也没写。）

### NIT 4 · S3 删半偶机器是「门禁看不见的行为变化」，值得在计划里写明

`implement.md:52` 说「BigInteger 半偶舍入机器可整段删（金标里半偶中点 0 命中）」。这句在
**`py-tables.md` 范围内为真**（`0.062`/`0.188`/`0.438`/`0.562`/`0.688`/`0.812`/`0.938`
出现 **0** 次；`0.312` 的 5 次是别的数），但在**向量表范围内为假**：
`py-number-vectors.json` 的 230 条 `fixed` 里有 **21 条**本身是所在格子的精确二进制中点
（`0.5@0→"0"`、`2.5@0→"2"`、`0.125@2→"0.12"`、`-2.675@2→"-2.67"` …）。
删掉半偶机器 = 放弃真实 campaign 上的 CPython 数值等价，而 oracle 只在冻结树上盖章。

**改法**：`implement.md:52` 追加一句「这是一处门禁看不见的行为变化：`py-tables.md` 0 命中，
但 `py-number-vectors.json` 有 21 条中点样本；若想保留等价，.NET 侧用
`Math.Round(value, digits, MidpointRounding.ToEven)` 显式声明，是零成本的。
本计划选择接受差异并在 `research/notes.md` 登记」。

### NIT 5 · S4 的「约 75」是在错误的参考侧测出来的，且 commit 会故意留红

`00-rng-swap-spike.md:23-24` 的 75 条是用 `--golden /tmp/spike-base-ref --cs-out /tmp/spike-rng`
（C# 基线 vs C# 换 RNG）比的，不是 vs 冻结的 Python 参考。两者在语义模式下应当一致
（基线 vs Python 本来就是 rc=0），但 `numbers_equivalent` 的容差单位取自**参考侧**的打印精度
（`oracle-diff.py:376-396`），所以严格来说「75」是另一个参考侧下的测量值。风险极低，但
`implement.md:63-64` 应该写成「与 spike 的 75 条**对齐**（spike 是在 C# 基线上测的，不是 vs 冻结参考）」。

另外这次 commit 是**故意红**的：`design.md:201-203` 已说明 S4 与 S5 必须分开，且 D7 决定的
CI step 不在本任务内（`implement.md:98-99`）。所以计划应在 S4 的 commit message 模板里写明
「本 commit 预期 oracle rc=1、75 条 `[value]`」。

---

## S4 判据的可测性（问题 2 的正面回答）

**可测**，但计划要收紧措辞。`oracle-diff.py` 确实给出实现者可以直接比较的数字：

- 汇总行：`oracle-diff.py:1257-1258`
  `compared N slice(s): D differ(ent), M missing` 与 `differences: {report.counts()}`，
  而 `Report.counts()`（`:767-771`）就是 `f"{structure} structure, {value} value, {missing} missing"`。
- 逐条明细：`:776-778` 每个 entry 打印一行 `   [{category}] {path}: expected X vs actual Y`。
- 因此「约 75 条 `[value]`、0 `[structure]`、0 missing」= 汇总行的 `75 value, 0 structure, 0 missing`，
  可以直接比对。

两个必须说清的边界：

1. **计数单位是「条目（entry）」不是「不同路径」**。`Report.entries` 是
   `(category, path, expected, actual)` 的列表（`:750`），一次 `report.value()` 追加一条
   （`:758-759`）。同一条路径在一次运行里只会被访问一次（切片覆盖互斥由 `:225-252` 保证），
   所以「75 条」= 「75 个位置」，重复计数不是现实风险。但 `[value]` 是**全批次**的总和，
   `tables.md` 的格子差异也算在里面——必须追加「且这 75 条的 path 全部以
   `verdict.json:metrics/` 开头」。这与 `00-rng-swap-spike.md:32` 的「表 `tables.md` 0 切片变化」
   是同一件事的两种说法。
2. **`0 missing` 有两个含义**：汇总里的 `M missing`（切片缺失，`:1257`）与
   `report.count(Report.MISSING)`（key 缺失，`:1259-1261`）。两者都必须为 0，计划应分别点名。

**替换判据（建议原文）**：

> S4 判据：在**未放宽**的 `oracle-diff.py` 上跑全批次（`--mode semantic`，默认），
> 要求 `differences:` 行匹配 `0 structure, ~75 value, 0 missing`，且
> `compared` 行的 `missing` 为 0；再要求所有 `[value]` 行的 path 满足
> `verdict.json:metrics/*.pairs[*].{p_value,p_equivalence,holm_p_equivalence}`，
> 且按字段计数约为 `p_value 58 / p_equivalence 10 / holm_p_equivalence 7`；
> `tables.md:*` 不得出现在任何差异行里。把 `oracle-diff.py` 的完整输出存进
> `research/notes.md`（`2>&1 | tee`），作为「先看见、再解释」的证据。

---

## S5 放松到底挂在哪里（问题 3 的正面回答）

**结论：挂得上，不是 BLOCKER。** 精确位置与签名如下。

- **入口**：`Comparer.compare_slice`（`oracle-diff.py:1005-1016`）。
  `metrics/<member>` **不在** `PINNED_BLOCKS` 里（`:197` = `frozenset({"bootstrap","thresholds"})`），
  所以 `:1010` 的 pinned 分支不拦它；`:1013-1015` 剥掉 metric 成员那一层包装后，
  `:1016` 调 `self.compare_json(f"verdict.json:{slice_name}", expected, actual)`。
  **`compare_members`（`:930-959`）与此无关**——它只从 `compare_pinned`（`:910,912,919`）调用，
  碰不到 `metrics`。
- **递归**：`compare_json`（`:961-1003`）对 dict 逐成员递归（`:994-1000`），数组按下标递归（`:986-992`）。
- **hook 的落点**：`:968-972` 的数字分支

  ```python
  if here == "number":
      assert isinstance(expected, VerdictNumber) and isinstance(actual, VerdictNumber)
      if not numbers_equivalent(expected.raw, actual.raw, self.tolerance):
          self.report.value(path, expected.raw, actual.raw)
      return
  ```

- **数字确实是 `VerdictNumber`**：语义模式下 `load_artifact` 走 `parse_document`（`:1108`），
  而 `parse_document`（`:677-679`）用 `parse_float=VerdictNumber, parse_int=VerdictNumber`；
  `VerdictNumber` 保留 `raw` 与 `value`（`:664-674`）。所以新规则可以用 `expected.raw` /
  `actual.raw`，也可以直接用 `.value`。
- **path 的真实形状**（由 `:1016` + `:991` + `:999` 拼出）：
  `verdict.json:metrics/<member>.pairs[<i>].p_value`、`….ci95[0]`。
  `<member>` 自身含点（`lat.tcp_rtt.p50`），**`pairs` 之前是点不是斜杠**。

**建议的签名与改法**（三处，都是小改）：

```python
# 模块级，紧挨 WORDING_KEYS（:204）之后：
STATISTICAL_FIELDS = frozenset({
    "p_value", "holm_p_value", "p_equivalence", "holm_p_equivalence",
})
STATISTICAL_RATIO = Decimal("0.01")   # count/10000 粒度 1e-4，绝对容差
STATISTICAL_RELATIVE = Decimal("0.01")  # ci95 是度量尺度上的区间，见 docstring
STATISTICAL_PATH = re.compile(
    r"verdict\.json:metrics/.+\.pairs\[\d+\]\.(p_value|holm_p_value|p_equivalence"
    r"|holm_p_equivalence|ci95\[[01]\])$"
)

def statistical_tolerance(path: str, expected: str, actual: str) -> bool | None:
    """None = not a statistical leaf; True/False = the declared rule's verdict."""
```

```python
# compare_json 的数字分支（:968-972）改成：
if here == "number":
    assert isinstance(expected, VerdictNumber) and isinstance(actual, VerdictNumber)
    verdict = statistical_tolerance(path, expected.raw, actual.raw)
    if verdict is not None:
        if not verdict:
            self.report.value(path, expected.raw, actual.raw)
        return
    if not numbers_equivalent(expected.raw, actual.raw, self.tolerance):
        self.report.value(path, expected.raw, actual.raw)
    return
```

要点：只放宽**数值叶子**，`pairs[i].verdict`/`raw_reason`/`holm_reason` 是字符串、走
`:973-981` 的 `text_identical`，天然不受影响（`design.md:58-59` 的「verdict 是字符串，仍逐字比对」
自动成立）；`estimate`/`median`/`iqr` 的 path 不匹配正则，照旧。**唯一要注意的是
`reason` 在 `WORDING_KEYS` 里**（`:204,999`），而 `raw_reason`/`holm_reason` **不在**——
这是既有行为，本次不要动。

---

## 我验证为真的断言

1. **`--batch` 语法**：`oracle-diff.py:1167-1168` 的 `action="append"` + `:1181` 的
   `args.batch[0].split(",")` ⇒ `--batch 2,4` 与 `--batch 1c,2,3,4,5` 都合法。
   `1` 会展开成 `1a,1b,1c`（`:184`, `:213-222`）。
2. **四个命令都能跑**：`oracle-diff.py --help` rc=0、`check-fairness.py --help` rc=0、
   `check-boundary-trees.py --help` rc=0、`effective-lines.py` 对四棵树 rc=0（无超限文件）。
3. **`1e-06` 与 `8.000e-03` 各 27 格**：`rg -c '1e-06'` = 27 行、`rg -o | wc -l` = 27 次；
   `rg -c '8\.000e-03'` = 27/27。计划里 S3 的 `rg -c` 判据（`implement.md:54`）成立。
4. **金标里 0 个半偶中点**：`0.062/0.188/0.438/0.562/0.688/0.812/0.938` 在 `py-tables.md`
   各 0 次；`0.312` 的 5 次不是中点样本（无 `0.3125`）。
   （但向量表里有 21 条中点样本，见 NIT 4。）
5. **`PythonExponential` 唯一调用者是 `Tables/GateValidity.cs:263`**，`rg` 全仓确认；
   文件 21 行。
6. **`VerbatimJson` 的调用面是 2 个生产文件**：`VerdictSections.cs` 88 处、
   `MetricComparisons.cs` 52 处（共 140），测试 33 处（见 NIT 3）。
7. **`Object(level, params (string,string)[])` 的第一个参数是缩进层级，不是成员数**
   （`VerbatimJson.cs:159`）。所谓「`Object(5, ...)` 里传成员数」在本仓库**不存在**；
   真正的形状依赖是**手工线程化的 `level`**（`VerdictSections.cs:69-118` 一路传 1/2/3/4），
   换成 `Utf8JsonWriter` 后这类参数整体消失——这是 S2 的收益，也是为什么要重构组合方式。
8. **`Utf8JsonWriter` 与现状的差异点**（逐条）：缩进字符——`json.dumps(indent=2)` 与
   `Utf8JsonWriter{Indented=true}` 都是两个空格 ✅；冒号后有空格——`json.dumps` 默认
   `key_separator=': '`，与 indented writer 一致 ✅（`VerbatimJson.cs:174` 也是 `": "`）；
   空容器——`json.dumps` 印 `{}`/`[]` 单行，`Utf8JsonWriter` 缩进模式下也印 `{}`/`[]`
   （`VerbatimJson.cs:135-138,163-166` 特意模仿）✅；插入序——`Utf8JsonWriter` 按调用顺序写 ✅；
   **转义——不等价 ❌**（见 BLOCKER 4）；**数字**——`VerbatimJson.Number` 委托给
   `VerbatimNumber.Json`（`VerbatimJson.cs:89`），即 Python `repr`（`:151` 的 `1.0`、`:154` 的 `1e+16`），
   .NET 的 `WriteNumberValue(double)` 走 shortest-roundtrip 且丢 `.0`、在 `1e16` 处行为不同 ❌。
   也就是说 S2 若同时换数字嵌入，会与 S3 的 `Json` 改动重叠——计划没写这个交集。
9. **`metrics` 切片走 `compare_json`（递归）、不走 `compare_members`（浅）**——见上一节。
10. **`StableHash`/`DeriveSeed` 的测试锚点只有一处**：`AnalyzerRandomGoldenTests.cs:93-102`。
11. **`RepoPaths.AnalyzerGolden` 的调用者只有三个将被删/改的测试文件**。
12. **`CpRandom.DeriveSeed` 的额外调用者两处 + 一处 cref**——见 SHOULD-FIX 2。
13. **keep 列表抽查三处为真**：
    - `Model/PosixPathText.cs:1-56` 是纯词法规范化（不解析 `..`、不绝对化），
      注释明说「spelling of the argument is part of the compared bytes」；被
      `CampaignLoader`/`RunLoader`/`LedgerLocator`/`PythonGlob` 使用。
    - `Stats/DescriptiveStats.cs:120-146` 的 `Sum` 是 Neumaier 补偿，注释明说 §7 斜率的末位依赖它。
    - `Tables/MarkdownTable.cs:1-31` 不校验单元格数、分隔行恰好 `|---|---|`，
      注释点名「§5 的 154 行 11 格」是刻意的。
    - 另两处（`AnalysisRunner.cs:71-79` 的 `OpenOutput`、`plots/SKIPPED.md`）由
      `oracle-diff.py:1019-1027`（`form_problems`，只查生产侧）与 `:1065-1072`（`check_plots`
      逐字节）直接钉住，确认是契约。
14. **子任务里没有任何门禁跑 `--mode byte`**：`rg` 全仓，`--byte`/`--mode byte` 只出现在
    `oracle-diff.py` 自己的 docstring/argparse 里；`.github/workflows/` 只有
    `analyzer-gate.yml` 与 `release-build.yml`，都不引用 oracle。⇒ S2 的「byte 会红、登记即可」
    在本仓库当前状态下成立。
15. **`Research/01` §5 的 18 条与 C1 的 S0–S6 不冲突**：逐条扫过，没有任何一步会动
    `VerdictWriter` 的 14 个顶层 key、`TablesWriter` 的 16 个标题、`ArmRecords` 的臂序、
    `MetricCatalogue` 的度量序、CLI flag 名与默认值。

---

## 我无法在不构建的情况下验证的断言

1. **`UnsafeRelaxedJsonEscaping` 对 `\u007f` 的精确输出**（`01-python-emulation.md:798-800`
   自己也列为待定）。我确认的是**金标要求它被转义**
   （`py-json-vectors.json` 的 `"the control characters with no short form"` 案例里是
   `\u007f`；`AnalyzerJsonGoldenTests.cs:61` 钉了 `"\\u007f"`），以及 relaxed 编码器**不**转义非 ASCII。
   `\u007f` 是否在 relaxed 下也逃逸，需要一条 `dotnet` 单测才能确定——而这正好是计划
   `implement.md:40-41` 要求做的那条单测。**结论不依赖它**：仅「非 ASCII」一项就足以否掉 relaxed。
2. **`System.Random(seed)` 在同一 .NET 版本内的稳定性**、以及换 RNG 后真实的 75 条差异，
   我没有构建、没有跑 analyzer，无法复现 spike 的 75。`00-rng-swap-spike.md:3` 声称代码已回滚、
   `git status benchmarks/` 为空——我确认了当前 `git status` 里 `benchmarks/` 无改动。
3. **`ci95` 在真实 campaign（pass 数 > 3）上到底跳多大**——`01-python-emulation.md:788-792`
   已经把它列为无权威来源的问题；我只能证明「绝对 `1e-2` 对 `loss.lossRate` 的 0.8 跨度而言
   只有 1.2%」，不能给出正确的容差值。建议按 SHOULD-FIX 5 用相对容差，并在真实 campaign 上复测。
4. **S3 换格式后表格数字的逐格等价性**：我验证了 27 格回退与 0 个中点，但没有跑
   `--batch 1c,2,3,4,5`，无法证明 .NET 的 `"F3"`/自定义格式串在**其余 1245 行**上与 CPython 逐格一致
   （《`.NET` 的 `F` 舍入不是半偶》`VerbatimNumber.cs:12-16` 是代码自己的说法，我采信但未复算）。
5. **`jb inspectcode` 与 `dotnet format` 的最终结果**——按规则未跑。

---

## 计划里我认为**不需要**改的部分（给父任务省一遍复核）

- **S4 在 S5 之前**：正确，理由（先看见 75 条、再让放宽解释）成立，`design.md:34-36` 与
  `00-rng-swap-spike.md:50-55` 一致。
- **S2 与 S3 各自可回滚**：在「改法二选一写清楚」的前提下成立；两者确实改同一个输出文件
  （`verdict.json`）且都会让 `--mode byte` 变红，但**互不依赖**（S2 动 `VerbatimJson`、
  S3 动 `VerbatimNumber`），各自 revert 不冲突。**前提是 BLOCKER 3 被修**——否则
  revert S3 之后 S2 仍然让测试项目编译不过，回滚点不干净。
- **S5 之后才删文件与向量表**：正确；`CpRandom.cs:19-20`、`VerbatimNumber.cs:21-24` 的文档注释
  都指向那三张表与 `make_cp_vectors.py`，删表与删注释必须同一步（`VerbatimNumber.cs` 不在删除
  清单里，所以它的 `:21-24` **必须改**——计划漏了这一条，并入 SHOULD-FIX 1 的改法）。
- **不搬 `oracle-diff.py`（留给 C3）**、**D7 的 CI step 不做**：边界清楚，正确。
- **S0 的 `Truthy`→`IsTrue` 语义**（在「这些键只可能是布尔」的契约下）：`JsonValue.cs:40-41`
  与 `:92-109` 的对比确认替换是等价的（缺失 → 两者都 false）。
- **S0.3 删整数零分支**：oracle 不会红，因为 `oracle-diff.py:386-387` 先做 `Decimal` 相等短路
  （`"0"` 与 `"0.0"` 相等），只有**不等**的数值才会走到 `:388-396` 的末位规则。
