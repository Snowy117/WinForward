# C1 执行计划（v2 · 已吸收 `research/plan-review.md` 的四条 BLOCKER 与七条 SHOULD-FIX）

> 每一步 = 一个可独立回滚的 commit。**改任何一步之前先确认基线绿**：
> ```bash
> dotnet build WinForward.slnx -c Release        # 0 warning
> dotnet test  WinForward.slnx -c Release        # 全绿
> python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py          # 全批次 rc=0
> python3 benchmarks/WinForward.E2E/scripts/check-fairness.py
> python3 benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py
> ```
> `oracle-diff.py` 自己解包 `synthetic-tree.tar.gz` 到 `/tmp/wf-synth` 并运行
> `Analysis/bin/Release/net10.0/WinForward.E2E.Analysis`，所以它之前必须先 Release build。
> 实测耗时：oracle 约 5 秒，build 约 10–15 秒。

## S0 · 零输出风险项（一批）

1. **`JsonValue.Truthy` → 已有的 `IsTrue`**。⚠️ 真实调用点（复核已改正，原计划写的写侧文件看不到
   `JsonValue`）：`Analysis/Model/RunSamples.cs:86,269`、`Analysis/Tables/GateFlow.cs:233`、
   `Analysis/Tables/TableEnvironment.cs:222,317`。**逐个确认它只可能看到 JSON 布尔**再切；
   删 `IsTruthy`（`Analysis/Model/JsonValue.cs:43-44,92-109`）。
2. 三个重复的 `Int(double)`（`Tables/GateFlow.cs:390`、`Checks/IdentityChecks.cs:343`、
   `Findings/FindingsCollector.cs:333`）合成一处共享实现，行为不变（向零截断）。
3. `Tables/TableLedger.cs:172-179` 去掉 `sum([])` 的整数零分支。**字形变化方向是 `0` → `0.0`**
   （空集改走浮点路径），oracle 按数字判等，允许。
4. `research/01` §2 的 E13/E15/E16/E17：**只改注释/文档叙事**。
5. **E14**（复核补入）：两处「Python 真值」pragma 的理由文案，同样只改注释。
6. `rm -rf benchmarks/WinForward.E2E/scripts/__pycache__ benchmarks/WinForward.E2E.Analysis/verification/__pycache__`
   （未跟踪、已被根 `.gitignore` 覆盖）。

**判据**：oracle 全批次 rc=0；`dotnet test tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj -c Release` 绿。

## S1 · `PythonExponential` → 格式串

`Json/PythonExponential.cs`（21 行）唯一调用者 `Tables/GateValidity.cs:263`。
⚠️ **不能用普通的 `E3`/`e3` 格式符**（它写 3 位指数 `0.000e+000`）；必须保持
`0.000e+00` / `8.000e-03` 这种 **2 位指数**字形（金标 27 格），用显式格式串（例如
`value.ToString("0.000e+00", CultureInfo.InvariantCulture)` 或手写指数拼接）。删文件。

**判据**：`oracle-diff.py --batch 2,4` rc=0，且全批次 rc=0。

## S2 · `VerbatimJson` 的转义/缩进换成显式实现（**不引入 `Utf8JsonWriter` 的自由发挥**）

⚠️ 复核**证伪**了原计划：`UnsafeRelaxedJsonEscaping` 的方向与金标**相反**。实测
`verification/golden/py-verdict.json`：非 ASCII 字节 **0**、`\uXXXX` 转义 **360**；
且 `AnalyzerJsonGoldenTests.cs:43-63` 钉住 `\u2013`/`\u007f`/代理对，同时要求
`'`、`+`、`<`、`>`、`&`、`/` **不**被转义（金标里有 **22 个字面 `+`**）。
`JavaScriptEncoder.Default` 转义全部非 ASCII ✅ 但会转义 `+<>&'` ❌；
`UnsafeRelaxedJsonEscaping` / `Create(UnicodeRanges.All)` 两头都错 ❌。

