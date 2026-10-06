# 技术设计：WinForward.E2E 重构与语义修复

> **开工前裁定（2026-10-07，设计稿审查后）**：`design-decisions.md` 取代本文件的以下部分——
> §3.2 的 `ILaneChannel` 接口草案与 §3.4 的 `ReplyClassifier` 签名（见 D3）、§2.2 的形状测试机制（见 D5）、
> §5.1/§5.2 的缺陷现状（见 D0）、§6 层零与层三的判据（见 D6/D7）、§7 的 P2→P4 顺序（见 D8）。
> **冲突处以 `design-decisions.md` 为准**。

对应 `prd.md` 的 R1–R12。本文回答四个问题：**目标形态是什么**、**接缝划在哪里**、
**13+3+3 条语义修复各自落在哪个模块**、**怎么在没有可信基线的情况下证明改对了**。

---

## 0. 设计约束（先钉死不可动的边界）

| 约束 | 来源 | 含义 |
|---|---|---|
| **线格式不可动** | client 与 target 是独立部署的二进制 | 帧布局、魔数 `0x57464531`、5 个偏移、大端、header 28 / trailer 4、CRC32C 参数、`Filler` 输出字节流、`TcpCommand` 5 字节布局、`DnsWire` 生成的字节 —— 全部逐字节保持 |
| **记录顶层骨架不变** | 分析逻辑要能被独立验证 | `{type, arm, kind, label, parameters, metrics, gates, latency, notes, startedTicks, endedTicks}` 的**形状**保持；本任务改的是**内部字段命名与类型**，不是形状 |
| **账本只增不改** | 账本被分析器消费且是人看的 | 字段名、`type` 拼写、`utc`/`label` 的位置与顺序不动，只允许新增字段 |
| **范围约束** | `prd.md` 范围外 | 绘图、线格式、`src/`、机器专有脚本 |

**为什么"顶层骨架不变"是设计约束而不是保守**：分析器重写需要一个**可独立验证的 oracle**
（见 §6）。骨架不变意味着 Python 版只需替换字段名即可继续作参考实现；形状一变，参考实现的
加载层就要重写，oracle 的可信度随之下降。

---

## 1. 目标形态

```
benchmarks/
├── WinForward.E2E.Contracts/          契约：记录模型 + 字段名 + 序列化
│   ├── ArmKeys.cs                     每臂字段名的唯一定义点
│   ├── Records/                       ArmResult, ArmSummary, RunRecord, Sample, ErrorRecord
│   ├── Metrics/                       每臂的强类型 metrics 值对象
│   └── Json/                          JsonlSink, JsonWriteExtensions, JsonReadExtensions
│
├── WinForward.E2E/                    harness（引用 Contracts）
│   ├── Program.cs  Cli/
│   ├── Client/
│   │   ├── ClientRunner.cs  Plan/  ResourceSampler.cs  UdpReliability.cs  LogHistogram.cs
│   │   ├── Lanes/                     ★ 新：传输接缝
│   │   │   ├── ILaneChannel.cs        TCP/UDP 两个 adapter 的接口
│   │   │   ├── TcpLaneChannel.cs
│   │   │   ├── UdpLaneChannel.cs
│   │   │   ├── LaneEngine.cs          共享引擎：配速、在途窗口、延迟队列、drain、统计
│   │   │   └── ReplyClassifier.cs     ★ 回复校验阶梯（三份合一）
│   │   └── Arms/                      每臂 1–2 个文件，全部 ≤400 有效行
│   ├── Target/
│   │   ├── TcpTargetServer.cs  UdpEchoServer.cs  DnsServer.cs
│   │   ├── SourceCensus.cs            ★ 从 UdpEchoServer 提出
│   │   ├── TcpConnectionProtocol.cs   ★ 从 TcpTargetServer 提出（可脱网单测）
│   │   ├── Sockets.cs  SocketIo.cs  TcpAcceptLoop.cs   ★ 新增：三台 server 的共同部分
│   │   ├── LedgerWriter.cs  TargetRunner.cs  TargetOptions.cs
│   └── Wire/                          FrameCodec, FrameStreamReader, Crc32C, Filler,
│                                      TrailerProtocol, TcpCommand, DnsWire
│
├── WinForward.E2E.Analysis/           ★ 新：C# 分析器（引用 Contracts）
│   ├── Loading/   Model/   Stats/   Checks/   Metrics/   Findings/   Tables/   Verdict/
│   └── Program.cs
│
tests/WinForward.E2E.Tests/            ★ 新：单元测试
```

`WinForward.E2E` 保持 `OutputType=Exe`；`WinForward.E2E.Direct` 仍由同一项目换
`AssemblyName` 发布（产品按镜像名区分应用，这个机制不动）。

---

## 2. 契约设计（R3 + R4）

### 2.1 问题重述

现状：字段名是**散落在 9000 行里的字符串字面量**，同一个统计量有 4 种拼写
（`metrics.udp.sentOk` / `metrics.udpSent` / `metrics.classes.udp.sent` / `metrics.latency.udp.sentOk`）。
改名不报错，只让分析器的格子静默变成 `n/a`。

### 2.2 设计：字段名的单一定义点

**核心手法**：把每个字段名变成 `Contracts` 里的一个 `const string`，写出侧与读入侧**都引用它**。
于是"改名"变成"改一处常量的值"，两侧自动一致——**这比让编译器报错更强**。

```csharp
// WinForward.E2E.Contracts/ArmKeys.cs
public static class ArmKeys
{
    public static class Loss
    {
        public const string Sent        = "sent";
        public const string Arrived     = "arrived";
        public const string LossRate    = "lossRate";
        // ...
    }

    public static class Latency
    {
        public const string TcpSentOk   = "tcp.sentOk";   // 点号是这一个 key 的一部分
        public const string UdpSentOk   = "udp.sentOk";
        // ...
    }
}
```

**为什么用常量表，而不是源生成序列化**（用户已裁定：benchmark **不需要** Native AOT，
「能拿到分数就行」——所以 AOT 不构成理由，下面三条才是）：

1. **分析器需要三态**：`缺键` / `null` / `0` 是三个不同的东西，且已写进 README 的契约——
   `缺键` = 这个臂不发布该量；`null` = 分母为 0、没测到；`0` = 真的测到了零。
   源生成反序列化会把「缺键」与「null」都变成 `null`，**这个区分会永久消失**，
   而它正是「没测到不能渲染成满分」这条约定的载体（即 `harness-audit.md` #13 修的病）。
2. **oracle 成本**：Python 参考实现只改字段名（`dig(record, "metrics/sent")` → 新名），
   分析逻辑一行不动。若同时改变 key 的**形态**（把扁平的点号 key 改成嵌套对象），
   参考实现的加载层要重写——而 oracle 的可信度正建立在「参考实现只动了皮毛」上。
