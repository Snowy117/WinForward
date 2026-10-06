# Arms 代码质量审查

审查范围：`benchmarks/WinForward.E2E/Client/Arms/`（10 个文件，**4364 行**，41 个类型），
以及它们直接依赖的 `Client/ArmContext.cs`、`Client/UdpReliability.cs`、`Client/FrameBuffer.cs`、
`Client/PlanFile.cs`。

本文**不重复** `harness-audit.md` 的语义正确性问题（计分口径、样本丢失、门限不严、公平性）。
本文只谈代码质量：重复、抽象、体量、命名、状态、参数、死代码、错误处理。

所有结论都在当前工作区（单提交 `59c3a09`，工作区干净）上核对过，引用格式为 `文件:行号`。

---

## 概览

这套代码的**意图密度**远高于普通代码库：59 条诊断 note、跨线程计数器的归属注释、每个
pragma 都写了为什么。问题不在"没想清楚"，而在**想清楚之后没有把同一条规则收到一处**：
同一个发送循环写了 TCP 版和 UDP 版、同一段数据报分类阶梯写了三遍、同一个 plan key 的默认值
在三个地方各写一份，于是"改一个漏一个"的形状已经成型（`LossArm.DefaultRatePerSecond = 500`
与 `BaseArm.DefaultLossRatePerSecond = 500` 就是同一个数字的两份拷贝）。

最严重的三类问题：

1. **重复**：`LatencyArm` 的 TCP/UDP 发送三件套共 113 行，归一化 diff 后只差 5 行；
   UDP 分类阶梯三份（`LossArm` / `MixArm` / `LatencyArm`）；"1 ms 轮询等到空或到点"五份。
2. **跨臂契约靠字符串维系**：`BaseArm.cs:49-56` 用字符串键从子臂的 `Gates` 字典里捞数，
   键名写错就静默发布 0；`ArmDispatch` 的 kind 列表与 `PlanFile.s_knownKinds` 是两份必须手工同步的清单。
3. **同一个概念在不同臂里是不同的东西**：`achievedRate` 在 PERSIST 是"完成的往返/秒"、在其余五臂
   是"发出的请求/秒"；`clientSendLoss`、`ObjectDisposedException`、接收侧 `SocketException`
   各自有 2～4 种互相矛盾的处理策略。

另外两处需要单独点名：

- **`UdpReliabilityTracker` 没有并发契约，而 `MixArm` 在两个线程上同时用它**（发送循环 +
  并发接收循环），全部计数与位图读改写无保护。这是本次审查发现的唯一一处**真实数据竞态**。
- **`BaseArm` 这个类名与实际语义不符**：它不是所有 arm 的基类，而是 `kind: "base"` 这个
  无产品控制臂的执行体。

规模补充：Arms 中 15.4% 的非空白字符是字符串字面量（`LossArm` 28%、`BaseArm` 28%），
最长单行 659 字符（`MixArm.cs:166`）。这些是输出契约，重构时按第 9 节处理。

---

## 1. 重复代码

### 1.1 `LatencyArm` 的 TCP/UDP 发送三件套（最严重的一处）

- 位置：
  - `LatencyArm.cs:442-485` `SendLoopAsync` ↔ `LatencyArm.cs:673-714` `SendUdpLoopAsync`
  - `LatencyArm.cs:487-517` `OfferFrameAsync` ↔ `LatencyArm.cs:716-742` `OfferDatagramAsync`
  - `LatencyArm.cs:519-549` `SendFrameAsync` ↔ `LatencyArm.cs:744-774` `SendDatagramAsync`
- 重复份数：2 份 × 3 个方法，合计 **113 行**
- 差异：我把 `ConcurrentQueue<long>`/`ConcurrentDictionary<ulong,long>`、`LatencyTcpState`/`UdpLatencyState`、
  `connectionId`/`UdpConnectionId` 归一化之后做了逐行 diff：

  | 方法对 | 归一化后行数 | 差异行数 | 差异内容 |
  | --- | --- | --- | --- |
  | `SendLoopAsync` / `SendUdpLoopAsync` | 38 / 37 | 3 | 多一个 `uint connectionId` 形参并向下透传 |
  | `OfferFrameAsync` / `OfferDatagramAsync` | 26 / 25 | 5 | 同上（纯形参） |
  | `SendFrameAsync` / `SendDatagramAsync` | 28 / 27 | 3 | 形参 + `pending.Enqueue(x)` vs `pending[seq] = x` |

  唯一的非形参差异是 `catch (SocketException)` 的注释：TCP 版写"一次请求失败，调度继续"，UDP 版写
  "connected UDP socket 会把上一个包的 ICMP 错误在本次 send 抛出"。**注释不同，行为相同。**
  这是本质冗余，不是本质差异。
- 建议的抽象：

  ```csharp
  // 发送侧对待配对集合只做两件事：登记一个 (sequence, intendedTicks)，以及按语义取回它。
  // TCP 忽略 sequence（FIFO 队列），UDP 用 sequence 作键 —— 这正是两者唯一的差别。
  internal interface IPendingReplies
  {
      void Register(ulong sequence, long intendedTicks);
      bool TryTake(ulong sequence, out long intendedTicks);
      bool IsEmpty { get; }
      int Count { get; }
  }
  ```

  然后 `SendLoopAsync<TPending>(..., uint connectionId, TPending pending, ...)`、
  `OfferAsync<TPending>`、`SendOneAsync<TPending>` 各一份。`where TPending : class, IPendingReplies`
  的类约束可以避免泛型装箱；`_inFlight`/`_sentOk` 等计数器改用 §1.5 的公共基类承载。
- 风险：**中**。这是热点路径（500 rps 的 LATLOAD、20 cps × 30 s 的 MIX bulk 都会走它），
  必须实测指标不变。特别地：`send.IsCompleted` 的 ValueTask 复用手法（见 §9.6）与
  `frame.Memory[..length]` 的原地切片必须原样保留。
- 收益：消除 113 行重复，并让 TCP/UDP 两侧以后不可能再漂移（今天的 UDP 版注释已经和 TCP 版
  讲了不同的故事）。

### 1.2 UDP 数据报分类阶梯（三份，账目目标各不相同）

- 位置：
  - `LossArm.cs:230-263`（`DrainAsync` 内联展开）
  - `MixArm.cs:698-707`（`BookUndecodable`）+ `MixArm.cs:734-763`（`ReceiveUdpLoopAsync`）
  - `LatencyArm.cs:790-819`（`ReceiveUdpLoopAsync`）
- 重复份数：**3**
- 差异：判定顺序三处完全一致（`TryDecode` → `header.ConnectionId != 期望` → `!Filler.Matches` →
  `!WasSent` → `MarkArrival`）。
  - `LossArm` 与 `MixArm` 是**同构**的：`MixArm` 把"坏包能否读出 sequence"抽成了 `BookUndecodable`，
    `LossArm.cs:232-239` 把同样 4 行内联在 `DrainAsync` 里。两份逻辑逐字相同。
  - `LatencyArm` 版有**本质**差异：坏包一律 `_corrupt++`（不区分"能读出 header 的坏包"，
    丢掉了 sequence 归属），且没有 `WasSent` 分支（延迟臂不追踪已发送集合）。
- 建议的抽象：把 `BookUndecodable` 提升为 `UdpReliabilityTracker` 的静态方法（或新建
  `UdpFrameClassifier`），`LossArm` 改调；`LatencyArm` 只复用其中"坏包是否可读出 sequence"
  的那一行判断，保留自己的计数器语义。

  ```csharp
  // 返回 true 表示 sequence 已知（调用方决定记到哪个计数器）
  internal static bool TryBookUndecodable(
      UdpReliabilityTracker tracker, ReadOnlySpan<byte> datagram, FrameDecodeError error);
  ```
- 风险：**低**（`LossArm`/`MixArm` 逐字相同）。`LatencyArm` 侧只搬一行判断。
- 附带说明：`MixArm` 比 `LossArm` 多一步 `pending.TryRemove(header.Sequence, out var intended)`
  用于记录 DNS/UDP RTT（`MixArm.cs:758-761`），这是真实差异，保留。

### 1.3 "1 ms 轮询直到空或到点"的 drain（5～6 份）

- 位置与份数：

  | 位置 | 形态 |
  | --- | --- |
  | `LatencyArm.cs:836-850` / `852-866` | 两个 overload，**归一化 diff 完全相同（14 行 ×2）**，只差集合类型 |
  | `LossArm.cs:189-216` | 循环体里夹带 `DrainAsync` 调用 + 三个 catch |
  | `MixArm.cs:709-715` | `while (!pending.IsEmpty && Clock.Now < drainUntilTicks) Task.Delay(1)` |
  | `DnsArm.cs:243-254` | 同上，加 try/catch |
  | `DnsArm.cs:454-458` | 同上，无 try/catch |

- 差异：只有三处 —— ①"还欠什么"的谓词；② 有没有 `!receive.IsCompleted` 这个附加条件；
  ③ 有没有吞取消异常。核心循环都是 `while (stillPending() && Clock.Now < until) await Task.Delay(1, ct);`
