# Research: Design health of the zero-allocation infrastructure (deep-module assessment)

- **Query**: Assess the recently added zero-allocation infrastructure for module design health against deep-module principles; produce a prioritized, actionable report.
- **Scope**: Internal, read-only. All evidence is from `src/` and `tests/` in this repository.
- **Date**: 2026-09-19

Vocabulary used exactly as given: module, interface, implementation, depth, shallow, seam, adapter, leverage, locality.

---

## 0. How to read this

"Interface" here means everything a caller must know: member signatures, constructor parameter count/optionality, invariants, ordering, error modes, and lifetime rules — not just the `public` keyword list. A module is **deep** when a small interface hides a large implementation; **shallow** when a caller must know almost as much as the implementation contains.

Two categories of seam are separated throughout, per PRD R2:

- **External seam** = a public interface/type a product caller can substitute. Test-only parameters or members that leak into these signatures are a problem.
- **Internal seam** = `internal` member/type reachable only inside the assembly (and by tests through `InternalsVisibleTo`). Used by the module's own tests, this is legitimate and should not be churned.

`InternalsVisibleTo` grants `WinForward.Core.Tests` and `WinForward.Benchmarks` access to every production assembly's internals (`WinForward.Runtime`, `WinForward.Cli`, `WinForward.NdisApi`, `WinForward.Protocols`, `WinForward.Windows`). Consequently every `internal` member listed below is test-visible; "internal" does not mean "hidden from tests".

---

## 1. Summary table

Severity: **High** = directly worsens the external interface or hides a production invariant; **Medium** = interface pollution / coupling that is contained; **Low** = cosmetic or already well-documented.

| Module | Depth verdict | Main weakness | Severity | Routing |
|---|---|---|---|---|
| `NativeBufferPool` / `NativeLease` / `NativeMemoryManager` | **Deep** | None material (release-exactly-once contract is documented) | Low | healthy — no churn |
| `FlowTable` / `FlowState` / `Domain` | **Deep** | `DateTimeOffset.UtcNow` hidden inside `TryResolve`/`TryClaimResolved`; only `RemoveExpired` takes `now` | Low–Med | [follow-up] |
| `SetupExecutor` / `ISetupExecutor` / `SetupWorkItem` | **Deep-ish** | Public `SetupWorkItem` with 0 public members; hidden "set `Handler` before enqueue" NRE invariant; 1-adapter `ISetupExecutor` | Med | [cleanup] + [follow-up] |
| `RuntimeHeartbeat` | **Deep** | `gcSnapshotProvider` is a public ctor parameter passed only by tests; `counters ?? RuntimeCounters.Shared` global fallback | Med | [cleanup] |
| `Socks5AddressCache` | **Deep** | None | Low | healthy — no churn |
| `TcpProxyRelay` / `TcpProxyRelayFactory` | **Deep** | Tests assert `internal` production constants; `SharedPumpBufferPool` static test/bench fallback | Low–Med | [follow-up] |
| `TcpProxyCoordinator` | **Deep** | 12-param ctor (6 optional) with inline ownership; `framePool` never passed by anyone; public `Table` used only by tests; 9 internal test hooks | High | [cleanup] + [follow-up] |
| `UdpProxyCoordinator` (+ `.Send` partial) | **Deep** | Public ctor forwards to internal 12-param ctor; `_owns*` flags; 6 internal diagnostics + internal ctor used by ~50 test call sites; 8-param `TrySendAsync` | High | [cleanup] + [follow-up] |
| `UdpSessionSetup` (internal) | **Deep** | 12-param ctor with 5 delegate couplings back into coordinator slot; no direct tests | Med | [follow-up] |
| `UdpProxySession` (internal) | **Deep** | 12-param ctor; constructed directly by unit tests | Med | [follow-up] |
| `Socks5UdpTransport` / `Socks5UdpTransportFactory` | **Deep** | Public 3-arg `CreateAsync` is a pass-through; internal 8-param overload mixes production and test-only parameters | Med | [cleanup] |
| `Socks5ControlConnection` | **Deep** | `resolveAddresses`/`socketFactory` are test-only optional parameters on the public static `ConnectAsync` | Med | [cleanup] |
| `NdisCapturePump` | **Deep** | 6 internal test hooks; 2 `NdisCapturePumpOptions` members are test-only | Low–Med | [follow-up] |
| `DurableCaptureBundle` (internal) | **Composition root** | 11-param ctor, 14 internal members, knows and constructs every module | Med | [follow-up] |
| `BoundedSetupQueue` / `UdpAssociationTable` | **Deep** (with thin wrappers) | Pass-through overload chains | Low | [follow-up] |

Bottom line: the **external** seam placement (the `ITcp*` / `IUdp*` / `INdisPacketReader` interfaces) is generally excellent — depth is real and the test suite substitutes 2–8 adapters per seam. The design problems are concentrated at the **constructor/optional-parameter layer** and in a handful of **test-only members that leak onto public signatures**.

---

## 2. Quantitative signals

Approximate public interface size (production members only; nested types included per file). "Opt." = optional parameters. "Internal test hooks" = `internal` members referenced from tests/benchmarks. "Adapters" = production + test implementations of the primary public interface.

