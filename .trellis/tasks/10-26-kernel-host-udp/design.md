# Design: Kernel provider host UDP

Parent PRD: `./prd.md`. Research: `./research/host-udp-driver.md`,
`./research/host-udp-userland.md`, `./research/host-udp-seams.md`. This document is the
cross-batch architecture; the child tasks own batch execution.

## 1. Shape

```text
WFP ALE_CONNECT_REDIRECT (one classify per UDP association)
        │ association redirected to (client local address, listener_port)
        ▼
HostListenerSet (one dual-mode v6 UDP socket + v4/v6 TCP listeners)
        │ WSARecvMsg: payload + client source + WFP redirect-context cmsg
        ▼
WfpUdpReceiver → WfpUdpContext parser
        │ validate UDP/family/rule index/target; normalize a v4-mapped client source to IPv4
        ▼
KernelHostUdpService
        ├─ FlowKey.Create(client, originalDestination, Udp, Host)
        ├─ rule_index → ProxyTarget (KernelPolicyCompiler table)
        └─ UdpProxyCoordinator.TrySendSpanAsync(flow, target, payload, MacAddress.Invalid)
                 ▼
        UdpProxySession / UdpSessionSetup (existing)
                 ▼
        Socks5UdpTransport | Socks5UotTransport | LocalUdpTransport (existing)
                 ▼
        HostUdpResponseSink : IUdpResponseSink
                 └─ (A4 failed: listener sendto source != original destination)
                 └─ driver-injected/source-rewritten reply (host-udp-reply-source)
```

The driver is unchanged only through the baseline; A4 proved the userland listener `sendto` does
not preserve the reply source. The accepted response leg must be a driver injection or a controlled
source rewrite (new control IOCTL/ABI in the reply-source batch), not the listener `sendto`. The
baseline code is not accepted until that batch passes A4/A8.

## 2. Components and files

| File | Responsibility |
|---|---|
| `src/WinForward.Kernel.Runtime/Relay/HostListenerSet.cs` (new) | Bind v4 TCP + v6 TCP (V6ONLY) + one dual-mode v6 wildcard UDP on the same port; set `IP_WFP_REDIRECT_CONTEXT` (levels 0/41) and disable `SIO_UDP_CONNRESET` before bind; expose `Port`, `TcpListeners`, `UdpSocket`; retry the whole port on failure. |
| `src/WinForward.Kernel.Runtime/Relay/WfpUdpContext.cs` (new) | Pure `WSACMSGHDR` chain parser that extracts the 36-byte cmsg payload; decoding/validation reuses `RedirectDestination.TryParse` (magic/version/family/protocol/port/rule index/process id). No sockets, no Windows calls; unit-testable on Linux. |
| `src/WinForward.Kernel.Runtime/Relay/WfpUdpReceiver.cs` (new) | Windows `WSARecvMsg` receive loop over the dual-mode socket; returns payload, client `IPEndPoint` and parsed context; skips `WSAECONNRESET`; cancellation by socket close/scope. |
| `src/WinForward.Cli/Backends/Kernel/HostUdpResponseSink.cs` (new) | `IUdpResponseSink` implementation; sends via the receiving socket to `flow.Local`; ignores `remoteSource`/MAC; catches send failures and counts/logs. |
| `src/WinForward.Cli/Backends/Kernel/KernelHostUdpService.cs` (new) | UDP receive loop orchestration, context validation, client-endpoint normalization, target resolution, coordinator admission, activity-clock tick and idle sweep, counters, dispose. |
| `src/WinForward.Cli/Backends/Kernel/KernelHostService.cs` (new) | Host lifecycle orchestrator extracted from `KernelHostTcpService`: ABI check, stale-state disable, compile, bind `HostListenerSet`/`BlockSink`, start UDP loop, publish blob/config, start TCP accept loops, shutdown order. |
| `src/WinForward.Cli/Backends/Kernel/KernelHostTcpService.cs` (refactor) | TCP accept/relay/session/capacity only; receives the bound listeners, target table and scope from the orchestrator; removes device publication and listener ownership. |
| `src/WinForward.Cli/UdpProxyComposer.cs` (extend) | Add a host sink/composition overload; the packet `Create(IPacketReinjector, ...)` delegates to the same core. |
| `src/WinForward.Cli/Backends/Kernel/KernelBackend.cs` (extend) | Create/own `SelfTrafficRegistry`, `Socks5AddressCache`, `SetupExecutor`, UDP pools/clock and the host service; dispose in reverse order. |
| `src/WinForward.Configuration/ConfigurationKernelCapabilities.cs` (batch 1) | Remove the UDP-proxy rejection and the inert UDP-setting warnings; keep adapter/forwarded/path gates. |
| `src/WinForward.Cli/Backends/Kernel/KernelPolicyCompiler.cs` (batch 2) | Allow local targets into the target table for UDP; the TCP resolver keeps rejecting them. |