- 建议的抽象：

  ```csharp
  internal static class DrainWait
  {
      // until 是绝对 tick；extraCondition 供 latency 臂附加 "接收任务未完成"。
      internal static Task UntilAsync(Func<bool> pending, long untilTicks, CancellationToken ct,
                                      Func<bool>? extraCondition = null, bool swallowCancellation = true);
  }
  ```
- 风险：**低**。`GraceDrainAsync` 两个 overload 的归并是零风险纯重复消除（委托分配只发生在
  drain 阶段，不在每包路径上）。
- **不要动**：`Task.Delay(1)` 的 1 ms 粒度是刻意的，见 §9.2。

### 1.4 参数默认值的三元表达式（9 份）与重复的常量

- 位置与份数：
  - `LatencyArm.cs:876-879`（`LatencyPlan`：Rate / PayloadBytes / InFlightWindow / Lanes，4 个）
    —— 这份是**好的**（集中在一个 struct 里，还带 `WriteParameters`）
  - `LossArm.cs:16-19`（4 个）
  - `ThroughputArm.cs:84-85`（2 个）
  - `ReliabilityArm.cs:133-135`（3 个）
  - `DnsArm.cs:51-53`（3 个，另加 `Math.Clamp`/`Math.Max` 守卫）
  - `PersistentArm.cs:141-143`（3 个，另加 `:147` 的派生死线）
  - `MixArm.cs:117-118`（2 个）
  - `BaseArm.cs:80-81`、`92-93`（4 个，作用于两个子 phase）
- 重复的常量（同一个数字写了两遍，最危险的部分）：
  - `LossArm.cs:8` `DefaultRatePerSecond = 500` ↔ `BaseArm.cs:11` `DefaultLossRatePerSecond = 500`
  - `LossArm.cs:9` `DefaultPayloadBytes = 200` ↔ `BaseArm.cs:12` `DefaultLossPayloadBytes = 200`
  - `LatencyArm.cs:150` `DefaultRatePerSecond = 20` ↔ `BaseArm.cs:9` `DefaultLatencyRatePerSecond = 20`
  - `LatencyArm.cs:151` `DefaultPayloadBytes = 120` ↔ `BaseArm.cs:10` `DefaultLatencyPayloadBytes = 120`
  - 而 `BaseArm.cs:73-74` 的注释还明确声称"两个 phase 各自保留默认负载" —— 也就是说这四对常量
    **必须**保持相等，却没有任何机制保证。
- 差异：语义相同（"plan 未声明则取默认"），但守卫风格四分五裂：`DnsArm` 用 `Math.Clamp`，
  `PersistentArm` 用 `Math.Max` + `(uint)` 转换，其余是裸三元。**没有任何 arm 拒绝非法值。**
- 建议的抽象：新增 `Client/Arms/ArmDefaults.cs`，把默认值与"未声明"规则收到一处：

  ```csharp
  internal static class ArmDefaults
  {
      internal const int LatencyRatePerSecond = 20;
      internal const int LatencyPayloadBytes = 120;
      internal const int LatencyInFlightWindow = 64;
      internal const int LossRatePerSecond = 500;
      internal const int LossPayloadBytes = 200;
      internal const int LossInFlightWindow = 4096;
      internal const int DnsUdpWindow = 256;
      internal const int DnsTcpWindow = 64;
      // …
  }
  ```

  并让 `LatencyPlan`（`LatencyArm.cs:870-918`）与对应的 `LossPlan`/`DnsPlan` 结构体成为唯一的
  取值点，`BaseArm` 直接从子臂的 plan 拿默认值，不再自己复制一份。
- 风险：**低**（纯提取）。唯一前置条件是先定案 §6.1 的 `window` 双默认值。

### 1.5 计数器字段清单（14 字段 ×2 + 一份 `AddFrom`）

- 位置：`LatencyArm.cs:10-86` `LatencyTcpState` ↔ `LatencyArm.cs:88-132` `UdpLatencyState`
- 重复份数：**2**（外加 `AddFrom` 一份 20 行，`LatencyArm.cs:66-85`）
- 差异：TCP 多 `_connectSamples/_connectFailures/_connectTicks/_remoteClosed`；UDP 多 `_foreignConnection`。
  其余 **14 个字段**（`_started/_supplied/_sentOk/_sendWouldBlock/_windowOverflow/_backlogDrops/
  _sendFailures/_received/_corrupt/_protocolErrors/_unmatchedReplies/_inFlight/_outstanding/
  _scheduleTruncated`）与 `ClientSendLoss` 表达式（`:64` vs `:131`）**逐字相同**。
  `AddFrom` 正确地没有聚合 `_inFlight`（它是活体 gauge）。
- 建议的抽象：

  ```csharp
  internal class LatencyCounters            // 14 个共有字段 + ClientSendLoss + AddFrom(共有部分)
  internal sealed class LatencyTcpState  : LatencyCounters   // + connect 组
  internal sealed class UdpLatencyState  : LatencyCounters   // + _foreignConnection
  ```
- 风险：**低-中**。字段继续用可写 `internal long`（不要改成属性，热点路径没必要）。`Total()` 必须
  仍然只在 `Task.WhenAll` 之后调用（`LatencyArm.cs:182-185`），这一点不变。

### 1.6 每个 arm 的 `RunAsync` 骨架

- 计数（全 Arms 范围）：
  `var startTicks = Clock.Now;` ×9、`context.DeadlineTicks(startTicks)` ×7、
  `using var linked = context.CreateLinkedTokenSource();` ×7、`var cancellationToken = linked.Token;` ×6、
  `await Task.WhenAll(...)` ×7、`new ArmOutcome { Parameters = { ["seconds"] = … } }` ×9。
- 差异：骨架相似，但**生命周期并不相同** —— `PersistentArm.cs:240` 的 `startTicks` 是"首次连接
  成功之后"才取的，`IdleArm.cs:17` 干脆不用 deadline，`BaseArm` 完全委托给子臂。
- 建议的抽象：**只提取取值**，不要做模板方法：

  ```csharp
  internal readonly struct ArmRun
  {
      internal ArmRun(ArmContext context);   // 记 StartTicks、建 linked CTS、Token
      internal long StartTicks { get; }
      internal CancellationToken Token { get; }
      internal long DeadlineTicks { get; }   // 只有需要的臂才读（Persistent/Idle 不读）
  }
  ```
- 风险：**低**。
- 反面建议（明确写下来，防止后来者做错）：**不要**引入
  `abstract class ArmBase { protected abstract Task<ArmOutcome> RunCoreAsync(); }`。理由见 §2.4。

### 1.7 六份 `FrameReadStatus` → 计数器的映射

- 位置：`LatencyArm.cs:563-590`、`ThroughputArm.cs:262-281`、`ReliabilityArm.cs:660-689`、
  `MixArm.cs:422-427`、`MixArm.cs:599-604`、`PersistentArm.cs:427-458`
- 重复份数：**6**（3 份带 `default:` 的 switch，3 份 `status != Frame` 的 if-chain）
- 差异：每份对 `BadChecksum`/`BadMagic`/`BadLength` 的处置与"继续还是结束"确实不同 —— 这是各臂
  的账目定义，**不是冗余**。真正的缺陷在 `default:` 分支：`LatencyArm.cs:587`、
  `ReliabilityArm.cs:684`、`PersistentArm.cs:441` 把"尚未认识的 status"记成 protocolError。
  将来给 `FrameReadStatus` 加一个成员，这三处会静默把它当协议错误；另外三处 if-chain 同样静默
  （`status != Frame` → protocolError / 跳过）。
- 建议的抽象：给枚举配一个**无 `default`** 的映射函数，让编译器强制穷尽：

  ```csharp
  internal enum FrameReadClass { Frame, CleanEof, Corrupt, ProtocolError }
  internal static FrameReadClass Classify(this FrameReadStatus status) => status switch { /* 全覆盖 */ };
  ```
  各臂保留自己的策略分支，只共享"成员 → 类别"这一步。
- 风险：**低**。

### 1.8 逐字重复的诊断字符串

- `"sendWouldBlock counts sends the kernel did not accept synchronously…"`
  —— `LatencyArm.cs:330`、`LossArm.cs:54`、`PersistentArm.cs:175`（3 份）
- `"W is the plan's lossWindowMs, 200 ms when the plan does not declare one…"`
  —— `LossArm.cs:47`、`MixArm.cs:164`（2 份，只差 `metrics.window` / `classes.udp.window`）
- `"a reply carrying another flow's connection id validates against that flow's own filler…"`
  —— `LatencyArm.cs:331`、`LossArm.cs:53`、`MixArm.cs:168`（3 份）
- `"a rate or ratio whose denominator is zero is written as null rather than 0"`
  —— `LossArm.cs:57`、`MixArm.cs:170`（2 份）
- 建议的抽象：抽到 `internal static class ArmNotes` 的 `const string`，各臂引用。
- 风险：**中**（这些文本原样进 JSONL，是输出契约）。必须逐字节比对 `notes` 数组；
  含实时插值的 note（如 `LatencyArm.cs:349` 的 `windowOverflow = {n}`）**不能**抽。
  收益也低（省的是维护一致性，不是行数），优先级排在最后。

### 1.9 连接 id 常量各自为政

