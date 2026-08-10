# Research: audit-runtime-udp-socks

- **Query**: Perform a read-only exhaustive audit of `Socks5Client.cs`, `UdpAssociations.cs`, `UdpProxyCoordinator.cs`, `UdpResponseReinjector.cs`, callers, and tests. Include PRT-007 and distinguish confirmed defects from coverage gaps.
- **Scope**: mixed
- **Date**: 2026-08-10

## Findings

### Audit Boundary and Ownership Chain

The runtime chain is:

1. `NdisPacketActionExecutor.ProxyAsync` parses a proxy-selected UDP packet and delegates only its payload to `UdpProxyCoordinator.TrySendAsync`; the captured packet remains consumed on all send/setup exceptions (`src/WinForward.Runtime/NdisPacketActionExecutor.cs:52-82`).
2. `UdpProxyCoordinator` creates one task per exact `FlowKey`, creates a transport, claims a dynamic relay alias, and starts a receive loop (`src/WinForward.Runtime/UdpProxyCoordinator.cs:35-78`, `:105-123`). `FlowKey` includes address family, protocol, both endpoints, origin kind, origin adapter stable ID, and adapter generation (`src/WinForward.Core/Domain.cs:69-85`).
3. `Socks5UdpTransport.CreateAsync` allocates a UDP socket, opens/authenticates a SOCKS TCP control connection, performs `UDP ASSOCIATE`, registers the returned relay tuple as self traffic, and owns the socket, control connection, and registration token (`src/WinForward.Runtime/Socks5Client.cs:264-308`, `:328-333`).
4. `UdpProxySession` sends SOCKS-framed datagrams to the dynamic `RelayEndpoint`; its receive loop routes decoded response payloads to the response sink using the original flow (`src/WinForward.Runtime/Socks5Client.cs:310-325`; `src/WinForward.Runtime/UdpProxyCoordinator.cs:180-255`).
5. `UdpResponseReinjector` rebuilds a UDP Ethernet/IP frame. It uses the host target toward MSTCP, or the original forwarded adapter target toward the adapter (`src/WinForward.Runtime/UdpResponseReinjector.cs:64-112`). A reinjected UDP reverse packet is passed by the dispatcher rather than sent to SOCKS again (`src/WinForward.Runtime/FlowDispatcher.cs:108-121`, `:181-185`).

The runtime starts `IdleExpirySweeper` with the dispatcher and both proxy coordinators, and disposes it before the coordinators in the capture-loop scope (`src/WinForward.Cli/Program.cs:175-230`). The sweeper calls UDP session and association expiry every minute by default, with a two-minute relay idle interval (`src/WinForward.Runtime/IdleExpirySweeper.cs:10-91`).

### Confirmed Defects

#### RUS-001 / PRT-007 - The SOCKS deadline ends after TCP connect; protocol states and reply-domain DNS can wait indefinitely

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/Socks5Client.cs:41-84`, `:94-143`, `:147-159`, `:173-234`; caller `src/WinForward.Runtime/UdpProxyCoordinator.cs:35-60`.
- **Evidence**: `ConnectOnceAsync` creates `attemptCts` and applies `CancelAfter(timeout)` only to `socket.ConnectAsync` (`Socks5Client.cs:122-124`). After TCP connect, it calls `AuthenticateAsync` with the unbounded caller token (`:126-128`). Greeting write/method read, RFC 1929 write/read, command writes, endpoint-prefix/body reads, and domain resolution all use that caller token (`:149-158`, `:173-234`). `Socket.ReceiveTimeout` and `SendTimeout` set at `:116-117` do not bound the asynchronous `NetworkStream.ReadExactlyAsync` operations.
- **Deterministic reproduction**:
  1. Run a loopback TCP listener that accepts the connection and reads the SOCKS greeting but never writes a method-selection response.
  2. Call `Socks5ControlConnection.ConnectAsync` with that address and a short `perAttemptTimeout`.
  3. TCP connect completes before the timeout; `AuthenticateAsync` remains in `ReadExactlyAsync` until its external cancellation token is cancelled.
  4. The same behavior applies after a selected RFC 1929 method when the auth response is withheld, and after an accepted greeting when the command reply is withheld.
- **Reachability**: UDP session creation awaits this task under the coordinator's shared shutdown token. A stalled flow retains its session-dictionary slot while callers wait (`UdpProxyCoordinator.cs:39-60`). This is the same PRT-007 finding recorded in `.trellis/tasks/08-10-audit-protocols/research/audit-findings-audit-protocols.md:150-165`.
- **Test status**: `FlowAndConfigurationTests.Socks5ConnectAsyncHonorsAttemptCapOnSocketCreationFailure` and `Socks5ConnectAsyncDisposesEveryCreatedSocketOnFailure` cover pre-protocol connection failures only (`tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:358-420`). No live peer drives any SOCKS state beyond a completed TCP connect.

#### RUS-002 - A failed control connection leaves the already allocated UDP socket undisposed

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/Socks5Client.cs:281-308`.
- **Evidence**: `Socks5UdpTransport.CreateAsync` allocates `socket` at line 283. It awaits `Socks5ControlConnection.ConnectAsync` at lines 286-290 before entering the `try` block at line 291. The only socket disposal is in the catch associated with that later block (`:302-306`). Therefore DNS, TCP-connect, authentication, or control-registration failure at line 286 bypasses the socket disposal path.
- **Deterministic reproduction**:
  1. Invoke `Socks5UdpTransport.CreateAsync` with a server address/port for which `ConnectAsync` fails after the UDP socket is constructed.
  2. The call throws from line 286 before execution reaches the `try` block.
  3. The local UDP socket has no deterministic disposal path; its eventual finalization is outside the method's ownership chain.
