# E4-b1a check 轮：独立复核、反证与修复

复核对象：**未提交**的 E4-b1a 工作树（HEAD `03cddb0`）。check 轮**未碰** `src/`、`verification/` 下的冻结物、
`benchmarks/results/…/analysis/analyze.py`（含那枚被跟踪的 `.pyc`），**未 commit**。判据、装置与结论全部由
check 轮自己重跑；作者 `E4b1a-oracle.md` 只在"作者声称什么"的意义上被逐条对照。
权威：`design-decisions.md` **D20**（D20.1/D20.2/D20.3/D20.5/D20.6/D20.7）、D6.4、`implement.md` 批次表、
`research/python-oracle-changes.md`。

**结论一句话**：判据（`--batch 1a` rc=0、其余五批全 rc=2）、逐字节切片、`RowCount` 两口径、`--flat` 与
相对 `--raw` 两条批外定点、六道门禁全部由 check 轮独立复现；**修掉 4 处读入侧缺陷**（2 个文件，每处都有
自造输入的微分反证与负控）：`pathlib` 的 glob **不隐藏点开头的名字**、glob **匹配目录条目而非只匹配文件**、
`_load_json_file` 的 **JSON `null` 与"没有值"同解**、`unreadable (<Exc>)` 的**异常名要按 CPython 打印**。
四处都不改变冻结树上的 rc=0（负控：还原任一处，`frozen` 仍绿、对应的自造输入变红）。**无阻塞项。**

修复后哈希（`sha256sum -c` 已核，还原自 `/tmp/e4b1a-check/*.fixed.cs`）：

| 文件 | sha256（修复后，= 最终树 / 门禁所测的那一份） |
|---|---|
| `benchmarks/WinForward.E2E.Analysis/Loading/PythonGlob.cs` | `a1596444c7f6e8ad8d0657156640e930b1932fc2af4313af972e5317914a0645` |
| `benchmarks/WinForward.E2E.Analysis/Loading/RunLoader.cs` | `f05a10292dd584e2dbf92142922e1926f73a669b6be0d2a938795133d458ef7d` |

（`RunLoader.cs` 在 §5 的四处修好后又改了一次**纯文档注释**：check 轮第一次跑 format 门禁时，
自己新写的那段 `<remarks>` 报 2 × `MA0154: Use langword in XML comment`（`<c>null</c>` 要写成
`<see langword="null"/>`）—— 这正是"门禁必须在最终树上重跑"的例证。改完重跑：format rc=0、0 字节输出；
四处负控也在这份最终源码上**重跑了一遍**（§5 的表就是重跑结果），还原后 `sha256sum -c` OK。）

项目有效行：**31 文件 / 1592**（作者记 1586，差 6 = 本轮两文件净增），最大仍是 `RunLoader.cs` **179**。

---

## 1. 判据可复现（check 轮自跑，最终树）

| 命令 | rc | 切片 |
|---|---|---|
| `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a` | **0** | `7/7 equal` |
| `--batch 1b` | **2** | `bootstrap`/`thresholds` 整键缺省（0 differ, 2 missing） |
| `--batch 1c` | **2** | §1/§2 还是 TODO（2 differ）+ `row_profiles` 缺省（1 missing） |
| `--batch 2` | **2** | §0/§3 占位（2 differ）+ 2 键缺省 |
| `--batch 3` | **2** | §5/§8/§9 占位（3 differ）+ 15 个 metrics 缺省 |
| `--batch 4` | **2** | 5 differ + 6 missing |
| `--batch 5` | **2** | 3 differ + 3 missing |

**三态**（在**最终产物**上做，走 `--cs-out`；每态一份拷贝）：

| 变异 | rc | 报出 |
|---|---|---|
| §15 某格 `1/1` → `2/1` | **1** | `tables.md:15: DIFFERS -- line 7: reference '… \| 1/1 \| …' != produced '… \| 2/1 \| …'` |
| `## 15. Data availability` → `## 15 Data availability` | **2** | `tables.md:15: MISSING (produced side)`；`compared 7 slice(s): 0 differ(ent), 1 missing` |
| 删 `verdict.json` 的 `rows` 键 | **2** | `verdict.json:rows: MISSING (produced side)`；`0 differ(ent), 1 missing` |

