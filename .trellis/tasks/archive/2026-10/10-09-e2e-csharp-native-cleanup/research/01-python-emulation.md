# 01 · Python 仿真的完整清单：每一处、谁依赖它、该留还是该删

**日期**：2026-10-09
**范围**：`benchmarks/WinForward.E2E/`、`benchmarks/WinForward.E2E.Contracts/`、
`benchmarks/WinForward.E2E.Analysis/`、`tests/WinForward.E2E.Tests/`
**方法**：纯静态阅读 + `rg` + `git log`；另有一段**不落盘**的 Python 数值实验（见附录 A），
用来独立复核 `research/00-rng-swap-spike.md` 的实测。

> 本文只回答一个问题：**哪些复杂度是为模仿 Python 而存在的，去掉它需要动什么。**
> 命名、脚本、文档三块各有专门报告（`02`–`04`），本文只在「改名是删除的必经步骤」时点到为止。

---

## 0. 判决（先读这一段）

**C# 侧约 900 行「为模仿 Python 而写」的代码里，约 500 行可以直接删掉（过半）；
连同为逐字一致而写的测试与三张向量表，总删减量约 6 400 行（见第 3 节）。
但"能删"的分布极不均匀：免费的那一半与需要一次登记的那一半，风险差了两个数量级。**

- **真正免费（删掉零风险）**：`PythonExponential`（21 行）、`JsonValue.Truthy`（Python 真值语义，
  18 行）、三个重复的 `Int(double)`「`%d`」小助手、`TableLedger.DatagramCount` 的 `sum([])`
  分支、`NaturalKey` 的「reference 的 `natural_key`」叙事。
  这些或者只影响一个单元格的整数拼写，或者在冻结树上**根本不触发**，删了没有任何门禁会红。
- **需要一次登记过的工作就能删**：`CpRandom`（249 行 MT19937）与 `VerbatimNumber` 的
  BigInteger 半偶舍入机器（342 行里的约 200 行）。用户已裁定换 `System.Random`；
  `00-rng-swap-spike.md` 实测只有 75 条 p 值差异、0 结构差异、0 verdict 翻转、表格 0 变化。
  数字格式化同理：**金标里没有任何一个半偶中点被打出来**（`0.062`/`0.188` 在
  `py-tables.md` 中出现 0 次），所以半偶机器可以整段删除；但 `%.3g` 回退
  （`1e-06` / `8.000e-03`，各 27 格）**必须保留某种等价物**，否则会撞上 oracle 的
  「参考打印过的分数不能丢」规则。
- **必须留、只能去掉叙事**：`PosixPathText` 的词法规范化、`PythonGlob` 的三条发现规则、
  `DescriptiveStats.Sum` 的 Neumaier 补偿、`RunLoader.ReferenceName` 的字符串、
  `JsonText` 的 `None`/`True`/`False`、`MarkdownTable` 的「不检查单元格数」。
  这些是**已发布契约**（`verdict.json.raw` 是逐字比对，§5 的 154 行 11 格是刻意的，
  §7 斜率的末位数字依赖补偿求和），删掉它们会改输出，而 oracle 会看见。
- **一行 C# 都不该动、只该改注释**：`AnalysisRunner.OpenOutput` 的 `\n` + 无 BOM、
  `PlotsNotice.Text`、`JsonReader` 的 `utf-8-sig` 容忍。它们是 `form_problems`
  （`oracle-diff.py:1019-1027`）逐字节检查的对象。

一句话：**RNG 与浮点/JSON 文本是"可以换实现"的那一类；路径、glob、求和、表格形状是
"实现即契约"的那一类。** 把这两类分开，是这个任务能不能一次做对的关键。

---

## 1. 阅读约定

| 标记 | 含义 |
|---|---|
| **删** | 文件/代码块整体消失，不需要替代物 |
| **换** | 换成列出的 .NET 构造；输出可能变化，变化已测量或可在一步内测出 |
| **留** | 行为是契约；只删"reference 的 XXX / CPython 的 XXX"叙事与名字 |
| `[结构]` | oracle 语义模式按 `[structure]` 报（含缺 key、类型、表格标题、单元格类别） |
| `[数值]` | oracle 语义模式按 `[value]` 报（数值按"参考末位 1 个单位"容差，`oracle-diff.py:1176`） |
| `[无门禁]` | 没有任何可执行门禁覆盖；只有人读得到 |

每个 emulation 一节的固定结构：**现状 / 调用者 / 依赖（测试·金标·文档·门禁）/ 判决 / 精确改法 / 风险**。

---

## 2. 逐个仿真项

### E1 · `CpRandom` — CPython 的 MT19937 全套

**现状**：`Analysis/Stats/CpRandom.cs`，249 行 / 9 834 B。复刻 `random.Random(int)` 的
`init_by_array`（`:184-224`）、`genrand_uint32`（`:80-96`）、`getrandbits`（`:105-129`）、
`_randbelow` 的拒绝采样（`:138-150`）；另有两个**不属于 RNG** 的成员：
`StableHash`（SHA-256 前 4 字节大端，`:60-67`）与 `DeriveSeed`
（`baseSeed + hash(text) % 1_000_000`，`:76`）。

**调用者**
- `Stats/BootstrapPair.cs:60` —— 唯一的构造点 `new CpRandom(seed)`；形参在 `:113`、`:160`，
  `Resample` 在 `:249-258`（`values[(int)random.RandBelow(values.Count)]`）。
- `Metrics/MetricComparisons.cs:225` —— `CpRandom.DeriveSeed(campaign.Seed, $"{spec.Key}|{pair.A}|{pair.B}")`。
- `Checks/ControlDrift.cs:197` —— `CpRandom.DeriveSeed(campaign.Seed, $"control|{metric.Key}")`。
- 种子来源：`Cli/AnalysisOptions.cs:20,50,160-168`（`--seed`，默认 `20261006`）→
  `Loading/CampaignLoader.cs:68` → `Model/CampaignModel.cs:47-48`。
- 发布形态：`Verdict/VerdictSections.cs:68`（`bootstrap.seed`），`AnalysisOptions.cs:9-12` 的注释。

**依赖**
1. **测试**（四条，全部是「断言 Python 精确值」）：
   - `tests/WinForward.E2E.Tests/AnalyzerRandomGoldenTests.cs:18-57`
     `EveryGoldenDrawIsReproducedInOrder` —— 直接消费 `cp-random-vectors.json`（18 seed / 702 draw）。
   - 同文件 `:59-74` `TheBoundariesOfTheRejectionLoopAgreeWithTheReference` —— 8 个硬编码 `RandBelow` 值。
   - 同文件 `:76-91` `ASeedsSignIsNotPartOfItAndZeroIsAKeyOfOneWord` —— MT19937 播种语义。
   - 同文件 `:93-102` `TheDerivedSeedIsTheReferenceFormula` —— `StableHash`/`DeriveSeed` 的三个常量。
2. **冻结物**：`verification/golden/cp-random-vectors.json`（3 121 行，hash 见 `FROZEN.md:51`），
   生成器 `verification/synthetic/make_cp_vectors.py`（352 行，hash 见 `FROZEN.md:54`）。
   `FROZEN.md:45-47` 明说这三张向量表**不参与双实现比对**；核对过 `oracle-diff.py` 全文，
   它只读 `py-tables.md`/`py-verdict.json`/`plots-SKIPPED.md`（`oracle-diff.py:110,127`）。
   → **对任何门禁都是惰性的**，唯一消费者是上面四条测试。
3. **文档承诺**：`Analysis/README.md:49`（「the per-pair seeds are derived from it deterministically」）、
   `:292`（「10000 resamples, seeded」）；`.trellis/spec/backend/measurement-tooling.md:108`
   （默认值表）、`:125`（向量表「not compared by the differ」）。
4. **输出契约**：`bootstrap.seed` 是整数且 `oracle-diff.py:923-930` 逐字比对 —— 但它就是
   `--seed` 的值，换 RNG 不动它。

**判决**：**换**（用户 2026-10-09 裁定：直接用标准库最简明的随机数，不复刻 MT19937）。
把 `StableHash` + `DeriveSeed` **摘出来留下**（它们是"同一 `--seed` 得到同一份比较种子"的
派生物，与 PRNG 无关，且 `DeriveSeed` 的值被 `AnalyzerRandomGoldenTests.cs:99-101` 钉住），
其余 200 余行整体删除。