- **Boundary**: A failure after the TCP control connection has been returned, including `UDP ASSOCIATE` failure, does enter lines 291-307 and disposes both the UDP socket and control connection.
- **Test status**: No test instantiates `Socks5UdpTransport` with a real or instrumented UDP-socket ownership seam. The coordinator tests use fake transports (`tests/WinForward.Core.Tests/UdpProxyCoordinatorTests.cs:154-204`; `tests/WinForward.Core.Tests/UdpRelayTests.cs:357-412`).

#### RUS-003 - Cancellation after socket creation is translated into `IOException`, so coordinator shutdown can fault

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/Socks5Client.cs:122-142`, `:72-83`; `src/WinForward.Runtime/UdpProxyCoordinator.cs:81-103`.
- **Evidence**: The only `OperationCanceledException` catch in `ConnectOnceAsync` accepts cancellation when the caller token was *not* cancelled (`Socks5Client.cs:130-134`). If the caller token is cancelled during connect or any subsequent authentication read, the generic catch returns a fatal `ConnectAttempt` (`:139-142`). `ConnectAsync` then disposes the returned socket/registration and throws a new `IOException` (`:72-83`) instead of retaining cancellation. `UdpProxyCoordinator.DisposeAsync` catches only `OperationCanceledException` from each setup task (`UdpProxyCoordinator.cs:90-101`), so this `IOException` leaves disposal.
- **Deterministic reproduction**:
  1. Have a loopback SOCKS peer accept the TCP connection and withhold its greeting response.
  2. Begin `UdpProxyCoordinator.TrySendAsync` using the concrete transport factory and wait until the peer accepts.
  3. Call `UdpProxyCoordinator.DisposeAsync`.
  4. Its `_shutdown` token cancels the pending `ReadExactlyAsync`; the control setup task becomes faulted with `IOException`, and the dispose loop does not match its cancellation catch filter.
- **Test status**: There is no coordinator test that cancels a real or controlled in-progress setup and then awaits coordinator disposal. Existing cancellation comments at `UdpProxyCoordinator.cs:54-60` are not exercised by a cancellation test.

#### RUS-004 - An abandoned shared setup that later faults retains a capacity slot

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/UdpProxyCoordinator.cs:35-60`, `:130-159`, `:161-177`.
- **Evidence**: A new flow task is inserted into `_sessions` before setup completes (`:39-46`). If a caller cancels its `WaitAsync` while that task is still pending, the catch intentionally leaves the shared task in place because it is not yet faulted/cancelled (`:49-60`). If the setup task faults later and no later caller reaches the same flow, no continuation removes it. `RemoveExpiredAsync` selects only `IsCompletedSuccessfully` tasks (`:132-136`), so it also retains the late-faulted task. At capacity, a different flow is rejected at `:41-44`.
- **Deterministic reproduction**:
  1. Construct a coordinator with capacity 1 and a transport factory whose `CreateAsync` is gated by a `TaskCompletionSource`.
  2. Call `TrySendAsync` with a caller token and cancel that token while `CreateAsync` is blocked.
  3. Fault the factory task after the caller has left; do not send another packet for the first flow.
  4. `RemoveExpiredAsync` does not remove the faulted task; a new flow returns `false` because `_sessions.Count` remains 1.
