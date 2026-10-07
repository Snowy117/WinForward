# E2-d check 报告（`Cli/CommandLine.cs` 解析器合一）

被检查的树：HEAD `1032bc7` + 未提交工作树（E2-d 实现轮冻结树，`linux/WinForward.E2E.dll`
sha256 = `8bca76db…`）。本文件是 check 轮的证据留档，与实现轮的 `E2d-vs-run1.md`、
`cli-snapshots/REPORT.md` 配套。

**检查轮改了两处，都是注释/文档，无一行可执行代码**：`Cli/CommandLine.cs` 的一句类文档（F1）、
`README.md` 的 Layout 表一行（F2）。二进制哈希因此变化，但**可执行差异为零**：用修复后的二进制
重采的 28 条命令快照与实现轮的 `after/` 逐字节相同（§2 的 D1），即哈希变化完全由这两处文字引起。

| 树 | `linux/WinForward.E2E.dll` sha256 |
|---|---|
| 实现轮冻结树（`REPORT.md`/`E2d-vs-run1.md` 记录的那一个） | `8bca76db14cfda57b2a222dfbfc11649f68846263704276b9e0f6d21527f795f` |
| check 轮修复**前**独立重发布（同一棵树，§2 的 before 侧） | `8bca76db14cfda57b2a222dfbfc11649f68846263704276b9e0f6d21527f795f`（逐位相同） |
| check 轮修复**后**（**本报告全部判定的依据**） | `8202869da95128a7b0968cd8cfa1fb268b7b1e1ff69ed8236bf24d4004453aac` |

判定因此整体在 check 轮的新二进制上重跑（§2 重采、§3 突变、§7 四条比对与零宽键、§8 六条门禁），
**未复用实现轮的运行结果**。发布可复现：同一个修复后的树发布两次（`/tmp/check/pub2`、`/tmp/check/pub3`）
哈希逐位相同。

---

## 0. 结论一句话

**E2-d 可以提交**：两个 verb 的已知选项集合、每个选项的拒绝条件与错误文本、退出码映射、错误通道
分界在两个二进制上逐条同义（28 条快照 + 77 条差分重放零失配）；快照树可由发布二进制逐字节重采；
回放测试对走查消息与 target help 的新句都有牙；记录侧 `contract=0`、改名表 9/9、零宽键四次运行
逐值相同、噪声地板两组 13 行逐行相同；六条门禁全绿。检查轮修了两处文档（均**不阻塞**），
上报 1 项非阻塞登记（§9）。

---

## 1. 已修复的问题

| # | 位置 | 改了什么 | 为什么 | 反证方式 |
|---|---|---|---|---|
| F1 | `benchmarks/WinForward.E2E/Cli/CommandLine.cs:9` | 类文档里 "Both used to be spelled once per verb, which is how a fix to one of them could miss the other." → "Spelled once here, a fix to either refusal reaches both verbs instead of one." | 原句陈述的是**这次改写删掉的那份代码**（两个 verb 各自手写的走查已不在树里），属本批 check 口径里的变更日志式注释（E2a2/E2a3 check 用同一条 `rg -i "previously\|used to\|no longer\|changed from\|formerly\|was renamed"` 判据；E2a3 允许的例外要求被对照的旧实现**仍在树里**） | 文本改动，行为无关：修复前后两个二进制的 28 条快照树逐字节相同（§2 D1）、77 条差分重放零失配（§5） |
| F2 | `benchmarks/WinForward.E2E/README.md:32` | Layout 表补一行 `Cli/CommandLine.cs`（两个 verb 共用的走查 + 两条走查级拒绝） | 该表是 harness 的文件清单，E2-b1/E2-b2 每加一个文件都同步过它；E2-d 新增 `Cli/CommandLine.cs` 后表里没有它，目录表与树不一致 | 纯文档；`Cli/` 下四个 .cs 现在逐行在表里（§8） |

