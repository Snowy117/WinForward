# Implementation plan — F5 pump I/O shape: speculative read, read-call instrument, event-driven idle wake

Five steps: one instrument (which makes the *pre-change* read shape exactly measurable and records it
red), one read-shape flip, one idle-wait step, one Windows-binding/wiring step, one evidence/spec
step. Each product step lands its exact proof in the same commit, so every commit leaves a green,
shippable tree; the transient red readings are captured during the step and recorded in the artifact
README. Every step is independently revertible (`design.md` §10).

Prerequisites already true: `dotnet` on PATH via direnv, `rg`/`fd`, the archived baselines under
`benchmarks/results/2026-09-29-benchmark-coverage/`, and the F2/F3/F4 facts as the regression surface.

**Read before starting**: `research/implementation-notes.md` (anchors, the ABI-evidence table, the
discrepancy register D1–D13, the reproduce-before numbers) and `design.md` §2–§6. Four restatements
change what the work *is* — the first from the operator's guard decision, the rest from the plan
review: **the read-first shape never lands without its self-healing guard** (`design.md` §3.5, Step 2b,
one commit); the read shape is **read-first with the query kept as the non-success disambiguator**, not
the query deleted (notes D2/D11); the "counting seam" is at the
**driver↔native** boundary, because the pump↔driver one already exists and stays green (notes D3); and
the vendor samples' **manual-reset** event is deliberately not copied (notes D9).

---

## Step 0 — Preflight

- [x] Re-verify every `file:line` anchor in `research/implementation-notes.md` §1–§6 against the
      current source (`rg -n` the named members). They were read on tree `56dd85f`
      (`git write-tree` `6599093d8033ce2b624a0b110c4fb2a65aa41d5f`); confirm no drift, and re-read
      `NdisApiDriver.cs:136-217`, `NdisCapture.cs:264-318`, `NdisNativeCallStatus.cs`,
      `NdisApiAbi.cs:155-231` before the first edit.
- [x] Record the current suite totals so a green tree is proven before the first edit:
      `dotnet test WinForward.slnx -c Release` (the F4 archive recorded `WinForward.Core.Tests 1,057`
      + `WinForward.Analyzers.Tests 18`; refresh) and the per-gate totals string
      (`hot-path.md:1303`: `HotPathAllocationGateTests:11 CapturePumpReadCallTests:3
      SweepAllocationGateTests:12 NdisCapturePumpTests:14`).
- [x] Confirm the four referenced baselines are on disk and quote their recorded numbers into the new
      artifact README's "before" section: `benchmarks/results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl`
      (3 runs: 877.6/885.6/885.8 polls/s, 0.017798/0.018501/0.017826 CPU-s per idle-s, 0 B,
      wake p50 0.0779/0.0783/0.0791 ms, p99 0.1890/0.1877/0.1662 ms), the README's reading
      conventions (`:275-330`), and the F2/F3/F4 per-gate proof procedures (`hot-path.md:1078-1139`,
      `:1292-1318`).
- [x] `git status` clean; create the branch/worktree the operator wants; note the base revision in
      every artifact header.
- [x] Enumerate the call sites the new API touches so no red compile is discovered late:
      `rg -n ': INdisPacketReader' tests/ benchmarks/ src/` (twelve implementations — the pump's read
      interface must NOT be widened, which is why the arrival signal goes into the options record),
      **`rg -n 'BuildMultiRequest|MultiRequestByteCount|MaxStackMultiRequestBytes' src/ tests/ benchmarks/`**
      (three facts call the builder: `NdisApiBatchedSendAbiTests.cs:36/60/79` — it stays on
      `NdisApiDriver`, notes D13), plus
      `rg -n 'NdisReadShapeMismatch|ReadShapeMismatchSink|IsQueryFirstForDiagnostics' src/ tests/`,
      plus `rg -n 'new NdisCapturePump\(|NdisCapturePumpOptions|MultiAdapterCaptureLoop\(|SetPacketEvent' src/ tests/ benchmarks/`
      (3 `new NdisCapturePump(` in benchmarks — `CapturePumpBenchmarks.cs:43` and the scenario's two
      rows — plus the loop's `Select`; the loop ctor's two test call sites at
      `NdisCaptureResilienceTests.cs:202/230`).
- [x] **Every series command in this plan carries an explicit `--output` or `--artifacts` path**: the
      stability runner always echoes its JSONL to the console but writes a file only when `--output` is
      given (`StabilityContext.Write`), and a BDN run without `--artifacts` leaves its exports under the
      default directory — a run that is not captured leaves no citable record.
- [x] Confirm the pinned upstream evidence is reproducible if a reviewer asks:
      `git clone --depth 1 https://github.com/wiresock/ndisapi.git && git fetch --depth 1 origin
      417b8734e844083a10236387fba705d94a2d6bc9` — check `ndisapi.vs2012/ndisapi.def:16`,
      `include/ndisapi.h:307`, `ndisapi/ndisapi.cpp:3586`, `examples/dotNet/TestDotNet/Program.cs:58/275-327`.

---

## Step 1 — Instrument first: the driver↔native read seam, the counters, the red-before  [rollback point A]

Nothing in this step changes the shape: `TryReadPackets` keeps querying first, so every new fact that
asserts a read-first shape is **red by construction** and its exact failure text is the recorded
before.

- [x] `src/WinForward.NdisApi/NdisReadPacketCalls.cs` (new): `internal interface INdisReadPacketCalls`
      with `GetAdapterPacketQueueSize(nint, out uint, out int)` and
      `ReadPackets(nint, NdisPacketBuffer[], int count, out uint, out int)`; its single implementation
      `NdisNativeReadPacketCalls(NdisApiSafeHandle handle)` absorbs `NdisApiDriver.ReadPacketsBatch`
      (`:170-188`), `ReadPacketsRequest` (`:190-198`) and the `GetAdapterPacketQueueSize` call
      (`:156`). **`BuildMultiRequest` (`:202-214`) and `MultiRequestByteCount` (`:216-217`) stay on
      `NdisApiDriver`** — three facts call the builder (`NdisApiBatchedSendAbiTests.cs:36/60/79`, notes
      D13, a planning error the review caught) — the new class calls them, `MultiRequestByteCount`
      widens `private` → `internal static`, and `MaxStackMultiRequestBytes` widens `private const` →
      `internal const` so the copy-free `stackalloc` decision keeps one source of truth. Nothing is
      moved; the send path (`:275-279`) is untouched.
- [x] `NdisApiDriver`: private `INdisReadPacketCalls _readCalls` field, assigned in `Open()`
      (`:51`) with `new NdisNativeReadPacketCalls(handle)`; `internal static NdisApiDriver
      CreateForTests(INdisReadPacketCalls readCalls)` using `NdisApiSafeHandle.FromRawHandle(0)`
      (invalid handle ⇒ `Dispose` never reaches `NdisApiNative.CloseFilterDriver`, so a Linux test
      never trips the DLL resolver); document both in the class XML doc.
- [x] Read counters on the driver (the Windows-experiment instrument, `design.md` §3.3): the fields
      `long _batchReads, _queueSizeQueries, _emptyReads, _failedReads; int _lastFailedReadNativeError;`,
      `Interlocked`-incremented at the **exact sites** the design pins — `BatchReads` once per read
      call (any result), `QueueSizeQueries` once per query call, **`FailedReads` once per non-success
      read immediately after capturing `readError` and before classification/throw**, `EmptyReads` once
      per drain whose *classified* result is 0 (read-success-0 or read-fail-then-query-0),
      `LastFailedReadNativeError` on every non-success read. An off-by-one site makes the
      hypothesis-A/B discrimination unsound, so the increment order is part of the instrument, not an
      implementation detail. Exposed as `internal NdisReadDiagnostics ReadDiagnostics` (new record
      beside `NdisPumpDiagnostics`). 0 B, no lock, no allocation.