日志：`/tmp/e4b1a-check/checks/final[-2]-oracle-*.log`、`final-mut-{mutated,nosection,nokey}.log`。

## 2. 逐字节

| 切片 | 字节 | 行 | `cmp` |
|---|---|---|---|
| preamble（`## 0.` 之前） | 948 | 8 | **identical** |
| §15（`## 15.` … EOF） | 4282 | 33 | **identical** |

`verdict.json` 五键：

* **值级**：两侧 `json.loads` 后逐键相等（`generated_by`/`raw`/`flat_mode`/`passes`/`rows`）。
* **插入序**：值级比对抓不到顺序，故另写一个取**原始文本**顶层键的正则检查 —— 产生的键序
  `generated_by, raw, flat_mode, passes, rows` 恰是 golden 14 键声明序的前缀（作者自报点 ③ 成立）。
* **逐字节**：产生的全文 **340 B** == golden 前缀（截到它第一个未拥有键 `row_profiles` 之前）去掉那枚
  "因为后面还有 11 个键才存在"的分隔逗号、再补一个结尾换行 —— 逐字节相同。

批外但同属 1a 的两条：16 个 `## N.` 标题与 golden **逐字相同**、顺序相同；
`TablesWriter.s_sections` 的批次归属与 `oracle-diff.py` 的 `BATCH_SECTIONS` **16/16 一致**。

## 3. Loading 的语义等价（自造输入的微分对照）

**装置**：`analyze.py` 以 **只读**方式导入（`PYTHONDONTWRITEBYTECODE=1`，`analysis/__pycache__/analyze.cpython-314.pyc`
的 sha256 全程 `1af8aa27…` 不变），对同一棵树取参考的 `discover()`、`table_availability()`、
`ctx.pass_ids`/`ctx.row_ids`、`ledger_paths` 与每条行的 `arms`/`result` 数/lane 的 `run_id`，
与 C# 产物的 §15 切片、`verdict.json` 的 `passes`/`rows`、stdout 的每 pass 账本行比对。
被测树全部是**冻结树在 `/tmp` 的副本**（`/tmp/e4b1a-check/trees/…`），冻结物零改动。
**17/17 用例两侧全等**（§15 逐字节 + passes/rows + 账本序）。

| 用例 | 自造的输入 | 覆盖的规则 | 结果 |
|---|---|---|---|
| `frozen` | 冻结树原样 | 基线 | ok |
| `dual-owner-early` | pass1 的 dual 标签改成 `control-pre-dual-*` | 认领走行**提前**停 | ok |
| `dual-owner-late` | 标签改成 `wf-aot-nativeudp-dual-*` | 走行**推后**、更多行拿到副本 | ok |
| `dual-no-owner` | 标签不含 `-dual-` | owner=None ⇒ **所有**无 lane 行都拿到副本、且不重写 | ok |
| `roster` | `LAT` 的 `file` 改名、加无 `file` 的 `EXTRA`、同名 `DNS` 后者覆盖到缺失文件、加未登记的 `ZZZ.jsonl` 与 `.hidden.jsonl` | 名→文件、默认名、后者覆盖、`*.jsonl` 补名、隐藏名 | ok |
| `passorder` | `pass1/pass2/pass3` 重排成 `pass2/pass10/pass1`，加空 `passX` 与非 pass 的 `norows` | `natural_key` 目录序 + 「无行的 pass 不成 pass」 | ok |
| `ledger` | 树根/pass1 加 `target-ledger.jsonl`、`zz-ledger.jsonl`、`.hidden-ledger.jsonl`，pass1 放一个**目录** `ledger.jsonl` | 四个固定名 → glob、pass 目录→raw→raw 父、首见去重、`is_file()` 过滤 | ok |
| `ledger-override` | `--ledger` 给「存在的文件 + 不存在的文件 + 一个目录」 | 原样且"不存在即过滤"、替换而非扩展 | ok |
| `truncated-run` | pass1/proxifier 的 `run.json` 写成截断的 JSON（合法 UTF-8） | `unreadable (JSONDecodeError)` + `no run.json` | ok |
| `chmod-run` | 同上，`run.json` 置 mode 000 | `unreadable (PermissionError)` | ok |
| `hidden-arm-only` | 一个行的 `*.jsonl` 全部删掉、只留 `.only.jsonl`（且 roster 清空） | 隐藏名是普通名（§F1） | ok |
| `jsonl-directory` | 行内放一个**目录** `sub.jsonl` | glob 匹配条目（§F2） | ok |
| `null-truth` | `proxy-truth.json` 内容为 `null` | JSON `null` ⇒ "没有值"（§F3） | ok |
| `hidden-ledger` | pass1 只有 `.only-ledger.jsonl` | 隐藏账本被发现（§F1） | ok |
| `flat-single-row` / `flat-subdirs` / `flat-owner` | `--flat` 三种形态（单行目录 / pass 目录 / dual 目录） | 隐式 pass、只有一层的 `dual/` 不是行 | ok |

