# E2-d 回归比对（`Cli/CommandLine.cs` 解析器合一；对照基线 run1）

本批是 E2 的最后一批（`implement.md` 的 E2-d 节）：把两个 verb 各自手写的参数走查抽成一个共享的
`benchmarks/WinForward.E2E/Cli/CommandLine.cs`，`ClientOptions`/`TargetOptions` 各自只留下"选项集合 +
`TryApply` 回调 + 后置校验"。**`src/` 一行未改，本轮未提交。**

判定线：两个 verb 的 `--help` 与全部错误消息**逐字不变**（`research/cli-snapshots/`，唯一登记项见
`INTENTIONAL.md`）；记录侧**结构差异为空、契约栏零越带**、D18.5 #12 的零宽键逐值相同、
改写前后差异 ≤ 同一二进制噪声地板。本批**不碰任何记录路径**（`Client/**`、`Target/**` 的
写盘代码、`Contracts/**` 全未改），所以下面这组运行同时也是"CLI 是运行入口"这一点的对照：
campaign 与 selftest 的每一条命令行都要经过被改写的解析器。

---

## 0. 结论一句话

`compare-records.py` 对 run1（冻结基线）与改写后的两次 selftest 运行给出
**structural=0 / conditional=0 / identity=0 / contract=0 / rename 0**（`exit 0`），改名表 B2 的
9 条全部落地；D18.5 #12 的四个零宽键在 run1 / 改写前 / 改写后两次共四次运行之间**逐值相同**
（34 个 (记录, 路径) 对 / 18 条路径）；改写前→改写后与改写后→改写后两组噪声地板的 13 条差异
**逐行相同**，即本批没有超出同一二进制噪声的差异。六条门禁全绿。

---

## 1. 本批的改动面（为什么期望记录零变化）

| 文件 | 变化 |
|---|---|
| `benchmarks/WinForward.E2E/Cli/CommandLine.cs` | **新增**（44 有效行）：共享的参数走查 + `TryApply<in TOptions>` 回调 + 单一错误通道 |
| `benchmarks/WinForward.E2E/Cli/ClientOptions.cs` | `TryCreate` 改为调 `CommandLine.TryParse` + 原样保留的三条必填校验（142 有效行，原 168） |
| `benchmarks/WinForward.E2E/Cli/TargetOptions.cs` | 同上 + `ValidatePorts`（117 有效行，原 146） |
| `benchmarks/WinForward.E2E/Program.cs` | 只有 `Main` 的可见性 `private` → `internal`（快照测试回放入口点） |
| `benchmarks/WinForward.E2E/Target/TargetRunner.cs` | `PrintHelp()` 的结语补退出码 1（**唯一有意变更**） |

选项集合、每个选项的拒绝条件与错误文本、`--help` 两段文本、退出码映射（0/1/2）、
`Program.cs` 的错误通道分界都不动。记录路径（臂实现、写盘、契约）一行未改。

---

## 2. 四次运行与判定比对

运行目录：`run1` = 冻结基线（`research/baseline/run1`）；`pre` = HEAD `1032bc7` 的已发布二进制；
`post1`/`post2` = 本批发布后的同一二进制连跑两次。四条都是
`scripts/selftest.sh scripts/plans/selftest-plan.json`（本地靶机、无产品），工作目录与端口沿用 run1 的
`/tmp/wf-bench/selftest`。

```console
$ python3 benchmarks/WinForward.E2E/scripts/compare-records.py $R/baseline/run1 <run> \
      --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
      --rename-table $R/contract-rename.json --batch B2 --strict
```

| 对照 | structural | conditional | identity | contract | declared | readings | exit |
|---|---|---|---|---|---|---|---|
| run1 → post1（**判据**） | 0 | 0 | 0 | **0** | 27 | 295/366（241 越带） | 0 |
| run1 → post2（复核） | 0 | 0 | 0 | **0** | 27 | 295/366（212 越带） | 0 |
| pre → post1（改写前后） | 0 | 0 | 0 | 13 | 0 | 288/366（199 越带） | 1 |
| post1 → post2（同二进制） | 0 | 0 | 0 | 13 | 0 | 286/366（132 越带） | 1 |

- 两条 run1 对照的 `contract=0`：**没有一个契约计数/布尔/参数越带**。
- 两条噪声地板各 13 条，且 `diff` 为空（13 行逐行相同，全部是改名后无带宽路径的
  `no recorded jitter band for this path`，见 §4）。`pre → post1` 的 13 条**不是**本批引入的差异：
  它与同一二进制自身的 13 条逐行相同。
- run1 对照的 `identity=12`（pid、ephemeral port、plan 路径等）按配置只比存在性与 JSON 种类，不比数值。
- `compared=1160 measured=878`（run1 对照）与 `1174/891`（噪声地板对）：后者含 13 条无带宽路径，
  计数器口径由工具决定，两个数字都>0 说明没有"空转通过"。

