# Native Lease and Pool Lifetime: rents, reinjection, and the GC-off posture

> The lifetime rules of everything rented on the packet path — native leases, in-place reinjection,
> the pool family, pooled flow/setup state — and the GC posture that makes zero-allocation hold.
> Part of the [hot-path family](./hot-path.md); read it when you add a rent site or change a pool,
> `SetupExecutor`, `PrepareForReinjection`, or the CLI GC config (the M1–M5 contract, task 09-18).

## What can be rented

- `NativeBufferPool(int bufferSize, int capacity = DefaultCapacity)` with `DefaultCapacity = 256`
  (`src/WinForward.Core/NativeBufferPool.cs`): `Rent()` → `NativeLease`, `Dispose()`, and `Stats`
  with interlocked `Rented` / `Returned` / `InPool` / `DisposedCount` / `OverflowAllocations` /
  `Outstanding`. The accounting sink is the settable property `Action<bool>? AccountingSink`, **not**
  a constructor parameter. Overflow allocates-and-tracks instead of failing.
- `NativeLease` (struct): `Span<byte>`, `Memory<byte>` — backed by `NativeMemoryManager :
  MemoryManager<byte>`, one manager per fresh native allocation, never per rent — and an idempotent
  `Dispose()`. `MemoryManager.Pin` returns the raw pointer and `Unpin` is a no-op (native memory
  never moves), so `NetworkStream`/`Socket` async IO can consume `lease.Memory`.
- `SetupExecutor` (`src/WinForward.Runtime/SetupExecutor.cs`): MPMC `ConcurrentQueue` ring +
  `SemaphoreSlim` signal + dedicated `Thread` workers (`s_defaultWorkerCount = max(2 × CPU, 16)`,
  `DefaultRingCapacity = 2_048`), with `RentItem`/`TryEnqueue` as its only surface. Per-flow
  `Task.Run` is replaced by `TcpProxyCoordinator.StartPendingSetup`/`LaunchSetup`.
- `FlowTable(capacity)`: `_states`/`_transportIndex` pre-sized dictionaries plus a `FlowState[]` free
  list; `FlowState.Reset(FlowKey, FlowDecision, long generation, long activityBucket)`
  re-initializes in place; `RemoveExpired` returns states.
- `UdpProxyCoordinator.ReceiveWindowSize(int maximumFrameSize) = frame + 22 + 1` — the single source
  of truth shared by composition and the receiver.
- `Socks5UdpTransport` caches `_relaySocketAddress = relayEndpoint.Serialize()` in its constructor.
- `RuntimeHeartbeat`: `RuntimeGcSnapshot(Gen0Collections, Gen1Collections, Gen2Collections,
  AllocatedBytes)`, a startup mark, an injectable snapshot provider, and the `WarnGcCollected`
  warn-on-new-collection event.

The pool's size-class pools are the relay pump (64 KiB), the UDP receive window
(`ReceiveWindowSize`), the SYN copy and the setup slot.

## Native lease lifetime

- Every rent is `using`/`try-finally`; release exactly once per rental. Release is idempotent across
  copies of a lease (an in-band `int` state cell, `StateRented → StateIdle`), and a return racing
  `Dispose` is freed by exactly one drainer — no stranding, no double free.
- A release from a **stale copy** after the buffer was re-rented is a rental-contract violation (no
  in-tree violator): never double-release, and never retain a lease past its owning scope.
- `lease.Memory` is valid **only until release**. It is the documented bridge for async IO over native
  memory; do not hold it across a release.
