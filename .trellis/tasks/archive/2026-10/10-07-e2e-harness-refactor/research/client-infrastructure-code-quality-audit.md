# Client 基础设施代码质量审查

审查对象：`benchmarks/WinForward.E2E/` 下 12 个文件，2638 行。

审查分工：`.trellis/tasks/10-06-e2e-competitor-benchmark/research/harness-audit.md` 记录了**测量语义**
缺陷（计分、样本丢失、门限、公平性），本文**不重复**那 24 条。本文只看代码质量：可维护性、可测试性、
健壮性、重复、契约实现质量。

**与姊妹审查的关系（同一批并行产出，请去重）**：

| 文件 | 覆盖 | 与本篇的关系 |
|---|---|---|
| `10-06.../research/arms-code-quality-audit.md` | `Client/Arms/*` + `ArmContext.cs`、`UdpReliability.cs`、`FrameBuffer.cs` | 与本篇在 `ArmContext`/`UdpReliability`/`FrameBuffer` 三个文件上重叠。**以当前落盘版本核对，该篇没有提到序号上界（`MaxSequence`/262143/`IndexOutOfRange` 均无命中），即本篇的 D7 是其缺口**；`TryConnectAsync` 的判定两篇不同，已在 §9.4 标注 |
| `10-06.../research/wire-target-code-quality-audit.md` | `Wire/`、`Target/`，并覆盖了 `JsonlFile` 与 `LedgerWriter` 的重复 | 与本篇 §1.6 结论一致（同一份 sink 两处实现），可合并成一个任务 |
| 本篇 | `ClientRunner`、`ResourceSampler`、`PlanFile`、`LogHistogram`、`JsonValue`、`JsonlFile`、`Program`、`ExitCodes` + 上述重叠文件的**代码质量切片** | |

---

## 概览

| 文件 | 总行 | 有效行 | 有效占比 | 结论 |
|---|---|---|---|---|
| `Client/ClientRunner.cs` | 498 | **439** | 88% | 违反 AC1；6 个职责 |
| `Client/ResourceSampler.cs` | 547 | **474** | 86% | 违反 AC1；一个类型 5 件事；95 行复制粘贴 |
| `Client/PlanFile.cs` | 331 | 262 | 79% | 校验强度不一致，4 条可复现崩溃路径 |
| `Client/UdpReliability.cs` | 437 | 268 | 61% | 结构最好的一份；边界只护住了一半 |
| `Program.cs` | 174 | 153 | 87% | 顶层不兜异常，是所有 SIGABRT 的放大器 |
| `Client/ArmContext.cs` | 171 | 125 | 73% | `Clock` 无接缝；7 处同文 pragma 的根源 |
| `Client/LogHistogram.cs` | 157 | 131 | 83% | 数学正确；桶布局是输出契约，不能动 |
| `Client/JsonValue.cs` | 88 | 73 | 82% | 不是手写 JSON（见 §0） |
| `Client/JsonlFile.cs` | 80 | 71 | 88% | 与 `LedgerWriter` 是同一件东西的两份实现 |
| `Client/FrameBuffer.cs` | 83 | 70 | 84% | `TryConnectAsync` 把一个三态压成 bool |
| `Client/ArmSpec.cs` | 73 | 38 | 52% | 默认值住在 4 个地方 |
| `Cli/ExitCodes.cs` | 11 | 7 | 63% | 没问题；但 `Cli/` 里只有它一个 |

有效行 = 非空且不以 `//` 开头的行（含 `using` 与单括号行）。AC1 为「每文件 ≤ 400 有效行」。

### 7 条已实测复现的缺陷（本文新增，不在 10-06 审查里）

全部在 Linux 上跑本仓库 `bin/Release/net10.0/WinForward.E2E` 实测，命令与输出见各节。

| # | 现象 | 触发 | 后果 |
|---|---|---|---|
| **D7** | **UDP 序号越过 2¹⁸−1 → `IndexOutOfRangeException`，进程崩** | plan 里 `"seconds": 20, "ratePerSecond": 20000`（LossArm） | 退出码 134（SIGABRT）；臂跑了约 13 秒、文件里只有 13 条 `sample`、**没有 `result`、没有 `run.json`**。任何 `ratePerSecond × seconds > 262143` 的 LOSS/BASE 臂都会崩（默认 500/s 时是 525 秒） |
| D1 | 越界 `dnsPort` → `ArgumentOutOfRangeException` | plan 里 `"dnsPort": 99999` | 退出码 134，**没有 `run.json`**，臂文件 0 字节 |
| D2 | `--plan=`（显式空值）→ 静默改用内置默认 plan，随后崩在 `run.json` | `--plan=` | 跑的是 8 臂默认 plan，最后 `Path.GetFullPath("")` 抛 `ArgumentException`，留下**207 字节的非法 `run.json`** |
| D3 | `SanitizeFileName` 非单射 → 两个臂写同一个文件 | 臂名 `A/B` 与 `A_B` | 后一个臂 `FileMode.Create` **截断**前一个臂的全部记录；`run.json` 里两个臂指向同一文件、都 `failed:false`、退出 0 |
| D4 | 臂名过长 → `PathTooLongException` | 270 字臂名 | 退出码 134，输出目录空，无 `run.json` |
| D5 | 小数写进 int 键 → 静默回落默认值 | `"window": 100.5` | 发布 `inFlightWindow = 64`（臂默认），退出 0，无 error 无 note |
| D6 | 空 `--sampler-process=` 被接受 | `--sampler-process=` | 记录里出现 `"process":"","absent":true` 的垃圾序列 |

D1/D2/D4/D7 是同一个放大器（§3.3）：`ClientRunner.RunArmAsync` 只兜 4 种异常，
`Program` 顶层不兜任何异常，于是「臂级失败」一律升级成「进程崩溃 + 没有 `run.json`」。
D7 尤其值得注意：它的触发条件只写在 plan 里，不需要任何网络异常或产品行为。

### 与 PRD R4 的冲突，需要先定

本次任务约束是「输出 JSON 字段与数值语义、时序行为、CLI 参数都不能变」，而
`.trellis/tasks/10-07-e2e-harness-refactor/prd.md` 的 R4 明确要改 JSON 键拼写、AC9 要求落迁移表。
两者不能同时成立。本文的处理方式：**每条建议标注「行为不变 / 改变行为」**，行为不变的那部分可以
在「行为归零」的前提下直接做；改变行为的那部分（含 D1–D7 的修复）必须单独成一个 commit，并配一条
能复现的最小 plan。

---

## 0. 构建约束（AOT/裁剪/依赖）—— 对「能否用 System.Text.Json」的结论

### 0.1 事实

| 事实 | 证据 |
|---|---|
| E2E 工程**零** PackageReference，只有 `OutputType/AssemblyName/RootNamespace` 三项 | `WinForward.E2E.csproj:1-7` |
| 没有任何地方设 `PublishAot`，只有产品 CLI 设了 | `benchmarks/WinForward.E2E/` 无匹配；`src/WinForward.Cli/WinForward.Cli.csproj:6` 是唯一的 `PublishAot=true` |
| 发布脚本是 framework-dependent，不裁剪不 AOT | `scripts/publish.sh`：`dotnet publish -c Release -r {linux,win}-x64 --self-contained false` |
| 但**分析器在编译期全开**：`EnableTrimAnalyzer`、`EnableAotAnalyzer` 均为 true，且 `TreatWarningsAsErrors` | `Directory.Build.props:7,10-11`（E2E 工程通过 MSBuild 向上查找继承到根 props） |
| 没有 `InvariantGlobalization`（那段只在 CLI 工程里）；代码里所有格式化都显式带 `CultureInfo.InvariantCulture` | `src/WinForward.Cli/WinForward.Cli.csproj:24`；`JsonValue.cs:55,71`、`ClientRunner.cs:221,291,410-411` 等 |
| **JSON 根本不是手写的**：读用 `JsonDocument`/`JsonElement`，写用 `Utf8JsonWriter` | `JsonValue.cs:3,9,18-58`、`JsonlFile.cs:1,10,23,26-42`、`PlanFile.cs:3,74-77,217-329`；全仓 `rg 'WriteRawValue|WriteRaw'` 无结果 |
| 仓库已有源生成先例 | `src/WinForward.Configuration/ConfigurationModels.cs:116`：`public partial class ConfigurationJsonContext : JsonSerializerContext;` |

### 0.2 结论（明确回答提问）

1. **前提不成立**：`JsonValue.cs` / `JsonlFile.cs` 不是手写 JSON 的读写实现。`JsonValue.cs` 是一个
   在 `Utf8JsonWriter` 之上的 `object?` 动态分派器（87 行里 40 行是 `switch`）；`JsonlFile.cs` 是
   `Utf8JsonWriter` + `FileStream` 的 JSONL 封装。转义、编码、数字格式全部由 STJ 保证，
   **不存在转义/解析缺陷**（也没有任何一处手写转义：`rg 'Replace\(|\\\\\"|Escape'` 在 `Client/ Target/ Wire/` 无结果）。
2. **「换成 System.Text.Json」已经是现状**，不需要换、也没有依赖成本：`System.Text.Json` 在
   `Microsoft.NETCore.App` 共享框架里，E2E 工程的零 PackageReference 就是证据。
3. **真正的约束是反过来的**：不能用的是**反射式序列化**。我加了一个临时探针
   （`JsonSerializer.Serialize(new ProbeDto{...})`）并构建，结果：
   ```
   error IL2026: ... 'JsonSerializer.Serialize<TValue>(...)' ... RequiresUnreferencedCodeAttribute ...
   error IL3050: ... RequiresDynamicCodeAttribute ... Use System.Text.Json source generation for native AOT applications.
   Build FAILED. 0 Warning(s) 2 Error(s)
   ```
   `TreatWarningsAsErrors` 把它变成硬失败。**`JsonDocument`/`JsonElement`/`Utf8JsonWriter` 不受影响**
   —— 所以现在的写法是 AOT/裁剪安全的那一层，而 R3 若想把 `Dictionary<string, object?>` 换成强类型
   模型，**必须走源生成**（`[JsonSerializable]` + `JsonSerializerContext`，形如
   `ConfigurationModels.cs:116`），否则构建红。这是 R3 唯一的实现约束，值得写进 PRD。
4. 探针文件已删除，工作树干净（`git status` 只剩本任务的目录）。

### 0.3 因此 JSON 层真正该做的三件事

1. `JsonValue.Write` 的 `default:` 分支静默降级（§1.2）。
2. 一个 JSONL sink 被实现两遍（§1.6）。
3. R3 落地方式：源生成 context，而不是反射（§0.2 第 3 条）。

---

## 1. 手写 JSON 的实现质量（`JsonValue.cs` / `JsonlFile.cs`）

### 1.1 先说结论：这两份文件的实现质量是好的

- 写入路径零分配（`JsonlFile` 复用 `MemoryStream` + `Utf8JsonWriter`，每条记录 `SetLength(0)` + `Reset`，
  `JsonlFile.cs:31-36`），这是热路径上的正确做法，**不要动**。
- 非有限 `double` 写成 `null`（`JsonValue.cs:76-86`），与 README:270-284 的 `null` 约定一致。
- 空分母的比率返回 `null`（`JsonValue.cs:70-71`）而不是 0，并有解释性注释（65-69）——这正是
  10-06 审查第 13 条要求的修复，**已修**。
- `IReadOnlyDictionary` 分支在 `IEnumerable` 之前（`JsonValue.cs:40-44`），`string` 在 `IEnumerable`
  之前（37-39），顺序是对的，`Dictionary<string,object?>` 不会被当成数组。

### 1.2 `JsonValue.Write` 的 `default:` 分支静默把未知类型写成**字符串**（潜伏缺陷）

```csharp
54:            default:
55:                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
```

任何不是 `null/bool/int/long/double/string/IReadOnlyDictionary/IEnumerable` 的值会被写成**带引号的
字符串**。`float`、`decimal`、`uint`、`ulong`、`byte`、`short`、任何 enum、`DateOnly` 全落这里。
对一个「JSON 键就是契约、改名只会让格子变成 `n/a`」的项目（README:303-354），这是最坏的失败形态：
记录结构合法、数字变成字符串、下游静默读不到。

- **当前不可达**，所以这是潜伏而非现存 bug。证据：我把 `Client/Arms/*.cs` 与 `ClientRunner.cs` 里
  全部 130 处 `Parameters/Metrics/Gates[...] =` 的右值分类，没有任何一处产生上述类型（只有数字字面量、
  变量、`JsonValue.Round/Ratio/Microseconds/PerSecond`、布尔、字符串、集合）。
