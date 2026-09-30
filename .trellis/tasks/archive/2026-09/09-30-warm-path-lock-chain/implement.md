# Implementation plan — F2 warm-path lock chain: lock-free resolve, self-traffic reorder, UDP ready path

One instrumentation step that lands **first** (the new benchmark arm and the diagnostics probes exist
before the product changes, so before and after are the same instrument), then five independently
revertible product steps, then the evidence/spec work. Every product step lands its exact proof in the
same commit, so every commit leaves a green, shippable tree; the transient red readings are captured
during the step and recorded in the artifact README.

Prerequisites already true: `dotnet` on PATH via direnv, `rg`/`fd`, the archived baselines under
`benchmarks/results/2026-09-29-benchmark-coverage/`, and the F3 artifact under
`benchmarks/results/2026-09-30-expiry-sweep-bounded-pause/`.

**Read before starting**: `research/implementation-notes.md` (the exact sequences, the hazard list and the
recorded discrepancies D1–D19) and `design.md` §2–§6. Three restatements change what the work *is*, and
the operator has amended the PRD to carry them, and then replaced requirement 1's mechanism after the concurrent index was measured and rejected (D1/D1b): requirement 2's
split into an exact half that leaves the warm path and a wildcard half that stays (D2/D13/D14), and the
bucket's 500 ms / never-early / zero-timeout rules (D4/D15/D17).

## Step 0 — Preflight

- [ ] Re-verify every `file:line` anchor in `research/implementation-notes.md` §1–§6 against the current
      source (`rg -n` the named members; they were read at planning time on tree `c6f58b7`).
- [ ] Record the current suite totals so a green tree is proven before the first edit:
      `dotnet test WinForward.slnx -c Release` (F3's check session recorded `995 + 18`; refresh — two
      figures move whenever a fact is added) and the per-gate proof totals string
      (`hot-path.md:1198-1216`, totals string at `:1201`, prose note at `:1194`:
      `HotPathAllocationGateTests:11 CapturePumpReadCallTests:3
      SweepAllocationGateTests:12 NdisCapturePumpTests:14`).
- [ ] Confirm the four referenced baselines are on disk and quote their recorded numbers into the new
      artifact README's "before" section: `scaling-contention.jsonl`, `udp-ready-path-contention.csv`,
      `flow-table-production-shape.csv`, `tcp-redirect-data-path.csv`.
- [ ] `git status` clean; create the branch/worktree the operator wants; note the base revision in every
      artifact header.
- [ ] Enumerate the call sites the API change will touch so no red compile is discovered late:
      `rg -n 'TryResolve\(|TryClaimResolved\(|LastActivityUtc|_timeProvider.GetUtcNow\(\)' src/ tests/ benchmarks/`
      (expected: `FlowDispatcher` 2 resolve + 1 claim; `CoreFlowStructuresTests` ~10 claims;
      `SweepAllocationGateTests` ~4; `HotPathAllocationGateTests` 2; `FlowTableBenchmarks` 3;
      `UdpProxySessionTests` 2 facts; `TcpProxyCoordinatorRewriteTests:31`).

## Step 1 — Instrumentation first: the warm arm, the probes, the before-artifact  [LANDED]

Nothing here changes product behaviour; all of it is report-only or diagnostics-only.

- [ ] `benchmarks/WinForward.Benchmarks/BenchmarkShared.cs`: `NeverOwnedGuard` gains
      `IsWildcardOwned(FlowContext) => false`, and a counting guard records the two halves separately
      (exact checks, wildcard checks) so Step 4's exact-count facts have an instrument.
