# TCP redirect in-place rewrite + lane-batched injection (research F1)

Parent: `08-30-proxy-perf-stability`. Source finding: F1 of
[research.md](../../archive/2026-09/09-29-tcp-udp-path-structural-perf/research.md).

## Goal

Remove the two structural per-packet costs of the TCP local-redirect data path — a native buffer
rent plus full-frame copy, and one user→kernel injection IOCTL per frame — by rewriting redirect
frames in place on the capture slot and injecting them through per-(adapter, target direction)
lanes flushed once per pump iteration.

User value: every proxied TCP byte in both directions crosses this path today, so its per-packet
cost is the product's throughput ceiling. The pass path already batches its reinjections
(08-30-batched-ioctls); the client-facing redirect legs were left out of that task on a
traffic-map row that described only the relay legs.

## Background (confirmed facts, re-verified against source at planning time)

- The forward leg (client → local listener), including mid-flow data, rents a pooled native buffer,
  copies the whole frame into it, rewrites the copy, injects it as one packet, then disposes:
  `TcpProxyCoordinator.ReinjectExistingFlowDataAsync` (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:336-376`).
  The reverse leg (proxy → client) has the identical shape: `HandleReverseAsync` /
  `InjectReverseFrameAsync` (`TcpProxyCoordinator.cs:424-468`).
- Both legs are required by the redirect contract, not incidental: after the handshake, client data
  on the original flow must be rewritten to the proxy tuple and reinjected, and the reversed reply
  must be rewritten back (`.trellis/spec/backend/tcp-local-redirect.md`, "Mid-flow data" and
  "Reverse path"). So the upload and download bytes of every proxied connection ride this path.
- One injection = one `DeviceIoControl`: `TcpRedirectInjector.Inject`
  (`src/WinForward.Runtime/TcpRedirect/TcpRedirectInjector.cs:26-36`) →
  `NdisApiDriver.SendPacketToMstcp/SendPacketToAdapter`
  (`src/WinForward.NdisApi/NdisApiDriver.cs:219-243`). The batched ABI already exists and is in
  production on the pass path (`NdisApiDriver.cs:254-262`;
  `NdisPacketActionExecutor.FlushLane`, `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:201-222`).
- The pump contract already provides what batching needs: batch slots stay stable for the whole
  iteration, each handler is awaited before the next slot is touched
  (`NdisCapturePump.InvokeHandler`, `src/WinForward.NdisApi/NdisCapture.cs:327-346`), and the
  batch-completed callback runs after the dispatch loop, on empty polls, **and** at loop exit while
  the batch buffers are still valid (`NdisCapture.cs:206,290-322`).
- The redirect legs' native frame lease is never materialized: the only `IsMaterialized` consumer is
  the pass executor (`NdisPacketActionExecutor.cs:76`), and the only `Lease.Frame` readers are the
  SYN setup path (`TcpRedirectSetup.cs:118`), the pass pooled fallback, and the no-capture-buffer
  fallback in `FlowDispatcher.InspectionSpan` (`src/WinForward.Runtime/FlowDispatcher.cs:47`). So an
  in-place rewrite on the capture slot is available to both data legs.
- Measured platform context: per-IOCTL cost is the structural Windows gap, not managed CPU
  (2026-08-30 VM data: `TcpRelay` 12.4× at chunk=1 → 2.9× at 8192). Windows loopback tops out near
  4.7k pps, so an end-to-end pps gain cannot be measured on the current harness
  (`benchmarks/results/2026-08-30-windows-vm/README.md`).
- Correction to the earlier artifact: the `08-30-batched-ioctls` injection-scope table lists
  "TCP mid-flow relay — userland sockets, no injection — out". That row describes the relay legs
  (proxy ↔ real server, which are userland sockets); the client-facing redirect legs do inject
  mid-flow data per packet and belong in batching scope.

## Requirements

- **R1 — Lane-batched data-leg injection.** Frames produced by the two mid-flow data legs
  (`ReinjectExistingFlowDataAsync`, `HandleReverseAsync`) accumulate in per-(adapter handle, target
  direction) lanes and leave in one batched reinjection call per lane per pump iteration, instead of
  one IOCTL per frame. Flush points match the pass lanes: end of the dispatching iteration, empty
  polls, and loop exit.
- **R2 — In-place staging on the capture slot.** When the packet has a native capture buffer and an
  unmaterialized lease, the rewrite runs in place on that frame and the slot itself is queued — no
  `NdisPacketBufferPool.Rent()`, no full-frame copy. Materialized or buffer-less packets keep a
  pooled-copy fallback with exactly-once return. The read-then-write order is preserved: sequence
  trackers always observe pre-rewrite bytes.
- **R3 — Control frames stay immediate.** The SYN injection (`TcpRedirectSetup`), every
  `ClientResetInjector` reset (relay-failure, mid-flow failure, capacity rejection), and
  fragment/teardown resets keep immediate single sends, issued before that iteration's lane flush,
  so a control frame can never be overtaken by batched data of the same iteration.
- **R4 — Per-flow order.** One association's data frames always land in one lane in capture order.
  Cross-flow/cross-lane ordering is not a contract (same posture as the pass lanes).
- **R5 — Failure attribution is preserved.** A failed batched flush must not silently swallow the
  per-packet failure contract: the lane degrades to per-frame single sends so
  `ClientResetInjector.HandleInjectionFailureAsync` still fires for the failing association
  (client-visible RST + fail-closed association teardown). Rented buffers are released exactly once
  on both the success and failure paths; a degraded lane is counted and warned rate-limited.
- **R6 — No new per-packet allocation.** Steady-state managed allocation on both redirect legs stays
  0 B; lane storage grows on the cold path only (doubling), mirroring the pass lanes.

## Acceptance Criteria

- [x] **AC1 (batching, observable)**: `TcpRedirectInjectionBatchingTests.FullBatchOfDataFramesLeavesAsOneBatchInAppendOrder`
      — 32 frames in one iteration leave as exactly one batched call per (adapter, direction) in append
      order with zero single sends (32× ≥ the 10× bar).
- [x] **AC2 (in-place, observable)**: `PumpOwnedFrameIsRewrittenInPlaceWithoutRentalOrMaterialization`
      asserts the capture slot's bytes are the rewritten frame (src/dst IP + ports + payload), the pool
      was never rented (`Rented == 0`), the lease is still unmaterialized, and one batch call left;
      `MaterializedLeaseTakesThePooledFallbackAndReturnsItExactlyOnce` covers the pooled fallback.
- [x] **AC3 (ordering)**: `CapacityResetControlFrameStaysImmediateAndPrecedesTheFlush` pins the
      call order `Async → Single → Batch`; `HostForwardAndReverseLegsShareTheTowardMstcpLaneInCaptureOrder`
      pins per-association append order; `ForwardedFlowReverseLegTargetsTheOriginAdapterLane` pins the
      cross-adapter lane key against a distinct capture handle.
- [x] **AC4 (failure)**: `FailedBatchDegradesToPerFrameSendsAndFailsEachAssociation` (3 per-frame
      retries, 3 per-association resets, 3 `tcp.redirect.failed`, degraded counter, no leak) and
      `ThrowingFailureTailOnOneFrameStillDrainsTheRestOfTheLane` (a faulting tail cannot strand the
      lane; the rentals it held return exactly once: `Rented == Returned`, `Outstanding == 0`).
      `DebugGuardPassesOnceTheIterationFlushReleasedEveryLane` + `HotPathAllocationGateTests.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes`
      cover the drain guard and the steady-state 0 B contract.
- [x] **AC5 (gates)**: Release build 0 warnings / 0 errors; `WinForward.Core.Tests` 952 + `WinForward.Analyzers.Tests`
      18 passed, 0 failed (938 baseline + 14 additive); `dotnet format --verify-no-changes` exit 0 with
      empty output; `jb inspectcode` run recorded with the task. `TcpThroughputScenario` socks5/bare ≥ 70 %
      and `gc-soak` are **not verifiable on this Linux host** (no Windows driver/NIC) — they stay with the
      `windows-real-nic` / on-hardware program, as the research report already assigned them. The
      host-independent substitute is the deterministic counting/allocation gate above.
- [x] **AC6 (observability)**: `tcp.redirect.deferred-overflow`, `tcp.redirect.batch-failed` and
      `tcp.redirect.deferred-failed` are rate-limited (`RuntimeLogThrottle`, check-first) and asserted;
      `TcpRedirectDiagnostics` exposes `RedirectPendingCount` / `RedirectOverflowCount` /
      `RedirectDegradedFlushCount`; the injection trace fires exactly once per frame by construction
      (append-time log on the deferred path, post-inject log on the immediate path, mutually exclusive).

## Out of Scope

- UDP response micro-batching (`UdpResponseReinjector`): stays a separate conditional decision gated
  on a DNS-dense measurement, per `08-30-batched-ioctls` R3.
- SYN-path and setup-worker injections: off-pump, per-flow, control — never batched here.
- Real-NIC/ETW end-to-end throughput validation: belongs to the `windows-real-nic` candidate; this
  task's evidence is the deterministic counting gate.
- Any change to the pass-lane implementation or its semantics.
- Other findings from the same research report (F2 locks, F3 sweeps, F4 data structures, F5 pump I/O
  shape, F6 UDP footprint, F8 attribution, F7 WFP).

## Notes

- Latency: batching does not add a poll period. A frame is dispatched and its lane is flushed inside
  the *same* pump iteration (the flush runs after the dispatch loop), so the added delay is the
  remainder of the current iteration — microseconds — not the ~1 ms idle poll cadence. The research
  report's "at most one poll cycle" is the conservative reading of a flush deferred to the next
  iteration; the implemented contract flushes before the next read.
- This task must be independently revertible in two steps (batching first, in-place second); see
  `implement.md` for the order and the rollback point.