- **建议**：`default:` 改成 `throw new InvalidOperationException($"unsupported JSON value type {value.GetType()}")`，
  或至少 `Debug.Fail` + 写成 null。**行为不变**（今天没人踩），收益是把将来的类型错误从「静默的字符串」
  变成「响亮的崩溃」。配 2 个单测：`float` 必须抛；`int?`/`double?` 的装箱路径必须写数字（可空装箱成
  底层类型，`case double` 能命中，现在是对的，值得钉住）。

### 1.3 空分母约定在 `Ratio` 与 `PerSecond` 之间不一致

```csharp
70:    internal static double? Ratio(long numerator, long denominator) =>
71:        denominator == 0 ? null : Round((double)numerator / denominator, 6);
73:    internal static double PerSecond(long count, long ticks, long frequency) =>
74:        ticks <= 0 ? 0 : Round(count * (double)frequency / ticks);
```

`Ratio` 遵守 README:276「分母为 0 的比率写 `null`」，`PerSecond` 在同样情形写 `0`。同一个文件、相邻
三行、两种约定。`PerSecond` 的 `ticks <= 0` 在实践中不可达（调用方都传真实的 elapsed），所以修复是
免费的：改成 `double?` 返回 `null`。**改变行为**（仅对不可达输入），风险为零，但因为它改了签名且
`PerSecond` 有 8 个调用点（`LatencyArm.cs:241,274,304,349,356`、`LossArm.cs:97`、`DnsArm.cs:129`、
`ReliabilityArm.cs:359`、`PersistentArm.cs:208`），建议放到 Tier 3 单独做。

### 1.4 `JsonlFile`：取消可能留下一行残缺的 JSONL

`WriteAsync` 把「写完检查点 → 补 `\n` → 一次 `WriteAsync`」串起来（`JsonlFile.cs:31-36`），最后一步带
`cancellationToken`。臂结束时的三条记录显式传 `CancellationToken.None`（`ClientRunner.cs:312,357,390`），
这是对的；但**采样器传的是活 token**（`ResourceSampler.cs:401,451,544`）。取消若落在
`FileStream.WriteAsync` 内部（64 KiB 缓冲刷盘时），文件中间就会留下一行截断的 JSON，下一条记录紧跟其后。
`analyze.py` 会把它计成 `bad_lines`（`analyze.py:757-760`）。

- **建议**：把记录写入本身设为不可取消（要么传 `None`，要么先完整写入内存再 `WriteAsync(None)`），
  只在取锁处响应取消。**行为不变**（不改变任何字段/时序），是纯健壮性。

### 1.5 `JsonlFile.DisposeAsync` 会 dispose 信号量，晚到的写者拿到 `ObjectDisposedException`

`JsonlFile.cs:76` 在 `finally` 里 `_gate.Dispose()`。今天安全，靠的是
`ClientRunner.cs:289` 在 `await using` 释放 sink 之前先 `await sampler.ClearTargetAsync()`。
这个不变量没有任何注释，而它跨两个文件、两个类型，依赖者看不出来。

- **建议**：在 `DisposeAsync` 与 `ClearTargetAsync` 各加一句注释写明这个不变量；或让
  `DisposeAsync` 早退（`if (_disposed) return;` 之后对 `_gate` 用 `TryWait` 风格）以容忍晚到的写者。
  **行为不变**。`ResourceSampler.cs:106-111` 的屏障注释见 §4.6。

### 1.6 同一个 JSONL sink 被实现了两遍

`JsonlFile`（`JsonlFile.cs:13-42`）与 `LedgerWriter`（`Target/LedgerWriter.cs:18-68`）是**同一件东西**：

| 步骤 | `JsonlFile` | `LedgerWriter` |
|---|---|---|
| `Path.GetFullPath` + `Directory.CreateDirectory` | :15-20 | :21-26 |
| `FileStream(Create, Write, FileShare.Read, 64*1024)` | :22 | :28 |
| `SemaphoreSlim(1,1)` 串行化 | :8, 28, 38-41 | :12, 43, 65-67 |
| `MemoryStream` + `Utf8JsonWriter`，`WriteByte('\n')`，`GetBuffer().AsMemory(0,(int)Length)` | :9, 23, 31-36 | :46-57 |

差异只有错误策略：`JsonlFile` 把异常抛给调用方；`LedgerWriter` 吞掉并计数（:59-63），
且 stderr 报告按 `failures == 1 || failures % 100 == 0` 限流（:110）。另外 `LedgerWriter` **每条记录**
新建一对 `MemoryStream`+`Utf8JsonWriter`（:46-47），`JsonlFile` 复用（这是更快的做法）。

- **建议**：抽 `JsonLineWriter`，构造参数带一个 `onWriteFailure` 策略（抛 / 计数+限流上报）。
  **行为不变**（两边的记录形状、文件名、flush 时机都不变）；顺带把 ledger 的逐记录分配去掉。
  注意 `LedgerWriter` 的限流是刻意设计（注释 :61），不要合并成「都抛」。

### 1.7 只有臂结束才 flush，所以崩溃 = 丢掉整臂样本

`JsonlFile.WriteAsync` 不 flush（只有 `FlushAsync` 显式刷，`JsonlFile.cs:44-55`），唯一的定期 flush 是
每个臂结束时的 `ClientRunner.cs:288`。D1/D2/D4 三次崩溃实测下来，臂文件里的内容都只剩下已经落盘的
部分（D2 的 `LAT.jsonl` 有 5951 字节是因为臂正常结束并 flush 过；D1 的 `DNSBAD.jsonl` 是 0 字节）。
`LedgerWriter` 有 1 Hz 的 flush 循环（`LedgerWriter.cs:8,146-165`），`JsonlFile` 没有。

- **建议**：给 `JsonlFile` 也加一个 1 Hz 的 flush（或每条 `error`/`samplerError` 记录后 flush），
  与 ledger 对齐。**行为不变**（不改变任何字段与顺序，只改变数据何时落到文件系统）。

---

## 2. `PlanFile.cs` 的 schema 校验

### 2.1 校验强度全景：5 个键是硬错误（+1 条重名规则），其余 15 个数值键全部静默

实测（`e1`–`e10` 十个 plan，见 §2.6）：

| 键 | 缺失 | 非法值 | 校验位置 | 退出码 |
|---|---|---|---|---|
| `arms` | 硬错误 | 硬错误 | `PlanFile.cs:87-93,112-116` | 2 |
| `name` | 硬错误（不说是哪个臂） | 空串=硬错误 | :228-232 | 2 |
| `kind` | 硬错误 | 硬错误 | :234-238 + `s_knownKinds`:50-61 | 2 |
| 重名 | — | 硬错误 | :103-107 | 2 |
| `seconds` | 默认 60（`ArmSpec.cs:28`） | ≤0 或非数字=硬错误；**非整数不报** | :243-252 | 2 |
| `protocol` | 默认 `tcp` | 硬错误（消息含观测值，好） | :255,258-262 | 2 |
| **其余 15 个数值键** | 一律 0 | **一律静默** | `ReadArmNumbers`:267-284 + 各臂的 `> 0 ?` 兜底 | 0 |

15 个静默键及其兜底位置（`desktops` → `MixArm.cs:117`，其余如下）：`ratePerSecond`（`LossArm.cs:16`、`DnsArm.cs:51`、`LatencyArm.cs:876`）、
`payloadBytes`（`LossArm.cs:17`、`PersistentArm.cs:143`、`LatencyArm.cs:877`）、`window`
（`LossArm.cs:18`、`LatencyArm.cs:878`）、`lanes`（`LatencyArm.cs:879`）、`lossWindowMs`（`LossArm.cs:19`）、
`connectionsPerSecond`/`expectedBytes`（`ReliabilityArm.cs:133-134`）、`streams`/`targetBytesPerSecond`
（`ThroughputArm.cs:84-85`）、`intervalMs`/`idleSeconds`（`PersistentArm.cs:141-142`）、
`tcpPercent`/`cnameEvery`（`DnsArm.cs:52-53`，顺带**静默 clamp** 到 0..100 与 ≥0）、
`dnsPort`（`DnsArm.cs:56`）、BASE 两相（`BaseArm.cs:80-81,92-93`）。

**「0 表示没写」这个哨兵的直接后果**（D5，实测）：`"window": 100.5` 被 `TryReadInt`（:301-321）判为非整数
→ `ReadInt` 回落 0 → 臂认为「没声明」→ 用默认 64。发布出来的 `parameters.inFlightWindow = 64`，
退出码 0，没有 `error` 记录、没有 `note`。用户以为自己声明了 100。

### 2.2 D1：越界 `dnsPort` 让整个 run 崩掉，且不留 `run.json`

```
$ WinForward.E2E client --target 127.0.0.1 --plan bad-dns.json --out out-dns
e2e client: arm DNSBAD (dns) starting
exit=134
Unhandled exception. System.ArgumentOutOfRangeException: (Parameter 'port')
   at System.Net.IPEndPoint..ctor(IPAddress address, Int32 port)
   at WinForward.E2E.Client.Arms.DnsArm.RunAsync(ArmContext context) in .../DnsArm.cs:line 57
   at WinForward.E2E.Client.ClientRunner.RunArmAsync ... ClientRunner.cs:line 260
   ...
Aborted (core dumped)
```

`DnsArm.cs:56` 只做 `spec.DnsPort > 0 ? spec.DnsPort : context.Options.DnsPort`，没有上界。
`ClientRunner.cs:258-277` 的 catch 列表是 `OperationCanceledException/SocketException/
InvalidOperationException/IOException`，`ArgumentOutOfRangeException` 不在其中；
`Program.cs:105-109` 只兜 `OperationCanceledException`。输出目录里是一个 0 字节的 `DNSBAD.jsonl`，
**没有 `run.json`** —— 整个 run 的记录（包括此前已跑完的臂的摘要）全部丢失。

对比：target 的端口在 `TargetRunner.cs:142` 有 1..65535 校验，client 端的 `dnsPort` 没有。

### 2.3 D2：`--plan=`（显式空值）先静默换 plan，再崩在 `run.json` 上

这是**两个缺陷叠加**，两个都在我的文件集合里：

```csharp
// PlanFile.cs:125
if (string.IsNullOrEmpty(path))       // "" 与 null 同样处理 → 静默用内置默认 plan
// ClientRunner.cs:445
if (options.PlanPath is null)         // 只判 null → "" 走到 :451
    writer.WriteNull("planPath");
else
    writer.WriteString("planPath", Path.GetFullPath(options.PlanPath));   // :451 抛 ArgumentException
```

实测：

```
$ WinForward.E2E client --target 127.0.0.1 --out out-empty2 --plan=   # 3 秒后 SIGTERM
e2e client: arm LAT (latency) starting
e2e client: arm LAT finished in 2.9s
exit=134
Unhandled exception. System.ArgumentException: The value cannot be an empty string. (Parameter 'path')
   at System.IO.Path.GetFullPath(String path)
   at WinForward.E2E.Client.ClientRunner.WriteEnvironment(...) ClientRunner.cs:line 451
```

`out-empty2/run.json` 是一个 **207 字节、到 `planHash` 就断掉的非法 JSON**（`FileStream` 在异常回卷时
被 `await using` 释放并刷了半截缓冲）。`planHash = e49acbd4c9ad7284`，我按
`PlanFile.cs:36-47` 的原始字符串字面量算出的内置默认 plan 哈希完全一致 —— 证明它确实在跑默认 plan。

修复有两处，建议都做：`ClientRunner.TryApply` 把 `--plan=` 判成 usage error（`ClientRunner.cs:121`），
或 `PlanFile.cs:125` 与 `ClientRunner.cs:445` 统一用 `IsNullOrEmpty`。**改变行为**（CLI 表面没变：参数名、
含义、默认值都不变，只是显式空值从「静默默认 + 崩溃」变成「用法错误」）。

### 2.4 D3：`SanitizeFileName` 非单射 → 两个臂互相截断（静默数据丢失）

`PlanFile.cs:206-215` 把每个非字母数字非 `-_.` 的字符映射成 `_`，`ClientRunner.cs:238` 用它拼文件名。
重名检查（`PlanFile.cs:103`）用的是**原始** `spec.Name`，不是映射后的名字，所以
「`A/B`」与「`A_B`」都通过校验、都写 `A_B.jsonl`，第二个 `JsonlFile` 用 `FileMode.Create`（`JsonlFile.cs:22`）
把第一个臂的记录**截断**掉。实测：

```
arms: [ {name:"A/B", file:"A_B.jsonl", failed:false}, {name:"A_B", file:"A_B.jsonl", failed:false} ]
failed: False          exit=0
A_B.jsonl 里只剩： sample A_B / result A_B / armSummary A_B
```

