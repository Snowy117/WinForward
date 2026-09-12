# PRD — Dynamic pass-lane table sized from capture scope

Date: 2026-09-12
Owner: qmazon
Status: draft

## Background

`NdisPacketActionExecutor` accumulates Pass frames per (adapter handle, direction) lane in a fixed 8-slot table (`PendingLaneCapacity = 8`, i.e. 4 adapters × 2 directions). When the capture scope widens to every MSTCP-bound adapter (any unconstrained policy rule — `CaptureAdapterScopeResolver` fallback), machines with more than 4 NICs constantly overflow: Passes degrade to immediate single sends (rate-limited warn, `ImmediateSendLaneOverflowCount` increments), losing the batched-IOCTL syscall amortization (measured 16× reduction, spec `windows-ndisapi.md` §281/§286). The "production shapes are single-adapter" assumption baked into the fixed capacity does not hold for real deployments.

## Problem

Correctness is intact (frames send exactly once), but the overflow warning fires constantly on multi-NIC hosts and Pass throughput/CPU regress to per-packet IOCTLs for every adapter beyond the 4th.

## Requirement

Size the pass-lane table from the installed capture scope instead of a compile-time constant, so the degradation path becomes structurally unreachable for in-scope adapters.

- **R1**: At every scope install (`RetireLanesExcept` — already invoked by `DurableCaptureBundle.OnScopeInstalled` with the exact scope handle list, strictly between generations), rebuild the lane table sized `2 × scope.Count` (each adapter needs at most a send and a receive lane).
- **R2**: Keep the overflow path as a defensive backstop (immediate single send + counter + rate-limited warn) — it must remain correct if ever reached, but normal operation never reaches it.
- **R3**: Preserve all existing contracts: exactly-once reinjection and pool return; per-lane capture order; flush points (per pump iteration + loop exit); generation-scoped lane retirement semantics (rebuild subsumes retirement — the fresh table only ever holds live-scope keys); driver handle-value reuse safety; lock-free fast-path scan once a lane exists.
- **R4**: Empty scope → minimal table (no lanes can accumulate; interception paused), matching today's retire-all behavior.
- **R5**: Update the lane-overflow batching test to reflect that in-scope keys never overflow; overflow fallback is still exercised via the defensive path where constructible (or pinned as unreachable-by-design).
- **R6**: Update spec `.trellis/spec/backend/windows-ndisapi.md` §281/§286 to describe scope-sized lane capacity.

## Acceptance Criteria

- [x] With a scope of N > 4 adapters, Pass frames on all N adapters batch into lanes; `ImmediateSendLaneOverflowCount` stays 0 and no degradation warning fires.
- [x] Scope refresh that shrinks/grows the adapter set rebuilds the table accordingly between generations; stale lanes never leak into the new table.
- [x] A retired lane that still holds frames at rebuild time keeps today's fail-closed drop + warn + exactly-once buffer return.
- [x] All existing `NdisPacketActionExecutorBatchingTests` semantics (append order, independent flush, exactly-once pool return, empty flush no-op) still pass.
- [x] Full backend check (build + tests) green.

## Non-Goals

- No config knob / runtime parameter for lane capacity (scope-derived sizing makes it unnecessary).
- No change to batching scope (only Pass dispositions batch), flush points, or reinjector ABI.
- No scope-side policy changes (constraining `adapterNames` remains a user-side mitigation, not part of this task).
