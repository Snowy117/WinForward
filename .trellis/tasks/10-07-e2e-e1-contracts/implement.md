# E1 执行计划（批次化，2026-10-07 计划审查后修订）

本文件是 E1 的可施工计划。**效力**：父目录 `design-decisions.md`（下称 DD，尤其 **D14**）> 父
`prd.md`/`design.md`/`implement.md` > 本目录 `prd.md` > 本文件。E1 切成三个 **impl → check** 批次，
每批次以可验证的门禁结束。

对应需求：R3 / R4 / R11（部分）/ R13；**AC1–AC4 / AC7 / AC11 / AC15**。

---

## 批次总览

| 批次 | 内容 | 结束判据 | 提交 |
|---|---|---|---|
| **E1-A** | 回归基线 + 测试工程 + Tier 0 止血（D1–D7、`selftest.sh`、`--label`）+ README 4 小节 | 单测全绿；每个最小 plan 得到预期 load error / 退出码；selftest 绿 | 逐条（0.1+0.2 同提交） |
| **E1-B1** | `Contracts` 骨架 + `JsonlSink` + 路径盘点 + 改名表 + IDLE/THRU 走**生产** typed 路径 | 形状测试读**落盘 JSONL**；改名表落盘；build/test/selftest 绿 | 2–3 个 |
| **E1-B2** | 其余 6 臂 + `ControlArm` + `ClientRunner` 类型化；字典清零；字面量 gate | `Dictionary<string, object?>` 限定在值对象内部；新旧路径差 == 改名表；全量门禁绿 | 2–3 个 |

**每个批次结束（每次 commit 前）的硬门禁**（DD D14.11）：
```bash
cd benchmarks/WinForward.E2E && scripts/publish.sh        # 必须重建！selftest.sh 只在产物缺失时才 publish
dotnet build WinForward.slnx -c Release                    # 零警告
dotnet test  tests/WinForward.E2E.Tests -c Release         # 绿
cd benchmarks/WinForward.E2E && scripts/selftest.sh scripts/plans/selftest-plan.json
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # 空输出
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # 零 <Issue>
```
时间预算：publish + build 数分钟、format 数分钟、inspectcode 10–20 分钟（别藏在"顺手跑一下"里）。

---

## E1-A：回归基线 + 安全网 + Tier 0 止血

### A0 回归基线（**必须最先，且期间不得有并行构建/编辑**）

1. **强制重建二进制**：`benchmarks/WinForward.E2E/scripts/publish.sh`（或先删 `${WF_PUB:-/tmp}/wf-bench/pub/linux`）
   —— `selftest.sh:15-18` 只在产物不存在时才 publish，不重建会拿旧二进制当"未修改树的基线"。
2. 记录到 `research/baseline/`：`git rev-parse HEAD`、`git status --porcelain`、发布二进制的 `sha256sum`。
3. 跑 **两次** `scripts/selftest.sh scripts/plans/selftest-plan.json`，每次结束后**立即**把
   `$work/out/`、`$work/ledger.jsonl`、`$work/target.out` 与完整脚本输出（`tee`）拷到
   `research/baseline/run1|run2/`（`$work` = `selftest.sh:8` 的 `/tmp/wf-bench/selftest`；
   第二次会覆盖它，不拷就丢）。
4. 落盘脚本（**全部在 `benchmarks/WinForward.E2E/scripts/`，仓库根没有 `scripts/`**）：
   `jsonl_paths.py`（flatten，**被盘点与比对共用**）+ `compare-records.py` + 配置 `research/record-normalize.json`
   （DD D7/D14.6/D14.23）：
   - 比对**范围**：`<arm>.jsonl` 与 `run.json` 的路径集合 + `ledger.jsonl` + `target.out`（文本归一化）；
   - 三栏输出：① 结构性（键集/类型/数组长度/字符串值）② 条件键差异（`UseTcp`/`UseUdp`/非空直方图，单列）③ 数值差异；
   - **逐键抖动带**（run1↔run2 实测带宽），不得用全局 tolerance。
5. 自检：`run1` vs `run1` → 空 diff；`run1` vs `run2` → 只报抖动带内差异；报告落
   `research/baseline/compare-run1-run2.md`，首行声明基线二进制 sha256。

