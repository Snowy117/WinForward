# 代码质量复核 — TCP close drain

> 复核对象：主工作树 `/home/paff/Projects/WinForward` 上**未提交**的「TCP close drain」实现
> （`git status`：`src/WinForward.Runtime/TcpRedirect/` 4 个文件 + `Logging/TcpRedirectLog.cs` 修改，
> `tests/WinForward.Runtime.TcpRedirect.Tests/` 3 个文件 + `tests/WinForward.Integration.Tests/PacketPathWalkCountTests.cs` 修改，
> 新增 `tests/WinForward.Runtime.TcpRedirect.Tests/TcpCloseDrainTests.cs`；任务文档 `.trellis/tasks/10-09-tcp-close-drain-primary/` 未跟踪）。
> 复核方式：**只读**。没有修改任何既有文件；本文件是唯一新增产物，位于任务自己的 `research/` 目录。
> 复核依据：任务三件套（`prd.md` / `design.md` / `implement.md`）、`research/implementation-notes.md`、
> `research/ac0-residual-classification.md`、`research/verification/ac0/`、父任务
> `archive/2026-10/10-06-tcp-half-close-fidelity/research/verification/README.md`，
> 以及 `.trellis/spec/backend/` 的 tcp-local-redirect 家族、hot-path、warm-path-dispatch、async-lifetime、
> logging-guidelines、test-stability、quality-guidelines、idle-expiry-sweep、directory-structure、error-handling
> 与 `.trellis/spec/guides/` 两份指南。
> 环境：2026-10-09 18:2x（UTC+8），load average 0.5–1.9；同一主机上有 `WinForward.E2E`（VM 实验的 publish/编排）
> 进程在跑。下文每条命令证据都注明了当时的并发情况；按 test-stability.md §2.9 的口径，与重负载重叠的失败
> 才会被判为「未证实」，本次所有跑到的测试都是绿的。

---

## 0. 结论摘要

- **Blocker：0。** 未发现会在生产上产生错误行为、资源泄漏、死锁或数据损坏的缺陷。
- **Major：3**
  - **F1**（验证计划阻塞，非运行时缺陷）：`implement.md` C1 / `design.md` §6 要求的**内部 A/B 开关没有落地**，clean-end 的 crafted FIN|ACK 仍是无条件注入，drain-only 臂无法按计划运行 → AC4 无法执行，AC1 的「证伪 §3 使能假设」被混淆。
  - **F2**（热路径证明缺口）：新的每包调用 `TrackClientAck` 没有被 `TcpRedirectDataPathAllocationGateTests` 的精确 0 B 门覆盖（benchmark forward 行仍只跑 `TrackClientSequence`），R6 的零分配承诺目前只有「肉眼证明」。
  - **F3**（spec 未同步）：`warm-path-dispatch.md`「association 不持有引用类型实例字段」与 `tcp-client-close-injection.md`「immediate retire」两处权威文档与落地代码相反；实现者已自报为 Phase E 待办，但 drain 提交必须带上它。
- **Minor：5（F4–F8），Nit：5（N1–N4、N6）。**
- **放行判断：有条件放行**（详见 §0.1）。

### 0.1 能否进 removal 与提交

**可以有条件放行。** 理由：

1. 运行时语义没有找到 blocker：内存序握手无丢唤醒（§2-1），retire 的原子性、tombstone、injection failure、shutdown 取消路径都与 design §3 不变量一致（§2-8/9/10/11）。
2. 但 Phase D 的验收证据在当前形态下**不可采信**：只要 crafted FIN 还在按默认路径注入，每个 clean end 都会先收到一个单发 FIN|ACK，客户端通常在 1 个 RTT 内 ACK 它并立刻结束 drain——VM 臂测到的是 crafted FIN 的效果，不是实栈 FIN + 重传 + drain 的效果（AC1 的 `timeout==0` 会被 F1 伪证）。
3. 因此建议的放行条件：
   - **进入 removal（C3）前**：补上 design §6 的 A/B 开关（或与用户重定 AC1/AC4 的措辞与判据），让 drain-only 臂可运行；
   - **提交 drain 之前**：把 `TrackClientAck` 补进 forward benchmark 行/0 B 门（F2），并同步三处 spec（F3）；
   - Phase C3 的 removal 提交按设计单独成 commit，回滚语义不变（design §10）。
4. 若坚持「先提交 drain、开关留给 Phase D」：代码可以提交（drain+FIN 形态是安全的中间态，不会让现状变差），但**任务不能以 AC1/AC4 的当前判据收口**，必须在任务记录里写明这一混淆。

---

## 1. 复核方法与证据清单

