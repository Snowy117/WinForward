# Design Review — Cli composition + tests organization + benchmarks

> Sub-agent report (explore), 2026-09-08. Read-only review; no files modified.
> Specs consulted: .trellis/spec/backend/directory-structure.md (TestHelpers organization, fake
> promotion rules, benchmark file-size rule), .trellis/spec/backend/quality-guidelines.md
> (testing requirements).

## 1. Composition root verdict: mostly clean assembly, two localized logic leaks

**`Program.cs` (340 lines, `src/WinForward.Cli/Program.cs`) structure** — clearly layered:

- L19-64 `Main`: command dispatch + usage/exit codes (2)
- L66-88, L304-328 `TryLoadConfig`/`Validate`: config loading & diagnostic printing
- L90-153 `ListAdapters`/`RunCaptureAsync`: NDISAPI exception → exit-code mapping (1/3), CLI presentation policy
- L155-176 `RunInterceptionAsync`: startup preflight (empty enumeration → 3, selector errors → 1; comments L159-162 state this is exit-code preflight, the runner redoes it)
- L178-240 `RunCaptureLoopAsync`: **the core assembly point**
- L242-274 `RunUntilCancelledAsync`: Ctrl+C/CancellationToken shell

**Assembly volume**: `RunCaptureLoopAsync` directly constructs 8 objects (driver, SelfTrafficRegistry, NdisPacketReinjector, HighResolutionTimerScope, bundle, watcher, generation factory, runner), plus `DurableCaptureBundle.CreateAsync` internally builds 13 more (redirect table, TCP coordinator + 5 collaborators, UDP coordinator + 3, executor, dispatcher, sweeper, target source) and the runner's 2 callback closures — **~25 constructions across 2 files**. This is not "god composition": Program delegates the durable layer wholesale to `DurableCaptureBundle.CreateAsync` (Program.cs:197), keeping only the CLI shell. The layering is healthy.

**Verdict: wiring-only as the rule, two real leaks**:

1. **`LogAdapterTransientRetry` (Program.cs:282-293)** — a 5-second rate-limited Interlocked CAS window algorithm, 13 lines of business logic (rate-limit semantics) living in the entry point, with `rg` over tests/ showing zero test coverage (the `onTransientRetry` pump seam itself is tested, NdisCaptureResilienceTests.cs:142, but the rate-limiting function is not). Should sink into Runtime (next to `RuntimeLogging`) and gain tests.
2. **`EnumerateAdapters` (Program.cs:296-302)** — news up a `WindowsAdapterInventory` per call with an inline 4-field lambda projection, a hidden mapping function; the duplicate enumeration vs the runner is intentional design (comment L159-162), but the projection closure could be extracted.

The rest (exit-code catch matrix, Console I/O, preflight) is the CLI shell's legitimate job. **Conclusion: passing, not god composition.**

## 2. `DurableCaptureBundle`: deep module, clear responsibility

`src/WinForward.Cli/DurableCaptureBundle.cs` (186 lines):

- **Positioning** (L13-22 class comment): built once per run, survives across adapter refreshes — redirect table, both proxy coordinators, dispatcher/executor chain, idle sweeper, refreshable UDP snapshot; per-generation state belongs to `LayeredCaptureRunner`.
- **Deep**: constructor exception-safety done right — nested try/catch reverse-order release (L106-116: dispatcher failure → drop UDP first; L112-116: UDP failure → drop TCP), matching the quality-guidelines acquisition-order-release contract.
- **`DisposeAsync` single-flight (L157-164)**: `_disposeTask ??=` + Lock, consistent with the coordinator single-flight spec; ordered sweeper→UDP→TCP (L166-183), matching R-1.
- **Single-source constants** (L84-87 comment + L87): `NdisApiAbi.MaximumEthernetFrame` uniformly bounds transport/reinjector/ABI buffer interfaces.
- **One piece of business logic**: `UpdateUdpTargets` (L127-155) zero-MAC filtering/fallback/empty-scope clearing policy (~30 lines) — cohesion inside the bundle is defensible, but **zero tests repo-wide** (Cli has `InternalsVisibleTo("WinForward.Core.Tests")`, WinForward.Cli.csproj:12, yet no test references `DurableCaptureBundle` at all).

## 3. Test organization

**Discipline enforcement**: 76 .cs files = 61 test files + 15 TestHelpers. 61/61 filenames end in `Tests`, theme-named (e.g. `TcpProxyCoordinatorLifecycleTests`); 60/61 strictly one public test class per file (sole exception `NdisCaptureResilienceTests.cs` with 2 public classes). Zero naming-convention violations.

**Production seam → fake mapping table**:

