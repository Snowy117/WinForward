# Research: existing tests touching the TCP/UDP lifecycle cluster

- **Query**: C3 question 6 — every existing test touching the cluster, file/class/method, and the invariant each pins
- **Scope**: internal (`tests/WinForward.Core.Tests`, incl. `TestHelpers/`)
- **Date**: 2026-09-21
- **Baseline**: `tests/WinForward.Core.Tests` (no analyzer tests in scope; `tests/WinForward.Analyzers.Tests` is C2's)

Method names verified by reading each file / `rg 'public (async )?(Task|void)'`. Grouped by the invariant
category the task asks for (lifecycle/quiescence, fault observation, stall, teardown reason, single-flight
disposal, allocation).

## Lifecycle / quiescence

| File | Method | Invariant pinned |
|---|---|---|
| `TcpRedirectSessionStoreTests.cs` | `DisposeIsSingleFlightAndLateTeardownNeverReEnters` (`:15`) | R2: `DisposeAsync` single-flight; late `TearDownSessionAsync`/`FailAssociationAsync`/`RemoveExpiredAsync` do not re-enter; one relay dispose, one tombstone. |
| `TcpRedirectSessionStoreTests.cs` | `RegistrationLosingTheDisposeRaceLeavesTheAssociationReleasable` (`:47`) | `TryRegister` after dispose retires the session; `ReleaseAssociationAsync` still consumes alias+token. |
| `TcpRedirectSessionTests.cs` | `RetireAfterTheLinkedShutdownAlreadyEndedTheLoopDoesNotThrow` (`:15`) | R1: `Retire` tolerates an already-disposed linked CTS. |
| `TcpRedirectSessionTests.cs` | `RetireCancelsTheLifetimeAndDisposeLifetimeIsIdempotent` (`:30`) | R1: `Retire` cancels `Token`; `DisposeLifetime` idempotent. |
| `TcpRedirectSessionTests.cs` | `RetireAfterDisposeLifetimeLeavesTheDisposedSourceUntouched` (`:46`) | R1: `Retire` after `DisposeLifetime` does not touch the disposed source. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `ShutdownDisposesAllSessionsAndListeners` (`:314`) | Store dispose releases every session/listener. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `DisposeWaitsForInFlightSetupAndReleasesLateListener` (`:332`) | Store dispose drains started `EnterSetup` work and releases a listener that arrives late. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `RetireRemovesTableAliasAndArmsTombstoneBeforeListenerDisposalCompletes` (`:277`) | R2 atomic retire: alias removal/tombstone committed before the trailing listener dispose completes. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `CancellationAfterSynDoesNotStopSharedAcceptLoop` (`:254`) | Caller cancellation does not stop the shared accept loop. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `CancelledRetransmitDoesNotRetireSharedRedirect` (`:234`) | Caller cancellation does not retire the shared redirect. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `AcceptLoopDoesNotRunAwayOnTransientAcceptError` (`:376`) | L3 accept retry back-off. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `UnrelatedAcceptedConnectionIsClosedBeforeRelaySetup` (`:355`) | Unrelated peer closed, no relay. |
| `TcpProxyCoordinatorConcurrencyTests.cs` | `ExpiryRacingRelayEstablishmentCannotAttachDetachedRelay` (`:238`) | Expiry race vs attach. |
| `TcpProxyCoordinatorConcurrencyTests.cs` | `SilentRelayingFlowSurvivesSweepsWithoutReevaluatingDecision` (`:263`) | M4 relaying session not idle-expired. |
| `TcpProxyCoordinatorConcurrencyTests.cs` | `ConcurrentSynBurstOnOneFlowUsesOneListener` (`:101`), `ConcurrentSynBurstWhileListenerSetupIsParkedIsAbsorbedIntoOneSetup` (`:129`) | One setup per flow; R8 background absorb. |
| `TcpPendingSynSetupTests.cs` | `LaunchedSetupItemCarriesTheShutdownTokenSoParkedSetupsUnwindOnDispose` (`:295`) | Setup items carry `_store.ShutdownToken`; parked setups unwind on dispose. |
| `TcpPendingSynSetupTests.cs` | `SynDispatchDoesNotWaitForListenerAllocation` (`:313`) | R8: dispatch returns before listener exists. |
| `TcpPendingSynSetupTests.cs` | `DisposeRacingAnInFlightSetupLeavesNoCooldownOrCharge` (`:388`) | Dispose vs in-flight setup: no cooldown/charge leak. |
| `TcpPendingSynSetupTests.cs` | `DisposeClearsTheCooldownAnEarlierFailureArmed` (`:368`) | Dispose clears cooldowns. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `RemoveExpiredDisposesIdleSessionAndReleasesAssociation` (`:24`) | Idle expiry disposes session and releases association. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `RemoveExpiredKeepsActiveSession` (`:46`) | Active session retained. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `FailedSetupReleasesSlotAndCapacityForOtherFlows` (`:62`) | Failed setup frees capacity. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `CoordinatorDisposalPreservesSetupCancellation` (`:123`) | Disposal cancels a pending setup without hanging. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `ConcurrentDisposalIsSingleFlightAndRejectsNewSends` (`:135`) | Single-flight coordinator dispose; new sends throw `ObjectDisposedException`. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `SuccessfulSendRefreshesAssociationAndAvoidsStaleExpiry` (`:150`) | Activity refresh prevents stale expiry. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `ActiveSessionRetainsRelayAliasAfterItsCreationTimestampExpires` (`:172`) | Alias retention + cooldown. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `ExpirySnapshotDoesNotDisposeSessionWhoseSendRefreshesActivity` (`:228`) and `…ReceiveRefreshesActivity` (`:269`) | Sweep snapshot vs concurrent activity; `BeforeExpiryRecheck` seam. |
| `UdpProxyCoordinatorTests.cs` | `SessionStateReportsSettingUpWhileDialingThenActiveWhenReady` (`:211`) | `UdpSessionState` transitions. |
| `UdpProxyCoordinatorTests.cs` | `ReceiveBufferIsBoundedAndReturnedWhenCoordinatorStops` (`:189`) | Receive-window pool balance after coordinator dispose. |
| `UdpProxyCoordinatorTests.cs` | `SameFlowBurstUsesOneTransportAndPreservesDatagrams` (`:19`), `ConcurrentSameFlowBurstUsesOneTransportWithoutResponseCrossWiring` (`:44`) | FIFO flush / one transport per flow. |
| `UdpSetupQueueTests.cs` | `FirstDatagramDoesNotAwaitAStalledSetupAndDatagramsRelayInFifoOrder` (`:26`), `SetupQueueOverflowDropsOldest…` (`:57`), `DatagramsCannotBypassTheSetupQueueAroundTheFlush` (`:97`), `DisposeDropsDatagramsStillQueuedForSetup` (`:249`) | Setup-queue ordering, drop-oldest, dispose drain. |
| `UdpSetupCooldownTests.cs` | `FailedSetupEntersCooldownAndRetriesAfterOneSecond` (`:23`), `SetupCooldownsAreBoundedAndEvictTheOldestAtCapacity` (`:46`), `SetupConcurrencyCapQueuesFlowsBeyondTheCapUntilASlotFrees` (`:82`), `SetupFlashCrowdOfDistinctFlowsQueuesThroughTheCapWithoutLoss` (`:121`) | Cooldown + 8-wide limiter (patient admission). |
| `UdpSetupQueueBudgetTests.cs` | `GlobalSetupBudgetRejectsBeyondTheAggregateAndCreditsBackOnFlush` (`:20`), `SetupFailureCreditsBackThePendingBudget` (`:59`), `DisposeCreditsBackDatagramsStillQueuedForSetup` (`:81`), `SetupQueueLeasesReturnToThePoolAcrossDropOldestFlushAndTtlDrop` (`:104`), `SetupQueueLeaseIsReleasedWhenTheDatagramExceedsTheFrameCap` (`:143`) | Budget charge/credit exactly once; lease balance. |

## Fault observation

| File | Method | Invariant pinned |
|---|---|---|
| `UdpProxySessionTests.cs` | `GenuineReceiveFaultRecordsFaultedAndFiresTheFailureHandler` (`:112`) | Receive fault → `State == Faulted`, handler fires; faulted session refuses sends. |
| `UdpProxySessionTests.cs` | `IdleExpiryEndsTheReceiveLoopWithoutRecordingAFailure` (`:83`) | R3: lifetime cancel = normal teardown; **no** failure handler call. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `ReceiveFaultDisposesAndRemovesSessionWithoutAnotherSend` (`:82`), `ImmediateReceiveFaultRemovesSessionAfterCoordinatorRegistration` (`:101`) | Fault handler removes slot/disposes session; pool returns. |
| `TcpRelayObservationTests.cs` | `AttachFailureObservesFaultedRelayCompletion` (`:19`), `AttachFailureObservesAlreadyFaultedRelayCompletion` (`:41`), `RelayDisposeObservesFaultedCompletion` (`:63`), `RelayDisposeObservesNothingWhenCompletionSucceeds` (`:80`), `FaultObserverSuppressesTheEventWhenDebugLoggingIsDisabled` (`:100`) | S3: discarded relay completions observed; `tcp.relay.faulted` emitted once; exception read precedes the Debug gate. **These are the tests whose premise C3 changes.** |
| `UdpReceiveResilienceTests.cs` | `ReceiveLoopSurvivesMalformedUnexpectedAndOversizedRelayDatagrams` (`:24`), `InjectionFailureSkipsOneResponseWithoutKillingTheSession` (`:73`), `ReceiveLoopSurvivesTransportConnectionReset` (`:198`), `DomainTypedResponseIsCountedAndSkipped` (`:235`) | Skip-class anomalies never tear the session down. |
| `Socks5UdpConnresetTests.cs` | `ReceiveFaultClassificationMapsOnlyConnectionResetToSkip` (`:61`) | Transport fault classification. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `RelaySetupFailureBlocksAndReleasesAlias` (`:41`), `ListenerAllocationFailureFailsClosedWithCooldown` (`:20`), `ExistingFlowInjectionFailureReleasesAssociation` (`:215`) | Fail-closed teardown on setup/injection fault. |

## Stall

| File | Method | Invariant pinned |
|---|---|---|
| `TcpProxyRelayTests.cs` | `OneSidedRelayFailureCancelsSiblingPumpImmediately` (`:83`) | Fault cancels sibling pump. |
| `TcpProxyRelayTests.cs` | `MidStreamFailureCancelsSiblingPumpAfterRepeatedStallWindowRearms` (`:94`) | Stall-window re-arm/throttle never unlinks the lifetime cancellation. |
| `TcpProxyRelayTests.cs` | `StallRearmThrottleIsOneSecond` (`:120`), `StallRearmIsDueOnlyForFirstArmAndAfterThrottleInterval` (`:126`) | X8a re-arm throttle. |
| `TcpRelayEndResetTests.cs` | `StalledRelayEndInjectsClientResetBeforeTeardown` (`:48`), `RelayEndKindIsStalledWhenPumpStalls` (`:132`) | `RelayEndKind.Stalled` derivation + client reset before teardown. |
| `TcpRelayEndResetTests.cs` | `FaultedRelayEndInjectsInWindowClientResetBeforeTeardown` (`:31`), `CleanRelayEndDoesNotInjectClientReset` (`:65`), `RelayWithoutEndInfoIsTreatedAsCleanEnd` (`:82`), `RelayEndKindIsCleanEndedWhenBothDirectionsFinish` (`:100`), `RelayEndKindIsFaultedWhenPumpFaults` (`:118`) | All three `RelayEndKind` paths + origin-direction matrix. |

## Teardown reason

| File | Method | Invariant pinned |
|---|---|---|
| `UdpSessionSetupTests.cs` | `SetupFailureReportsTheSetupFailureTeardownReason` (`:54`) | Genuine dial failure → `UdpTeardownReason.SetupFailure`. |
| `UdpSessionSetupTests.cs` | `CancelledSetupReportsTheShutdownTeardownReason` (`:75`) | Cancelled dial → `Shutdown` (no cooldown). |
| `UdpSessionSetupTests.cs` | `FlushDropsTtlExpiredDatagramThroughTheSlotHostSeam` (`:22`) | TTL-drop flush; lease returned; fake host observes session via opaque slot. |
| `TcpProxyCoordinatorLifecycleTests.cs` | `CapacityRejectedSynInjectsSingleRstPerTuplePerCooldownWindow` (`:405`), `CapacityResetFollowsOriginDirectionMatrix` (`:450`), `CapacityResetInjectionFailureWarnsWithoutChangingOutcome` (`:478`) | Capacity-reset cooldown (teardown reason adjacent). |

## Single-flight disposal

| File | Method | Invariant pinned |
|---|---|---|
| `TcpRedirectSessionStoreTests.cs` | `DisposeIsSingleFlightAndLateTeardownNeverReEnters` (`:15`) | Store single-flight dispose. |
| `UdpProxyCoordinatorLifecycleTests.cs` | `ConcurrentDisposalIsSingleFlightAndRejectsNewSends` (`:135`) | Coordinator single-flight dispose. |
| `TcpProxyRelayTests.cs` | `DisposeLeavesCompletionCompletedAndReturnsPumpBuffers` (`:176`) | R1: relay `DisposeAsync` awaits `Completion`; both pump buffers returned. |
| `TcpProxyCoordinatorConcurrencyTests.cs` | `ExpiryRacingRelayEstablishmentCannotAttachDetachedRelay` (`:238`) | Attach race. |

## Allocation

| File | Method | Invariant pinned |
|---|---|---|
| `HotPathAllocationGateTests.cs` | `MidFlowRewriteAndInjectAllocatesNoManagedBytes` (`:30`), `ReverseRewriteAndInjectAllocatesNoManagedBytes` (`:57`), `EstablishedUdpDatagramPathAllocatesNoManagedBytes` (`:84`), `UdpSetupEnqueuePathAllocatesNoManagedBytes` (`:125`), `Socks5MessageProductionAllocatesNoManagedBytes` (`:155`), `TcpResetBuildViaSpanAllocatesNoManagedBytes` (`:186`), `SynRetentionWithWarmSynCopyPoolAllocatesNoManagedBytes` (`:213`), `DispatcherWarmFastPathAllocatesNoManagedBytes` (`:254`), `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` (`:317`), `FlowTableRecyclesExpiredStatesThroughItsPool` (`:365`) | Per-packet 0-B gates; the UDP one is detailed in `allocation-gate-hardening.md`. |
| `QuiescenceScopeAllocationGateTests.cs` | `WarmEnterExitPairAllocatesNoManagedBytes` (`:9`) | C1 primitive: 4096 warm `TryEnter`/`Dispose` pairs = 0 B. |

## C1 primitive tests (reference for C3's new per-owner tests)

`QuiescenceScopeTests.cs` (363 lines) — the semantics C3 will rely on:
`EnterAndExitRestoreIdleAccounting` `:10`, `DoubleDisposeOfOneLeaseLeavesTheCountIntact` `:27`,
`DrainWaitsForEveryOutstandingLease` `:40`, `DrainOnIdleScopeCompletes` `:59`,
`SealedScopeRefusesEnterAndRunWithoutInvokingTheBody` `:67`, `DrainIsSingleFlightAndIdempotent` `:86`,
`DrainCallersObserveTheSameTaskInstance` `:104`, `ConcurrentDrainCallersObserveTheSameTaskInstance` `:120`,
`DrainCancelsTheOwnedToken` `:151`, `TokenIsReleasedOnceDrained` `:163`, `CancelAfterDrainIsANoOp` `:172`,
`RecordFaultKeepsTheFirstExceptionAndDoesNotFaultTheDrain` `:181`, `RecordFaultWithoutASiteLeavesTheFaultSiteEmpty` `:198`,
`FaultedRunChildRecordsItsName` `:208`, `ChildFaultDoesNotCancelSiblingsAndDrainStillCompletes` `:223`,
`FaultedRunChildIsRecordedAndDoesNotEscape` `:244`, `NestedScopesDrainByComposition` `:280`,
`ConcurrentEnterExitRacingDrainNeverLosesTheWakeup` `:301`.

**Reusable pattern for C3's fault-injection test:** `QuiescenceScopeTests.UnobservedExceptionProbe`
`QuiescenceScopeTests.cs:327-362` — a private class, *not* in `TestHelpers/`. It subscribes to
`TaskScheduler.UnobservedTaskException` and counts only the injected fault (process-global event, parallel
test classes). C3's §4.4 "delete the observers, assert no unobserved task exception and `tcp.relay.faulted`
still recorded" can reuse this shape, but would need to lift/copy the probe into `TestHelpers/` to share it.

## Test helpers relevant to the cluster

| Helper | File | Use |
|---|---|---|
| `UdpCoordinatorFakes.CreateCoordinator` | `TestHelpers/UdpCoordinatorFakes.cs:15-29` | Builds a coordinator with `TestPools` defaults. |
| `GatedTransportFactory`, `DelayedTransportFactory`, `FailingTransportFactory`, `CancellationAwareTransportFactory`, `CollidingAliasTransportFactory`, `MutableTimeProvider` | `TestHelpers/UdpCoordinatorFakes.cs:36-136` | Setup/expiry determinism. |
| `FakeTransportFactory`, `FakeTransport`, `FakeResponseSink`, `NoopResponseSink`, `ImmediateFaultTransportFactory`, `ImmediateFaultTransport` | `TestHelpers/UdpTransportFakes.cs` | Receive/send fakes. |
| `TcpCoordinatorFakes.CreateCoordinator/CreateSession/CreateHostAssociation`, `FakeListener`, `FakeAcceptedConnection`, `FakeRelay(Factory)`, `CompletableRelay(Factory)`, `Gated*` | `TestHelpers/TcpCoordinatorFakes.cs` | TCP session/relay fakes. |
| `AsyncTestExtensions.WaitForAsync` | `TestHelpers/AsyncTestExtensions.cs:13` | Polling (10 s default budget). |
| `AsyncTestExtensions.IgnoreExpectedCancellationAsync` | `TestHelpers/AsyncTestExtensions.cs:24` | Shutdown helper. |
| `RecordingRuntimeLogger` | `TestHelpers/RecordingLogger.cs` | Asserts `tcp.relay.faulted` / skip summaries. |

## Caveats / Not Found

- `TestHelpers/` contains **no** `SynchronizationContext`/single-thread scheduler helper and **no** public
  `UnobservedExceptionProbe`; both would be new for C3.
- `TcpProxyCoordinatorRewriteTests.cs`, `TcpRedirectInjectorTests.cs`, `TcpRedirectTombstoneTableTests.cs`,
  `TcpFragmentHandlingTests.cs`, `TcpResetBuilderTests.cs`, `TcpReversePrefilterTests.cs` touch the TCP
  redirect path but not the lifetime machinery; excluded except where they exercise tombstone/fail-closed.