**精确改法**
1. 新建 `Analysis/Stats/BootstrapSeed.cs`（或放进 `BootstrapPair.cs`）：
   `StableHash` / `DeriveSeed` 原样搬运，文档改成「比较的种子由 metric key 与两个行 id 派生，
   与 `--seed` 一起决定同一次分析里每个比较的随机流」。
2. `BootstrapPair.cs:60` → `var random = new System.Random(seed);`
   `:113`/`:160` 形参 → `Random`；`:249-258` → `values[random.Next(values.Count)]`
   （`Resample` 可以整段删掉，改成 `values.Select(_ => values[random.Next(values.Count)])`，
   但保留显式循环更省分配）。
   注意 spike 的插曲：留 `(int)` 会被 SonarAnalyzer `S1905` 拦下。
3. 删除 `Analysis/Stats/CpRandom.cs`、`tests/WinForward.E2E.Tests/AnalyzerRandomGoldenTests.cs`、
   `verification/golden/cp-random-vectors.json`。
4. `verification/synthetic/make_cp_vectors.py` 一次写三张表（`:326-340`），
   所以它的去留与 E2/E3 绑定，见第 4 节。
5. 文档改写：`Analysis/README.md:49` 改成「每个比较的种子由 metric key 与两行 id 派生」，
   `:292` 改成「同一运行时下可复现」；`measurement-tooling.md:108,125` 同步。

**风险**
- **已测量，可控**：`[数值]` 75 条，全部落在
  `metrics/*/pairs[*]/{p_value,holm_p_value,p_equivalence,holm_p_equivalence}`；
  `tables.md`、`estimate`、`median`、`iqr`、所有 verdict 字符串、所有 key 集**不动**
  （`research/00-rng-swap-spike.md` 的实测表；本文附录 A 用独立复算复核了同一结论）。
- **未测但已知方向**：`ci95` 在冻结树上不动，是因为每行只有 **3 个 pass**——3 个值的重采样中位数
  只有 3 个取值，2.5 % 分位点几乎必然是那个最小原子（P ≈ 0.259 ≫ 0.025），于是分位端点与
  随机流无关。附录 A 把 pass 数推到 5/6/8/10/15 复算：5–10 个 pass 时 24 次随机试验里 0 次移动，
  15 个 pass 时 6 次里 4 次移动。**真实 campaign 若跑很多 pass，`ci95` 会开始动**——
  spike 第 68 行的待确认项，这里给出了答案：建议**一并**把 `ci95` 纳入窄放宽，并在注释里写明
  「冻结树恰好没触发」。
- **不能放宽的东西**：`raw_verdict`/`holm_verdict`/`raw_reason`/`holm_reason`。p 值越靠近阈值，
  真 campaign 上越可能翻转；那是要看的信号，窄放宽不会掩盖它（verdict 是字符串，仍逐字比）。
- **`System.Random` 的序列不保证跨 .NET 版本稳定**（官方只承诺同一版本内；`Random.Shared`
  非确定性，在这里是错的）。诚实的三条路：(a) 接受「报告数字随运行时变」，README 改写为
  「同一运行时下可复现」；(b) 加一条廉价 tripwire（固定 seed 的 `Next(1000)` 前 N 项断言），
  版本升级时红给你看；(c) 若哪天"跨版本逐位可复现"重新变成需求，唯一的诚实做法是自带一个
  **有明确规格**的小 PRNG（xoshiro256\*\* / SplitMix64，约 20 行 + 自己的向量表），
  `System.Random` 从来不是那个选项。

---

### E2 · `VerbatimNumber` — `%.*f` / `%.*g` / `repr` 的文本

**现状**：`Analysis/Json/VerbatimNumber.cs`，342 行 / 13 530 B。
`Fixed`（`:38-56`，把 double 拆成精确有理数再半偶舍入）、`General`（`:66-100`）、
`Cell`（`:111-127`，即 `fmt_num`：定点 + 「非零却打印成零」时退到 `%.3g` + 空值 `n/a`）、
`Json`（`:136-161`，Python `repr`，含 `1.0` 保留小数点、指数区间 `1e-05`…`1e16`）。

**调用者**：**120 处引用，分布在 23 个文件**，按方法拆开是
`Cell` **91**、`Fixed` **26**、`Json` **2**、`General` **0**（只被 `Cell` 内部调用）。
最密集的是 `Tables/TableLedgerCross.cs`（19）、`Tables/GateFlow.cs`（11）、
`Tables/GateValidity.cs`（10）、`Findings/LedgerFindings.cs`（9）、
`Tables/TableFindings.cs`（7）、`Findings/FindingsCollector.cs`（7）；
`Json/VerbatimJson.cs:89` 也转调它的 `Json`。
`Fixed` 的 26 个直接调用点例：`Tables/TableDual.cs:137,142`、`Tables/TableHeadline.cs:65`、
`Tables/TableMemory.cs:196-197`、`Checks/ControlDrift.cs:275`。

**依赖**
1. **测试**（全部是「断言 Python 精确值」，7 条）：
   `AnalyzerNumberGoldenTests.cs:18-34`（`fixed` 230 值）、`:37-54`（`general` 135 值）、
   `:57-78`（`repr` 36 值）消费 `py-number-vectors.json`；
   `:85-95` `TheMidpointsOfTheGridRoundToTheEvenNeighbour`（8 个硬编码中点）；
   `:98-115` 中点的负控；`:117-128` `General` 的形态表；`:131-144` `Cell` 的形态表；
   `:147-160` `repr` 的两个 .NET 差异点。
2. **冻结物**：`verification/golden/py-number-vectors.json`（1 996 行，`FROZEN.md:52`）。
   同样不参与双实现比对（`FROZEN.md:45-47`），**对门禁惰性**。
3. **文档**：`measurement-tooling.md:125`；`python-oracle-changes.md:105-153` §5.5
   （`%.3g` 回退的两个渲染 `1e-06` 与 `8.000e-03`、27 个 `0.000` 格）、§5.6（无 BOM / `\n` / 单尾换行）。
4. **金标里到底触发了什么**（本次实测，`py-tables.md`）：
   - 半偶中点 `0\.062`/`0\.188`/`0\.312`/`0\.438`/`0\.562`/`0\.688`/`0\.812`/`0\.938`
     各出现 **0 次**（连 `0.0625`/`0.1875` 也没有）→ 半偶机器**没有被金标覆盖**。
   - `1e-06` 与 `8.000e-03` 各 **27 次**（27 行）→ `%.3g`/`%.3e` 的回退**被覆盖**。
   - `| 0.0 |` 248 格 vs `| 0 |` 433 格 → 定点零与整数零都在。

**判决**：**换 + 删**。去掉精确有理数/BigInteger 的舍入机器（金标不覆盖中点），
把文本生成交给 .NET；但**必须保留** `Cell` 的两条规则：`null → "n/a"`、
「非零不得被打印成零」（否则 §3.2 的 27 格会从 `1e-06`/`8.000e-03` 变成 `0.000`，撞上
oracle「参考打印过的分数不能丢」的规则，`oracle-diff.py:379-396`）。

**精确改法**
1. `Fixed(value, digits)` → `value.ToString("F" + digits, CultureInfo.InvariantCulture)`；
   负零保留符号要显式处理（`BitConverter.DoubleToUInt64Bits` 那两行 `IsNegative`/`IsZero` 可以留，
   它们与 Python 无关，是 IEEE 语义）。删掉 `Fraction`/`RoundHalfEven`/`Plain`
   （`:189-234`，约 46 行）。
2. `General(value, precision)` → 只在 `Cell` 的回退里用得到；用 `value.ToString("G" + precision)`
   会得到 `1E-06`（大写 E、三位指数），而 `oracle-diff.py:325` 的 `NUMBER_TOKEN` 接受 `[eE]`，
   `Decimal("1E-06") == Decimal("1e-06")` → **oracle 绿**，但人看到的字形变了。
   若要一字不动，保留一个 6 行的自定义指数格式（`"0.###e+00"` 一类），
   把 `General` 的 50 行缩到一个格式串。**推荐后者**：`tables.md` 是给人读的，
   字形是它的产物；用格式串既去掉了仿真叙事，又不动一个字符。
3. `Json(double)` → `value.ToString("R", CultureInfo.InvariantCulture)`。
   代价：整数值会丢 `.0`（`1.0` → `1`）。这在 `verdict.json` 里 oracle 完全看不见
   （JSON 数字解析后比较，`1.0 == 1`），在 `tables.md` 里也只有
   `Tables/TableLedger.cs:179` 一处（§14.2 的数据报计数）靠它区分「浮点零」与「`sum([])` 的整数零」——
   见 E12。**没有别的地方需要 `.0`**。
