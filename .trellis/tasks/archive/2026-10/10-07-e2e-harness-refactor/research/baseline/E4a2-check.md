# E4-a2 check 轮：独立复核、反证与修复

复核对象：**未提交**的 E4-a2 工作树（HEAD `979e27a`），`benchmarks/WinForward.E2E/scripts/oracle-diff.py`
337 → 1265 行。check 轮**未碰** `src/`、`benchmarks/WinForward.E2E.Analysis/verification/**`（含冻结物与分析器源码）、
`BATCH_SECTIONS`，**未 commit**。判据全部由 check 轮自己造变异重跑；`E4a2-semantic-mode.md` 只在
"作者声称什么"的意义上被逐条对照（对照结论见 §5）。

权威：`design-decisions.md` **D21**（语义等价；D21.1 的相对 ε 被本轮裁定"参考侧打印精度末位 1 个单位"取代）、
**D20.2/D20.3**（三态与切片机制保留）、D20.7/D20.8（批次表与 `metrics` 拆分）、`index.jsonl` 的 `E4-a2` 条目
（判据的逐字来源）。

**结论一句话**：`E4-a2` 声称的语义判据**逐条复现**——末位 1 单位的整数例外、分点串标识符、派生行键、
`thresholds` 逐 token 精确与措辞键数字容差、§7.2 删标题 rc=2、§7.4 byte 模式两处盲区、顶层键集的批内作用域，
以及 43/43 自检矩阵与七批端到端状态（`1a`/`1b` 两模式 rc=0，其余 rc=2）全部由 check 轮**另造变异**独立得到同一结果；
六道门禁在修复后的树上全绿。check 轮**修掉 3 处基础设施缺陷**（同一类：冻结物损坏时进程以**追踪栈退 1**，
正是作者本轮修掉"缺 `plots-SKIPPED.md`"时点名的违规），并**改正证据文档两处数字/状态**。

修复前后哈希（`sha256sum`，check 轮的两次探测都在**独立副本**里跑，真冻结物全程只读）：

| 状态 | `benchmarks/WinForward.E2E/scripts/oracle-diff.py` |
|---|---|
| 作者交付（check 前） | `161fca21cbe6727e7771e4c6ee4f6410141e69248c5603581881a72641012927` |
| check 修复后（最终树 = 门禁所测） | `a02819a66f3b470e113da7a587002258264f6f1b857783c1a569dd9b643c4bf8` |

修复只动 3 个函数的异常出口，**不动**任何比较语义、`BATCH_SECTIONS`/`BATCH_ALIASES`/`ALL_SECTIONS`/
`ALL_VERDICT_KEYS`（与 HEAD 逐字节相同，`sed -n` 取块后 `sha256sum` 比对）。证据：修复前后
43/43 自检矩阵输出**逐字节相同**，§1 的七批端到端状态与 §2 的全部突变结论**一条未变**。

## 1. 判据可复现（check 轮自跑，最终树）

端到端（`--cs-out` 省略 ⇒ 解压冻结树并跑分析器）：

| 批次 | semantic rc | byte rc | check 轮实测（semantic） |
|---|---|---|---|
| `1a` | **0** | **0** | 7/7 切片相等 |
| `1b` | **0** | **0** | 2/2 切片相等 |
| `1c` | 2 | — | 3 切片：2 differ、1 missing |
| `2` | 2 | — | 4 切片：2 differ、2 missing |
| `3` | 2 | — | 18 切片：3 differ、15 missing |
| `4` | 2 | — | 11 切片：5 differ、6 missing |
| `5` | 2 | — | 6 切片：3 differ、3 missing |
| 默认（全批次） | 2 | 2 | 51 切片：15 differ、27 missing；`differences: 16 structure, 14 value, 7 missing` |

与作者 §4 的表**逐格一致**（含"51 切片 / 27 缺 / 7 个缺键"）。冻结 golden 对自身在 `--tolerance 0` 下
仍 **rc=0**（三个顶层键的切片全等），说明"相等即短路"没有把 `600.0` vs `600` 这类**同值不同打印**误判成差异。

