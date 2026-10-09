# 冻结树提交前质量门报告（10-09-tcp-close-drain-primary）

- 仓库：/home/paff/Projects/WinForward
- 分支 / HEAD：master / 0aa6919（全部改动未提交，工作树包含本改动的最终形态）
- 验证窗口：2026-10-09 18:56:39 – 19:07:09（+0800）
- 主机：NixOS 26.11，28 核，13 GiB 内存（test-stability.md 记载的并行 OOM 风险机型）
- 结论：四道门全部通过；改动文件集与委托清单吻合（另有 5 个 Trellis 任务元数据文件在清单之外）；无验证探针与已删除符号残留；22 条 drain / 相关 fact 逐条执行并通过。支持提交。

---

## 0. 冻结状态核对

- 最新源码 mtime：18:55:22（src/WinForward.Runtime/TcpRedirect/TcpRedirectAcceptor.cs）。
- 强制全量重编译产物的 mtime：18:57:18–18:57:25，晚于全部源码，因此门运行在最终源码上。
- 门时序（+0800）：build 18:56:39–18:56:48 → 强制重编译 18:57:06–18:57:26 → 全量测试 18:57:54–18:58:58 → format 18:59:09–19:00:17 → jb warm 19:00:38–19:02:47 → jb cold 19:03:14–19:06:25 → 聚焦补充验证 19:06:59–19:07:09。
- 26 个门相关文件（25 个 tracked 改动 + 新增 TcpCloseDrainTests.cs）的 SHA-256 指纹在门运行前写入 /tmp/wf-tree-hashes.txt，全部门结束后复核完全一致（TREE_FINGERPRINT_UNCHANGED）。
- 例外（不影响四门）：.trellis/tasks/10-09-tcp-close-drain-primary/research/verification/drain-final/ 的三个文件于 18:57:30–18:58:08（测试门进行中）写入。该目录不属于 WinForward.slnx，不参与任何门；期间没有源码/测试文件改动。
- 测试以 -m:1 串行执行。本机 13 GiB 内存正是 test-stability.md 记录的 OOM 条件，串行是必要前提。

---

## 1. Gate 1 — Build

委托命令（增量）：

    $ dotnet build WinForward.slnx -c Release
    EXIT=0
    Build succeeded.
        0 Warning(s)
        0 Error(s)
    Time Elapsed 00:00:07.99

原始日志：/tmp/gate-build.out（36 行）。全部项目增量 up-to-date，7.99 s 说明此次基本未重新编译，故补做下面的强制重编译。

补充证据（--no-incremental 强制全量重编译，只写 bin/obj，不改任何源码）：

    $ dotnet build WinForward.slnx -c Release --no-incremental
    EXIT=0
    Build succeeded.
        0 Warning(s)
        0 Error(s)
    Time Elapsed 00:00:15.32

原始日志：/tmp/gate-build-forced.out（36 行）。

结论：绿。两次均 0 warning / 0 error；强编译覆盖全部 26 个项目。

---

## 2. Gate 2 — Tests

    $ dotnet test WinForward.slnx -c Release -m:1 --no-build
    EXIT=0

原始日志：/tmp/gate-test-1.out（57 行），耗时约 63 s，一次通过，无环境性红。

每个项目的汇总（取自日志中的 Passed! 行）：

