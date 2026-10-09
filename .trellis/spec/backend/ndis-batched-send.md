# NDIS Batched Reinjection Sends

> Batched reinjection: the executor Pass lanes and the TCP-redirect lanes, their flush points,
> ordering guarantees, capacity backstops and failure posture (wired 2026-08-30, task
> 08-30-batched-ioctls). Read it before touching the executor Pass path, the pump batch-end callback,
> the driver's batched send surface, or the redirect injector's data legs. Hub (handle rule, gate
> topology): [windows-ndisapi.md](./windows-ndisapi.md); the read side that feeds these lanes:
> [ndis-batched-capture.md](./ndis-batched-capture.md).

**Trigger**: any change to the executor Pass path, the pump batch-end callback, or the driver's
batched send surface. Infra contract: which reinjections may be deferred into a batch, the ordering
guarantees while deferring, and the observed all-or-nothing failure semantics.

## Signatures

- `NdisApiDriver.SendPacketsToMstcp/SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count)`
  — batched native send; one adapter-gate lease across all chunks, each chunk at most
  `MaxPacketsPerSendRequest` (126) packets.
- `NdisPacketActionExecutor.FlushPendingPasses(nint adapterHandle)` — flush the executor's pending
  Pass lanes for that adapter, one batched call per direction lane in lane-creation order, preserving
  append (capture) order within each lane.
- `NdisCapturePumpOptions.OnBatchCompleted` — invoked after every batch's slot loop (empty batches
  included) and again from the run loop's post-`try`/`catch` block on loop exit, before
  `ReleaseBatchBuffers()`.
- Telemetry: the driver exposes **no** batched-send counters. The nearest are
  `TcpRedirectDiagnostics.RedirectDegradedFlushCount`, `TcpRedirectDiagnostics.RedirectOverflowCount`
  and `NdisPacketActionExecutor.ImmediateSendLaneOverflowCount`; the read path's own counters are
  `NdisReadDiagnostics`.

## Contracts

- **PacketsSuccess is unobservable on the send path (export-verified 2026-08-30)**: the send IOCTLs
  pass `lpOutBuffer=NULL, nOutBufferSize=0` and both IOCTLs are `METHOD_BUFFERED` — verified against
  the pinned ndisapi source `417b8734` and the local DLL's disassembly (evidence:
  `.trellis/tasks/archive/2026-08/08-30-batched-ioctls/research/abi-packets-success.md`), and
  consistent with the driver wrapper's own fail-closed semantics. Batched send is therefore **all-or-nothing**: failure throws the same `Win32Exception`
  (native error, direction, chunk range, adapter) as the single-packet path. Never build resend logic
  on `PacketsSuccess` for sends.
