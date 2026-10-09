# Warm-Path Dispatch: the synchronous entry, its resolves, and the self-traffic split

> The synchronous per-packet entry — what it resolves, what it must not touch, and the counts that
> prove it. Part of the [hot-path family](./hot-path.md); read it when you change the dispatcher's
> warm shape, a reverse/original probe, the UDP ready path, or the activity stamp.

## No async state machines on the steady-state path

- A fat async method (large struct locals hoisted into the state machine) heap-allocates per call
  even when it completes synchronously (~193 B/op measured). `FlowDispatcher.DispatchAsync` is a
  **non-async** entry that runs the synchronous warm shape and returns the executor's `ValueTask`
  directly; everything else falls into `DispatchSlowAsync`.
- The warm shape is: trace-off ∧ reverse-diversion declined ∧ no wildcard self-traffic tuple ∧
  resolved ∧ (Pass ∨ Block ∨ Proxy-with-inline-target-hit).
- `FlowAction` has exactly the values {Proxy, Pass, Block}, so no defensive "unknown action" gate is
  needed after the proxy branch — a removed always-false gate taught that lesson.
- **Any new per-packet stage must follow the same split**: run inline on the warm entry or fall to
  the slow path. There is no third shape.
- The dispatcher's proxy branch stays warm for the product's main path (task
  08-29-socks5-perf-fullpath C2b): a resolved proxy decision whose `TargetName` hits the `_targets`
  map is served on the warm entry, while an unresolved server name fails closed through the slow
  path. Measured 352 B → 160 B and 596 ns → 258 ns per packet.

## Reverse diversion

- `ITcpReverseHandler.WantsPacket(in CapturedFlowPacket)` decides the diversion (task
  08-30-hot-path-revival X1): TCP ∧ src port ∈ active listener-port set. `TcpRedirectTable` keeps an
  `int[65_536]` reference count per port — Inc in `TryClaim` under the gate before SYN injection, Dec
  in `TryRemove`/`RemoveUnderGate`, queried with `Volatile.Read != 0`.
- `WantsPacket` is **only** the warm-entry diversion precheck: the slow path's
  `TryHandleReverseAsync` always calls the full handler (protocol gate + the single
  `TryResolveByReverse` fold probe + tombstone), so prefilter misses degrade to the slow path, never
  to wrong routing — listener-shaped tuples cannot resolve in any `FlowTable.TryResolve` mode, pinned
  by the tombstone-straggler test.
- F2 (2026-09-30) made a warm packet pay **one** reverse probe and **zero** redirect gate entries:
  the coordinator resolves once via `TryResolveByReverse` and passes the association into
  `HandleReverseAsync`, which no longer probes; `HandlePacketAsync` keeps the listener-port prefilter
  in front of it.
- The prefilter is a candidate filter, not a forward-direction absence proof: client source ports and
  listener ports are both OS-assigned ephemerals, so a forward flow whose source port number is itself
  a live listener port pays one gated reverse probe per packet for as long as that listener exists
  (one gate entry fewer than the pre-change candidate+resolve pair) and then falls through to the
  original index. A cache collision costs the same one gated probe on either direction.

## The self-traffic split

- Loop prevention is fail-closed, and F2 moved the per-packet lookups off the global gates. The warm
  entry answers the **wildcard relay-socket half only** — `ISelfTrafficGuard.IsWildcardOwned`, two
  lock-free `ConcurrentDictionary` probes. The **exact-tuple half runs once per claim**
  (`TryHandleSelfTrafficAsync`), because a self-owned exact tuple never produces a flow-table state.
- Dropping the wildcard half is forbidden: `TcpProxyRelay` registers `(Tcp, Any:port, proxyEndpoint)`
  before its SYN, and a recycled ephemeral port would otherwise resolve a stale proxy state and
  redirect WinForward's own control connection into its own proxy
  (`ARelayWildcardTupleIsNeverProxiedOnAWarmHit`, red against the naive-deletion variant).

