# 技术设计：E2E 的 C# 化清理

> 配套 `prd.md`（需求与验收）、`research/01`–`04`（逐条证据）、`research/00`（RNG 实测）。
> 本文件只回答"怎么改、为什么这么改、改了怎么知道没坏"。

## 0. 一条贯穿全局的分类原则

调研把"看着像 Python"的代码分成**两类**，它们的处置方式完全相反，混淆这两类是本次唯一真正的风险：

| 类 | 判据 | 处置 | 例子 |
|---|---|---|---|
| **可换实现** | 值/结构在容差或语义等价下不变；门禁看不见字形 | 直接用 .NET 常规写法替换，删掉仿真代码 | CPython RNG、`repr`/`%.*g` 的文本、`json.dumps` 的转义 |
| **实现即契约** | 输出被**逐字/逐格/逐字节**比对，或数据语义依赖它 | **行为一行不改**，最多改名字与注释 | `PosixPathText` 的词法拼写、`PythonGlob` 的三条发现规则、Neumaier 补偿求和、表格形状与空单元格规则、UTF-8 无 BOM |

"实现即契约"的完整清单在 `research/01` §5（18 条），实施时整段进 `implement.jsonl`。
**判据的边界由门禁定义**：`oracle-diff.py` 对数字按容差、对文本按 `text_identical`、
对结构逐 key 比对（`oracle-diff.py:961-980,997-999`），所以"字形"和"语义"的区别不是审美问题，
而是这份脚本里的具体分支。

## 1. C1 · Python 仿真收尾

### 1.1 步骤与每步的判据（顺序即依赖）

| 步 | 内容 | 判据 |
|---|---|---|
| S0 | 零输出风险项：`Truthy`→`IsTrue`（5 处）、三份 `Int(double)` 合一、`DatagramCount` 去掉整数零分支、两处 pragma 注释、叙事改名、删两个 `__pycache__` | 全批次 oracle rc=0；`dotnet test` 绿 |
| S1 | `PythonExponential.cs`（21 行）→ 一条格式串（唯一调用者 `Tables/GateValidity.cs:263`） | `oracle-diff.py --batch 2,4` rc=0 |
| S2 | `VerbatimJson`：**不换 `Utf8JsonWriter`**（评审证伪了 `UnsafeRelaxedJsonEscaping`：金标非 ASCII 字节 0、`\uXXXX` 360、还有 22 个字面 `+`，两个内建编码器各错一半）；改为把转义委托给一个显式/自定义 `JavaScriptEncoder`（放过 `0x20..0x7E`，其余 `\uXXXX`），并**同步更新** `AnalyzerJsonGoldenTests` 的 7 个方法 | 语义全批次 rc=0；`--mode byte` 会红 → 登记；`json.load()` 能解析产物 |
| S3 | `VerbatimNumber`：先换 `Fixed`/`Json`，再用格式串替掉 `General`；**保留 `%.3g`/`%.3e` 回退** | `--batch 1c,2,3,4,5` rc=0；`rg -c '1e-06'` 在 tables 里仍为 27 |
| S4 | `BootstrapPair` 换 `System.Random`（4 行，见 `research/00`）；`StableHash`/`DeriveSeed` 摘出到新文件保留；**先不删 `CpRandom.cs`** | 放宽前 oracle 应报**约 75 条 `[value]`、0 `[structure]`、0 missing**——与 spike 数字对上才算过 |
| S5 | oracle 窄放宽落地 + 删 `CpRandom.cs`、`AnalyzerRandomGoldenTests.cs`、三张向量表与 `make_cp_vectors.py`；更新 `FROZEN.md:43-63`、`measurement-tooling.md:125` | 全批次 rc=0；`dotnet test` 绿；`FROZEN.md` 无指向已删文件的哈希行 |
| S6 | 全门禁重跑（见 §5） | AC8 |

顺序的理由：S0 不碰输出 → S1 最小可验证 → S2/S3 各自可独立回滚 →
**S4 是唯一会动数值的一步，必须排在 S5 的放宽之前**，这样"75 条 p 值差异"是先被看见、再被解释的，
而不是放宽之后无迹可寻。

### 1.2 oracle 窄放宽的精确规则（D1 待裁）

**问题**：p 值与 bootstrap 分位是重采样噪声，`oracle-diff.py` 现在的容差是"参考打印精度的 1 个末位
单位"，而数字是 `repr` 全精度打印的，所以任何 RNG 变化都必然超出容差。

**规则**（在 `oracle-diff.py` 的 `Comparer.compare_json` 数字分支 `:968-972` 里按**路径**分流；
`metrics` 不在 `PINNED_BLOCKS`，`compare_members` 碰不到它；数字是 `VerdictNumber`）：