| 命令 / 动作 | 结果 |
|---|---|
| `git status --porcelain`、`git diff HEAD` | 8 个已跟踪文件修改、1 个新测试文件；工作树未提交 |
| `python3 tools/effective-lines.py --all <9 个改动文件>` | exit 0；最大 `TcpRedirectTable.cs` 370 行（上限 400），`TcpCloseDrainTests.cs` 337，其余 ≤301 |
| `dotnet test tests/WinForward.Runtime.TcpRedirect.Tests -c Release -m:1` | **168 passed / 0 failed**（load ≈0.9） |
| `TcpCloseDrainTests` 单独重复 **12 轮** | **12/12 绿**，无 flake（load ≈1.3） |
| `dotnet test tests/WinForward.Integration.Tests -c Release --filter "RedirectPacketTakesZeroSequenceGateEntries|EveryDispatchedFlowPacketCarriesAParsedLayout"` | 2 passed / 0 failed |
| `dotnet test tests/WinForward.Performance.Tests -c Release --filter "TcpRedirectDataPathAllocationGateTests|TcpRedirectWarmPathGateTests"` | **10 passed / 0 failed**（8 个精确分配门 + 2 个 warm-path 门，load ≈1.8） |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore --include <9 个改动文件>` | exit 0，**输出 0 字节**（独立复跑，非引用实现者结论） |
| 解析实现者的 `/tmp/jb-inspectcode-10-09.xml` | `ISSUES = 0`；报告 mtime 18:19:58 晚于所有改动文件（最晚 18:15:42），故对当前树有效 |
| 临时探针（`/tmp/wf-review-probe`）验证 `LazyInitializer.EnsureInitialized` 并发语义 | 8 线程并发 → **返回同一个实例**，且都等于字段值 |

未运行：`jb inspectcode` 全量重跑（作者报告 + 产物解析已足够，全量 10–20 分钟且与 VM 实验争 CPU）；整套 solution 的 1674 条（TcpRedirect 项目的 168 与报告的 +11 已独立复现）。所有结论在 §7 列出「未证实」项。

---

## 2. 逐条结论（对应委托问题 1–20）

### 并发与内存序

#### 2-1. ArmDrainAsync 的 publish-then-recheck 是否真的无丢唤醒？Volatile/MemoryBarrier 是否多余或缺失？

**结论：无丢唤醒；没有缺失的栅栏；`Interlocked.MemoryBarrier()` 严格来说是多余的（但与 QuiescenceScope 先例一致，不算缺陷）。**

写入方（acceptor 线程，`TcpRedirectTable.cs:241-257`）：

~~~
EnsureInitialized(_drainCompletion)          // 发布 cell
Volatile.Write(_drainTargetAck, targetAck)   // release 发布目标
Interlocked.CompareExchange(_draining, 1, 0) // full fence
Interlocked.MemoryBarrier()                  // 冗余（CAS 已是 full fence）
TryCompleteDrain()                           // 读 _clientAckMax
~~~

观测方（capture pump 线程，`TcpRedirectTable.cs:221-231` → `259-266`）：

~~~
CAS-max 循环（成功的 CAS 是 full fence）
TryCompleteDrain(): Volatile.Read(_draining) → Volatile.Read(_drainTargetAck)
                    → Volatile.Read(_clientAckMax) → Volatile.Read(_drainCompletion)?.TrySetResult()
~~~

穷举交错（A = arm，O = observe，X = `_clientAckMax`，Y = `_draining`）：

1. **O 的 CAS 先落地、随后读 Y==0（未见 arm）**：O 的 store X 在它的 lock cmpxchg 里提交，然后才读 Y；A 在 Y=1 的 CAS（full fence）+ barrier 之后读 X。这是教科书 store-buffering 形状，两侧都是 full fence，**两个 load 都读到旧值是被禁止的**，因此 A 的 `TryCompleteDrain` 必定看见 O 写的 X。✅
2. **O 读 Y==1（已 arm）**：`Volatile.Read(Y)` 与 A 的 release-CAS 在同一位置配对，X 与 `_drainTargetAck` 在 CAS 之前发布，配对成立；O 读自己刚写的 X 即覆盖目标，O 自己完成 drain。✅
3. **O 的 CAS 未改变 X（重复 ACK / 非前进值，走 break 路径）**：O 没有 store 要丢；真正写 X 的那次 `ObserveClientAck` 自己也跑了 `TryCompleteDrain`，若它读到 Y==0，则由情形 1 保证 A 能看见那次写入。✅
4. **O 在 A 的 CAS 与 A 的 barrier 之间读 Y**：由于 Y 的 0→1 只写一次，任何读到 1 的读都与该 release 写配对，`_drainTargetAck` 必定可见（不会出现 -1 参与比较）。✅
5. **`_drainCompletion` 的可见性**：`EnsureInitialized` 内部用 `Interlocked.CompareExchange` 发布，任何看到 Y==1 的线程都看到非空 cell；`?.` 只是防御。✅

缺失项：无。观测侧的 `Interlocked.CompareExchange` / arming 侧的 CAS 就是栅栏本身；`TryCompleteDrain` 里对四个字段的 `Volatile.Read` 覆盖了「读到别的线程刚写/未写」的两种需求，`long`/`int` 的原子性也有保证（`TcpRedirectTable.cs:143-148` 的 `Unobserved` 注释）。多余项：`TcpRedirectTable.cs:254` 的 `Interlocked.MemoryBarrier()`。`async-lifetime.md:188-193`（seal-vs-decrement handshake）与 `QuiescenceScope.cs:187-197` 用了同样的「发布 cell → `Interlocked.MemoryBarrier` → 复查」形状，所以保留它属于与先例一致；但 `TcpRedirectTable.cs:236-238` 的注释把保证归给「这里的 full fence」，措辞不准（保证来自 CAS）。见 N1。

#### 2-2. ArmDrainAsync 不是严格 single-flight —— 生产路径可达性？

**结论：提示里的具体假设不成立；真实残留是「并发第二调用覆盖 drain 目标」，但当前不可达，可接受，建议补断言/注释。**

- 先纠正假设：两个并发调用**不会各建一个 TCS**。`LazyInitializer.EnsureInitialized` 即使让两个调用各自执行一次 valueFactory，也**都返回同一个已存入字段的实例**（实测：8 线程并发返回 1 个 distinct 实例）。所以「前者的 await 永不完成」不成立——两边等的是同一个 cell。
- 真实残留：`TcpRedirectTable.cs:249` 的 `Volatile.Write(_drainTargetAck, targetAck)` 在 CAS **之前**、且**无条件**执行；并发的第二个调用会覆盖第一个调用已发布的目标（赢家 CAS，输家写目标后 CAS 失败直接返回，且不会自己 `TryCompleteDrain`）。后果是 drain 目标被挪后（多等，仍受 deadline 约束）或挪前（提前退出）。
- 可达性：**生产只有一个调用点**（`TcpRedirectAcceptor.cs:291`），每个会话的 relay 只 attach 一次（`TryEstablishRelayAsync` 之后 accept loop 返回 false），association 在 retire 后从表中移除、新连接拿新 association；其余调用点都在测试里（`rg ArmDrainAsync` 只有 3 处测试 + 1 处生产）。因此**可接受**。
- 建议（minor，见 F4）：在文档注释里把「a repeat call joins the first cell」限定为「顺序重复」，并发重复未被定义；或在写目标前加 `Debug.Assert(Volatile.Read(ref _draining) == 0)`，把不可达变成可捕获。真要严格 single-flight，可把 {target, cell} 放进一个不可变 holder、用**一次 CAS 发布引用**（引用发布本身就是 flag），但那会改变 §3 里那两个反射锁的语义（`_drainCompletion` 会变成 holder 字段），成本大于收益。

#### 2-3. 已 retired 的 association 再 arm 的守卫是否成立？有没有 TOCTOU？

**结论：守卫是「读取时判断」的 TOCTOU，严格来讲不成立；但没有行为后果，标为 minor。**

- 位置：`TcpRedirectTable.cs:253` `if (Phase != RelayPhase.Closing) Phase = RelayPhase.Draining;`。`Phase` 是普通自动属性（`TcpRedirectTable.cs:64`），读与写不在同一原子步骤里。retire 路径在 store gate 内写 `Closing`（`TcpRedirectSessionStore.cs:262-265`），而这个 guard 不在任何 gate 内，所以「读到非 Closing → 写入 Draining」之间另一个 retire 线程可以把相位改成 Closing，随后被这条语句**回写为 Draining**。
- 为什么无害：`Phase` 的读者只有四处——`RemoveExpiredAsync` 的 `== Redirecting`（`TcpRedirectSessionStore.cs:137,152`，Draining/Closing 一视同仁）、`TryAttachRelay` 的 `== Redirecting`（`:314`）、以及测试。数据路径完全不读 `Phase`（§2-14 已验证）。retire 在写 Closing 之前已把 session 从 `_sessions` 移除、alias 从表里删除，所以「被回写成 Draining 的已退休 association」不会再被任何生产逻辑看见；drain 的完成只由 `_draining` 门控，等待也已被取消的 session token 立刻打断（outcome=`retired`）。
- 测试 `TcpCloseDrainTests.cs:220-230` 只钉住了**顺序**场景（先 `Phase = Closing` 再 arm），不覆盖并发窗口。见 F5。

#### 2-4. ClientAckMax 的 CAS-max + ObserveClientAck 里「CAS 未成功也 TryCompleteDrain」是否有害？

**结论：无害，而且是必要的。**

- `ObserveClientAck`（`TcpRedirectTable.cs:221-231`）在两种出口都落到 `TryCompleteDrain()`：CAS 成功（值前进）与 break（值未前进）。后者是 arm/observe 握手的一部分——一个「未使 max 前进」的观测线程可能恰好是那个看见 `_draining==1` 的线程（§2-1 情形 3），必须复查。代价是一次 `Volatile.Read(_draining)` + 未 draining 时立即返回，可忽略。
- CAS 循环本身不会自旋失控：`current >= 0 && !IsSequenceAhead(...)` 时必然 break；最坏情况只是与并发写者竞争几次。

### 序列号与边界

#### 2-5. IsSequenceCovered 的回绕、unchecked 转换、delivered 边界

**结论：全部正确。**

- `IsSequenceCovered(observed, target) = observed == target || IsSequenceAhead(observed, target)`（`TcpRedirectTable.cs:187`），`IsSequenceAhead` 是 `candidate != current && (int)(candidate - current) > 0`（`:183`）＝ RFC 793 的 2^31 串行比较。等号计入覆盖，正好对应「客户端 ACK 覆盖 FIN」的语义。
- 边界：`observed = target - 1`（只 ACK 了数据、没 ACK FIN）→ `(int)(-1) < 0` → 不覆盖，drain 继续等。✅ `observed` 比 target 大一个 RTT 的字节数 → 覆盖。✅ 回绕：双方都在同一模 2^32 空间，差值远小于 2^31。✅
- `targetAck = unchecked((uint)((long)serverInitialSeq + 2 + delivered))`（`TcpRedirectAcceptor.cs:327`）：截断＝模 2^32，与 TCP 序列空间一致；`delivered` 到 2^32 以上（>4 GiB 传输）时目标回绕，而客户端 ACK 仍在目标的一个窗口内，比较仍正确；`long` 求和在 `delivered` 接近 `long.MaxValue` 时才可能溢出（物理上不可达）。`delivered < 0` 被拒（`:326`），`delivered == 0` → target = serverISN+2（SYN-ACK 与 FIN 各占 1），正确。
- 目标推导的所有输入在 arm 时都是终态：`ServerStreamBytes` 在两条 pump 都结束后由 finally 写入（`TcpProxyRelay.cs:296-301`），而 `Completion` 已在 `ObserveRelayCompletionAsync` 里被观测（`TcpRedirectAcceptor.cs:228`）；`ServerInitialSeq` 来自反向 SYN-ACK（`TcpSequenceObservation.cs:64-72`）。

#### 2-6. 要求 ACK 控制位是否足够？有没有漏掉合法帧类型？

**结论：实际足够；有两处值得记录但不构成缺陷。**

- 覆盖：纯 ACK（0x10）、数据+ACK、客户端自己的 FIN|ACK（0x11）、以及 RST|ACK（0x14）都带 ACK 位、都会被读，且它们的 ACK 字段都承认「本连接的 server→client 序列空间」——客户端在本流上的每一帧都只可能承认这条流的反向数据。SYN 无 ACK 位（被忽略，正是 `implementation-notes.md` §3.3 的理由：SYN 的 ack 字段是 0，0 在串行空间里可能「覆盖」目标）；TFO 数据 SYN 同样 SYN 置位、ACK 清零，走 `HandleSynAsync` 分支，不进观测点（`TcpProxyCoordinator.cs:429-436`），不需要观测。
- IPv6：`layout.TransportOffset + 8`（`TcpSequenceObservation.cs:178`）由解析器给出扩展头之后的传输层偏移，与既有 `TrackClientSequence`（+4）同族；`frame.Length < offset + 4` 完全挡住越界（`layout.IsTcp` 还蕴含解析器已验过 dataOffset ≥ 20，`IPTcpUdpPacket.cs:135-137`）。✅
- 值得记录的两点（不构成 finding）：① **RST|ACK**（客户端对我们 FIN 的拒绝/中止）如果其 ACK 字段覆盖目标，会以 `acknowledged` 结束 drain——语义上客户端确实放弃了连接，提前释放 alias 是对的，只是日志用词偏乐观；② **纯 RST（无 ACK）** 不被跟踪，客户端中止后 drain 仍会等满 deadline（有界 5 s）才退休 alias——可接受，design §8 的风险表已把「MSTCP 晚段 RST 由 deadline 兜底」列为接受残余。
- 唯一的理论缺口：forward 腿上若出现「非本流」的 SYN|ACK（客户端同时是监听方且四元组碰撞），其 ACK 值与本流无关，串行比较有约 50% 概率判为「覆盖」，会提前结束 drain。要发生需完全相同的四元组 + origin 上下文（`FlowKey` 已含 origin/adapter），实际不可达。

#### 2-7. ACK 可能小于 target 的假阴性是否只靠 deadline？有没有「ACK 永远到不了观测点」的真实路径？

**结论：假阴性确实靠 deadline 兜底（设计如此）；未找到观测点结构性不可达的路径；有三类已知有界情形。**

观测链（独立复核，不采信 notes 的结论）：

1. `TcpProxyCoordinator.HandlePacketAsync`：纯 ACK 非 SYN（`TcpFrameRewriter.IsTcpSyn` = SYN ∧ ¬ACK）→ 落到 `TryResolveByOriginal`（`TcpProxyCoordinator.cs:441`）→ `ReinjectExistingFlowDataAsync`（`:443`）。监听端口 prefiler 只影响反向前置探测，命中也只是多一次探针后落回原路（`tcp-redirect-teardown-grace.md:72-95`）。
2. `ReinjectExistingFlowDataAsync`：`TrackClientAck` 在 `StageFrame` 之后、rewrite 与一切失败返回之前（`TcpProxyCoordinator.Injections.cs:253-259`）；SYN 重用路径（`TcpProxyCoordinator.cs:121-124`）与 setup 在途路径（`:293`）都汇入同一函数。
3. 数据路径不读 `Phase`，`Draining` 与 `Relaying` 解析一致（`TcpRedirectTable.cs` 无 phase 门；grep 的 phase 读者只有 store/sweep/attach）。✅

三类「ACK 到不了」的情形，都有界且按设计落 deadline：
- **客户端 ACK 丢失 / 从未产生**：MSTCP 会用 RTO 重传 FIN，客户端再 ACK；重传全丢则 deadline（R5 的边界，§2-20）。
- **MSTCP 提前毁掉 TCB（晚段 RST）**：`ServerStreamBytes` 计的是「被 MSTCP 接收的字节」，若 TCB 在发出前被毁，target 可能永远等不到 → deadline（design §8 已列为接受残余）。
- **capture gap / forward 腿分片**：分片会走 `HandleFragmentAsync` 的 fail-closed 拆流（`tcp-client-close-injection.md:123-135`），此时 drain 以 `retired` 退出；capture 丢包则 deadline。
- flow 表项被 idle 淘汰这一路被 `HoldsFlow`（session ∨ tombstone）挡住（`tcp-redirect-teardown-grace.md:63-68`、`idle-expiry-sweep.md:51-56`），drain 期间 session 还在，故不会被淘汰。✅

### 生命周期与存储

#### 2-8. acceptor 先 dispose、store 再 dispose 一次：单飞、日志、`tcp.redirect.closed` 只记一次？

**结论：全部成立。**

- 单飞：`TcpProxyRelay.DisposeAsync` 用 `Interlocked.Exchange(ref _teardownStarted, 1)` 认领（`TcpProxyRelay.cs:344-349`）；第二次调用只 join `_scope.DrainAsync()` 与 `ObserveCompletionAsync()`，不重跑 socket/control teardown（D11，`tcp-relay-lifecycle.md:68-80`）。acceptor 的这次 dispose 是第一次认领，store 的 `ReleaseRetiredAsync`（`TcpRedirectSessionStore.cs:299-303`）是第二次、纯 join。
- 日志：`tcp.redirect.closed` 只在 `ReleaseRetiredAsync`（`TcpRedirectSessionStore.cs:280`）写一次；retire 的入口 `TryRetireSessionUnderGate` 有 `ReferenceEquals` 守卫（`:249`），第二次 retire 返回 null、不写日志。acceptor 的提前 dispose 不写任何 closed。
- 新测试 `TcpCloseDrainTests.TheRetireNeverPrecedesTheDrainExit`（`TcpCloseDrainTests.cs:121-157`）用真 store 断言 `CountEvents(logger, "tcp.redirect.closed") == 1` 且 drain 事件在下标上先于 closed。✅
- 注意一个语义细节（非缺陷）：若 acceptor 的第一次 `DisposeAsync` 在认领后**抛异常**，D11 规定后续调用不会重试 teardown，acceptor 的 catch + `TcpRedirectRelayDisposalFailed`（`TcpRedirectAcceptor.cs:284-289`）把它变得可见，这与既有 store 路径的语义一致。

#### 2-9. drain 把 accept loop 拉长最多 5 s 的影响（shutdown / sweep / capacity / listener 释放 / 冗余 accept）

**结论：各条都一致，未发现顺序反转或泄漏；容量/端口的使用是有界增加，属于 design §8 已接受的代价。**

- **shutdown**：`TcpRedirectSessionStore.DisposeCoreAsync` 先 `_scope.Cancel()`（store 的 scope 是 session scope 的 linked 父，`TcpRedirectSession.cs:21`），drain 的 `WaitAsync(..., token)` 立刻以 OCE 退出（outcome=`retired`），再 retire/await accept loop。**shutdown 不会被 5 s 拖住。** ✅
- **sweep**：`RemoveExpiredAsync` 只退休 `Phase == Redirecting` 的会话（`TcpRedirectSessionStore.cs:137,152`），`Draining` 与 `Relaying` 一样不被 idle 淘汰；drain 有硬 deadline，不需要 sweep 兜底。✅
- **capacity**：drain 期间 session 仍在 `_sessions`（retire 才移除），`SessionCount` 照常计入容量门（`TcpProxyCoordinator.cs:147`）。新增并发 ≈ deadline × clean-end rate，design §8 明示接受；最坏情形是半开洪泛下容量被 drain 占满 5 s。✅（记录）
- **listener 释放顺序**：`ReleaseRetiredAsync` 仍是 closed 日志 → listener+自流量 token → relay →（必要时）lifetime（`TcpRedirectSessionStore.cs:277-308`），listener 在 drain 之后才释放——这正是 drain 的目的（保 alias 与监听端口）。相对今天，**listener 端口多占最多 5 s**；「不多占端口」的假设（AC5）指不多占一条 accepted 连接的端口，listener 端口本身的驻留时长确实变长（AC5 应把它算进并发端口预算）。
- **冗余 accept**：`DrainRedundantConnectionsAsync` 与 `ObserveRelayCompletionAsync` 并发（`TcpRedirectAcceptor.cs:120-122`），drain 期间继续 accept 并立即 `DisposeAsync`（`:203`），不会堆积；它靠 teardown 释放 listener 结束（`:176-177`），而 teardown 在 drain 之后——即多排空 5 s 的冗余 SYN，语义正确。✅
- **新的同 tuple 连接**：association 仍在表里 + 无 tombstone，反/正索引都解析到本 association；新客户端端口走新 key、新 listener。同 tuple 的 straggler 由 `Draining` association 承接（测试 6 已钉）。✅

#### 2-10. session.Token 在 arm 前/等待中取消的所有路径；WaitAsync 异常语义是否完整处理？

**结论：路径覆盖完整，异常处理正确。**

- token 的来源：`RunAcceptLoopAsync` 的局部 `session.Token`（`TcpRedirectAcceptor.cs:42`），作为参数传进 `DrainCleanEndAsync`（`:258, 276`），**不是**在等待中重新读 `session.Token`（避免 CTS 在 accept loop 结束后被释放时读到已 dispose 的 scope token；`async-lifetime.md` 的 deviation 与 `TcpRedirectSession.cs:34-43` 注释）。
- 取消路径：`Retire()`（injection failure / fragment / capacity / sweep / shutdown 全部汇入，`implementation-notes.md` §4 与 `TcpRedirectSessionStore.cs:236-275`）→ session scope `Cancel` → store 的 `_scope.Cancel()` 也会经 linked 传播。arm 前取消：`WaitAsync` 立即 OCE → `retired`；等待中取消：同样 `retired`。✅
- `Task.WaitAsync(TimeSpan, CancellationToken)` 的异常：超时 `TimeoutException`（`:298` 捕获）、token 取消 `TaskCanceledException`（OperationCanceledException 子类，`:302` 带 `when (token.IsCancellationRequested)` 过滤）。由于 `_drainCompletion` 只会 `TrySetResult`、永不 fault/cancel，不存在「任务本身以取消/异常结束但 token 未取消」的分支；若真出现未过滤的异常，会被 `ObserveRelayCompletionAsync` 的外层 catch 吞掉（`TcpRedirectAcceptor.cs:262-265`）——**那样 teardown 会被跳过**，但如上所述该分支不可达。✅
- 一个可改进点（非缺陷）：外层 catch 只记 `tcp.redirect.relayCompletionFailed`，不重试 teardown；与既有代码同形，不建议在本任务里改。

#### 2-11. drain 期间 tombstone / reverse index / fragments / injection failure 是否与 design §3 一致？

**结论：一致。**

- **tombstone**：只在 retire 的 store-gate 临界区内 arm（`TcpRedirectSessionStore.cs:372-375`），drain 期间不 arm、不命中；测试 6 断言 `Tombstones.TryHit(...) == false` 且表内仍有一条 claim（`TcpCloseDrainTests.cs:181-191`）。✅ 与 design §3「alias 是合法存活、不是复活」一致：先解析、后 retire，而不是 retire 后再解析。
- **reverse index**：`RemoveUnderGate` 才清反向索引与 warm cache（`TcpRedirectTable.cs:536-550`）；drain 期间反/正索引都在，反向 straggler 直接解析到本 association（测试 6 用反向 FIN 钉住，`TcpCloseDrainTests.cs:185-191`）。✅
- **fragments**：`HandleFragmentAsync` → fail-closed 拆流 → `FailAssociationAsync` → retire → token 取消 → drain 以 `retired` 退出。与 design §8 的「fragment 退出路径」一致，属于「other exits still win」。✅
- **injection failure**：`HandleInjectionFailureAsync` → best-effort abort + `FailAssociationAsync`（`tcp-client-close-injection.md:137-145`）→ 同上退出。✅
- **客户端 ACK 只被观测、不被消费**：`TrackClientAck` 读的是 pre-rewrite 帧，帧继续走 rewrite/注入（`TcpProxyCoordinator.Injections.cs:259-267`），正是「让 MSTCP 自己的 TCB 完成 close」的转发。✅

### 性能与规范

#### 2-12. 包路径新增开销是否真的零分配零 gate？早退顺序？

**结论：零分配零 gate 成立；与 hot-path/warm-path 契约不冲突。**

- `TrackClientAck`（`TcpSequenceObservation.cs:167-182`）：`IsTcp` 门 + flags 位测试 + 长度检查 + 一次 4 字节大端读；`ObserveClientAck` 是 CAS-max 循环 + 一次 `Volatile.Read(_draining)`（`TcpRedirectTable.cs:221-231, 259-266`）。全程无 new/装箱/LINQ/闭包/delegate，无锁（`_drainCompletion` 只在 clean end 分配一次，且 `RunContinuationsAsynchronously`）。`TryCompleteDrain` 的早退顺序是最省的：先读 `_draining`（未 draining 直接返回），再读目标/最大值/cell。✅
- gate：`TcpRedirectWarmPathGateTests.TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` 驱动含 `TrackClientAck` 的真实 `HandlePacketAsync`，断言 0 表 gate 进入、0 时钟读、1 次反向探针——**独立复跑绿**。✅
- 与 hot-path 冲突：无（无分配、无 async 状态机、无 gate）。代价是 forward 腿现在每包两次 lock cmpxchg（client seq + client ack），而 R6 的措辞（`prd.md:88-91`「一次 4 字节读 + 一次带符号比较」）低估了它——这是记录项，不是违规。
- **但**：精确 0 B 分配门没有覆盖这次新调用（见 F2）。

#### 2-13. 注释/抑制是否符合 AGENTS.md？

**结论：基本符合；一处注释行尾以 ) 结尾违反新加的注释约定（nit），唯一的抑制恰当且理由可验证。**

- 任务号/历史叙述：新注释无任务号、无「曾经/以前」叙述；`crafted-close path already has`（`TcpRedirectAcceptor.cs:274`）是对现存路径的对照，不是历史。✅
- 抑制：全改动只有 1 处 `// ReSharper disable once ConvertIfStatementToReturnStatement`（`TcpRedirectAcceptor.cs:33`），理由是 guard-clause + throw 更可读、且 `cond ? throw … : value` 在本仓库无先例——**可验证**：`WinForward.NdisApi/NdisApiAbi.cs:179`、`WinForward.NdisApi/NdisPacketBuffer.cs:41,79`、`WinForward.Core/IPAddressValue.cs:39,46`、`tests/WinForward.TestSupport/FrameBuilders.cs:23` 用了逐字相同的理由，其中 `FrameBuilders.LayoutOf` 正是 notes 引用的先例。`ParamName` 也保住了。✅ 见 N3。
- 注释行尾：唯一命中 `^\s*//.*[;)}{]$|=>$` 的新增行是 `TcpRedirectAcceptor.cs:305`（`// Another retire path (an injection failure, a fragment, capacity, the sweep, shutdown)`）。`AGENTS.md` 的 S125 规则明说不要以 ) 结尾；但 `dotnet format --severity info --verify-no-changes` 对包含该行的 9 个文件**输出 0 字节、exit 0**（我独立复跑），且全仓已有 30 个文件存在同类 ) 结尾的 `//` 行——即 S125 并未触发，属「约定文本 vs 实际规则」的偏差。见 N2。

