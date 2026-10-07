# E2 执行计划（批次化，2026-10-07 计划审查后修订）

本文件是 E2 的可施工计划。**效力**：父目录 `design-decisions.md`（DD）> 父 `prd.md`/`design.md`/`implement.md`
> 本目录 `prd.md` > 本文件。**接缝与并发契约以 DD D18 为准**（它取代 design.md §3.2/§3.4 与 DD D3/D4 的草案）；
**批内顺序以 DD D8 为准**（先接缝、后拆分）。

对应需求：R1 / R2 / R7（部分）；AC1 / AC12。

---

## 起点（E1 已交付，不要重做）

- 契约单一定义：`WinForward.E2E.Contracts`（`ArmKeys` 11 分片、9 个 typed metrics record、`ArmParameters`、
  `JsonlSink`、`Rate`/`PerSecond`/`NumberFormat`）；`JsonValue`/`DictionaryMetrics` 已退役；
  字面量 gate + 形状测试（9 个 kind）就位；改名表 **9/9 satisfied**。
- 测试工程 173 用例；基线 `research/baseline/{run1,run2,jitter-band.json}`；比对工具四类判定 + 改名表判定。
- **已被 E1 关闭的 E2 零散项**：plan key 白名单、`JsonValue.Write` 的 `default:`（随类型删除闭合）。

---

## 批次总览

| 批次 | 内容 | 结束判据 | 提交 |
|---|---|---|---|
| **E2-a** | 零散修复（`TcpCommand` 兜底、`Target/Sockets.cs`、端口冲突、`MaxPayloadLength`）+ **`Client/Lanes/` 接缝（D18）** + `ReplyClassifier` 纯函数化 + `LatencyArm` 两 lane 迁移 + 5 处 `ConnectAsync` 机制统一 | 六条门禁 + `selftest` 绿 + `achievedRate`/`sendWouldBlock` 量级不变 + 结构差异为空、契约零越带 + `--strict` 读数摘要 + 并发测试与分配 gate（含反证） | 2a-1…2a-4 |
| **E2-b** | 7 个超标文件拆分 + Target 侧拆分 + `effective-lines.py` | **纯搬移**：结构差异为空、契约零越带；扫描脚本在三项目上无输出 | 1–2 |
| **E2-c** | `Cli/CommandLine.cs` 解析器合一 | 两个 verb 的 `--help` 与全部错误消息**逐字不变**（快照） | 1 |

每批次结束：`scripts/publish.sh` → `dotnet build WinForward.slnx -c Release`（零警告）→
`dotnet test tests/WinForward.E2E.Tests -c Release` → `selftest.sh` →
`dotnet format … --verify-no-changes --no-restore`（空输出）→ `jb inspectcode`（解析 XML，零 `<Issue>`）；
并跑 `compare-records.py`（带改名表）与 `--strict` 摘要，落 `research/baseline/E2*-vs-*.md`。

---

## E2-a：零散修复 + 传输接缝

### 1. 零散修复（低风险先做，各自小提交）

| 项 | 位置 | 判据 |
|---|---|---|
| `TcpCommand.Name` 去掉两个 `_ =>` 兜底改 `throw` | `Wire/TcpCommand.cs` | 既有"名字互不相同 + 不落兜底"用例继续绿；`rg '_ =>'` 在该文件无命中 |
| `Target/Sockets.cs`：统一 bind + Unix 显式清零 `SO_REUSEPORT` | 新建；三台 server 复用 | 残留实例再 bind → 响亮 `EADDRINUSE`（命令 + 证据） |
| 端口冲突：`dnsPort ∉ {tcpPort, udpPort}` | `Target/TargetOptions.cs`/`TargetRunner.cs` | 单测 + 一条命令（退出码 2） |
| `FrameCodec.MaxPayloadLength` 提 `internal` 并校验 plan | `Wire/FrameCodec.cs` + `Client/PlanFile.cs` | `payloadBytes` 超限 → 退出码 2，`'payloadBytes' is <v>, which is outside 0..<max>` |
| 比对工具：默认 summary 报**越带读数计数**（DD D17.4） | `scripts/compare-records.py` | 改一个延迟读数 → summary 计数变化、退出码不变 |

