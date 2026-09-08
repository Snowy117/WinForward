# P2 R1–R4 rg re-verification (2026-09-08, check pass)

Commands run from repo root over `src/` + `tests/` + `benchmarks/` (zero-hit result noted
per sweep; recorded post-hoc by the check agent — the R1–R4 commit itself carried no note):

- `rg -n "IWindowsAdapterInventory" src/ tests/ benchmarks/` → zero hits.
- `rg -n "EnsureDriverVersion|InterpretReadResult" src/ tests/ benchmarks/` → zero hits
  (`NdisNativeCallStatus.InterpretBatchReadResult`, the live batch path, is retained).
- `rg -n "\.TryGet\(" src/ tests/ benchmarks/` → zero hits (`FlowTable.TryGet` fully gone).
- `rg -n "\.Claim\(|\.TryClaim\(" src/ tests/ benchmarks/` → every remaining hit binds to a
  different class with a different signature (`UdpAssociationTable.Claim/TryClaim`,
  `TcpRedirectTable.TryClaim`, cooldown tables); `FlowTable` (Domain.cs:158) now exposes only
  `TryResolve` / `TryClaimResolved` / `RemoveExpired`.
- ABI preservation rule honored: `GetDriverVersion` (NdisApiAbi.cs:187), `ReadPacket`
  (NdisApiAbi.cs:211), and `ReadPackets` (NdisApiAbi.cs:215) P/Invoke declarations all retained.

# P2 R5–R8 implementation notes (2026-09-08)

Baseline: HEAD = de1152a (R1–R4 committed). 566/566 tests green, zero warnings.
Note: `UdpSetupQueueTests.LimiterQueueWaitDoesNotExpireTheTriggeringDatagram` failed once in the
untouched baseline run (timing-flaky; passes in isolation and in every subsequent full run).

Final: 578/578 tests green (+3 config R6, +9 R7: 4 gate + 5 bundle), zero warnings.

## R5 — TestHelpers convergence

Promoted fakes (all `internal`, namespace `WinForward.Core.Tests`, with design comments):

| Fake | TestHelpers file | Call sites updated |
|---|---|---|
| `FakeModes` | CaptureLifecycleFakes.cs (new) | CaptureLifecycleTests, NdisCaptureResilienceTests (CaptureDegradationPlumbingTests) |
| `CompletingCapture` | CaptureLifecycleFakes.cs (new) | same two files |
| `BlockingCapture` | CaptureLifecycleFakes.cs (new) | same two files |
| `ScriptedReader` | ScriptedReader.cs (new) | NdisCaptureResilienceTests, NdisCapturePumpTests |
| `NoopResponseSink` | UdpTransportFakes.cs (added next to FakeResponseSink) | Socks5UdpAssociateTests, IdleExpirySweeperFailureTests |
| `FakeGuard` (+`Owned` switch) | CapturePipelineFakes.cs (extended) | see below |

