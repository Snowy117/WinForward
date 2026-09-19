# PRD: Deep-module refactors (follow-up to compatibility-API cleanup)

Origin: design-health review produced during `09-19-compat-api-cleanup`; report §5 items 7–18.
Evidence base (archived, read-only):
`.trellis/tasks/archive/2026-09/09-19-compat-api-cleanup/research/design-health.md`.
All anchors below were re-verified at HEAD `7854b1f` (2026-09-19); numbers that drifted from the
report are stated as current.

## Goal

Apply the behavior-neutral structural refactors deliberately kept out of the cleanup task:
deepen module interfaces (constructors, seams, diagnostics), without changing behavior,
allocation contracts, or public packet-path semantics.

## Background (verified facts)

- Baseline: zero-warning `dotnet build -c Release`; `dotnet test -c Release` green (journal
  records 721 at `2ea413d`; M0 re-records the exact count). Linux + nix shell (dotnet-sdk_10).
- The cleanup task already resolved part of the report's framing: `TcpProxyCoordinator.framePool`
  deleted (ctor now 11 params), `.Table` internal, UDP send path span-only, `SetupWorkKind`
  demoted to internal, `SynCopyPool` property deleted.
- Current arities: TCP ctor 11 (6 req + 5 opt); UDP public 8 → internal 11; bundle ctor 12
  (7 req + 5 opt). UDP internal-ctor test sites ≈20 (report's "~50" conflated total ctor uses).
- `SetupWorkKind` / `SetupWorkItem.Kind` are currently write-only (assigned by both
  coordinators, read by nobody) — dead surface.
- External seam graph (`ITcp*` / `IUdp*` / `INdisPacketReader`) is healthy and out of scope.

## Requirements (R1–R12; each independently verifiable)

- R1 `SetupExecutor` (`src/WinForward.Runtime/SetupExecutor.cs`): replace the hidden
  `item.Handler!` ordering invariant (L215) with an enforced contract; remove the
  `workerCount = 0` sentinel (L99-103); delete the dead `SetupWorkKind`/`Kind`.
- R2 `TcpProxyCoordinator` (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs`):
  replace the 11-param ctor (L46-57) + `_ownsSynCopyPool`/`_ownsSetupExecutor` (L32/34) with a
  named options record (keep sole-caller test churn in mind: 66 test ctor sites).
- R3 `UdpProxyCoordinator` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`):
  consolidate the public 8-param ctor (L39-50) and internal 11-param ctor (L52-63) into one
  options-based ctor with internal test seams; address `_owns*` (L21/23/25).
- R4 `UdpSessionSetup` (`src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs`): replace the 5
  ctor delegate couplings (L38-42, ctor L47-59) with one internal slot-access seam so the
  module can be tested directly. `UdpProxyCoordinator.UdpSessionSlot` (nested, L457) is an
  opaque handle to this module today.
- R5 `UdpProxySession` (`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs`): group the
  12-param ctor (L53-65) into a context record; make `TryBeginExpiry` (L165) / `CancelExpiry`
  (L175) internal (they are `public` on an internal class; sole callers are in-assembly).
- R6 Coordinator diagnostics: consolidate the internal test/observability seam area into one
  internal diagnostics snapshot per module — TCP hooks (ConcurrentLoserCount L96,
  CapacityRejectionCount L103, LogCapacitySummary L111, HoldsFlow L614, Tombstones L618,
  PendingSetups L621, DrainPendingSetupsAsync L629, CapacityResetCooldowns L632, Table L82)
  and UDP diagnostics (5 `*ForDiagnostics` members, L104-125). `ConcurrentLoserCount` must stay
  readable (quality-guidelines.md pins it as a test-assertable counter).
- R7 `NdisCapturePump` (`src/WinForward.NdisApi/NdisCapture.cs`): consolidate the 6 internal
  hooks (PumpThread L166, RunIterationForTests L174, TransientReadRetryCount L369,
  TransientReadIncidentCount L372, IsDegraded L375, LastDegradedNativeErrorCode L378) into one
  diagnostics record; move test-only `BatchCapacity`/`TransientRetryBaseDelay` off the public
  `NdisCapturePumpOptions` (L36-42).
- R8 Clock injection: `FlowTable` (`src/WinForward.Core/FlowTable.cs` L138 `DateTimeOffset.UtcNow`
  inside `TryResolveLocked` while `RemoveExpired` takes `now` L85) and `UdpSetupQueueBudget`
  (`src/WinForward.Runtime/UdpProxy/UdpSetupQueueBudget.cs` L75 `DateTime.UtcNow.Ticks`).
- R9 Hash mirror: `FlowKey.GetHashCode` (`src/WinForward.Core/Domain.cs:97-105`) and
  `TransportTuple.GetHashCode` (`src/WinForward.Core/FlowTable.cs:174-182`) are identical
  expressions today, kept in sync by comment only; make drift impossible.
- R10 Ring/index capacity: `SetupExecutor.DefaultRingCapacity` (L76) vs
  `TcpPendingSynSetupIndex.DefaultCapacity` (`TcpPendingSynSetup.cs:48`); the "matches" claim
  is comment-only (L75).
- R11 `DurableCaptureBundle` (`src/WinForward.Cli/DurableCaptureBundle.cs`, 447 lines):
  shrink the god composition root by extracting UDP/TCP wiring sub-builders. Split into a
  child task only if this outgrows its milestone.
- R12 Single-adapter seams: `ISetupExecutor` (`SetupExecutor.cs:58`, 1+0 adapters) and
  `IUdpAdapterTargetSource` (`UdpAdapterTargetSource.cs:20`, 1+0) — either prove with a
  substituting fake or document as composition-only seams.

## Decisions

- 2026-09-19: single task with ordered milestone batches (user choice); commit per milestone.
  Details and per-item technical choices live in `design.md`.

## Constraints

- Behavior-neutral: zero-warning build, full suite green, exact baseline test count preserved,
  allocation gates (`HotPathAllocationGateTests`) and gc-soak contracts unchanged. Structural
  refactors must not change assertion semantics while migrating test call sites
  (quality-guidelines.md).
- Use the shared design vocabulary: module, interface, implementation, depth, seam, adapter,
  leverage, locality.
- Public API deltas are limited to in-repo consumers (Cli / tests / benchmarks): no external
  consumers exist.

## Out of scope

- Configuration-layer change for `SetupWorkerCount` (`0 = auto` stays; the bundle maps at the
  call site; `ConfigurationModels.cs` untouched).
- Behavior changes, new features, perf tuning, and the report's explicitly healthy modules.

## Acceptance criteria

- AC1: Each implemented item is covered by its design note in `design.md` explaining the
  depth/seam improvement (and, where behavior-adjacent docs change, a spec update at finish).
- AC2: No behavioral change: build zero-warning, full test suite green at the recorded
  baseline count, allocation gates pass, gc-soak smoke clean.
- AC3: Any item not implemented is explicitly re-routed or dropped with a reason (item R11 may
  split into a child task instead).

## Open questions

None blocking. (Resolved 2026-09-19: task organization = single task + milestone batches.)
