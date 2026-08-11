# Module Coverage Matrix

Audit date: 2026-08-10

The requirement described 41 production C# files. The checked source inventory contains 42: the
additional file is `src/WinForward.Windows/Properties/AssemblyInfo.cs`, assembly metadata with no
runtime logic. It is included below so every `src/**/*.cs` file has exactly one audit owner.

| Production file | Audit owner | Result | Evidence |
| --- | --- | --- | --- |
| `src/WinForward.Cli/Program.cs` | `audit-cli-integration` | CLI composition defect fixed; command/process coverage gaps remain. | `audit-findings-audit-cli-integration.md` |
| `src/WinForward.Configuration/ConfigurationModels.cs` | `audit-core-config` | CFG F2-F4 fixed. | `audit-findings-audit-core-config.md` |
| `src/WinForward.Core/Domain.cs` | `audit-core-config` | CFG F1 and F5 fixed. | `audit-findings-audit-core-config.md` |
| `src/WinForward.Core/IpPrefix.cs` | `audit-core-config` | Null parser boundary fixed with CFG F2. | `audit-findings-audit-core-config.md` |
| `src/WinForward.Core/PacketRuntime.cs` | `audit-core-config` | No defect confirmed. | `audit-findings-audit-core-config.md` |
| `src/WinForward.Core/Policy.cs` | `audit-core-config` | No defect confirmed. | `audit-findings-audit-core-config.md` |
| `src/WinForward.Core/ProcessSelectors.cs` | `audit-core-config` | No defect confirmed. | `audit-findings-audit-core-config.md` |
| `src/WinForward.NdisApi/NdisApiAbi.cs` | `audit-ndisapi` | NDIS-1, NDIS-5, NDIS-6 fixed. | `audit-findings-audit-ndisapi.md` |
| `src/WinForward.NdisApi/NdisApiDriver.cs` | `audit-ndisapi` | NDIS-1, NDIS-2, NDIS-3, NDIS-4, NDIS-6 fixed. | `audit-findings-audit-ndisapi.md` |
| `src/WinForward.NdisApi/NdisCapture.cs` | `audit-ndisapi` | NDIS-4 fixed; capture lifecycle reviewed by capture-flow audit. | `audit-findings-audit-ndisapi.md` |
| `src/WinForward.Protocols/IpTcpUdpPacket.cs` | `audit-protocols` | PRT-001 and PRT-003 fixed. | `audit-findings-audit-protocols.md` |
| `src/WinForward.Protocols/IpUdpPacket.cs` | `audit-protocols` | PRT-003 fixed. | `audit-findings-audit-protocols.md` |
| `src/WinForward.Protocols/PacketChecksums.cs` | `audit-protocols` | PRT-002, PRT-003, PRT-005 fixed. | `audit-findings-audit-protocols.md` |
| `src/WinForward.Protocols/Socks5State.cs` | `audit-protocols` | PRT-004 fixed. | `audit-findings-audit-protocols.md` |
| `src/WinForward.Protocols/Socks5Udp.cs` | `audit-protocols` | No defect confirmed. | `audit-findings-audit-protocols.md` |
| `src/WinForward.Protocols/UdpFrameBuilder.cs` | `audit-protocols` | PRT-006 fixed. | `audit-findings-audit-protocols.md` |
| `src/WinForward.Runtime/CaptureAdapterScopeResolver.cs` | `audit-runtime-capture-flow` | No defect confirmed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/CaptureLifecycle.cs` | `audit-runtime-capture-flow` | RCF-1 fixed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/CapturePacketProcessor.cs` | `audit-runtime-capture-flow` | RCF-2 fixed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/FlowDispatcher.cs` | `audit-runtime-capture-flow` | No additional defect confirmed; routing invariants reviewed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/IdleExpirySweeper.cs` | `audit-runtime-capture-flow` | No defect confirmed; direct controlled-time test remains a gap. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/MultiAdapterCaptureLoop.cs` | `audit-runtime-capture-flow` | Run-task join reviewed; direct pump seam remains a gap. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/NdisAdapterModeController.cs` | `audit-runtime-capture-flow` | No defect confirmed; driver integration remains a gap. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/NdisPacketActionExecutor.cs` | `audit-runtime-capture-flow` | No defect confirmed; fail-closed proxy paths reviewed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/NdisPacketReinjector.cs` | `audit-runtime-capture-flow` | No defect confirmed; native driver behavior is Windows-gated. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/PacketFlowClassifier.cs` | `audit-runtime-capture-flow` | No defect confirmed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/SelfTrafficRegistry.cs` | `audit-runtime-capture-flow` | No defect confirmed. | `audit-findings-audit-runtime-capture-flow.md` |
| `src/WinForward.Runtime/Socks5Client.cs` | `audit-runtime-udp-socks` | PRT-007 and RUS-001 through RUS-003 fixed. | `audit-findings-audit-runtime-udp-socks.md` |
| `src/WinForward.Runtime/TcpProxyCoordinator.cs` | `audit-runtime-tcp` | TCR-001 through TCR-007 and TCR-010 fixed. | `audit-findings-audit-runtime-tcp.md` |
| `src/WinForward.Runtime/TcpProxyRelay.cs` | `audit-runtime-tcp` | TCR-008 and TCR-009 fixed. | `audit-findings-audit-runtime-tcp.md` |
| `src/WinForward.Runtime/TcpRedirectInjector.cs` | `audit-runtime-tcp` | Forwarded/host direction contract reviewed. | `audit-findings-audit-runtime-tcp.md` |
| `src/WinForward.Runtime/TcpRedirectInterfaces.cs` | `audit-runtime-tcp` | Relay completion contract clarified with TCR-008/TCR-009. | `audit-findings-audit-runtime-tcp.md` |
| `src/WinForward.Runtime/TcpRedirectListener.cs` | `audit-runtime-tcp` | Accepted-peer identity fixed with TCR-010. | `audit-findings-audit-runtime-tcp.md` |
| `src/WinForward.Runtime/TcpRedirectTable.cs` | `audit-runtime-tcp` | TCR-001 and TCR-002 fixed. | `audit-findings-audit-runtime-tcp.md` |
| `src/WinForward.Runtime/UdpAssociations.cs` | `audit-runtime-udp-socks` | RUS-005 fixed. | `audit-findings-audit-runtime-udp-socks.md` |
| `src/WinForward.Runtime/UdpProxyCoordinator.cs` | `audit-runtime-udp-socks` | RUS-004 through RUS-007 fixed. | `audit-findings-audit-runtime-udp-socks.md` |
| `src/WinForward.Runtime/UdpResponseReinjector.cs` | `audit-runtime-udp-socks` | Host/forwarded adapter target and direction reviewed; Windows-gated. | `audit-findings-audit-runtime-udp-socks.md` |
| `src/WinForward.Windows/AdapterIdentity.cs` | `audit-windows` | W3 fixed. | `audit-findings-audit-windows.md` |
| `src/WinForward.Windows/IpHelperAbi.cs` | `audit-windows` | W1 and W2 fixed. | `audit-findings-audit-windows.md` |
| `src/WinForward.Windows/Platform.cs` | `audit-windows` | No defect confirmed. | `audit-findings-audit-windows.md` |
| `src/WinForward.Windows/ProcessAttribution.cs` | `audit-windows` | W1, W2, and W4 fixed. | `audit-findings-audit-windows.md` |
| `src/WinForward.Windows/Properties/AssemblyInfo.cs` | `audit-windows` | Assembly metadata only; no defect confirmed. | Source inspection |

The detailed evidence documents are archived under
`.trellis/tasks/archive/2026-08/08-10-audit-*/research/` and are indexed by
`audit-summary.md`.
