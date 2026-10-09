# C1 派发计划与执行日志

> 一个子任务拆成多次 `implement` 派发，每次之间由父 session 提交；
> 原因是"S4 必须留一次红、S5 才解释它"，而提交边界要求两次编辑之间有一次 commit。

| 派发 | 范围 | 预期状态 | 提交 |
|---|---|---|---|
| **α** | S0（`Truthy`/`Int`/`sum([])`/E13–E17 叙事/`__pycache__`）+ S1（`PythonExponential` → 格式串） | 全绿 | ✅ `a402289` |
| **β** | S2（`VerbatimJson` 按 D10：只删无契约价值的仿真 + 改叙事，不换 `Utf8JsonWriter`）+ S3（`VerbatimNumber`：删半偶 BigInteger，留 `%.3g`/`%.3e` 回退；测试步内同步） | 全绿（`--mode byte` 差异登记） | `refactor(analysis): stop narrating CPython and drop the half-even formatter (S2-S3)` |
| **γ** | S4（`BootstrapPair` → `System.Random`；`StableHash`/`DeriveSeed` 摘到新家；不删 `CpRandom.cs`）**→ 先跑未放宽的 oracle 并把 75 条 `[value]` 证据 tee 进 `notes.md`** → S5（按 D1/D8 放宽 + 删 `CpRandom`/向量表/`make_cp_vectors.py` + `RepoPaths` 死字段 + D6 发布文本）→ S6（全套门禁） | **最终全绿** | `refactor(analysis): draw the bootstrap from System.Random and register the statistical tolerance (S4-S6)` |

> **D11 修订（2026-10-09）**：S4 与 S5 **合并为一次派发、一个 commit**。
> 原计划让 S4 单独成一个"故意留红"的 commit，但那会在历史里留一个门禁红的点，
> `git bisect` 会踩雷。"先看见、再解释"改用**过程证据**保证：实施者必须在放宽之前
> 跑一次未放宽的全批次 oracle，把差异清单 `tee` 进 `notes.md`，再动放宽代码。

## 进度

- **α**：实施中（父 session 观察：`JsonValue.IsTruthy` 已删、三处 `Truthy` 调用点已切、
  `Int(double)` 三合一为 `Model/JsonNumber.cs`、`TableLedger` 的 `sum([])` 分支已去、
  `NaturalKey`/`RunClocks`/`MarkdownTable`/`ArmAccess` 的叙事已按 E13–E15 改写；
  `rg 'Truthy|IsTruthy'` 与 `rg 'static int Int\(double'` 均已归零）。
- **β/γ/δ**：待 α 的 commit 落地后依次派发。

## 父 session 的监督结论（不代跑构建的前提下）

- α 的 diff 只动叙事与三个自由项；`MarkdownTable.cs` 的"不校验单元格数"行为**未被改**（已逐行看 diff）。
- `ArmAccess.cs` 只改了一行 pragma 理由（E14），行为未动。

## 决策引用

- S2 的边界：父任务 `design-decisions.md` **D10**（转义规则是我们已发布的契约，不是 Python 残留）。
- 放宽范围：**D1/D8**（p 值四件套 + `ci95`；挂载点 `oracle-diff.py:968-972`，path 点分）。
- 命名与脚本的跨任务事项一律写 `research/notes.md`，不在本任务做。
