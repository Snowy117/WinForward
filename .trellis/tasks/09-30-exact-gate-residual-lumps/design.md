# Design — residual exact-gate lumps and the health-signal race

Two things must be established before any gate or product change: **who allocates** (attribution, not just
"when"), and **what the health-signal race actually is**. The predecessor already eliminated the tiering
family, `gcConcurrent`, OSR-only tuning, forced collections and the diagnostics server with replicated
evidence, so none of those is re-tested here.

## 0. What the plan review corrected

1. **The family table was mostly dead on arrival.** First-execution JIT (F1) cannot explain the two failing
   windows: both drive a fixed branch set (`script = [static (_, _) => 0]` reaches only the
   `readCount == 0` path, `NdisCapture.cs:286-297`; the sweep's 4,096 keys are all expired with
   `idleTimeout = TimeSpan.Zero`, `FlowTable.cs:90-118`) and the warm-up plus probe already cover it. The
   pump preallocates `_batchBuffers` (`NdisCapture.cs:143-144`), so there is no ring or `TryDequeue` on that
   path at all. A fresh thread's first counter read (F2) happens **inside the preflight**
   (`CapturePumpReadCallTests.cs:100`, `SweepAllocationGateTests.cs:41`, `NdisCapturePumpTests.cs:319`),
   before the window, and its cost is absorbed into the value it returns — and the 5,216 B pump lump landed
   *after* `stabilized` (`/tmp/wf-proof-suite-failure-6.txt`, stack at `CapturePumpReadCallTests.cs:115`).
   The 136 B figure belongs to a fresh *method's* first call, not to the counter read. F4's mechanism
   ("pooled structure refill caused by a GC") has no mechanism in this code: managed lists/arrays/
   dictionaries do not refill on GC, the pump preallocates, and the console control already showed 0 jumps
   under forced Gen0/1/2 plus 668 sibling-triggered collections (`hot-path.md:882-883`).
2. **min-of-K is unsound and was refuted before implementation.** For a regression allocating once per
   ~5,000 iterations in a 1,000-iteration window, the single window catches it with p ≈ 18 % per run; the
   minimum of five windows catches it only if all five contain the event, ≈ 0.02 % — three to four orders of
   magnitude of power lost, precisely for the rare-path class under diagnosis. The proposed safeguard was
   circular: injecting a per-iteration allocation inflates *every* window by construction. The honest
   variant (one injected allocation inside one of K windows) would pass min-of-K, so the shape is rejected.
   The weaker alternative ("at most one of K may be nonzero") is rejected for a different reason: it loses far
   less sensitivity than min-of-K (~43 % against the 1-in-5,000 example, not ~0.02 %), but it cannot pass
   the honest single-window injected-allocation check — it tolerates exactly a one-window allocation, which
   is the class under diagnosis. The rejection log records the accurate reason.
3. **The evidence table mixed measurement arms.** 1,880 B is the documented *tiering* lump
   (`WinForward.Core.Tests.csproj:11`, `hot-path.md:858-859`); 7,336 / 7,360 / 8,008 B were measured with
   tiering **on** (`hot-path.md:855-859`). The residual sizes actually observed with tiering off are
   **168, 5,216, 7,384, 7,448 B**, and "once per process, random iteration" is the *tiering control's*
   signature — it was never measured for the residual. Any tolerance decision needs that multiplicity
   measured first.
4. **The health-signal race was read backwards.** The test asserts on the log's `consecutive` field, which
   the product captured at log time (`LayeredCaptureRunner.cs:248-259` → `RecordingLogger.cs:50-53`), and it
   already waits past the refresh before reading it (`LayeredCaptureRunnerHealthSignalTests.cs:34-35`). The
   only writer of `0` is `NoteRefreshCompleted()` (`InterceptionHealthMonitor.cs:156-164`, called from
   `LayeredCaptureRunner.cs:230`) racing the field build, which is possible because `_demandGate.Signal()`
   (`:245`) precedes the log build (`:251`). **No refresh-completion signal exists to wait on**:
   `Generation(i).Started`/`InstalledScopes` complete *before* the reset. "Wait for the reset" would make
   the asserted value deterministically `0`.

