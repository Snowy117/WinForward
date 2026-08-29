# Research: External usage of WinForward.Runtime types

- **Query**: Catalog every usage of types from project WinForward.Runtime (`src/WinForward.Runtime/*.cs`) inside `src/WinForward.Cli/**`, `tests/WinForward.Core.Tests/**`, `benchmarks/WinForward.Benchmarks/**`
- **Scope**: internal (codebase cross-reference)
- **Date**: 2026-08-29
- **Method**: Enumerated all type declarations in `src/WinForward.Runtime/*.cs` (71 top-level + nested types), then word-boundary matched each type name against every `.cs` file in the three external projects (bin/obj excluded). Verified with per-type `rg -l | wc -l` aggregation.

## Key facts

- WinForward.Runtime declares everything in a **single flat namespace `WinForward.Runtime`** — no sub-namespaces exist today (all 40 Runtime files use `namespace WinForward.Runtime;` file-scoped).
- `src/WinForward.Runtime/WinForward.Runtime.csproj` grants:
  - `<InternalsVisibleTo Include="WinForward.Core.Tests" />`
  - `<InternalsVisibleTo Include="WinForward.Benchmarks" />`

  So external tests/benchmarks legally reference **internal** Runtime types too (e.g. `TcpProxyRelay`, `TcpRedirectTombstoneTable`, `TcpAcceptedConnection`).
- `Directory.Build.props` sets `ImplicitUsings=enable` (SDK defaults only). **No `global using`** anywhere — every Runtime type reference in external files is resolved via a file-level `using WinForward.Runtime;`.

## 1. `using WinForward.Runtime...` directives in external files

All 30 external files that reference Runtime types use exactly the same directive — **`using WinForward.Runtime;`** (flat, no sub-namespace usages exist anywhere in the repo):

| File | Directive |
|---|---|
| `src/WinForward.Cli/Program.cs` | `using WinForward.Runtime;` |
| `benchmarks/WinForward.Benchmarks/Program.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/AdapterScopeAndFlowTableTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/CoreFlowStructuresTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/FlowDispatcherExecutorTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/FlowDispatcherTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/PacketParsingTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/RuntimeLoggingTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/Socks5ControlTimeoutTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/Socks5ProtocolTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/Socks5UdpAssociateTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpProxyCoordinatorCapacityTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpProxyCoordinatorConcurrencyTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpProxyCoordinatorLifecycleTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpProxyCoordinatorRewriteTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpProxyRelayTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpRedirectInjectorTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TcpRedirectTombstoneTableTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/UdpProxyCoordinatorLifecycleTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/UdpProxyCoordinatorTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/UdpReceiveResilienceTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/UdpRelayTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/UdpSetupQueueTests.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TestHelpers/CapturePipelineFakes.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TestHelpers/PacketReinjectorFakes.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TestHelpers/RecordingLogger.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TestHelpers/TcpCoordinatorFakes.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TestHelpers/UdpCoordinatorFakes.cs` | `using WinForward.Runtime;` |
| `tests/WinForward.Core.Tests/TestHelpers/UdpTransportFakes.cs` | `using WinForward.Runtime;` |

The remaining 22 external `.cs` files (e.g. `ConfigurationValidationTests.cs`, `NdisApiAbiTests.cs`, `TestHelpers/FrameBuilders.cs`, `TestHelpers/Socks5TestServer.cs`, etc.) have **no** Runtime using and reference **zero** Runtime types.

## 2. Per-file referenced Runtime types

Types marked `†` are `internal` in Runtime (reachable via InternalsVisibleTo). Nested types (`SelfTrafficKey`, `SelfTrafficToken`) shown by bare name.

