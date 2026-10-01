# InternalsVisibleTo grants applied in batch 2c

| Owning project | Test project | Evidence |
|---|---|---|
| WinForward.Runtime | WinForward.Runtime.TcpRedirect.Tests | 8 internal TcpRedirect types; CS0122 at TcpRelayEndResetTests.cs:243 |
| WinForward.Runtime | WinForward.Runtime.Flow.Tests | AdapterTransientRetryLogGate, FlowAttributionPendingIndex, AttributionAdmission, PendingFlowAttribution, AttributionClaim, FlowAttributionPipeline; CS0122 at FlowAttributionPendingIndexTests.cs:304 |
| WinForward.NdisApi | WinForward.NdisApi.Tests | INdisReadPacketCalls (NdisReadPacketCalls.cs:19); CS0122 at NdisApiReadShapeTests.cs:363 |

## Candidate removals (remove-and-rebuild in batch 4)

- `src/WinForward.NdisApi/NdisApiAbi.cs:6` grants `WinForward.Core.Tests`, but no .cs file left in
  tests/WinForward.Core.Tests/ mentions NdisApi; only its stale .csproj reference does.
| WinForward.Runtime | WinForward.Runtime.UdpProxy.Tests | 10 internal UDP types (UdpAssociationPool, UdpProxySession, UdpSessionSetup, …); CS0122 at UdpAssociationCapabilityTests.cs:331 |
| WinForward.Runtime | WinForward.Runtime.Capture.Tests | AdapterScopeDiff, NdisPacketActionExecutor lanes, DrainPendingSetupsAsync; CS0122/CS1061/CS7036 at AdapterEnumerationDiffTests.cs:15 |
| WinForward.NdisApi | WinForward.Runtime.Capture.Tests | NdisCapturePumpOptions.TransientRetryBaseDelay, NdisCapturePump.Diagnostics; CS0117 at NdisCaptureResilienceTests.cs:51 |
| WinForward.Runtime | WinForward.Performance.Tests | TcpRedirectSessionStore (TcpRedirectSessionStore.cs:30); CS0122 at SweepAllocationGateTests.cs:445 |
| WinForward.Benchmarks | WinForward.Performance.Tests | SessionBudgetAcceptance, SoakOptions, SessionBudgetSample/Sink (internal in Stability/); CS0122 at UdpSessionBudgetAcceptanceTests.cs:322 |

## Process finding

A single build's error list is NOT proof of convergence: the compiler truncates it (Performance.Tests
reported 38 diagnostics on the first pass while more remained hidden). Always fix, rebuild, and
confirm on a second deterministic build.
| WinForward.Core | all 9 test projects referencing Core (pre-granted) | PacketLease.Disposition (PacketRuntime.cs:82), NativeBufferPool.Stats (NativeBufferPool.cs:58); CS1061 at TcpFragmentHandlingTests.cs:46. Pre-granted because Core is the shared base and these hot-path seams recur; batch 4 verifies each entry by remove-and-rebuild. |
| WinForward.NdisApi | WinForward.Runtime.TcpRedirect.Tests | NdisApiDriver.MaxPacketsPerSendRequest (NdisApiDriver.cs:30); CS0117 at TcpRedirectInjectionBatchingTests.cs:155 |
| WinForward.Benchmarks | WinForward.Runtime.UdpProxy.Tests | SoakScenario, SoakOptions, SoakRunner (internal in Stability/); CS0122 at UdpSessionRetentionTests.cs:174 |
| WinForward.Windows | WinForward.Windows.Tests | TcpOwnerRow (CS0122 at ProcessOwnerTableCacheTests.cs:209) |
| WinForward.Runtime | WinForward.Protocols.Tests | TcpFrameRewriter, TcpSequenceObservation; CS0122 at PacketLayoutTests.cs:69 |
| WinForward.Protocols | WinForward.Protocols.Tests | PacketChecksums.TryRewriteTcpEndpointsFullRecompute (PacketChecksums.cs:217); CS0117 at PacketLayoutTests.cs:118 |
| WinForward.Runtime | WinForward.NdisApi.Tests | FlowAttributionWakeRegistry (FlowAttributionWakeRegistry.cs:19); CS0122 at CompositePacketArrivalSignalTests.cs:59 |
| WinForward.Benchmarks | WinForward.NdisApi.Tests | SoakScenario/SoakOptions/SoakRunner; CS0122 at CapturePumpReadCallTests.cs:147 |