43 突变自检矩阵由 check 轮重跑：`/tmp/e4a2/selfcheck.py` ⇒ `SELF-CHECK OK`，总表与
`/tmp/e4a2/selfcheck-matrix.txt` **逐字节相同**。check 轮另造 **79 条**变异与负控
（`v1`/`v1b`/`v2`/`v2b`/`v3`/`v4`/`v5`/`v6` = 16 + 7 + 9 + 5 + 16 + 9 + 5 + 12），case 全部自建；
与作者矩阵重合的只有几处不可避免的判据点本身（末位 1 单位的正/负控、整数差一、删行/删切片、行序与键序），
作者未覆盖的有：§0.4 与 §2 的行键行为、合成表触发的 `[#N]` 兜底、`thresholds` 的 5 种拼写、
4 个措辞键 + `notes` 的单元断言、键集作用域的 5 组、§7.2 的编号撞车，以及 12 条基础设施负控（§3）。

## 2. 逐条独立验证（每条：自造变异 → 期望 → 实测）

计数三元组写作 `(rc, structure, value, missing)`。

### 2.1 "末位 1 个单位"的语义（自造变异）

| 变异（产侧副本，冻结物只读） | 期望 | 实测 |
|---|---|---|
| `< 3/6000 = 0.0500 %` → `< 3/6001 = 0.0500 %`（§8） | 红（界内两侧皆整数） | `(1,0,1,0)` 点名 `[row=wf-aot-opt&arm=LOSS].corruptRate` |
| `6000 [6000–6000] (n=3)` → `6001 …`（§8） | 红（计数器差一） | `(1,0,1,0)` 点名 `.sent` |
| `453.8 […] us (n=3)` → `(n=4)`（§5） | 红（pass 数差一） | `(1,0,1,0)` 点名 `.p50Us` |
| `(n=2 of 3; 1 unavailable)` → `(n=2 of 4; …)`（§4、§8 各一条） | 红（`of N` 的 N 差一） | `(1,0,1,0)` 两条 |
| `verdict.json` 的 `"records": 7306` → `7307`（ledger） | 红（JSON 整数） | `(1,0,1,0)` |
| `"harness-error": 8` → `9`（findings_by_severity） | 红 | `(1,0,1,0)` |
| `"resamples": 10000` → `10001`（bootstrap 精确整数） | 红 | `(1,0,1,0)` |
| `"resamples": 10000` → `10000.0` | 红（structure：整数承诺） | `(1,1,0,0)` |
| `453.8 […]` → `453.9 […]`（参考打印 1 位小数） | **绿**（末位 1 单位） | `(0,0,0,0)` |
| 同一条 + `--tolerance 0` | 红（容差确实是它在放行） | `(1,0,1,0)` |
| `10400`（参考整数）→ `10400.4`（产侧小数，1 单位内） | **绿**（参考自己取整过） | `(0,0,0,0)` |
| `10400` → `10405` | 红（超出 1 单位） | `(1,0,1,0)` |
| `0.0167 [0.0083–0.0250] %` → `0.017 […] %` | **绿**（产侧打印更粗，同一值） | `(0,0,0,0)` |
| `8.000e-03 < 1e-06` → `0.008 < 1e-06` | **绿**（同值不同拼写） | `(0,0,0,0)` |
| `0.0167 [0.0083–0.0250] %` → `0 […] %` | **红**（产侧抹掉参考的小数） | `(1,0,1,0)` |
| `"proxied_latency_p50_us": 647.575` → `0` | **红**（同上，JSON 侧） | `(1,0,1,0)` |
| `192.168.77.2:51234` → `…:51235`（§14.2 单元格） | 红（分点串=标识符） | `(1,0,1,0)` 点名 `.endpoints` |
| `192.168.77.2:40055` → `192.168.77.3:40055`（末位八位组） | 红 | `(1,0,1,0)` |
| 同一分点串出现在 §0.4 的 `detail` 里 | 红（自由文本里也是标识符） | `(1,0,1,0)` |
| 同一分点串出现在 `verdict.json:findings[14].detail` 里 | 红（措辞键的数字容差不放过它） | `(1,0,1,0)` |