- path 是**点分**的（metric 成员名自带点）：`verdict.json:metrics/lat.tcp_rtt.p50.pairs[8].p_value`，
  所以匹配用 `re.fullmatch(r"verdict\.json:metrics/.+\.pairs\[\d+\]\.(p_value|holm_p_value|p_equivalence|holm_p_equivalence|ci95\[[01]\])", path)`。
- **容差分两类**：p 值四件套是 `count/10000`（粒度 `1e-4`）→ 绝对 `1e-2`；
  `ci95[0]`/`ci95[1]` 是**度量尺度上的区间**（实测跨度 0.094–0.8）→ 相对容差
  `max(1e-2, 1e-2·abs(expected))`。
- 其余一切照旧：`estimate`、`median`、`iqr`、`tables.md` 的每一格、所有 verdict 字符串
  （`raw_verdict`/`holm_verdict`/`raw_reason`/`holm_reason`，它们是字符串，走 `text_identical`）、
  所有 key 集与类型。**理由是实测的**：换 RNG 后这些一条都没动
  （`research/00` 的 75 条差异按字段分组：58 + 10 + 7，全部落在 p 值上）。

**为什么 ci95 也要纳入**：冻结树每行只有 3 个 pass，重采样估计只有 3³ 种取值，2.5 % 分位落在
最小原子上（P≈0.259），所以端点稳定；报告 01 附录 A 的合成实验显示 5–10 pass 时 24 次试验 0 次移动、
**15 pass 时 6 次里 4 次移动**。只放宽 p 值等于给真实 campaign 埋一颗必红的雷。

**放宽不是免责**：verdict 是字符串，仍逐字比对——p 值越过阈值导致的判定翻转**一定会被抓住**，
那正是要看的信号。

### 1.3 `--seed` 与 `System.Random`

`System.Random(seed)` 只保证同一运行时内可复现（跨 .NET 版本/架构无保证）。
处理：README 里凡有"同 seed 逐字复现"的承诺一律改写为"同一运行时下可复现"；
是否加一条固定 seed 的 tripwire 测试由 D-decision 定（成本 5 行，收益是"运行时换了会有人告诉"）。

### 1.4 需用户表态的两处发布文本

`RunLoader.ReferenceName` 的 CPython 异常名与 `JsonText` 的 `None`/`True`/`False`：
金标 0 命中 ⇒ 改了不会红，但真实 campaign 会印给人看。属于 D6。

## 2. C2 · 命名与结构

### 2.1 房规（**用户 2026-10-09 的口径**，与 `research/02` Part A 从 `src/` 反推的结果一致）

> 两字母缩写全部大写，例如 `IO`、`IP`、`OS`（`Id` 是例外）；更多字母的缩写视作一个单词，
> 例如 `Html`、`Json`；`IPv4`/`IPv6` 里 `IP` 大写、`v` 小写。

E2E 三树的自查（实测）：

| 规则 | E2E 现状 |
|---|---|
| `IP`/`IPv4`/`IPv6` | **已合规**：`IPEndPoint`/`IPAddress`（`Target/DnsServer.cs:159`、`Target/SourceCensus.cs:147`），`IPv4-mapped IPv6`（`SourceCensus.cs:169`） |
| `Id` 不写 `ID` | **已合规**：三树里的 `ID` 只出现在 `IDLE`（臂名，本来就是全大写）与 `IDisposable`（BCL） |
| ≥3 字母 Pascal | **已合规**（`Tcp`/`Udp`/`Dns`/`Cpu`/`Json`） |
| **两字母全大写** | **3 处越界**：`SocketIo` → `SocketIO`、`LaneReceiveKind.IoError` → `IOError`、`ArmKeys.Run.OsDescription` → `OSDescription` |

`OsDescription` 是**成员**改名，常量**值** `"osDescription"` 冻结不动
（`check-readme-contract.py:204` 只用常量值推路径）。
2. 文件名 = 主类型名；partial 分片写 `Type.Topic.cs`（`ArmKeys.*.cs`、`TcpProxyCoordinator.Diagnostics.cs`）。
3. **多值返回用 record，不用命名元组**：`src/` 0 个，`Analysis` 39 个。
4. `<summary>` 讲整体，不做逐属性 `<param>`：`src/` 138 个文件只有 1 个 `<param>`，E2E 四个树有 157 个。
5. 400 有效行，**到 375 就该拆**（先例：`ConfigurationModels.cs`）。
6. 注释英文；`(D20.5)` 这类决策号是房规，迁移批次号（`E2-d`）不是。

### 2.2 改动清单（按收益/风险排序）

