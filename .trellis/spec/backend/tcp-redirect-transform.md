# TCP Redirect Transform and Routing

> How a proxied TCP frame is reshaped and routed: the host IP-swap transform, the forwarded
> DNAT-to-local shape, the reverse hook, the mid-flow data legs, and the accept-loop identity rules.
> Read it when you touch `TcpFrameRewriter` / `PacketChecksums` rewrite shapes, `TcpProxyCoordinator`'s
> data legs or reverse hook, the listener bind, `TcpRedirectAcceptor`'s peer validation, or the
> deferred-injection lane predicate. Part of the TCP local-redirect family; the hub
> [tcp-local-redirect.md](./tcp-local-redirect.md) has the pipeline overview, the cross-cutting
> invariants and the topic map. NDISAPI transport is [windows-ndisapi.md](./windows-ndisapi.md)'s; the lane
> mechanics themselves are [ndis-batched-send.md](./ndis-batched-send.md)'s.

## Host-originated flows: the `local_redirect` IP-swap transform

The official WinpkFilter transparent-TCP-redirect pattern (`ndisapi::local_redirector`, used by
socksify/ProxiFyre) is **not** "rewrite dst to loopback + SendToMstcp". It is a driver-side transform
whose header field names — `th_sport`, `th_dport` — come from the NDISAPI headers, not from this
repository:

1. Swap Ethernet src/dst MACs.
2. Swap IP src/dst.
3. Rewrite the destination port to the local proxy port. **The client's source port (`th_sport`) must be
   preserved** — the driver rewrites only `th_dport`. The accepted connection therefore has peer
   `server-ip:client-original-port`, which is how the per-flow mapping resolves it.
4. Recompute the IP and TCP checksums (the pseudo-header uses the swapped addresses).
5. The listener binds `0.0.0.0` or `[::]` on an ephemeral port — **never loopback** — because the
   rewritten destination is the client's own address on the proxy port.

`TcpFrameRewriter.TryRewriteForwardLeg` implements exactly this for the host shape: it rewrites the
endpoints to `src = (original server, client port)` / `dst = (client address, listener port)` and then
calls `SwapEthernetMacs`. A counter-experiment settled the shape: `dst=loopback + SendToMstcp` produced a
byte-correct frame (checksums verified) that MSTCP silently ignored, because the local-redirect contract
requires the IP-swap form (hardware-verified 2026-08-08, Win11).

## Reverse path: the hook runs before flow lookup and policy

The SYN-ACK MSTCP emits in response to the injected SYN is a reverse packet. It must be recognized and
reversed **before** flow-table lookup and policy evaluation, or it is evaluated as a new client flow and
silently passed to the wire, killing the handshake (the dominant failure mode during bring-up):