3. **写出路径零分配、无反射、无 trimming 风险**，且 harness 侧需要精确控制形状
   （按 kind 变化的扁平对象 + 结构性嵌套 + 只写非空直方图）。

作为对照记录：反射式序列化在本仓**当前配置下是构建错误**（实测 `error IL2026` + `error IL3050`，
因为 `EnableAotAnalyzer` + `TreatWarningsAsErrors` 是全局的）。要走那条路必须给 E2E 项目单独
关掉分析器——既然源生成与常量表都不需要付这个代价，不必走。

### 2.2.1 改名的具体映射（最小粒度）

**原则**：只消除「同一概念的多种拼写」；**保留**结构性嵌套（MIX 的 `classes.*`、BASE 的
`latency`/`loss` 相位）——那是语义分组，不是命名不一致；**不动**含点号的 key 形态。

| 现拼写 | 统一为 | 出现处 |
|---|---|---|
| `metrics.udp.sentOk` | `metrics.udp.sent` | latency 臂的 UDP 通道 |
| `metrics.udpSent` | `metrics.udp.sent` | dns 臂、mix 臂的汇总 |
| `metrics.classes.udp.sent` | 不变（已在 `classes.udp` 对象内） | mix 臂的分类块 |
| `metrics.latency.udp.sentOk` | `metrics.latency.udp.sent` | base 的 latency 相位 |
| `metrics.loss.sent` | 不变 | base 的 loss 相位 |
| `metrics.tcp.sentOk` | `metrics.tcp.sent` | latency 臂的 TCP 通道 |

同一统计量的**四种拼写收敛为两种**（臂内 `udp.sent` 与相位内嵌套 `latency.udp.sent`），
后者因为表达的是「哪个测量阶段的数」而不是「哪个统计量」而保留。

其余字段（`arrived`/`late`/`never`/`corrupt`/`lossRate`/`sent`/`supplied` …）**只做跨臂一致性检查**，
不做风格改写——`sentOk` 这类名字只要全局一致就不动。

**统一的是叶子名，不是分组层级**：MIX 的 `classes.{page,bulk,dns,udp}` 与 BASE 的
`latency`/`loss` 两相位表达的是**真实的语义分组**（不同类别的流量、不同的测量阶段），
不是命名不一致。这是"统一拼写"与"拍平结构"的边界。

#### `ArmKeys` 的完整形态规约

审核指出一个真实的深度问题：原稿的常量表只收敛了 key 的**值**，没有收敛 key 的**集合**——
集合仍然定义两遍（值对象属性 + 常量声明），而且"集合相等"的测试**结构上无法发现层级写错**
（`ArmKeys.Mix.Sent = "sent"` 同时被 `classes.udp` 与 `classes.dns` 引用时，集合依然相等）。
本节把两件事都钉死。

**形态 A：一个 key 里含点号**（`latency` 臂的扁平命名）

```csharp
public static class Latency
{
    // 值本身就是一个完整的 key；分析器按 '/' 走路径得到 metrics/tcp.sent
    public const string TcpSent          = "tcp.sent";
    public const string UdpSent          = "udp.sent";
    public const string TcpLaneSupplied  = "tcp.laneSupplied";   // 数组
}
```

**形态 B：真嵌套对象**——层级用**嵌套类**表达，叶子常量**只写叶子名**，绝不拼成点号路径：

```csharp
public static class Mix
{
    public static class Classes
    {
        public static class Page
        {
            public const string Connections = "connections";
            public const string Messages    = "messages";
        }

        public static class Bulk
        {
            public const string Frames = "frames";
        }

        // 同一叶子名出现在不同层级 → 必须写成两个不同的常量路径，
        // 不允许共用 ArmKeys.Mix.Sent
        public static class Dns
        {
            public const string Sent = "sent";
            public const string Rtt  = "rtt";
        }

        public static class Udp
        {
            public const string Sent     = "sent";
            public const string LossRate = "lossRate";
            public const string Window   = "window";
        }
    }
}
```

**路径拼法（唯一约定）**：

| 形态 | 拼法 | 例 |
|---|---|---|
| A | `metrics/` + 常量值（值里的点号保留，不拆） | `ArmKeys.Latency.TcpSent` → `metrics/tcp.sent` |
| B | `metrics/` + join(层级类名小驼峰, `/`) + `/` + 叶子值 | `ArmKeys.Mix.Classes.Udp.Sent` → `metrics/classes/udp/sent` |

嵌套层级**不设深度上限**，但每层的类名就是 JSON key 的小驼峰形式（`Classes`↔`classes`）。

**形状测试的匹配规则（把"两份真相"绑成可检的等价）**：

```csharp
[Fact]
public void EveryArmWritesExactlyTheDeclaredPaths()
{
    foreach (var (kind, metricsType, writeAction) in ContractRegistry.All)
    {
        // 1) 造一个"填满全部字段"的实例（非 null、非零，避免条件写出把字段藏起来）
        var instance = ContractRegistry.FullyPopulated(metricsType);

        // 2) 用生产的 WriteTo 写到内存 JSON
        var actual = FlattenPaths(Write(instance));

        // 3) 反射 ArmKeys 的对应子树，按上表拼出声明路径
        var declared = DeclaredPaths(metricsType);

        // 4) 比对的是【路径集合】，不是"名字集合"
        Assert.Equal(declared.Order(), actual.Order());
    }
}
```

**为什么比对路径而不是名字**：路径包含层级，所以"本该写进 `classes.dns.sent` 却写进了
`classes.udp`"会立刻红——这就是原稿缺失的那一层深度。

**三个必须同时说清的细节**：

1. **条件写出的字段**（`latency` 臂的 `tcp.*` 只在 `plan.UseTcp` 时写；`latency` 只写非空直方图）
   ——测试用**全填满**的实例，让所有分支都写出；条件本身的正确性由臂的行为测试覆盖，
   不由形状测试覆盖。契约注册表里为每个 kind 登记"哪些字段是条件写出的"。
2. **嵌套值对象**（`MixMetrics.Classes` 是 `MixClassesMetrics`）——`DeclaredPaths` 递归它的
   `ArmKeys` 子树，`FlattenPaths` 递归 JSON 对象。
3. **失败时的报错**必须列出**路径差集**（`declared - actual` 与 `actual - declared`），
   而不是一句"集合不相等"。

**可选升级（不在 P1 的验收内）**：用 source generator 从 `*Metrics` record 的属性生成
`ArmKeys`，让集合真正只剩一份。收益明显，但成本高于 P1 的预算；若 P1 之后维护中发现
"两份真相"仍会漂移，再升级。

**因此「同一统计量只有一种拼写」的准确含义是**：同一统计量在**同一层级下**只有一个叶子名
（`sent` 就是 `sent`），但**不同层级下必须各写各的常量**（`Classes.Dns.Sent` 与
`Classes.Udp.Sent` 是两个不同的路径）。分组前缀（`tcp.` vs `classes.udp.`）允许不同，
因为那表达的是不同的分组机制。