六条规则的落点（"可观测/仅源码级"分开写）：

| 规则 | 证据 |
|---|---|
| 目录序 `natural_key` | `passorder` 的 `passes == ['pass1','pass2','pass10']`（字典序会得 `pass1,pass10,pass2`），`rows` 与 §15 行序同步 |
| dual「逐行认领直到命名行」 | 四个用例 + 冻结树，**四种结局**（自有 lane / 拿到副本 / 是命名行而停 / 之后 `n/a`）都在 §15 里出现 |
| roster 名→文件 + `*.jsonl` 补名 | `roster`：`arms present` 与 `arms with a result` 逐字节相同 |
| `ARM_ORDER` 装载序 | **仅源码级**：`ArmRecords.LoadOrder` 与 `ARM_ORDER` 逐项相同、两段式装载（先 `ARM_ORDER` 后 `sorted(files)`）与参考同形。§15 只数数 ⇒ b1a 切片里不可观测（b3/b4 的表才读次序）；已对照源码确认 |
| `-dual-` 标签归属 + `run_id` 重写 | **归属可观测**（§15 的 `dual lanes` 列）；**重写仅源码级**（只有 §12/§13 读 lane 的 `run_id`）。参考在四个用例上算出的值已记在 `results-final.json` 的 `lane_run_ids`（如冻结树 pass1 的 `control-post`/`control-pre` 两个副本 lane 的 `run_id` 是 `proxybridge/proxied|direct`），供 b5 断言 |
| `--ledger` 原样 + 不存在的过滤 | `ledger-override`：两侧每个 pass 都只剩那一个存在的文件（不存在的与目录都被滤掉，且**不**回落去搜树） |

**两条"整跑"对照**（参考真的跑完整 `main()`，不是只调 `discover()`；`--resamples 2` 只为省 bootstrap 时间，
1a 的切片与它无关）：

| 场景 | 参考 vs 端口 |
|---|---|
| `--flat`（`--raw /tmp/wf-synth/raw/pass1/wf-aot-opt --flat`） | preamble **1033 B 逐字节相同**（含 `(flat mode: …)` 后缀）、§15 **795 B 逐字节相同**、五个键全等（`flat_mode: true`、`passes: ["flat"]`、`rows: ["wf-aot-opt"]`） |
| 相对 `--raw raw/`（cwd = `/tmp/wf-synth`） | preamble 逐字节相同（第 3 行 `Generated by \`e2e-analysis v1\` from \`raw\`.`）、§15 逐字节相同、`raw` 两侧都是 `"raw"`、五个键全等 |

## 4. `RowCount` 两口径

| 口径 | 值 | 落点 |
|---|---|---|
| §15 的行 | **27**（表头 + 27 = 28 行；与 golden 逐字节相同） | `CampaignModel.Rows`（逐 pass 展开的 loaded run） |
| `verdict.json` 的 `rows` | **9** | `CampaignModel.RowIds`（`ROW_ORDER` 秩 + `natural_key`） |
| stdout | `3 pass(es), 9 row(s), 2 ledger(s), 27 loaded run(s)` | `RowCount`=9（= `RowIds.Count`）+ 新的 27（仅 stdout） |