| External file | Referenced Runtime types | # |
|---|---|---|
| `src/WinForward.Cli/Program.cs` | CaptureAdapterScopeResolver, CapturePacketProcessor, ConsoleRuntimeLogger, FlowDispatcher, IdleExpirySweeper, IPacketCaptureLoop, IPacketReinjector, IRuntimeLogger, MultiAdapterCaptureLoop, NdisAdapterModeController, NdisPacketActionExecutor, NdisPacketReinjector, SelfTrafficRegistry, Socks5UdpTransportFactory, TcpProxyCoordinator, TcpProxyRelayFactory, TcpRedirectInjector, TcpRedirectListenerFactory, TcpRedirectTable, TransactionalCaptureRuntime, UdpAdapterTarget, UdpProxyCoordinator, UdpResponseReinjector | 23 |
| `benchmarks/WinForward.Benchmarks/Program.cs` | CapturePacketProcessor, CapturedFlowPacket, FlowDispatcher, IPacketActionExecutor, IRuntimeLogger, ISelfTrafficGuard, IUdpProxyTransport, IUdpProxyTransportFactory, IUdpResponseSink, SelfTrafficKey, SelfTrafficRegistry, Socks5UdpReceiveResult, TcpProxyRelay†, UdpProxyCoordinator | 14 |
| `tests/.../TestHelpers/TcpCoordinatorFakes.cs` | CapturedFlowPacket, FlowDispatcher, IPacketActionExecutor†-iface, ITcpAcceptedConnection, ITcpProxyRelayFactory, ITcpRedirectInjector, ITcpRedirectListener, ITcpRedirectListenerFactory, ITcpRelay, NdisPacketActionExecutor, PacketCaptureMetadata, SelfTrafficRegistry, TcpProxyCoordinator, TcpRedirectTable | 13 |
| `tests/.../FlowDispatcherExecutorTests.cs` | CapturePacketProcessor, CapturedFlowPacket, FlowDispatcher, NativeFrameHandle, NdisPacketActionExecutor, PacketCaptureMetadata, PacketFlowClassifier, SelfTrafficRegistry, TcpProxyCoordinator, TcpRedirectTable | 10 |
| `tests/.../TestHelpers/CapturePipelineFakes.cs` | CapturedFlowPacket, IPacketActionExecutor, ISelfTrafficGuard, ITcpAcceptedConnection, ITcpProxyRelayFactory, ITcpRedirectInjector, ITcpRedirectListener, ITcpRedirectListenerFactory, ITcpRelay | 9 |
| `tests/.../TcpProxyCoordinatorConcurrencyTests.cs` | IdleExpirySweeper, RelayPhase, SelfTrafficKey, SelfTrafficRegistry, TcpProxyCoordinator, TcpRedirectOutcome, TcpRedirectTable | 7 |
| `tests/.../FlowDispatcherTests.cs` | CapturedFlowPacket, FlowDispatcher, IPacketActionExecutor, ISelfTrafficGuard, SelfTrafficKey, SelfTrafficRegistry, TcpRedirectOutcome | 6 |
| `tests/.../RuntimeLoggingTests.cs` | CapturedFlowPacket, ConsoleRuntimeLogger, FlowDispatcher, IPacketActionExecutor, ISelfTrafficGuard, RuntimeLogField | 6 |
| `tests/.../Socks5UdpAssociateTests.cs` | IUdpResponseSink, SelfTrafficRegistry, Socks5ControlConnection, Socks5UdpTransport, Socks5UdpTransportFactory, UdpProxyCoordinator | 6 |
| `tests/.../TestHelpers/UdpTransportFakes.cs` | IUdpProxyTransport, IUdpProxyTransportFactory, IUdpResponseSink, Socks5UdpReceiveResult, Socks5UdpReceiveSkipReason, UdpProxyCoordinator | 6 |
| `tests/.../UdpRelayTests.cs` | CapturedFlowPacket, NdisPacketActionExecutor, PacketCaptureMetadata, UdpAdapterTarget, UdpProxyCoordinator, UdpResponseReinjector | 6 |
| `tests/.../CaptureLifecycleTests.cs` | AdapterModeSnapshot, CaptureRuntimeState, IAdapterModeController, IPacketCaptureLoop, TransactionalCaptureRuntime | 5 |
| `tests/.../TcpProxyCoordinatorCapacityTests.cs` | NdisPacketActionExecutor, SelfTrafficRegistry, TcpProxyCoordinator, TcpRedirectOutcome, TcpRedirectTable | 5 |
| `tests/.../TcpProxyCoordinatorLifecycleTests.cs` | CapturedFlowPacket, SelfTrafficRegistry, TcpProxyCoordinator, TcpRedirectOutcome, TcpRedirectTable | 5 |
| `tests/.../TcpProxyCoordinatorRewriteTests.cs` | CapturedFlowPacket, SelfTrafficRegistry, TcpProxyCoordinator, TcpRedirectOutcome, TcpRedirectTable | 5 |
| `tests/.../UdpReceiveResilienceTests.cs` | IUdpResponseSink, SelfTrafficRegistry, Socks5UdpReceiveSkipReason, Socks5UdpTransport, UdpProxyCoordinator | 5 |
| `tests/.../TcpProxyRelayTests.cs` | SelfTrafficRegistry, TcpAcceptedConnection†, TcpProxyRelay†, TcpProxyRelayFactory | 4 |
| `tests/.../Socks5ControlTimeoutTests.cs` | SelfTrafficRegistry, Socks5ControlConnection, Socks5UdpTransport | 3 |
| `tests/.../UdpProxyCoordinatorLifecycleTests.cs` | RelayAlias, UdpAssociationTable, UdpProxyCoordinator | 3 |
| `tests/.../CoreFlowStructuresTests.cs` | RelayAlias, UdpAssociationTable | 2 |
| `tests/.../Socks5ProtocolTests.cs` | Socks5ControlConnection | 1 |
| `tests/.../TcpRedirectInjectorTests.cs` | IPacketReinjector, TcpRedirectInjector | 2 |
| `tests/.../TestHelpers/RecordingLogger.cs` | IRuntimeLogger, RuntimeLogField | 2 |
| `tests/.../TestHelpers/UdpCoordinatorFakes.cs` | IUdpProxyTransport, IUdpProxyTransportFactory | 2 |
| `tests/.../UdpProxyCoordinatorTests.cs` | Socks5UdpTransport, UdpProxyCoordinator | 2 |
| `tests/.../AdapterScopeAndFlowTableTests.cs` | CaptureAdapterScopeResolver | 1 |
| `tests/.../PacketParsingTests.cs` | PacketFlowClassifier | 1 |
| `tests/.../TcpRedirectTombstoneTableTests.cs` | TcpRedirectTombstoneTable† | 1 |
| `tests/.../TestHelpers/PacketReinjectorFakes.cs` | IPacketReinjector | 1 |
| `tests/.../UdpSetupQueueTests.cs` | UdpProxyCoordinator | 1 |

