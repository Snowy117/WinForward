# B2a 回归比对（LAT/DNS 类型化 + 3 条改名 + D16.1/D16.2，对照基线 run1）

本轮把 `latency`、`dns` 两个臂的 `metrics` 迁到强类型 record（`LatencyMetrics` 的两块 + `DnsMetrics`），
执行改名表里 `batch: "B2"` 的 3 条（`metrics/tcp.sentOk`、`metrics/udp.sentOk`、`metrics/udpSent`），
并落地 D16.1（没有 `--band` 时契约值按带宽 0 判）与 D16.2（配置里 0 命中的模式清零）。

判定线：**结构差异只剩改名表登记的键集变化；契约类 0 越带；身份类 0 finding；读数类只作信息性输出**。
改名表判定按 D16.4 分批：本轮应报**剩余未落地项**而不是通过——工具确实报出 5 条并退出 1（§4）。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                     # 强制重建三份产物，退出码 0
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json             # 退出码 0
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/b2a-run/
R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR $R/baseline/run1 /tmp/b2a-run --normalize $R/record-normalize.json \
        --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 \
        --json-out /tmp/b2a/findings-b2a.json                    # 退出码 1（唯一失败面是 rename 栏 5 条）
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 | `linux/WinForward.E2E.Contracts.dll` sha256 |
|---|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e` | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` | 无此文件 |
| `b2a`（本轮） | HEAD `ce10b38` + 本轮未提交工作树 | `00da5d5cb0eda41414fd46fba839038f3146c80435f4bbe84b6dac7291d4c4eb` | `c8d13b528bae22539f99b9b2b9e0cca69c9339f85b609df86a495ddbe4014acd` |

本轮的 selftest 产物留在 `/tmp/b2a-run/`，未随任务提交（与 B1b/B1c 相同：run1/run2 是基线定义，
本轮是回归检查，四条命令随时可重建）。

## 2. 三次比对的四类计数

| 比对 | 结构 | 条件键 | 身份 | 契约 | 已声明 | 改名 | 读数（信息性） | 退出码 |
|---|---|---|---|---|---|---|---|---|
| `run1 vs run1`（无 band） | **0** | **0** | **0** | **0**（带宽 0） | 0 | 0 | 0/349 | **0** |
| `run1 vs /tmp/b2a-run --band --rename-table --batch B2` | **0** | **0** | **0** | **0** | 17 | **5** | 291/349 | 1（全是 rename） |
| `run1 vs /tmp/b2a-run --rename-table --batch B2`（无 band） | **0** | **0** | **0** | **0**（带宽 0） | 17 | **5** | 291/349 | 1（全是 rename） |

- **结构栏 0**：键集、JSON 类型、数组长度、字符串值（含 `target.out` 归一化文本）全部相同或由改名表登记。
  `declared` 的 17 条 = 8 条旧拼写（`only in base`）+ 8 条新拼写（`only in after`）+ `run.json planSource`。
- **契约栏 0 越带**：534 个契约对在两次独立运行之间全部落在逐键带宽内；带宽文件里契约类条目的
  `maxAbsDelta` 全是 0，所以这里等价于"契约值逐位相同"（`allowedCountDelta` 被 `--write-band` 放宽到
  ≥1，但本轮没有任何契约路径的记录数漂移——11 个臂文件 + `ledger.jsonl` 的行数与 run1 逐一对齐，§6）。
- **身份栏 0 finding**：12 个身份数值对（`*/pid` 11 + `sources/port` 1）只查存在与类型。
- **读数栏 291/349 移动**：按 D15 **信息性**输出，不进退出码（不加 `--strict` 时连路径名都不打印）。
  `classes(contract=534 reading=349 identity=12)`：契约对数由 B1c 的 542 降到 534，正是 8 条改名路径
  从"两侧同在"变成"单侧 + 已登记"，不是新的差异面。
- `run1 vs run1` 在**没有 band** 的新语义下仍然四类全空、退出 0：带宽 0 不会自己制造差异。

## 3. 改名执行情况（逐条）

`--batch B2` 的判定输出：

```
  table: 592 entries = added 3, identical 580, renamed 9
  path sets: base 589, after 590; only in base 4 (hit 4), only in after 5 (hit 5)
  renamed observed: landed 4, pending 5 (2 of them already publishing the target spelling), vanished 0
  added observed: 1, not observed 2
  executed batch: B2; 9 entries required, 4 satisfied, 5 not observed
  pending: metrics/desktops/udpArrived -> metrics/desktops/udp.arrived, metrics/desktops/udpForeignConnection -> metrics/desktops/udp.foreignConnection, metrics/desktops/udpSent -> metrics/desktops/udp.sent, metrics/udpLossRate -> metrics/udp.lossRate, metrics/udpSent -> metrics/udp.sent
  declared additions not observed: detail, error
```