- [ ] `benchmarks/WinForward.Benchmarks/Stability/ScalingContentionScenario.cs`:
      - add a third arm `warm`: the real registry populated (32 exact tuples) and the per-lookup unit
        `!guard.IsWildcardOwned(context) && table.TryResolveWarm(key, out _)` — the post-reorder production
        shape, retained wildcard half included. The arm **counts its own hits and misses** (the warm
        probe's return value) and the verdict row carries them, so the ratio is attributable to the
        measured miss rate (`design.md` §2.5). Keep the existing `fake`/`real` arms byte-for-byte as the
        recorded comparators (report-only), and state in the class XML doc that only the warm arm carries
        the acceptance figure and that the fake arm cannot show the reorder (measured, `README.md`
        finding 1).
      - **fix the arm-count arithmetic**: `windowSeconds = Math.Max(1, options.DurationSeconds /
        (2 * threads.Length))` (`:41`) hardcodes two arms; with three it must be
        `arms.Length * threads.Length`, and the series must run `--duration 63` so every configuration
        keeps the recorded 7 s window.
      - `BuildVerdict` gains `warmResolve` with `resolutionsPerSecond`, the self-normalised
        `scalingRatio` (`:224`, unchanged formula) **and** `ratioVersusRecordedBaseline =
        rps(i) / (i * 3_331_758.3)` — both readings in the row, neither re-derivable by hand.
- [ ] `ScalingContentionScenario`: keep the `note` honest — `gated: false`, and state that the
      self-normalised ratio's denominator rises with the fix, so both readings are recorded.
- [ ] Add the diagnostics probes (production-neutral, null in production, the `HoldProbe` shape from
      `FlowTable.cs:78-83`):
      - `FlowTable.GateHoldProbe` (`internal Action?`), invoked immediately after `_gate` is taken in
        `TryClaimResolved` and in `RemoveExpired`;
      - `TcpRedirectTable.GateHoldProbe` + `GateEntryCountForDiagnostics` (a plain counter incremented
        behind the null check), invoked after every `_gate` acquisition;
      - `UdpProxyCoordinator.GateHoldProbe` + `GateEntryCountForDiagnostics` at the same points;
      - `UdpProxySession.ActivityGateHoldProbe` (the Step 6 before/after counter). Clock reads are counted
        with an **injected** counting `TimeProvider` in tests — no production counter is added for them.
- [ ] `cd benchmarks && mkdir -p results/2026-09-30-warm-path-lock-chain`; capture the **instrumented
      before** series at unmodified product HEAD, one run per file then concatenated (the runner
      truncates its `--output` per process, F3 finding 19) — `--duration 63` once the arm count is fixed:
      `for r in 1 2 3; do dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario scaling --quick --flows 4096 --duration 63 --output /tmp/wf-warm-before-$r.jsonl; done; cat /tmp/wf-warm-before-{1,2,3}.jsonl > benchmarks/results/2026-09-30-warm-path-lock-chain/scaling-contention-before.jsonl`
- [ ] Capture the before probe counts with the new facts (written in Step 2 below, but they are red by
      construction here): the registry 1 gate / ≤4 probes per warm hit (of which 2 are the wildcard pair),
      the flow table 1 gate + 1 clock, the redirect table 2 gate entries + 2 reverse probes per warm TCP
      packet, the UDP coordinator 1 gate + 1 clock + the session's 2 activity-gate entries per ready
      datagram. Record them in `warm-path-gate-counts.txt` with the command, the tree revision and the
      exact assertion text of each red fact. **The warm arm's before-run drives `TryResolveWarm`'s
      pre-change stand-in** (a `TryResolve` alias until Step 3 lands) so the before/after numbers come from
      the same arm shape — and the three before-ratios (0.161 / 0.154 / 0.153, warm arm 3.29 M/s at four
      threads) are already recorded.
- [ ] Start `benchmarks/results/2026-09-30-warm-path-lock-chain/README.md`: host/runtime header, the
      command lines, the "before" table (the four recorded baselines plus the new arm's before-numbers),
      the two-readings rule for the scaling ratio, and the note that the new arm's before-number is the
      *locked* path because the arm landed first.

Validation for the step: the scenario builds and the `warmResolve` verdict block appears with
non-vacuous counters in all three thread configurations; the probes exist and are null in production; the
before-artifact exists. No product file is touched.

Rollback: revert the scenario + probes and delete the before-artifact (nothing depends on it yet).

## Step 2 — Activity representation: `ActivityBucket` + `ActivityBucketClock` + `FlowState` — **rollback point A**  [LANDED]

- [ ] `src/WinForward.Core/ActivityBucket.cs` (new): `ActivityBucket` (`TicksPerBucket = 500 ms`,
      `FromUtcTicks`, `FromUtc`, `ToUtc`) and `ActivityBucketClock` (`Current` = `Volatile.Read`,
      `Tick()` = exactly one `TimeProvider.GetUtcNow()`, `Publish(DateTimeOffset)` = **no clock read**,
      `BucketOf`), with the width derivation in the XML doc (`design.md` §3.1: `ConfigurationLimits.cs:58`
      gives a 5 s floor, so F3 item 4's "≥1 s" and "≤ idle/8" cannot both hold; 500 ms keeps ≥8 buckets).
- [ ] `src/WinForward.Core/Domain.cs`: `FlowState` gets `long _activityBucket` + `int _version`;
      `LastActivityUtc` becomes derived (`ActivityBucket.ToUtc(...)`); `Touch(DateTimeOffset)` writes the
      bucket; `internal TouchBucket(long)`; `Reset(key, decision, generation, activityBucket)` brackets its
      field writes with the **barrier-correct seqlock** of `design.md` §2.3 —
      `Volatile.Write(odd)` → `Interlocked.MemoryBarrier()` → fields → `Interlocked.MemoryBarrier()` →
      `Volatile.Write(even)`, monotone `++`, never reset to 0. `Volatile.Write` alone is release-only, so
      without the two full fences a reader on ARM64 can accept a mixed triple while both version reads see
      the old even value; x86 TSO hides the bug, which is why the barriers are stated, not inferred. And
      **`Reset` no longer reads `DateTimeOffset.UtcNow`** — F3's D9 is closed here.
- [ ] `src/WinForward.Core/FlowTable.cs`: ctor takes an optional `ActivityBucketClock` derived from the
      existing `TimeProvider` parameter (the named-argument call shape at
      `CoreFlowStructuresTests.cs:104`, `new FlowTable(timeProvider: time)`, must keep compiling);
      `_timeProvider` is deleted from the resolve path; `Touch` calls become `TouchBucket(_activityClock.Current)`;
      `Reset` is called with `_activityClock.Tick()`; `RemoveExpired` calls `_activityClock.Publish(now)`
      (**from the argument**, not a second clock read) and computes `cutoffBucket` once per call; with the
      zero rule `idleTimeout <= TimeSpan.Zero ⇒ cutoffBucket = long.MaxValue` (three drain call sites
      depend on "retire everything on this call": `UdpChurnScenario.cs:234`, `UdpSessionBudgetRun.cs:97`,
      `SessionSetupDecompositionBenchmarks.cs:249`); `ScanChunk`/`RemoveCandidateAt` compare
      `state.ActivityBucket < cutoffBucket` on the **internal integer bucket** (never `LastActivityUtc`),
      **strict `<`** — `<=` would retire early (`design.md` §3.3); `AssertRegistryConsistent` gains the
      counter check if Step 3 has landed.
- [ ] `internal ActivityBucketClock ActivityClock` on `FlowTable` for tests that drive bucket edges.
- [ ] Rewrite the four boundary assertions to the bucket form: `CoreFlowStructuresTests.cs:95-97`,
      `:113-117`, `:128-138` — "stamped at t0 and swept at t0+idle is retained; swept at the first tick
      at/after the next bucket edge it is removed" (`design.md` §3.3). **Do not "tidy"**
      `SweepAllocationGateTests`' `DateTimeOffset.UtcNow.AddSeconds(1)` sweep instants (`:78`, `:88`,
      `:177`, `:183`, `:196`) — 1 s is two buckets, so they stay exactly-expired — nor the `now − 2 min` /
      `now` pair in the same class. Keep the `Assert.Same`/identity assertions working (Step 3 supplies the
      claim-side accessor).
- [ ] New exact facts (a new `tests/WinForward.Core.Tests/WarmPathGateTests.cs` unless the existing gate
      file is clearly the better home — it is a *structural* class, so it gets its own proof procedure,
      see Step 7):
      - `ActivityBucketClockReadsTheClockExactlyOncePerTickAndNeverOnAWarmResolve` (a throwing
        `TimeProvider` after one `Tick()`);
      - `FlowTableRetainsAStateUntilTheBucketAfterItsIdleWindow` (never-early, exact bucket arithmetic);
      - `ZeroIdleTimeoutStillRetiresEveryStateOnTheCall` (the `long.MaxValue` rule);
      - `FlowTableActivitySurvivesARecycledState` — a claim → expire → re-claim cycle leaves no stale
        bucket on the new flow.
- [ ] Keep `HotPathAllocationGateTests.FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` and
      `FlowTableRecyclesExpiredStatesThroughItsPool` green **unchanged in intent**: the bucket is one
      `long`, the pool stays, and a claim still allocates 0 B.

Validation: `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~CoreFlowStructuresTests"`
plus the two gates above; `FlowTableProductionShape` `--job short` (the hit rows must not move beyond the
stated noise, the clock row is deleted in Step 7).

Rollback: revert `ActivityBucket.cs`, `Domain.cs`, `FlowTable.cs` and the touched tests together — the
clock, the field and the seqlock are one coherent unit.

## Step 3 — `FlowTable`: the direct-mapped warm cache (**the req-1 mechanism**) — **rollback point B**

The concurrent-index variant of this step was implemented, measured and **reverted** (§`design.md` §2.0):
it delivered the ratio (0.628; 11.06 M/s at four threads) but cost **488 B per claim+expire cycle**
(`Expected: 0, Actual: 124,928` over 256 cycles = 3 nodes × ~160 B), which the exact
`FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` gate forbids. This step lands the cache instead, and
that gate is its acceptance gate.

- [ ] `src/WinForward.Core/Domain.cs`: add `FlowHash.CombineCanonical(family, protocol, a, b)` — order the
      two endpoints by `(Address.Bits, Port)` and delegate to the existing `FlowHash.Combine`, so a packet
      and its reverse select the same slot and the one shared transport-only mix is preserved.
- [ ] `src/WinForward.Core/FlowTable.cs`:
      - ctor: `_warm = new FlowState?[BitOperations.RoundUpToPowerOf2((uint)Math.Clamp((long)capacity * 64, 4_096, 262_144))]`
        (2 MB at the shipped default; the only allocation this mechanism adds, once, never grown).
      - `public bool TryResolveWarm(FlowKey key, out FlowStateView view)` — the lock-free probe of
        `design.md` §2.3: `Volatile.Read` the slot, `candidate.TrySnapshot(out view)` (Step 2's landed
        barrier-bracketed seqlock, `Domain.cs:208-230`), corroborate with
        `TransportTuple.From(view.Key) == tuple || .Reverse() == tuple`, then `TouchBucket(_activityClock.Current)`.
        **No clock read, no lock, no counter write.**
      - `TryResolve`/`TryClaimResolved` keep `out FlowState?` and their bodies (gated authority); add the
        write-through in `TryResolveLocked` (`:381-391`) and the population in `TryClaimResolved`
        (`:178-203`); add the clear in `RemoveCandidateAt` (`:327-350`) immediately **before**
        `ReturnState` (`:375-379`), guarded by `ReferenceEquals(_warm[i], candidate)`.
      - All three writes are `Volatile.Write` and all three happen under `_gate`.
- [ ] `src/WinForward.Runtime/FlowDispatcher.cs:163`: the warm entry calls
      `_flows.TryResolveWarm(packet.Context.Key, out var existing)` and uses `existing.Decision` /
      `existing.Key` / `existing.Generation` (the view's members keep the names). The slow path (`:203`)
      keeps `TryResolve` unchanged.
- [ ] Exact facts (all in `WarmPathGateTests` unless noted):
      - `WarmResolveTakesNoFlowTableGateEntries` — the landed red-then-green fact: with a seeded table and
        the gate-count probe, 256 warm probes ⇒ **0** `_gate` entries (`Expected: 0, Actual: 256` on the
        pre-change tree — keep that recorded text in `warm-path-gate-counts.txt`).
      - `WarmResolveCompletesWhileFlowTableGateIsHeld` — the parked-gate fact: thread A parks inside the
        gate via `GateHoldProbe`; thread B's `TryResolveWarm` returns within a bounded wait. Recorded red
        on the pre-change tree.
      - **`FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` — re-run unchanged as this mechanism's
        acceptance gate** (same window, same `Assert.Equal(0, allocated)`, same call-count backstop); it
        must be green on the landed tree, and its recorded failure under the reverted variant is the
        evidence that it is discriminating.
      - `FlowTableWarmResolveAllocatesNoManagedBytes` — the four window-contract properties of
        `hot-path.md:945-950` over ≥256 warm probes on a populated table.
      - `FlowTableCollidingFlowsFallBackToTheGatedPath` — force two keys into one slot (pick keys whose
        canonical hashes share the low bits, or drive the slot index through a test-only hash seam), then
        assert **both** flows still resolve to their own decisions: one warms, the other falls back to the
        gate and write-throughs. This is the false-miss contract, and the scenario's hit/miss counts are
        its series-side companion.
      - `FlowTableRemovedFlowIsNeverServedFromItsOldSlot` — claim, warm it, sweep it, then assert the warm
        probe misses (`TryResolveWarm == false`) and that a re-claim of the same key serves the **new**
        decision/generation. Also assert the recycled-state case: hold the old instance across the sweep
        and prove `TrySnapshot`/corroboration rejects it.
      - `FlowTableTransportTupleIsUniqueAcrossOrigins` — pin the invariant §2.3 relies on: a second claim
        of the same transport tuple under a different origin/adapter resolves to the existing state (no
        second state, so the cache's tuple-only corroboration is equivalent to the two dictionary probes).
      - `FlowTableRegistryMirrorsCountAcrossChurn` — the F3 invariant plus `_liveCount == Count`; the cache
        adds no bookkeeping to it.
      - `WarmCacheHitServesTheValidatedView` — on a warm hit the returned `FlowKey`/`Decision`/`Generation`
        equal what the gated authority returns for the same key (run both paths for the same key and
        compare field by field).
- [ ] Keep `SweepAllocationGateTests` green untouched: `RemoveExpired`'s round, holds, probes and counts
      are unchanged; the only new line is the cache clear inside `RemoveCandidateAt`.

Validation: `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~FlowTableClaimAndExpireCycleAllocatesNoManagedBytes"`
(the acceptance gate), the full `WinForward.Core.Tests` suite, `SweepAllocationGateTests` alone, the
scaling scenario (record the warm arm's ratio **and its hit/miss counts**) plus a `--job short`
`*FlowTableProductionShape*` run.

Rollback: delete `_warm`, `TryResolveWarm`, the three write/clear points and the dispatcher's warm call;
revert `CombineCanonical`. The gated `Dictionary` path underneath was never modified, so the revert is
behaviour-zero. **Do not** restore the concurrent-index variant.

## Step 4 — Self-traffic: the exact half leaves the warm path, the wildcard half stays — **rollback point C**

- [x] `SelfTrafficRegistry`: `_wildcards` → `ConcurrentDictionary<WildcardKey, long>` (mutations keep
      running under `_gate` with the existing generation check); add
      `bool IsWildcardOwned(FlowContext context)` probing only the two wildcard entries, lock-free.
      `IsOwned` keeps its full meaning (exact pair + wildcard pair, four probes).
- [x] `FlowDispatcher.cs:162` becomes `if (_selfTraffic.IsWildcardOwned(packet.Context)) …`; the claim-time
      `TryHandleSelfTrafficAsync` (`:194`, `:246-252`) keeps the full `IsOwned` and stays before the reverse
      hook and before `TryClaimResolved` (`:225`). `DispatchNonFlowAsync:330` is untouched.
- [x] Update `ISelfTrafficGuard`'s and the dispatcher's XML docs (`:140-150`): the exact half is
      claim-time only, the wildcard half is the warm entry's lock-free loop-prevention guard, and the
      reason (a recycled ephemeral port + `TcpProxyRelay.cs:36-44` registering `(Tcp, Any:P, proxyEndpoint)`
      before its SYN) is stated in code, not only in the design.
- [x] Record the **naive-deletion red first**: temporarily delete the warm-entry check outright (no
      `IsWildcardOwned`), run `ARelayWildcardTupleIsNeverProxiedOnAWarmHit`, capture the exact failure text
      into `warm-path-gate-counts.txt`, then restore and land the split. The PRD's "red before the change"
      is discharged by this variant — the unmodified tree passes the fact (it runs the full check), and
      `design.md` §4/§12 D14 records that distinction rather than claiming a red the tree cannot produce.
- [x] Exact facts:
      - `WarmHitTakesZeroExactTupleGuardProbes` — a counting guard: N=256 warm dispatches on a claimed
        flow ⇒ 0 *exact-tuple* probes (the wildcard pair is expected and counted separately).
      - `EveryClaimCallsTheFullGuardExactlyOnce` — M distinct keys through the dispatcher ⇒ exactly M full
        checks (one per claim, none on the warm re-dispatches after each claim).
      - `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` — claim a host flow on local port P to the proxy
        endpoint, keep it live, register `(Tcp, Any:P, proxyEndpoint)`, dispatch on that tuple, assert the
        self-traffic pass path and `executor.ProxyCount == 0`.
      - `ASelfOwnedTupleIsNeverClaimed` — register the exact tuple, dispatch ⇒ the guard reports owned, the
        table stays empty, the packet is passed (`Count == 0`).
- [x] Pulse the scaling scenario's `fake` arm against the `warm` arm and record the delta attributable to
      this step alone (the exact counters are the proof; the series is the artefact).

Validation: `HotPathAllocationGateTests.DispatcherWarmFastPathAllocatesNoManagedBytes` and
`EstablishedUdpDatagramPathAllocatesNoManagedBytes` green; the whole `FlowDispatcher`/self-traffic test
family green; the loop-prevention suites green.

Rollback: revert the interface member, the registry field type and the call site (three edits).

## Step 5 — TCP redirect table: one probe, no gate on the packet path — **rollback point D**

- [x] `TcpRedirectTable`: all four `Dictionary` indexes stay the **gated authority** (no
      `ConcurrentDictionary` anywhere — §2.0's 488 B reason, and a concurrent index would also break the
      TCP redirect allocation gates); add **two pre-allocated direct-mapped caches**
      (`TcpRedirectAssociation?[]`, `slots = RoundUpToPowerOf2(clamp(capacity * 8, 1_024, 16_384))`, ≤128 KB):
      a **reverse** cache indexed by `HashCode.Combine(source, destination)` and validated by
      `assoc.ReverseSourceEndpoint == source && assoc.ReverseDestinationEndpoint == destination`, and an
      **original** cache indexed by `originalKey.GetHashCode()` and validated by
      `assoc.OriginalKey.Equals(originalKey)` (both field pairs are get-only, set in the ctor — exact, no
      ABA, no seqlock needed); delete `IsReverseCandidate` (`:287-290`); keep `Count` (`:183-189`) reading
      `_byOriginal.Count` under `_gate` (unchanged and cheap without a concurrent index); add
      `GateEntryCountForDiagnostics`.
- [x] **Factor the four-index removal into one private helper** (`RemoveUnderGate(association)`) used by
      `TryRemove` (`:344-360`) and by both `RemoveExpired` removal bodies (`:388-400`), and clear both
      cache entries there with `ReferenceEquals` guards — factoring is what makes "no removal site can
      forget the cache" structural rather than a review promise.
- [x] Populate both slots in `TryClaim` after the four index writes (`:250-256`); write through on a gated
      hit in `TryResolveByReverse` (`:272-284`) and `TryResolveByOriginal`/`TryFind` (`:304`, `:429-441`). All
      writes are `Volatile.Write` under `_gate`.
- [x] **Complete the fold at the call sites** or it does not exist: `TcpProxyCoordinator.cs:359-367` (the
      tombstone branch) and `:391` (reverse routing) each call `TryResolveByReverse` once and pass the
      association into `HandleReverseAsync`, whose own probe (`Injections.cs:305`) is deleted. Left
      half-folded, a warm reverse packet pays two probes and the 2→1 claim fails.
- [x] `TcpRedirectAssociation`: `LastActivityUtc` derived from `long _activityBucket`; `Touch(DateTimeOffset)`
      writes `ActivityBucket.FromUtc(now)`; `internal BucketForDiagnostics`.
- [x] `TcpProxyCoordinator.cs:386`'s per-packet `_timeProvider.GetUtcNow()` becomes the published bucket
      (`ActivityBucket.ToUtc(clock.Current)`); `TcpRedirectSetup`/`TryClaim` keep real timestamps (cold
      paths).
- [x] `TcpRedirectTable.RemoveExpired` (`:342`, `:355`) and `TcpRedirectSessionStore.RemoveExpiredAsync`
      (scan `:134` and re-check `:150`) compare in bucket space with the strict `<` cutoff and the zero
      rule. Keep the pending-SYN and tombstone clocks as they are (F3 item 5).
- [x] Update `TcpProxyCoordinatorRewriteTests.cs:31` to a bucket-aligned timestamp.
- [x] Exact facts:
      - `ReverseResolveCompletesWhileRedirectGateIsHeld` (park thread A via the new probe; thread B's
        `TryResolveByReverse` completes) — **recorded red** before this step.
      - `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` — `GateEntryCountForDiagnostics`
        unchanged and the injected clock unread across a warm forward packet (which also asserts the
        reverse-probe count is 1) and across a warm reverse packet through `HandlePacketAsync`.
      - The dispatcher's `WantsPacket` port prefilter still short-circuits: a packet whose source port is
        not a live listener port takes no reverse probe at all.
- [x] Must-not-move: `TcpProxyCoordinatorRewriteTests`, the tombstone-straggler tests (the fall-through
      theorem: a port-prefilter miss still reaches the full handler on the slow path), the port refcount
      tests, and the fragment path (now reading a gated `_byAddressPair`).

Rollback: revert the table, the association representation (with Step 2's bucket in place) and the two
coordinator call sites.

## Step 6 — UDP ready path: ready-first reorder, lock-free lookup, no session gate — **rollback point E**

- [x] `UdpProxyCoordinator.Send.cs`: split `TrySendSpanAsync` into the lock-free ready fast path and
      `SendAdmissionPathSpan` (the old body, gated, with the clock read, the cooldown probe, the capacity
      check, admission and the enqueue) — **including the re-check of `slot.Ready` under the gate**
      (`design.md` §6.1; without it a datagram read as "not ready" can be enqueued after the flush already
      drained, and would sit until the TTL).
- [x] `_sessions` **stays the gated `Dictionary` authority** (a concurrent index would break
      `UdpSetupEnqueuePathAllocatesNoManagedBytes`, whose window contains the admission insert); add a
      **pre-allocated direct-mapped session cache** (`UdpSessionSlot?[]`, 
      `slots = RoundUpToPowerOf2(clamp(capacity * 8, 1_024, 16_384))`, ≤128 KB) and make the ready path read
      it: `Volatile.Read(ref _sessionCache[flowHash & mask])` then validate
      `Volatile.Read(ref slot.Session) is { } s && s.Flow.Equals(flow)`, then `slot.Ready`. Populate at the
      admission add (`Send.cs:62`) and at the ready flip (`DequeueForFlush`, `:368-384`); clear with a
      `ReferenceEquals` guard in `RemoveSlotAsync` (`:460-462`) and alongside `_sessions.Clear()` in
      `DisposeCoreAsync` (`:276`). Keep `SessionCount` (`:105`) reading the dictionary under the gate — no
      maintained counter is needed.
- [x] Volatile `Session`/`Ready` accessors on `UdpSessionSlot` with the existing publication order
      (`Session` before `Ready`), so a reader that validates the flow key and then reads `Ready` is
      guaranteed the pair.
- [x] `UdpProxySession.cs`: `_expiring` becomes volatile and drops out of the send path; `SendSpanAsync`
      loses `lock (_activityGate)`; `TouchActivity` (`:410-427`) drops the lock and the clock read and
      writes the published bucket; the propagation rate limit becomes a bucket distance **with the
      sentinel changed from `0` to `long.MinValue` (or a flag)** — bucket 0 is real and
      `UdpProxySessionTests` starts at `UnixEpoch` (`:23`, `:38`), so a `0` sentinel would suppress the
      first propagation; `LastActivityUtc` is derived; `TryBeginExpiry` (`:194-207`) keeps `_activityGate`
      and compares buckets with the never-early operator.
- [x] `UdpProxyCoordinator.RemoveExpiredAsync`'s candidate scan (`:369`, `session.LastActivityUtc`) moves to
      the same bucket form (it may stay a one-bucket-older pre-filter, since `TryBeginExpiry` re-checks).
- [x] `UdpProxySession`'s ctor/context take the `ActivityBucketClock` (the same instance the flow table and
      the redirect table use).
- [x] Rewrite `UdpProxySessionTests.cs:11-14`/`:54`/`:60` — "stays exact per operation" is no longer true;
      the documented contract becomes "the session's activity is a bucket-derived stamp, exact to 500 ms" —
      and drive the clock explicitly (advance the injected clock, then `Tick()`), including the
      first-propagation case at `UnixEpoch` (the sentinel regression).
- [x] **Re-base every fake-clock and sub-bucket test in the same commit**, or the step lands red or
      bucket-phase dependent (the review's enumerated surface): `IdleExpirySweeperFailureTests.cs:57`
      (50 ms relay idle timeout → the ≥4/≥5 failing-tick assertions at `:63-69`),
      `UdpProxyCoordinatorLifecycleTests.cs:245/270/278/339/381`,
      `UdpSessionBudgetAcceptanceTests.cs:232-236`, `TcpProxyCoordinatorCapacityTests.cs:374`,
      `IdleExpirySweeperCadenceTests.cs:82/96`, `UdpSetupQueueBudgetTests.cs:123`,
      `CoreFlowStructuresTests.cs:111`. Each needs an explicit `Tick()` after the advance (or an advance
      past a bucket edge) — verify each one individually rather than assuming the list is complete.
- [x] Exact facts:
      - `UdpReadySendCompletesWhileCoordinatorGateIsHeld` (park thread A; a ready send completes on thread
        B) — **recorded red** before this step;
      - `UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads` (probe + throwing clock after
        one `Tick()`);
      - `UdpReadySendTakesZeroActivityGateEntries` (consumes `ActivityGateHoldProbe`: 2 → 0 per datagram);
      - `UdpSessionCacheMissesOnATornDownSlot` (remove a flow's slot, assert the warm path misses and the
        authoritative path admits a new session, and that a colliding flow's slot is never mistaken for
        ours);
      - `UdpSessionCountMatchesTheDictionaryAcrossChurn` (admit/send/expire/fault churn; `_sessions.Count`
        under the gate stays the single source of truth — no counter to drift);
      - `UdpReadyPathSendsThroughACooldownFlowOnlyAfterTheCooldownLapses` (the reorder's semantics: a
        flow with no slot is still cooldown-gated on admission);
      - `SendAdmittedDuringExpiryTransitionIsDeliveredNotDropped` (the accepted window of `design.md`
        §6.3, pinned so the delta is a recorded behaviour and not an accident) — **replaced during the
        session: the window has no seam and the fact is not deterministic; the landed fact is
        `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate`, which pins the ordering that makes the
        delta benign (the send does not wait on the gate and is delivered), not the counter flip. See
        the session record's deviation 3 and the artifact README's residual list.**.
- [x] Existing gates that must stay green untouched: `EstablishedUdpDatagramPathAllocatesNoManagedBytes`,
      **`UdpSetupEnqueuePathAllocatesNoManagedBytes`** (its window contains the admission insert — the gate
      a concurrent index would have broken, and why the authority stays a `Dictionary`), the F3 UDP sweep
      gates, the UDP retention/burst scenarios and `UdpAssociationRecoveryTests`.

Validation: `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~Udp"` plus the
`udpchurn`/`udp` stability scenarios (`--quick`).

Rollback: revert the coordinator partial, the slot type, the session and the coordinator's composition
wiring. The reorder and the session cache can be reverted separately if only one misbehaves.

## Step 7 — Clock wiring, evidence, spec, record

- [x] **Wire the tick where it is reachable**: create the one `ActivityBucketClock` in
      `DurableCaptureBundle` (from the bundle's `TimeProvider`) and thread it into `FlowDispatcher` (new
      optional ctor parameter → its `FlowTable`; `FlowDispatcher.cs:113` builds it with no clock today),
      the `TcpRedirectTable`, and `UdpProxyComposer` → `UdpProxyCoordinator` → `UdpProxySession`. Add the
      `Tick()` inside `DurableCaptureBundle.FlushPendingInjections` (`:362`) — the only `OnBatchCompleted`
      chain point, wired at `Program.cs:284`. Document the threading in the bundle's XML doc.
- [x] `ActivityBucketClockTicksExactlyOncePerPumpIteration` — drives the **composition** (a bundle with a
      fake capture loop, or `NdisCapturePump.RunIterationForTests`) with a counting clock: one tick per
      iteration including empty polls, zero per packet. It must fail if the wiring is removed (the
      frozen-bucket guard).
- [x] Retire the `ReadActivityClock` row from `FlowTableProductionShapeBenchmarks` (`:163-169`) and add a
      `ResolveWarmHit` row; update the class XML doc to say the retired row's ~40 ns is the headroom the
      bucket bought (`benchmarks/results/2026-09-29-benchmark-coverage/README.md` R2).
- [x] Capture the after-series and write the artifact README:
      `scaling-contention-after.jsonl` (3 runs, `--duration 63`),
      `udp-ready-path-contention.csv` + `flow-table-production-shape.csv` (BDN short job, 3 runs, keep the
      raw exports), `warm-path-gate-counts.txt` (before/after probe counts, the naive-deletion red, the
      claim-accessor 0 B re-run, the memory pair), `gate-stability.txt` (the per-gate loop), and the
      README's verdict table with **both** scaling-ratio readings.
- [x] Spec updates (`.trellis/spec/backend/`), one row at a time — the review's process finding is that a
      PRD-criteria map alone lets spec contradictions through, so `design.md` §9 carries a **spec row →
      proof** map and this step updates every row it names:
      - `hot-path.md:500` — the expiration comparison becomes the bucket form with the strict `<` and the
        zero rule;
      - `hot-path.md:38` and `tcp-local-redirect.md:24/166` — `IsReverseCandidate` is replaced by the single
        fold probe (same miss semantics, tombstone unchanged);
      - `hot-path.md:26-50` — the warm-shape enumeration: the self-traffic bypass becomes the wildcard-only
        guard, and the resolve/clock contract changes;
      - `hot-path.md:485-489` (pooling) — a caller never holds a `FlowState` across a released gate; the
        validated view is the only read; `:490-502` (registry) notes the warm-cache contract over the
        the sweep contract;
      - `traffic-policy-lifecycle.md:48` — the UDP session's activity is a bucket-derived stamp;
      - `udp-relay.md:471` — the lease is taken without `_activityGate`;
      - `async-lifetime.md:182-183` — `UdpProxySession` no longer holds a gate across `TryEnter` (the
        lock-order property itself is unchanged);
      - `quality-guidelines.md:17` (the coordinator gate as slot state's single gate), `:21` (association
        tables' single `_gate`), `:22` (`Touch` on every successful resolution) and `:44` (observation
        refreshes activity) — each restated with the narrow exception this task lands.
- [x] Keep the per-gate loop (`hot-path.md:1190-1216`) to the **four allocation classes** with its
      `totals=` / `signature=^(168|5216|7384|7448)$` strings untouched; `WarmPathGateTests` is a
      structural class and gets its **own** repetition proof (20 runs green, plus the recorded
      red-then-green runs of the parked-gate and relay-wildcard facts), not a fifth loop entry.
- [x] Record the session in the task's `implement.md` checkboxes and the parent backlog row; cite the
      artifact in the PRD's last acceptance bullet before archive.

Validation for the step: artifact README's every "exact" claim is re-derivable from a file in the
directory; the timing rows all carry `gated: false`; the control/baseline for each series is quoted.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                     # zero-warning
dotnet test WinForward.slnx -c Release                                      # full suite green
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~HotPathAllocationGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"
# per-gate stability proof: the spec's loop, the FOUR allocation classes only (hot-path.md:1198-1216),
# 20 runs each, unchanged totals/signature strings.
# WarmPathGateTests' own repetition proof (structural class, not an allocation gate):
for i in $(seq 1 20); do dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WarmPathGateTests"; done
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue>

# The proving series (PRD AC 1). 3 arms × 3 thread counts × 7 s = 63 s; --threads N pins one count.
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario scaling --quick --flows 4096 --duration 63 \
    --output /tmp/wf-warm-after-$r.jsonl
done
cat /tmp/wf-warm-after-{1,2,3}.jsonl > benchmarks/results/2026-09-30-warm-path-lock-chain/scaling-contention-after.jsonl

# UDP ready path (PRD AC 5), 3 runs of the BDN short job, raw exports kept.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*UdpReadyPathContention*' --job short

# Flow-table shape after the bucket (the retired clock row must be absent; hit rows must not move).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*FlowTableProductionShape*' --job short

# Must-not-move series.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*TcpRedirectDataPath*' '*Parser*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udpchurn --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --quick
```

## Acceptance-criteria mapping (amended PRD)

| PRD criterion | Where it is discharged | Kind |
|---|---|---|
| **AC 1** scaling, one line: warm arm (`TryResolveWarm` cache probe + lock-free wildcard guard, registry populated, no per-lookup exact `IsOwned`), self-normalised 4-thread ratio ≥0.6 over 3 runs, its 1-thread arm ≥3.00 M/s, recorded-baseline ratio (≥7,996,220/s) recorded beside it, **the measured cache hit/miss counts recorded beside both**, arm-count arithmetic fixed | Step 1 (arm + divisor + both verdict fields + hit/miss counting) → Step 3 (the cache) → Step 7 (after-series, `--duration 63`) | series |
| **AC 2** no global lock on the warm resolve, exact, red before | Step 3/5/6 facts `WarmResolveTakesNoFlowTableGateEntries` (landed, red 256→0), `WarmResolveCompletesWhileFlowTableGateIsHeld`, `ReverseResolveCompletesWhileRedirectGateIsHeld`, `UdpReadySendCompletesWhileCoordinatorGateIsHeld` (parked-gate structural proof) + the counts (flow-table gates 1→0 on a hit, redirect gates 2→0 and probes 2→1, coordinator gates 1→0 on a hit) | exact |
| Cache is exact, never approximate | Step 3 `FlowTableCollidingFlowsFallBackToTheGatedPath`, `FlowTableRemovedFlowIsNeverServedFromItsOldSlot`, `FlowTableTransportTupleIsUniqueAcrossOrigins`, `WarmCacheHitServesTheValidatedView`; Step 6 `UdpSessionCacheMissesOnATornDownSlot`; Step 5's alias/exactly-once suite stays green | exact |
| The mechanism does not regress an allocation gate | Step 3's **re-run** of `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` (unchanged window/assertion) with the reverted variant's `Actual: 124,928` recorded as the discriminating failure; Step 6's `UdpSetupEnqueuePathAllocatesNoManagedBytes` unchanged | exact |
| **AC 3** self-traffic: zero **exact-tuple** probes on warm hits, claim-time probes on misses, **plus** `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` | Step 4 `WarmHitTakesZeroExactTupleGuardProbes` (0 of N) and `EveryClaimCallsTheFullGuardExactlyOnce` (M of M), `ASelfOwnedTupleIsNeverClaimed`, and the relay-wildcard fact with its naive-deletion red recorded | exact |
| **AC 4** clock call gone: `Touch` performs no clock read, `ReadActivityClock` retired/obsolete | Step 2/5/6 throwing-clock facts (`…NeverOnAWarmResolve`, `UdpReadyDatagramTakesZeroCoordinatorGateEntriesAndZeroClockReads`, the TCP warm packet's zero reads; exactly 1 per `Tick()`), Step 7 retirement | exact + series |
| **AC 5** UDP ready path, one line: 4-worker `ReadySend` ≤261.7 ns over 3 runs, 1-/2-worker rows recorded beside it, **no** 4/1 ratio condition | Step 7 after-series, 0 B every row | series |
| Semantics preserved (exactly-once claim, fail-closed capacity, expiry/retention never-early, aliasing, self-traffic, 0 B warm) | Step 2's never-early and zero-timeout facts; Step 3's registry/counter facts and the capacity gate; Step 4's exact/wildcard split; Step 5's fall-through/tombstone suite; Step 6's counter/cooldown/window facts; the existing allocation gates (`FlowTableClaimAndExpireCycle…`, `DispatcherWarmFastPath…`, `EstablishedUdpDatagramPath…`, `UdpSetupEnqueuePath…`) stay green untouched | exact |
| Existing gates stay green (suite, `HotPathAllocationGateTests`, F3 sweep matrix, gc-soak anchors, UDP retention/burst) | Validation block; the F3 sweep code paths are deliberately untouched by Steps 2–6 | exact |
| Release zero-warning, suite green, format empty, inspectcode zero | Validation block, at every rollback point | exact |
| Benchmark data recorded and cited before archive | Step 1 before-artifact + Step 7 after-artifact README, `warm-path-gate-counts.txt`, `gate-stability.txt` | artifact |
| **Spec rows** touched by this design are restated with a proof each | `design.md` §9's spec row → proof map, discharged row by row in Step 7 | exact |

## Risky files / rollback points

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Core/FlowTable.cs` | The warm cache, the pooled-state contract and the F3 sweep all live here; a missing cache clear or a wrong corroboration is a wrong-decision bug (fail-open), and an allocation added to the claim path breaks the acceptance gate | Step 3 (rollback point B) |
| `src/WinForward.Core/Domain.cs` (`FlowState`) | `Reset`'s version bracket is the entire ABA argument; a missing `Volatile` voids it silently | Step 2 (rollback point A) |
| `src/WinForward.Core/ActivityBucket.cs` | A wrong width or a `<=` comparison changes retention for every flow table and every redirect association | Step 2 |
| `src/WinForward.Runtime/FlowDispatcher.cs` | The self-traffic *split* is the whole of requirement 2: deleting the wildcard half (not just the exact half) restores the relay-recursion finding; deleting `:194` instead removes loop prevention at claim time | Step 4 (rollback point C) |
| `src/WinForward.Runtime/TcpRedirect/TcpRedirectTable.cs` | Warm reverse routing and the exactly-once alias invariants; `IsReverseCandidate`'s removal must not become a false miss for a live alias | Step 5 (rollback point D) |
| `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` + `.Injections.cs` | The reverse/fold call sites and the per-packet clock reads | Step 5 |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.Send.cs` | The reorder is only semantics-preserving because of the cooldown writer analysis (`design.md` §6.1); a wrong reorder changes drop behaviour for cooling-down flows | Step 6 (rollback point E) |
| `src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs` | The session cache must be cleared at every `_sessions` mutation (admission add, `RemoveSlotAsync`, the dispose `Clear()`), and the authority must stay a `Dictionary` for the admission allocation gate | Step 6 |
| `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` | Removing `_activityGate` from the send path changes the expiry/send interleaving (argued in `design.md` §6.3) | Step 6 |
| `src/WinForward.Cli/*` (the pump callback chaining) | The per-iteration tick is the bucket's freshness source; a missed wiring freezes the bucket | Step 7 |
| `benchmarks/WinForward.Benchmarks/Stability/ScalingContentionScenario.cs` | The acceptance instrument: an arm that does not model the post-reorder shape, or a ratio read against the wrong baseline, silently voids AC-1 | Step 1 revert |
| `tests/**` expectations | The view API and the bucket boundaries move ~20 assertions; a mechanical rewrite can weaken an assertion into a tautology — re-read each one | per step |

## Commit plan skeleton

```text
0  test(bench): warm-resolve arm + gate/clock probes; scaling before-artifact        [Step 1]
1  perf(flow): bucketed lock-free activity stamps; injected-clock unification (D9)
   + the bucket/never-early facts                                                    [Step 2, rollback point A]
2  perf(flow): direct-mapped warm cache over the gated dictionary + validated FlowState view
   + the parked-gate and 0 B resolve facts                                           [Step 3, rollback point B]
3  perf(flow): self-traffic check moves to claim time only
   + the guard call-count facts                                                      [Step 4, rollback point C]
4  perf(tcp): one reverse probe per redirect packet; cache-backed reverse/original probes
   + the redirect gate-count facts                                                   [Step 5, rollback point D]
5  perf(udp): ready-first wait-free send path; cached session lookup
   + the coordinator gate/clock facts                                                [Step 6, rollback point E]
6  feat(capture): per-iteration activity tick in the composition
   + the tick-count fact                                                             [Step 7]
7  docs(evidence): before/after artifacts + spec contracts for F2                    [Step 7]
```

Each commit carries its own proving facts, so no commit is red. Steps 2 and 3 may need to be one commit if
the intermediate tree does not compile (the view type and the bucket are consumed together by the
dispatcher); if so, split the *tests* rather than the product change.

## Deferred / explicitly out of scope

- The §A4 sharded slab + per-shard open-addressing index, canonical transport key, incremental rehash
  (roadmap item 7) — the PRD's out-of-scope note; requirement 1 is met without it (design §2).
- The transport `_sendGate` and the relay-receive path (requirement 5 does not cover them; the recorded
  benchmark excludes them by construction).
- Tombstone, setup-cooldown, pending-SYN TTL and UDP setup-queue representations (F3 contract item 5).
- Lossy fingerprint caches for the tombstones/cooldowns (research §A5, roadmap item 11).
- A per-window control that can tell a host allocation lump from a driven allocation (closed as
  "priced and rejected" by `09-30-exact-gate-residual-lumps`; the per-gate process proof stands).
- A monotone guard against a backwards clock step (recorded in design §8 risk 8 as a review question, not
  silently added).
- **The concurrent-index mechanism (measured dead end, do not restore):** `ConcurrentDictionary` for
  `_states`/`_transportIndex` delivered the ratio (0.628; 11.06 M/s at four threads) and cost **488 B per
  claim+expire cycle** = 3 nodes × ~160 B, failing
  `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` with `Expected: 0, Actual: 124,928` over 256
  cycles. Recorded in `design.md` §2.0/§12 D1b and `research/implementation-notes.md` §13, with its numbers
  kept as the ceiling comparator — not as a fallback.
- The claim/slow-path post-gate ABA (`FlowDispatcher.cs:203-243`): the warm half is fixed by the validated
  cache view; the claim half needs the view API on `TryClaimResolved` (≈15 test call sites) and stays a
  follow-up (`design.md` §2.7).
- Windows-only validation rows (`gc-soak` on a real NIC, `TcpThroughputScenario` ratio): unchanged
  assignment to the `windows-real-nic` program.

## Review dispositions (independent review, for the archive)

| # | Severity | Disposition |
|---|---|---|
| 1 | BLOCKER | **Fixed.** The seqlock writer now publishes odd → `Interlocked.MemoryBarrier()` → fields → `Interlocked.MemoryBarrier()` → even, with the monotone-`++`/wraparound note and the mirrored reader fences (`design.md` §2.3, notes §8.2b); the same-key re-claim is written up as the deliberately-accepted ABA case with the `Generation`-is-log-only argument (notes §8.2c); the post-validation touch's "one bucket newer" value is stated (notes §8.2, design §2.3, risk 4). |
| 2 | BLOCKER | **Fixed.** AC 1 is one line: the scenario gains the warm arm (registry populated, no per-lookup exact `IsOwned`, wildcard guard included), acceptance is its self-normalised 4-thread ratio ≥0.6 with its 1-thread arm ≥3.00 M/s, and the recorded-baseline figure (≥7,996,220/s) is recorded beside it; the `windowSeconds` arm-count arithmetic is corrected to `arms.Length * threads.Length` and the series runs `--duration 63` to keep the recorded 7 s window (`design.md` §10.2–10.4, Step 1). |
| 3 | MAJOR | **Fixed.** The zero/sub-bucket rule is explicit (`idleTimeout ≤ 0 ⇒ long.MaxValue`, so the three drain call sites keep "retire everything on this call"); the `+1 s` sweep instants and the `now−2min`/`now` pair in `SweepAllocationGateTests` are explicitly marked do-not-tidy (design §3.3, notes §4 item 8). |
| 4 | MAJOR | **Fixed.** The fake-clock/sub-bucket surface is enumerated in Step 6 (`IdleExpirySweeperFailureTests:57/:63-69`, `UdpProxyCoordinatorLifecycleTests:245/270/278/339/381`, `UdpSessionBudgetAcceptanceTests:232-236`, `TcpProxyCoordinatorCapacityTests:374`, `IdleExpirySweeperCadenceTests:82/96`, `UdpSetupQueueBudgetTests:123`, `CoreFlowStructuresTests:111`) with an explicit `Tick()`/bucket-edge re-base, and `UdpProxySessionTests`' "stays exact per operation" is restated. |
| 5 | MAJOR | **Fixed.** The seam is named: one clock created in `DurableCaptureBundle`, threaded into `FlowDispatcher`'s table (`FlowDispatcher.cs:113` has none), the redirect table and the UDP composer/session, ticked in `DurableCaptureBundle.FlushPendingInjections` (`:362`) reached from `Program.cs:284`; the frozen-bucket fact drives the composition (design §3.1, Step 7). |
| 6 | MAJOR | **Superseded by the Step-3 reversal.** The concern (the claim must still yield the pooled instance so `HotPathAllocationGateTests.cs:440-441/450-451/470-477` stay green) is now met structurally: the landed mechanism leaves `TryResolve`/`TryClaimResolved` signatures untouched, so those gates compile and pass with **no accessor and no window change**. The accessor that the reverted view-API variant required is gone (design §2.7). |
| 7 | MAJOR | **Fixed.** The delta is restated as "for as long as the flow keeps receiving packets" (every warm hit re-touches; the 5-minute timeout only starts after silence), both registration sites are named (`TcpProxyRelay.cs:36-44` before its SYN; `TcpRedirectSetup.cs:179` after the claim), and the residual is re-made against the translated-tuple and wildcard match shapes (design §4, notes §3, D13). |
| 8 | MAJOR | **Fixed.** The fold is specified at the call sites (`:359-367` tombstone branch, `:391` routing, `Injections.cs:305` deleted, the association passed through) so the probe count really goes 2→1, and `TryResolveByAddressPair` is reconciled as **gated** in both documents (design §5, Step 5). |
| 9 | MAJOR | **Fixed.** The bucket-leg scope is completed: `TryBeginExpiry`, the coordinator's candidate scan (pre-filter or bucket form), `TcpRedirectSessionStore.cs:134/:150`, `TcpRedirectTable.cs:342/:355`, the internal-integer-bucket comparison rule, and the spec-update list (`hot-path.md:500`, `:38`, `tcp-local-redirect.md:166`). |
| 10 | MINOR | **Fixed.** Tick source 3 publishes from its argument via `Publish(DateTimeOffset)` (no second clock read); the propagation sentinel is `long.MinValue`/a flag, with the `UnixEpoch`-bucket-0 regression called out (design §3.1/§3.2, notes §4 item 9). |
| 11 | MINOR | **Fixed.** The measured residency numbers replace the estimate: the `flowTable` stage delta is 32,513,544 / 32,519,352 / 32,525,832 B at capacity 65,536 with 100 live flows; full-capacity node sets ≈23–27 MB ≈1.2× the replaced entry arrays (design §2.1, risk 6, notes §7). |
| 12 | MINOR | **Fixed.** The added "4/1-worker ratio ≤ 1.2" condition is dropped from both documents; AC 5 is the 4-worker `ReadySend` mean ≤261.7 ns with the 1-/2-worker rows recorded beside it. |
| 13 | MINOR | **Fixed.** The `UdpAssociation` justification is corrected (lease-based pool retirement, `s_idleRetireTimeout` 60 s, `CanRetire` on `_idleSinceTicks`, the only `LastActivityUtc` reader is test-only), and the never-early claim is scoped to the production legs, with the observer's stamp allowed to be up to 500 ms older than true activity (design §3.2, notes D18). |
| 14 | BLOCKER (spec) | **Fixed.** The wildcard relay-socket half stays on the warm path, answered lock-free via a new `ISelfTrafficGuard.IsWildcardOwned` and a `ConcurrentDictionary` wildcard index; only the exact-tuple half moves to claim time; `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` replaces the fact that pinned the regression, with the honest red-before shape (the naive-deletion variant, recorded in Step 4 and logged) stated rather than claimed against the unmodified tree (design §1/§4/§7/§8/§9, notes §3, D2/D14). |
| 15 | MAJOR (spec) | **Fixed.** The two contradicting rows are in the spec-update list with their replacements (`udp-relay.md:471`, `async-lifetime.md:182-183`), and the `ActivityGateHoldProbe` Step 1 adds is consumed by the named post-change fact `UdpReadySendTakesZeroActivityGateEntries`. |
| 16 | MAJOR (spec) | **Fixed.** `quality-guidelines.md` joins the Step 7 update list with its rows reconciled (`:17`, `:21`, `:22`, plus `:44`), and `TcpRedirectTable.Count` keeps reading `_byOriginal.Count` under the gate (the gated `Dictionary` authority makes a maintained counter unnecessary — the `_count` requirement was an artefact of the rejected concurrent index). |
| 17 | MINOR (spec) | **Fixed.** The gate-stability loop stays the four **allocation** classes with its totals/signature strings untouched; `WarmPathGateTests` gets its own 20-run repetition proof plus the recorded red-then-green runs, not a fifth loop entry (Step 7, Validation commands). |
| 18 | MINOR (spec) | **Fixed.** `traffic-policy-lifecycle.md:48` is in the spec-update list with the bucket restatement for the UDP session (the amended PRD req 3 names it among the quantised consumers). |
| 19 | PROCESS | **Fixed.** `design.md` §9 now carries a **spec row → proof** map (the PRD-criteria map alone let findings 14–17 through), `quality-guidelines.md` and `async-lifetime.md` are in the Step 7 list, and the validation block states that the per-gate loop covers allocation classes only. |
| — | CONFIRMED (review, gate-removal window) | **Folded in.** The dropped `_activityGate` adds a real window — a send admitted between the sweeper's `IsIdle` re-check and its `_expiring` store goes out instead of a counted `UdpFailClosedDrop` (`Send.cs:150-155`) — recorded as accepted delta 5 (design §7), risk 13b. The fact that pins it is `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate` (Step 6); the planned `SendAdmittedDuringExpiryTransitionIsDeliveredNotDropped` was **not** landed because the window has no seam (**the task's one acceptance gap**, session record deviation 3). |
| — | CONFIRMED (review, counter rule) | **Folded in.** `ConcurrentDictionary.Count` is banned on every hot path and under every other lock; with the landed mechanism the counters are moot (the gated `Dictionary` authorities keep `Count`/`Capacity`/`SessionCount` unchanged), and the rule is recorded for any future concurrent-index attempt. |
| 20 | BLOCKER (measured, post-implementation) | **Fixed by mechanism replacement.** Step 3's `ConcurrentDictionary` variant delivered the ratio (warm arm 0.161/0.154/0.153 → **0.628**; 4-thread 3.29 → **11.06 M/s**; `WarmResolveTakesNoFlowTableGateEntries` `Expected: 0, Actual: 256` → green) but cost **488 B per claim+expire cycle** (`Expected: 0, Actual: 124,928` over 256 cycles = 3 nodes × ~160 B) and broke `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes`. The 488 B is **not** sanctioned and the gate is **not** re-baselined: the variant was reverted and replaced by the operator's **pre-allocated direct-mapped warm cache** over the gated authority (`design.md` §2), applied to the flow table, the TCP redirect table and the UDP session lookup. Steps 3/5/6 were rewritten; Step 1's warm arm counts hits/misses; Step 3's acceptance gate is the re-run 0 B claim/expire gate. |
| 21 | REVERSAL (measured) | **Recorded, not hidden.** Three mechanisms are on record with numbers: the plain `Dictionary` (unsafe — the runtime throws on the torn chain), the concurrent index (ratio 0.628 but 488 B/claim — **rejected**), and the direct-mapped cache (chosen; §2.5's arithmetic bound with the measured miss rate as the acceptance companion). The cache's honest risk is stated: the measured ceiling (0.628) is only 4.7 % above the 0.6 line, so the miss rate must stay ≈≤2 % (the sizing table's target) and **the measurement decides**; the levers (raise the 262,144-slot cap; K-way probing, which needs the operator's nod because it changes the specified direct-mapped shape; or restating requirement 1 with the measured numbers) are named in `design.md` §2.5. |
| — | COUNTEREXAMPLE (reported) | The operator's illustrative `2 × capacity` sizing **fails the criterion**: at the `scaling` scenario's capacity (5,120) it yields 10,240 slots for 4,096 live flows (λ = 0.4 → ~33 % miss), which §2.5's arithmetic puts at ≈0.39 (linear model) to ≈0.5 (saturation model). The plan therefore sizes `64 × capacity` clamped to [4,096, 262,144] — 2 MB at the shipped default — for λ ≈ 0.016 and a modelled miss of ≈1.6 %. The sizing is the design's, not the operator's example, and the deviation is called out here so it is visible. |

### Independent check session (sub-agent, 2026-09-30) — findings and fixes

Every item below was found by re-reading the landed diff against the PRD, the design and the six spec
files, and re-deriving the artifact numbers from the raw files. No product behaviour was changed: the
fixes are comment/spec/record accuracy, nothing else. The re-derived evidence is appended to
`benchmarks/results/2026-09-30-warm-path-lock-chain/gate-stability.txt`.

| # | Severity | Disposition |
|---|---|---|
| C1 | MINOR (docs vs code) | **Fixed.** The claim "a stale entry fails validation by construction / the cache's only failure mode is a false miss" was **wrong** in three places: `TcpRedirectTable`'s cache comment, `quality-guidelines.md`'s association-table row, and (weaker) design §5's "live or stale, never wrong". A *removed* association still passes validation — its get-only fields are unchanged — so a probe that loaded the slot before `RemoveUnderGate`'s guarded clear is **served**, not missed. The three texts now state the real guarantee ("exact, or one in-flight probe old") and name the window; a matching note was added to `hot-path.md`'s `_warm` bullet for the flow table (where the window ends at `ReturnState`'s recycling `Reset`, so only a snapshot that completes before it can be served). The behaviour itself is the ordinary "reader linearized just before the removal" race, bounded by one probe, and is recorded as a residual (artifact README; design §8 rows 3/7b/12 already carried it). |
| C2 | MINOR (artifact) | **Fixed.** The artifact README's spec-row → proof map and `design.md` §9's map both cited `FlowTableSnapshotRejectsARecycledState`, a test that does not exist. Replaced with the landed facts (`FlowTableRemovedFlowIsNeverServedFromItsOldSlot`, `FlowTableActivitySurvivesARecycledState`). |
| C3 | MINOR (artifact) | **Fixed.** `warm-path-gate-counts.txt` carried a duplicated `### The exact facts, red before the change` heading and listed `--filter "FullyQualifiedName~WarmPathGateTests"` as `12/12` although that filter matches all four structural classes (`Total: 25`). Both corrected; the per-class counts are unchanged. |
| C4 | MINOR (record) | **Fixed.** Design §7 delta 5 and the "CONFIRMED (review, gate-removal window)" row still claimed the accepted UDP window was *pinned by* `SendAdmittedDuringExpiryTransitionIsDeliveredNotDropped` — the fact the session record (deviation 3) reports as not landed. Both now name the landed `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate` and record the gap instead of the pin. |
| C5 | MINOR (record) | **Fixed.** The artifact README's residual list now carries the two envelopes the operator must archive: (a) AC 1's ratio is demonstrated at the measured 4,096-flow working set, while a *full* 65,536-flow table has λ = 0.25 and design §2.1's own ~22 % modelled miss rate; (b) the stale-serve window of C1 with what would close it. |
| C6 | NOTE (verified, no change) | The listener-port prefilter is exact for the warm path: every association's `ReverseSourceEndpoint.Port` **is** its translated listener port in both shapes (`TcpRedirectTable.cs:33/41`), the count rises in the same `_gate` hold as the reverse-index write and falls in the same hold as its removal, and no reverse packet can be generated before the claim returns (`WarmReverseProbeIsExact…` reasoning in the report; pinned by `TcpReversePrefilterTests` + `TcpRedirectWarmPathGateTests`). The un-prefiltered `HandleReverseIfApplicableAsync` keeps the tombstone fall-through. |
| C7 | NOTE (verified, no change) | The new lock-free readers' publication order is sound on ARM64: `UdpSessionSlot.Session` is released before `Ready` under the gate and the reader acquires the cache slot → `Session` → validates `session.Flow` (a ctor-set readonly field) → `Ready`; the TCP/flow caches publish a fully constructed object with `Volatile.Write` and the reader acquires the slot before reading its get-only fields. |
| C8 | NOTE (verified, no change) | `ActivityBucket.Cutoff`'s zero rule (`idleTimeout <= 0 ⇒ long.MaxValue`) and never-early strict `<` are correct and covered by `ZeroIdleTimeoutStillRetiresEveryStateOnTheCall` + `FlowTableRetainsAStateUntilTheBucketAfterItsIdleWindow`; a backwards clock step still retires early relative to true time, which design §8 risk 8 records as an accepted, deliberately unguarded case. |
| C9 | MINOR (claim boundary) | **Fixed.** "A warm forward packet takes zero redirect gate entries" was stated unconditionally in `tcp-local-redirect.md`, `hot-path.md`, design §5 and the artifact's spec map. The listener-port prefilter is a *candidate* filter: a forward flow whose local port number is itself a live listener port (client and listener ports are both OS-assigned ephemerals) pays one gated `TryResolveByReverse` probe per packet while that listener exists — one gate entry fewer than the pre-change pair, but not zero. All four texts now carry the condition; the measured fact is unchanged (its packet's source port is not a listener port). |
| C10 | GATE (operator's `jb inspectcode`, 21 findings) | **Fixed.** `ActivityBucket`: `TicksPerBucket`/`FromUtcTicks` narrowed to `private` (no caller outside the type — verified by repository-wide `rg`), unused `BucketOf` deleted, `Publish` made `void` (both callers discard the value). `Domain.cs`: the two ambiguous `Volatile.Read`/`Volatile.Write` crefs became `<c>` — the compiler rejects `Volatile.Read(ref int)` with CS1574, and a `<c>` element cannot fail to resolve. `TcpRedirectTable`: the unresolvable `<see cref="ReferenceEquals"/>` qualified to `object.ReferenceEquals(object, object)`, and `GateEntryCountForDiagnostics` — previously unused — is now the counter `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads` asserts against (the UDP fact's shape). `UdpProxyCoordinator`: `ActivityClock` is an auto-property, `SessionReadyForDiagnostics` merges its checks into one pattern, and `UdpSessionSlot.Session`/`Ready` use the `field` keyword — same `Volatile.Read`/`Volatile.Write` accessors, no behaviour change. `UdpProxyCoordinator.Send`: the cache-hit guard merges into one property pattern (the pattern still reads `Session` before `Ready`). `SelfTrafficWarmPathGateTests`: the redundant `using WinForward.Core;` is gone (the enclosing `WinForward.Core.Tests` namespace already resolves those types). `UdpCoordinatorFakes`: dead `CountingTimeProvider.Advance` deleted, the throw converted to a conditional expression. `UdpWarmPathGateTests`: the four `AccessToDisposedClosure` sites restructured rather than suppressed — the coordinator is no longer `await using`; it is disposed in a `finally` after every capturing thread has been released and joined, which also removes the real failure-path hazard (disposing while a parked thread still runs). `DurableCaptureBundle.ActivityClock` was narrowed to `private` rather than suppressed: a repository-wide `rg` shows no friend-assembly reader (the tests drive the clock they inject and the coordinators' own `ActivityClock` properties), so this is not the `InternalsVisibleTo` false-positive class — the property's only reader is the bundle's own `FlushPendingInjections` tick. The known false-positive class applies to `FlowTable.ActivityClock` and `UdpProxyCoordinator.ActivityClock`, which tests do read and which inspectcode did not flag. |
| C11 | MINOR (artifact counts) | **Fixed.** Two filter counts in the evidence log did not reproduce on the frozen tree: `--filter "FullyQualifiedName~DurableCaptureBundleTests"` is **11/11** (10 HEAD facts + the new tick fact), not the recorded 15/15, and the `Dispatcher|SelfTraffic|ReversePrefilter` union is **48/48**, not 50/50. Both figures are corrected in `warm-path-gate-counts.txt` and the README with the correction noted; the code and the facts they stand for are unchanged. Every other quoted count re-derives exactly (suite 1021 + 18; `HotPathAllocationGateTests` 11; `SweepAllocationGateTests` 12; the four structural classes 12 + 4 + 2 + 7 = 25). |

## Session record (Steps 4–7, 2026-09-30)

Artifact: `benchmarks/results/2026-09-30-warm-path-lock-chain/` (README with the verdict tables, the
spec-row → proof map and the residuals; `warm-path-gate-counts.txt` with every exact count, its command
and the recorded red-before text; the before/after series and BDN exports). The PRD's last acceptance
bullet ("benchmark data recorded and cited in the task record before archive") is discharged there and
here; `prd.md` itself was not edited, per the operator's instruction.

**Outcome, one line per criterion.**

- AC 1 scaling: **met.** Warm arm, self-normalised four-thread ratio **0.933 / 0.957 / 0.980** over 3
  runs (`--duration 63`), one-thread arm 5.96 / 6.03 / 6.02 M/s (floor 3.00), recorded-baseline reading
  1.685 / 1.733 / 1.753 (line 0.6), measured cache miss rate 0.69–1.10 %, hit/miss counts in every
  verdict row. Four-thread absolute rate 22.46–23.36 M/s against 10.43–11.06 before.
- AC 2 no global lock on the warm resolve: **met exactly.** `WarmResolveTakesNoFlowTableGateEntries`
  (0/256), `WarmResolveCompletesWhileFlowTableGateIsHeld`, `ReverseResolveCompletesWhileRedirectGateIsHeld`
  (red recorded against the gated variant), `UdpReadySendCompletesWhileCoordinatorGateIsHeld` (red
  recorded against the admission-only variant); counts: flow table 1→0, redirect 2→0 gates and 2→1
  probes, UDP coordinator 1→0 and session activity gate 2→0.
- AC 3 self-traffic reorder: **met exactly.** `WarmHitTakesZeroExactTupleGuardProbes` (0 of 256 warm
  hits, 1 full check per claim), `EveryClaimCallsTheFullGuardExactlyOnce` (8 of 8), `ASelfOwnedTupleIsNeverClaimed`,
  and `ARelayWildcardTupleIsNeverProxiedOnAWarmHit` with its **naive-deletion red recorded**
  (`Expected: 1 / Actual: 2`, logged) before the split landed.
- AC 4 clock call gone: **met.** `Touch` writes one volatile bucket; the throwing-clock facts cover the
  flow table, the TCP redirect warm packet and the UDP ready datagram; the composition tick fact proves
  one tick per pump iteration; `ReadActivityClock` retired from the shape benchmark.
- AC 5 UDP ready path: **met.** `ReadySend` four-worker mean 186.1 / 187.8 / 186.8 ns over 3 runs
  (line ≤ 261.7 ns), one-/two-worker rows recorded beside, 0 B every row; no 4/1 ratio condition.
- Semantics preserved: 1021 + 18 = 1039 tests green, `HotPathAllocationGateTests` 11/11 (the 0 B
  claim/expire gate unchanged), `SweepAllocationGateTests` 12/12, the alias/exactly-once/tombstone
  families untouched and green.
- Build/format: Release build 0 warnings / 0 errors. `dotnet format` and `jb inspectcode` are the
  operator's gates and were deliberately not run.

**Deviations from the plan (code wins; each recorded in the artifact).**

1. **Redirect gate entries 2 → 0, not "with a 1-gate fallback on every forward packet".** The design's
   `ReverseResolveCompletesWhileRedirectGateIsHeld` + `TcpRedirectWarmPacketTakesZeroGateEntriesAndZeroClockReads`
   need zero gate entries; a direct-mapped reverse cache alone cannot prove absence on a miss, so
   `HandlePacketAsync` keeps the **listener-port prefilter** in front of the probe (a reverse tuple's
   source port is always a live listener port, so a prefilter miss *proves* the reverse index cannot
   match — the X1 invariant, already relied on by `WantsPacket`). The slow-path
   `HandleReverseIfApplicableAsync` deliberately does **not** prefilter, preserving the fall-through
   theorem for tombstone stragglers. One extra diagnostic counter
   (`TcpRedirectTable.ReverseProbeCountForDiagnostics`) was added because the fold's contract is a probe
   count; the plan's diagnostics list did not name it.
2. **`_count` not added.** The plan's Step-5 line and this task's brief mention a "maintained `_count`";
   design §5 and review disposition 16 supersede it (the four `Dictionary` authorities stay gated, so
   `Count` stays `_byOriginal.Count` under the gate and a counter would be a second source of truth).
3. **`SendAdmittedDuringExpiryTransitionIsDeliveredNotDropped` is not a deterministic test.** The
   window is a few instructions wide inside `TryBeginExpiry` under `_activityGate` and has no seam, so
   the landed fact is `SendIsAdmittedWhileTheSweeperHoldsTheActivityGate` — it parks the sweeper inside
   the gate via `ActivityGateHoldProbe` and proves the ready send neither waits nor is refused (the
   ordering that makes the delta benign), not the drop→send counter flip. **Reported as the task's one
   acceptance gap.**
4. **`ActivityBucket.Cutoff(now, idleTimeout)` centralised** the zero-timeout/never-early rule that
   Steps 2 and 5 had inlined at three sites (`FlowTable`, `TcpRedirectTable`,
   `UdpProxyCoordinator.RemoveExpiredAsync`), so one rule is stated once.
5. **One-thread cost recorded, not hidden.** The lock-free wildcard guard costs ~36 ns/lookup
   uncontended where the gated probe cost ~13 ns (`fake − warm` at one thread), and the warm cache probe
   is ~5–15 ns above the gated resolve in the shape benchmark (seqlock fences + canonical hash). The
   self-normalised ratio therefore also benefits from a lower denominator; the recorded-baseline reading
   (fixed denominator) is quoted beside it, and a COW wildcard snapshot is named as the lever if the
   operator wants the single-thread number back. Net measured: near-linear scaling and a 2.0–2.2×
   four-thread rate.

**Commit plan status** (the skeleton above is unchanged; no commit was made — the operator commits):

```text
0  test(bench): warm-resolve arm + gate/clock probes; scaling before-artifact        [Step 1, landed]
1  perf(flow): bucketed lock-free activity stamps; injected-clock unification (D9)  [Step 2, landed]
2  perf(flow): direct-mapped warm cache over the gated dictionary                   [Step 3, landed]
3  perf(flow): self-traffic exact half to claim time; wildcard half lock-free        [Step 4, this session]
4  perf(tcp): one reverse probe + warm reverse/original caches + factored removal    [Step 5, this session]
5  perf(udp): ready-first wait-free send path; cached session lookup                 [Step 6, this session]
6  feat(capture): one activity clock wired through the bundle, ticked per iteration  [Step 7, this session]
7  docs(evidence): before/after artifacts + the six spec restatements + this record  [Step 7, this session]
```
