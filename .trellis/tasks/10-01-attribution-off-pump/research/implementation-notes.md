# Implementation notes — F8 process attribution off the pump (pending-flow pipeline + owner-table epoch coalescer)

> Read at planning time on tree `ba51205` (2026-10-01), after F5 (`10-01-pump-io-shape`) was archived, and
> revised after the independent review. Every `file:line` below was verified with `rg -n` on that tree; §13
> lists the anchors the review corrected. Where the archived research (`09-29-tcp-udp-path-structural-perf`
> addendum §A3) and the code disagree, the code wins and the disagreement is in §9.

---

## 1. The current attribution sequence, exactly, and its pump-thread call chain

The whole chain below runs **on the capture pump's dedicated thread** (`WinForward.CapturePump.<handle>`,
`src/WinForward.NdisApi/NdisCapture.cs:194-200`) for every new *host* flow when any process rule is configured.

| # | Site | What happens |
|---|---|---|
| 1 | `NdisCapture.cs:297-343` `RunIteration`, per packet `:332-339` | `NdisCapturedPacket.FromCapture(_batchBuffers[index], _adapterHandle)` `:337` — the packet **is** the pump's batch slot |
| 2 | `NdisCapture.cs:366-385` `InvokeHandler` | calls `_handler(packet, ct)` `:368`; synchronously-completing → `GetAwaiter().GetResult()` `:374` (0 B); otherwise `pending.AsTask().GetAwaiter().GetResult()` `:383` **blocks the pump thread** until the handler's `ValueTask` completes |
| 3 | `Capture/CapturePacketProcessor.cs:52-111` `ProcessAsync` | parse `:76`, classify `:83`, `await _dispatcher.DispatchAsync(...)` `:84`; `finally { lease.Release(); }` `:109` |
| 4 | `FlowDispatcher.cs:165-201` `DispatchAsync` (warm entry) | trace `:170` → reverse claim `:174` → wildcard self-traffic `:176` → `_flows.TryResolveWarm` `:177`; a miss falls to `DispatchSlowAsync` |
| 5 | `FlowDispatcher.cs:203-258` `DispatchSlowAsync` | full self-traffic `:208`, reverse `:215`, `_flows.TryResolve` `:217`; on a table miss → **`await AttributeProcessAsync(context, ct)` `:237`** |
| 6 | `FlowDispatcher.cs:282-292` `AttributeProcessAsync` | gate `:284`: `!_policy.RequiresProcessAttribution \|\| _attributor is null \|\| context.Process is not null \|\| context.Key.Origin != FlowOriginKind.Host` → return; else `await _attributor.FindAsync(context.Key, ct)` `:285` |
| 7 | `Windows/ProcessAttribution.cs:28-39` `FindAsync` | non-Windows → `null` `:30`; **`FindOwnerSafely` `:31` runs synchronously on the caller** (no await precedes it); on `null` → `await Task.Delay(2 ms)` `:34` → **`FindOwnerSafely` again `:35`**; `ReadProcessIdentity` `:38` |
| 8 | `ProcessAttribution.cs:41-58` | `FindOwner` `:43-47` dispatches on protocol to `IPHelperTables.FindUdpOwner(key.Local)` / `FindTcpOwner(key.Local, key.Remote)`; `FindOwnerSafely` swallows `Win32Exception`/`InvalidOperationException`/`ArgumentException`/`UnauthorizedAccessException` as "no owner" |
| 9 | `ProcessAttribution.cs:179-196` | `FindUdpOwner`: full table read + `Where(...).Select(...).Distinct().ToArray()` `:187`; `FindTcpOwner`: same at `:194` |
| 10 | `ProcessAttribution.cs:198-287` | `ReadTcp4/ReadTcp6/ReadUdp4/ReadUdp6` materialize `new TcpOwner[rowCount]` (two `new IPAddress` per TCP row, `:242`/`:259`) after `ReadTable` `:266-287` — **two iphlpapi calls per read** (size probe `:269-272`, then the real call `:274-281`) plus `Marshal.AllocHGlobal`/`FreeHGlobal` |
| 11 | `ProcessAttribution.cs:61-137` `ReadProcessIdentity` | `Process.GetProcessById` `:65`, `process.StartTime` `:66`, identity cache under `_cacheGate` `:68-83`, `ProcessName` `:73`, `TryGetFullProcessImagePath` `:74` → `SafeProcessHandle.Open` (`OpenProcess`) `:112,150`, `QueryFullProcessImageName` `:124` |
| 12 | `FlowDispatcher.cs:239-245` | `_flows.TryClaimResolved(context.Key, () => EvaluateNewFlow(context), out claimed)` `:239`; capacity refusal → `LogCapacityBlock` + Block `:241-244` |
| 13 | `FlowDispatcher.cs:247-257` → `:377-421` | `packet with { Context = context, FlowGeneration = … }` `:247`; `ExecuteDecisionAsync` → `CompleteAsync` → `_executor.PassAsync/BlockAsync/ProxyAsync` |
| 14 | `Capture/NdisPacketActionExecutor.cs:71-97` `PassAsync` | native in-place `:76-85` (calls `captureBuffer.PrepareForReinjection`) **or** `_bufferPool.Rent()` + `SetFrame(packet.Lease.Frame.Span, …)` `:91-93` → `AppendPass` `:99-142` |
| 15 | `NdisCapture.cs:341` → `DurableCaptureBundle.cs:397-405` `FlushPendingInjections` → `NdisPacketActionExecutor.cs:190-224` | the pump's per-iteration callback ticks the activity clock, flushes the (adapter, direction) pass lanes as one batched IOCTL per lane, then flushes the TCP redirect lanes |

Consequences that matter for this task:

- **The first table scan is always on the pump thread** (`:31`, before the first await). The 2 ms `Task.Delay` blocks the pump thread inside `InvokeHandler`; the *second* scan then runs on a thread-pool thread while the pump stays parked in `AsTask().GetAwaiter().GetResult()` (`NdisCapture.cs:383`).
- Per new host flow: up to **2 table reads × 2 syscalls = 4 iphlpapi calls**, a managed copy of the whole owner table per read, a `Distinct/ToArray` per read, and one `OpenProcess`+`QueryFullProcessImageName`.
- The pump blocks **every** flow on that adapter for the duration (0.5–7 ms per the research; the retry adds ~2 ms + a pool hop).
- **An exception that escapes `FindOwnerSafely` does not stop one flow — it stops the whole capture run.** `CapturePacketProcessor.ProcessAsync` rethrows (`:96-106`), `NdisCapturePump.RunLoop` exits (`:245-248`), and `MultiAdapterCaptureLoop.RunPumpAsync` catches, cancels the linked source and rethrows (`:142-157`), so every adapter's interception ends. This is the baseline for the accepted delta in `design.md` §5.4.1.
- `FlowDispatcher.cs:284` means shapes A (no process rules) and C (forwarded origin) never enter this chain — the §A2 split holds in the code.

## 2. The attribution seam, and how a fake drives it

- `src/WinForward.Windows/IProcessAttributor.cs:5-10`: `readonly record struct ProcessIdentity(string? Name, string? FullPath)` `:5`; `ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken)` `:9`. The seam takes a **`FlowKey`**, not the packet — an attributor can never retain frame bytes, and the pending pipeline must carry the key/context itself.
- Injected at `FlowDispatcher.cs:111,119,129`; the **only production wiring** is `src/WinForward.Cli/DurableCaptureBundle.cs:272-273` (`new WindowsProcessAttributor()`), so there is no composition-level injection point: seam facts construct a `FlowDispatcher` directly.
- Test fake today: `tests/WinForward.Core.Tests/TestHelpers/CapturePipelineFakes.cs:27-36` `FakeAttributor` — counts `Calls`, returns `name is null ? null : new ProcessIdentity(name, null)`. It is **synchronous**, so it can model neither a stall nor a thread. Existing users: `FlowDispatcherExecutorTests.cs:21,42,129,229`. The new facts need a gated fake that records `Environment.CurrentManagedThreadId`, can sleep a configured cost, and can throw.
- **The owner-table provider has no seam.** `IPHelperTables` is `internal static` (`ProcessAttribution.cs:172`) with a private nested `Native` (`:317-324`); `src/WinForward.Windows/Properties/AssemblyInfo.cs:3` already grants `InternalsVisibleTo("WinForward.Core.Tests")`, so an internal reader interface is reachable from tests without widening the public surface.
- **The non-Windows short-circuit is at the top of the public seam** (`ProcessAttribution.cs:30`), so on this host *no* snapshot/cache logic reached through `FindAsync` can execute. The design moves the platform guard into the (platform-attributed) default reader, which keeps the managed cache logic exercisable here with a fake reader and keeps CA1416 quiet because the interface itself is not platform-attributed. `IPHelperTablesBoundsTests` already exercises part of this file on Linux.

## 3. The two pending templates, and exactly what F8 may copy

### 3.1 TCP: `TcpRedirect/TcpPendingSynSetup.cs` (retention + index + worker hand-off)

- Entry `PendingSynSetup` `:13-41`: `NativeLease RetainedFrame` `:21`, `int RetainedLength` (the charged bytes) `:23`, `FlowContext Context` `:25`, `PacketCaptureMetadata Metadata` `:26`, `PacketLayout Layout` `:34`, `long PacketSequence`/`FlowGeneration` `:35-36`, `DateTimeOffset LastWriteUtc` `:37`, `TaskCompletionSource SetupCompletionSource` `:40`. Mutated only under the index gate (`:11`).
- Index bounds: `DefaultCapacity = 1024` `:56`, `DefaultGlobalByteBudget = 1 MiB` `:63`, retention TTL 5 s `:69`, per-flow setup-failure cooldown 1 s `:77`, leaf `Lock _gate` `:79` ("never taken while holding the store, table, or tombstone gates" `:52`).
- Refusal accounting: `_capacityRejectionCount` incremented at `TryRetain`'s cap/budget branch `:169` (while `TryChargeBytes` *also* increments `_budgetRejectionCount` `:345`, so that one case double-counts — D7; copy the "one observable refusal counter" idea, not the arithmetic).
- `TryRetain` `:142-189`: an existing key is **overwritten in place** (charge-new, credit-old, dispose-old, `created = null` → the caller must **not** start a second setup task `:146-165`); a new key charges the budget and installs the entry `:167-187`; refusal leaves ownership with the caller.
- Budget: `TryChargeBytes` `:340-350` rolls the charge back and counts on exhaustion; `CreditBytes` `:352`; exactly-once credit at every sink — overwrite `:161`, completion `:220`, TTL `:252`, drain `:286`.
- Drain order: `AttachSetup` prunes completed task references on the next attach `:201`; `Complete` uses a `ReferenceEquals` guard so a TTL-reclaimed entry's later completion is a no-op `:217`; `RemoveAll` `:280-292` is the dispose drain; `DrainAsync` `:299-309` awaits every started task *including* tasks whose entries a TTL removed.
- Teardown interaction: `TcpProxyCoordinator.SetupPendingAsync` `:236-263` takes a `QuiescenceScope` work lease first (`_store.TryEnterSetup(out var lease)` `:242`, false → complete without a cooldown `:246`), and **completes the entry before releasing the lease** (`:257` then `:261`).
- Caller posture: index refusal → `lease.Dispose()` + count + trace + `Blocked` `:176-179`; ring refusal in `LaunchSetup` `:207-226` → the item's `_completion` is **settled by the pipeline itself** (see 3.3), `Complete(writeCooldown:false)`, warn, `Blocked` `:216-222`.
- Worker body rebuilds the packet from the retained **managed** copy: `new CapturedFlowPacket(new PacketLease(frame), entry.Context, entry.Metadata, entry.PacketSequence, entry.FlowGeneration, Layout: entry.Layout)` `RunSetupPipelineAsync:273` — note **no `NativeFrame`**.
- TTL sweep wiring: `TcpProxyCoordinator.cs:478` `_store.RemoveExpiredAsync(now, idleTimeout, _prunePendingSyn)`.