**完整映射表的产出时机**：逐字段映射表在 E1 实施时产出，落盘 `research/contract-rename.md`，
并由「新旧**路径**集合差异 == 该表」这条验收锁定（AC）。**在 E1 产出它之前，本节的映射表只覆盖
`sent`/`sentOk` 一族**——其余字段按"只做一致性检查、不做风格改写"处理，即默认不改。

**已登记的已知不一致对**（E1 按表施工，不必再找）：`sentOk`/`sent`/`udpSent`；
`lossRate`/`udpLossRate`；`arrived`/`udpArrived`；`foreignConnection`/`udpForeignConnection`
（后者在 `MixArm.cs:274` 的每-desktop 数组里也存在）。

### 2.3 强类型值与"写得出/读得回"

每个臂的 metrics 同时有一个**强类型值对象**，harness 内部只传它，不再传字典：

```csharp
public sealed record LossMetrics
{
    public required long Sent { get; init; }
    public required long Arrived { get; init; }
    public double? LossRate { get; init; }
    // ...
}
```

值对象与常量之间由一条**测试**绑定（不是运行时机制）：

```csharp
// tests/WinForward.E2E.Tests/ContractShapeTests.cs
[Fact]
public void LossMetricsWritesExactlyTheDeclaredKeys()
{
    // 用反射枚举 LossMetrics 的公开属性，与 ArmKeys.Loss 的常量集合比对
    // 集合相等 → 新增字段忘记加常量、或加了常量没写出去，都会红
}
```

于是 `BaseArm`（新名 `ControlArm`）的 `ReadCount(gates, "clientSendLoss")` 这类
「把值从字典读回来重新猜类型」的代码**整体消失**：它直接持有子臂的强类型结果对象。
（R4/AC2）

### 2.4 `null` 约定（#13）

**先纠正一个事实**（审核确认）：`JsonValue.Ratio` **自首个提交 `59c3a09` 起就是**
`denominator == 0 ? null`，README:276 与 `:281-283` 已把它写进契约，`client-infrastructure-code-quality-audit.md`
§11 第 5 条把它列为「不要动」。所以**这里没有 bug 可修**——逐点复核 `LossArm.cs:89`、`DnsArm.cs:128`、
`MixArm.cs:200,244`、`LatencyArm.cs:303`、`PersistentArm.cs:207`、`ReliabilityArm.cs:341` 全部走 `Ratio`，
`MixArm.cs:291` 的 `bytesPerPage` 也已有 `_pages == 0 ? null` 分支。

本条的实际动作是**搬家 + 防将来**。设计上钉死：

> **分母为 0 → 发布 JSON `null`；分析器把 `null` 渲染成空格子并注明原因，绝不渲染成 `0`。**

`Contracts` 提供唯一的比率函数 `Rate(numerator, denominator) => denominator == 0 ? null : ...`，
且**禁止**直接写 `(double)a / b`。由测试 + `dotnet format` 的规则检查落地。

---

## 3. 传输接缝（R2）

### 3.1 为什么这是真实接缝而不是假接缝

按 codebase-design 的判据「**one adapter means a hypothetical seam; two adapters means a real one**」：
这里**有两个真实的 adapter**（TCP lane 与 UDP lane），它们今天以 113 行的镜像代码存在，归一化
diff 只差 5 行。抽象不是想象出来的。

### 3.1.1 接缝的**边界**（设计约束，不是细节）

必须先把引擎**不做什么**钉死，否则它会变成一个假装能统一所有臂的浅接口：

> **`LaneEngine<TChannel>` 统一的是「TCP 与 UDP 两个 transport」，不是「所有 UDP 臂」。**

依据来自实测——三个 UDP 臂的**窗口语义根本不同**：

| 臂 | 窗口满时的行为 | 计数器 | 证据 |
|---|---|---|---|
| `DnsArm` | **丢弃**该槽位（`continue`），不延迟也不阻塞 | `_unsent` | `DnsArm.cs:294-298` |
| `LatencyArm` | **延迟**：进 deferred queue，队列满才丢 | `windowOverflow` / `backlogDrops` | `LatencyArm.cs:726-742` |
| `LossArm` / `MixArm` | **停止发送**：pacer 空转到槽位按 W 到期释放 | `windowOverflow` | `LossArm.cs:18-20`、`UdpReliabilityTracker` 槽位模型 |

把三者塞进一个共享的窗口机制，就是拿接口去抹平**语义**差异——那正是 §0 要防的错误类型。
因此划分是：

- **引擎提供机制**：配速（`Pacer`）、发送（构建 + 发送 + `sendWouldBlock` 观测）、接收调度、
  有界 drain、`scheduleTruncated` 判定，以及**统计骨架**（supplied / sentOk / sendFailures /
  receiveErrors / remoteClosed / protocolErrors）。
- **臂提供策略**：窗口语义（Drop / Defer / Block 三种，作为策略点而非硬编码）、记账模型
  （LAT 用 `UdpLatencyState`；LOSS/MIX 用 `UdpReliabilityTracker`）、分类逻辑、输出字段。

**哪些臂用引擎**：`LatencyArm` 的 TCP 与 UDP 两个 lane（主要目标，113 行镜像在这里）、
`DnsArm` 的 UDP 与 TCP 两相（用配速/发送/接收/drain，窗口取 Drop 策略）。

**哪些臂不用**：`LossArm` 与 `MixArm` 的 UDP——它们跑的是 `UdpReliabilityTracker` 的槽位模型，
与引擎的发送循环不是同一个机制。它们与其余臂共享的是 §3.4 的**回复校验阶梯**，不是发送循环。

**3 份 UDP 回复校验阶梯的重复**（F3）由 §3.4 的 `ReplyClassifier` 解决。**`DnsArm.cs:315` 不并入**——
它用 `DnsWire.TryParseResponse` 并以 transaction id 为键，不碰 `FrameCodec`/`Filler`/`ConnectionId`，
是另一套阶梯。（原 F3 写成"4 份手抄"是把这两套混为一谈，已修正。）

**§3.3 里"请求在窗口满时延迟而不丢弃"这句话只在 `LatencyArm` 的策略下成立**，实现时不要把
它当成引擎的固有行为。

### 3.2 接口与三个未定项（审核指出，必须先钉死）

```csharp
internal interface ILaneChannel : IDisposable
{
    /// 建立通道。TCP: connect（可能失败）；UDP: connect（几乎不失败，但同样受保护——见 #11）。
    ValueTask<LaneOpenResult> OpenAsync(CancellationToken ct);

    /// 构建并发送一个请求帧。返回该次发送的结果（成功 / 内核未同步接受 / 失败）。
    ValueTask<LaneSendResult> SendAsync(uint connectionId, long sequence, long intendedTicks, CancellationToken ct);

    /// 等一个回复帧。TCP 来自 FrameStreamReader；UDP 来自一个数据报。
    ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}
```

