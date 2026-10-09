# TCP Client-Visible Close: Abort Injection and Clean-End Drain

> How WinForward ends a client-visible connection it owns: the crafted RST|ACK that aborts an
> abnormal end, the bounded drain that carries a clean end's real FIN, how their sequences are
> derived, and what a failed injection does.
> Read it when you change `ClientResetInjector`, `TcpResetBuilder`, `TcpResetCooldownTable`, a sequence
> tracker, the acceptor's relay-end handling, or any teardown path whose client must see a close. Part
> of the TCP local-redirect family; the hub
> [tcp-local-redirect.md](./tcp-local-redirect.md) has the pipeline overview, the cross-cutting
> invariants and the topic map. The relay-side half — why an end is `CleanEnded`, `Stalled` or
> `Faulted`, and what `Completion` means — is in
> [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md).

## The abort shape (RST|ACK)

`TcpResetBuilder` crafts the abort from the association's recorded client-SYN template: the Ethernet
header is mirrored with the addresses swapped, and everything at L3/L4 is built fresh with its own
checksums, so the frame never depends on parser or rewrite state. It is header-only
(`MaxResetFrameLength` = 74 B for IPv6).

| Frame | Flags | Sequence | Acknowledgement | Meaning |
|---|---|---|---|---|
| Abort | RST\|ACK, `0x14` | `ServerNextSeq ?? serverInitialSeq + 1` | `ClientNextSeq ?? clientInitialSeq + 1` | The connection ends abnormally: relay setup failed, the relay stalled or faulted, a fragment hit an associated flow, or an injection failed. |

`ClientResetInjector.TryInjectClientResetAsync` builds the abort; a relay-less caller uses the
association-level overload. The abort acknowledges the client's tracked progress so it stays in-window
(RFC 5961) after a slow relay setup; a stale `ISN + 1` is out-of-window once the client has sent data
and the stack silently discards the reset, producing the slow-EOF symptom. Degradation is uniform: no
SYN template, or an unobserved initial sequence number, means nothing is injected and the teardown is
plain (the client then observes the failure on its own retransmission timeout).

## Sequence and acknowledgement tracking (F4, 2026-09-30)

`TcpRedirectAssociation` observes both directions at the two pre-rewrite points, wrap-aware and
advance-only: two `long` fields (`-1` = unobserved, so `0xFFFFFFFF` stays a legal tracked value) written
by a CAS-max loop over the `IsSequenceAhead` predicate and read with `Volatile.Read`. The `uint?`
properties are unchanged, and the builders tolerate a weakly consistent read because the value only ever
moves forward inside the comparison window. There is no per-association lock and no gate: a redirected
forward+reverse packet pair takes zero gate entries (`RedirectPacketTakesZeroSequenceGateEntries`,
`TcpRedirectAssociationHoldsNoLockField`, `ConcurrentSequenceObservationsKeepTheLargerValue`,
`UnobservedTrackerReadsNullAndObservedZeroReadsZero`). The entry points are
`TcpSequenceObservation.TrackClientSequence` / `TrackServerSequence` (a `PacketLayout` overload plus a
span-taking twin kept adjacent as the oracle); the private `TryReadTcpSequenceAdvance` computes
`payloadLen + SYN + FIN` from the IP-derived transport length, never the frame length — Ethernet padding
would otherwise be counted as payload. New observation sites must read the frame **before** the rewrite
(original bytes) and synchronously (a span must not cross an `await`).

`TrackClientAck` extends the same shape to the forward leg's ACK field and feeds the close drain
through `ObserveClientAck`, which keeps `ClientAckMax` as an advance-only serial maximum. It requires
the ACK control bit (`0x10`) before reading, so a SYN's zero field is never tracked as an
acknowledgement — a tracked zero would cover a drain target in the upper half of the serial space and
end a drain before the close was acknowledged. It runs on the forward data path
(`ReinjectExistingFlowDataAsync`), not only while draining, because the client can acknowledge a FIN
piggybacked on its own FIN before the drain arms. It is exposed as the same `PacketLayout` overload
plus span-taking twin as the sequence trackers.

## A clean end drains the close handshake instead of injecting a FIN

A clean end needs no crafted packet. `TcpProxyRelay.RunPumpAsync`'s graceful socket close makes MSTCP
emit the real FIN — in order behind everything the relay delivered, on the stack's own retransmission
timer — and the only thing that must outlive the relay is the alias that resolves the client's
acknowledgement and any retransmitted FIN. The drain moves the session lifetime, not a packet.

