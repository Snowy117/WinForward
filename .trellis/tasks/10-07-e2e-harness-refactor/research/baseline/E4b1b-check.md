# E4-b1b check 轮（补记）：独立复算与观察清单

**补记说明**：原 `E4b1b-check.md` 从未落盘（b1b 的 check 轮只在会话里报了结论）。本文件由 **E4-a2 的 check 轮**补写，
磁盘来源只有 `semantic-fixes/index.jsonl` 的 `E4-b1b` 条目与 `E4b1b-oracle.md`；凡写"实测"的都是补记轮自己重跑的。

## 1. 判据三项结论（补记轮重跑；树 = HEAD `979e27a`）

- **① 判据**：`--batch 1b` **rc=0**（2/2 切片，semantic 与 byte 各一次；`--batch 1a` 两模式亦 rc=0）。这一项同时是
  **E4-a2 验收的前提**（D21 保留 `bootstrap`/`thresholds` 两切片），故为两批共用判据。
- **② 三态**：`resamples` 10001 ⇒ **rc=1**（`(1,0,1,0)`，点名 `bootstrap.resamples`）；删 `bootstrap` 键 ⇒ **rc=2**（`(2,0,0,1)`）；
  `1a` rc=0；`1c/2/3/4/5` 各 rc=2（differ/missing = 2/1、2/2、3/15、5/6、3/3）。
- **③ 黄金向量**：`--filter FullyQualifiedName~Analyzer` **34/34 绿**；846 = 18×47；404 = 230 `%.*f` + 135 `%.*g` + 39 `repr`
  （36 finite + 3 named）；JSON 向量 12 份。

## 2. RNG 独立复算（补记轮新做，非转录）

- **冻结向量再推导**：按 `make_cp_vectors.py` 声明的 18 seed / 11 个 `getrandbits` 宽度 / 28 个 `randrange` 停止点，
  用 CPython **3.14.7** 重算全表比对 `golden/cp-random-vectors.json`：**846/846 相同**（`/tmp/e4a2-check/rng_independent.py`，`python3 -B`）。
- **另造 4400 次抽取**：100 个唯一 seed（18 个声明 seed + 10^k/2^k/2^k−1/3^k/i·7919，全在 64 位内）× 44 抽
  （8 `NextUInt32` + 11 `GetRandBits(宽度)` + 25 `RandBelow(停止点)`，停止点取拒绝采样形状 1…1025）。C# 侧由 `/tmp/cp-probe`
  （net10.0 控制台，只 `Reference` 已构建的 `WinForward.E2E.Analysis.dll`，不碰仓库）驱动 `CpRandom`，CPython 侧同序列：**4400/4400 相同**（`/tmp/e4a2-check/rng_4400.py`）。

## 3. O1–O7 观察清单

**编号来源**：原会话的 O1–O7 不在磁盘上（`trellis mem` 无 2026-10-08 会话，任务目录内 `rg 'checkout'` 无命中）。下表按
`E4b1b-oracle.md` **§9.1–§9.7** 编号，每条都能在该节找到原文；若原会话的编号是别的条目，以原会话为准。

- **O1**（§9.1）采样器（`bootstrap_pair`/`holm_adjust`/`decide`）延到批 3/4，`BootstrapResampler.cs` 每次运行打印
  `not yet rendered: 3/4 …` ⇒ 批 3/4 必须消费它，否则延宕变成永久缺省。
- **O2**（§9.2）两个文本基元按 D20.5 移到 `Json/`；`E4b1a-oracle.md` §1 的表仍写 `Tables/`、`Verdict/` ⇒ 按"b1a 当时的事实"读它。
- **O3**（§9.3）`CpRandom`/`VerbatimNumber`/`VerbatimJson` 三型 `public` + 测试工程加 `ProjectReference`，不加 IVT（D20.6）⇒ 公开面到此为止。
- **O4**（§9.4）`--resamples 0` 是 C# 单侧断言：参考 `ZeroDivisionError`、两份文件都不写（D20.1）⇒ 边界树只对 C# 产物定点断言。
- **O5**（§9.5）`python-oracle-changes.md` §5.5 两处数字归属错，更正落在 `E4b1b-oracle.md` §3.2，不回改那份文档 ⇒ 别再"修"回去。
- **O6**（§9.6）端口取 64 位 seed、最多抽 64 位；`RandBelow` 是 `long` 而非 `int`（为钉 2^31±1 宽度）⇒ 后续不要"简化"成 `int`。
- **O7**（§9.7）本批 `verification/` 不是逐字节不变（新增 3 份向量 + 生成器 + `FROZEN.md` §1.1）；tarball、两份 golden、
  `make_tree.py`、`check-fixture-drift.py`、`plots-SKIPPED.md` 未动 ⇒ A 未重冻，批 1–5 的旧 diff 仍有效。（§9.8 另记：参考只以
  `python3 -B`/`PYTHONDONTWRITEBYTECODE=1` 跑过；唯一被跟踪的 `analyze.cpython-314.pyc` 未改。）

## 4. 那次 `git checkout --` 事故

**磁盘上查不到任何记录**（无 `checkout` 命中、`E4b1b-oracle.md` 未提、`trellis mem` 无当日会话），故事故经过与当时的恢复
哈希在本轮**无法复述**——它们只存在于 b1b check 轮的会话里。可核验的替代：**当前内容就是恢复后的内容，且已入库**
（`git status --short benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests` 为空 = 与 HEAD `979e27a` 逐字节相同；
抽查 `git show HEAD:<path> | sha256sum` == 工作树），破坏性 `checkout` 再吃不到它，§1 的门禁与 34 个事实正是在这份内容上跑绿的。

| 文件（b1b 面，恢复后 = HEAD blob） | sha256 |
|---|---|
| `Stats/CpRandom.cs` | `a200be84a6cc9f8c55f08b598e8f396a52c868be4a55dad8060e289d1eecea0a` |
| `Json/VerbatimNumber.cs` | `e4cfd7addb301bb6e5beaeb70c52bfb28c806a3f48004ee4ce18e5a62e84dd41` |
| `Json/VerbatimJson.cs` | `e8c2a9b9919e7347fb467498baf5df29205c354402cd3b4f656ca7a587daf7b0` |
| `Verdict/VerdictSections.cs` | `ba91a234d34e53c622144169a06158cb8a91cb0a97fb43070ff2271884e79867` |
| `golden/cp-random-vectors.json` | `b011711e290a744827edb9ae7433304f29770619885cd7d4b953a0307bab2690` |

其余 b1b 面（`Stats/BootstrapResampler.cs`、`synthetic/make_cp_vectors.py`、`py-number-vectors.json`、`py-json-vectors.json`、
`FROZEN.md`、三个测试文件、`RepoPaths.cs`、`CampaignModel.cs`、`CampaignLoader.cs`、`AnalysisRunner.cs`、测试 csproj）同源：
`git show HEAD:<path> | sha256sum`。**若原会话的恢复哈希与本表不同，以原会话为准并回报**——本表证明"内容 == HEAD"，
不是"内容 == 事故前那一刻"。

三项判据、34 个黄金事实、846 个冻结 RNG 值与另外 4400 次自造抽取全部独立复现；O1–O7 已按磁盘来源登记；`checkout` 事故
经过不可考，后果已被 HEAD 提交消除。**b1b 无阻塞项。** —— 由 E4-a2 check 轮补记（同轮正文：`E4a2-check.md`），HEAD `979e27a`。