#### 2-14. RelayPhase 新枚举值让 Closing 从 2 变 3：有没有依赖序号/序列化/switch 穷尽？

**结论：没有。**

- 全仓 `rg RelayPhase`（含 tests/benchmarks）只出现：相等比较（`Redirecting`/`Relaying`/`Draining`/`Closing`）、`Phase = …` 赋值、以及 XML 引用。没有任何 switch、强制转换到 int/string、序列化、配置映射或 `Enum.GetValues` 依赖；`EndKindName` 的 switch（`TcpRedirectAcceptor.cs:358-363`）走的是 `RelayEndKind`，不是 `RelayPhase`。`RelayPhase` 是公开类型但没有持久化面。✅

### 测试质量

#### 2-15. 11 条 drain facts 是否都是真判别器？150/250 ms deadline 在满载 CI 会不会 flaky？

**结论：11 条都是对其「被删掉的实现部分」的真判别器；deadline 值本身不会造成 flake。**

逐条判别力（「删掉被钉的那段代码，这条 fact 会不会因正确理由红」）：

| # | Fact（`TcpCloseDrainTests.cs`） | 判别对象 | 删掉后 |
|---|---|---|---|
| 1 | `CleanEndDrainsUntilTheClientAcknowledgesThenRetires`（:34） | 顺序 dispose→arm→retire + `acknowledged` | 无 dispose 步骤 / 永不 Draining → 红 |
| 2 | `CleanEndWithoutAnAcknowledgementRetiresAtTheDeadline`（:60） | deadline 出口 | 无 drain 事件 → `Assert.Single` 抛 |
| 3 | `CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain`（:78） | `HasOriginalSynTemplate`/`ServerInitialSeq` 降级门 | 会先 dispose、会 Draining → 红 |
| 4 | `CleanEndWithoutEndInfoRetiresImmediatelyWithoutADrain`（:101） | `ITcpRelayEndInfo` 降级门 | 同上 |
| 5 | `TheRetireNeverPrecedesTheDrainExit`（:121） | 顺序 + closed 恰好一次 | 无 drain 事件 / 下标反 → 红 |
| 6 | `AStragglerDuringTheDrainResolvesToTheSameAssociationAndArmsNoSetup`（:160） | 数据路径不做 phase 门、索引保持 | 包被丢/新 setup → 红 |
| 7 | `AForwardAckThatCoversTheCloseCompletesTheArmedDrain`（:195） | 真包路径的 `TrackClientAck` | drain 不完成 → 红 |
| 8 | `ArmingADrainNeverRewritesAClosingPhase`（:220） | phase 守卫 | 变 Draining → 红 |
| 9 | `AnotherRetirePathEndsTheDrainImmediately`（:233） | token 绑定 | 等满 30 s → WaitForAsync 超时 |
| 10 | `APiggybackedAcknowledgementEndsTheDrainWithoutWaiting`（:255） | arm 时复查 | 等 30 s → 超时 |
| 11 | `ClientAckTrackingIsAdvanceOnlyWrapsAndIgnoresFramesWithoutTheAckFlag`（:277） | tracker 语义 + ACK 位门 | 直接断言失败 |