- 位置：`LatencyArm.cs:167-168`（`0x7100_0001` / `0x7400_0000`）、`LossArm.cs:11`（`0x1055_0001`）、
  `ThroughputArm.cs:79`（`0x5448_0000`）、`ReliabilityArm.cs:105`（`0x5245_0000`）、
  `PersistentArm.cs:136`（`0x5045_0000`）、`MixArm.cs:408`（`0x4D49_0000`）、
  `MixArm.cs:577`（`0x4D42_0000`）、`MixArm.cs:642`（`0x4D55_0000`）
- 重复份数：**8 处、7 个基址**，无集中登记。其中 6 个是 ASCII 缩写（TH/RE/PE/MI/MB/MU），
  Latency 的两个不是。
- 问题：arm 是串行跑的，所以今天撞不了；但没有任何东西阻止以后撞。两个 arm 共用 id 会让
  target 账本与客户端记录的归属静默合并。
- 建议的抽象：`internal static class ConnectionIds` 一张表，附一句"为什么可以跨臂复用"。
- 风险：**低**（纯搬家）。注意 `BaseArm` 会让 latency 与 loss 子臂在**同一条记录里**连续使用
  两个不同 base，保持现状。

---

## 2. 抽象断裂

**现状**：`BaseArm` 不是基类，它是一个 kind runner（`base` 这个无产品控制臂）。10 个 arm 全部是
`internal static class` + 静态 `RunAsync(ArmContext)`，**没有共同基类、没有共享的 outcome writer、
没有共同的错误分类**。所以"BaseArm 提供了什么"的答案是：什么都没提供 —— 它只有两个私有 helper
`ReadCount`/`ReadMilliseconds`（`BaseArm.cs:99-117`）。

### 2.1 `BaseArm` 的类名与实际语义不符（最该改名的一处）

`BaseArm.cs:5` 执行的是 `kind: "base"`：无产品的 latency + loss 控制臂。读者第一眼会以为它是
所有 arm 的基类。

**建议**：改名 `ControlArm`（或 `BaselineArm` —— analysis 侧已经在用 "baseline/floor" 这个词），
文件改名 `ControlArm.cs`。`ArmDispatch.cs:15` 与 `PlanFile` 里的 **kind 字符串 `"base"` 必须保持
不变**（它会写进 `run.json` 的 `kind` 字段），只改 C# 类型名。

风险：低。

### 2.2 `BaseArm` 用字符串键读子臂的 gates（最脆的一处耦合）

`BaseArm.cs:49-56` 在运行时按字符串键从子臂的 `Gates` 字典里捞 `long`/`double`：

```csharp
["clientSendLoss"] = ReadCount(latency.Gates, "clientSendLoss") + ReadCount(loss.Gates, "clientSendLoss"),
["scheduleTruncated"] = Math.Max(ReadCount(latency.Gates, "scheduleTruncated"), ReadCount(loss.Gates, "scheduleTruncated")),
["windowMs"] = ReadMilliseconds(loss.Gates, "windowMs"),
```

而 `ReadCount`/`ReadMilliseconds`（`BaseArm.cs:99-117`）在找不到键或类型不匹配时**返回 0**。
后果：子臂把 `gates.scheduleTruncated` 改个名、或把 `long` 换成 `int`，BASE 记录会安静地发布
一个"通过"的 gate。这是全仓**唯一**一处跨 arm 的类型级耦合，却用了最弱的类型。

**建议**：让子臂返回结构化摘要，而不是让 BASE 去解析 JSON 字典：

```csharp
internal readonly record struct ArmGateSummary(
    long ClientSendLoss, long WindowOverflow, long BacklogDrops, long SendFailures, long LaneShortfall,
    bool ScheduleTruncated, double InFlightCeilingMs, double WindowMs);

// ArmOutcome 上加：internal ArmGateSummary? GateSummary { get; init; }
```

`LatencyArm`/`LossArm` 填充，`BaseArm` 读；`ReadCount`/`ReadMilliseconds` 可删（20 行）。
gates 字典的内容一字不动。

若不改结构，至少把"找不到就返回 0"改成 `throw new InvalidOperationException(...)`，让改名立刻炸出来。

风险：低-中（三臂同时改，但输出不变）。

### 2.3 context 提供了没人用的 API，以及用完就绕开的 API

- **UDP 连接没有 helper，TCP 有。** `SocketOps.TryConnectAsync`（`FrameBuffer.cs:46-65`）把 TCP
  连接失败包成 `bool`；UDP 侧没有任何对应物，四个 UDP arm 全部裸调 `socket.ConnectAsync(...)`
  且**在 try 之外**：`LatencyArm.cs:660`、`LossArm.cs:129`、`MixArm.cs:638`、`DnsArm.cs:220`。
  后果：UDP 目标不可达时抛出的 `SocketException` 会穿过 `await Task.WhenAll(...)`
  （`LatencyArm.cs:182`、`LossArm.cs:38-40` 等）逃到 `ClientRunner.cs:266`，整个 arm 变成
  `type:"error"` 记录；而同一种故障在 TCP 路径上只是每条 lane 的 `connectFailures++`。
  **同一个 harness 对同一种失败给出两种结果。**
  建议：补 `SocketOps.TryConnectUdpAsync(Socket, EndPoint, CancellationToken)`，
  或至少在四处包上 try。
- **`ArmContext.DnsEndPoint` 只有一个调用者。** `ArmContext.cs:151` 只被 `MixArm.cs:457` 使用；
  `DnsArm` 因为要支持 per-arm `dnsPort` 覆盖（`ArmSpec.cs:62-67`）自己 new 了一个
  `IPEndPoint`（`DnsArm.cs:56-57`）。同一个概念两条路。建议给 `ArmContext` 加
  `DnsEndPointFor(int dnsPort)`，`DnsArm` 也走 context。
- **`Dedicated.RunOnOwnThreadAsync` 的"为什么"注释重复了 4 次**：`ArmContext.cs:79-83`、
  `LatencyArm.cs:214-215`、`MixArm.cs:151-154`、`MixArm.cs:325-327`。理由是同一条。把理由留在
  `Dedicated` 的 XML doc 上，调用点删注释。
- **`ArmContext.WithSpec`（`ArmContext.cs:161-169`）只有一个调用者**（`BaseArm.cs:20-21`，2 次），
  却是通用形状，而且它**共享** `Latency` 直方图集合（见 harness-audit §二.6）。建议改名为
  `WithPhaseSpec` 并把"共享 Latency"写进 doc，或让 BASE 自己构造 `ArmContext`。

### 2.4 该是模板方法的地方：没有，也不该有

明确记录一个**反面结论**，防止后来者"顺手"做掉：不要为了消除 §1.6 的骨架重复而引入
`abstract class ArmBase`。三个理由：

1. "开始时刻"语义不同：6 个臂从 `RunAsync` 开头算（`LatencyArm.cs:176` 等），
   `PersistentArm` 从首次连接成功算（`PersistentArm.cs:239-241`），`IdleArm` 不用 deadline。
2. "取消处理"不同：9 个臂吞 `OperationCanceledException`，`IdleArm.cs:17` 让它逃出去。
   于是 Ctrl+C 落在 IDLE 上会在 JSONL 里多出一条 `type:"error"` / `message:"cancelled"` 的记录
   （`ClientRunner.cs:262-265`），落在别的臂上不会。**这一条本身就该统一**，但统一的方向是
   行为决策，不是抽象的副产品。
3. 真正的共同点是**取值**与**写 outcome 的格式**，不是生命周期。

---

## 3. 超长方法与超大类型

### 3.1 超长方法（>45 行）

