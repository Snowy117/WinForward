# Design: Tolerate TFO SYN-with-payload

## Change surface (verified by code reading, 2026-09-06)

Single routing decision plus its supporting concept:

1. `TcpProxyCoordinator.HandlePacketAsync` (`TcpProxyCoordinator.cs:421`): delete the
   `WithPayload → Blocked` branch. `syn == Empty` and `syn == WithPayload` both route to
   `HandleSynAsync`; the mid-flow/tombstone/NotRelevant tail is untouched.
2. `TcpFrameRewriter.ClassifyTcpSyn` + `TcpSynKind`: remove the `WithPayload` member and the
   transport-length comparison that computes it. The classifier answers one question only:
   is this frame a SYN (SYN set, ACK clear)? Callers that need payload length already have
   `TryReadTcpSequenceAdvance`.

## Why each downstream stage is already payload-safe (no changes needed)

| Stage | Evidence |
| --- | --- |
| Pending-SYN retain (`StartPendingSetup`) | copies the whole inspection span (`ToArray()`); payload included, bounded budget unchanged |
| `TcpPendingSynSetup` retransmission overwrite | entry replacement is by-value frame copy; same client ISN means a rewritten retransmission is equivalent |
| Forward-leg rewrite (`TryRewriteForwardLeg`) | `PacketChecksums.TryRewriteTcpEndpoints` is an RFC 1624 incremental update over changed words (addresses/ports) only — payload bytes never touched, checksum stays valid |
| Client ISN + template (`RecordClientSyn`) | reads the sequence field; `OriginalSynFrameCopy` is a bounded 128-byte template — `TcpResetBuilder.BuildReset` builds a fresh header-only frame from it, payload irrelevant |
| Sequence tracking | `TryReadTcpSequenceAdvance` computes `payloadLen + SYN + FIN` from IP totalLength — already correct for data-bearing SYNs; fallback `ClientInitialSeq + 1` only fires when no advancement was observed |
| Capacity RST (`BuildResetFromSyn`) | header-only build, reads ISN from the SYN — works with payload SYNs |
| Listener/relay | non-TFO listener stack queues or drops SYN data; client retransmits post-handshake (RFC 7413 graceful degradation) — either way the SOCKS5 relay sees a normal stream |

## Rejected alternative

Keep `WithPayload` for telemetry-only classification. Rejected: it would be a dead concept
(no consumer) in a codebase whose modules intentionally carry no unused surface; the
classifier's doc comment ("which is blocked") would lie either way.

## Risks

- Hidden consumers of "SYN implies header-only frame" elsewhere: audited via
  `rg OriginalSynFrameCopy|ClassifyTcpSyn|WithPayload` — only the sites above.
- Real-world TFO middlebox quirks are moot: WinForward *is* the middlebox; the rewritten
  SYN keeps its payload and options verbatim, so the only new party that sees the payload
  is the local listener stack, which is on-loop for forwarded flows (and host-local for
  host flows).

## Test design

Replace `SynWithPayloadIsBlockedWithoutClaim` (`TcpProxyCoordinatorRewriteTests.cs:347`) with:

1. `SynWithPayloadIsRedirectedLikeBareSyn` — assert settled setup outcome, `table.Count == 1`,
   one listener, one injected frame whose payload bytes survive and whose TCP checksum
   validates.
2. `RetransmittedSynWithPayloadReusesAssociation` — second SYN-with-payload resolves to the
   existing association, no second listener/session, and `ClientNextSeq` advanced past
   `ISN + 1 + payloadLen` (pins R2's tracking claim).

Existing helpers (`HandleSynSettledAsync`, `MakeSynPacket(..., payload)`) already support
payload-bearing SYN construction.

## Rollback

Single-commit revert of the two-file change plus tests; no persistent state, no config
surface, no protocol migration.
