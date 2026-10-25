# Batch 5a design: Kernel runtime seam

## Layout

```text
src/WinForward.Kernel.Runtime/
  WinForward.Kernel.Runtime.csproj   (root props, net10.0, contracts reference)
  Relay/SocketIoctl.cs
  Relay/RedirectDestination.cs
  Relay/IRedirectContextSource.cs
  Relay/HostRedirectContextSource.cs
  Relay/ForwardedRedirectContextSource.cs
  Relay/BlockSink.cs
  Relay/ListenerSet.cs
```

The old `kernel/src/userland/WinForward.Userland` keeps `Relay/RelayServer.cs`, `PumpPair.cs`,
`Socks5Client.cs`, `OutboundConnector.cs`, `Socks5UdpAssociation.cs`, `ForwardedUdpRelay.cs`,
`Control/` and `Configuration/`; it gets a `ProjectReference` to the new runtime project. The
moved types keep their `WinForward.Userland.Relay` namespace, so the old sources resolve them
transitively. The new project references `WinForward.Kernel.Contracts` only (the moved sources use
`RedirectContext`, `ProbeForwardedRedirect` and `WfpDeviceClient`); it must not reference
`WinForward.Runtime`, `WinForward.NdisApi` or the driver.

Both `WinForward.slnx` and `kernel/src/userland/WinForward.Userland.slnx` include the new project
so a Release native build compiles it Release. `InternalsVisibleTo` for
`WinForward.Userland.Tests` only if the moved internals are asserted there.

## Behaviour

The move is source-identical except namespaces/usings/analyzer suppressions. Keep:

- `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` constants/levels and the `RedirectContext` magic/
  version/size checks;
- `RedirectDestination` host/forwarded field mapping and the family/protocol validation;
- `BlockSink` v4/v6 ports and the TCP bind-only fallback;
- `ListenerSet` wildcard/address binding, same-port retry, DualMode=false and UDP/TCP sockets;
- `HostRedirectContextSource`/`ForwardedRedirectContextSource` ordering and error text.

No new socket option, buffer size, retry count or timeout.

## Quality

The root analyzers apply to the new project. Fix genuine issues; for socket/PInvoke/interop code
use narrow, reasoned suppressions (project-scoped `.editorconfig` only for a specific analyzer and
path). No global NoWarn. The moved code is not a packet hot path; keep existing allocation shapes.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet build kernel/src/userland/WinForward.Userland.slnx -c Release
dotnet build kernel/tools/wfprobe/wfprobe.csproj -c Release
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
```

The Kernel test runner/Controller/wfprobe are the regression oracle for the moved helpers. Windows
driver E2E is not part of this batch.

## Risks and rollback

- Transitive project references across the kernel/root boundary; verify the actual native solution
  build, not just the root build.
- `ListenerSet`/`BlockSink` are stateful sockets; a wrong `using`/dispose move leaks a port. Keep the
  bodies identical and run the Kernel tests.
- The native solution must include the new project or Release builds the helper in Debug (the
  Batch 3 finding). Add it directly.
- Rollback: revert the move/project/solution/reference changes; the Kernel history holds the
  original paths.
