# E2 结构与传输接缝

> **开工前裁定（2026-10-07）**：接缝形状以 `../10-07-e2e-harness-refactor/design-decisions.md` **D3/D4** 为准
> （`ILaneChannel` 取消，改为 `ILaneTransport` + `ILanePolicy` + `LaneEngine`；`ReplyClassifier` 为纯函数；
> `DnsArm` 不并入引擎；并发契约提前定稿）。本文件的旧措辞已就地修正。

父任务：`10-07-e2e-harness-refactor`。详细设计见
`../10-07-e2e-harness-refactor/design.md` §3（传输接缝）与 §4（文件拆分），
执行步骤见同目录 `implement.md` 的「E2」节。

本子任务展开的父需求：R1 / R2 / R7（部分）；AC1 / AC12。

## 目标

把 7 个违反仓库「每 .cs 有效行 ≤ 400」规范的文件拆开，并把 TCP/UDP 的镜像实现收敛到一条接缝上。

**这不是审美问题**：`LatencyArm` 的 TCP/UDP 发送三件套（TCP `442–550`、UDP `673–775`）归一化后
仍有 18 行差异、非注释 7 行；差异被复制在多个地方意味着**每一条测量规则的修正都要改多遍**，
而漏掉的那一遍不会有任何提示（`LatencyArm` 的回复校验阶梯已经漏掉了 `WasSent` 一步）。
**合并时注释取两侧并集**（TCP 独有 4 条、UDP 独有 1 条，是 README「Non-obvious properties」的代码侧依据）。

## 范围

- `Client/Lanes/`：`ILaneTransport`（**两个 adapter**：frame TCP / datagram UDP）+ `ILanePolicy`
  （构建请求字节 + 解释回复）+ `LaneEngine<TTransport>`（配速 / 在途窗口 / 发送 / 接收调度 / 有界 drain）。
  **`ILaneChannel` 取消**——它的签名把 `FrameCodec` 的帧假设写进了"transport 接缝"，DNS 无法实现（D3）。
- `Client/Lanes/ReplyClassifier.cs`：**纯函数**（不吃 `UdpReliabilityTracker`），返回
  `ReplyVerdict(Kind, Sequence, PayloadBytes, FrameDecodeError)`；调用者自己记账（D3）。
  并且它**不是纯搬移**：给 `LatencyArm` 补 `WasSent` 是有意的行为修正，要单独登记。
- `LatencyArm` 的 TCP/UDP 两个 lane 改用引擎。**`DnsArm` 不并入引擎**（TCP 相是长度前缀流，是第三个
  线格式；只共享 `Pacer`/`SocketOps`/有界 drain helper）。**`LossArm`/`MixArm` 不走引擎**
  （窗口模型是 `UdpReliabilityTracker` 的槽位），只共享 `ReplyClassifier`。
- 引擎接收侧按 **D4 的并发契约**一次定稿（发送线程独占 book + 接收投递队列 + 显式 drain 顺序）。
- **至少 5 处** UDP `ConnectAsync` 移进 `LaneEngine.OpenAsync`（含容易漏的 `MixArm.cs:457`）。
- 拆 7 个超标文件（`design.md` §4 的表）。
- Target 侧拆分：`SourceCensus.cs`、`TcpConnectionProtocol.cs`、`TcpAcceptLoop.cs`、`SocketIo.cs`、
  `ILedgerSection`、`Cli/CommandLine.cs`。
- 零散修复（低风险先做）：`TcpCommand` 兜底改 `throw`、`LedgerWriter` catch 范围、
  `Target/Sockets.cs`（SO_REUSEPORT）、端口冲突校验、plan 白名单、`MaxPayloadLength` 提为 `internal`。

## 不在范围内

- 改变测量数值的语义修复（E3）。本子任务的目标是**行为等价**（唯一例外是 `ReplyClassifier`
  统一后 `LatencyArm` 补上 `WasSent`——该变化登记在 E3 的清单里）。
- 分析器（E4）。

## 依赖

E1（契约与安全网）必须先完成：拆文件时字段名已经在 `ArmKeys` 里，搬移不会把字符串字面量再散布一次；
且纯函数测试是本子任务的安全网。

## 验收标准

- [x] `Client/Lanes/` 就位；`LatencyArm` 的六对镜像函数消失；TCP 与 UDP 共用同一发送/接收/统计路径。
- [x] 发包路径**零额外分配**（Release 分配 gate 单测：fake transport 跑 N 次发送，
      `GC.GetAllocatedBytesForCurrentThread()` 增量 0）；`send.IsCompleted` 的 `ValueTask` 复用手法
      在引擎覆盖的两个 lane 逐字保持。去虚化是**观测项**，不作为验收断言（D3）。
