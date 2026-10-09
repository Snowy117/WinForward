# C1 · S0/S1 实施笔记

**日期**：2026-10-09
**范围**：本次 dispatch 只做 S0 与 S1；S2–S6、`oracle-diff.py`、向量表、`check-fairness.py`、
`check-boundary-trees.py`、`dotnet format`、`jb inspectcode` 一律未触碰（后续 dispatch 负责）。

## 1. 完成项与实测证据

| 步 | 内容 | 实测 |
|---|---|---|
| S0.1 | `JsonValue.Truthy`/`IsTruthy` 删除，5 个调用点改 `IsTrue` | `rg 'Truthy'` 全库 0 命中 |
| S0.2 | 三份 `Int(double)` 合一为 `Model/JsonNumber.IntText` | 输出逐字节不变 |
| S0.3 | `TableLedger.DatagramCount` 去掉 `sum([])` 整数零分支 | 下表 §2 |
| S0.4 | E13/E14/E15/E16/E17 只改注释与叙述 | 代码行为未动 |
| S0.5 | 删两个 `__pycache__`（8 个 `.pyc`） | 均未跟踪、被 `.gitignore:28` 覆盖 |
| S1 | `PythonExponential.Fixed(v, 3)` → `v.ToString("0.000e+00", InvariantCulture)`；删文件 | `8.000e-03` 仍 27 格，与金标一致 |

判据：Analysis 项目 build 0 警告；`oracle-diff.py --batch 2,4` rc=0（15 切片）；
全批次 rc=0（`compared 51 slice(s): 0 differ(ent), 0 missing`）；E2E 测试 361/361 绿。

## 2. 需要更正的两处文档叙述（实测与文档相反或不一致）

1. **E12 的字形变化方向写反了。** `implement.md` 与 `research/01` §2 E12 都说
   「测到的零从 `0.0` 印成 `0`」，但代码现状是**空普查印 `"0"`（整数零），非空普查才走
   `VerbatimNumber.Json` 印浮点**（金标 §14.2 里非空普查是 `10400.0`、`600.0`）。
   删掉分支后的实测方向是 **`0` → `0.0`**：§14.2 的 "ledger datagrams" 列共 **60 行**变化
   （IDLE/REL/THRU/PERSIST 一类空普查行），其余单元格、`verdict.json`、`plots/` 全字节不变。
   oracle 按数字判等 → 仍 rc=0。**结论不变（允许），但叙述该反过来写。**
2. **S1 引入了一个已登记的舍入差异。** .NET 的**自定义** `e` 格式串对十进制中点
   **远离零**舍入，而被删掉的仿真走 `ToString("E3")`（**半偶**，与 Python `%.3e` 一致）：
   实测 `1234.5` → 新 `1.235e+03` / 旧 `1.234e+03`，`9.9995e-3` → 新 `1.000e-02` / 旧 `9.999e-03`。
   金标这一格是 `0.008`（非中点），27 格逐字不变；oracle 容差是参考末位 1 个单位，可吸收 ±1 ulp。
   属于 `design.md` §0「可换实现」那一类，但**应在 S3 的文档里一并登记**。

## 3. 有意未做（越界或属于其它步骤）

- **`Cli/AnalysisRunner.cs:71-79` 的 `OpenOutput` 注释未改。** `research/01` E17 把它列进
  「自我叙述」，但该注释没有 reference/CPython 叙述，只说明「这些字节是 oracle 比对的一部分
  （D20.5）」——`design.md` §2.1 第 6 条明说 `(D20.5)` 这类决策号是房规，应当保留。故不动。
- **`JsonValue.cs` 的类摘要仍是「the way the reference's `dict.get` does」。** E9 只要求删
  `Truthy`；其余 reference 叙述属于 C4 的口径清理，不在 S0。
- **`TableEnvironment.cs:222` 用的是字面量 `"absent"` 而非 `ArmKeys.Sample.Absent`。**
  同一文件 `:317` 用的是常量，此处不一致。属于 C2 的命名/惯用法，不在 S0。
- **E9 建议的可选保险未加**：`ContractShapeTests` 里断言那五个键必须是 JSON boolean
  （`research/01` E9 称成本 3 行、可选）。dispatch 未列入 S0，留给测试形状那一步。
- 其余 `.trellis/scripts/**/__pycache__`（10 个 `.pyc`）不是 E18 点名的目标，未删。

## 4. `Truthy` 五个调用点的布尔性核查（先核查后改，逐条有写侧证据）

`implement.md` 与 `dispatch` 原列的调用点（`Client/ResourceSampleWriter.cs` 等）是**写侧**文件，
不是调用者；实际调用者是下面五个，全部只可能是 JSON boolean：

| 调用点 | 键 | 写侧证据 |
|---|---|---|
| `Model/RunSamples.cs:86` | `readError` | `ResourceSampleWriter.cs:98-101` 只在 `readErrors > 0` 时写 `true`（否则缺失）；`make_tree.py:347` 也只写 `True` |
| `Model/RunSamples.cs:269` | `countersRead` | `ResourceSampleWriter.cs:80` `WriteBoolean(..., process.CountersRead)`；`make_tree.py:326,366` 全是布尔 |
| `Tables/GateFlow.cs:233` | `failed`（`row.Document` 是 `ClientRun` 的 run.json） | `RunFileWriter.cs:57` `WriteBoolean(ArmKeys.Run.Failed, failed)` |
| `Tables/TableEnvironment.cs:222` | `absent` | `ResourceSampler.cs:162-165` 只在进程数为 0 时写 `true`（否则缺失）；冻结树里不出现该键 |
| `Tables/TableEnvironment.cs:317` | `failed`（run.json） | 同 `RunFileWriter.cs:57` |

结论：缺失/`true`/`false` 三种输入下 `Truthy` 与 `IsTrue` 行为完全一致；契约外的非布尔值
（例如 `"readError": 1`）会由真变假，但写者只有本仓库的 writer 与冻结树生成器，二者都写布尔。

---

# C1 · S2/S3 实施笔记

**日期**：2026-10-09
**范围**：本次 dispatch 只做 S2（`Json/VerbatimJson.cs`）与 S3（`Json/VerbatimNumber.cs`），
以及两步各自必须在本步内处理的测试（`AnalyzerJsonGoldenTests.cs`、`AnalyzerNumberGoldenTests.cs`）。
未触碰 `Stats/BootstrapPair.cs`、`Stats/CpRandom.cs`、`scripts/oracle-diff.py`、三张向量表、
`make_cp_vectors.py`，以及 `research/01` §5 名单（含 `Loading/JsonReader.cs` 的 `utf-8-sig`）。

## 1. S3：计划被实测证伪的两条（**必须回写进 `implement.md` / `research/01`**）

### 1.1 `Fixed` 改 away-from-zero 会让 oracle 变红 —— 最终采用 .NET 标准 `F`（半偶）

