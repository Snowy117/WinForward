# Test Stability: publication order, real synchronization points, and process-wide state

> How the suite's concurrent tests must be written so that a failure means the product is wrong,
> not that the host scheduled something unluckily. Derived from the 2026-10-06 flaky sweep, which
> reproduced nine failing signatures across 44 loaded full-suite runs and fixed every one that was
> a property of the tests rather than of the host.

---

## 1. Scope / Trigger

- **Trigger**: a fact whose assertion depends on another thread's progress (a setup worker, a capture
  runner, a sweeper tick, a scheduler callback) or on process-wide state (a `RuntimeCounters.Shared`
  counter, `AdapterSlotTable`, `TaskScheduler.UnobservedTaskException`, the console).
- **Also applies** to the shared fakes and harnesses under `tests/WinForward.TestSupport/`: a fake
  that publishes state from a product thread is part of the contract, not scaffolding.
- **Not in scope**: the residual exact-gate allocation lump, which is a host property of this
  machine — see `hot-path.md` §"The residual exact-gate lump".

## 2. Contracts

### 2.1 A fake's collection is read through the same lock that writes it

`List<T>.Add` stores `_size` before the element, so an unlocked `Count`-then-index read can observe a
count that includes a slot whose element is still `null` — `Assert.Single` then returns `null` and the
following member access throws `NullReferenceException`. Every collection that a product thread
appends to must be exposed as a snapshot property taken under the writer's lock:

```csharp
// Wrong: the setup worker appends from its own thread; the reader's Count-then-index pair is not atomic.
public List<FakeTransport> Transports { get; } = [];
...
lock (Transports) Transports.Add(transport);          // writer locks...
await WaitForAsync(() => factory.Transports.Count == 1);
var transport = Assert.Single(factory.Transports);     // ...reader does not -> possible null

// Correct: one lock, one consistent snapshot (UdpTransportFakes.RecordingTransportFactory shape).
private readonly Lock _gate = new();
private readonly List<FakeTransport> _transports = [];
public IReadOnlyList<FakeTransport> Transports { get { lock (_gate) return [.. _transports]; } }
```

Landed on `FakeTransportFactory`, `ImmediateFaultTransportFactory`, `GatedTransportFactory`,
`DelayedTransportFactory`, `CollidingAliasTransportFactory` and `UdpSessionRetentionTests`'
`ExchangeTransportFactory`; the `List<T>`-only members (`Exists`, `TrueForAll`) at their call sites
became `Any`/`All` over the snapshot. `FakeTransport.Sent` already locked on both sides and is the
other accepted shape.

### 2.2 Synchronize on the state you assert, not on a proxy for it

`CaptureRunnerHarness.WaitForGenerationStartedAsync` waited only for `FakeCaptureGeneration.Started`,
which is latched at the top of the generation's run — **before** `InstallGenerationAsync` publishes
the scope through the composed `onScopeInstalled` callback. Facts asserting on `InstalledScopes` or on
the recorded event timeline could therefore read a state whose writes had not happened yet
(`Expected 2 / Actual 1`, or a timeline missing its last two entries). The harness now publishes an
install counter **after** the composed callback returns and `WaitForGenerationStartedAsync` waits for
both:

```csharp
await AsyncTestExtensions.WaitForAsync(() => Generations.Generations.Count > index
    && Generations.Generations[index].Started.Task.IsCompleted).ConfigureAwait(false);
await AsyncTestExtensions.WaitForAsync(() => Volatile.Read(ref _installedScopes) > index).ConfigureAwait(false);
```

Rule: when a fact needs "generation N is installed", wait for the install, not for a latch that fires
earlier in the same pipeline.

### 2.3 Poll monotonic state monotonically

`Generations.Generations.Count` only ever grows, so `WaitForAsync(() => Count == 3)` samples a
*transient* value: the storm guard (`minimumRefreshInterval`) spaces consecutive generations, and a
continuation starved past that interval observes the count after it already moved on — the predicate
is then never true again and the fact dies on the 10 s budget instead of on its assertion. Use the
monotonic form:

```csharp
await AsyncTestExtensions.WaitForAsync(() => harness.Generations.Generations.Count >= 3);
```

### 2.4 A scripted fault is authoritative over a racing cancellation

`Task.WaitAsync(ct)` completes when either the source completes **or** the token fires, and the token's
callback can win the internal race even when the source completed first. A fake that holds a scripted
fault behind a release gate must therefore treat the release as decisive and only let cancellation
win while the release is still pending:

```csharp
try
{
    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
}
catch (OperationCanceledException) when (!release.Task.IsCompleted)
{
    throw;                       // the fault never landed: this really is a cancellation
}

throw startupFault;              // the release landed: the fault is the outcome, whoever ran first
```

Without the filter the fact's subject ("the refresh stop absorbs the startup fault") becomes a
function of callback ordering: measured on 2026-10-06, the unguarded form failed **7 times in 32
loaded suite runs** once the interleaving was staged.

