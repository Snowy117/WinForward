# F4 flow-key / parse-once / atomic-tracker evidence artifact

Task `.trellis/tasks/09-30-flow-key-parse-once` (finding F4 of the archived
`09-29-tcp-udp-path-structural-perf`). Base revision `ca8d999`
(`git write-tree` b6e93b5248ed5c67345fa1385643ba37a3d8bdcb). **Steps 0–7 and the defaulted-layout
hardening landed, uncommitted.** Step 6 (the optional IPv6 checksum delta) is **rejected by
measurement** — `step6-ipv6-delta-verdict.txt`.

Host / runtime (every series below): AMD Ryzen 9 9955HX (16 physical / 32 logical), NixOS 26.11,
.NET 10.0.12, Release. Allocation bytes and counts are gates; timing is a series comparison and the
recorded host rule applies (sub-2× deltas are noise). The AC-4 discharge is **per-leg
no-regression**, with the improvement and the IPv6:IPv4 ratio recorded as **readings, not gates**
(`hot-path.md` contract 9; `prd.md` AC 4 as restated).

## Commands

```bash
# before-series: pristine ca8d999 + the Step-1 probes, in a separate worktree (git worktree add /tmp/wf-f4-before ca8d999)
cd /tmp/wf-f4-before
for r in 1 2 3; do dotnet run -c Release --no-build --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRedirectDataPath*' --job short --artifacts /tmp/wf-f4-before-datapath-$r; done
for r in 1 2 3; do dotnet run -c Release --no-build --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short --artifacts /tmp/wf-f4-before-ftshape-$r; done
dotnet run -c Release --no-build --project benchmarks/WinForward.Benchmarks -- --filter '*Parser*' --job short --artifacts /tmp/wf-f4-before-parser
dotnet run -c Release --no-build --project benchmarks/WinForward.Benchmarks -- --filter '*Dispatcher*' --job short --artifacts /tmp/wf-f4-before-dispatcher

# after-series: the task tree (Steps 1-5), same commands with -after- artifact directories
# after companions were re-taken on a quiet host: /tmp/wf-f4-after2-{ftshape-1..3,parser,dispatcher}
# post-Step-7 dispatcher series (this session): /tmp/wf-f4-step7-dispatcher-{1,2,3}
for r in 1 2 3; do dotnet run -c Release --no-build --project benchmarks/WinForward.Benchmarks -- --filter '*Dispatcher*' --job short --artifacts /tmp/wf-f4-step7-dispatcher-$r; done
```

Note: the before worktree runs exit `1` even though every run completes (BDN's exporter/validation
exit code on the `--no-build` tree); the artifacts below are complete and the padded summaries are
in `/tmp/wf-f4-before-*.log`. The after runs exit `0`.

**Provenance of the files in this directory** (verified by md5 against the run directories):
`tcp-redirect-data-path-after-{1,2,3}.csv` is the 20:58–21:07 run;
`flow-table-production-shape-after-{1,2,3}.csv`, `parser-after.csv` and `dispatcher-after.csv` are the
21:15–21:25 **quiet re-take** (`/tmp/wf-f4-after2-*`), not the load-affected first after-run;
`dispatcher-after-step7-{1,2,3}.csv` is this session's post-Step-7 run.

## The AC-4 series — `tcp-redirect-data-path`, 3 runs, per-row medians

Rows are ns/op, `--job short`, 0 B allocated on every row. `ReverseLegForwarded` is the row the PRD
names. Mechanisms removed from these rows by this task: Step 2 deletes the two sequence-gate lock
entries per packet (the row calls `TrackServerSequence`); Step 3 replaces the row's re-parsing
sequence observation and span-revalidating rewriter with the layout-driven entries. The benchmark
was switched to the **same layout entry points production now calls** (the rows stopped re-parsing
a frame production had already parsed), so the before/after delta is the product change, not a
harness-only change — and the parse-only rows of the before-series are what the recorded 2.0× IPv6
row decomposed into (`research/implementation-notes.md` §7.2). The key steps (4–5) and the context
step (7) are **not** on this row and are not credited with its movement (attribution discipline,
`design.md` §11.4).

**Reading, not a gate.** Every row improves and **no row regresses**, so AC-4's per-leg
no-regression discharge holds. The AC-4 row `ReverseLegForwarded` improves 1.88× (IPv6, 128 B) and
1.68× (IPv6, 1400 B), 2.07× / 1.95× on IPv4. The **IPv6:IPv4 ratio moves the other way as a
reading**: 1.65 → 1.92 (1400 B) and 2.08 → 2.29 (128 B), because the removed work (parse, lock,
revalidation) is family-neutral while the remaining family-specific term is the IPv6 checksum
delta, which shrinks neither side proportionally. The **absolute** IPv6−IPv4 gap narrows (48.07 →
34.97 ns at 1400 B), which is the reading that keeps Step 6 open on paper and closed on measurement
(see below).