`RowCount` 的消费者只有 stdout 一行（`rg` 全域：`AnalysisRunner.cs:65`）；§15 只吃 `Rows`
（`TableAvailability.cs:56-57`）⇒ **`RowCount` 没有被喂给 §15**，作者自报点 ② 成立。
参考的 stdout 本是另一套文本（`N metric(s)`），stdout 不属契约（D20.7 / A2-9）。

## 5. 修掉的四处（每条：症状 → 原因 → 修法 → 负控 → 还原）

### F1 `Path.glob` 不隐藏点开头的名字（`Loading/PythonGlob.cs`）

* **症状**（`roster` 用例）：参考 `arms present` = `…, .hidden, …`（14 臂、13 有 result），端口 = 13 臂、12 有 result；
  `hidden-ledger` 用例：参考把 `pass1/.hidden-ledger.jsonl` 排在 `target-ledger.jsonl` 之后、`zz-ledger.jsonl` 之前，
  端口整个看不到它。`hidden-arm-only` 更重：参考 `| .only | 1/1 | … | .only |`，端口 `| none | 0/0 | — |`。
* **原因**：`glob.glob` 与 shell 才有"通配符不匹配点开头名字"的规矩；参考用的是 **`Path.glob`**，
  实测（CPython 3.14.7）`Path.glob('*.jsonl')` 返回 `.hidden.jsonl`。原注释把 `Path.glob` 的规则写错了。
* **修法**：删掉 `name.StartsWith('.')` 的跳过，并把类文档改成"通配符不隐藏点"。
* **负控 M1**：只把那条跳过加回去（`name.StartsWith('.') || !matches(name) → continue`）⇒ `roster`、`hidden-arm-only`
  （§15 变红）与 `ledger`、`hidden-ledger`（账本序变红）四处红，**`frozen` 仍绿** ⇒ 该修复不是 rc=0 的成因，是独立正确性修复。
* **还原**：`PythonGlob.cs` sha256 回到 `a1596444…`（`sha256sum -c` OK）。

### F2 `Path.glob` 匹配目录条目，不只是文件（同文件）

* **症状**（`jsonl-directory` 用例）：行内放一个目录 `sub.jsonl`，参考 `arms present` 含 `sub`、`10/11`、
  `present but undeclared = sub`；端口 `10/10`、`—`。
* **原因**：`load_run` 的 `sorted(path.glob("*.jsonl"))` **没有** `is_file()` 过滤（`config*` 与 `*ledger*.jsonl`
  才有），所以目录也算一个"臂"（`load_arm` 对缺失文件返回空臂 ⇒ "在、但没有 result"）；
  `looks_like_row` 的 `any(path.glob("*.jsonl"))` 同理。
* **修法**：展开改用 `Directory.EnumerateFileSystemEntries`，两个需要 `is_file()` 的模式（`config*`、`*ledger*.jsonl`）
  各自在方法内过滤 —— 与参考的过滤位置一一对应。
* **负控 M1b**：把展开换回 `Directory.EnumerateFiles` ⇒ 只有 `jsonl-directory` 红，`frozen`/`hidden-arm-only` 绿。
* **还原**：同上哈希。

### F3 文件本身就是 JSON `null` 时，`_load_json_file` 与"没有值"同解（`Loading/RunLoader.cs`）

* **症状**（`null-truth` 用例）：`proxy-truth.json` 内容 `null` ⇒ 参考 §15 的 `proxy-truth.json` 列 **`no`**，
  端口 **`yes`**。
* **原因**：Python `json.loads("null")` → `None`，而所有消费者都写 `is None`（§15 的
  `"yes" if row.proxy_truth is not None`、§4 的 `if truth is None: truth_reason = "no proxy-truth.json"`、
  §12/§13 的 `if row.dual_truth is None`）。C# 的 `JsonElement?` 分不开"没有值"与"JSON null 值"，
  于是 `is not null` 误判成"有"。
* **修法**：`ReadJsonFile` 把根为 `ValueKind.Null` 的文档折成 `null`（与 `is None` 同解），文档写明理由。
  `run.json` 侧行为不变（`ReadDocument` 本来就把非对象当 `no run.json`）。
