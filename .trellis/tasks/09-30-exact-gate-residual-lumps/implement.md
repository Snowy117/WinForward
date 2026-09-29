# Implementation plan — residual lumps and the health-signal race

Attribute first, then choose a disposition, then fix the race, then prove. The gates stay byte-exact
throughout; only the *proof procedure* may change, and only under disposition (b)/(c).

## Step 0 — Preflight and baseline

- [ ] Re-read the predecessor's record (`archive/2026-09/09-30-test-flake-and-hang-stabilization/`) and its
      captures `/tmp/wf-proof-suite-failure-{6,10,13}.txt`; reuse its recorded suite rate **with its CI**
      (its own power table: 20 runs = 80.7 %), do not re-establish a weaker baseline.
- [ ] Confirm the gate bodies and their windows (`CapturePumpReadCallTests.cs:89,95-116`,
      `SweepAllocationGateTests.cs:35-47,49-57`, `NdisCapturePumpTests.cs:296-334`) and write down each
      measured region.
- [ ] Note the host's tooling gap: the SDK is present, no `dotnet-trace`/`gcdump`/`counters` — install one
      for Step 2, or record that the attribution step is unavailable and R1 must be re-scoped.

## Step 1 — Measure multiplicity (≤30 min)

- [ ] Scratch reproducer: the **window bodies** (`NdisCapturePump.RunIterationForTests`, the sweep's
      `RemoveExpired`) can only be driven from an IVT'd assembly (`WinForward.Core.Tests` or
      `WinForward.Benchmarks`), so the reproducer is a **temporary file inside one of those**, removed before
      the frozen proof; the `/tmp` host carries only the public counter-read control. Loop with
      per-iteration deltas, no excluded iterations, logging iteration index, bytes, thread id and Gen0/1/2.
- [ ] Measure **how many lumps a single process shows** and whether they land in the window at all. This is
      the number every later decision needs, and "once per process" is unverified for the residual.
- [ ] Run the arms **one at a time**; never concurrently with a suite run (a co-resident reproducer absorbs
      the event it hunts and masks the real gates).

## Step 2 — Attribute (time-boxed: half a day)

- [ ] Thread-context arms, same window body: (A) the xunit thread, (B) a dedicated `Thread`, (C) a pool
      thread **other than the test's** (`Task.Run`). The observable is a per-thread counter event, so the thread is the variable.
- [ ] Tool-based attribution: state **what the tool guarantees to observe before running it** (allocation
      sampling is budget-based — order 100 KB — so a sparse ~7 KB one-shot lump need not be sampled, and a
      post-hoc heap dump may miss a transient object). A null capture is recorded as "the instrument could
      not see it" (→ R1 re-scope), never as "nothing allocated".
- [ ] The bare-console arm lives in the IVT'd benchmark assembly (`WinForward.Benchmarks`, `NdisApiAbi.cs:7`)
      because `RunIterationForTests` is internal; run the `GC.CollectionCount` correlation only there (in the
      suite, parallel classes move the counters constantly).
- [ ] Record the diagnosis before any change: what allocates, or the family-level cause with the product
      explicitly excluded and the time box honoured.

## Step 3 — Execute the disposition the evidence supports

- [ ] (a) Product owner → fix under product-change discipline (spec + fail-before test), gates unchanged.
- [ ] (b) Host event, no product owner → **no gate change**: switch the proof to per-gate processes
      (`dotnet test --filter` per exact gate) with the **operational criterion** — N runs per gate, each
      recorded, reporting the measured per-gate residual rate; a failure counts as the accepted host event
      only when its nonzero pattern matches the recorded signature. Per-gate isolation changes attribution,
      not incidence (a lump landed after the probe reported `stabilized`), so N is chosen for the rate's
      precision, not for a post-event window. Record the suite residual rate with its CI in `hot-path.md`.
- [ ] (c) Unattributable → (b) plus the sensitivity statement (which allocation rates the per-process gate
      no longer catches).
- [ ] If a tolerant shape is nevertheless proposed, it must fail the honest injected-allocation check — one
      allocation inside **one** window — and min-of-K / "at most one of K nonzero" are already refuted by
      that test; do not re-derive them.
- [ ] Delete the scratch reproducer when the cause is fixed or recorded; state its fate in the record.

## Step 4 — Fix the health-signal race (product change)

- [ ] Capture the trigger-time streak and window snapshot under the monitor's gate and hand them to the
      trigger, or move `_demandGate.Signal()` after the record build (`LayeredCaptureRunner.cs:245` vs
      `:251,248-259`); the race is with `InterceptionHealthMonitor.NoteRefreshCompleted()` (`:156-164`).
- [ ] Add the bounded wait for the post-state assertions (`LayeredCaptureRunnerHealthSignalTests.cs:53-54`),
      which are racy in the opposite direction. Do **not** add an isolated 200× loop (a thread-pool
      starvation race cannot reproduce that way).