| row | family | frame | before median (3) | after median (3) | speed-up |
|---|---|---:|---:|---:|---:|
| ForwardLegForwarded | IPv4 | 128 | 91.52 ns | 21.72 ns | 4.21× |
| ForwardLegForwarded | IPv4 | 1400 | 104.12 ns | 35.22 ns | 2.96× |
| ForwardLegForwarded | IPv6 | 128 | 88.84 ns | 51.31 ns | 1.73× |
| ForwardLegForwarded | IPv6 | 1400 | 99.59 ns | 61.81 ns | 1.61× |
| ForwardLegHost | IPv4 | 128 | 112.45 ns | 27.79 ns | 4.05× |
| ForwardLegHost | IPv4 | 1400 | 125.45 ns | 42.14 ns | 2.98× |
| ForwardLegHost | IPv6 | 128 | 102.58 ns | 56.51 ns | 1.82× |
| ForwardLegHost | IPv6 | 1400 | 116.43 ns | 68.79 ns | 1.69× |
| ReverseLegForwarded | IPv4 | 128 | 53.86 ns | 25.99 ns | 2.07× |
| ReverseLegForwarded | IPv4 | 1400 | 74.00 ns | 37.90 ns | 1.95× |
| ReverseLegForwarded | IPv6 | 128 | 112.05 ns | 59.64 ns | 1.88× |
| ReverseLegForwarded | IPv6 | 1400 | 122.07 ns | 72.87 ns | 1.68× |
| ReverseLegHost | IPv4 | 128 | 63.49 ns | 26.98 ns | 2.35× |
| ReverseLegHost | IPv4 | 1400 | 77.02 ns | 40.74 ns | 1.89× |
| ReverseLegHost | IPv6 | 128 | 90.39 ns | 55.06 ns | 1.64× |
| ReverseLegHost | IPv6 | 1400 | 102.69 ns | 68.28 ns | 1.50× |

Raw per-run exports: `tcp-redirect-data-path-before-{1,2,3}.csv`, `tcp-redirect-data-path-after-{1,2,3}.csv`.

## Step 6 — the optional IPv6 checksum delta: rejected by measurement

`implement.md` Step 6 is an optional commit adopted only on a reading. It is **not adopted**; the
full reading, the ratio and gap numbers, the isolated probe figures (wide-load delta 22.1–22.6 vs
27.6–28.3 ns narrow; the family-specific rewriter delta +43…+49 ns) and the three reasons are in
`step6-ipv6-delta-verdict.txt`. In one line: every row improved, so the series cannot attribute
anything to a checksum-shaped change, and the only measured win for the rewrite is ≈5.5 ns on the
isolated delta — below this host's noise floor and not worth an unmeasured edit to the
transparent-redirect primitive.

## The key/parse shape companions (report-only)

`tcp-redirect-data-path` contains no key and no dictionary probe, so it must **not** be credited to
the key steps; `flow-table-production-shape` is where the packed key and the packed warm slot
function show. Per-row medians of 3 runs:

| row | before | after | reading |
|---|---:|---:|---|
| `ResolveWarmHit` (warm slot function + tuple corroboration) | 76.21 ns | 26.06 ns | 2.9× — the slot function and the corroboration tuple are packed, so the warm resolve materializes no `Endpoint` |
| `ResolveSameOrientationHit` (gated resolve) | 66.57 ns | 45.80 ns | 1.45× — 128 → 64-byte key, integer equality |
| `ResolveReverseAliasHit` (gated reverse resolve) | 69.23 ns | 57.53 ns | 1.20× |

`parser` is a raw CSV beside this README (shape witness, not a target; the parse-once claim is
carried by the exact walk counts, not by a timing row).

### Post-Step-7 dispatcher series (`dispatcher-after-step7-{1,2,3}.csv`, medians of 3)

The slim context is copied 2–3× per packet, so `dispatcher` is the companion where Step 7 can show.
`dispatcher-before.csv` is a single run, so this is a companion reading, not a controlled series:

| row | before | after Step 7 | reading | allocated |
|---|---:|---:|---|---|
| `WarmPassProductionAsync` | 207.60 ns | 151.40 ns | 1.37× | 160 B → 160 B |
| `WarmPassDisabledTraceAsync` | 205.50 ns | 142.00 ns | 1.45× | 160 B → 160 B |
| `WarmProxyProductionAsync` | 184.10 ns | 160.30 ns | 1.15× | 160 B → 160 B |
| `WarmProxyDisabledTraceAsync` | 236.00 ns | 129.10 ns | 1.83× | 160 B → 160 B |
| `ReverseCandidateSlowPathAsync` | 371.20 ns | 331.40 ns | 1.12× | 352 B → 264 B |

The gate-critical reading is unchanged: `WarmProxyDisabledTraceAsync` still allocates the same 160 B
as `WarmPassDisabledTraceAsync` (`hot-path.md`, SOCKS5 benchmark gate). The slow-path row's −88 B is
recorded as a reading, not decomposed here.

**Measurement caveat, stated rather than smoothed over** (and re-stated by the independent check, which
re-computed every median in this file from the archived CSVs and reproduced all of them exactly):

- **Attributable, exact (load-independent):** the walk counts, gate counts, struct sizes, the F2/F3
  gate classes and every allocation figure. These carry the parse-once, tracker and key-shape claims.
- **Attributable as a reading, with the mechanism named:** the `tcp-redirect-data-path` AC-4 series
  (3 before + 3 after runs, quiet host). The before/after comparison is a product change **plus** the
  harness switch onto production's layout entry points, and the mechanisms on the row are Steps 2 and 3
  only — Steps 4/5/7 are not credited with its movement.
- **Witness only, not a controlled series:** the `flow-table-production-shape` and `parser` companions
  (their archived CSVs are the *quiet re-take* `/tmp/wf-f4-after2-*`; the first after-take was
  load-affected and discarded, so the caveat belongs to that discarded run, not to these files), and
  the post-Step-7 `dispatcher` series (3 after-runs against a **single** `dispatcher-before.csv`).
  The `parser` row is a single run. Their direction and magnitude agree with the named mechanisms, but
  no threshold is claimed from them.

## Struct sizes: the Step-7 target landed exactly

`FlowContext` **80** and `CapturedFlowPacket` **152** are the design's numbers, not re-derived ones:
`FlowKey` 64 + two 8-byte metadata references = 80; the packet loses the same 24 B. Asserted by
`FlowKeyShapeTests.StructSizesForDiagnostics`; full table in `struct-sizes-after.txt`.

| type | before | after Steps 1–5 | after Steps 6–7 | target |
|---|---:|---:|---:|---:|
| `FlowKey` | 128 | 64 | **64** | 64 |
| `FlowContext` | 168 | 104 | **80** | 80 |
| `CapturedFlowPacket` | 224 | 176 | **152** | 152 |
| `FlowStateView` | 160 | 96 | **96** | 96 |
| `PacketView` | 80 | 96 | 96 | (88 estimated; `UInt128` alignment rounds to 96) |
| `Endpoint` | 48 | 48 | 48 | — |
| `PacketLayout` | — | 16 | 16 | 16 (the validity stamp fits the padding) |

## Hardening: a defaulted layout is refused, not applied

`default(PacketLayout)` reads as a valid TCP layout (`PacketTransport.Tcp == 0`, family IPv4), so a
hand-built packet without a layout used to be rewritten at the IP header's own offset. `PacketLayout`
now carries an explicit validity stamp that only `From(in PacketView)` writes, and every consumer
gates on `IsTcp` — including the coordinator's SYN test, so a layout-less packet can never open a
redirect (it takes the data path, which refuses and blocks). Red-before and green-after, with the gate
that had been passing on the mis-rewrite: `defaulted-layout-hardening.txt`.

## Spec-row → proof map

