# Implementation plan — F8 process attribution off the pump thread (pending-flow pipeline + owner-table epoch coalescer)

One instrumentation step that lands **first** (the acceptance scenario, the counting/gated attributor fake and
the owner-table scan seam exist before any product change, so before and after come from the same instrument),
then four independently revertible product steps, then the evidence/spec work. Every product step lands its
exact proof in the same commit, so every commit leaves a green, shippable tree; transient red readings are
captured during the step and recorded in the artifact README (F2/F3/F4/F5 precedent).

Prerequisites already true: `dotnet` via direnv, `rg`/`fd`, the F2/F3/F4/F5 artifacts under
`benchmarks/results/2026-09-30-*` and `2026-10-01-pump-io-shape/`, and the reproduce-before series under
`.trellis/tasks/10-01-attribution-off-pump/research/reproduce/`.

**Read before starting**: `research/implementation-notes.md` (the exact sequences, the two templates, the
hazard list, discrepancies D1–D16, the reproduce-before numbers), `design.md` §2–§6, and the PRD as amended
(AC-1 counts attributions **per admitted pending entry** and requires re-admissions to be counted; AC-4 is
"one shared scan per coalesced epoch + one forced rescan for a late socket", a recorded series). The amendments
fold in the planning review's findings: the claim stays on the pump at the delivery point, the snapshot is
TCP-positive-only with UDP coalescing-only, the movement series comes from a new `attribution` scenario
(`tcpChurn` never runs the pipeline), and the ~91 KB churn number is a ceiling, not attribution's cost.

## Step 0 — Preflight

- [ ] Re-verify every `file:line` anchor in `research/implementation-notes.md` §1–§8 against the current
      source (`rg -n` the named members; they were read on tree `ba51205`). The anchors that the review
      corrected are marked in §13 of those notes — use those, not the earlier draft's.
- [ ] Record the pre-change suite totals so a green tree is proven before the first edit:
      `dotnet test WinForward.slnx -c Release` and the per-gate totals string (`hot-path.md:1034-1041`,
      `:1295-1303`): `HotPathAllocationGateTests:<n> CapturePumpReadCallTests:<n> SweepAllocationGateTests:<n>
      NdisCapturePumpTests:<n>` — two figures move whenever a fact is added, so refresh them here.
- [ ] Confirm the baselines exist and quote them into the artifact README's "before" section:
      `benchmarks/results/2026-09-29-benchmark-coverage/tcp-churn-{baseline,attribution-delay,attribution-delay-1pct}.jsonl`
      and the three fresh runs under `research/reproduce/`.
- [ ] `git status` clean; create the branch/worktree; note the base revision in every artifact header.
- [ ] Enumerate the call sites the API changes touch, so no red compile is discovered late:
      `rg -n 'AttributeProcessAsync|TryClaimResolved|FlushPendingInjections|PacketDisposition\.' src/ tests/`
      (expected: `FlowDispatcher` 2 call sites; `FlowTable` 1 definition + ~15 test claims;
      `DurableCaptureBundle.FlushPendingInjections` 1 site + its test; the ~8 `Disposition` assertions listed
      in implementation-notes §7.10); and `rg -n 'DefaultRingCapacity' src/ tests/`
      (the const + `SetupExecutorTests.DefaultRingCapacityCoversThePendingSynIndexCap:165-174`).
- [ ] Write the budget arithmetic into the artifact README before any code: entry cap 1024, global budget
      8 MiB, per-flow ring 32 packets, TTL 5 s, cooldown 1 s, and the **finite** ring relation
      `DefaultRingCapacity (2048) >= 1024 + 1024`. State explicitly that a UDP setup item can still be refused
      when the ring is full (`UdpProxyCoordinator.Send.cs:88` enqueues one item per admitted session against a
      16,384 session capacity) and that this is accepted, counted (`RuntimeCounters.UdpSetupRejections`) and
      fail-closed — there is no satisfiable inequality that also covers UDP.

## Step 1 — Instrument first: the acceptance scenario, the fakes, the scan seam, the red-before  [rollback point A]

Nothing here changes product behaviour except one additive internal seam; all of it is report-only.

- [ ] `tests/WinForward.Core.Tests/TestHelpers/CapturePipelineFakes.cs`: add, beside the existing
      `FakeAttributor` (keep it byte-for-byte — 4 facts use it), a **gated** attributor that records
      `Environment.CurrentManagedThreadId` per call, blocks on a `TaskCompletionSource`, sleeps a configured
      cost, and can be made to throw or to observe cancellation. Add a **counting owner-table reader fake**
      implementing the Step 2 interface (scripted rows, `ReadCount`, and a per-request scripting hook) so
      Step 1's instrument and Step 2's seam land together.
- [ ] `benchmarks/WinForward.Benchmarks/Stability/AttributionOffPumpScenario.cs` (new, ≤400 effective lines,
      `--scenario attribution`): drive the **real** `FlowDispatcher` from one dedicated "pump" thread over a
      scripted packet sequence through `CapturePacketProcessor.ProcessAsync` (or the dispatcher directly, with
      a real `NdisPacketActionExecutor` over a fake reinjector), with an attributor that sleeps
      `--attribution-cost-ms` and records the calling thread. Report: `newFlows`, `admittedEntries`,
      `attributionsOnPumpThread`, `attributionsOnSetupWorker`, `reAdmissions`, `pumpBlockedMs`
      (p50/p95/p99/max), `firstPacketDelayMs`, `deliveredPackets`, `refusedPackets`, `ownerTableScansPerBurst`,
      `gen0Collections`, `allocatedBytesPerNewFlow`, `gated: false`, and a `note` stating that the row measures
      the **pipeline**, never `iphlpapi`.
- [ ] Add the `--attribution-cost-ms` option (default 0 = the control arm). The scenario must run **unchanged**
      on the pre-change tree (where the same fake is called inline and the pump thread blocks) — that is what
      makes the before/after series attributable to this change.
- [ ] Capture the **before** series, one run per file then concatenated (the runner truncates `--output` per
      process): `mkdir -p benchmarks/results/2026-10-01-attribution-off-pump`; 3 ×
      `--scenario attribution --quick --attribution-cost-ms 7` → `attribution-off-pump-before.jsonl`, plus the
      `--attribution-cost-ms 0` control. Record in `attribution-before-counts.txt`: the exact pump-thread
      attribution count (**N**, the red-before for AC-1), the `pumpBlockedMs` p50 at the injected cost, the
      command, the tree revision, and that no Windows table read ran.
- [ ] Re-run the three `tcpChurn` commands (implementation-notes §11) into
      `benchmarks/results/2026-10-01-attribution-off-pump/tcp-churn-*.jsonl`, diff against
      `research/reproduce/`, and record the pooled-p99 arithmetic in the README **including** the fact that the
      1 % arm's pooled p99 lands back in the stable class (a host outlier) and is not an acceptance arm.
- [ ] Start `benchmarks/results/2026-10-01-attribution-off-pump/README.md`: host/runtime header, base
      revision, commands, the before table, the budgets section (Step 0), the exact-vs-series rules, and the
      statement that the churn row contains no attribution work.
- [ ] Write the new allocation gate **red** (red by construction until Step 4):
      `PendingAttributionAdmitAllocatesOnlyTheDocumentedColdBudget` — the F8 analogue of
      `UdpSetupEnqueuePathAllocatesNoManagedBytes` (`HotPathAllocationGateTests.cs:217-244`). It asserts the
      documented per-new-flow budget (one entry object + one 32-slot ring array + one lease rent + one ring
      enqueue) and **0 B for packets 2..32 of an already-pending flow** (the ring is pre-allocated, so the
      steady append is a native `memcpy` under an existing lease); record its red text.

Validation for the step: the scenario builds and produces both arms with non-vacuous counters; the fakes are
used by at least one fact; the before artifacts exist. No product file is touched.

Rollback: revert the scenario, the options, the fakes and the seam; delete the before artifact.

## Step 2 — The owner-table seam, the epoch coalescer and the snapshot — **rollback point B**

- [ ] `src/WinForward.Windows/IProcessOwnerTableReader.cs` (new): `internal interface IProcessOwnerTableReader
      { OwnerTable Read(OwnerTableKind kind); }` + `OwnerTableKind { Tcp4, Tcp6, Udp4, Udp6 }` + the shared
      parsed row types.
- [ ] `src/WinForward.Windows/IPHelperOwnerTableReader.cs` (new): today's `ReadTable` + `ReadTcp4/6` +
      `ReadUdp4/6` (`ProcessAttribution.cs:198-287`) moved verbatim, **including the size probe, the
      `ValidateRowCount` fail-closed check and the `finally { Marshal.FreeHGlobal }`**. This becomes the single
      `[SupportedOSPlatform("windows")]` boundary and reports "table unavailable" off-Windows.