4. 删掉 `Repr`/`Exponential`/`Shortest`/`DecimalExponent`/`CompareWithPowerOfTen`/`ReadsAsZero`
   中不再被引用的部分；`Cell` 的 5 行核心逻辑保留。
5. 删 `AnalyzerNumberGoldenTests.cs` 里 7 条里除「形态」之外的全部，保留一条新写的
   **行为锚点**（例如 §5.5 的两个回退 + `n/a` + 负零），断言改成 .NET 自己该有的答案。

**风险**
- 若把 `Cell` 简化成裸 `F3`（不留回退），**oracle 立刻红 27 格**：这是这一步唯一的硬门槛。
- 半偶中点：金标不覆盖 → oracle 看不见；但真实 campaign 里一个恰好落在网格中点上的值
  会印出与 Python 不同的末位。**语义模式容差恰好是 1 个末位单位，所以 oracle 依然绿**，
  但这属于「用户已裁定可接受的形态差异」。
- `tables.md` 的数字宽度会有一处变化（`G3` 形态），若采用第 2 步的推荐则没有。

---

### E3 · `VerbatimJson` — `json.dumps(..., indent=2, sort_keys=False)`

**现状**：`Analysis/Json/VerbatimJson.cs`，199 行 / 8 546 B。手写的 JSON 文本生成器：
转义表（`:39-78`）、缩进（`:183`）、容器（`:111-180`）、非 ASCII 一律 `\uXXXX`（`:189-198`）。

**调用者**：144 处引用，其中 `Verdict/VerdictSections.cs` **88** 处、
`Metrics/MetricComparisons.cs` **52** 处；这就是 `verdict.json` 的全部文本。

**依赖**
1. **测试**：`AnalyzerJsonGoldenTests.cs:16-30`（金标 12 个文档）、`:33-41`（插入序 + 空容器）、
   `:44-49`（六个「不该转义」字符）、`:52-63`（非 ASCII 与代理对）、`:66-71`（短控制转义）、
   `:74-85`（嵌套层级）、`:88-97`（标量）。全部是「断言 Python 精确字节」。
2. **冻结物**：`verification/golden/py-json-vectors.json`（119 行，`FROZEN.md:53`），门禁惰性。
3. **文档**：`python-oracle-changes.md:105-153` §5.7（两空格缩进、`": "`、`sort_keys=False`、
   `ensure_ascii=True` 的 `\uXXXX`、**转义按文件而不是按仓库**）。
4. **门禁实际检查什么**：`oracle-diff.py:1019-1027` 的 `form_problems` 只查
   **BOM / `\r` / 恰好一个尾换行**；JSON 是解析后比较的，文档头明说「object key order ignored」、
   「strings compared by their decoded value」（`oracle-diff.py:44-56`），
   数字解析成 `Decimal`（`:386-396`）。

**判决**：**换 + 删**。`verdict.json` 的字节形态**不是**契约，只有它的解析后语义是。
`--mode byte` 会红是预期内的——它自己就被文档定性为「structure-surface regression mode,
**not a batch criterion**」（`oracle-diff.py:61-66`），这次正好是它第一次真正需要登记例外。

**精确改法**
1. 用 `System.Text.Json.Utf8JsonWriter` 重写：`Indented = true`、
   `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，
   保留「按给定顺序书写成员」的 API 形状（`Object(level, params (Key, Value)[])` 可以整段换成
   `Utf8JsonWriter` 上的链式调用）。缩进不用自己写（STJ 的缩进就是两空格、`": "`，
   这一点项目自己已经在 `VerbatimJson.cs:14-15` 记过）。
2. `String(value)` → `writer.WriteStringValue(value)` 或
   `JsonEncodedText.Encode(value, UnsafeRelaxedJsonEscaping)`。
   删掉 `AppendCharacter` 与短转义表（约 30 行）。
3. `VerdictSections.cs`/`MetricComparisons.cs` 的 140 个调用点若不想一次改 140 行，
   可以保留现有「返回 JSON 文本片段」的签名，内部改用
   `JsonSerializer.Serialize`/`JsonEncodedText`；但**至少要删掉手写转义与手写缩进**，
   那是"仿真"的实体。
4. 加一条测试替代 `AnalyzerJsonGoldenTests`：断言**解析后**的等价性
   （`JsonNode.DeepEquals` 或对 `verdict.json` 做 round-trip），而不是字节。

**风险**
- `–`、`—`、中文会从 `\u2013` 变成原字符 → 文件仍是无 BOM UTF-8，oracle 解析后相等，
  **绿**；但 `--mode byte` 红。登记即可。
- STJ 默认转义器会转义 `'`/`+`/`<`/`>`/`&`/`/`（解码后相等 → 绿），
  `UnsafeRelaxedJsonEscaping` 不转义它们但可能不转义 `\u007f` 一类边缘码位 ——
  **这一条我没能从代码确认，需要一条单测**（原测试 `AnalyzerJsonGoldenTests.cs:61`
  恰好钉了 `\u007f`）。
- 顶层的 key 顺序：语义模式忽略，但 `VerdictWriter.cs:12`（「Order is output」）与人类读者在乎。
  换实现时**保住插入序**，成本为零。

---

### E4 · `PythonExponential` — 一个单元格的 `%.3e`

**现状**：`Analysis/Json/PythonExponential.cs`，21 行 / 920 B。
**唯一调用者**：`Tables/GateValidity.cs:263` —— BASE floor 单元格
`$"{PythonExponential.Fixed(value.Value, 3)} < 1e-06"`。

**依赖**
1. **测试**：无直接测试。
2. **冻结物**：`py-tables.md:394-420` 共 **27 行**含 `8.000e-03 < 1e-06` → **被 oracle 覆盖**。
3. **文档**：`python-oracle-changes.md:105-153` §5.5 把它与 `%.3g` 回退并列（其实它是独立的一处）。

**判决**：**换**（一行），然后**删**文件。

**精确改法**：`value.ToString("0.000e+00", CultureInfo.InvariantCulture)` 恰好产出 `8.000e-03`
（Python 的 `%.3e` 也是两位指数下限）。若用 `"E3"` 则得 `8.000E-003`：
`oracle-diff.py:325` 的 `NUMBER_TOKEN` 接受，`Decimal` 相等 → **仍然绿**，但字形变了。
**推荐自定义格式串**，理由是 §3.2 是给人读的表。

**风险**：几乎为零。唯一要检查的是 .NET 自定义 `e` 格式的舍入与 Python `%.3e` 在半偶上的差异——
但 oracle 对数字按容差比，金标里这一格也不在中点上。

---

### E5 · `PythonGlob` — `Path.glob` 的三条规则

**现状**：`Analysis/Loading/PythonGlob.cs`，66 行 / 2 992 B。
`ArmFiles`（`*.jsonl`，`:31-32`）、`ConfigFiles`（`config*` + `File.Exists`，`:35-36`）、
`LedgerFiles`（`*ledger*.jsonl` + `File.Exists`，`:39-41`），
展开在 `:43-65`：`Directory.EnumerateFileSystemEntries` + 谓词 + `PosixPathText.Join` + 序数排序。

**调用者**：`Loading/RunLoader.cs:86`（config）、`:254`（arms）；
`Loading/CampaignLoader.cs:253`（判空 pass）；`Loading/LedgerLocator.cs:55`（ledger）；
内部 `:59` 调 `PosixPathText.Join`。

**依赖**
1. **测试**：没有针对 `PythonGlob` 的单测（`rg` 全库无引用）。
2. **冻结物**：`synthetic-tree.tar.gz`（`FROZEN.md:18`）是唯一输入。
   核对过 `make_tree.py`：它只 `mkdir` 出 `pass<N>/`、`<row>/`、`dual/`、`proxied|direct/`
   （`:1063,1074,1087,1250,1265`），写的文件都是 `<ARM>.jsonl` 与 `run.json` 一类
   （`:1169,1182,1188,1297,1523`）——**没有点开头的名字，也没有名为 `*.jsonl` 的目录**。
   所以三条规则里「隐藏名也匹配」「目录也算臂」**都没有被冻结树覆盖**。
3. **文档/裁定**：`design-decisions.md:899`（D21 §5：加载语义——哪些文件、哪些记录、
   行/臂身份、账本发现——**仍按参考实现**）；`Analysis/README.md:45`、`:80-81` 公开了
   账本发现的顺序与四个固定名字。