- [x] `tests/WinForward.Core.Tests/NdisApiReadShapeTests.cs` (new, `[SupportedOSPlatform("windows")]`
      per fact, same pattern as `CapturePumpReadCallTests`): a `RecordingReadCalls` fake that scripts
      `(readResult, packetsSuccess, readError)` and `(queryResult, queued, queryError)` and records the
      **call order**. Facts, written for the target shape (§`design.md` §2.3/§3/§7):
      `SpeculativeReadIssuesOneReadAndNoQueryForANonEmptyDrain`,
      `SpeculativeReadNeverQueriesBeforeReading`,
      `AnEmptyQueueYieldsZeroPacketsWithNoErrorUnderEitherReadResult` (both ABI hypotheses),
      `AReadFailureOnANonEmptyQueueWhoseGuardedProbeAlsoFailsThrowsTheReadError` (the fake fails
      **every** read, so the fact is green on the pre-change body, on the unguarded read-first body and
      after the guard — the *healing* half is Step 2b's fact),
      `AFailedReadWithAFailedQueryStillThrowsTheQueryError`,
      `ANullHoleInTheBatchArrayThrowsOnADrainThatWouldOtherwiseBeEmpty` (the `windows-ndisapi.md:427`
      contract change of `design.md` §2.4),
      `TheReadDiagnosticsCountOneReadPerDrainAndOneQueryOnlyOnAFailedRead` (scoped to the conforming and
      query-first paths: one read per drain, a query only after a failed read — the mismatch path's extra
      probe read is Step 2b's own fact; the counters are the Windows experiment's instrument, so a test
      reads them; leaving them unread would repeat F4's unused-seam lesson),
      `DriverReadPathAllocatesNoManagedBytes` (**review MAJOR 5** — the per-drain driver body is
      constructible on this host through `CreateForTests`, so it carries the same exact window
      contract as every other gate: a non-recording fake, a JIT warm-up, bounded exactly-zero probe
      batches, `Assert.Equal(0, allocated)`, unchanged managed thread id, a thread-independent
      call-count backstop, and every assertion outside the window; `stackalloc` is native stack, so it
      is not what the counter sees).
- [x] Record the **red**: run the new class on this instrumented pre-change body; capture the exact
      failure text per fact (`Expected: 0, Actual: 1` queries; recorded order `[Query, Read]`) into
      `benchmarks/results/2026-10-01-pump-io-shape/read-shape-counts.txt` with the command, the tree
      revision and the facts' names. This discharges AC1's "red-before is recorded" **before** any
      product behaviour changes. (The prompt-counted facts — the two throw facts, the null-hole fact
      and the byte gate — are green on this body; the red set is named per fact in the file.)
- [x] Rewrite `NdisApiAbiTests`' classifier facts to the **three-helper** split (`ClampReadCount`, the
      reused `HasQueuedPackets`, `ThrowReadFailedOnNonEmptyQueue`): the clamp/throw/clamp-overreport
      matrix must keep every existing assertion (`:58-78`) with its message assertion updated to the
      new `requested` argument (now the driven capacity), and
      `QueueStatusSeparatesIdlePollingFromNativeFailures` (`:47-56`) is untouched.
- [x] Start `benchmarks/results/2026-10-01-pump-io-shape/README.md`: host/runtime header, the command
      lines, the "before" table (baseline series + the driver-level derived counts query+read = 2 per
      non-empty drain), the exact-vs-series rule, and the empty "after" table.

Validation: `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiReadShapeTests"`
expects the target-shape facts **red** (the order/count facts and the counter fact) and the
prompt-counted facts **green** (the query-failure throw, the all-reads-fail throw, the null-hole fact,
the byte gate);
`~NdisApiAbiTests` green; `~CapturePumpReadCallTests` and `~NdisCapturePumpTests` untouched and green;
`~NdisApiBatchedSendAbiTests` green and untouched (the builder did not move); no product behaviour
changes (the body still queries first).

Rollback: revert the seam file, the driver field/ctor/test factory, the visibility widenings and the
new tests; delete `read-shape-counts.txt` and the README skeleton. Behaviour-zero by construction.

---

## Step 2 — The read shape: speculative read, query as the non-success disambiguator  [rollback point B]

- [x] `NdisApiDriver.TryReadPackets` (`:142-166`): read first with `count = buffers.Length`; capture
      `readError` immediately; on `!readSucceeded` issue the query inside the **same** gate lease and
      capture `queryError`; classify outside the lease.
- [x] `NdisNativeCallStatus`: keep `HasQueuedPackets` verbatim; replace `InterpretBatchReadResult`
      with `ClampReadCount(uint packetsSuccess, int requestedCount)`; extract the "read failed from a
      non-empty queue" `Win32Exception` into `ThrowReadFailedOnNonEmptyQueue(int requestedCount, int
      readError, nint adapterHandle)` keeping its text and native error exactly (asserted at
      `NdisApiAbiTests.cs:64-67`, with the `requested` argument updated to the driven capacity). Three
      helpers, one classification site, no second copy of the matrix anywhere.
- [x] Update the doc comments that state the old shape: `TryReadPackets`' XML doc (`:136-141`), the
      `INdisPacketReader` doc (`NdisCapture.cs:24-28`, "0 means the adapter queue was empty" stays
      true), and `NdisApiDriver`'s class remarks if they mention the pair.
- [x] Flip the Step-1 facts green. Each fact's assertion keeps its exactness; no assertion is relaxed
      to accommodate the new order.
- [x] Capture the **after** call-shape counts into `read-shape-counts.txt` (per drain: happy path
      1 read / 0 queries, **short count** 1/0, empty-B 1/0, empty-A 1/1, error 1/≤1 then throw) with
      the recorded red beside them, and add the **partial-batch** row to the artifact README's
      Windows-open-items table (`design.md` §9.1, PRD requirement 7): a full-capacity request must
      succeed with a short count; if it fails, this step's rollback is the mandated fallback because
      the failure degrades a working adapter with packets available. The row is a recorded experiment,
      never a claim — nothing on this host can exercise it.
- [x] **Rollback shape (the PRD's literal fallback):** if the two empty facts cannot both be made
      green with a single body — e.g. the reviewer/operator reads PRD requirement 1 as forbidding the
      disambiguating query — revert this step's body to query-first and record the negative result in
      the README (the seam, the counters, the facts' red text and Step 3's idle work all survive;
      requirement 1 is then discharged as "the pinned ABI cannot distinguish empty from error on this
      host" — `prd.md:24-26`). **Step 2b's guard reverts with this body**: on a query-first shape the
      guard point is unreachable dead code, so leaving it in would be unverifiable state (§10 row 2b).

Validation: `~NdisApiReadShapeTests` green; `~NdisApiAbiTests` green; `~CapturePumpReadCallTests` and
`~NdisCapturePumpTests` green untouched (the pump is not involved); `~NdisCaptureResilienceTests`
green (the read-error path is unchanged downstream).

Rollback: one-commit revert of the body + the classifier split; the instrument stays.

---

## Step 2b — The self-healing ABI-mismatch guard (lands in the same commit as Steps 1–2)  [rollback points A/B]

The read-first shape must never be committed without this: on hardware whose ABI fails a request larger
than the queue depth, an unguarded read-first drain throws a transient read error, retries five times
(~3.1 s) and **degrades a healthy adapter** (review MAJOR 2 / `design.md` §3.5).

- [x] `NdisApiDriver` guard state: `private readonly ConcurrentDictionary<nint, byte> _queryFirstAdapters`
      probed with `TryGetValue` beside `_adapterGates.Get` (`:153`) — lock-free, no allocation, only grows
      (same model as the gate map); `internal bool IsQueryFirstForDiagnostics(nint adapterHandle)` for the
      facts and the Windows run.
- [x] The guard point inside `TryReadPackets`, exactly as `design.md` §3.5's transition shows:
      read-first; on failure query; `queued == 0` → 0; `queued > 0` and **not yet armed** → arm (sticky),
      publish one diagnostic, and probe with a single `read(min(queued, buffers.Length))` — the
      disambiguating query *is* the query of the query-first attempt, so the whole self-heal costs one
      extra read, not a query+read pair; probe failure → `throw readError` (today's path). `queued > 0`
      and already armed → `throw readError` immediately. The guard's probe never touches the pump's
      transient-retry budget.
- [x] The diagnostic: `public readonly record struct NdisReadShapeMismatch(nint AdapterHandle, int
      RequestedCount, uint QueuedPacketCount, int NativeError)` + `public Action<NdisReadShapeMismatch>?
      ReadShapeMismatchSink` (set with `Volatile.Write`, invoked at most once per arming). `WinForward.Runtime`
      is not a friend assembly, so the sink must be public; the precedent for a composition-wired sink on a
      shared component is `NdisPacketBufferPool.Shared.AccountingSink` (`Program.cs`).
- [x] `NdisReadDiagnostics` gains `ReadShapeMismatchCount` (`Interlocked`), and
      `NdisCaptureGenerationFactory.Create` wires the sink: resolve the adapter identity by handle from
      that generation's `bindings` (cold linear scan), then one rate-limited
      (`RuntimeLogThrottle`, like `_slotExhaustedWarn`) `adapter.readShape.mismatch` warn with
      `adapter`, `requested`, `queued`, `nativeError`. Without this wiring item (a) of the guard's
      semantics does not exist.
- [x] Facts (`NdisApiReadShapeTests`, all against `CreateForTests`, `[SupportedOSPlatform("windows")]`):
      - `AMismatchHealsTheShapeInsteadOfThrowing` — drain 1 scripts `read FALSE(31)` + `query 3` then the
        probe `read TRUE(3)`: returns 3, no throw, recorded order `[Read, Query, Read]`,
        `ReadShapeMismatchCount == 1`, `IsQueryFirstForDiagnostics == true`;
      - `TheGuardFiresExactlyOnceAndLaterDrainsAreQueryFirst` — three further non-empty drains each
        record `[Query, Read]`, the mismatch count stays 1, and no drain issues a read-first attempt;
      - `TheGuardNeverFiresOnAConformingDriver` — 1,000 mixed drains (non-empty, short, empty) record
        `ReadShapeMismatchCount == 0`, `IsQueryFirstForDiagnostics == false` and a happy path of `[Read]`
        with zero queries;
      - `TheMismatchNeverReachesTheTransientRetryBudget` — a **real** `NdisCapturePump` over a **real**
        `NdisApiDriver` built by `CreateForTests` with the mismatching fake, `TransientRetryBaseDelay =
        TimeSpan.Zero`: the sink fires once, the packet is delivered, `Diagnostics.IsDegraded == false`
        and `TransientReadRetryCount == 0` (assert the pump's own `Diagnostics`, not just the driver's);
      - `AGuardedProbeThatAlsoFailsThrowsTheReadErrorLikeToday` — the probe read fails too ⇒
        `Win32Exception(readError)`, mismatch count 1, sticky flag true (the fallback-to-today path);
      - `TheMismatchDiagnosticCarriesTheAdapterRequestedDepthAndError` — the observed record equals
        `(handle, buffers.Length, 3, 31)`.
- [x] Record the **red**: with the read-first body landed and the guard **not yet added**, run the two
      pump-level facts — the mismatching fake must degrade the adapter after the full retry budget.
      Capture into `read-shape-counts.txt`: the command, the tree revision, `TransientReadRetryCount = 5`,
      `IsDegraded = true`, `LastDegradedNativeErrorCode = 31`, and the fact names. Then add the guard and
      go green. (This is the only place the unguarded read-first body ever exists — it is never committed.)

Validation: the six guard facts green; the Step-1/2 facts green; `~NdisCaptureResilienceTests` green
(the degraded path itself is unchanged); `~NdisApiAbiTests` green; full suite green.

Rollback: revert the flag, the guard point, the diagnostic struct, the sink and its factory wiring —
**and revert the read shape with it** (`design.md` §10 row 2b): an unguarded read-first tree is not a
supported configuration on unverified hardware.

---

## Step 3 — The idle wait: injectable arrival signal, bounded event wait  [rollback point C]

- [x] `src/WinForward.NdisApi/NdisPacketArrivalSignal.cs` (new, **not** Windows-attributed) —
      **declares the interface too** (`directory-structure.md:66`: the seam goes beside its
      implementation, not in the consumer's file; the pump is in the same assembly, so nothing else
      moves): `public interface INdisPacketArrivalSignal : IDisposable { bool Wait(TimeSpan timeout); }`
      with the advisory-return-value doc from `design.md` §4.1, plus the implementation wrapping a
      `WaitHandle` + an optional release callback; `WaitOne` with the clamped millisecond form
      (`ValueTask`/`TimeSpan` machinery avoided, allocation-free); `Dispose` runs the release first,
      then disposes the handle. This is the class the scenario and the Linux gates drive, so the
      production wait entry point — not a fake — is inside the exact windows.
- [x] `NdisCapturePumpOptions` (`NdisCapture.cs:38-57`): add `public INdisPacketArrivalSignal?
      PacketArrivalSignal` (**borrowed**, never disposed by the pump) and `public TimeSpan?
      IdleWaitTimeout`; update the record's doc (`:34-36`, notes D8) and the pump class doc's
      wait-bound sentence (`:59-94`, `:406-415`). The default is
      `private static readonly TimeSpan s_defaultIdleWaitTimeout = TimeSpan.FromMilliseconds(100);`
      (`quality-guidelines.md:27`: `TimeSpan.FromMilliseconds` cannot be `const`, private statics are
      `s_camelCase`), with the `design.md` §4.2 rationale in its XML doc and an
      `internal TimeSpan IdleWaitTimeoutForTests` accessor (or the fact's own 100 ms literal) so the
      AC-2 assertion never reads a private member.
- [x] `NdisCapturePump.PaceIdle` (`:312-318`): signal installed ⇒ `_arrivalSignal.Wait(_idleWaitTimeout)`;
      otherwise the existing `Thread.Sleep(_pollDelay)`. Nothing else in `RunIteration` moves — the
      call site (`:295`) and the checks before it (`:290-294`) stay exactly where they are.
- [x] Facts (`NdisCapturePumpTests`, which is 288 effective lines today and stays under the
       `directory-structure.md:57` limit of 400 with these five):
       - `IdleIterationsIssueExactlyOneBoundedWaitAndNoSleepPacing` — a counting fake signal that
         **honours** the requested timeout; N = 16 iterations ⇒ exactly 16 waits, every observed
         timeout `== IdleWaitTimeout`, and the elapsed time ≈ N × 1 ms with `PollDelay = 5 s`
         (`IdleWaitTimeout = 1 ms`), i.e. the "not a sleep" discrimination has a ~5000× margin and
         cannot be flaky;
      - `WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep` — no signal ⇒ zero waits, the sleep
        path still paces (elapsed ≥ N × `PollDelay` with a small poll delay);
      - `AnArrivalSignalWakeEndsTheIdleWaitBeforeTheTimeout` — a real `EventWaitHandle(false,
        AutoReset)` behind the production `NdisPacketArrivalSignal`; the pump parks, the test sets the
        event with a frame armed, the packet is dispatched well inside the timeout;
      - `DisposeWhileParkedInTheIdleWaitCompletesWithinTheTimeout` — park with `IdleWaitTimeout = 200 ms`,
        `DisposeAsync()`, assert the run completes within timeout + margin; record the measured value;
      - `IdleWaitIterationsAllocateNoManagedBytes` — the new exact gate: production
        `NdisPacketArrivalSignal` over an unsignaled event, `IdleWaitTimeout = TimeSpan.Zero`, 1,000
        iterations, the `hot-path.md`-mandated window shape (bounded exactly-zero probe batches,
        `Assert.Equal(0, allocated)`, unchanged thread id, call-count backstop, assertions outside the
        window).
- [x] Pump-idle-seam facts (`CapturePumpReadCallTests`): one fact that the signal-installed idle
      iteration issues **one read + one wait** and never a second read, keeping the file's
      exact-count discipline.
- [x] `benchmarks/…/Stability/PumpIdleWakeScenario.cs`: **split first** — the file is 319 effective
      lines against the 400 limit (`directory-structure.md:57`), so move the probe types
      (`IterationCounter`, `IdleSample`, `WakeResult`, `GatedEmptyCaptureReader`,
      `SignallingCaptureReader`, `WakeRecorder`) into a new `Stability/PumpIdleWakeProbes.cs` before
      adding anything, keeping the scenario file the row orchestration only. Then add the two rows of
      `design.md` §8 (`pump.idleEvent`, `pump.idleWakeEvent`), extend `pump.idleWake.verdict` with the
      before/after pairs, and leave `pump.idle` and `pump.idleWake` **byte-for-byte** as the recorded
      comparators (`SoakRunner.cs:86` keeps its single `("pump", …)` entry, and
      `CapturePumpReadCallTests.PumpScenarioIsSelectableAndExcludedFromAll` stays green untouched).
      The event rows use the production `NdisPacketArrivalSignal` over an OS event; `pump.idleEvent`
      keeps the row's own `allocatedBytes == 0` throw (the `pump.idle` shape at `:92-95`) — that gate
      is what covers the **real 100 ms timeouts**, since the xunit gate can only exercise
      `WaitOne(0)`'s fast path — and reports `waitsPerSecond`, `emptyReadsPerSecond` beside the CPU
      number (attribution rule, `design.md` §7).
- [x] `pump.idleWakeEvent`'s **park-confirmation contract** (review MAJOR 4) is implemented, not
      assumed: a scenario-local decorator over the production signal records entry into `Wait` and
      delegates to it, and `DriveWakes` waits until the decorator has observed exactly one more entry
      than the previous wake (bounded) **before** the park delay and the arm timestamp, then asserts
      the dispatch count. Auto-reset makes this load-bearing: an arm+set issued before the pump parks
      would be retained and return immediately, so the entry-count check is what separates a real
      wake from a hot handoff. A missing entry fails the row (never a latency for a wake that did not
      happen), and the decorator's counts (`entered`, `returnedBeforeTimeout`, the observed timeout
      argument) are reported in the row so a shape change is visible.
- [x] The `pump.idleWakeEvent` row's `note` states the accounting change in the review's terms —
      "one extra *seam-level* read per wake relative to the proxy's accounting, because the old row
      counted only frame-delivering reads while its parked `TryReadPackets` hid the empty one; one
      IOCTL replaces ~886 empty-queue queries per second" — rather than a bare
      `readsPerWake 1.0 → 2.0`, which compares two different counts (review MINOR 12).
- [x] `benchmarks/README.md` (`:229-250`) and the scenario's doc: restate the F5.1/F5.2 coverage —
      the pump-side wait is now the measured shape, the driver pair is still below the read seam, the
      new rows are report-only series except their 0 B gates and exact counts.
- [x] Update the scenario's class doc and the rows' `note` fields to say exactly what is and is not
      established, including the park-confirmation contract and the accounting-change wording above.
- [x] Capture the after-series (3 runs, `--quick`), the before-series from the frozen Step-2 tree, and
      write `benchmarks/results/2026-10-01-pump-io-shape/README.md`'s idle-wake section with the
      before/after table (`design.md` §8's numbers).

Validation: the new pump facts green; `IdlePollIterationsAllocateNoManagedBytes` and
`CountingReaderIdleIterationsAllocateNoManagedBytes` green **unchanged**; `PumpMakesExactlyOneReadCallPerPollAndPerBatch`
green unchanged; the scenario's both old rows reproduce the recorded baseline within the reading
conventions.

Rollback: revert the option, the field, `PaceIdle` and the facts; the scenario rows go with them. The
pump returns to `Thread.Sleep` and every pre-existing gate is untouched (behaviour-zero).

---

## Step 4 — The Windows binding and the composition wiring  [rollback point D]

- [x] `NdisApiAbi.cs`: add the pinned import
      `SetPacketEvent(NdisApiSafeHandle, nint adapterHandle, nint win32Event)`
      (`EntryPoint = "SetPacketEvent"`, `SetLastError = true`, `CallConvStdcall`), with the pinned
      upstream anchors in its doc comment (`include/ndisapi.h:307`, `ndisapi.cpp:3586`, `.def:16`).
- [x] `NdisApiDriver.TryRegisterPacketEvent(nint adapterHandle, out INdisPacketArrivalSignal? signal,
      out int nativeError)` — control-gated (cold, mirrors `SetAdapterListChangeEvent:100-109`): create
      `EventWaitHandle(initialState: false, EventResetMode.AutoReset)`, hand its raw handle to the
      driver (`DangerousGetHandle`, with the same `#pragma warning disable S3869` rationale as
      `AdapterListWatcher.cs:44-46`), and return `new NdisPacketArrivalSignal(handle, release: () =>
      ReleasePacketEvent(adapterHandle))`. On native `FALSE` or `EntryPointNotFoundException`: dispose
      the event, return false with the native error (never throw — the event is an optimization).
      `ReleasePacketEvent` is best-effort (`SetPacketEvent(handle, adapterHandle, 0)`), swallowing a
      stale-handle failure like the mode restore.