| Spec row (`.trellis/spec/backend/`) | After this task | Proof |
|---|---|---|
| `hot-path.md` contract 8 ("flow keys hash flat") | no reference-typed key field; `OriginAdapterSlot` + `OriginAdapterGeneration` + both scope ids compared; packed hash entry point whose hashed field set stays transport-only | `FlowKeyFitsOneCacheLineAndHasNoReferenceTypedFields` (size bound + no reference-typed field), `FlowKeyShapeTests.StructSizesForDiagnostics` (exact 64), `FlowKeyPackedRoundTripsEndpoints`, `ReverseSwapsEndpointsAndScopes`, `PackedAndMaterializedHashesAgree` |
| `hot-path.md` F2 warm cache (packed slot function) | slot function reads packed fields; `TransportTuple` packed (48 B), same field set | `PackedAndMaterializedCanonicalHashesAgree`, `CanonicalSlotIsOrderIndependent`, `FlowTableTransportTupleIsUniqueAcrossOrigins` / `WarmCacheHitServesTheValidatedView` (forward + reverse corroboration), F2 facts unchanged. The planned direct field-set fact `TransportTupleEqualsTheKeysTransportFields` does not exist (`TransportTuple` is a private nested type) — see the residual below |
| `hot-path.md` contract 4 ("packets are structs") | `PacketLayout` (16 B) on the packet; recorded sizes 64 / 80 / 152 / 96 | `FlowKeyShapeTests` |
| `hot-path.md` measurement discipline (contract 9) | key/parse claims are exact counts and sizes; AC-4 improvement and ratio are readings | this README + `walk-and-gate-counts.txt` |
| `hot-path.md` contract 11 (defaulted layout hazard) | validity stamp + `IsTcp` gate; production always carries a parsed layout | `defaulted-layout-hardening.txt` |
| `quality-guidelines.md` (endpoint rewrite primitive) | layout overload keeps `IsTcp`, `frame.Length >= TransportEnd` and family equality; span entry point is the oracle | `ViewDrivenRewriterRejectsWhatTheSpanRewriterRejects`, `DefaultedLayoutIsRefusedByteIdenticallyRatherThanRewritten` |
| `quality-guidelines.md` (one shared hash expression) | `FlowHash.CombinePacked` behind `Combine`, `CombineCanonical` and both packed callers; `TransportTuple.GetHashCode` still delegates | the two agreement facts |
| `tcp-local-redirect.md` (TFO SYN / sequence tracking) | `IsTcpSyn` and both sequence observations consume the layout; trackers are CAS-max `long` (−1 unobserved) | `LayoutSynTestMatchesTheSpanTest`, `RedirectPacketTakesZeroSequenceGateEntries`, `ConcurrentSequenceObservationsKeepTheLargerValue` |
| `windows-ndisapi.md` (adapter identity) | slot table interns the GUID-primary `StableId`; a capture-scope adapter that cannot be interned is refused | `AdapterSlotTableRoundTripsStableId`, `TryInternIsIdempotentPerStableIdAndSlotsAreNeverReused`, `AdapterSlotSurvivesARefresh`, `AnAdapterThatCannotBeInternedIsRefusedNotAliased` |
| `udp-relay.md` (host-flow response adapter binding) | `Resolve(ushort slot)` over a slot-indexed array built from the same scope | `ScopeHeadBecomesHostFallbackAndEachAdapterResolves`, `AdapterIdsResolveThroughTheSlotTable`, `ResolveReadsTheLatestSnapshotAfterUpdateWithoutReconstruction` + the three response-target tests |
| `traffic-policy-lifecycle.md` (policy domains) | policy reads the same five properties, now backed by interned metadata + the packed key | policy suites unchanged; `FlowContextMetadataTests` |

## Exact evidence

- `walk-and-gate-counts.txt` — struct sizes, header walks 4/4 → 1 parse + 1 view rewrite per leg,
  sequence gate 2 → 0, defaulted-layout refusals; every command.
- `defaulted-layout-hardening.txt` — the hazard, the red-before text, the fix, the green-after text,
  and the independent check session's reproduction of the red with the same signatures.
- `step6-ipv6-delta-verdict.txt` — the AC-4 readings, the ratio/gap numbers and the Step-6 verdict.
- `struct-sizes-before.txt` / `struct-sizes-after.txt` — the measured sizes and counts.
- `class-totals.txt` — padded totals for every class this task grew, with the mapping against the
  Steps 1–5 values, the unchanged `hot-path.md` totals string, and the independent check session's
  per-class re-derivation (working-tree fingerprints included, because `git write-tree` is the base
  tree in this uncommitted state).
- `gate-stability.txt` — one run per allocation/structure class (no gate failed; the 20-run procedure
  was not re-derived), the discrimination re-proofs this session ran, and the check session's
  42 process runs over 17 classes (4 runs per exact gate, 2 per structural/grown class; 0 failures,
  0 vacuous totals assertions).

## Independent check session (2026-09-30)

