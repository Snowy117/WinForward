# Tolerate TFO SYN-with-payload in TCP redirect

## Goal

A LAN client using TCP Fast Open (RFC 7413) sends its SYN with data in the first segment
(`TcpSynKind.WithPayload`). Today `TcpProxyCoordinator.HandlePacketAsync` fail-closes this
shape (`Blocked`, packet silently consumed), so such a client's connections through
WinForward time out after 20–60 s of swallowed SYN retransmissions — the program appears
unable to reach the network at all. Make the redirect path tolerate SYN-with-payload by
routing it through the same pipeline as a bare SYN. WinForward does not implement TFO
itself (no speedup is claimed); it must merely not break flows that use it.

## Background

- TCP graceful degradation (RFC 7413 §1): a server that does not do TFO completes a normal
  handshake and does not ACK the SYN data; the client retransmits the data post-handshake.
  Tolerating (not supporting) TFO therefore requires no listener-side change: the local
  listener stack either queues or drops the SYN payload, and either way the flow recovers.
- Blocking point: `TcpProxyCoordinator.cs:421` — `if (syn == TcpSynKind.WithPayload)
  return TcpRedirectOutcome.Blocked;` (no RST is sent; the executor consumes the packet).

## Requirements

- R1. A proxy-selected TCP flow whose SYN carries payload must be redirected exactly like a
  bare SYN: pending-setup retain, listener allocation, table claim, forward-leg rewrite,
  injection toward MSTCP.
- R2. A retransmitted SYN-with-payload on an already-claimed flow must follow the existing
  reuse path (table hit → `ReinjectExistingFlowDataAsync`), including correct client-sequence
  tracking (`payload + SYN` advance is already implemented in
  `TcpSequenceObservation.TryReadTcpSequenceAdvance`).
- R3. Failure paths must keep today's fail-closed semantics for genuinely malformed frames
  (unparseable → `TcpSynKind.None` → not ours to handle).
- R4. The `TcpSynKind.WithPayload` concept is removed unless a live consumer remains; the
  classifier distinguishes only SYN vs non-SYN. No dead enum members or "which is blocked"
  doc comments may survive.
- R5. No behavior change for bare SYNs, mid-flow data, reverse legs, fragments, or UDP.

## Constraints

- Listener side stays a plain `Socket` — no TFO socket options (Windows user-mode listener
  TFO is not exposed; out of scope).
- Sequence/checksum correctness must not regress: the injected rewritten SYN must carry a
  valid TCP checksum (incremental RFC 1624 update is payload-agnostic — verified).
- Keep the pump contract: nothing in the SYN-with-payload path may block the capture pump
  (R8 pending-setup contract unchanged).

## Acceptance Criteria

- [ ] A SYN-with-payload on a new flow yields a claimed table entry, one listener, one
      injected (rewritten) frame — i.e. the same observable effects as a bare SYN test.
- [ ] The injected rewritten SYN retains the client's payload bytes and passes checksum
      validation.
- [ ] A retransmitted SYN-with-payload after the flow is claimed reuses the association
      (no second listener, no second session).
- [ ] The former `SynWithPayloadIsBlockedWithoutClaim` test is replaced by the tolerance
      tests above.
- [ ] `rg -n "WithPayload" src` returns no hits.
- [ ] Full test suite (`dotnet test`) green; `dotnet build -warnaserror` clean.