**未定项 A：适配器是 `class` 还是 `struct`**（审核发现原稿 `where TChannel : struct` 与
`IDisposable` 不能同时成立）。裁定：**两个适配器都是 `sealed class`**，约束写成
`where TChannel : class, ILaneChannel`。

理由：它们必须持有 `Socket`、`FrameStreamReader`、接收缓冲，并且需要**身份**（谁拥有那个 socket）。
若改成 `struct`，`using var channel = …` 会装箱，而结构体拷贝会让 socket 的所有权被重复释放——
那是比虚调用严重得多的问题。

**未定项 B：去虚化是保证还是期望**。裁定：**是期望，不是硬性能保证**。`class` 约束下 JIT 的
guarded devirtualization 通常能去虚化单态调用点，但**计划不把它写成性能契约**。真正的性能契约是
"零额外分配"与"`ValueTask` 复用手法保持"；若 P3 完成后 `achievedRate`/`sendWouldBlock` 与改前
量级不符，再考虑 `struct` + 显式 `Close()` 的替代形态（那时要另写所有权分析）。

**未定项 C：引擎的 per-lane 状态对象是谁**。审核指出 `internal static class LaneEngine<TChannel>`
不能持有实例状态，而 §3.3 要求它"内含"在途窗口、`DeferredQueue`、收发计数——**那个状态对象才是
引擎真正的接口，原稿没有写它**。裁定：

```csharp
/// 引擎的 per-lane 状态。由策略侧（目前是 LatencyArm）持有并传入，引擎只读写它。
internal sealed class LaneState
{
    // 发送侧
    internal long _supplied, _sentOk, _sendWouldBlock, _sendFailures;
    internal long _windowOverflow, _backlogDrops;
    internal long _inFlight;
    internal bool _scheduleTruncated;
    // 接收侧
    internal long _received, _corrupt, _protocolErrors, _unmatchedReplies, _foreignConnection;
    internal long _outstanding;
    // 每 lane 一份的延迟直方图引用，由策略注入
}
```

**这个类型的形态决定 `LaneEngine` 是深还是浅**：如果它只是把 `UdpLatencyState` 的字段抄一遍，
那引擎就是把复杂度搬了个家。设计上要求 `LaneState` **只装引擎真正读写的字段**，臂特有的东西
（连接样本、连接失败、探针统计）留在策略侧自己的类型里。**P3 结束时以"`LaneState` 的字段集合
== 引擎实际读写的字段集合"为验收**——多一个字段就说明接缝划错了。

### 3.3 共享引擎与性能

发包路径是测量工具自身的敏感路径——client 一旦阻塞就污染样本。因此：

- **泛型特化**：`internal static class LaneEngine<TChannel> where TChannel : class, ILaneChannel`
  （约束与理由见 §3.2 未定项 A/B）。
- **零额外分配**：`FrameBuffer` 预分配并就地写（现状保持）；`LaneSendResult` 是 `readonly struct`；
  `SendAsync` 的 `ValueTask` 复用手法保持（它是 `sendWouldBlock` 的唯一来源，见
  `arms-code-quality-audit.md` §9.6）。
- **`Dedicated.RunOnOwnThreadAsync` 的每 lane 独占线程语义保持**：注释里说明了"异步体首段
  同步完成会让后续 lane 永不启动"，这个性质在引擎里同样成立。

引擎内含（今天散在六个镜像函数里的东西）：

```
配速（Pacer.WaitUntil，含必须随代码搬走的 pragma —— 实测全仓 9 处，
      按 §3.1.1 的引擎范围应搬 4 处并收敛为 1 处；原稿写"3 处"是错的）
在途窗口 + DeferredQueue（注意：这只是 LatencyArm 的策略，见 §3.1.1）
发送统计（supplied / sentOk / sendWouldBlock / sendFailures / windowOverflow / backlogDrops）
接收调度（把解析结果交给策略，**不自持计数** —— 见下方"计数归属"）
drain（offer 循环后仍有界收尾；TCP 与 UDP 同一策略 —— 修 #9）
scheduleTruncated 判定
```

#### 计数归属（审核指出的一处重叠，必须先定）

原稿让引擎"内含接收统计：received / corrupt / protocolErrors / unmatchedReplies / foreignConnection"，
但 `UdpReliabilityTracker` 已经有 `Corrupt`/`UnmatchedReplies`/`ForeignConnection`/`ReceivedDatagrams`
（`UdpReliability.cs:157,165,167`）。同一个统计量又一次有了两个载体——正是 R4/AC3 要消灭的东西。

**裁定**：**引擎只负责调度接收并把解析结果交给策略；所有计数由策略侧持有。**

| 计数 | 归属 | 说明 |
|---|---|---|
| `received` / `corrupt` / `unmatchedReplies` / `foreignConnection` | **策略侧**：`UdpLatencyState`（LAT）或 `UdpReliabilityTracker`（LOSS/MIX） | 引擎不复制一份 |
| `protocolErrors` | **策略侧**：`UdpLatencyState` 已有（`LatencyArm.cs:88-131`）；`UdpReliabilityTracker` **没有对应物** | 若 LOSS/MIX 要发布它，**在 tracker 上新增字段**，不在引擎里另起一套 |
| `sendWouldBlock` / `sendFailures` / `supplied` / `sentOk` | **引擎**（发送是引擎的动作） | 策略通过 `LaneState` 读 |

**验收**：P3 完成后，同一个统计量不得同时出现在 `LaneState` 与策略类型上（用一条测试或人工核对）。

### 3.4 回复校验阶梯（三份合一）

`LossArm.cs:223-260`、`MixArm.cs:722-760`、`LatencyArm.cs:790-805` 是同一套阶梯的三份拷贝，
其中 **`LatencyArm` 漏掉了 `WasSent` 一步**。抽成一个模块。

**落点是 `Client/Lanes/ReplyClassifier.cs`，不是 `Wire/`**：签名里带着 `UdpReliabilityTracker`
（一个 `Client` 类型），放进 `Wire/` 会形成 `Wire → Client` 的反向依赖——今天 `Wire/*` 全部是叶子
命名空间（`rg 'using WinForward\.E2E' Wire/` 无结果），不能破。

**并且它不属于"纯搬移"批次**：给 `LatencyArm` 补 `WasSent` 是**有意的行为修正**（会改变 LAT 的
`unmatchedReplies` 语义——重复应答目前落进 `_unmatchedReplies`，补后会改道），必须在计划里
登记为行为变化，不能混进"行为零变化"的搬移里。

```csharp
// WinForward.E2E/Client/Lanes/ReplyClassifier.cs
internal enum ReplyFault { None, Corrupt, CorruptKnownSequence, ForeignConnection, Unmatched, Undecodable }

internal static ReplyFault Classify(
    ReadOnlySpan<byte> datagram,
    uint expectedConnectionId,
    UdpReliabilityTracker tracker,
    out FrameHeader header,
    out ReadOnlySpan<byte> payload);
```

