# 决策记录（10-09-e2e-csharp-native-cleanup）

> 本文件按时间顺序记录本任务确认过的裁定。**冲突时以本文件为准**；每条都带证据来源。
> 调研证据在 `research/00`–`04`，计划在 `prd.md` / `design.md` / `implement.md`。

## D0 · 立项依据（2026-10-09）

**裁定**：这不是新想法，是 `10-07-e2e-harness-refactor` 的 **D21** 的收尾 + **D22** 的未闭项。

- D21（用户 2026-10-08）已判定"复刻 CPython 的 RNG / `%g` 形态 / 转义规则只增加复杂度、收益为零"，
  并把 oracle 放宽为语义比对，但 §3/§4 把**删除**推迟了（"已写好的代码的修改之后再说"）。
  证据：`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md:881-905`。
- D22 的 T3（orchestrator 吞退出码）、T6（`make_tree.py` docstring）本轮开工时仍 open。
  证据：同目录 `research/tickets.md`。

## D1 · 用户裁定：不与 Python 对齐行为（2026-10-09）

> "我们没有必要完全对齐行为。这个项目只要能做到 E2E 就行了。为什么不能直接使用标准库最简明的随机数呢？
> 为什么非要 RNG 呢" —— 用户

**裁定**：`CpRandom` 直接换 `System.Random(seed)`，不复刻 MT19937、不做"逐位可复现"的额外设计；
`StableHash`/`DeriveSeed` 保留（它们是 `--seed` 复现性的载体，不是 RNG 算法）。

**配套实测**（`research/00-rng-swap-spike.md`，报告 01 附录 A 独立复算一致）：

| 指标 | 值 |
|---|---|
| oracle 切片 | 51 个中 10 个变红 |
| 差异条目 | 75 条 `[value]`、**0 `[structure]`、0 missing** |
| 字段分布 | `p_value` 58、`p_equivalence` 10、`holm_p_equivalence` 7 |
| `tables.md` | **0 切片变化** |
| verdict 字符串 | **0 翻转**（`estimate`/`median`/`iqr` 也 0 变化） |
| `ci95` | 冻结树上 0 移动（每行 3 pass ⇒ 2.5 % 分位落在最小原子，P≈0.259）；报告 01 合成实验：15 pass 时 6 次里 4 次会动 |

## D2 · 脚本搬家方向（用户同意，2026-10-09）

- `effective-lines.py` → 新建根级 `tools/`：400 行是**全解决方案**规则
  （`directory-structure.md:98,100,103`），还被用来跑 `WinForward.Benchmarks`（`benchmarks/README.md:433`），
  而 `WinForward.E2E/README.md:694` 却自称它是"harness 自己的门禁"。
- `oracle-diff.py` + `check-fairness.py` + 两张契约表 → `WinForward.E2E.Analysis/verification/`：
  它们的全部输入是 `verification/**` + 分析器二进制；`measurement-tooling.md:127` 已称 oracle-diff "外来"。
- `jsonl_paths.py` / `compare-records.py` / `cli-snapshots.py` / `publish.sh` / `selftest.sh` /
  `orchestrator.ps1` 留在原地（负面结论，见 `research/03` §4.5）。

## D3 · 四个 gitignored 本机脚本（用户同意，2026-10-09）

`wf.sh` / `deploy-campaign.sh` / `start-targets.sh` / `publish-campaign.sh`：**就地修 + README 写明是本机胶水**，
不入库。`orchestrator.ps1` 已被跟踪且是本机耦合最重的一个——跟踪状态与 README/`.gitignore` 一致，
不强行统一。

## D4 · E2E 测试项目的分析器豁免（用户同意，2026-10-09）

**实测**：`tests/WinForward.E2E.Tests.csproj:4` 的 `<IsTestProject>true</IsTestProject>` 在 **restore 期**
是唯一让根 `Directory.Build.props:14` 的分析器 ItemGroup 条件为假的东西（restore 时 NuGet 设
`ExcludeRestorePackageImports=true`，xunit 的 props 不参与）。结果：15 个测试项目里
**14 个 `analyzers=4`，只有它 `analyzers=0`**；`-p:IsTestProject=false` 立刻变 4 个。
（报告 02 说这行"冗余"——在 build 求值下对，在 restore 期不对；以实测为准。）

**裁定**：先 spike（删掉 → Release build → 数 findings），少则修掉、多则保留并在 csproj 写明豁免理由 +
复核时机；结论写进 spec。由 C2 执行、C4 复核落纸。

## D5 · CI 缺口（用户同意，2026-10-09）

没有 workflow 跑 `dotnet build`/`dotnet test`（1 660 个测试零强制），也没有跑 E2E 的 Python 门禁。
**另立任务**，不塞进本次清理。

## D6 · 发布文本里的"Python 残留"（用户同意，2026-10-09）

`RunLoader.ReferenceName` 的 CPython 异常名（`JSONDecodeError` 等）与 `JsonText` 的 `None`/`True`/`False`：
金标 0 命中（改了不会红），但真实 campaign 会印给人看 → **改成 .NET 名**。