`implement.md:52,77` 与 `plan-review` NIT 4 的依据「`py-tables.md` 里半偶中点 **0** 命中」**只查了
3 位小数的网格中点**（`0.062/0.188/0.438/0.562/0.688/0.812/0.938`），漏掉了**零位小数**的 `.5`
中点，而冻结表里它们是真实数据：`0 [0–0.5]`、`2 [1–4]`（2.5）、`15 [14–15]`（14.5）、
`5993 [5990–5996]`（5996.5）等。

实测：`Fixed` 用 `Math.Round(..., MidpointRounding.AwayFromZero)` + `"F{d}"` 时，全批次
`compared 51 slice(s): 2 differ(ent), 0 missing` / `differences: 0 structure, 11 value, 0 missing` /
`rc=1`。11 条全部是表单元：

```
tables.md:5.`tcp-rtt`[row=proxifier&arm=PERSIST].count        15 [14–15] → 15 [15–15]
tables.md:8.table1[row=wf-aot-opt&arm=LOSS].arrived           6000 [5998–6000] → 6000 [5999–6000]
tables.md:8.table1[row=wf-aot-opt&arm=LOSS].late              0 [0–0.5] → 0 [0–1]
tables.md:8.table1[row=wf-aot-opt&arm=MIX].never              0 [0–0.5] → 0 [0–1]
tables.md:8.table1[row=wf-fdd-opt&arm=MIX].late               0 [0–0.5] → 0 [0–1]
tables.md:8.table1[row=wf-fdd-opt&arm=MIX].never              0 [0–0.5] → 0 [0–1]
tables.md:8.table1[row=proxybridge&arm=LOSS].arrived          5993 [5990–5996] → 5993 [5990–5997]
tables.md:8.table1[row=proxybridge&arm=LOSS].late             2 [1–4] → 3 [1–4]
tables.md:8.table1[row=proxybridge&arm=LOSS].corruptDatagrams 1 [0.5–2] → 1 [1–2]
tables.md:8.table1[row=proxybridge&arm=LOSS].corrupt          1 [0.5–2] → 1 [1–2]
tables.md:8.table1[row=proxybridge&arm=MIX].late              1 [0.5–2] → 1 [1–2]
```

**最终实现**：`Fixed` = `value.ToString("F" + digits, InvariantCulture)`，即 .NET 自己的定点格式化。
它按**精确二进制值**做 half-to-even，与 `%.*f` 逐条相同 —— 230 条 `fixed` 向量实测 **0** 不一致，
上述 11 条也随之消失，全批次 rc=0。BigInteger 机器仍然整段删除。
附带证伪：`VerbatimNumber.cs` 原注释「.NET 的 `F` 舍入不是精确二进制上的半偶」在 .NET 10 上为假
（`(0.0625).ToString("F3")` = `0.062`、`(2.5).ToString("F0")` = `2`）。

**另一条实测**：`Math.Round` 与 `ToString("F")` 不是同一条规则 ——
`Math.Round(-2.675, 2, AwayFromZero)` = `-2.68`，而 `(-2.675).ToString("F2")` = `-2.67`
（`Math.Round` 的中点判定是十进制的，不是精确二进制的）。所以 NIT 4 说的 21 条「中点样本」里，
真正会让两种规则分歧的是 **11** 条（0.5@0、2.5@0、0.125@2、±2.675@2、0.0625/0.3125/0.5625/0.8125/−0.0625@3、1.0005@3）。

### 1.2 「`1e-06` 27 格是 `%.3g` 回退的判据」是误归因

27 个 `1e-06` 是 `Tables/GateValidity.cs:263` 里**硬编码的字面量**（`$"{….ToString("0.000e+00")} < 1e-06"`），
不是 `Cell` 回退的输出；`8.000e-03` 才是 S1 的 `%.3e` 格。把 `ReadsAsZero` 临时改成恒假
（回退失效）实测：`1e-06` 计数**仍是 27**（字面量），但 oracle 报
`0 structure, 7 value` 红在**区间单元格**上：`0 [0–0.5]`→`0 [0–0]`（4 条）、
`1 [0.5–2]`→`1 [0–2]`（3 条）。所以回退**确实被门禁钉住**，只是判据是零位小数的 `.5` 区间，
不是那 27 个字面量（`numbers_equivalent` 的 `max(两侧末位单位)` 会放过 `1e-06`→`0.000`）。
`1e-06`/`8.000e-03` 各 27 格在 S2/S3 之后逐字未变。

### 1.3 `%.*g` → `G<precision>` + 小写指数：135 条向量 **0** 不一致

`General` = `value.ToString("G" + digits, InvariantCulture).Replace('E', 'e')`（`precision == 0 → 1`
保留）。向量表的精度集合是 `{1,3,6}`，含 `1e-06`、`4.94e-324`、`1e+05`、`1e+100`、`0.000123`、
`9.9999e-05→0.0001`、`1.8e+308`：全部逐字相同，包括「先舍入再看指数」的 `99999 → 1e+05`。

### 1.4 `Json`（Python `repr`）→ .NET `R`：发布文本变化（有意）

`1.0`→`1`、`-0.0`→`-0`、`1e-05`→`1E-05`、`1e+16`→`10000000000000000`（**注意**：.NET `R`
在 `1e17` 才切指数、小值在 `1e-05` 切，与 `repr` 的 `1e16` 不同 —— 测试注释已按实测写），
`NaN`/`Infinity`/`-Infinity` 与 Python 的 JSON 拼写一致。语义模式下全部数值相等 ⇒ oracle rc=0；
`tables.md` 438 行变化**全部**是 ledger 的 datagrams 列去掉 `.0`（规范化尾部 `.0` 后两文件逐字节相同），
`verdict.json` 2918 行变化。`--mode byte` 的红面因此扩大（见 §3）。

## 2. S2：`VerbatimJson` 没有可删的仿真代码（结论性发现）

逐成员核对后**没有任何一段是「纯仿真、无契约价值」的**：`String` 68 处、`Object` 34、
`Integer` 21、`Boolean` 17、`Number` 15、`Array` 11、`StringArray` 7、`Null` 6 个调用点；
手工线程化的 `level` **就是**缩进契约本身（金标与 `--mode byte` 逐字节读它）；转义规则是契约。
所以 S2 的产出是**文档化**（199 → 202 行）：转义规则的 docstring 从「as `json.dumps` writes it」
改为我们自己的契约陈述，并写明「语义模式两侧都解码，所以这些字节只被 `--mode byte` 与当文本读的
读者看见」，以及两个内建编码器各错一半（`JavaScriptEncoder.Default` 转义 `+<>&'`、
`UnsafeRelaxedJsonEscaping` 放过非 ASCII）→ 这正是 D10 的结论。

## 3. `--mode byte` 的红面（登记，不消除）