- [x] `Pacer` 的 pragma 与注释随代码搬进引擎：E2E 内 pacing 家族 **9 处**（`ArmContext.cs:73` 定义体
      + 8 个同步调用点），引擎范围 4 处，**合并后目标 6 处**（不是 1 处）。它们必须随代码走，
      否则 quality gate 会红。
- [x] **本任务范围内的三个项目**（`WinForward.E2E` / `.Contracts` / `.Analysis`）每个 .cs 有效行
      （非空非注释）≤ 400，扫描脚本无输出。**不要**全扫 `benchmarks/`——`WinForward.Benchmarks/`
      有 3 个既有违规（909 / 684 / 410 有效行），它们不在本任务范围内（已登记为已知债务，见 E5）。
- [x] 三台 Target server 的重复收敛：bind / accept / `SendAll` / `ReadExact` / 账本段落各有唯一实现。
- [x] `Sockets.cs` 在 Unix 上显式清零 SO_REUSEPORT；残留实例再 bind 时**响亮地** `EADDRINUSE`。
- [x] `dotnet build WinForward.slnx -c Release` 零警告；`dotnet test tests/WinForward.E2E.Tests -c Release` 绿。
- [x] `scripts/selftest.sh scripts/plans/selftest-plan.json` 绿。

## 批次划分（顺序经审核修正：**先抽接缝，再拆文件**）

原稿写的"先纯搬移、再抽接缝"是**反的**：`design.md` §4 的拆分表明确说它描述的是**P3 之后**的形态，
按当前文件内容切会拆出很快空掉的文件（例如 `LatencyPlan.cs` 一栏里今天就塞着将被 `LaneEngine`
吃掉的 `DeferredQueue`）。正确顺序：

| 批次 | 内容 | 判据 |
|---|---|---|
| 2a | `Lanes/` 接缝 + 零散修复（含 `ReplyClassifier`） | selftest 绿 + `achievedRate`/`sendWouldBlock` 量级不变 |
| 2b | 7 个文件与 Target 侧**纯搬移**（行为零变化） | 归一化记录比对为空 |
| 2c | `Cli/CommandLine` 解析器合一（**重构而非搬移**） | 两个 verb 的 `--help` 与错误消息**逐字不变** |

任一阶段失败可回退到该批次边界。

> **权威规则**：父 `prd.md` 的 R/AC 是本子任务的**验收上限**；本文件的清单是它的展开，
> 冲突时**以父为准**，且本文件每条验收都必须能追溯到父的一个 R 或 AC。
> 父任务：`.trellis/tasks/10-07-e2e-harness-refactor/`。

---

## 完成记录（2026-10-07）

- 批次：**2a-1**（名字表兜底/`Target/Sockets.cs`/端口冲突/`payloadBytes` 上界/越带读数计数）、
  **2a-2**（`Client/Lanes/` 接口 + 引擎 + fake transport 测试）、
  **2a-3**（真实 transport + `ReplyClassifier` 纯函数 + `LatencyArm` 两 lane 迁移 + 5 处 connect 机制）、
  **2b1/2b2**（7 个超标文件 + Target 侧拆分，`effective-lines.py` 在三项目无输出 ⇒ **AC1 达成**）、
  **2c**（Target 账本键族 `ArmKeys.Ledger` + 形状测试 + gate 扩容）、
  **2d**（`Cli/CommandLine.cs` 合一 + 28 条命令快照）。
- 每批都经「impl → 独立 check（含突变验证）→ 修复 → 提交」；六条门禁每批全绿；测试 188 → **264**。
- 证据：`../10-07-e2e-harness-refactor/research/baseline/E2{a1,a2,a3,b1,b2,c,d}-vs-run1.md` 与
  `E2{a2,a3,d}-check.md`、`research/cli-snapshots/**`、`research/semantic-fixes/index.jsonl`（E2-* 条目）。
- **行为等价**：每批 `compare-records.py` 结构差异 0、契约零越带、改名表 9/9；拆前/拆后差异 ≤ 同一二进制
  噪声地板；D18.5 #12 的 34 个零宽 (记录,路径) 对在多次运行间逐值相同。
- **登记的有意变更**（只在病态路径可观测）：LAT 的 `WasSent` 检查、connect 失败不再打死整臂、
  同步抛 `SocketException` 计数+继续、超长 UDP 数据报显式命名、TCP `BadChecksum` 落 corrupt 而
  `BadMagic`/`BadLength` 落 protocolErrors 并终止、target help 补退出码 1 一行。
- **交接到后续批次**：Target 账本已闭合（2c）；DTO 簇文件名与 `ProcessSample.cs` 由 **E5** 目录重排裁定；
  gate 的"新 writer 需手工进 `s_writerFiles`"与字符串字面量假阳类登记在 DD D18.7。