下游影响可确证：`analyze.py:790-800` 按 `run.json` 的 `arms[].file` 逐臂 `load_arm(name, path / files[name])`，
`load_arm`（:740-752）取的是文件里**唯一那条** `result` 记录，并且**不校验 `record["arm"] == name`**。
于是两个臂会被填上同一份数据，报告里出现两行一模一样的数字。`selftest.sh` 的打印同理（按目录列举 + 按
`record['arm']` 打印），只会显示一个臂。

- **建议**：在 `TryLoad` 里用**映射后**的名字做唯一性检查（把 `:103` 的 `names.Add(spec.Name)` 换成
  `names.Add(SanitizeFileName(spec.Name))`，或另加一个集合），报错带上两个冲突的原始臂名。
  **改变行为**（把一个此前静默丢数据的 plan 变成 load error，退出码 2）。这是必须单独成 commit 的修复。
- 注意：不要改 `SanitizeFileName` 的**字符映射规则**，文件名出现在 `run.json` 与 campaign 目录树里，
  是既有数据的一部分（见 §11）。

### 2.5 D4：臂名过长直接崩

`SanitizeFileName` 不限制长度，`ClientRunner.cs:238-240` 用它建 sink，而 `new JsonlFile(...)` 在
`RunArmAsync` 的 `try`（:258）**之前**：

```
$ python3 -c "...270 字符臂名..."
Unhandled exception. System.IO.PathTooLongException: The path '/tmp/qareview/out-long/AAAA...jsonl' is too long
   at ...SafeFileHandle.Open(...)
exit=134
--- out dir --- (空)
```

- **建议**：`SanitizeFileName` 或 load 校验里加长度上限（Windows 上 260 是 `MAX_PATH`，靠
  `\\?\` 只是绕过；臂名 + `.jsonl` + 输出目录必须留余量），超限报 load error。
  **改变行为**（同上，是修复）。

### 2.6 报错质量：缺臂序号、缺观测值、缺 plan 路径

实测九个 plan 的消息原文：

| plan | 消息 | 诊断性 |
|---|---|---|
| `e1` kind 未知 | `arm 'X' has an unknown 'kind'` | 一般（含臂名，不含观测值） |
| `e2` 缺 name | `every arm needs a non-empty 'name'` | **差**：不说第几个臂 |
| `e3` 空数组 | `plan contains no arms` | 好 |
| `e4` 重名 | `duplicate arm name 'A'` | 好 |
| `e5` protocol 非法 | `arm 'X' has protocol 'sctp' (expected tcp, udp or tcp+udp)` | **好**（含观测值 + 期望集合，是全文件最好的模板） |
| `e6` 非法 JSON | `plan is not valid JSON: 'not json' is an invalid JSON literal. ... LineNumber: 0 \| BytePositionInLine: 1.` | 好（STJ 自带行列号） |
| `e7` 顶层是数组 | `plan must be an object with an 'arms' array` | 好 |
| `e9` `seconds:-5` | `arm 'X' has an invalid 'seconds'` | **差**：与 `e10` 同文 |
| `e10` `seconds:true` | `arm 'X' has an invalid 'seconds'` | **差**：类型错误与范围错误不可区分 |
| 目录当 plan | `cannot read plan '/tmp/qareview': Access to the path '/tmp/qareview' is denied.` | **误导**（不是权限问题，是目录） |

- **建议（全部行为不变）**：
  1. `TryReadArm` 增加 `index` 参数（`TryLoad` 的 `foreach` 有索引），所有臂级消息前缀
     `arm #3 'X':`；
  2. `seconds`/`protocol` 风格统一成 `e5`：带 `ValueKind` 或原始 JSON 文本；
  3. 缺 `name` 的消息带上序号；
  4. 整数键被写成小数时给一条**明确错误**（见 2.7），而不是静默回落。
  4 条都是纯字符串改动，可加 4 个单测钉住。

### 2.7 `TryReadInt` 的 unchecked 强转：超范围的值不报错且结果未定义

```csharp
301:    private static bool TryReadInt(JsonElement element, string name, out int value)
...
314:        if (!property.TryGetDouble(out var asDouble) || Math.Abs(asDouble - Math.Round(asDouble, MidpointRounding.ToEven)) > 1e-9)
315:        {
316:            return false;                       // 小数 → 静默 false → 回落默认值（D5）
317:        }
318:
319:        value = (int)asDouble;                  // 超 int 范围：未检查强转，结果未定义
```

`1e10` 这种值：`TryGetInt32` 失败 → `TryGetDouble` 成功 → `Math.Round` 差值 0 → `(int)1e10` 在 unchecked
上下文里结果未定义（x64 上通常给 `int.MinValue`）。如果它落在 `payloadBytes` 上，`LatencyArm.cs:877` 的
`> 0` 兜底会把它变成 120（静默），落在 `dnsPort` 上则走 D1（崩溃）。
`Math.Round(..., ToEven)` 与 `> 1e-9` 的容差是**魔数**（没有名字，也没有注释说明为什么是 1e-9）。

- **建议**：改成 `asDouble is >= int.MinValue and <= int.MaxValue` 才接受，否则返回 false 并**报错**
  （不是回落）。把 1e-9 命名成 `IntegerTolerance`。**改变行为**（原本静默的输入变成 load error）。

### 2.8 语义校验发生在「已经跑起来之后」

`modeMix` 只在 `ReliabilityArm` 里通过 `TryParseModeMix`（`PlanFile.cs:150-190`，全文件最好的校验函数之一：
带观测值、带期望、拒绝空权重）解析。一个 `"modeMix":"clean"` 的 plan 会**先跑完 LAT/LOSS 两个臂**
（默认 60+120 秒），才在 REL 启动时报错 —— 输出目录里已经有两条 `result` 记录，而 `run.json` 还没有。

- **建议**：加载期做一次「按 kind 的语义校验」（`modeMix` 语法、`tcpPercent` 范围、各键的下界/上界），
  在 `ArmSpec` 上加 `Validate()`，由 `TryLoad` 调用。**改变行为**：原本 exit 1 + 半份数据的 run，
  变成 exit 2 + 一条 load error。诊断性大幅改善，但改变退出码，必须单独成 commit 并同步 README
  （README:168-197 需要新增一句「哪些键是 load error」）。

### 2.9 `TryParseMode` 的 255 哨兵

```csharp
192:    private static bool TryParseMode(string text, out TcpMode mode)
194:        mode = text switch { "clean" => TcpMode.Clean, ..., _ => (TcpMode)255 };
203:        return (byte)mode != 255;
```

`(TcpMode)255` 是魔法哨兵：`TcpMode` 一旦新增成员或改成非 `byte` 基类型就静默失效。而且 `"stall"`
在 `TryParseMode` 里是合法模式（:200），但 `PlanFile` 的文档注释（:23）只列了四个
（`modeMix`、`connectionsPerSecond`、`expectedBytes`），README:185 也没提 `stall`。
建议改成 `Enum.TryParse<TcpMode>(text, out mode)`（`TcpMode` 成员名与文本名一致），或显式 `TryParse` 风格。
**行为不变**（保留 `stall` 可解析）。

### 2.10 `TryLoad` 的 4 个 out 参数与失败时的半成品

```csharp
63:    internal static bool TryLoad(string? path, out List<ArmSpec> arms, out byte[] planBytes, out string? error)
```

失败时 `arms` 里可能已经有若干成功解析的臂、`planBytes` 已读入文件字节，调用方（`ClientRunner.cs:187-191`）
两者都不用。4 个 out 参数在仓库规范下不常见（`TryCreate` 是 3 个）。建议返回
`PlanLoadResult(bool Ok, List<ArmSpec> Arms, byte[] Bytes, string? Error)` 记录类型，代价是
`ClientRunner.cs:187` 一个解构点。**行为不变**。这条优先级低。

### 2.11 默认值住在 4 个地方

1. `PlanFile.cs:35-48` 的内置默认 plan JSON；
2. `ArmSpec.cs:28`（`Seconds = 60`）；
3. 各臂的 `DefaultXxx` 常量（`LatencyArm.cs:150-151`、`LossArm.cs:16-19`、`DnsArm.cs:34`、
   `ReliabilityArm.cs:102-103`、`ThroughputArm.cs:78`、`PersistentArm.cs:134`…）；
4. `README.md:168-197` 的表格。

没有任何机制保证四处一致（README 说 `window` 默认 64，`LatencyArm.cs:878` 的 `DefaultWindow`
必须也是 64）。「缺失 → 0 → 臂兜底」这个链路让第 3 处成为事实上的 schema，
而 `parameters` 发布的正是兜底后的值（`LatencyArm.cs:903-914`）。

- **建议**：把「按 kind 的默认值表」提成一个地方（例如 `ArmDefaults`，每个 kind 一个 record），
  臂从它取、README 表格按它校对。这是 R3 的一部分，**行为不变**（只要数字不动）。

---

## 3. `ClientRunner.cs` 的职责

### 3.1 结论：不是「上帝类」，但是「6 个职责 + 439 有效行」，违反 AC1

它不是那种「一千行里什么都有」的上帝类：真正的臂逻辑在 `Client/Arms/` 里，这个文件 498 行里
有 100 行是 CLI、44 行是记录写入、78 行是 run.json。但它的确承担了 6 件不相关的事：

| # | 职责 | 行范围 | 有效行（约） |
|---|---|---|---|
| 1 | CLI 表面 + 词法（`s_knownOptions`） | 37-49, 472-494 | 40 |
| 2 | CLI 解析与校验 | 52-183 | 95 |
| 3 | 臂循环与生命周期（sink/sampler/LatencySet 的挂接） | 185-296 | 85 |
| 4 | 臂级记录写入（`error`/`result`/`armSummary`） | 298-391 | 70 |
| 5 | run.json 与环境块 | 393-470 | 60 |
| 6 | `ClientOptions` 模型 | 12-33 | 20 |

### 3.2 拆分接缝（直接满足 AC1 / R1）

四刀，纯物理搬移 + 可见性调整，零逻辑改动：

1. `ClientOptions`（:12-33）→ `Cli/ClientOptions.cs`。
2. `s_knownOptions` + `TryCreate` + `TryApply` + `TryAssignPort/TryAssignCount/TryPort`（:37-183）
   → `Cli/ClientOptionsParser.cs`。**顺带修掉 `Cli/` 目录里只有 `ExitCodes` 一件东西的组织问题**
   （`Cli/ExitCodes.cs:1-11`），以及 §7.1 的解析器重复。
3. `WriteFailureAsync/WriteResultAsync/WriteArmSummaryAsync`（:298-391）→ `Client/ArmRecordWriter.cs`。
4. `WriteRunFileAsync/WriteEnvironment` + `ArmSummary`（:393-470, 496）→ `Client/RunFileWriter.cs`。

剩下 `ClientRunner`（臂循环 + 生命周期）约 85 有效行，职责单一。

### 3.3 `RunArmAsync` 的 4 类型白名单是 D1/D2/D4/D7 四次崩溃的共同根因

```csharp
258:        try
260:            outcome = await ArmDispatch.RunAsync(context).ConfigureAwait(false);
262:        catch (OperationCanceledException) { failure = "cancelled"; }
266:        catch (SocketException exception) { failure = exception.Message; }
270:        catch (InvalidOperationException exception) { failure = exception.Message; }
274:        catch (IOException exception) { failure = exception.Message; }
```

这是一个**白名单**：任何不在列表里的异常逃出 `RunAsync`（:185-229）→ 逃出 `Program.RunClientAsync`
（:101-109，只兜 `OperationCanceledException`）→ 未处理异常 → SIGABRT + 没有 `run.json`。
实测逃逸的有 `ArgumentOutOfRangeException`（D1）、`ArgumentException`（D2）、`PathTooLongException`
（D4，注意 `PathTooLongException` 派生自 `IOException`，但它抛在 `try` 之前的 `:240`，所以照样逃逸）。
按同样逻辑还能逃逸的：`OverflowException`（`new byte[负数]`）、`JsonException`、`OutOfMemoryException`、
`KeyNotFoundException`。

同一个 `try` 的第二个问题是**范围**：`WriteFailureAsync/WriteResultAsync/WriteArmSummaryAsync`（:283-287）
在 `try` 之外，sink 的创建（:240）也在外面。也就是说，记录写入本身出问题时同样没有记录。

- **建议（两处，均改变行为但是纯健壮性提升）**：
  1. `catch (Exception exception) when (exception is not OperationCanceledException)` 作为最后一个
     catch，把 `failure` 设为 `$"{exception.GetType().Name}: {exception.Message}"`，让臂失败变成一条
     **`error` 记录 + `run.json` 里的 `failed:true`**，而不是进程崩掉。这正好符合模块已有的设计意图
     （`error` 记录类型已经存在，README:236 也描述了它）。
  2. `Program.RunClientAsync`/`RunTargetAsync`（:92-110, 68-90）加顶层 `catch (Exception)`，
     打印一行并返回 `ExitCodes.RuntimeError`。target 侧同理（`LedgerWriter` 构造函数抛 `IOException`
     时现在也是崩，且不写任何 summary）。
  配上 D1–D4、D7 的最小 plan 作为回归用例，这 7 条会一起变成可测的。

