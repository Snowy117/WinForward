# NDIS Batched Capture Reads

> The batched read ABI, the read-shape self-heal, pump batching, the arrival-signal idle wait, and
> batch-buffer ownership. Read it before touching the capture pump read loop, the frame copy path into
> `PacketLease`, in-place TCP rewrite, or `NdisPacketBuffer` allocation. Hub (which handle is valid,
> the gate topology, the `ETH_M_REQUEST` layout): [windows-ndisapi.md](./windows-ndisapi.md);
> refresh/self-healing of the adapter view: [ndis-capture-refresh.md](./ndis-capture-refresh.md);
> deferred reinjection: [ndis-batched-send.md](./ndis-batched-send.md).

## Signatures and seams

- `NdisApiDriver.TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers) -> int` — the batched
  read is issued **first**, for the caller's whole array, inside one `NdisNativeCallGate` lease; the
  queue-size query runs only when that read did not succeed, to disambiguate "empty" from "driver
  error" (F5, 2026-10-01). Returns the driver-filled success count (0 = empty queue). The request
  layout is the hub's [shared request ABI](./windows-ndisapi.md#the-shared-request-abi).
- `INdisReadPacketCalls` / `NdisNativeReadPacketCalls` (`NdisReadPacketCalls.cs`, F5) — the
  driver↔native read seam: the two read-path native calls behind one injectable interface, so the read
  *shape* (which call, in what order, with what requested count) is exactly provable without
  `ndisapi.dll`. `NdisApiDriver.CreateForTests(INdisReadPacketCalls)` builds a driver over it with an
  invalid (zero) open handle, so `Dispose` never reaches `CloseFilterDriver`. `NdisApiDriver.ReadDiagnostics`
  (`NdisReadDiagnostics`) is the read-path counter snapshot; `IsQueryFirstForDiagnostics(handle)` and
  `ReadShapeMismatchSink` are the self-heal surface. `BuildMultiRequest`/`MultiRequestByteCount` stay
  on `NdisApiDriver` (three facts call the builder) and the seam calls them.
- `INdisPacketArrivalSignal` / `NdisPacketArrivalSignal` (`NdisPacketArrivalSignal.cs`, F5) — one
  bounded `Wait(TimeSpan)` on a caller-owned `WaitHandle`, deliberately not Windows-attributed so the
  production wait entry point is gateable on any host. `NdisApiDriver.TryRegisterPacketEvent(handle, out signal, out nativeError)`
  binds an auto-reset event per adapter per generation (best-effort; false ⇒ sleep-poll fallback);
  `MultiAdapterCaptureLoop`'s trailing `arrivalSignals` list owns and disposes them.
- Batched send since 2026-08-30 (task 08-30-batched-ioctls): `NdisApiDriver.SendPacketsToMstcp/SendPacketsToAdapter(nint, NdisPacketBuffer[], int)`
  submit up to `MaxPacketsPerSendRequest` (126) packets per `EthernetMultiRequest`, chunking larger
  counts inside one adapter-gate lease — see [ndis-batched-send.md](./ndis-batched-send.md).
- `PacketLease(ReadOnlyMemory<byte> frame, Action<ReadOnlyMemory<byte>>? onCompleted)` — completion-callback
  constructor; the plain constructor keeps null semantics. `NdisPacketBufferPool` (process-wide
  `Shared`, capacity 256) with `Rent()`/`Return()`; `NdisPacketBuffer` carries an owner-pool state
  machine (private ctor → `Dispose` frees; pool-rented → `Dispose` returns, double-`Dispose` no-op).

## Contracts

- **Frame lifetime boundary (the load-bearing rule)**: `FlowDispatcher.CompleteAsync` calls
  `lease.TryComplete(disposition)` BEFORE `execute()`, so every frame consumer (pass injection copy,
  UDP payload parse, clientMac slice, TCP in-place rewrite, RST template) runs AFTER lease completion
  but must stay INSIDE `CapturePacketProcessor.ProcessAsync`'s await window. The
  `ArrayPool<byte>.Shared.Return` therefore happens inside `PacketLease.Release()`, which
  `ProcessAsync`'s outer `finally` calls — never on the lease completion callback. Any new code that
  lets a `Frame`/`Memory` slice escape the `ProcessAsync` window (returning it, capturing it in a
  background task, storing it without copying) reintroduces a use-after-return.