```csharp
// Wrong: leave the whole self-traffic check off the warm entry — a recycled ephemeral port then
// resolves a stale proxy state and redirects WinForward's own relay connection into its own proxy.
if (_selfTraffic.IsOwned(packet.Context)) return DispatchSlowAsync(packet, cancellationToken);

// Correct: the warm entry answers the wildcard half lock-free; the exact half runs once per claim.
if (_selfTraffic.IsWildcardOwned(packet.Context)) return DispatchSlowAsync(packet, cancellationToken);
```

- The exact-tuple half's frequency (F8, 2026-10-01): a flow whose attribution is pending has no
  claimed state yet, so each of its packets re-enters the full slow path — including the gated
  exact-tuple check — once per packet instead of once per claim. That is bounded by the pending ring
  (32 packets) and the retention TTL (5 s), counted by the pipeline's delivered-packet counter, and
  it is the recorded price of keeping the claim on the pump; an admission-side short-circuit is a
  recorded non-goal (`FlowAttributionPipelineTests`).

## Warm resolves take no global gate

- `FlowTable.TryResolveWarm` (one volatile slot read + the seqlock snapshot + tuple corroboration),
  `TcpRedirectTable.TryResolveByReverse`/`TryResolveByOriginal` (direct-mapped caches validated by
  immutable association fields, falling back to the gated authority),
  `ISelfTrafficGuard.IsWildcardOwned` and the UDP ready path's session cache are lock-free. A cache
  miss is always a fall-back to the unchanged gated path, never an approximation.
- UDP's ready path is wait-free and gate-free: `TrySendSpanAsync` resolves the ready session from the
  pre-allocated cache (validated by `Session.Flow`), otherwise takes the admission path, which keeps
  the cooldown probe, the clock read, the capacity check and the re-check of `slot.Ready` under
  `_gate`. `UdpProxySession.SendSpanAsync`/`TouchActivity` no longer take `_activityGate`:
  `_scope.TryEnter` is the admission authority and the scope's drain joins outstanding leases. The
  propagation sentinel is `long.MinValue` (bucket 0 is a real bucket), and `TryBeginExpiry` re-checks
  in bucket space under the gate it still holds.
- F4 keeps this contract with the packed key: the slot function and `Matches` read packed fields (no
  `Endpoint` materialization), the adapter slot the key carries is interned at classification and
  never resolved on a warm probe, and `FlowKey.IsReverseOf` serves the endpoint-swap test without
  materializing four `Endpoint`s — see
  [packet-shape-and-flow-identity.md](./packet-shape-and-flow-identity.md).
- **Counts are the proof.** Per warm hit: registry gate 1 → 0, exact-tuple guard probes 1 → 0,
  flow-table gate 1 → 0, redirect gate 2 → 0 (one reverse probe), UDP coordinator gate 1 → 0, session
  activity-gate entries 2 → 0, clock reads 2 → 0. The parked-gate facts
  (`WarmResolveCompletesWhileFlowTableGateIsHeld`, `ReverseResolveCompletesWhileRedirectGateIsHeld`,
  `UdpReadySendCompletesWhileCoordinatorGateIsHeld`) prove the removal structurally; every count and
  its before/after text is in
  `benchmarks/results/2026-09-30-warm-path-lock-chain/warm-path-gate-counts.txt`.
- Production-composition benchmarks (`WarmPassProductionAsync`/`WarmProxyProductionAsync`, real
  predicate over a fake handler) gate the warm shape at 160 B — handler-less benchmarks alone proved
  nothing once X1 made the warm entry dead code in production. The gate:
  `WarmProxyDisabledTraceAsync` = 160 B, equal to `WarmPassDisabledTraceAsync`; additionally
  `FrameRewriterBenchmarks` must stay at 0 B for all methods.

## Sequence trackers are atomic, not locked