范围声明：两处都是文字，**未改动任何一行可执行代码**（`Cli/CommandLine.cs` 是新增的未跟踪文件，F1
只替换了 `<remarks>` 里的那一句，其余逐字未动；`README.md` 只多一行表格行）；`src/` 一行未改；未提交。

---

## 2. 快照可复现与差异面（判据 1、2）

| 步 | 命令 | 结果 |
|---|---|---|
| D1 修复后重采 | `python3 benchmarks/WinForward.E2E/scripts/cli-snapshots.py /tmp/check/pub2/linux/WinForward.E2E /tmp/check/snap2/after` 然后 `diff -r --brief research/cli-snapshots/after /tmp/check/snap2/after` | 无输出（85 个文件逐字节相同）⇒ `after/` 与**修复后**的发布二进制同源 |
| D2 改写前重采 | 用保存的 HEAD 二进制 `/tmp/e2d/before-linux`（`f262a8a7…`，`REPORT.md` 记录的那一个）重采 | `diff -r --brief before /tmp/check/snap/before` 无输出 ⇒ `before/` 也可复现 |
| D3 修复前重采 | 用修复前独立重发布的二进制（`8bca76db…`）重采 | 与 `after/` 逐字节相同 ⇒ F1/F2 未改一个字符的用户可见文本 |
| D4 差异面 | `diff -r --brief before after` | **只有 8 个 `.stdout`**（`04-target-help` + 7 条 target usage 错误），无第 9 个文件 |
| D5 变更行归类 | `diff -ru before after \| rg '^[+-][^+-]' \| sort \| uniq -c` | 三种各 8 次：`−Runs until … 2 on a usage error.` / `+Runs until … 1 on a runtime error, 2 on a` / `+usage error.`，与 `INTENTIONAL.md` 登记句逐字相同 |
| D6 退出码 | 28 个 `.exit` 逐文件 `cmp` | 全部相同；且与我重采时子进程的真实退出码一致（D1 覆盖）；逐条值见 `REPORT.md` §2（help 0、usage/plan 错误 2，D9 的映射） |
| D7 stderr / index | 28 个 `.stderr` + `index.json` 逐文件 `cmp` | 全部相同 |

---

## 3. 回放测试有牙（判据 3）

`dotnet test tests/WinForward.E2E.Tests -c Release --filter FullyQualifiedName~CliSnapshotTests`（2 条）在
修复后的树上全绿；两处突变实跑并还原：

| 突变 | 结果 | 证据 |
|---|---|---|
| M1 `CommandLine.cs:50` `unknown argument` → `unknown option` | **红**：`Failed: 1, Passed: 1` | `Expected: "e2e target: unknown argument '--nope'\n"` / `Actual: "e2e target: unknown option '--nope'\n"`（回放测试在 `05-target-unknown-option` 上失败；第 2 条只读快照树，保持绿，符合设计） |
| M2 `TargetRunner.PrintHelp` 新句 `1 on a runtime error` → `1 on a runtime failure` | **红**：`Failed: 1, Passed: 1` | 位置 622 起 `Expected: …1 on a runtime error, 2 on a\nusage e…` / `Actual: …1 on a runtime failure, 2 on a\nusage…` |

还原哈希（`sha256sum`，与突变前基线逐位相同）：

```text
d9bd41d68482f990e2a45ce382b99581c082c5626579a63256e469221dcc99dd  benchmarks/WinForward.E2E/Cli/CommandLine.cs
cb906528545e7f5833df87e0eb82b1ac10d51800f1f0de4a60e824a112d57195  benchmarks/WinForward.E2E/Target/TargetRunner.cs
```

补充：第 2 条事实 `TheRegisteredTargetHelpSentenceIsTheOnlyDifferenceBetweenTheTrees` 对 M1/M2 都保持绿是
**正确**的——它只比对两棵快照树（比对对象是产物，不是源码），源码漂移由第 1 条事实抓。

---

## 4. 控制台 / cwd 卫生（判据 4）