"两侧都打印成整数 ⇒ 精确"与"参考取整留 1 单位"这两条**同时**成立，与 `index.jsonl` 的 `E4-a2` 条目逐字一致
（该条写作"参考把小数取整成整数则留 1 单位（`20` vs `20.4` 绿），产侧把参考打印的小数抹成整数则只认相等
（`0.0004` vs `0` 红）"）。注意本 check 轮任务书把前者写成"参考打印小数、产侧取整…⇒ 绿"，与 `index.jsonl`
**方向相反**；实测按 `index.jsonl`（也是 `E4a2-semantic-mode.md` §2.1 的表）执行，**判为正确**，见 §4 报备。

### 2.2 派生行键（三张重复行表 + `[#N]` 兜底）

check 轮先用**自己写的解析器与自己的规则**（不 import 被测模块）重算 40 张表的键宽，再与被测模块的
census 逐表比对：`(行数, 列数, 键宽)` 40/40 **完全相同**，`§0.4=kind&scope`、`§2=pass&run&label`、
`§14.2=pass&run&arm` 三处与作者 §2.2 一致；**冻结 golden 上没有任何一张表触发 `[#N]` 兜底**。

| 变异 | 期望 | 实测 |
|---|---|---|
| §14.2 三条重复行之一改 `10400.0` → `10100.0` | 该行键上的 value | `(1,0,1,0)`，路径含 `[pass=pass1&run=control-post&arm=BASE].ledger datagrams` |
| §14.2 删一行 | rc=2 | `(2,0,0,1)`，`[missing] …[pass=pass1&run=control-post&arm=BASE]` |
| §14.2 复制一行 | rc=1 structure | `(1,1,0,0)`，`expected <absent> vs actual 'pass1 \| control-post \| …'` |
| §14.2 交换两行 | **rc=0** | `(0,0,0,0)`（byte 模式同一变异 `(1,0,1,0)`，见 §2.4） |
| §0.4 `harness-error` 三条 `lane-witness-zero` 共享 `kind+scope`，改其中一条 `detail` 的 `(=0)` → `(=1)` | 该行键上的 value（不是 missing+extra） | `(1,0,1,0)`，`[kind=lane-witness-zero&scope=pass1/proxifier MIX].detail` |
| §0.4 交换两行 | rc=0 | `(0,0,0,0)` |
| §2 `Per-run metadata` 三条重复行之一 `60.0` → `61.0` | 该行键上的 value | `(1,0,1,0)`，`[pass=pass1&run=proxybridge/proxied&label=proxybridge-dual-proxied].wallSeconds` |
| §2 交换两行 | rc=0 | `(0,0,0,0)` |
| §0.4 删掉三条共享键行之一 | rc=2 | `(2,0,2,1)`——**多出 2 条 best-match 级联 value**，见 §4 |

`[#N]` 兜底用**合成表**验证（冻结 golden 上不可达）：给两侧的 `tables.md` 末尾追加一张 6 列、前 5 列全同、
只有末列不同的表（最短唯一前导列 = 整行 ⇒ 键退化），再对产侧做三种编辑；同样追加一张首列叫 `pass`
的表作对照（键宽 1），以及一张首列值 130 字符的表（触发 120 字符标签上限）：

| 合成表 | 变异 | 实测 |
|---|---|---|
| 退化键（6 列） | 末列 `2` → `3` | `(2,1,0,1)`：`[missing] …[#1]` + `[structure] …[#1]`（键即整行，改一格就换了键） |
| 退化键 | 删该行 | `(2,0,0,1)`，`[missing] …[#1]` |
| 退化键 | 复制该行 | `(1,1,0,0)`，`[structure] …[#2]` |
| 键宽 1（首列 `pass`） | 末列 `2` → `3` | `(1,0,1,0)`，`[pass=b].zeta` |
| 130 字符标签 | 末列 `2` → `3` | `(1,0,1,0)`，`[#1].zeta`（标签被截成 `#N`，判据不变） |

结论：`[#N]` **只改标签**；判据始终是"行集合相等 + 逐格比较"。键退化成整行时判据仍是同一套，只是
"改一格"必然改变键 ⇒ missing+extra（rc=2，**不会误判为通过**）。冻结 golden 上不触发，登记为 §4。

### 2.3 `thresholds` 逐 token 精确 vs 措辞键数字容差

