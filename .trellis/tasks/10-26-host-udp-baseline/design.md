# Design: host UDP baseline

Parent design: `../10-26-kernel-host-udp/design.md` §1–§11. This batch implements the listener,
SOCKS5 UDP proxy, pass/block, capacity/idle and the Windows baseline E2E. Local target/DNS, EDNS0
and UoT are the next batch.

> **Status 2026-10-26 (deferred):** §1–§3 and §5 are implemented and preserved, but the product path
> is gated. A4 failed — the listener `sendto` reply exposes the listener endpoint, not the original
> destination — so §4's "remove the UDP-proxy rejection" is **superseded**: the configuration gate
> refuses UDP-capable proxy rules again (with the reply-source reason and `Provider = WinpkFilter`),
> `KernelHostService.HostUdpProxyEnabled` is false with a TODO, the UDP service/coordinator are
> constructed but the receive leg never starts, and the listener/ready/started lines say the host UDP
> proxy is disabled. The driver-injected reply leg belongs to `10-26-host-udp-reply-source`; none of
> the UDP data-path acceptance is claimed until it passes A4/A8. See `verification/deferred.md`.

## 1. Components

| File | Responsibility |
|---|---|
| `src/WinForward.Kernel.Runtime/Relay/WfpUdpContext.cs` | `TryParseControl` walks the cmsg chain (type 70, level 0/41) and extracts the 36-byte payload; decoding/validation reuses `RedirectDestination.TryParse` (magic/version/family/protocol/port/rule index/process id). Public, platform-neutral; no second ABI decoder. |
| `src/WinForward.Kernel.Runtime/Relay/WfpUdpReceiver.cs` | Windows `WSARecvMsg` loop over the dual-mode UDP socket; pooled reusable payload/control buffers; returns client `IPEndPoint`, payload memory and context; `WSAECONNRESET` is a skip; cancellation is socket close + scope token. |
| `src/WinForward.Kernel.Runtime/Relay/HostListenerSet.cs` | Binds v4 TCP, v6 TCP V6ONLY and one dual-mode v6 UDP wildcard on one port; sets the WFP redirect-context option (levels 0/41) and disables `SIO_UDP_CONNRESET` before bind; exposes `Port`, `TcpListeners`, `UdpSocket`; bounded port retries. |
| `src/WinForward.Cli/Backends/Kernel/HostUdpResponseSink.cs` (provisional) | `IUdpResponseSink`; currently sends `payload` from the receiving UDP socket to `flow.Local`. **A4 proved this source is the listener, not the original destination**; it must be replaced by a driver-injected/source-rewritten sink in `10-26-host-udp-reply-source`. The rest of the entry path is reusable. |
| `src/WinForward.Cli/Backends/Kernel/KernelHostUdpService.cs` | Receive loop, context validation, client-endpoint normalization, `FlowKey`/target resolution, coordinator admission, activity-clock tick and idle sweep, counters, dispose. |
| `src/WinForward.Cli/Backends/Kernel/KernelHostService.cs` | New host orchestrator: ABI check, stale disable, compile, bind `HostListenerSet`/`BlockSink`, start UDP, publish/verify blob/config, start TCP accept, ready, shutdown order. |
| `src/WinForward.Cli/Backends/Kernel/KernelHostTcpService.cs` | Refactored to TCP accept/relay/capacity only; receives the bound listeners, target table and scope from the orchestrator. |
| `src/WinForward.Cli/UdpProxyComposer.cs` | Core overload taking an `IUdpResponseSink` + host composition; the packet `Create` builds the reinjector and delegates. |
| `src/WinForward.Cli/Backends/Kernel/KernelBackend.cs` | Owns `SelfTrafficRegistry`, `Socks5AddressCache`, `SetupExecutor`, the host `NativeBufferPool`s, `ActivityBucketClock`, the host service and the relay pool; disposes in reverse order. |
| `src/WinForward.Configuration/ConfigurationKernelCapabilities.cs` | Remove the UDP-proxy rejection and the inert UDP-setting warnings; keep the adapter/forwarded/path gates. |

## 2. Interfaces and flow