- **集合**：`CliSnapshotTests` 带 `[Collection("cli-snapshots")]`，`CliSnapshotCollection` 是
  `[CollectionDefinition(Name, DisableParallelization = true)]` ⇒ 该集合不与任何其它集合并行；
  `tests/WinForward.E2E.Tests` 内只有它碰 `Console.Out/Error` 与当前目录（`rg -l 'SetCurrentDirectory|Console.SetOut|Console.SetError'` 只有它自己），无并行污染面。
- **还原**：`previousOut` / `previousError` / `previousDirectory` 三个快照在 `try` 内每次迭代后复位，
  `finally` 再复位一次（`CliSnapshotTests.cs:66-71`）；即使 `Program.Main` 抛异常也复位。
- **无副作用**：28 条命令全在解析器或 plan 读取处结束（`ClientRunner.RunAsync` 的顺序是
  plan → target 解析 → 输出路径校验 → `CreateDirectory`；`TargetRunner.RunAsync` 的 bind 解析在
  `new JsonlSink(...)` 之前）⇒ 不建 `--out` 目录、不写账本。三条独立证据：
  ①采集器首尾两次 `os.path.exists(.cli-snapshot-out)` 断言（三次采集都 exit 0）；
  ②本 check 的 77 条差分重放在一个空 scratch cwd 里跑，结束时 scratch 目录里零文件、`--out` 目录不存在；
  ③跑完全部 264 条测试后仓库根仍无 `.cli-snapshot-out`（`ls` 报不存在）。
- **测试之间**：一个集合两条事实，xunit 不并行集合内用例；第 2 条不碰任何全局。

---

## 5. 语义等价（判据 5）

**旧实现逐条对照**（`git show HEAD:benchmarks/WinForward.E2E/Cli/{ClientOptions,TargetOptions}.cs`）：
两个 `TryCreate` 的走查体（`--name=value` 拆分、`Array.IndexOf(s_knownOptions, name) < 0` 的未知选项、
`++index >= args.Length` 的缺值、`TryApply`/`Apply` 返回 false 即返回）与 `CommandLine.TryParse`
逐行同形；`diff HEAD` 的 hunk 只有 try/catch 之外的走查被替换，`TryApply`/`Apply`/`TryPort`/`ValidatePorts`
一个字符未动。target 侧 `return ValidatePorts(...)` → `TryParse(...) && ValidatePorts(...)`：
`ValidatePorts` 自身 `error = null` 且只在走查成功后调用，短路等价。

**verb 特有校验没有进共享层**：`CommandLine.cs` 内 `rg 'StartsWith|65535|port number|collides|not an IP|dns'`
零命中；前导 `-` 规则仍在 `ClientOptions.TryApply:96-100`、端口区间与 DNS 冲突仍在
`TargetOptions.ValidatePorts`、两个 verb 的端口错误文本各自保留
（`'abc' is not a port number in 1..65535` vs `'abc' is not a port number`）。

**差分重放**：HEAD 二进制与修复后二进制各跑 77 条命令（工具 `/tmp/check/diff-cli.py`，覆盖 28 条快照
之外的形态：内联值、空值 `--x=`、重复选项、值内含 `=`、值像选项、数字边缘形态 `-1/0/1/65535/65536/+80/" 80"/0x50/1_0`、
`--dns-alt-port` 三种冲突、必填缺失、5 条 plan 加载错误、目录/不存在的 plan 路径、大小写 role），
逐条比 `退出码 + stdout + stderr`（把登记的那一句归一化后）：**0 失配**；没有一条命令耗时 >5 s
（即没有一条落进真实运行），scratch 目录零副作用。示例（两侧逐字相同的错误文本）：
`e2e target: 'abc' is not a port number`、`e2e client: 'abc' is not a port number in 1..65535`、
`e2e client: '--label' value '--out' starts with '-'…`、`e2e target: --label --out` 被接受后由后续参数报错。

---

## 6. 可见性与抑制（判据 6）

- **`Program.Main` `private` → `internal`**：inspectcode 在最终树上 `<Issues />` 空（§8），没有
  `MemberCanBePrivate.Global`；`WinForward.E2E.csproj` 有 `<InternalsVisibleTo Include="WinForward.E2E.Tests" />`，
  快照测试确实从 friend 程序集调用它。可见性没有第二条出路（回放要么提 internal，要么在测试里复制 Program 的组装）。
