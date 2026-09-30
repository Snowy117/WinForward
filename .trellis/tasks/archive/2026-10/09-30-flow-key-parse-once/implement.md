# Implementation plan — F4 flow keys, parse-once, atomic trackers

Task `09-30-flow-key-parse-once`. Design: `design.md`. Code census (anchors, sizes, measured attribution):
`research/implementation-notes.md`. Predecessors whose contracts are the regression surface:
`.trellis/tasks/archive/2026-09/09-30-warm-path-lock-chain/` (F2) and
`.../09-30-expiry-sweep-bounded-pause/` (F3).

Baseline for every count in this plan: tree `ca8d999`, recorded in Step 0. Every numbered step is
independently revertable; rollback points are named `A`–`G`. **No step may be committed red**: each step
carries its own proving facts.

Workflow rules that apply throughout: Release gates only (`dotnet build -c Release`, `dotnet test -c
Release` — the 0 B gates are exact only in Release), the pre-commit `dotnet format` + `jb inspectcode` gates
(`AGENTS.md`), file effective-line limit ≤400 (`directory-structure.md:57`), and `prd.md` is **not edited** —
if a requirement is infeasible it is reported in the session record with evidence, never weakened silently.

---

## Step 0 — Preflight

- [ ] Confirm the tree is clean apart from this task's `.trellis/tasks/09-30-flow-key-parse-once/`.
- [ ] `git rev-parse --short HEAD` and `git write-tree` into the artifact README's header.
- [ ] Record the suite baseline: `dotnet test WinForward.slnx -c Release 2>&1 | tail -5` — the padded
      summary line, plus the per-class totals for **every class this task grows**, not only the four
      allocation classes: `HotPathAllocationGateTests`, `WarmPathGateTests`, `SweepAllocationGateTests`,
      `TcpRedirectWarmPathGateTests`, `UdpWarmPathGateTests`, `SelfTrafficWarmPathGateTests`, and the new
      `FlowKeyShapeTests`. `hot-path.md:1236-1240`'s proof loop asserts a totals string and F2 recorded
      `12 + 4 + 2 + 7 = 25` for the four structural classes
      (`benchmarks/results/2026-09-30-warm-path-lock-chain/warm-path-gate-counts.txt`), so a grown class with
      a stale string fails a later proof loop for no reason. A class that does not match its recorded total
      means a vacuous filter — stop and fix the filter.
