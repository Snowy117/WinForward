# C2 · 命名与结构清理

父任务：`10-09-e2e-csharp-native-cleanup`（需求 R3/R6、AC5）。
逐条证据：`../10-09-e2e-csharp-native-cleanup/research/02-naming-and-idioms.md`（571 行；
Part A 是从 `src/` 反推的房规，Part B 是逐项清单 + Top 10 + 改名禁区 + 路径字面量风险表）。

## 目标

四个 E2E 相关树（`WinForward.E2E`、`.Contracts`、`.Analysis`、`tests/WinForward.E2E.Tests`）
回归仓库既有命名与结构习惯。**只做机械改名与纯移动，不改语义、不动输出。**

## 命名规则（用户 2026-10-09 的口径）

> 两字母缩写全部大写（`IO`、`IP`、`OS`；`Id` 例外）；更多字母的缩写视作一个单词（`Html`、`Json`）；
> `IPv4`/`IPv6` 里 `IP` 大写、`v` 小写。

自查结论（已实测）：四个 E2E 树里 `IP`/`IPv4`/`IPv6` 与 `Id` **已经合规**，
≥3 字母缩写**已经合规**；两字母全大写有 **4 处**越界 →
`SocketIo`→`SocketIO`、`LaneReceiveKind.IoError`→`IOError`、
`ArmKeys.Run.OsDescription`→`OSDescription`、测试方法 `AnIoFailureIsPropagatedUnderTheClientPolicy`→`AnIOFailure…`
（复核：实测共 **26 处引用**，不是 41；另外 `.trellis/spec/backend/measurement-lane-seam.md:19,25`
还写着 `IoError`，要一起改）。

⚠️ **`IPv4`/`IPv6` 的规则只对新代码生效**：复核发现 `src/` 里有 108 处 `Ipv[46]` 拼写
（12 个文件），把规则原样写进 spec 会"立一条 `src/` 自己违反的法"。
所以：规则照写，但明确"既有 `Ipv*` 拼写不在本任务范围，另立后续条目"。

## 验收标准

| # | 判据 |
|---|---|
| AC1 | 四处缩写改名完成（**实测 26 处引用**）：`rg -n 'SocketIo\|IoError\|OsDescription\|AnIoFailure' benchmarks/ tests/ --type cs` 归零（基线 26 + 1）；**`ArmKeys.Run.cs:30` 的常量值 `"osDescription"` 未变**；`.trellis/spec/backend/measurement-lane-seam.md:19,25` 的 `IoError` 同步。⚠️ **不要用 `check-readme-contract.py` 当本任务的判据**——它在 HEAD 上 rc=2（`check-readme-contract.py:47` 指向已归档的表，`:272` 无条件读它），修复归 C3。替代门禁：`JsonKeyLiteralGateTests`、`ContractShapeTests`、`CliSnapshotTests` 全绿 |
| AC2 | 命名元组收编为 **`Measured<T>`**（D12：`Reading<T>` 会与既有的 `Contracts.Json.Reading` 语义撞名）：**复核纠正了计数**——字面上是 `(X? Value, string? Reason)` 的声明只有 **14 个**，凑到 21 需要把 `Detail`/`Error` 改名（`IdentityChecks.cs:93,257,284,309`、`BootstrapPair.cs:41`）并接受 `MetricCell.cs:42` 的非空 `Status`（那与"无语义变化"冲突，**不做**）。所以判据是：14 个字面声明 + 约 4 处签名（含 `CpuDetail.cs:58` 的 3 元组，需单独裁定）+ 约 22 处解构点（其中 4 处非返回位置：`MetricSpec.cs:40`、`ControlDrift.cs:286`、`ArmDenominatorTable.cs:100`、`MetricCatalogue.cs:229` 形参）全部换成 `Measured<T>`；`uvicorn`式改名不算目标。`Measured<T>` 可以留在 `Analysis` 内部（`Analysis` 无 `InternalsVisibleTo`，复核已证） |
| AC3 | `Analysis/Findings/LedgerViews.cs`（385 有效行 / 4 类型）与 `Client/ArmContext.cs`（7 类型）已拆分；400 行门禁四路径无输出（工具在 `benchmarks/WinForward.E2E/scripts/effective-lines.py`，**C3 之后才搬到 `tools/`**）；`README.md:37` 对 `ArmContext` 的描述同步更正。⚠️ **`LedgerViews.cs` 没有 `.editorconfig` glob**（复核纠正：`:247` 的 glob 是 `Findings/LedgerFindings.cs`，而 `LedgerFindings.cs:21` 已经是那个 partial class） |
| AC4 | 2 处 `git mv`（`LedgerFindings.{ClientCounts,DnsTotals}.cs`）+ 3 处文件改名（`TableArmDenominator.cs`、`JsonRate.cs`、`JsonPerSecond.cs`）+ `Dedicated`→`DedicatedThread`（18 处）完成 |
| AC5 | 迁移 vocabulary 清零：`AnalysisOptions.cs:6`、`ContractRegistry.cs:113,136`、`ContractShapeTests.cs:15,30` + 11 行批次号；`Repeated`×3 去重（`Int(double)` 归 C1 的 S0，不在此重复） |
| AC6 | 4 个未用 `using` 清除；`IsTestProject` 按 D4 处置（删掉并修 findings，或保留并在 csproj 写明豁免理由——**必须留证据**） |
| AC7 | 门禁：`dotnet build -c Release` 零警告、`dotnet test -c Release` 绿、`dotnet format --severity info --verify-no-changes` 空、`jb inspectcode` 0 issue、oracle 全批次 rc=0（改名不该动输出） |

## 改名禁区（硬约束）

- `.editorconfig:238,241,244,247` 用精确路径 glob 了四个文件：
  `CampaignQueries.cs`、`GateValidity.cs`、`GateFlow.cs`、`LedgerFindings.cs` → **文件名不许动**
  （`LedgerFindings` 的两处分片改名是加后缀，主文件保持 `LedgerFindings.cs`）。
- `ArmKeys.*` 的常量**值**、15 个 CLI flag、`tables.md`/`verdict.json` 结构与 key、`verification/**` 内容。
- C1 留在 `CpRandom` 继承者 / `Verbatim*` 上的 `public`（D20.6 的可见性理由）。
- 3 个把源码路径当字符串读的测试；`README.md:30-56`、`:506-514` 点名的字段与文件（改名要同步）。
- 每个 C1 刚删/刚改的文件：先 `git log`/`rg` 确认它现在的形态再动。

## 风险

| # | 风险 | 处理 |
|---|---|---|
| 1 | 改名撞上门禁字面量（`.editorconfig` glob、路径字面量测试、README 点名的符号） | 禁区清单 + 每批改完跑 `check-readme-contract.py` 与全测试 |
| 2 | `Measured<T>` 收编改错语义（`Reason` 可为 null 的约定） | 它是纯 record 替换，行为必须逐点等价；用现有测试兜底，不加新语义 |
| 3 | 拆文件时漏改 partial 声明或 namespace | 每拆一个立刻 build；`git mv` 优先于新建文件，保留历史 |
| 4 | 批次号清理把仍有价值的决策引用一并删掉（`D20.5`/`D14.7` 是房规） | 只删迁移批次号（`E2-d`/`b1b`），保留 `D*` 引用 |

## 不在范围内

- 任何语义/输出变化（那是 C1 的事）。
- README 的 16 条陈旧声明（C4）。
- `analysis/verification/**` 的内容与脚本（C1/C3）。
