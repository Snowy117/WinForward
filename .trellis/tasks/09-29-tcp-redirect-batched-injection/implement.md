# Implementation plan — TCP redirect in-place rewrite + lane-batched injection

Two independently revertible code steps plus evidence/spec work. Each step must leave the tree
green and shippable; run the full gate set at every rollback point.

## Step 0 — Preflight

- [ ] Re-verify the PRD/design anchors against current source (they were verified at planning time;
      confirm no drift in `TcpProxyCoordinator`, `TcpRedirectInjector`, `NdisPacketActionExecutor`,
      `NdisCapture`, `DurableCaptureBundle`, `Program.cs:284`).
- [ ] Baseline: full suite green, and record `CapturePumpBenchmarks` / `DispatcherBenchmarks` /
      `HotPathAllocationGateTests` numbers at the current HEAD for the after-comparison.

## Step 1 — Lanes + deferral (pooled staging unchanged) — **rollback point A**

Delivers the IOCTL win alone; the rewrite/staging code is unchanged, so all existing redirect tests
must pass untouched.

- [ ] `src/WinForward.Runtime/TcpRedirect/RedirectInjectionLanes.cs` (new): (adapter handle, target
      direction)-keyed lane container — lock-free scan, creation under one lock, doubling growth with
      a cap, per-frame `buffer` / `rented` / `association` arrays, `TryAppend`, `TryTake`, `Release`,
      `PendingCount`, `OverflowCount`, `[Conditional("DEBUG")]` no-pending guard. Mechanics mirror
      `PendingPassLane` / `FlushLane` / `RetireLanesExcept` in
      `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs`.
- [ ] `src/WinForward.Runtime/TcpRedirect/TcpRedirectInterfaces.cs`: add
      `InjectBatch(NdisPacketBuffer[] frames, int count, bool towardMstcp, nint adapterHandle)` to
      `ITcpRedirectInjector`, documented as "sends a same-(direction, adapter) batch; the caller owns
      the buffers before and after the call".
- [ ] `src/WinForward.Runtime/TcpRedirect/TcpRedirectInjector.cs`: implement `InjectBatch` over
      `IPacketReinjector.SendPacketsToMstcp/SendPacketsToAdapter`.
- [ ] `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs`:
      - field `_lanes`; both data legs (`ReinjectExistingFlowDataAsync`, `HandleReverseAsync` /
        `InjectReverseFrameAsync`) append after the rewrite instead of injecting, with the existing
        immediate path kept as the overflow/no-lane fallback;
      - the append is gated on the pump-chain predicate `packet.NativeFrame.Buffer is not null`
        (design §3.1): the setup worker's concurrent-loser reinjection
        (`RunSetupPipelineAsync`, a reconstructed retained-copy packet) must always take the
        immediate single-send path — the lanes are pump-chain-serialized and have no append lock;
      - preserve the existing read-then-write order, the forwarded-direction `targetHandle == 0`
        fail-closed check, and the existing exception→outcome mapping on the immediate path;
      - new `FlushPendingRedirectInjections(nint adapterHandle)`: batch send per direction, and on a
        batch failure degrade to per-frame sends whose failures run the deferred failure tail
        (warn `tcp.redirect.failed`, client RST, `FailAssociationAsync`) without rethrowing; release
        rented buffers exactly once in `finally`; free the lane entry.
- [ ] `src/WinForward.Cli/DurableCaptureBundle.cs`: `internal void FlushPendingInjections(nint)`
      = pass flush then redirect flush; call the redirect DEBUG no-pending guard from
      `OnScopeInstalled`.
- [ ] `src/WinForward.Cli/Program.cs`: wire `bundle.FlushPendingInjections` as the processor's
      batch-completed callback (replacing the direct `FlushPendingPasses` method group).
- [ ] Tests — new `tests/WinForward.Core.Tests/TcpRedirectInjectionBatchingTests.cs` (model:
      `NdisPacketActionExecutorBatchingTests`): one batch call per (adapter, direction) per
      iteration; append order preserved across both legs of one association; immediate control
      injections ordered before the flush; batch-failure degradation reaches the per-association RST
      path; a throwing failure tail cannot strand the rest of the lane; exactly-once release of
      rented buffers; lane overflow → immediate single send; a reconstructed (non-pump) packet takes
      the immediate path and leaves every lane empty. End-to-end form to mirror:
      `BatchedPassReinjectionE2eTests` (call-reduction + per-direction order + flush-on-exit).