- 关于 deadline 的 flake：150/250 ms 是**产品**超时，不是测试等待预算。测试侧统一用 `WaitForAsync`（默认 10 s，`AsyncTestExtensions.cs:13`）等状态；主机调度只会推迟 deadline 到期，不会让断言提前看到结果（这些分支没有客户端 ACK，是确定性的）。反过来，若 deadline 被系统延迟越过 10 s，那是 test-stability §2.9 的「主机饥饿」预算问题，与 150/250 ms 的取值无关。✅ 我另做了 `TcpCloseDrainTests` 12 轮重复（load ≈1.3）全部绿。
- 需要记录的两点：
  - **「red before」证据未被独立复现**（只读禁止改树）：notes 记录的编译期红（`DrainCleanEndAsync`/枚举不存在）+ 把 clean-end 改成 Stalled 的 4 条失败是合理且自洽的，但 test 3/4 在改动前的树上本就会绿（它们锁的是「降级不被破坏」），真正的 red-before 只来自编译失败——这是可接受的锁形态，不是问题。
  - 若断言在 `session.Retire()` 之前失败，测试会留下一个最多 30 s 的 drain 和 accept loop（未 dispose 的 lifetime CTS）在后台；绿灯无影响，红灯时会给后续测试添噪声。留作 nit（N6）。

