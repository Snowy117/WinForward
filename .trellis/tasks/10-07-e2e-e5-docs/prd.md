# E5 文档与验收

> **开工前裁定（2026-10-07）**：见 `../10-07-e2e-harness-refactor/design-decisions.md`
> （D0.2/D0.3/D9/D11/D18 的披露改写、Windows 验证形态、AC18 三态）。**冲突处以该文件为准**。

父任务：`10-07-e2e-harness-refactor`。验收清单见
`../10-07-e2e-harness-refactor/implement.md` 的「全任务的总验收」节。

本子任务展开的父需求：R12 / R14；AC13 / AC14 / AC17 / AC18。

## 目标

让文档与代码重新对齐，并跑完全部门禁。

这套 harness 的 `README.md`（560 行）质量很高——它有「Which keys are contract」（`:303-353`）、
「Non-obvious properties」（`:490-534`）、「Verification」（`:536-560`）三节，把测量语义写得比代码
注释还清楚。**问题恰恰在这里**：文档描述的行为与代码实际行为已经分叉，而 E1–E4 会进一步改变
字段名与若干语义。E5 的职责是把这个分叉关掉，让文档重新成为可信的契约。

## 范围

- `benchmarks/WinForward.E2E/README.md`
  - 契约表（`:303-353`）逐项改为新字段名，注明定义在 `WinForward.E2E.Contracts/ArmKeys.cs`
  - `Non-obvious properties`（`:490-534`）逐条复核是否仍成立——E3 的修复会改变若干条
    （直方图饱和、`abandonedAtTeardown` 的含义、UDP 序列空间上界等）
  - `Verification`（`:536-560`）更新为新字段名与新 gate
  - `Layout` 表加入两个新项目
  - `Wire/` 的规格表（`:412-420`、`:434-437`）加交叉引用到 `FrameCodec.cs`/`TcpCommand.cs`
    （今天它们是手抄副本，改一处忘另一处就会产生"文档撒谎"）
- 分析器 README：**归属 E4**（D20.7：随分析器一起搬到 `benchmarks/WinForward.E2E.Analysis/README.md`）；
  E5 只负责复核其中的 `#17`/`#18`/`#19` 口径披露与绘图降级说明是否与表格脚注一致。
- **`harness-audit.md` §四 7 条口径披露**（这是它们唯一的载体，而 E5 正在改这两个文件）：
  - `tcp-connect` 的样本总体**排除了连接失败的请求** —— 这一栏可能反转连接排序
  - `tcp-connect` 与 `meanConnectMs` 是**两个统计量**，绝不能并列引用
  - MIX 的 `dns-rtt` 用 `Socket.Available` + `Task.Delay(1)` 轮询测（Windows 上可加 15.6 ms）
  - 直方图在 **17.18 秒处饱和**，更长的挂起无法区分
  - 账本**没有 arm/row/product 归属**，只能靠 `label` 关联（`analyze.py:2119 ledger_views`）；
    UDP/DNS 摘要是 1 Hz 区间增量而非累计；逐包身份不存在，因此"去程损坏"只能以 target 侧总量
    （`undecodable`）呈现，无法归因到具体序列或行
    （**注意**：`harness-audit.md` 原文"没有任何代码读它 / 没有绝对时间戳"已被证伪，必须按本行改写）
  - 分析器声明的 5 秒预热**只作用于内存，没有作用于 CPU**
  - 内存泄漏斜率的拟合序列按**固定臂序**而非实际运行序拼接

  前两条与最后一条能让一个看起来成立的结论直接翻掉。
- **Windows 轻量验证**：VM 上按 `AGENTS.local.md` §5 已部署的 `wf-aot` + sing-box，
  Windows client → 经透明代理 → Linux target。**plan 取保形压缩版**（新增
  `scripts/plans-windows/full-shape-plan.json`：保留 `full-plan` 的 LOSS `120s×500/s`、LATLOAD 500 rps、
  PERSIST idle、DNS 200 rps，只压缩其余臂时长），因为 `plans-short/` 同时改了负载形状，
  会让 `#4`/`#8` 在 Windows 段验不到。结果拉回本地后用 C# 分析器跑一遍。
  **AC18 是三态（pass/fail/blocked）**：blocked 时必须把失败命令与原始输出写进 `E5-WINDOWS-BLOCKED.md`，
  并在完成声明里显式写 `AC18=blocked`（依赖 `wf.sh`/`AGENTS.local.md` 两个 gitignored 资产）。
- 全量门禁。

## 不在范围内

- 绘图实现。
- `.trellis/spec/` 的更新（那是 Phase 3.3 的 `trellis-update-spec` 步骤）。

## 依赖

E2、E3、E4 全部完成——文档要描述的是最终形态。

## 验收标准

- [ ] 契约表的每一项都能在 `ArmKeys` 里找到对应常量，且代码里确实写出该字段。
- [ ] `Non-obvious properties` 每一条都经过复核；已不成立的条目被改写或删除，而不是留着。
- [ ] `Verification` 表的每个字段名与 gate 名都与代码一致。
- [ ] `Layout` 表包含 `WinForward.E2E.Contracts`、`WinForward.E2E.Analysis`、
      `tests/WinForward.E2E.Tests`。
- [ ] 线格式规格表与 `FrameCodec.cs`/`TcpCommand.cs` 互相交叉引用。
- [ ] 分析器 README（E4 交付）写明 CPU 口径、DNS 路径差异、UDP `not carried` 规则 —— E5 复核一致性。
- [ ] **§四 7 条口径披露全部落进分析器 README 或相关表格脚注**，逐条可查。
- [ ] **Windows 轻量验证（三态）**：经 `wf-aot` + sing-box 的保形压缩 plan 在 VM 上完成
      （client exit 0、无 `error` 记录、`run.json.failed == false`、findings 白名单外为空），
      拉回的结果能被 C# 分析器成功分析；无环境时按 D11 记 `blocked` 并留证。
- [ ] `dotnet build WinForward.slnx -c Release` 零警告。
- [ ] `dotnet test WinForward.slnx -c Release` 绿。
- [ ] `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` 退出 0 且输出为空。
- [ ] `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` 零 `<Issue>`。
- [ ] `scripts/selftest.sh scripts/plans/selftest-plan.json` 绿，client 退出码 0，无 `error` 记录。

> **权威规则**：父 `prd.md` 的 R/AC 是本子任务的**验收上限**；本文件的清单是它的展开，
> 冲突时**以父为准**，且本文件每条验收都必须能追溯到父的一个 R 或 AC。
> 父任务：`.trellis/tasks/10-07-e2e-harness-refactor/`。
