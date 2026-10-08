# B1c：`compare-records.py` 的 D15 四类判定与负向自检

本轮把 `benchmarks/WinForward.E2E/scripts/compare-records.py` 从"结构 / 条件键 / 数值（逐键带宽）"三栏升级为
DD D15 的**四类**判定，并做完 D15 点名的 5 条配套清理；同时给改名表加了 `batch` 字段与
"差集项必须命中、已执行批次必须恰好出现"的判定（B2 的判据，见
[B1c-vs-run1.md](./B1c-vs-run1.md) §5）。记录形状、`run1|run2` 基线与 `jitter-band.json` **一个字节都没动**
（`git status` 里 `baseline/run1`、`baseline/run2`、`baseline/jitter-band.json` 均无改动，所以不需要 `run1b/`）。

## 1. 四类的最终语义

| 类 | 判据 | 失败？ |
|---|---|---|
| **结构类** `structural` | 键集、JSON 类型、数组长度、归一化后的字符串值、`target.out` 文本 | **是**（除非差异命中改名表 → 落到下面的 `declared`） |
| **条件键** `conditional` | D7 第 4 条的既有豁免：`UseTcp`/`UseUdp` 单边块、空直方图省略。单列，永不失败 | 否 |
| **身份类** `identity` | 由 `identityPathPatterns` 声明：**只校验存在与类型**，值不比对 | 类型变化 → 是；值变化 → 不比对 |
| **契约类** `contract` | 其余全部数值 + 全部布尔值。逐键落在记录带宽内；布尔值必须相等（无带宽） | **是** |
| **读数类** `reading` | 由 `readingPathPatterns` 声明：时钟、CPU/内存/线程、吞吐、转移量、延迟直方图读数 | 否（`observed movement`，`--strict` 逐条列出） |
| **已声明键集变更** `declared` | 单边路径但被改名表覆盖（`renamed`/`added`/`removed`） | 否（由改名表判定接手） |

判定顺序（`Config.classify`）：**精确 `classOverrides` → `identityPathPatterns` → `contractPathPatterns` →
`readingPathPatterns` → 默认契约**。默认落在契约是**有意的 fail-closed**：读数类是显式清单，任何没被声明成
读数/身份的新数值键都会按契约带宽检查（带宽缺失直接报 `no recorded jitter band for this path`），
所以"某个计数器因为没人声明它而被静默成信息项"这条路径不存在。同一路径同时命中契约与读数模式会在配置期
直接 `SystemExit`（不是静默的优先级规则）。

`--explain-classes` 打印每条被测数值路径的类与**选中它的那条规则**，供人工核对（§3 用了它）。

## 2. 配置面：哪些在 `record-normalize.json`，哪些硬编码，为什么

`research/record-normalize.json`（version 3）：

