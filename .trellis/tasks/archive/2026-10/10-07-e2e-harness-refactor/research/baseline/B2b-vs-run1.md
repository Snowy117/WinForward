# B2b 回归比对（MIX 类型化 + ControlArm 纯改名 + 5 条改名，对照基线 run1）

本轮把 `mix` 臂的 `metrics` 迁到强类型 record（嵌套的 `classes` 四块 + `desktops[]` 元素类型），把
`BaseArm` **纯改名**为 `ControlArm`（`kind:"base"` 与 plan/记录文本一字不改），新增 `ArmKeys.Mix` /
`ArmKeys.Control` 两个分片，执行改名表 `batch: "B2"` 余下的 **5 条**，并把形状测试扩展到这两个 kind
（`classes.*` 层级、`desktops[]` arity、null/条件用例、块省略语义）。顺手修 D16.3
（`--band` 与 `--write-band` 同时给出直接报错）。

判定线：**结构差异只剩改名表登记的键集变化；契约类 0 越带；身份类 0 finding；读数类只作信息性输出；
`--batch B2` 必须 9/9 satisfied 且 exit 0**。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh                     # 强制重建三份产物，退出码 0
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json             # 退出码 0
# 运行后立即把 /tmp/wf-bench/selftest/{out,ledger.jsonl,target.out,client.out} 拷到 /tmp/b2b-run/
R=.trellis/tasks/10-07-e2e-harness-refactor/research
CR=benchmarks/WinForward.E2E/scripts/compare-records.py
python3 $CR $R/baseline/run1 /tmp/b2b-run --normalize $R/record-normalize.json \
        --band $R/baseline/jitter-band.json --rename-table $R/contract-rename.json --batch B2 \
        --json-out /tmp/b2b/findings-b2b.json                    # 退出码 0
```

| 侧 | 树 | `linux/WinForward.E2E.dll` sha256 | `linux/WinForward.E2E.Contracts.dll` sha256 |
|---|---|---|---|
| `run1`（基线） | A0：HEAD `d90707e` | `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58` | 无此文件 |
| `b2b`（本轮） | HEAD `048ae04` + 本轮未提交工作树 | `4d09e7c051b69e0ba849a52d2894b1d9bf599b5a5c57da697534dec2c7e4f54e` | `fb6d135ce59bc593cd3312daffef39ebb228e40581c0ef1d75e53708a3ae08b0` |

`WinForward.E2E.Contracts.dll` 的 sha256 来自**最后**一次 publish（check 收窄 `ArmKeys.Mix` 的抑制之后，
收窄前的构建是 `3bfc95f0c4c22c73ea2c53a8ef217715581ca292d8a08365c124aa788cb8637f`）；check 已用
`PEReader` 逐方法取 IL 字节与成员表做 SHA256，确认收窄前后的两份构建
`IL-SHA256 = 422D2B75BBA3D907C01D0A06CE6ED2F8282E8A166E1B1E465C05D7D955AE44DA`、
`API-SHA256 = 5E15EBBC6DAA3B764283F396DA14F6EA3EE38835D0A3E3BC5F0ED2D8B74E14CE` 逐字节相同——
只有 MVID（因此文件 sha256）随重编译变化。收窄只动了源码里的注释与 `#pragma`/`// ReSharper` 指令，
没有生成任何成员或 IL 差异。harness 的 `WinForward.E2E.dll` sha256 收窄前后逐位相同。

本轮的 selftest 产物留在 `/tmp/b2b-run/`，未随任务提交（与 B1b/B1c/B2a 相同：run1/run2 是基线定义，
本轮是回归检查，四条命令随时可重建）。两个 sha256 取自 §1 命令序列里 `publish.sh` 产出的那一份；
check 的复跑产物在 `/tmp/final-run`、`/tmp/final-run2`，不随任务提交（§10）。

## 2. 三次比对的四类计数

