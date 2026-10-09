# 复核 findings 修复记录（F2 / F4 / F5 / F6）

> 对象：`research/code-check.md` §3 的 F2/F4/F5/F6，落在「TCP close drain」这份未提交实现之上。
> 范围：只改这四条；spec 文档、任务文档、removal（手工 FIN 路径）、F1/F3/F7/F8 与 N1–N6 均未触碰；未提交。
> 环境：2026-10-09，本机另有 VM 实验的 `dotnet publish` 并行运行；下列命令都用 `-m:1` / `--no-build` 串行执行，
> 没有出现 OOM 或 `Thread.StartCore` 一类环境红，因此没有触发 test-stability 的重跑条款。
> 验证期间另有子代理在同一工作树推进 removal（`ClientResetInjector` / `TcpResetBuilder` / `TcpRedirectAcceptor` /
> `TcpRedirectLog` 与两个 redirect 测试文件）；本次改动与那些文件不相交，也没有回滚任何非本次改动。

## 0. 状态与改动文件

| Finding | 状态 | 结论 |
|---|---|---|
| F2（门窗口） | **已修** | forward 两行与新增 drain-armed 行的测量窗口都包含 `TrackClientAck`；门 10/10 绿，两个注入探针证明窗口有判别力 |
| F4（single-flight） | **已修** | drain 目标改为 single-flight 发布：首个调用者 CAS 写入，后续调用者加入同一 cell 并沿用首个 target |
| F5（TOCTOU 注释） | **已修** | 选择「改注释」路线：注释现在精确描述 best-effort 守卫与竞态为何良性 |
| F6（span 孪生） | **已修** | 补上 `TrackClientAck(span, association)` 孪生，并由 `PacketLayoutTests` 的两个新 fact 当作 oracle 使用，不是死代码 |

改动文件（工作区未提交，全部在上次复核的改动集之内，没有新增无关文件）：

| 文件 | 角色 |
|---|---|
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs` | F4 + F5 |
| `src/WinForward.Runtime/TcpRedirect/TcpSequenceObservation.cs` | F6 |
| `benchmarks/WinForward.Benchmarks/Perf/TcpRedirectDataPathBenchmarks.cs` | F2（行） |
| `tests/WinForward.Performance.Tests/TcpRedirectDataPathAllocationGateTests.cs` | F2（门） |
| `tests/WinForward.Protocols.Tests/PacketLayoutTests.cs` | F6（oracle parity） |

## 1. F2 — 新的每包调用进入精确 0 B 门

### 1.1 改了什么

生产调用点是 `TcpProxyCoordinator.Injections.cs:259` 的
`TcpSequenceObservation.TrackClientAck(frame, packet.Layout, association)`。原来 forward 腿的 benchmark 行
只跑 `TrackClientSequence`，门又复用这些行，所以新调用落在测量窗口之外。

修改后 forward 腿的窗口是「序列跟踪 + ACK 跟踪 + rewrite」，与协调器的数据腿一致：

```csharp
// benchmarks/WinForward.Benchmarks/Perf/TcpRedirectDataPathBenchmarks.cs
[Benchmark]
public bool ForwardLegHost()
{
    _pristine.AsSpan().CopyTo(_scratch);
    TcpSequenceObservation.TrackClientSequence(_scratch, _layout, _host);
    TcpSequenceObservation.TrackClientAck(_scratch, _layout, _host);   // 新增：此前缺失的每包调用
    return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _layout, _client, _server, _host, ListenerPort);
}
```

- `ForwardLegForwarded()` 同样补上 `TrackClientAck`；原有的 host/forwarded 两行语义只做这一处口径更新，
  没有把 drain-armed 的额外形态混进去。
- `ProveRowsSucceed()/Rewrite(association)` 也补上 `TrackClientAck`（并接受 association 参数），
  与行保持一致：setup 仍然证明「每行都会在 pristine 帧上成功」。

为了覆盖 `TryCompleteDrain` 的完整路径，新增一条独立的行（原来的两行不受影响）：
未武装时 `TryCompleteDrain` 在第一行 `if (Volatile.Read(ref _draining) == 0) return;` 就返回；
drain-armed 形态才会继续读 target、比较 ack。新行的 association 在 `Setup()` 里武装：

```csharp
// benchmarks/WinForward.Benchmarks/Perf/TcpRedirectDataPathBenchmarks.cs
_drainArmed = new TcpRedirectAssociation(hostKey, _server, 0, translated, forwardLocalAddress: null, 3, now);
_ = _drainArmed.ArmDrainAsync(targetAck: 1);

