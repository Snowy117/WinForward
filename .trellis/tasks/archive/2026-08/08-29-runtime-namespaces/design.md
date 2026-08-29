# Design: WinForward.Runtime sub-namespace split

## Namespace Layout (final)

| Namespace | Directory | Files |
|---|---|---|
| `WinForward.Runtime` (root) | `./` | FlowDispatcher, PacketFlowClassifier, IdleExpirySweeper, SelfTrafficRegistry, RuntimeLogging |
| `WinForward.Runtime.Capture` | `Capture/` | CaptureAdapterScopeResolver, CaptureLifecycle, CapturePacketProcessor, MultiAdapterCaptureLoop, NdisAdapterModeController, NdisPacketActionExecutor, NdisPacketReinjector |
| `WinForward.Runtime.TcpRedirect` | `TcpRedirect/` | TcpFrameRewriter, TcpProxyCoordinator, TcpProxyRelay, TcpRedirectAcceptor, TcpRedirectInjector, TcpRedirectInterfaces, TcpRedirectListener, TcpRedirectLogging, TcpRedirectSessionStore, TcpRedirectSetup, TcpRedirectTable, TcpRedirectTombstoneTable, TcpSequenceObservation, **ClientResetInjector** |
| `WinForward.Runtime.UdpProxy` | `UdpProxy/` | UdpAssociations, UdpProxyCoordinator, UdpProxySession, UdpResponseReinjector |
| `WinForward.Runtime.Socks5` | `Socks5/` | Socks5ControlConnection, Socks5UdpTransport |

Rationale for the three non-obvious calls (evidence in `research/runtime-internal-structure.md`):

1. **ClientResetInjector → TcpRedirect** (not dispatch core). It is defined in terms of `TcpRedirectSession`, `TcpRedirectAssociation`, `ITcpRedirectInjector`, `ITcpAcceptedConnection` (`ClientResetInjector.cs:22-25,33,38,72,88`) and is consumed by `TcpProxyCoordinator.cs:28,55`, `TcpRedirectAcceptor.cs:14,21`, `TcpRedirectSetup.cs:32,35`. Dispatch ↔ TcpRedirect bidirectional cycle dissolves.
2. **Socks5 as its own group** (not inside UdpProxy). `TcpProxyRelay.cs:35` calls `Socks5ControlConnection.ConnectAsync` — the SOCKS5 dialer is shared by TCP relay and UDP transport. A `.Socks5` namespace gives both dependents a clean single using; also mirrors `WinForward.Protocols` (codec) without colliding.
3. **Dispatch core stays at root.** `SelfTrafficRegistry` (13 external files), `FlowDispatcher`, `PacketFlowClassifier`, `IdleExpirySweeper` form the orchestration vocabulary every other group references; root placement means the 30 external consumers keep `using WinForward.Runtime;` unchanged and only add sub-namespace usings where they touch moved types (e.g. `CapturedFlowPacket`, 9 files, gains `using WinForward.Runtime.Capture;`).

## Known cross-group edges (acceptable, resolved by `using`)

| From → To | Evidence | Resolution |
|---|---|---|
| root FlowDispatcher → TcpRedirect (`TcpRedirectOutcome`) | `FlowDispatcher.cs:70,74,196,200` | `using WinForward.Runtime.TcpRedirect;` in FlowDispatcher.cs |
| root IdleExpirySweeper → TcpRedirect + UdpProxy (coordinators) | `IdleExpirySweeper.cs:15-16,27-28` | two usings |
| Capture NdisPacketActionExecutor → TcpRedirect + UdpProxy | `NdisPacketActionExecutor.cs:21-22,27,129` | two usings |
| Capture (`IPacketReinjector`) ← TcpRedirect, UdpProxy | `TcpRedirectInjector.cs:7`, `UdpResponseReinjector.cs:41,64` | dependents add `using ...Capture;` (direction: protocol layers onto driver abstraction — fine) |
| TcpRedirect TcpProxyRelay → Socks5 | `TcpProxyRelay.cs:35` | using |
| Capture NdisPacketActionExecutor (`TcpRedirectOutcome`) | `NdisPacketActionExecutor.cs:87,98,106` | using |

Doc-comment-only "dependencies" (TcpProxyCoordinator→UdpProxyCoordinator, TcpRedirectTable→UdpAssociationTable, PacketFlowClassifier→FlowDispatcher per research §false-positives) are ignored — no using needed for comments unless compiler warns on cref (add using only if warning appears).

## Mechanical strategy

1. `git mv` each file into its subdirectory (PascalCase dir name = namespace suffix). SDK-style csproj picks up nested dirs automatically; no csproj edit.
2. Rewrite `namespace WinForward.Runtime;` → `namespace WinForward.Runtime.<Group>;` in moved files.
3. Using-directive fixup, scripted (python): build type→new-namespace map from the 26 moved files' declarations (71 types total; nested types qualify under their parent). For every `.cs` file under `src/`, `tests/`, `benchmarks/`:
   - word-boundary-match each moved type name in file text (excluding comments is unnecessary — over-adding a using is harmless, we prune later);
   - insert missing `using WinForward.Runtime.<Group>;` lines alphabetically into the existing using block;
   - keep `using WinForward.Runtime;` in place (root types remain; prune step decides).
4. Prune & verify via compiler: `dotnet build` (unused-using shows only if IDE0005 elevated; run `dotnet format` pass if needed) + `dotnet test`.
5. `InternalsVisibleTo` unaffected (assembly-level; internal types `TcpProxyRelay`, `TcpRedirectTombstoneTable`, `TcpAcceptedConnection` visible to Tests/Benchmarks regardless of namespace).

## Rollback

Single-purpose mechanical commit → `git revert` restores everything. No data/schema/config impact.

## Compatibility

- Binary compatibility irrelevant (single-solution, all consumers rebuilt together).
- No reflection/`nameof` usage on moved types anywhere (research `external-usage.md` §4: zero).
