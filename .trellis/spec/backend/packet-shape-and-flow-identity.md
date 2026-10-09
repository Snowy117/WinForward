# Packet Shape and Flow Identity: structs, the one parse, and the packed key

> The per-packet value types: what a packet carries, what the key is made of, and the proofs that
> keep both from being re-derived per stage. Part of the [hot-path family](./hot-path.md); read it
> when you touch `FlowKey`/`FlowHash`, `PacketLayout`, `TryParse`, or the layout-driven rewriter.

The sizes of these types are the hub's: [hot-path.md](./hot-path.md), "Packets are structs with
pinned shapes".

## Flow keys hash flat

- `FlowKey`/`TransportTuple` store packed address halves, ports and scopes, and hash through one
  shared `HashCode.Combine` expression — `FlowHash.CombinePacked`; `Combine` and `CombineCanonical`
  delegate to it, so the packed and the materialized form are the same eight values by construction.
- `Equals` orders the cheapest discriminators first and compares **every** field, including
  `OriginAdapterSlot`, `OriginAdapterGeneration` and both `ScopeId`s.
- **F4 (2026-09-30):** `FlowKey` has no reference-typed field, so no string comparison can exist on
  any lookup path, and the struct is exactly one 64-byte cache line.
  `FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields` asserts the size bound and the absence of
  reference-typed fields in one fact; `FlowKeyShapeTests.StructSizesForDiagnostics` asserts the exact
  64; `FlowKeyPackedRoundTripsEndpoints`, `ReverseSwapsEndpointsAndScopes` and
  `PackedAndMaterializedHashesAgree` pin the round-trip and the hash agreement.
- The canonical warm-slot function reads the same packed hash (`FlowHash.CombineCanonicalPacked` over
  the key's packed halves) and corroborates by tuple equality — see
  [warm-path-dispatch.md](./warm-path-dispatch.md). `FlowKey.IsReverseOf` serves the dispatcher's
  endpoint-swap test without materializing four `Endpoint`s.

## A frame is parsed once; the packet carries the parse's proofs

- `IPHeaderLength`, `TransportLength` and the TCP flags byte are produced by the capture processor's
  one `IPTcpUdpPacket.TryParse` (the view `PacketFlowClassifier` then consumes) and derived into a
  16-byte `PacketLayout` (`WinForward.Protocols`) carried on `CapturedFlowPacket`.
- `TcpFrameRewriter.IsTcpSyn`, both `TcpSequenceObservation` reads and the layout overload of
  `PacketChecksums.TryRewriteTcpEndpoints` consume it and never re-walk the headers. The span-taking
  entry points stay as the **independent oracle**.
- The layout carries the address family (the rewriter's write geometry depends on it) and keeps only
  what the parse cannot prove: `IsTcp`, `frame.Length >= TransportEnd`, and the argument-address
  family equality.
- Facts: `LayoutSynTestMatchesTheSpanTest`, `RedirectedForwardPacketWalksHeadersExactlyOnce`,
  `RedirectedReversePacketWalksHeadersExactlyOnce`,
  `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects`, `SequenceAdvanceIgnoresEthernetPadding`,
  `ExtensionHeaderFramesProduceTheSameAdvance`, `PacketLayoutFitsSixteenBytes`.

## A defaulted layout is refused, not applied

- `PacketTransport.Tcp` is `0`, so `default(PacketLayout)` — what a hand-built `CapturedFlowPacket`
  without a layout carries — reads as a valid TCP layout whose transport header sits at the IP
  header's offset with an IPv4 family. The layout therefore carries an explicit validity stamp that
  only `PacketLayout.From(in PacketView)` writes.
- Every consumer gates on `IsTcp`/`IsValid`, and a defaulted layout is refused **byte-identically**
  (fail-closed) instead of being rewritten at wrong offsets.
- A packet without a layout can also never be routed into SYN handling, because
  `TcpProxyCoordinator.HandlePacketAsync` reads the layout-driven `IsTcpSyn`; it takes the data path,
  whose rewrite refuses and blocks the association.
- `CapturePacketProcessor.ProcessAsync` stamps every flow packet it dispatches. Facts:
  `OnlyAParsedFrameYieldsAValidLayout`, `DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten`,
  `DefaultedLayoutObservesNoSequence`, `RedirectLegRefusesADefaultedLayoutByteIdentically`,
  `EveryDispatchedFlowPacketCarriesAParsedLayout`.
- The non-flow arm carries `default` by design and no consumer on that path reads a layout.

## Accepted deltas and recorded residuals

Recorded rather than smoothed over; each is a constraint on future work, not a defect to re-open
blind.

- `FlowContext`/`CapturedFlowPacket` are record structs whose generated equality now compares metadata
  references instead of four strings. Nothing in `src/` compares either by equality, and the metadata
  shapes are records, so equal triples still compare equal.
- `FlowContext.RemotePort` is the key's remote port, so the three construction sites that used to pass
  it explicitly can no longer disagree with the key.
- `FlowKey.Create(…, AdapterSlotTable, …)` on an unregistered adapter yields `NoSlot`; only cold edges
  and tests reach that. Capture-scope adapters are interned at generation build and refused there —
  the slot table and its refusal rule are
  [windows-ndisapi.md](./windows-ndisapi.md), "Adapter Identity Contract".
- The Windows composition-level slot refusal has no Linux harness — only the table-level refusal is
  pinned (`AnAdapterThatCannotBeInternedIsRefusedNotAliased`), while the factory's
  scope-exclusion/log loop is untested.
- `ProcessMetadata` is allocated once per attributed claim and no 0 B gate drives that path.
- The design's by-slot `AdapterSlotTable.Observe` seam was removed as unused; the refresh runs through
  `TryIntern`.
- `TransportTuple`'s per-field equality has no direct fact (it is a private nested type), so a dropped
  field would only be caught through the warm corroboration it feeds — see
  [warm-path-dispatch.md](./warm-path-dispatch.md).
- `PacketView`'s constructor is public, so `PacketLayout.From` stamps a hand-built view too: the
  validity stamp excludes `default`, it does not authenticate a parse.
- The companion benchmark series are **shape witnesses**; their exact siblings are the gate classes,
  green in every run.