| | 切片不同 | 分类 |
|---|---|---|
| 本步之前（基线二进制 + 保存的输出） | 2 | 0 structure, 2 value |
| 本步之后 | **25** | 0 structure, 25 value, 0 missing |

25 = `tables.md:14`（ledger datagrams 的 `.0`）+ 24 个 `verdict.json:*`
（`control_blocks`、`dual_phase`、`ledger`、`metrics/cpu,dns,dnsalt×2,lat×4,latload×2,loss×3,mem,mix,persist×2,rel×2,thru`）。
`--mode byte` 不是批次判据（D21 §2），基线本来就是 rc=1；未为它改任何语义。
完整输出存于 `/tmp/wf-oracle/byte-before.txt`、`/tmp/wf-oracle/byte-after.txt`。

## 4. 本步内处理的测试（原计划留给 S5，BLOCKER 3 的 S3 部分）

- `AnalyzerNumberGoldenTests.cs`：`EveryGoldenFixedValueIsReproduced`、`EveryGoldenGeneralValueIsReproduced`、
  两条中点测试（半偶理论 + 「away-from-zero 会移动 4 格」的负控制）、`General` 形状表、
  `Cell` 回退表**全部原样保留且全绿**（因为最终采用半偶）。
  只有两条 `repr` 断言重写：`EveryGoldenFloatReprIsReproduced` → `EveryGoldenFloatReadsBackAsTheSameValue`
  （改为「发布文本必须读回同一个 double」+ 非有限拼写），
  `TheReprKeepsThePointOfAnIntegralValueAndItsOwnExponentRange` → `TheFloatTextIsTheShortestThatReadsBack`
  （按实测写我们发布的字形）。
- `AnalyzerJsonGoldenTests.cs`：转义/嵌套/顺序断言全部保留；**唯一**改动是
  `"one document with every scalar kind"` 这一例（含 `100.0`）从逐字比对改为
  `JsonElement.DeepEquals` 与向量自身的值比结构（并用 `Assert.Equal(1, floats)` 钉住豁免只有
  这一例），因为 S3 之后它的浮点文本是 `100`。12 例中其余 11 例仍逐字节比对。
  三个方法名/注释按「钉住我们的发布文本」改写（断言值不变，`Number(1.0)` 由 `"1.0"` → `"1"`）。
- 向量表、`RepoPaths.AnalyzerGolden`、`make_cp_vectors.py` 均未动（S5 收尾）。

## 5. 有意留下 / 需要父任务知道的

1. **S1 与 S3 的舍入规则不一致（已登记）**：`Tables/GateValidity.cs:263` 的 `%.3e` 格用**自定义**
   格式串（away-from-zero），`Fixed` 现在用标准 `F`（half-to-even）。冻结树两处都没有中点，
   门禁看不见；要统一只能二选一，而把 `Fixed` 改成 away-from-zero 会红 11 条（§1.1）。
2. **非有限值的字形变化**：删掉 `Named()` 的 printf 仿真后，表格里的非有限值从 `nan`/`inf`/`-inf`
   变成 .NET 拼写 `NaN`/`Infinity`/`-Infinity`。金标 0 命中（`nan`/`inf` 只作为
   `WinForward`/`informational`/`inflated` 的子串出现），无门禁覆盖；方向与 D6 一致，
   若 C4 要统一发布文本口径，这里是其中之一。
3. **`VerbatimNumber.cs` 的类注释仍点名 `verification/golden/py-number-vectors.json`**（测试仍在读它）。
   S5 删表时必须一并改这段（plan-review SHOULD-FIX 1 的同类）。
4. **`RepoPaths.AnalyzerGolden` 暂时仍有 3 个调用者**（三个向量表测试都还在读各自的表），
   不是死代码；S5 处理。
5. **`implement.md` / `research/01` 的两处叙述需要更正**：S3 的「半偶中点 0 命中」与
   「27 格 `1e-06` 是回退判据」（§1.1、§1.2），以及 S1 note 里「S3 的文档里一并登记」的
   舍入差异（已登记在 §5.1）。

## 6. 本步验证（逐条实跑）

```
dotnet build benchmarks/WinForward.E2E.Analysis/… -c Release   → Build succeeded, 0 Warning(s), 0 Error(s)
python3 …/oracle-diff.py --batch 1c,2,3,4,5                    → compared 42 slice(s): 0 differ(ent), 0 missing
                                                                 differences: 0 structure, 0 value, 0 missing / rc=0
python3 …/oracle-diff.py                                       → compared 51 slice(s): 0 differ(ent), 0 missing
                                                                 differences: 0 structure, 0 value, 0 missing / rc=0
rg -c '1e-06' /tmp/wf-oracle/cs/tables.md                      → 27
rg -c '8\.000e-03' /tmp/wf-oracle/cs/tables.md                 → 27
dotnet test tests/WinForward.E2E.Tests/… -c Release            → Passed! Failed: 0, Passed: 361, Total: 361
dotnet build WinForward.slnx -c Release                        → Build succeeded, 0 Warning(s), 0 Error(s)
```

改动面：4 个文件，+153/−343（`VerbatimNumber.cs` 342 → 105 行）。
`dotnet format` / `jb inspectcode` / `check-fairness.py` / `check-boundary-trees.py` 未跑（主 session 负责）。

---

# C1 · S4 预放宽证据（换 `System.Random` 之后、放宽之前）

**日期**：2026-10-09
**二进制**：`benchmarks/WinForward.E2E.Analysis/bin/Release/net10.0/WinForward.E2E.Analysis.dll`
sha256 `644e050987cfd80d317448e03ce0038083a6e4ef9c5ff74b01135e1bcc59b7e1`（含 S5.0 的指数格改动）

命令（**未放宽**的 differ，默认 `--mode semantic`，全批次）：

```bash
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py
```

汇总行与逐条 `[value]` 的**完整输出**（`2>&1 | tee`，rc=1）：