## D7 · oracle 的定位（用户 2026-10-09："可以"）

**裁定**：**保留 + 修剪 + 接进 `.github/workflows/analyzer-gate.yml`**。

理由：它是分析器**唯一覆盖整份报告输出面**的回归网（16 张表 + 14 个顶层 key 端到端），
本次换 RNG/JSON 写手/数字格式化正是靠它证明"只动了 p 值"；而 C1 要删掉三张向量表与逐字断言，
它的重要性只增不减。它的三个弱点都是"该修"而不是"该退役"：

1. 没有任何 CI/测试跑它（跑一次 4.6 秒，接进 CI 成本极低）；
2. 背着不属于它的行李（三张向量表是单测输入、`--mode byte` 不是批次判据）→ C1 修剪；
3. 金标无法就地再生（`analyze.py` 已删，重冻需从 git 历史恢复）→ 这是"独立答案键"的代价，
   **不采用**"用 C# 侧重冻金标"（那会把它降级成变更探测器）。

## D8 · oracle 窄放宽的范围（用户同意，2026-10-09；**容差于 γ 段实测修正**）

**裁定**：p 值四件套 **与 `ci95`** 一并纳入声明式统计容差；其余一律不放宽（verdict 字符串、`estimate`、
`median`、`iqr`、表格每一格、所有 key 集与类型）。判据：路径限
`metrics/*/pairs[*]/{p_value,holm_p_value,p_equivalence,holm_p_equivalence,ci95[0],ci95[1]}`，
理由写进 `oracle-diff.py` docstring + `FROZEN.md` + `measurement-tooling.md`。

**γ 段实测修正（2026-10-09）**：p 值四件套的容差从**绝对 `1e-2` 提到 `5e-2`**。
原因是原值取错了基准——`1e-2` 是从**打印粒度**（`count/10000` → `1e-4`）推的，
而容差应当由**量本身的复现性**决定。实测：

| 实验 | 移动的叶子 | p 值最大位移 | SD |
|---|---|---|---|
| 换 RNG（金标 → `System.Random`） | 75 | 0.035 | 0.008 |
| **同一二进制、同一棵树，只换 `--seed`**（20261006 vs 20261007） | 77 | 0.0304 | 0.0064 |

即"同一棵树换个种子"本身就能让 p 值动 ~3e-2，是原声明界的 3 倍。`5e-2` = 7.8×SD、
1.4× 实测最坏位移，同时远低于真实变化会造成的 ≥0.1。`ci95` 保持
`max(1e-2, 1e-2·abs(expected))`（两轮实验里 `ci95` 与 `holm_p_value` **一次都没动**）。
实测：`5e-2` 下全批次 rc=0（`0 structure, 0 value, 0 missing`）。

**ci95 纳入的理由**（D1）：冻结树测不出（3 pass），真实 campaign 的 15 pass 下 6 次试验有 4 次端点会动。

## D11 · 不要提交"故意留红"的状态；S4 与 S5 合成一次派发、一个 commit（2026-10-09，父 session 裁定）

**背景**：C1 计划原本把 S4（换 `System.Random`，oracle 会红约 75 条）与 S5（放宽 oracle 解释它）
拆成两次派发、两个 commit，理由是"先看见、再解释"。

**问题**：那意味着历史里留一个 **门禁红的 commit**，日后 `git bisect` 会踩雷；
而"先看见"要的是**过程证据**，不是**提交边界**。

**裁定**：
1. S4 与 S5 **合并为一次派发、一个 commit**；提交后的树必须是绿的。
2. "先看见"用过程证据保证：实施者必须在 S4 之后、S5 之前跑一次**未放宽**的全批次 oracle，
   把 `differences: 0 structure, ~75 value, 0 missing` 与逐条 `[value]` 的 path 清单
   `tee` 进 `research/notes.md`（含字段分布 p_value 58 / p_equivalence 10 / holm_p_equivalence 7），
   然后再做放宽。commit message 里引用这份记录。
3. 验收不变：放宽后全批次 rc=0，且放宽范围严格等于 D8 声明的那几类数值叶子。

## D10 · S2 的边界：转义规则是"我们的契约"，不是"Python 的残留"（2026-10-09，父 session 裁定）

**背景**：C1 计划的 S2 原本要"把 `VerbatimJson` 换成 `Utf8JsonWriter`"。评审用仓库自己的金标证伪了
内建编码器（我独立复核一致）：`py-verdict.json` **非 ASCII 字节 0、`\uXXXX` 360**，同时有
**22 个字面 `+`、9 个 `>`**——`JavaScriptEncoder.Default` 会转义 `+<>&'`，`UnsafeRelaxedJsonEscaping`
会放过非 ASCII，两个方向都错。

**裁定**：S2 的目标是**停止模仿 Python，而不是改变已发布的字节**。所以：