- **Boundary**: A subsequent call for the same flow observes the faulted task and invokes `RemoveFailedSessionAsync` (`:54-60`, `:161-177`). This does not release the slot when the abandoned flow is never retried.
- **Test status**: `ConcurrentSameFlowBurstUsesOneTransportWithoutResponseCrossWiring` has a synchronous factory and does not cause setup waiters to cancel (`tests/WinForward.Core.Tests/UdpProxyCoordinatorTests.cs:33-61`).

#### RUS-005 - Association activity stops at creation while the live session remains active

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/UdpAssociations.cs:9-23`, `:45-83`, `:85-129`; `src/WinForward.Runtime/UdpProxyCoordinator.cs:109-121`, `:157`; `:200-210`, `:229-240`.
- **Evidence**: `UdpAssociation.LastActivityUtc` is initialized on construction and changes only through table lookup/claim (`UdpAssociations.cs:9-23`, `:49-62`, `:118-129`). The coordinator claims the association once at setup (`UdpProxyCoordinator.cs:114`) and never calls `TryFindOriginal` or `TryFindRelay` while the session sends or receives. In contrast, `UdpProxySession` refreshes a separate clock on every send and receive (`:204-210`, `:236-240`). `RemoveExpiredAsync` keeps or disposes sessions using the session clock, then independently removes associations using the untouched association clock (`:130-159`).
- **Deterministic reproduction**:
  1. Create a session at time T0.
  2. Before the relay idle interval ends, successfully send or receive at T1, where T1 is within the interval of the sweep but T0 is outside it.
  3. Sweep at T2 with `T2 - T0 >= idleTimeout` and `T2 - T1 < idleTimeout`.
  4. The session survives the selection at lines 133-136, but its association is removed by line 157.
- **Observed consequence**: The relay-alias collision index no longer represents every active session. No production response path currently calls `TryFindRelay`; receives are bound directly to their session (`UdpProxyCoordinator.cs:229-240`). The lost index is therefore confirmed state/alias-liveness behavior, not a demonstrated live socket collision on the normal one-socket-per-session transport.
- **Test status**: Direct-table expiry is covered (`tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:233-243`) and session expiry is covered when the session itself is idle (`tests/WinForward.Core.Tests/UdpProxyCoordinatorTests.cs:118-149`). Neither test creates activity between association creation and an expiry boundary.

#### RUS-006 - Expiry decides a session is idle before synchronizing with a concurrent send

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/UdpProxyCoordinator.cs:130-159`; `:204-227`.
- **Evidence**: `RemoveExpiredAsync` snapshots potentially idle session tasks under `_gate` (`:132-136`) but later removes/disposes each task without a second activity check (`:139-150`). A concurrent caller can obtain the same session task before removal and update its last-activity ticks in `UdpProxySession.SendAsync` (`:204-210`). The sweeper still removes and disposes that session based on the earlier snapshot.
- **Deterministic reproduction**:
  1. Use a fake transport whose `DisposeAsync` is gated and a session old enough to be selected by the expiry snapshot.
  2. Start `RemoveExpiredAsync` and pause after selection but before dictionary removal.
  3. Call `TrySendAsync` for the same flow; it reuses the session and refreshes activity.
  4. Release the sweep; it removes and disposes the just-used session.
- **Test status**: The two expiry tests in `UdpProxyCoordinatorTests.cs:118-149` are sequential. They do not interleave a send with the sweep.

#### RUS-007 - Receive-loop failure holds the transport and self-traffic token until a later send or idle sweep