```csharp
// WfpUdpContext
bool TryParseControl(ReadOnlySpan<byte> control, out ReadOnlySpan<byte> contextPayload, out int level);
// The service hands contextPayload to RedirectDestination.TryParse; the cmsg level is diagnostic only.

// WfpUdpReceiver
Task RunAsync(Func<WfpUdpDatagram, ValueTask> onDatagram, CancellationToken cancellationToken);
readonly record struct WfpUdpDatagram(IPEndPoint Source, ReadOnlyMemory<byte> Payload,
                                      RedirectDestination Destination);

// HostListenerSet
static HostListenerSet Bind(); // v4 TCP + v6 TCP V6ONLY + dual-mode v6 UDP on one port
int Port; IReadOnlyList<TcpListener> TcpListeners; Socket UdpSocket;

// HostUdpResponseSink : IUdpResponseSink
InjectAsync(flow, remoteSource, payload, clientMac, token) =>
    _udpSocket.SendTo(payload.Span, ToListenerEndpoint(flow.Local));
// ToListenerEndpoint maps an IPv4 Flow.Local to ::ffff:a.b.c.d for the AF_INET6 listener.
```

Per datagram:

1. Reject missing/invalid context, protocol != 17, family not 4/6, rule index out of range, target
   null/non-proxy/unknown, and family mismatch. Count `udpFailClosedDrop` and
   `kernel.udp.dropped{reason}`; no session.
2. If context family is 4 and the client source is IPv4-mapped, `MapToIPv4()` the source. Build the
   remote endpoint from the context (family 4 → four bytes; family 6 → sixteen).
3. If `payload.Length > 4096`, drop/count **before admission**. This is mandatory: the transport's
   `Oversized` skip is receive-side only; its send path throws on an over-cap payload and the
   coordinator may remove the live session and rethrow into the receive loop.
4. `FlowKey.Create(client, remote, TransportProtocol.Udp, FlowOriginKind.Host)` (adapterless).
5. `coordinator.TrySendSpanAsync(flow, target, payload.Span, MacAddress.Invalid, token)` with the
   default packet sequence/generation. Wrap the call per datagram: a false admission or exception
   is counted/logged and the loop continues. The coordinator owns setup/capacity/idle and its own
   counters; the service counts only the kernel-level datagram/drop counters, avoiding a double
   count of capacity/setup failures.
6. The transport replies through `HostUdpResponseSink`; the sink maps an IPv4 `flow.Local` to
   `::ffff:` and sends from the receiving socket. This matches the proven reply shape (the v6
   endpoint returned by `WSARecvMsg`); a direct send to a pure v4 endpoint from the AF_INET6 socket
   is unverified.

The host cap is 4096 for the datagram payload: `UdpProxyOptions.MaximumFrameSize`, the two pools,
the transport factories and the admission check. The setup queue pool is `NativeBufferPool(4096)`;
the receive-window pool is `NativeBufferPool(UdpProxyCoordinator.ReceiveWindowSize(4096),
UdpProxyCoordinator.ReceiveWindowPoolCapacity(capacity))`. Confirm each transport's cap semantics
while implementing; align the admission check to its actual payload limit if it treats the value
as a frame cap, and document it.

## 3. Composition and lifecycle

`KernelBackend.RunComposedAsync`:

- create `SelfTrafficRegistry`, `Socks5AddressCache`, `SetupExecutor` (from `SetupWorkerCount`),
  `ActivityBucketClock`, the UDP pools and the TCP relay pool;
- create the `KernelHostService` with the device, configuration, relay factory, shared collaborators,
  target table/provider and logger;
- `TryStart`; on success, wait for the CLI cancellation, then dispose the host service (disable,
  cancel scope, UDP coordinator, TCP drain, listener/block sink, clear forwarded);
- dispose the UDP pools/executor/relay pool in reverse order.

`KernelHostService.TryStart`:

1. `VerifyControlAbi`; `DisableStaleState` (host disabled, forwarded cleared).
2. `KernelPolicyCompiler.TryCompile`; on failure return the diagnostics.
3. `HostListenerSet.Bind()`; `BlockSink.Bind(port)` if `ContainsBlock`.
4. Create and start `KernelHostUdpService` (receiver loop runs while the driver is disabled).
5. `SetRuleBlob` + readback; `SetConfig(EnabledFor(...))` + readback.
6. Start TCP accept loops; log ready with TCP listeners, UDP socket/port and UDP capacity.
7. On failure: stop UDP, dispose listeners/block sink; leave the driver disabled.

`KernelHostService.DisposeAsync` (the listener set is the single socket owner):

1. `SetConfig(disabled)` (first, so no new redirects).
2. Cancel the scope (TCP accept + UDP receive/sweep).
3. Dispose `HostListenerSet`: stop the TCP listeners and **close the UDP socket**; the close
   unblocks the blocking `WSARecvMsg`.