[Benchmark]
public bool ForwardLegHostDrainArmed()
{
    _pristine.AsSpan().CopyTo(_scratch);
    TcpSequenceObservation.TrackClientSequence(_scratch, _layout, _drainArmed);
    TcpSequenceObservation.TrackClientAck(_scratch, _layout, _drainArmed);
    return TcpFrameRewriter.TryRewriteForwardLeg(_scratch, _layout, _client, _server, _drainArmed, ListenerPort);
}
```

benchmark 帧（`BenchmarkShared.CreateIPv4TcpFrame(..., bareSyn: false)` / IPv6 对应物）的 flags 是 `0x10`，
ACK 字段为 0，target=1 永远不被覆盖，所以每次调用都会走完 armed 比较而不会提前完成——这是有意的窗口稳定性设计。

门测试新增一条与其它四条同形的 theory：

```csharp
// tests/WinForward.Performance.Tests/TcpRedirectDataPathAllocationGateTests.cs
[Theory]
[InlineData(false)]
[InlineData(true)]
public void ForwardHostDrainArmedLegAllocatesNoManagedBytes(bool ipv6) => AssertLegAllocatesNoManagedBytes(CreateBenchmarks(ipv6).ForwardLegHostDrainArmed);
```

### 1.2 窗口是否真的包含新调用

门驱动的就是 benchmark 方法本身（`AssertLegAllocatesNoManagedBytes(Func<bool> leg)` 收到的是
`CreateBenchmarks(ipv6).ForwardLegHost`），所以「窗口 = 整个 benchmark 行」；`TrackClientAck` 是行内第二条语句，
在 rewrite 之前，必然在窗口内。为了不靠肉眼断言，按 `allocation-gates.md`
「窗口变更后必须重证判别力 + 注入的分配必须 `GC.KeepAlive`」的要求做了两个探针：

**探针 A**：在 `TcpRedirectAssociation.ObserveClientAck`（`TrackClientAck` 的落地函数）第一行注入
`GC.KeepAlive(new byte[64]);`：

- 结果：`Failed: 6, Passed: 4`，失败的正好是三条 forward 行 × 两个 family，每条 `Actual: 88`（64 B + 24 B 对象头）；
  四条 reverse 行全绿——证明新调用在两个 forward 行和新行里，且门能失败、不会误伤 reverse 行。
- 探针已还原（`rg -n "GC.KeepAlive" src/WinForward.Runtime/TcpRedirect/` 无命中）。

**探针 B**：在 `TryCompleteDrain` 的 `_draining == 0` 早退之后注入 `GC.KeepAlive(new byte[64]);`：

- 结果：`Failed: 2, Passed: 8`，失败的只有 `ForwardHostDrainArmedLegAllocatesNoManagedBytes` 的两条，
  每条 `Actual: 88`——证明新行的窗口确实覆盖 armed 比较，且其它行不会意外命中这段代码。
- 探针已还原并重新构建、复跑（见 §5）。

### 1.3 本次验证命令

```console
$ rg -n "class TcpRedirectDataPathAllocationGateTests" tests/
tests/WinForward.Performance.Tests/TcpRedirectDataPathAllocationGateTests.cs
21:public sealed class TcpRedirectDataPathAllocationGateTests

