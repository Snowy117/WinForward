# Allocation Gates: the exact window, its host, and how to prove it can fail

> What an exact zero-allocation gate is, how to write one that can fail, and the host contract that
> makes its reading meaningful. Part of the [hot-path family](./hot-path.md); read it when you write
> or touch an exact 0 B gate, add a warm entry with a cold branch, or change the gate host.

The failure *mode* of an otherwise correct gate is
[allocation-gate-host-lumps.md](./allocation-gate-host-lumps.md).

## Closure hoisting is per-call, not per-branch

- Roslyn hoists a captured lambda's closure display class to **method entry**
  (`IL_0000: newobj '<>c__DisplayClass…'`), so an entry that *contains* a capturing lambda pays its
  allocation on **every** call, even when the warm path returns before the lambda's branch. Measured
  184 B/op deterministically on a coordinator whose warm path never entered the new-flow branch.
- Fix: extract the capturing lambda into a separate cold helper method. The warm entry then contains
  no lambda and no display class is hoisted. This is a source-level guarantee — do not rely on escape
  analysis.
- The landed shape of that fix: `UdpProxyCoordinator.TrySendSpanAsync` is the only (and non-async)
  warm send entry — task 09-19-compat-api-cleanup removed the memory `TrySendAsync` — and the cold
  new-flow work is delegated to
  `ScheduleSessionSetup(FlowKey, ProxyTarget, long, MacAddress, UdpSessionSlot)`, which rents a
  pooled setup work item and enqueues it on the shared `SetupExecutor`. `UdpProxyCoordinator` is a
  `partial class` split across `UdpProxyCoordinator.cs` (admission/lifecycle),
  `UdpProxyCoordinator.Send.cs` (the span send bridge) and `UdpProxyCoordinator.Sweep.cs` (the
  sweep), each ≤400 effective lines.

```csharp
// Correct: the cold helper contains no capturing lambda at all — the work item is rented from the
// shared pool, its fields filled, and enqueued, so no display class is hoisted and the warm path
// measures 0 B.
private bool ScheduleSessionSetup(FlowKey flow, ProxyTarget target, long flowGeneration, MacAddress capturedClientMac, UdpSessionSlot slot)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var item = _setupExecutor.RentItem(_setupHandler);
    item._completion = completion;
    item._flow = flow;
    item._udp._target = target;
    item._udp._flowGeneration = flowGeneration;
    item._udp._clientMac = capturedClientMac;
    item._udp._slot = slot;
    item._cancellationToken = _scope.Token;
    if (!_setupExecutor.TryEnqueue(item))
    {
        completion.TrySetCanceled(item._cancellationToken);
        return false;
    }

    slot.Completion = completion.Task;
    return true;
}
```

## Gates must discriminate the exact seam

- History: the transport interface once carried both a memory `SendAsync` and a span `SendSpanAsync`;
  a fake that incremented one shared `_sends` counter for both could not detect a regression that
  reverted the caller to the memory overload (a self-fulfilling gate), so the gate counted them
  separately (`MemorySends`/`SpanSends`).
- Task 09-19-compat-api-cleanup deleted the memory overload and made
  `IUdpProxyTransport.SendSpanAsync` the only send seam, so the gate now measures the span path
  directly: it asserts `GC.GetAllocatedBytesForCurrentThread()` delta `== 0` across real dispatches
  **and** that the fake's `SpanSends` advanced by exactly the expected count. A future
  re-materialization on the send path (a new memory overload, `ToArray()`, or `new byte[]`) must make
  that 0-B assertion fail.

## An allocation gate must open only after its path is ready, and must verify it stayed on one thread

- `GC.GetAllocatedBytesForCurrentThread()` is a **per-thread** counter, so a gate over a window that
  spans `await`s is only meaningful when (a) the driven path is actually **ready** — it takes the
  direct/warm shape rather than a cold setup path — and (b) the reading thread did not change.
  `EstablishedUdpDatagramPathAllocatesNoManagedBytes` measures 64 dispatches across 64 `await`s; run
  alone it failed repeatedly (`Expected: 0, Actual: 600`, and later `3688`, `4328`, `5352`).