| Module (primary file) | Lines | Public types | Public members | Ctor params (req/opt) | Internal test hooks | Adapters (prod + test) |
|---|---|---|---|---|---|---|
| `Core/NativeBufferPool.cs` | 249 | 3 | 18 (across pool/lease/manager/stats) | 2 (1/1) | 0 | n/a (concrete) |
| `Core/FlowTable.cs` | 196 | 1 | 6 | 1 (0/1) | 0 | n/a (concrete) |
| `Core/Domain.cs` | 177 | 10 | 23 | `FlowState`: 3 (0/0) + internal pooled ctor | 2 (`FlowState` internal ctor + `Reset`, used only by `FlowTable`) | n/a |
| `Runtime/SetupExecutor.cs` | 263 | 4 | 13 | 2 (1/1, `workerCount = 0` sentinel) | 2 (`IsDisposed`; `SetupWorkItem` internal fields) | `ISetupExecutor` = 1 + 0 |
| `Runtime/RuntimeHeartbeat.cs` | 249 | 3 | 4 | 7 (1/6) | 1 (`gcSnapshotProvider`, **public**) | n/a |
| `Socks5/Socks5AddressCache.cs` | 64 | 1 | 6 | 2 (0/2) | 0 | n/a |
| `TcpRedirect/TcpProxyRelay.cs` | 323 | 1 public (+1 internal iface/enum) | 6 | factory: 4 (1/3); relay: 5 (3/2, internal type) | 6 (`RelayConnectMaxAttempts`, `RelayConnectAttemptTimeout`, `ArmThrottleTicks`, `IsRearmDue`, `RelayEndKind`, direct internal ctor) | `ITcpProxyRelayFactory` = 1 + 5; `ITcpRelay` = 1 + 4 |
| `TcpRedirect/TcpRedirectInterfaces.cs` | 127 | 8 | — | — | — | `ITcpRedirectListenerFactory` 1+6, `ITcpRedirectInjector` 1+3, `ITcpReverseHandler` 1+1, etc. |
| `TcpRedirect/TcpProxyCoordinator.cs` | 682 | 1 (+1 internal) | 23 (incl. nested) | 12 (6/6) | 9 (`ConcurrentLoserCount`, `CapacityRejectionCount`, `LogCapacitySummary`, `HoldsFlow`, `Tombstones`, `PendingSetups`, `SynCopyPool`, `DrainPendingSetupsAsync`, `CapacityResetCooldowns`) | n/a concrete |
| `UdpProxy/UdpProxyCoordinator.cs` | 550 | 1 (+ `UdpSessionSlot` internal) | 6 | public 9 (2/7) → internal 12 (adds `TimeProvider`, `beforeExpiryRecheck`, `setupQueueGlobalByteBudget`) | 6 diagnostics + internal ctor + `TrySendSpanAsync` | n/a concrete |
| `UdpProxy/UdpProxyCoordinator.Send.cs` | 144 | — | 1 internal (`TrySendSpanAsync`) | 8 (6/2) | 1 (benchmark) | — |
| `UdpProxy/UdpSessionSetup.cs` | 214 | internal only | — | 12 (all required, 5 delegates) | 0 direct | internal |
| `UdpProxy/UdpProxySession.cs` | 382 | internal only | 12 (public-on-internal) | 12 (all required) | direct construction in 3 tests | `IUdpProxyTransport` = 1 + 2 |
| `NdisApi/NdisCapture.cs` | 417 | 4 | 5 | 4 (3/1) | 6 (`RunIterationForTests`, `PumpThread`, `TransientReadRetryCount`, `TransientReadIncidentCount`, `IsDegraded`, `LastDegradedNativeErrorCode`) | `INdisPacketReader` = 1 (driver) + 2 fakes |
| `Socks5/Socks5UdpTransport.cs` | 454 | 6 | 12 | public static `CreateAsync`: 3 (all req); internal static `CreateAsync`: 8 (3 req + 5 opt, 3 test-only) | 5 (internal `CreateAsync`, `IsAcceptableRelaySource`, `IsPossiblyTruncated`, `ClassifyReceiveFault`, `DisableUdpConnectionReset`) | `IUdpProxyTransport` = 1 + 2; `IUdpProxyTransportFactory` = 1 + 7 |
| `Socks5/Socks5ControlConnection.cs` | 384 | 1 | 4 | `ConnectAsync`: 8 (3 req + 5 opt, 2 test-only) | 1 (`GetUpstreamStream`, also production) | n/a |
| `Cli/DurableCaptureBundle.cs` | 447 | internal only | 0 public / 14 internal | 11 (7 req + 4 opt) | internal ctor + `UpdateUdpTargets` (+ all internals) | `IUdpAdapterTargetSource` = 1 + 0 |
| `UdpProxy/UdpAdapterTargetSource.cs` | 87 | 3 | 5 | 1 | 0 | `IUdpAdapterTargetSource` = 1 + 0 |
| `Core/BoundedSetupQueue.cs` | 137 | 1 | 7 | 2 (all req) | 0 | n/a |
| `Core/UdpAssociations.cs` | 168 | 3 exposed | ~12 | 2 (0/2) | 0 | n/a |

Tests per in-scope suite (27 files, ≈ 300 facts total):

| File | Tests | File | Tests |
|---|---|---|---|
| NativeBufferPoolTests | 14 | UdpProxyCoordinatorTests | 10 |
| SetupExecutorTests | 4 | UdpProxyCoordinatorLifecycleTests | 12 |
| TcpProxyRelayTests | 8 | UdpProxySessionTests | 3 |
| TcpProxyCoordinatorCapacityTests | 16 | UdpSetupQueueTests | 18 |
| TcpProxyCoordinatorConcurrencyTests | 13 | Socks5AddressCacheTests | 2 |
| TcpProxyCoordinatorLifecycleTests | 14 | Socks5ControlTimeoutTests | 9 |
| TcpProxyCoordinatorRewriteTests | 16 | Socks5UdpAssociateTests | 6 |
| TcpPendingSynSetupTests | 11 | Socks5UdpConnresetTests | 2 |
| TcpRelayEndResetTests | 7 | Socks5UdpTransportSendTests | 6 |
| TcpFragmentHandlingTests | 7 | NdisCapturePumpTests | 13 |
| RuntimeHeartbeatTests | 11 | NdisCaptureResilienceTests | 10 |
| DurableCaptureBundleTests | 10 | CoreFlowStructuresTests | 10 |
| AdapterScopeAndFlowTableTests | 8 | HotPathAllocationGateTests | 10 |
| GcSoakScenarioTests | 16 | | |

---

## 3. Per-module detail

### 3.1 `NativeBufferPool` / `NativeLease` / `NativeMemoryManager` — Deep, healthy

**Interface a caller must know**: `Rent()` returns a lease; `NativeLease.Span/Memory` is valid for the rental window; `Dispose()` releases exactly once; `Stats` is diagnostic. That is genuinely small for the machinery behind it (CAS state word, pool queue, zeroed overflow allocation, dispose racing, `MemoryManager<byte>` bridge).