| 文件 | 类型 | 方法 | 行数 | 建议拆分（按什么接缝） |
| --- | --- | --- | --- | --- |
| `MixArm.cs` | MixArm | `UdpLoopAsync` | 71（626-696） | 拆出"发一包并登记"（661-669）与"收尾"（686-695） |
| `LossArm.cs` | LossArm | `RunUdpPhaseAsync` | 71（117-187） | 拆 pacing+send（136-167）与 drain+horizon（182-186）；horizon 提成 `ComputeHorizon(lastSend, W)` |
| `MixArm.cs` | MixArm | `BulkLoopAsync` | 67（558-624） | 与 `ThroughputArm.RunStreamAsync` 是同构的"限速→建帧→发→等回显"，可提 `TcpEchoStream` |
| `MixArm.cs` | MixArm | `ReceiveUdpLoopAsync` | 62（717-778） | 分类阶梯归位（§1.2）后只剩约 15 行 |
| `MixArm.cs` | MixArm | `PageConnectionAsync` | 60（388-447） | 提取"pipelined 请求-响应回合"（415-431） |
| `DnsArm.cs` | DnsArm | `RunUdpAsync` | 60（209-268） | send / drain / book-timeouts 三段，后两段合并为 `FinishUdpAsync` |
| `LatencyArm.cs` | LatencyArm | `ReceiveUdpLoopAsync` | 59（776-834） | 同 §1.2 |
| `LatencyArm.cs` | LatencyArm | `WriteNotes` | 58（321-378） | 15 条 note 按 tcp / udp / validity 三段拆，与 `WriteTcpMetrics`/`WriteUdpMetrics` 对称 |
| `ReliabilityArm.cs` | ReliabilityArm | `TallyAttempts` | 58（434-491） | 单遍聚合，**已经是好形状**；可拆 `UnexpectedEofBreakdown` |
| `LatencyArm.cs` | LatencyArm | `ReceiveLoopAsync`（TCP） | 55（551-605） | 映射表归位（§1.7）后剩约 20 行 |
| `ReliabilityArm.cs` | ReliabilityArm | `ExchangeAsync` | 68（586-653） | 四个 catch（628-650）提成 `ClassifyException`；try 主体保持原样（时序敏感） |
| `DnsArm.cs` | DnsArm | `RunAsync` | 54（48-101） | 6 条 note（94-99）挪进 `WriteNotes`；参数解析（51-57）挪进 `DnsPlan`（对齐 `LatencyPlan`） |
| `PersistentArm.cs` | PersistentArm | `RunPersistentAsync` | 54（231-284） | idle 窗口进入/跳过（249-259）提成 `TryEnterIdleWindow` |
| `ReliabilityArm.cs` | ReliabilityArm | `WriteMetrics` | 52（310-361） | `meanConnectMs`/`meanTransferMs` 提成 `MeanOrNull(ticks, samples)`（三处重复的三元） |
| `PersistentArm.cs` | PersistentArm | `TryReadFrameAsync` | 50（410-459） | 映射表归位（§1.7） |
| `DnsArm.cs` | DnsArm | `SendTcpPhaseAsync` | 49（411-459） | 与 `SendUdpPhaseAsync` 是同一形状的窗口+配速循环 |
| `DnsArm.cs` | DnsArm | `RunTcpAsync` | 48（362-409） | drain + book-timeouts 提成 `FinishTcpAsync` |
| `PersistentArm.cs` | PersistentArm | `TryReadEchoAsync` | 48（361-408） | 嵌套 switch 可拆 `ClassifyRead` |
| `ThroughputArm.cs` | ThroughputArm | `RunStreamAsync` | 60（194-253） | 与 `MixArm.BulkLoopAsync` 共享 `TcpEchoStream` |
| `LossArm.cs` | LossArm | `DrainAsync` | 47（218-264） | 分类阶梯归位（§1.2） |
| `DnsArm.cs` | DnsArm | `ReceiveLoopAsync` | 45（516-560） | `ReadExactAsync` 已提出，可保留 |
| `ReliabilityArm.cs` | ReliabilityArm | `RunAsync` | 46（130-175） | 5 条 note（167-173）挪进 `WriteNotes` |
| `ThroughputArm.cs` | ThroughputArm | `RunAsync` | 46（81-126） | 4 条 note（121-124）挪进 `WriteNotes` |

### 3.2 超大类型（比超长方法更严重）

| 文件 | 行数 | 类型数 | 说明与建议 |
| --- | --- | --- | --- |
| `LatencyArm.cs` | **1013** | **8** | `LatencyArm` 静态类本身 866 行（148-1013）+ 4 个嵌套类型，同文件另有 4 个顶层类型。建议一分为四：`LatencyCounters.cs`（`LatencyTcpState`/`UdpLatencyState`/`AddFrom`）、`LatencyPlan.cs`（`LatencyPlan`/`DeferredQueue`/`DeferredRequest`）、`LatencyValidity.cs`（`MeasurementValidity`/`LaneStates`）、`LatencyArm.cs`（编排 + metrics）。行为不变，纯搬家 |
| `MixArm.cs` | 779 | 3 | `UdpTotals`（63-102）是纯数据聚合器却埋在执行类里 → 与 `MixCounters` 一起放 `MixCounters.cs` |
| `ReliabilityArm.cs` | 749 | 8 | 建议拆 `ReliabilityTypes.cs`（枚举 + `ReliabilityAttempt` + `ReliabilityTally`）与 `ReliabilityArm.cs`（执行 + `AttemptEvidence` + `ModeTally`） |
| `DnsArm.cs` | 578 | 3 | 还行；`DnsCounters` 可外移 |
| `PersistentArm.cs` | 522 | **8** | 类型数最多。建议拆 `PersistentPlan.cs`（`PersistentPlan`/`PersistentSchedule`）+ `PersistentLink.cs` + `PersistentArm.cs`（`PersistentCounters`/3 个枚举/执行） |
| `ThroughputArm.cs` | 297 | 4 | 内聚合理。**但** `AggregateRateLimiter`（19-72）被 `MixArm.cs:576` 复用 —— 一个共享类型住在别人的臂文件里，应移到 `Client/AggregateRateLimiter.cs` |

---

## 4. 命名与注释

### 4.1 `BaseArm` —— 名字说是基类，实际是控制臂

见 §2.1。

### 4.2 `supplied` / `offered` / `sent` 三个词指两套东西

- Latency / Loss：`supplied` = 配速器发放的槽位数；`sentOk` = 真正写进 socket 的；
  `clientSendLoss = supplied - sentOk`（`LatencyArm.cs:64`、`UdpReliability.cs:141-143`）。
- DNS：`sent` = 写进 socket 的；`unsent` = 因窗口满被跳过的；`offered = sent + unsent`
  （`DnsArm.cs:115-118`）。
- 于是 **`supplied`（LOSS）与 `offered`（DNS）是同一个量**，`sent` 在 LOSS 里是 `SentOk`、
  在 DNS 里也是"写进 socket"，词义一致但读者无从确认。三个词全仓没有集中定义。
- 建议：在 `Client/` 加一份 5 行术语表（或 CONTEXT.md），把
  `offered/supplied`（配速器发放）与 `sent/sentOk`（socket 接受）定死。
  **只改内部命名与注释，JSON key 不动**（`metrics.supplied`、`metrics.offered`、`metrics.sent`
  都是输出契约）。

### 4.3 `achievedRate` 在不同臂里是两个不同的统计量

| 位置 | 分子 |
| --- | --- |
| `LatencyArm.cs:274` / `:304` | `_sentOk`（发出） |
| `LossArm.cs:97` | `SentOk`（发出） |
| `DnsArm.cs:129` | `sent`（发出） |
| `ReliabilityArm.cs:359` | `attempts.Length`（完成并统计的尝试） |
| `PersistentArm.cs:208` | **`_responses`（成功完成的往返）** |
| `MixArm.cs:300` | `goodputBps`（另一套命名） |

六个同族字段里只有一个的分母是"完成"，其余都是"发出"。同名不同义，跨臂直接对比会读错。
建议：内部改名 `offeredRate`（PERSIST 那个改 `completedRate`）；JSON key 是否改需要单独决策
（`metrics.achievedRate` 出现在 5 个臂里，analysis 侧可能已引用）。

### 4.4 `MixArm` 把接收侧的 socket 错误记成"发送失败"

`MixArm.cs:770-773`：`ReceiveUdpLoopAsync` 的 `catch (SocketException)` → `tracker.MarkSendFailure()`。
后果：接收侧故障被发布成 `classes.udp.sendFailures`，并计入 `clientSendLoss`（"客户端自己毁掉的
样本"）。对照 `LossArm`：接收侧 socket 错误在 `DrainUntilAsync` 里被完全吞掉（`LossArm.cs:208-211`，
注释"the peer is already gone"），发送侧的才是 `MarkSendFailure`（`LossArm.cs:169-171`）。
**两个臂对同一种故障给出互相矛盾的分类**，而且方法名 `MarkSendFailure` 在接收路径上被调用，名字
直接撒谎。

建议：给 `UdpReliabilityTracker` 加 `MarkReceiveFault()`，或在 Mix 里用独立计数器。
**这会改数值**，属于需要先定语义的改动（与 harness-audit §二.11 "接通或删除"同类）。

### 4.5 `malformed` 一词两义，且没有被任何 note 定义

`DnsArm._malformed` 在 4 处递增：query 构造失败（`:304`、`:445`）、UDP 响应解析失败（`:332`）、
TCP 响应解析失败（`:491`）。前两者是"我造不出查询"，后两者是"对方回了坏包"。
`metrics.malformed`（`:126`）把两类混在一起，而 6 条 note（`:94-99`）里既没有定义 `malformed`，
也没有定义 `emptyAnswers`（`:125`）与 `rcodes`（`:141`）。

建议：拆成 `queryBuildFailures` / `unparseableResponses`，并补齐三条 note。改输出字段 → 需签字。

### 4.6 `DnsArm.TcpIdStride = 2` / `UdpIdStride = 2`（`DnsArm.cs:38-39`）

两个常量同值，起手 id 分别是 1 和 0（`:425`、`:283`），所以 UDP 用偶数、TCP 用奇数。
**这个设计原因没有任何注释**，而两个常量名暗示它们可以独立变化 —— 一旦有人只改一个，奇偶分离
就破了。

建议：合并为一个 `const int TransactionIdStride = 2`，补一句"两条 transport 共用同一个 target
DNS responder，奇偶分离让 id 空间不重叠"。

### 4.7 `PersistentArm` 的两个 `DeadlineTicks` 同名不同义

`PersistentSchedule.DeadlineTicks`（`:91`，臂的绝对截止 tick）与
`ArmContext.DeadlineTicks(long startTicks)`（`ArmContext.cs:157`，把起点换算成截止）。
两者在同一行出现：`PersistentArm.cs:241` `BuildSchedule(startTicks, context.DeadlineTicks(startTicks), plan)`
—— 读起来像自我赋值。

建议：方法改名 `DeadlineFor(startTicks)`，或把 struct 的属性改名 `EndTicks`。

