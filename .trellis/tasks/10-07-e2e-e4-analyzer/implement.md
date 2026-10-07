# E4 执行计划（批次化）

**效力**：父 `design-decisions.md`（F 权威：**D6** oracle 机制、**D19.3 H**、D2/D9/D15/D17）>
父 `prd.md`/`design.md`（§1 目录、§6 验证策略）> 本目录 `prd.md` > 本文件。

依赖：E1（契约，已归档）、E3（语义字段，已归档）。**oracle 通过前不删 `analyze.py`**（D6.6）。

---

## 批次总览（按 DD **D20** 修订）

> 判据一律用 `benchmarks/WinForward.E2E/scripts/oracle-diff.py`（三态退出码 0/1/2；切片缺失 ≠ 通过）。
> 正式判据 = **干净树**上的切片空 diff；边界树（`--window-overflow`/`--undecodable`/`--truncated-*`）
> **只对 C# 产物做定点断言**（Python 参考不扩例外，D20.1）。

| 批次 | 内容（`tables.md` 小节 / `verdict.json` 键） | 结束判据 | 提交 |
|---|---|---|---|
| **E4-a** | 机制与骨架：`make_tree.py` 升级 + 最小改动 Python 参考 + 冻结树/golden A + `oracle-diff.py` + 项目骨架 + `analyze.sh` + `plots/SKIPPED.md`（+ fixture 漂移 guard） | 骨架必须让 `oracle-diff.py` 报 **rc=2**；改一个数字必须 rc=1；`dotnet build` 零警告 | 1 |
| **E4-b1a** | `Loading` + `Model` + §15 可用性 + §2 环境（+ `preamble` 切片） | `--batch 1a` 空 diff | 1 |
| **E4-b1b** | `Stats`：`CpRandom`（CPython 语义）+ `VerbatimNumber`/`VerbatimJson` + bootstrap | 黄金向量/中点值单测绿；`verdict.json` 的 `bootstrap`/`thresholds` 键命中 | 1 |
| **E4-b1c** | §1（`ROW_PROFILES` 渲染）+ §2（环境与 provenance） | `--batch 1c` 空 diff | 1 |
| **E4-b2** | findings 分级 + gates 表（§0/§3） | `--batch 2` 空 diff | 1 |
| **E4-b3** | **§5 latency / §8 udp / §9 dns**（三张核心表） | `--batch 3` 空 diff | 1–2 |
| **E4-b4** | **§4 headline** / §6 cpu / §7 memory / §10 persist / §11 tcp | `--batch 4` 空 diff（覆盖率最大） | 1 |
| **E4-b5** | §12 dual / §13 control / §14 ledger + `verdict.json` 其余键 | `--batch 5` 空 diff（AC9 达成） | 1 |
| **E4-c** | E4-c=E4-d：C# 侧公平性断言（规则源 `verification/row-profiles.json`）+ 截断 caveat（§14.7，两种机制分开、不摊到臂，带负控）+ **删除 `analyze.py`/旧 `check-fairness.py`** + `git rm --cached` 那枚 `.pyc` + README 指向 `analyze.sh` | 断言先红后绿；负控红；`rg analyze.py` 只剩文档/历史 | 1 |

**最小里程碑**：`b1a + b3`（§5/§8/§9）**+ `b4`**（§4/§6/§7/§10/§11）——四张核心表要分两批才能各自达成
（D20.8：§4 打印全部 21 个 metrics 列，其中 6 个属批 4）；此后覆盖率最大。

每批门禁：build（零警告）→ `test -m:1` → format → inspectcode（解析 XML）→ **`dotnet build WinForward.slnx -c Release`**
（`publish.sh` 只发布 harness 自己，**不覆盖**分析器，D20.8 #4；`analyze.sh` 自带 build）
→ `effective-lines.py` **四个**路径；`selftest.sh` 对分析器**不适用**（写明理由）。

## E4-a 的细节（本批最重，先做对机制）

1. **`make_tree.py`**：生成**新契约**合成树（`--undecodable N`、`--truncated-tcp N`、`--truncated-dns N`），
   形状必须与 E1–E3 之后的真实记录一致（键集/三态/条件块）；生成后**冻结**为
   `benchmarks/WinForward.E2E.Analysis/verification/synthetic-tree.tar.gz`，并写"改动即需重新冻结 A"。
2. **最小改动 Python 参考**：只改 `dig` 路径与 key 常量 + `generated_by` 中性化（D6.4），
   统计/判定/渲染逻辑**一行不动**；改动清单逐条落 `research/python-oracle-changes.md`；冻结
   `verification/golden/py-tables.md` 与 `py-verdict.json`（= 冻结的 A）。
3. **`scripts/oracle-diff.py`**：固定解压到 `/tmp/wf-synth/raw`；按 `^## (\d+)\.` 切 `tables.md`、
   按 top-level key 切 `verdict.json`；`BATCH_SECTIONS` 映射写在脚本里；**切片缺失 = 报错**；
   未实现小节用 `<!-- TODO(batch N) -->` 占位；**不允许**"整文件 diff 为空"当批 1–4 的判据。
   自检：对"只写 TODO 的骨架"必须报缺失；对"故意改一个数字"必须红。
4. **骨架**：`benchmarks/WinForward.E2E.Analysis`（`Loading/ Model/ Stats/ Checks/ Metrics/ Findings/
   Tables/ Verdict/`，每文件 ≤400 行）入 `WinForward.slnx`、引用 `Contracts`；CLI 参数与 Python 版一致
   （`--raw --out --ledger --flat --warmup-seconds --resamples --seed`）；`scripts/analyze.sh` 薄封装；
   `plots/SKIPPED.md`。

## 证据与落盘

| 产物 | 位置 |
|---|---|
| oracle 改动清单 | `research/python-oracle-changes.md` |
| 冻结物 | `benchmarks/WinForward.E2E.Analysis/verification/{synthetic-tree.tar.gz,golden/*,synthetic/make_tree.py}` |
| 每批 diff | `research/baseline/E4{b1..b5}-oracle.md`（含 `--batch N` 的命令与空 diff 证据） |
| 收尾 | `research/semantic-fixes/index.jsonl` 的 `E4-*` 条目 |

## 风险与回退

| 风险 | 缓解 |
|---|---|
| 重写规模大（6053 行） | 按表格分批，批 3 先覆盖绝大多数结论；每批独立可交付 |
| Python 参考被"顺手改进" | 只允许字段名与 `generated_by`（D6.4）；清单逐条落盘；oracle 是唯一判据 |
| 骨架期"空切片=通过" | `oracle-diff.py` 对缺切片**报错**，并在 E4-a 用自检验证 |
| 冻结树与真实记录漂移 | `make_tree.py` 的形状由 E1–E3 的形状测试同源（复用 `tests/` 的声明表） |
| 删 `analyze.py` 太早 | D6.6：**oracle 全 5 批通过后**才删，且依赖 git 历史 |

**回退点**：E4-a（机制）→ E4-b1…b5（逐批）→ E4-c（caveat）→ E4-d（删除）。