Evidence: `NativeBufferPool.cs:37-43` ctor, `:76-98` `Rent`, `:110-125` internal `Release`, `:163-193` `NativeLease`, `:203-230` `NativeMemoryManager`, `:240-249` stats.

- **Seam in the right place**: the pool's own tests (14 facts, `NativeBufferPoolTests.cs`) cross only `Rent`/`Dispose`/`Count`/`Stats`/`AccountingSink` — zero internal reaches. Internal `Release`, `NativeMemoryManager`, and the lease fields are implementation detail used by the module.
- **Adapters**: none needed; this is a concrete leaf module. Its `Stats` record is the observability surface.
- **Design smells**: none material. The stale-release and double-release invariants are documented on `NativeLease` (`:163-193`) and enforced idempotently in `Release`. `AccountingSink` (`:59`) is set in production by `DurableCaptureBundle.RegisterPool` (`DurableCaptureBundle.cs:156-162`), so it is a real product seam, not a test hook.

### 3.2 `FlowTable` / `FlowState` / `Domain` — Deep, minor clock coupling

**Interface**: `Capacity`, `Count`, `TryResolve`, `TryClaimResolved`, `RemoveExpired` — 6 members. Behind it: pooled `FlowState` reuse, a dual-orientation transport index, generation fencing, and expiry semantics.

Evidence: `FlowTable.cs:16-23` ctor, `:42-48` `TryResolve`, `:56-75` `TryClaimResolved`, `:85-114` `RemoveExpired`, private state pool `:116-132`, `TransportTuple` `:160-195`.

- **Seam placement**: correct. `FlowState`'s pooled parameterless ctor and `Reset` are `internal` and consumed only by `FlowTable` (`Domain.cs:154-176`); the tests (`CoreFlowStructuresTests` 10, `AdapterScopeAndFlowTableTests` 8) go through the public table API.
- **Testability**: by interface. No internal test hooks. Zero above-the-interface exposes.
- **Smell — hidden clock**: `TryResolve`/`TryClaimResolved` call `DateTimeOffset.UtcNow` internally (`FlowTable.cs:134-144`), while `RemoveExpired` takes an explicit `now` (`:85`). Two time sources exist in one module; tests must use wall-clock (`CoreFlowStructuresTests` uses `DateTimeOffset.UtcNow`). Similar pattern in `UdpSetupQueueBudget.NoteDrop` (`DateTime.UtcNow.Ticks`).
- **Duplicated knowledge**: `FlowKey.GetHashCode` (`Domain.cs:97-105`) deliberately hashes fewer fields than `Equals` compares (`:108-121`), and must mirror `FlowTable.TransportTuple.GetHashCode` (`FlowTable.cs:170-183`). The relationship is documented in comments but enforced only by reviewer memory. A drift here is a subtle lookup bug.

### 3.3 `SetupExecutor` / `ISetupExecutor` / `SetupWorkItem` — Deep-ish, interface shape weak

**Interface a caller must know**: `ISetupExecutor.RentItem()` + `TryEnqueue(item)`; `SetupWorkItem` fields must be populated by the caller; `Handler` must be set before `TryEnqueue` or the worker throws `NullReferenceException`; `Completion` is optional; the returned item is recycled after execution (`Reset` wipes all fields).

Evidence: `SetupExecutor.cs:25-55` `SetupWorkItem` (12 internal fields, 0 public members), `:58-64` `ISetupExecutor`, `:73` `SetupExecutor`, `:76` `DefaultRingCapacity = 1_024`, `:99` ctor `workerCount = 0` sentinel, `:112` internal `IsDisposed`, `:120-156` rent/enqueue, `:216` `item.Handler!(item)`, `:231` `item.Reset()`.

- **Depth**: real (free-list, fail-closed ring, lazy worker start, overflow allocation). But the caller-visible shape is awkward: `SetupWorkItem` is a **public type with no public members** — only the Runtime assembly can populate it, so the public type is unusable to any external caller. `SetupWorkKind` is public but only consumed inside the assembly.
- **Hidden ordering invariant**: `item.Handler!` null-forgiving at `:216` turns "set `Handler` before `TryEnqueue`" into an unguarded NRE on a worker thread. This is a production invariant with no interface documentation or guard.
- **`workerCount = 0` sentinel**: `0` means "default worker count" (`:99`), a primitive-obsession sentinel rather than a nullable or an options object.
- **Adapters per seam**: `ISetupExecutor` has exactly **one** adapter (`SetupExecutor`) and **zero** test fakes; tests use the real executor because it is cheap. The seam is therefore hypothetical from the test suite's perspective, though the composition root does inject it into both coordinators.
- **Testability**: `SetupExecutorTests` (4 facts) crosses the public rent/enqueue/counters API but writes the **internal** `SetupWorkItem.Handler`/`Completion` fields (12 assignments, `SetupExecutorTests.cs:43-143`) and reads internal `IsDisposed` (`:113`). These are the production protocol, not test-only hooks, so this is acceptable — but it confirms the work item's real interface is internal.

### 3.4 `RuntimeHeartbeat` — Deep, one test-only public parameter

**Interface**: ctor + `DefaultInterval` + `Start` + `DisposeAsync` = 4 public members, plus two public result records (`RuntimeHeartbeatUsage`, `RuntimeGcSnapshot`). Behind it: periodic emission, usage/GC/counter aggregation, health logging.

Evidence: `RuntimeHeartbeat.cs:65-86` ctor (7 params, 6 optional), `:72` `gcSnapshotProvider`, `:81` `counters ?? RuntimeCounters.Shared`, `:203-209` `ReadGcSnapshot`.

