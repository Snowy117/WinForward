# PRD: Deep-module refactors (follow-up to compatibility-API cleanup)

Origin: `09-19-compat-api-cleanup` design-health review. The full evidence lives in that
task's `research/design-health.md` §5 items 7–18 (archived with that task; key expectations are
restated here so this task is self-contained).

## Goal

Apply the behavior-neutral module refactors that were deliberately kept out of the cleanup
task because they change structure rather than delete unused surface. No behavioural change;
interface depth, seam placement, and testability are the targets.

## Candidate items (each independently plannable)

1. `SetupExecutor` (`src/WinForward.Runtime/SetupExecutor.cs`): replace the hidden
   `item.Handler!` ordering invariant with an enforced contract (required-parameter factory or
   explicit guard); consider removing the `workerCount = 0` sentinel.
2. `TcpProxyCoordinator` (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs`): replace
   the 12-param ctor + `_owns*` flags with composition-owned dependencies or a
   `TcpRedirectOptions` record.
3. `UdpProxyCoordinator` (`src/WinForward.Runtime/UdpProxy/UdpProxyCoordinator.cs`): consolidate
   the public 9-param / internal 12-param dual constructors into one options-based ctor with
   internal test seams.
4. `UdpSessionSetup` (`src/WinForward.Runtime/UdpProxy/UdpSessionSetup.cs`): replace the 5
   delegate couplings with one internal slot-access seam so it can be tested directly.
5. `UdpProxySession` (`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs`): group the 12 ctor
   parameters into a context record; make `TryBeginExpiry`/`CancelExpiry` internal.
6. Coordinator diagnostics (`TcpProxyCoordinator`, `UdpProxyCoordinator`): consolidate the
   internal test/observability seam area into one internal diagnostics snapshot per module.
7. `NdisCapturePump` (`src/WinForward.NdisApi/NdisCapture.cs`): consolidate the 6 internal hooks
   into one internal diagnostics record; move test-only options members off the public options
   record.
8. Clock injection: `FlowTable` (`TryResolve`/`TryClaimResolved` read `DateTimeOffset.UtcNow`
   while `RemoveExpired` takes `now`) and `UdpSetupQueueBudget` — inject a clock/`TimeProvider`.
9. `FlowKey`/`TransportTuple` hash-mirror (`Domain.cs`, `FlowTable.cs`): guard with a shared
   field-set test or extracted hashing so the equivalence relations cannot drift.
10. `SetupExecutor.DefaultRingCapacity` vs `TcpPendingSynSetupIndex.DefaultCapacity`: link via a
    shared constant or an assertion test.
11. `DurableCaptureBundle` (`src/WinForward.Cli/DurableCaptureBundle.cs`): shrink the god
    composition root (11-param ctor, 14 internal members) by extracting UDP/TCP wiring
    sub-builders.
12. Single-adapter seams (`ISetupExecutor`, `IUdpAdapterTargetSource`): either prove them with a
    substituting fake or document them as composition-only seams.

## Constraints

- Behaviour-neutral, zero-warning, full suite green; allocation/soak contracts unchanged.
- Use the shared design vocabulary: module, interface, implementation, depth, seam, adapter,
  leverage, locality.
- Split into child tasks if a single item is large (e.g. item 11).

## Acceptance Criteria (draft)

- AC1: Each implemented item is covered by a design note explaining the depth/seam improvement.
- AC2: No behavioural change: build zero-warning, full test suite green, allocation gates pass.
- AC3: Items left unimplemented are explicitly re-routed or dropped with a reason.