- **setter 未加宽**：`ClientOptions` 5 个、`TargetOptions` 7 个 `private set` 原样保留；赋值点仍在
  `TryApply`/`Apply` 内（`CommandLine` 只把 `TOptions` 实例交回给该类型的回调），`private set` 是仍然成立的
  最窄可见性。加宽会红的结论**不是本轮实测**，依据是 E2-b1 门禁实测（同一条改法曾报 7 条
  `MemberCanBePrivate.Global`）+ 本轮 inspectcode 零 `MemberCanBePrivate`；判断依据写在
  `E2d-vs-run1.md` §7.1，本轮未重跑加宽实验（会额外花一次 549 文件 inspectcode）。
- **`in` 逆变标注必要**（实测）：删掉 `in` → `dotnet build benchmarks/WinForward.E2E -c Release` **exit 1**，
  `CommandLine.cs(23,37): error S3246: Add the 'in' keyword to parameter 'TOptions' to make it 'contravariant'`。
- **RCS1239 pragma 必要且最小**（实测）：删掉 pragma 对 → `dotnet format … --include benchmarks/WinForward.E2E/Cli/CommandLine.cs`
  **exit 2**，`CommandLine.cs(40,9): info RCS1239: Use 'for' statement instead of 'while' statement`。
  全仓 `pragma warning disable` 9 处 = 8 处 `S6966` + 1 处 `RCS1239`（原两个 verb 各一处 RCS1239 收敛为一处）。

---

## 7. 等价性重跑（判据 7）

三次运行都是 `scripts/selftest.sh scripts/plans/selftest-plan.json`（`pre` = HEAD 二进制，
`post1`/`post2` = 修复后二进制连跑两次），三次 `exit 0`；比对命令带
`--normalize research/record-normalize.json --band research/baseline/jitter-band.json
--rename-table research/contract-rename.json --batch B2 --strict`：

| 对照 | structural | conditional | identity | contract | rename | exit |
|---|---|---|---|---|---|---|
| `run1 → post1`（判据） | 0 | 0 | 0 | **0** | 0（B2 **9/9 satisfied**，landed 9/pending 0/vanished 0） | 0 |
| `run1 → post2`（复核） | 0 | 0 | 0 | **0** | 0（9/9 satisfied） | 0 |
| `pre → post1`（噪声地板 A） | 0 | 0 | 0 | 13 | 0 | 1 |
| `post1 → post2`（噪声地板 B） | 0 | 0 | 0 | 13 | 0 | 1 |

- 两组噪声地板**各 13 条、逐行相同**（`diff <(rg 'no recorded jitter band' … | sed 's/^[0-9]*://' | sort)` 为空），
  即本批没有超出同一二进制噪声的差异；条数与 E2-b1/E2-b2/E2-c 记录的 13 条同族。
- 判据对 `compared=1160 measured=878 classes(contract=512 reading=366 identity=12)`，契约栏非空转。
- **D18.5 #12 零宽键**：`metrics/*.received`、`metrics/*.unmatchedReplies`、`metrics/*.outstandingAtTeardown`、
  `latency/*-rtt/count` 在 run1 / pre / post1 / post2 四次运行上各 34 个 (记录,路径) 对 / 18 条不同路径，
  与 run1 的三次 `diff` 全空。

---

## 8. 门禁与规范（判据 8）