- **Depth**: high leverage — a single `Start` drives emission of 8 usage ints + 3 GC counters + pool stats.
- **Smell — test-only public parameter**: `gcSnapshotProvider` (`:72`) is passed **only** from `RuntimeHeartbeatTests.cs:125/212/238/283`. Production `Program.cs:303-311` constructs `RuntimeHeartbeat(logger, usage:, health:)` and never passes it. This is a test concern on the public constructor signature.
- **Global fallback**: `counters ?? RuntimeCounters.Shared` (`:81`) means production silently binds to a process-wide singleton when omitted — hidden global state (documented in the ctor default list but not at the call site).
- **Testability**: otherwise by interface; the 11 tests drive `Start`/`DisposeAsync` and inspect the logger output. `timeProvider`/`interval` are legitimate injectable seams used by both production and tests.

### 3.5 `Socks5AddressCache` — Deep, healthy

**Interface**: ctor + `TryGet` + `Set` + `Invalidate` + `ResolveAsync` = 5 public members in 64 lines. It hides eviction, capacity bounds, and async resolution. Evidence: `Socks5AddressCache.cs:24-64`. Two tests exercise it through the public API and the `ConnectAsync` resolve seam. Zero internal hooks. No changes warranted.

### 3.6 `TcpProxyRelay` / `TcpProxyRelayFactory` — Deep, test-bound constants

**Interface**: `TcpProxyRelayFactory.EstablishAsync` + `TcpProxyRelay.Completion`/`DisposeAsync`. Behind it: bidirectional 64 KiB-lease pumping, stall detection, throttled re-arm, relay-connect retry.

Evidence: `TcpProxyRelay.cs:12-62` factory, `:19-20` internal retry constants, `:23` `PumpBufferSize`, `:69-85` `RelayEndKind` + internal `ITcpRelayEndInfo`, `:96` static `SharedPumpBufferPool`, `:103-107` stall/arm constants, `:121-131` internal ctor, `:229-279` `PumpAsync`.

- **Depth**: strong. The public `ITcpRelay` is just `Completion`; all end-kind nuance stays behind the internal `ITcpRelayEndInfo`.
- **Seam placement**: correct. `TcpProxyRelay` is `internal`; the factory is the public seam. `ITcpRelayEndInfo` exists precisely because `ITcpRelay` is public while end kind is internal — a well-placed capability split.
- **Smell — `SharedPumpBufferPool`**: a process-wide static fallback (`:96`) used when no pool is injected; production injects the bundle pool, so the static exists for direct test/benchmark construction. It is a hidden global with the same shape as `RuntimeCounters.Shared`.
- **Testability**: `TcpProxyRelayTests` (8) and `TcpRelayEndResetTests` (7) construct the internal relay directly and assert **specific internal constant values** (`RelayConnectMaxAttempts`, `RelayConnectAttemptTimeout`, `ArmThrottleTicks`, `IsRearmDue`). Internal seams are legitimate, but asserting constants rather than injectable behavior makes the tests brittle to timing tuning.
- **Adapter count**: `ITcpProxyRelayFactory` 1 prod + 5 test fakes; `ITcpRelay` 1 + 4. Real seams, well exercised.

### 3.7 `TcpProxyCoordinator` — Deep, but the widest interface in scope

**Interface**: 12 public members (`Table`, `SessionCount`, `Capacity`, `HandleSynAsync`, `HandleReverseAsync`, `WantsPacket`, `HandleReverseIfApplicableAsync`, `HandlePacketAsync`, `HandleFragmentAsync`, `RemoveExpiredAsync`, `DisposeAsync`, ctor). Constructor has **12 parameters, 6 optional** (`TcpProxyCoordinator.cs:46-58`). Behind it: SYN interception, pending-SYN retention, setup launch, reverse reinjection, session store, capacity cooldown, tombstone suppression — with four extracted collaborators (`TcpRedirectSetup`, `TcpRedirectSessionStore`, `TcpRedirectAcceptor`, `ClientResetInjector`, doc `:21-24`).

Evidence: `:46-58` ctor, `:70-74` inline ownership (`_framePool = framePool ?? NdisPacketBufferPool.Shared`, `_synCopyPool = synCopyPool ?? new NativeBufferPool(...)`, `_setupExecutor = setupExecutor ?? new SetupExecutor()`), `:83` `Table`, `:124-180` `HandleSynAsync`, `:188-223` `StartPendingSetup`, `:230-250` `LaunchSetup`, `:260-318` `SetupPendingAsync`, `:320-369` `ReinjectExistingFlowDataAsync`, `:97-104`/`:615-636` internal hooks, `:662-682` nested `TcpRedirectSession`.

- **Depth**: high leverage — the four public `Handle*` entry points cover the entire TCP redirect pipeline. Delegation to four internal collaborators is the right direction; the file also hosts `TcpRedirectSession` (`:662-682`), so it is "682 lines" but not a monolith.
- **Smell — never-used optional parameter**: `framePool` (`:56`, assigned `:70`) is **never passed by production, tests, or benchmarks** (grep for `framePool:` returns zero call sites). This is dead configurability baked into the product constructor.
- **Smell — public `Table` used only by tests**: `Table` (`:83`) is read only by `TcpReversePrefilterTests`, `TcpProxyCoordinatorCapacityTests`, `TcpFragmentHandlingTests`; production never reads it. A public property existing for test access.
- **Smell — inline ownership flags**: `_ownsSynCopyPool`/`_ownsSetupExecutor` (`:72`, `:74`) duplicate the same conditional-ownership pattern used in `UdpProxyCoordinator` and `DurableCaptureBundle`.
- **Internal seam area is large**: 9 internal hooks (`:97-104`, `:615-636`), of which `DrainPendingSetupsAsync` is called from many test files and `PendingSetups`/`Tombstones`/`HoldsFlow`/`CapacityResetCooldowns` expose live internal state. All are legitimate R2 internal seams, but the surface adds up.
- **Seam placement**: external interfaces are small and well-factored (`TcpRedirectInterfaces.cs`); the constructor is the problem, not the interfaces.
- **Adapter counts**: `ITcpRedirectListenerFactory` 1 + 6, `ITcpProxyRelayFactory` 1 + 5, `ITcpRedirectInjector` 1 + 3, `ITcpReverseHandler` 1 + 1, `ITcpAcceptedConnection` 1 + 1. Real seams, adversarial fakes (`TcpCoordinatorFakes.cs` 414 lines with `GatedRelayFactory`, `CompletableRelayFactory`, `BarrierListenerFactory`, `ParkingListenerFactory`, `ThrowingListener`, etc.).