顺序固定为：`TryDecode` → （失败时）`BadChecksum && TryReadHeader` 决定能否归因到序列号 →
`ConnectionId` → `Filler.Matches` → `WasSent` → 到达。

**注意**：`LatencyArm` 原来少了 `WasSent`，补上会改变它的 `unmatchedReplies` 语义。
这一条是**有意的行为修正**（三臂统一），在 §5 的修复清单里登记。

---

## 4. 文件拆分（R1）

7 个超标文件的目标接缝。**原则**：先找自然接缝（static 纯函数簇、嵌套类提升、分区注释、
第二顶层类型），物理搬移 + 可见性调整，逻辑不动。

**重要**：本表描述的是 **P4 时刻**的目标形态，而 P3（传输接缝）已经先把发送/接收循环
（`SendLoopAsync`/`OfferFrameAsync`/`SendFrameAsync`/`ReceiveLoopAsync` 及 UDP 对应物）
移进了 `Lanes/LaneEngine`。所以拆分表按"P3 之后剩下什么"来切，不要按当前文件内容切——
否则会拆出很快就空掉的文件。

| 现文件 | 有效行 | 拆成（P3 之后） |
|---|---:|---|
| `Arms/LatencyArm.cs` | 792 | `LatencyArm.cs`（`RunAsync` 编排 + `StartLanes` + `Total`/`TightestCeiling` + `RunConnectProbeAsync`）、`LatencyPlan.cs`（`LatencyPlan`/`LatencyTcpState`/`UdpLatencyState`/`LaneStates`/`DeferredQueue`/`MeasurementValidity`）、`LatencyMetricsWriter.cs`（`WriteTcpMetrics`/`WriteUdpMetrics`/`WriteGates`/`WriteNotes` 四个方法） |
| `Arms/MixArm.cs` | 669 | `MixArm.cs`（`RunAsync` + `RunDesktopAsync`）、`MixCounters.cs`（`MixCounters`，含 `MixCountersPerDesktop` 一族）、`MixMetricsWriter.cs`（`WriteMetrics` + 5 个 `Build*Class` + `ClassifyDesktops` + `CountIdleLanes` + `BuildDesktopLanes`）、`MixPageLoop.cs`（`PageLoopAsync`/`PageConnectionAsync`/`PageDnsAsync`/`SendPageDnsQueryAsync`/`WaitForResponseAsync`）、`MixBulkLoop.cs`（`BulkLoopAsync` + `AggregateRateLimiter`） |
| `Arms/ReliabilityArm.cs` | 605 | `ReliabilityArm.cs`（`RunAsync`/`PumpAsync`/`CollectAsync`/`BuildSchedule`/`GreatestCommonDivisor`）、`ReliabilityAttempt.cs`（`AttemptEvidence`/`RunAttemptAsync`/`SendRequestAsync`）、`ReliabilityExchange.cs`（`ExchangeAsync`/`ReceivePhaseAsync`/`Classify`/`ExpectedOutcome`）、`ReliabilityTally.cs`（`ReliabilityTally`/`ModeTally`/`TallyAttempts`/`Zip`）、`ReliabilityMetricsWriter.cs`（`WriteMetrics`/`BuildModeBreakdown`） |
| `Arms/DnsArm.cs` | 487 | `DnsArm.cs`（`RunAsync`/`WriteMetrics`/`BuildQueryTypes`/`Classify`）、`DnsCounters.cs`、`DnsQueryBuilder.cs`（`BuildQuery`/`QueryTypeFor`/`TypeName`）、`DnsUdpPhase.cs`、`DnsTcpPhase.cs` |
| `Client/ResourceSampler.cs` | 465 | `ResourceSampler.cs`（采样循环 + 屏障，约 60 行）、`ProcessCounterSource.cs`（OS 计数器读取，含三段 catch 阶梯合一）、`ResourceSampleWriter.cs`（JSON 形状与错误上报）、`ProcessSample.cs`（每进程聚合模型） |
| `Arms/PersistentArm.cs` | 440 | `PersistentArm.cs`、`PersistentPlan.cs`（`PersistentPlan`/`PersistentSchedule`/`PersistentLink`）、`PersistentExchange.cs`（`PersistentExchange`/`PersistentFrame`/`TryReadEchoAsync`/`TryReadFrameAsync`/`WaitForReadable`/`TryOpenLinkAsync`） |
| `Client/ClientRunner.cs` | 439 | `ClientRunner.cs`（臂循环与生命周期）、`Cli/ClientOptions.cs`、`Cli/ClientOptionsParser.cs`（与 `TargetRunner` 的解析器合一）、`Client/ArmRecordWriter.cs`（`WriteFailure`/`WriteResult`/`WriteArmSummary`）、`Client/RunFileWriter.cs`（`WriteRunFile`/`WriteEnvironment`/`ArmSummary`） |

同批进行的目标形态拆分（来源见 `wire-target-code-quality-audit.md` §8）：

| 现类型 | 拆成 |
|---|---|
| `TcpTargetServer`（4 职责：socket 生命周期 / accept 记账 / TCP 协议状态机 / 账本 JSON） | `TcpTargetServer` + `TcpConnectionProtocol` |
| `UdpEchoServer`（含嵌套 `SourceCensus` 122 行） | `UdpEchoServer` + `SourceCensus.cs` |
| `TargetRunner`（编排 + CLI 解析 + 账本汇总） | `TargetRunner` + `Cli/CommandLine.cs` + 各 server 实现 `ILedgerSection` |
| 三台 server 的重复 | `Sockets.cs`（bind，含 SO_REUSEPORT 处理）、`SocketIo.cs`（`SendAll`/`ReadExact`）、`TcpAcceptLoop.cs`（accept + 连接表 + 修剪 + shutdown） |

---

## 5. 语义修复的落点（R5 + R6 + R7 + R8）

每条修复的**模块归属**与**验证方式**。证据汇总落盘到本任务 `research/`。

### 5.1 `harness-audit.md` §二 13 条（编号 4–16）

