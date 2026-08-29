# Research: WinForward.Runtime internal structure (namespace-split feasibility)

- **Query**: Analyze `src/WinForward.Runtime/*.cs` — namespaces, type inventory, internal dependency map, cross-group coupling for candidate grouping (Capture / TcpRedirect / UdpProxy / Dispatch core / Root)
- **Scope**: internal
- **Date**: 2026-08-29
- **Method**: rg word-boundary type-name search per type across all 31 `.cs` files; doc comments (`///`) and line comments (`//`) excluded from "real code" edges; every cross-group edge below verified with file:line evidence.

---

## 1. Namespace status today

**Every one of the 31 `.cs` files declares exactly `namespace WinForward.Runtime;`** (file-scoped, no variations, no `WinForward.Runtime.*` sub-namespaces). The project also references sibling projects: `WinForward.Core`, `WinForward.Configuration`, `WinForward.Protocols`, `WinForward.NdisApi`, `WinForward.Windows` (see per-file using lists, section 4).

## 2. Type inventory per file

Accessibility and containing file of every top-level type (significant nested types indented under their parent).

### Capture (candidate group)

| File | Namespace | Types |
|---|---|---|
| `CaptureAdapterScopeResolver.cs` | `WinForward.Runtime` | `public static class CaptureAdapterScopeResolver` |
| `CaptureLifecycle.cs` | `WinForward.Runtime` | `public enum CaptureRuntimeState`; `public interface IAdapterModeController : IAsyncDisposable`; `public interface IPacketCaptureLoop : IAsyncDisposable`; `public sealed class TransactionalCaptureRuntime : IAsyncDisposable` |
| `CapturePacketProcessor.cs` | `WinForward.Runtime` | `public sealed class CapturePacketProcessor` |
| `MultiAdapterCaptureLoop.cs` | `WinForward.Runtime` | `public sealed class MultiAdapterCaptureLoop : IPacketCaptureLoop` |
| `NdisAdapterModeController.cs` | `WinForward.Runtime` | `public sealed class NdisAdapterModeController : IAdapterModeController` |
| `NdisPacketActionExecutor.cs` | `WinForward.Runtime` | `public sealed class NdisPacketActionExecutor : IPacketActionExecutor` |
| `NdisPacketReinjector.cs` | `WinForward.Runtime` | `public interface IPacketReinjector`; `public sealed class NdisPacketReinjector : IPacketReinjector` |

### TcpRedirect (candidate group)

| File | Namespace | Types |
|---|---|---|
| `TcpFrameRewriter.cs` | `WinForward.Runtime` | `internal static class TcpFrameRewriter`; `internal enum TcpSynKind` |
| `TcpProxyCoordinator.cs` | `WinForward.Runtime` | `public sealed class TcpProxyCoordinator : IAsyncDisposable`; `internal sealed class TcpRedirectSession` (primary-ctor class, line 366) |
| `TcpProxyRelay.cs` | `WinForward.Runtime` | `public sealed class TcpProxyRelayFactory : ITcpProxyRelayFactory`; `internal sealed class TcpProxyRelay : ITcpRelay` (nested `private enum PumpResult` :189) |
| `TcpRedirectAcceptor.cs` | `WinForward.Runtime` | `internal sealed class TcpRedirectAcceptor` |
| `TcpRedirectInjector.cs` | `WinForward.Runtime` | `public sealed class TcpRedirectInjector : ITcpRedirectInjector` |
| `TcpRedirectInterfaces.cs` | `WinForward.Runtime` | `public interface ITcpRedirectListenerFactory`; `ITcpRedirectListener : IAsyncDisposable`; `ITcpAcceptedConnection : IAsyncDisposable`; `ITcpProxyRelayFactory`; `ITcpRelay : IAsyncDisposable`; `ITcpRedirectInjector`; `public enum TcpRedirectOutcome` |
| `TcpRedirectListener.cs` | `WinForward.Runtime` | `public sealed class TcpRedirectListenerFactory : ITcpRedirectListenerFactory`; `internal sealed class TcpRedirectListener : ITcpRedirectListener`; `internal sealed class TcpAcceptedConnection : ITcpAcceptedConnection` |
| `TcpRedirectLogging.cs` | `WinForward.Runtime` | `internal static class TcpRedirectLogging` |
| `TcpRedirectSessionStore.cs` | `WinForward.Runtime` | `internal sealed record RetiredSession`; `internal sealed class TcpRedirectSessionStore` |
| `TcpRedirectSetup.cs` | `WinForward.Runtime` | `internal sealed record RedirectSetup`; `internal sealed class TcpRedirectSetup` |
| `TcpRedirectTable.cs` | `WinForward.Runtime` | `public enum RelayPhase`; `public sealed class TcpRedirectAssociation`; `public sealed class TcpRedirectTable` (nested `private readonly record struct ReverseRedirectTuple` :271) |
| `TcpRedirectTombstoneTable.cs` | `WinForward.Runtime` | `internal sealed class TcpRedirectTombstoneTable` (nested `private sealed record TombstoneEntry` :106, `private readonly record struct ReverseTuple` :109) |
| `TcpSequenceObservation.cs` | `WinForward.Runtime` | `internal static class TcpSequenceObservation` |