- [ ] Update the four test implementors of `ITcpRedirectInjector` (`FakeInjector`,
      `CountingInjector`, `OrderingInjector`, `ThrowingRedirectInjector`) with `InjectBatch`.

Validation: `dotnet build -c Release`, full `dotnet test -c Release`, `dotnet format
--verify-no-changes` (empty), `jb inspectcode` (zero Issue). AC1, AC3, AC4, AC6.

Rollback: revert step-1 files; per-packet injection behavior returns unchanged.

## Step 2 — In-place staging on the capture slot — **rollback point B**

- [ ] `TcpProxyCoordinator.cs`: both legs take the capture buffer when
      `packet.NativeFrame.Buffer is { } && !packet.Lease.IsMaterialized`, rewrite it in place
      (`GetFrame()` → trackers → rewrite → `CompleteFrame(len, direction flag, handle)`), and append
      it with `rented: false`; otherwise keep the rented staging with the `!deferred` dispose guard.
- [ ] Tests: capture-slot bytes are the rewritten frame; the frame pool is never rented on that
      path; the lease stays unmaterialized; the materialized/buffer-less shape still takes the pooled
      fallback; an allocation gate for the redirect data leg added to the
      `HotPathAllocationGateTests` shape (0 B per frame).
- [ ] Verify the trace-enabled shape still behaves (trace logging must not materialize the lease
      before the rewrite).

Validation: same full gate set, plus `TcpThroughputScenario` socks5/bare ratio ≥ 70 % and
`gc-soak` green. AC2, AC5.

Rollback: revert the staging branch only — step 1's batching stays.

## Step 3 — Evidence, spec, and record corrections

- [x] Benchmark evidence: **the deterministic gate was chosen over a BenchmarkDotNet class**.
      `HotPathAllocationGateTests.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes` (0 B per
      frame, failed at 264 B per (adapter, direction) per iteration before the lane spare pool) plus the
      counting assertions in `TcpRedirectInjectionBatchingTests` (32 frames → 1 call, 32×) are exact and
      host-independent, where a BDN ns figure is not a gate on this loopback harness (design §7). The
      production-facing syscall-amortization evidence is the existing
      `NdisApiDriver.BatchedSendFlushCount` / `BatchedSendPacketCount`, which redirect batches now feed
      automatically.
- [x] Spec updates: `windows-ndisapi.md` gained § "3.1 Redirect deferred-injection lanes" (scope,
      signatures, flush points, in-place staging, pump-chain precondition, cross-adapter scope gate,
      failure posture, caps) plus matrix rows and required tests; its "Batching scope" contract and
      `tcp-local-redirect.md`'s "Mid-flow data" and R8 pump-side sections were corrected for the
      deferred data legs and the one attribution change.
- [x] Correct the misleading scope row in `08-30-batched-ioctls`' recorded injection-path table
      (through the parent `08-30-proxy-perf-stability/prd.md` backlog notes) so "TCP mid-flow relay —
      no injection" is not re-read as authority for the client-facing redirect legs.

## Validation commands

```bash
dotnet build WinForward.slnx -c Release                                  # zero-warning
dotnet test WinForward.slnx -c Release                                   # full suite green
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # empty output
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx # zero <Issue>

dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --filter '*CapturePump*' '*Dispatcher*' --job short
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability --scenario tcpthroughput --duration 60 --quick
```

## Risky files / rollback points

| File | Risk | Rollback |
|---|---|---|
| `src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs` | Hottest redirect path; ordering and failure semantics | step 1 / step 2 revert |
| `src/WinForward.Cli/DurableCaptureBundle.cs`, `Program.cs` | Composition wiring — a missed flush strands frames | step 1 revert; the DEBUG no-pending guard catches a missed flush in tests |
| `src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs` | **must not change** — any diff here is a scope violation | n/a |

## Follow-ups before `task.py start`

- [x] Confirm no user-owned decision remains open (scope, verification anchor, rollback granularity).
- [x] Keep `08-30-proxy-perf-stability`'s backlog table in sync once the task lands.

## Completion record (2026-09-29)