| 项目 | Failed | Passed | Skipped | Total |
|---|---:|---:|---:|---:|
| WinForward.Analyzers.Tests | 0 | 18 | 0 | 18 |
| WinForward.Configuration.Tests | 0 | 121 | 0 | 121 |
| WinForward.Core.Tests | 0 | 63 | 0 | 63 |
| WinForward.E2E.Tests | 0 | 364 | 0 | 364 |
| WinForward.Integration.Tests | 0 | 24 | 0 | 24 |
| WinForward.NdisApi.Tests | 0 | 74 | 0 | 74 |
| WinForward.Performance.Tests | 0 | 139 | 0 | 139 |
| WinForward.Protocols.Tests | 0 | 74 | 0 | 74 |
| WinForward.Runtime.Capture.Tests | 0 | 120 | 0 | 120 |
| WinForward.Runtime.Flow.Tests | 0 | 183 | 0 | 183 |
| WinForward.Runtime.Socks5.Tests | 0 | 108 | 0 | 108 |
| WinForward.Runtime.TcpRedirect.Tests | 0 | 168 | 0 | 168 |
| WinForward.Runtime.UdpProxy.Tests | 0 | 164 | 0 | 164 |
| WinForward.Windows.Tests | 0 | 58 | 0 | 58 |
| 合计 | 0 | 1678 | 0 | 1678 |

- 14 个测试程序集全部绿，0 failed / 0 skipped，退出码 0。
- 未出现 OOM / Thread.StartCore 假红，因此没有需要串行复跑的环境性失败轮次。
- 文档数字偏差（不影响门的结论）：research/implementation-notes.md 记「Final: 1674 passed」，本树实测 1678，相差 4（本树多出 1 条 drain fact、2 条 drain-armed 分配门等）。

---

## 3. Gate 3 — Format

    $ dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
    EXIT=0

- dotnet format 自身 stdout+stderr 为 0 字节。/tmp/gate-format.out 共 7 字节，全部是验证脚本追加的 EXIT=0 一行。
- 全量 solution 分析，耗时约 68 s。

结论：绿（exit 0 且输出 0 字节）。

---

## 4. Gate 4 — inspectcode

### 4.1 warm 运行（使用本机 ~/.local/share/JetBrains 增量缓存）

    $ jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode-10-09-final.xml WinForward.slnx
    EXIT=0（按既定约定不信任退出码）

- XML：329 字节，python3 ElementTree 解析合法；Issues 为空，Issue 元素计数 0；无 CSharpErrors。
- 日志：/tmp/gate-jb.out（1026 行），660 个 Inspecting 条目；8 个关键改动文件（TcpRedirectAcceptor.cs、TcpResetBuilder.cs、TcpRedirectTable.cs、TcpSequenceObservation.cs、ClientResetInjector.cs、TcpCloseDrainTests.cs、TcpRelayEndCloseTests.cs、TcpRedirectDataPathBenchmarks.cs）均在检查清单内。
- 耗时 128.7 s，远快于文档的 10–20 分钟（缓存热），因此追加冷跑。

### 4.2 cold 运行（补充证据，全新缓存目录，未触碰用户缓存）

    $ jb inspectcode --caches-home=/tmp/jb-cache-cold-10-09-final -f=Xml -e=HINT -o=/tmp/jb-inspectcode-10-09-final-cold.xml WinForward.slnx
    EXIT=0

- XML：329 字节；Issue 计数 0；无 CSharpErrors；660 个 Inspecting 条目；耗时 190.5 s。
- diff -q 两份 XML：REPORTS_IDENTICAL。

结论：绿，0 Issue，冷/热一致。报告未出现 CSharpErrors 或 unused-member 级联，因此未触发「清 ~/.local/share/JetBrains 与 /tmp/JB 后重跑」的条件，用户缓存未被清理。

---

## 5. 附带核对 A — 改动文件集

委托期望 21 条（14 条源码/基准/测试 + 7 份 .trellis/spec/backend 文档）全部命中，多/少为 0：

- 13 条 tracked modified 的源码/测试 + 7 份 tracked modified 的 spec 文档；
- 1 条 untracked 新文件：tests/WinForward.Runtime.TcpRedirect.Tests/TcpCloseDrainTests.cs。

清单之外的改动（如实报告）：

- tracked modified 5 条，全部是 Trellis 任务元数据，无源码影响：
  - .trellis/tasks/10-07-tcp-close-drain/check.jsonl
  - .trellis/tasks/10-07-tcp-close-drain/implement.jsonl
  - .trellis/tasks/10-07-tcp-close-drain/prd.md
  - .trellis/tasks/10-07-tcp-close-drain/task.json
  - .trellis/tasks/archive/2026-10/10-06-tcp-half-close-fidelity/task.json
  （10-07 的 PRD/task.json 被改写为「以 drain 为主、删除手工 FIN」的最终设计；归档任务补上 10-09 的父子链接。）