| # | 修复 | 落点 | 验证 |
|---|---|---|---|
| 4 | LOSS 在途窗口按「发送时刻 + W」释放，不再等一个永不到来的应答 | `UdpReliabilityTracker`（窗口槽位带到期时刻） | 仿真：0/10/50% 丢包下 `sent == supplied` 且发满全程 |
| 5 | `Classify` 按**实际发送过的序列集合**遍历 | `UdpReliabilityTracker`（发送位图） | 溢出槽位场景下 `never` 不含从未发出的序列 |
| 6 | W 变成显式 plan 参数并如实发布；不再假装自适应 | `Plan/ArmSpec.LossWindowMs` + `PlanFile` 校验 + 各臂发布 | `scripts/plans/` 全部 6 份 + `scripts/plans-short/` 全部 5 份都显式声明；`metrics.window` 等于声明值 |
| 7 | LOSS 从**预定时刻**起算（用 `pacer.IntendedTicks` 建帧） | `LaneEngine` | 人为制造客户端阻塞，`late`/`never` 反映阻塞 |
| 8 | LAT/LATLOAD 的 `window` 放大到覆盖预期尾部；`windowOverflow > 0` 输出 `measurement-caveat` 且该臂延迟格子 `n/a`（**已由 DD D9 取代"使该臂作废的 gate"的旧措辞**） | plan 默认值 + `LaneEngine` 的 gate 输出 | LATLOAD 在 500 rps 下 `windowOverflow == 0` |
| 9 | TCP 延迟通道也有界 drain | `LaneEngine`（TCP/UDP 同一策略） | TCP 直方图样本数较修复前上升 |
| 10 | `reordered` 按「已发送的最高序号」比较（实现而非删除） | `UdpReliabilityTracker` | 穷举 1..6 到达顺序，`(1,3,2)` 的重排计数为 1（今天恒为 0） |
| 11 | 三个恒零计数器：`unmatchedReplies` 在 LOSS 接通、`abandonedAtTeardown` 在 LOSS 接通、`corruptRate` 的去程损坏由 target 侧报回（账本新增 `undecodable` 计数并在分析器披露） | `LossArm` + `UdpEchoServer` + `Analysis` | 逐条给出「接通」或「字段删除」的二选一，不留无法移动的计数器 |
| 12 | **四个臂**硬编码 `clientSendLoss = 0` 改为由各自计数器派生：`IdleArm`/`DnsArm`/`ThroughputArm`/`ReliabilityArm` | 四处命中点见 `prd.md` 的语义缺陷表 #12 | 每个臂的 gate 都能真的失败 |
| 13 | **前提不成立，已重新定义**：`Ratio` 自首个提交起就是 `denominator == 0 ? null`，没有 bug 可修。实际动作是**搬家 + 防将来** | `Ratio` 搬进 `Contracts` 作为唯一下口 + 禁止裸 `(double)a / b` 的规则检查 | **证明不存在绕过 `Ratio` 的比率计算**（不是"把 0 改成 null"——那拿不出证据） |
| 14 | 采样记录带 PID + `StartTime`，逐样本校验身份；计数器非单调即拒收该格 | `ResourceSampler` + `ProcessSample` | 仿真一次产品重启：CPU 不再变成负数 |
| 15 | DNS 的 TCP 队列存 `(id, intended)`，出队按 id 比对 | `DnsArm` | 乱序/错配响应不再记成 `answered` |
| 16 | BASE 控制组：参数来自 plan；每轮产品块**前后各跑一次**；下限值与 LOSS 数字并列发布 | `ControlArm` + `orchestrator.ps1`（仅计划顺序）+ `Analysis` | 控制行的位置与参数可复核 |

### 5.2 `harness-audit.md` §三 公平性 3 条（R6）

| # | 修复 | 落点 |
|---|---|---|
| 17 | Proxifier 的 UDP 行标为 `not carried (UDP bypassed)`，排除出 UDP 精度与 DNS 延迟的成对比较 | `Analysis/Model/RowProfile`（机制已存在，落实标注与排除规则） |
| 18 | WinForward 的 DNS 走 `localTarget` 直连：DNS 延迟栏在表中显式标注路径差异，不与其他产品并列 | `Analysis/Tables` 的 DNS 表说明 + `RowProfile` |
| 19 | CPU 口径：报告中显式声明"仅用户态、不含驱动/DPC/ISR/非分页池"，并禁止把它当成用户态效率排名 | `Analysis/Tables/Cpu` 的表标题与脚注 + 分析器 README |

### 5.3 代码审查新发现（R7 + R8）

| 发现 | 修复 | 落点 |
|---|---|---|
| `UdpReliabilityTracker` 真实数据竞态 | 明确并发契约：**发送线程独占 book**，接收线程只投递到无锁队列由发送线程消费；或全部改为 `Interlocked`/单写者模型。二选一并在类文档写清 | `UdpReliability.cs` + `LaneEngine` |
| plan key 完全静默 | `PlanFile` 加 kind↔key 白名单：未知 key 让 `TryLoad` 失败并指出 arm 与 key | `Plan/PlanFile.cs` |
| **至少 5 处** UDP `ConnectAsync` 裸奔（原稿写 4 个臂，漏了 `MixArm.cs:457` 的 page DNS connect） | 移进 `LaneEngine.OpenAsync`，与 TCP 同策略（失败 → 计数器 + 臂继续/标记） | `Lanes/LaneEngine.cs` |
| **D7 越界守卫（Tier 0，E1）** | 边界检查提到 `MarkSent` 开头，但**必须仍然调用 `_sent.TrySet(sequence)`** 由它维护 `OutOfRange` 计数——若在它之前直接 return，`outOfRangeSequences` 会恒为 0，把一条已声明的披露信号关掉。负序号走同一条路径，同一个守卫能一并覆盖 | `UdpReliability.cs:196-209` |
| **D7 记账半（E3）** | 越界槽既不在 `sent` 里也不在任何桶里 → 定明它在 `sent`/`supplied`/`clientSendLoss` 之间的归属 | `UdpReliability.cs` + `LossArm` |
| **账本新增三个字段（E3）** | `detail`（Error 的原因：异常类型名，让「停机取消」与「真错误」可区分）· `acceptErrors`（accept 循环的静默失败计数）· `udpReceivers`（生效的 UDP 接收并发度，写进 `targetSummary`）。**只新增不改名**；新增前用 synthetic 数据集验证分析器输出不变 | `Target/*.cs` + `LedgerWriter` |
| `ObjectDisposedException` 四种策略 | 统一为「teardown 不产生数据点」：良性 teardown 一律不计数、不进样本分类 | 全部 catch 点 + `LaneEngine` |
| `achievedRate` 两种口径 | 统一为「成功**发出**的请求/秒」，PERSIST 另用 `completionRate` 命名其完成口径 | 各臂 + `ArmKeys` |
| `TcpCommand.Name` 兜底造重复 key | 去掉两个 `_ =>` 兜底改为 `throw`；加"枚举成员名互不相同"测试 | `Wire/TcpCommand.cs` + 测试 |
| `LedgerWriter` 的 catch 包住 body | `body(writer)` 移出 try | `Target/LedgerWriter.cs` |
| `ReuseAddress` 顺带打开 SO_REUSEPORT | `Sockets.cs` 统一 bind：Unix 上显式清零 SO_REUSEPORT，使残留实例**响亮地** `EADDRINUSE` | `Target/Sockets.cs` |
| `FrameStreamReader` EOF 丢半帧（**批次归属：E3，不是 E2**） | 新增 `FrameReadStatus.Truncated`；两个 server 各自记账。这是**有意的新增 verdict 路径**，与 E2 的「行为等价」目标冲突，必须落在 E3 并配一条 selftest 误报检查 | `Wire/FrameStreamReader.cs` |
| `MaxPayloadLength` 只在解码侧校验 | 提为 `internal`，`PlanFile` 用它校验 plan | `Wire/FrameCodec.cs` + `Plan/PlanFile.cs` |
| 端口冲突校验不全 | 显式拒绝 `dnsPort ∈ {tcpPort, udpPort}` | `Target/TargetOptions.cs` |