- `TcpProxyCoordinator` implements `ITcpReverseHandler` and is wired as `FlowDispatcher._reverseHandler`;
  `FlowDispatcher.TryHandleReverseAsync` consults it right after the self-traffic check. A packet whose
  endpoint pair matches a `ReverseRedirectTuple` (the full pre-rewrite wire tuple, never a listener port
  alone) is reversed (`src -> original server:port, dst -> original client:port`; the MAC swap is gated
  on the host shape, since a forwarded frame's arrival MACs are already correct) and injected toward
  MSTCP, or toward the origin adapter for a forwarded flow.
- **Warm-entry diversion precheck (X1, 2026-08-30).** `ITcpReverseHandler.WantsPacket(in CapturedFlowPacket)`
  is consulted only on the dispatcher's non-async warm entry: divert to the slow path iff
  `protocol == Tcp && src port ∈ active listener-port set` (`TcpRedirectTable.IsReverseCandidatePort`, a
  reference-count array). The count increments inside `TryClaim` under the table gate **before** the
  rewritten SYN is injected — so a SYN-ACK can never precede port visibility — and is released in the one
  `RemoveUnderGate` body, which every removal site (`TryRemove`, `RemoveExpired`) reaches only after a
  `ReferenceEquals` re-check, so it can never double-decrement. A prefilter miss is exact for the reverse
  direction (a reverse tuple's source port is always a live listener port), and listener-shaped tuples
  never resolve in any `FlowTable.TryResolve` mode, so a miss degrades to the pre-X1 slow path, never to
  wrong routing. The slow path always runs the full handler regardless of `WantsPacket`, so tombstone-window
  stragglers whose port was already decremented still reach the check.
- **Loopback filtering is not required for this path**: the hook catches the reverse packet on the normal
  capture path. Enabling the driver's loopback filter was investigated and rejected because it
  re-captured the injected SYN's own loopback reflection, which then had to be drained.

## Reverse injection direction follows the flow origin

- Host-originated flow: the reversed packet is injected toward MSTCP.
- Forwarded flow: the reversed packet goes back to the **origin adapter**, not MSTCP.

`TcpRedirectInjector.InjectAsync(frame, towardMstcp, adapterHandle, ct)` selects both the direction and
the driver flag (`PacketFlagOnReceive` toward MSTCP, `PacketFlagOnSend` toward an adapter); the
coordinator passes `association.OriginalKey.Origin == FlowOriginKind.Host`. The forwarded code path is
unit-locked, not hardware-verified: the Win11 test host has the "Microsoft Hyper-V Network Adapter"
interfaces but no vSwitch or guest VM, so guest-originated traffic needs a real guest VM for the
hardware matrix.

## Mid-flow data

After the handshake, client→listener data on the original flow is rewritten to the proxy tuple the same
way (`ReinjectExistingFlowDataAsync`: host shape swaps addresses, forwarded shape moves only the
destination) and reinjected. A flow with an active redirect association is recognized by
`TcpRedirectTable.TryResolveByOriginal`; anything else is not ours.

**Batched and in-place since 2026-09-29 (task 09-29-tcp-redirect-batched-injection).** Both data legs —
the forward leg above and the reverse leg in `HandleReverseAsync` — stage their rewritten frame and hand
it to a per-(adapter handle, target direction) lane instead of sending it immediately, so one pump
iteration pays one batched injection call per lane instead of one IOCTL per frame. When the packet was
dispatched by a capture pump and its lease never materialized, the rewrite runs **on the capture slot
itself** and the slot is queued — no pool rental, no frame copy; every other shape (a materialized lease,
or the setup worker's reconstructed packet, which carries no native slot) keeps the rented pooled stage
and takes an immediate single send. **The deferred lane carries data frames only.** Every
`ClientResetInjector` reset — the relay-end/FIN close, the capacity reset, the fragment-teardown reset and
the injection-failure reset — is an immediate single send issued during dispatch, so it can never be
overtaken by a batched data frame of the same iteration. The SYN setup injection is also an immediate
single send, but it is issued by the **background setup worker**
(`SetupPendingAsync` → `RunSetupPipelineAsync` → `SetupNewRedirectAsync` → `InjectAsync`) rather than
during a pump iteration, so no pump iteration owns it. The full lane contract, the pump-chain-only append
precondition, the cross-adapter scope gate, and the degraded-batch failure posture live in
[ndis-batched-send.md](./ndis-batched-send.md#redirect-deferred-injection-lanes). One attribution change
follows from deferral: a failed data-leg injection surfaces from the flush as a rate-limited
`tcp.redirect.deferred-failed` warn plus the same client reset and fail-closed association write, instead
of the per-packet `proxy-blocked` outcome line.

## Data-bearing SYNs (TCP Fast Open) are tolerated, never blocked (2026-09-06)

A client SYN carrying data (TFO, RFC 7413) rides the exact same redirect pipeline as a bare SYN:
`TcpFrameRewriter.IsTcpSyn` is a boolean SYN predicate (SYN set, ACK clear — no payload discrimination),
and `TcpProxyCoordinator.HandlePacketAsync` routes every SYN into `HandleSynAsync`. No extra support is
needed: the forward-leg rewrite is an RFC 1624 incremental update over addresses/ports only (payload
bytes are never touched), `TcpSequenceObservation` counts SYN data in the sequence advance
(`payloadLen + SYN + FIN`, from the IP-derived transport length), the close templates are header-only, and
the non-TFO local listener stack queues or drops the SYN data, after which the client retransmits it
post-handshake (RFC 7413 graceful degradation) — the relay sees a normal stream either way. Blocking
data-bearing SYNs (the pre-2026-09-06 payload-discriminating fast path) only blackholed TFO clients: the
executor consumed every retransmission silently and the client died at ETIMEDOUT. Locked by
`SynWithPayloadIsRedirectedLikeBareSyn` (payload survival + checksum validity) and
`RetransmittedSynWithPayloadReusesAssociation` (`ClientNextSeq == ISN + 1 + payloadLen`) in
`TcpProxyCoordinatorRewriteTests`. **F4 (2026-09-30)**: both predicates consume the `PacketLayout` the
classifier's single parse produced instead of re-parsing (`IsTcpSyn(in PacketLayout)` tests `layout.IsTcp`
and the flags byte; the advance reads `TransportOffset + 4` under a `frame.Length >= TransportOffset + 8`
bound) — the span entry points remain the oracle, and a defaulted layout is refused rather than read
(`LayoutSynTestMatchesTheSpanTest`, `DefaultedLayoutObservesNoSequence`,
`SequenceAdvanceIgnoresEthernetPadding`). The tracker half of this contract — who advances the trackers,
and what the close builder reads — is in [tcp-client-close-injection.md](./tcp-client-close-injection.md).

## Redundant accepts

A retransmitted SYN can make MSTCP open a second connection on the same listener. One logical flow owns
exactly one relay, so after the first relay is attached the accept loop drains further accepts and closes
them immediately (`DrainRedundantConnectionsAsync`) instead of starting a second relay — otherwise every
extra accept fails with `SocketException` and the log floods.

**Reference.** `TcpProxyCoordinator.cs` (`HandleSynAsync`, `HandleReverseIfApplicableAsync`),
`TcpProxyCoordinator.Injections.cs` (`HandleReverseAsync`, `ReinjectExistingFlowDataAsync`),
`TcpRedirectAcceptor.cs` (accept loop + `DrainRedundantConnectionsAsync`), `TcpRedirectListener.cs`
(`0.0.0.0` / `[::]` bind), `FlowDispatcher.cs` (`_reverseHandler`).

## Forwarded flows use the DNAT-to-local transform (fixed 2026-08-14)

The IP-swap transform is valid only for **host-originated** flows. Applied to a forwarded SYN it produces
`dst = client-ip:listener-port`, which is not a local address: MSTCP routes the frame back out to the
client, the listener never sees a SYN, and the flow hangs (observed on the 192.168.77.x gateway —
`tcp.relay.started` stayed 0 while mangled frames were passed back to the client). Forwarded flows
therefore use a different shape, selected per association by `TcpRedirectAssociation.ForwardLocalAddress`:

- **Forward leg** (SYN and mid-flow data, `TryRewriteForwardLeg`): `src` stays client:client-port; only
  `dst` moves to (adapter-local address L, listener port). L is resolved per origin adapter through
  `IAdapterLocalAddressProvider` (wired as `WindowsAdapterLocalAddressProvider`): IPv4 prefers a
  same-subnet address, IPv6 skips link-local and prefers a `/64` prefix match. **Never 127.0.0.1** — a
  reverse reply from a loopback destination would carry a martian source and can be dropped by the stack
  before the capture layer can rewrite it. No L candidate → fail closed (`tcp.redirect.rejected
  reason=localAddress`).
- **No MAC swap on the forward leg**: the arrival frame's dst MAC already addresses this host. The MAC
  swap is gated on the host shape everywhere.
- **Table endpoints follow the shape**: `ReverseSource = (L, listener-port)`,
  `AcceptedPeerEndpoint = ReverseDestination = (client, client-port)`, so the accept-loop peer validation
  and `TryResolveByReverse` see the real client tuple.
- **Reverse leg is unchanged textually** (`src -> original server:port, dst -> original client:port`) and
  still injects to the origin adapter; the origin-shaped endpoints make the same rewrite call correct for
  both shapes.
- The wildcard self-traffic registration `(Tcp, 0.0.0.0:P, 0.0.0.0:P)` cannot match the forwarded SYN-ACK
  `(L:P -> client:port)`, because the remote leg must equal the observed remote exactly — the reverse hook
  still owns that packet.
- Locked by `ForwardedFlowSynRewritesTowardAdapterLocalListener`,
  `ForwardedFlowWithoutLocalAddressFailsClosed`, `ForwardedFlowAcceptsClientTuplePeer`, and the rewritten
  `ForwardedFlowReverseInjectsTowardOriginAdapter`.

---

## Cold-edge addresses are cached once

The forwarded leg must never convert an address per packet.

- `TcpRedirectAssociation.ForwardLocalAddress` is `IPAddressValue?` (`TcpRedirectTable.cs`); the only
  `From(IPAddress)` conversion runs at `TcpRedirectSetup.ResolveForwardLocalAddress` (association
  creation, once per flow), and `TcpFrameRewriter.TryRewriteForwardLeg` consumes the stored value with
  zero conversions per packet. Forwarded-shape rewrite cost is O(frame), not O(conversions): before
  C2 the per-packet conversion made forwarded DNAT 4.8× slower than host shape (2,887.9 ns vs
  616.7 ns @1400 B), and storing the raw value at creation closed the gap (626 ns ≈ host shape). Any
  per-packet stage that needs an address from a cold-edge object must cache `IPAddressValue` at the
  cold edge.
- `TcpFrameRewriter.SwapEthernetMacs` swaps via byte-index pairs (`(frame[i], frame[i + 6])`),
  constructively allocation-free — do not reintroduce a heap temp (`new byte[6]` measured 0 B only
  under JIT escape analysis; the indexed swap is a source-level guarantee).
- An association created without a local address candidate gets `ForwardLocalAddress = null` and the
  forwarded leg fails closed as `Blocked`.
- These rules are the redirect transform's contract and belong to
  [tcp-redirect-transform.md](./tcp-redirect-transform.md); they move there in the integration pass.

---

## Accept-loop peer identity excludes the IPv6 zone (fixed 2026-10-04)

- `TcpRedirectAcceptor.TryEstablishRelayAsync` validates its peer with
  `accepted.RemoteEndPoint.MatchesPeerIgnoringScope(session.Association.AcceptedPeerEndpoint)`: port,
  address family and address bits, with the scope id excluded (`Endpoint.MatchesPeerIgnoringScope`,
  `src/WinForward.Core/Domain.cs`). `Endpoint.Equals` and `IPAddressValue.Equals` keep the scope, so they
  can never match a captured endpoint against an OS-reported one.
- **Why they can never be equal**: an IPv6 header has no zone, so a captured endpoint's scope is always 0,
  while Windows reports the interface index for a link-local peer of an accepted connection (the field log
  that found this showed `expected=[fe80::215:5dff:fe03:728b]:52840`,
  `actual=[fe80::215:5dff:fe03:728b%26]:52840`). Before the fix that single-field mismatch fired
  `tcp.redirect.unrelatedPeer`, disposed the accepted connection, and left the flow with no relay — an
  instant close/reset for the app, since the client's handshake had already completed through the reverse
  hook. Both shapes were affected: host (`AcceptedPeerEndpoint` = the original destination address plus
  the client's source port, so a link-local destination) and forwarded (the client's own tuple, so a
  link-local client).
- **Deliberately not relaxed elsewhere**: the reverse index and tombstone keys stay scope-sensitive (both
  sides are wire-derived there), and link-local process attribution still fails closed across interfaces.
  Do not answer this by making the owner-table comparison scope-insensitive; it waits on the adapter
  stable-ID → IPv6 interface-index/scope projection the 2026-08-10 Windows audit records as its
  prerequisite.
- **Known protocol ceiling, not fixed here**: a link-local *destination* still cannot be relayed — the
  SOCKS5 CONNECT request carries address bytes only (RFC 1928 has no zone field), so the upstream server
  receives `fe80::…` with no interface to dial on. The supported posture is to not intercept on-link
  traffic: `"RemoteCidr": ["fe80::/10"], "Action": "pass"` (prefix matching ignores the scope) leaves
  those flows direct. Config keys are PascalCase.
- Locked by `EndpointAndPolicyTests.EndpointPeerIdentityIgnoresTheIPv6ScopeWhileEqualityKeepsIt` (identity
  matches across scopes; port, address bits and family still reject; `==` stays scope-sensitive) and
  `TcpRedirectAcceptorTests.LinkLocalPeerWhoseZoneOnlyTheSocketKnowsStillEstablishesTheRelay` (a `%26`
  peer of a scope-zero association establishes the relay and emits no `tcp.redirect.unrelatedPeer`; red
  against the pre-fix `!=` comparison).