An independent check sub-agent verified the landed work and fixed what it found; everything is
uncommitted. Summary (details in `implement.md`'s C1–C7 rows and the appended artifact sections):

- **Verification**: Release build 0 warnings / 0 errors; full suite **1057 + 18 passed, 0 failed**
  (3 consecutive runs at the fixed tree; one earlier transient failure did not reproduce — residual).
  Every AC-4 median in this README was **re-computed from the raw CSVs and reproduces exactly**
  (worst row 1.50×, no regression, 0 B on every row; ratios and gaps reproduce). The companion series
  reproduce too. The walk-count red (3 parses + 1 revalidation per leg) was re-derived from the
  pre-change source; the defaulted-layout red was reproduced by neutralizing the stamp gate and the
  file was restored byte-identically (md5-checked).
- **Fixes**: C1 the `UdpAdapterTargetSource` seed snapshot's throwaway slot table (a real latent bug:
  a constructor-supplied map reported no adapter IDs); C2 the packed-canonical agreement fact's
  self-referential oracle, which AC-1 required to catch a self-consistent low/high swap and did not;
  C3–C7 citation/wording corrections plus one reject-path assertion added to the layout truncation
  corpus. No gate, threshold or existing assertion was relaxed; no production behaviour changed except
  C1's diagnostic surface.

## Status / residuals

- Full suite **1057 + 18** green (one run at the closing tree, after every change); Release build
  zero-warning. `dotnet format` / `jb inspectcode` are the operator's gates and were **not** run
  (hard constraint); nothing is committed.
- Steps 0–5 were landed by the previous agent; this session added the defaulted-layout hardening,
  Step 7, the Step-6 verdict and the Step-8 evidence/spec/record updates.
- **The non-flow arm carries no layout by design** (`CapturePacketProcessor.ProcessAsync` passes
  `default` for a frame that did not parse), and no consumer on that path reads one — the fragment
  handler parses the address pair itself. A future consumer of `packet.Layout` on the non-flow path
  must gate on `IsTcp`/`IsValid`, exactly like the five current consumers; the hardening fact covers
  the flow arm only.
- **The composition-level slot refusal is not exercised end to end on this host**:
  `AnAdapterThatCannotBeInternedIsRefusedNotAliased` pins the refusal at the **table** level only
  (exhaust the slot space, assert the next `TryIntern` returns false with `NoSlot`). The
  generation-build exclusion loop in `NdisCaptureGenerationFactory.Create` — the code that drops a
  refused adapter from the capture scope and emits `adapter.slot-exhausted` — has **no test at all**
  (the factory is Windows-only and needs a real driver), so both that exclusion and the
  operator-visible log line are unverified here. No packet can carry `NoSlot`: nothing else interns.
- **`ProcessMetadata` is allocated once per attributed claim** (`AttributeProcessAsync`), which no
  0 B gate drives: the dispatcher's gate claims before its measured window and the flow-table claim
  gate passes a pre-built context (design §9 risk 13). A future gate over an attributed claim path
  would see that one allocation.
- **The benchmark now follows production onto the layout entry points.** The before/after
  `tcp-redirect-data-path` comparison is therefore a product change *plus* the harness switch; the
  parse-only rows of the before-series are what the recorded 2.0× decomposed into, and the walk counts
  (not the timings) carry the parse-once claim.
- **The companion series were taken on a loaded host** (see the caveat above); their exact siblings
  are the gate classes, which were green in every run.
- **`TransportTuple`'s per-field equality has no direct discrimination fact.** The planned
  `TransportTupleEqualsTheKeysTransportFields` (design §2.5) was not written: `TransportTuple` is a
  private nested type, so asserting its field set directly would need a visibility change (out of
  scope). What is pinned is the observable corroboration (`FlowTableTransportTupleIsUniqueAcrossOrigins`,
  `WarmCacheHitServesTheValidatedView`); the per-field equality — notably the two scope ids, which the
  canonical slot hash deliberately excludes — rests on construction and review. A future warm-cache
  change should add a scope-differing key fact.
- **`AdapterSlotTable.Observe` was deleted.** The design specified it as the per-enumeration refresh seam, but the generation build refreshes through `TryIntern` (it holds the stable ID), so the by-slot method had no caller anywhere; the operator had it removed with the format-gate round rather than left as unused public API. `TryIntern` is now the table's only writer.
- **The validity stamp authenticates a derivation, not a parse.** `PacketLayout.From(in PacketView)`
  is the only producer of a valid layout, but `PacketView`'s constructor is public, so a hand-built
  view yields a valid-stamped layout. The hardening's contract is that `default(PacketLayout)` can
  never be applied; it is not a proof that every valid layout came from `IPTcpUdpPacket.TryParse`.
- The F2 re-proof facts are green: `WarmPathGateTests` (25), `TcpRedirectWarmPathGateTests` (2),
  `UdpWarmPathGateTests` (7), `SelfTrafficWarmPathGateTests` (4), `HotPathAllocationGateTests` (11),
  `SweepAllocationGateTests` (12 — F3 untouched).
