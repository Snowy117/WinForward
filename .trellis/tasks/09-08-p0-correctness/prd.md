# P0: fix correctness risks from design review

Parent: `09-08-design-review-remediation` · Evidence: `../09-08-design-review-remediation/research/00-synthesis.md` (P0 table) + `03-runtime-root-capture.md`, `02-ndisapi-windows.md`

## Goal

Fix the 5 behavioral risks found by the 2026-09-08 design review: executor pass-lane leak,
IPAddress on classify path, ReadTable bounds, pump DisposeAsync, wrong ArgumentNullException param.

## Requirements

### R1 — Executor pass-lane slot leak across capture generations (highest priority)

- Problem: `NdisPacketActionExecutor` lanes keyed by (adapter handle, direction) are never removed
  (`NdisPacketActionExecutor.cs:108-109`, `PendingLaneCapacity=8` at :39); the executor is durable
  across generations (`DurableCaptureBundle.cs:96`) but adapter handles are rebuilt fresh on every
  adapter-list refresh (`AdapterEnumeration.cs:11-12`). After ~4 refreshes all 8 lanes are dead →
  all passes permanently take the immediate single-send path — silently (no log/event;
  `PendingPassCount` internal and unpublished).
- Fix (choose in design.md): clear/reset lanes when a generation stops, OR notify the executor of
  scope changes (pattern: `UpdateUdpTargets`).
- Observability is part of the fix, not optional: lane-exhaustion degradation AND the >4-NIC
  capacity overflow (`NdisPacketActionExecutor.cs:29-31`) must emit an observable signal
  (rate-limited event/log per logging spec).
- Regression test: simulate ≥5 adapter refresh generations with a fake driver; assert batching
  still engages after the churn and the degradation signal fires when forced.

### R2 — `IPAddress` class on the classify path

- `PacketFlowClassifier.cs:40` uses `Endpoint.From(IPAddress.Any, 0)` (class-conversion overload,
  `Domain.cs:51`) — violates hot-path.md contract 1 verbatim. Replace with the `IPAddressValue`
  IPv4-any constant; keep `Endpoint` construction value-based.
- Verify no allocation regression on the non-flow path (ARP/ND-heavy shape); existing dispatcher
  benchmarks gate the warm path.

### R3 — `IPHelperTables.ReadTable` bounds cross-check

- `ProcessAttribution.cs:274-279` dereferences rows at `buffer + 4 + index*sizeof(T)` trusting
  `dwNumEntries` read from the buffer itself. Add the symmetric NDISAPI-style bound:
  `4 + rowCount*sizeof(T) ≤ allocated size` before any deref; fail closed (treat as no entries /
  actionable error) on violation.

### R4 — Pump `DisposeAsync` misuse-proofing

- `NdisCapture.cs:194-205` + `MultiAdapterCaptureLoop.cs:62-67`: freeing batch buffers while
  `RunAsync` may still be in flight is only prevented by caller convention. Encode the invariant:
  await loop exit inside `DisposeAsync` (bounded by cancellation) or make disposal defensive;
  document the contract either way.

### R5 — `PassAsync` exception names wrong parameter

- `NdisPacketActionExecutor.cs:55`: `packet.Lease is null` throws `ArgumentNullException(nameof(packet))`.
  Correct the parameter/exception so diagnostics point at the actual null (lease), matching the
  boundary-null conventions in quality-guidelines.md.

## Constraints

- Every fix ships with a deterministic regression test (barrier/TCS gating where concurrent, per
  quality-guidelines.md:20 — no scheduler-timing tests).
- Hot-path fixes (R1, R2) must not regress the recorded allocation gates (hot-path.md).
- Record before/after `dotnet test` totals in this task; baseline is the count at task start.

## Acceptance criteria

- [x] R1: refresh-churn regression proves batching survives multi-generation adapter lists;
      degradation signals observable for lane exhaustion and NIC-count overflow.
      (`RefreshChurnKeepsPassBatchingAliveAcrossGenerations` — 5 generations × 2 adapters,
      10 distinct handles, final generation still batches, overflow count 0; overflow branch
      has `ImmediateSendLaneOverflowCount` + 5 s rate-limited warn. Independently verified by
      trellis-check: PASS.)
- [x] R2: no `IPAddress` (class) remains in classify/flow-key code paths (rg-verified);
      warm-path allocation gates unchanged. (Linux dev box skipped benchmarks per implement.md
      batch 6 — change strictly reduces work; noted.)
- [x] R3: ReadTable rejects inconsistent size/count inputs without deref (unit test with
      crafted buffers). (`IPHelperTablesBoundsTests`, 6 cases; wired at all 4 Read* call sites.)
- [x] R4: DisposeAsync documented/encoded safe against mid-run disposal; test covers
      dispose-without-cancel shape. (`DisposeDuringRunWaitsForTheRunLoopToExit`,
      `DisposeBeforeAnyRunCompletesSynchronously`.)
- [x] R5: exception carries the correct parameter name. (9/9 sites via
      `CapturedFlowPacketGuards.ThrowLeaseRequired()`, ParamName "packet.Lease"; documented
      deviation from design.md D5's inline throw — analyzers MA0015/S3928 reject member-path
      paramNames, centralized guard with scoped suppressions is the minimal compliant form.)
- [x] Full build zero warnings; all tests green; new totals recorded.
      (Baseline 555 → final 569/569; +14 tests: batch2=1, batch3=6, batch4=2, batch5=5.
      Commits: 8c6a500, 6bfe27b, e44d419, d40243d, a9a8735 on branch `p0-correctness`.)

## Notes

- Complex task: requires `design.md` (R1 approach choice) + `implement.md` (ordered checklist with
  validation commands) before `task.py start`.