- **Severity**: Medium
- **Anchors**: `src/WinForward.Runtime/UdpProxyCoordinator.cs:63-78`, `:180-255`, `:130-159`; `src/WinForward.Runtime/Socks5Client.cs:316-333`.
- **Evidence**: Any non-shutdown receive failure is stored in `_receiveFailure` and ends the loop (`UdpProxyCoordinator.cs:229-254`), but that path does not remove the session or call `UdpProxySession.DisposeAsync`. The next send notices `_receiveFailure` and enters `RemoveFailedSessionAsync` (`:63-78`, `:204-209`); otherwise disposal waits for `RemoveExpiredAsync`. The concrete transport retains its UDP socket, relay self-traffic token, and control connection until `DisposeAsync` (`Socks5Client.cs:328-333`). Expected-relay mismatch and malformed SOCKS UDP replies take this receive-failure path (`Socks5Client.cs:316-325`).
- **Deterministic reproduction**:
  1. Return a fake transport from a successful setup and let the first send complete.
  2. Complete its pending `ReceiveAsync` with an `IOException` after `TrySendAsync` returns.
  3. The session remains in `_sessions` and the fake transport remains undisposed until either a second send arrives or the idle sweep selects it.
- **Boundary**: A subsequent send removes/disposes the failed session. Shutdown cancellation is separately handled in the receive loop (`UdpProxyCoordinator.cs:243-249`).
- **Test status**: Fake receive paths in `UdpProxyCoordinatorTests.cs:190-203` and `UdpRelayTests.cs:393-405` model disposal but no test completes a live session's receive with a non-shutdown fault.

### Verified Behavior (No Defect Found)

| Area | Observed behavior and anchors | Existing evidence |
|---|---|---|
| SOCKS control loop prevention | The control socket binds before `onSocketReady` registers `(TCP, Any:ephemeral, proxy)` and before `ConnectAsync` sends SYN (`src/WinForward.Runtime/Socks5Client.cs:114-124`). A failed candidate returns its socket/registration for disposal (`:68-83`). | Registry matching handles exact/reverse tuples and wildcard local binds (`src/WinForward.Runtime/SelfTrafficRegistry.cs:12-80`); forward/reverse ownership is unit-tested in `FlowDispatcherTests.cs:83-108`. |
| Dynamic UDP relay | The UDP transport retains the server-supplied `UDP ASSOCIATE` endpoint and port, uses it for every send, requires every received datagram to come from it, and registers that exact relay tuple as owned (`src/WinForward.Runtime/Socks5Client.cs:293-325`). | Bound-port decoding and wildcard/mapped/scope normalization have pure tests (`FlowAndConfigurationTests.cs:275-344`). There is no live relay test; recorded below as a gap. |
| SOCKS auth/control malformed data | Greeting version/method and RFC 1929 reply are checked (`src/WinForward.Runtime/Socks5Client.cs:173-187`). Reply prefix, status, reply length, full reply, and unsupported ATYP each fail by exception (`:189-227`). Unexpected UDP sender or malformed UDP frame also fail by exception (`:316-325`). The packet executor consumes these proxy failures rather than passing the original UDP packet (`src/WinForward.Runtime/NdisPacketActionExecutor.cs:58-82`). | Pure parser tests cover a normal reply, truncated success/failure distinction, and selected prefix cases (`FlowAndConfigurationTests.cs:275-299`, `:435-451`). |
| Alias isolation at claim time | The association table has separate original-flow and relay-alias indexes under one lock. A repeated original flow gets the existing association; a distinct original flow with an already-owned alias is rejected (`src/WinForward.Runtime/UdpAssociations.cs:25-129`). The coordinator disposes a just-created transport when the claim rejects (`UdpProxyCoordinator.cs:109-118`). | Direct same/different alias tests are at `FlowAndConfigurationTests.cs:213-256`; same-flow and different-flow coordinator tests are at `UdpProxyCoordinatorTests.cs:16-74`. |
| Host and forwarded response direction | Host responses use `SendToMstcp` with `ON_RECEIVE`; forwarded responses resolve `OriginAdapterId`, use `SendToAdapter` with `ON_SEND`, and drop unresolved origins (`src/WinForward.Runtime/UdpResponseReinjector.cs:64-112`). Builder rejection also drops the response without reaching the receive loop (`:84-97`). | Host, forwarded, missing-origin, unbuildable-family, and frame-cap behavior are covered using a fake reinjector (`UdpRelayTests.cs:151-266`). |
| Response loop prevention after reinjection | Dispatcher resolves the stored proxy decision; an UDP reverse tuple passes rather than re-entering proxy execution (`src/WinForward.Runtime/FlowDispatcher.cs:108-121`, `:181-185`). Self-owned traffic also passes before flow lookup/policy (`:79-86`). | `FlowDispatcherTests.FirstPacketClaimsPolicyAndUdpResponseDirectionIsPassed` and `SelfTrafficIsPassedBeforeCatchAllPolicy` cover these branches (`FlowDispatcherTests.cs:12-31`, `:69-81`). |