* **负控 M2**：去掉这行折叠 ⇒ 只有 `null-truth` 红，`frozen` 绿。
* **还原**：`RunLoader.cs` sha256 回到 `2ffb8e19…`。

### F4 `unreadable (<Exc>)` 要按 CPython 的异常名打印（同文件）

* **症状**（`truncated-run` / `chmod-run` 两个用例）：`run.json` 被截断（合法 UTF-8、非法 JSON，本仓 harness
  用 `FileMode.Create` + `Utf8JsonWriter` **就地**写 `run.json`，中断的写就会留下这种文件）
  ⇒ 参考 §15 notes = `run.json unreadable (JSONDecodeError); no run.json`，
  端口原为 `… (JsonReaderException) …`；文件 mode 000 ⇒ 参考 `(PermissionError)`，端口原为 `(UnauthorizedAccessException)`。
  这条文本**就在被比对的 §15 里**，且 `load_arm` 之外的读路径都会走到。
* **修法**：加一个 `ReferenceName(Exception)`，把 `JsonException`→`JSONDecodeError`、
  `UnauthorizedAccessException`→`PermissionError`、`FileNotFound/DirectoryNotFound`→`FileNotFoundError`、其余→`OSError`。
  两个可达分支都已对参考实测通过（`§15 equal: True`）；顺带把 `System.Object.GetType()` 也挤出了读入侧（见 §8）。
* **负控 M3**：换回 `error.GetType().Name` ⇒ 只有这两个用例红，`frozen` 绿。
* **还原**：哈希同上。

**四处都是"机械且局部"的读入侧修复，都在本批范围内；没有改接口、没有加抽象层、没有碰 `src/` 或冻结物。**

## 6. 偏离的可接受性

| 输入类偏离 | 实测（自造输入，/tmp 副本） | harness 能否写出 | 判断 |
|---|---|---|---|
| **NaN / Infinity**（作者 §8.1） | 参考把 `{"type":"result", …:NaN}` 当记录 ⇒ `10/10`；端口当坏行 ⇒ `9/10` | **不能**。`AllowNamedFloatingPointLiterals` 全仓 0 命中；JSONL 与 `run.json` 都走 `Utf8JsonWriter`，实测 `WriteNumberValue(double.NaN)` 与 `JsonSerializer.Serialize(Infinity)` 都抛 `ArgumentException` ⇒ 真出现只会让客户端**当场失败**，不会写出这种记录 | **可接受**（输入类；已登记） |
| **非法 UTF-8**（作者 §8.2） | 三个变体（坏字节在字符串值里 / token 之间 / 整文件）参考**全部崩**（`UnicodeDecodeError`，rc=1）；端口：前一种 STJ 直接替换、文件照常解析（**没有** note），后两种记 `run.json unreadable (JSONDecodeError)` 并继续 | 理论上只有"就地写被截断且正好切在多字节字符中间"能造出来（`FileMode.Create`，非原子） | **可接受**：参考在这个输入上**没有输出**（崩溃），所以没有可对齐的字节；D20.1 已把编码损坏类归"边界树只对 C# 产物做定点断言"。⚠️ 但作者 §8.2 对端口行为的描述与实测不符，见 §9 第 1 条 |
| **JSONL 里的非法 UTF-8**（check 轮补测） | 两侧都按 `errors="replace"` 替换 ⇒ §15 **逐字节相同** | 同上 | 等价，无需处理 |
| **`natural_key` 只认 ASCII 数字**（check 轮新查明） | Python 的 `\d`/`isdigit` 认 Unicode 数字（`"a٣"` vs `"a10"` 排序不同；`"²"` 之类 Python 自己会 `ValueError`） | **不能**：pass 名是 `pass<N>`，row 名是 orchestrator/plan 里的 ASCII 字面量（`wf-aot-opt` … `control-pre`） | **可接受**（输入类，登记） |
| **STJ 的 64 层嵌套上限**（check 轮新查明） | 更深的对象 Python 能解析、端口记 `JSONDecodeError` | 不能：记录最深 4 层 | **可接受**（输入类） |
| **非 null 非对象的 `proxy-truth.json`**（数字/数组/字符串） | Python `truth.get(...)` → `AttributeError` 崩；端口继续（`no usable tcp/udp/utcp`） | 不能：orchestrator 写的是 hashtable | **可接受**（输入类，与非法 UTF-8 同类） |