- untracked：.trellis/tasks/10-07-tcp-close-drain/design.md 以及整个 .trellis/tasks/10-09-tcp-close-drain-primary/ 任务目录（research 文档与 Windows VM 验证产物）。

以上额外内容都不参与四道门；提交时需由任务所有者决定是否随源码一并纳入。

---

## 6. 附带核对 B — 残留检查

### 6.1 验证探针

    $ rg -n -e 'KeepAlive\(new byte' src tests benchmarks
    0 命中（rg exit 1）

全仓搜索只命中 benchmarks/results/2026-10-01-udp-session-footprint/README.md 的 2 处，是对旧分配门判别方法的历史记录，非可执行代码，且本改动未触碰该文件。

    $ rg -n -e 'GC\.KeepAlive' src/WinForward.Runtime/TcpRedirect/
    0 命中（rg exit 1）

### 6.2 已删除符号

    $ rg -n -e 'TryBuildFin|TryInjectClientCloseAsync|TcpRedirectClientClose|TcpRedirectRelayEndCloseFailed|InjectClientVisibleCloseAsync|tcp\.redirect\.clientClose' src tests benchmarks
    0 命中（rg exit 1）

扩大到全仓（含 .trellis 文档）仍为 0 命中；加宽集合（TcpFinAck、TryInjectClientCloseCoreAsync）同样 0 命中。

### 6.3 新符号确在（抽查）

- DrainCleanEndAsync：TcpRedirectAcceptor.cs:277
- ArmDrainAsync：TcpRedirectTable.cs:243
- TrackClientAck：TcpSequenceObservation.cs:167/179、TcpProxyCoordinator.Injections.cs:259
- ClientAckMax：TcpRedirectTable.cs:195

---

## 7. 附带核对 C — drain facts 执行证据

聚焦过滤运行（逐条测试名落盘 /tmp/gate-drain-facts.out）：

    $ dotnet test tests/WinForward.Runtime.TcpRedirect.Tests -c Release --no-build -m:1 \
        --filter "FullyQualifiedName~TcpCloseDrainTests|FullyQualifiedName~TcpRelayEndCloseTests|FullyQualifiedName~SequenceTrackerTests" \
        --logger "console;verbosity=normal"
    Total tests: 22
         Passed: 22
    EXIT=0

TcpCloseDrainTests（12 条，全部 Passed）：

1. CleanEndDrainsUntilTheClientAcknowledgesThenRetires
2. CleanEndWithoutAnAcknowledgementRetiresAtTheDeadline
3. CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain
4. CleanEndWithoutEndInfoRetiresImmediatelyWithoutADrain
5. CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges
6. TheRetireNeverPrecedesTheDrainExit
7. AStragglerDuringTheDrainResolvesToTheSameAssociationAndArmsNoSetup
8. AForwardAckThatCoversTheCloseCompletesTheArmedDrain
9. ArmingADrainNeverRewritesAClosingPhase
10. AnotherRetirePathEndsTheDrainImmediately
11. APiggybackedAcknowledgementEndsTheDrainWithoutWaiting
12. ClientAckTrackingIsAdvanceOnlyWrapsAndIgnoresFramesWithoutTheAckFlag

TcpRelayEndCloseTests（7 条，全部 Passed）：

1. FaultedRelayEndInjectsInWindowClientResetBeforeTeardown
2. StalledRelayEndInjectsClientResetBeforeTeardown
3. RelayWithoutEndInfoIsTreatedAsCleanEnd
4. CleanRelayEndWithoutObservedSequencesInjectsNothing
5. RelayEndKindIsCleanEndedWhenBothDirectionsFinish
6. RelayEndKindIsStalledWhenPumpStalls
7. RelayEndKindIsFaultedWhenPumpFaults