- [x] `MultiAdapterCaptureLoop` (`:35-54`, `:99-107`): trailing optional
      `IReadOnlyList<INdisPacketArrivalSignal?>? arrivalSignals`, positionally paired with `bindings`
      (count mismatch ⇒ `ArgumentException`); each signal reaches the matching pump through
      `NdisCapturePumpOptions.PacketArrivalSignal`; `DisposeCoreAsync` disposes every signal **after**
      the pumps and before the scope drain. `DisposeAsync` stays single-flight and idempotent.
- [x] `NdisCaptureGenerationFactory.Create` (`NdisCaptureGeneration.cs:103-137`): create one signal per
      binding before the loop, dispose the already-created ones if the loop construction throws, then
      pass the list into the loop. A refused registration logs one rate-limited
      `capture.packetEvent.unavailable` warn (a `RuntimeLogThrottle` field like `_slotExhaustedWarn`,
      `:79`) carrying the adapter and the native error; sibling adapters keep their signals.
- [x] Facts: `MultiAdapterCaptureLoop` wiring — one signal per in-scope binding, the **same** instance
      reaching the pump (a pump that parks in it while disposed is joined), and every signal disposed
      exactly once on loop disposal. Build the loop the way `NdisCaptureResilienceTests.cs:200-208`
      does (pass configuration + `NoopExecutor` + `FlowBuilders.Slots`). If a direct
      "refused registration ⇒ sleep fallback" fact is not reachable without a real driver, pin it at
      the pump level (no signal ⇒ `WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep`, Step 3)
      and record the factory's log loop as a Windows-only residual — never as a passing fact.