```text
e2e-analysis: 3 pass(es), 9 row(s), 2 ledger(s), 27 loaded run(s) -> /tmp/wf-oracle/cs/tables.md
e2e-analysis: verdict -> /tmp/wf-oracle/cs/verdict.json; warmup 5.0 s, 10000 resamples, seed 20261006, min passes 3
e2e-analysis: plots/ is not rendered; wrote plots/SKIPPED.md
e2e-analysis: pass1 ledger(s): /tmp/wf-synth/ledger-main.jsonl, /tmp/wf-synth/ledger-direct.jsonl
e2e-analysis: pass2 ledger(s): /tmp/wf-synth/ledger-main.jsonl, /tmp/wf-synth/ledger-direct.jsonl
e2e-analysis: pass3 ledger(s): /tmp/wf-synth/ledger-main.jsonl, /tmp/wf-synth/ledger-direct.jsonl
reference: /home/paff/Projects/WinForward/benchmarks/WinForward.E2E.Analysis/verification/golden
produced:  /tmp/wf-oracle/cs
mode:      semantic (tolerance 1 last-digit unit(s))
batches:   1a, 1b, 1c, 2, 3, 4, 5
-- batch 1a
   tables.md:preamble: equal
   tables.md:15: equal
   verdict.json:generated_by: equal
   verdict.json:raw: equal
   verdict.json:flat_mode: equal
   verdict.json:passes: equal
   verdict.json:rows: equal
-- batch 1b
   verdict.json:bootstrap: equal
   verdict.json:thresholds: equal
-- batch 1c
   tables.md:1: equal
   tables.md:2: equal
   verdict.json:row_profiles: equal
-- batch 2
   tables.md:0: equal
   tables.md:3: equal
   verdict.json:findings: equal
   verdict.json:findings_by_severity: equal
-- batch 3
   tables.md:5: equal
   tables.md:8: equal
   tables.md:9: equal
   verdict.json:metrics/lat.tcp_rtt.p50: DIFFERS (11 difference(s))
   verdict.json:metrics/lat.tcp_rtt.p99: DIFFERS (11 difference(s))
   verdict.json:metrics/lat.udp_rtt.p50: DIFFERS (10 difference(s))
   verdict.json:metrics/lat.udp_lossRate: equal
   verdict.json:metrics/latload.tcp_rtt.p50: DIFFERS (7 difference(s))
   verdict.json:metrics/latload.tcp_rtt.p99: DIFFERS (7 difference(s))
   verdict.json:metrics/loss.lossRate: DIFFERS (3 difference(s))
   verdict.json:metrics/loss.corruptRate: DIFFERS (4 difference(s))
   verdict.json:metrics/loss.foreignConnection: equal
   verdict.json:metrics/dns.answerRate: equal
   verdict.json:metrics/dns.rtt.p50: equal
   verdict.json:metrics/dnsalt.answerRate: equal
   verdict.json:metrics/dnsalt.rtt.p50: equal
   verdict.json:metrics/thru.goodputMbps: equal
   verdict.json:metrics/mix.udp.lossRate: equal
-- batch 4
   tables.md:4: equal
   tables.md:6: equal
   tables.md:7: equal
   tables.md:10: equal
   tables.md:11: equal
   verdict.json:metrics/rel.unexpectedEofRate: equal
   verdict.json:metrics/rel.fidelityRate: equal
   verdict.json:metrics/persist.responseRate: DIFFERS (3 difference(s))
   verdict.json:metrics/persist.reconnects: equal
   verdict.json:metrics/mem.privateBytes.p50: DIFFERS (15 difference(s))
   verdict.json:metrics/cpu.proxy.vcpuPct: DIFFERS (4 difference(s))
-- batch 5
   tables.md:12: equal
   tables.md:13: equal
   tables.md:14: equal
   verdict.json:control_blocks: equal
   verdict.json:dual_phase: equal
   verdict.json:ledger: equal
-- differences (75)
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[8].p_equivalence: expected '0.2653' vs actual '0.2524'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[8].holm_p_equivalence: expected '0.5198' vs actual '0.5048'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[11].p_value: expected '0.526' vs actual '0.5322'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[12].p_value: expected '0.5148' vs actual '0.5074'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[12].p_equivalence: expected '0.2599' vs actual '0.265'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[12].holm_p_equivalence: expected '0.5198' vs actual '0.5048'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[13].p_value: expected '0.5262' vs actual '0.536'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[18].p_value: expected '0.5366' vs actual '0.5128'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[19].p_value: expected '0.528' vs actual '0.5302'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[30].p_value: expected '0.518' vs actual '0.5058'
   [value] verdict.json:metrics/lat.tcp_rtt.p50.pairs[31].p_value: expected '0.5216' vs actual '0.5296'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[8].p_equivalence: expected '0.2579' vs actual '0.2593'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[8].holm_p_equivalence: expected '0.5094' vs actual '0.5186'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[11].p_value: expected '0.5118' vs actual '0.5138'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[12].p_value: expected '0.5104' vs actual '0.5136'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[12].p_equivalence: expected '0.2547' vs actual '0.2672'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[12].holm_p_equivalence: expected '0.5094' vs actual '0.5186'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[13].p_value: expected '0.5184' vs actual '0.5162'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[18].p_value: expected '0.5144' vs actual '0.5124'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[19].p_value: expected '0.5114' vs actual '0.5154'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[30].p_value: expected '0.5212' vs actual '0.5134'
   [value] verdict.json:metrics/lat.tcp_rtt.p99.pairs[31].p_value: expected '0.517' vs actual '0.5216'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[8].p_equivalence: expected '0.2594' vs actual '0.2581'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[8].holm_p_equivalence: expected '0.2594' vs actual '0.2581'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[9].p_value: expected '0.5172' vs actual '0.5294'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[11].p_value: expected '0.5326' vs actual '0.528'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[13].p_value: expected '0.519' vs actual '0.5104'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[15].p_value: expected '0.5222' vs actual '0.527'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[19].p_value: expected '0.5028' vs actual '0.5108'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[22].p_value: expected '0.51' vs actual '0.5346'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[24].p_value: expected '0.5348' vs actual '0.516'
   [value] verdict.json:metrics/lat.udp_rtt.p50.pairs[31].p_value: expected '0.5196' vs actual '0.5122'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[8].p_value: expected '0.5264' vs actual '0.5286'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[17].p_value: expected '0.527' vs actual '0.5194'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[18].p_value: expected '0.5128' vs actual '0.5204'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[18].p_equivalence: expected '0.2621' vs actual '0.2573'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[18].holm_p_equivalence: expected '0.2621' vs actual '0.2573'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[19].p_value: expected '0.5174' vs actual '0.5156'
   [value] verdict.json:metrics/latload.tcp_rtt.p50.pairs[31].p_value: expected '0.5098' vs actual '0.5222'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[8].p_value: expected '0.5202' vs actual '0.5072'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[17].p_value: expected '0.518' vs actual '0.5126'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[18].p_value: expected '0.5272' vs actual '0.5134'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[18].p_equivalence: expected '0.2551' vs actual '0.2612'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[18].holm_p_equivalence: expected '0.2551' vs actual '0.2612'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[19].p_value: expected '0.528' vs actual '0.508'
   [value] verdict.json:metrics/latload.tcp_rtt.p99.pairs[31].p_value: expected '0.5206' vs actual '0.5042'
   [value] verdict.json:metrics/loss.lossRate.pairs[13].p_value: expected '0.4976' vs actual '0.5028'
   [value] verdict.json:metrics/loss.lossRate.pairs[19].p_value: expected '0.7656' vs actual '0.776'
   [value] verdict.json:metrics/loss.lossRate.pairs[24].p_value: expected '0.4956' vs actual '0.5028'
   [value] verdict.json:metrics/loss.corruptRate.pairs[13].p_value: expected '0.501' vs actual '0.485'
   [value] verdict.json:metrics/loss.corruptRate.pairs[19].p_value: expected '0.4916' vs actual '0.4882'
   [value] verdict.json:metrics/loss.corruptRate.pairs[24].p_value: expected '0.5106' vs actual '0.493'
   [value] verdict.json:metrics/loss.corruptRate.pairs[31].p_value: expected '0.5016' vs actual '0.4882'
   [value] verdict.json:metrics/persist.responseRate.pairs[12].p_equivalence: expected '0.2625' vs actual '0.2593'
   [value] verdict.json:metrics/persist.responseRate.pairs[18].p_equivalence: expected '0.2654' vs actual '0.2627'
   [value] verdict.json:metrics/persist.responseRate.pairs[33].p_equivalence: expected '0.2563' vs actual '0.2605'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[8].p_value: expected '0.543' vs actual '0.508'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[9].p_value: expected '0.515' vs actual '0.5456'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[10].p_value: expected '0.512' vs actual '0.5226'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[11].p_value: expected '0.5156' vs actual '0.5254'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[13].p_value: expected '0.51' vs actual '0.5296'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[17].p_value: expected '0.513' vs actual '0.5156'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[19].p_value: expected '0.5288' vs actual '0.513'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[21].p_value: expected '0.5318' vs actual '0.5246'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[22].p_value: expected '0.5164' vs actual '0.5244'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[24].p_value: expected '0.508' vs actual '0.5116'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[26].p_value: expected '0.5216' vs actual '0.5062'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[27].p_value: expected '0.5162' vs actual '0.5106'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[28].p_value: expected '0.5162' vs actual '0.5388'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[30].p_value: expected '0.5186' vs actual '0.5176'
   [value] verdict.json:metrics/mem.privateBytes.p50.pairs[31].p_value: expected '0.5198' vs actual '0.5258'
   [value] verdict.json:metrics/cpu.proxy.vcpuPct.pairs[8].p_value: expected '0.5128' vs actual '0.504'
   [value] verdict.json:metrics/cpu.proxy.vcpuPct.pairs[11].p_value: expected '0.5256' vs actual '0.503'
   [value] verdict.json:metrics/cpu.proxy.vcpuPct.pairs[12].p_value: expected '0.5404' vs actual '0.5268'
   [value] verdict.json:metrics/cpu.proxy.vcpuPct.pairs[31].p_value: expected '0.529' vs actual '0.525'
compared 51 slice(s): 10 differ(ent), 0 missing
differences: 0 structure, 75 value, 0 missing
rc=1: every slice exists but 10 differ(s)
rc=1
```

