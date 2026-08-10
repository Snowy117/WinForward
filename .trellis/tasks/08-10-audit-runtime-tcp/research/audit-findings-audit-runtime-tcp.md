# Research: audit findings audit runtime tcp

- **Query**: Reconcile TCR-001 through TCR-010 against the current TCP redirect patch, add deterministic regressions for confirmed defects, and close the Runtime TCP audit.
- **Scope**: `TcpProxyCoordinator.cs`, `TcpProxyRelay.cs`, `TcpRedirectInjector.cs`, `TcpRedirectInterfaces.cs`, `TcpRedirectListener.cs`, `TcpRedirectTable.cs`, and their TCP tests.
- **Date**: 2026-08-10

## Ownership Map

The original flow key is claimed by `TcpRedirectTable.TryClaim` under one `_gate` lock. The association owns the original key, original destination, origin adapter context and enumeration handle, translated listener tuple, exact reverse wire tuple, generation, phase, and activity timestamp (`src/WinForward.Runtime/TcpRedirectTable.cs:20-46`).

`TcpProxyCoordinator` owns the session dictionary, listener, listener self-traffic token, session lifetime CTS, accepted connection, relay, and setup-drain counter (`src/WinForward.Runtime/TcpProxyCoordinator.cs:25-32`, `217-233`, `744-764`). A successful SYN is claimed exactly once, rewritten and injected toward MSTCP, then starts the accept loop (`TcpProxyCoordinator.cs:68-112`, `164-214`). A duplicate initial packet re-injects the existing translated tuple; a concurrent loser disposes its redundant listener and falls back to the same path (`TcpProxyCoordinator.cs:143-161`).

The accepted socket is checked against `AcceptedPeerEndpoint` before SOCKS setup (`TcpProxyCoordinator.cs:518-559`). Relay setup transfers ownership only through `TryAttachRelay`; expiry/disposal retires the session under `_gate`, cancels its lifetime, removes all aliases, disposes the listener/token/relay, and disposes the lifetime CTS (`TcpProxyCoordinator.cs:435-449`, `647-694`). Disposal is single-flight and waits for all in-flight SYN setup callers before taking its session snapshot (`TcpProxyCoordinator.cs:453-514`).

The concrete listener binds the IPv4/IPv6 wildcard address and records the accepted remote endpoint (`src/WinForward.Runtime/TcpRedirectListener.cs:9-47`). `TcpRedirectInjector` maps host-to-MSTCP to `ON_RECEIVE`/`SendToMstcp` and forwarded-to-adapter to `ON_SEND`/`SendToAdapter` (`src/WinForward.Runtime/TcpRedirectInjector.cs:7-20`). The endpoint rewrite contract is owned by Protocols and cross-referenced, not duplicated here: `PacketChecksums.TryRewriteTcpEndpoints` changes only IP addresses, TCP ports, and checksums; the independent checksum/mutable-offset/round-trip oracle remains in the protocols audit.

## TCR Reconciliation

### TCR-001: Same-family numeric-port reverse collision

**Status: fixed.** The old numeric `_byProxyPort` index is gone. The table stores `ReverseRedirectTuple(Source, Destination)` and resolves only the complete pre-rewrite wire tuple (`TcpRedirectTable.cs:57-60`, `95-114`, `122-143`). The coordinator applies the same exact candidate before reverse handling (`TcpProxyCoordinator.cs:283-318`, `347-359`). A wrong same-family packet is therefore `NotRelevant` and cannot touch activity or inject through another flow.

**Regression:** `SameFamilyUnrelatedReverseTupleDoesNotMatchAssociation` (`tests/WinForward.Core.Tests/TcpProxyCoordinatorTests.cs:434`) and `NonExactReverseProbeDoesNotRefreshActivity` (`TcpProxyCoordinatorTests.cs:60`) prove isolation and non-observing rejection. The expected reverse wire orientation is covered by `ReversePacketWithClassifierOrientationResolvesToOriginalFlow` (`TcpProxyCoordinatorTests.cs:74`) and `ReverseRewriteRestoresOriginalRemoteEndpoint` (`TcpProxyCoordinatorTests.cs:267`).

### TCR-002: Same-port translated aliases causing partial claims

**Status: fixed.** `TryClaim` checks both the translated listener tuple and exact reverse tuple before adding all three indexes under the single lock (`TcpRedirectTable.cs:84-115`). There is no numeric-port `Dictionary.Add`, so distinct IPv4/IPv6 translated tuples sharing a port cannot throw after a partial claim.

**Regression:** `DistinctTranslatedTuplesMaySharePortWithoutPartialClaim` (`TcpProxyCoordinatorTests.cs:44`) claims IPv4 and IPv6 translated endpoints with the same port and asserts two complete associations. `TranslatedTupleAliasCollisionIsRejectedFailClosed` (`TcpProxyCoordinatorTests.cs:97`) retains the distinct-flow collision/fail-closed coverage.