### 2.5 Stage the interleaving, do not hope for it

A fact that pins a race must force the interleaving it asserts. The original
`RefreshDemandRacingAStartupStaleHandleFaultIsAbsorbedIntoARebuild` signalled a refresh demand and
released the fault on the next statement, leaving the winner to the scheduler — the only thing pacing
the runner was the 50 ms wall-clock storm guard, so a test thread starved past it lost the race and
the fact failed with "no absorbed startup fault". It now parks the runner deterministically:

```csharp
harness.Enumeration.NextEnumerationGate = refreshRead;   // consumed inside ProcessRefreshDemandAsync's Enumerate()
harness.Runner.SignalDegraded(...);
try { await WaitForAsync(() => harness.Enumeration.EnumerationCount >= 2, timeoutMs: 5000); }
finally { release.TrySetResult(); refreshRead.TrySetResult(); }   // both released even on timeout
```

The runner is therefore parked *after* the demand was consumed and *before* the outgoing generation is
stopped; the fault lands inside that window, so the stop always awaits an already-faulted generation.
Releasing both gates from `finally` is part of the shape: a poll that times out must not strand the
parked read and hang the harness's disposal.

### 2.6 Never re-read a field the tested code clears

`SetupExecutorRejectsBeyondRingCapacityAndRecyclesTheRejectedItem` released a worker's gate and then
re-read `blocker._completion` — a field the worker nulls through `Reset()` in its `finally` on the way
out. Measured as a `NullReferenceException` on that line in a loaded full-suite run. Capture the
reference while the code under test is still parked and await the captured one:

```csharp
var blockerCompletion = blocker._completion!;   // only readable while the gate is closed
gate.TrySetResult();
await blockerCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
```

### 2.7 A test class owns the interning identities it asserts on

`AdapterSlotTable` lives for the whole test process and `TryIntern` **republishes** the friendly name
on every call, so two classes running in parallel that intern the same stable id under different names
race on what the slot resolves to. `FlowContextMetadataTests` asserted `AdapterName == "Ethernet"`
against a slot that a sibling class republished as `"id-a"` (measured once in 20 loaded runs). A class
that asserts on resolved metadata uses a class-owned stable id:

```csharp
private const string AdapterStableId = "flow-context-metadata-adapter";
```

A shared well-known id (`"id-a"`) stays fine for classes that assert only on the slot's identity.

### 2.8 A process-wide counter needs a collection

xUnit parallelises across collections, so every fact whose assertion is an exact — or zero — delta on
a process-wide counter must share one collection with **every** class that can move that counter.
A class can join exactly one collection, so when a class moves two counters the counters share a
collection:

| Counter | Collection | Members |
|---|---|---|
| `udpLocalTargetFailures`, `udpResponseSourceMismatch` | `udp-process-counters` | `LocalUdpTransportTests` (writes both), `LocalUdpTransportRetentionTests`, `UdpResponseSourceMismatchTests` |
| `udpCapacityRejections` | `udp-capacity-rejections` | `UdpProxyCoordinatorTests` (exact delta), `UdpProxyCoordinatorLifecycleTests` (drives a capacity-1 coordinator to refusal while polling) |
| `udpAssociationLost` | `udp-association-lost` | unchanged |

### 2.9 Budgets: aligned, bounded, and never wall-clock

- **Fixed `WaitAsync` budgets align with `AsyncTestExtensions.WaitForAsync`'s 10 s default.** The
  suite's own comment records that a loaded host can starve queued continuations for *seconds*; a 2 s
  or 5 s budget turns that into a false failure. Aligned: `UdpSetupQueueBudgetTests`,
  `UdpProxySessionTests`, `UdpProxyCoordinatorLifecycleTests` (`WaitUntilTrueAsync` now polls 1 000 ×
  10 ms), the `NdisCaptureResilienceTests` degradation waits, `AdapterListWatcherTests`.
- **No unbounded waits.** `WaitAsync(CancellationToken.None)` / `ReadAsync(CancellationToken.None)`
  turns a product defect into a hung test host (this repository has already lost 41 minutes to one
  hang). Bounded: the receive waits in `UdpReceiveResilienceTests`, the sweep steps in
  `UdpProxyCoordinatorLifecycleTests.ExpirySnapshotDoesNotDisposeSessionWhoseReceiveRefreshesActivity`.
- **Dispatch contracts are state, not duration.** `FirstDatagramDoesNotAwaitAStalledSetupAndDatagramsRelayInFifoOrder`
  asserted `stopwatch.Elapsed < 3 s` to prove the dispatcher did not await the setup; that gates the
  host's scheduling. It now asserts the state that proves the same thing — the stall gate is still
  closed, so no transport can exist yet — whatever the host did to the test thread.
- **Publish before the count.** `LoopbackUdpResponder` incremented `ReceivedCount` before writing
  `LastPayload`, so a fact that waited for the count could still read a null payload; the payload is
  written first.