## 3. Aggregate: distinct Runtime types used externally (54 of 71)

Sorted by number of external files using each. `†` = internal type. "Declared in" = the Runtime source file.

| Type | Files | Declared in |
|---|---|---|
| SelfTrafficRegistry | 13 | `SelfTrafficRegistry.cs` |
| CapturedFlowPacket | 9 | `FlowDispatcher.cs` |
| UdpProxyCoordinator | 9 | `UdpProxyCoordinator.cs` |
| TcpRedirectTable | 7 | `TcpRedirectTable.cs` |
| TcpProxyCoordinator | 7 | `TcpProxyCoordinator.cs` |
| FlowDispatcher | 6 | `FlowDispatcher.cs` |
| IPacketActionExecutor | 4 | `FlowDispatcher.cs` |
| ISelfTrafficGuard | 4 | `FlowDispatcher.cs` |
| TcpRedirectOutcome | 5 | `TcpRedirectInterfaces.cs` |
| NdisPacketActionExecutor | 5 | `NdisPacketActionExecutor.cs` |
| Socks5UdpTransport | 4 | `Socks5UdpTransport.cs` |
| IUdpResponseSink | 4 | `UdpResponseReinjector.cs` |
| IUdpProxyTransport | 3 | `Socks5UdpTransport.cs` |
| IUdpProxyTransportFactory | 3 | `Socks5UdpTransport.cs` |
| IRuntimeLogger | 3 | `RuntimeLogging.cs` |
| IPacketReinjector | 3 | `NdisPacketReinjector.cs` |
| CapturePacketProcessor | 3 | `CapturePacketProcessor.cs` |
| PacketCaptureMetadata | 3 | `FlowDispatcher.cs` |
| SelfTrafficKey | 3 | `SelfTrafficRegistry.cs` (nested) |
| Socks5ControlConnection | 3 | `Socks5ControlConnection.cs` |
| CaptureAdapterScopeResolver | 2 | `CaptureAdapterScopeResolver.cs` |
| ConsoleRuntimeLogger | 2 | `RuntimeLogging.cs` |
| ITcpAcceptedConnection | 2 | `TcpRedirectInterfaces.cs` |
| ITcpRedirectInjector | 2 | `TcpRedirectInterfaces.cs` |
| ITcpRedirectListener | 2 | `TcpRedirectInterfaces.cs` |
| ITcpRedirectListenerFactory | 2 | `TcpRedirectInterfaces.cs` |
| ITcpProxyRelayFactory | 2 | `TcpRedirectInterfaces.cs` |
| ITcpRelay | 2 | `TcpRedirectInterfaces.cs` |
| IdleExpirySweeper | 2 | `IdleExpirySweeper.cs` |
| IPacketCaptureLoop | 2 | `CaptureLifecycle.cs` |
| PacketFlowClassifier | 2 | `PacketFlowClassifier.cs` |
| RelayAlias | 2 | `UdpAssociations.cs` |
| RuntimeLogField | 2 | `RuntimeLogging.cs` |
| Socks5UdpReceiveResult | 2 | `Socks5UdpTransport.cs` |
| Socks5UdpReceiveSkipReason | 2 | `Socks5UdpTransport.cs` |
| Socks5UdpTransportFactory | 2 | `Socks5UdpTransport.cs` |
| TcpProxyRelay† | 2 | `TcpProxyRelay.cs` |
| TcpProxyRelayFactory | 2 | `TcpProxyRelay.cs` |
| TcpRedirectInjector | 2 | `TcpRedirectInjector.cs` |
| TransactionalCaptureRuntime | 2 | `CaptureLifecycle.cs` |
| UdpAdapterTarget | 2 | `UdpResponseReinjector.cs` |
| UdpAssociationTable | 2 | `UdpAssociations.cs` |
| UdpResponseReinjector | 2 | `UdpResponseReinjector.cs` |
| AdapterModeSnapshot | 1 | `CaptureLifecycle.cs` |
| CaptureRuntimeState | 1 | `CaptureLifecycle.cs` |
| IAdapterModeController | 1 | `CaptureLifecycle.cs` |
| MultiAdapterCaptureLoop | 1 | `MultiAdapterCaptureLoop.cs` |
| NativeFrameHandle | 1 | `FlowDispatcher.cs` |
| NdisAdapterModeController | 1 | `NdisAdapterModeController.cs` |
| NdisPacketReinjector | 1 | `NdisPacketReinjector.cs` |
| RelayPhase | 1 | `TcpRedirectTable.cs` |
| TcpAcceptedConnection† | 1 | `TcpRedirectListener.cs` |
| TcpRedirectListenerFactory | 1 | `TcpRedirectListener.cs` |
| TcpRedirectTombstoneTable† | 1 | `TcpRedirectTombstoneTable.cs` |