### Coverage Gaps, Not Confirmed Defects

| ID | Area | What exists | What is not covered / classification boundary |
|---|---|---|---|
| GAP-RUS-001 | Live SOCKS protocol states | Failure/cap/disposal tests stop before SOCKS bytes (`tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:358-420`). Pure protocol tests cover only selected byte vectors (`:259-355`, `:435-451`). | No loopback/scripted server test covers greeting method selection, RFC 1929 success/failure, full IPv4/IPv6/domain command reply, malformed reply body, or PRT-007 timeout states. PRT-007 itself is confirmed by the production call sequence. |
| GAP-RUS-002 | DNS and cross-family control/relay path | UDP socket family is selected from the original flow before control-server DNS/relay reply (`src/WinForward.Runtime/UdpProxyCoordinator.cs:105-113`; `Socks5Client.cs:281-299`). Reply-domain BND.ADDR resolution uses an external caller token (`Socks5Client.cs:218-234`). | No test uses an IPv4 flow with an IPv6 SOCKS control/relay endpoint, or the inverse, including an unspecified BND.ADDR substituted with a differently-family control peer. No test stalls initial server-host DNS or reply-domain DNS. This report does not classify cross-family behavior as a defect because no supported relay-family contract was found. |
| GAP-RUS-003 | Dynamic relay and self-traffic integration | Code uses returned BND.ADDR/BND.PORT, exact received endpoint equality, and wildcard registration (`Socks5Client.cs:293-325`; `SelfTrafficRegistry.cs:22-48`). | No live server proves a relay port different from the SOCKS TCP port, multi-address control fallback, registration lifetime across association teardown, or rejection of a datagram from a different endpoint. |
| GAP-RUS-004 | Coordinator setup concurrency/cancellation | Same-flow bursts share one task with a synchronous fake factory (`UdpProxyCoordinatorTests.cs:33-61`). Individual caller cancellation is described in source (`UdpProxyCoordinator.cs:54-60`). | No barrier-gated asynchronous factory proves exactly-one setup under overlap, no canceled-waiter test, no setup-fault-after-waiter-cancellation test, and no coordinator disposal during real/pending control setup. RUS-003 and RUS-004 identify source-proven outcomes of two absent paths. |
| GAP-RUS-005 | Alias collision under coordinator and association liveness | Direct sequential table collisions are covered (`FlowAndConfigurationTests.cs:213-256`). Different coordinator flows use fake transports with intentionally unique local ports (`UdpProxyCoordinatorTests.cs:154-165`). | No coordinator test returns the same relay alias for two different flows, no concurrent claim test, and no test preserves a session activity timestamp while attempting association expiry. RUS-005 is source-proven from the separate clocks. |
| GAP-RUS-006 | Malformed response end-to-end handling | Codec rejection is tested for SOCKS UDP fragmentation only (`FlowAndConfigurationTests.cs:114-121`); transport throws on malformed/foreign relay datagrams (`Socks5Client.cs:316-325`). | No test drives malformed RSV/ATYP/truncation or unexpected relay sender through the concrete transport into a session. No test documents the current behavior that domain-ATYP relay responses are silently skipped when `DestinationAddress` is null (`UdpProxyCoordinator.cs:236-240`); reinjection requires an IP source, so this is not classified as a codec defect. |
| GAP-RUS-007 | Host/forwarded Windows integration | Reinjector logic is unit-driven through a fake `IPacketReinjector` (`UdpRelayTests.cs:172-266`). CLI creates a map of currently scoped adapters and uses NDISAPI's configured frame cap (`src/WinForward.Cli/Program.cs:256-300`). | No supported-Windows test runs an actual host flow, Hyper-V/forwarded flow, origin adapter handle/MAC map, native NDIS send, or recapture/pass sequence. The map is built from a startup snapshot; adapter-list re-resolution is explicitly deferred (`Program.cs:160-164`). |
| GAP-RUS-008 | Expiry/teardown resource behavior | One test verifies session disposal for a plainly idle session (`UdpProxyCoordinatorTests.cs:118-149`); table expiry verifies both indexes are removed (`FlowAndConfigurationTests.cs:233-243`). | No test covers RUS-006's concurrent activity/expiry ordering, RUS-007's receive-fault retention, concrete socket/token release on every setup failure, or disposal of a faulted setup task. |