**做法（父 session 已裁定，见父任务 `design-decisions.md` D10）**：目标是**停止模仿 Python，
不是改变已发布的字节**。所以：

1. **保留现有转义规则**（JSON 必需的两个 + 五个短控制转义 + 非可打印 ASCII 写 `\uXXXX` + 代理对拆半；
   `'`/`+`/`<`/`>`/`&`/`/` 原样放过）。它的 docstring 从 "as `json.dumps(value)` writes it"
   改写成**我们自己的契约陈述**（语义模式下两侧都解码，所以字节只被 `--mode byte` 看见）。
2. **不引入 `Utf8JsonWriter`**，不改 140 个调用点。我独立复核过评审的测量：
   `py-verdict.json` 非 ASCII 字节 **0**、`\uXXXX` **360**、字面 `+` **22**、`>` **9**、`<`/`&` 0
   —— 两个内建编码器都各错一半，自定义编码器只是为了复刻我们已经写好的规则，纯属绕路。
3. S2 真正要做的是：删掉缩进/发射机制里**没有契约价值**的 Python 仿真代码，
   并把 `AnalyzerJsonGoldenTests` 的定位从"钉住 CPython"改成"钉住我们的发布文本"
   （断言值不变，措辞与注释变）。
4. **`--mode byte` 不允许为了变绿而改语义**；残余差异登记进 `research/notes.md` 即可。

⚠️ **测试必须在本步内处理**（复核 BLOCKER 3）：`AnalyzerJsonGoldenTests.cs` 的 7 个方法都直接调
`VerbatimJson`，本步一动它们就**编译不过**——所以本 commit 必须同时把测试改成对新实现的断言，
**不能把测试留给 S5**。

**判据**：语义全批次 rc=0；`python3 -c "import json;json.load(open('/tmp/wf-oracle/cs/verdict.json'))"` 可解析；
`dotnet build`/`dotnet test` 绿；**`--mode byte` 会红 → 把差异范围登记进 `research/notes.md`，不要消除**。

## S3 · `VerbatimNumber` → .NET 格式

分两小步（同一 commit 内可分开提交）：
(a) `Fixed`/`Json` 换 .NET 格式化；
(b) `General`（`%.*g` 机器）换显式格式串。
**必须保留 `%.3g`/`%.3e` 回退**——实测 `1e-06` 与 `8.000e-03` 在 `py-tables.md` 里**各恰好 27 格**。
BigInteger 半偶舍入机器可整段删（`py-tables.md` 里半偶中点 **0 命中**；只有向量表里有 21 个，
而向量表本步之后就会被删）。这是「门禁看不见的行为变化」，commit message 要写明。

⚠️ 同 S2：`AnalyzerNumberGoldenTests.cs` 的断言（`:29,47,66,73,95,108,128,135-143,151-159`）
在本步就会编译不过/失配 → **同步改**，不要留给 S5。

**判据**：`oracle-diff.py --batch 1c,2,3,4,5` rc=0。⚠️ **实测更正（β 段）**：
`1e-06` 的 27 格是 `Tables/GateValidity.cs:263` 里**硬编码的字面量**，掐掉 `%.3g` 回退它照样是 27；
真正钉住回退的是**区间单元格**（`0 [0–0.5]`、`1 [0.5–2]` …）——掐掉回退会让 7 条这样的格子变红。
所以回退的判据是**oracle 全绿**，不是 `1e-06` 的计数。
另一条实测更正：**`Fixed` 必须保持 half-to-even**（用 `ToString("F{d}")`），
因为金标里有真实的**零位小数 `.5` 中点**（`2 [1–4]` 就是 2.5）——改成 away-from-zero
会红 11 格表单元格，而 S5 的放宽只覆盖 `verdict.json:metrics/…`，永远吸收不了。

## S4 · `CpRandom` → `System.Random`（**先不删 `CpRandom.cs`**）