| 配置键 | 作用 | 归属理由 |
|---|---|---|
| `identityPathPatterns` | `*/pid`、`sources/port`、`planPath`、`outDirectory`、`planHash`、`clientVersion`、`osDescription`、`frameworkDescription` | D15 点名"`*/pid`、`sources/port`、路径类、版本/hash"；**每次运行都变的值属于身份类**，写成清单才不会把新增的身份键当成契约回归 |
| `readingPathPatterns` | 时钟（`*ticks`/`*Ticks`/`wallSeconds`/`*elapsedSeconds`）、CPU/内存/线程（`cpuSeconds`、`generatorCpuSeconds`、`*/cpuSeconds`、`privateBytes`、`*/privateBytes`、`workingSetBytes`、`*/workingSetBytes`、`envWorkingSetBytes`、`peakWorkingSetBytes`、`threads`、`*/threads`）、吞吐（`*achievedRate`、`*goodputBps`、`*goodputMbps`）、转移量（`metrics/*Bytes`、`metrics/*bytes`、`metrics/*bytesSent`、`metrics/*bytesEchoed`、`metrics/frames*`、`metrics/classes/*/frames`、`metrics/classes/page/bytesPerPage`，账本的 `bytesEchoed`/`tcp/bytesEchoed`/`expectedBytes`/`tcp/expectedBytes`/`udp/bytes`）、直方图读数（`*Us`、`metrics/*Ms`）、账本每秒曲线（`received`、`bytes`、`sources/datagrams`）、观察到的空闲时长（`metrics/idleSeconds*`） | D15 的读数清单；**按路径形状声明**，不是按"看起来像"猜 |
| `contractPathPatterns` | `parameters/*` | 声明输入不是测量：`parameters/payloadBytes`、`parameters/lossWindowMs` 是 plan 要求的输入而不是测出来的读数。**当前读数模式没有一条能匹配 `parameters/*`**（没有裸 `*Bytes`/`*Ms` 形状），所以这一条是显式钉子而非承重规则：它把"plan 输入改了必须报"钉死，将来任何按形状命名的读数模式都无法把它静默降级（同时命中契约与读数模式是配置错误，不是优先级） |
| `classOverrides` | `{"gates/inFlightCeilingMs": "reading"}` | D15 第 6 条的跨类键：`gates.*` 形状、毫秒读数语义。**显式 override**，不靠 `*Ms` 之类的模式巧合命中（`gates/` 不在任何读数模式里，所以这条 override 是承重的） |
| `volatileKeyNames` / `volatileKeySuffixes` | 只剩字符串时钟：`utc`/`startedUtc`/`endedUtc`/`startUtc` 与后缀 `Utc` | D15 配套清理 1：`ticks`/`*Ticks`/`wallSeconds`/`elapsedSeconds`/`generatorCpuSeconds`/`envWorkingSetBytes`/`Us` **在这张表里对数值永远匹配不到**（`normalize()` 只看字符串值），声明与行为不符，已删；它们的数值孪生量现在是读数类 |
| ~~`pathKeyNames`~~ | 已删除，两条路径键移入 `identityPathPatterns` | "值不比对"只留一个机制；`<path>` 占位符归一化随之消失，行为不变（身份类整个跳过值比较） |
| `conditionalPathPatterns`、`volatileArityPatterns`、`endpointKeyNames`、`numericTextKeyNames`、`normalizeTargetOut*` | 原样保留 | D7 第 4 条的条件键/数据相关数组，以及"peer 只归一化端口、note 只归一化小数"两条既有语义 |

清单里**当前一条路径都没命中**的形状（核对 run1 + run2 + `/tmp/b1c-run` 三棵树）：读数的
`*/workingSetBytes`、`*/threads`（进程样本今天只有 `processes/privateBytes` 与 `processes/cpuSeconds`
两个嵌套读数）、`metrics/*bytesEchoed`、`tcp/expectedBytes`（真实的拼写是 `metrics/echoedBytes` 与根层
`expectedBytes`）；`volatileArityPatterns` 的 `*/sources`、`metrics/udp.lane*`（UDP 车道数组只在 selftest
plan 之外的臂里出现）。它们匹配不到路径，所以既不吞掉契约键、也不保护任何读数：是前瞻性的形状声明，不是本次
分类的承重项。identity 清单 8 条全部命中（6 条是字符串身份键，`classify()` 对它们只做存在与类型检查）。

硬编码在脚本里、不进配置的三件事：

1. **四类的框架与退出码**（结构/条件/身份/契约失败退出 1，读数与 `declared` 不影响退出码）——分类**判据**是契约，
   不能由一次运行的数据文件随意关闭。
2. **布尔值比较**：布尔没有带宽概念，必须相等。`jsonl_paths.kind_of` 把 bool 排在下标之前，所以旧脚本的
   `KIND_NUMBER` 分支永远看不到布尔值——**旧实现根本没有比较过任何布尔值**，`metrics.budgetReached:
   true → false` 会静默通过（已用 HEAD 脚本对照复现：新脚本报 `boolean values differ` 并退出 1，旧脚本
   `numeric=0`、退出 0）。本轮补上（D15 把 booleans 归契约类）。