| # | 门禁（最终树） | 结果 |
|---|---|---|
| 1 | `WF_PUB=/tmp/check/pub3 scripts/publish.sh` | exit 0；`linux/WinForward.E2E.dll` sha256 `8202869da95128a7b0968cd8cfa1fb268b7b1e1ff69ed8236bf24d4004453aac`（与 `/tmp/check/pub2` 逐位相同，发布可复现） |
| 2 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | **Failed: 0, Passed: 264** |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 修复后二进制连续两次 exit 0（post1/post2）+ 改写前二进制一次 exit 0（pre） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/check/jb-final.xml WinForward.slnx` | 解析 XML：`<IssueTypes />` 与 `<Issues />` 均空、549 个文件 Inspecting、无 `CSharpErrors` |

- **规范**：`effective-lines.py` 三项目**无输出、exit 0**；新/改文件有效行
  `CommandLine` 44、`ClientOptions` 142、`TargetOptions` 117、`TargetRunner` 129、`Program` 160、
  `cli-snapshots.py` 102、`CliSnapshotTests` 81、`RepoPaths` 35（全部 ≤400）。
- **无调试残留**：改动面 `rg 'TODO|FIXME|HACK|XXX'` 零命中；反射新增零处
  （`git diff HEAD | rg '^\+.*(GetType\(|typeof\(|Assembly\.|Activator\.)'` 零命中）。
- **变更日志式注释**：改动面 `rg -i "previously|used to|no longer|changed from|formerly|was renamed"`
  只剩 `cli-snapshots.py:114` 的**运行期消息**（"a case created …; the snapshot list is no longer side-effect
  free"，说的是这次采集观察到的状态，不是改动流水账），保留。
- `research/semantic-fixes/index.jsonl`：55 条全部可 `json.loads`，含 `E2-d` 条目。

---

## 9. 未修上报

1. **E2 批次表与新批次编号漂移**（非阻塞、文档）：`10-07-e2e-e2-structure/implement.md` 的批次总览表
   仍把 `Cli/CommandLine.cs` 写作 **E2-c**（且表里没有账本键族那一行），而正文的 `## E2-c` 是 Target 账本键族、
   `## E2-d` 才是解析器合一；`design-decisions.md` D8 的"2c 解析器合一"也是这次插入账本批次之前的编号。
   正文（E2-d 节）与 `semantic-fixes/index.jsonl` 的 `E2-d` 一致，故只是计划的目录行没跟上；
   本轮未改计划/DD（效力顺序上 DD > implement.md，编号裁定属主会话）。
2. **`after/` 与最终树的哈希关系**（需主会话知情，非阻塞）：本轮修了 F1/F2 后，最终树发布出的
   `WinForward.E2E.dll` 是 `8202869d…`，而 `REPORT.md` §1 与 `E2d-vs-run1.md` §5 记录的是实现轮冻结树的
   `8bca76db…`（那两份文档的**运行读数**确实是那个二进制上的，故本轮未改写它们，只在 F1/F2 的文字层面
   以本报告登记差异）。若希望三处哈希与最终树一致，需要把 `REPORT.md:30`、`E2d-vs-run1.md:118` 与
   `index.jsonl` 的 `E2-d` 条目里的哈希字符串一并更新——这会让那两份文档的读数栏归属到新二进制，
   本轮判断为**不该静默改写别人的运行记录**，留给主会话裁定。

---

## 10. 复跑方式

```console
# 1. 发布 + 采快照（修复后）
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/check/pub3 scripts/publish.sh
cd ../.. && python3 benchmarks/WinForward.E2E/scripts/cli-snapshots.py \
    /tmp/check/pub3/linux/WinForward.E2E /tmp/check/snap3/after
diff -r --brief .trellis/tasks/10-07-e2e-harness-refactor/research/cli-snapshots/after /tmp/check/snap3/after

# 2. 差分重放（HEAD 二进制在 /tmp/e2d/before-linux）
python3 /tmp/check/diff-cli.py

# 3. 等价性：三次 selftest + 四条比对（R = .trellis/tasks/10-07-e2e-harness-refactor/research）
bash /tmp/check/runs.sh
python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 /tmp/check/post1 \
    --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
    --rename-table $R/contract-rename.json --batch B2 --strict

# 4. 门禁（build / test / format / inspectcode）与 AC1
bash /tmp/check/gates2.sh
jb inspectcode -f=Xml -e=HINT -o=/tmp/check/jb-final.xml WinForward.slnx
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
    benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests
```