- [ ] `src/WinForward.Windows/ProcessOwnerTableCache.cs` (new): per-kind snapshot slot + single-flight refresh
      with the **request-instant** rule (`design.md` §3.2), the exact `FindUdpOwner`/`FindTcpOwner` predicates,
      a configurable window (`OwnerTableCacheWindowMs`, default 300, `0` = coalescing only), per-kind
      **positive-caching** flags (TCP on, UDP off), and an `OwnerTableScanCount` counter.
- [ ] `src/WinForward.Windows/ProcessAttribution.cs`: `FindOwnerSafely` consults the cache — attempt 1 with
      `requestInstant = now`, and after the 2 ms delay attempt 2 with a **later** `requestInstant` (not a
      `forceRefresh` flag: the instant rule is what makes both paths coalesce); the platform guard at `:30`
      moves into the reader; `WindowsProcessAttributor` gains an `internal` ctor parameter. **A miss stays a
      miss** — `null` reaches `FlowDispatcher.cs:286-290` unchanged.
- [ ] Facts (`ProcessOwnerTableCacheTests`, new): `NConcurrentMissesInsideTheWindowReadTheTableExactlyOnce`,
      `ARetryThatJoinsALaterEpochDoesNotReadAgain` (the review's MAJOR 7 — a retry must accept a snapshot
      published at/after its request instant), `ARequestWhoseSocketBoundAfterTheReadForcesExactlyOneMoreRead`,
      `ACachedTcpHitAnswersWithoutARead`, `AUdpLookupNeverAnswersFromTheSnapshot`,
      `ASnapshotOlderThanTheWindowForcesARead`, `TheSnapshotLookupAgreesWithTheScanForEveryScriptedRow`,
      `TheRecycledPortRowIsOnlyServedForTcp`, `WindowZeroKeepsTheCoalescingAndDropsTheReuse`.
- [ ] Keep `IPHelperTablesBoundsTests` green untouched.

Rollback: delete the cache + seam, restore the guard and `FindOwnerSafely`'s direct call.

## Step 3 — The pending index: ring, budget, state machine, counters — **rollback point C**

- [ ] `src/WinForward.Runtime/FlowAttributionPendingIndex.cs` (new, ≤400 effective lines): the
      `TcpPendingSynSetupIndex` shape (leaf `Lock`, `_pending` dictionary, global Interlocked byte budget
      charged on retain and credited **exactly once** at every sink, entry cap, TTL, 1 s cooldown, one counter
      per refusal class) with a **fixed-capacity per-entry ring** (`RetainedPacket[32]` + head/count, allocated
      once with the entry, carrying `NativeLease`/`Length`/`Metadata`/`PacketSequence`/`Layout`/`EnqueuedAt`).
      **Do not use `BoundedSetupQueue` as the payload** — its entry cannot carry metadata and it grows a
      `Queue<Entry>` on the second packet (`BoundedSetupQueue.cs:19,59`). One decided **queue per adapter
      handle** lives here too, with `_decidedCount` incremented on enqueue and decremented in the same critical
      section as the dequeue.
- [ ] Facts (`FlowAttributionPendingIndexTests`): `TheEntryCapRefusesExactlyOncePerRefusedPacket`,
      `TheGlobalBudgetIsConservedAcrossEverySink` (append, drain, TTL, refusal, claim failure, dispose),
      `TheRingKeepsThePrefixAndRefusesTheNewcomer`, `AFrameLargerThanThePoolBufferIsRefusedBeforeTheCopy`,
      `ATtlReclaimIsIdempotentWithTheRunnersCompletion`, `TheCooldownIsArmedOnlyOnAGenuineFailure` (and **not**
      on shutdown cancellation), `TheDisposeDrainSettlesAndReleasesEveryEntry`,
      `TheDecidedQueueDecrementsItsCountInTheSameCriticalSectionAsTheDequeue`.
- [ ] Record the drop-oldest rejection rationale in the type's XML doc (the head is the flow's triggering
      packet; `udp-relay.md`'s freshness argument does not transfer) and cite `design.md` §2.1.

Rollback: delete the file and its facts.

## Step 4 — The pipeline: admission, worker, delivery, the claim overload — **rollback point D**

- [ ] `src/WinForward.Runtime/FlowAttributionPipeline.cs` (new, ≤400 effective lines): `Admit` (eligibility
      gate first, then the pending → flow re-check under the gate, per `design.md` §2.1), the worker body
      (§2.2 with the shutdown-vs-failure classification), `DeliverDecided` + `DeliverEntry` (§2.3's three
      phases, the per-batch `finally`, the claim-last critical section, the `ClaimFailed` keep-and-retry
      rule), and the diagnostics snapshot.
- [ ] `src/WinForward.Core/PacketRuntime.cs`: add `PacketDisposition.Deferred`.
      `src/WinForward.Core/FlowTable.cs`: add the allocation-free `TryClaimResolved(FlowKey, FlowDecision, out
      FlowState?)` overload beside the factory overload.
- [ ] `src/WinForward.Runtime/FlowDispatcher.cs`: extract `ShouldAttribute(context)` from
      `AttributeProcessAsync` and call it from **both** paths; `DispatchSlowAsync`'s miss path (`:236-245`)
      routes eligible flows through `Admit` (admit → `Deferred`; refuse → Block + count) and **every other
      shape keeps today's inline path byte-for-byte**.