- **The cause was a not-yet-ready window, not thread migration** (corrected 2026-09-21 after the fix
  was measured; the earlier migration explanation was wrong). The managed thread id was constant
  across 40 instrumented 64-dispatch loops (8 runs × 5 loops) — no continuation migration ever
  occurred — and disabling tiered compilation/PGO did **not** remove the burst, so an early window
  riding the cold setup path is not a tiering event. (Reconciled 2026-09-30: tiering *does* add a
  separate one-time lump to the counter, which `TieredCompilation=0` removes on every shape measured
  since — see "Allocation-gate stability" below. Read the 2026-09-21 result as "readiness was the
  cause *of that burst*", not as "tiering cannot perturb a per-thread window".)
- What the measurements do show is coupling to readiness: the per-thread delta was non-zero **only in
  the first batch**, and every non-zero first batch coincided with a first-batch send-count shortfall
  (`SpanSends` delta of 17/40/67 where 64 was expected). While a session is not yet `Ready`,
  datagrams take the bounded drop-oldest setup queue and are sent later by the background flush
  pipeline, so an early window rides the cold setup path, which allocates once and leaks sends into
  the window. Once the path is ready the window is allocation-free and the counter is exact.
- So: **fix the window, not the threshold.** Open the measured window only after (1) a probe send
  proves the session admits directly — the counter advances by exactly one and
  `Diagnostics.PendingSetupBytes == 0` — and (2) an allocation-stable probe batch shows the exact
  0-byte reading (bounded retries; the landed gate allows 8). Keep the exact
  `Assert.Equal(0, allocated)`, assert the managed thread id did not change across the window, and
  always pair the byte assertion with the thread-independent call counter (`SpanSends`).
- Readiness is **not** a licence to relax the threshold: a real per-packet allocation never
  stabilizes, so the bounded loop fails rather than passes (injecting `new byte[1]` on the warm path
  makes the gate fail with "the UDP warm path never became allocation-stable").
- **A single-threaded `SynchronizationContext` is not a substitute for the readiness
  precondition.** This gate's chain awaits with `ConfigureAwait(false)` throughout
  (`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs`, the three await sites plus the send
  tails), so a context cannot capture the continuations at all. The thread-id assertion is kept as
  cheap insurance, since the counter is per-thread either way.

```csharp
// The reference window: prove the path is ready and allocation-stable first, then open a
// thread-checked, exactly-zero window and keep the call-count backstop.
Assert.True(await ProbeAdmitsDirectlyAsync(), "the gate relies on direct admission, not the setup queue");
Assert.True(await WaitForAllocationStableBatchAsync(), "the UDP warm path never became allocation-stable");

var threadId = Environment.CurrentManagedThreadId;
var before = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < count; index++)
{
    var pending = executor.ProxyAsync(packet, server, cancellationToken);
    Assert.True(pending.IsCompletedSuccessfully, "the gate relies on the synchronous fast path");
    await pending;
}
Assert.Equal(threadId, Environment.CurrentManagedThreadId);
Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
Assert.Equal(count, factory.Transport!.SpanSends - spanSendsBeforeMeasure);
```

## Allocation-gate stability: the tiering host contract, the gate shape, and the repeat-run proof

- **The per-thread counter is not sound on its own across a window.** In a host with tiered
  compilation the runtime performs one-time managed allocations on the calling thread as hot code is
  published (call counting and on-stack replacement both do it), and such a lump can land inside a
  window whose driven code allocates nothing. Measured on this host: a loop whose only work was
  reading the counter saw a single 7,336–7,360 B jump in 12/20 and 17/20 process runs, always in one
  batch at a random iteration; the dispatcher gate failed 2/20 runs with 1,880 B, and an instrumented
  run located the lump at iteration 65 of 256 with 8,008 B. One-time first-use costs are visible the
  same way and are absorbed by the warm-up (136 B for a fresh `NoInlining` method; 824 B for the
  dispatcher path's first batch).
- **The measurement environment is part of the gate.** `tests/Directory.Build.props` sets
  `<TieredCompilation>false</TieredCompilation>` for every test project (moved there from
  `WinForward.Core.Tests` when the 2026-10-01 test-project split spread the gates across four
  projects): every method is compiled by the optimizing JIT on first use, so no tiering event can
  land in a window, and the gates measure the optimized steady state production reaches after warm-up
  anyway. Evidence, 20 runs per arm with a class filter: OSR-only off
  (`TieredCompilationQuickJitForLoops=false`) 2/20 fails, a raised call-count threshold
  (`TC_CallCountThreshold=1000000`) 2/20, `TieredCompilation=0` 0/20, and
  `TieredCompilation=0 DOTNET_gcConcurrent=0` 0/20; the same toggle on the counter-read probe took
  17/20 and 12/20 baseline to 0/20.
- **The host change costs ~3 s per suite run.** `TieredCompilation=false` makes the test host compile
  every method with the optimizing JIT on first use, so the suite went from ~7 s to ~10.5 s per
  full-suite run (+50 %). That is the recorded price of determinism; it buys gates whose failure means
  a product allocation, and it is not a regression to rediscover.
- **Gate the tick shape the site actually repeats.** A retiring tick whose teardown `await`s disposal
  outside the gate cannot be byte-exact inside the window (the disposal legitimately allocates and
  suspends), so the two async sweep legs are gated on a **no-op tick over a populated world** —
  `TcpRedirectSessionStoreSweepAllocatesNoManagedBytes` (64 registered `Redirecting` sessions plus
  unexpired tombstones) and `UdpProxyCoordinatorSweepAllocatesNoManagedBytes` (16 fake-transport
  sessions). The population is the proof that the window is not vacuous: an empty store would green a
  "skip the scan when empty" fast path. The synchronous tables are gated on their real retiring tick
  instead. A gate whose driven call can strand a lease must release it **before** asserting — a
  failing assert that skips the release hangs the owner's drain instead of failing the test.
- **The landed shape extends to every exact window in the suite**, not only the two that flaked:
  `CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes`,
  `NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes`,
  `NdisCapturePumpIdleWaitTests.IdleWaitIterationsAllocateNoManagedBytes` (the production
  `NdisPacketArrivalSignal.Wait` entry point over an unsignaled event, timeout zero),
  `NdisApiReadShapeTests.DriverReadPathAllocatesNoManagedBytes` (the driver's per-drain read path
  through `CreateForTests`),
  `FlowAttributionPipelineTests.PendingAdmitAllocatesOnlyTheDocumentedColdBudget` (the
  deferred-attribution admission: the cold budget is one entry object + one 32-slot ring + the first
  retained slot, and every later packet of an already-pending flow is an exactly-zero ring append),
  `CompositePacketArrivalSignalTests.CompositeArrivalWaitAllocatesNoManagedBytes` (the production
  two-handle park over the borrowed driver signal and the pipeline-owned event, both unsignaled at a
  zero timeout) and `SweepAllocationGateTests.FlowTableSweepAllocatesNoManagedBytes` now open their
  windows only after a bounded run of probe batches that each read an **exactly-zero** delta on an
  unchanged thread. The probe batches are part of the contract, not decoration: the same host lump
  that failed the dispatcher gate was caught in the pump gate (168 B) and in the sweep gate (7,384 B)
  during full-suite runs. Classifying such a failure needs
  [allocation-gate-host-lumps.md](./allocation-gate-host-lumps.md).
- **Harness assertions inside a measured region are pollution.** `Assert.Equal` allocates (measured
  ~200–300 B per call; a sweep gate's probe loop that asserted inside its own window reported "never
  became allocation-stable" on *every* batch until the assertion moved out), while
  `Assert.True(condition, message)` does not (the UDP gate's measured loop has carried it at 0 B for
  100 runs). Keep every assertion outside the window, or use the boolean form.
- **Every gate states its window contract in code**: assert the driven operation completed
  synchronously (`IsCompletedSuccessfully`) so no continuation can migrate, capture
  `Environment.CurrentManagedThreadId` before the window and assert it unchanged after, keep the exact
  `Assert.Equal(0, allocated)`, and keep the thread-independent call-count backstop.
  `DispatcherWarmFastPathAllocatesNoManagedBytes` and
  `ReverseRewriteAndInjectAllocatesNoManagedBytes` carry all four; the UDP gate's landed
  readiness/stability preflight plus the same four is the reference shape.
- **The gate's discrimination must be re-proven after any change to its window.** Inject one
  allocation inside the measured region and record the exact failure before restoring: on 2026-09-30 a
  single `new byte[64]` per iteration failed the reverse gate with `Actual: 5632` (64 × 88 B) and the
  dispatcher gate with `Actual: 22528` (256 × 88 B), and both were green again after restoring.
  **Keep the injected allocation alive** (`GC.KeepAlive(new byte[64])`, or a use the optimizer cannot
  sink): a bare `_ = new byte[64]` is dead and was measured *eliminated* on .NET 10.0.401 — two runs
  of `UdpAdaptiveSweepAllocationGateTests` passed with the bare form and failed at `Actual: 88` with
  the `GC.KeepAlive` form, and the pump gate read `Actual: 88000` (1,000 × 88 B). A probe the JIT
  removes proves nothing about the window.
- The repeat-run procedure that proves a stability claim, the residual host family that no host
  setting removes, and the forbidden tolerant gate shapes are
  [allocation-gate-host-lumps.md](./allocation-gate-host-lumps.md). The two procedures are not
  interchangeable: the loop proves a product/allocation-regression claim, the per-gate process run
  classifies a host event.

## The capture refresh machinery is off the packet path

`LayeredCaptureRunner`'s monitor thread, its periodic tick, and the extracted `CaptureRefreshWorkers`
type allocate (a `Run` child's delegate + state machine, one OS thread) per *generation or tick*,
never per packet. The capture pump's own zero-allocation gate
(`NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes`) is unaffected.

## Approved cold-path materialization

`Socks5UdpTransport.SendSpanAsync` copies with `payload.ToArray()` only on the contended-gate branch:
the span views native capture memory that recycles once dispatch returns, so it cannot cross the gate
`await`. This is a documented cold-path exemption; the warm uncontended shape stays zero-alloc.

## Outcomes a gate author must be able to predict

| Condition | Required result |
|---|---|
| Warm entry contains a capturing lambda anywhere in its body | the 0 B gate fails by design; extract the lambda to a cold helper |
| A materializing overload or copy is re-added on the send seam | the 0 B gate fails (`SpanSends` count and/or allocation delta) |
| A gate's measured window opens before the driven path is ready, or spans `await`s that resume on another thread | the per-thread delta is invalid — prove direct admission and allocation stability first, then assert the thread is unchanged and keep the exact zero |
| A gate's readiness/stable-warm-up phase is implemented as a relaxed threshold | forbidden — the stabilization loop must require an exactly-0 delta, so a genuine per-call allocation makes it fail instead of pass |
| An exact gate runs in a host with tiered compilation enabled | report-only: a tiering lump can fail a gate at random; disable tiering for the gate host |
| A gate window opens without the synchronous-completion assertion | forbidden — an incomplete `ValueTask` can resume on another thread and invalidate the per-thread reading |
| The measured thread id changed across the window | the gate fails; the reading is invalid, not the path |
| A stability fix relaxes the byte assertion or skips an iteration | forbidden — the window keeps its exact zero and its call-count backstop |
| Span reaches `Socks5UdpTransport` with the send gate uncontended | zero-alloc sync `SendTo` |
| Span reaches `Socks5UdpTransport` with the gate contended | `payload.ToArray()` cold copy (documented exemption) |

## Tests required

- `HotPathAllocationGateTests`: `MidFlowRewriteAndInjectAllocatesNoManagedBytes`,
  `ReverseRewriteAndInjectAllocatesNoManagedBytes` and
  `EstablishedUdpDatagramPathAllocatesNoManagedBytes` — the UDP gate asserts 0 allocated bytes and
  that the fake's `SpanSends` advances by exactly the expected count after the measurement (span is
  the only send seam).
- Every allocation gate must be verifiable in isolation, not only inside the full suite: run it with
  `dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~<GateName>"`. A gate that only
  passes when the pool is warm is a weak gate — fix it before trusting it to protect a path you are
  about to change. The baseline for a gate's own run is the total that run reports, never a literal
  remembered in the spec.
