# Technical design — close drain as the primary fix

## 1. Root cause, restated

The session's lifetime is tied to `relay.Completion`, but the client-visible TCP connection's
lifetime extends past it, through the close handshake (FIN → ACK) and any tail retransmissions.
`7cababb` left the lifetime bug in place and worked around it by hand-crafting a FIN|ACK before
the retire. That workaround is structurally half a fix:

- **Single-shot.** After the retire, a reverse-leg straggler (MSTCP's retransmitted FIN or tail)
  resolves to a tombstone hit and is consumed as `Dropped`
  (`TcpProxyCoordinator.HandleReverseIfApplicableAsync` → `NdisPacketActionExecutor.cs:384`),
  and so does the client's ACK. A dropped close packet is never retransmitted — 2–8 % residual
  hangs, measured.
- **Overtake hazard.** The crafted FIN is sequenced from bytes *accepted by MSTCP's send buffer*
  (`ITcpRelayEndInfo.ServerStreamBytes`), not bytes on the wire, so it can arrive at the client
  ahead of tail data still queued in the local stack — the only mechanism in the system that can
  put an out-of-order FIN in front of the client.

The fix moves the lifetime, not the packet: keep the session — and therefore the alias — live for
exactly the close handshake, and let the operating system's TCP carry the close with its
retransmission timers. The crafted FIN then has no job left and is removed (the design rule:
craft a packet only for a semantic the real stack cannot produce — the abnormal end's RST stays).

## 2. The enabling property: `closesocket` is not an abort

With the default linger (`l_onoff = 0`), `closesocket` is a graceful close: the TCB survives
the handle, flushes the send queue in order, emits the FIN, and retransmits on its RTO timer until
the peer acknowledges or the retransmission budget is exhausted. That is why today's flow emits a
FIN at all. The drain therefore does **not** hold the socket handle — the retransmitter already
exists and is already running; it is merely no longer wired to the client after the retire. The
drain re-wires it by keeping the alias.

*Assumption to falsify (AC1).* If the TCB were torn down at the handle close, the drain would
still deliver the first FIN but not a retransmitted one, and AC1's residual would stay. The
fallback is this task's original framing — transfer ownership of the local socket to the drain —
an addition to this design, not a different one.

## 3. The design: dispose, drain, retire

```
relay.Completion completes (CleanEnded)
├─ log tcp.relay.ended (cleanEnded)               — unchanged (kept from 7cababb)
├─ relay.DisposeAsync()                           — MOVED before the retire: the graceful close
│                                                    emits the real FIN while the alias is live,
│                                                    and the upstream is released no later than today
├─ association.Phase = Draining
│  drainTargetAck = serverISN + 2 + delivered     — SYN-ACK and FIN each consume one sequence
└─ await drain:  ClientAckMax >= drainTargetAck   ──► acknowledged
                | deadline                        ──► bounded exit  (R5)
                | session token                   ──► another retire path already won
   └─ tearDownSession(session)                    — unchanged: atomic alias removal + tombstone
```

Abnormal ends (`Stalled` / `Faulted`) are untouched: crafted RST|ACK, then immediate teardown.

The five invariants that make this safe:

- **The relay is disposed before the drain arms, though not for the FIN.** The real FIN is
  emitted when `RunPumpAsync`'s `await using` closes the client-facing socket, i.e. just before
  `Completion` completes — arming the drain does not depend on a later disposal to produce it
  (measured 2026-10-09; the earlier reading that the acceptor's disposal was what emitted the FIN
  was wrong). The cleanup order still pulls the disposal forward because that is where the
  upstream budget is released: today it happens inside `ReleaseRetiredAsync` after the retire
  (`TcpRedirectSessionStore.cs:299-303`). Disposal is single-flight, so the store's later release
  joins the same teardown and the acceptor awaits it either way.
- **The retire keeps its atomicity.** Nothing about `RetireSessionBodyUnderGate` changes:
  session removal, `Phase = Closing`, lifetime cancel, table alias removal and tombstone arming
  stay one store-gate critical section. The drain only moves *when* that section runs.
- **The alias is legitimately live, not resurrected.** The session is still registered and the
  association still claims the tuple, so a straggler resolves to *this* flow exactly as during
  relaying, and no new setup can arm for the tuple. This is not the parent design's rejected
  "keep the reverse alias resolvable after the retire" — the drain never retires and then
  resolves; it resolves and then retires. The client's own TCB holds the tuple in
  FIN_WAIT/TIME_WAIT, so it cannot legitimately dial the same tuple meanwhile.
- **Every other exit still wins.** The drain's wait is bound to `session.Token`, which
  `Retire()` cancels: an injection failure, a fragment teardown, capacity pressure, the sweep or
  shutdown ends the drain immediately, and the later `tearDownSession` is the no-op it already
  is for an already-retired session.
- **The client's ACK is observed, never consumed.** The observation sits beside
  `TcpSequenceObservation.TrackClientSequence` (`TcpProxyCoordinator.Injections.cs:256`) and
  the frame continues through the rewrite toward MSTCP — that forwarding is exactly what lets
  MSTCP's TCB complete its own close.

## 4. The drain target is computed, not observed

`drainTargetAck = serverISN + 2 + delivered`:

- `serverISN` — `association.ServerInitialSeq`, recorded from the listener-side SYN-ACK
  (`TcpSequenceObservation.RecordServerSynAck`).
- `+1` for the SYN-ACK itself, `+1` for the FIN.
- `delivered` — the relay's `ServerStreamBytes`: bytes accepted into MSTCP's send buffer, which
  for a byte stream is exactly the sequence space the FIN will carry (the parent's iteration 2
  measured this as exact).

Rejected: observing the FIN's transit on the reverse leg and anchoring the target there. It needs
an arm-ordering race resolved (the FIN may transit before or after the drain arms), trusts a
capture event instead of a byte count, and buys nothing over the computed target.

Degradation (R8): no `serverISN`, no SYN template, or a relay that does not implement
`ITcpRelayEndInfo` → no computable target → no drain, immediate retire. Identical degradation
shape to the crafted FIN, which needs the same sequences.

## 5. ClientAckMax is tracked always, not only while draining

`TrackClientAck` reads the frame's ACK field (transport offset +8) and keeps the advance-only
serial-arithmetic maximum on the association — the same observer shape as
`ObserveClientSequence` (`TcpRedirectTable.cs:158-166`), reusing `IsSequenceAhead`
(`:181`). It runs for every forward-leg packet because the client can acknowledge our FIN
*piggybacked on its own FIN* — i.e. before the relay ends and the drain is armed (the
upstream-first ordering does exactly this). Arming the drain therefore starts with a check:
`ClientAckMax >= drainTargetAck` → exit immediately, no wait, no timer beyond the check.

The wait itself: one `TaskCompletionSource` per draining association, created with
`TaskCreationOptions.RunContinuationsAsynchronously` — the retire continuation must never run
on the capture pump thread. The ACK observation completes it; the deadline and the session token
complete it with their own reasons. One 4-byte read and a signed compare per forward packet,
steady-state and during the drain — no allocation, no delegate (R6).

## 6. What is removed, what is kept

Removed (R2):

- `ClientResetInjector.TryInjectClientCloseAsync` and its `TryInjectClientCloseCoreAsync`
  FIN flavor — the RST flavor stays as `TryInjectClientResetAsync`.
- `TcpResetBuilder.TryBuildFin` (the RST builder is untouched).
- The clean-end branch of `TcpRedirectAcceptor.InjectClientVisibleCloseAsync` — a clean end
  injects nothing; it drains.
- The FIN-side tests (`TcpRelayEndCloseTests.cs` becomes the drain test suite; AC6 names them).

Kept:

- `RelayEndKind` and the `tcp.relay.ended` outcome (the parent's R4 observability).
- `ITcpRelayEndInfo.ServerStreamBytes` — now the drain's target input instead of the FIN's
  sequence input.
- The abnormal-end crafted RST|ACK path in full.
- `TcpProxyRelay` itself is unchanged: it still closes the client-facing socket as
  `RunPumpAsync` returns — that close is now the close that matters, delivered by construction.

During VM verification only: the A/B is two builds from frozen snapshots of the same revision —
the as-landed tree and a drain-only variant whose only change removes this injection (AC4) — so
no switch ever exists in the code and the committed shape has no trace of one.

## 7. Landing points

| # | Unit | Change |
|---|---|---|
| 1 | `TcpRedirectTable.RelayPhase` / `TcpRedirectAssociation` | Add `Draining`; add the drain state: volatile flag, `DrainTargetAck`, advance-only `ClientAckMax`, and the completion source. Raw fields, matching the association's hot-path contract. |
| 2 | `TcpSequenceObservation` | `TrackClientAck(frame, layout, association)`: read the ACK field, keep the serial maximum. |
| 3 | `TcpProxyCoordinator.Injections.ReinjectExistingFlowDataAsync` | Call (2) beside `TrackClientSequence`; when draining, complete the drain if the tracker covers `DrainTargetAck`. |
| 4 | `TcpRedirectAcceptor.ObserveRelayCompletionAsync` | The `CleanEnded` branch becomes §3: dispose the relay, arm the drain, await it, then tear down. Non-clean ends unchanged. |
| 5 | `TcpRedirectAcceptor` / composition | The drain deadline: internal default of a few seconds, injectable for tests, no configuration surface. |
| 6 | `TcpRedirectLog` | One drain event: association, exit reason, elapsed — AC5's measurement. |
| 7 | `ClientResetInjector`, `TcpResetBuilder`, tests | The R2 removal. |

## 8. Risks, and how each is falsified

| Risk | Handling |
|---|---|
| MSTCP tears the TCB down at `closesocket` (the §2 assumption) | AC1: the residual stays instead of reaching zero; fallback is holding the socket handle in the drain. |
| MSTCP aborts the TCB with an RST late in the drain | Pre-existing (the TCB is MSTCP's in both designs), bounded by the deadline. The clean-end path is only reached after the client's own FIN, so no client data can follow it. |
| The drain never sees an acknowledgement (fragmented forward leg, capture gap) | The deadline; the exit reason is logged, so a systematic case shows up as a deadline cluster, not a silent hang. |
| A straggler during the drain arms a new setup | Cannot happen while the association claims the tuple — the flow key resolves to this association first, and the data path never gates on `RelayPhase`. |
| Capacity/DoS by half-closing and vanishing | Bounded by the deadline; the added concurrency is `deadline × clean-end rate`, and the capacity gate already covers the aggregate. |
| Removing the crafted FIN regresses a case the drain misses | AC4's A/B arms measure exactly this before the removal is committed; a worse drain-only result re-scopes the removal with the user. |

## 9. Verification design

**AC0 — settle the mechanism at zero code cost.** Classify the timed-out attempts of a
`halfClose=100` arm from the arm's own `type: "attempt"` records
(`echoedBytes`/`trailerBytes`/`eof`): a lost close vs a tail gap vs a mix. Recorded under
`research/`.

**AC6 — regression tests (each fails before its part of the change):**

1. A clean end drains and retires on the observed acknowledgement.
2. A clean end with no acknowledgement retires at the deadline.
3. Sequences never observed → no drain, immediate retire.
4. Ordering: the retire never precedes the drain's exit (asserted on the store's
   `tcp.redirect.closed` vs the drain event).
5. A straggler during the drain resolves to the same association — no setup armed, no tombstone.
6. The piggybacked acknowledgement (client's own FIN carries the ACK) ends the drain with no wait.
7. The removal: a clean end injects no crafted packet — fails while `7cababb`'s FIN path exists.

**AC1–AC4 — the VM arms** (existing harness, unchanged): `halfClose=100` and `clean=100`
single-mode runs, the four-mode REL arm as the regression gate, and the A/B pair for the removal.
**AC5** reads the drain event from the same runs.

**Spec.** `.trellis/spec/backend/tcp-client-close-injection.md` loses the clean-end FIN contract
and gains the drain contract (it becomes the close-*drain* spec); `tcp-local-redirect.md`'s
close row and its "known residual" paragraph are replaced by the measured outcome.

## 10. Rollback

The drain is confined to the acceptor's `CleanEnded` branch, the association's drain fields, one
observation call on the forward leg, one log event and the deadline default; reverting it restores
`7cababb`'s behaviour only if the removal commit is also reverted — the two land as separate
commits (drain first, removal second) so each is independently revertible. Reverting both restores
today's behaviour exactly, including its measured 92 % win.