**判决**：**留行为，换实现形状，改名字**。
`Directory.EnumerateFiles(dir, "*.jsonl")` **不能**作为替代：
(a) 它排除目录，而 `PythonGlob` 的 `*.jsonl` 刻意把名为 `sub.jsonl` 的**目录**当臂
（`:16-20` 的 Remarks 就是这个意思，且 `:31-32` 没有 `.Where(File.Exists)`）；
(b) 它的返回顺序是文件系统顺序，不是序数序（`:63` 显式排序）；
(c) 它对点开头名字的行为在 Unix 上依赖平台匹配器，不是"契约"。
而 `EnumerateFileSystemEntries` + 谓词**已经是**普通 .NET 写法——这里要拆的只是名字与叙事。

**精确改法**
1. 改名 `PythonGlob` → `RunFileDiscovery`（或 `ArmFileDiscovery`），
   去掉 `:5-21` 里「with the reference's own glob rules」「which is what the reference calls」
   两段，换成「臂/配置/账本三类文件的发现规则：点是普通字符、模式匹配目录项、
   按序数序返回」——把三条规则**写成规则**而不是写成对 Python 的注释。
2. 三个 `Func<string, bool>` 换成三个 `private static bool IsX(string name)`，
   去掉委托分配（顺带过 allocation 审计）。
3. 三个方法名 `ArmFiles`/`ConfigFiles`/`LedgerFiles` 保留（它们是行为名）。
4. 若将来要把「目录也算臂」降级为「只算文件」，那**是语义变更**，需要先改 `--zero-denominator`
   边界树与文档，不在本任务范围。

**风险**：低。唯一的坑是有人"顺手"换成 `EnumerateFiles` + pattern，那会静默改掉
「目录算不算臂」与「顺序」，而**没有任何门禁会发现**（冻结树没有这两类形状）。

---

### E6 · `PosixPathText` — 路径的词法拼写

**现状**：`Analysis/Model/PosixPathText.cs`，95 行 / 3 733 B。
`Normalize`（`:23-53`：折叠重复分隔符、丢 `.` 分量、去尾分隔符、**从不解析 `..`**、保留 `//` 根）、
`Parent`（`:60-75`：词法上取父目录）、`Join`（`:82-94`：`Path / name` 的拼写，`.` 目录不加前缀）。

**调用者**：22 处。`Loading/RunLoader.cs:65,106,125,152,180,267,273`；
`Loading/CampaignLoader.cs:60,166,195,196,253`；`Loading/LedgerLocator.cs:43,52`；
`Loading/PythonGlob.cs:59`。

**依赖**
1. **测试**：无单测。
2. **冻结物 / 输出契约**：`verdict.json` 的 `raw`（金标 `py-verdict.json:3` =
   `/tmp/wf-synth/raw`），以及 §2 的 `Path` 列、`ledger.passes[*].paths`
   （金标 `py-verdict.json:673-674` 等）。`oracle-diff.py:973-977` 对非 `WORDING_KEYS`
   （`:204` = `detail`/`status_reason`/`notes`/`reason`）的字符串走 **`text_identical`**（`:445`）——
   `raw` **是逐字比对**。
3. **文档**：`python-oracle-changes.md:105-153` §5.8（§2 与 §14 打印绝对路径，
   oracle 两侧都用硬编码的 `/tmp/wf-synth/raw`）；`Analysis/README.md:43`（默认 `--raw ../raw`）、
   `:34` 的示例用了 `--raw ./raw`。

**判决**：**留行为，改名字与叙事**。`PosixPathText` 看起来像 Python 的 `pathlib` 仿真，
但它承担的其实是**一条发布契约**：「`--raw` 的拼写按调用者给的词法形式发布」。
`Path.GetFullPath` 会解析 `..` 并转绝对路径，`Path.GetRelativePath` 语义完全不同，
`Path.Combine` 不做任何规范化 —— 三者都不能替代。

**精确改法**
1. 改名 `PosixPathText` → `PathText`（或 `ReportPath`），去掉 `:4-16` 里
   「the reference turns `--raw` into a `Path` and publishes `str(path)`」的叙事，
   换成「分析器按调用者给的拼写发布路径：词法规范化，不解析 `..`，不转绝对路径；
   这个值会进 `verdict.json.raw` 与 §2，是**输出**」。
2. `Parent`/`Join` 的方法名与语义保留（`Join` 的「`.` 目录不加前缀」是 §2 能读到
   `ledger-main.jsonl` 而不是 `./ledger-main.jsonl` 的原因）。
3. 顺手把 `:13-15`「Only POSIX separators are handled」改成明确的范围声明
   （分析器只在 POSIX 路径上被调用；Windows 上跑 campaign 时 `--raw` 也应给正斜杠）。
   **这一条需要确认**：仓库有 Windows E2E 机（`benchmarks/WinForward.E2E/AGENTS.local.md`），
   但分析器是否在 Windows 上跑过，我没找到证据（见第 6 节开放问题）。

**风险**：低，但**不可见**。若有人把它换成 `Path.GetFullPath`：
- oracle 仍绿（它两侧都用 `/tmp/wf-synth/raw`，规范化前后同形）；
- 但 `analyze.sh --raw ../raw`（README 的默认调用）会在 `verdict.json.raw` 里印出绝对路径，
  文档与 `check-fairness.py` 的读者都会看到不同的东西。**没有任何门禁会红**。

---

### E7 · `DescriptiveStats.Sum` — CPython 的 Neumaier 补偿求和

**现状**：`Stats/DescriptiveStats.cs:118-146`，原文注释是「the way CPython's `sum` computes it」。

**调用者**：`Stats/OlsSlope.cs:60,61,71,72,88`（§7 最小二乘斜率）、
`Stats/CpuDetail.cs:170`、`Tables/TableLedgerCross.cs:207`。

**依赖**
1. **测试**：`AnalyzerStatsAnchorTests.cs:16-32` `TheCompensatedSumKeepsTheMiddleTerm`
   —— 断言 `sum([1e16, 1, -1e16]) == 1.0` 并给出朴素累加的负控。
   **这是"数值正确性"断言，不是"Python 精确值"断言**：换了实现它照样成立。
2. **文档**：`measurement-tooling.md:141-143`（「Records whose numbers are summed for the report
   follow CPython's compensated summation when the reference does」）—— 这句话把
   「CPython 这么做」当成了理由，而真正的理由是"精度"。

**判决**：**留算法，删叙事**。补偿求和的收益与 Python 无关：§7 的斜率是大数之差，
朴素累加会把中间项丢掉（这正是 `AnalyzerStatsAnchorTests` 的负控所证明的）。
.NET 没有等价的 BCL API，所以"换成普通写法"在这里等于**降低正确性**。

**精确改法**
1. `:118-127` 的文档改写：「补偿（Neumaier）求和。§7 的最小二乘斜率是大数之差，
   `total += value` 会丢掉中间项；这里保留补偿项。」
2. `measurement-tooling.md:141-143` 改成同一句理由，不再提 CPython。
3. 可选：改名 `Sum` → `CompensatedSum`（调用点 5 个文件 8 处）。
4. `AnalyzerStatsAnchorTests` 保留（它现在是**算法的不变量**，不再是 Python 的锚）。

**风险**：若有人图省事换成 `values.Sum()`（LINQ），§7 的斜率末位会变；
oracle 的数值容差是"参考末位 1 个单位"，**很可能红**（斜率印到 2–4 位小数，朴素累加的偏差
远大于 1e-4）。这是一处"看起来像仿真、实际是正确性"的地方。

---

### E8 · `RunLoader.ReferenceName` — 「CPython 会打印的异常名」

**现状**：`Loading/RunLoader.cs:223-230`，把 .NET 异常映射成
`JSONDecodeError` / `PermissionError` / `FileNotFoundError` / `OSError`，
在 `:216` 拼成 §15 的 `"{label} unreadable ({name})"`。

**依赖**
1. **测试**：无。
2. **金标覆盖**：`rg 'JSONDecodeError|PermissionError|FileNotFoundError|OSError' py-tables.md`
   → **0 次**；`rg 'unreadable'` 命中的唯一一行（`py-tables.md:97`）是另一条 finding
   （`1 sample(s) carried readError with 1 unreadable process counter(s)`），与此无关。
   → **这个映射在冻结树上完全不触发**，`[无门禁]`。