### 逐字段计数（脚本复核，非人工数）

```bash
rg -o '^\s+\[value\] \S+' /tmp/wf-oracle/s4-prerelax.txt | wc -l          # 75
rg -o '^\s+\[(structure|missing)\]' /tmp/wf-oracle/s4-prerelax.txt | wc -l # 0
rg '^\s+\[' /tmp/wf-oracle/s4-prerelax.txt | rg -c 'tables\.md'              # 0
```

| 项 | 实测 | 与 spike（`00-rng-swap-spike.md`）对比 |
|---|---|---|
| `compared` 行 | `51 slice(s): 10 differ(ent), 0 missing` | 10 个切片变红，一致 |
| `differences` 行 | `0 structure, 75 value, 0 missing` | 75 条 `[value]`、0 结构、0 缺失，一致 |
| `[structure]` / `[missing]` 明细行 | 0 / 0（`report.count(MISSING)` 即 `differences` 里的 `0 missing`） | 一致 |
| `[value]` 明细行 | 75 | 一致 |
| 字段分布 | `p_value` **58**、`p_equivalence` **10**、`holm_p_equivalence` **7** | 58/10/7，逐项一致 |
| 未出现的字段 | `holm_p_value` **0**（金标里 212 处全饱和在 `1.0`）、`ci95[0/1]` **0** | 与 SHOULD-FIX 3 / D1 的断言一致：冻结树 3 pass，分位落在最小原子 |
| 路径前缀 | 75/75 以 `verdict.json:metrics/` 开头（`rg -v` 零命中） | 一致 |
| `tables.md` 出现在差异行 | **0**（`tables.md` 只出现在 18 条 `… : equal` 行里） | 一致（"表 0 切片变化"） |
| 触及的 metric 成员 | 10 个：`mem.privateBytes.p50` 15、`lat.tcp_rtt.p99` 11、`lat.tcp_rtt.p50` 11、`lat.udp_rtt.p50` 10、`latload.tcp_rtt.p99` 7、`latload.tcp_rtt.p50` 7、`loss.corruptRate` 4、`cpu.proxy.vcpuPct` 4、`persist.responseRate` 3、`loss.lossRate` 3 | 与「10 个切片」自洽 |

结论：换 RNG 之后**未放宽**的 oracle 报出的差异面与 spike 逐项对齐（75 = 58+10+7），
verdict 字符串、`estimate`/`median`/`iqr`、全部表格单元格与 key 集**零变化**。
`ci95` 与 `holm_p_value` 在冻结树上观察不到移动，放宽里仍列入（前瞻，D8）。

完整的逐条输出另存：`/tmp/wf-oracle/s4-prerelax.txt`（148 行）；路径清单 `/tmp/wf-oracle/s4-paths.txt`。

---

# C1 · S5.0/S5/S6 实施笔记

**日期**：2026-10-09
**范围**：S5.0（舍入规则对齐）、S4 的收尾（RNG 换 `System.Random`、`ComparisonSeed` 新家）、
S5（窄放宽 + 冻结物退役 + D6 发布文本）、S6（门禁）。未触碰 `research/01` §5 名单、
`--mode byte` 的红面、README（见 §7）。

## 1. S5.0：两条舍入规则对齐（`%.3e` 格改半偶）

`Tables/GateValidity.cs:263` 的 `value.ToString("0.000e+00")` → `VerbatimNumber.Exponential(value.Value, 3)`，
新成员实现在 `Json/VerbatimNumber.cs`：`ToString("E3")` → 指数去掉前导零、补足两位、`E` 转小写；
非有限值（`NaN`/`Infinity`）原样返回（实测 `E3` 与自定义格式对这三个值逐字相同）。

**实测（临时探针 `/tmp/e3probe`，209 224 个 double：14 个尾数 × 621 个十进指数 + 20 万随机位型 + 中点/边界值）**：

| 对照 | 与 Python `%.3e` 不一致 |
|---|---|
| `E3` 截指数 + 小写（新实现） | **0 / 209 224** |
| `0.000e+00`（S1 的旧实现） | **3 828 / 209 224** |

旧实现错的全是十进制中点/缩放误差样本（`1.0625`→`1.063`、`1234.5`→`1.235e+03`、
`9.9995e-308`→`1.000e-307`），即「自定义格式串按其缩放后的十进制远离零舍入」；
`E3` 与 `F` 一样按精确二进制值半偶，与 Python 的 `%.3e` 逐条相同 —— 所以两条规则现在是一条。