### 4.8 魔数清单（都缺名字或缺注释）

| 位置 | 字面量 | 含义与问题 |
| --- | --- | --- |
| `ReliabilityArm.cs:203` | `index & 0xFFFF` | connectionId 每 65536 次尝试回绕。`:56-60` 的注释说这个 id 用于与 target 账本 **1:1 拼接** —— 6 小时 × 1000 cps 的 plan 会回绕 55 次，拼接静默失效。**没有守卫、没有计数器、没有 note** |
| `MixArm.cs:408` | `(uint)(desktopIndex << 8)` | 每个 desktop 预留 256 个 connectionId。`PageConnections = 13`，但没有任何东西阻止它超过 256 而与下一个 desktop 撞 id |
| `MixArm.cs:466` | `(desktopIndex * 1024)` | 每个 desktop 预留 1024 个 DNS transaction id，同上 |
| `DnsArm.cs:36-37` | `UdpWindow = 256` / `TcpWindow = 64` | 写死在臂里、不来自 plan、**且没有发布到 `parameters`** —— DNS 记录看不出自己的窗口是多少（见 §6.3） |
| `DnsArm.cs:23` / `:206` | `new long[16]` / `rcode & 0xF` | 正确（DNS rcode 是 4 bit），但没有任何注释说明这是协议宽度而非随手截断。建议 `const int RcodeCount = 16` + 引 RFC 1035 |
| `DnsArm.cs:29` | `new long[128]` | "最大的 DNS type 值 + 1" |
| `DnsArm.cs:179-185` | `(index % 100)` 的 54/78/98 分界 | A 54% / AAAA 24% / HTTPS 20% / TXT 2% 的查询混合，无注释、不可配 |
| `MixArm.cs:104-113` | `DefaultDesktops=4`、`PageConnections=13`、`PageTotalRequests=73`、`PageMessageBytes=38_000`、`BulkBitsPerSecond=5_000_000`、`BulkPayloadBytes=32*1024`、`DnsQueriesPerPage=4`、`UdpPacketsPerSecond=30`、`UdpPayloadBytes=120`、`s_pageInterval=20s` | 全部硬编码负载，全部发布在 `parameters`（`:126-134`），所以记录自洽 —— 但 plan 改不动（§6.4）。13/73 还隐含"8 个连接各 6 请求、5 个各 5 请求"（`RequestsForConnection`，`:381-386`） |
| `LatencyArm.cs:158/161/165` | `BacklogSeconds=10` / `MaxBacklogPerLane=1<<20` / `DrainSeconds=1` | 有注释，**好** |
| `ReliabilityArm.cs:109/116/117` | `MaxInFlightAttempts=512` / `MaxAttemptRecords=4096` / `AttemptSampleStride=16` | 有 8 行注释，**好** |
| `ThroughputArm.cs:79` 等 6 处 | 连接 id 基址 | 见 §1.9 |

### 4.9 注释与代码不一致 / 注释范围过宽

- `ArmSpec.cs:56-60` 的 `LossWindowMs` doc 说 "the loss threshold the UDP arms publish and classify
  against" —— 实际只有 LOSS 与 MIX 读它；`LatencyArm` 的 UDP lane **不读**（延迟臂不判丢失）。
  措辞"UDP arms"过宽。
- `PlanFile.cs:18-19` 说 `window` 是 "the in-flight window: latency, loss, and both base phases"，
  但**没有**说明它在两个臂里的默认值差 64 倍（64 vs 4096）。`BaseArm.cs:73-74` 承认了这件事，
  schema 文档没有。
- `FrameBuffer.cs:28` `FlipPayloadByte(int payloadBytes, int offset)` 的 `offset` 形参两个调用点
  都传 0（`LossArm.cs:104`、`:110`），`offset % payloadBytes` 恒为 0 —— 形参暗示可变位置，实际是
  死参数（见 §7.2）。
- `PersistentArm.cs:171-175` 的 5 条 note 定义了 `requests/responses/reconnects/survivedIdle/
  idleSeconds*/sendWouldBlock/sendFailures/timeouts/remoteClosed/protocolErrors/meanConnectMs`，
  **没有**定义 `corrupt`（`:200`）、`unmatchedReplies`（`:201`）、`connectAttempts`（`:202`）、
  `responseRate`（`:207`）。对照 `LatencyArm.cs:333` 专门为 `connectAttempts` 写了一条 note ——
  同一个 key 在 Latency 有定义、在 Persistent 没有，而且两个臂的总体不同
  （见 §4.10）。
- `PersistentArm.cs:202` 的 `connectAttempts = _connectSamples + _connectFailures` 与
  `LatencyArm.cs:275` 同名，但 `PersistentArm._connectSamples` 在 `SendCommandAsync` 失败时
  （`:507-512`）**已经**被计过了 —— 一次握手失败的连接也被算进"成功的 connect 样本"，
  `meanConnectMs` 因此包含它。建议在 `:503-512` 失败时回退计数或加注释说明。

### 4.10 `internal long _field` 命名

11 个计数器 DTO（`LatencyTcpState`/`UdpLatencyState`/`PersistentCounters`/`DnsCounters`/
`MixCounters`/`ThroughputStreamState`/`ReliabilityTally`/`ReliabilityAttempt`/`ExchangeResult`/
`PersistentSchedule`/…）用 `_` 前缀声明 **internal** 字段，直接被臂代码读写。
C# 惯例里 `_` 是"私有"，这里是"可写的数据袋"，读者无法从名字区分。

建议：**不要**为风格做全仓重命名（diff 巨大、收益低）。在 §3.2 的类型搬家重构时顺手改成
`internal long SentOk;`，那是唯一自然的时机。

---

## 5. 状态管理与并发

### 5.1 `UdpReliabilityTracker` 没有并发契约，而 `MixArm` 在两个线程上同时用它（**最严重**）

- `LossArm` 里 tracker 只被一个线程触碰（发送循环 + 同一个 task 里的 drain：`LossArm.cs:140-166`、
  `218-263`），所以 `SentOk++`、`Outstanding++/--`、`_words[word] |= mask`
  （`UdpReliability.cs:39`、`199`、`208`、`229-233`、`269-295`）都是无保护的裸操作 —— **在那儿是对的**。
- `MixArm` 里同一个 tracker 被**两条并发流**使用：
  - 发送循环（`MixArm.cs:661-667`、`:676`）：`MarkSupplied` / `MarkSent` / `MarkSendRefused`
  - 接收循环（`MixArm.cs:736-763`）：`MarkCorrupt` / `MarkCorruptWithKnownSequence` /
    `MarkForeignConnection` / `MarkUnmatchedReply` / `MarkArrival` / `WasSent`
  全程无锁、无 `Interlocked`、无 `volatile`；`SequenceBitmap` 的 `_words[word] |= mask` 是
  非原子读改写。后果：`SentOk`/`Arrived` 会漂，而 harness 的全部结论都建立在这些数上。
- `Client/UdpReliability.cs:119-124` 的类注释只讲账目语义，**只字未提线程**。

建议（二选一，并把选择写进类 doc）：

- a) 写死"single-threaded only"，让 `MixArm` 的接收线程只把包塞进 `ConcurrentQueue`，
  由发送线程统一 book；
- b) 给跨线程的成员加 `Interlocked`/锁（每包一次 CAS，代价可接受）。

**这会改变 loss 数字**，需要与 harness-audit 的结论一起定。

### 5.2 `LatencyArm` 在一个对象上混用 `Interlocked` 与裸自增

`_inFlight` 走 `Interlocked.Read/Increment/Decrement`（`LatencyArm.cs:500`、`505`、`547`、`578`、
`726`、`731`、`772`、`812`），而同一函数里的 `_sentOk++`（`:546`、`:771`）、`_sendWouldBlock++`
（`:532`、`:756`）、`_sendFailures++`（`:542`、`:767`）、`_windowOverflow++`（`:511`、`:737`）、
`_backlogDrops++`（`:515`、`:740`）、`_supplied = index`（`:465`、`:695`）全是裸写。

今天是对的（每个字段只有一个写者：发送半 or 接收半），但代码读起来一半像"需要原子"、一半像
"不需要"。`LatencyTcpState:46-50` 已经为 `_inFlight` 写了归属说明 —— 把那段提升成整个类型的契约
即可。**不要**为了"统一"给单写者字段加 `Interlocked`（见 §9.4）。

### 5.3 可变状态被多处修改

- `MixArm.observationEnds[desktopIndex]`（`MixArm.cs:691`，写在 `finally` 里）只由该 desktop 写，
  `ClassifyDesktops`（`:206-224`）在 `Task.WhenAll` 之后读，安全。但裸 `long[]` 的形状像共享状态，
  建议加注释或包成 `DesktopObservation`。
- **范本**：`ReliabilityArm.AttemptEvidence` 的 `_seen/_claimed/_written/_omitted` 用 `Interlocked`
  （`:261`、`270`、`272`、`276`），并在 `:233-241`、`:268-269` 解释了为什么。这是全仓唯一一处
  用 `Interlocked` 明确表达跨线程的数据结构，可以拿去校准 §5.1。
- `LatencyArm.LaneStates`（`:924-941`）：注释说"每个 lane 只写自己的 state，totals 在所有 lane
  join 之后成形"，与代码一致。**好**。