- **Trigger and order.** `TcpRedirectAcceptor.ObserveRelayCompletionAsync` drains only
  `RelayEndKind.CleanEnded`: `DrainCleanEndAsync` computes the target, disposes the relay, calls
  `ArmDrainAsync`, waits, and only then runs the retire. The disposal precedes the arm because it
  releases the upstream and control sockets when the handshake starts rather than after it; the FIN
  itself is already emitted by the pump's own socket close, before `Completion` completes. Disposal is
  single-flight, so the store's later release joins the acceptor's teardown instead of repeating it.
- **The target is computed, not observed.** `TryComputeDrainTargetAck` needs all three inputs and
  derives `association.ServerInitialSeq + 2 + ServerStreamBytes`: the listener-side ISN (recorded from
  the reverse SYN-ACK), one for that SYN-ACK and one for the FIN, and the bytes the relay delivered to
  the client-facing socket — together, the acknowledgement that covers the close. Any unknown input
  degrades uniformly — no recorded SYN template, no listener-side ISN, or a relay without
  `ITcpRelayEndInfo` — to no drain and an immediate retire.
- **The arm publishes one target and one completion cell.** `ArmDrainAsync` is single-flight per
  association: the first caller wins the target and every later caller joins the first cell. The
  association enters `RelayPhase.Draining` while the handshake is in flight; the data path never gates
  on the phase, so a straggler still resolves through the live alias to this association and arms no
  setup.
- **Three exits, one event.** The wait ends as `acknowledged` when `ClientAckMax` covers the target
  (rechecked at arm time, so an acknowledgement that arrived before the arm exits with no wait), as
  `deadline` when the 5 s default elapses (`TcpRedirectAcceptor`, injectable for tests, no
  configuration surface), and as `retired` when the session token is cancelled because another retire
  path won. `tcp.redirect.drain` records the association, both endpoints, `Outcome` and `ElapsedMs`
  at Debug.
- **The retire is untouched.** The drain delays *when* the store's atomic critical section runs, never
  splits it: session-dict removal, `Phase = Closing`, lifetime cancel, alias removal and tombstone
  arming stay one step inside the store gate. Order is resolve-then-retire, never the reverse — the
  drain's stragglers consume the live alias, and only what arrives after the retire hits the tombstone.
- **Locked by** `TcpCloseDrainTests` — `CleanEndInjectsNoCraftedPacketAndDrainsUntilTheClientAcknowledges`
  (nothing is crafted from the SYN template; dispose → drain → retire),
  `CleanEndDrainsUntilTheClientAcknowledgesThenRetires`
  (dispose before the wait, retire only after the acknowledgement),
  `CleanEndWithoutAnAcknowledgementRetiresAtTheDeadline`,
  `CleanEndWithoutObservedSequencesRetiresImmediatelyWithoutADrain`,
  `CleanEndWithoutEndInfoRetiresImmediatelyWithoutADrain`, `TheRetireNeverPrecedesTheDrainExit`,
  `AStragglerDuringTheDrainResolvesToTheSameAssociationAndArmsNoSetup`,
  `AForwardAckThatCoversTheCloseCompletesTheArmedDrain`, `ArmingADrainNeverRewritesAClosingPhase`,
  `AnotherRetirePathEndsTheDrainImmediately`, `APiggybackedAcknowledgementEndsTheDrainWithoutWaiting`,
  and `ClientAckTrackingIsAdvanceOnlyWrapsAndIgnoresFramesWithoutTheAckFlag` — plus the retained RST
  facts `TcpRelayEndCloseTests.FaultedRelayEndInjectsInWindowClientResetBeforeTeardown` /
  `StalledRelayEndInjectsClientResetBeforeTeardown` and
  `TcpProxyRelayTests.RelayReportsTheServerStreamBytesItWroteToTheClient` for the target's byte input.

## Relay setup failure resets the client (fixed 2026-08-14)

