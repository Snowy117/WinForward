# Design — TCP redirect in-place rewrite + lane-batched injection

Scope: the two mid-flow redirect data legs only. Everything else on the TCP redirect path
(SYN setup, resets, tombstones, capacity rejections) keeps its immediate single-send shape.

## 1. Module boundaries

| Module | Change | Why here |
|---|---|---|
| `TcpProxyCoordinator` | Owns the redirect lanes; both data legs stage + append; new `FlushPendingRedirectInjections(nint)` | It owns the association, the pre-rewrite sequence reads, and the per-flow failure tails — attribution must stay with the frames' owner |
| `RedirectInjectionLanes` (new, internal) | Lane storage keyed by (adapter handle, target direction): append, overflow decision, take/release, diagnostics | A dumb, independently testable container; no collaborators, no policy |
| `ITcpRedirectInjector` / `TcpRedirectInjector` | New `InjectBatch(NdisPacketBuffer[], int, bool towardMstcp, nint)` delegating to the existing batched driver ABI | The injection seam already owns "how a frame reaches the driver"; batching is an injection concern |
| `DurableCaptureBundle` / `Program.cs` | Compose one batch-completed callback: pass flush first, then redirect flush | The pump has exactly one callback slot (`Action<nint>` wired at `src/WinForward.Cli/Program.cs:284`) |
| Pass path (`NdisPacketActionExecutor`) | **untouched** | The proven lane implementation must stay byte-identical; redirect lanes are a sibling table, not an extension of it |

Rejected: extending `NdisPacketActionExecutor`'s lane table with redirect keys. It would save at
most one IOCTL per (adapter, direction) per iteration on top of the 32:1 reduction, but it forces
either a coordinator→executor callback cycle or late-bound mutable wiring (the TCP coordinator is
constructed before the executor: `DurableCaptureBundle.cs:133` vs `:243`), and it puts redirect
frame lifecycle inside a module that knows nothing about associations.

## 2. Staging: in place on the capture slot

Both legs currently stage into a rented pooled buffer. The in-place shape applies whenever
`packet.NativeFrame.Buffer` is present and `packet.Lease.IsMaterialized` is false — which is the
production steady state for both legs:

```
var capture = packet.NativeFrame.Buffer;                 // pump-owned batch slot
if (capture is not null && !packet.Lease.IsMaterialized)
{
    var frame = capture.GetFrame();                      // writable, driver-stamped length
    ...read-then-write sequence tracking...              // unchanged order
    ...rewrite (endpoints/checksums/MAC swap)...         // same length, in place
    capture.CompleteFrame(frame.Length, flags, handle);  // direction flag + enumeration handle
    ...append to lane (rented: false)...
}
```

- `FlowDispatcher.InspectionSpan` (`src/WinForward.Runtime/FlowDispatcher.cs:47`) *is*
  `capture.GetFrame()` when a capture buffer exists, so the bytes rewritten are exactly the bytes
  the pre-rewrite readers observed.
- `CompleteFrame` stamps the length, the ON_RECEIVE/ON_SEND direction flag, the enumeration handle,
  and zeroes flags/padding — identical to what the rented path stamps today, so the wire shape is
  unchanged.
- The rewrite is same-length for both legs (endpoint + checksum rewrite, MAC swap), so the bounded
  `GetFrame()` span is sufficient; `GetFrameStorage()` is not needed.
- The capture slot is not released by the lane: like the pass path's in-place frames, it belongs to
  the pump's batch and is only sent before the next read (the pump's flush ordering guarantees this).
- Fallback (materialized lease, or no capture buffer): keep the rented pooled copy, but now append
  it to the lane with `rented: true`. `finally { if (!deferred) buffer.Dispose(); }` keeps the
  exactly-once return.

Post-rewrite readers were enumerated: sequence trackers run before the rewrite; the trace event reads
key/generation fields only (`TcpRedirectLogging.LogTrace`); the RST builders use their own templates
(`TcpResetBuilder`, the SYN copy pool); and the remaining `Lease.Frame` readers are the SYN setup
path and the no-capture-buffer fallback, neither reachable for a mid-flow data frame. The lease stays
unmaterialized on this path, so no pooled array can observe mutated bytes.

## 3. Lanes

`RedirectInjectionLanes` mirrors `PendingPassLane` mechanics:

- Key: (adapter handle, towardMstcp). Linear lock-free scan once a lane exists; creation under one
  small lock; the entry array is published with volatile semantics and only replaced on growth
  (cold path, doubling, capped — beyond the cap an append falls back to an immediate single send,
  counted and warned rate-limited, mirroring `ImmediateSendLaneOverflowCount`).
- Per-frame parallel arrays: buffer, `rented` flag, association (for deferred failure attribution).
  Append order = capture order.
- Entries are **recycled every iteration**: a lane is drained by the flush and returned to the free
  pool, so no adapter-handle-keyed entry can outlive its iteration and no scope-install retirement
  hook is needed. After warm-up the container allocates nothing in steady state.
- Diagnostics: `PendingCount`, `OverflowCount`, and a `[Conditional("DEBUG")]` no-pending guard
  called from the bundle's scope-installed path (the redirect analogue of
  `DebugAssertNoPendingPasses`, proving the flush contract holds).

### 3.1 Deferral is pump-chain-only (correctness precondition)