- **Subscribe after the target exists.** `UnobservedExceptionProbe` counts any unobserved fault while
  its target is unset (by design), so the probe must be `Track`ed *before*
  `TaskScheduler.UnobservedTaskException += ...`.

## 3. Validation & Error Matrix

| Condition | Required result |
|---|---|
| A fake appends from a product thread and exposes a raw `List<T>` | forbidden — expose a locked snapshot (or publish the element before the count) |
| A fact reads a field the code under test clears on its way out | forbidden — capture the reference, or assert the state instead of the field |
| A `== N` poll on a monotonically growing count | forbidden — use `>= N` |
| A scripted fault races a cancellation on `Task.WaitAsync` | the release decides: the fault is thrown whenever the release completed |
| A fact pins an interleaving without a gate on the losing side | forbidden — park the winner with a gate the fact releases |
| Two classes intern the same stable id and one asserts on its metadata | forbidden — the asserting class owns its id |
| A process-wide counter asserted as an exact delta | the asserting class and every writer share one collection |
| A fixed wait budget shorter than the suite's starvation allowance (~10 s) | forbidden — align with `WaitForAsync`'s default |
| `WaitAsync`/`ReadAsync` with `CancellationToken.None` on a product-controlled path | forbidden — bound it, so a defect fails instead of hanging |
| A `Stopwatch` bound asserted as a dispatch contract | forbidden — assert the state that proves the contract |
| A released gate re-read afterwards, or a gate not released on the timeout path | forbidden — capture before release, release from `finally` |

## 4. Tests Required — the repeat-run proof

Fixes of this class cannot be proven by one green run. The accepted evidence is a **loaded** soak:
two `dotnet test WinForward.slnx -c Release` streams running concurrently (which is also the condition
that reproduced every signature), plus a focused high-frequency stream over the affected class, with
every round's padded summary and every failure's assertion text recorded. A failure anywhere is
diagnosed, not averaged away.

Recorded on the 2026-10-06 sweep (`master` + the fix set). "Before" is the discovery phase — 20 serial
baseline rounds plus 24 concurrently loaded rounds, 44 loaded runs; "after" is 48 loaded full-suite
runs (2 concurrent streams) plus the focused streams named per row. Every round's padded summary and
every failure's assertion text are recorded under `/tmp/wf-*` for the session.

| Signature | Before (44 loaded runs) | After |
|---|---:|---|
| `SetupExecutorTests.SetupExecutorRejectsBeyondRingCapacityAndRecyclesTheRejectedItem` (NRE) | 7 | **0** (48 loaded runs) |
| `FlowContextMetadataTests.ClaimedFlowCarriesTheInternedAdapterAndProcessMetadata` | 1 | **0** (48 loaded runs) |
| `LayeredCaptureRunnerRefreshTests.RefreshDemandRacingAStartupStaleHandleFaultIsAbsorbedIntoARebuild` | 1 | **0** (24 loaded runs + 42 whole-project Capture.Tests runs); the staged interleaving first exposed a wrong-sign filter in the fake, which read **7 in 32** runs until corrected |
| `Socks5ControlConnectionDeferredHandshakeTests.DisposeJoinsAParkedCompletion` (surfaced by the soak) | 2 in 24 | **0** (24 loaded runs + 52 whole-project Socks5.Tests runs) |
| `SweepAllocationGateTests.*` — host residual, **not** a test property | 3 | 7 in ~125 loaded runs (≈6 %): recorded in `hot-path.md` §6, gates unchanged |

Gate results on the frozen tree: Release build **0 warnings**, `dotnet format --severity info
--verify-no-changes` **exit 0 with empty output**, `jb inspectcode` **0 `<Issue>` entries**.

## 5. Wrong vs Correct

```csharp
// Wrong: a fence that is not a fence. The assertion is about a state that another thread publishes
// later in the same pipeline, and the poll can observe its proxy before the state exists.
await harness.WaitForGenerationStartedAsync(1);
Assert.Equal(2, harness.InstalledScopes.Count);          // Expected 2 / Actual 1

// Correct: wait for the state the assertion reads.
await harness.WaitForGenerationStartedAsync(1);          // now also waits for the scope install
Assert.Equal(2, harness.InstalledScopes.Count);
```

```csharp
// Wrong: a race staged by statement order, paced only by a wall-clock guard.
harness.Runner.SignalDegraded(adapter, 87);
release.TrySetResult();                                  // whoever the host schedules first wins

// Correct: the loser is parked on a gate the fact owns, so the winner always runs first.
harness.Enumeration.NextEnumerationGate = refreshRead;
harness.Runner.SignalDegraded(adapter, 87);
try { await WaitForAsync(() => harness.Enumeration.EnumerationCount >= 2, timeoutMs: 5000); }
finally { release.TrySetResult(); refreshRead.TrySetResult(); }
```
