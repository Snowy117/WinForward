# Allocation-Gate Host Lumps: the residual, its signature, and the per-gate proof

> The one failure mode of an otherwise correct exact gate — a host lump inside a measured window —
> and the procedures that bound it. Part of the [hot-path family](./hot-path.md).

**Read it when** an exact 0-byte gate failed with a nonzero delta you cannot explain, or you are
about to change a proof procedure, a victim gate's window, or the accepted-host-event signature.
The window shape itself is [allocation-gates.md](./allocation-gates.md), "Allocation-gate stability".

## The residual exact-gate lump: multiplicity, the attribution limit, and the per-gate proof

Diagnosed 2026-09-30 (task 09-30-exact-gate-residual-lumps) with the tiering host contract already in
place. **No gate threshold, window shape, assertion or warm-up changed** — the work here is
attribution and proof procedure only.

- **Trigger**: the full suite still failed about one run in ten with a one-shot lump inside one exact
  allocation window — `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes`
  168 B / 5,216 B and `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` 7,384 B (plus
  7,448 B in the hunt) — on paths whose only work is polling. The predecessor's follow-up, "a
  per-window control that can tell a host lump from a driven allocation", is answered here (priced and
  rejected), and the proof procedure is replaced by per-gate process runs.
- **The residual is not a property of the window bodies** (measured 2026-09-30). A temporary
  in-assembly reproducer (deleted after the measurement; `NdisCapturePump.RunIterationForTests` is
  `internal`, so only an `InternalsVisibleTo` assembly can drive the window bodies) re-ran the exact
  window bodies with **per-iteration** deltas, no excluded iterations, recording iteration index,
  bytes, thread id and Gen0/1/2 — one process per arm, never concurrently with a suite run. The
  census reduced to its extremes:

  | Window body | Thread / co-resident load | Measured | Window lumps |
  |---|---|---:|---:|
  | pump idle iteration | xunit thread | 17,053,000 iterations / 17,053 windows | 0 |
  | pump idle iteration | + 8 blocking full-GC loaders | 1,026,000 / 1,026 (4,476 Gen0/1/2) | 0 |
  | flow-table sweep | xunit thread / dedicated `Thread` / pool thread | 53,107 windows | 0 |

  Each no-load arm shows exactly **one** nonzero delta at iteration 0 of the census loop (152–272 B);
  the counter-read-only control reproduces it identically while driving no product code, so it is the
  census loop's own first-transition overhead, not the residual. A bare console host
  (`TieredCompilation=false`, public counter read only) measured **0 lumps in 148,525,260 counter-read
  iterations**. Combined: **0 lumps in 196,774 pump windows and 53,107 sweep windows; 0 in 11,509,218
  control windows.**
- **Multiplicity.** In isolation the residual's measured count is **0 lumps per process** (27 arm
  processes, >200,000 window-body windows; Wilson 95 % upper bound 12.5 % per process) and **0 per
  window** (0 in 249,881 window-body windows; upper bound 1.6 × 10⁻⁵ per window). In the suite's own
  captures every lump failure names **one gate and one size**, and no run shows two failing gates.
  "Once per process, random iteration" is the **tiering** control's signature and stays in that
  family.
- **The pool arm needs `ThreadPool.UnsafeQueueUserWorkItem`, not
  `Task.Run(...).GetAwaiter().GetResult()`.** The xunit test thread is itself a pool thread, so the
  pool may execute the task inline on it — the first run of that arm silently measured the test
  thread again. Check the thread id in any future arm.
- **The event is suite-process-conditional, not window-conditional.** The recorded ~10 % per-run lump
  rate implies a per-window rate of order 10⁻², at which the isolated census would have fired
  hundreds to thousands of times; zero were observed. Whatever triggers the residual needs the
  co-resident suite process (≈980 sibling tests, their threads, their JIT/loader activity), not the
  window body — which is why per-gate isolation changes *attribution, not incidence*, and cannot
  place a window after the event.