`WinForward.Kernel.Runtime` must not reference `WinForward.Runtime`; the CLI owns the service/sink.
`WinForward.Kernel.Runtime` may host the Windows socket receiver because it already hosts
`SocketIoctl`/`ListenerSet`.

## 3. Host listener details

- Bind order: v4 TCP on port 0 → v6 TCP with `DualMode=false` on the same port → dual-mode v6 UDP
  (`DualMode=true`) on the same port. If any bind fails, dispose all and retry a new port (bounded
  attempts).
- Before the UDP bind: `SetSocketOption(IP, (SocketOptionName)70, true)` and the IPv6 level 41
  equivalent (try both; the WFP suite does the same); disable `SIO_UDP_CONNRESET`
  (`IOControl(0x9800000C, [0,0,0,0], null)`). A dual-mode v6 socket is needed because the suite
  receives v4-mapped connected flows on one such socket; V6ONLY listeners are not assumed to receive
  them (A8 decides).
- The TCP listener count stays 2; the UDP socket is a single wildcard. The block sink is unchanged
  and binds its own TCP+UDP on a different port.
- `listener_port` remains shared by TCP and UDP; no config/ABI change.

## 4. Datagram receive and validation

`WfpUdpReceiver` uses `WSARecvMsg` because the WFP context is a control message not exposed by
`ReceiveMessageFromAsync`. Payload buffer 65536, control buffer 2048 (matching the WFP suite).
A long-running task per socket (blocking receive); cancellation closes the socket. `WSAECONNRESET`
is a skip (client port gone), not a loop failure.

`WfpUdpContext` extracts the cmsg payload (type 70, level 0 or 41, at least 36 bytes) and hands it
to `RedirectDestination.TryParse`, which validates magic/version/family/protocol/port and exposes
the rule index, process id and original endpoint. The service additionally requires protocol UDP
and family 4/6. **The cmsg level and the context family are independent:** level 41 can carry a
family-4 context for a v4-mapped connected flow after the 5f normalization. Do not infer family
from the level. Multiple context cmsgs are rejected fail-closed rather than guessed.

`KernelHostUdpService` then:

1. Rejects a missing/invalid context, a non-UDP protocol, an out-of-range rule index, a non-proxy
   target, or an unknown target. Each increments `udpFailClosedDrop` (and a `kernel.udp.dropped`
   counter with a reason) and creates no session. The listener port is locally reachable, so this is
   the negative-path guard (E2E A10).
2. Normalizes the client source endpoint: if the context family is 4 and the source is
   `::ffff:a.b.c.d`, map it to IPv4. This keeps `FlowKey.Create` family-consistent and makes the
   reply target the pure IPv4 client. A remaining family mismatch drops the datagram.
3. Length-checks the datagram against the 4096 cap **before** coordinator admission. An over-cap
   datagram is dropped/counted without calling the coordinator. The transport's `Oversized` path
   is receive-side only; the send path throws on an over-cap payload and the coordinator may tear
   down the session, so the admission check is mandatory.