- [ ] Record the eight red-before struct sizes and counts that the exact criteria move (they are asserted
      by Step 1's test, so this bullet is the expectation, not a second measurement):
      `FlowKey` 128, `FlowContext` 168, `CapturedFlowPacket` 224, `FlowStateView` 160, `PacketView` 80,
      `Endpoint` 48, header walks per redirected packet 4/4, sequence-gate entries per packet 2.
- [ ] **No operator decision is outstanding.** The amended PRD settles everything this plan needs: the
      generation **stays a key field** with integer equality (Notes), AC 4 is a recorded reading whose
      discharge is per-leg no-regression, and the IPv6 checksum delta is an optional commit (Step 6) adopted
      only on a real reading.

## Step 1 — Instrumentation first: the walk probes, the gate probe, the size test, the before-artifact  [rollback point A]

Nothing in this step changes behaviour. It exists so every later "exact" claim has a recorded red-before.

- [ ] `src/WinForward.Protocols/PacketPathProbe.cs` (new): `ParseWalk`, `RevalidateWalk` and `ViewRewrite`
      as **[ThreadStatic]** `Action?` properties (the `t_` marker + localized IDE1006 pragma, as
      `PacketLease.t_recycleCache` does), each read once into a local behind a plain null check at exactly
      one site (`IPTcpUdpPacket.TryParse` entry; the span-taking `PacketChecksums.TryRewriteTcpEndpoints`
      entry; the layout-taking overload entry once Step 3 lands). Mirror F2's `FlowTable.NoteGateHold`
      (`FlowTable.cs:136`) — production carries a null check and nothing else.
      **They must be thread-scoped**: this test project has no `xunit.runner.json` and no
      `[CollectionBehavior]` (verified), so xUnit runs collections in parallel and a process-wide counter
      would be inflated by sibling parser tests. Serialising the assembly is the fallback only if a future
      async drive migrates threads mid-packet; the fact's thread assertion makes that loud.
- [ ] `TcpRedirectAssociation.SequenceGateEntryProbe` (`internal Action?`, diagnostics only) fired
      immediately after every `lock (_sequenceGate)` acquisition; `SequenceGateEntryCountForDiagnostics`.
      It is **deleted together with the gate in Step 2**; the artifact keeps the red text and the command.
- [ ] `tests/WinForward.Core.Tests/FlowKeyShapeTests.cs` (new): `StructSizesForDiagnostics` printing and
      asserting the eight sizes above — at this step the assertions are the *current* values, so the file
      records the before-shape and fails loudly the moment a later step changes a size without updating it.
- [ ] Facts that count the red-before walks and gate entries:
      `RedirectedForwardPacketWalksHeadersExactlyOnce` (asserting **4** at this step; flipped to 1 in
      Step 3), `RedirectedReversePacketWalksHeadersExactlyOnce` (4 → 1), and
      `RedirectedForwardPacketTakesTheSequenceGateTwice` (2 → 0 in Step 2). Each asserts the per-run total
      of the *other* filter classes it rides with, so a vacuous drive cannot pass.
- [ ] **Drive them from `CapturePacketProcessor.ProcessAsync`, not from the coordinator.** The processor's
      `TryParse` is the first of today's four walks, so a harness that starts at the coordinator cannot
      record the red: `HotPathAllocationGateTests` drives `HandlePacketAsync`/`HandleReverseAsync` directly
      (`:41`, `:104`) and `TcpRedirectInjectionBatchingTests` drives `HandlePacketAsync`. Compose
      `CapturePacketProcessor.ProcessAsync` → `FlowDispatcher` (a Proxy policy resolving to a known server)
      → the real `TcpProxyCoordinator` wired as **both** `reverseHandler` and executor, over a pump-owned
      `NdisPacketBuffer` — the `ProcessAsync` front end of `FlowDispatcherExecutorTests.cs:296` and
      `BatchedPassReinjectionE2eTests.cs:35-43`, plus the coordinator fixture of
      `HotPathAllocationGateTests:41` (forward mid-flow, claimed association) and `:104` (reverse SYN-ACK).
      Assert `ParseWalk`/`RevalidateWalk` **on the driving thread** and that
      `Environment.CurrentManagedThreadId` is unchanged across the drive. The per-leg before-counts quoted
      from that harness are **forward 4, reverse 4**.
- [ ] `WorkCountsOnTheDrivingThreadOnly`: a sibling thread parses an unrelated frame while the probe is
      attached; the driving thread's counts are unaffected (red against a process-wide probe).
- [ ] Before-artifact at `benchmarks/results/2026-09-30-flow-key-parse-once/`:
      `tcp-redirect-data-path-before.{md,csv}`, `flow-table-production-shape-before.{md,csv}`,
      `parser-before.{md,csv}`, `dispatcher-before.{md,csv}`, `struct-sizes-before.txt`,
      `walk-and-gate-counts.txt` (every count with its command and its red text), `README.md` (host, tree,
      commands, the per-class totals, the "before" column of every table). **The before-numbers must be
      re-taken on this tree**: F2 changed the warm path after the `2026-09-29-benchmark-coverage` artifacts
      were written.
- [ ] Suite + gates green with the probes attached (they are inert when unattached).

**Rollback A**: delete the new test file, the probe class and the probe fields; the artifact stays.

## Step 2 — Atomic sequence trackers  [rollback point B]

- [ ] `TcpRedirectAssociation`: `_clientNextSeq`/`_serverNextSeq` become `long` with a
      `private const long Unobserved = -1`; `_sequenceGate` and its `System.Threading.Lock` are deleted;
      `ObserveClientSequence`/`ObserveServerSequence` become the CAS-max loop of `design.md` §5.2 with the
      unchanged `IsSequenceAhead` predicate; `ClientNextSeq`/`ServerNextSeq` stay `public uint?` and read
      with `Volatile.Read` (`< 0` ⇒ `null`).
- [ ] Delete `SequenceGateEntryProbe`/`SequenceGateEntryCountForDiagnostics` (Step 1's temporary
      instrument) and flip `RedirectedForwardPacketTakesTheSequenceGateTwice` to
      `RedirectPacketTakesZeroSequenceGateEntries` asserting 0, with Step 1's recorded 2 quoted in the
      artifact.
- [ ] New facts: `TcpRedirectAssociationHoldsNoLockField` (reflection: no reference-typed instance field),
      `ConcurrentSequenceObservationsKeepTheLargerValue` (two barrier-released threads, wrap-aware maximum,
      reader never observes a decrease — genuinely overlapping tasks per `quality-guidelines.md:41`),
      `UnobservedTrackerReadsNullAndObservedZeroReadsZero` (the `-1` sentinel vs sequence 0 and vs
      `0xFFFFFFFF`).
- [ ] Confirm the RST paths untouched and green: `TcpResetBuilderTests`, `TcpRelayEndResetTests`,
      `TcpProxyCoordinatorRewriteTests` (`ClientNextSeq == ISN + 1 + payloadLen` at `:402`),
      `TcpFragmentHandlingTests`.
- [ ] `tcp-local-redirect.md:87` is restated in Step 8 ("wrap-aware advance-only" → the CAS-max form).

**Rollback B**: restore the `Lock`, the two `uint?` fields and the two lock bodies; delete the probe
deletion by restoring Step 1's probe.

## Step 3 — Parse once: `PacketView` → `PacketLayout`  [rollback point C]

- [ ] `IPTcpUdpPacket`: `PacketView` gains `int TransportLength` and `byte TcpFlags`; the IPv4/IPv6 arms pass
      the `availableLength` they already compute; `TryParseTransport` reads the flags byte once for TCP. No
      validation changes — the rejection matrix is byte-for-byte the same.
- [ ] `src/WinForward.Protocols/PacketLayout.cs` (new; **Protocols, not Core** — `PacketLayout.From(in
      PacketView)` needs `PacketView`, and `WinForward.Core` has no project references. `WinForward.Runtime`
      references both, and `FlowDispatcher.cs:6`/`CapturePacketProcessor.cs:5` already import
      `WinForward.Protocols`, so `CapturedFlowPacket` can carry it): the 16-byte record struct of
      `design.md` §4.1 — `Transport`, `TcpFlags`, **`Family`**, `IPHeaderLength`, `TransportHeaderLength`,
      `TransportLength` — with `TransportOffset`, `TransportEnd`, `From(in PacketView)`. The `Family` byte is
      **not optional**: without it the checksum overload cannot choose IPv4 vs IPv6 write geometry, and
      writing 16 bytes into an IPv4 frame would succeed. Compare `Transport` against
      `(byte)PacketTransport.Tcp` (which is **0**, not 1) wherever it is tested.
- [ ] `CapturedFlowPacket` gains `PacketLayout Layout = default`; `CapturePacketProcessor.ProcessAsync`
      computes `PacketLayout.From(view)` on the success arm only (non-flow packets keep `default`).
- [ ] Consumers switch to the layout, keeping the span-taking overloads as the independent/oracle path:
      `TcpFrameRewriter.IsTcpSyn(in PacketLayout)`, `TcpSequenceObservation.TryReadTcpSequenceAdvance(frame,
      layout, out next)` + `TrackClientSequence`/`TrackServerSequence`/`RecordServerSynAck`/`RecordClientSyn`
      overloads, and `PacketChecksums.TryRewriteTcpEndpoints(Span<byte>, in PacketLayout, IPAddressValue,
      ushort, IPAddressValue, ushort)` which **skips** every check the parse already made (version, IHL,
      protocol byte, fragment bits, total length, the IPv6 extension chain, `tcpLength >= 20`, the
      `dataOffset` bound) and keeps only `layout.Transport == (byte)PacketTransport.Tcp`,
      `frame.Length >= layout.TransportEnd`, and the **family** check (the argument addresses' family must
      equal `layout.Family` — they come from the association, and their family selects the write geometry).
      The 4-byte sequence reads are guarded by `frame.Length >= layout.TransportOffset + 8` (the whole read,
      not just its start), so a short frame rejects instead of throwing out of `Slice`.
- [ ] Call sites updated: `TcpProxyCoordinator.cs:398` (`IsTcpSyn`), `TcpProxyCoordinator.Injections.cs:256`,
      `:321`, `:322`, `:323`, `TcpRedirectSetup.cs:129`, `:131`,
      `TcpFrameRewriter.TryRewriteForwardLeg` (called from `Injections.cs:259` and `TcpRedirectSetup.cs:131`).
      `TcpFrameRewriter.TryRewriteForwardLeg` gains a layout-taking overload; the span-taking one stays for
      `FrameRewriterBenchmarks`/`BenchmarkFrameBuilderTests`.
- [ ] Facts: `RedirectedForwardPacketWalksHeadersExactlyOnce` and
      `RedirectedReversePacketWalksHeadersExactlyOnce` flip to `ParseWalk == N`, `RevalidateWalk == 0`,
      `ViewRewrite == N`; `LayoutSynTestMatchesTheSpanTest` (TCP **and** UDP — the literal-`1` regression);
      `PacketLayoutFitsSixteenBytes`; `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` (every truncation
      of an IPv4/IPv6 TCP frame, plus a cross-family call that must return `false` with the span unmutated;
      identical `bool` results; byte-identical output for accepted frames, asserted via the explicit
      mutable-offset set and the full-recompute oracle); `SequenceAdvanceIgnoresEthernetPadding`
      (a padded 1514-byte frame with a shorter IP total length yields the same advance through both paths);
      `ExtensionHeaderFramesProduceTheSameAdvance` (IPv6 with a hop-by-hop and a destination-options header).
- [ ] Re-run `tcp-redirect-data-path` and `parser`; record `*-after-step3.{md,csv}` and note in the artifact
      that the key is untouched by this step (so a move in those rows is page/parse-shaped, not key-shaped).

**Rollback C**: revert the overloads and the `Layout` field; the span entry points never changed shape, so a
revert is behaviour-zero.

## Step 4 — Interned adapter slot table, and `FlowKey` stores the slot  [rollback point D]

- [ ] `src/WinForward.Core/AdapterSlotTable.cs` (new): `NoSlot = 0`, monotone never-reused slots,
      `TryIntern(string stableId, long generation, string? friendlyName, out ushort slot)` (idempotent per
      `StableId`; `false` on exhaustion), `Observe` (publishes a **replacement** `AdapterMetadata` instance —
      the metadata is immutable and get-only), `TryResolve`, `CountForDiagnostics`,
      `ExhaustedForDiagnostics`, and the per-slot `AdapterMetadata`. Internals: a `Dictionary<string, ushort>`
      under a lock for `TryIntern`/`Observe` (cold, once per adapter per enumeration) and a grow-only
      `AdapterMetadata?[]` read lock-free by `TryResolve` (`Volatile.Read` of the array reference + an
      element read; publication ordered by the cold path's `Volatile.Write`). Nothing in `src/` compares
      `AdapterMetadata` by value; the record's equality exists for tests only.
- [ ] **The generation stays.** `FlowKey` keeps `OriginAdapterGeneration` and its `Equals` compare; only
      `string? OriginAdapterId` → `ushort OriginAdapterSlot` changes. `FlowKey.Create` overloads per
      `design.md` §3.5 (the generation is an explicit parameter on the adapter-carrying overloads); the
      4-argument overload means `NoSlot` + generation 0. The key's generation is read from the **same**
      `WindowsAdapter` instance as the slot at classification, so a key can never mix one enumeration's slot
      with another's generation.
- [ ] `PacketFlowClassifier.ClassifyFlow`/`ClassifyNonFlow` take the resolved `ushort slot` **and**
      `adapter.Generation` (and build the `AdapterMetadata` reference for the context in Step 7);
      `MultiAdapterCaptureLoop` resolves each adapter's slot when the generation is built and passes
      `(adapter, slot)` into `CapturePacketProcessor.ProcessAsync`; `DurableCaptureBundle` creates the table
      and hands it to the processor (`Program.cs:284`).
- [ ] **Exhaustion is refused, not absorbed.** If `TryIntern` fails, the generation build **excludes that
      adapter** from the capture scope and logs one rate-limited `adapter.slot-exhausted` error, so no
      captured packet ever carries `NoSlot` for a real adapter. Collapsing onto `NoSlot` is forbidden: two
      adapters' keys would compare equal whenever their 5-tuples match (the multi-VM case `OriginAdapterId`
      exists for), and the second flow would resolve the first's state — fail-open.
- [ ] Slot → string consumers rewired: `TcpRedirectSetup.ResolveForwardLocalAddress` (cold),
      `UdpAdapterTargetSource` (re-keyed by slot; `IUdpAdapterTargetSource.Resolve(ushort)`, the snapshot an
      array built in `DurableCaptureBundle.UpdateUdpTargets` (`:311-323`) from the same `scope` the slots
      came from, `AdapterIds` resolving through the slot table), `UdpResponseReinjector`'s two resolve sites
      and its logging, `NdisPacketActionExecutor.cs:121`, `Policy.RuleMatcher` (through the context).
      `TcpRedirectLogging`/`UdpProxyLogging` events that printed the adapter id resolve it from the slot.
- [ ] Test surface: one helper in `tests/WinForward.Core.Tests/TestHelpers/FlowBuilders.cs`
      (`WithAdapter(this AdapterSlotTable table, string stableId)`) and a mechanical switch of the
      `new AdapterContext("…")` sites to it. `AdapterContext` is deleted or reduced to
      `(ushort Slot, long Generation)`; **leaving a `string?` in it is forbidden** (design §3.5) and a
      repository-wide `rg 'AdapterContext'` is part of Step 8's evidence.
- [ ] Facts: `AdapterSlotTableRoundTripsStableId`, `AdapterSlotTableRefusesAnUnregisteredAdapter`,
      `SlotValuesAreNeverReused` (interleave `TryIntern`/retire), `AdapterSlotSurvivesARefresh` (same
      `StableId`, new generation ⇒ same slot, a **new** metadata instance with the updated generation),
      `TryInternIsIdempotentPerStableId`, `AnAdapterThatCannotBeInternedIsRefusedNotAliased` (with the slot
      space forced full, the composition refuses the adapter and the dispatcher never sees a `NoSlot` key for
      it), `TwoAdapterSlotsOnTheSameTupleStillAliasThroughTheTransportIndex`,
      `FlowTableTransportTupleIsUniqueAcrossOrigins` (unchanged), `UdpTargetMapResolvesBySlot` (slot → the
      same target the string map returned), `PacketParsingTests`' generation assertion restated against the
      side table.

**Rollback D**: restore `AdapterContext`/the string field and the generation; the slot table file stays
unused or is deleted with it.

## Step 5 — Pack the key to 64 B  [rollback point E]

- [ ] `FlowKey`'s storage becomes the packed field set of `design.md` §2.1 — **including
      `long _adapterGeneration`**, so the struct is `57 → 64` bytes and lands **exactly** on the contract
      with no headroom. Re-read §2.1's ceiling note before adding anything to the type; the `<= 64`
      assertion passes, but nothing else fits.
- [ ] `Local`/`Remote` become computed `Endpoint` properties; the packed primitives
      (`LocalLow`/`LocalHigh`/`LocalPort`/`LocalScopeId`/`Remote*`/`AdapterSlot`/`AdapterGeneration`) are
      `internal` to Core, and `public bool IsReverseOf(FlowKey other)` serves Runtime without a new
      `InternalsVisibleTo` edge (`WinForward.Core.csproj:8-9` friends `Core.Tests` and `Benchmarks` only —
      **do not add Runtime to it**). `FlowDispatcher.IsReverseOf` delegates to the packed method rather than
      materializing four `Endpoint`s per warm UDP proxy packet.
- [ ] `Reverse()` (and `TransportTuple.Reverse()`) become explicit private-constructor calls — a `with`
      expression has nothing to set once every field is a private readonly value; decide and record whether
      `FlowKey` stays a `record struct` or becomes `readonly struct : IEquatable<FlowKey>` (`design.md`
      §2.1's shape note).
- [ ] `FlowHash` gains `CombinePacked` and `CombineCanonicalPacked`; `Combine`/`CombineCanonical` delegate to
      them so there is **one expression** (`design.md` §2.3), and the hashed field set stays transport-only —
      **no slot, no generation**. `FlowTable.SlotOf`, `FlowKey.GetHashCode` and `TransportTuple`
      (packed: 4 ulongs + 2 scope uints + 2 ports + family + protocol = **48 B**, `Reverse()` a field swap,
      `GetHashCode` still delegating) all use the packed entry points.
- [ ] `FlowTable.TryResolveWarm`/`Matches` never materialize an `Endpoint`; `TcpRedirectTable.OriginalSlot`
      uses the packed hash.
- [ ] If `Domain.cs` would exceed 400 effective lines, split `FlowKey`+`FlowHash` into
      `src/WinForward.Core/FlowKey.cs` (same namespace) per `directory-structure.md:57-59`.
- [ ] Facts: `FlowKeyFitsOneCacheLine` (≤64, satisfied at exactly 64), `FlowKeyHasNoReferenceTypedFields`,
      **`FlowKeyPackedRoundTripsEndpoints`** (IPv4, IPv6, IPv6 + nonzero `ScopeId`:
      `key.Local == source && key.Remote == destination`, with `Address.Family`, `Address.ScopeId` and `Port`
      each equal — this is the fact that catches a self-consistent low/high swap or a misplaced scope id that
      every hash-agreement fact would pass), **`ReverseSwapsEndpointsAndScopes`**, `FlowKeyEqualityKeepsTheGeneration`
      (the retained generation compare), `PackedAndMaterializedHashesAgree`,
      `PackedAndMaterializedCanonicalHashesAgree` and `CanonicalSlotIsOrderIndependent` over the **specified
      corpus** of `design.md` §2.3 (a pair whose high-half order differs from its low-half order, the equal
      endpoint tie, both families, both orientations, a nonzero IPv6 scope),
      `TransportTupleEqualsTheKeysTransportFields`, `IsReverseOfMatchesTheEndpointComparison`,
      `OriginalKeyCacheStillValidatesTheWholeKey` (a different-slot key on the same tuple is not served from a
      populated original slot), and the eight size assertions of `FlowKeyShapeTests` updated to the
      after-values (**64** / 80 / 152 / 96 for the four key-carrying structs, §7).
- [ ] **The F2 re-proof runs here** (design §2.5): `WarmPathGateTests` in full — in particular
      `FlowTableWarmResolveAllocatesNoManagedBytes`, `WarmCacheHitServesTheValidatedView`,
      `FlowTableCollidingFlowsFallBackToTheGatedPath`, `FlowTableRemovedFlowIsNeverServedFromItsOldSlot`,
      `FlowTableTransportTupleIsUniqueAcrossOrigins`, `WarmResolveCompletesWhileFlowTableGateIsHeld` — plus
      `TcpRedirectWarmPathGateTests`, `UdpWarmPathGateTests`, `SelfTrafficWarmPathGateTests`.
- [ ] Re-run `flow-table-production-shape` and `dispatcher`; record `*-after-step5.{md,csv}` with the
      attribution note that `tcp-redirect-data-path` contains no key or dictionary probe
      (`research/implementation-notes.md` §7.2) and therefore must **not** be credited to this step. Watch
      `DispatcherBenchmarks`' proxy rows for an accidental `Endpoint` materialization (§2.4).

**Rollback E**: restore the two `Endpoint` fields (Step 4's key stays) and drop the packed twins.

## Step 6 — *(optional commit)* single-pass IPv6 address delta  [rollback point F]

This step is **not** required by the PRD and is expected to be dropped. It exists only because the row it
targets may still read slow after Step 5. Decide it on a measurement, never on the planning estimate.

- [ ] Re-take `tcp-redirect-data-path` 3× **after Steps 2–5** and compare against Step 1's before-series,
      naming the mechanism per row (Step 2 removed two lock entries per packet on both legs — the term the
      planning pass missed in the IPv6 reverse row — Step 3 removed three walks, Steps 4–5 shrank the
      copies). **No threshold**: the operator restated AC 4 as per-leg no-regression with the improvement and
      the IPv6:IPv4 ratio recorded as readings.
- [ ] Only if the IPv6 reverse rows still read slow with every other row unmoved: apply the delta rewrite.
      `PacketChecksums.TryRewriteIpv6Tcp`: the `stackalloc ushort[16]` snapshot and `FillAddressWords`
      become a single pass that keeps each old 16-bit word in a local as it is read, and the four 64-bit
      loads replace sixteen 16-bit `Slice` reads. The arithmetic is unchanged
      (`acc += (ushort)(old ^ 0xFFFF) + @new` per word, folded once) — proven bit-identical over 2,000,000
      random 32-byte pairs with 0 mismatches during planning (`research/implementation-notes.md` §7.3).
- [ ] Facts: `Ipv6AddressDeltaMatchesTheNarrowReference` (the same equivalence at a smaller N, kept as a
      regression), `ViewRewrittenChecksumsMatchTheFullRecomputeOracle` (both families through
      `TryRewriteTcpEndpointsFullRecompute`), and the mutable-offset assertion of
      `quality-guidelines.md:43`.
- [ ] Re-run `tcp-redirect-data-path` 3× again; keep the rewrite only if the IPv6 reverse rows' medians
      improve with the IPv4 rows unmoved and 0 B on every row, and record the reading either way. `prd.md`
      is not edited.

**Rollback F**: restore the snapshot + second pass (a plain revert of the optional commit).

## Step 7 — Slim per-packet context  [rollback point G]

- [ ] `FlowContext(FlowKey Key, AdapterMetadata? Adapter, ProcessMetadata? Process)` with the four string
      properties delegating to them and `RemotePort => Key.RemotePort` (`design.md` §7);
      `src/WinForward.Core/ProcessMetadata.cs` (in **Core**, because `FlowContext` is a Core type and holds
      the reference; `AttributeProcessAsync` in Runtime constructs it) as a two-string record.
- [ ] `AttributeProcessAsync` returns the context with a `ProcessMetadata` (created only when attribution
      produced an identity); `PacketFlowClassifier` attaches the slot table's `AdapterMetadata` by index.
- [ ] Every `FlowContext` construction site updated (`PacketFlowClassifier.cs:27`, `:40`, the test/benchmark
      helpers) and the log sites (`FlowDispatcher.cs:439-440`, `:452-453`, `NdisPacketActionExecutor.cs:121`)
      unchanged in output.
- [ ] Facts: the size assertions updated (**`FlowContext` 80, `CapturedFlowPacket` 152,
      `FlowStateView` 96** — the key is 64, not 56);
      `WarmHitContextCarriesNoProcessMetadata` (warm hits log `process = null`, exactly as today);
      `ClaimedContextCarriesTheInternedAdapterMetadata` (cold paths still see `AdapterId`); the policy
      suites unchanged; `HotPathAllocationGateTests` re-run unchanged (11/11).
- [ ] Re-run `dispatcher` for the warm rows (including the proxy rows, which is where an accidental
      `Endpoint` materialization would show) and record `*-after-step7.{md,csv}`.

**Rollback G**: restore the five-field context and the `with { ProcessName = … }` attribution return.

## Step 8 — Evidence, spec rows, record

- [ ] After-artifact in `benchmarks/results/2026-09-30-flow-key-parse-once/`: the full series, the size
      table (before → after for all eight types: **64 / 80 / 152 / 96**), `walk-and-gate-counts.txt` with the
      red-before text and every command, `gate-stability.txt` (one run of the four allocation classes with
      their totals), **the refreshed per-class totals for every class the task grew** (§Step 0), the
      discrimination re-proofs of `design.md` §11.6, and a README carrying the verdict tables, the AC-4
      readings (improvement figure + IPv6:IPv4 ratio, no threshold), the **spec-row → proof map** of
      `design.md` §10 and the residuals.
- [ ] Spec updates, each in the same commit as the change it describes:
      `hot-path.md:103-105` (contract 8), `hot-path.md:513-526` + `:1308-1312` (F2 cache, packed slot
      function), `hot-path.md:76-78` (contract 4, `PacketLayout` + the new sizes),
      `quality-guidelines.md:14`, `:36`, `:43`, `tcp-local-redirect.md:49`, `:87`, `windows-ndisapi.md:19`,
      `:25`, **`udp-relay.md:14` and `:27`** (the slot-keyed target map), `traffic-policy-lifecycle.md:9-11`.
      A one-line note in each F2 section that the slot function now reads packed fields, with the fact names.
- [ ] `rg 'AdapterContext'`, `rg 'OriginAdapterId'`, `rg 'OriginAdapterGeneration'`, `rg '_sequenceGate'`
      across `src/ tests/ benchmarks/` — `OriginAdapterGeneration` must still be a **live key field** (its
      remaining hits are the field, its compare, and the tests that assert it), and the only `_sequenceGate`
      hits may be the accepted-delta facts that document the removal.
- [ ] Session record in this file: outcome per PRD criterion (AC 4 as readings + per-leg no-regression), the
      Step-6 drop-or-keep decision with its numbers, every deviation, and the honest residuals.

## Validation commands

```bash
# gates (Release only; the 0 B gates are exact only in Release)
dotnet build WinForward.slnx -c Release                                     # zero-warning
dotnet test WinForward.slnx -c Release                                      # full suite green
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~HotPathAllocationGateTests"      # 11/11, totals asserted
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"        # F3 matrix untouched
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~TcpRedirectWarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~UdpWarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SelfTrafficWarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~FlowKeyShapeTests"
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue> (parse the XML)

# the AC 4 series (16 rows, 3 runs, recorded reading - no threshold). Keep the raw exports and concatenate.
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --filter '*TcpRedirectDataPath*' --job short \
    --exporters csv markdown --artifacts /tmp/wf-f4-datapath-$r
done

# the key/parse/dispatcher shape series (report-only companions, per-step before/after)
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableMiss*' '*FlowTableHit*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*Parser*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*Dispatcher*' --job short

# must-not-move series
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableClaimAndExpire*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --quick

# the exact counts, once per step that claims one (attach the probes, drive the real path, detach).
# The probe is [ThreadStatic]: drive and assert on ONE thread, and assert the thread id did not change.
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~RedirectedForwardPacketWalksHeadersExactlyOnce|FullyQualifiedName~RedirectedReversePacketWalksHeadersExactlyOnce|FullyQualifiedName~RedirectPacketTakesZeroSequenceGateEntries|FullyQualifiedName~WorkCountsOnTheDrivingThreadOnly"

# the per-class padded totals for every class this task grows (Step 0 and Step 8; the proof loops
# in hot-path.md assert these strings, and F2 recorded 12 + 4 + 2 + 7 = 25 for the structural classes).
for c in HotPathAllocationGateTests WarmPathGateTests SweepAllocationGateTests \
         TcpRedirectWarmPathGateTests UdpWarmPathGateTests SelfTrafficWarmPathGateTests FlowKeyShapeTests; do
  echo "class $c $(dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~$c" 2>&1 | rg -o 'Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+' | tail -1)"
done
```

**Gate-stability procedure.** Do **not** re-derive it: `hot-path.md:1234-1260`'s per-gate process loop is
the recorded procedure for the host's residual lump, and `hot-path.md:1020-1081`'s repeat-run loop is for a
product-regression claim. This task re-runs the four allocation classes once per rollback point and records
their totals; a failure is classified by the recorded signature predicate before anything is changed.

## Artifact list

Under `benchmarks/results/2026-09-30-flow-key-parse-once/`:

| file | content |
|---|---|
| `README.md` | host, tree hash + `git write-tree`, every command, the before/after verdict tables, the spec-row → proof map, the residuals |
| `tcp-redirect-data-path-before.{md,csv}` / `…-after.{md,csv}` | the 16-row series, 3 runs each, medians, `gated: false` |
| `flow-table-production-shape-before/after.{md,csv}` | the key's shape witness (incl. F2's `ResolveWarmHit`) |
| `parser-before/after.{md,csv}` | the parser series (a shape witness, not a target) |
| `dispatcher-before/after.{md,csv}` | the warm-shape companion of the 160 B gate |
| `struct-sizes-before.txt` | the eight `Unsafe.SizeOf` values, exact (before → after: key **128 → 64**, context **168 → 80**, packet **224 → 152**, view **160 → 96**) |
| `walk-and-gate-counts.txt` | header walks per packet 4/4 → 1/1 and sequence-gate entries 2 → 0, the defaulted-layout refusal table, each with its command and its red-before text |
| `defaulted-layout-hardening.txt` | the hazard, the red-before text, the fix and the green-after text (session 2) |
| `step6-ipv6-delta-verdict.txt` | the AC-4 readings (per-row medians, the IPv6:IPv4 ratio, the absolute gap), the isolated probe figures and the rejected-by-measurement verdict (session 2) |
| `dispatcher-after-step7-{1,2,3}.{csv,md}` | the post-Step-7 dispatcher companion (the slim context's watch series; session 2) |
| `gate-stability.txt` | one run of the four allocation classes and the F2/F3 structural classes, with totals and the discrimination re-proofs |
| `class-totals.txt` | the padded totals for every class this task grows, with the command and the tree hash (the proof loops assert totals strings) |

## Risky files / rollback points

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Core/Domain.cs` (`FlowKey`, `FlowHash`, `FlowContext`) | The key is the product's identity primitive: a wrong bit layout aliases two flows (fail-open routing), a wrong hash breaks F2's slot function, a dropped generation changes adapter-recreation semantics, and the key sits **exactly** on the 64 B ceiling so any added field breaks the contract | Steps 4, 5, 7 (points D, E, G) |
| `src/WinForward.Core/FlowTable.cs` | `SlotOf`, `TryResolveWarm`, `Matches`, `TransportTuple` are F2's landed contract; a packed-vs-materialized divergence silently costs a warm slot (not a wrong answer, because `Matches` corroborates by tuple equality) | Step 5 (point E) |
| `src/WinForward.Core/AdapterSlotTable.cs` (new) | Slot reuse is a wrong-adapter decision; exhaustion must refuse the adapter rather than alias it; the read array must be published safely for the lock-free `TryResolve` | Step 4 (point D) |
| `src/WinForward.Protocols/PacketChecksums.cs` | The endpoint rewriter is the transparent-redirect bedrock (`quality-guidelines.md:14`); a layout-driven shortcut that skips a needed check corrupts the wire, and without `PacketLayout.Family` it cannot even choose the write geometry | Step 3 (point C); Step 6 (point F) if kept |
| `src/WinForward.Protocols/IPTcpUdpPacket.cs`, `PacketLayout.cs` | The parse's rejection matrix is a security boundary (fragments, malformed, truncated) | Step 3 |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs` | The CAS-max loop and the deletion of a lock that also covered the RST readers | Step 2 (point B) |
| `src/WinForward.Runtime/TcpRedirect/TcpSequenceObservation.cs`, `TcpFrameRewriter.cs` | The pre-rewrite read order is load-bearing (`tcp-local-redirect.md:87`); a layout-driven read after a rewrite tracks the wrong sequence; `IsTcpSyn`'s transport compare must use the enum (`Tcp == 0`) | Step 3 |
| `src/WinForward.Runtime/FlowDispatcher.cs` | `FlowContext`'s field set is consumed by the warm entry, policy and logging; `IsReverseOf` must delegate to the packed key method | Steps 5, 7 (points E, G) |
| `src/WinForward.Runtime/UdpProxy/UdpAdapterTargetSource.cs`, `UdpResponseReinjector.cs` | Re-keying `Resolve(string)` → `Resolve(ushort)` touches the per-response path and the fail-closed fallback matrix | Step 4 (point D) |
| `src/WinForward.Runtime/Capture/CapturePacketProcessor.cs`, `MultiAdapterCaptureLoop.cs`, `NdisCaptureGeneration.cs` | The slot must be resolved once per generation and survive refreshes; an un-internable adapter must be refused, not keyed | Step 4 |
| `src/WinForward.Cli/*` (`DurableCaptureBundle`, `Program.cs:284`) | The table's lifetime is the whole process; creating it per generation would re-mint slots. `UpdateUdpTargets` (`:311-323`) builds the slot-indexed target array from the same scope | Step 4 |
| `tests/**` (215 `FlowKey` references across 47 files, 19 `AdapterContext` sites across 10) | A mechanical rewrite can weaken an assertion into a tautology — re-read every changed assertion, especially the policy/alias ones | per step |

## Commit plan skeleton

```text
0  test(bench): struct-size, header-walk and sequence-gate probes; the F4 before-artifact   [Step 1, point A]
1  perf(tcp): atomic CAS-max sequence trackers; the sequence gate removed
   + the concurrency and no-lock facts                                                        [Step 2, point B]
2  perf(proto): parse once - PacketLayout (with Family) on the packet; IsTcpSyn, both
   sequence observations and the checksum rewriter consume it
   + the walk-count, syn-parity and truncation facts                                          [Step 3, point C]
3  perf(core): interned adapter slot table; FlowKey stores a slot, keeps the generation;
   the UDP reinjection target map is slot-keyed
   + the interning/aliasing/target-map facts                                                  [Step 4, point D]
4  perf(core): packed 64-byte FlowKey; one packed hash expression behind FlowHash
   + the round-trip, reverse-swap and F2 re-proof facts                                       [Step 5, point E]
5  (ABSENT) perf(proto): single-pass IPv6 address delta - REJECTED BY MEASUREMENT; the reading
   and the three reasons are recorded in benchmarks/results/2026-09-30-flow-key-parse-once/
   step6-ipv6-delta-verdict.txt                                                               [Step 6, point F]
6  perf(core): slim FlowContext behind interned adapter/process metadata
   + the size, metadata-presence and logging-parity facts                                     [Step 7, point G]
7  fix(proto): refuse a defaulted PacketLayout by construction - a parsed-layout stamp, an
   IsTcp gate on every consumer, a private constructor
   + the byte-identical-refusal, no-sequence, redirect-leg and dispatched-layout facts         [hardening]
8  docs(evidence): after-artifact, spec contracts, spec-row -> proof map and the F4 record    [Step 8]
```

Steps 4 and 5 did not need to be one commit (the previous session split them). Commit 5 is **absent
by design**: dropping the optional IPv6 checksum commit with the reading recorded is the legitimate
outcome `implement.md` names, not a gap. Commit 7 is the hardening this session added on top of the
plan; it is independently revertable (delete the stamp, restore the public constructor, and the three
`Transport == Tcp` compares) and no other step depends on it.

## Acceptance-criteria + spec-row → proof mapping

| PRD criterion | Where it is discharged | Kind |
|---|---|---|
| **AC 1 key shape** — size ≤64 B, no string comparison on a lookup, table round-trip, unregistered adapter refused, **packed key round-trips its endpoints + the reverse swap** | Step 4 (table + slot, generation kept) → Step 5 (packed layout): `FlowKeyFitsOneCacheLine` (at exactly 64), `FlowKeyHasNoReferenceTypedFields`, `AdapterSlotTableRoundTripsStableId`, `AdapterSlotTableRefusesAnUnregisteredAdapter`, `SlotValuesAreNeverReused`, `AdapterSlotSurvivesARefresh`, `AnAdapterThatCannotBeInternedIsRefusedNotAliased`, `FlowKeyEqualityKeepsTheGeneration`, `FlowKeyPackedRoundTripsEndpoints`, `ReverseSwapsEndpointsAndScopes`, `PackedAndMaterializedHashesAgree`, `PackedAndMaterializedCanonicalHashesAgree` (specified corpus), `CanonicalSlotIsOrderIndependent`, `IsReverseOfMatchesTheEndpointComparison` | exact |
| **AC 2 parse-once** — a **thread-scoped** counter through the **processor → dispatcher → coordinator** path, one walk per redirected packet, same results IPv4/IPv6, malformed frames same rejection | Step 1 (**red 4 forward, 4 reverse** through `ProcessAsync`) → Step 3 (`ParseWalk == N`, `RevalidateWalk == 0`, `ViewRewrite == N` on the driving thread): `RedirectedForwardPacketWalksHeadersExactlyOnce`, `RedirectedReversePacketWalksHeadersExactlyOnce`, `WorkCountsOnTheDrivingThreadOnly`, `LayoutSynTestMatchesTheSpanTest`, `PacketLayoutFitsSixteenBytes`, `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects` (truncations + cross-family reject), `SequenceAdvanceIgnoresEthernetPadding`, `ExtensionHeaderFramesProduceTheSameAdvance`, existing parse corpora unchanged | exact |
| **AC 3 sequence trackers** — zero gate entries, CAS-max pinned, RST paths green | Step 1 (**red 2**) → Step 2 (0): `RedirectPacketTakesZeroSequenceGateEntries`, `TcpRedirectAssociationHoldsNoLockField`, `ConcurrentSequenceObservationsKeepTheLargerValue`, `UnobservedTrackerReadsNullAndObservedZeroReadsZero`, RST suites green | exact |
| **AC 4 IPv6 forwarded-leg series (one line, recorded reading)** — re-run against the re-taken before-series with the mechanism named; **discharge is per-leg no-regression**; the improvement and the IPv6:IPv4 ratio are readings, not gates | Step 2 (the `TrackServerSequence` lock term), Step 3 (three walks per leg), Steps 4–5 (copies) are the named mechanisms; the 3× series in the validation block; Step 6 is an **optional** commit decided on the reading. No threshold anywhere — `hot-path.md` contract 9's noise floor. `design.md` §13 D7 replaces the withdrawn "cannot move the row" claim | series / recorded reading |
| **AC 5 allocation** — per-packet 0 B gates incl. the dispatcher warm path and `FlowTableWarmResolveAllocatesNoManagedBytes` | `HotPathAllocationGateTests` 11/11 and `WarmPathGateTests` re-run **unchanged** at every rollback point; Step 7's claim-path metadata allocation is argued against the gates that exist (`design.md` §9 risk 13) | exact |
| **AC 6 gates** — suite, F3 sweep matrix, F2 warm facts, gc-soak anchors, UDP/TCP scenarios | the validation block; F3's sweep code is untouched by every step; every grown class re-records its padded total (§Step 0, §Step 8) | exact |
| Release zero-warning, format empty, inspectcode zero | the validation block at every rollback point | exact |
| Benchmark data recorded and cited before archive | Step 1's before-artifact + Step 8's after-artifact + `walk-and-gate-counts.txt` + `struct-sizes-before.txt` + `class-totals.txt` | artifact |
| **Spec rows** touched by this design are restated with a proof each | `design.md` §10's spec-row map (now including `udp-relay.md:14`/`:27`), discharged row by row in Step 8 | exact |

## Deferred / explicitly out of scope

- The §A4 sharded slab + per-shard open-addressing index with the canonical transport key and incremental
  rehash (roadmap item 7) — the PRD's out-of-scope note.
- F2's warm cache, activity bucket, self-traffic split, redirect/UDP caches and F3's sweep: untouched, and
  the acceptance surface (§2.5's re-proof table).
- The tombstone / setup-cooldown / pending-SYN / setup-queue representations (F3 contract item 5).
- Lossy fingerprint caches for tombstones and cooldowns, `_candidatePorts` nibble packing (research §A5).
- `IPFragment.IsFragment`'s bit test and `TcpResetBuilder.TryBuildResetFromSyn` (cold; convertible to the
  layout later, no per-packet value).
- The `PacketChecksums.TryRewriteUdpEndpoints` family: no production caller (`PacketChecksums.cs:26-28`).
- Widening `FlowKey`'s adapter identity beyond 65,535 slots, or reusing slots with a generation tag: both
  re-open the ABA §3.2 closes.
- Windows-only rows (`gc-soak` on a real NIC, `TcpThroughputScenario`): unchanged assignment to the
  `windows-real-nic` programme.

## Review dispositions (independent review, for the archive)

| # | Severity | Disposition |
|---|---|---|
| R1 | BLOCKER | **Fixed.** The generation **stays a key field** with integer equality (operator decision, PRD Notes): only `OriginAdapterId` → `OriginAdapterSlot` changes. `design.md` §2.1/§3.4/§2.3/§13-D4, risk 7, accepted delta 1, `§3.5`'s `Create` overloads (generation is an explicit parameter), `implement.md` Steps 0/4/5, and `FlowKeyEqualityIgnoresTheAdapterGeneration` is **deleted** (replaced by `FlowKeyEqualityKeepsTheGeneration`). The intern table is bounded by the adapter count because the generation is not part of the interned identity. `research/implementation-notes.md` §12 D4/D5 restated. |
| R2 | BLOCKER | **Fixed.** `32 + 8 + 8 + 4 + 2 + 3 = 57 → 64`, exactly on the contract: the "56 B / 8 B margin" was wrong and is withdrawn. Every after-size updated — `FlowKey` **64**, `FlowContext` **80**, `CapturedFlowPacket` **152**, `FlowStateView` **96** — in `design.md` §2.1/§2.2/§7/§11.2, `implement.md` Steps 5/7/8 and the artifact table, and §2.1 now states that **no further field fits**, including a "temporary" diagnostic one. |
| R3 | BLOCKER | **Fixed.** `PacketLayout` gains `byte Family` (fits the existing padding, still 16 B, asserted by `PacketLayoutFitsSixteenBytes`); the IPv4-vs-IPv6 write geometry and §4.3's retained family-equality reject are now performable, and risk 8b + `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects`' cross-family call pin it against writing 16 bytes into an IPv4 frame. |
| R4 | MAJOR | **Fixed.** `PacketPathProbe` becomes `[ThreadStatic]` with a driving-thread assertion, because the test project has no `xunit.runner.json`/`[CollectionBehavior]` (verified) and collections run in parallel; `WorkCountsOnTheDrivingThreadOnly` is the fact, and assembly serialisation is named as the fallback only. `design.md` §4.4, `implement.md` Step 1. |
| R5 | MAJOR | **Fixed.** The harness is now `CapturePacketProcessor.ProcessAsync` → dispatcher → coordinator, wired as both `reverseHandler` and executor (`FlowDispatcherExecutorTests.cs:296` / `BatchedPassReinjectionE2eTests.cs:35-43` front end + `HotPathAllocationGateTests:41`/`:104` coordinator fixture), with the per-leg before-counts **forward 4, reverse 4** quoted from it. `design.md` §4.4, `implement.md` Step 1, AC-2 mapping. |
| R6 | MAJOR | **Fixed.** AC 4 is a **recorded reading** with per-leg no-regression as the sole discharge; no threshold anywhere (`hot-path.md` contract 9's noise floor). `design.md` §6/§10/§11.5, `implement.md` Step 6, AC-4 mapping, and the series is re-taken after Steps 2–5. |
| R7 | MAJOR | **Fixed.** The "requirements 1–4 cannot reach AC 4" premise is **withdrawn**: the row is `copy + TrackServerSequence + TryRewriteTcpEndpoints`, `TrackServerSequence` takes the `_sequenceGate` (22.9–29.0 ns uncontended, measured), so Step 2 alone removes a term from it. `design.md` §6/§13-D7, `research/implementation-notes.md` §7.2/§12, and Step 6 is now a genuinely optional commit with no Step-0 decision. |
| R8 | MAJOR | **Fixed.** `FlowKeyPackedRoundTripsEndpoints` (IPv4 / IPv6 / IPv6 + nonzero `ScopeId`) and `ReverseSwapsEndpointsAndScopes` added, plus a **specified hashing corpus** (a high-vs-low half-order-divergent pair, the equal-endpoint tie, both families, both orientations, a nonzero scope). `design.md` §2.3/§2.5, `implement.md` Step 5, AC-1 mapping, and risk 5b names the low/high-swap failure the hash facts cannot see. |
| R9 | MAJOR | **Fixed.** The `quality-guidelines.md:14` map row now states the true retained set — `Transport == (byte)PacketTransport.Tcp`, `frame.Length >= TransportEnd`, family equality — and that the `dataOffset` bound, version, IHL, protocol byte, fragment bits, total length and the extension chain are the **view's** proofs and are skipped. `design.md` §4.2/§4.3/§10. |
| R10 | MAJOR | **Fixed.** New `design.md` §3.6 covers the slot-keyed UDP reinjection target map (`IUdpAdapterTargetSource.Resolve(ushort)`, array built in `DurableCaptureBundle.UpdateUdpTargets:311-323` from the same `scope`, `AdapterIds` through the slot table), with the `udp-relay.md:14`/`:27` spec rows, the `UdpTargetMapResolvesBySlot` fact, risk 17 and `implement.md` Steps 4/8. |
| R11 | MAJOR | **Fixed.** Exhaustion is no longer fail-open: an adapter that cannot be interned is **refused at generation build** (excluded from the capture scope, one rate-limited log), so no captured packet carries `NoSlot` and two adapters can never alias through it. `design.md` §3.1/§3.3, risk 2, accepted delta 3, fact `AnAdapterThatCannotBeInternedIsRefusedNotAliased`, `implement.md` Step 4. |
| R12 | MAJOR | **Fixed.** `IsTcpSyn(in PacketLayout)` compares against `(byte)PacketTransport.Tcp` (`Tcp == 0`, `Udp == 1`), with `LayoutSynTestMatchesTheSpanTest` over both transports and a discrimination re-proof in §11.6. `design.md` §4.2, risk 8c. |
| R13 | MINOR | **Fixed.** The sequence-read bound is `frame.Length >= layout.TransportOffset + 8` (the whole 4-byte read) so a short frame rejects instead of throwing out of `Slice`; and `TransportTuple`'s packed size is **48 B**, not 56. `design.md` §4.2/§4.3/§2.5, risk 8d, `implement.md` Step 3/5. |
| R14 | MINOR | **Fixed.** `Observe` publishes a **replacement** `AdapterMetadata` instance (the properties stay get-only; the by-slot seam was later removed as unused, C8); accepted delta 2 is qualified — nothing in `src/` compares `FlowContext`/`CapturedFlowPacket` by equality, only tests do; and classification's slot→metadata read is stated as a **plain array index**. `design.md` §3.1/§7/§8. |
| R15 | MINOR | **Fixed.** §2.4 no longer claims "no hot consumer gets worse": `IsReverseOf` would materialize four `Endpoint`s per warm UDP proxy packet and is served by the new packed `FlowKey.IsReverseOf(FlowKey)`; `SelfTrafficKey.From` is neutral (it already materialized). The Core-internals decision is stated — packed accessors stay Core-internal and **no `InternalsVisibleTo` edge is added** (`WinForward.Core.csproj:8-9`) — with `DispatcherBenchmarks`' proxy rows as the watch series. `design.md` §2.4, risk 16, `implement.md` Step 5. |
| R16 | MINOR | **Fixed.** Steps 0 and 8 now re-record the padded totals for **every** class the task grows (not only the four allocation classes), with `class-totals.txt` as the artifact and `hot-path.md:1236-1240`'s totals-string contract cited. `design.md` §11.9, `implement.md` Steps 0/8. |
| — | VERIFIED (no change) | CAS-max is exactly the lock's predicate (`IsSequenceAhead`, `TcpRedirectTable.cs:156`) with `0xFFFFFFFF` legal and `-1` unreachable; the only readers are cold; `TcpRedirectAssociationHoldsNoLockField` is achievable as written; the packed hash identity holds by construction and drift can only cost a warm slot because `Matches` corroborates by tuple equality; `FlowHash.Combine`'s field set stays slot- and generation-free and `TransportTuple.GetHashCode` keeps delegating to it. All folded into `design.md` §2.5/§5/§9. |
| C1 | MAJOR (found by the independent check) | **Fixed.** `UdpAdapterTargetSource`'s seed snapshot was built over a throwaway `AdapterSlotTable`: the primary-constructor field initializer cannot reference `_slots` (`CS0236`), and that compiler error had been worked around rather than fixed. A caller supplying a `bySlot` map therefore reported an empty `AdapterIds` until the first `Update` (the `udp.reinject.unresolved` diagnostic's `mapAdapters` field). Fixed by moving the seed into an explicit constructor over `_slots`; `AdapterIdsResolveThroughTheSlotTable` now asserts the constructor-supplied case. Discrimination re-run: with the old seed the fact fails (`Expected: ["wlan-1"], Actual: []`). Production was unaffected — `UdpAdapterTargetSource(adapterSlots)` never passes a map. |
| C2 | MAJOR (found by the independent check) | **Fixed.** AC-1's "a self-consistent low/high swap cannot pass the hash-agreement fact" did **not** hold: `PackedAndMaterializedCanonicalHashesAgree`'s "materialized" side was `FlowHash.CombineCanonical`, which itself delegates to `CombineCanonicalPacked`, so both sides moved together and the corpus's divergent pair was toothless. Fixed by giving the fact an independent materialized oracle (the pre-F4 `UInt128.CompareTo`-then-port order hashed through the key's materialized endpoints). Discrimination re-run: with a low-half-first packed order the fact now fails (`Expected: 1213610649, Actual: 1809849290`); before the fix the same injection passed 8/8. |
| C3 | MINOR (artifact accuracy, found by the independent check) | **Fixed.** Fact names that no fact carries: `FlowKeyFitsOneCacheLine` / `FlowKeyHasNoReferenceTypedFields` → `FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields` (plus the exact-64 `FlowKeyShapeTests.StructSizesForDiagnostics`); `TryInternIsIdempotentPerStableId` / `SlotValuesAreNeverReused` → `TryInternIsIdempotentPerStableIdAndSlotsAreNeverReused`; `UdpTargetMapResolvesBySlot` → `ScopeHeadBecomesHostFallbackAndEachAdapterResolves` + `AdapterIdsResolveThroughTheSlotTable`; `TransportTupleEqualsTheKeysTransportFields` → the observable corroboration facts (`FlowTableTransportTupleIsUniqueAcrossOrigins`, `WarmCacheHitServesTheValidatedView`), with the missing direct field-set fact recorded as a residual. Fixed in `hot-path.md` (contract 8, the F2 warm-cache row, both spec-row-map rows), `windows-ndisapi.md`, `udp-relay.md` and the evidence `README.md`. Also corrected contract 4's claim that `FlowKeyShapeTests` asserts `PacketLayout` 16 — that is `PacketLayoutTests.PacketLayoutFitsSixteenBytes`. |
| C4 | MINOR (artifact accuracy, found by the independent check) | **Fixed.** The recorded `git write-tree` fingerprint `b6e93b5248ed5c67345fa1385643ba37a3d8bdcb` **is the base tree**: nothing is staged in this uncommitted tree, so the value is identical for a pristine `ca8d999` checkout and fingerprints nothing. The check session's section in `gate-stability.txt` keeps the literal `git write-tree` (the spec asks for it, and the F2 artifact already carries the same caveat) and adds `git diff HEAD \| sha256` (first 16) plus a sha256 over the untracked `src`/`tests`/`benchmarks` files, which do cover the measured work. |
| C5 | MINOR (artifact accuracy, found by the independent check) | **Fixed.** `gate-stability.txt` claimed the Step-1/3/5 discrimination re-proofs "were already recorded by the previous agent in `README.md`" — `README.md` records no such thing. The check session re-ran them and recorded the real results in `gate-stability.txt`: the defaulted-layout injection (4 facts red, `Expected: Blocked / Actual: Injected`), the `IsTcpSyn` raw-transport compare (`Transport == 1` → `LayoutSynTestMatchesTheSpanTest` fails), the packed canonical low/high order (fails only after C2), and the key grown to 72 B (`FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields` and `StructSizesForDiagnostics` both fail). Each was run against the fixed tree with the temporary edit reverted immediately (files verified byte-identical by md5). |
| C6 | MINOR (artifact accuracy, found by the independent check) | **Fixed.** The composition-level slot refusal was described as pinned "at the table/generation-build level". Only the table level is pinned: `NdisCaptureGenerationFactory.Create`'s exclusion loop (drop the adapter, emit `adapter.slot-exhausted`) has **no test** — the factory is Windows-only and needs a real driver. The evidence README's residual now says so explicitly. |
| C7 | MINOR (found by the independent check) | **Fixed (docs/spec only).** `AdapterSlotTable.Observe` has **no caller** anywhere in `src`/`tests`/`benchmarks`: the generation build refreshes through `TryIntern` (it holds the stable ID), so the by-slot seam the design specified is unused public API. The type's XML doc claimed the metadata's generation was "the generation of the last `Observe`", and `windows-ndisapi.md` named `Observe` as the refresh path; both now say `TryIntern`. The method was **removed** in the operator's format-gate round (C8) after the caller search confirmed the analyzer: `TryIntern` is the table's only writer. |

| C8 | (format gate, operator-run) | **Fixed.** The operator's `dotnet format --severity info --verify-no-changes` reported 29 diagnostics, all on this task's new surface, and all were resolved **without any suppression**: **IDE0034 ×4** — `default(PacketLayout)` → `default` at the four argument sites in `PacketLayoutTests` (the two `default(PacketLayout).IsValid/.IsTcp` member accesses stay, since no target type exists there and the analyzer does not flag them); **RCS1085 ×13** — every cited field-returning accessor became a get-only auto-property assigned in the constructor: the 10 packed `FlowKey` accessors (`OriginAdapterSlot`, `OriginAdapterGeneration`, `LocalLow/High`, `RemoteLow/High`, `LocalPort`, `RemotePort`, `LocalScopeId`, `RemoteScopeId`), the 6 `PacketLayout` geometry accessors, and `FlowBuilders.Slots`. **No setter was added** (all are `{ get; }`, so the hardening's stamp/gates and the `with`-immunity are untouched), the three enum-narrowing `FlowKey` bytes stay plain fields, and the exact sizes were re-run: `FlowKey` **64** (`FlowKeyShapeTests`), `PacketLayout` **16** (`PacketLayoutFitsSixteenBytes`), with the defaulted-layout facts green. `FlowKey.Equals`/`IsReverseOf`/`Reverse`/`ToString`/`GetHashCode` and `FlowContext`'s delegating properties were repointed to the properties (the analyzer's `FlowContext` caution did not apply — it points at `FlowKey`'s packed accessors). **MA0154 ×1** — `PacketLayout.IsValid`'s doc uses `<see langword="default"/>`. **MA0076 ×4** — the invariant interpolation form this repo already uses (`string.Create(CultureInfo.InvariantCulture, $…)`) in `FlowKeyShapeTests`, `AdapterSlotTableTests` and `PackedFlowKeyTests`. **MA0003 ×2** — `friendlyName: null`. **RCS1205 ×1** — named arguments put back in parameter order at `PacketLayoutTests`'s IPv6 corpus row. On the operator's instruction, **`AdapterSlotTable.Observe` was deleted**: a full caller search (`src`, `tests`, `benchmarks`, reflection-by-name) found none — the only other `Observe` symbols are unrelated (`SweepPauseScenario.Observe`, `SequenceTrackerTests`' local `Observe`) — so `TryIntern` is the table's only writer and the docs carry no dangling reference. Re-measured after the round: Release build 0 warnings / 0 errors; full suite **1057 + 18 passed, 0 failed**; every affected class re-run green with unchanged totals (1/8/8/7/3/5/2/14) and the four exact gates 3× each green (11/3/12/14). |

| C9 | (jb inspectcode round, operator-run) | **Fixed.** The 44 findings in this task's surface were resolved without weakening a test. **InvalidXmlDocComment ×7**: `TcpFrameRewriter`'s layout-overload doc had an unclosed `<see>` tag (a `,` where `"/>` belonged — compiler CS1584/CS1570), `FlowKey.Create`'s doc referenced `WindowsAdapter` (unreachable from Core) → `<c>WindowsAdapter</c>`, and `FrameRewriterBenchmarks`' two crefs became ambiguous once `IsTcpSyn`/`TryRewriteForwardLeg` gained overloads → signature-qualified (`IsTcpSyn(ReadOnlySpan{byte})`, `TryRewriteForwardLeg(Span{byte}, Endpoint, Endpoint, TcpRedirectAssociation, ushort)`). **RedundantUsingDirective ×12**: 7× `using WinForward.Core;` (redundant inside `namespace WinForward.Core.Tests`), 2× `using System.Runtime.InteropServices;`, `using WinForward.Protocols;` in the batching tests, `PackedFlowKeyTests`' unused `using static …TcpCoordinatorFakes;`, and `NdisPacketActionExecutorLoggingTests`' `System.Buffers.Binary` (removal verified by a clean build). **UseNullPropagation ×3**: the probe sites are now `PacketPathProbe.X?.Invoke();` — the `FlowTable.NoteGateHold` shape, one read and no allocation, with `PacketPathProbe`'s own doc updated; the walk facts and every allocation gate re-run green. **ConvertToAutoPropertyWhenPossible ×1**: `WalkCounter.Count` is an auto-property with a private setter (`Bump() => Count++`). **MemberCanBePrivate.Local ×5**: `Composition.{Coordinator,Table,Processor}` and the redirect harness's `LastOutcome` are private. **MemberCanBePrivate.Global ×4**: `FlowBuilders.{Adapter,Process}` are private (only `Context` calls them, verified repo-wide) and the two `PacketLayout` geometry accessors are **suppressed** with the AGENTS.md reason (documented false-positive class; the parse-proof surface of a public cross-assembly type is not narrowed). **RedundantCast ×1**: `(int)slot` in `AdapterSlotTable.PublishUnderGate`. **RedundantArgumentDefaultValue ×2**: the explicit `0x18` equals `BuildIpv4TcpFrame`'s `tcpFlags` default. **ConvertIfStatementToReturnStatement ×1**: suppressed with the repo's existing B1 reason (guard-clause + throw reads failure-first), same as `FlowKey.Create`. **ArrangeStaticMemberQualifier ×1**: `FlowBuilders.Context` is called unqualified where the file already has `using static …FlowBuilders` and the editorconfig asks for unqualified calls. **ClassNeverInstantiated.Global ×1**: suppressed — the type is instantiated through target-typed `new` outside the partial file (`TcpRedirectComposer.cs:42`, `TcpCoordinatorFakes.cs:105`). **AccessToDisposedClosure / AccessToModifiedClosure ×4** (`SequenceTrackerTests`, `PacketPathWalkCountTests`): restructured, not suppressed — the shared mutable completion counter became a `CountdownEvent`, the sibling parse returns through its `TaskCompletionSource<bool>` (no thread touches a captured local), and both tests join every capturing thread and only then dispose, in a `finally`. Re-measured at the fixed tree: Release build 0 warnings / 0 errors; full suite **1057 + 18 passed, 0 failed**; the eleven affected classes green with unchanged totals; the four exact gates and the four warm-path classes 2 runs each, all green. Two **pre-existing** compiler-only doc defects outside this task's rows were left alone and are reported (`FlowDispatcher.cs:57`'s `ArgumentNullException.ParamName` cref, `DispatcherBenchmarks.cs:149`'s `DispatchSlowAsync` cref). |

## Check session (independent verification, 2026-09-30)

Independent check sub-agent; nothing committed. Verification performed at the fixed tree:

- **Build / suite**: `dotnet build WinForward.slnx -c Release` 0 warnings / 0 errors; full suite
  **1057 + 18 passed, 0 failed** (re-run after every fix).
- **AC-4 readings re-derived from the raw CSVs** (`tcp-redirect-data-path-{before,after}-{1,2,3}.csv`):
  per-row 3-run medians match the README table exactly (worst row 1.50×, **no row regresses**, every
  row 0 B); ratio readings reproduce (1.65 → 1.92 at 1400 B; 2.08 → 2.29 at 128 B); gap 48.07 → 34.97 ns.
  The companion tables (`flow-table-production-shape`, `dispatcher-after-step7`) also reproduce exactly.
- **Red-before, walk counts**: the pre-change source in `/tmp/wf-f4-before` confirms the red mechanism —
  `HandlePacketAsync` called the span `IsTcpSyn`, the reverse leg called `RecordServerSynAck` +
  `TrackServerSequence`, and both legs called the span rewriter: 3 parses + 1 revalidation = 4 walks
  per leg, matching `walk-and-gate-counts.txt`.
- **Attack list** (the eight items): (1) `PacketLayout`'s only producer is the private-ctor'd
  `From(in PacketView)`; `with`/`new`/`default` cannot stamp (all properties get-only, ctor private);
  a fabricated `PacketView` can, which is recorded as a residual — the stamp bounds `default`, it does
  not authenticate a parse. (2) every raw geometry read in `src/` is behind `IsTcp` (rewriter, SYN
  test, both sequence reads) or receives a layout only through those gates; the convention is
  documented on the type itself and in `hot-path.md` contracts 10–11. (3) the rewritten test contexts
  were diffed: no fact or assertion was dropped, and the two `EndpointAndPolicyTests` sites still test
  what they name (the port variant now builds the key for the matching port). (4) `context.Process is
  not null` is equivalent to the old pair of null checks for every identity the attributor can return
  (`WindowsProcessAttributor` builds `ProcessIdentity(process.ProcessName, …)`, never both-null), and
  the required `AdapterSlotTable` parameter is wired at every production and test construction site
  (`Program.cs`, `DurableCaptureBundle`, benchmarks, 8 test sites). (5) the refusal is pinned at the
  table level only (C6). (6) attribution: the README's claims match the measurement design — the
  datapath series is product + harness switch, the walk counts carry parse-once, the companions are
  witnesses. (7) the defaulted-layout red-before was reproduced by neutralizing the stamp check (C5).
  (8) the packed key's size bound, no-reference-field fact, round-trip and scope corpus all hold.
- **Gate/class proof runs**: `gate-stability.txt`'s appended section carries the per-gate/per-class
  process runs (own process, padded summary, revision, tree, exit status) for the four exact gates,
  the four F2/F3 structural classes and every class this task grew.

**Residuals the operator must record before archive** (in addition to the task's own list):

- `TransportTuple.Equals`'s per-field coverage has no direct fact (`TransportTuple` is a private
  nested type; a direct fact would need a visibility change). The warm corroboration is pinned
  observably; a future warm-cache change should add a scope-differing-key fact, since the canonical
  slot hash deliberately excludes the scope ids.
- `PacketView` has a public constructor, so `PacketLayout.From(fabricatedView)` yields a
  valid-stamped layout; the hardening's guarantee is "a defaulted layout is never applied", not
  "every valid layout came from a parse".
- `AdapterSlotTable.Observe` is **resolved**: deleted (C8), so the table has one writer (`TryIntern`).
- `PacketLayout`'s geometry accessors (`Transport`, `TcpFlags`, `Family`, `IPHeaderLength`,
  `TransportHeaderLength`, `TransportLength`, `TransportOffset`, `TransportEnd`) are public without an
  `IsValid` guard — `WinForward.Runtime` and the benchmark read them. The gate is convention plus
  tests, documented on the type (`TransportOffset`/`TransportEnd` are marked "meaningful only for a
  valid layout", `IsTcp`'s doc names itself as the gate) and in `hot-path.md` contracts 10–11, so a
  future caller meets the convention at the type before the call site; it is not compile-enforced.
- Pre-existing stale fact citations in spec rows this task did not touch:
  `windows-ndisapi.md:442` names `SynRewriteParseFailureLeavesFrameByteIdentical` (the fact is
  `TruncatedSynRewriteFailureLeavesFrameByteIdentical`), and `quality-guidelines.md:20` names
  `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce`, whose barrier scenario R8 made unreachable
  (the live contract is `ConcurrentSynBurstWhileListenerSetupIsParkedIsAbsorbedIntoOneSetup`, with
  `ConcurrentLoserCount` asserted `== 0`). Both left alone deliberately: they predate F4 and the
  second needs an R8-contract rewrite, not a rename.
- `dotnet format` and `jb inspectcode` remain the operator's gates and were not run.

## Session record

Session 2 (Steps 6–8 + the defaulted-layout hardening). Steps 0–5 were landed by session 1; this
record covers the whole task's outcome, with session 1's readings quoted from the artifact.

**Outcome per PRD criterion**

| Criterion | Outcome |
|---|---|
| **AC 1 key shape** | **Met, exact.** `FlowKey` is exactly **64 B** (`FlowKeyFitsOneCacheLine`) with no reference-typed field (`FlowKeyHasNoReferenceTypedFields`); `Equals` compares `OriginAdapterSlot` + `OriginAdapterGeneration` + both scope ids; the table round-trips, refuses an unregistered adapter, never reuses a slot and survives a refresh; the packed key round-trips both endpoints (IPv4 / IPv6 / IPv6 + nonzero scope) and the reverse swap. F2's warm path re-proven (packed slot function, packed 48 B `TransportTuple`, `FlowTableWarmResolveAllocatesNoManagedBytes` unchanged). |
| **AC 2 parse once** | **Met, exact.** Thread-scoped probes driven through `CapturePacketProcessor.ProcessAsync` → dispatcher → coordinator: **red-before 4 walks per leg** (3 parses + 1 revalidation), after **1 parse + 1 layout-driven rewrite** per leg, on the driving thread. Truncation corpus, SYN parity, padding and extension-header advances, and the malformed/truncated rejection path are unchanged. |
| **AC 3 sequence trackers** | **Met, exact.** Red-before **2** gate entries per packet pair; after **0**, with no reference-typed field on the association (the `Lock` allocation is gone too). CAS-max pinned by a two-thread barrier test; RST suites green untouched. |
| **AC 4 IPv6 forwarded-leg series (recorded reading)** | **Discharged as a reading.** Every one of the 16 rows improves; the AC-4 row `ReverseLegForwarded` improves 1.68× (IPv6, 1400 B) and 1.88× (IPv6, 128 B) against the re-taken before-series, with the mechanisms named (Step 2: the sequence-gate lock term inside `TrackServerSequence`; Step 3: three header walks). No row regresses beyond noise — the worst row still improves 1.50×. The IPv6:IPv4 ratio reading moved **1.65 → 1.92** (1400 B) / 2.08 → 2.29 (128 B), recorded rather than gated. |
| **AC 5 allocation** | **Met.** `HotPathAllocationGateTests` 11/11 unchanged, `FlowTableWarmResolveAllocatesNoManagedBytes` unchanged, the F3 sweep matrix and the F2 warm-path classes green; no gate relaxed. |
| **AC 6 gates** | **Met.** Full suite **1057 + 18** green (one run at the closing tree); Release build zero-warning; per-class totals re-recorded for every class this task grew (`class-totals.txt`, including the mapping against the Steps 1–5 values). `dotnet format` / `jb inspectcode` are the operator's gates and were not run (hard constraint). |
| **Benchmark data recorded and cited** | **Met.** `benchmarks/results/2026-09-30-flow-key-parse-once/` — README (verdict tables, AC-4 readings, size table, spec-row → proof map, residuals), the before/after series, `walk-and-gate-counts.txt`, `struct-sizes-{before,after}.txt`, `class-totals.txt`, `gate-stability.txt`, `defaulted-layout-hardening.txt`, `step6-ipv6-delta-verdict.txt`, `dispatcher-after-step7-{1,2,3}.csv`. |

**Step 6 decision (optional commit): rejected by measurement.** The single-pass wide-load IPv6 address
delta was **not** implemented. Readings (reproduced from this directory's CSVs, 3-run medians):
`ReverseLegForwarded` IPv6 1400 B 122.07 → 72.87 ns (1.68×), 128 B 112.05 → 59.64 ns (1.88×);
IPv4 1400 B 74.00 → 37.90 ns; every row improves (worst 1.50×, largest after/before ratio 0.665);
IPv6:IPv4 ratio 1.65 → **1.92**; absolute IPv6−IPv4 gap 48.07 → 34.97 ns. The step's own precondition
("the IPv6 reverse rows still read slow **with every other row unmoved**") is false, the only measured
win for the rewrite is ≈5.5 ns on the isolated delta (22.1–22.6 vs 27.6–28.3 ns; below this host's
noise floor per `hot-path.md` contract 9), and the larger structural half (dropping the `stackalloc`
snapshot and its second pass) is unmeasurable without editing product code. Full text:
`step6-ipv6-delta-verdict.txt`.

**The defaulted-layout hardening (session 2, requested on top of Steps 0–5).** `PacketTransport.Tcp`
is `0`, so `default(PacketLayout)` read as a valid TCP layout and the layout-driven rewriter wrote the
IPv4 endpoints at the IP header's own offset. Fixed by construction: a private validity stamp only
`PacketLayout.From(in PacketView)` writes, `IsValid`/`IsTcp` gates on **every** consumer, and a private
constructor so no hand-built value can claim a parse. Red-before recorded (3 facts failing, `Injected`
instead of fail-closed `Blocked`) and the red **was live**: `HotPathAllocationGateTests
.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes` had been passing while the leg mis-rewrote.
Green-after with 5 new facts; no existing packet test weakened. Text:
`defaulted-layout-hardening.txt`.

**Step 7 outcome.** `FlowContext` is `(FlowKey, AdapterMetadata?, ProcessMetadata?)` with the four
string properties and `RemotePort => Key.RemotePort`; `PacketFlowClassifier` attaches the slot table's
published metadata by index; `AttributeProcessAsync` creates the `ProcessMetadata` once per attributed
claim. Sizes land on the design's targets exactly: **`FlowContext` 80**, **`CapturedFlowPacket` 152**
(no re-derivation needed). Facts: `ClaimedFlowCarriesTheInternedAdapterAndProcessMetadata`,
`WarmHitKeepsTheAdapterMetadataAndCarriesNoProcessMetadata`.

**Deviations from the plan (each with its reason)**

1. **The hardening is an extra commit (commit 7 below), not part of Step 3.** It was requested after
   Steps 0–5 landed; it touches only `PacketLayout` and its consumers, and reverts alone.
2. **`PacketLayout`'s validity stamp is a private ctor, not a public seventh parameter.** The plan's
   "explicit invalid/sentinel encoding" is satisfied by the stamp; a public constructor with a stamp
   parameter would still let hand-built values claim a parse, so the constructor is private and the
   only producer is `From(in PacketView)`.
3. **`CapturePacketProcessor`'s constructor takes the `AdapterSlotTable` as a required parameter**
   (design §7 says the classifier fetches the metadata by index). An optional parameter would let a
   composition forget the table and silently produce contexts with a null `AdapterId` — which would
   silently stop adapter-qualified policy rules from matching, i.e. a fail-open routing change. The
   compiler now forces every composition and test to state what it passes.
4. **`FlowBuilders.Context(…)`/`Adapter(…)`/`Process(…)` helpers were added to the test project** to
   keep the ~50 test construction sites readable; the production shape is unchanged (the helpers wrap
   the same interned metadata types, and the adapter metadata carries the key's own slot).
5. **`EveryDispatchedPacketCarriesAParsedLayout` became `EveryDispatchedFlowPacketCarriesAParsedLayout`**:
   the non-flow arm carries `default` by design (`ProcessAsync` passes no layout for an unparseable
   frame, and no consumer on that path reads one — the fragment handler parses the address pair
   itself), so asserting "every dispatched packet" would have been wrong. Recorded as a residual.
6. **The Step-6 series was not re-taken.** The instruction is explicit that the series is taken and
   extended, not redone; the decision is made on the existing before/after artifact plus the
   re-aggregated medians, and the verdict file records that a row-level A/B of the delta rewrite does
   not exist.
7. **A post-Step-7 `dispatcher` companion was taken** (`dispatcher-after-step7-{1,2,3}.csv`) because
   Step 7's effect (a smaller context copied per packet) is only observable there; the key/parse
   companions were not re-run because Step 7 is not on those paths (attribution discipline, §11.4).

**Residuals (to archive, not to smooth over)**

- The non-flow arm carries `default(PacketLayout)` by design; a future consumer of `packet.Layout` on
  that path must gate on `IsTcp`/`IsValid` like the five current consumers.
- The composition-level slot-exhaustion refusal is exercised at the table/generation-build level
  (`AnAdapterThatCannotBeInternedIsRefusedNotAliased`); the Windows enumeration path has no Linux
  harness, so the operator-visible `adapter.slot-exhausted` line is unverified end to end here.
- `ProcessMetadata` is allocated once per attributed claim; no 0 B gate drives that path (design §9
  risk 13) — a future gate over an attributed claim would see that one allocation.
- The `tcp-redirect-data-path` benchmark now calls the layout entry points production calls, so the
  before/after comparison is a product change plus the harness switch; the walk counts, not the
  timings, carry the parse-once claim (§11.4).
- The companion series were taken on a loaded host; their exact siblings are the gate classes, green
  in every run (`gate-stability.txt`).
- `dotnet format` and `jb inspectcode` were not run (hard constraint); the next session must run them
  before committing, and the tree is uncommitted by instruction.

**Step-8 residue greps** (`src/`, `tests/`, `benchmarks/`): `AdapterContext` → **0** code hits (the
only matches are the archived F2 patch text under
`benchmarks/results/2026-09-30-warm-path-lock-chain/step3-blocked.patch`, a predecessor's evidence
file, not live code); `OriginAdapterId` → **0**; `OriginAdapterGeneration` → the live key field, its
`ToString`, its equality compare and the two tests that assert it; `_sequenceGate` → **0** code hits
(one mention in this task's verdict text, which documents the removal).

**Validation run at the closing tree**

```text
dotnet build WinForward.slnx -c Release                              -> 0 warnings, 0 errors
dotnet test WinForward.slnx -c Release                               -> 1057 + 18 passed, 0 failed
HotPathAllocationGateTests 11 | WarmPathGateTests 25 | SweepAllocationGateTests 12
TcpRedirectWarmPathGateTests 2 | UdpWarmPathGateTests 7 | SelfTrafficWarmPathGateTests 4
FlowKeyShapeTests 1 | PacketLayoutTests 8 | PacketPathWalkCountTests 5
FlowContextMetadataTests 2 | TcpRedirectInjectionBatchingTests 14 | PackedFlowKeyTests 8
AdapterSlotTableTests 7 | SequenceTrackerTests 3 | CapturePumpReadCallTests 3 | NdisCapturePumpTests 14
```