#### 2-16. 两个反射锁改动后是否仍锁住它声称的东西？

**结论：锁住了，而且是「恰好一个引用字段 + 名字 + 未武装为 null」三条真断言；但名字硬编码会在下次重命名时脆断。**

- `SequenceTrackerTests.cs:18-27`：`Assert.Single(fields, f => !f.FieldType.IsValueType)`（xUnit 的 predicate 重载：0 个或多个都抛）→ `Assert.Equal("_drainCompletion", drainCell.Name)` → `Assert.Null(drainCell.GetValue(新 association))`。加第二个引用字段（例如 `Lock`）会立刻红；把 cell 改成构造时分配也会红。`TcpRedirectAssociation` 的其它字段（含 `NativeLease _originalSynTemplate`，它是 readonly struct）确实是值类型，所以「恰好一个」成立。✅
- `PacketPathWalkCountTests.cs:124-127` 同一形状，对象是组合里的真实 association（未武装）。✅
- 语义变化如实：加锁字段仍会被 `Single` 抓住，所以「association 无 lock」这条性质**没有**被削弱——被削弱的只是「无任何引用类型字段」的字面表述；配合 `TcpRedirectWarmPathGateTests` 的 0 gate/0 时钟读（我复跑绿），spec 真正要保护的性质仍在测量中。✅
- 脆弱点（nit）：`Assert.Equal("_drainCompletion", …)` 把实现细节名字变成契约，改名的重构会红（虽然不是缺陷，但注释里说明了理由：避免用「名字排除」绕过）。若想更稳，可断言字段类型是 `TaskCompletionSource` 且新 association 为 null，而不钉名字。同时该 fact 现在**要求**这个字段存在——把 drain 状态搬走（比如搬进嵌套 holder）会红，需要一并更新。

#### 2-17. 有没有未覆盖的关键路径？

有，按重要性：

1. **forwarded（DNAT）形态**：11 条 facts 全部用 `CreateHostAssociation`（`TcpCloseDrainTests.cs:366`），没有一条在 `ForwardLocalAddress != null` / `towardMstcp == false` 的 association 上 arm drain 或完成 drain。目标计算与 ACK 观测确实是形态无关的，但注入方向不同（reverse → 源适配器），值得一条 forwarded fact。硬件上也未验证 forwarded（`tcp-redirect-transform.md:68-71` 明说 forwarded 路径只有单元锁）。
2. **并发 arm / deadline 与 ACK 同时到达**：没有 staged 交错（test-stability §2.5 要求钉 race 的 fact 必须强制交错）；也没有「两个 association 同时 drain」的多实例 fact。握手是本次改动里最高的并发风险点，但内部字段私有、无法从测试侧停放，需要 seam 才能 staged——记录为 residual。
3. **IPv6 的 ACK 读**：`TrackClientAck` 的偏移逻辑是 family 无关的，但没有 IPv6 fact（既有 oracle fact 只覆盖 sequence advance）。
4. **`TrackClientAck` 缺 span oracle 孪生**（见 F6）。

---

## 3. Findings 汇总

> 严重级定义：blocker = 必须修否则不能提交/会产生生产错误；major = 应在提交/进入下一步前解决或显式改范围；minor = 建议修或记录；nit = 风格/措辞。

### Major

#### F1 — A/B 开关没有落地，AC1/AC4 的验收证据会被 crafted FIN 混淆