3. **文档**：`RunLoader.cs:201-204` 的 Remarks 说这些字符串是 §15 比对字节的一部分——
   对**真实** campaign 成立，对冻结树不成立。

**判决**：**留字符串，改名字与理由**。这是本次调研里"最接近可删"的一处：
没有任何门禁覆盖它，但它确实是 §15 会印给人看的文本。
把它当"命名决定"交给 `02-naming` 更合适，理由是：
`.NET` 侧的名字（`JsonException` 等）同样可读，而 `JSONDecodeError` 只为对齐一个已经退休的参考。

**精确改法**（二选一，**必须显式选**，不能默默换）
- (a) 保守：方法改名 `FailureKind`，文档改成「§15 的单元格文本；这些拼写已发布，
  与参考一致」；`JsonException => "JSONDecodeError"` 等映射原样保留。
- (b) 去仿真：改成 `error.GetType().Name` 或手写一张 .NET 名字表
  （`JsonException` / `UnauthorizedAccessException` / `FileNotFoundException` / `IOException`）。
  **输出会变**（真实 campaign 的 §15 格子），但**没有门禁会红**，要在 PRD/README 里登记为
  「发布文本的一次性变化」。

---

### E9 · `JsonValue.Truthy` — Python 的真值语义

**现状**：`Model/JsonValue.cs:43-44, 92-109`。对 JSON 值做 Python 的 `not value`：
`false`/`null`/`0`/空串/空数组/空对象/缺失 都是假。文档 `:14-16` 说明它复刻了
`not sample.get("readError")` 与 `sample.get("self") is True` 的区别。

**调用者**（5 处）：`Model/RunSamples.cs:86`（`readError`）、`:269`（`countersRead`）；
`Tables/GateFlow.cs:233`（`failed`）；`Tables/TableEnvironment.cs:222`（`absent`）、`:317`（`failed`）。

**事实：这些键只可能是布尔**
- 写侧：`Client/ResourceSampleWriter.cs:80`（`countersRead` 用 `WriteBoolean`）、
  `:100`（`readError` 只在为真时写）、`Client/ResourceSampler.cs:164`（`absent` 写真）、
  `Client/RunFileWriter.cs:52,57`（`failed` 用 `WriteBoolean`）。
- 冻结树：`verification/synthetic/make_tree.py:326,347,366,1172,1300,1380,1451`
  写的也全是 `True`/`False`。
- 契约：`.trellis/spec/backend/measurement-record-contract.md` 的三态表（`:62`）。

**判决**：**换 → 删**。用已经存在的 `JsonValue.IsTrue`（`JsonValue.cs:40-41`）
替换 5 个调用点，删掉 `Truthy` + `IsTruthy`（18 行）与 `:14-16` 的整段文档。

**精确改法**：`RunSamples.cs:86` → `!JsonValue.IsTrue(sample, ArmKeys.Sample.ReadError)`
（语义：缺失 → `IsTrue` 为 false → `IsReadable` 为 true，与 `Truthy` 相同）；
其余 4 处同理。改完 `JsonValue.cs` 只剩「读成员 / 三态 / 路径」三件事，是一个干净的读模型。

**风险**：契约外的输入（有人写 `"readError": 1`）会从真变假。契约说它是 boolean，
写者是本仓库自己的 writer。**低**。若想留一道保险，可以在 `ContractShapeTests` 里加一条
「这些键必须是 JSON boolean」的断言，成本 3 行。

---

### E10 · `JsonText` — Python 的 `str()` 拼写

**现状**：`Model/JsonText.cs`，52 行 / 1 811 B。`Of` 把 JSON 值渲染成 Python `str()`：
`True`/`False`/`None`/数字保留原文本（`:43-51`）；`Same` 用 `GetRawText()` 比较。

**调用者**：**25 次调用、23 行，分布在 10 个文件**
（`rg -n 'JsonText\.' … -g '*.cs'`，不含定义文件）：
`Findings/LedgerViews.cs:343,350,494,495`、
`Findings/DualFindings.cs:305,307`、`Findings/FindingsCollector.cs:294`、
`Findings/FindingsCollector.Structure.cs:102,108,117`、`Findings/LedgerDnsTotals.cs:78,110,128`、
`Findings/LedgerTruncationTotals.cs:120`、`Checks/IdentityChecks.cs:193,247`、
`Tables/TableLedgerCross.cs:276`、`Tables/TableEnvironment.cs:110,246,341,351,352`、
`Tables/TableFindings.cs:193`。

**依赖**：`rg -o '\bNone\b' py-tables.md` → **0 次**；`True`/`False` 也是 0
（`py-verdict.json` 同样 0）。
→ 这套拼写在冻结树上**完全不触发**，`[无门禁]`。它只在真实 campaign 的 finding 文本里出现
（例如某个 `process` 成员是 JSON null → `None`）。

**判决**：**留行为，改叙事**（或改名）。`None`/`True` 是发布文本；
它现在没被门禁钉住，所以"改不改"是产品决定，不是 oracle 决定。

**精确改法**：`:6-10` 的文档改成「JSON 标量在报告文本里的拼写：空值是 `None`，
布尔是 `True`/`False`，数字保留它被写下来的形式」；把类名改成 `JsonScalarText` 之类。
`Same` 的语义（`"1"` 与 `1` 不同）在 `Model/RunSamples.cs:298` 有独立的理由说明，
不受影响。

**风险**：若顺手改成 `null`/`true`/`false`，变化只在真实 campaign 的文本里，
门禁全绿——**要做成显式决定**。

---

### E11 · `Int(double)` — 「Python 的 `%d`」，三份拷贝

**现状**：同一段代码抄了三遍，注释都是「Python's `%d` on a double: the integer part,
toward zero」：
`Tables/GateFlow.cs:390-391`、`Checks/IdentityChecks.cs:343-345`、
`Findings/FindingsCollector.cs:333-334`。

**调用者**：`GateFlow.cs:90,93,186`；`IdentityChecks.cs:276-278,301-303,327,340`；
`FindingsCollector.cs:104`（两处）。

**依赖**：无测试；输出的数字出现在 `py-tables.md` 的多个 finding 文本里（例：
`arrived(6001) + late(0) + … = 6001 != sent(6000)`），oracle 按文本比对。

**判决**：**换 + 合一**。实现（`(long)Math.Truncate`) **本来就是**普通 .NET；
要删的是注释里的 `%d` 叙事，以及两份重复。

**精确改法**：在 `Model/JsonNumber.cs`（或 `JsonValue`）加
`internal static string IntText(double value) => ((long)Math.Truncate(value)).ToString(CultureInfo.InvariantCulture);`
三个调用点改引用，删三个私有副本。
（注意：`(long)` 对超出 `long` 范围的 double 是未定义行为，Python 的 `%d` 不会——
但这里的值是计数器，不构成现实风险；若要严谨可加 `checked` 或范围判断，成本一行。）

**风险**：零（输出逐字不变）。

---

### E12 · `TableLedger.DatagramCount` — `sum([])` 是整数零

**现状**：`Tables/TableLedger.cs:172-179`：`UdpEndpoints.Count == 0 ? "0" : VerbatimNumber.Json(...)`，
文档 `:175-177` 明说「空普查是 python 的 `sum([])`，是整数零，而任何点名了来源的普查是浮点，
会打印小数点」。同文件 `:232` 还有 `NonZero` 的注释「which is python's own truthiness」。

**依赖**：无测试；`py-tables.md` 里 ` 0 |` 433 格、`0.0 |` 248 格，但**哪一格来自这条分支
没有单独的可辨识特征**——两者在 oracle 里都是数字（解析后相等）。

**判决**：**换**。这条分支存在的唯一理由是复刻"Python 的整数零与浮点零字形不同"，
而这个区别对读者没有信息量（空普查这件事已经在 `Endpoints` 列的 `—` 里说了）。

**精确改法**：`DatagramCount` 改成一行 `VerbatimNumber.Json(arm.UdpDatagrams)`
（或换实现后的 `ToString("R")`），删掉 `:175-177` 的文档与整数零分支；
`:232` 的注释改成「读数存在且不等于零」。
改完后 §14.2 里一个"测到的 0"会从 `0.0` 印成 `0`——**oracle 仍绿**（数字等价）。

**风险**：零门禁风险；唯一的"损失"是有人习惯了 `0.0` 与 `0` 的区分——而这个区分
在 `tables.md` 里从来没有被解释过。

---

### E13 · `NaturalKey` — 「reference 的 `natural_key`」