### A1 测试工程骨架 `tests/WinForward.E2E.Tests`

- csproj **照抄** `tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj` 的形状（Test.Sdk + xunit +
  xunit.runner.visualstudio，中央版本管理**不写版本号**，`<IsPackable>false</IsPackable>`），
  **并显式补 `<IsTestProject>true</IsTestProject>`**——模板里没有这一项，漏掉就会吃进
  `Directory.Build.props:14` 的 4 个分析器包；`ProjectReference` 指 `../../benchmarks/WinForward.E2E/WinForward.E2E.csproj`。
- `WinForward.slnx` 的 `/tests/` 加本工程；`WinForward.E2E.csproj` 加
  `<InternalsVisibleTo Include="WinForward.E2E.Tests" />`。
- **纯函数测试**（design §6 层一；DD D14.22 的归属切分）：
  1. `Crc32C`：长度 `0/1/4/8/9/16/17` 的向量，覆盖 SSE4.2 与表尾三条分支（`Crc32C.cs:18-38`）；
     **锚点必须写死**：空串 → `0`、`"123456789"` → `0xE3069283`（design §6 层一 #1），其余向量由测试内独立重算，
     不得"记录当前实现输出"。
  2. `Filler`：`Fill`/`Matches` 对称 + 固定 `(conn,seq)` 前 16 字节黄金向量 + 固化高 32 位截断事实。
  3. `FrameCodec`：往返 + 四种错误各一条 + 固化"尾部多余字节被接受"。
  4. `DnsWire`：查询/响应往返 + 压缩指针 QNAME + 超长名 + 5 种 queryType 的 `answerCount`。
  5. `TcpCommand`：**回归护栏**（今天必绿）——distinct 名字数 == 成员数，且已定义成员不得映射到兜底字面量。
  6. `LedgerWriter` 格式快照（`utc`/`label` 在前、`\n` 结尾、字段集合、`WriteErrors`）——B1 合一时改为对
     `JsonlSink(path, SwallowAndCount, envelope: 写 utc+label)` 的**等价断言**，`targetSummary.ledgerWriteErrors`
     键名不变。
  7. `FrameStreamReader`：加 internal 读委托 ctor（DD D14.18），用脚本化委托喂"一帧拆三段"；
     **EOF 落帧中间 → 当前行为（`EndOfStream`）固化**，注释标明 `// E3 D9: 将改为 Truncated`。
  8. `LogHistogram`：分桶、百分位、天花板饱和；`Record(long.MaxValue)` 不抛。
  9. **`UdpReliabilityTracker` 记账恒等式 + `OutOfRange` 上界**（纯逻辑，无并发；并发契约归 E2、口径归 E3）。
  10. **`PlanFile`**：11 份 shipped plan 全部通过 + **内置默认 plan `TryLoad(null)` 通过** + 未知 key 被拒
      （AC7：错误含 arm 与 key）+ **`#6` 的 W 判据**（遍历 11 份 plan，凡 `kind ∈ {loss,mix}` 的 arm 必须声明
      `lossWindowMs`，失败打印 plan 路径 + arm 名）。
      **注意**：空串由 CLI 层拒绝，`TryLoad("")` **不在契约内**（`TryReadPlanBytes` 只 catch
      `IOException`/`UnauthorizedAccessException`，空串会抛 `ArgumentException`）——不要写这条用例，
      或在 A2 0.4 顺手补 catch 后注明。
- 反射约束（DD D14.20）：形状测试只用 `typeof(...)` 字面量；测试工程仍吃 trim/AOT 分析器。

### A2 Tier 0 止血（逐条）