- [x] Correct the stale spec rows while they are in front of you (notes D1/D5/D6; full list in
      `design.md` §11): `windows-ndisapi.md:278` (the counters that do not exist), `:403`, **`:414`**
      (the falsified "the wait is bounded by the loop's poll delay"), `:415`, **`:421`** (query
      failure, now non-success-path only), `:424-426`, **`:427`** (the null-entry contract change),
      **`:437-440`** (Tests Required gains `NdisApiReadShapeTests` and the new pump facts), `:98`, the
      new `SetPacketEvent` row, and **`async-lifetime.md:131-133`/`:358-368`** (the new
      loop-owned `IDisposable` list). One row at a time; each cites its proof.

Validation: full `WinForward.Core.Tests` green; `~NdisCaptureResilienceTests` and
`~CaptureLifecycleTests` green (the loop's disposal ordering is their subject);
`dotnet build WinForward.slnx -c Release` zero-warning.

Rollback: pass `arrivalSignals: null`, drop the import and
`TryRegisterPacketEvent`; the pump falls back to the Step-3 default and the behaviour is exactly the
sleep-poll shape.

---

## Step 5 — Evidence, spec, record

- [x] Finish `benchmarks/results/2026-10-01-pump-io-shape/README.md`: host/runtime header, base
      revision, every command line, the before/after tables (idle CPU, polls/waits per second, wake
      percentiles, read-shape counts), the exact-vs-series statement per row, the attribution rules
      (`design.md` §7), the **Windows-open-items table** from `design.md` §9 quoted verbatim —
      **row 1 (the partial-batch/short-count experiment) included, because the amended PRD makes it
      acceptance-relevant and its fallback is the query-first rollback** — and the residual list
      (what a seam-level proof does not establish, notes §9).
- [x] `class-totals.txt`: the four gate classes per-process runs, the refreshed totals string, the
      git hash and tree fingerprint (`hot-path.md:1295-1318` procedure), plus `NdisApiReadShapeTests`'
      own repetition proof (a structural class, not a fifth allocation-gate entry).
- [x] `gate-stability.txt`: the per-gate proof loop for the four allocation classes (20 runs each,
      unchanged totals/signature strings) **plus** the two new exact gates
      (`IdleWaitIterationsAllocateNoManagedBytes`, `DriverReadPathAllocatesNoManagedBytes`), and the
      injected-allocation discrimination re-proof for each (one `new byte[64]` per iteration inside
      the window, recorded failure, restored green — `hot-path.md:1053-1056`).
- [x] Refresh `hot-path.md:1295-1303`'s totals string and add both new gates to `:1034-1041`'s
      exact-window list; restate the F5 spec rows named in `design.md` §11 with their proofs.
- [x] Record the session in this file's checkboxes, mark the deviations, and cite the artifact from
      `prd.md`'s last acceptance bullet **before** archive (the operator owns the PRD edit).
- [x] Hand the Windows-open-items table to the `windows-real-nic` program as the follow-up row
      (`prd.md:66-68`).

Validation: every "exact" claim in the artifact README is re-derivable from a file in the directory;
every timing row carries `gated: false` and a named control; `rg -n "0.017798|877.6" benchmarks/results/2026-10-01-pump-io-shape/`
finds the quoted before-numbers, not a paraphrase.

---

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                     # zero-warning
dotnet test WinForward.slnx -c Release                                      # full suite green
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiReadShapeTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~CapturePumpReadCallTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCapturePumpTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiAbiTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCaptureResilienceTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~HotPathAllocationGateTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~WarmPathGateTests"
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx        # zero <Issue>

# The F5 idle/wake series — BEFORE (pre-change tree) and AFTER (landed tree), 3 runs each,
# one run per file then concatenated (the runner truncates --output per process).
mkdir -p benchmarks/results/2026-10-01-pump-io-shape
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario pump --quick --output /tmp/wf-f5-before-$r.jsonl
done
cat /tmp/wf-f5-before-{1,2,3}.jsonl > benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-before.jsonl
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario pump --quick --output /tmp/wf-f5-after-$r.jsonl
done
cat /tmp/wf-f5-after-{1,2,3}.jsonl > benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-after.jsonl

# No-regression companion: the pump's throughput rows (BDN short job, 3 runs, raw exports kept).
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --filter '*CapturePump*' --job short --artifacts /tmp/wf-f5-pump-bdn-$r
done
# copy each run's *-report.csv + *-report.md into benchmarks/results/2026-10-01-pump-io-shape/

# Must-not-move gates and series (each with its own captured output — see Step 0's note).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario udp --quick --output /tmp/wf-f5-udp.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario gc-soak --quick --output /tmp/wf-f5-gcsoak.jsonl
```

---

## Acceptance-criteria + spec-row → proof mapping

| PRD criterion / spec row | Where it is discharged | Kind |
|---|---|---|
| **AC1** read shape: one batched read per drain where the pre-change shape issued query+read; empty ⇒ 0 packets, no error; red-before recorded; the residual query only on the non-success path | Step 1 (`NdisApiReadShapeTests` written, red captured in `read-shape-counts.txt`) → Step 2 (green; after-counts recorded). The per-drain counts are exact under both ABI hypotheses; the *rate* half of amended requirement 2 (idle IOCTLs) is discharged by the `pump.idleEvent` series, not by this criterion | exact |
| **AC2** idle wake: one bounded event wait per idle iteration (not a sleep) + the `pumpIdleWake` re-run | Step 3's `IdleIterationsIssueExactlyOneBoundedWaitAndNoSleepPacing` (exact counts + exact timeout argument + the 5000× elapsed discrimination), `WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep`, the scenario's `pump.idleEvent`/`pump.idleWakeEvent` after-series vs `pump.idle`/`pump.idleWake` (with the park-confirmation contract) | exact + series |
| **AC3** idle poll gate and drain paths allocate 0 B | the two existing gates unchanged (no-signal path byte-identical) + `IdleWaitIterationsAllocateNoManagedBytes` (production signal) + `DriverReadPathAllocatesNoManagedBytes` (per-drain driver body) + `pump.idleEvent`'s own 0 B gate over real 100 ms timeouts | exact |
| **AC4** Windows items recorded with experiment/expected/fallback, requirement-1 fallback honoured | `design.md` §9 verbatim in the artifact README — **including the partial-batch row (requirement 7), whose fallback is now "the §3.5 guard handles it; the experiment only confirms which shape the hardware takes"** — plus Step 2's explicit rollback shape | artifact |
| **The unverified ABI cannot cause a false degradation** (the operator's guard decision) | Step 2b: `AMismatchHealsTheShapeInsteadOfThrowing`, `TheGuardFiresExactlyOnceAndLaterDrainsAreQueryFirst`, `TheGuardNeverFiresOnAConformingDriver`, `TheMismatchNeverReachesTheTransientRetryBudget`, `AGuardedProbeThatAlsoFailsThrowsTheReadErrorLikeToday`, `TheMismatchDiagnosticCarriesTheAdapterRequestedDepthAndError`; the red is the recorded degradation of the unguarded body | exact |
| **AC5** Release zero-warning, full suite green, format empty, inspectcode zero | the validation block, at every rollback point | exact |
| **AC6** benchmark data recorded and cited before archive | Step 5's README + `class-totals.txt` + `gate-stability.txt` + `read-shape-counts.txt` + the before/after jsonl | artifact |
| PRD requirement 4 (burst loop, batch size, fail-closed, single-pump degradation unchanged) | `CapturePumpReadCallTests.PumpMakesExactlyOneReadCallPerPollAndPerBatch` (unchanged), `NdisCaptureResilienceTests` (unchanged), `NdisApiAbiTests`' throw/clamp matrix (rewritten, not weakened) | exact |
| PRD requirement 5/6 (0 B, existing gates, F2/F3/F4 facts) | the full suite plus the named classes: the four allocation classes, `WarmPathGateTests`/`TcpRedirectWarmPathGateTests`/`UdpWarmPathGateTests`/`SelfTrafficWarmPathGateTests` (F2), `SweepAllocationGateTests` (F3), `FlowKeyShapeTests`/`PackedFlowKeyTests`/`PacketPathWalkCountTests` (F4), and the UDP/TCP/gc-soak scenarios in the validation block | exact |
| Spec `windows-ndisapi.md:403/414/415/421/424-427` (read shape, the falsified wait bound, gate granularity, the query-failure row, the error matrix, the null-entry row) | Step 4's spec edits + the read-shape facts + the null-hole fact | exact |
| Spec `windows-ndisapi.md:437-440` (Tests Required) | Step 4/5 edits citing `NdisApiReadShapeTests` and the new pump facts | exact |
| Spec `windows-ndisapi.md:98` (idle poll note) | Step 3/4 edits + the pump idle facts + the byte-identical `pump.idle` row | exact + series |
| Spec `windows-ndisapi.md:278` (stale send telemetry, notes D1) | Step 4 correction, with the `rg` evidence quoted | exact |
| Spec `async-lifetime.md:131-133/358-368` (per-owner notes: `NdisCapturePump` owns no CTS; `MultiAdapterCaptureLoop`'s owned state) | Step 4's edits + the loop wiring fact (signals disposed exactly once, after the pumps) | exact |
| New spec row: `SetPacketEvent` | Step 4's pinned import/wiring + `design.md` §4 | exact (ABI) + open item (behaviour) |
| Spec `hot-path.md:1034-1041` (exact-window class list) and `:1295-1303` (per-gate totals string) | Step 5's `gate-stability.txt` + `class-totals.txt` | exact |

---

## Artifacts

| Path | Content |
|---|---|
| `benchmarks/results/2026-10-01-pump-io-shape/README.md` | host/runtime header, base revision, commands, before/after tables, exact-vs-series and attribution rules, the Windows-open-items table, the residuals |
| `benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-before.jsonl` | 3 pre-change runs (`pump.idle`, `pump.idleWake`, `pump.idleWake.verdict`) |
| `benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-after.jsonl` | 3 landed runs (both new rows + the two comparators + the extended verdict) |
| `benchmarks/results/2026-10-01-pump-io-shape/read-shape-counts.txt` | per-drain native call sequence before/after, both recorded reds (the pre-change `[Query, Read]` counts and the unguarded mismatching-ABI degradation with its retry count), the guard's post-arming shape, the per-hypothesis empty classification, and the `ReadShapeMismatchCount` read-out |
| `benchmarks/results/2026-10-01-pump-io-shape/class-totals.txt` | per-class totals, hash, tree fingerprint, run counts |
| `benchmarks/results/2026-10-01-pump-io-shape/gate-stability.txt` | the per-gate proof loop + the injected-allocation discrimination for the new gate |
| `benchmarks/results/2026-10-01-pump-io-shape/pump-throughput-*.csv` | the `CapturePumpBenchmarks` no-regression companion (raw BDN exports) |

---

## Risky files

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.NdisApi/NdisApiDriver.cs` | The read-shape flip is fail-closed-critical: a wrong classification turns an idle adapter into a degraded one, or (worse) silently swallows a read error while the queue is non-empty. The gate-lease shape (one lease, leaf-free map) must not change. The §3.5 guard adds the opposite risk — a *missing* or mis-ordered guard point silently restores the false-degradation path | Steps 2/2b (rollback points A/B) |
| `src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs` (the sink wiring) | The guard's diagnostic only exists if the sink is set per generation; a missing or stale wiring loses the only evidence the Windows experiment reads, and the handle→adapter resolution must use the *current* generation's scope | Step 2b |
| `src/WinForward.NdisApi/NdisNativeCallStatus.cs` | The single classification site; a second copy of the matrix anywhere voids the "one site" property | Step 2 |
| `src/WinForward.NdisApi/NdisReadPacketCalls.cs` (new) | The seam's `out`-parameter contract and the moved unsafe request code; an error-capture ordering mistake reads a stale last-error. The request builder stays on the driver (three facts call it), so the new class must call `NdisApiDriver.BuildMultiRequest`/`MultiRequestByteCount` rather than growing a second copy of the layout | Step 1 (rollback point A) |
| `src/WinForward.NdisApi/NdisCapture.cs` | The idle path is inside the 0 B loop; a `WaitHandle` overload that allocates, a `TimeSpan` conversion, or a wait placed before the `_stopped` check each break a documented contract | Step 3 (rollback point C) |
| `src/WinForward.NdisApi/NdisPacketArrivalSignal.cs` (new) | Ownership: the pump must never dispose a borrowed signal; the release must run before the handle closes | Step 3 |
| `src/WinForward.NdisApi/NdisApiAbi.cs` | The new `SetPacketEvent` import: a wrong `EntryPoint`, `SetLastError` or calling convention is invisible until a Windows run | Step 4 |
| `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs` | Signal/pump index pairing and disposal ordering; a signal disposed while a pump still waits is a use-after-dispose | Step 4 (rollback point D) |
| `src/WinForward.Runtime/Capture/NdisCaptureGeneration.cs` | Per-generation creation/partial-failure disposal and the rate-limited warn; a leaked registration per refresh accumulates in the kernel | Step 4 |
| `benchmarks/…/Stability/PumpIdleWakeScenario.cs` | The acceptance instrument: rows that stop being comparable, or an event row whose park-confirmation check is dropped (turning a wake row into a hot-handoff series), silently void the series | Step 3 revert |
| `src/WinForward.NdisApi/NdisApiBatchedSendAbiTests`' subject (`BuildMultiRequest`) | Three facts call it (`NdisApiBatchedSendAbiTests.cs:36/60/79`); the read path must call the same helper, never a copy | Step 1 (rollback point A) |
| `tests/**` expectations | The classifier rewrite moves assertions; a mechanical rewrite can weaken one into a tautology — re-read each | per step |
| `hot-path.md` / `windows-ndisapi.md` rows | The totals string and the exact-window list are load-bearing for the proof procedure; a stale entry makes a future per-gate proof vacuous | Step 5 |

---

## Commit plan skeleton

```text
0  test(ndis): driver↔native read seam, read counters, the speculative read shape and
   its self-healing ABI-mismatch guard + the red-then-green facts (both reds captured
   in read-shape-counts.txt between the half-steps)                [Steps 1+2+2b, rollback A/B]
1  perf(capture): injectable packet-arrival signal; bounded event wait on the idle
   path + the wait/alloc facts and the pump.idleEvent / pump.idleWakeEvent rows     [Step 3, rollback C]
2  feat(capture): SetPacketEvent binding per adapter per generation; fail-open to
   sleep polling + the loop wiring facts and the spec rows                          [Step 4, rollback D]
3  docs(evidence): before/after series, class totals, gate stability, Windows open
   items, spec-row map for F5                                                       [Step 5]
```

**Steps 1, 2 and 2b are one commit** (review MINOR 11 + the operator's guard decision): Step 1 alone
would commit facts that are red by construction, and Step 2 alone would commit the *unguarded* read-first
shape — the exact tree whose false-degradation risk the guard removes. A commit whose own gates fail, or
that ships a known unmitigated failure mode, is not shippable; the F2/F3/F4 precedent is "every commit
leaves a green tree; the transient red readings are captured during the step". Both reds (the pre-change
`[Query, Read]` counts, and the unguarded degradation) are produced inside the merged step and recorded. Revertibility is unchanged: the
body flip and the seam are disjoint hunks, so Step 2's rollback (restore query-first, keep the seam and
the counters) and Step 1's rollback (drop the seam too) are both single-hunk operations on a merged
commit. Steps 3–5 stay separate commits, each green with its own facts.

