# Design Review Synthesis — Cross-Area Findings & Discussion Agenda

> Meta: synthesized by the lead agent (qmazon session, 2026-09-08) from five sub-agent reports
> (see sibling files 01–05). Read this first, dive into area reports for file:line evidence.
> Review method: deep-module vocabulary (Ousterhout-derived: interface vs implementation, seams,
> adapters, deletion test) + audit against `.trellis/spec/backend/*` project conventions.

## Overall verdict

The codebase is **textbook-quality deep-module design** with high spec compliance:

- Dependency graph is a clean fixed DAG: `Cli → {Core, Configuration, Protocols, NdisApi, Runtime, Windows}`, `Runtime → {Configuration, Core, NdisApi, Protocols, Windows}` — no reversals found.
- Seams are overwhelmingly *real* (≥2 adapters incl. test fakes), hot path is allocation-disciplined, teardown ordering is structurally enforced (not convention), concurrency tests use barrier/TCS gating per spec.
- Build posture is serious: net10.0, nullable, TreatWarningsAsErrors, AOT/trim analyzers, 4 analyzer packs.
- Test baseline 463 tests, no tautological tests found; audit tests (WindowsBoundaryAuditTests, ProtocolAuditTests) turn spec invariants into executable assertions.

Issues found are localized, not systemic. They fall into three tiers below.

## Priority table

### P0 — behavioral / correctness risks (silent degradation or contract violations)

| # | Finding | Evidence | Area report |
|---|---------|----------|-------------|
| 1 | **Executor pass-lane slots leak across capture generations**: lanes keyed by (adapter handle, direction) are never removed; every adapter-list refresh mints fresh handles; after ~4 refreshes all 8 `PendingLaneCapacity` slots are dead → all passes permanently degrade to immediate single sends, **no log/telemetry** | `NdisPacketActionExecutor.cs:39,108-109`; `DurableCaptureBundle.cs:96` (executor built once per run); `AdapterEnumeration.cs:11-12` | 03 |
| 2 | **`IPAddress` class used on classify path** — violates hot-path spec contract 1 verbatim; non-flow frames (ARP/ND) are the high-frequency case on a gateway LAN | `PacketFlowClassifier.cs:40` (`Endpoint.From(IPAddress.Any, 0)`) | 03 |
| 3 | **`IPHelperTables.ReadTable` trusts `dwNumEntries` without cross-checking `4 + rowCount*sizeof(T) ≤ size`** before pointer deref — asymmetric with the NDISAPI seam's bounded discipline | `ProcessAttribution.cs:274-279` | 02 |
| 4 | **Pump `DisposeAsync` safe only by convention** — freeing batch buffers while `RunAsync` may still be mid-flight if caller disposes without cancelling; invariant not encoded in the type | `NdisCapture.cs:194-205`; `MultiAdapterCaptureLoop.cs:62-67` | 02 |
| 5 | `PassAsync` throws `ArgumentNullException(nameof(packet))` when actually `packet.Lease` is null — misleading diagnostics | `NdisPacketActionExecutor.cs:55` | 03 |

### P1 — spec violations / structural debt

| # | Finding | Evidence | Area report |
|---|---------|----------|-------------|
| 6 | **`UdpProxyCoordinator.cs` = 471 effective lines > 400 limit** (only src violator); six fused responsibilities. Concrete split proposal A–D ready (cooldown table ~60, setup budget ~65, setup pipeline ~150, logging ~40 → coordinator ~250) | measured; split plan in report 04 §3 | 04 |
| 7 | **`benchmarks/Stability/UdpBurstScenario.cs` = 437 effective lines > 400** (benchmarks bound by rule since 08-29); shares root cause with systematic gap: Stability lacks a `StabilityShared.cs` — `LatencyDistribution`/`CountingRuntimeLogger`/`InFlightTracker` duplicated vs UdpLossScenario | report 05 §5 | 05 |
| 8 | **`Domain.cs` / `PacketRuntime.cs` violate file-name = main-type convention** — Domain.cs holds 10 top-level types (FlowTable+TransportTuple ≈180 lines is the extraction candidate); PacketRuntime.cs houses unrelated roommate `BoundedSetupQueue` | report 01 §4 | 01 |
| 9 | **Unsanctioned cross-group edge UdpProxy→Socks5** — UdpProxy consumes SOCKS5 datagram codec types (`Socks5UdpDatagram` etc. defined in `Socks5/Socks5UdpTransport.cs`); sanctioned-edge set in directory-structure.md doesn't include it. Fix: either document the edge or move datagram codec to Protocols | `UdpProxyCoordinator.cs:5`, `UdpProxySession.cs:7` | 03 |
| 10 | **"Tombstone" naming drift** — TCP `Tombstones` = 60 s TIME_WAIT grace; UDP `_setupTombstones` = 1 s setup-failure cooldown. Same word, opposite contracts; specs cross-reference both | `TcpRedirectSessionStore.cs:42-46` vs `UdpProxyCoordinator.cs:33` | 04 |
| 11 | **Cli layer has zero tests despite `InternalsVisibleTo` configured** — `DurableCaptureBundle.UpdateUdpTargets` (zero-MAC policy, ~30 lines) and `Program.LogAdapterTransientRetry` (rate-limit CAS algorithm, 13 lines) untested; latter is business logic leaked into the entry point | `Program.cs:282-293`; `DurableCaptureBundle.cs:127-155` | 05 |