`TcpRedirectAssociation`'s two trackers are `long` (−1 = unobserved) written by a CAS-max loop over
the unchanged wrap-aware `IsSequenceAhead` predicate and read with `Volatile.Read`. The association
holds no reference-typed instance field (no `Lock`), so a redirected forward+reverse packet pair
takes **zero** gate entries and no per-association lock allocation; the cold RST readers tolerate a
weakly consistent value by construction. Facts: `RedirectPacketTakesZeroSequenceGateEntries`,
`TcpRedirectAssociationHoldsNoLockField`, `ConcurrentSequenceObservationsKeepTheLargerValue`,
`UnobservedTrackerReadsNullAndObservedZeroReadsZero`. The redirect shapes that write these trackers
are [tcp-redirect-transform.md](./tcp-redirect-transform.md); the close paths that read them are
[tcp-client-close-injection.md](./tcp-client-close-injection.md).

## The activity bucket

- **One bucket, 500 ms, one clock per composition.** `ActivityBucket.TicksPerBucket` is 500 ms (the
  binding half of the bounded-hold requirement is "≥8 buckets per retention window"; the 5 s session
  floor makes an older "≥1 s" reading unsatisfiable). `ActivityBucketClock` is created **once** in
  `DurableCaptureBundle`, threaded into the `FlowDispatcher`'s `FlowTable`, the `TcpProxyCoordinator`
  and the `UdpProxyCoordinator`/`UdpProxySession`, and advanced by three sources: the pump's
  per-iteration callback (`DurableCaptureBundle.FlushPendingInjections`, the only `OnBatchCompleted`
  chain point), every claim (`FlowState.Reset` via `Tick()`) and every sweep (`RemoveExpired`
  publishes the instant it was handed, so the cutoff and the stamps cannot drift inside a call). A
  missed wiring freezes the bucket and expires active flows — pinned by
  `DurableCaptureBundleTests.ActivityBucketClockTicksExactlyOncePerPumpIteration` (one tick per pump
  iteration, empty polls included, zero per packet) and by the throwing-clock facts.
- **No clock read on the warm path.** A hit stores or derives the bucket: `FlowState.TouchBucket`,
  `TcpRedirectAssociation.Touch` and `UdpProxySession.TouchActivity` each write one integer; the TCP
  coordinator's per-packet instant is `ActivityBucket.ToUtc(clock.Current)` and the UDP ready path
  reads none. The tombstone, setup-cooldown, pending-SYN and setup-queue stamps keep the real clock —
  their windows are not bucket comparisons.
- **Never early.** Every sweep compares the **internal integer bucket** with a strict `<` cutoff
  (`ActivityBucket.Cutoff(now, idleTimeout)`), so retirement lands in
  `(idleTimeout, idleTimeout + w]`; `idleTimeout <= TimeSpan.Zero` keeps "retire everything on this
  call". `LastActivityUtc` is derived and only for logs and tests.

```csharp
// Wrong: compare activity in DateTimeOffset space per entry, or with `<=` (retires up to one
// bucket early).
if (now - association.LastActivityUtc >= idleTimeout) Remove(association);

// Correct: one bucket cutoff per call, strict `<` on the internal integer bucket.
var cutoffBucket = ActivityBucket.Cutoff(now, idleTimeout);
if (association.BucketForDiagnostics < cutoffBucket) RemoveUnderGate(association);
```

## FlowTable's warm cache and chunked sweep

- **Warm cache (task 09-30-warm-path-lock-chain).** `FlowState?[] _warm` is a pre-allocated
  direct-mapped cache over the gated `Dictionary` authorities: `clamp(capacity × 64, 4,096, 262,144)`
  slots, rounded up to a power of two, allocated once in the constructor and never grown. A hit is
  served only after the seqlock snapshot and the exact tuple corroboration, so a collision, an
  unpopulated slot or a torn read is a **false miss** (the unchanged gated path), never a wrong
  decision.