### 3.4 重复的写记录样板

`parameters` 与 `gates` 的前导块在 `WriteResultAsync`（:332-344）与 `WriteArmSummaryAsync`（:377-384）
出现 4 次；`type/arm/kind/label` 3 次。建议一个
`WriteMap(writer, "parameters", map)` + 一个 `WriteArmHeader(writer, type, arm, options)`，
两个方法各减 8~10 行。**行为不变**（键的顺序必须保持：`type, arm, kind, label, parameters, …, gates, …`，
`analyze.py` 不依赖顺序但 JSONL 的 diff 会变，R7 的基线比对会看到「顺序变化」——所以这条建议
要放在 R7 基线冻结**之后**）。

### 3.5 顺带发现的小问题

- `IPAddress.TryParse` 校验出现两次（`TryCreate` :96-100 与 `RunAsync` :193-197），消息略有不同
  （一处有句号一处没有），第二处永远不可能失败（`options` 在两次之间不可变）→ 死分支，删一处。
- `--sampler-process=` 空值被接受（`TryApply` :129-131 直接 `Add(value)`），产生 D6 的垃圾序列。
  建议解析期拒绝空值/含路径分隔符/以 `.exe` 结尾的值（帮助文本已经在提醒 `no .exe suffix`，
  `PrintHelp` :484）。**改变行为**（usage error）。
- `--label --out x` 会把 `--out` 当成 label 的值（:71-80 不检查下一个参数是不是选项），随后报
  `unknown argument 'x'`。诊断性小问题，建议「值以 `--` 开头就报 missing value」。**改变行为**。
- `RunAsync` :208-217 的 `failed |= summary.Failed` 与 `if (cancellationToken.IsCancellationRequested) break;`
  配合正确，但一个被取消的臂会被标成 `failed:true`，同时进程再打一行「interrupted」（:222-226）——
  双重信号。无害，可在 R3 时顺手统一。

---

## 4. `ResourceSampler.cs`

### 4.1 结构：一个类型 5 件事，474 有效行

| 职责 | 行范围 |
|---|---|
| 采样循环（`PeriodicTimer` 1 Hz、生命周期、取消） | 89-138, 329-355 |
| 目标挂接与屏障（`SetTarget`/`ClearTargetAsync`） | 104-111 |
| 操作系统计数器读取（`Process`、`Refresh`、异常阶梯） | 140-257 |
| 每进程聚合（`ReadProcessTotals`、`ProcessTotals`） | 70-85, 454-495 |
| JSON 形状与错误上报 | 259-327, 381-452, 497-545 |

拆分接缝（纯搬移）：`ProcessCounterSource`（140-257 + 12-85 的数据类型）、`ResourceSampleWriter`
（275-327 + 385-452 + 497-545）、`ResourceSampler`（循环 + 屏障，约 60 行）。

### 4.2 三段一模一样的 catch 阶梯（95 行）

`TryRefresh`（:140-167）、`TryReadLong`（:169-201）、`TryReadStartTime`（:203-235）是同一个五元
catch 列表抄了三遍，只有返回类型和赋值目标不同：

```csharp
catch (InvalidOperationException) { ... }
catch (PlatformNotSupportedException) { ... }
catch (Win32Exception) { ... }
catch (NotSupportedException) { ... }
catch (IOException) { ... }
```

- **建议**：一个泛型 `TryRead<T>(Func<Process, T> read, out T value)` + `TryRefresh` 复用同一阶梯。
  采样是 1 Hz 的冷路径，`Func` 委托的开销无关紧要（而且 `static` lambda 会被缓存）。
  **行为不变**（异常集合与返回值完全一致）。三条方法 95 行 → 约 40 行。

### 4.3 `DisposeAsync` 里有一个死的异常过滤器

```csharp
126:        catch (OperationCanceledException) { /* ... */ }
130:        catch (Exception exception) when (exception is not OperationCanceledException)
```

`OperationCanceledException` 已被上一个 catch 全部接走，所以 :130 的 `when` 恒为真 —— 这是从
`LoopAsync`（:345-349，那里的过滤器**是必需的**，因为前面没有 catch）复制过来的产物。
同类问题见 `:407`（`catch (Exception sinkFailure) when (sinkFailure is not OperationCanceledException)`
前面是 `catch (OperationCanceledException)`，:403）—— 也是死的。
建议删掉两处过滤器。**行为不变**。

### 4.4 「零」与「没读到」在聚合层不可区分

```csharp
241:    private static bool TryReadCounters(Process process, bool hasHandles, out ProcessCounters counters)
243:        var read = TryRefresh(process);
244:        read &= TryReadLong(process, static i => (long)i.TotalProcessorTime.TotalMilliseconds, out var cpuMilliseconds);
245:        read &= TryReadLong(process, static i => i.PrivateMemorySize64, out var privateBytes);
...
255:        counters = new ProcessCounters(cpuMilliseconds / 1000.0, privateBytes, workingSet, peakWorkingSet, threads, handles);
256:        return read;
```

`read` 是**非短路** `&=`（刻意的：能读多少读多少，值得一句注释，因为分析器会把 bool 上的 `&` 当坏味道）。
但 `ProcessCounters` 是一包裸 `long`：读失败时它照样装着 0，`WriteCounters`（:275-282）把它们
**无条件**写进样本的聚合字段。于是：

- 每个进程条目有 `countersRead:false` + `cpuSeconds:null`/`privateBytes:null`（:306-316，对的）；
- 但样本级聚合 `cpuSeconds`/`privateBytes`/`workingSetBytes`/`threads` **是 0**，只有
  `readErrors`/`readError`（:322-326）能间接提示。

README:270-284 的 `null` 约定说「读不到计数器的进程，`cpuSeconds` 和 `privateBytes` 是 `null`，
并且整个样本带 `readError:true`」—— 逐进程维度执行了，聚合维度没有。一个 0 在这里会被误读成
「这个产品几乎不占 CPU」。这是「0 不是测量」这一类问题的最后一个藏身处。

- **建议**：`ProcessCounters` 改成逐字段可空（或加一个 `Complete` 标志），聚合字段在
  `countersRead == false` 时写 `null`，键名不变。**改变行为 + 改变契约**（`analyze.py` 读
  `samples[].cpuSeconds`，见 README:330），必须与 R5 一起做，并确认 analysis 对 `null` 的处理
  （它已经有 null 语义，README:270-284 说明它区分「缺键」与「null」；samples 的读集在 README:330）。

### 4.5 `absent` 与 `readError` 的 schema 不对称

`SampleNamedAsync` 在没匹配到进程时**不写计数块**（:517-520），只写 `absent:true`；而读失败时写
一堆 0 + `readError`。两种「没有测量」在记录里形状不同，下游要写两套判断。

- **建议**：永远写同一组键，用 `null` + `absent`/`readError` 区分原因。**改变契约**（README:330 的
  samples 读集不含 `absent`，新增/改动键要同步）。低优先级，但比 4.4 更容易被误用。

### 4.6 `ClearTargetAsync` 的屏障：正确，但没人看得懂

```csharp
106:    internal async Task ClearTargetAsync()
107:    {
108:        _target = null;
109:        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
110:        _gate.Release();
111:    }
```

「拿锁再立刻放」是等待在途 tick 结束的屏障写法：`LoopAsync`（:334-353）**整个** `SampleAllAsync`
都在锁里，所以这一对等待保证「没有 tick 正在写 sink」。这正是 `ClientRunner.cs:289` 敢在下一行
释放 sink 的原因。**它是对的**，而且不能简化成「直接 `_target = null`」（那会引入 use-after-dispose）。

- **建议**：加注释写明不变量（「等一个在途 tick 结束；调用方随后即可释放 sink，见
  `ClientRunner.cs:289-295`」）。**行为不变**。这是 §11 名单里的一条。

### 4.7 `samplerError` 没有任何限流

`SampleAllAsync`（:357-379）对每个名字每个 tick 都可能在 sink 里写一条 `samplerError`。一个持续
失败的进程名意味着 1 条/秒、整个臂长，全部堆进那个臂的 JSONL。对照 `LedgerWriter.RecordFailure`
（`Target/LedgerWriter.cs:107-116`）的 1/100 限流，这是同一问题的两种态度。

- **建议**：照抄 1 + 每 100 条的上报策略，把重复的 `samplerError` 收敛成计数 + 首次样本。
  **改变行为**（记录条数变少）。注意 README:536+ 的验证表把「没有 `samplerError` 记录」当健康检查，
  所以减少重复不会削弱这个门禁；但 `analyze.py` 会 `len(arm.sampler_errors)`（:751），
  若它把这个数字当判据就需要同步。

### 4.8 可测试性：零接缝，以及最小的开法

`ResourceSampler` 直接使用静态 API：`Process.GetProcessesByName`（:460）、`Process.GetCurrentProcess`
（:416）、`Environment.WorkingSet`（:422）、`Stopwatch.GetTimestamp`（:394,417,499）、
`PeriodicTimer`（:331）、`Console.Error`（:263）。没有接口、没有工厂、`sealed`，无法在测试里替换任何一环。

最小接缝（按收益排序）：

1. `IProcessProbe { IReadOnlyList<ProcessSample> Snapshot(string name); }` + 一个 `SelfProbe`。
   把 :454-495 与 :140-257 的 OS 读取挪到实现类里，`ResourceSampler` 只依赖接口。这样
   「1 Hz 循环、`absent` 判定、`samplerError` 生成、JSON 形状」全部可单测。
2. sink 抽象：`ResourceSampler` 现在持具体 `JsonlFile`（`SamplerTarget`:9、`:426`、`:507`）。
   抽一个 `IJsonlSink { ValueTask WriteAsync(Action<Utf8JsonWriter>, CancellationToken); }`
   （`JsonlFile` 天然满足），测试用内存 sink 断言 JSON。
3. 时钟：`TimeProvider`（.NET 8+）注入，替掉 `Stopwatch.GetTimestamp` 与 `PeriodicTimer`。
4. 诊断输出：`ReportToStderr`（:259-273）改成可注入的 `Action<string>`（顺带让它可断言）。

R6 没有点名采样器，但这四刀里第 1、2 条是**唯一**能让「采样正确性」这类问题（10-06 审查 §14
「资源采样没有进程身份」）有回归测试的办法，性价比高。

---

## 5. `UdpReliability.cs`

### 5.1 先说好的部分（这份是全集合里结构最好的）

- `SequenceBitmap.TrySet` 的边界检查 + 那条解释「为什么会溢出到死循环」的注释（:11-14）写得很好，
  是 10-06 审查第 3 条的修复。
- `MarkArrival` 的乱序判定按 RFC 4737（:281-290），注释解释了「比已到达的更高序号晚到才算乱序，
  比已发送的更高序号晚到不算」——这是本项目里少见的「注释解释为什么」的正面例子。
- `Classify` 的恒等式注释（:322-329）与 README:355-407 的三条会计恒等式完全对应，且
  「`corrupt` 故意不在和里」这种反直觉处有解释。

### 5.2 D7：序号越过 2¹⁸−1 时 `MarkSent` 直接越界写数组，进程崩（实测）

```csharp
196:    internal void MarkSent(long sequence, long sendTicks)
197:    {
198:        Ensure(sequence);                    // 越界时直接 return，不扩容
199:        _sent.TrySet(sequence);              // 越界时拒绝并计 OutOfRange
200:        _sendTicks[sequence] = sendTicks;    // 未受保护
201:        _arrivalMilliseconds[sequence] = -1; // 未受保护
...
207:        SentOk++;                            // 未受保护
208:        Outstanding++;
```

`Ensure`（:401-422）对 `sequence < 0 || sequence > MaxSequence` 早退，**因此数组的最大长度恰好是
262144**（从 4096 起翻倍，只为 262143 这个最大合法序号扩到 262144）。而 `_sendTicks`/
`_arrivalMilliseconds` 的下标写发生在 `Ensure` 之后、且不看 `TrySet` 的返回值：

- `sequence ≥ 262144`（`= 2^18 = MaxSequence + 1`）→ `_sendTicks[262144]` 越界 → `IndexOutOfRangeException`。
- `sequence < 0` → `_sendTicks[-1]` → 同样越界。
- 所以「越界」在这里**不是静默降级，而是立即崩溃**；`_sent.TrySet` 的边界检查（10-06 审查第 3 条的
  修复）只护住了位图，没护住并行的两个数组。

**实测**（`--target 127.0.0.1` 跑本地 target，plan：`{"kind":"loss","seconds":20,"ratePerSecond":20000}`）：