| 变异 | 期望 | 实测 |
|---|---|---|
| `thresholds.latency` `"5 %"` → `"6 %"` | 红 | `(1,0,1,0)` |
| `"5 %"` → `"5%"` | 绿 | `(0,0,0,0)` |
| `"5 %"` → `"5  %"` | 绿（空白折叠） | `(0,0,0,0)` |
| `"5 %"` → `"5.0 %"` | 红（逐 token，无数字容差） | `(1,0,1,0)` |
| `thresholds.note` 的 `0.05` → `0.06` | 红（阈值块整体精确） | `(1,0,1,0)` |
| `thresholds.note` 的 `Holm\u2013Bonferroni` 写成字面 en dash | 绿（解码后同字符） | `(0,0,0,0)` |
| `findings[0].detail` `directLeak=2:` → `=3:` | 红（两侧皆整数） | `(1,0,1,0)` |
| `findings[0].detail` `intercepted 2` → `2.4` | 绿（参考取整留 1 单位） | `(0,0,0,0)` |
| `status_reason` 的词 `LAT` → `LOSS` | 红（词必须相同） | `(1,0,1,0)` |
| `control_blocks…reason` 的 `0.50` → `0.51` | 绿（末位 1 单位） | `(0,0,0,0)` |
| 同一条 `0.50` → `0.60` | 红 | `(1,0,1,0)` |
| `bootstrap.method` `95 %` → `95.4 %` | 绿（bootstrap 散文走容差） | `(0,0,0,0)` |
| 同一条 `95 %` → `96 %` | 红（整数照旧精确） | `(1,0,1,0)` |
| `generated_by` `v1` → `v2` / → `v1.0` | 各红（非措辞串逐 token） | `(1,0,1,0)` 两条 |

措辞键表本身用单元级断言复核（`WORDING_KEYS = {detail, notes, reason, status_reason}`）：
`{"notes": "x 3 y"}` vs `"x 3.4 y"` ⇒ 0 差异；vs `"x 4 y"` ⇒ 1 差异；`note`（单数，即 `thresholds.note`）
与任意非措辞键在两个方向上都严格。冻结参考**没有** `notes` 成员（23 `detail` / 189 `status_reason` /
549 `reason` / 0 `notes`），`notes` 的容差在本批参考上不可达，只能由单元断言覆盖——已登记。

### 2.4 §7.2 与 §7.4：两处"刻意不等于字面读法"

| 变异 | 期望 | 实测 |
|---|---|---|
| 删掉整行 `## 7. Memory detail` | rc=2 | `(2,2,1,0)`：既有 `[structure] tables.md:headings`（编号集少 7）、又有 `tables.md:7: MISSING (produced side)`，且 §6 吸收了 §7 的正文（blocks/prose 差异） |
| `## 7. Memory detail` → `## 7. Cache detail` | rc=1 structure | `(1,1,0,0)`，点名 `tables.md:7.title`，尾行 `every slice is equal but the file's own structure differs` |
| `## 7.` → `## 6.`（编号撞车） | rc=2 | `(2,13,1,57)`：编号集 `…6 6 8…`，§7 缺、§6 被覆盖 ⇒ 绝不放过 |
| 顶层键换序（`row_profiles` 提到最前） | byte rc=0 | `(0,0,0,0)`；semantic 同为 0 |
| `verdict.json` 里 `\u2013` 改成字面 en dash | byte rc=0 | `(0,0,0,0)`；semantic 同为 0 |
| 交换 `bootstrap` 两个成员 / 交换表内两行 / 表内多一个空格 / 末位差一 | byte rc=1 | 各 `(1,0,1,0)`；semantic 各 `(0,0,0,0)` |

**判断：两处都接受，不需改。**
① 删标题 ⇒ rc=2 由 D20.2（"`2` 应存在的切片缺失"）、D21.1（"**缺失切片仍是 rc=2**（该机制保留）"）与
`implement.md` 的"删一个应存在切片 ⇒ rc=2"三处**明文**要求；"删/改标题"那一行的一行话只在"改"（rc=1）上
可字面执行，"删"必然删掉切片。报告同时给出结构行与缺失切片，rc=2 胜出，与 b1a check 轮对同一变异的实测一致。
② byte 模式的两处盲区来自"按顶层键切片 + 逐片规范化重新序列化"，与被保留的 E4-a 版本**同一机制**，
而 D21.2 给 byte 模式的定位正是"键集/表头/标题等结构面，以及需要'确实没变'的回归场景，不再作为批次判据"：
顶层键序与转义拼写恰好是 D21 明说**无所谓**的两件事（D21.1"键序无关"、D21.4"只要解析后的值在容差内一致"），
所以盲区不构成缺口；byte 模式真正承担的结构面（键集、表头、标题、行序、空白、数字）逐条实测都**能**抓到。
若要"真·逐字节"，需要换切分方式（按顶层键取原文），那是设计改动，本批不做。