### Test Map

| Test file | Covered runtime behavior | Audit boundary |
|---|---|---|
| `tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:101-132` | SOCKS UDP IP/domain encoding/decoding and FRAG rejection. | Pure codec only; no transport socket/session lifecycle. |
| `tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:213-256` | Table original/relay lookup, expiry, and sequential collision rejection. | No coordinator ownership, real relay, or concurrency interleaving. |
| `tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:259-355` | SOCKS greeting/request/reply bytes, wildcard/mapped IPv4, IPv6 scope behavior. | No live stream state machine or timeout. |
| `tests/WinForward.Core.Tests/FlowAndConfigurationTests.cs:358-451` | Attempt cap, failed-connect socket disposal, reply-prefix parser. | Does not reach successful TCP + SOCKS read/write. |
| `tests/WinForward.Core.Tests/FlowDispatcherTests.cs:12-108` | Reverse UDP pass and self-traffic precedence. | Does not construct the SOCKS transport. |
| `tests/WinForward.Core.Tests/UdpProxyCoordinatorTests.cs:16-149` | Same-flow reuse, fake transport separation, response identity, simple session expiry. | Factory completes synchronously; no cancellation/failure races. |
| `tests/WinForward.Core.Tests/UdpRelayTests.cs:151-308` | Response frame cap, host/forwarded target choice, unresolved origin drop, executor routing. | Uses fake reinjector/fake transport; no NDISAPI or live SOCKS server. |

### Platform Boundary

- The state-table, flow-dispatch, codec, and fake-reinjector tests are hardware-independent and can run on Linux. Some reinjector test methods carry `SupportedOSPlatform("windows")`, but their assertions use `FakeReinjector`, not a loaded NDISAPI driver (`tests/WinForward.Core.Tests/UdpRelayTests.cs:151-266`).
- `Socks5ControlConnection` and `Socks5UdpTransport` are socket code and are not Windows-only, but this repository has no loopback live-SOCKS test for them.
- `UdpResponseReinjector` is Windows-marked (`src/WinForward.Runtime/UdpResponseReinjector.cs:25-26`), and the production path depends on NDISAPI adapter enumeration handles/MACs assembled in `Program.CreateUdpCoordinator` (`src/WinForward.Cli/Program.cs:256-300`). Host delivery, forwarded/Hyper-V delivery, dynamic relay traversal, and NDISAPI send outcomes remain supported-Windows hardware gates.

### External References