改名表检查（run1 → post1）：

```text
table: 592 entries = added 3, identical 580, renamed 9
path sets: base 589, after 588; only in base 9 (hit 9), only in after 8 (hit 8)
renamed observed: landed 9, pending 0 (0 of them already publishing the target spelling), vanished 0
executed batch: B2; 9 entries required, 9 satisfied, 0 not observed
```

`--strict` 的读数摘要是信息性的（读数永不判败）：run1 → post1 有 241/366 条越过录制带宽，
run1 → post2 有 212/366——同一对基线与同一批代码的两次运行之间就有 29 条的差，
这说明读数越带是宿主噪声（CPU/内存采样、本机时间片），不是本批的信号。

---

## 3. D18.5 #12 的零宽发布键：四次运行逐值相同

四个模式 `metrics/*.received`、`metrics/*.unmatchedReplies`、`metrics/*.outstandingAtTeardown`、
`latency/*-rtt/count` 在 run1 / pre / post1 / post2 四次运行的 `out/` 上抽取：

```console
$ for d in $R/baseline/run1 /tmp/e2d/pre-run /tmp/e2d/post1 /tmp/e2d/post2; do python3 zerowidth.py $d/out > zw-$(basename $d).txt; done
$ diff zw-run1.txt zw-pre-run.txt && diff zw-run1.txt zw-post1.txt && diff zw-run1.txt zw-post2.txt
```

- **34 个 (记录, 路径) 对 / 18 条不同路径**，四次运行之间 `diff` 全部为空。
- 代表值（post1）：`LAT` tcp `received` 322 / udp 161、`LATLOAD` 两向各 1601、`BASE` 两向各 101、
  `DNS`/`DNSALT` `dns-rtt/count` 402、`MIX` 146/602/8、`PERSIST` tcp 15；三个
  `outstandingAtTeardown` 与全部 `unmatchedReplies` 为 0。

---

## 4. 噪声地板逐行相同

```console
$ diff <(rg 'no recorded jitter band' cmp-pre-post1.txt | sed 's/^[0-9]*://' | sort) \
       <(rg 'no recorded jitter band' cmp-post1-post2.txt | sed 's/^[0-9]*://' | sort)
（无输出）    # 两组各 13 条，逐行相同
```

13 条全部落在改名后的无带宽路径（`metrics/latency/tcp.sent`、`metrics/latency/udp.sent`、
`metrics/udp.sent` 等：B2 改名后带宽文件里没有对应条目）。这与 E2-b1/E2-b2/E2-c 三批记录的
13 条同族、数量相同。

---

## 5. 门禁（冻结树，六条逐条）

| # | 门禁 | 结果 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | exit 0；linux/win/win-direct 三份产物；`WinForward.E2E.dll` sha256 `8bca76db14cfda57b2a222dfbfc11649f68846263704276b9e0f6d21527f795f` |
| 2 | `dotnet build WinForward.slnx -c Release` | **0 Warning(s) / 0 Error(s)** |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | **Failed: 0, Passed: 264**（E2-c 的 257 + 7：快照 2 + `ClientOptionsTests` 2 + `TargetOptionsTests` 3） |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 发布后二进制**连续两次** exit 0（post1/post2）；改写前二进制一次 exit 0（pre） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0、**0 字节输出** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/e2d/jb-inspectcode-final.xml WinForward.slnx` | 解析 XML：`<Issue>` **0**、`<IssueType>` **0**、549 个文件、无 CSharpErrors（第一遍在中间态报 1 条 `ConvertIfStatementToReturnStatement`——`TargetOptions.TryCreate` 的 `if (!X) return false;`；按建议改成短路返回后**重新发布、重采 `after/`、重跑两次 selftest 与四条比对与零宽键**，在冻结树上重跑为 0） |

另：`effective-lines.py` 在 `benchmarks/WinForward.E2E`、`benchmarks/WinForward.E2E.Contracts`、
`tests/WinForward.E2E.Tests` 三项目上**无输出、exit 0**（AC1 保持）；
`benchmarks/WinForward.E2E/scripts/cli-snapshots.py` 的 28 条命令在改写前后逐字比对只差
`INTENTIONAL.md` 的一条（`research/cli-snapshots/REPORT.md`）。

测试层的反证（实跑并还原）：

| 突变 | 预期 | 实测 |
|---|---|---|
| M1：`TargetRunner.PrintHelp` 删掉退出码 1 那句 | 快照回放红 | 红：`Expected: …1 on a runtime error…` / `Actual: …2 on a usage error.\n`（`TheRegisteredTargetHelpSentenceIsTheOnlyDifferenceBetweenTheTrees` 仍绿——它只读快照树，符合设计） |
| M2：`CommandLine` 的 `missing value for` → `no value for` | 快照回放红 + 两条行为断言红 | 红：回放红（`e2e target: missing value for '--tcp-port'` vs `no value for`），`ClientOptionsTests`/`TargetOptionsTests` 各红一条 |

---

## 6. 重建方式（check 轮可逐条重跑）

```console
R=.trellis/tasks/10-07-e2e-harness-refactor/research

