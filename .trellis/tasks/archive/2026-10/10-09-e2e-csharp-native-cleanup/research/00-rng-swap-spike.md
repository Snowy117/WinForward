# 00 · 实测：把 bootstrap 的 RNG 换成 `System.Random` 会让 oracle 哪里变红

**日期**：2026-10-09 · **性质**：一次性测量（spike），代码已回滚；`git status benchmarks/` 为空，oracle 复跑 rc=0。

## 为什么测

D21 §3 说"C# 侧可自由选择 RNG 实现；bootstrap/CI 的数值按容差比较"，但**从没实测过**。
`oracle-diff.py` 的容差是"参考打印精度的 1 个末位单位"（`oracle-diff.py:1176`），而
`verdict.json` 的数字是按 `repr` 全精度打印的（例如 `"p_equivalence": 0.2653`、
`"ci95": [1.0023750585926172, 1.05301109663152]`），末位单位 ≈ 1e-4 ~ 1e-16。
所以"按容差比较"能不能吸收 RNG 变化，只能测。

## 怎么测

1. 基线：`python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic` → 51 切片 rc=0
   （4.6 s，输出留在 `/tmp/wf-oracle/cs`），复制到 `/tmp/spike-base`。
2. 改动（**仅 4 行，已回滚**）：`Analysis/Stats/BootstrapPair.cs`
   `new CpRandom(seed)` → `new Random(seed)`；`CpRandom random` → `Random random`（两处形参 + `Resample`）；
   `(int)random.RandBelow(values.Count)` → `random.Next(values.Count)`。
   `DeriveSeed`/`StableHash` 未动（它们只决定每个比较的种子，与 MT19937 无关）。
3. `dotnet build benchmarks/WinForward.E2E.Analysis/... -c Release` → 0 警告 0 错误
   （唯一插曲：留着 `(int)` 会被 SonarAnalyzer `S1905` 拦下）。
4. 重跑 oracle，把输出复制到 `/tmp/spike-rng`；再把基线树按 `py-` 前缀做成参考目录，
   用 `oracle-diff.py --golden /tmp/spike-base-ref --cs-out /tmp/spike-rng` 正式比对。

## 结果

| 指标 | 值 |
|---|---|
| 切片 | 51 个中 **10 个**变红（都在 `verdict.json` 的 `metrics/*`） |
| 差异条目 | **75 条 `[value]`，0 条 `[structure]`，0 条 missing** |
| 表 `tables.md` | **0 切片变化**（4 张核心表 + 全部 16 节都没动） |
| 顶层 key | 全部仍在，key 集不变 |
| verdict 字符串 | **没有一条 `raw_verdict`/`holm_verdict` 翻转**（否则会以 `[value]` 出现在 verdict 路径上） |

75 条差异按字段分：

| 字段 | 条数 | 例 |
|---|---|---|
| `p_value` | 58 | `0.529` → `0.525`；`0.515` → `0.5456` |
| `p_equivalence` | 10 | `0.2625` → `0.2593` |
| `holm_p_equivalence` | 7 | — |
| `ci95[0]`/`ci95[1]` | **0** | 冻结树的每行只有 **3 个 pass**，bootstrap 的估计只有 3³ 种取值，2.5 % / 97.5 % 分位落在同一批序统计量上 |
| `estimate`/`median`/`iqr` | **0** | 这些是数据的确定性函数，不经过重采样 |

## 结论（给 C1 的设计）

1. **换 `System.Random` 是安全的**：唯一会动的是"重采样噪声"本身——p 值；确定性统计量、表格、
   结构、findings、verdict 判定全部不动。用户已裁定"没必要对齐行为，直接用标准库最简明的随机数"。
2. **oracle 需要一次"窄放宽"**，否则仓库自带的门禁会长期变红。候选规则（按推荐次序）：
   - **窄**：路径匹配 `metrics/*/pairs[*]/{p_value,holm_p_value,p_equivalence,holm_p_equivalence}` 的数值，
     改为**声明式统计容差**（例如绝对 1e-2；这些量是 `count/10000`，granularity 1e-4）或"只查类型不查值"，
     并在 `FROZEN.md` / `measurement-tooling.md` / `oracle-diff.py` 的文档串里写明理由。
   - **宽**：把 `ci95` 也纳入同一条规则——冻结树测不出来（0 条差异），但**真实 campaign 的 pass 数更多**，
     ci 端点届时也会动。建议一并纳入，并在注释里说明"冻结树恰好没触发"。
3. **不能放宽的东西**（本次实测证明它们不受影响，所以放宽它们是纯粹的损失）：
   `raw_verdict`/`holm_verdict`/`raw_reason`/`holm_reason`、`estimate`、`median`、`iqr`、
   `tables.md` 的任何一格、所有 key 集与类型。
4. **判定风险仍在**：p 值越靠近阈值，verdict 越可能翻转。窄放宽不会掩盖它——verdict 是字符串，
   oracle 仍然逐字比。真实 campaign 上若出现翻转，那是**要看的信号**，不是噪声。
5. `--seed` 的承诺要改写：`System.Random(seed)` 的序列**不保证跨 .NET 版本稳定**
   （见下一条待确认项）。README 现在若写了"同 seed 逐字复现"，改成"同一运行时下可复现"。

## 没测的、留给 C1 的

- `System.Random` 跨 .NET 版本/架构的种子稳定性：官方只承诺"同一版本内"。需要一条廉价 tripwire
  （例如固定 seed 的前 N 个 `Next(1000)` 断言）或干脆接受"报告数字随运行时变"。
- 真实 campaign（pass 数 > 3）下 ci95 是否真的会动——冻结树给不出答案。