$ dotnet test tests/WinForward.Performance.Tests -c Release --no-build -m:1 --filter "FullyQualifiedName~TcpRedirectDataPathAllocationGateTests"
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 185 ms - WinForward.Performance.Tests.dll (net10.0)
```

（修复前该项目 8 条精确门，现在 10 条：新增的 drain-armed 行 × 两个 family。）

## 2. F4 — ArmDrainAsync 的目标发布改为 single-flight

### 2.1 原来的问题

旧顺序是：读 `_draining` → **无条件** `Volatile.Write(ref _drainTargetAck, targetAck)` →
`Interlocked.CompareExchange(ref _draining, 1, 0)`。两个并发调用者都读到 0 时，两个都会写 target，
输掉 flag CAS 的那个不会自己复查，于是已发布的 target 可能被后写者覆盖（生产单调用点不可达，但语义不成立）。

### 2.2 改法

用 `_drainTargetAck` 自己的 `Unobserved`(-1) 哨兵做单次发布 CAS：

```csharp
// 首个调用者独占发布 target；后续调用者（顺序或并发）直接加入已发布的 cell 并沿用首个 target
if (Interlocked.CompareExchange(ref _drainTargetAck, targetAck, Unobserved) != Unobserved) return completion.Task;
// target 先于 flag：观测侧可能在 claim 落地的那一刻读 flag，未发布的 target 会读成 -1（比较里的全 1）
Interlocked.CompareExchange(ref _draining, 1, 0);
```

- 只有 CAS 赢家会到达 flag 设置与后面的 `TryCompleteDrain`；`_draining` 现在不再是争抢对象，
  它的 `CompareExchange` 保留为「flag 先于复查」的 full fence，与观测侧 CAS-max 构成原有的
  store-buffering 配对（`Interlocked.MemoryBarrier()` 与 `QuiescenceScope` 先例一并保留，未动 N1）。
- 顺序不变：target CAS（full fence）→ flag CAS（full fence）→ Phase → barrier → `TryCompleteDrain`，
  因此不存在「观测侧看到 `_draining != 0` 而 target 还是 -1」的窗口。
- 零分配、无锁等待：多出的一次 CAS 只在每次 association 的 clean end 上发生一次，每包路径未变。
- XML doc 明确写了「armed once per association：首个调用者发布 target，后续调用者加入同一 cell 并沿用首个 target」，
  并发重复调用不再能替换首个 arm 的完成谓词。

### 2.3 验证

- `TcpRedirect` 项目 168/168 绿，覆盖 `ArmingADrainNeverRewritesAClosingPhase`、`AForwardAckThatCoversTheCloseCompletesTheArmedDrain`、
  `APiggybackedAcknowledgementEndsTheDrainWithoutWaiting`、`AStragglerDuringTheDrainResolvesToTheSameAssociationAndArmsNoSetup` 等。
- 并发形态没有加新测试：目标 CAS 之前的读—写窗口字段私有、无法从测试侧停放（没有 seam），
  按 test-stability §2.5 的口径登记为 residual；顺序重复语义由现有 facts 继续钉住。

## 3. F5 — Phase 守卫的注释改成精确描述

### 3.1 选择

在「收紧」与「改注释」之间选了后者，理由：

- 真正收紧要把 `Phase` 从自动属性改成 int 落地的 CAS 形式，会改变这个公开属性的存储与内存语义，
  而该竞态没有可观察的行为后果（`code-check.md` §2-3、§2-14），报告本身也不推荐为此改公开属性。

### 3.2 注释现在说了什么

```csharp
// Best-effort guard: the read and the store are not one atomic step, so a concurrent retire
// that moved the phase to Closing in between can be overwritten back to Draining. The race is
// benign: the retired association is already out of the session dictionary and both indexes,
// every production phase reader either requires Redirecting (which Draining already differs
// from) or does not distinguish Draining from Closing, and drain completion is gated by the
// armed flag, never by the phase.
if (Phase != RelayPhase.Closing) Phase = RelayPhase.Draining;
```

它不再声称「never rewritten back」；良性理由三条都能在代码里核对：retire 先把 session 移出
`_sessions` 并删掉 alias 索引（`TcpRedirectSessionStore.cs` 的 retire 临界区），生产 `Phase` 读者只有
`== Redirecting` 两处（`RemoveExpiredAsync`、`TryAttachRelay`）且数据路径不读 `Phase`，
drain 的完成只由 `_draining` 门控。

测试 `ArmingADrainNeverRewritesAClosingPhase` 仍钉住顺序场景；并发窗口同样没有 seam，residual 已记录。

## 4. F6 — TrackClientAck 的 span oracle 孪生

### 4.1 决定

**补上**，并且让它真的被当作 oracle 使用：`TcpSequenceObservation.cs` 里新增
`public static void TrackClientAck(ReadOnlySpan<byte> frame, TcpRedirectAssociation association)` 与私有
`TryReadTcpAcknowledgement(span, out uint)`，位置紧挨 layout 孪生，形状与既有的
`TrackClientSequence` / `TrackServerSequence` 双入口完全一致（span 侧自己 `IPTcpUdpPacket.TryParse`，
用解析出的 `view.IPHeaderLength` 定位 TCP 头，所以 options / IPv6 extension header 都会跟随解析）。

### 4.2 不是死代码：两个新 fact 把它当 oracle

在既有 oracle parity 所在文件 `tests/WinForward.Protocols.Tests/PacketLayoutTests.cs` 补了两条：

- `AcknowledgementEntryPointsAgree`：IPv4、IPv4+TCP options(24 B 头)、IPv6、IPv6+hop-by-hop 四种帧
  都通过解析偏移写入 `0x89AB_CDEF`，断言 span 入口（oracle）与 layout 入口读数一致；无 ACK 位的 SYN 帧
  两个入口都必须保持 unobserved（zero 覆盖上半个序列空间的假阳性防护）。
- `DefaultedLayoutObservesNoAcknowledgement`：`default(PacketLayout)` 被 layout 入口拒绝（`IsTcp` 含
  validity stamp），而 span oracle 仍读到真实 ACK——与既有 `DefaultedLayoutObservesNoSequence` 同形。

孪生被 friend assembly（`WinForward.Protocols.Tests`）真实引用，与既有两个 span 孪生的使用方式相同，
因此不会变成 `UnusedMember`；`jb inspectcode` 全量按用户指示留到后续阶段。

## 5. 本次验证汇总

```console
$ dotnet build WinForward.slnx -c Release -m:1
Build succeeded.
    0 Warning(s)
    0 Error(s)                      # BUILD_EXIT=0

