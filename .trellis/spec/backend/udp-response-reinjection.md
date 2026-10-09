# UDP Response Reinjection

> How a relayed UDP reply is validated, owned, rebuilt into an Ethernet frame, and injected toward
> the right adapter — or dropped fail-closed; and what the foreign-source counter observes. Read it
> before touching response building, adapter targeting, client-MAC handling, or reply
> observability. Family hub: [udp-relay.md](./udp-relay.md).

---

## What arrives, and what is rebuilt

- The session's receive loop hands a decoded reply to
  `IUdpResponseSink.InjectAsync(FlowKey originalFlow, Endpoint remoteSource,
  ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken)`
  (`UdpProxy/UdpResponseReinjector.cs`) — the client MAC is an inline six-byte value, not a
  `byte[]`.
- `UdpResponseReinjector` rebuilds a complete Ethernet II + IPv4/IPv6 + UDP frame **in place in a
  pooled native buffer**: the real server (`remoteSource`) is the source, `originalFlow.Local` the
  destination. The UDP checksum field is zeroed and filled by `PacketChecksums.WriteUdpChecksum`
  (0 → `0xFFFF` per RFC 768). No managed `byte[]` per response on the steady path.
- The injected frame's flags follow the delivery direction: toward MSTCP it is a simulated receive
  (`NdisApiAbi.PacketFlagOnReceive`), toward an adapter it is `NdisApiAbi.PacketFlagOnSend`. A
  forwarded (Hyper-V) response injected with the receive flag cannot reach the VM.
- A frame that cannot be built — MAC length, family mismatch, the frame cap, a short destination —
  drops the reply fail-closed (`udp.response.dropped reason=frameBuild`) and never throws into the
  receive loop. Failures that repeat at query rate are rate-limited: 5 s for the client-MAC and
  frame-build warns, 30 s for the structured `udp.reinject.drop` warn (which carries the
  `missingOriginAdapter` / `missingHostTarget` reasons) and for `udp.reinject.unresolved`, each
  beside a counter.

## Forwarded flow destination MAC

Fixed 2026-08-15: a forwarded (Hyper-V/VM) response must **not** use the adapter's own MAC as
destination — the vSwitch would deliver it to the host, never the VM.

- Destination MAC = the client (VM) MAC, source MAC = the origin adapter's MAC, for forwarded flows
  only.
- The client MAC is read from bytes 6..11 of **each** proxied datagram by `NdisPacketActionExecutor`
  (after `IPUdpPacket.TryParseSpan` succeeds) and passed inline with the send; the session records
  the **first** one, in a private get-only `UdpProxySession.ClientMac`. The coordinator consumes it
  only on the admission path, so a later datagram cannot rewrite it.
- Missing or invalid client MAC on a forwarded flow → drop fail-closed with a rate-limited warn
  (`LogMissingClientMac`), the same posture as a missing origin adapter. Locked by
  `ForwardedFlowResponseInjectsTowardOriginAdapter` (destination == client MAC),
  `ForwardedFlowResponseWithoutValidClientMacIsDroppedFailClosed`, and
  `RelayResponseCarriesRecordedClientMac`.
- TCP reverse legs are unaffected: they reuse captured frames whose MACs are already correct.

## Host-flow response adapter binding

Fixed 2026-08-27, hardware-verified: `SendPacketToMstcp` is **adapter-bound**. A host UDP response
must resolve the flow's origin adapter through the capture-scope `UdpAdapterTarget` map and use that
target's enumeration handle and MAC for both Ethernet header slots.

- **F4 (2026-09-30):** the lookup is `IUdpAdapterTargetSource.Resolve(ushort slot)` over a
  slot-indexed array rebuilt on each capture refresh from the same `scope` the slots were interned
  from (`UdpAdapterTargetSource`, `DurableCaptureBundle.UpdateUdpTargets`) — one array index, no
  string hash and no dictionary probe on the per-response path. `AdapterIds` resolves each non-null
  entry's `StableId` through the slot table for the diagnostic log. The slot↔target map and the flow
  key share one identity source, so they cannot disagree about which adapter a flow came from.
- The startup-selected `scope[0]` target is only a compatibility fallback for a host flow whose
  origin adapter is absent from the current map; that fallback emits a rate-limited warning and
  counts `udpOriginUnresolved`. Choosing `scope[0]` for every host response attaches the indication
  to an unrelated interface and can fail Windows adapter/source-address validation.
- Forwarded behavior is unchanged: an unresolved origin drops fail-closed, a resolved origin injects
  toward the adapter with the recorded client MAC as destination.

| Flow/adapter condition | Required response action |
|---|---|
| Host + origin slot resolves | `SendToMstcp(origin.Handle)`; src/dst MAC = `origin.Mac` |
| Host + origin slot missing/unresolved | warn (rate-limited), then `SendToMstcp(fallback.Handle)` |
| Forwarded + origin resolves + valid client MAC | `SendToAdapter(origin.Handle)`; dst MAC = client MAC |
| Forwarded + origin unresolved or client MAC invalid | drop fail-closed |