| 优先 | 改动 | 规模 |
|---|---|---|
| 1 | 21 个 `(T? Value, string? Reason)` 收成一个 `Reading<T>` 记录 | 21 处调用点，最大可读性收益 |
| 2 | 拆 `Analysis/Findings/LedgerViews.cs`（385 有效行 / 4 类型） | 拆分，纯移动 |
| 3 | 拆 `Client/ArmContext.cs`（7 个无关类型；`README.md:37` 已描述错） | 拆分 + 改 README 一行 |
| 4 | 三个缩写 + 一个测试方法名：`SocketIo`→`SocketIO`、`LaneReceiveKind.IoError`→`IOError`、`ArmKeys.Run.OsDescription`→`OSDescription`（**成员**改名不影响输出，常量**值** `"osDescription"` 冻结） | 41 处行、26 处引用，机械 |
| 5 | `LedgerFindings.{ClientCounts,DnsTotals}.cs` 两处 `git mv` | 0 内容变化 |
| 6 | 清迁移 vocabulary（`AnalysisOptions.cs:6`、`ContractRegistry.cs:113,136`、`ContractShapeTests.cs:15,30` + 11 行批次号）+ `Int`×3 / `Repeated`×3 去重 | 少量 |
| 7 | `TableArmDenominator.cs`、`JsonRate.cs`、`JsonPerSecond.cs` 三处 `git mv` | 机械 |
| 8 | `Dedicated` → `DedicatedThread`（形容词不当类型名，18 处） | 机械 |
| 9 | 拆 `LaneTestDoubles.cs`（10 类型 / 359 有效行） | 拆分 |
| 10 | 4 个未用 using + `IsTestProject` 的处置（D4） | 见 §4.3 |

### 2.3 改名禁区（`research/02` 的"不许动"清单）

- `.editorconfig:238,241,244,247` 用**精确文件路径** glob 了四个 Analysis 文件
  （`CampaignQueries.cs`、`GateValidity.cs`、`GateFlow.cs`、`LedgerFindings.cs`）——C2 不得改这些文件名。
  另：S1244 的三条压制是"Python 精确浮点比较"的产物，C1 删掉那些比较后要复核是否还需要。
- `ArmKeys` 的常量**值**、15 个 CLI flag、`tables.md`/`verdict.json` 的结构、`verification/**`、
  C1 留在 `CpRandom`/`Verbatim*` 上的 `public`（D20.6）。
- 3 个测试把源码路径当字符串读；`README.md:30-56`、`:506-514` 点名字段/文件 → 改名要同步。

## 3. C3 · 脚本

### 3.1 删除（有调用者证据：只有文档行）

`scripts/contract-inventory.py`（E1–E3 迁移工具；**其两张输出表是活输入，必须留下**）、
`scripts/normalize-pattern-hits.py`（D16.2 已完成的检查）、两个 `__pycache__`（8 个 `.pyc`，未跟踪）。
删除前确认：表被 `check-fixture-drift.py:65`、`check-readme-contract.py:47`、
`make_tree.py:23`（docstring）、`compare-records.py:836`（可选参数）消费 → 表搬家时同步改引用。

### 3.2 迁移

| 现在 | 去处 | 理由 |
|---|---|---|
| `WinForward.E2E/scripts/effective-lines.py` | 新建根级 `tools/` | 400 行是**全解决方案**规则（`directory-structure.md:98,100,103`），还被用来跑 `WinForward.Benchmarks`（`benchmarks/README.md:433`）|
| `WinForward.E2E/scripts/oracle-diff.py` | `WinForward.E2E.Analysis/verification/` | 全部输入是 `verification/**` + 分析器二进制；`measurement-tooling.md:127` 已称它"外来" |
| `WinForward.E2E/scripts/check-fairness.py` | 同上 | 同上；`row-profiles.json:4` 逐字点名它，要同步 |
| 两张契约表 + 归档路径 | `verification/`（或让查找兼容归档路径） | 一并修好两个坏掉的检查器 |

`jsonl_paths.py`、`compare-records.py`、`cli-snapshots.py`、`publish.sh`、`selftest.sh`、`orchestrator.ps1`
留在原地（报告 03 §4.5 的负面结论：`jsonl_paths.py` 确实只被 E2E 工具用）。

### 3.3 修复（每处都要复跑并留退出码）

1. `check-readme-contract.py:47` 死路径 → 指向表的**新家**；判据：`111 key(s) checked against 401 declared constant path(s): ok`，exit 0。
2. `check-fixture-drift.py:38-40` 同样 → 且读不到输入必须 exit **2**（现在是 exit 1 + traceback，违反三态约定）。
3. `publish-campaign.sh:8,36`（已删的 campaign 目录）与 `:28`（`target-ledger.jsonl` 已更名）→ 按
   `start-targets.sh:29,33` 的 `ledger-main.jsonl`/`ledger-direct.jsonl` 修。