```
$ WinForward.E2E client --target 127.0.0.1 --udp-port 41110 --plan overrun.json --out out-over
e2e client: arm LOSS (loss) starting
EXIT=134
Unhandled exception. System.IndexOutOfRangeException: Index was outside the bounds of the array.
   at WinForward.E2E.Client.Arms.LossArm.RunUdpPhaseAsync(...)     <- MarkSent 被内联进这里
   at WinForward.E2E.Client.Arms.LossArm.RunAsync(...)  LossArm.cs:line 38
   at WinForward.E2E.Client.ClientRunner.RunArmAsync(...)  ClientRunner.cs:line 260
   at ... Program.Main
--- out dir ---
LOSS.jsonl   5759 B    ← 只有 13 条 sample 记录，没有 result
（没有 run.json）
```

**可达性**：`LossArm.cs:134-166` 每接受一个**提供过的调度槽**就 `index++` 并用它当序号
（`tracker.MarkSent(index, intended)`，:166 —— 注意窗口溢出时 `continue` 跳过了 `MarkSent`，
但 `index` 已经加过，所以序号就是「提供过的槽数」）。于是触发条件是
**`ratePerSecond × seconds > 262143`**：

| 臂 | 速率 | 崩溃所需时长 |
|---|---|---|
| `LOSS`（`LossArm.cs:16`） | 500/s（README:179 与 README:296 的默认） | **525 秒** |
| `LOSS` | 20000/s | 14 秒（已实测，20 秒的臂跑到约 13 秒时崩） |
| `BASE` 的 loss 相（`BaseArm.cs:92`） | 同上 | 同上（每相 `seconds` 独立） |
| `MIX` 的 UDP 类（`MixArm.cs:111`） | 30/s/desktop | 2.4 小时，实际不可达 |

而 `UdpReliability.cs:127-129` 的注释正好在解释为什么「25 万的上限远高于任何合法序号」：

```
// The heaviest planned arm offers 500 datagrams a second for two minutes, so a ceiling of a
// quarter million sequences is far above any legitimate index and still bounds what a corrupt
// one can allocate.
```

这句话把「当前提交的计划文件」当成了不变量。**没有任何代码强制它**：`seconds` 是任意正数，
`ratePerSecond` 是任意正整数，两者都不与 `MaxSequence` 关联。所以这不只是注释不准确，
而是「一个 plan 就能让客户端崩掉，并且丢掉整臂数据」。

- **建议（三条，按顺序做）**：
  1. **止血**：把边界检查提到 `MarkSent` 开头
     （`if (sequence is < 0 or > MaxSequence) { OutOfRange++; return; }`），
     让越界序号在 `SentOk` 之外、不再写数组。**改变行为**（长臂从「崩」变成「跑完但少算」，
     且 `outOfRangeSequences` 非零 —— 这是 README:531-535 已经声明的披露信号）。
  2. **加载期校验**：plan 加载时对 `loss`/`mix`/`base` 检查
     `ratePerSecond × seconds ≤ MaxSequence`（`mix` 用 `UdpPacketsPerSecond × seconds`），
     超限报 load error。**改变行为**（同一 plan 从崩溃变成退出码 2 + 一句可诊断的错误）。
     建议 1 与 2 同时做：1 保证任何输入都不崩，2 保证用户知道自己的 plan 越界了。
  3. **注释改成陈述不变量**：把 :127-129 改写成「本跟踪器只能跟踪 `MaxSequence` 之内的序号；
     `ratePerSecond × seconds` 超过它时 `outOfRangeSequences` 非零、会计恒等式不再成立」，
     并指向校验它的那个地方。**行为不变**。

  另外注意：同一条路径上还有 `SentOk`（:207）—— 修 1 之后，越界的槽既不在 `sent` 里也不在任何桶里，
  `supplied`（`MarkSupplied`，:189，按提供过的槽计数）与 `sent` 的差会变大，`gates.clientSendLoss`
  的语义要跟着看一遍（属于 10-06 的片区）。

### 5.3 魔数：65535、-1、2^18

```csharp
201:        _arrivalMilliseconds[sequence] = -1;                         // -1 = 未到达哨兵，无名字
294:        _arrivalMilliseconds[sequence] = Math.Clamp(milliseconds, 0, 65535);   // 65535 = ?
339:        var windowMilliseconds = windowTicks * 1000.0 / Stopwatch.Frequency;
```

`65535` 没有名字也没有注释。它是一个隐藏上界：`lossWindowMs` 是 `int`，plan 可以声明任意大
（`LossArm.cs:19` 只做 `> 0` 兜底）。如果 W > 65535 ms，一条 70 秒后才到的数据报会被 clamp 到
65535，而 `windowMilliseconds` 是 70000 → 判成 `arrived` 而不是 `late`。当前计划里 W ≤ 2000，
所以不可达，但这是「静默截断」而不是「明确拒绝」。
`Math.Clamp(milliseconds, 0, ...)` 的下界 0 也有同样性质：若调用方传入过期的 `nowTicks`
（`elapsedTicks < 0`），会被静默记成 0 ms 的到达，而不是暴露调用方 bug。

- **建议**：`private const int MaxArrivalMilliseconds = 65535;`（或改成 `int.MaxValue` 并让
  `lossWindowMs` 有上界校验），加 `Debug.Assert(elapsedTicks >= 0)` 与
  `UdpLossMath` 的 `windowMilliseconds >= 0` 校验。**行为不变**（可达输入上完全等价）。

### 5.4 `TryClear` 与 `OutOfRange` 的定义

- `TryClear`（:48-63）没有 `MaxSequence` 检查、也不计 `OutOfRange`，与 `TrySet` 不对称。
  这是对的（它只能被 `MarkSendRefused` 用于 `TrySet` 已接受过的序号，:221-234），
  但值得一句注释说明「为什么不检查」。
- `OutOfRange`（:177）把三个位图的计数**相加**，而它的文档（:171-176）说的是「被位图拒绝的序号」。
  单位其实是「拒绝次数」而不是「序号个数」（同一个序号在理论上可能被两个位图各拒一次）。
  它发布的 JSON 键是 `metrics.outOfRangeSequences`（README:531-535 提到），所以**只能改内部属性名**，
  不能改键名。建议改名 `BitmapRefusals` 并让注释与单位一致。**行为不变**。

### 5.5 `UdpLossMath` 不校验负 W

```csharp
434:    internal static long WindowTicks(int windowMilliseconds) =>
435:        (long)(windowMilliseconds / 1000.0 * Stopwatch.Frequency);
```

负 W → 负 `windowTicks` → `Retire` 立即把所有在途清空 → 每个未到达的都被判 `never`，每个到达的
都被判 `late`。`DefaultWindowMilliseconds = 200`（:432）有很好的注释解释「W 是声明参数、
绝不从直方图推导」（这是 10-06 审查第 6 条的修复）。建议 ctor/解析期拒绝 `lossWindowMs < 0`（0
已经被 `> 0` 兜底吃掉了）。**改变行为**（非法输入 → load error）。

### 5.6 可测试性：这份**今天就能测**，唯一障碍是没有测试工程

`SequenceBitmap`、`UdpReliabilityTracker`、`UdpLossMath` 没有 socket、没有锁（单线程契约）、
没有时钟读取（`nowTicks` 由调用方传入；`Stopwatch.Frequency` 只是常量因子）。所以 R6 里
「UDP 序列记账与分类」是**零接缝成本**的一项。建议的测试清单（每条都能写成 10 行以内的确定性用例）：

1. 恒等式：随机生成一条「发送/到达/损坏/拒发/溢出」轨迹，断言
   `arrived + late + never + undetermined + corruptDatagrams == sent`（README:355 的硬门禁）。