3. **`--batch` 的"恰好出现"语义**：改名是 D7 第 4 条的判据，批次是判据的参数，不是数据。

另有下面三条（D15 配套清理 2/3，加上两条同类的静默绿路径堵口，都在脚本里）：

- `Side()` 现在把 `<run>/out` 解析回 `<run>`，被指到 `out/` 时**不再静默跳过** `ledger.jsonl`/`target.out`
  （以前只读记录，读出来"账本没有差异"）；两边都找不到记录/账本时直接报错，不再产出一个空比对——只有
  `target.out` 的目录也算空比对（"一个路径都没比"必须报错，而不是汇总成 `compared=0` 的绿）。
  账本永远只作为一个组 `ledger/ledger.jsonl` 进入路径集：run 根同时充当记录目录时，它不会被既当记录文件
  又当账本（那会把账本 88 条路径的 `compared`/`measured` 翻倍）。
- `allowedCountDelta` 默认 **0**：记录数漂移必须由带宽文件显式声明（`--write-band` 会把它观测到的漂移写成
  `max(|countDelta|, 1)`，即"显式声明"发生的地方）。本轮 12 个文件 + 账本的行数与 run1 逐一对齐（
  [B1c-vs-run1.md](./B1c-vs-run1.md) §2），所以默认 0 没有造成任何新 finding。
- `--band` 指向的文件必须带**非空** `paths` 对象：空 band 等于不检查任何契约值，所以误传别的 JSON（例如
  `record-normalize.json`）或传一个空表都会直接报错退出，而不是把 542 条契约检查静默关掉。不带 `--band`
  时契约值只测量不判定（`--write-band` 的测量模式），header 与 summary 都明写这一点
  （`contract=0 (not checked: no --band)`）。

## 3. 负向自检（3 条，命令与观测）

三条都用同一份新鲜产物 `/tmp/b1c-run`（本轮 selftest）与它的一份**只改一处**的副本比对，因此差异面只有那一处。
`--band` 用既有 `baseline/jitter-band.json`。反向对照跑的是 git HEAD 里的旧脚本
（`git show HEAD:benchmarks/WinForward.E2E/scripts/compare-records.py`，放在 `/tmp/b1c/compare-records-old.py`，
`PYTHONPATH` 指向脚本目录以复用 `jsonl_paths`），用来证明"结论变了"是分类造成的，不是值没变。

```bash
R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 /tmp/b1c/mutate.py /tmp/b1c-run /tmp/b1c        # 生成三份改坏一处的新鲜产物副本

# ① 契约计数：metrics.clientSendLoss +1（LOSS/MIX 各一条 result）
python3 $CR /tmp/b1c-run /tmp/b1c/neg-contract --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json
# ② 身份值：processes[].pid +100000（95 条记录；带宽 724，改动是带宽的 138 倍）
python3 $CR /tmp/b1c-run /tmp/b1c/neg-identity --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json \
        --strict --explain-classes
# ③ 延迟读数：latency.tcp-rtt.p99Us ×10（LAT 一条 result；带宽 2222.08 µs）
python3 $CR /tmp/b1c-run /tmp/b1c/neg-reading  --normalize $R/record-normalize.json --band $R/baseline/jitter-band.json --strict
```

| 自检 | 新脚本观测 | 退出码 | 旧脚本观测 |
|---|---|---|---|
| ① 契约计数 | `contract=6`：`records/LOSS.jsonl metrics/clientSendLoss: min/max/mean delta 1 exceeds jitter band 0`（MIX 同） | **1** | `numeric=6`，退出 1 |
| ② 身份值 | 四个类全空；`processes/pid` 在 `--explain-classes` 里是 `identity identityPathPatterns:*/pid` | **0** | `numeric=33`：`records/BASE.jsonl processes/pid: min/max/mean delta 100000 exceeds jitter band 724`（base 488653 → after 588653），退出 **1** |
| ③ 延迟读数 | `readings=1/349`，其余全空；不加 `--strict` 只报计数 | **0** | `numeric=3`：`records/LAT.jsonl latency/tcp-rtt/p99Us: min/max/mean delta 29251.6 exceeds jitter band 2222.08`（base 3250.18 → after 32501.8），退出 **1** |