| 比对 | 结构 | 条件键 | 身份 | 契约 | 已声明 | 改名 | 读数（信息性） | 退出码 |
|---|---|---|---|---|---|---|---|---|
| `run1 vs /tmp/b2b-run --band --rename-table --batch B2` | **0** | **0** | **0** | **0** | 27 | **0** | 285/349 | **0** |
| 控制 A：`run1 vs 文本改名后的 run1`（无 band，带改名表） | **0** | **0** | **0** | **0**（带宽 0） | 26 | 0 | **0/349** | **0** |
| 控制 B：`文本改名后的 run1 vs /tmp/b2b-run`（带 band） | **0** | **0** | **0** | 13（全是 fail-closed） | 1（`planSource`） | 0 | 285/349 | 1（只有那 13 条） |

- **结构栏 0**：键集、JSON 类型、数组长度、字符串值（含 `target.out` 归一化文本）全部相同或由改名表登记。
  `declared` 的 27 条 = 9 条旧拼写（`only in base 9 (hit 9)`）+ 8 条只在新侧出现的拼写
  （`only in after 8 (hit 8)`）+ `run.json planSource`；9 条新拼写里有 1 条（`metrics/udp.sent` 的 DNS 侧）
  在 run1 就存在，所以新侧只多 8 条，工具的分母/分子自洽。
- **契约栏 0 越带**：534→529 个契约对在两次独立运行之间全部落在逐键带宽内；`band = 0` 的控制 A 里契约值
  **逐位相同**，所以本轮改名没有动过任何一个计数值。
- **身份栏 0 finding**：12 个身份数值对（`*/pid` 11 + `sources/port` 1）只查存在与类型。
- **读数栏 285/349 移动**：按 D15 **信息性**输出，不进退出码。
- 控制 B 的 13 条契约 finding **一条 `exceeds` 都没有**，全是 `no recorded jitter band for this path`：
  冻结带宽是在旧拼写下测的，9 个新拼写（含 `metrics/tcp.sent`/`udp.sent` 这类多臂共用的）在它里面没有条目，
  工具 fail-closed 报出来。这正是 B2a §5 控制 ② 的同一条解释：**是"新拼写没有带宽条目"，不是"值越带"**。

## 3. 改名执行情况（`--batch B2` 9/9）

```
  table: 592 entries = added 3, identical 580, renamed 9
  path sets: base 589, after 588; only in base 9 (hit 9), only in after 8 (hit 8)
  renamed observed: landed 9, pending 0 (0 of them already publishing the target spelling), vanished 0
  added observed: 1, not observed 2
  executed batch: B2; 9 entries required, 9 satisfied, 0 not observed
  declared additions not observed: detail, error
```

B2a 结束时是 `4 satisfied, 5 not observed` 并退出 1；本轮把余下 5 条落地后是 **9 satisfied, 0 not observed**，
整轮比对退出 **0**。

| # | old → new | 发布者 | 本轮状态 |
|---|---|---|---|
| 1 | `metrics/tcp.sentOk` → `metrics/tcp.sent` | LAT, LATLOAD | B2a 已落地 |
| 2 | `metrics/udp.sentOk` → `metrics/udp.sent` | LAT, LATLOAD | B2a 已落地 |
| 3 | `metrics/udpSent` → `metrics/udp.sent` | DNS, DNSALT, **MIX** | B2a 只落 DNS 侧；本轮 `MixMetrics.UdpSent` 落 MIX 侧，**landed** |
| 4 | `metrics/latency/tcp.sentOk` → `metrics/latency/tcp.sent` | BASE | B2a 顺带（共享 `LatencyMetrics`） |
| 5 | `metrics/latency/udp.sentOk` → `metrics/latency/udp.sent` | BASE | B2a 顺带，同上 |
| 6 | `metrics/desktops/udpArrived` → `metrics/desktops/udp.arrived` | MIX | **本轮**（`MixDesktopMetrics.UdpArrived`） |
| 7 | `metrics/desktops/udpForeignConnection` → `metrics/desktops/udp.foreignConnection` | MIX | **本轮**（`MixDesktopMetrics.UdpForeignConnection`） |
| 8 | `metrics/desktops/udpSent` → `metrics/desktops/udp.sent` | MIX | **本轮**（`MixDesktopMetrics.UdpSent`） |
| 9 | `metrics/udpLossRate` → `metrics/udp.lossRate` | MIX | **本轮**（`MixMetrics.UdpLossRate`） |