| # | old → new | 发布者 | 本轮状态 |
|---|---|---|---|
| 1 | `metrics/tcp.sentOk` → `metrics/tcp.sent` | LAT, LATLOAD | **landed**（本轮执行） |
| 2 | `metrics/udp.sentOk` → `metrics/udp.sent` | LAT, LATLOAD | **landed**（本轮执行） |
| 3 | `metrics/udpSent` → `metrics/udp.sent` | DNS, DNSALT, **MIX** | **部分落地**：DNS/DNSALT 已改；MIX 仍发 `udpSent`，故工具报 pending |
| 4 | `metrics/latency/tcp.sentOk` → `metrics/latency/tcp.sent` | BASE | **landed（顺带）**：BASE 的 latency 相位值就是共享的 `LatencyMetrics`，键随 record 一起改 |
| 5 | `metrics/latency/udp.sentOk` → `metrics/latency/udp.sent` | BASE | **landed（顺带）**，同上 |
| 6 | `metrics/desktops/udpArrived` → `metrics/desktops/udp.arrived` | MIX | pending（B2b） |
| 7 | `metrics/desktops/udpForeignConnection` → `metrics/desktops/udp.foreignConnection` | MIX | pending（B2b） |
| 8 | `metrics/desktops/udpSent` → `metrics/desktops/udp.sent` | MIX | pending（B2b） |
| 9 | `metrics/udpLossRate` → `metrics/udp.lossRate` | MIX | pending（B2b） |

要点：

1. **"部分落地"确实存在且被工具如实报出**：第 3 条的 `new_path`（`metrics/udp.sent`）本轮已由 DNS/DNSALT
   产出，而 `old_path`（`metrics/udpSent`）仍由 MIX 产出——`new_path` 被多个臂共用，只改本轮的臂时，
   工具的状态判定是 `pending`（并且标出"目标拼写已经存在"），不是 `landed`。这正是 D16.4 要的
   "报剩余未落地项而不是通过"：`--batch B2` 报 `4 satisfied, 5 not observed` 并**退出 1**。
2. **第 4/5 条是共享 record 的顺带落地**：任务把 BASE 的两条划给 B2b，但 BASE 的 `metrics/latency` 值
   就是 `LatencyMetrics` 本身（`BaseArm` 把 `latency.Metrics` 塞进自己的 `DictionaryMetrics`），
   改 record 的键不可能只改 LAT 不改 BASE。这条已在 §5 控制 ③ 里从两侧都验证过：BASE 的嵌套块与外层
   LAT 同步改键，值逐位不变；B2b 剩下的只是 `BaseArm` 改名与 `loss` 相位，与这两条无关。
3. 第 9 条的 `pending` 也标着"目标拼写已经存在"：`metrics/udp.lossRate` 一直由 LAT 产出（那是 latency 块
   自己的键），MIX 的 `metrics/udpLossRate` 收敛过去即可——工具把这种"收敛到已有拼写"与"目标拼写由本轮
   新引入"区分开了。
4. `added` 的 `planSource` 命中（`only in after 5 (hit 5)` 里的一条）；`detail`/`error` 未观测是预期的
   （绿 run 不产生 `error` 记录）。

## 4. D16.1：没有 `--band` 时契约值按带宽 0 判

工具改动：`--band` 缺失不再等于"契约栏不检查"。判定顺序是 `--band`（按记录带宽）→ `--write-band`
（测量模式，不判）→ 都没有（**带宽 0**，且头行与 summary 都写 `band = 0 (not measured)`）。

**对照（A/B，同一对输入、同一条命令，只换工具版本）**：把 `/tmp/b2a-run` 复制一份，只把
`records/DNS.jsonl` 的 `metrics/sent` 从 402 改成 403（契约 +1），然后：

| 工具 | 命令 | 契约栏 | 退出码 |
|---|---|---|---|
| HEAD 版 `compare-records.py` | `run1 vs /tmp/b2a-run-perturbed --normalize … --rename-table …`（无 `--band`） | `contract=0 (not checked: no --band)` | **0**（静默通过） |
| 本轮版 `compare-records.py` | 同上 | `contract=3 band = 0 (not measured)` | **1** |

新版的逐条 finding（正是"契约 +1 必须红"）：

```
  records/DNS.jsonl metrics/sent: min delta 1 exceeds jitter band 0 (base 402, after 403)
  records/DNS.jsonl metrics/sent: max delta 1 exceeds jitter band 0 (base 402, after 403)
  records/DNS.jsonl metrics/sent: mean delta 1 exceeds jitter band 0 (base 402, after 403)
```

**改动在真实数据上的惰性**（不是"改了以后什么都红"）：