4. Builds `FlowKey.Create(client, originalDestination, TransportProtocol.Udp, FlowOriginKind.Host)`
   (adapterless slot/generation 0) and calls
   `coordinator.TrySendSpanAsync(flow, target, payload, MacAddress.Invalid, token)` with the
   default packet sequence/generation. The call is contained per datagram: an exception or a
   `false` admission result is counted/logged and the receive loop continues; one datagram must not
   kill the loop. The coordinator owns session creation, setup queue, capacity, idle and teardown.

The 4096-byte cap is the host datagram payload cap: `UdpProxyOptions.MaximumFrameSize`, the two
pool sizes, the transport factory cap and the admission check all use the same constant. Confirm
the transport's cap semantics while implementing; if a transport treats the value as a frame cap
rather than a payload cap, align the admission check with its actual payload limit and document it.

## 5. Shared coordinator composition

`UdpProxyComposer` gains a core overload:

```csharp
Create(
    IUdpResponseSink sink,
    SelfTrafficRegistry selfTraffic,
    ILogger logger,
    int maximumFrameSize,
    NativeBufferPool setupQueuePool,
    NativeBufferPool receiveWindowPool,
    SetupExecutor setupExecutor,
    Socks5AddressCache addressCache,
    int sessionCapacity,
    int relayReceiveBufferBytes,
    ActivityBucketClock? activityClock)
```