### Runtime types with ZERO external usage (17) — free to move without touching external code

`ClientResetInjector`†, `NullRuntimeLogger`, `RedirectSetup`†, `RetiredSession`†, `SelfTrafficToken` (nested), `TcpFrameRewriter`†, `TcpRedirectAcceptor`†, `TcpRedirectAssociation`, `TcpRedirectListener`†, `TcpRedirectLogging`†, `TcpRedirectSession`†, `TcpRedirectSessionStore`†, `TcpRedirectSetup`†, `TcpSequenceObservation`†, `TcpSynKind`†, `UdpAssociation`, `UdpProxySession`†

### Indicative cluster blast-radius (name-prefix based, for the proposed `.Capture` / `.TcpRedirect` / `.UdpProxy` / core split)

- `TcpRedirect*` / `TcpProxy*` / `ITcp*` / `RelayPhase` / `Socks5ControlConnection` cluster → referenced in **15** external files (all TcpProxyCoordinator* tests, TcpProxyRelayTests, TcpRedirectInjectorTests, TcpRedirectTombstoneTableTests, Socks5ProtocolTests, FlowDispatcher{,Executor}Tests via `TcpRedirectOutcome`/`TcpRedirectTable`, both TestHelpers fakes files in the list, Cli Program.cs, benchmarks Program.cs).
- `Udp*` cluster (UdpProxyCoordinator, UdpAssociations types, UdpResponseReinjector types) → referenced in **13** external files.
- `Socks5Udp*` transport types span both TCP-proxy tests and UDP tests (used in UdpProxyCoordinatorTests, UdpReceiveResilienceTests, Socks5UdpAssociateTests, Socks5ControlTimeoutTests, UdpTransportFakes, Cli, benchmarks) — a naming split between TCP redirect and UDP proxy sides will cross-cut these.
- Capture/loop/flow/logging types (CaptureLifecycle, MultiAdapterCaptureLoop, FlowDispatcher, RuntimeLogging, SelfTrafficRegistry, packet processors/reinjectors) → present in nearly every consumer; `SelfTrafficRegistry` (13 files) and `CapturedFlowPacket` (9 files) are the widest-reaching single types overall.