- **Batching scope**: executor Pass dispositions **and the two TCP redirect mid-flow data legs** are
  deferred (task 09-29-tcp-redirect-batched-injection). Per-(adapterHandle, direction) lanes accumulate
  in-place capture buffers and rented pool buffers; `PassAsync` appends instead of sending. TCP
  redirect SYN setup, every RST/control injection, and UDP response reinjection stay immediate single
  sends (they share only the adapter gates). The redirect legs' lanes are the sibling table described
  under [Redirect deferred-injection lanes](#redirect-deferred-injection-lanes) below.
- **Flush points**: once per pump iteration via `OnBatchCompleted` (before the next `TryReadPackets`)
  and on run-loop exit (before batch-buffer release) — teardown never drops pending frames.
  `FlushPendingPasses` is adapter-scoped because one executor serves every pump; pump A must never
  flush pump B's mid-iteration frames.
- **Serialization rests on the pump's strictly-ordered await chain, not thread identity** —
  `ConfigureAwait(false)` may resume on any thread-pool thread, but exactly one pump chain calls into
  a given adapter's lane at a time. A `PassAsync` caller outside a pump chain would break this; the
  audit in `.trellis/tasks/archive/2026-08/08-30-batched-ioctls/research/passasync-call-site-audit.md`
  pins the current callers.
- **Ordering**: same (adapter, direction) Pass frames are reinjected in capture order (append order ==
  slot order). Cross-lane reordering is not a contract: flows never mix dispositions, so per-flow
  order is untouched. Pass sends may shift relative to interleaved cold injections of other flows
  within one iteration. The two sanctioned relaxations of the pump's strict in-batch order (new TCP
  SYN, F8 attribution deferral) are documented in
  [ndis-batched-capture.md](./ndis-batched-capture.md#pump-batching-and-idle-pacing) — neither
  reorders frames within a flow.
- **Lane lifecycle is generation-scoped (fixed 2026-09-08, task 09-08-p0-correctness R1; retirement
  re-implemented as a scope-sized table rebuild 2026-09-12, task 09-12-lane-table-scope-sizing)**:
  `DurableCaptureBundle.OnScopeInstalled` (the runner's scope-installed callback) runs
  `UpdateUdpTargets` → `Executor.RetireLanesExcept(<scope handles>)` → `Tcp.UpdateRedirectTargets(<scope handles>)`,
  strictly after the old generation's run task is awaited and before the next install. Since
  2026-09-12 `RetireLanesExcept` REBUILDS the lane table at `2 × scope-count` slots under the
  lane-creation lock (rebuild subsumes retirement: the fresh table only ever holds live-scope keys),
  MIGRATING in-scope lane objects with identity and pending frames intact — migration is mandatory
  because the runner starts the new generation's run before invoking the callback, so the new pumps
  may already have appended to the old table; lane creation is serialized by the same lock, so no lane
  can be created in the dead table, and lock-free appends only touch lane objects migration preserves
  (dropping in-scope lanes would silently strand their pending frames). Out-of-scope lanes keep the
  defensive drain (rented buffers returned exactly once; a non-empty lane at retire time is a contract
  breach and warns rate-limited); an empty scope installs a zero-capacity table (interception paused).
  Precondition: no pump for a retired handle may still be running; driver handle-value reuse is safe
  (same key ⇒ same accumulation semantics).
- **Lane overflow** is structurally unreachable for in-scope adapters since 2026-09-12 (the table is
  sized 2 × scope-count at every scope install); it remains as a defensive backstop against the
  pre-install table (capacity 8, before the first scope install) and the zero-capacity paused table —
  those Passes degrade to immediate single sends and are **observable since 2026-09-08**:
  `ImmediateSendLaneOverflowCount` (Interlocked) increments and a rate-limited (5 s) warn fires;
  capacity degradation is never silent.
- **Exactly-once pool return**: rented buffers return in the flush's `finally` (including the throwing
  path); in-place capture buffers are never pool-returned.
- **Failure posture of the pass flush**: the batch is all-or-nothing, so the flush logs
  `reinject.pass-failed` with the adapter handle and direction (one lane spans many flows, so no flow
  key) and **rethrows** — the pump's caller observes the same fault as before.

## Redirect deferred-injection lanes

The TCP local-redirect data legs used to inject one frame per IOCTL with a rented buffer plus a
full-frame copy (research F1 of `09-29-tcp-udp-path-structural-perf`); they now use a sibling lane
table with the same mechanics, owned by `RedirectInjectionLanes` (Runtime.TcpRedirect).

- **Scope**: only the two mid-flow data legs defer — `TcpProxyCoordinator.ReinjectExistingFlowDataAsync`
  (forward: client → local listener) and `HandleReverseAsync` (reverse: listener → client; toward
  MSTCP for host flows, toward the origin adapter for forwarded ones). The SYN setup injection
  (`TcpRedirectSetup`), every `ClientResetInjector` reset (relay failure, mid-flow failure, capacity
  rejection), and the fragment/teardown resets stay immediate single sends.
- **Signatures**: `ITcpRedirectInjector.InjectBatch(NdisPacketBuffer[] frames, int count, bool towardMstcp, nint adapterHandle)`;
  `TcpProxyCoordinator.FlushPendingRedirectInjections(nint adapterHandle)` (toward-MSTCP lane first);
  `DurableCaptureBundle.FlushPendingInjections(nint adapterHandle)` = `ActivityClock.Tick()` →
  `Dispatcher.Attribution?.DeliverDecided(adapterHandle)` → `Executor.FlushPendingPasses` → the
  redirect flush. Decided attributions are delivered FIRST (F8, 2026-10-01): their pass frames must
  reach the lanes before that iteration's flush, and the callback is also the loop-exit one, so a
  delivery is never left lane'd when `RetireLanesExcept` runs. Pass-before-redirect then preserves the
  one plausible same-flow interleaving: a data frame that passed before its flow's association existed,
  followed by redirect frames once the background setup completed.
- **Flush points**: the same `OnBatchCompleted` callback as the pass lanes. Redirect lanes are drained
  and their table slots recycled every iteration, so they need no scope-sized rebuild and no generation
  migration; released lanes return to a spare pool with their arrays, so steady state allocates nothing
  (`HotPathAllocationGateTests.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes`).
- **In-place staging**: a pump-dispatched packet whose lease never materialized is rewritten on the
  capture slot itself and the slot is queued (`rented: false`); the rewrite is same-length, so the
  driver-stamped length bounds it. Any other shape (materialized lease, reconstructed packet) stages
  into a rented pooled buffer. Read-then-write order is preserved: the sequence trackers always observe
  pre-rewrite bytes. The capture slot stays the pump's property — never pool-returned — and the
  loop-exit flush runs before `ReleaseBatchBuffers()`.
- **Pump-chain-only appends**: the redirect lanes have no append lock; serialization is the pump's, as
  above. The discriminator is `CapturedFlowPacket.NativeFrame.Buffer is not null`. The one data-leg
  caller off the chain is the setup worker's concurrent-loser reinjection (`RunSetupPipelineAsync`, a
  packet reconstructed over the retained SYN copy) — it must take the immediate send, pinned by
  `NonPumpPacketsTakeTheImmediatePathAndLeaveEveryLaneEmpty`.