Behavior reunifications (all supersets, assertion semantics unchanged):
- `FakeModes`: superset of the two private variants — adds `Applied` recording + `failOnApply`
  injection (default −1 = never fails, identical to the resilience variant's behavior).
- `CompletingCapture`: adds the `Disposed` flag (used only by lifecycle tests).
- `BlockingCapture`: adds the `CancellationObserved` cancellation registration (unused by the
  resilience tests; harmless superset).
- `ScriptedReader`: superset — `throwAlways` failure injection (resilience variant) + `Calls`
  counter (pump variant). Pump-test call sites wrapped in collection expressions for the
  array-taking constructor.
- `FakeGuard`: gained an `Owned` init switch (superset). This also resolved a same-name collision
  the research table had folded away: FlowDispatcherTests declared a private `FakeGuard { Owned }`
  shadowing the promoted never-owns one. The private one is deleted; `new FakeGuard { Owned = true }`
  keeps working against the promoted superset.

Removed private duplicates (migrated to promoted fakes, not weakened):
- `NoSelfTraffic` ×2 (RuntimeLoggingTests, NdisCaptureResilienceTests) and
  `NeverSelfTrafficGuard` ×1 (TcpReversePrefilterTests) → `new FakeGuard()` (never-owns default,
  behaviorally identical).
- `FakeExecutor` collision: FlowDispatcherTests' private `FakeExecutor` {PassCount, ProxyCount}
  was a strict subset of the promoted `FakeExecutor` {PassCount, BlockCount, ProxyCount}; deleted
  the private one instead of renaming — the collision is gone and every assertion
  (`PassCount`/`ProxyCount`) is preserved against the promoted superset.

rg re-verification (repo root, post-change):
- `rg -n "private sealed class (FakeModes|CompletingCapture|BlockingCapture|ScriptedReader|NoopResponseSink|NoSelfTraffic|NeverSelfTrafficGuard|FakeGuard|FakeExecutor)\b" tests/`
  → zero hits (all promoted fakes live in TestHelpers/ only).
- Single-file fakes intentionally left local (≥2-file rule): `FakeCapture`, `BlockingDisposeCapture`
  (CaptureLifecycleTests), `GatedReader` (NdisCapturePumpTests), `PerHandleReader`/
  `PermanentFailureReader`/`CancellingReader`/`FailingRestoreModes`/`NoopExecutor`
  (NdisCaptureResilienceTests), `FiniteMixedPassReader`/`ScriptedFaultingReader`
  (BatchedPassReinjectionE2eTests), `RecordingExecutor`/`RecordingReverseHandler` (single-file).

## R6 — Indexed diagnostics

- `ParseNetworks`/`ParseSet`/`ParsePorts` now report `{path}[{index}]` for invalid (non-null)
  elements; message content (including the offending value) unchanged. Null-element and
  `ValidateNonEmpty` behavior untouched.
- New tests: `ConfigurationIndexesInvalidCidrElements`,
  `ConfigurationIndexesUnsupportedSetElements`,
  `ConfigurationIndexesEachInvalidPortElementIndependently` (exact path + message asserts).
- **Deferred**: the `logLevel` `JsonElement` → `string?` typing unification. Reason: STJ maps both
  an absent field and JSON `null` to `null` for a `string?` property, collapsing the two
  distinguishable cases the `JsonElement` typing exists for (`ConfigurationRejectsInvalidLogLevel`
  pins `"logLevel": null` as a validation error while absence defaults to Info). Preserving that
  contract requires a custom converter / sentinel machinery — beyond the ~20-line mechanical
  budget. Revisit as its own follow-up.

## R7 — Cli coverage

- `Program.LogAdapterTransientRetry` (5 s CAS rate-limit) moved to
  `src/WinForward.Runtime/AdapterTransientRetryLogGate.cs` (internal, next to RuntimeLogging;
  injectable `Func<long>` ticks source). Program keeps a thin delegate call
  (`retryLogGate.Log(adapter.StableId, adapter.FriendlyName, nativeError, attempt)`).
  rg: `LogAdapterTransientRetry` → zero hits in src/ + tests/ + benchmarks/.
  Not unified with `NdisPacketActionExecutor`'s ShouldWarn pattern (P1 territory, per instructions).
- Gate tests (deterministic, no sleeps): `FirstRetryLogsTheStructuredAdapterEvent`,
  `RetriesInsideTheWindowAreSuppressed`, `RetryAtTheWindowEdgeLogsAgain`,
  `SuppressedRetriesDoNotExtendTheWindow`.
- `DurableCaptureBundleTests` (5 tests): scope-head fallback + per-adapter resolve, zero-MAC
  adapter filtered with warn, zero-MAC head keeps zero-placeholder fallback (two warns — host
  fallback + own per-adapter skip), empty scope clears snapshot, wholesale snapshot swap.
- **New test seam**: `DurableCaptureBundle`'s constructor changed private → internal (the minimal
  hook pre-authorized by the instructions; no `[Obsolete]`/reflection). The ctor doc states tests
  as the reason; `CreateAsync` stays the production path.

## R8 — Doc correction

- `NdisPacketBuffer` class XML doc now states the rental-window Dispose contract (idle repeat =
  no-op; stale Dispose after re-rental returns the buffer under the current renter; pool never
  double-frees; Dispose exactly once per rental). The `Dispose` inline comment was aligned with
  the same contract. No behavior change.