### TCR-003: Forwarded reverse injection selecting the observed handle

**Status: fixed.** The initial packet's enumeration handle is saved in the association (`TcpProxyCoordinator.cs:139-143`, `TcpRedirectTable.cs:20-38`). Forwarded reverse injection selects `association.OriginAdapterHandle`, while host injection continues to use the captured MSTCP-side handle (`TcpProxyCoordinator.cs:307-318`). A missing forwarded handle fails closed.

**Regression:** `ForwardedFlowReverseInjectsTowardOriginAdapter` (`TcpProxyCoordinatorTests.cs:302`) supplies a different reverse capture handle and asserts the saved `0x1234` origin handle is injected. `HostFlowReverseInjectsTowardMstcp` (`TcpProxyCoordinatorTests.cs:327`) covers the host direction.

### TCR-004: Existing-flow rewrite/injection failure retaining resources

**Status: fixed.** Existing data and reverse rewrite failures call `FailAssociationAsync`; injection faults take the same fail-closed teardown path, while cancellation of a retransmit/reverse packet remains caller-local and cannot retire the shared redirect (`TcpProxyCoordinator.cs:255-284`, `288-336`, `702-708`). Teardown removes the table aliases and session, cancels the accept loop, and releases listener/token/relay resources even when listener disposal faults (`TcpProxyCoordinator.cs:653-694`).

**Regression:** `ExistingFlowInjectionFailureReleasesAssociation` (`TcpProxyCoordinatorTests.cs:326`) fails the second injection of a retransmitted SYN and asserts `Blocked`, zero table entries, disposed listener, and no duplicate successful injection. `CancelledRetransmitDoesNotRetireSharedRedirect` and `CancellationAfterSynDoesNotStopSharedAcceptLoop` (`TcpProxyCoordinatorTests.cs:345`, `365`) prove a canceled caller cannot tear down a shared association or accept loop. Initial setup and relay failure coverage remains in `ListenerAllocationFailureBlocks` and `RelaySetupFailureBlocksAndReleasesAlias` (`TcpProxyCoordinatorTests.cs:155`, `171`).

### TCR-005: Expiry racing relay establishment

**Status: fixed.** Expiry retires only `Redirecting` sessions while holding `_gate`, marks the session closing, cancels its lifetime, and removes it from `_sessions` before asynchronous release (`TcpProxyCoordinator.cs:435-449`, `657-670`). A late relay cannot attach unless the exact session is still present, unretired, and redirecting (`TcpProxyCoordinator.cs:685-694`); a rejected late relay and accepted socket are disposed (`TcpProxyCoordinator.cs:554-559`).

**Regression:** `ExpiryRacingRelayEstablishmentCannotAttachDetachedRelay` (`TcpProxyCoordinatorTests.cs:499`) gates `EstablishAsync`, expires the session, then releases the factory and asserts the late relay, accepted connection, listener, and table ownership are released. `RemoveExpiredAsyncExpiresOnlyRedirectingNotRelayingSessions` (`TcpProxyCoordinatorTests.cs:471`) covers the stable phase distinction.

### TCR-006: Disposal missing setup committed after its snapshot

**Status: fixed.** `HandleSynAsync` increments the in-flight setup count before any await and decrements it in `finally` (`TcpProxyCoordinator.cs:68-73`, `704-718`). Single-flight disposal marks closed, starts one cleanup task outside the lock, cancels shared shutdown, waits for `_setupsDrained`, then snapshots sessions (`TcpProxyCoordinator.cs:453-514`). A late setup observes `_disposed` during session registration and releases its listener/table/token (`TcpProxyCoordinator.cs:217-233`, `164-171`).

**Regression:** `DisposeWaitsForInFlightSetupAndReleasesLateListener` (`TcpProxyCoordinatorTests.cs:524`) holds listener creation behind a TCS, starts disposal, proves disposal cannot complete, then releases creation and asserts the late listener and table association are released. `ShutdownDisposesAllSessionsAndListeners` (`TcpProxyCoordinatorTests.cs:366`) covers ordinary teardown.

### TCR-007: TCP Fast Open SYN payload accepted

**Status: fixed.** `ClassifyTcpSyn` compares the IP transport length with the parsed TCP header length and returns `WithPayload` for SYN data (`TcpProxyCoordinator.cs:362-413`). Proxy-selected SYN data is blocked before claim or listener allocation (`TcpProxyCoordinator.cs:370-381`).

**Regression:** `SynWithPayloadIsBlockedWithoutClaim` (`TcpProxyCoordinatorTests.cs:545`) builds a valid IPv4 SYN with three payload bytes and asserts no listener, association, or injection.

### TCR-008: Relay half-close not propagated