### UdpProxy (candidate group)

| File | Namespace | Types |
|---|---|---|
| `UdpAssociations.cs` | `WinForward.Runtime` | `public sealed class UdpAssociation`; `public sealed class UdpAssociationTable` |
| `UdpProxyCoordinator.cs` | `WinForward.Runtime` | `public sealed class UdpProxyCoordinator : IAsyncDisposable` (nested `private sealed class UdpSessionSlot` :494) |
| `UdpProxySession.cs` | `WinForward.Runtime` | `internal sealed class UdpProxySession : IAsyncDisposable` |
| `UdpResponseReinjector.cs` | `WinForward.Runtime` | `public interface IUdpResponseSink`; `public sealed class UdpResponseReinjector : IUdpResponseSink` |
| `Socks5ControlConnection.cs` | `WinForward.Runtime` | `public sealed class Socks5ControlConnection : IAsyncDisposable` (nested `private sealed record ConnectAttempt` :161) |
| `Socks5UdpTransport.cs` | `WinForward.Runtime` | `public enum Socks5UdpReceiveSkipReason`; `public interface IUdpProxyTransport : IAsyncDisposable`; `public interface IUdpProxyTransportFactory`; `public sealed class Socks5UdpTransportFactory : IUdpProxyTransportFactory`; `public sealed class Socks5UdpTransport : IUdpProxyTransport` |

### Dispatch core (candidate group)

| File | Namespace | Types |
|---|---|---|
| `FlowDispatcher.cs` | `WinForward.Runtime` | `public interface ISelfTrafficGuard`; `public interface IPacketActionExecutor`; `public sealed class FlowDispatcher` (nested `private enum PacketAction` :56) |
| `PacketFlowClassifier.cs` | `WinForward.Runtime` | `public static class PacketFlowClassifier` |
| `IdleExpirySweeper.cs` | `WinForward.Runtime` | `public sealed class IdleExpirySweeper : IAsyncDisposable` |
| `SelfTrafficRegistry.cs` | `WinForward.Runtime` | `public sealed class SelfTrafficRegistry : ISelfTrafficGuard` (nested `public readonly record struct SelfTrafficKey` :51, `public sealed class SelfTrafficToken : IDisposable` :62, `private readonly record struct WildcardKey` :57) |
| `ClientResetInjector.cs` | `WinForward.Runtime` | `internal sealed class ClientResetInjector` |

### Root

