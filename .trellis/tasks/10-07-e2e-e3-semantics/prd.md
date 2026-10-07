# E3 测量语义修复

> **开工前裁定（2026-10-07）**：本文件的缺陷清单已被
> `../10-07-e2e-harness-refactor/design-decisions.md` **D0 重新基线**——13 条里约 10 条**已在代码树中实现**，
> 交付方式改为「premise re-verification + 回归测试 + 真缺口修复」。**冲突处以该文件为准**。

父任务：`10-07-e2e-harness-refactor`。逐条修复的落点见
`../10-07-e2e-harness-refactor/design.md` §5，执行步骤见同目录 `implement.md` 的「E3」节。
缺陷的权威描述与证据见
`../10-06-e2e-competitor-benchmark/research/harness-audit.md` 与 `arms-code-quality-audit.md`。

本子任务展开的父需求：R5 / R6 / R7 / R8；AC5–AC8 / AC16。

## 目标

修掉那些**会让数字失去意义**的缺陷。这份清单不是"锦上添花的清理"——`harness-audit.md` 把它们
标题为「跑批前必须修」，因为它们让测量结果不可信，而不是不精确：

审计的原始清单描述的是**更早的代码状态**：`#4`/`#5`/`#6`/`#7`/`#9`/`#10`/`#11`(LOSS 侧)/`#13`/`#15`
已在树里实现，`#14`/`#16` 半实现（逐条证据见 `design-decisions.md` D0）。因此本任务的实际工作是：
**premise re-verification（产出 `research/semantic-fixes/index.jsonl`）→ 为已实现项补回归测试 →
修真缺口**：`#8`（呈现与 caveat）、`#12`（四臂）、`#11` 的分析器消费、`#14` 的身份校验半、
F7/F8/F11、ODE 统一、`achievedRate` 统一、D7 记账半、`Truncated`、账本三字段。
**`harness-audit.md` 的原始叙事不得再作为"当前缺陷"引用。**

## 范围

- `harness-audit.md` §二 全部 13 条：**逐条给出结论**（已实现 → 回归测试 / 已修复 / 裁决不修），
  不是逐条重做（D0）。
- `harness-audit.md` §三 公平性 3 条（`#17`/`#18`/`#19`，落在分析器侧；与 E4 协同）。
- 矛盾语义统一：`ObjectDisposedException` 的四种策略、`achievedRate` 的两种口径。
- **`UdpReliabilityTracker` 的并发契约**（放在本任务最前面，因为多条修复依赖它的数据结构：
  窗口槽位按到期时刻释放、发送位图、重排按已发送最高序号）。
- **D7 的记账半**（Tier 0 的止血已在 E1 完成）：越界槽**既不在 `sent` 里、也不在任何桶里**，
  于是 `supplied` 与 `sent` 的差值变大。需要定：这些槽在 `sent`/`supplied`/`clientSendLoss`
  之间怎么算，并让 `gates.clientSendLoss` 的语义与新口径一致。
- **`FrameStreamReader` 新增 `Truncated` 状态**（EOF 丢半帧今天被读成「干净 half-close」，
  target 还会真的写 trailer）。这是新增一条 verdict 路径，单独成阶段做。
- 其余改变数值的 bug：`JsonValue.Write` 的 `default:` 改抛（E2 做，本任务复核）、
  `LossArm`/`MixArm` 的接收侧错误分类统一。

**已由 E1/E2 完成的 bug（本任务只复核，不重复实现）**：D1–D7 的止血半、顶层不兜异常、
`PlanFile` 数值键校验与 `TryReadInt` 强转、`selftest.sh` 漏参数、`TcpCommand` 兜底、
`LedgerWriter` catch 范围、SO_REUSEPORT、端口冲突校验、`MaxPayloadLength`、
plan key 白名单、UDP `ConnectAsync` 保护。

**记录但不修**：`ReliabilityArm.cs:602-606` 把取消写成 `ConnectFail`（两份审查结论冲突，
且未构造出可达实验）。登记到 `research/` 供后续裁决。

## 不在范围内

- `harness-audit.md` §四「只需在报告里如实披露」的条目（唯二例外是 `#19`）。
- 文件拆分与接缝（E2，本任务的前置）。
- 分析器重写（E4）；但 §三 三条需要分析器有能力承载体现在表格里的标注，
  若 E4 尚未开始，先把标注规则写进设计与 `RowProfile` 的数据里。