③ 加 `--strict` 后逐条列出（"只进信息性、`--strict` 才列出"）：

```
== 7. readings (informational observed movement; never a failure) ==
  1 of 349 measured reading paths moved between the runs
  every movement, largest first:
    records/LAT.jsonl::latency/tcp-rtt/p99Us: mean 3250.18 -> 32501.8, max |delta| 29251.6 (band 2222.08), n 1/1
summary: structural=0 conditional=0 identity=0 declared=0 contract=0 rename=0 readings=1/349 compared=1174 measured=891 classes(contract=542 reading=349 identity=12)
```

② 的 `--explain-classes` 片段（`/tmp/b1c/out-neg-identity-strict.txt:469,476`）：

```
  identity processes/pid                                           identityPathPatterns:*/pid
  identity sources/port                                            identityPathPatterns:sources/port
```

三条自检合起来证明：**契约值的变化报错、身份值的变化不报、读数的变化只作信息**——而且三条在旧脚本里全是
非零退出的数值 finding，所以新结论确实来自分类。

另加两条对改名表判定的负向/正向自检（细节见 [B1c-vs-run1.md](./B1c-vs-run1.md) §5）：把
`metrics.brandNewCounter` 塞进 IDLE 的 result 里，判定报
`only in after and not declared by any entry` 并退出 1；把 9 条改名**照着表**合成一份"B2 已落地"的产物后，
`--batch B2` 报 `9 required, 9 satisfied, 0 not observed`，退出 0。

## 4. 与 B1b 手工四类分解的差异（有意）

[B1b-vs-run1.md](./B1b-vs-run1.md) §3 的手工分解把时钟单列一类、把 `*Rate`（`lossRate`/`strictLossRate`/…）
算成读数，因此 `contract=270 / readings=524 / clock=83 / identity=26`。本实现的口径：

- 时钟不是第五类，归**读数**（D15 的四类表里没有独立的时钟类，而 B1b §3.2 也是按"信息性移动"处理的）；
- `*Rate` 里只有 `achievedRate`/`goodput*` 是吞吐读数；`lossRate`/`strictLossRate`/`corruptRate`/`reorderRate`/
  `lateRate`/`duplicateRate`/`answerRate`/`responseRate`/`fidelityRate`/`classes/*/lossRate` 是**计数器的确定性比值**，
  归契约类（它们在当前基线里带宽全是 0，所以归契约只提高严格度，不制造噪声）；
- 身份类只算真正的数值身份键（`*/pid` 11 条 + `sources/port` 1 条 = 12），`*Utc` 是字符串、本来就不进数值表。

本轮的实测分类（run1 vs `/tmp/b1c-run`：两侧都存在的数值 `(文件, 路径)` 对共 903 个 = `measured` 891 +
身份 12）：

| 类 | 对数 | 说明 |
|---|---|---|
| `contract` | **542** | 其中 `parameters/*` 63 对（显式钉死），其余 479 对按默认规则 |
| `reading` | **349** | |
| `identity` | **12** | 只存在与类型检查 |
| 非数值路径 | — | 字符串/布尔/数组：字符串值走结构类、布尔走契约类、数组长度走结构/条件类（不进数值表） |

**一条可核对的自洽性证据**：542 个契约对里，**没有任何一对的记录带宽非零**（冻结带宽文件里它们的
`maxAbsDelta` 全是 0）；反过来，冻结带宽 903 条里 **296 条带宽非零，没有一条**被判成契约（284 条读数 +
12 条身份）。也就是说"带宽为 0 的当契约、会动的当读数"这条分界与 run1↔run2 实测的带宽数据完全一致，不是拍脑袋。

## 5. 六条门禁

见 [B1c-vs-run1.md](./B1c-vs-run1.md) §6（发布 / 构建 / 测试 / selftest / format / inspectcode 的逐条结果与命令）。
