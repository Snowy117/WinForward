# Research: existing tests (C4 owners)

- **Query**: which tests currently cover the C4 owners, what each pins, what changes when a mechanism changes, where new quiescence tests belong, and the full-suite baseline.
- **Scope**: internal. Class→file confirmed with `rg`.
- **Baseline**: `dotnet test WinForward.slnx -c Release` → exit 0 (2026-09-21, HEAD `ce70098`).

## Baseline (exact output lines)

```
Passed!  - Failed:     0, Passed:    18, Skipped:     0, Total:    18, Duration: 4 s - WinForward.Analyzers.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   773, Skipped:     0, Total:   773, Duration: 7 s - WinForward.Core.Tests.dll (net10.0)
```

Total 791. This matches the C4 PRD's expected `WinForward.Core.Tests` 773 + `WinForward.Analyzers.Tests` 18.

---

## Test files by owner

### 1. `LayeredCaptureRunner` — `tests/WinForward.Core.Tests/LayeredCaptureRunnerTests.cs`

`public sealed class LayeredCaptureRunnerTests` at `:8` (9 test methods).

| Test | Pins |
|------|------|
| `StartupScopeResolutionFailurePropagatesFailClosed` | startup failure propagates; durable disposal still runs |
| `StartupEmptyEnumerationFailsClosed` | empty enumeration fails closed |
| `UserCancelDisposesGenerationBeforeDurableDisposal` | **disposal ordering**: generation disposed before `_disposeDurableAsync` |
| `GenerationFaultIsFailClosedAndStillDisposesDurable` | generation fault → fail closed + durable dispose |
| `NaturalGenerationEndStopsTheRunAndDisposesGenerationBeforeDurable` | natural end ordering |
| `DegradedError87WithUnchangedEnumerationLogsNoChangeWithoutRebuild` | `SignalDegraded` no-op path |
| `DegradedError87WithVanishedAdapterReportsNotPresentAndRebuilds` | rebuild path |
| `SignalWithIdenticalEnumerationIsLoggedNoOpAndKeepsGenerationRunning` | no-op rebuild avoidance |
| `PumpStateSnapshotReflectsTheCurrentGenerationAndClearsAtExit` | `_runTask`/generation publication and clear |

Related: `LayeredCaptureRunnerPeriodicRefreshTests.cs` (`:6`, 5 methods) — `AddressFingerprintChangeAloneRebuildsThroughTheRefreshPipeline`, `PeriodicTickWithoutChangesStaysNoOpWithoutGenerationChurn`, `PeriodicTickWithChangesRebuildsExactlyOnceDespitePendingTicks`, **`ZeroPeriodicIntervalDisablesTheTickEntirely`**, **`NegativePeriodicIntervalIsRejected`** (the last two pin the `_periodicRefreshInterval > TimeSpan.Zero` guard at `LayeredCaptureRunner.cs:148` that any `scope.Run` migration must preserve).
`LayeredCaptureRunnerHealthSignalTests.cs` (`:14`, 2 methods) — `FailureThresholdForcesARefreshDespiteIdenticalEnumeration`, `ReportsBelowTheThresholdNeverRaiseADemand`.

**Mechanism at risk**: the monitor (`Task.Factory.StartNew(..., LongRunning)` `:144-146`) and periodic (`Task.Run` `:148-150`) spawns, plus `_runTask` (`:63`). These tests assert *behavior* (ordering, rebuild counts, guard), not the spawn mechanism, so a correct migration should keep them green; `ZeroPeriodicIntervalDisablesTheTickEntirely` forces the non-positive-period guard to survive outside any `scope.Run`.

### 2. `MultiAdapterCaptureLoop` / degradation — `tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs`

`public sealed class NdisCaptureResilienceTests` at `:19` (6 methods) — `TransientReadFailureIsRetriedAndRunContinues`, `HealthyReadSeparatesTransientIncidents`, `ExhaustedTransientRetriesDegradeWithoutThrowing`, `PermanentReadFailureDegradesImmediately`, `RetryCallbackObservesAttempts`, `CancellationDuringBackoffUnwinds`.

`public sealed class CaptureDegradationPlumbingTests` at `:181` (4 methods) — `DegradedPumpKeepsSiblingsRunningAndForwardsCallback` (**directly pins `_ = ForwardDegradationAsync`** behavior: siblings keep running and the callback is forwarded), `MarkAdapterDegradedRestoresOnlyTheDegradedAdapterOnce`, `MarkAdapterDegradedSwallowsRestoreFailure`, `MarkUnknownAdapterIsANoOp`.

**Mechanism at risk**: `WF0001` removal at `MultiAdapterCaptureLoop.cs:110` changes the forward from fire-and-forget to scope-joined. `DegradedPumpKeepsSiblingsRunningAndForwardsCallback` and `RetryCallbackObservesAttempts` are the ones that observe the callback delivery and may need to await the tracked work.