- **位置**：`src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs:335-343`（clean-end 无条件注入 crafted FIN|ACK），全文件无开关；`implement.md:57-59`（C1「The internal A/B switch rides along here, defaulting to drain-only」）；`design.md:138-141`（开关在 VM 验证期「re-enables the crafted FIN」，删除于最终提交前）。
- **为什么**：drain 的 clean-end 顺序是 inject(crafted FIN) → dispose → arm → wait → retire。只要 crafted FIN 默认还在，客户端通常在一个 RTT 内 ACK 它并立刻结束 drain（arm 时复查甚至可能直接以 `acknowledged` 退出），于是：
  - AC4 的「两臂」只有一臂可跑（要跑 drain-only 得临时改码，计划里没有这一步）；
  - AC1 想证伪的 design §2 使能假设（「MSTCP 的 TCB 在 `closesocket` 后还会重传 FIN」）**无法被证伪**：即使 TCB 被拆，crafted FIN 仍让客户端看到 close、测量仍可能 clean；
  - AC5 的 drain 时长分布会集中在「ACK crafted FIN」的短尾，而不是设计预期的「1 RTT + RTO 形状」。
- **建议**：在 `TcpRedirectAcceptor` 上补一个 `internal` 的 `bool`（无配置面，默认 drain-only，符合 design §6），让 Phase D 可切换；或与用户重定 AC1/AC4 的判据（例如明确接受「drain+FIN」形态并把「FIN 冗余」的证明推迟到 removal 提交后的一次 VM 臂）。
- **验证方法**：读 `git diff`（无开关）、`rg -n 'drainOnly|EnableCrafted' src/WinForward.Runtime/TcpRedirect/`（无命中）、`implement.md` C1 原文；并与 `implementation-notes.md` §1「Kept untouched, exactly as required」对照——这是**未自报**的范围偏差（notes 只自报了 C3 未做）。

#### F2 — 新的每包调用没有被精确 0 B 分配门覆盖

- **位置**：`benchmarks/WinForward.Benchmarks/Perf/TcpRedirectDataPathBenchmarks.cs:77-90`（`ForwardLegHost`/`ForwardLegForwarded` 只调 `TrackClientSequence` + rewrite）、`tests/WinForward.Performance.Tests/TcpRedirectDataPathAllocationGateTests.cs:28-46`（门直接复用这两行）、`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.Injections.cs:259`（新调用）；依据 `prd.md:88-91`（R6）、`hot-path.md:20-22`（zero is a gate）。
- **为什么**：forward 腿现在有**两个** tracker，而「composed per-packet cost」的行与门仍然只跑一个。若将来有人在 `TrackClientAck`/`ObserveClientAck` 里引入分配（哪怕是一次装箱或闭包），现有 0 B 门**不会红**——这正是 allocation-gates 契约要求的那种「能失败的门」。代码当前由肉眼确认零分配（无 new/闭包/LINQ），且与已进门内的 `ObserveClientSequence` 同形。
- **建议**：在 `ForwardLegHost`/`ForwardLegForwarded`（以及 `ProveRowsSucceed` 的 `Rewrite`）里补 `TcpSequenceObservation.TrackClientAck(_scratch, _layout, association)`；benchmark 帧已是 ACK 位（`BenchmarkShared.cs:71`），会走真实路径。注意这会让已发布的 row 数字变化，属于有意的测量口径更新。或者新增一条只针对 `TrackClientAck`+`ObserveClientAck` 的精确门。
- **验证方法**：读上述三处代码；我另外确认当前 10 条门全绿（`TcpRedirectDataPathAllocationGateTests` 8 + `TcpRedirectWarmPathGateTests` 2），即缺口是「覆盖」而非「已分配」。

#### F3 — spec 与落地代码相反，未随本次改动更新

- **位置**：`.trellis/spec/backend/warm-path-dispatch.md:104-114`（“The association holds no reference-typed instance field (no `Lock`) …”）、`.trellis/spec/backend/tcp-client-close-injection.md:36-50`（“There is no per-association lock and no gate…” 连同点名 `TcpRedirectAssociationHoldsNoLockField` 的清单）、`tcp-client-close-injection.md:52-83`（“Every relay end injects its client-visible close before the retire … the retire that follows it is immediate … **The redesign is active in task 10-07-tcp-close-drain** … does not describe the drain.”）、`.trellis/spec/backend/tcp-local-redirect.md:25,45-46`（Close 行 + “Every relay end injects its client-visible close before the retire” 不变量）。
- **为什么**：`design.md:187-189` 与 `prd.md:152-154` 允许本任务改这两份 spec，`implement.md` Phase E 也把它列为收尾项。当前 drain 改变了 retire 时机与「哪个 close 是契约」；spec 还写着 immediate-retire，并指向已被本任务取代的 10-07 草稿。
- **建议**（Phase E 的措辞）：
  1. `tcp-client-close-injection.md`：「Every relay end ...」一节改写为：clean end 先由 relay 的 graceful close 发出真 FIN（**先 dispose relay、再 arm drain**），alias 保持到客户端 ACK 覆盖 `serverISN + 2 + ServerStreamBytes`，deadline（默认 5 s）与任何其它 retire 路径都能结束 drain；crafted FIN 只剩 abnormal end 的 RST|ACK（removal 提交后）；残差段改为「ACK 不会被观测到的三类有界情形 → deadline」。
  2. `warm-path-dispatch.md`：「The association holds no reference-typed instance field」改为「The association holds no lock and no eagerly-allocated per-association object; the only reference-typed field is the close drain's completion cell, allocated once per clean end and null otherwise」；保留 0 gate / 无 per-association lock 的实测清单，并说明 `TcpRedirectAssociationHoldsNoLockField` 现在断言的是「恰好一个引用字段（未武装为 null）」。
  3. `tcp-local-redirect.md`：Close 行与不变量加「（clean end：drain；abnormal end：crafted RST）」；删除指向 10-07 的指针。
- **验证方法**：逐句对照 `rg -n 'holds no reference-typed|immediate|10-07' .trellis/spec/backend/`；三条断言均可在文件里定位。实现者在 `implementation-notes.md` §8 自报了「spec 现在过期」，但把该句归给 `tcp-client-close-injection.md` 不准确（原文在 `warm-path-dispatch.md`）——结论不受影响。

### Minor

#### F4 — 并发 ArmDrainAsync 会覆盖已发布的 drain 目标（生产不可达）

- **位置**：`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:246-250`。
- **为什么**：见 §2-2。提示里的「各建 TCS、前者 await 永不完成」不成立（`EnsureInitialized` 保证同一实例，已实测）；真实后果是目标被后写者覆盖，可让 drain 早退或晚退。当前唯一生产调用点只调用一次，故不可达。
- **建议**：文档注释限定「顺序重复」语义 + `Debug.Assert`；或改 holder 单一 CAS 发布（代价：改反射锁语义）。**不建议**在 CAS 失败后简单回写，因为「先发布目标、后置 flag」的顺序是观测侧正确性的前提。
- **验证方法**：代码阅读 + `/tmp/wf-review-probe` 的 EnsureInitialized 实测 + `rg ArmDrainAsync` 调用点清单。

#### F5 — `Phase != Closing` 守卫是 TOCTOU，注释说得比代码强

- **位置**：`src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs:251-253`；测试 `TcpCloseDrainTests.cs:220-230`。
- **为什么**：读—写非原子；并发 retire 可在窗口内把相位改回 Draining。无行为后果（`Phase` 的生产读者不区分 Draining/Closing，session 已从 dict 移除、alias 已删），但注释「is never rewritten back」在并发下不成立，且测试只钉顺序场景。
- **建议**：把注释改成「best-effort：顺序上不回写；并发 retire 的相位回写是良性的（drain 的完成只由 `_draining` 门控）」，或真正做到原子（例如把 phase 存成 int 做 CAS——不推荐为这点外观竞争改公开属性语义）。
- **验证方法**：代码阅读；`rg RelayPhase` 的读者清单（§2-14）证明无行为后果。

#### F6 — `TrackClientAck` 缺少 span oracle 孪生

