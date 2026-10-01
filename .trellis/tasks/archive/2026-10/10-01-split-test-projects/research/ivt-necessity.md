# IVT necessity sweep (batch 4b step 5)

Each grant to a test friend was removed, its friend rebuilt (forcing a full rebuild when the
incremental build passed), and restored only when the build broke with an access error. Grants
whose friend does not reference the owner were dead on arrival.

| Verdict | Owning file | Friend | Detail |
|---|---|---|---|
| DEAD | `src/WinForward.Cli/WinForward.Cli.csproj` | `WinForward.Core.Tests` | WinForward.Core.Tests does not reference WinForward.Cli.csproj (removed during the pilot run) |
| NEEDED | `src/WinForward.Cli/WinForward.Cli.csproj` | `WinForward.Integration.Tests` | tests/WinForward.Integration.Tests/DurableCaptureBundleTests.cs(39,33): error CS0122: 'DurableCaptureBundle' is inacces |
| NEEDED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Core.Tests` | tests/WinForward.Core.Tests/CoreFlowStructuresTests.cs(47,61): error CS1061: 'PacketLease' does not contain a definitio |
| REMOVED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Configuration.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Integration.Tests` | tests/WinForward.Integration.Tests/DurableCaptureBundleTests.cs(91,15): error CS1061: 'ActivityBucketClock' does not co |
| NEEDED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Performance.Tests` | tests/WinForward.Performance.Tests/SweepAllocationGateTests.cs(161,41): error CS1061: 'FlowTable' does not contain a de |
| REMOVED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Protocols.Tests` | clean build, incremental and forced |
| REMOVED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Runtime.Capture.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Runtime.Flow.Tests` | tests/WinForward.Runtime.Flow.Tests/FlowDispatcherTests.cs(31,67): error CS1061: 'PacketLease' does not contain a defin |
| REMOVED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Runtime.Socks5.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Runtime.TcpRedirect.Tests` | tests/WinForward.Runtime.TcpRedirect.Tests/TcpFragmentHandlingTests.cs(46,70): error CS1061: 'PacketLease' does not con |
| NEEDED | `src/WinForward.Core/WinForward.Core.csproj` | `WinForward.Runtime.UdpProxy.Tests` | tests/WinForward.Runtime.UdpProxy.Tests/UdpSessionSetupTests.cs(44,37): error CS1061: 'NativeBufferPool' does not conta |
| REMOVED | `src/WinForward.NdisApi/NdisApiAbi.cs` | `WinForward.Core.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.NdisApi/NdisApiAbi.cs` | `WinForward.NdisApi.Tests` | tests/WinForward.NdisApi.Tests/NdisApiReadShapeTests.cs(363,63): error CS0122: 'INdisReadPacketCalls' is inaccessible d |
| NEEDED | `src/WinForward.NdisApi/NdisApiAbi.cs` | `WinForward.Runtime.Capture.Tests` | tests/WinForward.Runtime.Capture.Tests/NdisCaptureResilienceTests.cs(51,163): error CS0117: 'NdisCapturePumpOptions' do |
| NEEDED | `src/WinForward.NdisApi/NdisApiAbi.cs` | `WinForward.Runtime.TcpRedirect.Tests` | tests/WinForward.Runtime.TcpRedirect.Tests/TcpRedirectInjectionBatchingTests.cs(155,71): error CS0117: 'NdisApiDriver'  |
| NEEDED | `src/WinForward.NdisApi/NdisApiAbi.cs` | `WinForward.Runtime.Flow.Tests` | tests/WinForward.Runtime.Flow.Tests/FlowDispatcherExecutorTests.cs(299,32): error CS0117: 'NdisCapturedPacket' does not |
| NEEDED | `src/WinForward.NdisApi/NdisApiAbi.cs` | `WinForward.Integration.Tests` | tests/WinForward.Integration.Tests/DurableCaptureBundleTests.cs(98,63): error CS1061: 'NdisCapturePump' does not contai |
| REMOVED | `src/WinForward.Protocols/WinForward.Protocols.csproj` | `WinForward.Core.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.Protocols/WinForward.Protocols.csproj` | `WinForward.Protocols.Tests` | tests/WinForward.Protocols.Tests/PacketLayoutTests.cs(118,45): error CS0117: 'PacketChecksums' does not contain a defin |
| NEEDED | `src/WinForward.Protocols/WinForward.Protocols.csproj` | `WinForward.Integration.Tests` | tests/WinForward.Integration.Tests/PacketPathWalkCountTests.cs(43,9): error CS0122: 'PacketPathProbe' is inaccessible d |
| REMOVED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Core.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.TestSupport` | tests/WinForward.TestSupport/UdpAssociationFakes.cs(168,12): error CS0122: 'UdpAssociationLease' is inaccessible due to |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Runtime.TcpRedirect.Tests` | tests/WinForward.Runtime.TcpRedirect.Tests/TcpProxyRelayTests.cs(232,49): error CS0122: 'TcpProxyRelay' is inaccessible |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Runtime.Flow.Tests` | tests/WinForward.Runtime.Flow.Tests/AdapterTransientRetryLogGateTests.cs(27,21): error CS0122: 'AdapterTransientRetryLo |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Runtime.UdpProxy.Tests` | tests/WinForward.Runtime.UdpProxy.Tests/UdpAssociationCapabilityTests.cs(332,20): error CS0122: 'UdpAssociationEvidence |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Runtime.Capture.Tests` | tests/WinForward.Runtime.Capture.Tests/AdapterListWatcherTests.cs(22,44): error CS0117: 'NdisAdapterListWatcher' does n |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Performance.Tests` | tests/WinForward.Performance.Tests/SweepAllocationGateTests.cs(445,20): error CS0122: 'TcpRedirectSessionStore' is inac |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Protocols.Tests` | tests/WinForward.Protocols.Tests/PacketLayoutTests.cs(69,36): error CS0122: 'TcpFrameRewriter' is inaccessible due to i |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.NdisApi.Tests` | tests/WinForward.NdisApi.Tests/CompositePacketArrivalSignalTests.cs(59,28): error CS0122: 'FlowAttributionWakeRegistry' |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Runtime.Socks5.Tests` | tests/WinForward.Runtime.Socks5.Tests/Socks5UdpTransportLeaseTests.cs(168,56): error CS0122: 'UdpAssociationPool' is in |
| NEEDED | `src/WinForward.Runtime/WinForward.Runtime.csproj` | `WinForward.Integration.Tests` | tests/WinForward.Integration.Tests/UdpProxyCompositionTests.cs(29,44): error CS0122: 'UdpAssociationPool' is inaccessib |
| REMOVED | `src/WinForward.Windows/Properties/AssemblyInfo.cs` | `WinForward.Core.Tests` | clean build, incremental and forced |
| NEEDED | `src/WinForward.Windows/Properties/AssemblyInfo.cs` | `WinForward.TestSupport` | tests/WinForward.TestSupport/CapturePipelineFakes.cs(120,50): error CS0122: 'IProcessOwnerTableReader' is inaccessible  |
| NEEDED | `src/WinForward.Windows/Properties/AssemblyInfo.cs` | `WinForward.Windows.Tests` | tests/WinForward.Windows.Tests/ProcessOwnerTableCacheTests.cs(209,20): error CS0122: 'TcpOwnerRow' is inaccessible due  |
| DEAD | `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj` | `WinForward.Core.Tests` | WinForward.Core.Tests does not reference WinForward.Benchmarks.csproj |
| NEEDED | `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj` | `WinForward.Performance.Tests` | tests/WinForward.Performance.Tests/UdpSessionBudgetScenarioTests.cs(205,42): error CS0122: 'SessionBudgetSink' is inacc |
| NEEDED | `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj` | `WinForward.Runtime.UdpProxy.Tests` | tests/WinForward.Runtime.UdpProxy.Tests/UdpSessionRetentionTests.cs(174,22): error CS0122: 'SoakScenario' is inaccessib |
| NEEDED | `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj` | `WinForward.NdisApi.Tests` | tests/WinForward.NdisApi.Tests/CapturePumpReadCallTests.cs(147,22): error CS0122: 'SoakScenario' is inaccessible due to |

Checked 39 grants in total (the pilot run covered the first, the sweep the remaining 38): **29 needed**
(restored after an access error), **8 removed** as unproven, and **2 dead** because the friend does not
reference the owning project. Removed grants:

- `WinForward.Cli` → `WinForward.Core.Tests` (dead)
- `WinForward.Benchmarks` → `WinForward.Core.Tests` (dead)
- `WinForward.Core` → `WinForward.Configuration.Tests`, `WinForward.Protocols.Tests`,
  `WinForward.Runtime.Capture.Tests`, `WinForward.Runtime.Socks5.Tests`
- `WinForward.NdisApi`, `WinForward.Protocols`, `WinForward.Runtime`, `WinForward.Windows` →
  `WinForward.Core.Tests` (the Core tests that consumed them moved to their own projects)