| 步骤 | 位置（改哪个文件/方法） | 内容 | 判据 |
|---|---|---|---|
| 0.1 | `Client/UdpReliability.cs` 的 `MarkSent`（`:196-209`） | 越界守卫提到方法开头（仍调 `_sent.TrySet(sequence)`；不写两数组） | 单测按 **D14.5** 配方；**禁用端到端越界 plan 作判据** |
| 0.2 | 新建 `Client/Arms/ArmKind.cs`（描述符表，形状见 **D14.3**）；`Client/PlanFile.cs`（`:50` 的 `s_knownKinds`、`:267-321` 的读键路径）；`Client/Arms/ArmDispatch.cs`（`:5-17` 改查表） | 白名单 + 按 kind 的 `Validate` + `MaxSequence` 校验（**D14.4**）+ `modeMix` 等文本键；逐 kind 键清单见 D14.3 | 单测覆盖 262143/262144 边界（loss 与 base 各一条）+ 未知 key 拒绝；`20000/s × 20s` → 退出码 2、错误含 arm/offered/`MaxSequence`。**与 0.1 同 commit** |
| 0.3 | `Client/ClientRunner.cs` 的 `RunArmAsync` catch 阶梯（`:258-277`，**放宽为 `catch (Exception)`，取消单独判**）+ 写 `error` 记录/`run.json.failed` 的路径；`Program.cs`（`:68-90`、`:92-110`）两 verb 各加顶层 catch **仅作最后兜底** | 臂级异常 → `error` 记录（**D14.12 字段集**）+ `run.json.failed:true`，退出码 1 | D1/D4 最小 plan：退出码 1 + `error` 记录 + `failed:true` + `run.json` 存在。**只加 Program 层 catch 判据必红**（异常绕过写记录路径） |
| 0.4 | `Client/ClientRunner.cs` 的 `TryApply` `--plan` 分支（`:120-122`）判空；`PlanFile.cs:125` 改 `path is null`；`ClientRunner.cs:445` 保持 `is null`（**D14.1**） | 显式空值 → usage error；缺省 → 内置 plan | `--plan=` 与 `--plan ""` → 2；省略 → 正常跑内置 plan 且 `planPath:null`；另加 `planSource`（**D14.23**） |
| 0.5 | `Client/PlanFile.cs` 的重名检查（`:103`、`:206-215`）与臂名 sanitize | 重名检查用映射后名字；sanitize 后 ≤ **128** 字符；运行前输出路径长度检查 | `A/B`+`A_B`、270 字臂名 → load error；错误信息含原臂名/映射名/长度 |
| 0.6 | `Client/PlanFile.cs` 的 `ReadArmNumbers`（`:267-283`）+ `TryReadInt`（`:301-321`） | 取值域校验（**D14.14 表**）+ 三态读取（删 `ReadInt` 与 15 个调用点，加 int 范围判断，`1e-9` 提常量） | `dnsPort:99999`、`window:100.5` 各一条；`dnsPort:0` 合法（= 未声明） |
| 0.7 | `Client/ClientRunner.cs:129-131`（空 `--sampler-process`）；`benchmarks/WinForward.E2E/scripts/selftest.sh`（`:32-37` usage → `exit 2`，**usage 检查移到起 target 之前**，删 `ls` 那行，client 完整输出落 `$work/client.out`） | 不再静默 | 单测 + 两条手工命令 |
| 0.8 | `Client/ClientRunner.cs` 的取值分支 `TryCreate`/`TryApply`（`:61-82`；E2 随 `Cli/CommandLine.cs` 只搬不改） | 前导 `-` 检查（**仅字符串类选项**，**D14.23**） | `--label --out x` → 2 且报错指名 `--label` 与值。**target 侧不在本批** |
| 0.9 | `Client/UdpReliability.cs:127-129` | 注释改不变量陈述并指向上游加载期校验（零行为风险） | 代码评审 |

- 最小 plan 夹具放 `tests/WinForward.E2E.Tests/Fixtures/plans/*.json`（**不要**放 `scripts/plans/`）。
- 退出码判据的执行通道见 **D14.19**：单元层断言 `TryLoad` 错误文本；退出码用发布二进制跑并在 check 报告里记录命令与观测值。
- **不碰记录形状**；`outOfRangeSequences` 单测路径与 0.2 的门禁互斥是**有意的**。
- **README 同提交更新**（DD D14.10）：`:122-142`（选项+退出码）、`:168-175`（plan schema：未知 key 是硬错误）、
  `:176-197`（各键取值域与"0 = 未声明"）、`:238-240`（`error` 记录字段集 + `run.json.planSource`）、
  `:531-535`（序列上界与越界槽记账）。

### A3 提交切分与 message 约定（仓库风格 `type(scope): imperative`，harness 用 `bench`）

