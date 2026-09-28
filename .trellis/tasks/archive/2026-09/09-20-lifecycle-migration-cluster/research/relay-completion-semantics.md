# Research: `TcpProxyRelay` exit paths and `Completion` semantics

- **Query**: C3 question 5 — the three exit paths inside `RunPumpAsync`, and the exact semantics `Completion` exposes to `TcpRedirectAcceptor` (`WaitAsync(token)`) and to `TcpProxyRelay.DisposeAsync`
- **Scope**: internal
- **Date**: 2026-09-21

## `RunPumpAsync` — entry and set-up

`TcpProxyRelay` constructor `TcpProxyRelay.cs:117-127`:
```csharp
Completion = RunPumpAsync(upstream);   // :126
```
`public Task Completion { get; }` `:129`. `EndKind { get; private set; } = RelayEndKind.Faulted;` `:131`
(fault-visible default).

`RunPumpAsync(Stream upstream)` `:133-173`:
```csharp
await using var localStream = new NetworkStream(_localSocket, ownsSocket: true);   // :135
using var pumpCancellation = new CancellationTokenSource();                        // :136 (never disposed)
var localToUpstream = PumpAsync(localStream, upstream, _pumpBufferPool, pumpCancellation.Token);   // :137
var upstreamToLocal = PumpAsync(upstream, localStream, _pumpBufferPool, pumpCancellation.Token);   // :138
try
{
    var first = await Task.WhenAny(localToUpstream, upstreamToLocal).ConfigureAwait(false);  // :142
    var firstResult = await first.ConfigureAwait(false);                                     // :143
    ...
}
```

## The three exit paths

### Path 1 — stall fast-exit `:144-150`
```csharp
if (firstResult == PumpResult.Stalled)
{
    await pumpCancellation.CancelAsync().ConfigureAwait(false);                 // :146
    ObservePump(first == localToUpstream ? upstreamToLocal : localToUpstream);  // :147 — abandons the other pump
    EndKind = RelayEndKind.Stalled;                                             // :148
    return;                                                                     // :149 — returns WITHOUT awaiting WhenAll
}
```
`Completion` completes **successfully** (`Task` returns) while the other pump may still be running. The
abandoned pump gets only a `ContinueWith(OnlyOnFaulted)` observer (`ObservePump`). This is the deliberate
"stall fast-exit" the PRD says must not become "both pumps finished".

### Path 2 — normal finish `:152-164`
```csharp
if (first == localToUpstream) ShutdownSend(upstream);    // :152 half-close
else ShutdownSend(_localSocket);                          // :153
var results = await Task.WhenAll(localToUpstream, upstreamToLocal).ConfigureAwait(false);  // :155
if (results[0] == PumpResult.Stalled || results[1] == PumpResult.Stalled) { ... EndKind = Stalled; }  // :156-160
else { EndKind = RelayEndKind.CleanEnded; }               // :163
```
Both pumps are awaited. `Completion` completes successfully.

### Path 3 — fault `:166-172`
```csharp
catch
{
    await pumpCancellation.CancelAsync().ConfigureAwait(false);                                  // :168
    ObservePump(localToUpstream.IsCompleted ? upstreamToLocal : localToUpstream);                 // :169
    EndKind = RelayEndKind.Faulted;                                                              // :170
    throw;                                                                                        // :171
}
```
`Completion` completes **faulted**; the not-yet-completed pump is abandoned (observer attached). `EndKind`
is `Faulted`.

`PumpAsync` `:218-268` itself returns `PumpResult.Stalled` on any OCE (`:237-243` read; `:254-261` write)
and `Ended` on a zero-length read `:245-248`; a real IO fault propagates out of `PumpAsync` and reaches
`await first` / `await Task.WhenAll` in `RunPumpAsync`.

## `Completion` semantics as exposed

`ITcpRelay.Completion` (`TcpRedirectInterfaces.cs:55-58`) is the only member the public surface exposes:
> "transitions to a terminal state when both directions end, a direction stalls/errors, or the relay is
> torn down."

| Consumer | Site | Semantics relied on |
|---|---|---|
| `TcpRedirectAcceptor.ObserveRelayCompletionAsync` | `TcpRedirectAcceptor.cs:207` | `await relay.Completion.WaitAsync(token)` — bounded wait. OCE when `token.IsCancellationRequested` returns early `:209-212`; any other catch falls through to teardown `:213-216`. It then reads `ITcpRelayEndInfo.EndKind` `:222` and injects a client reset for `Stalled`/`Faulted`, then `tearDownSession` `:234`. The `WaitAsync` is the documented protection against "a relay whose completion never settles" (design §6; `hot-path.md`/acceptor comment `:200-202`). |
| `TcpProxyRelay.DisposeAsync` | `TcpProxyRelay.cs:322` | `await Completion` inside `finally`, swallowing any exception `:324-329`, **after** `_localSocket.Dispose()` `:309` and `_control.DisposeAsync()` `:312`. This makes dispose a real quiescence point (comment `:316-319`), but it also manufactures a fault in an in-flight pump that is then swallowed. |
| `TcpRelayFaultObserver.Observe` | `TcpRelayFaultObserver.cs:27` | `Completion.ContinueWith(OnlyOnFaulted, ExecuteSynchronously)`; reads `task.Exception` (the observation act) and debug-logs `tcp.relay.faulted` `:35-36`. |
| Tests | `TcpProxyRelayTests.cs:79,90,116,166,193`; `TcpRelayEndResetTests.cs:111,125,138`; `TcpRelayObservationTests.cs:73,93` | `Completion.WaitAsync(...)`, fault propagation, `IsCompleted`, `EndKind` for all three paths. |

**Key tension the design calls out (§4.4, confirmed here):** `RunPumpAsync` Path 1 (and Path 3) can leave a
pump running when `Completion` completes. Comment `:305-307` says "the dispose path discards the relay
without ever awaiting its completion", yet `:320-323` **does** `await Completion` — a stale comment. Because
`TcpProxyRelay.DisposeAsync` always awaits `Completion`, and `TcpRedirectAcceptor.DiscardUnattachedRelayAsync`
(`:131-150`, called from `:121`) calls `relay.DisposeAsync()`, the two external observers are redundant for
*observation* today; their remaining live effect is the `tcp.relay.faulted` debug event.

## Exact deletion set per `design.md` §4.4 / PRD

- `TcpProxyRelay.ObservePump` `TcpProxyRelay.cs:287-294` (the `:289` `_ = …ContinueWith`).
- `TcpRelayFaultObserver.cs` — whole file (27 lines of code + header).
- `RCS1075` pragma + empty `catch (Exception)` `TcpProxyRelay.cs:324-329`.
- `TcpRedirectAcceptor.cs:120` (`TcpRelayFaultObserver.Observe(unattachedRelay, logger);`).
- `TcpProxyRelay.cs:284` `_ = ShutdownSend(...)` → synchronous call.

## Caveats / Not Found

- `RunPumpAsync`'s `pumpCancellation` `:136` is never disposed on any of the three paths (leak; pre-existing).
- Whether `PumpAsync`'s two returned `Task<PumpResult>`s can be modelled as scope children is not answered
  by the design beyond §4.3's "scope tracks both pumps"; a pump that returns normally (Stalled/Ended) is
  **not** a fault, so `RecordFault` in the §4.4 snippet would not fire for it.