**现状**：`Model/NaturalKey.cs`，91 行 / 3 248 B。数字段按数值比较、其余按字符比较。
**调用者**：`Model/CampaignModel.cs:54,102`、`Metrics/MetricCell.cs:117`、
`Metrics/MetricComparisons.cs:181,193`、`Verdict/VerdictSections.cs:203`、
`Checks/ControlDrift.cs:247`、`Loading/CampaignLoader.cs:267`。

**判决**：**留，只改文档**。`NaturalKey` 排序**是**输出顺序
（`verdict.json` 的 `passes` 数组、每张表的行序），而它已经是 30 行普通 C#，
没有任何"仿真机器"。`:4-13` 的「The reference's `natural_key`」与 `:55` 的
「the reference's `sorted(..., key=natural_key)`」换成
「`pass2` 排在 `pass10` 之前；这是 `verdict.json.passes` 与所有表格的行序」。

**风险**：零。

---

### E14 · 两处「Python 真值」的精确零判断

**现状**：`Model/RunClocks.cs:27-31` 与 `Model/ArmAccess.cs:152-154`，
都带 `#pragma warning disable S1244 // An exact zero is the reference's own test here.`
（`== 0.0` 而不是 `<= 0.0` 或 `Math.Abs(x) < eps`）。

**判决**：**留判断，改 pragma 的理由**。`RunClocks` 拒的是"墙上时间恰好为零"，
`ArmAccess` 拒的是"分母恰好为零"——两者都是**真实语义**（零分母没有倒数），
不是仿真。pragma 的理由改成「精确零：一个近零的墙上时间仍能给出频率估计，
一个近零的分母仍能给出比率」。

**风险**：若改成 `<= 0.0`，行为只在"负的墙上时间/分母"这种垃圾输入上不同；
冻结树不受影响，真实 campaign 也是改进了。**零风险**，但要在 `02-naming` 里统一口径。

---

### E15 · `MarkdownTable` — 「reference 的 `md_table`」

**现状**：`Tables/MarkdownTable.cs`，36 行。**判决**：**留，改文档**。
两条规则是刻意的、且被 `python-oracle-changes.md:105-153` §5.1/§5.2 明文冻结：
不检查单元格数（§5 有 154 行 11 格对 10 列表头）、分隔行恰好是 `|---|---|`。
`:4-14` 的「the way the reference's `md_table` does」改成
「表格的渲染规则：单空格填充、不对齐、不截断、**不校验单元格数**（§5 的 154 行是刻意的）」，
把"为什么"从"因为参考这样"变成"因为金标这样，而金标是契约"。

**风险**：零。

---

### E16 · `JsonReader` 的 CPython 保真度叙事

**现状**：`Loading/JsonReader.cs:26-32` 承认了三件事：拒绝尾随内容（`json.loads` 的
"Extra data"）、容忍 BOM（`utf-8-sig`）、把无效 UTF-8 换成替换字符。

**判决**：**留代码，改文档**。这三条都是"输入是别人写的文件"的合理防御，
与 Python 无关。`:26-32` 改成同一条理由（campaign 在负载下写文件，可能截断）。
**注意**：`:78-81` 的 `ReadFile` 把"缺失 / 不可读 / 不是 JSON"三态压成 `null` + `error`，
下游（E8）才决定打印什么——这个形状保留。

**风险**：零。

---

### E17 · 分析器 CLI 的自我叙述

**现状**：`Program.cs:36-45` 的 `--help` 文本写着
「as a compiled replacement for the reference implementation it is diffed against」与
「The parameters and their defaults are the reference implementation's own」；
`Cli/AnalysisOptions.cs:3-13` 的类文档同理；
`Cli/AnalysisRunner.cs:71-79` 的 `OpenOutput` 注释引 D20.5。

**依赖**：**没有任何测试快照分析器的 CLI** —— `CliSnapshotTests` 只重放
`WinForward.E2E`（harness）的 31 条命令（类文档 `CliSnapshotTests.cs:6-30`，
两棵树由 `RepoPaths.CliSnapshotsDirectory` 定位：`RepoPaths.cs:43-61` 的
`before/` + `after/`，`:48-49` 取用；`CliSnapshotTests.cs:48-49`）。

**判决**：**留行为，改文案**。注意 `OpenOutput` 的 `NewLine = "\n"` 与无 BOM
**是门禁检查对象**（`oracle-diff.py:1019-1027` 的 `form_problems`），只改注释。

**风险**：零。

---

### E18 · 冻结物与 `__pycache__`：谁还在被比对

| 冻结物 | 谁读它 | 判决 |
|---|---|---|
| `golden/py-tables.md`（1245 行，`FROZEN.md:19`） | `oracle-diff.py:127`（`GOLDEN_NAMES`）——oracle 的参考侧 | **不可动** |
| `golden/py-verdict.json`（14125 行，`FROZEN.md:20`） | 同上 | **不可动** |
| `plots-SKIPPED.md`（`FROZEN.md:21`） | `oracle-diff.py:110,1066-1079`，**逐字节** | **不可动**（`Cli/PlotsNotice.cs:20-35` 是同一份文本，改任一处就红） |
| `synthetic-tree.tar.gz`（`FROZEN.md:18`） | `oracle-diff.py` 的 `extract_tree()` 与 `ARCHIVE` | **不可动** |
| `synthetic/make_tree.py`（`FROZEN.md:22`） | 冻结树的生成器；`FROZEN.md:116-118` 明说改它就要重冻 | **本任务不动** |
| `golden/cp-random-vectors.json`（`FROZEN.md:51`） | 只有 `AnalyzerRandomGoldenTests.cs:22` | E1 删 → 一并删 |
| `golden/py-number-vectors.json`（`FROZEN.md:52`） | 只有 `AnalyzerNumberGoldenTests.cs:21,40,60` | E2 换 → 一并删 |
| `golden/py-json-vectors.json`（`FROZEN.md:53`） | 只有 `AnalyzerJsonGoldenTests.cs:19` | E3 换 → 一并删 |
| `synthetic/make_cp_vectors.py`（352 行，`FROZEN.md:54`） | 一次生成上面三张表（`:326-340`） | 与 E1/E2/E3 同生共死：三张表全删则整个脚本删；留一张则脚本要按段裁剪 |
| `verification/__pycache__/check-boundary-trees.cpython-314.pyc` | 无 | **删**（`rm -rf`） |
| `benchmarks/WinForward.E2E/scripts/__pycache__/*.pyc`（7 个） | 无 | **删**（`rm -rf`） |

`__pycache__` 的两个目录**都不在 git 里**（`git ls-files | rg 'pycache|\.pyc'` 为空），
`.gitignore` 末段有 `__pycache__/`。它们是 `python3` 直接跑脚本留下的字节码缓存，
不参与任何门禁，`rm -rf` 即可（`git clean -X` 也可以，但会顺带清掉别的忽略物）。

`verification/check-boundary-trees.py`（481 行）与 `check-fixture-drift.py`（141 行）
**不是仿真**：前者是"边界树对 C# 输出自己的断言"（脚本头 `:1-30` 明说"Python 参考被刻意不调用"），
后者是 fixture key 集与契约的机械守卫。它们与 `check-fairness.py`、`oracle-diff.py` 一样，
是**harness 的 Python 脚本**，是这套工具的正当组成部分，不属于"要被 C# 取代的仿真"。

还有一处文档会随三张向量表一起过期：`tests/WinForward.E2E.Tests/RepoPaths.cs:23-30`
的 `AnalyzerGolden` 说明（「the numbers CPython actually produced」「generated by
`verification/synthetic/make_cp_vectors.py`」）。三张表删掉后这个方法没有调用者，
应一并删除，`RepoPaths.cs` 只留 Tier 0 plans 与 CLI snapshots 两件事。

---

## 3. 改动后的删减量（预估）

| 项 | 删除行数（约） |
|---|---|
| `CpRandom.cs`（249）− `StableHash`+`DeriveSeed`（约 20） | 230 |
| `VerbatimNumber.cs` 的 BigInteger 机器（`Fraction`/`RoundHalfEven`/`Plain`/`Significant`/`Repr`/`Exponential`/`Shortest`/`DecimalExponent`/`CompareWithPowerOfTen`/`ReadsAsZero`） | 180 |
| `VerbatimJson.cs` 的转义与缩进手写部分 | 120 |
| `PythonExponential.cs`（并入格式串） | 21 |
| `JsonValue.Truthy`+`IsTruthy` | 20 |
| `Int(double)` 两份重复 | 8 |
| `TableLedger.DatagramCount` 的整数零分支 | 4 |
| 四个 Analyzer 测试文件里的 Python 精确断言（`AnalyzerRandomGoldenTests.cs` 103 行全删；`AnalyzerNumberGoldenTests.cs` 161 行、`AnalyzerJsonGoldenTests.cs` 128 行里删约 230 行、留约 60 行重写；`AnalyzerStatsAnchorTests.cs` 保留） | 约 280 |
| 三张向量表 + `make_cp_vectors.py` | 约 5 600 |
| **合计** | **约 6 400 行**（其中 C# 约 700 行，冻结物/测试约 5 700 行） |