4. `deploy-campaign.sh:9,43-57` 的来源 `/tmp/wf-bench/deploy` 无人创建 → 改为从仓库取，或写明手工前提。
5. `orchestrator.ps1:64,358,382`（T3）→ `Invoke-Client` 的日志别被 `[void](…)` 吞掉。
6. `make_tree.py:15-16`（T6）→ docstring 与生成器对齐。
7. `FROZEN.md:35,92` 与 `10-06-e2e-competitor-benchmark` 的 4 处陈旧指针（那是个**未归档**任务）。

## 4. C4 · 数据、文档、plumbing

### 4.1 文档（门禁敏感）

- `WinForward.E2E/README.md:372-447` 是 `check-readme-contract.py` 的输入，用字面标题 +
  下一个 `## ` 定界 → **改动该区间、插入 `## `、或把它往文件后面挪都会静默缩小覆盖**。
  每次改动前后各跑一次检查器并对比 "111 key(s) / 401 constant(s)" 那行。
- `measurement-tooling.md:9` 按**行号**引用 "line 372"，`:65` 引用 "README.md:58-72" → 在 372 行之前插入内容必须同步这两处。
- 16 条陈旧声明逐条修（清单在 `research/04` §7，两边的 `file:line` 都有）。
  高价值几条：`:72` 的"28 CLI commands"（`cli-snapshots.py:42-76` 是 31）、
  `quality-guidelines.md:125` 把 `analyzer-gate.yml` 说成 build/test 的所在地（它两个都不跑）、
  `Analysis/README.md:498` 把 `check-fairness.py` 的路径写错、`:58-72` 漏掉 `compare-records.py`。

### 4.2 数据

- `plans-windows/full-shape-plan.json` = `plans/full-plan.json` 覆盖 9 个 `seconds` 叶子；
  `plans-short/` 有 7 个非 `seconds` 差异。**`PlanFile.cs` 没有覆盖机制**，去重需要新增 loader 行为
  → 本轮**不改数据**，只把三套 plan 的用途与差异如实写进 README；去重登记为后续条目。
- `wf-fdd-opt.json` 与 `wf-aot-opt.json` 逐字节相同 → 要么合并要么说明为何要两份（本轮只记录）。
- README 暗示的 `bench.ppx` 不在仓库 → 补齐或改文档。

### 4.3 `IsTestProject`（唯一一个需要实验的 plumbing 项）

**实测机制**：restore 时 NuGet 设 `ExcludeRestorePackageImports=true`，xunit 的 props 不参与，
于是 `tests/WinForward.E2E.Tests.csproj:4` 显式写的 `<IsTestProject>true</IsTestProject>` 是
**restore 期唯一**让根 `Directory.Build.props:14` 的条件为假的东西。结果：15 个测试项目里
14 个 `analyzers=4`，只有它 `analyzers=0`（`project.assets.json` 实测；`-p:IsTestProject=false`
立刻变 4）。build 求值下 xunit 的 props 也会把它设成 true，所以报告 02 说这行"冗余"——
**在 build 期对，在 restore 期不对**，而分析器引用是 restore 期定下来的。
处置（D4）：先删掉跑一次 `dotnet build -c Release`，看 4 个分析器在约 90 个测试文件上出多少条；
少就修掉，多就保留并在 csproj 里写明豁免理由。

## 5. 验证与回滚

**每个工作流的收尾门禁**（顺序固定，`oracle-diff` 需要先建 Release 二进制）：

```bash
dotnet build WinForward.slnx -c Release                          # 0 warning
dotnet test  WinForward.slnx -c Release                          # 全绿
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py          # 全批次，semantic
python3 benchmarks/WinForward.E2E/scripts/check-fairness.py
python3 benchmarks/WinForward.E2E.Analysis/verification/check-boundary-trees.py
python3 benchmarks/WinForward.E2E.Analysis/verification/check-fixture-drift.py --tree /tmp/wf-synth
python3 <effective-lines 的新家> benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts \
        benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx
```

**回滚点**：C1 的 S0–S5 每步一个 commit（S4 与 S5 必须分开——S4 会红、S5 才解释它）；
C2 机械改名一批一 commit；C3/C4 各自一 commit。任何一步 oracle 出现 `[structure]` 差异 =
回滚该步，结构差异从来不是"已放宽"的东西。

**不变量检查**（每个 commit 前）：

```bash
git diff --stat -- benchmarks/WinForward.E2E.Contracts  # 只应出现 C1/C2 计划内的改动
rg -n '"osDescription"|"clientSendLoss"' benchmarks/WinForward.E2E.Contracts  # 常量值不许变
```