| commit | 内容 |
|---|---|
| 1 | A0：`jsonl_paths.py` + `compare-records.py` + `record-normalize.json` + `research/baseline/**`（脚本+基线产物一起） |
| 2 | A1：测试工程（csproj + slnx + IVT + 纯函数用例） |
| 3 | 0.1 + 0.2（**必须同 commit**） |
| 4–8 | 0.3 / 0.4 / 0.5 / 0.6 / 0.7 / 0.8 / 0.9 各自独立 commit（可与对应 README 小节同 commit） |
| 9 | `#6` 的 W 判据 + 备用用例（若已随 0.2 落地则并入 0.2 的 commit） |

每条 message 一句话说明"把静默/崩溃变成什么"，并在 body 引用对应缺陷编号（D1–D7）。

### A4 批次结束判据

- 单测全绿；每个最小 plan 得到预期 load error / 退出码（命令与观测值进 check 报告）；selftest 绿；
  文首 6 条硬门禁全过。

---

## E1-B1：Contracts 骨架 + JsonlSink + 盘点/改名 + 两个臂走生产路径

### B1.1 新项目 `benchmarks/WinForward.E2E.Contracts`

- `net10.0` 库（无 `OutputType`），加进 `WinForward.slnx` 的 `/benchmarks/`；`WinForward.E2E` 引用它。
  类型一律 `public`（DD D14.20）；Contracts 自身要过 4 个分析器包 + `TreatWarningsAsErrors`。
- `ArmKeys` **按族分片**（DD D14.17）：每个分片 ≤ 400 有效行；形态 A（值含点号）用于扁平命名，
  形态 B（嵌套类 + 纯叶子值）用于真嵌套；writer 只引用常量；非条件 nullable 字段总是写出键（未知 ⇒ JSON `null`），
  条件字段（如纯 UDP 臂的 `tcp.*`）整键省略。
- `Json/Rate.cs`（`long,long → double?`，6 位）、`Json/PerSecond.cs`（`ticks <= 0 ⇒ null`）、
  `Json/NumberFormat.cs`（`Round(double,int=3)`、`Microseconds(long)`，**数值语义不得变**）。
- `Json/JsonlSink.cs`：契约见 **DD D14.7**（策略、body/close/dispose、调用点清单、`WriteErrors`、可注入 flush 间隔、
  internal `Stream` 重载、客户端用的 `CompleteAsync()`）。
- `Records/`：`ArmResult`、`ArmSummary`、`RunRecord`、`ErrorRecord`。
- **不做** `JsonValue.Write` 的 `default: throw`——该 dispatcher 在 B2 随 `JsonValue` 删除（DD D14.23）。

### B1.2 走通"生产路径"的强类型机制（DD D14.15）

- `IJsonWritable.WriteTo(Utf8JsonWriter)`；`ClientRunner.WriteResultAsync` 改为写 `IJsonWritable`；
  未迁移的臂用过渡适配器 `DictionaryMetrics : IJsonWritable`（B2 逐个删除）。
- IDLE/THRU 两个臂先迁移；**形状测试直接读落盘的 `.jsonl` 字节**（测试面 == 生产面）。
- 测试侧显式工厂（`new IdleMetrics { ... }`，属性全 `required`）⇒ 新增属性会编译失败。

### B1.3 路径盘点与改名表

1. 跑一次 selftest（仍是旧契约），用 `scripts/contract-inventory.py` + 共用的 `jsonl_paths.py` 提取全部路径
   （记录 + 账本）到 `research/contract-inventory.json`。
2. 产出 `research/contract-rename.json`（全量，含 `identical`）与 `research/contract-rename.md`（只列 changed +
   分组统计）；字母表见 DD D7 第 4 条。
3. 改名规则（DD D5/design §2.2.1）：只收敛同一统计量的多种拼写（`sentOk`/`sent`/`udpSent`；
   `lossRate`/`udpLossRate`；`arrived`/`udpArrived`；`foreignConnection`/`udpForeignConnection`）；
   保留结构性分组（`classes.*`、BASE 相位、`tcp.*`/`udp.*`）；其余默认不动。

### B1.4 B1 结束判据