---

## 4. 删除顺序（每一步都留一棵能编译、门禁能跑的树）

> **通用前置**：每一步之前先确认基线绿。
> ```bash
> dotnet build WinForward.slnx -c Release          # 0 warning
> dotnet test  WinForward.slnx -c Release
> python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py            # 全批次，--mode semantic 是默认
> python3 benchmarks/WinForward.E2E/scripts/check-fairness.py
> python3 benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py
> ```
> `oracle-diff.py` 自己解包 `synthetic-tree.tar.gz` 到 `/tmp/wf-synth` 并运行
> `benchmarks/WinForward.E2E.Analysis/bin/Release/net10.0/WinForward.E2E.Analysis`
> （`oracle-diff.py:274-305` 的 `extract_tree`/`run_analyzer`，在 `:1183-1185` 被调用），
> 所以它需要先 `dotnet build -c Release`。

**S0 — 先做不碰输出的那一类（互不依赖，可同一批做）**
1. E9：`Truthy` → `IsTrue`（5 处）+ 删 `IsTruthy`。
2. E11：三个 `Int(double)` 合成一个。
3. E12：`DatagramCount` 去掉整数零分支。
4. E14：两处 pragma 注释。
5. E13/E15/E16/E17：文档与名字。
6. E18：`rm -rf benchmarks/WinForward.E2E/scripts/__pycache__ benchmarks/WinForward.E2E.Analysis/verification/__pycache__`。
**判据**：`oracle-diff.py` 仍 rc=0（全批次）、`dotnet test` 绿。
S0 的六项里唯一会动发布字符的是 E12 的一格字形（测到的零从 `0.0` 变 `0`），
oracle 按数字判等；其余五项对一个字符都不动。

**S1 — `PythonExponential` 换成格式串（独立、最小）**
改 `Tables/GateValidity.cs:263` → 删 `Json/PythonExponential.cs`。
**判据**：`oracle-diff.py --batch 2` 与 `--batch 4` rc=0（§3.2 在批次 2）。

**S2 — `VerbatimJson` 换成 `Utf8JsonWriter`**
先写新实现与一条「解析后等价」的测试，再切 140 个调用点，最后删手写转义。
**判据**：`oracle-diff.py` 全批次 rc=0（语义）；**`--mode byte` 会红，必须登记**；
`python3 -c "import json;json.load(open('/tmp/wf-oracle/cs/verdict.json'))"` 能解析。

**S3 — `VerbatimNumber` 换 .NET 格式**
分两步做，每步都可回滚：
(a) 换 `Fixed`/`Json`，保留 `Cell` 的回退；
(b) 用一条格式串替掉 `General` 的 50 行。
**判据**：`oracle-diff.py --batch 1c,2,3,4,5` rc=0（表格在 1c/2/3/4/5，`verdict` 数字在 3/4/5）；
再单独看 §3.2 的 27 格 `1e-06`/`8.000e-03` 没变成 `0.000`：
`rg -c '1e-06' /tmp/wf-oracle/cs/tables.md` 应仍为 27。

**S4 — `CpRandom` → `System.Random`**
按 spike 的 4 行改法（`BootstrapPair.cs:60,113,160,249-258`），
`StableHash`/`DeriveSeed` 搬到新文件。先**不要**删 `CpRandom.cs`——
等 S5 的 oracle 放宽落地、确认差异表与 spike 的 75 条一致，再删文件与测试与向量表。
**判据**：`oracle-diff.py` 在**放宽前**应报约 75 条 `[value]`、**0 条 `[structure]`、0 条 missing**；
把这个数字与 spike 对上，就是这一步的验收。

**S5 — oracle 的窄放宽（`oracle-diff.py`）+ 冻结物收尾**
1. 在 `oracle-diff.py` 里给路径匹配
   `metrics/*/pairs[*]/{p_value,holm_p_value,p_equivalence,holm_p_equivalence}`（建议含 `ci95`，
   理由见 E1 的风险段：pass 数多时会动）加一条**声明式统计容差**
   （这些量是 `count/10000`，粒度 1e-4；建议绝对 1e-2 或"只查类型"），
   理由写进 `FROZEN.md` 与 `.trellis/spec/backend/measurement-tooling.md:133-143`。
2. 删 `Analysis/Stats/CpRandom.cs`、`tests/.../AnalyzerRandomGoldenTests.cs`、
   `verification/golden/cp-random-vectors.json`。
3. 删 `AnalyzerNumberGoldenTests.cs`/`AnalyzerJsonGoldenTests.cs` 里已被替代的断言与
   两张向量表。
4. 三张表都删后，删 `verification/synthetic/make_cp_vectors.py`，并更新 `FROZEN.md:43-63`
   的 §1.1（那节专门讲这三张表）与 `measurement-tooling.md:125`。
**判据**：`oracle-diff.py` 全批次 rc=0；`dotnet test -c Release` 绿；
`FROZEN.md` 里不再有指向已删文件的 hash 行。

**S6 — 收尾**
`python3 benchmarks/WinForward.E2E/scripts/check-fairness.py`
（它会自己跑 `make_tree.py` 再跑分析器，`check-fairness.py:430-432`）与
`python3 benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py`
（同为"自己建树、自己跑 C#、自己断言"，脚本头 `:1-30`）重跑 rc=0；
`check-fixture-drift.py --tree /tmp/wf-synth` 需要 `/tmp/wf-synth` 已存在
（它只读树，不建树，`check-fixture-drift.py:103-137`），所以放在 `oracle-diff.py` 之后跑。
`dotnet format --severity info --verify-no-changes --no-restore`；
`jb inspectcode`。最后按 AC1 的判据 `rg -i 'cpython' benchmarks/WinForward.E2E*` 复核：
只应剩 `verification/FROZEN.md` 等**文档对冻结参考的叙述**。

**顺序为什么是这样**：S0 无输出风险 → S1 最小可验证 → S2/S3 各自独立、可单独回滚 →
S4 是唯一会动数值的一步，必须在 S5 的 oracle 放宽**之前**做、以便先看见 75 条差异 →
S5 之后才删文件（此时删的都是没有消费者的死物）。

---

## 5. 「不许动」清单（看着像 Python，其实是契约）

