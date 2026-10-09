# TCP Client-Visible Close Injection

> How WinForward ends a client-visible connection it owns: the RST|ACK and FIN|ACK
> shapes, how their sequences are derived, which end produces which, and what a failed injection does.
> Read it when you change `ClientResetInjector`, `TcpResetBuilder`, `TcpResetCooldownTable`, a sequence
> tracker, the acceptor's relay-end handling, or any teardown path whose client must see a close. Part
> of the TCP local-redirect family; the hub
> [tcp-local-redirect.md](./tcp-local-redirect.md) has the pipeline overview, the cross-cutting
> invariants and the topic map. The relay-side half — why an end is `CleanEnded`, `Stalled` or
> `Faulted`, and what `Completion` means — is in
> [tcp-relay-lifecycle.md](./tcp-relay-lifecycle.md).

## The two shapes

Both are crafted by `TcpResetBuilder` from the association's recorded client-SYN template: the Ethernet
header is mirrored with the addresses swapped, and everything at L3/L4 is built fresh with its own
checksums, so the frame never depends on parser or rewrite state. Both are header-only
(`MaxResetFrameLength` = 74 B for IPv6).

| Frame | Flags | Sequence | Acknowledgement | Meaning |
|---|---|---|---|---|
| Abort | RST\|ACK, `0x14` | `ServerNextSeq ?? serverInitialSeq + 1` | `ClientNextSeq ?? clientInitialSeq + 1` | The connection ends abnormally: relay setup failed, the relay stalled or faulted, a fragment hit an associated flow, or an injection failed. |
| Close | FIN\|ACK, `0x11` | `serverInitialSeq + 1 + ServerStreamBytes` when the delivered count is known, otherwise `ServerNextSeq ?? serverInitialSeq + 1` | `ClientNextSeq ?? clientInitialSeq + 1` | The relay ended cleanly. |

`ClientResetInjector.TryInjectClientResetAsync` builds the abort and `TryInjectClientCloseAsync` the
close; a relay-less caller uses the association-level overloads. The abort acknowledges the client's
tracked progress so it stays in-window (RFC 5961) after a slow relay setup; a stale `ISN + 1` is
out-of-window once the client has sent data and the stack silently discards the reset, producing the
slow-EOF symptom. The **delivered byte count wins for the close's sequence**: `ServerStreamBytes` is
counted per successful write to the client-facing socket, while the packet-path tracker can lag the
capture pipeline and also counts the SYN-ACK and the client-facing socket's own FIN, either of which
would place the FIN past the client's receive sequence — a FIN the client queues forever. Degradation is
uniform: no SYN template, or an unobserved initial sequence number, means nothing is injected and the
teardown is plain (the client then observes the failure on its own retransmission timeout).

## Sequence tracking (F4, 2026-09-30)

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

## Every relay end injects its client-visible close before the retire

This is the relay-end section: `TcpRedirectAcceptor.ObserveRelayCompletionAsync` runs
`InjectClientVisibleCloseAsync` **before** `tearDownSession`, while the association still holds the SYN
template and the trackers. `RelayEndKind.Stalled` and `Faulted` get the RST|ACK; `CleanEnded` gets the
FIN|ACK. A relay that does not implement `ITcpRelayEndInfo` is treated as `CleanEnded`.

- **The clean-end FIN is not left to the socket close.** `TcpProxyRelay.RunPumpAsync` closes the
  client-facing socket as it returns, i.e. in the same instant `Completion` completes, so the socket's own
  FIN races the retire that releases the reverse index and arms the tombstone. A client that half-closed
  (`shutdown(SD_SEND)`) has already ended the read direction, so the race normally loses and the client
  observes **no end of stream at all** — measured 545 of 601 half-closing attempts at the time of the fix,
  hanging to the client's own 10 s timeout with no FIN, no RST and no truncation (task
  10-06-tcp-half-close-fidelity, 2026-10-06).
- **Ordering and containment.** Reset → teardown (the injector reads live-association state, matching the
  `HandleRelaySetupFailureAsync` precedent); a close-injection failure warns (rate-limited, per kind) but
  never blocks the teardown, and the whole completion tail is wrapped so nothing escapes the
  fire-and-forget task. An `OperationCanceledException` on an externally cancelled (retired) session
  returns early without injecting.
- **Locked by** `TcpRelayEndCloseTests` (clean end → FIN|ACK sequenced from the delivered byte count,
  injected before the teardown; end-info-less relay → FIN|ACK sequenced from the trackers; unobserved
  sequences → nothing injected; fault/stall → RST|ACK with seq/ack from the advanced trackers, not
  `ISN+1`; real-relay `EndKind` on all three terminal paths, including
  `RelayEndKindIsStalledWhenPumpStalls`) and `TcpProxyRelayTests.RelayReportsTheServerStreamBytesItWroteToTheClient`.
- **Residual (state of the current code).** The close is injected exactly once; the retire that follows it
  is immediate, and once the alias is gone neither the crafted close, nor the socket's own FIN, nor any
  unacknowledged tail data can be retransmitted — a close packet dropped at the client is lost for good
  (the parent task's arm evidence: 23 of 1201 four-mode attempts and 50 of 601 single-mode `halfClose`
  attempts still hung, with the close injected exactly once and no injection-path warning). **The redesign
  is active in task `10-07-tcp-close-drain`** — a bounded drain that keeps the alias alive until the
  client acknowledges the close. Until that lands, this document states the immediate-retire contract and
  does not describe the drain.

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