Step 1 and Step 2 both landed. Deviations from the plan, and what the independent check found:

- **Partial split (new structural choice)**: `TcpProxyCoordinator` hit the ≤400 effective-line budget, so
  the type is now split across `TcpProxyCoordinator.cs` (entry routing), `TcpProxyCoordinator.Injections.cs`
  (lanes, flush, failure tails, the two data legs) and `TcpProxyCoordinator.Diagnostics.cs` (snapshot +
  capacity summary). Precedent: `UdpProxyCoordinator.Send.cs`, `ConfigurationModels.Udp.cs`. The data legs
  were moved by a script-assisted line-range move and gated on the test baseline before any logic change.
- **Step 2 consequence for tests**: a deferred in-place frame keeps its capture slot, so a test harness
  must keep batch slots alive past the flush exactly like the pump does. The harness now tracks its slots
  and disposes them with the coordinator; the previous `using var capture` silently broke every
  in-place test.
- **Check-phase defect found and fixed (FIX 1, real R6 violation)**: `RedirectInjectionLanes.Release`
  dropped the lane object, so every iteration's first append re-allocated a lane plus three arrays —
  measured 264 B per (adapter, direction) per iteration. Fixed with a spare-lane pool (arrays kept);
  gated by the new `HotPathAllocationGateTests.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes`
  (failed at 16 896 B before, 0 B after).
- **Check-phase findings resolved after the check**:
  - U1 (orphan lane never flushed) — **fixed**: a lane is only drained by the pump it is keyed on, so a
    cross-adapter target (a forwarded flow's reverse leg) now defers only while its adapter is in the
    installed scope (`UpdateRedirectTargets`, pushed from `OnScopeInstalled`); otherwise the frame keeps
    the immediate send and fails closed through the per-flow tail.
  - U2 (degraded retry could duplicate already-delivered chunks) — **fixed**: the lane cap is now the
    driver's send-chunk budget (126), so a failed batch can never have delivered an earlier chunk;
    linkage asserted by `LaneFrameCapNeverExceedsOneDriverSendChunk`.
  - U3 (spurious DEBUG assert) — **fixed**: the no-pending guard is no longer called from the
    scope-installed path (a generation's pumps are live before that callback runs, which is why the pass
    path never asserted there either); it stays a test-called guard.
- **Out-of-scope fix bundled for a green gate**: `TcpProxyCoordinatorLifecycleTests.AcceptLoopDoesNotRunAwayOnTransientAcceptError`
  was a pre-existing load-dependent flake (it proved back-off with `elapsed >= 50ms`, and retries that
  ran during the preceding setup awaits were credited to the window — observed failing at `saw 27ms`).
  It now counts attempts relative to the window start. Committed separately from the feature work.
- **Not verifiable on this host**: `jb inspectcode` is run here; `TcpThroughputScenario` socks5/bare ≥ 70 %
  and `gc-soak` need the Windows driver/NIC and stay with the `windows-real-nic` program (the research
  report's own assignment). The deterministic counting/allocation gates stand in as the host-independent
  evidence.

### Suppression inventory (for the analyzer audit)

| Rule | Site | Reason |
|---|---|---|
| `VSTHRD002`, `CA2012` | `TcpProxyCoordinator.Injections.HandleDeferredInjectionFailure` | The flush runs on the pump thread, which already blocks on a pending handler the same way (`NdisCapturePump.InvokeHandler`); the `ValueTask` is async-method-backed and consumed exactly once, and the lane must not release its rentals before the cold teardown tail completes. |
| `ConvertIfStatementToSwitchStatement` (jb) | `TcpProxyCoordinator.Injections.HandleReverseAsync` (`if (towardMstcp) TcpFrameRewriter.SwapEthernetMacs(frame);`) | A single conditional void call has no switch shape; the inspection fires on the adjacent opposite-condition guard, and a bool switch would only restate the condition. |
| `CA1416` (avoided, not suppressed) | `TcpRedirectInjectionBatchingTests.LaneFrameCapNeverExceedsOneDriverSendChunk` | The assertion reads `NdisApiDriver.MaxPacketsPerSendRequest` (a member of a Windows-only type) and must also run on Linux; the test method carries `[SupportedOSPlatform("windows")]` instead of a suppression, and the attribute does not stop xunit from running it. |