### P2 — dead surface / hygiene (mechanical cleanups)

| # | Finding | Evidence | Area report |
|---|---------|----------|-------------|
| 12 | `FlowTable` dead/duplicate public surface: `TryGet` zero callers (src+tests); `TryClaim` behaviorally ≡ `TryClaimResolved`; `Claim` test-only. Production uses only 3 of ~6 lookup methods | `Domain.cs:167,216,222` | 01 |
| 13 | Dead interface `IWindowsAdapterInventory` (declared, implemented, zero consumers — call sites bind concrete class); `Platform.cs` is a 5-type junk drawer | `AdapterIdentity.cs:6`; `Platform.cs` | 02 |
| 14 | Dead native surface: `GetDriverVersion`/`EnsureDriverVersion`, `ReadPacket`/`InterpretReadResult` — no product callers, test-referenced legacy of the single-packet era | `NdisApiAbi.cs:185-187,209-211`; `NdisNativeCallStatus.cs:9-13,28-33` | 02 |
| 15 | TestHelpers promotion incomplete: `FakeModes`/`CompletingCapture`/`BlockingCapture`/`ScriptedReader`/`NoopResponseSink` each duplicated private in 2 files; `NoSelfTraffic` ×3 alongside already-promoted `FakeGuard`; private `FakeExecutor` name-collides with TestHelpers' `FakeExecutor` (different behavior) | report 05 §4 | 05 |
| 16 | Config diagnostics: invalid array *values* report collection path, not `[i]` indexed path (null elements do it right) — spec error-handling.md asks for indexed paths | `ConfigurationModels.cs:355,368,383,390` | 01 |
| 17 | `ITcpAcceptedConnection` not substitutable at the real relay — `TcpProxyRelayFactory.EstablishAsync` hard-type-checks the concrete class; seam only supports fake relays | `TcpProxyRelay.cs:28-31` | 04 |
| 18 | `IUdpAdapterTargetSource` and `IFrameSource` have zero test fakes (one-adapter seams); pool double-`Dispose` doc overpromises vs re-rental reality | `NdisPacketBuffer.cs:8-11,145` | 02/04 |
| 19 | Misc readability: `NdisCapturePump` ctor 9 params (options-record candidate); `FlowKey.GetHashCode` omits Origin while `Equals` compares it (intentional, undocumented); `logLevel` dual validation with divergent messages; `NormalizePath` over-exposed | scattered | 01/02 |

## Strengths worth preserving (do not "fix" these)

1. `FlowDispatcher` warm/slow split, `ConfigurationLoader` (2 methods hiding ~330 lines of validation), `LayeredCaptureRunner` (RunAsync + SignalDegraded), `NdisApiDriver`/`NdisApiAbi` layout-asserted ABI isolation, `UdpResponseReinjector` (1-method interface hiding direction matrix).
2. Atomic-retire single-tombstone-write in `TcpRedirectSessionStore` with documented acyclic lock order.
3. Mirrored exactly-once budget discipline (UDP global setup budget / TCP pending-SYN byte budget).
4. Composition root honesty: Program.cs is CLI shell only; durable layer delegated to `DurableCaptureBundle`.

## Suggested discussion agenda (for deciding remediation scope)

1. **P0 triage**: #1 (lane leak) is the only one that silently degrades a production contract on long-running gateways — likely first. #2 is a one-line spec-compliance fix. #3/#4 are hardening. Do we take all five now or split?
2. **Structural remediation shape**: #6 + #7 + #8 are behavior-zero refactors with ready split plans — one "structure" child task or separate? Test baseline (463) must hold per quality-guidelines.md:26.
3. **Dead-surface sweep**: #12–#14 (+ #18 partially) — the project's own rule requires rg re-verification before deletion; cheap wins, maybe one mechanical PR.
4. **Spec updates needed regardless**: sanctioned-edge set (#9), tombstone naming (#10), TestHelpers promotion completion (#15), Cli test coverage (#11).
5. **Task-tree shape**: parent task with children per tier? Or sequential lightweight tasks? (Current task `09-08-design-review-remediation` can become the parent.)