- [ ] Record the invariant in `.trellis/spec/backend/windows-ndisapi.md` (`:492,:502`) and the honest
      limitation: no deterministic fail-before test without a seam; the pre-fix capture plus the ordering
      argument is the evidence. A test-only seam is allowed if it makes the interleaving deterministic.
- [ ] Commit as `fix(runtime): …` — this is a product change, not a test-tree change.

## Step 5 — Gates, then the proof on a frozen tree

- [ ] Run Release build, full suite, `dotnet format`, `jb inspectcode` (parse the XML) **before** the proof.
- [ ] Then run the proof under the chosen disposition: ≥40 consecutive green full-suite runs, or R4's
      operational per-gate procedure. Every run records the padded summary, the **totals assertion
      (981 + 18)**, the git hash, the **tree fingerprint** and the exit status; no `--no-build`, no
      discarded output. The break-on-failure rule is qualified by the disposition: under (a) any failure
      stops the work; under (b)/(c) a failure stops the work unless its nonzero pattern matches the recorded
      host signature. No edit between the gates and the last proof run.

## Step 6 — Record, commit, archive

- [ ] `hot-path.md`: the outcome, the accepted residual rate, any tolerance **with its sensitivity floor**,
      the reconciliation of the `:940` forbidden-row and the `:878-881` per-window-control follow-up, and
      the do-not-re-test list (tiering, `gcConcurrent`, OSR-only, forced collections, diagnostics server,
      warm-up tuning).
- [ ] Record the diagnosis, the disposition and its numbers, the race fix and its evidence, and the proof
      results with the baseline CI beside them.
- [ ] Commit the lump work, the race fix and the spec separately; then the task record, the archive and the
      journal.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release --blame-hang --blame-hang-timeout 120s
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-lumps.xml WinForward.slnx     # parse for <Issue

# proof — every run recorded, totals asserted (both assemblies), break on failure
# fingerprint = tree hash of the frozen tree; the per-gate loop is the (b)/(c) criterion
log=/tmp/wf-lumps-proof.txt; : > "$log"; rev=$(git rev-parse --short HEAD); tree=$(git write-tree)
summary='Failed: *[0-9]+, Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+'
for i in $(seq 1 40); do
  out=$(dotnet test WinForward.slnx -c Release 2>&1); rc=$?
  echo "suite $i $rev $tree rc=$rc $(echo "$out" | rg -o "$summary" | paste -sd' | ')" >> "$log"
  [ "$rc" -eq 0 ] || { echo "FAILED at suite run $i"; break; }
  echo "$out" | rg -q 'Failed: *0,' || { echo "FAILED at suite run $i"; break; }
  echo "$out" | rg -q 'Passed: *981, Skipped: *0, Total: *981' || { echo "CORE TOTALS MOVED at run $i"; break; }
  echo "$out" | rg -q 'Total: *18' || { echo "ANALYZER TOTALS MISSING at run $i"; break; }