# 0. 改写前的二进制：HEAD 发布到独立目录（同时用于 before/ 快照）
cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2d/pub scripts/publish.sh
python3 scripts/cli-snapshots.py /tmp/e2d/pub/linux/WinForward.E2E $R/cli-snapshots/before

# 1. 三条门禁（顺序不可换：publish 冻结二进制，selftest/compare 都在它之上）
scripts/publish.sh                                        # 默认 WF_PUB=/tmp/wf-bench/pub
dotnet build WinForward.slnx -c Release
dotnet test tests/WinForward.E2E.Tests -c Release

# 2. 四次运行（pre 用改写前二进制，post1/post2 用发布后的同一二进制；工作目录沿用 run1）
scripts/selftest.sh scripts/plans/selftest-plan.json      # 每次跑前 rm -rf /tmp/wf-bench/selftest
#   run 目录 = 复制 /tmp/wf-bench/selftest 的 out/ + ledger.jsonl + target.out（+ client.out/exit-code）

# 3. 判定比对（四条）
python3 scripts/compare-records.py $R/baseline/run1 /tmp/e2d/post1 --normalize $R/record-normalize.json \
    --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 --strict

# 4. 零宽键（四个模式）在 run1 / pre / post1 / post2 之间逐值比对
# 5. format + inspectcode（解析 XML 数 <Issue>）
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/e2d/jb-inspectcode.xml WinForward.slnx

# 6. AC1
python3 benchmarks/WinForward.E2E/scripts/effective-lines.py benchmarks/WinForward.E2E \
    benchmarks/WinForward.E2E.Contracts tests/WinForward.E2E.Tests
```

---

## 7. 偏离与登记

1. **setter 可见性没有加宽**（与 `implement.md` 的 E2-d 措辞不同）：`ClientOptions` 五个
   `private set` 与 `TargetOptions` 七个 `private set` **原样保留**。新形状里 `TryApply` 仍然声明在
   它赋值的类型内部（`CommandLine` 只拿到 `TOptions` 实例并把它交回给该类型的回调），所以赋值点没有
   离开声明类型，`private set` 仍是最窄的正确可见性；改成 `internal set` 会让
   `MemberCanBePrivate.Global` 重新对 12 个 setter 报点（E2-b1/E2-b2 正是为这条把 setter 收窄的），
   即加宽会直接把第 6 条门禁弄红。对后续批次的影响：E3 的 `--udp-receivers` 等新选项只要 applier
   仍写在 `TargetOptions`/`ClientOptions` 内，setter 就继续 `private set`；一旦有人把 applier 挪出
   该类型，编译器会立刻要求加宽（不会静默变成"外部随便写"）。
2. **`Program.Main` 由 `private` 改 `internal`**：快照测试要回放"用户看到的东西"（退出码 + stdout +
   stderr）而不是重写一遍 `Program` 的组装。它是入口点、又被 friend 程序集使用，
   inspectcode 未报 `MemberCanBePrivate`（第 6 条门禁实测 0 条）。
3. **快照树放在任务 research 下**（按任务书要求），测试通过 `RepoPaths.CliSnapshotsDirectory` 定位，
   并在任务目录被归档时回退到 `.trellis/tasks/archive/<month>/10-07-e2e-harness-refactor/research/…`：
   归档是 `trellis-finish-work` 的常规动作，硬编码活路径会让套件在任务关闭那天红。
4. **有意变更只有一条**，但它在 8 个快照文件里可见（target 的 usage 错误会打印 help）。这一点在
   `REPORT.md` §3/§4 里逐文件列出，避免 check 轮把"8 个文件不同"读成 8 处变更。
5. **未做**（按范围）：E3 的语义修复、E4 分析器、任何 JSON 字段/记录形状变化；
   `--plan`/端口/前导 `-` 的**判定位置**（客户端字符串选项规则留在 `ClientOptions.TryApply`、
   target 的端口区间检查留在 `ValidatePorts`）有意不合并：两个 verb 的端口错误文本不同
   （`'abc' is not a port number in 1..65535` vs `'abc' is not a port number`），合并就会改文本。

> **哈希口径**：本文件记录的是**实现轮**冻结树的读数（`WinForward.E2E.dll` = `8bca76db…`）。
> check 轮的两处**文字**修复（`CommandLine.cs` 的 remarks、README Layout 一行）使发布哈希变为 `8202869d…`；
> 用户可见文本零变化（修复后二进制重采的 28 条快照与 `after/` 逐字节相同），且门禁与等价性判定
> 已在 `8202869d…` 上整体重跑 —— 见 `E2d-check.md` §9.2。