## 7. 门禁（最终树，逐条串行；日志 `/tmp/e4b1a-check/gates-final/`）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `dotnet build WinForward.slnx -c Release` | rc=0，**0 Warning(s) / 0 Error(s)**，日志有 `WinForward.E2E.Analysis -> …/WinForward.E2E.Analysis.dll` |
| 2 | `dotnet test WinForward.slnx -c Release -m:1` | rc=0，14 个程序集全绿，**1621 passed / 0 failed / 0 skipped**，无 `error ` 行 |
| 3 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0，**0 字节输出** |
| 4 | `jb inspectcode -f=Xml -e=HINT -o=…gates-final/inspectcode.xml WinForward.slnx` | rc=0，解析 XML：`<Issue>` **0**、`<IssueType>` **0**、`<CSharpErrors>` **0** |
| 5 | `python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E benchmarks/WinForward.E2E.Contracts benchmarks/WinForward.E2E.Analysis tests/WinForward.E2E.Tests`（**四路径**） | rc=0，**0 字节输出** |
| 6 | 本批判据 | `--batch 1a` rc=0；`--batch 1b..5` 全 rc=2（§1） |

门禁 3/4 跑在第 1–2 之后、单跑不并发；第 4 条按 AGENTS.md 解析 XML 而非信退出码。
第 3 条在**修复后的第一次**运行报过 2 × `MA0154`（check 轮自己写的注释，见文首哈希表下的说明），
修好后在最终树上重跑为 rc=0 / 0 字节 —— 本节五个读数全部来自**最终源码**（两份文件的 sha256 即上表）。
`selftest.sh` 对分析器不适用（D20.7/A2-10），本批未跑；`plots/SKIPPED.md` 与
`verification/plots-SKIPPED.md` `cmp` 逐字节相同、且不含 python/venv/nix 字样。

冻结物与被跟踪的 `.pyc`：

* `git status --porcelain benchmarks/WinForward.E2E.Analysis/verification/ benchmarks/WinForward.E2E/scripts/oracle-diff.py`
  ⇒ **空**（tarball / golden / `make_tree.py` / `FROZEN.md` / `oracle-diff.py` 零改动）。
* `git status --porcelain benchmarks/results/2026-10-06-e2e-competitors/analysis/` ⇒ **空**；
  `__pycache__/analyze.cpython-314.pyc` sha256 = `1af8aa27236232ca9c6c6ce9f820b918fdaa1f0b1f658b4603dba27c86e5cf42`
  （check 轮所有参考导入都带 `PYTHONDONTWRITEBYTECODE=1`，前后一致）。

## 8. 规范

| 项 | 证据 |
|---|---|
| 新文件 ≤400 有效行 | 五条门禁第 5 条（四路径 0 字节）；`PythonGlob` 35、`RunLoader` **179**、`JsonReader` 86、`TableAvailability` 86；项目 31 文件 / 1592 行 |
| 无变更日志式注释 | 对本批新/改文件扫 `was/used to/previously/no longer/now/this batch/changed`：命中项全是"参考语义/为什么必须这样"，没有一句描述"本批改了什么"。`VerdictSections` 的 "The reference used to publish its own path here" 讲的是 D6.4 的中性化理由（留着读者才知道那个常量为何是这句），保留 |
| 反射只用 `typeof(...)` 字面量 | 分析器里**一处反射都没有**：`System.Reflection` / `typeof(` / `GetType(` 全域 0 命中（F4 顺手把 `error.GetType().Name` 换成了类型模式匹配） |
| 读 JSONL 只用 `JsonDocument` + `ArmKeys` | `JsonSerializer` / `[JsonSerializable]` / `JsonSourceGeneration` / `AllowNamedFloatingPointLiterals` 0 命中；`JsonValue.*(…, "字面量")` 形态 0 命中（键一律走 `ArmKeys.*`；`"result"` 等是**值**，Contracts 里本就没有对应常量，`ArmRecords` 自带常量正确） |
| 无源生成 / 无 IVT | `WinForward.E2E.Analysis.csproj` 只有 `OutputType`/`AssemblyName`/`RootNamespace` + 对 `Contracts` 的 `ProjectReference`；`InternalsVisibleTo` 在本项目 0 命中（其余项目的 IVT 是既有物） |