`Stats/BootstrapPair.cs:60,113,160,249-258`：`new CpRandom(seed)` → `new Random(seed)`、
`RandBelow(n)` → `Next(n)`（注意 SonarAnalyzer `S1905`：别留 `(int)` 强转）。
`StableHash`/`DeriveSeed` 摘到新文件（建议 `Stats/ComparisonSeed.cs`）并保留 `public`。
⚠️ `DeriveSeed` 的调用者不止 `BootstrapPair`：还有 `Checks/ControlDrift.cs:197`、
`Metrics/MetricComparisons.cs:225`，以及 `MetricComparisons.cs:22` 的 cref。

**判据（复核收紧后的原文）**：在**未放宽**的 `oracle-diff.py` 上跑全批次（`--mode semantic`），要求

- `differences:` 行匹配 `0 structure, ~75 value, 0 missing`；
- `compared` 行的 `missing` 为 **0**；`report.count(MISSING)` 也为 0（两者都要点名）；
- 所有 `[value]` 行的 path 满足 `verdict.json:metrics/*.pairs[*].{p_value,p_equivalence,holm_p_equivalence}`，
  按字段计数约为 `p_value 58 / p_equivalence 10 / holm_p_equivalence 7`；
- **`tables.md:*` 不得出现在任何差异行里**。

**证据要求（D11 修订）**：把完整输出 `2>&1 | tee` 存进 `research/notes.md`（"先看见、再解释"的过程证据）。
**不要**把这一步单独提交成"故意留红"的 commit——S4 与 S5 合并成**一个绿的 commit**；
commit message 引用 `notes.md` 里那份 75 条差异清单（含字段分布 58/10/7）。
说明：spike 的 75 是在 **C# 基线 vs 换 RNG 的 C#** 上测的；本步是 vs **冻结 Python 金标**，
两者在语义模式下应一致（基线 vs 金标本来就是 rc=0），但容差的基准侧不同，对齐即可。

## S5.0 · 先把两条舍入规则对齐（β 段实测发现的遗留）

β 段把 `Fixed` 定为 **half-to-even**（`ToString("F{d}")`，因为金标里有真实的零位 `.5` 中点），
但 α 段的 S1 给 `%.3e` 单元格用的是自定义 `0.000e+00`（**away-from-zero**）——
同一个文件里两套舍入规则。做法：让 `%.3e` 也走 half-to-even，同时**保住两位指数的字形**
（`ToString("E3")` 会给三位指数 `1.234E-003`：截掉指数多余的一位再转小写即可）。
判据：`oracle-diff.py` 全批次 rc=0，且 `8.000e-03` 仍是 27 格、`1e-06` 仍是 27 格。
若实测发现 `E3` 的舍入与 `F` 不一致（用一条临时断言确认，结论写进 notes），就保留现状并把不一致登记清楚。

## S5 · oracle 窄放宽 + 冻结物收尾

### S5.1 放宽的落点（复核给出了精确位置）

`Comparer.compare_slice`（`oracle-diff.py:1005-1016`）→ `compare_json`（`:961-1003`）的**数字分支**
（`:968-972`）。`metrics` 不在 `PINNED_BLOCKS`（`:197`），`compare_members` 碰不到它。
数字在语义模式下是 `VerdictNumber`（有 `.raw`/`.value`）。

- **path 是点分的**，不是斜杠：`verdict.json:metrics/lat.tcp_rtt.p50.pairs[8].p_value`、
  `…pairs[11].ci95[0]`。metric 成员名自身含点（`lat.tcp_rtt.p50`），所以正则用 `.+` 而不是 `[^.]+`：

  ```python
  STATISTICAL_PATH = re.compile(
      r"verdict\.json:metrics/.+\.pairs\[\d+\]\."
      r"(p_value|holm_p_value|p_equivalence|holm_p_equivalence|ci95\[[01]\])$")
  ```