---

## 6. 怎么证明改对了（R7 的验证策略）

没有可信基线，且语义修复会**故意**改变数值。因此分四层 + 一层零：

### 层零：归一化回归基线（审核指出原稿把它丢了）

两份代码审查都建议"动手前先建回归基线"，原稿写了"采纳其精神但改变期望"，然后**在四层里
把它丢了**——没有冻结步骤、没有比对脚本。后果是：E1 同时改 key 集合与 Tier 0 行为、E2 改接缝、
E3 改数值，全程没有一条"每一处变化都要能解释"的机器判据；唯一剩下的客观判据是 E4 的 oracle，
而它只覆盖 synthetic 树，**真实 selftest 记录上没有任何等价性判据**。

**补回来**：E1 的步骤 0.0 先跑一次 `selftest` 存 `/tmp/base`，落一份归一化比对脚本
（配方：`client-infrastructure-code-quality-audit.md` §10.5 —— 归一化
`startedTicks`/`endedTicks`/`ticks`/`*Utc`/`wallSeconds`/`*Us`，**键顺序也要归一化**，
否则"只改写入顺序"的重构会产生假差异）。

**判据不是"必须逐字不变"，而是"每一处变化都能在改名表或语义清单里找到出处"**——
这正是语义修复会故意改数值时唯一可用的判据。

### 层一：单元测试（新增，覆盖纯逻辑）

`tests/WinForward.E2E.Tests` + `InternalsVisibleTo`（仓库惯例：`src/WinForward.Core/WinForward.Core.csproj:8-9`）。
工程要设 `<IsTestProject>true</IsTestProject>`，否则会吃到 `Directory.Build.props:14` 的 4 个分析器包。

优先序（全部无网络、无时钟依赖）：

1. `Crc32C` —— 黄金向量（`"123456789"` → `0xE3069283`、空串 → `0`）+ **表路径与硬件路径一致**
2. `Filler` —— `Fill`/`Matches` 对称 + 固定 `(conn,seq)` 的前 16 字节黄金向量 +
   **固化高 32 位截断事实**（防止有人"顺手修"）
3. `FrameCodec` —— 编解码往返 + 四种错误各一条 + **固化"尾部多余字节被接受"**
4. `DnsWire` —— 查询/响应往返 + 压缩指针 QNAME + 超长名 + 5 种 queryType 的 `answerCount`
5. `TcpCommand` —— **所有枚举成员的 `Name` 互不相同**（封住重复 JSON key）
6. `LedgerWriter` —— 格式快照（两行、`utc`/`label` 在前、`\n` 结尾、字段集合）
7. `FrameStreamReader` —— 一帧拆成三段喂入；EOF 落在帧中间 → `Truncated`
8. `UdpReliabilityTracker` —— 序列记账的恒等式、`OutOfRange` 上界、
   **`MarkSent(MaxSequence + 1)` 不抛且 `OutOfRange == 1`（D7 的回归断言）**、
   **并发契约由并发测试证明**、D7 记账半的口径
9. `LogHistogram` —— 分桶、百分位、天花板饱和语义；`Record(long.MaxValue)` 不抛
10. `PlanFile` —— 白名单拒绝未知 key、**`scripts/plans/` 全部 6 份 + `scripts/plans-short/` 全部 5 份**
    都能通过（原稿写"5 份 shipped plan"含义不明：`plans/` 有 6 份、`plans-short/` 有 5 份）
11. 契约形状 —— §2.2 的**路径集合**比对（不是名字集合）

### 层二：语义修复的定向证据 —— **写成可执行测试，不是落盘的文本**

审核指出：#4 的丢包仿真、#5 的溢出槽位场景、#10 的 1..6 穷举、#14 的重启仿真**全部是纯逻辑**，
本来就满足 R11 的"不依赖网络与真实时钟"，而 `UdpReliability.cs` 今天唯一缺的就是测试工程。
写成 Markdown 落盘后它们是一次性的，不会成为回归门禁。

**因此**：每条修复的定向证据**直接写成 `tests/WinForward.E2E.Tests` 里的用例**（命名带上缺陷编号，
例如 `SemanticFix_004_LossWindowReleasesWithoutArrival`），`research/semantic-fixes/` 只放
"测试名 → 缺陷编号 → 结论"的索引。每条索引必须包含**一条可重跑的命令**
（`dotnet test --filter SemanticFix_004`）。

### 层三：分析器的双向 oracle

分析器重写是本任务最大的单项工作，必须有客观判据：

```
新契约 synthetic 树  ──┬─→  Python 参考实现（仅改字段名，分析逻辑不动）─→ A
                       └─→  C# 分析器                                  ─→ B
                                                                      断言 A == B
```

可行性依据：
- `synthetic/make_tree.py` 已经能造出含全部边界情况的树（`foreignConnection`、身份恒等式违规、
  零车道见证、采样器报错、`scheduledAttempts` 不匹配、控制块漂移、逐 pass 账本……）
- 因为顶层骨架不变（§0），Python 版只需把 `dig(record, "metrics/sent")` 这类路径换成新字段名，
  **统计、判定、渲染逻辑一行不改** —— 这正是要对比的部分
- 对比通过后 Python 版退休（**V3 已定：立即删除**，依赖 git 历史保留）。顺带决定 `make_tree.py`
  的归属——它同样住在结果目录里

**分批**（用户已确认）：E4 按**表格分组**分 5 批，每批跑一次 oracle `diff`——它最贴近"完成"的定义，
且若某批卡住，前几批的交付已经有价值（四张核心表覆盖绝大多数结论）。

**判据是"该批负责的表格小节"，不是整个文件**（审核发现原稿的"每批 diff 为空"不可能成立：
`tables.md` 与 `verdict.json` 都是单文件，只要还有一张表没实现，整文件 `diff` 就非空，
于是批次 1–4 的判据永远不成立、"进度可度量"这条风险缓解是空的）。

| 批次 | 内容 | 判据 |
|---|---|---|
| 1 | `Loading` + `Model` + `Stats` + 环境/可用性表 | 这两节的**分段 diff** 为空；未实现小节在输出里显式写 `TODO` 占位 |
| 2 | 不变量校验 + findings 分级 + gates 表 | 同上 |
| 3 | **headline / latency / udp / dns（四张核心表）** | 同上（此批完成即覆盖绝大多数结论） |
| 4 | cpu / memory / persist / tcp | 同上 |
| 5 | dual / control / ledger / verdict.json | **全量 `diff` 为空 = AC9 达成** |