### 3. `TransactionalCaptureRuntime` (`CaptureLifecycle`) — `tests/WinForward.Core.Tests/CaptureLifecycleTests.cs`

`public sealed class CaptureLifecycleTests` at `:6` (9 named methods listed; `rg` counts 10 public methods).

`StartupFailureRestoresEveryAppliedAdapter`, `GracefulStopRestoresModesAndClosesCapture`, `CaptureLoopNormalCompletionRestoresModes`, **`ConcurrentStopWaitsForCaptureRunBeforeRestoringModes`** (pins `StopAsync` awaiting `_runTask` before `CleanupAsync`, `CaptureLifecycle.cs:126`), `DisposingBeforeStartClosesCaptureAndModes`, **`ConcurrentStopsBeforeStartWaitForTheSameCaptureCleanup`** (pins `_cleanupTask ??=` single-flight, `:204`), `ReachedPumpRunStaysFalseWhenSnapshotFailsDuringStart`, `ReachedPumpRunStaysFalseWhenModeApplyFailsDuringStart`, `ReachedPumpRunLatchesTrueOnceTheCaptureLoopRunStarted`.

`tests/WinForward.Core.Tests/DurableCaptureBundleTests.cs` (`:23`, 10 methods) covers `DurableCaptureBundle`, which constructs/disposes `IdleExpirySweeper` (`DurableCaptureBundle.cs:237`/`:351`).

**Mechanism at risk**: `_shutdown` CTS (`CaptureLifecycle.cs:32`), `_runTask` (`:36`), `_cleanupTask` (`:37`), `_captureDisposed` (`:38`). If the runtime gains a `QuiescenceScope` that owns the CTS (D7), the cleanup single-flight and the `_shutdown.Token` read at `:86` change mechanism; the two concurrency tests above are the load-bearing ones.

### 4. `NdisCapturePump` — `tests/WinForward.Core.Tests/NdisCapturePumpTests.cs`

`public sealed class NdisCapturePumpTests` at `:7` (13 methods).