- One window is inherent to the shape and is stated rather than smoothed over: a probe that loaded
  the slot reference before a concurrent sweep's `ReferenceEquals`-guarded clear can still return the
  triple it validated — the decision the flow had when the slot was read; the reader whose snapshot
  lands after the recycling `Reset` gets a false miss.
- Population at claim, write-through on a gated hit and the `ReferenceEquals`-guarded clear in the
  sweep's removal hold are the only writes, all under `_gate`. The authorities' `Count`/`Capacity`
  stay exact (no maintained counter) and no per-claim allocation is added —
  `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` is the acceptance gate (the reverted
  `ConcurrentDictionary` variant measured 488 B/claim+expire and is the recorded comparator).
- F4 re-proved the cache under the packed key: `FlowTable.SlotOf` reads
  `FlowHash.CombineCanonicalPacked` over the key's packed halves, `TransportTuple` is a packed value
  with the same field set, and `Matches` still corroborates by tuple equality, so packed-hash drift
  can only cost a warm slot, never a wrong answer (`PackedAndMaterializedCanonicalHashesAgree`,
  `CanonicalSlotIsOrderIndependent`, `FlowTableTransportTupleIsUniqueAcrossOrigins`,
  `WarmCacheHitServesTheValidatedView` for the forward and reverse corroboration, and
  `FlowTableWarmResolveAllocatesNoManagedBytes` re-run unchanged). The tuple's field-set equality has
  no dedicated discrimination fact (a private nested type): a field dropped from `TransportTuple.Equals`
  would only be caught if it also changed an existing fact's outcome — recorded residual.
- **Live-slot registry + chunked sweep (task 09-30-expiry-sweep-bounded-pause).** The table keeps
  `_liveStates[0.._liveCount)` as a hole-free mirror of `_states` (append in `TryClaimResolved` under
  `_gate`; removal is a swap-remove **at the cursor** that happens strictly **before** `ReturnState`,
  so a recycled state can never keep a registry slot; `LiveStateCountForDiagnostics` takes `_gate` so
  a churn test can assert it equals `Count`). `RemoveExpired` walks that registry at **minimal hold
  granularity**: a scan hold examines ≤ `SweepChunkEntries` (256) entries and stops on the first
  idle-elapsed candidate, the `isHeld` predicate runs with **no table lock held** (a held entry keeps
  its original idle point), and a removal hold re-checks identity/idleness/key-presence and removes
  exactly one entry, so the removal lands at the cursor and the swapped-in tail is examined next (no
  rewind, no batch). Deadline semantics: **expired is `ActivityBucket < cutoffBucket`**, where
  `cutoffBucket = ActivityBucket.Cutoff(now, idleTimeout)` is computed once per call from the argument
  and `idleTimeout <= TimeSpan.Zero` yields `long.MaxValue` (the drain call sites keep "retire
  everything on this call"). The strict `<` is load-bearing: a stamp is quantised **down** to its
  bucket, so retirement lands in `(idleTimeout, idleTimeout + 500 ms]` — never early, at most one
  bucket late — while `<=` would retire up to one bucket early; the comparison always reads the
  internal integer bucket, never `LastActivityUtc`. `SweepChunkEntries` is a scan bound only — one
  entry per removal hold whatever it is — and the sweep's own duration is report-only, because every
  removal takes its own gate hold.

---

## Moved out of this document

Two sections that used to live here are cold-edge or policy material rather than warm-path material:

- The owner-table snapshot cache (coalescing, freshness, the per-kind reuse asymmetry, the wake
  signal) is now [traffic-policy-lifecycle.md](./traffic-policy-lifecycle.md), "Process Attribution:
  The Owner-Table Cache".
- The forwarded-leg address rules (`ForwardLocalAddress` cached at the cold edge, the indexed
  `SwapEthernetMacs`, the fail-closed `Blocked` shape) are now
  [tcp-redirect-transform.md](./tcp-redirect-transform.md), "Cold-edge addresses are cached once".