`AnalyzerNumberGoldenTests` 新增三条（+10 个用例）：8 个四位有效数字中点（4 上 4 下）、
字形表（`8.000e-03`/`1.000e-06`/`1.234e+03`/`1.000e+100`/`0.000e+00`/`-0.000e+00`/`NaN`/`Infinity`）、
以及负控制（`0.000e+00` 会让恰好 4 个中点移动）。

判据：全批次 oracle rc=0；`rg -c '1e-06' /tmp/wf-oracle/cs/tables.md` = **27**、
`rg -c '8\.000e-03'` = **27**（与金标一致）。

## 2. S5：1e-2 装不下这批噪声，实测后改为 5e-2（父 session 已裁定）

**先看见**：未放宽的 oracle 报 `10 differ(ent) / 0 structure, 75 value, 0 missing`（§上文全文）。
**再解释**：按 AC3 原定的绝对 `1e-2` 放宽后，仍有 **27 条** `[value]`（rc=1）：

| 字段 | 1e-2 装不下的条数 | 该字段 75 条中的最大位移 |
|---|---|---|
| `p_value` | 23 | 0.035 |
| `p_equivalence` | 2 | 0.0129 |
| `holm_p_equivalence` | 2 | 0.0150 |

27 条**全部**满足六叶正则、**全部**以 `verdict.json:metrics/` 开头（脚本复核：非 metric 路径 0 条、
不匹配正则的路径 0 条）。`1e-2` 的出处是「字段以 `count/10000` 存储、粒度 1e-4」，
那是**打印粒度**，不是**移动幅度**。

**移动幅度的直接测量**（同一二进制、同一棵树，只换 `--seed 20261006` → `20261007`）：

| 字段 | 移动的叶子 | 最大位移 |
|---|---|---|
| `p_value` | 59 / 192 | 0.0304 |
| `p_equivalence` | 11 / 182 | 0.0143 |
| `holm_p_equivalence` | 7 / 212 | 0.0047 |
| `holm_p_value` | 0 / 212 | 0 |
| `ci95[0]` / `ci95[1]` | 0 | 0 |

`p_value` 位移的标准差 **0.0064**（n=192）。即这份量对**它自己**都只能复现到 ~3e-2，
`1e-2` 是它自身重复性以下的一条线。**裁定（父 session 批准）**：p 值四件套用绝对 **5e-2**
（= 7.8 个实测标准差、实测最坏位移的 1.4 倍，远小于真实变化会带来的 ≥0.1）；
`ci95[0]/[1]` 保持 `max(1e-2, 1e-2·abs(expected))`（冻结树上它们一动不动，见上表）。
**路径与叶子集合不变**（严格六叶、精确点分匹配）：表格单元格、`estimate`/`median`/`iqr`、
verdict 字符串、key 集一律照旧比对。放宽后全批次 `0 structure, 0 value, 0 missing`，rc=0。

改动落点：`oracle-diff.py` 的 module docstring、`STATISTICAL_PATH` 三常量 +
`statistical_tolerance()`、`compare_json` 数字分支（`statistical_tolerance` 返回 `None` 时回落原有
`numbers_equivalent`）。文档同步：`verification/FROZEN.md` 新增 §6（含两张实测表），
`.trellis/spec/backend/measurement-tooling.md` 的 differ contract 段；
AC3/D8 的文字仍写 1e-2，需父 session 改成 5e-2（两份记录都在 `.trellis/`，本 dispatch 不改）。

## 3. S4 收尾：`System.Random` 与 `Stats/ComparisonSeed.cs`

- `Stats/BootstrapPair.cs`：`new CpRandom(seed)` → `new Random(seed)`；三个 `CpRandom random` 参数
  与 `Resample` 的形参改 `Random`；`values[(int)random.RandBelow(n)]` → `values[random.Next(n)]`
  （`Next` 已是 `int`，无 S1905 强转）。类摘要里「the bootstrap the reference draws」改为
  「every interval and p-value is drawn from」，并新增一段写明**resampling 单位/配对规则/种子派生
  是契约，序列不是**。
- `Stats/ComparisonSeed.cs`（新，`public static`）：`StableHash`/`DeriveSeed` 原样搬来（含 SHA-256
  大端、`% 1000000`），文档改写成我们自己的 `--seed` 契约；`CpRandom.cs` 里删掉这两个成员与
  三条只服务它们的 `using`。
- 调用点：`Checks/ControlDrift.cs:197`、`Metrics/MetricComparisons.cs:225` 与 `:22` 的 cref 全部
  改 `ComparisonSeed.*`。
- `tests/.../AnalyzerRandomGoldenTests.cs` → **`AnalyzerComparisonSeedTests.cs`**：只留
  `TheDerivedSeedIsTheStableHashFormula`（`1973867047u` / `867047u` / `DeriveSeed(20261006, key) == 21128053`
  三条原值），三个 CPython 向量测试删除。

## 4. S5 冻结物退役

删除：`Stats/CpRandom.cs`、`verification/golden/{cp-random-vectors,py-number-vectors,py-json-vectors}.json`、
`verification/synthetic/make_cp_vectors.py`（删前 `rg` 确认读者只剩文档；见下）。
测试同步：`AnalyzerNumberGoldenTests` 删三个读向量表的方法（230 `fixed` / 135 `general` / 36 `repr`
的全量扫描随之退役，就地保留中点表、形状表、Cell 回退表）；`AnalyzerJsonGoldenTests` 删
`EveryGoldenDocumentIsReproduced`（12 个文档的向量扫描）与只服务它的
`Render`/`HoldsFloat`/`IsFloatLiteral`，就地保留转义/顺序/层级/标量断言；
`RepoPaths.AnalyzerGolden` 确认零调用者后删除。`VerbatimNumber` 类注释不再点名向量表。
文档：`FROZEN.md` §1.1 改写为「退役的向量表」（含四个历史 sha256 与「可从 git 历史恢复」），
`measurement-tooling.md` 删掉向量表那一行。

## 5. D6 发布文本

- `Loading/RunLoader.cs`：`ReferenceName` → `FailureName`，四个名字改
  `JsonException`/`UnauthorizedAccessException`/`FileNotFoundException`/`DirectoryNotFoundException`，
  兜底 `IOException`（`JsonReader.ReadFile` 只可能抛 `JsonException`、`UnauthorizedAccessException`
  与 `IOException` 族，所以兜底名是准确的；`FileNotFound` 与 `DirectoryNotFound` 拆成两条，
  合并会把目录缺失印成 `FileNotFoundException`）。相关叙述（`ReadJsonFile` 备注、`JsonReader.ReadFile`
  摘要）同步改写。
