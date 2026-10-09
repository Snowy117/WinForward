# C2 执行计划

> 前置：C1 已归档（先删后改名，别改将被删的代码）。
> 每批一个 commit；每批之后 `dotnet build -c Release` + `dotnet test -c Release`，
> 涉及输出面的批次再加 `oracle-diff.py` 全批次 rc=0。
> 证据与逐条 `file:line`：`../10-09-e2e-csharp-native-cleanup/research/02-naming-and-idioms.md`。

## N0 · 零风险机械项（一批）

1. **四处**缩写改名（复核实测 **26 处引用**，原计划写的 41 不可复现）：
   `Target/SocketIo.cs:27` → `SocketIO`（含 `README.md:53` 的说明行）、
   `Client/Lanes/LaneTransportContracts.cs:39` 的 `LaneReceiveKind.IoError` → `IOError`、
   `Contracts/ArmKeys.Run.cs:30` 的 `OsDescription` → `OSDescription`、
   `tests/.../JsonlSinkTests.cs:35` 的 `AnIoFailureIsPropagatedUnderTheClientPolicy` → `AnIOFailure…`；
   外加 `.trellis/spec/backend/measurement-lane-seam.md:19,25` 的 `IoError` 文案。
   **常量值 `"osDescription"` 一个字不动**。
   ⚠️ **本步的判据不是 `check-readme-contract.py`**（它在 HEAD 上 rc=2，见 prd AC1）：用
   `rg -n 'SocketIo|IoError|OsDescription|AnIoFailure' benchmarks/ tests/ --type cs` 归零
   + `JsonKeyLiteralGateTests`/`ContractShapeTests`/`CliSnapshotTests` 绿。
2. 4 个未用 `using`：`WinForward.E2E/Program.cs:2`、`Analysis/Checks/ControlDrift.cs:1`、
   `Analysis/Findings/LedgerViews.cs:2`、`Analysis/Loading/LedgerLoader.cs:1`。
3. `Repeated`×3 去重（`Int(double)` 已在 C1 的 S0 处理，别重复）。
4. `Dedicated` → `DedicatedThread`（18 处）。

**判据**：build 零警告；测试绿；oracle 全批次 rc=0。

## N1 · `Measured<T>` 收编命名元组（一批；见 D12）

`research/02` 的 Top 1，但**计数被复核纠正**：字面上是 `(X? Value, string? Reason)` 的**声明只有 14 个**；
`research/02` 的 21 是"把 `Detail`/`Error` 也改叫 `Reason`"之后构造出来的数。本步的范围：

- **做**：14 个字面声明 + 约 4 处签名（`BootstrapPair.cs:41` 等）+ 约 22 处解构点，
  其中 4 处不是返回位置（`MetricSpec.cs:40`、`ControlDrift.cs:286`、`ArmDenominatorTable.cs:100`、
  `MetricCatalogue.cs:229` 的形参）。
- **不做**：把 `Detail`/`Error` 改名成 `Reason`（`IdentityChecks.cs:93,257,284,309`）——
  那是语义改名，与"无语义变化"冲突；`MetricCell.cs:42` 的非空 `Status` 也不并入。
- **单独裁定**：`CpuDetail.cs:58` 的 3 成员元组——要么也收进 `Measured<T>` 的变体，
  要么明确记为"不属本模式"，写进 `research/notes.md`。

`src/` 的命名元组返回是 **0**，所以方向没问题。`Measured<T>`（见 D12）可以留在 `Analysis` 内部
（`Analysis` 没有 `InternalsVisibleTo`，复核已证）。

**判据**：`rg -n '\(.*\? Value, string\? Reason\)'` 在四个树里归零；测试绿；oracle rc=0。

## N2 · 拆两个超限/杂烩文件（一批）

1. `Analysis/Findings/LedgerViews.cs`：385 有效行、4 个类型 → 按类型拆。
   ⚠️ 复核纠正：这个文件**没有** `.editorconfig` glob；`:247` 的 glob 是
   `Analysis/Findings/LedgerFindings.cs`，而它 `:21` 已经是那个 partial class——
   **不要把 `LedgerViews.cs` 改名成 `LedgerFindings.cs`**（会撞上既有文件与它的 glob）。