| File | Namespace | Types |
|---|---|---|
| `RuntimeLogging.cs` | `WinForward.Runtime` | `public interface IRuntimeLogger`; `public sealed class NullRuntimeLogger : IRuntimeLogger`; `public sealed class ConsoleRuntimeLogger : IRuntimeLogger` |

## 3. Internal dependency map (real code references, comments excluded)

Format: `File → files whose types it references`. Only intra-`WinForward.Runtime` type usage counts (constructor params, fields, method calls, base lists); `using` directives irrelevant since everything is one namespace. Doc-comment-only mentions listed separately where they could mislead.

### Capture

- `CaptureAdapterScopeResolver.cs` → *(none)*
- `CaptureLifecycle.cs` → *(none)*
- `CapturePacketProcessor.cs` → `FlowDispatcher.cs` (FlowDispatcher :23,:27), `PacketFlowClassifier.cs` (:60,:65), `RuntimeLogging.cs` (NullRuntimeLogger :31)
- `MultiAdapterCaptureLoop.cs` → `CaptureLifecycle.cs` (IPacketCaptureLoop), `CapturePacketProcessor.cs`
- `NdisAdapterModeController.cs` → `CaptureLifecycle.cs` (IAdapterModeController)
- `NdisPacketActionExecutor.cs` → `FlowDispatcher.cs` (IPacketActionExecutor :16), `NdisPacketReinjector.cs` (IPacketReinjector :27), `RuntimeLogging.cs` (:27,:33), `TcpProxyCoordinator.cs` (TcpProxyCoordinator :21,:27), `TcpRedirectInterfaces.cs` (TcpRedirectOutcome :87,:98,:106), `UdpProxyCoordinator.cs` (:22,:27,:129)
- `NdisPacketReinjector.cs` → *(none)*

### TcpRedirect

- `TcpFrameRewriter.cs` → `TcpRedirectTable.cs`
- `TcpProxyCoordinator.cs` → `ClientResetInjector.cs` (:28,:55), `RuntimeLogging.cs` (:24,:40,:52), `SelfTrafficRegistry.cs` (:38,:366,:370), `TcpFrameRewriter.cs`, `TcpProxyRelay.cs`, `TcpRedirectAcceptor.cs`, `TcpRedirectInterfaces.cs`, `TcpRedirectLogging.cs`, `TcpRedirectSessionStore.cs`, `TcpRedirectSetup.cs`, `TcpRedirectTable.cs`, `TcpRedirectTombstoneTable.cs`, `TcpSequenceObservation.cs`
- `TcpProxyRelay.cs` → `SelfTrafficRegistry.cs` (:10,:36), **`Socks5ControlConnection.cs` (:35)**, `TcpRedirectInterfaces.cs`, `TcpRedirectListener.cs` (TcpAcceptedConnection)
- `TcpRedirectAcceptor.cs` → `ClientResetInjector.cs` (:14,:21), `RuntimeLogging.cs` (:13,:21), `TcpProxyCoordinator.cs` (TcpRedirectSession), `TcpRedirectInterfaces.cs`, `TcpRedirectLogging.cs`
- `TcpRedirectInjector.cs` → **`NdisPacketReinjector.cs` (IPacketReinjector :7)**
- `TcpRedirectInterfaces.cs` → *(none)*
- `TcpRedirectListener.cs` → `TcpRedirectInterfaces.cs`
- `TcpRedirectLogging.cs` → `RuntimeLogging.cs` (:13,:24), `TcpProxyCoordinator.cs` (TcpRedirectSession), `TcpRedirectTable.cs` (TcpRedirectAssociation)
- `TcpRedirectSessionStore.cs` → `RuntimeLogging.cs` (:24,:40), `SelfTrafficRegistry.cs` (:266), `TcpProxyCoordinator.cs`, `TcpRedirectInterfaces.cs`, `TcpRedirectLogging.cs`, `TcpRedirectTable.cs`, `TcpRedirectTombstoneTable.cs`
- `TcpRedirectSetup.cs` → `ClientResetInjector.cs` (:32,:35), `RuntimeLogging.cs` (:30,:35), `SelfTrafficRegistry.cs` (:27,:35,:189), `TcpFrameRewriter.cs`, `TcpProxyCoordinator.cs`, `TcpRedirectInterfaces.cs`, `TcpRedirectLogging.cs`, `TcpRedirectSessionStore.cs`, `TcpRedirectTable.cs`, `TcpSequenceObservation.cs`
- `TcpRedirectTable.cs` → *(none real; `UdpAssociationTable` mentioned only in doc comment :120)*
- `TcpRedirectTombstoneTable.cs` → `TcpRedirectTable.cs`
- `TcpSequenceObservation.cs` → `TcpRedirectTable.cs`

