# Batch 5c-1 design: raw-socket relay seam

## Entry point

`TcpProxyRelayFactory` (public, `WinForward.Runtime.TcpRedirect`) currently requires an
`ITcpAcceptedConnection` that only `TcpRedirectListener` can construct. Add:

```csharp
public ValueTask<ITcpRelay> EstablishAsync(
    Endpoint originalDestination,
    Socket acceptedSocket,
    Socks5Server server,
    CancellationToken cancellationToken)
```

It validates `acceptedSocket.RemoteEndPoint is IPEndPoint`, constructs the existing internal
`TcpAcceptedConnection(socket, Endpoint.From(remote))`, and delegates to the existing
`EstablishAsync(originalDestination, ITcpAcceptedConnection, server, token)`. The existing method
still requires the concrete type, so the packet path is unchanged. The new overload transfers
socket ownership to the relay; the caller must not dispose the socket after a successful call.

## End kind

Make `RelayEndKind` (`CleanEnded`/`Stalled`/`Faulted`) and `ITcpRelayEndInfo` public. The internal
`TcpProxyRelay` already implements the interface; the `TcpRedirectAcceptor` uses it unchanged. A
consumer checks `relay is ITcpRelayEndInfo` after `Completion` and can set linger 0 on
`Stalled`/`Faulted`. Document that the end kind is meaningful only after completion. No new
relay state or timeout.

## Tests

Use the existing `Socks5FakeUpstream`/relay test helpers:

- Create a loopback TCP socket pair (server accepted + client); call the raw overload with a fake
  SOCKS5 server that accepts CONNECT to the original destination; write both directions, assert
  bytes arrive and `EndKind == CleanEnded` after a clean close.
- A fake upstream that faults the connection yields `Faulted` (or the existing fault test shape);
  do not add a stall-duration test.
- Assert the accepted socket is disposed exactly once by the relay (the fake/counters), and the
  existing `ITcpProxyRelayFactory` path still passes.

## Risks / rollback

- Socket ownership: the accepted connection/relay owns the socket; a test must not double-dispose.
- `RemoteEndPoint` must be an `IPEndPoint`; an unconnected socket is a startup error.
- Making the end-kind types public is source-compatible; no ABI.
- Rollback: remove the overload/public types and tests; no runtime state.

## Verification

```bash
dotnet test tests/WinForward.Runtime.TcpRedirect.Tests -c Release
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --fast'
```