- 形状测试在 IDLE/THRU 上读**生产 JSONL** 且绿；null 用例、条件字段两种 flags、数组 arity 断言各就位。
  **数组 arity 清单**：`metrics.tcp.laneSupplied`/`laneSentOk` 长度 == 该臂 tcp lane 数（`LatencyArm.cs:256-257`）；
  MIX 的 `metrics.lanes` 长度 == desktops（`MixArm.cs:263-265`）。
- `LedgerWriter` 快照测试已迁为 `JsonlSink`（`SwallowAndCount` + envelope 写 `utc`+`label`）等价断言，
  `WriteErrors` 与 `targetSummary.ledgerWriteErrors` 键名不变（M5）。
- 全量门禁（见文首）+ `dotnet format` + `jb inspectcode`。
- 归一化比对中每一处差异都能在改名表找到出处。

---

## E1-B2：其余臂 + ControlArm + ClientRunner 类型化

- 6 个臂（`LatencyArm`/`MixArm`/`DnsArm`/`LossArm`/`ReliabilityArm`/`PersistentArm`）迁移到强类型；
  迁移一个删一个 `DictionaryMetrics` 用法。
- `BaseArm` → **纯改名** `ControlArm`（`kind:"base"` 不动）；直接持有子臂强类型结果；
  删 `ReadCount`/`ReadMilliseconds`（AC2 点名）。
- `ClientRunner` 的 4 个 `Write*Async` 改用 `JsonlSink` + `ArmKeys`；`JsonValue`/`JsonlFile` 退役
  （`Ratio`/`PerSecond`/`NumberFormat` 已搬进 Contracts，**不留别名**）；全部调用点同提交改完。
- `PerSecond` 的 9 个调用点逐个处理：`LatencyArm.cs:241-242` 是算术消费者（显式处理 null）、
  `:349/:356` 的插值格式化支持 null（DD D14.23）。
- `parameters`/`metrics` 强类型；`gates` 保留 `Dictionary<string,long>`，键由 `ArmKeys.Gates` 常量约束。
- 字典清零范围见 **DD D14.21**：只允许值对象内部构造嵌套块，删除返回字典的辅助函数。
- 字面量 gate（**DD D14.16**）：① `rg` 在 `Client/Arms/**` 与 4 个 writer 内无 `writer.Write*("…")` 命中；
  ② xunit 源扫描测试断言已知键名不以字面量出现。
- 形状/改名判据全量执行（覆盖 8 个 kind + 两组 flags）；`symmetric_difference(旧, 新) == rename_pairs`
  （`added`/`removed` 各自计入对应方向）；`#6` 的行为断言 `metrics.window == 声明值`（`LossArm`/`MixArm` 各一条）。
- 全量门禁（文首 5 条）+ AC1 行数抽查（E1 新增/改动文件）。

---

## 证据与落盘清单（E1 结束必须存在，且随任务提交——DD D14.9）

| 产物 | 位置 |
|---|---|
| 基线（两次运行 + 抖动带报告 + commit/sha256） | `research/baseline/` |
| 归一化配置 + 比对脚本 + 共用 flatten | `research/record-normalize.json`、`benchmarks/WinForward.E2E/scripts/{compare-records,jsonl_paths,contract-inventory}.py` |
| 路径盘点 | `research/contract-inventory.json` |
| 改名表（机器可读 + 人读） | `research/contract-rename.json`、`research/contract-rename.md` |
| Tier 0 夹具与退出码证据 | `tests/WinForward.E2E.Tests/Fixtures/plans/` + check 报告的命令与观测值 |
| 单测索引（缺陷编号 → 测试名） | `research/semantic-fixes/index.jsonl` 的 E1 条目（E3 续写） |

## 风险与回退

- **改名半途状态不可用** → B2 整体回退；**`JsonlSink` 动 I/O 生命周期** → 单测（含抛异常 body/流）+
  ledger 格式快照 + selftest 三重保护；**`ArmKind` 表误拒文本键** → `Keys[]` 必含 `protocol`/`modeMix`
  （3 份 plan 依赖，含 AC12 的 `selftest-plan.json`）；**基线失效** → A0 强制重建 + sha256 记录 + 期间禁并行构建。
- **安全点**：E1-A 结束、E1-B1 未开始时——止血 + 测试就位、契约未动，任何失败都能退回且仍优于今天。