## 3. check 轮修掉的问题（3 处，同一类；只改异常出口）

作者本轮修掉"冻结 `plots-SKIPPED.md` 缺失 ⇒ `FileNotFoundError` 退 1"这一真漏洞后，脚本自己写下的契约是
"基础设施失败不得报成内容差异"（模块 docstring 第 74–75 行、退出码 `2` 的定义）。check 轮按同一句话扫描
**其余冻结物读取点**，发现三处**同类**未堵：进程以追踪栈退 **1**（= "内容不同"）。三处都在**独立副本**里
先复现（`/tmp/e4a2-check/probe/repo/`，与真仓库同层级的假 REPO_ROOT，冻结物以符号链接只读挂入），
修完后原样重跑变 rc=2；真冻结物与真仓库全程未被写入。

| # | 症状（修前） | 原因 | 修法 |
|---|---|---|---|
| F1 | 冻结 `synthetic-tree.tar.gz` 缺失 / 不是 gzip / 是目录 ⇒ `FileNotFoundError` / `tarfile.ReadError` / `IsADirectoryError`，rc=1 | `extract_tree()` 的 `tarfile.open` 无保护；每个**不带 `--cs-out`** 的批次运行都会走到 | `try/except (OSError, tarfile.TarError)` ⇒ `fail(...)` |
| F2 | 冻结 `golden/py-verdict.json` 截断 / 非 UTF-8 ⇒ `JSONDecodeError` / `UnicodeDecodeError`，rc=1 | `metrics_members()` 的 `json.loads(path.read_text(...))` 无保护；该函数是**本批新加的** `check_coverage` 牙齿，且在 `main()` 里第一个被调用 | 同上一行，改 `fail(...)`；docstring 的"not readable yet"改成"not there"（缺文件仍返回 `None`，交给既有的 MISSING 路径报 rc=2） |
| F3 | 冻结 `plots-SKIPPED.md` 存在但不是合法 UTF-8 ⇒ `UnicodeDecodeError`，rc=1 | `check_plots()` 只保护了"缺失"，`PLOTS.read_text` 本身无保护 | `try/except (OSError, UnicodeDecodeError)` ⇒ `fail(...)` |

修复前后（同一批探测，假 REPO_ROOT，其余输入不变）：

| 探测 | 修前 | 修后 |
|---|---|---|
| 冻结 tarball 缺失 | rc=1 `FileNotFoundError` | rc=2 `…/synthetic-tree.tar.gz: the frozen tree cannot be unpacked: [Errno 2] …` |
| 冻结 tarball 内容不是 gzip | rc=1 `ReadError: not a gzip file` | rc=2 同上（`not a gzip file`） |
| 冻结 tarball 是目录 | rc=1 `IsADirectoryError` | rc=2 同上 |
| 冻结 `py-verdict.json` 截断 | rc=1 `JSONDecodeError` | rc=2 `…/py-verdict.json: the frozen reference is not readable JSON: …` |
| 冻结 `plots-SKIPPED.md` 非 UTF-8 | rc=1 `UnicodeDecodeError` | rc=2 `…/plots-SKIPPED.md: the frozen plots text is not readable UTF-8: …` |

**回归证据**：修复前后 ①43/43 自检矩阵输出逐字节相同（`diff` 无输出）；②§2 的 79 条变异/负控结论一条未变；
③七批端到端状态逐格相同；④`--batch 1a`/`1b` 两模式仍 rc=0。冻结物、分析器源码、`BATCH_SECTIONS` 未被触碰。

顺带修正作者证据文档两处（`E4a2-semantic-mode.md`，未提交的新文件）：