第 4/5 条与 `ArmKeys.Control` 的关系：BASE 的 latency 相位值就是共享的 `LatencyMetrics`，它的写出常量是
`ArmKeys.Latency`，所以相位里那两条随 record 一起改名，`ArmKeys.Control` 只需要声明相位容器
（`metrics/latency`、`metrics/loss`）与相位级字段。

**反例（本轮不该动的名字一个没动）**：`metrics/classes/udp/foreignConnection`、`metrics/classes/udp/sent`、
`metrics/classes/udp/lossRate`、`metrics/classes/udp/arrived` 都是 `identical` 条目（它们本来就是目标拼写，
且在另一层级）；`metrics/desktops/udpNever` 不在改名表里（`never` 不是被收敛的量），工具也如实把它算作
`identical`。

## 4. MIX 的 `classes.*` 层级与 arity 核验

落盘记录（`/tmp/b2b-run/out/MIX.jsonl`，本机 selftest plan 声明 `desktops: 2`）：

```
metrics 顶层成员（文档序）  = classes, pages, pageConnections, pageBytes, bulkBytes, dnsSent,
                              udp.sent, udp.lossRate, clientSendLoss, desktops
classes.udp 成员（19 个）   = sent, arrived, late, never, corrupt, corruptDatagrams, duplicate,
                              reordered, unmatchedReplies, foreignConnection, abandonedAtTeardown,
                              sendFailures, windowOverflow, outOfRangeSequences, bytes,
                              clientSendLoss, lossRate, window, sentPerDesktop
desktops 元素成员（8 个）   = desktop, udp.sent, udp.arrived, udpNever, udp.foreignConnection,
                              pageConnections, bulkFrames, dnsSent
```

- **层级正确**：`classes` 是真对象，下一层 `page`/`bulk`/`dns`/`udp` 也是真对象；同一叶子名在不同层级各有
  一个常量（`ArmKeys.Mix.Pages` = `metrics/pages`、`ArmKeys.Mix.PageClass.Pages` =
  `metrics/classes/page/pages`），`ArmKeys.Mix` 共 **57 个常量**，与该记录写出的 57 条 `metrics/*` 路径一一对应。
- **arity**：`metrics/desktops` 与 `metrics/classes/udp/sentPerDesktop` 的长度都 == 2 ==
  `parameters.desktops`（同一份 selftest plan 声明 2 个 desktop）；形状测试另按工厂的 4 desktop 断言一次，
  负控见 §6。
- **点号不拆**：`metrics/udp.sent`、`metrics/udp.lossRate` 与桌面元素里的 `udp.sent`/`udp.arrived`/
  `udp.foreignConnection` 都是"键里带点"的单个成员名，走 `ArmKeys.Mix.UdpSent` /
  `ArmKeys.Mix.DesktopLane.UdpSent` 这类常量，没有被展开成 `udp/sent`。

## 5. BASE 两个相位块的字节形状

落盘记录（`/tmp/b2b-run/out/BASE.jsonl`）：

```
metrics 成员         = elapsedSeconds, latency, loss
metrics/latency      = 对象，42 个成员（tcp.* 23 + udp.* 19），成员名是"点号成员"而不是嵌套对象
metrics/loss         = 对象，28 个成员（loss 臂自己的键，B2c 之前仍由 DictionaryMetrics 写出）
sentOk 残留          = 无（tcp.sent / udp.sent）
```

- **没有双层**：`metrics/latency` 之后直接是 `metrics/latency/tcp.laneStarted`、`metrics/latency/tcp.sent`
  这类路径，产物里**不存在** `metrics/latency/tcp/...`（控制 A 的"逐字节改名"比对与形状测试的
  "写错层级必须红"负控共同覆盖这一点）。
- `ArmKeys.Control` 共 **31 个常量** = 相位级 3（`elapsedSeconds`、`latency`、`loss`）+ loss 相位成员 28；
  latency 相位的 42 个键由 `ArmKeys.Latency` 组合到 `metrics/latency/` 前缀下（与
  `RecordContract.Histogram` 的组合方式相同），因此控制 kind 声明 73 条 `metrics/*` 路径 = 3 + 42 + 28。