- **Copy-out points are mandatory**: data that must outlive the window is copied synchronously — UDP
  `clientMac` (`ToArray()` at session creation), SOCKS5 payload (`Encode` copies before any await),
  the bounded SYN template (recorded before rewrite).
- **In-place rewrite ordering**: `RecordClientSyn`/`RecordServerSynAck` (reads of the original frame)
  MUST run BEFORE `TryRewriteTcpEndpoints` (write) on the same frame, or the RST template is polluted
  by rewritten endpoints/MACs. `TryRewriteIpv4Tcp/Ipv6Tcp` keeps the invariant "every
  parse/validation precedes the first field write; no failure branch after writing begins", so an
  in-place rewrite either leaves the frame untouched or fully rewrites it. A lease whose `Frame` is
  not array-backed fails closed (`reason=rewrite`, `TcpRedirectOutcome.Blocked`).
- **Gate granularity**: one gate lease per **drain** on the read path (the read, and the query +
  guarded probe when the read fails, all inside the same lease); since 2026-08-30 one gate lease per
  flush on the batched send path, with single-packet sends kept for non-batched callers (TCP
  injector, UDP response reinjector).
- **Read shape (F5, 2026-10-01)**: the drain issues `ReadPackets` unconditionally with
  `dwPacketsNumber = buffers.Length` (the vendor's own usage) and pays **one** native call per
  non-empty drain. A successful read that filled nothing is the empty queue. The query is retained
  **only** as the non-success disambiguator, because the pinned `ReadPackets` BOOL cannot be shown on
  this host to separate "empty queue" from "driver error". Because a read wider than the queue depth
  is a new, unverifiable ABI assumption (open item: does the real driver fill a short count?), the
  **first** `read failed + query > 0` observation for an adapter handle arms a sticky query-first
  shape, publishes one `NdisReadShapeMismatch(handle, requested, queued, nativeError)` through the
  composition-wired sink (one `adapter.readShape.mismatch` warn, rate-limited **per adapter handle**
  so every adapter's single arming is logged), and probes once with `read(min(queued, capacity))` — so
  a mismatching ABI self-corrects instead of degrading a healthy adapter through the pump's
  transient-retry budget. A second observation, or a failed probe, throws the read's own native error
  exactly as before. The flag lives on the durable driver keyed by enumeration handle, so a handle
  value reused by a refresh inherits it, and the guard's per-drain cost on conforming hardware is one
  lock-free lookup plus one branch.

## Pump batching and idle pacing

- Batch buffers are pump-private for the pump's lifetime and released exactly once (run-loop exit, or
  dispose). `DisposeAsync` sets the stop flag, then awaits the run-completion source that the run's
  post-release block completes, so a mid-run dispose can never free buffers a handler is still
  processing (fixed 2026-09-08, task 09-08-p0-correctness R4). The wait is bounded by the loop's poll
  delay **or, when an arrival signal is installed, by its idle-wait timeout** (default 100 ms) because
  the loop observes the stop flag every iteration; a dispose before any run started releases directly
  on the synchronous fast path.
- Packets within a batch are awaited strictly in index order, so reinjection order matches arrival
  order. Empty batches keep the poll-delay pacing, or park in the bounded arrival wait when a signal
  is installed. **Scope of the ordering contract (R8, 2026-08-30)**: strict index order governs
  per-flow correctness — a flow's rewritten packets reinject in arrival order. A genuinely new TCP SYN
  is the one sanctioned relaxation: the pump-side handler retains a bounded copy and returns
  `SetupPending`, and the rewrite+injection of that one frame completes in a background task (see
  [tcp-syn-setup-admission.md](./tcp-syn-setup-admission.md)). Per-flow ordering is preserved by construction (the client sends data only
  after the handshake completes, which requires our injected SYN), and same-flow retransmissions
  inside the pending window are absorbed by the pending index rather than reordered; cross-flow
  injection order within one adapter iteration is not a contract. **F8 (2026-10-01) adds a second
  sanctioned relaxation of the same shape**: a host flow that needs process attribution has its
  packets retained and returns `Deferred` immediately, and they are executed later from the pipeline's
  per-adapter decided queue inside the pump's existing batch-completed callback
  (`DurableCaptureBundle.FlushPendingInjections`), before the pass flush. Per-flow order is preserved
  (the ring is FIFO per flow); cross-flow order is not.
- The pump reads in batches (`ReadPackets`, batch capacity 32, one gate lease per drain; wired
  2026-08-27). Since 2026-10-01 (F5) an idle pump with a registered packet-arrival event parks in one
  bounded wait on it (~10 waits/s at the 100 ms default) instead of sleeping a 1 ms poll delay
  (~886 wakeups/s, measured 1.78 % of a core per idle second); the 1 ms poll delay remains the
  fallback when no signal is installed or the registration is refused, and the win32
  `HighResolutionTimerScope` (winmm `timeBeginPeriod(1)`, fail-open with a one-shot warn) still wraps
  the capture run.

## Validation & Error Matrix

| Condition | Result |
|---|---|
| Queue query native call fails (reachable only on the read's non-success path) | throw `Win32Exception` (existing `HasQueuedPackets` semantics, in `NdisNativeCallStatus.cs`) |
| `TryReadPackets` fails with a transient native error (21/170/1237/995/1167/31, `NdisNativeCallStatus.IsTransientReadError`; R7, 2026-08-30) | the pump retries with bounded exponential backoff (5 attempts, 100 ms base doubling, 1.6 s per-attempt cap → worst incident ≈3.1 s), rate-limited warn `adapter.retry`, counters `TransientReadRetryCount`/`TransientReadIncidentCount`; a healthy read closes the incident. The guard's probe is inside the driver, so it never consumes this budget |
| Transient retries exhausted, or a permanent read error (everything else, fail-closed conservative) | **degraded exit** (R7): the pump returns normally (no throw) — flush + batch-buffer release still run — and `onDegraded(nativeError)` fires once; the process and sibling adapter pumps keep running, the wiring restores just that adapter's mode, and the degradation log records the full native error so production ground truth can refine the table |
| Read succeeds with `dwPacketsSuccess == 0` (queue empty) | `TryReadPackets` returns 0; the pump takes its idle pacing — **no second call** |
| Read fails, query reports `queuedPacketCount == 0` (queue empty) | `TryReadPackets` returns 0; the pump takes its idle pacing — one query, no further read |
| Read fails, query reports a non-empty queue, **first** occurrence for this handle | the self-heal: arm the sticky query-first shape, publish one `NdisReadShapeMismatch`, probe once with `read(min(queued, capacity))`; probe success ⇒ its count, probe failure ⇒ throw `readError` |
| Read fails, query reports a non-empty queue, guard already armed (or the probe failed) | throw `Win32Exception(readError, "Unable to read NDISAPI packets from a non-empty queue (native error {e}, queued {q}, requested {n}, adapter 0x{h})")` — `queued` is the query's reading and `requested` the count of the call that failed, i.e. the pre-F5 message field for field |
| Driver fills `dwPacketsSuccess` > requested count | clamped to the request count (defensive; `ClampReadCount` in `NdisNativeCallStatus.cs`) |
| `buffers` empty | 0 (parameter validation) |
| `buffers` contains a null entry | `ArgumentNullException` from `BuildMultiRequest` — on **every** drain, because the request always covers the whole caller array (the old `0` half was an artefact of the queue-first guard; the pump's buffers are never null) |
| Packet-event registration refused (`SetPacketEvent` native FALSE, or a DLL without the export ⇒ `nativeError` 0) | one rate-limited `capture.packetEvent.unavailable` warn carrying the adapter and native error; that adapter's pump keeps the sleep-poll shape; capture continues — the event is an optimization, never a correctness dependency |
| Packet-event registration succeeds | one auto-reset event per adapter per generation; the pump parks in one bounded `Wait(IdleWaitTimeout)` per idle iteration; the loop disposes the signals **after** the pumps have stopped, releasing the registration with a NULL `SetPacketEvent` (best-effort) and then closing the event |
| Arrival signal lost between an empty read and the wait | the 100 ms bound re-reads; auto-reset additionally retains a signal raised while no waiter is parked, so a lost wake costs latency, never a packet |
| Lease frame not array-backed at an in-place rewrite point | fail-closed `TcpRedirectOutcome.Blocked`, `reason=rewrite` |
| Pool return above capacity (256) or after drain | buffer freed immediately, never double-returned |
| `MultiAdapterCaptureLoop` given an `arrivalSignals` list whose count differs from `bindings` | `ArgumentException` (the list is positionally paired) |

## Good/Base/Bad Cases

- Good: a full batch of 32 frames flows through classify → dispatch → in-place rewrite → pool-rented
  inject with zero per-packet managed allocations beyond the single `ArrayPool` copy and zero native
  allocs.
- Base: zero-length frame (`ArrayPool.Rent(0)` returns an empty array; safe), partial batch (n <
  capacity) processed in order.
- Bad: returning the pooled array from a lease `TryComplete` callback — the executor then reads a
  reused array (torn frame); storing `lease.Frame` in a session without copying and reading it after
  `ProcessAsync` returns.

## Tests Required

- `NdisApiAbiTests`: read classification matrix — `ClampReadCount` (partial read, over-reporting
  driver, empty success) and `ThrowReadFailedOnNonEmptyQueue` (native error, message text with the
  queue depth and the requested capacity).
- `NdisApiReadShapeTests` (F5, 2026-10-01): the per-drain call shape at the driver↔native seam, driven
  through `NdisApiDriver.CreateForTests` — one read and no query for a non-empty drain; a query never
  precedes a read; both empty-queue ABI hypotheses (success-with-0 and failure-with-query-0) yield 0
  with no error; a failed read with a failed query throws the query's error; a null slot throws on a
  drain that would otherwise be empty; the `NdisReadDiagnostics` counter sites; the guard facts
  (`AMismatchHealsTheShapeInsteadOfThrowing`, `TheGuardFiresExactlyOnceAndLaterDrainsAreQueryFirst`,
  `TheGuardNeverFiresOnAConformingDriver`, `AGuardedProbeThatAlsoFailsThrowsTheReadErrorLikeToday`,
  `TheMismatchDiagnosticCarriesTheAdapterRequestedDepthAndError`, and
  `TheMismatchNeverReachesTheTransientRetryBudget`, which drives a real pump over a real test driver);
  and `DriverReadPathAllocatesNoManagedBytes`.
- `NdisCapturePumpTests`: in-batch ordering with enumeration-handle stamping, partial batch,
  empty-batch poll, exactly-once batch-buffer release, capacity/null argument checks;
  `IdlePollIterationsAllocateNoManagedBytes`.
- `NdisCapturePumpIdleWaitTests` (F5): one bounded wait per idle iteration at the configured bound
  with no sleep pacing; the sleep fallback without a signal; a real `EventWaitHandle` wake ending the
  wait early; disposal bounded by the timeout while parked; `IdleWaitIterationsAllocateNoManagedBytes`
  over the production signal.
- `CapturePumpReadCallTests`: one read per poll and per batch; one read + one wait for a
  signal-installed idle iteration; `CountingReaderIdleIterationsAllocateNoManagedBytes`.
- `MultiAdapterCaptureLoopArrivalSignalTests` (F5): arrival signals are positionally paired with their
  bindings, a count mismatch is rejected, and every signal is disposed exactly once and only after the
  pumps have stopped.
- `FlowDispatcherExecutorTests`: completion callback fires exactly once across double `TryComplete` +
  `Dispose`; `Dispose` is a completion path; plain constructor has no callback; pooled copy is
  byte-identical at actual length.
- `TcpProxyCoordinatorRewriteTests`: `TruncatedSynRewriteFailureLeavesFrameByteIdentical` (parse
  failure leaves the frame byte-identical, returns Blocked, releases resources) — the in-place-rewrite
  no-intermediate-state lock.
- `NdisPacketBufferPoolTests`: rent/return round-trip reuse, over-capacity free, 64-thread rent
  uniqueness, double-`Dispose` no-op, drain-then-rent, foreign-buffer rejection, private-buffer (pump)
  dispose semantics regression.

**Related**: task `08-27-fix-datapath-throughput` (prd/design/implement artifacts hold the full audit
tables); the parent `08-27-fix-eof-reset-design-flaws` maps the throughput bottleneck to the RST/EOF
frequency symptom.