$ dotnet test tests/WinForward.Performance.Tests -c Release --no-build -m:1 --filter "FullyQualifiedName~TcpRedirectDataPathAllocationGateTests"
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 185 ms
                                    # GATE_EXIT=0

$ dotnet test tests/WinForward.Runtime.TcpRedirect.Tests -c Release --no-build -m:1
Passed!  - Failed:     0, Passed:   168, Skipped:     0, Total:   168, Duration: 1 s
                                    # TCP_EXIT=0

$ dotnet test tests/WinForward.Protocols.Tests -c Release --no-build -m:1 --filter "FullyQualifiedName~PacketLayoutTests"
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 212 ms
                                    # PROTO_EXIT=0

$ dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore --include \
    src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs \
    src/WinForward.Runtime/TcpRedirect/TcpSequenceObservation.cs \
    benchmarks/WinForward.Benchmarks/Perf/TcpRedirectDataPathBenchmarks.cs \
    tests/WinForward.Performance.Tests/TcpRedirectDataPathAllocationGateTests.cs \
    tests/WinForward.Protocols.Tests/PacketLayoutTests.cs
FORMAT_EXIT=0
FORMAT_BYTES=0                      # 输出 0 字节
```

上述结果全部来自探针还原后的最后一次构建；探针本身的结果在 §1.2。

## 6. 未做与残留

- **未做**：F1（冻结快照变体，用户另行处理）、F3（spec 同步属后续阶段）、F7/F8（记录为接受）；
  N1–N6 未顺手改；manual FIN / removal 路径、任务文档、任何 spec 文件都没有触碰；没有 git commit。
- **残留（已有记录，本次只是复核）**：F4 的并发 arm 与 F5 的并发相位回写都没有可停放的测试 seam，
  按 test-stability §2.5「要么钉住、要么记录」继续登记为 residual；两者的生产可达性判断未变。
- **后续阶段**：全量四道门（solution 全测、全量 `dotnet format`、`jb inspectcode`、以及 VM 实验）
  按用户指示留到最终收口，不在本次轻量验证范围内。