### UdpProxy

- `UdpAssociations.cs` → *(none)*
- `UdpProxyCoordinator.cs` → `RuntimeLogging.cs` (:29,:41,:53,:68), `Socks5UdpTransport.cs`, `UdpAssociations.cs`, `UdpProxySession.cs`, `UdpResponseReinjector.cs`
- `UdpProxySession.cs` → `RuntimeLogging.cs` (:21,:48), `Socks5UdpTransport.cs`, `UdpAssociations.cs`, `UdpResponseReinjector.cs`
- `UdpResponseReinjector.cs` → **`NdisPacketReinjector.cs` (IPacketReinjector :41,:64)**, `RuntimeLogging.cs` (:46,:69,:79)
- `Socks5ControlConnection.cs` → *(none intra; uses `WinForward.Protocols` Socks5 types)*
- `Socks5UdpTransport.cs` → **`SelfTrafficRegistry.cs`** (:63,:65,:86,:89,:100,:105,:112,:119,:133), `Socks5ControlConnection.cs`

### Dispatch core

- `FlowDispatcher.cs` → **`TcpRedirectInterfaces.cs` (TcpRedirectOutcome :70,:74,:196,:200)**, `RuntimeLogging.cs` (:71,:74,:86)
- `PacketFlowClassifier.cs` → *(none real; `FlowDispatcher` mentioned only in doc comment :18)*
- `IdleExpirySweeper.cs` → `FlowDispatcher.cs` (:14,:26), `RuntimeLogging.cs` (:22), **`TcpProxyCoordinator.cs` (:15,:27)**, **`UdpProxyCoordinator.cs` (:16,:28)**
- `SelfTrafficRegistry.cs` → `FlowDispatcher.cs` (ISelfTrafficGuard :7)
- `ClientResetInjector.cs` → **`TcpProxyCoordinator.cs` (TcpRedirectSession :22,:25,:33,:72)**, **`TcpRedirectTable.cs` (TcpRedirectAssociation :23,:25,:38,:88)**, **`TcpRedirectInterfaces.cs` (ITcpRedirectInjector :25, ITcpAcceptedConnection :72)**, `RuntimeLogging.cs` (:21,:25)

### Root

- `RuntimeLogging.cs` → *(none)*

### Intra-group edge density (real code references)

| Group pair | Reference count |
|---|---|
| TcpRedirect → TcpRedirect | 140 |
| UdpProxy → UdpProxy | 39 |
| Capture → Capture | 5 |
| Dispatch → Dispatch | 3 |
| Everything → Root (RuntimeLogging) | 13 files use `IRuntimeLogger`/`NullRuntimeLogger` |

## 4. Cross-group edges that make the split awkward (real code, file:line evidence)

### A. ClientResetInjector (Dispatch) is deeply typed against TcpRedirect types