- 条件字段登记：`metrics/latency/tcp.*`（plan 声明 udp-only 时整块省略）与 `metrics/latency/udp.*`
  （tcp-only 时整块省略）在 `ArmKeys.Control` 的 `<remarks>` 里登记；两个相位容器键恒在。loss 相位无条件字段。
- 相位级字段的 null 语义：latency 的 4 个（`tcp.achievedRate`/`tcp.meanConnectMs`/`udp.achievedRate`/
  `udp.lossRate`）+ loss 的 8 个（`lossRate`/`strictLossRate`/`lateRate`/`corruptRate`/`duplicateRate`/
  `reorderRate`/`clientSendLossRate`/`achievedRate`）各有一条 null 用例。

## 6. 形状测试的四个负控（"红得起来"的证明）

正控：`dotnet test tests/WinForward.E2E.Tests -c Release` → **169 passed**（B2a：169，本轮测试数量不变，
新增的两个 kind 复用同一组 10 个用例）。四条负控都是**临时改一处、看它红、再原样还原**（还原后 `cmp` 逐字节核过，
再跑一次全绿）：

| 负控 | 改动 | 观测 |
|---|---|---|
| 写错层级 | `MixUdpClassMetrics.WriteTo` 里把 `ArmKeys.Mix.UdpClass.Sent` 写成 `ArmKeys.Mix.UdpSent` | 4 个用例红，报 `declared but not written: metrics/classes/udp/sent` + `written but not declared: metrics/classes/udp/udp.sent`（两个方向都点名） |
| arity 不符 | `MixShape` 的 `Desktops` 由 4 改 3 | `EveryMigratedKindWritesExactlyTheMetricPathsItsKeysDeclare` 报 `metrics/classes/udp/sentPerDesktop: declared 3 element(s), measured 4` 与 `metrics/desktops: declared 3 …` |
| 常量与属性数不齐 | `ArmKeys.Mix.PageClass` 多一个常量 | `TheKeySetAndTheRecordDeclareTheSameMembers` 报 `mix metrics/classes/page/: the record declares 6 key-publishing propert(ies) … but ArmKeys declares 7 key(s) directly under it` |
| 嵌套块的省略登记漏一块 | CONTROL 的 `metrics/latency/tcp.` 块 `OmittedWhen` 由 `UdpOnly` 改成 `None` | `AConditionalBlockIsOmittedWholeInItsFlagState` 报 `base conditional keys: 42 declared path(s), 19 written`，逐条列出 23 条 `metrics/latency/tcp.*`（控制 kind 的**嵌套**块省略语义确实在判） |

第三条同时是**嵌套块**计数恒等式的负控：计数检查现在递归走整棵块树（record 自己的键 = 它直接声明的路径数
+ "成员点进父对象、自己不写容器键"的子块数），所以 MIX 的 `classes`/各 class 块、CONTROL 的
`metrics/latency/` 节点与它下面的 `tcp.`/`udp.` 块都被逐层核对（B2a 的扁平原式已由递归式取代）。

## 7. 结构证据（比对范围之外的独立核对）

- **记录数逐一对齐**：`BASE 12`、`DNS 10`、`DNSALT 10`、`IDLE 7`、`LAT 10`、`LATLOAD 10`、`LOSS 12`、
  `MIX 12`、`PERSIST 12`、`REL 18`、`THRU 10`、`ledger 258` 与 run1 完全相同；`run.json` 多 1 行，
  就是改名表登记过的顶层 `planSource`（A2 引入）。
- **文档序（键序）逐位相同**：把 run1 的 `MIX.jsonl`/`BASE.jsonl` 按 5+2 条改名做**文本**替换后，
  与 b2b 的同一文件逐路径比较：`MIX 118/118`、`BASE 134/134` 条路径**顺序与路径名一一相同**
  （`declare` 序 == 写出序由单测 `TheMetricsAreWrittenInKeyDeclarationOrder` 覆盖）。
- **控制 A（改名是纯改名）**：run1 vs "文本改名后的 run1"，四类全 0、`declared 26`（13 实例 × 2 方向）、
  **读数 0/349 移动**、退出 0 —— 改名只换名字：值、类型、数组长度、其它路径、键序一律不动。
  其中 `"tcp.sentOk"`/`"udp.sentOk"` 各命中 3 处、`"udpSent"` 5 处、`"udpArrived"`/`"udpForeignConnection"`
  各 2 处、`"udpLossRate"` 1 处，正是 9 条改名在产物里的全部实例。