- `Model/JsonText.cs`：`None`/`True`/`False` → `null`/`true`/`false`。
  ⚠️ **连带发现**：`Tables/TableEnvironment.cs:342` 的 `Sorted()` 用渲染后的哨兵
  `!string.Equals(value, "None")` 过滤缺失成员；只改 `JsonText` 会让过滤**静默失效**（缺失值不再被丢弃）。
  已改为 `JsonText.Of(null)`（行为不变，且以后不会再随哨兵拼写漂移）。金标 `None`/`True`/`False`
  各 0 命中，改后全批次 oracle 仍 rc=0。

## 6. S6 门禁（逐条实跑）

```
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py
    → compared 51 slice(s): 0 differ(ent), 0 missing
      differences: 0 structure, 0 value, 0 missing / rc=0
python3 benchmarks/WinForward.E2E/scripts/check-fairness.py
    → 14 个 guard 全 PASS / rc=0
python3 benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py
    → 5 棵边界树全 PASS（含 12 个负控制）/ rc=0
python3 .../verification/check-fixture-drift.py --tree /tmp/wf-synth
    → rc=1（**既有**问题：脚本内硬编码 `.trellis/tasks/10-07-e2e-harness-refactor/research`，
      该任务已归档）。用 `--root .trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research`
      实跑：`contract: 603 path(s) / fixture: 605 path(s) / fixture drift: none (both directions empty)` / rc=0。
      与 `check-readme-contract.py` 同源（`measurement-tooling.md` 已登记「脚本里的死路径」）。
dotnet build WinForward.slnx -c Release      → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test tests/WinForward.E2E.Tests -c Release → Passed! Failed: 0, Passed: 364, Total: 364
rg -i 'cpython' benchmarks/WinForward.E2E*
    → 2 条：`Stats/DescriptiveStats.cs:119`（Neumaier 求和，§5「不许动」+ AC5 保护，见 §7）
      与 `verification/FROZEN.md`（冻结参考叙述，允许）
```

`--mode byte` 状态：`25 differ(ent)` / `0 structure, 25 value, 0 missing` / rc=1 —— 与 β 登记的
25 个切片**完全相同**（`tables.md:14` + 24 个 `verdict.json` 切片，清单见 `/tmp/wf-oracle/byte-s5.txt`）。
换 RNG 只让**已经在红面里**的切片内部再多变几个字符，没有新增切片；`--mode byte` 仍非批次判据（D21 §2），
未为它改任何语义。

测试数：361（基线）→ 371（S5.0 后 +10）→ **364**（S5 删 7 个向量驱动测试）。
`effective-lines.py` 对全部改动的 `.cs` 文件 rc=0。

## 7. 有意未做 / 交给父任务（C2/C3/C4）

1. **AC7 的 README 措辞**：`Analysis/README.md:49` 现在写的是「`--seed` … the per-pair seeds are
   derived from it deterministically」——**没有**承诺与 Python 逐字一致，`rg -i 'cpython|逐字'` 0 命中，
   所以 AC7 的证据命令本来就过。要不要把「同一 .NET 运行时下可复现」这句**显式**写进去，属于
   PRD 里划给 **C4** 的 README 口径（「只登记、不动手」），本 dispatch 未动。README:124 的 "verbatim"
   说的是 ProxiFyre 源码，与本任务无关。
2. **`Stats/DescriptiveStats.cs:119` 的 "the way CPython's `sum` computes it"**：这是 `research/01` §5
   第 12 条「不许动」的 Neumaier 求和，注释解释的正是「为什么不能用 `total += value`」；删掉 CPython
   字样会丢掉这条理由。留原文，登记给 C4 判断要不要换个说法（**不要**只删词）。
3. **测试文件名的 "Golden" 已名不副实**：`AnalyzerNumberGoldenTests`/`AnalyzerJsonGoldenTests` 现在
   全部是就地断言、不再读金标表。改名属于 C2 的命名清理，未动。
4. **退役的向量扫描是覆盖率下降**：`fixed` 230 / `general` 135 / `repr` 36 / json 12 文档 /
   702 次 RNG 抽样的全量扫描随表退役，留下的是就地表（中点、形状、回退、转义、层级、标量）。
   若父任务认为需要，可把其中若干条做成内联表——那属于扩范围，未做。
5. **`check-fixture-drift.py` 的死路径**（§6）：修法在脚本里（archive-aware 查找，参照
   `RepoPaths.cs:49-71`），属 C3/C4 的脚本搬家与门禁修复范围。
6. **`__pycache__`**：跑门禁会重新生成 `benchmarks/WinForward.E2E/scripts/__pycache__`
   （`check-fixture-drift.py` 导入 `jsonl_paths`）。未跟踪、被根 `.gitignore` 覆盖，收尾时已删。
7. `Report.show()` 的 160 字符截断让 byte 模式的大切片差异只印首 160 字符——既有行为，未动。

## 8. 预提交门禁的五条 finding（2026-10-09，全部采纳为修复，无豁免）

| 位置 | 规则 | 处置 |
|---|---|---|
| `Tables/TableEnvironment.cs:342` | MA0003 | `JsonText.Of(null)` → `JsonText.Of(element: null)`（命名实参） |
| `Model/JsonText.cs:8` ×3 | MA0154 | 类注释里的 `<c>true</c>/<c>false</c>/<c>null</c>` → `<see langword="true"/>` 等三个（`RunLoader.cs` 里已有同样写法） |
| `Json/VerbatimNumber.cs:72` | RCS1267 | **采纳**：`string.Concat(...)` → 插值串 `$"{text.AsSpan(0, marker)}e{sign}{padded}"`。C# 的插值处理器有 `AppendFormatted(ReadOnlySpan<char>)` 重载，段是逐段追加、没有中间串，也不走当前区域性格式化（三段都是 `string`/span 原样追加） |

**等价性证据**：三条修完之后重跑分析器，`tables.md` 与 `verdict.json` 与修前**逐字节相同**
（`b3a4c49b…` / `02a1b82b…`，`cmp` 两文件均 identical）；
`oracle-diff.py` 全批次 rc=0（`0 structure, 0 value, 0 missing`）；
`dotnet build …Analysis.csproj -c Release` 0 警告；E2E 测试 364/364 绿。
没有为该规则加任何 `#pragma`/`.editorconfig` 豁免。

---

# C1 · check 复核（2026-10-09，独立复跑；不提交）

**范围**：`git diff f992370..HEAD`（a402289 / 90bc922 / 9310eda）对照 `prd.md` AC1–AC7、
`../10-09-e2e-csharp-native-cleanup/research/01-python-emulation.md` §5 的 18+1 条、
`design-decisions.md` 的 D1/D6/D8/D10/D11。

## 1. 本次独立复跑的实测（与上文的记录值对照）