| Production seam | Test fake | Location |
|---|---|---|
| `IPacketReinjector` | FakeReinjector (dual flag planes + batch snapshot), CountingReinjector | TestHelpers/PacketReinjectorFakes.cs |
| `IAdapterEnumerationProvider` | FakeAdapterEnumerationProvider | CaptureRunnerFakes.cs |
| `ICaptureGeneration(Factory)` | FakeCaptureGeneration(Factory) | CaptureRunnerFakes.cs |
| `IAdapterListChangeSource` | FakeAdapterListChangeSource | dedicated file |
| `IAdapterModeController` | FakeModes ×2 (**not promoted**, private nested) | CaptureLifecycleTests:104, NdisCaptureResilienceTests:358 |
| `IPacketCaptureLoop` | Completing/Blocking/BlockingDispose/FakeCapture ×2 (**not promoted**) | same two files |
| `INdisPacketReader` | ScriptedReader/PerHandle/PermanentFailure/Cancelling/Finite (**not promoted**) | 3 files |
| `IRuntimeLogger` | RecordingRuntimeLogger | RecordingLogger.cs |
| `ISelfTrafficGuard` | FakeGuard (promoted) + NoSelfTraffic/NeverSelfTrafficGuard private duplicates ×3 | RuntimeLoggingTests:142, NdisCaptureResilienceTests:314, TcpReversePrefilterTests:235 |
| `IPacketActionExecutor` | FakeExecutor/ThrowingPassExecutor (promoted) + Recording/Noop/Counting/same-name FakeExecutor private ×3 | FlowDispatcherTests:176 etc. |
| `IProcessAttributor` | FakeAttributor | CapturePipelineFakes.cs |
| `IAdapterLocalAddressProvider` | FakeLocalAddressProvider | AdapterFakes.cs |
| `ITcpRedirectListenerFactory` | Fake/Barrier/Gated/Single/Parking/Throwing ×6 | TcpCoordinatorFakes.cs |
| `ITcpRedirectListener` | FakeListener/Throwing/ParkingDispose | TcpCoordinatorFakes.cs |
| `ITcpAcceptedConnection` | FakeAcceptedConnection | same |
| `ITcpProxyRelayFactory`/`ITcpRelay` | Fake/Gated/Completable/Throwing | same |
| `ITcpRedirectInjector` | FakeInjector/ThrowingRedirectInjector | TcpCoordinatorFakes.cs + CapturePipelineFakes.cs |
| `ITcpReverseHandler` | TablePrefilter/RecordingReverseHandler (private, justified in place) | 2 test files |
| `IUdpProxyTransport(Factory)` | Fake + Gated/Delayed/Failing/CancellationAware/CollidingAlias/ImmediateFault | UdpTransportFakes.cs + UdpCoordinatorFakes.cs |
| `IUdpResponseSink` | FakeResponseSink (promoted) + Noop/Throwing private ×3 | Socks5UdpAssociateTests:250 etc. |
| `TimeProvider` | MutableTimeProvider | UdpCoordinatorFakes.cs |

**Seams with no fake / no test coverage**:
- `IFrameSource` (Core/PacketRuntime.cs:18) — sole implementation NdisPacketBuffer, zero references in tests/;
- `IWindowsAdapterInventory` (Windows/AdapterIdentity.cs:6) — zero fakes, inlined wrapper at Program.cs:298;
- **The Cli assembly as a whole**: `DurableCaptureBundle.UpdateUdpTargets` and `LogAdapterTransientRetry` untested (`InternalsVisibleTo` configured but unused).

**InternalsVisibleTo usage** (`Windows/Properties/AssemblyInfo.cs:3`, `NdisApiAbi.cs:5`, `Protocols.csproj:10`, `Cli.csproj:12`, `Runtime.csproj:7-8`): Runtime internals (LayeredCaptureRunner, TombstoneTable, PendingSynSetup, NdisAdapterGateMap, UdpProxySession, AdapterEnumerationDiff…) all have dedicated test files; **only Cli's internals are entirely unconsumed**.

**Tautological-test scan**: no `Assert.True(true)`; 23 `Assert.NotNull` occurrences are all dereference preconditions followed by substantive assertions (e.g. TcpProxyCoordinatorRewriteTests.cs:30 claimed→subsequent field assertions). ProtocolAuditTests' constant assertions (`IPHelperAbi.UdpTableOwnerPid==1`, WindowsBoundaryAuditTests.cs:12) are ABI wire-format pins — delete the feature and the test fails — not tautologies. **No tautological smells found.**

## 4. TestHelpers hygiene