SequenceTrackerTests（3 条，全部 Passed）：

1. ConcurrentSequenceObservationsKeepTheLargerValue
2. TcpRedirectAssociationHoldsNoLockField
3. UnobservedTrackerReadsNullAndObservedZeroReadsZero

数量核对说明：委托文本写「8 条」，但树内不存在 8 条的 drain fact 清单。实测与文档口径如下，供委托人复核：

- 实际文件与 spec 一致：TcpCloseDrainTests 12 条（.trellis/spec/backend/tcp-client-close-injection.md 的 Locked by 段逐条列出 12 条）。
- research 的两张旧表（implementation-notes.md 第 5 节、code-check.md §2-15）列为 11 条，比最终文件少 1 条；removal-notes.md 第 4 节记录了 11 → 12 的最后一次增补。
- removal-notes.md 第 4 节记录 TcpRelayEndCloseTests 由 8 条减为 7 条；当前树中确为 7 条。数字 8 最可能来自这个减一前的计数。

另外三个改动过的测试文件也做了聚焦验证（/tmp/gate-adapted-facts.out，全部 EXIT=0）：

- PacketPathWalkCountTests 5/5（含改动的 RedirectPacketTakesZeroSequenceGateEntries）。
- PacketLayoutTests 10/10（含新增 AcknowledgementEntryPointsAgree、DefaultedLayoutObservesNoAcknowledgement）。
- TcpRedirectDataPathAllocationGateTests 10/10（含两条新增 ForwardHostDrainArmedLegAllocatesNoManagedBytes(ipv6: true/false)，对应 check-fixes.md 的「8 → 10 条精确门」）。

---

## 8. 环境性异常与复跑记录

1. 首次 build 是增量 no-op（7.99 s，全部 up-to-date）→ 追加 --no-incremental 强编译（15.32 s），两次均 0/0，报告同时记录。
2. 测试串行一次全绿，未出现 test-stability.md 描述的并行 OOM；无需环境性复跑。
3. jb warm 2.1 分钟远快于文档的 10–20 分钟 → 追加全新缓存冷跑（3.2 分钟），两份 XML 逐字节一致、均 0 Issue；未触发清理缓存的条件，用户缓存未被清理。
4. 测试门进行期间任务目录新增 drain-final 产物（18:57:30–18:58:08）；不属于 slnx，且源码/测试指纹前后一致。
5. xmllint 未安装（先前一次 XML_INVALID 是命令缺失，不是 XML 错误）；改用 python3 ElementTree 验证为 XML_OK。

---

## 9. 提交判断

四道门在这棵冻结节上全部为绿：build 0 warning / 0 error（含强制重编译）、test 1678 passed / 0 failed（-m:1 串行）、format exit 0 且输出 0 字节、inspectcode 0 Issue（冷/热两份报告一致）。改动文件集与委托清单完全吻合，额外只有 Trellis 任务元数据与任务目录；探针与已删除符号无残留；drain 相关 fact 逐条执行且通过。

基于以上证据，这棵树可以提交。提交前建议任务所有者顺带确认两点：是否把任务目录/元数据一并纳入提交；以及两处陈旧文档数字（1674 vs 1678、旧表 11 vs 实际 12）——它们不影响代码质量门，但会留在 research 记录里。

## 10. 原始证据位置

- /tmp/gate-build.out、/tmp/gate-build-forced.out
- /tmp/gate-test-1.out
- /tmp/gate-format.out
- /tmp/gate-drain-facts.out、/tmp/gate-adapted-facts.out
- /tmp/gate-jb.out、/tmp/gate-jb-cold.out
- /tmp/jb-inspectcode-10-09-final.xml、/tmp/jb-inspectcode-10-09-final-cold.xml
- /tmp/wf-tree-hashes.txt（26 个门相关文件的 SHA-256，前后复核一致）