## 1. Attribution first (the only step that can justify anything)

The observable is a **per-thread counter event**, so the primary discriminator is **thread context**, not
host context. Three arms, same window body:

| Arm | Thread | What it separates |
|---|---|---|
| A | the xunit thread the gate already runs on | the status quo |
| B | a dedicated `Thread` created for the measurement | thread-lifetime/first-touch events |
| C | a pool thread **other than the test's** (`Task.Run`) — the pool reuses threads, so this arm differs from B by thread *history*, not freshness | pool-thread reuse and starvation effects |

Plus one **tool-based attribution** attempt, time-boxed: the host has the SDK but no profiler installed, so
budget one tool install (`dotnet-trace` with the allocation-sampling provider, or `dotnet-gcdump`) and try
to capture the allocating **type or stack** for a lump. The plan must state **what the chosen tool
guarantees to observe before it runs**: allocation sampling is budget-based (order 100 KB between samples),
so a sparse ~7 KB one-shot lump need not be sampled at all, and a heap dump taken afterwards may miss a
transient object. A null capture is therefore recorded as "the instrument could not see it" — which routes
to R1's re-scope — and never as "nothing allocated". Per-iteration deltas plus `GC.CollectionCount` and
thread id establish *when*; only a type/stack capture can establish *what*, and R1's "named cause" is
unreachable without it. If attribution cannot name the allocator inside the time box, R1 is re-scoped to a
**family-level cause with the product explicitly excluded**, and that re-scope is recorded here rather than
smuggled past the requirement.

Also measured, because every later decision needs it: **residual multiplicity** — how many lumps a single
process shows, in which window, and whether the count depends on the thread arm.

The **window** reproducers are temporary files inside an IVT'd assembly and are deleted when the cause is
fixed or recorded; the `/tmp` host carries only the public counter-read control. **A co-resident reproducer absorbs the once-per-process event it hunts**, so it must never run
concurrently with the gates it is investigating: the arms run one at a time, and the gates' own suite runs
are the evidence, not the reproducer's.

## 2. Dispositions (chosen from the evidence, not in advance)

| Evidence | Disposition |
|---|---|
| An attributable cause with a product owner | fix the product (product-change discipline: spec + fail-before test), keep every gate exact |
| An attributable host/runtime event with **no** product owner | no gate change. Replace the *proof*: run each exact gate **in its own process** (`dotnet test --filter`), which changes **attribution** — one gate's window per process instead of ~14 competing windows — and **not** incidence. It does **not** make the window post-event (the 5,216 B lump landed after the probe reported `stabilized`, `/tmp/wf-proof-suite-failure-6.txt`) and it can even raise the chance the gate's own window catches the event, because that window is a larger share of the process's measured work. The criterion is therefore **operational**: N per-gate process runs per exact gate, each recorded with its padded summary, totals, git hash and tree fingerprint, and the result is the **measured per-gate residual rate**, where a failure counts as the accepted host event only when **checkable predicates** hold: the gate is one of the recorded victim gates, the reported delta equals one of the recorded sizes (168 / 5,216 / 7,384 / 7,448 B), the run got past the gate's own `Assert.True(stabilized)` preflight, and the injected-allocation check still fails when re-run against the same gate; plus the suite-level rate with its CI |
| Unattributable after the time box (whatever the measured multiplicity) | the same proof change and the same operational criterion, plus an explicit **sensitivity statement** in the spec: which allocation rates the per-process gate no longer catches. Multiplicity ≤ 1 per window does not license a tolerant shape — it only bounds how often the accepted event can appear |

**The console arm's constraint**: `RunIterationForTests` is `internal`, so a bare console host can carry the
exact window body only through the IVT'd benchmark assembly (`WinForward.Benchmarks`, `NdisApiAbi.cs:7`) —
that is where the arm lives, not in a new project.