1. §8.1 说"`--batch 1a` 仍能在**缺 11 个键**的树上 rc=0"——"11"是 b1a 时代（只写 3 个顶层键）的旧数；
   b1b 落地后该树发布 **7** 个键（`generated_by/raw/flat_mode/passes/rows/bootstrap/thresholds`），
   缺的是 **7** 个，与它自己 §4 写的"7 missing keys"一致。已改 11 → 7。
2. 文件头的哈希表 + "哈希是自检矩阵与门禁所测的那一份"一句，补上 check 轮修复后的新哈希与"check 轮已据此重跑"。

## 4. 未修复但需上报（都不阻塞提交）

| # | 事实（实测） | 判断 / 建议 |
|---|---|---|
| U1 | **共享键组内删行会多报级联 value**：§0.4 三条 `lane-witness-zero`（键 `kind+scope` 不唯一）删一条 ⇒ `(2,0,2,1)`：剩下两条 expected 被 best-match 配到别的 produced 行，多出 2 条 value，缺的那条仍以 missing 报出。键唯一的组（§14.2/§2 的重复行）删行只报 1 条 missing | rc 与"哪一行缺"都正确，只是报告有噪声。要消噪需换配对算法（精确匹配优先占位 + 指派），属设计改动 ⇒ **登记不改**。若 b3/b4/b5 遇到共享键组里的删行噪声，再按需处理 |
| U2 | **键退化成整行时**（最短唯一前导列 = 全部列）改一格 ⇒ missing+extra（rc=2）而非 value（rc=1） | 冻结 golden 的 40 张表**没有**这种表（键宽最大 4，`[#N]` 一次不触发），未来新表才可能遇到；rc 仍是"不通过"，不会假绿 ⇒ **登记不改** |
| U3 | `numbers_equivalent` 用 `max(unit(expected), unit(actual))`：参考打印 13 位小数、产侧打印 1 位时，容差按**产侧更粗**的那一位（如 `640.9449999999999` vs `640.9` 会绿） | 这是作者 §2.1 明写的"coarser print bounds the comparison"（`0.0625` vs `0.062` 绿），语义是"两侧打印都与某个真值相容"。比"只按参考末位"更松，但**方向是 D21 允许的格式自由** ⇒ **接受**；已在本表登记 |
| U4 | **数组一律按序比较**（不只 `passes`/`rows`）：`findings` 换序会红 | 作者 §7.5 已自行登记（比 D21 更严）。当前无批次触发 ⇒ **接受** |
| U5 | byte 模式名为 byte、实为"逐顶层键规范化重序列化"（顶层键序与 `\uXXXX` 不可见） | D21.2 的定位就是"结构面回归"，两处盲区正是 D21 明说无所谓的东西 ⇒ **接受**（判断见 §2.4） |
| U6 | `notes` 措辞键在冻结参考里不存在（0 处） | 单元断言已覆盖，机制正确；等有 `notes` 的参考出现才会走到 ⇒ 登记即可 |
| U7 | `D20.7` 的 A2-5 行写"oracle 不比对 `plots/`"，而 E4-a 与 `index.jsonl` 的 `E4-a2` 条目都要求"与冻结文本逐字一致（缺 ⇒ rc=2、改 ⇒ rc=1）" | 实现按后者（更晚、更具体）；建议在 `design-decisions.md` 里把 A2-5 那半句标为 supersede，避免 b3–b5 误读 ⇒ **上报，不改代码** |
| U8 | 任务书把"参考把小数取整成整数则留 1 单位"写成"参考打印小数、产侧取整留 1 单位内 ⇒ 绿"（方向相反） | 实测按 `index.jsonl`（`20` vs `20.4` 绿；`0.0004` vs `0` 红）。若原意真是"产侧取整也绿"，那是**判据变更**而不是缺陷 ⇒ 上报给裁定人 |

## 5. 门禁（最终树，check 轮自跑，逐条串行；日志 `/tmp/e4a2-check/gates/`）