Required tests: `HostFlowResponseInjectsTowardItsOriginAdapter` asserts the handle, the ON_RECEIVE
flag and both MAC slots; `HostFlowWithUnresolvedOriginAdapterUsesFallbackAndWarns` asserts fallback
injection plus the warning; the forwarded response tests keep asserting `SendToAdapter`, ON_SEND and
the client destination MAC. For the re-keyed map F4 adds
`ScopeHeadBecomesHostFallbackAndEachAdapterResolves` (a slot resolves to the same target the scope's
adapter maps to), `UdpAdapterTargetSourceTests.AdapterIdsResolveThroughTheSlotTable`, and
`ResolveReadsTheLatestSnapshotAfterUpdateWithoutReconstruction`; the three response-target tests are
unchanged.

## Reply ownership: the foreign-source counter

Wired 2026-10-05. Until then the decoded reply source was handed straight to the reinjector as the
injected frame's source without ever being compared to `Flow.Remote`, so a reply the server
delivered to the wrong flow was injected toward that wrong flow's client as a legitimate frame and
nothing recorded it.

- **Count, then deliver.** One peer comparison against `Flow.Remote`
  (`UdpProxySession.TryGetReceiveSource`); on a mismatch `RuntimeCounters.UdpResponseSourceMismatch`
  is incremented and the warn `udp.response.foreign_source` may be emitted, and the method still
  returns `true` with that same decoded source. `InjectResponseAsync` and `UdpResponseReinjector`
  are untouched. The observation is **non-dispositional by contract**: `RuntimeCounters` counters
  must never influence packet disposition, fail-closed, relay, or shutdown decisions.
- **The comparison is `MatchesPeerIgnoringScope`, not `Endpoint` equality.** The decoded source's
  IPv6 scope comes from the relay socket's bind address while the flow's destination scope comes
  from the captured packet, so the two legitimately differ for one link-local peer; `Endpoint`
  equality compares `IPAddressValue.ScopeId` and would count that as a foreign source. A real
  foreign source differs in address bits and still counts — the scope tolerance must not swallow it.
  It is the same comparison `TcpRedirectAcceptor` makes when it matches an accepted socket endpoint
  against a wire-decoded one.
- **A mismatch is not an error.** Relay-source validation is deliberately port + address family
  rather than exact address (see the hub's "Accepting a reply"), because RFC 1928 does not pin the
  reply source, multi-homed/anycast relays answer from another address, and TFTP-style exchanges
  continue from a new endpoint by design. The count is the evidence a drop-on-mismatch decision
  would need; dropping is a separate, riskier decision this observation does not take.
- **The counter is cumulative; only the warn is windowed** — a 5 s per-session CAS-on-ticks window
  (`s_rateLimitedLogInterval`), the same shape as the skip summary. Every reply is counted and
  exactly one warn is emitted per session per window, so N sessions answering from an unexpected
  endpoint produce up to N warns: the volume **is** the signal (which flows see which peer), and the
  cumulative counter, not the line count, is the measurement. A coarser global throttle would lose
  that per-session attribution. Do not route this through `RecordSkippedDatagram` or add a
  `UdpTransportSkipReason` member: that path means "not delivered".
- **Visibility has one trap**: there is no registration step — `RuntimeCounters.Snapshot()` and the
  heartbeat pick up any key that has been incremented — and the 60 s `runner.heartbeat` line reports
  the key as `udpResponseSourceMismatch=<delta>` only when the delta is non-zero, so an absent field
  means "no mismatch in this window", not "never a mismatch".
- **Cost**: one struct comparison (port, family and raw address bits, no allocation, no boxing) plus
  one unconditional interlocked increment; a suppressed window costs one clock read and a compare. A
  domain-typed response (`DestinationAddress == null`) keeps its existing skip path and never
  reaches the comparison.
- **The blind spot is structural, not a gap.** Two flows to the same destination are
  address-indistinguishable: a cross-delivered reply carries the source the receiving flow also
  expects, so it never reaches the comparison. That was the undetectable misdelivery shape of a
  shared association, and it is why sharing was removed — a flow's relay socket belongs to that flow
  alone, so a reply can only arrive on the socket that sent the request. The counter therefore
  observes a server answering from an unexpected endpoint, not proxy misdelivery.
- Tests: `UdpResponseSourceMismatchTests` (counted and delivered with the declared source; matching
  reply does not move the counter; same address bits with a different IPv6 scope does not, and
  different address bits with the same scope does; a burst inside one window counts every reply and
  warns once, the next window warns again; the two-flows-one-destination blind-spot pin).
  `RuntimeCountersTests.SharedInstanceExposesTheStandardVocabulary` pins the key string, and
  `HotPathAllocationGateTests` stays green.