- **Cross-adapter targets are scope-gated**: a lane is only drained by the pump it is keyed on, so a
  forwarded flow's reverse leg (key = `association.OriginAdapterHandle`, not the capturing handle) may
  only defer while that adapter is in the installed scope (`TcpProxyCoordinator.UpdateRedirectTargets`,
  pushed from `OnScopeInstalled`). Once it leaves, the frame keeps the immediate send, which fails on
  the stale handle and runs the per-flow failure tail; a lane would otherwise hold the frame, its
  rental, and the client-visible reset forever
  (`ReverseFrameForAnOriginAdapterOutsideTheScopeStaysImmediate`). Same-handle lanes (both host legs,
  and a forwarded flow's forward leg) need no scope check: the capturing pump is by construction live.
- **Failure posture**: a failed batch is all-or-nothing, so every frame of the lane degrades to its own
  single send and each failure runs the deferred tail (rate-limited `tcp.redirect.deferred-failed`
  warn, then `ClientResetInjector.HandleInjectionFailureAsync`, which already owns the client reset and
  the fail-closed association write). The tail is contained per frame: one association's fault must not
  strand the lane's remaining frames. Unlike the pass flush, the redirect flush does **not** rethrow — a
  redirect injection failure already ends in a client RST plus a fail-closed association while the pump
  keeps running, so rethrowing would newly degrade the whole pump for one adapter fault. A failed batch
  is counted in `RedirectDegradedFlushCount` with a rate-limited `tcp.redirect.batch-failed` warn. The
  per-packet `proxy-blocked` outcome line for a failed data-leg injection is replaced by the flush-site
  warn (the accepted attribution change from the research finding).
- **Caps and degradation**: the lane table caps at `MaxLanes = 64` keys and a lane at
  `MaxFramesPerLane = 126` frames — the driver's `MaxPacketsPerSendRequest`, so a failed batch can
  never have delivered an earlier chunk and the degraded retry stays duplicate-free (linkage asserted
  by `LaneFrameCapNeverExceedsOneDriverSendChunk`). A refused append keeps the immediate send, counted
  in `TcpRedirectDiagnostics.RedirectOverflowCount` with a rate-limited
  `tcp.redirect.deferred-overflow` warn.

## Validation & Error Matrix

| Condition | Required result |
|---|---|
| Pass disposition inside a pump iteration | appended to its lane; no native call until flush |
| Flush with N pending in one lane | one batched send for the lane; rented buffers returned exactly once |
| Batched send returns FALSE | `Win32Exception` (all-or-nothing); rented buffers still returned; the pass flush rethrows |
| Count > 126 | chunked inside one gate lease |
| Run loop exits (cancel/stop/fault) | pending lanes flushed from the loop-exit callback before batch-buffer release |
| Scope install with N adapters | lane table rebuilt to 2N slots; in-scope lanes migrated; overflow backstop not reachable for in-scope keys |
| Lane capacity exceeded (pre-install table capacity 8, or paused zero-capacity table) | overflow Pass sends immediately as a single send (counted + warned; in-scope keys cannot reach this) |
| Redirect data-leg frame, pump-dispatched, lease unmaterialized | rewritten on the capture slot itself; slot queued (`rented: false`); no rental, no copy |
| Redirect data-leg frame, materialized lease or reconstructed packet | staged into a rented pooled buffer; queued or sent immediately; returned exactly once |
| Redirect data-leg frame with the capture slot absent (setup worker) | immediate single send; never appended |
| Redirect forward/reverse frame for a cross-adapter target outside the installed scope | immediate single send (fails on the stale handle → per-flow tail); never appended |
| Redirect lane batched send fails | counted (`RedirectDegradedFlushCount`) + rate-limited `tcp.redirect.batch-failed`; every frame retried on its own; the flush does not rethrow |
| Redirect per-frame retry fails | per-frame deferred tail (rate-limited `tcp.redirect.deferred-failed`, client reset, fail-closed association); remaining frames still attempted; rentals still returned |
| Redirect lane append refused (table cap 64 or lane cap 126) | immediate single send, counted (`RedirectOverflowCount`) + rate-limited `tcp.redirect.deferred-overflow` |
| Non-batched reinjector callers (TCP injector, UDP response reinjector) | unchanged single-packet path |

## Wrong vs Correct

```csharp
// Wrong: trusting PacketsSuccess on the send path to resend a "suffix" —
// the field never comes back (lpOutBuffer=NULL, METHOD_BUFFERED).
if (request->PacketsSuccess < count) ResendSuffix(...); // dead code at best

// Correct: fail the batch wholesale, same diagnostics as the single path.
if (result == 0) throw new Win32Exception(error, $"...packets {offset}..{end} of {count}...");
```

## Tests Required

- `NdisPacketActionExecutorBatchingTests`: same-lane append order; two keys flush independently; mixed
  materialized/in-place frames; exactly-once pool return on success and on batch failure; empty flush
  no-op; lane-overflow fallback (pre-install table); scope-install rebuild beyond the initial capacity
  with in-scope lane migration (pending frames survive); empty-scope rebuild degrades stray passes
  until the next install.
- `NdisApiBatchedSendAbiTests`: request-slot layout/chunk-budget derivation.
- `BatchedPassReinjectionE2eTests`: N frames → one batched call per direction per iteration; strictly
  increasing per-direction markers (order); flush-on-exit with a faulting handler slot.
- `TcpRedirectInjectionBatchingTests` (redirect lanes): a full 32-frame batch leaves as one call in
  append order; both host legs share the toward-MSTCP lane; a forwarded flow's reverse lane is keyed
  on the origin handle; a cross-adapter target outside the scope stays immediate;
  capacity-reset/control frames stay immediate and precede the flush; a failed batch degrades per
  frame to each association's tail and a faulting tail still drains the lane; in-place staging rewrites
  the capture slot with zero rentals and an unmaterialized lease; a materialized lease takes the pooled
  fallback and returns exactly once; lane-frame cap ≤ one driver send chunk.
- `HotPathAllocationGateTests.DeferredInPlaceRedirectInjectionAllocatesNoManagedBytes`: 0 B managed
  per frame for the deferred in-place leg (gate).
