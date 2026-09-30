# Design — F8: process attribution off the pump thread (pending-flow pipeline + owner-table epoch coalescer)

> Companion to `research/implementation-notes.md` (anchors, hazards, discrepancies D1–D16, reproduce-before
> numbers). Base tree `ba51205`, re-verified against the **amended** `prd.md` (AC-1 "per admitted pending
> entry", AC-4 "one shared scan per coalesced epoch + one forced rescan"). Where this design accepts a
> semantic delta it is listed in §5.4 and tested.

---

## 1. Chosen mechanism per site

| Site | Mechanism | Why this one |
|---|---|---|
| Attribute work | A **bounded per-flow pending index** + one **setup-worker item** per admitted entry that runs attribution **and** policy evaluation off the pump | The two proven templates (`TcpPendingSynSetupIndex`, `UdpSetupQueueBudget` + `UdpSessionSetup`) already provide this shape |
| Admission scope | Only the shape `AttributeProcessAsync` would actually attribute: `RequiresProcessAttribution && _attributor is not null && Origin == Host && context.Process is null` | Anything else keeps today's inline path byte-for-byte (§2.1). Forwarded eligibility is fixed by `traffic-policy-lifecycle.md:9` immediately before the claim; deferring forwarded flows would pay a worker round-trip for nothing |
| Retained packets | A **fixed-capacity ring owned by the entry** (`RetainedPacket[]`, 32 slots, allocated once with the entry) | `BoundedSetupQueue` cannot carry `Metadata`/`PacketSequence`/`Layout` and grows a `Queue<Entry>` on the second packet (`BoundedSetupQueue.cs:19,59`); a `List` would grow per packet (§2.1) |
| Delivery | **The pump thread**, from inside the existing per-iteration callback: decided entries are taken one at a time from a **per-adapter queue**, executed in order, and the flow is **claimed in the final critical section**. Failure arms never claim | Keeps F1's batched-lane contract (`NdisPacketActionExecutor.cs:17-20,99-142,190-224`) and F2's lock-free warm path; ordering and exactly-once are structural (§2.3) |
| Owner-table scan | An **immutable snapshot per table kind** + a **single-flight refresh keyed by the request instant**; TCP serves from cache, UDP never does | The burst property comes from epoch coalescing, not the TTL (§3.1); a UDP cached row is fail-open (§3.3) |
| Seam | `internal IProcessOwnerTableReader` + an internal ctor parameter on `WindowsProcessAttributor`; the platform guard moves into the default reader | No provider seam exists today (`ProcessAttribution.cs:172,317-324`), and the non-Windows short-circuit at `:30` makes every cache fact unreachable here |
| Wake (latency only) | A pipeline-owned auto-reset event composed with F5's borrowed driver signal, created where the driver signals are created (`NdisCaptureGeneration.RegisterArrivalSignals:140,167`) | The pump parks up to 100 ms (`NdisCapture.cs:128,353-357`). The wake is never a correctness requirement (§4.3) |

## 2. The off-pump pipeline

### 2.1 Admission (pump thread, eligible flow-table miss only)

Two new types, mirroring the TCP split:

- `src/WinForward.Runtime/FlowAttributionPendingIndex.cs` — the bounded index: leaf `Lock _gate`,
  `Dictionary<FlowKey, PendingFlowAttribution>`, **one decided queue per adapter handle**, global Interlocked
  byte budget, entry cap, per-flow ring cap, TTL, per-flow failure/claim-failure state, and every counter.
- `src/WinForward.Runtime/FlowAttributionPipeline.cs` — `Admit`, `DeliverDecided(adapterHandle)`, the worker
  body, diagnostics, `IAsyncDisposable`.

`PendingFlowAttribution` (one per admitted pending entry):

| Field | Purpose |
|---|---|
| `FlowContext Context` | claim-time context (the attributed `Process` is written by the worker here) |
| `FlowDecision? Decision` | the worker's verdict, written under the gate |
| `DecisionState State` (`Pending`, `Decided`, `Failed`) + `Exception? Failure` | the state machine the delivery branches on |
| `bool ClaimFailed` | set by a false `TryClaimResolved`; the entry is kept and fail-closed (§2.3) |
| `RetainedPacket[32] _ring`, `int _head/_count`, `int ChargedBytes` | fixed ring; `RetainedPacket` is a struct of `NativeLease Frame`, `int Length`, `PacketCaptureMetadata Metadata`, `long PacketSequence`, `PacketLayout Layout`, `DateTimeOffset EnqueuedAt` — no per-packet object |
| `DateTimeOffset LastWriteUtc` | TTL basis, refreshed on append |
| `nint AdapterHandle` | the decided-queue key (from `Metadata.AdapterHandle`) |

Admission, replacing `AttributeProcessAsync` + `TryClaimResolved` **only** on the eligible shape:

```text
DispatchSlowAsync: flow-table miss (:217)
  if (!ShouldAttribute(context))            // the SAME predicate AttributeProcessAsync uses (:284)
      → today's inline path unchanged       // no entry, no worker, no deferral
  outcome = pending.Admit(packet)            // pump thread
  if (outcome == Blocked)                    // cap / budget / sealed / oversized / ring full / enqueue refused
      → Block + count (never inline attribution, never pass)
  else  → return Deferred
```

`ShouldAttribute` is extracted from `FlowDispatcher.AttributeProcessAsync` and called by **both** paths, so
the four-way gate (`RequiresProcessAttribution`, null attributor, already-attributed, non-Host origin) cannot
drift. `Admit`:

```text
lock (_gate):
    if (_sealed)                                  → refuse (Block, counted)      // never fall back inline
    if (entry exists for key):
        if (entry.State == Pending)               → AppendToRing(packet); return Deferred
        if (entry.State == Failed)                → return Blocked (its packets are already failing closed)
        if (entry.ClaimFailed)                    → Append + re-enqueue the entry (retry once per arrival)
        if (entry.State == Decided)               → deliver inline: this pump thread owns the delivery
    else:
        if (_flows.TryResolve(key, out existing)) → unlock; continue today's claim/execute path
        created = TryCreateEntry(packet)          // entry cap + budget charged here
        if (created is null)                      → Block + count
unlock
launch the setup item (LaunchAttribution)
```

`AppendToRing` copies from **`packet.InspectionSpan`** (`FlowDispatcher.cs:51`, native-first — never
`packet.Lease.Frame`, which would materialize a pooled managed array per packet, `PacketRuntime.cs:69-85`)
into a lease rented from the pipeline's own pool. Three guards:

- **Size**: `span.Length > lease.Length` (the pool's fixed `BufferSize`, `NativeBufferPool.cs:44,174`) is
  refused fail-closed **before** `CopyTo` — a `CopyTo` throw would strand the half-written entry.
- **Complete synchronously**: the admission completes the lease with the additive `PacketDisposition.Deferred`
  and `CapturePacketProcessor.ProcessAsync`'s `finally` releases it on the pump thread
  (`PacketRuntime.cs:93-135` requires exactly that thread). `Deferred` is unread by `src/**`; only tests
  assert `Disposition` (implementation-notes §7.10), so it is observable without a behaviour change.
- **Pool sizing**: the pipeline's `NativeBufferPool` is sized to the maximum capture frame, with capacity
  ≈ budget / bufferSize (8 MiB / 1514 ≈ 5,500 slots is the budget's own bound; the entry cap binds first in
  the single-packet common case). The bundle creates it; the pipeline drains it and the bundle disposes it.

**Per-adapter ownership invariant (new, load-bearing).** `FlowKey` equality includes `OriginAdapterSlot` +
`OriginAdapterGeneration` (`Domain.cs:87-91,155-159`), so a transport tuple observed on a second adapter is a
**different key**: it admits its own entry, runs its own attribution, and is delivered by **its own adapter's**
pump only. The decided queues are per adapter handle and `DeliverDecided` never touches another adapter's
entries. Two consequences, both stated: (a) `TryClaimResolved` stays the once-per-logical-flow arbiter
(`FlowTable.cs:236-241`), so the second entry's claim returns the existing decision and the *observable*
policy outcome is unchanged; (b) the second attribution + policy evaluation is a real extra cost, recorded as
an accepted delta (§5.4) and counted (`AttributionReAdmission`) — which the amended AC-1 requires.

### 2.2 The worker (setup worker thread)

One `SetupWorkItem` per admitted entry on the shared `SetupExecutor` (the only WF0003-sanctioned door), with a
third pre-allocated payload plane beside `_tcp`/`_udp` (`SetupExecutor.cs:31-35,54-83`) carrying the entry and
the key.

```text
RunAttributionAsync(item):
    if (!_scope.TryEnter(out lease)) { entry.Fail(Shutdown); return; }     // sealed: no new work
    try:
        var context  = await AttributeProcessAsync(entry.Context, token);  // the SHARED method
        var decision = Evaluate(context);                                  // the SAME rule call
        lock (_gate): entry.Context = context; entry.Decision = decision; entry.State = Decided;
                      _decided[entry.AdapterHandle].Enqueue(entry); _decidedCount++;
        wake(entry.AdapterHandle);
    catch (OperationCanceledException) when (token.IsCancellationRequested):
        lock (_gate): entry.State = Failed; entry.Failure = Shutdown;      // NO cooldown, NO failure counter
                      _decided[entry.AdapterHandle].Enqueue(entry); _decidedCount++;
    catch (Exception e):
        lock (_gate): entry.State = Failed; entry.Failure = e;
                      _decided[entry.AdapterHandle].Enqueue(entry); _decidedCount++;
    finally: lease.Dispose();
```

- Classification follows `error-handling.md` ("shutdown cancellation unwinds queued waiters without a
  tombstone"; only a genuine failure arms one) and mirrors `TcpProxyCoordinator.TryEnterSetup`'s false path
  (`:242-247`).
- `AttributeProcessAsync` is **reused verbatim**, so the eligibility predicate, the miss logging and the
  `RuntimeCounters.AttributionMiss` increment are shared, not copied: requirement 6 is discharged by sharing
  the code.
- `Evaluate(context)` is `_policy.Evaluate` / `_policy.EvaluateForwarded`, the same call `EvaluateNewFlow`
  makes (`:374-375`), on an immutable `PolicySnapshot`.
- The **claim is deliberately not here** (amended PRD req 1 fixes it at the delivery point; the reasoning is
  §2.3).

### 2.3 Delivery, ordering and exactly-once (the load-bearing argument)

**Hook.** `DeliverDecided(adapterHandle)` runs inside the bundle's existing
`FlushPendingInjections(adapterHandle)` (`DurableCaptureBundle.cs:397-405`), which the pump invokes once per
iteration **and once at loop exit** (`NdisCapture.cs:255,323,341`) — so the pipeline keeps the single
activity-clock tick per iteration (`DurableCaptureBundleTests.ActivityBucketClockTicksExactlyOncePerPumpIteration`)
and the delivered frames reach the batched lanes **before** they are flushed:

```text
FlushPendingInjections(adapterHandle):
    ActivityClock.Tick();
    Pipeline.DeliverDecided(adapterHandle);          // NEW — may append pass frames to the lanes
    Executor.FlushPendingPasses(adapterHandle);      // they go out in this same iteration's batch
    Tcp.FlushPendingRedirectInjections(adapterHandle);
```

A delivery driven by anything *later* than this callback would append pass frames after the flush and strand
them until the next iteration — and at loop exit `RetireLanesExcept` drops still-lane'd frames with a warn
(`NdisPacketActionExecutor.cs:252-282`). Pinning the hook inside the existing callback is what keeps delivered
frames out of that path.

**Drain loop (pump thread, one adapter at a time).**

```text
DeliverDecided(adapterHandle):
    if (Volatile.Read(ref _decidedCount) == 0) return;              // 0 B fast path, no lock
    while (true):
        PendingFlowAttribution? entry;
        lock (_gate):                                                // O(1) dequeue — never an enumeration
            if (!_decided[adapterHandle].TryDequeue(out entry)) break;
            _decidedCount--;
        DeliverEntry(entry);                                         // outside the gate
```

The decided set is a **queue taken under the gate** (`TryDequeue`), not a list enumerated outside it: a
worker's `Enqueue` during a drain cannot invalidate an iterator — the previous shape's
`InvalidOperationException` would propagate out of `RunLoop` and
`MultiAdapterCaptureLoop.RunPumpAsync` cancels the **whole capture run** on any pump throw
(`MultiAdapterCaptureLoop.cs:142-157`). The count is decremented in the same critical section as the dequeue,
so the fast path returns to zero.

```text
DeliverEntry(entry):
  Phase A — failure arm: never claim, never execute
    if (entry.State == Failed):
        batch = TakeAll(entry)                 // under _gate; removes the entry; credits bytes
        if (!Shutdown) { count attributionSetupFailed; arm the 1 s cooldown }
        BlockEach(batch); return               // counted per packet; leases disposed in finally

  Phase B — execute the decided packets in order
    while (true):
        batch = TakeAll(entry)                 // under _gate; the entry STAYS in the map
        if (batch.Count == 0 && entry.State == Decided) break
        if (batch.Count == 0) return           // a decided-queue entry is always Decided or Failed
        try { foreach p in batch: Execute(p, entry.Decision) }   // pump thread, arrival order
        catch { BlockEach(remainder); throw }  // per-batch finally: nothing detached is ever stranded
  Phase C — claim last, in one critical section (nothing left to append to)
    lock (_gate):
        if (_flows.TryClaimResolved(entry.Key, entry.Decision, out _)):
            Remove(entry); CreditBytes; DisposeRing
        else:                                   // flow-table capacity only (FlowTable.cs:248-252)
            entry.ClaimFailed = true            // keep the entry; fail-closed
            RuntimeCounters.FlowCapacityBlock++; surface flow.capacity-block (the dispatcher's site)
    unlock
```

- **Ordering.** The entry stays in the map for the whole delivery, so a concurrent admission appends to it
  (picked up by the next `TakeAll`, after the packets already being executed) or, arriving after Phase C's
  removal, finds the flow claimed and dispatches warm (after every pending packet). No packet can bypass the
  ring and be followed by an older one — the UDP no-bypass argument (`UdpSessionSetup.cs:183-195`) with the
  readiness flip replaced by the claim.
- **Exactly-once.** `TakeAll` removes each packet from the ring under the gate, so a packet executes at most
  once; the loop re-reads until it observes an empty ring, so a packet appended mid-delivery executes at least
  once. A throwing executor call Blocks the unexecuted remainder in a `finally` and counts it — the batch can
  never be stranded between detach and execution.
- **A false claim is not a lost flow.** `TryClaimResolved` returns false only at flow-table capacity
  (`FlowTable.cs:242-265`); the entry is **kept** and marked `ClaimFailed`. Its already-executed packets are
  unaffected (they were dispatched under the entry's decision — the same decision a claim would have stored),
  and every later packet is appended and Blocked fail-closed at the next drain. A packet arriving for a
  `ClaimFailed` entry re-enqueues it so the pump retries the claim once per arrival; a successful retry
  removes the entry and the flow goes warm. A re-admission can still happen if the entry is TTL-reclaimed
  while the table is full — counted as `AttributionReAdmission` per amended AC-1.
- **Cross-adapter.** Not a delivery path: an entry is admitted, delivered and drained **only** on its own
  adapter's pump (§2.1). Executing adapter-A frames from pump B would race A's lock-free lane against A's own
  flush on the shared executor (`NdisPacketActionExecutor.cs:99-142,190-224`) — and the previous draft's
  "drain keyed by the entry's adapter handle" was unreachable anyway, because the keys differ per adapter.
- **Lock order.** `pending gate → flow table gate`, used by admission's re-check and by Phase C. Nothing takes
  `flow gate → pending gate`: `TryClaimResolved`'s `decide()` factory runs under the flow gate
  (`FlowTable.cs:254`), so `decide()` must never touch the pipeline.
- **Executor thread-affinity preserved.** Every `Execute` call is on the pump thread that owns
  `adapterHandle`; drained packets carry **no `NativeFrame`** (the pump slot is recycled), so `PassAsync`
  takes its rented-copy branch (`:91-93`) — the same shape the TCP R8 rebuild uses
  (`TcpProxyCoordinator.cs:273`).

### 2.4 Refusals, failures and teardown (every one counted; counters located)

| Condition | Result | Counter |
|---|---|---|
| Entry cap (1024) or global byte budget (8 MiB) refuses the **triggering** packet | No entry; packet Blocked; a retransmission retries | `RuntimeCounters.AttributionPendingRejected` (heartbeat-visible) + a rate-limited `flow.attribution.pending-rejected` warn |
| Per-flow ring full (32) or a frame larger than the pool buffer | That packet Blocked; the ring and the entry untouched | `RuntimeCounters.AttributionFlowFull` + a rate-limited trace |
| Pipeline sealed (`Admit` after dispose) | **Block fail-closed** — never inline attribution on the pump | `RuntimeCounters.AttributionSealed` |
| Eligibility miss (no process rules / non-Host origin) | **Not a refusal**: today's inline path, unchanged | none (proved by a negative fact) |
| `SetupExecutor.TryEnqueue` refuses | `TryEnqueue` recycles the item without settling `_completion` (`SetupExecutor.cs:183-197,220-224`), so the pipeline settles it itself, removes the entry and Blocks its packets — `LaunchSetup`'s posture (`TcpProxyCoordinator.cs:216-222`) | `RuntimeCounters.AttributionSetupRejected` |
| Worker: genuine failure | Entry `Failed` → Phase A Blocks its packets + the 1 s per-flow cooldown | `RuntimeCounters.AttributionSetupFailed` |
| Worker: `OperationCanceledException` (scope token / shutdown) | Entry `Failed(Shutdown)` → Phase A Blocks its packets, **no cooldown, not counted as a failure** (`error-handling.md`) | none |
| False claim (flow table at capacity) | The entry is kept, `ClaimFailed`, its later packets Blocked; `flow.capacity-block` surfaced at the dispatcher's existing site | `RuntimeCounters.FlowCapacityBlock` (existing) + `AttributionClaimFailed` |
| TTL (5 s) reclaims an entry | Packets Blocked; a later packet of the same flow re-admits | `AttributionPendingTtlExpired` + `AttributionReAdmission` |
| Module disposal | Seal → refuse → Block every pending entry → drain in-flight worker leases → release the pool | `AttributionSealed` |
| Second key for one transport tuple (cross-adapter) | A second admitted entry; its claim returns the existing decision | `AttributionReAdmission` |

Counter placement: the cross-cutting ones are `RuntimeCounters` shared keys (heartbeat-visible, beside
`attributionMiss`/`flowCapacityBlock`), the internal ones live on the pipeline's diagnostics snapshot
(`PendingAttributionCount`, `PendingAttributionBytes`, `DecidedDepth`, `DeliveredPacketCount`,
`AttributionsOnPumpThread` (must stay 0), `AttributionsOnSetupWorker`) — one snapshot per module, the
`TcpRedirectDiagnostics` convention. Each refusal class has **one** counter and one increment site (the TCP
index's double-count on a budget refusal, implementation-notes D7, is deliberately not copied).

**A refusal never becomes a pass.** "Pass it and let policy sort it out" and "evaluate with no process match
and claim" are both fail-open — a process rule that would have matched silently stops applying. Every path
above is fail-closed and observable.

### 2.5 Bounds

| Bound | Value | Basis |
|---|---|---|
| Pending entries | 1024 | `TcpPendingSynSetupIndex.DefaultCapacity` |
| Global retained bytes | 8 MiB | `UdpSetupQueueBudget.SetupQueueGlobalByteBudget` |
| Per-flow ring | 32 packets (bytes = 32 × the pool's frame cap) | the UDP setup queue's per-flow shape |
| Entry TTL | 5 s | the TCP pending index; a backstop, swept on its own leg (§4.2) |
| Failure / claim-failure cooldown | 1 s | the TCP pending index |
| Per-packet cost | one `memcpy` of ≤1514 B under a pool lease | replaces a 0.5–7 ms stall |
| Worst-case retained memory | `min(1024 × 32 × frame, 8 MiB)`; the typical single-packet flow is `1024 × ~1.5 KiB ≈ 1.5 MiB` | the entry cap binds first |

**Ring arithmetic (finite, and stated as such).** `UdpProxyCoordinator.Send.cs:88` enqueues **one setup item
per admitted session** against a default session capacity of 16,384
(`ConfigurationLoader.DefaultUdpSessionCapacity`, `ConfigurationModels.Udp.cs:63`), so no inequality of the
form `ring >= tcpCap + flowCap + udpHeadroom` is satisfiable. The finite relation is:

```text
SetupExecutor.DefaultRingCapacity (2048) >= TcpPendingSynSetupIndex.DefaultCapacity (1024)
                                          + FlowAttributionPendingIndex.DefaultCapacity (1024)
```

and a full ring can therefore refuse a UDP setup item; that refusal is already counted
(`RuntimeCounters.UdpSetupRejections`) and fail-closed, and is **explicitly accepted**. The relation's
load-bearing form is the test `SetupExecutorTests.DefaultRingCapacityCoversThePendingSynIndexCap`
(`:165-174`); `SetupExecutor.cs:117-123` is only the doc comment.

## 3. The owner-table epoch coalescer

### 3.1 The mechanism, and the correction it rests on

A TTL snapshot alone **cannot** serve a burst's own new flows: an owner-table row appears at socket bind,
microseconds before the packet that triggers the lookup, so it is never in a snapshot taken 250 ms earlier.
Against the predicates — `FindTcpOwner` needs all four tuple fields (`:194`, `OWNER_PID_ALL`); `FindUdpOwner`
matches the local port plus a wildcard-or-exact address and requires exactly one distinct PID (`:187`) — the
burst property therefore comes from **epoch coalescing**: concurrent missers join one in-flight scan whose
snapshot is published after the last of them asked, so each requester's socket already exists when that scan
runs. The TTL's only role is serving requests that arrive after a scan, which is the part carrying staleness
risk (§3.3). Per amended AC-4 the scan count is a **recorded series, not a threshold**.

### 3.2 Lookup, epochs, and the unchanged worst case

`ProcessOwnerTableCache` (`src/WinForward.Windows/ProcessOwnerTableCache.cs`), one slot + one refresh gate per
table kind (Tcp4, Tcp6, Udp4, Udp6):

```text
Lookup(kind, key, requestInstant):                  // requestInstant = the moment THIS caller asked
    snapshot = Volatile.Read(slot)
    if (snapshot is fresh && snapshot.TakenUtc <= requestInstant)   // TTL path: a row older than the socket cannot answer
        return snapshot.Lookup(key)
    lock (refreshGate[kind]):
        snapshot = Volatile.Read(slot)
        if (snapshot.TakenUtc >= requestInstant) return snapshot.Lookup(key)   // joined an epoch started after us
        rows = reader.Read(kind)                     // the ONE scan for this epoch
        publish(new(rows, now))
    return snapshot.Lookup(key)

FindAsync:  attempt 1 → Lookup(kind, key, now)
            on null   → Task.Delay(2 ms) → attempt 2 → Lookup(kind, key, now)
```

The freshness rule is **the request instant, not a flag**: the TTL path refuses a snapshot older than the
caller's own request (a row that predates the socket cannot answer it — amended AC-4's "one forced rescan for
a flow whose socket bound after that read"), and the in-lock recheck accepts any snapshot published **at/after**
the request instant, so a retry arriving after another refresh published joins that epoch instead of scanning
again. Two concurrent scans for one kind are impossible, and a joiner that misses the just-published snapshot
does not force a scan — the snapshot it is looking at was taken after it asked, which is the answer today's
scan would give. The second attempt is therefore no longer a special "force refresh" mode: it is a lookup with
a later request instant, which is what keeps today's worst case (one scan of latency, then a genuine
post-delay scan) while making both paths coalesce.

The lookup reproduces `FindUdpOwner`/`FindTcpOwner` exactly, including the wildcard branch and the
exactly-one-distinct-PID rule, so attribution results are identical for identical tables. `OWNER_MODULE` is
not used (PRD out-of-scope: the Windows API surface).

### 3.3 Staleness: what is accepted, what is not

| Direction | Consequence | Disposition |
|---|---|---|
| A socket younger than the snapshot | The request instant rejects the snapshot ⇒ a real scan answers it. No "unattributed" outcome, no fail-open | Safe by construction |
| A cached row is obsolete (port recycled inside the window) | The flow would be attributed to the **previous** process | TCP: an exact-4-tuple reuse inside 300 ms is effectively impossible under TIME_WAIT ⇒ accepted. **UDP: the predicate matches a local port alone ⇒ positive caching off**; UDP lookups coalesce but never answer from cache |

The staleness bound is `window` for TCP kinds and `0` for UDP kinds, both asserted by test.

### 3.4 The seam

`internal interface IProcessOwnerTableReader { OwnerTable Read(OwnerTableKind kind); }` with
`IPHelperOwnerTableReader` (today's `ReadTable` + `ReadTcp4/6`/`ReadUdp4/6`, `ProcessAttribution.cs:198-287`,
moved verbatim including the size probe, `ValidateRowCount` and the `finally { FreeHGlobal }`) as the single
`[SupportedOSPlatform("windows")]` boundary, reporting "table unavailable" off-Windows.
`WindowsProcessAttributor` gains an `internal` ctor parameter for the reader/cache; `FindAsync` keeps returning
`null` here because the default reader reports unavailable, so the observable contract is unchanged and the
managed cache logic is exercisable on this host (`IPHelperTablesBoundsTests` already runs here).

### 3.5 Memory

One parsed row array per kind, bounded by the OS table size and replaced per epoch; the
`Where/Select/Distinct/ToArray` of `:187,194` becomes one predicate pass (removing the per-lookup closure
allocation the research §A2 note complains about). Refresh allocation is cold, on a setup worker, and replaces
the identical allocation today's scan makes. No growth path, no per-flow state.

## 4. Thread and ownership model

### 4.1 Threads

| Thread | Work |
|---|---|
| Capture pump (one per adapter) | warm path; on an eligible miss, admission (eligibility check + one ring slot + one entry) and **delivery** of its own adapter's decided entries. Never scans, never opens a process, never sleeps |
| `wf-setup-*` (shared `SetupExecutor`, `max(2×CPU,16)`) | attribution, policy evaluation, enqueue decided/failed. Blocks its own dedicated thread for the pipeline's duration (`SetupExecutor.cs:278-304`) |
| Composition/loop threads | create/dispose the wake signal |
| `IdleExpirySweeper` (new leg) | TTL reclaim + cooldown prune (§4.2) |

### 4.2 Ownership, lifetime, sweeper leg

- `FlowAttributionPipeline` is created by `DurableCaptureBundle` with its own bundle-created
  `NativeBufferPool` (**not** `synCopyPool`/`udpDatagramPool`/`relayPool` — those are drained on other
  schedules, `DurableCaptureBundle.cs:440-456`).
- It owns a `QuiescenceScope` (the `TcpRedirectSessionStore` root-scope shape): admission and every worker item
  take a lease; `DisposeAsync` seals, cancels (an in-flight `Task.Delay(2 ms)` retry unwinds through the
  token, `ProcessAttribution.cs:34`), fail-closes every pending entry, joins the in-flight leases and releases
  the retained leases.
- **Bundle order:** dispose the pipeline immediately after `_sweeper` (`DisposeCoreAsync:420`), so no new
  admissions and every pending entry settles while the pumps have already stopped and the pools are alive;
  dispose the retention pool in the last block beside `_synCopyPool` (`:455-456`). The shared `SetupExecutor`
  stays last — the pipeline's **seal**, not the executor's join, stops new work.
- **TTL sweep:** a new `IdleExpirySweeper` leg calling `pipeline.RemoveExpired(now)` each tick (a third
  collaborator beside the dispatcher and the two coordinators), allocation-free on the tick in the
  `SweepAllocationGateTests` shape (`hot-path.md:1024-1033`): one reused scratch list, each candidate
  re-checked under the pipeline gate, disposal outside it. The TTL is a backstop for a stopped pump or a stuck
  worker — the **delivery path**, not the sweep, normally ends an entry's life, and the 60 s main-leg cadence
  means the sweep alone could not bound it (implementation-notes D6).
- The dispatcher gains no `IDisposable`; it holds an optional pipeline reference and the bundle owns the life.

### 4.3 The wake signal (latency, never correctness)

- `NdisPacketArrivalSignal` is unchanged (`NdisPacketArrivalSignal.cs:9-16`); a new
  `CompositePacketArrivalSignal` wraps the **borrowed** driver signal plus **one owned** auto-reset event and
  waits on both with `WaitHandle.WaitAny` under the same timeout.
- **Construction site (corrected):** the signals are created in
  `NdisCaptureGeneration.RegisterArrivalSignals` (`:140,167`) and only *received* by
  `MultiAdapterCaptureLoop`, which owns their disposal. The pipeline-owned event therefore has to be created at
  the generation site too, which needs a new collaborator on that builder (a `FlowAttributionWakeRegistry` the
  generation composes, or the pipeline exposing a signal factory); the registry owns the event's lifetime and
  the loop keeps disposing the composite list after the pumps stop.
- The pipeline signals the registry by `entry.AdapterHandle`. **If no signal can be supplied** (a refused
  driver registration — the existing fail-open-to-sleep path), the composite has no internal event and
  delivery latency is bounded by the poll delay (1 ms) or the idle timeout (100 ms). No acceptance fact
  asserts the wake.
- The composite's wait is a **new production idle-wait entry point**: it must be added to `hot-path.md`'s
  exact-window class list (`:1034-1041`) and covered by an allocation gate in the
  `NdisCapturePumpIdleWaitTests.IdleWaitIterationsAllocateNoManagedBytes` shape (unsignaled event, zero
  timeout), or the existing gate must be re-pointed at it explicitly.

## 5. Contracts

### 5.1 Unchanged public surface

`IProcessAttributor` / `ProcessIdentity`; `IPacketActionExecutor`; `ISelfTrafficGuard`; `FlowDispatcher`'s
constructor and members; `PacketLease`'s construction/completion semantics; `INdisPacketArrivalSignal`;
`ISetupExecutor`.

### 5.2 Additive

`PacketDisposition.Deferred` (a new enum member, consumed exactly once on the pump thread, unread by `src/**`);
`FlowTable.TryClaimResolved(FlowKey, FlowDecision, out FlowState?)` (allocation-free — the factory overload
would need a per-claim closure on the pump thread); `WindowsProcessAttributor`'s internal reader/cache ctor
parameter; `SetupExecutor.DefaultRingCapacity` 1024 → 2048 with the finite relation of §2.5;
`IdleExpirySweeper`'s pipeline leg.

### 5.3 New internal members

`FlowAttributionPipeline`, `FlowAttributionPendingIndex`, `PendingFlowAttribution`, `RetainedPacket` (Runtime
root — the scheduling vocabulary the dispatcher and the composition both reference);
`ProcessOwnerTableCache`, `IProcessOwnerTableReader`, `IPHelperOwnerTableReader`, `OwnerTable(Kind)`
(Windows); `CompositePacketArrivalSignal` + the wake registry (NdisApi/Capture); the diagnostics snapshot
(§2.4).

### 5.4 Accepted semantic deltas (each tested)

1. **An unexpected attribution exception now fails one flow, not the capture run.** Today an exception that
   escapes `FindOwnerSafely`'s four catches propagates out of `AttributeProcessAsync` →
   `CapturePacketProcessor.ProcessAsync` rethrows (`:96-106`) → `NdisCapturePump.RunLoop` exits
   (`NdisCapture.cs:245-248`) → `MultiAdapterCaptureLoop.RunPumpAsync` catches and **cancels the whole capture
   run** (`:142-157`), so every adapter stops. Under F8 it fails that flow closed.
2. **A flow's first packets are delayed by the attribution time** (plus one callback for the drain), not the
   whole adapter.
3. **`Policy.Evaluate` moves out of the flow-table gate** (worker, before the claim; same inputs, same rule
   order, no side effects) — retiring the research §A2 note as a side effect.
4. **A refused packet of a pending flow is Blocked before it is ever evaluated** — the only new drop class.
5. **A pending flow's packets re-enter the full slow path**, including the **gated** exact-tuple self-traffic
   check (`SelfTrafficRegistry.IsOwned`, `:16,29`) and the reverse-handler probe, once per packet instead of
   once per claim (`hot-path.md:1398-1401`). Bounded by the ring (32 packets) and the TTL (5 s), counted by
   the pipeline's delivered-packet counter, and asserted by a bounded fact. An admission-side short-circuit is
   deliberately not taken in v1 (it would need a second "proven not self" record before the claim exists).
6. **A cross-adapter duplicate observation of one transport tuple** admits a second entry and pays a second
   attribution + policy evaluation; the claim returns the existing decision, so the observable outcome is
   unchanged. Counted (`AttributionReAdmission`) per amended AC-1.
7. **The 2 ms retry keeps its second scan** but coalesces with any epoch that started after its request
   instant.

## 6. Semantics and risk table

| # | Risk | What happens | Mitigation / disposition |
|---|---|---|---|
| 1 | **A slow attribution delays the flow's first packets** | The flow reaches the executor only after the verdict + drain: a 7 ms attribution delays the flow ~7 ms, a retry 2 ms more, a stuck worker up to the TTL | Accepted — the delay moves from every flow on the adapter to the one flow paying for its own attribution. The ring bounds retained volume, the TTL the worst case, refusal is fail-closed |
| 2 | A packet is dropped under a burst | Cap / budget / ring / oversized / sealed | Counted per class, fail-closed, prefix-preserving (the ring keeps the oldest packets) |
| 3 | The pump is blocked by admission | One eligibility check + one ring slot + one `memcpy` + one enqueue | No scan, no process open, no `Task.Delay`, no policy evaluation. The exact fact is "zero attributions on the pump thread", not "zero work" |
| 4 | A detached batch is stranded | An executor throw between detach and execution | Per-batch `finally` Blocks the unexecuted remainder and counts it; `TakeAll` removes each packet exactly once |
| 5 | Mutation during enumeration aborts the whole capture run | A worker enqueue while the pump iterates a decided set | The decided set is a **queue dequeued under the gate**; no enumeration exists; a fact drives an enqueue during a drain |
| 6 | A false claim re-attributes per packet | Flow table at capacity | The entry is kept (`ClaimFailed`), its packets Blocked, the claim retried once per arrival; `flow.capacity-block` surfaced |
| 7 | A failed/shutdown worker claims or counts wrongly | Failure arm | Phase A never claims; `OperationCanceledException` → no cooldown, no failure counter; genuine failure → cooldown + counter (`error-handling.md`) |
| 8 | Lease/frame lifetime | A retained frame must outlive the pump slot; the lease must be released on the pump thread | Copy from `InspectionSpan`; complete synchronously with `Deferred`; release on the pump thread; drained packets carry no `NativeFrame` |
| 9 | Oversized frame | A `CopyTo` throw would strand the entry | Length checked against the pool's `BufferSize` before the copy, refused fail-closed |
| 10 | F1's lane contract | Delivered pass frames must be flushed in the same iteration | The delivery is pinned inside `FlushPendingInjections`, before `FlushPendingPasses`; no executor change |
| 11 | Lock order | pending → flow | `decide()` and every flow-gate-held region must not touch the pipeline; parked-gate fact |
| 12 | Snapshot staleness (TCP / UDP) | Obsolete row → wrong process | TCP accepted at 300 ms (TIME_WAIT); UDP positive caching off; bounds asserted |
| 13 | The snapshot changes a decision | It cannot: a hit is the table's answer at scan time and the predicate is identical | Scan/snapshot equality oracle |
| 14 | Ring contention | Attribution items refused at load | The finite relation of §2.5; UDP refusals explicitly accepted, counted (`UdpSetupRejections`), fail-closed |
| 15 | The TTL never runs | A stranded entry holds budget until process end | The sweeper leg (§4.2) + its fact + the tick allocation gate |
| 16 | Warm-path cost | The pending gate is taken only on an eligible flow-table miss | F2's warm gates stay green and are part of the acceptance |
| 17 | Shutdown | A worker or delivery in flight during teardown | Seal → refuse (Block) → settle every entry → drain worker leases → release the pool |
| 18 | Ring capacity vs UDP | UDP setup items can be refused at load | Accepted; already counted and fail-closed |

## 7. Acceptance mapping — exact counts vs recorded series

| Amended PRD criterion | Where discharged | Kind |
|---|---|---|
| **AC-1 off-pump, per admitted pending entry** | A counting attributor recording the calling thread id, driven through `NdisCapturePump.RunIterationForTests` (`NdisCapture.cs:213`) with a scripted reader: `AttributionsOnPumpThread == 0` and `AttributionsOnSetupWorker == admittedEntries` **exactly**; the pump iteration returns while the attributor is parked; `AttributionReAdmission` (failed claim, second key) counted, never hidden; red-before = the same fact on the pre-change tree | exact |
| **AC-2 order and exactly-once** | Order-equals-arrival; `delivered == admitted` with no duplicates; the claim published only after the last pending packet; an append during delivery lands after the earlier ones; a failing worker Blocks every pending packet with nothing stranded; a throwing executor Blocks the batch remainder | exact |
| **AC-3 bounded and fail-closed** | Cap/budget/ring refusals: exactly one counter increment per refused packet, the entry count and the ring never exceed their caps, charged == credited + live after every arm, and the counter name asserted (checklist 15) | exact |
| **AC-4 snapshot** | Fake reader: **one** `Read` per coalesced epoch (N concurrent missers, and a retry that joins a later epoch), **one** forced rescan for a request whose socket bound after that read, a hit served without a read, `window` expiry triggering a read, TCP-only positive caching, scan/snapshot output equality; `ownerTableScansPerBurst` recorded as a **series**; the `attribution` arm's exact thread counts + `pumpBlockedMs` series | exact + series |
| **AC-4 movement / tcpChurn calibration** | The `attribution` scenario before/after series; `tcpChurn` at the **5 %** arm as calibration + must-not-move, the pooled percentile recorded explicitly, the 1 % arm recorded as invalid | series |
| **AC allocation** | The four existing 0 B gates unchanged; the new pump-admission budget gate; `allocatedBytesPerConnection` as the churn ceiling (not attribution's cost); the recorded 91,088/91,066/91,031 B reproduce | exact |
| **F1 regression surface** | `NdisPacketActionExecutorBatchingTests`, `TcpRedirectInjectionBatchingTests` green, plus the loop-exit flush fact (no delivered frame is left lane'd at loop exit) | exact |
| **Semantics preserved** | The shared eligibility/miss method (negative facts: forwarded and no-process-rule misses never create an entry), the scan/snapshot equality oracle, the existing dispatcher facts untouched (`DispatcherFallsBackWhenHostAttributionIsUnknown`, `FlowDispatcherExecutorTests.cs:127`), the §5.4 delta facts, the lifecycle suites | exact |
| **Existing gates** | F2/F3/F4/F5 classes, the four allocation classes, gc-soak, UDP/TCP scenarios, and the new idle-wait gate for the composite signal | exact |
| **Windows-only paths behind the seam** | §9 verbatim into the artifact README; every fact labelled seam-level | artifact |

## 8. Measurement plan

```bash
git rev-parse HEAD; dotnet test WinForward.slnx -c Release            # baseline totals

# 1. the F8 series — BEFORE on the pre-change tree, AFTER on the landed tree, 3 runs each,
#    one run per file then concatenated; every run carries an explicit --output.
D=benchmarks/results/2026-10-01-attribution-off-pump
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario attribution --quick --attribution-cost-ms 7 --output /tmp/wf-f8-after-$r.jsonl
done
cat /tmp/wf-f8-after-{1,2,3}.jsonl > $D/attribution-off-pump-after.jsonl
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario attribution --quick --attribution-cost-ms 0 --output /tmp/wf-f8-control.jsonl

# 2. tcpChurn calibration + must-not-move (the 5 % arm only; the 1 % arm is recorded as invalid).
#    Exact args: research/implementation-notes.md §11.

# 3. must-not-move companions: gc-soak --quick, udp --quick, '*CapturePump*' --job short
```

The new scenario (`benchmarks/WinForward.Benchmarks/Stability/AttributionOffPumpScenario.cs`, ≤400 effective
lines, `--scenario attribution`) drives the **real** dispatcher from one dedicated pump thread over a scripted
sequence with an attributor that sleeps `--attribution-cost-ms` and records the calling thread, and reports
`newFlows`, `admittedEntries`, `attributionsOnPumpThread`, `attributionsOnSetupWorker`, `reAdmissions`,
`pumpBlockedMs` (p50/p95/p99/max), `firstPacketDelayMs`, `deliveredPackets`, `refusedPackets`,
`ownerTableScansPerBurst`, `gen0Collections`, `allocatedBytesPerNewFlow`, `gated: false` and a `note` stating
it measures the pipeline, never `iphlpapi`. It must run **unchanged** on the pre-change tree (where the same
fake is called inline) and must carry the `--attribution-cost-ms 0` control arm.

## 9. Windows open items

| # | Item | Experiment | Expected | Fallback |
|---|---|---|---|---|
| W1 | Real per-flow attribution cost / table size | The `attribution` scenario on Windows with a process rule and the real attributor: `pumpBlockedMs`, `attributionsOnSetupWorker`, `ownerTableScansPerBurst` | Scan 0.5–7 ms off-pump; one scan per epoch | If attribution is cheaper than the pipeline, the coalescer still removes the burst's redundant scans |
| W2 | Snapshot hit rate over a browsing burst | Production `ownerTableScansPerBurst` in the heartbeat | ≪ 1 scan per flow (series, never a threshold) | Window `0` keeps coalescing only |
| W3 | UDP local-port recycling inside 300 ms | Compare UDP snapshot lookups with fresh scans | Rare | UDP stays cache-off until an `OWNER_MODULE` creation-timestamp check exists |
| W4 | Arrival-signal availability (F5's open item) | F5's `SetPacketEvent` experiment | Available on modern NICs | No wake: delivery latency bounded by the poll delay or the idle timeout |
| W5 | `OpenProcess` failure rate | `flow.attribution-miss` (`afterRetry=true`) vs today's baseline | Unchanged | Today's miss path |
| W6 | F8 + R8 double window | A run with a process rule *and* a TCP redirect rule; both pending counters | Independent windows; the SYN is deferred by F8 then retained by R8 | The F8 drain hands the packet to the executor, which is the R8 entry |

## 10. Rollback shape

| Revert | Effect |
|---|---|
| The snapshot (`OwnerTableCacheWindowMs = 0`) | Coalescing only; no positive caching; one config line |
| The composite wake signal | Delivery latency reverts to the poll delay / idle timeout; nothing else changes |
| The sweeper leg | The TTL stops being enforced (entries then live until delivery) — a documented degradation, not a data-path change |
| The whole pipeline | `FlowDispatcher` returns to `AttributeProcessAsync` + `TryClaimResolved` inline; the index, the pipeline, the `Deferred` member, the claim overload, the pool, the sweeper leg and the ring constant are deleted together |
| The reader seam | Additive; deleting it restores `IPHelperTables` as the only provider |

## 11. Spec rows this design changes (each mapped to a proof in `implement.md`)

| Spec | Row | Change |
|---|---|---|
| `hot-path.md` | the exact-window class list (`:1034-1041`) and the per-gate totals string (`:1295-1303`) | add the pump-admission budget gate and `CompositePacketArrivalSignal.Wait` |
| `hot-path.md` | the exact-tuple self-traffic half "once per claim" (`:1398-1401`) | restate as "once per claim; a pending flow's packets re-enter it, bounded by the ring/TTL" (§5.4.5) |
| `traffic-policy-lifecycle.md` | the policy-evaluation location note (`:16`) and the forwarded-eligibility row (`:9`) | evaluation now runs on a setup worker before the claim; forwarded misses are never deferred |
| `async-lifetime.md` | per-owner notes (`:325-371`) | add `FlowAttributionPipeline`; note the sweeper leg |
| `async-lifetime.md` | the `SetupExecutor` note (`:125-130`) | the finite ring relation of §2.5 |
| `windows-ndisapi.md` | the pump ordering relaxation (`:416`) | extend to F8's deferred first-packet delivery |
| new row | `ProcessOwnerTableCache` (epoch coalescing, the request-instant rule, TCP-only caching, the recycled-port reason) | new |
| `error-handling.md` | the shutdown-vs-failure classification list | add the attribution pipeline's worker classification (§2.2) |

## 12. Dead ends (recorded so they are not re-explored)

- Miss-serves-as-unattributed and drop-oldest per-flow eviction (implementation-notes §8.1, §2.4).
- Worker-side claim/delivery (a warm-path pending marker + a thread-safe lane flush): amended PRD req 1
  excludes it.
- Enumerating a shared decided **list** outside the gate (mutation during enumeration aborts the capture run).
- Executing another adapter's frames: unsafe (lane race) *and* unreachable (keys differ per adapter).
- `BoundedSetupQueue` as the retained-packet payload (cannot carry metadata; grows a `Queue<Entry>`).
- Copying through `packet.Lease.Frame` (materializes a pooled managed array per packet).
- Retaining `PacketLease` objects or `NativeFrame` handles; any per-flow `Task.Run`.
- A UDP positive snapshot without a creation timestamp; `OWNER_MODULE`.
- Re-deriving attribution inside `TryClaimResolved`'s `decide()`.
- Relaxing the exact-zero gates: the admission copy is a documented cold-path budget with its own gate.