The activity tick uses a named 500 ms interval (the packet path's activity bucket cadence); the
sweep uses `DeriveUdpSweepInterval`. Both are cancelled with the host scope. `ShutdownFaulted`
moves from `KernelHostTcpService` to `KernelHostService`; `KernelBackend` reads it from the
orchestrator.

It builds the same `UdpTransportFactory` (SOCKS5 + local) and `UdpProxyOptions` as the packet path.
The existing packet `Create` builds the `UdpResponseReinjector` and delegates to the core. The host
service builds `HostUdpResponseSink` and the two pools with the host cap (4096), and passes
`MacAddress.Invalid` through the coordinator. The packet path's `Socks5AddressCache` is primed at
host startup (`PrimeSocks5AddressCacheAsync`) so the first UDP association does not pay DNS.

The sink sends from the dual-mode AF_INET6 listener socket. Because the service normalizes the v4
client endpoint to IPv4 for the `FlowKey`, the sink maps `Flow.Local` back to `::ffff:a.b.c.d` when
it is IPv4 before `SendTo`; this matches the proven reply shape (the v6 endpoint returned by
`WSARecvMsg`). A native IPv6 `Flow.Local` is sent unchanged.

The host service runs its own timer:

- tick the shared `ActivityBucketClock` at its bucket duration so session stamps advance;
- call `coordinator.RemoveExpiredAsync(now, configuration.UdpSessionIdleTimeout,
  UdpProxyCoordinator.OneShotIdleTimeout)` at `IdleExpirySweeper.DeriveUdpSweepInterval(...)`;
- log/debug the expired count; cancel with the host scope.

No packet `FlowDispatcher` or `IdleExpirySweeper` is required.

## 6. Lifecycle

`KernelHostService.TryStart`:

1. Verify control ABI and disable stale host/forwarded state (moved from `KernelHostTcpService`).
2. Compile the policy and target table.
3. Bind `HostListenerSet` and, if needed, the `BlockSink`.
4. Create `KernelHostUdpService` with the UDP socket, target table, config, counters and shared
   collaborators; start its receive/sweep loops while the driver is still disabled.
5. Publish and verify the host rule blob.
6. Publish and verify `ProbeConfig` with the shared listener port/PID/self-PID/block sink.
7. Start the TCP accept loops through `KernelHostTcpService`; report ready with UDP enabled/capacity.
8. On any failure before ready, stop the UDP service/listeners and leave the driver disabled.

`DisposeAsync` (single socket ownership in `HostListenerSet`):

1. Disable the host config first.
2. Cancel the scope (TCP accept and UDP receive/sweep).
3. Dispose `HostListenerSet`: stop the TCP listeners and **close the UDP socket**; the close is what
   unblocks the blocking `WSARecvMsg` loop.
4. Await `KernelHostUdpService.DisposeAsync` (the receiver joins, the coordinator disposes its
   sessions/transports).
5. Await `KernelHostTcpService.DisposeAsync` (accept loops stop, active relays drain; the accepted
   sockets are independent of the listener).
6. Dispose the block sink; clear forwarded state; set `ShutdownFaulted` if step 1 failed.

The `SetupExecutor`, pools, address cache and self-traffic registry are owned by `KernelBackend`
and disposed after the host service (the UDP service borrows them, like the packet path). The UDP
service never owns/closes the listener socket; only `HostListenerSet` does.

## 7. Configuration and target resolution

- `ConfigurationKernelCapabilities`: remove the “proxy rule may match UDP” rejection and the
  “UDP settings are inert” warnings. The host adapter, forwarded, `KernelPassCache` and path gates
  remain unchanged.
- Batch 1 keeps the compiler's local-target rejection and the TCP resolver's local rejection.
  Batch 2 removes the compiler rejection for local targets; the UDP resolver accepts
  `ProxyTarget.Local`, the TCP resolver still fails closed.
- `rule_index` remains the only target bridge; one config rule is one blob rule. The fallback
  sentinel `0xFFFFFFFF` and a non-proxy rule are fail-closed drops. A local target rule is already
  constrained to `Protocol: ["udp"]` by the config validator.
- `UdpSessionCapacity`, `UdpRelayReceiveBufferBytes` and `UdpSessionIdleTimeout` are read from the
  validated configuration and applied to the host coordinator.

## 8. Observability

- `KernelBackendLog`: listener-bound and ready lines include the UDP socket/port and session
  capacity; a UDP stop line; a rate-limited drop line with reason. Keep the existing TCP lines.
- `RuntimeCounters`: the coordinator already increments `udpCapacityRejections`,
  `udpSetupRejections`, `udpSetupFailures`, `udpAssociationLost`, `udpResponseSourceMismatch`,
  `udpFailClosedDrop` and local-target counters. The host service increments
  `kernel.udp.accepted`, `kernel.udp.dropped`, `kernel.udp.active`, `kernel.udp.capacityBlocked`
  when the TCP service has a `kernel.tcp.*` analog.
- The connected-UDP leak is documented in `wfp-connected-udp-record-refcount-leak.md` and the
  operations README; the E2E samples `wfprobe pooltag` and does not assert release. No new product
  counter is required for the leak in this milestone.

## 9. Tests

Unit (Linux, no driver):

- `WfpUdpContext`: levels 0/41, 36-byte payload, magic/version, family 4/6, protocol 17, short/
  long payloads, multiple cmsgs, malformed offsets.
- Client endpoint normalization: mapped source + family 4 → IPv4; native v4/v6 unchanged; family
  mismatch rejected.
- `FlowKey` construction and target resolution: valid proxy rule; out-of-range; null/non-proxy;
  local target accepted only by the UDP resolver.
- Config/compiler: UDP-capable proxy rules accepted; TCP-only behavior unchanged; local target
  rejection remains in batch 1 and is lifted in batch 2; UDP setting warnings removed.
- `UdpProxyComposer` core overload: packet and host sinks produce the same coordinator options and
  transport factory; the packet path remains byte-for-byte behavior-identical.

Windows driver gate: the existing `gate.ps1` (`rules`, `udp-rules` U1–U13, U10, selftest,
userland-e2e) must stay green.

Windows host UDP E2E (VM, test-signed driver, sing-box, Linux target):

| Arm | Rule | Assertions |
|---|---|---|
| A1 proxy | `Protocol:["udp"], RemotePort:["40010"] → proxy main` | send 3 datagrams; all receive; `ReceiveFrom` source equals the original destination; sing-box relays to `192.168.100.4:40010`; driver `udp_redirected +1`, `udp_classify_calls +1` (association count, not datagram count) |
| A2 pass | 40011 → pass | direct target receives; no sing-box flow; `udp_permitted` grows |
| A3 block | 40013 → block | send succeeds, receive times out, target receives none; `udp_blocked/deferred_blocks +1`; the host stop line reports `BlockSink.Received == 0` |
| A4 source | A1’s unconnected flow | **FAILED in the baseline**: the client source is the listener `192.168.100.2:<port>`, not `192.168.100.4:40010`; raw spoofing is not viable. This is the blocking gate for the reply-source batch. The connected arm cannot prove the source (its peer is fixed by definition). |
| A5 capacity/idle | `UdpSessionCapacity=1`, idle 5s | second flow (different source port) fails closed, first target only; after expiry a **new source-port** client rebuilds (`udp_classify_calls +1`); capacity rejection counter grows |
| A6 local DNS (batch 2) | `LocalTargets: dns=192.168.100.4:53`; UDP 53 → proxy dns | DNS answer, EDNS0 >512 bytes, source equals 192.168.100.4:53, one-shot expiry |
| A7 UoT (batch 2) | SOCKS5 target with UoT v2 | reply + sing-box UoT evidence |
| A8 dual-mode | native v4, v4-mapped connected, native v6 | all receive; no V6ONLY loss; listener is the single dual-mode socket; the connected arm checks payload only |
| A9 lifecycle | one connected, one unconnected | unconnected `WfPr` returns to baseline; connected is sampled only |
| A10 negative | a local `sendto` to the listener without a redirect context | dropped, no session, no crash, counted |

## 10. Risks and failure handling

- **R1 dual-mode/V6ONLY (blocking):** A8 must pass with the single dual-mode socket; if it does not,
  stop and revisit the listener shape before proceeding.
- **R3 reply source (blocking, confirmed):** A4 failed in the baseline: the client sees the listener
  address instead of the original destination. Raw sockets cannot spoof the source on Windows. The
  response leg must be a driver injection/source rewrite with a new control IOCTL/ABI; do not accept
  the baseline until the reply-source batch passes A4/A8.
- **R2 large datagrams:** the 4096 cap protects memory; the service length-checks before admission
  and contains the transport send failure per datagram (the transport's `Oversized` is receive-side
  only). The DNS EDNS0 arm (A6) proves the common case. A future config key can raise it.
- **R4 ICMP resets:** `SIO_UDP_CONNRESET` is disabled; `WSAECONNRESET` is a skip, not a loop exit.
- **R5 blocking receive:** one long-running task per socket; cancellation closes the socket; the
  loop is drained before the listener is disposed.
- **R6 public listener:** no-context datagrams are dropped/counted and never create sessions.
- **R7 target/client family mismatch:** normalize the mapped client endpoint against the context
  family; drop if still mismatched.
- **R8 two engines:** only the shared coordinator is used; the old `ForwardedUdpRelay` path is not
  extended or used for host UDP.
- **R9 startup order:** UDP receive starts while the driver is disabled; shutdown disables the driver
  first and stops loops before disposing the listener.
- **R10 connected-UDP leak:** accepted/documented/sampled; never free the driver record.

## 11. Compatibility and rollback

- No ABI/context change: control ABI 8, context v1/36 bytes, shared listener/block sink ports.
- `Provider` remains opt-in; WinpkFilter is the default. The WinpkFilter UDP path is untouched.
- The TCP Kernel path is revalidated by its existing E2E after the host-service refactor.
- If host UDP cannot meet A4/A8, revert the host UDP service/composer/listener additions and keep
  the UDP capability gate; the TCP path and WinpkFilter behavior remain the rollback point. The
  baseline's listener/coordinator work is preserved in the working tree for the reply-source batch,
  but is not committed/accepted until A4 passes.
