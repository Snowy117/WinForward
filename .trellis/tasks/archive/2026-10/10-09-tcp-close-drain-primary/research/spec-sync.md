# Spec 同步 —— close drain 已实现、手工 clean-end FIN 正在删除

> 对象：`.trellis/spec/backend/` 的三份权威文档 + 同族四份被 drain 推翻陈述的修正（另加本文件）。
> 依据：`research/code-check.md` §3-F3 / §4 / §2-14 / §5-19、`research/drain-verification.md`、
> 任务 `design.md` §3/§6/§9 与 `implement.md` C3/E1，以及**当前工作树**上 drain 实现与 removal 的实际状态
> （removal 在写作期间并行落盘；成文后按 post-removal 树复核：`TryBuildFin`/`TryInjectClientCloseAsync`/
> `InjectClientVisibleCloseAsync` 已从 `src/` 消失，clean-FIN facts 已从 `TcpRelayEndCloseTests` 删除）。
> 约束：只写 spec（+ 本文件）；不引用 removal 后不存在的符号；不写任务编号/日期；英文；单文件 ≤400 行。

## 0. 结论

- 主改三份 + 同族修正四份：`tcp-client-close-injection.md`、`tcp-local-redirect.md`、
  `warm-path-dispatch.md`、`tcp-relay-lifecycle.md`、`tcp-redirect-teardown-grace.md`、
  `tcp-redirect-transform.md`、`index.md`。
- 三份主改文档现在只引用 removal 后仍存在的符号：`TryInjectClientResetAsync`、
  `TcpResetBuilder.TryBuildReset/TryBuildResetFromSyn`、`DrainCleanEndAsync`、`ArmDrainAsync`、
  `ObserveClientAck`、`TrackClientAck`、`TryComputeDrainTargetAck`、`RelayPhase.Draining`、
  `ITcpRelayEndInfo`/`ServerStreamBytes`、`tcp.redirect.drain`、`TcpCloseDrainTests`。
  `TryBuildFin` / `TryInjectClientCloseAsync` / `TryInjectClientCloseCoreAsync` /
  `InjectClientVisibleCloseAsync` / `tcp.redirect.clientClose` 在 spec 目录零命中。
- 所有引用的测试名都对得上当前树（post-removal）：`TcpCloseDrainTests` 的 **12** 条 facts（含 removal
  新增的 `CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges`）、
  `TcpRelayEndCloseTests.FaultedRelayEndInjectsInWindowClientResetBeforeTeardown` /
  `StalledRelayEndInjectsClientResetBeforeTeardown`、
  `TcpProxyRelayTests.RelayReportsTheServerStreamBytesItWroteToTheClient`、
  `SequenceTrackerTests.TcpRedirectAssociationHoldsNoLockField`、
  `PacketPathWalkCountTests.RedirectPacketTakesZeroSequenceGateEntries`、
  `TcpRedirectWarmPathGateTests.TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads`。

## 1. 逐文件改动

### 1.1 `tcp-client-close-injection.md`（主改，145 → 166 行）

| 改前 | 改后 |
|---|---|
| 标题/Scope 只讲 "close injection" 的 RST/FIN 两种形状 | 标题 `TCP Client-Visible Close: Abort Injection and Clean-End Drain`；Scope 覆盖「异常端 crafted RST\|ACK + clean end 的有界 drain」 |
| `## The two shapes`，表含 Abort(RST\|ACK) 与 Close(FIN\|ACK) 两行 | `## The abort shape (RST\|ACK)`，表只留 Abort 一行（`0x14`，seq/ack 推导不变）；FIN 行的「delivered byte count wins for the close's sequence」整段删除 |
| `## Sequence tracking (F4, 2026-09-30)` | `## Sequence and acknowledgement tracking (F4, 2026-09-30)`：新增 `TrackClientAck` → `ObserveClientAck` → `ClientAckMax` 段落（ACK 控制位 `0x10` 门、advance-only 串行最大值、`ReinjectExistingFlowDataAsync` 上 **not only while draining**、与 sequence trackers 相同的 layout+span twin 形状） |
| `## Every relay end injects its client-visible close before the retire` + 「immediate retire」+ 指向 `10-07-tcp-close-drain` 的 Residual 段 | `## A clean end drains the close handshake instead of injecting a FIN`：触发（仅 `CleanEnded`）、顺序（compute target → dispose relay → `ArmDrainAsync` → wait → retire）、target 公式 `ServerInitialSeq + 2 + ServerStreamBytes`（listener-side ISN + SYN-ACK + FIN + delivered）、退化（缺 SYN 模板/serverISN/end-info → 不 drain、立即 retire）、单飞 arm + `RelayPhase.Draining`（数据路径不读 phase）、三个出口 `acknowledged`/`deadline`(5 s 默认，`TcpRedirectAcceptor` 可注入)/`retired` 与 `tcp.redirect.drain` 字段、retire 仍是 store gate 内单一步骤（resolve-then-retire）。LOCKED BY = `TcpCloseDrainTests` 12 条 facts（含 removal 判别器 `CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges`）+ 保留的 RST facts |
| 其余章节（relay setup failure、capacity RST、fragments、injection-failure exits） | 原文保留（RST 上下文，drain 未改变它们） |