When the SOCKS5 relay cannot be established after a successful redirect (proxy down, auth failure,
upstream unreachable), the client's connection is already established through the redirect leg and would
otherwise hang. The coordinator records the client ISN plus a bounded copy of the original SYN at setup,
and the server ISN when the reverse SYN-ACK passes the hook; on relay failure
`HandleRelaySetupFailureAsync` closes the accepted connection, injects the abort with
`seq = ServerNextSeq ?? serverInitialSeq + 1` toward MSTCP (host) or the origin adapter (forwarded), and
tears the session down. Missing sequence numbers degrade to plain teardown. Locked by
`RelaySetupFailureInjectsClientResetWhenSequencesKnown` /
`ForwardedRelayFailureInjectsClientResetTowardOriginAdapter` / `RelaySetupFailureBlocksAndReleasesAlias`
(degradation) and `TcpResetBuilderTests`.

The relay call site (`TcpProxyRelayFactory`) bounds the failure: `RelayConnectMaxAttempts = 2` (internal
const) with `s_relayConnectAttemptTimeout = 10 s` (internal `static readonly`). The per-attempt default is
30 s and multi-address DNS can multiply it, while the client is already established once the redirect is
accepted — so every extra budget second is a "connected then reset" second.

## Capacity-rejected SYN → RST|ACK (the shape only)

`ClientResetInjector.InjectCapacityRejectedResetAsync` aborts a SYN the capacity gate refused, before any
association exists: `TcpResetBuilder.TryBuildResetFromSyn(synFrame, serverAddress, serverPort,
clientAddress, clientPort, destination, out written)` reads the client ISN from the observed SYN and
builds `seq=0, ack=clientISN+1, flags=RST|ACK` — in-window for a SYN_SENT client, which aborts immediately
with ECONNREFUSED instead of a 20-60 s retransmission timeout. MACs and IPs are mirrored from the SYN
template (here the captured SYN itself). The call is **claim-then-inject**:
`TcpResetCooldownTable` (per original 4-tuple, 1 s window, capacity = the session budget, FIFO
evict-oldest; a refreshed tuple is not re-enqueued) claims the window **before** any build or inject
attempt, so even a failed injection consumes the cooldown — the strongest anti-amplification posture.
Injection follows the direction matrix (host → MSTCP, forwarded → the origin adapter's capture handle);
it never throws and never changes the `Blocked` result. Debug event `tcp.redirect.capacityReset`; the
`tcp.redirect.rejected reason=capacity` trace and the `tcp.redirect.capacity` summary are unchanged.
Locked by the capacity coordinator tests: exactly one RST per tuple per window, retransmissions inside the
window silent, a new RST after expiry, the direction matrix, and an injection-failure warn leaving the
result unchanged. **What triggers the gate** — the session budget, pending setups counted alongside live
sessions, and the pending-index refusal that shares the posture — is in
[tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md).

## Fragments on associated flows

`TcpProxyCoordinator.HandleFragmentAsync` (the `FlowDispatcher` fragment handler, consulted in
`DispatchNonFlowAsync` after the self-traffic check) resolves the fragment's normalized IP pair through
`TcpRedirectTable.TryResolveByAddressPair` (a third index, direction-agnostic, same-family gated,
last-writer-wins for multi-flow pairs, `ReferenceEquals`-guarded removal, all under the single `_gate`).
A hit traces `tcp.redirect.fragment reason=fragment` and runs `HandleFragmentTeardownAsync`: a best-effort
abort via the tracked sequences, a warn plus silent teardown when sequences were never observed, then the
fail-closed association removal through the single tombstone write point; the outcome is `Dropped`.
A miss stays `NotRelevant`, preserving the non-flow pass. The dispatcher maps the fragment handler's
`Dropped` to `ProxyConsumed` (silent) and `Blocked` to the policy path. Accepted residual: post-tombstone
fragments fall back to pass (bounded window), and address-pair granularity can tear down the newer of two
same-IP-pair associations.

## Injection-failure exits

Every `SendPacketTo*` failure exits through `ClientResetInjector.HandleInjectionFailureAsync`: a warn
`tcp.redirect.failed reason=injectionFailure` with the native error, adapter handle and flow key, then a
best-effort abort, then `FailAssociationAsync`. Free-text catches around injections are forbidden — a
silent `FailAssociationAsync` after adapter-handle staleness is exactly the invisible-teardown defect this
rule exists to prevent. The deferred-lane flush has the same posture through its own rate-limited
`tcp.redirect.deferred-failed` warn (see
[tcp-redirect-transform.md](./tcp-redirect-transform.md) for the lane contract).