- **No allocator can be named with the available instrument (recorded, not asserted).**
  `dotnet-trace collect --profile gc-verbose` sampling is **budget-based** (a thread's allocation
  context is sampled roughly once per ~100 KB that thread allocates), so a sparse ≤8 KB one-shot on a
  thread that allocates nothing else is never sampled: the positive control (an 8 KB class allocated
  every iteration) produced 1,559 samples naming the type, while the same class allocated **once**
  produced zero samples although the per-thread counter read its 8,280 B lump. A null capture
  therefore means "the instrument could not see it", never "nothing allocated". `dotnet-gcdump` is
  weaker still: it forces a collection, so a transient lump is gone before the dump.
- **The per-window control is priced and rejected.** A control window shaped like a gate window
  (same counter reads, no product call) cannot separate a random host lump from a driven allocation:
  the lump lands in exactly one of the two windows, so their difference is nonzero whichever cause it
  had. A **co-resident** control is worse — the residual fires at most once per suite process, so the
  control would absorb the event and mask the gates it is meant to calibrate. Every control therefore
  runs in its own process, never with a suite run.
- **No tolerant shape was adopted.** The gates keep `Assert.Equal(0, allocated)` and their call-count
  backstop. min-of-K and "at most one of K windows nonzero" tolerate exactly the class under
  diagnosis (a lump in one window) and are refuted by the injected-allocation check; min-of-K
  additionally loses three to four orders of magnitude of power for a 1-in-5,000-iteration regression
  (0.02 % against 18 %). Re-proven on this tree: one `new byte[64]` per measured iteration → pump gate
  `Actual: 88000` (1,000 × 88 B); one inside the single sweep window → sweep gate `Actual: 88`; both
  restored → green. A gate shape that cannot fail that check is not a gate. (The injection must be
  kept alive — see [allocation-gates.md](./allocation-gates.md).)
- **Disposition: no product fix, no gate change, per-gate proof.** The counter-read-only control *of
  the residual itself* (tiering off, suite process) could not be run — the event never appears in an
  isolated process, and a co-resident control masks the gates — so **the product is not excluded by a
  control of the residual**; that limitation is recorded rather than smoothed over. What the evidence
  supports is the operational proof: run each exact gate in its own process, record the measured
  per-gate residual rate, and keep the **suite-level rate as an accepted host property** (recorded
  ≈10 % per run; pooled over the recorded runs, 4 in 76 = 5.3 %, Wilson 95 % [2.1 %, 12.8 %]).
- **Sensitivity statement.** The disposition does not lower regression sensitivity — same exact
  window, same N — but it changes **what is proven**: per-gate process runs sample a process running
  one gate instead of a co-resident suite, so the suite-conditional trigger above is not exercised by
  the proof. The regression floor of an N-run proof over a 1,000-iteration window is
  `1 − (1 − 1,000/K)^N`: N = 20 gives 98.8 % at K = 5,000 and 33.2 % at K = 50,000, so a regression
  rarer than ~1 allocation per 50,000 iterations is no longer reliably caught. That floor is intrinsic
  to the run count, not to per-gate isolation.
- **An accepted host property, not a stability guarantee.** The record claims **no rate threshold**.
  A per-gate failure counts as the accepted host event only when the checkable signature holds: the
  gate is a recorded victim, the delta equals one of the recorded signature sizes, the run passed the
  gate's own `Assert.True(stabilized)` preflight, and the injected-allocation check still fails when
  re-run. Any other failure is an incomplete diagnosis, not an accepted event.
- **Do not re-test** (this task's negative results): thread context (xunit thread / dedicated
  `Thread` / a genuinely other pool thread), allocation pressure, blocking full collections, thread
  churn, a live allocation context on the measured thread, the counter-read-only control in the suite
  host and in a bare console host, and profiler-based attribution of a ≤8 KB one-shot. The tiering,
  `gcConcurrent`, OSR-only, forced-collection, diagnostics-server and warm-up families stay excluded
  by [allocation-gates.md](./allocation-gates.md).

## The per-gate proof procedure

The suite-level loop is **not** the criterion. Run each exact gate in its own process, N runs per
gate, and record every run's padded summary, its totals assertion, the git hash, the tree fingerprint
and the exit status; a failure is accepted only under the signature predicate above.