### 1.2 `tcp-local-redirect.md`（hub，117 → 119 行）

- Pipeline 的 **Close 行**：由「RST\|ACK for Stalled/Faulted, FIN\|ACK for CleanEnded」改为「crafted RST\|ACK for Stalled/Faulted；CleanEnded 走 bounded drain（释放 relay、alias 保活到客户端 ACK 掉栈自己的 FIN、然后 retire）」。
- Cross-cutting invariant：由「Every relay end injects its client-visible close before the retire」改为「**A relay end does not retire until its client-visible close has landed**」（异常端 RST 先行；clean end 等 ACK/deadline/其它 retire 路径）。
- Topic map 行：改为「abnormal-end RST\|ACK shape + clean-end drain 的 target/exits + capacity resets + fragment teardown + injection-failure exits」。
- `Where things moved` 冻结行保留旧标题，括号里的 `(same heading)` 改为指向两个新标题（旧 citations 仍可解析）。

### 1.3 `warm-path-dispatch.md`（205 → 215 行）

- `## Sequence trackers are atomic, not locked`：补上 ack tracker（`TrackClientAck` → `ObserveClientAck`）与同一 CAS-max 形状。
- 「The association holds no reference-typed instance field (no `Lock`)」→「holds exactly **one** reference-typed instance field: the close drain's completion cell, allocated once per clean end and null while no drain is armed」；零 gate / 无 per-association lock / 无 per-packet allocation 的结论保留。
- 点明两个反射测试的新断言形式：`Assert.Single(fields, f => !f.FieldType.IsValueType)` 式断言「恰好一个非值类型字段（drain cell），fresh association 上为 null」——`SequenceTrackerTests.TcpRedirectAssociationHoldsNoLockField` 与 `PacketPathWalkCountTests.RedirectPacketTakesZeroSequenceGateEntries`；**不写字段名**（spec 不把私有名变成契约）。
- 零 gate 的结构性保障改为由 `TcpRedirectWarmPathGateTests.TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` 的实测承担：forward 包（带 ACK tracker）0 gate entries / 0 clock reads / 0 reverse probes，reverse 包恰好 +1 reverse probe。

### 1.4 `tcp-relay-lifecycle.md`（184 → 190 行）

- `## What a relay end is`：acceptor 的映射由「RST\|ACK for non-clean, FIN\|ACK for clean」改为「crafted RST\|ACK for non-clean；clean 走 bounded drain（relay 先释放、栈自己的 FIN 由 live alias 承载、retire 等客户端 ACK）」。
- `## Dispose ordering and single-flight (D11)`：补一条——clean end 上 acceptor 的 drain 是**第一个 dispose 认领者**，store 之后的 release 只 join 同一份单飞 teardown（不双 dispose、不重复 `tcp.redirect.closed`）。这是 code-check §4 明确要求的一句。
- `## The accept loop owns the session lifetime`：`ObserveRelayCompletionAsync` 的描述由「injects the client-visible close」改为「delivers the close — RST for abnormal, bounded drain for clean — 然后 tear down」。

