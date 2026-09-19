# PRD: Audit and remove compatibility APIs; review module design health

Task: `.trellis/tasks/09-19-compat-api-cleanup`
Base commit: `0463b9b` (master)

## Problem

The repository contains numerous **compatibility APIs**: members, overloads, optional
parameters, public wrappers, and test-only hooks that exist so existing tests/callers did not
have to change when the production path moved on. They inflate the interface, blur the
production/test seam, and preserve dead code. The user asked to remove them and to review
whether the module design is healthy.

Two research artifacts back this work:

- `research/compat-inventory.md` — 62 grouped candidates with caller evidence and a disposition
  each (REMOVE / REPLACE-WITH-CLEANER-SEAM / KEEP / FOLLOW-UP).
- `research/design-health.md` — deep-module assessment (depth, seam placement, adapters,
  testability) plus 6 `[cleanup]` and 18 `[follow-up]` recommendations.

## Goal

1. Delete the compat APIs that are provably dead or test/benchmark-only, rewriting tests to
   cross the production seam instead of the compatibility one.
2. Move test-only parameters off public production signatures (internal overloads or named
   options records), keeping the capability but removing it from the product interface.
3. Keep behaviour identical: zero-warning build, full test suite green, allocation gates and
   soak contracts unchanged.
4. Land the design-health review as a task artifact and route the larger refactors to a
   follow-up task rather than silently doing them here.

## Scope

In scope (by inventory id):

- **M1 Dead members (zero callers repo-wide):** A1–A14, including the now-dead counter fields
  behind A1–A3.
- **M2 Test/benchmark-only allocating wrappers:** B1, B3–B8, B10, B12, B13, B17, B18, B19.
- **M3 Test-only parameters on public signatures:** `TcpProxyCoordinator.framePool`,
  `TcpProxyCoordinator.Table`, `RuntimeHeartbeat.gcSnapshotProvider`,
  `Socks5ControlConnection.ConnectAsync(resolveAddresses, socketFactory)`,
  `WindowsProcessAttributor.retryDelay`, `TcpRedirectSession(flowGeneration = 0)`,
  `AdapterTransientRetryLogGate.ticksProvider`, and the `ISetupExecutor`/`SetupWorkItem`
  visibility decision.
- **M4 Benchmark-only diagnostic surface:** B11 (`NativeBufferPoolStats`),
  B16 (`SetupExecutor` counters), B20 (`Socks5UdpReceiveResult` factories),
  B21 (`AdapterSelector`).
- **M5 Memory send chain (largest compat surface, B9):** remove
  `UdpProxyCoordinator.TrySendAsync` / `UdpProxySession.SendAsync` /
  `IUdpProxyTransport.SendAsync` / `Socks5UdpTransport.SendAsync`; make the span path the only
  transport send seam and rewrite the test/benchmark callers.

Out of scope (recorded as follow-ups in `research/design-health.md` §5 items 7–18):

- Constructor/ownership refactors (12-param ctors, `_owns*` flags, options records).
- Clock injection (`FlowTable`, `UdpSetupQueueBudget`), hash-mirror guard, shared-capacity
  constants, `DurableCaptureBundle` decomposition, single-adapter seam validation.
- Behavioural or performance changes.

Documented KEEP decisions (do not churn): internal seams used by the owning module's own tests
(inventory §C), production-consumed members, and `PacketChecksums.TryRewriteUdpEndpoints` /
`TryRewriteTcpEndpointsFullRecompute` (test oracles; add a doc note to the former).

## Requirements

- R1: Every removal must cite caller evidence (inventory) — no guessing.
- R2: Internal seams used by the owning module's own tests are legitimate and stay.
- R3: Behaviour is unchanged; no public semantics change beyond visibility/removal of unused
  surface.
- R4: Tests may be rewritten to the production seam. A test is only deleted when the unit under
  test is itself deleted as dead code, and each deletion is listed with justification.
- R5: Larger refactors are recorded, not forced.
- R6: The design-health review uses the shared vocabulary (module, interface, seam, adapter,
  depth, leverage, locality) and is actionable (file:line + recommendation).

## Acceptance Criteria

- AC1: `research/compat-inventory.md` lists every candidate with file:line, caller evidence,
  and disposition. (satisfied)
- AC2: All in-scope removals/replacements are applied; `dotnet build -c Release` is
  zero-warning and `dotnet test -c Release` is fully green (baseline 724; see R4 for the only
  permitted decrease).
- AC3: `research/design-health.md` exists with prioritized findings and in-scope vs follow-up
  routing. (satisfied)
- AC4: For every in-scope candidate, either the symbol is gone, or the inventory/`design.md`
  records why it stays. No production member is reachable only from tests/benchmarks unless
  documented.
- AC5: The allocation gates and gc-soak contracts from the previous task still pass unchanged.
- AC6: No public API gained a parameter solely to preserve a test's shape.

## Decisions

- D1 (user): remove compat APIs rather than keep them; test rewrites are acceptable.
- D2 (user): the design-health review is part of this task; larger refactors are surfaced, not
  silently performed.
- D3 (agent, review gate): the B9 memory send chain is in scope because production never uses
  it and it survives only on test/benchmark call sites; if it destabilizes, it is split into
  its own task.

## Status (2026-09-19)

All milestones M1–M5 implemented and adversarially checked (two check passes: M1–M4, then M5).

- **Build/tests:** `dotnet build -c Release` 0 warnings; `dotnet test -c Release` **721/721**
  green. Baseline was 724; the permitted decrease (PRD R4) is **3 tests of deleted dead code**:
  1 `AdapterSelector` fact (type deleted) + 2 `UdpRelayTests` byte-identity theory cases (the
  allocating `UdpFrameBuilder.TryBuild` oracle deleted). The 10 live `WindowsAdapterInventory`
  facts that the first check found accidentally deleted with `AdapterSelectorTests.cs` were
  recovered assertion-for-assertion into `WindowsAdapterInventoryTests.cs`.
- **Grep gate:** every removed M1/M2/M5 symbol returns zero hits across `src tests benchmarks`.
- **Contracts:** allocation gates (span-only UDP gate), `WarmSyncSendAllocatesNoManagedBytes`,
  pool balance, heartbeat, and gc-soak assertions unchanged and green; gc-soak 20 s smoke exit 0
  with zero overflow/outstanding deltas.
- **Specs updated:** `hot-path.md` (span-only send seam, gate wording) and `udp-relay.md`
  (`SendSpanAsync` signature/test names, encode-seam note). Inventory B9 disposition → REMOVED.
- **Routed follow-ups:** design-health report §5 items 7–18 are recorded in the follow-up task
  `.trellis/tasks/09-19-design-deepening-refactors`; `ISetupExecutor`/`SetupWorkItem` stay
  public because they appear on public coordinator ctor signatures (deferred to that task).
- **KEEP decisions honored:** inventory §C internal module-owns-test seams untouched;
  `PacketChecksums.TryRewriteUdpEndpoints` kept with a doc note.