- `PacketLease.TakeNative(IFrameSource)` recycles per thread. The native frame stays valid for the
  whole dispatch — the pump awaits each handler, so batch slots cannot be reused earlier, which
  `PumpDoesNotReuseBatchSlotWhileHandlerIsInFlight` locks. Consumers that keep frame bytes past the
  synchronous section must read `Lease.Frame` (lazy `ArrayPool` materialization; `Release()` in the
  processor's `finally` returns it). Parse/classify run directly on the native span.

## In-place pass reinjection

- An unmodified pass frame reinjects its own capture buffer:
  `PrepareForReinjection(enumerationHandle)` retargets only the adapter handle (and zeroes
  `UnionPadding`); DeviceFlags/Flags/Length/payload stay as captured. Materialized or buffer-less
  packets take the pooled-copy fallback. The enumeration handle is **not** the captured `m_hAdapter`
  — see [windows-ndisapi.md](./windows-ndisapi.md), "Adapter Handles: Enumeration vs Captured".
- Since 2026-09-29 the TCP redirect data legs do the same: a pump-dispatched packet whose lease never
  materialized is rewritten **on its capture slot** and the slot is queued into a per-(adapter,
  direction) injection lane, retained past the synchronous section only until that iteration's flush
  — which runs before the next read and, at loop exit, before `ReleaseBatchBuffers()`. The rewrite is
  same-length (endpoint + checksum rewrite, MAC swap), the direction flag and enumeration handle are
  restamped, and the pre-rewrite bytes are read first by the sequence trackers.
- Never retain such a slot beyond the flush, and never treat the capture buffer as read-only after a
  redirect leg has handled it. The lane mechanics are
  [ndis-batched-send.md](./ndis-batched-send.md#redirect-deferred-injection-lanes).

## Pooled flow state

- `FlowTable.RemoveExpired` returns states under the gate; `FlowTable.TryResolveLocked` calls
  `FlowState.TouchBucket(ActivityClock.Current)` before returning, so a live state cannot be
  idle-expired.
- Reuse means a `FlowState` reference held **past** expiry could observe the next flow's fields:
  callers must read state within the gate-held / `TouchBucket`-refreshed operation, and no production
  path retains a `FlowState` across an `await`.
- Since F2 (2026-09-30) the warm entry reads the validated view instead of the instance:
  `FlowTable.TryResolveWarm` returns a `FlowStateView` produced by the state's barrier-bracketed
  seqlock (`TrySnapshot`) plus a transport-tuple corroboration, so a recycled or torn state is a
  false miss, never a wrong decision. The gated `TryResolve`/`TryClaimResolved` still hand out the
  pooled instance; the pre-existing post-gate ABA on the slow/claim path is a recorded follow-up.
- `FlowState.Reset` writes the state's fields including the activity bucket; the sweep that retires
  states, the warm cache that serves them and the deadline comparison all live in
  [warm-path-dispatch.md](./warm-path-dispatch.md).

## Owner-table slots

`WinForward.Windows.OwnerTable` is the reusable slot behind process attribution's owner-table scans.
One slot per `OwnerTableKind` is refilled in place by `IPHelperOwnerTableReader` /
`IPHelperOwnerTableParser` and searched under `ProcessOwnerTableCache`'s per-kind gate, which is what
keeps a scan from allocating even though it is a system-wide enumeration:

- Rows carry `IPAddressValue` by value and the slot's arrays grow by doubling to the widest table
  seen and are then reused: no per-scan row array, no per-row `IPAddress`.
- The gate covers the **search**, not only the read. One shared slot replaces the immutable snapshot
  the design used to publish, so a search must never overlap a refill; the search is a linear scan on
  a setup worker, and the cache's reuse/negative/coalescing rules are unchanged.
- `BeginUdpFill`/`BeginTcpFill` invalidate the slot before any row is written and `CompleteFill`
  publishes it, so an interrupted fill is unsearchable and a failed scan cannot leave stale answers.
- A scan is affordable only because its rate is the new-flow rate, not the packet rate: the counter
  `attributionOwnerTableScans` tracks it. Its zero-allocation gate is
  `IPHelperOwnerTableParserTests.AFillAndItsLookupsAllocateNothingInSteadyState`, which is why the row
  decode lives behind `IPHelperOwnerTableParser` rather than inside the `iphlpapi` boundary — the
  gate must run on a host without the native tables.

## SetupExecutor

- Per-item exception containment (`TrySetException`), balanced pending/enqueued/completed/rejected
  counters, lazy worker start.
- `Dispose` joins workers, drains queued items (canceling their completions) and refuses new work; a
  worker that dequeues after `_disposed` drains that item instead of executing it.
- Shutdown races settle on **both** sides: `TryEnqueue` rechecks `_disposed` *after* the ring append
  and drains on the enqueuer's side, and a worker whose semaphore or shutdown source was disposed
  under it exits like a cancelled one instead of faulting its thread unhandled. Measured on
  `SetupExecutorDisposeRacingTheFirstEnqueueLeavesNoItemUnsettled` (512 natural attempts, no seam):
  without the worker guard the test host aborted inside the first hundred attempts on 3/3 runs;
  without the post-enqueue recheck, 1,997 of 1,998 accepted items were stranded with their
  completions never settled; with both, green over 2,000 attempts. The settlement half fails an
  assertion; the worker-abort half cannot (a process abort is not assertable), so it stays covered by
  that recorded evidence.

## GC-off posture (CLI)

`WinForward.Cli.csproj` sets `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`,
`RetainVMGarbageCollection=false`, and the fuse `System.GC.HeapHardLimit` (bytes) through
`<RuntimeHostConfigurationOption>`. There is **no** MSBuild GC property for this key; the explicit
item is what guarantees the runtimeconfig.json entry in both JIT and AOT publishes. The fuse value is
documented in-csproj with its measurement basis. A misspelled key or an MSBuild property in its place
leaves the fuse silently absent.

## The `gc-soak` contract

- Steady state = established flows only: no new setup inside the measured window, since
  per-connection BCL allocations are out of scope.
- The scenario asserts the application-level guarantee: UDP forward lanes stay within the leak
  allowance (`max(64 KiB, sends/512)`, far below any per-datagram leak); pools perform **no fresh
  overflow allocation** during the window (summed `OverflowAllocations` unchanged); the working-set
  slope/growth stay flat.
- **After teardown**, every pool drains (bounded wait, not a fixed sleep) to `Outstanding == 0` with
  the conservation identity `OverflowAllocations == DisposedCount + InPool + Outstanding`.
- It *reports* process-wide `gen0/1/2` and allocated bytes. Cumulative `Outstanding` is deliberately
  **not** asserted inside the window: established relays legitimately finish and return their leases
  mid-soak, and a return is not a leak. Process-wide "gen counts unchanged" is not assertable on the
  loopback harness; the heartbeat `gc.collected` alarm is the field signal.

## Gates must exercise the real production collaborator

A gate built on a fake that implements the same interface cannot observe an allocation trap inside
the real collaborator. `Socks5UdpTransport` handed a fresh `EndPoint` to `Socket.SendTo`/`SendToAsync`,
which serializes it to a `SocketAddress` — **72 B per relay datagram** on the product's main path —
while every fake-transport gate stayed green. When a path's real collaborator can allocate, add at
least one gate against the real one (loopback) or a dedicated regression test. See
[udp-datagram-path.md](./udp-datagram-path.md) for the per-datagram consequence.

```csharp
// Wrong: a fresh EndPoint reaches the kernel every datagram — SocketAddress serialization
// allocates 72 B/op, invisible to any fake-transport gate.
_socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, relayEndpoint);

// Correct: serialize once in the constructor; the send sites stay allocation-free.
_relaySocketAddress = relayEndpoint.Serialize();
_socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, _relaySocketAddress);
```

Not to be re-derived, as three one-line rules: assert the application-level per-thread guarantee
(leak-bounded) and *report* process-wide gen counts — a process-wide zero-GC assertion can never hold
on a loopback harness; gate the soak window on fresh overflow growth only, never on full pool-state
equality across the window; and fence a real relay socket with a loopback-backed gate rather than
another fake.

## Outcomes a reader must be able to predict

- Rent N → return N → dispose: `Stats.InPool == N`, `Rented == Returned`, `Outstanding == 0`.
- Return racing `Dispose`: freed by the drain or the return's post-enqueue recheck, exactly once.
- Second release of a lease copy: no-op (the in-band state is already idle).
- Release of a stale copy after re-rent: contract violation — it would enqueue a buffer another
  renter holds. Do not write such code.
- Pool at capacity with everything in use: overflow allocates and increments `OverflowAllocations`;
  it never fails mid-packet.
- `NativeLease.Memory` used after release: invalid — the memory may be re-rented.
- Warm entry hands the real relay socket an `EndPoint`: 72 B/datagram (forbidden); use the cached
  `SocketAddress`.
- Any lease still held after full teardown: post-teardown `Outstanding != 0` fails the soak.
- `SetupExecutor.TryEnqueue` after dispose: refuses, the item is completed/rejected, and no
  `ObjectDisposedException` escapes; a post-dispose worker dequeues and drains the item.
- `FlowState` reused after expiry: the caller must not observe it past the gate-held operation.

## Tests required

- `NativeBufferPoolTests`: rent/return balance, dispose-drain races
  (`ReturnsRacingDisposeNeverStrandBuffers`), idempotent release, `Memory` round-trip, 0 managed bytes
  on warm re-rent.
- `NdisPacketBufferPoolTests`: L1 (overflow-throw still returns the buffer) and L2 dispose-drain.
- `FlowTableClaimAndExpireCycleAllocatesNoManagedBytes` and
  `FlowTableRecyclesExpiredStatesThroughItsPool`.
- `SetupExecutorTests`: balance, reject-beyond-capacity, fault-keeps-draining, dispose
  joins/drains/refuses, and the dispose/enqueue race above.
- `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes`: real relay socket, warm
  synchronous `SendSpanAsync` completes on the calling thread with 0 B (the `EndPoint`-trap
  regression).
- `HotPathAllocationGateTests`: per-packet 0 B gates (TCP mid-flow/reverse, UDP, socks5, RST, SYN
  retention, FlowTable, dispatcher warm path).
- `GcSoakScenarioTests`: token parsing/defaults/selection, leak allowance, slope helper.