Qualify the filter with the test project's namespace — the gates live in four projects since the
2026-10-01 split, and a bare class-name substring is not unique: `~SweepAllocationGateTests` also
matches `UdpAdaptiveSweepAllocationGateTests`, so the loop stops on a false `VACUOUS MATCH`.

The expected total is **derived from the class source**, never remembered: a hard-coded per-class map
rots silently (the shipped one was stale for two of the ten classes and aborted the loop on its first
run), while the class file is the thing the filter actually selects.

```bash
log=/tmp/wf-lumps-proof.txt; : > "$log"; rev=$(git rev-parse --short HEAD); tree=$(git write-tree)
summary='Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+'
signature='^(168|5216|7384|7448|520|1552|3512|5256)$'
# gate-fully-qualified-class:the-file-that-owns-it
gates='WinForward.Performance.Tests.HotPathAllocationGateTests:tests/WinForward.Performance.Tests/HotPathAllocationGateTests.cs
WinForward.NdisApi.Tests.CapturePumpReadCallTests:tests/WinForward.NdisApi.Tests/CapturePumpReadCallTests.cs
WinForward.Performance.Tests.SweepAllocationGateTests:tests/WinForward.Performance.Tests/SweepAllocationGateTests.cs
WinForward.NdisApi.Tests.NdisCapturePumpTests:tests/WinForward.NdisApi.Tests/NdisCapturePumpTests.cs
WinForward.NdisApi.Tests.NdisCapturePumpIdleWaitTests:tests/WinForward.NdisApi.Tests/NdisCapturePumpIdleWaitTests.cs
WinForward.Runtime.Flow.Tests.FlowAttributionPipelineTests:tests/WinForward.Runtime.Flow.Tests/FlowAttributionPipelineTests.cs
WinForward.Runtime.Flow.Tests.FlowAttributionPendingIndexTests:tests/WinForward.Runtime.Flow.Tests/FlowAttributionPendingIndexTests.cs
WinForward.Windows.Tests.ProcessOwnerTableCacheTests:tests/WinForward.Windows.Tests/ProcessOwnerTableCacheTests.cs
WinForward.NdisApi.Tests.CompositePacketArrivalSignalTests:tests/WinForward.NdisApi.Tests/CompositePacketArrivalSignalTests.cs
WinForward.Performance.Tests.UdpAdaptiveSweepAllocationGateTests:tests/WinForward.Performance.Tests/UdpAdaptiveSweepAllocationGateTests.cs'
for entry in $gates; do
  gate=${entry%%:*}; file=${entry#*:}
  expected=$(rg -o '\[(Fact|InlineData)' "$file" | wc -l)
  for i in $(seq 1 20); do
    out=$(dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~$gate" 2>&1); rc=$?
    line=$(echo "$out" | rg -o "$summary" | tail -1)
    actual=$(echo "$out" | rg -o 'Actual: *[0-9]+' | tail -1 | rg -o '[0-9]+')
    echo "gate $gate run $i $rev $tree rc=$rc total=$expected $line actual=${actual:-none}" >> "$log"
    echo "$out" | rg -q "Total: *$expected" || { echo "VACUOUS MATCH: $gate run $i (expected $expected)"; break; }
    if [ "$rc" -ne 0 ]; then
      if echo "${actual:-x}" | rg -q "$signature"; then echo "accepted host hit: $gate run $i ($actual B)"; else echo "UNEXPLAINED FAILURE: $gate run $i"; break; fi
    fi
  done
done
```

The separate stability proof — the one that legitimises a *product* fix — runs the filter class and
then the whole suite from the repository root with the tree frozen: **≥100 consecutive green filter
runs and ≥40 consecutive green full-suite runs**, each recording its padded summary line, the git
revision and the process exit status; never `--no-build` (a stale binary greens vacuously) and never a
discarded stream; a failure stops the loop and returns to diagnosis. Against the recorded 7.9 %
(3/38) failure rate, 100 green filter runs and 40 green suite runs are 99.97 % and 96.28 %; 20 suite
runs would be only 80.7 % and are not accepted. On a host carrying the residual, the full-suite half
is not achievable (≈10 % per run) — that is why the two procedures above are separate, and why the
residual needs the signature predicate rather than a green streak.