- **位置**：`src/WinForward.Runtime/TcpRedirect/TcpSequenceObservation.cs:167-182`（只有 layout 重载）；对照文件头 `:9-18` 的「span-taking entries remain the independent oracle and sit next to their layout twin」，以及 `tcp-client-close-injection.md:45-50` 对 entry point 形状的记述（`TrackClientSequence`/`TrackServerSequence` 各有一对）。
- **为什么**：新观测点是唯一没有独立 oracle 的 entry point；ACK 字段偏移（`TransportOffset + 8`）与边界检查只在 layout 路径被测试（且只测 IPv4）。虽是正确的固定偏移，但破坏了本文件既定的「双入口 + 独立解析」防错形状。
- **建议**：加 `public static void TrackClientAck(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)`（用 `IPTcpUdpPacket.TryParse` + `14 + ipHeaderLength + 8`）与其 oracle 一致性的 `[Theory]`（IPv4/IPv6，带/不带 TCP options 各一）。
- **验证方法**：读文件与 spec；`rg -n 'TrackClientAck' src/ tests/` 显示只有 layout 重载被测试引用。

#### F7 — 测试覆盖缺口：forwarded 形态、并发 arm、deadline 与 ACK 同时到达

- **位置**：`tests/WinForward.Runtime.TcpRedirect.Tests/TcpCloseDrainTests.cs:363-378`（`CreateSession` 硬编码 `forwardLocalAddress: null`）、全文无并发 arm / 双 drain / IPv6 fact。
- **为什么**：见 §2-17。forwarded 是同一设计里注入方向不同的另一半，硬件路径又未验证；并发 arm 是 §2-2 的残留。
- **建议**：至少补一条 forwarded drain fact（arm 后从 forward 腿 ACK 使其完成，断言 `towardMstcp == false` 的注入方向不变）；并发 arm 若无法 staged，就在 `implementation-notes` 的 residual 里显式登记（test-stability §2.5 的「要么钉住、要么记录」）。
- **验证方法**：`rg -n 'ForwardLocalAddress|forwarded' tests/WinForward.Runtime.TcpRedirect.Tests/TcpCloseDrainTests.cs`（无命中）。

#### F8 — deadline 只约束等待，不约束 arm 前的 relay dispose

- **位置**：`src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs:280-296`（dispose 在 wait 之前，只有 catch 处理异常，不处理挂起）。
- **为什么**：若 `TcpProxyRelay.DisposeAsync`（内部 `_control.DisposeAsync` → `Socks5ControlConnection`）挂起，drain 连 arm 都到不了，alias/session 会被无限期持有；「bounded deadline」保证不覆盖这一段。设计文档说 disposal 由 socket close 界定（`tcp-relay-lifecycle.md:75-76`）且 reader 都会被 cancel，所以现实中应有界；此条按「记录 + 未证实」处理，不阻塞。
- **建议**：注释从「a disposal fault can never pin the session」改为明确「异常不会 pin；挂起依赖 relay 自身的 bounded dispose 契约」，把它登记进任务 residual。
- **验证方法**：代码阅读 + `Socks5ControlConnection.cs:408-437`（seal→cancel→join 的有界形状）；无实测（不制造挂起）。

### Nit

- **N1** — `TcpRedirectTable.cs:254` 的 `Interlocked.MemoryBarrier()` 在前一条 `Interlocked.CompareExchange`（full fence）之后严格多余；保留与 `QuiescenceScope` 先例一致，但 `:236-238` 注释应把保证归给 CAS，而不是「这里的 full fence」。
- **N2** — `TcpRedirectAcceptor.cs:305` 的 `//` 行以 ) 结尾，违反 `AGENTS.md` 的 S125 注释约定；我实测 format 门对该文件 0 输出、全仓已有 30 个同类行，故只是措辞 nit（改成「…, shutdown — all funnel through it」即可）。
- **N3** — `TcpRedirectTable.cs:239` 注释「a repeat call joins the first cell」对并发重复不成立（见 F4）。
- **N4** — `implementation-notes.md:70-72` 把「The association holds no reference-typed instance field」归给 `tcp-client-close-injection.md`，实际在 `warm-path-dispatch.md:108`；结论（冲突为真）不受影响。
- **N6** — 若 `TcpCloseDrainTests` 的断言在 `session.Retire()` 之前失败，会留下最多 30 s 的后台 drain/accept loop；绿灯无影响，红灯会拖慢/干扰后续测试。建议把 `session.Retire(); await acceptLoop;` 放进 `try/finally` 或 `IAsyncLifetime` 清理。

---

## 4. Spec 符合性复核（逐份）

| Spec | 核对结论 |
|---|---|
| `tcp-local-redirect.md`（hub） | 管线表 `Retire` 行仍成立（原子 retire 未动）；**Close 行与「Every relay end injects its client-visible close before the retire」需要按 F3 更新**（clean end 的时机从「inject→retire」变成「inject→dispose→drain→retire」）。 |
| `tcp-client-close-injection.md` | RST 形状、capacity RST、fragment、injection-failure 出口全部未动、仍准确；**clean-end 契约与残差段过期**（F3）。新观测点「ACK 位门」是对「reads the ACK field（offset +8）」的合理加强，spec 未提及但两者不冲突。 |
| `tcp-redirect-transform.md` | 前向/反向形态、pre-rewrite 观测点、数据腿、冗余 accept 均未改变；drain 不改写任何帧。无冲突。 |
| `tcp-redirect-teardown-grace.md` | 原子 retire、单 tombstone 写入点、late-packet consumption、flow hold、capacity 段落全部保持；drain 不绕过任何一条（§2-8/11）。无冲突。 |
| `tcp-relay-lifecycle.md` | dispose 单飞/D11、accept loop 拥有 lifetime、Completion 分类均未变；acceptor 提前 dispose 是 D11 的合法新调用者。**建议在 Phase E 的 spec 里补一句「clean end 的 relay dispose 由 acceptor 先行认领，store 的 release 只 join」**，否则读者会以为 dispose 仍在 release 路径。 |
| `hot-path.md` / `warm-path-dispatch.md` | 无 async 状态机、无新 gate、warm resolve 计数不变；**「no reference-typed instance field」与两个反射锁的语义要按 F3 更新**。 |
| `async-lifetime.md` | 完全符合：无新 CTS（deadline 用 `WaitAsync` 的内部定时器，不属 D7 的 owner-lifetime source）、无 `_ =` discard、无 `Task.Run`/`ContinueWith`、握手形状照抄 seal-vs-decrement（D5/D11）。 |
| `logging-guidelines.md` | `tcp.redirect.drain`：Debug（代理生命周期级，与 `tcp.relay.ended` 同级）、`EventName` 点分、消息为自然句、全部参数出现在模板里、字段是端点/关联 generation/原因/耗时，无敏感数据。✅ |
| `test-stability.md` | 新 fake 用锁快照（`StepLog`）✅；等待用 `WaitForAsync` 的 10 s 预算 ✅；没有把 wall-clock 断言当契约 ✅；deadline 不是等待预算 ✅。**未做** §4 要求的 loaded soak（我用 12 轮轻载代替，见 §7）。 |
| `quality-guidelines.md` | 唯一抑制的 scoped reason 可验证 ✅；无 `.editorconfig` 改动 ✅；测试基线 +11 与 notes 一致（TcpRedirect 项目 168 已独立复现）✅；**0 B 门覆盖不全（F2）**。 |
| `idle-expiry-sweep.md` | sweep 只碰 `Redirecting`；drain 期间 session hold 成立；`RemoveExpiredAsync` 的 scratch/semaphore 语义未动。✅ |
| `directory-structure.md` | 新测试文件命名/归属正确；最大文件 370 effective < 400 ✅；新 fake 都私有嵌套（单文件使用）✅。 |
| `error-handling.md` | 客户端可见失败的姿态未动（crafted close 仍在，异常有界）；drain 不产生新的 fail-open。✅ |
| `guides/index.md` + `cross-layer-thinking-guide.md` | 跨层数据流（capture→观测→rewrite→注入→MSTCP→客户端 ACK→回程观测）已按层核对，无新边界、无重复解析（ACK 只在一处读）。✅ |

---

## 5. 实现者自报偏差的独立复核（问题 18–20）

### 18. 「design §7-1 与 warm-path-dispatch.md / tcp-client-close-injection.md 的『无引用类型实例字段』冲突」是否属实？Phase E 该如何措辞？