实现上给 C# 版加 `--tables <list>` 只渲染该批的表，让 `diff` 只比对应切片。

**oracle 的两个提前量**（审核指出，原稿漏了）：
1. **Python 侧的改动清单必须落盘**：不只是 `analyze.py`（6053 行），还有
   **`synthetic/make_tree.py`（1210 行，内嵌全部 harness schema）**。没有这份清单，
   "只改字段名、逻辑一行不动"这个前提无法被检验。
2. **把生成结果冻结成数据**（golden tree），oracle 只对冻结数据跑。否则"两侧跑的是同一棵树"
   依赖于生成器在那次对比中没被动过——而 E4 恰恰要改这个生成器，这是自指风险。

**`verdict.json` 的 `generated_by` 是块石头**：`analyze.py:5512` 硬编码
`benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py`，而 V3 要删掉那个文件。
二选一：(a) C# 版原样输出该字符串并注明是历史标签；(b) 两侧一起改中性值并在同一提交里重新冻结 `A`。

### 层四：Windows 轻量验证（总验收时一次）

经一条产品的短链路：VM 上按 `AGENTS.local.md` §5 已部署的 `wf-aot` + sing-box，
Windows client → 经透明代理 → Linux target，用 `plans-short/` 的短 plan 跑几分钟。

选它而不是"跨机裸跑 selftest"：裸跑只验证跨机连通，而经产品链路才验证 **E3 的语义修复在真实
透明代理下的行为**。不需要跑完整 campaign，也不需要跑齐五个产品（那是后续任务）。

**端到端门禁的层次**：`scripts/selftest.sh`（Linux 单机）是每次改动的常规门禁；
Windows 轻量验证是 E5 总验收的一次性动作。

---

## 7. 实施顺序

顺序由「依赖」与「风险」共同决定：先做**低风险高收益**的（测试设施、1 行修复），
再做**结构搬移**，最后做**改变数值**的语义修复与分析器重写。

| 阶段 | 内容 | 依赖 | 风险 |
|---|---|---|---|
| **P-1 Tier 0 止血** | D1–D7 与 `selftest.sh` 的问题先让它**不崩、不静默**：`MarkSent` 边界检查、顶层兜底、加载期校验、文件名单射与长度、空 `--plan` 拒绝、空 `--sampler-process` 拒绝。约 50 行 + 6 个最小 plan 作回归用例 | — | 低（都是把静默失败变响亮），但**每条独立 commit** |
| **P0 安全网** | 建 `WinForward.E2E.Tests` + `InternalsVisibleTo`；写 §6 层一 的 1/2/3/5/6 号测试（纯函数）；把 P-1 的最小 plan 收成用例 | P-1 | 极低 |
| **P1 契约** | `WinForward.E2E.Contracts` 骨架：`ArmKeys` + 值对象 + `Rate` + `JsonlSink`；harness 接入并写出（**同时执行 R4 改名与 #13**） | P0 | 中（改名牵动分析器） |
| **P2 零散修复** | `TcpCommand` 兜底、`LedgerWriter` catch 范围、`Sockets.cs`（SO_REUSEPORT）、端口校验、plan 白名单、`MaxPayloadLength`、`JsonValue.Write` 的 `default:` 改抛 | P1 | 低 |
| **P3 传输接缝** | `Lanes/`（`ILaneChannel` + **两个** adapter + `LaneEngine`）+ `ReplyClassifier`。**范围严格按 §3.1.1**：`LatencyArm` 的 TCP/UDP 两个 lane 是主目标；`DnsArm` 两相复用引擎的配速/发送/有界 drain 三项（窗口取 Drop 策略），若要并入必须明确它是**第三个 adapter**（线格式是长度前缀而非 `FrameCodec`）；`LossArm`/`MixArm` **不走引擎**，它们只共享 `ReplyClassifier` | P2 | **中高**（热点路径，需 selftest + 定向证据） |
| **P4 结构拆分** | §4 的 7 个文件 + Target 侧拆分（`SourceCensus`/`TcpConnectionProtocol`/`TcpAcceptLoop`/`SocketIo`/`ILedgerSection`） | P3 | 中 |
| **P5 语义修复** | §5.1 的 13 条（#13 已重新定义为「搬家 + 禁裸除法」，随 P1 完成）+ §5.3 的剩余项：竞态契约、`ObjectDisposedException` 统一、`achievedRate` 统一、**D7 的记账半**、**`FrameReadStatus.Truncated`**、**账本三字段 `detail`/`acceptErrors`/`udpReceivers`**。提交按 §6 的提交图分组，不机械地一条一 commit | P4 | **高**（改变数值） |
| **P6 分析器** | `WinForward.E2E.Analysis` 重写，按表格分 5 批（§6 层三）；同步升级 `make_tree.py` 与 Python 参考实现的字段名；逐批 oracle 对比 | P5 | 高（工作量最大） |
| **P7 文档** | harness README 契约表 + Non-obvious properties + Verification；分析器 README（含 §四 7 条口径披露） | P6 | 低 |
| **P8 Windows 轻量验证** | 经一条产品的短链路（`wf-aot` + sing-box + `plans-short/`）在 VM 上跑几分钟 | P7 | 中（占用开发机） |

**为什么语义修复排在结构重构之后**：语义修复要改的是逻辑，结构没理清之前改逻辑会在
六个镜像函数里各改一遍；接缝抽好之后，多数修复只落在 `LaneEngine` 与 `UdpReliabilityTracker`
两处。

**为什么契约排在最前（P1）**：它是分析器重写的前置，且改名会牵动所有臂——越晚做返工越多。

---

## 8. 风险与回退

| 风险 | 缓解 |
|---|---|
| 契约改名漏掉某处 → 分析器静默 `n/a` | §2.3 的形状测试 + `ArmKeys` 单一定义点；分析器的 oracle 对比会暴露缺字段 |
| 传输接缝引入开销，污染测量 | 泛型特化（去虚化）+ 零分配；`LaneSendResult` 为 struct；改完后对比 `achievedRate`/`sendWouldBlock` 与修复前的量级 |
| 语义修复改变数值，无法判断"改对了"还是"改坏了" | 每条修复**先写定向证据再改代码**；证据落盘 `research/semantic-fixes/` |
| `FrameStreamReader` 新增 `Truncated` 会新增一条 verdict 路径 | 单独一个阶段做，selftest 确认无误报 |
| 分析器重写工作量超预期 | 双向 oracle 让进度可度量：按"表格数量"分批，每批对比一次 |
| 绘图后置导致报告缺图 | C# 分析器输出 `plots/SKIPPED.md` 等价声明；绘图是本任务范围外（Q2 待定） |

**回退点**：P1/P3/P5 各是一个天然的批次边界——每个阶段结束时 `selftest` 绿 + 测试绿 +
零警告门禁过，才进入下一阶段。任一阶段失败可整体回退到上一个批次边界（工作提交按阶段分开）。