2. 边界：`MaxSequence`、`MaxSequence+1`、`0`、`-1` 四个点的 `TrySet`/`IsSet`/**`MarkSent`** 行为 ——
   `MarkSent(MaxSequence+1, …)` 是今天的崩溃点（D7），修好之后它必须变成「计一次 `OutOfRange` 并且
   不写数组」，这条断言就是 D7 的回归测试。
3. 恰好 W 的到达：`nowTicks == sendTicks + W` 判 `arrived`，`+1 tick` 判 `late`
   （现有代码是 `> windowMilliseconds` 用 double 比较，:354 —— 这个边界值得钉住）。
4. 重复：同一序号两次到达 → `Duplicate` 计数、`ReceivedDatagrams` 只加一次（:267-271）。
5. 重放损坏帧：`MarkCorruptWithKnownSequence` 二次调用 → `Duplicate++` 而
   `CorruptDatagrams` 不重复（:247-251）。
6. `MarkSendRefused` 之后 `SentOk-1` 且该序号不再出现在任何桶里（:221-234）。
7. 乱序：高序号先到、低序号后到 → `Reordered++`（:283-290）。
8. 在途窗口：`Retire` 只释放 W 已过期的、且不重复递减（:302-320, :391-399 —— 这两处的
   `Outstanding` 记账是 10-06 审查第 4 条的修复，测试正好把它钉住）。

---

## 6. `LogHistogram.cs`

### 6.1 正确性复核：数学是对的

- `bucket = 63 - LeadingZeroCount(value)`，`value` 已 clamp 到 `[1, 2^34-1]`（:83-84），
  所以 `bucket ∈ [0,33]`；`_counts` 长度 `34 × 2048 = 69632`，最大下标 `33*2048+2047 = 69631`
  = 长度 - 1，**不越界**。
- `SubBucketIndex`（:119-121）两段式（bucket < 11 时线性、之后右移）与
  `HighestEquivalentValue`（:123-134）的逆运算自洽。
- `Percentile`（:136-155）在 `cumulative >= target` 时取**该桶的上界**，与 :54 的注释
  「报告所在桶的排他上界，永不低估」一致；`target = ceil(p/100 × count)` 也是标准做法。
- 饱和（>`2^34-1` 被 clamp，没有溢出计数）是 10-06 审查记录过的语义问题，不在本文重复；
  但值得在 :83 加一句注释，否则下一个人会把 clamp 当 bug「修掉」。

### 6.2 五个必须互相吻合的魔数

```csharp
57:    private const int SubBucketBits = 11;
58:    private const int SubBucketCount = 1 << SubBucketBits;
59:    private const int BucketCount = 34;
60:    private const long MaxTrackedValue = (1L << 34) - 1;
61:    private const long MinTrackedValue = 1;
```

`34` 出现两次（`BucketCount` 与 `MaxTrackedValue`），必须相等，否则 `Record` 会越界写
（`bucket = 33` 时下标落在 `_counts` 之外）。没有静态断言、没有测试。README:492-497 又把
「34 个桶 × 2048 子桶、`[1 ns, 2^34−1 ns]`」抄了一遍。

- **建议**：`MaxTrackedValue = (1L << BucketCount) - 1`（消除重复），加一个
  `static LogHistogram() { Debug.Assert(BucketCount * SubBucketCount == _counts 长度语义); }`
  或一个测试：`Record(long.MaxValue)` 后 `Snapshot()` 不抛、`Max` == `MaxTrackedValue`。
  **行为不变**。

### 6.3 不能动的第一名：桶布局就是输出契约

`p50Us/p90Us/p99Us/p999Us` 由 `analyze.py` **逐字读取、从不重算**
（`analyze.py:246` 的 `PERCENTILES`、:5631、:3704 的报告文案都这么写）。改 `SubBucketBits`/
`BucketCount`/clamp 上界，会让**每一个已发布与将来的百分位**静默变化，而没有任何编译期或运行期信号。
这是 §11 名单里的第一条。

### 6.4 每个直方图 557 KB，每个臂 2.2 MB

`new long[34 × 2048]` = 69,632 × 8 B = **557,056 B**（`LogHistogram.cs:64`），
`LatencySet` 持有 4 个（`ArmContext.cs:95-101`），每个臂新建一个 `LatencySet`
（`ClientRunner.cs:243`）→ 每臂约 **2.2 MB**（4 个都在 LOH 上）。臂之间会释放，所以不是泄漏；
但 R2 若「每个 lane 一个直方图」就会乘上 lane 数。建议在 `LogHistogram` 顶部写一句「一个实例
约 0.55 MB；按臂分配，不要按 lane 分配」，并在拆分时保持「每臂一个 `LatencySet`」。
**行为不变**。

### 6.5 `meanUs` 少了一次精度

```csharp
45:        writer.WriteNumber("meanUs", JsonValue.Microseconds((long)Mean));
```

`Mean` 是 `double`（纳秒），这里先截断成 `long` 再交给 `Microseconds`（`JsonValue.cs:63`，会
`Round(..., 3)`）。`JsonSnapshot` 的其它字段都是 `long`，所以只有 `Mean` 走了这条多余的截断。
影响 < 0.001 µs，但它**改变已发布的数字**（最后一两位），所以是契约改动。

- **建议**：加一个 `JsonValue.Microseconds(double)` 重载，在 R5 的契约变更批次里一起做。
  **改变行为**（1 ULP 级）。不要放在「行为归零」的批次里。

### 6.6 只写非空直方图：不要「顺手修」

`ArmContext.cs:113-119` 只在 `histogram.Count > 0` 时写 `latency/<class>`，所以 `latency` 的键集合
**逐臂不同**。README:261 明确写了这一点，`analyze.py` 也按「有这个键才算」处理。
「让四条直方图总是齐全」看起来更整齐，但会新增键、改变契约。**不要动。**

### 6.7 其它小项

- `LatencySet.WriteTo` 先读 `Count`（`LogHistogram.cs:70-79` 加锁）再 `Snapshot()`（:103 再加锁），
  两次加锁之间计数可变 → 理论上撕裂。臂已结束，实际无害；加一句注释或让 `Snapshot` 返回
  `Count == 0` 的判据，可以消掉这次双锁。**行为不变**。
- :54 的说明是 `//` 而不是 XML 文档注释（类注释），而这条说明恰恰是最需要被 `///` 出来的
  「百分位语义」；建议改成 `///`（`ArmSpec.cs:56-59`、`UdpReliability.cs:43-47` 都有好例子）。
  **行为不变**。

---

## 7. 重复代码（跨文件清单）

按「能省多少行 / 风险」排序。**用户猜测的「读环境变量的重复」不存在**：全 harness 没有读任何
环境变量，只有 `Environment.ProcessorCount`（`ClientRunner.cs:443`、`TargetRunner.cs:18`）与
`Environment.WorkingSet`（`ResourceSampler.cs:422`）。

| # | 重复单元 | 位置 A | 位置 B | 建议 |
|---|---|---|---|---|
| 1 | **CLI 解析器整体**（while 循环、`=` 切分、已知选项表检查、`missing value`、`unknown argument`，连 `#pragma warning disable RCS1239` 与注释都逐字相同） | `ClientRunner.cs:52-109` | `TargetRunner.cs:105-158` | 抽 `Cli/OptionParser`，接受「已知选项集合 + 一个 `TryApply(name,value,out error)` 回调」。省约 55 行，顺带消掉两份逐字相同的 pragma |
| 2 | **`TryPort` 同名不同契约** | `ClientRunner.cs:172-183`（1..65535 + 含范围的错误消息） | `TargetRunner.cs:223-233`（只解析；范围检查在 :142，消息「ports must be in the range 1..65535」不说是哪个端口、不说值） | 合并成一个，统一成 client 那份的消息质量（含值 + 范围）。省 11 行，且让 target 的报错达到 client 的水平 |
| 3 | **JSONL sink 整体** | `JsonlFile.cs:13-42` | `Target/LedgerWriter.cs:18-68` | 见 §1.6 |
| 4 | **`Process` 读取的 5 元 catch 阶梯 ×3** | `ResourceSampler.cs:140-167, 169-201, 203-235` | 同文件内 | 见 §4.2，省约 55 行 |
| 5 | **`ticks * 1000.0 / Stopwatch.Frequency`** | `ArmContext.cs:46, 67` | `UdpReliability.cs:293, 339, 435`（另有两处用全限定名 `System.Diagnostics.Stopwatch.Frequency`：`LossArm.cs:97`、`ReliabilityArm.cs:359`） | 加 `Clock.ToMilliseconds(long ticks)`（`Clock` 缺这一个，才导致各处自己写）。**行为不变**（同一个表达式） |
| 6 | **`ToString("O", CultureInfo.InvariantCulture)`** | `ClientRunner.cs:410-411` | `ResourceSampler.cs:298`、`Target/LedgerWriter.cs:50` | 加 `Timestamps.Iso(DateTimeOffset)`。省 4 处魔数格式串，且保证两端时区处理一致（`LedgerWriter` 写 UTC，`ResourceSampler.cs:298` 显式 `.ToUniversalTime()`，`ClientRunner.cs:410` 依赖 `DateTimeOffset.UtcNow`） |
| 7 | **`parameters`/`gates` 写入前导块 ×4** | `ClientRunner.cs:332-335, 341-344` | `ClientRunner.cs:377-380, 381-384` | 一个 `WriteMap(writer, name, map)`。省约 12 行（注意 §3.4 的时序忠告） |
| 8 | **`if (hasHandles) WriteNumber("handles", …) else WriteNull("handles")` ×2** | `ResourceSampler.cs:437-444` | `ResourceSampler.cs:531-538` | 折进 `WriteCounters`（:275-282），加一个 `bool hasHandles` 参数。省 14 行 |
| 9 | **端口赋值样板**：client 用 `Action<T,int>` 委托助手，target 把同一形状手写 4 遍 | `ClientRunner.cs:148-170` | `TargetRunner.cs:167-215` | 统一成一份（哪个形状都行，但只能有一个） |

另外一处**结构性**重复值得单列：`PlanFile.s_knownKinds`（:50-61）与 `ArmDispatch` 的 switch
（`Arms/ArmDispatch.cs:7-16`）各存一份 9 个 kind 的字符串。加一个臂要改 4 个地方（这两处 + README +
`analyze.py`），没有任何机制保证一致（两处的排列顺序已经不同）。建议提一个
`ArmKind`（canonical 字符串 + `TryParse` + `Dispatch`）供两者共用。**行为不变**。

还有一处同类：**7 个调用点上逐字相同的 pragma 对**
（`ArmContext.cs:73`、`LatencyArm.cs:460/690`、`LossArm.cs:142`、`ReliabilityArm.cs:195`、
`MixArm.cs:656`、`DnsArm.cs:287/429`，另 `PersistentArm.cs:226` 是少一条规则的变体）：

```csharp
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
// ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
```

这些规则是**因为 `Pacer.WaitUntil` 存在一个异步同胞 `WaitUntilAsync`**（`ArmContext.cs:64-76`）才
触发的。建议把阻塞等待收成一个**没有异步同胞**的入口（例如把现在的 `WaitUntil` 改名为
`WaitBlocking`，让它只被 `WaitUntilAsync` 与各臂调用，并撤掉 `WaitUntilAsync` 这个同胞名或把
异步版本收进内部），pragma 就能从 7 处减到 1 处（`ArmContext.cs:73` 那一处仍需保留给定义体内的调用）。
**行为不变**；唯一需要注意的是这 7 处 pragma 都在**发包/步进热路径**的调用点附近，改名时必须
逐处确认分析的抑制仍然生效（`dotnet build -c Release` 零警告 + `jb inspectcode` 零 Issue 是门禁）。
这条跨出了我这份文件清单，但它由 `ArmContext` 的 API 形状决定，所以归到这里。

---

## 8. 超长方法 / 超大类型

### 8.1 我这份清单里**没有超长方法**（实测）

用「签名行到匹配右花括号」的全量扫描，12 个文件里最长的 6 个方法：

| 方法 | 位置 | 行数 |
|---|---|---|
| `ClientRunner.TryCreate` | `ClientRunner.cs:52-109` | 58 |
| `PlanFile.TryLoad` | `PlanFile.cs:63-120` | 58 |
| `UdpReliabilityTracker.Classify` | `UdpReliability.cs:330-387` | 58 |
| `ResourceSampler.SampleNamedAsync` | `ResourceSampler.cs:497-545` | 49 |
| `PlanFile.TryReadArm` | `PlanFile.cs:217-265` | 49 |
| `ResourceSampler.ReadProcessTotals` | `ResourceSampler.cs:454-495` | 42 |

也就是说，第 8 项清单在这个文件集合里**只对类型成立，不对方法成立**（超过 60 行的方法一个也没有）。
真正上千行的方法在 `Client/Arms/` 里（`LatencyArm.cs` 1013 行等），那个属于 PRD 的 F1/F2/F3，
本文不重复。

### 8.2 真正的超大类型

| 类型 | 有效行 | 结论 |
|---|---|---|
| `ResourceSampler`（含同文件 5 个辅助类型） | 474 | 违反 AC1；5 个职责（§4.1） |
| `ClientRunner` | 439 | 违反 AC1；6 个职责（§3.1） |
| `PlanFile` | 262 | 未超标，但一个 `static class` 里混了「文件读取 + JSON 解析 + 逐字段搬运 + 语义校验 + 文件名消毒」5 件事 |
| `UdpReliabilityTracker` | 约 250 | 未超标；`SequenceBitmap` 独立成类是对的，建议继续把 `LossCounts` 的组装（`Classify` 的 58 行）留给 tracker、把 `SequenceBitmap` 移到自己文件（R1 的自然接缝） |

### 8.3 六个 45+ 行方法的拆分接缝（每个 1~2 行说明）

| 方法 | 混了几个层次 | 接缝 |
|---|---|---|
| `ClientRunner.TryCreate:52-109` | 词法（切 `=`）+ 分派（`TryApply`）+ 校验（IP/out） | 词法与分派整体搬到 `Cli/OptionParser`（§7.1），本方法只剩「解析 + 三条必填校验」约 20 行 |
| `PlanFile.TryLoad:63-120` | 读字节 + `JsonDocument` 解析 + 根形状 + 逐臂循环 + 重名 + 计数校验 | 拆成 `LoadBytes` / `LoadRoot` / `LoadArms` 三个私有方法；`LoadArms` 是唯一持有 `names` 集合的地方（顺带修 §2.4 的映射后重名检查） |
| `UdpReliability.Classify:330-387` | 退休扫描 + 三个计数器初始化 + 单遍分桶 + 组装 record | 把 :341-374 的分桶循环抽成 `CountBuckets(windowMilliseconds, out arrived, out late, out never, out undetermined, out corruptDatagrams)`（5 个 out 参数不好看，可用一个私有可变结构），外层剩 10 行 |
| `ResourceSampler.SampleNamedAsync:497-545` | 取快照 + 组装聚合 + 写入 JSON（30 行 lambda） | 把 lambda 体抽成 `WriteNamedSample(Utf8JsonWriter, SamplerTarget, string, long, ProcessTotals, bool hasHandles)`，与 `SampleSelfAsync:414-452` 共用（两份 lambda 的骨架 90% 相同，是 §7.8 那处重复的根源） |
| `PlanFile.TryReadArm:217-265` | 形状 + name + kind + seconds + 15 个数值 + protocol/modeMix + protocol 校验 | 数值搬运已在 `ReadArmNumbers`（:267-284）；把「name/kind 的必需项」与「可选键的读取 + 语义校验」再分一层，并让所有消息带臂序号（§2.6） |
| `ResourceSampler.ReadProcessTotals:454-495` | 枚举 + 逐进程读 + 聚合 | 循环体（:477-491）抽成 `Accumulate(Process, bool hasHandles, ProcessTotals)`；配合 §4.8 的 `IProcessProbe` 它会被整体搬走 |

---

## 9. 命名 / 撒谎注释 / 魔数 / 死代码 / 吞异常

### 9.1 注释与事实不符（这一项我找得很仔细，只找到 1 条 + 2 条半）

| 位置 | 注释说的 | 实际 |
|---|---|---|
| `UdpReliability.cs:127-129` | 「最重的计划臂是 500/s × 两分钟，所以 25 万的上限远高于任何合法序号」 | **最严重的一条**：只对当前提交的计划文件成立，而越界的后果不是「少算」而是 `IndexOutOfRangeException` 崩溃（D7，§5.2）。建议改成陈述不变量，并指向校验它的地方 |
| `ResourceSampler.cs:132` | 「每个 tick 都已经自己报告过了」（解释为什么 `DisposeAsync` 只打 stderr） | 对**tick 内**的失败成立；`LoopAsync` 若在 tick 之外抛（例如 `_gate.WaitAsync` 的取消之后又在 :343 抛），这一句就不成立。属于半条，建议加个「tick 内」限定 |
| `FrameBuffer.cs:28-31` | `FlipPayloadByte(int payloadBytes, int offset)` 的 `offset % payloadBytes` | 两个调用点（`LossArm.cs:104,110`）都传 `offset = 0`，这个参数与取模是**死的一般化**，而且会在热路径（每第 n 个数据报）上多算一次取模。半条 |

反面（做得好的，值得在新的拆分里保留）：`PlanFile.cs:9-31` 的 schema 文档与实现一致；
`ArmSpec.cs:56-67` 解释了 `LossWindowMs` 与 `Window` 为什么是两个键；`UdpReliability.cs:281-282`
解释了乱序定义；`ClientRunner.cs:51` 与 `ArmContext.cs:73` 的抑制都带理由。

### 9.2 魔数清单（建议命名或加注释，数值本身不能改）

| 位置 | 值 | 含义 | 是否契约 |
|---|---|---|---|
| `JsonValue.cs:60` | `digits = 3` | 所有发布数字的小数位 | **是**（JSON 文本） |
| `JsonValue.cs:71` | `6` | 比率的精度 | **是** |
| `JsonValue.cs:17` 等 | `1e9`/`1e6`/`1000.0` | 纳秒/微秒换算 | 否 |
| `ClientRunner.cs:204` | `[..16]` | planHash 前 16 个十六进制字符 | **是**（analysis 读） |
| `ClientRunner.cs:22`（JsonlFile） | `64 * 1024` | 文件缓冲 | 否 |
| `ClientRunner.cs:404` | `8192` | run.json 缓冲 | 否 |
| `ArmContext.cs:49-56` | `8` / `6` / `0.2` / `32` | 定时器粒度经验值（含 Windows 15.6 ms 的注释在调用点） | 否，但**是行为**（时序） |
| `LogHistogram.cs:57-61` | `11` / `2048` / `34` / `2^34-1` / `1` | 桶布局 | **是**（§6.3） |
| `UdpReliability.cs:130` | `2^18-1` | 序号空间 | 半（发布 `outOfRangeSequences` 的触发点） |
| `UdpReliability.cs:294` | `65535` | 到达毫秒的静默截断 | **是**（§5.3） |
| `UdpReliability.cs:201` | `-1` | 「未到达」哨兵 | 否 |
| `PlanFile.cs:314` | `1e-9` | 整数容差 | 否 |
| `ResourceSampler.cs:89` | `1 s` | 采样周期（README 承诺 1 Hz） | **是** |

### 9.3 死代码 / 无效果代码

| 位置 | 内容 | 判定 |
|---|---|---|
| `ResourceSampler.cs:130` | `when (exception is not OperationCanceledException)` | 死过滤（前面已 catch 全部 OCE） |
| `ResourceSampler.cs:407` | 同上 | 死过滤 |
| `ClientRunner.cs:193-197` | 第二次 `IPAddress.TryParse` | 永假分支（`TryCreate` 已校验且 options 不可变） |
| `JsonValue.cs:54-56` | `default:` 分支 | 今天不可达（§1.2），是**潜伏**而非死代码 |
| `Arms/ArmDispatch.cs:16` | `_ => throw new InvalidOperationException(...)` | 不可达（`PlanFile` 已拒未知 kind），但作为防御保留；注意它在 `RunArmAsync` 的白名单里（`InvalidOperationException`） |
| `SequenceBitmap.TryClear` | 只被 `MarkSendRefused` 调用 | 活的 |
| `FrameBuffer.Build` 两个重载 | 3 参 5 个调用点、4 参 3 个调用点 | 都活的，重载合理 |
| `FrameBuffer.FlipPayloadByte` 的 `offset` 参数 | 只被传 0 | 死参数（§9.1） |
| `Clock` 的 5 个成员 | `Now/ToSeconds/FromSeconds/ToNanoseconds/ToMicroseconds` | **全部活的**（我逐个查过调用点）——它看起来像杂物箱但不是死代码；缺的只是 `ToMilliseconds`（§7.5） |
| `JsonValue.PerSecond` | 8 个调用点 | 活的 |
| `ArmSpec.ModeMix = string.Empty`（:42） | 只对 `BaseArm.LatencyPhaseSpec/LossPhaseSpec`（`BaseArm.cs:76-97`）手工构造的 spec 生效，而 latency/loss 臂都不读它 | 无害；若做 §2.8 的加载期校验，必须按 kind 跳过无关键 |

### 9.4 吞异常清单（逐条判定）

| 位置 | 吞掉什么 | 判定 |
|---|---|---|
| `FrameBuffer.cs:53-64` `TryConnectAsync` | `SocketException`/`OperationCanceledException`/`ObjectDisposedException` → `false` | **本篇与姊妹审查判定不同，请裁决**：`arms-code-quality-audit.md` §8.5 的结论是「不改（取消时整个 run 都会终止，没有下游消费这个标记）」。本篇把范围收窄到可确证的一点：`ReliabilityArm.cs:602-606` 把 `false` 写成 `ExchangeStatus.ConnectFail`，而 `metrics.outcomes.connectFail` **在**契约读集里（README:321，且 REL 的恒等式要求七个 outcome 之和等于 `connectAttempts`），所以取消落在 `ConnectAsync` 里时，一条「被取消」的尝试会被记成产品的连接失败。是否可达取决于取消发生时该 attempt 是否还会进 `outcomes` —— 这一点我没有构造出实验，属于**待定**，建议先记录不改 |
| `ClientRunner.cs:262-277` | 只接 4 类；其余外泄 | **该改**（§3.3，D1/D2/D4 的根因） |
| `Program.cs:101-109` | 只接 OCE | **该改**（顶层兜底） |
| `Program.cs:81-89`（target） | 只接 `SocketException`；`LedgerWriter` 构造（`Target/LedgerWriter.cs:28`）抛 `IOException` 时外泄 | **该改**（同样是「崩掉且不写 summary」） |
| `ResourceSampler.cs:126-134` | OCE + 任意异常 → stderr | 合理（有注释说明「采样故障不能终结被观测的 run」） |
| `ResourceSampler.cs:345-349` | tick 内任意异常 → stderr | 合理（但见 §4.7 的限流） |
| `ResourceSampler.cs:363-366, 374-377` | 单个 self/进程名失败 → `samplerError` 记录 | 合理，且是正确做法（写进数据里） |
| `ResourceSampler.cs:147-166` 等 | 计数器读不到 → `false` | 合理（转成 `countersRead:false` + `readError`） |
| `ResourceSampler.cs:259-273` | `ReportToStderr` 自身 IO | 合理（有注释） |
| `FrameBuffer.cs:67-81` `ShutdownQuietly` | 关方向失败 | 合理（有注释：结局已在别处记录） |
| `Program.cs:122-140` `Cancel` | ODE/OCE/AggregateException | 合理（有注释） |
| `JsonValue.cs:54-56` | 未知类型 → 字符串 | **该改**（§1.2，静默降级） |

---

## 10. 可测试性

### 10.1 现状：这个 harness 有**零**个自动化测试

- 全仓 13 个测试工程，没有任何一个引用 `WinForward.E2E`（`rg 'WinForward\.E2E' tests/ src/` 无结果）；
- E2E 工程没有 `InternalsVisibleTo`（`WinForward.E2E.csproj:1-7`），而所有类型都是 `internal`；
- 唯一的验证是 `scripts/selftest.sh`，它需要：一个能跑起来的 target（固定端口 31010/5301/5302）、
  一个 plan 路径、一个 Linux 主机。它**不断言任何东西**，只打印每条 `result` 的 metrics，
  靠人眼比对。而且它没有 plan 参数时 `exit 0`（`selftest.sh:34-40`）—— CI 里漏传参数会静默变绿。

所以 AC1/AC2/AC4 这类「行为归零」的重构目前**没有门禁**。这是 R6/R7 必须补上的，也决定了 §12 的排序。

### 10.2 今天就能测的（纯逻辑，零接缝）

按 R6 的清单逐项对照，这些全部不需要网络、不需要真时钟、不需要进程：

| 模块 | 位置 | 备注 |
|---|---|---|
| UDP 序列记账与分类 | `UdpReliability.cs` 全文件 | §5.6 列了 8 条用例；**R6 点名项，成本最低** |
| 延迟直方图 | `LogHistogram.cs` | §6.2 的边界 + 百分位语义 + clamp |
| Plan 解析与校验 | `PlanFile.cs:63-215` | 可直接吃 D1–D7 的最小 plan；`TryParseModeMix` 尤其值得覆盖（12 个分支） |
| JSON 值写入 | `JsonValue.cs` | 用 `MemoryStream` + `Utf8JsonWriter` 断言文本；含 §1.2 的 fallback 与 `null` 约定 |
| JSONL 落盘 | `JsonlFile.cs` | 临时目录 + 逐行解析；含并发写（多线程 `WriteAsync`）与 `\n` 结尾 |
| CLI 解析 | `ClientRunner.cs:52-183` | 拆出 `Cli/ClientOptionsParser` 后就是纯函数（§3.2），可覆盖 10 个选项 + 3 条必填 + 端口边界 |
| 文件名消毒 | `PlanFile.cs:206-215` | 含 D3 的重名与 D4 的长度（后者取决于放在哪一层） |
| `Wire/` 全部 | `FrameCodec`/`Crc32C`/`DnsWire`/`Filler`/`TcpCommand`/`TrailerProtocol` | R6 点名项，且两端共享，回归价值最高 |
| `FrameBuffer` | `FrameBuffer.cs:6-34` | 断言 `Build` 的返回长度与头部字节；含 payloadBytes 边界 |

### 10.3 需要接缝的（每条给最小接缝）

| 目标 | 今天的障碍 | 最小接缝 |
|---|---|---|
| 臂的**时序/步进**行为 | `Clock` 是静态类包着 `Stopwatch`（`ArmContext.cs:9-20`），`Pacer` 自己也直接读 `Stopwatch.GetTimestamp()`（:40, 66） | 注入 `TimeProvider`（.NET 8+，自带 `TimestampFrequency`）到 `ArmContext`，`Pacer` 改成带一个 `long frequency` 字段（它已经是个 struct，加字段零成本）。**注意**：`Pacer.WaitUntil` 的忙等语义是测量的一部分，测试只能验证「IntendedTicks 的算术」与「deadline 之后的返回」，不能验证真实抖动 |
| 臂的**网络**行为 | `ArmContext.CreateTcpSocket/CreateUdpSocket`（:153-155）是唯一的 socket 工厂，而 `ArmContext` 是 `sealed` + `init` 属性，无法继承替换 | 给 `ArmContext` 加 `Func<Socket> TcpSocketFactory/UdpSocketFactory`（默认值就是现在这两行）。这是全集合里**唯一**现成的注入口，成本最低 |
| 记录写入的**形状** | 所有写入器都持具体 `JsonlFile`（`ArmContext.Sink`、`ClientRunner` 的 4 个 writer、`ResourceSampler` 的 3 处） | `IJsonlSink { ValueTask WriteAsync(Action<Utf8JsonWriter>, CancellationToken); }`，测试用内存 sink |
| 采样器的 OS 读取 | §4.8 | `IProcessProbe` + `TimeProvider` + `IJsonlSink` |
| **臂的 parameters 发布** | 计划结构体是 `private`（如 `LatencyArm.LatencyPlan:868-918`） | 把每个臂的 `XxxPlan` 提到自己的文件并改 `internal`。这一步同时满足 R1（拆文件）与 R6（可测「默认值 → parameters」这条链路），**一份改动两处收益**，是性价比最高的一刀 |

### 10.4 `InternalsVisibleTo` 的连带成本（提前告知）

新增测试工程要读 `internal` 类型，必须加 `InternalsVisibleTo("WinForward.E2E.Tests")`
（仓库先例：`src/WinForward.Core/WinForward.Core.csproj:8-14`）。副作用有两个，都要预算：

1. 仓库的 `AGENTS.md` 明确警告：IVT 成员会被 `jb inspectcode` 报 `MemberCanBePrivate` 假阳性，
   属于「已知假阳性，不得为了消警告而改代码」。加 IVT 后要按这条政策处理，不能把类型改 `public`。
2. 加测试工程会不会被 `Directory.Build.props:14` 的 `IsTestProject != true` 条件影响：
   条件里用的是 `IsTestProject`，新工程必须设 `<IsTestProject>true</IsTestProject>` 才能
   避开那 4 个分析器包（否则 `dotnet build` 会多出分析器诊断）。这属于 R6 的落地细节。

### 10.5 R7/AC4 的等价性证据配方（可直接复用）

我实测过「同一份 plan、改动前后」的比对方式，落到一个脚本：

```bash
# 1) 冻结基线
scripts/publish.sh && scripts/selftest.sh scripts/plans/selftest-plan.json   # 输出在 /tmp/wf-bench/selftest/out
cp -r /tmp/wf-bench/selftest/out /tmp/base
# 2) 重构后重跑，落到 /tmp/after
# 3) 归一化时间与延迟字段后逐行比对
python3 - <<'PY'
import json,glob,os,re
VOL=re.compile(r'^(startedTicks|endedTicks|ticks|startedUtc|endedUtc|startUtc|wallSeconds|minUs|maxUs|meanUs|p50Us|p90Us|p99Us|p999Us)$')
def norm(o):
    if isinstance(o,dict): return {k:('<ts>' if VOL.match(k) else norm(v)) for k,v in o.items()}
    if isinstance(o,list): return [norm(v) for v in o]
    return o
for d in ('/tmp/base',):
    for f in sorted(glob.glob(d+'/*.jsonl')):
        for line in open(f):
            r=json.loads(line)
            print(os.path.basename(f), json.dumps(norm(r),sort_keys=True))
PY
```

关键点：**键的顺序也要归一化**（`sort_keys=True`），否则 §3.4/§7.7 这类「只改写入顺序」的重构会
产生假差异；而**真实差异**（少一个键、多数值变字符串、百分位变了）会精确暴露。
注意 `scripts/selftest.sh` 的 plan 是 `scripts/plans/selftest-plan.json`，它含 `dnsPort: 5302`
（对应 target 的 `--dns-alt-port`），正好覆盖 DNSALT 臂。

---

## 11. 不要动的地方

| # | 位置 | 为什么不能动 |
|---|---|---|
| 1 | `LogHistogram` 的桶布局与五个常量（:57-61）、`Record` 的 clamp（:83） | p50/p90/p99/p999 由 `analyze.py:246` 逐字读取、从不重算；改布局等于静默重写所有历史与将来的百分位 |
| 2 | `JsonValue.Round` 的 3/6 位小数、`Microseconds` 的换算（:60-63）、`ClientRunner.cs:204` 的 `[..16]` | 这些数字与 planHash 长度直接进 JSON 文本，analysis 拿它做键/展示 |
| 3 | JSON 键的全部拼写（含 `metrics` 的四种约定与「键里带点」的 `tcp.sentOk`） | README:303-354 列了完整读集，`analyze.py` 按 `/` 走路径；改名只会让格子变 `n/a`。**注意 PRD R4 打算改这里 —— 那必须配 AC9 的迁移表，且不能与「行为归零」同批做** |
| 4 | `latency` 只写非空直方图（`ArmContext.cs:113-119`） | 键集合逐臂不同是**已文档化**的性质（README:261），补齐会新增键 |
| 5 | `null` 语义：`Ratio` 的空分母（`JsonValue.cs:70-71`）、非有限 double → null（:76-86）、逐进程 `countersRead:false` 时写 null（`ResourceSampler.cs:306-316`） | README:270-284 与 `analyze.py` 都依赖「缺键 ≠ null ≠ 0」 |
| 6 | `absent:true` 时不写计数块（`ResourceSampler.cs:517-520`） | 是既有记录形状；改它要同步 README:330 的 samples 读集 |
| 7 | `SanitizeFileName` 的**字符映射规则**（`PlanFile.cs:206-215`） | 文件名出现在 `run.json` 与 campaign 目录树里（`benchmarks/results/**/raw/`）。修 D3 要用「拒绝重名」，不能改映射 |
| 8 | `Pacer` 的忙等阈值与 `Thread.SpinWait`（`ArmContext.cs:49-56`）、`WaitUntil`/`WaitUntilAsync` 的分工（:64-76） | Windows 定时器粒度（15.6 ms）是实测结论，注释里有理由；改成 `await Task.Delay` 会真实改变时序 |
| 9 | `Dedicated.RunOnOwnThreadAsync` 的 `LongRunning | DenyChildAttach`（`ArmContext.cs:84-91`） | README:355-407 说明了「lane 必须在自己的线程上启动，否则首个 await 同步完成时后续 lane 根本不会启动」——这是一条修过的 bug，注释就在 :79-83 |
| 10 | `ResourceSampler.ClearTargetAsync` 的屏障（:106-111） | 见 §4.6：简化它 = use-after-dispose |
| 11 | `JsonlFile` 复用 `MemoryStream`/`Utf8JsonWriter`（:23, 31-36） | 零分配写入路径 |
| 12 | `SequenceBitmap._words` 初值 1024 与翻倍增长（:8, 21-31） | 与 `MaxSequence` 匹配（1024 → 4096 字 = 262144 位），改小会多几次扩容、改大会多分配 |
| 13 | `ClientRunner.cs:51`、`ArmContext.cs:73`、`LossArm.cs:142-145` 等处的 `#pragma warning disable` / `// ReSharper disable` | 每一条都带实测理由（Windows 定时器、RCS1239 的 for 循环禁令）；删之前先读注释（§7 那条「把抑制从 7 处减到 1 处」的建议是**移动**，不是删除） |
| 14 | `UdpReliabilityTracker.MarkSent/Retire/ResolveSlot/Classify` 的**记账**与 `SequenceBitmap.TrySet` 的边界 | 10-06 审查的第 3/4/5 条刚修过这里。要加的是「越界序号不许写数组」这一道**守卫**（§5.2 建议 1，Tier 0.1），而不是改记账；`sent`/`supplied`/`Outstanding` 之间的关系不要顺手调 |
| 15 | `LossArm` 的调度序号即 UDP 序号的约定（`LossArm.cs:134-166`：`index` 每个提供过的槽都加，窗口溢出时 `continue` 但序号已消耗） | 改成「只给真正发出的数据报分配序号」会改变 `sent`/`supplied`/`windowsOverflow` 三个已发布量的关系（10-06 的片区）。D7 的正确修法是加守卫 + 加载期校验，不是改这里 |
| 15 | target 侧的线格式与账本格式（`Wire/`、`Target/LedgerWriter.cs` 的记录形状） | 两端是独立部署的二进制，版本可能不一致（PRD 的「范围外」） |

