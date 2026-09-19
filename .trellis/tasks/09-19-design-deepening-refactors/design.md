# Design — Design-Deepening Refactors (R1–R12)

> Source: design-health report §5 items 7–18 (archived in 09-19-compat-api-cleanup/research/design-health.md).
> All items are behavior-neutral structural refactors at HEAD `7854b1f`. No packet-path behavior,
> allocation, lock ordering, or logging event changes. Test assertions move verbatim; no assertion
> semantics may change (quality-guidelines.md "Structural refactors are behavior-zero").

---

## Shared Patterns

Three patterns recur across items; later items reference them by name.

### P1 — Options record with internal test-seam members

Mirror the praised `NdisCapturePumpOptions` model (NdisCapture.cs:36-42): a public sealed record
holding all optional dependencies, defaults matching current behavior. Members that exist only to
serve tests are declared `internal` on the record; `InternalsVisibleTo` (WinForward.Core.Tests,
WinForward.Benchmarks) reaches them, external callers cannot.

- Hidden-but-required dependencies (clock, recheck hook) become `internal init` members, NOT
  required ctor params — that would break call sites and churn tests for no shape gain.
- Chosen over "composition-owned required dependencies" (rejected: 66 TCP + ≈20 UDP test ctor
  sites would each need pool construction + disposal boilerplate; the coordinator's
  create-if-absent ownership is a deliberate convenience, see R2/R3).
- `_owns*` fields remain internal coordinator details; public behavior unchanged: an injected pool
  or executor is never disposed by the coordinator, a created one is.

### P2 — Diagnostics snapshot record

One immutable record per coordinator/pump exposing counters + state a test or log reader wants to
observe, so the type's read surface reads as one concept instead of N scattered internals.
Control surfaces (methods that mutate or drain: `PendingSetups`, `DrainPendingSetupsAsync`,
`LogCapacitySummary`, `TrySendSpanAsync`, `Table`) are NOT part of the snapshot — they stay where
they are.

### P3 — Composition record (Cli only)

An internal record transferring ownership of already-created pools from DurableCaptureBundle into
the composer that wires a coordinator; `DurableCaptureBundle` keeps creation + disposal ordering
verbatim so the ordered-teardown contract (quality-guidelines.md) is untouched.

---

## R1 — SetupExecutor: required-parameter factory, dead kind removal

**Current**: `ISetupExecutor.RentItem()` returns a `SetupWorkItem` whose `Handler` field is
`internal Func<SetupWorkItem, Task>?` — nullable, so `Execute` needs `item.Handler!` (SetupExecutor.cs:215)
and every caller must know to assign it. `SetupWorkKind` (L10) is write-only; `Kind` (L29) has no reader.

**Design**:
- `ISetupExecutor.RentItem(Func<SetupWorkItem, Task> handler)` — the factory requires the handler, so
  a handler-less item is unrepresentable and `Handler` becomes non-nullable.
- Delete `SetupWorkKind` enum and `SetupWorkItem.Kind` (verified: no reader in src/tests/benchmarks).
- `SetupExecutor` ctor: `int? workerCount = null, int ringCapacity = DefaultRingCapacity` (null =
  auto). App layer (DurableCaptureBundle) maps config `SetupWorkerCount` `0 → null` at the call
  site; ConfigurationModels stays untouched (out of scope).
- `Execute` replaces `item.Handler!(item)` with the non-null field call; keep the
  `#pragma warning disable VSTHRD002` scope and comment — sync-over-async remains deliberate.

**Call-site updates**: TcpProxyCoordinator, UdpProxyCoordinator (each has one RentItem site) and
any test/benchmark direct users — mechanical `RentItem(() => ...)` lambda binding.

**Rejected**: making `Handler` a ctor parameter of SetupWorkItem (changes re-rent semantics — the
pool reuses item instances? verify at implement time; the factory form keeps the existing internal
allocation/rent shape).

## R2 — TcpRedirectOptions record

**Current**: 11-param public ctor (TcpProxyCoordinator.cs:46-57), 6 required + 5 optional; 66 test
ctor sites.

**Design**:
```csharp
public sealed record TcpRedirectOptions
{
    public IRuntimeLogger? Logger { get; init; }
    public int Capacity { get; init; } = 16_384;
    public IInterceptionHealthSignal? HealthSignal { get; init; }
    public NativeBufferPool? SynCopyPool { get; init; }
    public ISetupExecutor? SetupExecutor { get; init; }
}
public TcpProxyCoordinator(TcpRedirectListenerFactory listenerFactory, TcpRelayFactory relayFactory,
    IPacketReinjector injector, TcpRedirectTable table, SelfTrafficRegistry selfTraffic,
    IReadOnlySet<HashSet<IPAddress>> localAddresses, TcpRedirectOptions? options = null)
```
- Null/omitted options == current defaults exactly (Logger null → RuntimeLogging fallback path as
  today; Capacity 16_384; pools/executor created-if-absent).