## 9. 未修复但需上报（都不阻塞提交）

1. **作者证据文档 `E4b1a-oracle.md` §8.2 的描述与实测不符**（check 轮**没有**改作者的文档，按"一轮一份文档"惯例记在这里）：
   原文说端口"records `run.json unreadable (JsonException)`（a load error, so it lands in §15's `notes`）并继续"。
   实测：坏字节落在**字符串值**里时 `System.Text.Json` 直接替换成 U+FFFD、文件照常解析、**没有任何 note**；
   只有坏字节落在 token 之间或整个文件不可解析时才走 `unreadable (...)`，且类名原本是 `JsonReaderException`
   （本轮 F4 已按 CPython 打成 `JSONDecodeError`）。"参考崩、端口继续"这个**实质**结论成立，只是端口的
   具体行为要按上面两句读。建议 b1b 顺手把该段改写（check 轮的 §5/F4、§6 是修好后的口径）。
2. **`Cli/Program.cs` 的类文档兑现不了**（E4-a 遗留，不在本批 diff 内）：`Main` 是 `internal` 并写着
   "so the argument walk can be replayed from a test without starting a process"，但 D20.6 不允许 IVT、
   分析器也没有测试工程 ⇒ 这句话没有任何兑现路径。要么删掉后半句、要么留到真有测试宿主时再改。
3. **`NaturalKey` / `PosixPathText` 的输入类边界**（§6 表）与 **STJ 的 64 层上限**已登记为输入类偏离，
   不修（不可达 + 修了会让代码为一个不可能的状态长出一层）。
4. **`CampaignModel.LedgerFileCount` 的口径**：它把每个 pass 的账本并起来去重计数（冻结树 = 2）。b1a 的
   stdout 用得到；§2/§14 若另有口径（按 pass 列路径）不受影响。仅提醒 b5 对齐 §14 时复核一次。
5. **`AttachLedger` 对"无行的 pass"也记 `LedgerPaths`**（`passorder` 的 `passX`）：与参考的
   `ledger_paths` 同解（模型层正确），但 stdout 只列有行的 pass —— 参考的 stdout 是另一套文本、且 stdout
   不属契约。b1c 的 §2 若按 `ctx.ledger_paths` 迭代也不会分歧（参考的 `table_environment` 其实按
   `ctx.pass_ids` 迭代）。仅登记。

## 10. 复现

```bash
# 判据与三态（§1、§2）
for b in 1a 1b 1c 2 3 4 5; do python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch $b; echo "rc=$?"; done
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a --cs-out /tmp/e4b1a-check/mut2/mutated    # rc=1
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a --cs-out /tmp/e4b1a-check/mut2/nosection  # rc=2
python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1a --cs-out /tmp/e4b1a-check/mut2/nokey      # rc=2

# 自造输入的微分对照（§3）：建树 → 两侧同跑 → 比 §15/passes/rows/账本序
python3 /tmp/e4b1a-check/build_cases.py /tmp/e4b1a-check/cases.json
python3 /tmp/e4b1a-check/diffcase.py /tmp/e4b1a-check/cases.json /tmp/e4b1a-check/results-final.json   # 17/17 ok

# 负控（§5）：改一行 → 重 build → 只跑相关用例 → 还原后 sha256 -c 必须 OK
sha256sum -c /tmp/e4b1a-check/fixed-hashes.txt

# 六道门禁（§7）
bash /tmp/e4b1a-check/gates-final.sh
```

check 轮的装置（都在 `/tmp`，不入库）：`ref.py`（只读导入参考并导出 `discover()` 的读数）、
`build_cases.py`（造树）、`diffcase.py`（两侧跑 + 比对）、`gates-final.sh`（六门禁串行）、
`fixed-hashes.txt`（还原哈希），日志与产物在 `/tmp/e4b1a-check/`。