1. **转义规则保留**（当前 `VerbatimJson.String` 的行为：只转义 JSON 必需的两个 + 五个短控制转义，
   其余非可打印 ASCII 写 `\uXXXX`，代理对拆两半；`'`/`+`/`<`/`>`/`&`/`/` 原样放过），
   把 docstring 从"as `json.dumps(value)` writes it"改写成**我们自己的契约陈述**
   （为什么这么写：语义模式下两侧都会解码，所以字节只在 `--mode byte` 的结构回归里被看见）。
2. **不引入 `Utf8JsonWriter`**，不改 140 个调用点——收益不抵风险。
3. S2 真正要删/简化的是**纯 Python 仿真的那部分**（缩进/发射机制里没有契约价值的代码），
   以及把 `AnalyzerJsonGoldenTests` 的定位从"钉住 CPython"改成"钉住我们的发布文本"。
4. S3（`VerbatimNumber`）不受影响：半偶 BigInteger 机器照样删，`%.3g`/`%.3e` 回退照样留（各 27 格）。

**验收口径不变**：语义 oracle 全批次 rc=0；`--mode byte` 的任何残余差异都要登记，
但**不允许为了"让 byte 变绿"而改语义**。

## D9 · 命名规则（用户 2026-10-09）

> 两字母缩写全部大写（`IO`、`IP`、`OS`；`Id` 例外）；更多字母的缩写视作一个单词（`Html`、`Json`）；
> `IPv4`/`IPv6` 里 `IP` 大写、`v` 小写。

**实测自查**：四个 E2E 树里 `IP`/`IPv4`/`IPv6` 已合规（`Target/DnsServer.cs:159`、
`Target/SourceCensus.cs:147,169`），`Id` 已合规（三树里的 `ID` 只有 `IDLE` 臂名与 `IDisposable`），
≥3 字母缩写已合规。**两字母全大写有 4 处越界**：`SocketIo`→`SocketIO`、
`LaneReceiveKind.IoError`→`IOError`、`ArmKeys.Run.OsDescription`→`OSDescription`（成员名；
常量值 `"osDescription"` 冻结）、测试方法 `AnIoFailure…`→`AnIOFailure…`（评审补入，实测 26 处引用）。

⚠️ **规则只对新代码生效**：`src/` 里有 108 处 `Ipv[46]` 拼写（12 个文件），
把规则原样写进 spec 会立一条 `src/` 自己违反的法 → 既有拼写另立后续条目，不在本任务改。

## D12 · `Reading<T>` 改名 `Measured<T>`（2026-10-09，父 session 裁定，交 C2 执行）

**背景**：C2 的 Top-1 是把 14 个 `(T? Value, string? Reason)` 命名元组收进一个记录，原计划叫
`Reading<T>`（放 `Analysis/Model/`）。

**问题**：`WinForward.E2E.Contracts/Json/Reading.cs:13` **已经有一个 `Reading`**——它是把
可空读数写进 JSON 的写手，与"值 + 缺失原因"是**两回事**。两者分属不同命名空间/程序集，
编译器按元数区分，所以不会编译错；但 `tests/WinForward.E2E.Tests` 同时引用 `Contracts` 与
`Analysis`，一个测试文件里两个 `Reading` 同时在作用域内，语义还不同——这正是本仓库质量规约要避免的。

**实测**：`Measured` / `Outcome` / `Availability` / `ValueReason` / `Gauge` / `Probe` 在
`src/` + `benchmarks/` + `tests/` 里**都是 0 个声明**；`Reading` 恰好 1 个（就是上面那个）。
另外 `Analysis` 目前 **0 个文件** import `Contracts.Json`，所以冲突是语义层面而非编译层面。

**裁定**：新记录命名 **`Measured<T>`**（"这份分析得出的值，或得不出它的原因"），
放 `Analysis/Model/Measured.cs`；C2 的 AC2 与实现清单按此更新。
不用形容词式命名（`Available<T>`）——`research/02` Part A 已记下"形容词不是类型名"这条房规。

## D13 · D7 的落地形态：新增 `ubuntu-latest` job，不加进现有 windows job（2026-10-09，父 session 实测裁定）

**背景**：D7 批准把 oracle 接进 CI。原计划是给 `analyzer-gate.yml` 现有 job 加一步。

**实测障碍**：那个 workflow 的两个 job 都跑 `windows-latest`，而 differ 是 **POSIX-only**：

1. `oracle-diff.py` 的 `ANALYZER` 是**无扩展名**的路径
   （`…/bin/Release/net10.0/WinForward.E2E.Analysis`）——Linux 的 apphost 没扩展名，
   Windows 上那个文件叫 `.exe`，直接执行会失败；
2. 冻结树与 `--raw` 硬编码 `/tmp/wf-synth`（`TREE = Path("/tmp/wf-synth")`），
   `FROZEN.md` 还专门写明"路径是配方的一部分"（换目录树哈希就变）。

**裁定**：在 `.github/workflows/analyzer-gate.yml` 里**新增一个 `ubuntu-latest` job**
`oracle-regression`（`dotnet build -c Release` → `verification/oracle-diff.py` → `verification/check-fairness.py`），
**不动**既有两个 windows job。修 differ 的跨平台性是另一件事，不在本任务。
判据：workflow diff 审查 + 本地同序三条命令 rc=0。