### 3.2 UDP: `BoundedSetupQueue` + `UdpSetupQueueBudget` + `UdpSessionSetup` (per-flow FIFO + flush)

- `Core/BoundedSetupQueue.cs:14-136`: dual-bound per-flow FIFO (`maxPackets`, `maxBytes`) `:27-33`; the first entry lives inline (`_pending`/`_hasPending` `:21-22,49-58`) and a `Queue<Entry>` materializes only on the second entry `:59-62`; `TryEnqueue` checks *both* bounds before touching the lease `:44-47`; `TryDequeue` `:71-104`; `RefreshEnqueuedStamps` `:113-135`. **Not thread-safe by design** (`:12`).
- **F8 cannot use it as the retained-packet payload**: `Entry` is `(NativeLease Lease, int Length, DateTimeOffset EnqueuedAt)` `:19` and cannot carry the `Metadata`/`PacketSequence`/`Layout` a rebuilt packet needs, and the `Queue<Entry>` allocation at `:59` breaks a "0 B per additional packet" claim. F8 uses a fixed-capacity per-entry ring instead.
- Global byte budget: `UdpSetupQueueBudget.cs:23` `SetupQueueGlobalByteBudget = 8 MiB`; `TryCharge` `:44-55` (rollback + `_rejectionCount` + `RuntimeCounters.UdpSetupBudgetRejections`); `Credit` `:58`; `NoteDrop` `:64-75`; `AddDroppedTotal` `:78`.
- Admission under one gate: `UdpProxyCoordinator.Send.cs:55-111` — `ObjectDisposedException.ThrowIf(_scope.IsSealed, this)` `:62`, cooldown `:64`, session capacity `:72-78`, `ScheduleSessionSetup` `:88-93` (**one setup item per admitted session**, so the ring's UDP demand is bounded only by the 16,384 session capacity), `slot.Ready` re-check under the same gate `:98-106`.
- Flush + no-bypass: `UdpSessionSetup.FlushSetupQueueAsync:196-226`; `host.DequeueForFlush` returns `QueueEmpty` **and flips `Ready` in the same critical section** (`FlushStep` doc `:79-80`), and the flip only happens on the *next* dequeue call, after the previous datagram's send completed `:200-201`. That ordering argument is the one F8 reproduces (with the claim replacing the flip).

### 3.3 What F8 must add that neither template has

1. **Multi-packet retention per flow** with per-packet metadata (the TCP index stores one frame; the UDP FIFO cannot carry metadata).
2. **A decided-but-undelivered state**: neither template has one — UDP flushes immediately after the decision, TCP's worker *is* the delivery.
3. **A claim-after-delivery ordering**, because the flow table is a different structure from the pending index and the warm path must not overtake undelivered packets.
4. **`SetupExecutor.TryEnqueue`'s unsettled completion**: `TryEnqueue` returns false after `Recycle(item)` (`:191-197` via `Recycle:220-224`) **without** completing `item._completion`, so a caller that only checks the bool strands its awaiter. `TcpProxyCoordinator.LaunchSetup` settles it itself (`:218`); F8 must do the same.

## 4. The claim/policy path per §A2 shape, and the miss behaviour

- Shape A — no process rules: `AttributeProcessAsync` returns at `:284` without touching the seam. Claim at `:239` evaluates `EvaluateNewFlow(context)` `:374-375` → `_policy.Evaluate(context)` (host) / `_policy.EvaluateForwarded(context)` (forwarded).
- Shape B — any rule carries `Processes`: the F8 site.
- Shape C — `FlowOriginKind.Forwarded`: skipped by `:284`'s origin test, so forwarded churn never pays attribution. **F8 must not defer it either**: the eligibility gate is shared with `AttributeProcessAsync`, and a negative fact pins that a forwarded (or no-rule) miss creates no pending entry.
- `Policy.Evaluate` runs **inside** `FlowTable.TryClaimResolved`'s `_gate` today (`Core/FlowTable.cs:242-264`, the `decide()` factory invoked at `:254`), which the research §A2 note records. F8 moves evaluation to the worker (before the claim) — a stated delta (`design.md` §5.4.3) that must not create a flow-table → pending-index lock edge.
- `TryClaimResolved`'s doc `:236-241`: "policy is evaluated exactly once per logical flow" — the second entry a cross-adapter duplicate key produces (§9.11) is the one shape that pays a second evaluation; the claim still returns the existing decision.
- **A miss is not a refusal.** `null` from the attributor reaches `FlowDispatcher.cs:286-290`: `LogAttributionMiss(context)` then `return context` — the context is unchanged and policy evaluation continues without a process match. `LogAttributionMiss` `:321-330` increments `RuntimeCounters.AttributionMiss` (`RuntimeCounters.cs:29`) and emits a 5 s-throttled `flow.attribution-miss` warn with `afterRetry=true`. The live dispatcher fact is **`DispatcherFallsBackWhenHostAttributionIsUnknown`** (`FlowDispatcherExecutorTests.cs:127`).
- Because a process rule cannot match a process-less context, a miss means "a process rule silently did not apply" — which is why a *stale positive* attribution (a snapshot row whose socket has been recycled) is the dangerous direction, not a miss (§5, §9).

## 5. The owner-table snapshot's consumers, predicates and staleness

- Exactly **one** consumer of the owner tables: `WindowsProcessAttributor.FindOwnerSafely` (`:51-58`), reached only from `FindAsync` `:31,35`.
- The identity's only consumers: `FlowDispatcher.AttributeProcessAsync` `:285,291` (sets `FlowContext.Process`) → `RuleMatcher`'s process matching (`traffic-policy-lifecycle.md:16`) and the `flow.created` / `packet.*` log fields (`FlowDispatcher.cs:251-255,434-435,447-448`).
- **The predicates, exactly** (the cache must reproduce both): `FindUdpOwner` `:187` matches `row.Port == local.Port` **and** (`row.Address == wildcard` **or** the row address equals the local address), then returns the PID only when the distinct-PID set has **exactly one** element. `FindTcpOwner` `:194` requires all four tuple fields (local port + remote port + local address + remote address) equal, under the `TcpTableOwnerPidAll` class (`:177`; the TCP read sites are `:234,:251`).
- Two staleness directions, only one of which the research names:
  1. **Young socket → no row**: absent from an old snapshot. The research calls this "already covered by the existing attribution-miss path" (`research.md:414-416`) — correct but *incomplete*: the PRD forbids letting it be a miss (that would silently un-apply every process rule), so the snapshot lookup must fall through to a real scan (`design.md` §3.2).
  2. **Recycled port → obsolete row** (the research does not mention it): a snapshot row for a local port whose socket has since closed and been recycled attributes the flow to the **old** process. UDP matches the local port alone (§ above), so a UDP recycle inside the window is a concrete false-positive path; TCP needs an exact-4-tuple reuse, which TIME_WAIT makes effectively impossible inside 250–500 ms. The creation timestamp that would close the UDP half lives in the `*_TABLE_OWNER_MODULE` classes, which the PRD puts out of scope (`OWNER_MODULE` is a recorded Windows follow-up, never an option here).
- **Structural correction to the research premise.** A TTL snapshot cannot answer a burst's *own* new flows: a flow's row appears when its socket is bound/connected, microseconds before the packet that triggers the lookup, so it cannot be present in a snapshot taken 250 ms earlier. "One page load = dozens of connections, one scan serves the burst" (`research.md:411-413`) only holds if the miss path **coalesces onto one refresh whose snapshot is published at/after the last requester asked**. The window's role is then only to serve later requests, and it is exactly the part that carries the §5.2 staleness risk. The design's lookup is therefore keyed by a per-request `requestInstant` (a snapshot older than the request cannot answer it; a snapshot published at/after it is accepted), which also makes the 2 ms retry coalesce instead of forcing its own scan.
- Memory shape: one parsed table per (address family, protocol) — bounded by the OS table size, not by flow count — replaced on every refresh.

## 6. Where the pending structure's lifetime must attach

- `DurableCaptureBundle.DisposeCoreAsync` (`src/WinForward.Cli/DurableCaptureBundle.cs:416-461`) is the ordered teardown: sweeper `:420` → UDP coordinator `:426` → UDP association pool `:434` → `_udpDatagramPool`/`_udpWindowPool` `:440-441` → TCP coordinator `:449` → `_synCopyPool`/`_relayPool`/**`_setupExecutor`** `:455-456`.
- The delivery hook is `FlushPendingInjections(adapterHandle)` `:397-405`: `ActivityClock.Tick()` → `Executor.FlushPendingPasses(adapterHandle)` → `Tcp.FlushPendingRedirectInjections(adapterHandle)`. It is the pump's per-iteration **and loop-exit** callback (`NdisCapture.cs:255,323,341`) and the **single** activity-clock tick of the data path (`DurableCaptureBundleTests.ActivityBucketClockTicksExactlyOncePerPumpIteration`). F8's delivery must live **inside** it and **before** `FlushPendingPasses`, so delivered pass frames are flushed in the same iteration; a delivery after the flush would strand them until the next iteration, and a frame still lane'd at loop exit is dropped with a warn by `RetireLanesExcept` (`NdisPacketActionExecutor.cs:252-282`).
- The dispatcher is **not disposable** today, so the pending structure needs an owner with a real teardown that the bundle can order: a pipeline module disposed after the sweeper and before the pools, with the shared `SetupExecutor` (which joins its workers) staying where it is.
- The sweeper cannot be the F8 TTL's only bound: the main-leg group that calls `_tcp.RemoveExpiredAsync` (and with it the TCP pending index) runs on the **60 s** main interval (`IdleExpirySweeper.cs:58,92,108-112,159`), while the UDP leg runs at `max(5 s, retention/2)` → 15 s by default (`:62,166-167`). So the TCP pending index's documented *5 s* retention TTL is enforced on a **60 s** cadence in production — a recorded discrepancy (D6) and the reason F8 gives the pipeline **its own sweep leg** while treating the TTL as a backstop: the delivery path, not the sweep, is what normally ends an entry's life.

## 7. Hazards, per candidate mechanism

1. **Retaining the lease instead of copying.** `PacketLease` is recycled through a `[ThreadStatic]` single-entry cache (`Core/PacketRuntime.cs:25-28,93-109`) and `Release()` "must be called on the SAME thread" (`:88-91`) because it recycles into the *calling* thread's cache `:116-135`. A retained lease cannot be released by the worker (it would park on the worker's cache) and the pump would then allocate a fresh lease object for every subsequent packet. → retain a **copy**; complete the lease synchronously with `Deferred` on the pump thread and let `ProcessAsync`'s `finally` release it.
2. **Copying through `packet.Lease.Frame`.** That materializes a pooled managed array per retained packet (`PacketRuntime.cs:69-85`) — a managed allocation on the pump the design forbids. → copy from `packet.InspectionSpan` (`FlowDispatcher.cs:51`, native-first).
3. **Carrying `NativeFrame` past the dispatch.** `NativeFrameHandle.Buffer` (`FlowDispatcher.cs:32`) is the pump's batch slot (`NdisCapture.cs:337`), valid only until the next read (`FlowDispatcher.cs:22-29`). A drained packet carrying it would take `PassAsync`'s in-place branch (`NdisPacketActionExecutor.cs:76-85`) and rewrite a recycled slot. → drained packets carry **no `NativeFrame`**.
4. **Oversized frame.** `NativeLease.Length` is the pool's fixed `BufferSize` (`NativeBufferPool.cs:44,174`); a `CopyTo` beyond it throws, and a throw after a partial write leaves the entry half-built. → check the length before the copy and refuse fail-closed.
5. **Worker-thread `PassAsync`.** The lane contract is explicit: "every `PassAsync` caller lives inside the pump's serialized batch-loop chain" (`NdisPacketActionExecutor.cs:17-20,144-153`); `AppendPass` is lock-free (`:133-141`) while `FlushLane` reads and zeroes `lane.Count` and then releases the first `count` buffers (`:201-224`). A concurrent worker append can be released unsent. → all delivery stays on the pump thread.
6. **Mutation during enumeration aborts the whole capture run.** A shared decided **list** mutated by a worker while `DeliverDecided` enumerates it throws `InvalidOperationException` out of `RunLoop`; `MultiAdapterCaptureLoop.RunPumpAsync` then cancels the whole run (`:142-157`). → a per-adapter **queue dequeued under the gate**, no enumeration, and the count decremented in the same critical section.
7. **A detached batch stranded by a throw.** If the batch leaves the entry's ownership before execution, any throw between detach and execution loses up to 32 leases. → per-batch `finally` Blocks the unexecuted remainder and counts it.
8. **A false claim.** `TryClaimResolved` returns false only at flow-table capacity (`FlowTable.cs:242-265`); an unchecked false leaves the flow unclaimed so **every** later packet re-admits and re-attributes (defeating `traffic-policy-lifecycle.md:13`). → keep the entry (`ClaimFailed`), Block its later packets, count, surface `flow.capacity-block`, retry the claim on the next arrival.
9. **Cancellation classified as a failure.** `FindAsync`'s delay/scan honour the scope token (`:34`); counting an `OperationCanceledException` as a setup failure would arm a cooldown on shutdown, contradicting `error-handling.md`. → its own arm: no cooldown, no failure counter.
10. **Delivery latency when the pump is parked.** `PaceIdle` (`NdisCapture.cs:353-357`) sleeps `PollDelay` (1 ms) or waits on the arrival signal for `s_defaultIdleWaitTimeout = 100 ms` (`:128`). → the wake signal (`design.md` §4.3), created where the driver signals are created.
11. **Two gates, one order.** The pending index gate and `FlowTable._gate` (`FlowTable.cs:39`) are distinct. The pump's admission needs pending → flow; the worker must never take flow → pending, because `TryClaimResolved`'s `decide()` runs **under** the flow gate (`FlowTable.cs:254`).
12. **Refusal must not become a pass.** Passing a packet whose flow has no decision leaks a would-be-proxied flow; "evaluate with no process match and claim" is equally fail-open. → Block or keep pending, always counted.
13. **Admission scope.** Deferring every flow-table miss would send forwarded flows and no-rule configs through a worker round-trip for nothing (and `traffic-policy-lifecycle.md:9` fixes forwarded eligibility immediately before the claim). The existing green facts cannot detect it (the decisions are identical), so it needs a negative fact.
14. **Shared `SetupExecutor` budget.** `DefaultRingCapacity` is 1024 (`SetupExecutor.cs:123`); F8 adds a third producer whose cap is also 1024, so the constant must rise to the sum — and the relation's load-bearing form is the test `SetupExecutorTests.DefaultRingCapacityCoversThePendingSynIndexCap` (`:165-174`), not the doc comment at `:117-123`.
15. **A pending flow re-enters the full slow path**, including the **gated** exact-tuple self-traffic check (`SelfTrafficRegistry.IsOwned`, `:16,29`) and the reverse probe, once per packet (`hot-path.md:1398-1401` fixes the half as once per claim). Bounded by the ring (32) and the TTL (5 s), recorded as an accepted delta.
16. **WF rules.** `_ = <awaitable>`, `Task.Run`, `Task.Factory.StartNew`, `ContinueWith` are build errors under `src/**` (`async-lifetime.md:264-271`). The F8 worker must be a `SetupExecutor` item or a `QuiescenceScope.Run` child.
17. **`PacketLease.Disposition`** is asserted by tests (`FlowDispatcherTests.cs:29-30`, `FlowDispatcherExecutorTests.cs:74-76,97-98,119-121,141`) and set exactly once (`PacketRuntime.cs:137-147`, the `_onCompleted` callback fires inside `TryComplete`). A deferred packet must still complete exactly once, on the pump thread.
18. **Retention-pool lifetime.** The bundle disposes `_udpDatagramPool`/`_udpWindowPool` before the TCP coordinator and `_synCopyPool`/`_relayPool`/`_setupExecutor` last (`DurableCaptureBundle.cs:440-456`). The F8 pool must be its own, drained by the pipeline, and disposed with the last block.

## 8. Candidate mechanisms, and why the alternatives lose

### 8.1 Snapshot shapes

| Candidate | Verdict |
|---|---|
| Immutable snapshot + single-flight refresh **keyed by the request instant** | **Chosen.** Delivers the coalesced-epoch property *because* the shared scan runs after every joiner asked; the TTL window is a separate, bounded, safer feature |
| Per-worker TTL snapshot, miss → independent fresh scan | Rejected: it does **not** reduce scans for a burst's own new flows (§5) — each of N concurrent new flows misses the old snapshot and scans |
| A `forceRefresh` flag on the retry | Rejected after review: with the flag the in-lock freshness recheck is skipped, so a retry arriving after another refresh published scans again and "one scan per epoch" stops being exact. The request instant subsumes it |
| Snapshot miss ⇒ "no owner" | Rejected: a process rule silently stops applying ⇒ fail-open |
| Cache inside `IPHelperTables` as a static | Rejected: untestable on this host, no seam, global mutable state |
| UDP snapshot with `OWNER_MODULE` creation timestamps | Rejected for this task: changes the pinned table class; recorded as the on-Windows follow-up |

### 8.2 Delivery shapes

| Candidate | Verdict |
|---|---|
| **Worker decides; pump delivers from a per-adapter decided queue; claim last under the gate** | **Chosen** (`design.md` §2.3): no executor change, no warm-path probe, no lease/lane hazard |
| A shared decided **list** enumerated outside the gate | Rejected: a worker enqueue during the drain aborts the pump and cancels the capture run (hazard 6) |
| Executing another adapter's frames (cross-adapter drain) | Rejected: unsafe (lane race) *and* unreachable — `FlowKey` equality includes the adapter slot + generation (`Domain.cs:87-91,155-159`), so each adapter admits its own entry |
| Draining the batch before executing it (detach-then-execute) | Rejected: a throw strands up to 32 leases (hazard 7); the per-batch `finally` over an entry-owned batch is the landing shape |
| Worker-side claim/delivery | Excluded by the amended PRD req 1 (it needs a warm-path pending marker + a thread-safe lane flush) |
| Keep attribution inline but cache more aggressively | Rejected: does not satisfy "never runs on the pump thread" |
| A dedicated attribution thread per adapter | Rejected: an extra OS thread per adapter re-implementing the pooled-worker + quiescence machinery |

## 9. Recorded discrepancies (code wins; PRD/research vs code)

1. **The pump no longer polls with `Thread.Sleep(1 ms)` at `NdisCapture.cs:142,318`** (research F5/A3 anchors). F5 landed: `PaceIdle` (`:353-357`) either sleeps `PollDelay` (default 1 ms, `:165`) **or** parks in one bounded wait on the injected arrival signal (`:128`, default 100 ms). The *stall* claim is unaffected; the idle anchors are stale.
2. **`NdisCapturePump` now owns no `IDisposable`** and its arrival signal is borrowed (`async-lifetime.md:131-133`); an F8-owned wake signal must follow the same borrowed/owned split.
3. **The `tcpChurn` scenario does not run the dispatcher, the pump or the attributor.** `TcpChurnScenario.RunAsync` (`TcpChurnScenario.cs:46-81`) builds a `TcpProxyRelayFactory` + `LoopbackSocks5TcpServer` + a `RateGate`, and `--attribution-delay-ms` is a `Task.Delay` on the **client worker** (`:114`); the option's own doc says it is "a synthetic per-flow stall … standing in for the pump-thread process attribution (research F8) that only runs on Windows" (`SoakOptions.cs:169-181`). The row is a sensitivity instrument for the relay-establishment path; **F8 changes none of its inputs**.
4. **"~91 KB of transient garbage per new flow" is not attribution's allocation.** The README claims it as "the other half of F8" (`benchmarks/results/2026-09-29-benchmark-coverage/README.md:192-194`), but the number is `allocatedBytesPerConnection` from a scenario that never calls `IProcessAttributor` — it is the churn path's own per-connection cost. It reproduces tightly across trees (90,968 recorded; 91,088 / 91,066 / 91,031 on 2026-10-01, spread 0.06 %) — a good *ceiling* for "the churn row must not get worse", never a measurement of the enumeration.
5. **The archived "stable-class p99 8.308 → 10.068 ms" movement at 5 % does not reproduce.** Three fresh runs give 7.458 / 7.428 / 7.465 ms (spread 0.5 %) with the delay on. The stable class is *not* moved by the synthetic stall; the pooled p99 is the only metric that moves (§11).
6. **The TCP pending-SYN index's 5 s retention TTL is swept on the 60 s main interval** (`IdleExpirySweeper.cs:58,92,108-112,159`), not on a 5 s cadence. Its doc comment (`TcpPendingSynSetup.cs:64-69`) describes a 5 s bound the wiring does not provide. F8 gives its own TTL a dedicated leg for this reason.
7. **`TcpPendingSynSetupIndex.TryRetain` double-counts a budget refusal** (`:169` and `:345`), so `RejectionCount` is not the sum it looks like. Copied only as "one observable refusal counter", not as arithmetic.
8. **The research's `InvokeHandler` anchor `:327-346` is now `:366-385`**; the pump-thread blocking contract itself is unchanged (`:379-381`).
9. **`FlowDispatcher` has no `DisposeAsync`/`IDisposable`**, so the pending pipeline needs a separately owned module with a teardown the bundle can order (§6).
10. **`Policy.Evaluate` still runs inside the flow-table gate** (`FlowTable.cs:254`); F8 moves it to the worker (a stated delta).
11. **`FlowKey` equality includes the adapter slot + generation** (`Domain.cs:87-91,155-159`), so one transport tuple observed on two adapters is **two keys**: two admitted entries, two attributions, two policy evaluations. The claim still returns the existing decision (`FlowTable.cs:236-241`), so the observable outcome is unchanged; the extra work is a stated delta (`design.md` §5.4.6) and is counted. A "canonical key" admission gate is the alternative, not taken.
12. **An escaping attribution exception today ends the whole capture run**, not one adapter: `CapturePacketProcessor.ProcessAsync:96-106` → `NdisCapturePump.RunLoop:245-248` → `MultiAdapterCaptureLoop.RunPumpAsync:142-157` cancels the linked source. F8's per-flow fail-closed arm is a delta in the safe direction (`design.md` §5.4.1).
13. **The delivery hook must be `FlushPendingInjections`** (`DurableCaptureBundle.cs:397-405`), not a new pump callback: it is the loop-exit callback and the single activity-clock tick per iteration, and it is the only place where delivered pass frames are still flushed before `RetireLanesExcept` can drop lane'd frames (`NdisPacketActionExecutor.cs:252-282`).
14. **The arrival signals are created in `NdisCaptureGeneration.RegisterArrivalSignals`** (`:140,167`), and `MultiAdapterCaptureLoop` only receives and disposes the list — so the F8 wake registry must be composed at the generation site, not at the loop.
15. **`DispatcherAttributionMissStillClaimsWithNoProcess` does not exist**; the live fact is `DispatcherFallsBackWhenHostAttributionIsUnknown` (`FlowDispatcherExecutorTests.cs:127`).
16. **No ring inequality covering UDP is satisfiable**: `UdpProxyCoordinator.Send.cs:88` enqueues one item per admitted session against a 16,384 default session capacity (`ConfigurationModels.Udp.cs:63`). The stated relation is `ring >= tcpCap + flowCap` (2048), with UDP refusals accepted, counted (`RuntimeCounters.UdpSetupRejections`) and fail-closed.

## 10. Dead ends, recorded against re-exploration

- Serving a burst from one scan by letting young sockets miss (fail-open) — §8.1.
- A UDP owner-table snapshot without a creation-timestamp check — §5.
- Retaining `PacketLease` objects / materialized frames instead of copying — §7.1-7.2.
- `Task.Run`/`ThreadPool` per flow, or a dedicated attribution thread per adapter — §8.2.
- A per-iteration callback with no wake signal (100 ms first-packet latency) — §7.10.
- Draining through the pass lanes from a setup worker — §7.5.
- A shared decided list enumerated outside the gate — §7.6 and §8.2.
- Executing another adapter's frames — §8.2.
- `BoundedSetupQueue` as the retained-packet payload — §3.2.
- Re-deriving attribution inside `TryClaimResolved`'s `decide()` factory — §7.11.
- Relaxing the pump's exact-zero gates to "small" per-new-flow costs (`hot-path.md:440`).

## 11. Reproduce-before: commands, and the numbers they produce today

Tree `ba51205`, NixOS 26.11, .NET 10.0.12, `--quick` (15 s of load per run). Artifacts:
`.trellis/tasks/10-01-attribution-off-pump/research/reproduce/`.

```bash
D=.trellis/tasks/10-01-attribution-off-pump/research/reproduce
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario tcpChurn --quick --rate 60 --tcp-concurrency 8 --payload-bytes 4096 \
  --output $D/repro-baseline.jsonl
# same, plus: --attribution-delay-ms 50 --attribution-delay-percent 5  → repro-delay-5pct.jsonl
# same, plus: --attribution-delay-ms 50 --attribution-delay-percent 1  → repro-delay-1pct.jsonl
```

| Metric | archived baseline | repro baseline | archived 5 % | repro 5 % | archived 1 % | repro 1 % |
|---|---:|---:|---:|---:|---:|---:|
| attempts / successes / failures | 921 / 921 / 0 | 918 / 918 / 0 | 937 / 937 / 0 | 935 / 935 / 0 | 922 / 922 / 0 | 920 / 920 / 0 |
| stable count / mean / p50 (ms) | 921, 2.336, 1.204 | 918, 2.099, 1.084 | 891, 2.365, 1.224 | 889, 2.153, 1.110 | 913, 2.207, 1.132 | 911, 2.077, 1.061 |
| stable p95 / p99 (ms) | 3.445 / 8.308 | 3.489 / 7.458 | 2.261 / 10.068 | 1.955 / 7.428 | 2.676 / 7.254 | 3.184 / 7.465 |
| stable max (ms) | 100.656 | 90.737 | 104.663 | 102.433 | 95.804 | 88.134 |
| delayed count (share) | — | 0 | 46 (4.91 %) | 46 (4.92 %) | 9 (0.98 %) | 9 (0.98 %) |
| delayed mean / p50 (ms) | — | — | 51.749 / 51.708 | 51.745 / 51.803 | 52.301 / 52.300 | 51.747 / 51.527 |
| allocated per connection (B) | 90,968 | 91,088 | 90,999 | 91,066 | 90,938 | 91,031 |
| gen0 collections | 5 | 5 | 5 | 5 | 5 | 5 |

**Pooled** figures (nearest-rank, the artifact's own definition — `StabilityShared.cs:15-23`,
`LatencyDistribution.FromMilliseconds` `:63-75`), derived from the class counts:

- baseline: pooled p99 = 7.458 ms (the stable class *is* the pooled set).
- 5 %: pooled p99 = rank `ceil(0.99 × 935) = 926`, inside the delayed block (ranks 890–935) ⇒ **≈52.1 ms**; pooled mean ≈ `(889 × 2.153 + 46 × 51.745) / 935 = 4.59 ms`.
- 1 %: pooled p99 = rank `ceil(0.99 × 920) = 911` = the **largest stable sample** ⇒ **88.134 ms**, *not* ~52 ms; pooled mean ≈ 2.56 ms.

**Two measurement facts the plan depends on:**
1. At a **1 %** delayed share the pooled p99 falls back into the stable class and becomes a single host-scheduling outlier (~90 ms) — it would read as a *worse* regression than the stall it measures. The README's "pooled 1 % moves the p99 to ~52 ms" (`README.md:186-189`) does not reproduce under the artifact's own percentile rule. Any series must fix the delayed share at **5 %** (`SoakOptions.cs:181`) and record a **pooled percentile explicitly**, because per-class summaries cannot be re-pooled.
2. `allocatedBytesPerConnection` is stable to 0.06 % across trees and is a fine *must-not-move* row; it contains **no attribution work** and cannot serve as the attribution-allocation baseline (§9.4).

Confirmed by the runs: `serverConnectReplies == attempts` and `serverBytesEchoed == receivedBytes` in all three,
`failures: 0`, `droppedLatencySamples: 0`.

## 12. What this host cannot establish (recorded, never assumed)

- The real enumeration's cost, its row count, and the ~0.5–7 ms per-flow figure: `IPHelperTables` calls `iphlpapi.dll` (`ProcessAttribution.cs:319-323`) and is `[SupportedOSPlatform("windows")]`; on this host `FindAsync` returns at `:30`. Every off-pump fact here is **seam-level** (a fake attributor and a fake owner-table reader).
- `OpenProcess`/`QueryFullProcessImageName` behaviour and the `OpenProcess` failure rate under a protected process.
- Whether a real UDP local-port recycle inside a 250–500 ms window actually occurs on a busy desktop (§5, argued from the predicate, not measured).
- Whether `NdisApiDriver`'s send path tolerates a non-pump caller — relevant only to the rejected delivery alternatives (`src/WinForward.NdisApi/NdisApiDriver.cs:403-466` shows no internal serialization; no proof either way was established here).
- Whether the pump's arrival signal is available on a given NIC (F5's own open item, `windows-ndisapi.md:416`).
- The real cross-adapter duplicate-observation rate (§9.11) — the cost is bounded by the entry cap, but its frequency is a Windows-side measurement.

## 13. Anchors the review corrected, and the notes folded in

| Claim in the first draft | Corrected fact |
|---|---|
| "the dispatcher has no teardown, so the pipeline can be reached from `MultiAdapterCaptureLoop`" | the arrival signals are created in `NdisCaptureGeneration.RegisterArrivalSignals` (`:140,167`); the loop only receives/disposes them |
| "an escaping attribution exception fails the adapter" | it fails the **whole capture run** (`MultiAdapterCaptureLoop.RunPumpAsync:142-157`) |
| "the drain is keyed by the entry's adapter handle, so any pump can deliver it" | unreachable as well as unsafe: `FlowKey` equality includes `OriginAdapterSlot`/`OriginAdapterGeneration` (`Domain.cs:87-91,155-159`), so cross-adapter observations are distinct keys |
| "the delivery hook is the per-iteration callback" (unpinned) | it is `DurableCaptureBundle.FlushPendingInjections:397-405`, before `FlushPendingPasses`, and it is also the loop-exit flush (`NdisCapture.cs:255,323,341`) |
| "`BoundedSetupQueue` is the per-flow payload" | its `Entry` is `(NativeLease, int, DateTimeOffset)` (`:19`) and it grows a `Queue<Entry>` (`:59`) — F8 uses a fixed ring |
| "`forceRefresh` on the retry" | the request-instant rule; a flag makes the retry scan twice |
| "`DispatcherAttributionMissStillClaimsWithNoProcess`" | `DispatcherFallsBackWhenHostAttributionIsUnknown` (`FlowDispatcherExecutorTests.cs:127`) |
| "the ring relation is an asserted inequality" | the doc comment is `SetupExecutor.cs:117-123`; the assertion is the test `SetupExecutorTests.DefaultRingCapacityCoversThePendingSynIndexCap:165-174` |
| "`ring >= tcpCap + flowCap + udpHeadroom`" | unsatisfiable (UDP enqueues per admitted session against 16,384); the finite relation is `ring >= tcpCap + flowCap`, UDP refusals accepted and counted |
| "`SelfTrafficRegistry` gate once per claim" (as a property F8 keeps) | it becomes once per packet for a pending flow — a bounded, recorded delta |
| review's verified-correct notes | the exact UDP/TCP predicates and the `OWNER_MODULE` prohibition; complete-then-execute + `Deferred` unread by `src/**` + `Release()` on the pump thread; the one-clock-tick-per-iteration contract; `SetupExecutor.TryEnqueue`'s unsettled completion; one refusal counter per class |

---

## 14. Landed truth (folded back in, 2026-10-01)

The plan's anchors held except where §13 already corrected them. What the landed tree actually does,
for the archive:

- **The attribution thread counts are exact and the movement is large.** `attributionsOnPumpThread`
  64 → **0**, `attributionsOnSetupWorker` 0 → **64**, `pumpBlockedMs` p95 7.52–7.56 ms → **0.019–0.028 ms**
  (~380×), `admittedEntries` 64, `retainedPackets` 256, `deliveredPackets` 256. Artifact:
  `benchmarks/results/2026-10-01-attribution-off-pump/`.
- **The coalescer's series is one scan per burst.** 16 concurrent flows over one scripted table cost
  **1** read (`scansPerFlow = 0.0625`). §5's structural correction is what makes this the mechanism:
  the TTL never serves a burst's own sockets.
- **`tcpChurn` did not move** (`allocatedBytesPerConnection` 91,046 / 91,069 / 91,057 vs the reproduce
  91,088 / 91,066 / 91,031), and §11's two measurement facts were confirmed again: the 5 % arm is the
  only arm whose pooled p99 leaves the stable class (≈52.8 ms, rank 925 of 934), and the 1 % arm's
  pooled p99 is the largest stable sample (90.036 ms) — an outlier, not the stall.
- **§3.3's per-flow payload is a fixed 32-slot ring**, not `BoundedSetupQueue`; the steady append is
  exactly 0 B and the cold budget is one entry + its ring + the first slot (the gate's window).
- **The platform guard moved only as far as it could**: it now guards `ReadProcessIdentity` alone, so
  the owner-table cache is reachable through `FindAsync` on this host (the `attribution.ownerBurst`
  row depends on that). Off-Windows the result is still `null`.
- **Two facts the plan named are gaps, not landed work**: the parked-gate lock-order fact
  (`ThePipelineNeverTouchesTheFlowGateWhileHoldingItsOwn`) and the `CompositePacketArrivalSignal`
  facts + its idle-wait allocation gate. The composite and the registry are landed and wired; their
  facts are not written. `implement.md` §"Recorded gaps" carries the full list.
- **One measurement lesson worth keeping**: `_ = new byte[64]` and even `new byte[64].Length == 64`
  do **not** discriminate an allocation gate — RyuJIT removes both. Only an allocation that escapes
  (a `NoInlining` method returning the array) failed the gate, at exactly 7 × 88 = 616 B. Any future
  discrimination check must use the escaping form.