`ClientResetInjector.cs:22-25` holds `Func<TcpRedirectSession, ValueTask>` / `Func<TcpRedirectAssociation, ValueTask>` delegates and takes `ITcpRedirectInjector` in its ctor; `:33`, `:38`, `:72`, `:88` take/operate on `TcpRedirectSession` / `TcpRedirectAssociation` / `ITcpAcceptedConnection`. Meanwhile TcpRedirect files use it back: `TcpProxyCoordinator.cs:28,55`, `TcpRedirectAcceptor.cs:14,21`, `TcpRedirectSetup.cs:32,35`. **This is a genuine Dispatch ↔ TcpRedirect bidirectional cycle** — `ClientResetInjector` is arguably a TcpRedirect-domain type living in the Dispatch group.

### B. FlowDispatcher (Dispatch) returns/compares TcpRedirectOutcome

`FlowDispatcher.cs:70` (`Func<..., ValueTask<TcpRedirectOutcome>>? _reverseHandler`), `:74` ctor, `:196`, `:200`. Also `NdisPacketActionExecutor.cs:87,98,106` (Capture) switches on `TcpRedirectOutcome`. The reverse-path result enum defined in `TcpRedirectInterfaces.cs` is shared vocabulary for Dispatch + Capture.

### C. IdleExpirySweeper (Dispatch) holds both coordinators

`IdleExpirySweeper.cs:15-16` fields `_tcp` / `_udp`; ctor `:27-28` takes `TcpProxyCoordinator?` and `UdpProxyCoordinator?`. Sweep is a lifecycle-orchestration point above all three groups.

### D. NdisPacketActionExecutor (Capture) holds both coordinators

`NdisPacketActionExecutor.cs:21-22` fields `_tcpProxy` / `_udpProxy`; ctor `:27`; dispatch at `:129`. Capture-group packet sink forwards into TcpRedirect and UdpProxy group instances.

### E. TcpProxyRelay (TcpRedirect) dials Socks5ControlConnection (UdpProxy group)

`TcpProxyRelay.cs:35` — `await Socks5ControlConnection.ConnectAsync(...)`. `Socks5ControlConnection` is shared SOCKS5 dialing machinery used by both the TCP relay and `Socks5UdpTransport.cs`. Under the proposed grouping it lives in UdpProxy, making TcpRedirect → UdpProxy a hard code dependency (not just doc).

### F. IPacketReinjector seam lives in Capture, consumed by TcpRedirect + UdpProxy

`TcpRedirectInjector.cs:7` and `UdpResponseReinjector.cs:41,64` both take `IPacketReinjector` (defined in `NdisPacketReinjector.cs`, Capture group).

### G. SelfTrafficRegistry (Dispatch) consumed by TcpRedirect + UdpProxy

`Socks5UdpTransport.cs:63,65,86,89,100,105,112,119,133` (9 refs, incl. nested `SelfTrafficRegistry.SelfTrafficKey/Token`), `TcpProxyCoordinator.cs:38,366,370`, `TcpProxyRelay.cs:10,36`, `TcpRedirectSessionStore.cs:266`, `TcpRedirectSetup.cs:27,35,189`. Note the nested public types `SelfTrafficRegistry.SelfTrafficKey` / `.SelfTrafficToken` are part of this API surface, and `ISelfTrafficGuard` (implemented by SelfTrafficRegistry) is declared in `FlowDispatcher.cs:40`.

### H. CapturePacketProcessor (Capture) → FlowDispatcher + PacketFlowClassifier (Dispatch)

`CapturePacketProcessor.cs:23` (`private readonly FlowDispatcher _dispatcher;`), `:27` ctor; `:60`, `:65` call `PacketFlowClassifier.ClassifyNonFlow/ClassifyFlow`.

### I. Everything → IRuntimeLogger (Root)

13 files reference `IRuntimeLogger` / `NullRuntimeLogger` (CapturePacketProcessor:31, NdisPacketActionExecutor:27,33, TcpProxyCoordinator:24,40,52, TcpRedirectAcceptor:13,21, TcpRedirectLogging:13,24, TcpRedirectSessionStore:24,40, TcpRedirectSetup:30,35, ClientResetInjector:21,25, FlowDispatcher:71,74,86, IdleExpirySweeper:22, UdpProxyCoordinator:29,41,53,68, UdpProxySession:21,48, UdpResponseReinjector:46,69,79). RuntimeLogging as a shared root is well-placed — this edge is expected and clean.