### 3.8 `UdpProxyCoordinator` (+ `.Send` partial) — Deep, dual constructors

**Interface**: public ctor (9 params, 7 optional) → internal ctor (12 params, adds `TimeProvider`, `beforeExpiryRecheck`, `setupQueueGlobalByteBudget`) (`UdpProxyCoordinator.cs:39-50`, `:52-101`); public `SessionCount`, `Capacity`, `TrySendAsync` (8 params, 2 optional), `DisposeAsync`, `RemoveExpiredAsync` (`:107-113`, `:176`, `:310`, `:452`). Behind it: association table, session gate, setup admission/budget/cooldown, pre-seed queue, two native pools, cache flush, receive-window sizing.

- **Depth**: real. The `.Send.cs` split (`UdpProxyCoordinator.Send.cs:7-10`, header explicitly says "behavior-zero", file-size-only) keeps the hot span path separate; `TrySendSpanAsync` (`:21`) is the zero-copy entry used by `NdisPacketActionExecutor.cs:449` and a benchmark.
- **Smell — dual constructors**: the public ctor forwards to the internal ctor, so there are two ways to construct the module with overlapping parameters. The internal ctor carries test-only `beforeExpiryRecheck` (passed only by `IdleExpirySweeperFailureTests.cs:33`) and a budget override passed only by `UdpSetupQueueTests.cs:289/326/347`.
- **Smell — inline ownership flags**: `_owns*` pattern for pools/executor, same as the TCP coordinator.
- **Internal test reach is the highest in scope**: the internal ctor has ~50 test call sites (`UdpSetupQueueTests` 15, `UdpProxyCoordinatorLifecycleTests` 11, `UdpProxyCoordinatorTests` 9, `UdpReceiveResilienceTests` 4, plus benchmarks), and 6 internal diagnostics properties (`:104-125`) are read across `UdpSetupQueueTests`. All legitimate internal seams, but this is the largest internal seam area.
- **Smell — duplicated frame-size arithmetic**: `ReceiveWindowSize` = max + 22 + 1 (`:132-133`) while `Socks5UdpTransport` builds its send buffer as 6 + 16 + maximumFrameSize (`Socks5UdpTransport.cs` send path) and `NdisApiAbi.MaximumEthernetFrame` is the third constant. `DurableCaptureBundle.cs:177-181` documents the receive/send distinction, so the duplication is intentional — but the constants 22/6/16 are independent literals.
- **Adapter counts**: `IUdpProxyTransportFactory` 1 + 7, `IUdpProxyTransport` 1 + 2, `IUdpResponseSink` 1 + 2. Adversarial fakes (`UdpCoordinatorFakes.cs` 112 lines: `GatedTransportFactory`, `DelayedTransportFactory`, `FailingTransportFactory`, `CancellationAwareTransportFactory`, `CollidingAliasTransportFactory`, `MutableTimeProvider`).

### 3.9 `UdpSessionSetup` (internal) — Deep but coupling-heavy

**Interface**: internal class, 12-param ctor of which **5 are `Func`/`Action` delegates** back into the coordinator (`refreshSetupStamps`, `attachSession`, `dequeueForFlush`, `removeSlot`, `removeReceiveFailedSession`), and it binds to the coordinator's `UdpSessionSlot` type (`UdpSessionSetup.cs:47-73`). It encapsulates setup admission, dial-start re-stamping, association claim, session construction/attach/start, and flush (`:94-151`, `:179-204`).

- **Depth**: it hides a genuinely complex lifecycle; but the delegate fan-in means the "interface" is 12 names a caller must understand, and the module cannot be tested without a coordinator-like host (it has no direct tests; it is tested through the coordinator).
- **Seam**: the delegates are the seam; there is exactly **one** adapter (the coordinator). This is an internal seam that exists to avoid a circular type reference as much as to enable substitution.

### 3.10 `UdpProxySession` (internal) — Deep, direct-construction tests

**Interface**: 12-param ctor (`UdpProxySession.cs:53-85`); 12 public-on-internal members (`Flow`, `FlowGeneration`, `Association`, `LastActivityUtc`, `ClientMac`, `Start`, `SendAsync`, `SendSpanAsync`, `DisposeAsync`, `TryBeginExpiry`, `CancelExpiry`). Empirically hides the receive loop, skip classification, and activity throttling (`:221-282`, `:364-381`).

- **Smell — lifecycle ordering**: `Start` (`:99-105`) must follow construction and precede use; the ctor does not start the loop. This is a hidden temporal contract.
- **Testability**: `UdpProxySessionTests` (3) constructs the internal class directly with the full 12-param ctor. Fine as an internal unit test, but it is the only test that exercises the class outside the coordinator.
- **Members are `public` on an `internal` class**: callable only within the assembly today; the public modifier adds no leverage.

### 3.11 `Socks5UdpTransport` / `Socks5UdpTransportFactory` — Deep, mixed-parameter internal overload

**Interface**: `Socks5UdpTransportFactory.CreateAsync` (one-line pass-through to the internal static factory, `Socks5UdpTransport.cs:105-106`); `IUdpProxyTransport` 5 members; public static `Socks5UdpTransport.CreateAsync(server, selfTraffic, token)` (`:165-166`) is a 3-param wrapper over the **internal** 8-param overload (`:168-176`).

- **Depth**: high — send/receive/relay-source validation/truncation classification/connection-reset hardening are all behind a 5-member transport interface.
- **Smell — pass-through wrapper**: the public 3-arg `CreateAsync` exists for callers that bypass the factory, and tests use it heavily (`Socks5UdpTransportSendTests`, `Socks5UdpAssociateTests`, `Socks5UdpConnresetTests`, `UdpReceiveResilienceTests`); production always goes through the factory. It is a thin adapter over the internal overload.
- **Smell — mixed-parameter internal overload**: the 8-param internal `CreateAsync` mixes production parameters (`maximumFrameSize`, `addressCache`) with test-only ones (`createControl`, `socketFactory`, `disableUdpConnectionReset` — the last has a real production default but is injectable). Internal, so acceptable per R2, but it is a single signature serving two masters.
- **Testability**: internal statics `IsAcceptableRelaySource` (`:393`), `IsPossiblyTruncated` (`:404`), `ClassifyReceiveFault` (`:422`) are exercised directly by tests. These are pure functions and the tests are unit-level; legitimate internal seams.