---

## Deliberately out of scope

- The driver itself, and any change to the pinned ndisapi ABI.
- The batched-send telemetry (including repairing the stale `windows-ndisapi.md:278` row beyond the
  correction named in Step 4).
- The Windows-only measurement program's rows: they consume `design.md` §9's table; nothing here fakes
  a hardware number.
- Adaptive/backoff idle pacing, a config-file surface for `IdleWaitTimeout`, and the pump-owned
  stop-handle variant for instant disposal (`design.md` §12).
- The winmm `HighResolutionTimerScope` policy: it stays as it is; the idle wait no longer depends on
  the timer period when a signal is installed, which is a side effect to record, not a change to make.
- Any UDP/TCP/capture-processing behaviour: F5 changes two I/O shapes and adds one instrument.

---

## Review dispositions (independent review, 2026-10-01)

Verdict: **SUFFICIENT WITH REQUIRED CHANGES** — 1 BLOCKER, 5 MAJOR, 7 MINOR; the mechanism, the
fail-closed equivalence table and the auto-reset argument were accepted as sound. All 13 are
discharged above; the PRD was amended concurrently (requirement 2 → driver↔native seam + idle-IOCTL-rate
property; requirement 7 and AC-4 → the partial-batch row), and the amended text is what this plan now
targets.