## Per-project test totals (verified green, batch 2b)

| Project | Tests |
|---|---|
| WinForward.Core.Tests | 60 |
| WinForward.Configuration.Tests | 101 |
| WinForward.Protocols.Tests | 71 |
| WinForward.NdisApi.Tests | 74 |
| WinForward.Windows.Tests | 58 |
| WinForward.Runtime.Capture.Tests | 115 |
| WinForward.Runtime.Flow.Tests | 151 |
| WinForward.Runtime.TcpRedirect.Tests | 154 |
| WinForward.Runtime.UdpProxy.Tests | 154 |
| WinForward.Performance.Tests | 129 |
| **subtotal** | **1067** |
| WinForward.Runtime.Socks5.Tests | pending |
| WinForward.Integration.Tests | pending |

Target: 1141 across the twelve split projects (+18 analyzers = 1159 solution total).

## Cross-test-project dependencies

A global scan indexed 205 test classes and found exactly one cross-project reference:
`IPFragmentTests` (Protocols.Tests) called `TcpFragmentHandlingTests.BuildIpv4Fragment/BuildIpv6Fragment`,
which had moved to Runtime.TcpRedirect.Tests. Resolved by moving both builders verbatim into
`tests/WinForward.TestSupport/FrameBuilders.cs` (the shared-fixture home they always belonged in) and
requalifying the three call sites. Re-scan after the fix: none remain.
| WinForward.Cli | WinForward.Integration.Tests | DurableCaptureBundle (CS0122 at DurableCaptureBundleTests.cs:39); UdpProxyComposition/UdpProxyComposer (silent — probe-verified in a scratch project) |
| WinForward.Runtime | WinForward.Runtime.Socks5.Tests | UdpAssociationPool (UdpAssociationPool.cs:56); CS0122 at Socks5UdpTransportLeaseTests.cs:167 |
| WinForward.Protocols | WinForward.Integration.Tests | PacketPathProbe (PacketPathProbe.cs:16); CS0122 at PacketPathWalkCountTests.cs:43 |
| WinForward.NdisApi | WinForward.Integration.Tests | NdisCapturePumpOptions.BatchCapacity, NdisCapturePump.RunIterationForTests, NdisCapturedPacket.FromCapture; CS0117 at BatchedPassReinjectionE2eTests.cs:49 |
| WinForward.Runtime | WinForward.Integration.Tests | NdisPacketActionExecutor.PendingPassCount, UdpProxyCoordinator.ReceiveWindowSize, TcpProxyCoordinator.DrainPendingSetupsAsync, UdpAssociationPool; CS1061 at BatchedPassReinjectionE2eTests.cs:58 |

## Batch 4b — 必要性复验（2026-10-01）

批次 2c 先按「有编译器证据」建立授权集合，批次 4b 再由
`research/verify-ivt-necessity.py` 逐条复验：删掉声明 → 重建 friend（增量构建通过时追加一次
`--no-incremental` 全量重建确认）→ 只在构建因访问错误失败时装回。39 条（本任务新增 32 条 + 既有
`WinForward.Core.Tests` 条目 7 条）结果：**29 条有证据保留、8 条无证据删除、2 条死条目**。逐条明细
见 `research/ivt-necessity.md`。

删除的 10 条：

- `WinForward.Cli` / `WinForward.Benchmarks` → `WinForward.Core.Tests`：friend 根本不引用所属项目，
  授权从建立那天起就不可能有消费点。
- `WinForward.Core` → `WinForward.Configuration.Tests`、`Protocols.Tests`、`Runtime.Capture.Tests`、
  `Runtime.Socks5.Tests`：批次 2c 按「Core 是共享基座、热点接缝会反复出现」预授，实测无消费点。
- `WinForward.NdisApi` / `WinForward.Protocols` / `WinForward.Runtime` / `WinForward.Windows` →
  `WinForward.Core.Tests`：其消费方已随拆分移出 Core.Tests。

复验后全量 `dotnet build -c Release` 0 warning、`dotnet test -c Release` **1159 passed / 0 failed**
（13 个程序集）；文档所述类过滤器
`FullyQualifiedName~WinForward.Performance.Tests.HotPathAllocationGateTests` 命中 11/11。

**工程发现**：「删掉重建」在 friend 不在所属项目引用闭包内时是假阴性——MSBuild 什么都不重编译，
2 秒就「通过」。脚本因此先算引用闭包判死条目，再对增量通过的条目强制全量重建复核。