Appends are serialized by the pump's strictly-ordered await chain, not by a lock — the same
invariant the pass lanes rest on (`.trellis/spec/backend/windows-ndisapi.md`, "Batched reinjection
sends" § Contracts). Exactly one data-leg call site is not on a pump thread: the setup worker's
concurrent-loser reinjection (`TcpProxyCoordinator.RunSetupPipelineAsync`,
`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:304`), whose packet is reconstructed over
the retained SYN copy (`:296`) and therefore has a default `NativeFrameHandle`.

The discriminator is `packet.NativeFrame.Buffer is not null`: every pump-dispatched packet carries
the capture buffer (`NdisCapturedPacket.FromCapture(_batchBuffers[index], _adapterHandle)`), and
every reconstructed/retained-copy packet does not. A frame without a capture buffer is sent
immediately through the existing single-packet path with the existing failure mapping — never
appended. This is deliberately the same predicate that gates Step 2's in-place staging, so both
features live under one guard, and it is pinned by a test that drives the data leg with a
reconstructed packet and asserts an immediate send with every lane left empty.

## 4. Flush, ordering, failure

```
FlushPendingRedirectInjections(handle)
  for direction in { towardMstcp, towardAdapter }:
    if lane empty: continue
    try: injector.InjectBatch(lane.Buffers, count, direction, handle)
    catch batch:                       // batched ABI is all-or-nothing: nothing was delivered
      count + rate-limited warn
      for each frame: try injector.Inject(frame, direction, handle)
                      catch: deferred failure tail (warn, client RST, fail association) — never rethrows
    finally: release rented buffers exactly once; free the lane entry
```

- **Why per-frame degradation**: `.trellis/spec/backend/tcp-local-redirect.md` requires every
  `SendPacketTo*` failure to exit through `ClientResetInjector.HandleInjectionFailureAsync` — a
  silently failed batch would black-hole the client instead. The all-or-nothing ABI makes the retry
  duplicate-free.
- **No rethrow, unlike the pass flush**: a redirect injection failure today ends in a client RST plus
  `FailAssociationAsync` and the pump keeps running; rethrowing would newly degrade the whole pump
  for a single flow's adapter fault. Deliberate divergence, documented at the call site.
- **Outcome semantics**: a deferred frame makes the handler return `TcpRedirectOutcome.Injected`
  ("rewritten and committed to this iteration's flush") before the native send happens. The
  per-packet `LogProxyBlocked("redirect")` line for a failed injection becomes the flush-site
  `tcp.redirect.failed` warn. This is the accepted attribution change from the research finding; the
  product-visible contract (client RST + fail-closed association) is preserved.
- **Ordering**: every immediate injection (RSTs, capacity resets, SYN setup) happens during dispatch,
  i.e. before the flush, so control frames can never be overtaken. The batch-completed callback runs
  the **pass flush first, then the redirect flush**: the one plausible same-flow interleaving is a
  data frame passing before its flow's association exists (`NotRelevant` → `PassAsync`) followed by
  redirect frames after the background setup completes, and pass-first preserves that capture order.
- **Latency**: flush happens at the end of the dispatching iteration, so a frame waits the remainder
  of that iteration (microseconds), not a poll period. Idle-case latency is the pump's poll cadence
  (research F5), unchanged by this task.

## 5. Compatibility and rollback

- No change to routing, rewrite math, checksum math, the redirect table, teardown, or the outcome
  enum. No new configuration. Wire-format identical (same rewrite, same direction flags, same handle).
- Revertible in two independent steps: (1) lanes + deferral with the pooled staging unchanged;
  (2) in-place staging. Each step leaves a green, shippable tree (see `implement.md`).
- `InjectAsync` (setup path) and `Inject` (control frames) keep their contracts; `InjectBatch` adds
  "the caller owns the buffers before and after; the lane releases rented ones after the attempt".

## 6. Risks

| Risk | Mitigation |
|---|---|
| Capture-slot mutation loses the original bytes for a later reader | Enumerated (§2); the only post-rewrite reader is the injector, and the lease stays unmaterialized — locked by a test asserting the lease is unmaterialized and that the post-rewrite `InspectionSpan` is the rewritten frame |
| Deferred failure loses per-packet attribution | Per-frame degradation to the established failure tail (§4), pinned by a test that asserts the RST + fail-association calls happen for the failing association |
| Double release / leak of rented buffers | `finally` release in the flush, `rented` flag per frame, `!deferred` guard in the staging `finally`; asserted by a rent/return counting fake |
| Lane overflow on many adapters | Cap + immediate single send (still correct, counted, warned) |
| A pending lane at loop exit sends with a now-stale handle | Same posture as today's immediate send: the failure surfaces per-frame (§4); the pump's exit flush runs before buffer release, so no frame is sent from freed memory |
| Behavior drift in the proven pass path | Pass path untouched; existing batching tests must stay green |

## 7. Measurement

- Deterministic gate (acceptance): a counting injector asserting one batch call per
  (adapter, direction) per iteration, per-flow append order, and ≥10× fewer reinjection calls at a
  full 32-frame batch — the same discipline `NdisPacketActionExecutorBatchingTests` uses.
- Allocation gate: the redirect data leg's in-place shape allocates 0 B per frame
  (`HotPathAllocationGateTests` shape, with a zero-rent assertion on the frame pool).
- Optional BDN evidence (`TcpRedirectInjectionBenchmarks`): rewrite + inject against a counting fake
  for the per-packet vs batched shapes. Its ns figure is not a gate — the loopback harness cannot
  show the IOCTL win (Windows ceiling ≈ 4.7k pps); real-NIC validation stays with `windows-real-nic`.