- `PersistentArm` 的 `_idleEntered/_idleBeginTicks/_idleEndTicks/_idleGeneration/_survivedIdle`
  （`:25-29`）五个字段描述**一个状态机**，修改点散在 `:251-253`、`:306-318` 三处。
  `PersistentCounters`（名字说 counter）因此既是计数器又是状态机。
  建议收进一个 `IdleWindowState` 小类，`WriteMetrics`（`:183-187`）读它。
- `ThroughputArm.AggregateRateLimiter` 用 `Lock`（`:44`、`54`），正确且必要（多流共享）。
  但 `MixArm.cs:576` 传 `long.MaxValue` 当 budget → 见 §7.1。

### 5.4 生命周期不清的一处

`MixArm.RunDesktopAsync`（`:323`）建了 `linked` CTS 并把 token 传给三条 lane，但三条 lane 全部
结束后**没有人 cancel 它**（`using var` 只负责 Dispose）；`UdpLoopAsync` 自己另建了
`laneCancellation`（`:643`）并在 `:694` cancel。功能上没问题，读起来像漏了一步。
建议删掉 desktop 级 CTS（三条 lane 用外层 token），或补一句注释说明它存在的目的。

---

## 6. 参数与配置

### 6.1 同一个 plan key，两个 arm 两个默认值：`window`

- `LatencyArm.cs:152` `DefaultWindow = 64`
- `LossArm.cs:10` `DefaultWindow = 4096`

`BaseArm.cs:73-74` 的注释承认"每个 phase 保留自己的默认负载"，但 `PlanFile.cs:18-19` 的 schema
文档没有。实际 shipped plan 里 `LAT`/`LATLOAD` 显式写了 `window: 4096`（`scripts/plans/full-plan.json`），
说明 64 这个默认值非常容易被顶掉。

**可执行改法**：把两个默认值放进同一个 `ArmDefaults`（§1.4），并让 `PlanFile` 的 doc 明确
"latency 默认 64 / loss 默认 4096"。**不要统一这两个值** —— 统一会改掉所有不带 `window` 的
现有 plan 的时序行为。

### 6.2 plan key 打错或用错**完全静默**（最该补的守卫）

`PlanFile.ReadArmNumbers`（`:267-284`）为**每个** arm 解析**全部** 15 个数字 key；每个 arm 只读
它认识的那几个。于是下面这些 plan 都能通过校验、静默无效：

| plan 片段 | 结果 |
| --- | --- |
| `{"kind":"mix","payloadBytes":1200}` | 忽略（Mix 用硬编码 `UdpPayloadBytes=120`，`MixArm.cs:112`） |
| `{"kind":"mix","ratePerSecond":100}` | 忽略（Mix 用 `UdpPacketsPerSecond=30`） |
| `{"kind":"dns","window":4096}` | 忽略（DNS 用 `UdpWindow`/`TcpWindow`，`DnsArm.cs:36-37`） |
| `{"kind":"throughput","lanes":4}` | 忽略 |
| `{"kind":"loss","protocol":"tcp"}` | 忽略（`BaseArm.LossPhaseSpec` 强制 udp；`LossArm` 不读 `Protocol`） |

**建议**：在 `ArmSpec` 旁边加一张 `kind → 允许的 key 集合` 表，`TryReadArm` 里对"出现了但不属于
该 kind"的 key 直接报错（fail-closed，与本仓 error-handling 的一贯风格一致）。今天没有任何东西
会告诉你 plan 里写错了一个 key。

三个真实的解析守卫缺口：

- `PersistentArm.cs:144` 用 `(uint)Math.Max(0, spec.ExpectedBytes)` 把负值钳成 0 发给 target，
  而 `:157` 把**原始** `spec.ExpectedBytes`（可能是负数）发布到 `parameters.expectedBytes`
  —— 记录与线上行为不一致。建议负数直接报错（或两处发布同一个值）。
  附带：Persistent 的 `expectedBytes` 默认 0（无默认值），而 `ReliabilityArm` 默认 8192
  （`:102`）；0 在 Clean 模式下无害（target 不用它），但两个臂对同一个 key 的默认值不同这件事
  没有写在 `PlanFile.cs:24` 的文档里。
- `ThroughputArm.cs:87` `budget = (long)(targetBytesPerSecond * spec.Seconds)` 是
  long × double 再截断，`PlanFile.TryReadNumber` 又允许小数秒。建议对 `targetBytesPerSecond`
  设上限（`Pacer` 的 rate 也需要）。
- **没有 arm 校验 `payloadBytes` 的上界**：`FrameBuffer` 直接
  `new byte[HeaderSize + payloadBytes + TrailerSize]`（`FrameBuffer.cs:13`）。
  plan 写 `payloadBytes: 2000000000` 会 OOM，报错信息不会是"plan 非法"。
  建议在 `PlanFile` 里给 `payloadBytes`/`expectedBytes` 设显式上限（例如 1 MiB，理由写清楚）。

### 6.3 `DnsArm` 的窗口不可配置，连发布都没有

`UdpWindow = 256` / `TcpWindow = 64`（`DnsArm.cs:36-37`）不出现在 `parameters`（`:61-69` 只有
seconds / ratePerSecond / tcpPercent / cnameEvery / dnsPort / drainWindowMs）。同一个 harness 的
LAT 臂会把 `parameters.inFlightWindow` 发布出来（`LatencyArm.cs:913`），DNS 不发布，于是
**DNS 记录无法自证自己的在途窗口**。

建议：接 `spec.Window`（保留 256/64 作各自默认），或至少在 parameters 里发布这两个值。
后者是输出契约变更。

### 6.4 `MixArm` 的整臂负载都不来自 plan（有意，但要写清）

MixArm 只读 `Seconds` / `Desktops` / `LossWindowMs`（`:117-119`）。页面节奏（20 s）、
13 连接 × 73 请求、38 KB 消息、5 Mbps bulk、每 desktop 30 pps UDP、每页 4 个 DNS 查询，
全部是常量（`:104-113`）并发布在 parameters 里。

这是**有意的可比性设计**（记录自洽、跨产品同负载），不要改成"什么都能配"；但 §6.2 的守卫仍然
该加，否则读者会以为 plan 控制了它。

### 6.5 `Seconds` 的类型

`ArmSpec.Seconds` 是 `double`（`ArmSpec.cs:28`，默认 60），`PlanFile.TryReadNumber` 允许小数
（`:323-329`）；`IdleArm.cs:17` 直接 `Task.Delay(TimeSpan.FromSeconds(seconds))`，其他臂用
`Clock.FromSeconds` 换算，`PersistentArm.cs:214` 的 `Slots` 用整数除法 + 上取整 —— 小数秒在
Persistent 里的语义没有定义。`BaseArm.cs:35` 又把它 ×2 当 `parameters.seconds` 发布。

建议：plan schema 明确 `seconds` 必须是正整数（把 `TryReadNumber` 换成整数读取），或在文档里
说明小数秒在各臂的取整行为。

---

## 7. 死代码 / 无效代码

### 7.1 不可达分支

- **`MixArm.cs:585-588`** `if (!limiter.TryReserve(frameLength, out var waitUntilTicks)) { break; }`
  —— 该 limiter 的 budget 是 `long.MaxValue`（`:576`），`TryReserve` 里
  `_reserved + bytes > _totalBudget`（`ThroughputArm.cs:56`）在溢出之前恒为假。这个 `break`
  永远不执行，`BudgetExhausted` 在 Mix 路径上永远不被读。
  **建议**：给 `AggregateRateLimiter` 加 `Unlimited(long bytesPerSecond, long startTicks)` 工厂
  （内部仍用 `long.MaxValue`，但把"没有预算"变成有名字的构造），MixArm 用工厂且不再检查返回值
  —— 分支就消失了，而不是被隐藏。
- **`ThroughputArm.cs:146`** `frameLength == 0 ? 0 : totals._bytesEchoed / frameLength`
  —— `frameLength` 是 `const`（`:86`）= HeaderSize + 32768 + TrailerSize，永远非 0。
  删掉三元（或换成 `Debug.Assert`）。这是"防御性代码写在了编译期常量上"。

### 7.2 死参数

- **`FrameBuffer.FlipPayloadByte(int payloadBytes, int offset)`**（`FrameBuffer.cs:28-31`）：
  两个调用点都传 `0`（`LossArm.cs:104`、`:110`），`offset % payloadBytes` 恒为 0。
  建议删形参（`FlipPayloadByte(int payloadBytes)`，翻第 0 字节），或把它实现成真正的
  "翻第 index 个字节"。**这是 `--inject-corrupt-every`/`--inject-rewrite-every` 自检路径，删形参不改行为。**
- **`LossArm.ApplyFaultInjection`（`:100-115`）把整个 `ClientOptions` 传进帧构造路径** ——
  它只用 `options.InjectCorruptEvery`/`InjectRewriteEvery`，而这两个值在 `RunAsync` 里已经可用。
  建议签名收窄成 `(int injectCorruptEvery, int injectRewriteEvery)`，或提成 `FaultInjection`
  小结构体。CLI 选项袋不该出现在每包路径上。

### 7.3 结构性恒零 / 只写不读