2. `Client/ArmContext.cs`：7 个无关类型 → 拆到各自文件；顺手更正 `README.md:37` 对它的描述。

**判据**：`tools/effective-lines.py <四路径>` 无输出；build + 测试 + oracle 绿。

## N3 · 文件改名（一批，全 `git mv`）

> ⚠️ `.editorconfig:247` 的 glob 路径是 `Analysis/Findings/LedgerFindings.cs`（主文件，**动不得**）；
> 复核已确认四个 glob 覆盖的路径与下面的改名目标**没有冲突**，但改名前仍要 `rg` 每个旧文件名
> 在 `.trellis/spec`、`README.md`、`.editorconfig`、`tests/**` 里的全部引用。

1. `LedgerFindings.ClientCounts.cs` / `LedgerFindings.DnsTotals.cs`
   （现在叫 `LedgerClientCounts.cs`/`LedgerDnsTotals.cs` 却声明 `partial class LedgerFindings`）。
2. `Tables/ArmDenominatorTable.cs` → `TableArmDenominator.cs`（`Table*` 前缀顺序）。
3. `Json/Rate.cs` → `JsonRate.cs`、`Json/PerSecond.cs` → `JsonPerSecond.cs`。

**判据**：`git status` 显示 rename；build + 测试绿；`.editorconfig` 的四个 glob 路径**未变**。

## N4 · 迁移 vocabulary 清理（一批）

删 `E2-d`/`b1b` 一类的批次号与"参考实现"叙述（改写为对当前行为的陈述），
保留 `D*` 决策引用（房规）。位置：`Analysis/Cli/AnalysisOptions.cs:6`、
`tests/.../ContractRegistry.cs:113,136`、`ContractShapeTests.cs:15,30` + 11 行批次号。

**判据**：`rg -n '\b(E2-[a-z]|E4-[a-z0-9]+|b1b|b1c)\b'` 在四个树里无命中；测试绿。

## N5 · 测试项目拆分与 `IsTestProject`（一批）

1. 拆 `tests/WinForward.E2E.Tests/Lanes/LaneTestDoubles.cs`（10 类型 / 359 有效行）。
2. **D4 的 spike**：删掉 `tests/WinForward.E2E.Tests/WinForward.E2E.Tests.csproj:4` 的
   `<IsTestProject>true</IsTestProject>`（**父 session 已实测**：15 个测试项目里 14 个
   `project.assets.json` 解析出 4 个分析器包、只有它 0 个；`dotnet msbuild -p:IsTestProject=false
   -getItem:PackageReference` 立刻列出 4 个。机制：restore 期 NuGet 设
   `ExcludeRestorePackageImports=true`，xunit 的 props 不参与，于是这行显式声明是唯一让根
   `Directory.Build.props:14` 条件为假的东西——**注意评审子代理把这条标成"未验证"，
   但父 session 用上述两条命令亲自测过**）。`dotnet restore` 后
   `dotnet build -c Release` 看 findings 数量：
   - 少（可逐条修或有理由地窄压制）→ 修掉，让测试项目回到分析器覆盖下；
   - 多（工作量大）→ 还原那行，并在 csproj 里写明"豁免 + 理由 + 何时复核"。
   两种结果都要把 **findings 数量与选择**写进 `research/notes.md`。

**判据**：build 零警告（或 csproj 里有明确豁免注释）；测试绿；`effective-lines.py` 无输出。

## 收尾

- 全套门禁（父任务 `design.md` §5）。
- spec 更新：若 `README.md:37` 或命名规则需要落进 spec，写进
  `.trellis/spec/backend/directory-structure.md`（命名段）——这一次把"两字母缩写全大写、
  `Id` 例外、≥3 字母视作单词"写成明文规则，免得下次再靠反推。
- 交给父任务登记：任何在改名过程中发现的、属于 C4 的文档陈旧项。