4. Await `KernelHostUdpService.DisposeAsync` (receiver joins, coordinator disposes sessions).
5. Await `KernelHostTcpService.DisposeAsync` (accept loops stop, active relays drain).
6. Dispose the block sink; clear forwarded; set `ShutdownFaulted` if step 1 failed.

The UDP service never owns or closes the listener socket. The existing TCP start order is the
rollback signal; the TCP E2E must pass again.

## 4. Configuration gate

> Superseded by the deferred status above: the UDP-proxy rejection and the inert UDP-budget warnings
> stay until the reply source is preserved.

`ConfigurationKernelCapabilities`:

- remove the “proxy rule can match UDP” rejection (the Kernel host UDP path exists now) and update
  the named cases in `BackendDriverConfigurationTests.cs` that currently expect it;
- remove the “UDP settings are inert” warnings for `UdpSessionCapacity`,
  `UdpRelayReceiveBufferBytes`, `UdpSessionIdleTimeout` and their test cases;
- keep the adapter/forwarded/`KernelPassCache`/process-path gates;
- keep the local-target rejection in this batch: the compiler and the TCP resolver still refuse
  local targets; the UDP resolver also refuses any target whose `Socks5` is null (batch 2 lifts only
  the compiler/UDP side). Rewrite the stale “host UDP relay is not served” wording in the compiler
  and TCP resolver, and update the pinned test in `KernelPolicyCompilerTests.cs`.

`KernelPolicyCompiler` target table is unchanged: one config rule → one blob rule, `rule_index` is
the array index. A proxy rule with absent `Protocol` now compiles for both TCP and UDP; the TCP and
UDP services each resolve the same target.

## 5. Tests

Linux/root (add a direct `WinForward.Kernel.Runtime` project reference to
`WinForward.Integration.Tests`; there is no existing Linux test project that references it):

- `WfpUdpContext.TryParseControl`: level 0 and 41, type 70, cmsg header alignment (`IntPtr.Size`),
  multiple cmsgs, malformed lengths, missing context.
- `RedirectDestination.TryParse` on the extracted 36-byte payload: family 4/6, UDP, magic/version,
  short/long payloads; the service then rejects a non-UDP protocol.
- Level/family independence: a level-41 cmsg carrying a family-4 v4-mapped-normalized context is
  accepted.
- Client endpoint normalization: mapped source + family 4 → IPv4; native v4/v6 unchanged; family
  mismatch rejected.
- Flow/target resolution: valid proxy rule; out-of-range; null/non-proxy; local/unknown rejected.
- `HostUdpResponseSink`: bind a loopback UDP socket, inject, assert the destination/payload.
- Config matrix: UDP proxy accepted; TCP-only unchanged; local target still rejected; UDP setting
  warnings gone.
- `UdpProxyComposer` packet path still compiles/tests; host core construction is smoke-tested.

Gates: root `dotnet format`, build, test; `kernel/tools/check.sh --full`; Windows `gate.ps1` with the
`udp-rules` U1–U13/U10 suite. The Windows driver/C++ is unchanged, so the 5f driver hash is reused.

Windows E2E A1–A5, A8, A10: see the parent design §9 and the batch implement.md. The client must
assert `ReceiveFrom.RemoteEndPoint` equals the original destination (A4). A8 uses a native IPv4
client and a v4-mapped connected client against the proxy; a native IPv6 flow uses a local `::1`
echo if the Linux target has no IPv6 route. The no-context A10 sends a local datagram to the
listener and verifies a drop/counter with the service still alive.

## 6. Risks / rollback

- **A4 failed (blocking):** the listener `sendto` reply source is the listener, not the original
  destination. Do not commit/accept this batch. The entry/coordinator code is preserved; the
  response leg must be driver-injected/source-rewritten by `10-26-host-udp-reply-source`, and A4
  must pass before the combined work is accepted. A8/dual-mode still needs the same rerun.
- A4/A8 are blocking; if either fails, stop and revise the listener/reply design.
- The host-service refactor is the highest regression risk; the 5f TCP E2E and driver gate are the
  rollback signal. If the TCP path regresses, revert the refactor before pursuing UDP.
- The receive loop must not allocate per datagram; use pooled payload/control buffers and let the
  coordinator own its copies.
- The no-context path is reachable by any local process; it must never create a session or crash.
- A failed start must leave the driver disabled; the shutdown order disables first and closes the
  UDP socket before disposing the listener.
- If the batch cannot meet its acceptance, keep the UDP capability gate and revert the new files;
  the TCP Kernel and WinpkFilter paths remain the rollback point.