### 1.5 `tcp-redirect-teardown-grace.md`（154 → 155 行）

- Scope 行：「The close **frame** a relay end **injects** *before* its retire」→「The close a relay end **delivers** *before* its retire — crafted RST / bounded drain of the stack's FIN」。
- Grace 段的「it covers the client's final ACK and common FIN retransmissions」→「post-retire ACK and common FIN retransmissions」：clean end 的**第一个** final ACK 现在由 drain 在 retire 前消费，tombstone 只兜 retire 之后的 straggler。
- Good/Base/Bad 的 Good 例同步改为「post-retire straggler（重传 FIN、或错过 drain 窗口的 ACK）命中 tombstone」。
- 保留不动：atomic retire、单 tombstone 写入点，以及「same-tuple SYN 不得触到将死的 listener」这条规则和它的历史理由——drain 只是把「先 resolve 后 retire」变成常态，规则本身仍成立。

### 1.6 `tcp-redirect-transform.md`（同族，本次新发现）

- immediate-send 清单里的「the relay-end/**FIN** close」改为「the relay-end **abort**」：removal 之后 clean end 不再注入任何包，`ClientResetInjector` 的 reset 只剩异常端 abort / capacity / fragment / injection-failure 四种 RST。
- TFO 段的「the close templates are header-only」改为「the reset templates are header-only」（同因）。

### 1.7 `index.md`

- TCP Local Redirect 家族表里 Client Close Injection 一行：由「The RST\|ACK and FIN\|ACK shapes, their sequences...」改为「The abnormal-end RST\|ACK shape, its sequences, the clean-end close drain, and what a failed injection does」。

## 2. rg 自查清单（`.trellis/spec/backend/`）

| 词 | 命中 | 处理 |
|---|---|---|
| `FIN\|ACK`（字面量） | **0** | 三处旧契约全部清除（close 文档表/Scope、hub Close 行、index 行） |
| `FIN|ACK`（正则并集：大写 FIN 或 ACK，rg 输出 62 行含文件名标题行） | 全部为合法上下文 | `RST\|ACK`（abort / capacity，属保留的 RST 上下文）、协议散文的 `SYN-ACK`、ACK tracker 段落、`post-retire ACK/FIN retransmissions`；**没有**任何 clean-end 的 `FIN\|ACK` 契约 |
| `TryBuildFin` | **0** | — |
| `TryInjectClientCloseAsync` | **0** | — |
| `TryInjectClientCloseCoreAsync`（额外） | **0** | — |
| `InjectClientVisibleCloseAsync`（额外） | **0** | — |
| `clientClose`（含 `tcp.redirect.clientClose`） | **0** | — |
| `10-07` | 1 | `measurement-judgement.md:105` 引用归档路径 `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/record-normalize.json`（**已核实存在**）。这是另一个 10-07（E2E harness 重构），与 close drain 草稿无关，保留 |
| `immediate retire` | 1 | `tcp-client-close-injection.md:75` 的退化规则「no drain and an immediate retire」——新契约明确要求（缺 template/serverISN/end-info 时不 drain、立即 retire），保留 |
| `no reference-typed` | 1 | `packet-shape-and-flow-identity.md:17` 说的是 `FlowKey`（仍无引用类型字段），不是 association，保留 |
| `reference-typed instance field` | 1 | `warm-path-dispatch.md:109` 的新规则「exactly one reference-typed instance field: the close drain's completion cell」，即本次要求的改法 |

## 3. 与 `code-check.md` 建议不一致、或报告没提的地方

1. **§3-F3 建议 1 的「先由 relay 的 graceful close 发出真 FIN（先 dispose relay、再 arm drain）」措辞与报告 §5-19 自相矛盾。** §5-19 已核实真 FIN 由 `RunPumpAsync` 自己的 `await using` socket close 在 `Completion` 完成**之前**发出，acceptor 的 dispose 不是为了造 FIN、而是为了在 handshake 一开始就释放上游/控制连接。spec 采用 §5-19 + `design.md` §3 的说法：**dispose 前置于 arm 是上游预算的理由，FIN 早已在线上**。
2. **§3-F3 建议 2 的措辞「no eagerly-allocated per-association object」没有采用。** 两个反射测试断言的是「恰好一个非值类型字段 + fresh association 为 null」，spec 据此写「exactly one reference-typed instance field: the close drain's completion cell, allocated once per clean end and null while no drain is armed」；「eagerly-allocated object」没有对应的断言，写进 spec 会变成无人守的规则。
3. **§2-16 说反射测试硬编码 `_drainCompletion` 会在重命名时脆断。** spec 因此**不写字段名**，只写断言形式（恰好一个非值类型字段、未武装为 null），避免 spec 跟着实现名一起脆断。
4. **§3-F3 建议 3 说「删除指向 10-07 的指针」在 `tcp-local-redirect.md`——指针实际只在 `tcp-client-close-injection.md`**（报告 §5-18/N4 自己也纠正了归因）。hub 只改了 Close 行与不变量，没有 10-07 命中。
5. **`design.md` §6/§9 说「`TcpRelayEndCloseTests.cs` becomes the drain test suite」与落盘不符**：drain suite 是新文件 `TcpCloseDrainTests.cs`，`TcpRelayEndCloseTests` 保留 end-kind 与 RST facts。Locked by 按实际拆分写（`TcpCloseDrainTests` + 保留的 RST facts），没有照抄 design 的措辞。
6. **§2-12 的 F2（`TrackClientAck` 不在 0 B 分配门内）在当前树已被补上**：`TcpRedirectDataPathBenchmarks` 的 forward 行已含 `TrackClientAck`（host / forwarded / drain-armed / 独立门各一处）。spec 的 warm-path 结论只声明 **0 gate entries / 0 clock reads / 1 reverse probe**（由 `TcpRedirectWarmPathGateTests` 承担），没有把分配门写成本次已证的契约。
7. **报告 §4 未提、本次新发现的被推翻陈述两处**：`tcp-redirect-teardown-grace.md` 的「final ACK 命中 tombstone」与 `tcp-redirect-transform.md` 的「relay-end/FIN close 是 immediate single send」。前者是 drain 改变了「谁的 ACK 走 tombstone」，后者在 removal 后直接不再存在该注入——都已修正。
8. **明确核查后判定「无冲突、不改」**：`idle-expiry-sweep.md`（sweep 只退休 `Redirecting`，drain 期间 session hold 成立）、`hot-path.md`（无新分配/新 gate 契约）、`async-lifetime.md`（无新 CTS，deadline 是 `WaitAsync` 内部定时器）、`packet-shape-and-flow-identity.md`（FlowKey 无引用字段，与 association 无关）、`error-handling.md` / `tcp-syn-setup-admission.md`（其中的 RST\|ACK 都是 capacity 或 relay-failure 的保留 RST 上下文）。

## 4. 未决 / 交接

1. **removal 已在写作期间落盘，第 2 节的 rg 已在 post-removal 树上复跑**：`TryBuildFin` / `TryInjectClientCloseAsync` / `TryInjectClientCloseCoreAsync` / `InjectClientVisibleCloseAsync` / `TcpFinAck` 在 `src/` 已消失，spec 里零命中；clean-FIN facts 已从 `TcpRelayEndCloseTests` 删除，被引用的两类 RST facts 与全部 end-kind facts 仍在。removal 新增的 `TcpCloseDrainTests.CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges` 已补进 Locked by（12 条 facts）。
2. `TcpCloseDrainTests` 的 `order.Steps` 期望已在 removal 后变为不含 clean-end 的 `"inject"`（`["dispose"]` → `["dispose", "teardown"]`），与 spec 的 dispose → wait → retire 描述一致；若后续再改名/合并 facts，Locked by 需要跟着更新。
3. 文档文件名仍叫 `tcp-client-close-injection.md`，而主题现在是「abort injection + clean-end drain」。为保住 hub / topic map / `Where things moved` 与 `benchmarks/results/**` 的冻结链接，本次不改名；若将来把 family 再拆，改名要同步那张 moved 表。
4. `tcp.redirect.drain` 已按 `logging-guidelines.md` 的口径核对（Debug、`EventName` 点分、自然句、字段全在模板里），该文档不枚举事件，无需改动。