| # | Sev | Finding | Disposition |
|---|---|---|---|
| 1 | BLOCKER | D13 was false and the planned `BuildMultiRequest` move broke three existing facts (`NdisApiBatchedSendAbiTests.cs:36/60/79`) while Step 0's sweep never grepped it | **Fixed and re-verified in code.** Notes D13 rewritten with the three callers; `BuildMultiRequest`/`MultiRequestByteCount` stay `internal static` on `NdisApiDriver` (visibility widened for `MultiRequestByteCount` and `MaxStackMultiRequestBytes`), the read-calls class calls them, nothing moves, and Step 0 now greps the builder. `design.md` §3.1, §12 (recorded as a withdrawn plan), `implement.md` Step 0/Step 1, risky files |
| 2 | MAJOR | No proof for a non-empty-but-smaller-than-request failed read; the new full-capacity request could degrade a working adapter | **Fixed.** Notes §2.5 item 1 + §9.1; `design.md` §2.4 (new short-count row), §6 risk 2b, §9 row 1 (acceptance-relevant, fallback = query-first rollback); `implement.md` Step 2 and AC4. Cannot be tested here — recorded as the requirement-7 experiment, never as a claim |
| 3 | MAJOR | `NdisReadDiagnostics` increment points undefined, which `design.md` §9.2's A-vs-B discrimination depends on | **Fixed.** `design.md` §3.3 pins each counter's exact site (notably `FailedReads` before classification and `EmptyReads` after it); `implement.md` Step 1 carries the same table inline; notes §4's seam table gains the row |
| 4 | MAJOR | `pump.idleWakeEvent` had no park confirmation, so its samples could be hot handoffs | **Fixed.** `design.md` §8 specifies a delegating decorator + a per-wake entry-count assertion before the park delay and the arm; `implement.md` Step 3 has it as its own checklist item and the row must fail rather than report a latency for a wake that did not happen |
| 5 | MAJOR | No byte gate over the new per-drain production read path | **Fixed.** `design.md` §3.4 + §5 define `NdisReadShapeTests.DriverReadPathAllocatesNoManagedBytes` with the full window contract; `implement.md` Step 1 writes it (it is green on the pre-change body, so it is not part of the red set) and Step 5 re-proves discrimination |
| 6 | MAJOR | §11's spec-row map was incomplete | **Fixed.** Added `windows-ndisapi.md:414` (the falsified wait bound), `:421`, `:427` (+ the null-hole fact), `:437-440`, and `async-lifetime.md:131-133/358-368`; each carries its proof in both files |
| 7 | MINOR | §4.2's stop-latency reasoning was wrong (the conclusion survives) | **Fixed.** Notes §5 and `design.md` §4.2 now state the two orderings: production cancels `_scope` at `CaptureLifecycle.cs:126` before awaiting the run at `:129`, so parked pumps wake concurrently (~one timeout); a direct loop disposal is N × timeout with no production caller. `design.md` §9 row 7 controls both |
| 8 | MINOR | The classifier split named only two helpers, leaving the non-empty-queue throw unowned | **Fixed.** `design.md` §2.3 names three helpers incl. `ThrowReadFailedOnNonEmptyQueue`, keeps both facts in `NdisApiAbiTests`, and notes the `requested` argument is now the full capacity (a stronger message assertion) |
| 9 | MINOR | The default's name/visibility were unpinned | **Fixed.** `private static readonly TimeSpan s_defaultIdleWaitTimeout` (`quality-guidelines.md:27`; `TimeSpan.FromMilliseconds` cannot be `const`) + an `internal` accessor or a fact-owned literal — `design.md` §4.1, `implement.md` Step 3 |
| 10 | MINOR | `MaxStackMultiRequestBytes`' only user moved out; the interface belonged beside its implementation | **Fixed.** The constant widens to `internal const` and no longer moves (disposition 1); `INdisPacketArrivalSignal` is declared in `NdisPacketArrivalSignal.cs` per `directory-structure.md:66`, not in `NdisCapture.cs` |
| 11 | MINOR | The commit plan contradicted itself (red facts vs green commits) | **Fixed.** Steps 1+2 are one commit; the red is captured between the two half-steps; §10 of `design.md` and the commit skeleton both say so, and hunk-level revertibility is argued |
| 12 | MINOR | "readsPerWake 1.0 → 2.0" compared two different counts | **Fixed.** `design.md` §2.5 and `implement.md` Step 3 state the accounting change (the proxy hid the empty read inside its parked call) and forbid the bare comparison |
| 13 | MINOR | The zero-timeout gate exercises only `WaitOne(0)` | **Fixed.** `design.md` §5 states that the xunit gate covers the fast path and that `pump.idleEvent`'s own 0 B throw covers the ~150 real 100 ms timeouts |

### Operator addition — the self-healing ABI-mismatch guard (2026-10-01, after the review)

| # | Decision | Disposition |
|---|---|---|
| ADD | The operator rejected "detect the short-count ABI on Windows later" and a pre-emptive query, and specified a **self-healing guard**: on `read failed + query > 0`, log once per pump (rate-limited, with adapter identity / requested / queued / native error), mark that pump query-first **sticky**, retry the drain immediately in the query-first shape instead of entering the retry budget; if the guarded probe also fails, today's path resumes unchanged | **Added as Step 2b, landed in the same commit as the read shape.** `design.md` §3.5 (state machine, sites, counters, diagnostic, sink, the "not a pre-emptive query" argument, the accepted false-positive cost, the rejected pump-mediated alternative), §1 row 5, §2.3/§2.4 (the `FALSE + queued > 0` row splits into first occurrence vs subsequent), §3.3 (the counter), §5 (the guard's allocation entries), §6 risk 11, §7 AC1 + a dedicated AC row, §9 row 1 (the fallback is now the guard), §10 row 2b, §11 (the `:425` split + a new self-heal spec row); `implement.md` Step 2b (facts + the recorded red) and the commit plan; notes §9. One deviation from the literal wording is recorded in §3.5: the sticky flag lives on the **driver, keyed by adapter handle**, not on the pump object, because the call shape is the driver's and the pump↔driver interface must not be widened (twelve implementations); the observable contract is identical because one pump drives one handle at a time |

**Reviewer notes folded into the plan where they change it**: `OpenOutput(null)`/`StabilityContext.Write`
writes nothing on disk without `--output`, so every series command now carries a path
(`implement.md` Step 0 and the validation block); the classified states match the real code and `n` may
now be larger than the query reported (the short-count row); `FromRawHandle(0)` never trips the DLL
resolver (already in Step 1, now also justified in notes §6); auto-reset's retained-signal argument
holds with the residual as the signal-loss experiment; and `PaceIdle`'s placement keeps drain-till-empty,
batch 32, degradation and exactly-once release intact, so `pump.idle` must not move.

**Open questions for the reviewer to attack first** (unchanged from the pre-review list, minus the ones
this round resolved): (1) the fail-closed equivalence table (`design.md` §2.4), now including the
short-count and null-hole rows — is the relocation still state-for-state equivalent? (2) the empty-queue
inference from the vendor samples (notes §2.3); (3) auto-reset vs the vendor samples (`design.md` §4.4);
(4) the 100 ms timeout as both the latency bound and the disposal bound (`design.md` §4.2); (5) the
extra empty read per idle→active transition (`design.md` §2.5) — is the attribution honest? (6) whether
the seam's fake can carry an acceptance claim at all (`design.md` §3.2), and now (7) whether the
merged Steps 1+2 commit really preserves Step 2's independent rollback at hunk granularity.

---

## Session record (2026-10-01, implementation)

All five steps ran in order; both recorded reds were captured between the half-steps and are in
`benchmarks/results/2026-10-01-pump-io-shape/read-shape-counts.txt`. Steps 1–5 are one working tree
session, not yet committed (the operator owns the commit and the format/inspectcode gates).
**Deviations from the letter of this plan, each with its reason:**

1. **Step 0's full-suite baseline was taken with targeted class filters, not `dotnet test WinForward.slnx -c Release`.**
   The operator's instruction for this session allows exactly one full-suite run, at the end. The
   pre-edit green proof is the four gate classes (40/40) plus a zero-warning Release build; the full
   suite ran once on the landed tree.