| 门禁 | 结果 |
|---|---|
| `dotnet build WinForward.slnx -c Release` | rc=0，**0 Warning / 0 Error** |
| `dotnet test WinForward.slnx -c Release -m:1` | rc=0，14 个程序集汇总 **1655 passed / 0 failed / 0 skipped**，无 `error ` 行 |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0，**0 字节输出** |
| `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx` | rc=0；`ElementTree` 解析：**`<Issue>` 0、`<IssueType>` 0** |
| `effective-lines.py` 四个路径 | rc=0，**0 字节输出**（该脚本只 `rglob("*.cs")`，见 `effective-lines.py:182-188`；本批只改 Python，故必然不动——仍照跑） |
| oracle（`1a`/`1b` 两模式 + 七批 + 全批次） | **rc=0 / rc=0 / 其余 rc=2 / 全批次 rc=2**，见 §1 |

## 6. 规范

- `git status --short benchmarks/WinForward.E2E.Analysis/verification` **为空**：tarball、两份 golden、
  `plots-SKIPPED.md`、`make_tree.py`、`check-fixture-drift.py`、三份向量表都未被本批或 check 轮改动。
- `git status --short benchmarks/WinForward.E2E.Analysis/` 为空：分析器源码一行未动。
- `BATCH_SECTIONS`/`BATCH_ALIASES`/`ALL_SECTIONS`/`ALL_VERDICT_KEYS` 与 HEAD 逐字节相同（取块后 `sha256sum`）。
- `git ls-files '*.pyc'` 只有 `benchmarks/results/2026-10-06-e2e-competitors/analysis/__pycache__/analyze.cpython-314.pyc`
  这一枚已登记的债（E4-d 处理）；`benchmarks/WinForward.E2E/scripts/__pycache__/` 由 `.gitignore:28 __pycache__/` 覆盖，
  未新增被跟踪文件。（check 轮为语法自检跑过一次 `py_compile`，它刷新了该目录下 `oracle-diff.cpython-314.pyc`
  这一**忽略**文件；内容与最终源码一致，`git status` 无变化。）
- 本批未 commit；`src/` 未动。

## 7. 复现（check 轮的装置，全部在 `/tmp/e4a2-check/`，真仓库只读）

```bash
# 判据：七批 + 全批次 + byte（§1）
for b in 1a 1b 1c 2 3 4 5; do python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch $b --mode semantic; done
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic          # 51 切片 / 27 缺 / 7 缺键
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a --mode byte   # rc=0
# 作者的自检矩阵（43/43，§1）
python3 /tmp/e4a2/selfcheck.py && diff /tmp/e4a2/selfcheck-matrix.txt /tmp/e4a2-check/selfcheck-rerun.txt
# check 轮自造的 79 条突变/负控（§2、§3）：harness.py + v1/v1b/v2/v2b/v3/v4/v5/v6.py
python3 /tmp/e4a2-check/v1.py   # 末位 1 单位、整数例外、分点串
python3 /tmp/e4a2-check/v2.py   # §14.2 / §0.4 / §2 的行键、删行、增行、换序
python3 /tmp/e4a2-check/v2b.py  # 合成表触发 [#N] 兜底
python3 /tmp/e4a2-check/v3.py   # thresholds 精确 vs 措辞键容差
python3 /tmp/e4a2-check/v4.py   # §7.2 删标题、§7.4 byte 盲区与长处
python3 /tmp/e4a2-check/v5.py   # 顶层键集的批内作用域
# 键宽的独立重算（§2.2）：自写解析器 vs 被测模块，40/40 相同
python3 /tmp/e4a2-check/v2_compare.py
# 基础设施负控（§3）：假 REPO_ROOT 副本，真冻结物只读挂入
python3 /tmp/e4a2-check/v6.py
# 六道门禁（§5）
bash /tmp/e4a2-check/gates/run-gates.sh
```

## 8. 附：E4-b1b 的 check 补记

`research/baseline/E4b1b-check.md` 原先不存在（b1b 的 check 轮只在会话里报了结论），本轮按任务书补写；
其中 RNG 一节不是转录而是**重新做了一遍**独立复算（100 个唯一 seed × (8 个原始字 + 11 个 `getrandbits` 宽度
+ 25 个 `randrange` 停止点) = **4400 次抽取，C# `CpRandom` vs CPython 3.14.7，4400/4400 相同**），
并用 CPython 重算了冻结的 `cp-random-vectors.json`（846/846 相同）。详见该文件。