- [ ] Facts (`FlowAttributionPipelineTests`, `FlowDispatcherAttributionTests`):
      AC-1 — `NoAttributionRunsOnThePumpThread`, `ThePumpIterationReturnsWhileAttributionIsStillParked` (with
      the pre-change red recorded), `OneAttributionPerAdmittedEntry`,
      `AFailedClaimIsCountedAndDoesNotReAttributePerPacket`, `ASecondKeyForOneTupleIsCountedAsAReAdmission`;
      AC-2 — `PacketsAreDeliveredInArrivalOrderAfterTheDecision`, `NoPacketIsDeliveredTwice`,
      `AClaimIsPublishedOnlyAfterTheLastPendingPacket`,
      `APacketArrivingDuringDeliveryIsDeliveredAfterTheEarlierOnes`,
      `AFailingSetupBlocksEveryPendingPacketAndNothingIsStranded`,
      `AThrowingExecutorBlocksTheBatchRemainderAndNothingIsDetached`, `AnEnqueueDuringADrainDoesNotDisturbIt`
      (the review's BLOCKER 2), `AShutdownCancellationArmsNoCooldownAndCountsNoFailure`;
      eligibility — `AForwardedMissNeverCreatesAPendingEntry`, `ANoProcessRuleMissNeverCreatesAPendingEntry`,
      `ASealedPipelineBlocksInsteadOfAttributingInline`;
      bounds — `APendingFlowTakesTheFullSlowPathAtMostThePerFlowRing` (the §5.4.5 accepted cost);
      lock order — `ThePipelineNeverTouchesTheFlowGateWhileHoldingItsOwn` (parked-gate proof).
- [ ] Keep the existing dispatcher facts green untouched — in particular
      `DispatcherAttributionsHostFlowsButNotForwarded`, `DispatcherSkipsAttributionWhenPolicyHasNoProcessSelectors`,
      `DispatcherFallsBackWhenHostAttributionIsUnknown` (`FlowDispatcherExecutorTests.cs:127`).

Rollback: revert the dispatcher to the inline path and delete the pipeline + the two additive API members.

## Step 5 — Composition: pool, ownership order, ring relation, sweeper leg, wake — **rollback point E**

- [ ] `src/WinForward.Cli/DurableCaptureBundle.cs`: create `_attributionCopyPool` (a **new**
      `NativeBufferPool`, sized to the maximum capture frame, capacity ≈ 8 MiB / bufferSize — **not**
      `synCopyPool`/`udpDatagramPool`/`relayPool`), create the pipeline with it, pass the pipeline to the
      dispatcher, and add the delivery to `FlushPendingInjections` (`:397-405`): `ActivityClock.Tick()` →
      `Pipeline.DeliverDecided(adapterHandle)` → `Executor.FlushPendingPasses` → `Tcp.FlushPendingRedirectInjections`
      (the delivery must precede the pass flush so delivered frames go out in the same iteration's batch, and it
      must stay inside this callback so a loop-exit delivery is still flushed before `RetireLanesExcept` can drop
      it). Dispose the pipeline immediately after `_sweeper` (`:420`); dispose `_attributionCopyPool` in the last
      block beside `_synCopyPool` (`:455-456`).
- [ ] `src/WinForward.Runtime/IdleExpirySweeper.cs`: a new leg calling `pipeline.RemoveExpired(now)` each tick,
      allocation-free in the `SweepAllocationGateTests` shape (`hot-path.md:1024-1033`): one reused scratch
      list, each candidate re-checked under the pipeline gate, disposal outside it. Facts:
      `AttributionTtlReclaimsUnderTheSweepGate` (the `SweepAllocationGateTests` population shape) and an
      entry-allocation gate for the tick.
- [ ] `src/WinForward.Runtime/SetupExecutor.cs`: the third payload plane (`_flow`) on `SetupWorkItem` beside
      `_tcp`/`_udp`; `DefaultRingCapacity` 1024 → 2048 with the **finite** relation of the README (and the
      `SetupExecutorTests` fact updated to sum both producer caps, not weakened).
- [ ] `src/WinForward.NdisApi/CompositePacketArrivalSignal.cs` (new) + the wake registry: the composite wraps
      the borrowed driver signal and one owned auto-reset event, `WaitHandle.WaitAny` under the same timeout.
      **Construction site**: the signals are created in `NdisCaptureGeneration.RegisterArrivalSignals`
      (`:140,167`) — `MultiAdapterCaptureLoop` only receives and disposes the list — so the registry is composed
      there and handed to the pipeline; the loop keeps disposing the composite list after the pumps stop. Facts:
      `TheCompositeWaitsOnBothHandlesWithTheSameTimeout`, `ThePipelineSignalsOnlyItsOwnEvent`,
      `TheRegistryDisposesOnlyTheEventItOwns`, and a **new or re-pointed** idle-wait allocation gate covering
      `CompositePacketArrivalSignal.Wait` (unsignaled, zero timeout) in the
      `NdisCapturePumpIdleWaitTests.IdleWaitIterationsAllocateNoManagedBytes` shape.
- [ ] Config surface: `OwnerTableCacheWindowMs` (default 300, `0` allowed, out-of-range rejected) mapped from
      JSON with validation and a fact for both edges.
- [ ] Composition facts: `TheBundleDisposesThePipelineBeforeTheRetentionPool`,
      `ThePipelineSealsBeforeTheSetupExecutorJoinsItsWorkers`,
      `TheDeliveredFramesAreFlushedInTheSameIterationTheyWereExecuted` (the F1 loop-exit protection).

Validation for the step: full suite green; `SetupExecutorTests`' relation fact updated; the arrival-signal and
pump idle tests green; `DurableCaptureBundleTests.ActivityBucketClockTicksExactlyOncePerPumpIteration` green.

Rollback: revert the bundle + ring constant (the dispatcher then falls back to the inline path); the composite
signal, the sweeper leg and the retention pool are each independently revertible.

## Step 6 — Evidence, spec rows, record

- [ ] Run the **after** series (3 runs of `--scenario attribution --quick --attribution-cost-ms 7` + the 0
      control), concatenate per file into `attribution-off-pump-after.jsonl`, and write the README's
      before/after table with the exact-vs-series labels from `design.md` §7.
- [ ] Re-run the must-not-move rows: the three `tcpChurn` arms, `gc-soak --quick`, `udp --quick`, the four
      allocation classes (with the spec's repeat-run procedure where a gate is an exact window,
      `hot-path.md:963-1069`), `NdisPacketActionExecutorBatchingTests` /
      `TcpRedirectInjectionBatchingTests` (the amended PRD's F1 rows), and copy the raw outputs in.
- [ ] `class-totals.txt` and `gate-stability.txt` (the per-gate proof loop; the new gate's injected-allocation
      discrimination demonstrated and restored — `hot-path.md:1057-1060`).
- [ ] Spec edits per `design.md` §11.
- [ ] Record the Windows open items (`design.md` §9) verbatim in the README, plus the residue the operator
      must carry (the deferred UDP snapshot, the accepted UDP ring refusals, the 60 s TTL sweep cadence, the
      bounded slow-path re-entry).
- [ ] `research/implementation-notes.md`: fold the measured results back in so the archive carries the landed
      truth, not the plan.
- [ ] Append the review dispositions below (Step 6's last artefact).

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                     # zero-warning
dotnet test WinForward.slnx -c Release                                      # full suite green
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~FlowAttribution"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~ProcessOwnerTableCacheTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~PendingAttributionAdmitAllocatesOnlyTheDocumentedColdBudget"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~HotPathAllocationGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WarmPathGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisPacketActionExecutorBatchingTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~TcpRedirectInjectionBatchingTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCapturePumpIdleWaitTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~SweepAllocationGateTests"
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue>

# The F8 series — BEFORE on the pre-change tree, AFTER on the landed tree, 3 runs each, one run per file
# then concatenated; every run carries an explicit --output.
mkdir -p benchmarks/results/2026-10-01-attribution-off-pump
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario attribution --quick --attribution-cost-ms 7 \
    --output /tmp/wf-f8-after-$r.jsonl
done
cat /tmp/wf-f8-after-{1,2,3}.jsonl > benchmarks/results/2026-10-01-attribution-off-pump/attribution-off-pump-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario attribution --quick --attribution-cost-ms 0 --output /tmp/wf-f8-control.jsonl

# tcpChurn calibration + must-not-move — 5 % arm only (the 1 % arm is recorded as invalid).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario tcpChurn --quick --rate 60 --tcp-concurrency 8 --payload-bytes 4096 \
  --output /tmp/wf-f8-churn-baseline.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario tcpChurn --quick --rate 60 --tcp-concurrency 8 --payload-bytes 4096 \
  --attribution-delay-ms 50 --attribution-delay-percent 5 \
  --output /tmp/wf-f8-churn-delay5.jsonl
# the 1 % arm is re-run for the record only:  --attribution-delay-percent 1

# Must-not-move companions.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp --quick
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*CapturePump*' --job short
```

## Acceptance-criteria + spec-row → proof mapping

| Amended PRD criterion / spec row | Where discharged | Kind |
|---|---|---|
| **AC-1 off-pump, per admitted pending entry**: zero attributions on the pump thread, exactly one per admitted entry on a setup worker, re-admissions (failed claim, second key) counted, red-before recorded | Step 1's before arm + the gated counting fake → Step 4's exact-count facts + the `reAdmissions` field. The `pumpBlockedMs` series is quoted beside the counts | exact + series |
| **AC-2 order and exactly-once**: in-order delivery, no duplicate, a failing setup releases the pending packets under the refusal rules | Step 4's seven facts (order, no-double, claim-last, append-during-delivery, failure-blocks-all, throwing-executor remainder, shutdown classification) | exact |
| **AC-3 bounded (exact)**: a counted refusal beyond the limit, never grows past it, the refusal observable | Step 3's cap/budget/ring/conservation facts + Step 4's sealed and enqueue-refusal facts; every counter named and located (`design.md` §2.4) | exact |
| **AC-4 snapshot (exact + series)**: one shared scan per coalesced epoch plus one forced rescan per late socket; a miss falls back to a fresh scan and refreshes; the staleness bound asserted; UDP proven not to serve from cache | Step 2's nine facts + the scenario's `ownerTableScansPerBurst` **series** | exact + series |
| **AC-4 movement**: the new `attribution` scenario arm's exact thread counts + `pumpBlockedMs` series; `tcpChurn` at the 5 % arm as calibration + must-not-move; the pooled percentile recorded explicitly; the 1 % arm recorded as invalid | Steps 1/6, the artifact README | series |
| **Allocation**: the pump-loop 0 B gates stay green; the attribution path's own allocation gated separately; ~91 KB is the churn ceiling, not attribution's cost | The four existing gates unchanged; `PendingAttributionAdmitAllocatesOnlyTheDocumentedColdBudget` (red in Step 1, green in Step 4); the churn `allocatedBytesPerConnection` must-not-move row | exact |
| **F1 regression surface**: `NdisPacketActionExecutorBatchingTests`, `TcpRedirectInjectionBatchingTests` stay green; the deferred delivery must not reopen the lane contract | Step 5's `TheDeliveredFramesAreFlushedInTheSameIterationTheyWereExecuted` + the two suites in the validation block | exact |
| **Semantics preserved** | Step 4's eligibility negative facts (forwarded, no-process-rule, already-attributed) + the shared `ShouldAttribute`/`AttributeProcessAsync`; Step 2's scan/snapshot equality oracle; the existing dispatcher facts green untouched; the §5.4 delta facts (whole-run exception, bounded slow-path re-entry, cross-adapter re-admission); the lifecycle suites | exact |
| **Existing gates** | Validation block: full suite, F2/F3/F4/F5 classes, the four allocation classes, gc-soak, UDP/TCP scenarios, the new idle-wait gate for the composite signal | exact |
| **AC 8 Windows-only paths behind their seam** | `design.md` §9 verbatim in the README; `attribution-before-counts.txt` states that `iphlpapi` never ran; every fact labelled seam-level | artifact |
| Release zero-warning, suite green, format empty, inspectcode zero | Validation block, at every rollback point | exact |
| Spec `hot-path.md:1034-1041` / `:1295-1303` (exact-window class list + totals) | Step 5's new gate + Step 6's `gate-stability.txt` | exact |
| Spec `hot-path.md:1398-1401` (self-traffic half once per claim) | Step 4's bounded slow-path fact + Step 6's edit | exact |
| Spec `traffic-policy-lifecycle.md:9,16` (forwarded eligibility, evaluation location) | Step 4's eligibility facts + Step 6's edit | exact |
| Spec `async-lifetime.md:325-371` / `:125-130` (per-owner notes, `SetupExecutor` ring note) | Step 5's ownership facts + the finite relation + Step 6's edits | exact |
| Spec `windows-ndisapi.md:416` (pump ordering relaxation) | Step 5's delivery hook fact + Step 6's edit | exact |
| Spec `error-handling.md` (shutdown vs genuine failure) | Step 4's `AShutdownCancellationArmsNoCooldownAndCountsNoFailure` + Step 3's cooldown fact | exact |

## Artifacts

| Path | Content |
|---|---|
| `benchmarks/results/2026-10-01-attribution-off-pump/README.md` | host/runtime header, base revision, commands, budgets + the finite ring relation, before/after tables, exact-vs-series and attribution rules, the pooled-p99 arithmetic and the 1 %-arm warning, the Windows-open-items table, the residue |
| `…/attribution-off-pump-before.jsonl` | 3 pre-change runs of the new scenario |
| `…/attribution-off-pump-after.jsonl` | 3 landed runs, same instrument |
| `…/attribution-control.jsonl` | the `--attribution-cost-ms 0` control arm |
| `…/tcp-churn-*.jsonl` | the calibration + must-not-move churn arms (5 % arm authoritative) |
| `…/attribution-before-counts.txt` | the red-before: exact pump-thread attribution counts, the red budget-gate text, the tree revision, and the statement that no Windows table read ran |
| `…/gate-stability.txt` | the per-gate proof loop + the new gates' injected-allocation discrimination |
| `…/class-totals.txt` | per-class totals, tree revision, run counts |
| `.trellis/tasks/10-01-attribution-off-pump/research/reproduce/*.jsonl` | the planning-time reproduce-before churn runs |

## Risky files

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Runtime/FlowDispatcher.cs` | The miss path is the whole change: a wrong eligibility test defers flows that must not be deferred (forwarded/no-rule) or leaves an eligible flow inline; a missed `Deferred` completion leaks or double-releases the lease | Step 4 (rollback point D) |
| `src/WinForward.Runtime/FlowAttributionPipeline.cs` (new) | The §2.3 phases are load-bearing: a failure arm that claims, a Phase C that runs before the last packet, a batch left detached on a throw, or a claim failure that deletes the entry silently reorders, strands, or re-attributes per packet | Step 4 |
| `src/WinForward.Runtime/FlowAttributionPendingIndex.cs` (new) | Credit-exactly-once at every sink (including claim failure and dispose); the decided queue's count must fall in the same critical section as its dequeue or the 0 B fast path never returns to zero | Step 3 (rollback point C) |
| `src/WinForward.Core/FlowTable.cs` | The new claim overload must not disturb the existing factory overload's exactly-once/capacity semantics; the delivery's claim runs under the pending gate — the lock order is a deadlock surface | Step 4 |
| `src/WinForward.Core/PacketRuntime.cs` | The `Deferred` member is a public enum addition; `TryComplete`'s exactly-once callback must still fire on the deferred path | Step 4 |
| `src/WinForward.Windows/ProcessAttribution.cs` | The guard move and the cache insertion decide whether a *wrong* PID can be returned; the miss path must stay a miss | Step 2 (rollback point B) |
| `src/WinForward.Windows/ProcessOwnerTableCache.cs` (new) | The UDP positive-cache flag is the fail-open boundary; the request-instant rule decides whether a burst's later sockets are visible and whether a retry scans twice | Step 2 |
| `src/WinForward.Cli/DurableCaptureBundle.cs` | Ownership/order and the delivery hook: a delivery after the pass flush (or outside the callback) strands frames at loop exit; a pipeline disposed after its pool is a use-after-dispose | Step 5 (rollback point E) |
| `src/WinForward.Runtime/IdleExpirySweeper.cs` | The new leg must be allocation-free on the tick and must not hold the pipeline gate across disposal | Step 5 |
| `src/WinForward.Runtime/SetupExecutor.cs` | The ring relation is an asserted inequality (a test, `:165-174`); raising it changes sizing and must be stated as the finite sum | Step 5 |
| `src/WinForward.NdisApi/CompositePacketArrivalSignal.cs` (new) + `NdisCaptureGeneration` | Wait-handle ownership (owned internal event vs borrowed driver event), the new idle-wait allocation contract, and the generation-site wiring | Step 5 |
| `benchmarks/…/Stability/AttributionOffPumpScenario.cs` (new) | The acceptance instrument: a scenario that models the dispatcher wrongly, or drops the control arm, silently voids the series | Step 1 revert |
| `tests/**` expectations | `Deferred` and the claim overload touch ~8 assertions; a mechanical rewrite can weaken one into a tautology — re-read each | per step |

## Commit plan skeleton

```text
0  test(bench): attribution-off-pump scenario + gated/counting attributors + owner-table
   reader fake + the red budget gate; before-series and the churn calibration      [Step 1, rollback A]
1  perf(windows): injectable owner-table reader; request-instant epoch coalescer;
   TCP-positive snapshot with UDP caching off + the exact scan-count facts           [Step 2, rollback B]
2  perf(flow): bounded pending-attribution index (fixed ring, caps, global budget,
   decided queues, TTL, cooldown, one counter per refusal) + the conservation facts  [Step 3, rollback C]
3  perf(flow): off-pump attribution pipeline — eligibility-gated deferred admission,
   setup-worker attribution/policy, pump-thread in-order delivery with the
   claim-last critical section + the Deferred disposition and the claim overload     [Step 4, rollback D]
4  feat(capture): retention pool, bundle ownership order, delivery hook inside
   FlushPendingInjections, sweeper leg, finite ring relation, the composite wake     [Step 5, rollback E]
5  docs(evidence): before/after artifacts, gate stability, class totals, spec rows,
   Windows open items                                                                [Step 6]
```

Steps 3 and 4 may need to be one commit if the intermediate tree does not compile; if so, split the *facts*
rather than the product change. Step 5's composite signal and sweeper leg are each independently revertible
inside their commit.

## Deferred / explicitly out of scope

- **The UDP positive snapshot** (`design.md` §3.3): needs the `*_TABLE_OWNER_MODULE` creation timestamp to
  reject a recycled row; the table class is part of the pinned Windows API surface the PRD puts out of scope.
- **A satisfiable UDP ring budget**: accepted as refusals, counted (`UdpSetupRejections`) and fail-closed.
- **An admission-side short-circuit for a pending flow's slow-path re-entry** (`design.md` §5.4.5): recorded
  as a bounded accepted cost instead.
- A per-window control that can tell a host allocation lump from a driven allocation (closed by
  `09-30-exact-gate-residual-lumps`).
- The research's §A4 FlowTable rebuild, F6/F7, and anything about what attribution *means*.
- Windows-only rows (W1–W6 consume `design.md` §9; nothing here fakes a hardware number).
- The TTL's own sweeper cadence: the delivery path normally ends an entry's life; the sweep is the backstop,
  and the 60 s main-leg discrepancy is recorded (implementation-notes D6).

## PRD-versus-code conflicts (all resolved in the amended PRD; recorded for the archive)

| # | Conflict | Evidence | Resolution |
|---|---|---|---|
| 1 | The claim on a worker vs the ordering linchpin | `design.md` §2.3 | Amended req 1 fixes the claim on the pump at the delivery point |
| 2 | A TTL snapshot cannot serve a burst's own new flows | implementation-notes §5 | Amended req 4/AC-4 make coalescing the mechanism and the scan count a series |
| 3 | `tcpChurn` does not run the pipeline | `TcpChurnScenario.cs:46-137`; `SoakOptions.cs:169-181` | Amended AC-4 adds the `attribution` scenario and demotes `tcpChurn` to calibration + must-not-move |
| 4 | ~91 KB is the churn path's own cost, not attribution's | `README.md:192-194` vs the no-attributor scenario; 91,088/91,066/91,031 B reproduced | Amended AC "Allocation" states it |
| 5 | The pooled-p99 movement only exists at a 5 % delayed share | pooled p99 at 1 % = the stable max (88.1 ms) | Amended AC-4 fixes the 5 % arm and requires an explicit pooled percentile |
| 6 | The F1 lane contract is part of the regression surface | `NdisPacketActionExecutor.cs:144-153` | Amended AC adds the two F1 suites |

## Review dispositions (independent review, for the archive)

| # | Finding | Disposition |
|---|---|---|
| 1a | The failure arm claimed with a null decision | Fixed: `DeliverEntry` Phase A branches on `State == Failed` **inside the gate hold** — Block the drained batch, count, arm the cooldown unless shutdown, never claim (`design.md` §2.3) |
| 1b | A false `TryClaimResolved` was unchecked | Fixed: Phase C keeps the entry (`ClaimFailed`), Blocks its later packets fail-closed, counts `AttributionClaimFailed` + `FlowCapacityBlock` and surfaces `flow.capacity-block`; the claim is retried once per arrival |
| 1c | The "already decided → deliver now" branch did not say what happens to the triggering packet | Fixed: `design.md` §2.1 — the triggering packet is appended to the ring first, then the inline `DeliverEntry` runs, so it is delivered with its predecessors |
| 2 | `_decidedByAdapter[handle]` was mutated while enumerated | Fixed: the decided set is a **queue dequeued under the gate**; `_decidedCount` is decremented in the same critical section; fact `AnEnqueueDuringADrainDoesNotDisturbIt` |
| 3 | A detached batch could be stranded | Fixed: a per-batch `finally` Blocks the unexecuted remainder and counts it; fact `AThrowingExecutorBlocksTheBatchRemainderAndNothingIsDetached` |
| 4 | Cross-adapter delivery was unreachable and unsafe | Fixed: the per-adapter ownership invariant states admission/delivery/drain happen only on the owning adapter; the cross-adapter paragraph is deleted and the duplicate-entry/policy delta is recorded (§5.4.6) and counted |
| 5 | Admission was not restricted to the attribution-eligible shape | Fixed: `ShouldAttribute` is shared by both paths; negative facts for forwarded and no-process-rule misses |
| 6 | A pending flow's packets re-enter the gated self-traffic check per packet | Recorded as accepted and bounded (§5.4.5) with a bounded fact; the short-circuit is a recorded non-goal |
| 7 | The coalescer did not coalesce the forced retry | Fixed: the request-instant rule replaces `forceRefresh`; fact `ARetryThatJoinsALaterEpochDoesNotReadAgain` |
| 8 | The retained structure was unpinned and the 0 B claim wrong | Fixed: one fixed-capacity ring per entry; the budget gate asserts the documented per-new-flow allocation and 0 B for packets 2..32; `BoundedSetupQueue` is explicitly not the payload |
| 9 | The copy source and pool sizing were unpinned | Fixed: `packet.InspectionSpan`; the pool is sized to the maximum frame with capacity ≈ budget / bufferSize; an oversized frame is refused before the copy |
| 10 | The producer sum had no finite solution | Fixed: `ring >= tcpCap + flowCap` (2048), with UDP refusals explicitly accepted, counted and fail-closed |
| 11 | The TTL sweep had no implementation step | Fixed: Step 5's `IdleExpirySweeper` leg + its fact + the tick allocation gate |
| 12 | Teardown classification was missing | Fixed: `OperationCanceledException` → no cooldown, no failure counter; a sealed `Admit` Blocks fail-closed and never attributes inline |
| 13 | The wake's construction site was wrong and the new idle wait was ungated | Fixed: `NdisCaptureGeneration.RegisterArrivalSignals:140,167` + the wake registry; the new exact-window gate is a Step 5 deliverable |
| 14 | Phantom citation `DispatcherAttributionMissStillClaimsWithNoProcess` | Fixed: `DispatcherFallsBackWhenHostAttributionIsUnknown` (`FlowDispatcherExecutorTests.cs:127`) |
| 15 | Refusal counters were unlocated | Fixed: `design.md` §2.4 names each counter and its home (RuntimeCounters vs the module snapshot) |
| Verified-correct notes folded in | the UDP/TCP predicate detail and the `OWNER_MODULE` prohibition; the whole-capture-run exception delta (§5.4.1); the `FlushPendingInjections` pin and the `RetireLanesExcept` consequence; complete-then-execute + `Deferred` unread + pump-thread `Release()`; the one-clock-tick-per-iteration contract; `TryEnqueue`'s unsettled completion; one refusal counter per class | `design.md` §1–§5, `research/implementation-notes.md` §13 |

---

## Landed — measured results, deviations and review dispositions (Step 6, 2026-10-01)

### Landed results

- **AC-1 (off-pump, exact).** `--scenario attribution --quick --attribution-cost-ms 7`, 3 runs:
  `attributionsOnPumpThread` **64 → 0**, `attributionsOnSetupWorker` **0 → 64**, `admittedEntries`
  **0 → 64**, `pumpBlockedMs` p95 **7.52–7.56 ms → 0.019–0.028 ms** (~380×). Raw:
  `benchmarks/results/2026-10-01-attribution-off-pump/`.
- **AC-4 (snapshot, exact + series).** `attribution.ownerBurst`: 16 concurrent flows over one
  scripted table cost **1** scan (`scansPerFlow = 0.0625`). Nine `ProcessOwnerTableCacheTests` facts
  cover coalescing, the later-epoch retry, the forced rescan, TCP-only reuse, UDP never reusing,
  window expiry, scan/snapshot agreement, the recycled-port asymmetry and `window = 0`.
- **tcpChurn calibration (must-not-move).** `allocatedBytesPerConnection` 91,046 / 91,069 / 91,057
  against the reproduce series 91,088 / 91,066 / 91,031 (≤0.06 %). Pooled p99: baseline **6.951 ms**,
  5 % arm **≈52.8 ms** (rank 925 of 934, inside the delayed block), 1 % arm **90.036 ms** — recorded
  as **invalid**, exactly as the amended PRD predicted.
- **Allocation.** The four pump-loop gates and `FlowAttributionPipelineTests.
  PendingAdmitAllocatesOnlyTheDocumentedColdBudget` are green; the gate's cold budget is the entry +
  its 32-slot ring + the first retained slot, and every later packet of an already-pending flow is an
  exactly-zero append.
- **Suite.** Release build zero-warning; full suite **1135 green** (1,117 Core.Tests + 18
  Analyzers.Tests), up from the 1,099 baseline (36 new facts, none relaxed).
- **F1 regression surface.** `NdisPacketActionExecutorBatchingTests` 14/14 and
  `TcpRedirectInjectionBatchingTests` 14/14 green; the delivery is pinned inside
  `FlushPendingInjections` before `FlushPendingPasses`.
- **Check round (2026-10-01, independent verification).** Release build zero-warning; full suite
  **1141 green** (1,123 Core.Tests + 18 Analyzers.Tests) — the landed 1,135 plus the six facts this
  round added (three `CompositePacketArrivalSignalTests`, two `FlowAttributionPipelineTests`, one
  `FlowAttributionPendingIndexTests`), none relaxed. The four counter keys the class audit promoted
  and the TTL sink's packet accounting are covered by the existing facts plus the new index fact.

### Deviations from the plan (each with its reason)

1. **The pipeline is created by `FlowDispatcher`, not by the bundle.** The plan's Step 5 has the
   bundle create the pipeline and pass it in; the pipeline needs the dispatcher's `FlowTable`,
   executor, servers and policy, and the dispatcher creates its own flow table in its constructor.
   The bundle therefore creates the retention pool and the setup executor, passes both to the
   dispatcher, and owns the pipeline's life (it disposes the pipeline immediately after the sweeper
   and the pool in the last block). The ownership order and the single-pool rule are unchanged.
2. **`FlowDispatcher.Attribution` is a property, not a constructor injection of a built pipeline.**
   Same reason; `Attribution` is null exactly when attribution cannot run (no process selector, no
   attributor, no pool/executor).
3. **The `Consumed` admission outcome was dropped.** The design's "an already-decided entry is
   delivered inline by the admitting pump" was replaced by "append to the ring and let the next
   drain deliver it": the decided entry is always in its adapter's queue by construction, so the
   inline path would have to dequeue it a second time and could double-count a claim. Ordering is
   unchanged (the ring is FIFO and the queue is drained in the same iteration's callback).
4. **`Admissions` counts entries, not packets**; `RetainedPacketCount` counts packets. AC-1's
   "per admitted pending entry" is the entry count, and the scenario's `admittedEntries` maps to it.
5. **The platform guard stays in `FindAsync`** but now guards only `ReadProcessIdentity`, not the
   table lookup. Without this the cache is unreachable through the public seam on this host, so the
   `attribution.ownerBurst` row could not exist. Off-Windows the observable result is still `null`.
6. **`Pipeline.NeverTouchesTheFlowGateWhileHoldingItsOwn` (the parked-gate fact) was not written.**
   The lock order is enforced structurally (the index's `Admit`/`Claim` are the only callers and take
   the pending gate first), and the flow table's own gate-hold probe exists, but the parked-gate fact
   the plan names is a recorded gap. **Closed in the check round** by
   `AdmissionHoldsThePipelineGateWhileItWaitsForTheFlowGate`, under the name that states the landed
   nesting: the design's name described the inverse of the order its own §2.3 documents.
7. **The per-gate proof loop runs N = 5 per gate, not 20.** `gate-stability.txt` records the exact N;
   the spec's 20-run target was not met in this session.
8. **The configured `OwnerTableCacheWindowMs` surface was not added.** The window is
   `ProcessOwnerTableCache.DefaultWindowMs` (300) with `0` supported by the type and covered by a
   fact; the JSON/validation surface is a recorded gap.
9. **`WindowsProcessAttributor`'s identity resolution is unchanged** and still returns `null`
   off-Windows, so `FindAsync` remains identity-less on this host (documented in the row's note).

### Review dispositions (all fifteen, discharged where the plan says)

| # | Finding | Disposition | Proof |
|---|---|---|---|
| 1a | The failure arm claimed with a null decision | `DeliverEntry`'s failure branch runs inside the gate (`TryTakeFailed`) and never claims | `AFailingSetupBlocksEveryPendingPacketAndNothingIsStranded` |
| 1b | A false `TryClaimResolved` was unchecked | `Claim`'s `CapacityBlocked` keeps the entry, counts `ClaimFailed`, blocks later packets and surfaces `flow.capacity-block` | `AFailedClaimIsCountedAndDoesNotReAttributePerPacket` |
| 1c | "already decided → deliver now" left the trigger packet undefined | The trigger packet is appended to the ring first and delivered with its predecessors | `OneAttributionPerAdmittedEntry`, `PacketsAreDeliveredInArrivalOrderAfterTheDecision` |
| 2 | `_decidedByAdapter[handle]` mutated while enumerated | The decided set is a **queue dequeued under the gate**; `_decidedCount` falls in the same critical section | `AnEnqueueDuringADrainDoesNotDisturbIt`, `TheDecidedQueueDecrementsItsCountInTheSameCriticalSectionAsTheDequeue` |
| 3 | A detached batch could be stranded | The batch stays entry-owned; a per-batch `finally` blocks the unexecuted remainder (the throwing packet is not re-blocked) and the entry is re-queued for a later claim | `AThrowingExecutorBlocksTheBatchRemainderAndNothingIsDetached` |
| 4 | Cross-adapter delivery was unreachable and unsafe | Per-adapter ownership invariant; a second adapter's key admits its own entry | `ASecondKeyOfOneTupleIsCountedAsAReAdmission` |
| 5 | Admission was not restricted to the eligible shape | `ShouldAttribute` is shared by both paths | `AForwardedMissNeverCreatesAPendingEntry`, `ANoProcessRuleMissNeverCreatesAPendingEntry` |
| 6 | A pending flow re-enters the gated self-traffic check per packet | Recorded as an accepted bounded cost and restated in `hot-path.md` | spec row; `hot-path.md` §"The self-traffic wildcard half" |
| 7 | The coalescer did not coalesce the forced retry | The request-instant rule replaces `forceRefresh` | `ARetryThatJoinsALaterEpochDoesNotReadAgain` (the class lands **eleven** facts, not the nine the plan named) |
| 8 | The retained structure was unpinned and the 0 B claim wrong | One fixed-capacity ring per entry; the budget gate asserts the cold budget and the zero steady append | `PendingAdmitAllocatesOnlyTheDocumentedColdBudget`, `TheRingKeepsThePrefixAndRefusesTheNewcomer` |
| 9 | The copy source and pool sizing were unpinned | `packet.InspectionSpan`; the pool is the budget divided by the buffer size; an oversized frame is refused before the copy | `AFrameLargerThanThePoolBufferIsRefusedBeforeTheCopy` + the bundle's `NativeBufferPool(maximumFrameSize, budget / maximumFrameSize)` |
| 10 | The producer sum had no finite solution | `ring ≥ tcpCap + flowCap` (2,048), UDP refusals accepted and counted | `SetupExecutorTests.DefaultRingCapacityCoversBothFiniteProducerCaps` |
| 11 | The TTL sweep had no implementation step | The `IdleExpirySweeper` leg on the fast tick, allocation-free with a reused scratch list | `RemoveExpired` + the sweeper's attribution leg |
| 12 | Teardown classification was missing | `OperationCanceledException` → no cooldown, no failure counter; a sealed `Admit` blocks fail-closed | `AShutdownCancellationArmsNoCooldown`, `ADisposedPipelineBlocksInsteadOfAttributingInline`, `TheCooldownIsArmedOnlyOnAGenuineFailure` |
| 13 | The wake's construction site was wrong and the new idle wait was ungated | The registry is composed in `NdisCaptureGeneration.RegisterArrivalSignals`, not the loop | `FlowAttributionWakeRegistry` + `CompositePacketArrivalSignal`; the exact-window gate for `CompositePacketArrivalSignal.Wait` landed **in the check round** (`CompositePacketArrivalSignalTests.CompositeArrivalWaitAllocatesNoManagedBytes`) with the class added to `hot-path.md`'s exact-window list and the per-gate totals string |
| 14 | Phantom citation `DispatcherAttributionMissStillClaimsWithNoProcess` | Uses `DispatcherFallsBackWhenHostAttributionIsUnknown`, untouched and green | `FlowDispatcher` class run 28/28 |
| 15 | Refusal counters were unlocated | One `RuntimeCounters` key per class, named in `design.md` §2.4 and in the index | `TheEntryCapRefusesExactlyOncePerRefusedPacket`, `TheGlobalBudgetIsConservedAcrossEverySink`; the shared keys were **unwired until the check round** (C1) and are now incremented at their single sites |

### Recorded gaps (not discharged)

- ~~The parked-gate lock-order fact (deviation 6).~~ **Closed in the check round**:
  `FlowAttributionPipelineTests.AdmissionHoldsThePipelineGateWhileItWaitsForTheFlowGate`.
- ~~The `CompositePacketArrivalSignal.Wait` exact-window allocation gate, and its facts
  (`TheCompositeWaitsOnBothHandlesWithTheSameTimeout`, `ThePipelineSignalsOnlyItsOwnEvent`,
  `TheRegistryDisposesOnlyTheEventItOwns`) — the types are landed and wired, but their facts are not
  written.~~ **Closed in the check round**: `CompositePacketArrivalSignalTests` (three facts plus
  `CompositeArrivalWaitAllocatesNoManagedBytes`, added to `hot-path.md`'s exact-window class list and
  to the per-gate totals string) and
  `FlowAttributionPipelineTests.ThePipelineSignalsOnlyItsOwnAdaptersEvent`.
- The configured `OwnerTableCacheWindowMs` surface (deviation 8) — still open; the type supports `0`
  and a fact covers it, the JSON/validation surface does not exist.
- N = 5 per gate in the stability loop (deviation 7) — the check round also ran N = 5 over the nine
  gate classes and records the same limitation.
- `R8`/F8 double-window integration and the on-Windows experiments W1–W6 — still open.
- **Check round, newly recorded (not gaps the implementer listed):** the Windows handle-lifecycle
  assumptions the per-adapter ownership invariant rests on (see the check-round table below); the
  `SetupExecutor.TryEnqueue` refusal arming a 1 s cooldown through the failure arm (documented
  behaviour on a path the design only counts).

### Check round — independent verification, fixes and residual risks (2026-10-01)

An independent verification round (agent `trellis-check`) re-derived every PRD criterion from code and
artifacts, attacked the six interleavings the implementer listed, and closed the recorded gaps. What it
changed:

| # | Finding | Evidence | Fix |
|---|---|---|---|
| C1 | **Seven `RuntimeCounters` keys the design §2.4 and review disposition 15 promised were declared but never incremented** — `attributionPendingRejected`, `attributionFlowFull`, `attributionSealed`, `attributionCooldownBlocks`, `attributionClaimFailed`, `attributionPendingTtlExpired`, `attributionReAdmission` were reachable only through the pipeline's private index counters, so nothing surfaced them to the heartbeat | `rg` over `src/` found increments for `AttributionSetupRejected`/`AttributionSetupFailed` only | Each key is now incremented at the single site that classifies its class in `FlowAttributionPendingIndex` (the private per-instance counters stay the exact-count source the facts read); `error-handling.md`'s F8 row names them and states the one-key-one-site rule |
| C2 | **The wake registry leaked one OS event per adapter registration and never disposed any of them** — `Register` appended a fresh `EventWaitHandle` per call, so every generation refresh added an event that stayed in `_events` for the process lifetime, and nothing disposed the registry at all | `FlowAttributionWakeRegistry._events` grew per `Register`; no `.Dispose()` call site existed for `DurableCaptureBundle.WakeRegistry` | `Register` now keeps **one event per adapter handle** and reuses it across generations; `DurableCaptureBundle.DisposeCoreAsync` disposes the registry after the pipeline is sealed and every pump has stopped; pinned by `CompositePacketArrivalSignalTests.TheRegistryKeepsOneOwnedEventPerAdapterAndReleasesOnlyThatEvent` (`OwnedEventCount` would have read 2 before the fix) |
| C3 | **A TTL-reclaimed entry's retained packets were released without being counted**, so `BlockedPacketCount` under-reported fail-closed drops (design §2.4's TTL row says "Packets Blocked") | `FlowAttributionPipeline.RemoveExpired` disposed the scratch entries' rings without touching `_blockedPackets` | The reclaimed entries' ring sizes are added to `_blockedPackets` before the release empties them |
| C4 | **The `SetupExecutor.TryEnqueue` refusal marked its entry Failed without waking the pump**, so that arm's delivery waited out the poll delay or the idle bound while every worker arm signalled | `Launch` returned after `MarkFailed` with no `SignalWake(entry)` | `Launch` signals the wake on the refusal path too, making "a decided or failed entry always wakes its own adapter" uniform (latency only, never correctness) |
| C5 | **The scenario never reported `refusedPackets`**, which `design.md` §8 and `implement.md` Step 2 both list | the row carried no such field | `AttributionOffPumpScenario` emits `refusedPackets = pipeline?.RefusedPacketCount ?? 0`; verified by a post-fix diagnostic run (`refusedPackets = 0`), with the recorded series left untouched |
| C6 | **The exact-window totals string in `hot-path.md` still listed five gate classes**, so the spec's own proof loop could not run the gates this task grew, and the same file's exact-window list named the composite wait as covered while no gate existed | `hot-path.md` `totals=` vs the landed classes | The string now lists all nine gate classes with refreshed counts; the composite gate is named by class and fact in the exact-window list; the check-round loop in `gate-stability.txt` runs the refreshed string |
| C7 | **The artifact README and this file both said the coalescer has "nine facts"** while `ProcessOwnerTableCacheTests` lands eleven | `class-totals.txt` says 11 | Corrected to eleven (the nine planned plus the unavailable-reader and out-of-range-window edges) |
| C8 | **`ARetryThatJoinsALaterEpochDoesNotReadAgain` was flaky**: it started the first lookup with `Task.Run` and slept 30 ms hoping the 150 ms read was in flight, so on a loaded host the retry reached the refresh gate first, scanned, and the fact failed (`Expected: 1, Actual: 2`) once in a full-suite run | reproduced once in a full-suite run; the fact's own comment states the assumption it cannot guarantee | The scripted reader's `OnRead` hook now parks the first read until the fact releases it, and the retry's request instant is captured before that release, so "the retry asks while the epoch is in flight" is a property of the test rather than a scheduling hope. The assertion is unchanged (`ReadCount == 1` and equal answers); its discrimination was re-proven by removing the in-lock epoch recheck (→ `Expected: 1, Actual: 2`), and the class then ran 10/10 isolated processes green |

### Commit-gate round — `dotnet format --severity info` diagnostics (2026-10-01)

The operator ran the pre-commit format gate on the verified tree and reported 13 diagnostics, all in
this task's surface (one error). All 13 are fixed; no test was weakened and nothing was suppressed:

| # | Rule / site | Fix |
|---|---|---|
| F1 | `IMPORTS` error, `FlowAttributionPipeline.cs:1` | `using System.Runtime.ExceptionServices` before `using System.Runtime.InteropServices` (the repo's System-first alphabetical order) |
| F2 | `IDE0057`, `FlowAttributionPipeline.cs:427` | `retained.Frame.Memory.Slice(0, retained.Length)` → `retained.Frame.Memory[..retained.Length]` |
| F3 | `MA0185`, `FlowAttributionPipeline.cs:304` | the only hole is an enum (culture invariant) → plain `$"Unhandled attribution claim '{…}'."` |
| F4 | `MA0185`, `FlowAttributionPipelineTests.cs:386` | the only hole is a `ushort` (culture invariant) → plain `$"attribution-admitter-{port}"` |
| F5 | `MA0076`, `FlowAttributionPipelineTests.cs:305` | the hole is a `long` (culture sensitive) → `string.Create(CultureInfo.InvariantCulture, $"… allocated {steady} B …")` |
| F6 | `MA0193`, `AttributionOffPumpScenario.cs:189` | `Math.Round((double)reads / burst, 6, MidpointRounding.ToEven)` with the rounding stated: banker's rounding, the sibling scenarios' convention; the latency percentiles are nearest-rank selections and round nothing, so the recorded `0.0625` is unchanged |
| F7 | `RCS1085`, `NdisPacketArrivalSignal.cs:57` | `Handle` is a plain constructor-assigned handle (no `[ThreadStatic]`, no interlocked state) → converted to the auto-property `public WaitHandle Handle { get; }`; `Wait`/`Dispose` read it |
| F8 | `RCS1085`, `ProcessOwnerTableCache.cs:48` | `WindowMs` converted to the auto-property, assigned after the range check; `IsFresh` reads it |
| F9 | `CA2012`, `FlowAttributionPipelineTests.cs:156` | the nested re-entrant dispatch no longer blocks on the `ValueTask`: the fact asserts `IsCompletedSuccessfully` (stronger than the silent block — a genuinely asynchronous admission now fails loudly) and consumes no result |
| F10 | `MA0042`, `FlowAttributionPipelineTests.cs:393,396,401` | the three direct waits became awaited ones through a `CompletesWithinAsync(Task, TimeSpan)` helper built on `task.WaitAsync(timeout, TimeProvider.System)` (a `TimeoutException` maps to `false`, so the boolean negative checks and their messages survive). The parked-gate fact is unchanged in what it proves |
| F11 | `MA0042`, `ProcessOwnerTableCacheTests.cs:53` | the fact is now single-threaded and blocking-free: the retry's request instant is captured *before* the first lookup publishes its epoch, so the in-lock join branch is exercised by the instant rule alone — the same code path, no scheduler dependence (this also supersedes C8's thread-based rewrite; its discrimination is re-proven below) |

The parked `GateHoldProbe`'s `release.Task.Wait` is **not** suppressed: it sits in a `void`-returning
lambda, which MA0042 deliberately does not report (it cannot become `async` without becoming
`async void`), and the park is on the dedicated holder thread by construction.

Re-verified after the fixes: Release build zero-warning; the three affected gate classes 5/5 each in
their own processes (`ProcessOwnerTableCacheTests` 11/11, `FlowAttributionPipelineTests` 19/19,
`NdisCapturePumpIdleWaitTests` 5/5 — the last because `NdisPacketArrivalSignal` changed); the rewritten
coalescing fact still discriminates (removing the in-lock epoch recheck → `Expected: 1, Actual: 2`),
restored and green; and **three further consecutive fully green full-suite runs (1,124/1,124)**,
bringing the final tree to six consecutive green suite runs.

### Inspectcode round — `jb inspectcode` findings (2026-10-01)

The operator's JetBrains inspector reported 45 issues in this task's surface. All 45 are resolved:
genuinely dead members were **deleted**, genuinely internal-only members were **narrowed**, and exactly
**three** narrow ReSharper suppressions were written (two `AccessToDisposedClosure`, one
`ConvertIfStatementToSwitchStatement`; all three named with their reasons in the table below). No
`MemberCanBePrivate.Global` / `UnusedAutoPropertyAccessor.Global` finding was suppressed: every one had
a real consumer or was dead, so each was narrowed or deleted instead. `dotnet format` was already
exit 0 with empty output before this round and was not re-run here.

| Group | Sites | Disposition |
|---|---|---|
| Dead members (no caller anywhere, tests and benchmarks included) | `FlowAttributionPipeline`: `PendingBytes`, `DeliveredPacketCount` (+ its field, increment and the whole `AttributionDiagnostics` snapshot, whose only consumer was the deleted property); `FlowAttributionPendingIndex`: `PendingFlowAttribution.Failure` (+ `MarkFailed`'s `failure` parameter, all seven call sites updated — a genuine failure is still counted and fails closed, the exception itself was never read); `CapturePipelineFakes`: `GatedAttributor.Name`'s unused init setter (collapsed to a private const, the null-miss branch went with it), `GatedAttributor.CostMs` (+ its delay branch), `GatedAttributor.ThreadIds`, `ScriptedOwnerTableReader.OnRead` (+ invocation), `.ReadsOf`, the unused `Script(kind, table)` overload, `RecordingExecutor.Frames` (+ the per-action frame copy it allocated) and `RecordingExecutor.ProxyCount` (the dispatcher tests use their own `FakeExecutor`); `IProcessOwnerTableReader`: `OwnerTable.RowCount` | deleted |
| Internal-only members narrowed | `OwnerTable.Family`, `FindUdpOwner`, `FindTcpOwner` (private — `Lookup` is the only caller, the cache and the facts reach them through it) and `ProcessOwnerTableCache.WindowMs` (private) | narrowed |
| Set-once state | `NdisCaptureGeneration.WakeRegistry` → `{ get; init; }` (only the `Program.cs` object initializer sets it); `ScriptedOwnerTableReader.ReadDelay` → `init`; `TestClock.Now` → `private set` (`Advance` is the only writer); harness `Setup` → private; `PendingFlowAttribution.Count` → auto-property with a private setter (`ConvertToAutoPropertyWithPrivateSetter`) | narrowed/made init-only |
| Style/shape fixes | `MergeIntoPattern` (`entry is { ClaimFailed: true, Queued: false }`); `DuplicatedSequentialIfBodies` (one refusal body for ring-full/oversized/budget); `ConvertIfStatementToSwitchStatement` in `Execute` (now a switch over every `FlowAction`, the proxy arm resolving its server through `ResolveServer`, which also discharges the `MergeIntoPattern` finding on the old `&&` chain); `SwitchStatementHandlesSomeKnownEnumValuesWithDefault` in `FlowAttributionPipeline.Admit` and `FlowDispatcher.TryDeferAttributionAsync` (every blocked outcome enumerated in one `or` pattern; the dispatcher keeps an unreachable fail-closed tail so a future enum member cannot leave a packet unsettled) | fixed |
| Benchmark instrument | `RedundantArgumentDefaultValue` (the pool's capacity is already 256); `DisposeOnUsingVariable` — the pool is now `using var`, declared before the setup executor so it is released after it and after the explicitly disposed pipeline, which is the same ownership order the F5 round settled on | fixed |
| Test hygiene | three redundant `using WinForward.Core;` (the test namespace is nested under it); `Assert.All(answers, Assert.Null)`; the unread `answers` array in the window-zero fact removed (its only purpose was the write that the concurrent lookups perform); `new ProcessOwnerTableCache(UnavailableOwnerTableReader.Instance)` (the window default is redundant) | fixed |
| `AccessToDisposedClosure` ×2 | `AnEnqueueDuringADrainDoesNotDisturbIt`: the hook runs synchronously inside the `DeliverDecided` call on the test's own thread, so it cannot outlive the harness — suppressed with that reason. `AdmissionHoldsThePipelineGateWhileItWaitsForTheFlowGate`: **restructured** so every exit path (including a failed assertion) releases the gate and joins the holder and both admitter threads inside the `finally`, and the two outcome waits are bounded, after which the suppression is written with that ordering as its reason | one restructured, two narrow suppressions |
| `ConvertIfStatementToSwitchStatement` on `LogRefusal` | the two arms are independently throttled and carry their own events/levels; a switch would have to enumerate all seven outcomes for two logging arms — suppressed with that reason | suppressed |

One flake this round surfaced and fixed (not an inspectcode finding): `OneAttributionPerAdmittedEntry`
sampled the worker-side counts (`Attributor.Calls`, `AttributionsOnSetupWorker`) immediately after the
dispatches, so a loaded host could assert before the setup worker had started; it failed in one
full-suite run and is now awaited (`WaitUntilAsync(() => Attributor.Calls == 1)`), with the assertions
unchanged. Three consecutive full-suite runs after the fix were green (1,124/1,124 each; two more green
runs preceded it), and the four gate classes
whose code changed re-ran 5/5 each in their own processes (`FlowAttributionPipelineTests` 19/19,
`FlowAttributionPendingIndexTests` 10/10, `ProcessOwnerTableCacheTests` 11/11,
`NdisCapturePumpIdleWaitTests` 5/5). A compiler run with `EnforceCodeStyleInBuild=true` reports no
`IDE0005` in the task surface.

The check round's suite-level result, recorded honestly: seven full-suite runs on the frozen tree gave
**three consecutive fully green runs (1,124/1,124 each)** and four earlier runs with exactly one failure
each — the recorded host lump in `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` (its
`Actual` was lost to a `tail` pipe, so the spec's signature predicate cannot be applied), the fixed C8
flake, `HotPathAllocationGateTests.UdpSetupEnqueuePathAllocatesNoManagedBytes` with `Actual: 280` (not
one of the recorded signature sizes, so an **incomplete diagnosis** rather than an accepted host event;
that gate is off every path F8 touched, has no probe-batch preflight and awaits inside its window
without a completion/thread guard — a pre-existing shape weakness, left alone as out of scope), and an
unrelated `UdpAssociationCapabilityTests` teardown `TaskCanceledException`. Every F8 class was green in
every run and in its own per-gate loop; both allocation-gate classes re-ran 10/10 green in isolation,
which is the recorded "suite-process-conditional, not window-conditional" property. The operator should
still treat **"full suite green" as a per-run reading on this host**: the recorded suite-level rate in
`hot-path.md`'s residual-lump section is unchanged, and no run of this round claims stability.

What the check round verified without changing anything, and the residual risks it hands the operator:

| # | Item | Verdict |
|---|---|---|
| A1 | **Per-adapter ownership under two pumps with one transport tuple.** The key includes `OriginAdapterSlot` + `OriginAdapterGeneration`, so two adapters admit two entries and each is delivered only by its own handle's drain (new fact `AnEntryIsDrainedOnlyByItsOwnAdaptersPump`); the second claim returns the existing decision through `TransportTuple` and is counted. **Residual (Windows-side, unverifiable here):** the invariant rests on a 1:1 map from an entry's `Metadata.AdapterHandle` to the pump that drains it. Two NDIS adapters interning to one slot while keeping distinct handles, or a driver handle recycled to a different adapter inside one bundle lifetime, would let a second pump admit/drain one entry's key. The observable failure would be a frame lane'd for a handle the draining pump does not flush — dropped by `RetireLanesExcept` with a warn, not executed cross-adapter — plus the concurrent-append interleaving of A2. Record as a Windows experiment (enumerate two adapters reporting one interface GUID; watch for `A pass lane retired … still held N frame(s)`). |
| A2 | **The claim-last section vs a concurrently arriving packet.** Reachable only if one key is dispatched from two threads. In production, admission and delivery for a key both run on the one pump thread that owns its adapter, so the "append between the last empty take and the claim" interleaving cannot occur; the landed fact drives the append re-entrantly (before the last take, so it is delivered). **No change made:** closing it would mean adding a re-check to the BLOCKER-fixed critical section — a design change, not a local fix. The recorded residual is A1's precondition. |
| A3 | **`TryTakeFailed` vs a worker marking Failed after the drain started.** An entry is enqueued by exactly one `MarkDecided`/`MarkFailed` call and a Decided entry is never re-marked, so the failure arm cannot overtake a decided delivery; a Failed entry that is never drained is reclaimed by the TTL or the dispose drain, so nothing is stranded. The `Launch`-refusal arm's missing wake was the one real defect found (C4). |
| A4 | **Budget/pool conservation beyond the five asserted sinks.** Charged == credited + live holds for the ring-full refusal (charges nothing), the claim-failed sink (entry kept, bytes live) and the duplicate-claim sink (credited whole); pinned by the new `TheGlobalBudgetIsConservedUnderTheRefusalAndDuplicateClaimSinks`. |
| A5 | **The composite's `Dispose` with a parked waiter.** `MultiAdapterCaptureLoop.DisposeCoreAsync` awaits every pump's dispose **before** disposing the signal list, and the registry's events are disposed after the pipeline is sealed and the capture runner has stopped the pumps, so no waiter is parked; even if one were, `Wait` converts the resulting `ObjectDisposedException` into "not signalled" and the pump re-checks its stop flag. The disposed-event path is now exercised by `CompositePacketArrivalSignalTests`. Windows-side handle-close-under-wait semantics remain an ABI assumption, not a verified fact. |
| A6 | **Failure-path accounting.** A shutdown `OperationCanceledException` arms no cooldown and increments no failure counter; a sealed pipeline blocks fail-closed and never attributes inline; `AttributionSetupFailed` has exactly one site (the worker's classification catch) and `AttributionSetupRejected` its own. `TryTakeFailed`'s cooldown also fires for a `TryEnqueue`-refused entry — counted as a setup rejection, fail-closed, and recorded here as documented behaviour rather than a defect. |