### 2. `Client/Lanes/`（**D18.1 的形状**）

- `ILaneTransport`（两个 adapter：`TcpLaneTransport` = Socket + `FrameStreamReader`；`UdpLaneTransport` = Socket + 数据报缓冲）；
  `LaneReceiveKind ∈ {Payload, EndOfStream, Malformed, IoError}`——**adapter 不解释线格式合法性**，
  `OpenAsync` 返回结果而非抛异常（connect 保护就在这里）。
- `ILanePolicy`：`BuildRequest`（返回 0 = 本槽不发，窗口准入在策略侧）、`OnSent`、`OnReceive`（接收线程：解码+分类+入队）、
  `Settle`（发送线程：结算）、`IsDrained`。
- `LaneEngine<TTransport>`：配速 + offer 循环 + 有界 `Defer` 队列 + `scheduleTruncated`；
  计数只含发送侧（`LaneCounts`），窗口/in-flight 住策略（`UdpLatencyState`）。
  **窗口策略只暴露 `Defer`**（Drop/Block 不留零消费者枚举；`DnsArm`/`LossArm`/`MixArm` 的窗口留在各自臂并登记为已知重复）。
- `LaneCounts` 与策略计数不相交的断言按 D18.1 的落地方式（策略计数去掉下划线暴露为属性）。
- **接收/结算线程契约按 D18.2**：分类在接收线程、`Settle` 在发送线程（`WaitUntil` 之后）、臂末尾顺序
  "停止 offer → 取消并 join 接收 → `Settle` 排空 → 算 gates/metrics"；**并发测试**必须有（N 轮，恒等式不破）。
- `ReplyClassifier` 纯函数，**只用于 UDP 三处**（LAT/LOSS/MIX）；TCP 保持 FIFO 匹配（D18.3）。
  差异清单（统一项/保持项）写进证据；`LAT` 补 `WasSent` 是**有意行为修正**，单独登记。
- **计数→派生量的接线表**（必须进证据）：引擎与策略的每个计数如何喂给现有 gates/notes/ceiling
  （含 `outstandingAtTeardown`、两个 ceiling、7 条 note），一个都不能丢。
- **性能契约**：分配 gate 用 fake transport 跑在**调用线程**（按 `hot-path.md` 开窗/配对），
  **含反证用例**（故意分配必须红）；`ValueTask` 复用手法在两个 lane 逐字保持；去虚化只做观测。
- 合并镜像函数时**注释取并集**（TCP 独有 4 条、UDP 独有 1 条）。
- pragma：引擎合并 LAT 的 4 处 → **目标 6 处**（DnsArm 两处留在臂内），完成时 `rg` 报实际数字。
- `DnsArm` **不并入引擎**（只共享 `Pacer`/`SocketOps`/有界 drain helper）；5 处 UDP `ConnectAsync` 落机制统一，
  行为变化（失败→计数+继续）在 E2 登记。

### 3. E2-a 的判据

- 六条门禁 + `selftest` 绿；
- `compare-records.py`：**结构差异为空**、**契约计数零越带**（含 `received`/`unmatchedReplies`/`inFlight`
  三个零宽契约量的逐值核对）；
- `--strict` 读数摘要进证据，越带项逐条解释；
- `achievedRate`/`sendWouldBlock` 与 A0 基线**量级相同**；
- 并发测试 + 分配 gate（含反证）绿。

---

## E2-b：文件拆分（纯搬移）

**严格有效行口径的当前值**（E2-a3 之后）：`LatencyArm` 471（已从 803 降下来）、`MixArm` 720、
`ReliabilityArm` 612、`DnsArm` 504、`ResourceSampler` 460、`PersistentArm` 449、`ClientRunner` 567。
先建 `benchmarks/WinForward.E2E/scripts/effective-lines.py`（去空行、去 `//` 与 `/* */`），AC1 判据 = 它在三个项目上无输出。

**E2-a3 check 转来的必做项**（不得丢）：

1. **补 `LatencyArm` 的 `outstandingAtTeardown` 求和覆盖**：删掉 `Outstanding += counts.DeferredPending;`
   目前 245/245 仍绿，且基线上该键处处为 0 ⇒ D18.5 #12 的逐值核对对它**是空转**。
   加一条测试直接驱动该求和（`AddCounts` 或等价接缝，必要时提 `internal`），断言"去掉 `DeferredPending` 项必红"。