- [RFC 1928, SOCKS Protocol Version 5](https://www.rfc-editor.org/rfc/rfc1928.html) - Defines the SOCKS request/reply and UDP-associate relay model relevant to the dynamic BND endpoint and control state transitions.
- [Socket.ReceiveTimeout Property](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.receivetimeout?view=net-10.0) - States that `ReceiveTimeout` applies only to synchronous `Receive` calls.
- [NetworkStream.ReadTimeout Property](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.networkstream.readtimeout?view=net-10.0) - States that the timeout does not affect asynchronous `ReadAsync`, which is the basis for PRT-007's `ReadExactlyAsync` state stall.

### Related Specs

- `.trellis/tasks/08-10-audit-runtime-udp-socks/prd.md` - Requires the four-file audit, origin/alias/cancellation/expiry review, and Linux/Windows boundary.
- `.trellis/tasks/08-10-audit-runtime-udp-socks/design.md` - Defines the flow -> association -> control -> relay -> reinjection -> expiry ownership chain.
- `.trellis/tasks/08-10-audit-protocols/research/audit-findings-audit-protocols.md:150-165` - Original source-cited PRT-007 finding.
- `.trellis/spec/backend/error-handling.md:21-31` - Records fail-closed setup and shared-cancellation/alias contracts.
- `.trellis/spec/backend/quality-guidelines.md:25-42` - Records UDP association identity, failure-release, concurrency, and expiry expectations.
- `.trellis/spec/backend/windows-ndisapi.md:134-160` - Records the UDP relay/reinjection and Windows hardware boundary.

## Caveats / Not Found

- No source test or task artifact provides a live SOCKS5 server harness, a controlled DNS resolver timeout harness, or a concrete `Socks5UdpTransport` socket-disposal test.
- No production code calls `UdpAssociationTable.TryFindRelay`; response ownership currently comes from the dedicated `UdpProxySession`, so RUS-005 is reported as association-index liveness rather than a demonstrated normal-socket cross-wire.
- No Windows hardware results were executed by this read-only audit. Existing history records earlier hardware observations, but those are not treated as new verification here.

## Implementation Results (2026-08-10)

All confirmed findings RUS-001 through RUS-007 are fixed within the task-owned runtime surface. The
proxy-selected failure behavior remains fail-closed: failed control/session setup is removed and
disposed rather than passed or left reusable.

| Finding | Fix | Regression coverage |
|---|---|---|
| RUS-001 | The attempt deadline now covers initial server resolution, TCP connect, SOCKS greeting/authentication, command replies, and reply-domain resolution. Resolver timeout does not depend on a provider honoring cancellation. | `Socks5ControlConnectionTests.ServerAddressResolutionTimeoutDoesNotDependOnResolverCancellation`, `HandshakeTimeoutIncludesMethodSelectionRead`, `CommandTimeoutIncludesReplyRead` |
| RUS-002 | `Socks5UdpTransport.CreateAsync` owns its UDP socket from allocation and disposes it when control setup fails. | `Socks5ControlConnectionTests.UdpSocketIsDisposedWhenControlSetupFails` |
| RUS-003 | Caller/shutdown cancellation remains `OperationCanceledException` through control setup; coordinator disposal accepts cancelled setup tasks. | `Socks5ControlConnectionTests.CallerCancellationRemainsCancellationDuringHandshake`, `UdpProxyCoordinatorTests.CoordinatorDisposalPreservesSetupCancellation` |
| RUS-004 | Each newly inserted shared setup has a failure observer that removes its exact dictionary slot and association when it completes faulted/cancelled; a cancelled waiter does not cancel the shared setup. | `UdpProxyCoordinatorTests.CancelledWaiterDoesNotTearDownSharedSetupAndLateFaultReleasesCapacity` |
| RUS-005 | Successful session sends and receives touch the owned association with the same time source; association removal occurs only with the owning session. | `UdpProxyCoordinatorTests.SuccessfulSendRefreshesAssociationAndAvoidsStaleExpiry`, `ActiveSessionRetainsRelayAliasAfterItsCreationTimestampExpires` |
| RUS-006 | Expiry rechecks activity under the session activity gate before claiming removal, so send or receive activity refreshed after the sweep snapshot survives. | `UdpProxyCoordinatorTests.ExpirySnapshotDoesNotDisposeSessionWhoseSendRefreshesActivity`, `ExpirySnapshotDoesNotDisposeSessionWhoseReceiveRefreshesActivity` |
| RUS-007 | A non-shutdown receive-loop fault triggers prompt exact-session removal, association release, and transport disposal. | `UdpProxyCoordinatorTests.ReceiveFaultDisposesAndRemovesSessionWithoutAnotherSend`, `ImmediateReceiveFaultRemovesSessionAfterCoordinatorRegistration` |

### Validation

- Focused Linux-safe runtime regressions: `103` passed (`UdpProxyCoordinatorTests`, `UdpRelayTests`, `Socks5ControlConnectionTests`, and `FlowAndConfigurationTests`).
- Full Release validation: `dotnet build WinForward.slnx --configuration Release --no-restore` passed with `0` warnings and `0` errors; `dotnet test WinForward.slnx --configuration Release --no-restore` passed `220/220`; `git diff --check` passed.
- Native AOT publish: `dotnet publish src/WinForward.Cli/WinForward.Cli.csproj --configuration Release --runtime win-x64 --no-restore` is blocked on this Linux host because cross-OS native compilation is unsupported. Run this publish on Windows before release.

### Remaining Hardware Gate

- Not run on this Linux host: supported-Windows NDISAPI delivery for host and forwarded UDP responses,
  dynamic SOCKS relay traversal, adapter-map behavior, and the `win-x64` Native AOT publish. The
  existing fake-reinjector tests continue to cover target selection and frame direction; live
  driver/Hyper-V verification and Native AOT publish remain required on a supported Windows host.