- **`MixArm` 的 `classes.udp.windowOverflow`（`:240`）恒为 0**：`MarkWindowOverflow` 在 Mix 路径上
  **没有任何调用点**（全仓唯一调用点是 `LossArm.cs:152`），而它还是 `clientSendLoss` 的加数
  （`MixArm.cs:100`）。它是**故意**发布的（`MixArm.cs:166` 的 note 明说 "structurally zero"），
  所以不是漏改，而是"测不到的零带着解释"。最小改法：在 `UdpTotals.Add` 里删掉这一项并改 note。
- **`IdleArm.cs:20-21`** 的 `Gates["clientSendLoss"] = 0` / `["windowMs"] = 0`：一个不产生流量的臂
  把两个 gate 硬编码成"通过"。同族：`ReliabilityArm.cs:165-166`、`ThroughputArm.cs:119-120`、
  `DnsArm.cs:92-93`、`PersistentArm.cs:170` 的 `windowMs = 0`。
  `windowMs` 是"UDP 丢失门限"，对 4 个 TCP 臂没有意义却被发布成 0。
  （`clientSendLoss` 硬编码 0 已由 harness-audit §二.12 记录。）
- **`DnsArm._emptyAnswers`（`:19`）**：只在 `Classify`（`:198`）递增、只发布（`:125`），
  **没有任何 note 定义它**，也没有 gate 读它。要么定义，要么删。
- **`UdpReliabilityTracker.Outstanding`** 在 Mix 路径上被维护（`MarkSent` 加、`ResolveSlot` 减）
  但 Mix **从不发布它**；而且 Mix 从不在发送循环里调 `Retire`，只有 `Classify` 末尾调一次
  （`UdpReliability.cs:332`），所以整段运行里它单调上升。建议：MixArm 发布它（有价值：能看出在途
  积压），或在 doc 上写清"只有调用 `Retire` 的臂才该读 `Outstanding`"。

### 7.4 引用计数扫描结论

我逐字段、逐方法扫了 `Arms/` + `Client/UdpReliability.cs` + `Client/FrameBuffer.cs`：
**没有发现零引用的成员**。所有 `internal` 成员在 Arms 内至少有一次真实读写；
`ArmContext` 的三个 EndPoint 属性、`WithSpec`、`FrameBuffer.RecomputeChecksum`、
`SequenceBitmap.TryClear`、`MarkWouldBlock` 都有真实调用点。所以本节的结论集中在
"不可达分支 / 死参数 / 恒零字段"，而不是"没用的方法"。

### 7.5 从未被执行过的配置面

`modeMix` 支持 `stall`（`PlanFile.cs:200`、`TcpMode.Stall`），target 侧实现了它的语义
（`Target/TcpTargetServer.cs:257`、`:323`），但 5 份 shipped plan
（`scripts/plans/*.json`、`scripts/plans-short/*.json`）**没有一份**用它；客户端侧对 Stall 的处理
只是"不发 FIN"（`ReliabilityArm.cs:618`）+ 归类为 Clean（`ExpectedOutcome:539-544` 的 `_ =>`、
`Classify:723-730` 的 `_ =>`）。

这不是死代码（plan 可及），但它是**从未被跑过的分支**。要么在 plan 里加一个 STALL 臂，
要么在 `ArmSpec.DefaultModeMix` 旁注明为什么不测它。

---

## 8. 错误处理

### 8.1 三种"什么都不做"的 catch 及其复制粘贴的注释

全 Arms 的 catch 分布：`SocketException` ×24、`OperationCanceledException` ×24、
`ObjectDisposedException` ×19、`SocketException exception` ×1、`IOException` ×1。

其中：

- `OperationCanceledException` → `/* the arm deadline or a shutdown request ended the loop */`
  —— **23 处逐字相同**
- `ObjectDisposedException` → `/* teardown closed the socket first */`
  —— Arms 内 **16 处逐字相同**（另 1 处在 `FrameBuffer.cs:77`）
- `SocketException` → 各种变体，其中 `LossArm.cs:210` 的
  "the peer is already gone; the outcome was recorded before this point" 是同一件事的第三种说法

这**不是**"吞异常"的坏味道（取消与 teardown 是正常路径，注释也说明了为什么）。问题是注释被复制了
39 次，且出现了第三种写法。建议把这三条收尾语义提升为类型级契约：

```csharp
// 反复出现的收尾语义：取消与 teardown 是正常结束，不计数、不发布。
private static bool IsShutdown(Exception ex) => ex is OperationCanceledException or ObjectDisposedException;
```

**注意**：不要把这些 catch 换成 `catch (Exception) when (IsShutdown(ex))` 的全仓统一形式 ——
多处 catch 体里还有各自的计数器（`_protocolErrors++`、`_pageErrors++`、`_socketErrors++`），
统一过滤会静默改掉它们。只统一注释与判定。

### 8.2 同一个异常，四个臂四种账（真实的不一致）

`ObjectDisposedException` 在 19 个 catch 里有 **4 种策略**：

| 策略 | 位置 | 数量 |
| --- | --- | --- |
| 良性 teardown，不计数 | LatencyArm 4 处、LossArm 2、DnsArm 4、MixArm 3、ThroughputArm 2、PersistentArm 1 | 16 |
| 记成错误（`_pageErrors++` / `_bulkErrors++`） | `MixArm.cs:443-446`、`MixArm.cs:620-623` | 2 |
| 记成**样本级的 OtherError**（→ 分类成 OtherError 结果 + 计入 fidelityMismatch） | `ReliabilityArm.cs:643-646` | 1 |
| helper 内吞掉（→ `false` / 忽略） | `FrameBuffer.cs:61`、`FrameBuffer.cs:77` | 2 |

最有问题的是第三行：teardown 期间被切断的一次 REL 尝试，会被发布成该**产品**的
`otherError` 与 `fidelityMismatch`。而其余 16 处都认为 teardown 不产生数据点。
建议先定一条规则（"teardown 不产生数据点"），再统一 2、3 两处。**这会改数值。**

### 8.3 接收侧 `SocketException` 的分类互相矛盾

- `MixArm.cs:770-773`：接收侧 → `MarkSendFailure()`（见 §4.4）
- `LossArm.cs:208-211`：接收侧 → 完全忽略（注释"the peer is already gone"）
- `LossArm.cs:169-171`：发送侧 → `MarkSendFailure()`（正确）

### 8.4 `ConnectAsync` 未被保护（见 §2.3）

四个 UDP arm 的 `socket.ConnectAsync` 都在 try 之外（`LatencyArm.cs:660`、`LossArm.cs:129`、
`MixArm.cs:638`、`DnsArm.cs:220`），一次 `SocketException` 让整个 arm 变成 `type:"error"` 记录，
而 TCP 侧的同类失败只是计数器。

### 8.5 `catch` 类型过宽（有意，但值得写下）

`SocketOps.TryConnectAsync`（`FrameBuffer.cs:46-65`）把 `OperationCanceledException` 吞成 `false`，
调用方无法区分"对端拒绝"与"我被取消"。`LatencyArm.cs:414-422` 在 false 时同时
`_connectFailures++` 和 `_scheduleTruncated = true` —— 被取消时也会标记"调度被截断"。
**不改**（取消时整个 run 都会终止，没有下游消费这个标记），但应在 doc 上写明
"false 不表示失败，表示没有连上"。

### 8.6 plan 错误被当成运行期失败

`ReliabilityArm.cs:149-152` 在 modeMix 解析失败时 `throw new InvalidOperationException(error)`，
被 `ClientRunner.cs:270-273` 记成 `failed = true`（exit 1），而不是 usage error（exit 2）。
一个 plan 拼写错误被当成"被测产品的一次失败"，会污染 `run.json` 的 `failed` 与逐臂 summary。
建议把这个校验提前到 `PlanFile.TryLoad` 阶段（`TryParseModeMix` 已经存在）。

---

## 9. 不要动的地方（刻意为之）

以下每一处都有非显然的理由，重构时必须原样保留。若确实要动，先建立 §10 末尾的回归基线。

### 9.1 所有 `outcome.Notes` 文本

59 条 note、占 Arms 非空白字符的 **15.4%**（`LossArm` 28.3%、`BaseArm` 27.7%），最长单行 659 字符
（`MixArm.cs:166`）。它们**原样进 JSONL**，是输出契约，analysis 侧可能引用。
§1.8 的合并如果做，必须逐字节比对 `notes` 数组；含实时插值的 note（`LatencyArm.cs:349` 的
`windowOverflow = {n}`）**无论如何不能**变成静态模板。

### 9.2 `Task.Delay(1)` 的 1 ms 轮询与 `Socket.Available`

`LatencyArm.GraceDrainAsync`（`:836-866`）、`LossArm.DrainUntilAsync`（`:201`）、
`MixArm.DrainPendingAsync`（`:713`）、`DnsArm.cs:248`/`:457`、`MixArm.WaitForResponseAsync`（`:552`）。
harness-audit §四已指出这会给亚毫秒 RTT 加上最多一个定时器 tick；但改成 async receive 会改变
每包路径的分配与唤醒次数。**保持现状**，只有拿到测量证据之后再动。

### 9.3 `Pacer.WaitUntil` 的阻塞式忙等与它周围的三行抑制