2. **清理 `UdpLaneTransport.ConnectOk/ConnectTicks`**（无生产消费者；UDP lane 有意不记 connect 事实）——
   删除或在类文档里写明保留理由。
3. 拆 `LatencyArm` 时保持 D18.5 #12 的四个零宽键（`metrics/*.received`/`*.unmatchedReplies`/
   `*.outstandingAtTeardown`/`latency/*-rtt/count`）在拆分前后逐值相同。
- Target 侧：`SourceCensus.cs`、`TcpConnectionProtocol.cs`、`TcpAcceptLoop.cs`、`SocketIo.cs`、`ILedgerSection`；
- 判据：行为零变化（结构差异为空 + 契约零越带 + 迁移前后记录逐字节一致，除 D15 读数/身份类）；
  遵守 `directory-structure.md`（文件名=主类型名；不建 pass-through 别名层）。

## E2-c：Target 账本键族（D14.16 的闭合，**排在 E4 之前**）

- 新增 `ArmKeys` 分片：信封（`utc`/`label`/`type`）+ `tcp`/`udpSummary`/`dnsSummary`/`targetSummary`
  四个账本记录的键（当前 51 处 key 位字面量：`TcpTargetServer` 14、`UdpEchoServer` 14、`DnsServer` 13、
  `TargetRunner` 10——以 gate 同款正则实测为准）；
- 4–5 条**账本记录形状测试**（读生产写出的 `ledger.jsonl`，逐向比对键集/键序/三态）；
- 字面量 gate 的扫描面扩到 `Target/**`（`s_writerFiles` 加四个 writer）；
- 判据：账本字节等价（`compare-records.py` 的 ledger 组结构 0 差异，`target.out` 归一化 0 差异）；
- 登记项：新文件里"DTO 簇"的文件名（`ReliabilityAttempt.cs`/`TcpConnectionProtocol.cs`/`MixMetricsWriter.cs`、
  `ProcessSample.cs`）交 **E5 目录重排**时统一裁定（`directory-structure.md:89` 与"record+其消费者"簇的张力）。

## E2-d：CLI 解析器合一

- 抽 `Cli/CommandLine.cs`（已知选项集合 + `TryApply`），两个 verb 共用；`ClientOptions`/`TargetOptions` 的
  setter 需要重新加宽赋值入口（E2-b1/b2 已收窄）；
- 判据：`client --help`/`target --help` 与全部既有错误消息**逐字不变**（快照存 `research/cli-snapshots/`）；
  target help 补 E1 新增的退出码 1 说明属**有意变更**，单独登记。

---

## 证据与落盘

| 产物 | 位置 |
|---|---|
| 每批的归一化比对（含 `--strict` 读数摘要） | `../10-07-e2e-harness-refactor/research/baseline/E2{a,b,c}-vs-*.md` |
| 接缝的行为差异清单（统一/保持）与计数接线表 | 同上（E2-a 节） |
| 并发测试、分配 gate（含反证）、`--help` 快照 | `research/semantic-fixes/index.jsonl` 的 E2 条目 + `research/cli-snapshots/` |

## 风险与回退

| 风险 | 缓解 |
|---|---|
| 接缝污染热点路径 | 分配 gate（调用线程 + 反证）+ `achievedRate`/`sendWouldBlock` 量级对比 + selftest |
| 统一分类器抹平 TCP/UDP 差异 | D18.3 的差异清单 + 三个零宽契约量逐值核对 |
| 窗口/in-flight 双份真相 | 窗口只住策略；`LaneCounts` 与策略计数不相交的反射断言 |
| 拆分引入行为变化 | 纯搬移单独提交；结构差异为空 + 契约零越带 |
| 解析器错误文本漂移 | 快照逐字比对；不通过即回退该批 |

**回退点**：E2-a 的 4 个小批（2a-1 零散修复 / 2a-2 transport+engine / 2a-3 LAT 迁移+分类器 / 2a-4 收尾证据）、
E2-b、E2-c 各自一个边界。