---

## 12. 建议的重构顺序（收益 / 风险 排序）

排序原则：先把**会丢数据/崩**的修掉（独立、可实测、不依赖拆分），再建**测试工程**（它是后面
所有「行为归零」的门禁），最后才做纯搬移与语义调整。每一档都给出「改哪里 / 预期 diff / 门禁」。

### Tier 0 —— 崩溃与数据丢失（每条独立成 commit，全部**改变行为**）

| 顺序 | 任务 | 改哪里 | 门禁 |
|---|---|---|---|
| **0.1** | **`MarkSent` 的越界止血（D7）** | `UdpReliability.cs:196-201` 的边界提到方法开头 | `ratePerSecond:20000, seconds:20` 的 LOSS plan 不再崩、退出码 0 或 1、有 `result` 与 `run.json`、`outOfRangeSequences > 0` |
| 0.2 | `--plan=` 空值不再静默换 plan（D2） | `ClientRunner.cs:121` 或 `PlanFile.cs:125` + `ClientRunner.cs:445` | 最小 plan + `--plan=`：退出码 2 或至少不崩；`run.json` 合法 |
| 0.3 | 顶层兜底：任何臂级异常变成 `error` 记录 + `run.json` | `ClientRunner.cs:262-277` 加一个 `catch (Exception)`；`Program.cs:92-110, 68-90` 加顶层 catch | D1/D4 的最小 plan：退出码 1、有 `error` 记录、`run.json` 合法且 `failed:true` |
| 0.4 | 加载期范围校验：`dnsPort` 1..65535、各数值键的下界、`TryReadInt` 的超范围强转（§2.7）、`lossWindowMs ≥ 0`、**以及 `ratePerSecond × seconds ≤ MaxSequence`（D7 的可诊断化）** | `PlanFile.cs:267-321` + 一个按 kind 的校验（§2.8） | D1 的最小 plan 变 load error（退出码 2）；`window:100.5` 变 load error（D5）；20k/s × 20s 的 plan 变 load error |
| 0.5 | 文件名：映射后重名检查 + 长度上限（D3、D4） | `PlanFile.cs:103`（用 `SanitizeFileName` 后的名字）、`PlanFile.cs:206-215` 或加载期长度校验 | `A/B`+`A_B` 的 plan 变 load error；270 字臂名变 load error |
| 0.6 | 空 `--sampler-process` 拒绝（D6）；`--label --out x` 的「值像选项」判定 | `ClientRunner.cs:129-131, 71-80` | 单测 + 手工两条命令 |