### 3.12 `Socks5ControlConnection` — Deep, test-only public parameters

**Interface**: public static `ConnectAsync` with **8 parameters, 5 optional** (`Socks5ControlConnection.cs:59-67`); public instance `UdpAssociateAsync`, `ConnectDestinationAsync`, `DisposeAsync`. Behind it: retry loop, per-attempt timeout, scratch handshake buffer, loop-prevention socket hook, address caching.

- **Smell — test-only public parameters**: `resolveAddresses` (`:59`) and `socketFactory` (`:60`) are passed **only from tests** (grep for `resolveAddresses:`/`socketFactory:` returns zero production call sites). Production passes `onSocketReady` (loop prevention), `maxAttempts`/`perAttemptTimeout` (overridden by `TcpProxyRelayFactory`), and `addressCache`. Two of five optional parameters exist for testability on a public static method.
- **Internal seam**: `GetUpstreamStream` (`:238-246`) is used by both production (`TcpProxyRelayFactory`) and tests; legitimate.
- No other smells; the retry/handshake machinery is well hidden.

### 3.13 `NdisCapturePump` — Deep, options record is a plus

**Interface**: `NdisCapturedPacket`, `INdisPacketReader.TryReadPackets`, `NdisCapturePumpOptions` (6 optional members), `NdisCapturePump` ctor (4 params), `RunAsync`, `DisposeAsync`. Evidence: `NdisCapture.cs:22-42`, `:81-118`, `:145`, `:404`.

- **Good**: `NdisCapturePumpOptions` (`:36-42`) deliberately collapses "six former optional positional parameters" into a named record — exactly the right direction for optional-parameter proliferation. Keep.
- **Internal test hooks**: 6 (`RunIterationForTests` `:174` — a documented identical-loop-body seam; `PumpThread` `:166`; `TransientReadRetryCount` `:369`; `TransientReadIncidentCount` `:372`; `IsDegraded` `:375`; `LastDegradedNativeErrorCode` `:378`). `NdisCapturePumpTests` (13) uses `PumpThread`/`RunIterationForTests`; `NdisCaptureResilienceTests` (10) uses the four telemetry members.
- **Options members test-only**: `BatchCapacity` and `TransientRetryBaseDelay` are never set by production (`MultiAdapterCaptureLoop.cs:39-42` sets only `PollDelay`, `OnBatchCompleted`, `OnTransientRetry`, `OnDegraded`); they are test knobs inside a public options record.

### 3.14 `DurableCaptureBundle` — composition root, wide internal surface

Internal, 447 lines, 14 internal members, 0 public. Evidence: `DurableCaptureBundle.cs:25-78` (11-param internal ctor), `:101-149` `CreateAsync`, `:156-162` `RegisterPool`, `:258-276` `CreateUdpCoordinator`, `:278-300` `CreateTcpCoordinator`, `:314-357` `UpdateUdpTargets`, `:407-444` `DisposeCore`.

- **Depth**: it hides the entire wiring graph behind `CreateAsync`, which is real leverage. But it **knows and constructs every module** and exposes 14 internal members, so it is the god composition root. The 11-param internal ctor exists so `DurableCaptureBundleTests` (10 facts) can inject fakes; that is a legitimate internal test seam.
- **Notable**: `CreateTcpCoordinator` (`:278-300`) does **not** pass `framePool`, confirming that parameter is dead. Disposal ordering is documented in `DisposeCore` (sweeper → udp → pools → tcp → pools → executor).

### 3.15 `BoundedSetupQueue` / `UdpAssociationTable` — Deep with thin wrappers

- `BoundedSetupQueue` (137 lines, 7 public members): inline single-entry fast path plus lazy `Queue` (`BoundedSetupQueue.cs:45-105`); ownership/refusal semantics documented. `TryDequeue` has a 2-arg → 3-arg pass-through pair (`:70-72`).
- `UdpAssociationTable` (`UdpAssociations.cs`): `Claim` wraps `TryClaim` and throws (`:45-49`); a 4-arg `TryClaim` forwards to a 5-arg overload (`:51-52`). Pass-through overload chain, low leverage.
- Both are concrete leaves with 0 internal hooks; tests use the public API.

---

## 4. Cross-cutting findings

### 4.1 Test-only members on the product interface (PRD R2 scope)

| Member | File:line | Passed in production? | Notes |
|---|---|---|---|
| `RuntimeHeartbeat.gcSnapshotProvider` | `RuntimeHeartbeat.cs:72` | No (`Program.cs:303-311` omits it) | Tests only (`RuntimeHeartbeatTests.cs:125/212/238/283`) |
| `Socks5ControlConnection.ConnectAsync.resolveAddresses` | `Socks5ControlConnection.cs:59` | No | Tests only |
| `Socks5ControlConnection.ConnectAsync.socketFactory` | `Socks5ControlConnection.cs:60` | No | Tests only |
| `Socks5UdpTransport.CreateAsync` public 3-arg wrapper | `Socks5UdpTransport.cs:165-166` | No (factory used) | Tests only; pass-through of internal 8-arg overload |
| `TcpProxyCoordinator.framePool` | `TcpProxyCoordinator.cs:56` | No | Never passed by **any** caller (prod/test/bench) |

### 4.2 Internal seams (legitimate per R2 — do not remove)