## 依赖

E2 必须先完成：多数修复只落在 `LaneEngine` 与 `UdpReliabilityTracker` 两处，前提是它们已经存在。
结构没理清之前改逻辑，会在六个镜像函数里各改一遍。

## 验收标准

**先证据，后代码**。每条修复一份定向证据，落盘
`../10-07-e2e-harness-refactor/research/semantic-fixes/`。不接受"看起来对了"。

- [ ] `UdpReliabilityTracker` 的并发契约文档化，并由并发测试证明（发送与接收并发 N 轮，恒等式不破）。
- [ ] `#4` 仿真证据：0/10/50% 丢包下 `sent == supplied` 且发满全程。
- [ ] `#5` 溢出槽位场景下 `never` 不含从未发出的序列。
- [ ] `#6` **机械判据**：遍历 11 份 plan（`plans/` 6 + `plans-short/` 5），凡 `kind ∈ {loss,mix,base}`
      的 arm 必须存在 `lossWindowMs`（缺失即失败并打印 plan 路径 + arm 名）；`metrics.window` 等于声明值。
      `dns-plan`（无 loss/mix 臂）与 `base-plan`（由 `ControlArm` 传递）不声明属正常。
- [ ] `#7` 人为制造客户端阻塞时，`late`/`never` 反映阻塞而非被减掉。
- [ ] `#8` LATLOAD 在 500 rps 下 `windowOverflow == 0`；`windowOverflow > 0` 输出 `measurement-caveat`
      且该臂延迟格子 `n/a (windowOverflow > 0)`（**不改 gate 为"作废"**，见 D9；同步改 `README.md:550`）。
- [ ] `#9` TCP 直方图样本数较修复前上升（臂末尾在途请求被采样）。
- [ ] `#10` 穷举 1..6 的到达顺序：`(1,3,2)` 的重排计数为 1（今天恒为 0）。
- [ ] `#11` 三个恒零计数器逐条「接通」或「从输出删除」，不留无法移动的计数器。
- [ ] `#12` **四个臂**（`IdleArm`/`DnsArm`/`ThroughputArm`/`ReliabilityArm`）的 `clientSendLoss`
      由各自计数器派生，每个 gate 都能真的失败；另四臂已派生（`LossArm.cs:43` 为参照），**不得改动**。
- [ ] `#13` 复核全部比率调用点：零发送场景下为 `null` 而非 `0`。
- [ ] `#14` 仿真一次产品重启：CPU 不再出现负数；采样记录带 PID + `StartTime`。
- [ ] `#15` 乱序/错配的 DNS TCP 响应不再记成 `answered`。
- [ ] `#16` BASE 参数来自 plan；每轮产品块前后各跑一次；下限值与 LOSS 数字并列发布。
- [ ] `#17`/`#18`/`#19` 在分析器表格与脚注中落实（泄漏的 UDP 行标 `not carried`；DNS 路径差异标注；
      CPU 口径显式披露为「仅用户态」）。
- [ ] **D7 记账口径已定并有证据**：越界槽在 `sent`/`supplied`/`clientSendLoss` 之间的归属明确，
      `supplied` 与 `sent` 的差值变化可解释，`gates.clientSendLoss` 的语义与新口径一致。
- [ ] **`FrameStreamReader` 的 `Truncated`**：EOF 落在帧中间时不再被读成干净的 half-close；
      两个 server 各自记账；`selftest` 确认无误报。
- [ ] `ObjectDisposedException` 只有一种语义（teardown 不产生数据点）；`achievedRate` 只有一种口径
      （成功发出的请求/秒），PERSIST 的完成口径改名 `completionRate`。
- [ ] `dotnet build`/`dotnet test` 零警告且绿；`scripts/selftest.sh` 绿。

## 批次划分

按父 `implement.md` 的 **C1–C8 提交图**分组提交（旧文"每条修复一个提交"作废，见 D8）：
组内不可分，每组结束时 build/test/selftest 绿；改变数值的组在 commit message 里引用
`research/semantic-fixes/index.jsonl` 的条目。

> **权威规则**：父 `prd.md` 的 R/AC 是本子任务的**验收上限**；本文件的清单是它的展开，
> 冲突时**以父为准**，且本文件每条验收都必须能追溯到父的一个 R 或 AC。
> 父任务：`.trellis/tasks/10-07-e2e-harness-refactor/`。