Tier 0 全部加起来大约 50 行改动、6 个最小 plan 文件，而且它们**不碰**任何记录形状（0.1 会让
`outOfRangeSequences` 从「崩溃前」变成「非零」，这本来就是已声明的披露字段），
所以可以先于 R7 的基线冻结完成（冻结基线时应当已经带上这些修复，否则基线里包含已知崩溃）。
0.1 与 0.4 建议同一个 commit：前者保证不崩，后者保证可诊断，缺一个都不完整。

### Tier 1 —— 建测试工程（R6/AC5 的地基，收益最大的一步）

| 顺序 | 任务 | 说明 |
|---|---|---|
| 1.1 | 新建 `tests/WinForward.E2E.Tests`（设 `IsTestProject=true`）+ `InternalsVisibleTo`（§10.4） | 建议先放 UDP 记账、直方图、plan 解析、`Wire/` 四组（§10.2），一次就能覆盖 R6 点名的全部模块 |
| 1.2 | 把 D1–D7 各写成一个用例 | Tier 0 的修复从此有回归 |
| 1.3 | 写一条「记录形状」测试：用内存 sink 跑一个 `IdleArm`，断言 `result` 的键集合与 `null` 约定 | 它是 R3/R4 改契约时的安全网 |
| 1.4 | 落 §10.5 的归一化比对脚本（`scripts/compare-records.py` 之类） | R7/AC4 的直接交付物 |

### Tier 2 —— 纯搬移与去重（**行为不变**，可在 R7 基线冻结后做）

| 顺序 | 任务 | 预期收益 | 风险 |
|---|---|---|---|
| 2.1 | `ClientRunner` → 4 个文件（§3.2） | 解 AC1；顺带消化 §7.1/§7.2 的解析器重复 | 低（纯搬移），但注意 `Cli/` 命名空间与 `#pragma` 的位置 |
| 2.2 | `ResourceSampler` → 3 个类型 + `TryRead<T>` 合并（§4.1、§4.2） | 解 AC1；省约 55 行 | 低 |
| 2.3 | `JsonlFile` 与 `LedgerWriter` 合并成 `JsonLineWriter`（§1.6） | 消掉一份 IO+JSON 实现；顺带去掉 ledger 的逐记录分配 | 中：两边的错误策略不同，必须参数化并保留 ledger 的限流 |
| 2.4 | `Clock.ToMilliseconds` + `Timestamps.Iso` + `WriteMap`/`WriteCounters` 收敛（§7.5-7.8） | 省约 40 行，消掉 3 类魔数格式串 | 低 |
| 2.5 | 7 处 pragma 收敛成 1 处（§7 末） | 消掉跨 7 个文件的注释噪声 | 中：必须逐处确认抑制仍生效（`dotnet build` 零警告 + `jb inspectcode` 零 Issue） |
| 2.6 | `ArmKind` 单一来源（`PlanFile.s_knownKinds` + `ArmDispatch`） | 消掉「加一个 kind 要改 4 处」 | 低 |
| 2.7 | `PlanFile` 拆成「读取 / 解析 / 校验 / 文件名」四块（§8.3） | 为 §2.8 的加载期校验腾位置 | 低 |
| 2.8 | 各臂的 `XxxPlan` 结构体提到自己文件并 `internal`（§10.3 末行） | 同时服务 R1 与 R6 | 低，但涉及 `Arms/` 下的 7 个文件 |

### Tier 3 —— 语义相邻（每条都要先记进 10-06 的审查，再单独 commit）

| 顺序 | 任务 | 为什么放最后 |
|---|---|---|
| 3.1 | `SocketOps.TryConnectAsync` 的三态化（§9.4） | 直接影响 `connectFail`/`pageErrors` 这类产品指标，属于测量语义；且与 `arms-code-quality-audit.md` §8.5 的「不改」结论冲突，**先裁决再动手** |
| 3.2 | 采样器聚合字段的「0 → null」（§4.4、§4.5） | 改变 `samples` 的契约，必须与 R5/`analyze.py` 同步 |
| 3.3 | `samplerError` 限流（§4.7） | 改变记录条数 |
| 3.4 | `JsonValue.Write` 的 fallback 改成抛（§1.2）、`PerSecond` 返回 `null?`（§1.3） | 今天是不可达路径，改了是「防将来」；放在契约变更批次里一起走 |
| 3.5 | D7 的后续：越界槽在 `sent`/`supplied`/`gates.clientSendLoss` 之间怎么算（§5.2 末尾） | 守卫本身已进 Tier 0.1；但「越界槽算不算 client loss」是测量语义，属于 10-06 的片区，要先记录再改 |
| 3.6 | `meanUs` 的精度重载（§6.5） | 1 ULP 级的契约变更 |

### 每档的统一门禁

```bash
dotnet build WinForward.slnx -c Release                      # 零警告
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # 输出为空
jb inspectcode -f=Xml -e=HINT WinForward.slnx                # 零 <Issue>
dotnet test WinForward.slnx -c Release                        # Tier 1 之后必须全绿
scripts/publish.sh && scripts/selftest.sh scripts/plans/selftest-plan.json      # 端到端
python3 scripts/compare-records.py /tmp/base /tmp/after       # Tier 2/3 的等价性证据（§10.5）
```