done
# per-gate operational proof (dispositions b/c): one gate per process, N runs, measured rate,
# signature-matched failures. Totals asserted per class so a vacuous filter match cannot pass.
totals='HotPathAllocationGateTests:11 CapturePumpReadCallTests:3 SweepAllocationGateTests:2 NdisCapturePumpTests:14'
signature='^(168|5216|7384|7448)$'
for entry in $totals; do
  gate=${entry%%:*}; expected=${entry##*:}
  for i in $(seq 1 20); do
    out=$(dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~$gate" 2>&1); rc=$?
    line=$(echo "$out" | rg -o "$summary" | tail -1)
    actual=$(echo "$out" | rg -o 'Actual: *[0-9]+' | tail -1 | rg -o '[0-9]+')
    echo "gate $gate run $i $rev $tree rc=$rc total=$expected $line actual=${actual:-none}" >> "$log"
    echo "$out" | rg -q "Total: *$expected" || { echo "VACUOUS MATCH: $gate run $i"; break; }
    if [ "$rc" -ne 0 ]; then
      if echo "${actual:-x}" | rg -q "$signature"; then echo "accepted host hit: $gate run $i ($actual B)"; else echo "UNEXPLAINED FAILURE: $gate run $i"; break; fi
    fi
  done
done
```


## Execution record (implement sub-agent + parent verification, 2026-09-30)

**Disposition (c)** — no product fix for the lump, no gate change; the per-gate-process proof replaces the
suite-level streak, and the suite-level rate is recorded as an accepted host property with its CI.

| Step | Outcome |
|---|---|
| 1 multiplicity | A temporary in-assembly reproducer (deleted) ran the exact window bodies with per-iteration deltas: pump **196,774** windows across the xunit thread, a dedicated `Thread`, a genuinely other pool thread plus allocation/blocking-GC/thread-churn/live-allocation load arms → **0 lumps**; sweep **53,107** windows on all three contexts → **0**; counter-read-only control **11,509,218** windows / 9.1 B iterations → **0**; bare `/tmp` host **148,525,260** iterations → **0**. Census: **0/27 processes** (Wilson 95 % ≤ 12.5 %), **0/249,881 windows** (≤ 1.6e-5). Recorded honestly: each census arm shows one 152–272 B delta at iteration 0, reproduced by the counter-only control → first-transition overhead, not the residual. Conclusion: the residual is **suite-process-conditional, not window-conditional**. |
| 2 attribution | Thread arms null. `dotnet-trace` 10.0.745401 installed after writing its guarantee to `/tmp/wf-lump-tool-guarantee.txt` **before** running: gc-verbose sampling is budget-based (~100 KB/thread), so a sparse ≤8 KB one-shot on a non-allocating thread is never sampled. Positive control (8 KB per iteration) → 1,559 samples naming the type; the same class allocated **once** → **0 samples** while the counter read its 8,280 B. **Instrument live, blind to exactly the class under diagnosis → R1 re-scoped, no allocator named.** |
| 3 disposition | (c). The counter-read-only control **of the residual itself** could not be run (it never fires in isolation; a co-resident control would absorb the once-per-process event and mask the gates), so the spec records that the product is **not** claimed excluded by a control of the residual. |
| 3 sensitivity | Same exact window and same N, so regression power is unchanged: at N = 20 over 1,000-iteration windows, a per-`K`-iteration allocation is caught with K=5,000 → 98.8 %, 10,000 → 87.8 %, 20,000 → 64.2 %, 50,000 → 33.2 %; rarer than ~1/50,000 is no longer reliably caught. What the per-gate proof does **not** prove is suite-level stability, because it does not exercise the suite-conditional trigger. |
| 3 injected check | No shape changed, re-proven anyway: one `new byte[64]` per iteration → pump gate `Actual: 88000`; one `new byte[64]` inside the single sweep window → `Actual: 88`; files restored byte-identically → green. min-of-K and "at most one nonzero" stay rejected. |
| 4 race fix | `InterceptionHealthMonitor` now captures a `ForcedRefreshTrigger` (counter, streak, degraded, earned cooldown, window counts) **under the monitor gate in the same locked section that arms the trigger** and hands it to the handler; `LayeredCaptureRunner.OnForcedRefreshTriggered` builds the `runner.forcedRefresh` record purely from it (four separate lock reads → one atomic observation). Tests: the bounded post-state wait plus a new deterministic `TriggerSnapshotSurvivesAResetInsideTheHandler` (runs `NoteRefreshCompleted` inside the handler). Fail-before evidence: `/tmp/wf-proof-suite-failure-10.txt` plus the ordering argument; no seam, no 200× loop. |
| 5 spec | `hot-path.md`: new section with the arm table, census, multiplicity, the suite-conditional finding, the tool blindness, the per-window control priced and rejected, the `:940` forbidden row kept with the injected numbers, the disposition, the accepted rates with CIs, the sensitivity floor, the do-not-re-test list and the per-gate procedure. `windows-ndisapi.md`: the trigger-time-snapshot invariant added to both copies of the adapter-view self-healing section. |
| 6 gates | Release build 0 warnings; suite **981 + 18**, 0 failed; `dotnet format` exit 0 empty; `jb inspectcode` reported 2 genuine findings in the new test code (`AccessToModifiedClosure`, `MergeIntoPattern`), both fixed (the pattern merged; the self-reference suppressed with the reason that the handler runs only after the constructor returns), after which the test project reports **0 issues / 0 `CSharpErrors`**. Reproducer deleted; no scratch remnants. |

**Proof on the frozen tree** (`rev=e28cb87`, `tree=883583fc055516886b90b30091c07597a9ae677e`):

- **Per-gate criterion: met.** 20 process runs per gate × 4 gates = **80/80 green**, **0** signature-matched
  host hits, **0** unexplained failures, every run's class total asserted
  (`HotPathAllocationGateTests` 11, `CapturePumpReadCallTests` 3, `SweepAllocationGateTests` 2,
  `NdisCapturePumpTests` 14).
- **Suite-level rate measured, not claimed**: 13 runs → 11 green, 2 failed (**~15 %**, consistent with the
  predecessor's pooled 4/76 = 5.3 % [2.1, 12.8] and its 3/29 + 4/29). One failure is a **signature match** —
  `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes`, `Actual: 7448` (a recorded size) — so it
  is the accepted host event. The other is **not** an allocation gate and not a signature size:
  `UdpAssociationHeadTests.TheDefaultHeadKeepsTheAcceptanceLoadShared` failed with
  `Expected: 282, Actual: 281` (`pool.AssociationCount` at `UdpAssociationHeadTests.cs:104`) — a third,
  different family (a product sharing invariant observed 1/13 runs, plausibly a race between the count and
  the rent completion). It is **not** fixed here and is carried by the follow-up task
  `09-30-udp-association-head-count-flake`.