- **控制 B（强类型 writer 的产出形状 == 旧 writer 的产出形状）**：文本改名后的 run1 vs 真实 b2b 树，
  结构 0 / 条件 0 / 身份 0 / 已声明 1（只有 `planSource`）/ 契约 13 条全是 fail-closed 的"无带宽条目"、
  **无一条越带**、路径集 `only in base 0`/`only in after 1`。

## 8. 六条门禁

| # | 命令 | 观测 |
|---|---|---|
| 1 | `benchmarks/WinForward.E2E/scripts/publish.sh` | 退出 0；三份产物重建（§1 的 sha256） |
| 2 | `dotnet build WinForward.slnx -c Release` | `0 Warning(s) / 0 Error(s)` |
| 3 | `dotnet test tests/WinForward.E2E.Tests -c Release` | `Failed: 0, Passed: 169` |
| 4 | `scripts/selftest.sh scripts/plans/selftest-plan.json` | 退出 0（本轮 b2b 产物的来源） |
| 5 | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | 退出 0，**输出 0 字节** |
| 6 | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` | 报告 `<Report>` 下 **0 条 `<Issue>`**（解析 XML，不看退出码） |

补记（首轮 → 归零，全部按"改代码"而不是"放宽规则"处理）：

- 门禁 5 首轮报 **17 条**：`IDE0130` 6 条（`Shapes/` 目录与 namespace 不匹配 → 6 个文件改用
  `WinForward.E2E.Tests.Shapes`，并在 `ContractRegistry.cs` 加一条 `using`）、`IDE1006` 6 条
  （`IdleShape.Contract` 等 → `s_contract`，仓库既有 `s_` 私有静态字段约定）、`MA0154` 4 条
  （XML 注释里 `<c>base</c>` 看起来像关键字 → 写成 `<c>"base"</c>`：kind 名是**字符串字面量**，
  加引号比 `<see langword="base"/>` 更准确）、`CA1861` 1 条（`parameters.phases` 的数组实参 →
  工厂持有 `private static readonly string[] s_phases`，与 `ControlArm` 同形）。复跑输出 0 字节。
- 门禁 6 首轮报 **10 条**：`InvalidXmlDocComment` 8 条全是**未限定的 `<see cref>`**——文件级
  `<remarks>` 的宿主是外层 `ArmKeys` 类，R# 在**外层类**作用域解析 cref，所以嵌套类里的
  `PageClass.Bytes`/`UdpClass.SentPerDesktop`/`DesktopLane.*`/`LossPhase` 要写成
  `Mix.PageClass.Bytes`/`Mix.UdpClass.SentPerDesktop`/`Mix.DesktopLane.*`/`Control.LossPhase`
  （与 B2a 同一处理，无抑制）；`RedundantUsingDirective` 1 条（`ContractRegistry.cs` 不再需要
  `...Contracts.Metrics`）、`UnusedMember.Global` 1 条（`MetricsContract.OmittedPaths()` 没有被用上，
  直接删掉，改由条件用例就地按块收集）。改了代码后门禁 1–5 全部重跑，§1–§7 的每个观测值都来自重跑后的
  最终产物；sha256 在 selftest 之后再核一次，与 §1 表逐位相同。

## 9. 未落地与遗留登记

| 项 | 归属 |
|---|---|
| `LossArm`/`ReliabilityArm`/`PersistentArm` 类型化、`JsonValue` 退役、`Dictionary<string, object?>` 清零、`parameters` 强类型化、字面量 gate（D14.16） | **B2c** |
| `ArmKeys.Control.LossPhase`（28 个常量）是 loss 相位键在 B2b 的临时家；B2c 建 `ArmKeys.Loss` 后应改为"组合 `ArmKeys.Loss` 到 `metrics/loss`"，同时 `ArmKeys.Control.Loss` 这个容器常量会与 `ArmKeys.Loss` 同名 → 需要同款 `S3218`/`MemberHidesStaticFromOuterClass` 抑制（`ArmKeys.Control.Latency` 已有先例）或改名 | **B2c**（已在 `ArmKeys.Control` 的注释里写明） |
| 形状测试里 loss 块的 `PropertyCount` 是"声明键数"而不是反射读数（那一相位还没有 record 可反射）；B2c 把它换成 `typeof(LossMetrics)` 后计数恒等式立刻恢复编译期保护 | **B2c** |
| `MixArm.cs` **678 有效行**（本轮之前 672）与 E2E 其余 5 个超标文件的拆分 | **E2**（D15 已登记；本轮只把字典换成值对象，没有加接缝） |
| README 的 per-arm 契约表（`README.md:362` MIX 行）与判定标准（`:430` 的 `udpSent`）仍写旧拼写 | **E5**（契约表复核；B2a 已按同一口径登记） |
| `benchmarks/results/2026-10-06-e2e-competitors/analysis/**` 的冻结 Python 参考与 `make_tree.py` 仍产出旧拼写 | **E4**（按 D6.4 在重新冻结 A 的那个提交里对齐） |
| `jsonl_paths.py:6` 的字母表示例仍用 `metrics/tcp.sentOk` | B2c 全量改名收尾时顺手改（字母表本身未变，示例只说明点号不拆） |

## 10. check（2026-10-07，B2b 提交前）

check 独立重跑了六条门禁与三次比对，结论与 §2–§8 一致；两处**新观测**记在这里。

**`ArmKeys.Mix` 的抑制已收窄（check 修复的唯一代码改动）**：原先是"整个 `Mix` 类"的
`#pragma warning disable S3218` + `// ReSharper disable MemberHidesStaticFromOuterClass`。实测把两条同时关掉后
只报出 **6 个成员**（`ClassNames.Dns`、`PageClass.Pages`、`UdpClass.ClientSendLoss`、`UdpClass.UdpSent`、
`DesktopLane.PageConnections`、`DesktopLane.DnsSent`，两条规则报的是同一组），所以每条改为它自己的
单成员抑制，并各留一行 `<remarks>` 指出它遮蔽了哪个外层成员。收窄**未改动任何 IL**：用 `PEReader`
逐方法取 IL 字节 + 成员表做 SHA256，收窄前后两次构建分别是 `IL-SHA256 422D2B75…` /
`API-SHA256 5E15EBBC…`，逐字节相同（只有 MVID 随重编译变化，见 §1 对 Contracts dll sha256 的说明），
比较结果与单测（169 绿）也一致。

**`LATLOAD` 的三条计数键在冻结带宽里是零宽，宿主一抖动就会越带（不是本轮改动引起）**：check 的
第一次回归运行报了 9 条 `contract` exceeded，全部落在 `records/LATLOAD.jsonl`
（`latency/tcp-rtt/count` 1601→1600、`metrics/tcp.outstandingAtTeardown` 0→1、
`metrics/tcp.unmatchedReplies` 0→1）。判定为宿主抖动而非回归，证据有三条：

1. **同一份二进制**（§1 的 sha256）再跑一次：`contract=0`，三条键的值回到 run1 的 1601/0/0；
2. 越带的三条键在 `jitter-band.json` 里都是 `baseRange=[0,0]`、`afterRange=[0,0]`、`maxAbsDelta=0`，
   即 run1↔run2 两次恰好都取到同一个值——带宽是**零宽**，不是"这个量稳定"；
3. `LATLOAD` 本来就是 12.5k/s 的满载臂：两次 check 运行里 `latency/tcp-rtt/maxUs` 分别是 9295.688 与
   3774.476（run1 2702.153），说明第一次运行正撞上宿主争用，TCP 尾沿事件多发生了一次。
   `allowedCountDelta` 本来就是 1，说明工具预期这类计数有 ±1 漂移，但 `min/max/mean` 三列仍按带宽 0 判。

结论：**不阻塞 B2b**（改名与类型化都没有碰这三个键，结构/条件/身份三栏两次都是 0，`--batch B2`
两次都是 9/9 satisfied）。是否把 `LATLOAD` 这三条计数键的带宽从 0 放宽、或在带宽冻结时要求同一对
基线跑够次数，属于比对工具的口径问题，登记给 **E2/E5**（与 D16.3 的登记债同一处）。