| # | 项 | 位置 | 为什么不能动 |
|---|---|---|---|
| 1 | `verdict.json` 的 14 个顶层 key 与 21 个 `metrics` 成员 | `Verdict/VerdictWriter.cs:18-34`、`Metrics/MetricCatalogue.cs:123` | `oracle-diff.py:190-191` 的 `ALL_VERDICT_KEYS`；缺一个 rc=2 |
| 2 | `tables.md` 的 16 个小节编号与标题 | `Tables/TablesWriter.cs:51-69` | `oracle-diff.py:1052-1062` 逐字比对标题 |
| 3 | §5 的 154 行 11 格对 10 列表头 | `Tables/MarkdownTable.cs:9-13`、`Tables/TableLatency.cs:20-24` | `python-oracle-changes.md:105-153` §5.1；`oracle-diff.py` 逐行比单元格数 |
| 4 | `\|---\|---\|` 分隔行（无空格、无对齐冒号） | `MarkdownTable.cs:26` | 同 §5.2 |
| 5 | 空单元格（JSON null 的读数）≠ `n/a` ≠ `0` | `Stats/DescriptiveStats.cs:70-73` | §5.3；D21.2 #2；`check-boundary-trees.py:57,335,348` 的 `NULL_RATE_REASON` |
| 6 | `n/a` / `n/a (<reason>)` / `not carried (UDP bypassed)` 的用法 | 分散在 20+ 处；`Model/RowProfiles.cs`、`Tables/TableAvailability.cs:22` | §5.3；`oracle-diff.py:44-56` 的等价类表 |
| 7 | `fmt_stat` 的三种尾巴 `(n=1)` / `(n=K)` / `(n=K of T; j null, m unavailable)` 与 en dash | `DescriptiveStats.cs:103-115` | §5.4；金标 1085 格 |
| 8 | `tables.md` 的 en dash / `verdict.json` 的 `\u2013`（转义**按文件**） | `TablesWriter` vs `VerdictWriter` | §5.7；改统一会动 §0/§2 的文本 |
| 9 | UTF-8 无 BOM、`\n`、恰好一个尾换行 | `Cli/AnalysisRunner.cs:71-79`、`Cli/PlotsNotice.cs:41-46` | `oracle-diff.py:1019-1027` |
| 10 | `plots/SKIPPED.md` 的字节 | `verification/plots-SKIPPED.md` ↔ `Cli/PlotsNotice.cs:20-35` | `oracle-diff.py:1066-1079` 逐字节 |
| 11 | `verdict.json.raw` 与 §2 的路径拼写 | `Model/PosixPathText.cs`、`Loading/CampaignLoader.cs:60` | `oracle-diff.py:997-999` 对非 `WORDING_KEYS` 字符串走 `text_identical` |
| 12 | 账本发现顺序（pass 目录 → `--raw` → `--raw` 的父目录；四个固定名字先于 `*ledger*.jsonl`） | `Loading/LedgerLocator.cs:43-55`、`PythonGlob.cs:39-41` | `python-oracle-changes.md:105-153` §5.8；`Analysis/README.md:80-81` |
| 13 | 臂的加载顺序 `IDLE,LAT,LATLOAD,DNS,DNSALT,LOSS,REL,THRU,MIX,PERSIST,BASE` | `Model/ArmRecords.cs:36-38` | 决定 sample 序列与所有派生列表（`RunLoader.cs:265`） |
| 14 | 度量声明顺序 / 行声明顺序 / `passes` 与 `rows` 的数组序 | `MetricCatalogue.cs:123`、`Model/RowProfiles.cs`、`CampaignModel.cs:54` | `oracle-diff.py` 对数组按序比 |
| 15 | `run.json` 的 `arms[].file` 缺省是 `<name>.jsonl`，以及 roster 之外的文件也算臂 | `Loading/RunLoader.cs:232-275` | 加载语义（D21 §5） |
| 16 | `%` 阈值文本（`5 %`、`0.5 percentage points`、`0.1 percentage points`） | `Verdict/VerdictSections.cs:73-79` | `oracle-diff.py` 的 `PINNED_BLOCKS`（`:197`）对 `thresholds` 走 `strings_only` 逐字比对 |
| 17 | `bootstrap` 块的 `resamples`/`seed`/`min_passes` 是整数 | `VerdictSections.cs:65-71`、`AnalysisOptions.cs:19-23` | `oracle-diff.py:923-930` 会报 `float` 为 `[structure]` |
| 18 | `--raw/--out/--ledger/--flat/--warmup-seconds/--resamples/--seed` 的名字与默认值 | `Cli/AnalysisOptions.cs:16-32` | PRD 契约不变量 3；`measurement-tooling.md:108` |

---

## 6. 我无法从代码单独解决的问题

1. **`System.Random` 的版本稳定性要什么承诺**：官方只保证同一版本内。
   是否加一条固定 seed 的 tripwire（例如 `new Random(20261006).Next(1000)` 的前 N 项断言），
   还是接受"报告数字随运行时变"？这是产品决定，代码里没有答案。
   （`research/00-rng-swap-spike.md:64-68` 已把它列为待确认项。）
2. **真实 campaign 的 pass 数分布**：`ci95` 要不要一起放宽，取决于典型 pass 数。
   我能在冻结树（3 pass）之外做的只有合成实验（附录 A：5–10 pass 时 24 次试验 0 次移动，
   15 pass 时 6 次里 4 次移动）。**真实的 `--passes` 取值我没在仓库里找到权威来源**
   （`plans/*.json` 是臂的计划，不是 pass 数；`environment.json` 才带 `passCount`，
   但那要跑一次 campaign）。
3. **分析器是否需要在 Windows 上运行**：`PosixPathText` 的文档明说只处理 `/`。
   仓库有 Windows E2E 指引（`benchmarks/WinForward.E2E/AGENTS.local.md`），
   但没有证据表明分析器在 Windows 上被调用过。若要在 Windows 上跑，
   `Path.GetFileName`/`Path.GetFileNameWithoutExtension` 在 `RunLoader.cs:92,256,257` 与
   `PythonGlob.cs:56` 的行为也要一起审。
4. **`UnsafeRelaxedJsonEscaping` 对 `\u007f` 一类码位的实际输出**：
   `AnalyzerJsonGoldenTests.cs:61` 现在钉了 `\u007f`。换 `Utf8JsonWriter` 后它是否仍被转义，
   我未能从文档确定（需要一条单测，成本极小）。
5. **`E8` 的取舍（保留 `JSONDecodeError` 还是改用 .NET 名字）**：
   没有任何门禁覆盖（金标里 0 次命中），所以这是纯粹的发布文本决定，
   需要用户/PRD 表态，代码给不出答案。
6. **`tables.md` 的字形是否算契约**：oracle 语义模式对数字按容差、对文本逐字。
   于是"`1E-06` 还是 `1e-06`"在门禁眼里等价，在人眼里不等价。
   我的建议（E2/E4 采用自定义格式串保字形）是**保守选择**，不是代码能证明的结论。

---

## 附录 A · 独立复算：换 RNG 到底动了什么

**目的**：`research/00-rng-swap-spike.md` 是 C# 侧实测（改 4 行、跑 oracle、回滚）。
本文用一条**不落盘**的 Python 复算独立复核它，并回答 spike 留下的"pass 数 > 3 时 `ci95` 会不会动"。

**方法**：按 `Stats/BootstrapPair.cs:41-105,249-258` 与 `Stats/CpRandom.cs:60-76`
逐行复刻 bootstrap 与种子派生，输入取自 `verification/golden/py-verdict.json` 的
`metrics/<key>/rows/<row>/per_pass` 与 `metrics/<key>/threshold`，
用 CPython 自己的 `random.Random(seed).randrange(n)`（`CpRandom` 承诺复刻的就是它）
复算 `ci95` / `p_value` / `p_equivalence`，与金标比对；再把 RNG 换成
`random.Random("wf-"+str(seed))`（**同种子、不同算法**，等价于换 `System.Random`）重算。

**校准**：用 CPython 的 RNG 复算 212 个可比对里，**192 个的 `ci95` 与 `p_value` 与金标逐位相同**
（其余 20 个是 `ratio` 遇到非正值 → `ci95: null`，与金标一致）。复算模型成立。

**结果（换算法后）**

| 量 | 与金标不同 | 与金标相同 |
|---|---|---|
| `ci95[0]`/`ci95[1]`（384 个数） | **0** | 384 |
| `p_value`（192 个） | **54** | 138 |
| `p_equivalence`（182 个） | **11** | 171 |
| `holm_p_value` | **0**（多数被 `min(1, …)` 截到 1.0） | — |
| `raw_verdict` 翻转 | **0** | — |

样例：`lat.tcp_rtt.p50` 的 `wf-aot-opt` vs `proxifyre` 从 `0.526` 变 `0.515`；
`wf-fdd-opt` vs `proxifier` 从 `0.5366` 变 `0.5174`。
`verdict.json` 的 p 值按 `repr` 全精度打印，容差是 1 个末位单位（`oracle-diff.py:1176`），
所以这些差异**必然**报 `[value]`。这与 spike 的 75 条（`p_value` 58、`p_equivalence` 10、
`holm_p_equivalence` 7）一致；本文只逐个数了其中两个字段加一个未受影响的 `holm_p_value`。

**`ci95` 为什么不动，以及什么时候会动**：每行 3 个 pass 时，重采样中位数只有 3 个可能取值，
2.5 % 分位点几乎必然落在那 3 个取值的最小者上（其概率约 0.259，远大于 0.025），
于是**端点与随机流无关**。把 pass 数人为推到 5/6/8/10/15 各 6 组随机数据（共 30 次试验）：

| pass 数 | `ci95` 端点移动的试验数 |
|---|---|
| 5 / 6 / 8 / 10 | 0 / 6（各自） |
| 15 | **4 / 6** |

结论：3–10 个 pass 时 `ci95` 实际上稳定；pass 数再上去就会动。
**建议把 `ci95` 一并纳入窄放宽**，并在注释里写明"冻结树恰好没触发"——
这正是 spike §68 留给 C1 的那个未测项。
