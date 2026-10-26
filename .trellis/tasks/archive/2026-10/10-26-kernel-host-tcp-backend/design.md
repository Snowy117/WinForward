# Batch 5c-2 design: Kernel host TCP backend

## Files

```text
src/WinForward.Cli/Backends/Kernel/
  KernelBackend.cs          IInterceptionBackend; startup, run loop, shutdown, exit codes
  KernelHostTcpService.cs   device config publication + listener/block sink + accept loops
  KernelRelaySession.cs     one accepted connection: redirect context -> target -> relay -> close
  KernelBackendLog.cs       [LoggerMessage] events
```

`WinForward.Cli.csproj` adds `WinForward.Kernel.Runtime` (Contracts is already referenced).
`KernelPolicyCompilation` gains `bool ContainsBlock` (fallback block or any rule action block).

## Startup

1. `WfpDeviceClient.Open()`; `GetStats().AbiVersion` must equal `ProbeIoctl.AbiVersion`.
2. `SetConfig(ProbeConfig.Disabled())` so a stale run cannot redirect.
3. Disable forwarded: `SetForwardedConfig(ProbeForwardedConfig.Disabled(listenerPort? use 0/placeholder))`
   and `SetForwardedBlob(ForwardedBlobCompiler.Compile(new ForwardedPolicyTable([], RuleAction.Permit)))`
   to clear stale forwarded state (M1 has no forwarded path).
4. `KernelPolicyCompiler.TryCompile(configuration, WindowsProcessPathNormalizer.ForWindows(), ...)`;
   on failure log the diagnostics and return 1.
5. `ListenerSet.Bind()` (wildcard v4/v6; TCP listeners are used, UDP sockets stay bound/unused).
6. `BlockSink.Bind(listenerSet.Port)` when `ContainsBlock`; otherwise no sink (port 0). The driver
   rejects enabling a blob with block/fallback block without a non-zero sink.
7. `SetRuleBlob(compilation.HostBlob)`; verify `GetRuleInfo()` installed/rule count/fallback/size.
8. `SetConfig(ProbeConfig.EnabledFor(pid, listenerSet.Port, [pid], blockSinkPort))`; verify
   `GetConfig()` (enabled/port/PID/self/sink).
9. Log ready and start the accept loops.

Any startup failure unwinds in reverse order and leaves the driver disabled. `WfpDeviceClient`
Win32/InvalidOperation faults are logged as Kernel backend errors and return 1; they must not be
caught by the CLI's NDISAPI-specific catch blocks as misleading NDISAPI errors (the backend catches
and returns before escaping).

## Accept and relay

- One accept loop per `ListenerSet.Listeners`; each accepted `Socket` becomes a
  `KernelRelaySession`.
- Session steps:
  1. `HostRedirectContextSource.TryRead(socket, out destination, out error)`; missing/invalid or
     `destination.Protocol != RedirectContext.ProtocolTcp` -> close with linger 0, count/log.
  2. `ruleIndex = destination.RuleIndex`; must be in `[0, TargetsByRuleIndex.Length)` and the entry
     must be a `ProxyTarget` with `Socks5`; otherwise fail closed (no default target, no fallback
     redirect).
  3. Capacity: if the active session count is at `TcpFlowCapacity`, close with linger 0 and count.
  4. Establish the relay through `TcpProxyRelayFactory.EstablishAsync(
     Endpoint.From(destination.EndPoint.Address, destination.EndPoint.Port), socket, target.Socks5,
     token)` on a setup worker bounded by `SetupWorkerCount` (0 = auto: 2×logical, floor 16). The
     raw overload sets NoDelay and owns the socket on success; it also builds the relay with the
     abortive close armed (`abortiveCloseOnFault`), so a `Stalled`/`Faulted` end closes the client leg
     with linger 0 from inside the pumps. The session must not set the linger itself: the pumps close
     the socket before `Completion` completes, so a post-Completion linger write is a swallowed
     `ObjectDisposedException` that degrades the reset to a FIN.
  5. `await relay.Completion`; read `ITcpRelayEndInfo.EndKind` for counting/logging only, then
     `relay.DisposeAsync()` exactly once and decrement the count; a clean end keeps the normal FIN.
- The accept loop never blocks on a slow SOCKS5 setup: the session task waits a setup slot, and the
  loop keeps accepting. Session tasks are tracked for the shutdown drain.
- Process attribution: the redirect context's `ProcessId` is logged; no owner-table work. The driver
  self-PID gate is configured at startup.

## Shutdown

The order is visibility first: **disable the host config before releasing anything** (the driver
must stop redirecting to a listener/sink that is about to disappear), then dispose the listener and
block sink, cancel/await the session tasks (bounded), clear the forwarded state, and dispose the
device last. A repeated dispose is idempotent.

## Program wiring

`RunCaptureAsync` builds the backend by provider:

```csharp
IInterceptionBackend backend = configuration.BackendDriver.Provider switch
{
    BackendProvider.WinpkFilter => new WinpkFilterBackend(...),
    BackendProvider.WinForwardKernel => new KernelBackend(...),
    _ => throw new InvalidOperationException(...),
};
```

The Kernel branch is Windows-only (`[SupportedOSPlatform]` + the existing platform check). The
Batch 5c-2 `KernelPolicyCompiler` is called by `validate` and again by the backend. `ProviderName`
returns the Kernel constant so the Batch 4 guard passes. The existing `RunBackendUntilCancelled`
wrapper and exit-code catches stay.

## Errors and observability

- Kernel backend logs: device/ABI/rule publication/listener/block sink/ready; per-session accept
  (rule index, original destination, PID), relay established/faulted/stalled, capacity refusal,
  context failures; shutdown summary. Use `[LoggerMessage]` events with new EventIds.
- Counters: at minimum `kernel.tcp.accepted/refused/capacityBlocked/active` and the relay
  established/faulted counts; mirror into `RuntimeCounters` where cheap. Full E2E counter parity is
  Batch 5d.
- No process path or credentials logged.

## Risks

- Device API misuse: follow the Kernel controller's call order and verify every readback.
- Stale state: disable host config before anything; clear forwarded state.
- Socket ownership: the relay owns the socket after a successful establish; the session keeps only
  the linger decision. A failed establish leaves the socket to the session, which closes it.
- Capacity/setup: do not block the accept loop; do not let the active count leak on early exits.
- The listener's UDP sockets are unused but bound; do not configure host UDP rules (the capability
  gate rejects them). The block sink uses its own port.
- The Windows E2E is deferred; the device path is reviewed, not claimed green.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --fast'
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-cli-5c2.xml src/WinForward.Cli/WinForward.Cli.csproj
```