**属实（措辞归因略有偏差）。** `warm-path-dispatch.md:108` 的原句是「The association holds no reference-typed instance field (no `Lock`), so a redirected forward+reverse packet pair takes **zero** gate entries and no per-association lock allocation」，`TcpRedirectAssociation` 现在有一个 `TaskCompletionSource? _drainCompletion`（`TcpRedirectTable.cs:214`）。但该句给出的**理由**（0 gate、无 lock 分配）仍然成立：cell 在普通 packet 路径上为 null、不产生 gate。`tcp-client-close-injection.md:42-45` 的对应句是「There is no per-association lock and no gate」，本身没被推翻，只是它点名的 `TcpRedirectAssociationHoldsNoLockField` 语义变了。Phase E 措辞见 F3 建议——核心是「保留理由、把字面规则改成『唯一一个引用类型字段是 drain cell，且未武装时为 null』并让两个测试与新句子对齐」。

### 19. 「真 FIN 在 RunPumpAsync 的 await using 关闭 socket 时发出，早于 Completion」是否属实？acceptor 先 dispose 是否仍必要/安全？

**属实。** `TcpProxyRelay.cs:152` 的 `await using var localStream = new NetworkStream(_localSocket, ownsSocket: true)` 在方法返回时释放 → `Socket.Dispose` → `closesocket`（默认 linger）→ 发 FIN；`EndKind = CleanEnded`（`:201`）是最后一条语句，async 状态机的 finally 在任务完成**之前**跑 dispose。另一条路径是 `:183-184` 的 `ShutdownSend(_localSocket)`（upstream→local 先结束时显式半关）——两条都在 `Completion` 完成前发出同一个 FIN。所以「先 dispose relay 才能发 FIN」的说法不成立；**但先 dispose 仍然值得保留**：(a) 它在 drain 一开始就释放 SOCKS5 上游/控制连接，而不是拖到 drain 结束（上游预算，design §3 的意图）；(b) 安全——`DisposeAsync` 单飞（`TcpProxyRelay.cs:344-349`），第二次由 store join，socket 重复 dispose 不会发第二个 FIN 也不会变成 RST。

**顺带一个应记录的使能前提**（design §2 的延伸）：graceful close 还要求 `closesocket` 时接收缓冲无未读数据，否则 Windows 会发 RST。clean-end 路径上两条 pump 都已读到 EOF，接收缓冲已排空，前提成立；这是 AC1 应该顺带观察的量（reset 计数）。

### 20. 5 s deadline 与 AC0 的「整段未到需等初始 RTO」是否自洽？

**自洽，而且 5 s 比 notes 的论证更宽裕。** AC0 的 10/105「整段响应未到」意味着客户端没有 ACK 任何数据，MSTCP 必须等到自己的重传（`ac0-residual-classification.md:96-104, 227`）。notes（§3.2）用「Windows 默认初始 RTO 3 s」论证 5 s；但对一条已完成三次握手、有 RTT 测量的连接，RTO 是 `max(SRTT + 4·RTTVAR, 300 ms)`（Windows 最小 300 ms），而这批连接的实测 RTT 在毫秒级（`meanConnectMs 4.4–5.0`、`meanTransferMs 0.5`），所以首免重传约在 300 ms–1 s，5 s 覆盖约 2–4 次退避重传，足以覆盖「首传丢失 + 第一次重传成功」。真正的边界是「首传与后续多次重传连续丢失」和「MSTCP TCB 被 RST」——design §8 已把这两类列为 deadline 兜底的接受残余；因此 AC1 的 `timeout == 0` 是**概率性**判据（AC0 的 8.7% 挂起率里，有 12.4% 是 gap 类），若 VM 臂出现个位数 timeout，需要按 exit reason 拆开看是不是 deadline 簇，再决定是否调大常量；notes §8 的「deadline 是随 AC5 证据再议的常量」表述是对的。

**一个重要的限定**：在 crafted FIN 仍在默认路径的当前形态下（F1），5 s 的合理性无法被这次 VM 测量真正检验——大部分 drain 会因为 crafted FIN 的 ACK 瞬间结束。

---

## 6. Findings 索引（按严重级）

| 级别 | 编号 | 一句话 | 位置 |
|---|---|---|---|
| major | F1 | A/B 开关缺失 → AC1/AC4 证据被 crafted FIN 混淆 | `TcpRedirectAcceptor.cs:335-343`；`implement.md:57-59`；`design.md:138-141` |
| major | F2 | 新每包调用的 0 B 门未覆盖 | `TcpRedirectDataPathBenchmarks.cs:77-90`；`TcpRedirectDataPathAllocationGateTests.cs:28-46` |
| major | F3 | 三处 spec 与代码相反、未同步 | `warm-path-dispatch.md:104-114`；`tcp-client-close-injection.md:52-83`；`tcp-local-redirect.md:25,45-46` |
| minor | F4 | 并发 arm 覆盖 drain 目标（不可达） | `TcpRedirectTable.cs:246-250` |
| minor | F5 | phase 守卫 TOCTOU，注释过强 | `TcpRedirectTable.cs:251-253` |
| minor | F6 | `TrackClientAck` 缺 span oracle | `TcpSequenceObservation.cs:167-182` |
| minor | F7 | forwarded / 并发 arm / IPv6 覆盖缺口 | `TcpCloseDrainTests.cs:363-378` |
| minor | F8 | deadline 不约束 arm 前 dispose（理论） | `TcpRedirectAcceptor.cs:280-296` |
| nit | N1 | 冗余 `Interlocked.MemoryBarrier` + 注释归因 | `TcpRedirectTable.cs:236-238,254` |
| nit | N2 | `//` 注释行以 ) 结尾 | `TcpRedirectAcceptor.cs:305` |
| nit | N3 | 「repeat call joins the first cell」措辞 | `TcpRedirectTable.cs:239` |
| nit | N4 | notes 对 spec 句子的归因不准 | `implementation-notes.md:70-72` |
| nit | N6 | 失败路径不释放 accept loop/drain | `TcpCloseDrainTests.cs:55-56` 等 |

（无 N5，编号保留给 §2-10 提到但不建议改的「外层 catch 不重试 teardown」。）

---

## 7. 未证实清单（需要什么证据）

1. **「red before」证据**：notes 的编译红与 Stalled 窄化实验未被独立复现（只读约束）。需要：在一个可编辑的隔离 worktree 里把 `endKind == CleanEnded` 改为 `Stalled`（或删掉 `ObserveClientAck` 尾部的 `TryCompleteDrain`），跑 `TcpCloseDrainTests` 并记录失败理由。
2. **新每包调用的零分配**：目前是「代码检视 + 同形先例已进门」；需要 F2 的门补完后由该门给数。
3. **forwarded 形态的真机行为**：所有新 fact 都是 host 形态；需要 VM 侧的 forwarded 臂（或至少一条 forwarded 单元 fact）。
4. **loaded soak**：test-stability §4 要求两条并发全量流；本次因 VM 实验占机未做，只做了 12 轮轻载重复。
5. **实现者运行过的 `jb inspectcode`**：我解析了 `/tmp/jb-inspectcode-10-09.xml`（0 issues，mtime 晚于所有改动），未全量重跑（10–20 分钟 + CPU 争用）。
6. **AC1–AC5 的 VM 数据**：本复核不涵盖，且受 F1 影响其判据需要先明确。
7. **F8 的挂起场景**：未制造（`Socks5ControlConnection` 的 dispose 路径由 seal/cancel/join 界定，理论有界）。

---

## 8. 复核者签名

- 复核范围：工作树未提交的 drain 改动 + 相邻只读代码 + 任务/父任务证据 + spec 全家。
- 不采信项：notes 的每一条自述都回到代码/测试/spec 重新验证；结论与 notes 不一致处已在 §5、F4 明确写出（其中「并发 arm 各建 TCS」的提示假设被实测否定）。
- 交付物：本文件（唯一新增产物）。未修改仓库中任何既有文件。