- Test updates: mechanical rewrite of ctor calls; assertions and gates unchanged. The
  `ConcurrentLoserCount` accessor stays test-assertable (moves per R6).

## R3 — UdpProxyOptions record, single public ctor

**Current**: public 8-param ctor forwards `: this(...)` to an internal 11-param ctor
(UdpProxyCoordinator.cs:39-63); tests (~20 sites) use the internal ctor to inject TimeProvider,
beforeExpiryRecheck, setupQueueGlobalByteBudget.

**Design**:
```csharp
public sealed record UdpProxyOptions
{
    public IRuntimeLogger? Logger { get; init; }
    public int Capacity { get; init; } = 16_384;
    public int MaximumFrameSize { get; init; } = UdpFrameBuilder.DefaultMaximumEthernetFrame;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public NativeBufferPool? ReceiveWindowPool { get; init; }
    public NativeBufferPool? SetupQueuePool { get; init; }
    public ISetupExecutor? SetupExecutor { get; init; }
    internal Action<FlowKey, DateTimeOffset>? BeforeExpiryRecheck { get; init; }
    internal long? SetupQueueGlobalByteBudget { get; init; }
}
public UdpProxyCoordinator(IUdpProxyTransportFactory transportFactory, IUdpResponseSink responseSink,
    UdpProxyOptions? options = null)
```
- Delete the internal forwarding ctor; TimeProvider becomes public (it is a legitimate documented
  seam), the two test-only members internal init.
- `maximumFrameSize` consumers: composition (BuildWithUdpAsync), reinjector parity test
  (quality-guidelines scenario) — signature line in spec still matches via options.

## R4 — IUdpSessionSlotHost seam

**Current**: UdpSessionSetup takes 5 delegates (UdpSessionSetup.cs:38-42) into the coordinator's
gate; the slot type is an opaque handle.