### Doc-comment-only "edges" (NOT real dependencies — beware during mechanical analysis)

- `TcpProxyCoordinator.cs:10` → `UdpProxyCoordinator` (cref only)
- `TcpRedirectTable.cs:120` → `UdpAssociationTable` (cref only)
- `PacketFlowClassifier.cs:18` → `FlowDispatcher` (cref only)
- `NdisPacketActionExecutor.cs:12` → both coordinators (cref only; real refs are the ones listed in D)

## 5. Types NOT referenced by any other Runtime file (composition surface)

These are only constructed from outside the project (host `src/WinForward.Cli/Program.cs`, `tests/WinForward.Core.Tests`, `benchmarks/WinForward.Benchmarks`): `TransactionalCaptureRuntime`, `CaptureAdapterScopeResolver`, `MultiAdapterCaptureLoop`, `NdisAdapterModeController`, `IdleExpirySweeper`, `TcpRedirectInjector` (concrete class; its interface IS used internally), `ConsoleRuntimeLogger`. Consumers of `WinForward.Runtime`: `WinForward.Cli`, `WinForward.Core.Tests`, `WinForward.Benchmarks` (csproj ProjectReferences).

Composition chain in `Program.cs`: `ConsoleRuntimeLogger`(:117) → `SelfTrafficRegistry`(:182) → `NdisPacketReinjector`(:183) → `TcpProxyCoordinator`(:250, w/ `TcpRedirectListenerFactory`/`TcpProxyRelayFactory`/`TcpRedirectInjector`) → `UdpProxyCoordinator`(:340, w/ `Socks5UdpTransportFactory`/`UdpResponseReinjector`) → `NdisPacketActionExecutor`(:264) → `FlowDispatcher`(:265) → `CapturePacketProcessor`(:269) → `MultiAdapterCaptureLoop`(:269) → `IdleExpirySweeper`(:270) → `NdisAdapterModeController`(:196) → `TransactionalCaptureRuntime`(:197).

## 6. External project dependencies per file (`using WinForward.*`)

- `WinForward.Core`: 24 files (nearly universal — CapturedFlowPacket, FlowKey, Endpoint, TransportProtocol, etc.)
- `WinForward.Configuration`: 13 files (ValidatedConfiguration, Socks5Server)
- `WinForward.Protocols`: 11 files (TCP/UDP parsing, Socks5)
- `WinForward.NdisApi`: 6 files (NdisPacketBufferPool, driver interop) — CapturePacketProcessor, MultiAdapterCaptureLoop, NdisAdapterModeController, NdisPacketActionExecutor, NdisPacketReinjector, TcpRedirectInjector, FlowDispatcher, UdpResponseReinjector
- `WinForward.Windows`: 5 files (CaptureAdapterScopeResolver, MultiAdapterCaptureLoop, NdisAdapterModeController, CapturePacketProcessor, FlowDispatcher, TcpProxyCoordinator)

## Caveats / Not Found

- Reference detection is word-boundary regex on type names; C#-identical names elsewhere (e.g. a hypothetical `TcpRedirectListener` in other projects) were not an issue here — no name collisions found with sibling projects.
- Constructor-parameter vs field vs call-site usage not distinguished beyond what's quoted; the quoted lines are representative, not exhaustive, for high-fanout types (SelfTrafficRegistry in Socks5UdpTransport has 9 refs, all listed).
- `SelfTrafficRegistry.SelfTrafficKey` / `SelfTrafficToken` are nested public types and part of the cross-group API surface (used by TcpProxyRelay, Socks5UdpTransport, TcpProxyCoordinator, tests) — any namespace move must account for the qualified nested name.