| 项 | 记录值 | 本次实测 |
|---|---|---|
| 语义 oracle 全批次 | rc=0 | `compared 51 slice(s): 0 differ(ent), 0 missing` / rc=0 |
| **未放宽**的差异（把 `STATISTICAL_PATH` 改成永不匹配） | 75 = 58/10/7，0 structure、0 missing、10 切片、0 条 tables.md | **逐项相同**（`/tmp/unrelaxed.txt`） |
| `1e-2` 放宽后残留（副本改 `STATISTICAL_P_ABSOLUTE`） | 27 | **27** |
| `5e-2` / 界改成实测最坏位移 | rc=0 | 0.05 → rc=0；0.035 → rc=0；0.0305 → 残留 2；0.01 → 残留 27 |
| 只换 `--seed`（20261006→20261007） | 77 条、p_value max 0.0304、SD 0.0064 | **77**（59/11/7）、max 0.03040、SD(192) 0.00638 |
| 换 RNG 的 p_value SD | notes 写 0.008；文档写 “SD 6.4e-3 over the 192” | 用 notes 的 75 条清单重算：SD(192) **0.00629**、SD(移动的 58 条) 0.00749、mean(192) 0.00313 → 两种口径各自成立 |
| `1e-06` / `8.000e-03` 格 | 各 27 | 各 **27**（金标与产出逐字相同） |
| `--mode byte` | HEAD 25（24 verdict + `tables.md:14`） | **25** ✓；`f992370` = **1**（只有 `verdict.json:ledger`）、`a402289` = **2**（+ `tables.md:14`）→ commit 的「本步之前 2」指 S0/S1 之后，成立 |
| E2E 测试 | 364 | **364 passed**（`AnalyzerComparisonSeedTests` 单跑 1/1） |
| fairness / boundary | 全 PASS | fairness 14 guard PASS / rc=0；boundary 全部树 rc=0、**0 个 FAIL**（末棵 `--zero-denominator` 7 guard） |
| §5 的 154 行 11 格 | 154 | 金标与产出各 **154**（全文件 187 也相同；分隔行形状相同） |
| 退役向量表的条目数 | 230 / 135 / 36 / 12 / 702 | fixed 230、general 135、repr **36 finite + 3 named = 39**、json 12、random 18 seeds × 8 words + 198 bits + 504 ranges = 702 |
| 退役向量 vs 新实现（从 git 历史取回向量表，用临时控制台程序驱动 `VerbatimNumber`） | “`F` 230/230”“`%.*g` 135/135” | `Fixed` **230/230**、`General` **135/135**、`repr` 19/39（按设计变化，oracle 按数值比对） |

## 2. 需要更正的记录（已就地改文档，history 不改）

1. **`holm_p_value` 不是「212 处全饱和在 1.0」**（本文件 S4 表末行、`FROZEN.md` §6）。
   实测 `golden/py-verdict.json`：**41 处 `0.0` + 171 处 `1.0`**（41 个 0.0 与 `p_value` 的 41 个 0.0
   是同一批 pair）。「它不动」的结论不变，但理由只对 171 个成立 → `oracle-diff.py` docstring 与
   `FROZEN.md` §6 的括号已补上真实原因。
2. **退役的 `repr` 表是 39 条（36 finite + 3 named），不是 36 条**；`FROZEN.md` §1.1 已改写成
   “`repr` 36 finite and 3 named”。commit message 的 “36 repr” 与旧测试的 `seen >= 36` 都只覆盖
   finite 部分，历史提交不改。
3. `oracle-diff.py` 模块 docstring 原写 “a difference of **6.4e-3 on average** and 3.5e-2 at worst”，
   把标准差写成了平均值（同段后文自己写的是 standard deviation）→ 已改为 “whose standard deviation
   is 6.4e-3 and whose worst case is 3.5e-2”。实测 mean(192) = 3.1e-3、RMS 7.0e-3。

**未改、留给父 session 裁定**：`p_equivalence` / `holm_p_equivalence` 是 `max(两个单边份额)`
（`BootstrapPair.cs` 的 `Math.Max(belowEdge, aboveEdge)`），**没有** “doubled by the two-sided rule”；
只有 `p_value`/`holm_p_value` 是 `Math.Min(1, 2·Math.Min(below, above))`。三处文档（脚本 docstring、
`FROZEN.md` §6、`measurement-tooling.md`）都把四件套一起说成 doubled。5e-2 对等价那两件是**更宽**的界
（实测最大位移：`p_value` 0.0304、`p_equivalence` 0.0143、`holm_p_equivalence` 0.0047），所以判据不受
影响，只是理由句不准；改它要动三份文档，属 C4 的口径范围。

## 3. 覆盖率（S5 删表后的净损失：已确认，未做）

- `AnalyzerJsonGoldenTests` 的 12 文档向量扫描删除后，**没有任何测试逐字比对一份完整文档**；剩下的就地
  断言覆盖转义集、插入序、空容器、层级与标量。语义 oracle 会解码 JSON，所以**看不见缩进/冒号空格的
  漂移**；只有 `--mode byte` 会看见，而它基线本来就红（D21 §2）。属 D7 已登记的有意收窄。
- `AnalyzerNumberGoldenTests` 删掉的三张扫描表里，`Fixed`/`General` 两条按新契约仍然全绿（见 §1 末行），
  `repr` 那条按设计不再适用；就地表覆盖 `digits ∈ {0,3}` 与 8 个指数形状，**digits 1/2/6 等不再被扫**。
- 任务书描述的 `AnalyzerJsonGoldenTests`「一例改 `JsonElement.DeepEquals` + `Assert.Equal(1, floats)`」
  在最终 HEAD **不存在**：那是 S2 的中间态（本文件 §4 记的就是它），S5 把整个
  `EveryGoldenDocumentIsReproduced` 删掉了。

## 4. 环境陷阱（可复用的知识）

`oracle-diff.py` 每次运行都 `rmtree` 并重解 `/tmp/wf-synth`，两侧都写 `/tmp/wf-oracle/cs`。
**两个 oracle 并发（或任何别的进程在读那棵树）会互相截断**：症状是 §14.1 的 records 掉成
`6376 / 0 / 0`、`unparsable` 仍为 0，并冒出 `ledger-connection-mismatch`、`ledger-datagram-mismatch`、
`empty-target-ledger` 的 measurement-caveat（findings 从 23 条变 39 条，`verdict.json:findings` 报
`23 element(s)` vs `39 element(s)`）。
本次 check 在 a402289/90bc922 的 worktree 逐提交复跑时撞到过一次（`28 structure, 751 value, 118 missing`，
rc=2）；**同一 worktree、同一脚本重跑即 rc=0**，HEAD 的二进制在静态树上连跑 20 次全部
`7306/7306/7306`，90bc922 的二进制手工复跑 2 次也都是 `7306/7306/7306`。结论：那次红是并发写
`/tmp/wf-synth` 的假象，**不是 90bc922 的回归**；**跑 oracle 必须串行**。