## Addendum (2026-10-06 flaky sweep)

A loaded soak (two or three concurrent `dotnet test WinForward.slnx -c Release` streams — the
condition that reproduces the event) re-observed the residual on the same shape-compliant, fully
synchronous gates. **No gate assertion or window changed.**

- **The victim list is longer than the recorded signatures.** Measured deltas on the 2026-10-06 tree:
  `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` **520 B**,
  `SweepAllocationGateTests.FlowTableSweepWithHoldPredicateAllocatesNoManagedBytes` **1,552 B** (the
  fact the 10-06 appsettings record saw at 64 B), and
  `SweepAllocationGateTests.TcpRedirectTableSweepAllocatesNoManagedBytes` **3,512 B** and **5,256 B**.
  Only 5,216 of the whole set is a previously recorded signature, so the size predicate above is
  **incomplete**: a nonzero delta in a shape-compliant gate cannot be classified by size alone. Every
  victim still ran its own probe preflight successfully, kept its thread id, and had no `await` in its
  window (`void` window bodies), so none of these is a window-shape defect.
- **A peer thread's GC does not move the per-thread counter — measured, not argued.** Two console-host
  experiments (`TieredCompilation=false`, public counter API only): (a) 20,000,000 empty windows with
  a peer allocator driving 87 Gen0 collections → **0** nonzero deltas; (b) 200,000 windows each
  preceded by a 4,096-object batch (the `SeedRedirects` shape, so the measuring thread holds a freshly
  refilled allocation context) with 9,378 Gen0 collections → **0** nonzero deltas. The residual is
  therefore **not** "a GC refreshes the thread's allocation context"; that family joins the ruled-out
  list above.
- **A co-resident census probe found nothing in ~30 loaded suite runs.** A temporary 500,000-window
  probe (`GC.GetAllocatedBytesForCurrentThread()` bracketing an empty window, classifying every hit
  against `GC.GetTotalAllocatedBytes(precise: false)` and the GC counts) ran inside the
  `WinForward.Performance.Tests` host, alongside the gates, while the soak ran; it recorded **zero**
  hits and was deleted. That is consistent with the "at most once per suite process" property — the
  probe's 500,000 windows did not absorb an event the ~150 gate windows carried in ~7 % of runs.
- **Rate under a loaded soak:** 4 residual failures in ~60 full-suite runs (≈7 %), the same order as
  the recorded single-stream 10 % (Wilson intervals overlap). Disposition unchanged: **accepted host
  property, per-gate proof, no threshold relaxation**, and the gates' exact zero plus their call-count
  backstops stay in place.

## Outcomes a reader must be able to predict

| Condition | Required result |
|---|---|
| Exact gate fails in the suite with a nonzero delta | classify: recorded victim gate + the gate's clean `stabilized` preflight + the injected check still failing → accepted host event; anything else stops the work |
| Exact gate fails in its own process (per-gate proof) | same classification; the **measured** per-gate rate is recorded, never a threshold |
| A proposed gate shape tolerates one nonzero window | forbidden — one injected allocation inside one window must still fail it |
| A reproducer runs concurrently with a suite run | forbidden — it absorbs the once-per-suite-process event and masks the gates |
| A profiler null capture over a window body | not evidence of "nothing allocated": budget-based sampling cannot see a ≤8 KB one-shot on a non-allocating thread |
| A gate failure reports bytes with no per-iteration location | acceptable only for the recorded signature sizes; prefer per-iteration deltas before changing anything |

## Wrong vs Correct

- **Wrong**: "prove" stability by re-running the suite until it is green — each run carries the
  suite-conditional host event, the green streak is luck, and a failure is re-run away. **Wrong**:
  tolerate one nonzero window (min-of-K, "at most one nonzero") — that is exactly the class under
  diagnosis, and the injected-allocation check fails this shape by construction. **Wrong**:
  co-resident control windows — they absorb the once-per-suite-process event.
- **Correct**: no gate change; classify by the checkable signature and measure the rate — a per-gate
  process run with the recorded padded summary, totals, hash and fingerprint, plus the
  injected-allocation check re-run on the frozen tree. The gates keep the exact zero; only the proof
  procedure and what it proves changed.