**Status: fixed.** The relay now distinguishes ended versus stalled pumps, forwards a FIN by calling `Socket.Shutdown(SocketShutdown.Send)` on the corresponding `NetworkStream`/local socket, and keeps the opposite pump alive for a final response (`src/WinForward.Runtime/TcpProxyRelay.cs:77-150`). `ITcpRelay.Completion` documents completion after both directions end, or on stall/error/teardown (`src/WinForward.Runtime/TcpRedirectInterfaces.cs:49-55`).

**Regression:** `HalfClosePropagatesFinAndAllowsReverseResponse` (`tests/WinForward.Core.Tests/TcpProxyRelayTests.cs:13`) verifies request bytes, upstream EOF, reverse response bytes, local EOF, and terminal completion.

### TCR-009: One-sided relay fault waiting for the other pump

**Status: fixed.** A pump fault is observed immediately, cancels the sibling pump, and completes the relay task without awaiting the sibling's stall timeout (`TcpProxyRelay.cs:84-110`, `170-177`). The existing 30-minute per-direction timeout remains a bounded last-resort stall policy (`TcpProxyRelay.cs:51-58`, `112-150`).

**Regression:** `OneSidedRelayFailureCancelsSiblingPumpImmediately` (`TcpProxyRelayTests.cs:41`) uses a write-failing upstream stream with a pending opposite read and requires relay completion to fault within two seconds.

### TCR-010: Accepted connection peer not validated

**Status: fixed.** `TcpRedirectListener` captures `RemoteEndPoint` (`TcpRedirectListener.cs:42-47`), and the coordinator closes any accepted socket whose endpoint is not the association's saved `AcceptedPeerEndpoint` before calling the relay factory (`TcpProxyCoordinator.cs:546-555`).

**Regression:** `UnrelatedAcceptedConnectionIsClosedBeforeRelaySetup` (`TcpProxyCoordinatorTests.cs:563`) sends an unrelated accepted peer followed by the expected peer, asserting the unrelated connection is disposed and only the expected destination reaches relay setup.

## Verified Existing Behavior

- Same-key sequential retransmission and a genuinely overlapping TCS-gated SYN burst remain exactly once: `SynClaimIsExactlyOnce` (`TcpProxyCoordinatorTests.cs:23`) and `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce` (`TcpProxyCoordinatorTests.cs:232`); the latter asserts `ConcurrentLoserCount == N - 1`.
- Address-family isolation and family-specific listener creation remain covered by `IPv4AndIPv6SynsAllocateMatchingFamilyListener` (`TcpProxyCoordinatorTests.cs:251`) and `Ipv6ReverseFrameWithCollidingPortDoesNotMatchIpv4Association` (`TcpProxyCoordinatorTests.cs:410`).
- Host/forwarded injection direction and `ON_SEND`/`ON_RECEIVE` mapping remain covered by `TcpRedirectInjectorTests` and the coordinator origin tests. The TCP rewrite byte contract, independent checksum oracle, mutable offsets, options, payload preservation, and round-trip are owned by the Protocols audit and are not duplicated here.
- Redundant accepted connections are drained after the first relay (`TcpProxyCoordinator.cs:580-608`); direct extra-accept coverage remains a gap.

## Coverage Gaps and Windows Gate

- The verification seam ran on Linux. It does not prove WinpkFilter/NDISAPI behavior, actual wildcard IPv4/IPv6 listener binding, Windows MSTCP local redirect delivery, SOCKS5 negotiation against a real server, adapter-route selection on Hyper-V, or `ON_SEND`/`ON_RECEIVE` behavior through the driver.
- The required Windows TCP gate remains pending: host and forwarded Hyper-V IPv4/IPv6 SYN, SYN retransmission, TCP options, data, FIN/RST/half-close, reverse delivery through a real SOCKS5 server, exact enumeration-handle injection, listener loop prevention, and mode restoration. This is the release gate in `.trellis/spec/backend/windows-ndisapi.md:93-132` and the archived design's TCP gate.
- Direct redundant-accept drain behavior and disposal during an active real socket accept remain integration gaps; the fake accept, expiry, and disposal seams cover ownership decisions but not the Windows socket implementation.
- The 30-minute relay stall timeout is intentionally retained as a bounded idle/stall policy. A long-idle established TCP session and a real peer stall must be validated on the supported Windows host.

## Verification Results

| Command | Result |
|---|---|
| Focused TCP tests (`TcpProxyCoordinatorTests`, `TcpProxyRelayTests`, `TcpRedirectInjectorTests`, `TcpEndpointRewriteTests`) | Passed: 49; failed: 0; skipped: 0. |
| `dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj --configuration Release --no-restore` | Passed: 230; failed: 0; skipped: 0. |
| `dotnet build WinForward.slnx --configuration Release --no-restore` | Succeeded: 0 warnings; 0 errors. |
| `git diff --check` | Passed with no whitespace errors |

No commit was made. The task is ready for independent check after the final verification commands pass; Windows hardware gates remain explicitly pending.