`PumpProcessesBatchInArrivalOrder`, `PumpProcessesOnlyFilledSlotsOfPartialBatch`, `PumpPollsWhenQueueIsEmpty`, `PumpReleasesBatchBuffersExactlyOnce`, `PumpRejectsNonPositiveBatchCapacity`, `PumpRejectsNullDriverAndHandler`, `PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight`, **`DisposeDuringRunWaitsForTheRunLoopToExit`** (pins `DisposeAsync` parking on `_runCompletion`, `NdisCapture.cs:420`), **`DisposeBeforeAnyRunCompletesSynchronously`** (pins the `_runStarted == 0` branch `:421-422`), **`DedicatedPumpThreadExitsAndIsBackgroundAfterRun`** + **`DisposeStopsTheLoopNormallyAndReapsTheThread`** (pin the raw `Thread` `:169-175`), **`IdlePollIterationsAllocateNoManagedBytes`** (the pump's zero-alloc gate), **`SecondRunAsyncThrowsWithoutDisturbingTheFirstRun`** (pins the `_runStarted` one-shot `:161`).

`tests/WinForward.Core.Tests/NdisCaptureResilienceTests.cs`, `tests/WinForward.Core.Tests/BatchedPassReinjectionE2eTests.cs` (`:21`, 2 methods: `PumpFlushesEachIterationIntoOneBatchedSendPerDirection`, `PumpFlushesPendingPassesOnLoopExit`; helper `RunPumpAsync(NdisCapturePump, CancellationTokenSource)` at `:111`), and `tests/WinForward.Core.Tests/NdisCaptureQueueOrderTests.cs` (seen in the test list) also construct pumps.

**Mechanism at risk**: joining the pump to a scope would replace/augment `_runCompletion` (`:124`); `DisposeDuringRunWaitsForTheRunLoopToExit` and `SecondRunAsyncThrowsWithoutDisturbingTheFirstRun` pin the exact current contract. The zero-alloc test pins that nothing is added inside the loop.

### 5. `IdleExpirySweeper` — `tests/WinForward.Core.Tests/IdleExpirySweeperFailureTests.cs`

`public sealed class IdleExpirySweeperFailureTests` at `:17` (1 method) — `SweepFailureLogsRateLimitedWarnAndKeepsSweeping` (pins failure isolation + rate-limited warn). Disposal/start behavior is exercised indirectly via `DurableCaptureBundleTests`.

**Mechanism at risk**: `_shutdown` CTS (`IdleExpirySweeper.cs:25`), `_loop` (`:29`), and the **unguarded** `DisposeAsync` (`:115-127`). Replacing with a scope + D11 single-flight changes double-dispose semantics (today a second `DisposeAsync` throws `ObjectDisposedException` from `CancelAsync`).

### 6. `RuntimeHeartbeat` — `tests/WinForward.Core.Tests/RuntimeHeartbeatTests.cs`

`public sealed class RuntimeHeartbeatTests` at `:14` (9 named methods listed; `rg` counts 11 public methods).

`IdleHeartbeatOmitsEveryZeroValuedField`, `FaultyUsageProviderWarnsAndTheLoopSurvives`, **`DisposeStopsFurtherTicks`**, `DefaultHeartbeatIsSilentUntilStarted`, `NonPositiveIntervalIsRejected`, **`SecondStartIsRejected`** (pins the `_loop is not null` guard, `RuntimeHeartbeat.cs:106`), `HeartbeatReportsGcDeltasSinceTheStartupMarkAndWarnsOnNewCollections`, `GcCollectionWarnFiresOnlyOnTheTickThatObservesNewCollections`, `HeartbeatReportsAggregatePoolOccupancyFromRegisteredPools`.

**Mechanism at risk**: same shape as the sweeper — `_shutdown` CTS (`:57`), `_loop` (`:62`), unguarded `DisposeAsync` (`:250-262`). `SecondStartIsRejected` and `DisposeStopsFurtherTicks` are the guard tests.

### 7. `Socks5ControlConnection` — `tests/WinForward.Core.Tests/Socks5ControlTimeoutTests.cs`

`public sealed class Socks5ControlTimeoutTests` at `:12` (8 named methods listed; `rg` counts 9 public methods).

`ServerAddressResolutionTimeoutDoesNotDependOnResolverCancellation`, **`HandshakeTimeoutIncludesMethodSelectionRead`**, `CallerCancellationRemainsCancellationDuringHandshake`, **`CommandTimeoutIncludesReplyRead`**, `UpstreamStreamClearsPerAttemptSocketTimeouts`, `UpstreamSocketDisablesNagle`, `RelayDatagramFromSiblingAddressIsAccepted`, `RelaySourceValidationAcceptsSameFamilySamePort`, `RelaySourceValidationRejectsDifferentPortOrAddressFamily`.

Also `tests/WinForward.Core.Tests/Socks5UdpAssociateTests.cs`, `tests/WinForward.Core.Tests/Socks5UdpConnresetTests.cs` construct control connections/transports.

**Mechanism at risk**: the per-attempt timeout CTS `_attemptCancellation` (`Socks5ControlConnection.cs:171-172`, disposed `:244`). `HandshakeTimeoutIncludesMethodSelectionRead` and `CommandTimeoutIncludesReplyRead` pin the `CancelAfter(timeout)` mechanism; moving CTS ownership to a scope (D7) must preserve the per-attempt timeout semantics (see the open wrinkle in `socks5-token-audit.md`). `UpstreamStreamClearsPerAttemptSocketTimeouts` pins the socket-timeout clearing.

### 8. `Socks5UdpTransport` — `tests/WinForward.Core.Tests/Socks5UdpTransportSendTests.cs`

`public sealed class Socks5UdpTransportSendTests` at `:20` (6 methods) — `SyncSendReachesLoopbackEchoPeerAndReceivesTheEcho`, `RelaySocketRunsNonBlockingAfterAssociate`, `JumboCapSendBufferEncodesPayloadsBeyondTheDefaultCap`, `DefaultCapSendBufferFailsClosedOnOversizedPayloads`, **`SendSpanAsyncWarmPathRunsNoAsyncStateMachine`**, **`WarmSyncSendAllocatesNoManagedBytes`**.

Plus `UdpReceiveResilienceTests.cs` (`:19`, 6 named methods: `ReceiveLoopSurvivesMalformedUnexpectedAndOversizedRelayDatagrams`, `InjectionFailureSkipsOneResponseWithoutKillingTheSession`, `TransportClassifiesAnomalousRelayDatagramsAsSkipsInsteadOfThrowing`, `ConcurrentSendsSerializeIntoTheSharedSendBuffer`, `ReceiveLoopSurvivesTransportConnectionReset`, `DomainTypedResponseIsCountedAndSkipped`), `Socks5UdpConnresetTests.cs` (`:18`, 2 methods).

Also the global gate `tests/WinForward.Core.Tests/HotPathAllocationGateTests.cs` — `EstablishedUdpDatagramPathAllocatesNoManagedBytes` (window `:148-155`, assertions through `:171`).

**Mechanism at risk**: the UDP send/receive ledger is the strictest constraint. `WarmSyncSendAllocatesNoManagedBytes`, `SendSpanAsyncWarmPathRunsNoAsyncStateMachine`, `ConcurrentSendsSerializeIntoTheSharedSendBuffer`, and `EstablishedUdpDatagramPathAllocatesNoManagedBytes` must stay green with zero added allocation; no C4 change to `SendSpanAsync`/`ReceiveAsync`/dispose ordering can perturb them.

---

## Tests that will need updating (mechanism → test)

| Mechanism change | Test(s) to update | File |
|------------------|-------------------|------|
| `MultiAdapterCaptureLoop` forward becomes scope-joined (`WF0001` removal) | `DegradedPumpKeepsSiblingsRunningAndForwardsCallback`, `RetryCallbackObservesAttempts` (may need to await tracked work) | `NdisCaptureResilienceTests.cs` |
| `LayeredCaptureRunner` spawns replaced; `_runTask` possibly scope-owned | `ZeroPeriodicIntervalDisablesTheTickEntirely` (guard must survive) | `LayeredCaptureRunnerPeriodicRefreshTests.cs` |
| `TransactionalCaptureRuntime` CTS → scope-owned; cleanup single-flight | `ConcurrentStopWaitsForCaptureRunBeforeRestoringModes`, `ConcurrentStopsBeforeStartWaitForTheSameCaptureCleanup` | `CaptureLifecycleTests.cs` |
| `NdisCapturePump` completion joined to a scope | `DisposeDuringRunWaitsForTheRunLoopToExit`, `DisposeBeforeAnyRunCompletesSynchronously`, `SecondRunAsyncThrowsWithoutDisturbingTheFirstRun`, `DedicatedPumpThreadExitsAndIsBackgroundAfterRun` | `NdisCapturePumpTests.cs` |
| `IdleExpirySweeper` CTS → scope-owned + D11 single-flight | double-dispose no longer throws (new coverage needed) | `IdleExpirySweeperFailureTests.cs` |
| `RuntimeHeartbeat` CTS → scope-owned + D11 single-flight | `SecondStartIsRejected`, `DisposeStopsFurtherTicks` | `RuntimeHeartbeatTests.cs` |
| `Socks5ControlConnection` CTS → scope-owned (D7) | `HandshakeTimeoutIncludesMethodSelectionRead`, `CommandTimeoutIncludesReplyRead`, `UpstreamStreamClearsPerAttemptSocketTimeouts` | `Socks5ControlTimeoutTests.cs` |
| UDP hot path (must be unchanged) | `WarmSyncSendAllocatesNoManagedBytes`, `SendSpanAsyncWarmPathRunsNoAsyncStateMachine`, `EstablishedUdpDatagramPathAllocatesNoManagedBytes` | `Socks5UdpTransportSendTests.cs`, `HotPathAllocationGateTests.cs` |

---

## Where new per-owner quiescence tests belong

Per `.trellis/spec/backend/directory-structure.md`: `:32` one xunit class per file, `:79` file name = test class name with a `Tests` suffix, `:82` shared fakes moved to `tests/WinForward.Core.Tests/TestHelpers/` (namespace `WinForward.Core.Tests`, no `.TestHelpers`), `:33` TestHelpers groups fakes by category.

Existing grouped test classes already place multiple classes in one file (`NdisCaptureResilienceTests.cs` holds `NdisCaptureResilienceTests` and `CaptureDegradationPlumbingTests`), so a per-owner quiescence class in the same file is acceptable, but a new `*Tests.cs` per owner matches the C3 precedent. Candidate files (test class names to match):
- `tests/WinForward.Core.Tests/LayeredCaptureRunnerQuiescenceTests.cs`
- `tests/WinForward.Core.Tests/TransactionalCaptureRuntimeQuiescenceTests.cs`
- `tests/WinForward.Core.Tests/NdisCapturePumpQuiescenceTests.cs`
- `tests/WinForward.Core.Tests/IdleExpirySweeperQuiescenceTests.cs`
- `tests/WinForward.Core.Tests/RuntimeHeartbeatQuiescenceTests.cs`
- `tests/WinForward.Core.Tests/Socks5ControlConnectionQuiescenceTests.cs`

Shared fakes already available in `tests/WinForward.Core.Tests/TestHelpers/` (reuse before adding): `CaptureLifecycleFakes`, `CaptureRunnerFakes`, `AdapterFakes`, `CapturePipelineFakes`, `FakeAdapterListChangeSource`, `ScriptedReader`, `TrackingSocket`, `UdpTransportFakes`, `RecordingLogger`, `AsyncTestExtensions`. A new fake is warranted only when ≥2 test files need it (`directory-structure.md:82`).

The D11 double-dispose / late-join shape is already covered for the C3 owners (e.g. `TcpProxyRelay` teardown tests in the cluster task); the C4 per-owner tests should mirror that shape for the six owners above.