| 比对 | 无 band 的契约栏 | 退出码 |
|---|---|---|
| `run1 vs run1` | 0（逐位相同） | 0 |
| `run1 vs /tmp/b2a-run --rename-table --batch B2` | 0（本轮契约计数逐位不变） | 1，且失败面只有 rename 5 条 |

另外 `--write-band` 仍是**测量**模式：`run1 vs /tmp/b2a-run-perturbed --write-band /tmp/b2a/band-selftest.json`
（无 `--band`）退出 **0**，把 `records/DNS.jsonl::metrics/sent` 的 `maxAbsDelta = 1` 写进带宽文件而不是判失败
（否则"用它自己产出带宽"这件事会因为自己测出的噪声而失败）。

## 5. 改名是纯改键：三个合成控制

比对工具面对改名只能在"单侧路径 + 改名表"的层面上判定，所以另做三个控制把"改名"与"运行间噪声"拆开：

| # | 控制 | 观测 |
|---|---|---|
| ① | 把 run1 的 4 条旧拼写**按文本替换**成新拼写（LAT/LATLOAD/BASE 的 `tcp.sentOk`、`udp.sentOk`，DNS/DNSALT 的 `udpSent`，其余字节一律不动），再与 run1 比 | 结构 0 / 条件 0 / 身份 0 / **契约 0** / `only in base 4 (hit 4)`、`only in after 4 (hit 4)` / **读数 0/349 移动** / 退出 0 —— 改名只是换名字，值、类型、数组长度、其它路径逐字节不变 |
| ② | ①的合成树 vs 真实 `/tmp/b2a-run`（带 band、不带 `--batch`） | 结构 0 / 条件 0 / 身份 0 / 已声明 1（只有 `planSource`）——**强类型 writer 的产出形状 == 旧 writer 的产出形状**；契约栏的 8 条是"这 8 个新拼写在冻结带宽文件里没有条目"（fail-closed，符合预期），不是值差异 |
| ③ | 同上，但把带宽文件里那 8 条旧拼写的条目复制到新拼写下 | 四类全 0、退出 **0**：LAT/DNS/BASE 三处被改名的**契约值逐位不变** |

## 6. D16.2：配置里 0 命中的模式

新增一个小工具 `benchmarks/WinForward.E2E/scripts/normalize-pattern-hits.py`（D16.3 的"小工具项"）：
它用共用的 `jsonl_paths.flatten_file` 建与比对脚本同样的域（记录目录 + 账本），对
`readingPathPatterns` 检查**全部已观测路径**、对 `volatileArityPatterns` 只检查**数组**路径
（这正是比对脚本消费它们的两个域）；`identityPathPatterns`/`contractPathPatterns`/
`conditionalPathPatterns` 只打印不判失败——条件模式在没跑那个臂的 plan 里本来就该匹配不到。
命中 0 的模式报 `DEAD` 并**退出 1**，所以"配置不得声明无效的形状"从此是可复核的命令而不是一次性检查。

```bash
python3 benchmarks/WinForward.E2E/scripts/normalize-pattern-hits.py \
    --normalize research/record-normalize.json --run research/baseline/run1 --run research/baseline/run2
```

| 配置 | run1 | run2 | b2a | 退出码 |
|---|---|---|---|---|
| HEAD 版 | **6 条死模式** | 同 | — | 1（每条 `DEAD` 一行，共 12 行） |
| 本轮版 | **0 条**（589 条路径 / 12 条数组） | **0 条** | **0 条**（590 条路径 / 12 条数组） | **0** |

六条死模式的去向（就是 D16.2 点名的那六条）：`*/workingSetBytes`、`*/threads`、`metrics/*bytesEchoed`、
`tcp/expectedBytes` 四条 `readingPathPatterns` 删除——真实拼写本来就在：sample 记录的
`workingSetBytes`/`threads` 是**顶层**（`*/` 形式永远匹配不到），`metrics/*Bytes` 已覆盖全部 echoed/转移量，
账本的 `expectedBytes` 也是顶层且没有 `tcp/` 兄弟；两条数组模式 `*/sources`、`metrics/udp.lane*` 删除——
账本数组是裸 `sources`（已列），UDP 只有一条 lane、`udp.laneStarted` 是标量，不存在能变 arity 的 udp lane 数组。
为何删而不是改：这些形状在产物里**不存在**，配置不得声明与行为不符的语义（D16.2）。`$comment` 新增一段写明判据。

## 7. 结构证据（比对范围之外的独立核对）

- **记录数逐一对齐**：`BASE 12`、`DNS 10`、`DNSALT 10`、`IDLE 7`、`LAT 10`、`LATLOAD 10`、`LOSS 12`、
  `MIX 12`、`PERSIST 12`、`REL 18`、`THRU 10`、`ledger 258` 与 run1 完全相同；`run.json` 多 1 行，
  就是改名表登记过的顶层 `planSource`（A2 引入）。