**Explicitly rejected**: min-of-K, "at most one of K nonzero", retries keyed on a clean probe, any warm-up
tuning, and any threshold relaxation. The reasoning for each rejection is recorded in the spec so the next
gate author does not reinvent them, together with the two spec statements this design departs from and must
reconcile: the "relaxes the byte assertion or skips an iteration → forbidden" row (`hot-path.md:940`) and
the per-window-control follow-up (`hot-path.md:878-881`), which is priced here (it cannot tell a random host
lump from a product allocation, because the lump lands in exactly one of the two windows) and therefore not
adopted. If none of the three dispositions can be established, the honest
outcome is "keep the exact gates unchanged; the suite-level criterion is a host property" — recorded as the
task's result, not as a fix.

## 3. The health-signal race (product change)

The race is in the product: `_demandGate.Signal()` (`LayeredCaptureRunner.cs:245`) publishes the refresh
request *before* the log record is built from the monitor's state (`:251`, `:248-259`), so
`NoteRefreshCompleted()` can reset the streak (`InterceptionHealthMonitor.cs:156-164`) between the trigger
and the capture and log `consecutive = 0`.

Fix: make the captured state and the trigger consistent — either capture the streak and window snapshot
**under the monitor gate and hand them to the trigger**, or move `Signal()` **after** the log build — then
add the bounded wait for the *post*-state assertions in the test (`:53-54`), which are racy in the opposite
direction today. The invariant is recorded in `.trellis/spec/backend/windows-ndisapi.md` (which owns
`NoteRefreshCompleted`/`runner.forcedRefresh`, `:492,:502`), not only in `hot-path.md`.

A second, coarser mechanism is worth recording because it explains why the race only shows up under load:
the test's `WaitForAsync` polls at 10 ms and its own doc comment records seconds-long starvation under suite
load (`AsyncTestExtensions.cs:6-21`), which is the coherent context for the 50 ms guard window at
`LayeredCaptureRunner.cs:198-203`.

Honest limitation, stated up front: **no deterministic fail-before test exists** for this race without a
seam — the pre-fix evidence is the captured failure (`/tmp/wf-proof-suite-failure-10.txt`) plus the ordering
argument above. R3's bounded alternative (a deterministic *post*-state assertion plus the recorded
pre-fix evidence) is therefore the deliverable, and the "run the test 200×" idea is dropped: an isolated
loop cannot reproduce a thread-pool-starvation race.

## 4. Ordering, gating and recording

Full gates (Release build, format, `jb inspectcode`) run **before** the stability proof, and the tree is
frozen for the proof: no edit between the last gate and the last proof run. Each proof run records the
padded summary **and** the totals assertion (981 + 18) **and** the git hash **and** the tree fingerprint
(the predecessor recorded `rev=0cf25ba tree=6b89dc171fe4`), so a silently dropped collection cannot green
the loop. The baseline is the predecessor's recorded rate reused with its CI (its own power table shows 20
runs at 80.7 %), not a fresh, weaker one.

## 5. Risks

| Risk | Mitigation |
|---|---|
| The allocation cannot be attributed in the time box | R1 is re-scoped to family level with the product excluded, and disposition 2 or 3 follows; the re-scope is recorded, not hidden |
| A reproducer masks the event it hunts | reproducer arms run one at a time and never concurrently with a suite proof; the suite run is the evidence |
| The race fix is unverifiable end to end | the pre-fix capture and the ordering argument are the evidence; a seam is allowed if it makes the interleaving deterministic, with the seam documented as test-only |
| The proof change is read as gate weakening | the gates stay byte-exact and unchanged; only the *proof procedure* changes, and the spec states the resulting sensitivity |
| Re-litigating settled ground | `gcConcurrent`, OSR-only, warm-up tuning, forced Gen0/1/2 collections and the diagnostics server (`DOTNET_EnableDiagnostics=0`, `hot-path.md:884`) are on the do-not-re-test list with their evidence. The F4 correlation, if run at all, runs **only** in the single-threaded console arm: under the suite's parallel classes, `GC.CollectionCount` moves constantly and cannot discriminate |