2. **No branch/worktree was created** (Step 0's `git status` item): the operator did not name one, and
   the task's artifact header records the base revision `56dd85f` /
   `6599093d8033ce2b624a0b110c4fb2a65aa41d5f` instead.
3. **Step 3's before-series is the archived 2026-09-29 series, not a run of a frozen Step-2 tree.**
   The two comparator rows are unchanged code paths (no signal ⇒ the identical `Thread.Sleep` branch)
   and are re-measured on the landed tree in the after-series, which is the direct non-movement proof;
   the two new rows did not exist before Step 3, so a pre-change run of them would be a different
   program. The README says this in its own words under "before-series provenance". No number is
   presented as a before/after pair it is not.
4. **`ANullHoleInTheBatchArrayThrowsOnADrainThatWouldOtherwiseBeEmpty` is part of the recorded red set**,
   contrary to Step 1's note that it is green on the instrumented pre-change body: on that body the
   query-first drain never builds the request on an empty queue, so the fact fails with
   `DllNotFoundException` instead of `ArgumentNullException` (recorded in `read-shape-counts.txt` §2).
5. **`ThrowReadFailedOnNonEmptyQueue` is called with the requested count of the call that actually
   failed.** `design.md` §2.3 says the message's `requested` becomes "the full capacity"; at both live
   throw sites (the sticky query-first read, and the guard's probe) the failing call requested
   `min(queued, capacity)`, and §2.3's own rationale is that the message must report what the driver was
   asked for. `NdisApiAbiTests.ReadFailureOnANonEmptyQueueThrowsTheReadErrorWithTheRequestedCapacity`
   drives the capacity form directly at the helper, and the shape fact asserts `requested 3` for the
   probe. No assertion was weakened.
6. **`WaitForDispatch`'s failure bound was corrected** in `PumpIdleWakeScenario.cs`: it compared
   `Stopwatch.GetTimestamp()` against `TimeSpan.Ticks`, which off Windows shortens the documented 10 s
   bound tenfold. The new park-entry helper needs a correct bound, and leaving the shared helper wrong
   would have made the new row's "a wake did not happen" failure fire at 0.1 s. The measured series is
   untouched; only the failure path's bound changed.
7. **`prd.md`'s last acceptance bullet is not edited** (the operator owns that file and this session was
   told not to edit it). The artifact it should cite is
   `benchmarks/results/2026-10-01-pump-io-shape/README.md`.
8. **The Windows-open-items table has no separate consumer program to hand off to** in this tree; it is
   recorded in the artifact README as the follow-up row the plan asks for.

**Spec rows updated**: `windows-ndisapi.md` (`TryReadPackets` signature, the two new seam signatures,
the falsified "bounded by the loop's poll delay" claim, gate granularity, the read-shape contract and
self-heal, the queue-failure row, the read error matrix, the null-entry row, the packet-event matrix
rows, the stale send-telemetry row, the perf note, Tests Required), `async-lifetime.md` (the pump's
borrowed signal, the loop's owned signal list), `hot-path.md` (the exact-window class list and the
per-gate totals string).

---

## Check round — independent verification (trellis-check, 2026-10-01)

Verdict: **the mechanism, the fail-closed equivalence table and the guard's semantics hold**; the
task's acceptance claims reproduce at the seam, both new exact gates discriminate, the per-gate proof
loop re-derives 140/140 on the frozen tree, and the full suite is green there. Three defects were
genuinely wrong and are fixed below; two artifact claims were unachievable as written and are
corrected. Nothing relaxed a gate, an assertion or a threshold.

### Fixes made by the check round

| # | Finding | Fix | Where |
|---|---|---|---|
| C1 | `ThrowReadFailedOnNonEmptyQueue` had **dropped the `queued {n}` field** from the fail-closed message. `design.md` §2.3 requires "keeping the message text" and §2.4 claims the subsequent-occurrence state is byte-identical to today's — neither was true on the one path an operator actually reads, and the queue depth (the fact that makes the state "non-empty") was lost. | Restored the field: `ThrowReadFailedOnNonEmptyQueue(queuedPacketCount, requestedCount, readError, adapterHandle)`. Both live throw sites now emit today's message verbatim, differing only in `requested` (which §2.3 mandates). Assertions strengthened, not weakened: `NdisApiAbiTests` + `queued 5`, `NdisApiReadShapeTests` + `queued 3`. | `NdisNativeCallStatus.cs`, `NdisApiDriver.cs`, `NdisApiAbiTests.cs`, `NdisApiReadShapeTests.cs`, `windows-ndisapi.md` |
| C2 | The `adapter.readShape.mismatch` warn used **one process-wide `RuntimeLogThrottle`**, so the first adapter's single arming suppressed every sibling's — making §9.1's acceptance-relevant expected observation ("exactly one line per adapter") unreachable on a multi-adapter host. | One throttle per adapter handle (`ConcurrentDictionary<nint, RuntimeLogThrottle>`): still rate-limited, now per adapter, so each adapter's single arming is logged. | `NdisCaptureGeneration.cs`, `design.md` §3.5, `windows-ndisapi.md` |
| C3 | `pump.idleWakeEvent.emptyReadsPerWake` divided the **whole run's** empty reads (the 500 warmup parks included) by the *measured* wake count: 1.1006 where the per-wake rate is 1.0002. The row's own note claims it counts "one extra seam-level read per wake". | The row now reports the measured-window delta (`reader.EmptyReads - emptyReadsAtStart` → 5,001 / 5,000 = 1.0002); the note states every counter covers the measured window. After-series regenerated with the documented command. | `PumpIdleWakeScenario.cs`, artifact README |
| C4 | §9 row 8's expected observation named a **`capture.packetEvent` success log the code never emits** (only `capture.packetEvent.unavailable` on refusal) — an unachievable expected observation in the acceptance-relevant Windows table. | Row 8 (design + README) now expects *no* unavailable warn for that adapter plus the observable arrival cadence (~1 timeout of waits/s instead of ~886 polls/s). | `design.md` §9, artifact README |
| C5 | `design.md` stated the 32-slot request as "16 + 8·32 = 272 bytes"; `MultiRequestByteCount(32)` is 16 + 8·31 = **264**. | Corrected in §2.1 and §5. | `design.md` |
| C6 | `design.md` §8 called a non-blocked→ready wake "impossible by construction". The decorator counts a `Wait` **entry** before delegating, so a thread preempted between the entry count and `WaitOne` can still consume a retained auto-reset token, and `signalDrivenReturns` cannot separate that from a real wake. | Reworded to the actual residual (README residual 8). | `design.md` §8, artifact README |
| C7 | Two product doc comments asserted the driver's signalling behaviour as **verified fact** ("the driver signals the event whenever…", "so nothing is lost"), which §9 rows 4–5 explicitly leave as Windows open items. | Reworded: the pinned header *documents* the behaviour; the signalling discipline and signal loss are named open items. The bare-name export verification is delimited to the export table only. | `NdisApiAbi.cs`, `NdisApiDriver.cs` |
| C8 | The cited hardware export dump was **unresolvable** (`08-27-fix-datapath-throughput/implement.md`; the task is archived under `.trellis/tasks/archive/2026-08/`). | Full path in both citations. | `design.md` §4.5, `research/implementation-notes.md` §2.2 |
| C9 | Every artifact identified the measured tree by `git write-tree`, which with the change uncommitted returns the **index** tree (= HEAD, `6599093d…`) and contains none of F5 — so the recorded provenance did not identify the tree the runs measured. | Artifacts carry a measured-surface content fingerprint `c9c40c81d1137ad8cc58456e904674bd902be0ff2c5ae5db70a1eac44c996cf8` and state why `write-tree` cannot serve as one. | all four artifacts, `gate-stability.txt`, `class-totals.txt`, `read-shape-counts.txt`, README |
| C10 | `gate-stability.txt`'s header listed `NdisApiReadShapeTests:14` inside the `totals=` string that `hot-path.md` and `class-totals.txt` define without it. | Header corrected; the check round's loop lists the classes it actually ran and says which two are excluded from the lump-signature string and why. | `gate-stability.txt` |
| C12 | A stray empty file named `0` (an accidental shell redirect, unreferenced by anything) sat in the task directory and would have been archived with it. | Removed. | `.trellis/tasks/10-01-pump-io-shape/` |
| C11 | `WaitForDispatch`'s bounding comment claimed a raw `TimeSpan.Ticks` comparison shortens the bound "10× on a 1 GHz clock"; the unit ratio makes it 100× at a 1 GHz frequency. | Replaced with the frequency-independent statement (the comparison measures in the wrong unit and shortens the bound by that ratio) — the fix itself (`Deadline`) is unchanged and matches the `GcSoakScenario.cs:295` idiom. | `PumpIdleWakeScenario.cs` |

### Commit-gate round — `dotnet format` diagnostics (2026-10-01, operator-run gate)

The operator ran `dotnet format --severity info --verify-no-changes` on the verified tree and reported
12 diagnostics, all inside this task's surface. All 12 are fixed; the check round did not run
`dotnet format` or `jb inspectcode` itself, per instruction. No gate, assertion or threshold was
weakened, and no test's meaning changed.

| Rule | Site(s) | Fix |
|---|---|---|
| `RCS1139` (error) ×2 | `NdisPacketArrivalSignal` ctor, `MultiAdapterCaptureLoop` ctor | Added the missing `<summary>` to each doc comment, stating the contract (handle ownership + release-before-dispose; one pump per binding with a one-to-one signal list). Existing `<param>` content kept verbatim. |
| `IDE0230` | `NdisCapturePumpIdleWaitTests.cs:108` | `new byte[] { 0x70 }` → `"p"u8.ToArray()` (same expected byte). |
| `IDE0042` ×2 | `NdisApiReadShapeTests.cs:384,397` | Deconstructed the scripted steps: `var (succeeded, queued, error) = …` / `var (succeeded, success, error) = …`; the tuple names live on the ternary's first branch. Same values, same returns. |
| `CA1859` | `NdisCaptureGeneration.cs:167` | **Narrowed** `RegisterArrivalSignals`'s parameter to `List<AdapterCaptureBinding>` rather than suppressing: it is a private helper whose only caller passes the local `List<>`, so `Count`/the indexer devirtualise — a real (if small) win on a cold path and in line with the repo's performance-first rule. The public ctor/`IReadOnlyList` boundaries are untouched. |
| `MA0042` | `NdisApiReadShapeTests.cs:295` | `cts.Cancel()` → `await cts.CancelAsync().ConfigureAwait(false)`; the handler lambda became `async` (no `return ValueTask.CompletedTask`). Cancellation is still complete before the handler returns, so the fact's `ThrowsAnyAsync<OperationCanceledException>` is unchanged. Added the repo's existing `// ReSharper disable once AccessToDisposedClosure` narrow suppression with the same rationale as `CapturePumpReadCallTests.cs:40`, since the closure now spans an `await`. |
| `MA0154` | `NdisApiReadShapeTests.cs:179` | `<c>stackalloc</c>` → `<see langword="stackalloc"/>`. |
| `MA0076` ×4 | `NdisCapturePumpIdleWaitTests.cs:40,60,144`, `MultiAdapterCaptureLoopArrivalSignalTests.cs:88` | Wrapped each assert message in `string.Create(CultureInfo.InvariantCulture, $"…")` (the repo's existing form) and added `using System.Globalization;` to both files. |

Re-run after all 12: `dotnet build WinForward.slnx -c Release` **0 warnings / 0 errors**; the six
affected classes 54/54 green; full suite **18 + 1,081 green, 0 failed**. The measured-surface
fingerprint moves to `eccd8a9c95721daa3cce1d23c9e8e747bfaac42ca90a34fccf752e6ad96e22b3` (updated again by the inspectcode round below).

### Commit-gate round — `jb inspectcode` findings (2026-10-01, operator-run gate)

`dotnet format` then passed with empty output. `jb inspectcode` reported 10 issues in this task's
surface; all 10 are fixed. As instructed, neither gate was run by the check agent, no test was
weakened, and nothing was suppressed.

| Rule | Site(s) | Fix |
|---|---|---|
| `DisposeOnUsingVariable` ×2 | `PumpIdleWakeScenario.cs:170,378` | **Ownership fixed, not suppressed.** The manual `var dispose = pump.DisposeAsync()` existed only to initiate the stop before awaiting the run (the pump's loop only exits once the stop flag is set); the `await using` scope then disposed a second time, which is what the inspection flags. The stop is now its own seam — `NdisCapturePump.RequestStopForTests()` (the `RunIterationForTests` pattern: sets exactly the flag `DisposeAsync` sets first, and nothing more) — so each event row reads `pump.RequestStopForTests(); await run;` and the `await using` scope remains the single owner of the release. Ordering is unchanged (stop initiated before the run is awaited, `reader.Stop()` still between them in the wake row), the exception-path safety net is the scope's disposal, and the reader/event handles are still declared before the pump so they outlive it. Net **-2 effective lines** in a file that had only 2 left under the 400-line convention (398 → 396). |
| `RedundantUsingDirective` | `MultiAdapterCaptureLoopArrivalSignalTests.cs:4` | Removed `using WinForward.Core;` (redundant inside `namespace WinForward.Core.Tests`, the same class of finding the F4 round cleared 7×). |
| `ReplaceWithFieldKeyword` | `NdisApiDriver.cs:47` | Applied C# 14 `field`: the property is now `get => Volatile.Read(ref field); set => Volatile.Write(ref field, value);` and the explicit backing field is gone. Visibility (implicitly private) and the `Volatile` access semantics are unchanged; nothing else referenced the field. |
| `InvalidXmlDocComment` ×6 | `MultiAdapterCaptureLoop.cs:48` | The six findings are the six parameters the constructor's doc comment did not document (adding the `<summary>` in the previous round made the comment valid enough for param-coverage validation; the sibling ctor fixed in the same round documents 2 of 2 and was clean). All seven parameters are now documented. The `<paramref name="bindings"/>` was kept: it was already present, unflagged, in the previous round, so it resolves. |
| (unchanged) | — | The two older rows' pre-existing `DisposeOnUsingVariable` suppressions (present at HEAD, before this task) were left exactly as they are; only the two rows this round flagged were changed. |

Re-run after all 10: `dotnet build -c Release` **0 warnings / 0 errors**; the five affected classes
41/41 green; the `pump` stability scenario **run end to end** to validate the new stop path — exit 0,
no deadlock, all five rows written, `pump.idleEvent` 150 waits / 150 polls with 0 B,
`pump.idleWakeEvent` 5,000 frame reads + 5,001 empty reads, 1.0002 park confirmations per wake and
5,000/5,000 signal-driven returns; full suite **18 + 1,081 green, 0 failed**. Measured-surface
fingerprint `eccd8a9c…` → `da99a967abddb3ccf9666812fb4dc168886e235a12879bd8d83a13d7a2591075`.

### Verdict per PRD acceptance criterion

| Criterion | Verdict | Evidence |
|---|---|---|
| AC1 read shape (exact) + red-before | **PASS** | `NdisApiReadShapeTests` 14/14; recorded red `Failed: 12, Passed: 2` on the instrumented pre-change body with the exact `[Query, Read]` order (12 of 14 facts red). `design.md` §2.4's table now holds **including the message text** after C1. |
| AC2 idle wake (exact + series) | **PASS** | `NdisCapturePumpIdleWaitTests` 5/5 (wait counts, timeout argument, 5000× no-sleep discrimination, parked disposal); after-series regenerated: 10 waits/s, ~31× CPU drop, 0 B, 1.0002 park confirmations/wake, 5,000/5,000 signal-driven. |
| AC3 allocation | **PASS** | Both existing idle gates unchanged and green; both new gates exact-zero, and the check round independently re-proved their discrimination (`Actual: 88000` each with an escaping `new byte[64]`). The `pump.idleEvent` row's own 0 B throw covers the real 100 ms timeouts. |
| AC4 Windows items recorded | **PASS (after C4)** | README §"Windows open items" carries all 9 rows with experiment/expected/fallback, partial-batch row included, and row 8's expected observation is now one the landed code can actually produce. |
| AC5 build/format/inspectcode | **PASS for the two gates this round owns** | `dotnet build -c Release` zero-warning, full suite green on the frozen tree (18 + 1081). `dotnet format` / `jb inspectcode` are the operator's gates and were not run by this round, as instructed. |
| AC6 benchmark data recorded and cited | **PASS (after C3/C9)** | All six artifacts present; every quoted number re-derived from the jsonl by this round; the after-series regenerated after C3; provenance now carries a fingerprint that identifies the measured tree. |
| Requirement 4/5/6 (burst loop, gates, F2/F3/F4 surface) | **PASS** | `CapturePumpReadCallTests` 20/20, `NdisCapturePumpTests` 20/20, `SweepAllocationGateTests` 20/20, `HotPathAllocationGateTests` 20/20 in their own processes; landed full-suite delta exactly +24 tests, nothing removed or relaxed. |

### Residual risks the operator must record before archive

1. **Full-suite host lumps are not eliminated.** The pre-change baseline worktree failed 1/20 runs (a
   sweep gate, 7,704 B) and the landed tree 3/20, all on the untouched
   `NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes` (432/432/1,840 B); an earlier run
   hit `CountingReaderIdleIterationsAllocateNoManagedBytes` (1,096 B). The two rates are not
   statistically separable at n = 20 (Fisher p ≈ 0.6), no value scales with the 1,000-iteration
   window, no run reported "never became allocation-stable", and the per-gate process runs are
   140/140 — so this is the family `hot-path.md` already records, not an F5 allocation. What remains
   open: the observed values 432/1,096/1,840/7,704 are **not** in hot-path.md's `signature=` predicate
   `^(168|5216|7384|7448)$`; the predicate was deliberately not widened, so a future loop stops on
   them and re-diagnoses. A deterministic full suite still needs the per-window control that
   `09-30-exact-gate-residual-lumps` priced and rejected.
2. **C2 is inspection-only on this host.** `NdisCaptureGenerationFactory`'s sink wiring needs a real
   driver (`TryRegisterPacketEvent` throws `DllNotFoundException`, which its `EntryPointNotFoundException`
   catch does not cover, on a host without the DLL, so `Create` cannot be driven here). The per-adapter
   throttle is verified by construction, not by a fact — the same residual the plan already recorded
   for the whole wiring loop.
3. **The park-confirmation is not airtight** (README residual 8): the entry count is taken before
   `WaitOne`, so a preempted thread can consume a retained token. The 1 ms park delay makes it
   negligible; the row's guarantee is "a park was entered and the return was signal-driven".
4. **The before-series is still the archived 2026-09-29 run, not a frozen pre-change tree**
   (deviation §3). The check round verified it byte-identical to the archived file, verified the
   before/after metadata match on runtime/OS/scenario/duration/quick, and verified the comparator rows'
   code is unchanged — so every number quoted against it is honest — but "`pump.idle` did not move" is
   proven by code inspection plus a 1.5 %/8 % cross-session reading, **not** by a same-session A/B. The
   claims this weakens are exactly those two readings; the ~31× CPU drop rests on a same-session A/B
   (`pump.idle` vs `pump.idleEvent` in one after-series) and is unaffected.
5. **The ABI remains unverified** — unchanged from §9: empty-queue semantics, short-count behaviour,
   the event's signalling discipline and signal loss, the 3-arg signature beyond the export table, and
   generation-stop latency are all open experiments, and the guard's false-positive cost is priced but
   unmeasured.