Note: cluster assignment above is indicative only (derived from type-name prefixes and declaration files); the actual split mapping is a design decision outside this report.

## 4. Reflection / nameof / fully-qualified usage

- **Reflection: none.** Zero matches for `typeof(`, `GetType(`, `Activator.CreateInstance`, `Type.GetType`, `.GetMethod`, `.GetField`, `.GetProperty`, or the string "Reflection" in any external `.cs` file (bin/obj excluded). External code binds Runtime types purely statically.
- **`nameof(...)`: only in `benchmarks/WinForward.Benchmarks/Program.cs`** (lines 430, 553, 717, 726, 734, 740) — all `nameof` on **local parameters** (`frameSize`, `args`), none on Runtime type names. Tests use no `nameof` on Runtime types.
- **Fully-qualified `WinForward.Runtime.` inline spellings: none in any `.cs` file.** The only `WinForward.Runtime.` occurrences outside the Runtime project are `<ProjectReference Include=".../WinForward.Runtime.csproj" />` MSBuild paths in:
  - `src/WinForward.Cli/WinForward.Cli.csproj:20`
  - `tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj:13`
  - `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj:14`

  These reference the *project file path*, not namespaces, so they are unaffected by a namespace split (assembly name and csproj path stay put).

## Caveats / Not Found

- Word-boundary token matching cannot distinguish a code reference from a mention inside a comment/string; spot checks showed all hits are genuine code references, but a handful of comment-only matches cannot be fully excluded.
- Nested types are matched by bare name (`SelfTrafficKey`, `SelfTrafficToken`); qualified spellings like `SelfTrafficRegistry.SelfTrafficToken` also count as a `SelfTrafficRegistry` hit.
- External test/benchmark files may declare their own fakes *implementing* Runtime interfaces (e.g. TestHelpers fakes) — these still require the interface's namespace to resolve, so they are counted as usage.
- `tests/WinForward.Core.Tests` also ProjectReferences `WinForward.Cli` (csproj:15) and `WinForward.Cli` grants `InternalsVisibleTo` to the test project (Cli csproj:12); no Runtime-type visibility depends on that chain, but Cli-internal types used by tests are outside this report's scope.