- **容差分两类**（复核 SHOULD-FIX 5）：
  `p_value`/`holm_p_value`/`p_equivalence`/`holm_p_equivalence` 是 `count/10000`，粒度 `1e-4`
  → 绝对 `1e-2`；
  `ci95[0]`/`ci95[1]` 是**度量尺度上的区间**（实测跨度 `0.094`(us 比值) ~ `0.8`(pp)）
  → 相对容差 `max(1e-2, 1e-2 * abs(expected))`。
  docstring 要写明这两类粒度不同。
- 只放宽**数值叶子**；`pairs[i].verdict`/`raw_reason`/`holm_reason` 是字符串，天然不受影响
  （`reason` 在 `WORDING_KEYS` 里、`raw_reason`/`holm_reason` 不在，这是既有行为，本次别动）。
- 理由写进脚本 docstring + `verification/FROZEN.md` + `.trellis/spec/backend/measurement-tooling.md`。

### S5.2 冻结物与测试收尾

- 删 `Analysis/Stats/CpRandom.cs`。
- ⚠️ `tests/.../AnalyzerRandomGoldenTests.cs` **不能整文件删**（复核 BLOCKER 2）：它的 `:93-102`
  是**唯一**钉住 `StableHash`/`DeriveSeed` 的测试，而 AC4 要求这两个函数以新家存活。
  做法：删掉 CPython 向量部分（`:20-90` 一类），把 `:93-102` 的三条断言**改写**到新家
  （`Stats/ComparisonSeed.cs` 的对应 API）——`DeriveSeed(20261006, key) == 21128053` 这类值**必须保留**
  （它们是 `--seed` 复现性的锚），然后删 `verification/golden/cp-random-vectors.json`。
- S2/S3 已在各自步内处理过 `AnalyzerNumberGoldenTests.cs`/`AnalyzerJsonGoldenTests.cs`；
  本步只删 `py-number-vectors.json`、`py-json-vectors.json` 与 `make_cp_vectors.py`，
  并确认测试文件里不再有对它们的读取。
- `tests/WinForward.E2E.Tests/RepoPaths.cs` 的 `AnalyzerGolden`（或同类）会变成死代码 → 删除，
  否则 `UnusedMember` 类分析器会红（`research/01` E18:651-654）。
- 更新 `FROZEN.md` §1.1（`:43-63`，专讲这三张表）与 `measurement-tooling.md:125`。

**判据**：oracle 全批次 rc=0；`dotnet test -c Release` 绿；`FROZEN.md` 无指向已删文件的哈希行。

### S5.3 D6 的发布文本（复核补入：原计划没有落脚点）

`Loading/RunLoader.cs` 的 `ReferenceName`（CPython 异常名）与 `Model/JsonText.cs` 的
`None`/`True`/`False` → 改成 .NET 名（金标 0 命中 ⇒ 不会红）。commit 里说明这是**有意的发布文本变化**。

## S6 · 收尾

1. oracle 全批次 + `check-fairness.py` + `check-boundary-trees.py` rc=0；
   `check-fixture-drift.py --tree /tmp/wf-synth`（要在 oracle 之后跑）。
2. `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` 空输出。
3. `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` → 0 `<Issue>`。
4. 文档口径：`Analysis/README.md` 里"与参考逐字一致 / `--seed` 逐字复现"改为"同一运行时下可复现"；报告删减量。
5. `rg -i 'cpython' benchmarks/WinForward.E2E*` 只剩 `verification/FROZEN.md` 等文档对冻结参考的叙述。

## 「不许动」名单（复核补入第 19 条）

`research/01` §5 的 18 条 + **`Loading/JsonReader.cs` 的 `utf-8-sig` 容忍**（同样的"实现即契约"）。

## 交给父任务登记（不要在 C1 里做）

- 不搬 `oracle-diff.py`（C3 会搬）→ 改动只在文件内部。
- C2/C3/C4 的条目一律写进 `research/notes.md`。
- D7 的 CI step 归 C4，不在本任务。