- **全局路径集**：`only in base 4` 是 4 条被改名的旧拼写，`only in after 5` 是 4 条新拼写 + `planSource`；
  9 条里没有"两个拼写同时出现"（工具的第 4 条规则会报错，本轮没有触发）。
- **LAT 的 lane 数组**：`metrics.tcp.laneSupplied`/`laneSentOk` 落盘为 `[161, 161]`，与
  `parameters.lanes = 2` 一致（形状测试另有 arity 断言）。
- 记录键序未变：`tcp.sent`/`udp.sent` 就写在 `supplied` 之后（ArmKeys 的声明序 == 写出序，由单测
  `TheMetricsAreWrittenInKeyDeclarationOrder` 覆盖）。

## 8. 六条门禁

| # | 命令 | 观测 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 退出 0；三份产物重建（§1 的 sha256） |
| 2 | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 169`（B1：167，本轮 +2） |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出 0（本轮 B2a 产物的来源） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0，**输出 0 字节**（首轮跑出 1 条 `CA1859`，已按建议改返回类型后复跑） |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | 报告 `<Report>` 下 **0 条 `<Issue>`**（解析 XML，不看退出码） |

补记：首轮 `jb inspectcode` 报了 21 条（18 条 `InvalidXmlDocComment` + 1 条 `MemberHidesStaticFromOuterClass`
+ 1 条 `UseObjectOrCollectionInitializer` + 1 条 `UseDeconstruction`），逐条处理如下：

- 18 条 `InvalidXmlDocComment` 全是**未限定的 `<see cref>`**：嵌套类（`ArmKeys.Dns`/`ArmKeys.Latency`）的文档注释里
  R# 在**外层类**作用域解析 cref。按 `ArmKeys.Common`/`ArmKeys.Idle` 的既有写法改成限定名（`Dns.Rcodes`、
  `Latency.TcpSent` …），**没有加抑制**；
- `MemberHidesStaticFromOuterClass`：`ArmKeys.Common.Parameters.Latency` 与新的 `ArmKeys.Latency` 分片同名，
  按本文件既有做法补一条带理由的 `// ReSharper disable once`（S3218 的孪生抑制，理由与 `Record.Latency` 相同）；
- `UseObjectOrCollectionInitializer`：`DnsArm` 的两条 gate 赋值折进 `ArmOutcome` 的对象初始化器（值同时由 `0`
  写成 `0L`，与 `IdleArm`/`LatencyArm` 的 gate 一致；JSON 文本不变，见 §5 控制 ③ 的复核）；
- `UseDeconstruction`：形状测试改用 `var (kind, _, metrics) = contract;`。

改了代码之后门禁 1–5 **全部重跑**（发布、构建、单测、selftest、format），§1–§7 的每个观测值都来自重跑后的最终
产物；`sha256` 在 selftest 之后再核一次，与 §1 表**逐位相同**（`00da5d5c…` / `c8d13b52…`），即交付的二进制就是
产出本报告证据的那一份。门禁 3 之外还跑了全 solution 的 `dotnet test WinForward.slnx -c Release --no-build`：
14 个测试工程全绿（`WinForward.E2E.Tests` 169 passed）。

## 9. 未落地与遗留登记

| 项 | 归属 |
|---|---|
| MIX 的 4 条改名（3 条 `desktops/*` + `udpLossRate`）与 `metrics/udpSent` 的 MIX 侧 | **B2b**（`ArmKeys.Mix`/`ArmKeys.Control` 分片、`MixArm`/`ControlArm` 类型化） |
| `LossArm`/`ReliabilityArm`/`PersistentArm`、`JsonValue` 退役、`Dictionary<string, object?>` 清零、字面量 gate | **B2c**（本轮未动；`DictionaryMetrics` 仍由这 4 个臂使用） |
| README 的 per-arm 契约表与"同一统计量四种拼写"段落（`README.md:357`、`361`、`362`、`364`、`382`、`384`、`387-389`、`430`）仍写旧拼写 | **E5**（契约表复核）。半改会让 B2b/B2c 的读者更困惑，故本轮不动 |
| `jsonl_paths.py:6` 的字母表示例仍用 `metrics/tcp.sentOk` | B2c 全量改名收尾时顺手改（字母表本身未变，示例只说明点号不拆） |
| `benchmarks/results/2026-10-06-e2e-competitors/analysis/**` 的冻结 Python 参考与 `make_tree.py` 仍产出旧拼写 | **E4**：按 D6.4"只改字段名、逻辑一行不动"在重新冻结 A 的那个提交里对齐；冻结物不在本轮授权内 |
| `ControlArm` 的 latency 相位引用 `LatencyMetrics` 的部分 | 本轮**未动** `BaseArm`；但共享 record 使 BASE 的两条改名顺带落地（§3 第 2 点） |