- `NativeBufferPool.Release`, `NativeLease` internal fields, `NativeMemoryManager` — implementation.
- `FlowState` internal pooled ctor + `Reset` — used by `FlowTable`.
- `SetupExecutor.IsDisposed` — test observability of an internal state.
- `TcpProxyRelay` internal class/ctor, `RelayEndKind`, `ITcpRelayEndInfo`, retry/arm constants.
- `TcpProxyCoordinator` 9 internal hooks; `UdpProxyCoordinator` 6 diagnostics + internal ctor + `TrySendSpanAsync`; `NdisCapturePump` 6 hooks; `Socks5UdpTransport` 4 internal statics; `Socks5ControlConnection.GetUpstreamStream`; `UdpProxySession`/`UdpSessionSetup` internal classes; `DurableCaptureBundle` internal ctor/members.

The only internal seam that is *not* merely module-own-test is `Socks5UdpTransport.CreateAsync`'s 8-param overload, because it mixes production parameters with test-only ones; even so, it is `internal`.

### 4.3 Design smells with evidence

| Smell | Where | Detail |
|---|---|---|
| Interface parameter only ever default in production | `TcpProxyCoordinator.cs:56` (`framePool`) | Zero call sites pass it |
| Interface parameter only ever null in production | `RuntimeHeartbeat.cs:72` (`gcSnapshotProvider`) | Tests only |
| Interface parameters only ever provided by tests | `Socks5ControlConnection.cs:59-60` | `resolveAddresses`, `socketFactory` |
| Pass-through wrapper | `Socks5UdpTransport.cs:105-106`, `:165-166`; `UdpAssociations.cs:45-52`; `BoundedSetupQueue.cs:70-72` | Thin delegation with no added behavior |
| Hidden ordering constraint | `SetupExecutor.cs:216` (`item.Handler!`) | NRE on worker if `Handler` not set before `TryEnqueue`; `Reset` at `:231` forces re-population after recycle |
| Hidden temporal coupling | `FlowTable.cs:134-144` vs `:85`; `UdpProxySession.cs:99-105`; `UdpSetupQueueBudget` `DateTime.UtcNow.Ticks` | Internal `UtcNow` vs explicit `now`; `Start` after ctor; un-injected clock in budget rate-limit |
| Primitive obsession | `nint` adapter handles (`NdisCapture.cs:118`, `UdpAdapterTargetSource.cs:10`); `workerCount = 0` sentinel (`SetupExecutor.cs:99`); `long packetSequence`/`flowGeneration` (`UdpProxyCoordinator.cs:176`, `.Send.cs:21`) | Unit-less sentinels/primitives crossed through interfaces |
| Duplicated knowledge kept in sync by comment | `FlowKey` hash vs `TransportTuple` hash (`Domain.cs:97-105`, `FlowTable.cs:170-183`); ring capacity comment (`SetupExecutor.cs:75-76` vs `TcpPendingSynSetup.cs:48`); cooldown-table mirror (`UdpSetupCooldownTable.cs` vs `TcpResetCooldownTable.cs`) | No shared constant/assertion |
| Duplicated frame-size arithmetic | `UdpProxyCoordinator.cs:132-133` (22+1) vs `Socks5UdpTransport.cs` (6+16) vs `NdisApiAbi.MaximumEthernetFrame` | Intentional but unlinked literals |
| God composition | `DurableCaptureBundle.cs` | 11-param ctor, 14 internal members, constructs all modules |
| Wide constructors | `TcpProxyCoordinator.cs:46-58` (12), `UdpProxyCoordinator.cs:52-101` (12), `UdpSessionSetup.cs:47-73` (12), `UdpProxySession.cs:53-85` (12), `DurableCaptureBundle.cs:52-78` (11) | Hard to call without a harness |
| Duplicated ownership pattern | `TcpProxyCoordinator.cs:72,74`; `UdpProxyCoordinator` owns-flags; `DurableCaptureBundle` | `_ownsX` repeated |

### 4.4 Seam / adapter census

Real external seams (≥2 adapters): `ITcpProxyRelayFactory` (1+5), `ITcpRedirectListenerFactory` (1+6), `ITcpRedirectInjector` (1+3), `IUdpProxyTransportFactory` (1+7), `IUdpProxyTransport` (1+2), `IUdpResponseSink` (1+2), `INdisPacketReader` (1+2), `ITcpRelay` (1+4), `ITcpRedirectListener` (1+3), `ITcpAcceptedConnection` (1+1), `ITcpReverseHandler` (1+1).

Hypothetical (single adapter, no test fake): `ISetupExecutor` (1+0), `IUdpAdapterTargetSource` (1+0). Neither is harmful — `ISetupExecutor` is a genuine composition seam, and `IUdpAdapterTargetSource` decouples `UdpResponseReinjector` from the bundle's concrete snapshot type — but neither is validated by a substituting adapter in the test suite.

---

## 5. Prioritized recommendations

### [cleanup] — signature-level, behavior-preserving, fits this task

1. **Remove the dead `framePool` parameter** from `TcpProxyCoordinator` (`TcpProxyCoordinator.cs:56`) and reduce `_framePool` to the `NdisPacketBufferPool.Shared` assignment (`:70`). Grep proves no production, test, or benchmark caller passes it. Restores "one optional parameter = one real seam".
2. **Make `TcpProxyCoordinator.Table` internal** (`TcpProxyCoordinator.cs:83`). Only `TcpReversePrefilterTests`, `TcpProxyCoordinatorCapacityTests`, and `TcpFragmentHandlingTests` read it; production never does. Tests keep access via `InternalsVisibleTo`.
3. **Move `RuntimeHeartbeat.gcSnapshotProvider` off the public constructor** (`RuntimeHeartbeat.cs:72`). It is passed only by `RuntimeHeartbeatTests`. An `internal` seam (internal ctor overload or internal property) keeps the tests while removing a test hook from the product signature.
4. **Move `resolveAddresses`/`socketFactory` off the public `Socks5ControlConnection.ConnectAsync`** (`Socks5ControlConnection.cs:59-60`). Introduce an internal overload (mirroring the pattern `Socks5UdpTransport` already uses) so the public static method exposes only `onSocketReady`, `maxAttempts`, `perAttemptTimeout`, `addressCache` — all of which production actually passes.
5. **Collapse `Socks5UdpTransport`'s public 3-arg `CreateAsync`** (`Socks5UdpTransport.cs:165-166`) into the internal overload, or move the test-only `createControl`/`socketFactory`/`disableUdpConnectionReset` parameters into a separate internal factory method. Production always uses `Socks5UdpTransportFactory`; the public wrapper is a test-only pass-through.
6. **Decide the visibility of `ISetupExecutor`/`SetupWorkItem`/`SetupWorkKind`** (`SetupExecutor.cs:25-64`). `SetupWorkItem` is a public type with zero public members and `ISetupExecutor` has exactly one adapter; if a cross-assembly grep confirms the CLI/benchmarks only use the concrete `SetupExecutor`, demoting the interface and work item to `internal` removes an unusable public type. If any external caller exists, record as [follow-up] instead.