**Good**: all 15 files use `namespace WinForward.Core.Tests` (no `.TestHelpers` suffix), all `internal`; fakes are behavioral supersets with design comments (FakeReinjector dual flag planes PacketReinjectorFakes.cs:7-12; FakeListenerFactory port increment + fixedTuple/throwOnCreate TcpCoordinatorFakes.cs:160); CaptureRunnerFakes provides a full `CaptureRunnerHarness` (ordered event seam L130-215, matching quality-guidelines' ordered-event-seam requirement).

**Problems (violating "≥2 files duplicated → promote to TestHelpers", directory-structure.md:82-83)**:
1. **Incomplete promotion**: `FakeGuard` (never-owns guard) is already in TestHelpers, yet 3 files keep behaviorally identical private `NoSelfTraffic`/`NeverSelfTrafficGuard`;
2. `FakeModes`, `CompletingCapture`/`BlockingCapture`, `ScriptedReader` each privately duplicated in 2 files, unpromoted (NdisCaptureResilienceTests vs CaptureLifecycleTests overlap is heaviest);
3. `NoopResponseSink` ×2 private duplicates (FakeResponseSink already exists);
4. **Name collision**: FlowDispatcherTests.cs:176 private `FakeExecutor` vs TestHelpers' `FakeExecutor` — same name, different behavior, easy to misuse.

## 5. Benchmarks organization

**Perf/**: 12 benchmark classes one-class-per-file + `BenchmarkShared.cs` (198 lines, containing frame builder/flow key/guard/executor/logger/transport fakes) — fully compliant (directory-structure.md:58).

**Stability/**: scenario-per-file (6) + infrastructure (SoakRunner/SoakOptions/StabilityContext) + shared servers (LoopbackSocks5Tcp/UdpServer, EchoReceiver with `DatagramHeader`). `Program.cs` (14 lines) dual-mode dispatch `--stability` vs BDN switcher.

**Verdict**:
- `UdpBurstScenario.cs` (437 effective lines) **is an outlier, not a systemic problem** — runner-up LoopbackSocks5UdpServer 282, TcpEofScenario 266, all others <290.
- But it **also exposes a systemic gap**: `LatencyDistribution`, `CountingRuntimeLogger`, `InFlightTracker` are privately duplicated between UdpLossScenario.cs and UdpBurstScenario.cs (UdpBurstScenario.cs:294-410 comments even self-admit "mirroring UdpLossScenario's pattern" L36-37). Stability lacks a shared fixture corresponding to Perf/BenchmarkShared.
- Minor: `BenchmarkShared.cs` lives in the `Perf/` namespace but is referenced by Stability (`using WinForward.Benchmarks.Perf;` UdpBurstScenario.cs:4) — shared fixtures should move up to the project root.

**UdpBurstScenario split proposal** (per spec precedent "nested-class promotion", zero behavior change):
1. `Stability/StabilityShared.cs` (~120 lines): `LatencyDistribution` + `Percentile`/`TicksTo*` math cluster + `CountingRuntimeLogger` + `ProductEventNames`/`BuildProductEvents` — deduplicated in sync with UdpLoss;
2. `Stability/UdpBurstInstrumentation.cs` (~180 lines): `BackgroundWindow` enum + `InFlightStamp` + `InFlightTracker` + `BackgroundSender` + `BurstCountingSink` + `PhaseOutcome`/`BurstResult`;
3. Main file keeps ~150 effective lines of orchestration. All three files well under 400.

## 6. WindowsBoundaryAuditTests + ProtocolAuditTests

Not architecture tests (no type/namespace reflection assertions) but **protocol & ABI audits**: WindowsBoundaryAuditTests pins IP Helper ABI projection semantics (table id constants, IPv6 scope id order preservation, network-order port decoding); ProtocolAuditTests pins the parser/rewriter malicious-input matrix — short-segment rejection without throwing (L18-28), IPv6 short payload rejection with **bytes unchanged** (L39-51), reserved fragment bit 0x80 consistently rejected by both parsers (L54-69), SOCKS5 reply illegal fields (L72-85), 131KB overflow folding (L88-94), frame builder over-length rejection (L97-105). They are the executable form of quality-guidelines.md:14-16 invariants. **Assessment: small and sharp, worth keeping; if real architecture tests are ever added (e.g. dependency-direction Cli→{Core,…} assertions) they should go in a separate file.**

## Summary

**Top 3 strengths**:
1. **Honest composition-root layering** — Program is CLI shell only, the durable layer sinks into DurableCaptureBundle (exception-safe construction + single-flight ordered release); ~25 assembled objects do not constitute god composition;
2. **Very high seam→fake coverage with high fake quality** — 18 of 21 production seams have fakes, behavioral supersets + event-seam harness, concurrency tests gated by barrier/TCS rather than scheduler timing (TcpCoordinatorFakes.cs:183-187 is exactly the shape quality-guidelines.md:20 requires);
3. **Audit tests turn spec invariants into executable assertions** — reject-without-mutating byte-level verification, dual-parser fragment-bit consistency, no tautological tests.

**Ranked issues**:
1. **Cli layer has zero tests**: `DurableCaptureBundle.UpdateUdpTargets` (zero-MAC policy) and `LogAdapterTransientRetry` (rate-limit algorithm) uncovered despite `InternalsVisibleTo` configured — logic leak + coverage gap as a dual problem;
2. **Stability shared fixture missing**: UdpBurstScenario over-limit (437) and the three-class private duplication vs UdpLossScenario share one root cause; extracting StabilityShared solves both;
3. **TestHelpers promotion migration incomplete**: `FakeModes`/`ScriptedReader`/`CompletingCapture`/`BlockingCapture`/`NoopResponseSink` each privately duplicated in 2 files unpromoted, `NoSelfTraffic` ×3 coexisting with promoted `FakeGuard`, `FakeExecutor` name collision — one mechanical cleanup pass converges it all.