`Pacer.WaitUntil`（`ArmContext.cs:36-62`）用 `Thread.Sleep / Sleep(0) / SpinWait` 阶梯，
配 7 处 `#pragma warning disable S6966, VSTHRD103, MA0042` +
`// ReSharper disable once MethodHasAsyncOverload`：`LatencyArm.cs:460-463`、`:690-693`、
`LossArm.cs:142-145`、`MixArm.cs:656-659`、`DnsArm.cs:287-290`、`:429-432`、
`ReliabilityArm.cs:195-198`、`PersistentArm.cs:226-228`。

理由成立（Windows 定时器粒度 15.6 ms），且本仓的 quality gate（AGENTS.md）要求窄域抑制 + 理由。
**重构时必须把 pragma 原样搬走**，否则 `dotnet format --verify-no-changes` 与 `jb inspectcode` 会红。

### 9.4 单写者裸字段（见 §5.2）

不要为了"看起来更安全"给 `_supplied`/`_sentOk`/`_sendWouldBlock` 加 `Interlocked`。
热点路径上每包一次 CAS 买不到任何东西。要动就动文档。

### 9.5 每个 lane / desktop / stream 独占线程

`Dedicated.RunOnOwnThreadAsync`（`ArmContext.cs:86-90`）的 `TaskCreationOptions.LongRunning` 与
**12 个调用点**。理由（`ArmContext.cs:79-83`、`LatencyArm.cs:214-215`、`MixArm.cs:151-154`、
`:325-327`）是真实的：`await` 同步完成时 lane 会在启动循环里跑完，后面的 lane 根本不会被构造。
**不要**改成 `Task.Run` 或 `Parallel`。能做的只是把重复的注释删到只剩 `Dedicated` 的 doc。

### 9.6 `send.IsCompleted` 的 ValueTask 复用手法

`LatencyArm.cs:529-538`、`:753-762`，`LossArm.cs:159-165`，`PersistentArm.cs:343-349`。
把 `await socket.SendAsync(...)` 拆成"先看 `IsCompleted` 计数、再 await"是 `sendWouldBlock` 的
**唯一**来源；改成一行 await 会让这个已发布的计数器永久归零。

### 9.7 预分配与就地写

`FrameBuffer.cs:13`/`:24`（预分配缓冲 + `Filler.Fill` 就地填充）、`LogHistogram.cs:64`
（34 × 2048 定长桶 + 单锁）、`UdpReliability.cs:8`/`:135-136`（位图 + `_sendTicks[sequence] = ticks`
用序列号当下标）、`ThroughputArm.cs:21-71`（`AggregateRateLimiter` 的每帧一次锁）。
`_sendTicks[sequence]` 这种"用序列号当下标"的手法**不要**改成字典。

### 9.8 JSON 数值精度

`JsonValue.Round` 默认 3 位（`JsonValue.cs:60`）、`elapsedSeconds` 在 `ThroughputArm.cs:155` 与
`BaseArm.cs:43` 用 4 位、`Ratio` 用 6 位（`:71`）。不一致点是：同一个 key `metrics.elapsedSeconds`
在 `IdleArm.cs:19` 是 **3 位**、在另外两个臂是 **4 位**。这是输出契约，**修之前先定契约**。

### 9.9 故意的冗余发布

- `BaseArm.cs:38-45`：子臂的 parameters/metrics 各嵌一层
- `MixArm.cs:246` 的 `sentPerDesktop` 与 `MixArm.cs:271-277` 的 `desktops[].udpSent`：同一个数发布两遍
- `ThroughputArm.cs:144-162`：`bytes` 与 `bytesSent`、`frames` 与 `framesSent`/`framesEchoed` 并列

这些是 witness（逐 lane）与 aggregate（合计）的双视图，harness-audit 明确要求保留。**不要"规范化"掉。**

### 9.10 `ReliabilityArm.AttemptEvidence` 的限额与反压

`ReliabilityArm.cs:212-231`、`242-308`：4096 条上限、16 抽样、"让 sink 反压 attempt 槽位"。
有 8 行注释解释为什么。保持。

### 9.11 输出契约字段名

`parameters.*` / `metrics.*` / `gates.*` 的**所有 key 名字**。本文多处建议"内部改名"，
一律指 C# 成员名，JSON key 需要单独决策。

---

## 10. 建议的重构顺序（按 收益/风险 排序）

| # | 任务 | 收益 | 风险 | 验证方式 |
| --- | --- | --- | --- | --- |
| 1 | 删掉 `FrameBuffer.FlipPayloadByte` 的 `offset` 死形参（§7.2） | 低 | 极低 | 编译；`--inject-rewrite-every` 自检输出不变 |
| 2 | `AggregateRateLimiter.Unlimited(...)` 工厂，消除 `MixArm.cs:585` 的不可达分支（§7.1） | 低 | 极低 | MIX 的 `bulk` 指标逐字段不变 |
| 3 | 删 `ThroughputArm.cs:146` 的 `frameLength == 0` 三元（§7.1） | 低 | 极低 | `metrics.frames` 不变 |
| 4 | `LossArm` 复用 `BookUndecodable`（提到 `UdpReliabilityTracker`）（§1.2） | 中 | 低 | LOSS 的 metrics 全字段 diff 为空 |
| 5 | `GraceDrainAsync` 两个 overload 合一（§1.3） | 低 | 低 | LAT 的 tcp 直方图不变 |
| 6 | 连接 id 常量集中到 `ConnectionIds`（§1.9） | 低 | 低 | 全臂结果逐字节不变 |
| 7 | `BaseArm` → `ControlArm` 改名（§2.1），kind 字符串不动 | 中（可读性） | 低 | `run.json` 的 `kind:"base"` 不变 |
| 8 | `ArmDefaults` 集中 9 份默认值 + 消掉 4 对重复常量（§1.4） | 中 | 低 | 不带 `window`/`payloadBytes` 的 plan 跑一遍，参数块逐字段相同 |
| 9 | `PlanFile` 加 kind↔key 白名单校验（§6.2） | **高**（防静默错配） | 低 | 5 份 shipped plan 全部 `TryLoad` 通过 |
| 10 | `PersistentArm` 的 idle 五字段收进 `IdleWindowState`（§5.3） | 中 | 低 | PERSIST 的 metrics 不变 |
| 11 | `FrameReadStatus` 加无 `default` 的映射函数（§1.7） | 中 | 低 | 各臂 protocolErrors 不变 |
| 12 | 给 `UdpReliabilityTracker` 写并发契约，并让 `MixArm` 只在发送线程 book（§5.1） | **高**（当前是真实数据竞态） | 中（会改 loss 数字） | dry-run：新旧同轮对比，期望 `arrived` 不变或上升 |
| 13 | `LatencyTcpState`/`UdpLatencyState` 抽公共基类（§1.5） | 中 | 中（热点路径） | LAT/LATLOAD 全指标 + 直方图逐字段不变 |
| 14 | `LatencyArm` TCP/UDP 发送三件套合一（§1.1，-113 行） | **高** | 中-高（热点路径） | 同上 + `goodput`/`achievedRate` 一致 |
| 15 | `LatencyArm.cs` 按类型拆成 4 个文件（§3.2） | 高（可读性） | 低 | 纯搬家，编译 + 全指标不变 |
| 16 | `BaseArm` 的字符串键 gate 聚合改成结构化（§2.2） | 中 | 中（三臂同时改） | BASE 的 `gates.*` 逐字段不变 |
| 17 | 统一 MIX 的接收侧错误分类（§4.4、§8.3） | 中 | 中（改数值，需决策） | 先定语义（"接收侧故障算不算 client loss"） |
| 18 | 统一 `ObjectDisposedException` 的四种策略（§8.2） | 中 | 中（改数值，含 REL 的 fidelityMismatch） | 先定语义（"teardown 不产生数据点"） |
| 19 | 5 个臂的 `clientSendLoss` / `windowMs` 硬编码改为派生或删除（§7.3） | 中 | 中（改数值） | harness-audit §二.12 已列，需签字 |
| 20 | 修 `DnsArm` 的 `malformed` 一词两义并补 note（§4.5） | 中 | 中（改字段） | 需签字 |
| 21 | `ArmNotes` 抽共享 note 常量（§1.8） | 低 | 中（输出文本） | `notes` 数组逐字节 diff |
| 22 | §4.10 的 `internal long _field` → `internal long Field` 命名统一 | 低 | 低 | 与第 15 项同一批做 |

### 执行前提（所有"低风险"判断都依赖它）

这套代码**没有单元测试**：全仓没有任何测试项目引用 `Client/Arms/`（`tests/WinForward.Performance.Tests`
是产品侧的），唯一的回归手段是 `scripts/selftest.sh`。

因此建议**在动手之前**先建立回归基线：

```bash
scripts/publish.sh
scripts/selftest.sh scripts/plans/selftest-plan.json
```

（`AGENTS.local.md` §9：client 与 target 是同一个跨平台二进制，整套 harness 能在 Linux 上端到端
跑完，环里没有代理。）把每个臂的 `result` 记录（`parameters` / `metrics` / `gates` / `notes`）
连同 `latency` 直方图存成 golden，之后每个重构项 diff 一次。第 1～11 项应当做到 **diff 为空**；
第 12～20 项应当做到 **diff 只出现在预期字段**。