### [follow-up] — larger, behavior-neutral refactors (record per PRD R5; do not force)

7. **Encode the `SetupExecutor` ordering invariant** (`SetupExecutor.cs:216`): replace `item.Handler!` with an enforced contract (required-parameter factory or an explicit guard) so "Handler must be set before enqueue" is not an undocumented NRE.
8. **Replace `TcpProxyCoordinator`'s 12-param constructor + `_owns*` flags** (`TcpProxyCoordinator.cs:46-74`) with composition-owned dependencies or a `TcpRedirectOptions` record; the bundle already owns the pools and executor, so the coordinator's self-ownership path is only used by tests.
9. **Consolidate `UdpProxyCoordinator`'s dual constructors** (`UdpProxyCoordinator.cs:39-101`): one options-based ctor; make `beforeExpiryRecheck`/`setupQueueGlobalByteBudget` internal seams that do not duplicate the public parameter list.
10. **Introduce an internal slot-access seam for `UdpSessionSetup`** (`UdpSessionSetup.cs:47-73`) so its 5 delegate couplings become one interface with a fake, enabling direct tests without a live coordinator.
11. **Group `UdpProxySession`'s 12 constructor parameters** (`UdpProxySession.cs:53-85`) into a context record; make `TryBeginExpiry`/`CancelExpiry` internal (they are `public` only on an internal class).
12. **Consolidate coordinator internal diagnostics** (`TcpProxyCoordinator.cs:97-104,615-636`; `UdpProxyCoordinator.cs:104-125`) into a single internal diagnostics snapshot per module to shrink the internal seam area that tests depend on.
13. **Consolidate `NdisCapturePump`'s 6 internal hooks** (`NdisCapture.cs:166-378`) into one internal diagnostics record, and move the test-only `BatchCapacity`/`TransientRetryBaseDelay` options members to an internal test path.
14. **Inject a clock into `FlowTable`** (`FlowTable.cs:134-144`) and `UdpSetupQueueBudget` so `TryResolve`/`TryClaimResolved` and the drop rate-limiter stop reading `DateTimeOffset.UtcNow` while `RemoveExpired` takes `now`.
15. **Guard the `FlowKey`/`TransportTuple` hash mirror** (`Domain.cs:97-105`, `FlowTable.cs:170-183`) with a shared field-set test or extracted hashing, so the two equivalence relations cannot drift.
16. **Link `SetupExecutor.DefaultRingCapacity` and `TcpPendingSynSetupIndex.DefaultCapacity`** (`SetupExecutor.cs:75-76`, `TcpPendingSynSetup.cs:48`) through a shared constant or an assertion test; today the "matches the TCP pending-SYN index cap" claim is comment-only.
17. **Shrink `DurableCaptureBundle`'s surface** (`DurableCaptureBundle.cs:25-78`, 14 internal members) by extracting UDP/TCP wiring sub-builders; this is the one god-composition module.
18. **Validate or accept the single-adapter seams** `ISetupExecutor` and `IUdpAdapterTargetSource` (both 1+0): either add a substituting fake to prove the seam, or document them as composition-only seams.

---

## 6. What is healthy (explicitly no churn)

- **`NativeBufferPool` / `NativeLease` / `NativeMemoryManager`**: deep, documented invariants, 14 tests all crossing the public interface, zero internal test reaches.
- **`FlowTable` interface**: 6 members hiding pooled state reuse and a dual-orientation index; 18 tests across the public API.
- **`Socks5AddressCache`**: 5 members, 64 lines, fully public-interface-tested.
- **The external interface graph**: every `ITcp*`/`IUdp*`/`INdisPacketReader` seam is small relative to its implementation and is substituted by 2–8 adapters including adversarial/gated/barrier fakes (`TcpCoordinatorFakes.cs`, `UdpCoordinatorFakes.cs`). The seam *placement* is correct.
- **`NdisCapturePumpOptions` record**: the model for consolidating optional parameters (6 former positional parameters → one named record). Consider replicating this pattern rather than removing it.
- **`UdpProxyCoordinator.Send.cs` partial split**: explicitly behavior-zero, keeps the hot span path separate.
- **Internal seams used by a module's own tests** (per PRD R2): `NdisCapturePump.RunIterationForTests`/`PumpThread`/telemetry, `Socks5UdpTransport` internal statics, `SetupExecutor.IsDisposed`, `UdpProxyCoordinator.beforeExpiryRecheck`, `DurableCaptureBundle` internal ctor. These are legitimate and should be preserved.
- **`FlowState`/`TcpProxyRelay` internal classes**: hiding internal-only concepts behind `internal` while exposing narrow public interfaces is exactly right.

---

## 7. Caveats / not found

- Public-member counts in §2 are regex-derived (`^\s{4}public ` / `^public `) and include nested-type members; they are order-of-magnitude signals, not exact API surface. Files using block-scoped namespaces (`UdpAssociations.cs`) may be undercounted; the manual read in §3.15 is authoritative.
- "Adapters per seam" is derived from `: IFoo` implementation greps; a fake defined as a nested/private type could be missed, though the harness files (`TcpCoordinatorFakes.cs`, `UdpCoordinatorFakes.cs`) were read and account for the majority.
- No behaviour or performance measurement was performed; depth judgments are structural only.
- `UdpSessionSetup` has no direct test file; its behaviour was inferred from coordinator-path tests, so its testability verdict (3.9) is lower-confidence than the other modules.