**Design**: new file `src/WinForward.Runtime/UdpProxy/IUdpSessionSlotHost.cs` (interface next to its
implementation per directory-structure.md):
```csharp
internal interface IUdpSessionSlotHost
{
    int RefreshSetupStamps(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot);
    void AttachSession(UdpProxyCoordinator.UdpSessionSlot slot, UdpProxySession session);
    (UdpSessionSetup.FlushStep Step, NativeLease Lease, int Length, DateTimeOffset EnqueuedAt)
        DequeueForFlush(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot);
    Task RemoveSlotAsync(FlowKey flow, UdpProxyCoordinator.UdpSessionSlot slot, bool armCooldown);
    Task RemoveReceiveFailedSessionAsync(UdpProxySession session);
}
```
- Coordinator implements it (explicit interface implementation keeps the gates' surface internal).
- UdpSessionSetup ctor 12 → 8 params (`IUdpSessionSlotHost host` replaces the 5 delegates).
- `UdpSessionSlot` stays nested in UdpProxyCoordinator (referenced as
  `UdpProxyCoordinator.UdpSessionSlot` — same pattern as today's delegate signatures).
- **Seam proof**: one direct UdpSessionSetup test with a fake `IUdpSessionSlotHost` — the unit
  becomes constructible without a real coordinator, proving the seam (mirror of how
  TcpRedirectSetup is directly testable). This is the only new test file this task adds.
- Synergy: when R5 lands, the fake host builds contexts per R5's record — design R5 first in the
  same milestone so the test is written once.

## R5 — UdpProxySessionContext

**Current**: UdpProxySession 12-param ctor (UdpProxySession.cs:53-65); `TryBeginExpiry` /
`CancelExpiry` are `public` on an internal class with only the coordinator as caller.

**Design**:
- `internal sealed record UdpProxySessionContext(FlowKey Flow, long FlowGeneration,
  UdpAssociation Association, IUdpProxyTransport Transport, IUdpResponseSink Sink, byte[] ClientMac,
  CancellationToken Shutdown, TimeProvider TimeProvider, Action<FlowKey, DateTimeOffset>? ActivityObserver,
  IRuntimeLogger Logger, ArrayPool<byte>? ReceiveWindowPool, int ReceiveBufferSize);` — field order
  and nullability identical to today's ctor.
- `UdpProxySession(UdpProxySessionContext context)` single param; body destructures to the same
  fields; no behavior change.
- `TryBeginExpiry`/`CancelExpiry` → `internal`.
- Update sites: UdpSessionSetup construction call, UdpProxySessionTests helper (L100).

**Rejected**: keeping 12 params (the context record is the smallest change that gives the
construction vocabulary a name and lets R4's fake host read naturally).

## R6 — Diagnostics snapshot records

**Design**:
- `TcpRedirectDiagnostics` (new file under TcpRedirect/): `ConcurrentLoserCount`,
  `CapacityRejectionCount`, `PendingSetupCount`, `TombstoneCount`, `CapacityResetCooldownCount`,
  `IReadOnlySet<FlowKey> HeldFlows` + convenience `bool HoldsFlow(in FlowKey)`.
  Coordinator exposes `internal TcpRedirectDiagnostics Diagnostics { get; }` (computed snapshot;
  building it takes the gates briefly as today's individual accessors do).
  Removed individual members: `ConcurrentLoserCount`, `CapacityRejectionCount`, `HoldsFlow`,
  `Tombstones`, `PendingSetups` (count only — the drain method stays), `CapacityResetCooldowns`.
  Kept unchanged: `Table`, `PendingSetups` control surfaces, `DrainPendingSetupsAsync`,
  `LogCapacitySummary`, `TrySendSpanAsync`.
- `UdpProxyDiagnostics` (new file under UdpProxy/): `SetupCooldownCount`,
  `PendingSetupBytes`, `SetupBudgetRejectionCount`, `SetupTtlExpiredCount`,
  `SetupStampsRefreshedCount`. Coordinator exposes `internal UdpProxyDiagnostics Diagnostics { get; }`;
  the five individual internal accessors are removed.
- Test updates: mechanical property-path rewrite (`coordinator.ConcurrentLoserCount` →
  `coordinator.Diagnostics.ConcurrentLoserCount`); assertions unchanged. The
  `ConcurrentSynBurstWithAsyncListenerStaysExactlyOnce` gate keeps asserting `== N-1` through the
  new path.

## R7 — NdisPumpDiagnostics

**Current**: NdisCapturePumpOptions already exists; two of its members (`BatchCapacity`,
`TransientRetryBaseDelay`) are set only by tests + CapturePumpBenchmarks. Four internal telemetry
accessors on the pump.

**Design**:
- `internal sealed record NdisPumpDiagnostics(bool IsDegraded, long LastDegradedNativeErrorCode,
  long TransientReadRetryCount, long TransientReadIncidentCount);` pump exposes
  `internal NdisPumpDiagnostics Diagnostics { get; }`. Remove the four accessors.
- `BatchCapacity`/`TransientRetryBaseDelay` → `internal init` members on the public options record
  (tests + benchmarks have IVT). Production never sets them — same as today.
- `PumpThread` / `RunIterationForTests` stay (control seams; RunIterationForTests is documented as a
  deterministic single-iteration entry). No doc changes.

## R8 — Clock seams (FlowTable, UdpSetupQueueBudget)

**Design**:
- `FlowTable(int capacity = 65_536, TimeProvider? timeProvider = null)` — stored as
  `_timeProvider = timeProvider ?? TimeProvider.System`; `TryResolveLocked` uses
  `_timeProvider.GetUtcNow()` where `DateTimeOffset.UtcNow` is used today (FlowTable.cs:138).
  `RemoveExpired(now, …)` already takes explicit now — unchanged.
- `UdpSetupQueueBudget(long byteBudget, IRuntimeLogger logger, TimeProvider timeProvider)` — the
  timeProvider param is required (single construction site: UdpProxyCoordinator, which has the
  TimeProvider already); `NoteDrop` reads `timeProvider.GetUtcNow().UtcTicks` (L75).
- New test: FlowTable observation-refresh boundary test can finally use a fake clock (additive;
  existing tests untouched). UdpSetupQueueBudget rate-limit summary unaffected (throttle window
  behavior identical with TimeProvider.System).

## R9 — FlowHash shared helper

**Current**: `FlowKey.GetHashCode()` (Domain.cs:97-105) and `TransportTuple.GetHashCode()`
(FlowTable.cs:174-182) contain identical hash expressions, linked only by comments.

**Design**: `internal static class FlowHash` in Domain.cs (co-located with FlowKey — the cluster is
tightly related, allowed same-file cluster):
```csharp
internal static int Combine(AddressFamilyKind addressFamily, TransportProtocol protocol, Endpoint local, Endpoint remote);
```
Both GetHashCode methods delegate. Drift becomes impossible; hash values are identical
(byte-for-byte same expression). No test seam needed (TransportTuple is private; equality of
buckets is what matters and is covered by existing resolve tests).

## R10 — Ring-capacity linkage

**Current**: `SetupExecutor.DefaultRingCapacity` (1_024, SetupExecutor.cs:76) and
`TcpPendingSynSetupIndex.DefaultCapacity` (1_024, TcpPendingSynSetup.cs:48) linked only by comment.
The real invariant is an inequality: the pending-SYN index must not exceed the executor ring, else
enqueue can reject under load.

**Design**: add an assertion test `DefaultRingCapacity >= TcpPendingSynSetupIndex.DefaultCapacity`;
update the TcpPendingSynSetupIndex comment to state the inequality (not equality). **Rejected**:
`const` linkage (a const-referencing-expression across types couples the two files' compilation and
says equality — the wrong invariant).

## R11 — DurableCaptureBundle composers

**Current**: DurableCaptureBundle.cs (447 lines) is both composition root and per-coordinator
wiring: CreateAsync creates table/pools/cache/executor, BuildWithUdpAsync creates UDP pools,
BuildBundle/CreateUdpCoordinator/CreateTcpCoordinator wire coordinators, UpdateUdpTargets builds
the adapter map, DisposeCoreAsync owns ordered teardown.

**Design**:
- New `src/WinForward.Cli/TcpRedirectComposer.cs` + `UdpProxyComposer.cs` (internal static).
- Composition records: `TcpRedirectComposition(TcpRedirectTable Table, IReadOnlySet<IPAddressSet>? …)`
  / `UdpProxyComposition(...)` transferring already-created pools + executor (P3) — the bundle
  keeps creation and rollback; composers only wire coordinator options.
- Shared pool helper `BundlePools` for the pool-name constants (L39-42) + `RegisterPool` (L156-162)
  so both the bundle and composers name pools identically.
- `PrimeSocks5AddressCacheAsync` moves to UdpProxyComposer.
- **Unchanged in bundle**: `CreateAsync` orchestration + rollback try/catch, `BuildBundle` assembly,
  `UpdateUdpTargets`, `OnScopeInstalled`, `DisposeCoreAsync` ordering verbatim, `ReloadAsync` path.
- If the bundle still exceeds 400 effective lines after extraction, split a child task (documented;
  not planned).
- Synergy note: R2/R3 options records make the composer signatures small enough that extraction is
  mostly parameter passing.

## R12 — Document composition-only seams

**Design**: XML `<remarks>` on `ISetupExecutor` (SetupExecutor.cs) and `IUdpAdapterTargetSource`
(UdpAdapterTargetSource.cs) stating the seam is composition/test infrastructure, the production
adapter is the only implementation, and why no substituting fake exists (no scenario a fake would
exercise that the real adapter does not already cover in its tests). No code change; comment only.

---

## Cross-cutting compatibility

- Public API surface (Microsoft.Extensions DI–facing): Cli is the only consumer; no external
  package consumers. All ctor signature changes are in-repo-only.
- `InternalsVisibleTo` set: WinForward.Core.Tests + WinForward.Benchmarks (unchanged); the CLI
  assembly (WinForward) IVT to Runtime (unchanged).
- No serialization/schema changes. No config-file changes (`SetupWorkerCount` mapping is call-site
  only). No log event names, levels, or formats change.

## Risks & rollback

- **Risk: test churn obscures a real regression.** Mitigate: milestone batches with targeted test
  filters between full runs; R6/R5/R4 are mechanical renames — any red test is either a missed
  rename (compile fail, obvious) or a real bug.
- **Risk: coordinator ctor rewrite introduces an ownership mistake (`_owns*`).** Mitigate: ownership
  logic is copied verbatim; the lifecycle tests (mode restoration ordering, single-flight disposal)
  gate each coordinator milestone.
- **Rollback**: per-milestone revert (one commit per milestone, on master). No file moves across
  projects except R4's new interface file and R11's composer files, so reverts are clean.
- **Allocation posture**: no change to owned types, pool lifetimes, or hot-path code shapes; the
  allocation gates running per milestone prove it.

## Verification plan

- Per milestone: `dotnet build -c Release` (zero warnings) + targeted test filter; full run at
  milestone boundaries; final full run must equal M0 baseline count (721, re-recorded at M0 start).
- Grep gates at close: `SetupWorkKind` 0 hits; `Handler!` 0 hits; `DateTimeOffset.UtcNow` in
  FlowTable/UdpSetupQueueBudget 0 hits; `_example` 0 rows in jsonl manifests.
- `--stability --scenario gc-soak --duration 20` smoke after M6 (composition change).
- AC audit R1–R12 against prd.md at M7; unimplemented items re-routed with reasons.
